using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// Marks the fight circle with a ring of wisps, the Open World Threads ring (OutdoorWispRing): placed at Staging in the
/// arena copy, removed when the run closes. Markers are plain Generic wisps (wcid <see cref="OutdoorWispRing.WispWcid"/>:
/// Ethereal, not Attackable, hidden from radar), never adopted by the spawner, set never to rot. Spacing is the Thread ring's
/// tunable (dynamic_dungeons_outdoor_ring_spacing_m).
/// </summary>
internal sealed class LiveGauntletRingHooks : IGauntletRingHooks
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>Probe start height above terrain, as the Thread ring uses.</summary>
    private const float ProbeHeight = 60f;

    /// <summary>One run's placed markers, by landblock. Every field is guarded by the object itself.</summary>
    private sealed class RunMarkers
    {
        public bool Removed;
        public readonly List<(ushort Landblock, WorldObject Wo)> Placed = new List<(ushort, WorldObject)>();
    }

    private readonly ConcurrentDictionary<uint, RunMarkers> markers = new ConcurrentDictionary<uint, RunMarkers>();

    /// <summary>Test seam: the ring spacing in metres.</summary>
    internal Func<double> SpacingM { get; init; } = () => OutdoorRingSettings.FromTunables().SpacingM;

    /// <summary>Test seam: runs an action on the arena copy's landblock <paramref name="landblock"/>. False when it is not loaded.</summary>
    internal Func<ushort, uint, Action, bool> EnqueueOn { get; init; } = DefaultEnqueueOn;

    /// <summary>Test seam: builds an unplaced wisp (null when the weenie is missing).</summary>
    internal Func<WorldObject> CreateWisp { get; init; } = () => WorldObjectFactory.CreateNewWorldObject(OutdoorWispRing.WispWcid);

    /// <summary>Test seam: puts a wisp into the world at a ring point in the copy. False when it could not enter.</summary>
    internal Func<WorldObject, RingPoint, uint, bool> PlaceWisp { get; init; } = DefaultPlaceWisp;

    /// <summary>Test seam: takes a wisp out of the world.</summary>
    internal Action<WorldObject> DestroyWisp { get; init; } = w => w.Destroy();

    /// <summary>The markers placed for <paramref name="runKey"/> and not yet removed.</summary>
    internal int MarkerCount(uint runKey)
    {
        if (!markers.TryGetValue(runKey, out var m))
            return 0;

        lock (m)
            return m.Placed.Count;
    }

    /// <summary>The ring for a site: its fight circle, on the footprint of its Thread row (plus the buffer ring). Pure.</summary>
    internal static RingPlan PlanFor(GauntletSiteDef site, double spacingM)
    {
        var lb = (ushort)(site.Centre.Cell >> 16);
        var circle = new OutdoorCircleDef { Landblock = lb, LandblockHex = $"0x{lb:X4}", X = site.Centre.X, Y = site.Centre.Y, Radius = site.Radius };

        var footprint = new HashSet<ushort> { lb };
        if (site.Outdoor?.Outdoor != null)
        {
            footprint.Add(site.Outdoor.Outdoor.Primary);
            foreach (var f in site.Outdoor.Outdoor.Footprint ?? new List<ushort>())
                footprint.Add(f);
        }

        return OutdoorWispRingPlan.Build(circle, footprint, spacingM);
    }

    public void Place(GauntletRun run)
    {
        if (run == null || run.IsTerminal)
            return;

        var instance = run.Instance;
        var plan = PlanFor(run.Site, SpacingM());
        var queued = 0;
        var store = markers.GetOrAdd(run.RunKey, _ => new RunMarkers());

        foreach (var group in plan.ByLandblock())
        {
            var points = group.Value;
            var ok = EnqueueOn(group.Key, instance, () => PlaceGroup(run, store, instance, points));

            if (ok)
                queued += points.Count;
        }

        log.Info($"[GAUNTLET] {run}: arena ring points={plan.Total} queued={queued}");
    }

    /// <summary>On the landblock's own queue: create, place and record each wisp; a run gone terminal places nothing.</summary>
    private void PlaceGroup(GauntletRun run, RunMarkers store, uint instance, List<RingPoint> points)
    {
        foreach (var point in points)
        {
            bool removed;
            lock (store)
                removed = store.Removed;

            if (run.IsTerminal || removed)
                return;

            WorldObject wo = null;

            try
            {
                wo = CreateWisp();

                if (wo == null)
                    continue;

                // No generator back-reference: the heartbeat would rot it otherwise.
                wo.TimeToRot = -1;

                if (!PlaceWisp(wo, point, instance))
                {
                    if (!wo.IsDestroyed) DestroyWisp(wo);
                    continue;
                }

                bool late;

                lock (store)
                {
                    late = store.Removed;

                    if (!late)
                        store.Placed.Add((point.Landblock, wo));
                }

                // Remove already ran: this marker is not on its list, so it takes itself out (its own landblock thread).
                if (late)
                {
                    DestroyWisp(wo);
                    return;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] {run}: placing a ring wisp at landblock 0x{point.Landblock:X4} threw", ex);
                try { if (wo != null && !wo.IsDestroyed) DestroyWisp(wo); } catch { }
            }
        }
    }

    /// <summary>
    /// Takes the ring out: each landblock's markers are destroyed on that landblock's own queue, and directly only when the
    /// landblock is no longer loaded (its objects are going with it). A marker placed after this call destroys itself.
    /// </summary>
    public void Remove(GauntletRun run)
    {
        if (run == null || !markers.TryRemove(run.RunKey, out var store))
            return;

        List<(ushort Landblock, WorldObject Wo)> placed;

        lock (store)
        {
            store.Removed = true;
            placed = new List<(ushort, WorldObject)>(store.Placed);
            store.Placed.Clear();
        }

        var instance = run.Instance;

        foreach (var group in placed.GroupBy(p => p.Landblock))
        {
            var wisps = group.Select(p => p.Wo).ToList();
            bool queued;

            try
            {
                queued = EnqueueOn(group.Key, instance, () => DestroyAll(run, wisps));
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] {run}: queueing the ring teardown on 0x{group.Key:X4} threw", ex);
                queued = false;
            }

            if (!queued)
                DestroyAll(run, wisps);
        }
    }

    private void DestroyAll(GauntletRun run, List<WorldObject> wisps)
    {
        foreach (var wo in wisps)
        {
            try
            {
                if (!wo.IsDestroyed)
                    DestroyWisp(wo);
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] {run}: removing a ring wisp threw", ex);
            }
        }
    }

    private static bool DefaultEnqueueOn(ushort landblock, uint instance, Action action)
    {
        var lb = LandblockManager.GetLoadedLandblock(new LandblockId((uint)landblock << 16), instance);

        if (lb == null)
            return false;

        lb.EnqueueAction(new ActionEventDelegate(action));
        return true;
    }

    /// <summary>As OutdoorWispRing.PlaceGroup: probe the point to the ground, then enter the world.</summary>
    private static bool DefaultPlaceWisp(WorldObject wo, RingPoint point, uint instance)
    {
        var cellX = (int)(point.X / 24.0);
        var cellY = (int)(point.Y / 24.0);
        var cell = ((uint)point.Landblock << 16) | (uint)(cellX * 8 + cellY + 1);
        var position = new Position(cell, (float)point.X, (float)point.Y, 0f, 0f, 0f, 0f, 1f, instance);

        var terrainZ = position.GetTerrainZ();
        var probe = WorldEventGroundProbe.ProbeDown(position.LandblockId.Raw, position.Instance, position.PositionX, position.PositionY,
            terrainZ, ProbeHeight, WorldEventGroundProbe.BaseSphereRadius, out _);

        if (probe.Landed)
        {
            if ((probe.Cell >> 16) == (position.LandblockId.Raw >> 16))
            {
                position.LandblockId = new LandblockId(probe.Cell);
                position.PositionX = probe.Rest.X;
                position.PositionY = probe.Rest.Y;
            }

            position.PositionZ = probe.Rest.Z;
        }
        else
        {
            position.AdjustMapCoords();
        }

        wo.Location = position;
        return wo.EnterWorld();
    }
}
