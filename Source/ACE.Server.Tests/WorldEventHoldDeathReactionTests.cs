using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Prod 2026-11-17: HoldObjective.OnCreatureDied threw a NullReferenceException on 89 of 94 deaths because
    /// DamageHistoryInfo.TryGetPetOwner() dereferenced a null PetOwner for every non-pet killer, which swallowed the run's
    /// death reactions (corrupted-kill heal, rescue heal, knight last words). These pin the consumer: the heals a player's
    /// kill must queue, through the WorldEventHoldHeals.Enqueue seam (restored in a finally). No PropertyManager, no Player.
    /// </summary>
    [TestClass]
    public class WorldEventHoldDeathReactionTests
    {
        private const double T0 = 1_000_000.0;

        private static WorldObject PlayerRangeObject(string name)
            => new GenericObject(new Weenie
            {
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            }, new ObjectGuid(0x50000010u));

        private static Func<ACE.Server.WorldEvents.Defs.GoalDef, WorldEventHoldSettings> Resolver(Dictionary<string, string> props)
        {
            var full = props.ToDictionary(kv => K.PropertyPrefix + kv.Key, kv => kv.Value);

            return g => WorldEventHoldSettingsResolver.Resolve(g, p => full.TryGetValue(p, out var v) ? v : null);
        }

        [TestMethod]
        public void TryGetPetOwner_is_null_and_does_not_throw_for_a_non_pet_attacker()
        {
            var info = new DamageHistoryInfo(PlayerRangeObject("Killer"));

            Assert.IsNull(info.PetOwner, "precondition: only a CombatPet attacker carries a PetOwner");
            Assert.IsNull(info.TryGetPetOwner());
        }

        [TestMethod]
        public void KillerHasLiveOwner_is_false_for_null_and_for_a_non_pet_killer()
        {
            // A CombatPet with a live owner needs a constructed Player, which this harness cannot build (see
            // DamageHistoryInfoTests' class remarks), so that branch is not driven here.
            Assert.IsFalse(HoldObjective.KillerHasLiveOwner(null));
            Assert.IsFalse(HoldObjective.KillerHasLiveOwner(new DamageHistoryInfo(PlayerRangeObject("Killer"))));
        }

        [TestMethod]
        public void A_knights_last_words_reach_the_world_line_through_HoldObjective_on_a_player_kill()
        {
            var goal = new ACE.Server.WorldEvents.Defs.GoalDef
            {
                Id = "hold_test",
                DisplayName = "Hold Test",
                Type = "Hold",
                TypeKind = ACE.Server.WorldEvents.Defs.GoalType.Hold,
                HoldSeconds = 600,
                Protect = new ACE.Server.WorldEvents.Defs.ProtectDef
                {
                    Noun = "envoy",
                    NounPlural = "envoys",
                    Lines = new ACE.Server.WorldEvents.Defs.ProtectLinesDef { Destroyed = new List<string> { "Tell them I held." } },
                },
            };

            var run = new WorldEventHoldRun(43, Resolver(new Dictionary<string, string>()));
            run.Stage(goal, 1, T0);
            run.MarkActive();

            var voice = new RecordingVoice();
            run.AttachVoice(voice);

            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight, name: "Envoy Donovan");
            run.PublishKnights(new[] { knight });

            var objective = new HoldObjective(goal, run, () => T0, null, null);
            var killer = new DamageHistoryInfo(PlayerRangeObject("Hero"));

            objective.OnCreatureDied(knight, killer, killer);
            run.TickRuntime(T0 + 1, 0);

            Assert.IsTrue(voice.Lines.Any(l => l.Contains("Envoy Donovan") && l.Contains("Tell them I held.")),
                "the knight's last words are announced world-wide on the next tick; announced: " + string.Join(" | ", voice.Lines));
        }

        private sealed class RecordingVoice : IWorldEventHoldVoice
        {
            public readonly List<string> Lines = new List<string>();
            public string AnchorName => "Anchor";
            public string FamilyDisplayName => "Family";
            public void Announce(string line) => Lines.Add(line);
            public void AnnounceLocal(string line) => Lines.Add(line);
        }

        [TestMethod]
        public void Player_kill_of_a_corrupted_monster_heals_the_knights_through_HoldObjective()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<(Creature Knight, Action Act)>();

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add((k, a)); return true; };

                var props = new Dictionary<string, string> { [K.CorruptedKillHeal] = "0.1", [K.RescueHeal] = "0.05" };
                var run = new WorldEventHoldRun(42, Resolver(props));
                run.Stage(null, 1, T0);
                run.MarkActive();

                var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight, name: "Envoy Donovan");
                run.PublishKnights(new[] { knight });

                var corrupted = HoldFixtures.Tagged(run, HoldUnitKind.Monster, corrupted: true, name: "Corrupted Thing");
                var rescued = HoldFixtures.Tagged(run, HoldUnitKind.Monster, name: "Plain Thing");
                rescued.AttackTarget = knight;

                var objective = new HoldObjective(null, run, () => T0, null, null);
                var killer = new DamageHistoryInfo(PlayerRangeObject("Hero"));

                objective.OnCreatureDied(corrupted, killer, killer);

                Assert.AreEqual(1, queued.Count(q => ReferenceEquals(q.Knight, knight)),
                    "a player's kill of a corrupted monster queues the corrupted-kill heal on the standing knight");

                queued.Clear();
                objective.OnCreatureDied(rescued, killer, killer);

                Assert.AreEqual(1, queued.Count(q => ReferenceEquals(q.Knight, knight)),
                    "a player's kill of a monster that was attacking a knight queues the rescue heal");
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }
    }
}
