using ACE.Entity.Enum;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// Classifies a weapon into one of a fixed set of lowercase tokens for the web app's "Weapon
    /// Type" market filter. Pure and static - it takes the primitives a snapshot already has (no
    /// WorldObject/Weenie dependency), so MarketSnapshot's two projections and this class's own tests
    /// call it identically.
    ///
    /// The token set (light/heavy/finesse/sword/axe/mace/spear/dagger/staff/etc) is a CONTRACT shared
    /// with the web app: do not add, rename, or reorder tokens without updating both sides.
    ///
    /// WHY the retired melee skills (sword/axe/mace/spear/dagger/staff/unarmed) get their own buckets
    /// instead of folding into light/heavy/finesse: a weapon carrying one of those retired skills
    /// resolves its actual combat skill at wield time to the wielder's HIGHEST melee skill
    /// (WorldObject.ConvertToMoASkill returns player.GetHighestMeleeSkill() for
    /// SkillExtensions.RetiredMelee), so there is no single fixed Light/Heavy/Finesse answer for a
    /// listing captured with no wielder in context. Loot-generated melee weapons always carry a
    /// modern skill instead - ACE.Server.Factories.Enum.MeleeWeaponSkill only has
    /// HeavyWeapons/LightWeapons/FinesseWeapons/TwoHandedCombat as options - so the retired buckets
    /// only ever apply to static vendor and quest stock, never to generated loot.
    /// </summary>
    public static class MarketWeaponClass
    {
        /// <summary>
        /// Classifies a weapon from its own properties. Returns null when the item is not a weapon
        /// (or its weapon data doesn't resolve to any known bucket). Rules apply in order; the first
        /// match wins.
        /// </summary>
        public static string Classify(WeenieType type, int? weaponSkill, int? ammoType, int? weaponType)
        {
            // Rule 1: Casters never carry a resolvable skill, so they get their own bucket first.
            if (type == WeenieType.Caster)
                return "caster";

            // Rule 2: MUST run before the AmmoType rule below - an arrow itself carries an AmmoType
            // (Arrow) and would otherwise be misclassified as a bow. Ammunition weenies carry no
            // WeaponSkill in the world DB, so nothing downstream would catch it either.
            if (type == WeenieType.Ammunition)
                return "ammunition";

            // Rule 3: a launcher's ammo type is the true signal for bow/crossbow/atlatl, including
            // the crystal and chorizite ammo variants.
            if (type == WeenieType.MissileLauncher)
            {
                if (ammoType.HasValue)
                {
                    var ammo = (AmmoType)ammoType.Value;

                    if ((ammo & (AmmoType.Arrow | AmmoType.ArrowCrystal | AmmoType.ArrowChorizite)) != 0)
                        return "bow";

                    if ((ammo & (AmmoType.Bolt | AmmoType.BoltCrystal | AmmoType.BoltChorizite)) != 0)
                        return "crossbow";

                    if ((ammo & (AmmoType.Atlatl | AmmoType.AtlatlCrystal | AmmoType.AtlatlChorizite)) != 0)
                        return "atlatl";
                }

                // No ammo type, or one that matches nothing above: fall through to the skill/weapon
                // type rules below (a retired Bow/Crossbow skill still resolves the launcher).
            }
            else if (type == WeenieType.Missile)
            {
                // Rule 4: a thrown weapon (javelin, throwing axe, etc), as opposed to its ammunition.
                return "thrown";
            }

            // Rule 5: the modern and retired melee/missile skills.
            if (weaponSkill.HasValue)
            {
                switch ((Skill)weaponSkill.Value)
                {
                    case Skill.LightWeapons:
                        return "light";
                    case Skill.HeavyWeapons:
                        return "heavy";
                    case Skill.FinesseWeapons:
                        return "finesse";
                    case Skill.TwoHandedCombat:
                        return "two_handed";
                    case Skill.UnarmedCombat:
                        return "unarmed";
                    case Skill.Sword:
                        return "sword";
                    case Skill.Axe:
                        return "axe";
                    case Skill.Mace:
                        return "mace";
                    case Skill.Spear:
                        return "spear";
                    case Skill.Dagger:
                        return "dagger";
                    case Skill.Staff:
                        return "staff";
                    case Skill.Bow:
                        return "bow";
                    case Skill.Crossbow:
                        return "crossbow";
                    case Skill.ThrownWeapon:
                        return "thrown";

                    case Skill.MissileWeapons when type != WeenieType.MissileLauncher:
                        // Matches the web app's legacy fallback classifier, which reads only
                        // pre-weapon_class listings' appraisal panel lines (no WeenieType visible) and
                        // maps a bare "Skill: Missile Weapons" line with no ammunition line to
                        // "thrown" - the throwable tavern crockery case (chalice, tankard, etc: all
                        // WeenieType.Generic, ItemType.MissileWeapon, WeaponSkill = MissileWeapons, no
                        // AmmoType). Without this, such an item would silently change bucket the
                        // moment this snapshot starts winning over the fallback - which is when it is
                        // re-listed, or when an operator's /marketbackfill run re-projects it.
                        //
                        // GUARDED against WeenieType.MissileLauncher: a launcher can reach here only
                        // by falling through rule 3 with an absent/unrecognized AmmoType, and a real
                        // bow/crossbow/atlatl answering "thrown" here would be worse than the current
                        // fallthrough to rule 6, which can still resolve it from a retired WeaponType.
                        return "thrown";

                    // Skill.MissileWeapons on a MissileLauncher: no mapping, falls through to rule 6.
                }
            }

            // Rule 6: PropertyInt.WeaponType, as a last resort. ACE's own GetWeaponType prefers this
            // property over the weapon skill, but it is effectively unpopulated in the world DB, so
            // here it only matters when rule 5 found nothing.
            if (weaponType.HasValue)
            {
                switch ((WeaponType)weaponType.Value)
                {
                    case WeaponType.Sword:
                        return "sword";
                    case WeaponType.Axe:
                        return "axe";
                    case WeaponType.Mace:
                        return "mace";
                    case WeaponType.Spear:
                        return "spear";
                    case WeaponType.Dagger:
                        return "dagger";
                    case WeaponType.Staff:
                        return "staff";
                    case WeaponType.Bow:
                        return "bow";
                    case WeaponType.Crossbow:
                        return "crossbow";
                    case WeaponType.Thrown:
                        return "thrown";
                    case WeaponType.TwoHanded:
                        return "two_handed";
                    case WeaponType.Unarmed:
                        return "unarmed";
                    case WeaponType.Magic:
                        return "caster";

                    // WeaponType.Undef: no mapping.
                }
            }

            return null;
        }

        /// <summary>Display label for a Classify token, or null for an unknown/unrecognized token.</summary>
        public static string Label(string weaponClass)
        {
            switch (weaponClass)
            {
                case "light":
                    return "Light Weapons";
                case "heavy":
                    return "Heavy Weapons";
                case "finesse":
                    return "Finesse Weapons";
                case "two_handed":
                    return "Two Handed";
                case "unarmed":
                    return "Unarmed";
                case "sword":
                    return "Sword";
                case "axe":
                    return "Axe";
                case "mace":
                    return "Mace";
                case "spear":
                    return "Spear";
                case "dagger":
                    return "Dagger";
                case "staff":
                    return "Staff";
                case "bow":
                    return "Bow";
                case "crossbow":
                    return "Crossbow";
                case "atlatl":
                    return "Atlatl";
                case "thrown":
                    return "Thrown";
                case "ammunition":
                    return "Ammunition";
                case "caster":
                    return "Caster";
                default:
                    return null;
            }
        }
    }
}
