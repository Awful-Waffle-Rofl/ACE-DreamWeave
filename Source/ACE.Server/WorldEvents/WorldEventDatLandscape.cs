using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The clear-area check's view of the outdoor landscape, read from the CLIENT DAT ONLY: terrain heights, water and road
    /// bits (CellLandblock), buildings and static objects (LandblockInfo), and the procedural scenery (trees, rocks) derived
    /// from the terrain the same way Physics.Common.Landblock.get_land_scenes derives it.
    ///
    /// WHY NOT THE PHYSICS LANDBLOCKS. The contributor read LScape.get_landblock from the start command's thread under the
    /// World Events manager lock. On the server that call is LandblockManager.GetLandblock (Physics/Common/LScape.cs:63-68),
    /// which LOADS a landblock that is not loaded - a full game landblock with its population task, generators and
    /// group registration (Managers/LandblockManager.cs:713-770, Entity/Landblock.cs:333-357) - and a physics landblock's
    /// ServerObjects list is a plain List changed on its own landblock thread (Physics/Common/Landblock.cs:93-110). Even
    /// building a private physics landblock is not side-effect free: add_obj_to_cell runs calc_cross_cells_static
    /// (Physics/PhysicsObj.cs:2015-2025), which reaches LScape for neighbouring cells. So this reader touches none of it:
    /// every input is an immutable dat file fetched through DatDatabase.ReadFromDat, which is documented and built thread
    /// safe (ACE.DatLoader/DatDatabase.cs:62-85: a ConcurrentDictionary cache, the stream read under a lock).
    ///
    /// WHAT IT MISSES, deliberately. Objects placed by the server (landblock_instance NPCs, signs, portals) are not in the
    /// dat and are not seen; the spawner's own placement retries still step around them. Scenery is read conservatively:
    /// the per-object slope and "inside a building cell" filters are skipped, so a few phantom trees can stand where the
    /// game would not draw one (only ever making the check stricter), and the slope check is per 24 m cell, worst triangle.
    ///
    /// One instance per resolve, used on one thread; it caches what it reads per landblock for that resolve only.
    /// </summary>
    public sealed class WorldEventDatLandscape : IWorldEventLandscape
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventDatLandscape));

        public const float BlockLength = 192f;
        public const float CellLength = 24f;
        public const int VertexDim = 9;

        /// <summary>The steepest walkable ground: a contact plane normal's Z must be at least this (PhysicsGlobals.FloorZ).</summary>
        public const float FloorZ = 0.66417414618662751f;

        /// <summary>Terrain types 0x10 to 0x14 are water (Physics.Common.LandblockStruct.SurfChar).</summary>
        public const int WaterTypeMin = 0x10, WaterTypeMax = 0x14;

        private sealed class Block
        {
            public float[] Heights;
            public ushort[] Terrain;
            public List<(float X, float Y, float Radius, string Kind)> Obstacles;
        }

        private readonly Dictionary<uint, Block> blocks = new Dictionary<uint, Block>();

        /// <summary>The obstacles of every outdoor landblock touching the circle, relative to its centre.</summary>
        public IReadOnlyList<AreaObstacle> ObstaclesNear(double globalX, double globalY, double radius, double ignoreRadius)
        {
            var result = new List<AreaObstacle>();

            var minX = (int)Math.Floor((globalX - radius) / BlockLength);
            var maxX = (int)Math.Floor((globalX + radius) / BlockLength);
            var minY = (int)Math.Floor((globalY - radius) / BlockLength);
            var maxY = (int)Math.Floor((globalY + radius) / BlockLength);

            for (var bx = Math.Max(0, minX); bx <= Math.Min(254, maxX); bx++)
            {
                for (var by = Math.Max(0, minY); by <= Math.Min(254, maxY); by++)
                {
                    var block = Get((uint)bx, (uint)by);

                    if (block?.Obstacles == null)
                        continue;

                    foreach (var (x, y, r, kind) in block.Obstacles)
                    {
                        if (!(r > ignoreRadius))
                            continue;

                        var dx = bx * BlockLength + x - globalX;
                        var dy = by * BlockLength + y - globalY;

                        if (Math.Sqrt(dx * dx + dy * dy) - r > radius)
                            continue;

                        result.Add(new AreaObstacle(dx, dy, r, kind));
                    }
                }
            }

            return result;
        }

        /// <summary>Outdoors (inside the world grid), no water in its 24 m cell, and both triangles of the cell walkable.</summary>
        public bool GroundOk(double globalX, double globalY)
        {
            if (!TryLocate(globalX, globalY, out var block, out var cx, out var cy, out _, out _))
                return false;

            return CellGroundOk(block.Heights, block.Terrain, cx, cy);
        }

        /// <summary>The terrain height, interpolated on the cell's two triangles (split along the SW-NE diagonal; a few cm off on the other split).</summary>
        public float? TerrainZ(double globalX, double globalY)
        {
            if (!TryLocate(globalX, globalY, out var block, out var cx, out var cy, out var fx, out var fy))
                return null;

            return InterpolateHeight(block.Heights, cx, cy, fx, fy);
        }

        // ---- pure rules (D6) ------------------------------------------------------------------------------------

        /// <summary>Whether a terrain word is water (its type field, bits 2 to 6, is 0x10 to 0x14).</summary>
        public static bool IsWater(ushort terrain)
        {
            var type = (terrain >> 2) & 0x1F;

            return type >= WaterTypeMin && type <= WaterTypeMax;
        }

        /// <summary>The Z of a triangle's normal with corners at the given heights (pure): 1 for flat ground.</summary>
        public static float NormalZ(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Cross(b - a, c - a);
            var len = n.Length();

            return len > 0 ? Math.Abs(n.Z) / len : 0f;
        }

        /// <summary>
        /// One 24 m cell's ground rule (pure over the 9x9 vertex arrays): no water at any of its four corners, and every
        /// triangle of either diagonal split at least <see cref="FloorZ"/>.
        /// </summary>
        public static bool CellGroundOk(IReadOnlyList<float> heights, IReadOnlyList<ushort> terrain, int cx, int cy)
        {
            if (heights == null || terrain == null || cx < 0 || cy < 0 || cx > 7 || cy > 7)
                return false;

            int I(int x, int y) => x * VertexDim + y;

            foreach (var i in new[] { I(cx, cy), I(cx + 1, cy), I(cx, cy + 1), I(cx + 1, cy + 1) })
                if (IsWater(terrain[i]))
                    return false;

            var sw = new Vector3(0, 0, heights[I(cx, cy)]);
            var se = new Vector3(CellLength, 0, heights[I(cx + 1, cy)]);
            var nw = new Vector3(0, CellLength, heights[I(cx, cy + 1)]);
            var ne = new Vector3(CellLength, CellLength, heights[I(cx + 1, cy + 1)]);

            var worst = Math.Min(Math.Min(NormalZ(sw, se, ne), NormalZ(sw, ne, nw)), Math.Min(NormalZ(sw, se, nw), NormalZ(se, ne, nw)));

            return worst >= FloorZ;
        }

        /// <summary>The height inside cell (<paramref name="cx"/>, <paramref name="cy"/>) at fractions (<paramref name="fx"/>, <paramref name="fy"/>) of it (pure).</summary>
        public static float InterpolateHeight(IReadOnlyList<float> heights, int cx, int cy, double fx, double fy)
        {
            int I(int x, int y) => x * VertexDim + y;

            var h00 = heights[I(cx, cy)];
            var h10 = heights[I(cx + 1, cy)];
            var h01 = heights[I(cx, cy + 1)];
            var h11 = heights[I(cx + 1, cy + 1)];

            fx = Math.Clamp(fx, 0, 1);
            fy = Math.Clamp(fy, 0, 1);

            // SW-NE split: below the diagonal (fx >= fy) the SE triangle, else the NW one.
            return fx >= fy
                ? (float)(h00 + (h10 - h00) * fx + (h11 - h10) * fy)
                : (float)(h00 + (h01 - h00) * fy + (h11 - h01) * fx);
        }

        /// <summary>
        /// Whether a landblock-local point lies on a road, from the four road bits around its cell (pure; the rule of
        /// Physics.Common.Landblock.OnRoad, so scenery the game keeps off a road is kept off here too).
        /// </summary>
        public static bool OnRoad(IReadOnlyList<ushort> terrain, float lx, float ly)
        {
            const float roadWidth = 5f, tile = CellLength;

            var x = (int)(lx / tile);
            var y = (int)(ly / tile);

            uint Road(int vx, int vy) => vx < 0 || vy < 0 || vx >= VertexDim || vy >= VertexDim ? 0u : (uint)(terrain[vx * VertexDim + vy] & 0x3);

            var r0 = Road(x, y);
            var r1 = Road(x, y + 1);
            var r2 = Road(x + 1, y);
            var r3 = Road(x + 1, y + 1);

            if (r0 == 0 && r1 == 0 && r2 == 0 && r3 == 0)
                return false;

            var dx = lx - x * tile;
            var dy = ly - y * tile;
            var rMin = roadWidth;
            var rMax = tile - roadWidth;

            if (r0 > 0)
            {
                if (r1 > 0)
                {
                    if (r2 > 0)
                        return r3 > 0 || dx < rMin || dy < rMin;

                    return r3 > 0 ? (dx < rMin || dy > rMax) : dx < rMin;
                }

                if (r2 > 0)
                    return r3 > 0 ? (dx > rMax || dy < rMin) : dy < rMin;

                return r3 > 0 ? Math.Abs(dx - dy) < rMin : dx + dy < rMin;
            }

            if (r1 > 0)
            {
                if (r2 > 0)
                    return r3 > 0 ? (dx > rMax || dy > rMax) : Math.Abs(dx + dy - tile) < rMin;

                return r3 > 0 ? dy > rMax : tile + dx - dy < rMin;
            }

            if (r2 > 0)
                return r3 > 0 ? dx > rMax : tile - dx + dy < rMin;

            return r3 > 0 && tile * 2f - dx - dy < rMin;
        }

        // ---- dat reads ------------------------------------------------------------------------------------------

        private bool TryLocate(double globalX, double globalY, out Block block, out int cx, out int cy, out double fx, out double fy)
        {
            block = null;
            cx = cy = 0;
            fx = fy = 0;

            if (!double.IsFinite(globalX) || !double.IsFinite(globalY) || globalX < 0 || globalY < 0)
                return false;

            var bx = (int)(globalX / BlockLength);
            var by = (int)(globalY / BlockLength);

            if (bx > 254 || by > 254)
                return false;

            block = Get((uint)bx, (uint)by);

            if (block?.Heights == null)
                return false;

            var lx = globalX - bx * BlockLength;
            var ly = globalY - by * BlockLength;

            cx = Math.Clamp((int)(lx / CellLength), 0, 7);
            cy = Math.Clamp((int)(ly / CellLength), 0, 7);
            fx = (lx - cx * CellLength) / CellLength;
            fy = (ly - cy * CellLength) / CellLength;

            return true;
        }

        private Block Get(uint bx, uint by)
        {
            var key = (bx << 8) | by;

            if (blocks.TryGetValue(key, out var cached))
                return cached;

            Block block = null;

            try
            {
                block = Read(bx, by);
            }
            catch (Exception ex)
            {
                log.Debug($"[WORLDEVENT] spawn area: landblock {bx:X2}{by:X2} could not be read from the dat", ex);
            }

            blocks[key] = block;
            return block;
        }

        private static Block Read(uint bx, uint by)
        {
            if (DatManager.CellDat == null || DatManager.PortalDat == null)
                return null;

            var id = (bx << 24) | (by << 16);
            var cell = DatManager.CellDat.ReadFromDat<CellLandblock>(id | 0xFFFF);

            if (cell == null || cell.Height.Count < VertexDim * VertexDim || cell.Terrain.Count < VertexDim * VertexDim)
                return null;

            var table = DatManager.PortalDat.RegionDesc.LandDefs.LandHeightTable;
            var block = new Block
            {
                Heights = cell.Height.Take(VertexDim * VertexDim).Select(h => table[h]).ToArray(),
                Terrain = cell.Terrain.Take(VertexDim * VertexDim).ToArray(),
                Obstacles = new List<(float, float, float, string)>()
            };

            if (cell.HasObjects)
            {
                var info = DatManager.CellDat.ReadFromDat<LandblockInfo>(id | 0xFFFE);

                if (info != null)
                {
                    foreach (var b in info.Buildings)
                        block.Obstacles.Add((b.Frame.Origin.X, b.Frame.Origin.Y, ModelRadius(b.ModelId, 1f, full: true), "building"));

                    foreach (var o in info.Objects)
                        block.Obstacles.Add((o.Frame.Origin.X, o.Frame.Origin.Y, ModelRadius(o.Id, 1f, full: false), "static object"));
                }
            }

            AddScenery(block, bx, by);

            return block;
        }

        /// <summary>The scenery placement of Physics.Common.Landblock.get_land_scenes, positions and scale only (see the class remarks).</summary>
        private static void AddScenery(Block block, uint bx, uint by)
        {
            var region = DatManager.PortalDat.RegionDesc;
            var blockX = bx * 8;
            var blockY = by * 8;

            for (uint i = 0; i < block.Terrain.Length; i++)
            {
                var terrain = block.Terrain[i];
                var terrainType = terrain >> 2 & 0x1F;
                var sceneType = terrain >> 11;

                var sceneInfo = (int)region.TerrainInfo.TerrainTypes[terrainType].SceneTypes[sceneType];
                var scenes = region.SceneInfo.SceneTypes[sceneInfo].Scenes;

                if (scenes.Count == 0)
                    continue;

                var cellX = i / VertexDim;
                var cellY = i % VertexDim;
                var globalCellX = cellX + blockX;
                var globalCellY = cellY + blockY;

                var cellMat = globalCellY * (712977289 * globalCellX + 1813693831) - 1109124029 * globalCellX + 2139937281;
                var offset = cellMat * 2.3283064e-10;
                var sceneIdx = (int)(scenes.Count * offset);

                if (sceneIdx >= scenes.Count)
                    sceneIdx = 0;

                var scene = DatManager.PortalDat.ReadFromDat<Scene>(scenes[sceneIdx]);

                var cellXMat = -1109124029 * globalCellX;
                var cellYMat = 1813693831 * globalCellY;
                cellMat = 1360117743 * globalCellX * globalCellY + 1888038839;

                for (uint j = 0; j < scene.Objects.Count; j++)
                {
                    var obj = scene.Objects[(int)j];
                    var noise = (uint)(cellXMat + cellYMat - cellMat * (23399 + j)) * 2.3283064e-10;

                    if (!(noise < obj.Freq) || obj.WeenieObj != 0)
                        continue;

                    var position = ACE.Server.Entity.Scenery.Displace(obj, globalCellX, globalCellY, j);
                    var lx = cellX * CellLength + position.X;
                    var ly = cellY * CellLength + position.Y;

                    if (lx < 0 || ly < 0 || lx >= BlockLength || ly >= BlockLength || OnRoad(block.Terrain, lx, ly))
                        continue;

                    var scale = ACE.Server.Entity.Scenery.ScaleObj(obj, globalCellX, globalCellY, j);

                    block.Obstacles.Add((lx, ly, ModelRadius(obj.ObjId, scale, full: false), "tree or rock"));
                }
            }
        }

        /// <summary>
        /// A model's collision radius (dat): a Setup's widest cylinder (a tree trunk), else its spheres, else its radius (whole
        /// for a building, 0.6 of it otherwise); a GfxObj's physics polygons' horizontal extent (0 without physics polygons:
        /// it does not collide). Times <paramref name="scale"/>. 0 when it cannot be read.
        /// </summary>
        private static float ModelRadius(uint modelId, float scale, bool full)
        {
            try
            {
                scale = scale > 0 && float.IsFinite(scale) ? scale : 1f;

                if ((modelId >> 24) == 0x02)
                {
                    var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(modelId);

                    if (setup == null)
                        return 0;

                    if (setup.CylSpheres.Count > 0)
                        return setup.CylSpheres.Max(c => c.Radius) * scale;

                    if (setup.Spheres.Count > 0)
                        return setup.Spheres.Max(s => s.Radius + new Vector2(s.Origin.X, s.Origin.Y).Length()) * scale;

                    return setup.Radius * (full ? 1f : 0.6f) * scale;
                }

                if ((modelId >> 24) == 0x01)
                {
                    var gfx = DatManager.PortalDat.ReadFromDat<GfxObj>(modelId);

                    if (gfx == null || gfx.PhysicsPolygons.Count == 0 || gfx.VertexArray.Vertices.Count == 0)
                        return 0;

                    var extent = gfx.VertexArray.Vertices.Values.Max(v => new Vector2(v.Origin.X, v.Origin.Y).Length());

                    return extent * (full ? 1f : 0.6f) * scale;
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[WORLDEVENT] spawn area: model 0x{modelId:X8} radius could not be read", ex);
            }

            return 0;
        }
    }
}
