using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    public enum PlayerAttackMode
    {
        Melee,
        Missile,

        /// <summary>A war or void projectile spell, named by spell id.</summary>
        Magic,
    }

    /// <summary>
    /// Builds AttackerSpecs for a PLAYER attacking a monster, from the player's equipped weapon, ammo or wand and
    /// skills, through the same production functions DamageEvent and SpellProjectile call for a player source. The
    /// player is normally an offline one from ProfileBuilder.HydrateFromSnapshot. Pair the spec with a
    /// MonsterDefenderProfile and MonsterCombatSimulator.Analytic.
    ///
    /// STANCE AND BAR. The player is put into the attack's CombatMode (Melee, Missile, Magic) with
    /// SimulatorStance.With before any skill is read, and its PowerLevel (melee) or AccuracyLevel (missile) is set
    /// to <c>bar</c> (0..1, default 1.0 = a full bar); both are restored afterwards, so the Player is unchanged
    /// when this returns. A full power bar gives PowerMod 1.5 (Player.GetPowerMod, "PowerLevel + 0.5"); a full
    /// accuracy bar gives AccuracyMod 1.6 (Player.GetAccuracyMod), which multiplies the missile attack skill.
    ///
    /// THE OPTIONAL TARGET makes the slayer bonus, the target's crit-resist rating and the caster elemental PvP
    /// halving exact (see CreatureAttackerSpecs for the same contract). Pass the monster.
    ///
    /// Shared invariants with CreatureAttackerSpecs come from AttackerTerms and are identical: PreMitigationMod
    /// composition, crit handling (CritDamageMod for physical, MagicCritDamageMod for magic), the crit clamp.
    ///
    /// WHAT A PLAYER SPEC OMITS - each is a post-mitigation or stateful player term the analytic path cannot
    /// carry, so the table must disclose it. Direction: all of them can only RAISE the player's real damage.
    /// - Class abilities on outgoing damage (Player.ApplyOutgoingDamageClassAbilities: Poison Weapon and the like),
    ///   standalone equipment mods (ApplyEquipmentModOutgoingDamage) and Tier B weapon mods applied after
    ///   mitigation (ApplyWeaponModOutgoingDamage: Ambush and the leeches) - all in Player.DamageTarget. On the
    ///   spell path, GetClassAbilitySpellDamageMod and GetWeaponModSpellDamageMod (SpellProjectile.cs), which are
    ///   not pure reads (they grant stacks), so they are not called.
    /// - Elemental Proficiency (Archmage T3) on a player war spell, each of which can only RAISE damage: Fire,
    ///   x (1 + fire magnitude) on the final damage (SpellProjectile.CalculateDamage, GetElementalProficiencyDamageMod);
    ///   Pierce, which on each hit takes the larger of a resistance ignore folded into the Arcane Hollow term
    ///   or a flat x (1 + pierce magnitude) (Player_ArchmageResistance.cs GetArchmageInnateOverride and
    ///   ElementalProficiencySpellMath.DecideSpellInnate, with the same Inversion/Hollow override the spec does
    ///   not model); and Slash, + 5/10/15 points of spell crit chance (SpellProjectile.CalculateDamage,
    ///   GetElementalProficiencySlashCritBonus). Fire and Pierce scale with War Magic affinity and the Elemental
    ///   Rend gear amplifier; Slash does not. The Lightning damage-TAKEN multiplier is a defender term and IS
    ///   mirrored (MonsterDefenderProfile, GetElementalLightningMod).
    /// - Recklessness and Sneak Attack (neutral), cleave and multi-shot extra targets, Dirty Fighting, procs.
    /// - Missile: the projectile is approximated by the EQUIPPED ammo item; the engine builds a fresh projectile
    ///   from the ammo's wcid.
    /// - The crit-branch damage-rating recomposition (crit damage rating), as on every spec.
    /// </summary>
    public static class PlayerAttackerSpecs
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(PlayerAttackerSpecs));

        public static AttackerSpec FromPlayer(Player player, PlayerAttackMode mode, uint spellId = 0, Creature target = null, float bar = 1.0f)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (float.IsNaN(bar) || bar < 0.0f || bar > 1.0f)
                throw new ArgumentOutOfRangeException(nameof(bar), "The power or accuracy bar must be between 0 and 1.");

            switch (mode)
            {
                case PlayerAttackMode.Melee:
                    return SimulatorStance.With(player, CombatMode.Melee, () => WithBar(player, isMissile: false, bar, () => Melee(player, target)));

                case PlayerAttackMode.Missile:
                    return SimulatorStance.With(player, CombatMode.Missile, () => WithBar(player, isMissile: true, bar, () => Missile(player, target)));

                case PlayerAttackMode.Magic:
                    if (spellId == 0)
                        throw new ArgumentException("A spell id is required for PlayerAttackMode.Magic.", nameof(spellId));

                    var spell = new Spell(spellId);

                    if (spell.NotFound)
                        throw new ArgumentException($"Spell {spellId} was not found.", nameof(spellId));

                    CreatureAttackerSpecs.RequireWarOrVoidProjectile(spell);

                    return SimulatorStance.With(player, CombatMode.Magic, () => CreatureAttackerSpecs.SpellFromValues(
                        player, $"{player.Name} {spell.Name}", spell.School, spell.DamageType, spell.MinDamage, spell.MaxDamage, spell.Power, target));

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        /// <summary>
        /// The attack mode the character fights with. Decided by SKILL, not by what is in hand: the mode of
        /// PlayerLoadout.PrimaryAttackSkill (its highest trained or specialized attack skill). A snapshot records
        /// the logout loadout, which is often a buffing wand on a character that fights with a sword. Only when
        /// no attack skill is trained does it fall back to what is wielded (ProfileBuilder.DefenderCombatStance).
        /// </summary>
        public static PlayerAttackMode PrimaryAttackMode(Player player)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            var skill = PlayerLoadout.PrimaryAttackSkill(player);

            if (skill != null)
                return PlayerLoadout.AttackSkills[skill.Value];

            switch (ProfileBuilder.DefenderCombatStance(player))
            {
                case CombatMode.Magic: return PlayerAttackMode.Magic;
                case CombatMode.Missile: return PlayerAttackMode.Missile;
                default: return PlayerAttackMode.Melee;
            }
        }
        /// <summary>
        /// The war or void PROJECTILE spell the character would cast, or 0 when it has none. Candidates are the
        /// spells on its spell bars (Character.CharacterPropertiesSpellBar, what it actually casts from); only
        /// when no bar spell qualifies, its whole spellbook (Biota.PropertiesSpellBook). Among the candidates:
        /// 1. the school with the higher school skill Current (a war mage casts war even if void is on a bar),
        /// 2. then the highest spell Level,
        /// 3. then the highest mean damage per projectile ((MinDamage + MaxDamage) / 2),
        /// 4. then the lowest id, so the choice is deterministic.
        /// Damage over time spells and life projectiles are excluded; only war and void projectiles are modelled.
        /// Reads Spell, so it needs the portal dat and the world database.
        /// </summary>
        public static uint BestWarOrVoidProjectile(Player player)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            var fromBars = player.Character?.CharacterPropertiesSpellBar?.Select(s => s.SpellId) ?? Enumerable.Empty<uint>();
            var best = Best(player, fromBars);

            if (best == null)
            {
                var fromBook = player.Biota.PropertiesSpellBook?.Keys.Select(k => (uint)k) ?? Enumerable.Empty<uint>();
                best = Best(player, fromBook);
            }

            return best?.Id ?? 0;
        }

        private static Spell Best(Player player, IEnumerable<uint> spellIds)
        {
            return spellIds
                .Distinct()
                .Select(id => new Spell(id))
                .Where(s => !s.NotFound
                    && (s.School == MagicSchool.WarMagic || s.School == MagicSchool.VoidMagic)
                    && s.IsProjectile
                    && s.MetaSpellType != SpellType.LifeProjectile
                    && !s.IsDamageOverTime)
                .OrderByDescending(s => player.GetCreatureSkill(s.School).Current)
                .ThenByDescending(s => s.Level)
                .ThenByDescending(s => (s.MinDamage + s.MaxDamage) / 2.0)
                .ThenBy(s => s.Id)
                .FirstOrDefault();
        }

        /// <summary>
        /// The loadout the character fights with: among PlayerLoadout.Candidates for its PrimaryAttackSkill (every
        /// possessed, wieldable weapon of that skill, plus unarmed for melee and wandless for magic), the one whose
        /// spec has the highest expected damage per landed hit before mitigation (ExpectedPreMitigationDamage),
        /// then the higher attack skill, then the item name. Magic uses BestWarOrVoidProjectile. Null when no
        /// attack skill is trained, or when the skill is missile and no launcher has wieldable ammo.
        /// </summary>
        public static PlayerLoadout PrimaryLoadout(Player player, Creature target = null, float bar = 1.0f)
            => RankedLoadouts(player, target, bar).Select(x => x.Loadout).FirstOrDefault();

        /// <summary>
        /// Every candidate loadout of the primary attack skill with its spec, best first, by the PrimaryLoadout
        /// ordering. Empty when PrimaryLoadout would be null.
        /// </summary>
        internal static List<(PlayerLoadout Loadout, AttackerSpec Spec)> RankedLoadouts(Player player, Creature target, float bar)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            var skill = PlayerLoadout.PrimaryAttackSkill(player);

            if (skill == null)
                return new List<(PlayerLoadout, AttackerSpec)>();

            var mode = PlayerLoadout.AttackSkills[skill.Value];
            var spellId = mode == PlayerAttackMode.Magic ? BestWarOrVoidProjectile(player) : 0u;

            if (mode == PlayerAttackMode.Magic && spellId == 0)
                return new List<(PlayerLoadout, AttackerSpec)>();

            // A candidate whose spec cannot be built (code review 2026-10-23: one malformed item must not fail the whole
            // ranking, and with it PrimaryLoadout, BuildDefenderProfile and @simhit) is dropped and logged.
            var built = new List<(PlayerLoadout Loadout, AttackerSpec Spec)>();

            foreach (var l in PlayerLoadout.Candidates(player, skill.Value, spellId))
            {
                try
                {
                    built.Add((l, l.With(player, () => FromPlayer(player, l.Mode, l.SpellId, target, bar))));
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    log.Warn($"[SIM] {player.Name}: loadout candidate '{l}' dropped, its attacker spec could not be built: {ex.Message}");
                }
            }

            return built
                .OrderByDescending(x => ExpectedPreMitigationDamage(x.Spec))
                .ThenByDescending(x => x.Spec.AttackSkill)
                .ThenBy(x => x.Loadout.Weapon?.Name ?? "", StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// The swings the character's primary attack alternates between (owner ruling 2026-10-23). One entry,
        /// the Primary spec, except for a DUAL WIELDER: a melee primary whose Dual Wield skill is trained or
        /// specialized and who possesses at least two wieldable ONE-HANDED weapons of its attack skill gets two
        /// entries - the best and the second-best of those weapons, each read as a main-hand swing - and the
        /// matchup takes the MEAN of their per-swing results (hit chance and damage each), which is what
        /// alternating one weapon in each hand averages to.
        ///
        /// APPROXIMATIONS, disclosed: (1) the off-hand swing's attack skill is capped by the engine at
        /// min(weapon skill, Dual Wield) (Player_Combat.cs, GetCurrentWeaponSkill); each entry here is read as a
        /// main-hand swing, so the cap is not applied - exact whenever Dual Wield is at or above the weapon skill,
        /// which <see cref="OffHandSkillCapBinds"/> reports. (2) Dual wield's 20% faster swing animation and power
        /// bar refill are a rate, not a per-swing term, and are not in a per-swing table.
        /// </summary>
        public static IReadOnlyList<(PlayerLoadout Loadout, AttackerSpec Spec)> PrimarySwings(Player player, Creature target = null, float bar = 1.0f)
        {
            var ranked = RankedLoadouts(player, target, bar);

            if (ranked.Count == 0)
                return new List<(PlayerLoadout, AttackerSpec)> { (null, Primary(player, target, bar)) };

            var best = ranked[0];

            if (best.Loadout.Mode != PlayerAttackMode.Melee || !IsOneHanded(best.Loadout.Weapon) || !DualWieldTrained(player))
                return new List<(PlayerLoadout, AttackerSpec)> { best };

            var second = ranked.Skip(1).FirstOrDefault(x => IsOneHanded(x.Loadout.Weapon) && x.Loadout.Weapon.Guid != best.Loadout.Weapon.Guid);

            if (second.Loadout == null)
                return new List<(PlayerLoadout, AttackerSpec)> { best };

            return new List<(PlayerLoadout, AttackerSpec)> { best, second };
        }

        /// <summary>True when the engine's off-hand cap min(weapon skill, Dual Wield) would lower the attack skill.</summary>
        public static bool OffHandSkillCapBinds(Player player)
        {
            var skill = PlayerLoadout.PrimaryAttackSkill(player);

            return skill != null && player.GetCreatureSkill(Skill.DualWield).Current < player.GetCreatureSkill(skill.Value).Current;
        }

        private static bool DualWieldTrained(Player player)
        {
            var dualWield = player.GetCreatureSkill(Skill.DualWield);
            return dualWield.AdvancementClass == SkillAdvancementClass.Trained || dualWield.AdvancementClass == SkillAdvancementClass.Specialized;
        }

        private static bool IsOneHanded(WorldObject weapon)
            => weapon is MeleeWeapon && (weapon.ValidLocations & EquipMask.TwoHanded) == 0;
        /// <summary>
        /// The character's primary attack: FromPlayer under PrimaryLoadout, so the weapon is the one it fights
        /// with even when the snapshot left something else in hand. Falls back to FromPlayer in the wield-based
        /// mode when PrimaryLoadout is null (for Magic that needs a war or void projectile; throws
        /// InvalidOperationException when there is none).
        /// </summary>
        public static AttackerSpec Primary(Player player, Creature target = null, float bar = 1.0f)
        {
            var loadout = PrimaryLoadout(player, target, bar);

            if (loadout != null)
                return loadout.With(player, () => FromPlayer(player, loadout.Mode, loadout.SpellId, target, bar));

            var mode = PrimaryAttackMode(player);

            if (mode != PlayerAttackMode.Magic)
                return FromPlayer(player, mode, target: target, bar: bar);

            var spellId = BestWarOrVoidProjectile(player);

            if (spellId == 0)
                throw new InvalidOperationException($"{player.Name} has no war or void projectile spell on a bar or in the spellbook.");

            return FromPlayer(player, PlayerAttackMode.Magic, spellId, target, bar);
        }

        /// <summary>
        /// Expected damage per landed hit BEFORE the defender's mitigation, the ranking key PrimaryLoadout uses.
        /// Physical: PreMitigationMod * ((1 - crit) * mean base + crit * max base * CritDamageMod). Magic:
        /// PreMitigationMod * (mean base + MagicSkillBonus + crit * max base * 0.5 * MagicCritDamageMod), the
        /// shapes CombatSimulator.Analytic and AnalyticMagic use. Hit chance is not in it.
        /// </summary>
        public static float ExpectedPreMitigationDamage(AttackerSpec spec)
        {
            var mean = (spec.BaseDamageMin + spec.BaseDamageMax) / 2.0f;

            if (spec.CombatType == CombatType.Magic)
                return spec.PreMitigationMod * (mean + spec.MagicSkillBonus + spec.CritChance * spec.BaseDamageMax * 0.5f * spec.MagicCritDamageMod);

            return spec.PreMitigationMod * ((1.0f - spec.CritChance) * mean + spec.CritChance * spec.BaseDamageMax * spec.CritDamageMod);
        }
        private static T WithBar<T>(Player player, bool isMissile, float bar, Func<T> body)
        {
            var previous = isMissile ? player.AccuracyLevel : player.PowerLevel;

            if (isMissile)
                player.AccuracyLevel = bar;
            else
                player.PowerLevel = bar;

            try
            {
                return body();
            }
            finally
            {
                if (isMissile)
                    player.AccuracyLevel = previous;
                else
                    player.PowerLevel = previous;
            }
        }

        /// <summary>
        /// Player melee, DamageEvent.DoCalculateDamage with a player attacker: the damage source is the equipped
        /// melee weapon (Player_Melee.cs passes it to DamageTarget), or the player itself when unarmed, in which
        /// case GetBaseDamageMod reads the hand armor for a punch. An unarmed player is measured as punching.
        /// </summary>
        private static AttackerSpec Melee(Player player, Creature target)
        {
            var weapon = player.GetEquippedMeleeWeapon();

            var previousAttackType = player.AttackType;

            if (weapon == null)
                player.AttackType = AttackType.Punch;

            try
            {
                WorldObject damageSource = weapon ?? (WorldObject)player;

                // DamageEvent.GetBaseDamage(Player): damage type first, then the base damage mod, then the weapon
                // mods' base-damage folds
                var damageType = player.GetDamageType(false, CombatType.Melee);

                var baseDamageMod = player.GetBaseDamageMod(damageSource);

                WeaponModCombat.ApplyBaseDamageMods(baseDamageMod, weapon, player);

                return Physical(player, weapon, CombatType.Melee, damageType, baseDamageMod, target, $"{player.Name} melee ({weapon?.Name ?? "unarmed"})");
            }
            finally
            {
                player.AttackType = previousAttackType;
            }
        }

        /// <summary>
        /// Player missile, DamageEvent.GetBaseDamage(Player) for an ItemType.MissileWeapon damage source. Weapon is
        /// "ProjectileLauncher ?? ProjectileAmmo" as for a monster; the damage source is the ammo (see the class
        /// summary for the projectile approximation). In that order, as the engine applies them: the ammo's
        /// W_DamageType (prismatic Base arrows take the launcher's type, else Pierce); GetBaseDamageMod; the
        /// launcher's own Damage as a DamageBonus for a MissileLauncher; the launcher's missile elemental bonus;
        /// the Tier B base-damage weapon mods.
        /// </summary>
        private static AttackerSpec Missile(Player player, Creature target)
        {
            var missileWeapon = player.GetEquippedMissileWeapon();
            var ammo = player.GetMissileAmmo();

            if (missileWeapon == null || ammo == null)
                throw new InvalidOperationException($"{player.Name} has no missile weapon with ammo equipped.");

            var weapon = player.GetEquippedMissileLauncher() ?? ammo;

            var damageType = ammo.W_DamageType;

            if (damageType == DamageType.Base)
                damageType = weapon != null && weapon.W_DamageType != DamageType.Undef ? weapon.W_DamageType : DamageType.Pierce;

            var baseDamageMod = player.GetBaseDamageMod(ammo);

            if (weapon.WeenieType == WeenieType.MissileLauncher)
                baseDamageMod.DamageBonus += weapon.Damage ?? 0;

            baseDamageMod.ElementalBonus = WorldObject.GetMissileElementalDamageBonus(weapon, player, damageType);

            WeaponModCombat.ApplyBaseDamageMods(baseDamageMod, weapon, player);

            return Physical(player, weapon, CombatType.Missile, damageType, baseDamageMod, target, $"{player.Name} missile ({weapon.Name} + {ammo.Name})");
        }

        private static AttackerSpec Physical(Player player, WorldObject weapon, CombatType combatType, DamageType damageType, BaseDamageMod baseDamageMod, Creature target, string label)
        {
            var attackSkill = player.GetCreatureSkill(player.GetCurrentWeaponSkill());

            return new AttackerSpec
            {
                Label = label,

                CombatType = combatType,
                DamageType = damageType,
                AttackHeight = player.AttackHeight ?? AttackHeight.Medium,

                // Player.GetEffectiveAttackSkill: skill * accuracy mod * offense mod
                AttackSkill = player.GetEffectiveAttackSkill(),

                BaseDamageMin = baseDamageMod.MinDamage,
                BaseDamageMax = baseDamageMod.MaxDamage,

                CritChance = AttackerTerms.PhysicalCritChance(weapon, player, attackSkill, target),
                CritDamageMod = AttackerTerms.PhysicalCritDamageMod(weapon, player, attackSkill, target),

                PreMitigationMod = AttackerTerms.ComposePreMitigationMod(
                    player.GetAttributeMod(weapon),
                    player.GetPowerMod(weapon),
                    WorldObject.GetWeaponCreatureSlayerModifier(weapon, player, target),
                    AttackerTerms.PhysicalDamageRatingMod(player, weapon)),

                ArmorRendingMod = AttackerTerms.ArmorIgnoreMod(weapon, player, attackSkill),
                IgnoreShieldMod = player.GetIgnoreShieldMod(weapon),

                IgnoreMagicArmor = HollowMath.Resolve(weapon, player, PropertyBool.IgnoreMagicArmor) > 0.0,
                IgnoreMagicResist = HollowMath.Resolve(weapon, player, PropertyBool.IgnoreMagicResist) > 0.0,

                WeaponResistanceMod = WorldObject.GetWeaponResistanceModifier(weapon, player, attackSkill, damageType),
                IgnoreAllArmor = weapon != null && weapon.HasImbuedEffect(ImbuedEffectType.IgnoreAllArmor),

                WithinShieldArc = true,
                AttackerIsPlayer = true,
            };
        }
    }
}
