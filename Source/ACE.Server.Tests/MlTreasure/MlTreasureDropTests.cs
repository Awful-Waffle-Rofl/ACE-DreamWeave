using ACE.Server.MlTreasure;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// MlTreasureDrop.ShouldDrop and MlTreasureDrop.IsRelariaVariant - the whole drop decision as pure
    /// functions over already-read values (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Drop",
    /// step 7). Everything else on that path needs a Corpse, a WorldObjectFactory and PropertyManager,
    /// none of which ACE.Server.Tests can supply - PropertyManager reads throw under unit test - which
    /// is exactly why the decision is factored out this way.
    ///
    /// 0x21B0 (Bluespire) is the in-box landblock used throughout, matching MlTreasureLandblockTests;
    /// 0x0007 (Town Network) is the out-of-box one.
    /// </summary>
    [TestClass]
    public class MlTreasureDropTests
    {
        private const ushort MlLandblock = 0x21B0;
        private const ushort NonMlLandblock = 0x0007;

        // ---- the gate ------------------------------------------------------------------------------

        /// <summary>The baseline positive: in the box, in realm 1, killed by a player, enabled, and the
        /// roll lands under the chance.</summary>
        [TestMethod]
        public void ShouldDrop_MlLandblockRealm1_Drops()
        {
            Assert.IsTrue(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 0.008, roll: 0.001));
        }

        /// <summary>Same landblock, realm 0. Retail Marae Lassel exists in realm 0
        /// (TREASURE-HUNT-PLAN.md section 4), so the box alone must never be sufficient - a kill on the
        /// live retail island drops nothing.</summary>
        [TestMethod]
        public void ShouldDrop_MlLandblockRealm0_DoesNotDrop()
        {
            Assert.IsFalse(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 0, killedByPlayer: true,
                enabled: true, dropChance: 0.008, roll: 0.001));
        }

        /// <summary>A realm-1 landblock outside the box. Realm 1 carries non-ML landblocks
        /// (TREASURE-HUNT-PLAN.md section 4), so realm alone must never be sufficient either.</summary>
        [TestMethod]
        public void ShouldDrop_NonMlLandblockRealm1_DoesNotDrop()
        {
            Assert.IsFalse(MlTreasureDrop.ShouldDrop(NonMlLandblock, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 0.008, roll: 0.001));
        }

        /// <summary>Neither half of the gate holds.</summary>
        [TestMethod]
        public void ShouldDrop_NonMlLandblockRealm0_DoesNotDrop()
        {
            Assert.IsFalse(MlTreasureDrop.ShouldDrop(NonMlLandblock, realm: 0, killedByPlayer: true,
                enabled: true, dropChance: 0.008, roll: 0.001));
        }

        /// <summary>Both edges of the box on the same landblock value, so a one-off in either bound
        /// shows up here rather than only in MlTreasureLandblockTests.</summary>
        [TestMethod]
        public void ShouldDrop_BoxCorners_Drop()
        {
            // x 0x0C-0x2E, y 0xAC-0xCA (TREASURE-HUNT-PLAN.md section 4)
            Assert.IsTrue(MlTreasureDrop.ShouldDrop(0x0CAC, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 1.0, roll: 0.0), "min corner 0x0CAC should be in the box");

            Assert.IsTrue(MlTreasureDrop.ShouldDrop(0x2ECA, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 1.0, roll: 0.0), "max corner 0x2ECA should be in the box");

            Assert.IsFalse(MlTreasureDrop.ShouldDrop(0x0BAC, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 1.0, roll: 0.0), "0x0BAC is one landblock outside the x bound");

            Assert.IsFalse(MlTreasureDrop.ShouldDrop(0x2ECB, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 1.0, roll: 0.0), "0x2ECB is one landblock outside the y bound");
        }

        // ---- the other gates -----------------------------------------------------------------------

        /// <summary>Not killed by a player (environment kill, creature-on-creature, an Olthoi player).
        /// The drop is loot and follows CreateCorpse's own killed-by-a-player test.</summary>
        [TestMethod]
        public void ShouldDrop_NotKilledByPlayer_DoesNotDrop()
        {
            Assert.IsFalse(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: false,
                enabled: true, dropChance: 1.0, roll: 0.0));
        }

        /// <summary>ml_treasure_enabled is the master switch; false means no drop ever, whatever the
        /// chance is set to.</summary>
        [TestMethod]
        public void ShouldDrop_Disabled_DoesNotDrop()
        {
            Assert.IsFalse(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: true,
                enabled: false, dropChance: 1.0, roll: 0.0));
        }

        /// <summary>A chance of 0 must never drop even on the lowest possible draw. ThreadSafeRandom.
        /// Next(0.0f, 1.0f) can return exactly 0.0, so `roll &lt; dropChance` alone would not settle
        /// this - the explicit dropChance &lt;= 0 clause does.</summary>
        [TestMethod]
        public void ShouldDrop_ZeroChance_DoesNotDropEvenOnZeroRoll()
        {
            Assert.IsFalse(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 0.0, roll: 0.0));
        }

        /// <summary>A negative chance (a mistyped tunable) behaves as off, never as always-on.</summary>
        [TestMethod]
        public void ShouldDrop_NegativeChance_DoesNotDrop()
        {
            Assert.IsFalse(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: -1.0, roll: 0.0));
        }

        /// <summary>The drop-roll boundary is half-open: a roll strictly below the chance drops, a roll
        /// exactly at it does not. Pinned at the shipped 0.008 default (TREASURE-HUNT-PLAN.md section
        /// 2.10, 0.8 percent).</summary>
        [TestMethod]
        public void ShouldDrop_RollBoundary_IsHalfOpen()
        {
            Assert.IsTrue(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 0.008, roll: 0.0079), "just under the chance drops");

            Assert.IsFalse(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 0.008, roll: 0.008), "exactly at the chance does not drop");

            Assert.IsFalse(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 0.008, roll: 0.0081), "just over the chance does not drop");
        }

        /// <summary>A chance of 1.0 drops on every draw the RNG can actually produce. This is the case
        /// the LIVE step-7 verification (chance forced to 1.0) depends on, so it is pinned rather than
        /// assumed. ThreadSafeRandom.Next(0.0f, 1.0f) is NextDouble() * (max - min) + min
        /// (Source/ACE.Common/ThreadSafeRandom.cs:20) and NextDouble is [0, 1), so a roll of exactly 1.0
        /// is unreachable and the half-open comparison never costs a forced drop.</summary>
        [TestMethod]
        public void ShouldDrop_ChanceOne_DropsAcrossTheWholeRollRange()
        {
            Assert.IsTrue(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 1.0, roll: 0.0));

            Assert.IsTrue(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 1.0, roll: 0.999999));

            // A roll of exactly 1.0 is NOT < 1.0, recorded here as the boundary. It is not reachable
            // from ThreadSafeRandom.Next(0.0f, 1.0f), so this costs a forced-to-1.0 drop nothing.
            Assert.IsFalse(MlTreasureDrop.ShouldDrop(MlLandblock, realm: 1, killedByPlayer: true,
                enabled: true, dropChance: 1.0, roll: 1.0));
        }

        // ---- the Relaria variant -------------------------------------------------------------------

        /// <summary>The shipped boss wcid, pinned. It was held at 0 until BOTH the boss weenie (plan
        /// step 9, Content/sql/weenies/1004120 Aun Relaria the Unburied.sql) and its spawn (step 10,
        /// MlRelariaSpawner) existed, because PropertyInt 9066 is defined as "&gt; 0 makes this the
        /// Relaria variant and names the boss" (TREASURE-HUNT-PLAN.md section 3) - a map could not be
        /// marked as the variant without a real boss to name. Changing it again means changing the
        /// content the drop points at, so it fails loudly here.</summary>
        [TestMethod]
        public void IsRelariaVariant_ShippedBossWcidIsTheAuthoredBoss()
        {
            Assert.AreEqual(1004120u, MlTreasureDrop.RelariaBossWcid,
                "RelariaBossWcid must name Content/sql/weenies/1004120 Aun Relaria the Unburied.sql");

            Assert.IsTrue(MlTreasureDrop.IsRelariaVariant(MlTreasureDrop.RelariaBossWcid,
                relariaChance: 1.0, roll: 0.0));
        }

        /// <summary>A zero boss wcid vetoes the variant whatever the chance and the roll are.</summary>
        [TestMethod]
        public void IsRelariaVariant_ZeroBossWcid_NeverVariant()
        {
            Assert.IsFalse(MlTreasureDrop.IsRelariaVariant(bossWcid: 0, relariaChance: 1.0, roll: 0.0));
        }

        /// <summary>The variant roll boundary, same half-open convention as the drop roll, pinned at
        /// the shipped ml_treasure_relaria_chance default of 0.05.</summary>
        [TestMethod]
        public void IsRelariaVariant_RollBoundary_IsHalfOpen()
        {
            const uint bossWcid = 1004150;

            Assert.IsTrue(MlTreasureDrop.IsRelariaVariant(bossWcid, relariaChance: 0.05, roll: 0.049),
                "just under the chance is the variant");

            Assert.IsFalse(MlTreasureDrop.IsRelariaVariant(bossWcid, relariaChance: 0.05, roll: 0.05),
                "exactly at the chance is not the variant");

            Assert.IsFalse(MlTreasureDrop.IsRelariaVariant(bossWcid, relariaChance: 0.05, roll: 0.051),
                "just over the chance is not the variant");
        }

        /// <summary>A relaria chance of 0 turns the variant off without needing the wcid cleared.</summary>
        [TestMethod]
        public void IsRelariaVariant_ZeroChance_NeverVariant()
        {
            Assert.IsFalse(MlTreasureDrop.IsRelariaVariant(bossWcid: 1004150, relariaChance: 0.0, roll: 0.0));
        }

        /// <summary>The map weenie the drop creates is the one this branch actually authored
        /// (Content/sql/weenies/1004100 ML Treasure Map.sql), and it is inside the ml-treasure-hunt
        /// reserved block 1004100-1004199 (TREASURE-HUNT-PLAN.md section 3).</summary>
        [TestMethod]
        public void TreasureMapWcid_IsTheAuthoredMapWeenie()
        {
            Assert.AreEqual(1004100u, MlTreasureDrop.TreasureMapWcid);
        }
    }
}
