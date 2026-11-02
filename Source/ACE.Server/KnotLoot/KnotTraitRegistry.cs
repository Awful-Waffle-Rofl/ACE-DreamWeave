using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;

namespace ACE.Server.KnotLoot
{
    public enum KnotTraitId
    {
        CastSpeed = 1,
        ShieldTrade = 2,
        FrugalHand = 3,
        WristTrade = 4,
        OverpoweringIntent = 5,
    }

    /// <summary>Which existing PvP mod predicate a trait follows: Weapon follows Player.WeaponModSuppressed, Equipment follows Player.EquipmentModSuppressed.</summary>
    public enum KnotTraitSuppressionKind
    {
        Equipment,
        Weapon,
    }

    /// <summary>
    /// One Knot trait. An item carries the trait when it has <see cref="Property"/> (its stored position u in [0,1]);
    /// behaviour is keyed on that property, never on wcid.
    /// </summary>
    public sealed class KnotTraitDefinition
    {
        public KnotTraitId Id { get; init; }

        public PropertyFloat Property { get; init; }

        public string DisplayName { get; init; }

        /// <summary>The knot_trait_{prefix}_* settings prefix.</summary>
        public string SettingPrefix { get; init; }

        public KnotTraitSuppressionKind Suppression { get; init; }

        /// <summary>
        /// The shipped settings, built from <see cref="KnotLootDefaults"/>. This exact object is the fallback passed to
        /// <see cref="KnotLootTunables.Trait"/>: the property store caches a caller's fallback for unseeded keys, so a
        /// fallback that differed from the registered default would cache a wrong value.
        /// </summary>
        public KnotTraitSettings Defaults { get; init; }
    }

    public static class KnotTraitRegistry
    {
        private static readonly KnotTraitRange Unused = new(0, 0);

        private static readonly KnotTraitDefinition[] Definitions =
        {
            new KnotTraitDefinition
            {
                Id = KnotTraitId.CastSpeed,
                Property = PropertyFloat.KnotTraitCastSpeed,
                DisplayName = "Mirrorglass Haste",
                SettingPrefix = "cast_speed",
                Suppression = KnotTraitSuppressionKind.Weapon,
                Defaults = new KnotTraitSettings(
                    KnotLootDefaults.CastSpeedEnabled,
                    new KnotTraitRange(KnotLootDefaults.CastSpeedMin, KnotLootDefaults.CastSpeedMax),
                    Unused),
            },
            new KnotTraitDefinition
            {
                Id = KnotTraitId.ShieldTrade,
                Property = PropertyFloat.KnotTraitShieldTrade,
                DisplayName = "Rose's Bargain",
                SettingPrefix = "shield_trade",
                Suppression = KnotTraitSuppressionKind.Equipment,
                Defaults = new KnotTraitSettings(
                    KnotLootDefaults.ShieldTradeEnabled,
                    new KnotTraitRange(KnotLootDefaults.ShieldTradeArmorMin, KnotLootDefaults.ShieldTradeArmorMax),
                    new KnotTraitRange(KnotLootDefaults.ShieldTradeHealthMin, KnotLootDefaults.ShieldTradeHealthMax)),
            },
            new KnotTraitDefinition
            {
                Id = KnotTraitId.FrugalHand,
                Property = PropertyFloat.KnotTraitFrugalHand,
                DisplayName = "Frugal Hand",
                SettingPrefix = "frugal_hand",
                Suppression = KnotTraitSuppressionKind.Equipment,
                Defaults = new KnotTraitSettings(
                    KnotLootDefaults.FrugalHandEnabled,
                    new KnotTraitRange(KnotLootDefaults.FrugalHandMin, KnotLootDefaults.FrugalHandMax),
                    Unused),
            },
            new KnotTraitDefinition
            {
                Id = KnotTraitId.WristTrade,
                Property = PropertyFloat.KnotTraitWristTrade,
                DisplayName = "Glass Turtle",
                SettingPrefix = "wrist_trade",
                Suppression = KnotTraitSuppressionKind.Equipment,
                Defaults = new KnotTraitSettings(
                    KnotLootDefaults.WristTradeEnabled,
                    new KnotTraitRange(KnotLootDefaults.WristTradeGainMin, KnotLootDefaults.WristTradeGainMax),
                    new KnotTraitRange(KnotLootDefaults.WristTradeCostMin, KnotLootDefaults.WristTradeCostMax)),
            },
            new KnotTraitDefinition
            {
                Id = KnotTraitId.OverpoweringIntent,
                Property = PropertyFloat.KnotTraitOverpoweringIntent,
                DisplayName = "Overpowering Intent",
                SettingPrefix = "overpowering_intent",
                Suppression = KnotTraitSuppressionKind.Weapon,
                Defaults = new KnotTraitSettings(
                    KnotLootDefaults.OverpoweringIntentEnabled,
                    new KnotTraitRange(KnotLootDefaults.OverpoweringIntentMin, KnotLootDefaults.OverpoweringIntentMax),
                    Unused),
            },
        };

        private static readonly Dictionary<KnotTraitId, KnotTraitDefinition> ById = Definitions.ToDictionary(d => d.Id);

        public static IReadOnlyList<KnotTraitDefinition> All { get; } = System.Array.AsReadOnly(Definitions);

        public static bool TryGet(KnotTraitId id, out KnotTraitDefinition definition) => ById.TryGetValue(id, out definition);
    }
}
