using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T2: an enemy that has damaged the Berserker in the last 15 seconds takes 4/8/12% more
    /// damage by rank FROM THE BERSERKER, and the window refreshes on every further hit it lands. The effect
    /// is scoped to the pair - it is a grudge against one attacker, not a global damage bonus - so a
    /// Berserker fighting something that never hits back gets nothing from it.
    ///
    /// TWO HOOKS, ONE PAIR: IIncomingDamageAbility.OnDamageTaken (Player.ApplyIncomingDamageClassAbilities)
    /// records/refreshes the attacker's grudge window every time it lands a hit - fires on ANY contact,
    /// including one mitigated to 0 damage, matching that hook's own contract (a hit that connects still
    /// "damaged" the pair for this purpose). IOutgoingDamageAbility.ModifyOutgoingDamage then reads the
    /// window back to decide whether THIS target owes the bonus. State lives in
    /// Player_ClassAbilityBuffs.vengeanceAttackers (keyed by attacker guid), lazily expired on read and
    /// swept on ClassAbilityBuffsHeartbeat, same shape as every other transient class-ability buff there.
    ///
    /// PHYSICAL-ONLY ON THE TRACKING SIDE: Player.TakeDamage is the sole IIncomingDamageAbility entry point
    /// (melee/missile/hotspot), so a monster that only ever hits the Berserker with a spell never arms the
    /// grudge. The APPLYING side has no such restriction beyond what IOutgoingDamageAbility itself already
    /// carries (melee/missile weapon hits vs a monster, never PvP - the dispatch excludes player targets).
    ///
    /// AFFINITY: Recklessness, MULTIPLYING this ability's own rank bonus (2026-09-12 overhaul model), not an
    /// additive rider. At zero effective Recklessness the multiplier is exactly 1.0 and the bonus is
    /// bit-identical to rank alone.
    /// </summary>
    public class VengeanceAbility : IOutgoingDamageAbility, IIncomingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Vengeance,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 2,
            Name = "vengeance",
            DisplayName = "Vengeance",
            Description = "Enemies that have damaged you in the last 15 seconds take 4/8/12% more damage " +
                          "(by rank) from you. The window refreshes every time they land another hit. " +
                          "Higher Recklessness multiplies the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },   // premium reprice 2026-09-13: 1/rank -> 2/rank
            Implemented = true,
            AffinitySkill = Skill.Recklessness,
        };

        /// <summary>
        /// Arms/refreshes the grudge window for this attacker. Called on every landed physical hit against
        /// the defender - see Player.ApplyIncomingDamageClassAbilities for the shared gates (monster
        /// attackers only, never PvP/self/dead) that already ran before this fires.
        /// </summary>
        public void OnDamageTaken(Player defender, int rank, Creature attacker, DamageType damageType, uint damageTaken)
        {
            defender?.OnVengeanceAttackerHit(attacker);
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || rank <= 0)
                return;

            if (!attacker.IsVengeanceTarget(target))
                return;

            var rankBonus = rank * PropertyManager.GetDouble("class_ability_vengeance_percent_per_rank").Item;

            var affinity = attacker.GetClassAbilityAffinityMultiplier(Skill.Recklessness);

            var bonus = rankBonus * affinity;
            if (bonus <= 0.0)
                return;

            damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// Mirrors the rank/affinity terms in ModifyOutgoingDamage above exactly (x100 for display) - the
        /// two must stay in step. No gear mod exists for this ability, so Gear is always 0. No cap, so
        /// Effective always equals Total. Reports the bonus as if the target were currently in the grudge
        /// window (a "how strong is the ability" readout, not a "is this target owed it right now"
        /// simulation), matching Executioner/Savage Blows convention.
        ///
        /// Affinity is a MULTIPLIER on the rank term, but it is reported as the AMOUNT that multiplier adds
        /// (rankBonus * affinity - rankBonus), so the three displayed terms stay in the same unit and still
        /// sum to the effective bonus.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank * PropertyManager.GetDouble("class_ability_vengeance_percent_per_rank").Item;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Recklessness);

            var skill = rankBonus * 100.0;
            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;
            var total = skill + affinity;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = total,
                Unit = "%",
                Label = "dmg vs attacker",
                Per = null,
                CapNote = null,
            };
        }
    }
}
