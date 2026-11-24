using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Source pins for the arena copy's Landblock wiring, which needs a world database to run. CreateWorldObjects takes its
    /// outdoor site from EphemeralRealm.OutdoorSiteFor, which reads the Thread run's dungeon before OutdoorEntry; the
    /// encounter suppression and the shard-static skip still key on the Thread exit wcid the arena realm carries. Exact
    /// lines, mutation-proved (see the Task 5 report). The runtime half is GauntletRealmTests.OutdoorEntry_ThreadCopyResolution_Unchanged.
    /// </summary>
    [TestClass]
    public class GauntletLandblockFilterSiteTests
    {
        private static string Landblock() => PooledLootSourceText.Read("Source/ACE.Server/Entity/Landblock.cs");

        private static string Realm() => PooledLootSourceText.Read("Source/ACE.Server/Realms/EphemeralRealm.cs");

        [TestMethod]
        public void Resolver_ReadsRunDungeonBeforeOutdoorEntry()
        {
            var body = PooledLootSourceText.MethodBody(Realm(), "internal static ACE.Server.ThreadDungeons.Defs.OutdoorSiteDef OutdoorSiteFor(EphemeralRealm realm)");

            var shaped = body.IndexOf("if (realm?.ThreadCopyExitPortalWcid == null)", StringComparison.Ordinal);
            var order = body.IndexOf("var outdoorEntry = realm.Run?.Dungeon ?? realm.OutdoorEntry;", StringComparison.Ordinal);
            var outdoor = body.IndexOf("return outdoorEntry?.IsOutdoor == true ? outdoorEntry.Outdoor : null;", StringComparison.Ordinal);

            Assert.IsTrue(shaped >= 0, "only a Thread-shaped copy has an outdoor site");
            Assert.IsTrue(order > shaped, "Run?.Dungeon is read first, OutdoorEntry only when it is null");
            Assert.IsTrue(outdoor > order, "an entry that is not an outdoor row takes the dungeon filter");
        }

        [TestMethod]
        public void CreateWorldObjects_TakesItsSiteFromTheResolver()
        {
            var body = PooledLootSourceText.MethodBody(Landblock(), "private void CreateWorldObjects()");

            var assign = body.IndexOf("var outdoorSite = Realms.EphemeralRealm.OutdoorSiteFor(InnerRealmInfo);", StringComparison.Ordinal);
            var use = body.IndexOf("if (outdoorSite != null)", StringComparison.Ordinal);

            Assert.IsTrue(assign >= 0, "the outdoor site comes from the resolver");
            Assert.IsTrue(use > assign, "and is assigned before the exit and filter branches read it");
            Assert.AreEqual(-1, body.IndexOf("outdoorRun?.Dungeon?.IsOutdoor", StringComparison.Ordinal), "no second, run-only resolution beside it");
        }

        [TestMethod]
        public void ArenaCopy_SkipsShardStaticsAndEncounters_ByTheThreadExitWcid()
        {
            var landblock = Landblock();

            StringAssert.Contains(PooledLootSourceText.MethodBody(landblock, "private void CreateWorldObjects()"),
                "var shardObjects = threadCopyExit != null || !contentSources.HasFlag(Realms.EphemeralContentSources.ShardStatics)");

            var encounters = PooledLootSourceText.MethodBody(landblock, "private void SpawnEncounters()");
            var suppress = encounters.IndexOf("if (InnerRealmInfo?.ThreadCopyExitPortalWcid != null)", StringComparison.Ordinal);
            var load = encounters.IndexOf("GetCachedEncountersByLandblock(", StringComparison.Ordinal);

            Assert.IsTrue(suppress >= 0 && load > suppress, "a Thread-shaped copy returns before any encounter is read");
        }

        [TestMethod]
        public void GetNewGauntletOutdoorLandblock_BuildsTheRealmAndTheThreadGroupLoad()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Managers/RealmManager.cs"),
                "public static Entity.Landblock GetNewGauntletOutdoorLandblock(ACE.Server.ThreadDungeons.Defs.DungeonEntryDef site, IEphemeralHold hold, uint instance)");

            var realm = body.IndexOf("var ephemeralRealm = NewGauntletOutdoorRealm(site, hold);", StringComparison.Ordinal);
            var group = body.IndexOf("return LoadOutdoorGroup(", StringComparison.Ordinal);

            Assert.IsTrue(realm >= 0 && group > realm, $"realm={realm} group={group}");
            StringAssert.Contains(body, "primary => ReferenceEquals(primary.InnerRealmInfo, ephemeralRealm),");
            StringAssert.Contains(body, "loadAdjacents: true, permaload: false, ephemeralRealm: ephemeralRealm)");
        }
    }

    /// <summary>The pure census verdict behind <c>@gauntlet census</c> (GauntletArenaCensus in GauntletAdminCommands.cs).</summary>
    [TestClass]
    public class GauntletArenaCensusTests
    {
        private const uint Exit = 1003604;

        private static GauntletCensusItem Door => new GauntletCensusItem(412, "Door", false, false, false);
        private static GauntletCensusItem ExitPortal => new GauntletCensusItem(Exit, "Thread Exit", false, false, true);

        [TestMethod]
        public void Clean_WithDoorsAndOneExit()
        {
            var c = GauntletArenaCensus.Count(new[] { Door, Door, ExitPortal }, Exit);
            Assert.IsTrue(c.Clean, c.Summary());
            Assert.AreEqual(3, c.Objects);
            Assert.AreEqual(0, c.Offenders.Count);
        }

        [TestMethod]
        public void NotClean_OnACreatureGeneratorOrSecondPortal()
        {
            Assert.IsFalse(GauntletArenaCensus.Count(new[] { ExitPortal, new GauntletCensusItem(1, "Drudge", true, false, false) }, Exit).Clean, "a creature");
            Assert.IsFalse(GauntletArenaCensus.Count(new[] { ExitPortal, new GauntletCensusItem(2, "Gen", false, true, false) }, Exit).Clean, "a generator");
            Assert.IsFalse(GauntletArenaCensus.Count(new[] { ExitPortal, ExitPortal }, Exit).Clean, "two exits");
            Assert.IsFalse(GauntletArenaCensus.Count(new[] { new GauntletCensusItem(3, "Retail Portal", false, false, true) }, Exit).Clean, "one portal that is not the exit");
            Assert.IsFalse(GauntletArenaCensus.Count(new[] { Door }, Exit).Clean, "no exit at all");

            var c = GauntletArenaCensus.Count(new[] { ExitPortal, new GauntletCensusItem(1, "Drudge", true, false, false) }, Exit);
            Assert.AreEqual("1 Drudge (creature)", c.Offenders[0]);
        }
    }
}
