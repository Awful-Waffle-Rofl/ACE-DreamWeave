using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ACE.Server.BalanceTrials
{
    /// <summary>What the pre-spawn rep setup decided. A non-null Refusal or RebuildRequired stops the rep before any spawn.</summary>
    public readonly record struct TrialRepSetupResult(string Refusal, bool RebuildRequired, TrialDigest Digest, bool Drift, string DriftReason);

    /// <summary>
    /// The pre-spawn rep steps for one session, in the Plan C Task 7 order: the HardCopyEvery schedule, then the soft reset,
    /// then the digest, then the drift check. Drift flags the rep (it still runs) and the session requires a rebuild before
    /// the next one (TrialSession reads TrialRep.DigestDrift).
    ///
    /// THE BLOCK. A block is the run of reps on one rep character since its last copy. It is keyed by the rep's guid and
    /// outlives a session, because a rebuild needs the rep logged out, and logging out ends the session: the block, not the
    /// session, is what "since the last copy" counts. A COMPLETED rebuild (TrialRebuild.TryRebuild's DoCopyChar reporting
    /// success) starts a new block. The block also carries NeedsRebuild: drift, or a purge that failed, sets it, and every
    /// later rep of that character is refused, in this session or any later one (a relog does not bypass it), until a
    /// rebuild completes. The ledger is in memory only: a server restart starts every rep on a new block and clears
    /// NeedsRebuild, so the schedule can under-count across a restart, and a still-drifted rep is flagged again by the
    /// digest check on its first rep.
    ///
    /// THE SEED WALK. A Step-mode scenario's gem seed walks by rep index (TrialGemRoller.RepSeed). The index fed to RollLive
    /// is the rep character's next index for this scenario, kept beside the block and outliving the session too, so a
    /// second session (after the relog a rebuild needs) carries on at seed + k instead of starting again at seed. It is
    /// keyed by the rep guid and remembers the scenario sha: a different scenario starts the walk again at 1, and that is
    /// the only reset. A completed rebuild (ResetBlock) deliberately does NOT reset it: hardCopyEvery forces a rebuild
    /// every few reps, and resetting there would repeat the same seeds (final review F5). In memory only, like the block:
    /// a server restart starts every walk again at 1.
    ///
    /// THE MASTER DIGEST is computed once per session, off the offline master, through the loader seam
    /// (ProfileBuilder.LoadOfflinePlayerAsync in production, whose callback runs on any thread, possibly synchronously).
    /// The answer is a <see cref="TrialMasterSnapshot"/>: the digest, plus the master's consumable stacks, which the soft
    /// reset restores the rep's consumables to. The result is marshalled onto the room's thread before it is stored. Until it arrives, a rep is REFUSED with
    /// "master digest pending" (the session stays Ready; the operator retries), never run with the core check skipped. A
    /// null answer (master online, missing or failed to load) or no answer within <see cref="MasterDigestTimeoutSeconds"/>
    /// (a swallowed shard read never calls back) is asked again on the next rep.
    ///
    /// Every member except the block ledger runs on the room's thread.
    /// </summary>
    public sealed class TrialRepSetup
    {
        public const double MasterDigestTimeoutSeconds = 30.0;

        private enum MasterState { NotRequested, Pending, Ready, Failed }

        private sealed class Block
        {
            public int RepsSinceCopy;
            public string FirstEnchantments;

            /// <summary>The buff profile FirstEnchantments was recorded under. A different profile records it again (see Before).</summary>
            public string FirstEnchantmentsProfile;

            /// <summary>Set by drift or a failed purge; refuses every rep of this rep character, in any session, until ResetBlock.</summary>
            public bool NeedsRebuild;
            public string NeedsRebuildReason;

            /// <summary>Set (with NeedsRebuild) when a rebuild's purge of this rep failed, so the rep may be partly populated: @trial promote refuses it. Cleared only by ResetBlock, like NeedsRebuild. Drift alone does not set it.</summary>
            public bool PurgeFailed;
        }

        private static readonly ConcurrentDictionary<uint, Block> blocks = new ConcurrentDictionary<uint, Block>();

        private sealed class SeedWalk
        {
            public string ScenarioSha;
            public int Next = 1;
        }

        /// <summary>The seed walks, by rep guid. Never cleared by ResetBlock (see THE SEED WALK).</summary>
        private static readonly ConcurrentDictionary<uint, SeedWalk> seedWalks = new ConcurrentDictionary<uint, SeedWalk>();

        private readonly uint _repGuid;
        private readonly uint _masterGuid;
        private readonly int _hardCopyEvery;
        private readonly Func<double> _clock;
        private readonly Action<uint, Action<TrialMasterSnapshot>> _loadMaster;
        private readonly Func<Action, bool> _marshal;
        private readonly string _scenarioSha;
        private readonly string _buffProfile;

        private MasterState _masterState;
        private string _masterCore;
        private IReadOnlyDictionary<uint, long> _masterConsumables;
        private double _requestedAt;
        private int _generation;

        /// <param name="loadMaster">Loads the master's snapshot: (masterGuid, callback). The callback may run on any thread, and may receive null.</param>
        /// <param name="marshal">Runs an action on the room's thread; false when nothing was queued (the session is gone).</param>
        /// <param name="scenarioSha">The scenario this session runs; keys the seed walk.</param>
        /// <param name="buffProfile">The effective buff profile this session casts (TrialBuffs.EffectiveProfile); keys the block's first-rep Enchantments.</param>
        public TrialRepSetup(uint repGuid, uint masterGuid, int hardCopyEvery, Func<double> clock, Action<uint, Action<TrialMasterSnapshot>> loadMaster, Func<Action, bool> marshal,
            string scenarioSha = null, string buffProfile = null)
        {
            _scenarioSha = scenarioSha ?? "";
            _buffProfile = buffProfile ?? "";
            _repGuid = repGuid;
            _masterGuid = masterGuid;
            _hardCopyEvery = hardCopyEvery;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _loadMaster = loadMaster ?? throw new ArgumentNullException(nameof(loadMaster));
            _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));
        }

        public uint MasterGuid => _masterGuid;

        /// <summary>The master's Core, or null until it has arrived.</summary>
        public string MasterCore => _masterState == MasterState.Ready ? _masterCore : null;

        /// <summary>The master's consumable stacks by wcid, or null until the snapshot has arrived. Ready before Before runs the soft reset.</summary>
        public IReadOnlyDictionary<uint, long> MasterConsumables => _masterState == MasterState.Ready ? _masterConsumables : null;

        /// <summary>
        /// Asks for the master digest. State is published BEFORE the loader is called, because the loader may call back
        /// synchronously (an online master is refused on the caller's thread).
        /// </summary>
        public void RequestMasterDigest()
        {
            // In-flight guard: one outstanding load at a time. A load counts as dead only past the timeout.
            if (_masterState == MasterState.Pending && _clock() - _requestedAt <= MasterDigestTimeoutSeconds)
                return;

            var generation = ++_generation;
            _masterState = MasterState.Pending;
            _requestedAt = _clock();

            _loadMaster(_masterGuid, snapshot => _marshal(() => OnMasterDigest(generation, snapshot)));
        }

        private void OnMasterDigest(int generation, TrialMasterSnapshot snapshot)
        {
            if (_masterState == MasterState.Ready)
                return;

            if (snapshot?.Digest?.Core != null)
            {
                // Any answer for this master is good, even one from an earlier request that came back late.
                _masterCore = snapshot.Digest.Core;
                _masterConsumables = snapshot.Consumables ?? new Dictionary<uint, long>();
                _masterState = MasterState.Ready;
            }
            else if (generation == _generation)
            {
                _masterState = MasterState.Failed;
            }
        }

        /// <summary>The pre-spawn steps for the next rep. <paramref name="softReset"/> and <paramref name="compute"/> run only when no earlier step refused.</summary>
        public TrialRepSetupResult Before(Action softReset, Func<TrialDigest> compute)
        {
            var block = blocks.GetOrAdd(_repGuid, _ => new Block());

            // 0. A rebuild still owed from an earlier rep (drift) or an earlier rebuild attempt (purge failure).
            int repsSinceCopy;
            lock (block)
            {
                if (block.NeedsRebuild)
                    return new TrialRepSetupResult($"rebuild required: {block.NeedsRebuildReason}; run @trial rebuild", true, null, false, null);

                repsSinceCopy = block.RepsSinceCopy;
            }

            // 1. The hard-copy schedule.
            if (_hardCopyEvery > 0 && repsSinceCopy >= _hardCopyEvery)
                return new TrialRepSetupResult($"hard copy due: {repsSinceCopy} rep(s) since the last copy (hardCopyEvery {_hardCopyEvery}); run @trial rebuild", true, null, false, null);

            // The master digest must be in hand before anything changes the rep.
            if (_masterState != MasterState.Ready)
            {
                var timedOut = _masterState == MasterState.Pending && _clock() - _requestedAt > MasterDigestTimeoutSeconds;
                var failed = _masterState == MasterState.Failed || _masterState == MasterState.NotRequested;

                if (failed || timedOut)
                {
                    var why = failed
                        ? $"master digest unavailable (master 0x{_masterGuid:X8} online, missing or failed to load)"
                        : $"master digest pending for over {MasterDigestTimeoutSeconds:0} s (master 0x{_masterGuid:X8})";

                    RequestMasterDigest();
                    return new TrialRepSetupResult($"{why}; asked again, retry @trial next", false, null, false, null);
                }

                return new TrialRepSetupResult($"master digest pending (master 0x{_masterGuid:X8}); retry @trial next", false, null, false, null);
            }

            // 2. The soft reset, then 3. the digest.
            softReset();
            var digest = compute();

            // 4. The drift check, against the master's Core and the block's first rep.
            string first;
            lock (block)
            {
                // The Enchantments digest is the profile's cast set, so a session casting another profile (a scenario override)
                // would read every rep as drift against the old one. The first rep under a new profile records it again. Core,
                // which the profile never touches, is still checked against the master.
                if (!string.Equals(block.FirstEnchantmentsProfile, _buffProfile, StringComparison.Ordinal))
                {
                    block.FirstEnchantments = null;
                    block.FirstEnchantmentsProfile = _buffProfile;
                }

                block.FirstEnchantments ??= digest.Enchantments;
                first = block.FirstEnchantments;
            }

            var drift = TrialDigest.IsDrift(digest, _masterCore, first, out var reason);

            // Drift flags this rep (it runs) and owes a rebuild before the next, for this rep character, across sessions.
            if (drift)
                MarkRebuildRequired(_repGuid, "state digest drift (" + reason + ")");

            return new TrialRepSetupResult(null, false, digest, drift, reason);
        }

        /// <summary>A rep of this session ended (TrialSession.RepEnded): one more rep in the block, and the seed walk steps on.</summary>
        public void OnRepEnded()
        {
            var block = blocks.GetOrAdd(_repGuid, _ => new Block());

            lock (block)
                block.RepsSinceCopy++;

            var walk = seedWalks.GetOrAdd(_repGuid, _ => new SeedWalk());

            lock (walk)
            {
                SyncScenario(walk);
                walk.Next++;
            }
        }

        /// <summary>The rep index the next rep's gem rolls with (TrialGemRoller.RollLive): this rep character's place in its seed walk for this scenario.</summary>
        public int SeedRep()
        {
            var walk = seedWalks.GetOrAdd(_repGuid, _ => new SeedWalk());

            lock (walk)
            {
                SyncScenario(walk);
                return walk.Next;
            }
        }

        /// <summary>A different scenario starts the walk again at 1 (the only reset).</summary>
        private void SyncScenario(SeedWalk walk)
        {
            if (!string.Equals(walk.ScenarioSha, _scenarioSha, StringComparison.Ordinal))
            {
                walk.ScenarioSha = _scenarioSha;
                walk.Next = 1;
            }
        }

        /// <summary>Marks <paramref name="repGuid"/> as owing a rebuild: every later rep is refused until ResetBlock. Any thread.</summary>
        public static void MarkRebuildRequired(uint repGuid, string reason, bool purgeFailed = false)
        {
            var block = blocks.GetOrAdd(repGuid, _ => new Block());

            lock (block)
            {
                block.NeedsRebuild = true;
                block.NeedsRebuildReason = reason;

                if (purgeFailed)
                    block.PurgeFailed = true;
            }
        }

        /// <summary>True when the last rebuild of <paramref name="repGuid"/> failed at its purge and no rebuild has completed since. Any thread.</summary>
        internal static bool PurgeFailed(uint repGuid)
        {
            if (!blocks.TryGetValue(repGuid, out var block))
                return false;

            lock (block)
                return block.PurgeFailed;
        }

        internal static bool NeedsRebuild(uint repGuid)
        {
            if (!blocks.TryGetValue(repGuid, out var block))
                return false;

            lock (block)
                return block.NeedsRebuild;
        }

        /// <summary>Starts a new block for <paramref name="repGuid"/> (a rebuild completed), clearing NeedsRebuild. The only clear. Any thread.</summary>
        public static void ResetBlock(uint repGuid) => blocks.TryRemove(repGuid, out _);

        internal static (int RepsSinceCopy, string FirstEnchantments) BlockState(uint repGuid)
        {
            if (!blocks.TryGetValue(repGuid, out var block))
                return (0, null);

            lock (block)
                return (block.RepsSinceCopy, block.FirstEnchantments);
        }

        internal static void ResetAllBlocksForTests()
        {
            blocks.Clear();
            seedWalks.Clear();
        }
    }
}
