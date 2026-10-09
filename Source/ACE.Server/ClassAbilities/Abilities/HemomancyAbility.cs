using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T1 splash: damage-over-time effects of every school tick 2/4/6/8/10% harder by rank, each
    /// tick heals the caster for 1% of the damage it deals, and Drain spells drain 2/4/6/8/10% more. It is
    /// the class's third route into its own health economy, alongside Transfusion's surplus cascade and
    /// Sanguine Ward's absorb.
    ///
    /// IMPLEMENTED, bespoke rather than hooked - three separate call sites, none of them a
    /// ClassAbilityHooks interface:
    ///
    ///  - THE TICK BONUS applies at EnchantmentManager.ApplyDamageTick, per contribution, via
    ///    Player.GetHemomancyDotMod. Unlike Withering (void-only, gated on DamageType.Nether), this reads
    ///    unconditionally for every damage type - the per-enchantment loop there already resolves a
    ///    sourcePlayer for each contribution before it is folded into the tick total, which is also what
    ///    makes the heal possible (see below). PvE only: gated on the tick's TARGET not being a Player,
    ///    matching Withering's PvP carve-out at the same site.
    ///
    ///  - THE HEAL is credited to the caster from the ACTUAL applied damage (post overkill-clamp, the same
    ///    figure DamageHistory and the per-damager combat lines use), not the raw uncapped roll - so an
    ///    overkill tick cannot produce an inflated heal. It rides the same per-damager loop that already
    ///    calls TakeDamageOverTime_NotifySource, and is therefore skipped on a killing tick exactly the way
    ///    that notify call already is (the method returns before that loop once the target dies). The
    ///    fraction is FLAT - <see cref="ClassAbilityDefinition.AffinitySkill"/>'s Healing affinity multiplies
    ///    only the tick and Drain bonuses, never this fraction - and is gated on Hemomancy rank &gt;= 1, not
    ///    on the tick bonus being nonzero.
    ///
    ///  - THE DRAIN BONUS applies at WorldObject_Magic.HandleCastSpell_Transfer, via
    ///    Player.GetHemomancyDrainMod, folded into effectiveTransferCap alongside the existing crit-cap
    ///    multiplier. It rides the CAP rather than the roll for the same reason the crit bonus already
    ///    does (see that method's own comment): the roll is proportional to the target's current health and
    ///    routinely exceeds TransferCap for anything worth draining, so scaling the roll alone would be
    ///    invisible whenever the drain is capped, which is the common case. PvE and player-cast only,
    ///    matching every other Blood Mage lever in that method - this is independent of Transfusion's rank
    ///    gate and applies even at Transfusion rank 0.
    ///
    /// AFFINITY: Healing, multiplying the tick and Drain rank bonuses (never the flat heal fraction, never
    /// a gear term - this ability carries no equipment mod). The GetReadout below is what backs the
    /// declaration for ClassAbilityAffinityDeclarationTests; the live mechanic's own affinity calls live in
    /// Player_ClassAbilityBuffs.cs (GetHemomancyDotMod / GetHemomancyDrainMod), the same split Withering uses
    /// for GetWitheringVoidDotMod.
    /// </summary>
    public class HemomancyAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Hemomancy,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 1,
            Name = "hemomancy",
            DisplayName = "Hemomancy",
            Description = "Your damage-over-time effects of every school tick 2/4/6/8/10% harder (by rank), " +
                          "and each tick heals you for 1% of the damage it deals. Your Drain spells drain " +
                          "2/4/6/8/10% more. Higher Healing multiplies the bonus.",
            MaxRank = 5,
            CostPerRank = new[] { 1, 1, 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Healing,
        };

        /// <summary>
        /// The DoT tick multiplier at a given rank (1.0 = none): 1 + rank*percentPerRank + the Healing
        /// affinity rider. Applied to every damage school - EnchantmentManager.ApplyDamageTick reads this
        /// unconditionally via Player.GetHemomancyDotMod, unlike Withering's void-only gate. Pure for
        /// testability. No gear term: this ability carries no equipment mod.
        /// </summary>
        public static float TickMultiplier(int rank, double percentPerRank, double affinityRider)
        {
            if (rank <= 0)
                return 1.0f;

            var bonus = rank * percentPerRank + Math.Max(0.0, affinityRider);

            return (float)(1.0 + bonus);
        }

        /// <summary>
        /// The Drain multiplier at a given rank (1.0 = none): identical shape to <see cref="TickMultiplier"/>,
        /// folded into effectiveTransferCap in WorldObject_Magic.HandleCastSpell_Transfer via
        /// Player.GetHemomancyDrainMod. Pure for testability.
        /// </summary>
        public static float DrainMultiplier(int rank, double percentPerRank, double affinityRider)
        {
            if (rank <= 0)
                return 1.0f;

            var bonus = rank * percentPerRank + Math.Max(0.0, affinityRider);

            return (float)(1.0 + bonus);
        }

        /// <summary>
        /// The flat, NOT rank-scaled, NOT affinity-scaled self-heal fraction: the caster's own Hemomancy
        /// rank must be &gt;= 1 for any heal at all, but the fraction itself never changes with rank or
        /// Healing. Pure for testability.
        /// </summary>
        public static double HealFraction(int rank, double healFractionTunable)
        {
            return rank > 0 ? healFractionTunable : 0.0;
        }

        /// <summary>
        /// Mirrors the rank/affinity terms fed into <see cref="TickMultiplier"/> above (x100 for display).
        /// The tick bonus is reported here as the headline term - the ability's primary advertised effect.
        /// The heal fraction stays out of the readout (flat, carries no rank or affinity term to display).
        /// The Drain bonus - GetHemomancyDrainMod's own rank/Healing-scaled term - shares TickMultiplier's
        /// shape but scales a genuinely separate mechanic (Drain spells, not DoT ticks), so it is reported
        /// as a Secondary now that the contract supports one, mirroring GetHemomancyDrainMod exactly.
        /// No gear term exists for this ability, and nothing here is ever capped.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_hemomancy_tick_percent_per_rank").Item;
            var rankBonus = rank <= 0 ? 0.0 : rank * perRank;

            // Affinity is a MULTIPLIER on the rank term, reported as the AMOUNT it adds so the three
            // displayed terms stay in one unit and still sum to Effective.
            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Healing);

            var skill = rankBonus * 100.0;
            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;
            const double gear = 0.0;

            var total = skill + affinity + gear;

            var drainPerRank = PropertyManager.GetDouble("class_ability_hemomancy_drain_percent_per_rank").Item;
            var drainRankBonus = rank <= 0 ? 0.0 : rank * drainPerRank;
            var drainAffinity = (drainRankBonus * multiplier - drainRankBonus) * 100.0;

            var drainSecondary = new ClassAbilityReadout
            {
                HasValue = true,
                Skill = drainRankBonus * 100.0,
                Affinity = drainAffinity,
                Gear = 0.0,
                Effective = drainRankBonus * 100.0 + drainAffinity,
                Unit = "%",
                Label = "drain",
                Per = null,
                CapNote = null,
            };

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "dot tick",
                Per = null,
                CapNote = null,
                Secondary = new List<ClassAbilityReadout> { drainSecondary },
            };
        }
    }
}
