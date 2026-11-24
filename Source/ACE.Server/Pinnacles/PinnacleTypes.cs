using System;
using System.Collections.Generic;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles
{
    // FROZEN SHARED TYPES (Docs/Knots/PINNACLE-ENCOUNTER-PLAN.md section 4.0, with the section 7 amendments).
    // The core (M2b), the live adapter (M2c) and every boss script (M3) are written against these names; change
    // them only together with every implementer.

    public enum PinnacleFightState { NotStarted, Running, Held, Won, Lost }

    /// <summary>
    /// How hard a spawned creature is scaled to the Knot level (PinnacleScaling.Apply): Elite = 1.5 x the Threads
    /// health curve, Boss = the Threads boss pipeline at 3 x the curve, Head = the Boss pipeline at 1.5 x Boss health.
    /// A spawn that is not a creature (a seal) ignores it.
    /// </summary>
    public enum PinnacleTier { Elite, Boss, Head }

    /// <summary>
    /// An opaque reference to one object the fight spawned, issued by <see cref="IPinnacleWorld.Spawn"/>. Id 0 is
    /// <see cref="None"/> (spawn failed, or no object). Handles are never reused within one fight.
    /// </summary>
    public readonly struct PinnacleHandle : IEquatable<PinnacleHandle>
    {
        public static readonly PinnacleHandle None;

        public PinnacleHandle(int id)
        {
            Id = id;
        }

        public int Id { get; }

        public bool IsNone => Id == 0;

        public bool Equals(PinnacleHandle other) => Id == other.Id;

        public override bool Equals(object obj) => obj is PinnacleHandle other && Equals(other);

        public override int GetHashCode() => Id;

        public static bool operator ==(PinnacleHandle a, PinnacleHandle b) => a.Id == b.Id;

        public static bool operator !=(PinnacleHandle a, PinnacleHandle b) => a.Id != b.Id;

        public override string ToString() => IsNone ? "none" : $"#{Id}";
    }

    /// <summary>Landblock-local; Cell is the full cell id (0x0069xxxx); rotation W,X,Y,Z.</summary>
    public readonly record struct PinnaclePoint(uint Cell, float X, float Y, float Z, float QW = 1f, float QX = 0f, float QY = 0f, float QZ = 0f);

    public enum PinnacleEventKind { AddDied, HazardEnded, TimerFired, ObjectUsed }

    /// <summary>One thing that happened, delivered to <see cref="IPinnacleBossScript.OnEvent"/> on the fight thread.</summary>
    public readonly struct PinnacleEvent
    {
        /// <summary>The <see cref="Tag"/> of the AddDied the core raises for the head (the script's own adds carry its tags).</summary>
        public const string HeadTag = "head";

        public PinnacleEventKind Kind { get; init; }

        /// <summary>AddDied: the add (or the head) that died. ObjectUsed: the prop the owner used.</summary>
        public PinnacleHandle Handle { get; init; }

        /// <summary>The spawn tag (AddDied, ObjectUsed), the hazard tag (HazardEnded) or the timer tag (TimerFired).</summary>
        public string Tag { get; init; }

        /// <summary>HazardEnded: the FINAL outcome after the core's one retry.</summary>
        public HazardOutcome Outcome { get; init; }

        /// <summary>AddDied synthesised after the spawn and its one retry both failed; Handle is None.</summary>
        public bool SpawnFailed { get; init; }

        public override string ToString()
            => Kind switch
            {
                PinnacleEventKind.AddDied => $"AddDied({Tag}, {Handle}{(SpawnFailed ? ", spawn failed" : "")})",
                PinnacleEventKind.HazardEnded => $"HazardEnded({Tag}, {Outcome})",
                PinnacleEventKind.ObjectUsed => $"ObjectUsed({Tag}, {Handle})",
                _ => $"TimerFired({Tag})",
            };
    }

    /// <summary>
    /// A fight-scoped prop a script spawns through <see cref="IPinnacleFightPort.SpawnProp"/> (a door, a panel, a
    /// platform, an effect host): not phase-scoped, never reported dead, destroyed only by the fight's End or by the
    /// script. <paramref name="Translucency"/> and <paramref name="Script"/> (a visual effect script id) are stamped
    /// only when set; <paramref name="DoorOpen"/> applies to a door only (null = leave it as authored).
    /// <paramref name="Ethereal"/> makes the prop walk-through; on a door it holds through every close (Door.AlwaysEthereal).
    /// </summary>
    public sealed record PinnacleProp(uint Wcid, PinnaclePoint At, float Scale = 1f, float? Translucency = null, uint Script = 0, bool? DoorOpen = null, bool Ethereal = false);

    /// <summary>What a hit by a hazard does to the victim's movement: run speed cut by a fraction, attack speed scaled, for a time.</summary>
    public sealed record PinnacleHitSlow(double RunSlowFraction, double AttackSpeedFactor, double Seconds);

    /// <summary>
    /// Per-run options a script hands StartHazard. Null options = none. The run's region is, in order: <paramref name="Area"/>
    /// (an ad-hoc rectangle, see <see cref="PinnacleHazardArea"/>), else the preset named <paramref name="Region"/>
    /// (HazardRegionPresets), else the fight's own preset (PinnacleScriptData.HazardRegionPreset). Setting both Region and
    /// Area is refused (PinnacleHazardRegions.Resolve).
    /// </summary>
    public sealed record PinnacleHazardOptions(PinnacleHitSlow HitSlow = null, string Region = null, PinnacleHazardArea Area = null);

    /// <summary>
    /// An ad-hoc rectangular hazard region for one run, landblock-local in the fight's own landblock (the landblock of the
    /// fight's preset): x [<paramref name="MinX"/>, <paramref name="MaxX"/>], y [<paramref name="MinY"/>, <paramref name="MaxY"/>],
    /// a flat ground plane at <paramref name="GroundZ"/>, an optional ceiling, and the "in the region" band from
    /// GroundZ - <paramref name="BandBelow"/> to GroundZ + <paramref name="BandAbove"/> (defaults: the /hazard ad-hoc band,
    /// HazardDefaults). Moving hazards fly at the fight preset's hazard offset. The run still has to pass the mechanic's own
    /// validation (HazardManager.Start).
    /// </summary>
    public sealed record PinnacleHazardArea(float MinX, float MaxX, float MinY, float MaxY, float GroundZ, float? CeilingZ = null,
        float BandBelow = HazardDefaults.BandBelow, float BandAbove = HazardDefaults.BandAbove);

    /// <summary>A boss script's static data: where things are and what the core spawns for it.</summary>
    public sealed class PinnacleScriptData
    {
        /// <summary>True when the fight's floor is a long drop the script wants no fall damage from (the live world waives it).</summary>
        public bool WaiveFallDamage { get; init; }

        public ushort Landblock { get; init; }

        public PinnaclePoint ArrivalPoint { get; init; }

        public PinnaclePoint FightEntryPoint { get; init; }

        public uint SealWcid { get; init; }

        public IReadOnlyList<PinnaclePoint> SealPoints { get; init; }

        public uint HeadWcid { get; init; }

        /// <summary>
        /// Every OTHER world-database wcid the script spawns (props, hazard hosts, wave adds). The entry readiness check
        /// refuses the entry, with nothing created or charged, when any of them is missing. Default empty (Grael).
        /// </summary>
        public IReadOnlyList<uint> RequiredWcids { get; init; } = System.Array.Empty<uint>();

        public PinnaclePoint HeadPoint { get; init; }

        /// <summary>
        /// Max-health multipliers by wcid, on top of the spawn's tier (PinnacleScaling.TierHealth): the core passes
        /// <see cref="HealthMultiplierFor"/> to IPinnacleWorld.Spawn for every spawn it makes: the head, every add and every
        /// seal (a seal that is not a creature ignores it, as it ignores its tier). A wcid not listed spawns at 1.0. Default
        /// empty (Grael, Matron).
        /// </summary>
        public IReadOnlyDictionary<uint, double> HealthMultipliers { get; init; } = new Dictionary<uint, double>();

        /// <summary>The multiplier <see cref="HealthMultipliers"/> lists for <paramref name="wcid"/>, else 1.0.</summary>
        public double HealthMultiplierFor(uint wcid)
            => HealthMultipliers != null && HealthMultipliers.TryGetValue(wcid, out var m) ? m : 1.0;

        /// <summary>
        /// The tier the core spawns the head at (PinnacleScaling.Apply). Boss unless a script says otherwise; a boss
        /// whose head must stay the toughest creature in its room at every Knot level sets Head.
        /// </summary>
        public PinnacleTier HeadTier { get; init; } = PinnacleTier.Boss;

        /// <summary>
        /// True: the head leaves a corpse where it dies (still lootless: the reward is the cache). Default false: like every
        /// other encounter creature it leaves none (Grael).
        /// </summary>
        public bool HeadLeavesCorpse { get; init; }

        /// <summary>
        /// Radians added to the head's turn toward the player (PinnacleFacing.FaceToward faceYawOffset). 0 = the
        /// model's face is on its local +Y; pi = on local -Y (Grael's head, whose GraelData value a later step sets).
        /// </summary>
        public float HeadFaceYawOffset { get; init; }

        /// <summary>
        /// Metres above the head's origin its spell projectiles launch from (WorldObject.ProjectileCastHeight, set on the
        /// caster at each cast). 0 = the collision height, the retail rule. A head whose collision column reaches past
        /// the room's ceiling must set one, or its bolts are spent on the ceiling and its arcs spawn above it.
        /// </summary>
        public float HeadCastHeight { get; init; }

        /// <summary>
        /// Where the won fight's cache forms, or null to form it beside the owner. An arena whose walkable floor is
        /// object geometry whose physics bodies are NOT Static must set one over REAL geometry: the cache is ethereal and
        /// has gravity, so it passes through a non-Static object and falls. A weenie's PhysicsState Static bit does not
        /// survive InitPhysicsObj; only PropertyBool.ForcePhysicsStatic keeps it. (Grael's floor Ward Planes carry that bool
        /// since 2026-11-06, so its point is belt and braces there.)
        /// </summary>
        public PinnaclePoint? CachePoint { get; init; }

        /// <summary>Where the return portal stands, or null to stand it at the owner. Same rule as <see cref="CachePoint"/>.</summary>
        public PinnaclePoint? ReturnPortalPoint { get; init; }

        /// <summary>The hazard region preset name ("pinnacle").</summary>
        public string HazardRegionPreset { get; init; }

        /// <summary>
        /// The arena's object floor (Ward Planes over real geometry), or null when its floor is the room itself. While the
        /// copy is live, every tick, a player found inside it BELOW the plates is lifted back on top
        /// (<see cref="PinnaclePlateLift"/>, LivePinnacleWorld.LiftPlayersUnderPlateFloor).
        /// </summary>
        public PinnaclePlateFloor PlateFloor { get; init; }

        /// <summary>
        /// A per-copy box of landblock_instance rows the Knot copy does not load (the retail content inside the boss
        /// room), or null to load every row. Carried onto the copy's EphemeralRealm by the entry path.
        /// </summary>
        public ACE.Server.Realms.EphemeralRowExclusion ExcludeRows { get; init; }
    }

    /// <summary>
    /// A rectangle of floor plates, landblock-local, in the fight's own landblock. A player whose origin is inside
    /// [MinX, MaxX] x [MinY, MaxY] with <see cref="LowestZ"/> &lt; z &lt; <see cref="BottomZ"/> is under the plates (standing on
    /// the real geometry beneath them, overlapping a plate) and is lifted to <see cref="LiftZ"/> at the same x and y. When
    /// forced lifts keep failing, the escalation teleport goes to <see cref="TeleportZ"/> instead: a teleport re-runs the
    /// client's placement, so it must start past the step-down reach of the real geometry under the plates.
    /// </summary>
    public sealed record PinnaclePlateFloor(float MinX, float MaxX, float MinY, float MaxY, float BottomZ, float LowestZ, float LiftZ, float TeleportZ);

    public sealed class PinnacleFightConfig
    {
        public double HoldSeconds { get; init; } = 600;

        public double ResumeGraceSeconds { get; init; } = PinnacleTunables.DefaultResumeGraceSeconds;
    }
}
