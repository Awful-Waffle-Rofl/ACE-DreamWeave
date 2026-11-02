using System;
using System.Collections.Generic;

namespace ACE.Server.KnotLoot
{
    /// <summary>A post-creation change the materializer applies to a dropped item. None leaves the item as authored.</summary>
    public enum KnotLootMutation
    {
        None = 0,

        /// <summary>The upgraded Knot cloak roll (<see cref="UpgradedCloak"/>).</summary>
        UpgradedCloak = 1,
    }

    /// <summary>
    /// One weighted entry of a pick group. <see cref="Key"/> is the setting-key suffix (knot_loot_{table}_weight_{Key}).
    /// <see cref="Mutation"/> is applied by the materializer; the roller only copies it and makes no draw for it.
    /// </summary>
    public sealed record KnotLootEntry(string Key, uint Wcid, long DefaultWeight, KnotTraitId? Trait, KnotLootMutation Mutation = KnotLootMutation.None);

    /// <summary>
    /// One weighted draw: exactly one entry of the group is chosen. <see cref="Gated"/> makes the group optional: the
    /// roller first draws one gate u and skips the group when u is at or above the live chance
    /// (knot_loot_{table}_chance_{Key}, defaulting to <see cref="DefaultChance"/>). An ungated group has no gate and no
    /// extra draw. Gating is static so a chance tuned to 1 still draws its gate and stays tunable.
    /// </summary>
    public sealed record KnotLootPickGroup(string Key, IReadOnlyList<KnotLootEntry> Entries, double DefaultChance = 1.0, bool Gated = false);

    /// <summary>A stackable currency drop. <see cref="Key"/> is the setting-key prefix (knot_loot_{table}_{Key}_min / _max).</summary>
    public sealed record KnotLootCurrency(string Key, uint Wcid, long DefaultMin, long DefaultMax);

    public sealed record KnotLootTable(string Key, IReadOnlyList<KnotLootPickGroup> Picks, IReadOnlyList<KnotLootCurrency> Currency);

    /// <summary>The shipped loot tables. A table's defaults come from <see cref="KnotLootDefaults"/>.</summary>
    public static class KnotLootTables
    {
        public static readonly KnotLootTable Grael = new KnotLootTable(
            "grael",
            new List<KnotLootPickGroup>
            {
                new KnotLootPickGroup("infusion", new List<KnotLootEntry>
                {
                    new KnotLootEntry("elemental_infusion", 1007400, KnotLootDefaults.GraelWeightElementalInfusion, null),
                    new KnotLootEntry("armor_infusion", 1007401, KnotLootDefaults.GraelWeightArmorInfusion, null),
                    new KnotLootEntry("crippling_infusion", 1007402, KnotLootDefaults.GraelWeightCripplingInfusion, null),
                }),
                new KnotLootPickGroup("rare", new List<KnotLootEntry>
                {
                    new KnotLootEntry("mirrorglass_orb", 1007450, KnotLootDefaults.GraelWeightMirrorglassOrb, KnotTraitId.CastSpeed),
                    new KnotLootEntry("shield_of_the_rose", 1007451, KnotLootDefaults.GraelWeightShieldOfTheRose, KnotTraitId.ShieldTrade),
                    new KnotLootEntry("frugal_gauntlets", 1007452, KnotLootDefaults.GraelWeightFrugalGauntlets, KnotTraitId.FrugalHand),
                    new KnotLootEntry("glass_turtle_bracelet", 1007453, KnotLootDefaults.GraelWeightGlassTurtleBracelet, KnotTraitId.WristTrade),
                }),
            },
            new List<KnotLootCurrency>
            {
                new KnotLootCurrency("mmd", 20630, KnotLootDefaults.GraelMmdMin, KnotLootDefaults.GraelMmdMax),
                new KnotLootCurrency("note", 43901, KnotLootDefaults.GraelNoteMin, KnotLootDefaults.GraelNoteMax),
            });

        public static readonly KnotLootTable Matron = new KnotLootTable(
            "matron",
            new List<KnotLootPickGroup>
            {
                new KnotLootPickGroup("infusion", new List<KnotLootEntry>
                {
                    new KnotLootEntry("elemental_infusion", 1007400, KnotLootDefaults.MatronWeightElementalInfusion, null),
                    new KnotLootEntry("armor_infusion", 1007401, KnotLootDefaults.MatronWeightArmorInfusion, null),
                    new KnotLootEntry("crippling_infusion", 1007402, KnotLootDefaults.MatronWeightCripplingInfusion, null),
                }),
                new KnotLootPickGroup("rare", new List<KnotLootEntry>
                {
                    new KnotLootEntry("voidclaw", 1007600, KnotLootDefaults.MatronWeightVoidclaw, null),
                    new KnotLootEntry("torc", 1007601, KnotLootDefaults.MatronWeightTorc, null),
                    new KnotLootEntry("hive_stinger_baton", 1007603, KnotLootDefaults.MatronWeightHiveStingerBaton, KnotTraitId.OverpoweringIntent),
                    new KnotLootEntry("hive_nether_baton", 1007604, KnotLootDefaults.MatronWeightHiveNetherBaton, KnotTraitId.OverpoweringIntent),
                }),
                new KnotLootPickGroup("spines", new List<KnotLootEntry>
                {
                    new KnotLootEntry("spines", 1007602, KnotLootDefaults.MatronWeightSpines, null, KnotLootMutation.UpgradedCloak),
                }, KnotLootDefaults.MatronChanceSpines, Gated: true),
            },
            new List<KnotLootCurrency>
            {
                new KnotLootCurrency("mmd", 20630, KnotLootDefaults.MatronMmdMin, KnotLootDefaults.MatronMmdMax),
                new KnotLootCurrency("note", 43901, KnotLootDefaults.MatronNoteMin, KnotLootDefaults.MatronNoteMax),
            });

        private static readonly KnotLootTable[] All = { Grael, Matron };

        /// <summary>Looks a table up by key, compared lower-invariant. Null or unknown is false.</summary>
        public static bool TryGet(string key, out KnotLootTable table)
        {
            table = null;

            if (string.IsNullOrEmpty(key))
                return false;

            var lower = key.ToLowerInvariant();

            foreach (var candidate in All)
            {
                if (string.Equals(candidate.Key, lower, StringComparison.Ordinal))
                {
                    table = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
