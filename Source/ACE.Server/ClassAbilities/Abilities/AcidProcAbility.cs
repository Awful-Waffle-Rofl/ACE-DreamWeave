using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T3: landed weapon hits have a flat 30% chance (rank-invariant) to apply an acid damage-over-time
    /// effect to the target - the Poison Weapon upgrade (T3-builds-on-T1). Each tick equals the attacker's
    /// current Poison Weapon flat bonus (Alchemy rider included), so zero Poison Weapon ranks = zero DoT.
    /// The DoT refreshes rather than stacks.
    ///
    /// REWORKED 2026-08-17 (Berserker/Rogue balance pass). The proc chance no longer scales with rank or an
    /// affinity skill; rank instead raises a poison-DAMAGE bonus (+25/50/75%, see
    /// <see cref="PoisonDamageBonus"/>), and an affinity skill rides that bonus instead of the chance. The
    /// equipment-mod term on the chance itself is unchanged - EquipmentModId.AcidProc is still a MACHINERY
    /// proc-chance mod and still applies here.
    ///
    /// 2026-09-12 overhaul: the poison-damage bonus's affinity skill moved from Item Tinkering to Alchemy
    /// (Poison Weapon's own skill - Acid Proc is Poison Weapon's upgrade, so both riders now read the same
    /// legacy skill, though they still scale two different bonuses; see PoisonWeaponAbility.FlatBonus's doc
    /// comment for why the two reads must stay separate), and moved from an additive quotient rider to a
    /// multiplicative one.
    ///
    /// Uses the IOutgoingDamageAbility hook purely as a "landed weapon hit vs a monster" trigger (like
    /// Poison Weapon) - it does not modify the strike's damage; it schedules a separate DoT via
    /// Player.ApplyAcidProcDot. Delivered as unresisted DamageType.Base (matching Poison Weapon's ruling -
    /// each tick is "100% of the flat"; re-type to Acid later if it should be resistable).
    /// </summary>
    public class AcidProcAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.AcidProc,
            AbilityClass = ClassAbilityClass.Rogue,
            Tier = 2,
            Name = "acidproc",
            DisplayName = "Acid Proc",
            Description = "Your poisoned hits have a flat 30% chance to leave a caustic wound: a 3-tick acid " +
                          "damage-over-time equal to your Poison Weapon damage (needs Poison Weapon). Ranks " +
                          "raise all your poison damage by +25/50/75% (Poison Weapon hits and the acid ticks " +
                          "alike); higher Alchemy raises it further. Each wound keeps the scaling of the hit " +
                          "that opened it: damage ratings, the target's resistance, and critical bonuses.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },   // premium reprice 2026-09-13: 1/rank -> 2/rank
            Implemented = true,
            AffinitySkill = Skill.Alchemy, // rider computed in PoisonWeaponAbility.FlatBonus, not in this file
        };

        /// <summary>
        /// The proc chance: a flat, rank-invariant base plus the equipment-mod term. Pure for testability.
        /// Returns 0 for rank &lt;= 0 (unlearned).
        ///
        /// RANK-INVARIANT since the 2026-08-17 rework - rank now buys <see cref="PoisonDamageBonus"/>
        /// instead, and the Item Tinkering rider moved there with it. This carries no affinity term at all.
        ///
        /// <paramref name="gearModChance"/> is the Acid Proc equipment mod, a MACHINERY mod: it amplifies
        /// the ability's own roll and stays behind the same rank check. Defaults to 0, which reproduces the
        /// pre-equipment-mod behavior exactly.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            return (float)Math.Max(0.0, chanceBase + Math.Max(0.0, gearModChance));
        }

        /// <summary>
        /// The poison-damage MULTIPLIER bonus at a given rank: (base + step*(rank-1)) MULTIPLIED by the
        /// Alchemy affinity (2026-09-12 overhaul, replacing the additive Item Tinkering rider moved here in
        /// the 2026-08-17 rework), with the ADDED amount clamped by <paramref name="affinityCap"/>. Returns 0
        /// for rank &lt;= 0. Pure for testability.
        ///
        /// <paramref name="affinityMultiplier"/> is what <c>Player.GetClassAbilityAffinityMultiplier</c>
        /// returns - a FACTOR &gt;= 1.0 multiplying the rank-scaled base, not a raw additive quotient. What
        /// is capped is the AMOUNT that multiply ADDS (rankBonus * multiplier - rankBonus), deliberately NOT
        /// class_ability_affinity_chance_cap: that tunable's doc comment pins it to PROC CHANCE riders and
        /// explicitly excludes damage-fraction riders (Spell AOE was already the precedent). This bonus is a
        /// damage fraction, so it gets its own cap (class_ability_acidproc_damage_affinity_cap).
        ///
        /// Consumed as <c>flat *= 1 + PoisonDamageBonus(...)</c> inside PoisonWeaponAbility.FlatBonus, gated
        /// on the attacker owning Acid Proc rank &gt; 0 - so it reaches BOTH the Poison Weapon per-hit proc
        /// and every Acid Proc DoT tick, since both read that single shared flat.
        /// </summary>
        public static double PoisonDamageBonus(int rank, double bonusBase, double bonusStep, double affinityMultiplier, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = bonusBase + (rank - 1) * bonusStep;

            var added = rankBonus * affinityMultiplier - rankBonus;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            added = Math.Max(0.0, added);

            return Math.Max(0.0, rankBonus + added);
        }

        /// <summary>
        /// The integer acid-tick amount one proc opens the wound at: the attacker's Poison Weapon flat times
        /// the tick fraction times the Caustic gear term, MULTIPLIED by the producing strike's
        /// <see cref="DamageEvent.ProcDamageMultiplier"/> (crit multiplier x the attacker's damage rating x
        /// the target's damage resistance rating, crit damage resistance included on a crit), rounded once.
        /// Pure for testability - <paramref name="poisonWeaponFlat"/> is
        /// <see cref="PoisonWeaponAbility.FlatBonus"/>'s already-computed result, not recomputed here.
        ///
        /// SNAPSHOTTED WHEN THE WOUND OPENS: the result is what ApplyAcidProcDot stores as TickAmount, so
        /// every tick of that wound carries the multiplier of the strike that opened it, even if the target's
        /// resistance or the attacker's rating changes mid-wound.
        ///
        /// <paramref name="strikeMultiplier"/> may be below 1.0 (a resistant target) and that reduction is
        /// intended, so it is floored at 0 only, to keep a negative input from wrapping the uint. It used to
        /// be floored at 1.0, when the only input was the crit multiplier.
        ///
        /// Does NOT multiply <see cref="PoisonWeaponAbility.FlatBonus"/> itself - that value is shared with
        /// the Poison Weapon per-hit proc (which applies its OWN strike multiplier from ITS OWN triggering
        /// hit), so multiplying it here would double-apply the multiplier across two unrelated procs.
        ///
        /// Refresh semantic is deliberately unchanged by this multiplier: ApplyAcidProcDot overwrites
        /// TickAmount on every (re)proc rather than stacking, so a later NON-crit proc resets an
        /// already-open crit wound back down to the plain amount for its remaining ticks. Accepted - a wound
        /// is one running effect with one current tick size, not a ledger of past procs.
        /// </summary>
        public static uint ComputeTickAmount(double poisonWeaponFlat, double tickFraction, double caustic, double strikeMultiplier = 1.0)
        {
            var m = Math.Max(0.0, strikeMultiplier);

            return (uint)Math.Round(poisonWeaponFlat * tickFraction * (1.0 + caustic) * m);
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            // weapon hits only (shares Poison Weapon's trigger), and the DoT needs Poison Weapon ranks
            if (damageEvent.Weapon == null || attacker == null || target == null)
                return;

            if (!attacker.TryGetClassAbility(ClassAbilityId.PoisonWeapon, out var poisonRank))
                return;

            var tickFraction = PropertyManager.GetDouble("class_ability_acidproc_dot_fraction").Item;

            // Caustic equipment mod (MACHINERY): scales each acid tick, inside this ability's own tick
            // computation. Both this and the proc-chance mod below are unreachable without Acid Proc, which
            // is the machinery contract - there is no DoT to amplify otherwise.
            var caustic = attacker.GetEquippedModValue(EquipmentModId.Caustic);

            // PoisonWeaponAbility.FlatBonus already folds in this ability's poison-damage bonus (gated on
            // owning Acid Proc), so the tick amount below inherits it automatically without restating it.
            var tickAmount = ComputeTickAmount(PoisonWeaponAbility.FlatBonus(attacker, poisonRank), tickFraction, caustic, damageEvent.ProcDamageMultiplier);
            if (tickAmount == 0)
                return;

            var chance = Chance(rank,
                PropertyManager.GetDouble("class_ability_acidproc_chance_base").Item,
                attacker.GetEquippedModValue(EquipmentModId.AcidProc));

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            attacker.ApplyAcidProcDot(target, tickAmount);
        }

        /// <summary>
        /// Mirrors the proc-chance term fed into Chance() in ModifyOutgoingDamage above - the two must
        /// stay in step. Skill/Gear are percentage POINTS (x100 of the fraction Chance() works in).
        /// Affinity is 0 here - the chance carries no rider since the 2026-08-17 rework (see
        /// PoisonWeapon's own readout for where the Item Tinkering rider now shows up, on the damage side).
        ///
        /// SECONDARY: the poison-damage bonus PoisonDamageBonus grants (2026-09-12 overhaul) is the second
        /// player-meaningful magnitude rank buys here - the proc chance is flat/rank-invariant, so without
        /// this the rank number on the header would have nothing live backing it up at all. Mirrors
        /// PoisonDamageBonus's own terms exactly (x100 for display); Gear is 0 - PoisonDamageBonus carries
        /// no equipment-mod term of its own (Caustic scales the DoT tick amount, a different computation,
        /// not this bonus). CapNote is set only when the affinity cap actually reduced the added amount.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_acidproc_chance_base").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase;

            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.AcidProc) ?? 0.0);

            var skill = skillChance * 100.0;
            var gear = gearChance * 100.0;
            var effective = skill + gear;

            var damageBase = PropertyManager.GetDouble("class_ability_acidproc_damage_base").Item;
            var damageStep = PropertyManager.GetDouble("class_ability_acidproc_damage_step").Item;
            var damageAffinityCap = PropertyManager.GetDouble("class_ability_acidproc_damage_affinity_cap").Item;

            var damageRankBonus = rank <= 0 ? 0.0 : damageBase + (rank - 1) * damageStep;
            var damageAffinityMultiplier = player?.GetClassAbilityAffinityMultiplier(Skill.Alchemy) ?? 1.0;

            var rawDamageAdded = Math.Max(0.0, damageRankBonus * damageAffinityMultiplier - damageRankBonus);
            var damageCapBites = damageAffinityCap > 0.0 && rawDamageAdded > damageAffinityCap;
            var clampedDamageAdded = damageAffinityCap > 0.0 ? Math.Min(rawDamageAdded, damageAffinityCap) : rawDamageAdded;

            var poisonDamageSecondary = new ClassAbilityReadout
            {
                HasValue = true,
                Skill = damageRankBonus * 100.0,
                Affinity = clampedDamageAdded * 100.0,
                Gear = 0.0,
                Effective = (damageRankBonus + clampedDamageAdded) * 100.0,
                Unit = "%",
                Prefix = "+",
                Label = "poison dmg",
                Per = null,
                CapNote = damageCapBites ? "affinity cap" : null,
            };

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = effective,
                Unit = "%",
                Label = "proc",
                Per = null,
                CapNote = null,
                Secondary = new List<ClassAbilityReadout> { poisonDamageSecondary },
            };
        }
    }
}
