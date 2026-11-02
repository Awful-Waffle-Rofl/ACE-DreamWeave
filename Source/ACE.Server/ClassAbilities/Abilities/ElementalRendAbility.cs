using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// ELEMENTAL PROFICIENCY (Archmage T3, ranks 1-3). The class keeps its old name, ClassAbilityId.ElementalRend
    /// and Name "elementalrend" because that Name is the quest-key suffix that stores owned ranks; only the
    /// DisplayName and the behaviour changed. ELEMENTAL-PROFICIENCY-DESIGN.md is the authority.
    ///
    /// Every damaging spell the Archmage lands on a MONSTER applies an effect chosen by the spell's damage
    /// type, with no roll. Fire, Pierce and Slash act INSIDE SpellProjectile.CalculateDamage (see
    /// Player_ElementalProficiency.cs) because they change the damage being dealt; this hook runs AFTER the hit
    /// and so owns only the timed effects: Lightning (target takes more damage), Frost (slow), Acid (a burn
    /// sized off SpellProjectile.LastAppliedDamage) and Bludgeon (a stun with an immunity window). The work is
    /// done by Player.ApplyElementalProficiencySpellHit; this handler is the dispatch seam and the readout.
    ///
    /// No IsClassAbilitySpawned filter: the effects spawn no projectile, so they carry no cascade risk and
    /// Spell AOE / Cascade / Echo Cast children apply them too. Nether, Health and combo damage types carry
    /// no effect. Players, pets and game pieces never take one (ElementalProficiencyMath.IsEligibleTarget),
    /// which also keeps PvP out.
    ///
    /// GetVulnerabilitySpell and its table below are KEPT UNCHANGED from the Elemental Rend days: Sundermark
    /// and DebuffEffect read the vulnerability ids from here, and a test pins those call texts.
    /// </summary>
    public class ElementalRendAbility : ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ElementalRend,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 3,
            Name = "elementalrend",
            DisplayName = "Elemental Proficiency",
            Description = "Your damaging spells apply an effect by element when they land on a monster. " +
                          "Fire: +10/20/30% damage. " +
                          "Lightning: the target takes 10/20/30% more damage from all sources for 10 seconds. " +
                          "Frost: the target is slowed by 20/40/60% for 10 seconds. " +
                          "Acid: the target burns for 20/40/60% of the hit over 6 seconds; a new acid hit refreshes the burn. " +
                          "Bludgeon: the target is stunned for 2/3/4 seconds, then cannot be stunned again for 8 seconds. " +
                          "Pierce: ignores 10/20/30% of the target's resistance or deals 10/20/30% more damage, whichever is greater. " +
                          "Slash: +5/10/15 percentage points of critical chance. " +
                          "Higher War Magic strengthens the Fire, Lightning, Acid and Pierce effects.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.WarMagic,
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
        /// The matching element's Vulnerability spell at the landed spell's level (clamped to the 1-8 range
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
        /// Dispatch seam for the timed effects. The shared spell-hit dispatch already guarantees a landed
        /// damaging projectile on a non-player creature; Player.ApplyElementalProficiencySpellHit re-checks
        /// eligibility and owns every rule (see its summary).
        /// </summary>
        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            if (caster == null || primaryTarget == null || projectile?.Spell == null)
                return;

            caster.ApplyElementalProficiencySpellHit(primaryTarget, projectile);
        }

        /// <summary>
        /// PRIMARY is the Fire damage bonus; the other six elements are Secondary lines. Each line's Skill is
        /// the rank value, Affinity the amount the (capped) War Magic multiplier adds, Gear the amount the
        /// Elemental Rend gear amplifier adds on top of that, so Skill + Affinity + Gear == Effective exactly.
        /// Frost, Bludgeon and Slash are never scaled (Affinity and Gear 0). Null-Player-safe.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            return BuildReadout(rank,
                player?.GetClassAbilityAffinityMultiplier(Skill.WarMagic) ?? 1.0,
                player?.GetEquippedModValue(EquipmentModId.ElementalRend) ?? 0.0);
        }

        /// <summary>The pure core of <see cref="GetReadout"/>, taking the live affinity multiplier and gear amplifier.</summary>
        public static ClassAbilityReadout BuildReadout(int rank, double affinityMultiplier, double gearAmplifier)
        {
            List<ClassAbilityReadout> secondaries = null;

            if (rank > 0)
            {
                secondaries = new List<ClassAbilityReadout>
                {
                    Term(DamageType.Electric, "dmg taken", rank, affinityMultiplier, gearAmplifier),
                    Term(DamageType.Cold, "slow", rank, affinityMultiplier, gearAmplifier),
                    Term(DamageType.Acid, "burn of hit", rank, affinityMultiplier, gearAmplifier),
                    Term(DamageType.Pierce, "resist ignored", rank, affinityMultiplier, gearAmplifier),
                    Term(DamageType.Slash, "crit chance", rank, affinityMultiplier, gearAmplifier),
                    new ClassAbilityReadout
                    {
                        HasValue = true,
                        Skill = ElementalProficiencyMath.StunSeconds(rank),
                        Affinity = 0.0,
                        Gear = 0.0,
                        Effective = ElementalProficiencyMath.StunSeconds(rank),
                        Unit = "s",
                        Label = "stun",
                        Per = null,
                        CapNote = null,
                    },
                };
            }

            return Term(DamageType.Fire, "fire dmg", rank, affinityMultiplier, gearAmplifier, secondaries);
        }
        private static ClassAbilityReadout Term(DamageType type, string label, int rank, double affinityMultiplier, double gearAmplifier, List<ClassAbilityReadout> secondary = null)
        {
            var perRank = PropertyManager.GetDouble(ElementalProficiencyMath.PerRankKey(type)).Item;
            var cap = Math.Max(0.0, PropertyManager.GetDouble(ElementalProficiencyMath.KeyPrefix + "affinity_cap").Item);

            var scaled = ElementalProficiencyMath.IsAffinityScaled(type);
            var value = rank <= 0 ? 0.0 : perRank * rank;
            var affinity = scaled ? Math.Min(affinityMultiplier, 1.0 + cap) : 1.0;
            var gear = scaled ? Math.Max(0.0, gearAmplifier) : 0.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = value * 100.0,
                Affinity = value * (affinity - 1.0) * 100.0,
                Gear = value * affinity * gear * 100.0,
                Effective = ElementalProficiencyMath.Magnitude(type, rank, affinityMultiplier, gearAmplifier) * 100.0,
                Unit = "%",
                Label = label,
                Per = null,
                CapNote = scaled && affinityMultiplier > 1.0 + cap ? "affinity cap" : null,
                Secondary = secondary,
            };
        }
    }
}