using System;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Tunables snapshot for the outdoor perimeter rules (design R4, R11-R20). <see cref="FromTunables"/> is the ONLY
    /// place in the perimeter rules that reads PropertyManager; every rule takes this record so tests never need it.
    /// </summary>
    public sealed record OutdoorPerimeterSettings(
        double Band,
        double BackstopM,
        bool UnravelEnabled,
        double StartPct,
        double StepPct,
        bool Lethal,
        double GraceS,
        bool BackstopEnabled,
        double ContainmentMarginM,
        double InteriorAwarenessM)
    {
        /// <summary>Seconds between stack decay steps while the player is inside or in the warning band (R17).</summary>
        public const double DecayStepIntervalS = 2.0;

        /// <summary>Minimum seconds between processed ticks (0.9, so scheduler jitter on a 1 Hz caller never drops a tick; a faster caller still gets about 1 Hz).</summary>
        public const double MinTickIntervalS = 0.9;

        /// <summary>Upper bound on the Unraveling stack and on the damage percentage.</summary>
        public const int MaxStack = 1000;
        public const double MaxPct = 100.0;

        /// <summary>A client that has sent no inbound packet for longer than this many seconds is treated as stale (R19). Standing still is NOT stale: the client keeps sending while idle.</summary>
        public const double InboundStalenessS = 2.0;

        /// <summary>Awareness floor in metres for the drop-aggro clamp (R14).</summary>
        public const float AwarenessFloorM = 10f;

        /// <summary>Metres kept between a creature's awareness edge and the drop (R14).</summary>
        public const double AwarenessDropGapM = 5.0;

        /// <summary>Reads the live dynamic_dungeons_outdoor_* tunables through <see cref="OutdoorSiteTunables"/>.</summary>
        public static OutdoorPerimeterSettings FromTunables() => new OutdoorPerimeterSettings(
            OutdoorSiteTunables.WarningBandM,
            OutdoorSiteTunables.BackstopM,
            OutdoorSiteTunables.UnravelEnabled,
            OutdoorSiteTunables.UnravelStartPct,
            OutdoorSiteTunables.UnravelStepPct,
            OutdoorSiteTunables.UnravelLethal,
            OutdoorSiteTunables.UnravelGraceS,
            OutdoorSiteTunables.BackstopEnabled,
            OutdoorSiteTunables.ContainmentMarginM,
            OutdoorSiteTunables.InteriorAwarenessM);
    }

    /// <summary>Where a player stands relative to the circle (R15).</summary>
    public enum PerimeterZone { Inside, Warning, Outside, Backstop }

    /// <summary>What the caller must do after a tick (R16-R20).</summary>
    public enum PerimeterAction { None, WarnEnteredBand, Damage, SnapBackToDrop }

    /// <summary>Per-player state carried between ticks. <see cref="OutsideTicks"/> is the Unraveling stack; <see cref="LastTickAt"/> is the NowSeconds of the last processed tick (the 1 Hz rate limit). Callers MUST construct this explicitly: default(PlayerPerimeterState) has LastTickAt = 0 (not negative infinity), which would rate-limit a first tick at NowSeconds below 0.9.</summary>
    public readonly record struct PlayerPerimeterState(int OutsideTicks, double LastDecayAt, bool WarnedThisVisit, double LastTickAt = double.NegativeInfinity);

    /// <summary>One tick's observations, gathered by the caller (R19 inputs plus zone and health). SecondsSinceLastInbound is client liveness (time since the last inbound packet), not movement.</summary>
    public readonly record struct PlayerTickInput(
        PerimeterZone Zone,
        bool IsTeleporting,
        bool IsDead,
        bool IsLoggingOut,
        double SecondsSinceArrival,
        double SecondsSinceLastInbound,
        long CurrentHealth,
        long MaxHealth,
        double NowSeconds);

    /// <summary>The tick's verdict. <see cref="Damage"/> is a non-negative health amount; <see cref="MessageKey"/> is null when nothing should be said.</summary>
    public readonly record struct PlayerTickResult(PlayerPerimeterState State, long Damage, PerimeterAction Action, string MessageKey);

    /// <summary>
    /// Pure perimeter and creature-containment rules for outdoor Threads sites (Open World Threads WP4a).
    /// No PropertyManager reads (see <see cref="OutdoorPerimeterSettings.FromTunables"/>), no world objects, no clocks:
    /// every distance is HORIZONTAL global metres from the circle centre (R4), time is passed in.
    /// </summary>
    public static class OutdoorPerimeterRules
    {
        public const string MsgWarnBand = "owt.unravel.warn";
        public const string MsgDamage = "owt.unravel.damage";
        public const string MsgSnapBack = "owt.unravel.snapback";
        public const string MsgBackstop = "owt.backstop.snapback";

        /// <summary>
        /// R15. Classifies a player. Backstop: strictly past R + backstopM, or not on a footprint/buffer landblock.
        /// Outside: strictly past R. Warning: strictly past R - band (up to and including R), matching the store lint. Else Inside.
        /// The edge values R and R - band belong to the inner zone's side as stated: exactly R is Warning (the circle
        /// contains its edge), exactly R - band is Inside, exactly R + backstopM is Outside.
        /// </summary>
        public static PerimeterZone Classify(double horizDistFromCentre, double radius, bool onFootprintOrBuffer, OutdoorPerimeterSettings s)
        {
            if (!onFootprintOrBuffer || horizDistFromCentre > radius + s.BackstopM) return PerimeterZone.Backstop;
            if (horizDistFromCentre > radius) return PerimeterZone.Outside;
            if (horizDistFromCentre > radius - s.Band) return PerimeterZone.Warning;
            return PerimeterZone.Inside;
        }

        /// <summary>
        /// R15-R20. One tick for one player in an outdoor run, intended at 1 Hz. <c>NowSeconds</c> MUST be monotonic
        /// (Timers.RunningTime); a clock that runs backwards decays nothing and is rate-limited until it catches up.
        /// HARD HOLD (R19): teleporting, dead or logging out returns the state UNCHANGED (no decay, no stamp).
        /// RATE LIMIT: when <c>now - LastTickAt &lt; 0.9</c> the call is a no-op, so a faster caller gets about 1 Hz behaviour; every
        /// other call stamps <c>LastTickAt</c>.
        /// SUPPRESSION (R19): within the arrival grace (<c>secondsSinceArrival &lt; graceS</c>) or with a stale client
        /// (no inbound packet for more than <see cref="OutdoorPerimeterSettings.InboundStalenessS"/>; standing still is NOT stale)
        /// there is no damage, no snap-back and no warning, but Inside/Warning decay still runs.
        /// Inside/Warning (R17): the stack sheds floor(elapsed / 2 s) steps (floor 0) and the decay clock advances by whole steps.
        /// Warning (R16): WarnEnteredBand once per visit; the visit resets on Inside and on any snap-back.
        /// Outside (R17/R18): stack+1 (max 1000), damage = round((start + step*(stack-1) capped at 100)/100 * maxHealth), min 1.
        /// Non-lethal and health - damage &lt;= 1: SnapBackToDrop, damage = max(0, health-1), stack cleared. Lethal: the full amount.
        /// <c>UnravelEnabled == false</c> disables only the Outside penalty (the backstop still applies).
        /// Backstop (R20): enabled = SnapBackToDrop with no damage, stack cleared; disabled = treated as Outside.
        /// </summary>
        public static PlayerTickResult Tick(PlayerPerimeterState state, PlayerTickInput input, OutdoorPerimeterSettings s)
        {
            if (input.IsTeleporting || input.IsDead || input.IsLoggingOut)
                return new PlayerTickResult(state, 0, PerimeterAction.None, null);

            if (input.NowSeconds - state.LastTickAt < OutdoorPerimeterSettings.MinTickIntervalS)
                return new PlayerTickResult(state, 0, PerimeterAction.None, null);

            var now = input.NowSeconds;
            var suppressed = input.SecondsSinceArrival < s.GraceS
                || input.SecondsSinceLastInbound > OutdoorPerimeterSettings.InboundStalenessS;

            var zone = input.Zone;
            if (zone == PerimeterZone.Backstop)
            {
                if (suppressed)
                    return new PlayerTickResult(state with { LastTickAt = now, LastDecayAt = now }, 0, PerimeterAction.None, null);
                if (s.BackstopEnabled)
                    return new PlayerTickResult(new PlayerPerimeterState(0, now, false, now), 0, PerimeterAction.SnapBackToDrop, MsgBackstop);
                // Backstop disabled: falls through to the Outside branch below.
            }

            if (zone == PerimeterZone.Inside || zone == PerimeterZone.Warning)
            {
                var stack = state.OutsideTicks;
                var lastDecay = state.LastDecayAt;
                if (stack <= 0)
                {
                    stack = 0;
                    lastDecay = Math.Max(lastDecay, now);
                }
                else
                {
                    var steps = (long)Math.Floor((now - lastDecay) / OutdoorPerimeterSettings.DecayStepIntervalS);
                    if (steps > 0)
                    {
                        stack = (int)Math.Max(0, stack - steps);
                        lastDecay += steps * OutdoorPerimeterSettings.DecayStepIntervalS;
                    }
                }

                if (zone == PerimeterZone.Inside)
                    return new PlayerTickResult(new PlayerPerimeterState(stack, lastDecay, false, now), 0, PerimeterAction.None, null);

                if (!state.WarnedThisVisit && !suppressed)
                    return new PlayerTickResult(new PlayerPerimeterState(stack, lastDecay, true, now), 0, PerimeterAction.WarnEnteredBand, MsgWarnBand);
                return new PlayerTickResult(new PlayerPerimeterState(stack, lastDecay, state.WarnedThisVisit, now), 0, PerimeterAction.None, null);
            }

            // Outside
            if (suppressed || !s.UnravelEnabled)
                return new PlayerTickResult(state with { LastTickAt = now, LastDecayAt = now }, 0, PerimeterAction.None, null);

            var n = Math.Min(state.OutsideTicks + 1, OutdoorPerimeterSettings.MaxStack);
            var pct = Math.Min(s.StartPct + s.StepPct * (n - 1), OutdoorPerimeterSettings.MaxPct);
            var damage = (long)Math.Round(pct / 100.0 * input.MaxHealth, MidpointRounding.AwayFromZero);
            if (damage < 1) damage = 1;

            if (!s.Lethal && input.CurrentHealth - damage <= 1)
            {
                var safe = Math.Max(0, input.CurrentHealth - 1);
                return new PlayerTickResult(new PlayerPerimeterState(0, now, false, now), safe, PerimeterAction.SnapBackToDrop, MsgSnapBack);
            }

            return new PlayerTickResult(new PlayerPerimeterState(n, now, state.WarnedThisVisit, now), damage, PerimeterAction.Damage, MsgDamage);
        }

        /// <summary>R11(a). True when a run creature is past R - containmentMargin and must be sent home (past, not at, the line).</summary>
        public static bool ShouldReturnHome(double creatureHorizDist, double radius, OutdoorPerimeterSettings s)
            => creatureHorizDist > radius - s.ContainmentMarginM;

        /// <summary>R11(b). A player target past R is invalid; non-players (pets, creatures) are always valid. Exactly R is still valid.</summary>
        public static bool IsValidTarget(bool targetIsPlayer, double targetHorizDist, double radius)
            => !targetIsPlayer || targetHorizDist <= radius;

        /// <summary>R12. Damage from a player past R to a run creature is refused. Exactly R is allowed; non-player attackers are never refused here.</summary>
        public static bool ShouldRefuseDamage(bool attackerIsPlayer, double attackerHorizDist, double radius)
            => attackerIsPlayer && attackerHorizDist > radius;

        /// <summary>
        /// R13/R14. Interior-spawned creatures cap awareness at interiorAwarenessM; every creature is further capped at
        /// max(10, pointDistToDrop - 5) so it cannot aggro onto the drop from its point.
        /// </summary>
        public static float ClampAwareness(float current, bool spawnedInterior, double pointDistToDrop, OutdoorPerimeterSettings s)
        {
            var result = current;
            if (spawnedInterior) result = Math.Min(result, (float)s.InteriorAwarenessM);
            var dropCap = (float)Math.Max(OutdoorPerimeterSettings.AwarenessFloorM, pointDistToDrop - OutdoorPerimeterSettings.AwarenessDropGapM);
            return Math.Min(result, dropCap);
        }

        /// <summary>R10. HomeRadius for run creatures = 2 x R (TetherRadius is never set).</summary>
        public static double HomeRadiusFor(double radius) => 2.0 * radius;
    }
}