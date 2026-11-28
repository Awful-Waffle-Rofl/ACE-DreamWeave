using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Server.BalanceTrials;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// @trial start with the resolver: an unknown id hints at adopt, and an archetype runs under a scenario of any level (owner
    /// ruling: nothing refuses on the archetype's level), seeded or curated. Driven through TrialStarter.TryStart over an inline data dir
    /// (never tools/) and a faked stamp scan. Seeds balance_trials_enabled and balance_trials_data_dir and restores both, the
    /// stamp scan and the shared gate's broken flag in finally.
    /// </summary>
    [TestClass]
    public class TrialCuratedStartTests
    {
        private const string EnabledKey = "balance_trials_enabled";
        private const string DataDirKey = "balance_trials_data_dir";

        private static readonly DateTime At = new DateTime(2026, 11, 24, 9, 30, 15, DateTimeKind.Utc);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        private static Player BarePlayer(uint guid)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            GuidField(player) = new ObjectGuid(guid);
            return player;
        }

        private const string Archetype = @"{
  ""id"": ""archmage-400"", ""slot"": 0, ""class"": ""Archmage"", ""targetLevel"": 400,
  ""anchor"": { ""rule"": ""topByClassSpend"", ""class"": ""Archmage"", ""minLevel"": 350, ""maxLevel"": 400, ""rank"": 1, ""minShare"": 0.5 },
  ""liftUp"": {
    ""reference"": [""Rogue""],
    ""cap"": { ""percentile"": ""p50"", ""ranks"": {}, ""tolerance"": 0.1 },
    ""gear"": [], ""augs"": {}, ""facets"": ""keep"", ""consumables"": []
  },
  ""buffProfile"": ""ownSpellbook""
}";

        private static string Scenario(string id, int level) => @"{
  ""id"": """ + id + @""",
  ""room"": ""arena-x"",
  ""gem"": { ""level"": " + level + @", ""tier"": 8, ""dungeon"": ""artifex_collegium"", ""family"": ""any"", ""mods"": ""rolled"", ""seed"": 1234 },
  ""group"": 1,
  ""waves"": [ { ""role"": ""Trash"", ""count"": 1, ""pull"": ""all"" } ],
  ""hazards"": ""off"",
  ""reps"": 1,
  ""timeoutSecs"": 600
}";

        private const string Room = @"{
  ""id"": ""arena-x"",
  ""landblock"": ""0x596A"",
  ""start"": { ""cell"": 1500119308, ""x"": 40, ""y"": -20, ""z"": 0.05, ""qw"": 1, ""qx"": 0, ""qy"": 0, ""qz"": 0 },
  ""bossPoint"": { ""cell"": 1500119298, ""x"": 12.1, ""y"": -20, ""z"": 0.05, ""qw"": 1, ""qx"": 0, ""qy"": 0, ""qz"": 0 },
  ""points"": [
    { ""cell"": 1500119296, ""x"": -2.2, ""y"": -22, ""z"": 0.05, ""qw"": 1, ""qx"": 0, ""qy"": 0, ""qz"": 0 },
    { ""cell"": 1500119300, ""x"": 18, ""y"": 2.2, ""z"": 0.05, ""qw"": 1, ""qx"": 0, ""qy"": 0, ""qz"": 0 }
  ],
  ""excludeRows"": { ""landblock"": ""0x596A"", ""minX"": -10, ""maxX"": 50, ""minY"": -50, ""maxY"": 10, ""minZ"": -5, ""maxZ"": 5 }
}";

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static void Run(Action<Player> body, IReadOnlyDictionary<int, string> stamps)
        {
            var dataDir = Path.Combine(Path.GetTempPath(), "trialcuratedstart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dataDir, TrialStarter.ArchetypesFolder));
            Directory.CreateDirectory(Path.Combine(dataDir, TrialStarter.ScenariosFolder));
            Directory.CreateDirectory(Path.Combine(dataDir, TrialStarter.RoomsFolder));
            File.WriteAllText(Path.Combine(dataDir, TrialStarter.ArchetypesFolder, "archmage-400.json"), Archetype);
            File.WriteAllText(Path.Combine(dataDir, TrialStarter.ScenariosFolder, "s400.json"), Scenario("s400", 400));
            File.WriteAllText(Path.Combine(dataDir, TrialStarter.ScenariosFolder, "s380.json"), Scenario("s380", 380));
            File.WriteAllText(Path.Combine(dataDir, TrialStarter.RoomsFolder, "arena-x.json"), Room);

            var enabled = PropertyManager.GetBool(EnabledKey).Item;
            var dir = PropertyManager.GetString(DataDirKey).Item;
            var scan = TrialArchetypeResolver.StampScan;

            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool(EnabledKey, true));
                Assert.IsTrue(PropertyManager.ModifyString(DataDirKey, dataDir));
                TrialArchetypeResolver.StampScan = () => stamps;

                body(BarePlayer(TrialRoster.RepSlots[0].Guid));

                Assert.AreEqual(0, TrialManager.SessionCount, "a refusal registers nothing");
            }
            finally
            {
                PropertyManager.ModifyBool(EnabledKey, enabled);
                PropertyManager.ModifyString(DataDirKey, dir);
                TrialArchetypeResolver.StampScan = scan;
                for (var s = 0; s < TrialRoster.MasterCount; s++)
                    TrialCopyGate.Shared.ClearBroken(TrialRoster.MasterSlot(s));
                TrialManager.ResetForTests();
                Directory.Delete(dataDir, recursive: true);
            }
        }

        [TestMethod]
        public void Start_runs_an_archetype_at_any_scenario_level()
        {
            // Owner ruling: cross-level runs are allowed. A seeded L400 archetype under an L380 scenario, and a curated L380 one
            // under an L400 scenario, both get past resolution to the next refusal (a broken master, set here), never a level one.
            var stamps = new Dictionary<int, string> { [5] = TrialCurated.Stamp("cur-a", ClassAbilityClass.Rogue, 380, At) };

            Run(player =>
            {
                TrialCopyGate.Shared.MarkBroken(TrialRoster.MasterSlot(0), "test hold");
                TrialCopyGate.Shared.MarkBroken(TrialRoster.MasterSlot(5), "test hold");

                foreach (var (scenario, archetype) in new[] { ("s380", "archmage-400"), ("s400", "archmage-400"), ("s400", "cur-a"), ("s380", "cur-a") })
                {
                    Assert.IsFalse(TrialStarter.TryStart(player, scenario, archetype, out var refusal), $"{scenario} {archetype}");
                    StringAssert.Contains(refusal, "broken", $"{scenario} {archetype}: {refusal}");
                    Assert.IsFalse(refusal.Contains("level"), $"{scenario} {archetype}: {refusal}");
                }
            }, stamps);
        }

        [TestMethod]
        public void Start_refuses_an_unknown_id_with_the_adopt_hint()
        {
            Run(player =>
            {
                Assert.IsFalse(TrialStarter.TryStart(player, "s400", "nobody", out var refusal));
                StringAssert.Contains(refusal, "unknown archetype 'nobody'");
                StringAssert.Contains(refusal, "adopt it first");
            }, new Dictionary<int, string>());
        }
    }
}
