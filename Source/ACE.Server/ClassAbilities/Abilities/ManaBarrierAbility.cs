using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archmage T2: a share of every incoming hit is paid out of Mana instead of Health - 10/17.5/25% by
    /// rank, plus a Magic Defense rider, capped by a server tunable. Implemented as a REFUND on the
    /// IIncomingDamageAbility hook: the dispatch in Player.TakeDamage runs after health has already been
    /// deducted but before the death check, so draining Mana and putting the same amount of Health back can
    /// legitimately avert a killing blow. That is intended - the mana pool is the second health bar.
    ///
    /// If Mana cannot pay the whole diverted share, it pays what it can (partial) and never goes negative.
    /// </summary>
    public class ManaBarrierAbility : IIncomingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ManaBarrier,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 2,
            Name = "manabarrier",
            DisplayName = "Mana Barrier",
            Description = "10/17.5/25% of the damage you take is paid out of your Mana instead of your Health " +
                          "(60% cap). Higher Magic Defense increases the share. If your Mana runs short it pays " +
                          "what it can.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        public void OnDamageTaken(Player defender, int rank, Creature attacker, DamageType damageType, uint damageTaken)
        {
            // a hit fully mitigated to 0 still reaches this hook (enemy contact) - nothing to divert
            if (damageTaken == 0)
                return;

            defender.ApplyManaBarrierDivert(attacker, damageTaken);
        }

        /// <summary>
        /// The fraction of incoming damage diverted to Mana at a given rank: base + (rank-1)*step, plus the
        /// Magic Defense rider, plus the Mana Barrier equipment mod, clamped to <paramref name="cap"/>.
        /// <paramref name="gearModFraction"/> is the Mana Barrier equipment mod, a STANDALONE mod: same axis,
        /// additive, NOT rank-gated, so at rank 0 this degenerates to just the (capped) gear share. Defaults
        /// to 0, which reproduces the pre-equipment-mod behavior exactly. Pure for testability.
        /// </summary>
        public static double DivertShare(int rank, double baseShare, double stepPerRank, double magicDefFraction, double cap, double gearModFraction = 0.0)
        {
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (rank <= 0 && gearBonus <= 0.0)
                return 0.0;

            var abilityShare = rank <= 0 ? 0.0 : baseShare + (rank - 1) * stepPerRank + Math.Max(0.0, magicDefFraction);

            return Math.Clamp(abilityShare + gearBonus, 0.0, Math.Max(0.0, cap));
        }

        /// <summary>
        /// How much Health is refunded and how much Mana it costs, given a diverted share and the Mana
        /// actually on hand. Pure for testability.
        /// </summary>
        public readonly struct Divert
        {
            public uint HealthRestored { get; init; }
            public uint ManaSpent { get; init; }
        }

        /// <summary>
        /// Resolves the diverted share against the Mana available: the full amount when it is affordable,
        /// otherwise as much as <paramref name="currentMana"/> can buy at <paramref name="manaPerHealth"/>
        /// mana per point of health. Never spends more Mana than is present, and never returns a negative
        /// or unaffordable result. Pure.
        /// </summary>
        public static Divert Resolve(uint damageTaken, double share, uint currentMana, double manaPerHealth)
        {
            if (damageTaken == 0 || share <= 0.0 || currentMana == 0)
                return default;

            var rate = manaPerHealth > 0.0 ? manaPerHealth : 1.0;

            var wantedHealth = (uint)Math.Floor(damageTaken * Math.Max(0.0, share));
            if (wantedHealth == 0)
                return default;

            var manaNeeded = (uint)Math.Ceiling(wantedHealth * rate);

            if (manaNeeded <= currentMana)
                return new Divert { HealthRestored = wantedHealth, ManaSpent = manaNeeded };

            // partial: buy as much health as the remaining mana covers
            var affordableHealth = (uint)Math.Floor(currentMana / rate);
            if (affordableHealth == 0)
                return default;

            var manaSpent = (uint)Math.Ceiling(affordableHealth * rate);
            if (manaSpent > currentMana)
                manaSpent = currentMana;

            return new Divert { HealthRestored = affordableHealth, ManaSpent = manaSpent };
        }

        /// <summary>
        /// Mirrors the terms fed into DivertShare above (x100 for display, "pp" since it's a share of
        /// damage) - the two must stay in step. Affinity carries the RAW (unclamped) Magic Defense rider
        /// like AcidProc's affinity-cap pattern; Effective applies the same Math.Clamp(abilityShare +
        /// gearBonus, 0.0, cap) DivertShare uses. CapNote is set only when the clamp actually reduced this
        /// call's value.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseShare = PropertyManager.GetDouble("class_ability_manabarrier_base").Item;
            var stepPerRank = PropertyManager.GetDouble("class_ability_manabarrier_step").Item;
            var cap = PropertyManager.GetDouble("class_ability_manabarrier_max_share").Item;

            var skillShare = rank <= 0 ? 0.0 : baseShare + (rank - 1) * stepPerRank;

            var rawAffinity = Math.Max(0.0, player.GetClassAbilityScaling(Skill.MagicDefense,
                PropertyManager.GetDouble("class_ability_manabarrier_magicdef_per_trained").Item,
                PropertyManager.GetDouble("class_ability_manabarrier_magicdef_per_spec").Item) * 0.01);

            var gearBonus = Math.Max(0.0, player.GetEquippedModValue(EquipmentModId.ManaBarrier));

            var abilityShare = skillShare + rawAffinity;
            var uncapped = abilityShare + gearBonus;
            var clamped = Math.Clamp(uncapped, 0.0, Math.Max(0.0, cap));

            var skill = skillShare * 100.0;
            var affinity = rawAffinity * 100.0;
            var gear = gearBonus * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = clamped * 100.0,
                Unit = "pp",
                Label = "dmg to mana",
                Per = null,
                CapNote = clamped < uncapped - 0.0000001 ? "barrier cap" : null,
            };
        }
    }
}
