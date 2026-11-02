using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One DoT tick staged on a run creature (ThreadContributionHook.BeginDotTick): its damagers and their raw shares, in one
    /// order, and the managed thread that staged it. The staging thread is the only one the tick applies to: a hit recorded
    /// on any other thread while it is staged is an ordinary hit and is credited as one.
    /// </summary>
    internal sealed class ThreadDotTick
    {
        public ThreadDotTick(IReadOnlyList<WorldObject> damagers, IReadOnlyList<float> rawAmounts, int threadId)
        {
            Damagers = damagers;
            RawAmounts = rawAmounts;
            ThreadId = threadId;
        }

        public IReadOnlyList<WorldObject> Damagers { get; }
        public IReadOnlyList<float> RawAmounts { get; }
        public int ThreadId { get; }

        /// <summary>True when the calling thread is the one that staged this tick.</summary>
        public bool IsCurrentThread => ThreadId == Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Feeds the group-run contribution ledger (ThreadDungeonRun_Contribution.cs) from
    /// <see cref="ACE.Server.Entity.DamageHistory.Add(WorldObject, ACE.Entity.Enum.DamageType, uint)"/>, the site every damaging
    /// write to a Creature's or Player's health records through, AFTER the write and with the applied amount (World Events
    /// and the battleground stats hang off the same line for the same reason). One site sees both directions:
    ///
    ///   * DEALT - the victim is a run creature (Creature.P_DungeonRun) and the attacker resolves to a player: the player
    ///     itself, or a CombatPet's owner. Monster-on-monster, a trap or hotspot, and a creature's own self-inflicted
    ///     entries resolve to nobody and are ignored. Damage to another player never reaches this branch, because a
    ///     Player never carries P_DungeonRun.
    ///   * TAKEN - the victim is a Player standing in a registered run's instance (the run id IS the instance id), from
    ///     any source.
    ///
    /// A DoT tick on a run creature is the one DEALT exception: its DamageHistory credits are recorded before the write and
    /// before the monster's ward, so they are skipped and the tick is credited from the write itself (BeginDotTick,
    /// OnDotTickApplied).
    ///
    /// The one damage write that does not record through DamageHistory and that a roster member can suffer inside a copy,
    /// the Open World Threads perimeter (OutdoorPerimeterLive), calls <see cref="ThreadDungeonRun.RecordDamageTaken"/>
    /// itself.
    ///
    /// HOT PATH. A field read and a null check for every creature victim outside a Thread, and a type test plus a
    /// landblock-instance read for every player victim; the registry lookup is reached only for an instanced landblock.
    /// The run's own gates (group, roster, open ledger) take no lock.
    /// </summary>
    public static class ThreadContributionHook
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Test seam: instance id -> registered run, or null. Production is the manager's lock-free registry read.</summary>
        internal static Func<uint, ThreadDungeonRun> RunForInstance = ThreadDungeonManager.GetRegisteredRun;

        /// <summary>A health hit of <paramref name="amount"/> (already applied, already clamped) was recorded against <paramref name="victim"/>.</summary>
        public static void OnDamageRecorded(Creature victim, WorldObject attacker, uint amount)
        {
            if (amount == 0 || victim == null)
                return;

            try
            {
                // Read once into a local, the same discipline WorldEventCombatCreditHook keeps for P_WorldEvent.
                var run = victim.P_DungeonRun;

                if (run != null)
                {
                    // A DoT tick's credits are recorded BEFORE its vital write, and before the monster's ward has had
                    // its say, so they are not what the creature lost. Such a tick is credited from the write itself
                    // instead (OnDotTickApplied). Only on the staging thread: a hit landing from another thread meanwhile
                    // is not part of the tick.
                    var pending = victim.P_PendingThreadDotTick;
                    if (pending != null && pending.IsCurrentThread)
                        return;

                    var credited = CreditedPlayerGuid(attacker);
                    if (credited != 0)
                        run.RecordDamageDealt(credited, amount);
                    return;
                }

                if (victim is Player hurt)
                {
                    var instance = hurt.CurrentLandblock?.Instance ?? 0;
                    if (instance == 0)
                        return;

                    // A DoT tick records its credits BEFORE Player.TakeDamageOverTime, which then discards the tick for
                    // these two states (the same refusal WorldEventCombatCreditHook.VictimStateRefusesCredit makes).
                    if (hurt.Invincible || hurt.UnderLifestoneProtection)
                        return;

                    OnPlayerDamaged(hurt.Guid.Full, instance, amount);
                }
            }
            catch (Exception ex)
            {
                // A damage hook must never be able to abort the damage that triggered it.
                log.Error("[DYNDUNGEON] contribution hook threw", ex);
            }
        }

        /// <summary>
        /// Stages a DoT tick on a run creature, called by EnchantmentManager.ApplyDamageTick BEFORE it records the tick's
        /// credits in DamageHistory. While a tick is staged, <see cref="OnDamageRecorded"/> ignores DEALT for that creature,
        /// and Creature.TakeDamage credits the tick from its vital write (<see cref="OnDotTickApplied"/>), after the
        /// monster's ward and before the death check - so the figure is what the creature lost and the clearing tick is
        /// still counted. A no-op (returns false, stages nothing) for a Player victim and for any creature without a run:
        /// combat outside a Thread is unchanged. Always pair with <see cref="EndDotTick"/> in a finally.
        /// </summary>
        /// <param name="damagers">Each damager, in the same order as <paramref name="rawAmounts"/>.</param>
        internal static bool BeginDotTick(Creature victim, IReadOnlyList<WorldObject> damagers, IReadOnlyList<float> rawAmounts)
        {
            if (victim == null || victim is Player || victim.P_DungeonRun?.IsContributionOpen != true
                || damagers == null || rawAmounts == null || damagers.Count != rawAmounts.Count)
                return false;

            victim.P_PendingThreadDotTick = new ThreadDotTick(damagers, rawAmounts, Environment.CurrentManagedThreadId);
            return true;
        }

        /// <summary>
        /// Clears this thread's staged tick: the write never ran (a dead or refused victim), or it ran and was already
        /// consumed. A tick staged by another thread is left alone.
        /// </summary>
        internal static void EndDotTick(Creature victim)
        {
            var pending = victim?.P_PendingThreadDotTick;
            if (pending != null && pending.IsCurrentThread)
                victim.P_PendingThreadDotTick = null;
        }

        /// <summary>
        /// The staged tick's vital write removed <paramref name="applied"/> health from <paramref name="victim"/>: split it over
        /// the damagers in proportion to their raw contributions (DotTickCredits.Split, the same split ApplyDamageTick uses
        /// for DamageHistory) and credit each one's player. Consumes the staged tick, so it credits at most once, and only on
        /// the thread that staged it.
        /// </summary>
        internal static void OnDotTickApplied(Creature victim, int applied)
        {
            try
            {
                var tick = victim?.P_PendingThreadDotTick;
                if (tick == null || !tick.IsCurrentThread)
                    return;

                victim.P_PendingThreadDotTick = null;

                var run = victim.P_DungeonRun;
                if (run == null || applied <= 0)
                    return;

                var credits = ACE.Server.Entity.DotTickCredits.Split(tick.RawAmounts, (uint)applied);

                for (var i = 0; i < credits.Length; i++)
                {
                    var credited = CreditedPlayerGuid(tick.Damagers[i]);
                    if (credited != 0)
                        run.RecordDamageDealt(credited, credits[i]);
                }
            }
            catch (Exception ex)
            {
                log.Error("[DYNDUNGEON] contribution DoT credit threw", ex);
            }
        }

        /// <summary>The TAKEN half without a Player: a roster member <paramref name="playerGuid"/> lost health in instance <paramref name="instance"/>.</summary>
        internal static void OnPlayerDamaged(uint playerGuid, uint instance, uint amount)
        {
            if (amount == 0 || instance == 0)
                return;

            RunForInstance(instance)?.RecordDamageTaken(playerGuid, amount);
        }

        /// <summary>
        /// The player credited with <paramref name="attacker"/>'s damage: the attacker itself when it is a Player, a
        /// CombatPet's owner (CombatPet.P_PetOwner), else 0. The same resolution the World Events credit and the death walk
        /// use, so a pet's damage belongs to the same person everywhere.
        /// </summary>
        internal static uint CreditedPlayerGuid(WorldObject attacker)
        {
            if (attacker is Player player)
                return player.Guid.Full;

            if (attacker is CombatPet pet)
                return pet.P_PetOwner?.Guid.Full ?? 0;

            return 0;
        }
    }
}
