using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Spellsword T2: a landed hit with ANY weapon, or a landed damaging spell projectile, has a 7/13/19%
    /// chance (by rank) to apply the matching elemental Vulnerability to the target, at a level driven by
    /// the player's Life Magic.
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
    /// durations are long and a melee character swings roughly once a second, so at 19% the expected time
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
    ///
    /// SPELL HITS TOO, as of the 2026-10-24 Spellsword pass. The entry also rides ISpellHitAbility, the
    /// same opt-in Dispelling Dance uses, so a landed damaging spell projectile rolls the same chance and
    /// applies the Vulnerability for the spell's own damage type. Types with no single-element
    /// vulnerability spell (a life projectile is Health) apply nothing, exactly as a weapon swing of such a
    /// type does. The two paths are reached from disjoint call sites (weapon path only from
    /// Player.DamageTarget, spell path only from SpellProjectile.OnCollideObject), so one weapon hit or one
    /// projectile rolls once. A proc swing therefore rolls MORE than once: the weapon hit rolls on the weapon
    /// path, and each projectile the proc launches (a Runeblade blast's three or five, a Spellstorm ring's nine) is a
    /// separate spell hit that rolls again on the spell path. There is NO resist check on either path: the debuff is written straight to the registry
    /// through CreateEnchantment, which is what the weapon path has always done.
    ///
    /// THE NETHER BRANCH (owner ruling 2026-10-30, option B, replacing the 2026-10-27 Nether-melee gate):
    /// ANY Nether hit - a swing of any weapon, a Volatile Elements hit re-typed to Nether, a void bolt, a
    /// proc that launched a Nether spell - applies one of the three void curses (Corruption, Corrosion,
    /// Destructive Curse) instead of a vulnerability, chosen uniformly. Its chance is the same roll as the
    /// vulnerability path, on the same Life Magic affinity, and the curse LEVEL is chosen exactly as the
    /// vulnerability level is: the highest rung of the 8-rung ladder the attacker's Life Magic reaches
    /// (SpellswordSpellTables.SelectRungIndex, scaled by class_ability_spellsword_level_skill_scale), capped by
    /// class_ability_sundermark_nether_max_level (clamped 1-8, default 8) as a safety valve (owner correction
    /// 2026-10-30). See IsNetherCurseHit.
    /// </summary>
    public class SundermarkAbility : IOutgoingDamageAbility, ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Sundermark,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 2,
            Name = "sundermark",
            DisplayName = "Sundermark",
            Description = "Your landed hits with any weapon and your damaging spell projectiles have a 7/13/19% " +
                          "chance (by rank) to inflict the matching element's Vulnerability on the target. The " +
                          "level applied is set by your Life Magic, and higher Life Magic also increases the chance. " +
                          "A Nether hit instead inflicts a random void curse - Corruption, Corrosion or Destructive Curse - " +
                          "at the level your Life Magic sets.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // Spellsword pass 2026-10-24: 2/rank -> 1/rank (ClassAbilityRepricing wave 4)
            Implemented = true,
            // Life Magic scales EVERY roll, the Nether branch included (owner ruling 2026-10-30, option B; the
            // 2026-10-27 Void Magic rider and its AdditionalAffinitySkills entry are gone).
            AffinitySkill = Skill.LifeMagic,
        };

        /// <summary>
        /// The proc chance at a given rank: (base + step*(rank-1)) MULTIPLIED by the Life Magic affinity
        /// (2026-09-12 overhaul), plus the SUNDERMARK equipment mod. Pure for testability. Returns 0 for
        /// rank &lt;= 0.
        ///
        /// Unlike the three war procs this one DOES carry a rank term, and it deliberately carries no
        /// Spellsurge term - see the class doc comment.
        ///
        /// <paramref name="affinityMultiplier"/> is what <c>Player.GetClassAbilityAffinityMultiplier</c>
        /// returns - a FACTOR &gt;= 1.0 multiplying the rank-scaled base, not a raw additive quotient. What
        /// is reported/capped is the AMOUNT that multiply ADDS (rankBonus * multiplier - rankBonus).
        ///
        /// <paramref name="affinityCap"/> bounds that added amount. This entry is the most sensitive of the
        /// four to an uncapped rider, because the design's whole containment argument for Sundermark is
        /// that its chance stays LOW ("7/13/19%, fairly low"); a rider that can reach certainty deletes
        /// that premise outright. A cap of 0 means uncapped.
        ///
        /// <paramref name="gearModChance"/> is the SUNDERMARK equipment mod (EquipmentModId.Sundermark), a
        /// MACHINERY mod added OUTSIDE the affinity clamp - see SpellbladeAbility.Chance for why that
        /// placement is load-bearing. Last and defaulted to 0, so an unmodded build is bit-identical.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double chanceStep, double affinityMultiplier, double affinityCap = 0.0, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var rankBonus = chanceBase + (rank - 1) * chanceStep;

            var added = rankBonus * affinityMultiplier - rankBonus;

            // Affinity added amount capped - see SpellbladeAbility.Chance. The gear term below sits OUTSIDE
            // this clamp - it is bounded by its own registry MaxMagnitude and StackCap, so the clamp that
            // exists to bound an unbounded skill multiply must not be allowed to swallow it.
            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            added = Math.Max(0.0, added);

            return (float)Math.Max(0.0, rankBonus + added + Math.Max(0.0, gearModChance));
        }

        // ---- Nether branch (owner ruling 2026-10-30, option B) ---------------------------------------

        /// <summary>
        /// TRUE when this proc takes the Nether curse branch: the hit DEALT Nether. Nothing else is consulted -
        /// not the weapon class, not the weapon's declared W_DamageType, not the dispatch path - so a Nether
        /// swing of any weapon, a Volatile Elements hit re-typed to Nether, a void wand's void bolt and a
        /// Nether proc spell all qualify (owner ruling 2026-10-30, replacing the 2026-10-27 Nether-melee gate).
        /// </summary>
        public static bool IsNetherCurseHit(DamageType dealtDamageType)
        {
            return dealtDamageType == DamageType.Nether;
        }

        /// <summary>
        /// The affinity multiplier for one roll, shared by the proc and the readout so the two cannot drift.
        /// Life Magic on every roll, Nether included (owner ruling 2026-10-30). The call is literal so
        /// ClassAbilityAffinityDeclarationTests can see it back the declared AffinitySkill.
        /// </summary>
        private static double AffinityMultiplier(Player player)
        {
            return player.GetClassAbilityAffinityMultiplier(Skill.LifeMagic);
        }

        /// <summary>
        /// class_ability_sundermark_nether_max_level clamped to the ladder, 1-8: the safety-valve cap over the
        /// void curse level Life Magic selects.
        /// </summary>
        public static uint NetherMaxLevel(long tunable)
        {
            return (uint)Math.Clamp(tunable, 1L, 8L);
        }

        /// <summary>
        /// The uniform family pick, through an injectable inclusive-range integer source (the production
        /// caller passes ThreadSafeRandom.Next, whose int overload is inclusive at both ends).
        /// </summary>
        public static int RollNetherCurseFamily(Func<int, int, int> nextInclusive)
        {
            return nextInclusive(0, SpellswordSpellTables.NetherCurseFamilies.Length - 1);
        }

        /// <summary>
        /// The curse a Nether proc applies: family <paramref name="familyRoll"/>, at the highest rung of the
        /// 8-rung ladder <paramref name="lifeMagicCurrent"/> reaches (the same SelectRungIndex call the
        /// vulnerability path makes), capped by the clamped nether max-level tunable (owner correction
        /// 2026-10-30: the level scales with Life Magic exactly like the vulnerability). Pure for testability.
        /// </summary>
        public static SpellId SelectNetherCurse(int familyRoll, uint lifeMagicCurrent, long maxLevelTunable, double thresholdScale)
        {
            var rungIndex = SpellswordSpellTables.SelectRungIndex(SpellswordSpellTables.VulnerabilityLevels,
                NetherMaxLevel(maxLevelTunable),
                lifeMagicCurrent,
                thresholdScale);

            return SpellswordSpellTables.NetherCurseFor(familyRoll, rungIndex);
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (damageEvent == null)
                return;

            // NO weapon-skill gate here, on purpose. Any weapon class can carry this entry.
            //
            // The DEALT damage type only, never the weapon's declared W_DamageType. A combined Pierce|Slash
            // weapon marks the target for whichever type the swing actually landed, so a thrust applies
            // Piercing Vulnerability and a slash from the same sword applies Blade Vulnerability.
            TryApplyVulnerability(attacker, rank, target, damageEvent.DamageType, damageEvent.Weapon);
        }

        /// <summary>
        /// The SPELL path (2026-10-24): a landed damaging spell projectile of any school rolls the same
        /// chance and marks the target for the SPELL's damage type. The dispatch already guarantees a
        /// damaging projectile landed on a monster. Fires on every landed copy, the same as Dispelling Dance
        /// and Elemental Rend; the vulnerability registry's own stacking rules (a refresh, never a second
        /// layer from the same caster) keep a multi-projectile spell from doing more than one cast would.
        /// The projectile's launcher (the wielded weapon or wand) is passed as the enchantment's weapon
        /// argument, matching the weapon path.
        /// </summary>
        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            if (projectile?.Spell == null)
                return;

            TryApplyVulnerability(caster, rank, primaryTarget, projectile.Spell.DamageType, projectile.ProjectileLauncher);
        }

        /// <summary>
        /// The proc body both dispatch paths share, so the roll, the tunable reads, the level selection and
        /// the visual cannot drift between them. Every line came from the weapon path unchanged; only the
        /// damage type and the enchantment's weapon argument are supplied by the caller.
        /// </summary>
        private static void TryApplyVulnerability(Player attacker, int rank, Creature target, DamageType damageType, WorldObject weapon)
        {
            if (attacker == null || target == null)
                return;

            // A dead target cannot be usefully debuffed (the strike that triggered this frequently kills).
            if (target.IsDead)
                return;

            // NETHER BRANCH (owner ruling 2026-10-30, option B): ANY hit that dealt Nether applies a void curse
            // instead of a vulnerability - weapon path and spell path alike, whatever the weapon. The chance
            // below is the same roll on the same Life Magic affinity either way.
            var netherCurse = IsNetherCurseHit(damageType);

            var affinity = AffinityMultiplier(attacker);

            // Sundermark equipment mod (MACHINERY): +pp on this ability's own proc roll. Ownership is proven
            // by the dispatch itself - only LEARNED IOutgoingDamageAbility handlers are in the hook cache.
            var chance = Chance(rank,
                PropertyManager.GetDouble("class_ability_sundermark_chance_base").Item,
                PropertyManager.GetDouble("class_ability_sundermark_chance_step").Item,
                affinity,
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item,
                attacker.GetEquippedModValue(EquipmentModId.Sundermark));

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            if (netherCurse)
            {
                // One of Corruption / Corrosion / Destructive Curse, uniformly, at the rung LIFE Magic reaches on
                // the same 8-rung ladder the vulnerability uses, capped by its own safety-valve tunable (owner
                // correction 2026-10-30). Applied and shown exactly like the vulnerability below. The DoT's
                // damage-resist-rating reduction on the target is INTENDED (a party-wide multiplier) and is
                // deliberately left alone.
                var curse = SelectNetherCurse(RollNetherCurseFamily(ThreadSafeRandom.Next),
                    attacker.GetCreatureSkill(Skill.LifeMagic).Current,
                    PropertyManager.GetLong("class_ability_sundermark_nether_max_level").Item,
                    PropertyManager.GetDouble("class_ability_spellsword_level_skill_scale").Item);

                ApplyAndShow(attacker, target, weapon, new Spell(curse));
                return;
            }

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

            ApplyAndShow(attacker, target, weapon, new Spell(vuln.Value));

            // NOTE: no AddSpellsurgeStack call. See the class doc comment - this is load-bearing, not an
            // omission.
        }

        /// <summary>
        /// Writes the debuff and plays its visual. Shared by the vulnerability path and the Nether curse
        /// path so the two cannot drift.
        /// </summary>
        private static void ApplyAndShow(Player attacker, Creature target, WorldObject weapon, Spell vulnSpell)
        {
            // Applied through the normal enchantment registry, so it obeys the standard stacking rules: it
            // will not stack past a stronger cast Vulnerability and refreshes an equal or weaker one. The
            // wielded weapon is passed as the enchantment's weapon argument, matching the caster/launcher
            // split the war procs use.
            //
            // For a Nether curse this is the same call a landed Corrosion / Corruption projectile makes
            // (SpellProjectile's EnchantmentProjectile branch), so EnchantmentManager.BuildEntry runs the
            // damage-over-time branch on it and the ticks are credited to the attacker (CasterObjectId).
            attacker.CreateEnchantment(target, attacker, weapon, vulnSpell);

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
        /// carry a rank term. Affinity is the CAPPED added amount (rankBonus * multiplier - rankBonus, then
        /// clamped), so the three displayed terms still sum to Effective. Gear is the SUNDERMARK mod, added
        /// to Effective OUTSIDE the clamp exactly as Chance() adds it.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_sundermark_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_sundermark_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            // Life Magic for every roll, Nether included (owner ruling 2026-10-30), through the same helper
            // the proc uses.
            var affinityMultiplier = AffinityMultiplier(player);
            var rawAdded = skillChance * affinityMultiplier - skillChance;

            var clampedAdded = affinityCap > 0.0 ? Math.Min(rawAdded, affinityCap) : rawAdded;
            clampedAdded = Math.Max(0.0, clampedAdded);
            var capBit = affinityCap > 0.0 && rawAdded > affinityCap;

            // Rank-gated like skillChance above - a machinery mod reports nothing without its ability.
            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Sundermark) ?? 0.0);

            var skill = skillChance * 100.0;
            var affinity = clampedAdded * 100.0;
            var gear = gearChance * 100.0;
            var effective = skill + affinity + gear;

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
