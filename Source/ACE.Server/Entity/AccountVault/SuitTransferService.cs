using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common.Extensions;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    internal enum SuitTransferState
    {
        Queued,
        Running,
        Completed,
        Rejected,
    }

    internal enum SuitTransferOutcome
    {
        /// <summary>Not attempted yet.</summary>
        Pending,

        /// <summary>Every unit of the line is in the character's pack.</summary>
        Delivered,

        /// <summary>A unit was withdrawn, could not be delivered, and is back in the vault.</summary>
        Returned,

        /// <summary>Nothing (more) moved for the line: refused before or by the vault, nothing withdrawn.</summary>
        Refused,

        /// <summary>A unit was withdrawn, could not be delivered, and the vault refused it back. Manual recovery.</summary>
        Stranded,

        /// <summary>The vault's state is unknown after a throw. Manual reconciliation; never retried.</summary>
        Threw,
    }

    /// <summary>
    /// One requested line, as posted: a stored item by guid, a class line by key, or a ledger row by wcid - in the
    /// caller's OWN vault, or, with <see cref="SharedGrantId"/> set (the posted vault_key "s:&lt;grant id&gt;"), in a
    /// mule shared with the transfer's character.
    /// </summary>
    internal sealed class SuitTransferLine
    {
        public SuitTransferLine(uint? itemGuid, uint wcid, string classKey, int count, uint? sharedGrantId = null, uint? holderGuid = null)
        {
            ItemGuid = itemGuid;
            Wcid = wcid;
            ClassKey = classKey;
            Count = count;
            SharedGrantId = sharedGrantId;
            HolderGuid = holderGuid;
        }

        /// <summary>
        /// The posted holder_guid: another character on the caller's account whose PACK holds <see cref="ItemGuid"/>.
        /// Null for a vault line. Mutually exclusive with a vault_key; needs item_guid, count 1 and no class_key.
        /// </summary>
        public uint? HolderGuid { get; }

        public uint? ItemGuid { get; }
        public uint Wcid { get; }
        public string ClassKey { get; }
        public int Count { get; }

        /// <summary>The AccountVaultGrant.Id a shared line names; null for the caller's own vault. Re-resolved at Begin, never trusted.</summary>
        public uint? SharedGrantId { get; }

        /// <summary>The wire vault_key: null for the own vault, else "s:&lt;grant id&gt;".</summary>
        public string VaultKey => SharedGrantId == null ? null : MarketManager.SharedVaultKey(SharedGrantId.Value);
    }

    internal sealed class SuitTransferRequest
    {
        public SuitTransferRequest(uint accountId, uint characterGuid, string idempotencyKey, IReadOnlyList<SuitTransferLine> items, bool allowListed)
        {
            AccountId = accountId;
            CharacterGuid = characterGuid;
            IdempotencyKey = idempotencyKey;
            Items = items;
            AllowListed = allowListed;
        }

        public uint AccountId { get; }
        public uint CharacterGuid { get; }
        public string IdempotencyKey { get; }
        public IReadOnlyList<SuitTransferLine> Items { get; }
        public bool AllowListed { get; }
    }

    internal readonly struct SuitTransferSubmitResult
    {
        public SuitTransferSubmitResult(MarketError error, string transferId)
        {
            Error = error;
            TransferId = transferId;
        }

        public MarketError Error { get; }
        public string TransferId { get; }
    }

    internal sealed class SuitTransferItemStatus
    {
        /// <summary>The line's vault_key exactly as posted (null for the own vault).</summary>
        public string VaultKey { get; init; }

        /// <summary>The line's holder_guid exactly as posted (null for a vault line).</summary>
        public uint? HolderGuid { get; init; }

        public uint? ItemGuid { get; init; }
        public uint Wcid { get; init; }
        public string ClassKey { get; init; }
        public int Count { get; init; }
        public SuitTransferOutcome Outcome { get; init; }
        public string Message { get; init; }
    }

    internal sealed class SuitTransferStatus
    {
        public string TransferId { get; init; }
        public SuitTransferState State { get; init; }

        /// <summary>None unless the transfer was rejected, or stopped early by a re-validation.</summary>
        public MarketError Reason { get; init; }

        public IReadOnlyList<SuitTransferItemStatus> Items { get; init; }
    }

    /// <summary>
    /// "Move this suit from my vault to my character" (the suit builder's POST /v1/accounts/me/suit/transfers):
    /// a batched, idempotent transfer that reports an outcome per requested line, and moves every object through
    /// the shared <see cref="VaultPackDelivery"/> core, exactly as the facet restore does.
    ///
    /// STATE MACHINE. <see cref="Submit"/> (request thread) validates, finds the online character, enforces one
    /// active transfer per account, records the job as Queued and enqueues <see cref="Begin"/> on the world's
    /// inbound queue. Begin flips Queued to Running, RE-VALIDATES (same session, same Player, not logging out),
    /// runs the character gates, waits out a cold vault (bounded), resolves every line, and refuses the WHOLE
    /// job (Rejected, nothing withdrawn) when the main pack lacks the slots or the character the burden for
    /// everything that is going to move. Otherwise it schedules <see cref="Step"/> on the character's action
    /// queue: ONE single-object withdraw per step, re-validating before each, so a 24-object transfer costs 24
    /// small actions rather than one long one. When the units run out, or a re-validation fails mid-run, the
    /// job is Completed with every line's outcome; delivered objects stay delivered. A queued job the world
    /// never starts, or a running job that stops making progress (its character left the world and its action
    /// chain with it), is closed by <see cref="ExpireLocked"/> so the account is never wedged.
    ///
    /// ONE OBJECT PER WITHDRAW, ALWAYS. A ledger row or class line of count N is N withdraws of amount 1; a
    /// stored item is withdrawn whole; a member of a stored GROUP is withdrawn by its own guid
    /// (VaultEntry.ForItem), never through the group row, whose withdraw takes members from the front of the
    /// bucket rather than the one asked for. If the core still reports extra objects, the line is stopped and
    /// they are reported.
    ///
    /// NEVER A RETRY. A line stops at its first unit that is not delivered; in particular a Threw unit is never
    /// attempted again (VaultPackDelivery.TryReturnWithdrawn's double-credit remarks).
    ///
    /// NEVER ON THE STORE'S MUTATION QUEUE. Begin runs from the inbound queue and Step from the character's
    /// action queue; neither is work enqueued on AccountVaultStore, so WithdrawToPack's throw handling holds.
    ///
    /// In memory only: a restart forgets every transfer and every idempotency key.
    ///
    /// SHARED MULES. A line with a vault_key names a mule shared with the job's character. Job.AccountId stays the
    /// CALLER's account for everything caller-scoped (idempotency, single flight, status ownership, the online
    /// lookup, the logs); everything store-scoped (GetStore, ListedCount, BeforeGroupMemberWithdraw) uses the
    /// line's own StoreAccountId, which Begin sets to the grant owner only after re-reading the grant (it must
    /// still exist, name this character, and not be the caller's own account). Plan then asks the owner's store
    /// for DepositWithdraw access once; each unit relies on TryWithdraw's own re-check, so a revoke or downgrade
    /// mid-run stops that line with delivered units left delivered, and an undeliverable unit goes back to the
    /// OWNER's store (the store the withdraw came from). A shared line never moves a listed item: AllowListed is
    /// ignored for it in both the plan and every step.
    /// </summary>
    internal sealed partial class SuitTransferService
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Most lines, and most objects summed across the lines, one transfer may ask for.</summary>
        public const int MaxObjects = 24;

        public const int MaxIdempotencyKeyLength = 64;

        /// <summary>How long a finished transfer (and its idempotency key) stays readable and replayable.</summary>
        public static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

        /// <summary>A queued transfer the world has not started within this is closed as server_error.</summary>
        public static readonly TimeSpan QueuedTimeout = TimeSpan.FromSeconds(30);

        /// <summary>A running transfer with no progress for this long is closed; its remaining lines are refused.</summary>
        public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

        internal const double StepDelaySeconds = 0.1;
        internal const double VaultPollSeconds = 0.5;
        internal const double VaultMaxWaitSeconds = 20.0;

        private readonly ISuitTransferWorld world;
        private readonly object gate = new object();
        private readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>(StringComparer.Ordinal);
        private readonly Dictionary<(uint AccountId, string Key), string> idempotency = new Dictionary<(uint, string), string>();
        private readonly Dictionary<uint, string> activeByAccount = new Dictionary<uint, string>();

        public SuitTransferService(ISuitTransferWorld world)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
        }

        // ---- request thread ----

        /// <summary>
        /// Validates and records a transfer. Answers the SAME id for a replayed (account, idempotency key)
        /// without running anything again, whatever state that transfer is in.
        /// </summary>
        public SuitTransferSubmitResult Submit(SuitTransferRequest request)
        {
            var shape = Validate(request);

            if (shape != MarketError.None)
                return new SuitTransferSubmitResult(shape, null);

            var key = (request.AccountId, request.IdempotencyKey);

            // The replay lookup comes BEFORE the holder checks: a retry of a transfer already recorded answers its id
            // even if the kill switch has flipped or a holder has been deleted since (those are judged by the run).
            lock (gate)
            {
                ExpireLocked(world.UtcNow);

                if (idempotency.TryGetValue(key, out var replayed) && jobs.ContainsKey(replayed))
                    return new SuitTransferSubmitResult(MarketError.None, replayed);

                if (activeByAccount.ContainsKey(request.AccountId))
                    return new SuitTransferSubmitResult(MarketError.TransferInProgress, null);
            }

            var holderError = ResolveHolders(request, out var holderNames);

            if (holderError != MarketError.None)
                return new SuitTransferSubmitResult(holderError, null);

            SuitTransferCharacter character;

            try
            {
                character = world.FindOnlineCharacter(request.AccountId, request.CharacterGuid);
            }
            catch (Exception ex)
            {
                log.Warn($"[SUIT] the online character lookup threw for account {request.AccountId}: {ex.GetFullMessage()}");
                return new SuitTransferSubmitResult(MarketError.ServerError, null);
            }

            if (character == null)
                return new SuitTransferSubmitResult(MarketError.CharacterOffline, null);

            Job job;

            lock (gate)
            {
                // Re-checked: another request for this account may have landed while the lookup ran.
                if (idempotency.TryGetValue(key, out var replayed) && jobs.ContainsKey(replayed))
                    return new SuitTransferSubmitResult(MarketError.None, replayed);

                if (activeByAccount.ContainsKey(request.AccountId))
                    return new SuitTransferSubmitResult(MarketError.TransferInProgress, null);

                var now = world.UtcNow;

                job = new Job
                {
                    Id = Guid.NewGuid().ToString("N"),
                    AccountId = request.AccountId,
                    CharacterGuid = request.CharacterGuid,
                    IdempotencyKey = request.IdempotencyKey,
                    AllowListed = request.AllowListed,
                    Character = character,
                    // Every line starts on the caller's own store; Begin moves a shared line onto its owner's store
                    // only after re-resolving the grant it names.
                    Lines = request.Items.Select(l => new LineState(l)
                    {
                        StoreAccountId = request.AccountId,
                        HolderName = l.HolderGuid != null && holderNames.TryGetValue(l.HolderGuid.Value, out var holderName) ? holderName : null,
                    }).ToArray(),
                    State = SuitTransferState.Queued,
                    CreatedAt = now,
                    LastProgressAt = now,
                };

                jobs[job.Id] = job;
                idempotency[key] = job.Id;
                activeByAccount[job.AccountId] = job.Id;
            }

            try
            {
                world.EnqueueWorld(() => Begin(job, 0.0));
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] could not enqueue transfer {job.Id} onto the world queue: {ex.GetFullMessage()}");

                lock (gate)
                    RejectLocked(job, MarketError.ServerError, "The transfer could not be started.");

                return new SuitTransferSubmitResult(MarketError.ServerError, null);
            }

            return new SuitTransferSubmitResult(MarketError.None, job.Id);
        }

        /// <summary>The transfer's state and per-line outcomes, or null when this account has no transfer by that id.</summary>
        public SuitTransferStatus GetStatus(uint accountId, string transferId)
        {
            if (string.IsNullOrEmpty(transferId))
                return null;

            lock (gate)
            {
                ExpireLocked(world.UtcNow);

                // Another account's id answers exactly like an unknown one, so an id cannot be probed.
                if (!jobs.TryGetValue(transferId, out var job) || job.AccountId != accountId)
                    return null;

                return new SuitTransferStatus
                {
                    TransferId = job.Id,
                    State = job.State,
                    Reason = job.Reason,
                    Items = job.Lines.Select(l => new SuitTransferItemStatus
                    {
                        VaultKey = l.Request.VaultKey,
                        HolderGuid = l.Request.HolderGuid,
                        ItemGuid = l.Request.ItemGuid,
                        Wcid = l.Request.Wcid,
                        ClassKey = l.Request.ClassKey,
                        Count = l.Request.Count,
                        Outcome = l.CurrentOutcome,
                        Message = l.Message,
                    }).ToList(),
                };
            }
        }

        internal static MarketError Validate(SuitTransferRequest request)
        {
            if (request == null || request.AccountId == 0 || request.CharacterGuid == 0)
                return MarketError.InvalidTransfer;

            if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > MaxIdempotencyKeyLength)
                return MarketError.InvalidTransfer;

            if (request.Items == null || request.Items.Count == 0)
                return MarketError.InvalidTransfer;

            long objects = 0;
            var guids = new HashSet<uint>();
            var sharedGrants = new HashSet<uint>();
            var holders = new HashSet<uint>();

            foreach (var line in request.Items)
            {
                if (line == null || line.Count < 1)
                    return MarketError.InvalidTransfer;

                // A holder line names ONE item in another character's pack: an item guid, count 1, no class key, no vault
                // key, and never the transfer's own character (its pack is where the item is going).
                if (line.HolderGuid != null)
                {
                    if (line.HolderGuid.Value == 0 || line.ItemGuid == null || line.Count != 1 || line.ClassKey != null || line.SharedGrantId != null)
                        return MarketError.InvalidTransfer;

                    if (line.HolderGuid.Value == request.CharacterGuid)
                        return MarketError.InvalidTransfer;

                    if (holders.Add(line.HolderGuid.Value) && holders.Count > MaxHolders)
                        return MarketError.InvalidTransfer;
                }

                if (line.ItemGuid != null && (line.ItemGuid.Value == 0 || line.ClassKey != null))
                    return MarketError.InvalidTransfer;

                // One stored object is one line: a repeated guid would plan a second withdraw of an object the
                // first line already moved.
                if (line.ItemGuid != null && !guids.Add(line.ItemGuid.Value))
                    return MarketError.InvalidTransfer;

                if (line.ClassKey != null && string.IsNullOrWhiteSpace(line.ClassKey))
                    return MarketError.InvalidTransfer;

                if (line.SharedGrantId != null && line.SharedGrantId.Value == 0)
                    return MarketError.InvalidTransfer;

                // No more shared mules per transfer than the shared-inventory route ever lists.
                if (line.SharedGrantId != null && sharedGrants.Add(line.SharedGrantId.Value) && sharedGrants.Count > VaultGranterName.MaxSharedMules)
                    return MarketError.InvalidTransfer;

                // A stored item is one object whatever its stack size; every other line is one object per unit.
                objects += line.ItemGuid != null ? 1 : line.Count;
            }

            if (request.Items.Count > MaxObjects || objects > MaxObjects)
                return MarketError.TooManyItems;

            return MarketError.None;
        }

        // ---- world thread (inbound queue) ----

        /// <summary>
        /// The world-side start, and the cold-vault re-check. <paramref name="waitedSeconds"/> is 0 on the
        /// first run from the inbound queue and grows on each poll from the character's action queue.
        /// </summary>
        private void Begin(Job job, double waitedSeconds)
        {
            lock (gate)
            {
                if (waitedSeconds == 0.0)
                {
                    // A queued job the watchdog already closed must not start.
                    if (job.State != SuitTransferState.Queued)
                        return;

                    job.State = SuitTransferState.Running;
                }
                else if (job.State != SuitTransferState.Running)
                {
                    return;
                }

                job.InStep = true;
                job.LastProgressAt = world.UtcNow;
            }

            try
            {
                BeginCore(job, waitedSeconds);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] transfer {job.Id} threw while starting; nothing was withdrawn: {ex.GetFullMessage()}");

                lock (gate)
                    RejectLocked(job, MarketError.ServerError, "The transfer could not be started.");
            }
            finally
            {
                lock (gate)
                    job.InStep = false;
            }
        }

        private void BeginCore(Job job, double waitedSeconds)
        {
            // Re-validated on this thread: a logout that began after the request thread looked is caught here.
            if (!world.IsStillEligible(job.Character))
            {
                lock (gate)
                    RejectLocked(job, MarketError.CharacterOffline, "The character went offline before the transfer started.");

                return;
            }

            var gateError = world.CheckGates(job.Character, out var gateMessage, job.HolderOnly);

            if (gateError != MarketError.None)
            {
                TellSafe(job.Character, gateMessage);

                lock (gate)
                    RejectLocked(job, gateError, gateMessage);

                return;
            }

            // A shared line's grant is re-resolved ONCE, on this thread, at the first run: the grant it names must
            // still exist, still name this character, and not be owned by the caller's own account. The line then
            // carries its owner's store. Access itself is decided by the store (Plan, then every TryWithdraw).
            if (waitedSeconds == 0.0)
                ResolveSharedLines(job);

            List<uint> storeIds;

            // A holder line touches no vault at all, so a holder-only transfer loads no store.
            lock (gate)
                storeIds = job.Lines.Where(l => l.Final == null && !l.IsHolder).Select(l => l.StoreAccountId).Distinct().ToList();

            var stores = new Dictionary<uint, AccountVaultStore>();
            var stillLoading = new List<uint>();

            // F4b: once, after SharedLoadProbeSeconds of waiting, a shared store that is live but still not warm and whose
            // last load FAILED (LastLoadFailed, read from state) is refused at once rather than holding every other line
            // for the whole wait. The probe never calls TryCheckReady, so it never re-reads the shard on this thread.
            bool probeNow;

            lock (gate)
            {
                probeNow = !job.SharedLoadProbed && waitedSeconds >= SharedLoadProbeSeconds;

                if (probeNow)
                    job.SharedLoadProbed = true;
            }

            foreach (var storeId in storeIds)
            {
                var own = storeId == job.AccountId;

                if (!own)
                {
                    // A SHARED store is looked up only if already live, never built (GetStore builds one and refreshes
                    // its capacity bonuses), and never cold-loaded on this world thread: one that is not live or not
                    // warm is queued with the rate-limited warm queue (the /mule search all pattern; the queue builds
                    // it) and waited for by the poll below, within the same bounded wait. Warm is strictly stronger
                    // than ready, so the TryCheckReady after it does not load.
                    var shared = world.GetExistingStore(storeId);

                    if (shared == null || !shared.IsWarm)
                    {
                        if (probeNow && shared != null && shared.LastLoadFailed)
                        {
                            RefuseStoreLines(job, storeId, line => MuleUnavailableMessage(line.MuleName));
                            continue;
                        }

                        RequestWarmLogged(job, storeId);
                        stillLoading.Add(storeId);
                        continue;
                    }

                    if (!shared.TryCheckReady(out var sharedNotReady))
                    {
                        if (sharedNotReady == AccountVaultStore.StillLoadingMessage)
                            stillLoading.Add(storeId);
                        else
                            RefuseStoreLines(job, storeId, line => MuleUnavailableMessage(line.MuleName));

                        continue;
                    }

                    stores[storeId] = shared;
                    continue;
                }

                var store = world.GetStore(storeId);

                if (store == null)
                {
                    lock (gate)
                        RejectLocked(job, MarketError.VaultUnavailable, AccountVaultStore.UnavailableMessage);

                    return;
                }

                // TryCheckReady, never IsLoaded: on a cold store this is the call that STARTS the load, and only
                // "still loading" is worth waiting for (the facet gate's and MuleSummonHandler.DecideVaultLine's rule).
                if (!store.TryCheckReady(out var notReady))
                {
                    if (notReady == AccountVaultStore.StillLoadingMessage)
                    {
                        stillLoading.Add(storeId);
                        continue;
                    }

                    TellSafe(job.Character, notReady ?? AccountVaultStore.UnavailableMessage);

                    lock (gate)
                        RejectLocked(job, MarketError.VaultUnavailable, notReady ?? AccountVaultStore.UnavailableMessage);

                    return;
                }

                stores[storeId] = store;
            }
            // ONE bounded wait covers every store the transfer touches: the poll re-checks them all.
            if (stillLoading.Count > 0)
            {
                var ownLoading = stillLoading.Contains(job.AccountId);

                if (waitedSeconds < VaultMaxWaitSeconds)
                {
                    if (waitedSeconds == 0.0)
                        TellSafe(job.Character, ownLoading
                            ? "Your vault is loading; your transfer will continue in a moment."
                            : "A shared mule is loading; your transfer will continue in a moment.");

                    var waited = waitedSeconds + VaultPollSeconds;

                    // Nothing has been withdrawn yet, so a poll that cannot be scheduled rejects the transfer.
                    if (!TrySchedule(job, VaultPollSeconds, () => Begin(job, waited)))
                    {
                        lock (gate)
                            RejectLocked(job, MarketError.ServerError, "The transfer could not continue. Nothing was moved.");
                    }

                    return;
                }

                if (ownLoading)
                {
                    TellSafe(job.Character, "Your vault took too long to load, so nothing was moved. Try again.");

                    lock (gate)
                        RejectLocked(job, MarketError.VaultLoading, AccountVaultStore.StillLoadingMessage);

                    return;
                }

                foreach (var storeId in stillLoading)
                    RefuseStoreLines(job, storeId, line => $"{Capitalize(MulePhrase(line.MuleName))} took too long to load, so nothing was moved from it. Try again.");
            }

            if (!Plan(job, stores))
                return;

            if (job.Units.Count > 0)
            {
                var anyShared = job.Units.Any(i => job.Lines[i].IsShared || job.Lines[i].IsHolder);

                TellSafe(job.Character, anyShared
                    ? $"Moving {job.Units.Count} item{(job.Units.Count == 1 ? "" : "s")} to your pack."
                    : $"Moving {job.Units.Count} item{(job.Units.Count == 1 ? "" : "s")} from your vault to your pack.");
            }

            ScheduleNextOrFinish(job);
        }

        /// <summary>
        /// Moves each shared line onto its owner's store, or refuses it. The grant is read fresh, by the grantee
        /// (this job's character), and the line's grant id must be among those rows; the row must name this
        /// character and must not be owned by the caller's own account. A failed read refuses every shared line,
        /// never treating "unavailable" as "not shared". The own lines are untouched.
        /// </summary>
        private void ResolveSharedLines(Job job)
        {
            if (!job.Lines.Any(l => l.IsShared))
                return;

            List<ACE.Database.Models.Shard.AccountVaultGrant> grants;

            try
            {
                grants = world.GetGrantsForGrantee(job.CharacterGuid);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}: the shared-mule grant read threw; refusing the shared lines. {ex.GetFullMessage()}");
                grants = null;
            }

            foreach (var line in job.Lines)
            {
                if (!line.IsShared)
                    continue;

                if (grants == null)
                {
                    RefuseLineUnlocked(line, SharedUnavailableMessage);
                    continue;
                }

                var grantId = line.Request.SharedGrantId.Value;
                var grant = grants.FirstOrDefault(g => g != null && g.Id == grantId);

                if (grant == null || grant.GranteeCharacterGuid != job.CharacterGuid || grant.OwnerAccountId == 0 || grant.OwnerAccountId == job.AccountId)
                {
                    // Revoked, never this character's, or the caller's own account: nothing names the owner here.
                    RefuseLineUnlocked(line, NoLongerSharedMessage(null));
                    continue;
                }

                string name;

                try
                {
                    name = world.GranterName(grant);
                }
                catch (Exception ex)
                {
                    log.Error($"[SUIT] transfer {job.Id}: the granter-name lookup threw for grant {grantId}; using the fallback wording. {ex.GetFullMessage()}");
                    name = null;
                }

                lock (gate)
                {
                    line.StoreAccountId = grant.OwnerAccountId;
                    line.MuleName = name;
                }
            }
        }

        /// <summary>How long a transfer waits before it asks a still-not-warm shared store directly whether it can load.</summary>
        internal const double SharedLoadProbeSeconds = 5.0;

        /// <summary>Warm-queue refusals logged (once per transfer), for tests.</summary>
        internal int WarmRefusalsLogged;

        /// <summary>world.RequestWarm, logged ONCE per transfer when the warm queue refuses (full) and never thrown out.</summary>
        private void RequestWarmLogged(Job job, uint storeAccountId)
        {
            bool queued;

            try
            {
                queued = world.RequestWarm(storeAccountId);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] transfer {job.Id}: queueing a warm-up of store {storeAccountId} threw. {ex.GetFullMessage()}");
                queued = false;
            }

            if (queued)
                return;

            lock (gate)
            {
                if (job.WarmRefusalLogged)
                    return;

                job.WarmRefusalLogged = true;
                WarmRefusalsLogged++;
            }

            log.Warn($"[SUIT] transfer {job.Id} account {job.AccountId}: the vault warm queue refused store {storeAccountId} (queue full?); its lines wait and may time out as still loading.");
        }

        /// <summary>Refuses every still-open line on <paramref name="storeAccountId"/>'s store with its own message.</summary>
        private void RefuseStoreLines(Job job, uint storeAccountId, Func<LineState, string> message)
        {
            lock (gate)
            {
                foreach (var line in job.Lines)
                {
                    if (line.Final == null && line.StoreAccountId == storeAccountId)
                        FinalizeLineLocked(line, SuitTransferOutcome.Refused, message(line));
                }
            }
        }

        /// <summary>
        /// Resolves every line against its store as it is now, refuses the lines that cannot run (gone, listed
        /// without the opt-in - and ALWAYS for a shared line, a stored item asked for in part, more units than the
        /// row holds, a shared mule this character may no longer withdraw from), and refuses the WHOLE job when
        /// what is left does not fit. Returns false when the job was rejected.
        ///
        /// A shared store's access is checked here ONCE (AccountVaultStore.TryGetAccess, the one authority) for
        /// exactly this character; after this, each unit relies on TryWithdraw's own re-check.
        /// </summary>
        private bool Plan(Job job, IReadOnlyDictionary<uint, AccountVaultStore> stores)
        {
            var entriesByStore = new Dictionary<uint, IReadOnlyList<VaultEntry>>();
            var actor = new VaultActor(job.AccountId, job.CharacterGuid, job.Character?.Name);

            foreach (var pair in stores)
            {
                if (pair.Key != job.AccountId)
                {
                    if (!pair.Value.TryGetAccess(actor, out var access, out _))
                    {
                        RefuseStoreLines(job, pair.Key, line => MuleUnavailableMessage(line.MuleName));
                        continue;
                    }

                    if (access != VaultAccess.DepositWithdraw)
                    {
                        RefuseStoreLines(job, pair.Key, line => NoLongerSharedMessage(line.MuleName));
                        continue;
                    }
                }

                entriesByStore[pair.Key] = pair.Value.GetEntries(0, -1);
            }

            var units = new Queue<int>();
            var itemSlots = 0;
            var containerSlots = 0;
            long burden = 0;

            // Units claimed so far per (store, ledger/class row), so two lines naming the same row (the inventory
            // route splits a partly listed row into a free and a listed line) are budgeted together, and the same
            // wcid in two different vaults is not.
            var claimed = new Dictionary<string, long>(StringComparer.Ordinal);

            for (var i = 0; i < job.Lines.Length; i++)
            {
                var line = job.Lines[i];
                var request = line.Request;

                bool final;

                lock (gate)
                    final = line.Final != null;

                if (final)
                    continue;

                // A holder line is one non-container item in another character's pack: one main-pack slot, and its
                // template's burden as the estimate (the item itself is only read at its own step, which re-checks the
                // pack against the real object). No store and no listing is involved.
                if (line.IsHolder)
                {
                    itemSlots++;
                    burden += Math.Max(0, world.TemplateBurden(request.Wcid));
                    units.Enqueue(i);
                    continue;
                }

                // Owner ruling: a shared line never moves a listed item, whatever allow_listed says.
                var allowListed = job.AllowListed && !line.IsShared;

                var target = entriesByStore.TryGetValue(line.StoreAccountId, out var entries) ? Resolve(entries, request) : null;

                if (target == null)
                {
                    RefuseLineUnlocked(line, GoneMessage(line));
                    continue;
                }

                if (target.Kind == TargetKind.Stored || target.Kind == TargetKind.GroupMember)
                {
                    var whole = target.Object.StackSize ?? 1;

                    if (request.Count != whole)
                    {
                        RefuseLineUnlocked(line, "A stored item can only be moved whole.");
                        continue;
                    }

                    if (line.IsShared && MarketManager.IsBoundForSharing(target.Object))
                    {
                        RefuseLineUnlocked(line, BondedSharedMessage(line.MuleName));
                        continue;
                    }

                    if (!allowListed && world.ListedCount(line.StoreAccountId, target.Row) != null)
                    {
                        RefuseLineUnlocked(line, line.IsShared
                            ? SharedListedMessage
                            : "That item is listed on the market. Allow listed items to move it, which takes the listing down.");
                        continue;
                    }

                    if (target.Object.UseBackpackSlot)
                        containerSlots++;
                    else
                        itemSlots++;

                    burden += Math.Max(0, target.Object.EncumbranceVal ?? 0);
                    units.Enqueue(i);
                    continue;
                }

                // Ledger row or class line: one unit per withdraw. A shared one whose template is bonded or attuned stays.
                if (line.IsShared)
                {
                    var templateRefusal = SharedTemplateRefusal(job, line, target.Row.Wcid);

                    if (templateRefusal != null)
                    {
                        RefuseLineUnlocked(line, templateRefusal);
                        continue;
                    }
                }

                var rowKey = line.StoreAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
                    + (target.Kind == TargetKind.Class ? "c:" + target.Row.ClassDisplayId : "l:" + target.Row.Wcid);
                claimed.TryGetValue(rowKey, out var before);
                var after = before + request.Count;

                if (after > target.Row.Count)
                {
                    RefuseLineUnlocked(line, line.IsShared
                        ? $"{Capitalize(MulePhrase(line.MuleName))} holds only {target.Row.Count} of those."
                        : $"Your vault holds only {target.Row.Count} of those.");
                    continue;
                }

                if (!allowListed)
                {
                    var listed = world.ListedCount(line.StoreAccountId, target.Row);

                    // The keep-alive rule (MarketManager.OnVaultWithdraw): the listing survives while at least its
                    // count stays on the row, so only units that dig into the listed part would delist it.
                    if (listed != null && target.Row.Count - after < listed.Value)
                    {
                        RefuseLineUnlocked(line, line.IsShared
                            ? SharedPartListedMessage
                            : "Part of that is listed on the market. Allow listed items to move it, which takes the listing down.");
                        continue;
                    }
                }

                claimed[rowKey] = after;

                var unitBurden = Math.Max(0, world.TemplateBurden(target.Row.Wcid));

                itemSlots += request.Count;
                burden += (long)unitBurden * request.Count;

                for (var u = 0; u < request.Count; u++)
                    units.Enqueue(i);
            }

            var freeItems = world.FreeMainPackSlots(job.Character);
            var freeContainers = world.FreeContainerSlots(job.Character);
            var available = world.AvailableBurden(job.Character);

            string full = null;

            if (itemSlots > freeItems)
                full = $"Your main pack needs {itemSlots} free slot{(itemSlots == 1 ? "" : "s")} for this transfer and has {Math.Max(0, freeItems)}. Nothing was moved.";
            else if (containerSlots > freeContainers)
                full = $"This transfer needs {containerSlots} free pack slot{(containerSlots == 1 ? "" : "s")} for containers and you have {Math.Max(0, freeContainers)}. Nothing was moved.";
            else if (burden > available)
                full = $"This transfer weighs {burden:N0} burden and you can carry {Math.Max(0, available):N0} more. Nothing was moved.";

            if (full != null)
            {
                TellSafe(job.Character, full);

                lock (gate)
                    RejectLocked(job, MarketError.PackFull, full);

                return false;
            }

            lock (gate)
                job.Units = units;

            return true;
        }

        // ---- player-facing wording for shared lines (ASCII only) ----

        internal const string SharedUnavailableMessage = "Shared mules are unavailable right now, so nothing was moved from them.";
        internal const string SharedListedMessage = "That item is listed on the market by the mule's owner, so it cannot be moved.";
        internal const string SharedPartListedMessage = "Part of that is listed on the market by the mule's owner, so it cannot be moved.";

        /// <summary>
        /// The two refusals AccountVaultStore.TryWithdraw gives when access is no longer DepositWithdraw (no grant,
        /// deposit-only). A shared line's unit refused with either is reported as "no longer shared". Pinned against
        /// the real store by SuitTransferServiceTests' revoke and downgrade cases.
        /// </summary>
        internal const string WithdrawNoAccessReason = "You do not have permission to use this vault.";
        internal const string WithdrawDepositOnlyReason = "You do not have permission to withdraw from this vault.";

        /// <summary>"Bob's mule", or "that shared mule" when the granter is not known.</summary>
        internal static string MulePhrase(string muleName) => string.IsNullOrWhiteSpace(muleName) ? "that shared mule" : $"{muleName}'s mule";

        /// <summary>"Bob's mule is no longer shared with you."</summary>
        internal static string NoLongerSharedMessage(string muleName) => $"{Capitalize(MulePhrase(muleName))} is no longer shared with you.";

        /// <summary>"That item is no longer in Bob's mule."</summary>
        internal static string NoLongerInMuleMessage(string muleName) => $"That item is no longer in {MulePhrase(muleName)}.";

        /// <summary>"That item is bonded and can't leave Bob's mule."</summary>
        internal static string BondedSharedMessage(string muleName) => $"That item is bonded and can't leave {MulePhrase(muleName)}.";

        internal static string MuleUnavailableMessage(string muleName) => $"{Capitalize(MulePhrase(muleName))} is unavailable right now, so nothing was moved from it.";

        private static string Capitalize(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        /// <summary>"your vault" for an own line, the mule's phrase for a shared one, "Bob's pack" for a holder line.</summary>
        private static string VaultPhrase(LineState line) => line.IsHolder ? HolderPackPhrase(line) : line.IsShared ? MulePhrase(line.MuleName) : "your vault";

        private static string GoneMessage(LineState line) => line.IsShared ? NoLongerInMuleMessage(line.MuleName) : "That is no longer in your vault.";

        /// <summary>The store a shared line names, for the logs an operator reconciles against; empty for an own line.</summary>
        private static string StoreTag(LineState line) => line.IsShared ? $" store {line.StoreAccountId} (grant {line.Request.SharedGrantId})" : "";

        /// <summary>
        /// The bonded ruling for a shared ledger row or class line: null when its template weenie may leave the mule,
        /// else the refusal. The template comes from MarketManager.WeenieLookup, the cached source the suit projection
        /// already uses (the same cache TemplateBurden reads), so no new kind of read is added here. A lookup that
        /// throws refuses as unavailable rather than guessing.
        /// </summary>
        private static string SharedTemplateRefusal(Job job, LineState line, uint wcid)
        {
            ACE.Entity.Models.Weenie weenie;

            try
            {
                weenie = MarketManager.WeenieLookup?.Invoke(wcid);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] transfer {job.Id}{StoreTag(line)}: the template lookup threw for wcid {wcid}; refusing the line. {ex.GetFullMessage()}");
                return MuleUnavailableMessage(line.MuleName);
            }

            return MarketManager.IsBoundForSharing(weenie) ? BondedSharedMessage(line.MuleName) : null;
        }

        /// <summary>
        /// The non-owner listing guard for one member of a shared stored group, asked under the group's representative
        /// guid: true when withdrawing it would leave every owner listing standing. A null hook means the market never
        /// started, so there is no listing to protect.
        /// </summary>
        private static bool SharedGroupMemberIsFree(Job job, LineState line, uint representativeGuid, uint wcid, out string refusal)
        {
            refusal = null;

            var hook = AccountVaultStore.WithdrawWouldDelistHook;

            if (hook == null)
                return true;

            int? free;

            try
            {
                free = hook(line.StoreAccountId, representativeGuid, wcid, 1, null);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] transfer {job.Id}{StoreTag(line)}: the market listing guard threw for group representative 0x{representativeGuid:X8}; refusing the unit. {ex.GetFullMessage()}");
                free = AccountVaultStore.WithdrawGuardUnavailable;
            }

            if (free == null)
                return true;

            refusal = free.Value < 0 ? MuleUnavailableMessage(line.MuleName) : SharedListedMessage;
            return false;
        }

        // ---- character action queue ----

        private void ScheduleNextOrFinish(Job job)
        {
            bool more;

            lock (gate)
            {
                SkipStoppedUnitsLocked(job);
                more = job.Units != null && job.Units.Count > 0;

                if (!more)
                {
                    CompleteLocked(job, MarketError.None, null);
                    return;
                }
            }

            // A step that cannot be scheduled would leave the job Running until the stall watchdog: stop it now.
            if (!TrySchedule(job, StepDelaySeconds, () => Step(job)))
            {
                lock (gate)
                    CompleteLocked(job, MarketError.ServerError, VaultErrorStopMessage);
            }
        }

        /// <summary>world.Schedule, with a throw logged and reported as false instead of escaping the queue it runs on.</summary>
        private bool TrySchedule(Job job, double delaySeconds, Action work)
        {
            try
            {
                world.Schedule(job.Character, delaySeconds, work);
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId} char 0x{job.CharacterGuid:X8}: scheduling the next step threw; stopping the transfer. {ex.GetFullMessage()}");
                return false;
            }
        }

        private void Step(Job job)
        {
            int lineIndex;

            lock (gate)
            {
                if (job.State != SuitTransferState.Running)
                    return;

                SkipStoppedUnitsLocked(job);

                if (job.Units == null || job.Units.Count == 0)
                {
                    CompleteLocked(job, MarketError.None, null);
                    return;
                }

                lineIndex = job.Units.Dequeue();
                job.InStep = true;
                job.LastProgressAt = world.UtcNow;
            }

            var next = StepNext.Continue;

            try
            {
                var line = job.Lines[lineIndex];

                next = line.IsHolder
                    ? StepHolder(job, lineIndex)
                    : StepCore(job, line) ? StepNext.Continue : StepNext.Closed;
            }
            catch (Exception ex)
            {
                // StepCore guards the withdraw and the bookkeeping after it itself, and StepHolder guards everything from
                // its claim on, so a throw that reaches here came from BEFORE anything moved (a gate read, the resolve,
                // the listing read): nothing left the vault or the other character. The line is refused and the transfer
                // stops rather than carrying on past an unexplained fault.
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId} char 0x{job.CharacterGuid:X8}: a step for wcid {job.Lines[lineIndex].Request.Wcid} threw before its withdraw; nothing was withdrawn for it. Stopping the transfer. {ex.GetFullMessage()}");

                lock (gate)
                {
                    FinalizeLineLocked(job.Lines[lineIndex], SuitTransferOutcome.Refused, $"Something went wrong before this was moved; it is still in {VaultPhrase(job.Lines[lineIndex])}.");
                    CompleteLocked(job, MarketError.ServerError, VaultErrorStopMessage);
                }

                next = StepNext.Closed;
            }
            finally
            {
                lock (gate)
                    job.InStep = false;
            }

            // Pending: a holder unit is waiting on the shard queue; its own continuation schedules what comes next.
            if (next == StepNext.Continue)
                ScheduleNextOrFinish(job);
        }

        private enum StepNext
        {
            /// <summary>The unit is settled; schedule the next one.</summary>
            Continue,

            /// <summary>The job was closed.</summary>
            Closed,

            /// <summary>A holder unit is waiting on a read or a save; its callback carries the job on.</summary>
            Pending,
        }

        /// <summary>One unit. Returns false when the job was closed (a re-validation failed).</summary>
        private bool StepCore(Job job, LineState line)
        {
            // Re-validated before every unit: the character can log out, walk away or start a trade mid-run.
            if (!world.IsStillEligible(job.Character))
            {
                lock (gate)
                    CompleteLocked(job, MarketError.CharacterOffline, "The character went offline, so the rest was not moved.");

                return false;
            }

            var gateError = world.CheckGates(job.Character, out var gateMessage, job.HolderOnly);

            if (gateError != MarketError.None)
            {
                TellSafe(job.Character, gateMessage + " The rest of your transfer was stopped.");

                lock (gate)
                    CompleteLocked(job, gateError, gateMessage);

                return false;
            }

            // The LINE's store: the owner's for a shared line (set at Begin), the caller's for an own line. A shared
            // store is looked up only if already live, and one that is gone or whose index is no longer loaded refuses its
            // line here (after queueing a warm-up) rather than being built or loaded synchronously on this step's thread.
            // A live, index-loaded store that is merely not warm (a ledger reload pending after a debit, say) falls through
            // to TryCheckReady, which re-reads only the stale table, and refuses only if that fails.
            var store = line.IsShared ? world.GetExistingStore(line.StoreAccountId) : world.GetStore(line.StoreAccountId);

            if (line.IsShared && (store == null || !store.IsIndexLoaded))
            {
                RequestWarmLogged(job, line.StoreAccountId);

                lock (gate)
                    FinalizeLineLocked(line, SuitTransferOutcome.Refused, MuleUnavailableMessage(line.MuleName));

                return true;
            }

            if (store == null || !store.TryCheckReady(out _))
            {
                // A shared mule retired or reloading mid-run stops only its own line: the rest of the transfer
                // (the caller's own vault, other mules) is not that store's to stop. The own store keeps the
                // existing whole-transfer stop.
                if (line.IsShared)
                {
                    RequestWarmLogged(job, line.StoreAccountId);

                    lock (gate)
                        FinalizeLineLocked(line, SuitTransferOutcome.Refused, MuleUnavailableMessage(line.MuleName));

                    return true;
                }

                lock (gate)
                    CompleteLocked(job, MarketError.VaultUnavailable, AccountVaultStore.UnavailableMessage);

                return false;
            }

            // Resolved fresh, by identity, in the line's store: whatever the plan saw may have moved since.
            // Cost note: a full GetEntries enumeration per unit (at most 24 per transfer), accepted over caching a stale view.
            var target = Resolve(store.GetEntries(0, -1), line.Request);

            if (target == null)
            {
                lock (gate)
                    FinalizeLineLocked(line, SuitTransferOutcome.Refused, GoneMessage(line));

                return true;
            }

            // Owner ruling: a bonded (or attuned) item never leaves a shared mule. Checked on the LIVE object here, not
            // only at the plan: the item may have been bonded since.
            if (line.IsShared && (target.Kind == TargetKind.Stored || target.Kind == TargetKind.GroupMember) && MarketManager.IsBoundForSharing(target.Object))
            {
                lock (gate)
                    FinalizeLineLocked(line, SuitTransferOutcome.Refused, BondedSharedMessage(line.MuleName));

                return true;
            }

            // The same ruling for a shared ledger row or class line, from its template weenie, re-read at every unit.
            if (line.IsShared && (target.Kind == TargetKind.Ledger || target.Kind == TargetKind.Class))
            {
                var templateRefusal = SharedTemplateRefusal(job, line, target.Row.Wcid);

                if (templateRefusal != null)
                {
                    lock (gate)
                        FinalizeLineLocked(line, SuitTransferOutcome.Refused, templateRefusal);

                    return true;
                }
            }

            // Owner ruling: a shared line never moves a listed item, whatever allow_listed says.
            if (!job.AllowListed || line.IsShared)
            {
                var listed = world.ListedCount(line.StoreAccountId, target.Row);

                var wouldDelist = listed != null && (target.Kind == TargetKind.Stored || target.Kind == TargetKind.GroupMember
                    ? true
                    : target.Row.Count - 1 < listed.Value);

                if (wouldDelist)
                {
                    lock (gate)
                        FinalizeLineLocked(line, SuitTransferOutcome.Refused, line.IsShared ? SharedListedMessage : "That is listed on the market. Allow listed items to move it, which takes the listing down.");

                    return true;
                }
            }

            var isObject = target.Kind == TargetKind.Stored || target.Kind == TargetKind.GroupMember;

            // The pack is re-checked for THIS unit: the plan's fit is a snapshot, and the character may have picked
            // things up since. A unit that no longer fits stops the transfer before anything is withdrawn for it.
            var usesContainerSlot = isObject && target.Object.UseBackpackSlot;
            var freeSlots = usesContainerSlot ? world.FreeContainerSlots(job.Character) : world.FreeMainPackSlots(job.Character);
            var unitBurden = isObject ? Math.Max(0, target.Object.EncumbranceVal ?? 0) : Math.Max(0, world.TemplateBurden(target.Row.Wcid));

            if (freeSlots < 1 || unitBurden > world.AvailableBurden(job.Character))
            {
                var full = freeSlots < 1
                    ? "Your pack filled up, so the rest of your transfer was stopped."
                    : "You are carrying too much for the next item, so the rest of your transfer was stopped.";

                TellSafe(job.Character, full);

                lock (gate)
                    CompleteLocked(job, MarketError.PackFull, full);

                return false;
            }

            var isLedger = target.Kind == TargetKind.Ledger;
            var amount = isObject ? (int)target.Withdraw.Count : 1;

            if (target.Kind == TargetKind.GroupMember)
            {
                // A shared line never reports the group-member withdraw (that would close the owner's group listing).
                // It asks the market's non-owner guard about the REPRESENTATIVE instead, because the store's own
                // non-owner check sees only the member's guid, which no group listing names. Any answer refuses the
                // unit, including the unavailable sentinel and a throw.
                if (line.IsShared)
                {
                    if (!SharedGroupMemberIsFree(job, line, target.Row.Guid.Full, target.Row.Wcid, out var refusal))
                    {
                        lock (gate)
                            FinalizeLineLocked(line, SuitTransferOutcome.Refused, refusal);

                        return true;
                    }
                }
                else
                {
                    world.BeforeGroupMemberWithdraw(line.StoreAccountId, target.Row.Guid.Full, target.Row.Wcid);
                }
            }

            // ONLY the withdraw maps a throw to Threw: from here on an object may have left the vault.
            VaultPackDeliveryResult result;

            try
            {
                result = world.WithdrawToPack(job.Character, store, target.Withdraw, amount, isLedger);
            }
            catch (Exception ex)
            {
                // A throw outside VaultPackDelivery's own handling (a throwing pack delivery): an object may have left
                // the vault without landing anywhere. Never retried, and the whole transfer stops.
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId} char 0x{job.CharacterGuid:X8}{StoreTag(line)}: the withdraw of wcid {line.Request.Wcid} threw. The vault's state for that line is UNKNOWN - reconcile against account_vault_log; it is not retried. {ex.GetFullMessage()}");

                lock (gate)
                {
                    FinalizeLineLocked(line, SuitTransferOutcome.Threw, "Something went wrong moving that, and the vault's state afterward could not be confirmed. Contact staff; do not retry.");
                    CompleteLocked(job, MarketError.ServerError, VaultErrorStopMessage);
                }

                return false;
            }

            // The bookkeeping after a withdraw must never turn a delivered unit into a fault: the state is applied
            // first (Apply does nothing else), and the diagnostics and the chat line after it are each guarded, so
            // a throw in either is logged and does not change the outcome.
            var label = Label(result, line);
            int deliveredBefore;

            lock (gate)
                deliveredBefore = line.Delivered;

            try
            {
                Apply(line, result, label);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}: recording the {result.Status} withdraw of wcid {line.Request.Wcid} threw; the line is finalised from the withdraw's status. {ex.GetFullMessage()}");

                lock (gate)
                {
                    if (line.Final == null)
                    {
                        if (result.Status == VaultPackDeliveryStatus.Delivered)
                        {
                            // The unit landed: count it (once), and end the line only when it is the last one.
                            // Defensive: no current throw site in Apply sits after its increment, so this always counts today.
                            if (line.Delivered == deliveredBefore)
                                line.Delivered++;

                            if (line.Delivered >= line.Units)
                                FinalizeLineLocked(line, SuitTransferOutcome.Delivered, null);
                        }
                        else
                            FinalizeLineLocked(line, FromStatus(result.Status), "It was not moved.");
                    }
                }
            }

            try
            {
                Diagnose(job, line, result);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}: logging the {result.Status} withdraw of wcid {line.Request.Wcid} threw; the outcome stands. {ex.GetFullMessage()}");
            }

            try
            {
                Report(job, line, result, label);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}: reporting the {result.Status} withdraw of wcid {line.Request.Wcid} threw; the outcome stands. {ex.GetFullMessage()}");
            }

            // Ruling: a Threw or Stranded unit stops the WHOLE transfer, not just its line.
            SuitTransferOutcome? final;

            lock (gate)
                final = line.Final;

            if (final == SuitTransferOutcome.Threw || final == SuitTransferOutcome.Stranded)
            {
                lock (gate)
                    CompleteLocked(job, MarketError.ServerError, VaultErrorStopMessage);

                return false;
            }

            return true;
        }

        internal const string VaultErrorStopMessage = "A vault error stopped the rest of the transfer.";

        private static SuitTransferOutcome FromStatus(VaultPackDeliveryStatus status) => status switch
        {
            VaultPackDeliveryStatus.Delivered => SuitTransferOutcome.Delivered,
            VaultPackDeliveryStatus.Returned => SuitTransferOutcome.Returned,
            VaultPackDeliveryStatus.Refused => SuitTransferOutcome.Refused,
            VaultPackDeliveryStatus.Stranded => SuitTransferOutcome.Stranded,
            _ => SuitTransferOutcome.Threw,
        };

        private static string Label(VaultPackDeliveryResult result, LineState line)
        {
            try
            {
                var name = result.Item?.NameWithMaterial;

                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            catch (Exception)
            {
                // A name that cannot be read must not stand between a withdraw and its bookkeeping.
            }

            return line.Request.ItemGuid != null ? "that item" : "that";
        }

        /// <summary>The player's chat line for one unit. After <see cref="Apply"/>; may throw without changing the outcome.</summary>
        private void Report(Job job, LineState line, VaultPackDeliveryResult result, string label)
        {
            if (result.Status == VaultPackDeliveryStatus.Delivered)
                world.Tell(job.Character, $"{label} moved from {VaultPhrase(line)} to your pack.");
            else
                world.Tell(job.Character, line.Message ?? $"{label} was not moved.");
        }

        /// <summary>
        /// The error log for one withdraw result. Runs AFTER <see cref="Apply"/> in its own guarded region: these
        /// lines read the withdrawn objects (guids, the extras), and a throw here must not touch the outcome.
        /// </summary>
        private void Diagnose(Job job, LineState line, VaultPackDeliveryResult result)
        {
            if (DiagnoseFaultForTests != null)
                DiagnoseFaultForTests();

            if (result.ReturnException != null)
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}{StoreTag(line)}: the return of 0x{result.Item?.Guid.Full:X8} (wcid {line.Request.Wcid}) to the vault threw. Its state may be INCONSISTENT and needs manual reconciliation against account_vault_log. Not retried. {result.ReturnException}");

            if (result.WithdrawException != null)
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}{StoreTag(line)}: TryWithdraw threw for wcid {line.Request.Wcid}; return outcome {result.ReturnOutcome?.ToString() ?? "none (nothing recovered)"}. Reconcile against account_vault_log; not retried. {result.WithdrawException}");

            if (result.ExtraReturns.Count > 0)
            {
                var notBack = result.ExtraReturns.Count(e => e.Outcome != VaultReturnOutcome.Returned);

                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}: one withdraw of wcid {line.Request.Wcid} produced {result.ExtraReturns.Count + 1} objects where exactly one was asked for. Extras returned: {result.ExtraReturns.Count - notBack}; NOT returned: {notBack} ({string.Join(", ", result.ExtraReturns.Select(e => $"0x{e.Item?.Guid.Full:X8}={e.Outcome}"))}).");
            }

            if (result.Status == VaultPackDeliveryStatus.Stranded)
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}{StoreTag(line)}: withdrawn 0x{result.Item?.Guid.Full:X8} (wcid {line.Request.Wcid}) could not be delivered and the vault REFUSED it back. It is in NEITHER the vault NOR the character's possession - manual recovery required.");
        }

        /// <summary>Test seam: thrown into <see cref="Diagnose"/> when set. Null in production.</summary>
        internal Action DiagnoseFaultForTests;

        /// <summary>Test seam: thrown into <see cref="Apply"/>, before its state update, when set. Null in production.</summary>
        internal Action ApplyFaultForTests;

        /// <summary>
        /// The line's state for one withdraw result, and nothing else: no logging and no reads of the withdrawn
        /// objects, only the result's own status fields and the already-built <paramref name="label"/>.
        /// </summary>
        private void Apply(LineState line, VaultPackDeliveryResult result, string label)
        {
            if (ApplyFaultForTests != null)
                ApplyFaultForTests();

            // Exactly one object per withdraw is this feature's contract. The core returns any extras to the
            // vault itself; what is left here is to stop the line, and say so.
            string extrasNote = null;
            var extrasOutcome = SuitTransferOutcome.Delivered;
            var extras = result.ExtraReturns;

            if (extras.Count > 0)
            {
                var notBack = 0;
                var threw = false;

                for (var i = 0; i < extras.Count; i++)
                {
                    if (extras[i].Outcome != VaultReturnOutcome.Returned)
                        notBack++;

                    if (extras[i].Outcome == VaultReturnOutcome.Threw)
                        threw = true;
                }

                if (threw)
                    extrasOutcome = SuitTransferOutcome.Threw;
                else if (notBack > 0)
                    extrasOutcome = SuitTransferOutcome.Stranded;

                extrasNote = notBack == 0
                    ? "The vault handed over more than one object; the extras were put back."
                    : "The vault handed over more than one object and not all of the extras could be put back. Contact staff.";
            }

            lock (gate)
            {
                switch (result.Status)
                {
                    case VaultPackDeliveryStatus.Delivered:
                        line.Delivered++;

                        if (extrasNote != null)
                        {
                            // The unit itself landed. The line stops here either way: a bad extra outranks the
                            // delivery, and clean extras still end the line (the rest is reported not moved).
                            var outcome = extrasOutcome != SuitTransferOutcome.Delivered
                                ? extrasOutcome
                                : line.Delivered >= line.Units ? SuitTransferOutcome.Delivered : SuitTransferOutcome.Refused;

                            FinalizeLineLocked(line, outcome, extrasNote);
                        }
                        else if (line.Delivered >= line.Units)
                            FinalizeLineLocked(line, SuitTransferOutcome.Delivered, null);

                        break;

                    case VaultPackDeliveryStatus.Returned:
                        FinalizeLineLocked(line, Worse(SuitTransferOutcome.Returned, extrasOutcome), result.WithdrawException != null
                            ? $"A vault error interrupted moving {label}; it was returned to {VaultPhrase(line)}."
                            : $"Your pack had no room for {label}; it was left in {VaultPhrase(line)}.");
                        break;

                    case VaultPackDeliveryStatus.Refused:
                        FinalizeLineLocked(line, SuitTransferOutcome.Refused, RefusedMessage(line, result));
                        break;

                    case VaultPackDeliveryStatus.Stranded:
                        FinalizeLineLocked(line, Worse(SuitTransferOutcome.Stranded, extrasOutcome), $"{label} could not be delivered and could not be put back in {VaultPhrase(line)}. Contact staff.");
                        break;

                    default: // Threw
                        FinalizeLineLocked(line, SuitTransferOutcome.Threw, $"Moving {label} hit a vault error and the vault's state afterward could not be confirmed. Contact staff; do not retry.");
                        break;
                }
            }
        }

        /// <summary>
        /// A Refused withdraw's line message. For a shared line, TryWithdraw's own access refusal (a revoke or a
        /// downgrade since the plan; no second access read is made) reads as "no longer shared".
        /// </summary>
        private static string RefusedMessage(LineState line, VaultPackDeliveryResult result)
        {
            if (result.StoreUnavailable)
                return line.IsShared ? MuleUnavailableMessage(line.MuleName) : "Your vault is unavailable right now.";

            if (line.IsShared && (result.FailReason == WithdrawNoAccessReason || result.FailReason == WithdrawDepositOnlyReason))
                return NoLongerSharedMessage(line.MuleName);

            // Any other refusal of a shared unit is worded by the store for its OWNER ("Your vault holds only N of
            // those."), which is wrong for the grantee; it means the item is no longer there to take.
            if (line.IsShared)
                return NoLongerInMuleMessage(line.MuleName);

            return $"The vault refused it: {result.FailReason ?? "unknown reason"}.";
        }

        private static SuitTransferOutcome Worse(SuitTransferOutcome a, SuitTransferOutcome b) => Severity(a) >= Severity(b) ? a : b;

        private static int Severity(SuitTransferOutcome o) => o switch
        {
            SuitTransferOutcome.Threw => 5,
            SuitTransferOutcome.Stranded => 4,
            SuitTransferOutcome.Returned => 3,
            SuitTransferOutcome.Refused => 2,
            SuitTransferOutcome.Delivered => 1,
            _ => 0,
        };

        // ---- resolution ----

        internal enum TargetKind
        {
            Stored,
            GroupMember,
            Ledger,
            Class,
        }

        /// <summary>What one line names in the vault right now.</summary>
        internal sealed class Target
        {
            public TargetKind Kind { get; init; }

            /// <summary>The entry handed to the withdraw: the row itself, or a single-member entry for a group member.</summary>
            public VaultEntry Withdraw { get; init; }

            /// <summary>The row as the vault view lists it (for a group member, the GROUP row), which is what a listing names.</summary>
            public VaultEntry Row { get; init; }

            /// <summary>The stored object, for Stored and GroupMember.</summary>
            public WorldObject Object { get; init; }
        }

        /// <summary>
        /// Finds the line's row by IDENTITY: a guid line matches a stored item (or one member of a stored group)
        /// with that guid and that wcid; a class line matches the class line with that display id; any other
        /// line matches the ledger row of that wcid. Never a class row for a ledger line or the other way round.
        /// </summary>
        internal static Target Resolve(IReadOnlyList<VaultEntry> entries, SuitTransferLine line)
        {
            if (entries == null || line == null)
                return null;

            foreach (var entry in entries)
            {
                if (entry == null)
                    continue;

                if (line.ItemGuid != null)
                {
                    if (entry.Kind != VaultEntryKind.StoredItem)
                        continue;

                    if (!entry.IsGroup)
                    {
                        if (entry.Guid.Full == line.ItemGuid.Value && entry.WorldObject != null && entry.WorldObject.WeenieClassId == line.Wcid)
                            return new Target { Kind = TargetKind.Stored, Withdraw = entry, Row = entry, Object = entry.WorldObject };

                        continue;
                    }

                    foreach (var member in entry.Members)
                    {
                        if (member != null && member.Guid.Full == line.ItemGuid.Value && member.WeenieClassId == line.Wcid)
                            return new Target { Kind = TargetKind.GroupMember, Withdraw = VaultEntry.ForItem(member), Row = entry, Object = member };
                    }

                    continue;
                }

                if (line.ClassKey != null)
                {
                    if (entry.Kind == VaultEntryKind.Class && string.Equals(entry.ClassDisplayId, line.ClassKey, StringComparison.Ordinal))
                        return new Target { Kind = TargetKind.Class, Withdraw = entry, Row = entry };

                    continue;
                }

                if (entry.Kind == VaultEntryKind.Ledger && entry.Wcid == line.Wcid)
                    return new Target { Kind = TargetKind.Ledger, Withdraw = entry, Row = entry };
            }

            return null;
        }

        // ---- job bookkeeping (every *Locked method expects `gate` held) ----

        private void RefuseLineUnlocked(LineState line, string message)
        {
            lock (gate)
                FinalizeLineLocked(line, SuitTransferOutcome.Refused, message);
        }

        private static void FinalizeLineLocked(LineState line, SuitTransferOutcome outcome, string message)
        {
            if (line.Final != null)
                return;

            line.Final = outcome;

            var partial = line.Units > 1 && outcome != SuitTransferOutcome.Delivered ? $"Moved {line.Delivered} of {line.Units}. " : "";

            line.Message = message == null ? null : partial + message;
        }

        private static void SkipStoppedUnitsLocked(Job job)
        {
            while (job.Units != null && job.Units.Count > 0 && job.Lines[job.Units.Peek()].Final != null)
                job.Units.Dequeue();
        }

        private void RejectLocked(Job job, MarketError reason, string message)
        {
            if (job.State == SuitTransferState.Completed || job.State == SuitTransferState.Rejected)
                return;

            job.State = SuitTransferState.Rejected;
            job.Reason = reason;

            foreach (var line in job.Lines)
                FinalizeLineLocked(line, SuitTransferOutcome.Refused, message);

            FinishLocked(job, $"rejected: {MarketErrorCodes.ToCode(reason)}");
        }

        private void CompleteLocked(Job job, MarketError reason, string message)
        {
            if (job.State == SuitTransferState.Completed || job.State == SuitTransferState.Rejected)
                return;

            job.State = SuitTransferState.Completed;
            job.Reason = reason;

            foreach (var line in job.Lines)
            {
                if (line.Final == null)
                    FinalizeLineLocked(line, line.Delivered >= line.Units ? SuitTransferOutcome.Delivered : SuitTransferOutcome.Refused,
                        line.Delivered >= line.Units ? null : (message ?? "It was not moved."));
            }

            job.Units?.Clear();

            var summary = string.Join(", ", job.Lines.GroupBy(l => l.CurrentOutcome).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));

            FinishLocked(job, reason == MarketError.None ? $"completed: {summary}" : $"completed ({MarketErrorCodes.ToCode(reason)}): {summary}");

            var delivered = job.Lines.Count(l => l.CurrentOutcome == SuitTransferOutcome.Delivered);
            TellSafe(job.Character, $"{(job.HolderOnly ? "Transfer" : "Vault transfer")} finished: {delivered} of {job.Lines.Length} line{(job.Lines.Length == 1 ? "" : "s")} delivered.");
        }

        /// <summary>
        /// A chat line that can never throw out of the service: every one of these runs on a world or action queue,
        /// several under the gate lock after the job's state is already set. Report's per-unit line is the one
        /// exception, guarded by its own catch so the delivery outcome stands.
        /// </summary>
        private void TellSafe(SuitTransferCharacter character, string text)
        {
            try
            {
                world.Tell(character, text);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] telling 0x{character?.CharacterGuid:X8} a transfer line threw: {ex.GetFullMessage()}");
            }
        }

        private void FinishLocked(Job job, string summary)
        {
            job.FinishedAt = world.UtcNow;

            if (activeByAccount.TryGetValue(job.AccountId, out var active) && active == job.Id)
                activeByAccount.Remove(job.AccountId);

            log.Info($"[SUIT] transfer {job.Id} account {job.AccountId} char 0x{job.CharacterGuid:X8} items {job.Lines.Length} -> {summary}");
        }

        /// <summary>
        /// Closes a queued transfer the world never started and a running one that stopped making progress, and
        /// forgets finished transfers (and their idempotency keys) older than <see cref="Retention"/>.
        /// </summary>
        private void ExpireLocked(DateTime now)
        {
            List<string> forget = null;

            foreach (var job in jobs.Values)
            {
                if (job.State == SuitTransferState.Queued && now - job.CreatedAt > QueuedTimeout)
                {
                    RejectLocked(job, MarketError.ServerError, "The world did not start the transfer in time. Nothing was moved.");
                }
                else if (job.State == SuitTransferState.Running && !job.InStep && now - job.LastProgressAt > StallTimeout)
                {
                    log.Warn($"[SUIT] transfer {job.Id} account {job.AccountId} made no progress for {StallTimeout.TotalSeconds:N0} s (the character's action chain stopped); closing it.");

                    // A holder unit still waiting on the shard queue is abandoned first (its late callback is a no-op).
                    AbandonHolderPendingLocked(job);

                    CompleteLocked(job, MarketError.CharacterOffline, "The transfer stopped before this was moved.");
                }

                if (job.FinishedAt != null && now - job.FinishedAt.Value > Retention)
                    (forget ??= new List<string>()).Add(job.Id);
            }

            if (forget == null)
                return;

            foreach (var id in forget)
            {
                var job = jobs[id];
                jobs.Remove(id);
                idempotency.Remove((job.AccountId, job.IdempotencyKey));
            }
        }

        private sealed class Job
        {
            public string Id;
            public uint AccountId;
            public uint CharacterGuid;
            public string IdempotencyKey;
            public bool AllowListed;
            public SuitTransferCharacter Character;
            public LineState[] Lines;
            public SuitTransferState State;
            public MarketError Reason;
            public DateTime CreatedAt;
            public DateTime LastProgressAt;
            public DateTime? FinishedAt;
            public bool InStep;

            /// <summary>The one-time shared-store load probe (SharedLoadProbeSeconds) has run.</summary>
            public bool SharedLoadProbed;

            /// <summary>A warm-queue refusal has been logged for this transfer (log once).</summary>
            public bool WarmRefusalLogged;

            /// <summary>Line index per single-object withdraw still to run, in request order. Null until planned.</summary>
            public Queue<int> Units;

            /// <summary>The holder unit waiting on the shard queue (a read or a save), or null. Replaced, never reused.</summary>
            public HolderPending Pending;

            /// <summary>Every line is a holder line (no vault involved): the gates' refusals are worded for moving between characters.</summary>
            public bool HolderOnly => Lines != null && Lines.Length > 0 && Lines.All(l => l.IsHolder);
        }

        private sealed class LineState
        {
            public LineState(SuitTransferLine request)
            {
                Request = request;
                Units = request.ItemGuid != null ? 1 : request.Count;
            }

            public SuitTransferLine Request { get; }

            /// <summary>Single-object withdraws this line takes: 1 for a stored item, its count otherwise.</summary>
            public int Units { get; }

            /// <summary>A line naming a shared mule (a vault_key), as opposed to the caller's own vault.</summary>
            public bool IsShared => Request.SharedGrantId != null;

            /// <summary>A line naming another character's pack (a holder_guid). Touches no vault.</summary>
            public bool IsHolder => Request.HolderGuid != null;

            /// <summary>A holder line's character name, from the roster at submit; null falls back to "that character".</summary>
            public string HolderName;

            /// <summary>
            /// The account whose STORE this line reads and withdraws from: the caller's for an own line, the grant
            /// owner's for a shared line once Begin has re-resolved its grant. Every store-scoped call uses this,
            /// never Job.AccountId.
            /// </summary>
            public uint StoreAccountId;

            /// <summary>A shared line's granter character name (VaultGranterName), or null for the fallback wording.</summary>
            public string MuleName;

            public int Delivered;
            public SuitTransferOutcome? Final;
            public string Message;

            public SuitTransferOutcome CurrentOutcome => Final ?? SuitTransferOutcome.Pending;
        }
    }
}
