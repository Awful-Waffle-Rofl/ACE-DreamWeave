using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// The armor table is keyed by body part, damage type, and the two ignore-magic booleans,
    /// because those booleans are ORs of attacker and weapon properties and so cannot be
    /// resolved until an attacker is known. Storing one row per combination lets any attacker
    /// be applied exactly. See Docs/CombatSimulator/DESIGN.md, "Exact factorization".
    /// </summary>
    public readonly struct ArmorKey
    {
        public readonly BodyPart BodyPart;
        public readonly DamageType DamageType;
        public readonly bool IgnoreMagicArmor;
        public readonly bool IgnoreMagicResist;

        public ArmorKey(BodyPart bodyPart, DamageType damageType, bool ignoreMagicArmor, bool ignoreMagicResist)
        {
            BodyPart = bodyPart;
            DamageType = damageType;
            IgnoreMagicArmor = ignoreMagicArmor;
            IgnoreMagicResist = ignoreMagicResist;
        }
    }

    /// <summary>
    /// Effective armor level with armorRendingMod fixed at 1.0. The attacker's rending value is
    /// applied at call time, before the armor curve, which is where the engine applies it.
    /// </summary>
    public readonly struct ArmorRow
    {
        public readonly float EffectiveArmorLevel;

        public ArmorRow(float effectiveArmorLevel)
        {
            EffectiveArmorLevel = effectiveArmorLevel;
        }
    }

    /// <summary>
    /// P0 is the protection mod already floored against natural resistance and scaled by the
    /// player's resistance augmentation. V0 is the raw vulnerability mod, un-floored, because
    /// the attacker's weapon resistance modifier floors it at call time.
    /// </summary>
    public readonly struct ResistancePair
    {
        public readonly float P0;
        public readonly float V0;

        public ResistancePair(float p0, float v0)
        {
            P0 = p0;
            V0 = v0;
        }
    }

    /// <summary>
    /// The shield table is keyed by damage type and the ignore-magic-armor boolean, for the same
    /// reason ArmorKey is: that boolean is an OR of attacker and weapon properties and so cannot
    /// be resolved until an attacker is known. It is not cosmetic on a shield either - it zeroes
    /// or rescales BOTH the shield's own armor-level enchantment
    /// (Source\ACE.Server\WorldObjects\Creature_Combat.cs:734-735) and its per-damage-type
    /// bane/lure enchantment (:746-747), so the two rows genuinely differ for any shield carrying
    /// either. Storing one row per combination lets any attacker be applied exactly. See
    /// Docs/CombatSimulator/DESIGN.md, "Exact factorization".
    /// </summary>
    public readonly struct ShieldKey
    {
        public readonly DamageType DamageType;
        public readonly bool IgnoreMagicArmor;

        public ShieldKey(DamageType damageType, bool ignoreMagicArmor)
        {
            DamageType = damageType;
            IgnoreMagicArmor = ignoreMagicArmor;
        }
    }

    /// <summary>
    /// Effective shield level already through the defender-side skill cap. The attacker's
    /// ignore-shield term multiplies this afterwards, which is the engine's order.
    /// </summary>
    public readonly struct ShieldRow
    {
        public readonly float CappedEffectiveLevel;

        public ShieldRow(float cappedEffectiveLevel)
        {
            CappedEffectiveLevel = cappedEffectiveLevel;
        }
    }

    /// <summary>
    /// A flat numeric snapshot of one character's defensive posture. Holds no Player, no
    /// WorldObject and no database handle, so it is safe to cache and to read from any thread.
    /// </summary>
    public class DefenderProfile
    {
        public uint CharacterGuid { get; init; }
        public string Label { get; init; }
        public int Level { get; init; }
        public System.DateTime SnapshotUtc { get; init; }

        public uint MaxHealth { get; init; }

        /// <summary>
        /// Effective defense skill per combat type, for the closed-form hit chance.
        ///
        /// THE MAGIC ENTRY IS MAGIC DEFENSE, not the melee fallback, and it is built by a
        /// DIFFERENT engine function from the other two. Melee and Missile come from
        /// Creature.GetEffectiveDefenseSkill(CombatType); Magic comes from
        /// Creature.GetEffectiveMagicDefense() (Source\ACE.Server\WorldObjects\Creature_Magic.cs:172),
        /// which reads Skill.MagicDefense with its own weapon modifier and imbue terms.
        ///
        /// Say why, because the trap is silent and this shape has now bitten twice.
        /// GetEffectiveDefenseSkill picks its skill with a TWO-WAY ternary -
        /// "combatType == CombatType.Missile ? Skill.MissileDefense : Skill.MeleeDefense"
        /// (Source\ACE.Server\WorldObjects\Creature_Combat.cs:490) - so passing CombatType.Magic to
        /// it does not fail, does not warn, and silently returns MELEE defense. The engine's own
        /// spell-resist path never calls it for magic at all. A profile built that way stores a
        /// character's melee defense under the Magic key, and a defender whose two skills differ
        /// then reports a hit chance that can be wrong by more than an order of magnitude.
        ///
        /// The neighbouring GetDefenseSkill(CombatType) DOES map Magic to Skill.MagicDefense
        /// (Creature_Combat.cs:1152-1153), which is exactly why this is easy to miss: two methods
        /// on the same partial class disagree about what CombatType.Magic means. Check which one a
        /// call site uses before assuming either.
        /// </summary>
        public IReadOnlyDictionary<CombatType, uint> DefenseSkills { get; init; }

        public IReadOnlyDictionary<ArmorKey, ArmorRow> Armor { get; init; }
        public IReadOnlyDictionary<DamageType, ResistancePair> Resistance { get; init; }
        public IReadOnlyDictionary<ShieldKey, ShieldRow> Shield { get; init; }

        /// <summary>
        /// Damage resist rating mod with no attacker-dependent term applied, one entry per combat
        /// type. It has to be per combat type because GetSpecDefenseBonus grants a damage rating
        /// resist for a SPECIALIZED defense skill and therefore depends on which defense the attack
        /// is tested against, returning zero outright for a null combat type
        /// (Source\ACE.Server\WorldObjects\Creature_Rating.cs:296-297).
        /// </summary>
        public IReadOnlyDictionary<CombatType, float> DamageResistRatingBase { get; init; }

        /// <summary>
        /// The Battle Hardened multiplier, which the engine applies only when the defender is a
        /// player and the attacker is not. Stored separately so the PvE gate can be honoured.
        /// </summary>
        public float BattleHardenedMultiplier { get; init; }
    }
}
