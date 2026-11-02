using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using ACE.Entity;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Thread return (Docs/Pvp/BATTLEGROUNDS.md "Pulled from a Thread"): the players who were pulled into a PvP arena or battleground
    /// match from inside a Thread copy, each with the run they left, the exact spot they left it from and how long the copy is held
    /// for them. Keyed by character guid; one entry per character (a second pull replaces the first).
    ///
    /// <para/>
    /// Validity is by RUN REFERENCE, never by id: a run's id is its instance id, and both are re-issued, so an entry is only honoured
    /// while the run registered for that instance is the very object it recorded (<see cref="IsReturnValid"/>).
    ///
    /// <para/>
    /// Lifecycle:
    ///   - <see cref="Register"/> when the PvP coordinator stamps the player's match exit (LivePvpPlayerGateway.GetExitPoint): the hold
    ///     runs dynamic_dungeons_pvp_hold_minutes from then.
    ///   - <see cref="OnParticipationEnded"/> when their match participation ends: the hold shrinks to the run's empty grace from that
    ///     moment, never lengthens.
    ///   - released when they arrive back in the run's copy (ThreadDungeonManager.OnPlayerArrived), when the return finds the entry
    ///     invalid (Player.ResolvePvpReturn), or by <see cref="Prune"/> once the run itself is gone.
    ///
    /// <para/>
    /// Read from the world thread (the reap pass, the warning pass, the PvP gateway) and from a player's own thread (the return
    /// resolver). Entries are immutable records swapped in a ConcurrentDictionary, so no lock is needed.
    /// </summary>
    public static class ThreadPvpAway
    {
        /// <summary>dynamic_dungeons_pvp_hold_minutes: its default and the clamp applied at read.</summary>
        public const long HoldMinutesDefault = 30;
        public const long HoldMinutesMin = 5;
        public const long HoldMinutesMax = 180;

        /// <summary>
        /// One away player. <see cref="HoldWindow"/> is the clamped dynamic_dungeons_pvp_hold_minutes at registration: the copy's idle
        /// window while the entry holds it. <see cref="HoldUntilUtc"/> is when the hold stops (registration plus the window, shortened
        /// when participation ends).
        /// </summary>
        public sealed record Entry(ThreadDungeonRun Run, Position Inside, DateTime HoldUntilUtc, TimeSpan HoldWindow);

        private static readonly ConcurrentDictionary<uint, Entry> entries = new ConcurrentDictionary<uint, Entry>();

        /// <summary>The hold window for a raw dynamic_dungeons_pvp_hold_minutes value, clamped to [5, 180] minutes. Pure.</summary>
        public static TimeSpan ClampHold(long minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, HoldMinutesMin, HoldMinutesMax));

        /// <summary>Records <paramref name="guid"/> as away from <paramref name="run"/>, standing at <paramref name="inside"/> (copied).</summary>
        public static void Register(uint guid, ThreadDungeonRun run, Position inside, DateTime now, TimeSpan hold)
        {
            if (run == null || inside == null)
                return;

            entries[guid] = new Entry(run, new Position(inside), now + hold, hold);
        }

        /// <summary>
        /// A new pull into a match ALWAYS supersedes whatever entry the player still carries from an earlier one: that entry is dropped
        /// first, whatever run it names, and a new one is registered only when <paramref name="run"/> is given (the exit choice said the
        /// player stands in a Thread as a member, PvpReturnRules.ChooseExit). So a stale entry left by an exit that never ran the return
        /// resolver can never be consumed by a later, unrelated match. Returns the dropped entry, or null.
        /// </summary>
        public static Entry BeginPull(uint guid, ThreadDungeonRun run, Position inside, DateTime now, TimeSpan hold)
        {
            entries.TryRemove(guid, out var dropped);

            if (run != null && inside != null)
                entries[guid] = new Entry(run, new Position(inside), now + hold, hold);

            return dropped;
        }

        /// <summary>What ending a player's match participation does to their away entry (<see cref="ParticipationEndedAction"/>).</summary>
        public enum EndAction
        {
            /// <summary>Keep the entry and shrink its hold: the return resolver has yet to run (they are still in the match copy, on their way out, or offline until their next login).</summary>
            Shrink,

            /// <summary>Drop the entry: the player is online and already out of the match copy without the resolver (a walk-out, a failed rejoin), so no return will consume it.</summary>
            Release,
        }

        /// <summary>
        /// Pure: an online player who is not teleporting and does not stand in the match instance has left it by a path that never ran the
        /// return resolver, so the entry is released (they can still walk back into the Thread with their gem; a later match must never
        /// consume it). Anyone else keeps it, with the hold shrunk: an offline player returns through the login relocation.
        /// </summary>
        public static EndAction ParticipationEndedAction(bool online, bool teleporting, bool inMatchInstance)
            => online && !teleporting && !inMatchInstance ? EndAction.Release : EndAction.Shrink;

        /// <summary>
        /// Applies <see cref="ParticipationEndedAction"/> to the player's entry: released, or its hold shrunk
        /// (<see cref="OnParticipationEnded"/>). Null when the player carries no entry. The live gateway and the coordinator tests both
        /// go through here.
        /// </summary>
        public static EndAction? ApplyParticipationEnded(uint guid, bool online, bool teleporting, bool inMatchInstance, DateTime now)
        {
            if (!entries.ContainsKey(guid))
                return null;

            var action = ParticipationEndedAction(online, teleporting, inMatchInstance);

            if (action == EndAction.Release)
                Release(guid);
            else
                OnParticipationEnded(guid, now);

            return action;
        }

        public static bool TryGet(uint guid, out Entry entry) => entries.TryGetValue(guid, out entry);

        /// <summary>
        /// The player's match participation ended: hold-until becomes the earlier of what it was and now plus the run's empty grace
        /// (<see cref="ShortenedHoldUntil"/>). A no-op without an entry.
        /// </summary>
        public static void OnParticipationEnded(uint guid, DateTime now)
        {
            while (entries.TryGetValue(guid, out var e))
            {
                var until = ShortenedHoldUntil(e.HoldUntilUtc, now, e.Run.EmptyGrace);

                if (until == e.HoldUntilUtc || entries.TryUpdate(guid, e with { HoldUntilUtc = until }, e))
                    return;
            }
        }

        /// <summary>min(current, now + grace). Pure.</summary>
        internal static DateTime ShortenedHoldUntil(DateTime current, DateTime now, TimeSpan grace)
        {
            var candidate = now + grace;
            return candidate < current ? candidate : current;
        }

        /// <summary>Drops the player's entry, whatever run it names.</summary>
        public static void Release(uint guid) => entries.TryRemove(guid, out _);

        /// <summary>Drops the player's entry only when it names <paramref name="run"/> (by reference).</summary>
        public static void Release(uint guid, ThreadDungeonRun run)
        {
            if (run != null && entries.TryGetValue(guid, out var e) && ReferenceEquals(e.Run, run))
                ((ICollection<KeyValuePair<uint, Entry>>)entries).Remove(new KeyValuePair<uint, Entry>(guid, e));
        }

        /// <summary>True while <paramref name="guid"/> is recorded as away from this very run (the empty-grace warnings skip them).</summary>
        public static bool IsAwayFrom(ThreadDungeonRun run, uint guid) => run != null && entries.TryGetValue(guid, out var e) && ReferenceEquals(e.Run, run);

        /// <summary>
        /// The idle window the run's copy is held at for its away players: the longest <see cref="Entry.HoldWindow"/> among entries
        /// naming this run whose hold has not run out, or null when none holds it.
        /// </summary>
        public static TimeSpan? HeldWindow(ThreadDungeonRun run, DateTime now)
        {
            if (run == null)
                return null;

            TimeSpan? held = null;

            foreach (var e in entries.Values)
            {
                if (!ReferenceEquals(e.Run, run) || e.HoldUntilUtc <= now)
                    continue;

                if (held == null || e.HoldWindow > held.Value)
                    held = e.HoldWindow;
            }

            return held;
        }

        /// <summary>
        /// Drops every entry whose run has ended or is no longer the run registered for its instance (<paramref name="registered"/>,
        /// ThreadDungeonManager.GetRun in production). An expired hold alone is NOT pruned: it only stops holding the copy, and a
        /// player still in a match may yet return to a copy that is alive for other reasons (a group member inside).
        /// </summary>
        public static void Prune(Func<uint, ThreadDungeonRun> registered)
        {
            foreach (var kvp in entries)
            {
                var run = kvp.Value.Run;

                if (run.State == ThreadDungeonRunState.Ended || !ReferenceEquals(registered?.Invoke(run.Instance), run))
                    ((ICollection<KeyValuePair<uint, Entry>>)entries).Remove(kvp);
            }
        }

        /// <summary>
        /// The return rule, pure: an away player goes back to the spot they left only when the run registered for that instance is
        /// still the very run they left (<paramref name="sameRunRegistered"/>, by reference), it has not ended, its copy is loaded and
        /// its realm still accepts them (the roster and the puzzle fail policy). Anything else returns them to the outer exit.
        /// </summary>
        internal static bool IsReturnValid(bool sameRunRegistered, bool runEnded, bool landblockLoaded, bool realmAccepts)
            => sameRunRegistered && !runEnded && landblockLoaded && realmAccepts;

        /// <summary>Diagnostics and tests.</summary>
        internal static int Count => entries.Count;

        /// <summary>Test seam: empties the registry so one test class cannot leak entries into another.</summary>
        internal static void ClearForTests() => entries.Clear();
    }
}
