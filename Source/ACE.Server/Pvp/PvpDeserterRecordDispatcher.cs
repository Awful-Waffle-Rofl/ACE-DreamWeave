using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using log4net;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The optional conduct half of the player seam (Docs/Pvp/BATTLEGROUNDS.md "Deserter lockout"), detected as <c>players as
    /// IPvpConductGateway</c> like the other optional gateways. Called on the world thread. Never throws into the coordinator.
    /// </summary>
    public interface IPvpConductGateway
    {
        /// <summary>
        /// Stores <paramref name="record"/> on the character as absolute values: on the player's own action chain when online, on the
        /// stored character when offline, retried until it lands (<see cref="PvpDeserterRecordDispatcher"/>).
        /// </summary>
        void PersistDeserterRecord(uint characterId, PvpDeserterRecord record);
    }

    /// <summary>What <see cref="PvpDeserterRecordDispatcher"/> needs from the live server. Live: PvpDeserterLive.cs; tests: a scripted fake.</summary>
    public interface IPvpDeserterWriteTargets
    {
        /// <summary>The character is online (a logging-out player still counts as online here).</summary>
        bool IsOnline(uint characterId);

        /// <summary>The character is online but already logging out: its action queue may never run again.</summary>
        bool IsLoggingOut(uint characterId);

        /// <summary>Queues <paramref name="action"/> on the character's own action chain. False, with nothing queued, when offline or logging out.</summary>
        bool TryEnqueueOnPlayer(uint characterId, Action action);

        /// <summary>Writes the record on the online character (its action chain). False when it is no longer the live, not-logging-out owner.</summary>
        bool WriteOnline(uint characterId, PvpDeserterRecord record);

        /// <summary>Writes the record on the stored (offline) character. False when there is no offline record to write to yet.</summary>
        bool WriteOffline(uint characterId, PvpDeserterRecord record);

        /// <summary>Runs <paramref name="action"/> on the world thread after <paramref name="seconds"/>.</summary>
        void ScheduleOnWorld(double seconds, Action action);
    }

    /// <summary>
    /// Lands a deserter record on the character, in the style of <see cref="PvpBloodGrantDispatcher"/>: the record waits PENDING per
    /// character until one path claims it - the player's action chain when online, or a world-thread fallback after
    /// <see cref="FallbackSeconds"/> that writes the stored character. A write that fails (returns false or throws, even part way) is
    /// re-pended and retried by a fresh fallback, up to <see cref="MaxAttempts"/> times. Because the record is ABSOLUTE, a retry after a
    /// partial write simply rewrites all three values, and a NEWER record for the same character replaces an older pending one (the newest
    /// always wins; an older fallback still outstanding finds a newer generation and leaves it alone).
    /// </summary>
    public sealed class PvpDeserterRecordDispatcher
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpDeserterRecordDispatcher));

        public const double FallbackSeconds = 30;

        public const int MaxAttempts = 10;

        private sealed record Pending(PvpDeserterRecord Record, int Attempt, long Generation);

        private readonly IPvpDeserterWriteTargets targets;
        private readonly ConcurrentDictionary<uint, Pending> pending = new();
        private long nextGeneration;

        public PvpDeserterRecordDispatcher(IPvpDeserterWriteTargets targets)
        {
            this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        }

        /// <summary>Records still waiting to land. Diagnostics and tests.</summary>
        public int PendingCount => pending.Count;

        /// <summary>The attempt of the pending record for this character, or 0 when none is pending. Diagnostics and tests.</summary>
        public int AttemptOf(uint characterId) => pending.TryGetValue(characterId, out var p) ? p.Attempt : 0;

        /// <summary>World thread. Pends the record (replacing any older pending one) and starts it down the online or offline path.</summary>
        public void Persist(uint characterId, PvpDeserterRecord record)
        {
            if (record == null)
                return;

            var entry = new Pending(record, 1, Interlocked.Increment(ref nextGeneration));

            pending[characterId] = entry;

            // Offline, or logging out (whose queue may never run): try the stored character at once; if there is no offline record to write to
            // yet, the retry below falls back to the 30 s world-thread path.
            if (!targets.IsOnline(characterId) || targets.IsLoggingOut(characterId))
            {
                if (TryClaimGeneration(characterId, entry.Generation, out var claimed))
                    SettleOffline(characterId, claimed);

                return;
            }

            Dispatch(characterId, entry.Generation);
        }

        private void Dispatch(uint characterId, long generation)
        {
            targets.ScheduleOnWorld(FallbackSeconds, () => Fallback(characterId, generation));

            targets.TryEnqueueOnPlayer(characterId, () =>
            {
                if (!TryClaimGeneration(characterId, generation, out var claimed))
                    return; // settled by the fallback, or superseded by a newer record with its own dispatch

                bool ok;

                try
                {
                    ok = targets.WriteOnline(characterId, claimed.Record);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] 0x{characterId:X8}: writing the deserter record on the player threw", ex);
                    ok = false;
                }

                if (!ok)
                    Retry(characterId, claimed, "the player was no longer online when their queue ran");
            });
        }

        private void Fallback(uint characterId, long generation)
        {
            if (!TryClaimGeneration(characterId, generation, out var claimed))
                return;

            log.Warn($"[PVP] 0x{characterId:X8}: the deserter record was not written by the player's queue within {FallbackSeconds} s; writing it on the world thread (attempt {claimed.Attempt})");
            SettleOffline(characterId, claimed);
        }

        private void SettleOffline(uint characterId, Pending claimed)
        {
            bool ok;

            try
            {
                ok = targets.WriteOffline(characterId, claimed.Record);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] 0x{characterId:X8}: writing the deserter record on the stored character threw", ex);
                ok = false;
            }

            if (!ok)
                Retry(characterId, claimed, "the stored-character write did not land");
        }

        private void Retry(uint characterId, Pending claimed, string why)
        {
            if (claimed.Attempt >= MaxAttempts)
            {
                log.Error($"[PVP] 0x{characterId:X8}: deserter record (strikes {claimed.Record.Strikes}, locked until {claimed.Record.LockoutUntil}) NOT stored after {claimed.Attempt} attempts ({why})");
                return;
            }

            var next = new Pending(claimed.Record, claimed.Attempt + 1, Interlocked.Increment(ref nextGeneration));

            // A newer record pended meanwhile wins: it has its own dispatch.
            if (!pending.TryAdd(characterId, next))
                return;

            Dispatch(characterId, next.Generation);
        }

        /// <summary>Removes the entry only if it is still the generation the caller was armed for (atomic, value-conditional).</summary>
        private bool TryClaimGeneration(uint characterId, long generation, out Pending claimed)
        {
            claimed = null;

            if (!pending.TryGetValue(characterId, out var current) || current.Generation != generation)
                return false;

            if (!pending.TryRemove(new KeyValuePair<uint, Pending>(characterId, current)))
                return false;

            claimed = current;
            return true;
        }
    }
}
