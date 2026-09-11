using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Registry of live runs (TECH-DESIGN S5). Runs are keyed by instance id. Tick() runs on the world thread
    /// from WorldManager.UpdateGameWorld; it hands every Starting run whose copy has finished loading to
    /// ThreadDungeonSpawner.TryPopulate, and reaps expired runs and runs whose landblock has already
    /// unloaded. EndRun is the single exit path and is what destroys the bound gem (cleanup layer (a), Q10).
    ///
    /// Threading. Tick() runs on the world thread. Nothing else here is guaranteed to: OnRunCreatureDied()
    /// runs on whichever landblock tick thread killed the creature, and TryStart() runs on whatever thread
    /// reached the gem's ActOnUse - a gem used from the ground arrives through a landblock action chain
    /// (Player_Move.cs:71-76), not the world thread. So the registry is a ConcurrentDictionary, every mutable
    /// field of a ThreadDungeonRun is guarded by that run's own lock (see ThreadDungeonRun), and the
    /// admission sequence in TryStart - cap checks, instance-id allocation, registration - is serialised
    /// under <see cref="startLock"/> so two simultaneous gem uses cannot both pass the same cap.
    /// </summary>
    public static class ThreadDungeonManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ConcurrentDictionary<uint, ThreadDungeonRun> runs = new ConcurrentDictionary<uint, ThreadDungeonRun>();
        /// <summary>
        /// Two cadences, one pass. The tick is 1 s because a Starting run must be populated as soon as its
        /// copy finishes loading - the player is standing in it waiting - while the reaping decision (TTL,
        /// landblock gone, load timeout) is measured in minutes and does not need to be re-taken every
        /// second, so it keeps its own 15 s timer.
        /// </summary>
        private static DateTime nextTick = DateTime.MinValue;
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

        private static DateTime nextReap = DateTime.MinValue;
        private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Serialises the admission sequence in TryStart. Held only across the cap checks, the instance-id
        /// allocation and the registry insert - never across GetNewThreadDungeonLandblock, which loads a
        /// landblock and must not run under a static lock.
        /// </summary>
        private static readonly object startLock = new object();

        /// <summary>
        /// How long a run may sit in Starting with its landblock loaded but CreateWorldObjects unfinished
        /// before it is reaped. The seam in Landblock.CreateWorldObjects rethrows on a filter failure, which
        /// leaves CreateWorldObjectsCompleted false forever; a player teleporting in then retries
        /// materialisation on a 0.1s chain indefinitely and stays in pink bubbles (Player_Location.cs:906-917).
        /// </summary>
        private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long after a run reaches Cleared it may still be reaped for standing empty. The run ends on the
        /// first reap pass that finds BOTH no player inside the copy AND this much time elapsed since the
        /// clear; either condition alone keeps it alive.
        ///
        /// 30 seconds, chosen for the one case where "nobody is inside" is a lie rather than a fact: entry is
        /// asynchronous. A run whose plan placed nothing is Cleared the instant it is populated - which can be
        /// before its owner has materialised - and the teleport in can defer a second behind a fog chain on
        /// the player's own landblock queue (Player_Location.cs:798-807) and then retry materialisation on a
        /// 0.1s chain. A player who is inside and looting is covered by the player probe on its own and needs
        /// no grace at all; this window exists so a player who is on their way in is never met with a
        /// collapsing dungeon. It is an order of magnitude longer than the entry path needs and still an order
        /// of magnitude shorter than the five minutes a copy would otherwise idle for.
        ///
        /// Not a PropertyManager tunable, on purpose: ShouldEnd is pure (see its doc comment) and every input
        /// it needs is passed in.
        /// </summary>
        private static readonly TimeSpan ClearedGrace = TimeSpan.FromSeconds(30);

        public static ThreadDungeonStore Store { get; private set; } = ThreadDungeonStore.Empty;

        public static bool IsEnabled => PropertyManager.GetBool("dynamic_dungeons_enabled").Item;

        public static IReadOnlyList<ThreadDungeonRun> LiveRuns => runs.Values.Where(r => r.State != ThreadDungeonRunState.Ended).ToList();

        /// <summary>
        /// Validate into a LOCAL, then publish once. Both entry points do this identically, and the ordering
        /// is load-bearing rather than stylistic: <see cref="ValidateWorldWcids"/> can DROP a salvage_affinity
        /// modifier whose base wcid does not resolve, and that drop is a write to the plain Dictionary backing
        /// <see cref="ThreadDungeonStore.Modifiers"/>.
        ///
        /// <see cref="Reload"/> runs live from an admin command while runs are in flight, and Modifiers is read
        /// unsynchronized from several other threads by then - the world thread through
        /// ThreadDungeonSpawner.TryPopulate into DungeonPopulationBuilder.Build, a landblock action-chain
        /// thread through the gem's ActOnUse, and the appraisal path in ThreadDungeonGemHandler.
        /// Dictionary&lt;TKey,TValue&gt; guarantees nothing under a concurrent write: it can throw or return
        /// corrupted data. Assigning Store first and validating afterwards would open exactly that window on
        /// every reload that happened to find an unresolvable row.
        ///
        /// Keeping the assignment LAST closes it entirely, because nothing else can hold a reference to the
        /// new store until the only thread that can mutate it has finished.
        /// </summary>
        public static void Initialize()
        {
            var store = ThreadDungeonStore.LoadFromServerConfig();
            ValidateBossWeenies(store);
            ValidateWorldWcids(store);
            Store = store;
        }

        /// <summary>Reloads the store from disk. Same validate-then-publish ordering as <see cref="Initialize"/>, and for the reason given there.</summary>
        public static void Reload()
        {
            var store = ThreadDungeonStore.LoadFromServerConfig();
            ValidateBossWeenies(store);
            ValidateWorldWcids(store);
            Store = store;
        }

        /// <summary>
        /// Logs one WARN per attunement.json component and per salvage_affinity modifier whose wcid has no
        /// weenie in the world database. A no-op when DatabaseManager.World is null (unit-test harness),
        /// which is also why a missing salvage base cannot be checked at parse time. See
        /// ThreadDungeonStore.ValidateWorldWcids for which of the two is dropped and which is only reported.
        /// </summary>
        private static void ValidateWorldWcids(ThreadDungeonStore store)
        {
            // Deliberately NOT also gated on store.Attunement: the salvage half of this check has nothing to
            // do with attunement, and gating on it would silently skip the modifier sweep whenever
            // attunement.json failed to load.
            if (DatabaseManager.World == null || store == null)
                return;

            foreach (var missing in store.ValidateWorldWcids(wcid => DatabaseManager.World.GetCachedWeenie(wcid) != null))
                log.Warn($"[DYNDUNGEON] {missing}");
        }

        /// <summary>
        /// Diagnostic only: logs one warning per bosses.json entry whose weenie cannot actually be killed by a
        /// player (missing, not a Creature, Attackable false, or PlayerKillerStatus.NPC). It does not filter
        /// Store.Bosses - DungeonPopulationBuilder still draws from the full list - because
        /// ThreadDungeonSpawner.TryPlace refuses such a creature at spawn time and reports bossWcid 0 to
        /// MarkPopulated, which already keeps the run clearable. This pass exists so a bad bosses.json entry is
        /// caught in the log at load time rather than only discovered the next time that entry's level window
        /// happens to be drawn.
        ///
        /// A no-op when DatabaseManager.World is null, which is the case under the unit-test harness.
        /// </summary>
        private static void ValidateBossWeenies(ThreadDungeonStore store)
        {
            if (DatabaseManager.World == null || store?.Bosses == null)
                return;

            foreach (var boss in store.Bosses)
            {
                var weenie = DatabaseManager.World.GetCachedWeenie(boss.Wcid);

                if (weenie == null)
                {
                    log.Warn($"[DYNDUNGEON] bosses.json wcid {boss.Wcid} '{boss.Name}': no such weenie in the world database");
                    continue;
                }

                if (weenie.WeenieType != WeenieType.Creature)
                {
                    log.Warn($"[DYNDUNGEON] bosses.json wcid {boss.Wcid} '{boss.Name}': weenie type is {weenie.WeenieType}, not Creature");
                    continue;
                }

                var attackable = weenie.GetProperty(PropertyBool.Attackable) ?? true;
                var playerKillerStatus = (PlayerKillerStatus?)weenie.GetProperty(PropertyInt.PlayerKillerStatus) ?? PlayerKillerStatus.NPK;

                if (!DungeonPopulationBuilder.IsKillable(attackable, playerKillerStatus))
                {
                    var reason = !attackable ? "not attackable" : "flagged PlayerKillerStatus.NPC";
                    log.Warn($"[DYNDUNGEON] bosses.json wcid {boss.Wcid} '{boss.Name}': {reason}");
                }
            }
        }

        public static ThreadDungeonRun GetRun(uint instance)
            => runs.TryGetValue(instance, out var run) && run.State != ThreadDungeonRunState.Ended ? run : null;

        public static ThreadDungeonRun GetRunForOwner(uint ownerGuid)
            => runs.Values.FirstOrDefault(r => r.OwnerGuid == ownerGuid && r.State != ThreadDungeonRunState.Ended);

        public static int LiveRunCountForAccount(uint accountId)
            => runs.Values.Count(r => r.AccountId == accountId && r.State != ThreadDungeonRunState.Ended);

        /// <summary>
        /// Starting a run implicitly ends the owner's own FINISHED one (owner ruling, 2026-09-06: "This
        /// should not need to be manually managed by the player"). A Cleared run has already destroyed its
        /// gem (AnnounceCleared -> GemDestroyer, ruling A1), so there is nothing left for the player to spend
        /// or hand back and no way for them to re-enter it - but the copy itself lingers, and while it does
        /// the start gate below refuses their next dungeon with "You already have a dungeon open".
        ///
        /// Reaping it AT THE MOMENT THEY ACT is strictly better than either alternative. Letting a Cleared
        /// run through the gate would leave a player free to chain-open copies faster than they reap and
        /// occupy several of the server-wide slots at once; making them wait for the automatic reap is the
        /// very thing the ruling forbids. This way the player never waits AND the per-owner footprint is
        /// pinned at exactly one loaded run.
        ///
        /// Scoped to the OWNER, never to the account, and that asymmetry is load-bearing. A gem cannot be
        /// used from inside an ephemeral realm (DungeonGemRules.RefusesFromInstance), so the caller is
        /// provably not standing in the run being ended, and admission is by owner guid
        /// (DungeonGemRules.DecideReEntry) so nobody else can be in there either. Sweeping the whole ACCOUNT
        /// would carry no such proof: it could collapse a copy around a DIFFERENT character of the same
        /// account who is still inside looting. That case is left to the timed reap in ShouldEnd, which does
        /// probe for players first.
        /// </summary>
        private static void EndClearedRunsForOwner(uint ownerGuid)
        {
            foreach (var stale in runs.Values.Where(r => r.OwnerGuid == ownerGuid && r.State == ThreadDungeonRunState.Cleared).ToList())
                EndRun(stale, DungeonRunTelemetry.EndReasons.Superseded);
        }

        /// <summary>
        /// Opens the private copy. On success the run is registered, the landblock is loading (Init runs async;
        /// the next Tick that sees CreateWorldObjectsCompleted hands it to ThreadDungeonSpawner.TryPopulate)
        /// and run.EntryPosition / run.ExitTo are set.
        /// Every refusal returns false with a player-facing reason and registers nothing.
        ///
        /// Order matters: every refusal is decided BEFORE the ephemeral instance id is allocated, so a refused
        /// start leaks neither an instance id nor a registry entry, and the caller's gem is never consumed.
        /// </summary>
        /// <param name="startGroup">
        /// Telemetry grouping for the runs one GEM USE produced (see ThreadDungeonRun.StartGroup). Null
        /// mints a fresh group, which is correct for every caller today - nothing retries, so each use is
        /// its own group of one and COUNT(DISTINCT start_group) equals the row count. A retry loop passes
        /// the SAME id into each of its attempts, and the two counts then diverge by exactly the fault rate.
        /// </param>
        public static bool TryStart(Player owner, WorldObject gem, DungeonGemSpec spec, DungeonEntryDef dungeon, out ThreadDungeonRun run, out string reason, string startGroup = null)
        {
            run = null;
            reason = null;

            if (!IsEnabled) { reason = "Threads are not enabled on this server."; return false; }
            if (dungeon == null || !dungeon.Enabled) { reason = "That dungeon is not currently available."; return false; }
            if (!TryParseEntry(dungeon.Entry, out var entry)) { reason = "That dungeon's entry point is misconfigured."; log.Error($"[DYNDUNGEON] dungeon {dungeon.Id} entry '{dungeon.Entry}' unparseable"); return false; }

            var maxRuns = (int)PropertyManager.GetLong("dynamic_dungeons_max_concurrent_runs").Item;
            var perAccount = (int)PropertyManager.GetLong("dynamic_dungeons_max_runs_per_account").Item;
            var ttl = TimeSpan.FromMinutes(Math.Max(5, PropertyManager.GetLong("dynamic_dungeons_run_ttl_minutes").Item));
            var clearFraction = Math.Clamp(PropertyManager.GetDouble("dynamic_dungeons_clear_fraction").Item, 0.0, 1.0);
            var bossWeight = Math.Clamp(PropertyManager.GetDouble("dynamic_dungeons_boss_clear_weight").Item, 0.0, 1.0);

            uint instance;

            // Retire this owner's finished run before the gates below look at it. See EndClearedRunsForOwner.
            //
            // OUTSIDE startLock, deliberately. EndRun is not registry arithmetic: it evicts players, reaches
            // into LandblockManager to queue a copy for destruction and enqueues gem destruction onto the
            // owner's action queue. startLock is documented to cover only the cheap admission sequence and is
            // never held across a landblock lifecycle call (GetNewThreadDungeonLandblock is kept out of it
            // for the same reason), and widening it to span EndRun would put a static lock in front of
            // LandblockManager's own locking for every start. Nothing else in the process takes startLock, so
            // there is no deadlock to reason about either way - this is about not creating one later.
            //
            // The gap this leaves is benign and self-correcting. A run of this owner's would have to reach
            // Cleared between the sweep and the lock, and the owner cannot be the one clearing it: a gem is
            // refused from inside any ephemeral realm (DungeonGemRules.RefusesFromInstance), and a private
            // copy admits nobody but its owner, so with the caller standing outside there is no one left in
            // there to land the last kill. If it somehow happened, the gate below returns the ordinary
            // refusal and the player's very next attempt sweeps the run and succeeds - no stuck state, so a
            // retry loop here would buy nothing but a second EndRun call site.
            EndClearedRunsForOwner(owner.Guid.Full);

            // The admission sequence is one critical section. Every check below is a read of `runs` followed by
            // a write to it, and TryStart is not guaranteed to run on the world thread (a gem used from the
            // ground reaches ActOnUse through a landblock action chain), so two simultaneous gem uses could
            // otherwise both observe "one run below the cap" and both register. The lock covers the checks, the
            // id allocation and the insert; loading the landblock happens after it is released.
            lock (startLock)
            {
                // All three gates count every non-Ended run, the pre-fix behaviour. The sweep above is what
                // stops a finished run reaching them, so the gates stay a simple "one run per owner" rule and
                // a player's footprint stays bounded at one loaded copy.
                if (GetRunForOwner(owner.Guid.Full) != null) { reason = "You already have a dungeon open. Give its gem to the Fragment Press to close it."; return false; }
                if (maxRuns > 0 && LiveRuns.Count >= maxRuns) { reason = "The dungeon gates are busy. Try again in a few minutes."; return false; }
                if (perAccount > 0 && LiveRunCountForAccount(owner.Account.AccountId) >= perAccount) { reason = "Your account already has a dungeon open."; return false; }

                instance = LandblockManager.RequestNewEphemeralInstanceIDv1(0);

                // owner.Level is captured HERE, not resolved at EndRun, because the level that answers "was
                // this gem appropriate for this player" is the level they walked in with. See
                // ThreadDungeonRun.OwnerLevelAtStart for why the end-time value is biased toward clears.
                run = new ThreadDungeonRun(instance, owner.Guid.Full, owner.Name, owner.Account.AccountId, gem.Guid.Full, spec, dungeon, DateTime.UtcNow, ttl, owner.Level ?? 0, startGroup)
                {
                    ExitTo = new Position(owner.Location),
                    ClearFraction = clearFraction,
                    BossWeight = bossWeight,
                };

                entry.Instance = instance;
                run.EntryPosition = entry;

                if (!runs.TryAdd(instance, run)) { reason = "The dungeon could not be opened."; run = null; return false; }
            }

            var landblock = RealmManager.GetNewThreadDungeonLandblock(new LandblockId((uint)dungeon.Landblock << 16), owner, run, instance);

            // TECH-DESIGN section 6 step 3d. A landblock with a traversable overworld is not a dungeon and must
            // never be instanced: the copy would be a private slice of the open world. Mirrors the guard
            // Portal.cs:569-573 applies to PortalInstancing. Curation should make this unreachable, so it is an
            // error rather than a warning, and the half-built copy is disposed of rather than left loaded.
            if (landblock == null || !landblock.IsDungeon)
            {
                log.Error($"[DYNDUNGEON] dungeon {dungeon.Id} landblock 0x{dungeon.Landblock:X4} is not a dungeon (or failed to load) - refusing to open a run");

                if (landblock != null)
                    LandblockManager.AddToDestructionQueue(landblock);

                runs.TryRemove(instance, out _);
                reason = "The dungeon could not be opened.";
                run = null;
                return false;
            }

            log.Info($"[DYNDUNGEON] started {run} spec={spec.Serialize()}");
            return true;
        }

        /// <summary>
        /// "0x0150018A 30 0 0.005 1 0 0 0" -> Position (instance 0; the caller stamps it).
        ///
        /// The tail is the /loc quaternion order qw qx qy qz, while the Position constructor
        /// (ACE.Entity/Position.cs:246) takes its rotation w LAST, so field 4 is passed after fields 5-7.
        /// </summary>
        public static bool TryParseEntry(string entry, out Position position)
        {
            position = null;
            if (string.IsNullOrWhiteSpace(entry)) return false;
            var p = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) return false;
            var cellText = p[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? p[0].Substring(2) : p[0];
            if (!uint.TryParse(cellText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var cell)) return false;
            float F(int i, float dflt) => i < p.Length && float.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : dflt;
            position = new Position(cell, F(1, 0), F(2, 0), F(3, 0), F(5, 0), F(6, 0), F(7, 0), F(4, 1), 0);
            return true;
        }

        public static void OnRunCreatureDied(Creature creature)
        {
            var run = creature.P_DungeonRun;
            if (run == null || !runs.TryGetValue(run.RunId, out var live) || !ReferenceEquals(live, run))
                return;

            run.RecordKill(creature.DungeonRole == DungeonRole.Boss);

            // The boss cache, BEFORE the clear announcement, so on the kill that does both the player reads
            // "A Thread Cache has formed where the boss fell." above "... is cleared and your gem crumbles to
            // dust." rather than below it. Exactly-once is the run's (TryClaimBossChest), and the cache is
            // placed at the boss's AUTHORED anchor, not at the corpse - see ThreadDungeonRewardSpawner.
            if (creature.DungeonRole == DungeonRole.Boss)
                BossCacheSpawner(run);

            AnnounceCleared(run);

            // Ordered AFTER AnnounceCleared, and the order is load-bearing rather than cosmetic. RecordKill
            // has already run CheckClearedLocked, so the kill that finishes the run left Active before either
            // call; TryGetKillProgress reports nothing in that case and the clear line stands alone, which is
            // ruling R32's "the clear message is the only message" rule. Putting this first would still be
            // correct for the same reason, but reading the two calls in the order the player sees them is
            // what makes that obvious.
            AnnounceProgress(run);
        }

        /// <summary>
        /// Called by ThreadDungeonSpawner on the landblock action queue, immediately after the run's one
        /// MarkPopulated. Its only job today is the clear announcement: a plan that placed NOTHING (no
        /// eligible family, no boss, or a copy whose every placement failed) reaches Cleared the instant it
        /// is populated, and no creature death will ever arrive to announce it - the run would look broken
        /// to the player instead of empty.
        /// </summary>
        public static void OnRunPopulated(ThreadDungeonRun run)
        {
            if (run == null)
                return;

            AnnounceCleared(run);
        }

        /// <summary>
        /// Tells the owner their dungeon is clear, once. Both entry points reach this from a LANDBLOCK
        /// thread, never the world thread, which is fine: it only reads the run under its own lock and hands
        /// a message to the session's own network queue.
        ///
        /// Exactly-once is the run's job: TryClaimClearedAnnouncement latches under the run's own lock and
        /// returns true to one caller only, so the two entry points cannot both print the message.
        /// </summary>
        private static void AnnounceCleared(ThreadDungeonRun run)
        {
            if (!run.TryClaimClearedAnnouncement())
                return;

            var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);
            owner?.Session?.Network.EnqueueSend(new GameMessageSystemChat($"{run.Dungeon.Name} is cleared and your gem crumbles to dust. Take the exit portal when you are ready; the dungeon stands for a while yet.", ACE.Entity.Enum.ChatMessageType.Broadcast));
            log.Info($"[DYNDUNGEON] cleared {run}");

            // The completion visual and the summoned exit, owner-only. AFTER the clear line, so the effect
            // lands on a message the player has already read, and BEFORE the gem/survey bookkeeping, which is
            // invisible to them. Owner may be null (offline); the handler returns on that before it reads a
            // single tunable, which is what keeps AnnounceCleared callable from the unit tests.
            ClearRewardHandler(run, owner);

            GemDestroyer(run);
            SurveyRecorder(run);
        }

        /// <summary>
        /// Test seam for the boss cache, same shape as <see cref="GemDestroyer"/> and
        /// <see cref="SurveyRecorder"/> below: a swappable delegate so a test can observe the call without a
        /// world. Production points at ThreadDungeonRewardSpawner.
        /// </summary>
        internal static Action<ThreadDungeonRun> BossCacheSpawner = ThreadDungeonRewardSpawner.TrySpawnBossCache;

        /// <summary>
        /// Test seam for the clear-time rewards - the completion visual and the summoned exit - on the same
        /// swappable-delegate pattern as <see cref="BossCacheSpawner"/>. The Player is passed rather than
        /// re-resolved so the handler cannot disagree with the owner AnnounceCleared just messaged.
        /// </summary>
        internal static Action<ThreadDungeonRun, Player> ClearRewardHandler = ThreadDungeonRewardSpawner.OnRunCleared;

        /// <summary>
        /// Tells the owner where they stand after EVERY kill (owner ruling R32 as amended 2026-09-08:
        /// "After each kill, the player should see a message 'Monsters killed x/y. Boss still remaining'").
        /// This replaced the 25/50/75 "% remaining" milestones the first form of R32 shipped - the counts say
        /// the same thing without asking the player to convert a percentage back into corpses.
        ///
        /// y is the CLEAR TARGET, not the population: a run does not have to be emptied, so quoting the total
        /// placed would show a target the player never has to reach. See ThreadDungeonRun.TryGetKillProgress
        /// for why the boss is a separate sentence rather than a term in that fraction.
        ///
        /// Called only from OnRunCreatureDied, i.e. from the LANDBLOCK thread that recorded the kill, exactly
        /// as AnnounceCleared is - it reads the run only through the run's own lock and then hands a message
        /// to the session's own network queue, so nothing here touches the world thread. There is no
        /// OnRunPopulated entry point on purpose: a run that reaches Cleared on populate has no progress to
        /// narrate, and one that does not has had no kills to report.
        ///
        /// The line-per-kill discipline is the run's: TryGetKillProgress reports only while Active, so the
        /// kill that clears the run says nothing here and the clear message stands alone.
        /// </summary>
        private static void AnnounceProgress(ThreadDungeonRun run)
        {
            if (!run.TryGetKillProgress(out var killed, out var required, out var bossRemaining))
                return;

            var line = bossRemaining
                ? $"Monsters killed {killed}/{required}. Boss still remaining."
                : $"Monsters killed {killed}/{required}.";

            var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);
            owner?.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ACE.Entity.Enum.ChatMessageType.Broadcast));
            log.Info($"[DYNDUNGEON] progress {killed}/{required} boss_remaining={bossRemaining} for {run}");
        }

        /// <summary>
        /// Test seam: production code always sets this to RecordSurvey. Tests swap it for a counting
        /// delegate (and restore it in a finally) to observe the trigger without a live player.
        /// </summary>
        internal static Action<ThreadDungeonRun> SurveyRecorder = RecordSurvey;

        /// <summary>
        /// Daily-survey stamp hook (PHASE-2-DESIGN.md section 4.1, owner ruling B3). Files a survey for the
        /// owner when the run's gem level meets the floor, on the owner's own action queue - never inline on
        /// the landblock thread that calls AnnounceCleared. A run whose owner is offline at the moment of the
        /// clear files no survey (ruling P2-R4): there is no queue to enqueue onto.
        /// </summary>
        public static void RecordSurvey(ThreadDungeonRun run)
        {
            var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);
            if (owner == null)
                return;

            // Ruling P2-R22: a run whose plan placed nothing reaches Cleared via OnRunPopulated with no
            // clear to speak of - there was nothing to kill - so it must not file a survey either.
            if (run.Spawned <= 0)
            {
                log.Info($"[DYNDUNGEON] survey not filed for {run}: nothing spawned");
                return;
            }

            var minLevel = (int)Math.Clamp(PropertyManager.GetLong("dynamic_dungeons_survey_min_level").Item, 0, int.MaxValue);
            if (!DungeonSurveyRules.Counts(run.Spec.Level, minLevel))
            {
                log.Info($"[DYNDUNGEON] survey not filed for {run}: gem level {run.Spec.Level} < {minLevel}");
                return;
            }

            var runText = run.ToString();

            // Captured before the closure for the same reason runText is: the run's spec is what the survey
            // records, and reading it later would read whatever the run has become by then.
            var gemLevel = run.Spec.Level;

            owner.EnqueueAction(new ActionEventDelegate(() =>
            {
                var n = DungeonSurveyRules.Record(new QuestManagerSurveyLedger(owner.QuestManager));

                // The difficulty ring (owner ruling R1: GEM level, not the levels actually spawned). Written
                // INSIDE this delegate, after the ledger write and before MarkSurveyFiled, so the ring can
                // never record a survey that was not filed - which is the whole reason MarkSurveyFiled sits
                // where it does. Parse tolerates an absent or corrupt value by reading an empty ring, so this
                // write also self-heals a damaged property.
                var ring = SurveyLevelRing.Parse(owner.GetProperty(PropertyString.DungeonSurveyLevels)).Push(gemLevel);
                owner.SetProperty(PropertyString.DungeonSurveyLevels, ring.Serialize());

                // Latched HERE, inside the enqueued action and after the ledger write, not above where the
                // decision to try was taken. The two differ whenever the owner detaches between the clear and
                // this delegate running - EnqueueAction discards the work in that case
                // (WorldObject_Tick.cs:144-155) - and survey_filed has to mean the survey exists.
                run.MarkSurveyFiled();

                owner.Session?.Network.EnqueueSend(new GameMessageSystemChat($"Your survey is filed. The ledger counts {n} under your name today.", ACE.Entity.Enum.ChatMessageType.Broadcast));
                log.Info($"[DYNDUNGEON] survey filed by {owner.Name} count={n} for {runText}");
            }));
        }

        /// <summary>
        /// Test seam: production code always sets this to DestroyGem. Tests swap it for a counting
        /// delegate (and restore it in a finally) to observe the trigger without a live player.
        /// </summary>
        internal static Action<ThreadDungeonRun> GemDestroyer = DestroyGem;

        /// <summary>
        /// The reaping decision, extracted as a pure function so it can be tested without a world (visible to
        /// ACE.Server.Tests via InternalsVisibleTo in ACE.Server.csproj:15). It reads no PropertyManager
        /// tunable, deliberately: PropertyManager reads throw under the unit-test harness.
        /// Returns true when the run must be ended, with the reason EndRun should log.
        ///
        /// The four ways a run dies on its own, in priority order:
        ///
        /// 1. TTL. Checked first, so an expired run reports "expired" whatever else is wrong with it.
        /// 2. Its landblock is gone. A copy unloads by itself five minutes after the last player leaves, and
        ///    once it is gone the run has nowhere to happen. This is deliberately NOT gated on the state:
        ///    GetLandblock registers the instance in LandblockManager's ephemeral instance registry
        ///    (EphemeralInstanceRegistry, reached through LandblockManager.GetEphemeralLandblock)
        ///    synchronously, inside the write lock and before Init() runs, so a healthy Starting run always
        ///    has a landblock. A Starting run with none is one whose copy never came up, and gating this on
        ///    state left it pinned in the registry for the whole TTL - blocking its owner out of the feature
        ///    for three hours by way of GetRunForOwner and LiveRunCountForAccount.
        /// 3. Its landblock is up but never finished loading. The content-filter seam in
        ///    Landblock.CreateWorldObjects rethrows on failure, which leaves CreateWorldObjectsCompleted false
        ///    permanently; a player who teleports in then re-queues OnTeleportComplete every 0.1s forever and
        ///    never materialises (Player_Location.cs:906-917). Only meaningful while Starting, since the flag
        ///    is set once and never cleared.
        /// 4. It is Cleared, someone HAS been inside it, nobody is now, and it is past
        ///    <see cref="ClearedGrace"/>. A finished run has nothing left to do: its gem was destroyed at the
        ///    clear, so nobody can re-enter it, and the last player has left. Before this branch existed such
        ///    a run idled to its TTL - hours - which is what made a player who had finished and walked out
        ///    wait to open their next dungeon.
        ///
        ///    Three conditions, and the order they are written in is the order of their importance.
        ///    <paramref name="playerEverObserved"/> is the correctness one: "nobody is inside" is a fact for
        ///    a run somebody entered and left, but for one whose owner is still in transit it only means
        ///    "not yet", and the caller's probe cannot tell those apart (see
        ///    ThreadDungeonRun.PlayerEverObserved). A run nobody ever entered is left to branches 1 and 3.
        ///    <paramref name="playerPresent"/> is the behavioural one: a player still looting inside keeps
        ///    the copy up indefinitely, exactly as before. The grace is then a second line rather than the
        ///    only protection, for the entry race described on ClearedGrace.
        /// </summary>
        internal static bool ShouldEnd(ThreadDungeonRunState state, bool landblockPresent, bool createCompleted, bool playerEverObserved, bool playerPresent, TimeSpan sinceStart, TimeSpan sinceCleared, DateTime now, DateTime expiresUtc, out string reason)
        {
            reason = null;

            if (state == ThreadDungeonRunState.Ended)
                return false;

            // The reason strings are constants rather than literals because DungeonRunTelemetry.EndStateFor
            // maps them onto the analytics end_state vocabulary. Retyping one here and leaving the mapping
            // alone would silently reclassify the run as 'abandoned', which is the mapping's default.
            if (now >= expiresUtc)
            {
                reason = DungeonRunTelemetry.EndReasons.Expired;
                return true;
            }

            if (!landblockPresent)
            {
                reason = DungeonRunTelemetry.EndReasons.LandblockUnloaded;
                return true;
            }

            if (state == ThreadDungeonRunState.Starting && !createCompleted && sinceStart > LoadTimeout)
            {
                reason = DungeonRunTelemetry.EndReasons.LoadFailed;
                return true;
            }

            if (state == ThreadDungeonRunState.Cleared && playerEverObserved && !playerPresent && sinceCleared >= ClearedGrace)
            {
                reason = DungeonRunTelemetry.EndReasons.ClearedAndEmpty;
                return true;
            }

            return false;
        }

        public static void Tick()
        {
            var now = DateTime.UtcNow;
            if (now < nextTick) return;
            nextTick = now + TickInterval;

            var reap = now >= nextReap;
            if (reap) nextReap = now + ReapInterval;

            foreach (var run in runs.Values.ToList())
            {
                // One snapshot per pass. State is read under the run's own lock and a landblock thread can
                // move it (a kill that clears the run) between two reads, so the reap decision and the
                // populate decision below must be taken against the same value.
                var state = run.State;

                if (state == ThreadDungeonRunState.Ended)
                {
                    // Belt and braces: EndRun already removes the run. This catches anything that reached
                    // Ended by another path.
                    runs.TryRemove(run.RunId, out _);
                    continue;
                }

                var landblock = LandblockManager.GetEphemeralLandblock(run.Instance);
                var createCompleted = landblock != null && landblock.CreateWorldObjectsCompleted;

                if (reap)
                {
                    // Sampled only for a Cleared run, and only on a reap pass: this copies the copy's whole
                    // object dictionary (the same call EndRun's eviction uses), and ShouldEnd's Cleared branch
                    // is the only one that reads it. Everything else short-circuits to false without paying
                    // for the scan.
                    var playerPresent = state == ThreadDungeonRunState.Cleared && landblock != null
                        && landblock.GetAllWorldObjectsForDiagnostics().OfType<Player>().Any();

                    // Second, free source for the entry latch. The primary one is Player.OnTeleportComplete;
                    // this catches a player who reached the copy by some path that never runs it (an admin
                    // teleport, say) and then left, which would otherwise pin the run to its TTL. It costs
                    // nothing: the scan has already happened, and the latch is idempotent.
                    if (playerPresent)
                        run.MarkPlayerObserved();

                    var sinceCleared = run.ClearedUtc is DateTime clearedUtc ? now - clearedUtc : TimeSpan.Zero;

                    if (ShouldEnd(state, landblock != null, createCompleted, run.PlayerEverObserved, playerPresent, now - run.StartedUtc, sinceCleared, now, run.ExpiresUtc, out var why))
                    {
                        EndRun(run, why);
                        continue;
                    }
                }

                // Populate a Starting run the moment its copy has finished loading. TryPopulate is safe to
                // call every tick - it latches per run and returns immediately once a populate is in flight -
                // and it is what calls run.MarkPopulated, which moves the run to Active.
                //
                // This sits BELOW the ShouldEnd call deliberately. ShouldEnd is the only thing that reaps a
                // copy whose CreateWorldObjects threw (ruling R18), and a populate gated on createCompleted
                // would simply never fire for such a run rather than noticing it is broken.
                if (state == ThreadDungeonRunState.Starting && createCompleted)
                    ThreadDungeonSpawner.TryPopulate(run, landblock);
            }
        }

        /// <summary>The single exit path: marks the run ended, evicts anyone still inside, queues the landblock for destruction, destroys the gem.</summary>
        public static void EndRun(ThreadDungeonRun run, string why)
        {
            // MarkEnded returns true only for the caller that made the transition, so the eviction, the
            // destruction-queue push and the gem destruction below happen exactly once even if the world
            // thread and a landblock thread call EndRun on the same run at the same moment.
            //
            // The overload that reports the PRIOR state is what the telemetry row's end_state is derived
            // from. It has to come out of this same critical section: reading run.State first would leave a
            // window in which a landblock thread lands the clearing kill, and the run would be filed as
            // abandoned when the player had just finished it.
            if (run == null || !run.MarkEnded(why, out var priorState)) return;

            RecordRunTelemetry(run, priorState, why);

            // Drop the registry entry now rather than waiting for the next reap. An Ended run left in `runs`
            // for up to 15 seconds can collide with a re-issued instance id, and the TryAdd in TryStart would
            // then fail for the new run - pinning that id as pending in LandblockManager's ephemeral instance
            // registry (EphemeralInstanceRegistry.ClearPending) forever, because only a successful
            // GetLandblock clears it.
            runs.TryRemove(run.RunId, out _);

            // Drop the spawner's in-flight latch for the same reason. A run that ends mid-populate takes its
            // pending action delegate down with it when Landblock.Unload clears the landblock's action queue,
            // so the chain's own terminal step may never run and nothing else would release the latch. The
            // latch is keyed on the ephemeral instance id, which LandblockManager re-issues, and a re-issued
            // id that is still latched would never populate.
            ThreadDungeonSpawner.ForgetRun(run.RunId);

            log.Info($"[DYNDUNGEON] ended {run} ({why})");

            var landblock = LandblockManager.GetEphemeralLandblock(run.Instance);
            if (landblock != null)
            {
                var players = landblock.GetAllWorldObjectsForDiagnostics().OfType<Player>().ToList();

                foreach (var player in players)
                {
                    player.Session?.Network.EnqueueSend(new GameMessageSystemChat("The dungeon collapses around you.", ACE.Entity.Enum.ChatMessageType.Broadcast));
                    // EphemeralRealmExitTo first: the gem handler re-stamps it from the player's CURRENT
                    // location on every entry, so a player who re-entered from a second town is evicted back
                    // THERE rather than to run.ExitTo, which records only where the run was first opened.
                    var exit = player.GetPosition(PositionType.EphemeralRealmExitTo) ?? run.ExitTo ?? player.Sanctuary ?? player.Instantiation;
                    if (exit != null)
                        WorldManager.ThreadSafeTeleport(player, new Position(exit));
                }

                // Only an EMPTY copy is queued for destruction here, and the hazard is ordering rather than
                // thread safety (the queue itself is a ConcurrentBag, LandblockManager.cs:120,843). The
                // eviction above is asynchronous - ThreadSafeTeleport lands on the next ActionQueue pass, and
                // Player.Teleport can defer itself a further second behind a fog chain on the player's own
                // landblock queue (Player_Location.cs:798-807). Unloading runs at the end of the same
                // LandblockManager.Tick, and Landblock.Unload clears that action queue and detaches players,
                // so queueing an occupied copy here would drop the teleport it was meant to follow. An
                // occupied copy needs no help: it reaches the destruction queue on its own five minutes after
                // the last player leaves (Landblock.cs:1097-1115), which the eviction has just arranged.
                if (players.Count == 0)
                    LandblockManager.AddToDestructionQueue(landblock);
            }

            OnRunEnded(run);
        }

        /// <summary>
        /// Files the run's analytics row. Called from EndRun and nowhere else, immediately after the
        /// exactly-once MarkEnded gate, so there is exactly one row per run.
        ///
        /// Everything is snapshotted HERE, on the caller's thread, and the queued payload holds no reference
        /// to the run or to any WorldObject: EndRun is about to evict players and queue the copy for
        /// destruction, and the analytics writer thread reads its queue up to a second later.
        ///
        /// The level resolved here is the END-TIME one, and it is the lesser half of the pair: the row also
        /// carries the level captured when the run opened (ThreadDungeonRun.OwnerLevelAtStart), which is
        /// the unbiased one. It is resolved through PlayerManager.FindByGuid rather than GetOnlinePlayer,
        /// because a run can end long after its owner logged out (a TTL expiry, most obviously) and
        /// FindByGuid answers for an offline character too. 0 means the character could not be resolved.
        ///
        /// Wrapped in a try/catch on purpose. EndRun is the single exit path for a run - it evicts players,
        /// destroys the gem and releases the copy - and none of that may be skipped because a telemetry
        /// projection threw.
        /// </summary>
        private static void RecordRunTelemetry(ThreadDungeonRun run, ThreadDungeonRunState priorState, string why)
        {
            try
            {
                var charLevel = PlayerManager.FindByGuid(run.OwnerGuid)?.Level ?? 0;
                AnalyticsManager.RecordDungeonRun(DungeonRunTelemetry.BuildRow(run, priorState, why, DateTime.UtcNow, charLevel));
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] failed to record run telemetry for {run}", ex);
            }
        }

        /// <summary>
        /// The end-of-run trigger: destroys the gem, whatever ended the run (Cleared already destroyed it
        /// via AnnounceCleared -> GemDestroyer, and DestroyGem's own re-resolution makes a second call here
        /// a harmless no-op).
        /// </summary>
        public static void OnRunEnded(ThreadDungeonRun run)
        {
            GemDestroyer(run);
        }

        /// <summary>
        /// Cleanup layer (a): destroy the bound gem wherever it is reachable right now (owner's inventory
        /// online). TECH-DESIGN Q10 requires this to run on the HOLDER's action queue, never inline on the
        /// caller's thread - EndRun reaches here from the world thread, and mutating another player's
        /// inventory from there races the landblock tick that owns them. EnqueueAction routes the work to the
        /// owner's landblock queue, or discards it if they have already detached (WorldObject_Tick.cs:144-155).
        ///
        /// Everything the delegate needs is captured by value, and the gem is re-resolved inside it: by the
        /// time it runs the owner may have banked, dropped or traded the gem, in which case there is nothing
        /// to do here and ThreadDungeonSweeper catches it on login, on vault withdraw, or on next use
        /// (layers b and c).
        ///
        /// Idempotent by re-resolution, deliberately not latched: called from both AnnounceCleared (owner
        /// ruling A1, the gem dies the moment the run is Cleared) and OnRunEnded (so a run that ends without
        /// ever clearing still destroys it). If the owner is offline when the run clears, this call is a
        /// no-op and OnRunEnded's later call is what actually reaches the gem; if the owner is online for
        /// both, the second call simply finds no gem left to consume.
        /// </summary>
        public static void DestroyGem(ThreadDungeonRun run)
        {
            var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);
            if (owner == null)
                return;

            var gemGuid = run.GemGuid;
            var runText = run.ToString();

            owner.EnqueueAction(new ActionEventDelegate(() =>
            {
                var gem = owner.FindObject(gemGuid, Player.SearchLocations.MyInventory);
                if (gem == null)
                    return;

                var gemName = gem.Name;
                owner.TryConsumeFromInventoryWithNetworking(gem);
                owner.Session?.Network.EnqueueSend(new GameMessageSystemChat($"Your {gemName} crumbles to dust.", ACE.Entity.Enum.ChatMessageType.Broadcast));
                log.Info($"[DYNDUNGEON] destroyed gem 0x{gemGuid:X8} for {runText}");
            }));
        }
    }
}
