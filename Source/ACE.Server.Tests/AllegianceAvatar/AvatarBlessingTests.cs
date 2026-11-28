using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Auth;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Tests.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The Allegiance Blessing (owner design 2026-11-25): price, recipients, the claim -> reserve -> grant -> commit flow, and
    /// the refusals. Recipients run through the real monarch filter (Player.Allegiance) and the real bank grant
    /// (Player.AddOfflineBonusSeconds) on seeded Players; only the online list, the per-player thread, the save and the chat
    /// sends are seams. Nothing reads PropertyManager except the offline bank, whose keys are seeded and restored.
    /// </summary>
    [TestClass]
    public class AvatarBlessingTests
    {
        private const double Start = 1_800_000_000.0;
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // 2027-01-15 12:00Z = 7:00 AM EST: the Avatar day turns over at 2027-01-16 05:00Z, 17 hours away.
        private static readonly DateTime Now = new DateTime(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        private static readonly AvatarTuning T = AvatarTuning.Defaults with { EventMinLevel = 1 };

        private const uint AvatarId = 7;
        private const uint Monarch = 1000;
        private const uint MonarchAcct = 100;
        private const uint VassalGuid = 1001;
        private const uint VassalAcct = 101;

        private static uint nextGuid = 0x7ECB0000;

        private Store store;
        private AccountActivityIndex activity;
        private List<AvatarRoster> rosters;
        private List<Player> online;
        private List<(Player Player, Action Work)> queued;
        private List<Player> saved;
        private List<(Player To, string Text)> toPlayers;
        private List<(uint Monarch, string Text)> allegiance;
        private Player buyer;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Account>k__BackingField")]
        private static extern ref Account AccountField(Player player);

        [ClassInitialize]
        public static void ClassSetup(TestContext context) => ThornsCloseRangeTests.EnsurePlayerTypeInitializable();

        [TestInitialize]
        public void Setup()
        {
            AvatarTestChart.Install();
            SalvageRollerVendorTests.SeedVendorConstructible();

            PropertyManager.ModifyBool("offline_bonus_enabled", true);
            PropertyManager.ModifyLong("offline_bonus_max_seconds", 86400);
            PropertyManager.ModifyLong("offline_bonus_idle_timeout_seconds", 300);
            PropertyManager.ModifyDouble("offline_bonus_idle_accrual_rate", 1.0, init: true);

            store = new Store();
            activity = new AccountActivityIndex();
            rosters = new List<AvatarRoster>();
            online = new List<Player>();
            queued = new List<(Player, Action)>();
            saved = new List<Player>();
            toPlayers = new List<(Player, string)>();
            allegiance = new List<(uint, string)>();

            AvatarRights.AllegianceFacts = p => (Monarch, false);
            AvatarVendor.Tuning = () => T;
            AvatarAnnouncer.Sink = (m, text) => allegiance.Add((m, text));
            AvatarBlessing.AllOnline = () => online;
            AvatarBlessing.OfflineBonusEnabled = () => true;
            AvatarBlessing.RunOnPlayer = (p, work) => queued.Add((p, work));
            AvatarBlessing.SavePlayer = p => saved.Add(p);
            AvatarBlessing.TellPlayer = (p, text) => toPlayers.Add((p, text));
            AvatarBlessing.UtcNow = () => Now;
            AvatarBlessing.ResendPanel = (npc, p) => { };
            AvatarStatusText.UtcNow = () => Now;

            buyer = Member(Monarch, MonarchAcct, Monarch);
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarTestChart.Reset();
            AvatarBlessing.ResetSeams();
            AvatarVendor.ResetSeams();
            AvatarRights.ResetSeams();
            AvatarStatusText.ResetSeams();
            AvatarAnnouncer.Sink = null;
            // Restored to the compiled defaults (an unseeded read would hit the database, so nothing is read before seeding).
            PropertyManager.ModifyBool("offline_bonus_enabled", DefaultPropertyManager.DefaultBooleanProperties["offline_bonus_enabled"].Item);
            PropertyManager.ModifyLong("offline_bonus_max_seconds", DefaultPropertyManager.DefaultLongProperties["offline_bonus_max_seconds"].Item);
            PropertyManager.ModifyLong("offline_bonus_idle_timeout_seconds", DefaultPropertyManager.DefaultLongProperties["offline_bonus_idle_timeout_seconds"].Item);
            PropertyManager.ModifyDouble("offline_bonus_idle_accrual_rate", DefaultPropertyManager.DefaultDoubleProperties["offline_bonus_idle_accrual_rate"].Item, init: true);
            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
        }

        // ---- fixtures -------------------------------------------------------------------------------------------

        private static Allegiance AllegianceOf(uint monarch)
        {
            var a = (Allegiance)RuntimeHelpers.GetUninitializedObject(typeof(Allegiance));
            var biota = new ACE.Entity.Models.Biota { WeenieType = WeenieType.Allegiance, PropertiesIID = new Dictionary<PropertyInstanceId, uint> { { PropertyInstanceId.Monarch, monarch } } };
            PvpTemplatePlayerTests.SeedWorldObject(a, biota, 0x70000001);
            return a;
        }

        /// <summary>A seeded online character with an account and an allegiance led by <paramref name="monarch"/>.</summary>
        internal static Player Member(uint guid, uint account, uint monarch, bool mule = false)
        {
            var p = SalvageRollerVendorTests.MakePlayer(guid);
            AccountField(p) = new Account { AccountId = account };
            p.Allegiance = AllegianceOf(monarch);
            if (mule)
                p.SetProperty(PropertyBool.IsMule, true);
            return p;
        }

        private AvatarState InitActive(ulong favor = 10_000)
        {
            store.Avatars.Add(new AllegianceAvatar
            {
                AvatarId = AvatarId, MonarchGuid = Monarch, State = (byte)AvatarBindState.Bound,
                Favor = favor, LifetimeXp = 10_000, LevelXp = 10_000, LastDecayDay = Today,
            });
            rosters.Add(new AvatarRoster(Monarch, MonarchAcct, false, new List<KeeperCandidate>
            {
                new KeeperCandidate(Monarch, MonarchAcct, 1, 0, 0),
                new KeeperCandidate(VassalGuid, VassalAcct, 1, 0, 1),
            }));
            activity.Touch(MonarchAcct, Start);
            activity.Touch(VassalAcct, Start);

            AvatarManager.Initialize(store, () => T, () => Start, () => activity, () => rosters, m => rosters.FirstOrDefault(r => r.MonarchGuid == m));
            AvatarManager.Tick();

            var state = AvatarManager.Get(AvatarId);
            Assert.IsTrue(state.Active, "the fixture must start Active");
            return state;
        }

        private AvatarVendor MakeVendor()
        {
            var weenie = new Weenie
            {
                WeenieClassId = AvatarVendor.AvatarWcid,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.AllegianceAvatarVendor, true } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, 0 }, { PropertyInt.MerchandiseMinValue, 0 }, { PropertyInt.MerchandiseMaxValue, 0 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.BuyPrice, 0 }, { PropertyFloat.SellPrice, 1 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Allegiance Avatar" } },
            };

            var vendor = (AvatarVendor)WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(++nextGuid));
            vendor.AvatarId = AvatarId;
            vendor.Location = new Position((0x0090u << 16) | 0x0011, 50f, 50f, 0f, 0f, 0f, 0f, 1f, 0);
            return vendor;
        }

        private void RunGrants()
        {
            foreach (var (_, work) in queued.ToList())
                work();
            queued.Clear();
        }

        private bool Buy(AvatarVendor npc, out string line) => AvatarBlessing.TryBuy(buyer, npc, out line);

        private static (int Used, int Pending) DayCount() => AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Blessing, AvatarDay.BuildClock(T, Now));

        // ---- price (pure) ---------------------------------------------------------------------------------------

        [TestMethod]
        public void Price_TwoMembers_Is450()
        {
            Assert.AreEqual(450UL, AvatarBlessing.Price(2, T));
            Assert.AreEqual(250UL, AvatarBlessing.Price(0, T));
            Assert.AreEqual(350UL, AvatarBlessing.Price(1, T));
        }

        [TestMethod]
        public void Price_CapHoldsFrom198Members()
        {
            Assert.AreEqual(19_950UL, AvatarBlessing.Price(197, T), "control: just under the cap");
            Assert.AreEqual(20_000UL, AvatarBlessing.Price(198, T), "250 + 19,800 = 20,050, capped");
            Assert.AreEqual(20_000UL, AvatarBlessing.Price(5000, T));
            Assert.AreEqual(20_000UL, AvatarBlessing.Price(int.MaxValue, T), "no overflow");
            Assert.AreEqual(5_000UL, AvatarBlessing.Price(1000, T with { BlessingPriceCap = 5000 }), "the cap is a tunable");
        }

        // ---- recipients (pure) ----------------------------------------------------------------------------------

        [TestMethod]
        public void Recipients_IncludeEveryNonMuleCharacter_ButCountEachAccountOnce()
        {
            var a1 = Member(0x7EB00001, 501, Monarch);
            var a1Alt = Member(0x7EB00002, 501, Monarch); // a second character on the same account
            var mule = Member(0x7EB00003, 502, Monarch, mule: true);
            var b = Member(0x7EB00004, 503, Monarch);

            var r = AvatarBlessing.Recipients(new[] { b, a1Alt, mule, a1 });

            CollectionAssert.AreEqual(new[] { a1, a1Alt, b }, r.ToList(), "every non-mule character, in guid order");
            Assert.AreEqual(2, AvatarBlessing.CountedAccounts(r), "two characters on one account are priced once");
        }

        [TestMethod]
        public void Recipients_AndOnlineMembers_ExcludeACharacterLoggingOut()
        {
            var a1 = Member(0x7EB00001, 501, Monarch);
            var a1Leaving = Member(0x7EB00002, 501, Monarch);
            a1Leaving.IsLoggingOut = true;

            CollectionAssert.AreEqual(new[] { a1 }, AvatarBlessing.Recipients(new[] { a1Leaving, a1 }).ToList(), "Recipients drops it");

            online.AddRange(new[] { a1Leaving, a1 });
            CollectionAssert.AreEqual(new[] { a1 }, AvatarBlessing.OnlineMembers(Monarch).ToList(), "OnlineMembers drops it");
        }

        // ---- the purchase ---------------------------------------------------------------------------------------

        [TestMethod]
        public void Buy_GrantsEachOnlineMemberExactlyTheBankSeconds_AndDebitsThePrice()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            var vassal = Member(VassalGuid, VassalAcct, Monarch);
            vassal.OfflineExperienceBonusRemaining = 86400; // at the cap
            online.AddRange(new[] { buyer, vassal });

            Assert.IsTrue(Buy(npc, out var line), line);

            Assert.AreEqual(9_550UL, state.Favor, "Favor drops by exactly 250 + 100 x 2");
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual(2, queued.Count, "one grant per recipient, each on its own player's thread");
            Assert.AreEqual(0.0, buyer.OfflineExperienceBonusRemaining, "nothing lands until the recipient's thread runs it");

            RunGrants();

            Assert.AreEqual(3600.0, buyer.OfflineExperienceBonusRemaining, "an empty bank gains exactly 3600");
            Assert.AreEqual(86400.0 + 3600, vassal.OfflineExperienceBonusRemaining, "a bank at the cap gains exactly 3600 past it");
            CollectionAssert.AreEquivalent(new[] { buyer, vassal }, saved, "each recipient is saved promptly");
            Assert.AreEqual(2, toPlayers.Count(t => t.Text == "The Avatar's Blessing adds an hour to your offline bonus."));
            Assert.AreEqual(1, allegiance.Count);
            Assert.AreEqual(Monarch, allegiance[0].Monarch);
            Assert.AreEqual($"{buyer.Name} has called down the Avatar's Blessing: 2 members online gain an hour of bonus experience.", allegiance[0].Text);
            Assert.IsTrue(allegiance[0].Text.All(c => c < 128) && toPlayers.All(t => t.Text.All(c => c < 128)), "ASCII");
        }

        [TestMethod]
        public void Buy_OfflineMember_AndALateLogin_GetNothing()
        {
            InitActive();
            var npc = MakeVendor();
            var offline = Member(VassalGuid, VassalAcct, Monarch); // a member, but not in the online list
            var other = Member(0x7EB00009, 909, 0x5555); // online, another allegiance
            online.AddRange(new[] { buyer, other });

            Assert.IsTrue(Buy(npc, out var line), line);

            var late = Member(0x7EB0000A, 910, Monarch);
            online.Add(late); // logs in after the purchase
            RunGrants();

            Assert.AreEqual(3600.0, buyer.OfflineExperienceBonusRemaining);
            Assert.AreEqual(0.0, offline.OfflineExperienceBonusRemaining, "an offline member gets nothing");
            Assert.AreEqual(0.0, other.OfflineExperienceBonusRemaining, "another allegiance's member gets nothing");
            Assert.AreEqual(0.0, late.OfflineExperienceBonusRemaining, "recipients are fixed at purchase");
            StringAssert.Contains(allegiance.Single().Text, ": 1 member online gains an hour");
        }

        [TestMethod]
        public void Buy_SameAccountCharacters_AreAllGranted_ButChargedOnce_MulesGetNothing()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            var buyerAlt = Member(0x7EB00011, MonarchAcct, Monarch);
            var mule = Member(0x7EB00012, 777, Monarch, mule: true);
            online.AddRange(new[] { buyer, buyerAlt, mule });

            Assert.IsTrue(Buy(npc, out var line), line);
            RunGrants();

            Assert.AreEqual(10_000UL - 350, state.Favor, "one counted account: 250 + 100");
            Assert.AreEqual(0.0, mule.OfflineExperienceBonusRemaining, "a mule gets nothing");
            Assert.AreEqual(3600.0, buyer.OfflineExperienceBonusRemaining, "the buyer is granted");
            Assert.AreEqual(3600.0, buyerAlt.OfflineExperienceBonusRemaining, "a second character on the same account is granted too");
            StringAssert.Contains(allegiance.Single().Text, ": 2 members online gain an hour", "the line counts the characters granted");
        }

        [TestMethod]
        public void Buy_TwoSameAccountMembers_OneLoggingOut_OnlyTheOtherIsGrantedAndCounted()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            var vassal = Member(VassalGuid, VassalAcct, Monarch);
            var vassalLeaving = Member(0x7EB00013, VassalAcct, Monarch);
            vassalLeaving.IsLoggingOut = true;
            online.AddRange(new[] { buyer, vassal, vassalLeaving });

            Assert.AreEqual(450UL, AvatarBlessing.CurrentPrice(state, T), "the panel price counts two accounts");
            Assert.IsTrue(Buy(npc, out var line), line);
            Assert.AreEqual(2, queued.Count, "no grant is queued for the character logging out");
            RunGrants();

            Assert.AreEqual(10_000UL - 450, state.Favor, "two counted accounts: 250 + 100 x 2");
            Assert.AreEqual(3600.0, vassal.OfflineExperienceBonusRemaining, "the other character on that account is granted");
            Assert.AreEqual(0.0, vassalLeaving.OfflineExperienceBonusRemaining, "the character logging out is not");
            CollectionAssert.DoesNotContain(saved, vassalLeaving);
            StringAssert.Contains(allegiance.Single().Text, ": 2 members online gain an hour");
        }

        [TestMethod]
        public void SecondBuySameDay_Refused_WithTheCountdown_NextEasternDayAllowed()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            online.Add(buyer);
            Assert.IsTrue(Buy(npc, out _));
            var favorAfterFirst = state.Favor;

            Assert.IsFalse(Buy(npc, out var line));
            Assert.AreEqual("Your allegiance has already received the Avatar's Blessing today. It can be called again in 17h 0m.", line);
            Assert.AreEqual(favorAfterFirst, state.Favor, "nothing charged");
            Assert.AreEqual(1, queued.Count, "only the first purchase granted anything");

            AvatarBlessing.UtcNow = () => new DateTime(2027, 1, 16, 4, 59, 59, DateTimeKind.Utc);
            Assert.IsFalse(Buy(npc, out _), "11:59:59 PM Eastern is still the same day");

            AvatarBlessing.UtcNow = () => new DateTime(2027, 1, 16, 5, 0, 0, DateTimeKind.Utc);
            Assert.IsTrue(Buy(npc, out line), "12:00 AM Eastern: " + line);
        }

        [TestMethod]
        public void TheBlessing_AndThePrivateEvent_HaveSeparateDailySlots()
        {
            InitActive(favor: 10_000);
            var npc = MakeVendor();
            online.Add(buyer);
            var day = AvatarDay.BuildClock(T, Now);

            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day));
            AvatarManager.CommitDaily(AvatarId, AvatarDailyKind.Event, day);

            Assert.IsTrue(Buy(npc, out var line), "a private event used today does not use the Blessing: " + line);
        }

        [TestMethod]
        public void NotEnoughFavor_StatesThePriceAndTheMembers_NothingHeld()
        {
            var state = InitActive(favor: 400);
            var npc = MakeVendor();
            online.AddRange(new[] { buyer, Member(VassalGuid, VassalAcct, Monarch) });

            Assert.IsFalse(Buy(npc, out var line));

            Assert.AreEqual("The Avatar does not have enough Favor. The Blessing costs 450 Favor for 2 members online.", line);
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual((0, 0), DayCount(), "no claim held");
            Assert.AreEqual(0, queued.Count);
        }

        [TestMethod]
        public void NobodyToReceive_Refused()
        {
            var state = InitActive();
            var npc = MakeVendor();
            online.Add(Member(0x7EB00021, 621, Monarch, mule: true));

            Assert.IsFalse(Buy(npc, out var line));

            Assert.AreEqual("No member online can receive the Blessing today.", line);
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual((0, 0), DayCount());
        }

        // ---- once per character per Avatar day ------------------------------------------------------------------

        private static void Stamp(Player p, DateTime utc) =>
            p.SetProperty(PropertyInt64.AllegianceBlessingReceivedAt, new DateTimeOffset(utc).ToUnixTimeSeconds());

        [TestMethod]
        public void StampedToday_ExcludedFromRecipientsAndPrice_StampedYesterdayIncluded()
        {
            var day = AvatarDay.BuildClock(T, Now);
            var blessed = Member(0x7EB00041, 641, Monarch);
            var yesterday = Member(0x7EB00042, 642, Monarch);
            var fresh = Member(0x7EB00043, 643, Monarch);
            Stamp(blessed, new DateTime(2027, 1, 15, 6, 0, 0, DateTimeKind.Utc)); // after the 05:00Z reset: today
            Stamp(yesterday, new DateTime(2027, 1, 15, 4, 59, 0, DateTimeKind.Utc)); // before the reset: yesterday

            var r = AvatarBlessing.Recipients(new[] { fresh, yesterday, blessed }, day);

            CollectionAssert.AreEqual(new[] { yesterday, fresh }, r.ToList());
            Assert.AreEqual(2, AvatarBlessing.CountedAccounts(r), "the blessed member is not priced");
            Assert.AreEqual(3, AvatarBlessing.Recipients(new[] { fresh, yesterday, blessed }).Count, "control: no clock, no stamp filter");

            var state = InitActive();
            online.AddRange(new[] { buyer, blessed });
            Stamp(buyer, new DateTime(2027, 1, 15, 4, 59, 0, DateTimeKind.Utc));
            Assert.AreEqual(350UL, AvatarBlessing.CurrentPrice(state, T), "the panel price counts only the buyer");
        }

        [TestMethod]
        public void GrantWritesTheStamp()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            online.Add(buyer);

            Assert.IsNull(buyer.GetProperty(PropertyInt64.AllegianceBlessingReceivedAt));
            Assert.IsTrue(Buy(npc, out var line), line);
            RunGrants();

            Assert.AreEqual(new DateTimeOffset(Now).ToUnixTimeSeconds(), buyer.GetProperty(PropertyInt64.AllegianceBlessingReceivedAt));
            Assert.AreEqual(3600.0, buyer.OfflineExperienceBonusRemaining);
        }

        [TestMethod]
        public void StampedBetweenPurchaseAndGrant_RealBuy_SkipsBankTellAndSave()
        {
            InitActive(favor: 10_000);
            var npc = MakeVendor();
            var vassal = Member(VassalGuid, VassalAcct, Monarch);
            online.AddRange(new[] { buyer, vassal });

            Assert.IsTrue(Buy(npc, out var line), line);
            Stamp(vassal, Now.AddMinutes(-1)); // another Avatar's grant landed after this purchase filtered
            RunGrants();

            Assert.AreEqual(0.0, vassal.OfflineExperienceBonusRemaining, "no bank seconds");
            Assert.AreEqual(0, toPlayers.Count(x => x.To == vassal), "no tell line");
            CollectionAssert.DoesNotContain(saved, vassal, "no save");
            Assert.AreEqual(3600.0, buyer.OfflineExperienceBonusRemaining, "control: the other recipient is granted");
            Assert.AreEqual(1, toPlayers.Count(x => x.To == buyer));
        }

        [TestMethod]
        public void ZeroSecondGrant_DoesNotStampOrTell()
        {
            var day = AvatarDay.BuildClock(T, Now);
            var p = Member(0x7EB00061, 661, Monarch);

            Assert.IsFalse(AvatarBlessing.GrantBank(p, 0, day, 7));

            Assert.IsNull(p.GetProperty(PropertyInt64.AllegianceBlessingReceivedAt), "a 0-second Blessing does not burn the day");
            Assert.AreEqual(0, saved.Count);
            Assert.IsTrue(AvatarBlessing.GrantBank(p, 3600, day, 7), "control: a real grant still lands the same day");
        }

        [TestMethod]
        public void EveryOnlineMemberAlreadyBlessedToday_RefusesWithTheTodayLine_NothingCharged()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            online.Add(buyer);
            Stamp(buyer, Now.AddHours(-1)); // blessed by another Avatar earlier today

            Assert.IsFalse(Buy(npc, out var line));

            Assert.AreEqual("No member online can receive the Blessing today.", line);
            Assert.AreEqual(10_000UL, state.Favor, "nothing charged");
            Assert.AreEqual((0, 0), DayCount(), "no claim held");
            Assert.AreEqual(0, queued.Count);
        }

        [TestMethod]
        public void TwoAvatarsSameDay_SharedMemberBankGrowsOnce_AndTheSecondGrantIsSkipped()
        {
            var day = AvatarDay.BuildClock(T, Now);
            var shared = Member(0x7EB00051, 651, Monarch);

            Assert.IsTrue(AvatarBlessing.GrantBank(shared, 3600, day, 7), "first Avatar lands");
            Assert.IsFalse(AvatarBlessing.GrantBank(shared, 3600, day, 8), "second Avatar's grant is skipped, whatever its purchase saw");

            Assert.AreEqual(3600.0, shared.OfflineExperienceBonusRemaining, "the bank grew once");
            Assert.AreEqual(new DateTimeOffset(Now).ToUnixTimeSeconds(), shared.GetProperty(PropertyInt64.AllegianceBlessingReceivedAt));
            CollectionAssert.AreEqual(new[] { shared }, saved, "saved once");

            // End to end: both purchases queued their grant before either ran.
            var other = Member(0x7EB00052, 652, Monarch);
            var b1 = new List<Action>();
            AvatarBlessing.RunOnPlayer = (p, work) => b1.Add(work);
            InitActive(favor: 10_000);
            var npc = MakeVendor();
            online.AddRange(new[] { buyer, other });
            Assert.IsTrue(Buy(npc, out var line), line);
            // A second Avatar's purchase would have queued the same member too; model it with a direct second grant after the first.
            foreach (var w in b1) w();
            Assert.IsFalse(AvatarBlessing.GrantBank(other, 3600, day, 8));
            Assert.AreEqual(3600.0, other.OfflineExperienceBonusRemaining);
            Assert.AreEqual(1, toPlayers.Count(t => t.To == other), "one recipient line only");
        }

        [TestMethod]
        public void OfflineBonusDisabled_Refused_NothingReservedOrClaimed()
        {
            var state = InitActive();
            var npc = MakeVendor();
            online.Add(buyer);
            AvatarBlessing.OfflineBonusEnabled = () => false;

            Assert.IsFalse(Buy(npc, out var line));

            Assert.AreEqual("The Avatar's Blessing cannot be called while the offline experience bonus is switched off.", line);
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual(10_000UL, state.Favor);
            Assert.AreEqual((0, 0), DayCount());
            Assert.AreEqual(0, queued.Count);
        }

        [TestMethod]
        public void PlainMember_Refused()
        {
            InitActive();
            var npc = MakeVendor();
            online.Add(buyer);
            var plain = Member(0x7EB00031, 631, Monarch);

            // CanSpend: the monarch (buyer) may; a plain member who is neither monarch, Keeper nor Seneschal may not.
            Assert.IsFalse(AvatarBlessing.TryBuy(plain, npc, out var line));
            Assert.AreEqual(AvatarBlessing.LineFor(AvatarBlessingRefusal.NotPermitted, 0, 0), line);
            Assert.AreEqual((0, 0), DayCount());
        }

        [TestMethod]
        public void FailedReserve_ReleasesTheClaim()
        {
            var state = InitActive();
            var npc = MakeVendor();
            online.Add(buyer);
            AvatarBlessing.ReserveFavor = (id, price) => false;

            Assert.IsFalse(Buy(npc, out var line));

            StringAssert.StartsWith(line, "The Avatar does not have enough Favor.");
            Assert.AreEqual((0, 0), DayCount(), "the claim was given back");
            Assert.AreEqual(0, queued.Count);

            AvatarBlessing.ReserveFavor = AvatarManager.TryReserveFavor;
            Assert.IsTrue(Buy(npc, out line), line);
        }

        [TestMethod]
        public void EveryGrantFailsToQueue_ReleasesBoth()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            online.AddRange(new[] { buyer, Member(VassalGuid, VassalAcct, Monarch) });
            AvatarBlessing.RunOnPlayer = (p, work) => throw new InvalidOperationException("no action queue");

            Assert.IsFalse(Buy(npc, out var line));

            Assert.AreEqual("No member online can receive the Blessing today.", line);
            Assert.AreEqual(10_000UL, state.Favor, "nothing spent");
            Assert.AreEqual(0UL, state.ReservedFavor, "the reservation was released");
            Assert.AreEqual((0, 0), DayCount(), "the claim was released");
            Assert.AreEqual(0, allegiance.Count, "nothing announced");

            AvatarBlessing.RunOnPlayer = (p, work) => queued.Add((p, work));
            Assert.IsTrue(Buy(npc, out line), "the day is still free: " + line);
        }

        [TestMethod]
        public void OneGrantFailsToQueue_TheOthersStand_AndThePurchaseCommits()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            var vassal = Member(VassalGuid, VassalAcct, Monarch);
            online.AddRange(new[] { buyer, vassal });
            AvatarBlessing.RunOnPlayer = (p, work) => { if (p == vassal) throw new InvalidOperationException("gone"); queued.Add((p, work)); };

            Assert.IsTrue(Buy(npc, out var line), line);
            RunGrants();

            Assert.AreEqual(9_550UL, state.Favor, "priced at purchase for both");
            Assert.AreEqual((1, 0), DayCount(), "the day is used");
            Assert.AreEqual(3600.0, buyer.OfflineExperienceBonusRemaining);
            StringAssert.Contains(allegiance.Single().Text, ": 1 member online gains", "the announcement counts the grants that were queued");
        }

        [TestMethod]
        public void TheCommittedClaim_IsFlushedOnTheAvatarRow()
        {
            InitActive();
            var npc = MakeVendor();
            online.Add(buyer);

            Assert.IsTrue(Buy(npc, out _));
            AvatarManager.OnShutdown();

            var row = store.Saved.Last(a => a.AvatarId == AvatarId);
            Assert.AreEqual(Now, row.BlessingBoughtAt, "blessing_Bought_At carries the purchase instant");
            Assert.IsNull(row.LastEventAt, "the Blessing never stamps the event slot");
        }

        [TestMethod]
        public void StatusLine_ShowsTodaysBlessing()
        {
            var state = InitActive();
            var npc = MakeVendor();
            online.Add(buyer);

            Assert.IsTrue(AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "a").Contains("Allegiance Blessing: available today."));

            Assert.IsTrue(Buy(npc, out _));
            var lines = AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "a");

            Assert.IsTrue(lines.Contains("Allegiance Blessing: called today. It can be called again in 17h 0m."), string.Join(" | ", lines));
            StringAssert.StartsWith(lines.Last(), "Next unlock:");
        }

        [TestMethod]
        public void Duration_ReadsNaturally()
        {
            Assert.AreEqual("an hour", AvatarBlessing.Duration(3600));
            Assert.AreEqual("2 hours", AvatarBlessing.Duration(7200));
            Assert.AreEqual("30 minutes", AvatarBlessing.Duration(1800));
            Assert.AreEqual("a minute", AvatarBlessing.Duration(30));
            Assert.AreEqual("The Avatar's Blessing adds 2 hours to your offline bonus.", AvatarBlessing.RecipientLine(7200));
        }

        private sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public readonly List<AllegianceAvatar> Saved = new List<AllegianceAvatar>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => new List<AllegianceAvatarContribution>();

            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done)
            {
                Saved.AddRange(a);
                done(true);
            }

            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);
        }
    }
}
