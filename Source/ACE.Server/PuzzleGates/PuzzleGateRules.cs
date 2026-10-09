using System;

using ACE.Server.Entity;

namespace ACE.Server.PuzzleGates
{
    public enum PuzzleActivation
    {
        /// <summary>Inside the post-wrong lockout; nothing changed.</summary>
        Refused,

        /// <summary>The gate is already solved; nothing changed.</summary>
        AlreadySolved,

        /// <summary>Correct, more rounds remain: reshuffle.</summary>
        RoundAdvanced,

        /// <summary>Correct and the last round: open the gate.</summary>
        Solved,

        /// <summary>Wrong: progress reset, wrong count up, lockout started. Reshuffle.</summary>
        Wrong,
    }

    /// <summary>
    /// Pure round / wrong / lockout state over <see cref="ObjectiveLock"/>. Time is always a parameter.
    /// </summary>
    public sealed class PuzzleGateRules
    {
        private readonly ObjectiveLock _rounds;
        private readonly TimeSpan _lockout;

        public int Rounds { get; }

        public int WrongCount { get; private set; }

        public DateTime LockedUntil { get; private set; } = DateTime.MinValue;

        public bool Solved => _rounds.Latched;

        public PuzzleGateRules(int rounds, int lockoutSeconds)
        {
            Rounds = Math.Max(1, rounds);
            _lockout = TimeSpan.FromSeconds(Math.Max(0, lockoutSeconds));
            _rounds = new ObjectiveLock(Rounds);
        }

        public int RoundsCompleted(DateTime now) => (int)Math.Round(_rounds.CurrentWeight(now));

        public bool IsLocked(DateTime now) => now < LockedUntil;

        public PuzzleActivation Activate(bool correct, DateTime now)
        {
            if (_rounds.Latched)
                return PuzzleActivation.AlreadySolved;

            if (IsLocked(now))
                return PuzzleActivation.Refused;

            if (correct)
            {
                // Distinct key per completed round: Contribute replaces by key, so a repeated key could
                // never advance the counter.
                var key = "round:" + (RoundsCompleted(now) + 1);
                return _rounds.Contribute(key, 1.0, now) ? PuzzleActivation.Solved : PuzzleActivation.RoundAdvanced;
            }

            _rounds.Reset();
            WrongCount++;
            LockedUntil = now + _lockout;
            return PuzzleActivation.Wrong;
        }
    }
}