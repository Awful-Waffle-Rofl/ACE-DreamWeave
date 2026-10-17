using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// goals.json type "Hold" - protect the targets. The run places the goal's protect.wcid targets (wards)
    /// around the anchor; a share of every wave goes straight for them. The objective:
    ///
    ///   * SUCCEEDS once holdSeconds have passed since the run went Active with at least minSurvivors
    ///     targets still standing (minSurvivors is clamped to the number actually placed);
    ///   * FAILS (<see cref="WorldEventOutcome.FailedObjectiveLost"/>) the moment fewer than that are left;
    ///   * ABORTS (<see cref="WorldEventOutcome.AbortedError"/>) when no target was placed at all within
    ///     <see cref="PlacementGraceSeconds"/> of going Active - a run with nothing to protect is a content or
    ///     placement fault, not a win and not the players' loss.
    ///
    /// The success check sits ahead of the run timers on the same tick (WorldEvent.TickActive), and
    /// WorldEventManager.TryStart refuses holdSeconds above the run's maximum duration, so a held run always
    /// resolves as Success rather than FailedTimeout.
    /// </summary>
    public sealed class HoldObjective : IWorldEventObjective
    {
        /// <summary>Seconds after going Active before "nothing was placed" becomes an abort.</summary>
        public const double PlacementGraceSeconds = 5.0;

        private readonly GoalDef goal;
        private readonly Func<IReadOnlyCollection<uint>> protectedGuids;
        private readonly Func<IReadOnlyCollection<uint>> liveProtectedGuids;
        private readonly Func<double> activeAt;
        private readonly WorldEventParticipation ledger;

        private double now;

        private string lastKillerName;
        private string topDamagerName;

        /// <param name="family">The run's composed monster family display name, for the {family}/{Family} progress tokens. Null reads as the generic fallback.</param>
        public HoldObjective(GoalDef goal, Func<IReadOnlyCollection<uint>> protectedGuids,
            Func<IReadOnlyCollection<uint>> liveProtectedGuids, Func<double> activeAt, WorldEventParticipation ledger,
            Func<string> family = null)
        {
            this.goal = goal;
            this.protectedGuids = protectedGuids ?? (() => Array.Empty<uint>());
            this.liveProtectedGuids = liveProtectedGuids ?? (() => Array.Empty<uint>());
            this.activeAt = activeAt ?? (() => 0);
            this.ledger = ledger;
            this.family = family ?? (() => null);
        }

        private readonly Func<string> family;

        private ProtectDef Protect => goal?.Protect;

        private int HoldSeconds => Math.Max(0, goal?.HoldSeconds ?? 0);

        private string Noun => Protect?.EffectiveNoun ?? ProtectDef.DefaultNoun;

        private string NounPlural => Protect?.EffectiveNounPlural ?? ProtectDef.DefaultNounPlural;

        /// <summary>How many targets were placed (ever adopted).</summary>
        public int Placed => (protectedGuids() ?? Array.Empty<uint>()).Count;

        /// <summary>How many placed targets are still standing.</summary>
        public int Alive
        {
            get
            {
                var placed = protectedGuids() ?? Array.Empty<uint>();
                var live = liveProtectedGuids() ?? Array.Empty<uint>();

                return live.Count(placed.Contains);
            }
        }

        /// <summary>minSurvivors, clamped to [1, Placed]. 0 while nothing has been placed.</summary>
        public int RequiredSurvivors => RequiredFor(Protect?.MinSurvivors ?? ProtectDef.DefaultMinSurvivors, Placed);

        /// <summary>The pure clamp behind <see cref="RequiredSurvivors"/>.</summary>
        public static int RequiredFor(int minSurvivors, int placed)
        {
            if (placed <= 0)
                return 0;

            return Math.Clamp(minSurvivors, 1, placed);
        }

        /// <summary>Seconds since the run went Active, as of the last <see cref="Tick"/>; 0 before that.</summary>
        private double Elapsed
        {
            get
            {
                var start = activeAt();

                return start > 0 && now > 0 ? Math.Max(0, now - start) : 0;
            }
        }

        /// <summary>Seconds of the hold still to go, never below 0.</summary>
        public int RemainingSeconds => (int)Math.Ceiling(Math.Max(0, HoldSeconds - Elapsed));

        public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            OnDeathCore(creature?.Guid.Full ?? 0, creature?.GetProperty(PropertyInt.WorldEventId) != null,
                WorldEventMvpResolver.ResolveName(lastDamager), WorldEventMvpResolver.ResolveName(topDamager));
        }

        /// <summary>
        /// Testable core (D6). A protected target's own death changes nothing here - <see cref="Alive"/> reads
        /// the spawner's live set, which the death has already updated - and its killer is a monster, so it is
        /// never the MVP. Any other event creature's death records the killing blow and top damager for the
        /// KillingBlow MVP rules.
        /// </summary>
        public void OnDeathCore(uint guid, bool wasEventCreature, string lastDamagerName, string topDamagerName)
        {
            if (!wasEventCreature)
                return;

            if ((protectedGuids() ?? Array.Empty<uint>()).Contains(guid))
                return;

            lastKillerName = lastDamagerName;
            this.topDamagerName = topDamagerName;
        }

        public void Tick(double now)
        {
            this.now = now;
        }

        public bool IsComplete
        {
            get
            {
                var placed = Placed;

                if (placed == 0 || activeAt() <= 0 || now <= 0)
                    return false;

                return Alive >= RequiredFor(Protect?.MinSurvivors ?? ProtectDef.DefaultMinSurvivors, placed)
                    && Elapsed >= HoldSeconds;
            }
        }

        public WorldEventOutcome? FailureOutcome
        {
            get
            {
                if (activeAt() <= 0 || now <= 0)
                    return null;

                var placed = Placed;

                if (placed == 0)
                    return Elapsed >= PlacementGraceSeconds ? WorldEventOutcome.AbortedError : (WorldEventOutcome?)null;

                return Alive < RequiredFor(Protect?.MinSurvivors ?? ProtectDef.DefaultMinSurvivors, placed)
                    ? WorldEventOutcome.FailedObjectiveLost
                    : (WorldEventOutcome?)null;
            }
        }

        /// <summary>
        /// Tokens: {alive}, {total}, {lost}, {noun}, {nounPlural}, {remaining} (the hold time left, m:ss).
        /// ASCII only, like every player-facing string.
        /// </summary>
        public string ProgressText
        {
            get
            {
                var total = Placed;

                if (total == 0)
                    return $"no {NounPlural} have been placed yet.";

                var alive = Alive;

                var template = goal?.ProgressTemplate ?? DefaultProgressTemplate;

                // {lost} and {remaining} first; the shared protect-line filler then does the family, the
                // nouns and the counts, so progress text and caravan lines read the tokens identically.
                template = template
                    .Replace("{lost}", (total - alive).ToString())
                    .Replace("{remaining}", FormatRemaining(RemainingSeconds));

                return WorldEventHoldRules.FillLine(template, family(), Noun, NounPlural, null, alive, total);
            }
        }

        /// <summary>The progress line a Hold goal gets when goals.json declares no progressTemplate.</summary>
        public const string DefaultProgressTemplate = "{alive} of {total} {nounPlural} stand. Hold for {remaining}.";

        /// <summary>m:ss, e.g. 6:05.</summary>
        public static string FormatRemaining(int seconds)
        {
            seconds = Math.Max(0, seconds);

            return $"{seconds / 60}:{seconds % 60:D2}";
        }

        public WorldEventMvp Mvp()
        {
            switch (goal?.RuleKind)
            {
                case MvpRule.MostKills:
                    return WorldEventMvpResolver.MostKills(ledger);

                case MvpRule.KillingBlow:
                    return WorldEventMvpResolver.KillingBlow(lastKillerName);

                case MvpRule.KillingBlowAndTopDamage:
                    return WorldEventMvpResolver.KillingBlowAndTopDamage(lastKillerName, topDamagerName);

                default:
                    return WorldEventMvp.None;
            }
        }
    }
}
