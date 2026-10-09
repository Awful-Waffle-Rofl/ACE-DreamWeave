using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archer T2, taking the slot Heavy Draw vacated in the 2026-09-12 class ability overhaul: a missile hit
    /// has a 10/20/30% chance by rank to pin the target in place for 5 seconds. A PIN IS NOT A STUN - the
    /// pinned creature still attacks and still casts, it simply cannot close or flee - and each target
    /// carries a 15 second immunity afterwards, so the effect cannot be chain-applied into a permanent lock.
    /// Both of those limits are the reason the entry is a control tool rather than a damage rider, which is
    /// what Archer T2 was a wall of before the overhaul.
    ///
    /// A PIN IS NOT A RUN-SPEED DEBUFF, AND CANNOT BE. MovementSystem.GetRunRate floors its result at a
    /// positive rate even at skill 0 (the else branch adds a constant 4 before dividing), so a movement-rate
    /// debuff cannot root anything - it would look implemented and do nothing. The pin instead holds the
    /// creature's NextMoveTime forward and cancels the move in flight; see
    /// Creature.TryApplyClassAbilityPin for the mechanism and for why the hold alone is insufficient.
    ///
    /// A MOVEMENT SLOW, HOWEVER, IS ACHIEVABLE, AND IS NOW IMPLEMENTED: the target is held to
    /// class_ability_pinningshot_slow_factor of normal speed (0.2, a fifth) for
    /// class_ability_pinningshot_slow_seconds (10) AFTER the pin ends. It does NOT ride the Run skill, for
    /// exactly the reason the paragraph above gives - it rides MoveToParameters.Speed / MovementParameters.Speed,
    /// the wire and server "walk/run speed multiplier", which MotionTable.add_motion applies straight to the
    /// motion's velocity, omega and animation framerate with no minimum clamp anywhere in the path. See
    /// Creature.TryApplyClassAbilityMoveSlow and Creature.CurrentMovementSlowFactor.
    ///
    /// AND THE GetRunRate FLOOR IS EXACTLY WHY THE SLOW CAN NEVER BECOME A ROOT: a rate multiplier scales
    /// distance per second, so any factor above 0 still moves the creature, and the tunable's own guard refuses
    /// anything outside the open interval (0.0, 1.0). Rooting remains the pin's job, bounded by its duration and
    /// its immunity window; the slow is a tail on that, never a substitute for it.
    ///
    /// "STILL ATTACKS AND CASTS" FALLS OUT OF THE ENGINE, it is not implemented here. Movement gates on
    /// Creature.MoveReady() - Monster_Navigation.StartTurn early-outs on it, and so does CombatPet_Follow -
    /// while the attack branches in Monster_Tick gate on Creature.AttackReady(), which reads NextAttackTime /
    /// NextMagicAttackTime and IsAttackRange() and never consults NextMoveTime or the pin. So a pinned
    /// creature already in range keeps swinging and casting on its own timers, and only its approach and its
    /// flight are suppressed.
    ///
    /// RIDES THE EXISTING IOutgoingDamageAbility HOOK rather than a new one. That hook is already "a landed
    /// weapon hit vs a monster, after damage calculation, never PvP and never on a miss" - which is exactly
    /// this ability's trigger - and it already carries the CombatType on the DamageEvent, so the missile
    /// gate is a field read rather than a new dispatch. The four Spellsword procs ride it the same way
    /// without modifying damage; see the registry's Phase 13 comment. This handler likewise leaves
    /// damageEvent.Damage untouched.
    ///
    /// IT ALSO RIDES ISpellHitAbility as of 2026-09-13, so a landed damaging spell projectile can pin too.
    /// THE SECOND INTERFACE IS THE OPT-IN, AND THAT IS THE WHOLE POINT OF DOING IT THIS WAY. Spell damage
    /// does not reach ApplyOutgoingDamageClassAbilities at all (its only call site is Player.DamageTarget,
    /// the weapon path; SpellProjectile.DamageTarget is a different method on a different type and never
    /// calls it), so spell reach could NOT be granted by widening that dispatch without handing it to all
    /// 23 handlers hanging off it - Bloodlust, Poison Weapon, Sundermark, Hunter's Mark, Savage Blows and
    /// the rest - none of which may have it. Declaring the interface here means the C# type list is the
    /// permission list, so the reach cannot leak to a handler that did not ask for it. Dispelling Edge is
    /// wired the same way and for the same reason; VengeanceAbility is the prior art for one handler
    /// carrying two hooks.
    ///
    /// THE TWO PATHS CANNOT DOUBLE-PROC A SINGLE HIT. They are reached from disjoint call sites: the weapon
    /// path only from Player.DamageTarget (whose own comment records that spells never route through it),
    /// the spell path only from SpellProjectile.OnCollideObject. A weapon swing reaches one, a spell
    /// projectile the other, neither reaches both.
    ///
    /// THE MISSILE GATE STAYS ON THE WEAPON PATH ONLY. Melee is excluded there by AppliesTo; on the spell
    /// path it is excluded structurally, because ISpellHitAbility never fires for a weapon swing at all -
    /// so the spell path deliberately applies no CombatType test (there is no meaningful one to apply, and
    /// DamageEvent.CombatType is not available there).
    /// </summary>
    public class PinningShotAbility : IOutgoingDamageAbility, ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.PinningShot,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 2,
            Name = "pinning_shot",
            DisplayName = "Pinning Shot",
            Description = "Your missile hits and your damaging spell projectiles have a 10/20/30% chance " +
                          "(by rank) to pin the target in place for 5 seconds. A pinned creature still " +
                          "attacks and casts. Each target is immune to being pinned again for 15 seconds. " +
                          "Higher Assess Creature multiplies the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // reprice reversal 2026-09-29: back to 1/rank (was 2/rank premium reprice 2026-09-13)
            Implemented = true,
            AffinitySkill = Skill.AssessCreature,
        };

        /// <summary>
        /// The "missile hits only" rule as a pure decision, so it is unit testable and so the gate cannot be
        /// lost if this ever moves to another dispatch site. IOutgoingDamageAbility covers melee AND missile
        /// (and multi-shot extra hits), so without this every melee swing would roll a pin.
        ///
        /// THIS IS THE WEAPON PATH'S GATE AND ONLY THE WEAPON PATH'S. The spell path (OnSpellHit) does not
        /// consult it: a spell projectile is not a weapon swing, so there is no CombatType to test and melee
        /// is already excluded by the fact that ISpellHitAbility never fires for one.
        /// </summary>
        public static bool AppliesTo(CombatType combatType) => combatType == CombatType.Missile;

        /// <summary>
        /// The proc chance at a given rank: the ability's OWN rank bonus (base + step*(rank-1)) SCALED by the
        /// Assess Creature affinity multiplier, with the added amount CAPPED. Pure for testability, matching
        /// PocketSandAbility.Chance / SundermarkAbility.Chance.
        ///
        /// <paramref name="affinityMultiplier"/> is what
        /// Player.GetClassAbilityAffinityMultiplier(Skill.AssessCreature) returns: a factor >= 1.0 that is
        /// EXACTLY 1.0 at zero effective Assess Creature, leaving the chance bit-identical to rank alone.
        /// Floored at 1.0 here as well, so a caller that hands over 0.0 - the neutral value of the OLD
        /// additive primitive, and an easy mistake because the two have identical shapes - degrades to
        /// rank-only rather than silently multiplying the whole bonus away.
        ///
        /// <paramref name="affinityCap"/> bounds the AMOUNT the affinity multiply ADDS
        /// (rankBonus * multiplier - rankBonus), never the rank ladder and never the factor itself
        /// (class_ability_affinity_chance_cap). It applies here because this IS a proc chance, the scope that
        /// tunable's own doc comment gives it. It matters more than usual on this entry: the ability is
        /// crowd control, and an uncapped linear rider would walk a 30% pin toward a permanent root on every
        /// shot - the immunity window bounds how OFTEN a pin can land, but only this clamp bounds how often
        /// the roll wins. A cap of 0 means uncapped. There is no equipment mod for this entry, so no gear
        /// term sits outside the clamp.
        /// </summary>
        public static double Chance(int rank, double chanceBase, double chanceStep, double affinityMultiplier, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = chanceBase + (rank - 1) * chanceStep;

            // The multiplier scales this ability's OWN rank bonus. What the cap bounds is the AMOUNT that
            // multiply ADDS, not the factor itself - the factor is a bare number like 1.51 and clamping it
            // against a tunable expressed in percentage points would be a unit error.
            var added = rankBonus * Math.Max(1.0, affinityMultiplier) - rankBonus;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            return Math.Max(0.0, rankBonus + added);
        }

        /// <summary>
        /// Rolls the pin on a landed MISSILE hit. Deliberately does not touch damageEvent.Damage - this hook
        /// is used here purely as a "landed weapon hit vs a monster" trigger.
        ///
        /// PvE is already guaranteed by the dispatch (Player.ApplyOutgoingDamageClassAbilities returns early
        /// when the target is a Player, and never runs on a miss), and the pet exclusion is re-stated in
        /// Creature.CanBeClassAbilityPinned so no site can pin a player's own summon.
        /// </summary>
        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (damageEvent == null)
                return;

            // WEAPON PATH ONLY: missile hits, never melee. See AppliesTo.
            if (!AppliesTo(damageEvent.CombatType))
                return;

            TryPin(attacker, rank, target);
        }

        /// <summary>
        /// The SPELL path: a landed damaging spell projectile of any school rolls the same pin.
        ///
        /// No school test and no CombatType test - the dispatch already guarantees this is a damaging spell
        /// projectile that landed on a monster, and a weapon swing can never arrive here. The projectile
        /// itself is unused: the pin is a status applied to the struck target, not a rider on the hit.
        /// </summary>
        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            TryPin(caster, rank, primaryTarget);
        }

        /// <summary>
        /// The proc body both dispatch paths share, so the roll, the tunable reads, the legality check and
        /// the notification cannot drift between them. Factored out when the spell path was added on
        /// 2026-09-13; every line of it came from the weapon path unchanged.
        /// </summary>
        private static void TryPin(Player attacker, int rank, Creature target)
        {
            if (rank <= 0 || attacker == null || target == null)
                return;

            // cheapest rejection first: a target already pinned, still immune, dead, or not a legal pin
            // target at all costs one field compare and no roll
            if (!target.CanBeClassAbilityPinned())
                return;

            // Assess Creature is MULTIPLICATIVE on Pinning Shot's own rank bonus (2026-09-12 overhaul), not
            // an additive rider beside it. At zero effective Assess Creature the factor is exactly 1.0 and
            // the proc chance is bit-identical to rank alone. class_ability_affinity_chance_cap, passed
            // below, bounds the AMOUNT the multiply adds rather than a raw skill quotient.
            var assessCreature = attacker.GetClassAbilityAffinityMultiplier(Skill.AssessCreature);

            var chance = Chance(rank,
                PropertyManager.GetDouble("class_ability_pinningshot_chance_base").Item,
                PropertyManager.GetDouble("class_ability_pinningshot_chance_step").Item,
                assessCreature,
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            var duration = PropertyManager.GetDouble("class_ability_pinningshot_duration_seconds").Item;
            var immunity = PropertyManager.GetDouble("class_ability_pinningshot_immunity_seconds").Item;

            if (!target.TryApplyClassAbilityPin(duration, immunity))
                return;

            attacker.OnClassAbilityPinApplied(target, duration);

            // The post-pin movement slow. Read AFTER the pin is confirmed applied, because the slow window is
            // derived from ClassAbilityPinnedUntil - there is no window to arm without a pin. Either tunable at
            // its disabling value (0 seconds, or a factor outside the open interval 0.0 to 1.0) makes
            // TryApplyClassAbilityMoveSlow refuse, and this whole block then costs two dictionary reads and no
            // behaviour change at all.
            var slowSeconds = PropertyManager.GetDouble("class_ability_pinningshot_slow_seconds").Item;
            var slowFactor = (float)PropertyManager.GetDouble("class_ability_pinningshot_slow_factor").Item;

            if (target.TryApplyClassAbilityMoveSlow(slowSeconds, slowFactor))
            {
                // EnchantDownGrey (0x8C) rather than the EnchantDownBlue the design note suggested: Blue is
                // already the fade visual for two other class abilities (Resonance and Spellsurge, in
                // Player_ClassAbilityBuffs.ClassAbilityBuffsHeartbeat), and a debuff marker shared with an
                // unrelated entry teaches players the wrong thing. Grey has no other hardcoded user anywhere in
                // ACE.Server, stays inside the Enchant family the rest of the class-ability visuals use, and
                // reads as leaden rather than as an elemental hit. Broadcast on the TARGET, which is where the
                // slow is.
                target.EnqueueBroadcast(new GameMessageScript(target.Guid, PlayScript.EnchantDownGrey));
            }
        }

        /// <summary>
        /// Reports the PROC CHANCE, not the 5 second duration: the duration is rank-invariant, so chance is
        /// the only number rank moves and the only one worth a live readout. Affinity is the CAPPED amount
        /// the Assess Creature multiply ADDS (clamped the same way Chance() clamps it), so the three
        /// displayed terms still sum to Effective.
        ///
        /// The null-conditional on the affinity read is for the readout unit tests, which call this with a
        /// null Player because Player's static initializer cannot run under the test host - and it falls
        /// back to 1.0, the NEUTRAL factor, never 0.0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_pinningshot_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_pinningshot_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var rankBonus = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            // NEUTRAL IS 1.0, NOT 0.0. The null-Player fallback must be the identity factor: 0.0 would not
            // omit the rider, it would multiply this ability's entire rank bonus away.
            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.AssessCreature) ?? 1.0;

            // The RAW (pre-clamp) amount the multiply adds. This is the quantity the affinity cap bounds,
            // so it is also the quantity the cap must be compared against.
            var rawAdded = Math.Max(0.0, rankBonus * multiplier - rankBonus);

            // Chance() applies the clamp itself; this only records whether it bit, for CapNote.
            var affinityCapBites = affinityCap > 0.0 && rawAdded > affinityCap;

            // The CAPPED added amount, so Affinity below matches what Chance() actually folds in.
            var clampedAdded = affinityCap > 0.0 ? Math.Min(rawAdded, affinityCap) : rawAdded;

            var chance = Chance(rank, chanceBase, chanceStep, multiplier, affinityCap);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rankBonus * 100.0,
                Affinity = clampedAdded * 100.0,
                Gear = 0.0,
                Effective = chance * 100.0,
                Unit = "%",
                Label = "pin proc",
                Per = null,
                CapNote = affinityCapBites ? "affinity cap" : null,
            };
        }
    }
}
