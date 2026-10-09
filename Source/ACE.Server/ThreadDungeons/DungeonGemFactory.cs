using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The one place a Thread Gem or a Raw Fragment is built from a spec (PHASE-2-IMPLEMENTATION-PLAN.md
    /// task D4b). Extracted out of ThreadDungeonCommands.HandleGive so the Fragment Press and /dd give
    /// cannot drift on how a finished gem is shaped - spec string, Structure/MaxStructure and LongDesc are
    /// written here, once, for every producer.
    ///
    /// The two item-level invariants the codec deliberately does NOT enforce (DungeonGemSpec's class
    /// remarks) are enforced here instead: a Thread Gem always has an empty Load and a non-zero Seed, and
    /// a Raw Fragment always has Seed 0.
    /// </summary>
    public static class DungeonGemFactory
    {
        public const uint DungeonGemWcid = 1003600;
        public const uint FragmentPressWcid = 1003614;

        /// <summary>The level-185 rung. Every Raw Fragment is built on this weenie and renamed from its spec.</summary>
        public const uint RawFragmentBaseWcid = 1003615;

        /// <summary>
        /// THE rung ladder, ascending by level: each shipped Raw Fragment weenie and the level it is named
        /// for. This is the single source of the rung list.
        ///
        /// Written out as a table rather than derived from a base and a step, because the ladder's step is
        /// IRREGULAR: 185 to 275 in tens, then 300, 325, 350 and 375 in twenty-fives (the ceiling raise, owner
        /// ruling 2026-09-09). The old "RawFragmentBaseWcid + clamp((level - 185) / 10, 0, 9)" arithmetic
        /// described the first ten rungs exactly, and its clamp meant it did not go out of range once the
        /// ceiling moved - it did something quieter and worse. Level 300 computes rung index 11, clamps back
        /// to 9, and comes out 1003624: every one of the four new rungs would answer with the level-275
        /// weenie, and 1003626-1003629 would be unreachable from code while sitting in the world database.
        /// </summary>
        public static readonly IReadOnlyList<(uint Wcid, int Level)> Rungs = new[]
        {
            (1003615u, 185), (1003616u, 195), (1003617u, 205), (1003618u, 215), (1003619u, 225),
            (1003620u, 235), (1003621u, 245), (1003622u, 255), (1003623u, 265), (1003624u, 275),
            (1003626u, 300), (1003627u, 325), (1003628u, 350), (1003629u, 375),
        };

        /// <summary>The lowest rung's level, 185. Below it <see cref="RungWcid"/> clamps to the first rung.</summary>
        public static int RungBaseLevel => Rungs[0].Level;

        /// <summary>The highest rung's level, 375. Above it <see cref="RungWcid"/> clamps to the last rung.</summary>
        public static int RungTopLevel => Rungs[Rungs.Count - 1].Level;

        public static int RungCount => Rungs.Count;

        public const string GemHasLoadError = "A Thread Gem cannot be made from a spec that still carries a load.";
        public const string GemHasNoSeedError = "A Thread Gem cannot be made from an unpressed spec (seed 0).";
        public const string FragmentHasSeedError = "A Raw Fragment cannot be made from a pressed spec (seed is not 0).";
        public const string NoSpecError = "No spec was supplied.";

        /// <summary>PaletteTemplate for a tier-8 (or higher, should one ever ship) gem: Blue.</summary>
        public const int TierBluePaletteTemplate = 2;

        /// <summary>PaletteTemplate for every gem below tier 8: Green. Also the fallback for a tier
        /// this factory does not recognize, so an out-of-range tier never errors, it just reads Green.</summary>
        public const int TierGreenPaletteTemplate = 8;

        /// <summary>Loot tier at or above which a gem is stamped Blue instead of Green.</summary>
        public const int BlueTierThreshold = 8;

        /// <summary>
        /// The rung weenie whose shipped level matches <paramref name="level"/>, clamped to the fourteen rungs
        /// that exist (<see cref="Rungs"/>, levels 185..375). A level between rungs rounds DOWN, which is what
        /// makes 190 a level-185 rung rather than an error; a level below the first rung reads as the first
        /// rung and a level above the last reads as the last. Identical semantics to the arithmetic form this
        /// replaced - only the ladder it walks changed.
        /// </summary>
        public static uint RungWcid(int level)
        {
            var wcid = Rungs[0].Wcid;

            for (var i = 0; i < Rungs.Count; i++)
            {
                if (level < Rungs[i].Level)
                    break;

                wcid = Rungs[i].Wcid;
            }

            return wcid;
        }

        /// <summary>Matches the shipped rung weenies' PropertyString.Name exactly.</summary>
        public static string ComposeFragmentName(int level)
            => "Raw Fragment (Level " + level.ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>Matches the shipped rung weenies' PropertyString.PluralName exactly.</summary>
        public static string ComposeFragmentPluralName(int level)
            => "Raw Fragments (Level " + level.ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>
        /// Builds a finished Thread Gem. Requires an empty Load and a non-zero Seed; returns null and sets
        /// <paramref name="error"/> otherwise, so a caller that has already charged a player can report
        /// exactly what went wrong. Both checks run BEFORE the world-database read, so they are testable
        /// without a world database.
        ///
        /// Entries and maxEntries are separate (ruling P2-R19) so a partly-spent count can survive a round
        /// trip through the press. A fresh pressing passes the same number twice - a pressed gem is always
        /// full, and add_entry raises both - while /dd give passes its requested count for both.
        /// </summary>
        public static WorldObject CreateGem(DungeonGemSpec spec, int entries, int maxEntries, string dungeonName, out string error)
        {
            error = null;

            if (spec == null) { error = NoSpecError; return null; }
            if (spec.Load.Count > 0) { error = GemHasLoadError; return null; }
            if (spec.Seed == 0) { error = GemHasNoSeedError; return null; }

            var gem = WorldObjectFactory.CreateNewWorldObject(DungeonGemWcid);
            if (gem == null)
            {
                error = $"Weenie {DungeonGemWcid} is not in the world database. Apply Content/sql/weenies/1003600 Thread Gem.sql first.";
                return null;
            }

            var maxCount = (ushort)Math.Clamp(maxEntries, 0, ushort.MaxValue);
            var entryCount = (ushort)Math.Clamp(entries, 0, maxCount);

            gem.SetProperty(PropertyString.DungeonGemSpec, spec.Serialize());
            gem.MaxStructure = maxCount;
            gem.Structure = entryCount;
            gem.SetProperty(PropertyString.LongDesc, ThreadDungeonGemHandler.ComposeLongDesc(spec, dungeonName, entryCount, maxCount));
            gem.PaletteTemplate = PaletteForTier(spec.Tier);

            return gem;
        }

        /// <summary>
        /// The gem's colour for a given loot tier. Only tiers 7 and 8 are reachable from shipped content
        /// today (the fourteen rungs of <see cref="Rungs"/> carry tier 7 or 8), so this only needs one cut: tier 8 and
        /// above comes back Blue, anything lower - including a tier this factory has never seen - falls to
        /// Green rather than erroring. Pure and world-database-free so it can be unit tested directly.
        /// </summary>
        public static int PaletteForTier(int tier)
            => tier >= BlueTierThreshold ? TierBluePaletteTemplate : TierGreenPaletteTemplate;

        /// <summary>
        /// Builds a Raw Fragment. ALWAYS built on <see cref="RawFragmentBaseWcid"/> and always rewrites Name,
        /// PluralName, LongDesc and the spec string from <paramref name="spec"/>, so a gem re-opened at any
        /// level comes back as a truthfully named fragment rather than one labelled "Level 185". Requires
        /// Seed 0; returns null and sets <paramref name="error"/> otherwise.
        ///
        /// A re-open passes the gem's OWN Structure and MaxStructure (ruling P2-R19), so a gem with 1 of 3
        /// entries left comes back as a fragment reading "Entries: 1 of 3" rather than "1 of 1" - the ceiling
        /// an add_entry dose earned is not silently spent by walking the gem's entries down.
        /// </summary>
        public static WorldObject CreateFragment(DungeonGemSpec spec, int entries, int maxEntries, out string error)
        {
            error = null;

            if (spec == null) { error = NoSpecError; return null; }
            if (spec.Seed != 0) { error = FragmentHasSeedError; return null; }

            var fragment = WorldObjectFactory.CreateNewWorldObject(RawFragmentBaseWcid);
            if (fragment == null)
            {
                error = $"Weenie {RawFragmentBaseWcid} is not in the world database. Apply Content/sql/weenies/1003615 Raw Fragment Level 185.sql first.";
                return null;
            }

            var maxCount = (ushort)Math.Clamp(maxEntries, 0, ushort.MaxValue);
            var entryCount = (ushort)Math.Clamp(entries, 0, maxCount);

            fragment.SetProperty(PropertyString.DungeonGemSpec, spec.Serialize());
            fragment.MaxStructure = maxCount;
            fragment.Structure = entryCount;
            fragment.SetProperty(PropertyString.Name, ComposeFragmentName(spec.Level));
            fragment.SetProperty(PropertyString.PluralName, ComposeFragmentPluralName(spec.Level));
            fragment.SetProperty(PropertyString.LongDesc, ComposeFragmentLongDesc(spec, entryCount, maxCount));

            return fragment;
        }

        /// <summary>
        /// The ONE fragment description composer, used at creation (<see cref="CreateFragment"/>) and after every
        /// component dose (RawFragment.UseObjectOnTarget). The normal fragment text, preceded on a SPECIAL guide
        /// fragment by ThreadGuideText's REQUIRED / loaded line. A non-guide spec never reads the attunement
        /// table here, so an ordinary fragment's description is exactly what it always was.
        /// </summary>
        public static string ComposeFragmentLongDesc(DungeonGemSpec spec, int entries, int maxEntries)
        {
            var baseDesc = ThreadDungeonGemHandler.ComposeLongDesc(spec, ResolveDungeonName(spec), entries, maxEntries);

            if (!ThreadGuideRules.IsGuide(spec))
                return baseDesc;

            var defs = ThreadDungeonManager.Store?.Attunement;

            return ThreadGuideText.ComposeFragmentLongDesc(baseDesc, spec, type => RawFragmentRules.DosesOfType(spec, defs, type));
        }

        /// <summary>
        /// The name ComposeLongDesc should print for this spec. An "any" spec that is still unbound has its
        /// label overridden to "Any" inside ComposeLongDesc, so what is passed here only matters for a spec
        /// naming a specific dungeon.
        /// </summary>
        public static string ResolveDungeonName(DungeonGemSpec spec)
        {
            if (spec == null || spec.DungeonId == DungeonGemSpec.Any)
                return "any dungeon";

            return ThreadDungeonManager.Store.Dungeons.TryGetValue(spec.DungeonId, out var dungeon) ? dungeon.Name : spec.DungeonId;
        }
    }
}
