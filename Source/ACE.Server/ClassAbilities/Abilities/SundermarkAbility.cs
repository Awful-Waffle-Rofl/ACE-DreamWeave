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
    /// Spellsword T2: a landed hit with ANY weapon has a 5/10/15% chance (by rank) to apply the matching
    /// elemental Vulnerability to the target, at a level driven by the player's Life Magic.
    ///
    /// THIS IS THE ONE SPELLSWORD PROC THAT IS NOT WEAPON-GATED. The three war procs (Spellblade,
    /// Runeblade, Spellstorm) require Light Weapons; Sundermark deliberately does not, so a character who
    /// wants the class's debuff without its weapon identity can buy it (SPELLSWORD-DESIGN.md section 3).
    /// Its chance DOES scale with rank, unlike the war procs, and stays low by design.
    ///
    /// AND IT DOES NOT FEED SPELLSURGE, in either direction. It neither adds a Spellsurge stack nor reads
    /// the stack bonus on its own chance (design section 5e: "any of the three war procs, NOT Sundermark -
    /// otherwise an any-weapon entry feeds the light-weapon-only ones"). Breaking that would let a
    /// heavy-weapon Sundermark build ramp procs it can never fire.
    ///
    /// WHY THE LOW CHANCE IS NOT THE BRAKE, recorded here because it looks like one. Vulnerability
    /// durations are long and a melee character swings roughly once a second, so at 15% the expected time
    /// to land the debuff is under seven swings and uptime after the opener is effectively 100%. The user
    /// ruled (section 4b-i) that this ships anyway, and the reasoning corrects the alarm rather than
    /// overriding it: Creature_Properties takes vulnMod = max(vulnMod, weaponResistanceMod), so a rending
    /// weapon ALREADY occupies the slot this writes to, and any player with the Life Magic can simply cast
    /// the level-8 vulnerability themselves on demand. Score it as a convenience floor for players without
    /// the weapon or the skill, not as a multiplier the class uniquely adds.
    ///
    /// Standing invariant (design Q9): Sundermark, Archmage's Elemental Rend and Blood Mage's Weakened
    /// Blood all write the SAME enchantment key, so only the strongest applies and three classes can never
    /// compound their vulnerabilities. Any future ability writing that key must respect it.
    ///
    /// The vulnerability ids come from ElementalRendAbility.GetVulnerabilitySpell rather than a second
    /// table here - deliberately, per design section 6. That table already carries all eight rungs (level 7
    /// is the "X's Gift" family, level 8 the "Incantation of X Vulnerability Other"), so there is exactly
    /// one place to fix if the client ever changes them.
    /// </summary>
    public class SundermarkAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Sundermark,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 2,
            Name = "sundermark",
            DisplayName = "Sundermark",
            Description = "Your landed hits with any weapon have a 5/10/15% chance (by rank) to inflict the " +
                          "matching element's Vulnerability on the target. The level applied is set by your " +
                          "Life Magic, and higher Life Magic also increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        /// <summary>
        /// The proc chance at a given rank: base + step*(rank-1) + the Life Magic rider. Pure for
        /// testability. Returns 0 for rank &lt;= 0.
        ///
        /// Unlike the three war procs this one DOES carry a rank term, and it deliberately carries no
        /// Spellsurge term - see the class doc comment.
        ///
        /// <paramref name="gearModChance"/> is the SUNDERMARK equipment mod (EquipmentModId.Sundermark), a
        /// MACHINERY mod added OUTSIDE the affinity clamp - see SpellbladeAbility.Chance for why that
        /// placement is load-bearing. Last and defaulted to 0, so an unmodded build is bit-identical.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double chanceStep, double lifeMagicFraction, double affinityCap = 0.0, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var rider = Math.Max(0.0, lifeMagicFraction);

            // Affinity rider capped - see SpellbladeAbility.Chance. This entry is the most sensitive of the
            // four to an uncapped rider, because the design's whole containment argument for Sundermark is
            // that its chance stays LOW ("5/10/15%, fairly low"); a rider that can reach certainty deletes
            // that premise outright. A cap of 0 means uncapped. The gear term below sits OUTSIDE this
            // clamp - it is bounded by its own registry MaxMagnitude and StackCap, so the clamp that exists
            // to bound an unbounded skill quotient must not be allowed to swallow it.
            if (affinityCap > 0.0)
                rider = Math.Min(rider, affinityCap);

            return (float)Math.Max(0.0, chanceBase + (rank - 1) * chanceStep + rider + Math.Max(0.0, gearModChance));
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || damageEvent == null)
                return;

            // NO weapon-skill gate here, on purpose. Any weapon class can carry this entry.

            // A dead target cannot be usefully debuffed (the strike that triggered this frequently kills).
            if (target.IsDead)
                return;

            // The DEALT damage type only, never the weapon's declared W_DamageType. A combined Pierce|Slash
            // weapon marks the target for whichever type the swing actually landed, so a thrust applies
            // Piercing Vulnerability and a slash from the same sword applies Blade Vulnerability.
            var damageType = damageEvent.DamageType;

            var lifeMagic = attacker.GetClassAbilityScaling(Skill.LifeMagic,
                PropertyManager.GetDouble("class_ability_sundermark_lifemagic_per_trained").Item,
                PropertyManager.GetDouble("class_ability_sundermark_lifemagic_per_spec").Item) * 0.01;

            // Sundermark equipment mod (MACHINERY): +pp on this ability's own proc roll. Ownership is proven
            // by the dispatch itself - only LEARNED IOutgoingDamageAbility handlers are in the hook cache.
            var chance = Chance(rank,
                PropertyManager.GetDouble("class_ability_sundermark_chance_base").Item,
                PropertyManager.GetDouble("class_ability_sundermark_chance_step").Item,
                lifeMagic,
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item,
                attacker.GetEquippedModValue(EquipmentModId.Sundermark));

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            // Level is driven by LIFE Magic (vulnerabilities are Life Magic), not by rank and not by War
            // Magic. The cap is the tunable, not the rank ladder: the user ruled the entry ships uncapped,
            // and class_ability_sundermark_max_level survives only as a safety valve at its permissive
            // default of 8 (design section 4b-i).
            var maxLevel = (uint)Math.Clamp(PropertyManager.GetLong("class_ability_sundermark_max_level").Item, 1L, 8L);

            var rungIndex = SpellswordSpellTables.SelectRungIndex(SpellswordSpellTables.VulnerabilityLevels,
                maxLevel,
                attacker.GetCreatureSkill(Skill.LifeMagic).Current,
                PropertyManager.GetDouble("class_ability_spellsword_level_skill_scale").Item);

            var spellLevel = SpellswordSpellTables.VulnerabilityLevels[rungIndex];

            // Returns null for a damage type with no single-element vulnerability (Health / Stamina / Mana /
            // Nether / Base, or a combined type), which must apply nothing, silently.
            var vuln = ElementalRendAbility.GetVulnerabilitySpell(damageType, spellLevel);
            if (vuln == null)
                return;

            // Applied through the normal enchantment registry, so it obeys the standard stacking rules: it
            // will not stack past a stronger cast Vulnerability and refreshes an equal or weaker one. The
            // wielded weapon is passed as the enchantment's weapon argument, matching the caster/launcher
            // split the war procs use.
            var vulnSpell = new Spell(vuln.Value);
            attacker.CreateEnchantment(target, attacker, damageEvent.Weapon, vulnSpell);

            // AND PLAY THE SPELL'S OWN VISUAL, because CreateEnchantment does not.
            //
            // Reported live 2026-08-04: "the vuln proc doesn't seem to show a visual". Confirmed in source
            // rather than guessed - WorldObject.CreateEnchantment does exactly one thing, calls
            // target.EnchantmentManager.Add, and never broadcasts a play script. A normal CAST is what
            // supplies the visual, via DoSpellEffects at the end of HandleCastSpell; a proc that writes the
            // registry directly bypasses that path entirely, so the debuff lands silently and the player
            // has no way to know their proc fired.
            //
            // THE SCRIPT IS THE SPELL'S, NOT A GUESS. spell.TargetEffect is read straight out of portal.dat
            // via SpellBase, exactly as DoSpellEffects does, so whatever the client already plays for a cast
            // Fire Vulnerability is what plays here. Broadcast rather than sent, so nearby players see it
            // too - the same shape Weakened Blood's Fester visual uses (Player_ClassAbilityBuffs.cs), which
            // was added to answer this identical complaint on the Blood Mage side.
            target.EnqueueBroadcast(new GameMessageScript(target.Guid, vulnSpell.TargetEffect, vulnSpell.Formula.Scale));

            // NOTE: no AddSpellsurgeStack call. See the class doc comment - this is load-bearing, not an
            // omission.
        }

        /// <summary>
        /// Mirrors the proc-chance terms fed into Chance() above - unlike Spellblade/Runeblade this DOES
        /// carry a rank term. Affinity carries the RAW (pre-clamp) Life Magic rider, Effective uses the
        /// clamped one. Gear is the SUNDERMARK mod, added to Effective OUTSIDE the clamp exactly as
        /// Chance() adds it.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_sundermark_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_sundermark_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            var rawRider = Math.Max(0.0, player.GetClassAbilityScaling(Skill.LifeMagic,
                PropertyManager.GetDouble("class_ability_sundermark_lifemagic_per_trained").Item,
                PropertyManager.GetDouble("class_ability_sundermark_lifemagic_per_spec").Item) * 0.01);

            var clampedRider = affinityCap > 0.0 ? Math.Min(rawRider, affinityCap) : rawRider;
            var capBit = affinityCap > 0.0 && rawRider > affinityCap;

            // Rank-gated like skillChance above - a machinery mod reports nothing without its ability.
            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Sundermark) ?? 0.0);

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
                Unit = "pp",
                Label = "proc",
                Per = null,
                CapNote = capBit ? "affinity cap" : null,
            };
        }
    }
}
