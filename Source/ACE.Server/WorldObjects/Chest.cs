using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    public partial class Chest : Container, Lock
    {
        /// <summary>
        /// This is used for things like Mana Forge Chests
        /// </summary>
        public bool ChestRegenOnClose
        {
            get
            {
                if (ChestResetInterval <= 5)
                    return true;

                return GetProperty(PropertyBool.ChestRegenOnClose) ?? false;
            }
            set { if (!value) RemoveProperty(PropertyBool.ChestRegenOnClose); else SetProperty(PropertyBool.ChestRegenOnClose, value); }
        }

        /// <summary>
        /// This is used for things like Dirty Old Crate
        /// </summary>
        public bool ChestClearedWhenClosed
        {
            get => GetProperty(PropertyBool.ChestClearedWhenClosed) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.ChestClearedWhenClosed); else SetProperty(PropertyBool.ChestClearedWhenClosed, value); }
        }

        /// <summary>
        /// This is the default setup for resetting chests
        /// </summary>
        public double ChestResetInterval
        {
            get
            {
                var chestResetInterval = ResetInterval ?? Default_ChestResetInterval;

                if (chestResetInterval < 15)
                    chestResetInterval = Default_ChestResetInterval;

                return chestResetInterval;
            }
        }

        public virtual double Default_ChestResetInterval => 120;

        /// <summary>
        /// Threads (WaffleACE): the guid of the one player allowed to open this chest, or null for every
        /// chest in the game that is not a Thread boss cache - which is all of them except the ones
        /// ThreadDungeonRewardSpawner spawns. Purely in-memory and NEVER persisted, exactly like
        /// Creature.P_DungeonRun: a boss cache lives and dies inside one ephemeral instance, which is what
        /// keeps it out of the shard entirely. Landblock.SaveDB returns on `if (IsEphemeral)`
        /// (Landblock.cs:1863-1868) BEFORE it iterates worldObjects, so no object inside a Thread copy is
        /// ever offered to IsDynamicThatShouldPersistToShard in the first place. The
        /// PropertyInt.ThreadDungeonRunId stamp the spawner also writes is defense in depth for the one case
        /// that early return does not cover - an object that somehow ends up on a NON-ephemeral landblock -
        /// and is not the primary guarantee.
        ///
        /// Read by <see cref="CheckUseRequirements"/>, which is the gate every use path goes through
        /// (WorldObject.OnActivate calls it before dispatching to ActOnUse). NULL is the whole-game default
        /// and short-circuits to today's behaviour on the first comparison, so no existing chest changes.
        /// </summary>
        public uint? P_DungeonCacheOwnerGuid;

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Chest(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Chest(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            ContainerCapacity = ContainerCapacity ?? 10;
            ItemCapacity = ItemCapacity ?? 120;

            ActivationResponse |= ActivationResponse.Use;   // todo: fix broken data

            CurrentMotionState = motionClosed;              // do any chests default to open?

            if (IsLocked)
                DefaultLocked = true;

            if (DefaultLocked) // ignore regen interval, only regen on relock
                NextGeneratorRegenerationTime = double.MaxValue;
        }

        protected static readonly Motion motionOpen = new Motion(MotionStance.NonCombat, MotionCommand.On);
        protected static readonly Motion motionClosed = new Motion(MotionStance.NonCombat, MotionCommand.Off);

        public override ActivationResult CheckUseRequirements(WorldObject activator)
        {
            var baseRequirements = base.CheckUseRequirements(activator);
            if (!baseRequirements.Success)
                return baseRequirements;

            if (!(activator is Player player))
                return new ActivationResult(false);

            // Threads (WaffleACE): a boss cache belongs to the run owner and to nobody else. Guarded on the
            // stamp being present, so a chest that carries no owner - every other chest in the game - takes
            // the identical path it took before this check existed.
            //
            // This seam rather than an early return in ActOnUse: CheckUseRequirements is the single gate
            // ActOnUse is reached through (WorldObject_Use.cs:85-93 checks it and returns on failure),
            // Chest already overrides it, and it is the only one of the two that can refuse with a message
            // rather than silently doing nothing. Refusing in ActOnUse would still have run the walk-to
            // chain and the open motion first.
            if (P_DungeonCacheOwnerGuid != null && player.Guid.Full != P_DungeonCacheOwnerGuid.Value)
            {
                // Wording is deliberately system-agnostic. This one stamp is now the shared owner-only gate
                // for BOTH the Thread boss cache and the ML digsite reward chest (the digging player alone),
                // so a message naming "your thread" would have been wrong at a dig site.
                //
                // NO LEADING ARTICLE, and that is not a typo. Since 2026-09-17 a Thread Cache is labelled for its
                // holder ("Awfulwaffle's Thread Cache"), and "The Awfulwaffle's Thread Cache answers only to..."
                // is ungrammatical: a possessive is already a determiner and cannot take "the" as well. Dropping
                // the article is the one edit that reads for both users of this line, since the digsite chest's
                // name is "Unearthed Cache" and "Unearthed Cache answers only to..." reads as a label naming
                // itself. Both readings are pinned by tests. Do not re-add "The " for the digsite case alone; this
                // line has two callers and only one string.
                player.SendTransientError($"{Name} answers only to the adventurer it opened for.");
                return new ActivationResult(false);
            }

            if (IsLocked)
            {
                if (PropertyManager.GetBool("fix_chest_missing_inventory_window").Item)
                    player.SendTransientError($"The {Name} is locked");

                EnqueueBroadcast(new GameMessageSound(Guid, Sound.OpenFailDueToLock, 1.0f));
                return new ActivationResult(false);
            }

            if (UseLockTimestamp != null && activator.Guid.Full != LastUnlocker)
            {
                var currentTime = Time.GetUnixTime();

                // prevent ninja looting
                if (UseLockTimestamp.Value + PropertyManager.GetDouble("unlocker_window").Item > currentTime)
                {
                    player.SendTransientError(InUseMessage);
                    return new ActivationResult(false);
                }
            }

            if (IsOpen)
            {
                if (Viewer == player.Guid.Full)
                {
                    // current player has this chest open, close it
                    Close(player);
                }
                else
                {
                    // another player has this chest open -- ensure they are within range
                    var currentViewer = CurrentLandblock.GetObject(Viewer) as Player;

                    if (currentViewer == null)
                        Close(null);    // current viewer not found, close it
                    else
                        player.SendTransientError(InUseMessage);
                }

                return new ActivationResult(false);
            }

            // handle quest requirements
            if (Quest != null)
            {
                if (!player.QuestManager.HasQuest(Quest))
                    EmoteManager.OnQuest(player);
                else
                {
                    if (player.QuestManager.CanSolve(Quest))
                    {
                        EmoteManager.OnQuest(player);
                    }
                    else
                    {
                        player.QuestManager.HandleSolveError(Quest);
                        return new ActivationResult(false);
                    }
                }
            }

            return new ActivationResult(true);
        }

        /// <summary>
        /// This is raised by Player.HandleActionUseItem.<para />
        /// The item does not exist in the players possession.<para />
        /// If the item was outside of range, the player will have been commanded to move using DoMoveTo before ActOnUse is called.<para />
        /// When this is called, it should be assumed that the player is within range.
        /// </summary>
        public override void ActOnUse(WorldObject wo)
        {
            if (!(wo is Player player))
                return;

            // open chest
            Open(player);
        }

        public override void Open(Player player)
        {
            base.Open(player);

            if (!ResetMessagePending && !double.IsPositiveInfinity(ChestResetInterval))
            {
                var resetTimestamp = ResetTimestamp;

                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(ChestResetInterval);
                actionChain.AddAction(this, () => Reset(resetTimestamp));
                actionChain.EnqueueChain();

                ResetMessagePending = true;
            }

            UseLockTimestamp = null;
        }

        public override void Close(Player player)
        {
            Close(player);
        }

        /// <summary>
        /// Called when a chest is closed, or walked away from
        /// </summary>
        public void Close(Player player, bool tryReset = true)
        {
            base.Close(player);

            if (ChestRegenOnClose && tryReset)
                Reset(ResetTimestamp);
        }

        public override void FinishClose(Player player)
        {
            base.FinishClose(player);

            if (ChestClearedWhenClosed && InitCreate > 0)
            {
                if (CurrentCreate == 0)
                    FadeOutAndDestroy(); // Chest's complete generated inventory count has been wiped out
                    //Destroy(); // Chest's complete generated inventory count has been wiped out
            }
        }

        public void Reset(double? resetTimestamp)
        {
            if (resetTimestamp != ResetTimestamp)
                return;     // already cleared by previous reset

            // TODO: if 'ResetInterval' style, do we want to ensure a minimum amount of time for the last viewer?

            // should only be an edge case with reload-landblock
            if (CurrentLandblock == null)
                return;

            var player = CurrentLandblock.GetObject(Viewer) as Player;

            if (IsOpen)
                Close(player, false);

            if (DefaultLocked && !IsLocked)
            {
                IsLocked = true;
                if (!PropertyManager.GetBool("fix_chest_missing_inventory_window").Item)
                    EnqueueBroadcast(new GameMessagePublicUpdatePropertyBool(this, PropertyBool.Locked, IsLocked));
            }

            ClearUnmanagedInventory();

            if (IsGenerator)
            {
                ResetGenerator();
                CurrentlyPoweringUp = true;
                if (InitCreate > 0)
                    Generator_Generate();
            }

            ResetTimestamp = Time.GetUnixTime();
            ResetMessagePending = false;
        }
        protected override float DoOnOpenMotionChanges()
        {
            if (MotionTableId != 0)
                return ExecuteMotion(motionOpen);
            else
                return 0;
        }

        protected override float DoOnCloseMotionChanges()
        {
            if (MotionTableId != 0)
                return ExecuteMotion(motionClosed);
            else
                return 0;
        }

        public string LockCode
        {
            get => GetProperty(PropertyString.LockCode);
            set { if (value == null) RemoveProperty(PropertyString.LockCode); else SetProperty(PropertyString.LockCode, value); }
        }

        /// <summary>
        /// Used for unlocking a chest via lockpick, so contains a skill check
        /// player.Skills[Skill.Lockpick].Current should be sent for the skill check
        /// </summary>
        public UnlockResults Unlock(uint unlockerGuid, uint playerLockpickSkillLvl, ref int difficulty)
        {
            var result = LockHelper.Unlock(this, playerLockpickSkillLvl, ref difficulty);

            if (result == UnlockResults.UnlockSuccess)
            {
                LastUnlocker = unlockerGuid;
                UseLockTimestamp = Time.GetUnixTime();
            }
            return result;
        }

        /// <summary>
        /// Used for unlocking a chest via a key
        /// </summary>
        public UnlockResults Unlock(uint unlockerGuid, Key key, string keyCode = null)
        {
            var result = LockHelper.Unlock(this, key, keyCode);

            if (result == UnlockResults.UnlockSuccess)
            {
                LastUnlocker = unlockerGuid;
                UseLockTimestamp = Time.GetUnixTime();
            }
            return result;
        }
    }
}
