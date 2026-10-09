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
    /// Spellsword T3: a landed hit has a 10/16/22% chance (by rank) to strip ONE beneficial enchantment
    /// from the target - every caster's layer of it (see SelectAllLayers). Affinity: Item Enchantment (2026-09-12 overhaul, replacing Arcane Lore - the strip
    /// is fundamentally an enchantment-manipulation trick, so the affinity now matches the skill that
    /// governs enchantments rather than lore), which multiplies the chance.
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
    ///
    /// IT ALSO RIDES ISpellHitAbility as of 2026-09-13, so a landed damaging spell projectile can strip too
    /// - which suits a class that fights with a sword in one hand and a spell in the other. THE SECOND
    /// INTERFACE IS THE OPT-IN. Spell damage does not reach ApplyOutgoingDamageClassAbilities at all (its
    /// only call site is Player.DamageTarget, the weapon path; SpellProjectile.DamageTarget is a different
    /// method on a different type and never calls it), so the reach could NOT be granted by widening that
    /// dispatch without handing it to all 23 handlers on it, none of which may have it. Declaring the
    /// interface here makes the C# type list the permission list. Pinning Shot is wired the same way;
    /// VengeanceAbility is the prior art for one handler carrying two hooks.
    ///
    /// THE TWO PATHS CANNOT DOUBLE-PROC A SINGLE HIT: they are reached from disjoint call sites (the weapon
    /// path only from Player.DamageTarget, the spell path only from SpellProjectile.OnCollideObject), so a
    /// weapon swing reaches one, a spell projectile the other, and neither reaches both.
    /// </summary>
    public class DispellingEdgeAbility : IOutgoingDamageAbility, ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.DispellingEdge,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 3,
            Name = "dispellingedge",
            DisplayName = "Dispelling Edge",
            Description = "Your landed hits and your damaging spell projectiles have a 10/16/22% chance " +
                          "(by rank) to strip one of the target's beneficial enchantments. Higher Item " +
                          "Enchantment increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },   // premium reprice 2026-09-13: 1/rank -> 2/rank
            Implemented = true,
            AffinitySkill = Skill.ItemEnchantment,
        };

        /// <summary>
        /// The dispel chance at a given rank: (base + step*(rank-1)) MULTIPLIED by the Item Enchantment
        /// affinity (2026-09-12 overhaul, replacing the additive Arcane Lore rider), plus the Dispelling Edge
        /// equipment mod. Pure for testability. Returns 0 for rank &lt;= 0.
        ///
        /// <paramref name="affinityMultiplier"/> is what <c>Player.GetClassAbilityAffinityMultiplier</c>
        /// returns - a FACTOR &gt;= 1.0 multiplying the rank-scaled base, not a raw additive quotient. What
        /// is reported/capped is the AMOUNT that multiply ADDS (rankBonus * multiplier - rankBonus).
        ///
        /// <paramref name="gearModChance"/> is the DISPELLING EDGE equipment mod
        /// (EquipmentModId.DispellingEdge), a MACHINERY mod and a further summand on the total.
        ///
        /// <paramref name="affinityCap"/> bounds the added amount ONLY (class_ability_affinity_chance_cap),
        /// bringing this helper into line with its siblings (Spellblade, Runeblade, Sundermark). It has to
        /// exist for the same reason theirs do: GetClassAbilityAffinityMultiplier has no ceiling of its own,
        /// so an uncapped multiply is linear in a skill value the server does not constrain - a character
        /// measured live on 2026-08-04 at Item Enchantment 5226 produced a rider of +209 percentage points
        /// on the identically-shaped war procs. A cap of 0 means uncapped.
        ///
        /// The GEAR TERM SITS OUTSIDE THE CLAMP, which is why affinityCap comes after it rather than before:
        /// affinityCap exists to bound an unbounded SKILL multiply, while a gear term is already bounded by
        /// its registry MaxMagnitude and its StackCap. (The parameter ORDER here is gear-then-cap, matching
        /// AcidProcAbility.Chance, because the gear parameter shipped first and the enum-style append-only
        /// rule applies to a public signature too - every existing positional call site keeps compiling.)
        /// Both trailing parameters default to 0, so an unmodded, uncapped build is bit-identical.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double chanceStep, double affinityMultiplier, double gearModChance = 0.0, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var rankBonus = chanceBase + (rank - 1) * chanceStep;

            var added = rankBonus * affinityMultiplier - rankBonus;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            added = Math.Max(0.0, added);

            return (float)Math.Max(0.0, rankBonus + added + Math.Max(0.0, gearModChance));
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

        /// <summary>
        /// Every layer a strip of <paramref name="chosen"/> removes: all dispellable entries in the chosen
        /// entry's spell category, not just the chosen one. The registry keeps a separate layer per CASTER
        /// (EnchantmentManager.Add refreshes only a same-caster entry), so removing one layer merely exposes
        /// the next - two monsters casting the same buff, or two archers casting Gelidite's Gift on a player,
        /// left the effect fully in force after a "successful" strip. Repo-owner ruling 2026-09-24: a strip
        /// takes every copy.
        ///
        /// <paramref name="categoryEntries"/> is EnchantmentManager.GetEnchantments(chosen.SpellCategory),
        /// every layer, not the top layer. The shipped exclusions still apply per layer, so an item-sourced
        /// copy of the same category survives. The chosen entry is always included, so a strip never does
        /// less than it did before this ruling.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> SelectAllLayers(PropertiesEnchantmentRegistry chosen, List<PropertiesEnchantmentRegistry> categoryEntries)
        {
            if (chosen == null)
                return new List<PropertiesEnchantmentRegistry>();

            var layers = SelectDispellable(categoryEntries)
                .Where(e => e.SpellCategory == chosen.SpellCategory)
                .ToList();

            if (!layers.Any(e => e.SpellId == chosen.SpellId && e.CasterObjectId == chosen.CasterObjectId))
                layers.Add(chosen);

            return layers;
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (damageEvent == null)
                return;

            TryDispel(attacker, rank, target);
        }

        /// <summary>
        /// The SPELL path: a landed damaging spell projectile of any school rolls the same strip. No school
        /// test - the dispatch already guarantees a damaging spell projectile landed on a monster. The
        /// projectile itself is unused; the strip acts on the struck target, not on the hit.
        /// </summary>
        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            TryDispel(caster, rank, primaryTarget);
        }

        /// <summary>
        /// The proc body both dispatch paths share, so the roll, the tunable reads and the selection rule
        /// cannot drift between them. Factored out when the spell path was added on 2026-09-13; every line
        /// of it came from the weapon path unchanged.
        /// </summary>
        private static void TryDispel(Player attacker, int rank, Creature target)
        {
            if (attacker == null || target == null)
                return;

            // Nothing to strip from a corpse.
            if (target.IsDead)
                return;

            var itemEnchantAffinity = attacker.GetClassAbilityAffinityMultiplier(Skill.ItemEnchantment);

            // Dispelling Edge equipment mod (MACHINERY): +pp on this ability's own dispel roll. Ownership is
            // proven by the dispatch itself - only LEARNED IOutgoingDamageAbility handlers are in the hook
            // cache - so the plain read is correct here.
            var chance = Chance(rank,
                PropertyManager.GetDouble("class_ability_dispellingedge_chance_base").Item,
                PropertyManager.GetDouble("class_ability_dispellingedge_chance_step").Item,
                itemEnchantAffinity,
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

            // every caster's copy of that buff, or the next layer simply takes over (see SelectAllLayers).
            // No per-layer beneficial re-filter: retail SpellCategory ids never mix a Raising/Protection spell
            // with its Lowering/Vulnerability twin, so a beneficial entry's category holds only buffs.
            target.EnchantmentManager.Dispel(SelectAllLayers(entry, target.EnchantmentManager.GetEnchantments(entry.SpellCategory)));
        }

        /// <summary>
        /// Mirrors the terms fed into Chance() above: (base + step*(rank-1)) multiplied by the Item
        /// Enchantment affinity, plus the DISPELLING EDGE mod as a further summand exactly as Chance() takes
        /// it. Affinity is the CAPPED added amount (rankBonus * multiplier - rankBonus, then clamped), so the
        /// three displayed terms still sum to Effective.
        ///
        /// CapNote reports "affinity cap" exactly when the clamp actually reduced the added amount on this
        /// call - i.e. it is biting, not merely configured.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_dispellingedge_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_dispellingedge_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            var affinityMultiplier = player.GetClassAbilityAffinityMultiplier(Skill.ItemEnchantment);
            var rawAdded = skillChance * affinityMultiplier - skillChance;

            var clampedAdded = affinityCap > 0.0 ? Math.Min(rawAdded, affinityCap) : rawAdded;
            clampedAdded = Math.Max(0.0, clampedAdded);
            var capBit = affinityCap > 0.0 && rawAdded > affinityCap;

            // Rank-gated like skillChance above - a machinery mod reports nothing without its ability.
            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.DispellingEdge) ?? 0.0);

            var skill = skillChance * 100.0;
            var affinity = clampedAdded * 100.0;
            var gear = gearChance * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "dispel",
                Per = null,
                CapNote = capBit ? "affinity cap" : null,
            };
        }
    }
}
