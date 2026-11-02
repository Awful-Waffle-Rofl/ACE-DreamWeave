using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H4b, heals, pacing and cosmetics. The pure rules in WorldEventHoldRules.Heals.cs (ported in intent from
    /// the contributor's WorldEventHoldSettingsHealing / WorldEventHoldSurvivalTests heal cases and the defend-pose rules), then
    /// the one heal path (Creature.ApplyHoldKnightHeal), the marshalling in WorldEventHoldHeals (a player heal never writes the
    /// knight on the caller's thread), and the shrine on a real WorldEventHoldRun. No WorldEvent, no landblock, no
    /// PropertyManager: the run's settings come through its resolver seam and heals through the WorldEventHoldHeals.Enqueue
    /// seam, which every test restores in a finally.
    /// </summary>
    [TestClass]
    public class WorldEventHoldHealsTests
    {
        private const double T0 = 1_000_000.0;

        // ---- pure: heals --------------------------------------------------------------------------------------

        [TestMethod]
        public void HealAfterHitFactor_ReducedInsideTheWindowOnly()
        {
            Assert.AreEqual(1.0, WorldEventHoldRules.HealAfterHitFactor(null, 0.5, 5), "never hit");
            Assert.AreEqual(0.5, WorldEventHoldRules.HealAfterHitFactor(2, 0.5, 5));
            Assert.AreEqual(1.0, WorldEventHoldRules.HealAfterHitFactor(5, 0.5, 5), "the window is half-open");
            Assert.AreEqual(1.0, WorldEventHoldRules.HealAfterHitFactor(2, 0, 5), "no reduction");
            Assert.AreEqual(0.0, WorldEventHoldRules.HealAfterHitFactor(2, 3, 5), "a reduction above 1 is full, never negative");
            Assert.AreEqual(1.0, WorldEventHoldRules.HealAfterHitFactor(-1, 0.5, 5), "a clock step back is no window");
        }

        [TestMethod]
        public void HealWithinBudget_PerWaveShare_NoBudgetAtZero()
        {
            Assert.AreEqual(300, WorldEventHoldRules.HealWithinBudget(300, 10000, 0, 999999), "0 = no budget");
            Assert.AreEqual(300, WorldEventHoldRules.HealWithinBudget(300, 10000, 0.05, 0));
            Assert.AreEqual(200, WorldEventHoldRules.HealWithinBudget(300, 10000, 0.05, 300), "what is left of 500");
            Assert.AreEqual(0, WorldEventHoldRules.HealWithinBudget(300, 10000, 0.05, 500));
            Assert.AreEqual(0, WorldEventHoldRules.HealWithinBudget(0, 10000, 0.05, 0));
        }

        [TestMethod]
        public void ConvertPlayerHeal_TypicalHealIsAShare_HeldByTheTenSecondCap()
        {
            double tokens = 0, lastAt = 0;

            // 10000 health: a typical (100) heal is 4% = 400; the cap is 6% per ten seconds = 600 in the bucket.
            Assert.AreEqual(400, WorldEventHoldRules.ConvertPlayerHeal(100, 10000, 0.06, ref tokens, ref lastAt, T0));
            Assert.AreEqual(200, WorldEventHoldRules.ConvertPlayerHeal(100, 10000, 0.06, ref tokens, ref lastAt, T0), "what is left of the cap");
            Assert.AreEqual(0, WorldEventHoldRules.ConvertPlayerHeal(100, 10000, 0.06, ref tokens, ref lastAt, T0), "spent");
            Assert.AreEqual(60, WorldEventHoldRules.ConvertPlayerHeal(100, 10000, 0.06, ref tokens, ref lastAt, T0 + 1), "a tenth of the cap per second");

            double t2 = 0, l2 = 0;
            Assert.AreEqual(0, WorldEventHoldRules.ConvertPlayerHeal(100, 10000, 0, ref t2, ref l2, T0), "a cap of 0 refuses");
            Assert.AreEqual(0, WorldEventHoldRules.ConvertPlayerHeal(0, 10000, 0.06, ref t2, ref l2, T0));
        }

        [TestMethod]
        public void ShareHeal_And_RescueRoom()
        {
            Assert.AreEqual(1500, WorldEventHoldRules.ShareHeal(0.15, 10000, 1));
            Assert.AreEqual(750, WorldEventHoldRules.ShareHeal(0.15, 10000, 0.5), "after-hit factor");
            Assert.AreEqual(10000, WorldEventHoldRules.ShareHeal(3, 10000, 1), "never more than all of it");
            Assert.AreEqual(0, WorldEventHoldRules.ShareHeal(double.NaN, 10000, 1));

            Assert.AreEqual(1000, WorldEventHoldRules.RescueRoom(0.10, 10000, 0));
            Assert.AreEqual(400, WorldEventHoldRules.RescueRoom(0.10, 10000, 600));
            Assert.AreEqual(0, WorldEventHoldRules.RescueRoom(0.10, 10000, 5000));
        }

        [TestMethod]
        public void IsKnightHealSpell_OnlyABeneficialHealthBoost()
        {
            Assert.IsTrue(WorldEventHoldRules.IsKnightHealSpell(true, SpellType.Boost, DamageType.Health, 50, 100));
            Assert.IsFalse(WorldEventHoldRules.IsKnightHealSpell(false, SpellType.Boost, DamageType.Health, -50, -100), "Harm");
            Assert.IsFalse(WorldEventHoldRules.IsKnightHealSpell(true, SpellType.Boost, DamageType.Stamina, 50, 100), "another vital");
            Assert.IsFalse(WorldEventHoldRules.IsKnightHealSpell(true, SpellType.Enchantment, DamageType.Health, 0, 0), "a buff");
        }

        [TestMethod]
        public void PlayerHealLine_PerOutcome_Ascii()
        {
            Assert.AreEqual("You heal Sir Donovan for 400 health points. Your kit has 3 uses left.",
                WorldEventHoldRules.PlayerHealLine("Sir Donovan", 400, HoldHealOutcome.Healed, false, " Your kit has 3 uses left."));
            Assert.AreEqual("You expertly heal Sir Donovan for 1 health point.", WorldEventHoldRules.PlayerHealLine("Sir Donovan", 1, HoldHealOutcome.Healed, true, null));
            Assert.AreEqual("Sir Donovan is already at full health.", WorldEventHoldRules.PlayerHealLine("Sir Donovan", 0, HoldHealOutcome.Full, false, null));
            Assert.AreEqual("Sir Donovan cannot be healed right now.", WorldEventHoldRules.PlayerHealLine("Sir Donovan", 0, HoldHealOutcome.Refused, false, null));
            Assert.AreEqual("Sir Donovan has been healed as much as it can be for now.", WorldEventHoldRules.PlayerHealLine("Sir Donovan", 0, HoldHealOutcome.Capped, false, null));

            foreach (var line in new[] { WorldEventHoldRules.ShrineQuietLine, WorldEventHoldRules.ShrineNobodyLine, WorldEventHoldRules.ShrineCooldownLine(12.2),
                         WorldEventHoldRules.ShrinePlayerCooldownLine(1), WorldEventHoldRules.ShrineWastedLine("Sir Donovan", HoldHealOutcome.BudgetSpent),
                         WorldEventHoldRules.ShrineWastedLine("Sir Donovan", HoldHealOutcome.Full), WorldEventHoldRules.ProtectedHealthLine("Sir Donovan", 1, 3) })
                Assert.IsTrue(line.All(c => c >= 0x20 && c < 0x7F), $"ASCII only: {line}");
        }

        // ---- pure: shrine and health display ------------------------------------------------------------------

        [TestMethod]
        public void ShrineUseCheck_ShrineRestFirst_ThenThePlayers()
        {
            Assert.AreEqual(HoldShrineUseState.Ready, WorldEventHoldRules.ShrineUseCheck(T0, 0, 0, 30));
            Assert.AreEqual(HoldShrineUseState.ShrineResting, WorldEventHoldRules.ShrineUseCheck(T0, T0 + 5, T0 + 50, 30));
            Assert.AreEqual(HoldShrineUseState.PlayerResting, WorldEventHoldRules.ShrineUseCheck(T0, 0, T0 + 5, 30));
            Assert.AreEqual(HoldShrineUseState.Ready, WorldEventHoldRules.ShrineUseCheck(T0, 0, T0 + 5, 0), "no player rest configured");

            Assert.AreEqual(0.0, WorldEventHoldRules.ShrineCooldown(0), "0 allowed on purpose");
            Assert.AreEqual(600.0, WorldEventHoldRules.ShrineCooldown(5000));
            Assert.AreEqual(45.0, WorldEventHoldRules.ShrineCooldown(double.NaN));
            Assert.AreEqual("The shrine is still gathering its strength. It will be ready in 13 seconds.", WorldEventHoldRules.ShrineCooldownLine(12.2));
        }

        [TestMethod]
        public void HealthPercent_RoundsUp_NeverZeroWhileAlive()
        {
            Assert.AreEqual(1, WorldEventHoldRules.HealthPercent(1, 10000));
            Assert.AreEqual(64, WorldEventHoldRules.HealthPercent(6399, 10000));
            Assert.AreEqual(100, WorldEventHoldRules.HealthPercent(10000, 10000));
            Assert.AreEqual(0, WorldEventHoldRules.HealthPercent(0, 10000));
            Assert.AreEqual(0, WorldEventHoldRules.HealthPercent(5, 0));
            Assert.AreEqual("Sir Donovan is at 64% health.", WorldEventHoldRules.ProtectedHealthLine("Sir Donovan", 6399, 10000));
        }

        // ---- pure: pacing and ranged --------------------------------------------------------------------------

        [TestMethod]
        public void Pacing_ScalesBySpeedMult_UnusableMultIsNoChange()
        {
            Assert.AreEqual(0.5, WorldEventHoldRules.ScaledMeleeDelay(1.0, 2f), 1e-9);
            Assert.AreEqual(2.4f, WorldEventHoldRules.ScaledAnimSpeed(1.2f, 2f), 1e-6f);
            Assert.AreEqual(3.0f, WorldEventHoldRules.ScaledRunRate(2.0f, 1.5f), 1e-6f);

            foreach (var bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            {
                Assert.AreEqual(1f, WorldEventHoldRules.SpeedMult(bad));
                Assert.AreEqual(1.0, WorldEventHoldRules.ScaledMeleeDelay(1.0, bad), 1e-9);
            }
        }

        [TestMethod]
        public void OffensiveSpells_ProjectilesAndHarm_PickedByWeight()
        {
            Assert.IsTrue(WorldEventHoldRules.IsOffensiveSpell(0x1234, null, null, null), "a bolt");
            Assert.IsTrue(WorldEventHoldRules.IsOffensiveSpell(null, null, -50, (int)DamageType.Health), "Harm");
            Assert.IsFalse(WorldEventHoldRules.IsOffensiveSpell(null, null, 50, (int)DamageType.Health), "a heal");
            Assert.IsFalse(WorldEventHoldRules.IsOffensiveSpell(null, null, -50, (int)DamageType.Stamina));
            Assert.IsFalse(WorldEventHoldRules.IsOffensiveSpell(null, null, null, null), "a buff");

            var book = new[] { new KeyValuePair<int, float>(10, 1f), new KeyValuePair<int, float>(20, 3f), new KeyValuePair<int, float>(30, 5f) };
            Func<uint, bool> offensive = id => id != 30;

            Assert.AreEqual(10, WorldEventHoldRules.PickOffensiveSpell(book, offensive, 0.0));
            Assert.AreEqual(10, WorldEventHoldRules.PickOffensiveSpell(book, offensive, 0.24));
            Assert.AreEqual(20, WorldEventHoldRules.PickOffensiveSpell(book, offensive, 0.26));
            Assert.AreEqual(20, WorldEventHoldRules.PickOffensiveSpell(book, offensive, 1.5), "clamped");
            Assert.AreEqual(0, WorldEventHoldRules.PickOffensiveSpell(book, id => false, 0.5), "a book of buffs");
        }

        // ---- pure: the cosmetic defense -----------------------------------------------------------------------

        [TestMethod]
        public void PickDefendFoe_KeepsTheCurrentInReach_ElseTheNearest()
        {
            var foes = new[] { (1u, 3.0), (2u, 1.0), (3u, 9.0) };

            Assert.AreEqual(2u, WorldEventHoldRules.PickDefendFoe(foes, 4, 0));
            Assert.AreEqual(1u, WorldEventHoldRules.PickDefendFoe(foes, 4, 1), "the one it already faces");
            Assert.AreEqual(2u, WorldEventHoldRules.PickDefendFoe(foes, 4, 3), "the current one left reach");
            Assert.AreEqual(0u, WorldEventHoldRules.PickDefendFoe(foes, 0.5, 0));
            Assert.AreEqual(0u, WorldEventHoldRules.PickDefendFoe(null, 4, 0));
        }

        [TestMethod]
        public void DecidePose_DrawFaceSwingTurnBack()
        {
            Assert.AreEqual(HoldPoseAction.DrawWeapon, WorldEventHoldRules.DecidePose(false, true, 0, T0, 0, 0, true, 20, 3));
            Assert.AreEqual(HoldPoseAction.Face, WorldEventHoldRules.DecidePose(true, true, 45, T0, 0, 0, true, 20, 3));
            Assert.AreEqual(HoldPoseAction.Swing, WorldEventHoldRules.DecidePose(true, true, 10, T0, T0, 0, false, 20, 3));
            Assert.AreEqual(HoldPoseAction.None, WorldEventHoldRules.DecidePose(true, true, 10, T0, T0 + 1, 0, false, 20, 3), "waiting for the rhythm");
            Assert.AreEqual(HoldPoseAction.FaceOutward, WorldEventHoldRules.DecidePose(true, false, 0, T0, 0, T0 - 3, false, 20, 3));
            Assert.AreEqual(HoldPoseAction.None, WorldEventHoldRules.DecidePose(true, false, 0, T0, 0, T0 - 1, false, 20, 3), "not yet");
            Assert.AreEqual(HoldPoseAction.None, WorldEventHoldRules.DecidePose(true, false, 0, T0, 0, 0, true, 20, 3), "already outward");
        }

        [TestMethod]
        public void NextDefendSwingAt_LaterOfAnimationAndRhythm()
        {
            Assert.AreEqual(T0 + 2, WorldEventHoldRules.NextDefendSwingAt(T0, 1, 0, 2, 4), 1e-9);
            Assert.AreEqual(T0 + 4, WorldEventHoldRules.NextDefendSwingAt(T0, 1, 1, 2, 4), 1e-9);
            Assert.AreEqual(T0 + 5, WorldEventHoldRules.NextDefendSwingAt(T0, 5, 0.5, 2, 4), 1e-9, "a long swing is not cut short");
            Assert.AreEqual(T0 + 0.5, WorldEventHoldRules.NextDefendSwingAt(T0, 0, 0, 0, 0), 1e-9, "a floor of half a second");

            Assert.IsTrue(WorldEventHoldRules.DefendSwingCandidates(MotionStance.SwordShieldCombat).Contains(MotionCommand.SlashHigh));
            Assert.IsTrue(WorldEventHoldRules.DefendSwingCandidates(MotionStance.HandCombat).Contains(MotionCommand.AttackHigh1));
            Assert.IsTrue(WorldEventHoldRules.DefendSwingCandidates(MotionStance.NonCombat).Count > 0);
        }

        // ---- the one heal path --------------------------------------------------------------------------------

        private static FakeHoldRunView Run(WorldEventHoldTuning tuning) => new FakeHoldRunView(9) { Tuning = tuning };

        private static Creature HurtKnight(IWorldEventHoldRunView run, double missingShare = 0.5)
        {
            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight, name: "Sir Donovan");
            knight.Health.Current = (uint)(knight.Health.MaxValue * (1 - missingShare));
            return knight;
        }

        [TestMethod]
        public void ApplyHoldKnightHeal_ShareHeal_FullRefused_NotAKnightNothing()
        {
            var run = Run(WorldEventHoldTuning.Defaults with { HealBudgetPerWave = 0, HealReductionAfterHit = 0 });
            var knight = HurtKnight(run);
            var max = knight.Health.MaxValue;
            var before = knight.Health.Current;

            var healed = knight.ApplyHoldKnightHeal(0.10, HoldHealSource.Shrine, T0, out var outcome);
            Assert.AreEqual(HoldHealOutcome.Healed, outcome);
            Assert.AreEqual((int)Math.Floor(0.10 * max), healed);
            Assert.AreEqual(before + (uint)healed, knight.Health.Current);

            knight.Health.Current = max;
            Assert.AreEqual(0, knight.ApplyHoldKnightHeal(0.10, HoldHealSource.Shrine, T0, out outcome));
            Assert.AreEqual(HoldHealOutcome.Full, outcome);

            var plain = HoldFixtures.MakeCreature();
            plain.Health.Current = plain.Health.MaxValue / 2;
            var plainBefore = plain.Health.Current;
            Assert.AreEqual(0, plain.ApplyHoldKnightHeal(0.10, HoldHealSource.Shrine, T0, out outcome));
            Assert.AreEqual(HoldHealOutcome.None, outcome);
            Assert.AreEqual(plainBefore, plain.Health.Current, "a creature with no tag is never healed here");

            var monster = HoldFixtures.Tagged(run, HoldUnitKind.Monster);
            monster.Health.Current = monster.Health.MaxValue / 2;
            Assert.AreEqual(0, monster.ApplyHoldKnightHeal(0.10, HoldHealSource.Shrine, T0, out _), "a wave monster is not a knight");
        }

        [TestMethod]
        public void ApplyHoldKnightHeal_AfterAHit_Reduced()
        {
            var run = Run(WorldEventHoldTuning.Defaults with { HealBudgetPerWave = 0, HealReductionAfterHit = 0.5, HealReductionSeconds = 5 });
            var knight = HurtKnight(run);
            var max = knight.Health.MaxValue;

            knight.HoldState.LastMonsterHitAt = T0 - 2;
            Assert.AreEqual((int)Math.Floor(0.10 * max * 0.5), knight.ApplyHoldKnightHeal(0.10, HoldHealSource.Shrine, T0, out _), "inside the window");
            Assert.AreEqual((int)Math.Floor(0.10 * max), knight.ApplyHoldKnightHeal(0.10, HoldHealSource.Shrine, T0 + 10, out _), "after it");
        }

        [TestMethod]
        public void ApplyHoldKnightHeal_BudgetPerWave_AllSourcesTogether_NewWaveResets()
        {
            var run = Run(WorldEventHoldTuning.Defaults with { HealBudgetPerWave = 0.15, HealReductionAfterHit = 0, PlayerHealEnabled = true, PlayerHealCap = 1.0, PlayerHealMult = 1.0 });
            var knight = HurtKnight(run, 0.9);
            var max = knight.Health.MaxValue;
            var budget = (int)Math.Floor(0.15 * max);

            var a = knight.ApplyHoldKnightHeal(0.10, HoldHealSource.Shrine, T0, out _);
            var b = knight.ApplyHoldKnightHeal(200, HoldHealSource.Player, T0, out _); // asks 8%, only 5% is left
            Assert.AreEqual(budget, a + b, "a share heal and a player heal draw on one budget");
            Assert.AreEqual(budget, knight.HoldHealSpentThisWave);

            Assert.AreEqual(0, knight.ApplyHoldKnightHeal(0.10, HoldHealSource.CorruptedKill, T0, out var outcome));
            Assert.AreEqual(HoldHealOutcome.BudgetSpent, outcome);

            run.WaveNumber = 2;
            Assert.AreEqual((int)Math.Floor(0.10 * max), knight.ApplyHoldKnightHeal(0.10, HoldHealSource.CorruptedKill, T0, out _), "a new wave, a new budget");
        }

        [TestMethod]
        public void ApplyHoldKnightHeal_Rescue_HeldToItsOwnCap()
        {
            var run = Run(WorldEventHoldTuning.Defaults with { HealBudgetPerWave = 0, HealReductionAfterHit = 0, RescueCapPerWave = 0.10 });
            var knight = HurtKnight(run, 0.9);
            var max = knight.Health.MaxValue;

            var first = knight.ApplyHoldKnightHeal(0.06, HoldHealSource.Rescue, T0, out _);
            var second = knight.ApplyHoldKnightHeal(0.06, HoldHealSource.Rescue, T0, out _);
            Assert.AreEqual((int)Math.Floor(0.06 * max), first);
            Assert.AreEqual((int)Math.Floor(0.10 * max) - first, second, "what is left of the rescue cap");
            Assert.AreEqual(0, knight.ApplyHoldKnightHeal(0.06, HoldHealSource.Rescue, T0, out _));
            Assert.IsTrue(knight.ApplyHoldKnightHeal(0.06, HoldHealSource.Shrine, T0, out _) > 0, "the rescue cap holds rescues only");
        }

        [TestMethod]
        public void ApplyHoldKnightHeal_PlayerHeal_ConvertedCappedAndRefusable()
        {
            var on = WorldEventHoldTuning.Defaults with { HealBudgetPerWave = 0, HealReductionAfterHit = 0, PlayerHealEnabled = true, PlayerHealCap = 0.06, PlayerHealMult = 1.0 };
            var run = Run(on);
            var knight = HurtKnight(run);
            var max = knight.Health.MaxValue;

            Assert.AreEqual((int)Math.Floor(max * 0.04), knight.ApplyHoldKnightHeal(100, HoldHealSource.Player, T0, out var outcome), "a typical heal is 4% of max health");
            Assert.AreEqual(HoldHealOutcome.Healed, outcome);
            knight.ApplyHoldKnightHeal(100, HoldHealSource.Player, T0, out _);
            Assert.AreEqual(0, knight.ApplyHoldKnightHeal(100, HoldHealSource.Player, T0, out outcome));
            Assert.AreEqual(HoldHealOutcome.Capped, outcome, "player_heal_cap per ten seconds");

            run.Tuning = on with { PlayerHealEnabled = false };
            Assert.AreEqual(0, knight.ApplyHoldKnightHeal(100, HoldHealSource.Player, T0 + 100, out outcome));
            Assert.AreEqual(HoldHealOutcome.Refused, outcome, "player_heal off");

            run.Tuning = on with { PlayerHealMult = 0 };
            Assert.AreEqual(0, knight.ApplyHoldKnightHeal(100, HoldHealSource.Player, T0 + 200, out outcome));
            Assert.AreEqual(HoldHealOutcome.Refused, outcome, "player_heal_mult 0");

            run.Tuning = on;
            run.IsActive = false;
            Assert.AreEqual(0, knight.ApplyHoldKnightHeal(0.10, HoldHealSource.Shrine, T0 + 300, out outcome));
            Assert.AreEqual(HoldHealOutcome.Refused, outcome, "the run is over");
        }

        // ---- threading: a player heal never writes the knight on the caller's thread --------------------------

        [TestMethod]
        public void PlayerHeal_IsQueuedOnTheKnightsLandblock_NeverWrittenSynchronously()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<(Creature Knight, Action Action)>();

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add((k, a)); return true; };

                var run = Run(WorldEventHoldTuning.Defaults with { HealBudgetPerWave = 0, HealReductionAfterHit = 0, PlayerHealEnabled = true, PlayerHealCap = 1.0, PlayerHealMult = 1.0 });
                var knight = HurtKnight(run);
                var before = knight.Health.Current;

                WorldEventHoldHeals.Result? seen = null;
                Assert.IsTrue(WorldEventHoldHeals.QueuePlayerHeal(knight, 100, r => seen = r));

                Assert.AreEqual(before, knight.Health.Current, "nothing is written on the caller's thread");
                Assert.IsNull(seen, "nor is the reply sent from it");
                Assert.AreEqual(0, knight.HoldHealSpentThisWave);
                Assert.AreEqual(1, queued.Count);
                Assert.AreSame(knight, queued[0].Knight, "queued on the KNIGHT, not the healer");

                // The hook path too: no healer (a Player cannot be built here), the same queue.
                Assert.IsTrue(WorldEventHooks.TryQueuePlayerHeal(null, knight, 100, false, null, ChatMessageType.Magic));
                Assert.AreEqual(before, knight.Health.Current);
                Assert.AreEqual(2, queued.Count);

                // Now the knight's landblock runs the action: the write and the reply happen there.
                queued[0].Action();
                Assert.IsNotNull(seen);
                Assert.AreEqual(HoldHealOutcome.Healed, seen.Value.Outcome);
                Assert.AreEqual(before + (uint)seen.Value.Healed, knight.Health.Current);

                // The full-health refusal is decided in the queued action too, not at queue time.
                knight.Health.Current = knight.Health.MaxValue;
                seen = null;
                Assert.IsTrue(WorldEventHoldHeals.QueuePlayerHeal(knight, 100, r => seen = r), "a full knight is still queued");
                Assert.IsNull(seen);
                queued[queued.Count - 1].Action();
                Assert.AreEqual(HoldHealOutcome.Full, seen.Value.Outcome);
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        [TestMethod]
        public void Queue_LiveSeam_NoLandblock_QueuesNothing_AndNeverWrites()
        {
            var run = Run(WorldEventHoldTuning.Defaults with { HealBudgetPerWave = 0, PlayerHealEnabled = true, PlayerHealCap = 1.0 });
            var knight = HurtKnight(run);
            var before = knight.Health.Current;
            var called = false;

            Assert.IsNull(knight.CurrentLandblock, "sanity: a test creature has no landblock");
            Assert.IsFalse(WorldEventHoldHeals.Queue(knight, 0.10, HoldHealSource.Shrine, _ => called = true));
            Assert.IsFalse(WorldEventHoldHeals.QueuePlayerHeal(knight, 100, _ => called = true));
            Assert.AreEqual(before, knight.Health.Current);
            Assert.IsFalse(called);

            Assert.IsFalse(WorldEventHoldHeals.QueuePlayerHeal(HoldFixtures.MakeCreature(), 100, _ => called = true), "not a knight");
        }

        [TestMethod]
        public void ApplyHoldKnightHeal_ParallelHeals_BudgetNeverOverspent()
        {
            var run = Run(WorldEventHoldTuning.Defaults with { HealBudgetPerWave = 0.15, HealReductionAfterHit = 0 });
            var knight = HurtKnight(run, 0.9);
            var budget = (int)Math.Floor(0.15 * knight.Health.MaxValue);

            long healedA = 0, healedB = 0;
            var ta = System.Threading.Tasks.Task.Run(() => { for (var i = 0; i < 5000; i++) healedA += knight.ApplyHoldKnightHeal(0.0005, HoldHealSource.Regen, T0, out _); });
            var tb = System.Threading.Tasks.Task.Run(() => { for (var i = 0; i < 5000; i++) healedB += knight.ApplyHoldKnightHeal(0.0005, HoldHealSource.Regen, T0, out _); });
            System.Threading.Tasks.Task.WaitAll(ta, tb);

            Assert.IsTrue(healedA + healedB <= budget, $"healed {healedA + healedB} within the budget {budget}");
            Assert.AreEqual(healedA + healedB, (long)knight.HoldHealSpentThisWave, "the budget records exactly what was restored");
        }

        // ---- source pins (paths no unit test can drive: a Player cannot be built here) --------------------------

        private static string ServerFile(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldEvents", "WorldEventHooks.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "could not find Source/ACE.Server by walking up from the test output");

            return System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { dir.FullName, "Source", "ACE.Server" }.Concat(parts).ToArray()));
        }

        [TestMethod]
        public void HealKit_OnAFullKnight_RefusedBeforeTheKitIsSpent()
        {
            var src = ServerFile("WorldObjects", "Healer.cs");
            var start = src.IndexOf("private void HealHoldKnight(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);

            var peek = src.IndexOf("WeenieErrorWithString._IsAtFullHealth, knight.Name", start, StringComparison.Ordinal);
            var busy = src.IndexOf("healer.IsBusy = true;", start, StringComparison.Ordinal);
            var spend = src.IndexOf("UsesLeft--;", start, StringComparison.Ordinal);

            Assert.IsTrue(peek > start && peek < busy && busy < spend, "the full-health peek comes before the animation and the use");
        }

        [TestMethod]
        public void NonPlayerHealOnAKnight_RefusedBeforeTheVitalWrite()
        {
            var src = ServerFile("WorldObjects", "WorldObject_Magic.cs");
            var guard = src.IndexOf("player == null && ACE.Server.WorldEvents.WorldEventHooks.IsHoldKnight(targetCreature))", StringComparison.Ordinal);
            var write = src.IndexOf("boost = targetCreature.UpdateVitalDelta(targetCreature.Health, tryBoost);", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0 && write > guard, "the refusal sits before the health write");
            StringAssert.Contains(src.Substring(guard, 120), "return;");

            Assert.IsTrue(WorldEventHooks.IsHoldKnight(HoldFixtures.Tagged(Run(WorldEventHoldTuning.Defaults), HoldUnitKind.Knight)));
            Assert.IsFalse(WorldEventHooks.IsHoldKnight(HoldFixtures.MakeCreature()), "identity: an untagged target heals as before");
        }

        [TestMethod]
        public void OffensiveSpellMemo_ClearedWithTheSpellCaches()
        {
            var src = ServerFile("Command", "Handlers", "DeveloperContentCommands.cs");
            Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(src, @"MonsterSpellShapeCache\.Clear\(\);\s*Creature\.ClearHoldOffensiveSpellCache\(\);").Count,
                "both spell-cache clear paths drop the memo too");

            Creature.ClearHoldOffensiveSpellCache();
        }

        [TestMethod]
        public void ShrineHealCounts_BelowTwoPercentIsNothing()
        {
            Assert.IsFalse(WorldEventHoldRules.ShrineHealCounts(0, 10000, 0.15));
            Assert.IsFalse(WorldEventHoldRules.ShrineHealCounts(199, 10000, 0.15));
            Assert.IsTrue(WorldEventHoldRules.ShrineHealCounts(200, 10000, 0.15));
            Assert.IsTrue(WorldEventHoldRules.ShrineHealCounts(50, 10000, 0.01), "a low shrine_heal lowers the floor to half of it");
            Assert.IsFalse(WorldEventHoldRules.ShrineHealCounts(49, 10000, 0.01));
            Assert.IsFalse(WorldEventHoldRules.ShrineHealCounts(199, 10000, double.NaN), "an unusable shrine_heal keeps the 2% floor");
            Assert.AreEqual("Sir Donovan needs almost no restoring. The shrine holds its power for now.", WorldEventHoldRules.ShrineWastedLine("Sir Donovan", HoldHealOutcome.Healed));
        }

        // ---- the shrine, on a real run ------------------------------------------------------------------------

        private static WorldEventHoldRun ShrineRun(IDictionary<string, string> props)
        {
            var full = props.ToDictionary(kv => K.PropertyPrefix + kv.Key, kv => kv.Value);
            var run = new WorldEventHoldRun(42, g => WorldEventHoldSettingsResolver.Resolve(g, p => full.TryGetValue(p, out var v) ? v : null));
            run.Stage(null, 1, T0);
            run.SetWaveNumber(1);
            run.MarkActive();
            return run;
        }

        private static readonly Dictionary<string, string> ShrineOn = new Dictionary<string, string>
        {
            [K.Shrine] = WorldEventSettingsRegistry.On,
            [K.ShrineHeal] = "0.1",
            [K.ShrineCooldownSeconds] = "30",
            [K.ShrineBindRange] = "20",
            [K.HealBudgetPerWave] = "0",
        };

        [TestMethod]
        public void Shrine_HealsTheNearestKnight_OnItsLandblock_ThenRests()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<Action>();

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add(a); return true; };

                var run = ShrineRun(ShrineOn);
                Assert.IsTrue(run.Tuning.ShrineEnabled, "sanity: the shrine row resolved on");
                Assert.AreEqual(30.0, run.Tuning.ShrineCooldownSeconds);

                var near = HurtKnight(run);
                near.Location = HoldFixtures.At(10, 10);
                var far = HurtKnight(run);
                far.Location = HoldFixtures.At(10, 25);
                run.PublishKnights(new[] { near, far });

                var shrine = HoldFixtures.MakeCreature("Shrine of Restoration");
                shrine.Location = HoldFixtures.At(12, 10);
                var stranger = HoldFixtures.MakeCreature("Not a shrine");
                stranger.Location = HoldFixtures.At(12, 10);
                run.RegisterShrines(new[] { shrine });

                Assert.IsFalse(run.TryUseShrineCore(stranger, 1, "Roz", null, T0, out _), "an object the run did not register falls through");

                var nearBefore = near.Health.Current;
                var farBefore = far.Health.Current;

                Assert.IsTrue(run.TryUseShrineCore(shrine, 1, "Roz", null, T0, out var reply));
                Assert.IsNull(reply);
                Assert.AreEqual(1, queued.Count, "one knight, the nearest");
                Assert.AreEqual(nearBefore, near.Health.Current, "queued, not written on the user's thread");

                queued[0]();
                Assert.AreEqual(nearBefore + (uint)Math.Floor(0.1 * near.Health.MaxValue), near.Health.Current);
                Assert.AreEqual(farBefore, far.Health.Current);

                Assert.IsTrue(run.TryUseShrineCore(shrine, 2, "Kestrel", null, T0 + 10, out reply));
                Assert.AreEqual(WorldEventHoldRules.ShrineCooldownLine(20), reply, "the shrine rests");
                Assert.AreEqual(1, queued.Count);

                Assert.IsTrue(run.TryUseShrineCore(shrine, 2, "Kestrel", null, T0 + 31, out reply), "ready again");
                Assert.IsNull(reply);
                Assert.AreEqual(2, queued.Count);
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        [TestMethod]
        public void Shrine_RestoringNothing_GivesTheUseBack_FromTheKnightsLandblock()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<Action>();

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add(a); return true; };

                var run = ShrineRun(ShrineOn);
                var knight = HurtKnight(run);
                knight.Health.Current = knight.Health.MaxValue;
                knight.Location = HoldFixtures.At(10, 10);
                run.PublishKnights(new[] { knight });

                var shrine = HoldFixtures.MakeCreature("Shrine of Restoration");
                shrine.Location = HoldFixtures.At(12, 10);
                run.RegisterShrines(new[] { shrine });

                var told = new List<string>();
                Assert.IsTrue(run.TryUseShrineCore(shrine, 1, "Roz", told.Add, T0, out var reply));
                Assert.IsNull(reply);
                Assert.AreEqual(0, told.Count, "the wasted line waits for the knight's landblock");

                queued[0]();
                CollectionAssert.AreEqual(new[] { WorldEventHoldRules.ShrineWastedLine("Sir Donovan", HoldHealOutcome.Full) }, told);

                Assert.IsTrue(run.TryUseShrineCore(shrine, 1, "Roz", told.Add, T0 + 1, out reply));
                Assert.IsNull(reply, "the use was given back: no rest");
                Assert.AreEqual(2, queued.Count);
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        [TestMethod]
        public void Shrine_LowShrineHeal_StillRests_SecondUseRefused()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<Action>();

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add(a); return true; };

                var run = ShrineRun(new Dictionary<string, string>(ShrineOn) { [K.ShrineHeal] = "0.01" });
                Assert.AreEqual(0.01, run.Tuning.ShrineHeal, "sanity: shrine_heal resolved to 1%");

                var knight = HurtKnight(run);
                knight.Location = HoldFixtures.At(10, 10);
                run.PublishKnights(new[] { knight });

                var shrine = HoldFixtures.MakeCreature("Shrine of Restoration");
                shrine.Location = HoldFixtures.At(12, 10);
                run.RegisterShrines(new[] { shrine });

                var told = new List<string>();
                Assert.IsTrue(run.TryUseShrineCore(shrine, 1, "Roz", told.Add, T0, out var reply));
                Assert.IsNull(reply);
                queued[0]();
                Assert.AreEqual(0, told.Count, "a full 1% heal counts: nothing is given back");

                Assert.IsTrue(run.TryUseShrineCore(shrine, 2, "Kestrel", told.Add, T0 + 1, out reply));
                Assert.AreEqual(WorldEventHoldRules.ShrineCooldownLine(29), reply, "the shrine rests: a low shrine_heal does not make it free");
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        [TestMethod]
        public void ApplyHoldKnightHeal_PlayerHeal_UnusedBucketIsRefunded()
        {
            var run = Run(WorldEventHoldTuning.Defaults with { HealBudgetPerWave = 0, HealReductionAfterHit = 0, PlayerHealEnabled = true, PlayerHealCap = 0.06, PlayerHealMult = 1.0 });
            var knight = HurtKnight(run, 0.01);
            var max = knight.Health.MaxValue;
            var missing = (int)(max - knight.Health.Current);

            // Asks 4% but only 1% is missing: the other 3% goes back to the bucket.
            Assert.AreEqual(missing, knight.ApplyHoldKnightHeal(100, HoldHealSource.Player, T0, out _));

            knight.Health.Current = max / 2;
            Assert.AreEqual((int)Math.Floor(max * 0.04), knight.ApplyHoldKnightHeal(100, HoldHealSource.Player, T0, out var outcome),
                "the refunded points allow a full typical heal at the same instant");
            Assert.AreEqual(HoldHealOutcome.Healed, outcome);
        }

        [TestMethod]
        public void Shrine_AScratchHeal_IsGivenBack()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<Action>();

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add(a); return true; };

                var run = ShrineRun(ShrineOn);
                var knight = HurtKnight(run, 0.01);
                knight.Location = HoldFixtures.At(10, 10);
                run.PublishKnights(new[] { knight });

                var shrine = HoldFixtures.MakeCreature("Shrine of Restoration");
                shrine.Location = HoldFixtures.At(12, 10);
                run.RegisterShrines(new[] { shrine });

                var told = new List<string>();
                Assert.IsTrue(run.TryUseShrineCore(shrine, 1, "Roz", told.Add, T0, out _));
                var before = knight.Health.Current;
                queued[0]();

                Assert.IsTrue(knight.Health.Current > before, "sanity: the 1% was restored");
                CollectionAssert.AreEqual(new[] { WorldEventHoldRules.ShrineWastedLine("Sir Donovan", HoldHealOutcome.Healed) }, told);

                Assert.IsTrue(run.TryUseShrineCore(shrine, 1, "Roz", told.Add, T0 + 1, out var reply));
                Assert.IsNull(reply, "given back: the shrine is not resting");
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        [TestMethod]
        public void Shrine_HealsAll_OneFullOneUnreachable_GivesTheUseBack()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<Action>();

            try
            {
                var props = new Dictionary<string, string>(ShrineOn) { [K.ShrineHealsAll] = "true" };
                var run = ShrineRun(props);
                Assert.IsTrue(run.Tuning.ShrineHealsAll, "sanity: heals-all resolved on");

                var full = HurtKnight(run);
                full.Health.Current = full.Health.MaxValue;
                full.Location = HoldFixtures.At(10, 10);
                var unreachable = HurtKnight(run);
                unreachable.Location = HoldFixtures.At(11, 10);
                run.PublishKnights(new[] { full, unreachable });

                WorldEventHoldHeals.Enqueue = (k, a) =>
                {
                    if (k == unreachable)
                        return false; // no landblock

                    queued.Add(a);
                    return true;
                };

                var shrine = HoldFixtures.MakeCreature("Shrine of Restoration");
                shrine.Location = HoldFixtures.At(12, 10);
                run.RegisterShrines(new[] { shrine });

                var told = new List<string>();
                Assert.IsTrue(run.TryUseShrineCore(shrine, 1, "Roz", told.Add, T0, out var reply));
                Assert.IsNull(reply);
                Assert.AreEqual(1, queued.Count);

                queued[0]();
                Assert.AreEqual(1, told.Count, "the use is given back once");

                Assert.IsTrue(run.TryUseShrineCore(shrine, 1, "Roz", told.Add, T0 + 1, out reply));
                Assert.IsNull(reply, "the shrine's rest was cleared");
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        [TestMethod]
        public void Shrine_QuietWhenOff_NobodyWhenOutOfRange()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<Action>();

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add(a); return true; };

                var off = ShrineRun(new Dictionary<string, string> { [K.Shrine] = WorldEventSettingsRegistry.Off });
                var shrineA = HoldFixtures.MakeCreature("Shrine of Restoration");
                shrineA.Location = HoldFixtures.At(12, 10);
                off.RegisterShrines(new[] { shrineA });
                Assert.IsTrue(off.TryUseShrineCore(shrineA, 1, "Roz", null, T0, out var reply));
                Assert.AreEqual(WorldEventHoldRules.ShrineQuietLine, reply);

                var run = ShrineRun(ShrineOn);
                var knight = HurtKnight(run);
                knight.Location = HoldFixtures.At(100, 100);
                run.PublishKnights(new[] { knight });
                var shrineB = HoldFixtures.MakeCreature("Shrine of Restoration");
                shrineB.Location = HoldFixtures.At(12, 10);
                run.RegisterShrines(new[] { shrineB });

                Assert.IsTrue(run.TryUseShrineCore(shrineB, 1, "Roz", null, T0, out reply));
                Assert.AreEqual(WorldEventHoldRules.ShrineNobodyLine, reply);
                Assert.AreEqual(0, queued.Count);

                run.MarkInactive();
                Assert.IsTrue(run.TryUseShrineCore(shrineB, 1, "Roz", null, T0, out reply));
                Assert.AreEqual(WorldEventHoldRules.ShrineQuietLine, reply, "the run is over");
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }
    }
}
