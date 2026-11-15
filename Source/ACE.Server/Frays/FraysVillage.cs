using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using ACE.Entity;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>One placed Fray fixture: an arrival NPC, the guide sign, or a village piece.</summary>
    public sealed class FraysPiece
    {
        [JsonPropertyName("w")] public uint Wcid { get; set; }
        [JsonPropertyName("lb")] public ushort Landblock { get; set; }
        /// <summary>The cell; 0 = outdoors, from the position.</summary>
        [JsonPropertyName("c")] public ushort Cell { get; set; }
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        /// <summary>Outdoors: metres above the ground. Indoors: the height in the cell.</summary>
        [JsonPropertyName("z")] public float Z { get; set; }
        /// <summary>Degrees about the vertical (as /rotate: 0 north, 90 west).</summary>
        [JsonPropertyName("f")] public float Facing { get; set; }

        /// <summary>Size (1 = as made; 0 in older saves reads as 1).</summary>
        [JsonPropertyName("s")] public float Scale { get; set; } = 1;
        /// <summary>Outdoors: set on the lowest ground under its footprint (a building on a slope never floats).</summary>
        [JsonPropertyName("k")] public bool Sink { get; set; }

        [JsonIgnore] public bool Outdoor => Cell == 0 || Cell < 0x100;
        [JsonIgnore] public float Size => Scale > 0 ? Scale : 1;
    }

    /// <summary>
    /// The Fray village and arrival fixtures, and the in-game tool that arranges them, so staff can move
    /// pieces around in game. The layout is the frays_village_layout setting (JSON) once anyone
    /// has edited it, else FraysDefs.EntranceNpcs and FraysDefs.Village. Pieces are placed when their landblock loads;
    /// /frays admin piece moves, turns, adds and removes them live and saves the layout, so it stays after restarts.
    /// </summary>
    public static class FraysVillage
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly object sync = new object();
        private static List<FraysPiece> pieces;

        /// <summary>Each placed object (guid) and the piece it shows.</summary>
        private static readonly ConcurrentDictionary<uint, FraysPiece> placed = new ConcurrentDictionary<uint, FraysPiece>();

        /// <summary>Each placed object itself (buildings and their door and window covers), for KeepShown.</summary>
        private static readonly ConcurrentDictionary<uint, WorldObject> shownObjects = new ConcurrentDictionary<uint, WorldObject>();

        static FraysVillage()
        {
            // A house's covers put up again after a hand fix belong to the same piece (clicking one still selects the house).
            FraysHouses.Recovered = (house, covers) =>
            {
                if (placed.TryGetValue(house.Guid.Full, out var p))
                    foreach (var c in covers)
                    {
                        placed[c.Guid.Full] = p;
                        shownObjects[c.Guid.Full] = c;
                    }
            };
        }

        /// <summary>The piece each staff member is arranging (their last selected, moved or added one).</summary>
        private static readonly ConcurrentDictionary<uint, FraysPiece> editing = new ConcurrentDictionary<uint, FraysPiece>();

        /// <summary>Why each staff member's last click selected nothing (said with "Nothing selected").</summary>
        private static readonly ConcurrentDictionary<uint, string> lastMiss = new ConcurrentDictionary<uint, string>();

        private static readonly JsonSerializerOptions json = new JsonSerializerOptions { WriteIndented = false };

        /// <summary>Pure: a facing quaternion's angle about Z in degrees.</summary>
        public static float Degrees(Quaternion q) => (float)(2 * Math.Atan2(q.Z, q.W) * 180 / Math.PI);

        public static Quaternion Facing(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * (float)Math.PI / 180f);

        /// <summary>The built-in layout: the arrival NPCs and the guide sign, then the village.</summary>
        public static List<FraysPiece> Defaults() =>
            FraysDefs.EntranceNpcs.Select(n => new FraysPiece { Wcid = n.Wcid, Landblock = n.Landblock, Cell = n.Cell, X = n.Pos.X, Y = n.Pos.Y, Z = n.Cell >= 0x100 ? n.Pos.Z : 0, Facing = Degrees(n.Facing) })
                .Concat(FraysDefs.Village.Select(v => new FraysPiece { Wcid = v.Wcid, Landblock = v.Landblock, Cell = v.Cell, X = v.Pos.X, Y = v.Pos.Y, Z = v.Cell >= 0x100 ? v.Pos.Z : 0, Facing = v.Facing }))
                .Concat(FraysDefs.TownPieces.Select(t => new FraysPiece { Wcid = t.Wcid, Landblock = t.Landblock, X = t.X, Y = t.Y, Facing = t.Facing, Sink = true }))
                .Concat(FraysDefs.TownDoors.Select(t => new FraysPiece { Wcid = t.Wcid, Landblock = t.Landblock, X = t.X, Y = t.Y, Z = t.Z, Facing = t.Facing }))
                .Concat(FraysDefs.Hangings.Select(t => new FraysPiece { Wcid = t.Wcid, Landblock = t.Landblock, X = t.X, Y = t.Y, Z = t.Z, Facing = t.Facing }))
                .ToList();

        /// <summary>The current layout (loaded once from the setting, else the defaults).</summary>
        public static List<FraysPiece> Pieces
        {
            get
            {
                lock (sync)
                {
                    if (pieces != null)
                        return pieces;

                    var saved = FraysSettings.Text(FraysSettings.VillageLayout);

                    if (saved.Length > 2)
                    {
                        try
                        {
                            pieces = JsonSerializer.Deserialize<List<FraysPiece>>(saved, json);
                        }
                        catch (Exception ex)
                        {
                            log.Error("[FRAYS] frays_village_layout could not be read; using the built-in layout", ex);
                        }
                    }

                    pieces ??= Defaults();
                    return pieces;
                }
            }
        }

        /// <summary>The landblock each piece was last shown in (for live moves).</summary>
        private static readonly ConcurrentDictionary<FraysPiece, Landblock> shownIn = new ConcurrentDictionary<FraysPiece, Landblock>(ReferenceEqualityComparer.Instance);

        private static System.Threading.Timer liveTimer;
        private static DateTime liveStamp;

        /// <summary>One live change: the piece at index I of the layout (its wcid W must match), with any of its fields set.</summary>
        public sealed class LiveEdit
        {
            [JsonPropertyName("i")] public int Index { get; set; }
            [JsonPropertyName("w")] public uint Wcid { get; set; }
            [JsonPropertyName("x")] public float? X { get; set; }
            [JsonPropertyName("y")] public float? Y { get; set; }
            [JsonPropertyName("z")] public float? Z { get; set; }
            [JsonPropertyName("f")] public float? Facing { get; set; }
            [JsonPropertyName("s")] public float? Scale { get; set; }

            /// <summary>Takes the piece away (its index and wcid must match).</summary>
            [JsonPropertyName("remove")] public bool Remove { get; set; }

            /// <summary>A new piece (index ignored): wcid W in landblock Lb, cell C (0 outdoors), at x, y, z, facing f.</summary>
            [JsonPropertyName("add")] public bool Add { get; set; }
            [JsonPropertyName("lb")] public ushort Landblock { get; set; }
            [JsonPropertyName("c")] public ushort Cell { get; set; }
        }

        /// <summary>
        /// Live piece tuning, so a chest nudge needs no restart:
        /// frays-pieces-live.json beside the server, a list of LiveEdit, is read each second when it changes.
        /// Each edited piece is shown again where it now is, and the layout is saved through the server, so its own copy
        /// stays right.
        /// </summary>
        private static void WatchLive()
        {
            if (liveTimer != null)
                return;

            liveTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var path = System.IO.Path.Combine(AppContext.BaseDirectory, "frays-pieces-live.json");
                    if (!System.IO.File.Exists(path))
                        return;

                    var stamp = System.IO.File.GetLastWriteTimeUtc(path);
                    if (stamp == liveStamp)
                        return;

                    liveStamp = stamp;
                    var edits = JsonSerializer.Deserialize<List<LiveEdit>>(System.IO.File.ReadAllText(path));
                    var changed = new List<FraysPiece>();
                    var removed = new List<FraysPiece>();
                    var added = new List<FraysPiece>();
                    string text;

                    lock (sync)
                    {
                        // Removes go last and by reference, so the indexes of the other edits in the file still match.
                        foreach (var e in edits.Where(e => e.Add))
                        {
                            var p = new FraysPiece { Wcid = e.Wcid, Landblock = e.Landblock, Cell = e.Cell, X = e.X ?? 0, Y = e.Y ?? 0, Z = e.Z ?? 0, Facing = e.Facing ?? 0, Scale = e.Scale ?? 1 };
                            Pieces.Add(p);
                            added.Add(p);
                        }

                        foreach (var e in edits.Where(e => e.Remove))
                        {
                            if (e.Index >= 0 && e.Index < Pieces.Count && Pieces[e.Index].Wcid == e.Wcid)
                                removed.Add(Pieces[e.Index]);
                            else
                                log.Warn($"[FRAYS] live piece remove {e.Index} skipped: wcid {e.Wcid} is not there");
                        }

                        foreach (var e in edits.Where(e => !e.Add && !e.Remove))
                        {
                            if (e.Index < 0 || e.Index >= Pieces.Count || Pieces[e.Index].Wcid != e.Wcid)
                            {
                                log.Warn($"[FRAYS] live piece edit {e.Index} skipped: wcid {e.Wcid} is not there");
                                continue;
                            }

                            var p = Pieces[e.Index];
                            p.X = e.X ?? p.X;
                            p.Y = e.Y ?? p.Y;
                            p.Z = e.Z ?? p.Z;
                            p.Facing = e.Facing ?? p.Facing;
                            p.Scale = e.Scale ?? p.Scale;
                            changed.Add(p);
                        }

                        foreach (var p in removed)
                            Pieces.Remove(p);

                        text = JsonSerializer.Serialize(Pieces, json);
                    }

                    foreach (var p in removed)
                    {
                        if (!shownIn.TryRemove(p, out var landblock))
                            continue;

                        var lb = landblock;
                        var chain = new ACE.Server.Entity.Actions.ActionChain();
                        chain.AddAction(lb, () => Unplace(p, lb));
                        chain.EnqueueChain();
                        log.Info($"[FRAYS] live piece removed: wcid {p.Wcid} at {p.X:0.00}, {p.Y:0.00}");
                    }

                    foreach (var p in added)
                    {
                        var other = shownIn.Values.FirstOrDefault(l => l.Id.Landblock == p.Landblock);

                        if (other == null)
                        {
                            log.Info($"[FRAYS] live piece added: wcid {p.Wcid} (shown when 0x{p.Landblock:X4} loads)");
                            continue;
                        }

                        var chain = new ACE.Server.Entity.Actions.ActionChain();
                        chain.AddAction(other, () => Place(other, p, exact: true));
                        chain.EnqueueChain();
                        log.Info($"[FRAYS] live piece added: wcid {p.Wcid} at {p.X:0.00}, {p.Y:0.00}, cell 0x{p.Cell:X}");
                    }

                    foreach (var p in changed)
                    {
                        if (!shownIn.TryGetValue(p, out var landblock))
                            continue;

                        var lb = landblock;
                        var chain = new ACE.Server.Entity.Actions.ActionChain();
                        chain.AddAction(lb, () => Unplace(p, lb));
                        chain.AddDelaySeconds(0.5);
                        chain.AddAction(lb, () => Place(lb, p, exact: true));
                        chain.EnqueueChain();
                        log.Info($"[FRAYS] live piece edit: wcid {p.Wcid} now at {p.X:0.00}, {p.Y:0.00}, z {p.Z:0.00}, facing {p.Facing:0.0}");
                    }

                    if (FraysSettings.TryFind(FraysSettings.VillageLayout, out var setting))
                        ACE.Server.Managers.PropertyAdminService.TryModify(ACE.Server.Managers.PropertyAdminService.TypeString, setting.PropertyName, text, ACE.Server.Managers.PropertyAdminActor.Web("frays live tuning"));

                    // Settings reach the database a little later, so a stop right after an edit could lose it: the
                    // layout as it now is, beside the server, to put back before the next start.
                    System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "frays-layout-last.json"), text);
                }
                catch (Exception ex)
                {
                    log.Warn("[FRAYS] frays-pieces-live.json could not be applied", ex);
                }
            }, null, 2000, 1000);
        }

        /// <summary>A landblock loaded: places its pieces.</summary>
        public static void PlaceAll(Landblock landblock)
        {
            WatchLive();
            List<FraysPiece> mine;

            lock (sync)
                mine = Pieces.Where(p => p.Landblock == landblock.Id.Landblock).ToList();

            foreach (var p in mine)
                Place(landblock, p, exact: false);
        }

        /// <summary>
        /// The drawn footprint of each building piece (its model's x and y bounds, before turning and scaling), measured from
        /// the cell dat. Arwic's wall, gate and tower models are drawn well off their own centre.
        /// </summary>
        private static readonly Dictionary<uint, (float MinX, float MaxX, float MinY, float MaxY)> Footprints = new Dictionary<uint, (float, float, float, float)>
        {
            [FraysDefs.ArwicCottage] = (-11.2f, 12.0f, -11.0f, 12.0f), [FraysDefs.ArwicHouse] = (-6.0f, 7.6f, -6.1f, 5.2f),
            [FraysDefs.ArwicManor] = (-10.0f, 9.0f, -12.0f, 11.5f), [FraysDefs.ArwicShop] = (-12.0f, 8.0f, -9.0f, 9.0f),
            [FraysDefs.ArwicHall] = (-4.8f, 7.2f, -7.5f, 6.1f), [FraysDefs.ArwicTower] = (-12.0f, -5.0f, -12.0f, -5.0f),
            [FraysDefs.ArwicWall] = (-12.0f, 12.0f, -9.9f, -6.4f), [FraysDefs.ArwicGateA] = (-12.0f, 12.0f, 4.9f, 9.9f),
            [FraysDefs.ArwicGateB] = (-12.0f, 12.0f, 4.9f, 9.9f), [FraysDefs.ArwicGreatHall] = (-11.7f, 11.7f, -11.8f, 11.7f),
            [FraysDefs.ArwicChapel] = (-6.3f, 9.6f, -5.3f, 5.1f), [FraysDefs.ArwicWallB] = (-12.0f, 12.0f, -12.1f, -6.4f),
            [FraysDefs.ArwicWallC] = (-12.0f, 12.0f, -12.1f, -6.4f), [FraysDefs.ArwicGatehouse] = (-12.0f, 12.0f, -12.0f, -5.0f),
            [FraysDefs.ArwicWallD] = (-12.0f, 12.0f, -9.9f, -6.4f),
        };

        /// <summary>How far below the lowest ground a sunk piece's base goes. 0: the village ground is flat at 0 m and the models'
        /// bases sit at their origin, so any sink buried the floors (the Great Hall must sit flat on the floor).</summary>
        private const float SinkDepth = 0f;

        /// <summary>The height every city wall piece's base stands at outdoors: the flat shore round the temple, exactly 0 m.</summary>
        public const float WallBase = 0f;   // was -0.5, which sank the walls

        /// <summary>
        /// Outdoors, for a sinking piece: metres from the ground at its origin to the lowest ground under its footprint (0 or
        /// less), minus SinkDepth. Without it, on a slope one side of a build floated.
        /// </summary>
        private static float SinkOffset(Landblock landblock, FraysPiece p)
        {
            if (!p.Sink || !p.Outdoor)
                return 0;

            var (mnx, mxx, mny, mxy) = Footprints.TryGetValue(p.Wcid, out var f) ? f : FraysHouses.TryFootprint(p.Wcid, out var hf) ? hf : (-2f, 2f, -2f, 2f);
            var a = p.Facing * Math.PI / 180;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a), k = p.Size;

            float Ground(float x, float y)
            {
                var pos = new Position(((uint)p.Landblock << 16) | FraysRules.OutdoorCell(true, 0, new Vector3(x, y, 0)), x, y, 0, 0, 0, 0, 1, landblock.Instance);
                return ACE.Server.Entity.PositionExtensions.GetTerrainZ(pos);
            }

            var at = Ground(p.X, p.Y);

            // City walls all stand on one level (the flat shore, 0 m) so their walkways and ends meet: sinking each to its own
            // lowest ground left steps and gaps between them on the hill side.
            if (FraysHouses.IsWall(p.Wcid) || p.Wcid == FraysDefs.ArwicWall || p.Wcid == FraysDefs.ArwicTower)
                return WallBase - at;

            var low = at;

            for (var i = 0; i <= 4; i++)
                for (var j = 0; j <= 4; j++)
                {
                    float lx = mnx + (mxx - mnx) * i / 4, ly = mny + (mxy - mny) * j / 4;
                    float wx = Math.Clamp(p.X + k * (lx * c - ly * s), 0.1f, 191.9f), wy = Math.Clamp(p.Y + k * (lx * s + ly * c), 0.1f, 191.9f);
                    low = Math.Min(low, Ground(wx, wy));
                }

            return low - at - SinkDepth;
        }

        private static WorldObject Place(Landblock landblock, FraysPiece p, bool exact)
        {
            var cell = p.Cell >= 0x100 ? p.Cell : FraysRules.OutdoorCell(true, 0, new Vector3(p.X, p.Y, 0));
            // Buildings, walls and towers can't be clicked, and so are their fake doors and windows; portals, chests and NPCs still can.
            var building = Footprints.ContainsKey(p.Wcid) || FraysHouses.IsHouse(p.Wcid);

            var wo = FraysManager.PlaceFixture(landblock, p.Wcid, cell, new Vector3(p.X, p.Y, p.Cell >= 0x100 ? p.Z : 0), Facing(p.Facing), exact,
                p.Cell >= 0x100 ? 0 : p.Z + SinkOffset(landblock, p), p.Size, asPlaced: p.Sink && p.Outdoor, uiHidden: building, prepare: FraysMonument.Prepare);

            if (wo != null)
            {
                placed[wo.Guid.Full] = p;
                shownIn[p] = landblock;
                shownObjects[wo.Guid.Full] = wo;

                // A town house: its doors and windows covered (clicking a cover selects the house).
                // The covers are made unclickable as they are placed (FraysHouses.PlaceCovers).
                foreach (var cover in FraysHouses.PlaceCovers(landblock, wo, p.Facing, p.Size))
                {
                    placed[cover.Guid.Full] = p;
                    shownObjects[cover.Guid.Full] = cover;
                }

                // The Snarl Monument: its ring of stones belongs to its piece (they move and go with it).
                if (FraysMonument.Is(p.Wcid))
                    foreach (var stone in FraysMonument.PlaceStones(landblock, p))
                    {
                        placed[stone.Guid.Full] = p;
                        shownObjects[stone.Guid.Full] = stone;
                    }
            }

            return wo;
        }

        /// <summary>A Snarl Trophy dropped: every shown Snarl Monument is put up again, so its stones carry the new names.</summary>
        public static void RefreshMonuments()
        {
            foreach (var (p, landblock) in shownIn.Where(kv => FraysMonument.Is(kv.Key.Wcid)).Select(kv => (kv.Key, kv.Value)).ToList())
            {
                var lb = landblock;
                var chain = new ACE.Server.Entity.Actions.ActionChain();
                chain.AddAction(lb, () => Unplace(p, lb));
                chain.AddDelaySeconds(0.5);
                chain.AddAction(lb, () => Place(lb, p, exact: true));
                chain.EnqueueChain();
            }
        }

        // ---- keeping the village shown --------------------------------------------------------------------------

        /// <summary>Per player: the village objects they are near, where they were last tick, and when to look again.</summary>
        private sealed class Viewer
        {
            public readonly HashSet<uint> Near = new HashSet<uint>();
            public Position Last;
            public double WaitUntil;
        }

        private static readonly ConcurrentDictionary<uint, Viewer> viewers = new ConcurrentDictionary<uint, Viewer>();
        private static double nextKeepShown;

        /// <summary>Within this many metres an object counts as near (it is re-sent as it comes near).</summary>
        public const float NearRange = 80f;

        /// <summary>Past this many metres an object counts as left behind (the game may have dropped it).</summary>
        public const float FarRange = 150f;

        /// <summary>
        /// The game drops an object it thinks it can no longer see and the server does not always notice, so a house's
        /// door or window (or a whole building) could vanish until relog (the cottage door
        /// kept disappearing). Every 2 seconds, each player near the village is sent again any village object that has
        /// just come near: after a teleport or login, and whenever they walk back to something they had left behind.
        /// Objects already near are never re-sent, so nothing flickers while you stand or walk about.
        /// </summary>
        public static void KeepShown(double now)
        {
            if (now < nextKeepShown || shownObjects.IsEmpty)
                return;

            nextKeepShown = now + 2;

            try
            {
                var blocks = shownIn.Values.Select(l => l.Id.Landblock).ToHashSet();

                foreach (var player in PlayerManager.GetAllOnline())
                {
                    var at = player.Location;

                    if (at == null || !blocks.Any(b => Math.Abs((b >> 8) - (at.LandblockId.Landblock >> 8)) <= 1 && Math.Abs((b & 0xFF) - (at.LandblockId.Landblock & 0xFF)) <= 1))
                    {
                        viewers.TryRemove(player.Guid.Full, out _);
                        continue;
                    }

                    var v = viewers.GetOrAdd(player.Guid.Full, _ => new Viewer { WaitUntil = now + 3 });

                    // A jump (teleport, portal, login): start over once the game has settled.
                    if (v.Last != null && v.Last.DistanceTo(at) > 60)
                    {
                        v.Near.Clear();
                        v.WaitUntil = now + 3;
                    }

                    v.Last = new Position(at);

                    if (now < v.WaitUntil)
                        continue;

                    foreach (var (guid, wo) in shownObjects)
                    {
                        if (wo == null || wo.IsDestroyed || wo.Location == null || wo.PhysicsObj == null)
                            continue;

                        // Only this player's own copy of the village (a realm has its own instance of the same landblocks).
                        if (wo.Location.Instance != at.Instance)
                            continue;

                        var d = at.DistanceTo(wo.Location);

                        if (d > FarRange)
                            v.Near.Remove(guid);
                        else if (d < NearRange)
                        {
                            // The server had dropped it from what this player knows (the cottage
                            // door went missing): only objects it still knew were re-sent, so a dropped door stayed gone
                            // until relog. It is known again and sent, whenever that happens, even while standing by it.
                            if (player.ObjMaint.AddKnownObject(wo.PhysicsObj))
                            {
                                v.Near.Add(guid);
                                player.TrackObject(wo);
                            }
                            else if (v.Near.Add(guid))
                                player.TrackObject(wo, resend: true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] keeping the village shown threw", ex);
            }
        }

        /// <summary>Pure: a village piece's kind for the check, from its weenie type.</summary>
        public static string KindOf(ACE.Entity.Enum.WeenieType type) => type switch
        {
            ACE.Entity.Enum.WeenieType.Creature or ACE.Entity.Enum.WeenieType.Vendor => "NPC",
            ACE.Entity.Enum.WeenieType.Chest or ACE.Entity.Enum.WeenieType.Container => "chest",
            ACE.Entity.Enum.WeenieType.Portal => "portal",
            ACE.Entity.Enum.WeenieType.Book => "sign",
            _ => "decor",
        };

        /// <summary>
        /// /frays admin village check: every piece the layout expects - NPCs, chests, portals, signs
        /// and decor - and whether it is standing now. A piece whose landblock is not loaded cannot be checked.
        /// </summary>
        public static string Check()
        {
            List<FraysPiece> all;

            lock (sync)
                all = Pieces.ToList();

            var rows = new List<(string Kind, string Line, int State)>();   // State: 0 standing, 1 missing, 2 not loaded

            foreach (var p in all)
            {
                var weenie = ACE.Database.DatabaseManager.World.GetCachedWeenie(p.Wcid);
                var kind = FraysMonument.Is(p.Wcid) ? "sign" : weenie == null ? "decor" : KindOf(weenie.WeenieType);
                var name = weenie?.GetName() ?? $"wcid {p.Wcid}";

                var standing = placed.Where(kv => ReferenceEquals(kv.Value, p))
                    .Any(kv => shownObjects.TryGetValue(kv.Key, out var wo) && !wo.IsDestroyed && wo.CurrentLandblock != null);
                var loaded = standing || shownIn.ContainsKey(p) || LandblockManager.IsLoaded(new LandblockId(((uint)p.Landblock << 16) | 0xFFFF), 0);
                var state = standing ? 0 : loaded ? 1 : 2;
                var where = $"0x{p.Landblock:X4}{(p.Cell >= 0x100 ? $" cell {p.Cell:X4}" : "")} ({p.X:0.0}, {p.Y:0.0})";

                // The Snarl Monument: how many of its stones stand too.
                var extra = "";
                if (FraysMonument.Is(p.Wcid))
                {
                    var stones = placed.Where(kv => ReferenceEquals(kv.Value, p)).Count(kv => shownObjects.TryGetValue(kv.Key, out var s) && FraysMonument.IsStone(s.WeenieClassId) && !s.IsDestroyed && s.CurrentLandblock != null);
                    var want = FraysSettings.Int(FraysSettings.MonumentStones) + (FraysSettings.Toggle(FraysSettings.MonumentChampion) ? 1 : 0);   // and the Champion's stone
                    extra = $", {stones} of {want} stones standing";
                    if (state == 0 && stones < want)
                        state = 1;
                }

                rows.Add((kind, $"  {(state == 0 ? "OK     " : state == 1 ? "MISSING" : "unloaded")} {name} ({p.Wcid}) at {where}{extra}", state));
            }

            var lines = new List<string>
            {
                $"Village check: {all.Count} pieces expected, {rows.Count(r => r.State == 0)} standing, {rows.Count(r => r.State == 1)} missing, {rows.Count(r => r.State == 2)} in a landblock that is not loaded.",
            };

            foreach (var kind in new[] { "NPC", "chest", "portal", "sign", "decor" })
            {
                var mine = rows.Where(r => r.Kind == kind).ToList();

                if (mine.Count == 0)
                    continue;

                lines.Add($"{kind}s: {mine.Count(r => r.State == 0)} of {mine.Count} standing.");

                // Every NPC, chest, portal and sign is listed; decor only when it is not standing (there are a lot of it).
                foreach (var r in mine.OrderBy(r => r.State == 0).ThenBy(r => r.Line))
                    if (kind != "decor" || r.State != 0)
                        lines.Add(r.Line);
            }

            return string.Join("\n", lines);
        }

        private static void Unplace(FraysPiece p, Landblock landblock)
        {
            foreach (var guid in placed.Where(kv => ReferenceEquals(kv.Value, p)).Select(kv => kv.Key).ToList())
            {
                placed.TryRemove(guid, out _);
                shownObjects.TryRemove(guid, out _);

                if (landblock.GetObject(new ObjectGuid(guid)) is WorldObject wo && !wo.IsDestroyed)
                    wo.Destroy();
            }
        }

        private static string Save(Session session)
        {
            string text;

            lock (sync)
                text = JsonSerializer.Serialize(Pieces, json);

            return FraysAdmin.SetRaw(FraysSettings.VillageLayout, text, session);
        }

        private static string Describe(FraysPiece p, Player player)
        {
            var name = ACE.Database.DatabaseManager.World.GetCachedWeenie(p.Wcid)?.GetName() ?? $"wcid {p.Wcid}";
            return $"{name} (wcid {p.Wcid}) in 0x{p.Landblock:X4} at {p.X:0.0}, {p.Y:0.0}{(p.Z != 0 ? $", {(p.Outdoor ? "+" : "")}{p.Z:0.0} up" : "")}, facing {p.Facing:0} degrees{(Math.Abs(p.Size - 1) > 0.001f ? $", size {p.Size:0.##}" : "")}";
        }

        private static readonly Dictionary<string, Vector2> Dirs = new Dictionary<string, Vector2>
        {
            ["n"] = new Vector2(0, 1), ["north"] = new Vector2(0, 1), ["s"] = new Vector2(0, -1), ["south"] = new Vector2(0, -1),
            ["e"] = new Vector2(1, 0), ["east"] = new Vector2(1, 0), ["w"] = new Vector2(-1, 0), ["west"] = new Vector2(-1, 0),
            ["ne"] = Vector2.Normalize(new Vector2(1, 1)), ["northeast"] = Vector2.Normalize(new Vector2(1, 1)),
            ["nw"] = Vector2.Normalize(new Vector2(-1, 1)), ["northwest"] = Vector2.Normalize(new Vector2(-1, 1)),
            ["se"] = Vector2.Normalize(new Vector2(1, -1)), ["southeast"] = Vector2.Normalize(new Vector2(1, -1)),
            ["sw"] = Vector2.Normalize(new Vector2(-1, -1)), ["southwest"] = Vector2.Normalize(new Vector2(-1, -1)),
        };

        public const string Usage =
            "--- Fray village tool ---\n"
            + "Stand by a piece (within 4 m), then:\n"
            + "  /frays admin piece - select the piece nearest you, and say what it is and where\n"
            + "  /frays admin piece move <n|s|e|w|ne|nw|se|sw|up|down> [metres] - move it (1 m if no number)\n"
            + "  /frays admin piece move here - move it to where you stand\n"
            + "  /frays admin piece turn <degrees> - turn it (90 = a quarter turn left)\n"
            + "  /frays admin piece size <number> - its size (1 normal, 2 twice as big)\n"
            + "  /frays admin piece face <degrees> - face a direction (0 north, 90 west, 180 south, 270 east)\n"
            + "  /frays admin piece face me - face it toward you\n"
            + "  /frays admin piece add <wcid> - a new piece at your feet, facing your way\n"
            + "  /frays admin piece copy - a copy of the selected piece at your feet\n"
            + "  /frays admin piece remove - remove the selected piece\n"
            + "  /frays admin piece reset - back to the built-in layout (after a restart)\n"
            + "  /frays admin village check - every expected NPC, chest, portal, sign and decor piece, and whether it is standing\n"
            + "  /frays admin piece clear town - take away every building and wall (the camp and decor stay)\n"
            + "Village pieces: 2000096 cookfire, 97 brazier, 98 torch, 99 well, 100 banner, 101 barrel, 102 crate, 103 bench, 104 table, 105 cart, 106 bedroll, 107 cottage, 108 house, 109 manor, 110 shop, 111 hall, 112 watchtower, 113 wall, 114 and 115 gate.\n"
            + "Town houses, doors and windows covered: 2000124 to 2000140 (/frays admin houses shows them all).\n"
            + "The Snarl Trophy Wall sign: 2000194 (put it in the Great Hall).\n"
            + "The Sky Steward (sells for Sky Seals): 2000200 (put it on the Sky Fray plateau).\n"
            + "The Snarl Monument and its ring of finders' stones: 2000201 (put it in the square by the braziers).\n"
            + "Every change saves at once and stays after restarts.";

        /// <summary>/frays admin piece ...</summary>
        public static string Command(Session session, Player player, string[] a)
        {
            var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "";

            if (sub == "help" || sub == "?")
                return Usage;

            if (sub == "check")
                return Check();

            if (sub == "reset")
            {
                lock (sync)
                    pieces = null;

                var said = FraysAdmin.SetRaw(FraysSettings.VillageLayout, "[]", session);
                return said ?? "The village layout is back to the built-in one; it shows after a restart (or when the area next loads).";
            }

            // Every building and wall taken away at once, the
            // camp, NPCs and decor kept, so the ground is clear to lay a new village out.
            if (sub == "clear" && a.Length > 1 && a[1].Equals("town", StringComparison.OrdinalIgnoreCase))
            {
                List<FraysPiece> town;

                lock (sync)
                    town = Pieces.Where(t => t.Sink).ToList();

                foreach (var t in town)
                {
                    var block = LandblockManager.GetLandblock(new LandblockId(((uint)t.Landblock << 16) | 0xFFFF), player.Location.Instance, false);

                    if (block != null)
                        Unplace(t, block);
                }

                lock (sync)
                    Pieces.RemoveAll(t => t.Sink);

                return Save(session) ?? $"Took away {town.Count} buildings and walls (with their doors and windows). The camp, NPCs and decor stay.";
            }

            var landblock = player.CurrentLandblock;

            if (landblock == null)
                return "You are not in the world.";

            if (sub == "add" || sub == "copy")
            {
                uint wcid;

                if (sub == "copy")
                {
                    if (Selected(session, player) is not FraysPiece from)
                        return "Stand by a village piece (within 4 m), then /frays admin piece copy.";
                    wcid = from.Wcid;
                }
                else if (a.Length < 2 || !uint.TryParse(a[1], out wcid))
                    return "Use: /frays admin piece add <wcid>. " + Usage.Split('\n').Last(l => l.StartsWith("Village pieces"));

                var pos = player.Location;
                var piece = new FraysPiece
                {
                    Wcid = wcid,
                    Landblock = (ushort)(pos.LandblockId.Raw >> 16),
                    Cell = (pos.Cell & 0xFFFF) >= 0x100 ? (ushort)(pos.Cell & 0xFFFF) : (ushort)0,
                    X = pos.PositionX,
                    Y = pos.PositionY,
                    Z = (pos.Cell & 0xFFFF) >= 0x100 ? pos.PositionZ : 0,
                    Facing = Degrees(new Quaternion(pos.RotationX, pos.RotationY, pos.RotationZ, pos.RotationW)),
                    // A building piece is set on its lowest ground, like the town's.
                    Scale = 1,
                    Sink = Footprints.ContainsKey(wcid) || FraysHouses.IsHouse(wcid),
                };

                // "front" (in front of where you stand, placed against the wall): the piece turned to
                // face you and pushed out ahead of you until its near edge is 1.5 m away, so it backs onto what you face.
                if (a.Length > 2 && a[2].Equals("front", StringComparison.OrdinalIgnoreCase))
                {
                    var (mnx, mxx, mny, mxy) = Footprints.TryGetValue(wcid, out var fp) ? fp : FraysHouses.TryFootprint(wcid, out var hfp) ? hfp : (-2f, 2f, -2f, 2f);
                    var turn = piece.Facing * Math.PI / 180;
                    var ahead = new Vector2((float)-Math.Sin(turn), (float)Math.Cos(turn));   // where you look (facing 0 = north)
                    var x = pos.PositionX + ahead.X * (mxy + 1.5f);
                    var y = pos.PositionY + ahead.Y * (mxy + 1.5f);

                    if (x < 0.5f || x > 191.5f || y < 0.5f || y > 191.5f)
                        return "That would put it over the landblock edge. Step a little way along and try again.";

                    piece.X = x;
                    piece.Y = y;
                    piece.Facing = Normal(piece.Facing + 180);   // its local north (+Y) side toward you
                }

                if (Place(landblock, piece, exact: true) == null)
                    return $"Weenie {wcid} could not be placed here.";

                lock (sync)
                    Pieces.Add(piece);

                editing[player.Guid.Full] = piece;
                return Save(session) ?? $"Added: {Describe(piece, player)}. It is selected.";
            }

            // The bare command (and info) picks again from scratch, nearest first: the last piece worked on otherwise stays
            // selected within 6 m, which kept grabbing the wrong one of
            // two close Dream Chests.
            // "fetch <wcid>" (for example, move the Drift Network portal to you): the nearest piece of that weenie in
            // this or a neighbouring landblock is selected and moved to your feet, however far it is.
            FraysPiece fetched = null;

            if (sub == "fetch")
            {
                if (a.Length < 2 || !uint.TryParse(a[1], out var fw))
                    return "Use: /frays admin piece fetch <wcid> - the nearest piece of that weenie comes to you.";

                var hereLb = (ushort)(player.Location.LandblockId.Raw >> 16);

                lock (sync)
                    fetched = Pieces.Where(t => t.Wcid == fw && t.Outdoor && Math.Abs((t.Landblock >> 8) - (hereLb >> 8)) <= 1 && Math.Abs((t.Landblock & 0xFF) - (hereLb & 0xFF)) <= 1)
                        .OrderBy(t =>
                        {
                            var dx = player.Location.PositionX + ((hereLb >> 8) - (t.Landblock >> 8)) * 192f - t.X;
                            var dy = player.Location.PositionY + ((hereLb & 0xFF) - (t.Landblock & 0xFF)) * 192f - t.Y;
                            return dx * dx + dy * dy;
                        })
                        .FirstOrDefault();

                if (fetched == null)
                    return $"No outdoor piece of weenie {fw} is in this landblock or the ones next to it.";

                editing[player.Guid.Full] = fetched;
                sub = "move";
                a = new[] { "move", "here" };
            }

            if ((fetched ?? Selected(session, player, repick: sub == "" || sub == "info" || sub == "select")) is not FraysPiece p)
                return "Nothing selected. " + (lastMiss.TryRemove(player.Guid.Full, out var why) ? why + " " : "") + "/frays admin piece help lists them all.";

            // The piece can be changed from a nearby landblock too (standing just over the line from a house).
            if (p.Landblock != landblock.Id.Landblock)
            {
                var own = new LandblockId(((uint)p.Landblock << 16) | 0xFFFF);

                if (!LandblockManager.IsLoaded(own, player.Location.Instance))
                    return $"Walk closer to the piece (it is in landblock 0x{p.Landblock:X4}) to change it.";

                landblock = LandblockManager.GetLandblock(own, player.Location.Instance, false);
            }

            // Where the player stands, in the piece's landblock's frame (outdoor landblocks are 192 m apart).
            var playerLb = (ushort)(player.Location.LandblockId.Raw >> 16);
            var meX = player.Location.PositionX + ((playerLb >> 8) - (p.Landblock >> 8)) * 192f;
            var meY = player.Location.PositionY + ((playerLb & 0xFF) - (p.Landblock & 0xFF)) * 192f;

            // Pieces stacked on the same spot go along (such as a Portal Ring under the Drift Network portal):
            // a move or turn of one moves and turns the others the same way.
            List<FraysPiece> stacked;
            lock (sync)
                stacked = Pieces.Where(t => !ReferenceEquals(t, p) && t.Landblock == p.Landblock && t.Cell == p.Cell
                    && Math.Abs(t.X - p.X) < 0.3f && Math.Abs(t.Y - p.Y) < 0.3f).ToList();
            var startBlock = landblock;
            float startX = p.X, startY = p.Y, startZ = p.Z, startFacing = p.Facing;
            string Along(string said)
            {
                foreach (var t in stacked)
                {
                    Unplace(t, startBlock);
                    t.Landblock = p.Landblock;
                    t.Cell = p.Cell >= 0x100 ? p.Cell : t.Cell;
                    t.X = p.X + (t.X - startX);   // p.X is in p's (maybe new) landblock's frame
                    t.Y = p.Y + (t.Y - startY);
                    t.Z += p.Z - startZ;
                    t.Facing = Normal(t.Facing + (p.Facing - startFacing));
                    Replace(session, player, landblock, t, "Moved");
                }

                editing[player.Guid.Full] = p;
                return stacked.Count == 0 ? said : $"{said} (and {string.Join(", ", stacked.Select(t => Describe(t, player)))} on the same spot)";
            }

            switch (sub)
            {
                case "":
                case "info":
                case "select":
                    return "Selected: " + Describe(p, player);

                case "remove":
                case "delete":
                    Unplace(p, landblock);
                    lock (sync)
                        Pieces.Remove(p);
                    editing.TryRemove(player.Guid.Full, out _);
                    return Save(session) ?? $"Removed: {Describe(p, player)}.";

                case "move":
                {
                    var dir = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                    var metres = a.Length > 2 && float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 1f;

                    if (dir == "here")
                    {
                        var pos = player.Location;

                        if (playerLb != p.Landblock && (meX < 0.5f || meX > 191.5f || meY < 0.5f || meY > 191.5f))
                        {
                            // Into another landblock: an outdoor piece moves over to the player's (such as the guide
                            // sign moved out of the temple's landblock).
                            if (!p.Outdoor || player.CurrentLandblock == null)
                                return $"Stand in the piece's own landblock (0x{p.Landblock:X4}) to move it here.";

                            Unplace(p, landblock);
                            landblock = player.CurrentLandblock;
                            p.Landblock = playerLb;
                            p.Cell = 0;
                            meX = pos.PositionX;
                            meY = pos.PositionY;
                        }

                        p.X = meX;
                        p.Y = meY;

                        if (p.Cell >= 0x100)
                        {
                            p.Cell = (ushort)(pos.Cell & 0xFFFF);
                            p.Z = pos.PositionZ;
                        }
                    }
                    else if (dir == "up" || dir == "down")
                        p.Z += dir == "up" ? metres : -metres;
                    else if (Dirs.TryGetValue(dir, out var d))
                    {
                        var nx = p.X + d.X * metres;
                        var ny = p.Y + d.Y * metres;

                        if (p.Outdoor && (nx < 0.5f || nx > 191.5f || ny < 0.5f || ny > 191.5f))
                            return "That would leave the landblock. Stand where you want it and use /frays admin piece move here.";

                        p.X = nx;
                        p.Y = ny;
                    }
                    else
                        return "Use: /frays admin piece move <n|s|e|w|ne|nw|se|sw|up|down> [metres], or move here.";

                    return Along(Replace(session, player, landblock, p, "Moved"));
                }

                case "turn":
                    if (a.Length < 2 || !float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var by))
                        return "Use: /frays admin piece turn <degrees> (90 = a quarter turn left, -90 right).";
                    p.Facing = Normal(p.Facing + by);
                    return Along(Replace(session, player, landblock, p, "Turned"));

                // Resize a piece (for example the lifestone).
                case "size":
                case "scale":
                    if (a.Length < 2 || !float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size) || size < 0.1f || size > 10f)
                        return "Use: /frays admin piece size <0.1 to 10> (1 = normal size, 2 = twice as big).";
                    p.Scale = size;
                    return Replace(session, player, landblock, p, "Resized");

                case "face":
                {
                    if (a.Length > 1 && a[1].Equals("me", StringComparison.OrdinalIgnoreCase))
                    {
                        // Toward the player: atan2 of the offset, in the same frame as Facing (0 north, counter-clockwise).
                        var dx = meX - p.X;
                        var dy = meY - p.Y;
                        p.Facing = Normal((float)(Math.Atan2(-dx, dy) * 180 / Math.PI));
                    }
                    else if (a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var to))
                        p.Facing = Normal(to);
                    else
                        return "Use: /frays admin piece face <degrees> (0 north, 90 west, 180 south, 270 east) or face me.";

                    return Along(Replace(session, player, landblock, p, "Turned"));
                }

                default:
                    return Usage;
            }
        }

        private static float Normal(float deg) => ((deg % 360) + 360) % 360;

        /// <summary>Shows a changed piece again (the old object goes, a new one comes) and saves.</summary>
        private static string Replace(Session session, Player player, Landblock landblock, FraysPiece p, string what)
        {
            Unplace(p, landblock);

            // The new object comes half a second later (placed at once, it could take the old one's freed guid,
            // and the old one's delete reached the client after the new one's create - the piece vanished).
            var chain = new ACE.Server.Entity.Actions.ActionChain();
            chain.AddDelaySeconds(0.5);
            chain.AddAction(landblock, () =>
            {
                if (Place(landblock, p, exact: true) == null)
                    player.Session?.Network.EnqueueSend(new ACE.Server.Network.GameMessages.Messages.GameMessageSystemChat(
                        $"{Describe(p, player)}: it could not be shown there (something solid in the way?). Move it again.", ACE.Entity.Enum.ChatMessageType.Broadcast));
            });
            chain.EnqueueChain();

            editing[player.Guid.Full] = p;
            return Save(session) ?? $"{what}: {Describe(p, player)}.";
        }

        /// <summary>Each staff member's last assess the tool has used (guid and when), so an old one is not used again.</summary>
        private static readonly ConcurrentDictionary<uint, double> usedAssess = new ConcurrentDictionary<uint, double>();

        /// <summary>
        /// The piece being arranged: the piece last worked on if the player is within 6 m of it, else the nearest
        /// piece within 4 m, else nothing. Clicks and assesses are not used: a click stays in the game client, and an assess
        /// from earlier picked the guide sign instead of a lifestone just added.
        /// </summary>
        private static FraysPiece Selected(Session session, Player player, bool repick = false)
        {
            lastMiss.TryRemove(player.Guid.Full, out _);

            var target = player.RequestedAppraisalTarget;
            var when = player.AppraisalRequestedTimestamp ?? 0;
            var fresh = false && target != null && when > ACE.Common.Time.GetUnixTime() - 60
                && !(usedAssess.TryGetValue(player.Guid.Full, out var used) && used == when);

            if (fresh)
            {
                usedAssess[player.Guid.Full] = when;
                FraysPiece match = null;

                if (placed.TryGetValue(target.Value, out var clicked))
                    match = clicked;
                else if (player.FindObject(target.Value, Player.SearchLocations.Landblock | Player.SearchLocations.ObjectsKnownByMe) is WorldObject wo && wo.Location != null)
                {
                    // Not in the list of placed objects: a piece of the same weenie standing within 2 m of it.
                    var lb = (ushort)(wo.Location.LandblockId.Raw >> 16);

                    lock (sync)
                        match = Pieces.Where(t => t.Wcid == wo.WeenieClassId && t.Landblock == lb)
                            .OrderBy(t => (t.X - wo.Location.PositionX) * (t.X - wo.Location.PositionX) + (t.Y - wo.Location.PositionY) * (t.Y - wo.Location.PositionY))
                            .FirstOrDefault(t => Math.Abs(t.X - wo.Location.PositionX) < 2 && Math.Abs(t.Y - wo.Location.PositionY) < 2);

                    if (match != null)
                        placed[wo.Guid.Full] = match;
                    else
                        lastMiss[player.Guid.Full] = $"{wo.Name} (weenie {wo.WeenieClassId}) is not a village piece.";
                }

                if (match != null)
                {
                    editing[player.Guid.Full] = match;
                    return match;
                }
            }

            var plb = (ushort)(player.Location.LandblockId.Raw >> 16);
            float px = player.Location.PositionX, py = player.Location.PositionY;

            float Dist2(FraysPiece t)
            {
                if (!t.Outdoor && t.Landblock != plb)
                    return float.MaxValue;
                var dx = px + ((plb >> 8) - (t.Landblock >> 8)) * 192f - t.X;
                var dy = py + ((plb & 0xFF) - (t.Landblock & 0xFF)) * 192f - t.Y;

                // A building counts from its walls, not its centre: the centre of a chapel or hall is inside solid walls
                // nobody can stand in (counted from the centre, the chapel could not be selected).
                if (Footprints.TryGetValue(t.Wcid, out var f) || FraysHouses.TryFootprint(t.Wcid, out f))
                {
                    var a = t.Facing * Math.PI / 180;
                    float c = (float)Math.Cos(a), s = (float)Math.Sin(a), k = t.Size;
                    float lx = (dx * c + dy * s) / k, ly = (-dx * s + dy * c) / k;
                    var ox = Math.Max(0, Math.Max(f.MinX - lx, lx - f.MaxX)) * k;
                    var oy = Math.Max(0, Math.Max(f.MinY - ly, ly - f.MaxY)) * k;
                    return ox * ox + oy * oy;
                }

                return dx * dx + dy * dy;
            }

            List<FraysPiece> all;
            lock (sync)
                all = Pieces.ToList();

            var last = editing.TryGetValue(player.Guid.Full, out var e) && all.Contains(e) ? e : null;

            float Centre2(FraysPiece t)
            {
                var dx = px + ((plb >> 8) - (t.Landblock >> 8)) * 192f - t.X;
                var dy = py + ((plb & 0xFF) - (t.Landblock & 0xFF)) * 192f - t.Y;
                return dx * dx + dy * dy;
            }

            // Small pieces first, then buildings, then walls: a building's or wall's footprint reaches anyone standing by
            // the chest or sign beside it ("move here" for the chapel could move a wall, and "remove" by a Dream
            // Chest could remove the Great Hall).
            int Rank(FraysPiece t) =>
                FraysHouses.IsWall(t.Wcid) || t.Wcid == FraysDefs.ArwicWall || t.Wcid == FraysDefs.ArwicTower ? 2
                : Footprints.ContainsKey(t.Wcid) || FraysHouses.TryFootprint(t.Wcid, out _) ? 1 : 0;

            var near = all.Where(t => Dist2(t) < 16).OrderBy(Rank).ThenBy(Dist2).ThenBy(Centre2).FirstOrDefault();

            if (near != null)
            {
                editing[player.Guid.Full] = near;
                return near;
            }

            // Then where each placed object stands now (an NPC could not be selected: a creature can walk
            // off its saved spot, so the saved spot is not where you find it).
            float Live2(WorldObject wo)
            {
                if (wo?.Location == null || wo.IsDestroyed)
                    return float.MaxValue;
                var wlb = (ushort)(wo.Location.LandblockId.Raw >> 16);
                if ((wo.Location.Cell & 0xFFFF) >= 0x100 && wlb != plb)
                    return float.MaxValue;
                var dx = px + ((plb >> 8) - (wlb >> 8)) * 192f - wo.Location.PositionX;
                var dy = py + ((plb & 0xFF) - (wlb & 0xFF)) * 192f - wo.Location.PositionY;
                return dx * dx + dy * dy;
            }

            var live = shownObjects
                .Select(kv => (Wo: kv.Value, Piece: placed.TryGetValue(kv.Key, out var lp) && all.Contains(lp) ? lp : null))
                .Where(o => o.Piece != null)
                .Select(o => (o.Wo, o.Piece, D: Live2(o.Wo)))
                .Where(o => o.D < float.MaxValue)
                .OrderBy(o => o.D)
                .ToList();

            if (live.Count > 0 && live[0].D < 16)
            {
                editing[player.Guid.Full] = live[0].Piece;
                return live[0].Piece;
            }

            // The last piece worked on is kept only when nothing is in reach: after "move n 3" it can be out of reach. Keeping it
            // first grabbed it over the piece the player stood by.
            if (!repick && last != null && Dist2(last) < 36)
                return last;

            if (!lastMiss.ContainsKey(player.Guid.Full))
                lastMiss[player.Guid.Full] = "Stand within 4 m of the piece (of a building's walls)."
                    + (live.Count > 0 && live[0].D < 60 * 60 ? $" The nearest is {live[0].Wo.Name}, {Math.Sqrt(live[0].D):0} m away." : " No village piece is within 60 m of you.");

            return null;
        }
    }
}
