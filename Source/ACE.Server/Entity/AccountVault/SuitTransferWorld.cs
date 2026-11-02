using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// The online character a suit transfer delivers to, as found on the request thread. The Session and
    /// Player are null in tests; only the live world source reads them.
    /// </summary>
    internal sealed class SuitTransferCharacter
    {
        public SuitTransferCharacter(uint accountId, uint characterGuid, string name, Session session, Player player)
        {
            AccountId = accountId;
            CharacterGuid = characterGuid;
            Name = name ?? string.Empty;
            Session = session;
            Player = player;
        }

        public uint AccountId { get; }
        public uint CharacterGuid { get; }
        public string Name { get; }
        public Session Session { get; }
        public Player Player { get; }
    }

    /// <summary>
    /// One reading of the character-state gates, as plain values, so the order and the wording are a
    /// pure decision ACE.Server.Tests can call without a live Player. The live world source fills it from
    /// the same members the facet switch reads (Player_Facets.CheckFacetGates).
    /// </summary>
    internal readonly struct SuitTransferGateState
    {
        public SuitTransferGateState(bool busy, bool trading, bool dying, bool teleporting, bool pkTimer,
                                     bool atVaultVendor, bool inVaultArea, string vaultAreaName,
                                     bool inPvpMatch = false, string pvpTemplateRefusal = null, bool inArenaQueue = false)
        {
            InPvpMatch = inPvpMatch;
            InArenaQueue = inArenaQueue;
            PvpTemplateRefusal = pvpTemplateRefusal;
            Busy = busy;
            Trading = trading;
            Dying = dying;
            Teleporting = teleporting;
            PkTimer = pkTimer;
            AtVaultVendor = atVaultVendor;
            InVaultArea = inVaultArea;
            VaultAreaName = vaultAreaName;
        }

        /// <summary>Player.IsInPvpMatch, the facet switch's first gate (Player_Facets.cs, CheckFacetGates).</summary>
        public bool InPvpMatch { get; }

        /// <summary>Player.PvpTemplateBlocked(PvpTemplateAction.Vault): the refusal text while templated, else null. The facet switch's second gate, asked for the vault action.</summary>
        public string PvpTemplateRefusal { get; }

        /// <summary>PvpPlayerRules.RefusesFacetSwitchForArena(PvpMatchManager.Status(player).Kind): queued for, offered, or dispatched into an arena match. The facet switch's arena gate.</summary>
        public bool InArenaQueue { get; }

        public bool Busy { get; }
        public bool Trading { get; }
        public bool Dying { get; }
        public bool Teleporting { get; }
        public bool PkTimer { get; }

        /// <summary>The character's last-opened object is a PersonalVendor (the vault panel's proximity latch).</summary>
        public bool AtVaultVendor { get; }

        /// <summary>MuleSummonHandler.IsSummonPermitted for the character's location: denylist first, then allowlist.</summary>
        public bool InVaultArea { get; }

        public string VaultAreaName { get; }

        public static SuitTransferGateState Clear => new SuitTransferGateState(false, false, false, false, false, false, true, null);
    }

    internal static class SuitTransferGates
    {
        internal const string ArenaQueueMessage = "You cannot receive items from your vault while queued for or entering an arena match. Use /arena leave first.";

        /// <summary>The arena-queue refusal for a transfer made only of holder lines (no vault involved).</summary>
        internal const string ArenaQueueCharacterMoveMessage = "You cannot move items between your characters while queued for or entering an arena match. Use /arena leave first.";

        /// <summary>
        /// The character-state gates a transfer runs before it touches the vault, in order:
        ///   0. PvP - in a PvP match, then a PvP template, then queued for or entering an arena match - the facet
        ///      switch's PvP gates, first here too, so a
        ///      PvP refusal is never masked by a busy one (the arena moves and teleports its players);
        ///   1. busy / trading / dying / teleporting - the facet switch's busy gate (Player_Facets.cs, CheckFacetGates);
        ///   2. the PK timer - the facet switch's next gate;
        ///   3. standing at a vault vendor - the facet switch's PersonalVendor carve-out, for the same reason:
        ///      the vendor is a second view onto the same vault, and two paths moving vault state at once
        ///      has not been traced end to end;
        ///   4. the vault area - MuleSummonHandler.IsSummonPermitted, exactly as the summon applies it.
        /// Returns <see cref="MarketError.None"/> when every gate passes.
        ///
        /// <paramref name="characterMove"/> is true for a transfer made ONLY of holder lines (items from the player's other
        /// characters, no vault). The gates are the same - the vault area and the vendor gate are the safe-zone rule for
        /// any suit transfer - but the wording never says "from your vault".
        /// </summary>
        internal static MarketError Decide(in SuitTransferGateState state, out string message, bool characterMove = false)
        {
            message = null;

            if (state.InPvpMatch)
            {
                message = characterMove
                    ? "You cannot move items between your characters during a PvP match."
                    : "You cannot receive items from your vault during a PvP match.";
                return MarketError.InPvp;
            }

            if (state.PvpTemplateRefusal != null)
            {
                message = state.PvpTemplateRefusal;
                return MarketError.InPvp;
            }

            if (state.InArenaQueue)
            {
                message = characterMove ? ArenaQueueCharacterMoveMessage : ArenaQueueMessage;
                return MarketError.InPvp;
            }

            if (state.Busy || state.Trading || state.Dying || state.Teleporting)
            {
                message = characterMove
                    ? "You are too busy to receive items from your other characters right now. Try again in a moment."
                    : "You are too busy to receive items from your vault right now. Try again in a moment.";
                return MarketError.CharacterBusy;
            }

            if (state.PkTimer)
            {
                message = characterMove
                    ? "You have been in a player killer battle too recently to move items between your characters."
                    : "You have been in a player killer battle too recently to receive items from your vault.";
                return MarketError.CharacterBusy;
            }

            if (state.AtVaultVendor)
            {
                message = "Step away from your vault vendor before moving items from the suit builder.";
                return MarketError.VaultPanelOpen;
            }

            if (!state.InVaultArea)
            {
                var area = string.IsNullOrWhiteSpace(state.VaultAreaName) ? "the permitted area" : state.VaultAreaName;

                message = characterMove
                    ? $"You can only move items between your characters while you are in {area}. Go there and try again."
                    : $"You can only receive items from your vault in {area}.";
                return MarketError.NotInVaultArea;
            }

            return MarketError.None;
        }
    }

    /// <summary>
    /// Everything <see cref="SuitTransferService"/> needs from the running server, behind a seam so tests
    /// need no live Player, Session or world loop (the IWebCommandWorld / IAccountVaultWorldSource shape).
    /// </summary>
    internal interface ISuitTransferWorld
    {
        DateTime UtcNow { get; }

        /// <summary>
        /// The account's WorldConnected session, when its Player is <paramref name="characterGuid"/> and is
        /// not logging out; otherwise null. May throw (NetworkManager.Find uses SingleOrDefault).
        /// </summary>
        SuitTransferCharacter FindOnlineCharacter(uint accountId, uint characterGuid);

        /// <summary>The same session still holds the same Player, WorldConnected, not logging out.</summary>
        bool IsStillEligible(SuitTransferCharacter character);

        /// <summary>NetworkManager.InboundMessageQueue, the queue WebCommandDispatcher runs character work from.</summary>
        void EnqueueWorld(Action work);

        /// <summary>An ActionChain on the character: <paramref name="work"/> runs on its action queue after the delay.</summary>
        void Schedule(SuitTransferCharacter character, double delaySeconds, Action work);

        /// <summary>The character-state gates (<see cref="SuitTransferGates.Decide"/> over the live state). <paramref name="characterMove"/>: holder-only wording.</summary>
        MarketError CheckGates(SuitTransferCharacter character, out string message, bool characterMove = false);

        /// <summary>AccountVaultManager.GetStore. Called with the LINE's store account (the grant owner's for a shared line).</summary>
        AccountVaultStore GetStore(uint accountId);

        /// <summary>
        /// AccountVaultManager.GetGrantsForGrantee: every grant naming this character as grantee. NULL means the
        /// read FAILED, never "nothing is shared". Its own short-lived DbContext, so safe on any thread.
        /// </summary>
        List<ACE.Database.Models.Shard.AccountVaultGrant> GetGrantsForGrantee(uint granteeCharacterGuid);

        /// <summary>VaultGranterName.Resolve: the granter character's name, or null for the fallback wording.</summary>
        string GranterName(ACE.Database.Models.Shard.AccountVaultGrant grant);

        /// <summary>AccountVaultManager.RequestWarm: queue a store for a rate-limited load on the world heartbeat (which builds it if none is live), instead of loading it here. False when the queue refused it.</summary>
        bool RequestWarm(uint accountId);

        /// <summary>AccountVaultManager.TryGetExistingStore: the store only if one is ALREADY LIVE, else null. Never builds or loads one.</summary>
        AccountVaultStore GetExistingStore(uint accountId);

        /// <summary>Free item slots in the MAIN pack only (side packs excluded).</summary>
        int FreeMainPackSlots(SuitTransferCharacter character);

        /// <summary>Free container (side-pack) slots, for an object that takes a backpack slot.</summary>
        int FreeContainerSlots(SuitTransferCharacter character);

        /// <summary>Burden the character can still take before TryAddToInventory's burden check refuses.</summary>
        int AvailableBurden(SuitTransferCharacter character);

        /// <summary>EncumbranceVal of one unit of this wcid's template weenie, 0 when unknown.</summary>
        int TemplateBurden(uint wcid);

        /// <summary>The ACTIVE listing over this vault row (the vault view's own match), as its listed count; null when none.</summary>
        int? ListedCount(uint accountId, VaultEntry row);

        /// <summary>
        /// Reports a withdraw of one member of a stored GROUP under the group's representative guid, exactly
        /// as a panel withdraw from the group reports it (AccountVaultStore.PreWithdrawHook), because the
        /// per-member withdraw below names the member's own guid and would otherwise leave a listing over
        /// the group standing over a group that no longer backs it.
        /// </summary>
        void BeforeGroupMemberWithdraw(uint accountId, uint representativeGuid, uint wcid);

        /// <summary>VaultPackDelivery.WithdrawToPack for the live Player. NEVER called from the store's mutation queue.</summary>
        VaultPackDeliveryResult WithdrawToPack(SuitTransferCharacter character, AccountVaultStore store, VaultEntry entry, int amount, bool isLedger);

        /// <summary>One system chat line to the character, if it is still connected.</summary>
        void Tell(SuitTransferCharacter character, string text);

        // ---- holder lines: an item moved out of another (offline) character's pack on the same account ----

        /// <summary>The suit_character_moves_enabled kill switch. Off: the inventory reports character_moves false and holder lines are refused.</summary>
        bool CharacterMovesEnabled { get; }

        /// <summary>
        /// The roster check every account route uses (a character of THIS account that is neither deleted nor pending
        /// deletion). FAILS CLOSED: an unresolvable roster answers false. <paramref name="name"/> is the character's name.
        /// </summary>
        bool TryResolveRosterCharacter(uint accountId, uint characterGuid, out string name);

        /// <summary>What PlayerManager knows about the holder right now. Safe from any thread (PlayerManager's own lock).</summary>
        SuitHolderState GetHolderState(uint holderGuid);

        /// <summary>SuitHolderMoveGuard.TryClaimForMove: false while the holder is entering the world, being moved from, or pinned.</summary>
        bool TryClaimHolder(uint holderGuid);

        /// <summary>SuitHolderMoveGuard.ReleaseMove.</summary>
        void ReleaseHolder(uint holderGuid);

        /// <summary>SuitHolderMoveGuard.PinUnconfirmed: the holder may not enter the world until a restart (or a late confirmed save).</summary>
        void PinHolder(uint holderGuid);

        /// <summary>
        /// Queues a FRESH read of the holder's possessions on the shard queue (GetPossessedBiotasInParallel, the call
        /// login uses) and answers <paramref name="callback"/> ON THE SHARD WORKER with where the item is, converted to
        /// the runtime biota there (<see cref="SuitHolderRead.FromPossessions"/>). May never call back.
        /// </summary>
        void ReadHolderItem(uint holderGuid, uint itemGuid, Action<SuitHolderRead> callback);

        /// <summary>WorldObjectFactory.CreateWorldObject over the runtime biota the read produced. Registers nothing anywhere.</summary>
        WorldObject CreateHolderObject(ACE.Entity.Models.Biota biota);

        /// <summary>Player.TryCreateInInventoryWithNetworking (which also queues its own save of the item).</summary>
        bool AddToPack(SuitTransferCharacter character, WorldObject item);

        /// <summary>
        /// Queues one more save of the item on the shard queue with a callback, so the move can wait for the database to
        /// confirm it. The callback runs on the shard worker and may never come.
        /// </summary>
        void SaveHolderItem(WorldObject item, Action<bool> callback);
    }

    /// <summary>What PlayerManager knows about a holder character at one moment.</summary>
    internal readonly struct SuitHolderState
    {
        public SuitHolderState(bool exists, uint accountId, bool deleted, bool online, string name)
        {
            Exists = exists;
            AccountId = accountId;
            Deleted = deleted;
            Online = online;
            Name = name;
        }

        public bool Exists { get; }
        public uint AccountId { get; }

        /// <summary>Deleted or pending deletion.</summary>
        public bool Deleted { get; }

        public bool Online { get; }
        public string Name { get; }
    }

    internal enum SuitHolderReadStatus
    {
        /// <summary>The item is in the holder's main pack or one of its side packs; <see cref="SuitHolderRead.ItemBiota"/> is set.</summary>
        Found,

        /// <summary>The item is not among the holder's possessions (moved, dropped, traded, destroyed, in a pack inside a pack).</summary>
        NotFound,

        /// <summary>The holder is wearing it (a Wielder link). Worn gear is out of scope.</summary>
        Worn,

        /// <summary>The read or the conversion threw. Nothing is known; nothing moves.</summary>
        Failed,
    }

    /// <summary>One holder read's answer, produced on the shard worker.</summary>
    internal sealed class SuitHolderRead
    {
        public SuitHolderReadStatus Status { get; init; }

        /// <summary>The item's RUNTIME biota (ACE.Entity.Models.Biota), converted on the worker. Set only for Found.</summary>
        public ACE.Entity.Models.Biota ItemBiota { get; init; }

        public string Error { get; init; }

        public static SuitHolderRead Failed(string error) => new SuitHolderRead { Status = SuitHolderReadStatus.Failed, Error = error };

        /// <summary>
        /// Where <paramref name="itemGuid"/> sits among a holder's possessions, as GetPossessedBiotasInParallel returns them
        /// (EF entities: ACE.Database.Models.Shard.Biota). A Wielder link is Worn. In the inventory the item's Container
        /// must be the holder itself or one of the holder's own side packs (a Container-type biota whose Container is the
        /// holder); anything deeper is NotFound. Only the found item is converted, with <paramref name="convert"/>
        /// (BiotaConverter.ConvertToEntityBiota in production), so no EF entity leaves the worker.
        /// </summary>
        public static SuitHolderRead FromPossessions(uint holderGuid, uint itemGuid,
            IEnumerable<ACE.Database.Models.Shard.Biota> inventory, IEnumerable<ACE.Database.Models.Shard.Biota> wielded,
            Func<ACE.Database.Models.Shard.Biota, ACE.Entity.Models.Biota> convert)
        {
            foreach (var worn in wielded ?? Array.Empty<ACE.Database.Models.Shard.Biota>())
            {
                if (worn != null && worn.Id == itemGuid)
                    return new SuitHolderRead { Status = SuitHolderReadStatus.Worn };
            }

            var items = (inventory ?? Array.Empty<ACE.Database.Models.Shard.Biota>()).Where(b => b != null).ToList();

            var sidePacks = new HashSet<uint>(items
                .Where(b => b.WeenieType == (int)WeenieType.Container && ContainerOf(b) == holderGuid)
                .Select(b => b.Id));

            foreach (var biota in items)
            {
                if (biota.Id != itemGuid)
                    continue;

                var container = ContainerOf(biota);

                if (container != holderGuid && (container == null || !sidePacks.Contains(container.Value)))
                    return new SuitHolderRead { Status = SuitHolderReadStatus.NotFound };

                // A Wielder link on an inventory row as well is not a pack item either.
                if (IidOf(biota, PropertyInstanceId.Wielder) is uint wielder && wielder != 0)
                    return new SuitHolderRead { Status = SuitHolderReadStatus.Worn };

                return new SuitHolderRead { Status = SuitHolderReadStatus.Found, ItemBiota = convert(biota) };
            }

            return new SuitHolderRead { Status = SuitHolderReadStatus.NotFound };
        }

        private static uint? ContainerOf(ACE.Database.Models.Shard.Biota biota) => IidOf(biota, PropertyInstanceId.Container);

        private static uint? IidOf(ACE.Database.Models.Shard.Biota biota, PropertyInstanceId type)
        {
            var row = biota.BiotaPropertiesIID?.FirstOrDefault(p => p.Type == (ushort)type);
            return row?.Value;
        }
    }

    internal sealed class LiveSuitTransferWorld : ISuitTransferWorld
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static readonly LiveSuitTransferWorld Instance = new LiveSuitTransferWorld();

        private LiveSuitTransferWorld()
        {
        }

        public DateTime UtcNow => DateTime.UtcNow;

        public SuitTransferCharacter FindOnlineCharacter(uint accountId, uint characterGuid)
        {
            var session = NetworkManager.Find(accountId);

            if (session == null || session.State != SessionState.WorldConnected)
                return null;

            var player = session.Player;

            if (player == null || player.IsLoggingOut || player.Guid.Full != characterGuid)
                return null;

            return new SuitTransferCharacter(accountId, characterGuid, player.Name, session, player);
        }

        public bool IsStillEligible(SuitTransferCharacter character)
        {
            var session = character?.Session;
            var player = character?.Player;

            return session != null && player != null
                && session.State == SessionState.WorldConnected
                && ReferenceEquals(session.Player, player)
                && !player.IsLoggingOut
                && player.Guid.Full == character.CharacterGuid;
        }

        public void EnqueueWorld(Action work) => NetworkManager.InboundMessageQueue.EnqueueAction(new ActionEventDelegate(work));

        public void Schedule(SuitTransferCharacter character, double delaySeconds, Action work)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delaySeconds);
            chain.AddAction(character.Player, work);
            chain.EnqueueChain();
        }

        public MarketError CheckGates(SuitTransferCharacter character, out string message, bool characterMove = false)
        {
            var player = character.Player;

            // The facet switch's own resolution of LastOpenedContainerId (Player_Facets.cs, CheckFacetGates):
            // for a PersonalVendor the latch means "still standing at it", which is what is refused here.
            var lastOpened = player.LastOpenedContainerId != ObjectGuid.Invalid
                ? player.CurrentLandblock?.GetObject(player.LastOpenedContainerId)
                : null;

            var state = new SuitTransferGateState(
                busy: player.IsBusy,
                trading: player.IsTrading,
                dying: player.IsInDeathProcess,
                teleporting: player.Teleporting,
                pkTimer: player.PKTimerActive,
                atVaultVendor: lastOpened is PersonalVendor,
                inVaultArea: MuleSummonHandler.IsSummonPermitted(player.Location, out _),
                vaultAreaName: MuleSummonHandler.GetAllowlistName(),
                inPvpMatch: player.IsInPvpMatch,
                pvpTemplateRefusal: player.PvpTemplateBlocked(ACE.Server.Pvp.Templates.PvpTemplateAction.Vault),
                // Read here, on the character's action queue (a landblock thread), as Player_Facets.CheckFacetGates
                // reads it from its vault-poll re-entry: every write to the coordinator's state is on the world thread.
                inArenaQueue: ACE.Server.Pvp.PvpPlayerRules.RefusesFacetSwitchForArena(ACE.Server.Pvp.PvpMatchManager.Status(player).Kind));

            return SuitTransferGates.Decide(state, out message, characterMove);
        }

        public AccountVaultStore GetStore(uint accountId) => AccountVaultManager.GetStore(accountId);

        public List<ACE.Database.Models.Shard.AccountVaultGrant> GetGrantsForGrantee(uint granteeCharacterGuid) => AccountVaultManager.GetGrantsForGrantee(granteeCharacterGuid);

        public string GranterName(ACE.Database.Models.Shard.AccountVaultGrant grant) => VaultGranterName.Resolve(grant);

        public bool RequestWarm(uint accountId) => AccountVaultManager.RequestWarm(accountId);

        public AccountVaultStore GetExistingStore(uint accountId) => AccountVaultManager.TryGetExistingStore(accountId, out var live) ? live : null;

        public int FreeMainPackSlots(SuitTransferCharacter character) => character.Player.GetFreeInventorySlots(includeSidePacks: false);

        public int FreeContainerSlots(SuitTransferCharacter character) => character.Player.GetFreeContainerSlots();

        public int AvailableBurden(SuitTransferCharacter character) => character.Player.GetAvailableBurden();

        public int TemplateBurden(uint wcid)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

            if (weenie?.PropertiesInt != null && weenie.PropertiesInt.TryGetValue(PropertyInt.EncumbranceVal, out var burden))
                return Math.Max(0, burden);

            return 0;
        }

        public int? ListedCount(uint accountId, VaultEntry row) => MarketManager.SuitListedCount(accountId, row);

        public void BeforeGroupMemberWithdraw(uint accountId, uint representativeGuid, uint wcid)
        {
            var hook = AccountVaultStore.PreWithdrawHook;

            if (hook == null)
                return;

            try
            {
                hook(accountId, representativeGuid, wcid, 1, null);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] the pre-withdraw hook threw for a group member of account {accountId}, representative 0x{representativeGuid:X8}: {ex.GetFullMessage()}");
            }
        }

        public VaultPackDeliveryResult WithdrawToPack(SuitTransferCharacter character, AccountVaultStore store, VaultEntry entry, int amount, bool isLedger)
            => VaultPackDelivery.WithdrawToPack(store, entry, amount, isLedger, character.Player);

        public void Tell(SuitTransferCharacter character, string text)
            => character?.Player?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        // ---- holder lines ----

        public bool CharacterMovesEnabled => PropertyManager.GetBool("suit_character_moves_enabled").Item;

        public bool TryResolveRosterCharacter(uint accountId, uint characterGuid, out string name)
        {
            name = string.Empty;

            if (accountId == 0 || characterGuid == 0)
                return false;

            // The same roster MarketApiHost.TryResolveCharacter reads (Program.cs wires ListCharacters to this snapshot
            // with the same deleted / pending-deletion filter).
            foreach (var p in PlayerManager.GetAccountPlayersSnapshot(accountId))
            {
                if (p == null || p.Guid.Full != characterGuid || p.IsDeleted || p.IsPendingDeletion)
                    continue;

                name = p.Name ?? string.Empty;
                return true;
            }

            return false;
        }

        public SuitHolderState GetHolderState(uint holderGuid)
        {
            var player = PlayerManager.FindByGuid(holderGuid, out var online);

            if (player == null)
                return new SuitHolderState(false, 0, false, false, null);

            return new SuitHolderState(true, player.Account?.AccountId ?? 0, player.IsDeleted || player.IsPendingDeletion, online, player.Name);
        }

        public bool TryClaimHolder(uint holderGuid) => SuitHolderMoveGuard.TryClaimForMove(holderGuid);

        public void ReleaseHolder(uint holderGuid) => SuitHolderMoveGuard.ReleaseMove(holderGuid);

        public void PinHolder(uint holderGuid) => SuitHolderMoveGuard.PinUnconfirmed(holderGuid);

        public void ReadHolderItem(uint holderGuid, uint itemGuid, Action<SuitHolderRead> callback)
        {
            DatabaseManager.Shard.GetPossessedBiotasInParallel(holderGuid, possessions =>
            {
                // ON THE SHARD WORKER. GetPossessionsCore reads NoTracking through its own context and never puts the
                // rows in the biota cache (ProfileBuilder's remarks), and only the one found row is converted here, so
                // no EF entity leaves this callback. A throw is answered as Failed, never swallowed into a lost callback.
                SuitHolderRead result;

                try
                {
                    result = possessions == null
                        ? SuitHolderRead.Failed("no possessions were returned")
                        : SuitHolderRead.FromPossessions(holderGuid, itemGuid, possessions.Inventory, possessions.WieldedItems,
                            b => ACE.Database.Adapter.BiotaConverter.ConvertToEntityBiota(b));
                }
                catch (Exception ex)
                {
                    log.Error($"[SUIT] reading holder 0x{holderGuid:X8}'s possessions for 0x{itemGuid:X8} threw: {ex.GetFullMessage()}");
                    result = SuitHolderRead.Failed(ex.Message);
                }

                callback(result);
            });
        }

        public WorldObject CreateHolderObject(ACE.Entity.Models.Biota biota) => ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(biota);

        public bool AddToPack(SuitTransferCharacter character, WorldObject item) => character.Player.TryCreateInInventoryWithNetworking(item);

        public void SaveHolderItem(WorldObject item, Action<bool> callback)
            => DatabaseManager.Shard.SaveBiota(item.Biota, item.BiotaDatabaseLock, callback);
    }
}
