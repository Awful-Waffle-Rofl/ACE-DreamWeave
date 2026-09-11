using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class ThreadDungeonGemRulesTests
    {
        private static DungeonGemSpec Unbound() => new DungeonGemSpec("any", 100, 6, "any", 1, new (string, double)[0], 0, 0);
        private static DungeonGemSpec Bound(uint owner = 0x50000001u) => Unbound().WithBinding(0x80001234u, owner);

        [TestMethod]
        public void Disabled_refuses_everything()
        {
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(Unbound(), 0x50000001u, 3, false, false, 150, 150, out var r));
            StringAssert.Contains(r, "not enabled");
        }

        [TestMethod]
        public void Unbound_starts_a_run()
        {
            Assert.AreEqual(GemUseDecision.StartRun, DungeonGemRules.Decide(Unbound(), 0x50000001u, 3, false, true, 150, 150, out _));
        }

        [TestMethod]
        public void Bound_live_owner_with_entries_reenters()
        {
            Assert.AreEqual(GemUseDecision.ReEnter, DungeonGemRules.Decide(Bound(), 0x50000001u, 1, true, true, 150, 150, out _));
        }

        [TestMethod]
        public void Bound_live_owner_without_entries_is_refused()
        {
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(Bound(), 0x50000001u, 0, true, true, 150, 150, out var r));
            StringAssert.Contains(r, "entries");
        }

        [TestMethod]
        public void Bound_live_wrong_user_is_refused()
        {
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(Bound(), 0x50000002u, 3, true, true, 150, 150, out var r));
            StringAssert.Contains(r, "another");
        }

        [TestMethod]
        public void Bound_dead_run_is_consumed_whoever_holds_it()
        {
            Assert.AreEqual(GemUseDecision.ConsumeDeadGem, DungeonGemRules.Decide(Bound(), 0x50000002u, 3, false, true, 150, 150, out _));
            Assert.AreEqual(GemUseDecision.ConsumeDeadGem, DungeonGemRules.Decide(Bound(), 0x50000001u, 0, false, true, 150, 150, out _));
        }

        [TestMethod]
        public void Below_min_player_level_refuses_a_fresh_open_but_not_re_entry()
        {
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(Unbound(), 0x50000001u, 3, false, true, 149, 150, out var r1));
            StringAssert.Contains(r1, "The fragment does not answer you yet.");

            // Ruling P2-R21: a bound gem was already gated when it was opened, so re-entry is not gated
            // again - a player evicted by death or logged out at town must not be stranded outside their
            // own live run just because the tunable has since risen above their level.
            Assert.AreEqual(GemUseDecision.ReEnter, DungeonGemRules.Decide(Bound(), 0x50000001u, 1, true, true, 149, 150, out var r2));
            Assert.IsNull(r2);
        }

        [TestMethod]
        public void At_min_player_level_proceeds()
        {
            Assert.AreEqual(GemUseDecision.StartRun, DungeonGemRules.Decide(Unbound(), 0x50000001u, 3, false, true, 150, 150, out _));
            Assert.AreEqual(GemUseDecision.ReEnter, DungeonGemRules.Decide(Bound(), 0x50000001u, 1, true, true, 150, 150, out _));
        }

        [TestMethod]
        public void Min_player_level_zero_disables_the_gate()
        {
            Assert.AreEqual(GemUseDecision.StartRun, DungeonGemRules.Decide(Unbound(), 0x50000001u, 3, false, true, 0, 0, out _));
        }

        [TestMethod]
        public void Entries_decrement_to_zero_and_stop()
        {
            Assert.AreEqual(2, DungeonGemRules.EntriesAfterUse(3));
            Assert.AreEqual(0, DungeonGemRules.EntriesAfterUse(1));
            Assert.AreEqual(0, DungeonGemRules.EntriesAfterUse(0));
        }

        /// <summary>
        /// The relog case. EphemeralRealm.Accepts compares against Owner by REFERENCE and every login builds a
        /// new Player, so the rightful owner comes back unaccepted; re-entry must re-admit them rather than
        /// spend an entry on a teleport ValidateInstanceDestination would reroute to the public landblock.
        /// </summary>
        [TestMethod]
        public void ReEntry_readmits_the_owner_the_realm_no_longer_accepts()
        {
            Assert.AreEqual(ReEntryAdmission.Admit, DungeonGemRules.DecideReEntry(accepted: false, ownerMatches: true, out var r));
            Assert.IsNull(r);
        }

        [TestMethod]
        public void ReEntry_of_an_accepted_player_just_enters()
        {
            Assert.AreEqual(ReEntryAdmission.Enter, DungeonGemRules.DecideReEntry(accepted: true, ownerMatches: true, out var r1));
            Assert.IsNull(r1);

            // Still Enter, not Admit: the realm has already said yes (a fellowship member, say), so there is
            // nothing to re-admit and no reason to refuse.
            Assert.AreEqual(ReEntryAdmission.Enter, DungeonGemRules.DecideReEntry(accepted: true, ownerMatches: false, out var r2));
            Assert.IsNull(r2);
        }

        [TestMethod]
        public void ReEntry_refuses_a_non_owner_the_realm_does_not_accept()
        {
            Assert.AreEqual(ReEntryAdmission.Refuse, DungeonGemRules.DecideReEntry(accepted: false, ownerMatches: false, out var r));
            StringAssert.Contains(r, "another");
        }

        [TestMethod]
        public void RefusesFromInstance_allows_the_normal_open_world_case()
        {
            Assert.IsFalse(DungeonGemRules.RefusesFromInstance(Unbound(), 0, false, out var r));
            Assert.IsNull(r);
        }

        [TestMethod]
        public void RefusesFromInstance_reports_already_inside_over_any_other_reason()
        {
            var bound = Bound();
            Assert.IsTrue(DungeonGemRules.RefusesFromInstance(bound, 0x80001234u, true, out var r));
            StringAssert.Contains(r, "already inside");
        }

        [TestMethod]
        public void RefusesFromInstance_refuses_a_different_ephemeral_instance()
        {
            Assert.IsTrue(DungeonGemRules.RefusesFromInstance(Unbound(), 0x80009999u, true, out var r));
            StringAssert.Contains(r, "another instance");
        }

        private static ModifierDef FakeModifier(string id, string display, string monsterEffectKind, string target = "monster")
            => new ModifierDef { Id = id, Display = display, MonsterEffectKind = monsterEffectKind, Target = target };

        private static ModifierDef LookupFake(string id)
        {
            switch (id)
            {
                case "hardy": return FakeModifier("hardy", "Hardy", "health_mult");
                case "savage": return FakeModifier("savage", "Savage", "damage_rating");
                case "champions": return FakeModifier("champions", "Champions", "elite_share");
                case "vicious": return FakeModifier("vicious", "Vicious", "crit_damage_rating");
                case "vicious_boss": return FakeModifier("vicious_boss", "Vicious", "crit_damage_rating", target: "boss");
                default: return null;
            }
        }

        [TestMethod]
        public void ComposeLongDesc_renders_one_line_per_modifier()
        {
            var mods = new (string Id, double Magnitude)[] { ("hardy", 1.72), ("savage", 37.24), ("champions", 0.26) };
            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, mods, 0, 0);

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "Filo's Doom", 2, 3, LookupFake, null);

            var expected = string.Join("\n", new[]
            {
                "Dungeon: Filo's Doom",
                "Level: 100",
                "Loot Tier: 6",
                "Modifiers:",
                "Hardy: monsters have x1.72 health",
                "Savage: monsters attack with +37 damage rating, hitting harder",
                "Champions: 26% of the run's monsters are elites",
                // None of these three modifiers name a reward field, so ModifierDef's identity defaults
                // (rewardXpBase/rewardLumBase 1.0) leave both products at 1.0 - but the shipped 2.0 default
                // scale still totals x2.00 on Experience/Luminance (see the reward block on ComposeLongDesc),
                // so these two lines appear with no per-modifier attribution.
                "Experience: x2.00",
                "Luminance: x2.00",
                "Entries: 2 of 3",
                "Unbound. Use it to open a dungeon.",
            });

            Assert.AreEqual(expected, desc);
        }

        [TestMethod]
        public void ComposeLongDesc_renders_crit_damage_rating_for_monster_and_boss_targets()
        {
            var mods = new (string Id, double Magnitude)[] { ("vicious", 12.4), ("vicious_boss", 8.6) };
            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, mods, 0, 0);

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "Filo's Doom", 2, 3, LookupFake, null);

            var expected = string.Join("\n", new[]
            {
                "Dungeon: Filo's Doom",
                "Level: 100",
                "Loot Tier: 6",
                "Modifiers:",
                "Vicious: monsters have +12 critical damage rating, so their critical hits hurt more",
                "Vicious: the boss has +9 critical damage rating, so its critical hits hurt more",
                "Experience: x2.00",
                "Luminance: x2.00",
                "Entries: 2 of 3",
                "Unbound. Use it to open a dungeon.",
            });

            Assert.AreEqual(expected, desc);
        }

        [TestMethod]
        public void ComposeLongDesc_prints_modifiers_none_when_the_gem_has_no_modifiers()
        {
            var spec = new DungeonGemSpec("filos_doom", 50, 3, "any", 1, new (string, double)[0], 0, 0).WithBinding(0x80001234u, 0x50000001u);

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "Filo's Doom", 1, 3, LookupFake, null);

            var expected = string.Join("\n", new[]
            {
                "Dungeon: Filo's Doom",
                "Level: 50",
                "Loot Tier: 3",
                "Modifiers: none",
                "Experience: x2.00",
                "Luminance: x2.00",
                "Entries: 1 of 3",
                "Bound to Filo's Doom. It crumbles when the dungeon is cleared or its last entry is spent; a death after the clear leaves no way back in.",
            });

            Assert.AreEqual(expected, desc);
        }

        [TestMethod]
        public void ComposeLongDesc_prints_dungeon_any_for_an_unbound_any_gem()
        {
            var spec = new DungeonGemSpec("any", 10, 1, "any", 1, new (string, double)[0], 0, 0);

            // The caller-supplied name ("any dungeon", per ThreadDungeonCommands.HandleGive) must be
            // overridden to "Any" here - only ComposeLongDesc knows the gem is both unbound and a wildcard.
            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, LookupFake, null);

            var expected = string.Join("\n", new[]
            {
                "Dungeon: Any",
                "Level: 10",
                "Loot Tier: 1",
                "Modifiers: none",
                "Experience: x2.00",
                "Luminance: x2.00",
                "Entries: 3 of 3",
                "Unbound. Use it to open a dungeon.",
            });

            Assert.AreEqual(expected, desc);
        }
    }
}
