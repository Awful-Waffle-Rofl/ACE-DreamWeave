using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Group Threads roster formation (spec 4.1, rulings R9 and R12): eligibility and its rule order, the IP rule,
    /// seat order and the cap, and the player-facing lines S1-S3, S5, S6 and S10. Pure; no PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class GroupRosterRulesTests
    {
        private const int MinLevel = 150;
        private const uint OwnerGuid = 0x50000100u;

        private static RosterCandidate C(uint guid, string name = null, int level = 200, bool pkTimer = false,
            bool inEphemeral = false, bool onLiveRoster = false, bool hasPackRoom = true, string ip = null, bool inRange = true, bool puzzleLockedOut = false)
            => new RosterCandidate(guid, name ?? $"P{guid:X}", 7, level, pkTimer, inEphemeral, onLiveRoster, hasPackRoom, ip, inRange, puzzleLockedOut ? System.TimeSpan.FromMinutes(114) : (System.TimeSpan?)null);

        private static RosterCandidate Owner(string ip = null) => C(OwnerGuid, "Owner", ip: ip);

        private static RosterExclusionReason OnlyExclusion(RosterCandidate fellow)
        {
            var result = GroupRosterRules.Form(Owner(), new[] { fellow }, MinLevel, 20);
            Assert.AreEqual(1, result.Exclusions.Count, "exactly one exclusion");
            Assert.AreEqual(fellow.Name, result.Exclusions[0].Name);
            Assert.AreEqual(1, result.Seats.Count, "an excluded fellow holds no seat");
            Assert.IsFalse(result.IsGroup);
            return result.Exclusions[0].Reason;
        }

        [TestMethod]
        public void Each_exclusion_reason_is_reported()
        {
            Assert.AreEqual(RosterExclusionReason.OutOfRange, OnlyExclusion(C(0x50000200u, inRange: false)));
            Assert.AreEqual(RosterExclusionReason.BelowLevel, OnlyExclusion(C(0x50000201u, level: MinLevel - 1)));
            Assert.AreEqual(RosterExclusionReason.PkTimer, OnlyExclusion(C(0x50000203u, pkTimer: true)));
            Assert.AreEqual(RosterExclusionReason.InInstance, OnlyExclusion(C(0x50000204u, inEphemeral: true)));
            Assert.AreEqual(RosterExclusionReason.OnLiveRoster, OnlyExclusion(C(0x50000205u, onLiveRoster: true)));
            Assert.AreEqual(RosterExclusionReason.NoPackRoom, OnlyExclusion(C(0x50000206u, hasPackRoom: false)));
        }

        /// <summary>
        /// Owner ruling, 2026-09-17: attack or cast mode is NOT a roster exclusion. There was an InCombat reason and
        /// an inCombat input until then; both are gone rather than left dead, because nothing persists a reason (they
        /// are rendered into chat and the formation log and nowhere else).
        ///
        /// The rule set is now exactly these six, in this order, and this asserts the closed set rather than just the
        /// absence: a reason added without a decision would fail here.
        /// </summary>
        [TestMethod]
        public void Combat_mode_is_not_a_rule_and_the_rule_set_is_these_seven_in_this_order()
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    RosterExclusionReason.OutOfRange,
                    RosterExclusionReason.PuzzleLockout,
                    RosterExclusionReason.BelowLevel,
                    RosterExclusionReason.PkTimer,
                    RosterExclusionReason.InInstance,
                    RosterExclusionReason.OnLiveRoster,
                    RosterExclusionReason.NoPackRoom,
                },
                System.Enum.GetValues<RosterExclusionReason>(),
                "declaration order is the rule order, OutOfRange first");

            Assert.AreEqual(0, System.Enum.GetNames(typeof(RosterExclusionReason)).Count(n => n.Contains("Combat")),
                "no combat reason survives, dead or otherwise");

            // A fellow who passes every surviving rule is seated, whatever they are swinging at.
            var result = GroupRosterRules.Form(Owner(), new[] { C(0x50000401u, "Fighter") }, MinLevel, 20);

            Assert.AreEqual(0, result.Exclusions.Count);
            Assert.AreEqual(2, result.Seats.Count);
            Assert.IsTrue(result.IsGroup);
        }

        /// <summary>
        /// The puzzle fail policy's lockout excludes a fellow, second only to range: a barred fellow who is also below
        /// level or keyless hears the reason that will still be true after they fix the others.
        /// </summary>
        [TestMethod]
        public void A_puzzle_lockout_excludes_a_fellow_after_range_and_before_everything_else()
        {
            Assert.AreEqual(RosterExclusionReason.PuzzleLockout, OnlyExclusion(C(0x50000501u, puzzleLockedOut: true)));
            Assert.AreEqual(RosterExclusionReason.PuzzleLockout, OnlyExclusion(C(0x50000502u, puzzleLockedOut: true, level: 1, hasPackRoom: false, pkTimer: true)));
            Assert.AreEqual(RosterExclusionReason.OutOfRange, OnlyExclusion(C(0x50000503u, puzzleLockedOut: true, inRange: false)));
            Assert.AreEqual("Ann cannot join: barred from the Threads.", GroupRosterRules.ExclusionText("Ann", RosterExclusionReason.PuzzleLockout, 150));
        }

        /// <summary>
        /// Coordinator ruling 2026-10-06: the lockout reason carries the time left in the gem refusal's own words
        /// ("barred from the Threads for another {t}"), in the owner's line, the fellow's line and the offer dialog.
        /// </summary>
        [TestMethod]
        public void A_puzzle_lockout_reason_names_the_time_left()
        {
            var form = GroupRosterRules.Form(Owner(), new[] { C(0x50000601u, "Ann", puzzleLockedOut: true), C(0x50000602u, "Bea", level: 1) }, MinLevel, 20);

            Assert.AreEqual(System.TimeSpan.FromMinutes(114), GroupRosterRules.Remaining(form.LockoutRemaining, "Ann"));
            Assert.IsNull(GroupRosterRules.Remaining(form.LockoutRemaining, "Bea"), "only lockout exclusions carry a time");

            var t = GroupRosterRules.Remaining(form.LockoutRemaining, "Ann");
            Assert.AreEqual("Ann cannot join: barred from the Threads for another 1h 54m.",
                GroupRosterRules.ExclusionText("Ann", RosterExclusionReason.PuzzleLockout, MinLevel, t));
            Assert.AreEqual("Owner opened a Thread for the fellowship, but you could not be included: barred from the Threads for another 1h 54m.",
                GroupRosterRules.ExcludedFellowText("Owner", RosterExclusionReason.PuzzleLockout, MinLevel, t));

            StringAssert.Contains(GroupRosterRules.SoloOfferText(form.Exclusions, MinLevel, form.LockoutRemaining),
                "  Ann - barred from the Threads for another 1h 54m");
        }

        [TestMethod]
        public void At_the_minimum_level_a_fellow_is_eligible()
        {
            var result = GroupRosterRules.Form(Owner(), new[] { C(0x50000201u, level: MinLevel) }, MinLevel, 20);

            Assert.AreEqual(0, result.Exclusions.Count);
            Assert.AreEqual(2, result.Seats.Count);
            Assert.IsTrue(result.IsGroup);
        }

        /// <summary>A fellow failing several rules is reported under the first failing one, in the declared order.</summary>
        [TestMethod]
        public void The_first_failing_rule_is_the_exclusion()
        {
            Assert.AreEqual(RosterExclusionReason.OutOfRange, OnlyExclusion(C(0x50000300u, level: 1, pkTimer: true, inEphemeral: true, onLiveRoster: true, hasPackRoom: false, inRange: false)));
            Assert.AreEqual(RosterExclusionReason.BelowLevel, OnlyExclusion(C(0x50000301u, level: 1, pkTimer: true, inEphemeral: true, onLiveRoster: true, hasPackRoom: false)));
            Assert.AreEqual(RosterExclusionReason.PkTimer, OnlyExclusion(C(0x50000303u, pkTimer: true, inEphemeral: true, onLiveRoster: true, hasPackRoom: false)));
            Assert.AreEqual(RosterExclusionReason.InInstance, OnlyExclusion(C(0x50000304u, inEphemeral: true, onLiveRoster: true, hasPackRoom: false)));
            Assert.AreEqual(RosterExclusionReason.OnLiveRoster, OnlyExclusion(C(0x50000305u, onLiveRoster: true, hasPackRoom: false)));
        }

        [TestMethod]
        public void Two_non_exempt_characters_on_one_ip_fail_the_start_and_are_both_named()
        {
            var result = GroupRosterRules.Form(Owner("10.0.0.1"),
                new[] { C(0x50000401u, "Alt", ip: "10.0.0.1"), C(0x50000402u, "Friend", ip: "10.0.0.2") }, MinLevel, 20);

            Assert.IsTrue(result.IpConflict);
            CollectionAssert.AreEqual(new[] { "Owner", "Alt" }, result.IpConflictNames.ToArray());
            Assert.AreEqual(1, result.Seats.Count, "an IP conflict seats only the owner");
            Assert.AreEqual(OwnerGuid, result.Seats[0].Guid);
            Assert.IsFalse(result.IsGroup);
        }

        [TestMethod]
        public void Two_fellows_sharing_an_ip_conflict_without_the_owner()
        {
            var result = GroupRosterRules.Form(Owner("10.0.0.9"),
                new[] { C(0x50000402u, "B", ip: "10.0.0.1"), C(0x50000401u, "A", ip: "10.0.0.1") }, MinLevel, 20);

            Assert.IsTrue(result.IpConflict);
            CollectionAssert.AreEqual(new[] { "A", "B" }, result.IpConflictNames.ToArray(), "roster order: ascending guid");
        }

        [TestMethod]
        public void Three_on_one_ip_name_all_three()
        {
            var three = GroupRosterRules.Form(Owner("10.0.0.1"),
                new[] { C(0x50000403u, "C", ip: "10.0.0.1"), C(0x50000401u, "A", ip: "10.0.0.1"), C(0x50000402u, "B", ip: "10.0.0.2") }, MinLevel, 20);

            Assert.IsTrue(three.IpConflict);
            CollectionAssert.AreEqual(new[] { "Owner", "A", "C" }, three.IpConflictNames.ToArray(), "all three named in roster order; B, on its own IP, is not");
            Assert.AreEqual(1, three.Seats.Count);

            // Two separate colliding keys are both reported.
            var twoPairs = GroupRosterRules.Form(Owner("10.0.0.1"),
                new[] { C(0x50000401u, "A", ip: "10.0.0.1"), C(0x50000402u, "B", ip: "10.0.0.2"), C(0x50000403u, "C", ip: "10.0.0.2") }, MinLevel, 20);

            CollectionAssert.AreEqual(new[] { "Owner", "A", "B", "C" }, twoPairs.IpConflictNames.ToArray());
        }

        [TestMethod]
        public void Exempt_characters_with_null_keys_never_collide()
        {
            var result = GroupRosterRules.Form(Owner(null),
                new[] { C(0x50000401u, ip: null), C(0x50000402u, ip: null), C(0x50000403u, ip: "10.0.0.1") }, MinLevel, 20);

            Assert.IsFalse(result.IpConflict);
            Assert.AreEqual(0, result.IpConflictNames.Count);
            Assert.AreEqual(4, result.Seats.Count);
        }

        [TestMethod]
        public void Ineligible_fellows_are_not_ip_checked()
        {
            var result = GroupRosterRules.Form(Owner("10.0.0.1"),
                new[] { C(0x50000401u, "Alt", pkTimer: true, ip: "10.0.0.1"), C(0x50000402u, "Friend", ip: "10.0.0.2") }, MinLevel, 20);

            Assert.IsFalse(result.IpConflict, "the same-IP fellow was excluded first, so it cannot collide");
            Assert.AreEqual(1, result.Exclusions.Count);
            Assert.AreEqual(("Alt", RosterExclusionReason.PkTimer), result.Exclusions[0]);
            CollectionAssert.AreEqual(new[] { OwnerGuid, 0x50000402u }, result.Seats.Select(s => s.Guid).ToArray());
        }

        [TestMethod]
        public void Seats_are_owner_first_then_ascending_guid_and_capped()
        {
            var fellows = new[] { C(0x50000404u), C(0x50000401u), C(0x50000403u), C(0x50000402u) };

            var all = GroupRosterRules.Form(Owner(), fellows, MinLevel, 20);
            CollectionAssert.AreEqual(new[] { OwnerGuid, 0x50000401u, 0x50000402u, 0x50000403u, 0x50000404u }, all.Seats.Select(s => s.Guid).ToArray());
            Assert.AreEqual("Owner", all.Seats[0].Name);

            var capped = GroupRosterRules.Form(Owner(), fellows, MinLevel, 3);
            CollectionAssert.AreEqual(new[] { OwnerGuid, 0x50000401u, 0x50000402u }, capped.Seats.Select(s => s.Guid).ToArray());
            Assert.IsTrue(capped.IsGroup);

            var ownerOnly = GroupRosterRules.Form(Owner(), fellows, MinLevel, 1);
            Assert.AreEqual(1, ownerOnly.Seats.Count);
            Assert.IsFalse(ownerOnly.IsGroup);

            var nonsense = GroupRosterRules.Form(Owner(), fellows, MinLevel, 0);
            Assert.AreEqual(1, nonsense.Seats.Count, "a cap below 1 still seats the owner");
        }

        [TestMethod]
        public void A_seat_carries_the_candidates_captured_values()
        {
            var result = GroupRosterRules.Form(Owner(), new[] { new RosterCandidate(0x50000401u, "Ann", 42, 233, false, false, false, true, null) }, MinLevel, 20);
            var seat = result.Seats[1];

            Assert.AreEqual(0x50000401u, seat.Guid);
            Assert.AreEqual("Ann", seat.Name);
            Assert.AreEqual(42u, seat.AccountId);
            Assert.AreEqual(233, seat.Level);
        }

        [TestMethod]
        public void No_eligible_fellows_is_not_a_group()
        {
            var none = GroupRosterRules.Form(Owner(), new RosterCandidate[0], MinLevel, 20);
            Assert.IsFalse(none.IsGroup);
            Assert.AreEqual(1, none.Seats.Count);

            var nullFellows = GroupRosterRules.Form(Owner(), null, MinLevel, 20);
            Assert.IsFalse(nullFellows.IsGroup);

            var allExcluded = GroupRosterRules.Form(Owner(), new[] { C(0x50000401u, level: 1), C(0x50000402u, hasPackRoom: false) }, MinLevel, 20);
            Assert.IsFalse(allExcluded.IsGroup);
            Assert.AreEqual(2, allExcluded.Exclusions.Count);
        }

        [TestMethod]
        public void Nulls_repeats_and_the_owner_guid_are_ignored()
        {
            var result = GroupRosterRules.Form(Owner(),
                new[] { null, C(0x50000401u, "A"), C(0x50000401u, "A again"), C(OwnerGuid, "Owner again") }, MinLevel, 20);

            CollectionAssert.AreEqual(new[] { OwnerGuid, 0x50000401u }, result.Seats.Select(s => s.Guid).ToArray());
            Assert.AreEqual(0, result.Exclusions.Count);
        }

        [TestMethod]
        public void Form_refuses_a_null_owner()
        {
            Assert.ThrowsExactly<System.ArgumentNullException>(() => GroupRosterRules.Form(null, new RosterCandidate[0], MinLevel, 20));
        }

        /// <summary>Orchestrator ruling on Task 5: on Yes, only exclusions not already shown with the offer are sent again.</summary>
        [TestMethod]
        public void New_exclusions_suppress_repeats_but_keep_new_fellows_and_new_reasons()
        {
            var offered = new[]
            {
                ("Ann", RosterExclusionReason.InInstance),
                ("Bob", RosterExclusionReason.BelowLevel),
                ("Cid", RosterExclusionReason.NoPackRoom),
            };

            var current = new[]
            {
                ("Ann", RosterExclusionReason.InInstance),   // same fellow, same reason: suppressed
                ("Bob", RosterExclusionReason.PkTimer),      // same fellow, new reason: new
                ("Dee", RosterExclusionReason.OnLiveRoster), // new fellow: new
                ("Cid", RosterExclusionReason.NoPackRoom),   // same again: suppressed
            };

            var fresh = GroupRosterRules.NewExclusions(offered, current);
            CollectionAssert.AreEqual(new[] { ("Bob", RosterExclusionReason.PkTimer), ("Dee", RosterExclusionReason.OnLiveRoster) }, fresh.ToArray(),
                "current order kept");

            CollectionAssert.AreEqual(current, GroupRosterRules.NewExclusions(null, current).ToArray(), "nothing offered: all are new");
            CollectionAssert.AreEqual(current, GroupRosterRules.NewExclusions(new (string, RosterExclusionReason)[0], current).ToArray());
            Assert.AreEqual(0, GroupRosterRules.NewExclusions(offered, null).Count);
            Assert.AreEqual(0, GroupRosterRules.NewExclusions(offered, new (string, RosterExclusionReason)[0]).Count);
            Assert.AreEqual(0, GroupRosterRules.NewExclusions(offered, offered).Count, "an identical re-form reports nothing");
        }

        /// <summary>The offer and the re-form both come out of Form, so the pure pair composes end to end.</summary>
        [TestMethod]
        public void New_exclusions_over_two_formations()
        {
            var atOffer = GroupRosterRules.Form(Owner(), new[] { C(0x50000401u, "Ann", inEphemeral: true), C(0x50000402u, "Bob", level: 1), C(0x50000403u, "Cid") }, MinLevel, 20);
            var atYes = GroupRosterRules.Form(Owner(), new[] { C(0x50000401u, "Ann", inEphemeral: true), C(0x50000402u, "Bob", level: 1, hasPackRoom: false), C(0x50000403u, "Cid", pkTimer: true) }, MinLevel, 20);

            // Ann repeats (suppressed); Bob is still BelowLevel first (suppressed); Cid is newly excluded (new).
            CollectionAssert.AreEqual(new[] { ("Cid", RosterExclusionReason.PkTimer) }, GroupRosterRules.NewExclusions(atOffer.Exclusions, atYes.Exclusions).ToArray());
        }

        [TestMethod]
        public void Player_facing_lines_match_the_confirmed_wording()
        {
            Assert.AreEqual("This Thread cannot open for your fellowship: Ann, Bob share a connection. Only one character per connection may join.",
                GroupRosterRules.IpConflictText(new[] { "Ann", "Bob" }));

            Assert.AreEqual("Ann cannot join: too far away.", GroupRosterRules.ExclusionText("Ann", RosterExclusionReason.OutOfRange, 150));
            Assert.AreEqual("Ann cannot join: below level 150.", GroupRosterRules.ExclusionText("Ann", RosterExclusionReason.BelowLevel, 150));
            Assert.AreEqual("Ann cannot join: PK timer active.", GroupRosterRules.ExclusionText("Ann", RosterExclusionReason.PkTimer, 150));
            Assert.AreEqual("Ann cannot join: inside another instance.", GroupRosterRules.ExclusionText("Ann", RosterExclusionReason.InInstance, 150));
            Assert.AreEqual("Ann cannot join: already part of an open Thread.", GroupRosterRules.ExclusionText("Ann", RosterExclusionReason.OnLiveRoster, 150));
            Assert.AreEqual("Ann cannot join: no room for a key.", GroupRosterRules.ExclusionText("Ann", RosterExclusionReason.NoPackRoom, 150));

            Assert.AreEqual("Owner opened Filos Doom for your fellowship. A Thread Key is in your pack; use it to enter.",
                GroupRosterRules.KeyGrantedText("Owner", "Filos Doom"));
            Assert.AreEqual("Ann could not join: already part of an open Thread.", GroupRosterRules.DroppedText("Ann"));
            Assert.AreEqual("Ann did not receive a key.", GroupRosterRules.KeyFailedText("Ann"));
        }

        /// <summary>
        /// The lines a FELLOW gets, which did not exist before 2026-09-17: until then every exclusion and every key
        /// failure went to the owner alone, so the player it happened to heard nothing at all. Both must name the
        /// owner, because from the fellow's side nothing else explains why a message arrived.
        /// </summary>
        [TestMethod]
        public void The_excluded_fellow_and_the_keyless_member_are_told_themselves()
        {
            Assert.AreEqual("Owner opened a Thread for the fellowship, but you could not be included: too far away.",
                GroupRosterRules.ExcludedFellowText("Owner", RosterExclusionReason.OutOfRange, 150));

            Assert.AreEqual("Owner opened a Thread for the fellowship, but you could not be included: below level 150.",
                GroupRosterRules.ExcludedFellowText("Owner", RosterExclusionReason.BelowLevel, 150));

            // The same vocabulary as the owner's line, so the two can never disagree about one fellow.
            foreach (RosterExclusionReason reason in System.Enum.GetValues(typeof(RosterExclusionReason)))
            {
                var ownerLine = GroupRosterRules.ExclusionText("Ann", reason, 150);
                var reasonText = ownerLine.Substring("Ann cannot join: ".Length);

                StringAssert.EndsWith(GroupRosterRules.ExcludedFellowText("Owner", reason, 150), reasonText);
            }

            Assert.AreEqual("Owner opened Filos Doom for your fellowship, but your Thread Key could not be created. Ask them to close the Thread and open it again.",
                GroupRosterRules.KeyFailedMemberText("Owner", "Filos Doom"));

            StringAssert.Contains(GroupRosterRules.KeyGrantedPopupText("Owner", "Filos Doom"), "Owner");
            StringAssert.Contains(GroupRosterRules.KeyGrantedPopupText("Owner", "Filos Doom"), "Filos Doom");
            StringAssert.Contains(GroupRosterRules.KeyGrantedPopupText("Owner", "Filos Doom"), GroupRosterRules.KeyItemName);
            StringAssert.Contains(GroupRosterRules.KeyGrantedPopupText("Owner", "Filos Doom"), "USE");

            Assert.AreEqual("The Thread stays closed.", GroupRosterRules.ThreadStaysClosedText);
            Assert.AreEqual("Your fellowship can no longer join. The Thread stays closed.", GroupRosterRules.FellowshipCannotJoinText);
        }

        private static RosterFormResult FormOf(params RosterCandidate[] fellows)
            => GroupRosterRules.Form(Owner(), fellows, MinLevel, 20);

        /// <summary>
        /// S1, per owner ruling 2026-09-17 ("1 line per excluded character. Name, concise reason."), which superseded
        /// the same day's earlier shape that also named every joining member. The joining side is a count in the
        /// header, the roster INCLUDING the owner; below it, one "Name - reason" line per exclusion in formation order.
        /// </summary>
        [TestMethod]
        public void The_offer_counts_the_roster_and_lists_only_the_exclusions()
        {
            var form = GroupRosterRules.Form(
                C(OwnerGuid, "Kestrel Alpha"),
                new[]
                {
                    C(0x50000401u, "Kestrel Bravo"),
                    C(0x50000403u, "Corma Lan", inRange: false),
                    C(0x50000402u, "Quarry Echo"),
                    C(0x50000404u, "Tester Four", pkTimer: true),
                },
                MinLevel, 20);

            var text = GroupRosterRules.OfferText(form.Seats, form.Exclusions, MinLevel);

            Assert.AreEqual(
                "Open this Thread for your fellowship (3 joining)?\n"
                + "\n"
                + "Cannot join:\n"
                + "  Corma Lan - too far away\n"
                + "  Tester Four - PK timer active",
                text);

            Assert.IsFalse(text.Contains("Kestrel Bravo") || text.Contains("Quarry Echo") || text.Contains("(you)"),
                "joining members are counted, never listed");
        }

        [TestMethod]
        public void The_offer_is_exactly_the_one_line_question_when_nobody_was_excluded()
        {
            var form = GroupRosterRules.Form(C(OwnerGuid, "Kestrel Alpha"),
                new[] { C(0x50000401u, "Kestrel Bravo"), C(0x50000402u, "Quarry Echo") }, MinLevel, 20);

            Assert.AreEqual("Open this Thread for your fellowship (3 joining)?",
                GroupRosterRules.OfferText(form.Seats, form.Exclusions, MinLevel));
        }

        /// <summary>
        /// The dialog's reason wording IS ReasonText, reached through the exclusion line, so there is exactly one
        /// vocabulary for a reason and the dialog cannot drift away from the chat log.
        /// </summary>
        [TestMethod]
        public void The_offers_reasons_are_the_same_words_as_the_chat_lines()
        {
            foreach (RosterExclusionReason reason in System.Enum.GetValues(typeof(RosterExclusionReason)))
            {
                var form = FormOf(C(0x50000401u, "Ann", level: reason == RosterExclusionReason.BelowLevel ? 1 : 200,
                    pkTimer: reason == RosterExclusionReason.PkTimer,
                    inEphemeral: reason == RosterExclusionReason.InInstance,
                    onLiveRoster: reason == RosterExclusionReason.OnLiveRoster,
                    hasPackRoom: reason != RosterExclusionReason.NoPackRoom,
                    inRange: reason != RosterExclusionReason.OutOfRange,
                    puzzleLockedOut: reason == RosterExclusionReason.PuzzleLockout));

                Assert.AreEqual(1, form.Exclusions.Count, $"{reason}: exactly one exclusion");
                Assert.AreEqual(reason, form.Exclusions[0].Reason);

                var ownerLine = GroupRosterRules.ExclusionText("Ann", reason, MinLevel);
                var reasonText = ownerLine.Substring("Ann cannot join: ".Length).TrimEnd('.');

                StringAssert.Contains(GroupRosterRules.OfferText(form.Seats, form.Exclusions, MinLevel), $"  Ann - {reasonText}");
                StringAssert.Contains(GroupRosterRules.SoloOfferText(form.Exclusions, MinLevel), $"  Ann - {reasonText}");
            }
        }

        /// <summary>
        /// S1b: the solo fallback names the reasons too, and degenerates to one sentence for a fellowship where
        /// there was simply nobody else to consider.
        /// </summary>
        [TestMethod]
        public void The_solo_fallback_lists_the_reasons_and_collapses_when_there_are_none()
        {
            var form = FormOf(C(0x50000401u, "Corma Lan", inRange: false), C(0x50000402u, "Tester Four", pkTimer: true));

            Assert.AreEqual(
                "No one in your fellowship can join right now:\n"
                + "  Corma Lan - too far away\n"
                + "  Tester Four - PK timer active\n"
                + "\n"
                + "Open this Thread alone?",
                GroupRosterRules.SoloOfferText(form.Exclusions, MinLevel));

            Assert.AreEqual("No one in your fellowship can join right now. Open this Thread alone?",
                GroupRosterRules.SoloOfferText(new (string, RosterExclusionReason)[0], MinLevel));

            Assert.AreEqual("No one in your fellowship can join right now. Open this Thread alone?",
                GroupRosterRules.SoloOfferText(null, MinLevel));
        }

        /// <summary>
        /// The overflow trims ONLY trailing exclusion lines, in formation order, and never the header: the lines kept
        /// are exactly the first K exclusions, the summary counts exactly the rest, and the prompt fits the budget.
        /// Asserted as the exact expected prompt rebuilt from K, so a wrong count or a reordered line fails.
        /// </summary>
        [TestMethod]
        public void The_overflow_trims_only_trailing_exclusions_and_keeps_the_header()
        {
            var fellows = new List<RosterCandidate>();

            for (var i = 0; i < 2; i++)
                fellows.Add(C((uint)(0x50001000u + i), $"Joiner {i}"));

            for (var i = 0; i < 40; i++)
                fellows.Add(C((uint)(0x50002000u + i), $"Excluded Fellow Name {i:D2}", onLiveRoster: true));

            var form = GroupRosterRules.Form(C(OwnerGuid, "Kestrel Alpha"), fellows, MinLevel, 100);
            var text = GroupRosterRules.OfferText(form.Seats, form.Exclusions, MinLevel);

            Assert.IsTrue(text.Length <= GroupRosterRules.OfferTextMaxLength, $"length {text.Length} is inside the budget");

            var shown = text.Split('\n').Count(l => l.StartsWith("  Excluded Fellow Name ", System.StringComparison.Ordinal));
            Assert.IsTrue(shown > 0 && shown < 40, $"some but not all exclusions fit ({shown})");

            var expected = "Open this Thread for your fellowship (3 joining)?\n\nCannot join:";
            for (var i = 0; i < shown; i++)
                expected += $"\n  Excluded Fellow Name {i:D2} - already part of an open Thread";
            expected += $"\n  ... and {40 - shown} more.";

            Assert.AreEqual(expected, text, "the first K in formation order, then an exact count of the rest");

            // Maximal: one more line would not have fitted, so nothing was trimmed that did not need to be.
            var oneMore = expected.Replace($"\n  ... and {40 - shown} more.",
                $"\n  Excluded Fellow Name {shown:D2} - already part of an open Thread\n  ... and {39 - shown} more.");
            Assert.IsTrue(oneMore.Length > GroupRosterRules.OfferTextMaxLength, "the trim stops as soon as the prompt fits");

            var solo = GroupRosterRules.SoloOfferText(form.Exclusions, MinLevel);
            Assert.IsTrue(solo.Length <= GroupRosterRules.OfferTextMaxLength, $"length {solo.Length} is inside the budget");
            StringAssert.StartsWith(solo, "No one in your fellowship can join right now:\n  Excluded Fellow Name 00 - ");
            StringAssert.EndsWith(solo, " more.\n\nOpen this Thread alone?", "a trimmed solo prompt still ends on its question");
        }

        /// <summary>
        /// A near-maximum fellowship (fellowship_max_members defaults to 20) with the owner alone and 19 fellows
        /// excluded for mixed reasons, with realistic character names. Lands under the cap with a correct count.
        /// </summary>
        [TestMethod]
        public void A_near_max_fellowship_mostly_excluded_lands_under_the_cap_with_a_correct_count()
        {
            var names = new[]
            {
                "Rooksbane", "Corma Lan", "Aerlinthe Vale", "Tester Four", "Quarry Echo", "Mhoire Thorne", "Sella",
                "Kestrel Bravo", "Lady Ilseriane", "Old Bayle", "Gharu Nassir", "Tou Tou Rider", "Wyrmslayer",
                "Hebian To", "Cragstone Kid", "Ayan Baqur", "Sawato", "Linvak Tukal", "Dryreach Drifter",
            };

            var fellows = new List<RosterCandidate>();

            for (var i = 0; i < names.Length; i++)
            {
                var reason = i % 5;
                fellows.Add(C((uint)(0x50003000u + i), names[i],
                    inRange: reason != 0,
                    pkTimer: reason == 1,
                    inEphemeral: reason == 2,
                    onLiveRoster: reason == 3,
                    hasPackRoom: reason != 4));
            }

            var form = GroupRosterRules.Form(C(OwnerGuid, "Kestrel Alpha"), fellows, MinLevel, 20);
            Assert.AreEqual(19, form.Exclusions.Count, "fixture: all nineteen are excluded");

            var text = GroupRosterRules.OfferText(form.Seats, form.Exclusions, MinLevel);
            var lines = text.Split('\n');
            var shownNames = lines.Where(l => l.StartsWith("  ", System.StringComparison.Ordinal) && !l.StartsWith("  ... and ", System.StringComparison.Ordinal)).ToList();

            Assert.IsTrue(text.Length <= GroupRosterRules.OfferTextMaxLength, $"length {text.Length} is inside the budget");
            StringAssert.StartsWith(text, "Open this Thread for your fellowship (1 joining)?\n\nCannot join:");

            for (var i = 0; i < shownNames.Count; i++)
                StringAssert.StartsWith(shownNames[i], $"  {form.Exclusions[i].Name} - ", "formation order is kept");

            var hidden = 19 - shownNames.Count;

            if (hidden > 0)
                StringAssert.EndsWith(text, $"\n  ... and {hidden} more.");
            else
                Assert.IsFalse(text.Contains("... and "), "nothing is summarised when everything fit");
        }

        /// <summary>A retail-sized fellowship, eight excluded under the owner with the longest reason, fits whole.</summary>
        [TestMethod]
        public void A_retail_sized_fellowship_is_never_truncated()
        {
            var fellows = new List<RosterCandidate>();

            for (var i = 0; i < 8; i++)
                fellows.Add(C((uint)(0x50002000u + i), $"Sixteen Chars {i:D2}", onLiveRoster: true));

            var form = GroupRosterRules.Form(C(OwnerGuid, "Owner"), fellows, MinLevel, 9);
            var text = GroupRosterRules.OfferText(form.Seats, form.Exclusions, MinLevel);

            Assert.AreEqual(8, form.Exclusions.Count);
            Assert.AreEqual(479, text.Length, "the worst retail case OfferTextMaxLength's doc comment quotes");
            Assert.IsTrue(text.Length <= GroupRosterRules.OfferTextMaxLength, $"length {text.Length} is inside the budget");
            Assert.IsFalse(text.Contains("... and "), "nothing is summarised at retail fellowship size");
        }

        /// <summary>
        /// The formation line is the server-side record the 2026-09-17 stage reports did not have: seats AND
        /// exclusions, or a repeat report is just as undiagnosable as the first.
        /// </summary>
        [TestMethod]
        public void The_formation_log_line_names_the_seats_and_every_exclusion()
        {
            var form = GroupRosterRules.Form(C(OwnerGuid, "Kestrel Alpha"),
                new[] { C(0x50000401u, "Kestrel Bravo"), C(0x50000402u, "Corma Lan", inRange: false) }, MinLevel, 20);

            Assert.AreEqual("[DYNDUNGEON] formation offer owner=Kestrel Alpha seats=Kestrel Alpha,Kestrel Bravo excluded=Corma Lan:OutOfRange",
                GroupRosterRules.FormationLogText("offer", "Kestrel Alpha", form));

            var clean = GroupRosterRules.Form(C(OwnerGuid, "Owner"), new[] { C(0x50000401u, "Ann") }, MinLevel, 20);
            Assert.AreEqual("[DYNDUNGEON] formation answer owner=Owner seats=Owner,Ann excluded=none",
                GroupRosterRules.FormationLogText("answer", "Owner", clean));

            var conflict = GroupRosterRules.Form(C(OwnerGuid, "Owner", ip: "10.0.0.1"),
                new[] { C(0x50000401u, "Alt", ip: "10.0.0.1") }, MinLevel, 20);
            StringAssert.Contains(GroupRosterRules.FormationLogText("offer", "Owner", conflict), "ipConflict=Owner,Alt");

            Assert.AreEqual("[DYNDUNGEON] formation offer owner=Owner seats=none excluded=none",
                GroupRosterRules.FormationLogText("offer", "Owner", null));
        }

        /// <summary>
        /// The out-of-range rule replaced a SILENT filter: formation now sees every fellowship member, so the
        /// handler must feed it GetFellowshipMembers rather than WithinRange, which returns only the ones that pass.
        /// Needs a live Fellowship, so it is pinned on source; the rule it feeds is driven above (inRange: false).
        /// </summary>
        [TestMethod]
        public void Formation_is_fed_every_fellowship_member_not_only_those_in_range()
        {
            var handler = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs");
            var body = PooledLootSourceText.MethodBody(handler, "private static RosterFormResult FormRoster(");

            StringAssert.Contains(body, "fellowship.GetFellowshipMembers().Values");
            StringAssert.Contains(body, "GroupRosterRules.CandidateFrom(player, f)");
            StringAssert.Contains(body, "log.Info(GroupRosterRules.FormationLogText(stage, player.Name, form));");
            Assert.IsFalse(body.Contains("WithinRange"), "the silent range filter is gone from formation's input");

            // An expression-bodied overload, so the whole file is the unit here rather than a method body.
            var rules = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GroupRosterRules.cs");

            StringAssert.Contains(rules,
                "public static RosterCandidate CandidateFrom(Player owner, Player fellow)\n"
                + "            => CandidateFrom(fellow, Fellowship.GetDistanceScalar(owner?.Location, fellow?.Location) > 0);");
        }

        /// <summary>
        /// The excluded fellow is messaged from ReportFormation, beside the owner's line, and only for exclusions
        /// that are actually being reported this pass. Needs a live Player, so it is pinned on source.
        /// </summary>
        [TestMethod]
        public void The_excluded_fellow_is_messaged_from_report_formation()
        {
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs"),
                "private static bool ReportFormation(");

            var ownerLine = body.IndexOf("Say(player, GroupRosterRules.ExclusionText(name, why, minPlayerLevel, lockout));", System.StringComparison.Ordinal);
            var lookup = body.IndexOf("var fellow = PlayerManager.GetOnlinePlayer(name);", System.StringComparison.Ordinal);
            var fellowLine = body.IndexOf("Say(fellow, GroupRosterRules.ExcludedFellowText(player.Name, why, minPlayerLevel, lockout));", System.StringComparison.Ordinal);

            Assert.IsTrue(ownerLine >= 0 && lookup > ownerLine && fellowLine > lookup,
                "the owner's line, then the fellow lookup, then the fellow's own line");
            StringAssert.Contains(body, "if (fellow != null && fellow != player)");
        }

        /// <summary>
        /// The key grant is a popup PLUS an Advancement chat mirror, not one white Broadcast line: the stage report
        /// of 2026-09-17 was testers not noticing they had been handed a key at all.
        /// </summary>
        [TestMethod]
        public void The_key_grant_pops_a_dialog_and_mirrors_it_on_a_visible_chat_channel()
        {
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs"),
                "private static void GrantKey(");

            StringAssert.Contains(body, "new GameEventPopupString(target.Session, GroupRosterRules.KeyGrantedPopupText(ownerName, dungeonName))");
            StringAssert.Contains(body, "new GameMessageSystemChat(GroupRosterRules.KeyGrantedText(ownerName, dungeonName), ChatMessageType.Advancement)");
            StringAssert.Contains(body, "Say(target, GroupRosterRules.KeyFailedMemberText(ownerName, dungeonName));");

            Assert.AreEqual(2, body.Split(new[] { "KeyFailedMemberText(" }, System.StringSplitOptions.None).Length - 1,
                "both reachable mint failures tell the member; the offline branch has nobody to tell");
        }

        /// <summary>
        /// Final review F4: the live pack-room check reads a FELLOW's inventory from the owner's queue, so a throw there
        /// must count as no room (the fellow is excluded as NoPackRoom) rather than failing the formation. Needs a live
        /// Player, so it is pinned on source; the exclusion it feeds is driven above (hasPackRoom: false).
        /// </summary>
        [TestMethod]
        public void The_pack_room_check_counts_a_throw_as_no_room()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GroupRosterRules.cs");
            var body = PooledLootSourceText.MethodBody(source, "private static bool HasRoomForKey(Player player)");

            var tryAt = body.IndexOf("try", System.StringComparison.Ordinal);
            var build = body.IndexOf("new ItemsToReceive(player)", System.StringComparison.Ordinal);
            var limits = body.IndexOf("return !itemsToReceive.PlayerExceedsLimits;", System.StringComparison.Ordinal);
            var catchAt = body.IndexOf("catch (Exception ex)", System.StringComparison.Ordinal);
            var noRoom = body.IndexOf("return false;", catchAt < 0 ? 0 : catchAt, System.StringComparison.Ordinal);

            Assert.IsTrue(tryAt >= 0 && tryAt < build && build < limits && limits < catchAt && catchAt < noRoom, "the whole check sits in the try; the catch answers no room");

            // The check is WIRED into the candidate's pack-room slot. Since 2026-09-17 both public CandidateFrom
            // overloads are expression-bodied delegators, so the wiring lives in the private brace-bodied builder.
            // Until this was repointed, the pin named "CandidateFrom(Player player)" and MethodBody - which takes
            // the next '{' after the signature - silently read this private builder instead: it passed, but it
            // never checked the member it named. Pin the builder by its own signature, and pin each public
            // overload's routing to it as whole-file text, because MethodBody cannot read a '=>' body.
            var builder = PooledLootSourceText.MethodBody(source, "private static RosterCandidate CandidateFrom(Player player, bool inRange)");

            var onRoster = builder.IndexOf("ThreadDungeonManager.IsOnLiveRoster(player.Guid.Full),", System.StringComparison.Ordinal);
            var packRoom = builder.IndexOf("HasRoomForKey(player),", System.StringComparison.Ordinal);
            var ipKey = builder.IndexOf("ipKey,", System.StringComparison.Ordinal);

            Assert.IsTrue(onRoster >= 0 && onRoster < packRoom && packRoom < ipKey,
                "HasRoomForKey feeds RosterCandidate's hasPackRoom argument, between onLiveRoster and ipKey");

            StringAssert.Contains(source, "public static RosterCandidate CandidateFrom(Player player)\n            => CandidateFrom(player, inRange: true);");
            StringAssert.Contains(source, "public static RosterCandidate CandidateFrom(Player owner, Player fellow)\n"
                + "            => CandidateFrom(fellow, Fellowship.GetDistanceScalar(owner?.Location, fellow?.Location) > 0);");
        }
    }
}
