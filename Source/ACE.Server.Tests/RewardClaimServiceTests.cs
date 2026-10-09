using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.RewardClaims;

namespace ACE.Server.Tests
{
    /// <summary>
    /// In-memory reward_claim with the real table's two constraints: PRIMARY KEY (key, account) and
    /// UNIQUE KEY (key, ip) under which NULL ips never collide. Optional failure modes model a lost ack
    /// (commit, then report a duplicate), a throwing insert, and a failing read.
    /// </summary>
    internal sealed class FakeRewardClaimStore : IRewardClaimStore
    {
        public readonly List<RewardClaim> Rows = new List<RewardClaim>();

        public int InsertCalls;
        public int ReadCalls;
        public int DeleteCalls;

        public bool CommitThenReportDuplicate;
        public bool ThrowOnInsert;
        public bool FailReads;

        public RewardClaimInsertResult TryInsert(RewardClaim row)
        {
            InsertCalls++;

            if (ThrowOnInsert)
                throw new InvalidOperationException("store down");

            if (Rows.Any(r => r.ClaimKey == row.ClaimKey && r.AccountId == row.AccountId))
                return RewardClaimInsertResult.Duplicate;

            if (row.IpKey != null && Rows.Any(r => r.ClaimKey == row.ClaimKey && r.IpKey == row.IpKey))
                return RewardClaimInsertResult.Duplicate;

            Rows.Add(Copy(row));

            return CommitThenReportDuplicate ? RewardClaimInsertResult.Duplicate : RewardClaimInsertResult.Inserted;
        }

        public bool TryGetClaims(string claimKey, uint? accountId, string ipKey, out List<RewardClaim> rows)
        {
            ReadCalls++;
            rows = null;

            if (FailReads)
                return false;

            rows = Match(claimKey, accountId, ipKey).Select(Copy).ToList();
            return true;
        }

        public bool Delete(string claimKey, uint? accountId, string ipKey, out int deleted)
        {
            DeleteCalls++;
            deleted = 0;

            if (!accountId.HasValue && ipKey == null)
                return false;

            var doomed = Match(claimKey, accountId, ipKey).ToList();
            foreach (var row in doomed)
                Rows.Remove(row);

            deleted = doomed.Count;
            return true;
        }

        private IEnumerable<RewardClaim> Match(string claimKey, uint? accountId, string ipKey)
            => Rows.Where(r => r.ClaimKey == claimKey
                && ((!accountId.HasValue && ipKey == null)
                    || (accountId.HasValue && r.AccountId == accountId.Value)
                    || (ipKey != null && r.IpKey == ipKey)));

        private static RewardClaim Copy(RewardClaim r) => new RewardClaim
        {
            ClaimKey = r.ClaimKey,
            AccountId = r.AccountId,
            IpKey = r.IpKey,
            IpAddress = r.IpAddress,
            CharacterId = r.CharacterId,
            NpcWcid = r.NpcWcid,
            ClaimToken = r.ClaimToken,
            ClaimedAt = r.ClaimedAt,
        };
    }

    [TestClass]
    public class RewardClaimServiceTests
    {
        private FakeRewardClaimStore store;

        [TestInitialize]
        public void Init()
        {
            store = new FakeRewardClaimStore();
            RewardClaimService.ResetForTesting(store);
            RewardClaimService.EnabledSource = () => true;
            RewardClaimService.ActiveSpeedSeasonSource = () => 4;
        }

        [TestCleanup]
        public void Cleanup()
        {
            RewardClaimService.ResetForTesting(null);
        }

        private static RewardClaimRequest Request(string key, uint account, string ip, bool exempt = false, uint? npc = null, bool room = true)
        {
            RewardClaimAllowlist.TryGet(key, out var entry);

            return new RewardClaimRequest
            {
                ClaimKey = key,
                EmoterWcid = npc ?? entry?.NpcWcid ?? 1002511,
                AccountId = account,
                CharacterId = 0x50000000 + account,
                CharacterName = $"Char{account}",
                Address = ip == null ? null : IPAddress.Parse(ip),
                IsExempt = () => exempt,
                HasRoomForReward = _ => room,
            };
        }

        // ---------------------------------------------------------------- allowlist

        [TestMethod]
        public void Allowlist_IsExactlyTheHammerAndProvingGroundsKeys_WithPinnedWcidsAndPeriods()
        {
            const ClaimPeriod F = ClaimPeriod.Forever;
            const ClaimPeriod S = ClaimPeriod.SpeedSeason;

            var expected = new Dictionary<string, (uint Npc, uint Reward, ClaimPeriod Period)>(StringComparer.Ordinal)
            {
                ["AssayRowTigerEyeHammer"] = (1002511, 1001918, F),
                ["AssayRowTourmalineHammer"] = (1002512, 1001910, F),
                ["AssayRowSerpentineHammer"] = (1002513, 1001916, F),
                ["AssayRowAmethystHammer"] = (1002514, 1001911, F),
                ["AssayRowObsidianHammer"] = (1002516, 1001912, F),

                ["ProvingGroundsAttackTier1@claim"] = (1000303, 20630, F),
                ["ProvingGroundsAttackTier2@claim"] = (1000303, 20630, F),
                ["ProvingGroundsAttackTier3@claim"] = (1000303, 20630, F),
                ["ProvingGroundsDefenseTier1@claim"] = (1000306, 20630, F),
                ["ProvingGroundsDefenseTier2@claim"] = (1000306, 20630, F),
                ["ProvingGroundsDefenseTier3@claim"] = (1000306, 20630, F),
                ["ProvingGroundsWaveTier1@claim"] = (1001551, 20630, F),
                ["ProvingGroundsWaveTier2@claim"] = (1001551, 20630, F),
                ["ProvingGroundsWaveTier3@claim"] = (1001551, 20630, F),
                ["ProvingGroundsSpeedTier1@claim"] = (1003002, 20630, S),
                ["ProvingGroundsSpeedTier2@claim"] = (1003002, 20630, S),
                ["ProvingGroundsSpeedTier3@claim"] = (1003002, 20630, S),
            };

            Assert.IsTrue(new HashSet<string>(expected.Keys, StringComparer.Ordinal).SetEquals(RewardClaimAllowlist.Entries.Keys),
                $"allowlist keys were: {string.Join(", ", RewardClaimAllowlist.Entries.Keys)}");
            Assert.AreEqual(17, RewardClaimAllowlist.Entries.Count);

            foreach (var pair in expected)
            {
                Assert.IsTrue(RewardClaimAllowlist.TryGet(pair.Key, out var entry), pair.Key);
                Assert.AreEqual(pair.Key, entry.Key);
                Assert.AreEqual(pair.Value.Npc, entry.NpcWcid, $"{pair.Key} npc");
                Assert.AreEqual(pair.Value.Reward, entry.RewardWcid, $"{pair.Key} reward");
                Assert.AreEqual(pair.Value.Period, entry.Period, $"{pair.Key} period");
            }
        }

        [TestMethod]
        public void Allowlist_EveryStoredKey_FitsClaimKeyColumn_EvenWithTheLongestSuffix()
        {
            // reward_claim.claim_Key is varchar(64). Worst-case suffixes: a negative int season id and a full date.
            foreach (var entry in RewardClaimAllowlist.Entries.Values)
            {
                var worst = RewardClaimRules.StoredClaimKey(entry, int.MinValue, new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
                Assert.IsNotNull(worst, entry.Key);
                Assert.IsTrue(worst.Length <= RewardClaimRules.MaxStoredKeyLength, $"{worst} is {worst.Length} chars");
            }
        }

        [TestMethod]
        public void Allowlist_IsOrdinal()
        {
            Assert.IsFalse(RewardClaimAllowlist.TryGet("assayrowtigereyehammer", out _));
            Assert.IsFalse(RewardClaimAllowlist.TryGet("AssayRowTigerEyeHammer ", out _));
            Assert.IsFalse(RewardClaimAllowlist.TryGet(null, out _));
            Assert.IsFalse(RewardClaimAllowlist.TryGet("", out _));
        }

        [TestMethod]
        public void EmoteType_ClaimRewardOnce_Is9003()
        {
            Assert.AreEqual(9003, Convert.ToInt32(EmoteType.ClaimRewardOnce));
        }

        // ---------------------------------------------------------------- ungated paths

        [TestMethod]
        public void UnlistedKey_UngatedToTestSuccess_NoStoreCall()
        {
            var decision = RewardClaimService.Evaluate(Request("AssayRowGrainHammer", 7, "10.0.0.1", npc: 1002510));

            Assert.AreEqual(RewardClaimBranch.TestSuccess, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultUngatedUnlisted, decision.LogResult);
            Assert.IsNull(decision.Message);
            Assert.AreEqual(0, store.InsertCalls + store.ReadCalls + store.DeleteCalls);
            Assert.AreEqual(0, store.Rows.Count);
        }

        [TestMethod]
        public void WrongNpc_UngatedToTestSuccess_NoStoreCall()
        {
            // the Tourmaline NPC running the Tiger Eye key
            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1", npc: 1002512));

            Assert.AreEqual(RewardClaimBranch.TestSuccess, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultUngatedWrongNpc, decision.LogResult);
            Assert.AreEqual(0, store.InsertCalls + store.ReadCalls);
            Assert.AreEqual(0, store.Rows.Count);
        }

        [TestMethod]
        public void Disabled_UngatedToTestSuccess_NoStoreCall_EvenAfterAPriorClaim()
        {
            Assert.AreEqual(RewardClaimBranch.TestSuccess, RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1")).Branch);
            Assert.AreEqual(1, store.InsertCalls);

            RewardClaimService.EnabledSource = () => false;

            var roomChecked = false;
            var exemptChecked = false;
            var request = Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1");
            request.HasRoomForReward = _ => { roomChecked = true; return true; };
            request.IsExempt = () => { exemptChecked = true; return false; };

            var decision = RewardClaimService.Evaluate(request);

            Assert.AreEqual(RewardClaimBranch.TestSuccess, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultUngatedDisabled, decision.LogResult);
            Assert.AreEqual(1, store.InsertCalls, "disabled must make no store call");
            Assert.AreEqual(0, store.ReadCalls);
            Assert.IsFalse(roomChecked);
            Assert.IsFalse(exemptChecked);
        }

        [TestMethod]
        public void NoAccount_NoBranch_NoStoreCall()
        {
            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 0, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.None, decision.Branch);
            Assert.AreEqual(RewardClaimService.StoreFailedMessage, decision.Message);
            Assert.AreEqual(0, store.InsertCalls);
        }

        // ---------------------------------------------------------------- the gate

        [TestMethod]
        public void FirstClaim_Granted_WritesOneRow()
        {
            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.TestSuccess, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultGranted, decision.LogResult);
            Assert.IsNull(decision.Message);

            var row = store.Rows.Single();
            Assert.AreEqual(RewardClaimAllowlist.TigerEyeHammer, row.ClaimKey);
            Assert.AreEqual(7u, row.AccountId);
            Assert.AreEqual("10.0.0.1", row.IpKey);
            Assert.AreEqual("10.0.0.1", row.IpAddress);
            Assert.AreEqual(1002511u, row.NpcWcid);
            Assert.AreEqual(0x50000007u, row.CharacterId);
            Assert.AreEqual(32, row.ClaimToken.Length);
        }

        [TestMethod]
        public void SameAccount_DifferentIp_DeniedAccount()
        {
            RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1"));

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.2"));

            Assert.AreEqual(RewardClaimBranch.TestFailure, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultDeniedAccount, decision.LogResult);
            Assert.IsNull(decision.Message);
            Assert.AreEqual(1, store.Rows.Count);
        }

        [TestMethod]
        public void SameIp_DifferentAccount_DeniedIp()
        {
            RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1"));

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 8, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.TestFailure, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultDeniedIp, decision.LogResult);
            Assert.AreEqual(1, store.Rows.Count);
        }

        [TestMethod]
        public void SameIp_ViaIpv4MappedAddress_DeniedIp()
        {
            RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1"));

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 8, "::ffff:10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.TestFailure, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultDeniedIp, decision.LogResult);
        }

        [TestMethod]
        public void Exempt_Granted_StoresNullIp_AndDoesNotConsumeTheIp()
        {
            var exemptDecision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1", exempt: true));

            Assert.AreEqual(RewardClaimBranch.TestSuccess, exemptDecision.Branch);
            var exemptRow = store.Rows.Single();
            Assert.IsNull(exemptRow.IpKey, "an exempt claim stores ip_Key NULL");
            Assert.AreEqual("10.0.0.1", exemptRow.IpAddress, "the audit address is still recorded");

            var other = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 8, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.TestSuccess, other.Branch, "a non-exempt account on the same IP is not blocked by an exempt claim");
            Assert.AreEqual(2, store.Rows.Count);
        }

        [TestMethod]
        public void TwoExemptAccounts_SameIp_BothGranted()
        {
            Assert.AreEqual(RewardClaimBranch.TestSuccess, RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1", exempt: true)).Branch);
            Assert.AreEqual(RewardClaimBranch.TestSuccess, RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 8, "10.0.0.1", exempt: true)).Branch);
            Assert.AreEqual(2, store.Rows.Count(r => r.IpKey == null));
        }

        [TestMethod]
        public void Exempt_StillDeniedOnAccount()
        {
            RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1", exempt: true));

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.2", exempt: true));

            Assert.AreEqual(RewardClaimBranch.TestFailure, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultDeniedAccount, decision.LogResult);
        }

        [TestMethod]
        public void KeysAreIndependent()
        {
            foreach (var key in RewardClaimAllowlist.Entries.Keys)
            {
                var decision = RewardClaimService.Evaluate(Request(key, 7, "10.0.0.1"));
                Assert.AreEqual(RewardClaimBranch.TestSuccess, decision.Branch, key);
                Assert.AreEqual(RewardClaimService.ResultGranted, decision.LogResult, key);
            }

            Assert.AreEqual(RewardClaimAllowlist.Entries.Count, store.Rows.Count);
        }

        // ---------------------------------------------------------------- claim periods

        [TestMethod]
        public void SpeedSeason_StoredKey_CarriesTheActiveSeason()
        {
            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.ProvingGroundsSpeedTier1, 7, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.TestSuccess, decision.Branch);
            Assert.AreEqual("ProvingGroundsSpeedTier1@claim#S4", store.Rows.Single().ClaimKey);
            Assert.AreEqual(1003002u, store.Rows.Single().NpcWcid);
        }

        [TestMethod]
        public void SpeedSeason_SameIp_TwoDifferentSeasons_BothGranted()
        {
            RewardClaimService.ActiveSpeedSeasonSource = () => 4;
            var first = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.ProvingGroundsSpeedTier2, 7, "10.0.0.1"));

            RewardClaimService.ActiveSpeedSeasonSource = () => 5;
            var second = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.ProvingGroundsSpeedTier2, 7, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.TestSuccess, first.Branch);
            Assert.AreEqual(RewardClaimBranch.TestSuccess, second.Branch, "a new season re-opens the tier for the same account and IP");
            CollectionAssert.AreEquivalent(new[] { "ProvingGroundsSpeedTier2@claim#S4", "ProvingGroundsSpeedTier2@claim#S5" }, store.Rows.Select(r => r.ClaimKey).ToList());
        }

        [TestMethod]
        public void SpeedSeason_SameSeason_SecondAccountSameIp_DeniedIp()
        {
            RewardClaimService.ActiveSpeedSeasonSource = () => 4;
            Assert.AreEqual(RewardClaimBranch.TestSuccess, RewardClaimService.Evaluate(Request(RewardClaimAllowlist.ProvingGroundsSpeedTier2, 7, "10.0.0.1")).Branch);

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.ProvingGroundsSpeedTier2, 8, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.TestFailure, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultDeniedIp, decision.LogResult);
            Assert.AreEqual(1, store.Rows.Count);
        }

        [TestMethod]
        public void SpeedSeason_NoActiveSeason_FailsClosed_NoBranch_NoCallbacks_NoStoreCall()
        {
            RewardClaimService.ActiveSpeedSeasonSource = () => null;

            var roomChecked = false;
            var request = Request(RewardClaimAllowlist.ProvingGroundsSpeedTier1, 7, "10.0.0.1");
            request.HasRoomForReward = _ => { roomChecked = true; return true; };

            var decision = RewardClaimService.Evaluate(request);

            Assert.AreEqual(RewardClaimBranch.None, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultFailed, decision.LogResult);
            Assert.AreEqual(RewardClaimService.NoActiveSeasonMessage, decision.Message);
            Assert.AreNotEqual(RewardClaimAllowlist.ProvingGroundsStoreFailedMessage, decision.Message);
            Assert.IsFalse(roomChecked);
            Assert.AreEqual(0, store.InsertCalls + store.ReadCalls);
        }

        [TestMethod]
        public void SpeedSeason_SeasonSourceThrows_FailsClosed_NoBranch_NoStoreCall()
        {
            RewardClaimService.ActiveSpeedSeasonSource = () => throw new InvalidOperationException("registry down");

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.ProvingGroundsSpeedTier1, 7, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.None, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultFailed, decision.LogResult);
            Assert.AreEqual(0, store.InsertCalls);
            Assert.AreEqual(0, store.Rows.Count);
        }

        [TestMethod]
        public void Forever_Key_IgnoresTheSeason()
        {
            RewardClaimService.ActiveSpeedSeasonSource = () => null;

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.ProvingGroundsAttackTier1, 7, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.TestSuccess, decision.Branch);
            Assert.AreEqual("ProvingGroundsAttackTier1@claim", store.Rows.Single().ClaimKey);
        }

        [TestMethod]
        public void ProvingGrounds_NoRoom_UsesTheProvingGroundsMessage()
        {
            var request = Request(RewardClaimAllowlist.ProvingGroundsWaveTier3, 7, "10.0.0.1", room: false);

            var decision = RewardClaimService.Evaluate(request);

            Assert.AreEqual(RewardClaimBranch.None, decision.Branch);
            Assert.AreEqual(RewardClaimAllowlist.ProvingGroundsNoRoomMessage, decision.Message);
        }

        [TestMethod]
        public void UtcDay_SameDayDenies_NextDayGrants()
        {
            // No allowlist entry is UtcDay yet, so this drives a stand-alone entry through the same
            // CurrentStoredKey + TryClaim path Evaluate uses, with the clock seam.
            var daily = new RewardClaimEntry("TestDailyReward@claim", 1, 2, ClaimPeriod.UtcDay);

            RewardClaimService.UtcNowSource = () => new DateTime(2026, 10, 2, 0, 0, 1, DateTimeKind.Utc);
            var day1 = RewardClaimService.CurrentStoredKey(daily);
            Assert.AreEqual("TestDailyReward@claim#D20261002", day1);
            Assert.AreEqual(RewardClaimOutcome.Granted, RewardClaimService.TryClaim(day1, 7, 0x50000007, 1, "10.0.0.1", "10.0.0.1"));

            RewardClaimService.UtcNowSource = () => new DateTime(2026, 10, 2, 23, 59, 59, DateTimeKind.Utc);
            var laterSameDay = RewardClaimService.CurrentStoredKey(daily);
            Assert.AreEqual(day1, laterSameDay);
            Assert.AreEqual(RewardClaimOutcome.DeniedAccount, RewardClaimService.TryClaim(laterSameDay, 7, 0x50000007, 1, "10.0.0.2", "10.0.0.2"), "same account, same day");
            Assert.AreEqual(RewardClaimOutcome.DeniedIp, RewardClaimService.TryClaim(laterSameDay, 8, 0x50000008, 1, "10.0.0.1", "10.0.0.1"), "same IP, same day");

            RewardClaimService.UtcNowSource = () => new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
            var day2 = RewardClaimService.CurrentStoredKey(daily);
            Assert.AreEqual("TestDailyReward@claim#D20261003", day2);
            Assert.AreEqual(RewardClaimOutcome.Granted, RewardClaimService.TryClaim(day2, 7, 0x50000007, 1, "10.0.0.1", "10.0.0.1"), "the next UTC day re-opens it");
        }

        [TestMethod]
        public void UtcDay_UsesUtcDate_NotLocal()
        {
            var daily = new RewardClaimEntry("TestDailyReward@claim", 1, 2, ClaimPeriod.UtcDay);
            var utc = new DateTime(2026, 10, 2, 23, 30, 0, DateTimeKind.Utc);

            Assert.AreEqual("TestDailyReward@claim#D20261002", RewardClaimRules.StoredClaimKey(daily, null, utc));
            Assert.AreEqual("TestDailyReward@claim#D20261002", RewardClaimRules.StoredClaimKey(daily, null, utc.ToLocalTime()));
        }

        [TestMethod]
        public void StoredClaimKey_Rules()
        {
            var forever = new RewardClaimEntry("K", 1, 2);
            var season = new RewardClaimEntry("K", 1, 2, ClaimPeriod.SpeedSeason);
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual("K", RewardClaimRules.StoredClaimKey(forever, 9, now));
            Assert.AreEqual("K#S9", RewardClaimRules.StoredClaimKey(season, 9, now));
            Assert.IsNull(RewardClaimRules.StoredClaimKey(season, null, now));
            Assert.IsNull(RewardClaimRules.StoredClaimKey(null, 9, now));
            Assert.IsNull(RewardClaimRules.StoredClaimKey(new RewardClaimEntry(new string('x', 63), 1, 2, ClaimPeriod.SpeedSeason), 1, now), "65 chars must be refused, not truncated");
        }

        [TestMethod]
        public void TryParseStoredKey_Rules()
        {
            Assert.IsTrue(RewardClaimRules.TryParseStoredKey("AssayRowTigerEyeHammer", out var assay));
            Assert.AreEqual(RewardClaimAllowlist.TigerEyeHammer, assay.Key);

            Assert.IsTrue(RewardClaimRules.TryParseStoredKey("ProvingGroundsSpeedTier3@claim#S12", out var speed));
            Assert.AreEqual(RewardClaimAllowlist.ProvingGroundsSpeedTier3, speed.Key);

            Assert.IsFalse(RewardClaimRules.TryParseStoredKey("ProvingGroundsSpeedTier3@claim", out _), "a periodic key's bare form is not a stored key");
            Assert.IsFalse(RewardClaimRules.TryParseStoredKey("ProvingGroundsSpeedTier3@claim#S", out _));
            Assert.IsFalse(RewardClaimRules.TryParseStoredKey("ProvingGroundsSpeedTier3@claim#S012", out _));
            Assert.IsFalse(RewardClaimRules.TryParseStoredKey("ProvingGroundsSpeedTier3@claim#D20261002", out _), "wrong period suffix");
            Assert.IsFalse(RewardClaimRules.TryParseStoredKey("ProvingGroundsAttackTier1@claim#S4", out _), "a Forever key takes no suffix");
            Assert.IsFalse(RewardClaimRules.TryParseStoredKey("AssayRowTigerEyeHammer#S4", out _));
            Assert.IsFalse(RewardClaimRules.TryParseStoredKey("Nope#S4", out _));
            Assert.IsFalse(RewardClaimRules.TryParseStoredKey(null, out _));
        }

        [TestMethod]
        public void DuplicateCarryingOwnToken_IsGranted()
        {
            store.CommitThenReportDuplicate = true;

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.TestSuccess, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultGranted, decision.LogResult);
            Assert.AreEqual(1, store.ReadCalls, "a duplicate must be read back before it is judged");
        }

        [TestMethod]
        public void DuplicateAgainstAnotherToken_IsDenied_NotGranted()
        {
            RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1"));
            RewardClaimService.TokenSource = () => "ffffffffffffffffffffffffffffffff";

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.TestFailure, decision.Branch);
        }

        [TestMethod]
        public void StoreThrows_StoreFailed_NoBranch()
        {
            store.ThrowOnInsert = true;

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.None, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultFailed, decision.LogResult);
            Assert.AreEqual("The assay ledger could not be reached. Try the turn-in again shortly.", decision.Message);
        }

        [TestMethod]
        public void DuplicateButConflictReadFails_StoreFailed()
        {
            RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1"));
            store.FailReads = true;

            var decision = RewardClaimService.Evaluate(Request(RewardClaimAllowlist.TigerEyeHammer, 7, "10.0.0.1"));

            Assert.AreEqual(RewardClaimBranch.None, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultFailed, decision.LogResult);
        }

        [TestMethod]
        public void NoRoom_NoBranch_NoStoreCall_AndAskedAboutTheRightReward()
        {
            uint askedWcid = 0;
            var request = Request(RewardClaimAllowlist.ObsidianHammer, 7, "10.0.0.1");
            request.HasRoomForReward = wcid => { askedWcid = wcid; return false; };

            var decision = RewardClaimService.Evaluate(request);

            Assert.AreEqual(RewardClaimBranch.None, decision.Branch);
            Assert.AreEqual(RewardClaimService.ResultNoRoom, decision.LogResult);
            Assert.AreEqual("You need a free pack slot before the hammer can be handed over.", decision.Message);
            Assert.AreEqual(1001912u, askedWcid);
            Assert.AreEqual(0, store.InsertCalls);
        }

        [TestMethod]
        public void TransientMessages_AreAscii()
        {
            foreach (var message in new[] { RewardClaimService.NoRoomMessage, RewardClaimService.StoreFailedMessage, RewardClaimAllowlist.ProvingGroundsNoRoomMessage, RewardClaimAllowlist.ProvingGroundsStoreFailedMessage, RewardClaimService.NoActiveSeasonMessage })
                Assert.IsTrue(message.All(c => c >= 0x20 && c < 0x7F), message);
        }

        [TestMethod]
        public void ReadEnabledFromProperties_NeverThrows()
        {
            // No shard config in this host: an uncached read throws inside PropertyManager and must be absorbed.
            RewardClaimService.ReadEnabledFromProperties();
        }

        // ---------------------------------------------------------------- pure rules

        [TestMethod]
        public void NormalizeIpKey_Rules()
        {
            Assert.AreEqual("1.2.3.4", RewardClaimRules.NormalizeIpKey(IPAddress.Parse("1.2.3.4"), false));
            Assert.AreEqual("1.2.3.4", RewardClaimRules.NormalizeIpKey(IPAddress.Parse("::ffff:1.2.3.4"), false));
            Assert.AreEqual("2001:db8::1", RewardClaimRules.NormalizeIpKey(IPAddress.Parse("2001:DB8:0:0:0:0:0:1"), false));
            Assert.AreEqual("fe80::1", RewardClaimRules.NormalizeIpKey(IPAddress.Parse("fe80::1%3"), false));
            Assert.IsNull(RewardClaimRules.NormalizeIpKey(IPAddress.Parse("1.2.3.4"), true));
            Assert.IsNull(RewardClaimRules.NormalizeIpKey(null, false));
        }

        [TestMethod]
        public void Precheck_Order()
        {
            Assert.AreEqual(RewardClaimPrecheck.UngatedDisabled, RewardClaimRules.Precheck(false, "nope", 1, 0));
            Assert.AreEqual(RewardClaimPrecheck.UngatedUnlistedKey, RewardClaimRules.Precheck(true, "nope", 1002511, 0));
            Assert.AreEqual(RewardClaimPrecheck.UngatedWrongNpc, RewardClaimRules.Precheck(true, RewardClaimAllowlist.TigerEyeHammer, 1, 0));
            Assert.AreEqual(RewardClaimPrecheck.NoAccount, RewardClaimRules.Precheck(true, RewardClaimAllowlist.TigerEyeHammer, 1002511, 0));
            Assert.AreEqual(RewardClaimPrecheck.NeedsClaim, RewardClaimRules.Precheck(true, RewardClaimAllowlist.TigerEyeHammer, 1002511, 7));
        }

        [TestMethod]
        public void Interpret_Rules()
        {
            var own = new RewardClaim { ClaimKey = "k", AccountId = 7, IpKey = "1.1.1.1", ClaimToken = "mine" };
            var otherAccount = new RewardClaim { ClaimKey = "k", AccountId = 7, IpKey = "2.2.2.2", ClaimToken = "theirs" };
            var otherIp = new RewardClaim { ClaimKey = "k", AccountId = 8, IpKey = "1.1.1.1", ClaimToken = "theirs" };

            Assert.AreEqual(RewardClaimOutcome.Granted, RewardClaimRules.Interpret(RewardClaimInsertResult.Inserted, null, "mine", 7, "1.1.1.1"));
            Assert.AreEqual(RewardClaimOutcome.StoreFailed, RewardClaimRules.Interpret(RewardClaimInsertResult.Failed, null, "mine", 7, "1.1.1.1"));
            Assert.AreEqual(RewardClaimOutcome.StoreFailed, RewardClaimRules.Interpret(RewardClaimInsertResult.Duplicate, null, "mine", 7, "1.1.1.1"));
            Assert.AreEqual(RewardClaimOutcome.StoreFailed, RewardClaimRules.Interpret(RewardClaimInsertResult.Duplicate, new List<RewardClaim>(), "mine", 7, "1.1.1.1"));
            Assert.AreEqual(RewardClaimOutcome.Granted, RewardClaimRules.Interpret(RewardClaimInsertResult.Duplicate, new[] { otherIp, own }, "mine", 7, "1.1.1.1"));
            Assert.AreEqual(RewardClaimOutcome.DeniedAccount, RewardClaimRules.Interpret(RewardClaimInsertResult.Duplicate, new[] { otherIp, otherAccount }, "mine", 7, "1.1.1.1"));
            Assert.AreEqual(RewardClaimOutcome.DeniedIp, RewardClaimRules.Interpret(RewardClaimInsertResult.Duplicate, new[] { otherIp }, "mine", 7, "1.1.1.1"));
            Assert.AreEqual(RewardClaimOutcome.StoreFailed, RewardClaimRules.Interpret(RewardClaimInsertResult.Duplicate, new[] { otherIp }, "mine", 7, null), "an exempt claim cannot be denied on IP");
        }
    }
}
