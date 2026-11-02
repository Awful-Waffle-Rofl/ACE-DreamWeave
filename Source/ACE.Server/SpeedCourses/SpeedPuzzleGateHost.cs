using ACE.Server.PuzzleGates;
using ACE.Server.WorldObjects;

namespace ACE.Server.SpeedCourses
{
    /// <summary>What a speed puzzle host reports back to the course run that placed it.</summary>
    public interface ISpeedPuzzleOwner
    {
        /// <summary>The ephemeral instance id: the placement's RunId, which ClearForRun keys on.</summary>
        uint Instance { get; }

        /// <summary>The last round was answered (landblock thread).</summary>
        void OnPuzzleSolved(SpeedPuzzleDef puzzle, Player player);

        /// <summary>The placement left the registry (any thread).</summary>
        void OnPuzzleRemoved(SpeedPuzzleDef puzzle, PuzzleRemovalReason reason, bool solved);

        void Log(string line);
    }

    /// <summary>
    /// The host of one speed-course puzzle placement (owner ruling 2026-10-22: a wrong answer costs a 5 s delay and
    /// a re-rolled puzzle, nothing else).
    /// <list type="bullet">
    /// <item><see cref="PolicyMode"/> None: no boot, no lockout beyond the placement's own, no IP effect. <see cref="OnWrong"/>
    /// only logs; it never reaches ThreadPuzzleRunHost.FailSink or ThreadPuzzleFailPolicy. The delay and the re-roll
    /// are the manager's ONE wrong-answer path (PuzzleGateRules.Activate resets progress and starts the lockout,
    /// PuzzleGateManager.HandleActivation reshuffles), the same for every host.</item>
    /// <item><see cref="RunId"/> is the instance, so ClearForRun(instance) clears exactly this course's gates (a Thread
    /// run's id is its own instance, so the two never collide), and the admin tool leaves them alone.</item>
    /// <item><see cref="AllowAmbush"/> false.</item>
    /// <item><see cref="OnSolved"/> reports to the owner, which contributes the slot's token (P1/P3) to the Seal; P2 has no token.</item>
    /// <item><see cref="OnRemoved"/> without a solve reports to the owner, which re-places the site (bounded).</item>
    /// </list>
    /// </summary>
    public sealed class SpeedPuzzleGateHost : IPuzzleGateHost
    {
        public SpeedPuzzleGateHost(ISpeedPuzzleOwner owner, SpeedPuzzleDef puzzle)
        {
            Owner = owner ?? throw new System.ArgumentNullException(nameof(owner));
            Puzzle = puzzle ?? throw new System.ArgumentNullException(nameof(puzzle));
        }

        public ISpeedPuzzleOwner Owner { get; }

        public SpeedPuzzleDef Puzzle { get; }

        public PuzzlePolicyMode PolicyMode => PuzzlePolicyMode.None;

        public uint RunId => Owner.Instance;

        public bool AllowAmbush => false;

        public string CheckActivation(PuzzleGatePlacement placement, Player player) => null;

        public void OnSolved(PuzzleGatePlacement placement, Player player) => Owner.OnPuzzleSolved(Puzzle, player);

        public void OnWrong(PuzzleGatePlacement placement, Player player, bool scored)
        {
            if (scored)
                Owner.Log($"puzzle {Puzzle.Slot} wrong answer by {player?.Name ?? "-"}; lockout then a re-rolled round (no fail policy)");
        }

        public void OnRemoved(PuzzleGatePlacement placement, PuzzleRemovalReason reason, bool solved) => Owner.OnPuzzleRemoved(Puzzle, reason, solved);

        public void Report(PuzzleGatePlacement placement, string text) => Owner.Log($"puzzle {Puzzle.Slot} {text}");
    }
}
