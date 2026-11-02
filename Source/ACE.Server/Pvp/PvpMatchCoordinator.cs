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

        /// <summary>The repeat-pairing dials (pvp_arena_pair_paid_cap and friends), read when a match forms.</summary>
        private readonly Func<PvpPairingDials> pairingDialSource;

        /// <summary>Paid-match counts per opposing account pair per arena day. In memory: a restart resets it (accepted). World thread only.</summary>
        private readonly PvpPairLedger pairLedger = new PvpPairLedger();
        private readonly PvpPairAudit pairAudit = new PvpPairAudit();
        private readonly Action<string> auditSink;

        /// <summary>
        /// Per account key, the account keys of that account's most recent 1v1 / 2v2 opponents, oldest first (at most
        /// pvp_arena_mm_recent_opponents, a repeat moves to the newest end). Fed by <see cref="RecordPairings"/> under the same
        /// gating as the pair-cap count; read by the matchmaker's fresh-opponent tiers. In memory: a restart resets it. World thread only.
        /// </summary>
        private readonly Dictionary<uint, List<uint>> recentOpponents = new Dictionary<uint, List<uint>>();

        /// <summary>Test seam (InternalsVisibleTo, ACE.Server.csproj:15): the pair ledger's count for two accounts, whatever day it is stored on.</summary>
        internal int PairCountForTest(uint accountA, uint accountB) => pairLedger.CountFor(accountA, accountB, int.MinValue);

        /// <summary>Test seam (InternalsVisibleTo, ACE.Server.csproj:15): a copy of an account's recent-opponent ring, oldest first; empty when it has none.</summary>
        internal IReadOnlyList<uint> RecentOpponentsForTest(uint account) => recentOpponents.TryGetValue(account, out var ring) ? ring.ToList() : new List<uint>();

        /// <summary>True while the matchmaking pairing-dial read is failing, so the error is logged once per failure episode.</summary>
        private bool pairingReadFailing;
        private readonly ConcurrentQueue<PvpIntent> intents;

        /// <summary>
        /// Intents taken off <see cref="intents"/> mid-tick by <see cref="DrainCrystalIntents"/> that were not that match's crystal
        /// reports. World thread only. <see cref="DrainIntents"/> runs them first, in the order they arrived, so nothing is lost or
        /// reordered among themselves.
        /// </summary>
        private readonly List<PvpIntent> deferredIntents = new List<PvpIntent>();

        private readonly IPvpArenaCrierAnnouncer arenaAnnouncer;
        private readonly PvpArenaCrier arenaCrier = new PvpArenaCrier();
        private readonly BgBonusCrier bgBonusCrier = new BgBonusCrier();
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

        /// <summary>The pvp_conduct_* settings (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"); the sampler reads the engage radius from it.</summary>
        private readonly Func<PvpConductDials> conductDialSource;

        /// <summary>The conduct dials read for THIS tick's sampler pass: read once, by the first match that samples, and cleared when the tick starts.</summary>
        private PvpConductDials conductDialsThisTick;

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
        /// The team-colour coin (Docs/Pvp/TEMPLATES.md "Team hue"), drawn ONCE per match when it forms and read through
        /// <see cref="PvpTeamColours.Assign(string, int, int, bool)"/>: in a two-team mode true makes team 0 Red, in Tugak Brawl true
        /// makes everyone Red; Attack/Defend ignores it. ThreadSafeRandom.Next is inclusive at both ends. Tests swap it per instance.
        /// </summary>
        internal Func<bool> TeamColourCoin { get; set; } = () => ACE.Common.ThreadSafeRandom.Next(0, 1) == 1;

        /// <summary>
        /// The team hue dials (Docs/Pvp/TEMPLATES.md "Team hue"), read through ONCE per dispatch, next to where the arena masks are
        /// taken, and fixed for the match. Tests swap it per instance; production reads the pvp_team_hue_* settings.
        /// </summary>
        internal Func<PvpTeamHueDials> TeamHueDials { get; set; } = PvpTeamHueSettings.Snapshot;

        /// <summary>
        /// The random source of the Tugak Brawl palette draw (Docs/Pvp/TEMPLATES.md "Team hue"): returns an integer in [0, n). It
        /// shuffles the pool once per dispatch (<see cref="PvpTeamHue.FfaPaletteDeck"/>). ThreadSafeRandom.Next is inclusive at both
        /// ends, hence n - 1. Tests swap it per instance for a deterministic draw.
        /// </summary>
        internal Func<int, int> TeamHueRandom { get; set; } = n => ACE.Common.ThreadSafeRandom.Next(0, Math.Max(0, n - 1));

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
        /// <param name="conductDialSource">The pvp_conduct_* settings (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"); defaults to <see cref="PvpConductTunables.DialSource"/>.</param>
        public PvpMatchCoordinator(IPvpPlayerGateway players, IPvpMatchSpaces spaces, IPvpResultSink sink, Func<DateTime> clock, Func<PvpArenaDials> dialSource, ConcurrentQueue<PvpIntent> intents, IPvpArenaCrierAnnouncer arenaAnnouncer = null,
            Func<BattlegroundDials> bgDialSource = null, Func<IReadOnlyList<PvpModeDefinition>, PvpModeDefinition> bgModePicker = null,
            Func<PvpPairingDials> pairingDialSource = null, Func<PvpConductDials> conductDialSource = null, Action<string> auditSink = null)
        {
            this.conductDialSource = conductDialSource ?? (() => PvpConductTunables.DialSource());
            this.players = players ?? throw new ArgumentNullException(nameof(players));
            this.spaces = spaces ?? throw new ArgumentNullException(nameof(spaces));
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.dialSource = dialSource ?? throw new ArgumentNullException(nameof(dialSource));
            this.intents = intents ?? throw new ArgumentNullException(nameof(intents));
            this.arenaAnnouncer = arenaAnnouncer ?? new LivePvpArenaCrierAnnouncer();

            this.bgDialSource = bgDialSource ?? (() => BattlegroundTunables.DialSource());
            this.pairingDialSource = pairingDialSource ?? (() => PvpPairingTunables.DialSource());
            this.auditSink = auditSink ?? (msg => ACE.Server.Managers.PlayerManager.BroadcastToAuditChannel("Arena", msg));
            bgModePickerOverride = bgModePicker;
            bgPlayers = players as IBattlegroundPlayerGateway;
            bgSpaces = spaces as IBattlegroundMatchSpaces;
            objectiveSpaces = spaces as IObjectiveMatchSpaces;
            teamFellowships = players as IPvpTeamFellowshipGateway;
            conductGateway = players as IPvpConductGateway;

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

        /// <summary>Test seam: receives each catalog parse warning as well as the log. Null in production.</summary>
        internal Action<string> TemplateWarnSink { get; set; }

        /// <summary>Builds the catalog from <paramref name="rows"/> and logs what is offered.</summary>
        private void RebuildTemplateCatalog(List<PvpTemplateRecord> rows, string why)
        {
            templateCatalog = PvpTemplateCatalog.Build(rows, line =>
            {
                log.Warn($"[PVPTEMPLATE] {line}");
                TemplateWarnSink?.Invoke(line);
            });

            log.Info($"[PVPTEMPLATE] template catalog loaded ({why}): {templateCatalog.Count} row(s); offered: {string.Join("; ", PvpTemplateCatalog.ModeKeys.Select(k => $"{k} [{string.Join(",", templateCatalog.Offered(k).Select(e => e.Key))}]"))}");
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

            conductDialsThisTick = null;

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

            PruneDeserterRecords(now);

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

            // The Double Marks announcements are the point of that feature, so they are NOT behind pvp_arena_crier_enabled.
            RunBgBonusCrier(now, dials);

            if (!dials.CrierEnabled)
                return;

            var snapshots = new List<CrierQueueSnapshot>(rooms.Count);

            // Multi-queue: a unit whose member holds a seat in a pending match is paused, so it is not counted.
            var held = HeldCharacters();

            foreach (var room in rooms.Values)
            {
                var enabled = ModeEnabled(room.ModeKey, dials);
                var eligible = EligibleUnits(room, held);
                var count = eligible.Sum(u => u.Ids.Count);
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
                            eligible.OrderBy(u => u.QueuedAtUtc).ThenBy(u => u.EntrantId).Select(u => (u.Ids.Count, u.QueuedAtUtc)), bg);

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
                    var waited = count > 0 ? now - eligible.Min(u => u.QueuedAtUtc) : TimeSpan.Zero;
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

        /// <summary>
        /// The Double Marks window lines (Docs/Pvp/BATTLEGROUNDS.md "Double Marks windows"): the pure <see cref="BgBonusCrier"/> decides, this sends.
        /// Gated by the arena master switch (pvp_arena_enabled), pvp_bg_bonus_enabled and the battleground room being playable, never by pvp_arena_crier_enabled. A throw is logged and costs only that tick.
        /// </summary>
        private void RunBgBonusCrier(DateTime now, PvpArenaDials dials)
        {
            try
            {
                var bg = BgDials();
                var enabled = dials.Enabled && bg.BonusEnabled && ModeEnabled(BattlegroundModes.RoomKey, dials);
                var (spans, zone) = BgBonusConfig.Resolve(bg);
                var lines = bgBonusCrier.Tick(now, enabled, spans, zone, bg.BonusWarnMinutes);

                if (lines.Count > 0)
                    arenaAnnouncer.Announce(lines, dials.CrierSenderName);
            }
            catch (Exception ex)
            {
                log.Error("[PVP] the Double Marks Crier threw; skipping this tick", ex);
            }
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
                        RebuildTemplateCatalog(r.TemplateRows, $"{r.TemplateStatus}");
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

                // Battleground rejoin (Docs/Pvp/BATTLEGROUNDS.md "Disconnect and rejoin"): honoured only for a Live match, checked inside.
                if (intent.Kind == PvpIntentKind.Reconnected)
                {
                    HandleReconnected(m, intent, now);
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

                // Battleground rejoin: a logout from a Live battleground holds the seat instead of forfeiting it, when TryBeginGrace allows.
                // Every other forfeit, and every arena logout, takes MarkOut exactly as before.
                // A seat the walkout poll already held (it saw them offline before this intent arrived) is the same departure: the grace keeps
                // running from the poll. A server-caused intent still forfeits it below (the poll could not know the cause); a logout while
                // on the way back in is a second departure, and TryBeginGrace refuses it (one grace per seat).
                if (intent.Kind == PvpIntentKind.Forfeit && intent.ExitReason == ParticipantExit.ForfeitLogout)
                {
                    if (seat.IsAway && !seat.Rejoining && !intent.ServerCaused)
                        continue;

                    if (TryBeginGrace(m, seat, now, intent.ServerCaused, intent.OccurredAtUtc))
                        continue;
                }

                // Deserter lockout: a logout the server or staff forced is still a forfeit, never a strike.
                MarkOut(m, seat, intent.ExitReason, now, intent.KillerId, intent.ServerCaused ? PvpDeparture.StaffOrServerLogoff : null);
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

            // Performance stats: the ledger's own destroyed set, read lock-free by the damage hook on landblock threads.
            m.Match.Performance?.MarkCrystalDestroyed(index);

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

        // ---------------- multi-queue (pvp_arena_multi_queue_enabled; Docs/Pvp/DESIGN.md "Joining the queue") ----------------

        /// <summary>
        /// The fixed order rooms are matched in each pass, so when one player's units are ready in two rooms on the same tick the
        /// earlier room wins: battlegrounds, then Tugak Brawl, then 2v2, then 1v1. Dictionary order is not a contract.
        /// </summary>
        private static readonly string[] RoomOrder = { BattlegroundModes.RoomKey, ArenaMapCatalog.FfaKey, ArenaMapCatalog.TwoVTwoKey, ArenaMapCatalog.OneVOneKey };

        /// <summary>Every room in <see cref="RoomOrder"/>, then any room the order does not name.</summary>
        private IEnumerable<Room> OrderedRooms() =>
            RoomOrder.Where(rooms.ContainsKey).Select(k => rooms[k])
                .Concat(rooms.Values.Where(r => !RoomOrder.Contains(r.ModeKey, StringComparer.OrdinalIgnoreCase)));

        /// <summary>
        /// Test seam (InternalsVisibleTo): true makes the matchmaking loop read every unit, held or not, so a test can prove the
        /// <see cref="FormMatch"/> already-held guard on its own. Production never sets it.
        /// </summary>
        internal bool DisableHoldFilterForTest { get; set; }

        private static bool IsActive(MatchRuntime m) => m.Match.State != PvpMatchState.Closed && m.Match.State != PvpMatchState.Canceled;

        /// <summary>
        /// Every character holding a seat in a match that is not Closed or Canceled (AwaitingAccept through Resolving). Their queue units
        /// in other rooms are HELD: matchmaking and every count skip them. Read fresh for each decision, so a match formed earlier in the
        /// same pass holds its players for the rest of the pass.
        /// </summary>
        private HashSet<uint> HeldCharacters()
        {
            var held = new HashSet<uint>();

            foreach (var m in matches)
                if (IsActive(m))
                    held.UnionWith(m.Seats.Keys);

            return held;
        }

        /// <summary>The room's units with no held member: the only units matchmaking and the queue counts ever read.</summary>
        private static List<QueuedUnit> EligibleUnits(Room room, HashSet<uint> held) => room.Units.Where(u => !u.Ids.Any(held.Contains)).ToList();

        private static int EligibleCount(Room room, HashSet<uint> held) => room.Units.Where(u => !u.Ids.Any(held.Contains)).Sum(u => u.Ids.Count);

        /// <summary>The rooms (in <see cref="RoomOrder"/>) where <paramref name="characterId"/> has a queue unit.</summary>
        private List<string> QueuedRoomKeys(uint characterId) =>
            OrderedRooms().Where(r => r.Units.Any(u => u.Ids.Contains(characterId))).Select(r => r.ModeKey).ToList();

        /// <summary>
        /// Removes <paramref name="characterId"/> from every queue, except <paramref name="exceptRoom"/> when given. A solo unit goes;
        /// a duo or battleground group shrinks to its other members as a NEW unit (<see cref="QueuedUnit.Subset"/>, keeping its queue
        /// time and place), and each remaining member hears <paramref name="partnerText"/> ({name}, {mode}). Returns the room keys
        /// the character was removed from, in <see cref="RoomOrder"/>. The caller tells the character.
        /// </summary>
        private List<string> WithdrawFromAllQueues(uint characterId, string partnerText, string why, string exceptRoom = null)
        {
            var withdrawn = new List<string>();

            foreach (var room in OrderedRooms())
            {
                if (exceptRoom != null && string.Equals(room.ModeKey, exceptRoom, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!ShrinkOut(room, characterId, partnerText))
                    continue;

                withdrawn.Add(room.ModeKey);
            }

            if (withdrawn.Count > 0)
                log.Info($"[PVP] 0x{characterId:X8} withdrawn from the {string.Join(", ", withdrawn)} queue(s) ({why})");

            return withdrawn;
        }

        /// <summary>
        /// Takes <paramref name="characterId"/> out of every unit of <paramref name="room"/> that holds it; a multi-member unit is replaced
        /// in place by the Subset of its other members, who hear <paramref name="partnerText"/>. True when anything was removed.
        /// </summary>
        private bool ShrinkOut(Room room, uint characterId, string partnerText)
        {
            var removed = false;

            for (var i = room.Units.Count - 1; i >= 0; i--)
            {
                var unit = room.Units[i];

                if (!unit.Ids.Contains(characterId))
                    continue;

                removed = true;

                var rest = unit.Ids.Where(id => id != characterId).ToList();

                if (rest.Count == 0)
                {
                    room.Units.RemoveAt(i);
                    continue;
                }

                // A NEW unit object (a QueuedUnit belongs to exactly one room, and a match may still hold the old one for a requeue).
                room.Units[i] = unit.Subset(rest);

                if (partnerText != null)
                    SendAll(rest, PvpArenaText.Fill(partnerText, ("name", unit.NameOf(characterId)), ("mode", PvpArenaText.ModeLabel(room.ModeKey))));
            }

            if (removed)
                room.LastFfaCountAnnounced = -1;

            return removed;
        }

        /// <summary>
        /// Ruling 4: a queued player who is offline is dropped from every queue, silently for them; the rest of a duo or group stay
        /// queued (a smaller unit, same queue time) and hear <see cref="PvpArenaText.PartnerOffline"/>.
        /// </summary>
        private void PruneOfflineQueuedUnits()
        {
            var offline = new HashSet<uint>();

            foreach (var room in rooms.Values)
                foreach (var unit in room.Units)
                    foreach (var id in unit.Ids)
                        if (!offline.Contains(id) && !players.IsOnline(id))
                            offline.Add(id);

            foreach (var id in offline)
                WithdrawFromAllQueues(id, PvpArenaText.PartnerOffline, "offline");
        }

        /// <summary>
        /// The player caused a pre-placement failure (declined, or did not answer in time): they leave every queue, and hear
        /// <paramref name="selfText"/> naming them when there were any.
        /// </summary>
        private void WithdrawForPlayerFault(uint characterId, string selfText, string why)
        {
            var withdrawn = WithdrawFromAllQueues(characterId, PvpArenaText.PartnerLeftGroup, why);

            if (withdrawn.Count > 0)
                players.Send(characterId, PvpArenaText.Fill(selfText, ("modes", PvpArenaText.ModeList(withdrawn))));
        }

        private void RunMatchmaking(DateTime now, PvpArenaDials dials)
        {
            if (!dials.Enabled)
                return;

            PruneOfflineQueuedUnits();

            if (Ratings.State == PvpRatingStoreState.Loading)
            {
                if (!loggedWaitingForRatings && rooms.Values.Any(r => r.Units.Count > 0))
                {
                    log.Warn("[PVP] matchmaking is waiting for the boot ratings read to come back");
                    loggedWaitingForRatings = true;
                }

                return;
            }

            // The matches-full notice reaches each player at most once per pass, however many rooms they wait in.
            var notifiedFullThisPass = new HashSet<uint>();

            foreach (var room in OrderedRooms())
            {
                if (!ModeEnabled(room.ModeKey, dials) || EligibleCount(room, HeldCharacters()) == 0)
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

                // Fresh-opponent matchmaking (1v1 / 2v2 only): one dial snapshot per pass. A throwing read leaves the probe null,
                // which is the legacy first-valid-candidate matchmaking.
                long recentK = 0;
                Func<QueueEntrant, QueueEntrant, (bool Recent, IReadOnlyList<bool> UnpaidA, IReadOnlyList<bool> UnpaidB)> probe = null;

                if (room.ModeKey == ArenaMapCatalog.OneVOneKey || room.ModeKey == ArenaMapCatalog.TwoVTwoKey)
                {
                    try
                    {
                        var pairing = pairingDialSource();

                        recentK = Math.Max(0, pairing.MmRecentOpponents);
                        // With Marks off nothing is paid, so no rematch is "unpaid": a cap of 0 makes every pairing paid.
                        probe = BuildPairingProbe(dials.BloodEnabled ? pairing.PaidCap : 0, ArenaDayOf(now, dials));
                        pairingReadFailing = false;
                    }
                    catch (Exception ex)
                    {
                        // Once per failure episode, not per room per tick; a later good read re-arms it.
                        if (!pairingReadFailing)
                            log.Error("[PVP] could not read the pairing dials for matchmaking; matching without fresh-opponent tiers", ex);

                        pairingReadFailing = true;
                        recentK = 0;
                        probe = null;
                    }
                }

                // Units that refused an unpaid pairing at ANY point this pass, with the refusing members: told after the pass, and
                // only if still waiting (a unit matched later in the pass is not "waiting for a fresh opponent").
                var refusedThisPass = new Dictionary<Guid, HashSet<uint>>();

                while (true)
                {
                    // Rebuilt every iteration: a match formed in an earlier room, or earlier in this one, holds its players now.
                    var eligible = DisableHoldFilterForTest ? room.Units.ToList() : EligibleUnits(room, HeldCharacters());

                    if (eligible.Count == 0)
                        break;

                    // The battleground cap counts inside the global one (Docs/Pvp/BATTLEGROUNDS.md "Settings").
                    if (MatchCapReached(dials, isBg ? bg : null))
                    {
                        // Queued players stay queued; each unit hears it once, and each player at most once per pass.
                        foreach (var unit in eligible.Where(u => !u.NotifiedFull))
                        {
                            unit.NotifiedFull = true;
                            SendAll(unit.Ids.Where(notifiedFullThisPass.Add).ToList(), PvpArenaText.MatchesFull);
                        }

                        break;
                    }

                    var waiting = eligible.Select(u => ToEntrant(u, mode, now, dials, recentK)).ToList();
                    var ctx = new MatchmakingContext(now, dials.MmWindowInitial, dials.MmWindowGrowthPerMinute, dials.MmWindowMax, dials.DuoVsSoloAfterSeconds,
                        dials.FfaTargetPlayers, dials.FfaMinPlayers, dials.FfaMaxPlayers, dials.FfaMinDecaySeconds, dials.BlockSameIp, bg,
                        Probe: probe, OnRefusedUnpaid: (id, members) =>
                        {
                            if (!refusedThisPass.TryGetValue(id, out var set))
                                refusedThisPass[id] = set = new HashSet<uint>();

                            set.UnionWith(members);
                        });

                    var proposal = matchmaker.TryForm(waiting, ctx);

                    if (proposal == null)
                        break;

                    if (!FormMatch(room, isBg ? PickBgMode(bgModes, room, proposal) : mode, proposal, now, dials, bg))
                        break;
                }

                NotifyWaitingForFreshOpponent(room, refusedThisPass);

                if (room.ModeKey == ArenaMapCatalog.FfaKey)
                    AnnounceFfaLobby(room, now, dials);
            }
        }

        /// <summary>
        /// Tells each unit that refused an unpaid pairing this pass, once, that it is waiting for a fresh opponent. Only units STILL in
        /// the room are told, and only their refusing members.
        /// </summary>
        private void NotifyWaitingForFreshOpponent(Room room, IReadOnlyDictionary<Guid, HashSet<uint>> refused)
        {
            foreach (var kv in refused)
            {
                var unit = room.Units.FirstOrDefault(u => u.EntrantId == kv.Key);

                if (unit == null || unit.NotifiedRefusal)
                    continue;

                unit.NotifiedRefusal = true;
                SendAll(unit.Ids.Where(kv.Value.Contains).ToList(), PvpArenaText.WaitingForFreshOpponent);
            }
        }
        private QueueEntrant ToEntrant(QueuedUnit u, PvpModeDefinition mode, DateTime now, PvpArenaDials dials, long recentK = 0)
        {
            var ratings = u.Ids.Select(id => Ratings.Read(id, mode.LadderKey, now, dials).Rating).ToList();

            // IpKey stays the first member's (what every arena matchmaker reads); IpKeys and MonarchIds carry each
            // member's own, which only the battleground matchmaker reads (same-IP split, clanmate split).
            // AccountKeys, RecentOpponents (the union over the unit's members, newest K only) and RefuseUnpaidRematch (any member)
            // feed the fresh-opponent tiers of the 1v1 / 2v2 matchmakers; no other matchmaker reads them.
            var accountKeys = u.AccountKeys();
            var recent = new List<uint>();

            if (recentK > 0)
            {
                foreach (var account in accountKeys)
                    if (recentOpponents.TryGetValue(account, out var ring))
                        foreach (var opponent in ring.Skip(Math.Max(0, ring.Count - (int)Math.Min(recentK, int.MaxValue))))
                            if (!recent.Contains(opponent))
                                recent.Add(opponent);
            }

            return new QueueEntrant(u.EntrantId, u.Ids, ratings, u.IpOf(u.Ids[0]), u.QueuedAtUtc,
                MonarchIds: u.Ids.Select(u.MonarchOf).ToList(),
                IpKeys: u.Ids.Select(u.IpOf).ToList(),
                AccountKeys: accountKeys,
                RecentOpponents: recent,
                RefuseUnpaidRematch: u.RefusesUnpaidRematch,
                RefuseByMember: u.Ids.Select(u.RefusalOf).ToList());
        }

        /// <summary>
        /// The fresh-opponent probe for the 1v1 / 2v2 matchmakers: whether two entrants are recent opponents, and whether each side
        /// would be flagged <see cref="Seat.UnpaidRematch"/> by the pair ledger if they met now (the same test
        /// <see cref="DecideUnpaidRematches"/> applies at formation). Pure reads of the ledger.
        /// </summary>
        private Func<QueueEntrant, QueueEntrant, (bool Recent, IReadOnlyList<bool> UnpaidA, IReadOnlyList<bool> UnpaidB)> BuildPairingProbe(long cap, int day)
        {
            return (a, b) =>
            {
                var aKeys = a.AccountKeys ?? Array.Empty<uint>();
                var bKeys = b.AccountKeys ?? Array.Empty<uint>();

                var recent = aKeys.Any(k => b.RecentOpponents != null && b.RecentOpponents.Contains(k))
                    || bKeys.Any(k => a.RecentOpponents != null && a.RecentOpponents.Contains(k));

                // Per member: a member is unpaid when ITS account is at the cap against any account on the other side.
                IReadOnlyList<bool> unpaidA = aKeys.Select(k => pairLedger.IsUnpaidRematch(k, bKeys, day, cap)).ToList();
                IReadOnlyList<bool> unpaidB = bKeys.Select(k => pairLedger.IsUnpaidRematch(k, aKeys, day, cap)).ToList();

                return (recent, unpaidA, unpaidB);
            };
        }
        /// <summary>
        /// Updates the /arena norematch choice of a character already waiting in a queue (a unit not yet in a match). Returns true
        /// when the character was found queued. A newly-set choice clears the unit's once-only waiting notice so it can be told again.
        /// </summary>
        public bool SetRefuseUnpaidRematch(uint characterId, bool refuse)
        {
            var found = false;

            foreach (var room in rooms.Values)
            {
                foreach (var unit in room.Units.Where(u => u.Ids.Contains(characterId)))
                {
                    unit.SetRefusal(characterId, refuse);
                    found = true;
                }
            }

            // A unit inside a forming match is requeued as the SAME QueuedUnit object on a decline / cancel, so the choice made
            // meanwhile must reach those units too or the requeue would restore the stale one.
            foreach (var m in matches)
                foreach (var unit in m.Units.Where(u => u.Ids.Contains(characterId)))
                    unit.SetRefusal(characterId, refuse);

            return found;
        }
        private void AnnounceFfaLobby(Room room, DateTime now, PvpArenaDials dials)
        {
            // Held units (a member's match is pending elsewhere) are neither counted nor told.
            var eligible = EligibleUnits(room, HeldCharacters());
            var count = eligible.Sum(u => u.Ids.Count);

            if (count == 0 || count == room.LastFfaCountAnnounced)
                return;

            room.LastFfaCountAnnounced = count;

            // The same formula LobbyMatchmaker forms at, so the line never promises a size the matchmaker disagrees with.
            var oldest = eligible.Min(u => u.QueuedAtUtc);
            var needed = ArenaMapCatalog.FfaDecayedTargetSize(now - oldest, dials);

            var text = PvpArenaText.Fill(PvpArenaText.FfaLobbyProgress, ("count", count), ("needed", needed));

            foreach (var unit in eligible)
                SendAll(unit.Ids, text);
        }

        /// <summary>
        /// Forms a match from <paramref name="proposal"/>. False (nothing formed) only when a proposed character is already held in another
        /// pending match: matchmaking reads <see cref="EligibleUnits"/>, so that is a bug, logged as an error, and the room's pass stops.
        /// </summary>
        private bool FormMatch(Room room, PvpModeDefinition mode, MatchProposal proposal, DateTime now, PvpArenaDials dials, BattlegroundDials bg = null)
        {
            var heldNow = HeldCharacters();
            var alreadyHeld = proposal.Teams.SelectMany(t => t.Members).Select(p => p.CharacterId).Where(heldNow.Contains).ToList();

            if (alreadyHeld.Count > 0)
            {
                log.Error($"[PVP] the {room.ModeKey} matchmaker proposed {string.Join(", ", alreadyHeld.Select(id => $"0x{id:X8}"))}, already seated in another pending match; nothing formed");
                return false;
            }

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

                // Performance stats (Docs/Pvp/BATTLEGROUNDS.md "Performance stats"): built ONCE, here, with every seat of the match, before any
                // player is bound, so its membership never changes while landblock threads read it. Collection only.
                m.Match.Performance = BattlegroundPerformanceLedger.ForTeams(teams);
            }
            else if (mode.ModeKey == ArenaMapCatalog.FfaKey)
            {
                // Conduct signals (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): Tugak Brawl keeps the same ledger, built the same way, so its
                // damage, killshots and engaged seconds are collected. Its player damage also ranks the survivors at a timeout
                // (Docs/Pvp/DESIGN.md "Win condition"). 1v1 and 2v2 stay ledger-less.
                m.Match.Performance = BattlegroundPerformanceLedger.ForTeams(teams);
            }

            foreach (var team in teams)
            {
                foreach (var p in team.Members)
                {
                    var unit = unitsByCharacter[p.CharacterId];
                    m.Seats[p.CharacterId] = new Seat(p, team.TeamIndex, unit.NameOf(p.CharacterId), unit) { TemplateKey = unit.TemplateOf(p.CharacterId) };
                }
            }

            // Team colours (Docs/Pvp/TEMPLATES.md "Team hue"): the coin is drawn ONCE, here, and every later colour read (the
            // freeze, the accept re-check, the team names) derives from it, so a match never changes colour once it has formed.
            m.ColourCoin = FlipTeamColourCoin();
            AssignTeamColours(m);

            DecideUnpaidRematches(m, dials);

            // Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): decided ONCE, here, from the same dials snapshot
            // the mode was built from, so every binding of the match (Staging onward) carries the same flag.
            DecideTeamFellowships(m, bg);

            // PvP Template Facets: the ONE kit-warm path. The kit is issued at entry; warming each template's kit
            // weenies off-thread now, ahead of the accept window and staging, means that issue does no synchronous
            // world-database read. A warm still in flight only means the apply reads the weenie itself. The warm is of the
            // catalog definition; the team hue is applied to a copy at dispatch and changes no weenie, so the same warm serves every hue.
            foreach (var definition in ResolveForMatchForm(m).Distinct())
                WarmKit(definition);

            matches.Add(m);

            log.Info($"[PVP] match {m.Id} ({m.ModeKey}) formed on map {map.MapKey}: {string.Join(" vs ", teams.Select(t => string.Join("+", t.Members.Select(p => m.Seats[p.CharacterId].Name + "(" + p.RatingAtStart + ", " + (m.Seats[p.CharacterId].TemplateKey ?? "-") + ", " + PvpTeamColours.Token(m.Seats[p.CharacterId].Colour) + ")"))))}; accept by {m.AcceptDeadlineUtc:HH:mm:ss}");

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

            return true;
        }

        /// <summary>
        /// The repeat-pairing cap (Docs/Pvp/DESIGN.md "Repeat-pairing cap"), decided ONCE at formation for a 1v1 or 2v2 match: the
        /// arena day is measured from this match's own dials snapshot, and a seat is flagged <see cref="Seat.UnpaidRematch"/> when
        /// ANY of its cross-team account pairs has already reached pvp_arena_pair_paid_cap paid matches that day. Decided per
        /// player, so a 2v2 partner who rotates cannot reset the count. Tugak, battlegrounds and FFA are not covered. A throwing
        /// dial read leaves every seat paid (logged).
        /// </summary>
        private void DecideUnpaidRematches(MatchRuntime m, PvpArenaDials dials)
        {
            if (m.ModeKey != ArenaMapCatalog.OneVOneKey && m.ModeKey != ArenaMapCatalog.TwoVTwoKey)
                return;

            try
            {
                var cap = pairingDialSource().PaidCap;

                m.PairDay = ArenaDayOf(m.Match.CreatedAtUtc, dials);

                if (cap <= 0)
                    return;

                foreach (var seat in m.Seats.Values)
                {
                    var mine = seat.Unit.AccountOf(seat.Id);
                    var opponents = m.Seats.Values.Where(o => o.TeamIndex != seat.TeamIndex).Select(o => o.Unit.AccountOf(o.Id)).ToList();

                    seat.UnpaidRematch = pairLedger.IsUnpaidRematch(mine, opponents, m.PairDay.Value, cap);
                }

                if (m.Seats.Values.Any(s => s.UnpaidRematch))
                    log.Info($"[PVP] match {m.Id}: unpaid rematch (pair cap {cap}) for {string.Join(", ", m.Seats.Values.Where(s => s.UnpaidRematch).Select(s => s.Name))}");
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: the repeat-pairing check threw; every seat stays paid", ex);

                foreach (var seat in m.Seats.Values)
                    seat.UnpaidRematch = false;
            }
        }

        /// <summary>The arena day (the Blood reset zone and hour) that <paramref name="utc"/> falls on.</summary>
        private static int ArenaDayOf(DateTime utc, PvpArenaDials dials)
        {
            var unix = (uint)Math.Max(0, new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds());

            return PvpArenaRewards.DayIndex(unix, PvpArenaRewards.ResolveZone(dials.BloodResetTimezone), dials.BloodResetHour);
        }

        /// <summary>
        /// Counts a Live, non-same-IP 1v1 / 2v2 match against every cross-team account pair on the day it FORMED (forfeits count; a
        /// match that never went Live or was canceled does not). Each account pair counts at most once per match. Saturates at the cap. Once per match.
        /// </summary>
        private void RecordPairings(MatchRuntime m, bool sameIpOpponents)
        {
            if (m.PairCounted || !m.PairDay.HasValue || sameIpOpponents || !m.Match.LiveSinceUtc.HasValue)
                return;

            m.PairCounted = true;

            try
            {
                var pairing = pairingDialSource();
                var cap = pairing.PaidCap;

                // A pair counts at most once per match, even when two seats on one team share an account (or two opposing seats do).
                var pairs = new HashSet<(uint Low, uint High)>();

                foreach (var a in m.Seats.Values)
                    foreach (var b in m.Seats.Values)
                        if (a.TeamIndex < b.TeamIndex)
                            pairs.Add(PvpPairLedger.KeyOf(a.Unit.AccountOf(a.Id), b.Unit.AccountOf(b.Id)));

                foreach (var pair in pairs)
                {
                    pairLedger.Record(pair.Low, pair.High, m.PairDay.Value, cap);

                    // The matchmaker's recent-opponent ring, fed per deduped pair under the same gating as the count above.
                    PushRecentOpponent(pair.Low, pair.High, pairing.MmRecentOpponents);
                    PushRecentOpponent(pair.High, pair.Low, pairing.MmRecentOpponents);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: counting the repeat pairing threw; the pairing is not counted", ex);
            }
        }

        /// <summary>
        /// The alternating-result audit (Docs/Pvp/DESIGN.md "Alternating-result audit"): for a 1v1 / 2v2 match that went Live
        /// (same-IP included, this is for staff review), feeds the result to <see cref="PvpPairAudit"/> and posts its line, if any,
        /// through the audit sink. Reports only; never touches a payout, rating or queue. A throw is logged and Resolve carries on.
        /// </summary>
        private void AuditAlternation(MatchRuntime m, MatchOutcome outcome, DateTime now)
        {
            if ((m.ModeKey != ArenaMapCatalog.OneVOneKey && m.ModeKey != ArenaMapCatalog.TwoVTwoKey) || !m.Match.LiveSinceUtc.HasValue)
                return;

            try
            {
                var d = pairingDialSource();

                if (!d.AuditEnabled)
                    return;

                var teams = m.Seats.Values.GroupBy(s => s.TeamIndex).OrderBy(g => g.Key)
                    .Select(g => new AuditTeam(
                        g.Select(s => PvpPairLedger.AccountKey(s.Unit.AccountOf(s.Id), s.Id)).ToList(),
                        g.Select(s => s.Name).ToList(),
                        g.Where(s => IsForfeit(s.Participant.ExitReason)).Select(s => s.Name).ToList()))
                    .ToList();

                var teamIndexes = m.Seats.Values.Select(s => s.TeamIndex).Distinct().OrderBy(i => i).ToList();
                int? winner = null;

                if (!outcome.IsDraw && outcome.WinningTeams.Count == 1)
                    winner = teamIndexes.IndexOf(outcome.WinningTeams.Single());

                if (winner < 0)
                    winner = null;

                var line = pairAudit.Record(now, teams, winner, now - m.Match.LiveSinceUtc.Value, teams.Any(t => t.Forfeited.Count > 0), d.AuditWindowHours, d.AuditMinSwaps);

                if (line != null)
                    auditSink(line);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: the alternating-result audit threw; the match result is unaffected", ex);
            }
        }

        /// <summary>
        /// Appends <paramref name="opponent"/> to <paramref name="account"/>'s recent-opponent ring: a repeat moves to the newest
        /// end, and only the newest <paramref name="keep"/> are kept. A <paramref name="keep"/> of 0 or less is off (the ring is dropped).
        /// </summary>
        private void PushRecentOpponent(uint account, uint opponent, long keep)
        {
            if (keep <= 0)
            {
                recentOpponents.Remove(account);
                return;
            }

            if (account == opponent)
                return;

            if (!recentOpponents.TryGetValue(account, out var ring))
                recentOpponents[account] = ring = new List<uint>();

            ring.Remove(opponent);
            ring.Add(opponent);

            var max = (int)Math.Min(keep, int.MaxValue);

            if (ring.Count > max)
                ring.RemoveRange(0, ring.Count - max);
        }

        /// <summary>The team-colour coin; a throwing flip reads as true (logged), so the match still forms with colours.</summary>
        private bool FlipTeamColourCoin()
        {
            try
            {
                return TeamColourCoin?.Invoke() ?? true;
            }
            catch (Exception ex)
            {
                log.Error("[PVP] the team colour coin threw; using true", ex);
                return true;
            }
        }

        /// <summary>
        /// Sets every seat's colour from the match's one coin and its CURRENT teams (so a Tugak re-index never changes the answer: Tugak
        /// gives every seat the same colour), then hands the team colours to an objective handler that names teams by them.
        /// </summary>
        private static void AssignTeamColours(MatchRuntime m)
        {
            var teamCount = m.Match.Teams.Count;

            foreach (var seat in m.Seats.Values)
                seat.Colour = PvpTeamColours.Assign(m.ModeKey, teamCount, seat.TeamIndex, m.ColourCoin);

            if (m.TickHandler is ITeamColourAware aware)
            {
                var byTeam = m.Seats.Values.GroupBy(s => s.TeamIndex).ToDictionary(g => g.Key, g => g.First().Colour);
                aware.BindTeamColours(byTeam);
            }
        }

        /// <summary>The definition each seat would wear now, for the kit warm.</summary>
        private List<PvpTemplateDefinition> ResolveForMatchForm(MatchRuntime m)
        {
            var worn = new List<PvpTemplateDefinition>();

            if (templateCatalog == null)
                return worn;

            foreach (var key in m.Seats.Values.Where(s => s.TemplateKey != null).Select(s => s.TemplateKey).Distinct())
            {
                var entry = templateCatalog.Get(key);

                if (entry?.Definition != null)
                    worn.Add(entry.Definition);
            }

            return worn;
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

            log.Info($"[PVP] match {m.Id} ({m.ModeKey}): attackers are team 0 ({(sidesSwapped ? "the proposal's second team, by the coin flip" : "the proposal's first team")}); {plan.Count} crystal(s), {(plan.Sequence != null ? "in sequence" : "any order")}, at {plan.HealthPerCrystal} health for {plan.AttackerCount} attacker(s) [{string.Join(", ", plan.Crystals.Select(c => $"#{c.Index} {c.Site.Name} ({c.Compass})"))}]; time limit {m.ObjectiveTimeLimitSeconds} s; engaged-defender reduction {CrystalDefenderReduction.DescribeDials(m.Match.DefenderReduction)}");

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
                    WithdrawForPlayerFault(seat.Id, PvpArenaText.RemovedOnTimeout, $"did not accept match {m.Id} in time");
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
                WithdrawForPlayerFault(seat.Id, PvpArenaText.RemovedOnTimeout, $"did not accept match {m.Id} in time");
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
                {
                    if (players.IsOnline(id))
                        players.Send(id, PvpArenaText.Denylisted);

                    // Multi-queue: a denylisted player leaves every queue too, so a held entry elsewhere cannot pop (and cancel) a
                    // second match. Denylisted already told them; no further line.
                    WithdrawFromAllQueues(id, PvpArenaText.PartnerLeftGroup, $"denylisted before placement in match {m.Id}");
                }

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

            // Performance stats: the crystals' sites are only placeable once the space exists. Bound here, once, before anyone is bound to
            // the match, from the plan and the formation snapshot's pvp_bg_ad_kill_range, so "near" means what the kill chip means.
            if (m.Plan != null && m.BgDials != null)
                m.Match.Performance?.BindCrystals(new CrystalProximityFrame(m.Plan.Crystals, m.Plan.Sequence, m.Space.LandblockId, m.Space.Instance, m.BgDials.AdKillRange));
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
                // A seat held for its player (the rejoin grace, or a rejoin on its way in) is AdvanceGraces' to judge, never the poll's.
                if (!seat.Dispatched || seat.Participant.ExitReason.HasValue || seat.IsAway)
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
                else if (presence == PvpPresence.Offline && !TryBeginGrace(m, seat, now, serverCaused: false, departedAtUtc: now))
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
            m.HueDials = SnapshotHueDials();

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

                // The stamped exit is the gateway's exit point, never simply where the player stands: a player pulled out of a Thread is
                // recorded as away from it and exits to the Thread's outer stamp (Docs/Pvp/BATTLEGROUNDS.md "Pulled from a Thread").
                var exitTo = players.GetExitPoint(seat.Id);

                if (exitTo == null)
                {
                    // Went offline between allocation and now. Never bound, so no hook reports it: record it here.
                    log.Warn($"[PVP] match {m.Id}: {seat.Name} is offline at placement; recorded as a logout forfeit");
                    seat.Dispatched = true;
                    MarkOut(m, seat, ParticipantExit.ForfeitLogout, now, 0, PvpDeparture.OfflineAtPlacement);
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

            // Multi-queue: placement is the moment a player leaves their other queues (the seat marked offline at placement included).
            // A duo or group they were in elsewhere stays queued without them.
            var placedLabel = PvpArenaText.ModeLabel(m.ModeKey);

            foreach (var seat in m.Seats.Values.Where(s => s.Dispatched).ToList())
            {
                var withdrawn = WithdrawFromAllQueues(seat.Id, PvpArenaText.PartnerWithdrawn, $"placed in {m.ModeKey} match {m.Id}");

                if (withdrawn.Count > 0)
                    players.Send(seat.Id, PvpArenaText.Fill(PvpArenaText.RemovedOnPlacement, ("mode", placedLabel), ("modes", PvpArenaText.ModeList(withdrawn))));
            }
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

            // Colours are re-derived from the match's coin AFTER any Tugak re-index (DropSeats), so each seat's colour matches its
            // team as it stands at dispatch. For every mode that can re-index this changes nothing (Tugak is one colour for all).
            AssignTeamColours(m);

            // Team hue: the kit each seat wears is the catalog definition dressed in the seat's team hue, built once per (template, hue)
            // for this match from the dials taken at dispatch. The catalog's own definitions are never changed.
            var hueRule = PvpTeamColours.RuleFor(m.ModeKey);
            var hueDials = m.HueDials ?? SnapshotHueDials();
            var dressed = new Dictionary<(string TemplateKey, KitHue Hue), HueApplyResult>();
            var hueSummary = new List<string>();
            var hueSkips = new List<string>();

            // Tugak Brawl (owner ruling 2026-10-16): each seat wears its OWN palette from the FFA pool, drawn without repeats, so the
            // kit is dressed per seat and not cached per (template, hue). With no usable pool Tugak keeps the older one coin-flipped
            // Red or Blue for everyone (the cached path below).
            PvpTeamHue.FfaPaletteDeck ffaDeck = null;

            if (hueRule == PvpColourRule.Tugak && hueDials.Enabled)
            {
                if (hueDials.FfaPalettes != null && hueDials.FfaPalettes.Count > 0)
                    ffaDeck = new PvpTeamHue.FfaPaletteDeck(hueDials.FfaPalettes, TeamHueRandom);
                else
                    log.Warn($"[PVPTEMPLATE] match {m.Id} ({m.ModeKey}) the Tugak palette pool pvp_team_hue_ffa_palettes has no valid entry; falling back to one coin-flipped Red or Blue for everyone");
            }

            foreach (var seat in m.Seats.Values)
            {
                if (TemplatesEnabled() && templateCatalog != null && seat.TemplateKey != null && templateCatalog.TryGetOffered(seat.TemplateKey, m.ModeKey, out var entry))
                {
                    if (ffaDeck != null)
                    {
                        var palette = ffaDeck.Next();
                        var seatResult = PvpTeamHue.Apply(entry.Definition, KitHue.Neutral, PvpTeamHue.FfaSeatDials(hueDials, palette, hueDials.FfaShade),
                            players.ClothingPalettes, players.ClothingPaletteIcon);

                        hueSummary.Add($"{entry.Key}/ffa palette {palette} for {seat.Name}: {seatResult.AppliedCount} piece(s) dressed, {seatResult.Skipped.Count} kept their own look");
                        hueSkips.AddRange(seatResult.Skipped.Select(s => $"{entry.Key}/ffa palette {palette} for {seat.Name}, {s.Name} (wcid {s.WeenieClassId}): {s.Detail}"));

                        seat.Template = seatResult.Definition ?? entry.Definition;
                        seat.TemplateLabel = entry.Label;
                        continue;
                    }

                    var hue = PvpTeamHue.KitHueFor(hueRule, m.Match.Teams.Count, seat.Colour);

                    if (!dressed.TryGetValue((entry.Key, hue), out var result))
                    {
                        result = PvpTeamHue.Apply(entry.Definition, hue, hueDials, players.ClothingPalettes, players.ClothingPaletteIcon, players.WorldLookSource);
                        dressed[(entry.Key, hue)] = result;

                        if (hueDials.Enabled)
                        {
                            hueSummary.Add($"{entry.Key}/{PvpTeamHue.Token(hue)}: {result.AppliedCount} piece(s) dressed, {result.Skipped.Count} kept their own look");
                            hueSkips.AddRange(result.Skipped.Select(s => $"{entry.Key}/{PvpTeamHue.Token(hue)} {s.Name} (wcid {s.WeenieClassId}): {s.Detail}"));
                        }
                    }

                    seat.Template = result.Definition ?? entry.Definition;
                    seat.TemplateLabel = entry.Label;
                    continue;
                }

                withdrawn.Add(seat.Id);
                players.Send(seat.Id, TemplatesEnabled()
                    ? PvpArenaText.Fill(PvpArenaText.TemplateWithdrawn, ("template", seat.TemplateKey ?? "(none)"), ("mode", PvpArenaText.ModeLabel(m.ModeKey)))
                    : PvpArenaText.TemplatesDisabled);

                log.Warn($"[PVP] match {m.Id}: {seat.Name}'s template {seat.TemplateKey ?? "(none)"} is not offered for {m.ModeKey} at dispatch; removing the seat without fault");
            }

            log.Info(hueDials.Enabled
                ? $"[PVPTEMPLATE] match {m.Id} ({m.ModeKey}) team hue: {(hueSummary.Count > 0 ? string.Join("; ", hueSummary) : "no template dressed")}"
                : $"[PVPTEMPLATE] match {m.Id} ({m.ModeKey}) team hue is OFF: every kit keeps its own look");

            foreach (var skip in hueSkips)
                log.Warn($"[PVPTEMPLATE] match {m.Id} ({m.ModeKey}) team hue kept the own look of {skip}");

            if (withdrawn.Count == 0)
                return true;

            return RemoveSeatsNoFault(m, withdrawn, now, "template not offered at dispatch");
        }

        /// <summary>One read of the team hue dials; a throwing source reads as the compiled defaults, never as "off" by accident.</summary>
        private PvpTeamHueDials SnapshotHueDials()
        {
            try
            {
                return TeamHueDials?.Invoke() ?? PvpTeamHueSettings.Snapshot();
            }
            catch (Exception ex)
            {
                log.Warn($"[PVPTEMPLATE] the team hue settings could not be read ({ex.Message}); using the defaults");
                return PvpTeamHueSettings.Snapshot();
            }
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
            // Battleground rejoin: the template entry of a seat coming back into a Live match (HandleReconnected) failed.
            if (m != null && m.Match.State == PvpMatchState.Live && m.Seats.TryGetValue(intent.CharacterId, out var rejoining) && rejoining.Rejoining
                && !rejoining.Participant.ExitReason.HasValue)
            {
                HandleRejoinEntryFailed(m, rejoining, intent, now);
                return;
            }

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
            EndParticipation(m, seat);

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

                // A battleground seat coming back into a Live match (HandleReconnected) has been seen in the instance before; it is
                // re-sent with a Live binding. Every other seat is a Staging entry, exactly as before.
                if (!seat.Dispatched || seat.Exited || (seat.SeenInInstance && !seat.Rejoining) || seat.Participant.ExitReason.HasValue || seat.Spawn == null)
                    continue;

                players.EnterAndTeleport(seat.Id, Binding(m, seat, seat.Rejoining ? PvpMatchState.Live : PvpMatchState.Staging), seat.ExitTo, seat.Spawn);
                log.Info($"[PVP] match {m.Id}: {seat.Name} re-sent to apply template {seat.Template?.Key} (busy retry{(seat.Rejoining ? ", rejoin" : "")})");
            }
        }

        /// <summary>
        /// The seat's own team spawn room (cells and footprint), carried on EVERY binding of a layout that has rooms, not only while the seat
        /// is protected in room mode: protection reads it only behind ProtectedInRoom, and the respawn-zone refill needs it for the whole match.
        /// Null on a layout without rooms and for an arena seat.
        /// </summary>
        private static SpawnRoomArea SeatRoom(MatchRuntime m, Seat seat)
            => m.Layout?.SpawnRoomAreaFor(seat.TeamIndex);

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
                NotifyUnpaidRematches(m);

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

            // Conduct signals: Tugak Brawl (the one arena mode with a ledger) is sampled like a battleground; 1v1 and 2v2 are skipped inside.
            RunConductSampler(m, now);

            // Being anywhere but the match instance while Live is a forfeit (every active participant was seen in the
            // instance before Countdown began, so the poll's arrival rule never spares anyone here).
            PollWalkouts(m, now);

            var limit = TimeLimitFor(m.ModeKey, dials);
            var rankSurvivors = m.Mode.TeamCountRange.Max > 2;
            var elapsed = (now - m.Match.LiveSinceUtc.Value).TotalSeconds;

            // A kill always wins first, in regulation and in overtime alike.
            var outcome = m.WinCondition.Evaluate(m.Match, now);

            // Overtime (Docs/Pvp/DESIGN.md "Overtime"): at the regulation limit with no winner, and overtime on,
            // the match goes to overtime instead of timing out. Entered once; its values are snapshotted then.
            if (outcome == null && !m.OvertimeSinceUtc.HasValue && elapsed >= limit && OvertimeOn(dials))
                EnterOvertime(m, now, limit, elapsed, rankSurvivors, dials);

            // The hard end: regulation alone, or regulation + overtime once overtime has started. The pure
            // ResolveTimeout is unchanged - it simply gets the larger limit. With overtime off this is exactly the
            // old path. ResolveTimeout lives on EliminationWinCondition, the one win condition every v1 mode shares.
            var hardLimit = m.OvertimeSinceUtc.HasValue ? m.OvertimeHardLimitSeconds : limit;

            // Tugak Brawl ranks its survivors by the enemy-player damage in its conduct ledger (owner ruling G1, 2026-10-15). Built only on
            // the tick the timeout can resolve: no outcome yet, a ranking mode, and the hard limit reached (the same comparison ResolveTimeout
            // makes). 1v1 and 2v2 carry no ledger and never rank.
            var damageByTeam = outcome == null && rankSurvivors && elapsed >= hardLimit ? m.Match.Performance?.PlayerDamageByTeam() : null;

            outcome ??= (m.WinCondition as EliminationWinCondition)?.ResolveTimeout(m.Match, now, hardLimit, rankSurvivors, m.Mode.TimeoutRated, damageByTeam);

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
            // Battleground rejoin (Docs/Pvp/BATTLEGROUNDS.md "Disconnect and rejoin"): arrivals, busy re-sends and expiries of held seats,
            // first, so an expired seat is out before anything below (and the win condition) reads the match.
            AdvanceGraces(m, now);
            RetryBusyEntries(m, now);

            AdvanceRespawns(m, now);

            // After the pen state machine, so a respawn confirmed this tick has already reset its seat's idle clock.
            RunConductSampler(m, now);

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
        /// A Live battleground death (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 2): the death is credited, and the kill, the
        /// killer's team kill and the killshot performance stat only against the other team (a teamkill credits no kill; a
        /// drain death names the victim as their own killer, so it credits nobody), everyone hears it, the seat goes to DyingToPen with its respawn time, and the binding is
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
                if (killer.TeamIndex != seat.TeamIndex)
                {
                    // Only an enemy kill is a kill: a teamkill used to raise the killer's saved kill count too.
                    killer.Kills++;

                    m.Match.TeamKills.TryGetValue(killer.TeamIndex, out var kills);
                    m.Match.TeamKills[killer.TeamIndex] = kills + 1;

                    // Performance stats (Docs/Pvp/BATTLEGROUNDS.md "Performance stats"): the same counted-once point as the team kill.
                    CreditKillshot(m, killer, deathPosition);

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

            // Conduct (Docs/Pvp/BATTLEGROUNDS.md "Conduct"): after the pen transition, so a feeder removed here is DyingToPen and ExitSeat
            // leaves their trip home to the death return.
            CheckFeeding(m, seat, now);
        }

        /// <summary>
        /// Performance stats (Docs/Pvp/BATTLEGROUNDS.md "Performance stats"): one cross-team killshot for <paramref name="killer"/>,
        /// classified by where the VICTIM died. Attack/Defend: near or far the vulnerable crystal (the ledger's crystal frame, the same
        /// <see cref="CrystalProximity"/> rule as the kill chip). King of the Hill: near the hill when the death lies inside the zone the
        /// handler scores now, widened by pvp_bg_perf_koth_near_margin from the formation snapshot. Called only from
        /// <see cref="HandleBattlegroundDeath"/>, after the cross-team check. Collection only; best effort.
        /// </summary>
        private void CreditKillshot(MatchRuntime m, Seat killer, Position deathPosition)
        {
            var perf = m.Match.Performance;

            if (perf == null)
                return;

            try
            {
                var nearCrystal = perf.IsNearVulnerableCrystal(deathPosition);
                var nearHill = false;
                var onHill = false;

                if (m.ModeKey == BattlegroundModes.KothModeKey && m.BgDials != null && m.Space != null && deathPosition != null && deathPosition.Instance == m.Space.Instance
                    && (m.TickHandler as IObjectiveModeHandler)?.MarkerZone is KothZone zone)
                {
                    nearHill = BattlegroundPerformanceLedger.IsNearHill(zone, m.BgDials.KothPerfNearMargin, deathPosition.PositionX, deathPosition.PositionY, deathPosition.PositionZ);

                    // PvP dailies (Usurper): the victim was standing ON the hill, the same zone with no margin.
                    onHill = BattlegroundPerformanceLedger.IsOnHill(zone, deathPosition.PositionX, deathPosition.PositionY, deathPosition.PositionZ);
                }

                perf.CreditKillshot(killer.Id, nearCrystal, nearHill, onHill);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: crediting {killer.Name}'s killshot to the performance stats threw", ex);
            }
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
                // A held seat (offline, or on its way back in) is never advanced: HandleReconnected resets its pen state.
                if (!seat.Dispatched || seat.Participant.ExitReason.HasValue || seat.BgState == BgSeatState.None || seat.IsAway)
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

                    // Conduct signals (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): a confirmed respawn restarts the seat's idle clock.
                    seat.LastActiveUtc = now;

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
                if (!seat.Dispatched || seat.Participant.ExitReason.HasValue || seat.BgState != BgSeatState.None || seat.IsAway)
                    continue;

                var sample = bgPlayers.SampleZone(seat.Id, m.Space.Instance);

                if (sample != null)
                    result.Add(sample with { CharacterId = seat.Id, TeamIndex = seat.TeamIndex, IpKey = seat.Participant.IpKey });
            }

            return result;
        }

        /// <summary>
        /// The conduct sampler (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"), once per Live tick of a match carrying a ledger (every battleground
        /// and Tugak Brawl), acting about once a second (<see cref="PvpConductSampler.SampleIntervalSeconds"/>): every active, dispatched seat's
        /// last-active time moves by <see cref="PvpConductSampler.Step"/>, and each engaged seat earns the sampled seconds of EngagedSeconds in
        /// the ledger (at most <see cref="PvpConductSampler.MaxCreditSeconds"/> per sample). A seat's clock starts when the match went Live.
        /// After a pass that sampled, <see cref="EnforceIdle"/> warns and removes idle seats (Docs/Pvp/BATTLEGROUNDS.md "Conduct"). The
        /// sampling itself is best effort: a throw is logged, nobody is judged on that pass, and the match carries on.
        /// </summary>
        private void RunConductSampler(MatchRuntime m, DateTime now)
        {
            var perf = m.Match.Performance;

            if (perf == null || bgPlayers == null || m.Space == null || m.Match.State != PvpMatchState.Live)
                return;

            PvpConductDials conduct = null;
            var sampled = false;

            try
            {
                var liveSince = m.Match.LiveSinceUtc ?? now;

                // The sample clock starts when the match went Live; whole seconds are due, the remainder carries.
                var seconds = PvpConductSampler.DueSeconds(m.ConductAnchorUtc ?? liveSince, now, out var anchor);

                m.ConductAnchorUtc = anchor;

                if (seconds <= 0)
                    return;

                var kothZone = m.ModeKey == BattlegroundModes.KothModeKey && m.BgDials != null ? (m.TickHandler as IObjectiveModeHandler)?.MarkerZone : null;
                var inputs = new List<ConductSeatInput>();

                foreach (var seat in m.Seats.Values)
                {
                    // A held seat is not sampled: its clock is reset when its player stands in the match again (CompleteRejoin).
                    if (!seat.Dispatched || seat.Participant.ExitReason.HasValue || seat.IsAway)
                        continue;

                    seat.LastActiveUtc ??= liveSince;

                    var inPen = seat.BgState != BgSeatState.None;
                    var sample = inPen ? null : bgPlayers.SampleZone(seat.Id, m.Space.Instance);
                    var onObjective = !inPen && sample != null && OnConductObjective(m, perf, kothZone, sample);

                    inputs.Add(new ConductSeatInput(seat.Id, seat.TeamIndex, inPen, sample, onObjective, seat.LastActiveUtc.Value));
                }

                // One read of the conduct dials per sampler pass (every match of this tick shares it), not one per match.
                conduct = ConductDials();

                var credit = Math.Min(seconds, PvpConductSampler.MaxCreditSeconds);

                foreach (var outcome in PvpConductSampler.Step(m.Id, now, seconds, conduct.EngageRadius, inputs))
                {
                    if (!m.Seats.TryGetValue(outcome.CharacterId, out var seat))
                        continue;

                    seat.LastActiveUtc = outcome.LastActiveUtc;
                    seat.ConductStanding = outcome.Standing;

                    if (outcome.Engaged)
                        perf.CreditEngagedSeconds(outcome.CharacterId, credit);
                }

                sampled = true;
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: the conduct sampler threw; the match is unaffected", ex);
            }

            // Outside the sampler's best-effort try: a removal is the ordinary MarkOut exit, exactly as /arena leave runs it.
            if (sampled)
                EnforceIdle(m, now, conduct);
        }

        /// <summary>The conduct dials for this tick: read once, by the first caller of the tick, and shared by every match.</summary>
        private PvpConductDials ConductDials() => conductDialsThisTick ??= conductDialSource() ?? PvpConductTunables.Defaults;

        /// <summary>
        /// Idle enforcement (Docs/Pvp/BATTLEGROUNDS.md "Conduct"), after a sampler pass: for every active, dispatched seat in pen state None,
        /// idle = now - last active. At pvp_conduct_idle_warn_seconds the seat gets ONE warning for the idle stretch (re-armed once its idle
        /// time falls back below the warning, i.e. it was active again); at pvp_conduct_idle_remove_seconds it is removed through
        /// <see cref="MarkOut"/> as <see cref="ParticipantExit.ForfeitInactive"/>. A seat in the pen cycle is never judged here, and its clock
        /// is frozen there and reset at the confirmed respawn, so a respawn timer of any length never removes anyone.
        /// </summary>
        private void EnforceIdle(MatchRuntime m, DateTime now, PvpConductDials conduct)
        {
            if (conduct == null || !conduct.IdleEnabled || m.Match.State != PvpMatchState.Live)
                return;

            var due = new List<Seat>();

            foreach (var seat in m.Seats.Values)
            {
                if (!seat.Dispatched || seat.Participant.ExitReason.HasValue || seat.BgState != BgSeatState.None || !seat.LastActiveUtc.HasValue || seat.IsAway)
                    continue;

                // Only a seat this pass saw STANDING is judged. A frozen seat (dead, teleporting) could otherwise cross the threshold on
                // sub-second phase drift, and one that left the instance or went offline belongs to the walkout poll (ForfeitLeft /
                // ForfeitLogout), not to an inactivity removal with a teleport. IdleWarned is left as it is.
                if (!seat.ConductStanding)
                    continue;

                var idle = (now - seat.LastActiveUtc.Value).TotalSeconds;

                if (idle < conduct.IdleWarnSeconds)
                {
                    seat.IdleWarned = false;
                    continue;
                }

                if (idle >= conduct.IdleRemoveSeconds)
                {
                    due.Add(seat);
                    continue;
                }

                if (!seat.IdleWarned)
                {
                    seat.IdleWarned = true;

                    var left = Math.Max(1L, conduct.IdleRemoveSeconds - (long)Math.Floor(idle));

                    players.Send(seat.Id, PvpArenaText.Fill(PvpArenaText.IdleWarning, ("seconds", left)));
                    log.Info($"[PVP] match {m.Id}: {seat.Name} idle {idle:0}s; warned ({left}s to removal)");
                }
            }

            foreach (var seat in due)
            {
                // A removal earlier in this loop can end nothing mid-loop (the win condition is evaluated by the caller), but it is
                // still re-checked: never remove a seat twice, never remove from a match that has stopped being Live.
                if (m.Match.State != PvpMatchState.Live)
                    break;

                log.Info($"[PVP] match {m.Id}: {seat.Name} idle {(now - seat.LastActiveUtc.Value).TotalSeconds:0}s; removing for inactivity");
                RemoveForConduct(m, seat, ParticipantExit.ForfeitInactive, now);
            }
        }

        /// <summary>
        /// Feeding (Docs/Pvp/BATTLEGROUNDS.md "Conduct"), called from <see cref="HandleBattlegroundDeath"/> once the seat is on its way to the
        /// pen: with pvp_conduct_feed_enabled on, a seat with at least pvp_conduct_feed_min_deaths deaths whose contribution C
        /// (<see cref="PvpContribution"/>, measured against its own team) is BELOW pvp_conduct_feed_max_share is removed as
        /// <see cref="ParticipantExit.ForfeitFeeding"/>. A seat with no C (nothing on its team to compare against) is never judged. Battlegrounds
        /// only: a Tugak Brawl death already ends that player's match.
        /// </summary>
        private void CheckFeeding(MatchRuntime m, Seat seat, DateTime now)
        {
            var conduct = ConductDials();

            if (conduct == null || !conduct.FeedEnabled || seat.Participant.ExitReason.HasValue || m.Match.State != PvpMatchState.Live || m.Match.Performance == null)
                return;

            if (seat.Deaths < conduct.FeedMinDeaths)
                return;

            double? c;

            try
            {
                var dials = PerformanceDials.From(m.BgDials ?? BgDials());
                var all = PvpContribution.Compute(m.ModeKey, BuildPerformanceSeats(m, m.Match.Performance), dials);

                c = all.TryGetValue(seat.Id, out var mine) ? mine.Contribution : null;
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: computing {seat.Name}'s contribution for the feeding check threw; not judged", ex);
                return;
            }

            if (!c.HasValue || !(c.Value < conduct.FeedMaxShare))
                return;

            log.Info($"[PVP] match {m.Id}: {seat.Name} at {seat.Deaths} deaths with contribution {c.Value:0.###} (< {conduct.FeedMaxShare:0.###}); removing for feeding");
            RemoveForConduct(m, seat, ParticipantExit.ForfeitFeeding, now);
        }

        /// <summary>
        /// The one conduct removal: the ordinary <see cref="MarkOut"/> exit (idempotent per seat), then <see cref="ConductRemoved"/> once, only
        /// when this call is what took the seat out.
        /// </summary>
        private void RemoveForConduct(MatchRuntime m, Seat seat, ParticipantExit how, DateTime now)
        {
            if (seat.Participant.ExitReason.HasValue)
                return;

            // A throwing seam inside the exit (a send, the return teleport) must not stop the next due seat or swallow the hook: the
            // seat is out once its ExitReason is set, which happens before any seam is called.
            try
            {
                MarkOut(m, seat, how, now, 0);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: removing {seat.Name} ({how}) threw part-way; ExitReason is {seat.Participant.ExitReason?.ToString() ?? "unset"}", ex);
            }

            if (seat.Participant.ExitReason != how)
                return;

            var hook = ConductRemoved;

            if (hook == null)
                return;

            try
            {
                hook(seat.Id, how);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: a conduct-removal listener threw for {seat.Name} ({how})", ex);
            }
        }

        /// <summary>
        /// Fired on the world thread once per conduct removal (Docs/Pvp/BATTLEGROUNDS.md "Conduct") with the character id and the exit
        /// (<see cref="ParticipantExit.ForfeitInactive"/> or <see cref="ParticipantExit.ForfeitFeeding"/>), after the seat is already out.
        /// Nothing in production listens: the deserter lockout is decided in <see cref="MarkOut"/> itself, for every exit. Tests listen. A
        /// throwing listener is logged and ignored.
        /// </summary>
        internal event Action<uint, ParticipantExit> ConductRemoved;

        // =====================================================================================================
        // Battleground rejoin (Docs/Pvp/BATTLEGROUNDS.md "Disconnect and rejoin"): battlegrounds only
        // =====================================================================================================

        /// <summary>
        /// How long a rejoin may take to land after the coordinator sent it (HandleReconnected) before the held seat is given up. A busy
        /// template entry pending its re-send (EntryBusyRetryWindowSeconds) waits on that instead.
        /// </summary>
        public const double RejoinArrivalTimeoutSeconds = 30;

        /// <summary>
        /// How long after a grace began its window may still slide while the player is seen standing in the match instance: the logout
        /// intent fires at the head of LogOut, up to the PK logout delay (about 20 s) before the player actually leaves, and the window
        /// should run from the real disconnect (<see cref="AdvanceGraces"/>). Bounded, so a player who never leaves cannot hold a seat
        /// outside every check forever: past this the window simply runs.
        /// </summary>
        public const double MaxLogoutLingerSeconds = 60;

        /// <summary>A battleground match: an objective mode from the battleground room. Every arena mode (1v1, 2v2, Tugak Brawl) is not one.</summary>
        private static bool IsBattlegroundMatch(MatchRuntime m) =>
            m.Mode.IsObjective && string.Equals(m.RoomKey, BattlegroundModes.RoomKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// THE ONE ENTRY to the rejoin grace, called from both places a logout reaches the coordinator: the intent drain (the logout
        /// hook's forfeit intent) and the walkout poll's offline branch (the backstop). True when the seat is now held instead of
        /// forfeited: the match is Live, it is a battleground (<see cref="IsBattlegroundMatch"/>), pvp_bg_rejoin_grace_seconds is above 0,
        /// the logout is not one the server or staff forced and the server is not shutting down, and the seat has not used its grace
        /// already (ONE grace per seat per match: a second logout forfeits at once). False leaves the caller to forfeit exactly as before,
        /// which is every arena logout, every logout before Live and every server-caused one. <paramref name="departedAtUtc"/> is when
        /// the logout happened (the intent's own time; the poll passes now): an intent drained just after the Live flip for a drop that
        /// happened before it is a dodge, not a Live logout, and is refused.
        /// </summary>
        private bool TryBeginGrace(MatchRuntime m, Seat seat, DateTime now, bool serverCaused, DateTime departedAtUtc)
        {
            if (m.Match.State != PvpMatchState.Live || !IsBattlegroundMatch(m))
                return false;

            if (!m.Match.LiveSinceUtc.HasValue || departedAtUtc < m.Match.LiveSinceUtc.Value)
                return false;

            if (!seat.Dispatched || seat.Exited || seat.Participant.ExitReason.HasValue || seat.GraceUsed)
                return false;

            if (serverCaused || ServerShuttingDown())
                return false;

            var seconds = BgDials().RejoinGraceSeconds;

            if (seconds <= 0)
                return false;

            seat.GraceUsed = true;
            seat.GraceSeconds = seconds;
            seat.GraceBeganUtc = now;
            seat.GraceUntilUtc = now.AddSeconds(seconds);
            seat.Rejoining = false;
            seat.RejoinIssuedAtUtc = null;
            seat.ProtectedUntilUtc = null;
            seat.ProtectedInRoom = false;
            seat.IdleWarned = false;

            log.Info($"[PVP] match {m.Id}: {seat.Name} (0x{seat.Id:X8}) logged out during Live; seat held for {seconds} s (until {seat.GraceUntilUtc:HH:mm:ss})");

            SendAll(m.Seats.Values.Where(s => s.Dispatched && s.Id != seat.Id).Select(s => s.Id), BattlegroundText.SeatHeld(seat.Name, seconds));
            return true;
        }

        /// <summary>A held seat stops being held (it is back in, or it is going out).</summary>
        private static void ClearAway(Seat seat)
        {
            seat.GraceUntilUtc = null;
            seat.Rejoining = false;
            seat.RejoinIssuedAtUtc = null;
        }

        /// <summary>A held seat is given up: the ordinary logout forfeit, deserter strike included unless <paramref name="departure"/> says otherwise.</summary>
        private void ForfeitAwaySeat(MatchRuntime m, Seat seat, DateTime now, PvpDeparture? departure, string why)
        {
            log.Info($"[PVP] match {m.Id}: {seat.Name}'s held seat is given up ({why}); logout forfeit{(departure.HasValue ? $" ({departure.Value})" : "")}");
            MarkOut(m, seat, ParticipantExit.ForfeitLogout, now, 0, departure);
        }

        /// <summary>
        /// Once per Live battleground tick, before anything else reads the match: every held seat is judged.
        ///   - On its way back in (<see cref="Seat.Rejoining"/>): standing in the instance completes the rejoin
        ///     (<see cref="CompleteRejoin"/>); a busy template entry waiting on its re-send waits; otherwise, not in after
        ///     <see cref="RejoinArrivalTimeoutSeconds"/>, the seat is given up (a strike only when the player is offline again).
        ///   - Still offline: at the end of the grace, the ordinary logout forfeit.
        /// </summary>
        private void AdvanceGraces(MatchRuntime m, DateTime now)
        {
            if (m.Space == null)
                return;

            foreach (var seat in m.Seats.Values.ToList())
            {
                if (!seat.IsAway || seat.Participant.ExitReason.HasValue)
                    continue;

                if (seat.Rejoining)
                {
                    var presence = players.GetPresence(seat.Id, m.Space.Instance);

                    if (presence == PvpPresence.InInstance)
                    {
                        CompleteRejoin(m, seat, now);
                        continue;
                    }

                    if (seat.RetryEntryAtUtc.HasValue)
                        continue;

                    if (seat.RejoinIssuedAtUtc.HasValue && (now - seat.RejoinIssuedAtUtc.Value).TotalSeconds >= RejoinArrivalTimeoutSeconds)
                    {
                        if (presence != PvpPresence.Offline)
                            players.Send(seat.Id, BattlegroundText.RejoinFailedYou);

                        ForfeitAwaySeat(m, seat, now, presence == PvpPresence.Offline ? null : PvpDeparture.SpaceFailure, $"the rejoin did not land within {RejoinArrivalTimeoutSeconds} s ({presence})");
                    }

                    continue;
                }

                // The logout intent can precede the real disconnect by the PK logout delay: while the player still stands in the
                // instance the window slides, so it runs from the moment they actually leave (bounded by MaxLogoutLingerSeconds).
                if (seat.GraceBeganUtc.HasValue && (now - seat.GraceBeganUtc.Value).TotalSeconds <= MaxLogoutLingerSeconds
                    && players.GetPresence(seat.Id, m.Space.Instance) == PvpPresence.InInstance)
                {
                    seat.GraceUntilUtc = now.AddSeconds(seat.GraceSeconds);
                    continue;
                }

                if (now >= seat.GraceUntilUtc.Value)
                    ForfeitAwaySeat(m, seat, now, null, "the rejoin grace ran out");
            }
        }

        /// <summary>
        /// A Reconnected intent: the player of a held seat is back online (a NEW Player object). Honoured only while the match is Live and
        /// the seat is held and not already on its way in. The new Player is admitted to the match realm (<see cref="IPvpMatchSpaces.AdmitLate"/>),
        /// the seat's pen state is reset, a team spawn is picked (the respawn round-robin), and the ordinary placement runs with a Live
        /// binding and the seat's stored exit: the template is applied first, then the bind and the teleport (the same sequence a
        /// dispatch uses). The seat stays held until <see cref="AdvanceGraces"/> sees them in the instance. A space that cannot admit them,
        /// or a layout with no spawn for the team, gives the seat up without a strike (not the player's fault).
        /// </summary>
        private void HandleReconnected(MatchRuntime m, PvpIntent intent, DateTime now)
        {
            if (m == null || m.Match.State != PvpMatchState.Live)
            {
                log.Info($"[PVP] reconnect of 0x{intent.CharacterId:X8} for match {intent.MatchId} ignored: the match is {(m == null ? "gone" : m.Match.State.ToString())}");
                return;
            }

            if (!m.Seats.TryGetValue(intent.CharacterId, out var seat) || !seat.IsAway || seat.Rejoining || seat.Exited || seat.Participant.ExitReason.HasValue)
            {
                log.Info($"[PVP] reconnect of 0x{intent.CharacterId:X8} in match {m.Id} ignored: not a held seat");
                return;
            }

            if (!players.IsOnline(seat.Id))
            {
                log.Info($"[PVP] match {m.Id}: {seat.Name} reconnected but is offline again; the grace keeps running");
                return;
            }

            bool admitted;

            try
            {
                admitted = m.Space != null && spaces.AdmitLate(m.Space, seat.Id);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: admitting {seat.Name} back into the instance threw", ex);
                admitted = false;
            }

            if (!admitted)
            {
                players.Send(seat.Id, BattlegroundText.RejoinFailedYou);
                ForfeitAwaySeat(m, seat, now, PvpDeparture.SpaceFailure, "the instance could not admit them again");
                return;
            }

            m.RespawnCounters.TryGetValue(seat.TeamIndex, out var counter);
            m.RespawnCounters[seat.TeamIndex] = counter + 1;
            var point = BattlegroundMapCatalog.RespawnSpawn(m.Layout, seat.TeamIndex, counter);

            if (point == null)
            {
                log.Error($"[PVP] match {m.Id}: no team spawn for team {seat.TeamIndex} on {m.Map.MapKey} to put {seat.Name} back at");
                players.Send(seat.Id, BattlegroundText.RejoinFailedYou);
                ForfeitAwaySeat(m, seat, now, PvpDeparture.SpaceFailure, "no team spawn");
                return;
            }

            // Whatever pen state the seat left in (a logout while dying or waiting in the pen) is dropped: the rejoin is a fresh spawn.
            seat.BgState = BgSeatState.None;
            seat.RespawnAtUtc = null;
            seat.LastPenCountdownShown = int.MaxValue;
            ClearRespawnIssue(seat);
            seat.ProtectedUntilUtc = null;
            seat.ProtectedInRoom = false;
            seat.AwaitingDeathReturn = false;

            seat.Spawn = ArenaSpawnPosition.Build(m.Map, point, m.Space.Instance);
            seat.Rejoining = true;
            seat.RejoinIssuedAtUtc = now;
            seat.EntryBusySinceUtc = null;
            seat.RetryEntryAtUtc = null;

            players.EnterAndTeleport(seat.Id, Binding(m, seat, PvpMatchState.Live), seat.ExitTo, seat.Spawn);

            if (m.TeamFellowship && teamFellowships != null && !seat.TeamFellowshipReleased)
            {
                try
                {
                    teamFellowships.Readmit(m.Id, seat.TeamIndex, seat.Id);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] match {m.Id}: putting {seat.Name} back in the team fellowship threw", ex);
                }
            }

            log.Info($"[PVP] match {m.Id}: {seat.Name} reconnected; admitted again and sent to apply template {seat.Template?.Key}, then spawn {point.Label} on team {seat.TeamIndex}");
        }

        /// <summary>
        /// A held seat's player is standing in the instance again: no longer held, the idle clock restarts now (so idle enforcement does
        /// not count the time they were away), they are protected as after a respawn, the binding is republished, and everyone is told.
        /// </summary>
        private void CompleteRejoin(MatchRuntime m, Seat seat, DateTime now)
        {
            ClearAway(seat);
            seat.EntryBusySinceUtc = null;
            seat.RetryEntryAtUtc = null;
            seat.SeenInInstance = true;
            seat.LastActiveUtc = now;
            seat.IdleWarned = false;
            seat.ConductStanding = false;

            GrantSpawnProtection(m, seat, now);
            players.PublishBinding(seat.Id, Binding(m, seat, m.Match.State));

            players.Send(seat.Id, BattlegroundText.RejoinedYou);

            var vulnerable = (m.TickHandler as IObjectiveModeHandler)?.VulnerableLine();

            if (vulnerable != null)
                players.Send(seat.Id, vulnerable);

            SendAll(m.Seats.Values.Where(s => s.Dispatched && s.Id != seat.Id).Select(s => s.Id), BattlegroundText.Rejoined(seat.Name));

            log.Info($"[PVP] match {m.Id}: {seat.Name} rejoined; idle clock reset, spawn protection granted");
        }

        /// <summary>
        /// The rejoin entry of a held seat failed (HandleEntryFailed). A busy player is re-sent for the usual short window
        /// (<see cref="EntryBusyRetryWindowSeconds"/>); otherwise the held seat is given up: a strike when the player caused it (busy past
        /// the window, a pack too full for the kit), none otherwise.
        /// </summary>
        private void HandleRejoinEntryFailed(MatchRuntime m, Seat seat, PvpIntent intent, DateTime now)
        {
            if (intent.Retryable)
            {
                seat.EntryBusySinceUtc ??= now;

                if ((now - seat.EntryBusySinceUtc.Value).TotalSeconds < EntryBusyRetryWindowSeconds)
                {
                    seat.RetryEntryAtUtc = now.AddSeconds(EntryBusyRetryIntervalSeconds);
                    log.Info($"[PVP] match {m.Id}: {seat.Name} was busy at the rejoin template entry; retrying in {EntryBusyRetryIntervalSeconds} s");
                    return;
                }
            }

            seat.RetryEntryAtUtc = null;

            if (intent.Retryable)
                players.Send(seat.Id, ACE.Server.Pvp.Templates.PvpTemplateText.ApplyBusy);

            players.Send(seat.Id, BattlegroundText.RejoinFailedYou);

            ForfeitAwaySeat(m, seat, now, intent.PlayerCaused ? null : PvpDeparture.SpaceFailure, $"the rejoin template entry failed{(intent.PlayerCaused ? " (player caused)" : "")}");
        }

        /// <summary>The player's match participation has ended (Thread return): the gateway is told once per seat.</summary>
        private void EndParticipation(MatchRuntime m, Seat seat)
        {
            if (seat.ParticipationEnded || !seat.Dispatched)
                return;

            seat.ParticipationEnded = true;

            try
            {
                players.OnParticipationEnded(seat.Id, m.Space?.Instance ?? 0);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] ending the participation of {seat.Name} (0x{seat.Id:X8}) threw", ex);
            }
        }

        /// <summary>
        /// The Live battleground match <paramref name="characterId"/> holds a seat in (its rejoin grace, not yet on its way back in), or
        /// null. Read by the login path (world thread) to report <see cref="PvpIntentKind.Reconnected"/> and to leave a Thread away entry
        /// for after the rejoin.
        /// </summary>
        public Guid? GracedMatchOf(uint characterId)
        {
            var m = FindMatchOf(characterId);

            if (m == null || m.Match.State != PvpMatchState.Live || !m.Seats.TryGetValue(characterId, out var seat))
                return null;

            return seat.IsAway && !seat.Rejoining && !seat.Exited && !seat.Participant.ExitReason.HasValue ? m.Id : (Guid?)null;
        }

        /// <summary>Test seam: the seat's rejoin grace end, or null when it is not held.</summary>
        internal DateTime? GraceUntilForTest(uint characterId) =>
            FindMatchOf(characterId) is MatchRuntime m && m.Seats.TryGetValue(characterId, out var seat) ? seat.GraceUntilUtc : null;

        /// <summary>Test seam: the seat is on its way back in.</summary>
        internal bool IsRejoiningForTest(uint characterId) =>
            FindMatchOf(characterId) is MatchRuntime m && m.Seats.TryGetValue(characterId, out var seat) && seat.Rejoining;

        // =====================================================================================================
        // Deserter lockout (Docs/Pvp/BATTLEGROUNDS.md "Deserter lockout"): battlegrounds and Tugak Brawl only
        // =====================================================================================================

        /// <summary>The optional conduct seam: the player gateway itself, when it implements it. Null means strikes lock out in memory only.</summary>
        private readonly IPvpConductGateway conductGateway;

        /// <summary>
        /// Per character, the newest deserter record this coordinator knows: written at each strike (so the lockout holds at once, before the
        /// property write lands), and seeded at join from the stored record on the player facts when that one is newer. World thread only.
        /// A restart loses it; the stored record then carries the lockout.
        /// </summary>
        private readonly Dictionary<uint, PvpDeserterRecord> deserterRecords = new Dictionary<uint, PvpDeserterRecord>();

        /// <summary>
        /// True once the final shutdown has begun (ServerManager.ShutdownInProgress, set before the logoff cascade is queued): those logout
        /// forfeits are not desertions. An announced countdown (ShutdownInitiated alone) does NOT count, so a leave during it still strikes.
        /// Tests swap it.
        /// </summary>
        internal Func<bool> ServerShuttingDown { get; set; } = () => ACE.Server.Managers.ServerManager.ShutdownInProgress;

        /// <summary>Test seam: the deserter record the coordinator holds for a character, or null.</summary>
        internal PvpDeserterRecord DeserterRecordForTest(uint characterId) => deserterRecords.TryGetValue(characterId, out var r) ? r : null;

        private PvpConductDials DeserterDials() => PvpConductTunables.Clamp(conductDialSource() ?? PvpConductTunables.Defaults);

        /// <summary>The newer of the in-memory record and the stored one on <paramref name="facts"/> (by last strike time), remembered.</summary>
        private PvpDeserterRecord MergeDeserterFacts(PvpPlayerFacts facts) =>
            MergeDeserterRecord(facts.CharacterId, new PvpDeserterRecord(facts.DeserterStrikes, facts.DeserterLastStrikeAt, facts.DeserterLockoutUntil));

        /// <summary>
        /// The newer of the in-memory record and <paramref name="stored"/> (by last strike time; a stored record with no time is ignored),
        /// remembered. Null when neither exists.
        /// </summary>
        private PvpDeserterRecord MergeDeserterRecord(uint characterId, PvpDeserterRecord stored)
        {
            deserterRecords.TryGetValue(characterId, out var known);

            if (stored != null && stored.LastStrikeAt > 0 && (known == null || stored.LastStrikeAt > known.LastStrikeAt))
            {
                known = stored;
                deserterRecords[characterId] = known;
            }

            return known;
        }

        /// <summary>
        /// /arenaadmin deserter: the effective record (in memory or <paramref name="stored"/>, whichever is newer; <see cref="PvpDeserterRecord.None"/>
        /// when neither exists), the strike count after decay as of now, and the lockout seconds left. World thread.
        /// </summary>
        public PvpDeserterStatus DeserterStatus(uint characterId, PvpDeserterRecord stored)
        {
            var record = MergeDeserterRecord(characterId, stored) ?? PvpDeserterRecord.None;
            var now = PvpDeserterRules.ToUnix(clock());
            var dials = DeserterDials();

            return new PvpDeserterStatus(
                record,
                PvpDeserterLadder.Decayed(record.Strikes, record.LastStrikeAt, now, dials.DeserterDecayDays),
                PvpDeserterLadder.SecondsRemaining(record, now, dials));
        }

        /// <summary>
        /// /arenaadmin deserter &lt;character&gt; clear: zero strikes and lockout, held in memory at once and stored through the conduct seam (online
        /// or offline, the same dispatcher as a strike). The cleared record carries NOW as its time, so it is newer than any stored strike
        /// still landing and wins every merge. One audit line names the admin. World thread.
        /// </summary>
        public PvpDeserterClearResult ClearDeserter(uint characterId, string characterName, string adminName, PvpDeserterRecord stored = null)
        {
            var cleared = new PvpDeserterRecord(0, PvpDeserterRules.ToUnix(clock()), 0);

            var before = MergeDeserterRecord(characterId, stored);
            deserterRecords[characterId] = cleared;

            var persisted = false;

            try
            {
                if (conductGateway != null)
                {
                    conductGateway.PersistDeserterRecord(characterId, cleared);
                    persisted = true;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] clearing {characterName}'s deserter record: storing it threw; the in-memory clear still holds", ex);
            }

            var line = $"{PvpPairAudit.Tag} {adminName} cleared the deserter record of {characterName} (0x{characterId:X8}); was strikes {before?.Strikes ?? 0}, locked until {before?.LockoutUntil ?? 0}{(persisted ? "" : " (in memory only: nothing was stored)")}";

            log.Info($"[PVP] {line}");

            try
            {
                auditSink(line);
            }
            catch (Exception ex)
            {
                log.Error("[PVP] the deserter clear audit line threw", ex);
            }

            return new PvpDeserterClearResult(cleared, persisted);
        }

        /// <summary>How often (at most) <see cref="PruneDeserterRecords"/> sweeps the in-memory records.</summary>
        public const double DeserterPruneIntervalSeconds = 600;

        /// <summary>
        /// A record is kept at least this long after its last strike (or clear), so a clear or strike whose storage write is still being
        /// retried (at most 10 attempts 30 s apart) is never dropped in favour of the stale stored record.
        /// </summary>
        public const long DeserterPruneMinAgeSeconds = 3600;

        private DateTime? lastDeserterPruneUtc;

        /// <summary>
        /// Called from the tick; acts at most once per <see cref="DeserterPruneIntervalSeconds"/>. Drops every in-memory record whose lockout
        /// has ended, whose strikes have fully decayed, and whose last strike is at least <see cref="DeserterPruneMinAgeSeconds"/> old. Such
        /// a record changes nothing: the next strike or join treats a missing record as zero strikes and no lockout.
        /// </summary>
        private void PruneDeserterRecords(DateTime now)
        {
            if (lastDeserterPruneUtc.HasValue && (now - lastDeserterPruneUtc.Value).TotalSeconds < DeserterPruneIntervalSeconds)
                return;

            lastDeserterPruneUtc = now;

            if (deserterRecords.Count == 0)
                return;

            try
            {
                var nowUnix = PvpDeserterRules.ToUnix(now);
                var dials = DeserterDials();

                foreach (var id in deserterRecords.Where(kvp => IsSpent(kvp.Value, nowUnix, dials)).Select(kvp => kvp.Key).ToList())
                    deserterRecords.Remove(id);
            }
            catch (Exception ex)
            {
                log.Error("[PVP] pruning the deserter records threw", ex);
            }
        }

        private static bool IsSpent(PvpDeserterRecord r, long nowUnix, PvpConductDials dials) =>
            r.LockoutUntil <= nowUnix
            && nowUnix - r.LastStrikeAt >= DeserterPruneMinAgeSeconds
            && PvpDeserterLadder.Decayed(r.Strikes, r.LastStrikeAt, nowUnix, dials.DeserterDecayDays) == 0;

        /// <summary>
        /// The join check: seconds of deserter lockout left for a battleground or Tugak Brawl join, else 0. Any other room, or the switch
        /// off, is never refused. Reads the in-memory record and the stored one, whichever is newer.
        /// </summary>
        private long DeserterSecondsRemaining(PvpPlayerFacts facts, string roomKey, DateTime now)
        {
            var record = MergeDeserterFacts(facts);

            if (record == null || !PvpDeserterRules.InScope(roomKey))
                return 0;

            return PvpDeserterLadder.SecondsRemaining(record, PvpDeserterRules.ToUnix(now), DeserterDials());
        }

        /// <summary>
        /// Called from <see cref="MarkOut"/> once the seat is out. A departure the exit table calls a strike, from a Live battleground or Tugak
        /// Brawl match, outside a server shutdown, advances the ladder: the new record is held in memory at once (the lockout is immediate) and
        /// handed to the conduct seam to store. Returns the record, or null for no strike. Never throws: a failure is logged and the exit goes on.
        /// </summary>
        private PvpDeserterRecord ApplyDeserterStrike(MatchRuntime m, Seat seat, ParticipantExit how, PvpDeparture? overrideDeparture, DateTime now)
        {
            try
            {
                if (m.Match.State != PvpMatchState.Live || !PvpDeserterRules.InScope(m.RoomKey))
                    return null;

                var departure = overrideDeparture ?? PvpDeserterRules.DepartureOf(how);

                if (ServerShuttingDown())
                    departure = PvpDeparture.ServerShutdown;

                if (PvpDeserterRules.Classify(departure) != PvpDeserterVerdict.Strike)
                    return null;

                var nowUnix = PvpDeserterRules.ToUnix(now);
                deserterRecords.TryGetValue(seat.Id, out var known);
                known ??= PvpDeserterRecord.None;

                var next = PvpDeserterLadder.Next(known.Strikes, known.LastStrikeAt, nowUnix, DeserterDials());

                if (next == null)
                    return null;

                deserterRecords[seat.Id] = next;

                log.Info($"[PVP] match {m.Id}: {seat.Name} (0x{seat.Id:X8}) deserter strike {next.Strikes} ({departure}); locked out of battlegrounds and Tugak Brawl for {next.LockoutUntil - nowUnix} s");

                try
                {
                    conductGateway?.PersistDeserterRecord(seat.Id, next);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] match {m.Id}: storing {seat.Name}'s deserter record threw; the in-memory lockout still holds", ex);
                }

                return next;
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: the deserter check for {seat.Name} ({how}) threw", ex);
                return null;
            }
        }

        /// <summary>The strike line, only to a player still online, and only when the strike carries a lockout (a 0-minute length says nothing).</summary>
        private void TellDeserterStrike(Seat seat, PvpDeserterRecord strike)
        {
            var seconds = strike.LockoutUntil - strike.LastStrikeAt;

            if (seconds <= 0 || !players.IsOnline(seat.Id))
                return;

            players.Send(seat.Id, PvpArenaText.Fill(PvpArenaText.DeserterStruck, ("duration", PvpArenaText.LockoutDurationPhrase(seconds))));
        }

        /// <summary>
        /// The conduct sampler's objective test for one standing sample: King of the Hill inside the scored zone widened by the formation
        /// snapshot's pvp_bg_perf_koth_near_margin; Attack/Defend near the vulnerable crystal by the ledger's own rule. False for anything else.
        /// </summary>
        private static bool OnConductObjective(MatchRuntime m, BattlegroundPerformanceLedger perf, KothZone? kothZone, BattlegroundZoneSample sample)
        {
            if (!sample.InInstance || sample.IsDead || sample.IsTeleporting)
                return false;

            if (kothZone.HasValue)
                return PvpConductSampler.OnKothObjective(kothZone.Value, m.BgDials.KothPerfNearMargin, sample);

            if (perf.Crystals == null)
                return false;

            var at = new Position(((m.Space.LandblockId & 0xFFFF) << 16) | 0x0001, (float)sample.X, (float)sample.Y, (float)sample.Z, 0f, 0f, 0f, 1f, m.Space.Instance);

            return perf.IsNearVulnerableCrystal(at) == true;
        }

        /// <summary>
        /// Conduct signals: when the seat of <paramref name="characterId"/> was last active (see <see cref="RunConductSampler"/>), or null when
        /// it has no clock (not in a sampled Live match, or not sampled yet). World thread. Diagnostics, tests and the later enforcement.
        /// </summary>
        internal DateTime? ConductLastActiveUtc(uint characterId)
        {
            foreach (var m in matches)
                if (m.Seats.TryGetValue(characterId, out var seat) && !seat.Exited)
                    return seat.LastActiveUtc;

            return null;
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

            if (!seat.Dispatched || seat.Participant.ExitReason.HasValue || seat.BgState != BgSeatState.None || seat.IsAway)
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
        private void EnterOvertime(MatchRuntime m, DateTime now, int limit, double elapsed, bool rankSurvivors, PvpArenaDials dials)
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

            SendActive(m, PvpArenaText.OvertimeAnnouncement(m.OvertimeSeconds, healingFactor, ramp, rankSurvivors));
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
        ///
        /// Deserter lockout: <paramref name="departure"/> names the exit for the deserter table when it differs from what
        /// <paramref name="how"/> stands for (a server-caused eject recorded as a forfeit); null takes <see cref="PvpDeserterRules.DepartureOf"/>.
        /// </summary>
        private void MarkOut(MatchRuntime m, Seat seat, ParticipantExit how, DateTime now, uint killerId, PvpDeparture? departure = null)
        {
            if (seat.Participant.ExitReason.HasValue)
                return;

            // A held seat that goes out (its grace ran out, its rejoin failed, or any other exit) is no longer held.
            ClearAway(seat);

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

                    // Conduct signals (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): a Tugak Brawl elimination is the killer's killshot in the
                    // ledger. Arena matches only (a battleground death credits its killshot in HandleBattlegroundDeath), Live only, and only
                    // across teams; 1v1 and 2v2 carry no ledger, so this is Tugak alone.
                    if (!m.Mode.IsObjective && m.Match.State == PvpMatchState.Live && killer.TeamIndex != seat.TeamIndex)
                        m.Match.Performance?.CreditKillshot(killer.Id, null, false);
                }
                else
                    line = PvpArenaText.Fill(PvpArenaText.EliminationNoKiller, ("victim", seat.Name));
            }
            else if (how == ParticipantExit.ForfeitInactive)
                line = PvpArenaText.Fill(PvpArenaText.RemovedInactive, ("name", seat.Name));
            else if (how == ParticipantExit.ForfeitFeeding)
                line = PvpArenaText.Fill(PvpArenaText.RemovedFeeding, ("name", seat.Name));
            else
                line = PvpArenaText.Fill(PvpArenaText.Forfeit, ("name", seat.Name));

            log.Info($"[PVP] match {m.Id}: {seat.Name} out ({how}{(killerId != 0 ? $", killer 0x{killerId:X8}" : "")}) during {m.Match.State}");

            // A walkout or logout after being placed but before Live is a dodge: locked out like a decliner.
            if ((m.Match.State == PvpMatchState.Staging || m.Match.State == PvpMatchState.Countdown)
                && (how == ParticipantExit.ForfeitLeft || how == ParticipantExit.ForfeitLogout))
                LockOut(seat.Id, now, Dials(), $"left match {m.Id} during {m.Match.State}");

            // Deserter lockout: decided after the win condition has recorded the exit (so a seat already out can never be struck twice) and
            // before the sends, the binding publish and the exit seams below, so a failing one of those never skips the strike.
            var deserterStrike = ApplyDeserterStrike(m, seat, how, departure, now);

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

                // Conduct removals (Docs/Pvp/BATTLEGROUNDS.md "Conduct"): returned like /arena leave. A seat dying on its way to the pen
                // (a feeding removal is decided at the death) is never teleported here: ExitSeat leaves it to the death return.
                case ParticipantExit.ForfeitInactive:
                    players.Send(seat.Id, PvpArenaText.RemovedInactiveYou);
                    ExitSeat(m, seat, "removed for inactivity", teleport: true);
                    break;

                case ParticipantExit.ForfeitFeeding:
                    players.Send(seat.Id, PvpArenaText.RemovedFeedingYou);
                    ExitSeat(m, seat, "removed for feeding", teleport: true);
                    break;
            }

            if (deserterStrike != null)
                TellDeserterStrike(seat, deserterStrike);
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

            // Thread return (Docs/Pvp/BATTLEGROUNDS.md "Pulled from a Thread"): every exit ends the player's participation, once.
            EndParticipation(m, seat);

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
                    EndParticipation(m, seat);
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
        /// The repeat-pairing cap's one-line notice (<see cref="PvpArenaText.UnpaidRematchNotice"/>) to each flagged seat still in
        /// the match when it goes Live.
        /// </summary>
        private void NotifyUnpaidRematches(MatchRuntime m)
        {
            foreach (var seat in m.Seats.Values)
                if (seat.UnpaidRematch && seat.Dispatched && !seat.Participant.ExitReason.HasValue)
                    players.Send(seat.Id, PvpArenaText.UnpaidRematchNotice);
        }

        /// <summary>
        /// The arena announcement sent to every fighter when the match goes Live: each team's members with their
        /// templates, teams separated by "vs". FFA lists everyone.
        /// </summary>
        private static string BuildLineupLine(MatchRuntime m)
        {
            var active = m.Seats.Values.Where(s => s.Dispatched && !s.Participant.ExitReason.HasValue).ToList();

            string lineup;

            // Team colours (Docs/Pvp/TEMPLATES.md "Team hue"): a two-team lineup tags each side with its colour; Tugak Brawl,
            // where everyone wears the same colour, gets no tag.
            if (m.Mode.TeamCountRange.Max > 2)
                lineup = string.Join(", ", active.OrderBy(s => s.TeamIndex).Select(NameWithTemplate));
            else
                lineup = string.Join(" vs ", active.GroupBy(s => s.TeamIndex).OrderBy(g => g.Key)
                    .Select(g => PvpArenaText.LineupTeam(LineupTeamTag(m, g.Key, g.First().Colour), string.Join(" and ", g.Select(NameWithTemplate)))));

            if (m.Seats.Values.Any(s => s.UnpaidRematch))
                lineup += PvpArenaText.UnpaidRematchTag;

            return PvpArenaText.Fill(PvpArenaText.Lineup, ("mode", PvpArenaText.ModeLabel(m.ModeKey)), ("lineup", lineup));
        }

        /// <summary>
        /// One side's tag on a two-team lineup: "Attackers (Red)" where the mode keeps its own team names (Attack/Defend), "Red team"
        /// everywhere else (1v1, 2v2, King of the Hill).
        /// </summary>
        private static string LineupTeamTag(MatchRuntime m, int teamIndex, PvpTeamColour colour)
        {
            if (PvpTeamColours.RuleFor(m.ModeKey) == PvpColourRule.AttackDefend && m.TickHandler is IObjectiveModeHandler handler)
                return PvpArenaText.LineupModeTeamTag(handler.TeamName(teamIndex), colour);

            return PvpArenaText.LineupColourTeamTag(colour);
        }

        /// <summary>
        /// Splits the seats into the winners and everyone else (forfeiters included, even on the winning team). Shared by the
        /// Discord line and the Arena Crier result line so the two never group differently.
        /// </summary>
        private static (List<Seat> Winners, List<Seat> Losers) SplitResultSeats(MatchRuntime m, MatchOutcome outcome)
        {
            var winners = new List<Seat>();
            var losers = new List<Seat>();

            foreach (var seat in m.Seats.Values)
            {
                var forfeited = IsForfeit(seat.Participant.ExitReason);
                var won = outcome.WinningTeams.Contains(seat.TeamIndex) && !forfeited;

                (won ? winners : losers).Add(seat);
            }

            return (winners, losers);
        }

        /// <summary>
        /// A side's name on the Crier result line: "Red team" (1v1 prints bare names, so null), or the mode's own team name
        /// ("Attackers", "Defenders") where it keeps one.
        /// </summary>
        private static string ResultTeamLabel(MatchRuntime m, Seat seat)
        {
            if (m.ModeKey == ArenaMapCatalog.OneVOneKey)
                return null;

            if (PvpTeamColours.RuleFor(m.ModeKey) == PvpColourRule.AttackDefend && m.TickHandler is IObjectiveModeHandler handler)
                return handler.TeamName(seat.TeamIndex);

            return PvpArenaText.LineupColourTeamTag(seat.Colour);
        }

        private static PvpResultSide ResultSide(MatchRuntime m, IReadOnlyList<Seat> seats)
            => new PvpResultSide(seats.Count > 0 ? ResultTeamLabel(m, seats[0]) : null, seats.Select(s => s.Name).ToList());

        /// <summary>The one-line /lfg result the Arena Crier posts for a resolved match (see <see cref="PvpMatchResultLine"/>).</summary>
        private static string BuildMatchResultCrierLine(MatchRuntime m, MatchOutcome outcome)
        {
            var modeLabel = PvpArenaText.ModeLabel(m.ModeKey);
            var (winners, losers) = SplitResultSeats(m, outcome);

            if (m.Mode.TeamCountRange.Max > 2)
            {
                if (outcome.IsDraw)
                    return PvpMatchResultLine.Build(modeLabel, PvpResultKind.Draw);

                return winners.Count == 0
                    ? PvpMatchResultLine.Build(modeLabel, PvpResultKind.NoWinner)
                    : PvpMatchResultLine.Build(modeLabel, PvpResultKind.FfaWin, new[] { new PvpResultSide(null, winners.Select(s => s.Name).ToList()) }, losers.Count);
            }

            if (outcome.IsDraw)
            {
                var sides = m.Seats.Values.GroupBy(s => s.TeamIndex).OrderBy(g => g.Key).Select(g => ResultSide(m, g.ToList())).ToList();

                return PvpMatchResultLine.Build(modeLabel, PvpResultKind.Draw, sides);
            }

            if (winners.Count == 0)
                return PvpMatchResultLine.Build(modeLabel, PvpResultKind.NoWinner);

            // The losing side is the seats on the non-winning teams. A forfeiter from the winning team is on NEITHER side (it would
            // otherwise print under the losing team's label); the Discord line keeps its own grouping. No losing side = no winner line.
            var losingSeats = losers.Where(s => !outcome.WinningTeams.Contains(s.TeamIndex)).ToList();

            if (losingSeats.Count == 0)
                return PvpMatchResultLine.Build(modeLabel, PvpResultKind.NoWinner);

            var loserSide = ResultSide(m, losingSeats);

            return PvpMatchResultLine.Build(modeLabel, PvpResultKind.Win, new[] { ResultSide(m, winners), loserSide });
        }

        private static string BuildMatchResultDiscordMessage(MatchRuntime m, MatchOutcome outcome)
        {
            var modeLabel = PvpArenaText.ModeLabel(m.ModeKey);

            if (outcome.IsDraw)
            {
                var everyone = string.Join(", ", m.Seats.Values.Select(NameWithTemplate));
                return $"[Arena] {modeLabel} match ended in a draw: {everyone}.";
            }

            var (winnerSeats, loserSeats) = SplitResultSeats(m, outcome);
            var winners = winnerSeats.Select(NameWithTemplate).ToList();
            var losers = loserSeats.Select(NameWithTemplate).ToList();

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

            // Battleground rejoin (Docs/Pvp/BATTLEGROUNDS.md "Disconnect and rejoin"): a seat still held when the match is decided is a
            // participant like any other (no ExitReason, so not a forfeit): its team result, rating and payout below are a present seat's.
            var held = m.Seats.Values.Where(s => s.IsAway && !s.Participant.ExitReason.HasValue).Select(s => s.Name).ToList();

            if (held.Count > 0)
                log.Info($"[PVP] match {m.Id}: resolved with {held.Count} seat(s) held for a rejoin, scored as present: {string.Join(", ", held)}");


            // The match is decided: a pre-Live resolve (a walkout in Staging or Countdown) must not wall the survivors in until CloseNow.
            // CloseNow keeps its own call as an idempotent backstop.
            RemoveStartGates(m, "match resolved");

            Publish(m, PvpMatchState.Resolving);

            IReadOnlyDictionary<uint, SeatPerformanceModifier> perfModifiers = null;
            IReadOnlyDictionary<uint, double> appliedPerf = null;

            if (m.Mode.IsObjective)
            {
                // Battlegrounds: their own result line (never the FFA timeout text), and the team score to the log only.
                log.Info($"[PVP] match {m.Id} ({m.ModeKey}): final score [{string.Join(",", m.Match.ScoreBoard.OrderBy(s => s.Key).Select(s => s.Key + ":" + s.Value))}], team kills [{string.Join(",", m.Match.TeamKills.OrderBy(k => k.Key).Select(k => k.Key + ":" + k.Value))}], target {m.ScoreTarget}");

                // The stats first, on their own, so they are in the log whatever happens to the rating below.
                LogPerformance(m);

                // Rating and performance (Docs/Pvp/BATTLEGROUNDS.md): each seat's performance modifier from the ledger, read when the match
                // resolves. Null (every modifier 0, so the deltas are exactly team Elo) when pvp_bg_perf_enabled is off or there is no ledger.
                perfModifiers = BuildPerformanceModifiers(m);

                // The mode's own result line (Docs/Pvp/ATTACK-DEFEND.md "Mode seam"): King of the Hill returns exactly the score win,
                // timeout draw and timeout win it always sent; Attack/Defend its attackers' and defenders' lines; null for anything else.
                var objectiveLine = (m.TickHandler as IObjectiveModeHandler)?.ResultLine(outcome, m.Match.ScoreBoard);

                if (objectiveLine != null)
                    SendAll(m.PlacedIds, objectiveLine);
            }
            else
            {
                // Conduct signals: a Tugak Brawl ledger is logged like a battleground's (a no-op for 1v1 and 2v2, which carry none).
                LogPerformance(m);

                if (outcome.Reason == EndReason.Timeout)
                    SendAll(m.PlacedIds, outcome.IsDraw ? PvpArenaText.TimeUpDraw : PvpArenaText.TimeUpFfa);
            }

            // Ratings: decay-on-read inputs, each player's own K, the forfeiter's S = 0 in a two-team mode.
            var views = m.Seats.Values.ToDictionary(s => s.Id, s => Ratings.Read(s.Id, m.Mode.LadderKey, now, dials));
            IReadOnlyDictionary<uint, int> deltas = null;

            if (rated)
            {
                var ratedTeams = m.Match.Teams.Select(t => new RatedTeam(t.TeamIndex, t.Members.Select(p =>
                {
                    var v = views[p.CharacterId];
                    var k = EloRating.SelectK(v.Games, dials.RatingProvisionalGames, dials.RatingKProvisional, dials.RatingKEstablished);
                    var perf = perfModifiers != null && perfModifiers.TryGetValue(p.CharacterId, out var pm) ? pm.Modifier : 0.0;
                    return new RatedPlayer(p.CharacterId, v.Rating, k, p.IpKey, ForceZeroScore: twoTeam && IsForfeit(p.ExitReason), PerformanceModifier: perf);
                }).ToList())).ToList();

                if (m.Mode.Rating is PerformanceTeamEloModel performanceModel)
                {
                    var computed = performanceModel.Compute(ratedTeams, outcome);
                    deltas = computed.Deltas;
                    appliedPerf = computed.AppliedModifiers;
                }
                else
                    deltas = m.Mode.Rating.Deltas(ratedTeams, outcome);
            }

            // Rating and performance: a second line per seat with P, m and the modifier the rating applied, once it is known.
            if (perfModifiers != null)
                LogPerformanceModifiers(m, perfModifiers, appliedPerf);

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
                {
                    // Rating and performance: the applied modifier as a signed whole percent, only when it moved the change.
                    var perfPct = appliedPerf != null && appliedPerf.TryGetValue(seat.Id, out var applied) ? BattlegroundPerformanceModifier.ChatPercent(applied) : null;
                    var deltaText = (after.Value - before.Value).ToString("+0;-0;+0");

                    players.Send(seat.Id, perfPct != null
                        ? PvpArenaText.Fill(PvpArenaText.RatingChangePerformance, ("ladder", ladderLabel), ("before", before.Value), ("after", after.Value), ("delta", deltaText), ("perf", perfPct))
                        : PvpArenaText.Fill(PvpArenaText.RatingChange, ("ladder", ladderLabel), ("before", before.Value), ("after", after.Value), ("delta", deltaText)));
                }
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

            AuditAlternation(m, outcome, now);

            // The arena Blood payout (Docs/Pvp/DESIGN.md "Rewards"). AFTER the save, for the same reason as the Discord
            // post: nothing in it may ever stand between a decided match and its saved result. Never in the Discord
            // text either - the feed carries no reward text.
            if (m.Mode.PaysBlood)
            {
                // Counted BEFORE the payout so a throw in the payout cannot skip it (RecordPairings is idempotent via PairCounted).
                RecordPairings(m, sameIpOpponents);
                PayArenaBlood(m, outcome, dials, twoTeam, sameIpOpponents);
            }
            else if (m.Mode.IsObjective)
                PayBattlegroundMarks(m, outcome, dials, twoTeam, sameIpOpponents);
            else
            {
                // A mode that pays neither Blood nor battleground Marks: nothing to pay, and the match is marked paid
                // so no later path can.
                m.BloodPaid = true;
                log.Info($"[PVP] match {m.Id}: no Blood paid ({m.ModeKey} does not pay)");
            }

            // PvP dailies (PvpDailyCatalog): each eligible seat's facts, built once here beside the payouts and applied on the player's
            // own action chain. Last, and guarded on its own, so nothing in it can stand between the match and its save or its payout.
            DispatchDailyFacts(m, outcome, twoTeam, sameIpOpponents);

            // The Arena Crier result line on /lfg. Very last, after the save, the result sends, the payout and the daily facts, in its own
            // try/catch: a failure here must never reach any of them. Only a match that went Live is announced (a walkout during Staging or
            // Countdown posts nothing publicly). Gated by pvp_arena_enabled and pvp_arena_crier_results_enabled, never by
            // pvp_arena_crier_enabled (same as the Double Marks lines).
            try
            {
                if (dials.Enabled && dials.CrierResultsEnabled && m.Match.LiveSinceUtc.HasValue)
                    arenaAnnouncer.Announce(new[] { BuildMatchResultCrierLine(m, outcome) }, dials.CrierSenderName);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: the Crier result announcement failed; the match result above was already saved", ex);
            }
        }

        /// <summary>
        /// Test seam: runs the daily facts dispatch of an already-resolved match again, with the arguments Resolve used, as a second
        /// Resolve (or any later path) would. The <see cref="MatchRuntime.DailyFactsDispatched"/> guard must make it a no-op.
        /// </summary>
        internal void RedispatchDailyFactsForTest(Guid matchId)
        {
            var m = matches.FirstOrDefault(x => x.Id == matchId);

            if (m?.Outcome == null)
                return;

            var twoTeam = m.Mode.TeamCountRange.Max == 2;
            var sameIpOpponents = twoTeam && dialSource().BlockSameIp == false && HasCrossTeamSameIp(m);

            DispatchDailyFacts(m, m.Outcome, twoTeam, sameIpOpponents);
        }

        /// <summary>
        /// The PvP daily facts of a resolved match (PvpMatchFacts), exactly once per match: guarded by
        /// <see cref="MatchRuntime.DailyFactsDispatched"/>, set before the first dispatch. A seat gets facts only when the shared
        /// <see cref="PvpRewardEligibility"/> rule passes - the same rule as the Blood and Marks payouts (went Live, no same-IP
        /// opponents, no forfeit, no unpaid rematch, not withheld by the contribution floor) - and NOT gated on pvp_arena_blood_enabled,
        /// which switches the currency payout only. The one exception: a seat that forfeited a Live 1v1 gets the minimal forfeit fact
        /// (<see cref="PvpMatchFacts.ForfeitOf"/>), which only resets GladiatorsStreak. The peak rating is read from the rating store here, on the world thread. Each seat
        /// has its own try/catch; the whole thing is best effort.
        /// </summary>
        private void DispatchDailyFacts(MatchRuntime m, MatchOutcome outcome, bool twoTeam, bool sameIpOpponents)
        {
            if (m.DailyFactsDispatched)
            {
                log.Warn($"[PVP] match {m.Id}: daily facts already dispatched; not dispatching again");
                return;
            }

            m.DailyFactsDispatched = true;

            try
            {
                var wentLive = m.Match.LiveSinceUtc.HasValue;

                if (!wentLive || sameIpOpponents)
                {
                    log.Info($"[PVP] match {m.Id}: no daily facts ({(!wentLive ? "the match never went Live" : "same-IP opponents")})");
                    return;
                }

                // When this is the floor's first evaluation, the payout never reached it (the master switch is off, the mode pays nothing,
                // or the payout threw first), so no seat is owed Marks and a below-floor seat is logged "below floor", not "WITHHELD".
                var floor = EvaluateContributionFloor(m, amountFor: _ => 0);
                var stats = m.Match.Performance?.Snapshot().ToDictionary(s => s.CharacterId) ?? new Dictionary<uint, SeatPerformanceSnapshot>();
                var mode = PvpMatchFacts.ModeOf(m.ModeKey);
                var sent = new List<string>();

                foreach (var seat in m.Seats.Values)
                {
                    try
                    {
                        var forfeited = IsForfeit(seat.Participant.ExitReason);
                        var exclusion = PvpRewardEligibility.Evaluate(wentLive, sameIpOpponents, forfeited, seat.UnpaidRematch, BelowFloor(floor, seat.Id));

                        if (exclusion != PvpRewardExclusion.None)
                        {
                            // A Live 1v1 forfeit (same-IP matches already returned above) is a loss for GladiatorsStreak only: the
                            // forfeiter gets the minimal forfeit fact, which resets the streak and advances nothing else. Without it a
                            // forfeit would dodge the reset a loss gets. Every other exclusion gets nothing.
                            if (exclusion == PvpRewardExclusion.Forfeited && mode == PvpDailyMode.OneVOne)
                            {
                                players.ApplyDailyFacts(PvpMatchFacts.ForfeitOf(m.Id, seat.Id, seat.TeamIndex));
                                sent.Add($"{seat.Name} forfeit (streak reset only)");
                                continue;
                            }

                            sent.Add($"{seat.Name} skipped ({exclusion})");
                            continue;
                        }

                        var won = !outcome.IsDraw && outcome.WinningTeams.Contains(seat.TeamIndex);
                        var result = PvpArenaRewards.ResultFor(outcome.IsDraw, twoTeam, won, outcome.Placements[seat.TeamIndex]);
                        stats.TryGetValue(seat.Id, out var s);

                        // 1v1 only: the one opponent's repeat-pairing ACCOUNT key (never the character), for ProvingGround and GladiatorsStreak.
                        uint? opponentAccount = null;

                        if (mode == PvpDailyMode.OneVOne)
                        {
                            var opponents = m.Seats.Values.Where(o => o.TeamIndex != seat.TeamIndex).ToList();

                            if (opponents.Count == 1)
                                opponentAccount = opponents[0].Unit.AccountOf(opponents[0].Id);
                        }

                        var facts = new PvpMatchFacts(
                            MatchId: m.Id,
                            CharacterId: seat.Id,
                            Mode: mode,
                            TeamIndex: seat.TeamIndex,
                            Result: result,
                            Kills: seat.Kills,
                            HillSeconds: s.HillSeconds,
                            CrystalDamage: s.CrystalDamage,
                            KillshotsNearCrystal: s.KillshotsNearCrystal,
                            KillshotsOnHill: s.KillshotsOnHill,
                            OpponentAccount: opponentAccount,
                            PeakRating: Ratings.PeakAcrossLadders(seat.Id));

                        players.ApplyDailyFacts(facts);
                        sent.Add($"{seat.Name} {result.ToString().ToLowerInvariant()}");
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[PVP] match {m.Id}: building the daily facts for {seat.Name} (0x{seat.Id:X8}) threw; nobody gets them twice", ex);
                    }
                }

                log.Info($"[PVP] match {m.Id}: daily facts dispatched: {string.Join("; ", sent)}");
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: dispatching the daily facts threw", ex);
            }
        }

        /// <summary>
        /// Performance stats (Docs/Pvp/BATTLEGROUNDS.md "Performance stats"): one "[PVP] match &lt;id&gt; perf:" line per seat at Resolve,
        /// written through <see cref="PerformanceLogSink"/> (the log by default). Reads the ledger only; nothing here touches a rating,
        /// a score or a payout. Written BEFORE the rating is computed, so the stats survive anything that goes wrong there. Best effort: a
        /// throw is logged and Resolve carries on.
        /// </summary>
        private void LogPerformance(MatchRuntime m)
        {
            var perf = m.Match.Performance;

            if (perf == null)
                return;

            try
            {
                foreach (var s in perf.Snapshot())
                {
                    var name = m.Seats.TryGetValue(s.CharacterId, out var seat) ? seat.Name : "?";
                    var line = BattlegroundPerformanceLedger.FormatLogLine(m.Id, name, s);

                    if (PerformanceLogSink != null)
                        PerformanceLogSink(line);
                    else
                        log.Info(line);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: writing the performance stats threw", ex);
            }
        }

        /// <summary>
        /// Rating and performance (Docs/Pvp/BATTLEGROUNDS.md "Rating and performance"): one "[PVP] match &lt;id&gt; perf_mod:" line per seat after
        /// the rating is computed, through <see cref="PerformanceLogSink"/>: P, m before the team's zero-sum pass, and the modifier the rating
        /// applied ("-" when the match was unrated). Best effort: a throw is logged and Resolve carries on.
        /// </summary>
        private void LogPerformanceModifiers(MatchRuntime m, IReadOnlyDictionary<uint, SeatPerformanceModifier> modifiers, IReadOnlyDictionary<uint, double> applied)
        {
            try
            {
                foreach (var mod in modifiers.Values.OrderBy(x => x.TeamIndex).ThenBy(x => x.CharacterId))
                {
                    var name = m.Seats.TryGetValue(mod.CharacterId, out var seat) ? seat.Name : "?";
                    var line = BattlegroundPerformanceLedger.FormatModifierLine(m.Id, name, mod.CharacterId, mod.TeamIndex, mod.Score, mod.Modifier,
                        applied != null && applied.TryGetValue(mod.CharacterId, out var a) ? a : (double?)null);

                    if (PerformanceLogSink != null)
                        PerformanceLogSink(line);
                    else
                        log.Info(line);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: writing the performance modifiers threw", ex);
            }
        }

        /// <summary>
        /// Rating and performance (Docs/Pvp/BATTLEGROUNDS.md): every seat's performance modifier from the ledger, by the pure
        /// <see cref="BattlegroundPerformanceModifier.Modifiers"/> under the pvp_bg_perf_* settings read now. A seat is a forfeiter by the same rule the
        /// rating uses (<see cref="IsForfeit"/>) and eligible only once dispatched. Null when pvp_bg_perf_enabled is off, there is no ledger, or
        /// the computation throws (logged): the deltas are then exactly team Elo.
        /// </summary>
        private IReadOnlyDictionary<uint, SeatPerformanceModifier> BuildPerformanceModifiers(MatchRuntime m)
        {
            var ledger = m.Match.Performance;

            if (ledger == null)
                return null;

            try
            {
                PerformanceModifierFaultForTest?.Invoke(m.Id);

                var dials = PerformanceDials.From(BgDials());

                if (dials == null || !dials.Enabled)
                    return null;

                return BattlegroundPerformanceModifier.Modifiers(m.ModeKey, BuildPerformanceSeats(m, ledger), dials);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: computing the performance modifiers threw; rating this match on team Elo alone", ex);
                return null;
            }
        }

        /// <summary>Test seam: called inside <see cref="EvaluateContributionFloor"/>'s try (floor on), so a test can make it throw. Null in production.</summary>
        internal Action<Guid> ContributionFloorFaultForTest { get; set; }

        /// <summary>
        /// Every seat of the match with its final ledger counters (zeros when the ledger has no row), as the performance modifier, the
        /// contribution floor and the feeding check all read them: a forfeiter by <see cref="IsForfeit"/>, dispatched from the seat.
        /// </summary>
        private static List<PerformanceSeat> BuildPerformanceSeats(MatchRuntime m, BattlegroundPerformanceLedger ledger)
        {
            var stats = ledger.Snapshot().ToDictionary(s => s.CharacterId);
            var seats = new List<PerformanceSeat>();

            foreach (var team in m.Match.Teams)
                foreach (var p in team.Members)
                {
                    var dispatched = m.Seats.TryGetValue(p.CharacterId, out var seat) && seat.Dispatched;
                    var s = stats.TryGetValue(p.CharacterId, out var snap) ? snap : new SeatPerformanceSnapshot(p.CharacterId, team.TeamIndex, 0, 0, 0, 0, 0, 0, 0, 0, 0);

                    seats.Add(new PerformanceSeat(p.CharacterId, team.TeamIndex, s, p.IpKey, IsForfeit(p.ExitReason), dispatched));
                }

            return seats;
        }

        /// <summary>Test seam: called inside <see cref="BuildPerformanceModifiers"/>'s try, so a test can make it throw. Null in production.</summary>
        internal Action<Guid> PerformanceModifierFaultForTest { get; set; }

        /// <summary>Test seam: receives each performance line instead of the log. Null in production.</summary>
        internal Action<string> PerformanceLogSink { get; set; }

        /// <summary>
        /// Contribution floor (Docs/Pvp/BATTLEGROUNDS.md "Contribution floor"): each non-forfeiting seat's decision from the final ledger and the
        /// pvp_conduct_floor_* dials read now, logged next to the perf lines. Empty (nobody withheld) when the floor is off, there is no ledger
        /// (1v1, 2v2) or the computation throws (logged): a fault never costs a player their pay. amountFor says how many Marks each seat is
        /// owed, so a below-floor seat owed 0 is logged "below floor" rather than "WITHHELD"; it is read only by the FIRST call of a match,
        /// the one that evaluates and logs (the payout when it reaches the floor, otherwise the daily facts).
        /// </summary>
        private IReadOnlyDictionary<uint, FloorDecision> EvaluateContributionFloor(MatchRuntime m, Func<uint, int> amountFor)
        {
            // Decided once per match at Resolve: the payout and the PvP daily facts both read it, and it logs its lines only once.
            if (m.FloorDecisions == null)
                m.FloorDecisions = EvaluateContributionFloorNow(m, amountFor);

            return m.FloorDecisions;
        }

        private static bool BelowFloor(IReadOnlyDictionary<uint, FloorDecision> floor, uint characterId) => floor.TryGetValue(characterId, out var fd) && fd.BelowFloor;

        private IReadOnlyDictionary<uint, FloorDecision> EvaluateContributionFloorNow(MatchRuntime m, Func<uint, int> amountFor)
        {
            var empty = new Dictionary<uint, FloorDecision>();
            var ledger = m.Match.Performance;

            if (ledger == null)
                return empty;

            try
            {
                var conduct = conductDialSource() ?? PvpConductTunables.Defaults;

                if (!conduct.FloorEnabled)
                    return empty;

                ContributionFloorFaultForTest?.Invoke(m.Id);

                // The Live duration at Resolve scales the absolute threshold (a match the opponents forfeit early cannot withhold the winners).
                var liveSeconds = m.Match.LiveSinceUtc.HasValue ? (m.ResolvedAtUtc - m.Match.LiveSinceUtc.Value).TotalSeconds : 0.0;
                var decisions = PvpConductFloor.Evaluate(m.ModeKey, BuildPerformanceSeats(m, ledger), conduct, PerformanceDials.From(BgDials()), liveSeconds);

                foreach (var d in decisions.Values.OrderBy(x => x.CharacterId))
                {
                    var name = m.Seats.TryGetValue(d.CharacterId, out var seat) ? seat.Name : "?";
                    // With nothing owed (a 0 amount) the seat is not WITHHELD anything: the floor is still evaluated (the dailies engine reads it), the log just says so.
                    var line = PvpConductFloor.FormatLogLine(m.Id, name, d, nothingOwed: amountFor != null && amountFor(d.CharacterId) <= 0);

                    if (PerformanceLogSink != null)
                        PerformanceLogSink(line);
                    else
                        log.Info(line);
                }

                return decisions;
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match {m.Id}: evaluating the contribution floor threw; nobody is withheld", ex);
                return empty;
            }
        }

        /// <summary>A seat's payout inputs, computed once per payout by <see cref="ComputeSeatPayouts"/>.</summary>
        private readonly record struct SeatPayout(bool Forfeited, PvpBloodResult Result, int Amount);

        /// <summary>Test seam: called inside <see cref="ComputeSeatPayouts"/>'s per-seat try, so a test can make one seat's computation throw. Null in production.</summary>
        internal Action<uint> SeatPayoutFaultForTest { get; set; }

        /// <summary>
        /// Each seat's forfeit, result and base amount (before the contribution floor), computed ONCE per payout: the floor's
        /// amountFor and the pay loop both read this one table, so the amount rule is written once. Each seat has its own try/catch:
        /// a seat whose computation throws is logged and left out (it is paid nothing), and no other seat is affected - in particular
        /// the floor still runs for everyone else.
        /// </summary>
        private Dictionary<uint, SeatPayout> ComputeSeatPayouts(MatchRuntime m, MatchOutcome outcome, bool twoTeam, string currency, Func<Seat, bool, PvpBloodResult, int> amountOf)
        {
            var pays = new Dictionary<uint, SeatPayout>();

            foreach (var seat in m.Seats.Values)
            {
                try
                {
                    SeatPayoutFaultForTest?.Invoke(seat.Id);

                    var forfeited = IsForfeit(seat.Participant.ExitReason);
                    var won = !outcome.IsDraw && outcome.WinningTeams.Contains(seat.TeamIndex) && !forfeited;
                    var result = PvpArenaRewards.ResultFor(outcome.IsDraw, twoTeam, won, outcome.Placements[seat.TeamIndex]);

                    pays[seat.Id] = new SeatPayout(forfeited, result, amountOf(seat, forfeited, result));
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] match {m.Id}: computing the {currency} amount for {seat.Name} (0x{seat.Id:X8}) threw; that seat is paid nothing", ex);
                }
            }

            return pays;
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
            // Decided by when the match went LIVE, not when it resolves (Docs/Pvp/BATTLEGROUNDS.md "Double Marks windows").
            var bonus = BgBonusConfig.IsBonusMatch(bg, m.Match.LiveSinceUtc.Value) ? PvpArenaRewards.SanitizeBonusMultiplier(bg.BonusMultiplier) : 1.0;
            var grant = new PvpBloodGrant(m.Id, bg.MarksDailyCap, dials.BloodResetTimezone, dials.BloodResetHour, PvpBloodLedgerKind.Battleground, bonus > 1.0);
            var paid = new List<string>();
            var seatPays = ComputeSeatPayouts(m, outcome, twoTeam, "Marks", (s, forfeited, result) =>
                PvpArenaRewards.BgMarksFor(result, forfeited, wentLive, sameIpOpponents, dials.BloodEnabled, bg, withheld: false, bonusMultiplier: bonus));
            var floor = EvaluateContributionFloor(m, id => seatPays.TryGetValue(id, out var sp) ? sp.Amount : 0);

            foreach (var seat in m.Seats.Values)
            {
                // A seat whose amount could not be computed was logged by ComputeSeatPayouts and is paid nothing; nobody else is affected.
                if (!seatPays.TryGetValue(seat.Id, out var pay))
                    continue;

                try
                {
                    var forfeited = pay.Forfeited;
                    var result = pay.Result;
                    var baseAmount = pay.Amount;
                    // The shared eligibility rule (PvpRewardEligibility, also read by the PvP daily facts). A positive base already means
                    // Live, no same-IP, no forfeit; a battleground seat is never an unpaid rematch; so this is exactly the floor test.
                    // The floor is evaluated even at a 0 base amount (the daily facts read it too); nothing is owed then, so only the log notes it.
                    var belowFloor = BelowFloor(floor, seat.Id);
                    var exclusion = PvpRewardEligibility.Evaluate(wentLive, sameIpOpponents, forfeited, seat.UnpaidRematch, belowFloor);
                    var withheld = baseAmount > 0 && exclusion == PvpRewardExclusion.BelowContributionFloor;
                    var amount = withheld ? 0 : baseAmount;

                    if (withheld)
                        players.Send(seat.Id, PvpArenaText.ContributionFloorWithheld);

                    paid.Add($"{seat.Name} {(forfeited ? "forfeit" : result.ToString().ToLowerInvariant())} {amount}{(withheld ? " (withheld: contribution floor)" : belowFloor ? " (below floor)" : "")}");

                    if (amount > 0)
                        players.GrantArenaBlood(seat.Id, amount, grant);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] match {m.Id}: the Marks grant for {seat.Name} (0x{seat.Id:X8}) threw; nobody is paid twice for it", ex);
                }
            }

            log.Info($"[PVP] match {m.Id}: Marks granted before the battleground daily cap{(bonus > 1.0 ? $" (Double Marks window, x{bonus})" : "")}: {string.Join("; ", paid)}");
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
            var seatPays = ComputeSeatPayouts(m, outcome, twoTeam, "Blood", (s, forfeited, result) =>
                PvpArenaRewards.BloodFor(result, forfeited, wentLive, sameIpOpponents, dials, withheld: s.UnpaidRematch));
            var floor = EvaluateContributionFloor(m, id => seatPays.TryGetValue(id, out var sp) ? sp.Amount : 0);

            foreach (var seat in m.Seats.Values)
            {
                // A seat whose amount could not be computed was logged by ComputeSeatPayouts and is paid nothing; nobody else is affected.
                if (!seatPays.TryGetValue(seat.Id, out var pay))
                    continue;

                try
                {
                    var forfeited = pay.Forfeited;
                    var result = pay.Result;
                    var amount = pay.Amount;

                    // Contribution floor: gated here, not in BloodFor. A withheld seat makes no grant call, so it never consumes the daily cap.
                    // An unpaid rematch already has amount 0 from BloodFor above, so the floor never fires (or messages) on top of it.
                    // The shared eligibility rule (PvpRewardEligibility, also read by the PvP daily facts). A positive amount already means
                    // Live, no same-IP, no forfeit and no unpaid rematch, so this is exactly the floor test. belowFloor alone only annotates the log.
                    var belowFloor = BelowFloor(floor, seat.Id);
                    var exclusion = PvpRewardEligibility.Evaluate(wentLive, sameIpOpponents, forfeited, seat.UnpaidRematch, belowFloor);
                    var withheld = amount > 0 && exclusion == PvpRewardExclusion.BelowContributionFloor;

                    if (withheld)
                    {
                        amount = 0;
                        players.Send(seat.Id, PvpArenaText.ContributionFloorWithheld);
                    }

                    paid.Add($"{seat.Name} {(forfeited ? "forfeit" : seat.UnpaidRematch ? "unpaid-rematch" : result.ToString().ToLowerInvariant())} {amount}{(withheld ? " (withheld: contribution floor)" : belowFloor ? " (below floor)" : "")}");

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
                case ParticipantExit.ForfeitInactive: return "inactive";
                case ParticipantExit.ForfeitFeeding: return "feeding";
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

            conductDialsThisTick = null;

            var facts = players.GetFacts(characterId);

            if (facts == null)
                return new PvpJoinResult(PvpJoinRefusal.Offline, key);

            if (duo && key != ArenaMapCatalog.TwoVTwoKey)
                return new PvpJoinResult(PvpJoinRefusal.DuoNotSupportedForMode, key);

            // pvp_bg_group_join_enabled (owner ruling 2026-10-15): refused before Admit and before anything is queued, for every
            // member whoever issues it. This is the ONLY place a multi-member battleground unit is created (the sole QueuedUnit
            // built from a fellowship for bg; duo is 2v2-only above), so the gate covers every caller. A premade ALREADY queued
            // when the setting is turned off is deliberately left alone: nothing here touches room.Units.
            // It sits ABOVE the wrong-mode check so "/arena join 2v2 group" does not answer "Use /arena join bg group", which
            // would advertise the disabled feature.
            if (group && !BgDials().GroupJoinEnabled)
                return new PvpJoinResult(PvpJoinRefusal.GroupJoinDisabled, key);

            if (group && key != BattlegroundModes.RoomKey)
                return new PvpJoinResult(PvpJoinRefusal.GroupNotSupportedForMode, key);

            var refusal = Admit(facts, key, now, dials, out var lockoutSeconds, out var queuedMode);

            if (refusal != PvpJoinRefusal.None)
            {
                // Multi-queue on: AlreadyQueued can only mean this same room, which has its own line (the enum stays AlreadyQueued).
                var detail = refusal == PvpJoinRefusal.AlreadyQueued && dials.MultiQueueEnabled
                    ? PvpArenaText.Fill(PvpArenaText.AlreadyQueuedSameMode, ("mode", PvpArenaText.ModeLabel(queuedMode ?? key)))
                    : null;

                return new PvpJoinResult(refusal, queuedMode ?? key, LockoutSecondsRemaining: lockoutSeconds, Detail: detail);
            }

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

            // Held units (a member's match is pending elsewhere) are not counted as waiting.
            var waitingCount = EligibleCount(room, HeldCharacters());
            var queuedModes = QueuedRoomKeys(characterId);

            log.Info($"[PVP] {string.Join(" + ", unit.Ids.Select(id => unit.NameOf(id) + (unit.TemplateOf(id) != null ? " [" + unit.TemplateOf(id) + "]" : "")))} joined the {key} queue{(duo ? " as a pair" : group ? $" as a group of {unit.Ids.Count}" : "")}; {waitingCount} waiting{(queuedModes.Count > 1 ? $"; now queued in {string.Join(", ", queuedModes)}" : "")}");

            return new PvpJoinResult(PvpJoinRefusal.None, key, waitingCount, partnerName, TemplateKey: chosenTemplate, TemplateLabel: TemplateLabel(chosenTemplate), QueuedModeKeys: queuedModes);
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

            // The team hue changes only the look of worn pieces, never the item count, so the catalog definition is the room check.
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

            // pvp_arena_multi_queue_enabled: on, "already queued" means already in THIS room (one entry per room); off, in any room.
            // AlreadyInMatch is unchanged either way, so a player cannot add queues while a match they hold a seat in is pending.
            queuedMode = dials.MultiQueueEnabled
                ? (rooms.TryGetValue(modeKey, out var thisRoom) && thisRoom.Units.Any(u => u.Ids.Contains(facts.CharacterId)) ? thisRoom.ModeKey : null)
                : rooms.Values.FirstOrDefault(r => r.Units.Any(u => u.Ids.Contains(facts.CharacterId)))?.ModeKey;

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
                HasClearedThread: facts.HasClearedThread,
                IsDenylisted: PvpArenaDenylistTunables.IsDenylisted(facts.CharacterId),
                IsTemplateAccount: facts.IsTemplateAccount,
                IsPvpTemplated: facts.IsPvpTemplated);

            var refusal = PvpAdmission.Evaluate(request);

            if (refusal != PvpAdmissionRefusal.None)
                return MapRefusal(refusal);

            if (lockouts.TryGetValue(facts.CharacterId, out var until) && until > now)
            {
                lockoutSeconds = (int)Math.Ceiling((until - now).TotalSeconds);
                return PvpJoinRefusal.DeclineLockout;
            }

            // Deserter lockout (Docs/Pvp/BATTLEGROUNDS.md "Deserter lockout"): battlegrounds and Tugak Brawl only.
            var deserterSeconds = DeserterSecondsRemaining(facts, modeKey, now);

            if (deserterSeconds > 0)
            {
                lockoutSeconds = (int)Math.Min(int.MaxValue, deserterSeconds);
                return PvpJoinRefusal.DeserterLockout;
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
                case PvpAdmissionRefusal.InRespite: return PvpJoinRefusal.InRespite;
                case PvpAdmissionRefusal.ActivePkTimer: return PvpJoinRefusal.ActivePkTimer;
                case PvpAdmissionRefusal.IsOlthoi: return PvpJoinRefusal.IsOlthoi;
                case PvpAdmissionRefusal.IsMule: return PvpJoinRefusal.IsMule;
                case PvpAdmissionRefusal.IsDead: return PvpJoinRefusal.IsDead;
                case PvpAdmissionRefusal.IsTeleporting: return PvpJoinRefusal.IsTeleporting;
                case PvpAdmissionRefusal.AlreadyQueued: return PvpJoinRefusal.AlreadyQueued;
                case PvpAdmissionRefusal.AlreadyInMatch: return PvpJoinRefusal.AlreadyInMatch;
                case PvpAdmissionRefusal.NoThreadCleared: return PvpJoinRefusal.NoThreadCleared;
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
                case PvpJoinRefusal.AlreadyQueued: return r.Detail ?? PvpArenaText.Fill(PvpArenaText.AlreadyQueued, ("mode", PvpArenaText.ModeLabel(r.ModeKey)));
                case PvpJoinRefusal.AlreadyInMatch: return PvpArenaText.AlreadyInMatch;
                case PvpJoinRefusal.AlreadyInInstance: return PvpArenaText.InInstance;
                case PvpJoinRefusal.InRespite:
                case PvpJoinRefusal.ActivePkTimer: return PvpArenaText.RecentPvp;
                case PvpJoinRefusal.NoThreadCleared: return PvpArenaText.NoThreadCleared;
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
                case PvpJoinRefusal.TemplateNotOffered: return r.Detail ?? PvpArenaText.Fill(PvpArenaText.TemplateNotOffered, ("template", r.TemplateKey ?? "(none)"), ("mode", PvpArenaText.ModeLabel(r.ModeKey)));
                case PvpJoinRefusal.TemplateNoRoom: return r.Detail ?? PvpArenaText.CannotJoinNow;
                case PvpJoinRefusal.GroupJoinDisabled: return BattlegroundText.GroupJoinDisabled;
                case PvpJoinRefusal.GroupNotSupportedForMode: return BattlegroundText.GroupNotSupportedForMode;
                case PvpJoinRefusal.GroupNeedsFellowship: return PvpArenaText.Fill(BattlegroundText.GroupNeedsFellowship, ("max", r.GroupMax));
                case PvpJoinRefusal.GroupMemberIneligible: return PvpArenaText.Fill(BattlegroundText.GroupMemberIneligible, ("member", r.PartnerName));
                case PvpJoinRefusal.DeserterLockout: return PvpArenaText.Fill(PvpArenaText.DeserterLockout, ("remaining", PvpArenaText.DeserterRemainingPhrase(r.LockoutSecondsRemaining)));
                default: return PvpArenaText.CannotJoinNow;
            }
        }

        /// <summary>
        /// /arena leave. With no <paramref name="modeKey"/> the MATCH is checked first (multi-queue: a held player's queues must not
        /// shadow a pending match): a pending match is declined, a placed one needs the confirmation. Otherwise every queue is left
        /// (a duo or group leaves together, as before). With <paramref name="modeKey"/> (a room key) only that queue is left, whatever
        /// the player's match state; NotInMode when they are not in it.
        /// </summary>
        public PvpLeaveResult Leave(uint characterId, string modeKey = null)
        {
            if (modeKey != null)
            {
                var key = modeKey.Trim().ToLowerInvariant();

                if (!rooms.TryGetValue(key, out var only))
                    return new PvpLeaveResult(PvpLeaveOutcome.NotInMode, key);

                var left = LeaveRoom(only, characterId, out var onlyPartner);

                return left
                    ? new PvpLeaveResult(PvpLeaveOutcome.LeftQueue, only.ModeKey, onlyPartner, ModeKeys: new[] { only.ModeKey })
                    : new PvpLeaveResult(PvpLeaveOutcome.NotInMode, only.ModeKey);
            }

            var m = FindMatchOf(characterId);

            if (m != null)
            {
                if (m.Match.State == PvpMatchState.AwaitingAccept || (m.Match.State == PvpMatchState.Staging && !m.Dispatched))
                {
                    DeclineBeforePlacement(m, characterId, fromPopup: false);
                    return new PvpLeaveResult(PvpLeaveOutcome.DeclinedMatch, m.ModeKey);
                }

                var seat = m.Seats[characterId];

                if (!(seat.Participant.ExitReason.HasValue || m.Match.State == PvpMatchState.Resolving))
                    return new PvpLeaveResult(PvpLeaveOutcome.InMatchNeedsConfirm, m.ModeKey, MatchId: m.Id);

                // Already out of the match: fall through, so any queue they still hold can be left.
                if (QueuedRoomKeys(characterId).Count == 0)
                    return new PvpLeaveResult(PvpLeaveOutcome.NotQueued, m.ModeKey);
            }

            var leftKeys = new List<string>();
            string partner = null;

            foreach (var room in OrderedRooms())
            {
                if (LeaveRoom(room, characterId, out var roomPartner))
                {
                    leftKeys.Add(room.ModeKey);
                    partner ??= roomPartner;
                }
            }

            if (leftKeys.Count == 0)
                return new PvpLeaveResult(PvpLeaveOutcome.NotQueued);

            return new PvpLeaveResult(PvpLeaveOutcome.LeftQueue, leftKeys[0], partner, ModeKeys: leftKeys);
        }

        /// <summary>Removes the unit holding <paramref name="characterId"/> from <paramref name="room"/> (a duo or group leaves together). True when there was one.</summary>
        private bool LeaveRoom(Room room, uint characterId, out string partner)
        {
            partner = null;

            var unit = room.Units.FirstOrDefault(u => u.Ids.Contains(characterId));

            if (unit == null)
                return false;

            room.Units.Remove(unit);
            room.LastFfaCountAnnounced = -1;

            partner = unit.Ids.Count > 1 ? unit.NameOf(unit.Ids.First(id => id != characterId)) : null;
            log.Info($"[PVP] {string.Join(" + ", unit.Names)} left the {room.ModeKey} queue");

            return true;
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
                // The PK facet is no longer re-checked here (owner ruling 2026-10-03: any facet may queue; owner ruling 2026-10-15:
                // one Thread clear replaces the level and PK facet requirements, for every mode).
                //
                // PvP Template Facets: the template is re-validated at the moment of accept (the second of three
                // checks: join, accept, dispatch), because an admin can disable it or change its modes after the join,
                // and the pack room is re-checked because the player can fill their pack while queued. A failure is
                // handled exactly like a decline. A withdrawn template is not the player's doing, so it carries no
                // lockout; a full pack is, so it locks out like any other decline. A facts-less (offline) player is
                // left to the ordinary offline handling at BeginStaging.
                var facts = players.GetFacts(characterId);

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

            conductDialsThisTick = null;
            var seat = m.Seats[characterId];

            seat.Answer = AcceptAnswer.Declined;
            log.Info($"[PVP] match {m.Id}: {seat.Name} declined{(fromPopup ? "" : " (chat)")} during {m.Match.State}{(lockOut ? "" : " (no lockout)")}");

            players.Send(characterId, declineText ?? PvpArenaText.YouDeclined);

            // Multi-queue: a player-caused decline (lockOut) leaves every queue; a template an admin withdrew (lockOut false) is no
            // fault of theirs, so their other queues are kept.
            if (lockOut)
                WithdrawForPlayerFault(characterId, PvpArenaText.RemovedOnDecline, $"declined match {m.Id}");

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

        /// <summary>
        /// Every queue <paramref name="characterId"/> waits in, in <see cref="RoomOrder"/>: time waited, the room's eligible (not held)
        /// count, the template queued on, and whether the entry is held by a pending match.
        /// </summary>
        private List<PvpQueueEntry> QueueEntries(uint characterId, DateTime now)
        {
            var held = HeldCharacters();
            var entries = new List<PvpQueueEntry>();

            foreach (var room in OrderedRooms())
            {
                var unit = room.Units.FirstOrDefault(u => u.Ids.Contains(characterId));

                if (unit == null)
                    continue;

                var template = unit.TemplateOf(characterId);

                entries.Add(new PvpQueueEntry(room.ModeKey, now - unit.QueuedAtUtc, EligibleCount(room, held), template, TemplateLabel(template), unit.Ids.Any(held.Contains)));
            }

            return entries;
        }

        /// <summary>
        /// /arena status. The MATCH is checked first (multi-queue: a pending match is what matters, and its player's other queues are
        /// held); <see cref="PvpStatusResult.Queues"/> carries every queue entry either way. Facet gates and suit transfer read only Kind.
        /// </summary>
        public PvpStatusResult Status(uint characterId)
        {
            var now = clock();
            var queues = QueueEntries(characterId, now);
            var m = FindMatchOf(characterId);

            if (m == null)
            {
                if (queues.Count == 0)
                    return new PvpStatusResult(PvpStatusKind.Idle, Queues: queues);

                var first = queues[0];

                return new PvpStatusResult(PvpStatusKind.Queued, first.ModeKey, first.Waited, first.WaitingCount, Queues: queues);
            }

            if (m.Match.State == PvpMatchState.AwaitingAccept)
                return new PvpStatusResult(PvpStatusKind.AwaitingAccept, m.ModeKey, MapKey: m.Map?.MapKey, State: m.Match.State, MapName: m.Map?.DisplayName, Queues: queues);

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
                    StatusLine: (m.TickHandler as IObjectiveModeHandler)?.StatusLine(m.Match.ScoreBoard, m.ScoreTarget), Queues: queues);
            }

            return new PvpStatusResult(PvpStatusKind.InMatch, m.ModeKey, MapKey: m.Map?.MapKey, State: m.Match.State, Remaining: remaining, MapName: m.Map?.DisplayName, Overtime: overtime, Queues: queues);
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
                        if (seat.Dispatched && !seat.Exited && seat.SeenInInstance && !seat.AwaitingDeathReturn && seat.BgState == BgSeatState.None && seat.Template != null && !seat.IsAway)
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
                MarkOut(m, seat, ParticipantExit.ForfeitCommand, now, 0, PvpDeparture.TemplateBackstopEject);
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

        /// <summary>
        /// The catalog definition of every template offered in at least one mode, ordered by key, for the /pvphue audit. These are the
        /// catalog's own objects: callers read them and never change them.
        /// </summary>
        public IReadOnlyList<PvpTemplateDefinition> OfferedTemplateDefinitions() =>
            templateCatalog?.Offered().Where(e => e.Definition != null).Select(e => e.Definition).ToList() ?? new List<PvpTemplateDefinition>();

        /// <summary>The parsed catalog definition for <paramref name="key"/> (offered or not), or null. Read only, like <see cref="OfferedTemplateDefinitions"/>.</summary>
        public PvpTemplateDefinition TemplateDefinition(string key) => templateCatalog?.Get(NormalizeTemplateKey(key))?.Definition;

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
            /// <summary>Each member's repeat-pairing account key at join (PvpPairLedger.AccountKey: the account id, or the character id when unknown).</summary>
            private readonly Dictionary<uint, uint> accounts = new Dictionary<uint, uint>();

            public DateTime QueuedAtUtc { get; set; }

            public bool NotifiedFull { get; set; }

            /// <summary>True once the unit has been told it is waiting for a fresh opponent (the /arena norematch notice); sent once per opt-in.</summary>
            public bool NotifiedRefusal { get; set; }

            /// <summary>Each member's /arena norematch choice at join (true = refuse an unpaid rematch). Absent reads as false.</summary>
            private readonly Dictionary<uint, bool> refusals = new Dictionary<uint, bool>();

            /// <summary>True when ANY member refuses unpaid rematches.</summary>
            public bool RefusesUnpaidRematch => refusals.Values.Any(v => v);

            public bool RefusalOf(uint id) => refusals.TryGetValue(id, out var refuse) && refuse;

            /// <summary>Sets a member's choice. The once-only waiting notice is re-armed only on a real off-to-on change.</summary>
            public void SetRefusal(uint id, bool refuse)
            {
                var was = RefusalOf(id);

                refusals[id] = refuse;

                if (refuse && !was)
                    NotifiedRefusal = false;
            }

            public QueuedUnit(IEnumerable<PvpPlayerFacts> members, DateTime queuedAtUtc)
            {
                foreach (var f in members)
                {
                    Ids.Add(f.CharacterId);
                    refusals[f.CharacterId] = f.RefuseUnpaidRematch;
                    Names.Add(f.Name);
                    ips[f.CharacterId] = f.IpKey;
                    monarchs[f.CharacterId] = f.MonarchId;
                    accounts[f.CharacterId] = PvpPairLedger.AccountKey(f.AccountId, f.CharacterId);
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

            public uint AccountOf(uint id) => accounts.TryGetValue(id, out var account) ? account : id;

            public List<uint> AccountKeys() => Ids.Select(AccountOf).ToList();

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
                    copy.accounts[id] = AccountOf(id);
                    copy.refusals[id] = RefusalOf(id);
                }

                copy.NotifiedRefusal = NotifiedRefusal;
                copy.NotifiedFull = NotifiedFull;

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

            /// <summary>The repeat-pairing cap: one of this seat's cross-team account pairs was at the cap when the match formed, so the seat is paid no Marks. Rating is unaffected.</summary>
            public bool UnpaidRematch { get; set; }

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

            /// <summary>
            /// The seat's team colour (Docs/Pvp/TEMPLATES.md "Team hue"): derived from the match's one coin and the seat's team
            /// at formation, re-derived at dispatch. Picks the hue <see cref="Template"/> is dressed in, and names the team.
            /// </summary>
            public PvpTeamColour Colour { get; set; }

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

            /// <summary>
            /// Conduct signals (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): when the seat was last active, kept by the conduct sampler for every
            /// battleground and Tugak Brawl seat. Null until the first sample after Live; reset to the confirm time on a respawn.
            /// </summary>
            public DateTime? LastActiveUtc { get; set; }

            /// <summary>Conduct: the idle warning went out for the current idle stretch; cleared once the seat's idle time drops below the warning.</summary>
            public bool IdleWarned { get; set; }

            /// <summary>Conduct: the latest sampler pass saw the seat STANDING (ConductSeatOutcome.Standing); idle enforcement judges only then.</summary>
            public bool ConductStanding { get; set; }

            // ---- battleground rejoin (Docs/Pvp/BATTLEGROUNDS.md "Disconnect and rejoin") ----

            /// <summary>
            /// The seat is held for its player until this instant (their rejoin grace), or null when it is not held. Stays set while the
            /// rejoin is on its way in, and is cleared when they stand in the match again or the seat goes out. Never set for an arena seat.
            /// </summary>
            public DateTime? GraceUntilUtc { get; set; }

            /// <summary>The seat has had its one grace of the match; a second logout forfeits at once.</summary>
            public bool GraceUsed { get; set; }

            /// <summary>The grace length taken when it began (pvp_bg_rejoin_grace_seconds then), so the window can slide to the real disconnect.</summary>
            public int GraceSeconds { get; set; }

            /// <summary>When the grace began (the logout intent or the poll), bounding how long the window may slide (<see cref="MaxLogoutLingerSeconds"/>).</summary>
            public DateTime? GraceBeganUtc { get; set; }

            /// <summary>The player of a held seat logged back in and the placement was sent (HandleReconnected); waiting for them to land.</summary>
            public bool Rejoining { get; set; }

            /// <summary>When the rejoin placement was sent; <see cref="RejoinArrivalTimeoutSeconds"/> runs from here.</summary>
            public DateTime? RejoinIssuedAtUtc { get; set; }

            /// <summary>Held (in its grace, or on its way back in): every per-seat pass but AdvanceGraces leaves it alone.</summary>
            public bool IsAway => GraceUntilUtc.HasValue;

            /// <summary>The gateway has been told the player's participation ended (Thread return); never told twice.</summary>
            public bool ParticipationEnded { get; set; }

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

            /// <summary>The arena day the match formed on (1v1 / 2v2 only), from the match's own dials snapshot; the day its pairings are counted on. Null for every other mode.</summary>
            public int? PairDay { get; set; }

            /// <summary>The match's pairings have been counted (once).</summary>
            public bool PairCounted { get; set; }

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

            /// <summary>The conduct sampler's clock: the instant its sampled seconds run from. Null until the first Live sample.</summary>
            public DateTime? ConductAnchorUtc { get; set; }

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

            void IBattlegroundMatchContext.CreditObjectiveTime(uint characterId, int seconds) => Match.Performance?.CreditHillSeconds(characterId, seconds);

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

            /// <summary>The team hue dials taken once at dispatch, with the masks; fixed for the match.</summary>
            public PvpTeamHueDials HueDials { get; set; }

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

            /// <summary>The contribution floor's decisions, evaluated once at Resolve (null until then) and shared by the payout and the PvP daily facts.</summary>
            public IReadOnlyDictionary<uint, FloorDecision> FloorDecisions { get; set; }

            /// <summary>The PvP daily facts have been dispatched for this match (set before the first one goes out). They never go out twice.</summary>
            public bool DailyFactsDispatched { get; set; }

            /// <summary>When the template backstop last ran for this match's participants.</summary>
            public DateTime? LastTemplateBackstopUtc { get; set; }

            /// <summary>The team-colour coin, drawn once when the match formed (<see cref="TeamColourCoin"/>).</summary>
            public bool ColourCoin { get; set; }

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

            /// <summary>One reload of the template rows (null = that read failed).</summary>
            public static DbResult TemplatesLoaded(List<PvpTemplateRecord> rows, PvpTemplateStoreStatus status) =>
                new DbResult { Kind = DbResultKind.TemplatesLoaded, TemplateRows = rows, TemplateStatus = status };

            public static DbResult LatestTemplatesLoaded(List<PvpLatestParticipantTemplateRecord> rows, PvpTemplateStoreStatus status) => new DbResult { Kind = DbResultKind.LatestTemplatesLoaded, LatestTemplates = rows, TemplateStatus = status };

            public static DbResult StampsSaved(Guid matchId, PvpTemplateStoreStatus status) => new DbResult { Kind = DbResultKind.StampsSaved, MatchId = matchId, TemplateStatus = status };

        }
    }
}
