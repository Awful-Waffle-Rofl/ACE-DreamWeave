using System;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.KnotLoot
{
    /// <summary>
    /// A resolved trait: whether it applies, the window position p, and the positive magnitudes of its one or two
    /// halves. A linked trade is resolved from ONE p, and when it does not apply both halves are zero together
    /// (<see cref="None"/>), so a reader can never keep the cost without the benefit.
    /// </summary>
    public readonly record struct KnotTraitEffect(bool Active, double P, double Primary, double Secondary)
    {
        public static readonly KnotTraitEffect None = default;
    }

    /// <summary>
    /// The read-time trait resolver. Every combat, vitals and appraisal read of a Knot trait goes through here.
    /// The item stores only its level, table and trait position u; p and the values are computed from current
    /// settings on every read, and nothing is ever written back to the item.
    /// </summary>
    public static class KnotTraitValue
    {
        /// <summary>
        /// Pure. None when u is null, the mechanic is disabled, or the wielder is suppressed. A missing level reads as
        /// the window's low level.
        /// </summary>
        public static KnotTraitEffect Resolve(double? u, int? knotLevel, KnotWindowSettings window, KnotTraitSettings s, bool suppressed)
        {
            if (u == null || !s.Enabled || suppressed)
                return KnotTraitEffect.None;

            var level = knotLevel ?? (int)Math.Clamp(window.LowLevel, int.MinValue, int.MaxValue);

            var p = KnotLootWindow.Position(level, u.Value, window);

            return new KnotTraitEffect(
                true,
                p,
                KnotLootWindow.Lerp(s.Primary.Min, s.Primary.Max, p),
                KnotLootWindow.Lerp(s.Secondary.Min, s.Secondary.Max, p));
        }

        /// <summary>
        /// Live: resolves trait <paramref name="id"/> on <paramref name="item"/> as worn by <paramref name="wielder"/>,
        /// from the current settings. None when the item lacks the trait property. The window comes from the item's
        /// KnotLootTable; an unknown or missing table uses <see cref="KnotWindowSettings.Default"/>. Read-only: never
        /// sets a property on the item.
        /// </summary>
        public static KnotTraitEffect ResolveOn(WorldObject item, KnotTraitId id, WorldObject wielder)
        {
            if (item == null || !KnotTraitRegistry.TryGet(id, out var definition))
                return KnotTraitEffect.None;

            var u = item.GetProperty(definition.Property);

            if (u == null)
                return KnotTraitEffect.None;

            var settings = KnotLootTunables.Trait(definition.SettingPrefix, definition.Defaults);

            if (!settings.Enabled)
                return KnotTraitEffect.None;

            if (KnotTraitSuppression.SuppressedFor(wielder, definition.Suppression))
                return KnotTraitEffect.None;

            var window = WindowFor(item.GetProperty(PropertyString.KnotLootTable));

            return Resolve(u, item.GetProperty(PropertyInt.KnotLootLevel), window, settings, false);
        }

        /// <summary>
        /// Only a table in <see cref="KnotLootTables"/> is read from the store: an item-supplied table string is never
        /// turned into a setting key, so an unknown table cannot plant fallback entries in the settings cache.
        /// </summary>
        internal static KnotWindowSettings WindowFor(string table)
        {
            if (string.IsNullOrEmpty(table))
                return KnotWindowSettings.Default;

            return KnotLootTables.TryGet(table, out var known) ? KnotLootTunables.Window(known.Key) : KnotWindowSettings.Default;
        }
    }

    /// <summary>
    /// Per-wielder trait suppression. Traits apply only to Player wielders and follow the existing PvP mod predicates
    /// (<see cref="Player.KnotTraitSuppressed"/>); a null or non-Player wielder is never suppressed.
    /// </summary>
    public static class KnotTraitSuppression
    {
        /// <summary>
        /// How a Player's suppression is resolved. A seam only so tests can drive it without a live Player
        /// (the same idiom as WeaponModSuppression.PlayerSuppressedSource); production never reassigns it.
        /// </summary>
        internal static Func<Player, KnotTraitSuppressionKind, bool> PlayerSuppressedSource = DefaultPlayerSuppressed;

        private static bool DefaultPlayerSuppressed(Player player, KnotTraitSuppressionKind kind) => player.KnotTraitSuppressed(kind);

        public static bool SuppressedFor(WorldObject wielder, KnotTraitSuppressionKind kind) => wielder is Player player && PlayerSuppressedSource(player, kind);
    }
}
