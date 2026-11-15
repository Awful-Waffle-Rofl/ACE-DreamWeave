using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Snarl Monument: a retail statue in the village square (dreamweave-content 2000201,
    /// model monument_setup) ringed by monument_stones small stones (2000202, model monument_stone_setup). Appraising the
    /// statue lists the latest Snarl Trophy finders; each stone is named for one of the latest finders ("Alda, Olthoi
    /// Snarl, 9 Nov") and its appraisal says who, which family and when. A new trophy takes the stone of the oldest one
    /// shown. Reads the Trophy Wall's saved state (frays_trophy_wall_state); the Trophy Wall sign stays as well.
    /// Placed as a village piece: /frays admin piece add 2000201. Its stones go round it by code.
    /// </summary>
    public static class FraysMonument
    {
        public const uint Wcid = 2000201;
        public const uint StoneWcid = 2000202;

        public static bool Is(uint wcid) => wcid == Wcid;

        public static bool IsStone(uint wcid) => wcid == StoneWcid;

        /// <summary>Pure: a stone's name, "Alda, Olthoi Snarl, 9 Nov".</summary>
        public static string StoneName(FraysTrophyWall.Entry e) =>
            e == null ? "Unclaimed Snarl Stone" : $"{e.Finder}, {Family(e)} Snarl, {e.Date.ToString("d MMM", CultureInfo.InvariantCulture)}";

        /// <summary>Pure: what appraising a stone says.</summary>
        public static string StoneText(FraysTrophyWall.Entry e) =>
            e == null ? "This stone waits for the next Snarl Trophy found in the Frays."
                : $"Finder: {e.Finder}\nFamily: {Family(e)} Snarl\nDate: {e.Date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}\n\n{e.Finder} found a Snarl Trophy in a {Family(e)} Snarl's cache.";

        private static string Family(FraysTrophyWall.Entry e) => string.IsNullOrWhiteSpace(e.Family) ? "Fray" : e.Family;

        /// <summary>
        /// Pure: the entry each of <paramref name="count"/> stones shows (null = unclaimed). Trophy number k (1 the first ever)
        /// takes stone (k - 1) mod count, so a new trophy always takes the stone of the oldest one shown and the others stay.
        /// <paramref name="entries"/> is newest first; <paramref name="total"/> is how many trophies were ever found.
        /// </summary>
        public static FraysTrophyWall.Entry[] Slots(IReadOnlyList<FraysTrophyWall.Entry> entries, int total, int count)
        {
            var slots = new FraysTrophyWall.Entry[Math.Max(0, count)];

            if (count <= 0)
                return slots;

            total = Math.Max(total, entries.Count);

            for (var j = 0; j < Math.Min(count, entries.Count); j++)
                slots[((total - 1 - j) % count + count) % count] = entries[j];

            return slots;
        }

        /// <summary>Pure: the statue's appraisal text for the latest <paramref name="count"/> finders.</summary>
        public static string MonumentText(IReadOnlyList<FraysTrophyWall.Entry> entries, int count)
        {
            var shown = entries.Take(Math.Max(0, count)).ToList();

            if (shown.Count == 0)
                return "The Snarl Monument honours those who find a Snarl Trophy. No one has found one yet: a Snarl's cache holds one now and then.";

            return "The Snarl Monument honours those who find a Snarl Trophy. The latest finders:\n"
                + string.Join("\n", shown.Select(e => $"{e.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}: {e.Finder}, {Family(e)} Snarl"));
        }

        /// <summary>Pure: the stones' spots round the statue (north first, then going west), each facing outward.</summary>
        public static List<(Vector2 Pos, float Facing)> Ring(Vector2 centre, float radius, int count) => Ring(centre, radius, count, 0);

        /// <summary>Pure: the same, the first spot at <paramref name="startDeg"/> (as /rotate: 0 north, 90 west).</summary>
        public static List<(Vector2 Pos, float Facing)> Ring(Vector2 centre, float radius, int count, float startDeg)
        {
            var list = new List<(Vector2, float)>();

            for (var i = 0; i < count; i++)
            {
                var deg = startDeg + 360f * i / count;   // as /rotate: 0 north, 90 west
                var rad = deg * MathF.PI / 180f;
                list.Add((new Vector2(centre.X - radius * MathF.Sin(rad), centre.Y + radius * MathF.Cos(rad)), deg));
            }

            return list;
        }

        private static List<FraysTrophyWall.Entry> Entries() => FraysTrophyWall.Parse(FraysSettings.Text(FraysSettings.TrophyWallState));

        /// <summary>Gives the statue or a stone its model from the settings, before it enters the world.</summary>
        public static void Prepare(WorldObject wo)
        {
            var key = Is(wo.WeenieClassId) ? FraysSettings.MonumentSetup : IsStone(wo.WeenieClassId) ? FraysSettings.MonumentStoneSetup : null;

            if (key != null && FraysRules.ParseSetup(FraysSettings.Text(key)) is uint setup && setup != 0)
                wo.SetupTableId = setup;
        }

        /// <summary>Right before a player appraises an object: the statue's text is written from the latest finders.</summary>
        public static void BeforeAppraise(WorldObject wo)
        {
            if (wo != null && Is(wo.WeenieClassId))
            {
                var text = MonumentText(Entries(), FraysSettings.Int(FraysSettings.MonumentStones));
                var leader = FraysSettings.Toggle(FraysSettings.MonumentChampion) ? FraysTrophyWall.Champion(FraysTrophyWall.Counts()) : null;
                wo.LongDesc = leader == null ? text : $"{text}\n\nChampion: {leader.Name}, {Trophies(leader.Count)}.";
            }
        }

        // ---- the Champion's stone (most trophies) ------------------------

        private static string Trophies(int n) => n == 1 ? "1 Snarl Trophy" : $"{n} Snarl Trophies";

        /// <summary>Pure: the Champion's stone's name, "Champion's Stone: Alda, 7 Snarl Trophies".</summary>
        public static string ChampionName(FraysTrophyWall.Tally t) =>
            t == null ? "Unclaimed Champion's Stone" : $"Champion's Stone: {t.Name}, {Trophies(t.Count)}";

        /// <summary>Pure: what appraising the Champion's stone says.</summary>
        public static string ChampionText(FraysTrophyWall.Tally t) =>
            t == null
                ? "The Champion's Stone honours whoever has found the most Snarl Trophies in the Frays. No one has found one yet."
                : $"The Champion's Stone\n\nChampion: {t.Name}\nSnarl Trophies found: {t.Count}\nReached on: {t.Reached.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}\n\n"
                  + $"{t.Name} has found more Snarl Trophies than anyone. Find more than {t.Count} to take the stone; on a tie it stays with whoever reached the count first.";

        /// <summary>Pure: the stones' spots with the Champion's stone first, in front of the statue (plus the angle), the others round the rest of the ring.</summary>
        public static (List<(Vector2 Pos, float Facing)> Stones, (Vector2 Pos, float Facing)? Champion) Layout(Vector2 centre, float radius, int count, bool champion, float statueFacing, float angle)
        {
            if (!champion)
                return (Ring(centre, radius, count), null);

            var ring = Ring(centre, radius, count + 1, statueFacing + angle);
            return (ring.Skip(1).ToList(), ring[0]);
        }

        /// <summary>Places the stones round a placed statue; returns them (the village keeps them with the statue's piece).</summary>
        public static List<WorldObject> PlaceStones(Landblock landblock, FraysPiece statue)
        {
            var stones = new List<WorldObject>();
            var count = FraysSettings.Int(FraysSettings.MonumentStones);
            var text = FraysSettings.Text(FraysSettings.TrophyWallState);
            var slots = Slots(FraysTrophyWall.Parse(text), FraysTrophyWall.ParseTotal(text), count);
            var (ring, champion) = Layout(new Vector2(statue.X, statue.Y), (float)FraysSettings.Number(FraysSettings.MonumentRingRadius) * statue.Size, count,
                FraysSettings.Toggle(FraysSettings.MonumentChampion), statue.Facing, (float)FraysSettings.Number(FraysSettings.MonumentChampionAngle));
            var indoor = statue.Cell >= 0x100;

            WorldObject Put(Vector2 pos, float facing, Action<WorldObject> named)
            {
                var cell = indoor ? statue.Cell : FraysRules.OutdoorCell(true, 0, new Vector3(pos, 0));
                return FraysManager.PlaceFixture(landblock, StoneWcid, cell, new Vector3(pos, indoor ? statue.Z : 0), FraysVillage.Facing(facing), exact: true,
                    prepare: wo =>
                    {
                        Prepare(wo);
                        named(wo);
                    });
            }

            for (var i = 0; i < ring.Count; i++)
            {
                var entry = slots[i];
                var stone = Put(ring[i].Pos, ring[i].Facing, wo => { wo.Name = StoneName(entry); wo.LongDesc = StoneText(entry); });

                if (stone != null)
                    stones.Add(stone);
            }

            if (champion is { } spot)
            {
                var leader = FraysTrophyWall.Champion(FraysTrophyWall.Counts());
                var size = (float)FraysSettings.Number(FraysSettings.MonumentChampionSize);
                var stone = Put(spot.Pos, spot.Facing, wo =>
                {
                    wo.Name = ChampionName(leader);
                    wo.LongDesc = ChampionText(leader);
                    wo.ObjScale = (wo.ObjScale ?? 1) * size;
                });

                if (stone != null)
                    stones.Add(stone);
            }

            return stones;
        }
    }
}
