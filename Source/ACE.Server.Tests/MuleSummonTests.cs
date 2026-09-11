using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

// The alias rather than a namespace import: ACE.Database.Models.Shard and ACE.Entity.Models both
// declare a Biota, and this file already imports the second one.
using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor (Docs/MuleVendor/DESIGN.md section 11; risks R9, R10): summon, gating, placement and
    /// lifetime - MuleSummonHandler and the Player.CurrentSummonedVendor slot.
    ///
    /// No test in this project constructs a live Player (see PersonalVendorTests.cs, StageTestCommandsTests.cs,
    /// MuleCommerceTests.cs, BankTests.cs, all independently documenting the same DatabaseManager.Authentication /
    /// DatManager.PortalDat blockers). MuleSummonHandler.TrySummon's public signature nonetheless takes a live
    /// Player, so its gating (denylist + authorization) and placement ladder are split into internal cores that
    /// take a VaultActor/Position/WorldObject directly (TryGate, TryPlace) - the same testability split
    /// PersonalVendorTests and AccountVaultStore's own Player-typed API already use.
    ///
    /// Two tests (Summon_DestroysThePreviousVendor, Destroy_ClearsTheSummonerSlot) need a Player only as an
    /// inert holder of the CurrentSummonedVendor auto-property this task adds - no other Player state is
    /// touched by the code under test. FormatterServices.GetUninitializedObject(typeof(Player)), which
    /// MuleCommerceTests.cs evaluated and rejected for broader use (it skips every field initializer up the
    /// WorldObject/Container/Creature chain, so almost any OTHER method NREs on a null collection), is safe
    /// here specifically because neither test calls any Player method or reads any other Player field - both
    /// touch only CurrentSummonedVendor (a plain auto-property with no initializer) and, for the Destroy test,
    /// a guid used purely as a PlayerManager dictionary key.
    ///
    /// This file is also the only place in the assembly that reflects into a field Player declares
    /// DIRECTLY (MakeWiredSummoner's Account backing-field seed) rather than one it inherits from
    /// WorldObject - and doing so is what forces the CLR to run Player's own static type initializer
    /// for the first time in the process (confirmed: FormatterServices.GetUninitializedObject(typeof(
    /// Player)) alone does NOT trigger it - ten other tests in this file call it without issue).
    /// Player's static field initializers (Player_Location.cs) call DatabaseManager.World.
    /// GetCachedWeenie("<hardcoded portal name>") for 11 different string keys, which hits a live
    /// World database and NREs unless EnsureVendorConstructible() has already seeded all 11 names.
    /// EnsureVendorConstructible() is ALWAYS called first in every test here that needs a fully-wired
    /// Player, so this is safe today - but the containment rests entirely on nothing else in the
    /// process touching a Player-declared member by reflection FIRST, and MSTest's class/method
    /// execution order is not guaranteed. If that ever happened, Player's type initializer would run
    /// unseeded, fail once, and EVERY subsequent touch of Player anywhere in the process - not just
    /// the triggering test - would raise a cached TypeInitializationException for the rest of that
    /// run. If a future test needs a Player wired this way, call EnsureVendorConstructible() first
    /// rather than reinventing the seeding.
    /// </summary>
    [TestClass]
    public class MuleSummonTests
    {
        private const uint OwnerAccount = 5101;
        private const uint StrangerAccount = 5102;
        private const uint GranteeAccount = 5103;

        private const uint OwnerCharacter = 0x51000201;
        private const uint StrangerCharacter = 0x51000202;
        private const uint GranteeCharacter = 0x51000203;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Vaultowner");
        private static readonly VaultActor Stranger = new VaultActor(StrangerAccount, StrangerCharacter, "Somebodyelse");
        private static readonly VaultActor DepositOnlyGrantee = new VaultActor(GranteeAccount, GranteeCharacter, "Grantee");

        // Dynamic-range guids (>= ObjectGuid.DynamicMin), distinct from PersonalVendorTests' own
        // static-range 0x713xxxxx block - this file does not need dynamic guids for anything
        // persistence-related (that is MulePersistenceTests.cs's job), but keeping the range distinct
        // avoids any cross-file confusion when reading a failure.
        private static uint nextGuid = 0x71310000;

        private static uint NextGuid() => ++nextGuid;

        private static bool vendorConstructibleSetUpDone;

        /// <summary>Mirrors PersonalVendorTests.EnsureVendorConstructible - see that file's remarks for why each seed is needed.</summary>
        private static void EnsureVendorConstructible()
        {
            if (vendorConstructibleSetUpDone)
                return;

            TestGameTables.EnsureInitialized();

            PropertyManager.ModifyBool("vendor_shop_uses_generator", false);
            PropertyManager.ModifyLong("account_vault_entry_cap", 500);
            PropertyManager.ModifyLong("account_vault_landblock", 0x7F20);

            SeedWorldWeenie((uint)WeenieClassName.W_COINSTACK_CLASS, WeenieType.Coin);

            // Fix round 2: Player's own static field initializers (Player_Location.cs) unconditionally
            // call DatabaseManager.World.GetCachedWeenie("<hardcoded portal name>") for 11 DIFFERENT
            // string keys - MarketplaceDrop (line 25) plus 10 PK-arena spawn points (lines 566-570,
            // 644-648) - the first time the CLR runs Player's type initializer in this process, which
            // FieldInfo.SetValue on ANY field Player declares directly (this file's MakeWiredSummoner
            // seeding Account) forces, even though touching only INHERITED WorldObject members (Biota,
            // PhysicsObj, Location - everything this file's other Player-touching tests use) does not.
            // A string-keyed lookup miss falls through to a live World DB call and NREs (confirmed by
            // reading the TypeInitializationException -> WorldDbContext.OnConfiguring chain this
            // produced before this seed existed, and by re-deriving the full 11-name list from a grep
            // of every GetCachedWeenie("...") call in Player_Location.cs rather than stopping at the
            // first one found). Seeding all 11 names avoids the live DB call entirely; each seeded
            // weenie has no PropertiesPosition, so GetPosition(Destination) returns null and each call
            // site's own `?? new Position(...)` fallback supplies the same value production would use
            // for a landblock with no such portal weenie.
            foreach (var name in new[]
            {
                "portalmarketplace",
                "portalpkarenanew1", "portalpkarenanew2", "portalpkarenanew3", "portalpkarenanew4", "portalpkarenanew5",
                "portalpklarenanew1", "portalpklarenanew2", "portalpklarenanew3", "portalpklarenanew4", "portalpklarenanew5",
            })
            {
                SeedWorldWeenieByName(name, playerStaticFieldWeenieWcid++);
            }

            vendorConstructibleSetUpDone = true;
        }

        private static uint playerStaticFieldWeenieWcid = 90399;

        private static void SeedWorldWeenieByName(string className, uint wcid)
        {
            SeedWorldWeenie(wcid, WeenieType.Generic);

            var nameField = typeof(WorldDatabaseWithEntityCache).GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(nameField, "WorldDatabaseWithEntityCache.weenieClassNameToClassIdCache was not found by reflection - has it been renamed?");

            var nameDict = (ConcurrentDictionary<string, uint>)nameField.GetValue(DatabaseManager.World);
            nameDict[className.ToLower()] = wcid;
        }

        /// <summary>Mirrors PersonalVendorTests.SeedWorldWeenie - Vendor.ValidateVendorRequirements looks up the currency weenie in this cache.</summary>
        private static void SeedWorldWeenie(uint wcid, WeenieType type)
        {
            var weenie = new Weenie { WeenieClassId = wcid, WeenieType = type };

            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");

            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            dict[wcid] = weenie;
        }

        private static Weenie MakeVendorWeenie(bool personalVendor)
        {
            var properties = new Dictionary<PropertyBool, bool>();

            if (personalVendor)
                properties[PropertyBool.PersonalVendor] = true;

            return new Weenie
            {
                WeenieClassId = 90301,
                WeenieType = WeenieType.Vendor,
                PropertiesBool = properties,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, -1 },
                    { PropertyInt.MerchandiseMinValue, 0 },
                    { PropertyInt.MerchandiseMaxValue, 1000000 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.BuyPrice, 1.0 },
                    { PropertyFloat.SellPrice, 1.0 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Test Mule" },
                },
            };
        }

        private static PersonalVendor MakeVendor()
        {
            EnsureVendorConstructible();

            return new PersonalVendor(MakeVendorWeenie(true), new ObjectGuid(NextGuid()));
        }

        private static AccountVaultStore MakeStore(uint accountId, out FakeVaultBackend backend, out FakeVaultWorld world)
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            return new AccountVaultStore(accountId, backend, world);
        }

        /// <summary>
        /// account_vault_allowlist ships with a NON-EMPTY default (016C, the Marketplace), so without
        /// this every pre-existing test in this file - each of which gates at some arbitrary landblock
        /// and expects a pass - would start failing for a reason that has nothing to do with what it
        /// asserts. Both location-gate rows are cleared to the fully-open state here and restored to it
        /// afterwards, so a test that sets one cannot leak it into the next test or into another class
        /// (PropertyManager state is process-global and test.runsettings pins MaxCpuCount=1).
        ///
        /// The tests that care about the allowlist set it themselves, and the shipped DEFAULT is
        /// asserted separately against DefaultStringProperties rather than through this fixture.
        /// </summary>
        [TestInitialize]
        public void ResetLocationGateConfig()
        {
            PropertyManager.ModifyString("account_vault_denylist", "");
            PropertyManager.ModifyString("account_vault_allowlist", "");
            PropertyManager.ModifyString("account_vault_allowlist_name", "the Marketplace");

            // mule_form_enabled is seeded here for the same reason the three above are, and it is a
            // sharper hazard: PropertyManager.GetBool falls through to DatabaseManager.ShardConfig for
            // an uncached key, which NREs in a unit test. MSTest allows exactly one TestInitialize per
            // class, so this fixture owns both settings rather than adding a second one.
            Assert.IsTrue(PropertyManager.ModifyBool("mule_form_enabled", true),
                "mule_form_enabled is missing from DefaultBooleanProperties");
        }

        [TestCleanup]
        public void ClearLocationGateConfig()
        {
            ResetLocationGateConfig();

            // Back to the shipped default, because PropertyManager state is process-global and
            // test.runsettings pins MaxCpuCount=1 - a test that flipped this off would otherwise leak
            // it into every later class in the run.
            PropertyManager.ModifyBool("mule_form_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["mule_form_enabled"].Item);
        }

        private static Position MakeLocation(ushort landblock = 0x0090, float x = 50f, float y = 50f, float z = 0f)
        {
            return new Position
            {
                LandblockId = new LandblockId(landblock),
                PositionX = x,
                PositionY = y,
                PositionZ = z,
                RotationW = 1f,
            };
        }

        private static GenericObject MakeGenericWorldObject()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 90302,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Placement Target" } },
            };

            return new GenericObject(weenie, new ObjectGuid(NextGuid()));
        }

        #region Denylist (DESIGN 11.2)

        /// <summary>
        /// Fix round 2, F7: renamed from Denylist_EmptyByDefault_PermitsEveryLandblock, which passed
        /// with its own mechanism deleted - deleting the `if (string.IsNullOrWhiteSpace(raw)) return
        /// result;` guard at GetDenylistLandblocks still leaves this green, because "".Split(',')
        /// yields [""] and the trimmed.Length == 0 continue already skips it. Confirmed by actually
        /// deleting that guard locally, rebuilding, and rerunning this test before renaming it - it
        /// stayed green. The old name also asserted something this test never touched: parsing to an
        /// empty set is not the same claim as "a summon is permitted", which is what
        /// Gate_WithAnEmptyDenylist_PermitsAnArbitraryLandblock below now covers instead.
        /// </summary>
        [TestMethod]
        public void Denylist_EmptyByDefault_ParsesToAnEmptySet()
        {
            PropertyManager.ModifyString("account_vault_denylist", "");

            var denylist = MuleSummonHandler.GetDenylistLandblocks();

            Assert.AreEqual(0, denylist.Count);
        }

        /// <summary>
        /// The behavioral half F7 asked for: an empty denylist must let TryGate pass for an arbitrary
        /// landblock, not merely parse to an empty set. Drives TryGate (not just
        /// GetDenylistLandblocks), so a regression that broke the denylist-vs-authorization ORDER, or
        /// that broke IsDenylisted's null-set handling, would fail this even if the parse-only test
        /// above stayed green.
        /// </summary>
        [TestMethod]
        public void Gate_WithAnEmptyDenylist_PermitsAnArbitraryLandblock()
        {
            PropertyManager.ModifyString("account_vault_denylist", "");

            var store = MakeStore(OwnerAccount, out _, out _);
            var location = MakeLocation(0x0234); // arbitrary, not the same landblock any other test uses

            var ok = MuleSummonHandler.TryGate(Owner, location, store, out var failReason);

            Assert.IsTrue(ok, failReason);
        }

        [TestMethod]
        public void Denylist_ParsesBareAndPrefixedHex_CaseInsensitively()
        {
            PropertyManager.ModifyString("account_vault_denylist", "0x016C,017d,0X01FF");

            var denylist = MuleSummonHandler.GetDenylistLandblocks();

            Assert.AreEqual(3, denylist.Count);
            Assert.IsTrue(denylist.Contains(0x016C));
            Assert.IsTrue(denylist.Contains(0x017D));
            Assert.IsTrue(denylist.Contains(0x01FF));
        }

        [TestMethod]
        public void Denylist_SkipsAMalformedEntry_WithoutThrowing()
        {
            PropertyManager.ModifyString("account_vault_denylist", "016C, , not-hex ,017D,");

            HashSet<ushort> denylist = null;

            try
            {
                denylist = MuleSummonHandler.GetDenylistLandblocks();
            }
            catch (Exception ex)
            {
                Assert.Fail($"a malformed entry must be skipped with a logged warning, never thrown: {ex}");
            }

            Assert.AreEqual(2, denylist.Count);
            Assert.IsTrue(denylist.Contains(0x016C));
            Assert.IsTrue(denylist.Contains(0x017D));
        }

        [TestMethod]
        public void Denylist_RefusesASummonInAListedLandblock()
        {
            PropertyManager.ModifyString("account_vault_denylist", "016C");

            var store = MakeStore(OwnerAccount, out _, out _);
            var location = MakeLocation(0x016C);

            // Even the owner - full DepositWithdraw access - is refused: the denylist is a LOCATION
            // gate, checked before authorization, not a permission gate.
            var ok = MuleSummonHandler.TryGate(Owner, location, store, out var failReason);

            Assert.IsFalse(ok);
            Assert.IsFalse(string.IsNullOrEmpty(failReason));
        }

        [TestMethod]
        public void Gate_OutsideTheDenylist_PermitsTheOwner()
        {
            PropertyManager.ModifyString("account_vault_denylist", "016C");

            var store = MakeStore(OwnerAccount, out _, out _);
            var location = MakeLocation(0x0090);

            var ok = MuleSummonHandler.TryGate(Owner, location, store, out var failReason);

            Assert.IsTrue(ok, failReason);
        }

        /// <summary>
        /// Reconciliation fix (Task 12): TryGate used AccountVaultStore.GetAccess, which collapses a
        /// grant-read FAILURE into VaultAccess.None - a database outage was reported to the summoner as
        /// "you do not have permission" rather than "unavailable", the most alarming thing this feature
        /// can say to someone with items stored. TryGetAccess distinguishes the two. Security is
        /// unaffected: the store's own withdraw-time check is unconditional regardless of this gate.
        /// Stranger (not the account owner) is required here - the owner's own access short-circuits
        /// before ever touching the grant list, so an owner-actor test would never exercise this path.
        /// </summary>
        [TestMethod]
        public void Gate_ReportsUnavailableRatherThanNoPermission_OnAGrantReadFailure()
        {
            PropertyManager.ModifyString("account_vault_denylist", "");

            var store = MakeStore(OwnerAccount, out var backend, out _);
            backend.FailGrantRead = true;
            var location = MakeLocation(0x0567); // arbitrary, not the same landblock any other test uses

            var ok = MuleSummonHandler.TryGate(Stranger, location, store, out var failReason);

            Assert.IsFalse(ok);
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, failReason, "a grant-read failure must report UnavailableMessage, not the no-permission string");
        }

        /// <summary>
        /// The repo owner's ruling after the first live test - the mule is Marketplace-only - lives in
        /// the SHIPPED DEFAULT of account_vault_allowlist, not in code. Asserted against
        /// DefaultStringProperties rather than GetString, because this file's [TestInitialize]
        /// deliberately clears the live value to the fully-open state for every other test. 016C is the
        /// Marketplace, the same landblock mule_landblocks already defaults to.
        /// </summary>
        [TestMethod]
        public void Allowlist_ShipsDefaultingToTheMarketplace()
        {
            Assert.AreEqual("016C", DefaultPropertyManager.DefaultStringProperties["account_vault_allowlist"].Item);
        }

        [TestMethod]
        public void Allowlist_ParsesBareAndPrefixedHex_CaseInsensitively()
        {
            PropertyManager.ModifyString("account_vault_allowlist", "0x016C,017d,0X01FF");

            var allowlist = MuleSummonHandler.GetAllowlistLandblocks();

            Assert.AreEqual(3, allowlist.Count);
            Assert.IsTrue(allowlist.Contains(0x016C));
            Assert.IsTrue(allowlist.Contains(0x017D));
            Assert.IsTrue(allowlist.Contains(0x01FF));
        }

        [TestMethod]
        public void Allowlist_RefusesASummonOutsideIt()
        {
            PropertyManager.ModifyString("account_vault_allowlist", "016C");

            var store = MakeStore(OwnerAccount, out _, out _);
            var location = MakeLocation(0x0090);

            // Even the owner - full DepositWithdraw access - is refused: like the denylist, this is a
            // LOCATION gate checked before authorization, not a permission gate.
            var ok = MuleSummonHandler.TryGate(Owner, location, store, out var failReason);

            Assert.IsFalse(ok);
            StringAssert.Contains(failReason, "the Marketplace", "the refusal must name where summoning IS permitted, or the player has no way to act on it");
        }

        [TestMethod]
        public void Allowlist_PermitsASummonInsideIt()
        {
            PropertyManager.ModifyString("account_vault_allowlist", "016C");

            var store = MakeStore(OwnerAccount, out _, out _);
            var location = MakeLocation(0x016C);

            var ok = MuleSummonHandler.TryGate(Owner, location, store, out var failReason);

            Assert.IsTrue(ok, failReason);
        }

        /// <summary>
        /// An EMPTY allowlist means "no allowlist configured", never "nothing is allowed". Inverting
        /// that would silently disable the whole feature everywhere the moment the row was blanked,
        /// and it would fail in the safe-looking direction (a refusal, not a crash), so it gets its
        /// own test rather than riding on the fixture.
        /// </summary>
        [TestMethod]
        public void Allowlist_WhenEmpty_PermitsAnArbitraryLandblock()
        {
            PropertyManager.ModifyString("account_vault_allowlist", "");

            var store = MakeStore(OwnerAccount, out _, out _);
            var location = MakeLocation(0x0345); // arbitrary, not the same landblock any other test uses

            var ok = MuleSummonHandler.TryGate(Owner, location, store, out var failReason);

            Assert.IsTrue(ok, failReason);
        }

        /// <summary>
        /// The denylist is the emergency lever and must outrank the allowlist, so a landblock named in
        /// BOTH is refused. Without the ordering, allowlisting the Marketplace would re-open a
        /// Marketplace that had been denied for a live incident.
        /// </summary>
        [TestMethod]
        public void Allowlist_DoesNotReopenADenylistedLandblock()
        {
            PropertyManager.ModifyString("account_vault_allowlist", "016C");
            PropertyManager.ModifyString("account_vault_denylist", "016C");

            var store = MakeStore(OwnerAccount, out _, out _);
            var location = MakeLocation(0x016C);

            var ok = MuleSummonHandler.TryGate(Owner, location, store, out var failReason);

            Assert.IsFalse(ok);
            StringAssert.Contains(failReason, "cannot summon your vault vendor here", "a denylisted landblock must give the denylist refusal, not the allowlist one");
        }

        /// <summary>
        /// The area NAME is a config row precisely so the refusal cannot go stale when the allowlist is
        /// pointed somewhere else. If this ever regresses to a baked-in "the Marketplace", the message
        /// starts lying rather than erroring, which nothing else would catch.
        /// </summary>
        [TestMethod]
        public void Allowlist_RefusalNamesTheConfiguredArea()
        {
            PropertyManager.ModifyString("account_vault_allowlist", "017D");
            PropertyManager.ModifyString("account_vault_allowlist_name", "Holtburg");

            var store = MakeStore(OwnerAccount, out _, out _);
            var location = MakeLocation(0x0090);

            var ok = MuleSummonHandler.TryGate(Owner, location, store, out var failReason);

            Assert.IsFalse(ok);
            StringAssert.Contains(failReason, "Holtburg");
            Assert.IsFalse(failReason.Contains("Marketplace"), "the refusal must read the configured name, not a baked-in one");
        }

        [TestMethod]
        public void Allowlist_RefusalFallsBackWhenTheAreaNameIsBlank()
        {
            PropertyManager.ModifyString("account_vault_allowlist", "016C");
            PropertyManager.ModifyString("account_vault_allowlist_name", "   ");

            var store = MakeStore(OwnerAccount, out _, out _);
            var location = MakeLocation(0x0090);

            var ok = MuleSummonHandler.TryGate(Owner, location, store, out var failReason);

            Assert.IsFalse(ok);
            StringAssert.Contains(failReason, "the permitted area");
        }

        #endregion

        #region Placement ladder (DESIGN 11.3, risk R10)

        [TestMethod]
        public void PlacementLadder_FallsBackToTheSummonerPosition_WhenEveryDistanceFails()
        {
            var summonerLocation = MakeLocation();
            var vendor = MakeGenericWorldObject();

            var attemptCount = 0;

            bool PlaceAttempt(WorldObject wo)
            {
                attemptCount++;
                // Only the FINAL rung (distance 0, the summoner's own position) succeeds.
                return attemptCount == 4;
            }

            var placed = MuleSummonHandler.TryPlace(vendor, summonerLocation, 3.0f, PlaceAttempt);

            Assert.IsTrue(placed, "the final rung (the summoner's exact position) must always succeed");
            Assert.AreEqual(4, attemptCount, "all four rungs (spawnDist, 0.6x, 0.3x, 0) must have been tried in order");
            Assert.AreEqual(summonerLocation.Pos, vendor.Location.Pos, "the final rung must place the vendor exactly at the summoner's position");
        }

        [TestMethod]
        public void PlacementLadder_SetsHomeBeforeEnterWorld()
        {
            var summonerLocation = MakeLocation();
            var vendor = MakeGenericWorldObject();

            Position homeSeenByPlaceAttempt = null;

            bool PlaceAttempt(WorldObject wo)
            {
                // Captured BEFORE returning success - R10 requires Home to be live at this point,
                // because production's placeAttempt is what calls the real EnterWorld/ApproachVendor
                // chain that dereferences it (Vendor.cs:387 via CheckResetToHome).
                homeSeenByPlaceAttempt = wo.Home;
                return true;
            }

            var placed = MuleSummonHandler.TryPlace(vendor, summonerLocation, 3.0f, PlaceAttempt);

            Assert.IsTrue(placed);
            Assert.IsNotNull(homeSeenByPlaceAttempt, "Home must already be set by the time the placement attempt (EnterWorld) runs");
            Assert.AreEqual(vendor.Location.Pos, homeSeenByPlaceAttempt.Pos);
        }

        /// <summary>
        /// Repo owner's ruling after live test 2: the vendor must FACE the summoner, not stand with its
        /// back to them. Heading is derived the same way Position.InFrontOf does it
        /// (atan2(2*qw*qz, 1 - 2*qz*qz), Position.cs:119-125), and the assertion is on the ANGULAR
        /// DIFFERENCE being pi rather than on specific quaternion components, so it holds for any
        /// summoner heading rather than only the identity rotation MakeLocation happens to produce.
        ///
        /// Both arms of the ladder are covered, because they are two different code paths that must
        /// agree: the offset rungs go through Position.InFrontOf(dist, rotate180: true) and the final
        /// rung goes through MuleSummonHandler.FacingBack, which exists only because InFrontOf couples
        /// its rotation to a translation plus a 0.05 bump height.
        /// </summary>
        [TestMethod]
        public void PlacementLadder_TurnsTheVendorToFaceTheSummoner_OnAnOffsetRung()
        {
            // A rotation that is NOT the identity, so a regression that simply copied the summoner's
            // quaternion through would be caught.
            var summonerLocation = MakeLocation();
            summonerLocation.RotationW = 0.923879533f; // 45 degrees about Z
            summonerLocation.RotationZ = 0.382683432f;

            var vendor = MakeGenericWorldObject();

            var placed = MuleSummonHandler.TryPlace(vendor, summonerLocation, 3.0f, wo => true);

            Assert.IsTrue(placed);
            AssertFacesOppositeWay(summonerLocation, vendor.Location);
        }

        [TestMethod]
        public void PlacementLadder_TurnsTheVendorToFaceTheSummoner_OnTheFinalRung()
        {
            var summonerLocation = MakeLocation();
            summonerLocation.RotationW = 0.923879533f;
            summonerLocation.RotationZ = 0.382683432f;

            var vendor = MakeGenericWorldObject();

            var attemptCount = 0;
            var placed = MuleSummonHandler.TryPlace(vendor, summonerLocation, 3.0f, wo => ++attemptCount == 4);

            Assert.IsTrue(placed);
            Assert.AreEqual(4, attemptCount);
            Assert.AreEqual(summonerLocation.Pos, vendor.Location.Pos, "the final rung must still place the vendor at the summoner's exact position - FacingBack must rotate only");
            AssertFacesOppositeWay(summonerLocation, vendor.Location);
        }

        private static void AssertFacesOppositeWay(Position summoner, Position vendor)
        {
            var difference = Math.Abs(HeadingOf(vendor) - HeadingOf(summoner)) % (2 * Math.PI);

            Assert.AreEqual(Math.PI, difference, 0.0001, $"expected the vendor to face back along the summoner's heading; summoner {HeadingOf(summoner):F4} rad, vendor {HeadingOf(vendor):F4} rad");
        }

        private static double HeadingOf(Position position)
        {
            return Math.Atan2(2 * position.RotationW * position.RotationZ, 1 - 2 * position.RotationZ * position.RotationZ);
        }

        [TestMethod]
        public void PlacementLadder_NeverCallsThePlaceAttemptAfterASuccess()
        {
            var summonerLocation = MakeLocation();
            var vendor = MakeGenericWorldObject();

            var attemptCount = 0;

            bool PlaceAttempt(WorldObject wo)
            {
                attemptCount++;
                return true; // succeed on the very first rung
            }

            var placed = MuleSummonHandler.TryPlace(vendor, summonerLocation, 3.0f, PlaceAttempt);

            Assert.IsTrue(placed);
            Assert.AreEqual(1, attemptCount, "a successful rung must stop the ladder immediately");
        }

        /// <summary>
        /// Fix round 2, F1 (coordinator-owned defect - the original brief prescribed vendor.PhysicsObj.
        /// GetPhysicsRadius() and mis-cited it as "copying Pet.Init"). Drives the REAL production glue
        /// directly - not the injected TryPlace seam every other placement test above uses - against a
        /// freshly constructed PersonalVendor whose PhysicsObj is deliberately left null, exactly like
        /// production (WorldObject.PhysicsObj is never initialized before EnterWorld runs -
        /// WorldObject.cs:63-75).
        ///
        /// Before the fix (confirmed by temporarily reverting GetVendorRadius(vendor) back to
        /// vendor.PhysicsObj.GetPhysicsRadius() - the pre-fix-round-2 MuleSummonHandler.cs:238 - then
        /// rebuilding and running this test): TryPlaceVendor threw a NullReferenceException immediately,
        /// with PhysicsObj.GetPhysicsRadius() as the top stack frame and MuleSummonHandler.TryPlaceVendor
        /// directly beneath it - before TryPlace's loop, EnterWorld, or anything landblock-related ever
        /// ran. This test failed (uncaught NRE) against that code.
        ///
        /// After the fix, control reaches all the way to WorldObject.EnterWorld ->
        /// LandblockManager.AddObject -> `new Landblock(...)`, which is the one call this test cannot
        /// follow further: no test in this project constructs a real Landblock (dat/world-db
        /// dependencies), the same class of limit as "no test constructs a live Player" documented in
        /// this file's own class remarks and in PersonalVendorTests.cs/MuleCommerceTests.cs. That NRE is
        /// caught and asserted to originate in Landblock construction specifically - never in
        /// PhysicsObj.GetPhysicsRadius or GetVendorRadius - so a regression of the original defect (or a
        /// new one shaped the same way) still fails this test rather than being silently absorbed by the
        /// catch.
        /// </summary>
        [TestMethod]
        public void TryPlaceVendor_ComputesTheVendorsRadiusFromTheDat_NotFromItsPhysicsObj()
        {
            PropertyManager.ModifyString("account_vault_denylist", "");

            var summoner = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            var summonerLocation = MakeLocation(x: 50f, y: 50f);
            SeedBiota(summoner);
            SeedPositionCaches(summoner);
            SeedPhysicsObj(summoner, summonerLocation);

            // Short-circuits GetPhysicsRadius() on the SUMMONER's side (production always has a real,
            // fully-populated PartArray there - only the not-yet-placed VENDOR's side is this task's
            // concern). PhysicsObj.GetPhysicsRadius() does not null-guard PartArray the way
            // GetRadius()/GetHeight() do (confirmed by reading PhysicsObj.cs:611-626), so this file's
            // shared SeedPhysicsObj (a bare `new PhysicsObj()`) is not enough on its own for a method
            // that calls GetPhysicsRadius specifically.
            summoner.PhysicsObj.State |= PhysicsState.HasPhysicsBSP;
            summoner.Location = summonerLocation;

            var vendor = MakeVendor();

            Assert.IsNull(vendor.PhysicsObj, "sanity: a not-yet-placed vendor must never have a PhysicsObj - if this fails, the fix's whole premise is untested here");

            // Fix round 3, item 1: a bare try/catch with no assertion on the no-throw path passes
            // VACUOUSLY if something upstream short-circuits before ever reaching the landblock
            // barrier (e.g. the denylist check regressing to always-deny would make TryPlace's ladder
            // exhaust every rung and return false cleanly, with the catch block never running and
            // nothing else asserted). `threw` makes that outcome fail instead of passing silently.
            var threw = false;

            try
            {
                MuleSummonHandler.TryPlaceVendor(vendor, summoner);
            }
            catch (NullReferenceException ex)
            {
                threw = true;

                Assert.IsTrue(ex.StackTrace.Contains("Landblock"),
                    "the only NRE this test can tolerate is landblock construction (this test project cannot build a real Landblock) - any OTHER NullReferenceException here means the PhysicsObj-on-a-not-yet-placed-vendor regression is back. Actual stack:\n" + ex.StackTrace);
            }

            Assert.IsTrue(threw, "expected to reach landblock construction; a clean return means the denylist check or the placement ladder short-circuited before this test exercised anything past that point");
        }

        #endregion

        #region Lifetime (DESIGN 11.4)

        /// <summary>
        /// Registers a bare, uninitialized Player into PlayerManager's private online-player table so
        /// WorldObject.Destroy's PersonalVendor branch (PlayerManager.GetOnlinePlayer) can find it. See
        /// class remarks for why this is safe here: the code under test touches only
        /// CurrentSummonedVendor, nothing else on Player. Always paired with
        /// <see cref="UnregisterFakeOnlinePlayer"/> in a finally block - this is a real, shared static
        /// dictionary and must not leak a fake entry into any other test.
        ///
        /// This mutates PlayerManager.onlinePlayers directly rather than through a lock, unlike
        /// production callers of that table - safe here only because test.runsettings pins
        /// MaxCpuCount=1 (see CLAUDE.md's Test section), so no other test can run concurrently and race
        /// this dictionary. A future change to run this test project's tests in parallel would need
        /// this helper to take PlayerManager's own lock (or an equivalent) first.
        /// </summary>
        private static void RegisterFakeOnlinePlayer(uint guid, Player player)
        {
            var field = typeof(PlayerManager).GetField("onlinePlayers", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "PlayerManager.onlinePlayers was not found by reflection - has it been renamed?");

            var dict = (Dictionary<uint, Player>)field.GetValue(null);
            dict[guid] = player;
        }

        private static void UnregisterFakeOnlinePlayer(uint guid)
        {
            var field = typeof(PlayerManager).GetField("onlinePlayers", BindingFlags.NonPublic | BindingFlags.Static);
            var dict = (Dictionary<uint, Player>)field.GetValue(null);
            dict.Remove(guid);
        }

        [TestMethod]
        public void Summon_DestroysThePreviousVendor()
        {
            var summoner = (Player)FormatterServices.GetUninitializedObject(typeof(Player));

            var oldVendor = MakeVendor();
            var newVendor = MakeVendor();

            summoner.CurrentSummonedVendor = oldVendor;

            MuleSummonHandler.AssignSummonedVendor(summoner, newVendor);

            Assert.IsTrue(oldVendor.IsDestroyed, "load-bearing twice (DESIGN 11.4): re-summon must destroy the previous vendor - this is both the one-at-a-time rule and the self-service recovery for a badly placed vendor");
            Assert.AreSame(newVendor, summoner.CurrentSummonedVendor);
        }

        [TestMethod]
        public void Summon_WithNoPreviousVendor_DoesNotThrow()
        {
            var summoner = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            var newVendor = MakeVendor();

            MuleSummonHandler.AssignSummonedVendor(summoner, newVendor);

            Assert.AreSame(newVendor, summoner.CurrentSummonedVendor);
        }

        [TestMethod]
        public void Destroy_ClearsTheSummonerSlot()
        {
            var summoner = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            var vendor = MakeVendor();

            var summonerGuid = NextGuid();
            vendor.SummonerGuid = summonerGuid;
            summoner.CurrentSummonedVendor = vendor;

            RegisterFakeOnlinePlayer(summonerGuid, summoner);

            try
            {
                vendor.Destroy();

                Assert.IsNull(summoner.CurrentSummonedVendor, "Destroy must clear the summoner's slot when it still points at this vendor (DESIGN 11.4, mirroring Pet's P_PetOwner.CurrentActivePet clear)");
            }
            finally
            {
                UnregisterFakeOnlinePlayer(summonerGuid);
            }
        }

        [TestMethod]
        public void Destroy_DoesNotClearADifferentVendorsSlot()
        {
            var summoner = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            var vendor = MakeVendor();
            var otherVendor = MakeVendor();

            var summonerGuid = NextGuid();
            vendor.SummonerGuid = summonerGuid;

            // The slot points at a DIFFERENT vendor than the one being destroyed.
            summoner.CurrentSummonedVendor = otherVendor;

            RegisterFakeOnlinePlayer(summonerGuid, summoner);

            try
            {
                vendor.Destroy();

                Assert.AreSame(otherVendor, summoner.CurrentSummonedVendor, "Destroy must only clear the slot when it points at THIS vendor - a stale/mismatched guid must not clobber a live re-summon");
            }
            finally
            {
                UnregisterFakeOnlinePlayer(summonerGuid);
            }
        }

        /// <summary>
        /// Window-count leak (coordinator correction, task 9): PersonalVendor.Store's setter pins the
        /// store against idle eviction via AddWindow, but nothing called RemoveWindow when the vendor
        /// was destroyed without a rebind - so a store touched by a summoned mule was NEVER evictable
        /// again for the life of the process (TryEvict returns false unconditionally while
        /// OpenWindows &gt; 0, and nothing else clears it). This is not "eviction merely delayed" - it
        /// is permanent, because AccountVaultManager.Tick's only removal path IS TryEvict returning
        /// true. WorldObject.Destroy's `mule.Store = null;` (this task, WorldObject.cs) is what closes
        /// it: the Store setter's RemoveWindow call fires as a side effect of that assignment. This test
        /// must fail if that line is ever removed.
        /// </summary>
        [TestMethod]
        public void Destroy_ReleasesTheStoreWindowPin_SoTheStoreBecomesEvictableAgain()
        {
            var store = MakeStore(OwnerAccount, out _, out _);
            var vendor = MakeVendor();

            vendor.Store = store;
            Assert.AreEqual(1, store.OpenWindows, "sanity: binding Store must pin the window count");

            vendor.Destroy();

            Assert.AreEqual(0, store.OpenWindows, "Destroy must release the window pin");

            var evictable = store.TryEvict(Time.GetUnixTime() + AccountVaultStore.IdleEvictionSeconds + 1.0);

            Assert.IsTrue(evictable, "with the pin released and the idle threshold elapsed, the store must become evictable again - before the fix this returned false forever");
        }

        #endregion

        #region Distance leash wiring (fix round 1, DESIGN 11.4)

        /// <summary>
        /// Gives a bare-constructed WorldObject a real (but otherwise inert) PhysicsObj so
        /// GetCylinderDistance - which Player.MuleVendorLeashTick calls - has something non-null to
        /// read on both sides. `new PhysicsObj()` needs no dat/engine dependency (confirmed by reading
        /// its constructor: every field it sets is a plain default or a `new` of a dependency-free
        /// type - ObjectMaint, ChildList, etc.), and GetRadius()/GetHeight() both short-circuit to 0
        /// when PartArray is null, so distance math reduces to plain position subtraction. The
        /// `Physics.Common.Position(ACE.Entity.Position)` constructor exists specifically to bridge
        /// the two Position types (see PersonalVendorTests-adjacent code for the same split at the
        /// ACE.Entity.Models.Biota / ACE.Database.Models.Shard.Biota level - a different pair, same
        /// "two similarly-named types are not interchangeable" shape).
        /// </summary>
        private static void SeedPhysicsObj(WorldObject wo, Position location)
        {
            var physicsObj = new ACE.Server.Physics.PhysicsObj
            {
                Position = new ACE.Server.Physics.Common.Position(location)
            };

            var prop = typeof(WorldObject).GetProperty("PhysicsObj", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(prop, "WorldObject.PhysicsObj was not found by reflection - has it been renamed?");

            prop.SetValue(wo, physicsObj);
        }

        /// <summary>
        /// A bare-reflection Player has a null Biota (WorldObject's constructor, which normally sets it,
        /// never ran) - and every Heartbeat sub-call ahead of MuleVendorLeashTick that reads a
        /// PropertyBool-backed getter (LifestoneProtectionTick's UnderLifestoneProtection is the one
        /// that actually bit this: WorldObject.GetProperty(PropertyBool) -&gt; Biota.GetProperty(...)
        /// dereferences `biota.PropertiesBool` on a null biota) NREs there instead of reaching the leash
        /// tick at all - confirmed by a debug run that logged the exact stack trace before this fix.
        /// A plain `new Biota()` is enough: its property dictionaries default to null (no field
        /// initializers on Biota's auto-properties), and BiotaExtensions.GetProperty's very first line
        /// is `if (biota.PropertiesBool == null) return null;` - which is satisfied without ever
        /// touching WorldObject.BiotaDatabaseLock (ALSO null here, since it too is a skipped `readonly`
        /// field initializer), so this stays a one-field, dependency-free seed.
        /// </summary>
        /// <summary>
        /// Fix round A, A4: TrySummon now gates on the summoner's own state - IsDead, IsBusy,
        /// Teleporting, suicideInProgress and IsJumping - so both callers inherit the gating Gem.cs
        /// used to hold alone. IsDead reads Health, which is Vitals[MaxHealth], and Creature.Vitals is
        /// a readonly field with an initializer that FormatterServices.GetUninitializedObject skips:
        /// null, so a bare-reflection summoner NREs before it can be gated at all.
        ///
        /// Seeds the dictionary, the MaxHealth vital (whose constructor needs Biota.
        /// PropertiesAttribute2nd to be a real dictionary rather than null), and a positive current
        /// health so the summoner reads as ALIVE. Without the last part every summon test would be
        /// refused as dead, which is the failure mode that looks like a passing gate.
        ///
        /// BiotaDatabaseLock is seeded alongside them purely defensively - today's property reads on
        /// this path all short-circuit on a null property dictionary before touching it (
        /// BiotaExtensions.cs), but that is a property of which properties happen to be unset.
        /// </summary>
        private static void SeedAliveVitals(Player summoner)
        {
            var lockField = typeof(WorldObject).GetField("BiotaDatabaseLock", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(lockField, "WorldObject.BiotaDatabaseLock was not found by reflection - has it been renamed?");
            lockField.SetValue(summoner, new ReaderWriterLockSlim());

            summoner.Biota.PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>();

            var vitalsField = typeof(Creature).GetField("Vitals", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(vitalsField, "Creature.Vitals was not found by reflection - has it been renamed?");
            vitalsField.SetValue(summoner, new Dictionary<PropertyAttribute2nd, CreatureVital>());

            summoner.Vitals[PropertyAttribute2nd.MaxHealth] = new CreatureVital(summoner, PropertyAttribute2nd.MaxHealth);
            summoner.Health.Current = 100;

            Assert.IsFalse(summoner.IsDead, "sanity: a seeded summoner must read as alive, or every summon test below is refused by the A4 death gate rather than by what it means to test");
        }

        private static void SeedBiota(WorldObject wo)
        {
            var field = typeof(WorldObject).GetField("<Biota>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldObject's Biota backing field was not found by reflection - has the property's auto-generated name changed (e.g. from a get-only to a get/init or explicit backing field)?");

            field.SetValue(wo, new ACE.Entity.Models.Biota());
        }

        /// <summary>
        /// PROBE helper: a bare-reflection Player's `positionCache`/`ephemeralPositions` dictionary
        /// field initializers never ran (WorldObject_Properties.cs:761/766), so Location's getter
        /// (which checks ephemeralPositions first unconditionally) NREs before this fix. Seeds both to
        /// empty dictionaries via reflection.
        /// </summary>
        private static void SeedPositionCaches(WorldObject wo)
        {
            foreach (var name in new[] { "positionCache", "ephemeralPositions" })
            {
                var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
                field.SetValue(wo, new Dictionary<PositionType, Position>());
            }

            // BiotaDatabaseLock is `public readonly ReaderWriterLockSlim = new ReaderWriterLockSlim()`
            // (WorldObject_Database.cs:37) - also a skipped field initializer, and
            // BiotaExtensions.SetPosition unconditionally calls rwLock.EnterWriteLock() before ever
            // touching Biota.PropertiesPosition.
            var lockField = typeof(WorldObject).GetField("BiotaDatabaseLock", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(lockField, "WorldObject.BiotaDatabaseLock was not found by reflection - has it been renamed?");
            lockField.SetValue(wo, new System.Threading.ReaderWriterLockSlim());
        }

        /// <summary>
        /// Both same-landblock so Physics.Common.Position.GetOffset's LandDefs.GetBlockOffset takes its
        /// same-block fast path (Vector3.Zero, no dat lookup) - confirmed by reading LandDefs.cs before
        /// relying on it. Distance between the two then reduces to plain |dx|.
        /// </summary>
        private static Position VendorLocationAt(float dx)
        {
            return MakeLocation(x: 50f + dx, y: 50f);
        }

        /// <summary>
        /// Wraps a Heartbeat() call: on a bare-reflection Player (with SeedBiota applied), everything
        /// Heartbeat calls BEFORE MuleVendorLeashTick is a bool/null early-return guard against a
        /// zeroed field or an empty Biota (confirmed by reading NotifyLandblocks, ManaConsumersTick,
        /// HandleTargetVitals, LifestoneProtectionTick, PK_DeathTick, GagsTick and
        /// ClassAbilityBuffsHeartbeat before relying on this), so the leash tick's own effect
        /// (destroying the vendor, or not) has already happened by the time anything downstream might
        /// throw. The one confirmed downstream thrower is the periodic-save check right after: it
        /// unconditionally reads PlayerSaveIntervalSecs (Player_Tick.cs, immediately after
        /// MuleVendorLeashTick), which hits PropertyManager.GetLong's live-shard-DB fallback and NREs -
        /// the same live-Player constructibility gap MuleCommerceTests.cs and PersonalVendorTests.cs
        /// already document project-wide, not something this task introduces or is testing. Verified by
        /// a one-off debug run that logged the full stack trace before this comment was written; the
        /// exception is swallowed here rather than asserted on, because its exact identity can shift
        /// with unrelated changes elsewhere in Heartbeat and is not what these tests are about.
        /// </summary>
        private static void RunHeartbeatIgnoringUnrelatedFailures(Player summoner, double currentUnixTime)
        {
            try
            {
                summoner.Heartbeat(currentUnixTime);
            }
            catch (Exception)
            {
                // Expected past the leash tick - see doc comment above.
            }
        }

        [TestMethod]
        public void Heartbeat_PastTheLeashDistance_DestroysTheMuleAndClearsTheSlot()
        {
            PropertyManager.ModifyDouble("account_vault_summon_leash", 10.0);

            var summoner = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            SeedBiota(summoner);
            var vendor = MakeVendor();

            var summonerLocation = MakeLocation(x: 50f, y: 50f);
            SeedPhysicsObj(summoner, summonerLocation);
            SeedPhysicsObj(vendor, VendorLocationAt(20f)); // 20 > leash of 10

            // WorldObject.Destroy's slot-clear resolves the summoner via
            // PlayerManager.GetOnlinePlayer(mule.SummonerGuid), same as Destroy_ClearsTheSummonerSlot
            // above - register the fake summoner under a matching guid so that lookup succeeds.
            var summonerGuid = NextGuid();
            vendor.SummonerGuid = summonerGuid;
            summoner.CurrentSummonedVendor = vendor;
            RegisterFakeOnlinePlayer(summonerGuid, summoner);

            try
            {
                RunHeartbeatIgnoringUnrelatedFailures(summoner, Time.GetUnixTime());

                Assert.IsTrue(vendor.IsDestroyed, "a heartbeat past the leash distance must destroy the mule");
                Assert.IsNull(summoner.CurrentSummonedVendor, "and clear the summoner's slot - same Destroy() lifecycle as every other despawn path");
            }
            finally
            {
                UnregisterFakeOnlinePlayer(summonerGuid);
            }
        }

        [TestMethod]
        public void Heartbeat_InsideTheLeashDistance_DoesNotDestroyTheMule()
        {
            PropertyManager.ModifyDouble("account_vault_summon_leash", 10.0);

            var summoner = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            SeedBiota(summoner);
            var vendor = MakeVendor();

            var summonerLocation = MakeLocation(x: 50f, y: 50f);
            SeedPhysicsObj(summoner, summonerLocation);
            SeedPhysicsObj(vendor, VendorLocationAt(3f)); // 3 < leash of 10

            summoner.CurrentSummonedVendor = vendor;

            RunHeartbeatIgnoringUnrelatedFailures(summoner, Time.GetUnixTime());

            Assert.IsFalse(vendor.IsDestroyed, "a heartbeat inside the leash distance must not destroy the mule");
            Assert.AreSame(vendor, summoner.CurrentSummonedVendor);
        }

        /// <summary>
        /// Fix round A, A9: this test could not fail. It asserted CurrentSummonedVendor was null having
        /// never set it, and its only call ran through RunHeartbeatIgnoringUnrelatedFailures, which
        /// swallows every exception - so deleting MuleVendorLeashTick() from Player_Tick.Heartbeat
        /// entirely, or making it throw, both left it green.
        ///
        /// Kept rather than deleted, because the property it is named for is real and worth pinning:
        /// the leash tick's null-CurrentSummonedVendor early return is the only cost paid by the
        /// overwhelming majority of players who have no mule out, and an exception there would fire on
        /// every heartbeat of every such player. Made able to fail by calling MuleVendorLeashTick
        /// DIRECTLY and UNWRAPPED - a throw now fails the test instead of being absorbed. It no longer
        /// claims to test Heartbeat's wiring; that claim belongs to
        /// Heartbeat_PastTheLeashDistance_DestroysTheMuleAndClearsTheSlot, which does drive Heartbeat
        /// and does assert an effect.
        /// </summary>
        [TestMethod]
        public void MuleVendorLeashTick_WithNoMuleSummoned_ReturnsWithoutThrowing()
        {
            PropertyManager.ModifyDouble("account_vault_summon_leash", 10.0);

            var summoner = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            SeedBiota(summoner);
            SeedPhysicsObj(summoner, MakeLocation(x: 50f, y: 50f));

            // CurrentSummonedVendor left null - the null-check-first requirement (fix round 1): no
            // distance math must run, and nothing must throw, for a player with no mule out.
            summoner.MuleVendorLeashTick();

            Assert.IsNull(summoner.CurrentSummonedVendor, "a leash tick with no mule out must not invent one");
        }

        /// <summary>
        /// Fix round 2, F3: mirrors Pet.SlowTick's P_PetOwner?.PhysicsObj == null guard. Drives
        /// MuleVendorLeashTick directly (not through Heartbeat) so the summoner's own PhysicsObj can be
        /// left null without also needing to route around every other Heartbeat sub-call that reads it.
        /// Before the fix, GetCylinderDistance(vendor) dereferenced the missing PhysicsObj and threw an
        /// uncaught NullReferenceException instead of destroying the mule.
        /// </summary>
        [TestMethod]
        public void MuleVendorLeashTick_WithNoPhysicsObjOnTheVendor_DestroysItInsteadOfThrowing()
        {
            PropertyManager.ModifyDouble("account_vault_summon_leash", 10.0);

            var summoner = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            SeedBiota(summoner);
            SeedPhysicsObj(summoner, MakeLocation(x: 50f, y: 50f));

            var vendor = MakeVendor();
            // vendor.PhysicsObj deliberately left null - matches TryPlaceVendor's own remarks: a vendor
            // has no PhysicsObj until EnterWorld runs.
            Assert.IsNull(vendor.PhysicsObj, "sanity: this test's whole premise is a vendor with no PhysicsObj yet");

            var summonerGuid = NextGuid();
            vendor.SummonerGuid = summonerGuid;
            summoner.CurrentSummonedVendor = vendor;
            RegisterFakeOnlinePlayer(summonerGuid, summoner);

            try
            {
                summoner.MuleVendorLeashTick();

                Assert.IsTrue(vendor.IsDestroyed, "a missing PhysicsObj on either side must destroy the mule, not throw");
            }
            finally
            {
                UnregisterFakeOnlinePlayer(summonerGuid);
            }
        }

        #endregion

        #region Gate 1-equivalent authorization (DESIGN section 10, exercised through TryGate)

        [TestMethod]
        public void Summon_WithNoGrant_ForAnotherPlayersStore_IsRefused()
        {
            PropertyManager.ModifyString("account_vault_denylist", "");

            var store = MakeStore(OwnerAccount, out _, out _);
            var location = MakeLocation();

            var ok = MuleSummonHandler.TryGate(Stranger, location, store, out var failReason);

            Assert.IsFalse(ok);
            Assert.AreEqual("You do not have permission to use this vault.", failReason);
        }

        [TestMethod]
        public void Summon_WithADepositOnlyGrant_ForAnotherPlayersStore_Succeeds()
        {
            PropertyManager.ModifyString("account_vault_denylist", "");

            var store = MakeStore(OwnerAccount, out var backend, out _);
            var location = MakeLocation();

            backend.Grants.Add(new ACE.Database.Models.Shard.AccountVaultGrant
            {
                Id = 1,
                OwnerAccountId = OwnerAccount,
                GranteeCharacterGuid = GranteeCharacter,
                GranteeCharacterName = "Grantee",
                CanWithdraw = false,
                GrantedAt = DateTime.UtcNow,
            });

            var ok = MuleSummonHandler.TryGate(DepositOnlyGrantee, location, store, out var failReason);

            Assert.IsTrue(ok, failReason);
        }

        #endregion

        #region End-to-end TrySummon (fix round 2, F1 coverage gap / TimeToRot clamp)

        /// <summary>
        /// Seeds GuidManager's private dynamicAlloc field via reflection so the REAL
        /// WorldObjectFactory.CreateNewWorldObject path TrySummon uses can run without a live shard
        /// database. Identical pattern to PersonalVendorTests.EnsureGuidManagerConstructible (see that
        /// file's remarks), with one deliberate correction found while writing
        /// TrySummon_WhenPlacementThrows_StillDestroysTheVendor (fix round 3, item 2): the seeded
        /// `current` MUST be >= ObjectGuid.DynamicMin (0x80000000). PersonalVendorTests' own copy of
        /// this pattern seeds 0x7C000000, which is BELOW DynamicMin - Alloc() does not gate its return
        /// value against `min`/`max` at all (it only compares `current == max`), so that allocator
        /// silently hands out guids that ARE allocated by GuidManager.NewDynamicGuid() but that
        /// Guid.IsDynamic() classifies as NOT dynamic, which makes WorldObject.Destroy()'s `if
        /// (Guid.IsDynamic()) GuidManager.RecycleDynamicGuid(Guid);` a silent no-op. That went
        /// unnoticed there because nothing in PersonalVendorTests exercises recycling; it surfaced
        /// here as a failing assertion (RecycledGuidsTotal never incremented) before this fix. Seeded
        /// well clear of both this file's own static-range guids (0x713xxxxx) and PersonalVendorTests'
        /// own (differently-ranged, non-dynamic) counter, so no test in either file can collide with a
        /// real materialized object's guid. Not fixed in PersonalVendorTests.cs itself - out of this
        /// file's scope, and that file's own tests do not depend on the distinction.
        /// </summary>
        private static bool guidManagerSetUpDone;

        private static void EnsureGuidManagerConstructible()
        {
            if (guidManagerSetUpDone)
                return;

            var allocatorType = typeof(GuidManager).GetNestedType("DynamicGuidAllocator", BindingFlags.NonPublic);
            Assert.IsNotNull(allocatorType, "GuidManager.DynamicGuidAllocator was not found by reflection - has it been renamed?");

            var allocator = FormatterServices.GetUninitializedObject(allocatorType);

            void SetField(string name, object value) =>
                allocatorType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(allocator, value);

            SetField("min", ObjectGuid.DynamicMin);
            SetField("max", ObjectGuid.DynamicMax);
            SetField("current", 0x8D000000u);
            SetField("name", "test-dynamic");
            SetField("recycledGuids", Activator.CreateInstance(typeof(Queue<Tuple<DateTime, uint>>)));

            var availableIdsFieldType = allocatorType.GetField("availableIDs", BindingFlags.NonPublic | BindingFlags.Instance).FieldType;
            SetField("availableIDs", Activator.CreateInstance(availableIdsFieldType));
            SetField("useSequenceGapExhaustedMessageDisplayed", false);

            var dynamicAllocField = typeof(GuidManager).GetField("dynamicAlloc", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(dynamicAllocField, "GuidManager.dynamicAlloc was not found by reflection - has it been renamed?");
            dynamicAllocField.SetValue(null, allocator);

            guidManagerSetUpDone = true;
        }

        /// <summary>Seeds the summonable mule weenie into the world weenie cache under MuleSummonHandler's hardcoded MuleVendorWcid (1003150), so WorldObjectFactory.CreateNewWorldObject(1003150) resolves without a live world database.</summary>
        private static void SeedMuleVendorWeenie()
        {
            SeedWorldWeenie(1003150, WeenieType.Vendor);

            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            dict[1003150] = MakeVendorWeenie(true);
            dict[1003150].WeenieClassId = 1003150;
        }

        /// <summary>
        /// Injects a fake-backed AccountVaultStore straight into AccountVaultManager's private static
        /// `stores` table, so TrySummon's own AccountVaultManager.GetStore(storeAccountId) call returns
        /// it instead of building one against a live shard database. Mirrors this file's other
        /// reflection-into-a-private-static-table helpers (RegisterFakeOnlinePlayer against
        /// PlayerManager.onlinePlayers).
        /// </summary>
        private static void InjectFakeStore(uint accountId, AccountVaultStore store)
        {
            var field = typeof(AccountVaultManager).GetField("stores", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "AccountVaultManager.stores was not found by reflection - has it been renamed?");

            var dict = (ConcurrentDictionary<uint, AccountVaultStore>)field.GetValue(null);
            dict[accountId] = store;
        }

        /// <summary>
        /// Builds a bare-reflection Player wired up enough to drive MuleSummonHandler.TrySummon's
        /// public signature all the way through gating, weenie creation and into the real
        /// TryPlaceVendor - Guid and Account seeded (VaultActor.From and AccountVaultStore.
        /// TryGetAccess both read them - CharacterGuid == 0 is an unconditional refusal, checked BEFORE
        /// the owner-account shortcut), plus every seed TryPlaceVendor itself needs (see
        /// TryPlaceVendor_ComputesTheVendorsRadiusFromTheDat... above for why each of these is here).
        /// </summary>
        private static Player MakeWiredSummoner(uint accountId, uint characterGuid, Position location)
        {
            var summoner = (Player)FormatterServices.GetUninitializedObject(typeof(Player));

            SeedBiota(summoner);
            SeedPositionCaches(summoner);
            SeedAliveVitals(summoner);
            SeedPhysicsObj(summoner, location);
            summoner.PhysicsObj.State |= PhysicsState.HasPhysicsBSP;
            summoner.Location = location;

            var guidField = typeof(WorldObject).GetField("<Guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(guidField, "WorldObject's Guid backing field was not found by reflection - has the property's auto-generated name changed?");
            guidField.SetValue(summoner, new ObjectGuid(characterGuid));

            var accountField = typeof(Player).GetField("<Account>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(accountField, "Player's Account backing field was not found by reflection - has the property's auto-generated name changed?");
            accountField.SetValue(summoner, new Account { AccountId = accountId });

            return summoner;
        }

        /// <summary>
        /// Drives the REAL public MuleSummonHandler.TrySummon end to end - gate, weenie creation via
        /// the real WorldObjectFactory/GuidManager, and into the real TryPlaceVendor - as far as this
        /// test project can go. It cannot go all the way to a successful placement: EnterWorld ->
        /// LandblockManager.AddObject -> `new Landblock(...)` needs real client dat files
        /// (DatManager.CellDat.ReadFromDat, Landblock.cs:206), which no test in this project loads (see
        /// TryPlaceVendor_ComputesTheVendorsRadiusFromTheDat...'s remarks for the identical limit found
        /// there). That NRE is tolerated here on the same terms as that test: caught, and asserted to
        /// come from Landblock construction specifically, so a regression anywhere EARLIER in TrySummon
        /// (gating, guid-leak, the F1 PhysicsObj defect, VaultActor wiring) still fails this test.
        ///
        /// This is also the F1-required "at least one test that drives TrySummon end to end against the
        /// fakes" - it is as end-to-end as this test host can honestly go. The TimeToRot clamp itself
        /// (RestampRot's own math) is unit-tested directly in PersonalVendorTests.RestampRot_
        /// FallsBackToASafeShortLifetime_WhenConfiguredNonPositive, which this task does not own or
        /// duplicate; the paired guard below (TrySummon_CallsRestampRot_NotADuplicatedUnclampedRead)
        /// proves TrySummon actually reaches that call rather than a raw, unclamped config read - the
        /// coordinator's TimeToRot addendum - since RestampRot only runs after a successful placement
        /// that this harness cannot produce.
        /// </summary>
        [TestMethod]
        public void TrySummon_GatesCreatesAndAttemptsPlacement_EndToEnd()
        {
            EnsureVendorConstructible();
            EnsureGuidManagerConstructible();
            SeedMuleVendorWeenie();

            PropertyManager.ModifyString("account_vault_denylist", "");
            PropertyManager.ModifyLong("account_vault_summon_rot_seconds", -1); // coordinator's TimeToRot addendum: misconfigured on purpose
            MuleSummonHandler.ClearSummonCooldowns(); // A5's window is process-wide static state

            var store = MakeStore(OwnerAccount, out _, out _);
            InjectFakeStore(OwnerAccount, store);

            var characterGuid = NextGuid();
            var summonerLocation = MakeLocation(x: 50f, y: 50f);
            var summoner = MakeWiredSummoner(OwnerAccount, characterGuid, summonerLocation);

            // Fix round 3, item 1: same vacuous-pass fix as TryPlaceVendor_ComputesTheVendorsRadiusFromTheDat...
            // above - without `threw`, a regression to TryGate always denying, the factory no longer
            // returning a PersonalVendor, or the denylist check regressing to always-true would all
            // make TrySummon return false cleanly, the catch never firing, and this test reporting
            // green having verified nothing past the gate.
            var threw = false;

            try
            {
                MuleSummonHandler.TrySummon(summoner, OwnerAccount, out var failReason);
            }
            catch (NullReferenceException ex)
            {
                threw = true;

                Assert.IsTrue(ex.StackTrace.Contains("Landblock"),
                    "the only NRE this test can tolerate is landblock construction (this test project cannot build a real Landblock) - any OTHER NullReferenceException here means something earlier in TrySummon regressed. Actual stack:\n" + ex.StackTrace);
            }

            Assert.IsTrue(threw, "expected to reach landblock construction; a clean return means gating, creation or placement short-circuited before this test exercised anything");
        }

        /// <summary>
        /// Fix round 3, item 2: TryPlaceVendor (or the EnterWorld -> LandblockManager.AddObject ->
        /// new Landblock(...) chain underneath it) can THROW rather than return false - this test
        /// exploits the very same landblock-construction barrier the two tests above tolerate (this
        /// test project cannot build a real Landblock) as its throwing scenario, rather than adding a
        /// dedicated injection seam to TrySummon purely for this test. Proves TrySummon's try/catch
        /// around TryPlaceVendor still destroys the vendor (recycling its dynamic guid) on a THROWING
        /// placement, not just one that returns false - a bare `if (!TryPlaceVendor(...))` lets the
        /// throw escape the condition and skip Destroy() entirely, leaking the guid.
        /// GuidManager.RecycledGuidsTotal is a real, process-wide counter (not per-test-isolated), so
        /// this asserts the DELTA across the call rather than an absolute count.
        /// </summary>
        [TestMethod]
        public void TrySummon_WhenPlacementThrows_StillDestroysTheVendor()
        {
            EnsureVendorConstructible();
            EnsureGuidManagerConstructible();
            SeedMuleVendorWeenie();

            PropertyManager.ModifyString("account_vault_denylist", "");
            PropertyManager.ModifyLong("account_vault_summon_rot_seconds", 600);
            MuleSummonHandler.ClearSummonCooldowns(); // A5's window is process-wide static state

            var store = MakeStore(OwnerAccount, out _, out _);
            InjectFakeStore(OwnerAccount, store);

            var characterGuid = NextGuid();
            var summonerLocation = MakeLocation(x: 50f, y: 50f);
            var summoner = MakeWiredSummoner(OwnerAccount, characterGuid, summonerLocation);

            var recycledBefore = GuidManager.RecycledGuidsTotal;

            Exception caught = null;

            try
            {
                MuleSummonHandler.TrySummon(summoner, OwnerAccount, out var failReason);
            }
            catch (NullReferenceException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, "this test's premise is that placement THROWS (the landblock-construction barrier) - if nothing threw, this test proves nothing about the throw-safety fix");
            Assert.IsTrue(caught.StackTrace.Contains("Landblock"),
                "expected the known landblock-construction NRE, not a different regression. Actual stack:\n" + caught.StackTrace);

            Assert.AreEqual(recycledBefore + 1, GuidManager.RecycledGuidsTotal,
                "a THROWING placement must still destroy the vendor (which recycles its dynamic guid), not just a placement that returns false");
        }

        /// <summary>
        /// Coordinator's TimeToRot addendum: TrySummon must stamp TimeToRot by calling
        /// PersonalVendor.RestampRot() - which is independently clamped (PersonalVendorTests.
        /// RestampRot_FallsBackToASafeShortLifetime_WhenConfiguredNonPositive) - rather than by
        /// duplicating account_vault_summon_rot_seconds's raw config read a second time in this file. A
        /// duplicated raw read is exactly how a non-positive config value could resolve to TimeToRot ==
        /// -1 ("Never Rot", WorldObject_Decay.cs:42) again even after RestampRot's own clamp is correct,
        /// since nothing would connect the two. RestampRot only runs after a successful placement this
        /// test project cannot produce (see TrySummon_GatesCreatesAndAttemptsPlacement_EndToEnd's
        /// remarks), so this is asserted against source rather than behaviourally - with comments
        /// stripped first (AccountVaultPurgeTests.StripComments' rationale applies identically here: an
        /// earlier draft of this exact check would have been satisfiable by a comment alone).
        /// </summary>
        [TestMethod]
        public void TrySummon_CallsRestampRot_NotADuplicatedUnclampedRead()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "Entity", "AccountVault", "MuleSummonHandler.cs");
            var raw = File.ReadAllText(path);
            var code = StripComments(raw);

            // Control for the stripper itself.
            StringAssert.Contains(raw, "TimeToRot",
                "sanity: MuleSummonHandler.cs must still mention TimeToRot somewhere, or this file has drifted out from under this test");

            StringAssert.Contains(code, "vendor.RestampRot()",
                "TrySummon must stamp TimeToRot by calling PersonalVendor.RestampRot(), not a duplicated raw config read");

            Assert.IsFalse(code.Contains("PropertyManager.GetLong(\"account_vault_summon_rot_seconds\")") || code.Contains("PropertyManager.GetDouble(\"account_vault_summon_rot_seconds\")"),
                "MuleSummonHandler.cs must not read account_vault_summon_rot_seconds directly - that is exactly the duplicated, unclamped path the TimeToRot addendum closed. RestampRot (owned by PersonalVendor.cs) is the only place this config key may be read.");
        }

        #endregion

        #region Fix round A: store warming (A2), vendor name (A3), summoner-state gating (A4), summon cooldown (A5)

        /// <summary>
        /// A2, the mechanism half. Reading AccountVaultStore.IsLoaded is itself the call that STARTS
        /// the load - TryEnsureLoadedLocked reads the index and constructs the containers - and on that
        /// same call AllVaultsLoadedLocked is still false because the containers' inventory load is
        /// async. That is the whole reason warming has to happen at summon time and not at first click.
        /// Proven here against the fake backend's index-read counter rather than assumed.
        /// </summary>
        [TestMethod]
        public void ReadingIsLoaded_OnAColdStore_StartsTheIndexRead()
        {
            var store = MakeStore(OwnerAccount, out var backend, out _);

            Assert.AreEqual(0, backend.VaultReads,
                "sanity: a freshly built store must not have read its index yet, or this test cannot tell warming from construction");

            // The VALUE is not the claim here - the side effect is. A store with no vaults at all
            // legitimately reports loaded on the first read; a store with vaults does not.
            _ = store.IsLoaded;

            Assert.AreEqual(1, backend.VaultReads,
                "reading IsLoaded must kick the index read - that is what makes it usable as the summon path's warm-up");
        }

        /// <summary>
        /// A2, the placement half. TrySummon cannot be driven to a successful placement in this test
        /// host (no live landblock - see TrySummon_GatesCreatesAndAttemptsPlacement_EndToEnd's
        /// remarks), and the warm-up necessarily sits after placement succeeds, so this is a source
        /// scan for the same reason TrySummon_CallsRestampRot_NotADuplicatedUnclampedRead is one.
        ///
        /// It is a source scan and it proves PLACEMENT, not behaviour: that TrySummon touches
        /// store.IsLoaded, and that it does so between binding the store and handing the vendor to the
        /// summoner. Before this fix nothing on the summon path read IsLoaded at all - TryGetAccess
        /// short-circuits for the owning account before any load - so every returning player's first
        /// click was refused with "still loading".
        /// </summary>
        [TestMethod]
        public void CallSite_TrySummon_WarmsTheStoreBeforeHandingOverTheVendor()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "Entity", "AccountVault", "MuleSummonHandler.cs");
            var code = StripComments(File.ReadAllText(path));

            var iBind = code.IndexOf("vendor.Store = store;", StringComparison.Ordinal);
            var iWarm = code.IndexOf("store.IsLoaded", StringComparison.Ordinal);
            var iAssign = code.IndexOf("AssignSummonedVendor(summoner, vendor)", StringComparison.Ordinal);

            Assert.IsTrue(iBind >= 0, "TrySummon must still bind the store to the vendor");
            Assert.IsTrue(iWarm >= 0, "TrySummon must read store.IsLoaded to start the vault load - without it the first click on a fresh mule is always refused as still loading");
            Assert.IsTrue(iAssign >= 0, "TrySummon must still hand the vendor to the summoner through AssignSummonedVendor");

            Assert.IsTrue(iWarm > iBind && iWarm < iAssign,
                "the warm-up must sit between binding the store and handing the vendor over: earlier and there is no store to warm, later and the player can already be clicking it");
        }

        /// <summary>
        /// A3 / DESIGN section 12. The composed name is asserted directly because the assignment site
        /// itself is unreachable in this host (it is inside TrySummon, past object creation), and its
        /// placement is pinned by the call-site scan below.
        /// </summary>
        [TestMethod]
        public void ComposeVendorName_UsesTheOwningCharactersName()
        {
            Assert.AreEqual("Vaultowner's Vault", MuleSummonHandler.ComposeVendorName("Vaultowner"));
            Assert.AreEqual("Vaultowner's Vault", MuleSummonHandler.ComposeVendorName("  Vaultowner  "),
                "a resolved name is trimmed rather than pasted in with its whitespace");
        }

        /// <summary>
        /// A3's other half: an unresolvable owner name must leave the weenie's own Name alone rather
        /// than writing "'s Vault" or a blank over it.
        /// </summary>
        [TestMethod]
        public void ComposeVendorName_WithNoResolvedName_IsNull()
        {
            Assert.IsNull(MuleSummonHandler.ComposeVendorName(null));
            Assert.IsNull(MuleSummonHandler.ComposeVendorName(""));
            Assert.IsNull(MuleSummonHandler.ComposeVendorName("   "));
        }

        /// <summary>
        /// A3, placement. Source scan, proving placement and not behaviour: TrySummon must assign
        /// vendor.Name from ComposeVendorName, and must do it BEFORE placement, because EnterWorld
        /// broadcasts a create message that serializes the name to every client in range - a name
        /// assigned afterwards leaves everyone looking at the weenie's default until something
        /// unrelated re-sends it.
        /// </summary>
        [TestMethod]
        public void CallSite_TrySummon_NamesTheVendorBeforePlacingIt()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "Entity", "AccountVault", "MuleSummonHandler.cs");
            var code = StripComments(File.ReadAllText(path));

            var iName = code.IndexOf("vendor.Name = vendorName", StringComparison.Ordinal);
            var iCompose = code.IndexOf("ComposeVendorName(ownerName)", StringComparison.Ordinal);
            var iPlace = code.IndexOf("placed = TryPlaceVendor(vendor, summoner)", StringComparison.Ordinal);

            Assert.IsTrue(iCompose >= 0, "TrySummon must derive the vendor name from the resolved owner name (DESIGN section 12), never from player input");
            Assert.IsTrue(iName >= 0, "TrySummon must actually assign vendor.Name - DESIGN section 12 was documented as done while nothing in the branch assigned it");
            Assert.IsTrue(iPlace >= 0, "TrySummon must still place the vendor");

            Assert.IsTrue(iName < iPlace,
                "the name must be assigned before EnterWorld broadcasts the object, or every client in range sees the default weenie name instead");
        }

        /// <summary>
        /// A4. The summoner-state gate is a private of MuleSummonHandler, so it is asserted through the
        /// public TrySummon with a summoner that is busy - the cheapest of the five to stage on a
        /// bare-reflection Player. Before this fix the /mule command path checked none of the five,
        /// and only the contract item was gated (by Gem.cs, which is not on the command path at all).
        /// </summary>
        [TestMethod]
        public void TrySummon_WithABusySummoner_IsRefusedBeforeAnythingIsCreated()
        {
            EnsureVendorConstructible();
            EnsureGuidManagerConstructible();
            SeedMuleVendorWeenie();

            PropertyManager.ModifyString("account_vault_denylist", "");
            MuleSummonHandler.ClearSummonCooldowns();

            var store = MakeStore(OwnerAccount, out _, out _);
            InjectFakeStore(OwnerAccount, store);

            var summoner = MakeWiredSummoner(OwnerAccount, NextGuid(), MakeLocation(x: 50f, y: 50f));
            summoner.IsBusy = true;

            var recycledBefore = GuidManager.RecycledGuidsTotal;

            var ok = MuleSummonHandler.TrySummon(summoner, OwnerAccount, out var failReason);

            Assert.IsFalse(ok, "a busy summoner must be refused - this is the gating Gem.cs held alone before A4");
            Assert.AreEqual("You are too busy.", failReason);

            Assert.AreEqual(recycledBefore, GuidManager.RecycledGuidsTotal,
                "the refusal must come before any world object is created, so there is no guid to recycle - refusing after creation would be a leak as well as a wasted spawn");
        }

        /// <summary>
        /// A4, the death half, staged through the same public entry point. Confirms the gate reads the
        /// summoner's actual vitals rather than a constant, which the busy test above cannot show.
        /// </summary>
        [TestMethod]
        public void TrySummon_WithADeadSummoner_IsRefused()
        {
            EnsureVendorConstructible();
            EnsureGuidManagerConstructible();
            SeedMuleVendorWeenie();

            PropertyManager.ModifyString("account_vault_denylist", "");
            MuleSummonHandler.ClearSummonCooldowns();

            var store = MakeStore(OwnerAccount, out _, out _);
            InjectFakeStore(OwnerAccount, store);

            var summoner = MakeWiredSummoner(OwnerAccount, NextGuid(), MakeLocation(x: 50f, y: 50f));
            summoner.Health.Current = 0;

            var ok = MuleSummonHandler.TrySummon(summoner, OwnerAccount, out var failReason);

            Assert.IsFalse(ok, "a dead summoner must be refused - Gem.UseGem's IsDead check never protected the /mule command path");
            Assert.AreEqual("You cannot summon your vault vendor while dead.", failReason);
        }

        /// <summary>
        /// A5. A summon creates a Creature, broadcasts its spawn to everyone in radar range, and
        /// destroys the previous one, so a macro is a spawn/despawn strobe for every bystander - yet
        /// /mule access and /mule log, which cost one shard read each, were the only commands with a
        /// cooldown. Asserted on the pure window arithmetic and on the stamping wrapper, both of which
        /// are reachable without a live Player.
        /// </summary>
        [TestMethod]
        public void TryStartSummon_RefusesASecondSummonInsideTheWindow()
        {
            MuleSummonHandler.ClearSummonCooldowns();

            var guid = NextGuid();
            var now = DateTime.UtcNow;

            Assert.IsTrue(MuleSummonHandler.TryStartSummon(guid, now, out var firstReason), firstReason);

            Assert.IsFalse(MuleSummonHandler.TryStartSummon(guid, now.AddSeconds(1), out var secondReason),
                "a second summon one second later must be refused");

            StringAssert.Contains(secondReason, "too recently");

            Assert.IsTrue(MuleSummonHandler.TryStartSummon(guid, now.AddSeconds(6), out var thirdReason), thirdReason);
        }

        /// <summary>
        /// A5: the window is PER PLAYER, not global. A shared window would let one player in a crowded
        /// Marketplace block everyone else's summons.
        /// </summary>
        [TestMethod]
        public void TryStartSummon_IsPerSummoner()
        {
            MuleSummonHandler.ClearSummonCooldowns();

            var now = DateTime.UtcNow;

            Assert.IsTrue(MuleSummonHandler.TryStartSummon(NextGuid(), now, out _));
            Assert.IsTrue(MuleSummonHandler.TryStartSummon(NextGuid(), now, out var otherReason), otherReason);
        }

        /// <summary>
        /// A5, placement: the cooldown must be stamped only for a summon that actually reaches the
        /// spawn, i.e. after the gate. Stamping before it would let a refusal (wrong landblock, no
        /// permission) burn a window that no bystander ever paid for.
        /// </summary>
        [TestMethod]
        public void CallSite_TrySummon_ChecksTheCooldownAfterTheGate()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "Entity", "AccountVault", "MuleSummonHandler.cs");
            var code = StripComments(File.ReadAllText(path));

            var iGate = code.IndexOf("if (!TryGate(actor, summoner.Location, store, out failReason))", StringComparison.Ordinal);
            var iCooldown = code.IndexOf("TryStartSummon(summoner.Guid.Full", StringComparison.Ordinal);
            var iCreate = code.IndexOf("WorldObjectFactory.CreateNewWorldObject(MuleVendorWcid)", StringComparison.Ordinal);

            Assert.IsTrue(iGate >= 0, "TrySummon must still run the denylist/authorization gate");
            Assert.IsTrue(iCooldown >= 0, "TrySummon must enforce a summon cooldown - the two cheap read commands have one and the expensive, world-visible one did not");
            Assert.IsTrue(iCreate >= 0, "TrySummon must still create the vendor");

            Assert.IsTrue(iCooldown > iGate && iCooldown < iCreate,
                "the cooldown must be checked after the gate (so a refusal does not burn the window) and before creation (so a refused summon spawns nothing)");
        }

        #endregion

        #region Call-site guards (fix round 2, F6)

        /// <summary>
        /// These prove only that the call site EXISTS - nothing about behavior. Gem.UseGem's dispatch
        /// to MuleSummonHandler.TryHandleUse is exercised behaviourally nowhere in this project (no
        /// test constructs a live Gem/Player interaction chain - see class remarks), and
        /// Player.LogOut_Inner's CurrentSummonedVendor.Destroy() call is exercised behaviourally nowhere
        /// either, for the same reason LogOut_Inner itself is never driven live in this project. Each
        /// test below was verified to fail when its call site is deleted (fix round 2, F6) - confirmed
        /// by temporarily deleting the line, rebuilding, and observing this test go red, then restoring.
        /// </summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        /// <summary>Mirrors AccountVaultPurgeTests.StripComments - removes block and line comments, leaving string literals alone.</summary>
        private static string StripComments(string source)
        {
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

            var result = new System.Text.StringBuilder();

            foreach (var line in withoutBlocks.Split('\n'))
            {
                result.Append(StripLineComment(line));
                result.Append('\n');
            }

            return result.ToString();
        }

        private static string StripLineComment(string line)
        {
            var inString = false;

            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                    inString = !inString;
                else if (!inString && i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                    return line.Substring(0, i);
            }

            return line;
        }

        [TestMethod]
        public void CallSite_UseGem_DispatchesToTheMuleHandler()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "WorldObjects", "Gem.cs");
            var code = StripComments(File.ReadAllText(path));

            StringAssert.Contains(code, "PropertyBool.MuleVendorContract",
                "the contract item's opt-in flag must be checked in Gem.UseGem");

            StringAssert.Contains(code, "MuleSummonHandler.TryHandleUse(this, player)",
                "the mule contract branch must actually dispatch to MuleSummonHandler.TryHandleUse - DESIGN 11.1");
        }

        /// <summary>
        /// Fix round 2, F5: /mule was the one command on this surface with no per-player cooldown.
        /// Grant, revoke, access and log have all called TryStartMuleVaultCommand since fix round 1;
        /// summon called nothing, so a NAMED summon ran AccountVaultManager.TryResolveCharacter - a
        /// synchronous ShardDbContext SELECT on the world thread - for every valid name given, and then
        /// TryGate ran a second SELECT for the grant list. Both run BEFORE
        /// MuleSummonHandler.TryStartSummon, which is deliberately stamped only after the gate so a
        /// refused summon does not burn the summon window; that ordering is precisely what left the
        /// query pair unbounded.
        ///
        /// The mechanism is the resolver call COUNT, not the refusal: a throttle that works by not
        /// reaching the database is only observable as a call that did not happen. Both edges are
        /// asserted, so the test cannot pass with the cooldown simply set to forever.
        /// </summary>
        [TestMethod]
        public void SummonByName_IsThrottled_AndTheThrottledCallNeverReachesTheCharacterResolver()
        {
            EnsureVendorConstructible();

            var summoner = MakeWiredSummoner(OwnerAccount, OwnerCharacter, MakeLocation());

            var resolverCalls = 0;

            bool Resolve(string characterName, out string ownerName, out uint accountId)
            {
                resolverCalls++;
                ownerName = "Vaultowner";
                accountId = OwnerAccount;
                return true;
            }

            var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsTrue(summoner.TrySummonMuleCore("Vaultowner", now, Resolve, out var resolvedAccount, out var resolvedName, out var firstReason), firstReason);
            Assert.AreEqual(OwnerAccount, resolvedAccount);
            Assert.AreEqual("Vaultowner", resolvedName);
            Assert.AreEqual(1, resolverCalls, "sanity: the first summon must actually resolve the name, or the second assertion below proves nothing");

            // One second later - inside the five second window the other four /mule commands share.
            Assert.IsFalse(summoner.TrySummonMuleCore("Vaultowner", now.AddSeconds(1), Resolve, out _, out _, out var throttledReason),
                "a second named summon inside the cooldown window must be refused");

            Assert.AreEqual("You have used a vault command too recently. Try again in a few seconds.", throttledReason);

            Assert.AreEqual(1, resolverCalls,
                "the throttled call must not reach the character resolver at all. Refusing AFTER the lookup would leave the shard query itself unbounded, which is the whole cost this cooldown exists to bound.");

            // And past the window it works again, so the throttle is a window rather than a one-shot.
            Assert.IsTrue(summoner.TrySummonMuleCore("Vaultowner", now.AddSeconds(6), Resolve, out _, out _, out var laterReason), laterReason);
            Assert.AreEqual(2, resolverCalls);
        }

        /// <summary>
        /// The bare form is throttled too. It resolves nothing, so it costs no shard query of its own -
        /// but leaving it out would make /mule with no argument the one spammable spelling, and it
        /// still reaches TryGate's grant read through TrySummon.
        /// </summary>
        [TestMethod]
        public void SummonOwn_IsThrottledToo_WithTheSameMessage()
        {
            EnsureVendorConstructible();

            var summoner = MakeWiredSummoner(OwnerAccount, OwnerCharacter, MakeLocation());

            bool NeverResolves(string characterName, out string ownerName, out uint accountId)
            {
                ownerName = null;
                accountId = 0;
                Assert.Fail("the bare form must never resolve a character name");
                return false;
            }

            var now = new DateTime(2026, 8, 29, 13, 0, 0, DateTimeKind.Utc);

            Assert.IsTrue(summoner.TrySummonMuleCore("", now, NeverResolves, out var accountId, out _, out var firstReason), firstReason);
            Assert.AreEqual(OwnerAccount, accountId, "the bare form summons the caller's own store");

            Assert.IsFalse(summoner.TrySummonMuleCore("", now.AddSeconds(1), NeverResolves, out _, out _, out var throttledReason));
            Assert.AreEqual("You have used a vault command too recently. Try again in a few seconds.", throttledReason);
        }

        /// <summary>
        /// The throttle lives in TrySummonMuleCore, so HandleActionSummonMule must route through it
        /// rather than keeping a resolve of its own. A source scan is the only thing that can pin this:
        /// no test in this project can drive HandleActionSummonMule itself, because its refusal path
        /// needs a live Session.
        /// </summary>
        [TestMethod]
        public void CallSite_HandleActionSummonMule_ResolvesOnlyThroughTheThrottledCore()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "WorldObjects", "Player_Mule_Vendor.cs");
            var code = StripComments(File.ReadAllText(path));

            var iMethod = code.IndexOf("public void HandleActionSummonMule(", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "HandleActionSummonMule was not found - has it been renamed? Update this test's anchor.");

            var iNext = code.IndexOf("internal delegate bool VaultCharacterResolver(", iMethod, StringComparison.Ordinal);
            Assert.IsTrue(iNext > iMethod, "the delegate declaration that ends HandleActionSummonMule's body was not found - update this test's anchors.");

            var body = code.Substring(iMethod, iNext - iMethod);

            StringAssert.Contains(body, "TrySummonMuleCore(",
                "HandleActionSummonMule must go through the throttled core - that is where the cooldown is");

            Assert.IsFalse(body.Contains("AccountVaultManager.TryResolveCharacter"),
                "the character lookup must NOT be reached directly from the handler: that is the shard query the cooldown exists to sit in front of, and a copy here would run before any throttle");
        }

        /// <summary>
        /// Fix round C, C1 (code review on a2677799e): a freshly summoned vendor's store is very often
        /// still running its async index load (summoning is what starts it - see IsLoaded's own remarks
        /// and ReadingIsLoaded_OnAColdStore_StartsTheIndexRead above), so the TryPrepareApproach call
        /// HandleActionMuleSearch makes right after a summon can legitimately fail. Before this fix that
        /// return value was discarded (`out _`), so LastRebuildShown/LastRebuildTotal stayed at their
        /// 0/0 default and the player was told "0 of 0 entries shown" for a vault that might hold
        /// hundreds of items - flatly contradicting the surrounding comment's own stated intent. No test
        /// in this project can drive HandleActionMuleSearch itself (its every path ends in
        /// Session.Network.EnqueueSend, which NREs on a bare-reflection Player - see class remarks), so
        /// this is a source scan, mirroring CallSite_HandleActionSummonMule_ResolvesOnlyThroughTheThrottledCore
        /// immediately above. PersonalVendorTests.TryPrepareApproach_OnAColdStore_RefusesWithStillLoadingMessage_AndNeverRebuilds
        /// is the companion mechanism-half test proving TryPrepareApproach really does fail this way.
        /// </summary>
        [TestMethod]
        public void CallSite_HandleActionMuleSearch_NeverDiscardsATryPrepareApproachFailure()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "WorldObjects", "Player_Mule_Vendor.cs");
            var code = StripComments(File.ReadAllText(path));

            var iMethod = code.IndexOf("public void HandleActionMuleSearch(", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "HandleActionMuleSearch was not found - has it been renamed? Update this test's anchor.");

            var iNext = code.IndexOf("private bool RefreshMuleVendorView(", iMethod, StringComparison.Ordinal);
            Assert.IsTrue(iNext > iMethod, "the method that ends HandleActionMuleSearch's body was not found - update this test's anchors.");

            var body = code.Substring(iMethod, iNext - iMethod);

            Assert.IsFalse(body.Contains("TryPrepareApproach(VaultActor.From(this), out _)"),
                "TryPrepareApproach's failReason must never be discarded here - a cold store's refusal must be told to the player instead of falling through to a false '0 of 0 entries shown' reply");

            Assert.IsFalse(body.Contains("RefreshMuleVendorView(vendor);"),
                "RefreshMuleVendorView must be called with its bool return checked (out failReason), never as a fire-and-forget void call, in either branch that goes on to report a count");
        }

        [TestMethod]
        public void CallSite_LogOutInner_DestroysTheSummonedVendor()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "WorldObjects", "Player.cs");
            var code = StripComments(File.ReadAllText(path));

            var iMethod = code.IndexOf("public void LogOut_Inner(", StringComparison.Ordinal);
            var iNextMethod = code.IndexOf("private void LogOut_Final(", StringComparison.Ordinal);

            Assert.IsTrue(iMethod >= 0 && iNextMethod > iMethod,
                "LogOut_Inner/LogOut_Final were not found by source scan - have they been renamed or reordered? Update this test's anchors.");

            var body = code.Substring(iMethod, iNextMethod - iMethod);

            StringAssert.Contains(body, "CurrentSummonedVendor.Destroy()",
                "DESIGN 11.4: the summoned vendor must be destroyed on logout, inside LogOut_Inner, the same as CurrentActivePet/SecondaryActivePet just above it");
        }

        #endregion

        #region Mule form appearance (Mule Form Token design section 6)

        private const uint FormDonorWcid = 90401;
        private const uint FormMuleSetup = 0x02000A0B;
        private const uint FormDonorSetup = 0x0200004E;
        private const uint FormUnmeasurableSetup = 0x02009999;

        private const uint FormAccount = 4444;

        /// <summary>
        /// Heights for the two setups the form tests use. Injected, so none of this needs a dat - and
        /// the unmeasurable id is what stands in for a donor whose model cannot be read.
        /// </summary>
        private static float FormSetupHeight(uint setupId)
        {
            if (setupId == FormMuleSetup)
                return 2.0f;

            if (setupId == FormDonorSetup)
                return 4.0f;

            return 0f;
        }

        private static Weenie MakeFormMuleWeenie()
        {
            var weenie = MakeVendorWeenie(true);

            weenie.WeenieClassId = 1003150;
            weenie.PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, FormMuleSetup } };
            weenie.PropertiesFloat[PropertyFloat.DefaultScale] = 0.5;

            return weenie;
        }

        private static Weenie MakeFormDonorWeenie(uint setupId = FormDonorSetup)
        {
            return new Weenie
            {
                WeenieClassId = FormDonorWcid,
                WeenieType = WeenieType.Creature,
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.Setup, setupId },
                    { PropertyDataId.MotionTable, 0x09000101 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Tusker Guard" } },
            };
        }

        private static Weenie ResolveForm(MuleFormLook? form, Weenie donor)
        {
            return MuleSummonHandler.ResolveVendorWeenie(
                MakeFormMuleWeenie(), form, wcid => wcid == FormDonorWcid ? donor : null, FormSetupHeight, FormAccount);
        }

        [TestMethod]
        public void ResolveVendorWeenie_WithNoSavedForm_ReturnsThePlainMuleWeenie()
        {
            var mule = MakeFormMuleWeenie();

            var resolved = MuleSummonHandler.ResolveVendorWeenie(mule, null, wcid => null, FormSetupHeight, FormAccount);

            Assert.AreSame(mule, resolved);
        }

        [TestMethod]
        public void ResolveVendorWeenie_WithTheTunableOff_ReturnsThePlainMuleWeenie()
        {
            PropertyManager.ModifyBool("mule_form_enabled", false);

            var mule = MakeFormMuleWeenie();

            var resolved = MuleSummonHandler.ResolveVendorWeenie(
                mule, new MuleFormLook(FormDonorWcid, "Tusker Guard"), wcid => MakeFormDonorWeenie(), FormSetupHeight, FormAccount);

            Assert.AreSame(resolved, mule, "the tunable gates the appearance substitution and nothing else");
        }

        [TestMethod]
        public void ResolveVendorWeenie_WithADonorMissingFromTheCache_FallsBackRatherThanRefusing()
        {
            var mule = MakeFormMuleWeenie();

            var resolved = MuleSummonHandler.ResolveVendorWeenie(
                mule, new MuleFormLook(999999, "Deleted Thing"), wcid => null, FormSetupHeight, FormAccount);

            Assert.AreSame(mule, resolved, "a stale look must never be able to refuse a summon");
        }

        [TestMethod]
        public void ResolveVendorWeenie_WithADonorWhoseSetupCannotBeMeasured_FallsBack()
        {
            var mule = MakeFormMuleWeenie();

            var resolved = MuleSummonHandler.ResolveVendorWeenie(
                mule, new MuleFormLook(FormDonorWcid, "Tusker Guard"),
                wcid => MakeFormDonorWeenie(FormUnmeasurableSetup), FormSetupHeight, FormAccount);

            Assert.AreSame(mule, resolved);
        }

        [TestMethod]
        public void ResolveVendorWeenie_WithAValidDonor_ReturnsTheClone()
        {
            var donor = MakeFormDonorWeenie();

            var resolved = ResolveForm(new MuleFormLook(FormDonorWcid, "Tusker Guard"), donor);

            Assert.IsNotNull(resolved);
            Assert.AreEqual(FormDonorSetup, resolved.PropertiesDID[PropertyDataId.Setup]);
            Assert.AreEqual(0x09000101u, resolved.PropertiesDID[PropertyDataId.MotionTable]);

            // Mule setup height 2.0 at its own DefaultScale 0.5 is a 1.0 target; the donor stands 4.0
            // tall, so it is scaled to a quarter. Nothing about the Lugian is hard-coded: both the
            // setup id and the 0.5 came off the mule weenie.
            Assert.AreEqual(0.25, resolved.PropertiesFloat[PropertyFloat.DefaultScale], 0.0001);

            Assert.IsTrue(resolved.PropertiesBool[PropertyBool.PersonalVendor],
                "bool 9049 must survive the clone or the factory returns a plain Vendor");
        }

        [TestMethod]
        public void ResolveVendorWeenie_WithANullMuleWeenie_ReturnsNull()
        {
            // The caller falls back to creating by wcid, which is what it did before this hook existed.
            Assert.IsNull(MuleSummonHandler.ResolveVendorWeenie(
                null, new MuleFormLook(FormDonorWcid, "Tusker Guard"), wcid => MakeFormDonorWeenie(), FormSetupHeight, FormAccount));
        }

        [TestMethod]
        public void ResolveVendorWeenie_NeverMutatesTheCachedMuleWeenie()
        {
            // GetCachedWeenie hands back the shared instance; a write here would give every future
            // mule on the shard this account's body.
            var mule = MakeFormMuleWeenie();

            MuleSummonHandler.ResolveVendorWeenie(
                mule, new MuleFormLook(FormDonorWcid, "Tusker Guard"), wcid => MakeFormDonorWeenie(), FormSetupHeight, FormAccount);

            Assert.AreEqual(FormMuleSetup, mule.PropertiesDID[PropertyDataId.Setup]);
            Assert.AreEqual(0.5, mule.PropertiesFloat[PropertyFloat.DefaultScale], 0.0001);
        }

        #endregion

        #region Vault line

        private const uint VaultLineAccount = 4711;

        /// <summary>
        /// Seeds the two config keys the store reads on this path with their SHIPPED defaults, so the
        /// values are cached rather than changed: PropertyManager.GetLong falls through to
        /// DatabaseManager.ShardConfig for an uncached key, which opens a ShardDbContext and NREs in a
        /// unit test. Seeding the default means nothing has to be restored afterwards, and the cap the
        /// assertions below quote is the one the shipped config actually produces.
        /// </summary>
        private static int SeedVaultConfigDefaults()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap",
                DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item));

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            return AccountVaultStore.BaseEntryCap;
        }

        private static Container SeedVaultLineVault(FakeVaultBackend backend, FakeVaultWorld world, int itemCount, int order)
        {
            var container = FakeVaultWorld.MakeContainer(itemsCapacity: 8);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = (uint)(9500 + order),
                AccountId = VaultLineAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(order),
            });

            for (var i = 0; i < itemCount; i++)
                Assert.IsTrue(container.TryAddToInventory(FakeVaultWorld.MakeStack(8000, 1, 100)), "could not seed a vault item");

            return container;
        }

        [TestMethod]
        public void DecideVaultLine_WhileAVaultIsStillLoading_DefersRatherThanQuotingZero()
        {
            var cap = SeedVaultConfigDefaults();

            var store = MakeStore(VaultLineAccount, out var backend, out var world);

            SeedVaultLineVault(backend, world, itemCount: 2, order: 1);
            var slow = SeedVaultLineVault(backend, world, itemCount: 3, order: 2);

            FakeVaultWorld.SetInventoryLoaded(slow, false);

            // The reported bug: every cold summon printed "Vault: 0 of 500 entries" over a vault full
            // of goods, because EntryCount answers 0 for a store that is not READY and that zero means
            // "unknown". Nothing may quote a number in this state.
            Assert.AreEqual(MuleSummonHandler.VaultLineAction.Defer,
                MuleSummonHandler.DecideVaultLine(store, out var message));

            Assert.IsNull(message);

            FakeVaultWorld.SetInventoryLoaded(slow, true);

            // Control: the same store, the same call, once the async inventory load has finished.
            Assert.AreEqual(MuleSummonHandler.VaultLineAction.Send,
                MuleSummonHandler.DecideVaultLine(store, out message));

            StringAssert.Contains(message, $"Vault: 5 of {cap} entries.", $"got: {message}");
        }

        [TestMethod]
        public void DecideVaultLine_WhenTheIndexReadFails_Drops()
        {
            SeedVaultConfigDefaults();

            var store = MakeStore(VaultLineAccount, out var backend, out var world);

            SeedVaultLineVault(backend, world, itemCount: 2, order: 1);

            // An index read that FAILS never resolves on its own, so waiting for it would burn the
            // whole poll budget to say nothing. IsLoaded cannot tell this apart from "still loading",
            // which is why the decision reads TryCheckReady.
            backend.FailVaultRead = true;

            Assert.AreEqual(MuleSummonHandler.VaultLineAction.Drop,
                MuleSummonHandler.DecideVaultLine(store, out var message));

            Assert.IsNull(message);
        }

        [TestMethod]
        public void DecideVaultLine_WithNoStore_Drops()
        {
            Assert.AreEqual(MuleSummonHandler.VaultLineAction.Drop,
                MuleSummonHandler.DecideVaultLine(null, out var message));

            Assert.IsNull(message);
        }

        [TestMethod]
        public void ComposeVaultLine_SpellsOutThatAStackIsOneEntry()
        {
            // DESIGN 7.3's collapse rule is invisible without this sentence: 9,000 healing kits read as
            // "1 of 500" here and would read as "9,000 of 500" under a unit count.
            var message = MuleSummonHandler.ComposeVaultLine(1, 500);

            StringAssert.Contains(message,
                "Vault: 1 of 500 entries. A whole stack of one item counts as a single entry, however many units it holds.",
                $"got: {message}");
        }

        #endregion
    }
}
