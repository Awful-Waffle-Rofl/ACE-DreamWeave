using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.Managers;

using K = ACE.Server.WorldEvents.DestroySourceKeys;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// The phased Destroy the Source settings, read ONCE when the objective is staged. <see cref="Read"/> never throws: a failed read
    /// (PropertyManager throws on a cache miss in a unit test) falls back to the compiled defaults, which are the shipped values.
    /// </summary>
    public sealed class DestroySourcePhaseSettings
    {
        public bool Enabled { get; init; } = true;
        public int PhaseCount { get; init; } = 10;
        public double QuotaFraction { get; init; } = 0.75;
        public double SoftTimeoutSeconds { get; init; } = 90;
        public double HardTimeoutSeconds { get; init; } = 180;
        public int BankMax { get; init; } = 1;

        /// <summary>The shipped values.</summary>
        public static DestroySourcePhaseSettings Defaults => new DestroySourcePhaseSettings();

        /// <summary>Test seam: where <see cref="Read"/> gets its values. Restore it in a finally.</summary>
        internal static Func<DestroySourcePhaseSettings> Reader = ReadLive;

        /// <summary>The settings for a run being staged now; the compiled defaults when the read fails.</summary>
        public static DestroySourcePhaseSettings Read()
        {
            try
            {
                return Sanitize(Reader());
            }
            catch (Exception)
            {
                return Defaults;
            }
        }

        private static DestroySourcePhaseSettings ReadLive()
        {
            var d = Defaults;

            return new DestroySourcePhaseSettings
            {
                Enabled = PropertyManager.GetBool(K.PropertyPrefix + K.PhasesEnabled, d.Enabled).Item,
                PhaseCount = (int)Math.Clamp(PropertyManager.GetLong(K.PropertyPrefix + K.PhaseCount, d.PhaseCount).Item, 1, 100),
                QuotaFraction = PropertyManager.GetDouble(K.PropertyPrefix + K.QuotaFraction, d.QuotaFraction).Item,
                SoftTimeoutSeconds = PropertyManager.GetDouble(K.PropertyPrefix + K.SoftTimeoutSeconds, d.SoftTimeoutSeconds).Item,
                HardTimeoutSeconds = PropertyManager.GetDouble(K.PropertyPrefix + K.HardTimeoutSeconds, d.HardTimeoutSeconds).Item,
                BankMax = (int)Math.Clamp(PropertyManager.GetLong(K.PropertyPrefix + K.BankMax, d.BankMax).Item, 0, 5),
            };
        }

        /// <summary>Clamps to the rows' ranges; a non-finite number reads as the default; the hard timeout is never below the soft one.</summary>
        internal static DestroySourcePhaseSettings Sanitize(DestroySourcePhaseSettings s)
        {
            if (s == null)
                return Defaults;

            var d = Defaults;
            var soft = Finite(s.SoftTimeoutSeconds, d.SoftTimeoutSeconds, 5, 1800);
            var hard = Finite(s.HardTimeoutSeconds, d.HardTimeoutSeconds, 10, 3600);

            return new DestroySourcePhaseSettings
            {
                Enabled = s.Enabled,
                PhaseCount = Math.Clamp(s.PhaseCount, 1, 100),
                QuotaFraction = Finite(s.QuotaFraction, d.QuotaFraction, 0.05, 3),
                SoftTimeoutSeconds = soft,
                HardTimeoutSeconds = Math.Max(hard, soft),
                BankMax = Math.Clamp(s.BankMax, 0, 5),
            };
        }

        private static double Finite(double v, double fallback, double min, double max) =>
            double.IsFinite(v) ? Math.Min(Math.Max(v, min), max) : fallback;
    }

    /// <summary>What the phase machine needs from the world: a source's guard and health, and the player text.</summary>
    public interface IDestroySourcePhasePort
    {
        /// <summary>Sets the source's immunity and its health floor (0 = no floor; never 0 until the final phase).</summary>
        void SetGuard(uint guid, bool immune, uint floorHp);

        /// <summary>The source's current and max health; false when it is not live (not placed yet, dead or destroyed).</summary>
        bool TryGetHealth(uint guid, out uint current, out uint max);

        /// <summary>One line of player text to the event area.</summary>
        void Announce(string line);
    }

    /// <summary>The pure arithmetic and text of the phases.</summary>
    public static class DestroySourcePhaseRules
    {
        public const string PhaseOpenText = "The ward falters ({phase} of {phases})! Strike while it is down.";
        public const string ReShieldText = "The ward rises again. Slay {need} more invaders to weaken it.";
        public const string FinalPhaseText = "The ward is broken. Destroy it!";

        /// <summary>
        /// The health floor while phase <paramref name="phase"/> of <paramref name="phaseCount"/> is open: ceil(max x (1 - phase/phaseCount)),
        /// so phase 0 is the full max (sealed). At and past the last phase the floor is 0 EXPLICITLY - the speed-course helper clamps to a
        /// minimum of 1 and would leave the source unkillable.
        /// </summary>
        public static uint FloorFor(uint maxHealth, int phase, int phaseCount)
        {
            if (maxHealth == 0 || phaseCount < 1 || phase >= phaseCount)
                return 0;

            var k = Math.Max(0, phase);
            var raw = ((ulong)maxHealth * (ulong)(phaseCount - k) + (ulong)phaseCount - 1) / (ulong)phaseCount;

            return (uint)Math.Min(raw, maxHealth);
        }

        /// <summary>Adds to slay for one phase: ceil(waveCount x fraction), at least 1.</summary>
        public static int QuotaFor(int waveCount, double fraction)
        {
            if (waveCount <= 0 || !double.IsFinite(fraction) || fraction <= 0)
                return 1;

            return Math.Max(1, (int)Math.Ceiling(waveCount * fraction - 1e-9));
        }

        public static string Format(string template, int phase, int phases, int need) =>
            template
                .Replace("{phase}", phase.ToString(CultureInfo.InvariantCulture))
                .Replace("{phases}", phases.ToString(CultureInfo.InvariantCulture))
                .Replace("{need}", need.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The phase machine of one phased Destroy the Source run. Pure: no engine type crosses <see cref="IDestroySourcePhasePort"/>.
    ///
    /// Phase k (0 = sealed) leaves each source a health floor of ceil(max x (1 - k/phaseCount)), so every phase lets 10% (at the default of
    /// 10 phases) of the source's OWN max be removed. A source whose health reaches its floor goes immune again and stays so until the next
    /// phase opens (sticky, so a regenerating rift does not flap). At phase = phaseCount the floor is 0 and the source is killable.
    ///
    /// Earning: every non-source event creature slain counts; <c>quota</c> of them earn one phase (carrying the surplus over). A boss death
    /// earns one full phase on its own. The quota only ratchets UP with the wave count. Soft timeout: no phase earned for that long and at
    /// least one kill this phase = the phase is earned (the quota fell to the kills made). Hard timeout: earned regardless of kills.
    ///
    /// Banking: an earned phase opens only while the open phase minus the phase the slowest live source has used up is at most bank_max, so
    /// the current phase plus bank_max more can sit unused. A boss phase ignores that gate.
    ///
    /// Threading: Tick runs on the world thread, but OnCreatureDied arrives on the landblock Parallel.ForEach threads, so the three entry points
    /// (RegisterSource, OnDeath, Tick) each take one private lock. The port is called inside it and never calls back in.
    /// </summary>
    public sealed class DestroySourcePhases
    {
        private sealed class SourceState
        {
            public uint Guid;
            public bool Dead;

            /// <summary>The last phase this source used up (health reached the floor). 0 for a freshly sealed source.</summary>
            public int SealedPhase;

            public bool? PushedImmune;
            public int PushedPhase = -1;
        }

        private readonly DestroySourcePhaseSettings settings;
        private readonly IDestroySourcePhasePort port;
        private readonly Func<int> waveCount;
        private readonly object gate = new object();
        private readonly List<SourceState> sources = new List<SourceState>();

        private int phase;
        private int quota = 1;
        private int killsInPhase;
        private int credits;
        private int bossCredits;
        private double? lastEarn;
        private double lastNow;
        private int riseAnnouncedPhase = -1;

        public DestroySourcePhases(DestroySourcePhaseSettings settings, IDestroySourcePhasePort port, Func<int> waveCount)
        {
            this.settings = DestroySourcePhaseSettings.Sanitize(settings);
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.waveCount = waveCount ?? (() => 0);
        }

        public bool Enabled => settings.Enabled;
        public int PhaseCount => settings.PhaseCount;

        /// <summary>Phases opened so far (0 = sealed).</summary>
        public int Phase => phase;

        public bool IsFinalPhase => phase >= settings.PhaseCount;
        public int Quota => quota;
        public int KillsInPhase => killsInPhase;

        /// <summary>Phases earned and waiting on the banking rule.</summary>
        public int PendingPhases => credits + bossCredits;

        /// <summary>
        /// A source was placed. <paramref name="guardApplied"/> says the spawn already put it in the sealed state (immune, floor = max), so the
        /// first tick still pushes the sealed state once, which plays the shield cue now that the source is in the world.
        /// </summary>
        public void RegisterSource(uint guid, bool guardApplied)
        {
            lock (gate)
                RegisterSourceCore(guid, guardApplied);
        }

        private void RegisterSourceCore(uint guid, bool guardApplied)
        {
            if (!settings.Enabled || guid == 0 || sources.Any(s => s.Guid == guid))
                return;

            sources.Add(new SourceState
            {
                Guid = guid,
                PushedImmune = guardApplied ? true : (bool?)null,
                PushedPhase = -1,
            });
        }

        /// <summary>
        /// A creature died. A source is marked dead; the boss earns a phase; any other event creature is a kill (the way KillCountObjective
        /// counts: stamped event creatures that are not sources).
        /// </summary>
        public void OnDeath(uint guid, bool wasEventCreature, bool isSource, bool isBoss)
        {
            lock (gate)
                OnDeathCore(guid, wasEventCreature, isSource, isBoss);
        }

        private void OnDeathCore(uint guid, bool wasEventCreature, bool isSource, bool isBoss)
        {
            if (!settings.Enabled)
                return;

            if (isSource)
            {
                var s = sources.FirstOrDefault(x => x.Guid == guid);

                if (s != null)
                    s.Dead = true;

                return;
            }

            if (!wasEventCreature || phase >= settings.PhaseCount)
                return;

            if (isBoss)
            {
                bossCredits++;
                lastEarn = lastNow;
                return;
            }

            // The credits a kill may bank are capped, so a long-blocked phase cannot pile up a burst of free ones.
            if (credits >= Math.Max(1, settings.BankMax))
                return;

            // A kill that lands before the first tick must not be weighed against the unsized quota of 1.
            RatchetQuota();

            killsInPhase++;

            if (killsInPhase >= quota)
            {
                killsInPhase -= quota;
                credits++;
                lastEarn = lastNow;
            }
        }

        public void Tick(double now)
        {
            lock (gate)
                TickCore(now);
        }

        private void TickCore(double now)
        {
            if (!settings.Enabled)
                return;

            lastNow = now;
            lastEarn ??= now;

            RatchetQuota();

            var live = new List<(SourceState S, uint Cur, uint Max)>();

            foreach (var s in sources)
            {
                if (!s.Dead && port.TryGetHealth(s.Guid, out var cur, out var max))
                    live.Add((s, cur, max));
            }

            // A source whose health is at this phase's floor has used the phase up (before any new phase is weighed against the bank).
            var rose = false;

            if (phase < settings.PhaseCount)
            {
                foreach (var (s, cur, max) in live)
                {
                    if (cur <= DestroySourcePhaseRules.FloorFor(max, phase, settings.PhaseCount) && s.SealedPhase != phase)
                    {
                        s.SealedPhase = phase;
                        rose = true;
                    }
                }
            }

            var before = phase;

            if (phase < settings.PhaseCount)
            {
                RunTimeouts(now);
                OpenEarned(live);
            }

            var opened = phase > before;

            // Guards, per source: sealed again once its health is at the floor, free while a phase is open.
            foreach (var (s, cur, max) in live)
            {
                var floor = DestroySourcePhaseRules.FloorFor(max, phase, settings.PhaseCount);

                if (phase < settings.PhaseCount && cur <= floor && s.SealedPhase != phase)
                    s.SealedPhase = phase;

                var immune = phase < settings.PhaseCount && s.SealedPhase == phase;

                if (s.PushedImmune != immune || s.PushedPhase != phase)
                {
                    s.PushedImmune = immune;
                    s.PushedPhase = phase;
                    port.SetGuard(s.Guid, immune, floor);
                }
            }

            if (opened)
            {
                port.Announce(phase >= settings.PhaseCount
                    ? DestroySourcePhaseRules.FinalPhaseText
                    : DestroySourcePhaseRules.Format(DestroySourcePhaseRules.PhaseOpenText, phase, settings.PhaseCount, 0));
            }
            else if (rose && PendingPhases == 0 && riseAnnouncedPhase != phase)
            {
                riseAnnouncedPhase = phase;
                port.Announce(DestroySourcePhaseRules.Format(DestroySourcePhaseRules.ReShieldText, phase, settings.PhaseCount,
                    Math.Max(1, quota - killsInPhase)));
            }
        }

        private void RatchetQuota()
        {
            int fresh;

            try
            {
                fresh = DestroySourcePhaseRules.QuotaFor(waveCount(), settings.QuotaFraction);
            }
            catch (Exception)
            {
                return;
            }

            if (fresh > quota)
                quota = fresh;
        }

        private void RunTimeouts(double now)
        {
            // Nothing earned is waiting: a phase held back by the banking rule is not a stall of the players' making.
            if (PendingPhases > 0)
                return;

            var idle = now - (lastEarn ?? now);

            if (idle >= settings.HardTimeoutSeconds || (idle >= settings.SoftTimeoutSeconds && killsInPhase >= 1))
            {
                credits++;
                killsInPhase = 0;
                lastEarn = now;
            }
        }

        private void OpenEarned(List<(SourceState S, uint Cur, uint Max)> live)
        {
            while (phase < settings.PhaseCount)
            {
                if (bossCredits > 0)
                {
                    bossCredits--;
                    phase++;
                    lastEarn = lastNow;
                    continue;
                }

                if (credits > 0 && BankAllows(live))
                {
                    credits--;
                    phase++;
                    lastEarn = lastNow;
                    continue;
                }

                break;
            }

            if (phase >= settings.PhaseCount)
            {
                credits = 0;
                bossCredits = 0;
            }
        }

        private bool BankAllows(List<(SourceState S, uint Cur, uint Max)> live)
        {
            if (live.Count == 0)
                return true;

            var slowest = live.Min(l => l.S.SealedPhase);

            return phase - slowest <= settings.BankMax;
        }
    }
}
