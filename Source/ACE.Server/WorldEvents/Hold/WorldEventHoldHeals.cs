using System;

using log4net;

using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>
    /// The one way anything heals a Hold knight. Every source - a player's kit or spell, a shrine, a corrupted kill, a rescue,
    /// clear regeneration, the between-waves heal - is queued here and applied by Creature.ApplyHoldKnightHeal ON THE KNIGHT'S
    /// OWN LANDBLOCK, so the knight's health, its after-hit window, its per-wave budget and its player-heal bucket are only ever
    /// written by the thread that owns it.
    ///
    /// This replaces the contributor's player-heal path (DreamWeave #17, Creature_WorldEventHold.cs:793-824), which converted
    /// and wrote the knight's health and budget on the HEALER's thread while every run heal was already marshalled: two writers
    /// racing on one knight whenever the healer and the knight were ticked by different landblock groups.
    ///
    /// THREADING: <see cref="Queue"/> may be called from any thread. It never touches the knight's state itself; the
    /// <c>after</c> callback runs on the knight's landblock thread.
    /// </summary>
    public static class WorldEventHoldHeals
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// How a heal action reaches the knight's landblock. The live default enqueues on knight.CurrentLandblock (false when
        /// the knight has none). A seam for tests, which capture the action instead of running it.
        /// </summary>
        internal static Func<Creature, Action, bool> Enqueue = EnqueueOnLandblock;

        private static bool EnqueueOnLandblock(Creature knight, Action action)
        {
            var landblock = knight?.CurrentLandblock;

            if (landblock == null)
                return false;

            landblock.EnqueueAction(new ActionEventDelegate(action));
            return true;
        }

        /// <summary>The result of one queued heal, as the <c>after</c> callback sees it (on the knight's landblock thread).</summary>
        public readonly struct Result
        {
            public Result(int healed, HoldHealOutcome outcome)
            {
                Healed = healed;
                Outcome = outcome;
            }

            /// <summary>Health actually restored (0 or more).</summary>
            public int Healed { get; }

            public HoldHealOutcome Outcome { get; }
        }

        /// <summary>
        /// Queues a heal of <paramref name="amount"/> on <paramref name="knight"/>'s own landblock (a share of max health, or raw
        /// player heal points for <see cref="HoldHealSource.Player"/>) and calls <paramref name="after"/> there with what it came
        /// to. Any thread. False when nothing was queued (no knight, no landblock, nothing asked for), and then
        /// <paramref name="after"/> never runs.
        /// </summary>
        public static bool Queue(Creature knight, double amount, HoldHealSource source, Action<Result> after = null)
        {
            if (knight == null || !(amount > 0) || !double.IsFinite(amount))
                return false;

            var k = knight;

            return Enqueue(k, () =>
            {
                var result = new Result(0, HoldHealOutcome.None);

                try
                {
                    if (!k.IsDead && !k.IsDestroyed)
                    {
                        var healed = k.ApplyHoldKnightHeal(amount, source, ACE.Common.Time.GetUnixTime(), out var outcome);
                        result = new Result(healed, outcome);

                        if (healed > 0)
                            log.Debug($"[WORLDEVENT] run={k.WorldEventHold?.RunId} knight heal {source} {k.Name} +{healed} ({k.Health.Current}/{k.Health.MaxValue})");
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={k.WorldEventHold?.RunId} knight heal ({source}) on {k.Name} threw", ex);
                }

                try
                {
                    after?.Invoke(result);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={k.WorldEventHold?.RunId} knight heal ({source}) callback threw", ex);
                }
            });
        }

        /// <summary>
        /// A player's heal (kit or spell) of <paramref name="raw"/> points on <paramref name="knight"/>: the conversion, the
        /// full-health refusal, the budget and the write all happen in the queued action on the knight's landblock, which then
        /// hands the result to <paramref name="reply"/>. Any thread; never writes the knight here. False when nothing was
        /// queued (not a knight, or no landblock).
        /// </summary>
        public static bool QueuePlayerHeal(Creature knight, double raw, Action<Result> reply)
        {
            if (knight?.WorldEventHold?.Kind != HoldUnitKind.Knight)
                return false;

            return Queue(knight, raw, HoldHealSource.Player, reply);
        }
    }
}
