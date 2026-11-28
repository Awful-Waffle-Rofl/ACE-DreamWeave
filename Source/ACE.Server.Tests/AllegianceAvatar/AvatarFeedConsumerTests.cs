using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Auth;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Managers;
using ACE.Server.Tests.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// Pins the consumer: what reaches AvatarFeed.Sink when a seeded Player runs the real GrantXP. The sink throws a
    /// recorder; a seeded Player has no action queue, so GrantXP ends in the NullReferenceException from EnqueueAction, which Grant swallows after checking that is where it ended.
    /// </summary>
    [TestClass]
    public class AvatarFeedConsumerTests
    {
        private const string EnabledKey = "allegiance_avatar_enabled";
        private const uint MonarchGuid = 0x50000001; // PvpTemplateCoreTests.PlayerGuid: the seeded player IS the monarch in the monarch test
        private const uint OtherMonarch = 0x50000777;
        private const uint AccountId = 4242;

        private sealed class EmptyStore : IAvatarStore
        {
            public List<AllegianceAvatar> LoadAll() => new List<AllegianceAvatar>();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => new List<AllegianceAvatarContribution>();
            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done) => done(true);
            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 1);
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Account>k__BackingField")]
        private static extern ref Account AccountField(Player player);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "altCharacterBonusThreshold")]
        private static extern ref long AltThreshold(Player player);

        private Action<AvatarFeedEvent> savedSink;
        private Action<uint, long> savedLevelSink;
        private List<(uint Monarch, long Amount)> levelCaptured;
        private bool savedEnabled;
        private Func<Player, (int MaxLevel, long MaxLevelXp)> savedCeiling;

        private const int Max = 100;
        private const long MaxXp = 5000;
        private List<AvatarFeedEvent> captured;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void Setup()
        {
            savedSink = AvatarFeed.Sink;
            savedLevelSink = AvatarFeed.LevelSink;
            levelCaptured = new List<(uint, long)>();
            AvatarFeed.LevelSink = (m, a) => levelCaptured.Add((m, a));
            savedCeiling = AvatarFeed.CeilingSource;
            AvatarFeed.CeilingSource = p => (Max, MaxXp); // the real chart needs the client dat
            captured = new List<AvatarFeedEvent>();
            AvatarFeed.Sink = e => captured.Add(e);

            savedEnabled = PropertyManager.GetBool(EnabledKey).Item;
            PropertyManager.ModifyBool(EnabledKey, true);
            EnableManager();
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarFeed.Sink = savedSink;
            AvatarFeed.LevelSink = savedLevelSink;
            AvatarFeed.CeilingSource = savedCeiling;
            PropertyManager.ModifyBool(EnabledKey, savedEnabled);

            // Leave the static manager disabled for whichever class runs next.
            AvatarManager.Initialize(new NullStore(), () => AvatarTuning.Defaults, () => 0, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
        }

        private sealed class NullStore : IAvatarStore
        {
            public List<AllegianceAvatar> LoadAll() => null;
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => null;
            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done) => done(false);
            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(false, 0);
        }

        private static void EnableManager()
        {
            AvatarManager.Initialize(new EmptyStore(), () => AvatarTuning.Defaults, () => 1_800_000_000.0, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
            Assert.IsTrue(AvatarManager.Enabled, "the manager must be enabled for the hook to feed");
        }

        private static Allegiance AllegianceOf(uint monarch)
        {
            var allegiance = (Allegiance)RuntimeHelpers.GetUninitializedObject(typeof(Allegiance));
            var biota = new ACE.Entity.Models.Biota { WeenieType = WeenieType.Allegiance, PropertiesIID = new Dictionary<PropertyInstanceId, uint> { { PropertyInstanceId.Monarch, monarch } } };
            PvpTemplatePlayerTests.SeedWorldObject(allegiance, biota, 0x70000001);
            return allegiance;
        }

        private static Player Earner(uint? monarch = OtherMonarch, int level = 1, long totalXp = 0)
        {
            var biota = PvpTemplateCoreTests.OwnBiota();
            biota.PropertiesInt[PropertyInt.Level] = level;
            biota.PropertiesInt64[PropertyInt64.TotalExperience] = totalXp;
            var player = PvpTemplatePlayerTests.SeededPlayer(biota);

            // Location reads these two caches, and a seeded Player is in no world: with them empty it is null.
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), player, "positionCache", new Dictionary<PositionType, Position>());
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), player, "ephemeralPositions", new Dictionary<PositionType, Position>());
            AccountField(player) = new Account { AccountId = AccountId };

            if (monarch != null)
                player.Allegiance = AllegianceOf(monarch.Value);

            return player;
        }

        /// <summary>Runs the real GrantXP. A seeded Player ends it in EnqueueAction (no action queue); anything else is a failure.</summary>
        private static void Grant(Player player, long amount, XpType type, bool combatShare = false)
        {
            try
            {
                player.GrantXP(amount, type, ShareType.None, combatShare);
                Assert.Fail("GrantXP was expected to end in EnqueueAction on a seeded Player");
            }
            catch (NullReferenceException ex) when (ex.StackTrace != null && ex.StackTrace.Contains("EnqueueAction"))
            {
            }
        }

        // ---- the level pool hook (AvatarFeed.CaptureLevel) ----

        [TestMethod]
        public void CaptureLevel_CountedTypes_EnqueueTheAddedAmount()
        {
            foreach (var type in new[] { XpType.Kill, XpType.Quest, XpType.Fellowship, XpType.Proficiency })
                AvatarFeed.CaptureLevel(Earner(), 1000, type);

            Assert.AreEqual(4, levelCaptured.Count);
            Assert.IsTrue(levelCaptured.All(c => c.Monarch == OtherMonarch && c.Amount == 1000L));
        }

        [TestMethod]
        public void CaptureLevel_MonarchOwnKill_ResolvesTheMonarchThroughTheAllegiance()
        {
            AvatarFeed.CaptureLevel(Earner(MonarchGuid), 500, XpType.Kill);

            CollectionAssert.AreEqual(new[] { (MonarchGuid, 500L) }, levelCaptured);
        }

        [TestMethod]
        public void CaptureLevel_AllegiancePassupAdminAndEmote_EnqueueNothing()
        {
            foreach (var type in new[] { XpType.Allegiance, XpType.Admin, XpType.Emote })
                AvatarFeed.CaptureLevel(Earner(), 1000, type);

            Assert.AreEqual(0, levelCaptured.Count);
        }

        [TestMethod]
        public void CaptureLevel_ZeroOrNegativeAdded_EnqueuesNothing()
        {
            AvatarFeed.CaptureLevel(Earner(), 0, XpType.Kill);
            AvatarFeed.CaptureLevel(Earner(), -5, XpType.Kill);

            Assert.AreEqual(0, levelCaptured.Count);
        }

        [TestMethod]
        public void CaptureLevel_NoAllegiance_SettingOff_ManagerDisabled_EnqueueNothing()
        {
            AvatarFeed.CaptureLevel(Earner(monarch: null), 1000, XpType.Kill);
            Assert.AreEqual(0, levelCaptured.Count, "no allegiance");

            PropertyManager.ModifyBool(EnabledKey, false);
            AvatarFeed.CaptureLevel(Earner(), 1000, XpType.Kill);
            Assert.AreEqual(0, levelCaptured.Count, "setting off");

            PropertyManager.ModifyBool(EnabledKey, true);
            AvatarManager.Initialize(new NullStore(), () => AvatarTuning.Defaults, () => 0, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
            AvatarFeed.CaptureLevel(Earner(), 1000, XpType.Kill);
            Assert.AreEqual(0, levelCaptured.Count, "manager disabled");
        }

        [TestMethod]
        public void CaptureLevel_SinkThrowing_DoesNotThrow()
        {
            AvatarFeed.LevelSink = (m, a) => throw new InvalidOperationException("level feed failure");

            AvatarFeed.CaptureLevel(Earner(), 1000, XpType.Kill);
        }

        /// <summary>The Favor hook still ran for the same grant: the pool feed is additive and leaves Capture untouched.</summary>
        [TestMethod]
        public void RealGrant_FeedsTheFavorSink_AndTheLevelHookIsNotInGrantXpItself()
        {
            Grant(Earner(), 1000, XpType.Kill);

            Assert.AreEqual(1, captured.Count, "Capture still feeds Favor from GrantXP");
            Assert.AreEqual(0, levelCaptured.Count, "the pool feed runs in UpdateXpAndLevel on the player's thread, which this harness never reaches");
        }

        [TestMethod]
        public void Capture_Kill_Feeds_TheReceivedAmount_AsBase()
        {
            Grant(Earner(), 1000, XpType.Kill);

            Assert.AreEqual(1, captured.Count);
            Assert.AreEqual(1000L, captured[0].Amount);
            Assert.AreEqual(AvatarFeedSource.Base, captured[0].Source);
            Assert.AreEqual(OtherMonarch, captured[0].MonarchGuid);
            Assert.AreEqual(AccountId, captured[0].AccountId);
        }

        // ---- offline bank bonus XP counts toward Favor (owner ruling 2026-11-25) ----

        /// <summary>Seeds the offline and alt bonus keys GrantXP reads, runs <paramref name="body"/>, and restores them.</summary>
        private static void WithBonusKeys(bool offlineOn, double offlineMultiplier, bool altOn, Action body)
        {
            var keys = (Enabled: PropertyManager.GetBool("offline_bonus_enabled").Item, Mult: PropertyManager.GetDouble("offline_bonus_multiplier").Item,
                AltOn: PropertyManager.GetBool("alt_character_bonus_enabled").Item, AltMult: PropertyManager.GetDouble("alt_character_bonus_multiplier").Item,
                Gap: PropertyManager.GetLong("alt_character_bonus_gap").Item);
            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool("offline_bonus_enabled", offlineOn));
                Assert.IsTrue(PropertyManager.ModifyDouble("offline_bonus_multiplier", offlineMultiplier, init: true));
                Assert.IsTrue(PropertyManager.ModifyBool("alt_character_bonus_enabled", altOn));
                Assert.IsTrue(PropertyManager.ModifyDouble("alt_character_bonus_multiplier", 1.0, init: true));
                Assert.IsTrue(PropertyManager.ModifyLong("alt_character_bonus_gap", 0));
                body();
            }
            finally
            {
                PropertyManager.ModifyBool("offline_bonus_enabled", keys.Enabled);
                PropertyManager.ModifyDouble("offline_bonus_multiplier", keys.Mult, init: true);
                PropertyManager.ModifyBool("alt_character_bonus_enabled", keys.AltOn);
                PropertyManager.ModifyDouble("alt_character_bonus_multiplier", keys.AltMult, init: true);
                PropertyManager.ModifyLong("alt_character_bonus_gap", keys.Gap);
            }
        }

        [TestMethod]
        public void RealGrant_WithABankedOfflineBonus_FeedsTheBoostedAmount() => WithBonusKeys(true, 1.0, false, () =>
        {
            var player = Earner();
            player.OfflineExperienceBonusRemaining = 3600;

            Grant(player, 1000, XpType.Kill);

            Assert.AreEqual(1, captured.Count);
            Assert.AreEqual(2000L, captured[0].Amount, "multiplier 1.0 (+100%): the Favor feed sees the doubled amount");
        });

        [TestMethod]
        public void RealGrant_WithAnEmptyBank_FeedsTheRawAmount() => WithBonusKeys(true, 1.0, false, () =>
        {
            Grant(Earner(), 1000, XpType.Kill);

            Assert.AreEqual(1000L, captured.Single().Amount, "control: no bank, no boost");
        });

        [TestMethod]
        public void RealGrant_AltBonus_IsNotFed_ButTheOfflineBonusIs() => WithBonusKeys(true, 1.0, true, () =>
        {
            var player = Earner();
            player.OfflineExperienceBonusRemaining = 3600;
            AltThreshold(player) = 1_000_000; // far behind the account's best character: the alt bonus is active
            Assert.IsTrue(player.IsAltCharacterBonusActive, "control: the alt bonus applies to this grant");
            Assert.AreEqual(4000L, player.ApplyAltCharacterBonus(2000), "control: after the feed the alt bonus doubles again");

            Grant(player, 1000, XpType.Kill);

            Assert.AreEqual(2000L, captured.Single().Amount, "offline-boosted, and taken before the alt bonus (not 4000)");
        });

        [TestMethod]
        public void RealGrant_FellowshipShareOfACombatKill_FeedsTheBoostedAmount() => WithBonusKeys(true, 1.0, false, () =>
        {
            var player = Earner();
            player.OfflineExperienceBonusRemaining = 3600;

            Grant(player, 1000, XpType.Fellowship, combatShare: true);

            Assert.AreEqual(2000L, captured.Single().Amount, "a fellow's combat share takes the receiver's offline bonus, and Favor sees it");
        });

        [TestMethod]
        public void RealGrant_FellowshipShareNotFromCombat_FeedsTheRawAmount() => WithBonusKeys(true, 1.0, false, () =>
        {
            var player = Earner();
            player.OfflineExperienceBonusRemaining = 3600;

            Grant(player, 1000, XpType.Fellowship, combatShare: false);

            Assert.AreEqual(1000L, captured.Single().Amount, "control: a non-combat Fellowship share takes no offline bonus");
        });

        [TestMethod]
        public void RealGrant_QuestXp_TakesNoOfflineBonus_SoFeedsTheRawAmount() => WithBonusKeys(true, 1.0, false, () =>
        {
            var player = Earner();
            player.OfflineExperienceBonusRemaining = 3600;

            Grant(player, 1000, XpType.Quest);

            Assert.AreEqual(1000L, captured.Single().Amount, "quest XP never reaches the offline bonus, so Favor sees it unboosted");
        });

        [TestMethod]
        public void Capture_MonarchOwnKill_Feeds()
        {
            // The monarch's own MonarchId is null (AllegianceManager.cs:36-37); the Allegiance object knows the guid.
            var monarch = Earner(MonarchGuid);
            Assert.IsNull(monarch.MonarchId, "the earner is the monarch: its own MonarchId is null");

            Grant(monarch, 1000, XpType.Kill);

            Assert.AreEqual(1, captured.Count);
            Assert.AreEqual(MonarchGuid, captured[0].MonarchGuid);
            Assert.AreEqual(1000L, captured[0].Amount);
        }

        [TestMethod]
        public void Capture_QuestXp_Feeds()
        {
            Grant(Earner(), 750, XpType.Quest);

            Assert.AreEqual(1, captured.Count);
            Assert.AreEqual(750L, captured[0].Amount);
        }

        [TestMethod]
        public void Capture_AllegiancePassup_DoesNotFeed()
        {
            AvatarFeed.Capture(Earner(), 1000, XpType.Allegiance);
            Assert.AreEqual(0, captured.Count);
        }

        [TestMethod]
        public void Capture_NoAllegiance_DoesNotFeed()
        {
            AvatarFeed.Capture(Earner(monarch: null), 1000, XpType.Kill);
            Assert.AreEqual(0, captured.Count);
        }

        [TestMethod]
        public void Capture_SettingOff_DoesNotFeed()
        {
            PropertyManager.ModifyBool(EnabledKey, false);

            AvatarFeed.Capture(Earner(), 1000, XpType.Kill);
            Assert.AreEqual(0, captured.Count);
        }

        [TestMethod]
        public void Capture_ManagerDisabled_DoesNotFeed()
        {
            AvatarManager.Initialize(new NullStore(), () => AvatarTuning.Defaults, () => 0, () => new AccountActivityIndex(), () => new List<AvatarRoster>());

            AvatarFeed.Capture(Earner(), 1000, XpType.Kill);
            Assert.AreEqual(0, captured.Count);
        }

        [TestMethod]
        public void Capture_ZeroAmount_DoesNotFeed()
        {
            AvatarFeed.Capture(Earner(), 0, XpType.Kill);
            Assert.AreEqual(0, captured.Count);
        }

        [TestMethod]
        public void Capture_SinkThrowing_DoesNotAbortTheGrant()
        {
            AvatarFeed.Sink = e => throw new InvalidOperationException("feed failure");

            // Grant fails the test unless GrantXP still ran on to EnqueueAction after the hook.
            Grant(Earner(), 1000, XpType.Kill);
        }

        [TestMethod]
        public void Capture_NearTheLevelCeiling_FeedsOnlyWhatIsReceived()
        {
            Grant(Earner(level: Max - 1, totalXp: MaxXp - 300), 1000, XpType.Kill);

            Assert.AreEqual(1, captured.Count);
            Assert.AreEqual(300L, captured[0].Amount);
        }

        [TestMethod]
        public void Capture_AtTheHardCeiling_FeedsNothing()
        {
            AvatarFeed.Capture(Earner(level: Max, totalXp: MaxXp), 1000, XpType.Kill);

            Assert.AreEqual(0, captured.Count);
        }

        [TestMethod]
        public void ResolveMonarch_MonarchWithNullMonarchId_UsesTheAllegiance()
        {
            var monarch = Earner(MonarchGuid);
            Assert.IsNull(monarch.MonarchId);
            Assert.AreEqual((uint?)MonarchGuid, AvatarFeed.ResolveMonarch(monarch));
            Assert.IsNull(AvatarFeed.ResolveMonarch(Earner(monarch: null)));
        }

        [TestMethod]
        public void EnterSplit_ExitSplit_RestoreThePreviousEarner()
        {
            var a = Earner();
            var b = Earner();

            var prev = AvatarFeed.EnterSplit(a);
            Assert.IsNull(prev);
            var inner = AvatarFeed.EnterSplit(b);
            Assert.AreSame(a, inner);
            AvatarFeed.ExitSplit(inner);
            AvatarFeed.ExitSplit(prev);
            Assert.IsNull(AvatarFeed.EnterSplit(a), "back to no earner after both exits");
            AvatarFeed.ExitSplit(null);
        }

        private static Position At(int blockX, float x) => new Position(((uint)blockX << 24) | (1u << 16) | 0x0064u, x, 0f, 0f, 0f, 0f, 0f, 1f, 0);

        private static Player Located(Position at)
        {
            var player = Earner();
            player.Location = at;
            return player;
        }

        [TestMethod]
        public void MemberFacts_Monarch_ResolvesThroughTheAllegiance()
        {
            var monarch = Earner(MonarchGuid);
            Assert.IsNull(monarch.MonarchId);

            var facts = AvatarFeed.MemberFacts(monarch, null, monarch);

            Assert.AreEqual((uint?)MonarchGuid, facts.monarch);
            Assert.AreEqual((uint?)AccountId, facts.accountId);
            Assert.IsTrue(facts.inRange, "the receiver is always in range of themselves");
        }

        [TestMethod]
        public void MemberFacts_MemberBeyondShareRange_IsOutOfRange()
        {
            var self = Located(At(10, 0));
            var near = Located(At(11, 0));   // 192 units
            var far = Located(At(16, 148));  // 1300 units

            Assert.IsTrue(AvatarFeed.MemberFacts(self, self.Location, near).inRange);
            Assert.IsFalse(AvatarFeed.MemberFacts(self, self.Location, far).inRange);
        }

        [TestMethod]
        public void SplitShare_RangeIsMeasuredFromTheEarner_NotTheReceiver()
        {
            var earner = Located(At(10, 0));     // E at 1920
            var receiver = Located(At(13, 124)); // 700 from E
            var fellow = Located(At(16, 148));   // 1300 from E, 600 from the receiver

            var prev = AvatarFeed.EnterSplit(earner);
            try
            {
                var anchor = AvatarFeed.RangeAnchor(receiver.Location);
                Assert.IsFalse(AvatarFeed.MemberFacts(receiver, anchor, fellow).inRange, "measured from E the fellow is 1300 away");
            }
            finally
            {
                AvatarFeed.ExitSplit(prev);
            }

            var own = AvatarFeed.RangeAnchor(receiver.Location);
            Assert.IsTrue(AvatarFeed.MemberFacts(receiver, own, fellow).inRange, "no split running (a quest grant): measured from the receiver, 600 away");
        }
    }
}
