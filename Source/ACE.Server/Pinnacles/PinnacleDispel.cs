using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Models;
using ACE.Server.ClassAbilities.Abilities;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// The memory-fail penalty's choice: up to N random beneficial enchantments. The filter is NOT re-derived
    /// here: it is DispellingEdgeAbility.SelectDispellable, the shipped beneficial-dispel rule (item-sourced and
    /// vitae entries, Duration -1, and item/cooldown pseudo-spells, SpellId above short.MaxValue, are never taken).
    /// The live adapter strips every caster's layer of each chosen entry (DispellingEdgeAbility.SelectAllLayers)
    /// through EnchantmentManager.Dispel.
    /// </summary>
    public static class PinnacleDispel
    {
        /// <summary>
        /// <paramref name="topLayerBeneficial"/> is EnchantmentManager.GetEnchantments_TopLayer(Beneficial). Filters,
        /// shuffles with a seeded Fisher-Yates over <paramref name="rng"/> (input order is the only other input, so
        /// the same seed and list give the same choice), and takes at most <paramref name="n"/>.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> Select(IReadOnlyList<PropertiesEnchantmentRegistry> topLayerBeneficial, int n, Random rng)
        {
            if (topLayerBeneficial == null || n <= 0 || rng == null)
                return new List<PropertiesEnchantmentRegistry>();

            var pool = DispellingEdgeAbility.SelectDispellable(topLayerBeneficial.ToList());

            for (var i = pool.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            if (pool.Count > n)
                pool.RemoveRange(n, pool.Count - n);

            return pool;
        }

        /// <summary>The line shown when a failed round had nothing to dispel.</summary>
        public const string NothingDispelledLine = "None of your protections could be unravelled.";

        /// <summary>One chat line holds at most 255 characters on the client; 250 leaves headroom.</summary>
        public const int MaxLineLength = 250;

        /// <summary>
        /// The chat lines naming the dispelled buffs: "Dispelled: A, B, and C." Names are never split; a list too long
        /// for one line wraps onto further lines (each at most <paramref name="max"/> characters unless one name alone
        /// is longer), the first opening with "Dispelled: ", every name exactly once in order. No names: one line,
        /// <see cref="NothingDispelledLine"/>.
        /// </summary>
        public static List<string> FormatLines(IReadOnlyList<string> names, int max = MaxLineLength)
        {
            var clean = (names ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

            if (clean.Count == 0)
                return new List<string> { NothingDispelledLine };

            var lines = new List<string>();
            var current = new List<string>();

            for (var i = 0; i < clean.Count; i++)
            {
                var candidate = new List<string>(current) { clean[i] };

                if (current.Count > 0 && Render(candidate, lines.Count == 0, i == clean.Count - 1, clean.Count).Length > max)
                {
                    lines.Add(Render(current, lines.Count == 0, false, clean.Count));
                    current = new List<string> { clean[i] };
                }
                else
                {
                    current = candidate;
                }
            }

            lines.Add(Render(current, lines.Count == 0, true, clean.Count));

            return lines;
        }

        private static string Render(List<string> group, bool first, bool last, int total)
        {
            string body;

            if (!last || group.Count == 1 && total == 1)
                body = string.Join(", ", group);
            else if (group.Count == 1)
                body = "and " + group[0];
            else if (total == 2)
                body = group[0] + " and " + group[1];
            else
                body = string.Join(", ", group.Take(group.Count - 1)) + ", and " + group[group.Count - 1];

            return (first ? "Dispelled: " : "") + body + (last ? "." : ",");
        }
    }
}
