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
    /// Rogue T3: landed weapon hits have an 8/14/20% chance (by rank) to apply an acid damage-over-time
    /// effect to the target - the Poison Weapon upgrade (T3-builds-on-T1). Each tick equals the attacker's
    /// current Poison Weapon flat bonus (Alchemy rider included), so zero Poison Weapon ranks = zero DoT.
    /// The DoT refreshes rather than stacks. Proc chance scales with Item Tinkering (etching acids are
    /// workshop tools).
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
            Description = "Your weapon hits have an 8/14/20% chance (by rank) to apply an acid damage-over-time " +
                          "effect equal to your Poison Weapon damage (needs Poison Weapon). Higher Item Tinkering " +
                          "increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        /// <summary>
        /// The proc chance at a given rank: base + step*(rank-1) + the Item Tinkering rider + the
        /// equipment-mod term. Pure for testability. Returns 0 for rank &lt;= 0.
        ///
        /// <paramref name="gearModChance"/> is the Acid Proc equipment mod, a MACHINERY mod: it amplifies
        /// the ability's own roll and stays behind the same rank check. Defaults to 0, which reproduces the
        /// pre-equipment-mod behavior exactly.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double chanceStep, double itemTinkerFraction, double gearModChance = 0.0, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var rider = Math.Max(0.0, itemTinkerFraction);

            // THE AFFINITY RIDER IS CAPPED (class_ability_affinity_chance_cap, added 2026-08-04).
            // GetClassAbilityScaling returns a raw quotient (skill / divisor) with no bound of its own, so
            // this rider is linear in a skill value the server does not constrain. Measured live on a
            // character with Item Tinkering 5226: the rider came to +209 percentage points and the proc
            // fired on literally every swing. The bug was found on the Spellsword war procs, which share
            // this exact shape; this ability had it too and was equally saturated.
            //
            // The GEAR MOD is deliberately NOT capped by this: it is a bounded equipment roll rather than
            // an unbounded skill quotient, and it is the machinery mod that only exists for owners of this
            // ability. A cap of 0 means uncapped, which preserves the pre-2026-08-04 behavior exactly.
            if (affinityCap > 0.0)
                rider = Math.Min(rider, affinityCap);

            return (float)Math.Max(0.0, chanceBase + (rank - 1) * chanceStep + rider + Math.Max(0.0, gearModChance));
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

            var tickAmount = (uint)Math.Round(PoisonWeaponAbility.FlatBonus(attacker, poisonRank) * tickFraction * (1.0 + caustic));
            if (tickAmount == 0)
                return;

            var itemTinker = attacker.GetClassAbilityScaling(Skill.ItemTinkering,
                PropertyManager.GetDouble("class_ability_acidproc_itemtink_per_trained").Item,
                PropertyManager.GetDouble("class_ability_acidproc_itemtink_per_spec").Item) * 0.01;

            var chance = Chance(rank,
                PropertyManager.GetDouble("class_ability_acidproc_chance_base").Item,
                PropertyManager.GetDouble("class_ability_acidproc_chance_step").Item,
                itemTinker,
                attacker.GetEquippedModValue(EquipmentModId.AcidProc),
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            attacker.ApplyAcidProcDot(target, tickAmount);
        }

        /// <summary>
        /// Mirrors the proc-chance terms fed into Chance() in ModifyOutgoingDamage above - the two must
        /// stay in step. Skill/Affinity/Gear are percentage POINTS (x100 of the fraction Chance() works
        /// in). The affinity rider is clamped by class_ability_affinity_chance_cap the same way Chance()
        /// clamps it; CapNote is set only when the raw (unclamped) rider actually exceeds the cap this
        /// call, i.e. the clamp is currently biting - not merely configured.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_acidproc_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_acidproc_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            var rawRider = Math.Max(0.0, player.GetClassAbilityScaling(Skill.ItemTinkering,
                PropertyManager.GetDouble("class_ability_acidproc_itemtink_per_trained").Item,
                PropertyManager.GetDouble("class_ability_acidproc_itemtink_per_spec").Item) * 0.01);

            var clampedRider = affinityCap > 0.0 ? Math.Min(rawRider, affinityCap) : rawRider;
            var capBit = affinityCap > 0.0 && rawRider > affinityCap;

            var gearChance = Math.Max(0.0, player.GetEquippedModValue(EquipmentModId.AcidProc));

            var skill = skillChance * 100.0;
            var affinity = rawRider * 100.0;   // RAW (pre-clamp), so Total shows what the rider would be uncapped
            var gear = gearChance * 100.0;

            // Effective reflects the clamp - uses clampedRider, not the raw affinity term above.
            var effective = skill + clampedRider * 100.0 + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = effective,
                Unit = "pp",
                Label = "proc",
                Per = null,
                CapNote = capBit ? "affinity cap" : null,
            };
        }
    }
}
