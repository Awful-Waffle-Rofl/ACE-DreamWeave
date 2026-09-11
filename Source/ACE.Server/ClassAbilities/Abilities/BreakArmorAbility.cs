using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T2 (replaces the retired Blood Fury, 2026-08-17): a landed MELEE hit has a flat 15% chance
    /// to shatter the target's guard, applying Imperil III / V / VII (by rank) - a Life Magic BodyArmorValue
    /// debuff, the same family Malediction already amplifies (MaledictionAbility.AffectsSpell). Patterned on
    /// SundermarkAbility: applied directly through CreateEnchantment (no resist check on that path) plus a
    /// GameMessageScript broadcast for the target's visual, since CreateEnchantment does not play one.
    ///
    /// THE CHANCE IS RANK-INVARIANT, on purpose - rank buys the Imperil RUNG (III/V/VII), not the proc odds,
    /// matching the shape the three Spellsword war procs and Break Armor's own sibling war/rogue procs use
    /// (Spellblade/Runeblade/Spellstorm are rank-invariant too; only Sundermark and Acid Proc's OLD shape
    /// carried a rank term, and Acid Proc lost its rank term in this same balance pass).
    ///
    /// Weapon Tinkering affinity rider (in-class via Berserker Training), clamped by
    /// class_ability_affinity_chance_cap exactly like Sundermark/Acid Proc/Elemental Rend. The Break Armor
    /// equipment mod (EquipmentModId.BreakArmor, formerly Blood Fury's row, repurposed in Phase 0) is a
    /// MACHINERY proc-chance mod added OUTSIDE that clamp, same placement as every other proc-chance gear
    /// term in this catalog.
    /// </summary>
    public class BreakArmorAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.BreakArmor,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 2,
            Name = "breakarmor",
            DisplayName = "Break Armor",
            Description = "Your melee hits have a 15% chance to shatter the target's guard, casting Imperil " +
                          "III / V / VII on it by rank. It cannot be resisted. Higher Weapon Tinkering raises " +
                          "the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },
            Implemented = true,
            AffinitySkill = Skill.WeaponTinkering,
        };

        /// <summary>
        /// The proc chance at a given rank: a flat base (rank-gated only, no per-rank step) plus the Weapon
        /// Tinkering rider, clamped, plus the machinery gear term outside the clamp. Pure for testability.
        /// Returns 0 for rank &lt;= 0.
        ///
        /// <paramref name="gearModChance"/> is the BREAK ARMOR equipment mod (EquipmentModId.BreakArmor), a
        /// MACHINERY mod added OUTSIDE the affinity clamp - see SundermarkAbility.Chance for why that
        /// placement is load-bearing. Last and defaulted to 0, so an unmodded build is bit-identical.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double weaponTinkerFraction, double affinityCap = 0.0, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var rider = Math.Max(0.0, weaponTinkerFraction);

            if (affinityCap > 0.0)
                rider = Math.Min(rider, affinityCap);

            return (float)Math.Max(0.0, chanceBase + rider + Math.Max(0.0, gearModChance));
        }

        /// <summary>
        /// The Imperil rung this rank casts: III at rank 1, V at rank 2, VII at rank 3. Undefined
        /// (SpellId.Undef) for any other rank, which callers must treat as "apply nothing".
        /// </summary>
        public static SpellId ImperilFor(int rank)
        {
            switch (rank)
            {
                case 1: return SpellId.ImperilOther3;
                case 2: return SpellId.ImperilOther5;
                case 3: return SpellId.ImperilOther7;
                default: return SpellId.Undef;
            }
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || damageEvent == null)
                return;

            // MELEE ONLY - the old Blood Fury melee gate, carried forward. Missile/magic hits never proc it.
            if (damageEvent.CombatType != CombatType.Melee)
                return;

            // A dead target cannot be usefully debuffed (the strike that triggered this frequently kills).
            if (target.IsDead)
                return;

            var weaponTinker = attacker.GetClassAbilityScaling(Skill.WeaponTinkering,
                PropertyManager.GetDouble("class_ability_breakarmor_weptink_per_trained").Item,
                PropertyManager.GetDouble("class_ability_breakarmor_weptink_per_spec").Item) * 0.01;

            var chance = Chance(rank,
                PropertyManager.GetDouble("class_ability_breakarmor_chance").Item,
                weaponTinker,
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item,
                attacker.GetEquippedModValue(EquipmentModId.BreakArmor));

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            var imperilId = ImperilFor(rank);
            if (imperilId == SpellId.Undef)
                return;

            // Applied through the normal enchantment registry (obeys standard stacking - refreshes an equal
            // or weaker Imperil, never stacks past a stronger one), and with NO resist check, matching
            // Sundermark's path. Malediction amplifies this automatically via EnchantmentManager.BuildEntry,
            // since it reads the enchantment's own stat-mod shape rather than an id list.
            var imperilSpell = new Spell(imperilId);
            attacker.CreateEnchantment(target, attacker, damageEvent.Weapon, imperilSpell);

            // AND PLAY THE SPELL'S OWN VISUAL, because CreateEnchantment does not - see Sundermark's
            // identical comment (reported live 2026-08-04 for that proc; same gap applies here).
            target.EnqueueBroadcast(new GameMessageScript(target.Guid, imperilSpell.TargetEffect, imperilSpell.Formula.Scale));
        }

        /// <summary>
        /// Mirrors the proc-chance terms fed into Chance() above. Affinity carries the RAW (pre-clamp)
        /// Weapon Tinkering rider, Effective uses the clamped one. Gear is the BREAK ARMOR mod, added to
        /// Effective OUTSIDE the clamp exactly as Chance() adds it.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_breakarmor_chance").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase;

            var rawRider = Math.Max(0.0, player.GetClassAbilityScaling(Skill.WeaponTinkering,
                PropertyManager.GetDouble("class_ability_breakarmor_weptink_per_trained").Item,
                PropertyManager.GetDouble("class_ability_breakarmor_weptink_per_spec").Item) * 0.01);

            var clampedRider = affinityCap > 0.0 ? Math.Min(rawRider, affinityCap) : rawRider;
            var capBit = affinityCap > 0.0 && rawRider > affinityCap;

            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.BreakArmor) ?? 0.0);

            var skill = skillChance * 100.0;
            var affinity = rawRider * 100.0;
            var gear = gearChance * 100.0;
            var effective = skill + clampedRider * 100.0 + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = effective,
                Unit = "%",
                Label = "proc",
                Per = null,
                CapNote = capBit ? "affinity cap" : null,
            };
        }
    }
}
