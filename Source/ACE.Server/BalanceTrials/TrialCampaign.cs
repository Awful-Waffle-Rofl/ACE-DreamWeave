using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using log4net;

using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.WorldObjects;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// What <see cref="TrialStarter.TryStart"/> takes for a campaign member: a refusal over the loaded scenario (asked after the
    /// scenario is read, before anything is created), and the session's campaign setup (run right after the session is built,
    /// before anything else subscribes to it). Null on a plain start, which then runs exactly as it always has.
    /// </summary>
    public sealed class TrialCampaignStart
    {
        public Func<TrialScenario, string> Precheck { get; init; }

        public Action<TrialSession> Configure { get; init; }
    }

    /// <summary>
    /// One rep character in a campaign. The tallies are built from the rep's trial_run rows as they are written (the same values
    /// the run log gets). Every field is read and written under the campaign's <see cref="TrialCampaign.Gate"/>.
    /// </summary>
    public sealed class TrialCampaignMember
    {
        internal TrialCampaignMember(TrialCampaign campaign, uint playerGuid, string archetypeId, int repIndex, string driver)
        {
            Campaign = campaign;
            PlayerGuid = playerGuid;
            ArchetypeId = archetypeId;
            RepIndex = repIndex;
            Driver = driver;
        }

        public TrialCampaign Campaign { get; }

        /// <summary>The rep's guid. Never written to a log line or the report.</summary>
        internal uint PlayerGuid { get; }

        public string ArchetypeId { get; }

        /// <summary>The rep slot number (1-based index into TrialRoster.RepSlots); 0 for a guid that is not a rep.</summary>
        public int RepIndex { get; }

        /// <summary>trial_run.driver for this member's reps: the start's driver label, or "manual".</summary>
        public string Driver { get; }

        /// <summary>The member's latest session (the one its room tick drives). Older sessions are ignored by the campaign.</summary>
        internal TrialSession Session;

        internal int Reps, Cleared, Died, TimedOut, Aborted, KillsTrash, KillsElite, Bosses, Deaths, DriftReps, DriftUnknownReps;
        internal long Xp, Lum;
        internal double EngagedSecs;

        /// <summary>The manager clock at the member's first successful start; its wall time runs from here to the close (or now).</summary>
        internal double? FirstStartedAt;

        /// <summary>Successful re-starts, by either path (reported as resumes).</summary>
        internal int Resumes;

        /// <summary>Successful world-tick re-starts: the count <see cref="TrialCampaigns.MaxResumes"/> caps. A start-line re-join does not count.</summary>
        internal int TickResumes;

        /// <summary>The manager clock before which the world tick does not try a re-start again after a refused one, and the next backoff step.</summary>
        internal double NextRestartAt;
        internal double RestartBackoffSecs = TrialCampaigns.RestartBackoffMinSecs;

        internal bool BreakerTripped;

        /// <summary>Breaker counters. They run across the member's sessions: a re-start does not clear them.</summary>
        internal int ZeroKillTimeoutsInRow, FailedAutoStartsInRow;

        /// <summary>The manager clock before which the room tick starts no rep (the gap after a rep, a death or a failed auto-start).</summary>
        internal double NextAutoAt;
    }

    /// <summary>One campaign: a scenario run by up to every rep slot for a fixed duration. In memory only.</summary>
    public sealed class TrialCampaign
    {
        internal TrialCampaign(string id, string scenarioId, int minutes, double now, DateTime utcNow)
        {
            Id = id;
            ScenarioId = scenarioId;
            Minutes = minutes;
            StartedAt = now;
            DeadlineAt = now + minutes * 60.0;
            StartedUtc = utcNow;
            DeadlineUtc = utcNow.AddMinutes(minutes);
            LastStatusAt = now;
            NextRestartScanAt = now + TrialCampaigns.RestartScanSecs;
        }

        public string Id { get; }
        public string ScenarioId { get; }
        public int Minutes { get; }

        /// <summary>The manager clock (TrialManager.Now) at creation, and the deadline on the same clock. Fixed for the campaign's life.</summary>
        public double StartedAt { get; }
        public double DeadlineAt { get; }

        public DateTime StartedUtc { get; }
        public DateTime DeadlineUtc { get; }

        /// <summary>Guards every mutable field here and on the members: they are touched from the world thread, room threads and the run-row writers.</summary>
        internal readonly object Gate = new object();

        internal readonly List<TrialCampaignMember> Members = new List<TrialCampaignMember>();

        internal bool Closed;
        internal string StoppedBy;
        internal double ClosedAt;
        internal bool ReportWritten;

        /// <summary>The manager clock when the report was written; the campaign is evicted <see cref="TrialCampaigns.EvictAfterSecs"/> later.</summary>
        internal double ReportedAt;

        internal double LastStatusAt;
        internal double NextRestartScanAt;

        /// <summary>Reps that ended with a recorder whose run row has not reached the tee yet.</summary>
        internal int PendingRows;

        /// <summary>Member sessions the close has queued onto their room and that have not ended yet.</summary>
        internal int PendingCloses;

        public bool DeadlinePassed(double now) => now >= DeadlineAt;
    }

    /// <summary>
    /// The Balance Trials campaign registry and driver (Docs/BalanceTrials/DESIGN.md, "Campaigns"). A campaign is created by the
    /// first <c>@trial start ... for &lt;minutes&gt; campaign &lt;id&gt;</c>; every later start with the same id joins it.
    ///
    /// Threads. Membership, re-starts and the close run on the WORLD thread (the command handler and <see cref="WorldTick"/>,
    /// called from TrialManager.TickAt). The auto-next and the breaker run on each member's ROOM thread (<see cref="RoomTick"/>
    /// from TrialManager's room tick, and the session's RepEnded). The tallies arrive from the run-row writer (the player's
    /// queue, or the world thread's overdue sweep). Everything mutable sits under the campaign's Gate; session methods are
    /// only ever called on the session's room thread (TrialManager.Enqueue), as everywhere else.
    ///
    /// The deadline is enforced twice: <see cref="RoomTick"/> does not try a rep after it (so the attempt is not counted as a
    /// failed auto-start), and TrialSession.TryNext refuses one (NoNewRepAfter), which also covers a typed @trial next.
    /// </summary>
    public static class TrialCampaigns
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string Tag = "[TRIAL-CAMPAIGN]";

        /// <summary>The gap after a rep that ended any way but a death, and after a failed auto-start.</summary>
        public const double RepGapSecs = 3.0;

        /// <summary>The gap after a rep that ended in a death (the respawn at the room start runs in it).</summary>
        public const double DeathGapSecs = 10.0;

        public const double RestartScanSecs = 5.0;
        public const double StatusEverySecs = 60.0;
        public const int MaxResumes = 5;
        public const int BreakerZeroKillTimeouts = 3;
        public const int BreakerFailedAutoStarts = 5;

        /// <summary>How long after the close the report waits for in-flight run rows and room-thread closes before it is written anyway.</summary>
        public const double ReportWaitSecs = 15.0;

        /// <summary>A reported campaign is evicted from the registry this long after its report (24 h); status and report then answer "unknown campaign".</summary>
        public const double EvictAfterSecs = 24 * 3600.0;

        /// <summary>The world tick's backoff after a refused re-start: 5 s, doubling to 60 s.</summary>
        public const double RestartBackoffMinSecs = 5.0;
        public const double RestartBackoffMaxSecs = 60.0;

        private static readonly ConcurrentDictionary<string, TrialCampaign> campaigns = new ConcurrentDictionary<string, TrialCampaign>(StringComparer.Ordinal);

        // ================================================================ seams

        internal delegate bool MemberStarter(uint guid, string scenarioId, string archetypeId, TrialCampaignStart options, out string refusal);

        /// <summary>Test seam: the clock every campaign time is on (live: the manager's clock, the one sessions are given).</summary>
        internal static Func<double> Clock = TrialManager.Now;

        /// <summary>Test seam: the wall clock (startedUtc, deadlineUtc).</summary>
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        /// <summary>Test seam: where the [TRIAL-CAMPAIGN] lines go (live: the server log at Info).</summary>
        internal static Action<string> Log = line => log.Info(line);

        /// <summary>Test seam: the rep is online and alive (the room tick's check; online and in the room come from the session's world).</summary>
        internal static Func<uint, bool> PlayerAlive = guid => TrialManager.OnlinePlayer(guid) is Player p && !p.IsDead;

        /// <summary>Test seam: the rep is online, alive and not logging out (the world tick's re-start check).</summary>
        internal static Func<uint, bool> PlayerReady = DefaultPlayerReady;

        /// <summary>The live PlayerReady: online, not logging out, alive. The logout guard is read first.</summary>
        internal static bool DefaultPlayerReady(uint guid) => TrialManager.OnlinePlayer(guid) is Player p && !p.IsLoggingOut && !p.IsDead;

        /// <summary>Test seam: the rep has a live trial session (bound or registered, in any phase but Done and Aborted).</summary>
        internal static Func<uint, bool> HasLiveSession = LiveSessionFor;

        /// <summary>Test seam: starts one member's session (live: TrialStarter.TryStart on the online rep, with the campaign options).</summary>
        internal static MemberStarter StartMember = StartOnline;

        /// <summary>Test seam: logs the rep out through the normal logout path, behind the closing reset (see <see cref="LogOutOnline"/>).</summary>
        internal static Action<uint> LogOut = LogOutOnline;

        internal static void ResetForTests()
        {
            campaigns.Clear();
            Clock = TrialManager.Now;
            UtcNow = () => DateTime.UtcNow;
            Log = line => log.Info(line);
            PlayerAlive = guid => TrialManager.OnlinePlayer(guid) is Player p && !p.IsDead;
            PlayerReady = DefaultPlayerReady;
            HasLiveSession = LiveSessionFor;
            StartMember = StartOnline;
            LogOut = LogOutOnline;
        }

        private static bool LiveSessionFor(uint guid)
        {
            var session = TrialManager.OnlinePlayer(guid)?.TrialSession;

            if (session == null)
                TrialManager.TryGetByPlayer(guid, out session);

            return session != null && session.Phase != TrialPhase.Done && session.Phase != TrialPhase.Aborted;
        }

        private static bool StartOnline(uint guid, string scenarioId, string archetypeId, TrialCampaignStart options, out string refusal)
        {
            var player = TrialManager.OnlinePlayer(guid);

            if (player == null)
            {
                refusal = "the rep is not online";
                return false;
            }

            return TrialStarter.TryStart(player, scenarioId, archetypeId, out refusal, options);
        }

        /// <summary>
        /// The live logout: the standard path a client's own logoff request takes (Session.LogOffPlayer, which saves the character
        /// first), on the world thread. It is queued through the rep's own action queue first, so it lands BEHIND the session's
        /// closing consumables reset (TrialStarter.ClosingReset queues that on the same queue when the session ends): the reset runs
        /// while the rep is not yet logging out, and only then is the logout requested. Never a kill, never a forced disconnect.
        /// </summary>
        private static void LogOutOnline(uint guid)
        {
            var player = TrialManager.OnlinePlayer(guid);

            if (player == null || player.IsLoggingOut)
                return;

            TrialStarter.EnqueueOnPlayer(player, () => WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (ReferenceEquals(TrialManager.OnlinePlayer(guid), player) && !player.IsLoggingOut)
                        player.Session?.LogOffPlayer();
                }
                catch (Exception ex)
                {
                    log.Error($"{Tag} logging a rep out at the campaign close threw", ex);
                }
            })));
        }

        // ================================================================ lookups

        public static bool TryGet(string id, out TrialCampaign campaign) => campaigns.TryGetValue(id ?? "", out campaign);

        public static int Count => campaigns.Count;

        /// <summary>The rep slot number of <paramref name="guid"/> (1-based), or 0 when it is not a rep.</summary>
        internal static int RepIndexOf(uint guid)
        {
            var slots = TrialRoster.RepSlots;

            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i].Guid == guid)
                    return i + 1;
            }

            return 0;
        }

        /// <summary>The campaign-mode refusal over a scenario, or null. A hardCopyEvery schedule would stall an unattended run on a rebuild.</summary>
        internal static string ScenarioRefusal(TrialScenario scenario)
            => scenario != null && scenario.HardCopyEvery > 0
                ? $"a campaign cannot run scenario '{scenario.Id}': it has hardCopyEvery {scenario.HardCopyEvery}, and a rebuild would stall an unattended run"
                : null;

        // ================================================================ join (world thread)

        /// <summary>
        /// <c>@trial start &lt;scenario&gt; &lt;archetype&gt; for &lt;minutes&gt; campaign &lt;id&gt; [driver &lt;label&gt;]</c> for the rep
        /// <paramref name="guid"/>. Returns the one reply line. The first start of an id creates the campaign (its deadline is now plus
        /// <paramref name="minutes"/>); a later start joins it with the same scenario; a start for a member already in it does nothing
        /// when its session is live and re-starts it otherwise (the deadline never moves, the seed walk continues). World thread.
        /// </summary>
        public static string Join(uint guid, string scenarioId, string archetypeId, int minutes, string id, string driver)
        {
            var now = Clock();
            TrialCampaignMember existing = null;

            if (campaigns.TryGetValue(id, out var campaign))
            {
                lock (campaign.Gate)
                {
                    if (campaign.Closed)
                        return $"campaign {id} is closed; nothing started";

                    if (!string.Equals(campaign.ScenarioId, scenarioId, StringComparison.Ordinal))
                        return $"trial refused: campaign {id} runs scenario {campaign.ScenarioId}, not {scenarioId}";

                    existing = campaign.Members.FirstOrDefault(m => m.PlayerGuid == guid);
                }

                if (existing != null)
                {
                    if (HasLiveSession(guid))
                        return $"campaign {id}: already running";

                    // A tripped member is never re-started, by the world tick or by a relaunched client's start line.
                    bool tripped;
                    lock (campaign.Gate)
                        tripped = existing.BreakerTripped;

                    if (tripped)
                        return $"campaign {id}: this rep's breaker has tripped; nothing started";

                    return Restart(campaign, existing, "re-joined", fromTick: false);
                }
            }

            var creating = campaign == null;
            campaign ??= new TrialCampaign(id, scenarioId, minutes, now, UtcNow());

            var member = new TrialCampaignMember(campaign, guid, archetypeId, RepIndexOf(guid), driver ?? "manual");

            if (!StartMember(guid, scenarioId, archetypeId, Options(campaign, member), out var refusal))
                return $"trial refused: {refusal}";

            lock (campaign.Gate)
                campaign.Members.Add(member);

            if (creating)
                campaigns[id] = campaign;

            Log($"{Tag} {id} {(creating ? "created" : "joined")}: scenario={scenarioId} minutes={campaign.Minutes} slot={member.RepIndex} archetype={archetypeId} driver={member.Driver}");

            return $"trial started: scenario {scenarioId}, archetype {archetypeId}, campaign {id} ({MinutesLeft(campaign, now)} min left)";
        }

        /// <summary>
        /// Re-starts an existing member (a start-line re-join, or the world tick's resume). A resume is spent only when the start
        /// succeeds; only a world-tick resume counts toward <see cref="MaxResumes"/>. A refused world-tick re-start backs off (5 s,
        /// doubling to 60 s) before the tick tries again. World thread.
        /// </summary>
        private static string Restart(TrialCampaign campaign, TrialCampaignMember member, string why, bool fromTick)
        {
            if (StartMember(member.PlayerGuid, campaign.ScenarioId, member.ArchetypeId, Options(campaign, member), out var refusal))
            {
                int resume;

                lock (campaign.Gate)
                {
                    resume = ++member.Resumes;

                    if (fromTick)
                        member.TickResumes++;

                    member.RestartBackoffSecs = RestartBackoffMinSecs;
                    member.NextRestartAt = 0;
                }

                Log($"{Tag} {campaign.Id} slot={member.RepIndex}: {why}, resume {resume}");
                return $"campaign {campaign.Id}: re-started (resume {resume})";
            }

            double backoff = 0;

            if (fromTick)
            {
                lock (campaign.Gate)
                {
                    backoff = member.RestartBackoffSecs;
                    member.NextRestartAt = Clock() + backoff;
                    member.RestartBackoffSecs = Math.Min(backoff * 2, RestartBackoffMaxSecs);
                }
            }

            Log($"{Tag} {campaign.Id} slot={member.RepIndex}: {why} refused{(fromTick ? $", next try in {backoff:0} s" : "")}: {Scrub(refusal)}");
            return $"trial refused: {refusal}";
        }

        private static TrialCampaignStart Options(TrialCampaign campaign, TrialCampaignMember member) => new TrialCampaignStart
        {
            Precheck = ScenarioRefusal,
            Configure = session => Configure(campaign, member, session),
        };

        /// <summary>
        /// A member session's campaign setup, inside TryStart right after the session is built (before TrialRunLog and the rep
        /// setup subscribe, so <see cref="OnRepEnded"/> reads the rep's recorder before the run log finishes it): the reps cap lifted
        /// to the scenario maximum, no rep after the deadline, the run rows' driver, and the hooks.
        /// </summary>
        internal static void Configure(TrialCampaign campaign, TrialCampaignMember member, TrialSession session)
        {
            session.RepCap = TrialScenarioLoader.MaxReps;
            session.NoNewRepAfter = campaign.DeadlineAt;
            session.DeadlineClock = () => Clock();
            session.Driver = member.Driver;
            session.CampaignMember = member;
            session.RepEnded += (rep, endState) => OnRepEnded(member, session, rep, endState);
            session.RunRowWritten = (rep, row) => OnRunRow(member, row);

            lock (campaign.Gate)
            {
                member.Session = session;
                member.NextAutoAt = Clock() + RepGapSecs;
                member.FirstStartedAt ??= Clock();
            }
        }

        // ================================================================ room thread

        /// <summary>
        /// The member session's room tick (TrialManager's room-thread tick, after the session's own tick): runs the next rep itself
        /// when the session is Ready, the deadline has not passed, the rep is online, alive and in the room, and the gap has
        /// elapsed. A refusal counts toward the failed-auto-start breaker. Room thread.
        /// </summary>
        internal static void RoomTick(TrialSession session, ITrialWorld world, double now)
        {
            var member = session?.CampaignMember;

            if (member == null)
                return;

            var campaign = member.Campaign;

            lock (campaign.Gate)
            {
                if (campaign.Closed || member.BreakerTripped || !ReferenceEquals(member.Session, session) || now < member.NextAutoAt)
                    return;
            }

            // The deadline against a FRESH clock: the tick's time was taken on the world thread when the tick was queued, and the
            // room thread may run it later, past the deadline.
            if (session.Phase != TrialPhase.Ready || campaign.DeadlinePassed(Math.Max(now, Clock())))
                return;

            if (world == null || !world.PlayerOnline || !world.PlayerInRoom || !PlayerAlive(session.PlayerGuid))
                return;

            if (session.TryNext(now, out var refusal))
            {
                lock (campaign.Gate)
                    member.FailedAutoStartsInRow = 0;

                return;
            }

            int failed;
            bool trip;

            lock (campaign.Gate)
            {
                failed = ++member.FailedAutoStartsInRow;
                member.NextAutoAt = now + RepGapSecs;
                trip = failed >= BreakerFailedAutoStarts && !member.BreakerTripped;
            }

            Log($"{Tag} {campaign.Id} slot={member.RepIndex}: auto-start {failed} of {BreakerFailedAutoStarts} in a row refused: {Scrub(refusal)}");

            if (trip)
                Trip(member, session, $"{BreakerFailedAutoStarts} auto-starts in a row failed");
        }

        /// <summary>
        /// RepEnded of a member session: the gap before the next rep, the zero-kill timeout breaker, and the pending row. Room thread.
        /// It runs inside the session's EndRep, ahead of the run log and the rep setup, so a throw is logged here and never reaches them.
        /// </summary>
        internal static void OnRepEnded(TrialCampaignMember member, TrialSession session, TrialRep rep, string endState)
        {
            try
            {
                RepEndedCore(member, session, rep, endState);
            }
            catch (Exception ex)
            {
                log.Error($"{Tag} {member?.Campaign?.Id} slot={member?.RepIndex}: the campaign's rep-ended handler threw", ex);
            }
        }

        private static void RepEndedCore(TrialCampaignMember member, TrialSession session, TrialRep rep, string endState)
        {
            var campaign = member.Campaign;
            var recorder = rep?.Recorder;
            var kills = recorder?.Kills ?? 0;
            var trip = false;

            lock (campaign.Gate)
            {
                if (recorder != null && !campaign.ReportWritten)
                    campaign.PendingRows++;

                // A superseded session's last rep (it ended after a re-start) still counts in the tallies, through its row,
                // but it never moves the current session's gap or breaker.
                if (!ReferenceEquals(member.Session, session))
                    return;

                member.NextAutoAt = (rep?.EndedAt ?? Clock()) + (endState == TrialEventKinds.EndDied ? DeathGapSecs : RepGapSecs);

                if (endState == TrialEventKinds.EndTimeout && kills == 0)
                    member.ZeroKillTimeoutsInRow++;
                else if (endState != TrialEventKinds.EndForfeit)
                    member.ZeroKillTimeoutsInRow = 0;

                trip = member.ZeroKillTimeoutsInRow >= BreakerZeroKillTimeouts && !member.BreakerTripped;
            }

            if (trip)
                Trip(member, session, $"{BreakerZeroKillTimeouts} reps in a row timed out with no kills");
        }

        /// <summary>Trips the member's breaker: its session ends (on its room thread) and it is never re-started. The campaign runs on.</summary>
        private static void Trip(TrialCampaignMember member, TrialSession session, string why)
        {
            var campaign = member.Campaign;

            lock (campaign.Gate)
            {
                if (member.BreakerTripped)
                    return;

                member.BreakerTripped = true;
            }

            Log($"{Tag} {campaign.Id} slot={member.RepIndex}: breaker tripped ({why}); the member's session ends and it is not re-started");
            TrialManager.EndSession(session, "campaign breaker: " + why);
        }

        /// <summary>The run-row tee (TrialRunLog, once per rep, on whichever thread wrote the row; a null row is a write that failed).</summary>
        internal static void OnRunRow(TrialCampaignMember member, AnalyticsDatabase.TrialRunRow row)
        {
            var campaign = member.Campaign;

            lock (campaign.Gate)
            {
                if (campaign.PendingRows > 0)
                    campaign.PendingRows--;

                if (row == null || campaign.ReportWritten)
                    return;

                member.Reps++;

                switch (row.EndState)
                {
                    case TrialEventKinds.EndCleared: member.Cleared++; break;
                    case TrialEventKinds.EndDied: member.Died++; break;
                    case TrialEventKinds.EndTimeout: member.TimedOut++; break;
                    default: member.Aborted++; break;
                }

                member.KillsTrash += row.KillsTrash;
                member.KillsElite += row.KillsElite;
                if (row.BossKilled)
                    member.Bosses++;
                member.Deaths += row.Deaths;
                member.Xp += row.XpEarned;
                member.Lum += row.LumEarned;
                member.EngagedSecs += row.EngagedSecs;
                if (row.DigestDrift)
                    member.DriftReps++;
                if (row.StateDigest == null)
                    member.DriftUnknownReps++;
            }
        }

        // ================================================================ world thread

        /// <summary>
        /// The world-thread sweep (TrialManager.TickAt, every half second): re-starts members every <see cref="RestartScanSecs"/>,
        /// closes a campaign once its deadline has passed with no rep in flight, writes the status line every
        /// <see cref="StatusEverySecs"/>, and the report once after the close.
        /// </summary>
        internal static void WorldTick(double now)
        {
            foreach (var campaign in campaigns.Values)
            {
                bool scan = false, close = false, status = false, report = false;

                lock (campaign.Gate)
                {
                    if (campaign.ReportWritten)
                    {
                        // Reported: kept for the status and report verbs for a day, then evicted.
                        if (now - campaign.ReportedAt >= EvictAfterSecs)
                            campaigns.TryRemove(new KeyValuePair<string, TrialCampaign>(campaign.Id, campaign));

                        continue;
                    }

                    if (!campaign.Closed)
                    {
                        if (now >= campaign.NextRestartScanAt)
                        {
                            campaign.NextRestartScanAt = now + RestartScanSecs;
                            scan = true;
                        }

                        if (campaign.DeadlinePassed(now) && !AnyInFlight(campaign))
                            close = true;
                        else if (now - campaign.LastStatusAt >= StatusEverySecs)
                        {
                            campaign.LastStatusAt = now;
                            status = true;
                        }
                    }
                    else if ((campaign.PendingCloses <= 0 && campaign.PendingRows <= 0) || now - campaign.ClosedAt >= ReportWaitSecs)
                    {
                        campaign.ReportWritten = true;
                        campaign.ReportedAt = now;

                        // The report is final: drop every session reference, so a kept campaign holds no room, rep or recorder alive.
                        foreach (var member in campaign.Members)
                            member.Session = null;

                        report = true;
                    }
                }

                try
                {
                    if (close)
                        Close(campaign, "deadline", now);
                    else if (scan)
                        RestartScan(campaign, now);

                    if (status)
                        Log($"{Tag} status {Json(campaign)}");

                    if (report)
                        Log($"{Tag} report {Json(campaign)}");
                }
                catch (Exception ex)
                {
                    log.Error($"{Tag} {campaign.Id} tick threw", ex);
                }
            }
        }

        /// <summary>True while any member's latest session has a rep running. Under the Gate.</summary>
        private static bool AnyInFlight(TrialCampaign campaign)
            => campaign.Members.Any(m => m.Session != null && m.Session.Phase == TrialPhase.Running);

        /// <summary>
        /// Re-starts each member with no live session that is online, alive and not logging out, before the deadline, up to
        /// <see cref="MaxResumes"/> successful world-tick resumes, and not inside a refused re-start's backoff.
        /// </summary>
        private static void RestartScan(TrialCampaign campaign, double now)
        {
            List<TrialCampaignMember> members;

            lock (campaign.Gate)
            {
                if (campaign.Closed || campaign.DeadlinePassed(now))
                    return;

                members = campaign.Members.Where(m => !m.BreakerTripped && m.TickResumes < MaxResumes && now >= m.NextRestartAt).ToList();
            }

            foreach (var member in members)
            {
                if (HasLiveSession(member.PlayerGuid) || !PlayerReady(member.PlayerGuid))
                    continue;

                Restart(campaign, member, "re-started", fromTick: true);
            }
        }

        /// <summary>
        /// Closes <paramref name="campaign"/> (the deadline, or @trial campaign stop). Each member's session is ended on its room
        /// thread (a rep still in flight ends aborted, as forfeit), which fires the session's closing consumables reset; the rep is
        /// then logged out through <see cref="LogOut"/>, which queues behind that reset. Returns the reps that were in flight, or -1
        /// when the campaign was already closed. World thread.
        /// </summary>
        internal static int Close(TrialCampaign campaign, string stoppedBy, double now)
        {
            List<TrialCampaignMember> members;

            lock (campaign.Gate)
            {
                if (campaign.Closed)
                    return -1;

                campaign.Closed = true;
                campaign.StoppedBy = stoppedBy;
                campaign.ClosedAt = now;
                members = campaign.Members.ToList();
            }

            var reason = stoppedBy == "stop" ? "campaign stopped" : "campaign closed at its deadline";
            var inFlight = 0;

            foreach (var member in members)
            {
                TrialSession session;

                lock (campaign.Gate)
                    session = member.Session;

                var guid = member.PlayerGuid;

                if (session == null || !TrialManager.TryGetInstance(session, out _))
                {
                    LogOut(guid);
                    continue;
                }

                if (session.Phase == TrialPhase.Running)
                    inFlight++;

                lock (campaign.Gate)
                    campaign.PendingCloses++;

                void End()
                {
                    try
                    {
                        session.Abort(reason, Clock());
                        TrialManager.Remove(session);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"{Tag} {campaign.Id} ending a member session at the close threw", ex);
                    }
                    finally
                    {
                        LogOut(guid);

                        lock (campaign.Gate)
                            campaign.PendingCloses--;
                    }
                }

                if (!TrialManager.Enqueue(session, End))
                    End();
            }

            Log($"{Tag} {campaign.Id} closed: stoppedBy={stoppedBy} members={members.Count} inFlightEnded={inFlight}");

            return inFlight;
        }

        // ================================================================ verbs

        /// <summary>@trial campaign stop &lt;id&gt;: the reply line. World thread.</summary>
        public static string Stop(string id)
        {
            if (!campaigns.TryGetValue(id, out var campaign))
                return $"unknown campaign '{id}'";

            var inFlight = Close(campaign, "stop", Clock());

            return inFlight < 0
                ? $"campaign {id} is already closed"
                : $"campaign {id} stopped: {inFlight} in-flight reps ended aborted; its online reps are being logged out";
        }

        /// <summary>@trial campaign status|report &lt;id&gt;: the chat lines (each under 200 characters). The JSON goes to the server log as a query line.</summary>
        public static IReadOnlyList<string> Describe(string id, bool report)
        {
            if (!campaigns.TryGetValue(id, out var campaign))
                return new[] { $"unknown campaign '{id}'" };

            Log($"{Tag} query {Json(campaign)}");

            return SummaryLines(campaign, Clock(), report);
        }

        internal const int MaxChatLine = 199;

        internal static IReadOnlyList<string> SummaryLines(TrialCampaign campaign, double now, bool report)
        {
            var lines = new List<string>();

            lock (campaign.Gate)
            {
                var state = campaign.Closed ? $"closed ({campaign.StoppedBy})" : "open";
                lines.Add(Clip($"campaign {campaign.Id}: scenario {campaign.ScenarioId}, {state}, {MinutesLeft(campaign, now)} of {campaign.Minutes} min left, {campaign.Members.Count} members"));

                foreach (var m in campaign.Members.OrderBy(x => x.RepIndex))
                {
                    var wall = WallSecs(campaign, m, now);
                    var head = $"slot {m.RepIndex} {m.ArchetypeId}: {m.Reps} reps ({m.Cleared} cleared, {m.Died} died, {m.TimedOut} timed out, {m.Aborted} aborted)";
                    var tail = report
                        ? $", kills {m.KillsTrash}/{m.KillsElite}/{m.Bosses}, xp/h {Rate(m.Xp, wall)}, lum/h {Rate(m.Lum, wall)}, drift {m.DriftReps}/{m.DriftUnknownReps}, resumes {m.Resumes}"
                        : $", xp/h {Rate(m.Xp, wall)}, resumes {m.Resumes}";

                    lines.Add(Clip(head + tail + (m.BreakerTripped ? ", BREAKER TRIPPED" : "")));
                }
            }

            return lines;
        }

        /// <summary>
        /// The member's wall time, the denominator of xpPerHour and lumPerHour: from its first successful start to the campaign close, or
        /// to <paramref name="now"/> while the campaign is open. It includes the gaps, deaths and relogs between reps (the owner's headline
        /// is XP and luminance over the campaign hour). The engaged rates keep the reps' own engaged time. Under the Gate.
        /// </summary>
        internal static double WallSecs(TrialCampaign campaign, TrialCampaignMember m, double now)
            => m.FirstStartedAt is double first ? Math.Max(0, (campaign.Closed ? campaign.ClosedAt : now) - first) : 0;

        private static string Rate(long total, double secs) => secs > 0 ? Math.Round(total / secs * 3600.0).ToString("0", CultureInfo.InvariantCulture) : "-";

        private static string Clip(string line) => line.Length <= MaxChatLine ? line : line.Substring(0, MaxChatLine);

        private static int MinutesLeft(TrialCampaign campaign, double now)
            => (int)Math.Ceiling(Math.Max(0, campaign.DeadlineAt - now) / 60.0);

        // ================================================================ JSON

        /// <summary>
        /// The one-line campaign JSON of the status and report lines (the frozen key set). No guids, no character names, no
        /// account names: a member is its archetype id and rep slot number.
        /// </summary>
        internal static string Json(TrialCampaign campaign)
        {
            var now = Clock();
            using var stream = new MemoryStream();

            lock (campaign.Gate)
            {
                using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

                w.WriteStartObject();
                w.WriteString("campaign", campaign.Id);
                w.WriteString("scenario", campaign.ScenarioId);
                w.WriteNumber("minutes", campaign.Minutes);
                w.WriteString("startedUtc", Utc(campaign.StartedUtc));
                w.WriteString("deadlineUtc", Utc(campaign.DeadlineUtc));
                w.WriteBoolean("closed", campaign.Closed);

                if (campaign.StoppedBy == null)
                    w.WriteNull("stoppedBy");
                else
                    w.WriteString("stoppedBy", campaign.StoppedBy);

                w.WriteStartArray("members");

                foreach (var m in campaign.Members.OrderBy(x => x.RepIndex))
                {
                    var wall = WallSecs(campaign, m, now);
                    w.WriteStartObject();
                    w.WriteString("archetype", m.ArchetypeId);
                    w.WriteNumber("repIndex", m.RepIndex);
                    w.WriteNumber("reps", m.Reps);
                    w.WriteNumber("cleared", m.Cleared);
                    w.WriteNumber("died", m.Died);
                    w.WriteNumber("timedOut", m.TimedOut);
                    w.WriteNumber("aborted", m.Aborted);
                    w.WriteNumber("killsTrash", m.KillsTrash);
                    w.WriteNumber("killsElite", m.KillsElite);
                    w.WriteNumber("bosses", m.Bosses);
                    w.WriteNumber("deaths", m.Deaths);
                    w.WriteNumber("xp", m.Xp);
                    w.WriteNumber("lum", m.Lum);
                    w.WriteNumber("engagedSecs", Math.Round(m.EngagedSecs, 1));
                    w.WriteNumber("wallSecs", Math.Round(wall, 1));
                    PerHour(w, "xpPerHour", m.Xp, wall);
                    PerHour(w, "lumPerHour", m.Lum, wall);
                    PerHour(w, "xpPerEngagedHour", m.Xp, m.EngagedSecs);
                    PerHour(w, "lumPerEngagedHour", m.Lum, m.EngagedSecs);
                    w.WriteNumber("driftReps", m.DriftReps);
                    w.WriteNumber("driftUnknownReps", m.DriftUnknownReps);
                    w.WriteNumber("resumes", m.Resumes);
                    w.WriteBoolean("breakerTripped", m.BreakerTripped);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
                w.Flush();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void PerHour(Utf8JsonWriter w, string name, long total, double secs)
        {
            if (secs > 0)
                w.WriteNumber(name, Math.Round(total / secs * 3600.0));
            else
                w.WriteNull(name);
        }

        private static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        // ================================================================ log hygiene

        private static readonly Regex HexGuid = new Regex("0x[0-9A-Fa-f]{8}", RegexOptions.CultureInvariant);
        private static readonly Regex MasterName = new Regex(@"Trialmaster \d+", RegexOptions.CultureInvariant);
        private static readonly Regex AccountName = new Regex(@"\btrial-\d{2}\b", RegexOptions.CultureInvariant);

        /// <summary>A refusal made fit for a server log line: every 8-digit hex value (a guid), every trial character name and every trial-NN account name masked.</summary>
        internal static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";

            var s = HexGuid.Replace(text, "0x????????");
            s = MasterName.Replace(s, "<master>");
            s = AccountName.Replace(s, "<account>");

            foreach (var rep in TrialRoster.RepSlots)
                s = s.Replace(rep.Name, "<rep>", StringComparison.Ordinal);

            return s;
        }
    }
}
