using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.Entity.RewardClaims;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.ThreadDungeons;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldEvents;

namespace ACE.Server.Tests
{
    /// <summary>
    /// IP household clusters: the cluster parser, the household key, and - per site that limits by client IP -
    /// two discriminating cases each: (1) accounts in DIFFERENT households sharing one IP no longer collide, and
    /// (2) two accounts in the SAME cluster on DIFFERENT IPs now collide. Every case asserts the raw-IP answer too,
    /// so a site wired back to the raw address fails here (the raw answer is the opposite of the household one).
    /// Pure: no PropertyManager, DB or Session is touched.
    /// </summary>
    [TestClass]
    public class IpHouseholdClusterTests
    {
        private static readonly IPAddress X = IPAddress.Parse("203.0.113.7");
        private static readonly IPAddress Y = IPAddress.Parse("198.51.100.9");

        // Accounts: 1 husband, 2 wife (cluster "fam"); 3 stranger; 4 and 5 exempt-list accounts; 6 wife2.
        private static IpLimitManager.HouseholdLists Lists(string clusters = "fam=1,2,6", params string[] exemptNames)
            => new IpLimitManager.HouseholdLists(exemptNames, Array.Empty<string>(), AccountClusters.Parse(clusters, out _));

        private static string Key(uint account, string name, IPAddress ip, IpLimitManager.HouseholdLists lists)
            => IpLimitManager.HouseholdKeyCore(account, name, ip, lists);

        private static string Raw(IPAddress ip) => RewardClaimRules.NormalizeIpKey(ip, false);

        // ------------------------------------------------------------------ AccountClusters

        [TestMethod]
        public void Parse_EmptyAndWhitespace_AreEmptyWithNoWarnings()
        {
            foreach (var raw in new[] { null, "", "   ", " ; ; " })
            {
                var parsed = AccountClusters.Parse(raw, out var warnings);
                Assert.AreEqual(0, parsed.Count, $"'{raw}'");
                Assert.AreEqual(0, warnings.Count, $"'{raw}'");
                Assert.AreEqual("", parsed.Format());
            }
        }

        [TestMethod]
        public void Parse_ToleratesWhitespace_AndFormatsCanonically()
        {
            var parsed = AccountClusters.Parse("  Zeta = 3 , 1 ; alpha=2 ;", out var warnings);

            Assert.AreEqual(0, warnings.Count);
            Assert.AreEqual("alpha=2;zeta=1,3", parsed.Format());
            Assert.AreEqual("zeta", parsed.ClusterOf(3));
            Assert.AreEqual("alpha", parsed.ClusterOf(2));
            Assert.IsNull(parsed.ClusterOf(99));
            CollectionAssert.AreEqual(new uint[] { 1, 3 }, parsed.MembersOf("zeta").ToArray());
            Assert.AreEqual(0, parsed.MembersOf("nope").Count);
            Assert.AreEqual(parsed.Format(), AccountClusters.Parse(parsed.Format(), out _).Format(), "Format round-trips");
        }

        [TestMethod]
        public void Parse_BadNamesAndIds_AreSkippedWithWarnings()
        {
            var parsed = AccountClusters.Parse("bad name=1;toolong" + new string('x', 40) + "=2;nonequals;ok=3,abc,0,-4,5", out var warnings);

            Assert.AreEqual("ok=3,5", parsed.Format());
            Assert.IsTrue(warnings.Count >= 6, string.Join(" | ", warnings));
        }

        [TestMethod]
        public void Parse_AccountInTwoClusters_FirstInSortedOrderKeepsIt_WithWarning()
        {
            var parsed = AccountClusters.Parse("beta=5,6;alpha=5,7", out var warnings);

            Assert.AreEqual("alpha", parsed.ClusterOf(5));
            Assert.AreEqual("beta", parsed.ClusterOf(6));
            Assert.AreEqual("alpha=5,7;beta=6", parsed.Format());
            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains(warnings[0], "5");
        }

        [TestMethod]
        public void Parse_ClusterLeftEmptyByDuplicates_IsDropped()
        {
            var parsed = AccountClusters.Parse("a=1;b=1", out var warnings);

            Assert.AreEqual("a=1", parsed.Format());
            Assert.AreEqual(1, warnings.Count);
        }

        // ------------------------------------------------------------------ the household key

        [TestMethod]
        public void Key_OrdinaryAccount_IsByteIdenticalToTheNormalisedAddress()
        {
            var lists = Lists();

            foreach (var text in new[] { "203.0.113.7", "::ffff:203.0.113.7", "2001:db8:1:2:aaaa::1", "fe80::1%4" })
            {
                var ip = IPAddress.Parse(text);
                Assert.AreEqual(RewardClaimRules.NormalizeIpKey(ip, false), Key(3, "stranger", ip, lists), text);
            }

            Assert.AreEqual("203.0.113.7", Key(3, "stranger", IPAddress.Parse("::ffff:203.0.113.7"), lists));
            Assert.IsNull(Key(3, "stranger", null, lists), "no address and no household: nothing to key on");
        }

        [TestMethod]
        public void Key_ClusterMember_IsTheClusterKey_FromAnyAddress()
        {
            var lists = Lists();

            Assert.AreEqual("cluster:fam", Key(1, "husband", X, lists));
            Assert.AreEqual("cluster:fam", Key(2, "wife", Y, lists));
            Assert.AreEqual("cluster:fam", Key(2, "wife", null, lists));
        }

        [TestMethod]
        public void Key_ExemptAccount_IsAHouseholdOfOne()
        {
            var lists = Lists("", "Exempt4", "exempt5");

            Assert.AreEqual("acct:4", Key(4, "exempt4", X, lists));
            Assert.AreEqual("acct:5", Key(5, "EXEMPT5", X, lists));
            Assert.AreEqual(Raw(X), Key(3, "stranger", X, lists));
        }

        [TestMethod]
        public void Key_HandEditedBothLists_ClusterIsKept()
        {
            var lists = Lists("fam=1,2", "husband");

            Assert.AreEqual("cluster:fam", Key(1, "husband", X, lists));
        }

        [TestMethod]
        public void Key_NeverParsesAsAnIp_AndFitsTheColumn()
        {
            var name = new string('a', 32);
            var lists = Lists(name + "=" + uint.MaxValue, "maxacct");

            var keys = new[]
            {
                Key(uint.MaxValue, "x", X, lists),
                Key(uint.MaxValue - 1, "maxacct", X, Lists("", "maxacct")),
                IpLimitManager.ClusterKey("fam"),
                IpLimitManager.AccountKey(uint.MaxValue),
            };

            foreach (var key in keys)
            {
                Assert.IsTrue(key.Length <= 45, key);
                Assert.IsFalse(IPAddress.TryParse(key, out _), key);
            }

            Assert.AreEqual(name.Length + "cluster:".Length, keys[0].Length);

            // The widest address key (a full IPv6 text) is itself within the column.
            Assert.IsTrue(Raw(IPAddress.Parse("2001:0db8:85a3:0000:0000:8a2e:0370:7334")).Length <= 45);
        }

        [TestMethod]
        public void Key_AcctPrefixIsTheOneThePuzzleLedgerAlreadyWrites()
        {
            Assert.AreEqual(IpLimitManager.AccountKeyPrefix, ThreadPuzzleIpKey.AccountPrefix);
            Assert.AreEqual(ThreadPuzzleIpKey.For(X, true, 4), IpLimitManager.AccountKey(4), "an exempt account is acct:<id> in both");
        }

        [TestMethod]
        public void ReadHouseholdLists_UnreadableProperties_FallBackToEmpty_WithoutThrowing()
        {
            var first = IpLimitManager.ReadHouseholdLists();

            Assert.IsNotNull(first);
            Assert.AreEqual(0, first.Clusters.Count);
        }

        [TestMethod]
        public void ReadHouseholdListsCore_SameInputsReturnTheSameInstance_ChangedInputsReparse()
        {
            IpLimitManager.ListsSnapshot snapshot = null;
            var warnings = new List<string>();
            var allowed = new[] { "192.0.2.1" };

            var a = IpLimitManager.ReadHouseholdListsCore("exempt4", "fam=1,2", allowed, ref snapshot, warnings.Add, out var r1);
            var b = IpLimitManager.ReadHouseholdListsCore("exempt4", "fam=1,2", allowed, ref snapshot, warnings.Add, out var r2);

            Assert.IsTrue(r1);
            Assert.IsFalse(r2);
            Assert.AreSame(a, b, "same inputs, same instance");
            Assert.AreEqual(0, warnings.Count, "the first parse has no previous membership to differ from");

            var c = IpLimitManager.ReadHouseholdListsCore("exempt4", "fam=1,2;other=3", allowed, ref snapshot, warnings.Add, out var r3);
            Assert.IsTrue(r3);
            Assert.AreNotSame(b, c, "a changed clusters value reparses");
            Assert.AreEqual(2, c.Clusters.Count);
            Assert.AreEqual(1, warnings.Count(w => w.Contains("membership changed")), string.Join(" | ", warnings));
            StringAssert.Contains(warnings.Single(w => w.Contains("membership changed")), "hand edit does not");

            var d = IpLimitManager.ReadHouseholdListsCore("exempt5", "fam=1,2;other=3", allowed, ref snapshot, warnings.Add, out var r4);
            Assert.IsTrue(r4);
            Assert.AreNotSame(c, d, "a changed exempt list reparses");
            Assert.AreEqual(1, warnings.Count(w => w.Contains("membership changed")), "unchanged membership logs nothing more");

            var e = IpLimitManager.ReadHouseholdListsCore("exempt5", "fam=1,2;other=3", new[] { "192.0.2.1" }, ref snapshot, warnings.Add, out var r5);
            Assert.IsTrue(r5, "a different allowlist instance reparses");
            Assert.AreNotSame(d, e);
        }

        [TestMethod]
        public void ReadHouseholdLists_NeverRunsTheAccountLookupOnTheReadingThread()
        {
            var caller = Environment.CurrentManagedThreadId;
            var resolverThreads = new List<int>();

            var oldSource = IpLimitManager.RawSource;
            var oldResolver = IpLimitManager.AccountIdResolver;

            try
            {
                IpLimitManager.RawSource = () => ("f3husband", "f3-slow=1,2", Array.Empty<string>());
                IpLimitManager.AccountIdResolver = _ =>
                {
                    lock (resolverThreads) resolverThreads.Add(Environment.CurrentManagedThreadId);
                    System.Threading.Thread.Sleep(400);
                    return 1u;
                };

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var lists = IpLimitManager.ReadHouseholdLists();
                watch.Stop();

                Assert.AreEqual("f3-slow", lists.Clusters.ClusterOf(1));
                Assert.IsTrue(watch.ElapsedMilliseconds < 300, $"the read waited on a slow resolver ({watch.ElapsedMilliseconds} ms)");

                Assert.IsTrue(IpLimitManager.LastConflictCheck.Wait(10000));
                CollectionAssert.DoesNotContain(resolverThreads, caller, "the lookup ran on the reading thread");
                Assert.AreEqual(1, resolverThreads.Count, "the advisory check did run, off-thread");

                // A throwing resolver is contained too.
                IpLimitManager.RawSource = () => ("f3husband", "f3-throws=1,2", Array.Empty<string>());
                IpLimitManager.AccountIdResolver = _ => throw new InvalidOperationException("db down");

                var lists2 = IpLimitManager.ReadHouseholdLists();
                Assert.AreEqual("f3-throws", lists2.Clusters.ClusterOf(1));
                Assert.IsTrue(IpLimitManager.LastConflictCheck.Wait(10000));
                Assert.IsFalse(IpLimitManager.LastConflictCheck.IsFaulted);
            }
            finally
            {
                IpLimitManager.RawSource = oldSource;
                IpLimitManager.AccountIdResolver = oldResolver;
            }
        }
        // ------------------------------------------------------------------ refusals and the conflict warning

        [TestMethod]
        public void Refusal_ClusterAdd_OfAnAccountAlreadyInAnotherCluster()
        {
            var lists = Lists("fam=1,2;other=3");

            StringAssert.Contains(IpLimitManager.ClusterMembershipRefusal(3, "stranger", "fam", lists), "already in cluster 'other'");
            Assert.IsNull(IpLimitManager.ClusterMembershipRefusal(3, "stranger", "other", lists), "its own cluster is not a refusal");
            Assert.IsNull(IpLimitManager.ClusterMembershipRefusal(9, "free", "fam", lists));
        }

        [TestMethod]
        public void Refusal_ClusterAddOrCreate_OfAnExemptListAccount_SaysRemoveItFromTheExemptListFirst()
        {
            var lists = Lists("fam=1,2", "exempt4");

            var refusal = IpLimitManager.ClusterMembershipRefusal(4, "Exempt4", "fam", lists);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "Remove it from the exempt list first");
            Assert.IsNull(IpLimitManager.ClusterMembershipRefusal(5, "free", "fam", lists));
        }

        [TestMethod]
        public void Refusal_ExemptAdd_OfAnAccountInACluster()
        {
            var lists = Lists("fam=1,2");

            var refusal = IpLimitManager.ExemptMembershipRefusal(1, "husband", lists);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "cluster 'fam'");
            Assert.IsNull(IpLimitManager.ExemptMembershipRefusal(3, "stranger", lists));
            Assert.IsNull(IpLimitManager.ExemptMembershipRefusal(1, "husband", null));
        }

        [TestMethod]
        public void ConflictWarning_AccountOnBothLists_NamesTheIgnoredAcctHousehold()
        {
            var clusters = AccountClusters.Parse("fam=1,2", out _);
            var ids = new Dictionary<string, uint> { ["husband"] = 1, ["stranger"] = 3 };

            var warnings = IpLimitManager.ExemptClusterConflictWarnings(new[] { "husband", "stranger", "ghost" }, clusters,
                n => ids.TryGetValue(n, out var id) ? id : (uint?)null);

            Assert.AreEqual(1, warnings.Count, string.Join(" | ", warnings));
            StringAssert.Contains(warnings[0], "husband");
            StringAssert.Contains(warnings[0], "cluster 'fam'");
            StringAssert.Contains(warnings[0], "acct:");
            StringAssert.Contains(warnings[0], "ignored");
        }

        // ------------------------------------------------------------------ site 1: the active-player limit

        private sealed record Acct(uint Id, string Name, IPAddress Ip, uint Guid);

        // Violators under maxFree=1, grouping by the supplied key function: the sweep's own shape.
        private static int Violators(IEnumerable<Acct> accts, Func<Acct, string> keyOf)
        {
            var violators = 0;

            foreach (var group in IpLimitManager.GroupByHousehold(accts, keyOf).Values)
            {
                var candidates = group.Select(a => new IpLimitCandidate(a.Guid, 100, false, a.Guid)).ToList();
                violators += IpLimitManager.SelectViolators(candidates, 1, 0).Count;
            }

            return violators;
        }

        private static int ByHousehold(IEnumerable<Acct> accts, IpLimitManager.HouseholdLists lists)
            => Violators(accts, a => Key(a.Id, a.Name, a.Ip, lists));

        private static int ByRawIp(IEnumerable<Acct> accts) => Violators(accts, a => Raw(a.Ip));

        [TestMethod]
        public void ActivePlayerLimit_AClusterHasTheSameCapsAsAnIp()
        {
            var accts = new[] { new Acct(1, "husband", X, 101), new Acct(2, "wife", X, 102) };

            Assert.AreEqual(1, ByRawIp(accts), "raw-IP grouping would evict one");
            Assert.AreEqual(1, ByHousehold(accts, Lists("")), "unclustered, they are one household by address: 1");
            Assert.AreEqual(1, ByHousehold(accts, Lists("fam=1,2")), "a cluster has the same caps as an IP: two characters in one household, maxFree=1, is 1");
        }

        [TestMethod]
        public void ActivePlayerLimit_HouseholdsSharingOneIp_DoNotCollide()
        {
            // The husband is in the cluster; the stranger is an ordinary account on the SAME address.
            var accts = new[] { new Acct(1, "husband", X, 101), new Acct(3, "stranger", X, 103) };

            Assert.AreEqual(1, ByRawIp(accts), "raw-IP grouping puts them together: 1 violator");
            Assert.AreEqual(0, ByHousehold(accts, Lists("fam=1,2")), "the cluster is its own household: 0");
        }

        [TestMethod]
        public void ActivePlayerLimit_ClusterMembersOnDifferentIps_Collide()
        {
            var accts = new[] { new Acct(2, "wife", X, 102), new Acct(6, "wife2", Y, 106) };

            Assert.AreEqual(0, ByRawIp(accts), "raw-IP grouping sees two addresses: 0 violators");
            Assert.AreEqual(1, ByHousehold(accts, Lists("fam=1,2,6")), "one cluster: 1 violator");
        }

        [TestMethod]
        public void ActivePlayerLimit_TwoExemptAccountsOnOneIp_AreEachTheirOwnHousehold()
        {
            var accts = new[] { new Acct(4, "exempt4", X, 104), new Acct(5, "exempt5", X, 105) };

            Assert.AreEqual(1, ByRawIp(accts));
            Assert.AreEqual(0, ByHousehold(accts, Lists("", "exempt4", "exempt5")));
        }

        [TestMethod]
        public void ActivePlayerLimit_ExemptAccountDoesNotCountTowardItsRawIpHousehold()
        {
            var accts = new[] { new Acct(4, "exempt4", X, 104), new Acct(3, "stranger", X, 103) };

            Assert.AreEqual(1, ByRawIp(accts));
            Assert.AreEqual(0, ByHousehold(accts, Lists("", "exempt4")));
        }

        [TestMethod]
        public void GroupByHousehold_DropsNullKeys_AndKeepsInputOrder()
        {
            var groups = IpLimitManager.GroupByHousehold(new[] { "a1", "b1", "a2", "x" }, s => s == "x" ? null : s.Substring(0, 1));

            Assert.AreEqual(2, groups.Count);
            CollectionAssert.AreEqual(new[] { "a1", "a2" }, groups["a"]);
        }

        // ------------------------------------------------------------------ site 2: reward claims

        private const string ClaimKey = "AssayRowTigerEyeHammer";

        private static RewardClaimRequest Claim(uint account, string name, IPAddress ip, IpLimitManager.HouseholdLists lists)
        {
            RewardClaimAllowlist.TryGet(ClaimKey, out var entry);

            return new RewardClaimRequest
            {
                ClaimKey = ClaimKey,
                EmoterWcid = entry.NpcWcid,
                AccountId = account,
                CharacterId = 0x50000000 + account,
                CharacterName = name,
                Address = ip,
                HouseholdKey = Key(account, name, ip, lists),
                IsExempt = () => false,
                HasRoomForReward = _ => true,
            };
        }

        private static void WithClaimStore(Action<FakeRewardClaimStore> body)
        {
            var store = new FakeRewardClaimStore();
            RewardClaimService.ResetForTesting(store);
            RewardClaimService.EnabledSource = () => true;

            try { body(store); }
            finally { RewardClaimService.ResetForTesting(null); }
        }

        [TestMethod]
        public void RewardClaims_HouseholdsSharingOneIp_BothClaim_AndTheRawAddressStaysInIpAddress()
        {
            WithClaimStore(store =>
            {
                var lists = Lists("fam=1,2");

                Assert.AreEqual(RewardClaimService.ResultGranted, RewardClaimService.Evaluate(Claim(1, "husband", X, lists)).LogResult);
                Assert.AreEqual(RewardClaimService.ResultGranted, RewardClaimService.Evaluate(Claim(3, "stranger", X, lists)).LogResult,
                    "raw-IP keying would deny the stranger as denied:ip");

                Assert.AreEqual("cluster:fam", store.Rows.Single(r => r.AccountId == 1).IpKey);
                Assert.AreEqual(Raw(X), store.Rows.Single(r => r.AccountId == 3).IpKey);
                Assert.IsTrue(store.Rows.All(r => r.IpAddress == X.ToString()), "ip_Address stays the raw address");
            });
        }

        [TestMethod]
        public void RewardClaims_ClusterMembersOnDifferentIps_SecondIsDenied()
        {
            WithClaimStore(_ =>
            {
                var lists = Lists("fam=1,2");

                Assert.AreEqual(RewardClaimService.ResultGranted, RewardClaimService.Evaluate(Claim(1, "husband", X, lists)).LogResult);
                Assert.AreEqual(RewardClaimService.ResultDeniedIp, RewardClaimService.Evaluate(Claim(2, "wife", Y, lists)).LogResult,
                    "raw-IP keying would grant the wife (a different address)");
            });
        }

        [TestMethod]
        public void RewardClaims_TrueBypass_StillStoresANullKey()
        {
            WithClaimStore(store =>
            {
                var request = Claim(1, "husband", X, Lists("fam=1,2"));
                request.IsExempt = () => true;

                Assert.AreEqual(RewardClaimService.ResultGranted, RewardClaimService.Evaluate(request).LogResult);
                Assert.IsNull(store.Rows.Single().IpKey);
            });
        }

        [TestMethod]
        public void RewardClaims_ClusterSubject_ResolvesToTheClusterKey()
        {
            var request = RewardClaimCommandText.Parse(new[] { "show", ClaimKey, "cluster", "Fam" });

            Assert.IsNull(request.Error);
            Assert.AreEqual(IpLimitManager.ClusterKey("fam"), request.IpKey);
            Assert.IsNotNull(RewardClaimCommandText.Parse(new[] { "show", ClaimKey, "cluster", "bad name" }).Error);
        }

        // ------------------------------------------------------------------ site 3: Threads

        [TestMethod]
        public void Threads_PuzzleKey_HouseholdsSharingOneIp_KeepDistinctKeys()
        {
            var lists = Lists("fam=1,2");

            var clustered = ThreadPuzzleIpKey.For(X, 1, "husband", lists);
            var stranger = ThreadPuzzleIpKey.For(X, 3, "stranger", lists);

            Assert.AreEqual(ThreadPuzzleIpKey.For(X, false, 1), ThreadPuzzleIpKey.For(X, false, 3), "raw-IP keying shares one key");
            Assert.AreNotEqual(clustered, stranger);
            Assert.AreEqual("cluster:fam", clustered);
            Assert.AreEqual("203.0.113.7", stranger, "an ordinary account's key is unchanged");
        }

        [TestMethod]
        public void Threads_PuzzleKey_ClusterMembersOnDifferentIps_ShareAKey()
        {
            var lists = Lists("fam=1,2");

            Assert.AreNotEqual(ThreadPuzzleIpKey.For(X, false, 1), ThreadPuzzleIpKey.For(Y, false, 2), "raw-IP keying: two keys");
            Assert.AreEqual(ThreadPuzzleIpKey.For(X, 1, "husband", lists), ThreadPuzzleIpKey.For(Y, 2, "wife", lists));
        }

        [TestMethod]
        public void Threads_PuzzleKey_LedgerKeysForOrdinaryAndExemptAccounts_AreByteIdentical()
        {
            var lists = Lists("fam=1,2", "exempt4");
            var v6 = IPAddress.Parse("2001:db8:1:2:aaaa::1");

            Assert.AreEqual("2001:db8:1:2::/64", ThreadPuzzleIpKey.For(v6, 3, "stranger", lists));
            Assert.AreEqual("acct:4", ThreadPuzzleIpKey.For(X, 4, "exempt4", lists));
            Assert.IsNull(ThreadPuzzleIpKey.For((IPAddress)null, 1, "husband", lists), "no address: nothing counted, even for a cluster member");
        }

        private static RosterCandidate Seat(uint guid, string name, uint account, IPAddress ip, IpLimitManager.HouseholdLists lists)
            => new RosterCandidate(guid, name, account, 200, false, false, false, true, Key(account, name, ip, lists), true, null);

        [TestMethod]
        public void Threads_Roster_HouseholdsSharingOneIp_FormAGroup()
        {
            var lists = Lists("fam=1,2");

            var form = GroupRosterRules.Form(Seat(0x50000100u, "Owner", 3, X, lists), new[] { Seat(0x50000101u, "Husband", 1, X, lists) }, 150, 20);

            Assert.IsFalse(form.IpConflict, "raw-IP keying would report a conflict");
            Assert.IsTrue(form.IsGroup);
        }

        [TestMethod]
        public void Threads_Roster_ClusterMembersOnDifferentIps_Conflict()
        {
            var lists = Lists("fam=1,2");

            var form = GroupRosterRules.Form(Seat(0x50000100u, "Owner", 1, X, lists), new[] { Seat(0x50000101u, "Wife", 2, Y, lists) }, 150, 20);

            Assert.IsTrue(form.IpConflict, "raw-IP keying would form the group");
        }

        [TestMethod]
        public void Threads_Roster_CandidateBuilder_KeysThroughTheHouseholdResolver_AfterTheBypass()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GroupRosterRules.cs");
            var builder = PooledLootSourceText.MethodBody(source, "private static RosterCandidate CandidateFrom(Player player, bool inRange)");

            StringAssert.Contains(builder, "var ipKey = IpLimitManager.IsExempt(session) ? null : IpLimitManager.HouseholdKey(session);");
            Assert.IsFalse(builder.Contains("EndPointC2S"), "the builder must not read the raw address");
            Assert.IsFalse(builder.Contains("NormalizeIpKey"), "the key comes only from the resolver");
        }

        [TestMethod]
        public void Threads_PuzzleKeySession_ChecksTheClusterFirst()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadPuzzleIpKey.cs");
            var core = PooledLootSourceText.MethodBody(source, "public static string For(IPAddress address, uint accountId, string accountName, IpLimitManager.HouseholdLists lists)");

            Assert.IsTrue(core.IndexOf("ClusterKey(cluster)", StringComparison.Ordinal) < core.IndexOf("IsHouseholdExemptCore", StringComparison.Ordinal), "the cluster is checked before the existing logic");
            StringAssert.Contains(source, "return For(address, session.AccountId, session.Account, lists);");
        }

        // ------------------------------------------------------------------ site 4: World Events

        private static ClaimDenial WorldEventClaim(string rawIp, string householdKey, string claimedKey, string exemptIp = null)
            => WorldEventClaimRules.Evaluate(1, 0, rawIp, householdKey,
                new HashSet<uint>(), new HashSet<uint>(), claimedKey == null ? new HashSet<string>() : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { claimedKey },
                exemptIp == null ? new HashSet<string>() : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exemptIp },
                false, false, true);

        [TestMethod]
        public void WorldEvents_HouseholdsSharingOneIp_BothClaim()
        {
            var lists = Lists("fam=1,2");

            // The stranger already claimed from X; a cluster member on X is a different household.
            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp, WorldEventClaim("203.0.113.7", Raw(X), Raw(X)), "raw-IP keying: the second claimant on X is denied");
            Assert.AreEqual(ClaimDenial.None, WorldEventClaim("203.0.113.7", Key(1, "husband", X, lists), Raw(X)));
        }

        [TestMethod]
        public void WorldEvents_ClusterMembersOnDifferentIps_SecondIsDenied()
        {
            var lists = Lists("fam=1,2");

            Assert.AreEqual(ClaimDenial.None, WorldEventClaim("198.51.100.9", Raw(Y), Key(1, "husband", X, lists)), "raw-IP keying: a different address is a fresh claim");
            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp, WorldEventClaim("198.51.100.9", Key(2, "wife", Y, lists), Key(1, "husband", X, lists)));
        }

        [TestMethod]
        public void WorldEvents_IpExemptList_IsStillCheckedAgainstTheRawAddress()
        {
            var lists = Lists("fam=1,2");
            var clusterKey = Key(1, "husband", X, lists);

            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp, WorldEventClaim("203.0.113.7", clusterKey, clusterKey));
            Assert.AreEqual(ClaimDenial.None, WorldEventClaim("203.0.113.7", clusterKey, clusterKey, exemptIp: "203.0.113.7"), "an exempt raw address stays exempt whatever its household");
            Assert.AreEqual(ClaimDenial.AlreadyClaimedIp, WorldEventClaim("203.0.113.7", clusterKey, clusterKey, exemptIp: clusterKey), "the exempt list is not matched against the household key");
        }

        [TestMethod]
        public void WorldEvents_Handler_PassesTheHouseholdKey_AndRecordsIt()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEventCacheHandler.cs");

            StringAssert.Contains(source, "var householdKey = IpLimitManager.HouseholdKey(player.Session);");
            Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(source, @"WorldEventClaimRules\.Evaluate\(characterGuid, accountId, ip, householdKey,").Count);
            StringAssert.Contains(source, "WorldEventClaimRules.NormalizeIp(householdKey)");
        }

        // ------------------------------------------------------------------ site 5: PvP

        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static MatchmakingContext Ctx() => new MatchmakingContext(
            UtcNow: Now,
            MmWindowInitial: 100, MmWindowGrowthPerMinute: 50, MmWindowMax: 400,
            DuoVsSoloAfterSeconds: 120,
            FfaTargetPlayers: 10, FfaMinPlayers: 5, FfaMaxPlayers: 15, FfaMinDecaySeconds: 60,
            BlockSameIp: true);

        private static QueueEntrant Solo(uint id, string key)
            => new QueueEntrant(Guid.NewGuid(), new[] { id }, new[] { 1500 }, key, Now);

        [TestMethod]
        public void Pvp_HouseholdsSharingOneIp_CanBePaired()
        {
            var lists = Lists("fam=1,2");

            Assert.IsNull(new PairMatchmaker().TryForm(new[] { Solo(3, Raw(X)), Solo(1, Raw(X)) }, Ctx()), "raw-IP keying blocks the pairing");
            Assert.IsNotNull(new PairMatchmaker().TryForm(new[] { Solo(3, Key(3, "stranger", X, lists)), Solo(1, Key(1, "husband", X, lists)) }, Ctx()));
        }

        [TestMethod]
        public void Pvp_ClusterMembersOnDifferentIps_CannotBePaired()
        {
            var lists = Lists("fam=1,2");

            Assert.IsNotNull(new PairMatchmaker().TryForm(new[] { Solo(1, Raw(X)), Solo(2, Raw(Y)) }, Ctx()), "raw-IP keying pairs them");
            Assert.IsNull(new PairMatchmaker().TryForm(new[] { Solo(1, Key(1, "husband", X, lists)), Solo(2, Key(2, "wife", Y, lists)) }, Ctx()));
        }

        [TestMethod]
        public void Pvp_LiveAdapter_KeysThroughTheHouseholdResolver()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Pvp/PvpLiveAdapters.cs");

            StringAssert.Contains(source, "IpKey: IpLimitManager.HouseholdKey(p.Session),");
            Assert.IsFalse(source.Contains("EndPointC2S"), "PvpLiveAdapters must not read the raw address");
        }

        // ------------------------------------------------------------------ review follow-ups

        [TestMethod]
        public void RewardClaims_RequestWithNoHouseholdKey_FallsBackToTheAddressKey_AndStillDeniesTheSecondAccount()
        {
            WithClaimStore(store =>
            {
                var lists = Lists("");
                var first = Claim(1, "husband", X, lists);
                first.HouseholdKey = null;
                var second = Claim(3, "stranger", X, lists);
                second.HouseholdKey = null;

                Assert.AreEqual(RewardClaimService.ResultGranted, RewardClaimService.Evaluate(first).LogResult);
                Assert.AreEqual(RewardClaimService.ResultDeniedIp, RewardClaimService.Evaluate(second).LogResult, "a null key must not fail open");
                Assert.AreEqual(Raw(X), store.Rows.Single().IpKey);
            });
        }

        [TestMethod]
        public void RewardClaims_EmoteManager_WiresTheHouseholdKeyIntoTheRequest()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Managers/EmoteManager.cs");

            StringAssert.Contains(source, "HouseholdKey = IpLimitManager.HouseholdKey(session)");
        }

        [TestMethod]
        public void ActivePlayerLimit_Tick_GroupsByTheHouseholdKey()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Managers/IpLimitManager.cs");
            var tick = PooledLootSourceText.MethodBody(source, "public static void Tick()");

            StringAssert.Contains(tick, "GroupByHousehold(eligible, p => HouseholdKey(p.Session, lists))");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(tick, "EndPointC2S").Count, "the sweep's only raw read is its log line");
        }

        [TestMethod]
        public void ActivePlayerLimit_GetResidents_MatchesByTheHouseholdKey()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Managers/IpLimitManager.cs");
            var body = PooledLootSourceText.MethodBody(source, "public static List<IpLimitCandidate> GetResidents(string householdKey, uint excludeAccountId)");

            StringAssert.Contains(body, "HouseholdKey(session, lists), householdKey");
            Assert.IsFalse(body.Contains("EndPointC2S"), "GetResidents must not compare raw addresses");
        }

        [TestMethod]
        public void ActivePlayerLimit_LoginGate_ResolvesTheKeyThenAsksForResidentsByIt()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Network/Handlers/CharacterHandler.cs");
            var gate = PooledLootSourceText.MethodBody(source, "private static bool HandleIpLimitOnEnterWorld(Session session,");

            var key = gate.IndexOf("IpLimitManager.HouseholdKey(session)", StringComparison.Ordinal);
            var residents = gate.IndexOf("GetResidents(householdKey", StringComparison.Ordinal);

            Assert.IsTrue(key >= 0 && residents > key, "the gate resolves HouseholdKey(session), then calls GetResidents(householdKey");
        }

        [TestMethod]
        public void ActivePlayerLimit_Ipv4MappedAndPlainIpv4_ShareOneHousehold()
        {
            var mapped = IPAddress.Parse("::ffff:203.0.113.7");
            var accts = new[] { new Acct(3, "stranger", X, 103), new Acct(1, "other", mapped, 101) };

            Assert.IsFalse(mapped.Equals(X), "the old IPAddress.Equals grouping kept them apart");
            Assert.AreEqual(0, Violators(accts, a => a.Ip.ToString()), "legacy address grouping: two households of one");
            Assert.AreEqual(1, ByHousehold(accts, Lists("")), "household keying merges them: 1 violator");
            Assert.AreEqual(1, IpLimitManager.GroupByHousehold(accts, a => Key(a.Id, a.Name, a.Ip, Lists(""))).Count);
        }
        [TestMethod]
        public void ReadHouseholdListsCore_CommandWriteThenRead_DoesNotWarnAboutAHandEdit()
        {
            IpLimitManager.ListsSnapshot snapshot = null;
            var warnings = new List<string>();
            var allowed = new[] { "192.0.2.1" };

            IpLimitManager.ReadHouseholdListsCore("", "fam=1,2", allowed, ref snapshot, warnings.Add, out _);

            // The command wrote this canonical value (TryWriteClusters records it), then the next read parses it.
            var written = AccountClusters.Parse("fam=1,2;other=3", out _).Format();
            IpLimitManager.ReadHouseholdListsCore("", written, allowed, ref snapshot, warnings.Add, out var reparsed, written);

            Assert.IsTrue(reparsed);
            Assert.AreEqual(0, warnings.Count(w => w.Contains("membership changed")), string.Join(" | ", warnings));
        }

        [TestMethod]
        public void ReadHouseholdListsCore_RawChangeWithNoPriorWrite_WarnsAboutAHandEdit()
        {
            IpLimitManager.ListsSnapshot snapshot = null;
            var warnings = new List<string>();
            var allowed = new[] { "192.0.2.1" };

            IpLimitManager.ReadHouseholdListsCore("", "fam=1,2", allowed, ref snapshot, warnings.Add, out _);
            IpLimitManager.ReadHouseholdListsCore("", "fam=1,2;other=3", allowed, ref snapshot, warnings.Add, out _, "fam=1,2");

            Assert.AreEqual(1, warnings.Count(w => w.Contains("membership changed")), "a value that differs from the last command write is a hand edit");
        }

        [TestMethod]
        public void ReadHouseholdLists_AndTryWriteClusters_AreWiredToTheLastWrittenValue()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Managers/IpLimitManager.cs");

            StringAssert.Contains(PooledLootSourceText.MethodBody(source, "public static HouseholdLists ReadHouseholdLists()"), "out var reparsed, lastWrittenClustersFormat)");

            var write = PooledLootSourceText.MethodBody(source, "public static bool TryWriteClusters(AccountClusters clusters, out string message)");
            Assert.IsTrue(write.IndexOf("lastWrittenClustersFormat = format;", StringComparison.Ordinal) < write.IndexOf("PropertyManager.ModifyString", StringComparison.Ordinal), "recorded before the write");
        }    }
}
