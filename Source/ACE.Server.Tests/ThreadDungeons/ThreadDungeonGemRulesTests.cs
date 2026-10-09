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

        /// <summary>Group Threads ruling R8: a member's unused key re-enters at zero entries; without the free entry it is refused.</summary>
        [TestMethod]
        public void Free_entry_lets_a_zero_entry_bound_gem_re_enter()
        {
            Assert.AreEqual(GemUseDecision.ReEnter, DungeonGemRules.Decide(Bound(), 0x50000001u, 0, true, true, 150, 150, out var free, freeEntry: true));
            Assert.IsNull(free);

            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(Bound(), 0x50000001u, 0, true, true, 150, 150, out var paid, freeEntry: false));
            Assert.AreEqual(DungeonGemRules.NoEntriesReason, paid);
            Assert.AreEqual("This gem has no entries left. It will crumble when the dungeon closes.", paid);

            // The legacy overload is the freeEntry: false form.
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(Bound(), 0x50000001u, 0, true, true, 150, 150, out var legacy));
            Assert.AreEqual(DungeonGemRules.NoEntriesReason, legacy);
        }

        /// <summary>The free entry waives only the entry count: every other refusal still applies.</summary>
        [TestMethod]
        public void Free_entry_does_not_bypass_any_other_rule()
        {
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(Bound(), 0x50000001u, 0, true, false, 150, 150, out var disabled, freeEntry: true));
            StringAssert.Contains(disabled, "not enabled");

            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(Bound(), 0x50000002u, 0, true, true, 150, 150, out var other, freeEntry: true));
            StringAssert.Contains(other, "another");

            Assert.AreEqual(GemUseDecision.ConsumeDeadGem, DungeonGemRules.Decide(Bound(), 0x50000001u, 0, false, true, 150, 150, out _, freeEntry: true));

            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(Unbound(), 0x50000001u, 3, false, true, 149, 150, out var level, freeEntry: true));
            StringAssert.Contains(level, "does not answer");
        }

        [TestMethod]
        public void Entry_charge_is_free_once_then_spends_and_refuses_at_zero()
        {
            Assert.AreEqual(DungeonGemRules.EntryCharge.Free, DungeonGemRules.DecideEntryCharge(true, 0));
            Assert.AreEqual(DungeonGemRules.EntryCharge.Free, DungeonGemRules.DecideEntryCharge(true, 3));
            Assert.AreEqual(DungeonGemRules.EntryCharge.Spend, DungeonGemRules.DecideEntryCharge(false, 1));
            Assert.AreEqual(DungeonGemRules.EntryCharge.NoEntries, DungeonGemRules.DecideEntryCharge(false, 0));
            Assert.AreEqual(DungeonGemRules.EntryCharge.NoEntries, DungeonGemRules.DecideEntryCharge(false, -1));
        }

        /// <summary>
        /// The free-entry latch end to end on a real roster: a member's first key use is free at zero entries, a second
        /// use before the presence sampler latched MemberEntered (so Decide still offers the free entry) is refused at
        /// the spend point rather than granted again, and the owner never gets a free entry.
        /// </summary>
        [TestMethod]
        public void A_key_gets_exactly_one_free_entry_and_the_owner_none()
        {
            const uint owner = 0x50000001u;
            const uint member = 0x50000002u;
            const uint key = 0x800000AAu;

            var seats = new[] { new RosterSeat(owner, "Owner", 7, 200), new RosterSeat(member, "Member", 8, 200) };
            var group = GroupScaling.Compute(2, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(0x80001234u, seats, 0x80000099u, Unbound(), dungeon, System.DateTime.UtcNow, System.TimeSpan.FromMinutes(180), group);
            run.SetMemberKey(member, key);

            var isKey = DungeonGemRules.IsKeyUse(run, member, key);
            Assert.IsTrue(isKey);
            Assert.IsFalse(DungeonGemRules.IsKeyUse(run, owner, 0x80000099u));

            var keySpec = Unbound().WithBinding(run.Instance, member);
            var offered = isKey && !run.MemberEntered(member);
            Assert.AreEqual(GemUseDecision.ReEnter, DungeonGemRules.Decide(keySpec, member, 0, DungeonGemRules.IsRunLiveForGem(run, key), true, 150, 150, out _, offered));
            Assert.AreEqual(DungeonGemRules.EntryCharge.Free, DungeonGemRules.DecideEntryCharge(isKey && run.TryClaimFreeKeyEntry(member), 0));

            // Second use, MemberEntered not yet latched: still offered by Decide, refused at the spend point.
            Assert.AreEqual(GemUseDecision.ReEnter, DungeonGemRules.Decide(keySpec, member, 0, true, true, 150, 150, out _, isKey && !run.MemberEntered(member)));
            Assert.AreEqual(DungeonGemRules.EntryCharge.NoEntries, DungeonGemRules.DecideEntryCharge(isKey && run.TryClaimFreeKeyEntry(member), 0));

            // With entries left, the second use spends one.
            Assert.AreEqual(DungeonGemRules.EntryCharge.Spend, DungeonGemRules.DecideEntryCharge(isKey && run.TryClaimFreeKeyEntry(member), 2));

            // Once latched, Decide no longer offers the free entry.
            run.MarkMemberEntered(member);
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(keySpec, member, 0, true, true, 150, 150, out _, isKey && !run.MemberEntered(member)));

            // The owner gem is never a key: no free entry, and the claim is refused for the owner anyway.
            Assert.IsFalse(run.TryClaimFreeKeyEntry(owner));
            Assert.AreEqual(DungeonGemRules.EntryCharge.Spend, DungeonGemRules.DecideEntryCharge(false, 3));
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
                // Level 100 is below the modifier curve's anchor (185), so every line prints the magnitude the
                // run applies at s = (100 / 185)^1.76 = 0.33867 (DungeonModifierLevelScale), not the rolled one:
                // hardy 1 + s x 0.72 = 1.244, savage 37.24 x s = 12.61, champions 0.15 + s x (0.26 - 0.15) = 0.187.
                "Hardy: monsters have x1.24 health",
                "Savage: monsters attack with +13 damage rating, hitting harder",
                "Champions: 19% of the run's monsters are elites",
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
                // Scaled by s = (100 / 185)^1.76 = 0.33867 (DungeonModifierLevelScale): 12.4 x s = 4.20 and
                // 8.6 x s = 2.91.
                "Vicious: monsters have +4 critical damage rating, so their critical hits hurt more",
                "Vicious: the boss has +3 critical damage rating, so its critical hits hurt more",
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

        /// <summary>
        /// Orchestrator ruling on Task 5 (R29): a GROUP run's owner gem and every member key say they die when the
        /// Thread closes; a solo gem keeps its clear-time line byte for byte, and the default is solo.
        /// </summary>
        [TestMethod]
        public void ComposeLongDesc_group_bound_line_replaces_only_the_crumble_sentence()
        {
            var spec = new DungeonGemSpec("filos_doom", 50, 3, "any", 1, new (string, double)[0], 0, 0).WithBinding(0x80001234u, 0x50000001u);

            var solo = ThreadDungeonGemHandler.ComposeLongDesc(spec, "Filo's Doom", 1, 3, LookupFake, null);
            var soloExplicit = ThreadDungeonGemHandler.ComposeLongDesc(spec, "Filo's Doom", 1, 3, LookupFake, null, groupRun: false);
            var group = ThreadDungeonGemHandler.ComposeLongDesc(spec, "Filo's Doom", 1, 3, LookupFake, null, groupRun: true);

            Assert.AreEqual(solo, soloExplicit, "groupRun defaults to the solo text");
            StringAssert.EndsWith(solo, "\nBound to Filo's Doom. It crumbles when the dungeon is cleared or its last entry is spent; a death after the clear leaves no way back in.");
            StringAssert.EndsWith(group, "\nBound to Filo's Doom. It crumbles when this Thread closes.");
            Assert.AreEqual("It crumbles when this Thread closes.", ThreadDungeonGemHandler.GroupBoundLineTail);

            // Every line but the last is identical.
            var soloLines = solo.Split('\n');
            var groupLines = group.Split('\n');
            Assert.AreEqual(soloLines.Length, groupLines.Length);
            for (var i = 0; i < soloLines.Length - 1; i++)
                Assert.AreEqual(soloLines[i], groupLines[i], $"line {i}");

            // Unbound gems and fragments are unaffected by the flag.
            var unbound = new DungeonGemSpec("filos_doom", 50, 3, "any", 1, new (string, double)[0], 0, 0);
            Assert.AreEqual(ThreadDungeonGemHandler.ComposeLongDesc(unbound, "Filo's Doom", 3, 3, LookupFake, null),
                ThreadDungeonGemHandler.ComposeLongDesc(unbound, "Filo's Doom", 3, 3, LookupFake, null, groupRun: true));

            var fragment = new DungeonGemSpec("filos_doom", 185, 7, "any", 0, new (string, double)[0], 0, 0);
            Assert.AreEqual(ThreadDungeonGemHandler.ComposeLongDesc(fragment, "Filo's Doom", 3, 3, LookupFake, null),
                ThreadDungeonGemHandler.ComposeLongDesc(fragment, "Filo's Doom", 3, 3, LookupFake, null, groupRun: true));
        }

        /// <summary>
        /// Orchestrator ruling on Task 5: a key's free first entry shows the owner's first-entry line; every paid entry
        /// shows the re-entry line. The owner's own first-entry text is unchanged.
        /// </summary>
        [TestMethod]
        public void Entry_message_uses_the_first_entry_line_only_for_a_free_entry()
        {
            const string first = "The Thread Gem opens a way into Filo's Doom. It will bring you back 3 more time(s) while the dungeon stands.";
            const string again = "You step back into Filo's Doom. Entries left: 2.";

            Assert.AreEqual(first, DungeonGemRules.FirstEntryMessage("Thread Gem", "Filo's Doom", 3));
            Assert.AreEqual(again, DungeonGemRules.ReEntryMessage("Filo's Doom", 2));

            Assert.AreEqual(first, DungeonGemRules.EntryMessage(DungeonGemRules.EntryCharge.Free, "Thread Gem", "Filo's Doom", 3));
            Assert.AreEqual(again, DungeonGemRules.EntryMessage(DungeonGemRules.EntryCharge.Spend, "Thread Gem", "Filo's Doom", 2));
            Assert.AreEqual("The Thread Gem opens a way into Filo's Doom. It will bring you back 0 more time(s) while the dungeon stands.",
                DungeonGemRules.EntryMessage(DungeonGemRules.EntryCharge.Free, "Thread Gem", "Filo's Doom", 0), "a free entry at zero entries");
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
