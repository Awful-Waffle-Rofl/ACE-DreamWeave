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
    /// ELEMENTAL REND (Archmage T3), RETURNED 2026-11-03 as a NEW, ADDITIONAL ability beside Elemental Proficiency
    /// (owner ruling). Behaviour is identical to the pre-#1945 Rend: a landed damaging spell projectile has a
    /// 10/18/26% chance (by rank) to inflict the matching element's Life-Magic Vulnerability on the struck
    /// target, at a level equivalent to the spell that landed (a level-8 fire bolt applies the level-8
    /// "Incantation of Fire Vulnerability Other").
    ///
    /// NAMING COLLISION, DELIBERATE. On master ClassAbilityId.ElementalRend (42), Name "elementalrend", the class
    /// ElementalRendAbility, EquipmentModId.ElementalRend (23) and PropertyFloat.GearModElementalRend are all the
    /// ELEMENTAL PROFICIENCY ability and its amplifier mod; the persisted character key is derived from that Name,
    /// so none of them could be renamed. This ability therefore has fresh internals: ClassAbilityId.ElementalRendVuln
    /// (97), Name "elementalrendvuln", EquipmentModId.ElementalRendVuln (50), PropertyFloat.GearModElementalRendVuln
    /// (8169). Only the DisplayName says "Elemental Rend". The two abilities stack freely and a player may hold both.
    ///
    /// THE DAMAGE-TYPE GATE BOUNDS THIS ENTRY. GetVulnerabilitySpell is keyed on the spell's DAMAGE TYPE and the
    /// vulnerability table covers only the seven elemental types (Slash, Pierce, Bludgeon, Cold, Fire, Acid,
    /// Electric). A void bolt is DamageType.Nether and a life projectile is DamageType.Health, so both return null
    /// and OnSpellHit returns without proccing. The player-facing text must not promise void or life coverage.
    /// The vulnerability table itself lives on <see cref="ElementalRendAbility.GetVulnerabilitySpell"/> (kept
    /// unchanged through the Elemental Proficiency rewrite because Sundermark and DebuffEffect read it too); this
    /// ability reuses it rather than carrying a second copy.
    ///
    /// The proc chance scales with Arcane Lore, the one scaling source living outside every Training bundle. The
    /// rider is MULTIPLICATIVE on this ability's own rank bonus; <see cref="RendChance"/>'s fraction parameter stays
    /// additive in its own units, so the rider fed to it is the AMOUNT the multiplier adds
    /// (rankBonus * affinity - rankBonus), bounded by class_ability_affinity_chance_cap.
    ///
    /// Applied through the normal enchantment registry (WorldObject.CreateEnchantment), so it obeys the standard
    /// stacking rules. Because it spawns no projectile it carries no cascade risk, so unlike Spell AOE and Echo
    /// Cast it deliberately fires even on class-ability-spawned copies: a Spell AOE radiated copy or an Echo Cast
    /// recast can proc it.
    ///
    /// PvP: never. Player.ApplySpellHitClassAbilities drops every Player target before dispatch, and OnSpellHit
    /// repeats the check so a direct call cannot reach a player either. Pets are monsters of the player's own side
    /// and are not struck by their owner's projectiles.
    /// </summary>
    public class ElementalRendVulnAbility : ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ElementalRendVuln,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 3,
            Name = "elementalrendvuln",
            DisplayName = "Elemental Rend",
            // Live text: chance = class_ability_elementalrend_chance_base + (rank - 1) * class_ability_elementalrend_chance_step
            // (RankBonus; read in OnSpellHit and GetReadout). The affinity rider and gear term sit on top of it.
            RankTailTemplate = "{ladder:class_ability_elementalrend_chance_base,class_ability_elementalrend_chance_step|pct}% chance",
            DescriptionTemplate = "Your damaging spell projectiles can put the matching element's Vulnerability on the target, at " +
                          "the spell's level. Only elemental damage types have a matching Vulnerability.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ArcaneLore,
            Ceilings = new[]
            {
                new ClassAbilityCeiling { Id = "affinity", Bounds = CeilingBounds.Affinity, Source = CeilingSource.Tunable, Key = "class_ability_affinity_chance_cap", Label = "max chance from affinity", Format = "pct" },
                new ClassAbilityCeiling { Id = "gear", Bounds = CeilingBounds.Gear, Source = CeilingSource.GearStackCap, GearMod = EquipmentModId.ElementalRendVuln, Label = "max from gear", Format = "pct" },
            },
        };

        /// <summary>
        /// The base + step*(rank-1) rank bonus, before any rider. Pure. Returns 0 for rank &lt;= 0.
        /// </summary>
        public static double RankBonus(int rank, double chanceBase, double chanceStep) =>
            rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

        /// <summary>
        /// The proc chance at a given rank: base + step*(rank-1) + the affinity rider + the equipment-mod term.
        /// Pure for testability. Returns 0 for rank &lt;= 0.
        ///
        /// <paramref name="gearModChance"/> is the Elemental Rend equipment mod, a MACHINERY mod: it amplifies the
        /// ability's own roll and stays behind the same rank check. Defaults to 0.
        /// </summary>
        public static float RendChance(int rank, double chanceBase, double chanceStep, double affinityRider, double gearModChance = 0.0, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var rider = Math.Max(0.0, affinityRider);

            // THE AFFINITY RIDER IS CAPPED (class_ability_affinity_chance_cap). GetClassAbilityScaling returns a
            // raw quotient with no bound of its own, so an uncapped rider is linear in a skill value the server
            // does not constrain (measured live: +209 percentage points, a proc on every landed spell).
            //
            // The GEAR MOD is deliberately NOT capped by this - it is a bounded equipment roll rather than an
            // unbounded skill quotient. A cap of 0 means uncapped.
            if (affinityCap > 0.0)
                rider = Math.Min(rider, affinityCap);

            return (float)Math.Max(0.0, chanceBase + (rank - 1) * chanceStep + rider + Math.Max(0.0, gearModChance));
        }

        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            if (caster == null || primaryTarget == null || projectile?.Spell == null)
                return;

            // Never PvP (class abilities do not affect players). The dispatch already filters this; kept here so
            // the handler is safe on its own.
            if (primaryTarget is Player)
                return;

            // Applies no projectile, so it may fire on AOE copies / echoes; but a dead target can't be
            // debuffed meaningfully.
            if (primaryTarget.IsDead)
                return;

            var vuln = ElementalRendAbility.GetVulnerabilitySpell(projectile.Spell.DamageType, projectile.Spell.Level);
            if (vuln == null)
                return;

            var chanceBase = PropertyManager.GetDouble("class_ability_elementalrend_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_elementalrend_chance_step").Item;

            var rankBonus = RankBonus(rank, chanceBase, chanceStep);

            // Arcane Lore (MULTIPLICATIVE, no-arg overload = the shared rate pair). The AMOUNT the multiplier adds
            // is what feeds the additive-shaped RendChance() below, bounded by class_ability_affinity_chance_cap.
            var affinity = caster.GetClassAbilityAffinityMultiplier(Skill.ArcaneLore);
            var arcaneLore = rankBonus * affinity - rankBonus;

            // Elemental Rend equipment mod (MACHINERY): +pp on this ability's own vulnerability-proc roll. Read
            // inside the handler, which only runs for a caster who owns Elemental Rend.
            var chance = RendChance(rank, chanceBase, chanceStep, arcaneLore,
                caster.GetEquippedModValue(EquipmentModId.ElementalRendVuln),
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            // Apply the vuln through the normal enchantment registry (standard stacking/refresh rules).
            var vulnSpell = new Spell(vuln.Value);
            caster.CreateEnchantment(primaryTarget, caster, projectile.ProjectileLauncher, vulnSpell);

            // AND PLAY THE SPELL'S OWN VISUAL, because CreateEnchantment does not - it calls EnchantmentManager.Add
            // and nothing else. A normal cast gets its visual from DoSpellEffects at the end of HandleCastSpell; a
            // proc that writes the registry directly never reaches that path, so without this the debuff lands
            // completely silently. spell.TargetEffect comes from portal.dat via SpellBase, exactly as
            // DoSpellEffects reads it, so this plays whatever the client already plays for a cast Vulnerability.
            // Broadcast so nearby players see it too.
            primaryTarget.EnqueueBroadcast(new GameMessageScript(primaryTarget.Guid, vulnSpell.TargetEffect, vulnSpell.Formula.Scale));
        }

        /// <summary>
        /// Mirrors the proc-chance terms fed into RendChance() in OnSpellHit above - the two must stay in step.
        /// Skill/Affinity/Gear are percentage POINTS. Affinity is a MULTIPLIER on the rank term, but it is reported
        /// as the CAPPED AMOUNT that multiplier adds (rankBonus * affinity - rankBonus, then clamped by
        /// class_ability_affinity_chance_cap the same way RendChance() clamps it), so the three displayed terms
        /// still sum to Effective; CapNote is set only when the raw (unclamped) rider actually exceeds the cap.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_elementalrend_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_elementalrend_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var rankBonus = RankBonus(rank, chanceBase, chanceStep);

            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.ArcaneLore) ?? 1.0;

            var rawRider = Math.Max(0.0, rankBonus * multiplier - rankBonus);

            var clampedRider = affinityCap > 0.0 ? Math.Min(rawRider, affinityCap) : rawRider;
            var capBit = affinityCap > 0.0 && rawRider > affinityCap;

            var gearChance = Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.ElementalRendVuln) ?? 0.0);

            var skill = rankBonus * 100.0;
            var affinity = clampedRider * 100.0;
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
                Ceilings = new[]
                {
                    ClassAbilityCeilings.Read(Definition, "affinity", rawRider * 100.0, rank, ClassAbilityText.Live),
                    ClassAbilityCeilings.Read(Definition, "gear", gear, rank, ClassAbilityText.Live),
                },
            };
        }
    }
}
