using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Frays: always-on campfires in real dungeons (Vissidal Island and the Sky Fray). Unlike the World Events Hold
    /// (one timed event, WorldEventManager.Current), there are many fires and they never end; this manager keeps every
    /// fire of every loaded copy of a Frays landblock, ticks them once a second on their landblock's thread, and is the
    /// one place the game hooks call (use, death, damage, targeting, per-viewer attackability).
    /// </summary>
    public static class FraysManager
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(FraysManager));

        /// <summary>Fires by (landblock instance, fire id).</summary>
        private static readonly ConcurrentDictionary<(uint Instance, ushort Landblock, string Id), FraysFire> fires =
            new ConcurrentDictionary<(uint, ushort, string), FraysFire>();

        private static readonly ConcurrentDictionary<uint, FraysPlayerRecord> records = new ConcurrentDictionary<uint, FraysPlayerRecord>();

        private static double nextTick;

        public static IEnumerable<FraysFire> Fires => fires.Values;

        // ---- which landblocks --------------------------------------------------------------------------------

        public static bool Enabled
        {
            get
            {
                try { return FraysSettings.Toggle(FraysSettings.Enabled); }
                catch (Exception ex) { log.Error("[FRAYS] could not read frays_enabled", ex); return false; }
            }
        }

        /// <summary>Is <paramref name="landblock"/> a Frays dungeon (in frays_landblocks, with the feature on)?</summary>
        /// <summary>
        /// A landblock whose retail monsters are left out: a Fray landblock, or Vissidal Island ground while a Vissidal Fray
        /// is active (so the island holds only campfire creatures).
        /// </summary>
        public static bool IsClearedLandblock(ushort landblock, uint instance)
        {
            if (!InFrayRealm(instance))
                return false;

            if (IsFraysLandblock(landblock))
                return true;

            if (!Enabled || !FraysDefs.VissidalIsland.Contains(landblock))
                return false;

            var frays = FraysFamilies.Frays;
            return frays.Contains(FraysDefs.VissidalCoast) || frays.Contains(FraysDefs.VissidalHeights);
        }

        /// <summary>
        /// True for a landblock copy in the Frays' realm (frays_fray_realm, realm 1 by default: the Drift copy of the world,
        /// as with Drift Marae Lassel). The base world's landblocks stay retail.
        /// </summary>
        public static bool InFrayRealm(uint instance)
        {
            ACE.Entity.Position.ParseInstanceID(instance, out var ephemeral, out var realm, out _);
            return !ephemeral && realm == FraysSettings.Int(FraysSettings.FrayRealm);
        }

        /// <summary>
        /// Is this spot on the Frays: the Frays' realm copy of Vissidal Island or a Fray landblock, with the feature on. The
        /// realm alone is not enough (it is also the Drift copy of the world).
        /// </summary>
        public static bool OnFrays(ACE.Entity.Position pos)
        {
            if (pos == null || !Enabled || !InFrayRealm(pos.Instance))
                return false;

            var landblock = (ushort)(pos.LandblockId.Raw >> 16);
            return FraysDefs.VissidalIsland.Contains(landblock) || IsFraysLandblock(landblock);
        }

        public static bool IsFraysLandblock(ushort landblock)
        {
            if (!Enabled)
                return false;

            // No landblock list set: every landblock that holds a fire of an active Fray (frays_fray_order).
            var listed = FraysSettings.Text(FraysSettings.Landblocks);

            if (listed.Trim().Length == 0)
            {
                var frays = FraysFamilies.Frays;
                return FraysDefs.ForLandblock(landblock).Any(d => frays.Contains(d.FrayKey));
            }

            foreach (var part in FraysSettings.Text(FraysSettings.Landblocks).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim();

                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    t = t.Substring(2);

                if (ushort.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var lb) && lb == landblock)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The world rows a Frays landblock loads: everything but creatures and generators, so the retail spawns are
        /// gone and the doors, portals and scenery stay. Pure (the lookups are passed in) and unit tested.
        /// </summary>
        public static List<T> FilterRetailSpawns<T>(IEnumerable<T> rows, Func<T, uint> wcidOf, Func<uint, WeenieType?> typeOf, Func<uint, bool> isGenerator, Func<uint, bool> isNpc = null)
        {
            var kept = new List<T>();

            foreach (var row in rows)
            {
                var wcid = wcidOf(row);
                var type = typeOf(wcid);

                if (type == null)
                    continue;

                // Monsters and generators go; a creature that cannot be attacked (an NPC: a vendor, a quest giver) stays.
                if (type == WeenieType.Creature && isNpc != null && isNpc(wcid))
                {
                    kept.Add(row);
                    continue;
                }

                if (type == WeenieType.Creature || isGenerator(wcid))
                    continue;

                kept.Add(row);
            }

            return kept;
        }

        // ---- landblock life ----------------------------------------------------------------------------------

        /// <summary>A Frays landblock finished loading (on its own action queue): place its fires.</summary>
        public static void OnLandblockLoaded(Landblock landblock)
        {
            try
            {
                if (landblock.IsEphemeral || !InFrayRealm(landblock.Instance) || !Enabled)
                    return;

                // Fires only on a Frays landblock; the arrival NPCs and the village (below) on any landblock that lists
                // them (the Vissidal village sits in 0xC8E2, which has no fire).
                foreach (var def in IsFraysLandblock(landblock.Id.Landblock) ? FraysDefs.ForLandblock(landblock.Id.Landblock) : Enumerable.Empty<FraysFireDef>())
                {
                    var key = (landblock.Instance, def.Landblock, def.Id);

                    if (fires.ContainsKey(key))
                        continue;

                    var zone = BuildZone(def, out var error);

                    if (zone == null)
                    {
                        log.Error($"[FRAYS] {def.Id}: no leash zone ({error}); the fire is not placed");
                        continue;
                    }

                    var fire = new FraysFire(def, landblock, zone);
                    fire.PlaceFireObject();
                    fires[key] = fire;

                    log.Info($"[FRAYS] {def.Id} ({def.RoomName}) placed in 0x{landblock.Id.Landblock:X4} instance 0x{landblock.Instance:X8}: {zone.Cells.Count} room cells, {zone.Doorways.Count} doorways, {def.SpawnPoints.Count} spawn points");
                }

                // The arrival NPCs, the guide sign and the village: the saved layout (/frays admin piece) or the built-in one.
                FraysVillage.PlaceAll(landblock);
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] placing fires in 0x{landblock.Id.Landblock:X4} threw", ex);
            }
        }

        /// <summary>
        /// The village buildings' setup: one part hung 4.9 m below the origin, no spheres. With the origin at the model's base
        /// (the old crate setup), the player's game lifted a piece whose origin sat under a slope (a wall's origin is 8 m off
        /// its line, up the hill) and the wall floated well above the ground.
        /// </summary>
        public const uint HungSetup = 0x02001790;
        public const float HungDrop = 4.9f;

        /// <summary>
        /// The solid stand-in: the hung setup above lacks the
        /// setup flag that tells the game to use the model's collision, so every piece on it could be walked through. This
        /// retail setup has that flag, one part and no spheres, but hangs its part only 1.18 m below the origin. A piece uses
        /// it wherever the ground at its origin is low enough for that (else the game would lift it); flush doors always do.
        /// </summary>
        public const uint SolidSetup = 0x02000F84;
        public const float SolidDrop = 1.17795f;

        /// <summary>How far a piece's model hangs below its origin (0 for most things).</summary>
        public static float PartDrop(WorldObject wo) => DropOf(wo.SetupTableId) * (wo.ObjScale ?? 1);

        public static float DropOf(uint setup) => setup == HungSetup ? HungDrop : setup == SolidSetup ? SolidDrop : 0;

        /// <summary>The height of a piece's model base (its origin, less any hang).</summary>
        public static float BaseZ(WorldObject wo) => wo.Location.PositionZ - PartDrop(wo);

        /// <summary>
        /// Puts a fixed Fray object (an NPC) at a spot, trying small steps around it (scenery can block a spot)
        /// inside the 10 m tile. Outdoors it stands on the terrain. Null when the weenie is missing or nowhere fits.
        /// </summary>
        /// <param name="exact">Only the spot itself (the village tool: the staff member chose it).</param>
        /// <param name="above">Outdoors: metres above the terrain.</param>
        public static WorldObject PlaceFixture(Landblock landblock, uint wcid, ushort cell, System.Numerics.Vector3 at, System.Numerics.Quaternion facing, bool exact = false, float above = 0, float scale = 1, float? z = null, bool ethereal = false, bool asPlaced = false, bool solid = false, bool uiHidden = false, Action<WorldObject> prepare = null)
        {
            var offsets = exact ? new[] { (0f, 0f) } : new[] { (0f, 0f), (1.5f, 0f), (-1.5f, 0f), (0f, 1.5f), (0f, -1.5f), (3f, 0f), (-3f, 0f), (0f, 3f), (0f, -3f) };
            var lb = landblock.Id.Landblock;

            foreach (var (dx, dy) in offsets)
            {
                var wo = Factories.WorldObjectFactory.CreateNewWorldObject(wcid);

                if (wo == null)
                {
                    log.Error($"[FRAYS] weenie {wcid} is not in the world database; import its SQL from C:\\dev\\dreamweave-content");
                    return null;
                }

                wo.Location = new Position(((uint)lb << 16) | cell, at.X + dx, at.Y + dy, at.Z, facing.X, facing.Y, facing.Z, facing.W, landblock.Instance);

                if (cell < 0x100)
                    wo.Location.PositionZ = z ?? ACE.Server.Entity.PositionExtensions.GetTerrainZ(wo.Location) + above;   // z: a set height (a house's door covers)

                wo.FraysTransient = true;
                prepare?.Invoke(wo);   // a model or name set before it enters the world (the Snarl Monument)

                if (scale > 0 && Math.Abs(scale - 1) > 0.001f)
                    wo.ObjScale = (wo.ObjScale ?? 1) * scale;   // a village piece built smaller
                wo.TimeToRot = -1;

                // A village building at its set height: no spawn nudge. Without this the game lifted any piece whose origin sat
                // under the ground (a wall's origin is 8 m off its line, up the hill) onto the ground there, and the wall floated
                // (as it did under the east wall).
                if (asPlaced && wo.Stuck)
                    wo.SpawnAtExactPosition = true;   // read when its physics object is made (as it joins the landblock)

                // The player's game lifts the piece too, until its origin clears the ground there. A building's setup hangs its
                // model PartDrop below the origin, so the origin is raised to match and sits in the air above any slope.
                // Solid where it can be: the solid setup's shorter hang still keeps the origin clear of the ground there.
                if (wo.SetupTableId == HungSetup && (solid || cell >= 0x100
                    || ACE.Server.Entity.PositionExtensions.GetTerrainZ(wo.Location) <= wo.Location.PositionZ + SolidDrop * (wo.ObjScale ?? 1) - 0.05f))
                    wo.SetupTableId = SolidSetup;

                wo.Location.PositionZ += PartDrop(wo);

                if (ethereal)
                    wo.Ethereal = true;   // a house cover: the game's collision would shove it off its doorway

                if (uiHidden)
                    wo.UiHidden = true;   // a building: not clickable

                if (wo.EnterWorld())
                {
                    log.Info($"[FRAYS] {wo.Name} placed in 0x{lb:X4} instance 0x{landblock.Instance:X8} ({wo.Location.ToLOCString()}){(wo.SetupTableId == SolidSetup ? " solid" : wo.SetupTableId == HungSetup ? " walk-through" : "")}");
                    return wo;
                }

                wo.Destroy();
            }

            log.Error($"[FRAYS] weenie {wcid} could not be placed near 0x{lb:X4}{cell:X4} {at}");
            return null;
        }

        public static void OnLandblockUnloaded(Landblock landblock)
        {
            foreach (var key in fires.Keys.Where(k => k.Instance == landblock.Instance && k.Landblock == landblock.Id.Landblock).ToList())
            {
                if (fires.TryRemove(key, out var fire))
                {
                    try { fire.Extinguish("the dungeon unloaded"); }
                    catch (Exception ex) { log.Error($"[FRAYS] {fire.Def.Id}: extinguish on unload threw", ex); }
                }
            }
        }

        /// <summary>The fire's zone: its room cells and the doorways out of them, from the cell dat's portals.</summary>
        public static FraysLeashZone BuildZone(FraysFireDef def, out string error)
        {
            if (def.Outdoor)
            {
                error = null;
                return new FraysLeashZone(System.Array.Empty<ushort>(), def.FirePos, null) { WholeLandblock = true };
            }

            var graph = DungeonWalkGraphSource.Get(def.Landblock, out error);

            if (graph == null)
                return null;

            var portals = graph.Portals.Select(p => ((ushort)(p.From & 0xFFFF), (ushort)(p.To & 0xFFFF), p.Centroid));

            return new FraysLeashZone(def.RoomCells, def.FirePos, portals);
        }

        // ---- the tick ----------------------------------------------------------------------------------------


        /// <summary>Per Fray: true once its "all campfires lit" banner line has gone out (cleared when one goes out).</summary>
        private static readonly Dictionary<ushort, bool> bannerShown = new Dictionary<ushort, bool>();

        /// <summary>The Fray banner: when every campfire of a Fray burns, its fellowship hears about it once.</summary>
        private static void BannerCheck()
        {
            foreach (var group in fires.Values.GroupBy(f => f.Def.FrayKey))
            {
                var all = group.All(f => f.Lit);
                bannerShown.TryGetValue(group.Key, out var shown);

                if (all && !shown)
                {
                    bannerShown[group.Key] = true;
                    var heat = group.SelectMany(f => f.CampersSnapshot()).Sum(c => c.Level);
                    Feed(group.First(), $"Every campfire in the {FrayName(group.Key)} burns! Combined Heat {heat}. The Fray banner flies.");
                }
                else if (!all && shown)
                    bannerShown[group.Key] = false;
            }
        }

        /// <summary>
        /// Every campfire, Fray by Fray (/frays camps and the Fray-Warden, so players
        /// can see where the camps are): a line a Fray, then the 5 nearest free campfires with their map coordinates outdoors
        /// and how far and which way from you.
        /// </summary>
        public static List<string> CampsLines(Player player)
        {
            var lines = new List<string>();
            var me = player?.Location;
            var meGlobal = me?.ToGlobal();

            // A fire is placed only once its landblock
            // loads (someone near), so every campfire is listed from the defs; one not placed yet is free and shows as such.
            var inst = me?.Instance ?? 0;
            var shown = FraysDefs.All.Where(d => IsFraysLandblock(d.Landblock))
                .Select(d => (Def: d, Fire: fires.Values.Where(f => f.Def.Id == d.Id).OrderByDescending(f => f.Instance == inst).FirstOrDefault())).ToList();

            // /fc shows only the 5 closest free campfires. One line
            // a Fray (free count, Heat, families), then the nearest free campfires across every Fray.
            var freeRows = new List<(double Dist, string Text)>();

            foreach (var group in shown.GroupBy(x => CampListName(x.Def.FrayKey)).OrderBy(g => g.Key))
            {
                var free = group.Count(x => x.Fire == null || !x.Fire.Lit);
                var heatSum = group.Where(x => x.Fire != null).SelectMany(x => x.Fire.CampersSnapshot()).Sum(c => c.Level);
                var fams = group.Select(x => x.Def.FrayKey).Distinct().Select(FraysFamilies.For).Where(f => f != null).Select(f => f.Name).Distinct().ToList();
                lines.Add($"{group.Key}: {free} of {group.Count()} campfires free, combined Heat {heatSum}{(fams.Count > 0 ? $", {string.Join(" and ", fams)} this week" : "")}{(free == 0 ? " - the banner flies" : "")}.");

                foreach (var (d, f) in group)
                {
                    if (f != null && f.Lit)
                        continue;

                    var fInstance = f?.Instance ?? inst;
                    var pos = new Position(((uint)d.Landblock << 16) | d.FireCell, d.FirePos.X, d.FirePos.Y, d.FirePos.Z, 0, 0, 0, 1, fInstance);
                    var coords = MapCoordText(pos);
                    var dist = double.MaxValue;
                    var way = "";

                    if (me != null && meGlobal != null && me.Instance == fInstance && d.Outdoor && (me.Cell & 0xFFFF) < 0x100)
                    {
                        var g = pos.ToGlobal();
                        var dx = g.X - meGlobal.Value.X;
                        var dy = g.Y - meGlobal.Value.Y;
                        dist = Math.Sqrt(dx * dx + dy * dy);
                        way = dist < 5 ? " - you are here" : $" - {dist:0} m {FraysRules.Compass(dx, dy)}";
                    }
                    else if (me != null && (me.LandblockId.Raw >> 16) == d.Landblock && me.Instance == fInstance)
                    {
                        dist = 0;
                        way = " - in this dungeon";
                    }

                    freeRows.Add((dist, $"  {group.Key}, {d.RoomName}{(coords != null ? $", {coords}" : "")}{way}."));
                }
            }

            const int Nearest = 5;
            if (freeRows.Count > 0)
            {
                lines.Add($"The {Math.Min(Nearest, freeRows.Count)} nearest free campfires:");
                lines.AddRange(freeRows.OrderBy(r => r.Dist).Take(Nearest).Select(r => r.Text));
            }
            else if (lines.Count > 0)
                lines.Add("Every campfire is taken.");

            if (lines.Count == 0)
                lines.Add("No Fray is loaded right now.");

            return lines;
        }

        /// <summary>WorldManager's heartbeat. Once a second each lit fire's tick is queued on its landblock.</summary>
        public static void Tick()
        {
            var now = Time.GetUnixTime();

            if (now < nextTick)
                return;

            nextTick = now + 1;

            if (Enabled)
            {
                FraysLongTerm.Tick(now);
                FraysFrayWide.Tick(now);
                FraysLfg.Tick(now);
            }

            BannerCheck();
            FraysVillage.KeepShown(now);   // re-send village doors and windows the game may have dropped

            foreach (var fire in fires.Values)
            {
                if (!fire.Lit)
                    continue;

                var f = fire;

                f.Landblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        f.Tick(Time.GetUnixTime());
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[FRAYS] {f.Def.Id}: tick threw", ex);
                    }
                }));
            }
        }

        // ---- the dungeon fellowship -----------------------------------------------------------------------------

        /// <summary>
        /// One fellowship per Frays dungeon copy, run by its campfires: everyone holding a fire in that dungeon is in
        /// it (one player per fire, so up to 20). Nobody leads it in practice: disband, lock, rename, open, recruit,
        /// dismiss and hand-over are refused (Player.FraysFellowshipBlocked). It is made by the first camper and
        /// remembered here while it has members.
        /// </summary>
        private static readonly ConcurrentDictionary<(uint Instance, ushort Landblock), Fellowship> dungeonFellowships =
            new ConcurrentDictionary<(uint, ushort), Fellowship>();

        public static bool IsDungeonFellowship(Fellowship fellowship) =>
            fellowship != null && dungeonFellowships.Values.Any(f => ReferenceEquals(f, fellowship));

        /// <summary>
        /// The Frays fellowships: the first 20 campers to take any campfire join the same fellowship, and the
        /// 21st starts a second one. Every solo camper in any Fray joins the first one with room; when all are
        /// full the next camper starts another ("The Frays 2", ...). Only the landblock copy (instance) keeps them apart.
        /// </summary>
        public static IEnumerable<Fellowship> FraysFellowships(uint instance) =>
            dungeonFellowships.Where(kv => kv.Key.Instance == instance && kv.Value.FellowshipMembers.Count > 0)
                .OrderBy(kv => kv.Key.Landblock).Select(kv => kv.Value);

        /// <summary>True when <paramref name="player"/> is in one of this landblock copy's Frays fellowships.</summary>
        public static bool InFraysFellowship(Player player, uint instance) =>
            player?.Fellowship != null && FraysFellowships(instance).Any(f => ReferenceEquals(f, player.Fellowship));

        /// <summary>The name of the Nth Frays fellowship.</summary>
        public static string FraysFellowshipName(int slot) => slot <= 1 ? "The Frays" : $"The Frays {slot}";

        /// <summary>
        /// Puts <paramref name="player"/> in a Frays fellowship: out of any other fellowship first, then into the first one
        /// with room and someone online in it, or a new one. True when they are in one afterwards.
        /// </summary>
        public static bool JoinDungeonFellowship(Player player, FraysFire fire)
        {
            if (player == null || fire == null)
                return false;

            if (InFraysFellowship(player, fire.Instance))
                return true;

            if (player.Fellowship != null)
            {
                Tell(player, "You leave your fellowship to join the Frays fellowship.");
                player.FellowshipQuit(false, system: true);
            }

            // The first one with room, through any member who is online (added directly, no invite to accept).
            foreach (var fellowship in FraysFellowships(fire.Instance).ToList())
            {
                if (fellowship.FellowshipMembers.Count >= Fellowship.MaxFellows)
                    continue;

                var inviter = fellowship.GetFellowshipMembers().Values.FirstOrDefault(m => m.Fellowship == fellowship);

                if (inviter == null)
                    continue;

                fellowship.AddConfirmedMember(inviter, player, true);

                if (player.Fellowship == fellowship)
                    return true;
            }

            // All full (or none yet): start the next one, in the first free slot.
            var slot = 1;

            while (dungeonFellowships.TryGetValue((fire.Instance, (ushort)slot), out var held) && held.FellowshipMembers.Count > 0
                && held.GetFellowshipMembers().Values.Any(m => m.Fellowship == held))
                slot++;

            player.FellowshipCreate(FraysFellowshipName(slot), false);

            if (player.Fellowship == null)
                return false;

            NoSharing(player.Fellowship);
            dungeonFellowships[(fire.Instance, (ushort)slot)] = player.Fellowship;

            if (slot > 1)
                Tell(player, $"The Frays fellowships are full, so you start {FraysFellowshipName(slot)}.");

            return true;
        }

        // ---- lookups -----------------------------------------------------------------------------------------

        public static FraysFire FireOwnedBy(Fellowship fellowship) =>
            fellowship == null ? null : fires.Values.FirstOrDefault(f => f.Lit && f.OwnerFellowship == fellowship);

        /// <summary>A lit fire held by another character who plays from the same IP address as this player, if any.</summary>
        public static FraysFire FireHeldFromSameIp(Player player)
        {
            var ip = player?.Session?.EndPointC2S?.Address;

            if (ip == null)
                return null;

            foreach (var f in fires.Values)
            {
                if (!f.Lit || f.SoloOwner == 0 || f.SoloOwner == player.Guid.Full)
                    continue;

                var other = PlayerManager.GetOnlinePlayer(f.SoloOwner)?.Session?.EndPointC2S?.Address;

                if (other != null && other.Equals(ip))
                    return f;
            }

            return null;
        }

        /// <summary>The dungeon fellowship never shares XP, luminance or loot: everything is paid to each camper alone.</summary>
        private static void NoSharing(Fellowship fellowship)
        {
            if (fellowship == null)
                return;

            fellowship.DesiredShareXP = false;
            fellowship.ShareXP = false;
            fellowship.ShareLoot = false;
        }

        public static FraysFire FireOwnedBySolo(uint guid) => fires.Values.FirstOrDefault(f => f.Lit && f.SoloOwner == guid);

        /// <summary>The lit fire this player owns, if any.</summary>
        public static FraysFire FireOf(Player player) =>
            player == null ? null : fires.Values.FirstOrDefault(f => f.IsOwner(player));

        /// <summary>The fire nearest the player in their own landblock copy (for status and admin commands).</summary>
        public static FraysFire NearestFire(Player player)
        {
            if (player?.Location == null)
                return null;

            var lb = (ushort)(player.Location.LandblockId.Raw >> 16);

            return fires.Values.Where(f => f.Instance == player.Location.Instance && f.Def.Landblock == lb)
                .OrderBy(f => f.DistanceToFire(player.Location)).FirstOrDefault();
        }

        public static FraysFire FindFire(Player player, string id)
        {
            var inst = player?.Location?.Instance;

            return fires.Values.Where(f => string.Equals(f.Def.Id, id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Instance == inst ? 0 : 1).FirstOrDefault();
        }

        // ---- records -----------------------------------------------------------------------------------------

        public static FraysPlayerRecord Record(Player player) => records.GetOrAdd(player.Guid.Full, _ => FraysPlayerRecord.Load(player));

        public static void SaveRecord(Player player, FraysPlayerRecord rec)
        {
            lock (rec)
                rec.Save(player);
        }

        public static void Tell(Player player, string text)
        {
            player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        /// <summary>
        /// Center-screen text (the game's transient message), for what a player must not miss: Heat levels, the Snarl, the
        /// Great Snarl. The green chat lines alone scrolled away under combat spam. frays_center_text off = chat only.
        /// </summary>
        public static void Shout(Player player, string text)
        {
            if (player?.Session == null || !FraysSettings.Toggle(FraysSettings.CenterText))
                return;

            player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, text));
        }

        /// <summary>A particle effect on <paramref name="target"/>, seen by everyone near.</summary>
        public static void Effect(WorldObject target, PlayScript script)
        {
            if (target != null && FraysSettings.Toggle(FraysSettings.HeatEffects))
                target.EnqueueBroadcast(new GameMessageScript(target.Guid, script));
        }

        /// <summary>
        /// The Heat level-up looks to choose from (the old swirl was hard to see). heat_up_effect picks
        /// one by number; /frays admin heatfx <n> plays one on you to compare. Each is one or two client scripts.
        /// </summary>
        public static readonly IReadOnlyList<(string Name, PlayScript[] Scripts)> HeatUpLooks = new List<(string, PlayScript[])>
        {
            ("the old orange swirl", new[] { PlayScript.SkillUpOrange }),
            ("the retail level-up burst", new[] { PlayScript.LevelUp }),
            ("the aetheria level-up glow", new[] { PlayScript.AetheriaLevelUp }),
            ("a gout of flame", new[] { PlayScript.BreatheFlame }),
            ("an orange shield flare", new[] { PlayScript.ShieldUpOrange, PlayScript.SkillUpOrange }),
            ("the destruction surge", new[] { PlayScript.AetheriaSurgeDestruction }),
            ("level-up burst and flame", new[] { PlayScript.LevelUp, PlayScript.BreatheFlame }),
        };

        /// <summary>Plays Heat look <paramref name="index"/> (1-based) on <paramref name="target"/>, ignoring the effects toggle. False when there is no such look.</summary>
        public static bool PlayHeatLook(WorldObject target, int index)
        {
            if (target == null || index < 1 || index > HeatUpLooks.Count)
                return false;

            foreach (var s in HeatUpLooks[index - 1].Scripts)
                target.EnqueueBroadcast(new GameMessageScript(target.Guid, s));

            return true;
        }

        /// <summary>A Heat level gained: the chosen look, and every 5th level the bright flash as well.</summary>
        public static void HeatUpEffect(Player p, bool milestone)
        {
            if (p == null || !FraysSettings.Toggle(FraysSettings.HeatEffects))
                return;

            PlayHeatLook(p, Math.Clamp(FraysSettings.Int(FraysSettings.HeatUpEffect), 1, HeatUpLooks.Count));

            if (milestone)
                p.EnqueueBroadcast(new GameMessageScript(p.Guid, PlayScript.SpecialStateOrange));
        }

        /// <summary>
        /// A Fray-wide moment: the feed line, and the same as center-screen text. <paramref name="everyone"/> (the Great
        /// Snarl, for anyone holding a fire in any fellowship, group camps and solo camps alike)
        /// sends it to every fire holder in every Fray, not just this one.
        /// </summary>
        public static void Announce(FraysFire fire, string text, string shout = null, bool everyone = false)
        {
            Feed(fire, text, everyone);

            foreach (var member in everyone ? AllCampers(fire) : FrayAudience(fire))
                Shout(member, shout ?? text);
        }

        /// <summary>Everyone holding a lit fire in a fire's Fray (solo or group, whatever their fellowship), in that landblock copy.</summary>
        private static List<Player> FrayAudience(FraysFire fire) =>
            fire == null ? new List<Player>() : Holders(fires.Values.Where(f => f.Lit && f.Def.FrayKey == fire.Def.FrayKey && f.Instance == fire.Instance));

        /// <summary>Everyone holding a lit fire in any Fray (solo or group), in that landblock copy.</summary>
        public static List<Player> AllCampers(FraysFire fire) =>
            fire == null ? new List<Player>() : Holders(fires.Values.Where(f => f.Lit && f.Instance == fire.Instance));

        private static List<Player> Holders(IEnumerable<FraysFire> lit) =>
            lit.SelectMany(f => f.OwnerGuids).Distinct().Select(g => PlayerManager.GetOnlinePlayer(g)).Where(p => p != null).ToList();

        private static readonly ConcurrentDictionary<uint, double> blessedUntil = new ConcurrentDictionary<uint, double>();

        /// <summary>Emberblessed (the Great Snarl's gift): +10% Embers, +5% damage for emberblessed_minutes.</summary>
        public static bool Blessed(Player p) => p != null && blessedUntil.TryGetValue(p.Guid.Full, out var until) && Time.GetUnixTime() < until;

        public static void Bless(Player p)
        {
            var minutes = FraysSettings.Number(FraysSettings.EmberblessedMinutes);

            if (p == null || minutes <= 0)
                return;

            blessedUntil[p.Guid.Full] = Time.GetUnixTime() + minutes * 60;
            Tell(p, $"Emberblessed for {minutes:0} minutes: +10% Embers and +5% damage, one Heat at once, and your fire does not cool for 2 minutes.");
            Shout(p, "Emberblessed!");
            Effect(p, PlayScript.SpecialStateOrange);
            FireOf(p)?.BlessCamp(p);
        }

        /// <summary>The camp tick: Emberblessed on the camper's modifiers.</summary>
        public static void ApplyBlessing(Player p, FraysCampMods m)
        {
            if (!Blessed(p))
                return;

            m.Embers *= 1.10;
            m.BonusDamage += 0.05;   // in the Frays damage pool
        }

        /// <summary>The Fray's feed: a line in fellowship chat to every camper of that Fray (highlights, Snarl kills, records).</summary>
        public static void Feed(FraysFire fire, string text, bool everyone = false)
        {
            foreach (var member in everyone ? AllCampers(fire) : FrayAudience(fire))
                member.Session?.Network.EnqueueSend(new GameMessageSystemChat($"[{FrayName(fire.Def.FrayKey)}] {text}", ChatMessageType.Fellowship));
        }

        /// <summary>The player-facing name of a Fray ("the Sky Fray").</summary>
        /// <summary>A position's map coordinates as the client shows them (null indoors); see FraysRules.MapCoords.</summary>
        public static string MapCoordText(Position pos)
        {
            if (pos == null || (pos.Cell & 0xFFFF) >= 0x100)
                return null;

            var g = pos.ToGlobal();
            var (ns, ew) = FraysRules.MapFromGlobal(g.X, g.Y);
            return FraysRules.MapCoords(ns, ew);
        }

        /// <summary>
        /// The name a camp list shows for a Fray: the Coast and the Heights are one place to players,
        /// the Ember Isles; the Sky Fray stays its own. Everything else still uses <see cref="FrayName"/>.
        /// </summary>
        public static string CampListName(ushort frayKey) =>
            frayKey == FraysDefs.VissidalCoast || frayKey == FraysDefs.VissidalHeights ? "Ember Isles" : FrayName(frayKey);

        public static string FrayName(ushort landblock) => landblock == FraysDefs.VissidalCoast ? "Vissidal Coast Fray"
            : landblock == FraysDefs.VissidalHeights ? "Vissidal Heights Fray"
            : landblock == FraysDefs.VissidalSky ? "Sky Fray" : $"Fray {landblock:X4}";

        // ---- Embers (a bonded currency item, WCID 2000032) --------------------------------------------------

        /// <summary>Embers owed but not yet paid as whole items, per player (fractions and full packs wait here).</summary>
        private static readonly ConcurrentDictionary<uint, double> emberOwed = new ConcurrentDictionary<uint, double>();

        /// <summary>
        /// Pays <paramref name="embers"/> Embers: whole ones go into the player's pack as Ember items (merged into a
        /// stack they already carry); the fraction, and anything the pack cannot take, wait for the next payment. The
        /// record keeps the lifetime total for /frays status.
        /// </summary>
        public static double PayEmbers(Player player, double embers)
        {
            if (player == null || !(embers > 0))
                return 0;

            try
            {
                // The weekly soft cap: the full rate up to ember_weekly_cap a week, then ember_over_cap_rate of it.
                var rec = Record(player);
                rec.RollWeek(FraysWeek.Current);
                var asked = embers;
                embers = FraysRules.CappedEmbers(embers, rec.WeekEmbers, FraysSettings.Number(FraysSettings.EmberWeeklyCap),
                    FraysSettings.Number(FraysSettings.EmberOverCapRate));
                FraysMetrics.AddPositive(FraysMetrics.EmbersCapLost, asked - embers);
                rec.WeekEmbers += embers;
                rec.Embers += embers;
                SaveRecord(player, rec);

                var owed = emberOwed.AddOrUpdate(player.Guid.Full, embers, (_, v) => v + embers);
                var whole = (int)Math.Floor(owed);

                if (whole > 0)
                {
                    var paid = player.FraysAddStackable(FraysDefs.EmberWcid, whole);
                    emberOwed.AddOrUpdate(player.Guid.Full, 0, (_, v) => v - paid);
                }

                return embers;
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] paying Embers to {player?.Name} threw", ex);
                return 0;
            }
        }

        // ---- hooks -------------------------------------------------------------------------------------------

        /// <summary>
        /// Player_Use: a Hollow Campfire (lit through a yes/no popup), or the Hearth Keeper (who explains the Frays).
        /// True when it was handled.
        /// </summary>
        public static bool TryUseCampfire(Player player, WorldObject item)
        {
            if (player == null || item == null)
                return false;

            if (item.WeenieClassId == FraysDefs.HearthKeeperWcid)
            {
                if (Record(player).Claims.Count > 0)
                    Tell(player, FraysLongTerm.Claim(player));

                // Popup, sign and journal at once is too much: the popup carries the guide,
                // so chat gets one line - the journal on the first talk, else the week.
                // first talk: the 4-step How page; later talks: the menu
                var first = FraysJournal.WardenGift(player);

                if (!first)
                    Tell(player, "Fray-Warden says, \"" + FraysWeek.Summary() + "\"");

                FraysGuide.Show(player, first ? "how" : null);

                return true;
            }

            if (Array.IndexOf(FraysHunts.Givers, item.WeenieClassId) >= 0)
            {
                FraysHunts.Talk(player, item);
                return true;
            }

            if (item.WeenieClassId == FraysDefs.BoonwrightWcid)
            {
                // A vendor now (dreamweave-content 2000162): a line, then the Sigil shop opens as usual.
                Tell(player, "Boonwright says, \"Every boon you pick at a campfire joins your collection. My Sigils raise one a rank: /fray sigil <boon>. /fray collection shows your ranks.\"");
                return false;
            }

            if (item.WeenieClassId == FraysDefs.StewardWcid)
            {
                FraysLfg.Talk(player);
                return true;
            }

            // An Ember Shrine (a solo camp's fixed layout): its owner takes the week's buff.
            if (FraysFire.TryUseShrine(player, item))
                return true;

            if (item.FraysFire is not FraysFire fire)
                return false;

            try
            {
                var refuse = fire.RefuseLight(player);

                if (refuse != null)
                {
                    Tell(player, refuse);
                    return true;
                }

                var rec = Record(player);
                var start = FraysRules.StartLevel(rec.WantedStart(fire.Def.Group), rec.RatingFor(fire.Def.Group), FraysPerks.CapFor(rec));
                var who = player.Fellowship != null ? $"your fellowship ({player.Fellowship.FellowshipName})" : "you";

                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_FraysLight(player.Guid, fire),
                        $"Light the campfire in {fire.Def.RoomName} for {who}? You start at Heat {start} ({rec.HeatText}, {(fire.Def.Group ? "group " : "")}rating {rec.RatingFor(fire.Def.Group)})."))
                    Tell(player, "Answer the open question first.");
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] {fire.Def.Id}: lighting threw", ex);
            }

            return true;
        }

        /// <summary>What the Hearth Keeper tells a player: how the Frays work, and their own numbers.</summary>
        public static List<string> HearthKeeperLines(Player player)
        {
            var rec = Record(player);

            const string W = "Fray-Warden says, \"";

            var lines = new List<string>
            {
                W + "The Weave has worn thin here and the Drift pours through. Light a cold campfire and burn it back: the fire is yours, one player to a fire, and the first 20 campers across the Frays share one fellowship. Its creatures come only to you, and only you can fight them or loot them.\"",
                W + "While you fight at your fire you earn experience set for your level, and luminance, and your Heat multiplies both, and your Embers. Your creatures always match your level; Heat adds levels to them.\"",
                W + "Your Heat is your own. Kill fast and it climbs; stop and it cools; fall and it drops two. Higher Heat brings more and tougher creatures, better loot and more Embers. The same Heat is always the same danger: only your gear and build keep you alive.\"",
                W + "Fight at a Heat for 15 minutes without falling and it becomes your rating. Your rating sets where you may start (/frays start) and the leaderboards.\"",
                W + "Your Snarl always comes when your bar fills, and every week one affix marks every Gilded creature: bring the gear for it. Collect boons from the Boonwright with Sigils; /fray setup shows your loadout.\"",
                W + FraysWeek.Summary() + "\"",
            };

            lines.AddRange(FraysFamilies.WeekLines().Select(l => W + l + "\""));
            // Where the camps are: the same list as /frays camps.
            lines.Add(W + "The campfires, and who holds them (/frays camps shows this again, with the way from where you stand):\"");
            lines.AddRange(CampsLines(player).Select(l => W + l.Trim() + "\""));
            lines.Add(W + $"Your rating is {rec.Rating}; you have earned {Math.Floor(rec.Embers):0} Embers ({Math.Floor(rec.WeekEmbers):0} this week). /frays status and /frays report show the rest.\"");
            return lines;
        }

        /// <summary>Creature.Die: one of a fire's monsters died.</summary>
        public static void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            try
            {
                creature.FraysTag?.Fire?.OnKill(creature);
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] kill report threw", ex);
            }
        }

        /// <summary>Creature.CreateCorpse: a fire monster's corpse.</summary>
        public static void OnCorpseCreated(Creature creature, Corpse corpse)
        {
            try
            {
                var tag = creature.FraysTag;
                if (tag == null)
                    return;

                corpse.FraysLootOwner = tag.LootOwner != 0 ? tag.LootOwner : tag.ForPlayer;   // a group camp: its top damage dealer
                tag.Fire?.OnCorpse(corpse);

                // Once the corpse has its loot: a cache's best item is attuned, Snarl extras, the loot log.
                var chain = new ActionChain();
                chain.AddDelaySeconds(1);
                chain.AddAction(corpse, () => FraysLongTerm.AfterLoot(corpse, tag));
                chain.EnqueueChain();
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] corpse report threw", ex);
            }
        }

        /// <summary>DamageHistory: a hit between a player (or their pet) and a fire monster marks the player active.</summary>
        public static void OnCombatContact(Creature victim, WorldObject attacker)
        {
            try
            {
                if (victim?.FraysTag != null)
                {
                    var player = attacker as Player ?? (attacker as CombatPet)?.P_PetOwner;

                    if (player != null)
                    {
                        victim.FraysTag.Fire.OnCombat(player);

                        // Pack linking: a hit on one creature brings its whole pack.
                        victim.FraysTag.Fire.LinkPack(victim, attacker as Creature);
                    }
                }
                else if (victim is Player hurt && attacker is Creature monster && monster.FraysTag != null)
                    monster.FraysTag.Fire.OnCombat(hurt);
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] combat contact threw", ex);
            }
        }

        /// <summary>Player death: the fire this player owns cools by the death drop.</summary>
        public static void OnPlayerDied(Player player, string cause = null)
        {
            try
            {
                FireOf(player)?.OnOwnerDied(player, cause);
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] death report threw", ex);
            }
        }

        /// <summary>True unless the target is a fire's monster and the player does not own that fire.</summary>
        public static bool PlayerMayHarm(Player player, Creature target) =>
            target?.FraysTag == null || target.FraysTag.Fire.IsOwner(player);

        /// <summary>
        /// A fire monster's hit on a player. Difficulty comes from Heat alone: the lower of the monster's Heat and the
        /// defender's own (with the step at 5, 10, 15 and 20), the elite or Snarl multiplier and the affixes
        /// that raise it; then the defender's pools and Fray-only proof sets. Returns the damage to apply.
        /// </summary>
        /// <summary>A Frays creature's attack skill for one swing at a camper (attack_vs_defense); unchanged for anything else.</summary>
        public static uint AdjustAttackVsPlayer(Creature attacker, Creature defender, uint attack, uint defense)
        {
            var tag = attacker?.FraysTag;

            if (tag == null || defender is not Player player)
                return attack;

            try
            {
                return FraysRules.AttackVsDefense(attack, defense, FraysRules.HitHeat(tag.Heat, tag.Fire.LevelOf(player)),
                    FraysSettings.Number(FraysSettings.AttackVsDefense), FraysSettings.Number(FraysSettings.AttackVsDefensePerHeat));
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] attack vs defense threw", ex);
                return attack;
            }
        }

        /// <summary>A Frays fire creature's hit on a camper. Only a Frays fire tags creatures, so nothing off the island comes here.</summary>
        /// <param name="beforeMitigation">The hit before armor, resists and the camper's own damage cuts, when known (melee and
        /// missile). The hit cap is taken on it, then those cuts scale it down, so a Vanguard's mitigation still counts on a big hit.</param>
        public static float AdjustDamageToPlayer(Creature attacker, Player defender, float damage, DamageType type = DamageType.Undef, float beforeMitigation = 0)
        {
            var tag = attacker?.FraysTag;

            if (tag == null || defender == null || damage <= 0)
                return damage;

            try
            {
                var own = tag.Fire.LevelOf(defender);
                var hitHeat = FraysRules.HitHeat(tag.Heat, own);
                var mods = defender.FraysMods?.Fire == tag.Fire ? defender.FraysMods : null;

                // A Threads-shaped creature is built at the camper's own level, so Heat raises its hits through the damage curve.
                // With threads_shape_heat_levels it was built at the Heat's extra levels instead and skips the curve (no double count).
                // The creature's side: Heat, elite, the fire's own damage and the boons that make creatures hit harder.
                var heatMult = tag.ThreadsShaped && FraysSettings.Toggle(FraysSettings.ThreadsShapeHeatLevels) ? 1.0 : FraysCurve.Damage(hitHeat);
                var otherMult = 1.0
                    * tag.DamageMult
                    * FraysHeatShifts.DamageMult(attacker, tag);
                var creatureMult = heatMult * otherMult;

                // The camper's side: boon damage cuts.
                var camperMult = 1.0;

                if (mods != null)
                {
                    var onYou = tag.Fire.AliveSnapshot().Count(c => c.AttackTarget == defender);
                    creatureMult *= mods.MonsterDamage;
                    otherMult *= mods.MonsterDamage;
                    camperMult = mods.IncomingMult(type, onYou);
                }

                // Weapon hits sized to the camper's health (hit_by_health): heat_curve_hit_share replaces the Heat damage
                // curve, armor cuts at most hit_armor_max_cut, then the elite, Snarl and boon boosts, the cap and the cuts.
                // Spells and drains (no raw hit known) keep the armor-based hit.
                if (FraysSettings.Toggle(FraysSettings.HitByHealth) && beforeMitigation > 0 && FraysCurve.HitShare(hitHeat) is double share)
                {
                    var hit = FraysRules.HealthHit(defender.Health.MaxValue, share, damage / beforeMitigation,
                        FraysSettings.Number(FraysSettings.HitArmorMaxCut), ThreadSafeRandom.Next(0.0f, 1.0f)) * otherMult;
                    var sized = (float)(FraysRules.CapHit(hit, defender.Health.MaxValue, FraysCurve.HitCap(hitHeat)) * camperMult);
                    LogHit(attacker, defender, hitHeat, damage, beforeMitigation, otherMult, camperMult, sized);
                    FraysHeatShifts.OnHitPlayer(attacker, tag, defender, sized);
                    tag.Fire.OnDamageTaken(defender, type, sized);
                    return sized;
                }

                // No single hit takes more than heat_curve_hit_cap of the camper's max health, so heals always matter. The cap is
                // taken after armor and resists, then the camper's own damage cuts scale it (Vanguards stay useful).
                var result = (float)FraysRules.CapHitBeforeMitigation(damage, beforeMitigation, creatureMult, camperMult, defender.Health.MaxValue, FraysCurve.HitCap(hitHeat));

                // Spells and drains with hit_by_health: no bigger than spell_hit_share times a weapon hit's
                // share of max health, so a landed bolt is not a third of the camper's health. A weaker spell keeps its own hit.
                if (FraysSettings.Toggle(FraysSettings.HitByHealth) && beforeMitigation <= 0 && FraysCurve.HitShare(hitHeat) is double spellShare)
                    result = (float)FraysRules.SpellHit(result, defender.Health.MaxValue, spellShare * FraysSettings.Number(FraysSettings.SpellHitShare),
                        otherMult, camperMult, FraysCurve.HitCap(hitHeat), ThreadSafeRandom.Next(0.0f, 1.0f));

                LogHit(attacker, defender, hitHeat, damage, beforeMitigation, creatureMult, camperMult, result);
                FraysHeatShifts.OnHitPlayer(attacker, tag, defender, result);
                tag.Fire.OnDamageTaken(defender, type, result);
                return result;
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] damage adjust threw", ex);
                return damage;
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, DateTime> hitLogged = new();

        /// <summary>One creature hit on a camper, logged at most every 20 seconds per camper, so balance can be read from the log.</summary>
        private static void LogHit(Creature attacker, Player defender, int heat, float mitigated, float raw, double creatureMult, double camperMult, float result)
        {
            var now = DateTime.UtcNow;

            if (hitLogged.TryGetValue(defender.Guid.Full, out var last) && (now - last).TotalSeconds < 20)
                return;

            hitLogged[defender.Guid.Full] = now;
            log.Info($"[FRAYS] hit {attacker.Name} -> {defender.Name} at Heat {heat}: raw {raw:0}, after armor {mitigated:0}, creature x{creatureMult:0.##}, cuts x{camperMult:0.##}, took {result:0} of {defender.Health.MaxValue} health");
        }

        /// <summary>
        /// A hit from a player (or their pet) on one of their own fire's creatures: the boons, the
        /// pools and the week's resist affixes. Returns the damage to apply.
        /// </summary>
        public static float AdjustDamageToMonster(WorldObject source, Creature target, float damage, DamageType type, WorldObject weapon = null)
        {
            var tag = target?.FraysTag;

            if (tag == null || damage <= 0)
                return damage;

            try
            {
                var pet = source as CombatPet;
                var player = source as Player ?? pet?.P_PetOwner;
                var mods = player?.FraysMods;

                if (mods == null || mods.Fire != tag.Fire)
                    return damage;

                if (tag.FirstHitAt <= 0)
                    tag.FirstHitAt = Time.GetUnixTime();   // a Gilded pack's fight starts here (rich_rest_fight_mult)

                weapon ??= player.GetEquippedWeapon() ?? player.GetEquippedWand();
                var result = (float)(damage * mods.OutgoingMult(target, type, weapon, pet != null));
                result = FraysHeatShifts.OnHitMonster(target, tag, result);

                return result;
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] outgoing damage adjust threw", ex);
                return damage;
            }
        }

        /// <summary>
        /// The class boon on one class ability while camping: 1 with none. Never off the Frays (boons go when you leave
        /// the Frays): the fire drops a camper's modifiers on its next tick
        /// once they leave the zone, and this check covers the moment between.
        /// </summary>
        public static double AbilityBoost(Player player, ACE.Server.ClassAbilities.ClassAbilityId ability)
        {
            var m = player?.FraysMods;
            return m != null && m.AbilityBoost.TryGetValue(ability, out var v) && OnFrays(player.Location) ? v : 1;
        }

    }
}
