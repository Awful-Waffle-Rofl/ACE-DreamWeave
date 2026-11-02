using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// When one of the player's equipped Aetheria surges, OR their equipped cloak fires its spell proc, all
    /// nearby monsters are taunted into attacking the player for a short time. Rides along with the proc
    /// ROLL, regardless of which spell procs or whether it resists/fails.
    ///
    /// THREE RANKS AT 1/1/1, NO AFFINITY (2026-10-17 Vanguard pass). Rank buys RADIUS - 15/30/65 m by the
    /// class_ability_taunt_radius_r1/_r2/_r3 tunables - and the hold duration is flat (the base tunable plus
    /// the Provoke gear term). The Assess Person duration bonus and its cap tunable
    /// (class_ability_taunt_loyalty_bonus_cap_seconds) were retired; Assess Person no longer feeds Taunt.
    /// Existing rank-1 owners (who paid 3) are promoted to rank 3 by ClassAbilityRepricing wave 2.
    ///
    /// THE CLOAK TRIGGER WAS ADDED 2026-09-29 AND REUSES THE EXISTING IItemProcAbility HOOK rather than a new
    /// one. Cloak procs resolve entirely inside ACE.Server.Entity.Cloak and never touched
    /// WorldObject.TryProcItem, so Cloak.TryProcSpell now calls Player.OnClassAbilityItemProc(cloak) on its
    /// success path. Routing it through the existing hook is safe precisely because this class is the ONLY
    /// implementor of IItemProcAbility, so no other handler can pick up the new source; the filter in
    /// <see cref="TriggersOn"/> is what keeps it honest if that ever stops being true.
    /// </summary>
    public class TauntAbility : IItemProcAbility, ITestableClassAbility, IAbilityReadout
    {
        /// <summary>
        /// The top rank. Named so the Custom Summons Tank pulse (CombatPet.TickTauntPulse) can ask for the
        /// rank-3 radius through <see cref="RadiusForRank(int)"/> instead of holding a copy of the number.
        /// </summary>
        public const int TopRank = 3;

        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Taunt,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 2,
            Name = "taunt",
            DisplayName = "Taunt",
            Description = "When one of your equipped Aetheria surges, or your equipped cloak fires its spell " +
                          "proc, all monsters within 15/30/65 meters (by rank) are taunted into attacking you " +
                          "for a short time.",
            MaxRank = TopRank,
            // 2026-10-17: one rank at 3 -> three ranks at 1/1/1 (rank 3 = the old single rank's price).
            // Existing owners are raised to rank 3 by ClassAbilityRepricing wave 2.
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
        };

        public void OnItemProc(Player wielder, int rank, WorldObject procSource)
        {
            // taunt only rides along with an aetheria surge or a cloak spell proc, not any other item proc
            if (!TriggersOn(procSource))
                return;

            var taunted = Activate(wielder, rank);

            if (taunted > 0)
                wielder.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{SourceClause(procSource)} taunts {taunted} nearby monster{(taunted == 1 ? "" : "s")} into attacking you!", ChatMessageType.CombatSelf));
        }

        /// <summary>
        /// The two proc sources Taunt rides: an equipped Aetheria's surge and an equipped cloak's spell proc.
        /// Pure so the filter is unit-testable and so it stays a positive allowlist - anything else reaching
        /// the IItemProcAbility hook (a weapon's cast-on-strike, say) must NOT taunt, and a new proc source
        /// wired into the hook later is excluded by default rather than included by accident.
        ///
        /// A null source is refused rather than throwing: both dispatch paths pass a real item today, and a
        /// third that did not should be inert here rather than killing the hit.
        /// </summary>
        public static bool TriggersOn(WorldObject procSource)
        {
            if (procSource == null)
                return false;

            return Aetheria.IsAetheria(procSource.WeenieClassId) || Cloak.IsCloak(procSource);
        }

        /// <summary>
        /// The subject of the taunt chat line, so an aetheria surge and a cloak proc do not both claim to be
        /// a "surging aetheria". Pure, and only ever called for a source <see cref="TriggersOn"/> accepted.
        /// </summary>
        public static string SourceClause(WorldObject procSource) =>
            procSource != null && Cloak.IsCloak(procSource) ? "Your cloak's woven magic" : "Your surging aetheria";

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
        public static int Activate(Player player, int rank)
        {
            // Bellow equipment mod (MACHINERY): +m of taunt radius, added to the rank radius. There is no
            // clamp anywhere on this value, so this is a plain summand.
            var radius = EffectiveRadius(RadiusForRank(rank),
                player.GetMachineryEquipmentModValue(EquipmentModId.Bellow));

            // Flat duration: the base tunable plus the Provoke equipment mod (MACHINERY), a summand.
            var duration = EffectiveDuration(BaseDuration(),
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
        /// The effective Taunt hold duration in seconds: the flat base duration plus
        /// <paramref name="gearSeconds"/>, the PROVOKE equipment mod (EquipmentModId.Provoke), a MACHINERY mod.
        /// There is no affinity term since the 2026-10-17 Vanguard pass. Pure for testability. The gear term is
        /// last and defaulted to 0 so an unmodded build is bit-identical.
        /// </summary>
        public static double EffectiveDuration(double baseDuration, double gearSeconds = 0.0) =>
            baseDuration + Math.Max(0.0, gearSeconds);

        /// <summary>
        /// The flat hold duration in seconds before any gear term, from class_ability_taunt_duration. The hold
        /// is the same at every rank. Shared by Activate and the Custom Summons Tank pulse, which takes this
        /// hold with no gear term (a pet wears no Provoke).
        /// </summary>
        public static double BaseDuration() =>
            PropertyManager.GetDouble("class_ability_taunt_duration").Item;

        /// <summary>
        /// The Taunt radius in metres at a rank, from class_ability_taunt_radius_r1/_r2/_r3 (15/30/65). A rank
        /// outside 1..MaxRank clamps to the nearest end so a stray value never reads past the ladder.
        /// </summary>
        public static double RadiusForRank(int rank) =>
            RadiusForRank(rank,
                PropertyManager.GetDouble("class_ability_taunt_radius_r1").Item,
                PropertyManager.GetDouble("class_ability_taunt_radius_r2").Item,
                PropertyManager.GetDouble("class_ability_taunt_radius_r3").Item);

        /// <summary><see cref="RadiusForRank(int)"/> over explicit per-rank radii, pure for tests.</summary>
        public static double RadiusForRank(int rank, double r1, double r2, double r3) =>
            rank <= 1 ? r1 : rank == 2 ? r2 : r3;

        /// <summary>
        /// The effective Taunt radius in metres: the rank radius (<see cref="RadiusForRank(int)"/>) plus the
        /// BELLOW equipment mod, a plain summand with no clamp of its own. Pure for testability - shared by
        /// Activate, Test and the GetReadout radius Secondary so all three cannot drift apart.
        /// </summary>
        public static double EffectiveRadius(double baseRadius, double gearRadius = 0.0) =>
            baseRadius + Math.Max(0.0, gearRadius);

        public string Test(Player player)
        {
            // /testskill bypasses the learned requirement: use the owned rank, or the top rank for a non-owner.
            var rank = player.TryGetClassAbility(Definition.Id, out var owned) && owned > 0 ? owned : Definition.MaxRank;

            var taunted = Activate(player, rank);

            // Quotes the radius Activate actually used, Bellow included, through the same machinery gate -
            // so a non-owner running /testskill sees the unmodded number, matching what just happened.
            var radius = EffectiveRadius(RadiusForRank(rank),
                player.GetMachineryEquipmentModValue(EquipmentModId.Bellow));

            return $"Taunt test (rank {rank}): taunted {taunted} monster{(taunted == 1 ? "" : "s")} within {radius:0} units.";
        }

        /// <summary>
        /// The hold duration (flat base plus the Provoke gear term, already in seconds) as the primary
        /// segment, with the rank radius plus Bellow as a Secondary - mirroring Activate, so the two must stay
        /// in step. No affinity and no cap, so Affinity is 0 and CapNote null.
        ///
        /// GEAR IS PROVOKE ONLY on the primary segment: Provoke is seconds, Bellow is metres, and only the
        /// mod matching the segment's own Unit can be summed into it. Bellow is reported on the radius
        /// Secondary.
        ///
        /// A plain GetEquippedModValue is correct HERE, unlike in Activate: /abilities list only renders a
        /// readout for abilities the player already owns at rank > 0, so ownership is proven by the caller.
        /// The null-conditional is for the readout unit tests, which pass a null Player because Player's
        /// static initializer cannot run under the test host. No Player method is called otherwise.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseDuration = PropertyManager.GetDouble("class_ability_taunt_duration").Item;

            // Rank-gated - a machinery mod reports nothing without its ability.
            var gear = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Provoke) ?? 0.0);

            var radiusGear = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Bellow) ?? 0.0);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = baseDuration,
                Affinity = 0.0,
                Gear = gear,
                Effective = EffectiveDuration(baseDuration, gear),
                Unit = "s",
                Label = "taunt",
                Per = null,
                CapNote = null,
                Secondary = new List<ClassAbilityReadout> { RadiusSecondary(RadiusForRank(rank), radiusGear) },
            };
        }

        /// <summary>
        /// The radius secondary readout for a given rank radius and Bellow gear term - always present
        /// (the rank radius is never 0 in shipped configuration, unlike the zero-omission secondaries
        /// on other abilities). Pure for testability, built on <see cref="EffectiveRadius"/> so GetReadout
        /// and this segment cannot drift from Activate's own radius computation.
        /// </summary>
        public static ClassAbilityReadout RadiusSecondary(double baseRadius, double gearRadius = 0.0)
        {
            var gear = Math.Max(0.0, gearRadius);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = baseRadius,
                Affinity = 0.0,
                Gear = gear,
                Effective = EffectiveRadius(baseRadius, gear),
                Unit = "m",
                Label = "radius",
                Per = null,
                CapNote = null,
            };
        }
    }
}