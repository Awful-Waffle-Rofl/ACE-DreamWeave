using System;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Pure arithmetic for the Summoner casters (Docs/SummonerCasters/DESIGN.md): how a focus's elemental damage
    /// mod scales its wielder's combat pets, and how it cuts the wielder. No world state, so every rule here is
    /// testable without a Player. SummonerFocus resolves WHO a hit applies to and feeds these.
    /// </summary>
    public static class SummonerFocusMath
    {
        /// <summary>Pet damage scale on (elemental damage mod - 1). 1.0 passes the focus bonus to pets 1:1 as damage (owner ruling 2026-11-21).</summary>
        public const double DefaultPetDamageScale = 1.0;

        /// <summary>Pet damage-taken scale on (elemental damage mod - 1). 0 leaves pets taking normal damage (owner ruling 2026-11-21); kept as a lever.</summary>
        public const double DefaultPetDefenseScale = 0.0;

        /// <summary>Lowest the damage-taken multiplier on a pet can fall to.</summary>
        public const double DefaultPetDefenseFloor = 0.25;

        /// <summary>The multiplier on the wielder's own damage while wielding a focus.</summary>
        public const double DefaultWielderDamageMod = 0.25;

        /// <summary>Share (0 to 1) of every focus weapon mod bonus a pet reads. 1.0 = the full bonus (the tuning measured so far).</summary>
        public const double DefaultPetModTransferPercent = 1.0;

        /// <summary>Share (0 to 1) of a focus imbue's bonus a pet hit takes. 1.0 = the full imbue.</summary>
        public const double DefaultPetImbueTransferPercent = 1.0;

        /// <summary>A transfer share: the fallback when NaN, infinite or negative, otherwise clamped to at most 1.</summary>
        public static double SanitizeTransferPercent(double value, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 ? fallback : Math.Min(1.0, value);

        /// <summary>An integer rating bonus at a transfer share, rounded down: floor(rating * share). A rating of 0 or less is returned as is.</summary>
        public static int ScaleRating(int rating, double share) =>
            rating <= 0 ? rating : (int)Math.Floor(rating * share);

        /// <summary>A chance imbue (Critical Strike) at a transfer share: baseline + (value - baseline) * share.</summary>
        public static float ScaleImbueChance(float value, float baseline, double share) =>
            (float)(baseline + (value - baseline) * share);

        /// <summary>A multiplier imbue whose neutral value is 1 (Crippling Blow, a rend) at a transfer share: 1 + (value - 1) * share.</summary>
        public static float ScaleImbueMultiplier(float value, double share) =>
            (float)(1.0 + (value - 1.0) * share);

        /// <summary>Armor Rending (the share of armor kept, 1 = none ignored) at a transfer share: 1 - (1 - value) * share.</summary>
        public static float ScaleArmorRending(float value, double share) =>
            (float)(1.0 - (1.0 - value) * share);

        /// <summary>1 + damageScale * max(0, elementalDamageMod - 1). A mod at or below 1 gives 1.</summary>
        public static double DealtMultiplier(double elementalDamageMod, double damageScale) =>
            1.0 + damageScale * Math.Max(0.0, elementalDamageMod - 1.0);

        /// <summary>clamp(1 - defenseScale * max(0, elementalDamageMod - 1), floor, 1).</summary>
        public static double TakenMultiplier(double elementalDamageMod, double defenseScale, double floor)
        {
            var value = 1.0 - defenseScale * Math.Max(0.0, elementalDamageMod - 1.0);
            return Math.Min(1.0, Math.Max(floor, value));
        }

        /// <summary>
        /// A hit's applied damage with the wielder cut it took divided back out: the amount a follow-on effect sized from
        /// that hit starts from when its own damage lands through a sink that applies the cut again. 0 for a cut of 0 or
        /// less, where the hit itself dealt nothing.
        /// </summary>
        public static double BeforeWielderCut(double appliedDamage, double wielderDamageMod) =>
            wielderDamageMod > 0.0 ? appliedDamage / wielderDamageMod : 0.0;

        /// <summary>The value, or the fallback when it is NaN, infinite or negative.</summary>
        public static double SanitizeScale(double value, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 ? fallback : value;

        /// <summary>The value, or the fallback when it is NaN or outside [0, 1].</summary>
        public static double SanitizeUnit(double value, double fallback) =>
            double.IsNaN(value) || value < 0.0 || value > 1.0 ? fallback : value;
    }
}
