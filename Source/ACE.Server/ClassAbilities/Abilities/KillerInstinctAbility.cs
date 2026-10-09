using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T3: a critical hit grants an Opening for 8 seconds, stacking up to 3, and each Opening is worth
    /// 4/7/10% critical damage by rank plus a flat 2% critical chance. A landed Sneak Attack grants an
    /// Opening whether or not it crits, which is the entry's second route into the stack and the reason the
    /// affinity is Sneak Attack on both halves.
    ///
    /// THE ONLY ENTRY IN THIS WAVE PRICED AT 2 CAP PER RANK (6 to max, against 3 or 5 for everything else).
    /// That is the signed-off cost table, not a typo to be flattened onto the wave's 1-per-rank curve: a
    /// stacking crit-damage-and-crit-chance engine sits on two multiplying axes at once, which is what the
    /// surcharge is buying.
    ///
    /// TWO SEPARATE CHOKE POINTS, because the two halves fire at different points in the combat pipeline:
    ///  - Critical DAMAGE is applied from the IOutgoingDamageAbility hook (ModifyOutgoingDamage below),
    ///    exactly like every other outgoing-damage-percent ability, gated on damageEvent.IsCritical - the
    ///    crit roll has already happened by the time this hook runs, so this only ever scales a hit that
    ///    IS a crit, never decides whether one occurs.
    ///  - Critical CHANCE has to feed the roll itself, which happens earlier in DamageEvent.DoCalculateDamage
    ///    (WorldObject.GetWeaponCriticalChance), well before ModifyOutgoingDamage is ever dispatched. So the
    ///    flat +2%/opening chance is read there directly (Player.GetKillerInstinctCritChanceBonus), the same
    ///    bespoke-integration pattern the existing Crit Rating class ability already uses (it also cannot
    ///    ride IOutgoingDamageAbility for the same reason). It is rank-invariant and carries NO affinity -
    ///    only the critical-damage half is multiplied by Sneak Attack (see Definition.AffinitySkill's doc
    ///    comment on why a declaration only promises a rider exists, not which half of a two-part mechanic
    ///    carries it).
    ///
    /// ORDERING WITHIN ONE HIT: this hit's own crit-damage bonus is computed from Openings held BEFORE this
    /// hit (Player.GetKillerInstinctStacks, read first), and only THEN does the hit grant/refresh its own
    /// Opening (Player.AddKillerInstinctOpening, called last) - the same "a hit must not boost itself"
    /// discipline Resonance documents at its own AddResonanceStack call.
    ///
    /// Openings are transient, non-persisted stack state (Player_ClassAbilityBuffs.killerInstinctStacks),
    /// shared-window expiry exactly like Frenzy/Resonance/Nether Rush: any new Opening refreshes the WHOLE
    /// pool's expiry to now + 8s, rather than each Opening tracking its own independent countdown.
    ///
    /// AFFINITY: Sneak Attack, MULTIPLYING the critical-damage half only (2026-09-12 overhaul model), not an
    /// additive rider. At zero effective Sneak Attack the multiplier is exactly 1.0 and the crit-damage bonus
    /// is bit-identical to rank/stacks alone.
    /// </summary>
    public class KillerInstinctAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.KillerInstinct,
            AbilityClass = ClassAbilityClass.Rogue,
            Tier = 3,
            Name = "killer_instinct",
            DisplayName = "Killer Instinct",
            Description = "Every critical hit grants an Opening for 8 seconds, stacking up to 3. Each " +
                          "Opening adds 4/7/10% critical damage (by rank) and 2% critical chance. A landed " +
                          "Sneak Attack grants an Opening whether or not it crits. Higher Sneak Attack " +
                          "multiplies the critical damage.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },
            Implemented = true,
            AffinitySkill = Skill.SneakAttack,
        };

        /// <summary>
        /// The per-Opening critical-damage rate at a given rank: base at rank 1, plus one step per rank
        /// above that (4/7/10% for ranks 1-3 with the shipped tunables). 0 for rank &lt;= 0. Pure for
        /// testability.
        /// </summary>
        public static double PerOpeningCritDamage(int rank, double baseRate, double stepPerRank)
        {
            if (rank <= 0)
                return 0.0;

            return baseRate + (rank - 1) * stepPerRank;
        }

        /// <summary>
        /// Runs in the CritDamage band, ahead of every Modify handler, so Poison Weapon and Acid Proc (same
        /// hook, Modify band) read the bonus this handler stashes. See OutgoingDamageDispatchOrder.CritDamage.
        /// </summary>
        public int DispatchOrder => OutgoingDamageDispatchOrder.CritDamage;

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || rank <= 0)
                return;

            // Critical damage: scales THIS hit only if it IS a crit, using Openings held BEFORE this hit.
            if (damageEvent.IsCritical)
            {
                var stacks = attacker.GetKillerInstinctStacks();
                if (stacks > 0)
                {
                    var perOpening = PerOpeningCritDamage(rank,
                        PropertyManager.GetDouble("class_ability_killerinstinct_critdamage_base").Item,
                        PropertyManager.GetDouble("class_ability_killerinstinct_critdamage_step").Item);

                    var rankBonus = stacks * perOpening;

                    // Sneak Attack is MULTIPLICATIVE on this ability's own crit-damage bonus, not the flat
                    // crit-chance half (that one carries no affinity at all - see the type doc comment).
                    var affinity = attacker.GetClassAbilityAffinityMultiplier(Skill.SneakAttack);

                    var bonus = rankBonus * affinity;
                    if (bonus > 0.0)
                    {
                        damageEvent.Damage *= (float)(1.0 + bonus);

                        // The same number, stashed for the on-hit procs (DamageEvent.ProcDamageMultiplier) -
                        // the strike takes it exactly once above; the procs never read Damage.
                        damageEvent.ClassAbilityCritDamageBonus = bonus;
                    }
                }
            }

            // Grant/refresh an Opening AFTER this hit's own bonus is fixed: any critical hit, or any landed
            // Sneak Attack whether or not it happened to crit (SneakAttackMod > 1.0f means a sneak attack
            // landed - see Creature.GetSneakAttackMod).
            if (damageEvent.IsCritical || damageEvent.SneakAttackMod > 1.0f)
                attacker.AddKillerInstinctOpening();
        }

        /// <summary>
        /// Reports the PER-OPENING crit-damage rate (Per = "/opening"), the same "rate, not total" shape
        /// Frenzy's readout uses - the /abilities list is "how strong is one stack", not "what is the
        /// current live total right now". Affinity is a MULTIPLIER on that rate, reported as the AMOUNT it
        /// adds (perOpening * multiplier - perOpening), so the three displayed terms stay in one unit and
        /// still sum to the effective bonus. No gear mod exists for this ability, so Gear is always 0. The
        /// flat crit-CHANCE half (rank-invariant, no affinity) is described in the ability's Description
        /// text rather than folded into this scalar, since it is a second, differently-shaped axis with
        /// nothing to multiply.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perOpening = PerOpeningCritDamage(rank,
                PropertyManager.GetDouble("class_ability_killerinstinct_critdamage_base").Item,
                PropertyManager.GetDouble("class_ability_killerinstinct_critdamage_step").Item);

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.SneakAttack);

            var skill = perOpening * 100.0;
            var affinity = (perOpening * multiplier - perOpening) * 100.0;
            var total = skill + affinity;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = total,
                Unit = "%",
                Label = "crit dmg",
                Per = "/opening",
                CapNote = null,
            };
        }
    }
}
