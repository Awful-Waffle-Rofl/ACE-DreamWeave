using System;
using System.Collections.Generic;

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
    /// Archmage T3: a landed war spell has an 8/14/20% chance (by rank) to inflict the matching
    /// element's Life-Magic Vulnerability on the struck target, at a level equivalent to the war spell
    /// that landed (a level-8 fire bolt applies the level-8 "Incantation of Fire Vulnerability Other"). The proc chance scales with Life
    /// Magic (vulns ARE Life Magic) - the one scaling source living outside every Training bundle, and
    /// the scaling is Trained-gated (an untrained Life Magic contributes no rider, but the base chance
    /// still applies).
    ///
    /// Applied through the normal enchantment registry (WorldObject.CreateEnchantment), so it obeys the
    /// standard stacking rules - it won't stack past a stronger cast Vulnerability and refreshes an
    /// equal/weaker one. Because it spawns no projectile it carries no cascade risk, so unlike Spell AOE
    /// and Echo Cast it deliberately fires even on class-ability-spawned copies: a Spell AOE radiated copy
    /// or an Echo Cast recast can proc it.
    /// </summary>
    public class ElementalRendAbility : ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ElementalRend,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 3,
            Name = "elementalrend",
            DisplayName = "Elemental Rend",
            Description = "Your war spells have an 8/14/20% chance (by rank) to inflict the matching " +
                          "element's Vulnerability on the target at the spell's level. Higher Life Magic " +
                          "increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        // Per-element Life-Magic Vulnerability progressions (the "...Other" line). The SpellId enum values
        // are not contiguous per family, so the levels are listed explicitly rather than computed.
        //
        // The ladder actually runs levels 1-8, not 1-6. Level 7 breaks the roman-numeral naming pattern -
        // it's the "X's Gift" family (e.g. "Swordsman's Gift" for Slash), with no "VII" in its display
        // name - and level 8 is "Incantation of X Vulnerability Other". A name-based search for a
        // roman-numeral "level 7" will find nothing; that absence is NOT evidence the ladder caps at 6,
        // and must not be used to revert this table back to 6 entries per element. The effective clamp is
        // progression.Length == 8, computed automatically by GetVulnerabilitySpell's Math.Clamp below -
        // there is no hardcoded cap to update if the client ever adds a level 9.
        private static readonly IReadOnlyDictionary<DamageType, SpellId[]> VulnByElement = new Dictionary<DamageType, SpellId[]>
        {
            [DamageType.Slash] = new[]
            {
                SpellId.BladeVulnerabilityOther1, SpellId.BladeVulnerabilityOther2, SpellId.BladeVulnerabilityOther3,
                SpellId.BladeVulnerabilityOther4, SpellId.BladeVulnerabilityOther5, SpellId.BladeVulnerabilityOther6,
                SpellId.BladeVulnerabilityOther7, SpellId.BladeVulnerabilityOther8,
            },
            [DamageType.Pierce] = new[]
            {
                SpellId.PiercingVulnerabilityOther1, SpellId.PiercingVulnerabilityOther2, SpellId.PiercingVulnerabilityOther3,
                SpellId.PiercingVulnerabilityOther4, SpellId.PiercingVulnerabilityOther5, SpellId.PiercingVulnerabilityOther6,
                SpellId.PiercingVulnerabilityOther7, SpellId.PiercingVulnerabilityOther8,
            },
            [DamageType.Bludgeon] = new[]
            {
                SpellId.BludgeonVulnerabilityOther1, SpellId.BludgeonVulnerabilityOther2, SpellId.BludgeonVulnerabilityOther3,
                SpellId.BludgeonVulnerabilityOther4, SpellId.BludgeonVulnerabilityOther5, SpellId.BludgeonVulnerabilityOther6,
                SpellId.BludgeonVulnerabilityOther7, SpellId.BludgeonVulnerabilityOther8,
            },
            [DamageType.Cold] = new[]
            {
                SpellId.ColdVulnerabilityOther1, SpellId.ColdVulnerabilityOther2, SpellId.ColdVulnerabilityOther3,
                SpellId.ColdVulnerabilityOther4, SpellId.ColdVulnerabilityOther5, SpellId.ColdVulnerabilityOther6,
                SpellId.ColdVulnerabilityOther7, SpellId.ColdVulnerabilityOther8,
            },
            [DamageType.Fire] = new[]
            {
                SpellId.FireVulnerabilityOther1, SpellId.FireVulnerabilityOther2, SpellId.FireVulnerabilityOther3,
                SpellId.FireVulnerabilityOther4, SpellId.FireVulnerabilityOther5, SpellId.FireVulnerabilityOther6,
                SpellId.FireVulnerabilityOther7, SpellId.FireVulnerabilityOther8,
            },
            [DamageType.Acid] = new[]
            {
                SpellId.AcidVulnerabilityOther1, SpellId.AcidVulnerabilityOther2, SpellId.AcidVulnerabilityOther3,
                SpellId.AcidVulnerabilityOther4, SpellId.AcidVulnerabilityOther5, SpellId.AcidVulnerabilityOther6,
                SpellId.AcidVulnerabilityOther7, SpellId.AcidVulnerabilityOther8,
            },
            [DamageType.Electric] = new[]
            {
                SpellId.LightningVulnerabilityOther1, SpellId.LightningVulnerabilityOther2, SpellId.LightningVulnerabilityOther3,
                SpellId.LightningVulnerabilityOther4, SpellId.LightningVulnerabilityOther5, SpellId.LightningVulnerabilityOther6,
                SpellId.LightningVulnerabilityOther7, SpellId.LightningVulnerabilityOther8,
            },
        };

        /// <summary>
        /// The matching element's Vulnerability spell at the war spell's level (clamped to the 1-8 range
        /// element vulns support), or null when the damage type has no single-element vuln (e.g. a combo
        /// or non-elemental type). Pure/static for testability.
        /// </summary>
        public static SpellId? GetVulnerabilitySpell(DamageType damageType, uint spellLevel)
        {
            if (!VulnByElement.TryGetValue(damageType, out var progression))
                return null;

            var index = (int)Math.Clamp(spellLevel, 1u, (uint)progression.Length) - 1;
            return progression[index];
        }

        /// <summary>
        /// The proc chance at a given rank: base + step*(rank-1) + the Life Magic rider + the equipment-mod
        /// term. Pure for testability. Returns 0 for rank &lt;= 0.
        ///
        /// <paramref name="gearModChance"/> is the Elemental Rend equipment mod, a MACHINERY mod: it
        /// amplifies the ability's own roll and stays behind the same rank check. Defaults to 0, which
        /// reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static float RendChance(int rank, double chanceBase, double chanceStep, double lifeMagicFraction, double gearModChance = 0.0, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var rider = Math.Max(0.0, lifeMagicFraction);

            // THE AFFINITY RIDER IS CAPPED (class_ability_affinity_chance_cap, added 2026-08-04).
            // GetClassAbilityScaling returns a raw quotient (skill / divisor) with no bound of its own, so
            // this rider is linear in a skill value the server does not constrain. Measured live on a
            // character with Life Magic 5226: the rider came to +209 percentage points and the proc fired
            // on literally every landed war spell. The bug surfaced on the Spellsword procs, which share
            // this shape; this ability had it too.
            //
            // The GEAR MOD is deliberately NOT capped by this - it is a bounded equipment roll rather than
            // an unbounded skill quotient. A cap of 0 means uncapped, preserving pre-2026-08-04 behavior.
            if (affinityCap > 0.0)
                rider = Math.Min(rider, affinityCap);

            return (float)Math.Max(0.0, chanceBase + (rank - 1) * chanceStep + rider + Math.Max(0.0, gearModChance));
        }

        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            // Applies no projectile, so it may fire on AOE copies / echoes; but a dead target can't be
            // debuffed meaningfully.
            if (primaryTarget.IsDead)
                return;

            var vuln = GetVulnerabilitySpell(projectile.Spell.DamageType, projectile.Spell.Level);
            if (vuln == null)
                return;

            var lifeMagic = caster.GetClassAbilityScaling(Skill.LifeMagic,
                PropertyManager.GetDouble("class_ability_elementalrend_lifemagic_per_trained").Item,
                PropertyManager.GetDouble("class_ability_elementalrend_lifemagic_per_spec").Item) * 0.01;

            // Elemental Rend equipment mod (MACHINERY): +pp on this ability's own vulnerability-proc roll.
            // Read inside the handler, which only runs for a caster who owns Elemental Rend.
            var chance = RendChance(rank,
                PropertyManager.GetDouble("class_ability_elementalrend_chance_base").Item,
                PropertyManager.GetDouble("class_ability_elementalrend_chance_step").Item,
                lifeMagic,
                caster.GetEquippedModValue(EquipmentModId.ElementalRend),
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            // Apply the vuln through the normal enchantment registry (standard stacking/refresh rules).
            var vulnSpell = new Spell(vuln.Value);
            caster.CreateEnchantment(primaryTarget, caster, projectile.ProjectileLauncher, vulnSpell);

            // AND PLAY THE SPELL'S OWN VISUAL, because CreateEnchantment does not - it calls
            // EnchantmentManager.Add and nothing else. A normal cast gets its visual from DoSpellEffects at
            // the end of HandleCastSpell; a proc that writes the registry directly never reaches that path,
            // so without this the debuff lands completely silently. Reported live on Spellsword's Sundermark
            // 2026-08-04 ("the vuln proc doesn't seem to show a visual"); this ability had the identical gap
            // and is fixed alongside it.
            //
            // spell.TargetEffect comes from portal.dat via SpellBase, exactly as DoSpellEffects reads it, so
            // this plays whatever the client already plays for a cast Vulnerability rather than a hardcoded
            // guess. Broadcast so nearby players see it too.
            primaryTarget.EnqueueBroadcast(new GameMessageScript(primaryTarget.Guid, vulnSpell.TargetEffect, vulnSpell.Formula.Scale));
        }

        /// <summary>
        /// Mirrors the proc-chance terms fed into RendChance() in OnSpellHit above - the two must stay in
        /// step. Skill/Affinity/Gear are percentage POINTS. The affinity rider is clamped by
        /// class_ability_affinity_chance_cap the same way RendChance() clamps it; CapNote is set only when
        /// the raw (unclamped) rider actually exceeds the cap this call.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_elementalrend_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_elementalrend_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            var rawRider = Math.Max(0.0, player.GetClassAbilityScaling(Skill.LifeMagic,
                PropertyManager.GetDouble("class_ability_elementalrend_lifemagic_per_trained").Item,
                PropertyManager.GetDouble("class_ability_elementalrend_lifemagic_per_spec").Item) * 0.01);

            var clampedRider = affinityCap > 0.0 ? Math.Min(rawRider, affinityCap) : rawRider;
            var capBit = affinityCap > 0.0 && rawRider > affinityCap;

            var gearChance = Math.Max(0.0, player.GetEquippedModValue(EquipmentModId.ElementalRend));

            var skill = skillChance * 100.0;
            var affinity = rawRider * 100.0;   // RAW (pre-clamp)
            var gear = gearChance * 100.0;

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
