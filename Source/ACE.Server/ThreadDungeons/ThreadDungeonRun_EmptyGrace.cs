using System;
using System.Collections.Generic;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The empty grace: how long a live copy survives with nobody inside, and the owner's warnings as it runs
    /// out (owner rulings 2026-09-17: 10 minutes for a Starting or Active run, warnings at 5m, 2m, 1m and 30s).
    ///
    /// The landblock PULLS the grace on every heartbeat through EphemeralRealm.UnloadIntervalOverride, and
    /// this side only ever answers "what should it be right now". Nothing is pushed onto the landblock, so
    /// there is no stale override to clear when the run clears or ends: <see cref="UnloadIntervalOverride"/>
    /// simply turns null and the copy falls back to Landblock.UnloadInterval. A Cleared run that owes nothing
    /// therefore keeps exactly the behaviour it had before - ThreadDungeonManager.ShouldEnd reaps it 30 seconds
    /// after the clear once it is empty.
    ///
    /// The loot hold (Group Threads ruling R29 as merged with this mechanism, extended to solo runs by owner ruling
    /// 2026-10-10): the empty grace is the ONE empty-copy lifetime mechanism for every run. The exception is a
    /// Cleared run that still owes loot (<see cref="LootHoldActive"/>, fed by ThreadRunPresence.LootOwed): its
    /// copy keeps a window - the run's lock-time <see cref="LootHold"/> (dynamic_dungeons_group_loot_hold_minutes,
    /// for solo and group runs alike) - in place of the grace, and ShouldEnd's 30-second cleared-and-empty reap
    /// stands down for it. The landblock's own idle clock (refreshed by any player standing in the copy, and only
    /// roster members can be) is what the hold is measured on, so once no member has been inside for the hold the
    /// copy unloads and the run ends exactly as a live run's does. Uncleared runs and runs owing nothing are
    /// untouched.
    ///
    /// A SOLO run's gem survives the clear (owner ruling 2026-10-10, superseding ruling A1 of 2026-09-27, which
    /// destroyed it at the clear and never held a solo run): an owner who dies to the monsters left behind can
    /// still come back for their loot. The gem is retired only by the owner's willing exit from the cleared copy
    /// (<see cref="RetireOwnerGem"/>, see ThreadWillingExit), and a retired gem owes nothing more, so the hold
    /// releases and the copy reaps as before.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        /// <summary>The empty grace used by callers, tests included, that never set <see cref="EmptyGrace"/>.</summary>
        public static readonly TimeSpan DefaultEmptyGrace = TimeSpan.FromMinutes(10);

        /// <summary>
        /// The remaining-time marks at which the owner is warned, least urgent first. The order is
        /// load-bearing: <see cref="GraceWarningDue"/> counts how many of these have been crossed, and that
        /// count is the latch.
        /// </summary>
        internal static readonly TimeSpan[] GraceWarningThresholds =
        {
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(30),
        };

        /// <summary>
        /// How long the copy may stand empty while the run is Starting or Active. Read once from the
        /// dynamic_dungeons_empty_grace_minutes tunable by ThreadDungeonManager.TryStart and set here before
        /// the run is published to the registry - same write-once-before-publication convention as
        /// ClearFraction, so it MUST NOT be re-assigned after that (landblock threads read it without the lock).
        /// </summary>
        public TimeSpan EmptyGrace { get; set; } = DefaultEmptyGrace;

        /// <summary>
        /// How long a Cleared copy that still owes loot may stand empty (<see cref="LootHoldActive"/>). Stamped by
        /// ThreadDungeonManager.TryStart from dynamic_dungeons_group_loot_hold_minutes - a group run's
        /// <see cref="GroupScaling.LootHold"/>, a solo run's own sanitised read of the same tunable (owner ruling
        /// 2026-10-10) - and set before the run is published to the registry, the same write-once-before-publication
        /// convention as <see cref="EmptyGrace"/>, so it MUST NOT be re-assigned after that (landblock threads read it
        /// without the lock). Zero (the tunable's "no hold") never holds a copy. A run built without TryStart (tests)
        /// that never sets it reads <see cref="Group"/>'s LootHold: the lock-time hold for a group run, 0 for solo.
        /// </summary>
        public TimeSpan LootHold
        {
            get => lootHold ?? Group.LootHold;
            set => lootHold = value;
        }

        /// <summary>Backing field for <see cref="LootHold"/>; null until stamped.</summary>
        private TimeSpan? lootHold;

        /// <summary>Set once by <see cref="RetireOwnerGem"/>. Guarded by stateLock.</summary>
        private bool ownerGemRetired;

        /// <summary>
        /// Retires the owner's gem: from now on <see cref="IsRunGem"/> no longer knows it, so the sweeper, a lazy
        /// use (ConsumeDeadGem) and the Fragment Press all treat it as a dead gem, and a SOLO run owes nothing more
        /// (ThreadRunPresence.LootOwed), so its loot hold releases. Called by Player.Teleport when a solo owner's
        /// willing exit (ThreadWillingExit) actually leaves the cleared copy. One-way and idempotent.
        /// </summary>
        public void RetireOwnerGem()
        {
            lock (stateLock)
                ownerGemRetired = true;
        }

        /// <summary>See <see cref="RetireOwnerGem"/>.</summary>
        public bool IsOwnerGemRetired { get { lock (stateLock) return ownerGemRetired; } }

        /// <summary>How many of <see cref="GraceWarningThresholds"/> have already been announced. Guarded by stateLock.</summary>
        private int graceWarningsFired;

        /// <summary>
        /// The idle window for a copy hosting a run in <paramref name="state"/>: the grace while the run is
        /// Starting or Active, null (the landblock default) for Cleared and Ended.
        /// </summary>
        internal static TimeSpan? ResolveUnloadInterval(ThreadDungeonRunState state, TimeSpan grace)
            => ResolveUnloadInterval(state, grace, lootHeld: false, lootHold: TimeSpan.Zero);

        /// <summary>
        /// The floor the empty grace tunable is clamped to (ThreadDungeonManager.EmptyGraceMinutesMin, the ordinary
        /// landblock UnloadInterval). A positive loot hold is clamped to it too, so a held copy never unloads
        /// faster than master's grace allows.
        /// </summary>
        internal static readonly TimeSpan EmptyWindowFloor = TimeSpan.FromMinutes(ThreadDungeonManager.EmptyGraceMinutesMin);

        /// <summary>
        /// The idle window with the loot hold folded in: the grace while Starting or Active; the
        /// <paramref name="lootHold"/>, raised to <see cref="EmptyWindowFloor"/>, for a Cleared run whose
        /// <paramref name="lootHeld"/> is set (see <see cref="LootHoldActive"/>, which already requires a
        /// positive hold); null otherwise. With <paramref name="lootHeld"/> false this is exactly the
        /// two-argument form. Exactly the six-argument form with no PvP hold.
        /// </summary>
        internal static TimeSpan? ResolveUnloadInterval(ThreadDungeonRunState state, TimeSpan grace, bool lootHeld, TimeSpan lootHold)
            => ResolveUnloadInterval(state, grace, lootHeld, lootHold, pvpHeld: false, pvpHold: TimeSpan.Zero);

        /// <summary>
        /// The idle window with both holds folded in. First the window the run has without a PvP hold: the grace while Starting or
        /// Active; the <paramref name="lootHold"/> for a Cleared run whose <paramref name="lootHeld"/> is set (see
        /// <see cref="LootHoldActive"/>, which already requires a positive hold); null otherwise. Then the PvP hold (Docs/Pvp/
        /// BATTLEGROUNDS.md "Pulled from a Thread"): while <paramref name="pvpHeld"/> is set (a member pulled into a PvP match is away,
        /// ThreadPvpAway) with a positive <paramref name="pvpHold"/>, the window is that hold whenever it is longer than (or replaces
        /// the absence of) the window above, for any state but Ended. Every window that is a HOLD (loot or PvP) is raised to
        /// <see cref="EmptyWindowFloor"/>; the grace is clamped where it is read. This is the single site where a hold replaces the
        /// grace, and so the single clamp, for both holds.
        /// </summary>
        internal static TimeSpan? ResolveUnloadInterval(ThreadDungeonRunState state, TimeSpan grace, bool lootHeld, TimeSpan lootHold, bool pvpHeld, TimeSpan pvpHold)
        {
            if (state == ThreadDungeonRunState.Ended)
                return null;

            TimeSpan? window = null;
            var isHold = false;

            if (state == ThreadDungeonRunState.Starting || state == ThreadDungeonRunState.Active)
                window = grace;
            else if (state == ThreadDungeonRunState.Cleared && lootHeld && lootHold > TimeSpan.Zero)
            {
                window = lootHold;
                isHold = true;
            }

            if (pvpHeld && pvpHold > TimeSpan.Zero && (window == null || pvpHold > window.Value))
            {
                window = pvpHold;
                isHold = true;
            }

            if (isHold && window.Value < EmptyWindowFloor)
                return EmptyWindowFloor;

            return window;
        }

        /// <summary>
        /// Is a run's copy on the loot hold? A Cleared run, solo or group, that still owes loot
        /// (<paramref name="lootOwed"/>, ThreadRunPresence.LootOwed) with a positive lock-time hold. False for an
        /// uncleared run, a run owing nothing (a solo run whose owner gem is retired owes nothing), or a hold of 0
        /// (the tunable's "no hold"). The solo/group difference lives entirely in what LootOwed counts as owed.
        /// </summary>
        internal static bool LootHoldActive(ThreadDungeonRunState state, bool lootOwed, TimeSpan lootHold)
            => state == ThreadDungeonRunState.Cleared && lootOwed && lootHold > TimeSpan.Zero;

        /// <summary>
        /// The last <see cref="LootHoldActive"/> answer, stored by ThreadDungeonManager.Tick on the world
        /// thread each reap pass so the landblock heartbeat can read it without computing LootOwed (which
        /// reads placed caches) on a landblock thread. Up to one reap interval stale, in either direction; the
        /// reap decision itself always uses a fresh value. Guarded by stateLock.
        /// </summary>
        private bool lootHeld;

        /// <summary>See <see cref="lootHeld"/>.</summary>
        public bool IsLootHeld { get { lock (stateLock) return lootHeld; } }

        /// <summary>Stores the reap pass's <see cref="LootHoldActive"/> answer. Only ThreadDungeonManager.Tick calls it.</summary>
        public void SetLootHeld(bool held)
        {
            lock (stateLock)
                lootHeld = held;
        }

        /// <summary>
        /// The PvP hold (Docs/Pvp/BATTLEGROUNDS.md "Pulled from a Thread"): the window ThreadPvpAway.HeldWindow gave on the last reap
        /// pass, or null when no away member holds the copy. Stored by ThreadDungeonManager.Tick on the world thread next to
        /// <see cref="lootHeld"/>, the same shape and the same staleness (up to one reap interval). Guarded by stateLock.
        /// </summary>
        private TimeSpan? pvpHeldWindow;

        /// <summary>See <see cref="pvpHeldWindow"/>.</summary>
        public TimeSpan? PvpHeldWindow { get { lock (stateLock) return pvpHeldWindow; } }

        /// <summary>True while an away member's PvP hold holds the copy (<see cref="PvpHeldWindow"/> is set).</summary>
        public bool IsPvpHeld => PvpHeldWindow.HasValue;

        /// <summary>Stores the reap pass's ThreadPvpAway.HeldWindow answer. Only ThreadDungeonManager.Tick calls it.</summary>
        public void SetPvpHeld(TimeSpan? window)
        {
            lock (stateLock)
                pvpHeldWindow = window;
        }

        /// <summary>
        /// Read from the landblock heartbeat, on a landblock thread. Takes stateLock (through State,
        /// IsLootHeld and PvpHeldWindow) and nothing else, and nothing that holds stateLock ever takes a landblock lock, so
        /// there is no ordering to get wrong. <see cref="LootHold"/> is write-once before publication, so it is
        /// read without the lock.
        /// </summary>
        public TimeSpan? UnloadIntervalOverride
        {
            get
            {
                var pvp = PvpHeldWindow;
                return ResolveUnloadInterval(State, EmptyGrace, IsLootHeld, LootHold, pvp.HasValue, pvp ?? TimeSpan.Zero);
            }
        }

        /// <summary>
        /// Who the empty-grace warning goes to (the merged ruling: "whoever is relevant"), in roster order, with
        /// the gem each one would step back in with. A SOLO run: the owner with the run gem, exactly master's
        /// recipient. A GROUP run: every roster member who can re-enter - the owner with the run gem, and each
        /// other member with their recorded key (a keyless member has nothing to step back in with and is
        /// skipped). In both, only members <paramref name="isReachableOutside"/> answers true for (online with a
        /// session and not inside the copy) are included; an empty list means the caller leaves the latch alone.
        /// </summary>
        internal static List<(uint Guid, uint GemGuid)> GraceWarningRecipients(bool isGroup, uint ownerGuid, uint runGemGuid,
            IReadOnlyList<ThreadRosterMember> roster, Func<uint, uint> keyOf, Func<uint, bool> isReachableOutside)
        {
            var recipients = new List<(uint Guid, uint GemGuid)>();

            if (isReachableOutside == null)
                return recipients;

            if (!isGroup)
            {
                if (isReachableOutside(ownerGuid))
                    recipients.Add((ownerGuid, runGemGuid));

                return recipients;
            }

            if (roster == null)
                return recipients;

            foreach (var member in roster)
            {
                if (member == null)
                    continue;

                var gem = member.IsOwner ? runGemGuid : (keyOf?.Invoke(member.Guid) ?? 0);
                if (gem == 0 || !isReachableOutside(member.Guid))
                    continue;

                recipients.Add((member.Guid, gem));
            }

            return recipients;
        }

        /// <summary>
        /// Time left before an empty copy collapses: the earlier of the empty-grace deadline (measured from
        /// the landblock's own idle clock, the same instant its heartbeat measures from) and the run's TTL.
        /// </summary>
        internal static TimeSpan GraceRemaining(DateTime lastActiveUtc, TimeSpan grace, DateTime expiresUtc, DateTime now)
        {
            var idleDeadline = lastActiveUtc + grace;
            var deadline = idleDeadline < expiresUtc ? idleDeadline : expiresUtc;
            return deadline - now;
        }

        /// <summary>
        /// The warning latch, pure. <paramref name="fired"/> is how many thresholds have been announced.
        ///
        /// - More than the first threshold remaining resets the latch to 0 and sends nothing. This is what
        ///   makes a re-entry restart the ladder: the owner's presence refreshes the idle clock, so the first
        ///   look after they leave again sees a full grace.
        /// - Nothing remaining sends nothing; the copy is going regardless.
        /// - Otherwise, if more thresholds are crossed than have been announced, ONE warning is due (the
        ///   caller words it from the actual remaining time, which makes it the most urgent one) and the
        ///   latch jumps to the crossed count, so crossing several at once - an owner logging back in with
        ///   45 seconds left - never produces a burst.
        /// </summary>
        internal static bool GraceWarningDue(TimeSpan remaining, int fired, out int newFired)
        {
            newFired = fired;

            if (remaining > GraceWarningThresholds[0])
            {
                newFired = 0;
                return false;
            }

            if (remaining <= TimeSpan.Zero)
                return false;

            var crossed = 0;
            foreach (var threshold in GraceWarningThresholds)
            {
                if (remaining <= threshold)
                    crossed++;
            }

            if (crossed <= fired)
                return false;

            newFired = crossed;
            return true;
        }

        /// <summary>
        /// Thread-safe wrapper over <see cref="GraceWarningDue"/>: true for the one caller that should send the
        /// warning for this crossing. The latch is written back whether or not a warning is due, so a reset
        /// sticks.
        /// </summary>
        public bool TryAdvanceGraceWarning(TimeSpan remaining)
        {
            lock (stateLock)
            {
                var due = GraceWarningDue(remaining, graceWarningsFired, out var newFired);
                graceWarningsFired = newFired;
                return due;
            }
        }

        /// <summary>
        /// "5 minutes", "1 minute", "30 seconds", "1 second". Seconds are rounded UP to a whole second, which
        /// absorbs the sub-second lag of the 1 s tick so each warning reads as its threshold (the 1-minute
        /// warning fires with a hair under 60 seconds left and reads "1 minute", not "60 seconds"). From 60
        /// rounded seconds on, whole minutes are rounded DOWN: a collapse warning may understate the time
        /// left but must never overstate it, so an owner coming back online with 65 seconds left reads
        /// "1 minute", not "2 minutes".
        /// </summary>
        internal static string FormatRemaining(TimeSpan remaining)
        {
            var ticks = Math.Max(0L, remaining.Ticks);
            var seconds = (ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;

            if (seconds >= 60)
            {
                var minutes = seconds / 60;
                return minutes == 1 ? "1 minute" : $"{minutes} minutes";
            }

            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        /// <summary>The countdown warning. <paramref name="canReEnter"/> false drops the gem sentence (a gem with no entries left).</summary>
        internal static string BuildGraceWarning(string dungeonName, TimeSpan remaining, bool canReEnter)
            => BuildGraceWarning(dungeonName, remaining, canReEnter, isKeyHolder: false);

        /// <summary>
        /// The countdown warning from one template. <paramref name="isKeyHolder"/> true is a Group Threads member who
        /// steps back in with a key rather than the owner's gem: the one word "Gem" becomes "Key". The owner and a
        /// solo run pass false and get the exact text above.
        /// </summary>
        internal static string BuildGraceWarning(string dungeonName, TimeSpan remaining, bool canReEnter, bool isKeyHolder)
        {
            var line = $"Your Thread in {dungeonName} collapses in {FormatRemaining(remaining)}.";
            return canReEnter ? line + $" Use your Thread {(isKeyHolder ? "Key" : "Gem")} to step back in before then." : line;
        }

        /// <summary>The login line for an owner whose saved instance is their own still-live run.</summary>
        internal static string BuildStillOpenLoginMessage(string dungeonName, bool canReEnter)
        {
            var line = $"Your Thread in {dungeonName} is still open.";
            return canReEnter ? line + " Use your Thread Gem to step back in before it collapses." : line;
        }

        /// <summary>
        /// Whether the gem sentence belongs in a message. False only when the bound gem was found AND has no
        /// entries left. A gem that is not in the owner's pack (banked, dropped, in a chest) keeps the
        /// sentence: using the gem is still how they get back in, and saying nothing would imply they cannot.
        /// </summary>
        internal static bool GemCanReEnter(bool gemFound, int structure) => !gemFound || structure > 0;

        /// <summary>
        /// <see cref="GemCanReEnter(bool, int)"/> with the cleared-solo return folded in (owner ruling 2026-10-10):
        /// re-entering a Cleared SOLO run is free (DungeonGemRules.IsClearedSoloReturn), so its gem can always step
        /// back in, whatever its entry count.
        /// </summary>
        internal static bool GemCanReEnter(bool gemFound, int structure, bool clearedSoloReturn) => clearedSoloReturn || GemCanReEnter(gemFound, structure);
    }
}
