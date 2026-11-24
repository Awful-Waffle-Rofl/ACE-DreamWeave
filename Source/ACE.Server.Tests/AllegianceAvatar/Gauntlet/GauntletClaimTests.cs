using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The Gauntlet crate claim (Task 12). The run, the daily claim table, the crate factory, the give, the thread hop and the
    /// clock are all seams, so a claim runs end to end with no database and no landblock. No PropertyManager key is read: the
    /// gates arrive on the window as plain values.
    ///
    /// The fake table models the real one: one row per (account, UTC day), a duplicate answers OwnRowOnRetry for the same token
    /// and AlreadyClaimedToday for another, and a lost acknowledgement commits the row and reports Failed.
    /// </summary>
    [TestClass]
    public class GauntletClaimTests
    {
        private const uint Account = 7001;
        private const string Profile = "gauntlet_t2";
        private const double Multiplier = 1.4;
        private static readonly DateTime Noon = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        private sealed class FakeRun : IGauntletClaimRun
        {
            public uint ClaimRunId { get; set; } = 501;
            public HashSet<uint> Members { get; } = new HashSet<uint>();
            public HashSet<uint> Eligible { get; } = new HashSet<uint>();

            public bool IsMember(Player player) => Members.Contains(player.Guid.Full);
            public bool IsEligible(uint characterGuid) => Eligible.Contains(characterGuid);
            public (int Counted, int Required) WaveCredit(uint characterGuid) => (Eligible.Contains(characterGuid) ? 3 : 1, 3);

            private int crates;
            public int CratesNoted => Volatile.Read(ref crates);
            public void NoteCrateClaimed() => Interlocked.Increment(ref crates);
        }

        private sealed class FakeStore : IGauntletClaimStore
        {
            public readonly object Gate = new object();
            public readonly Dictionary<(uint Account, DateTime Day), (string Token, uint Run, uint Character)> Rows = new();
            public readonly List<string> Tokens = new List<string>();
            public readonly List<(uint Account, DateTime Day, uint Run, uint Character)> Deletes = new();
            public int Inserts;

            /// <summary>The next insert commits and reports Failed, as a lost acknowledgement does.</summary>
            public bool LoseNextAck;

            /// <summary>The next insert reports Failed and writes nothing.</summary>
            public bool FailNextWithoutWriting;

            public int DelayMs;

            public void Insert(uint accountId, DateTime utcDay, uint runId, uint characterId, string token, Action<GauntletClaimInsertResult> callback)
            {
                if (DelayMs > 0)
                    Thread.Sleep(DelayMs);

                GauntletClaimInsertResult result;

                lock (Gate)
                {
                    Inserts++;
                    Tokens.Add(token);
                    var key = (accountId, utcDay.Date);

                    if (FailNextWithoutWriting)
                    {
                        FailNextWithoutWriting = false;
                        result = GauntletClaimInsertResult.Failed;
                    }
                    else if (Rows.TryGetValue(key, out var held))
                    {
                        result = held.Token == token ? GauntletClaimInsertResult.OwnRowOnRetry : GauntletClaimInsertResult.AlreadyClaimedToday;
                    }
                    else
                    {
                        Rows[key] = (token, runId, characterId);
                        result = LoseNextAck ? GauntletClaimInsertResult.Failed : GauntletClaimInsertResult.Inserted;
                        LoseNextAck = false;
                    }
                }

                callback(result);
            }

            public void Delete(uint accountId, DateTime utcDay, uint runId, uint characterId, Action<bool, int> callback)
            {
                int deleted = 0;

                lock (Gate)
                {
                    Deletes.Add((accountId, utcDay.Date, runId, characterId));

                    if (Rows.TryGetValue((accountId, utcDay.Date), out var held) && held.Run == runId && held.Character == characterId)
                    {
                        Rows.Remove((accountId, utcDay.Date));
                        deleted = 1;
                    }
                }

                callback(true, deleted);
            }
        }

        /// <summary>What the give seam saw: the crate and the properties on it AT THE MOMENT OF THE GIVE.</summary>
        private sealed record Given(Player Who, WorldObject Crate, string ProfileAtGive, double? MultiplierAtGive);

        private static uint nextGuid = 0x7ED00000;
        private static uint nextPlayerGuid = 0x50000100;

        private FakeStore store;
        private FakeRun run;
        private DateTime now;
        private readonly object told = new object();
        private List<string> lines;
        private List<Given> given;
        private List<WorldObject> created;
        private bool giveSucceeds;
        private bool crateExists;
        private Player departed;

        [TestInitialize]
        public void Setup()
        {
            store = new FakeStore();
            run = new FakeRun();
            now = Noon;
            lines = new List<string>();
            given = new List<Given>();
            created = new List<WorldObject>();
            giveSucceeds = true;
            crateExists = true;

            GauntletClaims.Store = store;
            GauntletClaims.UtcNow = () => now;
            GauntletClaims.RunOnWorld = work => work();
            GauntletClaims.Tell = (p, text) => { lock (told) lines.Add(text); };
            GauntletClaims.IsGone = p => ReferenceEquals(p, departed);
            departed = null;
            GauntletClaims.CreateCrate = wcid =>
            {
                if (!crateExists)
                    return null;

                var crate = MakeCrate(wcid);
                lock (created)
                    created.Add(crate);
                return crate;
            };
            GauntletClaims.Give = (cache, player, crate) =>
            {
                lock (given)
                    given.Add(new Given(player, crate, crate.GetProperty(PropertyString.WorldEventRewardProfile), crate.GetProperty(PropertyFloat.WorldEventRewardMultiplier)));

                return giveSucceeds;
            };
        }

        [TestCleanup]
        public void Cleanup()
        {
            GauntletClaims.ResetSeams();
            GauntletCacheHandler.ResetRegistry();
        }

        private static WorldObject MakeCrate(uint wcid)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.WorldEventRewardCrate, true } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Gauntlet Crate" } },
            };

            return new GenericObject(weenie, new ACE.Entity.ObjectGuid(Interlocked.Increment(ref nextGuid)));
        }

        private Player Member(bool eligible = true, bool member = true)
        {
            var player = SalvageRollerVendorTests.MakePlayer(Interlocked.Increment(ref nextPlayerGuid));

            if (member)
                run.Members.Add(player.Guid.Full);

            if (eligible)
                run.Eligible.Add(player.Guid.Full);

            return player;
        }

        private GauntletClaimWindow Window(IGauntletClaimRun forRun = null, int minutes = 5, ISet<string> exempt = null, bool ipGate = true)
            => new GauntletClaimWindow(forRun ?? run, Profile, Multiplier, GauntletRewards.CrateWcid, now.AddMinutes(minutes))
            {
                GateByIp = ipGate,
                ExemptIps = exempt ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            };

        private static GauntletClaimResult ClaimNow(GauntletClaimWindow w, Player p, uint account = Account, string ip = "10.0.0.1", string household = "10.0.0.1")
        {
            GauntletClaimResult? answer = null;
            GauntletClaims.Claim(w, null, p, account, ip, household, r => answer = r);
            Assert.IsTrue(answer.HasValue, "the claim answered");
            return answer.Value;
        }

        // ---- who is paid, and what ----------------------------------------------------------------------------

        [TestMethod]
        public void Claim_Eligible_GetsCrateWithTierProfileAndMultiplier()
        {
            var w = Window();
            var p = Member();

            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, p));

            Assert.AreEqual(1, given.Count);
            Assert.AreSame(p, given[0].Who);
            Assert.AreEqual(Profile, given[0].ProfileAtGive, "the profile was already on the crate when it was given");
            Assert.AreEqual(Multiplier, given[0].MultiplierAtGive, "the multiplier was already on the crate when it was given");
            Assert.AreEqual(Profile, given[0].Crate.GetProperty(PropertyString.WorldEventRewardProfile));
            Assert.AreEqual(Multiplier, given[0].Crate.GetProperty(PropertyFloat.WorldEventRewardMultiplier));
            Assert.AreEqual(GauntletRewards.CrateWcid, given[0].Crate.WeenieClassId);
            Assert.AreEqual(1, store.Rows.Count, "the daily row stands");
            Assert.AreEqual(1, w.Paid);
            Assert.AreEqual(1, run.CratesNoted, "the paid crate reaches the run");
        }

        [TestMethod]
        public void Claim_CrateCarriesTheTierAndScoreName()
        {
            var w = new GauntletClaimWindow(run, Profile, Multiplier, GauntletRewards.CrateWcid, now.AddMinutes(5)) { CrateName = GauntletRewards.CrateName("gauntlet_t2", 1234) };
            var p = Member();

            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, p));
            Assert.AreEqual("Gauntlet Crate (Tier 2, Wave 12.34)", given.Single().Crate.Name, "the crate a member holds says what it was earned for");
        }

        [TestMethod]
        public void CrateName_Formats()
        {
            Assert.AreEqual("Gauntlet Crate (Tier 0, Wave 0.07)", GauntletRewards.CrateName("gauntlet_t0", 7));
            Assert.AreEqual("Gauntlet Crate (Tier 6, Wave 35.00)", GauntletRewards.CrateName("gauntlet_t6", 3500));
            Assert.AreEqual("Gauntlet Crate (Tier 0, Wave 0.00)", GauntletRewards.CrateName(null, -5), "a bad input still names it");
            Assert.IsTrue(GauntletRewards.CrateName("gauntlet_t6", long.MaxValue).Length < 255);

            // The live window is built with the name (BuildWindow reads PropertyManager, so its wiring is pinned as source).
            StringAssert.Contains(ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletRewards.cs"),
                "CrateName = GauntletRewards.CrateName(profile, run.ScoreCenti),");
        }

        [TestMethod]
        public void Claim_OnlyPaidClaims_ReachTheRunCount()
        {
            var w = Window();

            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, Member(), account: 7401, household: "a"));
            Assert.AreEqual(GauntletClaimResult.NotEligible, ClaimNow(w, Member(eligible: false), account: 7402, household: "b"));

            giveSucceeds = false;
            Assert.AreEqual(GauntletClaimResult.GiveFailed, ClaimNow(w, Member(), account: 7403, household: "c"));

            giveSucceeds = true;
            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, Member(), account: 7404, household: "d"));

            Assert.AreEqual(2, run.CratesNoted, "a refusal or a failed give is not counted");
        }

        [TestMethod]
        public void TierFor_ScoreCentiReachesTheCrate()
        {
            // 7 waves cleared and a 50 percent partial: tier floor(7 / 5) = 1, m = 1 + (2 + 0.5) / 5.
            var (profile, mult) = GauntletRewards.TierFor(750, 6);
            var expected = GauntletCrateTier.For(7, 50, 6);

            Assert.AreEqual("gauntlet_t1", profile);
            Assert.AreEqual(expected.Profile, profile);
            Assert.AreEqual(expected.Multiplier, mult, 1e-12);
            Assert.AreEqual(1.5, mult, 1e-12);

            Assert.AreEqual(("gauntlet_t0", 1.0), GauntletRewards.TierFor(0, 6));
            Assert.AreEqual("gauntlet_t6", GauntletRewards.TierFor(100000, 6).Profile);
            Assert.AreEqual("gauntlet_t2", GauntletRewards.TierFor(100000, 2).Profile, "the tier is held to the max-tier tunable");
        }

        [TestMethod]
        public void Claim_NotEligible_Refused()
        {
            var w = Window();
            var p = Member(eligible: false);

            Assert.AreEqual(GauntletClaimResult.NotEligible, ClaimNow(w, p));

            CollectionAssert.AreEqual(new[] { "You fought or helped others in 1 of the 3 waves needed for a crate." }, lines, "the refusal carries the counts");
            Assert.AreEqual(0, store.Inserts, "no daily row for an ineligible member");
            Assert.AreEqual(0, given.Count);
        }

        [TestMethod]
        public void Claim_FormerMember_Refused()
        {
            var w = Window();
            var p = Member(eligible: true, member: false);

            Assert.AreEqual(GauntletClaimResult.NotMember, ClaimNow(w, p));

            Assert.AreEqual(0, store.Inserts);
            Assert.AreEqual(0, given.Count);
        }

        [TestMethod]
        public void Claim_NoAccount_Refused()
        {
            Assert.AreEqual(GauntletClaimResult.NoAccount, ClaimNow(Window(), Member(), account: 0));
            Assert.AreEqual(0, store.Inserts);
        }

        [TestMethod]
        public void Claim_WindowOverOrClosed_Refused()
        {
            var w = Window(minutes: 5);
            var p = Member();

            now = Noon.AddMinutes(6);
            Assert.AreEqual(GauntletClaimResult.NotRewarding, ClaimNow(w, p), "past the window's end");

            now = Noon;
            w.Close();
            Assert.AreEqual(GauntletClaimResult.NotRewarding, ClaimNow(w, p), "a closed window");
            Assert.AreEqual(0, store.Inserts);
        }

        // ---- the in-run gates ---------------------------------------------------------------------------------

        [TestMethod]
        public void Claim_SecondCharacterSameAccount_Denied()
        {
            var w = Window();
            var first = Member();
            var second = Member();

            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, first));
            Assert.AreEqual(GauntletClaimResult.AlreadyClaimedAccount, ClaimNow(w, second));

            Assert.AreEqual(1, given.Count);
            Assert.AreEqual(1, store.Inserts, "the account rule answered from memory, before the table");
        }

        [TestMethod]
        public void Claim_SameCharacterAgain_Denied()
        {
            var w = Window();
            var p = Member();

            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, p));
            Assert.AreEqual(GauntletClaimResult.AlreadyClaimedCharacter, ClaimNow(w, p));
            Assert.AreEqual(1, given.Count);
        }

        [TestMethod]
        public void Claim_SameHousehold_Denied_ExemptIpAllowed()
        {
            var w = Window();
            var a = Member();
            var b = Member();

            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, a, account: 7101, ip: "10.0.0.5", household: "cluster:home"));
            Assert.AreEqual(GauntletClaimResult.AlreadyClaimedIp, ClaimNow(w, b, account: 7102, ip: "10.0.0.6", household: "cluster:home"));

            // The exempt address is matched against the RAW address, whatever household its claimant is in.
            var exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "10.0.0.7" };
            var w2 = Window(exempt: exempt);
            var c = Member();
            var d = Member();
            var e = Member();

            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w2, c, account: 7201, ip: "10.0.0.5", household: "cluster:home"));
            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w2, d, account: 7202, ip: "10.0.0.7", household: "cluster:home"), "an exempt address is never held to the household rule");
            Assert.AreEqual(GauntletClaimResult.AlreadyClaimedIp, ClaimNow(w2, e, account: 7203, ip: "10.0.0.8", household: "cluster:home"));
        }

        [TestMethod]
        public void Claim_IpGateOff_SameHouseholdAllowed()
        {
            var w = Window(ipGate: false);

            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, Member(), account: 7301, household: "cluster:home"));
            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, Member(), account: 7302, household: "cluster:home"));
        }

        // ---- the daily account cap ----------------------------------------------------------------------------

        [TestMethod]
        public void Claim_SameAccountSecondRunSameDay_Denied()
        {
            var first = Member();
            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(Window(), first));

            // A second run's window, another character on the same account, the same UTC day.
            var run2 = new FakeRun { ClaimRunId = 502 };
            var second = SalvageRollerVendorTests.MakePlayer(Interlocked.Increment(ref nextPlayerGuid));
            run2.Members.Add(second.Guid.Full);
            run2.Eligible.Add(second.Guid.Full);

            Assert.AreEqual(GauntletClaimResult.AlreadyClaimedToday, ClaimNow(Window(run2), second));

            Assert.AreEqual(1, given.Count, "one crate for the account that day");
            Assert.AreEqual(2, store.Inserts, "the second run's claim reached the table and lost the primary key");
            Assert.AreEqual(1, store.Rows.Count);
            CollectionAssert.Contains(lines, GauntletClaims.TodayLine);
        }

        [TestMethod]
        public void Claim_SameAccountNextUtcDay_Allowed()
        {
            var p = Member();
            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(Window(minutes: 30), p));

            now = new DateTime(2026, 11, 22, 0, 5, 0, DateTimeKind.Utc);

            var run2 = new FakeRun { ClaimRunId = 503 };
            var q = SalvageRollerVendorTests.MakePlayer(Interlocked.Increment(ref nextPlayerGuid));
            run2.Members.Add(q.Guid.Full);
            run2.Eligible.Add(q.Guid.Full);

            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(Window(run2), q));
            Assert.AreEqual(2, given.Count);
            CollectionAssert.AreEquivalent(new[] { new DateTime(2026, 11, 21), new DateTime(2026, 11, 22) }, store.Rows.Keys.Select(k => k.Day).ToArray());
        }

        // ---- the race -----------------------------------------------------------------------------------------

        [TestMethod]
        public void Claim_SameCharacterTwiceConcurrently_PaidOnce()
        {
            var w = Window();
            var p = Member();
            store.DelayMs = 20;

            using var barrier = new Barrier(2);
            var results = new GauntletClaimResult[2];

            Parallel.Invoke(
                () => { barrier.SignalAndWait(); results[0] = ClaimNow(w, p); },
                () => { barrier.SignalAndWait(); results[1] = ClaimNow(w, p); });

            Assert.AreEqual(1, results.Count(r => r == GauntletClaimResult.Paid), $"exactly one paid: {string.Join(",", results)}");
            Assert.AreEqual(1, given.Count, "one crate given");
            Assert.AreEqual(1, created.Count, "one crate created");
            Assert.AreEqual(1, store.Rows.Count);
            Assert.AreEqual(1, w.Paid);
        }

        // ---- failure paths ------------------------------------------------------------------------------------

        [TestMethod]
        public void Claim_CrateGiveFails_DailyRowRemoved()
        {
            var w = Window();
            var p = Member();
            giveSucceeds = false;

            Assert.AreEqual(GauntletClaimResult.GiveFailed, ClaimNow(w, p));

            Assert.AreEqual(0, store.Rows.Count, "the day is not consumed");
            Assert.AreEqual(1, store.Deletes.Count);
            Assert.AreEqual((Account, Noon.Date, 501u, p.Guid.Full), store.Deletes[0], "the undo names the account, day, run and character that inserted the row");
            Assert.AreEqual(0, w.Paid);

            // Retryable: the reservation went back with the row, and the same use now pays.
            giveSucceeds = true;
            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, p));
            Assert.AreEqual(1, store.Rows.Count);
            Assert.AreNotEqual(store.Tokens[0], store.Tokens[1], "a finished logical claim does not carry its token to the next");
        }

        [TestMethod]
        public void Claim_PlayerGoneAfterTheRoundTrip_NothingGiven_DailyRowRemoved()
        {
            var w = Window();
            var p = Member();

            // The claimant logs out while the insert is in flight: the store answers after they are gone.
            var inner = store;
            GauntletClaims.Store = new LeavingStore(inner, () => departed = p);

            Assert.AreEqual(GauntletClaimResult.PlayerGone, ClaimNow(w, p));
            Assert.AreEqual(0, created.Count, "no crate is created for a claimant who left");
            Assert.AreEqual(0, given.Count);
            Assert.AreEqual(0, inner.Rows.Count, "the day is not consumed");
            Assert.AreEqual(1, inner.Deletes.Count);
            Assert.AreEqual(0, w.Paid);
            Assert.AreEqual(0, run.CratesNoted);

            // Back in the world, the same character can claim.
            GauntletClaims.Store = inner;
            departed = null;
            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, p));
        }

        [TestMethod]
        public void DefaultIsGone_NoSessionOrLoggingOut()
        {
            var p = Member();
            Assert.IsTrue(GauntletClaims.DefaultIsGone(p), "a player with no session is gone");
            Assert.IsTrue(GauntletClaims.DefaultIsGone(null));
        }

        /// <summary>Runs an action (the logout) before forwarding the insert, so the answer arrives after it.</summary>
        private sealed class LeavingStore : IGauntletClaimStore
        {
            private readonly IGauntletClaimStore inner;
            private readonly Action leave;
            public LeavingStore(IGauntletClaimStore inner, Action leave) { this.inner = inner; this.leave = leave; }

            public void Insert(uint accountId, DateTime utcDay, uint runId, uint characterId, string token, Action<GauntletClaimInsertResult> callback)
            {
                leave();
                inner.Insert(accountId, utcDay, runId, characterId, token, callback);
            }

            public void Delete(uint accountId, DateTime utcDay, uint runId, uint characterId, Action<bool, int> callback)
                => inner.Delete(accountId, utcDay, runId, characterId, callback);
        }

        [TestMethod]
        public void Claim_CrateWeenieMissing_DailyRowRemoved()
        {
            var w = Window();
            var p = Member();
            crateExists = false;

            Assert.AreEqual(GauntletClaimResult.CrateFailed, ClaimNow(w, p));

            Assert.AreEqual(0, store.Rows.Count);
            Assert.AreEqual(0, given.Count);
            CollectionAssert.Contains(lines, GauntletClaims.NothingLine);
        }

        [TestMethod]
        public void Claim_ResubmitAfterFailed_ReusesTheToken()
        {
            var w = Window();
            var p = Member();

            // The row commits but the acknowledgement is lost: the claim reports Failed, nothing is given.
            store.LoseNextAck = true;
            Assert.AreEqual(GauntletClaimResult.StoreFailed, ClaimNow(w, p));
            Assert.AreEqual(0, given.Count);

            // The next use resubmits the SAME logical claim and reads its own row back.
            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, p));
            Assert.AreEqual(2, store.Tokens.Count);
            Assert.AreEqual(store.Tokens[0], store.Tokens[1], "one token per logical claim");
            Assert.AreEqual(1, given.Count);
        }

        [TestMethod]
        public void Claim_FailedWithNothingWritten_ThenRetried_Pays()
        {
            var w = Window();
            var p = Member();

            store.FailNextWithoutWriting = true;
            Assert.AreEqual(GauntletClaimResult.StoreFailed, ClaimNow(w, p));
            CollectionAssert.Contains(lines, GauntletClaims.StoreFailedLine);

            Assert.AreEqual(GauntletClaimResult.Paid, ClaimNow(w, p));
            Assert.AreEqual(store.Tokens[0], store.Tokens[1]);
        }

        // ---- the dispatch -------------------------------------------------------------------------------------

        private static WorldObject MakeCache(bool marked)
        {
            var weenie = new Weenie
            {
                WeenieClassId = GauntletRewards.CacheWcid,
                WeenieType = WeenieType.Generic,
                PropertiesBool = marked ? new Dictionary<PropertyBool, bool> { { PropertyBool.AllegianceGauntletCache, true } } : new Dictionary<PropertyBool, bool>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Gauntlet Cache" } },
            };

            return new GenericObject(weenie, new ACE.Entity.ObjectGuid(Interlocked.Increment(ref nextGuid)));
        }

        [TestMethod]
        public void TryHandleUse_UnmarkedObject_NotHandled()
        {
            Assert.IsFalse(GauntletCacheHandler.TryHandleUse(MakeCache(marked: false), Member()));
            Assert.AreEqual(0, lines.Count);
        }

        [TestMethod]
        public void TryHandleUse_MarkedButUnregistered_IsSpent()
        {
            Assert.IsTrue(GauntletCacheHandler.TryHandleUse(MakeCache(marked: true), Member()));
            CollectionAssert.AreEqual(new[] { GauntletClaims.SpentLine }, lines);
        }

        [TestMethod]
        public void TryHandleUse_RegisteredCache_RunsTheClaim()
        {
            var cache = MakeCache(marked: true);
            var w = Window();
            GauntletCacheHandler.Register(cache.Guid.Full, w);

            // The seeded player has no session, so the claim reaches the pipeline and stops at its account check.
            Assert.IsTrue(GauntletCacheHandler.TryHandleUse(cache, Member()));
            CollectionAssert.AreEqual(new[] { GauntletClaims.NothingLine }, lines);
            Assert.AreEqual(0, store.Inserts);
        }

        // ---- cache placement rules ----------------------------------------------------------------------------

        [TestMethod]
        public void CacheCount_IsCeilEligibleOverPerCache_AtLeastOne()
        {
            Assert.AreEqual(1, GauntletRewards.CacheCount(0, 8), "a run with nobody eligible still places one");
            Assert.AreEqual(1, GauntletRewards.CacheCount(1, 8));
            Assert.AreEqual(1, GauntletRewards.CacheCount(8, 8));
            Assert.AreEqual(2, GauntletRewards.CacheCount(9, 8));
            Assert.AreEqual(3, GauntletRewards.CacheCount(17, 8));
            Assert.AreEqual(8, GauntletRewards.CacheCount(8, 1));
            Assert.AreEqual(2, GauntletRewards.CacheCount(9, 0), "a missing per-cache value falls back to the default");
            Assert.AreEqual(ACE.Server.WorldEvents.WorldEventRewardDelivery.MaxCachesPerRun, GauntletRewards.CacheCount(100000, 1), "the ring is capped");
        }

        [TestMethod]
        public void RingOffsets_OneAtCentre_ManyOnTheRing()
        {
            var one = GauntletRewards.RingOffsets(1);
            Assert.AreEqual(1, one.Count);
            Assert.AreEqual(System.Numerics.Vector3.Zero, one[0]);

            var four = GauntletRewards.RingOffsets(4);
            Assert.AreEqual(4, four.Count);

            foreach (var o in four)
                Assert.AreEqual(GauntletRewards.RingRadius, o.Length(), 1e-4);

            Assert.AreEqual(4, four.Select(o => (Math.Round(o.X, 3), Math.Round(o.Y, 3))).Distinct().Count());
        }

        [TestMethod]
        public void ResolveGates_ProfileAndOperatorSwitch()
        {
            var reward = new ACE.Server.WorldEvents.Defs.RewardDef { GateByCharacter = true, GateByAccount = true, GateByIp = true };

            var on = GauntletRewards.ResolveGates(reward, ipGateEnabled: true, exemptCsv: "10.0.0.1, 10.0.0.2");
            Assert.IsTrue(on.Ip);
            Assert.IsTrue(on.Exempt.Contains("10.0.0.2"));

            Assert.IsFalse(GauntletRewards.ResolveGates(reward, ipGateEnabled: false, exemptCsv: null).Ip, "the operator's switch turns the household rule off");

            reward.GateByIp = false;
            Assert.IsFalse(GauntletRewards.ResolveGates(reward, ipGateEnabled: true, exemptCsv: null).Ip, "the profile turns it off too");
        }

        // ---- the GenericObject dispatch (source pin, mutation-proved in the test itself) -----------------------

        private const string ActOnUsePath = "Source/ACE.Server/WorldObjects/GenericObject.cs";
        private const string CacheHead = "if (ACE.Server.Avatars.Gauntlet.GauntletCacheHandler.TryHandleUse(this, player))\n                return;";
        private const string GateHead = "if (ACE.Server.Avatars.Gauntlet.GauntletGate.TryHandleUse(this, player))\n                return;";
        private const string SoundTail = "if (UseSound > 0)";

        internal static string DispatchFault(string source)
        {
            if (System.Text.RegularExpressions.Regex.Matches(source, System.Text.RegularExpressions.Regex.Escape(CacheHead)).Count != 1)
                return "the cache dispatch if-head is not in GenericObject exactly once";

            var cache = source.IndexOf(CacheHead, StringComparison.Ordinal);
            var gate = source.IndexOf(GateHead, StringComparison.Ordinal);
            var tail = source.IndexOf(SoundTail, StringComparison.Ordinal);

            if (gate < 0 || cache < gate)
                return "the cache dispatch does not follow the gate dispatch";

            if (tail < cache)
                return "the cache dispatch is not ahead of the default use sound";

            return null;
        }

        [TestMethod]
        public void GenericObjectActOnUse_DispatchesTheCache_MutationProved()
        {
            var source = PooledLootSourceText.Read(ActOnUsePath);

            Assert.IsNull(DispatchFault(source), "the real source passes");

            Assert.IsNotNull(DispatchFault(source.Replace(CacheHead, "")), "no dispatch faults");
            Assert.IsNotNull(DispatchFault(source.Replace(CacheHead, CacheHead + "\n            " + CacheHead)), "a doubled dispatch faults");
            Assert.IsNotNull(DispatchFault(source.Replace(CacheHead, "if (ACE.Server.Avatars.Gauntlet.GauntletCacheHandler.TryHandleUse(this, player))\n                { }")), "an if-head that no longer returns faults");
            Assert.IsNotNull(DispatchFault(source.Replace(CacheHead, "").Replace(SoundTail, SoundTail + " { }\n            " + CacheHead + "\n            if (false)")), "a dispatch after the use sound faults");
        }
    }
}
