using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Spellsword T3: a landed hit has a 10/15/20% chance (by rank) to strip ONE beneficial enchantment
    /// from the target. Affinity: Arcane Lore, which is bound to this entry in SKILL-DISTRIBUTION.md
    /// section 3 and raises the chance.
    ///
    /// No weapon gate - unlike the three war procs, the design's tier table (SPELLSWORD-DESIGN.md section
    /// 3) states no weapon restriction for this entry, and it deals no damage, so it sits on no damage
    /// axis at all.
    ///
    /// BENEFICIAL ENCHANTMENTS ONLY, which is the whole point rather than a filter of convenience:
    /// stripping a HARMFUL enchantment off an enemy would remove the party's own debuffs - including this
    /// class's Sundermark and an Archmage's Elemental Rend - so "strip one enemy enchantment" can only mean
    /// one of the enemy's own buffs. Selection reuses the shipped dispel machinery's two invariants rather
    /// than inventing new ones (EnchantmentManager.SelectDispel / RemoveAllEnchantments):
    ///   - Duration == -1 marks an item-sourced enchantment (and vitae), which no dispel touches;
    ///   - SpellId above short.MaxValue is an item/cooldown pseudo-spell, likewise excluded.
    /// Removal itself goes through EnchantmentManager.Dispel(entry), the exact call the engine's own
    /// dispel spells use (WorldObject_Magic.HandleCastSpell_Dispel), so the registry write, the
    /// ChangesDetected flag and the client dispel message are all handled by shipped code.
    ///
    /// Uses IOutgoingDamageAbility purely as a "landed weapon hit vs a monster" trigger; it never touches
    /// the strike's damage.
    /// </summary>
    public class DispellingEdgeAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.DispellingEdge,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 3,
            Name = "dispellingedge",
            DisplayName = "Dispelling Edge",
            Description = "Your landed hits have a 10/15/20% chance (by rank) to strip one of the target's " +
                          "beneficial enchantments. Higher Arcane Lore increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        /// <summary>
        /// The dispel chance at a given rank: base + step*(rank-1) + the CAPPED Arcane Lore rider + the
        /// Dispelling Edge equipment mod. Pure for testability. Returns 0 for rank &lt;= 0.
        ///
        /// <paramref name="gearModChance"/> is the DISPELLING EDGE equipment mod
        /// (EquipmentModId.DispellingEdge), a MACHINERY mod and a further summand on the total.
        ///
        /// <paramref name="affinityCap"/> bounds the Arcane Lore rider ONLY
        /// (class_ability_affinity_chance_cap), bringing this helper into line with its three siblings
        /// (Spellblade, Runeblade, Sundermark). It has to exist for the same reason theirs do:
        /// GetClassAbilityScaling returns a RAW quotient (effectiveSkill / divisor) with no upper bound of
        /// its own, so the rider is linear in a skill value the server does not constrain - a character
        /// measured live on 2026-08-04 at Item Enchantment 5226 produced a rider of +209 percentage points
        /// on the identically-shaped war procs. A cap of 0 means uncapped, which reproduces the pre-cap
        /// behaviour exactly.
        ///
        /// The GEAR TERM SITS OUTSIDE THE CLAMP, which is why affinityCap comes after it rather than before:
        /// affinityCap exists to bound an unbounded SKILL quotient, while a gear term is already bounded by
        /// its registry MaxMagnitude and its StackCap. (The parameter ORDER here is gear-then-cap, matching
        /// AcidProcAbility.Chance, because the gear parameter shipped first and the enum-style append-only
        /// rule applies to a public signature too - every existing positional call site keeps compiling.)
        /// Both trailing parameters default to 0, so an unmodded, uncapped build is bit-identical.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double chanceStep, double arcaneLoreFraction, double gearModChance = 0.0, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var rider = Math.Max(0.0, arcaneLoreFraction);

            if (affinityCap > 0.0)
                rider = Math.Min(rider, affinityCap);

            return (float)Math.Max(0.0, chanceBase + (rank - 1) * chanceStep + rider + Math.Max(0.0, gearModChance));
        }

        /// <summary>
        /// Filters a target's top-layer beneficial enchantments down to the ones a dispel is allowed to
        /// remove, applying the same two exclusions the shipped dispel path applies. Pure/static and taking
        /// the list rather than the creature, so the selection rule is testable without a live registry.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> SelectDispellable(List<PropertiesEnchantmentRegistry> candidates)
        {
            if (candidates == null)
                return new List<PropertiesEnchantmentRegistry>();

            // Duration == -1: item-sourced enchantment or vitae - never dispellable.
            // SpellId > short.MaxValue: an item/cooldown pseudo-spell rather than a real cast.
            return candidates
                .Where(e => e != null && e.Duration != -1 && e.SpellId <= short.MaxValue)
                .ToList();
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || damageEvent == null)
                return;

            // Nothing to strip from a corpse.
            if (target.IsDead)
                return;

            var arcaneLore = attacker.GetClassAbilityScaling(Skill.ArcaneLore,
                PropertyManager.GetDouble("class_ability_dispellingedge_arcanelore_per_trained").Item,
                PropertyManager.GetDouble("class_ability_dispellingedge_arcanelore_per_spec").Item) * 0.01;

            // Dispelling Edge equipment mod (MACHINERY): +pp on this ability's own dispel roll. Ownership is
            // proven by the dispatch itself - only LEARNED IOutgoingDamageAbility handlers are in the hook
            // cache - so the plain read is correct here.
            var chance = Chance(rank,
                PropertyManager.GetDouble("class_ability_dispellingedge_chance_base").Item,
                PropertyManager.GetDouble("class_ability_dispellingedge_chance_step").Item,
                arcaneLore,
                attacker.GetEquippedModValue(EquipmentModId.DispellingEdge),
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            // Top layer per spell category, so a stack of the same buff counts once and the strongest is
            // what gets taken.
            var dispellable = SelectDispellable(target.EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Beneficial));

            if (dispellable.Count == 0)
                return;

            // Exactly one, chosen at random - the entry strips "one enemy enchantment", and picking at
            // random keeps it from becoming a deterministic buff-priority tool.
            var entry = dispellable[ThreadSafeRandom.Next(0, dispellable.Count - 1)];

            target.EnchantmentManager.Dispel(entry);
        }

        /// <summary>
        /// Mirrors the terms fed into Chance() above: base + step*(rank-1), plus the Arcane Lore rider, plus
        /// the DISPELLING EDGE mod as a third summand exactly as Chance() takes it. Affinity carries the RAW
        /// (pre-clamp) rider while Effective uses the clamped one, matching AcidProcAbility's pattern.
        ///
        /// The Arcane Lore rider IS clamped now (class_ability_affinity_chance_cap), so CapNote reports
        /// "affinity cap" exactly when the clamp actually reduced the rider on this call - i.e. it is
        /// biting, not merely configured.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_dispellingedge_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_dispellingedge_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            var rider = Math.Max(0.0, player.GetClassAbilityScaling(Skill.ArcaneLore,
                PropertyManager.GetDouble("class_ability_dispellingedge_arcanelore_per_trained").Item,
                PropertyManager.GetDouble("class_ability_dispellingedge_arcanelore_per_spec").Item) * 0.01);

            var clampedRider = affinityCap > 0.0 ? Math.Min(rider, affinityCap) : rider;
            var capBit = affinityCap > 0.0 && rider > affinityCap;

            // Rank-gated like skillChance above - a machinery mod reports nothing without its ability.
            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.DispellingEdge) ?? 0.0);

            var skill = skillChance * 100.0;
            var affinity = rider * 100.0;      // RAW (pre-clamp), so Total shows what the rider would be uncapped
            var gear = gearChance * 100.0;

            // Effective reflects the clamp - uses clampedRider, not the raw affinity term above.
            var total = skill + clampedRider * 100.0 + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "pp",
                Label = "dispel",
                Per = null,
                CapNote = capBit ? "affinity cap" : null,
            };
        }
    }
}
