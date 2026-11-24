using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// Where the Seat is hooked into the house and landblock code. Each pin names the call and its landmark, so a call
    /// removed, renamed or moved to the wrong side of its landmark fails here.
    /// </summary>
    [TestClass]
    public class AvatarSeatHookSiteTests
    {
        private const string HouseManagerFile = "Source/ACE.Server/Managers/HouseManager.cs";
        private const string PlayerHouseFile = "Source/ACE.Server/WorldObjects/Player_House.cs";
        private const string LandblockFile = "Source/ACE.Server/Entity/Landblock.cs";
        private const string WorldObjectDatabaseFile = "Source/ACE.Server/WorldObjects/WorldObject_Database.cs";
        private const string AvatarManagerFile = "Source/ACE.Server/AllegianceAvatar/AvatarManager.cs";

        private const string HouseLost = "ACE.Server.Avatars.AvatarNpcs.OnHouseLost(house.Guid.Full);";

        private static string Body(string file, string signature) => PooledLootSourceText.MethodBody(PooledLootSourceText.Read(file), signature);

        private static int At(string body, string text, int from = 0)
        {
            var i = body.IndexOf(text, from, StringComparison.Ordinal);
            Assert.IsTrue(i >= 0, $"missing: {text}");
            return i;
        }

        private static int Count(string body, string text)
        {
            var n = 0;
            for (var i = body.IndexOf(text, StringComparison.Ordinal); i >= 0; i = body.IndexOf(text, i + 1, StringComparison.Ordinal))
                n++;
            return n;
        }

        [TestMethod]
        public void Eviction_UnseatsAfterTheOwnerIsCleared_NotBeforeTheRentDisabledReturn()
        {
            var body = Body(HouseManagerFile, "public static void HandleEviction(House house, uint playerGuid, bool multihouse = false, bool force = false)");

            var rentDisabled = At(body, "if (!PropertyManager.GetBool(\"house_rent_enabled\", true).Item && !multihouse && !force)");
            var earlyReturn = At(body, "return;", rentDisabled);
            var ownerCleared = At(body, "house.HouseOwner = null;");
            var hook = At(body, HouseLost);

            Assert.IsTrue(earlyReturn < ownerCleared, "landmark order");
            Assert.IsTrue(ownerCleared < hook, "the Seat goes only once the house has actually been lost");
            Assert.IsTrue(earlyReturn < hook, "a rent-disabled push-forward keeps the house, so it must not unseat");
            Assert.AreEqual(1, Count(body, "AvatarNpcs.OnHouseLost("));
        }

        [TestMethod]
        public void Abandon_UnseatsAfterTheOwnerIsCleared()
        {
            var body = Body(PlayerHouseFile, "public void HandleActionAbandonHouse()");

            var guard = At(body, "if (house != null)");
            var ownerCleared = At(body, "house.HouseOwner = null;", guard);
            var hook = At(body, HouseLost);

            Assert.IsTrue(guard < ownerCleared && ownerCleared < hook, "the abandon hook runs inside the house branch, after the owner is cleared");
            Assert.AreEqual(1, Count(body, "AvatarNpcs.OnHouseLost("));
        }

        [TestMethod]
        public void LandblockInit_SpawnsSeatsInsideThePopulationTask_AfterSkyDecor()
        {
            var body = Body(LandblockFile, "public void Init(bool reload = false)");

            var task = At(body, "Task.Run(() =>");
            var sky = At(body, "SpawnSkyDecor();");
            var seats = At(body, "SpawnAvatarSeats();");
            var continuation = At(body, ".ContinueWith(");

            Assert.IsTrue(task < sky && sky < seats && seats < continuation, "SpawnAvatarSeats runs in the Init task, after SpawnSkyDecor");
            Assert.AreEqual(1, Count(body, "SpawnAvatarSeats();"));
        }

        [TestMethod]
        public void SpawnAvatarSeats_NeverThrows_AndSpawnsOnTheLandblockQueue()
        {
            var body = Body(LandblockFile, "private void SpawnAvatarSeats()");

            var outerTry = At(body, "try");
            var lookup = At(body, "ACE.Server.Avatars.AvatarManager.SeatsOn(Id.Landblock, Instance)");
            var enqueue = At(body, "actionQueue.EnqueueAction(");
            var spawn = At(body, "ACE.Server.Avatars.AvatarNpcs.SpawnSeated(this);");
            var lastCatch = body.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal);

            Assert.IsTrue(outerTry < lookup && lookup < enqueue && enqueue < spawn, "the Seats are read first, then the spawn is queued onto this landblock");
            Assert.IsTrue(spawn < lastCatch, "the outer catch covers the whole body");
            Assert.AreEqual(2, Count(body, "catch (Exception ex)"), "one catch around the lookup and one inside the queued spawn");
        }

        [TestMethod]
        public void AvatarVendor_IsExcludedFromPersistence_AboveTheAlreadySavedShortCircuit()
        {
            var body = Body(WorldObjectDatabaseFile, "public bool IsDynamicThatShouldPersistToShard()");

            var exclusion = At(body, "if (this is ACE.Server.Avatars.AvatarVendor)");
            var saved = At(body, "if (BiotaOriginatedFromOrHasBeenSavedToDatabase())");

            Assert.IsTrue(exclusion < saved);
            Assert.IsTrue(At(body, "return false;", exclusion) < saved);
        }

        [TestMethod]
        public void Recompute_RechecksSeats_AfterTheKeepersAreSettled()
        {
            var body = Body(AvatarManagerFile, "private static void Recompute(double now, AvatarTuning t)");

            var keepers = At(body, "AvatarLifecycle.RecomputeKeeper(monarch, roster);");
            var recheck = At(body, "AvatarNpcs.RecheckSeats();");

            Assert.IsTrue(keepers < recheck);
        }
    }
}
