using System;
using System.Collections.Generic;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Appraisal lines for a cloak carrying a Mantle and/or a Proc Amplifier. Computed live from the item and the
    /// settings on every appraisal, never stored, so retuning armor-per-level retunes every existing cloak.
    /// </summary>
    public static class CloakMantleDisplay
    {
        public static IEnumerable<string> GetAppraisalLines(WorldObject wo)
        {
            var lines = new List<string>();

            if (wo == null || !Cloak.IsCloak(wo))
                return lines;

            var rearGuard = RearGuardEffect(wo);
            if (rearGuard != null)
                lines.Add($"Rear Guard: {rearGuard}");

            var amplifier = ProcAmplifierEffect(wo);
            if (amplifier != null)
                lines.Add($"Proc Amplifier: {amplifier}");

            return lines;
        }

        /// <summary>
        /// The text after "Rear Guard: " on the appraisal line, or null when the item is not a cloak or carries no Rear
        /// Guard. The ONE source of this text: the appraisal line and the market listing's mod row both read it.
        /// </summary>
        public static string RearGuardEffect(WorldObject wo)
        {
            if (wo == null || !Cloak.IsCloak(wo) || wo.GetProperty(PropertyBool.CloakRearGuard) != true)
                return null;

            var al = CloakMantleMath.RearGuardArmorLevel(
                true,
                wo.ItemLevel,
                PropertyManager.GetDouble("cloak_rear_guard_armor_per_level").Item,
                PropertyManager.GetBool("cloak_rear_guard_enabled").Item,
                false);

            return $"{(int)Math.Round(al, MidpointRounding.AwayFromZero)} armor vs attacks from behind (cloak level {wo.ItemLevel ?? 0})";
        }

        /// <summary>The text after "Proc Amplifier: " on the appraisal line ("+150%"), or null when the item is not a cloak or has no amplifier. Shared with the market row.</summary>
        public static string ProcAmplifierEffect(WorldObject wo)
        {
            if (wo == null || !Cloak.IsCloak(wo))
                return null;

            var amp = wo.GetProperty(PropertyFloat.CloakProcAmplifier) ?? 0.0;

            return amp > 0.0 ? $"+{CloakMantleApplicator.Pct(amp)}%" : null;
        }
    }
}
