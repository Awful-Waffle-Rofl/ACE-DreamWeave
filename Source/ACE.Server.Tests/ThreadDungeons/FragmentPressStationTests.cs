using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The pure surface of the Fragment Press (PHASE-2-IMPLEMENTATION-PLAN.md task D5): the per-dose log
    /// line, the give gate's decision and its refusals, and the empty-load pressing. The Player branches -
    /// the kill switch, the Trade Note charge, the confirmation, the consume/EndRun ordering - need a live
    /// session and are covered by the live checks in D7b instead.
    ///
    /// Nothing here may reach PropertyManager: its reads throw in unit tests, which is why the press builds
    /// its PressLimits in FragmentPressStation.BuildLimits and every test below constructs one by hand.
    /// </summary>
    [TestClass]
    public class FragmentPressStationTests
    {
        private const string EmptyIndex = "{\"dungeons\":[]}";
        private const string EmptyBosses = "{\"bosses\":[]}";

        private const string ModifiersJson =
            "{\"modifiers\":[" +
            "{\"id\":\"savage\",\"display\":\"Savage\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40}," +
            "{\"id\":\"precise\",\"display\":\"Precise\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":30}" +
            "],\"xpLadder\":[]}";

        private const string AttunementJson =
            "{\"version\":1,\"colors\":[{\"color\":\"red\",\"modifier\":\"savage\"}],\"components\":[" +
            "{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"color\":\"red\",\"dose\":10,\"limit\":4,\"instability\":5," +
            "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}" +
            "],\"wild\":[{\"op\":\"nothing\",\"weight\":100}]}";

        private static ThreadDungeonStore Store()
            => ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, ModifiersJson, new Dictionary<string, string>(), attunementJson: AttunementJson);

        /// <summary>
        /// The shipped defaults, hand-built - PressLimits is the seam that keeps PropertyManager out of the
        /// core. aimChance is pinned at 1.0 rather than left at the shipped 0.5 for the same reason
        /// RawFragmentRulesTests pins it: the press test below asserts which modifier a named component
        /// produces, which is a statement about the aim landing. The 50/50 miss is covered there.
        /// </summary>
        private static PressLimits Limits()
            => new PressLimits(4, DungeonGemSpec.MaxLocks, RawFragmentRules.MaxEntries, DungeonGemSpec.MaxRunLevel,
                aimChance: 1.0);

        [TestMethod]
        public void ComposeInsufficientFundsMessage_computes_banked_total()
        {
            Assert.AreEqual(
                "You need 1 Trade Note (250,000), in your pack or as 250,000 banked pyreals, to use the Fragment Press.",
                FragmentPressStation.ComposeInsufficientFundsMessage(1));

            var twoNotes = FragmentPressStation.ComposeInsufficientFundsMessage(2);
            StringAssert.Contains(twoNotes, "2 Trade Notes");
            StringAssert.Contains(twoNotes, "500,000");
        }

        /// <summary>
        /// REWRITTEN from ComposePressedMessage_uses_the_steadiness_word, which asserted the four
        /// instability bands of RawFragmentRules.SteadinessWord. That function went with the mechanic (owner
        /// ruling, 2026-09-07), leaving the message with nothing to vary on, so the wording the calm band
        /// produced is now a fixed literal. Kept rather than deleted because the exact sentence is still
        /// player-facing text worth pinning.
        /// </summary>
        [TestMethod]
        public void ComposePressedMessage_is_a_fixed_line()
        {
            Assert.AreEqual("Pressed. It holds.", FragmentPressStation.ComposePressedMessage());
        }

        /// <summary>
        /// The live aim-chance tunable is sanitized before it reaches the pure core, so a shard operator
        /// cannot set it to a value that makes the aim roll meaningless. NaN in particular has to read as the
        /// default rather than being passed through: every comparison against NaN is false, so an unclamped
        /// NaN would silently mean "always miss" - the exact opposite of a disabled dial.
        /// </summary>
        [TestMethod]
        public void The_aim_chance_tunable_is_sanitized_before_it_reaches_the_core()
        {
            Assert.AreEqual(0.5, FragmentPressStation.SanitizeAimChance(0.5), 0.0001);
            Assert.AreEqual(0.0, FragmentPressStation.SanitizeAimChance(0.0), 0.0001, "0 is a legal setting, not a garbled one");
            Assert.AreEqual(1.0, FragmentPressStation.SanitizeAimChance(4.2), 0.0001);
            Assert.AreEqual(0.0, FragmentPressStation.SanitizeAimChance(-1.0), 0.0001);
            Assert.AreEqual(PressLimits.DefaultAimChance, FragmentPressStation.SanitizeAimChance(double.NaN), 0.0001);
        }

        [TestMethod]
        public void Dose_log_line_format()
        {
            var dose = new DoseLogEntry
            {
                Wcid = 1650u,
                Name = "Red Taper",
                Op = "add_or_raise",
                Before = "mods=",
                After = "mods=savage:10",
            };

            Assert.AreEqual("[DYNDUNGEON] press Frostfell Red Taper mods= -> mods=savage:10",
                FragmentPressStation.ComposeDoseLog("Frostfell", dose));
        }

        [TestMethod]
        public void Destroy_of_a_fragment_is_refused()
        {
            // A fragment is refused on its own, specific terms.
            var fragment = new DungeonGemSpec("any", 195, 7, "any", 0, new (string, double)[0], 0, 0);
            Assert.IsFalse(FragmentPressStation.CanDestroy(fragment, 3, 3, out var alreadyRefusal));
            Assert.AreEqual(FragmentPressStation.AlreadyAFragmentMessage, alreadyRefusal);

            // An unbound gem is the case the press actually takes.
            var unbound = new DungeonGemSpec("any", 195, 7, "any", 918273, new (string, double)[0], 0, 0);
            Assert.IsTrue(FragmentPressStation.CanDestroy(unbound, 3, 3, out var none));
            Assert.IsNull(none);
        }

        private const uint Owner = 0x50000001u;
        private const uint Stranger = 0x50000002u;
        private const uint Run = 0x80001234u;

        private static DungeonGemSpec BoundGem(uint ownerGuid)
            => new DungeonGemSpec("filos_doom", 195, 7, "any", 918273, new (string, double)[0], Run, ownerGuid);

        /// <summary>
        /// Someone else's bound gem is refused at the press in the SAME words a use of it gets
        /// (DungeonGemRules.Decide), whether or not the run behind it is still live.
        /// </summary>
        [TestMethod]
        public void Give_of_another_players_bound_gem_is_refused()
        {
            Assert.AreEqual(PressGiveDecision.Refuse,
                FragmentPressStation.DecideGive(BoundGem(Stranger), Owner, runIsLive: true, 2, 3, out var refusal));
            Assert.AreEqual("This gem is bound to another adventurer's dungeon.", refusal);
            Assert.AreEqual(FragmentPressStation.NotYourGemMessage, refusal);

            Assert.AreEqual(PressGiveDecision.Refuse,
                FragmentPressStation.DecideGive(BoundGem(Stranger), Owner, runIsLive: false, 2, 3, out var deadRefusal));
            Assert.AreEqual(FragmentPressStation.NotYourGemMessage, deadRefusal);
        }

        /// <summary>The fix this feature exists for: the owner's own LIVE run can be closed at the press.</summary>
        [TestMethod]
        public void Give_of_your_own_bound_gem_with_a_live_run_offers_the_close()
        {
            Assert.AreEqual(PressGiveDecision.CloseRun,
                FragmentPressStation.DecideGive(BoundGem(Owner), Owner, runIsLive: true, 2, 3, out var refusal));
            Assert.IsNull(refusal);

            // Zero entries left is still a closeable run - that is exactly the stranded case.
            Assert.AreEqual(PressGiveDecision.CloseRun,
                FragmentPressStation.DecideGive(BoundGem(Owner), Owner, runIsLive: true, 0, 3, out _));
        }

        /// <summary>
        /// A bound gem whose run is gone is just a spent gem: it is destroyed outright, rather than being
        /// refused as it was before this gesture existed.
        /// </summary>
        [TestMethod]
        public void Give_of_your_own_bound_gem_whose_run_is_dead_destroys()
        {
            var gem = BoundGem(Owner);

            Assert.AreEqual(PressGiveDecision.Destroy,
                FragmentPressStation.DecideGive(gem, Owner, runIsLive: false, 2, 3, out var refusal));
            Assert.IsNull(refusal);
        }

        /// <summary>
        /// REVERSAL of rulings R7/R32 (owner ruling, 2026-09-17): a member's key on a LIVE run now closes the run for
        /// the whole fellowship. A key spec is bound to the member, so DecideGive's owner check passes for its
        /// rightful holder and refuses anyone else; what changed is the liveness test feeding it, which is now the
        /// key-aware IsRunGem. A key on a DEAD run is still the ordinary destroy.
        /// </summary>
        [TestMethod]
        public void Give_of_a_member_key_closes_the_live_run_and_destroys_a_dead_ones_key()
        {
            const uint member = 0x50000003u;
            const uint ownerGem = 0x80000099u;
            const uint key = 0x800000AAu;

            var seats = new[] { new RosterSeat(Owner, "Owner", 7, 200), new RosterSeat(member, "Member", 8, 200) };
            var group = GroupScaling.Compute(2, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);
            var dungeon = new ACE.Server.ThreadDungeons.Defs.DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(Run, seats, ownerGem, BoundGem(Owner), dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
            run.SetMemberKey(member, key);

            // The press's own liveness expression (FragmentPressStation.TryGate, candidate.IsRunGem(item.Guid.Full)).
            var keyIsLive = run.IsRunGem(key);
            Assert.IsTrue(keyIsLive, "the live run knows the key, and that is now what liveness means at the press");
            Assert.IsTrue(run.IsRunGem(ownerGem), "the owner gem is live too");

            Assert.AreEqual(PressGiveDecision.CloseRun,
                FragmentPressStation.DecideGive(BoundGem(member), member, keyIsLive, 0, 3, out var refusal));
            Assert.IsNull(refusal);

            // A key whose run is gone is just a spent gem, exactly as a dead owner gem is.
            Assert.AreEqual(PressGiveDecision.Destroy,
                FragmentPressStation.DecideGive(BoundGem(member), member, runIsLive: false, 0, 3, out var deadRefusal));
            Assert.IsNull(deadRefusal);

            // Someone else handing in the member's key is still refused, live or dead.
            Assert.AreEqual(PressGiveDecision.Refuse,
                FragmentPressStation.DecideGive(BoundGem(member), Owner, keyIsLive, 0, 3, out var notYours));
            Assert.AreEqual(FragmentPressStation.NotYourGemMessage, notYours);

            Assert.AreEqual(PressGiveDecision.Refuse,
                FragmentPressStation.DecideGive(BoundGem(member), Stranger, runIsLive: false, 0, 3, out var notYoursDead));
            Assert.AreEqual(FragmentPressStation.NotYourGemMessage, notYoursDead);
        }

        /// <summary>
        /// The belt-and-braces check in HandleDestroyConfirm, driven against a real run: it asks whether this exact
        /// item is this exact player's own gem ON THIS RUN, which is the owner gem for the owner and that member's
        /// recorded key for a member. A bare run.OwnerGuid comparison would refuse every member close.
        /// </summary>
        [TestMethod]
        public void The_close_owner_check_accepts_the_owner_gem_and_the_holders_own_key_and_nothing_else()
        {
            const uint member = 0x50000003u;
            const uint member2 = 0x50000004u;
            const uint ownerGem = 0x80000099u;
            const uint key = 0x800000AAu;
            const uint key2 = 0x800000BBu;

            var seats = new[] { new RosterSeat(Owner, "Owner", 7, 200), new RosterSeat(member, "Member", 8, 200), new RosterSeat(member2, "Member2", 9, 200) };
            var group = GroupScaling.Compute(3, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);
            var dungeon = new ACE.Server.ThreadDungeons.Defs.DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(Run, seats, ownerGem, BoundGem(Owner), dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
            run.SetMemberKey(member, key);
            run.SetMemberKey(member2, key2);

            // The expression HandleDestroyConfirm evaluates, spelled out here because it needs a Player there.
            Func<uint, uint, bool> claims = (playerGuid, itemGuid)
                => run.GemGuid == itemGuid ? run.OwnerGuid == playerGuid : run.MemberKeyGem(playerGuid) == itemGuid;

            Assert.IsTrue(claims(Owner, ownerGem), "the owner closes with their own gem");
            Assert.IsTrue(claims(member, key), "a member closes with their own key");
            Assert.IsTrue(claims(member2, key2));

            Assert.IsFalse(claims(member, ownerGem), "a member cannot close with the owner gem");
            Assert.IsFalse(claims(member, key2), "nor with another member's key");
            Assert.IsFalse(claims(Owner, key), "nor the owner with a member's key");
            Assert.IsFalse(claims(Stranger, key), "nor a non-member with anything");
            Assert.IsFalse(claims(member, 0x800000CCu), "nor with a gem the run never recorded");
        }

        /// <summary>
        /// A member key on a CLEARED run still closes it, and the prompt now says what that throws away.
        ///
        /// The decision is deliberately state-blind: neither IsRunGem nor DecideGive looks at run.State, so a run
        /// sitting in its group loot-hold window closes exactly as an Active one does. That is the owner's ruling and
        /// it is NOT narrowed here. What was missing is the warning: EndRun -> ThreadLootPool.DisposeForRunEnd drains
        /// the ledger, the pending bonus, the overflow and every member's undelivered pile for the WHOLE roster, and
        /// the prompt said only that the way would close.
        /// </summary>
        [TestMethod]
        public void A_member_key_still_closes_a_cleared_run_and_the_prompt_names_the_loot_it_forfeits()
        {
            const uint member = 0x50000003u;
            const uint ownerGem = 0x80000099u;
            const uint key = 0x800000AAu;

            var seats = new[] { new RosterSeat(Owner, "Owner", 7, 200), new RosterSeat(member, "Member", 8, 200) };
            var group = GroupScaling.Compute(2, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);
            var dungeon = new ACE.Server.ThreadDungeons.Defs.DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(Run, seats, ownerGem, BoundGem(Owner), dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
            run.SetMemberKey(member, key);
            run.MarkPooledLoot(true);

            run.MarkPopulated(0, 0, 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "precondition: a cleared run held open for its loot");
            Assert.IsFalse(run.HasUnclaimedLoot, "precondition: nothing owed yet");

            Assert.IsTrue(run.IsRunGem(key), "liveness is state-blind, so the key is still one of the run's gems");
            Assert.AreEqual(PressGiveDecision.CloseRun,
                FragmentPressStation.DecideGive(BoundGem(member), member, run.IsRunGem(key), 1, 3, out var refusal),
                "the ruling stands: a member can close a cleared run too");
            Assert.IsNull(refusal);

            // Nothing owed: no warning on either prompt.
            var quietMember = FragmentPressStation.ComposeMemberCloseConfirm("Filo's Doom", 1, 3, unclaimedLoot: false);
            var quietOwner = FragmentPressStation.ComposeCloseConfirm("Filo's Doom", 1, 3, unclaimedLoot: false);
            Assert.IsFalse(quietMember.Contains(FragmentPressStation.UnclaimedLootWarning));
            Assert.IsFalse(quietOwner.Contains(FragmentPressStation.UnclaimedLootWarning));
            Assert.AreEqual(FragmentPressStation.ComposeMemberCloseConfirm("Filo's Doom", 1, 3), quietMember, "the default is no warning");
            Assert.AreEqual(FragmentPressStation.ComposeCloseConfirm("Filo's Doom", 1, 3), quietOwner);

            // Now the hold owes something. HasUnclaimedLoot is what the give path reads, and it is a pure read.
            Assert.IsTrue(run.TryMarkLootBonusPending());
            Assert.IsTrue(run.HasUnclaimedLoot, "a pending bonus is unclaimed loot the close would destroy");
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "asking the question did not change the run");

            var loudMember = FragmentPressStation.ComposeMemberCloseConfirm("Filo's Doom", 1, 3, unclaimedLoot: true);
            var loudOwner = FragmentPressStation.ComposeCloseConfirm("Filo's Doom", 1, 3, unclaimedLoot: true);

            StringAssert.Contains(loudMember, FragmentPressStation.UnclaimedLootWarning);
            StringAssert.Contains(loudOwner, FragmentPressStation.UnclaimedLootWarning, "the owner had this hazard too and was equally untold");
            StringAssert.StartsWith(loudMember, quietMember, "the warning is appended, it does not replace anything");
            StringAssert.StartsWith(loudOwner, quietOwner);

            Assert.AreEqual("Loot from this Thread is still waiting to be collected, and closing forfeits it.",
                FragmentPressStation.UnclaimedLootWarning);
        }

        /// <summary>
        /// The give path asks the run-wide question, from the existing read-only checks, and hands the answer to both
        /// prompts. Run-wide and not per-member on purpose: a close discards what the whole roster is owed.
        /// </summary>
        [TestMethod]
        public void The_give_path_reads_the_existing_unclaimed_loot_checks_without_draining_anything()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/FragmentPressStation.cs");

            // An expression-bodied helper, so the whole file is the unit here rather than a method body.
            StringAssert.Contains(source,
                "private static bool HasUnclaimedLoot(ThreadDungeonRun run)\n"
                + "            => run != null && (run.HasUnclaimedLoot || ThreadLootPool.AnyCacheHoldsItems(run));");

            var give = PooledLootSourceText.MethodBody(source, "public static void HandleGive(");

            StringAssert.Contains(give, "var unclaimedLoot = decision == PressGiveDecision.CloseRun && HasUnclaimedLoot(run);");
            StringAssert.Contains(give, "ComposeCloseConfirm(dungeonName, entries, maxEntries, unclaimedLoot)");
            StringAssert.Contains(give, "ComposeMemberCloseConfirm(dungeonName, entries, maxEntries, unclaimedLoot)");

            Assert.IsFalse(give.Contains("Drain"), "the give never drains the pool to find out what is in it");
            Assert.IsFalse(give.Contains("TryClaim"), "nor claims from it");

            // The run-wide question, not the per-member one: a close discards what the whole roster is owed. Comment
            // lines are stripped, because the helper's doc comment names the per-member variants to say why it does
            // not use them.
            var code = string.Join("\n", source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                && !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));
            Assert.IsFalse(code.Contains("HasUnclaimedLootFor"), "the run-wide question, not the per-member one");
            Assert.IsFalse(code.Contains("AnyCacheHoldsItemsFor"));
        }

        /// <summary>The line the rest of the roster gets, and the member's own two lines, which differ from the owner's.</summary>
        [TestMethod]
        public void A_member_close_says_it_closed_for_everyone_and_names_the_closer_to_the_rest()
        {
            Assert.AreEqual("Ann closed the way into Filo's Doom.", FragmentPressStation.ComposeClosedByText("Ann", "Filo's Doom"));

            Assert.AreEqual("The press grinds your key to dust and the way closes for your whole fellowship.",
                FragmentPressStation.MemberClosedMessage);

            Assert.AreEqual(
                "Destroy your key and close Filo's Doom for your WHOLE FELLOWSHIP? Everyone inside is turned out, your key has 2 of 3 entries left, and nothing comes back.",
                FragmentPressStation.ComposeMemberCloseConfirm("Filo's Doom", 2, 3));

            Assert.AreNotEqual(FragmentPressStation.ClosedMessage, FragmentPressStation.MemberClosedMessage,
                "the two closes do not say the same thing, because they do not do the same thing");
            Assert.AreNotEqual(FragmentPressStation.ComposeCloseConfirm("Filo's Doom", 2, 3), FragmentPressStation.ComposeMemberCloseConfirm("Filo's Doom", 2, 3));
        }

        /// <summary>
        /// ReleaseMemberKey itself, which since the 2026-09-17 reversal is reached only on the DEAD-run destroy: it
        /// clears that member's key record and never touches the run. Nothing else releases a record - the owner gem,
        /// someone else's key, a stale key guid, a null run and a zero guid all change nothing. The deal-seat
        /// assertions below are what the record still drives for a run that is Cleared but held for its loot.
        /// </summary>
        [TestMethod]
        public void A_key_given_to_the_press_clears_the_member_key_record_while_the_run_continues()
        {
            const uint member = 0x50000003u;
            const uint member2 = 0x50000004u;
            const uint ownerGem = 0x80000099u;
            const uint key = 0x800000AAu;
            const uint key2 = 0x800000BBu;

            var seats = new[] { new RosterSeat(Owner, "Owner", 7, 200), new RosterSeat(member, "Member", 8, 200), new RosterSeat(member2, "Member2", 9, 200) };
            var group = GroupScaling.Compute(3, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);
            var dungeon = new ACE.Server.ThreadDungeons.Defs.DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(Run, seats, ownerGem, BoundGem(Owner), dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
            run.SetMemberKey(member, key);
            run.SetMemberKey(member2, key2);

            Assert.IsFalse(FragmentPressStation.ReleaseMemberKey(null, member, key), "no run");
            Assert.IsFalse(FragmentPressStation.ReleaseMemberKey(run, member, 0), "a zero guid");
            Assert.IsFalse(FragmentPressStation.ReleaseMemberKey(run, Owner, ownerGem), "the owner gem is not a key record");
            Assert.IsFalse(FragmentPressStation.ReleaseMemberKey(run, member, key2), "someone else's key");
            Assert.IsFalse(FragmentPressStation.ReleaseMemberKey(run, Stranger, key), "a non-member");
            Assert.AreEqual(key, run.MemberKeyGem(member));
            CollectionAssert.AreEqual(new[] { Owner, member, member2 }, run.DealSeats().ToArray());

            Assert.IsTrue(FragmentPressStation.ReleaseMemberKey(run, member, key));
            Assert.AreEqual(0u, run.MemberKeyGem(member), "the record is cleared");
            Assert.IsFalse(run.IsRunGem(key), "the destroyed key no longer belongs to the run");
            CollectionAssert.AreEqual(new[] { Owner, member2 }, run.DealSeats().ToArray(), "the member loses their deal seat");
            Assert.AreEqual(key2, run.MemberKeyGem(member2), "other keys untouched");
            Assert.AreNotEqual(ThreadDungeonRunState.Ended, run.State, "the run continues");
            Assert.IsFalse(FragmentPressStation.ReleaseMemberKey(run, member, key), "released once");
        }

        /// <summary>The release runs on the destroy branch only, after the consume succeeded, and before the success line.</summary>
        [TestMethod]
        public void The_press_releases_a_member_key_after_the_destroy_consume()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/FragmentPressStation.cs");
            var body = PooledLootSourceText.MethodBody(source, "private static void HandleDestroyConfirm(");

            var branch = body.IndexOf("if (decision != PressGiveDecision.CloseRun)", StringComparison.Ordinal);
            var consume = body.IndexOf("if (!player.TryConsumeFromInventoryWithNetworking(item))", branch, StringComparison.Ordinal);
            var release = body.IndexOf("ReleaseMemberKey(spec.IsBound ? ThreadDungeonManager.GetRun(spec.RunId) : null, player.Guid.Full, itemGuid)", StringComparison.Ordinal);
            var said = body.IndexOf("Say(player, DestroyedMessage);", StringComparison.Ordinal);
            var closeRun = body.IndexOf("ThreadDungeonManager.EndRun(run,", StringComparison.Ordinal);

            Assert.IsTrue(branch >= 0 && branch < consume && consume < release && release < said && said < closeRun);
            Assert.AreEqual(1, body.Split(new[] { "ReleaseMemberKey(" }, StringSplitOptions.None).Length - 1, "one release site");
        }

        /// <summary>
        /// The reversal, pinned where it lives: the press's live-run test is the key-aware IsRunGem, so a member key
        /// resolves to CloseRun. Comment lines are stripped before the negative check, because the comment above the
        /// line quotes the owner-gem-only test it replaced.
        /// </summary>
        [TestMethod]
        public void Press_liveness_accepts_the_owner_gem_or_any_member_key()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/FragmentPressStation.cs");
            var body = PooledLootSourceText.MethodBody(source, "private static bool TryGate(");

            StringAssert.Contains(body, "var runIsLive = candidate != null && candidate.IsRunGem(item.Guid.Full);");

            var code = string.Join("\n", body.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.IsFalse(code.Contains("candidate.GemGuid =="), "the owner-gem-only liveness test is gone");
        }

        /// <summary>
        /// The close half of HandleDestroyConfirm, in order: the ownership check, the consume, the roster notice, then
        /// EndRun with the reason that matches who closed it. The notice must precede EndRun, or the attribution
        /// arrives after the eviction it explains.
        /// </summary>
        [TestMethod]
        public void The_close_notifies_the_roster_before_ending_the_run_and_files_the_right_reason()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/FragmentPressStation.cs");
            var body = PooledLootSourceText.MethodBody(source, "private static void HandleDestroyConfirm(");

            var claim = body.IndexOf("var claimed = ownerClose ? run.OwnerGuid == player.Guid.Full : run.MemberKeyGem(player.Guid.Full) == itemGuid;", StringComparison.Ordinal);
            var refusal = body.IndexOf("Say(player, NotYourGemMessage);", StringComparison.Ordinal);
            var consume = body.IndexOf("if (!player.TryConsumeFromInventoryWithNetworking(item))", claim < 0 ? 0 : claim, StringComparison.Ordinal);
            var notify = body.IndexOf("NotifyRosterOfClose(run, player,", StringComparison.Ordinal);
            var endRun = body.IndexOf("ThreadDungeonManager.EndRun(run, ownerClose ? DungeonRunTelemetry.EndReasons.ClosedByOwner : DungeonRunTelemetry.EndReasons.ClosedByMember);", StringComparison.Ordinal);
            var said = body.IndexOf("Say(player, ownerClose ? ClosedMessage : MemberClosedMessage);", StringComparison.Ordinal);

            Assert.IsTrue(claim >= 0, "the ownership claim is computed");
            Assert.IsTrue(refusal > claim, "and refused before anything is touched");
            Assert.IsTrue(consume > claim && notify > consume && endRun > notify && said > endRun,
                "claim, consume, notify the roster, end the run, then tell the closer");
        }

        /// <summary>
        /// The unbound path: an unbound gem is destroyed, a fragment and a non-gem are refused on their own
        /// terms, and DecideGive hands those two straight to CanDestroy.
        /// </summary>
        [TestMethod]
        public void Give_of_an_unbound_gem_destroys()
        {
            var unbound = new DungeonGemSpec("any", 195, 7, "any", 918273, new (string, double)[0], 0, 0);

            Assert.AreEqual(PressGiveDecision.Destroy,
                FragmentPressStation.DecideGive(unbound, Owner, runIsLive: false, 3, 3, out var none));
            Assert.IsNull(none);

            // runIsLive is meaningless for an unbound gem and must not change the answer.
            Assert.AreEqual(PressGiveDecision.Destroy,
                FragmentPressStation.DecideGive(unbound, Owner, runIsLive: true, 3, 3, out _));

            var fragment = new DungeonGemSpec("any", 195, 7, "any", 0, new (string, double)[0], 0, 0);
            Assert.AreEqual(PressGiveDecision.Refuse,
                FragmentPressStation.DecideGive(fragment, Owner, runIsLive: false, 3, 3, out var alreadyRefusal));
            Assert.AreEqual(FragmentPressStation.AlreadyAFragmentMessage, alreadyRefusal);

            Assert.AreEqual(PressGiveDecision.Refuse,
                FragmentPressStation.DecideGive(null, Owner, runIsLive: false, 0, 0, out var notAGem));
            Assert.AreEqual(FragmentPressStation.NotAGemMessage, notAGem);
        }

        /// <summary>
        /// The two confirmation prompts, since they are the only place the forfeited entries are stated and
        /// the only place "nothing comes back" is promised before the player commits.
        /// </summary>
        [TestMethod]
        public void Confirmation_prompts_name_the_entries_and_promise_nothing_back()
        {
            Assert.AreEqual(
                "End your run in Filo's Doom and destroy the gem? It has 2 of 3 entries left, and nothing comes back.",
                FragmentPressStation.ComposeCloseConfirm("Filo's Doom", 2, 3));

            Assert.AreEqual(
                "Destroy this gem at the press? It has 2 of 3 entries left, and nothing comes back.",
                FragmentPressStation.ComposeDestroyConfirm(2, 3));

            // StringAssert rather than Assert.AreEqual: both sides would be compile-time constants and
            // MSTEST0032 flags that as an assertion whose condition is known at build time.
            StringAssert.Contains(FragmentPressStation.ClosedMessage,
                "The press grinds the gem to dust and the way behind it closes.");
        }

        /// <summary>
        /// Neither success line implies a fragment or gem comes back - that is the whole behavioral change
        /// this feature makes. (Not asserting the two messages differ from each other: both sides would be
        /// compile-time constants, which MSTEST0032 flags as a condition known at build time.)
        /// </summary>
        [TestMethod]
        public void Destroyed_and_closed_messages_promise_nothing_back()
        {
            StringAssert.DoesNotMatch(FragmentPressStation.DestroyedMessage, new System.Text.RegularExpressions.Regex("fragment", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            StringAssert.DoesNotMatch(FragmentPressStation.ClosedMessage, new System.Text.RegularExpressions.Regex("fragment", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }

        /// <summary>
        /// GemEntries feeds the confirmation prompts with the two entry counts read straight off the gem.
        /// The composed description is asserted alongside the pair, because "Entries: 1 of 3" is the whole
        /// point of keeping the two numbers apart.
        /// </summary>
        [TestMethod]
        public void GemEntries_reads_both_counts_off_the_gem()
        {
            var (entries, maxEntries) = FragmentPressStation.GemEntries(1, 3);

            Assert.AreEqual(1, entries);
            Assert.AreEqual(3, maxEntries);

            var gem = new DungeonGemSpec("any", 195, 7, "any", 918273, new (string, double)[0], 0, 0);
            var desc = ThreadDungeonGemHandler.ComposeLongDesc(gem, "any dungeon", entries, maxEntries, null, null);

            StringAssert.Contains(desc, "\nEntries: 1 of 3\n");

            // A gem the press never touched is full, and a nonsense pair still cannot exceed its own ceiling.
            Assert.AreEqual((3, 3), FragmentPressStation.GemEntries(3, 3));
            Assert.AreEqual((2, 2), FragmentPressStation.GemEntries(5, 2));
            Assert.AreEqual((0, 0), FragmentPressStation.GemEntries(null, null));
        }

        /// <summary>
        /// REWRITTEN for press v2 (owner ruling, 2026-09-07). A fragment with nothing loaded used to press
        /// into a bare gem carrying no modifiers at all; under the slot board every slot resolves, so it now
        /// presses into a COMPLETE, fully random gem. That reversal is the whole point of the board - it
        /// removes the failure mode where a lightly loaded fragment produced a thin, boring gem - so the
        /// test is inverted rather than deleted.
        ///
        /// The fixture here carries only tapers, so the four taper slots resolve and the other five are
        /// skipped; the level, tier and entry count are untouched because no scarab is in the store.
        /// </summary>
        [TestMethod]
        public void Pressing_an_empty_fragment_still_fills_every_slot_the_store_can_fill()
        {
            var store = Store();
            var fragment = new DungeonGemSpec("any", 235, 7, "any", 0, new (string, double)[0], 0, 0);

            var pressed = RawFragmentRules.Resolve(new PressState(fragment, 3), store.Attunement, store.Modifiers,
                Limits(), new Random(20260904), out var log);

            Assert.AreEqual(4, log.Count, "four taper slots, all drawn");
            Assert.IsTrue(log.All(d => d.Drawn), "nothing was loaded, so every slot drew");
            Assert.AreNotEqual(0, pressed.Spec.Seed);
            Assert.AreEqual(1, pressed.Spec.Presses);
            Assert.AreEqual(0, pressed.Spec.Load.Count);
            Assert.AreEqual(1, pressed.Spec.Modifiers.Count, "the only taper in the fixture names savage");
            Assert.AreEqual("savage", pressed.Spec.Modifiers[0].Id);
            Assert.AreEqual(235, pressed.Spec.Level, "no scarab in the fixture, so the level is untouched");
            Assert.AreEqual(7, pressed.Spec.Tier);
            Assert.AreEqual(3, pressed.Entries);
        }
    }
}
