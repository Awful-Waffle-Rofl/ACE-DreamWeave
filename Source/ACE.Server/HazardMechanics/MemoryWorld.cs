using System;
using System.Collections.Generic;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>A spawned pad marker the mechanic may check for life.</summary>
    public interface IMemoryPiece
    {
        bool Alive { get; }
    }

    /// <summary>
    /// Everything the memory mechanic needs from the world, behind one small seam so its Start/Tick/OnStop
    /// decisions can be tested without a landblock. The live implementation is <see cref="HazardMemoryWorld"/>,
    /// a thin wrapper over <see cref="HazardContext"/> (shared invariants 1, 4, 5 and 9 all stay with the context).
    /// </summary>
    public interface IMemoryWorld
    {
        double Now { get; }

        Random Rng { get; }

        HazardRegion Region { get; }

        HazardArgs Args { get; }

        float EdgeInset { get; }

        /// <summary>The point on the ground at (x, y): ctx.PointFor(x, y, Ground).</summary>
        Vector3 GroundPoint(float x, float y);

        /// <summary>Would the point resolve to a cell (ctx.HasCell)? <paramref name="raise"/>: for a stationary object.</summary>
        bool HasCell(Vector3 point, bool raise);

        /// <summary>Spawns one pad marker via ctx.Spawn. Null when refused.</summary>
        IMemoryPiece SpawnMarker(uint wcid, Vector3 position, string tag);

        /// <summary>A beam effect with the given host <paramref name="orientation"/>; <paramref name="raise"/> false spawns it exactly at the point (no 1.5 m raise onto a cell) so a HasCell(point, false) check and the spawn agree. For <paramref name="seconds"/> via ctx.SpawnTimedEffect. False when refused. <paramref name="raise"/> true is exactly the FLOOR host case (MemoryMechanic: raise = no ceiling), so the live world also passes it as grounded: a turned floor host gets HazardGround.TurnedHostLift.</summary>
        bool SpawnBeam(Vector3 point, uint script, double seconds, string tag, Quaternion orientation, bool raise);

        /// <summary>A fire pillar (HazardDefaults.PillarScript, identity orientation, default raise) standing <paramref name="seconds"/> via ctx.SpawnTimedEffect, as the wisps raise theirs. Null when refused.</summary>
        IMemoryPiece SpawnPillar(Vector3 point, double seconds, string tag);

        /// <summary>The standard hazard hit of a pillar: ctx.HitPlayers with the pillar as the damage source, an infinite window (one hit per player per pillar) and ctx.DamageType. Returns how many players were hit.</summary>
        int Strike(IMemoryPiece pillar, Vector3 centre, float radius);

        /// <summary>Every live player in the region with their landblock-local planar position.</summary>
        IReadOnlyList<(uint Id, Vector2 Position)> Players();

        string NameOf(uint id);

        /// <summary>The developer channel (a [hazard #N] line); silent when the run's host is player-facing.</summary>
        void Say(string line);

        /// <summary>The player channel: a short plain line, said only when the run's host is player-facing (IHazardHost.PlayerFacing).</summary>
        void SayPlayer(string line) { }

        void Log(string line);

        void End(HazardOutcome outcome, string reason);
    }

    /// <summary>The live <see cref="IMemoryWorld"/>: every call goes through the run's <see cref="HazardContext"/>.</summary>
    internal sealed class HazardMemoryWorld : IMemoryWorld
    {
        private sealed class Piece : IMemoryPiece
        {
            private readonly HazardHandle handle;

            public Piece(HazardHandle handle) => this.handle = handle;

            public bool Alive => handle != null && handle.Alive;

            public HazardHandle Handle => handle;
        }

        private readonly HazardContext ctx;
        private readonly Dictionary<uint, string> names = new Dictionary<uint, string>();

        public HazardMemoryWorld(HazardContext ctx) => this.ctx = ctx;

        public double Now => ctx.Now;

        public Random Rng => ctx.Rng;

        public HazardRegion Region => ctx.Region;

        public HazardArgs Args => ctx.Args;

        public float EdgeInset => ctx.EdgeInset;

        public Vector3 GroundPoint(float x, float y) => ctx.PointFor(x, y, HazardFollow.Ground);

        public bool HasCell(Vector3 point, bool raise) => ctx.HasCell(point, raise);

        public IMemoryPiece SpawnMarker(uint wcid, Vector3 position, string tag)
        {
            var handle = ctx.Spawn(new HazardSpawnSpec { Wcid = wcid, Position = position, Tag = tag });

            return handle == null ? null : new Piece(handle);
        }

        public bool SpawnBeam(Vector3 point, uint script, double seconds, string tag, Quaternion orientation, bool raise)
            => ctx.SpawnTimedEffect(point, script, orientation, seconds, 1f, tag, raise, grounded: raise) != null;

        public IMemoryPiece SpawnPillar(Vector3 point, double seconds, string tag)
        {
            var handle = ctx.SpawnTimedEffect(point, HazardDefaults.PillarScript, Quaternion.Identity, seconds, 1f, tag);

            return handle == null ? null : new Piece(handle);
        }

        public int Strike(IMemoryPiece pillar, Vector3 centre, float radius)
            => pillar is Piece p ? ctx.HitPlayers(p.Handle, centre, radius, double.PositiveInfinity, damageType: ctx.DamageType) : 0;

        public IReadOnlyList<(uint Id, Vector2 Position)> Players()
        {
            var inRegion = ctx.PlayersInRegion();
            var list = new List<(uint, Vector2)>(inRegion.Count);

            foreach (var (player, position) in inRegion)
            {
                names[player.Guid.Full] = player.Name;
                list.Add((player.Guid.Full, MemoryMarkers.Planar(position)));
            }

            return list;
        }

        public string NameOf(uint id) => names.TryGetValue(id, out var name) ? name : "Someone";

        public void Say(string line) => ctx.Say($"[hazard #{ctx.Run.Id}] {line}");

        public void SayPlayer(string line) => ctx.SayPlayer(line);

        public void Log(string line) => ctx.Log(line);

        public void End(HazardOutcome outcome, string reason) => ctx.End(outcome, reason);
    }
}
