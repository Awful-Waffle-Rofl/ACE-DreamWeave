using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The one place every Thread encounter object (shrine, pillar, chest, NPC, wisp, golem in its dormant form, ...) is made
    /// selectable and shown on the radar (owner ruling 2026-11-21, "Visual indicators on shrines" Discord post). Called from
    /// ThreadEncounterInstance.Track (and directly for the reward cache chests and the summoned exit portal, which are not
    /// tracked), which every encounter type already calls before EnterWorld, so a new encounter type gets
    /// it by tracking its objects and nothing else. Properties are set on the spawned INSTANCE, never on the shared weenie.
    ///
    /// What reaches the client (WorldObject_Networking.cs): ShowableOnRadar is the RadarBehavior byte and RadarBlipColor the
    /// RadarColor byte, each written into the create-object weenie header when the property is non-null
    /// (CalculateWeenieHeaderFlag, :816-820; SerializeCreateObject, :152-156). UiHidden is the UiHidden bit of the object
    /// description flags (UpdateObjectDescriptionFlags, :910): set, the client does not let the object be selected.
    /// Visibility (PropertyBool 18) is the admin-only draw gate. Ethereal and Attackable are deliberately left alone: they
    /// change movement and combat, not selection.
    ///
    /// Monsters (a Creature that is Attackable) are excluded: they already show as creatures and are not interactables.
    /// </summary>
    public static class ThreadEncounterObjectVisibility
    {
        /// <summary>
        /// The blip colour an encounter object gets when its weenie carries none. Yellow is what the shrine, the Trialmaster, the
        /// golem, the chest and the peddler weenies already ship (RadarColor.NPC / Vendor are Yellow too).
        /// </summary>
        public const RadarColor DefaultBlip = RadarColor.Yellow;

        /// <summary>The radar behaviour every encounter object gets: always on the radar, not only while moving or attacking.</summary>
        public const RadarBehavior Behavior = RadarBehavior.ShowAlways;

        /// <summary>Is this a hostile creature (not an encounter object)? Pure.</summary>
        public static bool IsMonster(WorldObject wo) => wo is Creature creature && creature.Attackable;

        /// <summary>
        /// Makes <paramref name="wo"/> selectable and radar-visible. Returns false (and changes nothing) for null or a monster.
        /// A blip colour the weenie already carries (a purple portal, say) is kept. Call before EnterWorld.
        /// </summary>
        public static bool Apply(WorldObject wo)
        {
            if (wo == null || IsMonster(wo))
                return false;

            wo.RadarBehavior = Behavior;

            if (wo.RadarColor == null || wo.RadarColor == RadarColor.Default)
                wo.RadarColor = wo is Portal ? RadarColor.Portal : DefaultBlip;

            wo.UiHidden = false;
            // SetProperty(false) is required: the setter's remove path is a no-op for an ephemeral property that was never set, so a weenie-set true would survive.
            wo.SetProperty(PropertyBool.Visibility, false);

            return true;
        }
    }
}
