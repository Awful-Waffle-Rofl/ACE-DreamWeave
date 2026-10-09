using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T1: the Drain Health surplus cascade. Without this ability
    /// <c>WorldObject.HandleCastSpell_Transfer</c> bounds the transfer by the caster's own missing health, so
    /// a life caster at full health drains for ZERO - that is retail behaviour and it stays the baseline for
    /// everyone else. With it, the surplus that would have been discarded cascades to fellowship members AND
    /// the caster's own summons within 10m who need it, at 40/70/100% by rank, plus an uncapped Healing rider
    /// that can push delivery past 100% (party healing only - it is applied after every cap, so it can never
    /// touch drain damage or the caster's own recoup). No fellowship is required since 2026-08-03: a solo
    /// caster with a hurt summon in range cascades to it. BLOOD-MAGE-DESIGN.md sec 3 / 3c.
    ///
    /// IMPLEMENTED, and bespoke rather than hooked: the mechanic already shipped
    /// (<c>WorldObject_Magic.GetDrainSurplusFellows</c> gates on
    /// <c>GetClassAbilityRank(ClassAbilityId.Transfusion) &gt;= 1</c> and scales by
    /// <see cref="ACE.Server.ClassAbilities.DrainSurplusDistribution.ShareFraction"/>, tunables
    /// class_ability_transfusion_share_rN plus the shared multiplicative affinity rate pair
    /// (class_ability_affinity_rate_per_trained/_per_spec) since the 2026-09-12 overhaul. This
    /// definition is what makes that gate reachable at all - before it, the rank was structurally 0. Carries
    /// IPassiveStatAbility for the same reason Parry does: the effect is read at its own site, not dispatched
    /// from a combat hook.
    /// </summary>
    public class TransfusionAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Transfusion,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 1,
            Name = "transfusion",
            DisplayName = "Transfusion",
            Description = "Your Drain Health surplus cascades to fellowship members and your own summons " +
                          "within 10 metres who need it, at 40/70/100% by rank, instead of being discarded. " +
                          "No fellowship required. Higher Healing delivers more than was drained. Drain " +
                          "damage is identical at every rank. Against monsters only.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Healing,
        };

        /// <summary>
        /// What fraction of Drain Health's surplus is DELIVERED to the caster's fellows/summons, for a
        /// caster at the given Transfusion rank. Rank sets the base fraction (40/70/100% via the
        /// class_ability_transfusion_share_rN tunables); the Healing rider is additive on top and
        /// deliberately uncapped, so a skilled healer delivers more than the drain produced. See
        /// <see cref="DrainSurplusDistribution.ShareFraction"/> for why a share above 100% is safe - this
        /// method only assembles the tunable reads and the rider, it does not change that arithmetic.
        ///
        /// Moved out of WorldObject_Magic.GetDrainSurplusFellows so the tunable reads live beside the
        /// ability they belong to rather than inline in the cast pipeline.
        ///
        /// THE TRANSFUSION EQUIPMENT MOD IS READ HERE, as a third additive summand handed to
        /// DrainSurplusDistribution.ShareFraction (DESIGN.md 3.3, the axis rule). It is machinery, and
        /// ownership is doubly guaranteed: the only combat caller reaches this behind
        /// DrainSurplusEligibility.CasterQualifies, which requires transfusionRank &gt;= 1, AND the pure
        /// helper returns 0 for rank &lt;= 0 before the gear term is ever added - so a rank-0 caller passing
        /// a stray rank cannot leak the mod either.
        ///
        /// Healing is MULTIPLICATIVE on the rank's own share fraction (2026-09-12 overhaul), not an
        /// additive dual-ratio quotient. ShareFraction's healingRider parameter stays additive in its own
        /// units, so the value fed here is the AMOUNT the multiplier adds
        /// (rankFraction * affinity - rankFraction), not the multiplier itself.
        /// </summary>
        public static double ShareFraction(Player caster, int rank)
        {
            var r1 = PropertyManager.GetDouble("class_ability_transfusion_share_r1").Item;
            var r2 = PropertyManager.GetDouble("class_ability_transfusion_share_r2").Item;
            var r3 = PropertyManager.GetDouble("class_ability_transfusion_share_r3").Item;

            var rankFraction = rank switch
            {
                <= 0 => 0.0,
                1 => r1,
                2 => r2,
                _ => r3,
            };

            var affinity = caster.GetClassAbilityAffinityMultiplier(Skill.Healing);
            var healingRider = rankFraction * affinity - rankFraction;

            return DrainSurplusDistribution.ShareFraction(
                rank, r1, r2, r3,
                healingRider,
                caster.GetEquippedModValue(EquipmentModId.Transfusion));
        }

        /// <summary>
        /// Skill = the rank's base surplus-share fraction (40/70/100%); Affinity = the amount the Healing
        /// MULTIPLIER adds to it (rankFraction * affinity - rankFraction); Gear = the TRANSFUSION equipment
        /// mod - computed the same way ShareFraction() above assembles them, and the three mirror each
        /// other because DrainSurplusDistribution.ShareFraction is additive on all three terms
        /// (rankFraction + healingRider + gear, floored at 0). The share is DELIBERATELY UNCAPPED - a
        /// skilled healer delivers more than the drain produced - so CapNote is always null.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankFraction = DrainSurplusDistribution.ShareFraction(
                rank,
                PropertyManager.GetDouble("class_ability_transfusion_share_r1").Item,
                PropertyManager.GetDouble("class_ability_transfusion_share_r2").Item,
                PropertyManager.GetDouble("class_ability_transfusion_share_r3").Item,
                0.0);

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Healing);
            var healingRider = rankFraction * multiplier - rankFraction;

            var gearShare = player.GetEquippedModValue(EquipmentModId.Transfusion);

            var skill = rankFraction * 100.0;
            var affinity = healingRider * 100.0;
            var gear = gearShare * 100.0;
            var effective = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = effective,
                Unit = "%",
                Label = "surplus share",
                Per = null,
                CapNote = null,
            };
        }
    }
}
