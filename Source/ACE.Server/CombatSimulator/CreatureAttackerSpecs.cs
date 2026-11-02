using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// Builds AttackerSpecs for a NON-PLAYER creature - typically one constructed in memory with
    /// WorldObjectFactory.CreateNewWorldObject and never placed in the world - from the same production functions
    /// the engine's monster attack paths call. Melee is the builder @simhit has always used, moved here unchanged.
    ///
    /// Every builder puts the creature into the stance its attack is made from first (AttackerTerms
    /// .EnterCreatureStance) and leaves it there. The shared invariants - PreMitigationMod composition, crit
    /// handling, crit-chance clamp - come from AttackerTerms and are identical across Melee, Missile and Spell.
    ///
    /// THE OPTIONAL DEFENDER. Pass the creature being attacked to make the three defender-dependent attacker
    /// terms exact: the weapon slayer bonus (compares the weapon's SlayerCreatureType with the defender's
    /// CreatureType), the defender's crit-resist rating inside the crit chance, and the PvP-only Crippling Blow
    /// lever. Without one, slayer is its neutral 1.0 and the crit-resist term is divided back out, which is what
    /// @simhit measures against a roster of profiles. Nothing is read off the defender beyond those.
    ///
    /// WHAT EVERY SPEC HERE LEAVES AT ITS DEFAULT, and why (the table that consumes these must disclose it):
    /// - Recklessness and Sneak Attack fold into the damage rating at their neutral 1.0: both read the DEFENDER
    ///   or live positions (Creature_Combat.cs, GetRecklessnessMod and GetSneakAttackMod).
    /// - The crit branch's damage-rating recomposition (crit damage rating added, recklessness dropped) is not
    ///   modelled: AttackerSpec carries one PreMitigationMod for both branches.
    /// - The defender's Critical Defense augmentation and crit damage resist rating are not applied.
    /// - AttackHeight falls back to Medium, the engine's own fallback (DamageEvent.cs, DoCalculateDamage). A live
    ///   monster sets it per attack from its combat maneuver table, which needs the dats and a target.
    /// - WithinShieldArc stays true: the defender is credited with its shield in full.
    /// - Hollow (IgnoreMagicArmor / IgnoreMagicResist) is on or off: any intensity above 0 is simulated as full.
    /// - Overpower, monster combat effects (ApplyOutgoingHitMonsterEffects) and the defender's class-ability
    ///   avoidance (Shield Block, Parry) are outside the spec entirely.
    /// </summary>
    public static class CreatureAttackerSpecs
    {
        /// <summary>
        /// The melee spec, mirroring the path the engine takes for a NON-player attacker:
        /// DamageEvent.DoCalculateDamage -> Creature.GetBaseDamage(attackPart) (Entity\DamageEvent.cs,
        /// WorldObjects\Monster_Melee.cs).
        ///
        /// The wielded melee weapon IS the damage source when there is one; otherwise base damage is AVERAGED over
        /// the combat body parts GetAttackPart draws from uniformly (DVal non-zero, not Breath), which is the
        /// expected base damage and makes a DVal scale factor apply uniformly to every part. Damage TYPE follows the engine's
        /// split (Monster_Combat.cs, GetDamageType(PropertiesBodyPart, ...)): the wielded MELEE weapon's type when
        /// there is one, otherwise the body part's type - a launcher or thrown weapon held without a melee weapon
        /// does not supply a melee type - and a multi-type body part is fixed on its first single flag, where the
        /// engine rolls one per swing.
        ///
        /// Crits are modelled: CritChance from GetWeaponCriticalChance with the defender crit-resist term handled
        /// as described on the class, clamped to [0, 1]; CritDamageMod with the +1.0 base and the Execution weapon
        /// mod folded in. An unimbued weaponless monster comes out at 2.0, the value CombatSimulatorParityTests
        /// pins against the engine.
        ///
        /// The creature is put into MELEE combat mode first, because GetEffectiveAttackSkill multiplies by
        /// GetWeaponOffenseModifier, which is 1.0 out of stance.
        /// </summary>
        public static AttackerSpec Melee(Creature creature, Creature defender = null)
        {
            if (creature == null)
                throw new ArgumentNullException(nameof(creature));

            AttackerTerms.EnterCreatureStance(creature, CombatMode.Melee);

            // Entity\DamageEvent.cs - the wielded melee weapon IS the damage source when there is one. The
            // Creature constructor already ran GenerateWieldList / EquipInventoryItems / GenerateWieldedTreasure,
            // so a creature that wields in play wields here too.
            var weapon = creature.GetEquippedMeleeWeapon();


            // the same CreatureSkill object the engine hands to every weapon-derived modifier. GetCreatureSkill
            // writes on read (it inserts a missing PropertiesSkill row), which lands only on this throwaway
            var attackSkill = creature.GetCreatureSkill(creature.GetCurrentWeaponSkill());

            float baseDamageMin;
            float baseDamageMax;
            DamageType damageType;

            if (weapon != null)
            {
                // WorldObjects\Monster_Melee.cs, GetBaseDamage
                var weaponDamage = weapon.GetDamageMod(creature);

                baseDamageMin = weaponDamage.MinDamage;
                baseDamageMax = weaponDamage.MaxDamage;
            }
            else
            {
                var parts = CombatAttackParts(creature);

                baseDamageMin = 0.0f;
                baseDamageMax = 0.0f;

                foreach (var part in parts)
                {
                    // Entity\BaseDamage.cs - MinDamage is MaxDamage * (1 - Variance)
                    baseDamageMin += part.DVal * (1.0f - part.DVar);
                    baseDamageMax += part.DVal;
                }

                if (parts.Count > 0)
                {
                    baseDamageMin /= parts.Count;
                    baseDamageMax /= parts.Count;
                }
            }

            // same condition as Monster.GetDamageType(PropertiesBodyPart, ...) for a melee hit
            if (weapon != null)
            {
                damageType = creature.GetDamageType(false, CombatType.Melee);
            }
            else
            {
                damageType = DamageType.Undef;

                foreach (var part in CombatAttackParts(creature))
                    damageType |= part.DType;

                if (damageType.IsMultiDamage())
                    damageType = FirstSingleDamageType(damageType);

                // the engine never returns Undef from a body part
                if (damageType == DamageType.Undef)
                    damageType = DamageType.Bludgeon;
            }

            return new AttackerSpec
            {
                Label = $"{creature.Name} (wcid {creature.WeenieClassId})",

                // Entity\DamageEvent.cs - a non-projectile damage source is Melee
                CombatType = CombatType.Melee,
                DamageType = damageType,
                AttackHeight = creature.AttackHeight ?? AttackHeight.Medium,

                AttackSkill = creature.GetEffectiveAttackSkill(),

                BaseDamageMin = baseDamageMin,
                BaseDamageMax = baseDamageMax,

                CritChance = AttackerTerms.PhysicalCritChance(weapon, creature, attackSkill, defender),
                CritDamageMod = AttackerTerms.PhysicalCritDamageMod(weapon, creature, attackSkill, defender),

                PreMitigationMod = AttackerTerms.ComposePreMitigationMod(
                    creature.GetAttributeMod(weapon),
                    creature.GetPowerMod(weapon),
                    WorldObject.GetWeaponCreatureSlayerModifier(weapon, creature, defender),
                    AttackerTerms.PhysicalDamageRatingMod(creature, weapon)),

                ArmorRendingMod = AttackerTerms.ArmorIgnoreMod(weapon, creature, attackSkill),
                IgnoreShieldMod = creature.GetIgnoreShieldMod(weapon),

                IgnoreMagicArmor = HollowMath.Resolve(weapon, creature, PropertyBool.IgnoreMagicArmor) > 0.0,
                IgnoreMagicResist = HollowMath.Resolve(weapon, creature, PropertyBool.IgnoreMagicResist) > 0.0,

                WeaponResistanceMod = WorldObject.GetWeaponResistanceModifier(weapon, creature, attackSkill, damageType),
                IgnoreAllArmor = weapon != null && weapon.HasImbuedEffect(ImbuedEffectType.IgnoreAllArmor),

                WithinShieldArc = true,
                AttackerIsPlayer = false,
            };
        }

        /// <summary>
        /// The missile spec: a wielded launcher plus equipped ammo, or a thrown weapon (which is its own ammo).
        /// Throws InvalidOperationException when the creature has no missile weapon or no ammo for it, because the
        /// engine's RangeAttack returns without firing in exactly that case (Monster_Missile.cs).
        ///
        /// What the engine resolves, and from where:
        /// - The damage source is the PROJECTILE, created from the ammo's wcid, and DamageEvent's Weapon is
        ///   "ProjectileLauncher ?? ProjectileAmmo" - the launcher for a bow, the thrown item itself otherwise
        ///   (Monster_Missile.cs LaunchMissile passes GetEquippedMissileLauncher(); Creature_Missile.cs
        ///   LaunchProjectile stores both). Power, attribute, slayer, rending, crit and weapon resistance all read
        ///   that Weapon.
        /// - Base damage is GetMissileDamage(), "ammo.GetDamageMod(this)", which takes its DamageMod (and the
        ///   wielder-aura terms) from GetEquippedWeapon() - normally the launcher, so the ammo's damage is scaled by
        ///   the launcher's DamageMod. If the creature ALSO wields a melee weapon, GetEquippedWeapon returns that
        ///   instead, and so does the engine. Read from the equipped ammo here, not from a fresh projectile; a
        ///   projectile is built from the ammo's weenie, so ammo with item-level edits or enchantments would differ.
        /// - The engine only reaches that base damage through GetAttackPart, which needs at least one combat body
        ///   part with a non-zero DVal (Monster_Melee.cs); with none, DamageEvent flags a GeneralFailure and the
        ///   arrow deals 0. Reproduced: such a creature gets a zero-damage spec, labelled as such.
        /// - Damage type is GetDamageType(false, CombatType.Missile): the ammo's W_DamageType for a launcher, the
        ///   thrown weapon's own otherwise.
        /// - Attack skill is GetEffectiveAttackSkill, whose offense modifier is 1.0 for any ranged weapon
        ///   (WorldObject_Weapon.cs, "HeartSeeker does not affect missile launchers"). Monsters have no accuracy bar.
        ///
        /// Not modelled beyond the class-level list: the projectile's flight (it can miss on geometry, or be
        /// reflected), and the player-only launcher Damage bonus and missile elemental bonus, which the engine
        /// applies to PLAYER attackers only (DamageEvent.GetBaseDamage(Player)).
        /// </summary>
        public static AttackerSpec Missile(Creature creature, Creature defender = null)
        {
            if (creature == null)
                throw new ArgumentNullException(nameof(creature));

            AttackerTerms.EnterCreatureStance(creature, CombatMode.Missile);

            var missileWeapon = creature.GetEquippedMissileWeapon();
            var ammo = creature.GetMissileAmmo();

            if (missileWeapon == null || ammo == null)
                throw new InvalidOperationException($"{creature.Name} (wcid {creature.WeenieClassId}) has no missile weapon with ammo; the engine would not fire.");

            var weapon = creature.GetEquippedMissileLauncher() ?? ammo;

            var attackSkill = creature.GetCreatureSkill(creature.GetCurrentWeaponSkill());

            var hasAttackPart = CombatAttackParts(creature).Count > 0;

            var baseDamage = creature.GetMissileDamage();

            var damageType = creature.GetDamageType(false, CombatType.Missile);

            var label = $"{creature.Name} (wcid {creature.WeenieClassId}) missile";

            if (!hasAttackPart)
                label += " [no combat body part with DVal: the engine fails every hit, 0 damage]";

            return new AttackerSpec
            {
                Label = label,

                CombatType = CombatType.Missile,
                DamageType = damageType,
                AttackHeight = creature.AttackHeight ?? AttackHeight.Medium,

                AttackSkill = creature.GetEffectiveAttackSkill(),

                BaseDamageMin = hasAttackPart ? baseDamage.MinDamage : 0.0f,
                BaseDamageMax = hasAttackPart ? baseDamage.MaxDamage : 0.0f,

                CritChance = AttackerTerms.PhysicalCritChance(weapon, creature, attackSkill, defender),
                CritDamageMod = AttackerTerms.PhysicalCritDamageMod(weapon, creature, attackSkill, defender),

                PreMitigationMod = AttackerTerms.ComposePreMitigationMod(
                    creature.GetAttributeMod(weapon),
                    creature.GetPowerMod(weapon),
                    WorldObject.GetWeaponCreatureSlayerModifier(weapon, creature, defender),
                    AttackerTerms.PhysicalDamageRatingMod(creature, weapon)),

                ArmorRendingMod = AttackerTerms.ArmorIgnoreMod(weapon, creature, attackSkill),
                IgnoreShieldMod = creature.GetIgnoreShieldMod(weapon),

                IgnoreMagicArmor = HollowMath.Resolve(weapon, creature, PropertyBool.IgnoreMagicArmor) > 0.0,
                IgnoreMagicResist = HollowMath.Resolve(weapon, creature, PropertyBool.IgnoreMagicResist) > 0.0,

                WeaponResistanceMod = WorldObject.GetWeaponResistanceModifier(weapon, creature, attackSkill, damageType),
                IgnoreAllArmor = weapon.HasImbuedEffect(ImbuedEffectType.IgnoreAllArmor),

                WithinShieldArc = true,
                AttackerIsPlayer = false,
            };
        }

        /// <summary>
        /// The spell spec for a war or void PROJECTILE spell cast by a monster, for CombatSimulator.AnalyticMagic.
        /// Reads the Spell (which needs the portal dat and the world database), refuses anything that is not a war
        /// or void projectile - life projectiles take a different damage formula - and hands the values to
        /// SpellFromValues.
        /// </summary>
        public static AttackerSpec Spell(Creature creature, uint spellId, Creature defender = null)
        {
            if (creature == null)
                throw new ArgumentNullException(nameof(creature));

            var spell = new Spell(spellId);

            if (spell.NotFound)
                throw new ArgumentException($"Spell {spellId} was not found.", nameof(spellId));

            RequireWarOrVoidProjectile(spell);

            return SpellFromValues(creature, $"{creature.Name} (wcid {creature.WeenieClassId}) {spell.Name}",
                spell.School, spell.DamageType, spell.MinDamage, spell.MaxDamage, spell.Power, defender);
        }

        internal static void RequireWarOrVoidProjectile(Spell spell)
        {
            if (spell.School != MagicSchool.WarMagic && spell.School != MagicSchool.VoidMagic)
                throw new ArgumentException($"Spell {spell.Id} ({spell.Name}) is {spell.School}; only war and void projectiles are modelled.");

            if (!spell.IsProjectile || spell.MetaSpellType == SpellType.LifeProjectile)
                throw new ArgumentException($"Spell {spell.Id} ({spell.Name}) is not a war/void projectile spell.");
        }

        /// <summary>
        /// The spell spec from explicit spell values, so the composition is testable without the portal dat. Shared
        /// by monster and player casters (PlayerAttackerSpecs calls it with the player already in Magic stance).
        ///
        /// Engine path (WorldObjects\SpellProjectile.cs, CalculateDamage then DamageTarget):
        /// - Hit chance: the projectile resist roll, MagicDefenseCheck(caster's school skill .Current, target's
        ///   GetEffectiveMagicDefense) - the plain skill check, no offense modifier (WorldObject_Magic.cs,
        ///   TryResistSpell). AttackSkill is that school skill's Current.
        /// - Base: a uniform roll over the spell's own MinDamage..MaxDamage. A MONSTER gets no skill bonus (the
        ///   bonus block is gated on the source being a Player); a player gets MagicSkillBonus.
        /// - The wand is the caster's GetEquippedWand() (Monster_Magic.cs CastSpell passes it as the projectile
        ///   launcher): crit chance, crit multiplier, elemental damage mod, slayer and weapon resistance mod all
        ///   read it. Most monsters have none, which gives the flat 5 percent crit and 1.0 for the rest.
        /// - PreMitigationMod: attribute and power are 1.0 (the spell path has neither), times slayer, the caster
        ///   elemental mod and the magic damage rating mod (AttackerTerms).
        /// - IgnoreMagicResist is false: the engine passes the PROJECTILE as the resistance attacker and a null
        ///   weapon, so a caster's or wand's hollow never reaches a spell; only a projectile weenie that carries it
        ///   directly would, and that is not modelled.
        ///
        /// Not modelled: the magic absorb term (shield or caster AbsorbMagicDamage on the defender), class-ability
        /// and weapon-mod spell multipliers (PvE player casters), Elemental Proficiency Fire, Pierce and Slash on a
        /// player caster (listed on PlayerAttackerSpecs), monster combat effect wards, and the crit-branch
        /// rating recomposition. Also not modelled: the Knot trait Overpowering Intent (a held Matron hive baton raises a
        /// player caster's War/Void skill for the resist check against non-players; KnotTraitCombat.OverpoweringIntentResistSkill).
        /// </summary>
        internal static AttackerSpec SpellFromValues(Creature caster, string label, MagicSchool school, DamageType damageType, int minDamage, int maxDamage, uint spellPower, Creature defender)
        {
            if (!(caster is Player))
                AttackerTerms.EnterCreatureStance(caster, CombatMode.Magic);

            var wand = caster.GetEquippedWand();
            var magicSkill = caster.GetCreatureSkill(school);

            var skillBonus = 0.0f;

            if (caster is Player && magicSkill.Current > spellPower)
                skillBonus = minDamage * ((magicSkill.Current - spellPower) / 1000.0f);

            return new AttackerSpec
            {
                Label = label,

                CombatType = CombatType.Magic,
                DamageType = damageType,
                AttackHeight = AttackHeight.Medium,

                AttackSkill = magicSkill.Current,

                BaseDamageMin = minDamage,
                BaseDamageMax = maxDamage,

                CritChance = AttackerTerms.MagicCritChance(wand, caster, magicSkill, defender),
                MagicCritDamageMod = AttackerTerms.MagicCritDamageMod(wand, caster, magicSkill, defender),

                MagicSkillBonus = skillBonus,

                PreMitigationMod = AttackerTerms.ComposePreMitigationMod(
                    1.0f,
                    1.0f,
                    WorldObject.GetWeaponCreatureSlayerModifier(wand, caster, defender),
                    AttackerTerms.MagicDamageRatingMod(caster, wand),
                    WorldObject.GetCasterElementalDamageModifier(wand, caster, defender, damageType)),

                WeaponResistanceMod = WorldObject.GetWeaponResistanceModifier(wand, caster, magicSkill, damageType),

                IgnoreMagicArmor = false,
                IgnoreMagicResist = false,

                AttackerIsPlayer = caster is Player,
            };
        }

        /// <summary>
        /// The combat body parts a monster's melee swing draws from - the same filter GetAttackPart applies for a
        /// normal attack, DVal non-zero and not the Breath part (WorldObjects\Monster_Melee.cs, GetAttackPart).
        /// </summary>
        internal static List<PropertiesBodyPart> CombatAttackParts(Creature creature)
        {
            if (creature.Biota.PropertiesBodyPart == null)
                return new List<PropertiesBodyPart>();

            return creature.Biota.PropertiesBodyPart
                .Where(b => b.Value.DVal != 0 && b.Key != CombatBodyPart.Breath)
                .Select(b => b.Value)
                .ToList();
        }

        /// <summary>
        /// The first single damage-type flag set on a composite, in the enum's own declaration order. Mirrors the
        /// loop in Creature.GetDamageType (WorldObjects\Creature_Combat.cs), minus its motion-dependent Thrust
        /// exception, which needs a live swing. Returns the input unchanged when no single flag is set.
        /// </summary>
        internal static DamageType FirstSingleDamageType(DamageType damageTypes)
        {
            foreach (DamageType damageType in Enum.GetValues(typeof(DamageType)))
            {
                if ((damageTypes & damageType) != 0 && !damageType.IsMultiDamage())
                    return damageType;
            }

            return damageTypes;
        }
    }
}
