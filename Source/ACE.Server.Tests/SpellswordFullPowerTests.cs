using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-11-01 Spellsword full-power pass (SPELLSWORD-DESIGN.md section 11): a swing at a full power
    /// bar upgrades each war proc's shape (Streak -> Bolt, Blast -> Wall, Ring -> Ring II) and every proc's
    /// damage scales by 1 + a x PowerLevel. Everything here is a pure function of SpellswordSpellTables, so no
    /// PropertyManager key is read (the only tunable reads are of the DEFAULT dictionaries, which are static
    /// data) and nothing needs seeding.
    ///
    /// The expected ids are literal numbers read out of ace_world.spell on 2026-11-01 (by e_Type), NOT derived
    /// from the SpellId enum members the production tables use, so a wrong member in the table fails here.
    /// </summary>
    [TestClass]
    public class SpellswordFullPowerTests
    {
        private static readonly DamageType[] EightTypes =
        {
            DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon,
            DamageType.Cold, DamageType.Fire, DamageType.Acid, DamageType.Electric, DamageType.Nether,
        };

        // ---- 1. Bolt table ----------------------------------------------------------------------

        private static uint[] Ids(params uint[] ids) => ids;

        [TestMethod]
        public void BoltByType_EveryTypeHasAllEightRungsWithTheDatabaseIds()
        {
            var expected = new System.Collections.Generic.Dictionary<DamageType, uint[]>
            {
                [DamageType.Slash]    = Ids(92, 93, 94, 95, 96, 97, 2146, 4457),
                [DamageType.Pierce]   = Ids(86, 87, 88, 89, 90, 91, 2132, 4443),
                [DamageType.Bludgeon] = Ids(64, 65, 66, 67, 68, 69, 2144, 4455),
                [DamageType.Cold]     = Ids(28, 70, 71, 72, 73, 74, 2136, 4447),
                [DamageType.Fire]     = Ids(27, 81, 82, 83, 84, 85, 2128, 4439),
                [DamageType.Acid]     = Ids(58, 59, 60, 61, 62, 63, 2122, 4433),
                [DamageType.Electric] = Ids(75, 76, 77, 78, 79, 80, 2140, 4451),
                [DamageType.Nether]   = Ids(5349, 5350, 5351, 5352, 5353, 5354, 5355, 5356),
            };

            CollectionAssert.AreEquivalent(EightTypes, SpellswordSpellTables.BoltByType.Keys.ToArray());

            foreach (var type in EightTypes)
                CollectionAssert.AreEqual(expected[type], SpellswordSpellTables.BoltByType[type].Select(s => (uint)s).ToArray(), type.ToString());
        }

        [TestMethod]
        public void BoltLevels_AreParallelToEveryBoltArrayAndToTheStreakLevels()
        {
            // Nether Bolt has the full I-VIII (5349-5356), so there is no short array to special-case; this
            // pins that every array is exactly as long as the level array, and that Bolt and Streak share
            // level numbering - the property "same rung" depends on.
            foreach (var type in EightTypes)
                Assert.AreEqual(SpellswordSpellTables.BoltLevels.Length, SpellswordSpellTables.BoltByType[type].Length, type.ToString());

            CollectionAssert.AreEqual(SpellswordSpellTables.StreakLevels, SpellswordSpellTables.BoltLevels);
        }

        [TestMethod]
        public void SelectSpell_BoltPicksTheSameRungIndexAsStreak_ForEverySkillRankAndType()
        {
            foreach (var type in EightTypes)
            {
                for (var rank = 1; rank <= 3; rank++)
                {
                    foreach (uint skill in new uint[] { 0, 1, 49, 50, 100, 150, 199, 200, 250, 299, 300, 399, 400, 1000 })
                    {
                        var streak = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels, type, skill, rank, 1.0);
                        var bolt = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.BoltByType, SpellswordSpellTables.BoltLevels, type, skill, rank, 1.0);

                        Assert.AreEqual(
                            System.Array.IndexOf(SpellswordSpellTables.StreakByType[type], streak.Value),
                            System.Array.IndexOf(SpellswordSpellTables.BoltByType[type], bolt.Value),
                            $"{type} rank {rank} skill {skill}");
                    }
                }
            }
        }

        [TestMethod]
        public void SelectSpell_BoltRankCapStillApplies_AndLevelEightIsUnreachable()
        {
            // Rank caps at level 3 / 5 / 7 whatever the skill, so the Incantation (index 7) never fires.
            Assert.AreEqual(82u, (uint)SpellswordSpellTables.SelectSpell(SpellswordSpellTables.BoltByType, SpellswordSpellTables.BoltLevels, DamageType.Fire, 1000, 1, 1.0).Value);   // Flame Bolt III
            Assert.AreEqual(84u, (uint)SpellswordSpellTables.SelectSpell(SpellswordSpellTables.BoltByType, SpellswordSpellTables.BoltLevels, DamageType.Fire, 1000, 2, 1.0).Value);   // Flame Bolt V
            Assert.AreEqual(2128u, (uint)SpellswordSpellTables.SelectSpell(SpellswordSpellTables.BoltByType, SpellswordSpellTables.BoltLevels, DamageType.Fire, 1000, 3, 1.0).Value); // Ilservian's Flame
            Assert.AreEqual(5355u, (uint)SpellswordSpellTables.SelectSpell(SpellswordSpellTables.BoltByType, SpellswordSpellTables.BoltLevels, DamageType.Nether, 1000, 3, 1.0).Value);
        }

        // ---- 2. Wall and Ring II tables ----------------------------------------------------------

        [TestMethod]
        public void WallByType_IsTheSevenElementalWalls_AndNetherHasNone()
        {
            var expected = new System.Collections.Generic.Dictionary<DamageType, uint>
            {
                [DamageType.Acid] = 1839, [DamageType.Slash] = 1840, [DamageType.Fire] = 1841,
                [DamageType.Pierce] = 1842, [DamageType.Cold] = 1843, [DamageType.Electric] = 1844,
                [DamageType.Bludgeon] = 1845,
            };

            Assert.AreEqual(7, SpellswordSpellTables.WallByType.Count);
            Assert.IsFalse(SpellswordSpellTables.WallByType.ContainsKey(DamageType.Nether), "no void wall exists");

            foreach (var kv in expected)
                Assert.AreEqual(kv.Value, (uint)SpellswordSpellTables.WallByType[kv.Key], kv.Key.ToString());
        }

        [TestMethod]
        public void RingIIByType_IsTheEightRingIIs_NotInDamageTypeOrder()
        {
            var expected = new System.Collections.Generic.Dictionary<DamageType, uint>
            {
                [DamageType.Acid] = 6189, [DamageType.Slash] = 6190, [DamageType.Fire] = 6191,
                [DamageType.Pierce] = 6192, [DamageType.Cold] = 6193, [DamageType.Electric] = 6194,
                [DamageType.Nether] = 6195, [DamageType.Bludgeon] = 6196,
            };

            CollectionAssert.AreEquivalent(EightTypes, SpellswordSpellTables.RingIIByType.Keys.ToArray());

            foreach (var kv in expected)
                Assert.AreEqual(kv.Value, (uint)SpellswordSpellTables.RingIIByType[kv.Key], kv.Key.ToString());
        }

        // ---- 3. Wall gate ------------------------------------------------------------------------

        [TestMethod]
        public void WallReplacesBlast_OnlyWhenTheBlastIsAtLeastTheWallLevel()
        {
            Assert.IsFalse(SpellswordSpellTables.WallReplacesBlast(blastLevel: 3, wallLevel: 5), "below");
            Assert.IsFalse(SpellswordSpellTables.WallReplacesBlast(blastLevel: 4, wallLevel: 5), "just below");
            Assert.IsTrue(SpellswordSpellTables.WallReplacesBlast(blastLevel: 5, wallLevel: 5), "at");
            Assert.IsTrue(SpellswordSpellTables.WallReplacesBlast(blastLevel: 6, wallLevel: 5), "above");
            Assert.IsTrue(SpellswordSpellTables.WallReplacesBlast(blastLevel: 7, wallLevel: 5), "far above");
        }

        [TestMethod]
        public void WallReplacesBlast_WithTheRealWallLevelFive_SwapsFromBlastRungVUp()
        {
            // The 7 Walls are DAT Power 200 = spell level 5 (probed from client_portal.dat, ids 1839-1845).
            // BlastLevels is {3,4,5,6,7,8}: rungs III and IV keep the Blast, V, VI, VII (and VIII) wall.
            const uint wallLevel = 5;

            foreach (var level in SpellswordSpellTables.BlastLevels)
                Assert.AreEqual(level >= 5, SpellswordSpellTables.WallReplacesBlast(level, wallLevel), $"Blast level {level}");

            // rank 1 caps at level III, so a rank-1 Runeblade never walls; rank 2 (cap V) and 3 (cap VII) do
            Assert.IsFalse(SpellswordSpellTables.WallReplacesBlast(SpellswordSpellTables.MaxLevelForRank(1), wallLevel));
            Assert.IsTrue(SpellswordSpellTables.WallReplacesBlast(SpellswordSpellTables.MaxLevelForRank(2), wallLevel));
            Assert.IsTrue(SpellswordSpellTables.WallReplacesBlast(SpellswordSpellTables.MaxLevelForRank(3), wallLevel));
        }

        // ---- 4. Nether parity --------------------------------------------------------------------

        [TestMethod]
        public void NetherBolt_ParityReference_IsFireBoltAtTheSameIndex()
        {
            var nether = SpellswordSpellTables.BoltByType[DamageType.Nether];
            var fire = SpellswordSpellTables.BoltByType[DamageType.Fire];

            for (var i = 0; i < nether.Length; i++)
                Assert.AreEqual(fire[i], SpellswordSpellTables.ParityReferenceFor(SpellswordSpellTables.BoltByType, DamageType.Nether, nether[i]), $"rung {i + 1}");

            // an elemental Bolt stays unstamped
            Assert.IsNull(SpellswordSpellTables.ParityReferenceFor(SpellswordSpellTables.BoltByType, DamageType.Fire, fire[0]));
        }

        [TestMethod]
        public void CloudedSoulII_ParityReference_IsCassiusRingOfFireII()
        {
            Assert.AreEqual((SpellId?)SpellId.CassiusRingOfFireII,
                SpellswordSpellTables.RingParityReferenceFor(DamageType.Nether, SpellswordSpellTables.RingIIByType));
            Assert.AreEqual(6191u, (uint)SpellswordSpellTables.RingParityReferenceFor(DamageType.Nether, SpellswordSpellTables.RingIIByType).Value);
            Assert.IsNull(SpellswordSpellTables.RingParityReferenceFor(DamageType.Fire, SpellswordSpellTables.RingIIByType));

            // the one-argument overload still answers the tier-I ring
            Assert.AreEqual((SpellId?)SpellId.FlameRing, SpellswordSpellTables.RingParityReferenceFor(DamageType.Nether));
        }

        // ---- 5. Full-bar threshold ---------------------------------------------------------------

        [TestMethod]
        public void FullPowerThreshold_IsAHairBelowOne_AndOnlyAFullBarPasses()
        {
            Assert.IsTrue(SpellswordSpellTables.FullPowerThreshold < 1.0f && SpellswordSpellTables.FullPowerThreshold >= 0.98f,
                "slack must stay tiny: a lower threshold makes partial power optimal");

            Assert.IsTrue(SpellswordSpellTables.IsFullPower(1.0f));
            Assert.IsTrue(SpellswordSpellTables.IsFullPower(SpellswordSpellTables.FullPowerThreshold));
            Assert.IsFalse(SpellswordSpellTables.IsFullPower(0.98f));
            Assert.IsFalse(SpellswordSpellTables.IsFullPower(0.9f));
            Assert.IsFalse(SpellswordSpellTables.IsFullPower(0.5f));
            Assert.IsFalse(SpellswordSpellTables.IsFullPower(0.0f));
        }

        [TestMethod]
        public void UseFullPowerShape_RequiresTheKillSwitchAndAFullBar()
        {
            Assert.IsTrue(SpellswordSpellTables.UseFullPowerShape(true, 1.0f));
            Assert.IsFalse(SpellswordSpellTables.UseFullPowerShape(false, 1.0f), "kill switch off");
            Assert.IsFalse(SpellswordSpellTables.UseFullPowerShape(true, 0.6f));
        }

        // ---- 6. Which hits count as a swing ------------------------------------------------------

        [TestMethod]
        public void SwingPowerLevel_IsPowerModMinusHalf_ForARealMeleeSwing()
        {
            Assert.AreEqual(0.0f, SpellswordSpellTables.SwingPowerLevel(0.5f, true), 0.0f);
            Assert.AreEqual(0.4f, SpellswordSpellTables.SwingPowerLevel(0.9f, true), 1e-6);
            Assert.AreEqual(1.0f, SpellswordSpellTables.SwingPowerLevel(1.5f, true), 0.0f);
            Assert.AreEqual(1.0f, SpellswordSpellTables.SwingPowerLevel(3.0f, true), 0.0f, "clamped");
            Assert.AreEqual(0.0f, SpellswordSpellTables.SwingPowerLevel(0.0f, true), 0.0f, "clamped");
        }

        [TestMethod]
        public void SwingPowerLevel_IsZeroForAHitThatWasNotAPowerBarSwing()
        {
            // A Riposte counter reaches Player.DamageTarget with powerBarSwing:false and a PowerMod taken from
            // whatever the bar last held - which can be a full 1.5. It must read as power 0.
            Assert.AreEqual(0.0f, SpellswordSpellTables.SwingPowerLevel(1.5f, false), 0.0f);

            var counter = new DamageEvent { CombatType = CombatType.Melee, PowerMod = 1.5f, PowerBarSwing = false };
            Assert.AreEqual(0.0f, SpellswordSpellTables.SwingPowerLevel(counter), 0.0f);
            Assert.IsFalse(SpellswordSpellTables.IsFullPower(SpellswordSpellTables.SwingPowerLevel(counter)));

            var swing = new DamageEvent { CombatType = CombatType.Melee, PowerMod = 1.5f, PowerBarSwing = true };
            Assert.AreEqual(1.0f, SpellswordSpellTables.SwingPowerLevel(swing), 0.0f);

            // a missile hit reads PowerMod 1.0 (ranged weapons) and must never read as a half bar
            var missile = new DamageEvent { CombatType = CombatType.Missile, PowerMod = 1.0f, PowerBarSwing = true };
            Assert.AreEqual(0.0f, SpellswordSpellTables.SwingPowerLevel(missile), 0.0f);

            Assert.AreEqual(0.0f, SpellswordSpellTables.SwingPowerLevel((DamageEvent)null), 0.0f);
        }

        [TestMethod]
        public void SourcePins_OnlyRiposteOptsOutOfThePowerBar_AndEveryHandlerReadsTheSwingPower()
        {
            // Player.DamageTarget needs a live Session, so the wiring is pinned against the source text.
            var combat = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(
                ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Combat.cs"),
                "public DamageEvent DamageTarget(Creature target, WorldObject damageSource, float damageMultiplier = 1.0f, bool primaryTarget = true, bool powerBarSwing = true)");

            var mark = combat.IndexOf("damageEvent.PowerBarSwing = powerBarSwing;", System.StringComparison.Ordinal);
            var outgoing = combat.IndexOf("ApplyOutgoingDamageClassAbilities(target, damageEvent);", System.StringComparison.Ordinal);
            Assert.IsTrue(mark >= 0 && outgoing > mark, "the swing mark precedes the outgoing-damage dispatch");

            foreach (var file in new[] { "SpellbladeAbility.cs", "RunebladeAbility.cs", "SpellstormAbility.cs" })
            {
                var text = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/" + file);
                StringAssert.Contains(text, "SpellswordSpellTables.SwingPowerLevel(damageEvent)", file);
                StringAssert.Contains(text, "SpellswordSpellTables.PowerProcScale(swingPower,", file);
                StringAssert.Contains(text, "class_ability_spellsword_power_proc_scale", file);
                StringAssert.Contains(text, "class_ability_spellsword_full_power_shape", file);
            }
        }

        // ---- 7. Power multiplier -----------------------------------------------------------------

        [TestMethod]
        public void PowerProcScale_EmptyBarIsExactlyOne_FullBarIsOnePlusA()
        {
            Assert.AreEqual(1.0f, SpellswordSpellTables.PowerProcScale(0.0f, 0.78), 0.0f);
            Assert.AreEqual(1.0f, SpellswordSpellTables.PowerProcScale(0.0f, 123.0), 0.0f, "any tunable, empty bar");
            Assert.AreEqual(1.78f, SpellswordSpellTables.PowerProcScale(1.0f, 0.78), 1e-6);
            Assert.AreEqual(1.39f, SpellswordSpellTables.PowerProcScale(0.5f, 0.78), 1e-6);
        }

        [TestMethod]
        public void PowerProcScale_ClampsPowerAndReadsABadTunableAsZero()
        {
            Assert.AreEqual(1.78f, SpellswordSpellTables.PowerProcScale(7.0f, 0.78), 1e-6, "power clamped to 1");
            Assert.AreEqual(1.0f, SpellswordSpellTables.PowerProcScale(-3.0f, 0.78), 0.0f, "power clamped to 0");
            Assert.AreEqual(1.0f, SpellswordSpellTables.PowerProcScale(1.0f, -0.5), 0.0f, "negative tunable reads as 0");
            Assert.AreEqual(1.0f, SpellswordSpellTables.PowerProcScale(1.0f, double.NaN), 0.0f);
            Assert.AreEqual(1.0f, SpellswordSpellTables.PowerProcScale(1.0f, double.PositiveInfinity), 0.0f);
            Assert.AreEqual(1.0f, SpellswordSpellTables.PowerProcScale(float.NaN, 0.78), 0.0f);
        }

        [TestMethod]
        public void PowerProcScale_ComposesMultiplicativelyWithTheNetherParityScale()
        {
            var parity = SpellswordSpellTables.NetherParityScale(100.0, 150.0, 1.0);   // 0.6667
            var composed = parity * SpellswordSpellTables.PowerProcScale(1.0f, 0.78);

            Assert.AreEqual(parity * 1.78f, composed, 1e-6);
        }

        // ---- 8. Full power strictly beats partial power ------------------------------------------

        [TestMethod]
        public void FullPower_StrictlyBeatsEveryPartialPower_MultiplierOnlyModel()
        {
            // MULTIPLIER-ONLY MODEL: the shape factor is a literal here, not production spell selection; this
            // pins the monotonic/jump property of IsFullPower x PowerProcScale, nothing more.
            // Per-swing proc output relative to an empty bar: the swing's own damage (PowerMod = 0.5 + p) is
            // not part of the proc, but the player trades it off against the bar refill, so the invariant
            // that matters for the proc is that the (shape x multiplier) factor is non-decreasing in p and
            // JUMPS at full. Modeled full-bar shape factor 1.87 (Bolt ~2.35x Streak is the spellblade case;
            // the Wall/Ring II factors are smaller, so use the smallest, Ring II ~1.17, as the floor).
            const double smallestShapeFactor = 1.17;

            double Factor(float p)
            {
                var shape = SpellswordSpellTables.IsFullPower(p) ? smallestShapeFactor : 1.0;
                return shape * SpellswordSpellTables.PowerProcScale(p, 0.78);
            }

            var full = Factor(1.0f);

            for (var p = 0.0f; p <= 0.98f; p += 0.01f)
                Assert.IsTrue(Factor(p) < full, $"partial power {p} must be strictly worse than a full bar");

            // and non-decreasing across the whole range, so no partial setting beats a higher one
            var prev = 0.0;
            for (var p = 0.0f; p <= 1.0f; p += 0.01f)
            {
                var f = Factor(p);
                Assert.IsTrue(f >= prev - 1e-9, $"factor must not fall as power rises (p={p})");
                prev = f;
            }
        }

        // ---- 9. Tunables -------------------------------------------------------------------------

        [TestMethod]
        public void Tunables_AreRegisteredWithTheirDefaults()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties["class_ability_spellsword_full_power_shape"].Item,
                "new settings default ON");
            Assert.AreEqual(0.78, DefaultPropertyManager.DefaultDoubleProperties["class_ability_spellsword_power_proc_scale"].Item, 1e-12);

            StringAssert.StartsWith(DefaultPropertyManager.DefaultBooleanProperties["class_ability_spellsword_full_power_shape"].Description, "(non-retail function) Spellsword:");
            StringAssert.StartsWith(DefaultPropertyManager.DefaultDoubleProperties["class_ability_spellsword_power_proc_scale"].Description, "(non-retail function) Spellsword:");
        }
    }
}
