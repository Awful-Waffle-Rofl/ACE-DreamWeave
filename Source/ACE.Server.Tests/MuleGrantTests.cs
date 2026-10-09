using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor (Docs/MuleVendor/DESIGN.md section 13, and the audit half of section 10): the
    /// grant/revoke lifecycle - AccountVaultStore.TryGrantCore/TryRevokeCore, the unique-key upsert,
    /// the name-snapshot behavior, and the audit trail every one of the four actions writes.
    ///
    /// Driven the same way AccountVaultStoreTests drives deposit/withdraw: through
    /// IAccountVaultBackend/IAccountVaultWorldSource fakes and VaultActor, never a live Player - see
    /// that file's remarks and MuleSummonTests' class remarks for why (Player's constructors and
    /// static field initializers reach a live database).
    /// </summary>
    [TestClass]
    public class MuleGrantTests
    {
        /// <summary>
        /// Terse access read for assertions. The production-side collapsing GetAccess overload was
        /// DELETED by Task 12's reconciliation pass - three of five production sites had taken it and
        /// reported "you do not have permission" to players during a database outage - so this local
        /// helper exists to keep the assertions one-liners WITHOUT putting a bool-discarding wrapper
        /// back where production code could reach it. Any test that cares about the read-failure case
        /// must call TryGetAccess directly and assert on failReason; this helper cannot see it.
        /// </summary>
        private static VaultAccess AccessOf(AccountVaultStore store, Player player)
        {
            return store.TryGetAccess(VaultActor.From(player), out var access, out _) ? access : VaultAccess.None;
        }

        /// <summary>Same as the Player overload, for the sites that already hold a VaultActor.</summary>
        private static VaultAccess AccessOf(AccountVaultStore store, VaultActor actor)
        {
            return store.TryGetAccess(actor, out var access, out _) ? access : VaultAccess.None;
        }

        private const uint OwnerAccount = 5301;
        private const uint GranteeAccount = 5302;

        private const uint OwnerCharacter = 0x53000001;
        private const uint GranteeCharacter = 0x53000002;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Vaultowner");
        private static readonly VaultActor Grantee = new VaultActor(GranteeAccount, GranteeCharacter, "Grantee");

        [TestInitialize]
        public void Setup()
        {
            // Same seeding as AccountVaultStoreTests.Setup - CreateVault's ReservedVaultLocation and
            // the entry cap both fall through to a live shard DB read without these being cached.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000),
                "account_vault_entry_cap is missing from DefaultLongProperties");

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            VaultClassTestConfig.Seed();
        }

        private static AccountVaultStore MakeStore(out FakeVaultBackend backend, out FakeVaultWorld world)
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            return new AccountVaultStore(OwnerAccount, backend, world);
        }

        private static void SeedCharacter(FakeVaultWorld world, string name, uint guid, uint accountId)
        {
            world.Characters[name] = (guid, name, accountId);
        }

        private static bool TryGrant(AccountVaultStore store, string granteeName, bool canWithdraw, out string failReason)
        {
            return TryGrant(store, granteeName, canWithdraw, out _, out failReason);
        }

        /// <summary>
        /// Fix round 2, F6: also surfaces TryGrantCore's <c>changed</c> out-param, which is false when
        /// the grant already existed on identical terms and is what HandleActionMuleGrant uses to decide
        /// whether the grantee hears about it.
        /// </summary>
        private static bool TryGrant(AccountVaultStore store, string granteeName, bool canWithdraw, out bool changed, out string failReason)
        {
            var ok = false;
            var didChange = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryGrantCore(Owner, granteeName, canWithdraw, out _, out didChange, out reason));

            changed = didChange;
            failReason = reason;
            return ok;
        }

        private static bool TryRevoke(AccountVaultStore store, string granteeName, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryRevokeCore(Owner, granteeName, out reason));

            failReason = reason;
            return ok;
        }

        /// <summary>
        /// Fix round 1, F5/F7: same as <see cref="TryGrant(AccountVaultStore, string, bool, out string)"/>,
        /// but also surfaces the canonical name.
        ///
        /// Fix round 2, F6: that name now comes from TryGrantCore's own out-param rather than from a
        /// second resolution done here. The helper used to mirror the production TryGrant, which
        /// resolved the name a second time itself; both duplicate lookups are gone, so this now
        /// exercises the same plumbing production does instead of a parallel copy of it. The world fake
        /// is no longer a parameter, because nothing here reads it any more.
        /// </summary>
        private static bool TryGrantWithCanonicalName(AccountVaultStore store, string granteeName, bool canWithdraw, out string canonicalName, out string failReason)
        {
            var ok = false;
            string resolvedName = null;
            string reason = null;

            store.Enqueue(() => ok = store.TryGrantCore(Owner, granteeName, canWithdraw, out resolvedName, out _, out reason));

            canonicalName = ok ? resolvedName : null;
            failReason = reason;
            return ok;
        }

        private static bool Deposit(AccountVaultStore store, WorldObject item, VaultActor actor, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryDeposit(item, actor, out reason));

            failReason = reason;
            return ok;
        }

        private static bool Withdraw(AccountVaultStore store, VaultEntry entry, int amount, VaultActor actor, out List<WorldObject> withdrawn, out string failReason)
        {
            var ok = false;
            List<WorldObject> got = null;
            string reason = null;

            store.Enqueue(() => ok = store.TryWithdraw(entry, amount, actor, out got, out reason));

            withdrawn = got ?? new List<WorldObject>();
            failReason = reason;
            return ok;
        }

        [TestMethod]
        public void Grant_CreatesADepositOnlyRow()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(TryGrant(store, "Grantee", false, out var reason), reason);

            Assert.AreEqual(1, backend.Grants.Count);

            var grant = backend.Grants[0];
            Assert.AreEqual(OwnerAccount, grant.OwnerAccountId);
            Assert.AreEqual(GranteeCharacter, grant.GranteeCharacterGuid);
            Assert.IsFalse(grant.CanWithdraw);
        }

        [TestMethod]
        public void Grant_Twice_UpdatesRatherThanDuplicates()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(TryGrant(store, "Grantee", false, out _));
            Assert.IsTrue(TryGrant(store, "Grantee", true, out _));

            // F10: FakeVaultBackend.UpsertAccountVaultGrant implements the update-not-duplicate
            // semantics itself; this test proves only that TryGrantCore routes through that upsert
            // (rather than, say, always Add-ing a new row) and passes a stable guid across both calls.
            // The database's actual unique key is covered separately by AccountVaultSchemaTests.cs's
            // string scan.
            Assert.AreEqual(1, backend.Grants.Count,
                "a second grant to the same character must route through the same upsert and update the existing row rather than adding a second one");
            Assert.IsTrue(backend.Grants[0].CanWithdraw, "the second grant's canWithdraw must win");
        }

        /// <summary>
        /// Fix round 1, F7: the input and the canonical name deliberately DIFFER (the player types the
        /// wrong case) - granting to the exact seeded spelling would pass identically whether the row
        /// stored the resolved canonical name or the raw input, which is what made the pre-fix version
        /// of this test a tautology. Also covers F5's out-param.
        /// </summary>
        [TestMethod]
        public void Grant_SnapshotsTheGranteeName()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(TryGrantWithCanonicalName(store, "gRaNtEe", false, out var canonicalName, out var reason), reason);

            Assert.AreEqual("Grantee", backend.Grants[0].GranteeCharacterName,
                "the row stores the canonical name the world resolved, not the raw input");
            Assert.AreEqual("Grantee", canonicalName, "F5's out-param must equal the row's GranteeCharacterName");
        }

        [TestMethod]
        public void Revoke_RemovesTheRow()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(TryGrant(store, "Grantee", true, out _));
            Assert.IsTrue(TryRevoke(store, "Grantee", out var reason), reason);

            Assert.AreEqual(0, backend.Grants.Count);
        }

        /// <summary>
        /// DESIGN 16 step 6's stated verification, and the whole reason gate 2 is re-resolved on every
        /// transaction (DESIGN section 10): a grantee who already holds a reference to this exact store
        /// object - as if a vendor window had been opened before the revoke - must still be refused on
        /// the very next withdraw. There is no cached "already checked" state anywhere to invalidate.
        /// </summary>
        [TestMethod]
        public void Revoke_MidSession_BlocksTheNextTransaction()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(9302, 1, 100), Owner, out var depositReason), depositReason);
            Assert.IsTrue(TryGrant(store, "Grantee", true, out var grantReason), grantReason);

            // The grantee holds this exact store reference from before the revoke - GetAccess is
            // re-resolved on every call, so nothing here is a cached decision.
            Assert.AreEqual(VaultAccess.DepositWithdraw, AccessOf(store, Grantee), "sanity: access must be live before the revoke");

            Assert.IsTrue(TryRevoke(store, "Grantee", out var revokeReason), revokeReason);

            var entry = store.GetEntries(0, int.MaxValue).First(e => e.Kind == VaultEntryKind.StoredItem);

            var ok = Withdraw(store, entry, 0, Grantee, out _, out var withdrawFailReason);

            Assert.IsFalse(ok, "a revoke must block the very next transaction, with no session invalidation logic to run");

            // Fix round A, A7: the store used to collapse VaultAccess.None and VaultAccess.Deposit into
            // the withdraw wording. It no longer does, and a REVOKED grantee has None rather than
            // Deposit - they have no access to this vault at all, not merely no withdraw right. That
            // distinction is the point of the A7 split, so this assertion pins the None wording
            // deliberately; if it ever reads "withdraw from" again, the collapse has come back.
            Assert.AreEqual("You do not have permission to use this vault.", withdrawFailReason,
                "a revoked grantee has NO access, so the refusal must be the use-this-vault message, not the narrower withdraw one");
        }

        [TestMethod]
        public void Grant_ToYourself_IsRefusedWithAMessage()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Vaultowner", OwnerCharacter, OwnerAccount);

            var ok = TryGrant(store, "Vaultowner", false, out var reason);

            Assert.IsFalse(ok);
            Assert.IsFalse(string.IsNullOrEmpty(reason));
            Assert.AreEqual(0, backend.Grants.Count);
        }

        [TestMethod]
        public void Grant_ToAnUnknownCharacter_IsRefusedWithAMessage()
        {
            var store = MakeStore(out var backend, out _);

            var ok = TryGrant(store, "Nobody", false, out var reason);

            Assert.IsFalse(ok);
            Assert.IsFalse(string.IsNullOrEmpty(reason));
            Assert.AreEqual(0, backend.Grants.Count);
        }

        [TestMethod]
        public void Access_ListsGranteeNameAndLevel()
        {
            var store = MakeStore(out _, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(TryGrant(store, "Grantee", true, out _));

            var grants = store.GetGrants();

            Assert.IsNotNull(grants);
            Assert.AreEqual(1, grants.Count);
            Assert.AreEqual("Grantee", grants[0].GranteeCharacterName);
            Assert.IsTrue(grants[0].CanWithdraw);
        }

        /// <summary>
        /// DESIGN 13: "Deposits, withdrawals, grants and revocations are all logged with actor
        /// identity." Drives all four through this store, then checks each logged the ACTUAL actor -
        /// the withdraw is deliberately done by the grantee, not the owner, so a bug that always
        /// logged the owner would be caught.
        /// </summary>
        [TestMethod]
        public void EveryAction_WritesAnAuditRowNamingTheActor()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(9303, 1, 100), Owner, out var depositReason), depositReason);
            Assert.IsTrue(TryGrant(store, "Grantee", true, out var grantReason), grantReason);

            var entry = store.GetEntries(0, int.MaxValue).First(e => e.Kind == VaultEntryKind.StoredItem);
            Assert.IsTrue(Withdraw(store, entry, 0, Grantee, out _, out var withdrawReason), withdrawReason);

            Assert.IsTrue(TryRevoke(store, "Grantee", out var revokeReason), revokeReason);

            bool HasRow(AccountVaultAction action, uint actorGuid) =>
                backend.Logs.Any(l => l.Action == (int)action && l.ActorCharacterGuid == actorGuid);

            Assert.IsTrue(HasRow(AccountVaultAction.Deposit, OwnerCharacter), "Deposit must log the depositing actor");
            Assert.IsTrue(HasRow(AccountVaultAction.Withdraw, GranteeCharacter), "Withdraw must log the withdrawing actor, not the owner");
            Assert.IsTrue(HasRow(AccountVaultAction.Grant, OwnerCharacter), "Grant must log the owner as actor");
            Assert.IsTrue(HasRow(AccountVaultAction.Revoke, OwnerCharacter), "Revoke must log the owner as actor");
        }

        /// <summary>
        /// Grant/Revoke rows store the grantee name in ItemName (AccountVaultStore.WriteLog's
        /// itemName parameter). A SNAPSHOT, not a join: it must still read correctly after the
        /// character it named can no longer be resolved at all - the deleted-character case DESIGN 13
        /// exists to keep answerable.
        /// </summary>
        [TestMethod]
        public void AuditRow_SurvivesTheGranteeBeingRenamed()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(TryGrant(store, "Grantee", false, out _));

            var grantLog = backend.Logs.Single(l => l.Action == (int)AccountVaultAction.Grant);

            // Fix round 1, F7: this is the real assertion - it fails if WriteLog's itemName argument at
            // AccountVaultStore.cs's TryGrantCore ever stops passing the resolved canonical name. The
            // Remove below only simulates the character becoming unresolvable and asserts nothing new:
            // grantLog is a live object reference already held by this test, and nothing re-reads
            // world.Characters for an existing row, so no production change could make a second assert
            // after the Remove fail differently from this one.
            Assert.AreEqual("Grantee", grantLog.ItemName);

            world.Characters.Remove("Grantee");
        }

        // ---------------------------------------------------------------- F3: null-vs-empty on reads

        /// <summary>
        /// Fix round 1, F3: AccountVaultStore.GetGrants must forward the DAO's null (a read FAILURE)
        /// rather than swallowing it into an empty list - /mule access renders an empty list as "you
        /// have shared with nobody", which would misreport a database outage as a fact about the
        /// player's vault during exactly the theft investigation DESIGN 13's closing paragraph exists
        /// for. FailGrantRead was already wired to the ACCESS path (TryGetAccess) before this fix; this
        /// is the first test to set it and assert on GetGrants's own return.
        /// </summary>
        [TestMethod]
        public void GetGrants_OnAReadFailure_ReturnsNullNotAnEmptyList()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(TryGrant(store, "Grantee", false, out _));

            backend.FailGrantRead = true;

            Assert.IsNull(store.GetGrants(), "a failed grant read must be null, never an empty list - /mule access renders empty as 'you have shared with nobody'");
        }

        /// <summary>
        /// Fix round 1, F3: same claim as GetGrants_OnAReadFailure_ReturnsNullNotAnEmptyList, for
        /// GetRecentLog. Before this fix, FailLogRead was declared and used in the log-reading fake but
        /// set by no test anywhere in the repo, and GetRecentLog appeared in no test file at all.
        /// </summary>
        [TestMethod]
        public void GetRecentLog_OnAReadFailure_ReturnsNullNotAnEmptyList()
        {
            var store = MakeStore(out var backend, out _);

            backend.FailLogRead = true;

            Assert.IsNull(store.GetRecentLog(20), "a failed log read must be null, never an empty list - /mule log renders empty as 'There is no recent activity on your vault.'");
        }

        // ---------------------------------------------------------------- F4: log detail formatting

        /// <summary>
        /// Fix round 1, F4: Player.FormatLogDetail, extracted from HandleActionMuleLog so it is
        /// testable without a Player. A Grant row's Count carries the withdraw flag (WriteLog at
        /// AccountVaultStore.cs), not a unit count, and must render as a permission level.
        /// </summary>
        [TestMethod]
        public void FormatLogDetail_ForGrant_ShowsThePermissionLevel()
        {
            Assert.AreEqual("Bob (deposit only)", Player.FormatLogDetail(AccountVaultAction.Grant, "Bob", 0));
            Assert.AreEqual("Bob (deposit and withdraw)", Player.FormatLogDetail(AccountVaultAction.Grant, "Bob", 1));
        }

        /// <summary>A Revoke row's Count is always 0 and carries no unit-count meaning at all.</summary>
        [TestMethod]
        public void FormatLogDetail_ForRevoke_ShowsOnlyTheName()
        {
            Assert.AreEqual("Bob", Player.FormatLogDetail(AccountVaultAction.Revoke, "Bob", 0));
        }

        /// <summary>Deposit/Withdraw rows keep the original "x{count}" unit-count rendering.</summary>
        [TestMethod]
        public void FormatLogDetail_ForDepositAndWithdraw_ShowsTheUnitCount()
        {
            Assert.AreEqual("Sword x1", Player.FormatLogDetail(AccountVaultAction.Deposit, "Sword", 1));
            Assert.AreEqual("Healing Kit x9001", Player.FormatLogDetail(AccountVaultAction.Withdraw, "Healing Kit", 9001));
        }

        // ------------------------------------------------------------- F6: command cooldown window

        /// <summary>
        /// Fix round 1, F6: the pure window-arithmetic half of the /mule vault command cooldown, shared
        /// by /mule access and /mule log from round 1 and by /mule grant and /mule revoke from round 2.
        /// The player-facing wrapper (Player.TryStartMuleVaultCommand) is not unit-testable
        /// through a live Player for the same reason nothing else in this file constructs one; this is
        /// the part that is.
        /// </summary>
        [TestMethod]
        public void MuleVaultReadCooldown_BlocksWithinTheWindow()
        {
            var previous = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var now = previous.AddSeconds(4);

            Assert.IsTrue(Player.IsMuleVaultReadOnCooldown(previous, now, 5));
        }

        [TestMethod]
        public void MuleVaultReadCooldown_AllowsAfterTheWindow()
        {
            var previous = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var now = previous.AddSeconds(5.1);

            Assert.IsFalse(Player.IsMuleVaultReadOnCooldown(previous, now, 5));
        }

        // ------------------------------------------- Fix round 2, F6: grant/revoke throttle and quiet

        [TestMethod]
        public void RepeatGrantWithIdenticalTerms_WritesNothingAndNotifiesNobody()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(TryGrant(store, "Grantee", canWithdraw: false, out var changed, out _));

            // The rate limit must not silence the ORDINARY case: a first grant to a player who has
            // never been granted before is exactly the message the notification exists to send.
            Assert.IsTrue(changed, "a first grant changed something and must be free to notify the grantee");

            var writesAfterFirst = backend.UpsertGrantCalls;
            var logsAfterFirst = backend.AddLogCalls;

            Assert.IsTrue(TryGrant(store, "Grantee", canWithdraw: false, out changed, out _),
                "a repeat grant with identical terms must still report success - it is idempotent, not an error.");

            Assert.IsFalse(changed, "a repeat grant on identical terms changed nothing, which is what suppresses the grantee's chat line");

            Assert.AreEqual(writesAfterFirst, backend.UpsertGrantCalls,
                "a repeat grant with identical terms must write nothing. The backend is an UPSERT with no already-granted refusal, so an uncooldowned macro was four blocking shard operations and one permanent audit row per iteration.");
            Assert.AreEqual(logsAfterFirst, backend.AddLogCalls,
                "and it must not append an audit row either.");
        }

        [TestMethod]
        public void CallSite_GrantAndRevoke_GoThroughTheSharedCommandThrottle()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/Player_Mule_Vendor.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            foreach (var handler in new[] { "HandleActionMuleGrant", "HandleActionMuleRevoke", "HandleActionMuleAccess", "HandleActionMuleLog" })
            {
                var iMethod = code.IndexOf($"public void {handler}(", StringComparison.Ordinal);
                Assert.IsTrue(iMethod >= 0, $"{handler} was not found.");

                var iBody = code.IndexOf('{', iMethod);
                var window = code.Substring(iBody, System.Math.Min(600, code.Length - iBody));

                Assert.IsTrue(window.Contains("TryStartMuleVaultCommand()"),
                    $"{handler} must open with the shared throttle. Grant and revoke are more expensive than the two read commands the cooldown already guarded: grant costs a character resolution plus a grant-list read and, whenever anything actually changes, a write and a permanent audit row; revoke costs two grant-list reads plus a delete and an audit row. This same task removed grant's SECOND character resolution and made a repeat on identical terms write nothing at all, so the per-iteration cost of a macro is lower than it was - but it is still a shard round trip per invocation, and nothing but this throttle caps how often it can be paid.");
            }
        }

        // ------------------------------------------------ /mule search all | mine | <owner> (Player_Mule_Search.cs)

        [TestMethod]
        public void Grant_StampsTheGrantingCharacter()
        {
            var store = MakeStore(out var backend, out var world);
            SeedCharacter(world, "Grantee", GranteeCharacter, GranteeAccount);

            Assert.IsTrue(TryGrant(store, "Grantee", false, out var failReason), failReason);

            var row = backend.Grants.Single();
            Assert.AreEqual(OwnerCharacter, row.GrantedByCharacterGuid);
            Assert.AreEqual("Vaultowner", row.GrantedByCharacterName);
        }

        [TestMethod]
        public void GetAccountVaultGrantsForGrantee_OnAReadFailure_ReturnsNullNotAnEmptyList()
        {
            var backend = new FakeVaultBackend { FailGrantRead = true };

            Assert.IsNull(backend.GetAccountVaultGrantsForGrantee(GranteeCharacter));
        }

        private const uint AltCharacter = 0x53000009;

        /// <summary>A resolver over a fixed name table, counting its calls (each is a shard read in production).</summary>
        private sealed class SearchWorld
        {
            public readonly Dictionary<string, (string name, uint account)> Names = new Dictionary<string, (string, uint)>(StringComparer.OrdinalIgnoreCase);
            public int Stamps;
            public int Resolves;
            public int AccessChecks;
            public string CooldownRefusal;
            public Func<uint, (bool ok, VaultAccess access)> Access = _ => (true, VaultAccess.None);

            public Player.MuleSearchTarget Resolve(uint callerAccount, params string[] tokens)
            {
                Player.VaultCharacterResolver resolver = (string n, out string owner, out uint account) =>
                {
                    Resolves++;
                    owner = null;
                    account = 0;

                    if (!Names.TryGetValue(n, out var found))
                        return false;

                    owner = found.name;
                    account = found.account;
                    return true;
                };

                Player.MuleSearchAccessCheck access = (uint account, out VaultAccess level, out string failReason) =>
                {
                    AccessChecks++;
                    var (ok, a) = Access(account);
                    level = a;
                    failReason = ok ? null : AccountVaultStore.UnavailableMessage;
                    return ok;
                };

                return Player.TryResolveSearchTarget(tokens, callerAccount, n => Names.ContainsKey(n),
                    () => { Stamps++; return CooldownRefusal; }, resolver, access);
            }
        }

        [TestMethod]
        public void SearchTarget_ASharedOwner_OpensThatMule_WithTheRestAsText()
        {
            var w = new SearchWorld();
            w.Names["vaultowner"] = ("Vaultowner", OwnerAccount);
            w.Access = a => (true, a == OwnerAccount ? VaultAccess.Deposit : VaultAccess.None);

            var target = w.Resolve(GranteeAccount, "vaultowner", "ancient", "sword");

            Assert.AreEqual(Player.MuleSearchTargetKind.Shared, target.Kind);
            Assert.AreEqual(OwnerAccount, target.AccountId);
            Assert.AreEqual("Vaultowner", target.OwnerName, "the canonical name, never the typed spelling");
            Assert.AreEqual("ancient sword", target.Pattern);
            Assert.AreEqual(1, w.Stamps, "the cooldown is stamped exactly once");
            Assert.IsTrue(target.CooldownStamped);
        }

        /// <summary>
        /// Access is per CHARACTER. The owner shared with the caller's ALT, not with the caller, so the
        /// caller may not open that mule by name - the whole string is search text instead. Driven
        /// through a real store and the one access authority, TryGetAccess.
        /// </summary>
        [TestMethod]
        public void SearchTarget_AGrantToMyAlt_DoesNotCount()
        {
            var store = MakeStore(out var backend, out var world);
            backend.Grants.Add(new ACE.Database.Models.Shard.AccountVaultGrant
            {
                OwnerAccountId = OwnerAccount, GranteeCharacterGuid = AltCharacter, GranteeCharacterName = "Alt", CanWithdraw = true,
            });

            var w = new SearchWorld();
            w.Names["Vaultowner"] = ("Vaultowner", OwnerAccount);
            w.Access = a => store.TryGetAccess(Grantee, out var level, out _) ? (true, level) : (false, VaultAccess.None);

            var target = w.Resolve(GranteeAccount, "Vaultowner", "sword");

            Assert.AreEqual(Player.MuleSearchTargetKind.Text, target.Kind);
            Assert.AreEqual("Vaultowner sword", target.Pattern, "a mule the caller cannot open leaves the WHOLE string as text");
            Assert.IsTrue(target.CooldownStamped, "the fall-through must know the stamp is already spent");
        }

        [TestMethod]
        public void SearchTarget_MyOwnCharacterName_IsMyOwnMule_WithNoAccessRead()
        {
            var w = new SearchWorld();
            w.Names["Myalt"] = ("Myalt", GranteeAccount);

            var target = w.Resolve(GranteeAccount, "Myalt", "sword");

            Assert.AreEqual(Player.MuleSearchTargetKind.Own, target.Kind);
            Assert.AreEqual(GranteeAccount, target.AccountId);
            Assert.AreEqual("sword", target.Pattern);
            Assert.AreEqual(0, w.AccessChecks, "the owner short-circuit needs no grant read");
        }

        [TestMethod]
        public void SearchTarget_ALoneToken_IsAlwaysText_EvenWhenItIsAName()
        {
            var w = new SearchWorld();
            w.Names["Vaultowner"] = ("Vaultowner", OwnerAccount);
            w.Access = _ => (true, VaultAccess.DepositWithdraw);

            var target = w.Resolve(GranteeAccount, "Vaultowner");

            Assert.AreEqual(Player.MuleSearchTargetKind.Text, target.Kind);
            Assert.AreEqual("Vaultowner", target.Pattern);
            Assert.AreEqual(0, w.Stamps);
            Assert.AreEqual(0, w.Resolves);
        }

        /// <summary>An unknown first token is text with NO shard read and NO cooldown stamp: plain /mule search is unchanged.</summary>
        [TestMethod]
        public void SearchTarget_AnUnknownFirstToken_IsTextWithNoDbAccessAndNoStamp()
        {
            var w = new SearchWorld();

            var target = w.Resolve(GranteeAccount, "legendary", "coord|legendary", "quick");

            Assert.AreEqual(Player.MuleSearchTargetKind.Text, target.Kind);
            Assert.AreEqual("legendary coord|legendary quick", target.Pattern);
            Assert.IsFalse(target.CooldownStamped);
            Assert.AreEqual(0, w.Stamps);
            Assert.AreEqual(0, w.Resolves);
            Assert.AreEqual(0, w.AccessChecks);
        }

        [TestMethod]
        public void SearchTarget_OnCooldown_IsRefusedBeforeAnyShardRead()
        {
            var w = new SearchWorld { CooldownRefusal = "You have used a vault command too recently. Try again in a few seconds." };
            w.Names["Vaultowner"] = ("Vaultowner", OwnerAccount);

            var target = w.Resolve(GranteeAccount, "Vaultowner", "sword");

            Assert.AreEqual(Player.MuleSearchTargetKind.Refused, target.Kind);
            Assert.AreEqual(w.CooldownRefusal, target.FailReason);
            Assert.AreEqual(0, w.Resolves);
        }

        [TestMethod]
        public void SearchTarget_AFailedGrantRead_IsRefusedAsUnavailable_NotSearchedAsText()
        {
            var w = new SearchWorld();
            w.Names["Vaultowner"] = ("Vaultowner", OwnerAccount);
            w.Access = _ => (false, VaultAccess.None);

            var target = w.Resolve(GranteeAccount, "Vaultowner", "sword");

            Assert.AreEqual(Player.MuleSearchTargetKind.Refused, target.Kind);
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, target.FailReason);
        }

        private static VaultScanResult ScanOf(int total, params (string name, long count)[] hits)
        {
            var scan = new VaultScanResult { Total = total };

            foreach (var (name, count) in hits)
                scan.Hits.Add(new VaultSearchHit(name, count));

            return scan;
        }

        private static (string, long)[] Hits(string prefix, int n) => Enumerable.Range(1, n).Select(i => ($"{prefix} {i}", 1L)).ToArray();

        [TestMethod]
        public void Report_OwnMuleFirst_ThenSharedAlphabeticallyByLabel()
        {
            var lines = Player.FormatSearchAllReport("sword", new[]
            {
                new Player.MuleSearchReportEntry { GranterName = "Zed", Scan = ScanOf(1, ("Zed Sword", 1)) },
                new Player.MuleSearchReportEntry { IsOwn = true, Scan = ScanOf(1, ("My Sword", 3)) },
                new Player.MuleSearchReportEntry { GranterName = "Abe", Scan = ScanOf(1, ("Abe Sword", 1)) },
            }, false, false, false);

            CollectionAssert.AreEqual(new[]
            {
                "Your mule: 1 match(es)",
                "  My Sword x3",
                "Abe's mule: 1 match(es)",
                "  Abe Sword",
                "Zed's mule: 1 match(es)",
                "  Zed Sword",
                "3 match(es) in 3 mule(s).",
            }, lines);
        }

        [TestMethod]
        public void Report_PerMuleCap_PrintsFiveThenPlusMoreWithTheCommandToNarrow()
        {
            var lines = Player.FormatSearchAllReport("sword", new[]
            {
                new Player.MuleSearchReportEntry { IsOwn = true, Scan = ScanOf(8, Hits("S", 5)) },
                new Player.MuleSearchReportEntry { GranterName = "+Testing thing", Scan = ScanOf(7, Hits("T", 5)) },
            }, false, false, false);

            Assert.AreEqual(5, lines.Count(l => l.StartsWith("  S ")));
            CollectionAssert.Contains(lines, "  +3 more - /mule search mine sword");
            CollectionAssert.Contains(lines, "  +2 more - /mule search \"+Testing thing\" sword", "a spaced owner name is quoted so the hint can be typed back");
            CollectionAssert.Contains(lines, "15 match(es) in 2 mule(s).");
        }

        [TestMethod]
        public void Report_TotalCap_StopsAtTwentyFiveHitLines_AndSaysSo()
        {
            var mules = new List<Player.MuleSearchReportEntry> { new Player.MuleSearchReportEntry { IsOwn = true, Scan = ScanOf(5, Hits("Own", 5)) } };

            foreach (var name in new[] { "Aa", "Bb", "Cc", "Dd", "Ee", "Ff" })
                mules.Add(new Player.MuleSearchReportEntry { GranterName = name, Scan = ScanOf(5, Hits(name, 5)) });

            var lines = Player.FormatSearchAllReport("x", mules, false, false, false);

            var hitLines = lines.Count(l => l.StartsWith("  ") && !l.StartsWith("  +"));
            Assert.AreEqual(Player.SearchAllMaxHitsTotal, hitLines);
            CollectionAssert.Contains(lines, "Showing the first 25 matches - narrow your pattern or search one mule.");
            CollectionAssert.Contains(lines, "  +5 more - /mule search Ee x", "a mule past the total cap still reports its count");
            CollectionAssert.Contains(lines, "35 match(es) in 7 mule(s).");
        }

        [TestMethod]
        public void Report_LoadingAndUnavailableLines()
        {
            var lines = Player.FormatSearchAllReport("sword", new[]
            {
                new Player.MuleSearchReportEntry { IsOwn = true, Loading = true },
            }, true, false, false);

            CollectionAssert.AreEqual(new[]
            {
                "Shared mules are unavailable right now - showing your mule only.",
                "Your mule: still loading - try again in a moment.",
            }, lines, "nothing was searched, so there is no totals line claiming zero matches");
        }

        [TestMethod]
        public void Report_NoMatches_NamesThePatternAndTheMuleCount()
        {
            var lines = Player.FormatSearchAllReport("sword", new[]
            {
                new Player.MuleSearchReportEntry { IsOwn = true, Scan = ScanOf(0) },
                new Player.MuleSearchReportEntry { GranterName = "Abe", Loading = true },
            }, false, false, false);

            CollectionAssert.AreEqual(new[]
            {
                "Abe's mule: still loading - try again in a moment.",
                "No matches for \"sword\" in 1 mule(s).",
            }, lines);
        }

        [TestMethod]
        public void Report_BudgetAndMuleCapNotes()
        {
            var lines = Player.FormatSearchAllReport("sword", new[]
            {
                new Player.MuleSearchReportEntry { IsOwn = true, Scan = ScanOf(1, ("A", 1)) },
                new Player.MuleSearchReportEntry { GranterName = "Abe", Skipped = true },
            }, false, true, true);

            CollectionAssert.Contains(lines, "Search stopped early - try a narrower pattern.");
            CollectionAssert.Contains(lines, "Searched the first 10 shared mules only.");
            Assert.IsFalse(lines.Any(l => l.StartsWith("Abe")), "a mule the budget never reached is not listed");
            CollectionAssert.Contains(lines, "1 match(es) in 1 mule(s).");
        }

        [TestMethod]
        public void Report_FallbackLabels_AreNumbered_AndGetNoCommandHint()
        {
            var lines = Player.FormatSearchAllReport("x", new[]
            {
                new Player.MuleSearchReportEntry { Scan = ScanOf(6, Hits("P", 5)) },
                new Player.MuleSearchReportEntry { Scan = ScanOf(1, ("Q", 1)) },
            }, false, false, false);

            CollectionAssert.Contains(lines, "A shared mule: 6 match(es)");
            CollectionAssert.Contains(lines, "A shared mule (2): 1 match(es)");
            CollectionAssert.Contains(lines, "  +1 more", "with no granting character there is no owner name to type back");
        }

        [TestMethod]
        public void GranterName_PrefersTheCurrentName_ThenTheStoredOne_ThenNothing()
        {
            Assert.AreEqual("Newname", Player.ChooseGranterName("Newname", "Oldname"));
            Assert.AreEqual("Oldname", Player.ChooseGranterName(null, "Oldname"));
            Assert.IsNull(Player.ChooseGranterName(null, null));
            Assert.IsNull(Player.ChooseGranterName(null, "<unknown>"), "WriteLog's placeholder is not a name");
        }

        /// <summary>
        /// The double-stamp trap, pinned by source scan (no test here can drive a live Player): the
        /// search-all and mine handlers stamp the shared cooldown once, and the filtered-summon helper
        /// summons through TrySummon directly - TrySummonMuleCore would stamp a second time and refuse.
        /// </summary>
        [TestMethod]
        public void CallSite_MuleSearchForms_StampOnce_AndNeverSummonThroughTheStampingCore()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/Player_Mule_Search.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            foreach (var handler in new[] { "public void HandleActionMuleSearchAll(", "public void HandleActionMuleSearchMine(" })
            {
                var i = code.IndexOf(handler, StringComparison.Ordinal);
                Assert.IsTrue(i >= 0, $"{handler} was not found.");

                var window = code.Substring(i, Math.Min(900, code.Length - i));
                StringAssert.Contains(window, "TryStartMuleVaultCommandCore(", $"{handler} must stamp the shared /mule cooldown");
            }

            Assert.IsFalse(code.Contains("TrySummonMuleCore("), "Player_Mule_Search.cs must summon through MuleSummonHandler.TrySummon, never the stamping core");
            StringAssert.Contains(code, "MuleSummonHandler.TrySummon(");
        }

        /// <summary>
        /// Same walk-up idiom as PropertyRegistryTests.cs's FindInSourceTree (kept private to that file,
        /// so this is a local copy rather than a cross-test-file dependency).
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
