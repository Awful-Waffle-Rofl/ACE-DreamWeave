using System.Collections.Generic;
using System.Numerics;

using ACE.Server.Managers;
using ACE.Server.MlTreasure;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// MlRelariaSpawner's pure half - the boss-spawn eligibility predicate and the three property values
    /// it stamps (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Boss siting", step 10). Everything
    /// else on that path needs a live Player, a landblock and PropertyManager, none of which
    /// ACE.Server.Tests can supply - PropertyManager reads throw here - which is exactly why the
    /// decision is factored out as CanSpawn over already-read values.
    ///
    /// 0x21B0 (Bluespire) is the in-box landblock, matching MlTreasureLandblockTests and
    /// MlTreasureDropTests; 0x0007 (Town Network) is the out-of-box one.
    /// </summary>
    [TestClass]
    public class MlRelariaSpawnTests
    {
        private const ushort MlLandblock = 0x21B0;
        private const ushort NonMlLandblock = 0x0007;

        private const int BossWcid = 1004120;

        /// <summary>ml_treasure_near_metres at its shipped default.</summary>
        private const double NearMetres = 40.0;

        private static bool CanSpawn(ushort landblock = MlLandblock, ushort realm = 1, bool indoors = false,
            int bossWcid = BossWcid, bool siteIsBossOk = true, bool enabled = true, double nearMetres = NearMetres)
        {
            return MlRelariaSpawner.CanSpawn(landblock, realm, indoors, bossWcid, siteIsBossOk, enabled, nearMetres);
        }

        // ---- the gate ------------------------------------------------------------------------------

        /// <summary>The baseline positive: in the box, realm 1, outdoors, a real boss wcid, a boss_ok
        /// site, the feature on, and the dig radius inside the town margin.</summary>
        [TestMethod]
        public void CanSpawn_AllClausesHold_Spawns()
        {
            Assert.IsTrue(CanSpawn());
        }

        /// <summary>Same landblock, realm 0. Retail Marae Lassel exists in realm 0 at identical physical
        /// coordinates (TREASURE-HUNT-PLAN.md section 4), so the box alone must never be sufficient - a
        /// dig on the live retail island can never summon the boss.</summary>
        [TestMethod]
        public void CanSpawn_MlLandblockRealm0_DoesNotSpawn()
        {
            Assert.IsFalse(CanSpawn(realm: 0));
        }

        /// <summary>A realm-1 landblock outside the box. Realm 1 also carries non-ML landblocks
        /// (TREASURE-HUNT-PLAN.md section 4), so realm alone is not sufficient either.</summary>
        [TestMethod]
        public void CanSpawn_NonMlLandblockRealm1_DoesNotSpawn()
        {
            Assert.IsFalse(CanSpawn(landblock: NonMlLandblock));
        }

        /// <summary>Neither half of the realm/box gate holds.</summary>
        [TestMethod]
        public void CanSpawn_NonMlLandblockRealm0_DoesNotSpawn()
        {
            Assert.IsFalse(CanSpawn(landblock: NonMlLandblock, realm: 0));
        }

        /// <summary>THE town-plaza guarantee. A site the catalogue did not mark boss_ok never spawns the
        /// boss, whatever else holds - boss_ok is cleared for the five town landblocks and their eight
        /// neighbours (TREASURE-HUNT-PLAN.md section 6), and this clause is the only thing standing
        /// between that flag and a level 240 monster in Bluespire.</summary>
        [TestMethod]
        public void CanSpawn_SiteNotBossOk_DoesNotSpawn()
        {
            Assert.IsFalse(CanSpawn(siteIsBossOk: false));
        }

        /// <summary>The master switch vetoes the spawn on its own.</summary>
        [TestMethod]
        public void CanSpawn_FeatureDisabled_DoesNotSpawn()
        {
            Assert.IsFalse(CanSpawn(enabled: false));
        }

        /// <summary>An ordinary map (PropertyInt 9066 absent, read as 0) is not the Relaria variant.
        /// 9066 is defined as "&gt; 0 makes this the Relaria variant and names the boss"
        /// (TREASURE-HUNT-PLAN.md section 3).</summary>
        [TestMethod]
        public void CanSpawn_ZeroBossWcid_DoesNotSpawn()
        {
            Assert.IsFalse(CanSpawn(bossWcid: 0));
        }

        /// <summary>A negative 9066 is a content bug, not a boss.</summary>
        [TestMethod]
        public void CanSpawn_NegativeBossWcid_DoesNotSpawn()
        {
            Assert.IsFalse(CanSpawn(bossWcid: -1));
        }

        /// <summary>Indoors is refused: the spawn snaps to terrain (Position.AdjustMapCoords) and a dig
        /// site is an outdoor terrain cell, so an indoor cell has no ground to snap to.</summary>
        [TestMethod]
        public void CanSpawn_Indoors_DoesNotSpawn()
        {
            Assert.IsFalse(CanSpawn(indoors: true));
        }

        // ---- the town margin ------------------------------------------------------------------------

        /// <summary>
        /// The boss lands where the PLAYER stands, not at the stored site coordinates, so the town
        /// exclusion only holds while a digging player is necessarily still within the boss_ok cell's
        /// landblock margin. A boss_ok cell is at least one whole landblock (192 m) from any excluded
        /// block, so ml_treasure_near_metres must stay under that - raising it past a landblock length
        /// would silently let the boss land in a town block, and this clause fails closed instead.
        /// </summary>
        [TestMethod]
        public void CanSpawn_NearMetresBeyondTheTownMargin_DoesNotSpawn()
        {
            Assert.AreEqual(192.0, MlRelariaSpawner.TownExclusionMarginMetres, 1e-9);

            Assert.IsTrue(CanSpawn(nearMetres: 191.9));
            Assert.IsFalse(CanSpawn(nearMetres: 192.0));
            Assert.IsFalse(CanSpawn(nearMetres: 500.0));
        }

        /// <summary>A negative dig radius is nonsense; refuse rather than reason about it.</summary>
        [TestMethod]
        public void CanSpawn_NegativeNearMetres_DoesNotSpawn()
        {
            Assert.IsFalse(CanSpawn(nearMetres: -1.0));
        }

        /// <summary>NaN fails every comparison, so the clause is written as !(x &gt;= 0) to catch it.</summary>
        [TestMethod]
        public void CanSpawn_NaNNearMetres_DoesNotSpawn()
        {
            Assert.IsFalse(CanSpawn(nearMetres: double.NaN));
        }

        // ---- the stamped values ---------------------------------------------------------------------

        /// <summary>HomeRadius is twice the tether, matching WorldEventSpawner.BossHomeRadiusFor, so the
        /// ordinary 192 m leash still sits outside the tether and only catches a boss the tether's forced
        /// retarget could not pull back.</summary>
        [TestMethod]
        public void HomeRadiusFor_IsTwiceTheTether()
        {
            Assert.AreEqual(80.0, MlRelariaSpawner.HomeRadiusFor(40.0), 1e-9);
            Assert.AreEqual(0.0, MlRelariaSpawner.HomeRadiusFor(0.0), 1e-9);
        }

        /// <summary>A non-positive or non-finite tether radius stamps nothing, which is how PropertyFloat
        /// 9009 is defined (absent or &lt;= 0 leaves targeting unchanged).</summary>
        [TestMethod]
        public void AppliesTether_RejectsNonPositiveAndNonFinite()
        {
            Assert.IsTrue(MlRelariaSpawner.AppliesTether(40.0));
            Assert.IsFalse(MlRelariaSpawner.AppliesTether(0.0));
            Assert.IsFalse(MlRelariaSpawner.AppliesTether(-1.0));
            Assert.IsFalse(MlRelariaSpawner.AppliesTether(double.NaN));
            Assert.IsFalse(MlRelariaSpawner.AppliesTether(double.PositiveInfinity));
        }

        /// <summary>
        /// 0 and -1 are both meaningful to WorldObject_Decay - instant rot and never rot - and neither is
        /// a TTL, so a misconfigured tunable must fall back rather than produce an immortal boss (-1) or
        /// one that evaporates the instant it lands (0).
        /// </summary>
        [TestMethod]
        public void TimeToRotFor_NonPositiveTtlFallsBack()
        {
            Assert.AreEqual(1800.0, MlRelariaSpawner.TimeToRotFor(1800, MlRelariaSpawner.DefaultTtlSeconds), 1e-9);
            Assert.AreEqual(MlRelariaSpawner.DefaultTtlSeconds, MlRelariaSpawner.TimeToRotFor(0, MlRelariaSpawner.DefaultTtlSeconds), 1e-9);
            Assert.AreEqual(MlRelariaSpawner.DefaultTtlSeconds, MlRelariaSpawner.TimeToRotFor(-1, MlRelariaSpawner.DefaultTtlSeconds), 1e-9);
        }

        /// <summary>The persistence exclusion and the trophy key are the same reserved property, 9066,
        /// read off the creature rather than off the map. Pinned so a rename cannot silently split the
        /// two halves (WorldObject.IsDynamicThatShouldPersistToShard and MlRelariaTrophy).</summary>
        [TestMethod]
        public void BossMarker_IsTheReservedTreasureMapBossWcid()
        {
            Assert.AreEqual(ACE.Entity.Enum.Properties.PropertyInt.TreasureMapBossWcid, MlRelariaSpawner.BossMarker);
            Assert.AreEqual(9066, (int)MlRelariaSpawner.BossMarker);
        }

        // ---- PrepareBoss (shared with /relariaspawn, MlRelariaCommands.cs) -----------------------------

        /// <summary>
        /// PrepareBoss also runs the RoZ round 19 level-spread scaling (MlMapEventTunables.Read plus the digsite
        /// audience radius), so every key that reads is seeded to its registered default - and re-seeded in the
        /// finally, the restore this class already uses - rather than relying on an earlier class in the run
        /// having cached it. PropertyManager reads of an unseeded key throw in this process.
        /// </summary>
        private static void SeedMapEventDefaults()
        {
            foreach (var kv in DefaultPropertyManager.DefaultBooleanProperties)
            {
                if (kv.Key.StartsWith("ml_mapevent_"))
                    PropertyManager.ModifyBool(kv.Key, kv.Value.Item);
            }

            foreach (var kv in DefaultPropertyManager.DefaultLongProperties)
            {
                if (kv.Key.StartsWith("ml_mapevent_"))
                    PropertyManager.ModifyLong(kv.Key, kv.Value.Item);
            }

            foreach (var kv in DefaultPropertyManager.DefaultDoubleProperties)
            {
                if (kv.Key.StartsWith("ml_mapevent_") || kv.Key == "ml_digsite_audience_radius_metres")
                    PropertyManager.ModifyDouble(kv.Key, kv.Value.Item);
            }
        }

        /// <summary>
        /// PrepareBoss is the exact code both TrySpawn (the real dig path) and /relariaspawn (the admin
        /// test-spawn command) call to stamp the boss before EnterWorld, so this pins the one thing every
        /// caller depends on: the marker MlRelariaTrophy.TryDropTrophy reads (BossMarker_ ==
        /// PropertyInt.TreasureMapBossWcid - pinned above) actually lands on the creature, at the wcid
        /// value passed in - not the creature's own wcid, matching TrySpawn's original call, which stamps
        /// the MAP's PropertyInt 9066 value.
        ///
        /// Both tunables PrepareBoss reads (ml_treasure_boss_ttl_seconds, ml_treasure_boss_tether_radius)
        /// are seeded to their shipped defaults first and restored in finally - PropertyAdminServiceTests'
        /// precedent - because an uncached PropertyManager read falls through to
        /// DatabaseManager.ShardConfig, which NREs with no live shard database in this test process.
        /// </summary>
        [TestMethod]
        public void PrepareBoss_StampsTheMarkerAtTheSuppliedWcid()
        {
            PropertyManager.ModifyLong("ml_treasure_boss_ttl_seconds", (long)MlRelariaSpawner.DefaultTtlSeconds);
            PropertyManager.ModifyDouble("ml_treasure_boss_tether_radius", 40.0);
            SeedMapEventDefaults();

            try
            {
                var boss = TestCreatures.CreateEffectCarrier(wcid: BossWcid);

                Assert.IsNull(boss.GetProperty(MlRelariaSpawner.BossMarker));

                MlRelariaSpawner.PrepareBoss(boss, BossWcid);

                Assert.AreEqual(BossWcid, boss.GetProperty(MlRelariaSpawner.BossMarker));
            }
            finally
            {
                PropertyManager.ModifyLong("ml_treasure_boss_ttl_seconds", (long)MlRelariaSpawner.DefaultTtlSeconds);
                PropertyManager.ModifyDouble("ml_treasure_boss_tether_radius", 40.0);
                SeedMapEventDefaults();
            }
        }

        /// <summary>PrepareBoss also stamps TimeToRot and the tether/HomeRadius pair, matching TrySpawn's
        /// own writes exactly (same source, same order) - this is what makes the extraction safe: there is
        /// no behaviour TrySpawn had that PrepareBoss dropped.</summary>
        [TestMethod]
        public void PrepareBoss_StampsTtlAndTether()
        {
            PropertyManager.ModifyLong("ml_treasure_boss_ttl_seconds", 900);
            PropertyManager.ModifyDouble("ml_treasure_boss_tether_radius", 25.0);
            SeedMapEventDefaults();

            try
            {
                var boss = TestCreatures.CreateEffectCarrier(wcid: BossWcid);

                MlRelariaSpawner.PrepareBoss(boss, BossWcid);

                Assert.AreEqual(900.0, boss.TimeToRot ?? 0.0, 1e-9);
                Assert.AreEqual(25.0, boss.TetherRadius ?? 0.0, 1e-9);
                Assert.AreEqual(50.0, boss.HomeRadius ?? 0.0, 1e-9);
            }
            finally
            {
                PropertyManager.ModifyLong("ml_treasure_boss_ttl_seconds", (long)MlRelariaSpawner.DefaultTtlSeconds);
                PropertyManager.ModifyDouble("ml_treasure_boss_tether_radius", 40.0);
                SeedMapEventDefaults();
            }
        }

        /// <summary>A freshly-created wcid-1004120 creature that never went through PrepareBoss (the
        /// /create path, before this change) carries no marker at all - GetProperty returns null, which
        /// MlRelariaTrophy.TryDropTrophy reads as 0 via `?? 0` (MlRelariaTrophy.cs:148) and refuses to drop
        /// a trophy. This is the exact bug report this change fixes: staff using /create 1004120 never saw
        /// a trophy.</summary>
        [TestMethod]
        public void PlainCreature_WithoutPrepareBoss_CarriesNoMarker()
        {
            var boss = TestCreatures.CreateEffectCarrier(wcid: BossWcid);

            Assert.IsNull(boss.GetProperty(MlRelariaSpawner.BossMarker));
            Assert.AreEqual(0, boss.GetProperty(MlRelariaSpawner.BossMarker) ?? 0);
        }

        // ---- boss_ok site confirmation ---------------------------------------------------------------

        private const string Header = "landblock\tcell_x\tcell_y\tlocal_x\tlocal_y\tz\tmap_ns\tmap_ew\tboss_ok";

        /// <summary>A stored dig site that matches a boss_ok catalogue row is confirmed; one that matches
        /// only a boss_ok=0 row is not. This is a CONFIRMATION, never a nearest lookup - snapping a
        /// non-boss_ok site to the nearest boss_ok one would defeat the town exclusion.</summary>
        [TestMethod]
        public void IsBossSite_ConfirmsOnlyBossOkRows()
        {
            var store = MlTreasureSiteStore.ParseLines(new List<string>
            {
                Header,
                "0x21B0\t1\t2\t50\t60\t10\t-10.5\t20.25\t1",  // boss_ok
                "0x0FB9\t3\t4\t70\t80\t12\t-9.5\t21.25\t0"    // town block, not boss_ok
            });

            Assert.IsTrue(store.IsBossSite(new Vector2(20.25f, -10.5f), MlRelariaSpawner.SiteMatchToleranceMapUnits));
            Assert.IsFalse(store.IsBossSite(new Vector2(21.25f, -9.5f), MlRelariaSpawner.SiteMatchToleranceMapUnits));
        }

        /// <summary>A site nowhere in the catalogue - a hand-stamped 9010/9011 pair, say - is refused
        /// rather than matched to whatever happens to be closest.</summary>
        [TestMethod]
        public void IsBossSite_UnknownCoordinates_AreRefused()
        {
            var store = MlTreasureSiteStore.ParseLines(new List<string>
            {
                Header,
                "0x21B0\t1\t2\t50\t60\t10\t-10.5\t20.25\t1"
            });

            Assert.IsFalse(store.IsBossSite(new Vector2(0f, 0f), MlRelariaSpawner.SiteMatchToleranceMapUnits));
        }

        /// <summary>An empty or degraded store (a missing or unreadable dig-sites.tsv) fails CLOSED: no
        /// rows means no boss_ok row, so the spawn is refused and the map is left in the player's pack.</summary>
        [TestMethod]
        public void IsBossSite_EmptyStore_FailsClosed()
        {
            Assert.IsFalse(MlTreasureSiteStore.Empty.IsBossSite(new Vector2(20.25f, -10.5f),
                MlRelariaSpawner.SiteMatchToleranceMapUnits));
        }

        // ---- /testtreasuremap relaria: the catalogue-check bypass -----------------------------------------

        /// <summary>
        /// EffectiveSiteIsBossOk is what TrySpawn feeds CanSpawn's siteIsBossOk parameter. For an ordinary
        /// (non-test) Relaria dig, skipBossSiteCheck is always false, so the real catalogue match is what
        /// decides - unchanged from before this command existed.
        /// </summary>
        [TestMethod]
        public void EffectiveSiteIsBossOk_NotSkipped_ReturnsTheCatalogueMatch()
        {
            Assert.IsTrue(MlRelariaSpawner.EffectiveSiteIsBossOk(catalogueMatch: true, skipBossSiteCheck: false));
            Assert.IsFalse(MlRelariaSpawner.EffectiveSiteIsBossOk(catalogueMatch: false, skipBossSiteCheck: false));
        }

        /// <summary>
        /// A /testtreasuremap relaria map's site was stamped at the admin's own position, never rolled from
        /// the catalogue, so skipBossSiteCheck always answers true regardless of what the catalogue says -
        /// including when the catalogue match is false, which is the case every test map actually hits.
        /// </summary>
        [TestMethod]
        public void EffectiveSiteIsBossOk_Skipped_IsAlwaysTrue()
        {
            Assert.IsTrue(MlRelariaSpawner.EffectiveSiteIsBossOk(catalogueMatch: false, skipBossSiteCheck: true));
            Assert.IsTrue(MlRelariaSpawner.EffectiveSiteIsBossOk(catalogueMatch: true, skipBossSiteCheck: true));
        }
    }
}
