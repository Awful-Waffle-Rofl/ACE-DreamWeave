using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Vanguard T1 as of 2026-09-29 (owner ruling), having entered at T2 in the 2026-09-12 class ability
    /// overhaul in the slot Shield Check vacated. Only the TIER moved: the cost is still 1 CAP per rank,
    /// MaxRank is still 3, the affinity is still Armor Tinkering and none of the mechanics below changed.
    /// Every attack that REACHES the Vanguard builds a charge - any damage type, and whether the blow lands, is
    /// blocked, or is parried - and at 5 charges the next attack spends the whole stack for 20/40/60% more
    /// damage by rank. Charges fade after 10 seconds without being hit.
    ///
    /// COUNTING BLOCKED AND PARRIED HITS IS THE WHOLE POINT, and is what makes this a real Vanguard entry
    /// where Shield Check was not: it pays the tank for being attacked rather than for holding a second
    /// class's avoidance ability, so it is worth the same to every Vanguard instead of only to one who also
    /// bought Rogue's Parry.
    ///
    /// THAT PROMISE IS WHY IT COUNTS FROM THE PRE-WRITE HOOK AT THE Observe BAND rather than from
    /// IIncomingDamageAbility. The post-write hook hangs off Player.TakeDamage, which is melee, missile and
    /// hotspots ONLY - magic damage and DoT ticks write Health straight to the vital and never reach it - so
    /// a charge counter wired there would silently read "any damage type" as "any PHYSICAL damage type". The
    /// pre-write dispatch is the one that sees all five sites. <see cref="ModifyIncomingDamage"/> counts and
    /// writes nothing to the context, which is the Observe band's contract.
    ///
    /// TWO FEEDS, AND NEITHER CAN DOUBLE-COUNT. A blow that deals damage arrives at the hook above exactly
    /// once per site. A blow that is BLOCKED OR PARRIED never gets that far - the avoidance roll in
    /// DamageEvent.DoCalculateDamage resolves before the hit is applied - so it is fed separately from
    /// Player.OnClassAbilityAttackAvoided, the same place blocked hits already reach Thorns. The two paths
    /// are disjoint by construction: an avoided attack deals no damage and a damaging attack was not avoided.
    ///
    /// A DoT TICK IS NOT AN ATTACK and is excluded here. IsDamageOverTime, not a null attacker, is the test
    /// for that: an excluded PvP attacker also leaves Attacker null, and a tick that ramps a Vanguard's
    /// charges five times a fight without anyone swinging at them is not the mechanic that was signed off.
    ///
    /// PvE ONLY on every path: the damage feed requires the dispatch's filtered Attacker, the avoidance feed
    /// re-checks for a Player attacker itself, and the spend is reached only through
    /// ApplyOutgoingDamageClassAbilities, which excludes Player targets.
    ///
    /// MELEE-ONLY CLEAVE ON THE SPENDING STRIKE: when the spend lands on a melee attack, that same strike
    /// also cleaves into class_ability_kineticcharge_cleave_targets (default 2) extra nearby creatures, on
    /// top of whatever the weapon's own CleaveTargets and Whirlwind's +1 already grant - it works even on a
    /// weapon with no innate cleave, same as Whirlwind. It does NOT widen the arc; the normal 180-degree
    /// front arc still applies unless Whirlwind is also active. A missile spend keeps the damage bonus and
    /// grants no cleave - see the CombatType gate in <see cref="ModifyOutgoingDamage"/>.
    ///
    /// EVERY TARGET THE SPENDING STRIKE CLEAVES ALSO TAKES THE DAMAGE BONUS (user ruling 2026-09-14) - the
    /// aimed (primary) target AND every cleave hit alongside it, whether that cleave came from the Kinetic
    /// extra above, the weapon's own CleaveTargets, or Whirlwind. The STACK CONSUME still happens exactly
    /// once, on the primary hit only: a cleave hit never touches TryConsumeKineticCharges (see
    /// Player.ResolvingMeleeCleaveHits), it only receives the bonus fraction the primary hit already
    /// recorded on Player.KineticChargeCleaveBonus. If the primary MISSES, nothing was spent, that field
    /// stays 0.0, and every cleave hit of that strike gets nothing - the bonus rides the spend, not the
    /// cleave list. <see cref="ModifyOutgoingDamage"/> is the single site both halves run through.
    /// </summary>
    public class KineticChargeAbility : IClassAbility, IPreWriteDamageAbility, IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.KineticCharge,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 1,
            Name = "kinetic_charge",
            DisplayName = "Kinetic Charge",
            Description = "Every attack that reaches you builds a charge - any damage type, whether it " +
                          "lands, is blocked, or is parried. At 5 charges your next attack spends them all " +
                          "for 20/40/60% more damage (by rank), and a melee attack that spends them also " +
                          "cleaves up to 2 more nearby enemies, even with a weapon that does not normally " +
                          "cleave; every enemy that attack cleaves takes the bonus damage too. Charges fade " +
                          "after 10 seconds without being hit. Higher Armor Tinkering multiplies the damage " +
                          "bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ArmorTinkering,
        };

        /// <summary>
        /// Observe band: this handler counts a hit and changes no damage, so it must not sit between two
        /// mitigations. See DamageMitigationOrder.Observe.
        /// </summary>
        public int MitigationOrder => DamageMitigationOrder.Observe;

        /// <summary>
        /// FALSE. The charge stack is worth nothing without a rank: the spend recomputes its bonus from the
        /// live rank and Player.TryConsumeKineticCharges is only reached through the rank-filtered outgoing
        /// hook, so an unlearn makes held charges inert rather than stranding a pool that is still paying
        /// out. Contrast Sanguine Ward's absorb, which keeps draining and must declare TRUE.
        /// </summary>
        public bool RunsWithoutLearnedRank => false;

        /// <summary>
        /// RANK-GATED and DAMAGE-NEUTRAL: it adds one charge and returns. <paramref name="rank"/> is unused
        /// because rank buys the SPEND bonus, not the charge threshold or the stack size - the threshold is
        /// its own tunable and identical at every rank.
        /// </summary>
        public void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context)
        {
            if (context.IsDamageOverTime || context.Attacker == null)
                return;

            defender.AddKineticCharge();
        }

        /// <summary>
        /// The payoff: a full stack is spent for 20/40/60% by rank on this hit. Melee, missile and multishot
        /// extra hits all route through Player.DamageTarget and so all qualify; spells deliberately do not -
        /// the description says "your next attack", and a Vanguard's damage is a swing.
        ///
        /// TryConsumeKineticCharges is all-or-nothing: it returns FALSE (and spends nothing) below the
        /// threshold, so a partial stack is never cashed for a partial bonus.
        ///
        /// MELEE ONLY, this same strike also arms a cleave: <see cref="Player.SetKineticChargeCleaveTargets"/>
        /// is set from the tunable and read back by Creature.GetCleaveTarget for the primary DamageTarget
        /// call this hook is nested inside - so the value must be set here, before that call returns to the
        /// strike loop in Player_Melee.cs. A missile spend leaves it untouched at 0 (the strike loop resets
        /// it before every primary hit), so it keeps the damage bonus and grants no cleave.
        ///
        /// This same primary hit also records the bonus fraction on <see cref="Player.SetKineticChargeCleaveBonus"/>,
        /// so every cleave hit the strike lands can receive it too (user ruling 2026-09-14) without spending
        /// the stack a second time.
        ///
        /// A CLEAVE HIT MUST NEVER REACH TryConsumeKineticCharges AT ALL - checked FIRST, via
        /// Player.ResolvingMeleeCleaveHits (set by Player_Melee.cs around the cleave-hit foreach). A cleave
        /// hit is a target the player did not aim at, so a CONSUME there would cash the stack on the wrong
        /// strike; and by the time a cleave hit resolves, GetCleaveTarget already fixed the cleave list, so
        /// the spend could not even add its own extra targets. Instead, while that flag is set, this method
        /// takes an entirely separate branch: it applies the RECORDED bonus (from the strike's primary hit,
        /// 0.0 and therefore a no-op if the primary missed) and returns immediately - no consume, no second
        /// chat line. The stack itself stays held until an attack lands on the aimed (primary) target, on
        /// this strike or a later one.
        /// </summary>
        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || rank <= 0)
                return;

            if (attacker.ResolvingMeleeCleaveHits)
            {
                damageEvent.Damage *= (float)CleaveHitDamageMultiplier(true, attacker.KineticChargeCleaveBonus);
                return;
            }

            if (!attacker.TryConsumeKineticCharges())
                return;

            var bonus = SpendBonus(rank,
                PropertyManager.GetDouble("class_ability_kineticcharge_percent_per_rank").Item,
                attacker.GetClassAbilityAffinityMultiplier(Skill.ArmorTinkering));

            if (bonus <= 0.0)
                return;

            damageEvent.Damage *= (float)(1.0 + bonus);

            var cleaveTargets = 0;

            if (damageEvent.CombatType == CombatType.Melee)
            {
                cleaveTargets = FlooredCleaveTargets(PropertyManager.GetLong("class_ability_kineticcharge_cleave_targets").Item);
                attacker.SetKineticChargeCleaveTargets(cleaveTargets);
                attacker.SetKineticChargeCleaveBonus(bonus);
            }

            // The spend has no tell of its own - without this line a +60% hit is indistinguishable from
            // ordinary weapon variance. It lands just before DamageTarget's attacker notification, so it reads
            // as the cause of the hit line that follows it.
            if (attacker.Session != null && !attacker.SquelchManager.Squelches.Contains(target, ChatMessageType.CombatSelf))
            {
                var cleaveClause = cleaveTargets > 0 ? $", cleaving {cleaveTargets} more targets" : string.Empty;

                attacker.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"You release your kinetic charge! (+{bonus * 100.0:N0}% damage{cleaveClause})", ChatMessageType.CombatSelf));
            }
        }

        /// <summary>
        /// The multiplier a cleave hit on the spending strike should take: 1 + the recorded bonus while
        /// resolving a cleave hit AND a positive bonus was actually recorded (a melee spend happened on this
        /// strike's primary), else a bare 1.0 no-op - covering both "not a cleave hit at all" and "this was
        /// a cleave hit, but the primary missed so nothing was ever spent (recordedBonus stays 0.0)". Pure,
        /// so the three cases are unit-testable without a live Player.
        /// </summary>
        public static double CleaveHitDamageMultiplier(bool resolvingCleaveHit, double recordedBonus) =>
            resolvingCleaveHit && recordedBonus > 0.0 ? 1.0 + recordedBonus : 1.0;

        /// <summary>
        /// Floors the cleave-targets tunable at 0, so a mis-tuned negative value cannot make
        /// GetCleaveTarget's total go negative. Pure, so it is unit-testable without a live Player.
        /// </summary>
        public static int FlooredCleaveTargets(long tunable) => (int)Math.Max(0L, tunable);

        /// <summary>
        /// The damage bonus on the attack that spends a full stack, as a fraction: rank * perRank multiplied
        /// by the Armor Tinkering affinity factor. Pure, so the spend and the readout share one expression.
        ///
        /// Armor Tinkering MULTIPLIES this ability's OWN rank bonus (the 2026-09-12 model) rather than
        /// riding beside it. At zero effective Armor Tinkering the factor is exactly 1.0 and the bonus is
        /// bit-identical to rank alone.
        /// </summary>
        public static double SpendBonus(int rank, double percentPerRank, double affinityMultiplier)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = rank * percentPerRank;

            return rankBonus * (affinityMultiplier < 1.0 ? 1.0 : affinityMultiplier);
        }

        /// <summary>
        /// Mirrors <see cref="SpendBonus"/> exactly (x100 for display) - the two must stay in step. Reports
        /// the bonus the next full-stack spend is worth, not whether a stack is currently held.
        ///
        /// Affinity is reported as the AMOUNT the multiplier adds, so the terms sum exactly to Effective.
        /// No cap applies at any term (CapNote null), and the ability carries no equipment mod (Gear 0) -
        /// the Shield Check mod row its tier slot inherited is still held inert in the equipment-mod
        /// registry and is deliberately NOT repurposed by this slice.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_kineticcharge_percent_per_rank").Item;

            var rankBonus = rank <= 0 ? 0.0 : rank * perRank;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.ArmorTinkering);

            var skill = rankBonus * 100.0;
            var affinity = (SpendBonus(rank, perRank, multiplier) - rankBonus) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = skill + affinity,
                Unit = "%",
                Label = "charged dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}
