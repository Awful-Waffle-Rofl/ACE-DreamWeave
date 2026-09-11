using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WeaponMods;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Quick Refresh (WeaponModId.QuickRefresh): a cast-speed multiplier on the player's own self-cast
    /// beneficial enchantments. Split into its own file rather than added to Player_WeaponMods.cs, which a
    /// sibling agent owns for this same phase.
    ///
    /// Composed multiplicatively into Player_Magic's castSpeedMultiplier, alongside
    /// <see cref="Player.ApplyClassAbilityCastSpeed"/>, at both of that method's call sites
    /// (CreatePlayerSpell(WorldObject, ...) and CreatePlayerSpell(uint)) - see the class remarks on
    /// Player_WeaponMods.cs for why a Tier B row like this is hand-wired at its one call site rather than
    /// dispatched. Called exactly once per committed cast, mirroring ApplyClassAbilityCastSpeed itself (that
    /// call advances Nether Rush stacks as a side effect, so it is never called a second time to avoid double
    /// counting).
    ///
    /// DELIBERATELY NO CEILING (repo-owner accepted, 2026-08-17): unlike the attack-speed axis, which is
    /// clamped against class_ability_attack_speed_ceiling, cast speed from Quick Refresh is uncapped. It only
    /// ever reduces downtime on a player's own beneficial buffs/heals, never on an offensive cast against a
    /// target, so there is no axis being pushed past a shared ceiling here.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// Quick Refresh's cast-speed multiplier (1.0 = none). Applies only to the player's own self-cast
        /// beneficial enchantments: <paramref name="target"/> must be null (untargeted/self cast) or this
        /// player (explicit self-target), the spell must be Beneficial, and its MetaSpellType must be
        /// Enchantment - a war/void bolt, a debuff, or a heal cast on someone else all read 1.0 unconditionally.
        ///
        /// Reads the equipped WAND, not the melee/missile weapon - see Player_WeaponMods.cs's
        /// GetCasterOnlyModValue for why the two accessors are separate.
        /// </summary>
        public float GetWeaponModCastSpeedMod(Spell spell, WorldObject target)
        {
            if (spell == null || !spell.IsBeneficial || spell.MetaSpellType != SpellType.Enchantment)
                return 1.0f;

            if (target != null && target != this)
                return 1.0f;

            var quickRefresh = GetCasterOnlyModValue(WeaponModId.QuickRefresh);

            if (quickRefresh <= 0.0)
                return 1.0f;

            return (float)(1.0 + quickRefresh);
        }
    }
}
