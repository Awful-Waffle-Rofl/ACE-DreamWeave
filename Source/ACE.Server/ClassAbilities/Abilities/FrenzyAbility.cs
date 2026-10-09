using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// A stacking attack-speed buff for the Berserker's two melee styles. Every landed hit against a
    /// monster made with a two-handed or dual-wielded melee weapon adds one stack worth +5% attack speed
    /// (tunable); stacks cap at 3 / 6 / 10 by rank, and reset if you go
    /// class_ability_frenzy_expire_seconds without landing a qualifying hit.
    ///
    /// The trigger reuses the IOutgoingDamageAbility hook (fires exactly on a landed, damaging hit vs a
    /// monster - never a miss or PvP) but modifies no damage; it just bumps the player's transient
    /// Frenzy state. The *application* is a bespoke integration in Creature.GetAnimSpeed, where the
    /// current stack count multiplies the computed attack animation speed on top of the normal cap,
    /// up to a separate ceiling (class_ability_attack_speed_ceiling). Stacks are transient runtime
    /// state (Player_ClassAbilityBuffs), never persisted.
    ///
    /// Weapon gate (FINDINGS-LEDGER L6-73, user decision 2026-07-25): BOTH the trigger and the
    /// application are gated on IsQualifyingStance, so unarmed, a lone one-hander, missile and magic
    /// neither build nor benefit from stacks. Stacks earned on a qualifying weapon go inert (not
    /// consumed) while a non-qualifying weapon is out, and become live again on a style swap back if
    /// the expiry window has not lapsed. This formalizes the premise POWER-LEDGER's Round 9 note
    /// already priced against (rows at POWER-LEDGER.md:232,250) - the whole Berserker melee package on
    /// one style.
    /// </summary>
    public class FrenzyAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Frenzy,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 1,
            Name = "frenzy",
            DisplayName = "Frenzy",
            Description = "Each landed hit against a monster with a two-handed or dual-wielded melee weapon " +
                          "increases your attack speed by 5%, stacking up to 3 / 6 / 10 times by rank. " +
                          "Stacks reset after 10 seconds without landing a hit. " +
                          "Higher Recklessness increases the per-stack bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // flat 1/rank (class ability overhaul repricing, 2026-09-12)
            Implemented = true,
            AffinitySkill = Skill.Recklessness, // rider computed in Player_ClassAbilityBuffs.GetFrenzyPerStackTerms, not in this file
        };

        // Trigger only: bumps the attacker's transient Frenzy stack. Damage is deliberately untouched.
        // The two-handed / dual-wield gate lives in Player.OnFrenzyLandedHit so every call site inherits it.
        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            attacker?.OnFrenzyLandedHit(rank);
        }

        /// <summary>
        /// TRUE for the two combat stances Frenzy is allowed on: two-handed melee (sword or staff) and
        /// dual-wielded melee. Everything else - unarmed, a single one-hander with an empty offhand,
        /// missile and magic - is FALSE, including a null stance.
        ///
        /// Stance is the authoritative mid-attack weapon-style signal on a Player and is correct for both
        /// hands of a dual-wield swing: DualWieldCombat is held for the whole swing regardless of which
        /// hand lands it (the main/off alternation is the separate DualWieldAlternate flag, see
        /// Player_Melee.cs:454). The stance values mirror Creature.IsDualWieldAttack
        /// (Creature_Melee.cs:20) and Creature.TwoHandedCombat (Creature_Melee.cs:30), which is the same
        /// pair the sibling Berserker ability Whirlwind gates on
        /// (Player_ClassAbilityBuffs.ApplyWhirlwindStamina).
        /// </summary>
        public static bool IsQualifyingStance(MotionStance? stance) =>
            stance == MotionStance.TwoHandedSwordCombat ||
            stance == MotionStance.TwoHandedStaffCombat ||
            stance == MotionStance.DualWieldCombat;

        /// <summary>
        /// Maximum number of stacks at a given owned rank: 3 / 6 / 10 for ranks 1-3, 0 if unlearned.
        /// </summary>
        public static int StackCap(int rank) => rank switch
        {
            1 => 3,
            2 => 6,
            3 => 10,
            _ => 0,
        };

        /// <summary>
        /// The attack-speed multiplier for a given number of active stacks (1.0 = none).
        /// </summary>
        public static float AttackSpeedMultiplier(int stacks, double percentPerStack) =>
            (float)(1.0 + Math.Max(0, stacks) * percentPerStack);

        /// <summary>
        /// Reports the PER-STACK rate, not a total-stacks bonus. Skill/Affinity/Gear come from
        /// Player.GetFrenzyPerStackTerms - the SAME call the applied multiplier and the peak announcement
        /// make - rather than being restated here, so the three cannot drift.
        ///
        /// The anim-speed ceiling (class_ability_attack_speed_ceiling) composes Frenzy MULTIPLICATIVELY
        /// with Attack Speed and weapon mods and clamps the PRODUCT
        /// (Player_ClassAbilityBuffs.ApplyClassAbilityAttackSpeed), so it is not a per-stack cap and must
        /// not be baked into a per-stack Effective - Effective is always Total here. Instead this calls the
        /// real composed methods (not a restatement of their math) against a reference base anim speed to
        /// see whether the ceiling is ACTUALLY clipping for this player right now, and only then sets
        /// CapNote.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var terms = player.GetFrenzyPerStackTerms();

            var skill = terms.Skill * 100.0;
            var affinity = terms.Affinity * 100.0;
            var gear = terms.Gear * 100.0;
            var total = skill + affinity + gear;

            // Creature.MaxAttackSpeed is the documented saturating base the ceiling clamp is written
            // against (see ApplyClassAbilityAttackSpeed's own doc comment). Referenced rather than copied
            // as a literal: it is a mutable static, so a duplicated 2.0 here would drift silently the
            // moment it is retuned. This never restates the clamp itself - it calls the real composed mod
            // getters and the real ApplyClassAbilityAttackSpeed, then compares the real clamped result
            // against the naive uncapped product to see whether the ceiling actually bit.
            var referenceBaseAnimSpeed = (float)Creature.MaxAttackSpeed;
            var composedMod = player.GetFrenzyAttackSpeedMod() * player.GetAttackSpeedSkillMod() * player.GetWeaponModAttackSpeedMod();
            var uncappedSpeed = referenceBaseAnimSpeed * composedMod;
            var appliedSpeed = player.ApplyClassAbilityAttackSpeed(referenceBaseAnimSpeed);
            var ceilingBit = appliedSpeed < uncappedSpeed - 0.0001f;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "atk speed",
                Per = "/stack",
                CapNote = ceilingBit ? "anim ceiling" : null,
            };
        }
    }
}
