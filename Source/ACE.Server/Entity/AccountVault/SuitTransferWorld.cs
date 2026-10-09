using System;

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
        /// </summary>
        internal static MarketError Decide(in SuitTransferGateState state, out string message)
        {
            message = null;

            if (state.InPvpMatch)
            {
                message = "You cannot receive items from your vault during a PvP match.";
                return MarketError.InPvp;
            }

            if (state.PvpTemplateRefusal != null)
            {
                message = state.PvpTemplateRefusal;
                return MarketError.InPvp;
            }

            if (state.InArenaQueue)
            {
                message = ArenaQueueMessage;
                return MarketError.InPvp;
            }

            if (state.Busy || state.Trading || state.Dying || state.Teleporting)
            {
                message = "You are too busy to receive items from your vault right now. Try again in a moment.";
                return MarketError.CharacterBusy;
            }

            if (state.PkTimer)
            {
                message = "You have been in a player killer battle too recently to receive items from your vault.";
                return MarketError.CharacterBusy;
            }

            if (state.AtVaultVendor)
            {
                message = "Step away from your vault vendor before moving items from the suit builder.";
                return MarketError.VaultPanelOpen;
            }

            if (!state.InVaultArea)
            {
                message = $"You can only receive items from your vault in {(string.IsNullOrWhiteSpace(state.VaultAreaName) ? "the permitted area" : state.VaultAreaName)}.";
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

        /// <summary>The character-state gates (<see cref="SuitTransferGates.Decide"/> over the live state).</summary>
        MarketError CheckGates(SuitTransferCharacter character, out string message);

        /// <summary>AccountVaultManager.GetStore.</summary>
        AccountVaultStore GetStore(uint accountId);

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

        public MarketError CheckGates(SuitTransferCharacter character, out string message)
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

            return SuitTransferGates.Decide(state, out message);
        }

        public AccountVaultStore GetStore(uint accountId) => AccountVaultManager.GetStore(accountId);

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
    }
}
