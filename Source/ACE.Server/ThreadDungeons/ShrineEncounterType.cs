using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using log4net;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// A shrine row's parsed config (encounters.json "config"). Defaults when a key is absent: the Wayfarer's Shrine
    /// wcid, 5 to 8 guardians, and all six blessings at the run's cap with weight 1.
    /// </summary>
    public sealed class ShrineConfig
    {
        public uint Wcid { get; init; } = ShrineEncounterType.DefaultWcid;
        public int AddsMin { get; init; } = 5;
        public int AddsMax { get; init; } = 8;

        /// <summary>The blessing table, magnitudes already clamped to the cap the config was parsed with.</summary>
        public IReadOnlyList<ShrineBoonRow> Boons { get; init; } = Array.Empty<ShrineBoonRow>();

        public const int MaxAdds = 12;
    }

    /// <summary>
    /// The Wayfarer's Shrine (Docs/Threads/ENCOUNTERS.md "Shrine"), the first concrete Thread encounter type. One object;
    /// its first Use by a roster member inside the run grants ONE random blessing to every member inside at that moment,
    /// for the rest of the run and only while inside it, wakes a few guardians from the run's trash pool whose kills do
    /// not count toward the clear, and spends the shrine (destroyed about 3 s later).
    ///
    /// Threading. Place runs on the point's landblock queue (the framework's rule) and enters the shrine there. OnUse
    /// runs on the user's thread: the grant itself is one locked call on the run (ThreadDungeonRun.TryUseShrine), and
    /// every world-object change after it (guardians, the destroy) is queued onto the shrine's own landblock through an
    /// ActionChain on the shrine. OnRunEnded releases in-memory state only.
    /// </summary>
    public sealed class ShrineEncounterType : IThreadEncounterType, IThreadEncounterUsable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string KindName = "shrine";

        /// <summary>The Wayfarer's Shrine weenie (Content/sql/weenies/1007950 Wayfarers Shrine.sql; wcid-registry.d/wcid-1007950.tsv).</summary>
        public const uint DefaultWcid = 1007950;

        /// <summary>Seconds between the use and the spent shrine's destroy.</summary>
        public const float SpentDestroyDelaySeconds = 3.0f;

        /// <summary>How far from the shrine guardians may stand (NearbyPoints radius, same landblock only).</summary>
        public const float GuardianRadius = ThreadEncounterPlaceRequest.DefaultNearbyRadius;

        public string Kind => KindName;

        /// <summary>Test seam: the object factory. Production is WorldObjectFactory.</summary>
        internal static Func<uint, WorldObject> CreateObject = wcid => WorldObjectFactory.CreateNewWorldObject(wcid);

        /// <summary>Test seam: the blessing draw, uniform in [0, 1].</summary>
        internal static Func<double> Draw = () => ThreadSafeRandom.Next(0.0f, 1.0f);

        /// <summary>The per-instance state: the use latch and what the guardians are built from. Released by OnRunEnded.</summary>
        internal sealed class ShrineState
        {
            public ShrineState(ThreadEncounterPlaceRequest request, ShrineConfig config)
            {
                Request = request;
                Config = config;
            }

            public ShrineLatch Latch { get; } = new ShrineLatch();

            public ShrineConfig Config { get; }

            /// <summary>The shrine object's guid. The instance also tracks its guardians, and using one of THEM is not a shrine use.</summary>
            public uint ShrineGuid;

            /// <summary>The Place request (run, the shrine's landblock, context, point). Null once the run ended.</summary>
            public volatile ThreadEncounterPlaceRequest Request;
        }

        // ---- config ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Parses a shrine row's config. An absent or empty config yields the defaults. A config that is not an object,
        /// a bad wcid, a bad adds block, or a boons array with no usable row is an error (null with <paramref name="error"/>).
        /// A single bad boon row (unknown kind, magnitude below 1, bad weight) is skipped and noted in
        /// <paramref name="problems"/>. Magnitudes above <paramref name="cap"/> are clamped to it. Pure.
        /// </summary>
        public static ShrineConfig ParseConfig(JsonElement config, double cap, List<string> problems, out string error)
        {
            error = null;
            problems ??= new List<string>();
            cap = ThreadDungeonRun.ClampShrineBoonCap(cap);

            if (config.ValueKind == JsonValueKind.Undefined || config.ValueKind == JsonValueKind.Null)
                return new ShrineConfig { Boons = DefaultBoons(cap) };

            if (config.ValueKind != JsonValueKind.Object)
            {
                error = $"config must be an object, not {config.ValueKind}";
                return null;
            }

            var wcid = DefaultWcid;

            if (config.TryGetProperty("wcid", out var w))
            {
                if (w.ValueKind != JsonValueKind.Number || !w.TryGetUInt32(out wcid) || wcid == 0)
                {
                    error = "config.wcid must be a positive whole number";
                    return null;
                }
            }

            int addsMin = 5, addsMax = 8;

            if (config.TryGetProperty("adds", out var adds))
            {
                if (adds.ValueKind != JsonValueKind.Object
                    || !TryReadInt(adds, "min", 5, out addsMin) || !TryReadInt(adds, "max", 8, out addsMax)
                    || addsMin < 0 || addsMax < addsMin || addsMax > ShrineConfig.MaxAdds)
                {
                    error = $"config.adds must be {{ min, max }} with 0 <= min <= max <= {ShrineConfig.MaxAdds}";
                    return null;
                }
            }

            IReadOnlyList<ShrineBoonRow> boons;

            if (config.TryGetProperty("boons", out var rows))
            {
                if (rows.ValueKind != JsonValueKind.Array)
                {
                    error = "config.boons must be an array";
                    return null;
                }

                var parsed = new List<ShrineBoonRow>();
                var i = 0;

                foreach (var row in rows.EnumerateArray())
                {
                    var label = $"boons[{i++}]";

                    if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String
                        || !ThreadBoons.TryParseKind(k.GetString(), out var kind))
                    {
                        problems.Add($"{label}: unknown or missing kind; skipped");
                        continue;
                    }

                    var magnitude = cap;

                    if (row.TryGetProperty("magnitude", out var m) && (m.ValueKind != JsonValueKind.Number || !m.TryGetDouble(out magnitude)))
                    {
                        problems.Add($"{label}: magnitude must be a number; skipped");
                        continue;
                    }

                    var weight = 1.0;

                    if (row.TryGetProperty("weight", out var wt) && (wt.ValueKind != JsonValueKind.Number || !wt.TryGetDouble(out weight)))
                    {
                        problems.Add($"{label}: weight must be a number; skipped");
                        continue;
                    }

                    if (!double.IsFinite(magnitude) || magnitude <= 1.0)
                    {
                        problems.Add($"{label}: magnitude {magnitude.ToString(CultureInfo.InvariantCulture)} must be above 1; skipped");
                        continue;
                    }

                    if (!double.IsFinite(weight) || weight <= 0)
                    {
                        problems.Add($"{label}: weight {weight.ToString(CultureInfo.InvariantCulture)} must be above 0; skipped");
                        continue;
                    }

                    if (parsed.Any(p => p.Kind == kind))
                    {
                        problems.Add($"{label}: kind {kind} repeated; the first stands");
                        continue;
                    }

                    if (magnitude > cap)
                        problems.Add($"{label}: magnitude {magnitude.ToString(CultureInfo.InvariantCulture)} clamped to the cap {cap.ToString(CultureInfo.InvariantCulture)}");

                    parsed.Add(new ShrineBoonRow(kind, Math.Min(magnitude, cap), weight));
                }

                if (parsed.Count == 0)
                {
                    error = "config.boons has no usable row";
                    return null;
                }

                boons = parsed;
            }
            else
                boons = DefaultBoons(cap);

            return new ShrineConfig { Wcid = wcid, AddsMin = addsMin, AddsMax = addsMax, Boons = boons };
        }

        private static bool TryReadInt(JsonElement obj, string name, int fallback, out int value)
        {
            value = fallback;

            if (!obj.TryGetProperty(name, out var e))
                return true;

            return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
        }

        /// <summary>All six kinds at <paramref name="cap"/>, weight 1, in kind order.</summary>
        public static IReadOnlyList<ShrineBoonRow> DefaultBoons(double cap)
            => Enum.GetValues(typeof(ThreadBoonKind)).Cast<ThreadBoonKind>().Select(k => new ShrineBoonRow(k, cap, 1.0)).ToList();

        // ---- guardians (pure planning) ---------------------------------------------------------------------------

        /// <summary>
        /// The guardians one use wakes: between AddsMin and AddsMax (inclusive) creatures drawn from the plan's TRASH
        /// entries (their uplift and stamp levels carried over, so they are shaped exactly as the run's own trash), each
        /// standing on one of <paramref name="nearby"/> in order, reusing them from the nearest when there are too few,
        /// and on the shrine's own point when there are none. Only points on the shrine's landblock are ever used (the
        /// threading rule). Empty when the plan has no trash. Pure over <paramref name="rng"/>.
        /// </summary>
        public static List<DungeonSpawnPlanEntry> PlanGuardians(ShrineConfig config, IReadOnlyList<DungeonSpawnPlanEntry> planEntries,
            IReadOnlyList<DungeonSpawnPointDef> nearby, DungeonSpawnPointDef shrinePoint, Random rng)
        {
            var result = new List<DungeonSpawnPlanEntry>();

            if (config == null || shrinePoint == null || rng == null)
                return result;

            var pool = ThreadEncounterTrash.Pool(planEntries);

            if (pool.Count == 0)
                return result;

            var landblock = (ushort)(shrinePoint.Cell >> 16);
            var points = (nearby ?? Array.Empty<DungeonSpawnPointDef>()).Where(p => p != null && (ushort)(p.Cell >> 16) == landblock).ToList();

            if (points.Count == 0)
                points.Add(shrinePoint);

            var count = rng.Next(config.AddsMin, config.AddsMax + 1);

            for (var i = 0; i < count; i++)
                result.Add(ThreadEncounterTrash.Draw(pool, points[i % points.Count], rng));

            return result;
        }

        // ---- IThreadEncounterType -------------------------------------------------------------------------------

        public bool Place(ThreadEncounterPlaceRequest request, out string failure)
        {
            failure = null;

            // A cap of 1.0 (dynamic_dungeons_shrine_boon_cap at its floor) makes every blessing nothing: a shrine placed
            // then could only ever say "already holds every blessing", which is untrue. Not placed at all instead.
            if (!CapAllowsBlessings(request.Run.ShrineBoonCap))
            {
                failure = $"dynamic_dungeons_shrine_boon_cap is {request.Run.ShrineBoonCap.ToString(CultureInfo.InvariantCulture)}: no blessing above 1.0 to grant";
                return false;
            }

            var problems = new List<string>();
            var config = ParseConfig(request.Def?.Config ?? default, request.Run.ShrineBoonCap, problems, out var error);

            foreach (var problem in problems)
                request.Run.RecordEncounterOutcome($"{request.Def?.Id}: {problem}");

            if (config == null)
            {
                failure = error;
                return false;
            }

            var shrine = CreateObject(config.Wcid);

            if (shrine == null)
            {
                failure = $"wcid {config.Wcid} did not create - is its weenie in the world database?";
                return false;
            }

            shrine.Location = request.PositionOf(request.Point.Point);
            shrine.TimeToRot = -1;
            shrine.IsTransientSpawn = true;

            request.Instance.State = new ShrineState(request, config) { ShrineGuid = shrine.Guid.Full };

            // Before EnterWorld: a use can arrive the moment it is visible.
            request.Instance.Track(shrine);

            bool entered;

            try
            {
                entered = shrine.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {request.Run} shrine EnterWorld threw at {shrine.Location?.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                shrine.P_ThreadEncounter = null;
                try { shrine.Destroy(); } catch { }
                request.Instance.State = null;
                failure = $"wcid {config.Wcid} failed to enter the world at {shrine.Location?.ToLOCString()}";
                return false;
            }

            return true;
        }

        public void OnRunEnded(ThreadEncounterInstance instance, string why)
        {
            if (instance?.State is ShrineState state)
                state.Request = null;

            // The run's blessings end with it. Idempotent: every shrine of the run calls this.
            instance?.Run.ReleaseBoons();
        }

        // ---- IThreadEncounterUsable ------------------------------------------------------------------------------

        public void OnUse(ThreadEncounterInstance instance, WorldObject used, Player player)
        {
            if (!(instance?.State is ShrineState state) || player == null)
                return;

            // The instance tracks the guardians too: only a use of the shrine itself is a shrine use.
            if (!IsTheShrine(state, used))
                return;

            var run = instance.Run;
            var userInside = ThreadRunPresence.IsMemberInside(run, player);
            var recipients = userInside ? ThreadRunPresence.OnlineMembersInside(run) : new List<Player>();

            var outcome = Activate(run, state, userInside, recipients.Select(p => p.Guid.Full).ToList(), out var kind, out var magnitude);

            switch (outcome)
            {
                case ShrineUseOutcome.Spent:
                    Send(player, ThreadBoons.SpentLine);
                    return;

                case ShrineUseOutcome.NotInside:
                    // Only a roster member standing in the run may use it; anyone else (an admin visiting the copy, a
                    // member the puzzle policy removed) is refused without spending it.
                    Send(player, ThreadBoons.RefusedLine);

                    if (log.IsDebugEnabled)
                        log.Debug($"[DYNDUNGEON] {run} {instance} use by {player.Name} refused ({outcome})");
                    return;

                case ShrineUseOutcome.RunOver:
                    if (log.IsDebugEnabled)
                        log.Debug($"[DYNDUNGEON] {run} {instance} use by {player.Name} refused ({outcome})");
                    return;

                case ShrineUseOutcome.Silent:
                    Send(player, ThreadBoons.SilentLine);
                    ScheduleAfterUse(instance, state, used, spawnGuardians: false);
                    run.RecordEncounterOutcome($"{instance}: used by {player.Name}; silent (every blessing already held)");
                    return;

                case ShrineUseOutcome.Granted:
                    var line = ThreadBoons.GrantLine(kind, magnitude);

                    // Each recipient: their blessing line, then the guardians line.
                    foreach (var recipient in recipients)
                    {
                        Send(recipient, line);
                        Send(recipient, ThreadBoons.GuardiansLine);
                        PlayBlessingVisual(recipient, kind);
                    }

                    ScheduleAfterUse(instance, state, used, spawnGuardians: true);

                    run.RecordEncounterOutcome(string.Format(CultureInfo.InvariantCulture, "{0}: used by {1}; {2} x{3:0.##} to {4} member(s)",
                        instance, player.Name, kind, magnitude, recipients.Count));
                    log.Info($"[DYNDUNGEON] {run} {instance} used by {player.Name}: {kind} x{magnitude.ToString("0.##", CultureInfo.InvariantCulture)} to {recipients.Count} member(s)");
                    return;
            }
        }

        /// <summary>
        /// The one-shot visual per blessing kind, played on each recipient at the moment of the grant (owner-approved 2026-11-21,
        /// Discord "Visual indicators on shrines"). Every script is an entry of the Golem encounter's colour-per-buff bank
        /// (GolemEncounterType.AuraScripts, indexed by GolemBuff), so no new effect is introduced; the buff named beside each
        /// row is the bank slot it borrows, chosen for the closest colour to what the blessing does. One row per
        /// ThreadBoonKind (a test pins that the table is complete). Not replayed on re-entry: only the grant plays it.
        /// </summary>
        public static readonly IReadOnlyDictionary<ThreadBoonKind, PlayScript> BlessingScripts = new Dictionary<ThreadBoonKind, PlayScript>
        {
            [ThreadBoonKind.Damage] = GolemEncounterType.AuraScripts[(int)GolemBuff.Might],              // red: hits harder
            [ThreadBoonKind.Mitigation] = GolemEncounterType.AuraScripts[(int)GolemBuff.Bulwark],        // grey shield: takes less
            [ThreadBoonKind.AttackSpeed] = GolemEncounterType.AuraScripts[(int)GolemBuff.Frenzy],        // yellow: swings faster
            [ThreadBoonKind.Xp] = GolemEncounterType.AuraScripts[(int)GolemBuff.Vigor],                  // green: growth
            [ThreadBoonKind.Luminance] = GolemEncounterType.AuraScripts[(int)GolemBuff.Ruin],            // purple: arcane currency
            [ThreadBoonKind.Loot] = GolemEncounterType.AuraScripts[(int)GolemBuff.KeenEdge],             // orange: treasure
        };

        /// <summary>Queues the blessing's one-shot script onto the recipient's own landblock queue (the grant runs on the user's thread). Never throws.</summary>
        private static void PlayBlessingVisual(Player recipient, ThreadBoonKind kind)
        {
            if (recipient == null || !BlessingScripts.TryGetValue(kind, out var script))
                return;

            try
            {
                var chain = new ActionChain();
                chain.AddAction(recipient, () => recipient.PlayParticleEffect(script, recipient.Guid));
                chain.EnqueueChain();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] blessing visual {kind} for {recipient.Name} threw", ex);
            }
        }

        /// <summary>Can a run stamped with this cap grant anything? False at (or, NaN aside, below) 1.0 after the clamp. Pure.</summary>
        internal static bool CapAllowsBlessings(double cap) => ThreadDungeonRun.ClampShrineBoonCap(cap) > 1.0;

        /// <summary>Is <paramref name="used"/> the shrine this state belongs to (not one of its guardians)? Pure.</summary>
        internal static bool IsTheShrine(ShrineState state, WorldObject used)
            => state != null && used != null && state.ShrineGuid != 0 && used.Guid.Full == state.ShrineGuid;

        /// <summary>The Player-free core of a use: one locked grant on the run. Tests drive it with guids.</summary>
        internal static ShrineUseOutcome Activate(ThreadDungeonRun run, ShrineState state, bool userInside, IReadOnlyCollection<uint> recipients,
            out ThreadBoonKind kind, out double magnitude)
            => run.TryUseShrine(state.Latch, userInside, recipients, state.Config.Boons, Draw, out kind, out magnitude);

        private static void Send(Player player, string line)
        {
            if (!string.IsNullOrEmpty(line))
                player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        /// <summary>
        /// Everything after a spending use, on the SHRINE's landblock queue (an ActionChain on the shrine): the guardians
        /// first (when granted), then the destroy <see cref="SpentDestroyDelaySeconds"/> later.
        /// </summary>
        private static void ScheduleAfterUse(ThreadEncounterInstance instance, ShrineState state, WorldObject shrine, bool spawnGuardians)
        {
            if (shrine?.CurrentLandblock == null)
                return;

            var chain = new ActionChain();

            if (spawnGuardians)
                chain.AddAction(shrine, () => SpawnGuardians(instance, state));

            chain.AddDelaySeconds(SpentDestroyDelaySeconds);
            chain.AddAction(shrine, () =>
            {
                if (!shrine.IsDestroyed)
                    shrine.Destroy();
            });

            chain.EnqueueChain();
        }

        /// <summary>Shrine landblock queue: creates, shapes, binds and enters the guardians. Never throws.</summary>
        private static void SpawnGuardians(ThreadEncounterInstance instance, ShrineState state)
        {
            var request = state.Request;
            var run = instance.Run;

            if (request == null || run.State == ThreadDungeonRunState.Ended || run.EncountersEnded || request.Context?.Plan == null)
                return;

            var placed = 0;

            try
            {
                var rng = new Random(unchecked(request.Seed * 31 + 0x5A17));
                var planned = PlanGuardians(state.Config, request.Context.Plan.Entries, request.NearbyPoints(GuardianRadius), request.Point.Point, rng);

                foreach (var entry in planned)
                {
                    if (TrySpawnGuardian(request, entry))
                        placed++;
                }

                run.RecordEncounterOutcome($"{instance}: {placed}/{planned.Count} guardian(s) woke");
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} {instance} guardians threw after {placed} placed", ex);
            }
        }

        private static bool TrySpawnGuardian(ThreadEncounterPlaceRequest request, DungeonSpawnPlanEntry entry)
        {
            var wo = CreateObject(entry.Wcid);

            if (!(wo is Creature creature))
            {
                wo?.Destroy();
                return false;
            }

            if (!DungeonPopulationBuilder.IsKillable(creature.Attackable, creature.PlayerKillerStatus))
            {
                creature.Destroy();
                return false;
            }

            creature.Location = request.PositionOf(entry.Point);

            // The threading rule: only ever onto the landblock this queue belongs to.
            if (request.Landblock != null && creature.Location.LandblockId.Landblock != request.Landblock.Id.Landblock)
            {
                creature.Destroy();
                return false;
            }

            request.ShapeCreature(creature, entry, new CreatureShapeOptions
            {
                Run = request.Run,
                Rewards = true,
                GroupEffort = request.Run.Group.Effort,
                ExcludedFromClear = true,
            });

            request.BindToRunPerimeter(creature, entry.Point);
            request.Instance.Track(creature);

            bool entered;

            try
            {
                entered = creature.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {request.Run} guardian EnterWorld threw for wcid {entry.Wcid}", ex);
                entered = false;
            }

            if (!entered)
            {
                creature.P_ThreadEncounter = null;
                try { creature.Destroy(); } catch { }
                return false;
            }

            return true;
        }
    }
}
