using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Factories.Tables;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The item a salvage-affinity modifier injects onto a run creature's corpse: an ORDINARY weenie, restamped
    /// with the modifier's material, that the player then salvages themselves through the normal panel.
    ///
    /// THE TRAP this class exists to close. Player_Crafting.HandleSalvaging skips any item where
    /// `Workmanship == null || Retained` with a bare `continue` - no player message and no log line, unlike the
    /// MaterialType check immediately above it, which at least warns. WorldObject.Workmanship returns null
    /// exactly when PropertyInt.ItemWorkmanship is null, and NONE of the three shipped base weenies carries it
    /// (verified against ace_world on 2026-09-06: wcids 243, 2393 and 2398 have neither MaterialType nor
    /// ItemWorkmanship except the two gems' own material). An injected item that skipped
    /// <see cref="Stamp"/> would therefore drop, appraise correctly as its material, and be SILENTLY IGNORED by
    /// the salvage panel - it would look completely right and do nothing.
    ///
    /// ItemType gates nothing on this path. Player_Crafting reads it in exactly three places (Player_Crafting.cs
    /// :238, :336, :385) and all three ask the same question - "is this an existing salvage bag being combined",
    /// i.e. ItemType == TinkeringMaterial. ItemType 256 dinnerware and ItemType 2048 gems both answer no and take
    /// the identical fresh-salvage path, which is what lets Obsidian ship on a dinner plate.
    /// </summary>
    public static class DungeonSalvageAffinity
    {
        /// <summary>
        /// Builds one injected item, or null when the base wcid does not resolve to a world object (already a
        /// load-time diagnostic - see ThreadDungeonStore.ValidateWorldWcids - so the caller just skips it).
        /// </summary>
        public static WorldObject TryCreate(uint baseWcid, int materialId, int tier)
        {
            if (baseWcid == 0)
                return null;

            var wo = WorldObjectFactory.CreateNewWorldObject(baseWcid);

            if (wo == null)
                return null;

            Stamp(wo, materialId, tier);
            return wo;
        }

        /// <summary>
        /// Stamps the material and a rolled workmanship onto an already-created item.
        ///
        /// The workmanship comes from WorkmanshipChance.Roll(tier) - the same call a naturally generated gem
        /// gets from LootGenerationFactory.MutateGem - so an injected item is indistinguishable from a rolled
        /// one, and it is what makes the item salvageable at all (see the class remarks).
        ///
        /// Separated from <see cref="TryCreate"/> so it can be unit-tested: WorldObjectFactory needs the world
        /// database, while this half needs nothing but the object.
        /// </summary>
        internal static void Stamp(WorldObject wo, int materialId, int tier)
        {
            if (wo == null)
                return;

            wo.MaterialType = (MaterialType)materialId;
            wo.ItemWorkmanship = WorkmanshipChance.Roll(tier);
        }
    }
}
