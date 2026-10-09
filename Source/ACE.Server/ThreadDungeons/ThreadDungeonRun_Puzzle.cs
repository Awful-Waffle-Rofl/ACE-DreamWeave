using System.Threading;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Puzzle gates in Thread runs: the three settings stamped by TryStart, and the reward seal.
    ///
    /// PLACED AT ARMING. The puzzle pass seals a run PENDING at populate (<see cref="SealRewardPending"/>) and places
    /// nothing; the kill that arms the run places the reward scene near the clearing player and attaches it
    /// (<see cref="AttachRewardPlacement"/>). A failed arming placement unseals at once. <see cref="SealReward"/>
    /// (seal with a placement in hand) remains for callers that place first.
    ///
    /// THE SEAL (architect design R2). A run whose reward scene spawned is SEALED: <see cref="CheckClearedLocked"/>
    /// will not move it to Cleared while the seal holds, however many kills land, so neither the clear
    /// announcement nor pooled-loot delivery can happen. The reward scene's levers are refused (no penalty) until
    /// <see cref="IsRewardArmed"/>; solving it calls <see cref="UnsealReward"/>, which re-runs the clear check, and the
    /// manager's OnRewardUnsealed then does exactly what OnRunPopulated does.
    ///
    /// FAIL-OPEN. Every path that removes the reward placement without a solve unseals (the run host's
    /// OnRemoved: spawn failure, reshuffle failure, clear, reap), and ThreadDungeonManager.Tick's watchdog
    /// unseals a sealed run whose placement is no longer registered. A sealed run with no live puzzle would
    /// otherwise sit Active until its TTL with every kill done.
    ///
    /// All seal state lives under stateLock with the rest of the clear rule.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        /// <summary>
        /// dynamic_dungeons_puzzle_gates_enabled, read once by TryStart and stamped before publication (the
        /// ExitTo convention). Defaults to false so a run that was never stamped - tests, any other caller - has
        /// no puzzles; the production default (true) lives in PropertyManager.
        /// </summary>
        public bool PuzzleGatesEnabled { get; set; }

        /// <summary>dynamic_dungeons_puzzle_gates_per_run, clamped by TryStart. Stamped like <see cref="PuzzleGatesEnabled"/>.</summary>
        public int PuzzleGatesPerRun { get; set; }

        /// <summary>dynamic_dungeons_puzzle_reward_scene_enabled. Stamped like <see cref="PuzzleGatesEnabled"/>.</summary>
        public bool PuzzleRewardSceneEnabled { get; set; }

        private bool rewardSealed;
        private bool rewardArmedNoticed;
        private object rewardPuzzle;

        /// <summary>
        /// Sealed at populate with NO placement yet: the reward scene is placed at arming, near the clearing player
        /// (ThreadPuzzlePass.PlaceRewardAtArming). Cleared when the placement is attached or the seal lifts.
        /// </summary>
        private bool rewardPending;

        /// <summary>The curated reward pick the pass planned at populate; the arming placer's last-resort site.</summary>
        private ThreadPuzzlePick? rewardFallbackPick;

        /// <summary>When a pending run was first seen armed without a placement (the watchdog's stall clock).</summary>
        private System.DateTime? pendingArmedSinceUtc;

        /// <summary>The gate puzzles this run actually placed (site and type), for the arming placer's exclusions.</summary>
        private readonly System.Collections.Generic.List<ThreadPuzzlePick> placedGatePicks = new System.Collections.Generic.List<ThreadPuzzlePick>();

        /// <summary>
        /// How long a pending run may sit armed without its arming placement before the watchdog unseals it. The
        /// arming placement is synchronous on the clearing kill, so this only ever fires when that path was skipped
        /// (a throw before it, a lost call); it is a fail-open backstop, not a timing assumption.
        /// </summary>
        public static readonly System.TimeSpan PendingArmedGrace = System.TimeSpan.FromSeconds(10);

        /// <summary>True while the run is sealed pending its arming placement (no reward scene in the world yet).</summary>
        public bool IsRewardPending { get { lock (stateLock) return rewardSealed && rewardPending; } }

        /// <summary>The curated reward pick recorded by <see cref="SealRewardPending"/>, or null.</summary>
        public ThreadPuzzlePick? RewardFallbackPick { get { lock (stateLock) return rewardFallbackPick; } }

        /// <summary>A snapshot of the gate puzzles this run placed.</summary>
        public System.Collections.Generic.List<ThreadPuzzlePick> PlacedGatePicks
        {
            get { lock (stateLock) return new System.Collections.Generic.List<ThreadPuzzlePick>(placedGatePicks); }
        }

        /// <summary>Records a gate puzzle the pass placed (the arming placer excludes its site and its type).</summary>
        public void RecordPlacedGatePick(ThreadPuzzlePick pick)
        {
            lock (stateLock)
                placedGatePicks.Add(pick);
        }

        /// <summary>
        /// Seals the run's reward WITHOUT a placement: the reward scene is placed later, at arming. The clear is
        /// withheld exactly as by <see cref="SealReward"/>. Refused (false) once the run has Cleared or Ended, or when
        /// it is already sealed. <paramref name="fallback"/> is the curated site the pass would have used at
        /// populate; the arming placer falls back to it when it has no player position or no other candidate.
        /// </summary>
        public bool SealRewardPending(ThreadPuzzlePick fallback)
        {
            lock (stateLock)
            {
                if (rewardSealed || rewardPuzzle != null)
                    return false;

                if (state != ThreadDungeonRunState.Starting && state != ThreadDungeonRunState.Active)
                    return false;

                rewardFallbackPick = fallback;
                rewardPending = true;
                rewardSealed = true;
                return true;
            }
        }

        /// <summary>
        /// Attaches the arming placement to a pending seal. False when the run is no longer pending-sealed (the
        /// seal lifted, a placement is already attached) or is no longer Active; the caller then owns the orphan.
        /// </summary>
        public bool AttachRewardPlacement(object placement)
        {
            if (placement == null)
                return false;

            lock (stateLock)
            {
                if (!rewardSealed || !rewardPending || rewardPuzzle != null)
                    return false;

                if (state != ThreadDungeonRunState.Active)
                    return false;

                rewardPuzzle = placement;
                rewardPending = false;
                return true;
            }
        }

        /// <summary>
        /// The watchdog's pending-seal test. False (hold) while the run is not pending, or pending and not yet armed
        /// (the kills are still being done: the seal is waiting legitimately). Once armed with still no placement,
        /// the first call starts a clock and returns false; a call at least <see cref="PendingArmedGrace"/> later
        /// returns true, and the watchdog then unseals.
        /// </summary>
        public bool PendingRewardStalled(System.DateTime now)
        {
            lock (stateLock)
            {
                if (!rewardSealed || !rewardPending)
                    return false;

                if (state != ThreadDungeonRunState.Active || ClearProgressLocked() < ClearFraction - 1e-9)
                    return false;

                if (pendingArmedSinceUtc == null)
                {
                    pendingArmedSinceUtc = now;
                    return false;
                }

                return now - pendingArmedSinceUtc.Value >= PendingArmedGrace;
            }
        }

        /// <summary>0/1: set once the puzzle pass registered any placement for this run, so the watchdog skips the rest.</summary>
        private int hasPuzzles;

        /// <summary>True once the puzzle pass registered at least one placement for this run.</summary>
        public bool HasPuzzles => Volatile.Read(ref hasPuzzles) != 0;

        public void MarkHasPuzzles() => Interlocked.Exchange(ref hasPuzzles, 1);

        /// <summary>Is the run's reward sealed behind its reward scene right now?</summary>
        public bool IsRewardSealed { get { lock (stateLock) return rewardSealed; } }

        /// <summary>The reward scene's placement (a PuzzleGatePlacement; typed loosely so this class stays world-free). Null while none.</summary>
        public object RewardPuzzle { get { lock (stateLock) return rewardPuzzle; } }

        /// <summary>
        /// Seals the run's reward behind <paramref name="placement"/>. Refused (false) once the run has Cleared or
        /// Ended, or when it is already sealed: a run has ONE reward scene. The placement is recorded in the same
        /// critical section as the seal, so the watchdog can never observe a seal without its placement.
        /// </summary>
        public bool SealReward(object placement)
        {
            if (placement == null)
                return false;

            lock (stateLock)
            {
                if (rewardSealed || rewardPuzzle != null)
                    return false;

                if (state != ThreadDungeonRunState.Starting && state != ThreadDungeonRunState.Active)
                    return false;

                rewardPuzzle = placement;
                rewardSealed = true;
                return true;
            }
        }

        /// <summary>
        /// Lifts the seal and re-runs the clear check, so a run whose kills were already done moves to Cleared
        /// here. True for the ONE caller that lifted it (the solve, or whichever fail-open path got there first);
        /// false when the run was not sealed. The caller that gets true owes ThreadDungeonManager.OnRewardUnsealed.
        /// </summary>
        public bool UnsealReward()
        {
            lock (stateLock)
            {
                if (!rewardSealed)
                    return false;

                rewardSealed = false;
                rewardPending = false;
                CheckClearedLocked();
                return true;
            }
        }

        /// <summary>
        /// May the reward scene be attempted? Only while the run is Active and its weighted clear progress has
        /// reached ClearFraction - the point at which an unsealed run would have Cleared.
        /// </summary>
        public bool IsRewardArmed
        {
            get
            {
                lock (stateLock)
                    return state == ThreadDungeonRunState.Active && ClearProgressLocked() >= ClearFraction - 1e-9;
            }
        }

        /// <summary>True for the ONE caller that may tell the run "the kills are done, the reward is sealed": sealed, armed, and not told before.</summary>
        public bool TryClaimRewardArmedNotice()
        {
            lock (stateLock)
            {
                if (!rewardSealed || rewardArmedNoticed)
                    return false;

                if (state != ThreadDungeonRunState.Active || ClearProgressLocked() < ClearFraction - 1e-9)
                    return false;

                rewardArmedNoticed = true;
                return true;
            }
        }

        // ---- the fail policy's removed set (ThreadPuzzleFailPolicy) ----

        /// <summary>Roster members the puzzle fail policy removed from this run. Under stateLock. Never shrinks.</summary>
        private readonly System.Collections.Generic.HashSet<uint> puzzleRemoved = new System.Collections.Generic.HashSet<uint>();

        /// <summary>
        /// Removes a roster member from this run for good: from then on they are never inside it
        /// (ThreadRunPresence.IsMemberInside, so no clear reward, no rare, no message), hold no deal seat
        /// (<see cref="DealSeats"/>, so the pooled deal and the reap hold skip them), file no survey or clear count
        /// (<see cref="SnapshotRoster"/>'s PuzzleRemoved, read by SurveyRecipients), earn no guide credit, and may not
        /// re-enter (the gem handler's ReEnter branch). True when this call removed them; false for a non-member or a
        /// repeat.
        /// </summary>
        public bool MarkPuzzleRemoved(uint guid)
        {
            if (!IsRosterMember(guid))
                return false;

            lock (stateLock)
                return puzzleRemoved.Add(guid);
        }

        public bool IsPuzzleRemoved(uint guid)
        {
            lock (stateLock)
                return puzzleRemoved.Contains(guid);
        }
    }
}
