using System;
using System.Linq;

using ACE.Server.Entity.Facets;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Equipment-mod read side. Mod magnitudes are recomputed per combat event, exactly like class ability
    /// tunables, so there is deliberately NO cache here and therefore no invalidation problem: equipping,
    /// unequipping, rerolling or retuning a mod is picked up on the next event with no bookkeeping. (Contrast
    /// the Gear* rating cache in Creature_Equipment.cs, which this system does not touch.)
    /// </summary>
    partial class Creature
    {
        /// <summary>
        /// Sums this creature's equipped items' stored POTENCY for one mod type, clamping each item's
        /// contribution to [0, 1] individually, then applying the mod's <see cref="EquipmentModDefinition.StackCap"/>
        /// (if any) to the sum. The per-instance clamp is shard-contamination defense (a rollback can
        /// resurrect rows written by an older build); the SUM itself is uncapped UNLESS the mod declares a
        /// StackCap, per the no-cross-item-cap ruling (user, 2026-07-24 - RNG scarcity and one-mod-type-per-item
        /// are the governors) - that ruling holds only for rows that leave StackCap at its 0/unset default.
        /// This is the single place the sum-and-cap logic lives; <see cref="GetEquippedModValue"/> calls this
        /// method rather than duplicating the expression, so there is no path that returns an uncapped sum for
        /// a mod that declares a cap.
        /// Returns 0 when the equipment-mod feature is disabled.
        /// </summary>
        public double GetEquippedModPotencySum(EquipmentModId modId)
        {
            if (!PropertyManager.GetBool("equipment_mods_enabled").Item)
                return 0.0;

            if (!EquipmentModRegistry.TryGet(modId, out var definition))
                return 0.0;

            var sum = EquippedObjects.Values.Sum(i => EquipmentModRoller.Clamp01(i.GetProperty(definition.Property) ?? 0.0));

            return definition.StackCap > 0.0 ? Math.Min(sum, definition.StackCap) : sum;
        }

        /// <summary>
        /// The applied value of one mod type across all equipped items, in the hook's own units: the capped
        /// potency sum (see <see cref="GetEquippedModPotencySum"/>) times the mod's resolved per-potency
        /// magnitude (registry max x equipment_mod_potency_scale). This is what a combat hook reads.
        /// Returns 0 when the equipment-mod feature is disabled.
        /// </summary>
        public double GetEquippedModValue(EquipmentModId modId)
        {
            if (!PropertyManager.GetBool("equipment_mods_enabled").Item)
                return 0.0;

            if (!EquipmentModRegistry.TryGet(modId, out var definition))
                return 0.0;

            var potencySum = GetEquippedModPotencySum(modId);

            // PK facet: equipment mods do not work there while the facet PK rule is active
            // (Player.IsPkFacetRuleActive). This is the single read every mod's combat effect goes through -
            // the standalone rank-0 paths (GetStandaloneEquipmentModValue), the machinery paths
            // (GetMachineryEquipmentModValue) and every ability handler's own gear term - so zeroing it here
            // suppresses all of them at once. The predicate costs nothing for a non-player or a player off the
            // PK facet. Potency readers are untouched: the appraisal text (EquipmentModDisplay) still shows
            // what the gear carries, and the equipment mod command lists the carried potency beside an
            // applied value that reads 0 here. The PvP arena equipment-mod mask joins through the combined
            // predicate (Player.EquipmentModSuppressed = facet rule OR arena mask, Docs/Pvp/DESIGN.md H9).
            return FacetPk.EquipmentModValue(this is Player player && player.EquipmentModSuppressed, potencySum * EquipmentModValue.MagnitudePerPotency(definition));
        }
    }
}
