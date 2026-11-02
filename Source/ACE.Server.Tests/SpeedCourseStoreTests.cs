using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.SpeedCourses;

namespace ACE.Server.Tests
{
    [TestClass]
    public class SpeedCourseStoreTests
    {
        private static SpeedCourseStore ParseWith(System.Action<System.Text.Json.Nodes.JsonObject> edit, ISpeedCourseLookup lookup = null)
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(SpeedCourseFixtures.SampleJson).AsObject();
            edit?.Invoke(root["courses"].AsArray()[0].AsObject());
            return SpeedCourseStore.Parse(root.ToJsonString(), lookup);
        }

        private static string Joined(SpeedCourseStore s) => string.Join(" | ", s.Diagnostics);

        [TestMethod]
        public void Sample_IsValid_AndLoads()
        {
            var store = SpeedCourseStore.Parse(SpeedCourseFixtures.SampleJson, new SpeedCourseFixtures.FakeLookup());

            Assert.AreEqual(0, store.Diagnostics.Count, Joined(store));
            var c = store.GetCourse(3);
            Assert.IsNotNull(c);
            Assert.AreEqual("speed_black_book", c.DungeonId);
            Assert.AreEqual(5, c.Hazards.Count);
            Assert.AreEqual(8.0, c.SealRequired);
            Assert.AreEqual(144179597u, c.Named.Single(n => n.Slot == "BOSS").Position.Cell);
            Assert.IsNull(store.GetCourse(4));
        }

        [TestMethod]
        public void FixedLevel_OutsideZeroToMaxRunLevel_IsRejected()
        {
            var neg = ParseWith(c => c["fixedLevel"] = -1);
            Assert.IsNull(neg.GetCourse(3), "a negative fixedLevel must reject the course");
            Assert.IsTrue(Joined(neg).Contains("fixedLevel"), Joined(neg));

            var high = ParseWith(c => c["fixedLevel"] = ACE.Server.ThreadDungeons.DungeonGemSpec.MaxRunLevel + 1);
            Assert.IsNull(high.GetCourse(3), "a fixedLevel above the ceiling must reject the course");

            var ok = ParseWith(c => c["fixedLevel"] = 150);
            Assert.AreEqual(150, ok.GetCourse(3).FixedLevel, Joined(ok));
        }

        [TestMethod]
        public void EmptyOrMalformed_NeverThrows()
        {
            Assert.AreEqual(0, SpeedCourseStore.Parse("", null).Diagnostics.Count);
            Assert.AreEqual(0, SpeedCourseStore.Parse("   ", null).Courses.Count);

            var bad = SpeedCourseStore.Parse("{ not json", null);
            Assert.AreEqual(0, bad.Courses.Count);
            Assert.AreEqual(1, bad.Diagnostics.Count);

            var ver = SpeedCourseStore.Parse("{\"version\":9,\"courses\":[]}", null);
            Assert.AreEqual(1, ver.Diagnostics.Count);
        }

        [TestMethod]
        public void MissingFolder_IsEmptyStore()
        {
            var store = SpeedCourseStore.Load(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no_such_speed_course_dir_" + System.Guid.NewGuid().ToString("N")), null);
            Assert.AreEqual(0, store.Courses.Count);
        }

        [TestMethod]
        public void WronglyTypedCourse_CostsOnlyThatCourse()
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(SpeedCourseFixtures.SampleJson).AsObject();
            root["courses"].AsArray().Insert(0, System.Text.Json.Nodes.JsonNode.Parse("{\"seasonId\":\"x\"}"));
            var json = root.ToJsonString();
            var store = SpeedCourseStore.Parse(json, null);

            Assert.IsNotNull(store.GetCourse(3), Joined(store));
            Assert.AreEqual(1, store.Diagnostics.Count);
        }

        [TestMethod]
        public void DuplicateSeasonId_KeepsFirst()
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(SpeedCourseFixtures.SampleJson).AsObject();
            var arr = root["courses"].AsArray();
            arr.Add(System.Text.Json.Nodes.JsonNode.Parse(arr[0].ToJsonString()));
            var store = SpeedCourseStore.Parse(root.ToJsonString(), null);

            Assert.AreEqual(1, store.Courses.Count);
            Assert.IsTrue(Joined(store).Contains("duplicate seasonId 3"), Joined(store));
        }

        [TestMethod]
        public void MissingRequiredFields_AreReported()
        {
            var store = ParseWith(c => { c["dungeonId"] = null; c["family"] = ""; c["sealKey"] = null; });
            var d = Joined(store);

            Assert.AreEqual(0, store.Courses.Count);
            Assert.IsTrue(d.Contains("dungeonId is required"), d);
            Assert.IsTrue(d.Contains("family is required"), d);
            Assert.IsTrue(d.Contains("sealKey is required"), d);
        }

        [TestMethod]
        public void UnknownHazardKindPatternTrigger_AreReported()
        {
            var store = ParseWith(c =>
            {
                var h = c["hazards"].AsArray();
                h[0]["kind"] = "lasers";
                h[1]["pattern"] = "spiral";
                h[2]["trigger"]["type"] = "whenever";
            });
            var d = Joined(store);

            Assert.IsTrue(d.Contains("unknown hazard kind 'lasers'"), d);
            Assert.IsTrue(d.Contains("unknown pattern 'spiral'"), d);
            Assert.IsTrue(d.Contains("unknown trigger type 'whenever'"), d);
        }

        [TestMethod]
        public void TriggerArguments_AreChecked()
        {
            var store = ParseWith(c =>
            {
                var h = c["hazards"].AsArray();
                h[2]["trigger"]["slot"] = "K9";
                h[3]["trigger"]["seconds"] = 0;
                h[4]["trigger"]["fraction"] = 1.5;
            });
            var d = Joined(store);

            Assert.IsTrue(d.Contains("needs slot K1, K2 or BOSS"), d);
            Assert.IsTrue(d.Contains("needs seconds > 0"), d);
            Assert.IsTrue(d.Contains("fraction in (0, 1]"), d);
        }

        [TestMethod]
        public void TokensNotInTable_AreReported()
        {
            var store = ParseWith(c =>
            {
                c["puzzles"].AsArray()[0]["token"] = "p9";
                c["named"].AsArray()[0]["token"] = "west_rite";
                c["hazards"].AsArray()[2]["token"] = "nope";
            });
            var d = Joined(store);

            Assert.IsTrue(d.Contains("'p9' is not in the token table"), d);
            Assert.IsTrue(d.Contains("'west_rite' is not in the token table"), d);
            Assert.IsTrue(d.Contains("'nope' is not in the token table"), d);
        }

        [TestMethod]
        public void PuzzleSlots_MustBeExactlyP1P2P3()
        {
            var missing = ParseWith(c => c["puzzles"].AsArray().RemoveAt(2));
            Assert.IsTrue(Joined(missing).Contains("exactly the slots P1, P2, P3"), Joined(missing));

            var dup = ParseWith(c => c["puzzles"].AsArray()[2]["slot"] = "P1");
            Assert.IsTrue(Joined(dup).Contains("exactly the slots P1, P2, P3"), Joined(dup));
        }

        [TestMethod]
        public void P2_CarryingAToken_IsReported()
        {
            var store = ParseWith(c => c["puzzles"].AsArray()[1]["token"] = "north_rite");
            Assert.IsTrue(Joined(store).Contains("P2 must not carry a token"), Joined(store));
        }

        [TestMethod]
        public void NamedSlots_MustBeExactlyK1K2Boss()
        {
            var store = ParseWith(c => c["named"].AsArray()[1]["slot"] = "K3");
            Assert.IsTrue(Joined(store).Contains("exactly the slots K1, K2, BOSS"), Joined(store));
        }

        [TestMethod]
        public void SealRequired_MustBeSatisfiable()
        {
            var store = ParseWith(c => c["sealRequired"] = 10);
            Assert.IsTrue(Joined(store).Contains("not satisfiable"), Joined(store));

            var zero = ParseWith(c => c["sealRequired"] = 0);
            Assert.IsTrue(Joined(zero).Contains("sealRequired 0 must be positive"), Joined(zero));
        }

        [TestMethod]
        public void ThreadsCrossChecks_UseTheInjectedLookup()
        {
            var lookup = new SpeedCourseFixtures.FakeLookup();
            lookup.Dungeons.Clear();
            var noDungeon = SpeedCourseStore.Parse(SpeedCourseFixtures.SampleJson, lookup);
            Assert.IsTrue(Joined(noDungeon).Contains("not a Threads index row"), Joined(noDungeon));

            lookup = new SpeedCourseFixtures.FakeLookup();
            lookup.Sites.Remove("bb_p2");
            lookup.Families.Clear();
            var d = Joined(SpeedCourseStore.Parse(SpeedCourseFixtures.SampleJson, lookup));
            Assert.IsTrue(d.Contains("siteId 'bb_p2' is not a puzzle site"), d);
            Assert.IsTrue(d.Contains("family 'undead' is not a species table"), d);
        }

        [TestMethod]
        public void HazardArgs_PassThroughAsTheSettingsLayer()
        {
            var c = SpeedCourseFixtures.Course();
            var args = c.Hazards[0].ToHazardArgs();

            Assert.AreEqual("0.2", args.GetString("pct"));
            Assert.AreEqual(0, c.Hazards[1].ToHazardArgs().Count);
        }

        [TestMethod]
        public void Region_BuildsAndContains()
        {
            var c = SpeedCourseFixtures.Course();
            var rect = c.Hazards[0].Region;
            var region = rect.Build("cloister", 0x0089, 7u);

            Assert.AreEqual((ushort)0x0089, region.Landblock);
            Assert.AreEqual(-1f, region.CeilingZ);
            Assert.IsTrue(rect.Contains(10, 10, -5));
            Assert.IsFalse(rect.Contains(25, 10, -5));
            Assert.IsFalse(rect.Contains(10, 10, 5), "above the band");

            var circle = c.Hazards[1].Region;
            Assert.IsTrue(circle.Contains(44, 10, -6));
            Assert.IsFalse(circle.Contains(50, 10, -6));
            Assert.IsNotNull(circle.Build("balls", 0x0089, 7u));
        }

        [TestMethod]
        public void RunnerMessages_AndTheK1Hold_AreParsed()
        {
            var c = SpeedCourseFixtures.Course();

            Assert.AreEqual("P2", c.Named.Single(n => n.Slot == "K1").HoldUntilSolved);
            Assert.IsNull(c.Named.Single(n => n.Slot == "K2").HoldUntilSolved);
            Assert.AreEqual("First ward gate", c.LabelOf("p1"));
            Assert.AreEqual("nonesuch", c.LabelOf("nonesuch"), "an unlabelled token is told by its id");
            Assert.AreEqual(2, c.ProgressGroups.Count);
            Assert.AreEqual("{label} complete. Seal: {groups}.", c.Messages.Progress);

            Assert.AreEqual("ward gates 1 of 2, rites 2 of 2",
                SpeedCourseRules.ProgressText(c, new[] { "p1", "north_rite", "east_rite", "south_rite" }), "a group's count is capped at its 'of'");
            Assert.AreEqual("First ward gate complete. Seal: x.", SpeedCourseRules.FormatMessage(c.Messages.Progress, "First ward gate", "x"));
            Assert.IsNull(SpeedCourseRules.FormatMessage(null, "a", "b"));
        }

        [TestMethod]
        public void BadHold_LabelOrGroup_IsADiagnostic()
        {
            Assert.IsTrue(Joined(ParseWith(c => c["named"].AsArray()[0]["holdUntilSolved"] = "P9")).Contains("holdUntilSolved 'P9'"));
            Assert.IsTrue(Joined(ParseWith(c => c["named"].AsArray()[2]["holdUntilSolved"] = "P2")).Contains("K1/K2 only"));
            Assert.IsTrue(Joined(ParseWith(c => c["labels"]["nope"] = "Nope")).Contains("labels: 'nope'"));
            Assert.IsTrue(Joined(ParseWith(c => c["progressGroups"].AsArray()[0]["of"] = 3)).Contains("of 3 must be 1..2"));
            Assert.IsTrue(Joined(ParseWith(c => c["progressGroups"].AsArray()[1]["tokens"].AsArray().Add("ghost"))).Contains("token 'ghost'"));

            var noMessages = ParseWith(c => { c.Remove("messages"); c.Remove("labels"); c.Remove("progressGroups"); });
            Assert.AreEqual(0, noMessages.Diagnostics.Count, Joined(noMessages));
            Assert.IsNull(noMessages.GetCourse(3).Messages, "messages are optional");
        }

        private static System.Text.Json.Nodes.JsonNode GuardianNode(string id, string doorKey, double scale = 1)
            => GuardianKeysNode(id, scale, doorKey);

        /// <summary>A guardian naming <paramref name="doorKeys"/> (each rendered verbatim, so "" is a blank entry).</summary>
        private static System.Text.Json.Nodes.JsonNode GuardianKeysNode(string id, double scale, params string[] doorKeys)
            => System.Text.Json.Nodes.JsonNode.Parse(
                $@"{{ ""id"": ""{id}"", ""doorKeys"": [ {string.Join(", ", doorKeys.Select(k => "\"" + k + "\""))} ], ""position"": {{ ""cell"": 8979044, ""x"": 168, ""y"": -152, ""z"": -6, ""qw"": 1, ""qx"": 0, ""qy"": 0, ""qz"": 0 }}, ""displayName"": ""Cell Keeper"", ""scale"": {scale.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}");

        private static SpeedCourseStore WithGuardians(params System.Text.Json.Nodes.JsonNode[] guardians)
            => ParseWith(c =>
            {
                var arr = new System.Text.Json.Nodes.JsonArray();
                foreach (var g in guardians)
                    arr.Add(g);
                c["guardians"] = arr;
            }, new SpeedCourseFixtures.FakeLookup());

        [TestMethod]
        public void Guardians_AreOptional_AndParse()
        {
            Assert.AreEqual(0, SpeedCourseFixtures.Course().Guardians.Count, "absent: an empty list");

            var store = WithGuardians(GuardianKeysNode("G_NW", 1.5, "bb_nw_out", "bb_nw_in"), GuardianNode("G_NW2", "bb_nw_out"), GuardianNode("G_SW", "bb_sw_out"));
            Assert.AreEqual(0, store.Diagnostics.Count, Joined(store));

            var g = store.GetCourse(3).Guardians;
            Assert.AreEqual(3, g.Count);
            Assert.AreEqual("G_NW", g[0].Id);
            CollectionAssert.AreEqual(new[] { "bb_nw_out", "bb_nw_in" }, g[0].DoorKeys, "a keeper names both doors of its room, in order");
            Assert.AreEqual("Cell Keeper", g[0].DisplayName);
            Assert.AreEqual(1.5f, g[0].Scale);
            Assert.AreEqual(8979044u, g[0].Position.Cell);
            Assert.AreEqual(1f, g[1].Scale);
            Assert.AreEqual(g[0].DoorKeys[0], g[1].DoorKeys[0], "two guardians may share one door");
        }

        /// <summary>
        /// The 2026-10-25 migration from a single "doorKey" to "doorKeys": a guardian carrying only the old field has no
        /// doors, which is a diagnostic that drops the course, never a guardian that silently opens nothing.
        /// </summary>
        [TestMethod]
        public void Guardians_TheRetiredSingleDoorKey_IsRejectedLoudly()
        {
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(
                @"{ ""id"": ""G1"", ""doorKey"": ""bb_nw_out"", ""position"": { ""cell"": 8979044, ""x"": 168, ""y"": -152, ""z"": -6 }, ""displayName"": ""Cell Keeper"" }");
            var store = WithGuardians(legacy);

            Assert.IsTrue(Joined(store).Contains("guardian 'G1': doorKeys is required"), Joined(store));
            Assert.IsNull(store.GetCourse(3));
        }

        [TestMethod]
        public void OptionalGateKeys_AreOptional_AndNeverTheSeal()
        {
            Assert.AreEqual(0, SpeedCourseFixtures.Course().OptionalGateKeys.Count, "absent: an empty list");

            var ok = ParseWith(c => c["optionalGateKeys"] = new System.Text.Json.Nodes.JsonArray("bb_nw_in", "bb_nw_out"));
            Assert.AreEqual(0, ok.Diagnostics.Count, Joined(ok));
            CollectionAssert.AreEqual(new[] { "bb_nw_in", "bb_nw_out" }, ok.GetCourse(3).OptionalGateKeys);

            var seal = ParseWith(c => c["optionalGateKeys"] = new System.Text.Json.Nodes.JsonArray("bb_nw_in", "bb_seal"));
            Assert.IsTrue(Joined(seal).Contains("'bb_seal' is the sealKey"), Joined(seal));
            Assert.IsNull(seal.GetCourse(3));

            var blank = ParseWith(c => c["optionalGateKeys"] = new System.Text.Json.Nodes.JsonArray(" "));
            Assert.IsTrue(Joined(blank).Contains("optionalGateKeys: a key is blank"), Joined(blank));
        }

        [TestMethod]
        public void GuardianDoorMessage_IsParsed()
        {
            var store = ParseWith(c => c["messages"]["guardianDoor"] = "The keeper falls.");
            Assert.AreEqual("The keeper falls.", store.GetCourse(3).Messages.GuardianDoor, Joined(store));
            Assert.IsNull(SpeedCourseFixtures.Course().Messages.GuardianDoor, "optional");
        }

        [TestMethod]
        public void Guardians_AreValidated()
        {
            Assert.IsTrue(Joined(WithGuardians(GuardianNode("G1", "a"), GuardianNode("G1", "b"))).Contains("guardian 'G1': duplicate id"));
            Assert.IsTrue(Joined(WithGuardians(GuardianNode("", "a"))).Contains("a guardian has no id"));
            Assert.IsTrue(Joined(WithGuardians(GuardianNode("G1", ""))).Contains("guardian 'G1': a doorKeys entry is blank"));
            Assert.IsTrue(Joined(WithGuardians(GuardianKeysNode("G1", 1))).Contains("guardian 'G1': doorKeys is required"));
            Assert.IsTrue(Joined(WithGuardians(GuardianNode("G1", "bb_seal"))).Contains("must not be the sealKey"));
            Assert.IsTrue(Joined(WithGuardians(GuardianKeysNode("G1", 1, "a", "bb_seal"))).Contains("doorKey 'bb_seal' must not be the sealKey"), "the Seal is refused in any position");
            Assert.IsTrue(Joined(WithGuardians(GuardianKeysNode("G1", 1, "a", "b", "a"))).Contains("guardian 'G1': doorKey 'a' is listed twice"));
            Assert.AreEqual(0, WithGuardians(GuardianKeysNode("G1", 1, "a", "b")).Diagnostics.Count, "two distinct keys are fine");
            Assert.IsTrue(Joined(WithGuardians(GuardianNode("G1", "a", 0))).Contains("guardian 'G1': scale 0 must be in (0, 10]"));
            Assert.IsTrue(Joined(WithGuardians(GuardianNode("G1", "a", 10.5))).Contains("scale 10.5 must be in (0, 10]"));
            Assert.AreEqual(0, WithGuardians(GuardianNode("G1", "a", 10)).Diagnostics.Count, "10 is the inclusive ceiling");

            var noPosition = GuardianNode("G1", "a");
            noPosition.AsObject().Remove("position");
            var store = WithGuardians(noPosition);
            Assert.IsTrue(Joined(store).Contains("guardian 'G1': position is required"), Joined(store));
            Assert.IsNull(store.GetCourse(3), "a bad guardian drops the course");

            Assert.IsTrue(Joined(ParseWith(c => c["guardians"] = new System.Text.Json.Nodes.JsonArray { null })).Contains("a guardian entry is null"));
        }

        [TestMethod]
        public void ObjectiveLock_HasToken_ReadsLiveTokensOnly()
        {
            var now = new System.DateTime(2026, 10, 22, 0, 0, 0, System.DateTimeKind.Utc);
            var lockState = new ACE.Server.Entity.ObjectiveLock(8);

            Assert.IsFalse(lockState.HasToken("east_rite", now));
            lockState.Contribute("east_rite", 1, now);
            lockState.Contribute("brief", 1, now, now.AddSeconds(5));

            Assert.IsTrue(lockState.HasToken("east_rite", now));
            Assert.IsTrue(lockState.HasToken("brief", now.AddSeconds(4)));
            Assert.IsFalse(lockState.HasToken("brief", now.AddSeconds(5)), "an expired token is not live");
            Assert.IsFalse(lockState.HasToken(null, now));
            Assert.AreEqual(2.0, lockState.CurrentWeight(now), "HasToken does not mutate");
        }
    }
}