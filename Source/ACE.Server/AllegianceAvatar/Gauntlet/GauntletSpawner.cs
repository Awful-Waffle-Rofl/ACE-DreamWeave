using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Physics;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// Shapes, places and wakes Allegiance Gauntlet creatures, in the TrialSpawner shape. A Gauntlet creature is a Thread
/// creature of the wave's group: it goes through <see cref="ThreadDungeonSpawner.ShapeCreature"/> with Run = null,
/// Rewards = true and the wave group's effort (live XP and luminance, live health, damage rating and creature abilities),
/// and then:
/// <list type="bullet">
/// <item>its loot is stripped (<see cref="Creature.StripLootRewards"/>) and its kill tasks removed;</item>
/// <item>its XpOverride is scaled by allegiance_gauntlet_kill_xp_scale (0 turns kill XP off; luminance is left as shaped);</item>
/// <item>it is stamped with P_Gauntlet, its wave, and an OutdoorCreaturePerimeter on the run's fight circle (never interior),
/// with HomeRadius = 2 x R as the Thread binding sets it;</item>
/// <item>its max health is clamped to allegiance_gauntlet_max_creature_health AFTER shaping, through WorldEventSpawner.ApplyHealth
/// (spike 1B: at level 1000 and up with a big group the boss saturates at int.MaxValue before the clamp).</item>
/// </list>
///
/// Persistence: the arena is an ephemeral copy (Landblock.SaveDB returns before iterating any object when IsEphemeral). A
/// Gauntlet creature carries no ThreadDungeonRunId and no generator, so on a PERSISTENT landblock nothing would stop it
/// reaching the shard: <see cref="Spawn"/> refuses any landblock that is not ephemeral before it creates anything.
/// </summary>
internal static class GauntletSpawner
{
    /// <summary>The chat line a strike from outside the fight circle gets (the Thread perimeter's line, in Gauntlet words).</summary>
    public const string RefusedStrikeLine = "You must stand inside the arena circle to strike.";

    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>
    /// The live group shape: no run, rewards on, the wave group's effort. StripCasterVitalSpells (owner ruling 2026-11-22): a Gauntlet
    /// creature loses caster-vital-scaled life projectiles (Martyr's Hecatomb and kin) like a Thread enemy. Both ShapeCreature
    /// paths below (Prepare and the pack-max observation) take their options from here.
    /// </summary>
    internal static CreatureShapeOptions ShapeOptions(GroupScaling group, string tag) => new CreatureShapeOptions
    {
        Run = null,
        Rewards = true,
        GroupEffort = (group ?? GroupScaling.Solo).Effort,
        LogTag = tag,
        StripCasterVitalSpells = true,
    };

    /// <summary>
    /// The off-world half of a Gauntlet spawn, in TrialSpawner.Prepare's order (shape, loot strip, kill-task strip), then the
    /// XP scale, the run stamps, the perimeter and the health clamp. Touches no Location, landblock or EnterWorld. Reads the
    /// PropertyManager keys PrepareForPlacement reads.
    /// </summary>
    internal static void Prepare(Creature c, GauntletWavePlan wave, DungeonSpawnPlanEntry e, uint observedMaxNonBoss, IGauntletCreatureOwner owner,
        GauntletTuning tuning, OutdoorPerimeterSettings perimeterSettings, string tag)
    {
        if (c == null) throw new ArgumentNullException(nameof(c));
        if (wave?.Plan == null) throw new ArgumentNullException(nameof(wave));
        if (e == null) throw new ArgumentNullException(nameof(e));
        if (owner == null) throw new ArgumentNullException(nameof(owner));

        var t = GauntletTuning.Sanitized(tuning ?? GauntletTuning.Defaults);

        ThreadDungeonSpawner.ShapeCreature(c, wave.Plan, e, observedMaxNonBoss, ShapeOptions(wave.Group, tag));

        // Rewards = true wrote the plan's loot profile and salvage affinities; a Gauntlet creature pays XP and luminance only.
        c.StripLootRewards();

        // Gauntlet kills must not advance retail kill tasks (the Trials and speed-course strip).
        c.RemoveProperty(PropertyString.KillQuest);
        c.RemoveProperty(PropertyString.KillQuest2);
        c.RemoveProperty(PropertyString.KillQuest3);

        ScaleXp(c, t.KillXpScale);

        // Membership: no kill-fill vessel, no mule token, no corpse, and the death hook (Creature_Gauntlet.cs).
        c.P_Gauntlet = owner;
        c.P_GauntletWave = owner.CurrentWave;

        // The fight circle, never interior: keeps it inside, refuses players past the edge and strikes from outside.
        c.P_OutdoorPerimeter = new OutdoorCreaturePerimeter(owner.CentreGx, owner.CentreGy, owner.Radius, interior: false,
            perimeterSettings ?? OutdoorPerimeterSettings.FromTunables(), RefusedStrikeLine);
        c.HomeRadius = OutdoorPerimeterRules.HomeRadiusFor(owner.Radius);

        if (ClampHealth(c, t.MaxCreatureHealth))
            log.Info($"[GAUNTLET] {tag} wcid {e.Wcid} ({e.Role}) health clamped to {c.Health.MaxValue} (max_creature_health {t.MaxCreatureHealth})");

        c.P_GauntletSpawnMaxHealth = c.Health.MaxValue;
    }

    /// <summary>
    /// XpOverride x scale, rounded, saturating at int.MaxValue; 0 (or a non-positive scale) writes 0. A creature with no
    /// XpOverride is left alone. Returns the value written, or null.
    /// </summary>
    internal static int? ScaleXp(Creature c, double scale)
    {
        if (c?.XpOverride is not int xp)
            return null;

        var s = double.IsFinite(scale) ? Math.Max(0.0, scale) : 1.0;
        var scaled = Math.Round(xp * s, MidpointRounding.AwayFromZero);
        var value = (int)Math.Clamp(scaled, int.MinValue, int.MaxValue);

        c.XpOverride = value;
        return value;
    }

    /// <summary>
    /// Holds max health at or under <paramref name="max"/> (clamped to [1, int.MaxValue]) through WorldEventSpawner.ApplyHealth,
    /// with Current refilled to the new max. MaxValue is computed in float, so one write can land a step above the target
    /// (128 at 2e9); a second write aims that far lower. True when it clamped.
    /// </summary>
    internal static bool ClampHealth(Creature c, long max)
    {
        var cap = (int)Math.Clamp(max, 1L, int.MaxValue);

        if (c.Health.MaxValue <= (uint)cap)
            return false;

        WorldEventSpawner.ApplyHealth(c, cap);

        if (c.Health.MaxValue > (uint)cap)
        {
            var over = (long)c.Health.MaxValue - cap;
            WorldEventSpawner.ApplyHealth(c, (int)Math.Max(1L, cap - over));
        }

        return true;
    }

    /// <summary>
    /// The boss health floor's pack term, over every distinct non-boss entry of the WHOLE plan (as a Thread run reaches it by
    /// placing the pack before its boss; TrialSpawner.ObservedMaxNonBoss's rule): each is created, shaped with the wave's
    /// group exactly as <see cref="Prepare"/> shapes it up to the clamp, measured and destroyed. Entries that would shape
    /// identically (same wcid, role, uplift, stamp and stretch) are measured once. A null create or a throwing create or
    /// shaping propagates; whatever was created is destroyed.
    /// </summary>
    internal static uint ObservedMaxNonBoss(GauntletWavePlan wave, Func<uint, Creature> create, string tag)
    {
        if (wave?.Plan == null) throw new ArgumentNullException(nameof(wave));
        if (create == null) throw new ArgumentNullException(nameof(create));

        var max = 0u;
        var seen = new HashSet<(uint, DungeonRole, int, int, bool)>();

        foreach (var e in wave.Plan.Entries)
        {
            if (e == null || e.Role == DungeonRole.Boss || !seen.Add((e.Wcid, e.Role, e.UpliftLevel, e.StampLevel, e.Stretched)))
                continue;

            Creature c = null;

            try
            {
                c = create(e.Wcid) ?? throw new InvalidOperationException($"[GAUNTLET] {tag} observing the pack max: wcid {e.Wcid} ({e.Role}) could not be created");

                ThreadDungeonSpawner.ShapeCreature(c, wave.Plan, e, 0, ShapeOptions(wave.Group, tag));

                if (c.Health.MaxValue > max)
                    max = c.Health.MaxValue;
            }
            finally
            {
                if (c != null)
                {
                    try { c.Destroy(); } catch { }
                }
            }
        }

        return max;
    }

    /// <summary>
    /// Creates, shapes and places one planned spawn at its portal point in <paramref name="instance"/>, then wakes it at the
    /// nearest member. Returns the creature that entered the world, or null (every refusal after the create destroys what it
    /// created). Must be called on <paramref name="lb"/>'s action queue: it enters the world synchronously and does not
    /// enqueue. <paramref name="create"/> is a test seam (null: WorldObjectFactory.CreateNewWorldObject).
    /// </summary>
    internal static Creature Spawn(GauntletWavePlan wave, PortalWaveSpawn spawn, uint observedMaxNonBoss, IGauntletCreatureOwner owner, Landblock lb, uint instance,
        GauntletTuning tuning, OutdoorPerimeterSettings perimeterSettings, string tag, Func<uint, WorldObject> create = null)
    {
        var e = spawn.Entry;

        if (wave?.Plan == null || e?.Point == null || lb == null || owner == null)
            return null;

        // Fail closed: off an ephemeral copy nothing stops the shard save (see the class summary). Before any create.
        if (!lb.IsEphemeral)
        {
            log.Error($"[GAUNTLET] {tag} landblock 0x{lb.Id.Landblock:X4} instance {lb.Instance} is not ephemeral; nothing spawned");
            return null;
        }

        WorldObject wo;

        try
        {
            wo = (create ?? WorldObjectFactory.CreateNewWorldObject)(e.Wcid);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] {tag} could not create wcid {e.Wcid}", ex);
            return null;
        }

        if (wo is not Creature creature)
        {
            log.Warn($"[GAUNTLET] {tag} wcid {e.Wcid} is not a Creature; skipped");
            wo?.Destroy();
            return null;
        }

        if (!DungeonPopulationBuilder.IsKillable(creature.Attackable, creature.PlayerKillerStatus))
        {
            log.Error($"[GAUNTLET] {tag} wcid {e.Wcid} ({e.Role}) is not attackable or is an NPC; not placed");
            creature.Destroy();
            return null;
        }

        var p = e.Point;
        creature.Location = new Position(p.Cell, p.X, p.Y, p.Z, p.QX, p.QY, p.QZ, p.QW, instance);

        if (creature.Location.LandblockId.Landblock != lb.Id.Landblock)
        {
            log.Warn($"[GAUNTLET] {tag} point {creature.Location.ToLOCString()} is not on the arena landblock 0x{lb.Id.Landblock:X4}; dropped");
            creature.Destroy();
            return null;
        }

        try
        {
            Prepare(creature, wave, e, observedMaxNonBoss, owner, tuning, perimeterSettings, tag);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] {tag} shaping threw for wcid {e.Wcid}", ex);
            try { creature.Destroy(); } catch { }
            return null;
        }

        try
        {
            if (!creature.EnterWorld())
            {
                log.Warn($"[GAUNTLET] {tag} EnterWorld refused wcid {e.Wcid} at {creature.Location.ToLOCString()}");
                creature.P_Gauntlet = null;
                creature.Destroy();
                return null;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] {tag} EnterWorld threw for wcid {e.Wcid}", ex);
            creature.P_Gauntlet = null;
            try { creature.Destroy(); } catch { }
            return null;
        }

        // Engage on arrival; a miss stays idle for the wave monitor tick (EngageEnemy again).
        EngageEnemy(creature, owner, out _);

        return creature;
    }

    // ---- engage, on the creature's own landblock thread -------------------------------------------------------------

    /// <summary>
    /// The Defense wake pattern (ThreadEncounterPortalWaves.EngageEnemy) over the run's members: an idle, standing Gauntlet
    /// creature of a live run is woken at the nearest member. False, waking nothing, when the run is not live, or the creature
    /// is not standing, has no physics object or is not idle. True with <paramref name="wake"/> the attempt's result otherwise.
    /// </summary>
    internal static bool EngageEnemy(Creature creature, IGauntletCreatureOwner owner, out ThreadEncounterPortalWaves.WakeResult wake)
    {
        wake = ThreadEncounterPortalWaves.WakeResult.MemberUntargetable;

        if (owner == null || !owner.IsLive || !ThreadEncounterPortalWaves.IsStanding(creature) || creature.PhysicsObj == null || creature.MonsterState != Creature.State.Idle)
            return false;

        wake = Wake(creature, owner);
        return true;
    }

    /// <summary>
    /// The nearest member the creature may hold (ThreadEncounterPortalWaves.IsWakeCandidate: alive, not teleporting, attackable,
    /// with physics, and inside the perimeter), within chase range, made visible if it is not yet; woken with alertNearby false.
    /// The members come from the owner (IGauntletCreatureOwner.OnlineMembersInside); the result from ClassifyWake.
    /// </summary>
    private static ThreadEncounterPortalWaves.WakeResult Wake(Creature creature, IGauntletCreatureOwner owner)
    {
        var (best, members) = NearestTarget(creature, owner.OnlineMembersInside(),
            p => creature.PhysicsObj.get_distance_sq_to_object(p.PhysicsObj, true));

        if (best == null)
            return ThreadEncounterPortalWaves.ClassifyWake(members, false);

        if (!creature.IsVisibleTarget(best))
            creature.PhysicsObj.ObjMaint.AddVisibleTargets(new List<PhysicsObj> { best.PhysicsObj });

        if (!creature.IsVisibleTarget(best))
            return ThreadEncounterPortalWaves.ClassifyWake(members, false);

        creature.AttackTarget = best;
        creature.WakeUp(alertNearby: false);
        return ThreadEncounterPortalWaves.ClassifyWake(members, true);
    }

    /// <summary>
    /// The wake's choice: the member nearest by <paramref name="distanceSq"/> among wake candidates within
    /// Creature.MaxChaseRangeSq, and how many members were offered. <paramref name="distanceSq"/> is asked only of candidates.
    /// </summary>
    internal static (Player Best, int Members) NearestTarget(Creature creature, IEnumerable<Player> members, Func<Player, double> distanceSq)
    {
        Player best = null;
        var bestDistSq = double.MaxValue;
        var count = 0;

        foreach (var p in members ?? Enumerable.Empty<Player>())
        {
            if (p == null)
                continue;

            count++;

            if (!ThreadEncounterPortalWaves.IsWakeCandidate(creature.P_OutdoorPerimeter, p.IsDead, p.Teleporting, p.Attackable, p.PhysicsObj != null, p.Location))
                continue;

            var d = distanceSq(p);

            if (d <= Creature.MaxChaseRangeSq && d < bestDistSq)
            {
                best = p;
                bestDistSq = d;
            }
        }

        return (best, count);
    }

    // ---- death ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Creature.Die's Gauntlet hook (after the Balance Trials hook, so after OnDeath_GrantXP paid the kill): reports the death
    /// to the owner while its run is live. Never throws: a run fault must not abort Die after dieEntered is set.
    /// </summary>
    public static void OnCreatureDied(Creature creature)
    {
        var owner = creature?.P_Gauntlet;

        if (owner == null)
            return;

        try
        {
            if (owner.IsLive)
                owner.OnGauntletCreatureDied(creature);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] the death report for {creature.Name} (0x{creature.Guid}, wcid {creature.WeenieClassId}) threw", ex);
        }
    }
}
