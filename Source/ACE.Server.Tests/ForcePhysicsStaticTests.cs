using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PropertyBool.ForcePhysicsStatic (9091). WorldObject.InitPhysicsObj drops PhysicsState.Static for every object,
    /// because CalculatedPhysicsState reads the Static accessor (which reads PhysicsObj) before the PhysicsObj exists.
    /// The bool is the opt-in that keeps it, on non-creatures only. Both halves are proven on the SAME PhysicsState 25
    /// (Static, ReportCollisions, IgnoreCollisions): without the bool the body is not Static (the unchanged path), with
    /// it the body and the serialized state are.
    /// </summary>
    [TestClass]
    public class ForcePhysicsStaticTests
    {
        private const uint WardPlaneSetup = 0x02000C2E;   // 1006900 Pinnacle Ward Plane, Setup
        private const uint HumanSetup = 0x02000001;
        private const int StaticState = 25;               // Static, ReportCollisions, IgnoreCollisions

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;
        private static uint nextGuid = 0x7F0E2001;

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void WithoutTheBool_PhysicsStateStaticBit_IsDroppedFromTheBody()
        {
            RequireDats();

            var wo = MakeGeneric(forceStatic: null);
            wo.InitPhysicsObj();

            Assert.IsFalse(wo.PhysicsObj.State.HasFlag(PhysicsState.Static), "the unchanged path: the weenie's Static bit never reaches the body");
            Assert.IsFalse(SerializedState(wo).HasFlag(PhysicsState.Static), "nor the serialized state");
            Assert.IsTrue(wo.PhysicsObj.State.HasFlag(PhysicsState.ReportCollisions), "the other bits still arrive");
            Assert.IsTrue(wo.PhysicsObj.State.HasFlag(PhysicsState.IgnoreCollisions), "the other bits still arrive");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void WithTheBoolFalse_TakesTheUnchangedPath()
        {
            RequireDats();

            var wo = MakeGeneric(forceStatic: false);
            wo.InitPhysicsObj();

            Assert.IsFalse(wo.ForcedPhysicsStatic);
            Assert.IsFalse(wo.PhysicsObj.State.HasFlag(PhysicsState.Static), "False is the same as absent");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void WithTheBool_TheBodyAndTheSerializedStateAreStatic()
        {
            RequireDats();

            var wo = MakeGeneric(forceStatic: true);
            wo.InitPhysicsObj();

            Assert.IsTrue(wo.ForcedPhysicsStatic);
            Assert.IsTrue(wo.PhysicsObj.State.HasFlag(PhysicsState.Static), "the body keeps Static");
            Assert.IsTrue(SerializedState(wo).HasFlag(PhysicsState.Static), "and so does the state CreateObject serializes");
            Assert.IsTrue(wo.PhysicsObj.State.HasFlag(PhysicsState.ReportCollisions), "the other bits are untouched");
            Assert.IsFalse(wo.PhysicsObj.set_active(true), "a Static body refuses activation, as a non-dynamic physics object does");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void WithTheBool_OnAPhysicsStateWithoutStatic_StillMakesTheBodyStatic()
        {
            RequireDats();

            var wo = MakeGeneric(forceStatic: true, state: 24);
            wo.InitPhysicsObj();

            Assert.IsTrue(wo.PhysicsObj.State.HasFlag(PhysicsState.Static), "the bool alone is the switch (1006900 ships PhysicsState 24)");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void OnACreature_TheBoolIsIgnored()
        {
            RequireDats();

            var weenie = new Weenie
            {
                WeenieClassId = 999975,
                ClassName = "forcephysicsstatictestcreature",
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Force Physics Static Test Creature" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, HumanSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, StaticState } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.ForcePhysicsStatic, true } },
            };

            TestGameTables.EnsureInitialized();   // Creature's ctor reads the vital formula tables

            var wo = new Creature(weenie, new ObjectGuid(nextGuid++));
            wo.InitPhysicsObj();

            Assert.IsTrue(wo.ForcePhysicsStatic, "the creature carries the bool");
            Assert.IsFalse(wo.ForcedPhysicsStatic, "but a Creature never honours it");
            Assert.IsFalse(wo.PhysicsObj.State.HasFlag(PhysicsState.Static), "its body is not Static");
        }

        private static GenericObject MakeGeneric(bool? forceStatic, int state = StaticState)
        {
            var bools = new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, true } };
            if (forceStatic.HasValue)
                bools[PropertyBool.ForcePhysicsStatic] = forceStatic.Value;

            var weenie = new Weenie
            {
                WeenieClassId = 999974,
                ClassName = "forcephysicsstatictestobject",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Force Physics Static Test Object" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, WardPlaneSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, state } },
                PropertiesBool = bools,
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>The state the CreateObject physics description writes (WorldObject.GetPhysicsStateOrDefault, before its per-viewer tweaks).</summary>
        private static PhysicsState SerializedState(WorldObject wo)
        {
            var method = typeof(WorldObject).GetMethod("GetPhysicsStateOrDefault", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "GetPhysicsStateOrDefault not found - has it been renamed?");

            return (PhysicsState)method.Invoke(wo, null);
        }

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

        /// <summary>Portal dat only (InitPhysicsObj reads the Setup), as BadSetupDidTests.</summary>
        private static string InitializeDats()
        {
            if (ACE.Server.Physics.PhysicsEngine.Instance == null)
            {
                var engine = new ACE.Server.Physics.PhysicsEngine(new ACE.Server.Physics.Common.ObjectMaint(), new ACE.Server.Physics.Common.SmartBox());
                engine.Server = true;
            }

            if (DatManager.PortalDat != null)
                return null;

            var candidates = new List<string>();

            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            return DatManager.PortalDat == null ? $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat." : null;
        }
    }
}
