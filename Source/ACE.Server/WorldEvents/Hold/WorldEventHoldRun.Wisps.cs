using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>How the run creates and removes one health wisp. The spawner implements it; tests fake it.</summary>
    public interface IHoldHealthWispHost
    {
        /// <summary>
        /// World thread: constructs a wisp for <paramref name="spot"/> (an offset from the run's anchor) and queues its placement
        /// on the anchor landblock. <paramref name="placed"/> is called once from that landblock's queue with the object, which is
        /// already destroyed when placement failed.
        /// </summary>
        void Create(DecorDef spot, Action<WorldObject> placed);

        /// <summary>Queues the wisp's destroy on its own landblock.</summary>
        void Destroy(WorldObject wisp);
    }

    /// <summary>
    /// World Events Hold: the knights' health wisps (protect.healthWisps). The spawner registers each knight's wisp spots when it
    /// places the knight; <see cref="TickHealthWisps"/> (once a second, world thread) reads the knight's health the way the other
    /// ticks do and makes the wisps that exist match <see cref="WorldEventHoldRules.HealthWispsShown"/>.
    /// </summary>
    public sealed partial class WorldEventHoldRun
    {
        private sealed class WispSlot
        {
            public DecorDef Spot;
            public int Index;
            public WorldObject Wisp;
            public bool Pending;
            public bool Failed;

            public bool Present => Pending || (Wisp != null && !Wisp.IsDestroyed);
        }

        private sealed class KnightWisps
        {
            public Creature Knight;
            public WispSlot[] Slots;
            public int Shown;
        }

        // The slots are written from landblock threads (the placed callback), so every touch is under wispSync.
        private readonly object wispSync = new object();
        private readonly Dictionary<uint, KnightWisps> wispKnights = new Dictionary<uint, KnightWisps>();
        private IHoldHealthWispHost wispHost;

        /// <summary>World thread, with the spawner: how wisps are created and destroyed. Null (the default) leaves the feature off.</summary>
        public void AttachHealthWispHost(IHoldHealthWispHost host) => Volatile.Write(ref wispHost, host);

        /// <summary>World thread, as a knight is built: its wisp spots (offsets from the run's anchor), all shown at the start.</summary>
        public void RegisterHealthWisps(Creature knight, IReadOnlyList<DecorDef> spots)
        {
            if (knight == null || spots == null || spots.Count == 0)
                return;

            lock (wispSync)
            {
                wispKnights[knight.Guid.Full] = new KnightWisps
                {
                    Knight = knight,
                    Shown = spots.Count,
                    Slots = spots.Select((s, i) => new WispSlot { Spot = s, Index = i }).ToArray()
                };
            }
        }

        /// <summary>The wisps of knight <paramref name="knightGuid"/> that exist or are being placed. Any thread.</summary>
        public int HealthWispsPresent(uint knightGuid)
        {
            lock (wispSync)
                return wispKnights.TryGetValue(knightGuid, out var k) ? k.Slots.Count(s => s.Present) : 0;
        }

        /// <summary>
        /// World thread, once a second: per registered knight, the wisps shown follow its health (a dead or destroyed knight shows
        /// none; one the spawner has not published yet is left alone). At most one batch per knight per tick: the highest-index
        /// wisps past the shown count are destroyed, else the missing ones below it are created. A wisp whose placement failed is
        /// not retried.
        /// </summary>
        private void TickHealthWisps()
        {
            var host = Volatile.Read(ref wispHost);

            if (host == null)
                return;

            List<KnightWisps> all;

            lock (wispSync)
                all = wispKnights.Values.ToList();

            if (all.Count == 0)
                return;

            var published = new HashSet<uint>(Volatile.Read(ref knights).Where(k => k != null).Select(k => k.Guid.Full));

            foreach (var state in all)
            {
                var knight = state.Knight;
                int desired;

                if (knight.IsDead || knight.IsDestroyed)
                    desired = 0;
                else if (!published.Contains(knight.Guid.Full))
                    continue;
                else
                    desired = WorldEventHoldRules.HealthWispsShown(knight.Health.Current, knight.Health.MaxValue, state.Slots.Length, state.Shown);

                var toDestroy = new List<WorldObject>();
                var toCreate = new List<WispSlot>();

                lock (wispSync)
                {
                    state.Shown = desired;

                    for (var i = state.Slots.Length - 1; i >= desired; i--)
                    {
                        var slot = state.Slots[i];

                        if (slot.Wisp != null && !slot.Wisp.IsDestroyed)
                            toDestroy.Add(slot.Wisp);
                    }

                    if (toDestroy.Count == 0)
                    {
                        for (var i = 0; i < desired; i++)
                        {
                            var slot = state.Slots[i];

                            if (!slot.Present && !slot.Failed)
                            {
                                slot.Pending = true;
                                toCreate.Add(slot);
                            }
                        }
                    }
                }

                foreach (var wisp in toDestroy)
                {
                    try
                    {
                        host.Destroy(wisp);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={RunId} hold health wisp destroy threw (knight 0x{knight.Guid.Full:X8})", ex);
                    }
                }

                foreach (var slot in toCreate)
                {
                    var s = slot;

                    try
                    {
                        host.Create(s.Spot, wisp =>
                        {
                            var failed = wisp == null || wisp.IsDestroyed;

                            lock (wispSync)
                            {
                                s.Pending = false;
                                s.Wisp = wisp;
                                s.Failed = s.Failed || failed;
                            }

                            if (failed)
                                log.Warn($"[WORLDEVENT] run={RunId} hold health wisp slot {s.Index} of knight 0x{knight.Guid.Full:X8} failed to place; not retried");
                        });
                    }
                    catch (Exception ex)
                    {
                        lock (wispSync)
                        {
                            s.Pending = false;
                            s.Failed = true;
                        }

                        log.Error($"[WORLDEVENT] run={RunId} hold health wisp slot {s.Index} of knight 0x{knight.Guid.Full:X8} create threw; failed, not retried", ex);
                    }
                }
            }
        }
    }
}
