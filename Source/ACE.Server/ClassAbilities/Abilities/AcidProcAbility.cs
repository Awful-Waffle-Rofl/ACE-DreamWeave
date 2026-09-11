using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T3: landed weapon hits have a flat 25% chance (rank-invariant) to apply an acid damage-over-time
    /// effect to the target - the Poison Weapon upgrade (T3-builds-on-T1). Each tick equals the attacker's
    /// current Poison Weapon flat bonus (Alchemy rider included), so zero Poison Weapon ranks = zero DoT.
    /// The DoT refreshes rather than stacks.
    ///
    /// REWORKED 2026-08-17 (Berserker/Rogue balance pass). The proc chance no longer scales with rank or
    /// Item Tinkering; rank instead raises a poison-DAMAGE bonus (+25/50/75%, see
    /// <see cref="PoisonDamageBonus"/>), and Item Tinkering rides that bonus instead of the chance. The
    /// equipment-mod term on the chance itself is unchanged - EquipmentModId.AcidProc is still a MACHINERY
    /// proc-chance mod and still applies here.
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
            Tier = 3,
            Name = "acidproc",
            DisplayName = "Acid Proc",
            Description = "Your poisoned hits have a flat 25% chance to leave a caustic wound: a 3-tick acid " +
                          "damage-over-time equal to your Poison Weapon damage (needs Poison Weapon). Ranks " +
                          "raise all your poison damage by +25/50/75% (Poison Weapon hits and the acid ticks " +
                          "alike); higher Item Tinkering raises it further.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },
            Implemented = true,
            AffinitySkill = Skill.ItemTinkering, // rider computed in PoisonWeaponAbility.FlatBonus, not in this file
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
        /// The poison-damage MULTIPLIER bonus at a given rank: base + step*(rank-1), plus the Item Tinkering
        /// rider (moved here from Chance() in the 2026-08-17 rework), clamped by
        /// <paramref name="affinityCap"/>. Returns 0 for rank &lt;= 0. Pure for testability.
        ///
        /// Consumed as <c>flat *= 1 + PoisonDamageBonus(...)</c> inside PoisonWeaponAbility.FlatBonus, gated
        /// on the attacker owning Acid Proc rank &gt; 0 - so it reaches BOTH the Poison Weapon per-hit proc
        /// and every Acid Proc DoT tick, since both read that single shared flat.
        /// </summary>
        public static double PoisonDamageBonus(int rank, double bonusBase, double bonusStep, double itemTinkerFraction, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var rider = Math.Max(0.0, itemTinkerFraction);

            // Its own cap, deliberately NOT class_ability_affinity_chance_cap: that tunable's doc comment
            // pins it to PROC CHANCE riders and explicitly excludes damage-fraction riders (Spell AOE was
            // already the precedent). This bonus is a damage fraction, so it gets its own cap.
            if (affinityCap > 0.0)
                rider = Math.Min(rider, affinityCap);

            return Math.Max(0.0, bonusBase + (rank - 1) * bonusStep + rider);
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
            var tickAmount = (uint)Math.Round(PoisonWeaponAbility.FlatBonus(attacker, poisonRank) * tickFraction * (1.0 + caustic));
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
            };
        }
    }
}
