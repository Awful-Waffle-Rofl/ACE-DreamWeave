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
