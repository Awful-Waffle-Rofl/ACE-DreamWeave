using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// Shared regex text search over an item's name, spell names and equipment set, used by /mule
    /// search (PersonalVendor.RebuildView) and any other feature that wants to filter items by
    /// free-text pattern. Spell-name resolution is NOT duplicated here - <see cref="MarketSnapshot.SpellName"/>
    /// (made internal for exactly this) is the one place that turns a spell id into a display name,
    /// and this class calls it rather than reimplementing it.
    ///
    /// The equipment-set matching rule is IDENTICAL to the one the separate web market app implements
    /// against the same snapshot fields, deliberately: see the 4-arg SearchText overload's remarks.
    ///
    /// A pattern cannot contain a literal double quote: CommandManager.ParseCommand strips every `"`
    /// from a command line that has one (pre-existing, not specific to this class) before /mule search's
    /// handler ever sees the pattern.
    /// </summary>
    public static class ItemTextSearch
    {
        /// <summary>Matches the web market app's free-text cap (ListingQuery.MaxTextLength).</summary>
        public const int MaxPatternLength = 64;

        /// <summary>
        /// Compiles a player-supplied pattern into a case-insensitive, culture-invariant Regex with a
        /// short match timeout. Never throws - an invalid or oversized pattern returns false with a
        /// player-readable error instead.
        /// </summary>
        public static bool TryCompile(string text, out Regex regex, out string error)
        {
            regex = null;
            error = null;

            var trimmed = text?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                error = "Search pattern cannot be empty.";
                return false;
            }

            if (trimmed.Length > MaxPatternLength)
            {
                error = $"Search pattern is too long (max {MaxPatternLength} characters).";
                return false;
            }

            try
            {
                regex = new Regex(trimmed, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                return true;
            }
            catch (ArgumentException ex)
            {
                regex = null;
                error = $"Invalid search pattern: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// The name followed by every non-null spell name, all joined with "; " on ONE line - no
        /// newlines, so a pattern like "legendary.*legendary" can match across two spell names on an
        /// item that carries both. Delegates to the 4-arg overload with no set label/token, so an item
        /// with no set produces exactly this string either way.
        /// </summary>
        public static string SearchText(string name, IEnumerable<string> spellNames)
            => SearchText(name, spellNames, null, null);

        /// <summary>
        /// The name, then every non-null spell name, then the set label, then the set token (unless it
        /// is ordinal-equal to the label), all joined with "; " on ONE line - no newlines, so a pattern
        /// like "legendary.*legendary" can match across two spell names on an item that carries both.
        ///
        /// This exact rule - name, spells, label, token-if-different - is a DELIBERATE DUPLICATE of the
        /// rule the separate web market app implements against the same snapshot fields
        /// (equipment_set_name, equipment_set). Both must apply it identically, since they are two
        /// independent readers of the same search text contract: an item with no set produces exactly
        /// today's (pre-set-search) string, which is why setLabel/setToken are optional and null-safe.
        /// </summary>
        public static string SearchText(string name, IEnumerable<string> spellNames, string setLabel, string setToken)
        {
            var parts = new List<string> { name ?? string.Empty };

            if (spellNames != null)
            {
                foreach (var spellName in spellNames)
                {
                    if (spellName != null)
                        parts.Add(spellName);
                }
            }

            if (!string.IsNullOrEmpty(setLabel))
                parts.Add(setLabel);

            if (!string.IsNullOrEmpty(setToken) && !string.Equals(setToken, setLabel, StringComparison.Ordinal))
                parts.Add(setToken);

            return string.Join("; ", parts);
        }

        /// <summary>
        /// Builds the search text for a live item, using the same spell-name resolution
        /// <see cref="MarketSnapshot"/> uses (MarketSnapshot.FromItem). Never throws on a null/empty
        /// spellbook.
        /// </summary>
        public static string SearchText(WorldObject item)
        {
            if (item == null)
                return string.Empty;

            List<int> spellIds = null;

            try
            {
                if (item.Biota != null)
                    spellIds = item.Biota.GetKnownSpellsIds(item.BiotaDatabaseLock);
            }
            catch (Exception)
            {
                spellIds = null;
            }

            var spellNames = spellIds?.Select(MarketSnapshot.SpellName);

            var equipmentSetId = item.GetProperty(PropertyInt.EquipmentSetId);
            var setLabel = MarketEquipmentSet.Label(equipmentSetId);
            var setToken = MarketEquipmentSet.Classify(equipmentSetId);

            return SearchText(item.Name, spellNames, setLabel, setToken);
        }

        /// <summary>
        /// Builds the search text for a weenie (a collapsed ledger row with no live WorldObject), using
        /// the same spell-name resolution <see cref="MarketSnapshot"/> uses (MarketSnapshot.FromWeenie).
        /// Never throws on a null/empty spellbook.
        /// </summary>
        public static string SearchText(Weenie weenie)
        {
            if (weenie == null)
                return string.Empty;

            var spellNames = weenie.PropertiesSpellBook?.Keys.Select(MarketSnapshot.SpellName);

            var equipmentSetId = weenie.GetProperty(PropertyInt.EquipmentSetId);
            var setLabel = MarketEquipmentSet.Label(equipmentSetId);
            var setToken = MarketEquipmentSet.Classify(equipmentSetId);

            return SearchText(weenie.GetName(), spellNames, setLabel, setToken);
        }

        /// <summary>True on a match, false on a timeout - never throws.</summary>
        public static bool IsMatch(Regex regex, string searchText)
        {
            try
            {
                return regex.IsMatch(searchText ?? string.Empty);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }
}
