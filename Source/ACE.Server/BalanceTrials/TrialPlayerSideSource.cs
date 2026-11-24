using System;

using log4net;

using ACE.Server.CombatSimulator;
using ACE.Server.WorldObjects;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// Holds the session's <see cref="TrialPlayerSide"/>: requested ONCE at start from the offline master
    /// (ProfileBuilder.LoadOfflinePlayerAsync in production, whose callback runs on any thread, possibly synchronously), and
    /// marshalled onto the room's thread before it is stored. Same shape as TrialRepSetup's master digest, including the
    /// in-flight guard. Until the answer arrives, or if it fails (null), <see cref="Side"/> is null and spawns carry no
    /// prediction: nothing ever waits on it. Every member runs on the room's thread.
    /// </summary>
    internal sealed class TrialPlayerSideSource
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const double TimeoutSeconds = 30.0;

        private enum State { NotRequested, Pending, Ready, Failed }

        private readonly uint _masterGuid;
        private readonly Func<double> _clock;
        private readonly Action<uint, Action<TrialPlayerSide>> _load;
        private readonly Func<Action, bool> _marshal;

        private State _state;
        private TrialPlayerSide _side;
        private double _requestedAt;
        private int _generation;

        public TrialPlayerSideSource(uint masterGuid, Func<double> clock, Action<uint, Action<TrialPlayerSide>> load, Func<Action, bool> marshal)
        {
            _masterGuid = masterGuid;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));
        }

        /// <summary>The player side, or null until it has arrived (or when it failed).</summary>
        public TrialPlayerSide Side => _state == State.Ready ? _side : null;

        /// <summary>
        /// Asks for the player side. State is published BEFORE the loader is called, because the loader may call back
        /// synchronously. One outstanding load at a time; a load counts as dead only past <see cref="TimeoutSeconds"/>.
        /// </summary>
        public void Request()
        {
            if (_state == State.Ready || (_state == State.Pending && _clock() - _requestedAt <= TimeoutSeconds))
                return;

            var generation = ++_generation;
            _state = State.Pending;
            _requestedAt = _clock();

            _load(_masterGuid, side => _marshal(() => OnArrived(generation, side)));
        }

        private void OnArrived(int generation, TrialPlayerSide side)
        {
            if (_state == State.Ready)
                return;

            if (side != null)
            {
                _side = side;
                _state = State.Ready;
            }
            else if (generation == _generation)
            {
                _state = State.Failed;
            }
        }

        /// <summary>
        /// The LoadOfflinePlayerAsync projection: the master's primary attack and defender profile. Null when either cannot be
        /// built (no attack, a throw), so the session runs without predictions.
        /// </summary>
        internal static TrialPlayerSide Project(Player master)
        {
            try
            {
                return new TrialPlayerSide(PlayerAttackerSpecs.Primary(master), ProfileBuilder.BuildDefenderProfile(master), "primary attack, no target");
            }
            catch (Exception ex)
            {
                log.Warn($"[TRIAL] the player side of 0x{master?.Guid.Full:X8} could not be built; spawns carry no prediction", ex);
                return null;
            }
        }
    }
}
