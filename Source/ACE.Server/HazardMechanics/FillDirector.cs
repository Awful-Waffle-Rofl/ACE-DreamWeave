using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The fill director's world (pattern <see cref="IRainPort{THandle}"/>): spawn, move, hit, say and end through
    /// one HazardContext in the game, a recording fake in the tests. <typeparamref name="TPlayer"/> is opaque to the
    /// director; it only hands it back to <see cref="HitFlat"/>.
    /// </summary>
    public interface IFillPort<THandle, TPlayer> where THandle : class
    {
        /// <summary>One panel at <paramref name="centre"/>, <paramref name="edge"/> metres square (the sheet plan's, FillRules.Sheet): facing down or up; moving panels (every surface panel, including a doubled tile's down-facing partner) will MoveLeg, the under sheet does not.</summary>
        THandle SpawnPanel(Vector3 centre, float edge, bool faceDown, bool moving);

        THandle SpawnWisp(Vector3 at);

        bool HasCell(Vector3 point);

        bool Alive(THandle handle);

        /// <summary>Moves a group over <paramref name="seconds"/>; seconds &lt;= 0 snaps every member to its end. Returns how many started.</summary>
        int Move(IReadOnlyList<(THandle Handle, Vector3 End)> group, double seconds);

        /// <summary>Stops a handle where it is now.</summary>
        void Halt(THandle handle);

        void Destroy(THandle handle);

        /// <summary>Every live player in the region with their feet position in the region frame.</summary>
        IReadOnlyList<(TPlayer Player, Vector3 Feet)> Players();

        /// <summary>One flat, resist-mitigated hit from <paramref name="source"/>. Returns the damage taken.</summary>
        int HitFlat(THandle source, TPlayer player, double flat);

        /// <summary>The runtime controls waiting for this tick (FIFO).</summary>
        IReadOnlyList<HazardControl> TakeControls();

        Random Rng { get; }

        void Say(string line);

        void Log(string line);

        void End(HazardOutcome outcome, string reason);
    }

    /// <summary>
    /// The fill run: a flat sheet of panels rising (or falling) through a contained room, wisps riding just under the
    /// surface, an under-sheet that shows once the surface is over a player's head, and a flat resist-mitigated hit
    /// on everyone below the surface. Every decision is a <see cref="FillRules"/>/<see cref="FillSurface"/> call;
    /// this class only sequences them against a port.
    ///
    /// ONE TICK, in this fixed order: controls, duration, panels lost, motion, under sheet, damage.
    /// </summary>
    public sealed class FillDirector<THandle, TPlayer> where THandle : class
    {
        private const double LegSlack = 1e-6;

        private sealed class Piece
        {
            public THandle Handle;

            /// <summary>The down-facing partner of a doubled surface tile at the same centre; null for every other piece.</summary>
            public THandle Back;
            public Vector2 Xy;
            public float ZOffset;

            /// <summary>
            /// The lowest z this tile's panels may be put at: for a doubled sheet the SHEET-WIDE face-down floor
            /// (<see cref="SheetFloor"/>, the same for every tile, so the sheet stays one plane); -infinity otherwise.
            /// The SURFACE (damage, level, legs' timing) is unchanged - only where the panels are drawn is floored.
            /// </summary>
            public float MinZ = float.NegativeInfinity;

            /// <summary>Where this piece's panels go for surface z <paramref name="z"/>.</summary>
            public float PanelZ(double z) => Math.Max((float)z, MinZ) + ZOffset;
        }

        private readonly HazardRegion region;
        private readonly FillSettings s;
        private readonly IFillPort<THandle, TPlayer> port;
        private readonly FillSurface surface;

        /// <summary>The ONE sheet plan (centres, panel edge; every ZOffset 0) every panel is drawn from.</summary>
        private readonly (float Edge, IReadOnlyList<(Vector2 Centre, float ZOffset)> Tiles) sheet;

        private readonly List<Piece> tiles = new List<Piece>();
        private readonly List<Piece> wisps = new List<Piece>();
        private readonly List<THandle> under = new List<THandle>();

        private double damageScale;
        private double nextDamageAt;
        private double legEndsAt = double.PositiveInfinity;
        private int seenVersion = int.MinValue;
        private bool moving;
        private bool underShown;
        private bool ended;
        private volatile string describe = "starting";

        public FillDirector(HazardRegion region, FillSettings s, IFillPort<THandle, TPlayer> port)
        {
            this.region = region;
            this.s = s;
            this.port = port;

            surface = new FillSurface(s.FloorZ, s.MaxZ, s.StartZ, s.Rate);
            sheet = FillRules.Sheet(region, s.PanelSize, s.SheetMargin);
            damageScale = s.DamageScale;
        }

        public int TilesMissed { get; private set; }

        public int WispsMissed { get; private set; }

        public int Hits { get; private set; }

        /// <summary>Where the surface is at run time <paramref name="now"/> (for the host and the tests).</summary>
        public double SurfaceAt(double now) => surface.ZAt(now);

        public string Describe() => describe;

        /// <summary>Spawns the surface tiles and the wisps at the start surface. Moves nothing.</summary>
        public void Start(double now)
        {
            var z0 = surface.ZAt(now);
            var floor = s.Doubled ? SheetFloor((float)s.FloorZ) : float.NegativeInfinity;

            foreach (var (centre, offset) in sheet.Tiles)
            {
                var piece = new Piece { Xy = centre, ZOffset = offset, MinZ = floor };

                var at = new Vector3(centre.X, centre.Y, piece.PanelZ(z0));
                var handle = port.SpawnPanel(at, sheet.Edge, false, true);
                var back = s.Doubled && handle != null ? port.SpawnPanel(at, sheet.Edge, true, true) : null;

                if (handle == null || (s.Doubled && back == null))
                {
                    // a pair that is not whole is one missed tile: the half that did spawn is taken down again
                    if (handle != null)
                        port.Destroy(handle);

                    TilesMissed++;
                }
                else
                {
                    piece.Handle = handle;
                    piece.Back = back;
                    tiles.Add(piece);
                }
            }

            if (floor - (float)s.FloorZ > 1e-4f)
                port.Log(string.Format(CultureInfo.InvariantCulture,
                    "fill: the doubled sheet draws its panels no lower than z {0:0.##} (floor_z {1:0.##} + {2:0.##} m): floor_z is within {3:0.##} m of the floor, where a face-down panel cannot be placed",
                    floor, s.FloorZ, floor - (float)s.FloorZ, HazardPanels.FaceClearance));

            for (var i = 0; i < s.Wisps; i++)
            {
                var spawned = false;

                for (var attempt = 0; attempt < 8 && !spawned; attempt++)
                {
                    if (!region.TrySamplePoint(port.Rng, out var xy, edgeInset: 2f))
                        continue;

                    var at = new Vector3(xy.X, xy.Y, FillRules.WispZ(z0, s.GroundZ, region.CeilingZ, s.WispScale));

                    if (!port.HasCell(at))
                        continue;

                    var handle = port.SpawnWisp(at);

                    if (handle != null)
                    {
                        wisps.Add(new Piece { Handle = handle, Xy = xy });
                        spawned = true;
                    }
                }

                if (!spawned)
                    WispsMissed++;
            }

            port.Say($"The {s.Element} is rising.");
            Refresh(now);
        }

        public void Tick(double now)
        {
            if (ended)
                return;

            // 1. controls, in arrival order
            var snap = false;

            foreach (var control in port.TakeControls())
            {
                switch (control.Key)
                {
                    case "rate":
                        surface.SetRate(now, Math.Clamp(control.Value, -2.0, 2.0));
                        break;
                    case "target":
                        surface.SetTarget(now, control.Value, Math.Abs(s.Rate));
                        break;
                    case "set_z":
                        surface.SetZ(now, control.Value);
                        snap = true;
                        break;
                    case "damage_scale":
                        damageScale = Math.Clamp(control.Value, 0.0, 100.0);
                        break;
                    case "stop":
                        Finish(HazardOutcome.Stopped, "control stop");
                        return;
                    default:
                        port.Log($"fill ignores unknown control '{control.Key}'");
                        break;
                }
            }

            // 2. duration
            if (s.Duration > 0.0 && now >= s.Duration)
            {
                Finish(HazardOutcome.Completed, "duration elapsed");
                return;
            }

            // 3. the sheet itself
            if (!AnyAlive(tiles))
            {
                Finish(HazardOutcome.Aborted, "every surface panel was lost");
                return;
            }

            // 4. motion
            if (snap)
            {
                SnapAll(now);
            }
            else if (surface.Version != seenVersion || now >= legEndsAt - LegSlack)
            {
                PlanLeg(now);
            }

            // 5. the under sheet
            var surfaceZ = surface.ZAt(now);
            var shown = FillRules.UnderSheetShown(underShown, surfaceZ, UnderZ, s.UnderMargin);

            if (shown && !underShown)
                SpawnUnder();
            else if (!shown && underShown)
                DestroyUnder();

            underShown = shown;

            // 6. damage
            if (now >= nextDamageAt)
            {
                nextDamageAt = now + s.Tick;
                HurtPlayers(surfaceZ);
            }

            Refresh(now);
        }

        private double UnderZ => s.GroundZ + s.HeadHeight;

        /// <summary>
        /// ONE floor for every face-down panel of the sheet at base <paramref name="z"/>: the highest of the per-tile
        /// FillRules.LowestFaceDownZ over the plan's centres (a centre in no cell gives <paramref name="z"/> back, so it
        /// never raises the sheet). Per-tile floors would step the sheet wherever the room floor is uneven, and a step on
        /// a shared edge draws as a seam line. At most HazardPanels.FaceClearance above <paramref name="z"/>.
        /// </summary>
        private float SheetFloor(float z)
        {
            var floor = z;

            foreach (var (centre, _) in sheet.Tiles)
                floor = Math.Max(floor, FillRules.LowestFaceDownZ(centre, z, port.HasCell));

            return floor;
        }

        private static bool AnyAlive(List<Piece> pieces, IFillPort<THandle, TPlayer> port, out THandle first)
        {
            first = null;

            foreach (var p in pieces)
            {
                if (Whole(p, port))
                {
                    first = p.Handle;
                    return true;
                }
            }

            return false;
        }

        /// <summary>A piece counts as alive only while every panel of it is: a doubled tile with one panel lost is a lost tile.</summary>
        private static bool Whole(Piece p, IFillPort<THandle, TPlayer> port)
            => port.Alive(p.Handle) && (p.Back == null || port.Alive(p.Back));

        private bool AnyAlive(List<Piece> pieces) => AnyAlive(pieces, port, out _);

        private void Finish(HazardOutcome outcome, string reason)
        {
            ended = true;
            port.End(outcome, reason);
        }

        private float WispZAt(double z) => FillRules.WispZ(z, s.GroundZ, region.CeilingZ, s.WispScale);

        private List<(THandle Handle, Vector3 End)> Group(List<Piece> pieces, double z, bool wisp)
        {
            var group = new List<(THandle, Vector3)>(pieces.Count);

            foreach (var p in pieces)
            {
                var endZ = wisp ? WispZAt(z) : p.PanelZ(z);
                var end = new Vector3(p.Xy.X, p.Xy.Y, endZ);

                if (port.Alive(p.Handle))
                    group.Add((p.Handle, end));

                if (p.Back != null && port.Alive(p.Back))
                    group.Add((p.Back, end));
            }

            return group;
        }

        private void SnapAll(double now)
        {
            var z = surface.ZAt(now);
            var all = Group(tiles, z, false);

            all.AddRange(Group(wisps, z, true));
            port.Move(all, 0.0);
            moving = false;
            legEndsAt = double.PositiveInfinity;
        }

        private void PlanLeg(double now)
        {
            seenVersion = surface.Version;

            var leg = FillRules.NextLeg(surface, now, s.Resnap);

            if (leg.HasValue)
            {
                port.Move(Group(tiles, leg.Value.EndZ, false), leg.Value.Seconds);
                port.Move(Group(wisps, leg.Value.EndZ, true), leg.Value.Seconds);
                legEndsAt = now + leg.Value.Seconds;
                moving = true;
                return;
            }

            legEndsAt = double.PositiveInfinity;

            if (!moving)
                return;

            moving = false;

            foreach (var p in tiles)
            {
                if (port.Alive(p.Handle))
                    port.Halt(p.Handle);

                if (p.Back != null && port.Alive(p.Back))
                    port.Halt(p.Back);
            }

            foreach (var p in wisps)
                if (port.Alive(p.Handle))
                    port.Halt(p.Handle);
        }

        private void SpawnUnder()
        {
            // face down too, so never sent into the floor (only a head_height under 0.21 m can reach it); one z for all
            var z = SheetFloor((float)UnderZ);

            foreach (var (centre, _) in sheet.Tiles)
            {
                var handle = port.SpawnPanel(new Vector3(centre.X, centre.Y, z), sheet.Edge, true, false);

                if (handle != null)
                    under.Add(handle);
            }
        }

        private void DestroyUnder()
        {
            foreach (var handle in under)
                port.Destroy(handle);

            under.Clear();
        }

        private void HurtPlayers(double surfaceZ)
        {
            if (!AnyAlive(tiles, port, out var source))
                return;

            foreach (var (player, feet) in port.Players())
            {
                var flat = FillRules.FlatDamage(surfaceZ - feet.Z, s, damageScale);

                if (flat <= 0.0)
                    continue;

                if (port.HitFlat(source, player, flat) > 0)
                    Hits++;
            }
        }

        private void Refresh(double now)
        {
            var alive = 0;

            foreach (var p in tiles)
                if (Whole(p, port))
                    alive++;

            describe = string.Format(CultureInfo.InvariantCulture, "z={0:0.##} rate={1:0.###}{2} tiles={3}/{4} wisps={5} under={6} hits={7} scale={8:0.##}",
                surface.ZAt(now), surface.Rate, surface.Target.HasValue ? " target=" + surface.Target.Value.ToString("0.##", CultureInfo.InvariantCulture) : "",
                alive, tiles.Count + TilesMissed, wisps.Count, underShown ? "on" : "off", Hits, damageScale);
        }
    }
}
