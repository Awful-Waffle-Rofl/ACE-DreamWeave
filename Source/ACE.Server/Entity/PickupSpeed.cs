using System;
using System.Collections.Generic;

namespace ACE.Server.Entity
{
    /// <summary>
    /// WaffleACE: pure composition of the player pick-up animation speed multiplier from the live
    /// pickup_animation_speed tunable, any permanent per-character quest boons the player has claimed
    /// (see Player_PickupBoons.cs), and any Custom Dreamweave +10% pick-up speed augmentations they hold
    /// (PropertyInt.AugmentationPickupSpeed). The two bonus sources are additive within one term and share
    /// one clamp - see the six-argument Compute. Kept as a pure function - no PropertyManager reads - because
    /// PropertyManager reads THROW in unit tests on a cache miss, so the math has to be testable without a
    /// live shard config table (same reason as SalvageForge.BagsRequired's explicit-threshold overload).
    /// </summary>
    public static class PickupSpeed
    {
        /// <summary>
        /// Composes the final pick-up animation playback speed.
        ///
        /// - max: non-finite becomes 1.0, then clamped to [1.0, 10.0].
        /// - baseSpeed: reproduces the original sanitize-by-reject semantics (Player_Inventory.cs's old
        ///   GetPickupAnimationSpeed) - anything outside [0.1, 10.0], including non-finite values, is
        ///   treated as 1.0 rather than clamped. This is the live global tunable, so out-of-range here means
        ///   a misconfigured server, not a boon-holder's composed value.
        /// - perBoon: clamped to [0, 10]; non-finite becomes 0.
        /// - boonCount: negative treated as 0.
        /// - The composed result base * (1 + boonCount * perBoon) is CLAMPED to [0.1, max], never reset to
        ///   1.0. This is load-bearing: with a reject instead of a clamp, a boon-holder whose composed speed
        ///   exceeds the accepted range would silently fall back to 1.0 instead of being capped at max.
        /// </summary>
        public static double Compute(double baseSpeed, int boonCount, double perBoon, double max)
        {
            return Compute(baseSpeed, boonCount, perBoon, 0, 0, max);
        }

        /// <summary>
        /// Composes the final pick-up animation playback speed from BOTH permanent sources: the shipped
        /// Quickhand quest boons (boonCount at perBoon each) and the Custom Dreamweave +10% pick-up speed
        /// augmentation (augCount at perAug each, PropertyInt.AugmentationPickupSpeed, uncapped in count).
        ///
        /// The two sources are ADDITIVE inside ONE term - base * (1 + boons*perBoon + augs*perAug) - and
        /// share the SAME [0.1, max] clamp the boon-only overload uses, so a player holding both cannot
        /// exceed pickup_animation_speed_max by any route. augCount and perAug are sanitized exactly as
        /// boonCount and perBoon are (negative count treated as 0; non-finite perAug becomes 0, then clamped
        /// to [0, 10]).
        ///
        /// KNOWN AND ACCEPTED: with the shipped defaults (pickup_animation_speed_max 3.0,
        /// pickup_speed_quest_bonus 0.5) four Quickhand boons alone already reach the ceiling, so further
        /// augmentations are inert until an operator raises pickup_animation_speed_max. That is a tuning
        /// decision, not a bug here - <see cref="IsCapped"/> reports it honestly and /pickupspeed says so.
        /// </summary>
        public static double Compute(double baseSpeed, int boonCount, double perBoon, int augCount, double perAug, double max)
        {
            var s = Sanitize(baseSpeed, boonCount, perBoon, augCount, perAug, max);

            var composed = s.baseSpeed * (1 + s.boonCount * s.perBoon + s.augCount * s.perAug);

            return Math.Clamp(composed, 0.1, s.max);
        }

        /// <summary>
        /// TRUE when the uncapped composed value (base * (1 + boonCount * perBoon), after the same
        /// sanitization Compute applies) exceeds the effective max - i.e. the player's true bonus is being
        /// held down by the cap rather than by their boon count.
        /// </summary>
        public static bool IsCapped(double baseSpeed, int boonCount, double perBoon, double max)
        {
            return IsCapped(baseSpeed, boonCount, perBoon, 0, 0, max);
        }

        /// <summary>
        /// TRUE when the uncapped COMBINED value (boons and augmentations together) exceeds the effective
        /// max. This is the honest-reporting half of the accepted cap collision documented on
        /// <see cref="Compute"/>: once four Quickhand boons have reached the default ceiling, every
        /// additional augmentation is inert, and this is what tells the player so.
        /// </summary>
        public static bool IsCapped(double baseSpeed, int boonCount, double perBoon, int augCount, double perAug, double max)
        {
            var s = Sanitize(baseSpeed, boonCount, perBoon, augCount, perAug, max);

            var composed = s.baseSpeed * (1 + s.boonCount * s.perBoon + s.augCount * s.perAug);

            return composed > s.max;
        }

        /// <summary>
        /// The percent bonus contributed by boonCount boons at perBoon each (perBoon sanitized the same way
        /// Compute sanitizes it), rounded to the nearest whole percent. Used for both the per-boon percent
        /// (boonCount=1) and the total percent (boonCount=current count) in claim/status messages, so the
        /// wording always agrees with Compute's own math.
        /// </summary>
        public static int PercentBonus(int boonCount, double perBoon)
        {
            return PercentBonus(boonCount, perBoon, 0, 0);
        }

        /// <summary>
        /// The COMBINED percent bonus from boons and Custom Dreamweave pick-up augmentations, rounded to the
        /// nearest whole percent. Rounds the sum once rather than rounding each source and adding, so the
        /// printed percent is the same quantity <see cref="Compute"/> multiplies by.
        /// </summary>
        public static int PercentBonus(int boonCount, double perBoon, int augCount, double perAug)
        {
            var s = Sanitize(1.0, boonCount, perBoon, augCount, perAug, 1.0);

            return (int)Math.Round((s.boonCount * s.perBoon + s.augCount * s.perAug) * 100);
        }

        /// <summary>
        /// Renders the /pickupspeed status lines. Pure - no PropertyManager reads - so it is directly unit
        /// testable; the caller (Player.ShowPickupSpeedStatus) supplies the live tunable values.
        /// </summary>
        public static string[] FormatPickupSpeedStatus(int boonCount, double baseSpeed, double perBoon, double max)
        {
            return FormatPickupSpeedStatus(boonCount, baseSpeed, perBoon, 0, 0, max);
        }

        /// <summary>
        /// Renders the /pickupspeed status lines for a player holding boons and/or Custom Dreamweave
        /// pick-up augmentations. Every figure printed here is the COMBINED one, because this class's
        /// contract is that the /pickupspeed wording always agrees with <see cref="Compute"/>; a status line
        /// quoting only the boon half would be a lie the moment an augmentation is bought.
        /// </summary>
        public static string[] FormatPickupSpeedStatus(int boonCount, double baseSpeed, double perBoon, int augCount, double perAug, double max)
        {
            var lines = new List<string>();

            var percent = PercentBonus(boonCount, perBoon, augCount, perAug);
            var speed = Compute(baseSpeed, boonCount, perBoon, augCount, perAug, max);

            lines.Add($"Pick-up speed bonus: {percent} percent ({speed:0.00}x pick-up speed).");

            if (boonCount == 0 && augCount == 0)
                lines.Add("You have not earned any pick-up speed bonuses yet. They are granted by quest rewards.");

            if (IsCapped(baseSpeed, boonCount, perBoon, augCount, perAug, max))
                lines.Add("This is the maximum bonus this server allows.");

            if (baseSpeed != 1.0)
                lines.Add($"Server-wide pick-up speed is currently set to {baseSpeed.ToString("0.##")}x, which is included in the figure above.");

            return lines.ToArray();
        }

        private static (double baseSpeed, int boonCount, double perBoon, int augCount, double perAug, double max) Sanitize(double baseSpeed, int boonCount, double perBoon, int augCount, double perAug, double max)
        {
            if (!double.IsFinite(max))
                max = 1.0;
            max = Math.Clamp(max, 1.0, 10.0);

            if (!double.IsFinite(baseSpeed) || baseSpeed < 0.1 || baseSpeed > 10.0)
                baseSpeed = 1.0;

            if (!double.IsFinite(perBoon))
                perBoon = 0;
            perBoon = Math.Clamp(perBoon, 0, 10);

            if (boonCount < 0)
                boonCount = 0;

            if (!double.IsFinite(perAug))
                perAug = 0;
            perAug = Math.Clamp(perAug, 0, 10);

            if (augCount < 0)
                augCount = 0;

            return (baseSpeed, boonCount, perBoon, augCount, perAug, max);
        }
    }
}
