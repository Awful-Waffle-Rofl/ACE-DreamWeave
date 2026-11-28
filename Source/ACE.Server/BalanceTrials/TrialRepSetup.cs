using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// What the pre-spawn rep setup decided. A non-null Refusal or RebuildRequired stops the rep before any spawn. Drift is
    /// information only. DriftUnknown: the master digest was not in hand, so the rep ran with nothing to compare against
    /// (Drift is false, DriftReason says why).
    /// </summary>
    public readonly record struct TrialRepSetupResult(string Refusal, bool RebuildRequired, TrialDigest Digest, bool Drift, string DriftReason, bool DriftUnknown = false);

    /// <summary>
    /// The pre-spawn rep steps for one session, in the Plan C Task 7 order: the HardCopyEvery schedule, then the soft reset,
    /// then the digest, then the drift check. Drift is information only (owner ruling 2026-11-24): a rep that differs from its
    /// master is flagged on its run row and runs, and nothing is refused for it. An unavailable master digest runs the rep too,
    /// with drift unknown. Only a failed purge and the opt-in hardCopyEvery schedule refuse a rep for a rebuild.
    ///
    /// THE BLOCK. A block is the run of reps on one rep character since its last copy. It is keyed by the rep's guid and
    /// outlives a session, because a rebuild needs the rep logged out, and logging out ends the session: the block, not the
    /// session, is what "since the last copy" counts. A COMPLETED rebuild (TrialRebuild.TryRebuild's DoCopyChar reporting
    /// success) starts a new block. The block also carries NeedsRebuild: only a purge that failed sets it (the rep's items are
    /// half deleted), and every later rep of that character is refused, in this session or any later one (a relog does not
    /// bypass it), until a rebuild completes. Drift never sets it, and a drift-set mark left over from before that rule is cleared
    /// at the next rep. The ledger is in memory only: a server restart starts every rep on a new block and clears NeedsRebuild,
    /// so the schedule can under-count across a restart.
    ///
    /// THE SEED WALK. A Step-mode scenario's gem seed walks by rep index (TrialGemRoller.RepSeed). The index fed to RollLive
    /// is the rep character's next index for this scenario, kept beside the block and outliving the session too, so a
    /// second session (after the relog a rebuild needs) carries on at seed + k instead of starting again at seed. It is
    /// keyed by the rep guid and remembers the scenario sha and the archetype id: a different scenario, or a different
    /// archetype on the same scenario, starts the walk again at 1, and that is the only reset (so every archetype meets a
    /// rotation in the same order). A completed rebuild (ResetBlock) deliberately does NOT reset it: hardCopyEvery forces a rebuild
    /// every few reps, and resetting there would repeat the same seeds (final review F5). One exception: a ROTATION scenario's
    /// session that reaches Done clears its walk (OnRepEnded's runDone, passed only when the scenario has a rotation), so a fresh
    /// run of the same archetype starts at entry 1. A scenario without a rotation never does, so its Step seeds stay fresh each run.
    /// In memory only, like the block:
    /// a server restart starts every walk again at 1.
    ///
    /// THE MASTER DIGEST is computed once per session, off the offline master, through the loader seam
    /// (ProfileBuilder.LoadOfflinePlayerAsync in production, whose callback runs on any thread, possibly synchronously).
    /// The answer is a <see cref="TrialMasterSnapshot"/>: the digest. The result is marshalled onto the room's thread before it
    /// is stored. Until it arrives (or when it cannot, a null answer: master online, missing or failed to load), a rep still RUNS,
    /// with drift unknown: the digest never blocks a rep. No answer within <see cref="MasterDigestTimeoutSeconds"/> (a swallowed
    /// shard read never calls back) or a null answer is asked again on the next rep.
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
            /// <summary>The scenario sha and the archetype id (WalkKey): a change of either restarts the walk at 1.</summary>
            public string Key;
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
        private readonly string _archetypeId;

        private MasterState _masterState;
        private string _masterCore;
        private double _requestedAt;
        private int _generation;

        /// <param name="loadMaster">Loads the master's snapshot: (masterGuid, callback). The callback may run on any thread, and may receive null.</param>
        /// <param name="marshal">Runs an action on the room's thread; false when nothing was queued (the session is gone).</param>
        /// <param name="scenarioSha">The scenario this session runs; keys the seed walk.</param>
        /// <param name="buffProfile">The effective buff profile this session casts (TrialBuffs.EffectiveProfile); keys the block's first-rep Enchantments.</param>
        /// <param name="archetypeId">The archetype this session runs; with the scenario sha it keys the seed walk, so each archetype walks the scenario from rep 1.</param>
        public TrialRepSetup(uint repGuid, uint masterGuid, int hardCopyEvery, Func<double> clock, Action<uint, Action<TrialMasterSnapshot>> loadMaster, Func<Action, bool> marshal,
            string scenarioSha = null, string buffProfile = null, string archetypeId = null)
        {
            _scenarioSha = scenarioSha ?? "";
            _buffProfile = buffProfile ?? "";
            _archetypeId = archetypeId ?? "";
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
                // A mark that did not come from a failed purge is a leftover of the old drift rule: drift no longer blocks.
                if (block.NeedsRebuild && !block.PurgeFailed)
                {
                    block.NeedsRebuild = false;
                    block.NeedsRebuildReason = null;
                }

                if (block.NeedsRebuild)
                    return new TrialRepSetupResult($"rebuild required: {block.NeedsRebuildReason}; run @trial rebuild", true, null, false, null);

                repsSinceCopy = block.RepsSinceCopy;
            }

            // 1. The hard-copy schedule.
            if (_hardCopyEvery > 0 && repsSinceCopy >= _hardCopyEvery)
                return new TrialRepSetupResult($"hard copy due: {repsSinceCopy} rep(s) since the last copy (hardCopyEvery {_hardCopyEvery}); run @trial rebuild", true, null, false, null);

            // The master digest is wanted for the drift comparison but never blocks a rep: with none in hand the rep runs with drift
            // unknown. Waiting for a pending answer would hold the room thread (the answer is marshalled onto it), so a pending
            // digest is not waited for; a dead or failed load is asked for again, so a later rep compares.
            var masterReady = _masterState == MasterState.Ready;
            string unknownReason = null;

            if (!masterReady)
            {
                var pendingInTime = _masterState == MasterState.Pending && _clock() - _requestedAt <= MasterDigestTimeoutSeconds;

                unknownReason = _masterState == MasterState.Pending
                    ? (pendingInTime ? "master digest still pending" : $"master digest pending for over {MasterDigestTimeoutSeconds:0} s")
                    : "master digest unavailable (master online, missing or failed to load)";

                if (!pendingInTime)
                    RequestMasterDigest();
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

            if (!masterReady)
                return new TrialRepSetupResult(null, false, digest, false, "drift unknown: " + unknownReason, DriftUnknown: true);

            // Drift is information only: it flags this rep's row, and nothing is owed for it.
            var drift = TrialDigest.IsDrift(digest, _masterCore, first, out var reason);

            return new TrialRepSetupResult(null, false, digest, drift, reason);
        }

        /// <summary>A rep of this session ended (TrialSession.RepEnded): one more rep in the block, and the seed walk steps on.</summary>
        /// <param name="runDone">True when this rep finished a ROTATION scenario's run (TrialPhase.Done with a rotation): the walk key is cleared, so a fresh run of the same archetype and scenario starts at 1. The caller passes false for a scenario without a rotation.</param>
        public void OnRepEnded(bool runDone = false)
        {
            var block = blocks.GetOrAdd(_repGuid, _ => new Block());

            lock (block)
                block.RepsSinceCopy++;

            var walk = seedWalks.GetOrAdd(_repGuid, _ => new SeedWalk());

            lock (walk)
            {
                SyncScenario(walk);
                walk.Next++;

                if (runDone)
                {
                    walk.Key = null;
                    walk.Next = 1;
                }
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

        /// <summary>
        /// The walk index the next rep of <paramref name="repGuid"/> would use under this scenario and archetype, without creating or
        /// moving a walk (1 when there is none or it belongs to another scenario or archetype). Any thread. For @trial status.
        /// </summary>
        public static int PeekSeedRep(uint repGuid, string scenarioSha, string archetypeId)
        {
            if (!seedWalks.TryGetValue(repGuid, out var walk))
                return 1;

            lock (walk)
                return string.Equals(walk.Key, WalkKey(scenarioSha, archetypeId), StringComparison.Ordinal) ? walk.Next : 1;
        }

        private static string WalkKey(string scenarioSha, string archetypeId) => (scenarioSha ?? "") + "|" + (archetypeId ?? "");

        /// <summary>A different scenario, or a different archetype on the same scenario, starts the walk again at 1 (the only reset).</summary>
        private void SyncScenario(SeedWalk walk)
        {
            var key = WalkKey(_scenarioSha, _archetypeId);

            if (!string.Equals(walk.Key, key, StringComparison.Ordinal))
            {
                walk.Key = key;
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
