using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Flat bonus damage per rank on every landed weapon hit against monsters, MULTIPLIED by the Alchemy
    /// affinity (2026-09-12 overhaul, replacing the thresholded additive rider: "+R of the ability's own
    /// bonus per 100 points of Alchemy", no threshold - the design document specifies none for Poison
    /// Weapon). Dealt as a SEPARATE proc (user ruling
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
                          "increased further by high Alchemy. Ranks in Acid Proc raise this damage further still. " +
                          "The poison scales like the strike that carried it: your damage and critical damage " +
                          "ratings, the target's damage resistance, and on a critical hit the strike's critical " +
                          "multiplier and Killer Instinct. Extra Multishot arrows carry reduced poison.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // flat 1/rank (class ability overhaul repricing, 2026-09-12)
            Implemented = true,
            AffinitySkill = Skill.Alchemy, // the Item Tinkering read in FlatBonus/GetReadout is ACID PROC's rider, not this ability's
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            // weapon hits only - unarmed/other damage sources don't qualify
            if (damageEvent.Weapon == null || attacker == null || target == null)
                return;

            // Venom equipment mod (STANDALONE): summed with the ability's own flat and rounded ONCE. The
            // rank-0 half lives in Player.ApplyEquipmentModOutgoingDamage, which fires this same proc with
            // the mod value alone - exactly one of the two paths runs per hit.
            //
            // Strike multiplier: the WHOLE poison proc (ability + gear) takes the triggering strike's crit
            // multiplier, damage rating and the target's damage resistance rating (see
            // DamageEvent.ProcDamageMultiplier), applied before the single rounding below.
            var poison = ComputePoisonDamage(FlatBonus(attacker, rank), attacker.GetEquippedModValue(EquipmentModId.Venom), damageEvent.ProcDamageMultiplier);
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

                target.TakeDamage(attacker, DamageType.Base, poison, false, IncomingDamageOrigin.Secondary);   // a proc riding the strike, not the strike

                if (attacker.Session != null && !attacker.SquelchManager.Squelches.Contains(target, ChatMessageType.CombatSelf))
                    attacker.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"Your poison deals {poison:N0} additional points of damage to {target.Name}!", ChatMessageType.CombatSelf));
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// The current Poison Weapon flat bonus for a player at a given rank: (rank * per-rank base)
        /// MULTIPLIED by the Alchemy affinity. Shared by the proc and Acid Proc's DoT (which ticks a share of
        /// this value). Returns 0 at rank 0. Carries NO equipment-mod term: the Venom mod is added later, in
        /// <see cref="ComputePoisonDamage"/>, so that a single Venom roll cannot pay out into two separate
        /// damage streams (Acid Proc derives its tick from this value and has its own Caustic mod).
        ///
        /// TWO SEPARATE ALCHEMY READS LIVE IN THIS METHOD, and that is deliberate rather than a duplicate:
        /// this ability's own rank bonus is multiplied by Alchemy once, and (further below) Acid Proc's OWN
        /// rank bonus is separately multiplied by Alchemy again - Acid Proc's affinity skill moved from Item
        /// Tinkering to Alchemy in the same 2026-09-12 overhaul, so the two riders now happen to read the
        /// same skill, but they scale two different bonuses and must not be collapsed into one read.
        ///
        /// ACID PROC POISON-DAMAGE BONUS (2026-08-17 rework, migrated to the multiplicative affinity
        /// 2026-09-12): gated on the attacker owning Acid Proc rank &gt; 0, the flat is multiplied by
        /// <c>1 + AcidProcAbility.PoisonDamageBonus(...)</c>. This is the single shared source both the
        /// per-hit Poison Weapon proc AND every Acid Proc DoT tick read, so a rank in Acid Proc raises both
        /// automatically without either path restating the math. A player with zero Acid Proc ranks is
        /// unaffected (the multiplier term is skipped entirely, not applied at 1.0).
        /// </summary>
        public static double FlatBonus(Player attacker, int rank)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = rank * PropertyManager.GetDouble("class_ability_poisonweapon_damage_per_rank").Item;

            // Untrained/Inactive Alchemy, or no live player, resolves to the neutral 1.0 factor - the
            // rank bonus alone, bit-identical to an unaffiliated build.
            var alchemyAffinity = attacker == null ? 1.0 : attacker.GetClassAbilityAffinityMultiplier(Skill.Alchemy);

            var flat = rankBonus * alchemyAffinity;

            if (attacker != null && attacker.TryGetClassAbility(ClassAbilityId.AcidProc, out var acidRank) && acidRank > 0)
            {
                var acidAlchemyAffinity = attacker.GetClassAbilityAffinityMultiplier(Skill.Alchemy);

                var poisonDamageBonus = AcidProcAbility.PoisonDamageBonus(acidRank,
                    PropertyManager.GetDouble("class_ability_acidproc_damage_base").Item,
                    PropertyManager.GetDouble("class_ability_acidproc_damage_step").Item,
                    acidAlchemyAffinity,
                    PropertyManager.GetDouble("class_ability_acidproc_damage_affinity_cap").Item);

                flat *= 1.0 + poisonDamageBonus;
            }

            return flat;
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
        ///
        /// <paramref name="strikeMultiplier"/> is the producing strike's
        /// <see cref="DamageEvent.ProcDamageMultiplier"/>: its crit multiplier, the attacker's damage rating
        /// and the target's damage resistance rating (crit damage resistance included on a crit). It
        /// multiplies the WHOLE proc (ability + gear) BEFORE the single rounding above, so the proc scales
        /// exactly as the strike's own rating terms do.
        ///
        /// It may be BELOW 1.0 (a resistant target), and that reduction is intended - so the clamp here is
        /// a floor of 0, which only guards against a negative input producing a wrapped uint. It used to be
        /// a floor of 1.0, when the only input was the crit multiplier and a sub-1.0 value could only mean a
        /// bad caller. Defaults to 1.0, which keeps both branches - including the no-gear banker's-rounding
        /// branch - bit-identical to before this parameter existed.
        /// </summary>
        public static uint ComputePoisonDamage(double abilityFlat, double gearFlat, double strikeMultiplier = 1.0)
        {
            var ability = Math.Max(0.0, abilityFlat);
            var gear = Math.Max(0.0, gearFlat);
            var m = Math.Max(0.0, strikeMultiplier);

            if (gear <= 0.0)
                return (uint)Math.Round(ability * m);

            return (uint)Math.Round((ability + gear) * m, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Mirrors FlatBonus (rank term multiplied by the Alchemy affinity, further scaled by the Acid Proc
        /// poison-damage bonus when owned - see FlatBonus's doc comment) plus the Venom gear term from
        /// ComputePoisonDamage above, which is NOT scaled by that bonus (Venom is added after FlatBonus, not
        /// inside it). Flat "" unit (already a damage amount, not a percentage), so no x100 conversion. No
        /// cap on the flat bonus itself (only the final integer rounding, which isn't a clamp), so Effective
        /// always equals Total.
        ///
        /// Affinity is the AMOUNT the Alchemy multiply adds (rankBonus * affinity - rankBonus), scaled by the
        /// same Acid Proc multiplier as Skill, so Skill + Affinity + Gear still sum to Effective.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank * PropertyManager.GetDouble("class_ability_poisonweapon_damage_per_rank").Item;

            var alchemyAffinity = player?.GetClassAbilityAffinityMultiplier(Skill.Alchemy) ?? 1.0;
            var addedByAlchemy = rankBonus * alchemyAffinity - rankBonus;

            var acidProcMultiplier = 1.0;
            if (player != null && player.TryGetClassAbility(ClassAbilityId.AcidProc, out var acidRank) && acidRank > 0)
            {
                var acidAlchemyAffinity = player.GetClassAbilityAffinityMultiplier(Skill.Alchemy);

                var poisonDamageBonus = AcidProcAbility.PoisonDamageBonus(acidRank,
                    PropertyManager.GetDouble("class_ability_acidproc_damage_base").Item,
                    PropertyManager.GetDouble("class_ability_acidproc_damage_step").Item,
                    acidAlchemyAffinity,
                    PropertyManager.GetDouble("class_ability_acidproc_damage_affinity_cap").Item);

                acidProcMultiplier = 1.0 + poisonDamageBonus;
            }

            var gear = player.GetEquippedModValue(EquipmentModId.Venom);

            var skill = rankBonus * acidProcMultiplier;
            var affinity = addedByAlchemy * acidProcMultiplier;
            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
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
