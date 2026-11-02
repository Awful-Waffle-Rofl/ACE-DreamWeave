using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H5 review findings F5 and F7: the spawner's Hold wiring. Source scans pin the one place a Hold tag is
    /// set (TryPlace, before EnterWorld) and the one place a wave entry's tag reaches its candidate; the test seams drive the
    /// knight and wave bookkeeping the H6 views read, and the throughput rule's friendly exclusion. No landblock, no
    /// PropertyManager, no dat.
    /// </summary>
    [TestClass]
    public class WorldEventSpawnerHoldWiringTests
    {
        private static uint nextGuid = 0x7F0003A1;

        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        private static Creature NewCreature(WorldEventHoldTag tag = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 0x7F0003A1,
                ClassName = "holdwiringtest",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 50 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Hold Wiring Test" },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 100 },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 1000 },
                },
            };

            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            creature.Health.Current = creature.Health.MaxValue;

            if (tag != null)
                Assert.IsTrue(creature.SetWorldEventHoldTag(tag));

            return creature;
        }

        private static WorldEventHoldTag Tag(HoldUnitKind kind) => new WorldEventHoldTag { RunId = 77, Kind = kind };

        // ---- source scans --------------------------------------------------------------------------------------------

        private static string WorldEventsFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldEvents", "WorldEventSpawner.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server/WorldEvents/WorldEventSpawner.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldEvents");
        }

        /// <summary>Code only: // line comments removed, so a doc line naming the call is not counted as the call.</summary>
        private static string Code(string path) =>
            string.Join("\n", File.ReadAllLines(path).Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l.Substring(0, i) : l; }));

        [TestMethod]
        public void TheHoldTag_IsSetExactlyOnce_InTryPlace_BeforeEnterWorld()
        {
            const string Call = "SetWorldEventHoldTag(holdTag)";

            var folder = WorldEventsFolder();
            var hits = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => Regex.Matches(Code(f), Regex.Escape(Call)).Cast<Match>().Select(_ => Path.GetFileName(f)))
                .ToList();

            Assert.AreEqual(1, hits.Count, $"{Call} under ACE.Server/WorldEvents: {string.Join(", ", hits)}");
            Assert.AreEqual("WorldEventSpawner.cs", hits[0]);

            var spawner = Code(Path.Combine(folder, "WorldEventSpawner.cs"));
            var tryPlace = spawner.IndexOf("private bool TryPlace(", StringComparison.Ordinal);
            var set = spawner.IndexOf(Call, StringComparison.Ordinal);
            var enter = spawner.IndexOf("creature.EnterWorld()", tryPlace, StringComparison.Ordinal);

            Assert.IsTrue(tryPlace >= 0, "TryPlace not found");
            Assert.IsTrue(enter > tryPlace, "TryPlace's EnterWorld not found");
            Assert.IsTrue(set > tryPlace && set < enter, $"the tag is set inside TryPlace before EnterWorld (TryPlace {tryPlace}, set {set}, EnterWorld {enter})");

            var nextMethod = spawner.IndexOf("private ", tryPlace + 1, StringComparison.Ordinal);
            Assert.IsTrue(nextMethod < 0 || enter < nextMethod, "the EnterWorld found is TryPlace's own");
        }

        [TestMethod]
        public void AWaveEntrysTag_ReachesItsCandidate()
        {
            var spawner = Code(Path.Combine(WorldEventsFolder(), "WorldEventSpawner.cs"));

            Assert.IsTrue(spawner.Contains("candidate.HoldTag = entry.Tag", StringComparison.Ordinal), "Spawn hands each entry's tag to its candidate");
            Assert.IsTrue(spawner.Contains("candidateEntry.HoldTag)", StringComparison.Ordinal), "PlaceOnLandblock passes the candidate's tag to TryPlace");
        }

        // ---- N2 / N5: the bookkeeping calls, pinned to their methods ----------------------------------------------------

        private sealed record CallPin(string File, string Signature, string Pattern, string What);

        private static readonly CallPin[] Pins =
        {
            new CallPin("WorldEventSpawner.cs", "private bool Adopt(WorldEvent evt, WorldObject wo, WorldEventSpawnKind kind, int waveIndex",
                @"kind == WorldEventSpawnKind\.Protected\)\s*AdoptHoldKnight\(creature\);", "Adopt sends a Protected creature to AdoptHoldKnight"),
            new CallPin("WorldEventSpawner.cs", "private bool Adopt(WorldEvent evt, WorldObject wo, WorldEventSpawnKind kind, int waveIndex",
                @"NoteHoldFinaleBoss\(creature\);", "Adopt notes a finale boss's adopt time"),
            new CallPin("WorldEventSpawner.cs", "public void OnDied(Creature creature)",
                @"RemoveHoldKnight\(creature\);", "OnDied drops a dead knight"),
            new CallPin("WorldEventSpawner.cs", "public void DestroyAll(WorldEvent evt, bool includeCaches)",
                @"ClearHoldKnights\(\);", "DestroyAll clears the knights"),
            new CallPin("WorldEventSpawner.Hold.cs", "private void ApplyFinaleBoss(",
                @"WorldEventHoldFinaleBoss\.DecideScale\(", "ApplyFinaleBoss sizes the boss through DecideScale"),
        };

        // The next member at class indentation: a method or property signature (a field initializer has '=' before any '(').
        private static readonly Regex NextMember = new Regex(@"\n {8}(public|private|internal|protected)\b[^\n;=]*\(", RegexOptions.Compiled);

        /// <summary>The text of the member starting at <paramref name="signature"/>, up to the next member; null when absent.</summary>
        private static string Body(string code, string signature)
        {
            var start = code.IndexOf(signature, StringComparison.Ordinal);

            if (start < 0)
                return null;

            var next = NextMember.Match(code, start + signature.Length);

            return next.Success ? code.Substring(start, next.Index - start) : code.Substring(start);
        }

        private static bool Holds(string code, CallPin pin)
        {
            var body = Body(code, pin.Signature);
            return body != null && Regex.IsMatch(body, pin.Pattern);
        }

        [TestMethod]
        public void TheHoldBookkeepingCalls_SitInTheirMethods()
        {
            var folder = WorldEventsFolder();

            foreach (var pin in Pins)
                Assert.IsTrue(Holds(Code(Path.Combine(folder, pin.File)), pin), $"{pin.What} ({pin.File}: {pin.Signature} / {pin.Pattern})");
        }

        /// <summary>
        /// Each pin discriminates: with its call deleted from an in-memory copy of the real source (the files on disk are
        /// never written), or moved into the next method, the pin fails.
        /// </summary>
        [TestMethod]
        public void EachBookkeepingPin_FailsWhenItsCallIsDeleted()
        {
            var folder = WorldEventsFolder();

            foreach (var pin in Pins)
            {
                var code = Code(Path.Combine(folder, pin.File));
                var body = Body(code, pin.Signature);

                Assert.IsNotNull(body, $"control: {pin.Signature} found");

                var match = Regex.Match(body, pin.Pattern);
                Assert.IsTrue(match.Success, $"control: {pin.What}");

                var bodyStart = code.IndexOf(pin.Signature, StringComparison.Ordinal);
                var deleted = code.Remove(bodyStart + match.Index, match.Length);

                Assert.IsFalse(Holds(deleted, pin), $"{pin.What}: still passes with the call deleted");

                // Moved into the next member: the pin reads only its own method, so it still fails.
                var next = NextMember.Match(deleted, bodyStart + pin.Signature.Length);
                Assert.IsTrue(next.Success, $"control: a member follows {pin.Signature}");

                var lineEnd = deleted.IndexOf('\n', next.Index + 1);
                var moved = deleted.Insert(lineEnd + 1, match.Value + "\n");

                Assert.IsTrue(Regex.IsMatch(moved, pin.Pattern), "control: the call is still in the file");
                Assert.IsFalse(Holds(moved, pin), $"{pin.What}: still passes with the call moved into the next member");
            }
        }

        // ---- the knight bookkeeping ------------------------------------------------------------------------------------

        [TestMethod]
        public void Knights_AdoptRemoveAndClear_DriveLiveKnights_AndKnightGuidsKeepsEveryOne()
        {
            var spawner = new WorldEventSpawner(() => 100.0);
            var a = NewCreature(Tag(HoldUnitKind.Knight));
            var b = NewCreature(Tag(HoldUnitKind.Knight));

            spawner.AdoptHoldKnightForTest(a);
            spawner.AdoptHoldKnightForTest(b);
            spawner.AdoptHoldKnightForTest(a);

            CollectionAssert.AreEqual(new[] { a.Guid.Full, b.Guid.Full }, spawner.KnightGuids.ToArray(), "placement order, no duplicate");
            Assert.AreEqual(3, spawner.LiveKnights.Length, "the live list is a plain list; production Adopt runs once per creature");

            spawner.RemoveHoldKnightForTest(a);
            spawner.RemoveHoldKnightForTest(a);
            CollectionAssert.AreEqual(new[] { b }, spawner.LiveKnights.ToArray());

            b.Health.Current = 0;
            Assert.AreEqual(0, spawner.LiveKnights.Length, "a dead knight is not standing");
            b.Health.Current = b.Health.MaxValue;

            spawner.ClearHoldKnightsForTest();
            Assert.AreEqual(0, spawner.LiveKnights.Length);
            Assert.AreEqual(2, spawner.KnightGuids.Count, "every knight ever placed is remembered");
        }

        [TestMethod]
        public void LiveWaveMonsters_AreTheTaggedStandingWaveCreatures()
        {
            var spawner = new WorldEventSpawner(() => 100.0);
            var tagged = NewCreature(Tag(HoldUnitKind.Monster));
            var untagged = NewCreature();
            var dead = NewCreature(Tag(HoldUnitKind.Monster));

            spawner.AdoptHoldWaveForTest(tagged);
            spawner.AdoptHoldWaveForTest(untagged);
            spawner.AdoptHoldWaveForTest(dead);
            dead.Health.Current = 0;

            CollectionAssert.AreEqual(new[] { tagged }, spawner.LiveWaveMonsters.ToArray());
            Assert.AreEqual(0, spawner.LiveKnights.Length, "a wave monster is never a knight");
        }

        [TestMethod]
        public void PublishHoldTo_HandsTheRunItsKnightsMonstersAndFinaleBossClock_Once()
        {
            var now = 100.0;
            var spawner = new WorldEventSpawner(() => now);
            var knight = NewCreature(Tag(HoldUnitKind.Knight));
            var monster = NewCreature(Tag(HoldUnitKind.Monster));
            var boss = NewCreature(Tag(HoldUnitKind.FinaleBoss));

            spawner.AdoptHoldKnightForTest(knight);
            spawner.AdoptHoldWaveForTest(monster);
            spawner.AdoptHoldWaveForTest(boss);

            var settings = new Dictionary<string, string>
            {
                [HoldKeys.PropertyPrefix + HoldKeys.FinaleEnrageSeconds] = "3",
                [HoldKeys.PropertyPrefix + HoldKeys.FinaleEnrageMult] = "2",
            };
            var run = new WorldEventHoldRun(77, g => WorldEventHoldSettingsResolver.Resolve(g, k => settings.TryGetValue(k, out var v) ? v : null));
            run.Stage(null, 1, 0);

            Assert.AreEqual(1.0, run.FinaleEnrageMult(boss.Guid.Full, 500), "control: no clock before the publish");

            spawner.PublishHoldTo(run);

            CollectionAssert.AreEqual(new[] { knight }, run.Knights.ToArray());
            CollectionAssert.AreEquivalent(new[] { monster, boss }, run.WaveMonsters.ToArray());
            CollectionAssert.AreEqual(new[] { knight.Guid.Full }, run.PlacedKnightGuids.ToArray());
            Assert.AreEqual(2.0, run.FinaleEnrageMult(boss.Guid.Full, 105), 1e-9, "the boss's clock started at its adopt time (100), not the publish");
            Assert.AreEqual(1.0, run.FinaleEnrageMult(boss.Guid.Full, 101), "before the enrage window");

            // A later publish does not restart the clock.
            now = 300.0;
            spawner.PublishHoldTo(run);
            Assert.AreEqual(2.0, run.FinaleEnrageMult(boss.Guid.Full, 111), 1e-9, "the clock is still the adopt time");
        }

        [TestMethod]
        public void ResolvedWaveCount_CountsDistinctWaveIndices()
        {
            var spawner = new WorldEventSpawner(() => 100.0);

            Assert.AreEqual(0, spawner.ResolvedWaveCount);

            spawner.NoteWaveResolvedForTest(0);
            spawner.NoteWaveResolvedForTest(1);
            spawner.NoteWaveResolvedForTest(1);
            spawner.NoteWaveResolvedForTest(-1);

            Assert.AreEqual(2, spawner.ResolvedWaveCount, "a finale sent in two batches counts once; no wave index counts nothing");
        }

        [TestMethod]
        public void AProtectedKnight_CountsAsLive()
        {
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Protected), "a knight takes the creature path (P_WorldEvent, so its death reaches the objective)");
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Decor), "control");
        }

        // ---- F5: a knight's death is not throughput ----------------------------------------------------------------------

        [TestMethod]
        public void AKnightsDeath_IsNeverThroughput()
        {
            var spawner = new WorldEventSpawner(() => 100.0);
            var knight = NewCreature(Tag(HoldUnitKind.Knight));
            var monster = NewCreature(Tag(HoldUnitKind.Monster));

            spawner.AdoptHoldKnightForTest(knight);
            spawner.AdoptHoldWaveForTest(monster);

            // The removal on death must not make a dead knight count again.
            spawner.RemoveHoldKnightForTest(knight);

            Assert.IsTrue(spawner.IsFriendlyGuid(knight.Guid.Full));
            Assert.IsFalse(spawner.IsFriendlyGuid(monster.Guid.Full));

            Assert.IsFalse(WorldEventThroughput.Counts(knight.Guid.Full, 0, spawner.IsDecorGuid(knight.Guid.Full), spawner.IsNpcGuid(knight.Guid.Full), spawner.IsFriendlyGuid(knight.Guid.Full)));
            Assert.IsTrue(WorldEventThroughput.Counts(monster.Guid.Full, 0, spawner.IsDecorGuid(monster.Guid.Full), spawner.IsNpcGuid(monster.Guid.Full), spawner.IsFriendlyGuid(monster.Guid.Full)), "control: a wave monster counts");

            Assert.IsFalse(WorldEventThroughput.Counts(5, 9, isDecor: false, isNpc: false, isFriendly: true));
            Assert.IsTrue(WorldEventThroughput.Counts(5, 9, isDecor: false, isNpc: false), "the default is not friendly");
        }

        [TestMethod]
        public void WorldEventsThroughputNote_PassesTheFriendlyLookup()
        {
            var code = Code(Path.Combine(WorldEventsFolder(), "WorldEvent.cs"));

            Assert.IsTrue(code.Contains("Spawner.IsNpcGuid(guid), Spawner.IsFriendlyGuid(guid))", StringComparison.Ordinal),
                "WorldEvent's NoteThroughput hands Counts the friendly lookup");
        }
    }
}
