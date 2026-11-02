using System.Collections.Generic;
using System.Linq;

using ACE.Server.SpeedCourses;

namespace ACE.Server.Tests
{
    /// <summary>A small valid speed-courses.json and a lookup for it. Test fixture only; the real file is a later step.</summary>
    internal static class SpeedCourseFixtures
    {
        public const string SampleJson = @"{
  ""version"": 1,
  ""courses"": [
    {
      ""seasonId"": 3,
      ""dungeonId"": ""speed_black_book"",
      ""family"": ""undead"",
      ""seed"": 3003,
      ""tier"": 2,
      ""levelFloor"": 60,
      ""sealKey"": ""bb_seal"",
      ""sealRequired"": 8,
      ""tokens"": { ""p1"": 3, ""p3"": 3, ""north_rite"": 1, ""east_rite"": 1, ""south_rite"": 1 },
      ""labels"": { ""p1"": ""First ward gate"", ""p3"": ""Last ward gate"", ""north_rite"": ""North rite"", ""east_rite"": ""East rite"", ""south_rite"": ""South rite"" },
      ""progressGroups"": [
        { ""name"": ""ward gates"", ""tokens"": [ ""p1"", ""p3"" ], ""of"": 2 },
        { ""name"": ""rites"", ""tokens"": [ ""north_rite"", ""east_rite"", ""south_rite"" ], ""of"": 2 }
      ],
      ""messages"": {
        ""intro"": ""INTRO"",
        ""progress"": ""{label} complete. Seal: {groups}."",
        ""granted"": ""{label} granted: the cells could not offer it this run."",
        ""sealOpen"": ""SEAL OPEN""
      },
      ""puzzles"": [
        { ""slot"": ""P1"", ""siteId"": ""bb_p1"", ""token"": ""p1"", ""rounds"": 3, ""lockoutSeconds"": 5 },
        { ""slot"": ""P2"", ""siteId"": ""bb_p2"", ""token"": null, ""rounds"": 2, ""lockoutSeconds"": 5 },
        { ""slot"": ""P3"", ""siteId"": ""bb_p3"", ""token"": ""p3"", ""rounds"": 3, ""lockoutSeconds"": 5 }
      ],
      ""named"": [
        { ""slot"": ""K1"", ""position"": { ""cell"": 144179595, ""x"": 10, ""y"": 20, ""z"": -6, ""qw"": 1, ""qx"": 0, ""qy"": 0, ""qz"": 0 }, ""token"": ""north_rite"", ""displayName"": ""Archivist"", ""holdUntilSolved"": ""P2"" },
        { ""slot"": ""K2"", ""position"": { ""cell"": 144179596, ""x"": 30, ""y"": 20, ""z"": -6, ""qw"": 1, ""qx"": 0, ""qy"": 0, ""qz"": 0 }, ""token"": null, ""displayName"": ""Warden"" },
        { ""slot"": ""BOSS"", ""position"": { ""cell"": 144179597, ""x"": 50, ""y"": 20, ""z"": -6, ""qw"": 0.7071, ""qx"": 0, ""qy"": 0, ""qz"": 0.7071 }, ""token"": null, ""displayName"": ""The Reader"" }
      ],
      ""hazards"": [
        {
          ""id"": ""cloister_zones"", ""kind"": ""zones"", ""pattern"": ""corners"",
          ""region"": { ""shape"": ""rect"", ""minX"": 0, ""maxX"": 20, ""minY"": 0, ""maxY"": 20, ""groundZ"": -6, ""ceilingZ"": -1, ""bandBelow"": 1, ""bandAbove"": 4 },
          ""trigger"": { ""type"": ""clock_rebase"" },
          ""args"": { ""pct"": ""0.2"" }, ""pctAuthored"": 0.6, ""pctMin"": 0.05, ""pctMax"": 0.4
        },
        {
          ""id"": ""east_balls"", ""kind"": ""balls"", ""pattern"": ""ring-alt"",
          ""region"": { ""shape"": ""circle"", ""centerX"": 40, ""centerY"": 10, ""radius"": 8, ""groundZ"": -6 },
          ""triggerRegion"": { ""shape"": ""rect"", ""minX"": 30, ""maxX"": 70, ""minY"": 0, ""maxY"": 30, ""groundZ"": -6 },
          ""trigger"": { ""type"": ""while_in_region"" },
          ""args"": {}, ""pctAuthored"": 0.6, ""pctMin"": 0.05, ""pctMax"": 0.4
        },
        {
          ""id"": ""south_memory"", ""kind"": ""memory"",
          ""region"": { ""shape"": ""rect"", ""minX"": 0, ""maxX"": 10, ""minY"": -30, ""maxY"": -10, ""groundZ"": -6 },
          ""trigger"": { ""type"": ""on_named_dead_and_in_region"", ""slot"": ""K2"" },
          ""args"": {}, ""token"": ""south_rite"", ""retrySeconds"": 5
        },
        {
          ""id"": ""boss_fans"", ""kind"": ""wisps"", ""pattern"": ""row"",
          ""region"": { ""shape"": ""rect"", ""minX"": 40, ""maxX"": 60, ""minY"": 0, ""maxY"": 20, ""groundZ"": -6 },
          ""trigger"": { ""type"": ""boss_alive_interval"", ""seconds"": 20 }, ""args"": {}
        },
        {
          ""id"": ""hub_zones"", ""kind"": ""zones"",
          ""region"": { ""shape"": ""rect"", ""minX"": 40, ""maxX"": 60, ""minY"": 0, ""maxY"": 20, ""groundZ"": -6 },
          ""trigger"": { ""type"": ""boss_health_below"", ""fraction"": 0.3333 }, ""args"": {}
        }
      ]
    }
  ]
}";

        public static SpeedCourseDef Course()
            => SpeedCourseStore.Parse(SampleJson, null).GetCourse(3);

        public sealed class FakeLookup : ISpeedCourseLookup
        {
            public HashSet<string> Dungeons { get; } = new HashSet<string> { "speed_black_book" };
            public HashSet<string> Sites { get; } = new HashSet<string> { "bb_p1", "bb_p2", "bb_p3" };
            public HashSet<string> Families { get; } = new HashSet<string> { "undead" };

            public bool DungeonExists(string dungeonId) => Dungeons.Contains(dungeonId);
            public bool PuzzleSiteExists(string dungeonId, string siteId) => Dungeons.Contains(dungeonId) && Sites.Contains(siteId);
            public bool FamilyExists(string family) => Families.Contains(family);
        }
    }
}