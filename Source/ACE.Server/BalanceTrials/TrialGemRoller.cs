using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.BalanceTrials
{
    /// <summary>One rep's gem: the spec the plan is built from, the level the press produced, the seed used and the mods as JSON.</summary>
    public sealed record TrialGemRoll(DungeonGemSpec Spec, int PressedLevel, int RepSeed, string ModsJson);

    /// <summary>
    /// Rolls a trial scenario's gem through the live fragment press rules. A rolled gem is whatever
    /// RawFragmentRules.Resolve produces for the scenario band's rung fragment, seeded per rep; it is never a copy of that
    /// code. The run itself is at exactly the scenario's level (owner ruling): the pressed modifiers are kept, the pressed
    /// level is only recorded.
    /// </summary>
    public static class TrialGemRoller
    {
        /// <summary>The seed rep n (1-based) rolls and runs with. Fixed repeats the scenario seed; Step walks it, wrapped into 1..int.MaxValue-1.</summary>
        public static int RepSeed(TrialGem gem, int rep)
        {
            if (gem.SeedMode == TrialSeedMode.Fixed)
                return gem.Seed;

            const long span = int.MaxValue - 1L;
            var offset = (long)gem.Seed - 1 + (rep - 1);
            var wrapped = ((offset % span) + span) % span;

            return (int)(wrapped + 1);
        }

        /// <summary>The pure roll. RollLive supplies the live fragment, attunement, modifiers and limits.</summary>
        public static TrialGemRoll Roll(TrialGem gem, int rep, DungeonGemSpec fragmentSpec, int fragmentEntries, AttunementDef attunement,
            IReadOnlyDictionary<string, ModifierDef> modifiers, PressLimits limits)
        {
            var seed = RepSeed(gem, rep);

            IReadOnlyList<(string Id, double Magnitude)> mods;
            int pressedLevel;
            string mode;

            if (gem.PinnedMods == null)
            {
                var pressed = RawFragmentRules.Resolve(new PressState(fragmentSpec, fragmentEntries), attunement, modifiers, limits, new Random(seed), out _);
                mods = pressed.Spec.Modifiers.ToList();
                pressedLevel = pressed.Spec.Level;
                mode = "rolled";
            }
            else
            {
                foreach (var m in gem.PinnedMods)
                {
                    if (!modifiers.ContainsKey(m.Id))
                        throw new TrialArchetypeException($"pinned modifier '{m.Id}' is not a known modifier id");
                }

                mods = gem.PinnedMods.ToList();
                pressedLevel = gem.Level;
                mode = "pinned";
            }

            var spec = new DungeonGemSpec(gem.Dungeon, gem.Level, gem.Tier, gem.Family, seed, mods, 0, 0);

            return new TrialGemRoll(spec, pressedLevel, seed, ComposeModsJson(mode, pressedLevel, mods));
        }

        /// <summary>The live roll: the rung weenie for the scenario's band, the live store and the live press limits.</summary>
        public static TrialGemRoll RollLive(TrialGem gem, int rep)
        {
            var rungWcid = DungeonGemFactory.RungWcid(gem.Level);
            var weenie = DatabaseManager.World.GetCachedWeenie(rungWcid)
                ?? throw new TrialArchetypeException($"rung weenie {rungWcid} for level {gem.Level} is not in the world database");

            var text = weenie.GetProperty(PropertyString.DungeonGemSpec);

            if (!DungeonGemSpec.TryParse(text, out var fragmentSpec, out var error))
                throw new TrialArchetypeException($"rung weenie {rungWcid} has an unparseable spec '{text}': {error}");

            var store = ThreadDungeonManager.Store;

            return Roll(gem, rep, fragmentSpec, weenie.GetProperty(PropertyInt.Structure) ?? 0, store.Attunement, store.Modifiers, FragmentPressStation.BuildLimits());
        }

        private static string ComposeModsJson(string mode, int pressedLevel, IReadOnlyList<(string Id, double Magnitude)> mods)
            => JsonSerializer.Serialize(new
            {
                mode,
                pressedLevel,
                mods = mods.Select(m => new { id = m.Id, magnitude = m.Magnitude }).ToList(),
            });
    }
}
