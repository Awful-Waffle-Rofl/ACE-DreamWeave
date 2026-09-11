using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Regression cover for the bad-Setup-DID world stop: a weenie whose PropertyDataId.Setup names
    /// something that is not a SetupModel (here 0x010016C8, which exists in client_portal.dat as a
    /// GfxObj) made SetupModel.Unpack throw EndOfStreamException out of WorldObject.CSetup, up through
    /// Landblock.AddWorldObject and the landblock tick, and terminated the world thread.
    /// </summary>
    [TestClass]
    public class BadSetupDidTests
    {
        /// <summary>A GfxObj id (0x01 range). It exists in client_portal.dat, but not as a SetupModel.</summary>
        private const uint BadSetupId = 0x010016C8;

        /// <summary>A real setup file (0x02 range) - the arrow setup, cited in WorldObject.cs.</summary>
        private const uint KnownGoodSetupId = 0x02000124;

        private static readonly object initLock = new object();

        private static bool initialized;

        private static string skipReason;

        /// <summary>
        /// Loads client_portal.dat once for the whole class, or records why it could not be loaded.
        /// PersonalVendorTests.RequireDats is a second, independent DatManager consumer in this
        /// project (the forEachItem material key's RecipeManager.GetMaterialName call) - each class
        /// loads its own copy behind DatManager.PortalDat's own null check, so the two do not collide.
        /// </summary>
        private static void RequireDats()
        {
            lock (initLock)
            {
                if (!initialized)
                {
                    initialized = true;
                    skipReason = InitializeDats();
                }
            }

            if (skipReason != null)
                Assert.Inconclusive(skipReason);
        }

        private static string InitializeDats()
        {
            if (DatManager.PortalDat != null)
                return null;    // already loaded by something else in this test host

            var candidates = new List<string>();

            var fromEnv = System.Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            var portalDat = Path.Combine(datDir, "client_portal.dat");

            try
            {
                using (new FileStream(portalDat, FileMode.Open, FileAccess.Read))
                { }
            }
            catch (IOException ex)
            {
                return $"Skipped: {portalDat} could not be opened for reading ({ex.Message}). Close any running client that holds the .dat files.";
            }

            try
            {
                // the dat readers use Encoding.Default (cp1252), which is not present on .NET without this
                // - ACE.Server does the same in Program.cs, and ACE.DatLoader.Tests in DatTests
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                // loadCell: false - none of these tests need client_cell_1.dat, and it is the slow one
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            if (DatManager.PortalDat == null)
                return $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat.";

            // the physics layer reads PhysicsEngine.Instance.Server unguarded (Physics/Common/GfxObj.cs),
            // so InitPhysicsObj needs the same engine WorldManager's static constructor builds
            if (ACE.Server.Physics.PhysicsEngine.Instance == null)
            {
                var engine = new ACE.Server.Physics.PhysicsEngine(new ACE.Server.Physics.Common.ObjectMaint(), new ACE.Server.Physics.Common.SmartBox());
                engine.Server = true;
            }

            return null;
        }

        /// <summary>
        /// A DB-free WorldObject carrying the given Setup DID.
        /// </summary>
        private static WorldObject MakeWorldObject(uint setupId)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999999,
                ClassName = "badsetuptestobject",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Bad Setup Test Object" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, setupId } },
            };

            return new GenericObject(weenie, new ObjectGuid(0x7F000001));
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void IsValidSetupId_GfxObjId_ReturnsFalse()
        {
            RequireDats();

            Assert.IsTrue(DatManager.PortalDat.AllFiles.ContainsKey(BadSetupId), $"0x{BadSetupId:X8} is expected to exist in client_portal.dat (as a GfxObj)");

            Assert.IsFalse(WorldObject.IsValidSetupId(BadSetupId), $"0x{BadSetupId:X8} is a GfxObj and must not validate as a setup model");
        }

        /// <summary>
        /// Control: the unguarded read this whole change exists to contain still faults, so the guard
        /// above is load-bearing rather than covering a hazard that quietly went away.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void RawReadFromDat_GfxObjIdAsSetupModel_StillThrows()
        {
            RequireDats();

            Exception thrown = null;

            try
            {
                DatManager.PortalDat.ReadFromDat<SetupModel>(BadSetupId);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            // EndOfStreamException out of SetupModel.Unpack on a cold cache; InvalidCastException if the
            // shared FileCache already holds the GfxObj for this id (test order is not guaranteed).
            // Either way the unguarded call is fatal to its caller.
            Assert.IsNotNull(thrown, $"DatDatabase.ReadFromDat<SetupModel>(0x{BadSetupId:X8}) was expected to throw");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void IsValidSetupId_RealSetupId_ReturnsTrue()
        {
            RequireDats();

            Assert.IsTrue(DatManager.PortalDat.AllFiles.ContainsKey(KnownGoodSetupId), $"0x{KnownGoodSetupId:X8} is expected to exist in client_portal.dat");

            Assert.IsTrue(WorldObject.IsValidSetupId(KnownGoodSetupId), $"0x{KnownGoodSetupId:X8} is a real setup file and must validate");

            var setup = WorldObject.GetSetupModel(KnownGoodSetupId);

            Assert.IsNotNull(setup);
            Assert.AreEqual(KnownGoodSetupId, setup.Id, "a valid setup id must return the setup model actually stored under that id");
        }

        /// <summary>
        /// A well-formed 0x02 id that is simply not present in the dat must stay VALID. Before this guard
        /// existed, ReadFromDat handed such an id an empty never-unpacked SetupModel and the object spawned
        /// as a degraded placeholder; rejecting it would be a regression unrelated to the crash being fixed.
        /// One live ace_world wcid is in this state (1010084 'scratch-2h-4', Setup 0x0200267F).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void IsValidSetupId_SetupIdAbsentFromDat_ReturnsTrue()
        {
            RequireDats();

            // 0x02FFFFFF is in the setup range but is not a file in client_portal.dat
            const uint absent = 0x02FFFFFF;

            Assert.IsFalse(DatManager.PortalDat.AllFiles.ContainsKey(absent));

            Assert.IsTrue(WorldObject.IsValidSetupId(absent), "a 0x02 id absent from the dat must keep its pre-existing degraded-placeholder behavior");

            // and it reads back as the same empty model the dat loader produced before this change
            var setup = WorldObject.GetSetupModel(absent);

            Assert.IsNotNull(setup);
            Assert.AreEqual(0, setup.Parts.Count);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void IsValidSetupId_Zero_ReturnsTrue()
        {
            RequireDats();

            // 0 means "no setup"; it is not a defect and must keep its pre-existing behavior
            Assert.IsTrue(WorldObject.IsValidSetupId(0));
        }

        /// <summary>
        /// Bad-setup reports must be bounded: a generator wired to a bad-setup wcid re-enters
        /// AddWorldObjectInternal on every regen tick. Needs no dat files.
        /// </summary>
        [TestMethod]
        public void ShouldReportBadSetupId_DedupesPerSitePerWcid()
        {
            const uint wcid = 999998;

            Assert.IsTrue(WorldObject.ShouldReportBadSetupId("BadSetupDidTests.siteA", wcid));
            Assert.IsFalse(WorldObject.ShouldReportBadSetupId("BadSetupDidTests.siteA", wcid), "the same site must report a given wcid only once");

            Assert.IsTrue(WorldObject.ShouldReportBadSetupId("BadSetupDidTests.siteB", wcid), "each report site keeps its own first-report slot");
            Assert.IsFalse(WorldObject.ShouldReportBadSetupId("BadSetupDidTests.siteB", wcid));
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void CSetup_BadSetupDid_DoesNotThrow()
        {
            RequireDats();

            var wo = MakeWorldObject(BadSetupId);

            SetupModel setup = null;

            // before the fix this threw System.IO.EndOfStreamException out of SetupModel.Unpack
            setup = wo.CSetup;

            Assert.IsNotNull(setup, "CSetup must never return null");
            Assert.IsFalse(setup.HasPhysicsBSP);
            Assert.AreEqual(0, setup.Parts.Count);
            Assert.AreEqual(0u, setup.DefaultAnimation);
            Assert.AreEqual(0u, setup.DefaultScript);

            // and the read sites that go through CSetup stay total too
            Assert.IsFalse(wo.HasMissileFlightPlacement);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void CSetup_ValidSetupDid_ReadsTheRealSetup()
        {
            RequireDats();

            var wo = MakeWorldObject(KnownGoodSetupId);

            Assert.AreEqual(KnownGoodSetupId, wo.CSetup.Id, "a valid Setup DID must still read its real setup model");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void CalculatedPhysicsState_BadSetupDid_DoesNotThrow()
        {
            RequireDats();

            var wo = MakeWorldObject(BadSetupId);

            // CalculatedPhysicsState is private; it is the frame that actually threw in the reported stack
            var method = typeof(WorldObject).GetMethod("CalculatedPhysicsState", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(method, "CalculatedPhysicsState not found - has it been renamed?");

            try
            {
                method.Invoke(wo, null);
            }
            catch (TargetInvocationException ex)
            {
                Assert.Fail($"CalculatedPhysicsState threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void InitPhysicsObj_BadSetupDid_DoesNotThrow()
        {
            RequireDats();

            var wo = MakeWorldObject(BadSetupId);

            wo.InitPhysicsObj();

            // InitPhysicsObj stays total: Landblock.AddWorldObjectInternal dereferences PhysicsObj
            // straight after calling it, so it must never leave it null.
            Assert.IsNotNull(wo.PhysicsObj, "InitPhysicsObj must always produce a PhysicsObj");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void BadSetupDid_DoesNotPoisonTheDatFileCache()
        {
            RequireDats();

            // force the validity check and a CSetup read for the bad id first
            Assert.IsFalse(WorldObject.IsValidSetupId(BadSetupId));
            Assert.IsNotNull(MakeWorldObject(BadSetupId).CSetup);

            // DatDatabase.FileCache is keyed by file id alone, with no notion of type, so a fallback
            // SetupModel cached under this id would come back here and throw InvalidCastException
            var gfxObj = DatManager.PortalDat.ReadFromDat<GfxObj>(BadSetupId);

            Assert.IsNotNull(gfxObj);
            Assert.AreEqual(BadSetupId, gfxObj.Id, "the shared dat FileCache must still hold a GfxObj for this id");
        }
    }
}
