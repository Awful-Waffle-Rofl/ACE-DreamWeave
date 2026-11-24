using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Realms;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// Starts a Balance Trial: refuses in a fixed order with nothing created, then opens a private copy of the room, builds the
    /// session over it, registers it, binds the player and teleports them to the room start. The entry sequence is
    /// PinnacleEncounter's: create, IsDungeon, ValidateInstanceDestination, AdjustDungeon, register, teleport.
    /// </summary>
    public static class TrialStarter
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string EnabledKey = "balance_trials_enabled";

        public const string ScenariosFolder = "scenarios";
        public const string RoomsFolder = "rooms";
        public const string ArchetypesFolder = "archetypes";

        /// <summary>Test seam: the external config directory (live: ExternalConfigDirectory.Resolve(Program.IsRunningInContainer)).</summary>
        internal static Func<string> ExternalConfigDir = () => ExternalConfigDirectory.Resolve(Program.IsRunningInContainer);

        /// <summary>Test seam: the directory-exists probe the data-dir resolution uses.</summary>
        internal static Func<string, bool> DirectoryExists = Directory.Exists;

        /// <summary>The trial data directory, or null with the reason naming the setting.</summary>
        public static string ResolveDataDir(out string reason)
            => TrialDataDir.Resolve(PropertyManager.GetString(TrialDataDir.SettingKey).Item, ExternalConfigDir(), DirectoryExists, out reason);

        /// <summary>The kill-switch refusal, or null when balance_trials_enabled is on. Checked first by start and by rebuild.</summary>
        internal static string DisabledRefusal()
            => PropertyManager.GetBool(EnabledKey).Item ? null : $"Balance Trials are switched off ({EnabledKey} is false)";

        /// <summary>
        /// @trial start's refusal for a master that a copy or purge holds (any hold, shared or exclusive) or that a promote or
        /// unpromote left broken, else null.
        /// </summary>
        internal static string MasterCopyRefusal(TrialCopyGate gate, uint master)
        {
            if (gate.IsBroken(master, out var why))
                return $"master 0x{master:X8} is broken ({why}); {TrialPromote.RecoveryHint}, then start again";

            if (gate.IsBusy(master))
                return $"master 0x{master:X8} is being copied or purged (a promote, unpromote or rebuild is in flight); wait for its 'Successfully copied' line, then start again";

            return null;
        }

        /// <summary>True when <paramref name="guid"/> is one of the rep characters (TrialRoster.RepSlots).</summary>
        public static bool IsRepGuid(uint guid) => TrialRoster.RepSlots.Any(r => r.Guid == guid);

        public static bool TryStart(Player player, string scenarioId, string archetypeId, out string refusal)
        {
            refusal = null;

            if (player == null)
                return Refuse("no player", out refusal);

            // 1. The kill-switch.
            var off = DisabledRefusal();
            if (off != null)
                return Refuse(off, out refusal);

            // 2. Stage only: a data directory must resolve.
            var dataDir = ResolveDataDir(out var dirReason);
            if (dataDir == null)
                return Refuse(dirReason, out refusal);

            // 3. One live session per player. A finished (Done or Aborted) session is replaced on success.
            var bound = player.TrialSession;
            if (bound == null)
                TrialManager.TryGetByPlayer(player.Guid.Full, out bound);
            if (bound != null && bound.Phase != TrialPhase.Done && bound.Phase != TrialPhase.Aborted)
                return Refuse("you are already in a trial; finish or abort it first", out refusal);

            // 4. Only the rep characters run trials.
            if (!IsRepGuid(player.Guid.Full))
                return Refuse($"0x{player.Guid.Full:X8} is not a trial rep character (TrialRoster.RepSlots)", out refusal);

            // 5. The scenario, its room and the archetype must exist in the data directory.
            TrialScenario scenario;
            string scenarioSha;
            TrialRoom room;
            TrialArchetype archetype;
            string archetypeSha;

            try
            {
                var scenarios = TrialScenarioLoader.LoadDirectory(Path.Combine(dataDir, ScenariosFolder));
                var s = scenarios.FirstOrDefault(x => string.Equals(x.Scenario.Id, scenarioId, StringComparison.Ordinal));
                if (s.Scenario == null)
                    return Refuse($"unknown scenario '{scenarioId}'", out refusal);
                (scenario, scenarioSha) = s;

                var rooms = TrialRoomLoader.LoadDirectory(Path.Combine(dataDir, RoomsFolder));
                var r = rooms.FirstOrDefault(x => string.Equals(x.Room.Id, scenario.Room, StringComparison.Ordinal));
                if (r.Room == null)
                    return Refuse($"unknown room '{scenario.Room}' (scenario '{scenario.Id}')", out refusal);
                room = r.Room;

                if (string.IsNullOrWhiteSpace(archetypeId))
                    return Refuse("name an archetype", out refusal);

                var archetypes = TrialArchetypeLoader.LoadDirectory(Path.Combine(dataDir, ArchetypesFolder));
                var a = archetypes.FirstOrDefault(x => string.Equals(x.Archetype.Id, archetypeId, StringComparison.Ordinal));
                if (a.Archetype == null)
                    return Refuse($"unknown archetype '{archetypeId}'", out refusal);
                (archetype, archetypeSha) = a;
            }
            catch (TrialArchetypeException ex)
            {
                return Refuse($"trial data: {ex.Message}", out refusal);
            }

            // 6. The room must fit the scenario.
            var fits = TrialRoomLoader.CheckFits(room, scenario);
            if (fits != null)
                return Refuse(fits, out refusal);

            // 6b. The rep must be able to take the effective buff profile (the scenario's override first), refused here rather
            // than at the first rep.
            var buffProfile = TrialBuffs.EffectiveProfile(scenario, archetype);
            var buffRefusal = TrialBuffs.Precheck(player, buffProfile);
            if (buffRefusal != null)
                return Refuse(buffRefusal, out refusal);

            // 6c. The session reads this archetype's master (its digest, consumables and player side), so it must not be
            // mid-copy or mid-purge, nor left broken by a promote or unpromote (TrialCopyGate). Before anything is created.
            var copyRefusal = MasterCopyRefusal(TrialCopyGate.Shared, TrialRoster.MasterSlot(archetype.Slot));
            if (copyRefusal != null)
                return Refuse(copyRefusal, out refusal);

            // 7. The private copy. Realm 0 always (a realm-1 character would otherwise get its home realm's copy). From the
            // create to the register, any throw sends the copy to the destruction queue and undoes the bind, so a throw leaks
            // neither a landblock nor a half-registered session.
            Landblock landblock = null;
            TrialSession session = null;
            var previouslyBound = player.TrialSession;
            Position start;

            try
            {
                landblock = RealmManager.GetNewEphemeralLandblock(new LandblockId(((uint)room.Landblock << 16) | 0xFFFF), player, realmId: 0, openToFellowship: false, excludedRows: room.ExcludeRows);

                if (landblock == null)
                    return Refuse($"could not open a private copy of 0x{room.Landblock:X4}", out refusal);

                if (!landblock.IsDungeon)
                {
                    log.Error($"[TRIAL] room '{room.Id}' landblock 0x{room.Landblock:X4} is not a dungeon; refusing a start for 0x{player.Guid.Full:X8}");
                    LandblockManager.AddToDestructionQueue(landblock);
                    return Refuse($"room '{room.Id}' landblock 0x{room.Landblock:X4} is not a dungeon", out refusal);
                }

                start = LiveTrialWorld.StartPosition(room, landblock.Instance);

                start.ValidateInstanceDestination(player, out var rejection);

                if (rejection != InstanceRejection.None)
                {
                    log.Error($"[TRIAL] the instance 0x{landblock.Instance:X8} just created does not validate ({rejection}); refusing a start for 0x{player.Guid.Full:X8}");
                    LandblockManager.AddToDestructionQueue(landblock);
                    return Refuse($"the private copy did not validate ({rejection})", out refusal);
                }

                WorldObject.AdjustDungeon(start);

                // Success from here: build, bind, register, teleport.
                var world = new LiveTrialWorld(landblock, player.Guid.Full, room);
                var tag = $"trial:0x{landblock.Instance:X8}";

                // Task 7: the rep's pre-spawn steps. The setup's master-digest answer is marshalled onto the room thread
                // through TrialManager.Enqueue, which finds the session once it is registered below.
                var consumableWcids = TrialDigest.ConsumableWcids(archetype);
                var repSetup = new TrialRepSetup(player.Guid.Full, TrialRoster.MasterSlot(archetype.Slot), scenario.HardCopyEvery, TrialManager.Now,
                    (master, callback) => MasterSnapshotLoader(master, consumableWcids, callback), action => TrialManager.Enqueue(session, action), scenarioSha, buffProfile);

                // The gem rolls with the rep character's seed-walk index (it outlives this session), not this session's rep number.
                session = new TrialSession(Guid.NewGuid().ToString("N"), scenario, scenarioSha, archetype, archetypeSha, room,
                    player.Guid.Full, world, n => PrepareRep(scenario, room, n, tag, () => PreSpawn(session, repSetup, archetype), repSetup.SeedRep()));

                session.RepEnded += (_, _) => repSetup.OnRepEnded();

                // Task 10: each rep's recorder (created at RepStarted) and its trial_run row (written at RepEnded).
                TrialRunLog.Attach(session);

                world.Bind(session);

                // Task 11: the player half of the spawn predictions, from the offline master, marshalled onto the room thread.
                world.PlayerSides = new TrialPlayerSideSource(TrialRoster.MasterSlot(archetype.Slot), TrialManager.Now,
                    PlayerSideLoader, action => TrialManager.Enqueue(session, action));

                // Bound before the teleport, with the entry window open: CheckTrialInstanceExit ignores the session until the
                // player first stands in the room (an earlier teleport landing now is not a way out).
                player.TrialEntryPending = true;
                player.TrialSession = session;
                TrialReset.EnsureAttackable(player);
                TrialManager.Register(session, landblock);

                // Once per session, after Register (the answer may come back synchronously and is enqueued onto the room).
                repSetup.RequestMasterDigest();
                world.PlayerSides.Request();
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] opening the room '{room.Id}' threw for 0x{player.Guid.Full:X8}; nothing started", ex);

                if (session != null)
                {
                    if (ReferenceEquals(player.TrialSession, session))
                    {
                        // Back to what was bound before (a finished trial, or nothing).
                        player.TrialSession = previouslyBound;
                        player.TrialEntryPending = false;
                    }

                    TrialManager.Remove(session);
                }

                if (landblock != null)
                    LandblockManager.AddToDestructionQueue(landblock);

                return Refuse($"opening the room failed: {ex.Message}", out refusal);
            }

            if (bound != null)
                TrialManager.EndSession(bound, "replaced by a new trial");

            log.Info($"[TRIAL] trial {session.TrialUuid} scenario={scenario.Id} room={room.Id} archetype={archetype.Id} for 0x{player.Guid.Full:X8} in instance 0x{landblock.Instance:X8}");

            // The trial remembers the exact stamp it wrote, and only ever clears that one (Player.ClearOwnTrialExitStamp).
            var exitStamp = new Position(player.Location);
            player.SetPosition(PositionType.EphemeralRealmExitTo, exitStamp);
            player.TrialExitStamp = exitStamp;
            WorldManager.ThreadSafeTeleport(player, start, new ActionEventDelegate(() => player.SendWeenieError(WeenieError.ITeleported)), source: "trial-start");

            return true;
        }

        /// <summary>
        /// The prepareRep the live session is built with. For rep <paramref name="n"/>: RollLive (with <paramref name="seedRep"/>,
        /// the rep character's seed-walk index, or n when none is given), BuildPlan (the live builder,
        /// solo), Select (shortfalls go on the rep), Repoint onto the room, then the boss floor's pack term over the WHOLE plan.
        /// Any throw refuses the rep (the session stays Ready) rather than run it half-built.
        /// </summary>
        internal static TrialRep PrepareRep(TrialScenario scenario, TrialRoom room, int n, string tag, Func<TrialRepSetupResult> preSpawn = null, int? seedRep = null)
        {
            try
            {
                var gem = TrialGemRoller.RollLive(scenario.Gem, seedRep ?? n);
                var plan = TrialWavePlan.BuildPlan(gem.Spec, ThreadDungeonSpawner.LivePlanSources.Live, out _);
                var picked = TrialWavePlan.Select(plan, scenario.Waves, out var shortfalls);

                if (shortfalls.Count > 0)
                    return new TrialRep { Gem = gem, Plan = plan, Waves = picked, Shortfalls = shortfalls };

                var waves = picked.Select(w => TrialWavePlan.Repoint(w, room)).ToList();

                // Task 7: the pre-spawn rep steps, in order: the HardCopyEvery schedule, the soft reset, the digest, the drift
                // check (TrialRepSetup.Before). A schedule hit refuses with RebuildRequired; a pending master refuses; drift
                // runs this rep flagged and the session requires a rebuild before the next.
                var setup = preSpawn?.Invoke() ?? default;

                if (setup.RebuildRequired)
                    return new TrialRep { Gem = gem, Plan = plan, Waves = waves, RebuildRequired = true, Refusal = setup.Refusal };

                if (setup.Refusal != null)
                    return new TrialRep { Gem = gem, Plan = plan, Waves = waves, Refusal = setup.Refusal };

                if (setup.Drift)
                    log.Warn($"[TRIAL] {tag} rep {n} state digest drift: {setup.DriftReason}; this rep runs flagged and the next needs @trial rebuild");

                var observed = TrialSpawner.ObservedMaxNonBoss(plan, wcid => WorldObjectFactory.CreateNewWorldObject(wcid) as Creature, tag);

                return new TrialRep { Gem = gem, Plan = plan, Waves = waves, ObservedMaxNonBoss = observed, StateDigest = setup.Digest?.Core, DigestDrift = setup.Drift };
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] {tag} preparing rep {n} threw", ex);
                return new TrialRep { Refusal = $"rep {n} setup failed: {ex.Message}" };
            }
        }

        /// <summary>
        /// Loads the master's snapshot (its digest and consumable stacks, with the archetype's consumable wcids left out of
        /// Core) off the offline master (ProfileBuilder.LoadOfflinePlayerAsync, callback on any thread, null when the master
        /// is online or missing). A test seam.
        /// </summary>
        internal static Action<uint, IReadOnlyCollection<uint>, Action<TrialMasterSnapshot>> MasterSnapshotLoader = (master, consumableWcids, callback) =>
        {
            try
            {
                ACE.Server.CombatSimulator.ProfileBuilder.LoadOfflinePlayerAsync(master, p => TrialMasterSnapshot.Project(p, consumableWcids), callback);
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] loading the master digest of 0x{master:X8} threw", ex);
                callback(null);
            }
        };

        /// <summary>
        /// Loads the player side of the spawn predictions off the offline master (callback on any thread, null when the master is
        /// online, missing, or has no usable attack). A test seam.
        /// </summary>
        internal static Action<uint, Action<TrialPlayerSide>> PlayerSideLoader = (master, callback) =>
        {
            try
            {
                ACE.Server.CombatSimulator.ProfileBuilder.LoadOfflinePlayerAsync(master, TrialPlayerSideSource.Project, callback);
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] loading the player side of 0x{master:X8} threw", ex);
                callback(null);
            }
        };

        /// <summary>
        /// The pre-spawn steps for <paramref name="session"/>'s next rep, on the room thread. The rep character must be online
        /// and bound to this session (it is the one the soft reset and the digest touch).
        /// </summary>
        internal static TrialRepSetupResult PreSpawn(TrialSession session, TrialRepSetup setup, TrialArchetype archetype)
        {
            var player = session == null ? null : TrialManager.OnlinePlayer(session.PlayerGuid);

            if (player == null || !ReferenceEquals(player.TrialSession, session))
                return new TrialRepSetupResult("the rep character is not online in this trial", false, null, false, null);

            // Refused before anything changes the rep, so a refusal never leaves it half reset (Apply keeps the check as a backstop).
            var buffProfile = TrialBuffs.EffectiveProfile(session.Scenario, archetype);
            var buffRefusal = TrialBuffs.Precheck(player, buffProfile);
            if (buffRefusal != null)
                return new TrialRepSetupResult(buffRefusal, false, null, false, null);

            TrialReset.EnsureAttackable(player);

            // The master's consumable counts are in hand whenever Before reaches the soft reset (it refuses until the snapshot is).
            var consumableWcids = TrialDigest.ConsumableWcids(archetype);
            return setup.Before(() => TrialReset.Soft(player, archetype, setup.MasterConsumables, buffProfile), () => TrialDigest.Compute(player, consumableWcids));
        }

        private static bool Refuse(string reason, out string refusal)
        {
            refusal = reason;
            return false;
        }
    }
}
