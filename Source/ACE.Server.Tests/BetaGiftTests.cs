using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.BetaGift;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Dreamweave beta-tester gift trinket: BetaGiftRules.IsEligible (pure, no DB) and
    /// BetaGiftGrant.TryAddGiftToPack (the add-to-pack step CharacterHandler.GrantBetaGiftIfEligible calls,
    /// extracted because a live Player cannot be constructed in this project - Player's constructor makes an
    /// unconditional DatabaseManager.Authentication.GetAccountById round-trip; see MonsterEffectWardTests.cs's
    /// note on "new Player(" for the same limitation hit elsewhere in this suite).
    ///
    /// NOT covered here, and left for live/in-game verification (Docs/VERIFY-QUEUE.md): the actual
    /// CharacterHandler.CharacterCreateEx call site, PropertyManager reading the two beta_gift_* tunables
    /// live, and WorldObjectFactory.CreateNewWorldObject(beta_gift_wcid) resolving the real weenie from a
    /// live ace_world (that path needs DatabaseManager.World, also unavailable here).
    /// </summary>
    [TestClass]
    public class BetaGiftTests
    {
        private const long OneDaySeconds = 86400;

        // ---- BetaGiftRules.IsEligible ----

        [TestMethod]
        public void IsEligible_AccountCreatedBeforeCutoff_IsEligible()
        {
            var cutoff = BetaGiftRules.DefaultAccountCutoffUnix;
            var createTime = DateTimeOffset.FromUnixTimeSeconds(cutoff - OneDaySeconds).UtcDateTime;

            var eligible = BetaGiftRules.IsEligible(createTime, cutoff, 1006600, HeritageGroup.Aluvian, out var reason);

            Assert.IsTrue(eligible, reason);
        }

        [TestMethod]
        public void IsEligible_AccountCreatedExactlyAtCutoff_IsNotEligible()
        {
            var cutoff = BetaGiftRules.DefaultAccountCutoffUnix;
            var createTime = DateTimeOffset.FromUnixTimeSeconds(cutoff).UtcDateTime;

            var eligible = BetaGiftRules.IsEligible(createTime, cutoff, 1006600, HeritageGroup.Aluvian, out var reason);

            Assert.IsFalse(eligible);
            StringAssert.Contains(reason, "cutoff");
        }

        [TestMethod]
        public void IsEligible_AccountCreatedAfterCutoff_IsNotEligible()
        {
            var cutoff = BetaGiftRules.DefaultAccountCutoffUnix;
            var createTime = DateTimeOffset.FromUnixTimeSeconds(cutoff + OneDaySeconds).UtcDateTime;

            var eligible = BetaGiftRules.IsEligible(createTime, cutoff, 1006600, HeritageGroup.Aluvian, out var reason);

            Assert.IsFalse(eligible);
        }

        [TestMethod]
        public void IsEligible_WcidZero_DisablesTheGiftEvenBeforeCutoff()
        {
            var cutoff = BetaGiftRules.DefaultAccountCutoffUnix;
            var createTime = DateTimeOffset.FromUnixTimeSeconds(cutoff - OneDaySeconds).UtcDateTime;

            var eligible = BetaGiftRules.IsEligible(createTime, cutoff, 0, HeritageGroup.Aluvian, out var reason);

            Assert.IsFalse(eligible);
            StringAssert.Contains(reason, "disabled");
        }

        [TestMethod]
        public void IsEligible_OlthoiHeritage_IsSkippedEvenBeforeCutoff()
        {
            var cutoff = BetaGiftRules.DefaultAccountCutoffUnix;
            var createTime = DateTimeOffset.FromUnixTimeSeconds(cutoff - OneDaySeconds).UtcDateTime;

            var eligibleOlthoi = BetaGiftRules.IsEligible(createTime, cutoff, 1006600, HeritageGroup.Olthoi, out var reasonOlthoi);
            var eligibleOlthoiAcid = BetaGiftRules.IsEligible(createTime, cutoff, 1006600, HeritageGroup.OlthoiAcid, out var reasonAcid);

            Assert.IsFalse(eligibleOlthoi);
            StringAssert.Contains(reasonOlthoi, "Olthoi");
            Assert.IsFalse(eligibleOlthoiAcid);
            StringAssert.Contains(reasonAcid, "Olthoi");
        }

        [TestMethod]
        public void IsEligible_UnspecifiedKindDateTime_IsTreatedAsUtc_NotLocalConverted()
        {
            // Guards the exact trap the spec calls out: ace_auth.account.create_Time is always written with
            // DateTime.UtcNow, but Pomelo/MySql hands it back as Kind=Unspecified. One second before the
            // default cutoff (2026-10-01T23:59:59, Unspecified) must read as eligible - if this method ever
            // called DateTime.ToUniversalTime() on an Unspecified value instead of DateTime.SpecifyKind, the
            // host's local offset would shift the value and could push it to either side of the cutoff.
            var createTime = new DateTime(2026, 10, 1, 23, 59, 59, DateTimeKind.Unspecified);
            Assert.AreEqual(DateTimeKind.Unspecified, createTime.Kind, "test input must actually be Unspecified-kind to guard the trap");

            var eligible = BetaGiftRules.IsEligible(createTime, BetaGiftRules.DefaultAccountCutoffUnix, 1006600, HeritageGroup.Aluvian, out var reason);

            Assert.IsTrue(eligible, reason);
        }

        [TestMethod]
        public void IsEligible_NullAccountCreateTime_IsNotEligible_WithNoAccountReason()
        {
            // Guards CharacterHandler.GrantBetaGiftIfEligible's player.Account?.CreateTime call - a null
            // Account (GetAccountById can return null; AuthenticationDatabase.cs) must never NRE, and
            // CharacterHandler warns specifically on this reason (never on ordinary ineligibility).
            var eligible = BetaGiftRules.IsEligible(null, BetaGiftRules.DefaultAccountCutoffUnix, 1006600, HeritageGroup.Aluvian, out var reason);

            Assert.IsFalse(eligible);
            Assert.AreEqual(BetaGiftRules.NoAccountReason, reason);
        }

        [TestMethod]
        public void IsEligible_WcidAboveUintMaxValue_IsNotEligible()
        {
            // CharacterHandler casts giftWcid to uint before calling WorldObjectFactory.CreateNewWorldObject;
            // a value this large must be rejected here, not silently wrapped into an unrelated small wcid
            // by the cast.
            var cutoff = BetaGiftRules.DefaultAccountCutoffUnix;
            var createTime = DateTimeOffset.FromUnixTimeSeconds(cutoff - OneDaySeconds).UtcDateTime;

            var eligible = BetaGiftRules.IsEligible(createTime, cutoff, 5_000_000_000L, HeritageGroup.Aluvian, out var reason);

            Assert.IsFalse(eligible);
            StringAssert.Contains(reason, "disabled");
        }

        [TestMethod]
        public void DefaultAccountCutoffUnix_Equals20261002UtcMidnight()
        {
            var expected = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

            Assert.AreEqual(expected, BetaGiftRules.DefaultAccountCutoffUnix);
        }

        // ---- BetaGiftGrant.TryAddGiftToPack (the delivery path CharacterHandler calls) ----

        private static uint nextGuid = 0x7E000000;

        /// <summary>
        /// A bare pack Container, built the same way PossessionEnumerationTests.CreateSidePack is (no DB, no
        /// dat files) - stands in for the new character's main pack, which is what CharacterHandler actually
        /// hands to BetaGiftGrant.TryAddGiftToPack (a live Player IS a Container; see the class remarks).
        /// </summary>
        private static Container CreatePack(int itemsCapacity = 10)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 136,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemsCapacity, itemsCapacity },
                },
            };

            return new Container(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>
        /// A stand-in for the gift item, carrying the same non-stackable-jewelry shape and the three
        /// properties this build adds on top of the retail Pathwarden Trinket clone (GearMaxHealth, Attuned,
        /// Bonded) - built from an in-memory Weenie via WorldObjectFactory.CreateWorldObject(Weenie, ObjectGuid),
        /// which (unlike WorldObjectFactory.CreateNewWorldObject(wcid)) needs no live ace_world.
        /// </summary>
        private static WorldObject CreateGiftItemStandIn()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1006600,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Jewelry },
                    { PropertyInt.EncumbranceVal, 60 },
                    { PropertyInt.ValidLocations, (int)EquipMask.TrinketOne },
                    { PropertyInt.GearMaxHealth, 3 },
                    { PropertyInt.Attuned, (int)AttunedStatus.Attuned },
                    { PropertyInt.Bonded, (int)BondedStatus.Bonded },
                },
            };

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void TryAddGiftToPack_AddsTheItemToAnEmptyPack()
        {
            var pack = CreatePack();
            var gift = CreateGiftItemStandIn();

            var added = BetaGiftGrant.TryAddGiftToPack(pack, gift);

            Assert.IsTrue(added, "Container.TryAddToInventory should accept the gift into an empty pack");
            CollectionAssert.Contains(new List<WorldObject>(pack.Inventory.Values), gift);
            Assert.AreEqual((int)AttunedStatus.Attuned, gift.GetProperty(PropertyInt.Attuned));
            Assert.AreEqual((int)BondedStatus.Bonded, gift.GetProperty(PropertyInt.Bonded));
            Assert.AreEqual(3, gift.GetProperty(PropertyInt.GearMaxHealth));
        }

        [TestMethod]
        public void TryAddGiftToPack_FullPack_ReturnsFalse_AndNeverThrows()
        {
            var pack = CreatePack(itemsCapacity: 0);
            var gift = CreateGiftItemStandIn();

            var added = BetaGiftGrant.TryAddGiftToPack(pack, gift);

            Assert.IsFalse(added, "a full pack must refuse the add rather than throw - CharacterHandler logs a Warn on false and character creation still succeeds");
        }

        [TestMethod]
        public void TryAddGiftToPack_NullItem_ReturnsFalse()
        {
            var pack = CreatePack();

            Assert.IsFalse(BetaGiftGrant.TryAddGiftToPack(pack, null));
        }

        [TestMethod]
        public void TryAddGiftToPack_NullPack_ReturnsFalse()
        {
            var gift = CreateGiftItemStandIn();

            Assert.IsFalse(BetaGiftGrant.TryAddGiftToPack(null, gift));
        }

        // ---- config-metadata.tsv / config-defaults.tsv pin (the coverage tests already assert the keys
        // exist and are sorted; this pins the two rows' actual field values, since a hand-edit could set
        // a wrong tab/group/default without either coverage test noticing) ----

        [TestMethod]
        public void ConfigMetadataAndDefaults_BetaGiftKeys_HaveExpectedValues()
        {
            var metadata = ConfigMetadata.LoadEmbedded();

            Assert.IsTrue(metadata.TryGet("beta_gift_wcid", out var wcidRow));
            Assert.AreEqual("server", wcidRow.Tab);
            Assert.AreEqual("fork", wcidRow.Origin);
            Assert.IsFalse(wcidRow.Sensitive);

            Assert.IsTrue(metadata.TryGet("beta_gift_account_cutoff_unix", out var cutoffRow));
            Assert.AreEqual("server", cutoffRow.Tab);
            Assert.AreEqual("fork", cutoffRow.Origin);
            Assert.IsFalse(cutoffRow.Sensitive);

            Assert.AreEqual(1006600L, DefaultPropertyManager.DefaultLongProperties["beta_gift_wcid"].Item);
            Assert.AreEqual(BetaGiftRules.DefaultAccountCutoffUnix, DefaultPropertyManager.DefaultLongProperties["beta_gift_account_cutoff_unix"].Item);
        }

        // ---- the weenie SQL itself: GearMaxHealth=3, Attuned, Bonded, the retail compass icon ----

        [TestMethod]
        public void DreamwalkersCompassSql_CarriesGearMaxHealthAttunedBondedAndIcon()
        {
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1006600 Dreamwalker's Compass.sql");

            var intBlockMatch = Regex.Match(sql, @"INSERT INTO `weenie_properties_int`[^;]*;", RegexOptions.Singleline);
            Assert.IsTrue(intBlockMatch.Success, "no weenie_properties_int INSERT block found");
            var intBlock = intBlockMatch.Value;

            StringAssert.Matches(intBlock, new Regex(@"\(1006600,\s*379,\s*3\)"), "GearMaxHealth (379) must be 3");
            StringAssert.Matches(intBlock, new Regex(@"\(1006600,\s*114,\s*1\)"), "Attuned (114) must be AttunedStatus.Attuned (1)");
            StringAssert.Matches(intBlock, new Regex(@"\(1006600,\s*33,\s*1\)"), "Bonded (33) must be BondedStatus.Bonded (1)");

            var didBlockMatch = Regex.Match(sql, @"INSERT INTO `weenie_properties_d_i_d`[^;]*;", RegexOptions.Singleline);
            Assert.IsTrue(didBlockMatch.Success, "no weenie_properties_d_i_d INSERT block found");
            StringAssert.Matches(didBlockMatch.Value, new Regex(@"\(1006600,\s*8,\s*0x06006AA4\)"), "Icon (8) must be the retail compass icon 0x06006AA4");

            Assert.IsTrue(sql.Contains("-- @owner: beta-gift"), "unit must carry its @owner header");
            Assert.IsTrue(sql.Contains("-- @owns: wcid:1006600"), "unit must carry its @owns header");
        }
    }
}
