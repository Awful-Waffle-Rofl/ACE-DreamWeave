using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// A kill-fill vessel: an item carried in the pack that gains one charge each time the player HOLDING
    /// it lands the killing blow on a creature of a named type, inside a named landblock.
    ///
    /// THE HOLDER'S OWN KILLING BLOW IS THE MECHANIC, NOT A LIMITATION OF IT. A fellowship-mate's kill
    /// gives no credit, and neither does a kill the holder merely contributed damage to. That exclusion is
    /// the whole difference between this and an ordinary kill task, which credits everyone in range through
    /// the quest registry; it is deliberate and is not to be "fixed" later. The vessel's own description
    /// text is where the rule is explained to the player, so a player whose fellow landed the blow and saw
    /// no charge can work out why without asking anyone.
    ///
    /// ENTIRELY DATA-DRIVEN. Nothing here names a wcid, a landblock or a creature type. A second vessel -
    /// different creature type, different landblock, different capacity - is a new weenie and no code
    /// change at all:
    ///
    ///   PropertyInt 9038 KillFillCreatureType  the CreatureType whose death fills it (PRESENCE marks the item)
    ///   PropertyInt 9039 KillFillLandblock     OPTIONAL landblock the kill must happen in (0 = anywhere)
    ///   PropertyInt 9040 KillFillRealm         OPTIONAL realm the kill must happen in (ABSENT = any realm)
    ///   PropertyInt   91 MaxStructure          capacity
    ///   PropertyInt   92 Structure             starting charges
    ///
    /// The first user is the Assay Row bay 2 vessel (wcid 1002534, Golem inside 0x564E, realm 1). See
    /// Content/preview/quest_assay_row/PORTAL-PHASE-OPTIONS.md section 6.
    ///
    /// THE TWO LOCATION FILTERS ARE OPTIONAL, AND OMITTING EITHER IS AN AUTHORING DECISION WITH TEETH. A
    /// vessel a quest giver hands out to send the player through a particular portal needs BOTH: without
    /// them the vessel fills wherever its creature type appears, the portal becomes decorative, and the
    /// player farms whatever is most convenient - the failure this repo already paid for once at Shoushi
    /// (Source/.claude/skills/quest-generator/references/quest_archetypes.md, archetype A's turn-in note).
    /// A vessel sold off a vendor shelf has no portal to protect and can legitimately leave them off.
    ///
    /// THE LANDBLOCK ALONE IS NOT ENOUGH, AND THIS IS THE PART THAT IS EASY TO GET WRONG. A landblock id
    /// is shared by every realm's copy of that block, so a landblock filter still admits the retail
    /// original of any dungeon we copied. Bay 2 is the live case: retail portal 22870
    /// portalcrystalminelow lands at cell 0x564E0233, byte-identical to bay 2's own portal 1002521, and
    /// retail 0x564E is populated with copper, granite and sandstone golems that are all CreatureType 13.
    /// Scoping a copied-dungeon vessel therefore means BOTH 9039 and 9040.
    ///
    /// Note the deliberate asymmetry between the two sentinels: 9039 spends 0 on "anywhere" because
    /// landblock 0 is not a real place, while 9040 uses ABSENCE for "any realm" because realm 0 is the
    /// base retail world and has to stay authorable.
    /// </summary>
    public static class KillFillVessel
    {
        /// <summary>
        /// The creature type this vessel fills on, 0 when the property is absent. A value above 0 doubles as
        /// the marker that the item is a kill-fill vessel at all.
        /// </summary>
        public static int GetFillCreatureType(WorldObject vessel) => vessel?.GetProperty(PropertyInt.KillFillCreatureType) ?? 0;

        /// <summary>
        /// The landblock kills must happen in, 0 when the property is absent. 0 means "anywhere" - the
        /// filter is skipped entirely rather than being compared against landblock 0.
        /// </summary>
        public static int GetFillLandblock(WorldObject vessel) => vessel?.GetProperty(PropertyInt.KillFillLandblock) ?? 0;

        /// <summary>
        /// The realm kills must happen in, or NULL for any realm.
        ///
        /// Null rather than a 0 sentinel, deliberately, and unlike <see cref="GetFillLandblock"/>: realm 0
        /// is the base retail world, a real and reachable place, so a vessel authored with realm 0 must
        /// mean "retail only" rather than "anywhere". Landblock 0 is not a real place, so that one can
        /// afford to spend 0 as its sentinel and this one cannot.
        /// </summary>
        public static int? GetFillRealm(WorldObject vessel) => vessel?.GetProperty(PropertyInt.KillFillRealm);

        /// <summary>Charges in the vessel right now, read off Structure. 0 when absent.</summary>
        public static int GetCharges(WorldObject vessel) => vessel?.Structure ?? 0;

        /// <summary>The vessel's capacity, read off MaxStructure. 0 when absent.</summary>
        public static int GetCapacity(WorldObject vessel) => vessel?.MaxStructure ?? 0;

        /// <summary>
        /// TRUE for an item that names a creature type and has a real capacity. The landblock is NOT part of
        /// recognition: a vessel without one is a valid, deliberately unscoped vessel.
        /// </summary>
        public static bool IsKillFillVessel(WorldObject vessel) =>
            GetFillCreatureType(vessel) > 0 && GetCapacity(vessel) > 0;

        /// <summary>TRUE for a vessel that is a kill-fill vessel and is not already full.</summary>
        public static bool HasRoom(WorldObject vessel) =>
            IsKillFillVessel(vessel) && GetCharges(vessel) < GetCapacity(vessel);

        /// <summary>
        /// TRUE when this specific vessel accepts this specific victim. Both halves of the test are read off
        /// the VESSEL, so the pairing is authored data and no creature type or landblock is ever a literal in
        /// this file. A victim with no Location (already removed from the world) never qualifies.
        /// </summary>
        public static bool Accepts(WorldObject vessel, Creature victim) =>
            victim?.Location != null && Accepts(vessel, victim.Location.LandblockId.Landblock, victim.Location.RealmID, (int?)victim.CreatureType);

        /// <summary>
        /// The decision itself, over primitives, so every branch is testable without constructing a Creature
        /// (whose ephemeral setup reaches for the world database and the client dat files).
        /// </summary>
        public static bool Accepts(WorldObject vessel, ushort victimLandblock, ushort victimRealm, int? victimCreatureType)
        {
            if (!IsKillFillVessel(vessel))
                return false;

            var landblock = GetFillLandblock(vessel);

            // 0 is "anywhere" and skips the filter, rather than matching landblock 0
            if (landblock != 0 && victimLandblock != landblock)
                return false;

            var realm = GetFillRealm(vessel);

            // absence is "any realm"; 0 is a real realm and matches only realm 0
            if (realm != null && victimRealm != realm.Value)
                return false;

            return victimCreatureType == GetFillCreatureType(vessel);
        }

        /// <summary>
        /// The first vessel anywhere in this container that has room AND accepts this victim, or null.
        ///
        /// Side packs are searched too, and deliberately: <c>Container.Inventory</c> holds only the items
        /// directly in that container, so a search of the main pack alone would make the feature depend on
        /// where the player happened to stow the vessel - working in the backpack and silently doing nothing
        /// in a side pouch, with no message either way to explain it.
        ///
        /// Exactly ONE vessel is filled per kill, the first found. A player carrying several is not a case
        /// worth paying for, and filling them all would let one kill count many times.
        /// </summary>
        public static WorldObject FindFillable(Container container, Creature victim) =>
            victim?.Location == null ? null : FindFillable(container, victim.Location.LandblockId.Landblock, victim.Location.RealmID, (int?)victim.CreatureType);

        /// <summary>
        /// The search itself, over primitives, so it is testable without constructing a Creature.
        /// </summary>
        public static WorldObject FindFillable(Container container, ushort victimLandblock, ushort victimRealm, int? victimCreatureType)
        {
            if (container == null)
                return null;

            foreach (var item in container.Inventory.Values)
            {
                if (HasRoom(item) && Accepts(item, victimLandblock, victimRealm, victimCreatureType))
                    return item;
            }

            foreach (var item in container.Inventory.Values)
            {
                if (item is Container sideContainer)
                {
                    var found = FindFillable(sideContainer, victimLandblock, victimRealm, victimCreatureType);

                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        /// <summary>
        /// The charge count after one qualifying kill, clamped to capacity. Kept separate from the write so
        /// the clamp is testable without a Player.
        /// </summary>
        public static int NextCharges(int charges, int capacity)
        {
            if (charges >= capacity)
                return capacity;

            return charges + 1;
        }

        /// <summary>
        /// The quest stamped on the holder the moment a vessel reaches capacity. It exists because the emote
        /// layer cannot inspect a given item's properties at all - an NPC's Give/Refuse handler runs with the
        /// PLAYER as its target object, so InqIntStat on the turn-in reads the player's Structure, not the
        /// vessel's. This stamp is the only bridge from "the vessel is full" to something an emote can test.
        /// The quest row is Content/sql/quests/AssayRowVesselFull.sql (max_Solves 1, so InqQuest, which is
        /// HasQuest AND NOT CanSolve, reads TRUE once stamped).
        ///
        /// One name is shared by every vessel on purpose: only one vessel is ever in play at a time in the
        /// arc that uses it. A second concurrent vessel family would need its own stamp name, which is the
        /// one thing on this class that is not yet data-driven.
        /// </summary>
        public const string FullQuestName = "AssayRowVesselFull";
    }
}
