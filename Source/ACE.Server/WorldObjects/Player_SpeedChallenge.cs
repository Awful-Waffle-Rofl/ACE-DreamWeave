using System;

using ACE.Common;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    // WaffleACE speed challenge ("The Proving Grounds (Speed)"): a portal (PropertyBool.SpeedChallengeEntry)
    // drops the player into a strictly single-player ephemeral copy of the CURRENT SEASON's dungeon. The clock
    // starts the moment they land and stops the moment they complete that season's declared objective - either a
    // goal object (PropertyBool.SpeedChallengeGoal) being used or a boss (PropertyBool.SpeedChallengeBoss) dying,
    // both funnelling through TryFinishSpeedChallenge. Score = elapsed CENTISECONDS, lower is better.
    // Dying, leaving the instance by any means, or logging out mid-run forfeits the attempt with no time recorded;
    // a death inside the instance costs no vitae and drops no items.
    //
    // A season whose row declares a StartWcid (PropertyBool.SpeedChallengeStart on some object in the dungeon)
    // REBASES that arrival clock to now the moment the player activates it, via TryStartSpeedChallengeClock. It
    // is a rebase, not a gate: arrival still arms and binds the run exactly as always, so a player who never
    // reaches or pulls the lever is simply timed from arrival - worse for them, never a stuck run. StartWcid 0
    // (the default) means no lever exists for that season and the arrival clock is the only clock.
    //
    // This is the fourth sibling of Player_DpsChallenge.cs, Player_SurvivalChallenge.cs and
    // Player_WaveChallenge.cs and mirrors them deliberately: the same run-instance binding to invalidate a stale
    // or abandoned run, the same persisted-active-flag + login-clear forfeit model, the same two-flag death split
    // (the penalty skips key off the death-in-progress marker, never the active flag, because the run is consumed
    // up front), and the same EphemeralRealmExitTo return with a 3 second grace.
    //
    // The one deliberate difference from the three arenas is WHAT the season is: the run is not configured from
    // properties on the portal weenie but from a `speed_season` row resolved by SpeedSeasonManager. The season id
    // is captured at ARM time (speedRunSeasonId) so a rotation landing mid-run cannot file the result into the
    // wrong season, and the season's ObjectiveWcid - not the bool flag alone - is what may end a run, so a
    // dungeon can host props of the same class without them accidentally finishing it.
    //
    // See Docs/ProvingGroundsSpeed/DESIGN.md sections 3.1, 3.4, 5 and 7.
    partial class Player
    {
        // Instance id the current run is bound to; 0 = no active run. Every hook re-checks this (plus the
        // player's live Location.Instance) via IsSpeedRunValid, so a run that was abandoned - the player left the
        // instance, died out of it, or logged out - silently no-ops rather than scoring late or scoring into
        // someone else's instance.
        private uint speedRunInstance;

        // Unix time (seconds) the current run started, captured on arrival and REBASED to now by
        // TryStartSpeedChallengeClock if the season has a start object and the player reaches it. The elapsed
        // time at the objective is measured against whichever value is current - the same Time.GetUnixTime()
        // clock the Survival and Wave controllers use.
        private double speedRunStartTime;

        // The speed_season id the run was ARMED under. Captured at arm time rather than read at completion, so a
        // rotation landing mid-run files the result into the season the run actually started in.
        private int speedRunSeasonId;

        // Captured at the start of the death sequence: true iff this death is a valid in-instance speed-run
        // death. The death-penalty skips key off this rather than SpeedChallengeActive, because the forfeit
        // clears the active flag up front (before CalculateDeathItems / ThreadSafeTeleportOnDeath run later in
        // the chain).
        private bool speedChallengeDeathInProgress;

        // True once this run's clock has been REBASED off the season's start object (TryStartSpeedChallengeClock).
        // This is a rebase, not a gate: the run is still armed and bound on arrival exactly as before, and a
        // player who never reaches or uses the start object is simply timed from arrival - strictly worse for
        // them, never a stuck or unfinishable run. The flag exists only to refuse a SECOND rebase and to let
        // the completion message note an unpulled lever. Reset alongside every other per-run field, both where
        // a run is consumed on completion (TryFinishSpeedChallenge) and where it is forfeited
        // (ForfeitSpeedChallenge), so a later run never inherits a stale rebase.
        private bool speedChallengeClockRebased;

        /// <summary>
        /// True while a speed-challenge run is armed/in progress. Persisted so a mid-run logout can be caught at
        /// next login (see WorldManager.DoPlayerEnterWorld) and the player re-homed to their lifestone.
        /// </summary>
        public bool SpeedChallengeActive
        {
            get => GetProperty(PropertyBool.SpeedChallengeActive) ?? false;
            set => SetProperty(PropertyBool.SpeedChallengeActive, value);
        }

        /// <summary>
        /// This player's best speed-run time this season, in CENTISECONDS, cached on the character biota
        /// for personal-best messaging. Mirrors BestWaveScoreCenti's shape (Player_WaveChallenge.cs:101).
        /// <para/>
        /// It is a CACHE, never the authority: character_speed_run is the record of truth (DESIGN 3.5).
        /// It is meaningless on its own - read it only through GetCachedSpeedBest, which pairs it with
        /// SpeedChallengeSeasonId, and always WRITE the two together.
        /// </summary>
        public long? BestSpeedRunCenti
        {
            get => GetProperty(PropertyInt64.BestSpeedRunCenti);
            set { if (!value.HasValue) RemoveProperty(PropertyInt64.BestSpeedRunCenti); else SetProperty(PropertyInt64.BestSpeedRunCenti, value.Value); }
        }

        /// <summary>
        /// The season id BestSpeedRunCenti was recorded under. Stored beside the number precisely so a
        /// best from a PREVIOUS season can be told apart from this season's and treated as unset - see
        /// GetCachedSpeedBest.
        /// </summary>
        public int? SpeedChallengeSeasonId
        {
            get => GetProperty(PropertyInt.SpeedChallengeSeasonId);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.SpeedChallengeSeasonId); else SetProperty(PropertyInt.SpeedChallengeSeasonId, value.Value); }
        }

        /// <summary>
        /// The staleness rule for the personal-best cache, as a pure function: the cached best counts
        /// only when it was recorded under THIS season and is a positive time. Anything else - a best
        /// from a different season, an unstamped season id, an unset or non-positive number - reads as
        /// UNSET, so the next completion of the new season is a first personal best.
        /// <para/>
        /// DESIGN 4.3 states that rule explicitly, and it is the entire reason the season id is stored
        /// beside the number. Pure and static for the same reason IsSeasonObjective is: it is the one
        /// rule in the completion path reachable from a unit test.
        /// </summary>
        public static long? GetCachedSpeedBest(int? cachedSeasonId, long? cachedBest, int seasonId)
        {
            if (!cachedSeasonId.HasValue || cachedSeasonId.Value != seasonId)
                return null;

            if (!cachedBest.HasValue || cachedBest.Value <= 0)
                return null;

            return cachedBest.Value;
        }

        /// <summary>
        /// The three claim-once quest flags the Proving Grounds (Speed) reward NPC stamps, one per reward
        /// tier. Each is stamped when the player claims that tier and gates the tier from paying twice.
        /// <para/>
        /// THE OTHER HALF OF THIS COUPLING IS CONTENT, NOT CODE: reward NPC weenie 1003002 carries the
        /// matching InqQuest / StampQuest emote rows, and these strings must match those rows EXACTLY. A
        /// divergence is SILENT in both directions - a name only the weenie knows is never reset here, so
        /// that tier pays once per character forever; a name only this array knows is erased every season
        /// and never pays at all. Nothing at build or load time compares the two, so change them together.
        /// <para/>
        /// The three arenas do not need this because their ladders never reset; Speed CYCLES, and the emote
        /// system has no notion of which season is active, so the season rollover has to erase these here.
        /// </summary>
        public static readonly string[] SpeedRewardClaimQuests =
        {
            "ProvingGroundsSpeedTier1",
            "ProvingGroundsSpeedTier2",
            "ProvingGroundsSpeedTier3",
        };

        /// <summary>
        /// The season-rollover rule for the reward claims, as a pure function: the claims are stale - and so
        /// must be erased before this run can be scored - whenever the player's cached season stamp is absent
        /// or names a season other than the one being scored.
        /// <para/>
        /// Deliberately NOT the same question as "is the cached personal best unset". GetCachedSpeedBest also
        /// returns null for a player who is already in this season but has no cached best yet (a first clear,
        /// or a best that was zeroed by a backwards clock), and resetting on that would re-open all three
        /// tiers on every first clear WITHIN a season, making them farmable. Only the season id answers this.
        /// <para/>
        /// Pure and static for the same reason IsSeasonObjective and GetCachedSpeedBest are: it is the one
        /// part of the rollover reachable from a unit test without a live Player.
        /// </summary>
        public static bool ShouldResetSpeedRewardClaims(int? cachedSeasonId, int seasonId)
        {
            return !cachedSeasonId.HasValue || cachedSeasonId.Value != seasonId;
        }

        /// <summary>
        /// The personal-best cache as the REWARD NPC must see it: the cached time if it was set in the
        /// currently active season, otherwise null. This is the season-aware read that
        /// EmoteType.InqInt64Stat substitutes for its raw GetProperty on BestSpeedRunCenti, so a stale
        /// number can never be tested against a reward tier's ceiling.
        /// <para/>
        /// A null <paramref name="activeSeasonId"/> - a scheduling GAP between seasons - reads as unset, so
        /// nothing pays during a gap. That is a deliberate call and it is worth stating why, because it
        /// looks like it contradicts the login-time clear (ShouldClearStaleSpeedBest), which treats the very
        /// same gap as "do nothing". The two are the same principle applied to opposite risks: the login
        /// clear DESTROYS data, so its safe direction in the absence of a season is to leave the number
        /// alone; this one AUTHORIZES A PAYOUT, so its safe direction is to refuse. Harmonizing them into
        /// one rule breaks whichever one gets converted. It also matches the rest of this controller, where
        /// IsSeasonObjective, IsSeasonStartObject and TryFinishSpeedChallenge all fail CLOSED on a null
        /// season, and it is not a lost reward so much as a closed window: a season's ladder is claimable
        /// while that season is running, which is the same rule that already applies the moment the NEXT
        /// season starts and the stamp stops matching.
        /// <para/>
        /// Returning null rather than 0 is load-bearing on the content side. InqInt64Stat routes a null
        /// stat to EmoteCategory.TestNoQuality before its `stat ??= 0` coercion, and weenie 1003002 ships a
        /// TestNoQuality line for each tier ("You have set no time this season."). Coercing to 0 instead
        /// would sail under every tier's max_64 ceiling - which is exactly why all three of its
        /// InqInt64Stat rows also carry min_64 = 1.
        /// </summary>
        public static long? GetSeasonAwareSpeedBest(int? cachedSeasonId, long? cachedBest, int? activeSeasonId)
        {
            if (!activeSeasonId.HasValue)
                return null;

            return GetCachedSpeedBest(cachedSeasonId, cachedBest, activeSeasonId.Value);
        }

        /// <summary>
        /// The login-time rule for the personal-best cache: the stamped pair is stale, and so must be
        /// removed, only when there IS an active season and the stamp names a different one.
        /// <para/>
        /// A null <paramref name="activeSeasonId"/> is a scheduling GAP, not a season change, and clears
        /// nothing - see GetSeasonAwareSpeedBest for why this fails in the opposite direction to the emote
        /// read despite both being "fail closed". A gap says nothing about whether the stamped season has
        /// been superseded, and this branch DESTROYS the number, so silence must not be read as evidence.
        /// <para/>
        /// Pure and static for the same reason ShouldResetSpeedRewardClaims is: it is the whole decision
        /// behind the WorldManager.DoPlayerEnterWorld clear, and it is otherwise unreachable from a test.
        /// </summary>
        public static bool ShouldClearStaleSpeedBest(int? cachedSeasonId, int? activeSeasonId)
        {
            return activeSeasonId.HasValue
                && cachedSeasonId.HasValue
                && cachedSeasonId.Value != activeSeasonId.Value;
        }

        private void SpeedChallengeMsg(string message)
        {
            Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.System));
        }

        /// <summary>
        /// THE validity guard, and the load-bearing piece of this controller: the run must be armed, and the
        /// player must still be standing in the exact ephemeral instance it was bound to. Every hook calls this
        /// - arming, finishing, the death gate and the exit reconciliation - so a run is bound to exactly one
        /// instance and an abandoned one can never score late or score into another player's instance.
        /// <para/>
        /// Shape copied from the three sibling controllers' XChallengeRunValid(runInstance) - see
        /// Player_WaveChallenge.cs:141-148. Theirs takes the instance as a PARAMETER because their staged
        /// ActionChains capture it at schedule time and must reject a segment left over from an earlier run;
        /// this controller schedules nothing, so every call site would pass the field, which is exactly what
        /// OnWaveCreatureDied already does (Player_WaveChallenge.cs:440). The parameter is therefore dropped
        /// rather than carried as a redundant argument.
        /// </summary>
        private bool IsSpeedRunValid()
        {
            return speedRunInstance != 0
                && CurrentLandblock != null
                && Location != null
                && Location.Instance == speedRunInstance;
        }

        /// <summary>
        /// True while the player is armed for a speed run AND still standing in the bound run instance. Read by
        /// anything that needs to know a run is live.
        /// </summary>
        public bool IsInSpeedChallengeInstance => SpeedChallengeActive && IsSpeedRunValid();

        /// <summary>
        /// Converts an elapsed run duration in seconds to the stored score unit, CENTISECONDS (hundredths of a
        /// second), matching the existing BestWaveScoreCenti convention. Floored, mirroring the siblings' scoring
        /// conversions (Player_SurvivalChallenge.cs:299, Player_WaveChallenge.cs:623), so a time is never
        /// rounded down below what the player actually spent.
        /// <para/>
        /// Clamps at zero: Time.GetUnixTime() reads the wall clock, so a clock stepping backwards mid-run (an
        /// NTP correction, a manual change) would otherwise produce a negative elapsed value and record an
        /// unbeatable time. The caller logs the clamp; this stays pure so it can be unit tested.
        /// </summary>
        public static long ToSpeedRunCentiseconds(double elapsedSeconds)
        {
            if (!(elapsedSeconds > 0))   // also catches NaN
                return 0;

            var centi = Math.Floor(elapsedSeconds * 100);

            // an absurd elapsed value (a clock stepping FORWARD by years) would overflow the cast; saturate
            // rather than wrap into a negative time
            if (centi >= long.MaxValue)
                return long.MaxValue;

            return (long)centi;
        }

        /// <summary>
        /// Formats a centisecond time as m:ss.cc (e.g. 12345 -> "2:03.45"), the render used by the run
        /// announcement and by /top speed. Mirrors FormatWaveScore's role in Player_WaveChallenge.cs:120.
        /// </summary>
        public static string FormatSpeedRunTime(long centi)
        {
            if (centi < 0)
                centi = 0;

            var minutes = centi / 6000;
            var seconds = centi / 100 % 60;
            var hundredths = centi % 100;

            return $"{minutes}:{seconds:D2}.{hundredths:D2}";
        }

        /// <summary>
        /// Arms the run. Called on arrival in the season instance (from Portal.ActOnUse's teleport-completion
        /// follow-up), so the clock starts when the player LANDS, not when they click the portal. Binds the run
        /// to the player's current ephemeral instance, captures the start time and the season id, sets the
        /// persisted active flag, and announces the start.
        /// </summary>
        public void StartSpeedChallenge(SpeedSeason season)
        {
            // Mule (WaffleACE): a mule cannot fight, so it has no business in a Proving Grounds run.
            if (MuleBlocked(MuleAction.StartChallenge))
                return;

            if (season == null)
            {
                // The portal resolves the active season before it teleports, so arriving here with none means
                // the season rotated out in the window between the click and the landing, or the portal branch
                // is miswired. Either way the run cannot be filed anywhere, so it is not armed. The flag the
                // portal set ahead of the teleport is cleared harmlessly by CheckSpeedChallengeInstanceExit's
                // unbound-run branch on the very next teleport reconciliation.
                log.Error($"[SPEED] {Name} (0x{Guid}) - StartSpeedChallenge no-opped: no season was supplied. Check the season portal branch in Portal.ActOnUse and SpeedSeasonManager's registry.");
                return;
            }

            if (Location == null || !Location.IsEphemeralRealm)
            {
                // A run MUST be bound to a private ephemeral instance: the whole integrity model is one run, one
                // instance. Arming in the shared world would bind the run to a landblock other players share.
                log.Error($"[SPEED] {Name} (0x{Guid}) - StartSpeedChallenge no-opped for season {season.Id} ({season.Name}): the player is not in an ephemeral instance (location {(Location == null ? "null" : Location.ToLOCString())}). Check that the season portal carries PortalInstancing.");
                return;
            }

            speedRunInstance = Location.Instance;
            speedRunStartTime = Time.GetUnixTime();
            speedRunSeasonId = season.Id;
            SpeedChallengeActive = true;
            RushNextPlayerSave(5);

            SpeedChallengeMsg($"The trial begins! {season.DungeonName} - reach the objective as fast as you can. Your time starts now.");
        }

        /// <summary>
        /// The season's objective gate: does this object actually finish a run under this season?
        /// <para/>
        /// Pure and static on purpose. DESIGN section 7's central integrity rule is that the season row - not
        /// the bool flag an object carries - is the single authority on what finishing means, and that rule was
        /// otherwise unreachable from a unit test, because every other part of the funnel needs a live Player,
        /// a Session and a landblock. Keeping the comparison here means the rule itself is covered even though
        /// the funnel around it is not.
        /// <para/>
        /// Fails CLOSED on a null season: no season row means no declared objective, so nothing qualifies.
        /// </summary>
        public static bool IsSeasonObjective(uint sourceWcid, SpeedSeason season)
        {
            return season != null && sourceWcid == season.ObjectiveWcid;
        }

        /// <summary>
        /// The season's start object: does this object rebase the run's clock to now?
        /// <para/>
        /// Pure and static for the same reason as IsSeasonObjective, and deliberately mirrors its shape: the
        /// season row - not a bare bool flag - stays the single authority on what counts. StartWcid of 0 means
        /// "this season starts on arrival" (DESIGN's existing behaviour, and the SpeedSeasonPartial doc comment
        /// on the column), so a season that never opted into a lever start cannot be started by a stray flagged
        /// object left over from a different season's dungeon.
        /// <para/>
        /// Fails CLOSED on a null season and on an unset StartWcid.
        /// </summary>
        public static bool IsSeasonStartObject(uint sourceWcid, SpeedSeason season)
        {
            return season != null && season.StartWcid != 0 && sourceWcid == season.StartWcid;
        }

        /// <summary>
        /// Rebases the run's clock to now. A CLOCK REBASE, not a gate: the run stays armed and bound exactly as
        /// StartSpeedChallenge left it, so a player who never reaches or uses the start object is simply timed
        /// from arrival rather than left stuck in an "entered but not started" state that fights the forfeit and
        /// death paths. See the class doc comment for why a genuine gated-start state was rejected.
        /// <para/>
        /// Requires the run to be valid (IsSpeedRunValid - reused, not reimplemented), requires
        /// <paramref name="source"/> to be the season's declared start object, and refuses a second rebase so a
        /// start object that can be reused (or two of them) cannot rewind an in-progress time.
        /// </summary>
        /// <returns>true only when this call actually rebased the clock.</returns>
        public bool TryStartSpeedChallengeClock(WorldObject source)
        {
            if (!IsSpeedRunValid())
                return false;

            if (source == null || speedChallengeClockRebased)
                return false;

            var season = SpeedSeasonManager.GetSeason(speedRunSeasonId);

            if (!IsSeasonStartObject(source.WeenieClassId, season))
                return false;

            speedRunStartTime = Time.GetUnixTime();
            speedChallengeClockRebased = true;

            SpeedChallengeMsg("The trial has begun! Your time starts now.");

            return true;
        }

        /// <summary>
        /// The SINGLE completion funnel. Both objective hooks - the goal object's use path and
        /// OnSpeedChallengeBossDied - call this and nothing else finishes a run.
        /// <para/>
        /// <paramref name="source"/> is the object claiming to have completed the objective. It is gated against
        /// the SEASON's declared ObjectiveWcid, not merely against the bool flag it carries, so a flagged prop
        /// left over from another season (or a second prop of the same class placed as scenery) cannot end a run
        /// early - the season row stays the single authority on what finishing means.
        /// </summary>
        /// <returns>true only when this call actually finished a run.</returns>
        public bool TryFinishSpeedChallenge(WorldObject source)
        {
            if (!IsSpeedRunValid())
                return false;

            if (source == null)
                return false;

            var season = SpeedSeasonManager.GetSeason(speedRunSeasonId);

            if (season == null)
            {
                // Fails CLOSED. Without the season row there is no ObjectiveWcid to gate on and no season to
                // file the result into, so finishing here would either credit the wrong season or accept any
                // flagged object. The run stays armed - the player can still leave, which forfeits it - and the
                // content error is logged loudly. SpeedSeasonManager.Reload() is additive and never drops a
                // season a live run might reference, so this should be unreachable in practice.
                log.Error($"[SPEED] {Name} (0x{Guid}) - run armed under season {speedRunSeasonId}, which is no longer in the registry; refusing to finish. The run can only be forfeited.");
                return false;
            }

            if (!IsSeasonObjective(source.WeenieClassId, season))
                return false;

            var elapsedSeconds = Time.GetUnixTime() - speedRunStartTime;

            if (elapsedSeconds < 0)
            {
                log.Warn($"[SPEED] {Name} (0x{Guid}) - season {season.Id} run finished with a NEGATIVE elapsed time ({elapsedSeconds:N3}s); the server clock stepped backwards mid-run. Recording 0 rather than a negative time.");
            }

            var centiseconds = ToSpeedRunCentiseconds(elapsedSeconds);

            var finishMsg = $"Objective complete! Your time: {FormatSpeedRunTime(centiseconds)}.";

            // A season that declares a start object but whose lever was never pulled is timed from arrival
            // (StartSpeedChallenge's original clock) rather than rebased - that is the whole point of a
            // REBASE, not a gate: forgetting the lever is self-punishing, never a stuck run. The score itself
            // is never changed for this; the note is purely informational so the player understands why their
            // time reads long.
            if (season.StartWcid != 0 && !speedChallengeClockRebased)
                finishMsg += " (The trial lever was never pulled - your time was measured from arrival.)";

            SpeedChallengeMsg(finishMsg);

            // Persistence, the board cache and the record broadcast (DESIGN sections 3.5 and 5). The order
            // below is load-bearing rather than stylistic: BOTH pre-update reads must happen before anything
            // mutates, and the announcement order is copied from the Survival arena so the two arenas read
            // identically to a player (Player_SurvivalChallenge.cs:297-334).

            // 1. The PRE-update record bar. Reading it BEFORE the cache update is what lets the reigning
            //    holder beat their own record. Exempt players are excluded from the bar real players are
            //    measured against - the same treatment FinishSurvivalChallenge gives its own maxima.
            var exemptNames = LeaderboardExemptionManager.GetExemptAccountNames();
            var previousRecord = SpeedBoardManager.GetSeasonRecord(season.Id, exemptNames);

            // 2. The PRE-update personal best. A cached best stamped with a DIFFERENT season id reads as
            //    unset, which is why the first completion of a new season always announces a first best.
            var previousPersonalBest = GetCachedSpeedBest(SpeedChallengeSeasonId, BestSpeedRunCenti, season.Id);

            // 2b. The season rollover for the reward ladder. The Proving Grounds (Speed) reward NPC pays each
            //     tier once and remembers that with a quest flag, so a new season has to erase those flags or
            //     the ladder never re-opens. The emote system has no idea what the active season is, which is
            //     why this lives here rather than on the weenie.
            //
            //     THIS BLOCK'S POSITION IS LOAD-BEARING, both ends of it. It must run AFTER the
            //     previousPersonalBest read above and BEFORE step 6's personal-best write, because step 6
            //     overwrites SpeedChallengeSeasonId with THIS season - once that has happened the comparison
            //     below can only ever be false and the ladder never re-opens. Do not reorder it into step 6,
            //     and do not fold it into the step 6 condition.
            //
            //     And the test is the SEASON ID, never `previousPersonalBest == null` as a proxy for it.
            //     GetCachedSpeedBest also returns null for a player already in this season who simply has no
            //     cached best yet (their first clear of it, or a best zeroed by a backwards clock), so that
            //     proxy would erase the claims on every first clear WITHIN a season and make all three tiers
            //     farmable. Erase-then-restamp is safe against stamp farming here because the permanent
            //     QuestStampSeen_ ledger row survives the erase (Player_QuestStamps.HandleQuestStampRowCreated).
            if (ShouldResetSpeedRewardClaims(SpeedChallengeSeasonId, season.Id))
            {
                foreach (var claimQuest in SpeedRewardClaimQuests)
                    QuestManager.Erase(claimQuest);
            }

            // 3. The row. character_speed_run is the RECORD OF TRUTH, so this is the only durable write in
            //    the whole funnel. CompletedAt is stamped explicitly in UTC and NEVER left to the column
            //    default, which stamps the database server's LOCAL time. The values are captured into locals
            //    first because the callback runs on the shard queue's worker thread, which must not reach
            //    back into live Player state.
            var completedCharacterId = Character.Id;
            var completedName = Name;
            var completedSeasonId = season.Id;
            var completedLevel = Level ?? 0;
            var completedAt = DateTime.UtcNow;

            var row = new CharacterSpeedRun
            {
                CharacterId = completedCharacterId,
                CharacterName = completedName,
                SeasonId = completedSeasonId,
                Centiseconds = centiseconds,
                CharacterLevel = completedLevel,
                CompletedAt = completedAt,
            };

            DatabaseManager.Shard.AddSpeedRun(row, success =>
            {
                if (!success)
                    log.Error($"[SPEED] {completedName} (0x{completedCharacterId:X8}) - FAILED to persist the character_speed_run row for season {completedSeasonId} ({centiseconds} centiseconds, {FormatSpeedRunTime(centiseconds)}). That table is the record of truth, so this run is lost from it; the in-memory board still shows the time until the next restart rebuilds the board from the table.");
            });

            // 4. The cache. Deliberately updated SYNCHRONOUSLY on this world thread even though the insert
            //    above is asynchronous, so the announcements below are deterministic instead of racing the
            //    shard queue. If that insert then fails, the cache is AHEAD of the table until the next boot
            //    rebuilds it from the table - which is exactly why the failure callback logs loudly.
            SpeedBoardManager.RecordCompletion(new SpeedBoardEntry(completedCharacterId, completedName, completedSeasonId, centiseconds, completedLevel, completedAt));

            // 5. The record broadcast. An exempt player still gets their row, their cache entry and their
            //    personal best below - only the world broadcast is suppressed, same as Survival.
            if (centiseconds > 0
                && (previousRecord == null || centiseconds < previousRecord.Value)
                && !LeaderboardExemptionManager.IsExempt(this, exemptNames))
            {
                PlayerManager.BroadcastToAll(new GameMessageSystemChat(
                    $"[The Proving Grounds] {Name} cleared {season.DungeonName} in {FormatSpeedRunTime(centiseconds)} - a new record!",
                    ChatMessageType.WorldBroadcast));
            }

            // 6. The personal-best cache. ALWAYS write both properties together: a BestSpeedRunCenti with no
            //    matching SpeedChallengeSeasonId is indistinguishable from a stale one. The biota writes are
            //    persisted by the RushNextPlayerSave(5) already in the run-consume block below.
            if (previousPersonalBest == null || centiseconds < previousPersonalBest.Value)
            {
                BestSpeedRunCenti = centiseconds;
                SpeedChallengeSeasonId = season.Id;

                if (previousPersonalBest == null)
                    SpeedChallengeMsg($"New personal best: {FormatSpeedRunTime(centiseconds)}.");
                else
                    SpeedChallengeMsg($"New personal best: {FormatSpeedRunTime(centiseconds)} - you beat your previous best by {(previousPersonalBest.Value - centiseconds) / 100.0:N2} seconds.");
            }

            // consume the run so nothing can re-score it
            speedRunInstance = 0;
            speedRunSeasonId = 0;
            speedChallengeClockRebased = false;
            SpeedChallengeActive = false;
            RushNextPlayerSave(5);

            // return the player to where they came from (fallback: their lifestone), clearing the exit stamp
            var exitTo = GetPosition(PositionType.EphemeralRealmExitTo);
            var dest = exitTo != null ? new Position(exitTo) : Sanctuary;

            SetPosition(PositionType.EphemeralRealmExitTo, null);

            if (dest == null)
            {
                // No EphemeralRealmExitTo AND no Sanctuary: the run is consumed either way, but the teleport
                // below is skipped and the player is left standing in the finished instance with no automatic
                // way out. Nothing else reports this, so say so rather than inventing a destination they never
                // chose. Mirrors Player_WaveChallenge.cs:673.
                log.Error($"[SPEED] {Name} (0x{Guid}) - run finished but there is no destination to return to: EphemeralRealmExitTo is unset and Sanctuary is null. The player is stranded in the season instance and must recall out.");
            }
            else
            {
                var exitChain = new ActionChain();
                exitChain.AddDelaySeconds(3);   // brief pause so the player can read the result
                exitChain.AddAction(this, () => Teleport(dest));   // Teleport self-validates the instance destination
                exitChain.EnqueueChain();
            }

            return true;
        }

        /// <summary>
        /// Boss hook: called from Creature.Die() for a creature flagged PropertyBool.SpeedChallengeBoss, in the
        /// same shape OnWaveCreatureDied already uses for the Wave gauntlet
        /// (Player_WaveChallenge.cs:435-451). Forwards to the funnel, which re-validates the run and gates the
        /// creature's wcid against the season's declared objective.
        /// </summary>
        public void OnSpeedChallengeBossDied(Creature creature)
        {
            if (creature == null)
                return;

            TryFinishSpeedChallenge(creature);
        }

        /// <summary>
        /// True iff this player is dying inside a valid, active speed run - the gate for waiving the normal
        /// death penalties. Requires the persisted active flag AND that the player is still in the bound run
        /// instance, so a stale flag (e.g. after some non-portal exit) cannot turn an ordinary death into a
        /// penalty-free one. Mirrors IsWaveChallengeDeath (Player_WaveChallenge.cs:724-730).
        /// </summary>
        private bool IsSpeedChallengeDeath()
        {
            return IsInSpeedChallengeInstance;
        }

        /// <summary>
        /// Death-sequence entry point (called from Player.Die()). A death inside the season instance FORFEITS
        /// the run - no row is written, no time is recorded - but it is still penalty-free: returning true tells
        /// the caller to waive vitae and item loss (and the lifestone return) for the rest of the death
        /// sequence. Returns false, and does nothing at all, for an ordinary death.
        /// </summary>
        public bool TryBeginSpeedChallengeDeath()
        {
            speedChallengeDeathInProgress = IsSpeedChallengeDeath();

            if (speedChallengeDeathInProgress)
            {
                SpeedChallengeMsg("You have fallen. The run is forfeit - no time is recorded.");
                ForfeitSpeedChallenge();
            }

            return speedChallengeDeathInProgress;
        }

        /// <summary>
        /// True while the current death sequence is a speed-challenge death - read by the death-penalty skips in
        /// Player_Death.cs (death items, teleport destination), which run LATER in the death chain than
        /// TryBeginSpeedChallengeDeath and so cannot key off the already-cleared active flag. Mirrors
        /// WaveChallengeDeathInProgress (Player_WaveChallenge.cs:752).
        /// </summary>
        public bool SpeedChallengeDeathInProgress => speedChallengeDeathInProgress;

        /// <summary>
        /// Clears the speed-death marker once the death sequence has fully completed.
        /// </summary>
        public void EndSpeedChallengeDeath()
        {
            speedChallengeDeathInProgress = false;
        }

        // Unix time the player was last warned that leaving would forfeit. A second use of the exit portal
        // inside SpeedRunExitConfirmWindow is taken as a deliberate abandon and let through.
        private double speedRunExitPromptTime;

        private const double SpeedRunExitConfirmWindow = 30.0;

        /// <summary>
        /// Guards an instance-exiting portal against a SILENT forfeit. Returns TRUE when the player may leave.
        ///
        /// Without this, walking into the exit portal mid-run is indistinguishable from finishing: the run is
        /// forfeit, no row is written, and the only feedback is CheckSpeedChallengeInstanceExit's message AFTER
        /// the teleport has already happened. Reported from play - a runner killed the boss, took the portal, and
        /// lost the clear, because killing the boss is not what ends a run (picking up the season's ObjectiveWcid
        /// is, via TryFinishSpeedChallenge on the pickup hook).
        ///
        /// A completed run has already cleared SpeedChallengeActive, so this never blocks the way out after a
        /// legitimate finish, and it deliberately does NOT inspect inventory - the active flag alone is the
        /// question. Abandoning stays possible, it just has to be chosen twice.
        /// </summary>
        public bool CheckSpeedChallengeExitConfirmed()
        {
            if (!SpeedChallengeActive || speedRunInstance == 0)
                return true;

            if (Location == null || Location.Instance != speedRunInstance)
                return true;

            var now = Time.GetUnixTime();

            if (speedRunExitPromptTime > 0 && now - speedRunExitPromptTime <= SpeedRunExitConfirmWindow)
            {
                speedRunExitPromptTime = 0;
                return true;
            }

            speedRunExitPromptTime = now;
            SpeedChallengeMsg("Your run is still in progress - leaving now abandons it and records no time. Use the portal again within 30 seconds if that is what you want.");
            return false;
        }

        /// <summary>
        /// Reconciles the persisted run flag against the player's current instance after a teleport completes.
        /// Only death, a completion, and the login clear reset SpeedChallengeActive; every OTHER way out of the
        /// instance (lifestone/portal recall, /hometown, the exit portal, an admin teleport) leaves the flag set.
        /// Called from OnTeleportComplete: if the player is speed-active but no longer in their bound run
        /// instance, the run is forfeit. Because the arrival-arming callback (StartSpeedChallenge) runs before
        /// the client's teleport-complete ack, a genuine season arrival already has
        /// speedRunInstance == Location.Instance here and is left untouched; a flag left set by a
        /// StartSpeedChallenge that no-opped (run instance still 0) is cleared silently.
        /// </summary>
        public void CheckSpeedChallengeInstanceExit()
        {
            if (!SpeedChallengeActive)
                return;

            // still inside the bound, started run - nothing to do
            if (speedRunInstance != 0 && Location != null && Location.Instance == speedRunInstance)
                return;

            // a started run whose instance we have now left - tell the player they gave it up
            if (speedRunInstance != 0)
                SpeedChallengeMsg("You have abandoned the run. No time is recorded.");

            ForfeitSpeedChallenge();
        }

        /// <summary>
        /// Forfeit: ends an in-progress speed run with NO time recorded and no row written. Used by the death
        /// path and by the instance-exit reconciliation. A mid-run logout is handled instead by the login clear
        /// in WorldManager (mirrors DPS, Defense and Wave).
        /// </summary>
        public void ForfeitSpeedChallenge()
        {
            if (!SpeedChallengeActive)
                return;

            speedRunInstance = 0;
            speedRunSeasonId = 0;
            speedChallengeClockRebased = false;
            SpeedChallengeActive = false;
            RushNextPlayerSave(5);
        }
    }
}
