using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Thread encounter visibility (owner-approved 2026-11-21, Discord "Visual indicators on shrines"): every encounter object is
    /// selectable and on the radar through ONE helper called from ThreadEncounterInstance.Track, and a shrine blessing plays a
    /// per-kind one-shot script. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadEncounterVisibilityTests
    {
        private static uint nextGuid = 0x7F100000;

        private static WorldObject NewObject(WeenieType type, Action<Dictionary<PropertyInt, int>, Dictionary<PropertyBool, bool>> configure = null)
        {
            var ints = new Dictionary<PropertyInt, int>();
            var bools = new Dictionary<PropertyBool, bool>();
            configure?.Invoke(ints, bools);

            var weenie = new Weenie
            {
                WeenieClassId = 9990001,
                ClassName = "visibilitytest",
                WeenieType = type,
                PropertiesInt = ints,
                PropertiesBool = bools,
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Visibility Test" },
            };

            if (type == WeenieType.Creature)
            {
                TestGameTables.EnsureInitialized();
                return new Creature(weenie, new ObjectGuid(nextGuid++));
            }

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        // ---- the blessing table ----------------------------------------------------------------------------------

        [TestMethod]
        public void The_blessing_script_table_covers_every_blessing_kind_with_a_golem_bank_colour_each()
        {
            foreach (var kind in Enum.GetValues(typeof(ThreadBoonKind)).Cast<ThreadBoonKind>())
            {
                Assert.IsTrue(ShrineEncounterType.BlessingScripts.TryGetValue(kind, out var script), $"{kind} has no blessing script");
                CollectionAssert.Contains(GolemEncounterType.AuraScripts.ToList(), script, $"{kind} must borrow a script from the Golem colour bank");
            }

            Assert.AreEqual(Enum.GetValues(typeof(ThreadBoonKind)).Length, ShrineEncounterType.BlessingScripts.Count, "no stray rows");
            Assert.AreEqual(ShrineEncounterType.BlessingScripts.Count, ShrineEncounterType.BlessingScripts.Values.Distinct().Count(), "a different colour per blessing");
        }

        [TestMethod]
        public void The_grant_plays_the_visual_for_each_recipient_and_the_silent_outcome_does_not()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ShrineEncounterType.cs");
            var grant = src.IndexOf("case ShrineUseOutcome.Granted:", StringComparison.Ordinal);
            var silent = src.IndexOf("case ShrineUseOutcome.Silent:", StringComparison.Ordinal);

            Assert.IsTrue(grant > 0 && silent > 0);
            var granted = src.Substring(grant, src.IndexOf("ScheduleAfterUse(instance, state, used, spawnGuardians: true);", grant, StringComparison.Ordinal) - grant);

            StringAssert.Contains(granted, "foreach (var recipient in recipients)");
            StringAssert.Contains(granted, "PlayBlessingVisual(recipient, kind);");
            Assert.IsFalse(src.Substring(silent, grant - silent).Contains("PlayBlessingVisual"), "no blessing was granted on a silent use");
        }

        // ---- the helper ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Apply_puts_a_plain_prop_on_the_radar_and_makes_it_selectable()
        {
            var wo = NewObject(WeenieType.Generic, (i, b) => { i[PropertyInt.ShowableOnRadar] = (int)RadarBehavior.ShowNever; b[PropertyBool.UiHidden] = true; b[PropertyBool.Visibility] = true; });

            Assert.IsTrue(ThreadEncounterObjectVisibility.Apply(wo));

            Assert.AreEqual(RadarBehavior.ShowAlways, wo.RadarBehavior);
            Assert.AreEqual(RadarColor.Yellow, wo.RadarColor);
            Assert.IsFalse(wo.UiHidden);
            Assert.IsFalse(wo.Visibility);
        }

        [TestMethod]
        public void Apply_keeps_a_blip_colour_the_weenie_already_carries()
        {
            var wo = NewObject(WeenieType.Generic, (i, b) => i[PropertyInt.RadarBlipColor] = (int)RadarColor.Portal);

            ThreadEncounterObjectVisibility.Apply(wo);

            Assert.AreEqual(RadarColor.Portal, wo.RadarColor);
            Assert.AreEqual(RadarBehavior.ShowAlways, wo.RadarBehavior);
        }

        [TestMethod]
        public void Apply_covers_a_peaceful_creature_but_leaves_a_monster_alone()
        {
            var npc = NewObject(WeenieType.Creature, (i, b) => b[PropertyBool.Attackable] = false);
            var monster = NewObject(WeenieType.Creature, (i, b) =>
            {
                b[PropertyBool.Attackable] = true;
                i[PropertyInt.ShowableOnRadar] = (int)RadarBehavior.ShowMovement;
            });

            Assert.IsTrue(ThreadEncounterObjectVisibility.Apply(npc));
            Assert.AreEqual(RadarBehavior.ShowAlways, npc.RadarBehavior);

            Assert.IsFalse(ThreadEncounterObjectVisibility.Apply(monster));
            Assert.AreEqual(RadarBehavior.ShowMovement, monster.RadarBehavior, "a monster keeps its own radar behaviour");
            Assert.IsNull(monster.RadarColor);

            Assert.IsFalse(ThreadEncounterObjectVisibility.Apply(null));
        }

        // ---- every spawn path goes through it -------------------------------------------------------------------

        [TestMethod]
        public void Track_applies_the_helper_before_it_records_the_object()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterTypes.cs");
            var track = PooledLootSourceText.MethodBody(StripLineComments(src), "public void Track(WorldObject wo)");

            var apply = track.IndexOf("ThreadEncounterObjectVisibility.Apply(wo);", StringComparison.Ordinal);
            var record = track.IndexOf("Run.TrackEncounterObject(this, wo);", StringComparison.Ordinal);

            Assert.IsTrue(apply >= 0, "Track must call the visibility helper");
            Assert.IsTrue(record > apply, "the helper runs before the object is recorded");
        }

        [TestMethod]
        public void Every_encounter_spawn_path_tracks_through_Track_and_only_the_portal_rings_are_decor()
        {
            // relative file -> instance.Track( call count, searched over ALL of Source/ACE.Server. A new call site (or a moved one) fails
            // here: confirm it reaches the helper, or is scenery.
            var expectedTrack = new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                { "ThreadDungeons/DefenseEncounterType.cs", 1 },          // pillar
                { "ThreadDungeons/GambleEncounterType.cs", 1 },           // chest
                { "ThreadDungeons/GolemEncounterType.cs", 3 },            // dormant golem, wisp, awakened golem (a monster: the helper skips it)
                { "ThreadDungeons/JumpEncounterType.cs", 2 },             // Trialmaster, goal (platforms are scenery and stay UiHidden, never tracked)
                { "ThreadDungeons/PeddlerEncounterType.cs", 1 },          // peddler
                { "ThreadDungeons/ShrineEncounterType.cs", 2 },           // shrine, guardian (a monster: the helper skips it)
                { "ThreadDungeons/ThreadEncounterPortalWaves.cs", 1 },    // wave enemy (a monster: the helper skips it)
            };

            var expectedDecor = new SortedDictionary<string, int> { { "ThreadDungeons/ThreadEncounterPortalWaves.cs", 1 } };

            // Objects that are not Tracked but are applied directly, before their EnterWorld.
            var expectedDirect = new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                { "ThreadDungeons/ThreadDungeonRewardSpawner.cs", 2 },    // CreateCacheChest (every reward and pooled cache chest), CreateExitAt (summoned exit portal)
                { "ThreadDungeons/ThreadEncounterTypes.cs", 1 },          // Track itself
            };

            var actualTrack = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var actualDecor = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var actualDirect = new SortedDictionary<string, int>(StringComparer.Ordinal);

            var serverDir = Path.GetDirectoryName(FindServerDir());
            serverDir = Path.GetFullPath(Path.Combine(serverDir, ".."));

            foreach (var file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = file.Substring(serverDir.Length + 1).Replace(Path.DirectorySeparatorChar, '/');

                if (rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal))
                    continue;

                var code = StripLineComments(File.ReadAllText(file));

                var track = rel == "ThreadDungeons/ThreadEncounterTypes.cs" ? 0 : Regex.Matches(code, @"\.Track\(").Count;
                var decor = rel == "ThreadDungeons/ThreadEncounterTypes.cs" ? 0 : Regex.Matches(code, @"\.TrackDecor\(").Count;
                var direct = rel == "ThreadDungeons/ThreadEncounterObjectVisibility.cs" ? 0 : Regex.Matches(code, @"ThreadEncounterObjectVisibility\.Apply\(").Count;

                if (track > 0) actualTrack[rel] = track;
                if (decor > 0) actualDecor[rel] = decor;
                if (direct > 0) actualDirect[rel] = direct;
            }

            CollectionAssert.AreEqual(expectedTrack.ToList(), actualTrack.ToList(), ".Track( sites changed: " + string.Join(", ", actualTrack.Select(kv => $"{kv.Key}={kv.Value}")));
            CollectionAssert.AreEqual(expectedDecor.ToList(), actualDecor.ToList(), "instance.TrackDecor( sites changed");
            CollectionAssert.AreEqual(expectedDirect.ToList(), actualDirect.ToList(), "ThreadEncounterObjectVisibility.Apply( direct sites changed: " + string.Join(", ", actualDirect.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        [TestMethod]
        public void The_cache_chest_and_summoned_exit_are_applied_before_they_are_returned_or_entered()
        {
            var src = StripLineComments(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRewardSpawner.cs"));

            var chest = PooledLootSourceText.MethodBody(src, "internal static Chest CreateCacheChest(uint ownerGuid, string ownerName, uint wcid, uint? runId, string purpose)");
            Assert.IsTrue(chest.IndexOf("ThreadEncounterObjectVisibility.Apply(chest);", StringComparison.Ordinal) > chest.IndexOf("is Chest chest", StringComparison.Ordinal));
            Assert.IsFalse(chest.Contains("EnterWorld"), "the factory never enters the chest; its callers do, after this");

            var exit = PooledLootSourceText.MethodBody(src, "private static Portal CreateExitAt(ThreadDungeonRun run, Player owner)");
            var apply = exit.IndexOf("ThreadEncounterObjectVisibility.Apply(portal);", StringComparison.Ordinal);
            Assert.IsTrue(apply >= 0);
            Assert.IsTrue(apply < exit.IndexOf("EnterWorld", StringComparison.Ordinal), "applied before EnterWorld");
        }

        [TestMethod]
        public void A_portal_without_a_blip_colour_gets_the_portal_colour_not_the_default()
        {
            var weenie = new Weenie { WeenieClassId = 9990002, ClassName = "visibilityportal", WeenieType = WeenieType.Portal, PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "P" } };
            var portal = new Portal(weenie, new ObjectGuid(nextGuid++));

            ThreadEncounterObjectVisibility.Apply(portal);

            Assert.AreEqual(RadarColor.Portal, portal.RadarColor);
        }

        // ---- F2/F3: the blessing visual against the real player table, and a null recipient ----------------------

        [TestMethod]
        public void The_blessing_script_values_are_pinned()
        {
            // A dat-free pin: change a colour on purpose, change this. (PlayScript ids are read from the enum, not recalled.)
            Assert.AreEqual(PlayScript.EnchantUpRed, ShrineEncounterType.BlessingScripts[ThreadBoonKind.Damage]);
            Assert.AreEqual(PlayScript.ShieldUpGrey, ShrineEncounterType.BlessingScripts[ThreadBoonKind.Mitigation]);
            Assert.AreEqual(PlayScript.EnchantUpYellow, ShrineEncounterType.BlessingScripts[ThreadBoonKind.AttackSpeed]);
            Assert.AreEqual(PlayScript.EnchantUpGreen, ShrineEncounterType.BlessingScripts[ThreadBoonKind.Xp]);
            Assert.AreEqual(PlayScript.EnchantUpPurple, ShrineEncounterType.BlessingScripts[ThreadBoonKind.Luminance]);
            Assert.AreEqual(PlayScript.EnchantUpOrange, ShrineEncounterType.BlessingScripts[ThreadBoonKind.Loot]);
        }

        /// <summary>The human player weenie (wcid 1) carries PropertyDataId 22 (PhysicsEffectTable) = 872415236 = 0x34000004 in the local retail world DB (queried 2026-11-21).</summary>
        private const uint PlayerPhysicsEffectTable = 0x34000004;

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void The_player_physics_effect_table_holds_every_blessing_script()
        {
            var datDir = new[] { Environment.GetEnvironmentVariable("ACE_DAT_PATH"), @"C:\ACE\Dats\", @"C:\Turbine\Asheron's Call\" }
                .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d, "client_portal.dat")));

            if (datDir == null)
                Assert.Inconclusive("client_portal.dat not found (set ACE_DAT_PATH to the directory holding the client .dat files).");

            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            if (ACE.DatLoader.DatManager.PortalDat == null)
                ACE.DatLoader.DatManager.Initialize(datDir, true, false);

            var table = ACE.DatLoader.DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.PhysicsScriptTable>(PlayerPhysicsEffectTable);
            Assert.IsNotNull(table, $"player table 0x{PlayerPhysicsEffectTable:X8} did not read");

            foreach (var kv in ShrineEncounterType.BlessingScripts)
                Assert.IsTrue(table.ScriptTable.ContainsKey((uint)kv.Value), $"{kv.Key} -> {kv.Value} is absent from the player PhysicsEffectTable 0x{PlayerPhysicsEffectTable:X8}");
        }

        [TestMethod]
        public void PlayBlessingVisual_with_no_recipient_does_not_throw_for_any_kind()
        {
            var method = typeof(ShrineEncounterType).GetMethod("PlayBlessingVisual", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "PlayBlessingVisual was renamed; re-aim this test");

            foreach (var kind in Enum.GetValues(typeof(ThreadBoonKind)).Cast<ThreadBoonKind>())
                method.Invoke(null, new object[] { null, kind });
        }

        private static string FindServerDir()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "ThreadDungeons", "ThreadEncounterTypes.cs");

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail("Could not find ThreadEncounterTypes.cs by walking up from " + AppContext.BaseDirectory);
            return null;
        }

        private static string StripLineComments(string code) => Regex.Replace(code.Replace("\r\n", "\n"), @"//[^\n]*", string.Empty);
    }
}
