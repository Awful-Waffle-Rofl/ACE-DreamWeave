using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Server.Managers;

namespace ACE.Server.EquipmentMods
{
    /// <summary>
    /// Rolls and sanitizes equipment-mod POTENCY scalars. A potency is always in [0, 1] and is never a
    /// magnitude - see <see cref="EquipmentModRegistry"/> for why, and <see cref="EquipmentModValue"/> for the
    /// resolution step that turns a potency into an applied effect.
    ///
    /// <see cref="Clamp01"/> is applied at BOTH ends: at write, so a bad roll or a bad admin edit can never
    /// land out of range, and again at read, because a shard rollback can resurrect rows written by an older
    /// build. Read-side clamping is the only defense that survives a restore.
    /// </summary>
    public static class EquipmentModRoller
    {
        /// <summary>
        /// The potency floor a definition gets when it declares no <see cref="EquipmentModDefinition.MinPotency"/>
        /// of its own, i.e. the floor 27 of the 30 catalog rows use.
        ///
        /// EVERY mod is floored, not just the integer-quantized ones. The governing rule is "it should not be
        /// possible to have a fully useless special mod", and a near-zero potency produces exactly that on a
        /// continuous pipeline too: the resolved magnitude is rendered through
        /// <see cref="EquipmentModDefinition.DisplayFormat"/>, so a low enough roll prints as a literal 0. The
        /// reported case was "Frenzied Pace: +0%" - MaxMagnitude 0.0015 at DisplayScale 100 renders through
        /// "0.###" as 0 for any potency below 0.00333, about 1 roll in 300 of an unfloored draw.
        ///
        /// 0.10 is chosen to keep the gamble intact: the reachable band is still [0.10, 1], and the roll is
        /// rescaled over that band rather than truncated, so a low roll is still a bad roll - just not a dead
        /// one. It raises the mean potency of an unfloored mod from 0.500 to 0.550.
        /// </summary>
        public const double DefaultMinPotency = 0.10;

        /// <summary>
        /// A uniform potency roll over [<see cref="EquipmentModDefinition.MinPotency"/>, 1]: the high-tier
        /// (conversion / reroll) application's gamble. A converted roll can legitimately land below the
        /// low-tier value - that is the intended risk of rerolling - but never below the mod's floor, which
        /// exists so no mod can roll into a dead band (user rule 2026-07-25, widened to every mod 2026-08-01).
        /// A definition that declares no floor of its own rolls over [<see cref="DefaultMinPotency"/>, 1].
        /// </summary>
        public static double RollPotency(EquipmentModDefinition definition)
        {
            var floor = MinPotency(definition);

            return Clamp01(floor + ThreadSafeRandom.Next(0.0f, 1.0f) * (1.0 - floor));
        }

        /// <summary>
        /// The potency a low-tier application grants: the equipment_mod_lowtier_potency tunable (default 0.2 =
        /// 20% of a mod's maximum magnitude), raised to the mod's floor. Not a roll - the low tier is a
        /// guaranteed value. At the shipped defaults only Venom and Caustic (floor 0.25) are actually raised:
        /// the 0.2 tunable already sits above <see cref="DefaultMinPotency"/>.
        /// DETERMINISTIC BY DESIGN, NOT A MISSING ROLL: the TigerEye (low-tier) path always pays exactly this
        /// value, because that path's gamble lives entirely in which mod TYPE comes up. Potency RNG is the
        /// Obsidian (high-tier) path's differentiator - see <see cref="RollPotency"/>.
        /// </summary>
        public static double LowTierPotency(EquipmentModDefinition definition)
        {
            var tunable = Clamp01(PropertyManager.GetDouble("equipment_mod_lowtier_potency").Item);

            return Math.Max(tunable, MinPotency(definition));
        }

        /// <summary>
        /// A definition's effective potency floor, in [0, 1). A declared floor is honored only when it is a
        /// usable one; everything else - unset, zero, negative, NaN, or a floor of 1 or higher (which would
        /// collapse the roll to a constant) - falls back to <see cref="DefaultMinPotency"/>.
        ///
        /// THERE IS NO OPT-OUT VALUE. Writing MinPotency = 0.0 on a registry row does not exempt a mod from
        /// the floor; a C# double field cannot tell a declared 0.0 from an unset one, so both read as "use the
        /// default". Exempting a mod would need a separate explicit flag, and would need the useless-mod rule
        /// waived first.
        ///
        /// A null definition is not a mod at all and stays at 0.0.
        /// </summary>
        public static double MinPotency(EquipmentModDefinition definition)
        {
            if (definition == null)
                return 0.0;

            var floor = definition.MinPotency;

            if (double.IsNaN(floor) || floor <= 0.0 || floor >= 1.0)
                return DefaultMinPotency;

            return floor;
        }

        /// <summary>
        /// Rolls <paramref name="count"/> DISTINCT mod types, honoring the no-duplicate-mod-types-on-one-item
        /// rule: nothing in <paramref name="exclude"/> can come up, and nothing rolled can repeat. Returns
        /// fewer than requested only if the catalog runs out of eligible types, which cannot happen in
        /// practice (27 types versus a maximum capacity of 3) - callers should treat a short list as a bug.
        /// </summary>
        public static List<EquipmentModId> RollDistinctModTypes(int count, ICollection<EquipmentModId> exclude = null)
        {
            var rolled = new List<EquipmentModId>();

            if (count <= 0)
                return rolled;

            var taken = exclude == null ? new HashSet<EquipmentModId>() : new HashSet<EquipmentModId>(exclude);

            for (var i = 0; i < count; i++)
            {
                var next = EquipmentModRegistry.RollModType(taken);

                if (next == null)
                    break;

                taken.Add(next.Value);
                rolled.Add(next.Value);
            }

            return rolled;
        }

        /// <summary>
        /// Forces a potency into the valid [0, 1] range. NaN sanitizes to 0 (a NaN would otherwise survive both
        /// comparisons of a naive clamp and poison every downstream multiply).
        /// </summary>
        public static double Clamp01(double potency)
        {
            if (double.IsNaN(potency))
                return 0.0;

            return Math.Clamp(potency, 0.0, 1.0);
        }
    }
}
