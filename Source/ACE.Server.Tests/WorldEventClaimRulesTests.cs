using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the WP-05 Weave Cache claim gate (TECH-DESIGN 2.7, 5.6). Everything under test is
    /// a pure static: no database, no landblock, no live Player or Session (D6).
    /// </summary>
    [TestClass]
    public class WorldEventClaimRulesTests
    {
        private const uint Char1 = 0x50000001;
        private const uint Char2 = 0x50000002;
        private const uint Account1 = 1001;
        private const uint Account2 = 1002;
        private const string Ip1 = "192.168.0.10";
        private const string Ip2 = "192.168.0.11";

        private static ISet<uint> Uints(params uint[] values) => new HashSet<uint>(values);

        /// <summary>
        /// Deliberately the DEFAULT (case-sensitive) comparer, not the OrdinalIgnoreCase one the live
        /// WorldEvent uses: the rules must not depend on how the caller built the set.
        /// </summary>
        private static ISet<string> Strings(params string[] values) => new HashSet<string>(values);

        private static ClaimDenial Evaluate(uint characterGuid, uint accountId, string ip,
            ISet<uint> claimedCharacters = null, ISet<uint> claimedAccounts = null,
            ISet<string> claimedIps = null, ISet<string> exemptIps = null,
            bool gateByCharacter = true, bool gateByAccount = true, bool gateByIp = true)
        {
            return WorldEventClaimRules.Evaluate(characterGuid, accountId, ip,
                claimedCharacters ?? Uints(), claimedAccounts ?? Uints(),
                claimedIps ?? Strings(), exemptIps ?? Strings(),
                gateByCharacter, gateByAccount, gateByIp);
        }

        // ---- nothing claimed --------------------------------------------------------------------------

        [TestMethod]
        public void Evaluate_EmptySets_Allows()
        {
            Assert.AreEqual(ClaimDenial.None, Evaluate(Char1, Account1, Ip1));
        }

        [TestMethod]
        public void Evaluate_NullSets_Allows()
        {
            Assert.AreEqual(ClaimDenial.None,
                WorldEventClaimRules.Evaluate(Char1, Account1, Ip1, null, null, null, null, true, true, true));
        }

        // ---- each rule alone --------------------------------------------------------------------------

        [TestMethod]
        public void Evaluate_CharacterAlreadyClaimed_DeniesCharacter()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedCharacter,
                Evaluate(Char1, Account1, Ip1, claimedCharacters: Uints(Char1)));
        }

        [TestMethod]
        public void Evaluate_AccountAlreadyClaimed_DeniesAccount()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedAccount,
                Evaluate(Char1, Account1, Ip1, claimedAccounts: Uints(Account1)));
        }

        [TestMethod]
        public void Evaluate_IpAlreadyClaimed_DeniesIp()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp,
                Evaluate(Char1, Account1, Ip1, claimedIps: Strings(Ip1)));
        }

        [TestMethod]
        public void Evaluate_OtherCharacterClaimed_Allows()
        {
            Assert.AreEqual(ClaimDenial.None,
                Evaluate(Char1, Account1, Ip1, claimedCharacters: Uints(Char2),
                    claimedAccounts: Uints(Account2), claimedIps: Strings(Ip2)));
        }

        // ---- rule ORDER: character, then account, then IP ---------------------------------------------

        [TestMethod]
        public void Evaluate_CharacterAndAccountBothClaimed_ReportsCharacterFirst()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedCharacter,
                Evaluate(Char1, Account1, Ip1, claimedCharacters: Uints(Char1), claimedAccounts: Uints(Account1)));
        }

        [TestMethod]
        public void Evaluate_CharacterAndIpBothClaimed_ReportsCharacterFirst()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedCharacter,
                Evaluate(Char1, Account1, Ip1, claimedCharacters: Uints(Char1), claimedIps: Strings(Ip1)));
        }

        [TestMethod]
        public void Evaluate_AccountAndIpBothClaimed_ReportsAccountFirst()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedAccount,
                Evaluate(Char1, Account1, Ip1, claimedAccounts: Uints(Account1), claimedIps: Strings(Ip1)));
        }

        [TestMethod]
        public void Evaluate_AllThreeClaimed_ReportsCharacterFirst()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedCharacter,
                Evaluate(Char1, Account1, Ip1, claimedCharacters: Uints(Char1),
                    claimedAccounts: Uints(Account1), claimedIps: Strings(Ip1)));
        }

        // ---- toggles off ------------------------------------------------------------------------------

        [TestMethod]
        public void Evaluate_CharacterGateOff_SkipsCharacterRule()
        {
            Assert.AreEqual(ClaimDenial.None,
                Evaluate(Char1, Account1, Ip1, claimedCharacters: Uints(Char1), gateByCharacter: false));
        }

        [TestMethod]
        public void Evaluate_CharacterGateOff_StillFallsThroughToAccount()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedAccount,
                Evaluate(Char1, Account1, Ip1, claimedCharacters: Uints(Char1), claimedAccounts: Uints(Account1),
                    gateByCharacter: false));
        }

        [TestMethod]
        public void Evaluate_AccountGateOff_SkipsAccountRule()
        {
            Assert.AreEqual(ClaimDenial.None,
                Evaluate(Char1, Account1, Ip1, claimedAccounts: Uints(Account1), gateByAccount: false));
        }

        [TestMethod]
        public void Evaluate_AccountGateOff_StillFallsThroughToIp()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp,
                Evaluate(Char1, Account1, Ip1, claimedAccounts: Uints(Account1), claimedIps: Strings(Ip1),
                    gateByAccount: false));
        }

        [TestMethod]
        public void Evaluate_IpGateOff_SkipsIpRule()
        {
            Assert.AreEqual(ClaimDenial.None,
                Evaluate(Char1, Account1, Ip1, claimedIps: Strings(Ip1), gateByIp: false));
        }

        [TestMethod]
        public void Evaluate_AllGatesOff_AllowsEvenWhenEverythingIsClaimed()
        {
            Assert.AreEqual(ClaimDenial.None,
                Evaluate(Char1, Account1, Ip1, claimedCharacters: Uints(Char1), claimedAccounts: Uints(Account1),
                    claimedIps: Strings(Ip1), gateByCharacter: false, gateByAccount: false, gateByIp: false));
        }

        // ---- account id 0 (session gone or unauthenticated) -------------------------------------------

        [TestMethod]
        public void Evaluate_AccountIdZero_NeverDeniesOnAccount()
        {
            // 0 is not an account, so a claimed set that happens to contain 0 must not gate anyone.
            Assert.AreEqual(ClaimDenial.None,
                Evaluate(Char1, 0, Ip1, claimedAccounts: Uints(0)));
        }

        [TestMethod]
        public void Evaluate_AccountIdZero_CharacterRuleStillApplies()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedCharacter,
                Evaluate(Char1, 0, Ip1, claimedCharacters: Uints(Char1)));
        }

        // ---- IP specifics -----------------------------------------------------------------------------

        [TestMethod]
        public void Evaluate_NullIp_SkipsIpRule()
        {
            Assert.AreEqual(ClaimDenial.None, Evaluate(Char1, Account1, null, claimedIps: Strings(Ip1)));
        }

        [TestMethod]
        public void Evaluate_EmptyIp_SkipsIpRule()
        {
            Assert.AreEqual(ClaimDenial.None, Evaluate(Char1, Account1, "", claimedIps: Strings(Ip1)));
        }

        [TestMethod]
        public void Evaluate_WhitespaceIp_SkipsIpRule()
        {
            Assert.AreEqual(ClaimDenial.None, Evaluate(Char1, Account1, "   ", claimedIps: Strings(Ip1)));
        }

        [TestMethod]
        public void Evaluate_IpWithSurroundingWhitespace_StillMatches()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp,
                Evaluate(Char1, Account1, "  " + Ip1 + "  ", claimedIps: Strings(Ip1)));
        }

        [TestMethod]
        public void Evaluate_ClaimedIpStoredWithWhitespace_StillMatches()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp,
                Evaluate(Char1, Account1, Ip1, claimedIps: Strings(" " + Ip1 + " ")));
        }

        [TestMethod]
        public void Evaluate_Ipv6DifferentCase_StillMatches()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp,
                Evaluate(Char1, Account1, "FE80::1", claimedIps: Strings("fe80::1")));
        }

        [TestMethod]
        public void Evaluate_ExemptIp_SkipsIpRuleEvenWhenClaimed()
        {
            Assert.AreEqual(ClaimDenial.None,
                Evaluate(Char1, Account1, Ip1, claimedIps: Strings(Ip1), exemptIps: Strings(Ip1)));
        }

        [TestMethod]
        public void Evaluate_ExemptIpDifferentCase_StillExempt()
        {
            Assert.AreEqual(ClaimDenial.None,
                Evaluate(Char1, Account1, "FE80::1", claimedIps: Strings("fe80::1"), exemptIps: Strings("Fe80::1")));
        }

        [TestMethod]
        public void Evaluate_ExemptIp_DoesNotExemptTheCharacterOrAccountRules()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedAccount,
                Evaluate(Char1, Account1, Ip1, claimedAccounts: Uints(Account1),
                    claimedIps: Strings(Ip1), exemptIps: Strings(Ip1)));
        }

        [TestMethod]
        public void Evaluate_ExemptListDoesNotContainThisIp_StillDenies()
        {
            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp,
                Evaluate(Char1, Account1, Ip1, claimedIps: Strings(Ip1), exemptIps: Strings(Ip2)));
        }

        [TestMethod]
        public void Evaluate_ClaimedIpsContainsNullEntry_DoesNotThrow()
        {
            Assert.AreEqual(ClaimDenial.None,
                Evaluate(Char1, Account1, Ip1, claimedIps: Strings(null, Ip2)));
        }

        // ---- the two-call decomposition the handler relies on -----------------------------------------

        // WorldEventCacheHandler splits Evaluate into two calls so the account SIBLING check (which needs
        // PlayerManager and therefore cannot live in this pure class) can run between the account rule and
        // the IP rule, keeping the reported reason in the fixed 5.6 order. These two tests pin the property
        // that makes the split safe: each call only ever returns denials from the gates it was given.

        [TestMethod]
        public void Evaluate_WithIpGateSuppressed_NeverReportsAnIpDenial()
        {
            var denial = Evaluate(Char1, Account1, Ip1, claimedIps: Strings(Ip1),
                gateByCharacter: true, gateByAccount: true, gateByIp: false);

            Assert.AreEqual(ClaimDenial.None, denial);
        }

        [TestMethod]
        public void Evaluate_WithOnlyTheIpGate_NeverReportsACharacterOrAccountDenial()
        {
            var denial = Evaluate(Char1, Account1, Ip1,
                claimedCharacters: Uints(Char1), claimedAccounts: Uints(Account1),
                gateByCharacter: false, gateByAccount: false, gateByIp: true);

            Assert.AreEqual(ClaimDenial.None, denial);
        }

        [TestMethod]
        public void Evaluate_TwoCallDecomposition_MatchesTheSingleCallAnswer()
        {
            // With no sibling rule in play the split must be indistinguishable from one call.
            foreach (var charClaimed in new[] { false, true })
            foreach (var accountClaimed in new[] { false, true })
            foreach (var ipClaimed in new[] { false, true })
            {
                var claimedCharacters = charClaimed ? Uints(Char1) : Uints();
                var claimedAccounts = accountClaimed ? Uints(Account1) : Uints();
                var claimedIps = ipClaimed ? Strings(Ip1) : Strings();

                var single = WorldEventClaimRules.Evaluate(Char1, Account1, Ip1,
                    claimedCharacters, claimedAccounts, claimedIps, Strings(), true, true, true);

                var first = WorldEventClaimRules.Evaluate(Char1, Account1, Ip1,
                    claimedCharacters, claimedAccounts, claimedIps, Strings(), true, true, false);

                var split = first != ClaimDenial.None
                    ? first
                    : WorldEventClaimRules.Evaluate(Char1, Account1, Ip1,
                        claimedCharacters, claimedAccounts, claimedIps, Strings(), false, false, true);

                Assert.AreEqual(single, split,
                    $"charClaimed={charClaimed} accountClaimed={accountClaimed} ipClaimed={ipClaimed}");
            }
        }

        // ---- full combination matrix ------------------------------------------------------------------

        /// <summary>
        /// Every combination of (character claimed, account claimed, IP claimed) x (each gate on/off): the
        /// expected answer is the first ON gate whose set contains the value, in the fixed order.
        /// </summary>
        [TestMethod]
        public void Evaluate_FullCombinationMatrix_MatchesFirstFiringEnabledRule()
        {
            var cases = 0;

            foreach (var charClaimed in new[] { false, true })
            foreach (var accountClaimed in new[] { false, true })
            foreach (var ipClaimed in new[] { false, true })
            foreach (var gateChar in new[] { false, true })
            foreach (var gateAccount in new[] { false, true })
            foreach (var gateIp in new[] { false, true })
            {
                var expected = ClaimDenial.None;

                if (gateChar && charClaimed)
                    expected = ClaimDenial.AlreadyClaimedCharacter;
                else if (gateAccount && accountClaimed)
                    expected = ClaimDenial.AlreadyClaimedAccount;
                else if (gateIp && ipClaimed)
                    expected = ClaimDenial.AlreadyClaimedIp;

                var actual = Evaluate(Char1, Account1, Ip1,
                    claimedCharacters: charClaimed ? Uints(Char1) : Uints(),
                    claimedAccounts: accountClaimed ? Uints(Account1) : Uints(),
                    claimedIps: ipClaimed ? Strings(Ip1) : Strings(),
                    gateByCharacter: gateChar, gateByAccount: gateAccount, gateByIp: gateIp);

                Assert.AreEqual(expected, actual,
                    $"charClaimed={charClaimed} accountClaimed={accountClaimed} ipClaimed={ipClaimed} " +
                    $"gateChar={gateChar} gateAccount={gateAccount} gateIp={gateIp}");

                cases++;
            }

            Assert.AreEqual(64, cases, "the matrix should cover all 2^6 combinations");
        }

        // ---- ParseExemptIps ---------------------------------------------------------------------------

        [TestMethod]
        public void ParseExemptIps_Null_ReturnsEmptySet()
        {
            var parsed = WorldEventClaimRules.ParseExemptIps(null);

            Assert.IsNotNull(parsed);
            Assert.AreEqual(0, parsed.Count);
        }

        [TestMethod]
        public void ParseExemptIps_Empty_ReturnsEmptySet()
        {
            Assert.AreEqual(0, WorldEventClaimRules.ParseExemptIps("").Count);
            Assert.AreEqual(0, WorldEventClaimRules.ParseExemptIps("   ").Count);
        }

        [TestMethod]
        public void ParseExemptIps_Comma_Splits()
        {
            var parsed = WorldEventClaimRules.ParseExemptIps("192.168.0.10,192.168.0.11");

            Assert.AreEqual(2, parsed.Count);
            Assert.IsTrue(parsed.Contains("192.168.0.10"));
            Assert.IsTrue(parsed.Contains("192.168.0.11"));
        }

        [TestMethod]
        public void ParseExemptIps_SemicolonAndWhitespace_Split()
        {
            var parsed = WorldEventClaimRules.ParseExemptIps("192.168.0.10; 192.168.0.11 192.168.0.12");

            Assert.AreEqual(3, parsed.Count);
            Assert.IsTrue(parsed.Contains("192.168.0.12"));
        }

        [TestMethod]
        public void ParseExemptIps_EmptyEntries_AreDropped()
        {
            var parsed = WorldEventClaimRules.ParseExemptIps(",,192.168.0.10,;; ,192.168.0.11,,");

            Assert.AreEqual(2, parsed.Count);
            Assert.IsFalse(parsed.Any(string.IsNullOrWhiteSpace));
        }

        [TestMethod]
        public void ParseExemptIps_EntriesAreTrimmed()
        {
            var parsed = WorldEventClaimRules.ParseExemptIps("  192.168.0.10  ,\t192.168.0.11\r\n");

            Assert.IsTrue(parsed.Contains("192.168.0.10"));
            Assert.IsTrue(parsed.Contains("192.168.0.11"));
        }

        [TestMethod]
        public void ParseExemptIps_IsCaseInsensitive()
        {
            var parsed = WorldEventClaimRules.ParseExemptIps("FE80::1");

            Assert.IsTrue(parsed.Contains("fe80::1"));
        }

        [TestMethod]
        public void ParseExemptIps_Duplicates_Collapse()
        {
            Assert.AreEqual(1, WorldEventClaimRules.ParseExemptIps("10.0.0.1, 10.0.0.1 ,10.0.0.1").Count);
        }

        [TestMethod]
        public void ParseExemptIps_FeedsEvaluateDirectly()
        {
            var exempt = WorldEventClaimRules.ParseExemptIps(" 192.168.0.10 ; 10.0.0.1 ");

            Assert.AreEqual(ClaimDenial.None,
                Evaluate(Char1, Account1, Ip1, claimedIps: Strings(Ip1), exemptIps: exempt));

            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp,
                Evaluate(Char1, Account1, Ip2, claimedIps: Strings(Ip2), exemptIps: exempt));
        }

        // ---- NormalizeIp ------------------------------------------------------------------------------

        [TestMethod]
        public void NormalizeIp_NullOrWhitespace_IsEmpty()
        {
            Assert.AreEqual("", WorldEventClaimRules.NormalizeIp(null));
            Assert.AreEqual("", WorldEventClaimRules.NormalizeIp(""));
            Assert.AreEqual("", WorldEventClaimRules.NormalizeIp(" \t "));
        }

        [TestMethod]
        public void NormalizeIp_Trims()
        {
            Assert.AreEqual("10.0.0.1", WorldEventClaimRules.NormalizeIp("  10.0.0.1 "));
        }

        // ---- DenialMessage ----------------------------------------------------------------------------

        [TestMethod]
        public void DenialMessage_NotRewarding_NamesTheCacheAndIsSealed()
        {
            Assert.AreEqual("The Weave Cache is sealed - the event's claim window is closed.",
                WorldEventClaimRules.DenialMessage(ClaimDenial.NotRewarding, "Weave Cache"));
        }

        [TestMethod]
        public void DenialMessage_NotRewarding_FallsBackToTheDefaultName()
        {
            Assert.AreEqual("The Weave Cache is sealed - the event's claim window is closed.",
                WorldEventClaimRules.DenialMessage(ClaimDenial.NotRewarding, null));

            Assert.AreEqual("The Weave Cache is sealed - the event's claim window is closed.",
                WorldEventClaimRules.DenialMessage(ClaimDenial.NotRewarding, "   "));
        }

        [TestMethod]
        public void DenialMessage_Character()
        {
            Assert.AreEqual("You have already claimed your reward from this event.",
                WorldEventClaimRules.DenialMessage(ClaimDenial.AlreadyClaimedCharacter, "Weave Cache"));
        }

        [TestMethod]
        public void DenialMessage_Account()
        {
            Assert.AreEqual("Another character on your account has already claimed this event's reward.",
                WorldEventClaimRules.DenialMessage(ClaimDenial.AlreadyClaimedAccount, "Weave Cache"));
        }

        [TestMethod]
        public void DenialMessage_Ip()
        {
            Assert.AreEqual("A reward for this event has already been claimed from your connection.",
                WorldEventClaimRules.DenialMessage(ClaimDenial.AlreadyClaimedIp, "Weave Cache"));
        }

        [TestMethod]
        public void DenialMessage_None_IsEmpty()
        {
            Assert.AreEqual("", WorldEventClaimRules.DenialMessage(ClaimDenial.None, "Weave Cache"));
        }

        [TestMethod]
        public void DenialMessage_EveryDenialHasAMessageAndIsAscii()
        {
            foreach (ClaimDenial denial in Enum.GetValues(typeof(ClaimDenial)))
            {
                var message = WorldEventClaimRules.DenialMessage(denial, "Weave Cache");

                Assert.IsNotNull(message, $"{denial} returned null");

                if (denial != ClaimDenial.None)
                    Assert.IsTrue(message.Length > 0, $"{denial} has no player-facing message");

                foreach (var c in message)
                    Assert.IsTrue(c < 128, $"{denial} message contains a non-ASCII character '{c}'");
            }
        }

        // ---- PaysCoal (participation payout, repo owner decision 2026-09-07) --------------------------

        [TestMethod]
        public void PaysCoal_Participant_NeverGetsCoal_EvenWithFeatureOnAndValidCoalWcid()
        {
            Assert.IsFalse(WorldEventClaimRules.PaysCoal(featureEnabled: true, coalWcid: 12345, hasCredit: true));
        }

        [TestMethod]
        public void PaysCoal_NonParticipant_FeatureOnAndCoalWcidSet_GetsCoal()
        {
            Assert.IsTrue(WorldEventClaimRules.PaysCoal(featureEnabled: true, coalWcid: 12345, hasCredit: false));
        }

        [TestMethod]
        public void PaysCoal_NonParticipant_CoalWcidZero_DoesNotGetCoal()
        {
            // Fail-open: an axis that never configured coalWcid must never punish anyone.
            Assert.IsFalse(WorldEventClaimRules.PaysCoal(featureEnabled: true, coalWcid: 0, hasCredit: false));
        }

        [TestMethod]
        public void PaysCoal_NonParticipant_FeatureDisabled_DoesNotGetCoal()
        {
            // Kill switch: false restores the pre-change behaviour regardless of coalWcid.
            Assert.IsFalse(WorldEventClaimRules.PaysCoal(featureEnabled: false, coalWcid: 12345, hasCredit: false));
        }

        // ---- cache-count arithmetic (WorldEventRewardDelivery, the one pure piece of SpawnCaches) ------

        [TestMethod]
        public void ComputeCacheCount_EmptyAudience_StillPlacesOne()
        {
            Assert.AreEqual(1, WorldEventRewardDelivery.ComputeCacheCount(0, 8));
        }

        [TestMethod]
        public void ComputeCacheCount_RoundsUp()
        {
            Assert.AreEqual(1, WorldEventRewardDelivery.ComputeCacheCount(1, 8));
            Assert.AreEqual(1, WorldEventRewardDelivery.ComputeCacheCount(8, 8));
            Assert.AreEqual(2, WorldEventRewardDelivery.ComputeCacheCount(9, 8));
            Assert.AreEqual(3, WorldEventRewardDelivery.ComputeCacheCount(17, 8));
        }

        [TestMethod]
        public void ComputeCacheCount_NonPositivePerCache_UsesTheDefaultInsteadOfDividingByZero()
        {
            Assert.AreEqual(2, WorldEventRewardDelivery.ComputeCacheCount(9, 0));
            Assert.AreEqual(2, WorldEventRewardDelivery.ComputeCacheCount(9, -4));
        }

        /// <summary>
        /// The 24 ceiling is a deliberate constant, not a tunable (orchestrator call). This test is the
        /// tripwire: changing MaxCachesPerRun must be a conscious edit here as well.
        /// </summary>
        [TestMethod]
        public void ComputeCacheCount_IsClampedAtTwentyFour()
        {
            Assert.AreEqual(24, WorldEventRewardDelivery.ComputeCacheCount(10000, 1));
            Assert.AreEqual(24, WorldEventRewardDelivery.ComputeCacheCount(192, 8));
            Assert.AreEqual(24, WorldEventRewardDelivery.ComputeCacheCount(193, 8), "one over the ceiling still clamps");
            Assert.AreEqual(23, WorldEventRewardDelivery.ComputeCacheCount(184, 8), "one under the ceiling is untouched");
        }

        [TestMethod]
        public void ComputeCacheCount_ReportsTheUnclampedCountWhenItClamps()
        {
            var count = WorldEventRewardDelivery.ComputeCacheCount(1000, 1, out var unclamped);

            Assert.AreEqual(24, count);
            Assert.AreEqual(1000, unclamped, "the caller needs the raw count to log the clamp");
        }

        [TestMethod]
        public void ComputeCacheCount_UnclampedEqualsCountWhenNoClampFires()
        {
            var count = WorldEventRewardDelivery.ComputeCacheCount(17, 8, out var unclamped);

            Assert.AreEqual(3, count);
            Assert.AreEqual(count, unclamped, "no clamp fired, so nothing should be logged");
        }

        [TestMethod]
        public void ComputeCacheCount_UnclampedIsAtLeastOneForAnEmptyAudience()
        {
            var count = WorldEventRewardDelivery.ComputeCacheCount(0, 8, out var unclamped);

            Assert.AreEqual(1, count);
            Assert.AreEqual(1, unclamped);
        }

        [TestMethod]
        public void ComputeCacheCount_BothOverloadsAgree()
        {
            foreach (var audience in new[] { 0, 1, 7, 8, 9, 100, 1000 })
            foreach (var per in new[] { -1, 0, 1, 8, 50 })
            {
                Assert.AreEqual(
                    WorldEventRewardDelivery.ComputeCacheCount(audience, per),
                    WorldEventRewardDelivery.ComputeCacheCount(audience, per, out _),
                    $"audience={audience} per={per}");
            }
        }

        [TestMethod]
        public void ComputeCacheCount_NegativeAudience_StillPlacesOne()
        {
            Assert.AreEqual(1, WorldEventRewardDelivery.ComputeCacheCount(-5, 8));
        }
    }
}
