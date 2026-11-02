using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Database.Adapter;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// The live <see cref="IPvpTemplateKitHost"/> for /pvptemplate kit.
    ///
    /// Login claim: SuitHolderMoveGuard, the suit builder's holder-move guard (TryClaimForMove / ReleaseMove), so a login of the
    /// character is refused while the edit holds it, and the edit is refused while a login is entering.
    ///
    /// Biota types: the possession read hands back EF entities (ACE.Database.Models.Shard.Biota); each one is converted on the shard
    /// worker with BiotaConverter.ConvertToEntityBiota and only the planner's plain rows leave that callback. New objects are built
    /// with WorldObjectFactory.CreateNewWorldObject and saved with SerializedShardDatabase.SaveBiota (the runtime
    /// ACE.Entity.Models.Biota and its lock), and removed with RemoveBiotasInParallel (ids; one transaction of set-based deletes).
    /// No raw SQL.
    /// </summary>
    internal sealed class PvpTemplateKitLiveHost : IPvpTemplateKitHost
    {
        public string TemplateAccount => PvpTemplateSettings.AccountSource();

        public PvpKitCharacter FindCharacter(string name)
        {
            var player = PlayerManager.FindByName(name);

            if (player == null)
                return null;

            return new PvpKitCharacter
            {
                Guid = player.Guid.Full,
                Name = player.Name,
                AccountName = player.Account?.AccountName,
                Deleted = player.IsDeleted || player.IsPendingDeletion,
                HasRestoreRecord = !string.IsNullOrEmpty(player.GetProperty(PropertyString.PvpTemplateRestore)),
                MainPackCapacity = player.GetProperty(PropertyInt.ItemsCapacity) is int capacity && capacity > 0 ? capacity : PvpTemplateKitEdit.DefaultMainPackCapacity,
            };
        }

        public bool IsOnline(uint characterGuid) => PlayerManager.GetOnlinePlayer(characterGuid) != null;

        public bool TryClaim(uint characterGuid) => SuitHolderMoveGuard.TryClaimForMove(characterGuid);

        public void Release(uint characterGuid) => SuitHolderMoveGuard.ReleaseMove(characterGuid);

        public PvpKitWeenieInfo GetWeenie(uint wcid)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

            if (weenie == null)
                return null;

            return new PvpKitWeenieInfo
            {
                Wcid = wcid,
                Name = weenie.GetName(),
                MaxStackSize = weenie.GetProperty(PropertyInt.MaxStackSize),
                ValidLocations = weenie.GetProperty(PropertyInt.ValidLocations) ?? 0,
                IsContainer = weenie.WeenieType == WeenieType.Container,
                RequiresPackSlot = weenie.GetProperty(PropertyBool.RequiresBackpackSlot) ?? false,
            };
        }

        public void ReadPossessions(uint characterGuid, Action<List<PvpKitPossession>> callback)
        {
            DatabaseManager.Shard.GetPossessedBiotasInParallel(characterGuid, possessed =>
            {
                // Shard worker. Null = the read failed (SerializedShardDatabase answers a throwing read with null).
                List<PvpKitPossession> rows = null;

                try
                {
                    if (possessed != null)
                    {
                        rows = new List<PvpKitPossession>();

                        foreach (var entity in possessed.WieldedItems.Concat(possessed.Inventory))
                            rows.Add(ToPossession(BiotaConverter.ConvertToEntityBiota(entity)));
                    }
                }
                catch (Exception ex)
                {
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] kit edit: converting the possessions of 0x{characterGuid:X8} threw.", ex);
                    rows = null;
                }

                callback(rows);
            });
        }

        /// <summary>
        /// One converted runtime biota (ACE.Entity.Models.Biota, never the EF entity) as a planner row. The biota is this callback's
        /// own copy, so no lock is needed. Internal for the tests.
        /// </summary>
        internal static PvpKitPossession ToPossession(Biota biota)
        {
            int? Int(PropertyInt p) => biota.PropertiesInt != null && biota.PropertiesInt.TryGetValue(p, out var v) ? v : null;
            uint? Iid(PropertyInstanceId p) => biota.PropertiesIID != null && biota.PropertiesIID.TryGetValue(p, out var v) && v != 0 ? v : null;
            var requiresPackSlot = biota.PropertiesBool != null && biota.PropertiesBool.TryGetValue(PropertyBool.RequiresBackpackSlot, out var b) && b;

            var wielder = Iid(PropertyInstanceId.Wielder);
            var location = Int(PropertyInt.CurrentWieldedLocation);

            return new PvpKitPossession
            {
                Guid = biota.Id,
                Wcid = biota.WeenieClassId,
                Name = biota.PropertiesString != null ? biota.GetName() : null,
                StackSize = Math.Max(1, Int(PropertyInt.StackSize) ?? 1),
                ContainerId = Iid(PropertyInstanceId.Container),
                OwnerId = Iid(PropertyInstanceId.Owner),
                WielderId = wielder,
                WieldLocation = wielder != null && location is int l && l != 0 ? l : null,
                PlacementPosition = Int(PropertyInt.PlacementPosition),
                IsContainer = biota.WeenieType == WeenieType.Container,
                RequiresPackSlot = requiresPackSlot,
            };
        }

        public PvpKitNewItem Create(PvpKitCreation creation)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(creation.Wcid);

            if (wo == null)
                return null;

            if (creation.StackSize > 1 || wo is Stackable)
                wo.SetStackSize(creation.StackSize);

            // SetStackSize does nothing on an object that is not Stackable (WorldObject_Properties.SetStackSize), so a planned stack
            // above 1 on such an object would silently become 1. A stack that did not stick is a failed build (the caller then writes
            // nothing).
            if (creation.StackSize > 1 && wo.StackSize != creation.StackSize)
                return null;

            if (creation.WieldLocation.HasValue)
            {
                // What Creature.TryEquipObject persists; the hand placement is rebuilt from it at login (SetChildren).
                wo.WielderId = creation.WielderId;
                wo.CurrentWieldedLocation = (EquipMask)creation.WieldLocation.Value;
            }
            else
            {
                // What Container.TryAddToInventory persists; placement positions are re-normalized at login.
                wo.OwnerId = creation.OwnerId ?? creation.ContainerId;
                wo.ContainerId = creation.ContainerId;
                wo.PlacementPosition = creation.PlacementPosition ?? 0;
            }

            return new PvpKitNewItem { Guid = wo.Guid.Full, Creation = creation, Handle = wo };
        }

        public void Save(PvpKitNewItem item, Action<bool> callback)
        {
            var wo = (WorldObject)item.Handle;
            DatabaseManager.Shard.SaveBiota(wo.Biota, wo.BiotaDatabaseLock, callback);
        }

        public void Remove(IReadOnlyCollection<uint> guids, Action<bool> callback)
            => DatabaseManager.Shard.RemoveBiotasInParallel(guids, callback, null);

        public void OnWorld(Action action) => WorldManager.EnqueueAction(new ActionEventDelegate(action));

        public void LogInfo(string message) => PvpTemplateSettings.Log.Info(message);

        public void LogError(string message, Exception ex = null)
        {
            if (ex == null)
                PvpTemplateSettings.Log.Error(message);
            else
                PvpTemplateSettings.Log.Error(message, ex);
        }
    }
}
