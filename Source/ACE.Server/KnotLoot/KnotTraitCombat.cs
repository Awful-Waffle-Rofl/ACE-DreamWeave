using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.KnotLoot
{
    /// <summary>
    /// Pure composition helpers for the Knot traits read on the combat and magic paths. The values come from
    /// <see cref="KnotTraitValue.ResolveOn"/> through the Player accessors in Player_KnotTraits.cs; this class only
    /// says how they combine with the weapon-mod halves they share a site with.
    /// </summary>
    public static class KnotTraitCombat
    {
        /// <summary>
        /// Buff cast-speed multiplier (1.0 = none) from Quick Refresh and Mirrorglass Haste. The two fractions ADD
        /// before the single multiplier, so +100% halves the cast time and +200% cuts it to a third. A negative half
        /// is floored at zero, never subtracted from the other, so neither can slow a cast. Uncapped, as Quick
        /// Refresh already is (see Player_WeaponMods_Casting.cs).
        /// </summary>
        public static float CastSpeedMultiplier(double quickRefresh, double knotCastSpeed)
        {
            var total = Math.Max(0.0, quickRefresh) + Math.Max(0.0, knotCastSpeed);

            if (total <= 0.0)
                return 1.0f;

            return (float)(1.0 + total);
        }

        /// <summary>
        /// The cost-reduction fraction handed to WeaponModCombat.CostMultiplier: Efficiency plus Frugal Hand, summed
        /// before the single multiplier so the two stack additively. A negative Frugal fraction is floored at zero so
        /// it can never raise a cost; the sum is clamped to [0, 1] by CostMultiplier itself.
        /// </summary>
        public static double CostFraction(double efficiency, double frugal)
        {
            return efficiency + Math.Max(0.0, frugal);
        }

        /// <summary>
        /// Shield of the Rose: the whole-number armor level a Knot ShieldTrade shield adds to its BASE armor level,
        /// treated at every read site exactly as extra stored ArmorLevel (so enchantment-only scaling such as Hollow
        /// never touches it). Rounded the same way KnotTraitDisplay shows it, so the appraised and applied numbers
        /// agree. Zero when the shield has no trait, the mechanic is off, or the wielder is suppressed; also zero
        /// for a non-Player wielder, since traits apply only to Player wielders. A null wielder is the item-centric
        /// (appraisal) read and gets the full bonus. Read-only.
        /// </summary>
        public static int ShieldArmorBonus(WorldObject shield, WorldObject wielder)
        {
            if (wielder != null && !(wielder is Player))
                return 0;

            var effect = KnotTraitValue.ResolveOn(shield, KnotTraitId.ShieldTrade, wielder);

            if (!effect.Active)
                return 0;

            return (int)Math.Round(effect.Primary, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Shield of the Rose: subtracts the whole-number max-health penalty from an unsigned vital total, in long
        /// arithmetic so it can never wrap, floored at 1. A zero or negative penalty returns the total unchanged, so
        /// the penalty can never add health.
        /// </summary>
        public static uint SubtractMaxHealthPenalty(uint total, int penalty)
        {
            if (penalty <= 0)
                return total;

            return (uint)Math.Max(1L, (long)total - penalty);
        }

        /// <summary>
        /// Whether equipping or dequipping <paramref name="item"/> can move its wielder's maximum health through a Knot
        /// trait: the item carries the ShieldTrade trait property. Keyed on the property, never on wcid. Read-only.
        /// </summary>
        public static bool AffectsMaxHealth(WorldObject item)
        {
            return item != null && item.GetProperty(PropertyFloat.KnotTraitShieldTrade) != null;
        }

        /// <summary>
        /// Glass Turtle Bracelet: the signed whole-number rating one bracelet adds to <paramref name="kind"/>, by the wrist
        /// it is worn on. Left (WristWearLeft): +round(Primary) Damage Resist Rating, -round(Secondary) Damage Rating.
        /// Right (WristWearRight): +round(Primary) Damage Rating, -round(Secondary) Damage Resist Rating. Rounded the same
        /// way KnotTraitDisplay shows it, so the appraised and applied numbers agree. Zero when the trade is inactive or
        /// the location is anything but exactly one wrist (null, another slot, or the combined both-wrist mask). Pure.
        /// </summary>
        public static int WristRatingDelta(EquipMask? location, KnotTraitEffect e, KnotRatingKind kind)
        {
            if (!e.Active || location == null)
                return 0;

            var gain = (int)Math.Round(e.Primary, MidpointRounding.AwayFromZero);
            var cost = (int)Math.Round(e.Secondary, MidpointRounding.AwayFromZero);

            switch (location.Value)
            {
                case EquipMask.WristWearLeft:
                    return kind == KnotRatingKind.DamageResist ? gain : -cost;
                case EquipMask.WristWearRight:
                    return kind == KnotRatingKind.Damage ? gain : -cost;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Overpowering Intent (Matron hive batons): whether the trait reaches a spell's resist check. True only for a
        /// harmful War or Void Magic spell that does damage (a projectile, or a damage-over-time) cast at a target that
        /// is not a player. A non-damaging Void debuff, a beneficial spell, any other school, and every player target
        /// are excluded. Pure.
        /// </summary>
        public static bool OverpoweringIntentApplies(MagicSchool school, bool isHarmful, bool isProjectile, bool isDot, bool targetIsPlayer)
        {
            if (school != MagicSchool.WarMagic && school != MagicSchool.VoidMagic)
                return false;

            return isHarmful && (isProjectile || isDot) && !targetIsPlayer;
        }

        /// <summary>
        /// Overpowering Intent: the caster skill the resist check uses, round-away-from-zero of skill * (1 + X). X is
        /// floored at zero (a non-finite X reads as zero), so the trait can never lower a skill; the result is clamped
        /// to int.MaxValue. Pure.
        /// </summary>
        public static uint OverpoweringIntentSkill(uint skill, double x)
        {
            if (!double.IsFinite(x) || x <= 0.0)
                return skill;

            var value = Math.Round(skill * (1.0 + x), MidpointRounding.AwayFromZero);

            return (uint)Math.Clamp(value, 0.0, int.MaxValue);
        }

        /// <summary>
        /// Overpowering Intent: the whole resist-check decision, called from WorldObject.TryResistSpell between the
        /// difficulty assignment and the roll. Returns <paramref name="magicSkill"/> scaled by (1 + X) only when
        /// <paramref name="self"/> is a Player casting the spell itself (<paramref name="caster"/> is the player, not an
        /// item or cloak caster) and <see cref="OverpoweringIntentApplies"/> holds for the spell and target; X is read off
        /// the player's equipped wand. Otherwise returns <paramref name="magicSkill"/> unchanged. Never touches difficulty.
        /// </summary>
        public static uint OverpoweringIntentResistSkill(uint magicSkill, WorldObject self, WorldObject caster, WorldObject target, MagicSchool school, bool isHarmful, bool isProjectile, bool isDot)
        {
            if (self is not Player player || caster != self)
                return magicSkill;

            if (!OverpoweringIntentApplies(school, isHarmful, isProjectile, isDot, target is Player))
                return magicSkill;

            return OverpoweringIntentSkill(magicSkill, player.GetKnotOverpoweringIntentFraction());
        }
    }

    /// <summary>The two ratings the Glass Turtle Bracelet trades between.</summary>
    public enum KnotRatingKind
    {
        Damage,
        DamageResist,
    }
}
