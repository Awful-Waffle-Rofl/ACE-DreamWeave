using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Entity;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rating;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The PvP arena match coordinator (Docs/Pvp/DESIGN.md "Architecture", "Modes", "Rating", "Enter and exit").
    /// Owns the queues (one room per mode), every live match, the decline lockouts and the in-memory ladders, and
    /// drives each match through AwaitingAccept -> Staging -> Countdown -> Live -> Resolving -> Closed, with
    /// Canceled reachable from AwaitingAccept and Staging, and from Countdown and Live by an admin cancel.
    ///
    /// <para/>
    /// <b>Threading.</b> Every public method, and <see cref="Tick"/>, runs on the WORLD thread: the tick from
    /// WorldManager.UpdateGameWorld (H1), and the API from commands and popup replies, which the world loop
    /// dispatches through NetworkManager.InboundMessageQueue. No state here is locked, because nothing else touches
    /// it. Exactly two things cross threads, and both only through a ConcurrentQueue the tick drains:
    ///   - intents (deaths from landblock threads, logout forfeits), pushed by the player hooks;
    ///   - database results (the boot ratings read, each match save), pushed by the sink's callbacks, which run on
    ///     a thread-pool thread and do nothing but enqueue a plain record.
    /// Every change to a player goes through <see cref="IPvpPlayerGateway"/>, whose live implementation queues it on
    /// the player's own action queue. Timers compare against the injected clock; there are no Tasks and no Sleep.
    ///
    /// <para/>
    /// An instance class so tests can build a fresh one; production owns exactly one, behind
    /// <see cref="PvpMatchManager"/>.
    /// </summary>
    public sealed class PvpMatchCoordinator
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpMatchCoordinator));

        /// <summary>
        /// After a space allocation fails, that room forms no new match for this long. A refusal that repeats (a
        /// realm not registered, a bad map) would otherwise re-propose, re-prompt and re-fail every tick.
        /// </summary>
        public const double AllocationBackoffSeconds = 15;

        private readonly IPvpPlayerGateway players;
        private readonly IPvpMatchSpaces spaces;
        private readonly IPvpResultSink sink;
        private readonly Func<DateTime> clock;
        private readonly Func<PvpArenaDials> dialSource;
        private readonly ConcurrentQueue<PvpIntent> intents;

        /// <summary>
        /// Intents taken off <see cref="intents"/> mid-tick by <see cref="DrainCrystalIntents"/> that were not that match's crystal
        /// reports. World thread only. <see cref="DrainIntents"/> runs them first, in the order they arrived, so nothing is lost or
        /// reordered among themselves.
        /// </summary>
        private readonly List<PvpIntent> deferredIntents = new List<PvpIntent>();

        private readonly IPvpArenaCrierAnnouncer arenaAnnouncer;
        private readonly PvpArenaCrier arenaCrier = new PvpArenaCrier();
        private readonly ConcurrentQueue<DbResult> dbResults = new ConcurrentQueue<DbResult>();

        private readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MatchRuntime> matches = new List<MatchRuntime>();
        private readonly Dictionary<uint, DateTime> lockouts = new Dictionary<uint, DateTime>();

        private int mapRotation;
        private bool loggedWaitingForRatings;

        // ---- PvP Template Facets (Docs/Pvp/TEMPLATES.md): every match is templated ----

        /// <summary>
        /// How often (at most) the equipped-item backstop runs for each live participant (TEMPLATES.md "Backstop":
        /// "on every match tick after"). The coordinator ticks every 250 ms; once a second keeps the queued work small.
        /// </summary>
        public const double TemplateBackstopIntervalSeconds = 1;

        /// <summary>
        /// A template apply refused only because the player was busy (mid-animation with gear to strip) is retried
        /// every <see cref="EntryBusyRetryIntervalSeconds"/> until <see cref="EntryBusyRetryWindowSeconds"/> after the
        /// first busy report, then counts as a player-caused entry failure. The retry is the coordinator's, on the world
        /// thread, so it is never issued once the match has left Staging (a player-side delayed retry could run after
        /// a cancel's exit and template a player into a dead match).
        /// </summary>
        public const double EntryBusyRetryWindowSeconds = 3;

        public const double EntryBusyRetryIntervalSeconds = 0.5;

        /// <summary>
        /// The kit warm (PvpTemplateSnapshotService.WarmKitWeeniesAsync), called once per distinct template when a match
        /// forms. A seam so a test can count the calls; production never reassigns it.
        /// </summary>
        internal static Action<PvpTemplateDefinition> WarmKit = definition => PvpTemplateSnapshotService.WarmKitWeeniesAsync(definition);

        /// <summary>The parsed pvp_template rows, or null until the first read lands. Replaced wholesale on each reload.</summary>
        private PvpTemplateCatalog templateCatalog;

        /// <summary>The last template read failed (rows null). With no earlier catalog, template joins are refused as unavailable.</summary>
        private bool templateReadFailed;

        /// <summary>(character, ladder) -> the template key of their latest resolved match on that ladder, for /top.</summary>
        private readonly Dictionary<(uint CharacterId, string Ladder), string> latestTemplateByLadder = new Dictionary<(uint, string), string>();

        /// <summary>Participant template stamps waiting for their match's save to come back with a pvp_match id.</summary>
        private readonly Dictionary<Guid, List<PvpMatchParticipantTemplateRecord>> pendingStamps = new Dictionary<Guid, List<PvpMatchParticipantTemplateRecord>>();
        // ---- battlegrounds (Docs/Pvp/BATTLEGROUNDS.md) ----

        private readonly Func<BattlegroundDials> bgDialSource;

        /// <summary>Set when a battleground match forms; the next Arena Crier tick reads and clears it (a fill-window new-episode signal).</summary>
        private bool bgMatchFormedSinceCrierTick;

        /// <summary>An injected mode picker (the constructor's bgModePicker), or null for the built-in rotation below.</summary>
        private readonly Func<IReadOnlyList<PvpModeDefinition>, PvpModeDefinition> bgModePickerOverride;

        /// <summary>
        /// The built-in battleground mode choice (Docs/Pvp/ATTACK-DEFEND.md "Mode picking"): a round-robin over the enabled modes whose
        /// template every seat of the forming match is offered, falling back to the available mode offered to the most seats.
        /// </summary>
        private readonly BattlegroundModePicker bgModeRotation = new BattlegroundModePicker();

        /// <summary>The battleground halves of the seams: the gateway and spaces themselves, when they implement them. Null otherwise, and then the bg room never opens.</summary>
        private readonly IBattlegroundPlayerGateway bgPlayers;
        private readonly IBattlegroundMatchSpaces bgSpaces;

        /// <summary>
        /// The Attack/Defend half of the space seam (Docs/Pvp/ATTACK-DEFEND.md "Placement"): the spaces themselves, when they implement
        /// it. Null otherwise, and then the built-in picker never chooses Attack/Defend.
        /// </summary>
        private readonly IObjectiveMatchSpaces objectiveSpaces;

        /// <summary>
        /// The Attack/Defend side coin flip (Docs/Pvp/ATTACK-DEFEND.md "Sides"): true swaps the proposal's two teams, so its SECOND team
        /// attacks. Team 0 is always the attacker after the swap. ThreadSafeRandom.Next is inclusive at both ends. Tests swap it per
        /// instance, like <see cref="LayoutResolver"/>.
        /// </summary>
        internal Func<bool> AttackDefendSwapSides { get; set; } = () => ACE.Common.ThreadSafeRandom.Next(0, 1) == 1;

        /// <summary>
        /// The Attack/Defend content guard: true when the crystal weenie of this wcid resolves in the world cache. The built-in picker
        /// never chooses Attack/Defend for a map whose crystal does not resolve, so a missing weenie never forms a match that would only
        /// cancel at placement. Tests swap it per instance.
        /// </summary>
        internal Func<uint, bool> CrystalWeenieResolves
        {
            get => crystalWeenieResolves;
            set
            {
                crystalWeenieResolves = value;
                crystalWeenieCache.Clear();
            }
        }

        private Func<uint, bool> crystalWeenieResolves = wcid =>
        {
            var world = ACE.Database.DatabaseManager.World;

            return world != null && CrystalWeenieResolvesWithRefresh(wcid, id => world.GetCachedWeenie(id) != null, id => world.GetWeenie(id) != null);
        };

        /// <summary>
        /// The default <see cref="CrystalWeenieResolves"/> rule. The world cache remembers a miss as a cached null and GetCachedWeenie never
        /// reads again, so a crystal weenie applied after its first lookup would stay "missing" until a restart. A cached miss therefore
        /// falls through to a real read: WorldDatabase.GetWeenie(uint) opens its own context and runs the virtual
        /// WorldDatabaseWithEntityCache.GetWeenie(context, id), which queries the database and overwrites the cache entry (the weenie, or
        /// null again). The coordinator's TTL bounds that to one read per wcid per <see cref="CrystalWeenieCacheTtl"/>.
        /// </summary>
        internal static bool CrystalWeenieResolvesWithRefresh(uint wcid, Func<uint, bool> cached, Func<uint, bool> read) =>
            cached(wcid) || read(wcid);

        /// <summary>Map rotation for objective modes, kept apart so a battleground never shifts the arena map rotation.</summary>
        private int bgMapRotation;

        /// <summary>
        /// The team fellowship seam (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): the player gateway itself, when it
        /// implements it. Null otherwise, and then no match ever builds team fellowships.
        /// </summary>
        private readonly IPvpTeamFellowshipGateway teamFellowships;

        /// <summary>How often (at most) the team fellowship sweep runs: a backstop for a Dispose that never happened.</summary>
        public const double TeamFellowshipSweepIntervalSeconds = 30;

        private DateTime? lastTeamFellowshipSweepUtc;

        /// <summary>
        /// Finds a battleground layout by map key at match formation. <see cref="BattlegroundMapCatalog.Find"/>; tests swap
        /// it (per instance, so nothing leaks between test classes) to drive a layout the catalog does not ship, such as
        /// one with a zone-marker wcid while the catalog still carries the placeholder 0.
        /// </summary>
        internal Func<string, BattlegroundLayout> LayoutResolver { get; set; } = BattlegroundMapCatalog.Find;

        public PvpRatingStore Ratings { get; } = new PvpRatingStore();

        /// <summary>The template catalog as last read, or null while the first read is in flight (or it failed). Diagnostics, commands and tests.</summary>
        public PvpTemplateCatalog Templates => templateCatalog;

        /// <summary>How many participant template stamp writes were queued. Diagnostics and tests.</summary>
        public int TemplateStampsQueued { get; private set; }

        /// <summary>How many save results the tick has handled. Diagnostics and tests.</summary>
        public int SaveResultsHandled { get; private set; }

        /// <summary>How many saves were queued. Diagnostics and tests.</summary>
        public int SavesQueued { get; private set; }

        /// <summary>The last save result the tick handled, or null.</summary>
        public PvpMatchSaveResult? LastSaveResult { get; private set; }

        /// <summary>Database results waiting for the next tick. Diagnostics and tests.</summary>
        public int PendingDbResults => dbResults.Count;

        /// <summary>
        /// Test-only seam for the PvP Discord feed post in <see cref="Resolve"/>: null (the default) calls the
        /// real <see cref="ACE.Server.Managers.DiscordRelayManager.QueuePvp"/>. Static, not instance, because
        /// DiscordRelayManager itself is static with no injectable failure point - a test proving the
        /// try/catch around that call actually protects the match save needs a way to make the call throw
        /// deterministically. Tests must restore it to null in a finally.
        /// </summary>
        internal static Action<string> QueuePvpForTest;

        /// <param name="bgDialSource">The pvp_bg_* settings; defaults to <see cref="BattlegroundTunables.DialSource"/>.</param>
        /// <param name="bgModePicker">
        /// Overrides the battleground mode choice when a bg match forms, from the modes enabled at that moment (never empty). Null (the
        /// default) uses the built-in <see cref="BattlegroundModePicker"/> rotation with the template filter. A result outside the list
        /// falls back to the first.
        /// </param>
        public PvpMatchCoordinator(IPvpPlayerGateway players, IPvpMatchSpaces spaces, IPvpResultSink sink, Func<DateTime> clock, Func<PvpArenaDials> dialSource, ConcurrentQueue<PvpIntent> intents, IPvpArenaCrierAnnouncer arenaAnnouncer = null,
            Func<BattlegroundDials> bgDialSource = null, Func<IReadOnlyList<PvpModeDefinition>, PvpModeDefinition> bgModePicker = null)
        {
            this.players = players ?? throw new ArgumentNullException(nameof(players));
            this.spaces = spaces ?? throw new ArgumentNullException(nameof(spaces));
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.dialSource = dialSource ?? throw new ArgumentNullException(nameof(dialSource));
            this.intents = intents ?? throw new ArgumentNullException(nameof(intents));
            this.arenaAnnouncer = arenaAnnouncer ?? new LivePvpArenaCrierAnnouncer();

            this.bgDialSource = bgDialSource ?? (() => BattlegroundTunables.DialSource());
            bgModePickerOverride = bgModePicker;
            bgPlayers = players as IBattlegroundPlayerGateway;
            bgSpaces = spaces as IBattlegroundMatchSpaces;
            objectiveSpaces = spaces as IObjectiveMatchSpaces;
            teamFellowships = players as IPvpTeamFellowshipGateway;

            foreach (var key in new[] { ArenaMapCatalog.OneVOneKey, ArenaMapCatalog.TwoVTwoKey, ArenaMapCatalog.FfaKey })
                rooms[key] = new Room(key);

            // The one battleground room (Docs/Pvp/BATTLEGROUNDS.md "Matchmaking"): the mode is chosen when a match forms.
            rooms[BattlegroundModes.RoomKey] = new Room(BattlegroundModes.RoomKey);
        }

        /// <summary>Both battleground seams are present. Without them the bg room refuses joins as a disabled mode.</summary>
        public bool BattlegroundsAvailable => bgPlayers != null && bgSpaces != null;

        // =====================================================================================================
        // boot
        // =====================================================================================================

        /// <summary>
        /// Queues the boot read of every rating row. The callback only enqueues; the tick publishes the result
        /// (SpeedBoardManager precedent, but loaded on the shard database queue rather than inline at boot).
        /// Until it lands the store is Loading and matchmaking waits; joins are still accepted.
        /// </summary>
        public void BeginLoadRatings()
        {
            log.Info("[PVP] queuing the boot read of every arena rating");

            sink.LoadRatings(list => dbResults.Enqueue(DbResult.RatingsLoaded(list)));
        }

        /// <summary>
        /// Queues a read of every pvp_template row (PvP Template Facets). Runs at boot and again after every admin change
        /// (/pvptemplate enable, disable, modes, snapshot), and from /pvptemplate list. The callback only enqueues; the
        /// tick parses the rows and swaps the catalog in. Until the first read lands, template joins are refused.
        /// </summary>
        public void BeginLoadTemplates()
        {
            sink.LoadTemplates((rows, status) => dbResults.Enqueue(DbResult.TemplatesLoaded(rows, status)));
        }

        /// <summary>Queues the boot read behind /top's template column: each character's latest stamped template per ladder.</summary>
        public void BeginLoadLatestTemplates()
        {
            sink.LoadLatestParticipantTemplates((rows, status) => dbResults.Enqueue(DbResult.LatestTemplatesLoaded(rows, status)));
        }

        // =====================================================================================================
        // tick
        // =====================================================================================================

        /// <summary>One pass: database results, intents, every match, then matchmaking. World thread only.</summary>
        public void Tick()
        {
            var now = clock();
            var dials = Dials();

            DrainDbResults();
            DrainIntents(now);

            foreach (var m in matches.ToList())
            {
                try
                {
                    Advance(m, now, dials);
                }
                catch (Exception ex)
                {
                    // One faulted match must not stall the rest. It stays in its state and is retried next tick.
                    log.Error($"[PVP] match {m.Id} ({m.ModeKey}) threw while advancing from {m.Match.State}", ex);
                }
            }

            matches.RemoveAll(m => m.Match.State == PvpMatchState.Closed || m.Match.State == PvpMatchState.Canceled);

            RunTeamFellowshipSweep(now);

            RunTemplateBackstops(now);

            foreach (var id in lockouts.Where(kvp => kvp.Value <= now).Select(kvp => kvp.Key).ToList())
                lockouts.Remove(id);

            RunMatchmaking(now, dials);
            RunArenaCrier(now, dials);
        }

        /// <summary>
        /// Arena Crier (Docs/Pvp/DESIGN.md "Arena Crier"): snapshots each room's queued count and "still needed"
        /// (the SAME per-mode formula the matchmaker and the FFA lobby line use, never a literal here), hands them
        /// to the pure <see cref="PvpArenaCrier"/>, and sends back whatever lines it decides to announce.
        /// </summary>
        private void RunArenaCrier(DateTime now, PvpArenaDials dials)
        {
            // Consumed every tick (even while the Crier is off) so a stale flag never leaks into a later tick.
            var matchFormed = bgMatchFormedSinceCrierTick;
            bgMatchFormedSinceCrierTick = false;

            if (!dials.CrierEnabled)
                return;

            var snapshots = new List<CrierQueueSnapshot>(rooms.Count);

            foreach (var room in rooms.Values)
            {
                var enabled = ModeEnabled(room.ModeKey, dials);
                var count = room.PlayerCount;
                int needed;
                int? fillSecondsLeft = null;
                DateTime? fillCloseUtc = null;
                var fillRoom = 0;

                if (IsBgRoom(room))
                {
                    // The battleground room (Docs/Pvp/BATTLEGROUNDS.md "Commands and other carriers"): the match forms
                    // once pvp_bg_min_players are queued (then the fill window runs), so that is what is still needed.
                    var bg = BgDials();

                    needed = Math.Max(0, bg.MinPlayers - count);

                    // The fill window (Count in [MinPlayers, MaxPlayers)), from the SAME helper the matchmaker closes it
                    // with, fed the same queue-time then EntrantId order.
                    // A room the matchmaker is holding back (see BgMatchmakingHeld) is not about to start, so it gets no
                    // fill line either.
                    if (count >= bg.MinPlayers && count < bg.MaxPlayers && !BgMatchmakingHeld(room, now, dials, bg))
                    {
                        var close = BattlegroundMatchmaker.FillWindowCloseUtc(
                            room.Units.OrderBy(u => u.QueuedAtUtc).ThenBy(u => u.EntrantId).Select(u => (u.Ids.Count, u.QueuedAtUtc)), bg);

                        if (close.HasValue)
                        {
                            fillSecondsLeft = (int)Math.Ceiling((close.Value - now).TotalSeconds);
                            fillCloseUtc = close;
                            fillRoom = bg.MaxPlayers - count;
                        }
                    }
                }
                else if (room.ModeKey == ArenaMapCatalog.FfaKey)
                {
                    var waited = count > 0 ? now - room.Units.Min(u => u.QueuedAtUtc) : TimeSpan.Zero;
                    needed = Math.Max(0, ArenaMapCatalog.FfaDecayedTargetSize(waited, dials) - count);
                }
                else
                {
                    var mode = ModeFor(room.ModeKey, dials);
                    needed = Math.Max(0, mode.TeamCountRange.Min * mode.TeamSize - count);
                }

                snapshots.Add(new CrierQueueSnapshot(room.ModeKey, enabled, count, needed, fillSecondsLeft, fillRoom, fillCloseUtc, matchFormed));
            }

            var lines = arenaCrier.Tick(now, dials, snapshots);

            if (lines.Count > 0)
                arenaAnnouncer.Announce(lines, dials.CrierSenderName);
        }

        /// <summary>The live dials, with the coordinator's own clamps: accept at or under 30 s, FFA seats at most the map's.</summary>
        private PvpArenaDials Dials()
        {
            var d = dialSource();
            var ffaMax = Math.Clamp(d.FfaMaxPlayers, 2, ArenaMapCatalog.FfaSeats);
            var ffaMin = Math.Clamp(d.FfaMinPlayers, 2, ffaMax);

            return d with
            {
                AcceptSeconds = PvpTunables.ClampAcceptSeconds(d.AcceptSeconds),
                FfaMaxPlayers = ffaMax,
                FfaMinPlayers = ffaMin,
                FfaTargetPlayers = Math.Clamp(d.FfaTargetPlayers, ffaMin, ffaMax),
                MaxConcurrentMatches = Math.Max(0, d.MaxConcurrentMatches),
            };
        }

        private static PvpModeDefinition ModeFor(string modeKey, PvpArenaDials dials)
        {
            switch (modeKey)
            {
                case ArenaMapCatalog.OneVOneKey: return PvpModes.OneVOne(dials);
                case ArenaMapCatalog.TwoVTwoKey: return PvpModes.TwoVTwo(dials);
                case ArenaMapCatalog.FfaKey: return PvpModes.Ffa(dials);
                default: return null;
            }
        }

        /// <summary>
        /// Whether a ROOM takes joins and forms matches. The arena rooms read their own switch; the bg room needs both
        /// battleground seams, pvp_bg_enabled and at least one enabled battleground mode that can be played (PlayableBgModes). pvp_arena_enabled sits above
        /// all of them and is checked by each caller (RunMatchmaking, admission).
        /// </summary>
        private bool ModeEnabled(string modeKey, PvpArenaDials dials)
        {
            switch (modeKey)
            {
                case ArenaMapCatalog.OneVOneKey: return dials.OneVOneEnabled;
                case ArenaMapCatalog.TwoVTwoKey: return dials.TwoVTwoEnabled;
                case ArenaMapCatalog.FfaKey: return dials.FfaEnabled;
                case BattlegroundModes.RoomKey: return BattlegroundsAvailable && PlayableBgModes(BgDials()).Count > 0;
                default: return false;
            }
        }

        private static bool IsBgRoom(Room room) => string.Equals(room.ModeKey, BattlegroundModes.RoomKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>The pvp_bg_* dials, never null and never throwing, with the coordinator's own clamp on the cap.</summary>
        private BattlegroundDials BgDials()
        {
            BattlegroundDials d;

            try
            {
                d = bgDialSource() ?? BattlegroundTunables.Defaults;
            }
            catch (Exception ex)
            {
                log.Error("[PVP] reading the battleground dials threw; using the defaults", ex);
                d = BattlegroundTunables.Defaults;
            }

            // The same clamps the live read applies, so any dial source (an injected one included) is safe to run on.
            return BattlegroundTunables.Clamp(d) with { MaxConcurrentMatches = Math.Max(0, d.MaxConcurrentMatches) };
        }

        /// <summary>Every battleground mode enabled under <paramref name="bg"/>, each built from that one snapshot.</summary>
        private static IReadOnlyList<PvpModeDefinition> EnabledBgModes(BattlegroundDials bg) =>
            BattlegroundModes.All(bg).Where(mode => BattlegroundModes.IsEnabled(mode.ModeKey, bg)).ToList();

        /// <summary>
        /// Every battleground mode enabled under <paramref name="bg"/> that can actually be played (<see cref="BgModeAvailable"/>). The one
        /// list the bg room is gated on, matched on, offered templates for and picked from: when it is empty no battleground forms and the
        /// room reports disabled, rather than forming a match that could only cancel at placement.
        /// </summary>
        private IReadOnlyList<PvpModeDefinition> PlayableBgModes(BattlegroundDials bg) =>
            EnabledBgModes(bg).Where(BgModeAvailable).ToList();

        // ---------------- PvP Template Facets: which joins and matches are templated ----------------

        /// <summary>The modes a join to <paramref name="roomKey"/> can end up in: the arena room's own mode, or the bg room's enabled modes.</summary>
        private IReadOnlyList<PvpModeDefinition> ModesOfRoom(string roomKey)
        {
            if (string.Equals(roomKey, BattlegroundModes.RoomKey, StringComparison.OrdinalIgnoreCase))
                return PlayableBgModes(BgDials());

            var mode = ModeFor(roomKey, Dials());

            return mode != null ? new[] { mode } : Array.Empty<PvpModeDefinition>();
        }

        /// <summary>
        /// Whether a join to <paramref name="roomKey"/> needs a template: when any mode it can form is templated, which
        /// is every mode unless it opts out (PvpModeDefinition.Templated). A room with no known mode answers true, so an
        /// unknown case fails closed (a template is required).
        /// </summary>
        private bool RoomTemplated(string roomKey)
        {
            var modes = ModesOfRoom(roomKey);

            return modes.Count == 0 || modes.Any(m => m.Templated);
        }

        /// <summary>Whether a formed match of <paramref name="modeKey"/> is templated; an unknown key answers true (fail closed).</summary>
        private bool ModeKeyTemplated(string modeKey) =>
            (ModeFor(modeKey, Dials()) ?? BattlegroundModes.All(BgDials()).FirstOrDefault(m => m.ModeKey == modeKey))?.Templated ?? true;

        /// <summary>The template <paramref name="key"/> offered for any templated mode a join to <paramref name="roomKey"/> can form.</summary>
        private bool TryGetOfferedForRoom(string key, string roomKey, out PvpTemplateCatalogEntry entry)
        {
            entry = null;

            if (templateCatalog == null)
                return false;

            foreach (var mode in ModesOfRoom(roomKey).Where(m => m.Templated))
            {
                if (templateCatalog.TryGetOffered(key, mode.ModeKey, out entry))
                    return true;
            }

            entry = null;
            return false;
        }

        /// <summary>
        /// The mode a forming bg match plays, from <paramref name="enabled"/> (the playable modes, <see cref="PlayableBgModes"/>): the injected
        /// picker's choice when one was given, else the built-in rotation over the modes offered to every seat of <paramref name="proposal"/> (Docs/Pvp/ATTACK-DEFEND.md
        /// "Mode picking"); when none is, the available mode offered to the most seats. Anything outside the list falls back to the first.
        /// A seat whose template is not offered for the picked mode is still withdrawn without fault, exactly as before (the accept-time
        /// template re-check, or FreezeTemplatesForDispatch).
        /// </summary>
        private PvpModeDefinition PickBgMode(IReadOnlyList<PvpModeDefinition> enabled, Room room, MatchProposal proposal)
        {
            PvpModeDefinition picked = null;

            try
            {
                if (bgModePickerOverride != null)
                    picked = bgModePickerOverride(enabled);
                else
                {
                    var seats = proposal.Teams.SelectMany(t => t.Members)
                        .Select(p => (Id: p.CharacterId, Template: room.Units.FirstOrDefault(u => u.Ids.Contains(p.CharacterId))?.TemplateOf(p.CharacterId)))
                        .ToList();

                    // `enabled` is already PlayableBgModes: every mode in it can be played, so only the seats decide.
                    picked = bgModeRotation.Pick(enabled,
                        mode => seats.All(s => SeatOffered(mode, s.Template)),
                        mode => seats.Count(s => SeatOffered(mode, s.Template)));

                    // No mode was offered to every seat: the pick is the one offered to the most, and the rest are withdrawn without fault
                    // by the template re-check (at accept, or FreezeTemplatesForDispatch). Logged here, where the reason is known.
                    if (picked != null)
                    {
                        var withdrawn = seats.Where(s => !SeatOffered(picked, s.Template)).ToList();

                        if (withdrawn.Count > 0)
                            log.Warn($"[PVP] battleground mode {picked.ModeKey} picked for the most seats; {withdrawn.Count} seat(s) will be withdrawn by the template re-check, their template not offered for it: {string.Join(", ", withdrawn.Select(s => $"0x{s.Id:X8} ({s.Template ?? "(none)"})"))}");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("[PVP] the battleground mode picker threw; using the first enabled mode", ex);
            }

            return picked != null && enabled.Contains(picked) ? picked : enabled[0];
        }

        /// <summary>
        /// Whether <paramref name="mode"/> can be played at all, whatever the seats: Attack/Defend needs the crystal seam and a crystal weenie
        /// that resolves for every map in its pool (<see cref="CrystalWeenieResolves"/>). Every other mode is always available.
        /// </summary>
        private bool BgModeAvailable(PvpModeDefinition mode)
        {
            if (mode.ModeKey != BattlegroundModes.AttackDefendModeKey)
                return true;

            if (objectiveSpaces == null || mode.MapPool.Count == 0)
                return false;

            foreach (var map in mode.MapPool)
            {
                var wcid = LayoutResolver(map.MapKey)?.CrystalWcid ?? 0;

                if (wcid == 0)
                {
                    WarnNoCrystalWcid(map.MapKey);
                    return false;
                }

                if (!CrystalResolvesCached(wcid, map.MapKey))
                    return false;
            }

            return true;
        }

        /// <summary>When the "no crystal wcid" warning was last logged, per map; it repeats at most once per <see cref="CrystalWeenieCacheTtl"/>.</summary>
        private readonly Dictionary<string, DateTime> noCrystalWcidWarnedAtUtc = new Dictionary<string, DateTime>();

        /// <summary>Logs that a map in the Attack/Defend pool has no crystal wcid, at most once per map per <see cref="CrystalWeenieCacheTtl"/>.</summary>
        private void WarnNoCrystalWcid(string mapKey)
        {
            var now = clock();
            var key = mapKey ?? "";

            if (noCrystalWcidWarnedAtUtc.TryGetValue(key, out var at) && now >= at && now - at < CrystalWeenieCacheTtl)
                return;

            noCrystalWcidWarnedAtUtc[key] = now;
            log.Warn($"[PVP] Attack/Defend is unavailable: map {mapKey} has no crystal wcid (or its layout does not resolve)");
        }

        /// <summary>How long one crystal-weenie lookup is trusted (the lookup can read the world database on a miss).</summary>
        internal static readonly TimeSpan CrystalWeenieCacheTtl = TimeSpan.FromSeconds(30);

        private readonly Dictionary<uint, (bool Resolves, DateTime CheckedAtUtc)> crystalWeenieCache = new Dictionary<uint, (bool Resolves, DateTime CheckedAtUtc)>();

        /// <summary>
        /// <see cref="CrystalWeenieResolves"/>, remembered per wcid for <see cref="CrystalWeenieCacheTtl"/> on the coordinator's own clock, so
        /// the room gate, the matchmaker and the template lookups (each called often) never hit the database between refreshes. A miss is
        /// logged once per refresh. World thread only.
        /// </summary>
        private bool CrystalResolvesCached(uint wcid, string mapKey)
        {
            var now = clock();

            if (crystalWeenieCache.TryGetValue(wcid, out var cached) && now >= cached.CheckedAtUtc && now - cached.CheckedAtUtc < CrystalWeenieCacheTtl)
                return cached.Resolves;

            bool resolves;

            try
            {
                resolves = CrystalWeenieResolves?.Invoke(wcid) ?? false;
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] resolving the crystal weenie {wcid} for map {mapKey} threw; Attack/Defend is unavailable", ex);
                resolves = false;
            }

            if (!resolves)
                log.Warn($"[PVP] Attack/Defend is unavailable: the crystal weenie {wcid} for map {mapKey} does not resolve (rechecked every {CrystalWeenieCacheTtl.TotalSeconds:0} s)");

            crystalWeenieCache[wcid] = (resolves, now);
            return resolves;
        }

        /// <summary>Whether a seat that queued on <paramref name="template"/> can play <paramref name="mode"/>: always for an untemplated mode, else only when the template is offered for it.</summary>
        private bool SeatOffered(PvpModeDefinition mode, string template)
        {
            if (!mode.Templated)
                return true;

            return templateCatalog != null && TemplatesEnabled() && template != null && templateCatalog.TryGetOffered(template, mode.ModeKey, out _);
        }

        private static int TimeLimitFor(string modeKey, PvpArenaDials dials)
        {
            switch (modeKey)
            {
                case ArenaMapCatalog.OneVOneKey: return dials.TimeLimitSeconds1v1;
                case ArenaMapCatalog.TwoVTwoKey: return dials.TimeLimitSeconds2v2;
                default: return dials.TimeLimitSecondsFfa;
            }
        }

        private void DrainDbResults()
        {
            while (dbResults.TryDequeue(out var r))
            {
                switch (r.Kind)
                {
                    case DbResultKind.RatingsLoaded:
                        Ratings.Publish(r.Ratings);

                        if (Ratings.State == PvpRatingStoreState.Unavailable)
                            log.Error("[PVP] RATINGS UNAVAILABLE: the boot read of character_pvp_rating FAILED. Every arena match will run UNRATED and no rating row will be written until the server restarts with a working read.");
                        else
                            log.Info($"[PVP] arena ratings loaded: {Ratings.Count} ladder row(s)");
                        break;

                    case DbResultKind.SaveCompleted:
                        SaveResultsHandled++;
                        LastSaveResult = r.SaveResult;

                        // NEVER resubmitted, on Failed or Ambiguous: the DAO's caller contract. The in-memory ladder
                        // already holds the result and is rewritten in full by each player's next match.
                        if (r.SaveResult == PvpMatchSaveResult.Saved)
                            log.Info($"[PVP] match {r.MatchId} saved as pvp_match {r.DbMatchId}");
                        else
                            log.Error($"[PVP] match {r.MatchId} save returned {r.SaveResult}; NOT resubmitted (a resubmit after Ambiguous could record it twice). The in-memory ladder keeps the result.");

                        // The participant template stamps target the saved rows, so they go only after a Saved result
                        // that carries the pvp_match id. On Failed or Ambiguous they are dropped with the match.
                        if (pendingStamps.Remove(r.MatchId, out var stamps))
                        {
                            if (r.SaveResult == PvpMatchSaveResult.Saved && r.DbMatchId != 0 && stamps.Count > 0)
                            {
                                var matchId = r.MatchId;
                                TemplateStampsQueued++;
                                sink.SaveParticipantTemplates(r.DbMatchId, stamps, status => dbResults.Enqueue(DbResult.StampsSaved(matchId, status)));
                            }
                            else if (stamps.Count > 0)
                                log.Warn($"[PVP] match {r.MatchId}: {stamps.Count} participant template stamp(s) dropped with the {r.SaveResult} save");
                        }
                        break;

                    case DbResultKind.StampsSaved:
                        if (r.TemplateStatus == PvpTemplateStoreStatus.Ok)
                            log.Info($"[PVP] match {r.MatchId}: participant template stamps saved");
                        else
                            log.Warn($"[PVP] match {r.MatchId}: participant template stamps returned {r.TemplateStatus}; the match row is saved without them");
                        break;

                    case DbResultKind.TemplatesLoaded:
                        if (r.TemplateRows == null)
                        {
                            templateReadFailed = true;
                            log.Error($"[PVPTEMPLATE] the template read FAILED ({r.TemplateStatus}); {(templateCatalog == null ? "no catalog is loaded, so every arena and battleground join is refused" : "the previous catalog stays in use")} until a read succeeds");
                            break;
                        }

                        templateReadFailed = false;
                        templateCatalog = PvpTemplateCatalog.Build(r.TemplateRows, line => log.Warn($"[PVPTEMPLATE] {line}"));
                        log.Info($"[PVPTEMPLATE] template catalog loaded ({r.TemplateStatus}): {templateCatalog.Count} row(s); offered: {string.Join("; ", PvpTemplateCatalog.ModeKeys.Select(k => $"{k} [{string.Join(",", templateCatalog.Offered(k).Select(e => e.Key))}]"))}");
                        break;

                    case DbResultKind.LatestTemplatesLoaded:
                        if (r.LatestTemplates == null)
                        {
                            log.Warn($"[PVPTEMPLATE] the /top template read returned {r.TemplateStatus}; /top shows templates only for matches resolved since boot");
                            break;
                        }

                        // A match resolved since boot is newer than anything the read saw: never overwritten by it.
                        foreach (var row in r.LatestTemplates)
                        {
                            if (row?.TemplateKey != null && row.Ladder != null)
                                latestTemplateByLadder.TryAdd((row.CharacterId, row.Ladder), row.TemplateKey);
                        }

                        log.Info($"[PVPTEMPLATE] /top templates loaded: {r.LatestTemplates.Count} (character, ladder) row(s)");
                        break;
                }
            }
        }

        private void DrainIntents(DateTime now)
        {
            while (TryNextIntent(out var intent))
            {
                var m = matches.FirstOrDefault(x => x.Id == intent.MatchId);

                if (intent.Kind == PvpIntentKind.BackstopFired)
                {
                    NoteTemplateBackstop(m, intent, now);
                    continue;
                }

                if (intent.Kind == PvpIntentKind.EntryFailed)
                {
                    HandleEntryFailed(m, intent, now);
                    continue;
                }

                // Attack/Defend: a crystal's destruction carries no character (CharacterId 0), so it is handled BEFORE the seat lookup
                // below, which would otherwise drop it as "not a placed participant".
                if (intent.Kind == PvpIntentKind.ObjectiveDestroyed)
                {
                    HandleObjectiveDestroyed(m, intent);
                    continue;
                }

                if (m == null || !(m.Match.State == PvpMatchState.Staging || m.Match.State == PvpMatchState.Countdown || m.Match.State == PvpMatchState.Live))
                {
                    log.Info($"[PVP] {intent.Kind} intent for 0x{intent.CharacterId:X8} in match {intent.MatchId} ignored: the match is {(m == null ? "gone" : m.Match.State.ToString())}");
                    continue;
                }

                if (!m.Seats.TryGetValue(intent.CharacterId, out var seat) || !seat.Dispatched)
                {
                    log.Warn($"[PVP] {intent.Kind} intent for 0x{intent.CharacterId:X8} ignored: not a placed participant of match {m.Id}");
                    continue;
                }

                // Spawn protection ended early on the player's side (the binding there is already swapped): drop the seat's window so
                // no later republish brings it back. Nothing to announce; the player was told.
                if (intent.Kind == PvpIntentKind.SpawnProtectionEnded)
                {
                    seat.ProtectedUntilUtc = null;
                    seat.ProtectedInRoom = false;
                    continue;
                }

                // Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 2): a Live objective-mode death whose policy says
                // Respawn sends the seat to the pen instead of out of the match. Every other death, and every forfeit,
                // takes MarkOut exactly as in the arena.
                if (intent.Kind == PvpIntentKind.Death && m.Mode.IsObjective && m.Match.State == PvpMatchState.Live && !seat.Participant.ExitReason.HasValue
                    && m.Mode.Respawn?.OnDeath(m.Match, seat.Participant) is DeathDisposition.RespawnDisposition respawn)
                {
                    HandleBattlegroundDeath(m, seat, respawn, now, intent.KillerId, intent.DeathPosition);
                    continue;
                }

                MarkOut(m, seat, intent.ExitReason, now, intent.KillerId);
            }
        }

        /// <summary>The next intent to drain: any deferred mid-tick first, then the cross-thread queue.</summary>
        private bool TryNextIntent(out PvpIntent intent)
        {
            if (deferredIntents.Count > 0)
            {
                intent = deferredIntents[0];
                deferredIntents.RemoveAt(0);
                return true;
            }

            return intents.TryDequeue(out intent);
        }

        /// <summary>
        /// Review F6, the clock edge: a crystal that died on the landblock thread after this tick's <see cref="DrainIntents"/> must still count
        /// before the win condition reads the clock, or the defenders could win on a timeout the attackers had already beaten. Takes
        /// everything queued so far, handles this match's crystal reports now, and defers every other intent, in arrival order, to the next
        /// <see cref="DrainIntents"/>.
        /// </summary>
        private void DrainCrystalIntents(MatchRuntime m)
        {
            while (intents.TryDequeue(out var queued))
                deferredIntents.Add(queued);

            for (var i = 0; i < deferredIntents.Count;)
            {
                var intent = deferredIntents[i];

                if (intent.Kind == PvpIntentKind.ObjectiveDestroyed && intent.MatchId == m.Id)
                {
                    deferredIntents.RemoveAt(i);
                    HandleObjectiveDestroyed(m, intent);
                }
                else
                    i++;
            }
        }

        /// <summary>
        /// A crystal fell (Docs/Pvp/ATTACK-DEFEND.md "Death"). Honoured only while the match is Live, and only once per crystal index:
        /// the tick handler counts it in ScoreBoard[0] and tells everyone, and the win condition sees the count on this same tick. A
        /// late report (the match already resolving or gone), a duplicate, an out-of-range index or a match that is not Attack/Defend is
        /// logged and ignored.
        /// </summary>
        private void HandleObjectiveDestroyed(MatchRuntime m, PvpIntent intent)
        {
            var index = intent.Count;

            if (m == null || m.Match.State != PvpMatchState.Live)
            {
                log.Info($"[PVP] crystal {index} destroyed in match {intent.MatchId} ignored: the match is {(m == null ? "gone" : m.Match.State.ToString())}");
                return;
            }

            if (m.TickHandler is not IObjectiveModeHandler handler)
            {
                log.Warn($"[PVP] crystal {index} destroyed in match {m.Id} ({m.ModeKey}) ignored: not an objective match");
                return;
            }

            if (!handler.OnObjectiveDestroyed(m, index))
            {
                log.Info($"[PVP] crystal {index} destroyed in match {m.Id} ({m.ModeKey}) ignored: already counted, not a planned crystal, or the mode has none");
                return;
            }

            m.Match.ScoreBoard.TryGetValue(CrystalWinCondition.AttackerTeam, out var destroyed);

            log.Info($"[PVP] match {m.Id}: crystal {index} destroyed (killer 0x{intent.KillerId:X8}); {destroyed} of {m.Plan?.Count ?? 0} down");
        }

        private void Advance(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            PollZoneMarkers(m);

            switch (m.Match.State)
            {
                case PvpMatchState.AwaitingAccept: AdvanceAwaitingAccept(m, now, dials); break;
                case PvpMatchState.Staging: AdvanceStaging(m, now, dials); break;
                case PvpMatchState.Countdown: ProcessDeathReturns(m); AdvanceCountdown(m, now, dials); break;
                case PvpMatchState.Live: ProcessDeathReturns(m); AdvanceLive(m, now, dials); break;
                case PvpMatchState.Resolving: ProcessDeathReturns(m); AdvanceResolving(m, now, dials); break;
            }
        }

        private void SetState(MatchRuntime m, PvpMatchState next, DateTime now, string why)
        {
            var previous = m.Match.State;

            if (next == PvpMatchState.Live)
                m.Match.GoLive(now);
            else
                m.Match.SetState(next);

            m.StateSinceUtc = now;

            log.Info($"[PVP] match {m.Id} ({m.ModeKey}, map {m.Map?.MapKey ?? "-"}): {previous} -> {next}{(why != null ? " (" + why + ")" : "")}; participants {string.Join(", ", m.Seats.Values.Select(s => s.Name + "/t" + s.TeamIndex))}");

            // Battlegrounds: the match is over for good; any team fellowship still standing is dissolved, exactly once.
            if (next == PvpMatchState.Closed || next == PvpMatchState.Canceled)
                DisposeTeamFellowships(m);
        }

        // =====================================================================================================
        // matchmaking
        // =====================================================================================================

        private int ActiveMatchCount => matches.Count(m => m.Match.State != PvpMatchState.Closed && m.Match.State != PvpMatchState.Canceled);

        /// <summary>Active matches formed through the bg room: pvp_bg_max_concurrent_matches counts these, inside the global cap.</summary>
        private int ActiveBgMatchCount => matches.Count(m => m.Match.State != PvpMatchState.Closed && m.Match.State != PvpMatchState.Canceled
            && string.Equals(m.RoomKey, BattlegroundModes.RoomKey, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The concurrent-match caps RunMatchmaking stops forming at: the global cap, plus (for the battleground room,
        /// <paramref name="bg"/> non-null) the bg cap inside it. Shared by the matchmaking loop and the Arena Crier.
        /// </summary>
        private bool MatchCapReached(PvpArenaDials dials, BattlegroundDials bg) =>
            ActiveMatchCount >= dials.MaxConcurrentMatches || (bg != null && ActiveBgMatchCount >= bg.MaxConcurrentMatches);

        /// <summary>
        /// True when RunMatchmaking would not form the battleground room this tick however full it is: arena disabled,
        /// ratings still loading, the room in backoff, no enabled bg modes, or a concurrent-match cap reached. Mirrors the
        /// guards at the top of RunMatchmaking's room loop, and the Arena Crier uses it so it never promises a held match.
        /// </summary>
        private bool BgMatchmakingHeld(Room room, DateTime now, PvpArenaDials dials, BattlegroundDials bg) =>
            !dials.Enabled
            || Ratings.State == PvpRatingStoreState.Loading
            || (room.BackoffUntilUtc.HasValue && room.BackoffUntilUtc.Value > now)
            || PlayableBgModes(bg).Count == 0
            || MatchCapReached(dials, bg);

        private void RunMatchmaking(DateTime now, PvpArenaDials dials)
        {
            if (!dials.Enabled)
                return;

            if (Ratings.State == PvpRatingStoreState.Loading)
            {
                if (!loggedWaitingForRatings && rooms.Values.Any(r => r.Units.Count > 0))
                {
                    log.Warn("[PVP] matchmaking is waiting for the boot ratings read to come back");
                    loggedWaitingForRatings = true;
                }

                return;
            }

            foreach (var room in rooms.Values)
            {
                if (!ModeEnabled(room.ModeKey, dials) || room.Units.Count == 0)
                {
                    room.LastFfaCountAnnounced = -1;
                    continue;
                }

                if (room.BackoffUntilUtc.HasValue && room.BackoffUntilUtc.Value > now)
                    continue;

                var isBg = IsBgRoom(room);
                BattlegroundDials bg = null;
                IReadOnlyList<PvpModeDefinition> bgModes = null;
                PvpModeDefinition mode;

                if (isBg)
                {
                    // One snapshot per pass: the matchmaker's dials, the mode definitions (win condition, zone, respawn
                    // delay) and the cap all come from it.
                    bg = BgDials();
                    bgModes = PlayableBgModes(bg);

                    if (bgModes.Count == 0)
                        continue;

                    // Every battleground mode shares the matchmaker and the ladder; the mode itself is picked once a
                    // match forms.
                    mode = bgModes[0];
                }
                else
                    mode = ModeFor(room.ModeKey, dials);

                var matchmaker = mode.Matchmaker();

                while (room.Units.Count > 0)
                {
                    // The battleground cap counts inside the global one (Docs/Pvp/BATTLEGROUNDS.md "Settings").
                    if (MatchCapReached(dials, isBg ? bg : null))
                    {
                        // Queued players stay queued; each unit hears it once.
                        foreach (var unit in room.Units.Where(u => !u.NotifiedFull))
                        {
                            unit.NotifiedFull = true;
                            SendAll(unit.Ids, PvpArenaText.MatchesFull);
                        }

                        break;
                    }

                    var waiting = room.Units.Select(u => ToEntrant(u, mode, now, dials)).ToList();
                    var ctx = new MatchmakingContext(now, dials.MmWindowInitial, dials.MmWindowGrowthPerMinute, dials.MmWindowMax, dials.DuoVsSoloAfterSeconds,
                        dials.FfaTargetPlayers, dials.FfaMinPlayers, dials.FfaMaxPlayers, dials.FfaMinDecaySeconds, dials.BlockSameIp, bg);

                    var proposal = matchmaker.TryForm(waiting, ctx);

                    if (proposal == null)
                        break;

                    FormMatch(room, isBg ? PickBgMode(bgModes, room, proposal) : mode, proposal, now, dials, bg);
                }

                if (room.ModeKey == ArenaMapCatalog.FfaKey)
                    AnnounceFfaLobby(room, now, dials);
            }
        }

        private QueueEntrant ToEntrant(QueuedUnit u, PvpModeDefinition mode, DateTime now, PvpArenaDials dials)
        {
            var ratings = u.Ids.Select(id => Ratings.Read(id, mode.LadderKey, now, dials).Rating).ToList();

            // IpKey stays the first member's (what every arena matchmaker reads); IpKeys and MonarchIds carry each
            // member's own, which only the battleground matchmaker reads (same-IP split, clanmate split).
            return new QueueEntrant(u.EntrantId, u.Ids, ratings, u.IpOf(u.Ids[0]), u.QueuedAtUtc,
                MonarchIds: u.Ids.Select(u.MonarchOf).ToList(),
                IpKeys: u.Ids.Select(u.IpOf).ToList());
        }

        private void AnnounceFfaLobby(Room room, DateTime now, PvpArenaDials dials)
        {
            var count = room.PlayerCount;

            if (count == 0 || count == room.LastFfaCountAnnounced)
                return;

            room.LastFfaCountAnnounced = count;

            // The same formula LobbyMatchmaker forms at, so the line never promises a size the matchmaker disagrees with.
            var oldest = room.Units.Min(u => u.QueuedAtUtc);
            var needed = ArenaMapCatalog.FfaDecayedTargetSize(now - oldest, dials);

            var text = PvpArenaText.Fill(PvpArenaText.FfaLobbyProgress, ("count", count), ("needed", needed));

            foreach (var unit in room.Units)
                SendAll(unit.Ids, text);
        }

        private void FormMatch(Room room, PvpModeDefinition mode, MatchProposal proposal, DateTime now, PvpArenaDials dials, BattlegroundDials bg = null)
        {
            if (IsBgRoom(room))
                bgMatchFormedSinceCrierTick = true;

            var unitsByCharacter = new Dictionary<uint, QueuedUnit>();

            foreach (var unit in room.Units)
                foreach (var id in unit.Ids)
                    unitsByCharacter[id] = unit;

            // Attack/Defend (Docs/Pvp/ATTACK-DEFEND.md "Sides"): the attacking side is a coin flip, applied by reversing the proposal's
            // team order BEFORE the teams are built, so team 0 is always the attacker (layout side 0) and nothing downstream needs a
            // side lookup.
            var proposedTeams = proposal.Teams;
            var sidesSwapped = false;

            if (mode.ModeKey == BattlegroundModes.AttackDefendModeKey && proposedTeams.Count == 2)
            {
                sidesSwapped = FlipAttackDefendSides();

                if (sidesSwapped)
                    proposedTeams = new[] { proposedTeams[1], proposedTeams[0] };
            }

            // Rebuild every participant with its OWN session IP (a duo's QueueEntrant carries only one key).
            var teams = new List<PvpTeam>();

            foreach (var team in proposedTeams)
            {
                var members = team.Members.Select(p => new PvpParticipant(p.CharacterId, p.RatingAtStart, unitsByCharacter[p.CharacterId].IpOf(p.CharacterId))).ToList();
                teams.Add(new PvpTeam(teams.Count, members));
            }

            var matchUnits = teams.SelectMany(t => t.Members).Select(p => unitsByCharacter[p.CharacterId]).Distinct().ToList();

            foreach (var unit in matchUnits)
                room.Units.Remove(unit);

            var map = mode.IsObjective ? mode.MapPool[bgMapRotation++ % mode.MapPool.Count] : mode.MapPool[mapRotation++ % mode.MapPool.Count];

            var m = new MatchRuntime(this, new PvpMatch(Guid.NewGuid(), mode.ModeKey, teams, now), mode, map, matchUnits)
            {
                StateSinceUtc = now,
                AcceptDeadlineUtc = now.AddSeconds(dials.AcceptSeconds),
            };

            if (mode.IsObjective)
            {
                // Snapshotted from the SAME dials the mode (and so its ScoreWinCondition) was built from this tick.
                var d = bg ?? BgDials();

                m.Layout = LayoutResolver(map.MapKey);
                m.ObjectiveTimeLimitSeconds = BattlegroundModes.TimeLimitSeconds(mode.ModeKey, d);
                m.ScoreTarget = BattlegroundModes.ScoreTarget(mode.ModeKey, d);
                m.BgDials = d;

                // Formation binding: the one place a handler is bound to the match's own layout and, for a crystal mode, its plan. It goes
                // through IObjectiveModeHandler like every later read, so formation never switches on a handler's type. The plan is the
                // mode's data (its key), built from this snapshot and the attacking team's size at formation.
                var objectiveHandler = m.TickHandler as IObjectiveModeHandler;
                var plan = mode.ModeKey == BattlegroundModes.AttackDefendModeKey ? BindAttackDefendPlan(m, d, sidesSwapped) : null;

                objectiveHandler?.BindMatch(m.Layout, plan);

                // The zone-marker ring (Docs/Pvp/BATTLEGROUNDS.md "Zone markers"): planned here, once, on the zone this match's own
                // handler SCORES, read after the bind above rebuilt it from the resolved layout, so the ring sits exactly on the scored
                // edge even if the live dials later differ. The layout supplies only the wcid and the landblock. A mode with no ring
                // (MarkerZone null) and a wcid of 0 both leave it empty.
                var markerZone = objectiveHandler?.MarkerZone;

                if (markerZone.HasValue && d.KothMarkersEnabled && m.Layout != null)
                    m.ZoneMarkers = BattlegroundZoneMarkers.Plan(m.Layout, markerZone.Value, d.KothMarkerSpacing, m.Layout.ZoneMarkerWcid, d.KothMarkerZOffset);

                foreach (var team in teams)
                {
                    m.Match.ScoreBoard[team.TeamIndex] = 0;
                    m.Match.TeamKills[team.TeamIndex] = 0;
                }
            }

            foreach (var team in teams)
            {
                foreach (var p in team.Members)
                {
                    var unit = unitsByCharacter[p.CharacterId];
                    m.Seats[p.CharacterId] = new Seat(p, team.TeamIndex, unit.NameOf(p.CharacterId), unit) { TemplateKey = unit.TemplateOf(p.CharacterId) };
                }
            }

            // Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): decided ONCE, here, from the same dials snapshot
            // the mode was built from, so every binding of the match (Staging onward) carries the same flag.
            DecideTeamFellowships(m, bg);

            // PvP Template Facets: the ONE kit-warm path. The kit is issued at entry; warming each template's kit
            // weenies off-thread now, ahead of the accept window and staging, means that issue does no synchronous
            // world-database read. A warm still in flight only means the apply reads the weenie itself.
            foreach (var key in m.Seats.Values.Select(s => s.TemplateKey).Where(k => k != null).Distinct())
            {
                var definition = templateCatalog?.Get(key)?.Definition;

                if (definition != null)
                    WarmKit(definition);
            }

            matches.Add(m);

            log.Info($"[PVP] match {m.Id} ({m.ModeKey}) formed on map {map.MapKey}: {string.Join(" vs ", teams.Select(t => string.Join("+", t.Members.Select(p => m.Seats[p.CharacterId].Name + "(" + p.RatingAtStart + ", " + (m.Seats[p.CharacterId].TemplateKey ?? "-") + ")"))))}; accept by {m.AcceptDeadlineUtc:HH:mm:ss}");

            var modeLabel = PvpArenaText.ModeLabel(m.ModeKey);
            var popup = PvpArenaText.Fill(PvpArenaText.AcceptPopup, ("mode", modeLabel), ("map", map.DisplayName));
            var fallback = PvpArenaText.Fill(PvpArenaText.AcceptPopupFallback, ("mode", modeLabel), ("seconds", dials.AcceptSeconds));

            foreach (var seat in m.Seats.Values)
            {
                if (!players.SendAcceptPrompt(seat.Id, m.Id, popup))
                {
                    log.Info($"[PVP] match {m.Id}: accept popup for {seat.Name} could not be queued; sent the /arena accept line instead");
                    players.Send(seat.Id, fallback);
                }
            }
        }

        /// <summary>The Attack/Defend coin flip; a throwing flip keeps the proposal's order (logged).</summary>
        private bool FlipAttackDefendSides()
        {
            try
            {
                return AttackDefendSwapSides?.Invoke() ?? false;
            }
            catch (Exception ex)
            {
                log.Error("[PVP] the Attack/Defend side flip threw; keeping the proposal's team order", ex);
                return false;
            }
        }

        /// <summary>
        /// Builds a forming Attack/Defend match's own plan (Docs/Pvp/ATTACK-DEFEND.md "Crystals", "Match flow") from the SAME dials snapshot
        /// the mode was built with: the crystals for the resolved layout and the attacking team's size at formation, and a fresh
        /// <see cref="CrystalWinCondition"/> on the planned count and the mode's time limit; the score target becomes the crystal count. The
        /// health the crystals are sized for is kept on the match for the attacker-leave rescale. Returns the plan for the handler's bind.
        /// </summary>
        private AttackDefendPlan BindAttackDefendPlan(MatchRuntime m, BattlegroundDials d, bool sidesSwapped)
        {
            var attackers = m.Match.Teams.FirstOrDefault(t => t.TeamIndex == CrystalWinCondition.AttackerTeam)?.Members.Count ?? 0;
            var plan = AttackDefendPlan.Build(m.Layout, d, attackers);

            m.Plan = plan;

            // Sequential crystals: the match carries the plan's sequence so the crystal damage gate, which only sees a player's binding
            // and its match, can read which crystal is vulnerable. Null (any-order play) when the dial was off at formation.
            m.Match.CrystalSequence = plan.Sequence;

            // Engaged-defender reduction: the same formation snapshot, carried on the match so the crystal damage sink can read it from
            // the attacker's binding on the crystal's landblock thread.
            m.Match.DefenderReduction = CrystalDefenderReductionDials.From(d);
            m.CrystalsSizedForAttackers = plan.AttackerCount;
            m.WinCondition = new CrystalWinCondition(plan.Count, m.ObjectiveTimeLimitSeconds);
            m.ScoreTarget = plan.Count;

            log.Info($"[PVP] match {m.Id} ({m.ModeKey}): attackers are team 0 ({(sidesSwapped ? "the proposal's second team, by the coin flip" : "the proposal's first team")}); {plan.Count} crystal(s), {(plan.Sequence != null ? "in sequence" : "any order")}, at {plan.HealthPerCrystal} health for {plan.AttackerCount} attacker(s) [{string.Join(", ", plan.Crystals.Select(c => $"#{c.Index} {c.Site.Name} ({c.Compass})"))}]; time limit {m.ObjectiveTimeLimitSeconds} s");

            return plan;
        }

        /// <summary>
        /// Puts units back at the FRONT of their room: ahead of everyone waiting, keeping their own relative order.
        /// Their QueuedAt is moved earlier than the room's oldest (never later than it was), because every
        /// matchmaker orders by it.
        /// </summary>
        private void RequeueAtFront(string roomKey, IEnumerable<QueuedUnit> units, DateTime now)
        {
            // A ROOM key (MatchRuntime.RoomKey): a battleground match goes back to the one bg room, never a room named after its mode.
            var modeKey = roomKey;
            var room = rooms[roomKey];
            var toRequeue = units.Where(u => u.Ids.Count > 0).OrderBy(u => u.QueuedAtUtc).ToList();

            if (toRequeue.Count == 0)
                return;

            var front = room.Units.Count > 0 ? room.Units.Min(u => u.QueuedAtUtc) : now;

            for (var i = 0; i < toRequeue.Count; i++)
            {
                var unit = toRequeue[i];
                var slot = front.AddMilliseconds(-(toRequeue.Count - i));

                if (slot < unit.QueuedAtUtc)
                    unit.QueuedAtUtc = slot;

                unit.NotifiedFull = false;
            }

            room.Units.InsertRange(0, toRequeue);
            room.LastFfaCountAnnounced = -1;

            log.Info($"[PVP] requeued at the front of {modeKey}: {string.Join(", ", toRequeue.SelectMany(u => u.Names))}");
        }

        // =====================================================================================================
        // AwaitingAccept
        // =====================================================================================================

        private void AdvanceAwaitingAccept(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            var allAnswered = m.Seats.Values.All(s => s.Answer != AcceptAnswer.Pending);
            var timedOut = now >= m.AcceptDeadlineUtc;

            if (m.Mode.Accept is AcceptPolicy.AllOrNothingPolicy)
            {
                if (m.Seats.Values.All(s => s.Answer == AcceptAnswer.Accepted))
                {
                    BeginStaging(m, now, dials);
                    return;
                }

                if (!timedOut)
                    return;

                var late = m.Seats.Values.Where(s => s.Answer == AcceptAnswer.Pending).ToList();

                foreach (var seat in late)
                {
                    players.AbortAcceptPrompt(seat.Id, m.Id);
                    players.Send(seat.Id, PvpArenaText.AcceptTimeoutSelf);
                    LockOut(seat.Id, now, dials, "did not accept in time");
                }

                FailAllOrNothing(m, late.Select(s => s.Id).ToHashSet(), now, "accept timeout");
                return;
            }

            if (!allAnswered && !timedOut)
                return;

            foreach (var seat in m.Seats.Values.Where(s => s.Answer == AcceptAnswer.Pending))
            {
                seat.Answer = AcceptAnswer.TimedOut;
                players.AbortAcceptPrompt(seat.Id, m.Id);
                players.Send(seat.Id, PvpArenaText.AcceptTimeoutSelf);
            }

            var accepted = m.Seats.Values.Where(s => s.Answer == AcceptAnswer.Accepted).Select(s => s.Id).ToList();

            if (m.Mode.Accept.Proceeds(accepted.Count, m.Seats.Count))
            {
                DropSeats(m, m.Seats.Keys.Except(accepted).ToList());
                BeginStaging(m, now, dials);
                return;
            }

            foreach (var id in accepted)
                players.Send(id, PvpArenaText.TooFewAccepted);

            RequeueAtFront(m.RoomKey, UnitsKeeping(m, accepted), now);
            SetState(m, PvpMatchState.Canceled, now, $"only {accepted.Count} of {m.Seats.Count} accepted; the floor is {MinimumFor(m)}");
        }

        private static int MinimumFor(MatchRuntime m) => m.Mode.Accept is AcceptPolicy.ProceedIfAtLeastPolicy p ? p.CurrentMinimum : m.Seats.Count;

        /// <summary>
        /// An AllOrNothing match loses a player before anyone was placed: everyone else hears a player declined and
        /// goes back to the FRONT of the queue, and the match is canceled. <paramref name="failed"/> are the players
        /// who caused it; they are messaged (and locked out) by the caller, never requeued.
        /// </summary>
        private void FailAllOrNothing(MatchRuntime m, HashSet<uint> failed, DateTime now, string why)
        {
            var keep = m.Seats.Keys.Where(id => !failed.Contains(id)).ToList();

            foreach (var seat in m.Seats.Values.Where(s => !failed.Contains(s.Id)))
            {
                if (seat.Answer == AcceptAnswer.Pending)
                    players.AbortAcceptPrompt(seat.Id, m.Id);

                players.Send(seat.Id, PvpArenaText.OtherDeclined);
            }

            RequeueAtFront(m.RoomKey, UnitsKeeping(m, keep), now);
            CancelMatch(m, now, why);
        }

        /// <summary>
        /// The match's original queue units, trimmed to <paramref name="keep"/>. A duo that lost a member comes back
        /// as a solo unit for the partner, keeping its queue time.
        /// </summary>
        private static List<QueuedUnit> UnitsKeeping(MatchRuntime m, IReadOnlyCollection<uint> keep)
        {
            var result = new List<QueuedUnit>();

            foreach (var unit in m.Units)
            {
                var ids = unit.Ids.Where(keep.Contains).ToList();

                if (ids.Count == 0)
                    continue;

                result.Add(ids.Count == unit.Ids.Count ? unit : unit.Subset(ids));
            }

            return result;
        }

        /// <summary>FFA only: removes seats before anyone is placed, and re-indexes the teams so TeamIndex stays each team's list position.</summary>
        private static void DropSeats(MatchRuntime m, IReadOnlyCollection<uint> ids)
        {
            if (ids.Count == 0)
                return;

            foreach (var id in ids)
                m.Seats.Remove(id);

            var kept = m.Match.Teams
                .Select(t => t.Members.Where(p => !ids.Contains(p.CharacterId)).ToList())
                .Where(members => members.Count > 0)
                .ToList();

            m.Match.Teams.Clear();

            for (var i = 0; i < kept.Count; i++)
            {
                m.Match.Teams.Add(new PvpTeam(i, kept[i]));

                foreach (var p in kept[i])
                    m.Seats[p.CharacterId].TeamIndex = i;
            }

            m.Units.RemoveAll(u => u.Ids.All(ids.Contains));
        }

        private void LockOut(uint characterId, DateTime now, PvpArenaDials dials, string why)
        {
            if (dials.DeclineLockoutSeconds <= 0)
                return;

            lockouts[characterId] = now.AddSeconds(dials.DeclineLockoutSeconds);
            log.Info($"[PVP] 0x{characterId:X8} locked out of the arena queue for {dials.DeclineLockoutSeconds} s ({why})");
        }

        // =====================================================================================================
        // Staging
        // =====================================================================================================

        private void BeginStaging(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            SetState(m, PvpMatchState.Staging, now, "accepted");

            // Anyone who logged out since accepting is not placed. Re-checked here too (accept time):
            // the denylist is only checked at queue join otherwise, so a character added to it AFTER
            // joining but before placement would slip through without this second check.
            var offline = m.Seats.Values.Where(s => !players.IsOnline(s.Id)).Select(s => s.Id).ToList();
            var denylisted = m.Seats.Values.Where(s => PvpArenaDenylistTunables.IsDenylisted(s.Id)).Select(s => s.Id).ToList();
            var ineligible = offline.Concat(denylisted).Distinct().ToList();

            if (ineligible.Count > 0)
            {
                foreach (var id in denylisted)
                    if (players.IsOnline(id))
                        players.Send(id, PvpArenaText.Denylisted);

                if (m.Mode.Accept is AcceptPolicy.AllOrNothingPolicy)
                {
                    FailAllOrNothing(m, ineligible.ToHashSet(), now, $"{ineligible.Count} accepted player(s) ineligible before placement ({offline.Count} offline, {denylisted.Count} denylisted)");
                    return;
                }

                DropSeats(m, ineligible);

                if (!m.Mode.Accept.Proceeds(m.Seats.Count, m.Seats.Count + ineligible.Count))
                {
                    foreach (var seat in m.Seats.Values)
                        players.Send(seat.Id, PvpArenaText.TooFewAccepted);

                    RequeueAtFront(m.RoomKey, UnitsKeeping(m, m.Seats.Keys.ToList()), now);
                    CancelMatch(m, now, "too few players online/eligible at placement");
                    return;
                }
            }

            IReadOnlyList<SpawnAssignment> assignments;

            if (m.Mode.IsObjective)
            {
                if (m.Layout == null)
                {
                    log.Error($"[PVP] match {m.Id}: map {m.Map.MapKey} has no battleground layout");
                    FailBeforePlacement(m, now, dials, "no battleground layout");
                    return;
                }

                assignments = BattlegroundMapCatalog.AssignSpawns(m.Layout, m.Match.Teams);
            }
            else
                assignments = ArenaMapCatalog.AssignSpawns(m.ModeKey, m.Match.Teams);

            if (assignments == null)
            {
                log.Error($"[PVP] match {m.Id}: the team shape does not fit the {m.ModeKey} spawn set on {m.Map.MapKey}");
                FailBeforePlacement(m, now, dials, "no spawn assignment");
                return;
            }

            m.Assignments = assignments;

            // Timed for the battleground map in particular: 0x016C is 536 cells against the arena's 84, and Allocate runs
            // synchronously on the world thread (Docs/Pvp/BATTLEGROUNDS.md "Risks"; EphemeralMatchSpaceProvider).
            var allocateTimer = Stopwatch.StartNew();
            var allocation = spaces.Allocate(m.Map, m.Seats.Keys.ToList());
            allocateTimer.Stop();

            log.Info($"[PVP] match {m.Id} ({m.ModeKey}): allocating map {m.Map.MapKey} (landblock 0x{m.Map.LandblockId:X4}) took {allocateTimer.Elapsed.TotalMilliseconds:F1} ms ({(allocation.Succeeded ? "ok" : allocation.Failure.ToString())})");

            if (!allocation.Succeeded)
            {
                log.Warn($"[PVP] match {m.Id}: space allocation refused: {allocation.Failure} - {allocation.Detail}");
                FailBeforePlacement(m, now, dials, $"allocation refused ({allocation.Failure})");
                return;
            }

            m.Space = allocation.Space;
            log.Info($"[PVP] match {m.Id}: space allocated, instance 0x{m.Space.Instance:X8}");
        }

        /// <summary>
        /// Nobody was placed and it was nobody's fault (no space, no spawn fit): cancel, put everyone back at the
        /// FRONT of the queue, and pause the room so a refusal that repeats does not loop every tick.
        /// </summary>
        private void FailBeforePlacement(MatchRuntime m, DateTime now, PvpArenaDials dials, string why)
        {
            SendAll(m.Seats.Keys, PvpArenaText.Canceled);
            RequeueAtFront(m.RoomKey, UnitsKeeping(m, m.Seats.Keys.ToList()), now);
            rooms[m.RoomKey].BackoffUntilUtc = now.AddSeconds(AllocationBackoffSeconds);
            CancelMatch(m, now, why);
        }

        private void AdvanceStaging(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            // A walkout after arriving is charged before anything else, even on the tick the staging timer runs out.
            if (m.Dispatched && PollWalkoutsAndResolve(m, now, dials))
                return;

            if ((now - m.StateSinceUtc).TotalSeconds >= dials.StagingTimeoutSeconds)
            {
                log.Warn($"[PVP] match {m.Id}: staging timed out after {dials.StagingTimeoutSeconds} s ({(m.Dispatched ? "waiting for arrivals" : "space never became ready")})");

                if (!m.Dispatched)
                    SendAll(m.Seats.Keys, PvpArenaText.Canceled);

                // No-fault cancel: someone never loaded in. Nobody is charged; everyone who DID arrive and is still in
                // the match goes back to the FRONT of the queue, since the failure was not theirs. The seats that never
                // arrived are not requeued.
                var arrived = m.Seats.Values.Where(s => s.SeenInInstance && !s.Participant.ExitReason.HasValue).Select(s => s.Id).ToList();

                CancelMatch(m, now, $"staging timeout; {arrived.Count} of {m.Seats.Count} arrived");

                if (arrived.Count > 0)
                    RequeueAtFront(m.RoomKey, UnitsKeeping(m, arrived), now);

                return;
            }

            if (m.Space == null)
                return;

            if (!m.Dispatched)
            {
                var readiness = spaces.GetReadiness(m.Space);

                if (readiness == MatchSpaceReadiness.Loading)
                    return;

                if (readiness == MatchSpaceReadiness.Gone)
                {
                    log.Warn($"[PVP] match {m.Id}: instance 0x{m.Space.Instance:X8} is gone before placement");
                    FailBeforePlacement(m, now, dials, "space gone before placement");
                    return;
                }

                // Battlegrounds: the pen seals go in after the space is Ready and before anyone is sent in; staging
                // waits on the placement, and a failure is the same no-fault cancel as a refused space.
                if (!FixturesPlaced(m, now, dials))
                    return;

                // King of the Hill: the start gates close each team's start room for the Countdown; same strict wait, and they are
                // in before anyone is dispatched (Docs/Pvp/BATTLEGROUNDS.md "Start gates").
                if (!StartGatesPlaced(m, now, dials))
                    return;

                // Attack/Defend: the crystals go in after the seals and are waited on the same way; a failure is the same no-fault
                // cancel (Docs/Pvp/ATTACK-DEFEND.md "Placement").
                if (!ObjectivesPlaced(m, now, dials))
                    return;

                // The zone markers go in alongside dispatch and are never waited on: cosmetic, best effort, log-only.
                QueueZoneMarkers(m);

                Dispatch(m, now, dials);
                return;
            }

            RetryBusyEntries(m, now);

            // Countdown waits until every placed, still-active participant has been seen in the instance (the walkout
            // poll above marks each arrival).
            if (m.Seats.Values.Any(s => !s.Participant.ExitReason.HasValue && !s.SeenInInstance))
                return;

            BeginCountdown(m, now, dials);
        }

        /// <summary>
        /// The pen seals (Docs/Pvp/BATTLEGROUNDS.md "Pen seals"). True when nothing needs placing (every arena mode, or a
        /// layout with no seals) or the placement finished. On the first call it queues the placement through
        /// <see cref="IBattlegroundMatchSpaces.SpawnFixtures"/>, which runs on the instance landblock's own queue; until
        /// that job reports, staging waits (the staging timeout still applies). A refusal or a failed job cancels with
        /// <see cref="FailBeforePlacement"/>; no cleanup is needed, the pieces die with the instance.
        /// </summary>
        private bool FixturesPlaced(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            var seals = m.Layout?.Seals;

            if (!m.Mode.IsObjective || seals == null || seals.Count == 0)
                return true;

            if (m.Fixtures == null)
            {
                BattlegroundFixtureJob job = null;

                try
                {
                    job = bgSpaces?.SpawnFixtures(m.Space, seals);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] match {m.Id}: queuing the pen seals threw", ex);
                }

                if (job == null)
                {
                    FailBeforePlacement(m, now, dials, "pen seals could not be queued");
                    return false;
                }

                m.Fixtures = job;
                log.Info($"[PVP] match {m.Id}: {seals.Count} pen seal piece(s) queued on instance 0x{m.Space.Instance:X8}");
            }

            switch (m.Fixtures.Status)
            {
                case BattlegroundFixtureStatus.Pending:
                    return false;

                case BattlegroundFixtureStatus.Failed:
                    log.Warn($"[PVP] match {m.Id}: pen seal placement failed ({m.Fixtures.Placed} placed): {m.Fixtures.Detail}");
                    FailBeforePlacement(m, now, dials, "pen seal placement failed");
                    return false;

                default:
                    if (!m.FixturesLogged)
                    {
                        m.FixturesLogged = true;
                        log.Info($"[PVP] match {m.Id}: {m.Fixtures.Placed} pen seal piece(s) placed; dispatching");
                    }

                    return true;
            }
        }

        /// <summary>
        /// The start gates (Docs/Pvp/BATTLEGROUNDS.md "Start gates"). True when the layout declares none or the placement finished.
        /// Queued once through <see cref="IBattlegroundMatchSpaces.SpawnStartGates"/> after the pen seals are in, and waited on the same
        /// way (strict: a refusal, a null job or a failed job is the no-fault <see cref="FailBeforePlacement"/> cancel, which also
        /// takes any gate that did land back down). They come down at Live in <see cref="AdvanceCountdown"/>, or earlier through
        /// <see cref="RemoveStartGates"/> on a cancel or close.
        /// </summary>
        private bool StartGatesPlaced(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            var gates = m.Layout?.StartGates;

            if (!m.Mode.IsObjective || gates == null || gates.Count == 0)
                return true;

            if (m.GateJob == null)
            {
                BattlegroundFixtureJob job = null;

                try
                {
                    job = bgSpaces?.SpawnStartGates(m.Space, gates);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] match {m.Id}: queuing the start gates threw", ex);
                }

                if (job == null)
                {
                    FailBeforePlacement(m, now, dials, "start gates could not be queued");
                    return false;
                }

                m.GateJob = job;
                log.Info($"[PVP] match {m.Id}: {gates.Count} start gate piece(s) queued on instance 0x{m.Space.Instance:X8}");
            }

            switch (m.GateJob.Status)
            {
                case BattlegroundFixtureStatus.Pending:
                    return false;

                case BattlegroundFixtureStatus.Failed:
                    log.Warn($"[PVP] match {m.Id}: start gate placement failed ({m.GateJob.Placed} placed): {m.GateJob.Detail}");
                    FailBeforePlacement(m, now, dials, "start gate placement failed");
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Takes the start gates down (at Live, or on a cancel or close). Idempotent: the job is dropped first, so a second call is a
        /// no-op, and a throw is logged and swallowed because a teardown must never be stopped by it.
        /// </summary>
        private void RemoveStartGates(MatchRuntime m, string why)
        {
            var job = m.GateJob;

            if (job == null)
                return;

            m.GateJob = null;

            try
            {
                bgSpaces?.RemoveStartGates(m.Space, job);
                log.Info($"[PVP] match {m.Id}: start gates removed ({why})");
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: removing the start gates threw ({why})", ex);
            }
        }

        /// <summary>
        /// The Attack/Defend crystals (Docs/Pvp/ATTACK-DEFEND.md "Placement"). True when the match plans none (every other mode) or the
        /// placement finished. On the first call it queues the placement through <see cref="IObjectiveMatchSpaces.SpawnObjectives"/>,
        /// each crystal tagged with this match, its index and the defending team; until that job reports, staging waits (the staging
        /// timeout still applies). Strict: an empty plan, a crystal wcid of 0, a missing seam, a throw, a null job or a failed job all
        /// cancel with <see cref="FailBeforePlacement"/>, so nobody is charged; the placed pieces die with the instance.
        /// </summary>
        private bool ObjectivesPlaced(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            var plan = m.Plan;

            if (plan == null)
                return true;

            if (m.Objectives == null)
            {
                var wcid = m.Layout?.CrystalWcid ?? 0;

                if (plan.Count == 0 || wcid == 0)
                {
                    log.Error($"[PVP] match {m.Id}: map {m.Map.MapKey} plans {plan.Count} crystal(s) with crystal wcid {wcid}; nothing can be placed");
                    FailBeforePlacement(m, now, dials, "no crystals to place");
                    return false;
                }

                BattlegroundFixtureJob job = null;
                var matchId = m.Id;

                try
                {
                    job = objectiveSpaces?.SpawnObjectives(m.Space, wcid, plan, crystal => new BattlegroundObjectiveTag(matchId, crystal.Index, CrystalWinCondition.DefenderTeam));
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] match {m.Id}: queuing the crystals threw", ex);
                }

                if (job == null)
                {
                    FailBeforePlacement(m, now, dials, "crystals could not be queued");
                    return false;
                }

                m.Objectives = job;
                log.Info($"[PVP] match {m.Id}: {plan.Count} crystal(s) (wcid {wcid}, {plan.HealthPerCrystal} health) queued on instance 0x{m.Space.Instance:X8}");
            }

            switch (m.Objectives.Status)
            {
                case BattlegroundFixtureStatus.Pending:
                    return false;

                case BattlegroundFixtureStatus.Failed:
                    log.Warn($"[PVP] match {m.Id}: crystal placement failed ({m.Objectives.Placed} placed): {m.Objectives.Detail}");
                    FailBeforePlacement(m, now, dials, "crystal placement failed");
                    return false;

                default:
                    if (!m.ObjectivesLogged)
                    {
                        m.ObjectivesLogged = true;
                        log.Info($"[PVP] match {m.Id}: {m.Objectives.Placed} crystal(s) placed");
                    }

                    return true;
            }
        }

        /// <summary>
        /// Queues the King of the Hill zone markers once (Docs/Pvp/BATTLEGROUNDS.md "Zone markers"), right after the pen
        /// seals are placed and before dispatch, into the match's own space. Never waited on and never fatal: a throw or a
        /// null job is logged and the match carries on without markers. Nothing to do for an arena match, a disabled
        /// setting or a layout whose marker wcid is 0 (all three leave <see cref="MatchRuntime.ZoneMarkers"/> empty).
        /// </summary>
        private void QueueZoneMarkers(MatchRuntime m)
        {
            var pieces = m.ZoneMarkers;

            // MarkersQueued is defensive: today the only caller runs on the single staging tick that dispatches, which
            // is never re-entered, so no test can observe it. It keeps a null or throwing first attempt from ever being
            // retried if a later change calls this from a path that does repeat.
            if (m.MarkersQueued || pieces == null || pieces.Count == 0)
                return;

            m.MarkersQueued = true;

            try
            {
                m.MarkerJob = bgSpaces?.SpawnZoneMarkers(m.Space, pieces);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: queuing the zone markers threw; the match goes on without them", ex);
                return;
            }

            if (m.MarkerJob == null)
                log.Warn($"[PVP] match {m.Id}: the zone markers could not be queued; the match goes on without them");
            else
                log.Info($"[PVP] match {m.Id}: {pieces.Count} zone marker(s) queued on instance 0x{m.Space.Instance:X8}");
        }

        /// <summary>
        /// The moving hill moved (Docs/Pvp/BATTLEGROUNDS.md "Moving hill"): removes the old marker ring and queues a new one on
        /// <paramref name="zone"/>, planned from the SAME formation snapshot as the first ring (spacing, height offset, wcid).
        /// Best effort and never blocking: a missing seam, a null job or a throw is logged and the match carries on. Nothing
        /// to do when the match planned no ring (setting off, marker wcid 0, no layout) or the first ring was never queued.
        /// </summary>
        private void ReplaceZoneMarkers(MatchRuntime m, KothZone zone)
        {
            var d = m.BgDials;

            if (d == null || m.Layout == null || !d.KothMarkersEnabled || m.ZoneMarkers.Count == 0 || !m.MarkersQueued)
                return;

            try
            {
                if (m.MarkerJob != null)
                    bgSpaces?.RemoveZoneMarkers(m.Space, m.MarkerJob);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: removing the old zone markers threw; the new ring is placed anyway", ex);
            }

            var pieces = BattlegroundZoneMarkers.Plan(m.Layout, zone, d.KothMarkerSpacing, m.Layout.ZoneMarkerWcid, d.KothMarkerZOffset);
            m.ZoneMarkers = pieces;
            m.MarkersLogged = false;
            m.MarkerJob = null;

            if (pieces.Count == 0)
                return;

            try
            {
                m.MarkerJob = bgSpaces?.SpawnZoneMarkers(m.Space, pieces);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: queuing the moved zone markers threw; the match goes on without them", ex);
                return;
            }

            if (m.MarkerJob == null)
                log.Warn($"[PVP] match {m.Id}: the moved zone markers could not be queued; the match goes on without them");
            else
                log.Info($"[PVP] match {m.Id}: hill moved; {pieces.Count} zone marker(s) re-queued at ({zone.CenterX:0.##}, {zone.CenterY:0.##})");
        }

        /// <summary>
        /// Logs the zone-marker job's result once, the first tick it is no longer pending ("N of M zone markers placed;
        /// detail"). Log-only: it never changes the match.
        /// </summary>
        private void PollZoneMarkers(MatchRuntime m)
        {
            var job = m.MarkerJob;

            if (job == null || m.MarkersLogged || job.Status == BattlegroundFixtureStatus.Pending)
                return;

            m.MarkersLogged = true;

            var line = $"[PVP] match {m.Id}: {job.Placed} of {m.ZoneMarkers.Count} zone markers placed; {job.Detail ?? "none refused"}";

            if (job.Status == BattlegroundFixtureStatus.Placed && job.Detail == null)
                log.Info(line);
            else
                log.Warn(line);
        }

        /// <summary>
        /// The walkout poll, shared by Staging, Countdown and Live. For every placed, still-active participant: being
        /// seen in the instance marks them arrived; once arrived, being anywhere else (recall, /leaveinstance, a portal)
        /// is a ForfeitLeft, and being offline a ForfeitLogout (the logout hook's intent is the usual route; this is the
        /// backstop). A participant never yet seen in the instance is still loading in and is never forfeited here.
        /// </summary>
        private void PollWalkouts(MatchRuntime m, DateTime now)
        {
            foreach (var seat in m.Seats.Values)
            {
                if (!seat.Dispatched || seat.Participant.ExitReason.HasValue)
                    continue;

                var presence = players.GetPresence(seat.Id, m.Space.Instance);

                if (presence == PvpPresence.InInstance)
                {
                    seat.SeenInInstance = true;
                    continue;
                }

                if (!seat.SeenInInstance)
                    continue;

                if (presence == PvpPresence.Elsewhere)
                    MarkOut(m, seat, ParticipantExit.ForfeitLeft, now, 0);
                else if (presence == PvpPresence.Offline)
                    MarkOut(m, seat, ParticipantExit.ForfeitLogout, now, 0);
            }
        }

        /// <summary>
        /// Before Live: polls walkouts, then asks the win condition whether the match is already decided (one team
        /// left). If so it resolves exactly as it would in Live - the walkout takes the loss, rated by the normal rules,
        /// and the other side wins - through the same <see cref="Resolve"/>. Returns true when it resolved.
        /// </summary>
        private bool PollWalkoutsAndResolve(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            PollWalkouts(m, now);

            if (!m.Seats.Values.Any(s => s.Participant.ExitReason.HasValue))
                return false;

            var outcome = m.WinCondition.Evaluate(m.Match, now);

            if (outcome == null)
                return false;

            Resolve(m, outcome, now, dials);
            return true;
        }

        private void Dispatch(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            m.Masks = dials;

            // PvP Template Facets: the third template check, and the freeze. Each seat's template is resolved from the
            // catalog one last time and its parsed definition copied onto the seat, so every binding of the match
            // carries that frozen copy and a re-snapshot after this point never changes the match.
            if (!FreezeTemplatesForDispatch(m, now, dials))
                return;

            foreach (var assignment in m.Assignments)
            {
                // A seat dropped by the freeze above (FFA) may still be in an assignment list the smaller roster could
                // not be re-fitted to.
                if (!m.Seats.TryGetValue(assignment.Participant.CharacterId, out var seat))
                    continue;
                var exitTo = players.GetCurrentPosition(seat.Id);

                if (exitTo == null)
                {
                    // Went offline between allocation and now. Never bound, so no hook reports it: record it here.
                    log.Warn($"[PVP] match {m.Id}: {seat.Name} is offline at placement; recorded as a logout forfeit");
                    seat.Dispatched = true;
                    MarkOut(m, seat, ParticipantExit.ForfeitLogout, now, 0);
                    continue;
                }

                var spawn = ArenaSpawnPosition.Build(m.Map, assignment.Spawn, m.Space.Instance);

                seat.ExitTo = exitTo;
                seat.Spawn = spawn;
                seat.Dispatched = true;

                // Battlegrounds: the team pen inside THIS instance, carried on every binding so Die() can latch it.
                if (m.Mode.IsObjective && m.Layout != null)
                {
                    var pen = m.Layout.PenFor(seat.TeamIndex);
                    seat.PenPosition = pen != null ? ArenaSpawnPosition.Build(m.Map, pen, m.Space.Instance) : null;
                }

                players.EnterAndTeleport(seat.Id, Binding(m, seat, PvpMatchState.Staging), exitTo, spawn);

                log.Info($"[PVP] match {m.Id}: {seat.Name} sent to apply template {seat.Template?.Key} v{seat.Template?.Version} on team {seat.TeamIndex}, then spawn {assignment.Spawn.Label} on success; exit stamped at {exitTo}");
            }

            m.Dispatched = true;
        }

        /// <summary>
        /// Resolves every seat's template at dispatch and freezes the parsed definition onto the seat. Seats whose
        /// template is no longer offered (an admin disabled it, or changed its modes, after the accept) are removed
        /// without fault through <see cref="RemoveSeatsNoFault"/>. Returns false when that canceled the match.
        /// </summary>
        private bool FreezeTemplatesForDispatch(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            // A mode that opted out of templates has nothing to freeze.
            if (!m.Mode.Templated)
                return true;

            var withdrawn = new List<uint>();

            foreach (var seat in m.Seats.Values)
            {
                if (TemplatesEnabled() && templateCatalog != null && seat.TemplateKey != null && templateCatalog.TryGetOffered(seat.TemplateKey, m.ModeKey, out var entry))
                {
                    seat.Template = entry.Definition;
                    seat.TemplateLabel = entry.Label;
                    continue;
                }

                withdrawn.Add(seat.Id);
                players.Send(seat.Id, TemplatesEnabled()
                    ? PvpArenaText.Fill(PvpArenaText.TemplateWithdrawn, ("template", seat.TemplateKey ?? "(none)"), ("mode", PvpArenaText.ModeLabel(m.ModeKey)))
                    : PvpArenaText.TemplatesDisabled);

                log.Warn($"[PVP] match {m.Id}: {seat.Name}'s template {seat.TemplateKey ?? "(none)"} is not offered for {m.ModeKey} at dispatch; removing the seat without fault");
            }

            if (withdrawn.Count == 0)
                return true;

            return RemoveSeatsNoFault(m, withdrawn, now, "template not offered at dispatch");
        }

        /// <summary>
        /// PvP Template Facets: removes participants who could not enter through no fault of the rest - a template
        /// withdrawn before dispatch, or an apply that failed at entry (<see cref="HandleEntryFailed"/>). The removed
        /// players are not charged, locked out, rated or requeued; their own line is the caller's. The rest follow the
        /// coordinator's existing seat-removal rules for an accept-time loss:
        ///   - AllOrNothing (1v1, 2v2): the match is canceled, anyone already placed is exited and returned, and
        ///     everyone else goes back to the FRONT of the queue keeping their wait time (FailAllOrNothing's shape,
        ///     without a lockout and with its own line).
        ///   - ProceedIfAtLeast (FFA): the seats are dropped; if the rest are still at or above the floor the match
        ///     carries on (spawns re-fitted if nobody is placed yet), otherwise it is canceled and the rest requeued.
        /// Returns true when the match carries on.
        /// </summary>
        private bool RemoveSeatsNoFault(MatchRuntime m, IReadOnlyCollection<uint> removed, DateTime now, string why)
        {
            var removedSet = removed.ToHashSet();
            var rest = m.Seats.Keys.Where(id => !removedSet.Contains(id)).ToList();

            if (m.Mode.Accept is AcceptPolicy.AllOrNothingPolicy)
            {
                // Not yet placed: told here. Already placed: CancelMatch tells them as it exits and returns them.
                foreach (var id in rest.Where(id => !m.Seats[id].Dispatched))
                    players.Send(id, PvpArenaText.OtherCouldNotEnter);

                // Removed seats are never placed or already marked exited, so CancelMatch's exit pass skips them.
                foreach (var id in removedSet)
                    m.Seats[id].Exited = true;

                RequeueAtFront(m.RoomKey, UnitsKeeping(m, rest), now);
                CancelMatch(m, now, $"{why}: {string.Join(", ", removedSet.Select(id => m.Seats[id].Name))}", PvpArenaText.OtherCouldNotEnterPlaced);
                return false;
            }

            var names = removedSet.Select(id => m.Seats[id].Name).ToList();

            DropSeats(m, removedSet);

            if (!m.Mode.Accept.Proceeds(m.Seats.Count, m.Seats.Count + removedSet.Count))
            {
                foreach (var seat in m.Seats.Values)
                    players.Send(seat.Id, PvpArenaText.TooFewAccepted);

                RequeueAtFront(m.RoomKey, UnitsKeeping(m, m.Seats.Keys.ToList()), now);
                CancelMatch(m, now, $"{why} left too few players: {string.Join(", ", names)}");
                return false;
            }

            if (!m.Dispatched && m.Assignments != null)
                m.Assignments = ArenaMapCatalog.AssignSpawns(m.ModeKey, m.Match.Teams) ?? m.Assignments;

            var line = PvpArenaText.Fill(PvpArenaText.SeatRemovedCarriesOn, ("names", string.Join(", ", names)));

            foreach (var seat in m.Seats.Values)
                players.Send(seat.Id, line);

            log.Info($"[PVP] match {m.Id}: {why}; dropped {string.Join(", ", names)}, {m.Seats.Count} remain and the match carries on");
            return true;
        }

        /// <summary>
        /// PvP Template Facets: the live gateway reports that a participant's template apply failed at entry (or the
        /// binding carried no template). They were never bound and never teleported. Honoured only while the match is
        /// still in Staging and the seat has not arrived; anything else is logged and ignored (the seat cannot have
        /// arrived without a successful apply).
        /// </summary>
        private void HandleEntryFailed(MatchRuntime m, PvpIntent intent, DateTime now)
        {
            if (m == null || m.Match.State != PvpMatchState.Staging)
            {
                log.Warn($"[PVP] entry-failed intent for 0x{intent.CharacterId:X8} in match {intent.MatchId} ignored: the match is {(m == null ? "gone" : m.Match.State.ToString())}");
                return;
            }

            if (!m.Seats.TryGetValue(intent.CharacterId, out var seat) || !seat.Dispatched || seat.Exited || seat.SeenInInstance)
            {
                log.Warn($"[PVP] entry-failed intent for 0x{intent.CharacterId:X8} in match {m.Id} ignored: not a placed, not-yet-arrived participant");
                return;
            }

            // Busy only: retried for a short window before it counts (see EntryBusyRetryWindowSeconds).
            if (intent.Retryable)
            {
                seat.EntryBusySinceUtc ??= now;

                if ((now - seat.EntryBusySinceUtc.Value).TotalSeconds < EntryBusyRetryWindowSeconds)
                {
                    seat.RetryEntryAtUtc = now.AddSeconds(EntryBusyRetryIntervalSeconds);
                    log.Info($"[PVP] match {m.Id}: {seat.Name} was busy at template entry; retrying in {EntryBusyRetryIntervalSeconds} s");
                    return;
                }
            }

            seat.EntryFailed = true;
            seat.Exited = true;
            seat.RetryEntryAtUtc = null;

            if (intent.Retryable)
                players.Send(seat.Id, ACE.Server.Pvp.Templates.PvpTemplateText.ApplyBusy);

            players.Send(seat.Id, PvpArenaText.EntryFailedSelf);

            // A failure the player caused (busy past the retry window, or a pack too full to strip into) locks them out
            // like a decline, so failing on purpose cannot cancel a 1v1/2v2 for free. Everyone else still goes back
            // without fault.
            if (intent.PlayerCaused)
                LockOut(seat.Id, now, Dials(), $"entry failed (player caused) in match {m.Id}");

            log.Warn($"[PVP] match {m.Id}: {seat.Name}'s template {seat.Template?.Key ?? seat.TemplateKey} did not apply at entry{(intent.PlayerCaused ? " (player caused; locked out)" : "")}; removing the seat, no fault to the others");

            RemoveSeatsNoFault(m, new[] { seat.Id }, now, intent.PlayerCaused ? "template entry failed (player caused)" : "template entry failed");
        }

        /// <summary>
        /// Re-sends every busy seat whose retry time has come (Staging only, never after the seat arrived, exited or
        /// was removed). The seat's frozen binding, stamped exit and spawn are reused unchanged.
        /// </summary>
        private void RetryBusyEntries(MatchRuntime m, DateTime now)
        {
            foreach (var seat in m.Seats.Values)
            {
                if (seat.RetryEntryAtUtc == null || seat.RetryEntryAtUtc.Value > now)
                    continue;

                seat.RetryEntryAtUtc = null;

                if (!seat.Dispatched || seat.Exited || seat.SeenInInstance || seat.Participant.ExitReason.HasValue || seat.Spawn == null)
                    continue;

                players.EnterAndTeleport(seat.Id, Binding(m, seat, PvpMatchState.Staging), seat.ExitTo, seat.Spawn);
                log.Info($"[PVP] match {m.Id}: {seat.Name} re-sent to apply template {seat.Template?.Key} (busy retry)");
            }
        }

        /// <summary>The seat's team spawn room (cells and footprint) to carry on its binding while it is protected in room mode; null otherwise.</summary>
        private static SpawnRoomArea SeatRoom(MatchRuntime m, Seat seat)
            => seat.ProtectedInRoom ? m.Layout?.SpawnRoomAreaFor(seat.TeamIndex) : null;

        /// <summary>
        /// Grants the seat spawn protection (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection"): on a layout with spawn rooms the seat is protected
        /// without an end time while it stands in its own team's room, and the player side starts the window when it sees them leave; on a
        /// layout without, a plain window of the snapshotted N seconds from <paramref name="now"/>. Sends the matching line. Does nothing when
        /// the match has no battleground dials snapshot (an arena match) or the window is 0. The caller publishes the binding.
        /// </summary>
        private void GrantSpawnProtection(MatchRuntime m, Seat seat, DateTime now)
        {
            var seconds = m.BgDials?.SpawnProtectionSeconds ?? 0;

            if (seconds <= 0)
                return;

            var inRoom = m.Layout?.SpawnRoomSetFor(seat.TeamIndex) != null;

            seat.ProtectionStamp = now.Ticks;
            seat.ProtectedInRoom = inRoom;
            seat.ProtectedUntilUtc = inRoom ? (DateTime?)null : now.AddSeconds(seconds);

            players.Send(seat.Id, inRoom ? BattlegroundText.SpawnProtectedRoom(seconds) : BattlegroundText.SpawnProtected(seconds));
        }

        /// <summary>
        /// Grants every dispatched, still-present seat its match-start protection (see <see cref="GrantSpawnProtection"/>), except a seat
        /// that is outside its own team's spawn room when the layout has rooms.
        /// </summary>
        private void GrantMatchStartProtection(MatchRuntime m, DateTime now)
        {
            if (m.BgDials == null)
                return;

            foreach (var seat in m.Seats.Values)
            {
                if (!seat.Dispatched || seat.Participant.ExitReason.HasValue)
                    continue;

                // A seat that already walked out of its own spawn room during the countdown gets nothing and is told nothing. Only on
                // a layout with rooms: with none (King of the Hill) every seat gets the plain window as before.
                var room = m.Layout?.SpawnRoomAreaFor(seat.TeamIndex);

                if (room != null && !room.Contains(players.GetCurrentPosition(seat.Id)))
                    continue;

                GrantSpawnProtection(m, seat, now);
            }
        }

        private PvpPlayerBinding Binding(MatchRuntime m, Seat seat, PvpMatchState state)
        {
            var d = m.Masks ?? PvpTunables.Defaults;

            // PvP Template Facets (TEMPLATES.md "Power-source table", rows marked Existing): every match is templated,
            // so the five arena masks are FORCED on whatever their pvp_arena_suppress_* settings say - a template build
            // is only comparable if no class ability, equipment mod, weapon mod or speed boon rides on top of it.
            // RestrictSpells stays the setting's: it is a spell rule, not a power mask. The template itself is the
            // seat's frozen copy, carried by every binding of the match.
            //
            // The overtime fields are the match's own snapshot (null / identity until overtime starts), so every
            // binding published after that - the Live-to-Live republish, a death's Closed, Resolving - carries them.
            //
            // Battlegrounds: the pen and Respawning are DERIVED from the seat on every build, never carried over from a
            // previous binding, so no republish (Countdown to Live, Resolving, another seat's death) can clear a
            // respawning player's flag and make them targetable or counted on the zone (Docs/Pvp/BATTLEGROUNDS.md
            // "Risks"). Both are null / false for every arena seat.
            //
            // TeamFellowship is derived the same way, from the match's own snapshot (MatchRuntime.TeamFellowship), never
            // from a previous binding: false for every arena match.
            //
            // The forced masks and the template apply to every templated mode - all of them unless a mode opts out
            // (PvpModeDefinition.Templated). An opted-out mode's binding keeps the pvp_arena_suppress_* dials, carries no
            // template, and is marked Untemplated so the live gateway places it without one.
            if (m.Mode.Templated)
                return new PvpPlayerBinding(m.Match, seat.TeamIndex, state, true, true, true, true, true, d.RestrictSpells,
                    m.OvertimeSinceUtc, m.OvertimeHealingMod, m.OvertimeRampPerMinute,
                    template: seat.Template, respawnPen: seat.PenPosition, respawning: seat.BgState != BgSeatState.None,
                    teamFellowship: m.TeamFellowship, protectedUntilUtc: seat.ProtectedUntilUtc, spawnRoom: SeatRoom(m, seat),
                    protectedInRoom: seat.ProtectedInRoom, protectionSeconds: m.BgDials?.SpawnProtectionSeconds ?? 0, protectionStamp: seat.ProtectionStamp);

            return new PvpPlayerBinding(m.Match, seat.TeamIndex, state, d.SuppressClassAbilities, d.SuppressEquipmentMods, d.SuppressWeaponMods, d.SuppressPickupBoons, d.SuppressTurnSpeed, d.RestrictSpells,
                m.OvertimeSinceUtc, m.OvertimeHealingMod, m.OvertimeRampPerMinute,
                respawnPen: seat.PenPosition, respawning: seat.BgState != BgSeatState.None, untemplated: true,
                teamFellowship: m.TeamFellowship, protectedUntilUtc: seat.ProtectedUntilUtc, spawnRoom: SeatRoom(m, seat),
                protectedInRoom: seat.ProtectedInRoom, protectionSeconds: m.BgDials?.SpawnProtectionSeconds ?? 0, protectionStamp: seat.ProtectionStamp);
        }

        // =====================================================================================================
        // Countdown and Live
        // =====================================================================================================

        private void BeginCountdown(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            SetState(m, PvpMatchState.Countdown, now, "every participant is in the instance");
            Publish(m, PvpMatchState.Countdown);
            FormTeamFellowships(m);

            // The objective mode's role line, once per team (Attack/Defend tells each side what it is playing for). Sent when the
            // countdown BEGINS, so players read it while the start gates still hold them in their rooms. Never again at Live.
            if (m.TickHandler is IObjectiveModeHandler roles)
            {
                foreach (var team in m.Match.Teams)
                {
                    var roleLine = roles.RoleLine(team.TeamIndex);

                    if (roleLine != null)
                        SendTeam(m, team.TeamIndex, roleLine);
                }
            }

            m.LastCountdownShown = int.MaxValue;
            AdvanceCountdown(m, now, dials);
        }

        private void AdvanceCountdown(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            if (PollWalkoutsAndResolve(m, now, dials))
                return;

            var remaining = dials.CountdownSeconds - (now - m.StateSinceUtc).TotalSeconds;

            if (remaining <= 0)
            {
                // Re-checked immediately before Live: a character added to the denylist after placement
                // must not enter Live combat. Cancels the whole match with no forfeit for anyone - by this
                // point everyone is already dispatched into the instance, so there is no seat-drop path
                // that leaves the rest of the match running; CancelMatch teleports every participant out
                // and requeues the eligible ones at the front, exactly like the accept-time ineligibility
                // path in BeginStaging.
                if (BootDenylistedBeforeLive(m, now))
                    return;

                SetState(m, PvpMatchState.Live, now, null);

                // King of the Hill: the doorways open at "go" (Docs/Pvp/BATTLEGROUNDS.md "Start gates"). The pen seals are not touched.
                RemoveStartGates(m, "match is Live");

                // Battlegrounds: every seat starts the match protected (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection"), under the same rule as a
                // respawn. Granted BEFORE the Live republish so the very first Live binding carries it. Arena matches have no dials snapshot.
                GrantMatchStartProtection(m, now);

                Publish(m, PvpMatchState.Live);
                SendActive(m, PvpArenaText.Start);

                // PvP Template Facets: the arena announcement names each fighter's template.
                SendActive(m, BuildLineupLine(m));

                // Attack/Defend: an attacker who left before Live left the crystals sized for them; reconciled once, here (ruling 12).
                ReconcileCrystalsToAttackers(m, "attacker(s) left before Live");

                m.TickHandler.OnLive(m.Match);
                return;
            }

            var whole = (int)Math.Ceiling(remaining);

            if (whole < m.LastCountdownShown)
            {
                m.LastCountdownShown = whole;
                SendActive(m, PvpArenaText.Fill(PvpArenaText.Countdown, ("seconds", whole)));
            }
        }

        /// <summary>
        /// PvP admission denylist, re-checked immediately before a match goes Live (Docs/Pvp/DESIGN.md
        /// "Joining the queue"). Returns TRUE and cancels the whole match - every mode, including 2v2 and FFA,
        /// where a partial removal is not possible once everyone is already placed and physically in the
        /// instance - with no forfeit for anyone, when any still-active seat is now denylisted. Writes the
        /// SAME unrated canceled-history row an admin cancel during Countdown/Live writes
        /// (<see cref="SaveCanceledHistory"/>), so this cancellation shows up in match history exactly like
        /// that one does. The denylisted character is not requeued; everyone else goes back to the FRONT of
        /// the queue, keeping their wait time - the same treatment BeginStaging gives an accepted player who
        /// turned out ineligible before placement.
        /// </summary>
        private bool BootDenylistedBeforeLive(MatchRuntime m, DateTime now)
        {
            var denylisted = m.Seats.Values
                .Where(s => !s.Participant.ExitReason.HasValue && PvpArenaDenylistTunables.IsDenylisted(s.Id))
                .Select(s => s.Id)
                .ToHashSet();

            if (denylisted.Count == 0)
                return false;

            log.Warn($"[PVP] match {m.Id}: {denylisted.Count} participant(s) denylisted before Live; canceling with no forfeit");

            var keep = m.Seats.Keys.Where(id => !denylisted.Contains(id)).ToList();

            foreach (var id in denylisted)
                players.Send(id, PvpArenaText.Denylisted);

            RequeueAtFront(m.RoomKey, UnitsKeeping(m, keep), now);
            SaveCanceledHistory(m, now);
            CancelMatch(m, now, $"{denylisted.Count} participant(s) denylisted before Live");
            return true;
        }

        private void AdvanceLive(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            // Objective (battleground) modes take their own branch: no overtime, no elimination-timeout cast, the mode's
            // own time limit. Every arena mode continues below exactly as before.
            if (m.Mode.IsObjective)
            {
                AdvanceObjectiveLive(m, now, dials);
                return;
            }

            m.TickHandler.OnTick(m.Match, now);

            // Being anywhere but the match instance while Live is a forfeit (every active participant was seen in the
            // instance before Countdown began, so the poll's arrival rule never spares anyone here).
            PollWalkouts(m, now);

            var limit = TimeLimitFor(m.ModeKey, dials);
            var survivorsShareFirst = m.Mode.TeamCountRange.Max > 2;
            var elapsed = (now - m.Match.LiveSinceUtc.Value).TotalSeconds;

            // A kill always wins first, in regulation and in overtime alike.
            var outcome = m.WinCondition.Evaluate(m.Match, now);

            // Overtime (Docs/Pvp/DESIGN.md "Overtime"): at the regulation limit with no winner, and overtime on,
            // the match goes to overtime instead of timing out. Entered once; its values are snapshotted then.
            if (outcome == null && !m.OvertimeSinceUtc.HasValue && elapsed >= limit && OvertimeOn(dials))
                EnterOvertime(m, now, limit, elapsed, survivorsShareFirst, dials);

            // The hard end: regulation alone, or regulation + overtime once overtime has started. The pure
            // ResolveTimeout is unchanged - it simply gets the larger limit. With overtime off this is exactly the
            // old path. ResolveTimeout lives on EliminationWinCondition, the one win condition every v1 mode shares.
            var hardLimit = m.OvertimeSinceUtc.HasValue ? m.OvertimeHardLimitSeconds : limit;

            outcome ??= (m.WinCondition as EliminationWinCondition)?.ResolveTimeout(m.Match, now, hardLimit, survivorsShareFirst, m.Mode.TimeoutRated);

            if (outcome != null)
            {
                Resolve(m, outcome, now, dials);
                return;
            }

            if (m.OvertimeSinceUtc.HasValue)
            {
                // One minute before the END of overtime; an overtime of a minute or less gets no separate warning,
                // the announcement already said how long it is.
                if (!m.WarnedOvertimeMinute && m.OvertimeSeconds > 60 && elapsed >= hardLimit - 60)
                {
                    m.WarnedOvertimeMinute = true;
                    SendActive(m, PvpArenaText.TimeWarning);
                }

                return;
            }

            // In regulation the one-minute warning fires only when the match will actually END at the limit, i.e.
            // overtime is off; with overtime on it moves to the end of overtime (above).
            if (!m.WarnedOneMinute && !OvertimeOn(dials) && limit > 60 && elapsed >= limit - 60)
            {
                m.WarnedOneMinute = true;
                SendActive(m, PvpArenaText.TimeWarning);
            }
        }

        /// <summary>
        /// Live for an objective mode (Docs/Pvp/BATTLEGROUNDS.md "Winning", "King of the Hill", "Respawn"): the pen state
        /// machine, the mode's tick handler (handed the runtime as its <see cref="IBattlegroundMatchContext"/>), the
        /// walkout poll, then the win condition, which owns the time limit snapshotted at formation. No overtime and no
        /// cast to <see cref="EliminationWinCondition"/>. The one-minute warning runs against the mode's own limit.
        /// </summary>
        private void AdvanceObjectiveLive(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            AdvanceRespawns(m, now);

            m.TickHandler.OnTick(m, now);

            PollWalkouts(m, now);

            // A crystal that fell since this tick's drain counts before the clock is checked (the score check runs first).
            if (m.Plan != null)
                DrainCrystalIntents(m);

            var outcome = m.WinCondition.Evaluate(m.Match, now);

            if (outcome != null)
            {
                Resolve(m, outcome, now, dials);
                return;
            }

            var limit = m.ObjectiveTimeLimitSeconds;
            var elapsed = (now - m.Match.LiveSinceUtc.Value).TotalSeconds;

            if (!m.WarnedOneMinute && limit > 60 && elapsed >= limit - 60)
            {
                m.WarnedOneMinute = true;
                SendActive(m, PvpArenaText.TimeWarning);
            }
        }

        /// <summary>
        /// A Live battleground death (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 2): the death and the kill are credited (the
        /// killer's team kill too, only against the other team; a drain death names the victim as their own killer, so
        /// it credits nobody), everyone hears it, the seat goes to DyingToPen with its respawn time, and the binding is
        /// republished with Respawning set. ExitReason stays null: the seat is still active. A second death intent while
        /// the seat is already respawning is logged and ignored.
        /// </summary>
        private void HandleBattlegroundDeath(MatchRuntime m, Seat seat, DeathDisposition.RespawnDisposition respawn, DateTime now, uint killerId, Position deathPosition = null)
        {
            if (seat.BgState != BgSeatState.None)
            {
                log.Info($"[PVP] match {m.Id}: second death of {seat.Name} while {seat.BgState}; ignored");
                return;
            }

            seat.Deaths++;

            string line;

            if (killerId != 0 && killerId != seat.Id && m.Seats.TryGetValue(killerId, out var killer))
            {
                killer.Kills++;

                if (killer.TeamIndex != seat.TeamIndex)
                {
                    m.Match.TeamKills.TryGetValue(killer.TeamIndex, out var kills);
                    m.Match.TeamKills[killer.TeamIndex] = kills + 1;

                    // Attack/Defend (Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal"): an enemy kill near the vulnerable crystal chips or
                    // heals it. Same counted-once point as the team kill above; suicides, teamkills and kills by no player never get here.
                    ApplyKillToCrystal(m, killer, seat, deathPosition);
                }

                line = BattlegroundText.Slain(seat.Name, killer.Name);
            }
            else
                line = BattlegroundText.SlainNoKiller(seat.Name);

            SendAll(m.Seats.Where(kvp => kvp.Value.Dispatched).Select(kvp => kvp.Key), line);

            seat.ProtectedUntilUtc = null;
            seat.ProtectedInRoom = false;
            seat.BgState = BgSeatState.DyingToPen;
            seat.RespawnAtUtc = now + respawn.Delay;
            seat.RespawnSpawnIndex = respawn.SpawnPointIndex;
            seat.RespawnDelay = respawn.Delay;
            seat.LastPenCountdownShown = int.MaxValue;
            ClearRespawnIssue(seat);

            players.PublishBinding(seat.Id, Binding(m, seat, m.Match.State));

            log.Info($"[PVP] match {m.Id}: {seat.Name} died ({(killerId != 0 && killerId != seat.Id ? $"killer 0x{killerId:X8}" : "no killer")}); to the pen, respawn at {seat.RespawnAtUtc:HH:mm:ss}; team kills [{string.Join(",", m.Match.TeamKills.OrderBy(k => k.Key).Select(k => k.Key + ":" + k.Value))}]");
        }

        /// <summary>
        /// The pen state machine, once per Live tick (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 5-6):
        ///   - DyingToPen to InPen once the player is standing in the instance and the death probe says the death
        ///     sequence is over; the countdown line is sent then.
        ///   - InPen back to DyingToPen if they died again in the pen (a /die): the respawn waits for the new sequence.
        ///   - InPen at RespawnAtUtc: the respawn is queued on the player and the seat goes to Respawning. The spawn is
        ///     the disposition's index, or the next team spawn round-robin for -1.
        ///   - Respawning: see <see cref="ConfirmOrRetryRespawn"/>. The binding keeps Respawning until the probe confirms.
        /// </summary>
        private void AdvanceRespawns(MatchRuntime m, DateTime now)
        {
            if (bgPlayers == null || m.Space == null)
                return;

            foreach (var seat in m.Seats.Values)
            {
                if (!seat.Dispatched || seat.Participant.ExitReason.HasValue || seat.BgState == BgSeatState.None)
                    continue;

                if (seat.BgState == BgSeatState.Respawning)
                {
                    ConfirmOrRetryRespawn(m, seat, now);
                    continue;
                }

                if (seat.BgState == BgSeatState.DyingToPen)
                {
                    if (players.GetPresence(seat.Id, m.Space.Instance) != PvpPresence.InInstance || bgPlayers.IsInDeathProcess(seat.Id))
                        continue;

                    seat.BgState = BgSeatState.InPen;

                    var arrival = Math.Max(0, (int)Math.Ceiling((seat.RespawnAtUtc.Value - now).TotalSeconds));
                    seat.LastPenCountdownShown = arrival;
                    players.Send(seat.Id, BattlegroundText.PenArrival(arrival));

                    log.Info($"[PVP] match {m.Id}: {seat.Name} is in the pen; respawn in {arrival} s");
                }
                else if (bgPlayers.IsInDeathProcess(seat.Id))
                {
                    seat.BgState = BgSeatState.DyingToPen;
                    log.Info($"[PVP] match {m.Id}: {seat.Name} died again in the pen; waiting for the death sequence");
                    continue;
                }

                var remaining = (seat.RespawnAtUtc.Value - now).TotalSeconds;

                if (remaining > 0)
                {
                    var whole = (int)Math.Ceiling(remaining);

                    foreach (var mark in PenCountdownMarks)
                    {
                        if (whole <= mark && seat.LastPenCountdownShown > mark)
                        {
                            seat.LastPenCountdownShown = whole;
                            players.Send(seat.Id, BattlegroundText.PenCountdown(whole));
                            break;
                        }
                    }

                    continue;
                }

                RespawnSeat(m, seat, now);
            }
        }

        /// <summary>The pen countdown lines after the arrival line, in seconds remaining.</summary>
        private static readonly int[] PenCountdownMarks = { 10, 5 };

        /// <summary>How close (metres, landblock-local) the probe must see the player to the target spawn to confirm a respawn.</summary>
        internal const double RespawnConfirmRadius = 8.0;

        /// <summary>How long a queued respawn may go unconfirmed, with the player alive and not in transit, before it is queued again.</summary>
        internal const double RespawnRetrySeconds = 3.0;

        private static void ClearRespawnIssue(Seat seat)
        {
            seat.RespawnTarget = null;
            seat.RespawnIssuedAtUtc = null;
            seat.RespawnAttempts = 0;
        }

        /// <summary>
        /// A queued respawn is confirmed only by what the probe sees, never assumed (the player's own guard may refuse it):
        ///   - dying or dead again (a /die between the probe and the queued action, or after): the seat re-enters the pen
        ///     cycle at DyingToPen with a fresh RespawnAtUtc, exactly as a new death would;
        ///   - alive, in the instance, out of transit and within <see cref="RespawnConfirmRadius"/> of the target spawn:
        ///     confirmed, the seat is alive (None) and the binding republished with Respawning false;
        ///   - alive and out of transit but not there <see cref="RespawnRetrySeconds"/> after the last queue: the same
        ///     respawn is queued again (a retry, not a coordinator teleport - the player's queue still does the move);
        ///   - otherwise (offline, in transit, inside the grace): wait.
        /// </summary>
        private void ConfirmOrRetryRespawn(MatchRuntime m, Seat seat, DateTime now)
        {
            var sample = bgPlayers.SampleZone(seat.Id, m.Space.Instance);

            if (bgPlayers.IsInDeathProcess(seat.Id) || (sample != null && sample.IsDead))
            {
                seat.BgState = BgSeatState.DyingToPen;
                seat.RespawnAtUtc = now + seat.RespawnDelay;
                seat.LastPenCountdownShown = int.MaxValue;
                log.Warn($"[PVP] match {m.Id}: {seat.Name} died before the respawn to {seat.RespawnTarget?.Label} landed (attempt {seat.RespawnAttempts}); back to the pen cycle, respawn at {seat.RespawnAtUtc:HH:mm:ss}");
                ClearRespawnIssue(seat);
                return;
            }

            if (sample == null || sample.IsTeleporting)
                return;

            var target = seat.RespawnTarget;

            if (sample.InInstance && target != null)
            {
                var dx = sample.X - target.X;
                var dy = sample.Y - target.Y;
                var dz = sample.Z - target.Z;

                if (dx * dx + dy * dy + dz * dz <= RespawnConfirmRadius * RespawnConfirmRadius)
                {
                    seat.BgState = BgSeatState.None;
                    seat.RespawnAtUtc = null;
                    log.Info($"[PVP] match {m.Id}: {seat.Name} respawn confirmed at {target.Label} (attempt {seat.RespawnAttempts})");
                    ClearRespawnIssue(seat);

                    // Spawn protection (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection"): the window opens HERE, when the probe sees the
                    // player at the spawn point, not when the teleport was issued, so the trip does not eat it. Only in a Live match,
                    // from the window snapshotted at formation (0 disables it). Arena seats never reach this path.
                    if (m.Match.State == PvpMatchState.Live)
                    {
                        GrantSpawnProtection(m, seat, now);

                        // Attack/Defend sequential mode: tell the returning player which crystal is vulnerable now.
                        var vulnerable = (m.TickHandler as IObjectiveModeHandler)?.VulnerableLine();

                        if (vulnerable != null)
                            players.Send(seat.Id, vulnerable);
                    }

                    players.PublishBinding(seat.Id, Binding(m, seat, m.Match.State));
                    return;
                }
            }

            if (seat.RespawnIssuedAtUtc.HasValue && (now - seat.RespawnIssuedAtUtc.Value).TotalSeconds < RespawnRetrySeconds)
                return;

            log.Warn($"[PVP] match {m.Id}: {seat.Name} is not at {target?.Label} {RespawnRetrySeconds} s after the respawn was queued (in instance {sample.InInstance}); queuing it again");
            IssueRespawn(m, seat, target, now);
        }

        /// <summary>Queues the respawn on the player (the player's own queue moves them) and holds the seat in Respawning.</summary>
        private void IssueRespawn(MatchRuntime m, Seat seat, PvpSpawnPoint point, DateTime now)
        {
            seat.BgState = BgSeatState.Respawning;
            seat.RespawnTarget = point;
            seat.RespawnIssuedAtUtc = now;
            seat.RespawnAttempts++;

            bgPlayers.Respawn(seat.Id, m.Id, ArenaSpawnPosition.Build(m.Map, point, m.Space.Instance));
        }

        private void RespawnSeat(MatchRuntime m, Seat seat, DateTime now)
        {
            var spawns = m.Layout?.SpawnsFor(seat.TeamIndex);
            PvpSpawnPoint point = null;

            if (spawns != null && seat.RespawnSpawnIndex >= 0 && seat.RespawnSpawnIndex < spawns.Count)
                point = spawns[seat.RespawnSpawnIndex];

            if (point == null)
            {
                m.RespawnCounters.TryGetValue(seat.TeamIndex, out var counter);
                m.RespawnCounters[seat.TeamIndex] = counter + 1;
                point = BattlegroundMapCatalog.RespawnSpawn(m.Layout, seat.TeamIndex, counter);
            }

            if (point == null)
            {
                // Cannot happen with a valid layout (AssignSpawns already seated this team). The seat stays InPen (still
                // Respawning on the binding: never marked alive while it has not left the pen) and tries again after
                // another respawn delay; the error names the bad layout.
                seat.RespawnAtUtc = now + seat.RespawnDelay;
                seat.LastPenCountdownShown = int.MaxValue;
                log.Error($"[PVP] match {m.Id}: no respawn point for team {seat.TeamIndex} on {m.Map.MapKey}; {seat.Name} stays in the pen");
                return;
            }

            // The binding is NOT republished here: the seat stays Respawning until ConfirmOrRetryRespawn sees the player
            // at the spawn, so a refused respawn can never leave a seat marked alive in the pen.
            IssueRespawn(m, seat, point, now);

            log.Info($"[PVP] match {m.Id}: {seat.Name} respawn queued to {point.Label}");
        }

        /// <summary>The zone sample the KOTH handler reads: every active, non-respawning, dispatched seat that is online.</summary>
        private IReadOnlyList<BattlegroundZoneSample> SampleZone(MatchRuntime m)
        {
            var result = new List<BattlegroundZoneSample>();

            if (bgPlayers == null || m.Space == null)
                return result;

            foreach (var seat in m.Seats.Values)
            {
                if (!seat.Dispatched || seat.Participant.ExitReason.HasValue || seat.BgState != BgSeatState.None)
                    continue;

                var sample = bgPlayers.SampleZone(seat.Id, m.Space.Instance);

                if (sample != null)
                    result.Add(sample with { CharacterId = seat.Id, TeamIndex = seat.TeamIndex, IpKey = seat.Participant.IpKey });
            }

            return result;
        }

        /// <summary>The Attack/Defend health sample (cosmetic): the placed crystals' health, or empty before they are placed or without the seam.</summary>
        private IReadOnlyList<CrystalHealth> SampleCrystals(MatchRuntime m)
        {
            if (objectiveSpaces == null || m.Space == null || m.Objectives == null)
                return Array.Empty<CrystalHealth>();

            try
            {
                return objectiveSpaces.SampleObjectives(m.Space, m.Objectives) ?? (IReadOnlyList<CrystalHealth>)Array.Empty<CrystalHealth>();
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: sampling the crystals threw", ex);
                return Array.Empty<CrystalHealth>();
            }
        }

        /// <summary>A line to every placed, still-active seat of one team (the defenders' under-attack alert).</summary>
        private void SendTeam(MatchRuntime m, int teamIndex, string text)
        {
            foreach (var seat in m.Seats.Values)
            {
                if (seat.Dispatched && !seat.Participant.ExitReason.HasValue && seat.TeamIndex == teamIndex)
                    players.Send(seat.Id, text);
            }
        }

        /// <summary>The KOTH drain, for an active, dispatched, non-respawning seat only, queued on the player.</summary>
        private void RequestDrain(MatchRuntime m, uint characterId, int health, int stamina, int mana, bool lethal)
        {
            if (bgPlayers == null || m.Space == null || !m.Seats.TryGetValue(characterId, out var seat))
                return;

            if (!seat.Dispatched || seat.Participant.ExitReason.HasValue || seat.BgState != BgSeatState.None)
                return;

            bgPlayers.DrainVitals(characterId, m.Id, m.Space.Instance, health, stamina, mana, lethal);
        }

        /// <summary>Overtime is on: the toggle is on and the duration is positive (0 or negative counts as off).</summary>
        private static bool OvertimeOn(PvpArenaDials dials) => dials.OvertimeEnabled && dials.OvertimeSeconds > 0;

        /// <summary>
        /// Starts overtime (Docs/Pvp/DESIGN.md "Overtime"): snapshots the duration, the healing factor and the damage
        /// ramp onto the match, so a setting edited mid-round never changes this round; republishes every active
        /// participant's Live binding carrying them (a Live-to-Live republish, which ReplacePvpBinding allows); and
        /// announces it. Overtime starts at the regulation limit as it stands at entry, or at the current whole
        /// second if the limit was lowered mid-round below the time already played, so overtime always runs its
        /// full length. The hard end is that start plus the overtime duration.
        /// </summary>
        private void EnterOvertime(MatchRuntime m, DateTime now, int limit, double elapsed, bool survivorsShareFirst, PvpArenaDials dials)
        {
            var regulationEnd = Math.Max(limit, (int)Math.Floor(elapsed));

            m.OvertimeSeconds = dials.OvertimeSeconds;
            m.OvertimeHardLimitSeconds = (int)Math.Min(int.MaxValue, (long)regulationEnd + dials.OvertimeSeconds);
            m.OvertimeSinceUtc = m.Match.LiveSinceUtc.Value.AddSeconds(regulationEnd);
            m.OvertimeHealingMod = dials.OvertimeHealingMod;
            m.OvertimeRampPerMinute = dials.OvertimeDamageRampPerMinute;

            log.Info($"[PVP] match {m.Id} ({m.ModeKey}): regulation ended at {regulationEnd} s with no winner; OVERTIME for {m.OvertimeSeconds} s (hard end {m.OvertimeHardLimitSeconds} s), healing mod {m.OvertimeHealingMod}, damage ramp {m.OvertimeRampPerMinute}/min");

            Publish(m, PvpMatchState.Live);

            var healingFactor = Rules.PvpRules.SanitizeMod(m.OvertimeHealingMod);
            var ramp = Rules.PvpRules.OvertimeRampMultiplier(m.OvertimeRampPerMinute, 60) - 1.0;

            SendActive(m, PvpArenaText.OvertimeAnnouncement(m.OvertimeSeconds, healingFactor, ramp, survivorsShareFirst));
        }

        /// <summary>
        /// Takes a participant out of the match: death or forfeit. Idempotent per participant. Everyone in the match
        /// hears it, and the participant is EXITED as soon as they are out, in every mode. They stay a participant for
        /// placement and rating at Resolving.
        ///
        /// Exactly ONE return trip per participant:
        ///   - Died: the death path owns the trip (ThreadSafeTeleportOnDeath sends a latched match death to the
        ///     stamped EphemeralRealmExitTo). The coordinator never teleports them. Their binding is republished as
        ///     Closed at once, so the damage gate refuses them. ExitPvpMatch runs once <see cref="ProcessDeathReturns"/>
        ///     sees them land outside the instance, which is after the death teleport completes.
        ///   - ForfeitLeft: already outside by their own recall or portal. Exited now, never teleported.
        ///   - ForfeitCommand: still standing in the arena. Exited and returned now.
        ///   - ForfeitLogout: offline. Player.LogOut runs ExitPvpMatch itself, and the 9075 login restore is the backstop.
        /// </summary>
        private void MarkOut(MatchRuntime m, Seat seat, ParticipantExit how, DateTime now, uint killerId)
        {
            if (seat.Participant.ExitReason.HasValue)
                return;

            m.WinCondition.OnParticipantOut(m.Match, seat.Participant, how);

            RescaleCrystalsForAttackers(m, seat);

            string line;

            if (how == ParticipantExit.Died)
            {
                seat.Deaths++;

                if (killerId != 0 && killerId != seat.Id && m.Seats.TryGetValue(killerId, out var killer))
                {
                    killer.Kills++;
                    line = PvpArenaText.Fill(PvpArenaText.Elimination, ("victim", seat.Name), ("killer", killer.Name));
                }
                else
                    line = PvpArenaText.Fill(PvpArenaText.EliminationNoKiller, ("victim", seat.Name));
            }
            else
                line = PvpArenaText.Fill(PvpArenaText.Forfeit, ("name", seat.Name));

            log.Info($"[PVP] match {m.Id}: {seat.Name} out ({how}{(killerId != 0 ? $", killer 0x{killerId:X8}" : "")}) during {m.Match.State}");

            // A walkout or logout after being placed but before Live is a dodge: locked out like a decliner.
            if ((m.Match.State == PvpMatchState.Staging || m.Match.State == PvpMatchState.Countdown)
                && (how == ParticipantExit.ForfeitLeft || how == ParticipantExit.ForfeitLogout))
                LockOut(seat.Id, now, Dials(), $"left match {m.Id} during {m.Match.State}");

            SendAll(m.Seats.Where(kvp => kvp.Value.Dispatched).Select(kvp => kvp.Key), line);

            switch (how)
            {
                case ParticipantExit.Died:
                    seat.AwaitingDeathReturn = true;
                    players.Send(seat.Id, PvpArenaText.YouWereEliminated);
                    players.PublishBinding(seat.Id, Binding(m, seat, PvpMatchState.Closed));
                    break;

                case ParticipantExit.ForfeitLeft:
                    players.Send(seat.Id, PvpArenaText.LeftTheArena);
                    ExitSeat(m, seat, "left the arena", teleport: false);
                    break;

                case ParticipantExit.ForfeitCommand:
                    players.Send(seat.Id, PvpArenaText.LeftTheArena);
                    ExitSeat(m, seat, "forfeit command", teleport: true);
                    break;

                case ParticipantExit.ForfeitLogout:
                    ExitSeat(m, seat, "logout forfeit", teleport: false);
                    break;
            }
        }

        /// <summary>
        /// Attack/Defend ruling 12 (Docs/Pvp/ATTACK-DEFEND.md): when an attacker leaves a Live match, the crystals are reconciled to the
        /// attackers still in it (<see cref="ReconcileCrystalsToAttackers"/>). A defender leaving changes nothing, and a leave before
        /// Live is reconciled once, at the Live transition.
        /// </summary>
        private void RescaleCrystalsForAttackers(MatchRuntime m, Seat seat)
        {
            if (m.Match.State != PvpMatchState.Live || seat.TeamIndex != CrystalWinCondition.AttackerTeam)
                return;

            ReconcileCrystalsToAttackers(m, $"{seat.Name} (attacker) left");
        }

        /// <summary>
        /// Attack/Defend kill chip and heal (Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal"): an enemy kill whose victim died near the
        /// vulnerable crystal queues a chip (attacker killed a defender) or a heal (defender killed an attacker) on it, decided by
        /// <see cref="CrystalKillRules.Decide"/> from the match's formation dials snapshot and applied by the space seam on the crystal's
        /// landblock thread. Called only from <see cref="HandleBattlegroundDeath"/>, which runs only for a Live match, once per death,
        /// and only for a killer seat on the other team. Nothing for a match with no plan (King of the Hill) or no placed crystals. Best
        /// effort: a throwing seam is logged and the match carries on.
        /// </summary>
        private void ApplyKillToCrystal(MatchRuntime m, Seat killer, Seat victim, Position deathPosition)
        {
            if (m.Plan == null || objectiveSpaces == null || m.Space == null || m.Objectives == null || m.TickHandler is not IObjectiveModeHandler handler)
                return;

            // The handler's destroyed set is the authority on which crystals still stand (any-order play must skip a fallen one).
            var effect = CrystalKillRules.Decide(m.Plan, m.BgDials, m.Space.LandblockId, m.Space.Instance, killer.TeamIndex, victim.TeamIndex, deathPosition, handler.IsObjectiveStanding);

            if (effect == null)
                return;

            try
            {
                objectiveSpaces.AdjustObjective(m.Space, m.Objectives, effect.Value, killer.Id);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: the kill {(effect.Value.IsChip ? "chip" : "heal")} on crystal {effect.Value.CrystalIndex} threw", ex);
                return;
            }

            log.Info($"[PVP] match {m.Id}: {killer.Name} killed {victim.Name} near crystal {effect.Value.CrystalIndex}; {(effect.Value.IsChip ? "chip" : "heal")} {effect.Value.Percent}% queued");
        }

        /// <summary>
        /// Every standing crystal's current and maximum health scale by (attackers still in the match / attackers the crystals are
        /// currently sized for), and the match then records the new count as what the crystals are sized for, so a later leave scales
        /// from this result rather than compounding from the formation count. Nothing happens for a match with no plan, when no attacker
        /// has left since the last sizing, or when none is left (the elimination check ends the match). Best effort: a throwing seam
        /// is logged and the match carries on.
        /// </summary>
        private void ReconcileCrystalsToAttackers(MatchRuntime m, string reason)
        {
            if (m.Plan == null || objectiveSpaces == null)
                return;

            var attackers = m.Match.Teams.FirstOrDefault(t => t.TeamIndex == CrystalWinCondition.AttackerTeam);
            var current = attackers?.Members.Count(p => !p.ExitReason.HasValue) ?? 0;
            var sizedFor = m.CrystalsSizedForAttackers;

            if (current <= 0 || current >= sizedFor)
                return;

            try
            {
                objectiveSpaces.ScaleObjectives(m.Space, m.Objectives, sizedFor, current);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: rescaling the crystals from {sizedFor} to {current} attacker(s) threw", ex);
                return;
            }

            m.CrystalsSizedForAttackers = current;
            log.Info($"[PVP] match {m.Id}: {reason}; crystals rescaled from {sizedFor} to {current} attacker(s)");
        }
        /// <summary>
        /// Exits a participant once, and teleports them to the stamped exit only when <paramref name="teleport"/> is
        /// set. Nothing is done twice: an exited seat is skipped by every later exit path (post-match, cancel).
        /// </summary>
        private void ExitSeat(MatchRuntime m, Seat seat, string context, bool teleport)
        {
            // FIRST, before every early return below (the dying-to-pen one included): every coordinator exit releases the
            // seat from its team fellowship. Idempotent per seat.
            ReleaseTeamFellowship(m, seat, context);

            if (seat.Exited)
                return;

            seat.Exited = true;

            // Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 8): a seat still dying on its way to the pen is NEVER
            // teleported by the coordinator, whatever the caller asked - ReturnToExit would clear the stamp the death
            // teleport needs and drop the player at Sanctuary. It is exited without a teleport and awaits its death
            // return; the death teleport (binding no longer Live), the completion backstop, or the player-side check in
            // ExitMatchFromPen takes them home. An InPen seat is alive in the instance and falls through to the normal exit.
            // An InPen or Respawning seat whose player is dying again (a /die not yet seen by the pen probe) is the same case.
            if (bgPlayers != null && (seat.BgState == BgSeatState.DyingToPen || (seat.BgState != BgSeatState.None && bgPlayers.IsInDeathProcess(seat.Id))))
            {
                seat.AwaitingDeathReturn = true;
                bgPlayers.ExitMatchFromPen(seat.Id, context);
                log.Info($"[PVP] match {m.Id}: {seat.Name} exited while dying ({context}); no coordinator teleport, the death path returns them");
                return;
            }

            players.ExitMatch(seat.Id, context);

            if (teleport)
                players.ReturnToExit(seat.Id, seat.ExitTo, m.Space?.Instance ?? 0);

            log.Info($"[PVP] match {m.Id}: {seat.Name} exited ({context}{(teleport ? ", returned to the stamped exit" : "")})");
        }

        /// <summary>
        /// Exits every participant who died, once the death path has carried them out of the instance. Offline counts
        /// as done (the logout path ran ExitPvpMatch). In the instance or in transit means the death chain is still
        /// running: wait. The coordinator never teleports a dead participant, so the death return is the only trip.
        /// </summary>
        private void ProcessDeathReturns(MatchRuntime m)
        {
            if (m.Space == null)
                return;

            foreach (var seat in m.Seats.Values)
            {
                if (!seat.AwaitingDeathReturn || seat.Exited)
                    continue;

                var presence = players.GetPresence(seat.Id, m.Space.Instance);

                if (presence == PvpPresence.Offline)
                {
                    ReleaseTeamFellowship(m, seat, "offline after dying");
                    seat.Exited = true;
                    log.Info($"[PVP] match {m.Id}: {seat.Name} went offline after dying; the logout path exits them");
                }
                else if (presence == PvpPresence.Elsewhere)
                    ExitSeat(m, seat, "eliminated, death return complete", teleport: false);
            }
        }

        // =====================================================================================================
        // Battleground team fellowships (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships")
        // =====================================================================================================

        /// <summary>
        /// At formation: whether this match builds team fellowships, and whether a release restores the outside fellowship.
        /// Off unless the gateway implements <see cref="IPvpTeamFellowshipGateway"/>, the mode's policy is TeamFellowship
        /// (never an arena mode), pvp_bg_team_fellowship is on and no team is larger than the fellowship cap (both teams
        /// are formed or both are skipped).
        /// </summary>
        private void DecideTeamFellowships(MatchRuntime m, BattlegroundDials bg)
        {
            m.TeamFellowship = false;

            if (teamFellowships == null || m.Mode.Fellowship != TeamFellowshipPolicy.TeamFellowship)
                return;

            var d = bg ?? BgDials();
            int cap;

            try
            {
                cap = teamFellowships.FellowshipCap;
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: reading the fellowship cap threw; no team fellowships for this match", ex);
                return;
            }

            var largest = m.Match.Teams.Count > 0 ? m.Match.Teams.Max(team => team.Members.Count) : 0;
            var decision = TeamFellowshipPlan.Decide(m.Mode.Fellowship, d.TeamFellowship, largest, cap);

            m.TeamFellowship = decision == TeamFellowshipPlanDecision.Form;
            m.TeamFellowshipRestore = d.TeamFellowshipRestore;

            if (decision == TeamFellowshipPlanDecision.SkipOverCap)
                log.Warn($"[PVP] match {m.Id} ({m.ModeKey}): a team of {largest} is larger than the fellowship cap of {cap}; NEITHER team gets a team fellowship");
            else
                log.Info($"[PVP] match {m.Id} ({m.ModeKey}): team fellowships {decision}{(m.TeamFellowship ? $", restore on exit {(m.TeamFellowshipRestore ? "on" : "off")}" : "")}");
        }

        /// <summary>At Countdown: each team's still-active seats, in team order, are put in that team's fellowship.</summary>
        private void FormTeamFellowships(MatchRuntime m)
        {
            if (!m.TeamFellowship || teamFellowships == null || m.TeamFellowshipFormed)
                return;

            m.TeamFellowshipFormed = true;

            // The team's name comes from the mode (Docs/Pvp/ATTACK-DEFEND.md "Player-facing names"): "West Team" for King of the Hill as
            // before, "Attackers Team" / "Defenders Team" for Attack/Defend.
            var modeHandler = m.TickHandler as IObjectiveModeHandler;

            var teams = m.Match.Teams
                .Select(team => new PvpTeamFellowshipTeam(team.TeamIndex, BattlegroundText.TeamFellowshipName(modeHandler?.TeamName(team.TeamIndex) ?? BattlegroundText.TeamName(team.TeamIndex)),
                    team.Members.Where(p => m.Seats.TryGetValue(p.CharacterId, out var s) && IsActiveForTeamFellowship(s)).Select(p => p.CharacterId).ToList()))
                .ToList();

            try
            {
                teamFellowships.Form(m.Id, teams);
                log.Info($"[PVP] match {m.Id}: team fellowships formed: {string.Join("; ", teams.Select(team => $"{team.Name} [{string.Join(", ", team.MemberIds.Select(id => m.Seats[id].Name))}]"))}");
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: forming the team fellowships threw", ex);
            }
        }

        private static bool IsActiveForTeamFellowship(Seat seat) => seat.Dispatched && !seat.Exited && !seat.Participant.ExitReason.HasValue;

        /// <summary>A seat leaves the match: released from its team fellowship (and restored, per the match's snapshot) once.</summary>
        private void ReleaseTeamFellowship(MatchRuntime m, Seat seat, string context)
        {
            if (!m.TeamFellowship || teamFellowships == null || seat.TeamFellowshipReleased)
                return;

            seat.TeamFellowshipReleased = true;

            try
            {
                teamFellowships.Release(m.Id, seat.Id, m.TeamFellowshipRestore);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: releasing {seat.Name} from the team fellowship ({context}) threw", ex);
            }
        }

        /// <summary>Closed or Canceled: the match's team fellowships are dissolved, exactly once.</summary>
        private void DisposeTeamFellowships(MatchRuntime m)
        {
            if (!m.TeamFellowship || teamFellowships == null || m.TeamFellowshipDisposed)
                return;

            m.TeamFellowshipDisposed = true;

            try
            {
                teamFellowships.Dispose(m.Id);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: disposing the team fellowships threw", ex);
            }
        }

        /// <summary>Rate-limited backstop: any team fellowship whose match is no longer active is dissolved.</summary>
        private void RunTeamFellowshipSweep(DateTime now)
        {
            if (teamFellowships == null)
                return;

            if (lastTeamFellowshipSweepUtc.HasValue && (now - lastTeamFellowshipSweepUtc.Value).TotalSeconds < TeamFellowshipSweepIntervalSeconds)
                return;

            lastTeamFellowshipSweepUtc = now;

            var active = matches.Where(x => x.Match.State != PvpMatchState.Closed && x.Match.State != PvpMatchState.Canceled).Select(x => x.Id).ToHashSet();

            try
            {
                teamFellowships.Sweep(active);
            }
            catch (Exception ex)
            {
                log.Error("[PVP] the team fellowship sweep threw", ex);
            }
        }

        // =====================================================================================================
        // Resolving and Closed
        // =====================================================================================================

        /// <summary>
        /// The PvP Discord feed's one-line match result: mode, winners, losers, no reward text. Winner/loser per
        /// seat uses the same (won, forfeited) logic as the participant record below, so the feed never
        /// disagrees with what gets written to the rating ladder. A draw lists everyone as a draw instead of
        /// splitting winners/losers.
        /// </summary>
        /// <summary>
        /// A participant as every results line names them: "Name (Template)" (PvP Template Facets: the template is
        /// shown beside each name on the results, the arena announcement and /top). Just the name when no template was
        /// frozen onto the seat, which never happens for a dispatched seat.
        /// </summary>
        private static string NameWithTemplate(Seat seat) => seat.TemplateLabel != null ? $"{seat.Name} ({seat.TemplateLabel})" : seat.Name;

        /// <summary>
        /// The arena announcement sent to every fighter when the match goes Live: each team's members with their
        /// templates, teams separated by "vs". FFA lists everyone.
        /// </summary>
        private static string BuildLineupLine(MatchRuntime m)
        {
            var active = m.Seats.Values.Where(s => s.Dispatched && !s.Participant.ExitReason.HasValue).ToList();

            string lineup;

            if (m.Mode.TeamCountRange.Max > 2)
                lineup = string.Join(", ", active.OrderBy(s => s.TeamIndex).Select(NameWithTemplate));
            else
                lineup = string.Join(" vs ", active.GroupBy(s => s.TeamIndex).OrderBy(g => g.Key).Select(g => string.Join(" and ", g.Select(NameWithTemplate))));

            return PvpArenaText.Fill(PvpArenaText.Lineup, ("mode", PvpArenaText.ModeLabel(m.ModeKey)), ("lineup", lineup));
        }

        private static string BuildMatchResultDiscordMessage(MatchRuntime m, MatchOutcome outcome)
        {
            var modeLabel = PvpArenaText.ModeLabel(m.ModeKey);

            if (outcome.IsDraw)
            {
                var everyone = string.Join(", ", m.Seats.Values.Select(NameWithTemplate));
                return $"[Arena] {modeLabel} match ended in a draw: {everyone}.";
            }

            var winners = new List<string>();
            var losers = new List<string>();

            foreach (var seat in m.Seats.Values)
            {
                var forfeited = IsForfeit(seat.Participant.ExitReason);
                var won = outcome.WinningTeams.Contains(seat.TeamIndex) && !forfeited;

                (won ? winners : losers).Add(NameWithTemplate(seat));
            }

            var winnersText = winners.Count > 0 ? string.Join(", ", winners) : "(none)";
            var losersText = losers.Count > 0 ? string.Join(", ", losers) : "(none)";

            return $"[Arena] {modeLabel} match: {winnersText} defeated {losersText}.";
        }

        private void Resolve(MatchRuntime m, MatchOutcome outcome, DateTime now, PvpArenaDials dials)
        {
            var twoTeam = m.Mode.TeamCountRange.Max == 2;

            // A double knockout (every remaining team out in the same tick, no winner) in a two-team mode is an UNRATED
            // draw (owner ruling on #1399), consistent with a timeout draw.
            var doubleKnockout = twoTeam && !outcome.IsDraw && outcome.WinningTeams.Count == 0 && outcome.Reason == EndReason.Elimination && outcome.Placements.Distinct().Count() == 1;

            if (doubleKnockout)
                outcome = outcome with { IsDraw = true, Rated = false };
            else if (twoTeam && !outcome.IsDraw && outcome.WinningTeams.Count == 0 && outcome.Placements.Distinct().Count() > 1)
            {
                // No team left standing but the placements differ (one side forfeited in the same tick the other side
                // died): the better-placed side is the winner, rather than both sides being scored as losers.
                var best = outcome.Placements.Min();
                outcome = outcome with { WinningTeams = new HashSet<int>(Enumerable.Range(0, outcome.Placements.Count).Where(i => outcome.Placements[i] == best)) };
            }

            var sameIpOpponents = twoTeam && dials.BlockSameIp == false && HasCrossTeamSameIp(m);
            var ratingsAvailable = Ratings.IsAvailable;
            var rated = outcome.Rated && ratingsAvailable && !sameIpOpponents;

            if (outcome.Rated && !ratingsAvailable)
                log.Error($"[PVP] match {m.Id}: RATINGS UNAVAILABLE ({Ratings.State}) - resolving UNRATED; no rating row will be written");

            m.Outcome = outcome;
            m.Rated = rated;
            m.ResolvedAtUtc = now;

            SetState(m, PvpMatchState.Resolving, now, $"{outcome.Reason}, winners [{string.Join(",", outcome.WinningTeams)}], placements [{string.Join(",", outcome.Placements)}], draw {outcome.IsDraw}, rated {rated}{(sameIpOpponents ? " (same-IP opponents)" : "")}");


            // The match is decided: a pre-Live resolve (a walkout in Staging or Countdown) must not wall the survivors in until CloseNow.
            // CloseNow keeps its own call as an idempotent backstop.
            RemoveStartGates(m, "match resolved");

            Publish(m, PvpMatchState.Resolving);

            if (m.Mode.IsObjective)
            {
                // Battlegrounds: their own result line (never the FFA timeout text), and the team score to the log only.
                log.Info($"[PVP] match {m.Id} ({m.ModeKey}): final score [{string.Join(",", m.Match.ScoreBoard.OrderBy(s => s.Key).Select(s => s.Key + ":" + s.Value))}], team kills [{string.Join(",", m.Match.TeamKills.OrderBy(k => k.Key).Select(k => k.Key + ":" + k.Value))}], target {m.ScoreTarget}");

                // The mode's own result line (Docs/Pvp/ATTACK-DEFEND.md "Mode seam"): King of the Hill returns exactly the score win,
                // timeout draw and timeout win it always sent; Attack/Defend its attackers' and defenders' lines; null for anything else.
                var objectiveLine = (m.TickHandler as IObjectiveModeHandler)?.ResultLine(outcome, m.Match.ScoreBoard);

                if (objectiveLine != null)
                    SendAll(m.PlacedIds, objectiveLine);
            }
            else if (outcome.Reason == EndReason.Timeout)
                SendAll(m.PlacedIds, outcome.IsDraw ? PvpArenaText.TimeUpDraw : PvpArenaText.TimeUpFfa);

            // Ratings: decay-on-read inputs, each player's own K, the forfeiter's S = 0 in a two-team mode.
            var views = m.Seats.Values.ToDictionary(s => s.Id, s => Ratings.Read(s.Id, m.Mode.LadderKey, now, dials));
            IReadOnlyDictionary<uint, int> deltas = null;

            if (rated)
            {
                var ratedTeams = m.Match.Teams.Select(t => new RatedTeam(t.TeamIndex, t.Members.Select(p =>
                {
                    var v = views[p.CharacterId];
                    var k = EloRating.SelectK(v.Games, dials.RatingProvisionalGames, dials.RatingKProvisional, dials.RatingKEstablished);
                    return new RatedPlayer(p.CharacterId, v.Rating, k, p.IpKey, ForceZeroScore: twoTeam && IsForfeit(p.ExitReason));
                }).ToList())).ToList();

                deltas = m.Mode.Rating.Deltas(ratedTeams, outcome);
            }

            var participantRecords = new List<PvpMatchParticipantRecord>();
            var upserts = new List<PvpRatingRecord>();
            // Labelled by ROOM: an arena room is its mode key (unchanged), and every battleground mode shares one
            // ladder, so its rating line reads "Battleground rating", never the mode's own name.
            var ladderLabel = PvpArenaText.ModeLabel(m.RoomKey);
            var total = m.Match.Teams.Count;

            foreach (var seat in m.Seats.Values)
            {
                var placement = outcome.Placements[seat.TeamIndex];
                var forfeited = IsForfeit(seat.Participant.ExitReason);
                var won = !outcome.IsDraw && outcome.WinningTeams.Contains(seat.TeamIndex) && !forfeited;
                var result = outcome.IsDraw ? "draw" : won ? "win" : "loss";
                var v = views[seat.Id];

                int? before = null, after = null;

                if (rated && deltas != null && deltas.TryGetValue(seat.Id, out var delta))
                {
                    before = v.Rating;
                    after = v.Rating + delta;

                    var row = new PvpRatingRecord
                    {
                        CharacterId = seat.Id,
                        Ladder = m.Mode.LadderKey,
                        CharacterName = seat.Name,
                        Rating = after.Value,
                        Games = v.Games + 1,
                        Wins = v.Wins + (result == "win" ? 1 : 0),
                        Losses = v.Losses + (result == "loss" ? 1 : 0),
                        Draws = v.Draws + (result == "draw" ? 1 : 0),
                        Peak = Math.Max(v.HasRecord ? v.Peak : after.Value, after.Value),
                        LastMatchAt = now,
                    };

                    upserts.Add(row);
                    Ratings.Apply(row);
                }

                participantRecords.Add(new PvpMatchParticipantRecord
                {
                    CharacterId = seat.Id,
                    CharacterName = seat.Name,
                    Team = seat.TeamIndex,
                    Placement = placement,
                    Result = result,
                    RatingBefore = before,
                    RatingAfter = after,
                    Kills = seat.Kills,
                    Deaths = seat.Deaths,
                    ForfeitReason = ForfeitCode(seat.Participant.ExitReason),
                });

                if (!seat.Dispatched)
                    continue;

                // Result lines. Sent to everyone placed; an offline player simply does not receive them.
                if (!twoTeam)
                    players.Send(seat.Id, PvpArenaText.Fill(PvpArenaText.FfaPlacement, ("placement", placement), ("total", total)));
                else if (doubleKnockout)
                    players.Send(seat.Id, PvpArenaText.DoubleKnockoutDraw);
                else if (!outcome.IsDraw)
                    players.Send(seat.Id, won ? PvpArenaText.Win : PvpArenaText.Loss);

                if (before.HasValue)
                    players.Send(seat.Id, PvpArenaText.Fill(PvpArenaText.RatingChange, ("ladder", ladderLabel), ("before", before.Value), ("after", after.Value), ("delta", (after.Value - before.Value).ToString("+0;-0;+0"))));
                else
                    players.Send(seat.Id, PvpArenaText.Unrated);
            }

            var matchRecord = new PvpMatchRecord
            {
                Mode = m.ModeKey,
                Ladder = m.Mode.LadderKey,
                Map = m.Map.MapKey,
                StartedAt = m.Match.LiveSinceUtc,
                EndedAt = now,
                Outcome = outcome.IsDraw ? "draw" : outcome.WinningTeams.Count == 1 ? "win" : outcome.WinningTeams.Count > 1 ? "shared" : "none",
                EndReason = outcome.Reason.ToString(),
                Rated = rated,
            };

            var matchId = m.Id;

            // PvP Template Facets: each participant's frozen template, stamped onto their participant row once the
            // match row is saved (the stamp needs the pvp_match id the save hands back), and remembered for /top.
            QueueTemplateStamps(m, m.Seats.Values);

            foreach (var seat in m.Seats.Values.Where(s => s.Template != null))
                latestTemplateByLadder[(seat.Id, m.Mode.LadderKey)] = seat.Template.Key;

            // The single save. The callback runs off the world thread: it only enqueues a plain record. This
            // MUST happen before the Discord feed below - the save is the one thing here that must never be
            // skipped, and a relay failure must never be able to prevent it.
            SavesQueued++;
            sink.SaveMatchResult(matchRecord, participantRecords, upserts, (saveResult, dbId) => dbResults.Enqueue(DbResult.SaveCompleted(matchId, saveResult, dbId)));

            log.Info($"[PVP] match {m.Id}: result queued for save ({participantRecords.Count} participant(s), {upserts.Count} rating upsert(s)); {string.Join("; ", m.Seats.Values.Select(s => participantRecords.First(p => p.CharacterId == s.Id)).Select(p => $"{p.CharacterName} [{m.Seats[p.CharacterId].Template?.Key ?? "-"} v{m.Seats[p.CharacterId].Template?.Version ?? 0}] {p.Result} #{p.Placement} {p.RatingBefore?.ToString() ?? "-"}->{p.RatingAfter?.ToString() ?? "-"}{(p.ForfeitReason != null ? " forfeit:" + p.ForfeitReason : "")}"))}{(m.BackstopMoves > 0 ? $"; TEMPLATE BACKSTOP moved {m.BackstopMoves} personal item(s) during this match ({string.Join(", ", m.Seats.Values.Where(s => s.BackstopMoves > 0).Select(s => s.Name + " x" + s.BackstopMoves))})" : "")}");

            // PvP Template Facets: the results line, naming each player's template, to everyone placed. The same text
            // the Discord feed carries below, so the two never disagree.
            var resultLine = BuildMatchResultDiscordMessage(m, outcome);
            SendAll(m.PlacedIds, resultLine);

            // The PvP Discord feed: one post per finished match, mode/winners/losers, no reward text. A no-op
            // (Managers.DiscordRelayManager.QueuePvp) unless discord_relay_enabled and discord_webhook_url_pvp
            // are both configured. Deliberately AFTER the save above and in its own try/catch: a relay read
            // (e.g. discord_relay_enabled throwing in an uncached-key environment) must never be able to
            // prevent the match result from being saved. QueuePvpForTest is a test-only seam (defaults to the
            // real DiscordRelayManager.QueuePvp) - DiscordRelayManager has no injectable failure point of its
            // own, so a test proving this try/catch actually protects the save needs a way to make THIS call
            // throw deterministically.
            try
            {
                (QueuePvpForTest ?? ACE.Server.Managers.DiscordRelayManager.QueuePvp)(resultLine);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: the Discord feed post failed; the match result above was already saved", ex);
            }

            // The arena Blood payout (Docs/Pvp/DESIGN.md "Rewards"). AFTER the save, for the same reason as the Discord
            // post: nothing in it may ever stand between a decided match and its saved result. Never in the Discord
            // text either - the feed carries no reward text.
            if (m.Mode.PaysBlood)
                PayArenaBlood(m, outcome, dials, twoTeam, sameIpOpponents);
            else if (m.Mode.IsObjective)
                PayBattlegroundMarks(m, outcome, dials, twoTeam, sameIpOpponents);
            else
            {
                // A mode that pays neither Blood nor battleground Marks: nothing to pay, and the match is marked paid
                // so no later path can.
                m.BloodPaid = true;
                log.Info($"[PVP] match {m.Id}: no Blood paid ({m.ModeKey} does not pay)");
            }
        }

        /// <summary>
        /// Pays each participant's Mark of the Hopeslayer for a resolved BATTLEGROUND match (Docs/Pvp/BATTLEGROUNDS.md
        /// "Rewards"; reverses ruling 12 on 2026-10-08), exactly once. Shares <see cref="PayArenaBlood"/>'s guard
        /// (<see cref="MatchRuntime.BloodPaid"/>, set BEFORE any grant), its master switch (pvp_arena_blood_enabled) and
        /// its exclusions: a match that never went Live, same-IP opposing teams and a forfeiter pay 0. A battleground
        /// draw pays the draw amount. Amounts come from the pure <see cref="PvpArenaRewards.BgMarksFor"/>; the grant
        /// carries the battleground ledger, so the battleground daily cap (applied at grant time on the player's thread)
        /// never consumes the arena's. Each seat has its own try/catch.
        /// </summary>
        private void PayBattlegroundMarks(MatchRuntime m, MatchOutcome outcome, PvpArenaDials dials, bool twoTeam, bool sameIpOpponents)
        {
            if (m.BloodPaid)
            {
                log.Warn($"[PVP] match {m.Id}: Marks already paid; not paying again");
                return;
            }

            m.BloodPaid = true;

            var wentLive = m.Match.LiveSinceUtc.HasValue;

            if (!dials.BloodEnabled || !wentLive || sameIpOpponents)
            {
                log.Info($"[PVP] match {m.Id}: no Marks paid ({(!dials.BloodEnabled ? "pvp_arena_blood_enabled is off" : !wentLive ? "the match never went Live" : "same-IP opponents")})");
                return;
            }

            var bg = BgDials();
            var grant = new PvpBloodGrant(m.Id, bg.MarksDailyCap, dials.BloodResetTimezone, dials.BloodResetHour, PvpBloodLedgerKind.Battleground);
            var paid = new List<string>();

            foreach (var seat in m.Seats.Values)
            {
                try
                {
                    var forfeited = IsForfeit(seat.Participant.ExitReason);
                    var won = !outcome.IsDraw && outcome.WinningTeams.Contains(seat.TeamIndex) && !forfeited;
                    var result = PvpArenaRewards.ResultFor(outcome.IsDraw, twoTeam, won, outcome.Placements[seat.TeamIndex]);
                    var amount = PvpArenaRewards.BgMarksFor(result, forfeited, wentLive, sameIpOpponents, dials.BloodEnabled, bg);

                    paid.Add($"{seat.Name} {(forfeited ? "forfeit" : result.ToString().ToLowerInvariant())} {amount}");

                    if (amount > 0)
                        players.GrantArenaBlood(seat.Id, amount, grant);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] match {m.Id}: the Marks grant for {seat.Name} (0x{seat.Id:X8}) threw; nobody is paid twice for it", ex);
                }
            }

            log.Info($"[PVP] match {m.Id}: Marks granted before the battleground daily cap: {string.Join("; ", paid)}");
        }

        /// <summary>
        /// Pays each participant's Blood for a resolved match, exactly once (Docs/Pvp/DESIGN.md "Rewards"). Guarded by
        /// the match's own <see cref="MatchRuntime.BloodPaid"/>, set BEFORE any grant goes out, so no later path - a save
        /// result of any kind, an admin cancel during Resolving, a second resolve - can ever pay the match again; a grant
        /// that throws part way leaves the rest unpaid rather than risking a double payment. Amounts come from the pure
        /// <see cref="PvpArenaRewards.BloodFor"/>; the daily cap is NOT applied here - the gateway applies it at grant
        /// time against the live character. Each seat has its own try/catch: one failed grant never blocks the others.
        /// </summary>
        private void PayArenaBlood(MatchRuntime m, MatchOutcome outcome, PvpArenaDials dials, bool twoTeam, bool sameIpOpponents)
        {
            if (m.BloodPaid)
            {
                log.Warn($"[PVP] match {m.Id}: Blood already paid; not paying again");
                return;
            }

            m.BloodPaid = true;

            var wentLive = m.Match.LiveSinceUtc.HasValue;

            if (!dials.BloodEnabled || !wentLive || sameIpOpponents)
            {
                log.Info($"[PVP] match {m.Id}: no Blood paid ({(!dials.BloodEnabled ? "pvp_arena_blood_enabled is off" : !wentLive ? "the match never went Live" : "same-IP opponents")})");
                return;
            }

            var grant = new PvpBloodGrant(m.Id, dials.BloodDailyCap, dials.BloodResetTimezone, dials.BloodResetHour);
            var paid = new List<string>();

            foreach (var seat in m.Seats.Values)
            {
                try
                {
                    var forfeited = IsForfeit(seat.Participant.ExitReason);
                    var won = !outcome.IsDraw && outcome.WinningTeams.Contains(seat.TeamIndex) && !forfeited;
                    var result = PvpArenaRewards.ResultFor(outcome.IsDraw, twoTeam, won, outcome.Placements[seat.TeamIndex]);
                    var amount = PvpArenaRewards.BloodFor(result, forfeited, wentLive, sameIpOpponents, dials);

                    paid.Add($"{seat.Name} {(forfeited ? "forfeit" : result.ToString().ToLowerInvariant())} {amount}");

                    if (amount > 0)
                        players.GrantArenaBlood(seat.Id, amount, grant);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] match {m.Id}: the Blood grant for {seat.Name} (0x{seat.Id:X8}) threw; nobody is paid twice for it", ex);
                }
            }

            log.Info($"[PVP] match {m.Id}: Blood granted before the daily cap: {string.Join("; ", paid)}");
        }

        private void AdvanceResolving(MatchRuntime m, DateTime now, PvpArenaDials dials)
        {
            if ((now - m.ResolvedAtUtc).TotalSeconds < dials.PostMatchSeconds)
                return;

            CloseNow(m, now, "post-match exit");
        }

        /// <summary>
        /// The survivors' exit: everyone placed who has not already been exited is exited now, and returned unless they
        /// died (the death path owns a dead participant's one trip). Then the space is released and the match Closed.
        /// </summary>
        private void CloseNow(MatchRuntime m, DateTime now, string why)
        {
            ExitEveryoneRemaining(m, PvpArenaText.Returning, onlyIfInside: true, "match closed");

            RemoveStartGates(m, "match closed");
            spaces.Release(m.Space);
            SetState(m, PvpMatchState.Closed, now, why);
        }

        /// <summary>
        /// Exits every placed participant not yet exited. A dead participant still waiting on the death return is
        /// exited without a teleport; everyone else is exited and returned to the stamped exit (the gateway only moves
        /// someone still in the instance or on the way in). <paramref name="line"/> goes to each of them, or only to
        /// those standing in the instance when <paramref name="onlyIfInside"/> is set.
        /// </summary>
        private void ExitEveryoneRemaining(MatchRuntime m, string line, bool onlyIfInside, string context)
        {
            // Review note (#1399, finding 2): a dead seat can be exited here at the post-match deadline before its death teleport lands; no corruption path was found, so it is left as is.
            foreach (var seat in m.Seats.Values.Where(s => s.Dispatched && !s.Exited))
            {
                if (line != null && (!onlyIfInside || (m.Space != null && players.GetPresence(seat.Id, m.Space.Instance) == PvpPresence.InInstance)))
                    players.Send(seat.Id, line);

                ExitSeat(m, seat, context, teleport: !seat.AwaitingDeathReturn);
            }
        }

        /// <summary>
        /// Cancels a match. Anyone already placed and not yet exited is exited and returned (a dead participant is exited
        /// without a teleport) and hears <paramref name="placedLine"/>; the space is released. Messages to the players
        /// not yet placed are the caller's.
        /// </summary>
        private void CancelMatch(MatchRuntime m, DateTime now, string why, string placedLine = PvpArenaText.Canceled)
        {
            foreach (var seat in m.Seats.Values)
            {
                if (m.Match.State == PvpMatchState.AwaitingAccept && seat.Answer == AcceptAnswer.Pending)
                    players.AbortAcceptPrompt(seat.Id, m.Id);
            }

            ExitEveryoneRemaining(m, placedLine, onlyIfInside: false, "match canceled");

            RemoveStartGates(m, "match canceled");

            if (m.Space != null)
                spaces.Release(m.Space);

            SetState(m, PvpMatchState.Canceled, now, why);
        }

        private void Publish(MatchRuntime m, PvpMatchState state)
        {
            foreach (var seat in m.Seats.Values)
            {
                if (seat.Dispatched && !seat.Participant.ExitReason.HasValue)
                    players.PublishBinding(seat.Id, Binding(m, seat, state));
            }
        }

        private void SendActive(MatchRuntime m, string text)
        {
            foreach (var seat in m.Seats.Values)
            {
                if (seat.Dispatched && !seat.Participant.ExitReason.HasValue)
                    players.Send(seat.Id, text);
            }
        }

        private void SendAll(IEnumerable<uint> ids, string text)
        {
            foreach (var id in ids)
                players.Send(id, text);
        }

        private static bool IsForfeit(ParticipantExit? exit) => exit.HasValue && exit.Value != ParticipantExit.Died;

        private static string ForfeitCode(ParticipantExit? exit)
        {
            switch (exit)
            {
                case ParticipantExit.ForfeitLogout: return "logout";
                case ParticipantExit.ForfeitLeft: return "left";
                case ParticipantExit.ForfeitCommand: return "command";
                default: return null;
            }
        }

        private static bool HasCrossTeamSameIp(MatchRuntime m)
        {
            foreach (var a in m.Match.Teams.SelectMany(t => t.Members.Select(p => (t.TeamIndex, p.IpKey))))
                foreach (var b in m.Match.Teams.SelectMany(t => t.Members.Select(p => (t.TeamIndex, p.IpKey))))
                    if (a.TeamIndex != b.TeamIndex && a.IpKey != null && a.IpKey == b.IpKey)
                        return true;

            return false;
        }

        // =====================================================================================================
        // API (world thread). PvpMatchManager is the facade PR D's commands call.
        // =====================================================================================================

        /// <summary>
        /// Queues a player (or a premade duo, or a battleground group). PvP Template Facets: every arena and battleground
        /// match is templated (owner ruling 2026-10-03; a mode opts out only through PvpModeDefinition.Templated), so a
        /// join also names the template the player fights on - <paramref name="templateKey"/> when given, else the
        /// player's remembered preference (PvpPlayerFacts.PreferredTemplateKey). The key must be an enabled template
        /// offered for a templated mode the room can form, and the player must have pack room for it; both are checked
        /// again at accept and at dispatch (against the mode actually formed). A duo partner and every member of a
        /// battleground group always use their OWN remembered template (TEMPLATES.md "Commands").
        /// </summary>
        /// <param name="group">
        /// /arena join bg group (Docs/Pvp/BATTLEGROUNDS.md "Matchmaking" 5): queues the caller's whole fellowship as one
        /// premade unit, 2 to pvp_bg_max_premade_size members, every member admitted. Any member may issue it; each
        /// member's accept popup is their consent. Battleground room only.
        /// </param>
        public PvpJoinResult Join(uint characterId, string modeKey, bool duo, string templateKey = null, bool group = false)
        {
            var key = modeKey?.Trim().ToLowerInvariant();

            if (key == null || !rooms.TryGetValue(key, out var room))
                return new PvpJoinResult(PvpJoinRefusal.UnknownMode, modeKey);

            var now = clock();
            var dials = Dials();

            var facts = players.GetFacts(characterId);

            if (facts == null)
                return new PvpJoinResult(PvpJoinRefusal.Offline, key);

            if (duo && key != ArenaMapCatalog.TwoVTwoKey)
                return new PvpJoinResult(PvpJoinRefusal.DuoNotSupportedForMode, key);

            if (group && key != BattlegroundModes.RoomKey)
                return new PvpJoinResult(PvpJoinRefusal.GroupNotSupportedForMode, key);

            var refusal = Admit(facts, key, now, dials, out var lockoutSeconds, out var queuedMode);

            if (refusal != PvpJoinRefusal.None)
                return new PvpJoinResult(refusal, queuedMode ?? key, LockoutSecondsRemaining: lockoutSeconds, MinLevel: dials.MinLevel);

            var templateRefused = CheckJoinTemplate(facts, key, templateKey, out var chosenTemplate);

            if (templateRefused != null)
                return templateRefused;

            QueuedUnit unit;
            string partnerName = null;
            string partnerTemplate = null;

            if (duo)
            {
                var members = facts.FellowshipMemberIds ?? Array.Empty<uint>();

                if (members.Count != 2 || !members.Contains(characterId))
                    return new PvpJoinResult(PvpJoinRefusal.DuoNeedsPair, key);

                var partnerId = members.First(id => id != characterId);
                var partner = players.GetFacts(partnerId);

                if (partner == null)
                    return new PvpJoinResult(PvpJoinRefusal.DuoNeedsPair, key);

                if (Admit(partner, key, now, dials, out _, out _) != PvpJoinRefusal.None)
                    return new PvpJoinResult(PvpJoinRefusal.DuoPartnerIneligible, key, PartnerName: partner.Name);

                // The partner fights on their own remembered template, never the joiner's key.
                var partnerRefused = CheckJoinTemplate(partner, key, null, out partnerTemplate);

                if (partnerRefused != null)
                {
                    log.Info($"[PVP] duo join by {facts.Name} refused: partner {partner.Name}'s template check failed ({partnerRefused.Refusal}, {partnerRefused.TemplateKey ?? "no template"})");
                    return new PvpJoinResult(PvpJoinRefusal.DuoPartnerIneligible, key, PartnerName: partner.Name, TemplateKey: partnerRefused.TemplateKey);
                }

                partnerName = partner.Name;
                unit = new QueuedUnit(new[] { facts, partner }, now);
                unit.SetTemplate(partner.CharacterId, partnerTemplate);
            }
            else if (group)
            {
                var max = BgDials().MaxPremadeSize;
                var members = facts.FellowshipMemberIds ?? Array.Empty<uint>();

                if (members.Count < 2 || members.Count > max || !members.Contains(characterId))
                    return new PvpJoinResult(PvpJoinRefusal.GroupNeedsFellowship, key, GroupMax: max);

                var all = new List<PvpPlayerFacts> { facts };
                var memberTemplates = new Dictionary<uint, string>();

                foreach (var memberId in members.Where(id => id != characterId))
                {
                    var member = players.GetFacts(memberId);

                    if (member == null)
                        return new PvpJoinResult(PvpJoinRefusal.GroupMemberIneligible, key, PartnerName: $"0x{memberId:X8}");

                    if (Admit(member, key, now, dials, out _, out _) != PvpJoinRefusal.None)
                        return new PvpJoinResult(PvpJoinRefusal.GroupMemberIneligible, key, PartnerName: member.Name);

                    // Like a duo partner, each member fights on their OWN remembered template, never the caller's key.
                    var memberRefused = CheckJoinTemplate(member, key, null, out var memberTemplate);

                    if (memberRefused != null)
                    {
                        log.Info($"[PVP] group join by {facts.Name} refused: member {member.Name}'s template check failed ({memberRefused.Refusal}, {memberRefused.TemplateKey ?? "no template"})");
                        return new PvpJoinResult(PvpJoinRefusal.GroupMemberIneligible, key, PartnerName: member.Name, TemplateKey: memberRefused.TemplateKey);
                    }

                    memberTemplates[memberId] = memberTemplate;
                    all.Add(member);
                }

                unit = new QueuedUnit(all, now);

                foreach (var kv in memberTemplates)
                    unit.SetTemplate(kv.Key, kv.Value);
            }
            else
                unit = new QueuedUnit(new[] { facts }, now);

            unit.SetTemplate(characterId, chosenTemplate);

            room.Units.Add(unit);
            room.LastFfaCountAnnounced = -1;

            log.Info($"[PVP] {string.Join(" + ", unit.Ids.Select(id => unit.NameOf(id) + (unit.TemplateOf(id) != null ? " [" + unit.TemplateOf(id) + "]" : "")))} joined the {key} queue{(duo ? " as a pair" : group ? $" as a group of {unit.Ids.Count}" : "")}; {room.PlayerCount} waiting");

            return new PvpJoinResult(PvpJoinRefusal.None, key, room.PlayerCount, partnerName, TemplateKey: chosenTemplate, TemplateLabel: TemplateLabel(chosenTemplate));
        }

        /// <summary>
        /// The join-time template check (the first of three: join, accept, dispatch). Null when the player may queue
        /// on <paramref name="chosenKey"/>; otherwise the refusal. Order: the master switch, the catalog having been
        /// read, a key at all (the requested one, else the remembered preference), the key offered for the mode, then
        /// pack room for that template's strip and kit.
        /// </summary>
        private PvpJoinResult CheckJoinTemplate(PvpPlayerFacts facts, string modeKey, string requestedKey, out string chosenKey)
        {
            chosenKey = null;

            // Only a room whose every mode opted out of templates (none does today) skips the template entirely.
            if (!RoomTemplated(modeKey))
                return null;

            if (!TemplatesEnabled())
                return new PvpJoinResult(PvpJoinRefusal.TemplatesDisabled, modeKey);

            if (templateCatalog == null)
                return new PvpJoinResult(PvpJoinRefusal.TemplatesUnavailable, modeKey);

            var key = NormalizeTemplateKey(string.IsNullOrWhiteSpace(requestedKey) ? facts.PreferredTemplateKey : requestedKey);

            if (key == null)
                return new PvpJoinResult(PvpJoinRefusal.NoTemplateChosen, modeKey);

            if (!TryGetOfferedForRoom(key, modeKey, out var entry))
                return new PvpJoinResult(PvpJoinRefusal.TemplateNotOffered, modeKey, TemplateKey: key);

            var room = players.CheckTemplateRoom(facts.CharacterId, entry.Definition);

            if (room != null)
                return new PvpJoinResult(PvpJoinRefusal.TemplateNoRoom, modeKey, TemplateKey: entry.Key, Detail: room);

            chosenKey = entry.Key;
            return null;
        }

        /// <summary>
        /// The accept-time and dispatch-time template re-check for one participant. Null when <paramref name="key"/> is
        /// still offered for the mode and the player has room; otherwise the player-facing line.
        /// <paramref name="playerCaused"/> is true only for the pack-room failure (the player filled their pack), never
        /// for a template an admin withdrew.
        /// </summary>
        private string TemplateRefusal(uint characterId, string key, string modeKey, out bool playerCaused)
        {
            playerCaused = false;

            // A mode that opted out of templates has nothing to re-check here.
            if (!ModeKeyTemplated(modeKey))
                return null;

            if (!TemplatesEnabled())
                return PvpArenaText.TemplatesDisabled;

            if (templateCatalog == null || key == null || !templateCatalog.TryGetOffered(key, modeKey, out var entry))
                return PvpArenaText.Fill(PvpArenaText.TemplateWithdrawn, ("template", key ?? "(none)"), ("mode", PvpArenaText.ModeLabel(modeKey)));

            var room = players.CheckTemplateRoom(characterId, entry.Definition);

            if (room != null)
            {
                playerCaused = true;
                return room;
            }

            return null;
        }

        private static bool TemplatesEnabled()
        {
            try
            {
                return PvpTemplateSettings.EnabledSource();
            }
            catch (Exception)
            {
                return PvpTemplateSettings.DefaultEnabled;
            }
        }

        /// <summary>A key as typed: trimmed and lowercased; null for blank.</summary>
        internal static string NormalizeTemplateKey(string key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim().ToLowerInvariant();

        private PvpJoinRefusal Admit(PvpPlayerFacts facts, string modeKey, DateTime now, PvpArenaDials dials, out int lockoutSeconds, out string queuedMode)
        {
            lockoutSeconds = 0;
            queuedMode = rooms.Values.FirstOrDefault(r => r.Units.Any(u => u.Ids.Contains(facts.CharacterId)))?.ModeKey;

            var request = new PvpAdmissionRequest(
                ArenaEnabled: dials.Enabled,
                ModeEnabled: ModeEnabled(modeKey, dials),
                AlreadyInInstance: facts.InInstance,
                InRespite: facts.InRespite,
                HasActivePkTimer: facts.PkTimerActive,
                IsOlthoi: facts.IsOlthoi,
                IsMule: facts.IsMule,
                IsDead: facts.IsDead,
                IsTeleporting: facts.IsTeleporting,
                AlreadyQueued: queuedMode != null,
                AlreadyInMatch: FindMatchOf(facts.CharacterId) != null,
                CharacterLevel: facts.Level,
                MinLevel: dials.MinLevel,
                IsDenylisted: PvpArenaDenylistTunables.IsDenylisted(facts.CharacterId),
                IsTemplateAccount: facts.IsTemplateAccount,
                IsPvpTemplated: facts.IsPvpTemplated,
                // The PK facet is required only for a room whose modes all opted out of templates (none today).
                RequiresPkFacet: !RoomTemplated(modeKey),
                OnPkFacet: facts.OnPkFacet);

            var refusal = PvpAdmission.Evaluate(request);

            if (refusal != PvpAdmissionRefusal.None)
                return MapRefusal(refusal);

            if (lockouts.TryGetValue(facts.CharacterId, out var until) && until > now)
            {
                lockoutSeconds = (int)Math.Ceiling((until - now).TotalSeconds);
                return PvpJoinRefusal.DeclineLockout;
            }

            return PvpJoinRefusal.None;
        }

        private static PvpJoinRefusal MapRefusal(PvpAdmissionRefusal r)
        {
            switch (r)
            {
                case PvpAdmissionRefusal.ArenaDisabled: return PvpJoinRefusal.ArenaDisabled;
                case PvpAdmissionRefusal.ModeDisabled: return PvpJoinRefusal.ModeDisabled;
                case PvpAdmissionRefusal.AlreadyInInstance: return PvpJoinRefusal.AlreadyInInstance;
                case PvpAdmissionRefusal.NotOnPkFacet: return PvpJoinRefusal.NotOnPkFacet;
                case PvpAdmissionRefusal.InRespite: return PvpJoinRefusal.InRespite;
                case PvpAdmissionRefusal.ActivePkTimer: return PvpJoinRefusal.ActivePkTimer;
                case PvpAdmissionRefusal.IsOlthoi: return PvpJoinRefusal.IsOlthoi;
                case PvpAdmissionRefusal.IsMule: return PvpJoinRefusal.IsMule;
                case PvpAdmissionRefusal.IsDead: return PvpJoinRefusal.IsDead;
                case PvpAdmissionRefusal.IsTeleporting: return PvpJoinRefusal.IsTeleporting;
                case PvpAdmissionRefusal.AlreadyQueued: return PvpJoinRefusal.AlreadyQueued;
                case PvpAdmissionRefusal.AlreadyInMatch: return PvpJoinRefusal.AlreadyInMatch;
                case PvpAdmissionRefusal.BelowMinLevel: return PvpJoinRefusal.BelowMinLevel;
                case PvpAdmissionRefusal.Denylisted: return PvpJoinRefusal.Denylisted;
                case PvpAdmissionRefusal.TemplateAccount: return PvpJoinRefusal.TemplateAccount;
                case PvpAdmissionRefusal.TemplateLocked: return PvpJoinRefusal.TemplateLocked;
                default: return PvpJoinRefusal.None;
            }
        }

        /// <summary>The PvpArenaText line for a refusal, filled. Null for None. PR D may use it as is.</summary>
        public static string RefusalText(PvpJoinResult r)
        {
            switch (r.Refusal)
            {
                case PvpJoinRefusal.None: return null;
                case PvpJoinRefusal.ArenaDisabled: return PvpArenaText.Disabled;
                case PvpJoinRefusal.ModeDisabled: return PvpArenaText.Fill(PvpArenaText.ModeDisabled, ("mode", PvpArenaText.ModeLabel(r.ModeKey)));
                case PvpJoinRefusal.AlreadyQueued: return PvpArenaText.Fill(PvpArenaText.AlreadyQueued, ("mode", PvpArenaText.ModeLabel(r.ModeKey)));
                case PvpJoinRefusal.AlreadyInMatch: return PvpArenaText.AlreadyInMatch;
                case PvpJoinRefusal.AlreadyInInstance: return PvpArenaText.InInstance;
                case PvpJoinRefusal.NotOnPkFacet: return PvpArenaText.NotOnPkFacet;
                case PvpJoinRefusal.InRespite:
                case PvpJoinRefusal.ActivePkTimer: return PvpArenaText.RecentPvp;
                case PvpJoinRefusal.BelowMinLevel: return PvpArenaText.Fill(PvpArenaText.TooLow, ("level", r.MinLevel));
                case PvpJoinRefusal.Denylisted: return PvpArenaText.Denylisted;
                case PvpJoinRefusal.IsOlthoi: return PvpArenaText.Olthoi;
                case PvpJoinRefusal.IsMule: return PvpArenaText.Mule;
                case PvpJoinRefusal.IsDead:
                case PvpJoinRefusal.IsTeleporting:
                case PvpJoinRefusal.Offline: return PvpArenaText.CannotJoinNow;
                case PvpJoinRefusal.DeclineLockout: return PvpArenaText.Fill(PvpArenaText.DeclineLockout, ("seconds", r.LockoutSecondsRemaining));
                case PvpJoinRefusal.DuoNeedsPair:
                case PvpJoinRefusal.DuoNotSupportedForMode: return PvpArenaText.DuoNeedsPair;
                case PvpJoinRefusal.DuoPartnerIneligible: return PvpArenaText.Fill(PvpArenaText.DuoPartnerIneligible, ("partner", r.PartnerName));
                case PvpJoinRefusal.TemplateAccount: return PvpArenaText.TemplateAccountRefused;
                case PvpJoinRefusal.TemplateLocked: return PvpArenaText.TemplateLockedRefused;
                case PvpJoinRefusal.TemplatesDisabled: return PvpArenaText.TemplatesDisabled;
                case PvpJoinRefusal.TemplatesUnavailable: return PvpArenaText.TemplatesUnavailable;
                case PvpJoinRefusal.NoTemplateChosen: return PvpArenaText.NoTemplateChosen;
                case PvpJoinRefusal.TemplateNotOffered: return PvpArenaText.Fill(PvpArenaText.TemplateNotOffered, ("template", r.TemplateKey ?? "(none)"), ("mode", PvpArenaText.ModeLabel(r.ModeKey)));
                case PvpJoinRefusal.TemplateNoRoom: return r.Detail ?? PvpArenaText.CannotJoinNow;
                case PvpJoinRefusal.GroupNotSupportedForMode: return BattlegroundText.GroupNotSupportedForMode;
                case PvpJoinRefusal.GroupNeedsFellowship: return PvpArenaText.Fill(BattlegroundText.GroupNeedsFellowship, ("max", r.GroupMax));
                case PvpJoinRefusal.GroupMemberIneligible: return PvpArenaText.Fill(BattlegroundText.GroupMemberIneligible, ("member", r.PartnerName));
                default: return PvpArenaText.CannotJoinNow;
            }
        }

        public PvpLeaveResult Leave(uint characterId)
        {
            foreach (var room in rooms.Values)
            {
                var unit = room.Units.FirstOrDefault(u => u.Ids.Contains(characterId));

                if (unit == null)
                    continue;

                room.Units.Remove(unit);
                room.LastFfaCountAnnounced = -1;

                var partner = unit.Ids.Count > 1 ? unit.NameOf(unit.Ids.First(id => id != characterId)) : null;
                log.Info($"[PVP] {string.Join(" + ", unit.Names)} left the {room.ModeKey} queue");

                return new PvpLeaveResult(PvpLeaveOutcome.LeftQueue, room.ModeKey, partner);
            }

            var m = FindMatchOf(characterId);

            if (m == null)
                return new PvpLeaveResult(PvpLeaveOutcome.NotQueued);

            if (m.Match.State == PvpMatchState.AwaitingAccept || (m.Match.State == PvpMatchState.Staging && !m.Dispatched))
            {
                DeclineBeforePlacement(m, characterId, fromPopup: false);
                return new PvpLeaveResult(PvpLeaveOutcome.DeclinedMatch, m.ModeKey);
            }

            var seat = m.Seats[characterId];

            if (seat.Participant.ExitReason.HasValue || m.Match.State == PvpMatchState.Resolving)
                return new PvpLeaveResult(PvpLeaveOutcome.NotQueued, m.ModeKey);

            return new PvpLeaveResult(PvpLeaveOutcome.InMatchNeedsConfirm, m.ModeKey, MatchId: m.Id);
        }

        public PvpAnswerResult Accept(uint characterId) => Answer(characterId, null, true, fromPopup: false);

        public PvpAnswerResult Decline(uint characterId) => Answer(characterId, null, false, fromPopup: false);

        /// <summary>
        /// The accept popup's reply. Ignored unless that exact match is still waiting on this player: a stale answer
        /// (the client's automatic No after an abort, or a popup left over from a canceled match) changes nothing.
        /// </summary>
        public PvpAnswerResult AnswerFromPopup(uint characterId, Guid matchId, bool accepted) => Answer(characterId, matchId, accepted, fromPopup: true);

        private PvpAnswerResult Answer(uint characterId, Guid? matchId, bool accepted, bool fromPopup)
        {
            var m = matches.FirstOrDefault(x => x.Match.State == PvpMatchState.AwaitingAccept && x.Seats.ContainsKey(characterId) && (matchId == null || x.Id == matchId.Value));

            if (m == null)
            {
                if (fromPopup)
                    log.Info($"[PVP] stale accept popup answer ({(accepted ? "yes" : "no")}) from 0x{characterId:X8} for match {matchId} ignored");

                return new PvpAnswerResult(PvpAnswerOutcome.NoPendingMatch);
            }

            var seat = m.Seats[characterId];

            if (seat.Answer != AcceptAnswer.Pending)
                return new PvpAnswerResult(PvpAnswerOutcome.AlreadyAnswered, m.Id);

            if (!fromPopup)
                players.AbortAcceptPrompt(characterId, m.Id);

            if (accepted)
            {
                // The PK facet is no longer re-checked here (owner ruling 2026-10-03: any facet may queue).
                //
                // PvP Template Facets: the template is re-validated at the moment of accept (the second of three
                // checks: join, accept, dispatch), because an admin can disable it or change its modes after the join,
                // and the pack room is re-checked because the player can fill their pack while queued. A failure is
                // handled exactly like a decline. A withdrawn template is not the player's doing, so it carries no
                // lockout; a full pack is, so it locks out like any other decline. A facts-less (offline) player is
                // left to the ordinary offline handling at BeginStaging.
                var facts = players.GetFacts(characterId);

                // A mode that opted out of templates keeps the PK facet re-check this accept used to make.
                if (facts != null && !m.Mode.Templated && !facts.OnPkFacet)
                {
                    DeclineBeforePlacement(m, characterId, fromPopup, PvpArenaText.NotOnPkFacet);
                    return new PvpAnswerResult(PvpAnswerOutcome.Declined, m.Id);
                }

                if (facts != null)
                {
                    var templateRefusal = TemplateRefusal(characterId, seat.TemplateKey, m.ModeKey, out var playerCaused);

                    if (templateRefusal != null)
                    {
                        log.Info($"[PVP] match {m.Id}: {seat.Name}'s accept refused by the template re-check ({seat.TemplateKey ?? "no template"}): {templateRefusal}");
                        DeclineBeforePlacement(m, characterId, fromPopup, templateRefusal, lockOut: playerCaused);
                        return new PvpAnswerResult(PvpAnswerOutcome.Declined, m.Id);
                    }
                }

                seat.Answer = AcceptAnswer.Accepted;
                log.Info($"[PVP] match {m.Id}: {seat.Name} accepted{(fromPopup ? "" : " (chat)")}");
                return new PvpAnswerResult(PvpAnswerOutcome.Accepted, m.Id);
            }

            DeclineBeforePlacement(m, characterId, fromPopup);
            return new PvpAnswerResult(PvpAnswerOutcome.Declined, m.Id);
        }

        private void DeclineBeforePlacement(MatchRuntime m, uint characterId, bool fromPopup, string declineText = null, bool lockOut = true)
        {
            var now = clock();
            var dials = Dials();
            var seat = m.Seats[characterId];

            seat.Answer = AcceptAnswer.Declined;
            log.Info($"[PVP] match {m.Id}: {seat.Name} declined{(fromPopup ? "" : " (chat)")} during {m.Match.State}{(lockOut ? "" : " (no lockout)")}");

            players.Send(characterId, declineText ?? PvpArenaText.YouDeclined);

            if (m.Mode.Accept is AcceptPolicy.AllOrNothingPolicy)
            {
                if (lockOut)
                    LockOut(characterId, now, dials, "declined");

                FailAllOrNothing(m, new HashSet<uint> { characterId }, now, $"{seat.Name} declined");
                return;
            }

            // FFA: the decliner is dropped. Before placement in Staging the lobby is re-checked against its floor now;
            // in AwaitingAccept the tick decides once every answer is in or the deadline passes.
            if (m.Match.State == PvpMatchState.Staging)
            {
                DropSeats(m, new[] { characterId });

                if (!m.Mode.Accept.Proceeds(m.Seats.Count, m.Seats.Count + 1))
                {
                    foreach (var s in m.Seats.Values)
                        players.Send(s.Id, PvpArenaText.TooFewAccepted);

                    RequeueAtFront(m.RoomKey, UnitsKeeping(m, m.Seats.Keys.ToList()), now);
                    CancelMatch(m, now, "too few players after a decline in staging");
                }
                else if (m.Space != null)
                {
                    // The space was allocated for the old roster; seats are re-assigned from the smaller one.
                    m.Assignments = ArenaMapCatalog.AssignSpawns(m.ModeKey, m.Match.Teams) ?? m.Assignments;
                }
            }
        }

        /// <summary>
        /// /arena leave in a match, after PR D's confirmation. Before anyone is placed it is a decline; once placed it
        /// is a forfeit (ForfeitCommand): the player is exited and returned at once, and always placed last.
        /// </summary>
        public PvpForfeitResult Forfeit(uint characterId) => Forfeit(characterId, null);

        /// <summary>
        /// The leave popup's Yes passes the match it was opened in. A popup that outlived its match (the player left
        /// another way and re-queued before answering) must not decline or forfeit the NEW match: it is NotInMatch.
        /// </summary>
        public PvpForfeitResult Forfeit(uint characterId, Guid? expectedMatchId)
        {
            var m = FindMatchOf(characterId);

            if (m == null || (expectedMatchId.HasValue && m.Id != expectedMatchId.Value))
                return new PvpForfeitResult(PvpForfeitOutcome.NotInMatch);

            if (m.Match.State == PvpMatchState.AwaitingAccept || (m.Match.State == PvpMatchState.Staging && !m.Dispatched))
            {
                DeclineBeforePlacement(m, characterId, fromPopup: false);
                return new PvpForfeitResult(PvpForfeitOutcome.Declined);
            }

            var seat = m.Seats[characterId];

            if (seat.Participant.ExitReason.HasValue || m.Match.State == PvpMatchState.Resolving || !seat.Dispatched)
                return new PvpForfeitResult(PvpForfeitOutcome.AlreadyOut);

            MarkOut(m, seat, ParticipantExit.ForfeitCommand, clock(), 0);
            return new PvpForfeitResult(PvpForfeitOutcome.Forfeited);
        }

        public PvpStatusResult Status(uint characterId)
        {
            var now = clock();

            foreach (var room in rooms.Values)
            {
                var unit = room.Units.FirstOrDefault(u => u.Ids.Contains(characterId));

                if (unit != null)
                    return new PvpStatusResult(PvpStatusKind.Queued, room.ModeKey, now - unit.QueuedAtUtc, room.PlayerCount);
            }

            var m = FindMatchOf(characterId);

            if (m == null)
                return new PvpStatusResult(PvpStatusKind.Idle);

            if (m.Match.State == PvpMatchState.AwaitingAccept)
                return new PvpStatusResult(PvpStatusKind.AwaitingAccept, m.ModeKey, MapKey: m.Map?.MapKey, State: m.Match.State, MapName: m.Map?.DisplayName);

            TimeSpan? remaining = null;
            var overtime = false;

            if (m.Match.State == PvpMatchState.Live && m.Match.LiveSinceUtc.HasValue)
            {
                // In overtime the time left is to the snapshotted hard end, never the live regulation setting. An
                // objective mode's limit is its own snapshot from formation (the one its win condition plays to).
                overtime = m.OvertimeSinceUtc.HasValue;
                var limitSeconds = m.Mode.IsObjective ? m.ObjectiveTimeLimitSeconds : overtime ? m.OvertimeHardLimitSeconds : TimeLimitFor(m.ModeKey, Dials());
                var left = TimeSpan.FromSeconds(limitSeconds) - (now - m.Match.LiveSinceUtc.Value);
                remaining = left < TimeSpan.Zero ? TimeSpan.Zero : left;
            }

            if (m.Mode.IsObjective)
            {
                return new PvpStatusResult(PvpStatusKind.InMatch, m.ModeKey, MapKey: m.Map?.MapKey, State: m.Match.State, Remaining: remaining, MapName: m.Map?.DisplayName, Overtime: false,
                    Scores: new Dictionary<int, int>(m.Match.ScoreBoard), ScoreTarget: m.ScoreTarget,
                    StatusLine: (m.TickHandler as IObjectiveModeHandler)?.StatusLine(m.Match.ScoreBoard, m.ScoreTarget));
            }

            return new PvpStatusResult(PvpStatusKind.InMatch, m.ModeKey, MapKey: m.Map?.MapKey, State: m.Match.State, Remaining: remaining, MapName: m.Map?.DisplayName, Overtime: overtime);
        }

        /// <summary>A character's standing on a ladder (decay applied), or null while ratings are loading or unavailable.</summary>
        public PvpRatingView GetRating(uint characterId, string ladder)
        {
            if (!Ratings.IsAvailable)
                return null;

            return Ratings.Read(characterId, ladder, clock(), Dials());
        }

        /// <summary>The top of a ladder from memory, or null while ratings are loading or unavailable.</summary>
        public IReadOnlyList<PvpRatingView> GetTop(string ladder, int count)
        {
            if (!Ratings.IsAvailable)
                return null;

            return Ratings.Top(ladder, count, clock(), Dials());
        }

        public IReadOnlyList<PvpMatchSummary> ListMatches()
        {
            var now = clock();

            return matches
                .Where(m => m.Match.State != PvpMatchState.Closed && m.Match.State != PvpMatchState.Canceled)
                .Select(m => new PvpMatchSummary(m.Id, m.ModeKey, m.Match.State, m.Map?.MapKey, m.Map?.DisplayName, m.Space?.Instance, m.Seats.Values.Select(s => s.TemplateKey != null ? $"{s.Name} [{s.TemplateKey}]" : s.Name).ToList(), now - m.Match.CreatedAtUtc))
                .ToList();
        }

        /// <summary>
        /// Admin cancel, allowed in EVERY state (owner ruling on #1399). Nobody is requeued.
        ///   - AwaitingAccept / Staging: canceled as before; anyone already placed is exited and returned.
        ///   - Countdown / Live: canceled and UNRATED. Everyone hears the admin line, is exited and returned (a dead
        ///     participant only exited: the death path owns their trip), the space is released, and one history row is
        ///     saved with end reason Canceled and rated = false. No rating row is written.
        ///   - Resolving: the result is already decided and saved; the post-match wait is cut short and everyone exits
        ///     now. The match ends Closed, keeping its result.
        /// </summary>
        public PvpCancelResult Cancel(Guid matchId)
        {
            var m = matches.FirstOrDefault(x => x.Id == matchId && x.Match.State != PvpMatchState.Closed && x.Match.State != PvpMatchState.Canceled);

            if (m == null)
                return new PvpCancelResult(PvpCancelOutcome.NotFound);

            var now = clock();

            switch (m.Match.State)
            {
                case PvpMatchState.AwaitingAccept:
                case PvpMatchState.Staging:
                    SendAll(m.Seats.Values.Where(s => !s.Dispatched).Select(s => s.Id), PvpArenaText.Canceled);
                    CancelMatch(m, now, "admin cancel");
                    return new PvpCancelResult(PvpCancelOutcome.Canceled, PvpMatchState.Canceled);

                case PvpMatchState.Countdown:
                case PvpMatchState.Live:
                    SaveCanceledHistory(m, now);
                    CancelMatch(m, now, $"admin cancel during {m.Match.State}", PvpArenaText.AdminCanceled);
                    return new PvpCancelResult(PvpCancelOutcome.Canceled, PvpMatchState.Canceled);

                default: // Resolving
                    CloseNow(m, now, "admin cancel during Resolving: result kept, exit brought forward");
                    return new PvpCancelResult(PvpCancelOutcome.Canceled, PvpMatchState.Closed);
            }
        }

        /// <summary>The history row for a match an admin canceled after placement: end reason Canceled, unrated, no rating rows.</summary>
        private void SaveCanceledHistory(MatchRuntime m, DateTime now)
        {
            var matchRecord = new PvpMatchRecord
            {
                Mode = m.ModeKey,
                Ladder = m.Mode.LadderKey,
                Map = m.Map.MapKey,
                StartedAt = m.Match.LiveSinceUtc,
                EndedAt = now,
                Outcome = "canceled",
                EndReason = EndReason.Canceled.ToString(),
                Rated = false,
            };

            var parts = m.Seats.Values.Where(s => s.Dispatched).Select(s => new PvpMatchParticipantRecord
            {
                CharacterId = s.Id,
                CharacterName = s.Name,
                Team = s.TeamIndex,
                Placement = 0,
                Result = "canceled",
                Kills = s.Kills,
                Deaths = s.Deaths,
                ForfeitReason = ForfeitCode(s.Participant.ExitReason),
            }).ToList();

            var matchId = m.Id;

            QueueTemplateStamps(m, m.Seats.Values.Where(s => s.Dispatched));

            SavesQueued++;
            sink.SaveMatchResult(matchRecord, parts, Array.Empty<PvpRatingRecord>(), (saveResult, dbId) => dbResults.Enqueue(DbResult.SaveCompleted(matchId, saveResult, dbId)));

            log.Info($"[PVP] match {m.Id}: admin-canceled during {m.Match.State}; unrated history row queued ({parts.Count} participant(s))");
        }

        /// <summary>
        /// PvP Template Facets: parks each seat's frozen template (key and version) until the match's save comes back
        /// with its pvp_match id; DrainDbResults then queues SetPvpMatchParticipantTemplates. A seat with no frozen
        /// template (never dispatched) has nothing to stamp.
        /// </summary>
        private void QueueTemplateStamps(MatchRuntime m, IEnumerable<Seat> seats)
        {
            var stamps = seats
                .Where(s => s.Template != null)
                .Select(s => new PvpMatchParticipantTemplateRecord { CharacterId = s.Id, TemplateKey = s.Template.Key, TemplateVersion = s.Template.Version })
                .ToList();

            if (stamps.Count > 0)
                pendingStamps[m.Id] = stamps;
        }

        /// <summary>
        /// PvP Template Facets (TEMPLATES.md "Backstop": "on every match tick after"): asks each placed, arrived,
        /// still-active participant's own queue to run the equipped-item backstop, at most once per
        /// <see cref="TemplateBackstopIntervalSeconds"/> per match. A dead participant on the way out is skipped (the
        /// death return restores them). What the backstop moves comes back as a BackstopFired intent.
        /// </summary>
        private void RunTemplateBackstops(DateTime now)
        {
            foreach (var m in matches)
            {
                try
                {
                    var state = m.Match.State;

                    if (!m.Dispatched || !(state == PvpMatchState.Staging || state == PvpMatchState.Countdown || state == PvpMatchState.Live || state == PvpMatchState.Resolving))
                        continue;

                    if (m.LastTemplateBackstopUtc.HasValue && (now - m.LastTemplateBackstopUtc.Value).TotalSeconds < TemplateBackstopIntervalSeconds)
                        continue;

                    m.LastTemplateBackstopUtc = now;

                    foreach (var seat in m.Seats.Values)
                    {
                        // A battleground seat in its pen cycle is skipped: dying, dead or respawning, it is the death
                        // and respawn paths' to handle, and it keeps its issued kit through them.
                        if (seat.Dispatched && !seat.Exited && seat.SeenInInstance && !seat.AwaitingDeathReturn && seat.BgState == BgSeatState.None && seat.Template != null)
                            players.RunTemplateBackstop(seat.Id, m.Id);
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] match {m.Id}: the template backstop pass threw", ex);
                }
            }
        }

        /// <summary>
        /// A BackstopFired intent: the gate miss is noted on the match (and in the resolve log line) for staff.
        /// Survivors - personal items STILL worn after the backstop tried to move them - fail the participant: they are
        /// ejected (a forfeit while the match is undecided, so ExitPvpMatchNow restores them and the stamped exit
        /// returns them; a plain exit and return once Resolving), and an error line names them for staff.
        /// </summary>
        private void NoteTemplateBackstop(MatchRuntime m, PvpIntent intent, DateTime now)
        {
            if (m == null || !m.Seats.TryGetValue(intent.CharacterId, out var seat))
            {
                log.Warn($"[PVP] TEMPLATE BACKSTOP moved {intent.Count} personal item(s) off 0x{intent.CharacterId:X8} ({intent.Survivors} still worn), whose match {intent.MatchId} is gone");
                return;
            }

            seat.BackstopMoves += intent.Count;
            m.BackstopMoves += intent.Count;

            if (intent.Count > 0)
                log.Warn($"[PVP] match {m.Id}: TEMPLATE BACKSTOP moved {intent.Count} personal item(s) off {seat.Name} (template {seat.Template?.Key ?? seat.TemplateKey}) during {m.Match.State} - a gate miss; see the [PVPTEMPLATE] GATE MISS lines for the items");

            if (intent.Survivors <= 0 || seat.Exited || seat.AwaitingDeathReturn)
                return;

            log.Error($"[PVP] match {m.Id}: TEMPLATE BACKSTOP could not move {intent.Survivors} personal item(s) off {seat.Name} (0x{seat.Id:X8}, template {seat.Template?.Key ?? seat.TemplateKey}) during {m.Match.State}; ejecting and restoring them. Staff: check the character's gear and the [PVPTEMPLATE] lines.");

            players.Send(seat.Id, PvpArenaText.TemplateBackstopEjected);

            var state = m.Match.State;

            if ((state == PvpMatchState.Staging || state == PvpMatchState.Countdown || state == PvpMatchState.Live) && !seat.Participant.ExitReason.HasValue)
                MarkOut(m, seat, ParticipantExit.ForfeitCommand, now, 0);
            else
                ExitSeat(m, seat, "template backstop survivors", teleport: true);
        }

        // ---------------- PvP Template Facets: catalog API (world thread) ----------------

        /// <summary>True once a template read has landed (the catalog may still be empty).</summary>
        public bool TemplatesLoaded => templateCatalog != null;

        /// <summary>True while the last template read failed and no earlier catalog is loaded.</summary>
        public bool TemplatesUnavailable => templateCatalog == null && templateReadFailed;

        /// <summary>
        /// Every template the catalog holds, as the commands list them; <paramref name="offeredOnly"/> keeps only the
        /// ones enabled, parsed and offered in at least one mode. Empty while the catalog is not loaded.
        /// </summary>
        public IReadOnlyList<PvpTemplateOffer> ListTemplates(bool offeredOnly)
        {
            var catalog = templateCatalog;

            if (catalog == null)
                return Array.Empty<PvpTemplateOffer>();

            var entries = offeredOnly ? catalog.Offered() : catalog.All;

            return entries.Select(e => new PvpTemplateOffer(e.Key, e.Label, e.Version, e.Enabled, e.Modes, e.Definition != null)).ToList();
        }

        /// <summary>Whether <paramref name="key"/> is an enabled, parsed template offered for <paramref name="modeKey"/> right now.</summary>
        public bool IsTemplateOffered(string key, string modeKey)
        {
            var normalized = NormalizeTemplateKey(key);
            var mode = modeKey?.Trim().ToLowerInvariant();

            // A room key (the arena rooms, or "bg") is answered for the modes that room can form; a mode key on its own.
            return templateCatalog != null && (TryGetOfferedForRoom(normalized, mode, out _) || templateCatalog.TryGetOffered(normalized, mode, out _));
        }

        /// <summary>Whether <paramref name="key"/> is offered in any mode right now.</summary>
        public bool IsTemplateOfferedAnywhere(string key) => PvpTemplateCatalog.ModeKeys.Any(mode => IsTemplateOffered(key, mode));

        /// <summary>The display label for a template key (its display name when known, else the key). Null for null.</summary>
        public string TemplateLabel(string key) => key == null ? null : (templateCatalog?.LabelFor(key) ?? key);

        /// <summary>The template a character fought their latest match on, on <paramref name="ladder"/>, as a label; null when unknown.</summary>
        public string LatestTemplateLabel(uint characterId, string ladder) =>
            latestTemplateByLadder.TryGetValue((characterId, ladder), out var key) ? TemplateLabel(key) : null;

        /// <summary>Admin: empties a mode's queue. Returns how many players were removed.</summary>
        public int ClearQueue(string modeKey)
        {
            if (modeKey == null || !rooms.TryGetValue(modeKey.Trim(), out var room))
                return 0;

            var count = room.PlayerCount;
            room.Units.Clear();
            room.LastFfaCountAnnounced = -1;

            log.Info($"[PVP] {room.ModeKey} queue cleared by an admin ({count} player(s))");

            return count;
        }

        // ---------------- test and diagnostic reads ----------------

        internal IReadOnlyList<uint> QueuedIds(string modeKey) =>
            rooms[modeKey].Units.OrderBy(u => u.QueuedAtUtc).SelectMany(u => u.Ids).ToList();

        internal PvpMatch FindMatch(uint characterId) => FindMatchOf(characterId)?.Match;

        internal MatchSpace SpaceOf(Guid matchId) => matches.FirstOrDefault(m => m.Id == matchId)?.Space;

        internal bool IsLockedOut(uint characterId) => lockouts.TryGetValue(characterId, out var until) && until > clock();

        /// <summary>Personal items the template backstop moved during a live match (gate misses). Diagnostics and tests.</summary>
        internal int TemplateBackstopMoves(Guid matchId) => matches.FirstOrDefault(m => m.Id == matchId)?.BackstopMoves ?? 0;

        private MatchRuntime FindMatchOf(uint characterId) =>
            matches.FirstOrDefault(m => m.Match.State != PvpMatchState.Closed && m.Match.State != PvpMatchState.Canceled && m.Seats.ContainsKey(characterId));

        // =====================================================================================================
        // private types
        // =====================================================================================================

        private enum AcceptAnswer
        {
            Pending,
            Accepted,
            Declined,
            TimedOut
        }

        private sealed class Room
        {
            public string ModeKey { get; }

            /// <summary>Queued units. Order is not significant: every matchmaker orders by QueuedAtUtc.</summary>
            public List<QueuedUnit> Units { get; } = new List<QueuedUnit>();

            public DateTime? BackoffUntilUtc { get; set; }

            public int LastFfaCountAnnounced { get; set; } = -1;

            public int PlayerCount => Units.Sum(u => u.Ids.Count);

            public Room(string modeKey)
            {
                ModeKey = modeKey;
            }
        }

        /// <summary>A solo player or a premade duo waiting in a room, with each member's name and session IP.</summary>
        private sealed class QueuedUnit
        {
            public Guid EntrantId { get; } = Guid.NewGuid();

            public List<uint> Ids { get; } = new List<uint>();

            public List<string> Names { get; } = new List<string>();

            private readonly Dictionary<uint, string> ips = new Dictionary<uint, string>();

            /// <summary>PvP Template Facets: the template key each member queued on (validated at join).</summary>
            private readonly Dictionary<uint, string> templates = new Dictionary<uint, string>();
            /// <summary>Each member's allegiance monarch at join (0 = none), for the battleground clanmate split.</summary>
            private readonly Dictionary<uint, uint> monarchs = new Dictionary<uint, uint>();

            public DateTime QueuedAtUtc { get; set; }

            public bool NotifiedFull { get; set; }

            public QueuedUnit(IEnumerable<PvpPlayerFacts> members, DateTime queuedAtUtc)
            {
                foreach (var f in members)
                {
                    Ids.Add(f.CharacterId);
                    Names.Add(f.Name);
                    ips[f.CharacterId] = f.IpKey;
                    monarchs[f.CharacterId] = f.MonarchId;
                }

                QueuedAtUtc = queuedAtUtc;
            }

            private QueuedUnit(DateTime queuedAtUtc)
            {
                QueuedAtUtc = queuedAtUtc;
            }

            public string IpOf(uint id) => ips.TryGetValue(id, out var ip) ? ip : null;

            public string TemplateOf(uint id) => templates.TryGetValue(id, out var key) ? key : null;

            public void SetTemplate(uint id, string key) => templates[id] = key;
            public uint MonarchOf(uint id) => monarchs.TryGetValue(id, out var monarch) ? monarch : 0;

            public string NameOf(uint id)
            {
                var i = Ids.IndexOf(id);
                return i >= 0 ? Names[i] : $"0x{id:X8}";
            }

            public QueuedUnit Subset(IEnumerable<uint> keep)
            {
                var copy = new QueuedUnit(QueuedAtUtc);

                foreach (var id in keep)
                {
                    copy.Ids.Add(id);
                    copy.Names.Add(NameOf(id));
                    copy.ips[id] = IpOf(id);
                    copy.templates[id] = TemplateOf(id);
                    copy.monarchs[id] = MonarchOf(id);
                }

                return copy;
            }
        }

        private sealed class Seat
        {
            public PvpParticipant Participant { get; }

            public uint Id => Participant.CharacterId;

            public int TeamIndex { get; set; }

            public string Name { get; }

            public QueuedUnit Unit { get; }

            public AcceptAnswer Answer { get; set; } = AcceptAnswer.Pending;

            public bool Dispatched { get; set; }

            /// <summary>ExitPvpMatch has been issued for this participant. Every exit path checks it, so nothing runs twice.</summary>
            public bool Exited { get; set; }

            /// <summary>Observed standing in the match instance at least once. Until then a player is still loading in and is never forfeited for being elsewhere.</summary>
            public bool SeenInInstance { get; set; }

            /// <summary>Died in the match: the death path is carrying them to the stamped exit; exit once they land.</summary>
            public bool AwaitingDeathReturn { get; set; }

            public Position ExitTo { get; set; }

            public int Kills { get; set; }

            public int Deaths { get; set; }

            // ---- PvP Template Facets ----

            /// <summary>The template key the player queued on (join), re-checked at accept and dispatch.</summary>
            public string TemplateKey { get; set; }

            /// <summary>The parsed definition frozen at dispatch; every binding of the match carries this copy. Null before dispatch.</summary>
            public Templates.PvpTemplateDefinition Template { get; set; }

            /// <summary>The template's display label at dispatch, shown beside the name on the lineup, results and /top.</summary>
            public string TemplateLabel { get; set; }

            /// <summary>The template apply failed at entry: never bound, never teleported, removed without fault.</summary>
            public bool EntryFailed { get; set; }

            /// <summary>Personal items the equipped-item backstop moved off this participant during the match (gate misses).</summary>
            public int BackstopMoves { get; set; }

            /// <summary>
            /// Battleground spawn protection: the instant the seat's window ends, set when a respawn is confirmed at the spawn point
            /// (confirm time plus the match's snapshotted window), cleared on death, on an early end and on exit. Null for an arena seat.
            /// </summary>
            public DateTime? ProtectedUntilUtc { get; set; }

            /// <summary>
            /// Room-based spawn protection: the seat holds protection with no end time, for as long as the player stands in their team's spawn
            /// room (the player side starts the window when it sees them leave). Mutually exclusive with <see cref="ProtectedUntilUtc"/> here.
            /// </summary>
            public bool ProtectedInRoom { get; set; }

            /// <summary>Identifies the seat's current grant of protection (the clock ticks at the grant); carried on every binding of it.</summary>
            public long ProtectionStamp { get; set; }

            /// <summary>The spawn the seat was dispatched to, kept so a busy retry re-sends the same placement.</summary>
            public Position Spawn { get; set; }

            /// <summary>When the first busy entry report arrived; the retry window runs from here.</summary>
            public DateTime? EntryBusySinceUtc { get; set; }

            /// <summary>When the next busy retry is due; null when none is pending.</summary>
            public DateTime? RetryEntryAtUtc { get; set; }
            // ---- battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Respawn") ----

            /// <summary>Where this seat is in the pen cycle. None for every arena seat and every living battleground seat.</summary>
            public BgSeatState BgState { get; set; }

            /// <summary>When the seat respawns; set with DyingToPen.</summary>
            public DateTime? RespawnAtUtc { get; set; }

            /// <summary>The disposition's spawn index; -1 = the next team spawn round-robin.</summary>
            public int RespawnSpawnIndex { get; set; } = -1;

            public int LastPenCountdownShown { get; set; } = int.MaxValue;

            /// <summary>The respawn delay from the death's disposition; a refused respawn waits this long again.</summary>
            public TimeSpan RespawnDelay { get; set; }

            /// <summary>Where the queued respawn sends the seat; set with <see cref="BgSeatState.Respawning"/>.</summary>
            public PvpSpawnPoint RespawnTarget { get; set; }

            /// <summary>When the respawn was last queued on the player; the retry clock.</summary>
            public DateTime? RespawnIssuedAtUtc { get; set; }

            /// <summary>Respawns queued for the current pen cycle (1 = the first, more = retries).</summary>
            public int RespawnAttempts { get; set; }

            /// <summary>The team pen inside this match's instance, set at dispatch; null for an arena seat.</summary>
            public Position PenPosition { get; set; }

            /// <summary>Released from the team fellowship (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"); never released twice.</summary>
            public bool TeamFellowshipReleased { get; set; }

            public Seat(PvpParticipant participant, int teamIndex, string name, QueuedUnit unit)
            {
                Participant = participant;
                TeamIndex = teamIndex;
                Name = name;
                Unit = unit;
            }
        }

        /// <summary>A battleground seat's place in the pen cycle (Docs/Pvp/BATTLEGROUNDS.md "Respawn").</summary>
        private enum BgSeatState
        {
            /// <summary>Alive in the fight (and every arena seat).</summary>
            None,

            /// <summary>Died; the death sequence is carrying them to the pen. Never teleported by the coordinator.</summary>
            DyingToPen,

            /// <summary>Alive in the pen, waiting for RespawnAtUtc.</summary>
            InPen,

            /// <summary>
            /// The respawn is queued on the player but not yet confirmed. Still Respawning on the binding (not targetable,
            /// not on the zone) until the probe sees the player alive, out of transit, at the target spawn.
            /// </summary>
            Respawning
        }

        /// <summary>
        /// One match's runtime state. For an objective mode it is also the <see cref="IBattlegroundMatchContext"/> its tick
        /// handler gets: the view and the score dictionaries are the PvpMatch's own, and the seat, sample, drain and
        /// announce calls go back through the owning coordinator, so the handler never touches a seam directly.
        /// </summary>
        private sealed class MatchRuntime : IObjectiveMatchContext
        {
            private readonly PvpMatchCoordinator owner;

            public PvpMatch Match { get; }

            public Guid Id => Match.MatchId;

            public string ModeKey => Match.ModeKey;

            /// <summary>The queue room this match came from and goes back to: the mode key for an arena mode, "bg" for every battleground mode.</summary>
            public string RoomKey => Mode.RoomKey;

            // ---- battlegrounds ----

            /// <summary>The battleground layout for <see cref="Map"/>, found by map key at formation; null for an arena match.</summary>
            public BattlegroundLayout Layout { get; set; }

            /// <summary>The mode's time limit, snapshotted at formation from the same dials its win condition was built from.</summary>
            public int ObjectiveTimeLimitSeconds { get; set; }

            /// <summary>The mode's score target, snapshotted with the limit. For /arena status.</summary>
            public int ScoreTarget { get; set; }

            /// <summary>The pen-seal placement, queued once the space is Ready.</summary>
            public BattlegroundFixtureJob Fixtures { get; set; }

            public bool FixturesLogged { get; set; }

            /// <summary>The start-gate placement (see <see cref="BattlegroundLayout.StartGates"/>); null until queued and again once removed.</summary>
            public BattlegroundFixtureJob GateJob { get; set; }

            /// <summary>
            /// The King of the Hill zone-marker ring, planned at formation from the match's own dials snapshot; empty for
            /// every other match, a disabled setting or a marker wcid of 0.
            /// </summary>
            public IReadOnlyList<BattlegroundSealPiece> ZoneMarkers { get; set; } = Array.Empty<BattlegroundSealPiece>();

            /// <summary>The zone-marker placement, queued once after the seals; never waited on. Null until queued, or when queuing failed.</summary>
            public BattlegroundFixtureJob MarkerJob { get; set; }

            public bool MarkersQueued { get; set; }

            /// <summary>The dials snapshot the match formed with (an objective match only): the moving hill re-plans its marker ring from it.</summary>
            public BattlegroundDials BgDials { get; set; }

            public bool MarkersLogged { get; set; }

            /// <summary>Respawns issued per team, for the round-robin spawn.</summary>
            public Dictionary<int, int> RespawnCounters { get; } = new Dictionary<int, int>();

            // ---- Attack/Defend (Docs/Pvp/ATTACK-DEFEND.md): decided once, at formation ----

            /// <summary>The match's crystals, planned at formation from the dials snapshot; null for every other mode.</summary>
            public AttackDefendPlan Plan { get; set; }

            /// <summary>
            /// The attacker count the standing crystals' health is CURRENTLY sized for: the formation count, then the count passed on the
            /// last rescale. Fed to <see cref="CrystalHealthScaler.Scale"/> as its "sized for" argument so successive leaves never compound.
            /// </summary>
            public int CrystalsSizedForAttackers { get; set; }

            /// <summary>The crystal placement, queued once the seals are placed. Null until queued.</summary>
            public BattlegroundFixtureJob Objectives { get; set; }

            public bool ObjectivesLogged { get; set; }

            // ---- team fellowships (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): decided once, at formation ----

            /// <summary>This match builds team fellowships. Every binding of the match is derived from it.</summary>
            public bool TeamFellowship { get; set; }

            /// <summary>pvp_bg_team_fellowship_restore as it stood when the match formed.</summary>
            public bool TeamFellowshipRestore { get; set; }

            /// <summary>Form has been called (at Countdown).</summary>
            public bool TeamFellowshipFormed { get; set; }

            /// <summary>Dispose has been called (Closed or Canceled).</summary>
            public bool TeamFellowshipDisposed { get; set; }

            // ---- IBattlegroundMatchContext ----

            Guid IMatchView.MatchId => Match.MatchId;

            string IMatchView.ModeKey => Match.ModeKey;

            IReadOnlyList<PvpTeam> IMatchView.Teams => Match.Teams;

            PvpMatchState IMatchView.State => Match.State;

            DateTime? IMatchView.LiveSinceUtc => Match.LiveSinceUtc;

            void IMatchContext.SetState(PvpMatchState state) => Match.SetState(state);

            Dictionary<int, int> IScoredMatchView.ScoreBoard => Match.ScoreBoard;

            Dictionary<int, int> IScoredMatchView.TeamKills => Match.TeamKills;

            IReadOnlyList<BattlegroundSeat> IBattlegroundMatchContext.ActiveSeats =>
                Seats.Values.Where(s => s.Dispatched && BattlegroundSeats.IsActive(s.Participant))
                    .Select(s => new BattlegroundSeat(s.Participant, s.TeamIndex, s.BgState != BgSeatState.None))
                    .ToList();

            IReadOnlyList<BattlegroundZoneSample> IBattlegroundMatchContext.SampleZone() => owner.SampleZone(this);

            void IBattlegroundMatchContext.RequestDrain(uint characterId, int health, int stamina, int mana, bool lethal) => owner.RequestDrain(this, characterId, health, stamina, mana, lethal);

            void IBattlegroundMatchContext.Announce(string text) => owner.SendActive(this, text);

            // ThreadSafeRandom.Next is inclusive at both ends: 0 is west, 1 is east.
            bool IBattlegroundMatchContext.RollHillTieBreakWest() => ACE.Common.ThreadSafeRandom.Next(0, 1) == 0;

            void IBattlegroundMatchContext.ReplaceZoneMarkers(KothZone zone) => owner.ReplaceZoneMarkers(this, zone);

            // ---- IObjectiveMatchContext ----

            IReadOnlyList<CrystalHealth> IObjectiveMatchContext.SampleCrystals() => owner.SampleCrystals(this);

            void IObjectiveMatchContext.AnnounceToTeam(int teamIndex, string text) => owner.SendTeam(this, teamIndex, text);

            public PvpModeDefinition Mode { get; }

            /// <summary>The mode definition's win condition; Attack/Defend replaces it at formation with one sized for the match's own plan.</summary>
            public IWinCondition WinCondition { get; set; }

            /// <summary>The mode definition's tick handler; bound to the match's own layout (and plan) at formation.</summary>
            public IMatchTickHandler TickHandler { get; }

            public ArenaMap Map { get; }

            /// <summary>The queue units the match was formed from, for requeueing.</summary>
            public List<QueuedUnit> Units { get; }

            public Dictionary<uint, Seat> Seats { get; } = new Dictionary<uint, Seat>();

            public IEnumerable<uint> PlacedIds => Seats.Values.Where(s => s.Dispatched).Select(s => s.Id);

            public DateTime StateSinceUtc { get; set; }

            public DateTime AcceptDeadlineUtc { get; set; }

            public IReadOnlyList<SpawnAssignment> Assignments { get; set; }

            public MatchSpace Space { get; set; }

            public bool Dispatched { get; set; }

            /// <summary>The dials the masks were taken from at placement; fixed for the match.</summary>
            public PvpArenaDials Masks { get; set; }

            public int LastCountdownShown { get; set; } = int.MaxValue;

            public bool WarnedOneMinute { get; set; }

            // ---- overtime (Docs/Pvp/DESIGN.md "Overtime"): set once, by EnterOvertime, and never changed after ----

            /// <summary>When overtime started, or null while the match is in regulation.</summary>
            public DateTime? OvertimeSinceUtc { get; set; }

            /// <summary>pvp_arena_overtime_seconds at entry.</summary>
            public int OvertimeSeconds { get; set; }

            /// <summary>Seconds after LiveSinceUtc at which overtime ends: the regulation end at entry plus <see cref="OvertimeSeconds"/>.</summary>
            public int OvertimeHardLimitSeconds { get; set; }

            /// <summary>pvp_arena_overtime_healing_mod at entry; 1.0 (the binding identity) until then.</summary>
            public double OvertimeHealingMod { get; set; } = 1.0;

            /// <summary>pvp_arena_overtime_damage_ramp_per_minute at entry; 0 until then.</summary>
            public double OvertimeRampPerMinute { get; set; }

            public bool WarnedOvertimeMinute { get; set; }

            public MatchOutcome Outcome { get; set; }

            public bool Rated { get; set; }

            public DateTime ResolvedAtUtc { get; set; }

            /// <summary>The Blood payout has run for this match (set before the first grant). It never runs twice.</summary>
            public bool BloodPaid { get; set; }

            /// <summary>When the template backstop last ran for this match's participants.</summary>
            public DateTime? LastTemplateBackstopUtc { get; set; }

            /// <summary>Personal items the template backstop moved during this match, across every participant (gate misses).</summary>
            public int BackstopMoves { get; set; }

            public MatchRuntime(PvpMatchCoordinator owner, PvpMatch match, PvpModeDefinition mode, ArenaMap map, List<QueuedUnit> units)
            {
                this.owner = owner;
                Match = match;
                Mode = mode;
                Map = map;
                Units = units;
                WinCondition = mode.WinCondition();
                TickHandler = mode.TickHandler();
            }
        }

        private enum DbResultKind
        {
            RatingsLoaded,
            SaveCompleted,
            TemplatesLoaded,
            LatestTemplatesLoaded,
            StampsSaved
        }

        /// <summary>What a database callback hands the tick. Plain data: the callback never touches anything else.</summary>
        private sealed class DbResult
        {
            public DbResultKind Kind { get; private init; }

            public List<PvpRatingRecord> Ratings { get; private init; }

            public Guid MatchId { get; private init; }

            public PvpMatchSaveResult SaveResult { get; private init; }

            public uint DbMatchId { get; private init; }

            public static DbResult RatingsLoaded(List<PvpRatingRecord> ratings) => new DbResult { Kind = DbResultKind.RatingsLoaded, Ratings = ratings };

            public static DbResult SaveCompleted(Guid matchId, PvpMatchSaveResult result, uint dbMatchId) => new DbResult { Kind = DbResultKind.SaveCompleted, MatchId = matchId, SaveResult = result, DbMatchId = dbMatchId };

            public List<PvpTemplateRecord> TemplateRows { get; private init; }

            public List<PvpLatestParticipantTemplateRecord> LatestTemplates { get; private init; }

            public PvpTemplateStoreStatus TemplateStatus { get; private init; }

            public static DbResult TemplatesLoaded(List<PvpTemplateRecord> rows, PvpTemplateStoreStatus status) => new DbResult { Kind = DbResultKind.TemplatesLoaded, TemplateRows = rows, TemplateStatus = status };

            public static DbResult LatestTemplatesLoaded(List<PvpLatestParticipantTemplateRecord> rows, PvpTemplateStoreStatus status) => new DbResult { Kind = DbResultKind.LatestTemplatesLoaded, LatestTemplates = rows, TemplateStatus = status };

            public static DbResult StampsSaved(Guid matchId, PvpTemplateStoreStatus status) => new DbResult { Kind = DbResultKind.StampsSaved, MatchId = matchId, TemplateStatus = status };
        }
    }
}
