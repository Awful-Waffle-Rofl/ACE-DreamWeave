using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Fray town's houses and city walls: 17 models picked from the catalogue, every door and window blocked, for look
    /// only. Models 12 to 17 are the city walls and corners, with no doors on their sides. Each is a solid shell (WCIDs 2000124-2000140, the crate's object with its
    /// model swapped). Town Houses 1 to 11 have every opening covered when placed: doorways with the locked town door
    /// (2000123), windows with the lattice window (2000141, the door repainted). City Walls 12 to 17 get no covers.
    /// The openings are each model's own opening faces (the clear or black panels in its doorways and windows), with the
    /// way out of the house found by rays; /frays admin cover fixes any one by hand, for every copy of that house.
    /// </summary>
    public static class FraysHouses
    {
        public const uint FirstWcid = 2000124;
        public const uint WindowCover = 2000141;

        /// <summary>The models, in their set order: number 1 is FirstWcid. 12 to 17 are city walls.</summary>
        public static readonly uint[] Models =
        {
            0x0100222F, 0x010022EF, 0x010022CE, 0x010025E3, 0x010025E0, 0x0100081C, 0x01001117, 0x01000830, 0x01000E11,
            0x01000827, 0x01000C87, 0x01000802, 0x01000E05, 0x01000E00, 0x01000E03, 0x01000E0B, 0x01003AB1,
        };

        /// <summary>The first city wall's number (12): from here on, no covers.</summary>
        public const int FirstWall = 12;

        public static IEnumerable<uint> Wcids => Models.Select((m, i) => FirstWcid + (uint)i);

        /// <summary>
        /// Arwic's own buildings that get covers too (the Great Hall, the Chapel and the Cottage): numbered after the city walls.
        /// </summary>
        public static readonly IReadOnlyDictionary<uint, (int Number, uint Model, string Name)> Extras = new Dictionary<uint, (int, uint, string)>
        {
            [FraysDefs.ArwicCottage] = (18, 0x01000C17, "Cottage"),
            [FraysDefs.ArwicGreatHall] = (19, 0x01002383, "Great Hall"),
            [FraysDefs.ArwicChapel] = (20, 0x0100228B, "Chapel"),
            // The village ring's watchtowers (every tower and wall has covers): a doorway and two
            // upper windows. Arwic's wall section has no openings.
            [FraysDefs.ArwicTower] = (21, 0x01002D1B, "Watchtower"),
        };

        public static uint WcidOf(int number)
        {
            foreach (var (wcid, e) in Extras)
                if (e.Number == number) return wcid;
            return FirstWcid + (uint)number - 1;
        }

        /// <summary>What the player is told the building is: "Town House 3", "Chapel".</summary>
        public static string Label(uint wcid) => Extras.TryGetValue(wcid, out var e) ? e.Name : $"Town House {Number(wcid)}";

        /// <summary>One opening: its centre (x, y) and bottom in the model's frame, its width and height, the way out (degrees).</summary>
        public readonly record struct Opening(float X, float Y, float Bottom, float Width, float Height, float Face);

        /// <summary>
        /// Per model: its drawn footprint (x and y bounds) and its openings, from the cell dat: each opening face's
        /// centre, bottom, width, height and the facing that points out of the house.
        /// </summary>
        private static readonly Dictionary<uint, ((float MinX, float MaxX, float MinY, float MaxY) Footprint, (float, float, float, float, float, float)[] Openings)> Data = new()
        {
            [0x0100222F] = ((-7.0f, 7.6f, -5.8f, 4.2f), new[] { (-3.35f, -5.40f, 0.00f, 1.90f, 2.50f, 180f), (3.00f, 3.40f, 1.00f, 3.00f, 1.90f, -0f) }),
            [0x010022EF] = ((-6.8f, 7.0f, -10.9f, 4.7f), new[] { (5.69f, -0.04f, 1.00f, 2.93f, 1.90f, -90f), (-0.80f, -10.59f, 0.00f, 1.90f, 2.50f, 180f) }),
            [0x010022CE] = ((-11.8f, 11.8f, -11.8f, 11.9f), new[] { (5.20f, -11.14f, 5.80f, 2.20f, 1.90f, 180f), (-10.94f, 8.40f, 5.80f, 2.20f, 1.90f, 90f), (-8.15f, -11.40f, 0.00f, 1.90f, 2.50f, 180f), (-5.22f, -6.90f, 0.00f, 1.90f, 2.50f, -90f), (-5.50f, 2.42f, 0.00f, 1.90f, 2.50f, 0f) }),
            [0x010025E3] = ((-3.5f, 9.9f, -8.6f, 3.6f), new[] { (-3.25f, -0.10f, 1.00f, 1.90f, 2.55f, 90f), (6.50f, -8.48f, 0.00f, 1.92f, 2.50f, 180f) }),
            [0x010025E0] = ((-5.3f, 10.8f, -5.8f, 4.4f), new[] { (0.20f, 3.30f, 1.00f, 3.00f, 1.90f, -0f), (6.50f, -4.50f, 0.00f, 1.90f, 2.50f, -180f) }),
            [0x0100081C] = ((-5.8f, 5.8f, -6.6f, 6.6f), new[] { (0.00f, 6.00f, 0.00f, 3.00f, 2.50f, 0f), (0.00f, 6.00f, 7.00f, 2.00f, 1.00f, 180f), (0.00f, -6.00f, 7.00f, 2.00f, 1.00f, 0f), (0.00f, -6.00f, 0.00f, 3.00f, 2.50f, 180f) }),
            [0x01001117] = ((-7.0f, 7.0f, -7.0f, 7.0f), new[] { (3.05f, -4.50f, 8.80f, 1.90f, 2.50f, 180f), (-0.01f, -5.00f, 0.00f, 1.88f, 2.50f, 180f) }),
            [0x01000830] = ((-6.7f, 7.6f, -6.1f, 5.4f), new[] { (-6.20f, -0.35f, 4.00f, 1.10f, 1.50f, 90f), (-2.85f, -5.65f, 1.00f, 1.10f, 1.50f, 180f), (3.45f, -5.65f, 1.00f, 1.10f, 1.50f, -180f), (-6.20f, 2.10f, 0.00f, 1.50f, 2.50f, 90f), (0.35f, -5.65f, 0.00f, 1.50f, 2.50f, -180f) }),
            [0x01000E11] = ((-11.8f, 7.5f, -11.0f, 11.0f), new[] { (-2.00f, 4.50f, 0.00f, 2.00f, 2.80f, -90f), (1.50f, -9.50f, 3.60f, 2.00f, 2.80f, -90f), (-8.50f, 9.50f, 4.00f, 2.00f, 2.80f, 90f), (3.50f, 1.50f, 0.00f, 2.00f, 2.80f, 0f) }),
            [0x01000827] = ((-4.8f, 7.2f, -7.5f, 6.1f), new[] { (2.20f, -3.88f, 3.00f, 1.76f, 2.50f, -90f), (-1.45f, -6.40f, 4.00f, 1.00f, 1.50f, -180f), (6.40f, 1.50f, 4.00f, 1.00f, 1.50f, -90f), (6.40f, 3.50f, 4.00f, 1.00f, 1.50f, -90f), (6.00f, 1.50f, 1.00f, 1.00f, 1.50f, -90f), (4.68f, -1.45f, 0.00f, 1.90f, 2.50f, -90f), (0.20f, -6.40f, 0.00f, 1.90f, 2.50f, 180f), (3.32f, -0.10f, 3.00f, 1.76f, 2.50f, 180f) }),
            [0x01000C87] = ((-11.0f, 11.0f, -8.5f, 8.5f), new[] { (0.00f, 5.50f, 0.00f, 2.00f, 2.50f, 180f), (0.00f, -5.50f, 0.00f, 2.00f, 2.50f, 0f) }),
            // Arwic's buildings (Extras). The Great Hall's two inside doorways are left out; its front door showed as two faces.
            [0x01000C17] = ((-11.2f, 12.0f, -11.0f, 12.0f), new[] { (0.00f, 11.00f, 0.00f, 1.90f, 2.50f, 0f), (0.00f, -10.00f, 0.00f, 1.90f, 2.50f, 180f), (-5.00f, 0.00f, 3.50f, 1.90f, 2.50f, 90f) }),
            // The front door at the back of its porch (2.71, 6.95) is no portal in the cell dat, so it is added from retail's door
            // there (it left a gap).
            [0x01002383] = ((-11.7f, 11.7f, -11.8f, 11.7f), new[] { (0.00f, -11.19f, 5.80f, 2.90f, 1.90f, -180f), (10.30f, 5.00f, 5.80f, 2.20f, 1.90f, -90f), (-10.40f, 8.00f, 5.80f, 2.20f, 1.90f, 90f), (9.18f, -9.50f, 0.00f, 1.90f, 2.50f, -138f), (2.71f, 6.95f, 0.00f, 1.92f, 2.50f, 0f) }),
            [0x0100228B] = ((-6.3f, 9.6f, -5.3f, 5.1f), new[] { (6.52f, 4.70f, 1.35f, 2.35f, 2.55f, 0f), (-0.95f, -4.60f, 0.00f, 1.90f, 2.50f, -180f) }),
            [0x01002D1B] = ((-12.0f, -5.0f, -12.0f, -5.0f), new[] { (-10.89f, -10.89f, 0.08f, 1.20f, 2.52f, 135f), (-8.83f, -11.54f, 5.94f, 2.06f, 2.65f, -180f), (-11.55f, -8.57f, 5.94f, 2.15f, 2.70f, 90f) }),
            [0x01003AB1] = ((-12.0f, 12.0f, -12.0f, 5.5f), Array.Empty<(float, float, float, float, float, float)>()),   // a city wall: no covers
            [0x01000E05] = ((-3.0f, 3.0f, -12.0f, 12.0f), Array.Empty<(float, float, float, float, float, float)>()),   // a city wall: no covers
            [0x01000E0B] = ((-3.0f, 3.0f, -12.0f, 12.0f), Array.Empty<(float, float, float, float, float, float)>()),   // a city wall: no covers
            [0x01000E00] = ((-12.0f, 4.9f, -5.3f, 12.0f), Array.Empty<(float, float, float, float, float, float)>()),   // a city wall: no covers
            [0x01000802] = ((-11.1f, 12.0f, -12.0f, 12.0f), Array.Empty<(float, float, float, float, float, float)>()),   // a city wall: no covers
            [0x01000E03] = ((-12.0f, 3.0f, -3.0f, 12.0f), Array.Empty<(float, float, float, float, float, float)>()),   // a city wall: no covers
        };

        public static bool IsHouse(uint wcid) => (wcid >= FirstWcid && wcid < FirstWcid + Models.Length) || Extras.ContainsKey(wcid);

        public static int Number(uint wcid) => Extras.TryGetValue(wcid, out var e) ? e.Number : (int)(wcid - FirstWcid) + 1;

        public static bool IsWall(uint wcid) => IsHouse(wcid) && !Extras.ContainsKey(wcid) && Number(wcid) >= FirstWall;

        public static uint Model(uint wcid) => Extras.TryGetValue(wcid, out var e) ? e.Model : Models[wcid - FirstWcid];

        public static bool TryFootprint(uint wcid, out (float MinX, float MaxX, float MinY, float MaxY) footprint)
        {
            footprint = default;
            if (!IsHouse(wcid) || !Data.TryGetValue(Model(wcid), out var d)) return false;
            footprint = d.Footprint;
            return true;
        }

        public static List<Opening> Openings(uint wcid)
        {
            if (!IsHouse(wcid) || IsWall(wcid) || !Data.TryGetValue(Model(wcid), out var d))
                return new List<Opening>();

            var list = d.Openings.Select(o => new Opening(o.Item1, o.Item2, o.Item3, o.Item4, o.Item5, o.Item6)).ToList();

            // Then the openings added by hand, numbered on from the model's own (such as a
            // doorway of the Great Hall that retail never put a door in).
            lock (fixSync)
                for (var k = list.Count; Fixes.TryGetValue(Key(wcid, k), out var f) && f.At is { Length: 6 } at; k++)
                    list.Add(new Opening(at[0], at[1], at[2], at[3], at[4], at[5]));

            return list;
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Hand fixes: per house and opening, saved in the frays_house_covers setting.
        // ---------------------------------------------------------------------------------------------------------------

        /// <summary>A hand fix to one opening's cover (metres; Kind "door", "window" or "none", empty = as worked out).</summary>
        public sealed class Fix
        {
            public float Out { get; set; }
            public float Side { get; set; }
            public float Up { get; set; }
            public float Size { get; set; } = 1;
            public bool Flip { get; set; }
            public string Kind { get; set; } = "";
            /// <summary>Degrees turned about its own middle, counterclockwise from above.</summary>
            public float Turn { get; set; }
            /// <summary>An opening added by hand (/frays admin cover add), in the model's frame: x, y, bottom, width, height, face.</summary>
            public float[] At { get; set; }
            /// <summary>Degrees leaned about its own width (its top toward or away from the wall).</summary>
            public float Tilt { get; set; }
        }

        /// <summary>
        /// The built-in fixes. Key: "house number:opening number".
        /// </summary>
        private static Dictionary<string, Fix> BuiltInFixes() => new()
        {
            // House 1's back windows had Out -0.2 here; dropped once covers went ethereal (it sank them into the wall).
            // House 5's back windows had Out -0.1 here; dropped for the same reason.
            ["11:1"] = new Fix { Out = 0.15f },   // House 11's Sho doors stood proud of the wall (fitted live)
            ["11:2"] = new Fix { Out = 0.15f },
            // The watchtower's two upper doorways (where the wall walks meet it): the stone slab at the hole's inside face (its spare width hides in the wall;
            // flush, it wrapped the tower's corners), lowered and enlarged to close the sky under it (fitted live).
            ["21:2"] = new Fix { Up = -0.9f, Size = 1.3f },
            ["21:3"] = new Fix { Up = -0.9f, Size = 1.3f },
            // The Great Hall's corner doorway has two black panels about 1 m apart. Its own door sat between them and poked out
            // beside the flush pair on the inner panel (19:7, 19:8), so it is off (two doors in all, one on each side).
            ["19:4"] = new Fix { Kind = "none" },
            // Its other outside door, on a leaning wall: tilted to lie along it so its top does not poke out (fitted live).
            ["19:5"] = new Fix { Tilt = -5f },
            // An added door (opening 6) in the Great Hall's inside doorway off the main room, also a black panel (fitted live: square to
            // the hall at -90, not the scan's -77, 0.2 m out to clear the panel's lean, and tilted to lie in it (-9, set in game); the flush door so the client cannot lift it off the floor).
            ["19:6"] = new Fix { Kind = "flush", Out = 0.2f, Tilt = -9f, At = new[] { 3.82f, -1.65f, 0f, 1.96f, 2.5f, -90f } },
            // Opening 7: the inside of the corner entrance, the black panel behind the front door (the scan's inner panel), fitted live.
            ["19:7"] = new Fix { Kind = "flush", Out = 0.05f, Tilt = -1f, At = new[] { 8.83f, -9.12f, 0f, 1.9f, 2.5f, 42f } },
            // Opening 8: the doorway's outside door, on its outer black panel facing out. With opening 7 on the inner panel that makes one door on each side.
            ["19:8"] = new Fix { Kind = "flush", Out = 0.05f, Flip = true, At = new[] { 9.52f, -9.88f, 0f, 1.9f, 2.5f, 42f } },
            // Opening 9: the hall's other inside doorway, on its leaning black panel like opening 6.
            ["19:9"] = new Fix { Kind = "flush", Out = 0.2f, Tilt = -9f, At = new[] { -6.6f, 1.64f, 0f, 1.96f, 2.5f, 180f } },
            // The Cottage's upstairs door, onto its roof deck: the flush door
            // exactly at the cell dat's gable doorway, facing the deck. Its own opening (3) is off so there is one door there.
            // The town door there arrived solid at login and the game shoved it off the doorway.
            ["18:3"] = new Fix { Kind = "none" },
            ["18:4"] = new Fix { Kind = "flush", At = new[] { -5f, 0f, 3.505f, 1.9f, 2.5f, 90f } },
            // House 9's upper "windows" are one balcony doorway drawn as two panels, so its table row is that one doorway
            // (with Gharu doors, and no extra windows).
        };

        private static readonly object fixSync = new object();
        private static Dictionary<string, Fix> fixes;

        private static Dictionary<string, Fix> Fixes
        {
            get
            {
                lock (fixSync)
                {
                    if (fixes != null)
                        return fixes;

                    var saved = FraysSettings.Text(FraysSettings.HouseCovers);

                    if (saved.Length > 2)
                    {
                        try { fixes = JsonSerializer.Deserialize<Dictionary<string, Fix>>(saved); }
                        catch { fixes = null; }
                    }

                    fixes ??= new Dictionary<string, Fix>();

                    // The built-in fixes, where no hand fix has replaced them.
                    foreach (var (key, fix) in BuiltInFixes())
                        fixes.TryAdd(key, fix);

                    return fixes;
                }
            }
        }

        private static string Key(uint wcid, int opening) => $"{Number(wcid)}:{opening + 1}";

        private static System.Threading.Timer liveTimer;
        private static DateTime liveStamp;

        /// <summary>
        /// Dev tuning: frays-cover-fixes.json beside the server, in the
        /// setting's shape, is read each second when it changes; its fixes replace those keys (and are saved) and the houses'
        /// covers go up again. The tuned values then go into the code.
        /// </summary>
        private static void WatchLiveFixes()
        {
            if (liveTimer != null)
                return;

            liveTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var path = System.IO.Path.Combine(AppContext.BaseDirectory, "frays-cover-fixes.json");
                    if (!System.IO.File.Exists(path))
                        return;

                    var stamp = System.IO.File.GetLastWriteTimeUtc(path);
                    if (stamp == liveStamp)
                        return;

                    liveStamp = stamp;
                    var live = JsonSerializer.Deserialize<Dictionary<string, Fix>>(System.IO.File.ReadAllText(path));
                    var houses = new HashSet<uint>();
                    string text;

                    lock (fixSync)
                    {
                        foreach (var (key, fix) in live)
                        {
                            Fixes[key] = fix;
                            houses.Add(WcidOf(int.Parse(key.Split(':')[0])));
                        }

                        text = JsonSerializer.Serialize(Fixes);
                    }

                    foreach (var wcid in houses)
                        Recover(wcid);

                    // Saved too, from the server's own copy: a fix written straight to the database would be
                    // overwritten when an in-game change saved the server's older copy.
                    if (FraysSettings.TryFind(FraysSettings.HouseCovers, out var setting))
                        ACE.Server.Managers.PropertyAdminService.TryModify(ACE.Server.Managers.PropertyAdminService.TypeString, setting.PropertyName, text, ACE.Server.Managers.PropertyAdminActor.Web("frays live tuning"));
                }
                catch (Exception) { }
            }, null, 2000, 1000);
        }

        public static Fix FixFor(uint wcid, int opening)
        {
            lock (fixSync)
                return Fixes.TryGetValue(Key(wcid, opening), out var f) ? f : null;
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Covers
        // ---------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A door model's drawn size at scale 1: width and height (from its base), and its depth, with where the middle of that
        /// depth sits from its origin along its own +y. The Aluvian door (retail 412) is 1.92 wide, 2.5 tall, 0.25 deep, all
        /// on one side of its origin; the Sho sliding door (720) 2.12 by 2.55, 0.4 deep, centred; the Gharu double door (1148)
        /// 2.0 by 2.8, 0.3 deep, centred. The lattice window (2000141) is the Aluvian door repainted.
        /// </summary>
        public readonly record struct DoorStyle(string Name, uint Wcid, float Width, float Height, float Depth, float DepthMiddle);

        public static readonly DoorStyle Aluvian = new("aluvian", FraysDefs.TownDoor, 1.92f, 2.49f, 0.25f, 0.125f);
        public static readonly DoorStyle Sho = new("sho", 2000142, 2.12f, 2.55f, 0.4f, 0f);
        public static readonly DoorStyle Gharu = new("gharu", 2000143, 2.0f, 2.8f, 0.3f, 0.01f);

        // The stone slab (retail 25669, the secret door: dark grey stone brick, setup 0x02000FF1, centred, base at 0) for the stone
        // watchtowers and walls (wooden doors do not match them). Wider than a doorway,
        // the extra runs into the wall either side and reads as wall. (Retail 4453's iron gate, tried first, has posts and a
        // lintel and showed as a box.)
        public static readonly DoorStyle Stone = new("stone", 2000148, 3.34f, 3.0f, 0.5f, 0.09f);
        private static readonly DoorStyle Lattice = Aluvian with { Name = "window", Wcid = WindowCover };

        // The town door's leaf alone on the hung setup (2000149, no collision sphere), so the client cannot lift it off ground
        // that rises toward its doorway (as at the Great Hall's inside door, which left a gap at the bottom). Drawn
        // just as the Aluvian door is; it has no handles.
        public static readonly DoorStyle Flush = Aluvian with { Name = "flush", Wcid = 2000149 };

        /// <summary>The Aluvian door leaf's middle in the door's frame when shut (setup 0x020019FF, motion 0x09000202's closed cycle).</summary>
        public static readonly Vector3 AluvianLeaf = new(0f, 0.13f, 1.18f);

        public static readonly DoorStyle[] Styles = { Aluvian, Sho, Gharu, Stone, Flush };

        /// <summary>The houses whose town doors are placed as the flush door: the Cottage (18) and the Great Hall (19).</summary>
        private static readonly HashSet<int> FlushHouses = new() { 18, 19 };

        /// <summary>
        /// Each house's door style, from the doors retail puts at that model's doorways (Aluvian, Sho and Gharu style
        /// houses each use their own door models): Town House 9 is Gharu, 11 is Sho, the stone walls (12-17) and watchtower (21) stone, the rest Aluvian.
        /// </summary>
        public static DoorStyle StyleOf(int house) => house switch { 9 => Gharu, 11 => Sho, 21 => Stone, >= FirstWall and < 18 => Stone, _ => Aluvian };

        /// <summary>How much bigger than its opening a cover is made, each way, so no gaps show round doors and windows.</summary>
        public const float Margin = 0.2f;

        /// <summary>How far in front of the opening the cover's outer face sits (in front of a drawn-black opening, no flicker).</summary>
        public const float OutsideBy = 0.01f;

        /// <summary>A cover for one opening: what, where (in the house's frame, z from the house's base), facing, size.</summary>
        public readonly record struct Cover(uint Wcid, Vector3 Local, float Face, float Scale, float Tilt = 0);

        /// <summary>
        /// Pure: whether an opening is a doorway. Tall ones on the floor, or high up (a balcony's door); a tall one a step up
        /// from the floor is a window (so house 4's windows do not get doors).
        /// </summary>
        public static bool IsDoorway(Opening o) => o.Height >= 2.2f && (o.Bottom < 0.3f || o.Bottom >= 2.5f);

        /// <summary>
        /// Pure: the covers for one opening: a doorway gets the house's door style (style), a window the lattice window, sized
        /// to it with a margin and split across a wide one. In a doorway it stands on the floor; in a window it is centred on
        /// the window. It goes into the wall, its outer face just in front of the opening, so it does not look
        /// popped out: turned so its depth runs inward. A hand fix moves, sizes, turns about or swaps it (its Kind:
        /// "door" for the house's style, "aluvian", "sho", "gharu" or "stone" for that one, "window", or "none").
        /// </summary>
        public static List<Cover> CoversFor(Opening o, Fix fix = null, DoorStyle? style = null)
        {
            var list = new List<Cover>();
            var kind = fix?.Kind is { Length: > 0 } fk ? fk : IsDoorway(o) ? "door" : "window";

            if (kind == "none")
                return list;

            var doorway = kind != "window";
            var d = kind == "window" ? Lattice : Styles.FirstOrDefault(s => s.Name == kind) is { Name: not null } named ? named : style ?? Aluvian;
            float w = o.Width + Margin, h = o.Height + (doorway ? Margin / 2 : Margin);
            var n = Math.Max(1, (int)Math.Round(w / (h * d.Width / d.Height)));
            var seg = w / n;
            var k = Math.Max(seg / d.Width, h / d.Height) * (fix?.Size > 0 ? fix.Size : 1);
            var a = o.Face * Math.PI / 180;
            var outward = new Vector2((float)-Math.Sin(a), (float)Math.Cos(a));
            if (fix?.Flip == true)
                outward = -outward;
            var along = new Vector2(outward.Y, -outward.X);    // the opening's width, left to right as seen from outside

            // The door's depth runs along its own +y, which a turn of F degrees points at (-sin F, cos F): point it inward, and
            // set its outer face (its depth's middle less half its depth) just outside the opening.
            var inward = -outward;
            var face = (float)(Math.Atan2(-inward.X, inward.Y) * 180 / Math.PI);
            var z = (doorway ? o.Bottom : o.Bottom + o.Height / 2 - d.Height * k / 2) + (d.Wcid == Aluvian.Wcid || d.Wcid == WindowCover || d.Wcid == Flush.Wcid ? AluvianDrop * k : 0) + (fix?.Up ?? 0);
            var centre = new Vector2(o.X, o.Y) + outward * (OutsideBy + (fix?.Out ?? 0)) - inward * ((d.DepthMiddle - d.Depth / 2) * k) + along * (fix?.Side ?? 0);

            for (var i = 0; i < n; i++)
            {
                var t = (i - (n - 1) / 2f) * seg;
                list.Add(new Cover(d.Wcid, new Vector3(centre + along * t, z), face, k));
            }

            return list;
        }

        /// <summary>Each placed cover (guid): its house object and the opening it covers.</summary>
        private static readonly ConcurrentDictionary<uint, (uint House, int Opening)> coverOf = new();

        /// <summary>Each covered house (guid): the house, its facing and size, and its covers' guids.</summary>
        private static readonly ConcurrentDictionary<uint, (WorldObject House, float Facing, float Scale, List<uint> Covers)> covered = new();

        /// <summary>Called with a house and its new covers when they are placed again after a hand fix (the village adopts them).</summary>
        public static Action<WorldObject, List<WorldObject>> Recovered;

        /// <summary>
        /// The doors retail puts on each house: for every
        /// copy of each model on the retail map, its doors in the house's own frame; these are the ones on most copies (house
        /// 11's two doorway doors, and house 9's back doorway door, on about half or more). Each: our locked copy of that
        /// retail door (412 Aluvian 2000123, 720 Sho sliding 2000142, 722 Sho 2000144, 1148 Gharu 2000143, 2181 Aluvian tower
        /// 2000145), where, how high, and facing (degrees, as the house's own facing), at full size. House 6 has none.
        /// Left out: house 9's door between two inside rooms (no opening there), and house 7's upper door (its see-through
        /// opening gets a door sized to it instead); then house 11's side sliding doors
        /// (no opening there). House 10's upper doors stay: they open onto its roof terrace (its upper openings face out there).
        /// </summary>
        private static readonly Dictionary<int, (uint Wcid, float X, float Y, float Z, float Face)[]> RetailDoors = new()
        {
            [1] = new[] { (2000123u, -3.35f, -5.41f, 0f, 0f) },
            [2] = new[] { (2000123u, -0.80f, -10.60f, 0f, 0f) },
            [3] = new[] { (2000123u, -5.20f, -6.90f, 0f, 90f), (2000123u, -5.50f, 2.39f, 0f, 0f), (2000123u, -8.15f, -11.08f, 0f, 180f) },
            [4] = new[] { (2000123u, 6.50f, -8.36f, 0f, 0f) },
            [5] = new[] { (2000123u, 6.50f, -4.51f, 0f, 0f) },
            [7] = new[] { (2000145u, -0.03f, -5.04f, 0.08f, 0f) },
            [8] = new[] { (2000123u, -6.22f, 2.07f, 0f, -91f), (2000123u, 0.34f, -5.67f, 0f, 0f) },
            [9] = new[] { (2000143u, -1.99f, 4.50f, 0f, 90f), (2000143u, 3.50f, 1.51f, 0f, 180f) },
            [10] = new[] { (2000123u, 0.20f, -6.41f, 0.01f, 0f), (2000123u, 2.21f, -3.81f, 3.00f, 90f), (2000123u, 3.25f, -0.11f, 3.00f, 0f), (2000123u, 4.70f, -1.46f, 0f, 90f) },
            [11] = new[] { (2000144u, 0f, 5.53f, 0f, 180f), (2000144u, 0f, -5.53f, 0f, 0f) },
            // The Great Hall's front door: retail's Aluvian door 412 in its porch (retail 0x3EAC 153.29, 101.05, the hall turned 180).
            [19] = new[] { (2000123u, 2.71f, 6.95f, 0f, 180f) },
        };

        /// <summary>Each retail door copy's depth along its own +y at full size: from, to (Aluvian 412 all on one side).</summary>
        private static (float From, float To) DepthOf(uint wcid) => wcid switch
        {
            FraysDefs.TownDoor => (0f, 0.25f),
            2000143 => (-0.14f, 0.16f),
            _ => (-0.2f, 0.2f),
        };

        /// <summary>
        /// Pure: where a retail door goes so its outer face is just in front of its opening (house 4's
        /// door: retail sets it 12 cm inside the doorway, behind the black panel our shell draws there, so it is hidden; house 3's
        /// door: retail sets that one sticking 22 cm out). Every retail door goes flush, in or out.
        /// </summary>
        public static Vector2 InFrontOf(Opening o, uint wcid, Vector2 spot, float face, float scale = 1)
        {
            var a = o.Face * Math.PI / 180;
            var outward = new Vector2((float)-Math.Sin(a), (float)Math.Cos(a));
            var f = face * Math.PI / 180;
            var depthWay = Vector2.Dot(new Vector2((float)-Math.Sin(f), (float)Math.Cos(f)), outward);
            var (from, to) = DepthOf(wcid);
            var outer = Vector2.Dot(spot - new Vector2(o.X, o.Y), outward) + Math.Max(depthWay * from, depthWay * to) * scale;
            return spot + outward * (OutsideBy - outer);
        }

        /// <summary>
        /// Pure: a retail door's facing squared to its opening's wall, when it is within 15 degrees of facing straight in
        /// (house 4's door: that wall is 8 degrees off square).
        /// </summary>
        public static float Square(Opening o, float face)
        {
            var straightIn = o.Face + 180;
            var off = ((face - straightIn) % 360 + 540) % 360 - 180;
            return Math.Abs(off) < 15 ? face - off : face;
        }

        /// <summary>
        /// Openings whose panel leans in with its wall (degrees, top in; key "house:opening"): house 4's adobe front leans 6
        /// degrees, so its door is tilted to fit. Every other opening stands upright.
        /// </summary>
        private static readonly Dictionary<string, float> Leans = new() { ["4:2"] = 6f };

        /// <summary>
        /// Covers set by hand in ACE Creator (key "house:opening"; position, height and facing in the house's frame, and size):
        /// used exactly as placed, with no fitting. House 3's corner door (in-game nudges never sat it flush, so a town door
        /// was placed against a Town House 3 in ACE Creator: house at 190.27 degrees, door at 9.14).
        /// A hand fix still nudges one.
        /// </summary>
        private static readonly Dictionary<string, (uint Wcid, float X, float Y, float Z, float Face, float Scale)> Exact = new()
        {
            ["3:5"] = (FraysDefs.TownDoor, -5.489f, 2.637f, 0.027f, 178.87f, 1f),
            // Houses 7, 9 and 10 the same way (windows fitted as usual, doors as placed in ACE Creator). House 7's ground door is a town door here, as placed. House 9's were refitted
            // against the in-game house (at 82.5156, 153, facing 0) with doors 7E7EA00D, 00E, 00F and 010.
            ["7:1"] = (FraysDefs.TownDoor, 3.067f, -4.275f, 8.810f, -178.86f, 1.1f),   // 10% bigger to close the gap at the top
            ["7:2"] = (FraysDefs.TownDoor, -0.027f, -5.016f, 0.027f, -0.85f, 1f),
            ["9:1"] = (2000143u, -1.999f, 4.507f, 0.055f, 89.57f, 1f),
            ["9:2"] = (2000143u, 1.495f, -9.498f, 3.595f, 91.65f, 1f),
            ["9:3"] = (2000143u, -8.493f, 9.512f, 4.005f, -89.98f, 1f),   // fitted live
            ["9:4"] = (2000143u, 3.496f, 1.488f, 0f, -179.02f, 1f),
            ["10:1"] = (FraysDefs.TownDoor, 1.946f, -3.860f, 3.000f, -90.22f, 1.05f),   // 5% bigger to close the gap at the top
            ["10:6"] = (FraysDefs.TownDoor, 4.695f, -1.452f, 0.027f, 91.62f, 1f),
            ["10:7"] = (FraysDefs.TownDoor, 0.186f, -6.411f, 0.027f, -2.35f, 1f),
            ["10:8"] = (FraysDefs.TownDoor, 3.255f, -0.089f, 2.990f, -1.41f, 1.1f),   // 10% bigger to close the gap at the top
        };

        /// <summary>The floor tile (dreamweave-content 2000146): the plank model 0x01000E2A, 2 m square and 10 cm thick at full size, centred on its origin.</summary>
        public const uint FloorTile = 2000146;
        private const float FloorTileSize = 2f, FloorTileThick = 0.1f;

        /// <summary>
        /// Floors laid where a house model has none (Town House 7's 3rd floor,
        /// at 15.2 m, is a U round a 4 by 6 m hole running to the north wall; its 2nd floor, at 8.8 m, a ledge
        /// round a 9 m hole, gets no floor).
        /// The middles were in retail's rooms. Per house, each patch: its
        /// middle in the house's frame, its top's height, its size each way and how many tiles each way. Kept just inside
        /// the hole: a solid tile overlapping the house is shoved off by the game's collision.
        /// </summary>
        private static readonly Dictionary<int, (float X, float Y, float Top, float W, float D, int Nx, int Ny)[]> FloorPatches = new()
        {
            [7] = new[] { (0f, 0.975f, 15.2f, 4.02f, 6.08f, 2, 3) },   // grown 3% live to close its edges
        };

        /// <summary>Pure: a house's floor tiles (a hand fix under key "house:floor" moves and sizes them all).</summary>
        public static List<Cover> Floors(uint wcid, Fix fix = null)
        {
            var list = new List<Cover>();
            if (!IsHouse(wcid) || !FloorPatches.TryGetValue(Number(wcid), out var patches))
                return list;

            var grow = fix?.Size > 0 ? fix.Size : 1f;
            foreach (var p in patches)
            {
                float w = p.W * grow, d = p.D * grow, tw = w / p.Nx, td = d / p.Ny;
                var scale = Math.Max(tw, td) / FloorTileSize;
                var top = p.Top + (fix?.Up ?? 0);
                for (var i = 0; i < p.Nx; i++)
                    for (var j = 0; j < p.Ny; j++)
                        list.Add(new Cover(FloorTile, new Vector3(p.X + (fix?.Side ?? 0) - w / 2 + (i + 0.5f) * tw,
                            p.Y + (fix?.Out ?? 0) - d / 2 + (j + 0.5f) * td, top - FloorTileThick * scale / 2), 0, scale));
            }
            return list;
        }

        /// <summary>A facing with a door's tilt about its own width (degrees; + tips its top toward its own -y).</summary>
        public static Quaternion Tilted(Quaternion facing, float tilt) =>
            tilt == 0 ? facing : facing * Quaternion.CreateFromAxisAngle(Vector3.UnitX, tilt * (float)Math.PI / 180f);

        /// <summary>Each retail door copy's drawn width at full size.</summary>
        private static float WidthOf(uint wcid) => wcid switch
        {
            FraysDefs.TownDoor => 1.92f,
            2000143 => 2.0f,
            2000145 => 1.98f,
            _ => 2.12f,
        };

        /// <summary>Each retail door copy's drawn height at full size (from its base).</summary>
        private static float HeightOf(uint wcid) => wcid switch
        {
            FraysDefs.TownDoor => 2.49f,
            2000143 => 2.8f,
            _ => 2.55f,
        };

        /// <summary>
        /// How far below its own base the Aluvian door (and the lattice window, the same model) is drawn closed: 6 cm at full
        /// size (its closed animation frame, which also makes it 2.49 m tall, not 2.5); it is set that much higher.
        /// </summary>
        public const float AluvianDrop = 0.06f;

        /// <summary>
        /// Pure: a retail door's size: big enough to cover its opening and overlap the frame by the margin each way, never
        /// smaller than full size (so no gap shows round doors such as house 9's back door).
        /// </summary>
        public static float FitScale(Opening o, uint wcid) =>
            Math.Max(1f, Math.Max((o.Width + Margin) / WidthOf(wcid), (o.Height + Margin / 2) / HeightOf(wcid)));

        /// <summary>Pure: the opening a retail door stands in (within a metre, its base near the opening's), or -1.</summary>
        public static int OpeningAt(List<Opening> openings, float x, float y, float z)
        {
            for (var i = 0; i < openings.Count; i++)
                if (Vector2.Distance(new Vector2(openings[i].X, openings[i].Y), new Vector2(x, y)) < 1.0f && Math.Abs(openings[i].Bottom - z) < 0.6f)
                    return i;
            return -1;
        }

        /// <summary>
        /// Pure: every cover of a house and the opening each covers (-1: a retail door on a plain wall). Retail's own doors at
        /// full size where it has them; a computed cover for each other opening, and for one with a hand fix (which then
        /// replaces retail's door there).
        /// </summary>
        public static List<(Cover Cover, int Opening)> AllCovers(uint wcid, Func<int, Fix> fixFor)
        {
            var list = new List<(Cover, int)>();

            if (!IsHouse(wcid) || IsWall(wcid))
                return list;

            var house = Number(wcid);
            var openings = Openings(wcid);
            var byRetail = new HashSet<int>();

            for (var i = 0; i < openings.Count; i++)
                if (Exact.TryGetValue($"{house}:{i + 1}", out var e))
                {
                    var fx = fixFor(i);
                    var a = openings[i].Face * Math.PI / 180;
                    var outward = new Vector2((float)-Math.Sin(a), (float)Math.Cos(a));
                    var along = new Vector2(outward.Y, -outward.X);
                    var spot = new Vector2(e.X, e.Y) + outward * (fx?.Out ?? 0) + along * (fx?.Side ?? 0);
                    var size = e.Scale * (fx?.Size > 0 ? fx.Size : 1);
                    list.Add((new Cover(e.Wcid, new Vector3(spot, e.Z + (fx?.Up ?? 0)), e.Face, size), i));
                    byRetail.Add(i);
                }

            if (RetailDoors.TryGetValue(house, out var doors))
                foreach (var d in doors)
                {
                    var at = OpeningAt(openings, d.X, d.Y, d.Z);

                    if (byRetail.Contains(at))
                        continue;   // set by hand in ACE Creator

                    if (at >= 0 && fixFor(at) != null)
                        continue;   // a hand fix: the computed cover instead

                    if (at < 0)
                        continue;   // no opening there (such as house 11's side doors)

                    byRetail.Add(at);
                    var o = openings[at];
                    var face = Square(o, d.Face);
                    var lean = Leans.TryGetValue($"{house}:{at + 1}", out var l) ? l : 0f;
                    var a = o.Face * Math.PI / 180;
                    var outward = new Vector2((float)-Math.Sin(a), (float)Math.Cos(a));
                    var foot = new Vector2(o.X, o.Y) + outward * (float)(Math.Tan(lean * Math.PI / 180) * o.Height / 2);   // its bottom edge
                    var spot = InFrontOf(o with { X = foot.X, Y = foot.Y }, d.Wcid, new Vector2(d.X, d.Y), face, FitScale(o, d.Wcid));
                    var f = face * Math.PI / 180;
                    var depthOut = Vector2.Dot(new Vector2((float)-Math.Sin(f), (float)Math.Cos(f)), outward) > 0;

                    // Centred on its opening along the wall (house 10's upper door is 7 cm off in retail), and raised by the
                    // Aluvian door's drop.
                    var along = new Vector2(outward.Y, -outward.X);
                    spot -= along * Vector2.Dot(spot - new Vector2(o.X, o.Y), along);
                    var scale = FitScale(o, d.Wcid);
                    var z = d.Wcid == FraysDefs.TownDoor ? d.Z + AluvianDrop * scale : d.Z;
                    list.Add((new Cover(d.Wcid, new Vector3(spot, z), face, scale, depthOut ? lean : -lean), at));
                }

            for (var i = 0; i < openings.Count; i++)
                if (!byRetail.Contains(i))
                    foreach (var cover in CoversFor(openings[i], fixFor(i), StyleOf(house)))
                        list.Add((cover, i));

            // A hand turn about its own middle (House 9's upper door stood square to its wall instead of in it).
            // And a hand tilt toward or away from the wall (to close a gap at the top).
            return list.Select(c => c.Item2 >= 0 && fixFor(c.Item2) is { } t && (t.Turn != 0 || t.Tilt != 0)
                ? (c.Item1 with { Face = c.Item1.Face + t.Turn, Tilt = c.Item1.Tilt + t.Tilt }, c.Item2)
                : c).ToList();
        }

        /// <summary>Places the covers of a placed house (its position and facing set), as fixtures; returns them.</summary>
        public static List<WorldObject> PlaceCovers(Landblock landblock, WorldObject house, float facing, float scale)
        {
            var placed = new List<WorldObject>();
            WatchLiveFixes();

            if (house == null || !IsHouse(house.WeenieClassId))
                return placed;

            var wcid = house.WeenieClassId;
            var at = house.Location;
            var r = facing * Math.PI / 180;
            float c = (float)Math.Cos(r), s = (float)Math.Sin(r);

            Fix floorFix;
            lock (fixSync)
                floorFix = Fixes.TryGetValue($"{Number(wcid)}:floor", out var ff) ? ff : null;

            const int floor = -2;   // a floor tile, not a cover: solid, and not for the cover command
            foreach (var (worked, opening) in AllCovers(wcid, i => FixFor(wcid, i)).Concat(Floors(wcid, floorFix).Select(f => (f, floor))))
            {
                // The Cottage's and Great Hall's town doors go up as the flush door (only those two need it). There the town door arrives solid at login and the player's game
                // shoves it off its doorway; the flush door, the same leaf on the hung setup with no collision sphere, stays where
                // it is put. Placed below so its leaf lands where the town door's would.
                var cover = worked.Wcid == FraysDefs.TownDoor && FlushHouses.Contains(Number(wcid)) ? worked with { Wcid = Flush.Wcid } : worked;
                var lx = cover.Local.X * scale;
                var ly = cover.Local.Y * scale;
                var x = at.PositionX + lx * c - ly * s;
                var y = at.PositionY + lx * s + ly * c;
                var z = FraysManager.BaseZ(house) + cover.Local.Z * scale;
                var turn = Tilted(FraysVillage.Facing(facing + cover.Face), cover.Tilt);

                // The flush door's leaf hangs 4.9 m below its origin: set the origin so the leaf lands where the town door's
                // would, turned and tilted with it (PlaceFixture then adds the plain drop to z, so take that off here).
                if (cover.Wcid == Flush.Wcid)
                {
                    var size = cover.Scale * scale;
                    var o = Vector3.Transform((AluvianLeaf + new Vector3(0, 0, FraysManager.SolidDrop)) * size, turn);
                    x += o.X;
                    y += o.Y;
                    z += o.Z - FraysManager.SolidDrop * size;
                }

                // Past the landblock's edge: into the next landblock over. The watchtower stands 8.5 m off its origin, so on
                // towers near an edge all their covers fell past it and were left out.
                var block = landblock;
                int bx = (int)Math.Floor(x / 192f), by = (int)Math.Floor(y / 192f);

                if (bx != 0 || by != 0)
                {
                    var lb = landblock.Id.Landblock;
                    int nx = (lb >> 8) + bx, ny = (lb & 0xFF) + by;

                    if (nx < 0 || nx > 0xFE || ny < 0 || ny > 0xFE)
                        continue;

                    block = ACE.Server.Managers.LandblockManager.GetLandblock(new LandblockId((uint)((nx << 24) | (ny << 16) | 0xFFFF)), landblock.Instance, false);
                    x -= bx * 192f;
                    y -= by * 192f;
                }

                x = Math.Clamp(x, 0.1f, 191.9f);
                y = Math.Clamp(y, 0.1f, 191.9f);

                var cell = FraysRules.OutdoorCell(true, 0, new Vector3(x, y, 0));
                var wo = block == null ? null : FraysManager.PlaceFixture(block, cover.Wcid, cell, new Vector3(x, y, 0), turn,
                    exact: true, scale: cover.Scale * scale, z: z, ethereal: opening != floor, solid: cover.Wcid == Flush.Wcid,
                    uiHidden: true);   // fake doors and windows: not clickable or selectable

                if (wo == null)
                    continue;

                if (opening != floor)
                {
                    coverOf[wo.Guid.Full] = (house.Guid.Full, opening);

                    // Sent to every player ethereal, then solid: sent solid at login, the game shoved the Cottage's and Great
                    // Hall's doors off their doorways (on every relog).
                    if (FlushHouses.Contains(Number(wcid)))
                        wo.FraysCreateEthereal = true;

                    // Placed ethereal so collision can't shove it off the doorway, then made solid: the buildings are shells
                    // with no interiors, so nobody walks in (no door can be opened).
                    wo.Ethereal = false;
                    wo.EnqueueBroadcastPhysicsState();
                }
                placed.Add(wo);
            }

            covered[house.Guid.Full] = (house, facing, scale, placed.Select(p => p.Guid.Full).ToList());
            return placed;
        }

        /// <summary>Takes every copy of a house's covers down and puts them up again half a second later (after a hand fix).</summary>
        private static void Recover(uint wcid)
        {
            foreach (var entry in covered.Values.Where(e => e.House.WeenieClassId == wcid && !e.House.IsDestroyed).ToList())
            {
                var landblock = entry.House.CurrentLandblock;

                if (landblock == null)
                    continue;

                foreach (var cg in entry.Covers)
                {
                    coverOf.TryRemove(cg, out _);
                    if (landblock.GetObject(new ObjectGuid(cg)) is WorldObject wo && !wo.IsDestroyed)
                        wo.Destroy();
                }

                var chain = new ACE.Server.Entity.Actions.ActionChain();
                chain.AddDelaySeconds(0.5);
                chain.AddAction(landblock, () =>
                {
                    var again = PlaceCovers(landblock, entry.House, entry.Facing, entry.Scale);
                    Recovered?.Invoke(entry.House, again);
                });
                chain.EnqueueChain();
            }
        }

        /// <summary>Takes a house's covers down and forgets them (the house is going).</summary>
        public static void RemoveCovers(WorldObject house)
        {
            if (house == null || !covered.TryRemove(house.Guid.Full, out var entry))
                return;

            foreach (var cg in entry.Covers)
            {
                coverOf.TryRemove(cg, out _);
                if (house.CurrentLandblock?.GetObject(new ObjectGuid(cg)) is WorldObject wo && !wo.IsDestroyed)
                    wo.Destroy();
            }
        }

        public const string CoverUsage =
            "--- Town house covers ---\n"
            + "Click a door or window on a town house, or stand right next to it (that wins), then:\n"
            + "  /frays admin cover - which house and opening it is, and its fix\n"
            + "  /frays admin cover in|out [metres] - push it into the wall or out (0.05 if no number)\n"
            + "  /frays admin cover left|right [metres] - slide it along the wall (as seen from outside)\n"
            + "  /frays admin cover up|down [metres] - raise or lower it\n"
            + "  /frays admin cover bigger|smaller - 5% bigger or smaller\n"
            + "  /frays admin cover flip - turn it to face the other side of the wall\n"
            + "  /frays admin cover turn [degrees] - turn it about its middle (90 if no number; negative turns the other way)\n"
            + "  /frays admin cover tilt [degrees] - lean its top toward or away from the wall (1 if no number; negative the other way)\n"
            + "  /frays admin cover door|window|none - the house's door, a lattice window, or nothing there\n"
            + "  /frays admin cover aluvian|sho|gharu|stone - that style of door\n"
            + "  /frays admin cover flush - the town door that sits flush on rising ground (no gap under it; no handles)\n"
            + "  /frays admin cover reset - back to how it was worked out (retail's own door, where it has one)\n"
            + "  /frays admin cover add - a door in the doorway you stand in, facing into it (then in|out|left|right to fit it)\n"
            + "  /frays admin cover here - move the last added door on the nearest house to where you stand, facing into the doorway\n"
            + "Where retail has its own door, any fix puts a fitted door or window there instead.\n"
            + "Each fix applies to every copy of that house and stays after restarts.";

        /// <summary>The guid of the placed cover nearest the player, within range metres, or null.</summary>
        private static uint? NearestCover(Player player, float range)
        {
            var landblock = player.CurrentLandblock;
            if (landblock == null || player.Location == null)
                return null;

            uint? best = null;
            var bestDistance = range;

            foreach (var guid in coverOf.Keys)
            {
                var wo = landblock.GetObject(guid);
                if (wo?.Location == null)
                    continue;

                // From the drawn piece: a flush door's origin sits 4.9 m and more above its leaf (without this
                // the cover command never found the flush door it stood at, so tilt did nothing).
                var distance = player.Location.DistanceTo(wo.Location);
                var lift = wo.Location.PositionZ - player.Location.PositionZ;
                var drawn = FraysManager.BaseZ(wo) + (wo.WeenieClassId == Flush.Wcid ? AluvianLeaf.Z * (wo.ObjScale ?? 1) : 0) - player.Location.PositionZ;
                distance = MathF.Sqrt(Math.Max(0, distance * distance - lift * lift) + drawn * drawn);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = guid;
                }
            }

            return best;
        }

        /// <summary>
        /// /frays admin cover add: a new doorway on the nearest town house where the player stands, facing into it, with a door in
        /// it. Saved as that house's next opening (every copy of the house gets it).
        /// </summary>
        private static string AddDoor(Session session, Player player, bool move)
        {
            var me = player.Location;
            if (me == null)
                return "You are not in the world.";

            var meLb = me.LandblockId.Raw >> 16;
            (WorldObject House, float Facing, float Scale) best = default;
            var bestLocal = Vector2.Zero;
            var bestDistance = float.MaxValue;

            foreach (var e in covered.Values)
            {
                var at = e.House.Location;
                if (e.House.IsDestroyed || at == null || IsWall(e.House.WeenieClassId))
                    continue;

                var lb = at.LandblockId.Raw >> 16;
                var dx = me.PositionX - at.PositionX + (int)((meLb >> 8) - (lb >> 8)) * 192f;
                var dy = me.PositionY - at.PositionY + (int)((meLb & 0xFF) - (lb & 0xFF)) * 192f;
                var r = e.Facing * Math.PI / 180;
                float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
                var local = new Vector2(dx * c + dy * s, -dx * s + dy * c) / e.Scale;

                if (!TryFootprint(e.House.WeenieClassId, out var fp))
                    continue;

                // How far outside its footprint the player stands (0 inside it).
                var ox = Math.Max(0, Math.Max(fp.MinX - local.X, local.X - fp.MaxX));
                var oy = Math.Max(0, Math.Max(fp.MinY - local.Y, local.Y - fp.MaxY));
                var outside = MathF.Sqrt(ox * ox + oy * oy);

                if (outside < 3f && outside < bestDistance)
                {
                    bestDistance = outside;
                    best = (e.House, e.Facing, e.Scale);
                    bestLocal = local;
                }
            }

            if (best.House == null)
                return "Stand in the doorway of a town house (within 3 m of its walls), facing into it, then /frays admin cover add.";

            var wcid = best.House.WeenieClassId;

            if (IsWall(wcid))
                return "Walls take no doors.";

            // The opening faces out: the way the player looks, turned around, in the house's frame.
            var heading = FraysVillage.Degrees(new Quaternion(me.RotationX, me.RotationY, me.RotationZ, me.RotationW));
            var face = ((heading + 180 - best.Facing) % 360 + 360) % 360;
            if (face > 180)
                face -= 360;

            // A ground door sits on the model's floor; one higher up (a balcony) at the player's feet.
            var up = (me.PositionZ - FraysManager.BaseZ(best.House)) / best.Scale;
            var bottom = up < 1.5f ? 0f : up;

            // The player stands pressed against the doorway: the opening is about their body's half-width further in.
            var f = (heading - best.Facing) * Math.PI / 180;
            var spot = bestLocal + new Vector2((float)-Math.Sin(f), (float)Math.Cos(f)) * (0.4f / best.Scale);

            string text;
            int number;

            lock (fixSync)
            {
                var count = Openings(wcid).Count;
                var built = Data.TryGetValue(Model(wcid), out var d) ? d.Openings.Length : 0;

                if (move && count <= built)
                    return $"{Label(wcid)} has no added door yet. Use /frays admin cover add first.";

                number = move ? count - 1 : count;
                Fixes[Key(wcid, number)] = new Fix { Kind = "door", At = new[] { spot.X, spot.Y, bottom, 1.9f, 2.5f, face } };
                text = JsonSerializer.Serialize(Fixes);
            }

            Recover(wcid);
            var said = FraysAdmin.SetRaw(FraysSettings.HouseCovers, text, session);
            return said ?? $"{Label(wcid)}: {(move ? "moved its added door" : "added a door")} (opening {number + 1}) to where you stand. Fit it with /frays admin cover in|out|left|right [metres] (5 cm with no number), turn, or none to take it away.";
        }

        /// <summary>/frays admin cover ...</summary>
        public static string Command(Session session, Player player, string[] a)
        {
            var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "";

            if (sub == "help" || sub == "?")
                return CoverUsage;

            if (sub == "add" || sub == "here")
                return AddDoor(session, player, sub == "here");

            var target = player.RequestedAppraisalTarget;

            // The cover you stand next to (within 2.5 m) wins: the game does not ask again about a door it already knows, so
            // the last one asked about can be stale (two doors could both show the same opening). Otherwise the
            // one asked about, or the nearest within 5 m.
            var near = NearestCover(player, 2.5f);
            if (near != null)
                target = near;
            else if (target == null || !coverOf.ContainsKey(target.Value))
                target = NearestCover(player, 5f);

            if (target == null || !coverOf.TryGetValue(target.Value, out var which) || !covered.TryGetValue(which.House, out var entry))
                return "Click a door or window on a town house first, or stand next to one.\n" + CoverUsage;

            var wcid = entry.House.WeenieClassId;
            var key = Key(wcid, which.Opening);
            var openings = Openings(wcid);
            if (which.Opening < 0)
                return $"That is one of retail's own doors on {Label(wcid)}, on a plain wall; it stays as retail has it.";

            var o = openings[which.Opening];
            Fix fix;

            lock (fixSync)
            {
                fix = Fixes.TryGetValue(key, out var had)
                    ? new Fix { Out = had.Out, Side = had.Side, Up = had.Up, Size = had.Size, Flip = had.Flip, Kind = had.Kind, Turn = had.Turn, At = had.At, Tilt = had.Tilt }
                    : new Fix();
            }

            var metres = a.Length > 1 && float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0.05f;

            switch (sub)
            {
                case "":
                case "info":
                    return $"{Label(wcid)}, opening {which.Opening + 1} of {openings.Count}: {(IsDoorway(o) ? "doorway" : "window")} {o.Width:0.0} m wide, {o.Height:0.0} m tall, {o.Bottom:0.0} m up. "
                        + $"Fix: {(fix.Kind.Length > 0 ? fix.Kind + ", " : "")}out {fix.Out:0.00}, side {fix.Side:0.00}, up {fix.Up:0.00}, size {fix.Size:0.00}{(fix.Turn != 0 ? $", turned {fix.Turn:0.#}" : "")}{(fix.Flip ? ", flipped" : "")}.";
                case "in": fix.Out -= metres; break;
                case "out": fix.Out += metres; break;
                case "left": fix.Side -= metres; break;
                case "right": fix.Side += metres; break;
                case "up": fix.Up += metres; break;
                case "down": fix.Up -= metres; break;
                case "bigger": fix.Size = (fix.Size > 0 ? fix.Size : 1) * 1.05f; break;
                case "smaller": fix.Size = (fix.Size > 0 ? fix.Size : 1) / 1.05f; break;
                case "flip": fix.Flip = !fix.Flip; break;
                case "turn": fix.Turn += a.Length > 1 ? metres : 90f; break;
                case "tilt": fix.Tilt += a.Length > 1 ? metres : 1f; break;
                case "door":
                case "window":
                case "none":
                case "aluvian":
                case "sho":
                case "gharu":
                case "stone":
                case "flush": fix.Kind = sub; break;
                // An added opening keeps its place (none hides it); the others go back to as worked out.
                case "reset": fix = fix.At != null ? new Fix { At = fix.At, Kind = "door" } : null; break;
                default: return CoverUsage;
            }

            string text;

            lock (fixSync)
            {
                if (fix == null)
                    Fixes.Remove(key);
                else
                    Fixes[key] = fix;

                text = JsonSerializer.Serialize(Fixes);
            }

            Recover(wcid);
            var said = FraysAdmin.SetRaw(FraysSettings.HouseCovers, text, session);
            return said ?? $"{Label(wcid)}, opening {which.Opening + 1}: {(fix == null ? "back to as worked out" : sub)}. Every copy of the house shows it in a moment.";
        }
    }
}
