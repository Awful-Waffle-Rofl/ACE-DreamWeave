using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using ACE.Entity.Enum.Properties;

using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// LiveGauntletWorld's occupant source (Task 15 review F5): the world thread must never walk a landblock's object list,
    /// so the players inside a copy come from the online roster filtered by instance. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class GauntletLiveWorldTests
    {
        private const uint Arena = 0x7F010001;

        // The seeded player is built without its constructor, so its position maps are written through UnsafeAccessor
        // (FieldInfo.SetValue on a Player runs its type initializer and poisons the process).
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "positionCache")]
        private static extern ref Dictionary<PositionType, Position> PositionCache(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ephemeralPositions")]
        private static extern ref Dictionary<PositionType, Position> EphemeralPositions(WorldObject wo);

        private static Player At(uint guid, uint? instance)
        {
            var p = SalvageRollerVendorTests.MakePlayer(guid);
            PositionCache(p) = new Dictionary<PositionType, Position>();
            EphemeralPositions(p) = new Dictionary<PositionType, Position>();

            if (instance is { } i)
                PositionCache(p)[PositionType.Location] = new Position(0x54560001, 100, 96, 50, 0, 0, 0, 1, i);

            return p;
        }

        [TestMethod]
        public void PlayersIn_KeepsOnlyTheArenaInstance_Distinct()
        {
            var a = At(0x50000301, Arena);
            var b = At(0x50000302, Arena);
            var outside = At(0x50000303, 0);
            var otherCopy = At(0x50000304, Arena + 1);
            var nowhere = At(0x50000305, null);

            var inside = LiveGauntletWorld.PlayersIn(new[] { a, outside, b, otherCopy, a, null, nowhere }, Arena);

            CollectionAssert.AreEqual(new[] { a, b }, inside);
            Assert.AreEqual(0, LiveGauntletWorld.PlayersIn(new[] { a, outside }, 0).Count, "instance 0 is no copy");
            Assert.AreEqual(0, LiveGauntletWorld.PlayersIn(null, Arena).Count);
        }

        private const string Path = "Source/ACE.Server/AllegianceAvatar/Gauntlet/LiveGauntletWorld.cs";
        private const string Signature = "private static List<Player> PlayersInside(uint instance)";

        internal static string OccupantSourceFault(string body)
        {
            if (body.Contains("GetAllWorldObjectsForDiagnostics", StringComparison.Ordinal) || body.Contains("GetLoadedLandblocksUnder", StringComparison.Ordinal))
                return "PlayersInside walks landblock object lists from the world thread";

            if (!body.Contains("return PlayersIn(PlayerManager.GetAllOnline(), instance);", StringComparison.Ordinal))
                return "PlayersInside does not read the online roster";

            return null;
        }

        [TestMethod]
        public void PlayersInside_ReadsTheOnlineRoster_NotLandblockLists_MutationProved()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Path), Signature);

            Assert.IsNull(OccupantSourceFault(body), "the real source passes");
            Assert.IsNotNull(OccupantSourceFault(body.Replace("return PlayersIn(PlayerManager.GetAllOnline(), instance);",
                "return LandblockManager.GetLoadedLandblocksUnder(instance).SelectMany(lb => lb.GetAllWorldObjectsForDiagnostics().OfType<Player>()).ToList();")), "the old walk faults");
            Assert.IsNotNull(OccupantSourceFault(body.Replace("PlayerManager.GetAllOnline()", "new List<Player>()")), "no roster read faults");
        }

        internal static string GatePlacementFault(string place, string rung)
        {
            if (!place.Contains("MuleSummonHandler.TryPlace(gate, npcLocation, GateDistance, PlaceRung)", StringComparison.Ordinal))
                return "the gate does not use the summon placer's ladder";

            var cell = rung.IndexOf("wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());", StringComparison.Ordinal);
            var enter = rung.IndexOf("return wo.EnterWorld();", StringComparison.Ordinal);

            if (cell < 0 || enter < cell)
                return "a rung does not recompute its cell before it enters the world";

            return null;
        }

        [TestMethod]
        public void GatePlacement_UsesTheSummonLadder_WithTheCellRecomputed_MutationProved()
        {
            var source = PooledLootSourceText.Read(Path);
            var place = PooledLootSourceText.MethodBody(source, "private static bool DefaultPlaceGate(WorldObject gate, Position npcLocation)");
            var rung = PooledLootSourceText.MethodBody(source, "private static bool PlaceRung(WorldObject wo)");

            Assert.IsNull(GatePlacementFault(place, rung), "the real source passes");
            Assert.IsNotNull(GatePlacementFault(place.Replace("MuleSummonHandler.TryPlace(gate, npcLocation, GateDistance, PlaceRung)", "PlaceRung(gate)"), rung), "a single fixed offset faults");
            Assert.IsNotNull(GatePlacementFault(place, rung.Replace("wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());", "")), "no cell recompute faults");
        }

        [TestMethod]
        public void EvictAndRelease_CatchesPerPlayer()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Path), "public void EvictAndRelease(GauntletRun run, string line)");
            var loop = body.IndexOf("foreach (var p in players)", StringComparison.Ordinal);
            var tryAt = body.IndexOf("try", loop, StringComparison.Ordinal);
            var teleport = body.IndexOf("WorldManager.ThreadSafeTeleport(", loop, StringComparison.Ordinal);
            var catchAt = body.IndexOf("catch (Exception ex)", loop, StringComparison.Ordinal);

            Assert.IsTrue(loop >= 0 && tryAt > loop && teleport > tryAt && catchAt > teleport, "each player's eviction is inside its own try in the loop");
        }
    }
}
