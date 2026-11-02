using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Models;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ItemTextSearch: the shared regex text-search matcher behind /mule search (PersonalVendor's
    /// display filter). Exercised mostly through the pure <see cref="ItemTextSearch.SearchText(string, System.Collections.Generic.IEnumerable{string})"/>
    /// overload rather than a live WorldObject/Weenie's real spell names - this test project has no
    /// guaranteed client dat access (see PersonalVendorTests.RequireDats), and the pure overload is
    /// exactly what MarketSnapshot.SpellName's resolved output feeds into, so testing composition
    /// against literal spell-name strings covers the same join/match logic without depending on dats
    /// being present.
    /// </summary>
    [TestClass]
    public class ItemTextSearchTests
    {
        [TestMethod]
        public void TryCompile_AlternationMatchesEitherSide()
        {
            Assert.IsTrue(ItemTextSearch.TryCompile("cat|dog", out var regex, out var error));
            Assert.IsNull(error);

            Assert.IsTrue(ItemTextSearch.IsMatch(regex, "a cat sat"));
            Assert.IsTrue(ItemTextSearch.IsMatch(regex, "a dog sat"));
            Assert.IsFalse(ItemTextSearch.IsMatch(regex, "a bird sat"));
        }

        /// <summary>
        /// The reason SearchText joins name + every spell name onto ONE line rather than one per line:
        /// "." must be able to span from one spell name to the next so a pattern like
        /// "legendary.*legendary" can match an item carrying two Legendary-named spells.
        /// </summary>
        [TestMethod]
        public void LegendaryDotStarLegendary_MatchesTwoLegendarySpells_ButNotOne()
        {
            Assert.IsTrue(ItemTextSearch.TryCompile("legendary.*legendary", out var regex, out var error));
            Assert.IsNull(error);

            var twoLegendaries = ItemTextSearch.SearchText("Test Ring", new[] { "Legendary Strength", "Legendary Focus" });
            var oneLegendary = ItemTextSearch.SearchText("Test Ring", new[] { "Legendary Strength", "Minor Focus" });

            Assert.IsTrue(ItemTextSearch.IsMatch(regex, twoLegendaries), $"expected a match against '{twoLegendaries}'");
            Assert.IsFalse(ItemTextSearch.IsMatch(regex, oneLegendary), $"expected no match against '{oneLegendary}' - only one Legendary spell is present");
        }

        [TestMethod]
        public void TryCompile_IsCaseInsensitive()
        {
            Assert.IsTrue(ItemTextSearch.TryCompile("LEGENDARY", out var regex, out _));

            Assert.IsTrue(ItemTextSearch.IsMatch(regex, ItemTextSearch.SearchText("Item", new[] { "legendary strength" })));
        }

        [TestMethod]
        public void TryCompile_EmptyPattern_IsRefusedWithAnError()
        {
            Assert.IsFalse(ItemTextSearch.TryCompile("", out var regex, out var error));
            Assert.IsNull(regex);
            Assert.IsFalse(string.IsNullOrEmpty(error));

            Assert.IsFalse(ItemTextSearch.TryCompile("   ", out var regexWhitespace, out var errorWhitespace));
            Assert.IsNull(regexWhitespace);
            Assert.IsFalse(string.IsNullOrEmpty(errorWhitespace));
        }

        /// <summary>An unbalanced group is invalid regex syntax and must be refused, never throw.</summary>
        [TestMethod]
        public void TryCompile_InvalidPattern_IsRefusedWithAnErrorAndNeverThrows()
        {
            var ok = ItemTextSearch.TryCompile("(", out var regex, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(regex);
            Assert.IsNotNull(error);
            StringAssert.Contains(error, "Invalid search pattern");
        }

        [TestMethod]
        public void TryCompile_OverCapLength_IsRefusedWithAnErrorNamingTheCap()
        {
            var overCap = new string('a', ItemTextSearch.MaxPatternLength + 1);

            var ok = ItemTextSearch.TryCompile(overCap, out var regex, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(regex);
            StringAssert.Contains(error, ItemTextSearch.MaxPatternLength.ToString());
        }

        [TestMethod]
        public void TryCompile_AtExactlyTheCap_Succeeds()
        {
            var atCap = new string('a', ItemTextSearch.MaxPatternLength);

            Assert.IsTrue(ItemTextSearch.TryCompile(atCap, out var regex, out var error));
            Assert.IsNotNull(regex);
            Assert.IsNull(error);
        }

        /// <summary>
        /// The join is "; " on ONE line with no embedded newlines - the name first, then every
        /// non-null spell name in order.
        /// </summary>
        [TestMethod]
        public void SearchText_JoinsNameAndSpells_OnOneLineWithSemicolons()
        {
            var text = ItemTextSearch.SearchText("Ring of Testing", new[] { "Spell A", "Spell B" });

            Assert.AreEqual("Ring of Testing; Spell A; Spell B", text);
            Assert.IsFalse(text.Contains("\n"), "the joined search text must be a single line");
            Assert.IsFalse(text.Contains("\r"), "the joined search text must be a single line");
        }

        [TestMethod]
        public void SearchText_WithNoSpells_IsJustTheName()
        {
            Assert.AreEqual("Plain Item", ItemTextSearch.SearchText("Plain Item", null));
            Assert.AreEqual("Plain Item", ItemTextSearch.SearchText("Plain Item", System.Array.Empty<string>()));
        }

        [TestMethod]
        public void SearchText_SkipsNullSpellNamesWithoutThrowing()
        {
            var text = ItemTextSearch.SearchText("Item", new[] { "Spell A", null, "Spell B" });

            Assert.AreEqual("Item; Spell A; Spell B", text);
        }

        [TestMethod]
        public void SearchText_NullName_DoesNotThrow()
        {
            var text = ItemTextSearch.SearchText(null, new[] { "Spell A" });

            Assert.AreEqual("; Spell A", text);
        }

        /// <summary>IsMatch must never throw or propagate a timeout - a timeout is simply "no match".</summary>
        [TestMethod]
        public void IsMatch_OnATimeout_ReturnsFalseRatherThanThrowing()
        {
            // A classic catastrophic-backtracking shape against a string with no terminator, well
            // within the 100ms timeout budget's ability to actually trip for a long enough input.
            Assert.IsTrue(ItemTextSearch.TryCompile("(a+)+$", out var regex, out _));

            var pathological = new string('a', 40) + "!";

            // Whether this actually times out or simply fails to match (the trailing "!" can never
            // satisfy "$" after only "a" repetitions) is immaterial - either way IsMatch must return
            // false without letting a RegexMatchTimeoutException escape.
            var result = ItemTextSearch.IsMatch(regex, pathological);
            Assert.IsFalse(result);
        }

        /// <summary>
        /// SearchText(WorldObject) with a null item and SearchText(Weenie) with a null weenie must both
        /// return an empty string rather than throwing - RebuildView's filter treats "could not resolve"
        /// as "no match", never as a crash.
        /// </summary>
        [TestMethod]
        public void SearchText_NullWorldObjectAndNullWeenie_ReturnEmptyString()
        {
            Assert.AreEqual(string.Empty, ItemTextSearch.SearchText((ACE.Server.WorldObjects.WorldObject)null));
            Assert.AreEqual(string.Empty, ItemTextSearch.SearchText((Weenie)null));
        }

        /// <summary>
        /// A weenie with no spellbook at all (PropertiesSpellBook null) must not throw - the search
        /// text is simply the name.
        /// </summary>
        [TestMethod]
        public void SearchText_WeenieWithNoSpellbook_IsJustTheName()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1,
                PropertiesString = new System.Collections.Generic.Dictionary<ACE.Entity.Enum.Properties.PropertyString, string>
                {
                    { ACE.Entity.Enum.Properties.PropertyString.Name, "No Spells Here" },
                },
            };

            Assert.AreEqual("No Spells Here", ItemTextSearch.SearchText(weenie));
        }

        /// <summary>
        /// A weenie WITH a spellbook must not throw even when spell-name resolution cannot find real
        /// spell data (MarketSnapshot.SpellName's own try/catch falls back to "Spell {id}") - only the
        /// shape (name, then "; "-joined entries, one per spellbook key) is asserted here, not the
        /// specific resolved text, since that depends on client dat availability this test project does
        /// not guarantee (see PersonalVendorTests.RequireDats).
        /// </summary>
        // ---- 4-arg SearchText(name, spellNames, setLabel, setToken) ----

        /// <summary>An item with no set (both setLabel and setToken null) must be byte-identical to the 2-arg overload's output.</summary>
        [TestMethod]
        public void SearchText_WithNoSet_IsByteIdenticalToTheTwoArgOverload()
        {
            var twoArg = ItemTextSearch.SearchText("Ring of Testing", new[] { "Spell A", "Spell B" });
            var fourArgNoSet = ItemTextSearch.SearchText("Ring of Testing", new[] { "Spell A", "Spell B" }, null, null);

            Assert.AreEqual(twoArg, fourArgNoSet);
            Assert.AreEqual("Ring of Testing; Spell A; Spell B", fourArgNoSet);
        }

        /// <summary>The set label is appended after the spells, then the token, when the two differ.</summary>
        [TestMethod]
        public void SearchText_WithSet_AppendsLabelThenToken()
        {
            var text = ItemTextSearch.SearchText("Ring of Testing", new[] { "Spell A" }, "Defender's", "Defenders");

            Assert.AreEqual("Ring of Testing; Spell A; Defender's; Defenders", text);
        }

        /// <summary>When the token is ordinal-equal to the label, it must not be duplicated.</summary>
        [TestMethod]
        public void SearchText_TokenEqualToLabel_IsNotDuplicated()
        {
            var text = ItemTextSearch.SearchText("Ring of Testing", null, "Noble Relic", "Noble Relic");

            Assert.AreEqual("Ring of Testing; Noble Relic", text);
        }

        /// <summary>
        /// A regex against a Defenders-set item's search text must match "defender's" case-insensitively,
        /// via the same TryCompile path /mule search uses.
        /// </summary>
        [TestMethod]
        public void DefendersSet_SearchText_MatchesDefendersApostropheSCaseInsensitively()
        {
            var setLabel = MarketEquipmentSet.Label((int)ACE.Entity.Enum.EquipmentSet.Defenders);
            var setToken = MarketEquipmentSet.Classify((int)ACE.Entity.Enum.EquipmentSet.Defenders);
            var text = ItemTextSearch.SearchText("Ring of Testing", null, setLabel, setToken);

            Assert.IsTrue(ItemTextSearch.TryCompile("defender's", out var regex, out var error));
            Assert.IsNull(error);
            Assert.IsTrue(ItemTextSearch.IsMatch(regex, text), $"expected a match against '{text}'");
        }

        // ---- 5-arg SearchText(name, spellNames, setLabel, setToken, ratingLines) ----

        private static Weenie RatedWeenie(string name, params (ACE.Entity.Enum.Properties.PropertyInt Key, int Value)[] ratings)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 3,
                PropertiesString = new System.Collections.Generic.Dictionary<ACE.Entity.Enum.Properties.PropertyString, string>
                {
                    { ACE.Entity.Enum.Properties.PropertyString.Name, name },
                },
                PropertiesInt = new System.Collections.Generic.Dictionary<ACE.Entity.Enum.Properties.PropertyInt, int>(),
            };

            foreach (var (key, value) in ratings)
                weenie.PropertiesInt[key] = value;

            return weenie;
        }

        /// <summary>No ratings must produce EXACTLY the pre-ratings string, byte for byte.</summary>
        [TestMethod]
        public void SearchText_NoRatings_IsByteIdenticalToTheFourArgOverload()
        {
            var spells = new[] { "Spell A", "Spell B" };

            Assert.AreEqual(
                ItemTextSearch.SearchText("Ring of Testing", spells, "Defender's", "Defenders"),
                ItemTextSearch.SearchText("Ring of Testing", spells, "Defender's", "Defenders", null));
            Assert.AreEqual(
                "Ring of Testing; Spell A; Spell B; Defender's; Defenders",
                ItemTextSearch.SearchText("Ring of Testing", spells, "Defender's", "Defenders", System.Array.Empty<string>()));
            Assert.AreEqual(
                "Ring of Testing; Spell A; Spell B",
                ItemTextSearch.SearchText("Ring of Testing", spells, null, null, new string[] { null, "" }));
        }

        /// <summary>Rating segments come after the set parts, in the order given, joined with "; ".</summary>
        [TestMethod]
        public void SearchText_RatingLines_AreAppendedAfterTheSetParts_InOrder()
        {
            var text = ItemTextSearch.SearchText(
                "Ring of Testing", new[] { "Spell A" }, "Defender's", "Defenders",
                new[] { "Damage Rating: +1", "Critical Damage Rating: +2" });

            Assert.AreEqual("Ring of Testing; Spell A; Defender's; Defenders; Damage Rating: +1; Critical Damage Rating: +2", text);
        }

        /// <summary>The reported bug: "legendary.*\+1" must find a Legendary-spell item with a +1 rating.</summary>
        [TestMethod]
        public void LegendaryThenPlusOne_MatchesALegendaryItemWithAPlusOneRating_ButNotOneWithout()
        {
            Assert.IsTrue(ItemTextSearch.TryCompile(@"legendary.*\+1", out var regex, out var error));
            Assert.IsNull(error);

            var rated = ItemTextSearch.SearchText("Ring of Testing", new[] { "Legendary Strength" }, null, null, new[] { "Critical Damage Rating: +1" });
            var unrated = ItemTextSearch.SearchText("Ring of Testing", new[] { "Legendary Strength" }, null, null, null);

            Assert.IsTrue(ItemTextSearch.IsMatch(regex, rated), $"expected a match against '{rated}'");
            Assert.IsFalse(ItemTextSearch.IsMatch(regex, unrated));
        }

        /// <summary>
        /// Ratings are read from the item's gear-rating properties in the fixed AddRatings order, zero
        /// is omitted, a negative keeps its sign, and a weenie with none yields exactly the plain string.
        /// </summary>
        [TestMethod]
        public void SearchText_Weenie_AppendsNonZeroRatingsInFixedOrder_OmittingZeroes()
        {
            // Inserted out of order on purpose: output order is the table's, not insertion order.
            var weenie = RatedWeenie("Rated Ring",
                (ACE.Entity.Enum.Properties.PropertyInt.GearHealingBoost, 3),
                (ACE.Entity.Enum.Properties.PropertyInt.GearCritDamage, 2),
                (ACE.Entity.Enum.Properties.PropertyInt.GearDamageResist, 0),
                (ACE.Entity.Enum.Properties.PropertyInt.GearDamage, -1));

            Assert.AreEqual(
                "Rated Ring; Damage Rating: -1; Critical Damage Rating: +2; Healing Boost Rating: +3",
                ItemTextSearch.SearchText(weenie));
        }

        /// <summary>
        /// Pins the whole 14-label table and its order as one literal. The web market repo pins the SAME
        /// 14 literals (TextPatternSearchTests.ItemRatings_Labels_AreTheFourteenInOrder), so a label
        /// edited on one side only fails a test on that side. Mixed signs, inserted out of order.
        /// </summary>
        [TestMethod]
        public void SearchText_Weenie_AllFourteenRatings_PinsTheFullTableAndOrder()
        {
            var weenie = RatedWeenie("Full Ring",
                (ACE.Entity.Enum.Properties.PropertyInt.GearOverpowerResist, -14),
                (ACE.Entity.Enum.Properties.PropertyInt.GearOverpower, 13),
                (ACE.Entity.Enum.Properties.PropertyInt.GearPKDamageResistRating, -12),
                (ACE.Entity.Enum.Properties.PropertyInt.GearPKDamageRating, 11),
                (ACE.Entity.Enum.Properties.PropertyInt.GearMaxHealth, -10),
                (ACE.Entity.Enum.Properties.PropertyInt.GearLifeResist, 9),
                (ACE.Entity.Enum.Properties.PropertyInt.GearNetherResist, -8),
                (ACE.Entity.Enum.Properties.PropertyInt.GearHealingBoost, 7),
                (ACE.Entity.Enum.Properties.PropertyInt.GearCritDamageResist, -6),
                (ACE.Entity.Enum.Properties.PropertyInt.GearCritDamage, 5),
                (ACE.Entity.Enum.Properties.PropertyInt.GearCritResist, -4),
                (ACE.Entity.Enum.Properties.PropertyInt.GearCrit, 3),
                (ACE.Entity.Enum.Properties.PropertyInt.GearDamageResist, -2),
                (ACE.Entity.Enum.Properties.PropertyInt.GearDamage, 1));

            Assert.AreEqual(
                "Full Ring; Damage Rating: +1; Damage Reduction Rating: -2; Critical Rating: +3; "
                + "Critical Resistance Rating: -4; Critical Damage Rating: +5; Critical Damage Reduction Rating: -6; "
                + "Healing Boost Rating: +7; Nether Resistance Rating: -8; Life Resistance Rating: +9; "
                + "Maximum Health Bonus: -10; PK Damage Rating: +11; PK Damage Reduction Rating: -12; "
                + "Overpower Rating: +13; Overpower Resistance Rating: -14",
                ItemTextSearch.SearchText(weenie));
        }

        /// <summary>The live-item overload reads the same ratings (FakeVaultWorld builds a WorldObject without dats).</summary>
        [TestMethod]
        public void SearchText_WorldObject_AppendsItsRatings()
        {
            var item = FakeVaultWorld.MakeStack(92002, 1, 1);   // named "Test Item 92002"

            Assert.AreEqual("Test Item 92002", ItemTextSearch.SearchText(item));

            item.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.GearCritDamage, 2);
            item.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.GearDamage, -1);

            Assert.AreEqual("Test Item 92002; Damage Rating: -1; Critical Damage Rating: +2", ItemTextSearch.SearchText(item));
        }

        [TestMethod]
        public void SearchText_Weenie_WithOnlyZeroRatings_IsJustTheName()
        {
            var weenie = RatedWeenie("Plain Ring", (ACE.Entity.Enum.Properties.PropertyInt.GearCrit, 0));

            Assert.AreEqual("Plain Ring", ItemTextSearch.SearchText(weenie));
        }

        [TestMethod]
        public void SearchText_WeenieWithASpellbook_IncludesOneEntryPerSpell_AndNeverThrows()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 2,
                PropertiesString = new System.Collections.Generic.Dictionary<ACE.Entity.Enum.Properties.PropertyString, string>
                {
                    { ACE.Entity.Enum.Properties.PropertyString.Name, "Spellbound Item" },
                },
                PropertiesSpellBook = new System.Collections.Generic.Dictionary<int, float> { { 1, 1.0f }, { 2, 1.0f } },
            };

            var text = ItemTextSearch.SearchText(weenie);

            var segments = text.Split(new[] { "; " }, System.StringSplitOptions.None);

            Assert.AreEqual("Spellbound Item", segments[0]);
            Assert.AreEqual(3, segments.Length, $"expected the name plus 2 spell entries; got '{text}'");
            Assert.IsFalse(text.Contains("\n"));
        }
    }
}
