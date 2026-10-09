using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.MlDigsite;
using ACE.Server.MlDigsite.Mechanics;
using ACE.Server.Physics.Animation;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// RoZ playtest feedback (2026-09-24): the Boss Rush drum cadence announced each beat in chat and drew
    /// marker telegraphs, but the boss itself played no visible animation. DrumCadenceMechanic now also
    /// broadcasts a motion on the boss through MlDigsiteMechanicContext.Motion, driven by the
    /// ml_digsite_bossrush_drum_motion tunable (default MotionCommand.AttackHigh1 = 268435554).
    ///
    /// The broadcast itself needs a live landblock (EnqueueBroadcastMotion), which ACE.Server.Tests cannot
    /// construct - the same limit MlDigsiteRozR17Tests documents for the rest of this driver. What IS
    /// testable in-process: the tunable's value and cast, the skip guards (MotionCommand.Invalid and a
    /// MotionTableId of 0, both real production code paths that never touch DatManager), and - by source-text
    /// pin, the same pattern MlDigsiteRozR17Tests uses for this same file - that DrumCadenceMechanic actually
    /// wires the call in and that ctx.Motion carries both guards.
    /// </summary>
    [TestClass]
    public class MlDigsiteDrumMotionTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---- the tunable -----------------------------------------------------------------------------------

        [TestMethod]
        public void The_default_motion_is_AttackHigh1_and_the_tunable_casts_to_it_cleanly()
        {
            Assert.AreEqual(268435554L, DefaultPropertyManager.DefaultLongProperties["ml_digsite_bossrush_drum_motion"].Item);
            Assert.AreEqual(268435554L, MlDigsiteTunables.BossRushDrumMotion);

            Assert.AreEqual(MotionCommand.AttackHigh1, (MotionCommand)MlDigsiteTunables.BossRushDrumMotion);
        }

        [TestMethod]
        public void The_config_metadata_and_defaults_snapshots_carry_the_new_key()
        {
            // Guards against the tunable being read in code but never registered, which is exactly what
            // config-metadata.tsv / config-defaults.tsv --check exists to catch - pinned here as well so a
            // unit test fails fast rather than only CI's snapshot check.
            var metadata = PooledLootSourceText.Read("Source/ACE.Server/config-metadata.tsv");
            StringAssert.Contains(metadata, "ml_digsite_bossrush_drum_motion\tserver\tGeneral\tfork\tno");

            var defaults = PooledLootSourceText.Read("Source/config-defaults.tsv");
            StringAssert.Contains(defaults, "ml_digsite_bossrush_drum_motion\tlong\t268435554\t268435554");
        }

        // ---- the skip guards, exercised for real (no dats needed) ------------------------------------------

        [TestMethod]
        public void MotionCommand_Invalid_is_the_off_switch_and_never_throws_with_no_landblock()
        {
            var ctx = NewContext(TestCreatures.CreateDefender());

            // Boss.CurrentLandblock is null (a test creature never enters the world) - the call must return
            // quietly whether it short-circuits on Invalid or on the null landblock.
            ctx.Motion(MotionCommand.Invalid);
        }

        [TestMethod]
        public void A_zero_MotionTableId_skips_without_ever_touching_DatManager()
        {
            // TestCreatures.CreateDefender never sets PropertyDataId.MotionTable, so MotionTableId is 0 - the
            // same guard Physics.Animation.MotionTable.GetAnimationLength takes before it would read a dat
            // (motionTableId == 0 -> return 0), proven directly below without a live boss at all.
            Assert.AreEqual(0u, TestCreatures.CreateDefender().MotionTableId);

            Assert.AreEqual(0f, MotionTable.GetAnimationLength(0, MotionStance.HandCombat, MotionCommand.AttackHigh1));

            var ctx = NewContext(TestCreatures.CreateDefender());
            ctx.Motion(MotionCommand.AttackHigh1);   // must not throw even though it resolves to a real motion
        }

        // ---- wiring, by source-text pin (the pattern MlDigsiteRozR17Tests uses for this same file) ---------

        [TestMethod]
        public void Each_beat_broadcasts_the_tunable_motion_alongside_the_unchanged_chat_line_and_sound()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/Mechanics/DrumCadenceMechanic.cs");

            var tick = PooledLootSourceText.MethodBody(src, "public void Tick(MlDigsiteMechanicContext ctx)");

            StringAssert.Contains(tick, "ctx.Say($\"{ctx.Boss.Name} strikes the drum. ({beat})\", ChatMessageType.WorldBroadcast);",
                "the chat line is unchanged");
            StringAssert.Contains(tick, "ctx.Sound(Sound.UI_Drums);", "the sound is unchanged");
            StringAssert.Contains(tick, "ctx.Motion((MotionCommand)MlDigsiteTunables.BossRushDrumMotion);",
                "the new animation broadcast, driven by the tunable rather than a hardcoded motion");
        }

        [TestMethod]
        public void Motion_carries_both_the_off_switch_and_the_no_animation_skip()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteBossMechanics.cs");

            var motion = PooledLootSourceText.MethodBody(src, "public void Motion(MotionCommand motion)");

            StringAssert.Contains(motion, "if (motion == MotionCommand.Invalid)", "0 is the documented off switch");
            StringAssert.Contains(motion,
                "if (Physics.Animation.MotionTable.GetAnimationLength(target.MotionTableId, stance, motion) <= 0f)",
                "a bad tunable value (no animation in the boss's current stance) is skipped rather than broadcast");
            StringAssert.Contains(motion, "target.EnqueueBroadcastMotion(new Motion(stance, motion));");
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
