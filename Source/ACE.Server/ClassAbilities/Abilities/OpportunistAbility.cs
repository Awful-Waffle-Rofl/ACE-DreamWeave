using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T1 splash: 2/4/6/8/10% more damage by rank against any target already carrying a debuff or a
    /// crowd control effect - Vulnerability, Imperil, blindness, a taunt, or Weakened Blood - applied by the
    /// Rogue OR BY A FELLOW. Reading a fellow's debuff is deliberate: it makes the entry pay for grouping
    /// with the classes that apply those effects rather than rewarding a solo Rogue for debuffing itself.
    ///
    /// PvE ONLY (the outgoing-damage dispatch already excludes player targets - see
    /// Player.ApplyOutgoingDamageClassAbilities).
    ///
    /// WHERE EACH MARK IS DETECTED, since there is no single "is this target marked" choke point:
    ///  - Weakened Blood: Creature.HasWeakenedBlood. Read for ANY caster, not just self/fellow - the mark
    ///    is inherently party-wide by its own design (WeakenedBloodAbility's doc comment: "from ANY caster,
    ///    not only the one who applied it"), so there is no narrower caster to check.
    ///  - Taunt: Creature.TauntTarget / TauntExpiry (Monster_Awareness.ApplyTaunt), checked against the
    ///    attacker/fellow.
    ///  - Blindness: Pocket Sand's synthetic attack debuff, filed under
    ///    EnchantmentManager.SpellCategory_ClassAbility_PocketSand (Player_ClassAbilityCombat.TryPocketSand)
    ///    - the only "blindness" this fork's server side actually applies.
    ///  - Hunter's Mark: EnchantmentManager.SpellCategory_ClassAbility_HuntersMark
    ///    (HuntersMarkAbility.ApplyMark), checked against the attacker/fellow. A qualifying mark by owner
    ///    ruling 2026-09-14; it is its own debuff, so the Vulnerability read below does not see it.
    ///  - Imperil: Break Armor's proc (SpellId.ImperilOther3/5/7), read by exact spell id since Imperil has
    ///    no dedicated SpellCategory of its own.
    ///  - Vulnerability: the seven per-element SpellCategory entries (AcidVulnerability etc), read by
    ///    category since a Vulnerability spell exists at many levels per element.
    ///
    /// AFFINITY: Sneak Attack, MULTIPLYING this ability's own rank bonus (2026-09-12 overhaul model) - not
    /// an additive rider. At zero effective Sneak Attack the multiplier is exactly 1.0 and the bonus is
    /// bit-identical to rank alone.
    /// </summary>
    public class OpportunistAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Opportunist,
            AbilityClass = ClassAbilityClass.Rogue,
            Tier = 1,
            Name = "opportunist",
            DisplayName = "Opportunist",
            Description = "You deal 2/4/6/8/10% more damage (by rank) to any target carrying a debuff or " +
                          "crowd control applied by you or a fellow - Vulnerability, Imperil, blindness, " +
                          "taunts, or Weakened Blood. Higher Sneak Attack multiplies the bonus.",
            MaxRank = 5,
            CostPerRank = new[] { 1, 1, 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.SneakAttack,
        };

        private static readonly SpellId[] ImperilSpellIds =
        {
            SpellId.ImperilOther3,
            SpellId.ImperilOther5,
            SpellId.ImperilOther7,
        };

        private static readonly SpellCategory[] VulnerabilityCategories =
        {
            SpellCategory.AcidVulnerability,
            SpellCategory.BludgeonVulnerability,
            SpellCategory.ColdVulnerability,
            SpellCategory.ElectricVulnerability,
            SpellCategory.FireVulnerability,
            SpellCategory.PierceVulnerability,
            SpellCategory.SlashVulnerability,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || rank <= 0)
                return;

            if (!TargetHasQualifyingMark(attacker, target))
                return;

            var rankBonus = rank * PropertyManager.GetDouble("class_ability_opportunist_percent_per_rank").Item;

            var affinity = attacker.GetClassAbilityAffinityMultiplier(Skill.SneakAttack);

            var bonus = rankBonus * affinity;
            if (bonus <= 0.0)
                return;

            damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// TRUE when <paramref name="target"/> currently carries any of the qualifying marks, applied
        /// by <paramref name="attacker"/> or one of their fellowship members. See the type doc comment for
        /// where each mark actually lives.
        /// </summary>
        public static bool TargetHasQualifyingMark(Player attacker, Creature target)
        {
            if (attacker == null || target == null)
                return false;

            // Weakened Blood: party-wide by its own design, so any live caster qualifies.
            if (target.HasWeakenedBlood)
                return true;

            // Taunt: Monster_Awareness.ApplyTaunt's forced-attack-target state.
            if (target.TauntTarget != null && Timers.RunningTime < target.TauntExpiry &&
                IsAppliedBySelfOrFellow(attacker, target.TauntTarget.Guid.Full))
                return true;

            // Blindness: Pocket Sand's synthetic attack debuff.
            foreach (var entry in target.EnchantmentManager.GetEnchantments((SpellCategory)EnchantmentManager.SpellCategory_ClassAbility_PocketSand))
            {
                if (IsAppliedBySelfOrFellow(attacker, entry.CasterObjectId))
                    return true;
            }

            // Hunter's Mark: its own class-ability category. It stays a qualifying mark by owner ruling
            // 2026-09-14; before the mark became its own debuff it qualified through the Vulnerability
            // categories below, so this keeps that behaviour.
            foreach (var entry in target.EnchantmentManager.GetEnchantments((SpellCategory)EnchantmentManager.SpellCategory_ClassAbility_HuntersMark))
            {
                if (IsAppliedBySelfOrFellow(attacker, entry.CasterObjectId))
                    return true;
            }

            // Imperil: exact spell id, since Imperil has no SpellCategory of its own.
            foreach (var imperilId in ImperilSpellIds)
            {
                var entry = target.EnchantmentManager.GetEnchantment((uint)imperilId);
                if (entry != null && IsAppliedBySelfOrFellow(attacker, entry.CasterObjectId))
                    return true;
            }

            // Vulnerability: any of the seven per-element categories, any level.
            foreach (var category in VulnerabilityCategories)
            {
                foreach (var entry in target.EnchantmentManager.GetEnchantments(category))
                {
                    if (IsAppliedBySelfOrFellow(attacker, entry.CasterObjectId))
                        return true;
                }
            }

            return false;
        }

        private static bool IsAppliedBySelfOrFellow(Player attacker, uint casterGuid)
        {
            if (casterGuid == 0)
                return false;

            if (casterGuid == attacker.Guid.Full)
                return true;

            return attacker.Fellowship?.GetFellowshipMembers().ContainsKey(casterGuid) ?? false;
        }

        /// <summary>
        /// Mirrors the rank/affinity terms in ModifyOutgoingDamage above exactly (x100 for display) - the
        /// two must stay in step. No gear mod exists for this ability, so Gear is always 0. No cap, so
        /// Effective always equals Total.
        ///
        /// Affinity is a MULTIPLIER on the rank term, but it is reported as the AMOUNT that multiplier adds
        /// (rankBonus * affinity - rankBonus), so the three displayed terms stay in the same unit and still
        /// sum to the effective bonus.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank * PropertyManager.GetDouble("class_ability_opportunist_percent_per_rank").Item;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.SneakAttack);

            var skill = rankBonus * 100.0;
            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;
            var total = skill + affinity;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = total,
                Unit = "%",
                Label = "dmg vs marked",
                Per = null,
                CapNote = null,
            };
        }
    }
}
