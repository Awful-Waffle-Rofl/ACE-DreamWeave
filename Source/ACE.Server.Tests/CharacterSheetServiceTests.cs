using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CharacterSheetService through fakes only: no PropertyManager, PlayerManager or database. One test per
    /// service rule in the Task 5 brief, plus the rotate-during-build race and the SheetSlug shape.
    /// </summary>
    [TestClass]
    public class CharacterSheetServiceTests
    {
        private const uint Guid = 0x50000001;
        private const uint Guid2 = 0x50000002;
        private const string Slug = "Ab3dE5gH9k";
        private const string Slug2 = "Zz9yY8xX7w";

        private DateTime now;

        // the service's monotonic deadline clock, in milliseconds; frozen unless a test advances it
        private long ms;

        [TestInitialize]
        public void Init()
        {
            now = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
            ms = 1_000_000;
        }

        private CharacterSheetService Service(
            FakeCharacterSheetWorld world,
            FakeCharacterSheetLinkRepository links,
            bool enabled = true,
            bool classAbilities = true,
            string host = "char.example.com",
            int cacheSeconds = 120,
            int maxConcurrentOfflineBuilds = 2,
            Func<bool> enabledFunc = null)
        {
            return new CharacterSheetService(world, links,
                enabledFunc ?? (() => enabled),
                () => classAbilities,
                () => host,
                () => cacheSeconds,
                () => now,
                maxConcurrentOfflineBuilds,
                2500,
                () => ms);
        }

        private static IReadOnlyDictionary<uint, List<SheetRank>> Ranks(uint guid, string board, int rank) =>
            new Dictionary<uint, List<SheetRank>>
            {
                [guid] = new List<SheetRank> { new SheetRank { Board = board, Title = board, Rank = rank, ScoreText = "x" } },
            };

        // ---- SheetSlug ----------------------------------------------------------------------------

        [TestMethod]
        public void SheetSlug_New_IsWellFormedAndVaries()
        {
            var seen = new HashSet<string>();
            for (var i = 0; i < 200; i++)
            {
                var slug = SheetSlug.New();
                Assert.AreEqual(SheetSlug.Length, slug.Length);
                Assert.IsTrue(SheetSlug.IsWellFormed(slug), slug);
                seen.Add(slug);
            }

            Assert.IsTrue(seen.Count > 190, "200 random 10-char base62 slugs should essentially never repeat");
        }

        [TestMethod]
        public void SheetSlug_IsWellFormed_RejectsWrongLengthNullAndOutOfAlphabet()
        {
            Assert.IsTrue(SheetSlug.IsWellFormed("0123456789"));
            Assert.IsTrue(SheetSlug.IsWellFormed("abcdefXYZ9"));
            Assert.IsFalse(SheetSlug.IsWellFormed(null));
            Assert.IsFalse(SheetSlug.IsWellFormed(""));
            Assert.IsFalse(SheetSlug.IsWellFormed("Ab3dE5gH9"));
            Assert.IsFalse(SheetSlug.IsWellFormed("Ab3dE5gH9kk"));
            Assert.IsFalse(SheetSlug.IsWellFormed("Ab3dE5gH9!"));
            Assert.IsFalse(SheetSlug.IsWellFormed("Ab3dE5gH9-"));
            Assert.IsFalse(SheetSlug.IsWellFormed("Ab3dE5gH9 "));
            Assert.IsFalse(SheetSlug.IsWellFormed("Ab3dE5gH9" + (char)0xE9), "non-ASCII letters (e-acute) are outside the alphabet");
        }

        // ---- Rule 1: disabled, malformed slug -----------------------------------------------------

        [TestMethod]
        public void GetSheet_Disabled_IsDisabledAndTouchesNothing()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, enabled: false);

            var result = svc.GetSheet(Slug);

            Assert.AreEqual(SheetOutcome.Disabled, result.Outcome);
            Assert.IsNull(result.Sheet);
            Assert.AreEqual(0, links.SlugReads);
            Assert.AreEqual(0, world.PresenceCount);
        }

        [TestMethod]
        public void GetSheet_MalformedSlug_IsNotFoundWithoutADatabaseRead()
        {
            var world = new FakeCharacterSheetWorld();
            var links = new FakeCharacterSheetLinkRepository();
            var svc = Service(world, links);

            foreach (var bad in new[] { null, "", "short", "Ab3dE5gH9kX", "Ab3dE5gH9!", "../../etc1" })
                Assert.AreEqual(SheetOutcome.NotFound, svc.GetSheet(bad).Outcome, bad ?? "(null)");

            Assert.AreEqual(0, links.SlugReads);
        }

        // ---- Rule 2: cache hit --------------------------------------------------------------------

        [TestMethod]
        public void GetSheet_CacheHit_ReturnsCachedSheetWithNoWorldOrRepoCall()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, cacheSeconds: 120);

            var first = svc.GetSheet(Slug);
            now = now.AddSeconds(119);
            var second = svc.GetSheet(Slug);

            Assert.AreEqual(SheetOutcome.Ok, second.Outcome);
            Assert.AreSame(first.Sheet, second.Sheet);
            Assert.AreEqual(1, links.SlugReads);
            Assert.AreEqual(1, world.PresenceCount);
            Assert.AreEqual(1, world.OnlineBuildCount);
        }

        [TestMethod]
        public void GetSheet_CacheEntryAsOldAsCacheSeconds_IsRebuilt()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, cacheSeconds: 120);

            svc.GetSheet(Slug);
            now = now.AddSeconds(120);
            var again = svc.GetSheet(Slug);

            Assert.AreEqual(SheetOutcome.Ok, again.Outcome);
            Assert.AreEqual(2, world.OnlineBuildCount);
            Assert.AreEqual(2, links.SlugReads);
        }

        [TestMethod]
        public void GetSheet_CacheSecondsZero_NeverServesFromCache()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, cacheSeconds: 0);

            svc.GetSheet(Slug);
            svc.GetSheet(Slug);

            Assert.AreEqual(2, world.OnlineBuildCount);
        }

        [TestMethod]
        public void GetSheet_DisabledAfterCaching_IsDisabledNotServedFromCache()
        {
            var on = true;
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, enabledFunc: () => on);

            Assert.AreEqual(SheetOutcome.Ok, svc.GetSheet(Slug).Outcome);
            on = false;

            Assert.AreEqual(SheetOutcome.Disabled, svc.GetSheet(Slug).Outcome);
        }

        // ---- Rule 3: link row ---------------------------------------------------------------------

        [TestMethod]
        public void GetSheet_LinkReadFails_IsFailed()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            links.FailReads = true;
            var svc = Service(world, links);

            Assert.AreEqual(SheetOutcome.Failed, svc.GetSheet(Slug).Outcome);
            Assert.AreEqual(0, world.PresenceCount);
        }

        [TestMethod]
        public void GetSheet_NoLinkRow_IsNotFound()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online } };
            var links = new FakeCharacterSheetLinkRepository();
            var svc = Service(world, links);

            Assert.AreEqual(SheetOutcome.NotFound, svc.GetSheet(Slug).Outcome);
            Assert.AreEqual(1, links.SlugReads);
            Assert.AreEqual(0, world.PresenceCount);
        }

        // ---- Rule 4: missing / deleted character --------------------------------------------------

        [TestMethod]
        public void GetSheet_MissingOrDeletedCharacter_IsNotFoundAndNeverBuilds()
        {
            foreach (var presence in new[] { CharacterPresence.Missing, CharacterPresence.Deleted })
            {
                var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = presence } };
                var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
                var svc = Service(world, links);

                Assert.AreEqual(SheetOutcome.NotFound, svc.GetSheet(Slug).Outcome, presence.ToString());
                Assert.AreEqual(0, world.OnlineBuildCount + world.OfflineBuildCount, presence.ToString());
            }
        }

        [TestMethod]
        public void GetSheet_WorldThrows_IsFailed()
        {
            var world = new FakeCharacterSheetWorld { ThrowOnPresence = true };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links);

            Assert.AreEqual(SheetOutcome.Failed, svc.GetSheet(Slug).Outcome);
        }

        // ---- Rule 5: online / offline / cap / null build ------------------------------------------

        [TestMethod]
        public void GetSheet_Online_BuildsOnlineWithTheClassAbilityFlagAndTimeout()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, classAbilities: false);

            var result = svc.GetSheet(Slug);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreEqual("Character50000001", result.Sheet.Name);
            Assert.AreEqual(1, world.OnlineBuildCount);
            Assert.AreEqual(0, world.OfflineBuildCount);
            Assert.AreEqual(false, world.LastIncludeClassAbilities);
            Assert.AreEqual(TimeSpan.FromMilliseconds(2500), world.LastBuildTimeout);
        }

        [TestMethod]
        public void GetSheet_Offline_BuildsOffline()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Offline } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, classAbilities: true);

            var result = svc.GetSheet(Slug);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreEqual(0, world.OnlineBuildCount);
            Assert.AreEqual(1, world.OfflineBuildCount);
            Assert.AreEqual(true, world.LastIncludeClassAbilities);
        }

        [TestMethod]
        public void GetSheet_OfflineCapReached_IsBusyAndDoesNotBuild()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Offline }, BlockOfflineBuilds = true };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, maxConcurrentOfflineBuilds: 1);

            var first = Task.Run(() => svc.GetSheet(Slug));   // occupies the only slot
            Assert.IsTrue(world.OfflineBuildStarted.Wait(TimeSpan.FromSeconds(5)), "first offline build never started");
            links.With(Guid2, Slug2);
            world.PresenceOf[Guid2] = CharacterPresence.Offline;

            try
            {
                Assert.AreEqual(SheetOutcome.Busy, svc.GetSheet(Slug2).Outcome);
                Assert.AreEqual(1, world.OfflineBuildCount);
            }
            finally
            {
                world.ReleaseOfflineBuilds();
                Assert.IsTrue(first.Wait(TimeSpan.FromSeconds(5)));
            }

            Assert.AreEqual(SheetOutcome.Ok, first.Result.Outcome);
        }

        [TestMethod]
        public void GetSheet_OfflineSlotIsReleasedAfterEachBuild()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Offline, [Guid2] = CharacterPresence.Offline } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug).With(Guid2, Slug2);
            var svc = Service(world, links, maxConcurrentOfflineBuilds: 1);

            Assert.AreEqual(SheetOutcome.Ok, svc.GetSheet(Slug).Outcome);
            Assert.AreEqual(SheetOutcome.Ok, svc.GetSheet(Slug2).Outcome);

            // a null build must release its slot too
            world.OfflineReturnsNull = true;
            svc = Service(world, links, maxConcurrentOfflineBuilds: 1);
            Assert.AreEqual(SheetOutcome.Busy, svc.GetSheet(Slug).Outcome);
            world.OfflineReturnsNull = false;
            Assert.AreEqual(SheetOutcome.Ok, svc.GetSheet(Slug).Outcome);
        }

        [TestMethod]
        public void GetSheet_NullBuild_IsBusyAndIsNotCached()
        {
            var world = new FakeCharacterSheetWorld
            {
                PresenceOf = { [Guid] = CharacterPresence.Online, [Guid2] = CharacterPresence.Offline },
                OnlineReturnsNull = true,
                OfflineReturnsNull = true,
            };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug).With(Guid2, Slug2);
            var svc = Service(world, links);

            var online = svc.GetSheet(Slug);
            var offline = svc.GetSheet(Slug2);

            Assert.AreEqual(SheetOutcome.Busy, online.Outcome);
            Assert.IsNull(online.Sheet);
            Assert.AreEqual(SheetOutcome.Busy, offline.Outcome);
            Assert.AreEqual(0, world.RankBuildCount, "a failed build never reaches the rank step");

            world.OnlineReturnsNull = false;
            Assert.AreEqual(SheetOutcome.Ok, svc.GetSheet(Slug).Outcome);
            Assert.AreEqual(2, world.OnlineBuildCount, "the Busy answer was not cached");
        }

        // ---- Rule 6: rank snapshot ----------------------------------------------------------------

        [TestMethod]
        public void GetSheet_AttachesThisCharactersRanksFromTheSnapshot()
        {
            var world = new FakeCharacterSheetWorld
            {
                PresenceOf = { [Guid] = CharacterPresence.Online, [Guid2] = CharacterPresence.Online },
                RankSnapshot = Ranks(Guid, "level", 3),
            };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug).With(Guid2, Slug2);
            var svc = Service(world, links);

            var ranked = svc.GetSheet(Slug).Sheet;
            var unranked = svc.GetSheet(Slug2).Sheet;

            Assert.AreEqual(1, ranked.Ranks.Count);
            Assert.AreEqual("level", ranked.Ranks[0].Board);
            Assert.AreEqual(3, ranked.Ranks[0].Rank);
            Assert.IsNotNull(unranked.Ranks);
            Assert.AreEqual(0, unranked.Ranks.Count);
            Assert.AreEqual(TimeSpan.FromMilliseconds(2500), world.LastRankTimeout);
        }

        [TestMethod]
        public void GetSheet_RankSnapshotYoungerThan60s_IsReused()
        {
            var world = new FakeCharacterSheetWorld
            {
                PresenceOf = { [Guid] = CharacterPresence.Online, [Guid2] = CharacterPresence.Online },
                RankSnapshot = Ranks(Guid, "level", 1),
            };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug).With(Guid2, Slug2);
            var svc = Service(world, links, cacheSeconds: 0);

            svc.GetSheet(Slug);
            now = now.AddSeconds(59);
            svc.GetSheet(Slug2);
            Assert.AreEqual(1, world.RankBuildCount);

            now = now.AddSeconds(1);
            svc.GetSheet(Slug);
            Assert.AreEqual(2, world.RankBuildCount);
        }

        [TestMethod]
        public void GetSheet_RankBuildFails_UsesThePreviousSnapshot()
        {
            var world = new FakeCharacterSheetWorld
            {
                PresenceOf = { [Guid] = CharacterPresence.Online },
                RankSnapshot = Ranks(Guid, "dps", 7),
            };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, cacheSeconds: 0);

            svc.GetSheet(Slug);
            world.RankSnapshot = null;
            now = now.AddSeconds(61);
            var result = svc.GetSheet(Slug);

            Assert.AreEqual(2, world.RankBuildCount);
            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreEqual(1, result.Sheet.Ranks.Count);
            Assert.AreEqual(7, result.Sheet.Ranks[0].Rank);
        }

        [TestMethod]
        public void GetSheet_RankBuildFailsWithNoPreviousSnapshot_IsOkWithNoRanks()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online }, RankSnapshot = null };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links);

            var result = svc.GetSheet(Slug);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.IsNotNull(result.Sheet.Ranks);
            Assert.AreEqual(0, result.Sheet.Ranks.Count);

            // ranks that fell back to EMPTY are not cached, so the next request tries again
            Assert.AreEqual(0, svc.CachedCount);
            svc.GetSheet(Slug);
            Assert.AreEqual(2, world.OnlineBuildCount);
        }

        [TestMethod]
        public void GetSheet_RankBuildThrowsWithAPreviousSnapshot_IsOkWithThePreviousRanks()
        {
            var world = new FakeCharacterSheetWorld
            {
                PresenceOf = { [Guid] = CharacterPresence.Online },
                RankSnapshot = Ranks(Guid, "dps", 7),
            };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, cacheSeconds: 0);

            svc.GetSheet(Slug);
            world.ThrowOnRankBuild = true;
            now = now.AddSeconds(61);
            var result = svc.GetSheet(Slug);

            Assert.AreEqual(2, world.RankBuildCount);
            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreEqual(1, result.Sheet.Ranks.Count);
            Assert.AreEqual(7, result.Sheet.Ranks[0].Rank);
        }

        // ---- Shared deadline (fix round 1, ruling B) ----------------------------------------------

        [TestMethod]
        public void GetSheet_SlowBuild_LeavesTheRankBuildOnlyTheRemainingBudget()
        {
            var world = new FakeCharacterSheetWorld
            {
                PresenceOf = { [Guid] = CharacterPresence.Online },
                RankSnapshot = Ranks(Guid, "level", 1),
            };
            world.DuringBuild = () => ms += 2000;
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links);

            var result = svc.GetSheet(Slug);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreEqual(TimeSpan.FromMilliseconds(2500), world.LastBuildTimeout);
            Assert.AreEqual(TimeSpan.FromMilliseconds(500), world.LastRankTimeout, "one 2500ms budget spans the build and the rank wait");
        }

        [TestMethod]
        public void GetSheet_BudgetSpentBeforeRanks_ServesTheLastSnapshotWithoutBuildingAndCachesIt()
        {
            var world = new FakeCharacterSheetWorld
            {
                PresenceOf = { [Guid] = CharacterPresence.Online, [Guid2] = CharacterPresence.Online },
                RankSnapshot = new Dictionary<uint, List<SheetRank>>
                {
                    [Guid] = new List<SheetRank> { new SheetRank { Board = "level", Title = "Level", Rank = 1, ScoreText = "x" } },
                    [Guid2] = new List<SheetRank> { new SheetRank { Board = "stamp", Title = "Quest Stamps", Rank = 4, ScoreText = "y" } },
                },
            };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug).With(Guid2, Slug2);
            var svc = Service(world, links);

            svc.GetSheet(Slug);                     // builds the snapshot
            now = now.AddSeconds(61);               // it is now stale
            world.DuringBuild = () => ms += 2500;   // the next sheet build spends the whole budget

            var result = svc.GetSheet(Slug2);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreEqual(1, world.RankBuildCount, "no budget left, so no rank build is attempted");
            Assert.AreEqual(1, result.Sheet.Ranks.Count);
            Assert.AreEqual(4, result.Sheet.Ranks[0].Rank);

            svc.GetSheet(Slug2);
            Assert.AreEqual(2, world.OnlineBuildCount, "a sheet carrying stale-snapshot ranks is cached normally");
        }

        [TestMethod]
        public void GetSheet_BudgetSpentWithNoSnapshot_IsOkWithEmptyRanksAndIsNotCached()
        {
            var world = new FakeCharacterSheetWorld
            {
                PresenceOf = { [Guid] = CharacterPresence.Online },
                RankSnapshot = Ranks(Guid, "level", 1),
            };
            world.DuringBuild = () => ms += 2500;
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links);

            var result = svc.GetSheet(Slug);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreEqual(0, result.Sheet.Ranks.Count);
            Assert.AreEqual(0, world.RankBuildCount);
            Assert.AreEqual(0, svc.CachedCount);

            svc.GetSheet(Slug);
            Assert.AreEqual(2, world.OnlineBuildCount, "the empty-rank fallback was not cached");
        }

        // ---- Offline throw (fix round 1, G) -------------------------------------------------------

        [TestMethod]
        public void GetSheet_OfflineBuildThrows_IsFailedAndReleasesTheSlot()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Offline }, ThrowOnOfflineBuild = true };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links, maxConcurrentOfflineBuilds: 1);

            Assert.AreEqual(SheetOutcome.Failed, svc.GetSheet(Slug).Outcome);

            world.ThrowOnOfflineBuild = false;
            Assert.AreEqual(SheetOutcome.Ok, svc.GetSheet(Slug).Outcome, "the throwing build released its slot");
        }

        // ---- Response cache sweep (fix round 1, E) ------------------------------------------------

        [TestMethod]
        public void ResponseCache_ExpiredEntriesAreSweptOnALaterWrite()
        {
            const uint Guid3 = 0x50000003;
            const string Slug3 = "Qq1wW2eE3r";

            var world = new FakeCharacterSheetWorld
            {
                PresenceOf = { [Guid] = CharacterPresence.Online, [Guid2] = CharacterPresence.Online, [Guid3] = CharacterPresence.Online },
            };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug).With(Guid2, Slug2).With(Guid3, Slug3);
            var svc = Service(world, links, cacheSeconds: 120);
            svc.SweepEveryWrites = 3;

            svc.GetSheet(Slug);
            svc.GetSheet(Slug2);
            Assert.AreEqual(2, svc.CachedCount);

            now = now.AddSeconds(121);
            svc.GetSheet(Slug3);   // the third write sweeps the two expired entries

            Assert.AreEqual(1, svc.CachedCount);
        }

        // ---- Concurrent enable (fix round 1, A) ---------------------------------------------------

        [TestMethod]
        public void EnableOrRotate_ConcurrentFirstEnable_ReturnsTheWinnersRowWithoutRotatingIt()
        {
            var links = new FakeCharacterSheetLinkRepository();
            links.BeforeUpsert = () =>
            {
                // another request enables the same character first; its insert makes ours hit the PK (1062)
                links.BeforeUpsert = null;
                links.With(Guid, "Hooked1234");
                links.ForceCollisions = 1;
            };
            var svc = Service(new FakeCharacterSheetWorld(), links);

            var result = svc.EnableOrRotate(Guid, rotate: false);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.IsTrue(result.Link.Enabled);
            Assert.AreEqual("Hooked1234", result.Link.Slug);
            Assert.AreEqual(1, links.UpsertCalls);
            Assert.AreEqual("Hooked1234", links.SlugOf(Guid));
        }

        [TestMethod]
        public void Rotate_RacingAnotherRotate_ReturnsTheWinnersSlugWithoutUpsertingAgain()
        {
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            links.BeforeUpsert = () =>
            {
                links.BeforeUpsert = null;
                links.With(Guid, Slug2);
                links.ForceCollisions = 1;
            };
            var svc = Service(new FakeCharacterSheetWorld(), links);

            var result = svc.EnableOrRotate(Guid, rotate: true);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreEqual(Slug2, result.Link.Slug);
            Assert.AreEqual(1, links.UpsertCalls);
            Assert.AreEqual(Slug2, links.SlugOf(Guid));
        }

        [TestMethod]
        public void Rotate_PureSlugCollision_RetriesInsteadOfReturningTheUnchangedRow()
        {
            var links = new FakeCharacterSheetLinkRepository { ForceCollisions = 1 }.With(Guid, Slug);
            var svc = Service(new FakeCharacterSheetWorld(), links);

            var result = svc.EnableOrRotate(Guid, rotate: true);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreNotEqual(Slug, result.Link.Slug);
            Assert.AreEqual(2, links.UpsertCalls);
            Assert.AreEqual(result.Link.Slug, links.SlugOf(Guid));
        }

        // ---- Rule 7: cached with ranks ------------------------------------------------------------

        [TestMethod]
        public void GetSheet_CachesTheSheetWithRanksAttached()
        {
            var world = new FakeCharacterSheetWorld
            {
                PresenceOf = { [Guid] = CharacterPresence.Online },
                RankSnapshot = Ranks(Guid, "cap", 2),
            };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links);

            svc.GetSheet(Slug);
            world.RankSnapshot = null;
            var cached = svc.GetSheet(Slug);

            Assert.AreEqual(1, world.OnlineBuildCount);
            Assert.AreEqual(1, world.RankBuildCount);
            Assert.AreEqual(1, cached.Sheet.Ranks.Count);
            Assert.AreEqual("cap", cached.Sheet.Ranks[0].Board);
        }

        // ---- Rule 8: link operations --------------------------------------------------------------

        [TestMethod]
        public void LinkOperations_Disabled_AreDisabledAndTouchNothing()
        {
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(new FakeCharacterSheetWorld(), links, enabled: false);

            Assert.AreEqual(SheetOutcome.Disabled, svc.GetLink(Guid).Outcome);
            Assert.AreEqual(SheetOutcome.Disabled, svc.EnableOrRotate(Guid, rotate: false).Outcome);
            Assert.AreEqual(SheetOutcome.Disabled, svc.EnableOrRotate(Guid, rotate: true).Outcome);
            Assert.AreEqual(SheetOutcome.Disabled, svc.Disable(Guid).Outcome);
            Assert.AreEqual(0, links.CharacterReads + links.UpsertCalls + links.DeleteCalls);
            Assert.AreEqual(Slug, links.SlugOf(Guid));
        }

        [TestMethod]
        public void LinkOperations_RepositoryFailure_IsFailed()
        {
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(new FakeCharacterSheetWorld(), links);

            links.FailReads = true;
            Assert.AreEqual(SheetOutcome.Failed, svc.GetLink(Guid).Outcome);
            Assert.AreEqual(SheetOutcome.Failed, svc.EnableOrRotate(Guid2, rotate: false).Outcome);
            Assert.AreEqual(SheetOutcome.Failed, svc.Disable(Guid).Outcome);

            links.FailReads = false;
            links.FailWrites = true;
            Assert.AreEqual(SheetOutcome.Failed, svc.EnableOrRotate(Guid2, rotate: false).Outcome);
            Assert.AreEqual(1, links.UpsertCalls, "a non-collision write failure is not retried");
            Assert.AreEqual(SheetOutcome.Failed, svc.EnableOrRotate(Guid, rotate: true).Outcome);
            Assert.AreEqual(SheetOutcome.Failed, svc.Disable(Guid).Outcome);
            Assert.AreEqual(Slug, links.SlugOf(Guid));
        }

        [TestMethod]
        public void GetLink_ReturnsTheRowOrADisabledLink()
        {
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(new FakeCharacterSheetWorld(), links);

            var on = svc.GetLink(Guid);
            var off = svc.GetLink(Guid2);

            Assert.AreEqual(SheetOutcome.Ok, on.Outcome);
            Assert.IsTrue(on.Link.Enabled);
            Assert.AreEqual(Slug, on.Link.Slug);

            Assert.AreEqual(SheetOutcome.Ok, off.Outcome);
            Assert.IsFalse(off.Link.Enabled);
            Assert.IsNull(off.Link.Slug);
            Assert.IsNull(off.Link.Url);
        }

        [TestMethod]
        public void EnableOrRotate_NoRotateOnAnExistingRow_ReturnsItUnchanged()
        {
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(new FakeCharacterSheetWorld(), links);

            var result = svc.EnableOrRotate(Guid, rotate: false);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.IsTrue(result.Link.Enabled);
            Assert.AreEqual(Slug, result.Link.Slug);
            Assert.AreEqual(0, links.UpsertCalls);
        }

        [TestMethod]
        public void EnableOrRotate_NoRow_CreatesAWellFormedSlug()
        {
            var links = new FakeCharacterSheetLinkRepository();
            var svc = Service(new FakeCharacterSheetWorld(), links);

            var result = svc.EnableOrRotate(Guid, rotate: false);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.IsTrue(result.Link.Enabled);
            Assert.IsTrue(SheetSlug.IsWellFormed(result.Link.Slug));
            Assert.AreEqual(result.Link.Slug, links.SlugOf(Guid));
            Assert.AreEqual(1, links.UpsertCalls);
        }

        [TestMethod]
        public void EnableOrRotate_SlugCollision_RetriesWithANewSlugThenSucceeds()
        {
            var links = new FakeCharacterSheetLinkRepository { ForceCollisions = 2 };
            var svc = Service(new FakeCharacterSheetWorld(), links);

            var result = svc.EnableOrRotate(Guid, rotate: false);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreEqual(3, links.UpsertCalls);
            Assert.AreEqual(result.Link.Slug, links.SlugOf(Guid));
        }

        [TestMethod]
        public void EnableOrRotate_CollisionsOnEveryAttempt_FailsAfterFiveRetries()
        {
            var links = new FakeCharacterSheetLinkRepository { ForceCollisions = int.MaxValue };
            var svc = Service(new FakeCharacterSheetWorld(), links);

            var result = svc.EnableOrRotate(Guid, rotate: false);

            Assert.AreEqual(SheetOutcome.Failed, result.Outcome);
            Assert.AreEqual(6, links.UpsertCalls, "the first attempt plus five retries");
            Assert.IsNull(links.SlugOf(Guid));
        }

        [TestMethod]
        public void Rotate_EvictsTheOldSlugFromTheCache()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links);

            Assert.AreEqual(SheetOutcome.Ok, svc.GetSheet(Slug).Outcome);
            var rotated = svc.EnableOrRotate(Guid, rotate: true);

            Assert.AreEqual(SheetOutcome.Ok, rotated.Outcome);
            Assert.AreNotEqual(Slug, rotated.Link.Slug);
            Assert.AreEqual(SheetOutcome.NotFound, svc.GetSheet(Slug).Outcome);
            Assert.AreEqual(SheetOutcome.Ok, svc.GetSheet(rotated.Link.Slug).Outcome);
        }

        [TestMethod]
        public void Disable_DeletesTheRowAndEvictsTheOldSlugFromTheCache()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Online } };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links);

            Assert.AreEqual(SheetOutcome.Ok, svc.GetSheet(Slug).Outcome);
            var result = svc.Disable(Guid);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.IsFalse(result.Link.Enabled);
            Assert.IsNull(result.Link.Slug);
            Assert.IsNull(result.Link.Url);
            Assert.IsNull(links.SlugOf(Guid));
            Assert.AreEqual(SheetOutcome.NotFound, svc.GetSheet(Slug).Outcome);
        }

        [TestMethod]
        public void Rotate_WhileABuildForTheOldSlugIsInFlight_DoesNotRecacheTheOldSlug()
        {
            var world = new FakeCharacterSheetWorld { PresenceOf = { [Guid] = CharacterPresence.Offline }, BlockOfflineBuilds = true };
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);
            var svc = Service(world, links);

            var inFlight = Task.Run(() => svc.GetSheet(Slug));
            Assert.IsTrue(world.OfflineBuildStarted.Wait(TimeSpan.FromSeconds(5)), "offline build never started");

            try
            {
                Assert.AreEqual(SheetOutcome.Ok, svc.EnableOrRotate(Guid, rotate: true).Outcome);
            }
            finally
            {
                world.ReleaseOfflineBuilds();
                Assert.IsTrue(inFlight.Wait(TimeSpan.FromSeconds(5)));
            }

            Assert.AreEqual(SheetOutcome.NotFound, svc.GetSheet(Slug).Outcome, "the rotated-away slug must not be served from a cache entry written after the eviction");
        }

        // ---- Rule 9: URL shape --------------------------------------------------------------------

        [TestMethod]
        public void Link_Url_IsHttpsHostSlashSlug_WithTheHostTrimmed()
        {
            var links = new FakeCharacterSheetLinkRepository().With(Guid, Slug);

            Assert.AreEqual("https://char.example.com/" + Slug, Service(new FakeCharacterSheetWorld(), links, host: "char.example.com").GetLink(Guid).Link.Url);
            Assert.AreEqual("https://char.example.com/" + Slug, Service(new FakeCharacterSheetWorld(), links, host: "  char.example.com/  ").GetLink(Guid).Link.Url);
            Assert.AreEqual("https://char.example.com/" + Slug, Service(new FakeCharacterSheetWorld(), links, host: "char.example.com//").GetLink(Guid).Link.Url);
        }
    }
}
