using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Void/Summon T2: combat-pet stats (health, damage, defenses) scale up per rank, with a Leadership
    /// rider on the stat bonus. Bespoke: applied to a freshly summoned CombatPet in CombatPet.Init via
    /// Player.GetEmpoweredSummonsStatMod. Carries IPassiveStatAbility because it hooks no combat event of
    /// its own.
    ///
    /// The SECOND half, shipped 2026-09-12 on the repo owner's call: pet *duration* scales with Loyalty
    /// (SKILL-TABLES-PREVIEW's long-standing proposal, +1% per 10 points Trained). Applied as a
    /// PropertyInt.Lifespan bump in CombatPet.Init, alongside the stat bonus and read at the same moment,
    /// so a pet keeps the duration it was summoned with.
    ///
    /// It attaches to a real clock: every combat-pet weenie carries Lifespan = 43 (verified against
    /// ace_world 2026-09-12), enforced by WorldObject.Heartbeat's IsLifespanSpent check, against the
    /// essence's 45 s CooldownDuration. An earlier version of this comment claimed ACE combat pets carry
    /// no server-enforced lifespan and deferred this half for want of somewhere to put it; that was simply
    /// false, and it is why the rider sat unbuilt. See PetLifespanSync for how that clock interacts with
    /// Summon 2x.
    ///
    /// What the rider actually buys, so it is not mistaken for a power spike: with a 43 s pet against a
    /// 45 s cooldown the owner was already at roughly 96% pet uptime, and re-using the essence over a live
    /// pet is refused without spending a charge. So longer duration mostly removes the dead gap between
    /// pets and cuts essence charges burned per minute; it does not multiply pet uptime.
    ///
    /// Deliberately NOT fed by the Empowered Summons equipment mod, whose own display text promises
    /// health, damage and defenses only. Gear scales stats; Loyalty scales duration.
    ///
    /// THIRD half, widened 2026-09-12 per the signed-off design text (tools/ca-workbench/workspace.json
    /// rev 50): "pets now gain health, armor, elemental resistances, accuracy and damage". The same
    /// bonusFraction that already scaled Health/DamageRating/DamageResistRating/CritDamageResistRating in
    /// CombatPet.Init now ALSO reaches ArmorLevel (armor), the per-damage-type resist floats (elemental
    /// resistances), and the pet's current attack skill via CreatureSkill.InitLevel (accuracy - the closest
    /// real analogue to accuracy a non-player Creature has). See CombatPet.Init for the exact field list and
    /// the per-axis mapping comment. No new tunable was needed for this half either - it reuses
    /// class_ability_empoweredsummons_percent_per_rank and the existing Leadership rider unchanged.
    ///
    /// A SEPARATE leech rides the same statMod-vs-CombatPet wiring: 3/6/9% of the damage a combat pet lands
    /// is recovered as Health, applied at the four pet-landed-hit sites (Pet.ApplyEmpoweredSummonsLeech,
    /// called beside NotifyOwnerOfDamage), Leadership-scaled the same way the stat half is. RECIPIENT IS
    /// AMBIGUOUS in the design text ("their hits leech...back as health" names no "their") - implemented
    /// healing the PET (see Pet.ApplyEmpoweredSummonsLeech's single switch-point comment to redirect it to
    /// the owner instead). See LeechFraction below and Player.GetEmpoweredSummonsLeechFraction for the
    /// affinity glue.
    ///
    /// The leech rate is class_ability_empoweredsummons_leech_percent_per_rank (0.03/rank), read live in
    /// Player.GetEmpoweredSummonsLeechFraction. It was registered centrally as this wave merged: the
    /// mechanic slice needed the key, correctly declined to add it while six sibling slices were live
    /// against PropertyManager.cs, and carried a hardcoded constant with a loud comment in the interim.
    /// Both the constant and that comment are gone - do not reintroduce either.
    /// </summary>
    public class EmpoweredSummonsAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.EmpoweredSummons,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 2,
            Name = "empoweredsummons",
            DisplayName = "Empowered Summons",
            Description = "Your combat pets are stronger - more health, damage, and defenses per rank. " +
                          "Higher Leadership increases the bonus. Higher Loyalty makes them last longer.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },   // premium reprice 2026-09-13: 1/rank -> 2/rank
            Implemented = true,
            // TWO riders: Leadership is the headline one (pet stats), Loyalty rides the duration half.
            AffinitySkill = Skill.Leadership,
            AdditionalAffinitySkills = new[] { Skill.Loyalty },
        };

        /// <summary>
        /// The combat-pet stat multiplier at a given rank (1.0 = none): 1 + rank*perRank + the Leadership
        /// rider fraction + the equipment-mod term. Pure for testability.
        ///
        /// <paramref name="gearModFraction"/> is the Empowered Summons equipment mod, a STANDALONE mod:
        /// same axis, additive, NOT rank-gated, so at rank 0 this degenerates to 1 + gearModFraction and a
        /// player who never learned the ability still gets a slightly stronger pet. Defaults to 0, which
        /// reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static float StatMultiplier(int rank, double percentPerRank, double leadershipFraction, double gearModFraction = 0.0)
        {
            var abilityBonus = rank <= 0 ? 0.0 : Math.Max(0, rank) * percentPerRank + Math.Max(0.0, leadershipFraction);
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (abilityBonus <= 0.0 && gearBonus <= 0.0)
                return 1.0f;

            return (float)(1.0 + abilityBonus + gearBonus);
        }

        /// <summary>
        /// The multiplier applied to a combat pet's elemental resist floats (1.0 = neutral, LOWER = more
        /// resistant - see Creature_Properties.ResistXMod, which multiplies this straight into damage taken).
        /// Pure for testability; CombatPet.Init is the only caller and owns the actual floor value
        /// (CombatPet.MinResistMod).
        ///
        /// CLAMPED to <paramref name="floor"/> as a CORRECTNESS guard, not a balance cap: bonusFraction (the
        /// same StatMultiplier-derived fraction that scales Health/ArmorLevel/accuracy/DamageRating upward)
        /// is UNBOUNDED, because the Leadership affinity rider has no cap of its own (unlike the proc-chance
        /// family's class_ability_affinity_chance_cap - see PropertyManager.cs:1200's live-observed 5226
        /// effective skill for why "unbounded" is not theoretical). Left unclamped, 1.0 - bonusFraction
        /// crosses zero once bonusFraction passes 1.0 and goes negative beyond that, which would turn
        /// incoming elemental damage into HEALING - a SIGN INVERSION, categorically different from the other
        /// four axes merely becoming very strong.
        /// </summary>
        public static double ResistMultiplier(double bonusFraction, double floor)
        {
            return Math.Max(1.0 - bonusFraction, floor);
        }

        /// <summary>
        /// The multiplier on a combat pet's Lifespan at a given rank (1.0 = none): 1 + the Loyalty rider,
        /// clamped to <paramref name="cap"/>. Pure for testability.
        ///
        /// Unlike <see cref="StatMultiplier"/> there is no per-rank term and no gear term - ranks buy stats,
        /// Loyalty buys duration - so this is RANK-GATED only: a player who has not learned the ability gets
        /// 1.0 however much Loyalty they carry. That gate is the whole reason the ability is the thing being
        /// bought rather than the skill.
        /// </summary>
        public static float DurationMultiplier(int rank, double loyaltyFraction, double cap)
        {
            if (rank <= 0)
                return 1.0f;

            return (float)(1.0 + Math.Clamp(Math.Max(0.0, loyaltyFraction), 0.0, Math.Max(0.0, cap)));
        }

        /// <summary>
        /// The fraction of a pet's dealt damage recovered as Health at a given rank (0.0 = none): rank *
        /// percentPerRank, plus the Leadership rider fraction. Pure for testability - same additive-fraction
        /// shape as UmbralSiphonAbility.LeechFraction. Who receives the heal (the pet, by default) is decided
        /// entirely in Pet.ApplyEmpoweredSummonsLeech, not here - this method only computes the magnitude.
        /// </summary>
        public static double LeechFraction(int rank, double percentPerRank, double leadershipFraction)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = Math.Max(0, rank) * percentPerRank;

            return rankBonus + Math.Max(0.0, leadershipFraction);
        }

        /// <summary>
        /// Mirrors the rank/affinity/gear terms fed into StatMultiplier above (x100 for display) - the two
        /// must stay in step.
        ///
        /// Reports the STAT half as the primary number. The other two rank/affinity-scaled halves - pet
        /// DURATION (Loyalty-scaled, DurationMultiplier) and the pet LEECH fraction (Leadership-scaled,
        /// LeechFraction) - are genuinely separate player-meaningful magnitudes, not restatements of the
        /// stat bonus, so they are reported as Secondary entries rather than folded into Description now
        /// that the readout contract carries more than one scalar.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_empoweredsummons_percent_per_rank").Item;

            // Affinity is a MULTIPLIER on the rank term, reported as the AMOUNT it adds so the three
            // displayed terms stay in one unit and still sum to Effective.
            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Leadership);

            var skill = rankBonus * 100.0;

            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.EmpoweredSummons) * 100.0;

            var total = skill + affinity + gear;

            // DURATION secondary: calls the REAL live mechanic, Player.GetEmpoweredSummonsDurationMod,
            // rather than re-deriving its Loyalty scaling here - EmpoweredSummonsAbility.cs's OWN
            // GetClassAbilityScaling calls must all name Leadership (ClassAbilityAffinityDeclarationTests'
            // EveryHandlerScalingCall_IsDeclaredAsAffinitySkill), so the Loyalty rider stays sourced from
            // Player_ClassAbilityBuffs.cs, exactly as SecondaryScalingCalls documents for this ability's
            // AdditionalAffinitySkills entry. RANK-GATED only (no per-rank term: ranks buy stats, Loyalty
            // alone buys duration), already clamped to class_ability_empoweredsummons_max_duration_bonus
            // internally, so this segment never needs its own CapNote detection.
            var durationSecondary = DurationSecondary(player.GetEmpoweredSummonsDurationMod());

            // LEECH secondary: mirrors LeechFraction above - rank * percentPerRank plus the Leadership
            // rider fraction, same shape as the stat half (and the same affinity skill).
            var leechPerRank = PropertyManager.GetDouble("class_ability_empoweredsummons_leech_percent_per_rank").Item;
            var leechRankBonus = rank <= 0 ? 0.0 : rank * leechPerRank;
            var leechAffinityAdded = leechRankBonus * multiplier - leechRankBonus;

            var leechSecondary = new ClassAbilityReadout
            {
                HasValue = true,
                Skill = leechRankBonus * 100.0,
                Affinity = leechAffinityAdded * 100.0,
                Gear = 0.0,
                Effective = (leechRankBonus + leechAffinityAdded) * 100.0,
                Unit = "%",
                Label = "pet leech",
                Per = null,
                CapNote = null,
            };

            var secondaries = new List<ClassAbilityReadout>();

            // OMITTED, not included with a zero (ClassAbilityReadout.Secondary's own contract): an
            // untrained/Innate Loyalty summoner gets no live "pet duration" line at all, matching every
            // other secondary and primary readout in the catalog that only prints when it has something
            // to say.
            if (durationSecondary.HasValue)
                secondaries.Add(durationSecondary.Value);

            secondaries.Add(leechSecondary);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "pet stats",
                Per = null,
                CapNote = null,
                Secondary = secondaries,
            };
        }

        /// <summary>
        /// The pet-DURATION secondary readout for a given live duration multiplier (1.0 = no bonus, from
        /// <see cref="Player.GetEmpoweredSummonsDurationMod"/>). Returns null - OMITTED, not a zero entry -
        /// when the bonus is not positive (an untrained/Innate Loyalty summoner, or rank &lt;= 0, both of
        /// which GetEmpoweredSummonsDurationMod already resolves to a multiplier of exactly 1.0). Pure for
        /// testability: GetReadout calls this directly rather than restating its arithmetic, so the two
        /// cannot drift, and the zero-omission rule is exercised without a live Player.
        /// </summary>
        public static ClassAbilityReadout? DurationSecondary(double durationMultiplier)
        {
            var durationBonus = Math.Max(0.0, durationMultiplier - 1.0);

            if (durationBonus <= 0.0)
                return null;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = 0.0,
                Affinity = durationBonus * 100.0,
                Gear = 0.0,
                Effective = durationBonus * 100.0,
                Unit = "%",
                Prefix = "+",
                Label = "pet duration",
                Per = null,
                CapNote = null,
            };
        }
    }
}
