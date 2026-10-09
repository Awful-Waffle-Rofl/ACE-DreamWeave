using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MlDigsite;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// RoZ playtest feedback (round 18, 2026-09-24): the drum cadence's Ring and Wall shapes hit players with
    /// an invisible geometric check - nothing was visibly cast, so the shapes read as unexplained damage.
    /// DrumCadenceMechanic now casts a REAL retail spell for both shapes at resolve (the boss AND every live
    /// Boss Rush add), driven by three new tunables, and plays a beat-coloured PlayScript on the boss for
    /// every beat.
    ///
    /// HARNESS LIMIT, the same one MonsterEffectCastspellTests/MonsterEffectRecastTests already document for
    /// this codebase: `new Spell(spellId)` reads DatManager.PortalDat.SpellTable immediately in its own
    /// Init, which is never loaded under ACE.Server.Tests, so no test here can construct a real Spell or
    /// drive TryCastShape's cast path end to end. What IS testable in-process: the pure decision rules
    /// (DrumBeatPlayScript, IsValidDrumSpell, SelectDrumCasters - none of which touch DatManager), the three
    /// tunables and their config snapshots, CastSpellProjectiles' null guards, and - by source-text pin, the
    /// same pattern MlDigsiteDrumMotionTests uses for the sibling feature - that the wiring is actually there.
    /// </summary>
    [TestClass]
    public class MlDigsiteDrumSpellTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---- DrumBeatPlayScript ------------------------------------------------------------------------------

        [TestMethod]
        public void DrumBeatPlayScript_maps_each_beat_to_its_own_colour()
        {
            Assert.AreEqual(PlayScript.RestrictionEffectBlue, MlDigsiteBossMechanicRules.DrumBeatPlayScript(1));
            Assert.AreEqual(PlayScript.RestrictionEffectGreen, MlDigsiteBossMechanicRules.DrumBeatPlayScript(2));
            Assert.AreEqual(PlayScript.RestrictionEffectGold, MlDigsiteBossMechanicRules.DrumBeatPlayScript(3));
        }

        [TestMethod]
        public void DrumBeatPlayScript_falls_back_to_Invalid_outside_1_to_3()
        {
            Assert.AreEqual(PlayScript.Invalid, MlDigsiteBossMechanicRules.DrumBeatPlayScript(0));
            Assert.AreEqual(PlayScript.Invalid, MlDigsiteBossMechanicRules.DrumBeatPlayScript(4));
        }

        [TestMethod]
        public void The_enum_values_match_the_owner_ruling_0x98_0x99_0x9A()
        {
            // Pinned against source (ACE.Entity/Enum/PlayScript.cs), not recalled - the spec's own evidence
            // requirement for a numeric citation written into code.
            Assert.AreEqual(0x98u, (uint)PlayScript.RestrictionEffectBlue);
            Assert.AreEqual(0x99u, (uint)PlayScript.RestrictionEffectGreen);
            Assert.AreEqual(0x9Au, (uint)PlayScript.RestrictionEffectGold);
        }

        // ---- IsValidDrumSpell ---------------------------------------------------------------------------------

        [TestMethod]
        public void IsValidDrumSpell_rejects_a_zero_or_negative_id_regardless_of_classification()
        {
            Assert.IsFalse(MlDigsiteBossMechanicRules.IsValidDrumSpell(0, ProjectileSpellType.Ring, ProjectileSpellType.Ring));
            Assert.IsFalse(MlDigsiteBossMechanicRules.IsValidDrumSpell(-1, ProjectileSpellType.Ring, ProjectileSpellType.Ring));
        }

        [TestMethod]
        public void IsValidDrumSpell_rejects_a_positive_id_of_the_wrong_shape()
        {
            Assert.IsFalse(MlDigsiteBossMechanicRules.IsValidDrumSpell(1844, ProjectileSpellType.Wall, ProjectileSpellType.Ring));
            Assert.IsFalse(MlDigsiteBossMechanicRules.IsValidDrumSpell(3993, ProjectileSpellType.Ring, ProjectileSpellType.Wall));
            Assert.IsFalse(MlDigsiteBossMechanicRules.IsValidDrumSpell(5, ProjectileSpellType.Bolt, ProjectileSpellType.Ring));
        }

        [TestMethod]
        public void IsValidDrumSpell_accepts_a_positive_id_that_classifies_as_the_expected_shape()
        {
            Assert.IsTrue(MlDigsiteBossMechanicRules.IsValidDrumSpell(3993, ProjectileSpellType.Ring, ProjectileSpellType.Ring));
            Assert.IsTrue(MlDigsiteBossMechanicRules.IsValidDrumSpell(1844, ProjectileSpellType.Wall, ProjectileSpellType.Wall));
        }

        // ---- SelectDrumCasters ---------------------------------------------------------------------------------

        [TestMethod]
        public void SelectDrumCasters_puts_the_boss_first_then_adds_in_order_up_to_the_cap()
        {
            var boss = TestCreatures.CreateDefender();
            var add1 = TestCreatures.CreateDefender();
            var add2 = TestCreatures.CreateDefender();
            var add3 = TestCreatures.CreateDefender();

            var casters = MlDigsiteBossMechanicRules.SelectDrumCasters(boss, new List<Creature> { add1, add2, add3 }, 3);

            Assert.AreEqual(3, casters.Count);
            Assert.AreSame(boss, casters[0], "the boss casts too, and always first");
            Assert.AreSame(add1, casters[1]);
            Assert.AreSame(add2, casters[2]);
            CollectionAssert.DoesNotContain(casters, add3, "the third add is cut by the cap - the boss counts against the total");
        }

        [TestMethod]
        public void SelectDrumCasters_skips_a_dead_add_without_consuming_a_cap_slot()
        {
            var boss = TestCreatures.CreateDefender();
            var deadAdd = TestCreatures.CreateDefender();
            var aliveAdd = TestCreatures.CreateDefender();

            deadAdd.Health.Current = 0;
            Assert.IsTrue(deadAdd.IsDead, "the fixture must actually read as dead for this test to mean anything");

            var casters = MlDigsiteBossMechanicRules.SelectDrumCasters(boss, new List<Creature> { deadAdd, aliveAdd }, 5);

            Assert.AreEqual(2, casters.Count);
            Assert.AreSame(boss, casters[0]);
            Assert.AreSame(aliveAdd, casters[1], "the dead add is skipped entirely, not merely deferred");
        }

        [TestMethod]
        public void SelectDrumCasters_skips_a_dead_boss_but_still_gathers_live_adds()
        {
            var boss = TestCreatures.CreateDefender();
            boss.Health.Current = 0;

            var add = TestCreatures.CreateDefender();

            var casters = MlDigsiteBossMechanicRules.SelectDrumCasters(boss, new List<Creature> { add }, 5);

            Assert.AreEqual(1, casters.Count);
            Assert.AreSame(add, casters[0]);
        }

        [TestMethod]
        public void SelectDrumCasters_returns_empty_when_the_cap_is_below_one()
        {
            var boss = TestCreatures.CreateDefender();

            Assert.AreEqual(0, MlDigsiteBossMechanicRules.SelectDrumCasters(boss, new List<Creature>(), 0).Count);
        }

        [TestMethod]
        public void SelectDrumCasters_tolerates_a_null_adds_list_and_a_null_boss()
        {
            var boss = TestCreatures.CreateDefender();

            var bossOnly = MlDigsiteBossMechanicRules.SelectDrumCasters(boss, null, 8);
            Assert.AreEqual(1, bossOnly.Count);
            Assert.AreSame(boss, bossOnly[0]);

            var neither = MlDigsiteBossMechanicRules.SelectDrumCasters(null, null, 8);
            Assert.AreEqual(0, neither.Count);
        }

        // ---- the tunables ---------------------------------------------------------------------------------

        [TestMethod]
        public void The_default_spell_ids_and_max_casters_match_the_registered_defaults()
        {
            Assert.AreEqual(3993L, DefaultPropertyManager.DefaultLongProperties["ml_digsite_bossrush_drum_ring_spell"].Item);
            Assert.AreEqual(1844L, DefaultPropertyManager.DefaultLongProperties["ml_digsite_bossrush_drum_wall_spell"].Item);
            Assert.AreEqual(8L, DefaultPropertyManager.DefaultLongProperties["ml_digsite_bossrush_drum_max_casters"].Item);

            Assert.AreEqual(3993L, MlDigsiteTunables.BossRushDrumRingSpell);
            Assert.AreEqual(1844L, MlDigsiteTunables.BossRushDrumWallSpell);
            Assert.AreEqual(8L, MlDigsiteTunables.BossRushDrumMaxCasters);
        }

        [TestMethod]
        public void BossRushDrumMaxCasters_clamps_to_1_through_50()
        {
            PropertyManager.ModifyLong("ml_digsite_bossrush_drum_max_casters", 0L);
            try
            {
                Assert.AreEqual(1L, MlDigsiteTunables.BossRushDrumMaxCasters, "a non-positive cap must never disable casting entirely");
            }
            finally
            {
                PropertyManager.ModifyLong("ml_digsite_bossrush_drum_max_casters", 8L);
            }

            PropertyManager.ModifyLong("ml_digsite_bossrush_drum_max_casters", 99999L);
            try
            {
                Assert.AreEqual(50L, MlDigsiteTunables.BossRushDrumMaxCasters);
            }
            finally
            {
                PropertyManager.ModifyLong("ml_digsite_bossrush_drum_max_casters", 8L);
            }
        }

        [TestMethod]
        public void The_config_metadata_and_defaults_snapshots_carry_all_three_new_keys()
        {
            var metadata = PooledLootSourceText.Read("Source/ACE.Server/config-metadata.tsv");
            StringAssert.Contains(metadata, "ml_digsite_bossrush_drum_ring_spell\tserver\tGeneral\tfork\tno");
            StringAssert.Contains(metadata, "ml_digsite_bossrush_drum_wall_spell\tserver\tGeneral\tfork\tno");
            StringAssert.Contains(metadata, "ml_digsite_bossrush_drum_max_casters\tserver\tGeneral\tfork\tno");

            var defaults = PooledLootSourceText.Read("Source/config-defaults.tsv");
            StringAssert.Contains(defaults, "ml_digsite_bossrush_drum_ring_spell\tlong\t3993\t3993");
            StringAssert.Contains(defaults, "ml_digsite_bossrush_drum_wall_spell\tlong\t1844\t1844");
            StringAssert.Contains(defaults, "ml_digsite_bossrush_drum_max_casters\tlong\t8\t8");
        }

        // ---- CastSpellProjectiles' null guards, exercised for real (no dats needed) ------------------------

        [TestMethod]
        public void CastSpellProjectiles_returns_quietly_for_a_null_caster()
        {
            var ctx = NewContext(TestCreatures.CreateDefender());
            ctx.CastSpellProjectiles(null, null, null);
        }

        [TestMethod]
        public void CastSpellProjectiles_returns_quietly_for_a_null_spell_even_with_a_live_caster()
        {
            var ctx = NewContext(TestCreatures.CreateDefender());

            // A real caster, still no landblock (a test creature never enters the world) and no Spell (which
            // this harness cannot construct at all - see the class remarks). The null-spell guard must fire
            // before either the landblock or the DatManager-backed spell ever comes into play.
            ctx.CastSpellProjectiles(TestCreatures.CreateDefender(), null, null);
        }

        // ---- wiring, by source-text pin (the pattern MlDigsiteDrumMotionTests uses for the sibling feature) --

        [TestMethod]
        public void Each_beat_also_plays_the_beat_coloured_PlayScript_on_the_boss()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/Mechanics/DrumCadenceMechanic.cs");
            var tick = PooledLootSourceText.MethodBody(src, "public void Tick(MlDigsiteMechanicContext ctx)");

            StringAssert.Contains(tick, "ctx.Say($\"{ctx.Boss.Name} strikes the drum. ({beat})\", ChatMessageType.WorldBroadcast);",
                "the chat line is unchanged");
            StringAssert.Contains(tick, "ctx.Sound(Sound.UI_Drums);", "the sound is unchanged");
            StringAssert.Contains(tick, "ctx.Motion((MotionCommand)MlDigsiteTunables.BossRushDrumMotion);", "the motion is unchanged");
            StringAssert.Contains(tick, "MlDigsiteProps.PlayScriptOn(ctx.Boss, MlDigsiteBossMechanicRules.DrumBeatPlayScript(beat));",
                "the new beat-coloured visual");
        }

        [TestMethod]
        public void Resolve_tries_the_spell_cast_before_either_geometric_fallback()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/Mechanics/DrumCadenceMechanic.cs");
            var resolve = PooledLootSourceText.MethodBody(src, "private static void Resolve(MlDigsiteMechanicContext ctx, MlDigsiteDrumShape shape, DateTime resolveAt)");

            var ringCast = resolve.IndexOf(
                "if (TryCastShape(ctx, MlDigsiteTunables.BossRushDrumRingSpell, ProjectileSpellType.Ring, shape))", StringComparison.Ordinal);
            var ringFallback = resolve.IndexOf("hit = ctx.RadialHit(centre, radius, damage, DamageType.Bludgeon);", StringComparison.Ordinal);

            Assert.IsTrue(ringCast >= 0, "the Ring resolve must try the spell cast");
            Assert.IsTrue(ringFallback > ringCast, "and the original geometric hit stays as the fallback, tried second");

            var wallCast = resolve.IndexOf(
                "if (TryCastShape(ctx, MlDigsiteTunables.BossRushDrumWallSpell, ProjectileSpellType.Wall, shape))", StringComparison.Ordinal);
            var wallFallback = resolve.IndexOf("MlDigsiteBossMechanicRules.InsideWall(", StringComparison.Ordinal);

            Assert.IsTrue(wallCast >= 0, "the Wall resolve must try the spell cast");
            Assert.IsTrue(wallFallback > wallCast, "and the original InsideWall geometric hit stays as the fallback, tried second");
        }

        [TestMethod]
        public void TryCastShape_falls_back_without_ever_throwing_on_an_invalid_id_and_never_casts_on_valid_one_without_a_live_caster()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/Mechanics/DrumCadenceMechanic.cs");
            var body = PooledLootSourceText.MethodBody(src,
                "private static bool TryCastShape(MlDigsiteMechanicContext ctx, long spellId, ProjectileSpellType expected, MlDigsiteDrumShape shape)");

            StringAssert.Contains(body, "MlDigsiteBossMechanicRules.IsValidDrumSpell(spellId, actualType, expected)", "the pure rule decides validity");
            StringAssert.Contains(body, "return false;", "an invalid id returns to the caller's fallback rather than throwing");
            StringAssert.Contains(body, "spell.NotFound", "a spell that fails to load is also a fallback, not a throw");
            StringAssert.Contains(body, "MlDigsiteBossMechanicRules.SelectDrumCasters(ctx.Boss, ctx.Encounter.LiveAddSnapshot(), (int)MlDigsiteTunables.BossRushDrumMaxCasters)",
                "casters come from the LIVE ADD snapshot, never the wave list");
            StringAssert.Contains(body, "ctx.CastSpellProjectiles(casters[i], spell, target);", "each caster casts through the context, not directly");
        }

        [TestMethod]
        public void CastSpellProjectiles_queues_onto_the_casters_own_landblock_and_skips_a_dead_or_destroyed_caster()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteBossMechanics.cs");
            var body = PooledLootSourceText.MethodBody(src, "public void CastSpellProjectiles(Creature caster, Spell spell, WorldObject target)");

            StringAssert.Contains(body, "caster.CurrentLandblock", "queued on the CASTER's own landblock, not the boss's");
            StringAssert.Contains(body, "landblock.EnqueueAction(new ActionEventDelegate(", "the same queued-action shape as Sound/Motion");
            StringAssert.Contains(body, "if (caster.IsDestroyed || caster.IsDead)", "a caster gone by the time the action runs is skipped");
            StringAssert.Contains(body, "caster.CreateSpellProjectiles(spell, target, null);",
                "the low-level entry that skips the normal cast windup - the drum beats ARE the windup");
        }

        // ---- helpers -----------------------------------------------------------------------------------------

        private static readonly DateTime T0 = new DateTime(2026, 9, 24, 18, 0, 0, DateTimeKind.Utc);

        private static MlDigsiteMechanicContext NewContext(Creature boss)
        {
            var encounter = new MlDigsiteEncounter(1, 0x50000001, "Digger", MlDigsiteType.BossRush, new ACE.Entity.Position(), T0);
            var state = new MlDigsiteBossMechanicState(new MlDigsiteMechanicSet(1, MlDigsiteMechanic.Drums, MlDigsiteMechanic.None, 0.25, 1.0), T0);

            return new MlDigsiteMechanicContext(encounter, state, boss, T0, MlDigsiteMechanicSlot.Main,
                MlDigsiteMechanic.Drums, MlDigsiteMechanicArgs.Empty, new Random(1));
        }
    }
}
