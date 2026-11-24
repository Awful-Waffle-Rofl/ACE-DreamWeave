using System;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.SalvageRoller
{
    /// <summary>
    /// Everything the batch state machine touches outside itself, for ONE character: the shard rows, the audit table,
    /// the account's escrow store, the character's pack, item saves, and the player's screen. Production is
    /// <see cref="PlayerSalvageRollerHost"/>; the fault-injection tests drive the same state machine through a fake,
    /// because a Player cannot be built under the test harness.
    /// </summary>
    internal interface ISalvageRollerHost
    {
        uint CharacterId { get; }

        uint AccountId { get; }

        string CharacterName { get; }

        /// <summary>The account's vault store, which owns the escrow containers. Null when it cannot be had.</summary>
        AccountVaultStore GetStore();

        // ---- the shard rows (ShardDatabase_SalvageRoller.cs) ----

        /// <summary>False = the read failed. True with null = the character has no batch.</summary>
        bool TryReadBatch(out CharacterSalvageBatch batch);

        bool AddBatch(CharacterSalvageBatch batch);

        bool TryCommitPending(ulong batchId, string payload);

        bool TrySetTaking(ulong batchId, int seq);

        bool DeleteBatchItem(uint guid);

        /// <summary>Deletes the batch and every item row it still has. Only after the delivery saves have landed.</summary>
        bool DeleteBatch(ulong batchId);

        bool AddAudit(SalvageRollerAudit row);

        /// <summary>The detail of this batch's most recent audit row of <paramref name="ev"/>, or null. False when the read failed.</summary>
        bool TryReadAuditDetail(ulong batchId, SalvageRollerAuditEvent ev, out string detail);

        // ---- the pack and item persistence ----

        /// <summary>The object with this guid in the character's own inventory (pack, side packs, equipped), or null.</summary>
        WorldObject FindInPack(uint guid);

        bool HasPackRoomFor(WorldObject item);

        /// <summary>Adds an unparented object to the pack and tells the client. False, having moved nothing, when it does not fit.</summary>
        bool TryPutInPack(WorldObject item);

        /// <summary>Saves the item. <paramref name="landed"/> is told whether the write reached the shard; it may run later, on another thread.</summary>
        void SaveItem(WorldObject item, Action<bool> landed);

        /// <summary>
        /// What the shard holds for this guid, read fresh (never from the biota cache):
        ///   Absent     - no biota row at all;
        ///   Loose      - a biota with no container, no wielder and no Location, rehydrated into <paramref name="loose"/>:
        ///                the item a crash left between the sale's detach save and its escrow save;
        ///   Held       - a biota something holds: <paramref name="containerId"/> names its container when it has one, and
        ///                <paramref name="holder"/> says what holds it (a container, a wielder, or a landblock Location).
        ///                It is never rehydrated, because its holder loads it and a second live copy must never exist;
        ///   Unreadable - the read failed, which says nothing about whether it exists.
        /// </summary>
        ShardBiotaState LookUpShard(uint guid, out WorldObject loose, out uint? containerId, out string holder);

        /// <summary>
        /// Removes the shard biota with this guid when no live object for it is in reach (a Hammer still recorded in an
        /// escrow container after its in-memory copy was destroyed). <paramref name="landed"/> hears whether the removal
        /// reached the shard; a row that is already gone counts as removed.
        /// </summary>
        void RemoveShardBiota(uint guid, Action<bool> landed);

        /// <summary>
        /// Destroys a batch item that is NOT in escrow: a Hammer found in the pack (<paramref name="inPack"/>), held in
        /// memory, or loaded from the shard. <paramref name="landed"/> hears whether the biota's removal reached the shard.
        /// </summary>
        void DestroyItem(WorldObject item, bool inPack, Action<bool> landed);

        /// <summary>Runs <paramref name="action"/> on the character's own thread (the player's action queue).</summary>
        void RunOnOwnerThread(Action action);

        // ---- the screen ----

        void Chat(string text);

        void Tell(string text);

        /// <summary>
        /// Opens the forced-pick confirmation for <paramref name="batchId"/>. Its answer reaches
        /// <see cref="SalvageRollerManager.OnRollAnswered(ISalvageRollerHost, ulong, bool)"/>. False when it could not be
        /// shown (another confirmation of the same kind is open).
        /// </summary>
        bool AskToRoll(ulong batchId, string prompt);

        /// <summary>After a take: the weapon tier transition (aura plus announcement) when <paramref name="tierBefore"/> is set.</summary>
        void AfterTake(WorldObject item, WeaponQualityTier? tierBefore);
    }

    /// <summary>What the shard holds for a batch item's guid. See <see cref="ISalvageRollerHost.LookUpShard"/>.</summary>
    internal enum ShardBiotaState { Absent, Loose, Held, Unreadable }

    /// <summary>The production host: one online Player, the real shard, the real account store.</summary>
    internal sealed class PlayerSalvageRollerHost : ISalvageRollerHost
    {
        private static readonly ShardAccountVaultWorldSource World = new ShardAccountVaultWorldSource();

        private readonly Player player;

        public PlayerSalvageRollerHost(Player player)
        {
            this.player = player ?? throw new ArgumentNullException(nameof(player));
        }

        public Player Player => player;

        public uint CharacterId => player.Guid.Full;

        public uint AccountId => player.Account?.AccountId ?? 0;

        public string CharacterName => player.Name;

        public AccountVaultStore GetStore() => AccountId == 0 ? null : AccountVaultManager.GetStore(AccountId);

        private static ShardDatabase Db => DatabaseManager.Shard.BaseDatabase;

        public bool TryReadBatch(out CharacterSalvageBatch batch) => Db.TryGetSalvageBatchForCharacter(CharacterId, out batch);

        public bool AddBatch(CharacterSalvageBatch batch) => Db.AddSalvageBatch(batch);

        public bool TryCommitPending(ulong batchId, string payload) => Db.TryCommitSalvageBatchPending(batchId, payload);

        public bool TrySetTaking(ulong batchId, int seq) => Db.TrySetSalvageBatchTaking(batchId, seq);

        public bool DeleteBatchItem(uint guid) => Db.DeleteSalvageBatchItem(guid);

        public bool DeleteBatch(ulong batchId) => Db.DeleteSalvageBatch(batchId);

        public bool AddAudit(SalvageRollerAudit row) => Db.AddSalvageRollerAudit(row);

        public bool TryReadAuditDetail(ulong batchId, SalvageRollerAuditEvent ev, out string detail) => Db.TryGetLatestSalvageRollerAuditDetail(batchId, ev, out detail);

        public WorldObject FindInPack(uint guid) => player.FindObject(guid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems);

        public bool HasPackRoomFor(WorldObject item) => item != null && player.CanAddToInventory(item);

        /// <summary>
        /// The client may already hold this guid (the item was in the pack, often drawn, before it was sold), so the
        /// create rebuilds it and drops its emitters; TryCreateInInventoryWithNetworking forgets the sent record on
        /// success so the next equip re-sends the item's script (tier aura or base). Nothing is sent here: the item
        /// is in the pack.
        /// </summary>
        public bool TryPutInPack(WorldObject item) => player != null && item != null && player.TryCreateInInventoryWithNetworking(item);

        /// <summary>The vault's own save with a completion signal (ShardAccountVaultWorldSource.SaveBiota): the same bookkeeping as an ordinary save.</summary>
        public void SaveItem(WorldObject item, Action<bool> landed) => World.SaveBiota(item, landed);

        public ShardBiotaState LookUpShard(uint guid, out WorldObject loose, out uint? containerId, out string holder)
        {
            loose = null;
            containerId = null;
            holder = null;

            // A fresh context and the non-virtual read (GetBiotaCore), never the cached GetBiota: reconcile must see
            // what the shard holds NOW. The EF biota is converted by the factory's EF overload
            // (BiotaConverter.ConvertToEntityBiota).
            ACE.Database.Models.Shard.Biota biota;

            try
            {
                biota = Db.GetSalvageRollerBiotaUncached(guid);
            }
            catch (Exception ex)
            {
                SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} 0x{guid:X8}: the shard read failed: {ex.Message}");
                return ShardBiotaState.Unreadable;
            }

            if (biota == null)
                return ShardBiotaState.Absent;

            var item = WorldObjectFactory.CreateWorldObject(biota);

            if (item == null)
            {
                SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} 0x{guid:X8} has a shard biota that did not rehydrate; it was not loaded.");
                return ShardBiotaState.Unreadable;
            }

            // Somewhere real holds it (a container's load, or its landblock, brings it in); a second live copy must never
            // be made. The caller decides whether that is expected (a Hammer whose removal is still queued) or not.
            if (!SalvageRollerManager.IsLoose(item, out holder))
            {
                containerId = item.ContainerId;
                return ShardBiotaState.Held;
            }

            loose = item;
            return ShardBiotaState.Loose;
        }

        /// <summary>Queued on the shard's FIFO worker, so it runs after any save or removal already queued for this guid.</summary>
        public void RemoveShardBiota(uint guid, Action<bool> landed) => DatabaseManager.Shard.RemoveBiota(guid, landed);

        public void DestroyItem(WorldObject item, bool inPack, Action<bool> landed)
        {
            if (item == null)
            {
                landed?.Invoke(false);
                return;
            }

            if (inPack && !player.TryRemoveFromInventoryWithNetworking(item.Guid, out _, Player.RemoveFromInventoryAction.ConsumeItem))
            {
                SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} 0x{item.Guid.Full:X8} ({item.Name}) could not be removed from {player.Name}'s pack to be consumed; it was not destroyed.");
                landed?.Invoke(false);
                return;
            }

            World.DestroyItem(item, landed);
        }

        public void RunOnOwnerThread(Action action) => player.EnqueueAction(new ActionEventDelegate(action));

        public void Chat(string text) => SalvageRollerManager.Chat(player, text);

        public void Tell(string text)
        {
            if (player.Session == null || string.IsNullOrEmpty(text))
                return;

            player.SendTransientError(text);
        }

        public bool AskToRoll(ulong batchId, string prompt)
        {
            return player.ConfirmationManager.EnqueueSend(new Confirmation_SalvageRollerRoll(player.Guid, batchId, DateTime.UtcNow), prompt);
        }

        public void AfterTake(WorldObject item, WeaponQualityTier? tierBefore)
        {
            if (tierBefore.HasValue)
                WeaponModManager.HandleTierTransition(player, item, tierBefore.Value);
        }
    }

    /// <summary>
    /// The forced-pick confirmation (DESIGN 5.4 step 4.4). Confirmation_Custom's shape and type (Yes_No), but it reports
    /// EVERY answer: Custom returns early on No and never hears a timeout, while here No and timeout must both abort
    /// the batch and hand everything back.
    ///
    /// The answer is run on the player's own action queue, as Confirmation_ThreadGroupStart does, and a timeout or an
    /// answer at or after Confirmation_ThreadGroupStart.LateAfter counts as No (ConfirmationManager's 30 s abort leaves
    /// the entry and the client's automatic reply then arrives as a plain No). A player who has logged out cannot be
    /// handed anything; the batch is then left for reconcile at the next station Use.
    /// </summary>
    public sealed class Confirmation_SalvageRollerRoll : Confirmation
    {
        public readonly ulong BatchId;

        public readonly DateTime SentUtc;

        public Confirmation_SalvageRollerRoll(ObjectGuid playerGuid, ulong batchId, DateTime sentUtc)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            BatchId = batchId;
            SentUtc = sentUtc;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;

            if (player == null)
            {
                SalvageRollerManager.OnRollAbandoned(BatchId);
                return;
            }

            var yes = response && !Confirmation_ThreadGroupStart.IsLate(SentUtc, DateTime.UtcNow, timeout);

            player.EnqueueAction(new ActionEventDelegate(() => SalvageRollerManager.OnRollAnswered(player, BatchId, yes)));
        }
    }
}
