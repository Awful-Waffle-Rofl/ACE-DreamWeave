using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-level tripwires for Open World Threads P0 (instance groups), kept only where no behavioural test can
    /// reach: the decisions themselves are tested in EphemeralInstanceGroupTests, LandblockGroupClusteringTests and
    /// EphemeralUnloadThrottleTests, but the code that CALLS them (LandblockManager.GetLandblock / UnloadOne,
    /// Landblock's content guards, InstanceRouting) cannot run in this harness, because a Landblock needs the client
    /// dat to construct. Each assertion is brace-scoped to one method, whole-line comments are stripped so prose
    /// cannot satisfy it, and every run of whitespace is collapsed to one space on BOTH sides, so re-indenting or
    /// re-wrapping a line does not break a tripwire - only changing what it calls does.
    /// </summary>
    [TestClass]
    public class EphemeralInstanceGroupWiringTests
    {
        private const string GetLandblockSignature =
            "public static Landblock GetLandblock(LandblockId landblockId, uint instance, bool loadAdjacents, bool permaload = false, EphemeralRealm ephemeralRealm = null)";

        [TestMethod]
        public void GetLandblock_AssignsTheRoleBeforeInit()
        {
            var body = Method(Source("Managers", "LandblockManager.cs"), GetLandblockSignature);

            var classify = body.IndexOf("landblock.GroupRole = EphemeralInstanceGroup.Classify(", StringComparison.Ordinal);
            var init = body.IndexOf("landblock.Init();", StringComparison.Ordinal);
            Assert.IsTrue(classify >= 0 && init > classify, "GroupRole must be set before landblock.Init(), which reads it to choose the copy's content");
        }

        [TestMethod]
        public void GetLandblock_InheritsRealmAndGatesAdjacents()
        {
            var body = Method(Source("Managers", "LandblockManager.cs"), GetLandblockSignature);

            AssertContains(body, "else if (EphemeralInstanceGroup.InheritsRealm(landblock.GroupRole)) landblock.InnerRealmInfo = livePrimary?.InnerRealmInfo;");
            AssertContains(body, "if (loadAdjacents && EphemeralInstanceGroup.LoadsAdjacents(landblock.GroupRole, lazyBufferRing))");
            AssertContains(body, "if (loadAdjacents && ephemeralRealm != null && ephemeralRealm.Footprint == null)",
                "a single-landblock ephemeral creator must still be refused adjacents");
        }

        [TestMethod]
        public void GetLandblock_CountsLoadsBesideTheLoadedSet_AndTheAllocatorReadsTheCount()
        {
            var source = Source("Managers", "LandblockManager.cs");
            var body = Method(source, GetLandblockSignature);

            var add = body.IndexOf("if (!loadedLandblocks.Add(landblock))", StringComparison.Ordinal);
            var count = body.IndexOf("CountEphemeralLoad(instance, +1);", StringComparison.Ordinal);
            Assert.IsTrue(add >= 0 && count > add, "the load is counted only once loadedLandblocks.Add has succeeded");

            AssertContains(Method(source, "private static bool UnloadOne(Landblock landblock)"),
                "if (loadedLandblocks.Remove(landblock)) { CountEphemeralLoad(landblock.Instance, -1);");
            AssertContains(Method(source, "public static uint RequestNewEphemeralInstanceIDv1(ushort homeRealmId)"),
                "ephemeralInstances.RequestNewInstanceId(homeRealmId, IsAnyLandblockLoadedUnder)");
        }

        [TestMethod]
        public void UnloadOne_OnlyThePrimaryUnregisters_AndThePrimaryGoesLast()
        {
            var body = Method(Source("Managers", "LandblockManager.cs"), "private static bool UnloadOne(Landblock landblock)");

            AssertContains(body, "EphemeralInstanceGroup.MayUnregister(landblock.Id.Landblock, ephemeralInstances.GetFootprint(landblock.Instance))");
            AssertContains(body, "ephemeralInstances.TryUnregister(landblock.Instance, landblock);");
            Assert.IsFalse(body.Contains("ephemeralInstances.TryUnregister(landblock.Instance);"),
                "the unconditional unregister lets the first member to unload end the whole group");
            AssertContains(body, "EphemeralInstanceGroup.TimesUnload(landblock.GroupRole)", "only the primary feeds ace.threads.unload.duration");

            var claim = body.IndexOf("TryClaimGroupPrimaryForDestruction(landblock)", StringComparison.Ordinal);
            var unload = body.IndexOf("landblock.Unload();", StringComparison.Ordinal);
            Assert.IsTrue(claim >= 0 && unload > claim, "the group claim must run before the destructive Unload");
        }

        [TestMethod]
        public void TryClaimGroupPrimary_UsesTheScanAndTheDecision()
        {
            var body = Method(Source("Managers", "LandblockManager.cs"), "private static bool TryClaimGroupPrimaryForDestruction(Landblock primary)");

            AssertContains(body, "EphemeralInstanceGroup.SelectGroupOthers(loadedLandblocks, primary, lb => lb.Instance, lb => lb.HasPlayerIncludingPending(), out occupied)");
            AssertContains(body, "EphemeralInstanceGroup.DecidePrimaryUnload(others.Count, occupied)");
        }

        [TestMethod]
        public void ValidateInstanceDestination_RoutesByFootprint()
        {
            var body = Method(Source("Realms", "InstanceRouting.cs"),
                "public static Position ValidateInstanceDestination(this Position pos, Player player, out InstanceRejection rejection)");

            AssertContains(body, "else if (!EphemeralInstanceGroup.RoutingAccepts(footprint, pos.Cell)) rejection = InstanceRejection.EphemeralLandblockMismatch;");
        }

        [TestMethod]
        public void BothClusteringPaths_UseTheOneRule()
        {
            AssertContains(Method(Source("Entity", "LandblockGroup.cs"), "public bool ShouldBeAddedToThisLandblockGroup(Landblock landblock)"),
                "if (LandblockGroupClustering.MayCluster(value, landblock)) return true;");
            AssertContains(Method(Source("Entity", "LandblockGroupSplitHelper.cs"), "public bool ShouldBeAddedToThisLandblockGroup(Landblock landblock)"),
                "if (LandblockGroupClustering.MayCluster(value, landblock)) return true;");
        }

        [TestMethod]
        public void Landblock_ContentGuardsUseTheChooser()
        {
            var source = Source("Entity", "Landblock.cs");

            AssertContains(source, "internal Realms.EphemeralContentSources ContentSources => Realms.EphemeralInstanceGroup.ContentSourcesFor(GroupRole);");

            var create = Method(source, "private void CreateWorldObjects()");
            AssertContains(create, "var objects = contentSources.HasFlag(Realms.EphemeralContentSources.WorldRows) ? DatabaseManager.World.GetCachedInstancesByLandblock(Id.Landblock, realmId)");
            AssertContains(create, "var shardObjects = threadCopyExit != null || !contentSources.HasFlag(Realms.EphemeralContentSources.ShardStatics)");

            AssertContains(Method(source, "private void SpawnDynamicShardObjects()"),
                "if (!ContentSources.HasFlag(Realms.EphemeralContentSources.ShardDynamics)) return;");
            AssertContains(Method(source, "private void SpawnEncounters()"),
                "if (!ContentSources.HasFlag(Realms.EphemeralContentSources.Encounters)) return;");
        }

        // ------------------------------------------------------------------ helpers

        private static void AssertContains(string normalizedHaystack, string needle, string message = null)
        {
            StringAssert.Contains(normalizedHaystack, Normalize(needle), message);
        }

        private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ");

        /// <summary>The file, with whole-line // and /// comments dropped and whitespace collapsed.</summary>
        private static string Source(params string[] parts)
        {
            var relative = Path.Combine("Source", Path.Combine("ACE.Server", Path.Combine(parts)));

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);

                if (File.Exists(candidate))
                {
                    var text = File.ReadAllText(candidate).Replace("\r\n", "\n");
                    text = Regex.Replace(text, @"^[ \t]*//.*\n", string.Empty, RegexOptions.Multiline);
                    return Normalize(text);
                }
            }

            Assert.Fail($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        private static string Method(string source, string signature)
        {
            var start = source.IndexOf(Normalize(signature), StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not find '{signature}' in the source under test");

            var open = source.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"Unbalanced braces while extracting '{signature}'");
            return null;
        }
    }
}
