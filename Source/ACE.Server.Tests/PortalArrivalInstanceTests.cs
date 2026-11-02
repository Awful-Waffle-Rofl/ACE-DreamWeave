using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Server.Physics;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The phantom realm-0 Aerfalle Keep load (prod, 2026-10-08/09): realm 0's landblock 0x01F5 was activated
    /// ~250 times a day with nobody in it, each time ~one world tick before a player teleported into realm 1's
    /// Marketplace - the same landblock id.
    ///
    /// Cause: the portal-family teleports (walking through a portal, a portal gem, portal recall) ran
    /// WorldObject.AdjustDungeon on the RAW weenie Destination, which is always instance 0, and only then routed
    /// it into the realm the player actually arrives in. AdjustDungeon reaches LScape.get_landblock with the
    /// position's own instance, and on the server that is LandblockManager.GetLandblock - a full activation.
    /// Portal.ResolveArrivalDestination fixes the order: resolve the instance, then adjust.
    ///
    /// A live Player cannot be constructed in this project (its constructor needs the auth database - see
    /// BetaGiftTests), so Portal.ActOnUse / Gem.UsePortalGem / the portal recall cannot be driven end to end.
    /// These tests drive the extracted ResolveArrivalDestination, with the realm resolution injected, through
    /// the REAL AdjustDungeon -> LScape.get_landblock path. PhysicsEngine.Server is flipped to false (the same
    /// bootstrap ExactPlacementTests uses) so LScape builds the landblock from client_cell.dat and records it in
    /// LScape.Landblocks keyed by (instance, landblock) - the observable stand-in for "this instance's copy was
    /// activated", since on the server the same call is LandblockManager.GetLandblock(id, instance).
    /// </summary>
    [TestClass]
    public class PortalArrivalInstanceTests
    {
        private const uint Aerfalle = 0x01F5FFFF;

        /// <summary>Realm 1's default instance - where the Marketplace (realm 1's copy of 0x01F5) lives.</summary>
        private static readonly uint Realm1Default = Position.InstanceIDFromVars(1, 0, false);

        /// <summary>The Marketplace landing (MarketplaceRecall.FallbackDrop), as a weenie Destination: instance 0.</summary>
        private static Position RawMarketplaceDestination() => new Position(0x01F50224, 50f, -132.618988f, 0.005f, 0f, 0f, 0f, 1f, 0);

        private static ulong Key(uint instance) => ((ulong)instance << 32) | Aerfalle;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        /// <summary>
        /// The fix: a realm-routed arrival activates only the copy of the landblock it arrives in. Fails on the
        /// pre-fix order (AdjustDungeon on the raw destination, then resolve), which activates realm 0's copy.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void ResolveArrivalDestination_RealmRoutedArrival_NeverActivatesTheRealm0Twin()
        {
            RequireDats();

            WithClientPhysics(() =>
            {
                var raw = RawMarketplaceDestination();

                var arrival = Portal.ResolveArrivalDestination(raw, p => new Position(p, Realm1Default));

                Assert.AreEqual(Realm1Default, arrival.Instance, "the arrival is in the resolved instance");
                Assert.AreEqual((ushort)0x01F5, arrival.LandblockShort);
                Assert.AreEqual(0u, raw.Instance, "the raw destination is not mutated");

                Assert.IsFalse(LScape.Landblocks.ContainsKey(Key(0)),
                    "resolving a realm-1 arrival activated realm 0's copy of 0x01F5 (Aerfalle Keep) - AdjustDungeon ran on the raw instance-0 destination");

                // positive control: AdjustDungeon really reached the landblock lookup in this harness, so the
                // absence asserted above is not vacuous
                Assert.IsTrue(LScape.Landblocks.ContainsKey(Key(Realm1Default)),
                    "AdjustDungeon should have looked up 0x01F5 in the arrival instance (realm 1 default)");
            });
        }

        /// <summary>
        /// The cell correction AdjustDungeon exists for still happens, in the resolved instance: a destination
        /// whose cell id disagrees with its point is moved to the cell that contains the point. The point is the
        /// Salvage Forge's authored spot, which ExactPlacementTests places in 0x01F501B3.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void ResolveArrivalDestination_StillCorrectsTheDungeonCell()
        {
            RequireDats();

            WithClientPhysics(() =>
            {
                var raw = new Position(0x01F50224, 16.75f, -72.45f, -17.995f, 0f, 0f, 0f, 1f, 0);

                var arrival = Portal.ResolveArrivalDestination(raw, p => new Position(p, Realm1Default));

                Assert.AreEqual(0x01F501B3u, arrival.Cell, $"AdjustDungeonCells should move the point into the cell that contains it; got 0x{arrival.Cell:X8}");
                Assert.AreEqual(Realm1Default, arrival.Instance);
            });
        }

        /// <summary>
        /// Control: the failure mode is reachable in this harness. AdjustDungeon on a raw (instance 0) weenie
        /// destination - what the three call sites did before the fix - activates realm 0's copy.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Control_AdjustDungeonOnTheRawDestination_ActivatesTheRealm0Copy()
        {
            RequireDats();

            WithClientPhysics(() =>
            {
                WorldObject.AdjustDungeon(RawMarketplaceDestination());

                Assert.IsTrue(LScape.Landblocks.ContainsKey(Key(0)),
                    "control: AdjustDungeon on an instance-0 position should look up (activate) realm 0's 0x01F5; if this fails the harness no longer reaches the activation and the test above proves nothing");
            });
        }

        // ================= pure: MayAdjustInInstance (ActOnUse's deferred-adjust guard) =================

        private const ushort Lb = 0x01F5;
        private const ushort OtherLb = 0x0007;
        private static readonly uint Ephemeral = Position.InstanceIDFromVars(1, 0x0042, true);

        private static Position At(ushort landblock, uint instance) => new Position(((uint)landblock << 16) | 0x0100, 10f, -10f, 0.005f, 0f, 0f, 0f, 1f, instance);

        [TestMethod]
        public void MayAdjustInInstance_PersistentInstance_AlwaysAllowed()
        {
            // the lookup must not even matter for a persistent instance - it says "nothing live" here
            Assert.IsTrue(Portal.MayAdjustInInstance(At(Lb, 0), _ => null), "realm 0");
            Assert.IsTrue(Portal.MayAdjustInInstance(At(Lb, Realm1Default), _ => null), "realm 1 default");
        }

        /// <summary>
        /// The case the guard exists for: a HousePortal used from inside an ephemeral instance, whose Destination is
        /// a DIFFERENT landblock, carries the ephemeral id with the foreign landblock. Adjusting it would create an
        /// unregistered plain landblock under the ephemeral id. An unguarded deferred adjust (the pre-fix-round
        /// code) is the same as this predicate returning true.
        /// </summary>
        [TestMethod]
        public void MayAdjustInInstance_EphemeralInstanceHoldingADifferentLandblock_IsRefused()
        {
            Assert.IsTrue(At(OtherLb, Ephemeral).IsEphemeralRealm, "precondition: the test instance id is ephemeral");
            Assert.IsFalse(Portal.MayAdjustInInstance(At(OtherLb, Ephemeral), i => i == Ephemeral ? Lb : (ushort?)null));
        }

        [TestMethod]
        public void MayAdjustInInstance_EphemeralInstanceNotLive_IsRefused()
        {
            Assert.IsFalse(Portal.MayAdjustInInstance(At(Lb, Ephemeral), _ => null));
        }

        [TestMethod]
        public void MayAdjustInInstance_EphemeralInstanceHoldingThatLandblock_IsAllowed()
        {
            Assert.IsTrue(Portal.MayAdjustInInstance(At(Lb, Ephemeral), i => i == Ephemeral ? Lb : (ushort?)null));
        }

        // ================= harness =================

        private static void WithClientPhysics(Action body)
        {
            var engine = PhysicsEngine.Instance;
            var wasServer = engine.Server;
            engine.Server = false;
            try
            {
                UnloadBoth();
                Assert.IsFalse(LScape.Landblocks.ContainsKey(Key(0)), "precondition: realm 0's 0x01F5 not loaded");
                Assert.IsFalse(LScape.Landblocks.ContainsKey(Key(Realm1Default)), "precondition: realm 1's 0x01F5 not loaded");

                body();
            }
            finally
            {
                engine.Server = wasServer;
            }
        }

        private static void UnloadBoth()
        {
            LScape.unload_landblock(Aerfalle, 0);
            LScape.unload_landblock(Aerfalle, Realm1Default);
        }

        // Same cell-dat handling as ExactPlacementTests: other classes in this project depend on
        // DatManager.CellDat being null, so the cell dat is opened once into a private instance and swapped in
        // only for the duration of each test, then swapped back out in TestCleanup with the landblocks it built.

        private static CellDatDatabase ownCellDat;
        private static bool cellDatSwapped;
        private static CellDatDatabase previousCellDat;

        private static readonly System.Reflection.PropertyInfo cellDatProperty = typeof(DatManager).GetProperty(nameof(DatManager.CellDat));

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

            previousCellDat = DatManager.CellDat;
            cellDatProperty.SetValue(null, ownCellDat);
            cellDatSwapped = true;
        }

        [TestCleanup]
        public void RestoreGlobalState()
        {
            if (!cellDatSwapped)
                return;

            var engine = PhysicsEngine.Instance;
            var wasServer = engine.Server;
            engine.Server = false;
            try
            {
                UnloadBoth();
            }
            finally
            {
                engine.Server = wasServer;
                cellDatProperty.SetValue(null, previousCellDat);
                previousCellDat = null;
                cellDatSwapped = false;
            }
        }

        private static string InitializeDats()
        {
            if (PhysicsEngine.Instance == null)
            {
                var engine = new PhysicsEngine(new ObjectMaint(), new SmartBox());
                engine.Server = true;
            }

            var candidates = new List<string>();

            var fromEnv = System.Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")) && File.Exists(Path.Combine(c, "client_cell_1.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat + client_cell_1.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                if (DatManager.PortalDat == null)
                    DatManager.Initialize(datDir, true, false);

                ownCellDat = new CellDatDatabase(Path.Combine(datDir, "client_cell_1.dat"), true);
            }
            catch (Exception ex)
            {
                return $"Skipped: loading the dats from {datDir} failed: {ex.Message}";
            }

            if (DatManager.PortalDat == null || ownCellDat == null)
                return $"Skipped: {datDir} did not produce both a PortalDat and a CellDat.";

            return null;
        }
    }
}
