using System;
using System.Reflection;

using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WaveEncounters
{
    /// <summary>
    /// Places one roster creature for a wave encounter. Called only from the world-thread tick.
    ///
    /// The fixed order, as in MlDigsiteSpawner and Player_WaveChallenge.SpawnWave:
    ///   create -> fail closed on a non-Creature -> position in the anchor's instance, same-landblock check
    ///   -> ALL stamps -> Adopt -> EnterWorld -> TrackAlive.
    ///
    /// Every property write happens BEFORE EnterWorld, so no landblock save and no client create packet ever
    /// sees the creature as an ordinary persistable monster. TimeToRot = -1 is mandatory: the decay pass
    /// destroys with a bare Destroy() and no Die(), so a creature that rotted mid-fight would never report
    /// its death and its wave could never clear. Crowd scaling (PropertyBool 9063) needs nothing here - it
    /// resolves inside WorldObject.EnterWorld, at the creature's own spawn moment, from its Location.
    /// </summary>
    public static class WaveEncounterSpawner
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static Creature TrySpawn(WaveEncounter encounter, WaveRosterEntry entry, int wave)
        {
            if (encounter?.AnchorPosition == null)
                return null;

            var anchor = encounter.AnchorPosition;

            var wo = WorldObjectFactory.CreateNewWorldObject(entry.Wcid);

            if (wo == null)
            {
                log.Error($"[WAVE_ENCOUNTER] {encounter} wave {wave}: failed to create wcid {entry.Wcid}; is its weenie applied to this world database?");
                return null;
            }

            // Only a Creature can die and report back; anything else would hold its wave open forever.
            if (!(wo is Creature creature))
            {
                log.Error($"[WAVE_ENCOUNTER] {encounter} wave {wave}: wcid {entry.Wcid} is a {wo.WeenieType}, not a Creature; dropped");
                wo.Destroy();
                return null;
            }

            creature.Location = entry.ToPosition(anchor.Instance);

            // Belt and braces over WaveRoster.Read's landblock filter: the instanced landblock must match
            // exactly, or the creature would stand outside the encounter's presence scan and cleanup.
            if (creature.Location.InstancedLandblock != anchor.InstancedLandblock)
            {
                log.Error($"[WAVE_ENCOUNTER] {encounter} wave {wave}: wcid {entry.Wcid} cell 0x{entry.ObjCellId:X8} is not in the anchor's landblock instance; dropped");
                creature.Destroy();
                return null;
            }

            // ---- every stamp, before EnterWorld ----

            // The persistence exclusion, the orphan filter's marker, and the death hook's persisted key.
            creature.SetProperty(PropertyInt.WaveEncounterId, (int)encounter.EncounterId);

            creature.TimeToRot = -1;

            // The in-memory half of the two-key death check. Nulled when the encounter ends.
            creature.P_WaveEncounter = encounter;

            if (!encounter.Adopt(creature))
            {
                creature.P_WaveEncounter = null;
                creature.Destroy();
                return null;
            }

            bool entered;

            try
            {
                entered = creature.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[WAVE_ENCOUNTER] {encounter} wave {wave}: EnterWorld threw for wcid {entry.Wcid} at {creature.Location.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                log.Warn($"[WAVE_ENCOUNTER] {encounter} wave {wave}: wcid {entry.Wcid} failed to enter the world at {creature.Location.ToLOCString()}");
                creature.P_WaveEncounter = null;
                creature.Destroy();
                return null;
            }

            encounter.TrackAlive(creature);

            return creature;
        }
    }
}
