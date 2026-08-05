using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Flat bonus damage per rank on every landed weapon hit against monsters, plus a thresholded
    /// Alchemy rider (Round 6): +1 flat per 12 points of effective Alchemy above 100 trained (~per 9
    /// specialized), so the expensive hook pays at the late end. Dealt as a SEPARATE proc (user ruling
    /// 2026-07-16): its own combat-feedback line, unresisted <see cref="DamageType.Base"/> "damage"
    /// (poison has no in-game mitigation; can be re-typed to Acid later if it ever needs to be resistable),
    /// resolved just AFTER the weapon strike so it reads as a follow-up. Covers melee, missile, and
    /// multi-shot hits - they all route through Player.DamageTarget. Acid Proc's DoT is defined as a share
    /// of this same flat bonus (Alchemy included), so the flat computation lives in one reusable place.
    /// </summary>
    public class PoisonWeaponAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.PoisonWeapon,
            AbilityClass = ClassAbilityClass.Rogue,
            Tier = 1,
            Name = "poisonweapon",
            DisplayName = "Poison Weapon",
            Description = "Your weapon strikes against monsters deal separate poison damage per rank on every landed hit, " +
                          "increased further by high Alchemy.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },   // Tier-1 GC: rank 1 always 1 point (the class's power splash)
            Implemented = true,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            // weapon hits only - unarmed/other damage sources don't qualify
            if (damageEvent.Weapon == null || attacker == null || target == null)
                return;

            // Venom equipment mod (STANDALONE): summed with the ability's own flat and rounded ONCE. The
            // rank-0 half lives in Player.ApplyEquipmentModOutgoingDamage, which fires this same proc with
            // the mod value alone - exactly one of the two paths runs per hit.
            var poison = ComputePoisonDamage(FlatBonus(attacker, rank), attacker.GetEquippedModValue(EquipmentModId.Venom));
            if (poison == 0)
                return;

            SchedulePoisonProc(attacker, target, poison);
        }

        /// <summary>
        /// Deals a poison amount as its own proc AFTER the weapon strike (message order, user 2026-07-16):
        /// deferred onto a short action chain so its damage + line land just after the main hit's.
        /// Unresisted <see cref="DamageType.Base"/> ("damage"), attributed to the attacker so kill credit
        /// works. Shared by the ability and by the standalone Venom equipment mod, so a modded player with no
        /// Poison Weapon rank sees an identical proc, just smaller.
        /// </summary>
        public static void SchedulePoisonProc(Player attacker, Creature target, uint poison)
        {
            if (attacker == null || target == null || poison == 0)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(PropertyManager.GetDouble("class_ability_poisonweapon_proc_delay").Item);
            chain.AddAction(attacker, () =>
            {
                if (target.IsDead)
                    return;

                target.TakeDamage(attacker, DamageType.Base, poison);

                if (attacker.Session != null && !attacker.SquelchManager.Squelches.Contains(target, ChatMessageType.CombatSelf))
                    attacker.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"Your poison deals {poison:N0} additional points of damage to {target.Name}!", ChatMessageType.CombatSelf));
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// The current Poison Weapon flat bonus for a player at a given rank: rank * per-rank base, plus
        /// the thresholded Alchemy rider. Shared by the proc and Acid Proc's DoT (which ticks a share of this
        /// value). Returns 0 at rank 0. Carries NO equipment-mod term: the Venom mod is added later, in
        /// <see cref="ComputePoisonDamage"/>, so that a single Venom roll cannot pay out into two separate
        /// damage streams (Acid Proc derives its tick from this value and has its own Caustic mod).
        /// </summary>
        public static double FlatBonus(Player attacker, int rank)
        {
            if (rank <= 0)
                return 0.0;

            var perRank = rank * PropertyManager.GetDouble("class_ability_poisonweapon_damage_per_rank").Item;

            // only the Alchemy rider needs the live player; the per-rank base stands on its own
            var alchemy = attacker == null ? 0.0 : attacker.GetClassAbilityScaling(Skill.Alchemy,
                PropertyManager.GetDouble("class_ability_poisonweapon_alchemy_per_trained").Item,
                PropertyManager.GetDouble("class_ability_poisonweapon_alchemy_per_spec").Item,
                PropertyManager.GetDouble("class_ability_poisonweapon_alchemy_threshold").Item);

            return perRank + alchemy;
        }

        /// <summary>
        /// The integer poison damage one landed hit deals: the ability's flat plus the Venom equipment-mod
        /// contribution (already summed across every equipped item), rounded exactly ONCE.
        ///
        /// Rounding follows the user rule of 2026-07-25 - "fractional rounds to nearest with 0.5 rounding up,
        /// and fractional is additive with other equivalent mods before rounding" - so two 0.4 rolls on two
        /// items become 0.8 and pay out as +1, rather than each vanishing on its own. Half-up
        /// (<see cref="MidpointRounding.AwayFromZero"/>) is applied only when a gear term is actually
        /// present; with no mods this takes the original expression unchanged, including .NET's default
        /// banker's rounding, so a player with no equipment mods sees bit-identical damage to before.
        ///
        /// EVERY path that deals the poison flat routes through here - main hits, Multishot extra arrows and
        /// Riposte counters all reach it through Player.DamageTarget - so they cannot drift apart.
        /// </summary>
        public static uint ComputePoisonDamage(double abilityFlat, double gearFlat)
        {
            var ability = Math.Max(0.0, abilityFlat);
            var gear = Math.Max(0.0, gearFlat);

            if (gear <= 0.0)
                return (uint)Math.Round(ability);

            return (uint)Math.Round(ability + gear, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Mirrors FlatBonus (rank term + thresholded Alchemy rider) plus the Venom gear term from
        /// ComputePoisonDamage above - the three must stay in step. Flat "" unit (already a damage amount,
        /// not a percentage), so no x100 conversion. No cap on the flat bonus itself (only the final integer
        /// rounding, which isn't a clamp), so Effective always equals Total.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = rank * PropertyManager.GetDouble("class_ability_poisonweapon_damage_per_rank").Item;

            var alchemy = player.GetClassAbilityScaling(Skill.Alchemy,
                PropertyManager.GetDouble("class_ability_poisonweapon_alchemy_per_trained").Item,
                PropertyManager.GetDouble("class_ability_poisonweapon_alchemy_per_spec").Item,
                PropertyManager.GetDouble("class_ability_poisonweapon_alchemy_threshold").Item);

            var gear = player.GetEquippedModValue(EquipmentModId.Venom);

            var total = perRank + alchemy + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = perRank,
                Affinity = alchemy,
                Gear = gear,
                Effective = total,
                Unit = "",
                Label = "poison",
                Per = null,
                CapNote = null,
            };
        }
    }
}
