using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// When one of the player's equipped Aetheria surges, the surge also taunts all nearby
    /// monsters into attacking the player for a short time. Rides along with any surge the
    /// wielder triggers, regardless of which surge spell procs (or whether it resists/fails).
    /// Higher Loyalty extends the hold duration, up to a hard cap.
    /// </summary>
    public class TauntAbility : IItemProcAbility, ITestableClassAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Taunt,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 2,
            Name = "taunt",
            DisplayName = "Taunt",
            Description = "When one of your equipped Aetheria surges, the surge also taunts all nearby monsters " +
                          "into attacking you for a short time. Higher Loyalty increases the hold duration.",
            MaxRank = 1,
            CostPerRank = new[] { 3 },
            Implemented = true,
        };

        public void OnItemProc(Player wielder, int rank, WorldObject procSource)
        {
            // taunt only rides along with aetheria surges, not other item procs
            if (!Aetheria.IsAetheria(procSource.WeenieClassId))
                return;

            var taunted = Activate(wielder);

            if (taunted > 0)
                wielder.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Your surging aetheria taunts {taunted} nearby monster{(taunted == 1 ? "" : "s")} into attacking you!", ChatMessageType.CombatSelf));
        }

        /// <summary>
        /// The Taunt effect itself: forces every attackable monster within the taunt radius and
        /// line of sight to attack the player for the configured duration, returning how many
        /// were taunted. No learned-skill check - callers gate (the surge proc requires the
        /// skill; the /testskill developer command deliberately doesn't).
        ///
        /// THE TWO EQUIPMENT MODS ARE READ THROUGH THE MACHINERY GATE PRECISELY BECAUSE THIS METHOD DOES
        /// NOT PROVE OWNERSHIP. Its two callers disagree: OnItemProc is dispatched from the LEARNED
        /// IItemProcAbility hook cache, so an owner is guaranteed there, but Test(player) is reached from
        /// /testskill, which the command's own help text says "bypasses its normal trigger and the learned
        /// requirement" - so a developer without Taunt can run this. Provoke and Bellow are machinery mods
        /// and must stay inert for that non-owner, which is what Player.GetMachineryEquipmentModValue
        /// enforces (it returns 0 unless the linked ability is live). A plain GetEquippedModValue here would
        /// have let a /testskill taunt from an unowning character pick up gear magnitudes.
        /// </summary>
        public static int Activate(Player player)
        {
            // Bellow equipment mod (MACHINERY): +m of taunt radius, added to the raw radius read. There is
            // no clamp anywhere on this value, so this is a plain summand.
            var radius = PropertyManager.GetDouble("class_ability_taunt_radius").Item
                + Math.Max(0.0, player.GetMachineryEquipmentModValue(EquipmentModId.Bellow));

            // Taunt's Loyalty rider: extends the hold duration in seconds, clamped to a hard cap.
            // Untrained Loyalty reproduces the base duration exactly (raw scaling quotient = 0).
            var baseDuration = PropertyManager.GetDouble("class_ability_taunt_duration").Item;
            var loyaltyBonus = player.GetClassAbilityScaling(Skill.Loyalty,
                PropertyManager.GetDouble("class_ability_taunt_loyalty_per_trained").Item,
                PropertyManager.GetDouble("class_ability_taunt_loyalty_per_spec").Item);
            var bonusCap = PropertyManager.GetDouble("class_ability_taunt_loyalty_bonus_cap_seconds").Item;

            // Provoke equipment mod (MACHINERY): +s of hold duration, a further summand on the SUM and
            // therefore OUTSIDE the Loyalty clamp - see EffectiveDuration.
            var duration = EffectiveDuration(baseDuration, loyaltyBonus, bonusCap,
                player.GetMachineryEquipmentModValue(EquipmentModId.Provoke));

            var taunted = 0;

            // same visible-objects source and monster filters as GetMultiShotTargets
            var visible = player.PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj.WorldObject != null);

            foreach (var obj in visible)
            {
                var creature = obj.WeenieObj.WorldObject as Creature;

                if (creature == null || creature is Player || creature.IsDead || creature.Teleporting)
                    continue;

                if (!creature.Attackable && creature.TargetingTactic == TargetingTactic.None)
                    continue;

                if (creature is CombatPet)
                    continue;

                if (player.Location.DistanceTo(creature.Location) > radius)
                    continue;

                if (!player.IsDirectVisible(creature))
                    continue;

                creature.ApplyTaunt(player, duration);
                taunted++;
            }

            return taunted;
        }

        /// <summary>
        /// The effective Taunt hold duration in seconds: the base duration plus the Loyalty rider bonus,
        /// clamped to <paramref name="bonusCapSeconds"/> (0 reproduces <paramref name="baseDuration"/>
        /// exactly). Pure for testability.
        ///
        /// <paramref name="gearSeconds"/> is the PROVOKE equipment mod (EquipmentModId.Provoke), a MACHINERY
        /// mod added as a further summand ON THE SUM, deliberately OUTSIDE the Loyalty clamp. Only the RIDER
        /// is clamped here, and the clamp exists to bound an unbounded skill quotient; the gear term is
        /// already bounded by its registry MaxMagnitude, so folding it inside the clamp would let a
        /// fully-capped Loyalty silently eat the mod - the Resonance failure recorded in DESIGN.md 2.2.
        /// Provoke is therefore live at any Loyalty. Last and defaulted to 0, so an unmodded build is
        /// bit-identical.
        /// </summary>
        public static double EffectiveDuration(double baseDuration, double loyaltyBonusSeconds, double bonusCapSeconds, double gearSeconds = 0.0)
        {
            var bonus = Math.Clamp(loyaltyBonusSeconds, 0.0, bonusCapSeconds);
            return baseDuration + bonus + Math.Max(0.0, gearSeconds);
        }

        public string Test(Player player)
        {
            var taunted = Activate(player);

            // Quotes the radius Activate actually used, Bellow included, through the same machinery gate -
            // so a non-owner running /testskill sees the unmodded number, matching what just happened.
            var radius = PropertyManager.GetDouble("class_ability_taunt_radius").Item
                + Math.Max(0.0, player.GetMachineryEquipmentModValue(EquipmentModId.Bellow));

            return $"Taunt test: taunted {taunted} monster{(taunted == 1 ? "" : "s")} within {radius:0} units.";
        }

        /// <summary>
        /// Mirrors the base duration + Loyalty rider terms fed into EffectiveDuration in Activate above - the
        /// two must stay in step. Already in seconds ("s" unit, no x100). Affinity is the RAW (pre-clamp)
        /// rider so Total shows what it would be uncapped; Effective uses EffectiveDuration's own clamp.
        /// CapNote is set only when the raw rider actually exceeds the cap this call.
        ///
        /// GEAR IS PROVOKE ONLY, not Provoke plus Bellow. Taunt carries two mods and they are in different
        /// units - Provoke is seconds of hold, Bellow is metres of radius - so only the one matching this
        /// line's own Unit ("s") can be summed into it. Bellow has no readout line because this readout
        /// reports a duration; that is a display gap, not a wiring gap - Bellow is live in Activate.
        ///
        /// A plain GetEquippedModValue is correct HERE, unlike in Activate: /abilities list only renders a
        /// readout for abilities the player already owns at rank > 0, so ownership is proven by the caller.
        /// The null-conditional is for the readout unit tests, which pass a null Player because Player's
        /// static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseDuration = PropertyManager.GetDouble("class_ability_taunt_duration").Item;

            var loyaltyBonus = player.GetClassAbilityScaling(Skill.Loyalty,
                PropertyManager.GetDouble("class_ability_taunt_loyalty_per_trained").Item,
                PropertyManager.GetDouble("class_ability_taunt_loyalty_per_spec").Item);

            var bonusCap = PropertyManager.GetDouble("class_ability_taunt_loyalty_bonus_cap_seconds").Item;

            // Rank-gated - a machinery mod reports nothing without its ability.
            var gear = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Provoke) ?? 0.0);

            var effective = EffectiveDuration(baseDuration, loyaltyBonus, bonusCap, gear);
            var capBit = Math.Clamp(loyaltyBonus, 0.0, bonusCap) < loyaltyBonus;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = baseDuration,
                Affinity = loyaltyBonus,
                Gear = gear,
                Effective = effective,
                Unit = "s",
                Label = "taunt",
                Per = null,
                CapNote = capBit ? "loyalty cap" : null,
            };
        }
    }
}
