using System;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T3 game-changer: melee swings in ANY melee stance (unarmed included) become a full 360-degree
    /// arc and strike 1 / 2 / 3 additional targets by rank ON TOP of the weapon's own cleave - even a weapon
    /// with no innate cleave, or no weapon at all, gains those extra targets. Each Whirlwind swing costs
    /// +35% / +75% / +100% stamina by rank; if stamina is insufficient the swing falls back to a normal swing.
    /// Deliberately unscaled (no affinity).
    ///
    /// BERSERKER PASS 2026-10-24 (owner ruling): MaxRank 1 -> 3 at 1/1/1 (it was a single rank at 3 -
    /// ClassAbilityRepricing wave 3 promotes every owner to rank 3 at no cost), every melee stance qualifies
    /// rather than two-handed / dual wield only, the extra targets became rank-scaled, and the flat +100%
    /// surcharge became a per-rank one with the same semantics.
    ///
    /// Bespoke: the stamina surcharge + affordability decision live in Player.ApplyWhirlwindStamina (gated on
    /// FrenzyAbility.IsQualifyingStance, setting Player.WhirlwindSwingActive and Player.WhirlwindExtraTargets),
    /// and the 360-degree / +rank-target cleave selection reads those in Creature.GetCleaveTarget. Carries
    /// IPassiveStatAbility because it hooks no combat event of its own.
    /// </summary>
    public class WhirlwindAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Whirlwind,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 3,
            Name = "whirlwind",
            DisplayName = "Whirlwind",
            Description = "Your melee swings (any melee style, unarmed included) hit in a full 360-degree arc and " +
                          "strike 1 / 2 / 3 extra targets by rank on top of your weapon's own cleave - even with no " +
                          "innate cleave. Each Whirlwind swing costs +35% / +75% / +100% stamina by rank; with too " +
                          "little stamina it falls back to a normal swing.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // was a single rank at 3 until 2026-10-24 (wave 3 PromoteToPaid)
            Implemented = true,
        };

        /// <summary>Extra cleave targets a Whirlwind swing adds at a given rank: the rank itself (1/2/3), 0 if unlearned.</summary>
        public static int ExtraTargets(int rank) => Math.Clamp(rank, 0, 3);

        /// <summary>The per-rank stamina-surcharge tunable key (rank clamped to 1-3).</summary>
        public static string SurchargeKey(int rank) => "class_ability_whirlwind_stamina_surcharge_r" + Math.Clamp(rank, 1, 3);

        /// <summary>
        /// The stamina surcharge for one swing: baseStaminaCost x fraction, rounded to the nearest point and
        /// floored at 1, so a cheap swing (base cost 1 at +35%) still pays for its cleave rather than rounding to
        /// a free one. 0 only for a non-positive fraction, which disables Whirlwind (ApplyWhirlwindStamina treats
        /// a 0 surcharge as "no Whirlwind swing"). Pure.
        /// </summary>
        public static int Surcharge(int baseStaminaCost, double fraction)
        {
            if (fraction <= 0.0)
                return 0;

            return Math.Max(1, (int)Math.Round(baseStaminaCost * fraction));
        }

        /// <summary>
        /// Reports the STAMINA SURCHARGE for this rank as the primary scalar, with the extra-target count as a
        /// Secondary. No affinity and no gear term exist for either.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var surcharge = rank <= 0 ? 0.0 : PropertyManager.GetDouble(SurchargeKey(rank)).Item * 100.0;
            var targets = (double)ExtraTargets(rank);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = surcharge,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = surcharge,
                Unit = "%",
                Prefix = "+",
                Label = "stamina/swing",
                Per = null,
                CapNote = null,
                Secondary = new System.Collections.Generic.List<ClassAbilityReadout>
                {
                    new ClassAbilityReadout
                    {
                        HasValue = true,
                        Skill = targets,
                        Affinity = 0.0,
                        Gear = 0.0,
                        Effective = targets,
                        Unit = "",
                        Prefix = "+",
                        Label = "cleave targets",
                        Per = null,
                        CapNote = null,
                    },
                },
            };
        }
    }
}
