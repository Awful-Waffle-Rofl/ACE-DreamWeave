using System;

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
    public class Door : WorldObject, Lock
    {
        private static readonly Motion motionOpen = new Motion(MotionStance.NonCombat, MotionCommand.On);
        private static readonly Motion motionClosed = new Motion(MotionStance.NonCombat, MotionCommand.Off);

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Door(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Door(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            ObjectDescriptionFlags |= ObjectDescriptionFlag.Door;

            if (!DefaultOpen)
            {
                CurrentMotionState = motionClosed;
                IsOpen = false;
                //Ethereal = false;
            }
            else
            {
                CurrentMotionState = motionOpen;
                IsOpen = true;
                Ethereal = true;
            }

            ResetInterval = ResetInterval ?? 30.0f;
            LockCode = LockCode ?? "";

            // Account for possible missing property from recreated weenies
            if (IsLocked && !DefaultLocked)
                DefaultLocked = true;

            if (DefaultLocked)
                IsLocked = true;
            else
                IsLocked = false;

            ActivationResponse |= ActivationResponse.Use;
        }

        /// <summary>
        /// Open World Threads R9: makes this door permanently open and unlocked, for an outdoor run's copy. Called on
        /// every kept door across the footprint BEFORE it enters the world (Landblock.CreateWorldObjects), so the create
        /// packet already carries the open state and no broadcast is needed. Monsters with AiOptions 0 cannot open a
        /// door (OnCollideObject), so a closed door would wall them off; and Reset re-locks a DefaultLocked door after
        /// ResetInterval, so DefaultLocked goes and DefaultOpen is set, which makes Reset reopen it instead. Locked is
        /// cleared FIRST: SetEphemeralValues re-derives DefaultLocked from a Locked door.
        /// </summary>
        public void ForceOpenUnlocked()
        {
            IsLocked = false;
            DefaultLocked = false;
            DefaultOpen = true;

            CurrentMotionState = motionOpen;
            IsOpen = true;
            Ethereal = true;
        }

        public string LockCode
        {
            get => GetProperty(PropertyString.LockCode);
            set { if (value == null) RemoveProperty(PropertyString.LockCode); else SetProperty(PropertyString.LockCode, value); }
        }

        public override void OnTalk(WorldObject activator)
        {
            if (activator is Player player)
            {
                var behind = player != null && player.GetRelativeDir(this).HasFlag(Quadrant.Back);

                if (IsOpen && !behind) // not sure if retail made this distinction, but for the doors tested, it seemed more logical given the text shown
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(ActivationTalk, ChatMessageType.Broadcast));
            }
        }

        /// <summary>
        /// Speed course boss fight: the door is sealed shut and refuses every use, including from the far side a locked
        /// door normally opens from (the `behind` case in ActOnUse). Ephemeral, never persisted; set and cleared by
        /// LiveSpeedCourseWorld.SetSealDoor on the landblock thread.
        /// </summary>
        public bool SealedShut { get; set; }

        public override void ActOnUse(WorldObject worldObject)
        {
            // DIVERGES FROM UPSTREAM (WaffleACE, 2026-10-27): a door the player's own Pinnacle fight spawned (the Olthoi Matron's
            // pit doors) hands the use to the fight and nothing else happens. First line, ahead of IsBusy: a prop door is sealed
            // shut, and the player's use must reach the script even while a script-driven Open/Close animation holds IsBusy.
            if (worldObject is Player user && ACE.Server.Pinnacles.PinnacleFightManager.TryHandlePropUse(this, user))
                return;

            if (IsBusy) return;

            if (SealedShut)
            {
                if (worldObject is Player sealedPlayer)
                {
                    sealedPlayer.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(sealedPlayer.Session, "The door is sealed shut!"));
                    EnqueueBroadcast(new GameMessageSound(Guid, Sound.OpenFailDueToLock, 1.0f));
                }

                return;
            }

            var player = worldObject as Player;
            var behind = player != null && player.GetRelativeDir(this).HasFlag(Quadrant.Back);

            if (!IsLocked || behind)
            {
                if (!IsOpen)
                    Open(worldObject.Guid);
                else if (!(worldObject is Switch) && !(worldObject is PressurePlate))
                    Close(worldObject.Guid);

                // Create Door auto close timer
                var useTimestamp = UseTimestamp ?? 0;

                var autoCloseTimer = new ActionChain();
                autoCloseTimer.AddDelaySeconds(ResetInterval ?? 0);
                autoCloseTimer.AddAction(this, () => Reset(useTimestamp));
                autoCloseTimer.EnqueueChain();
            }
            else
            {
                if (player != null)
                {
                    var doorIsLocked = new GameEventCommunicationTransientString(player.Session, "The door is locked!");
                    player.Session.Network.EnqueueSend(doorIsLocked);
                    EnqueueBroadcast(new GameMessageSound(Guid, Sound.OpenFailDueToLock, 1.0f));
                }
            }
        }

        public void Open(ObjectGuid opener = new ObjectGuid())
        {
            if (CurrentMotionState == motionOpen)
                return;

            EnqueueBroadcastMotion(motionOpen);
            CurrentMotionState = motionOpen;

            Ethereal = true;
            IsOpen = true;

            EnqueueBroadcastPhysicsState();

            if (opener.Full > 0)
                UseTimestamp = Time.GetUnixTime();

            IsBusy = true;

            var animTime = Physics.Animation.MotionTable.GetAnimationLength(MotionTableId, MotionStance.NonCombat, MotionCommand.Off, MotionCommand.On);

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(animTime);
            actionChain.AddAction(this, () => IsBusy = false);
            actionChain.EnqueueChain();
        }

        public double CloseTimestamp;

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// This door's real collision width for anti-blink (see DoorCollisionWidth), derived once, lazily.
        /// NaN = not derived yet, 0 = the shape was not available (the global floor applies). A single float, so
        /// a race between two player ticks at most derives the same value twice.
        /// </summary>
        private float antiBlinkCollisionWidth = float.NaN;

        /// <summary>
        /// The width anti-blink builds this door's blocking segment with: max(floorWidth, real collision width).
        /// The real width is derived on the first call made while the door is fully closed and then cached, so
        /// the player movement tick never re-walks the door's geometry.
        /// </summary>
        public float GetAntiBlinkDoorWidth(float floorWidth)
        {
            var realWidth = antiBlinkCollisionWidth;

            if (float.IsNaN(realWidth))
            {
                // only the closed pose is the shape a player collides with; an open leaf has swung off the
                // width axis. Until the door is seen closed (and in the world), the floor applies uncached.
                if (IsOpen || Ethereal == true || PhysicsObj == null)
                    return floorWidth;

                var derived = DoorCollisionWidth.Derive(PhysicsObj);

                realWidth = derived ?? 0.0f;
                antiBlinkCollisionWidth = realWidth;

                if (derived == null)
                    log.Debug($"[BLINK] '{Name}' (0x{Guid.Full:X8}, setup 0x{SetupTableId:X8}) - no collision shape available, anti-blink uses the global width {floorWidth:F2}");
                else
                    log.Debug($"[BLINK] '{Name}' (0x{Guid.Full:X8}, setup 0x{SetupTableId:X8}) - collision width {realWidth:F2}");
            }

            return DoorCollisionWidth.GetEffectiveWidth(floorWidth, realWidth);
        }

        public void Close(ObjectGuid closer = new ObjectGuid())
        {
            if (CurrentMotionState == motionClosed)
                return;

            EnqueueBroadcastMotion(motionClosed);
            CurrentMotionState = motionClosed;

            IsOpen = false;

            var animTime = Physics.Animation.MotionTable.GetAnimationLength(MotionTableId, MotionStance.NonCombat, MotionCommand.On, MotionCommand.Off);

            //Console.WriteLine($"AnimTime: {animTime}");

            IsBusy = true;

            CloseTimestamp = Time.GetUnixTime();

            var closeTimestamp = CloseTimestamp;

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(animTime);
            actionChain.AddAction(this, () =>
            {
                FinalizeClose(closeTimestamp);
                IsBusy = false;
            });
            actionChain.EnqueueChain();

            if (closer.Full > 0)
                UseTimestamp = Time.GetUnixTime();
        }

        private void FinalizeClose(double closeTimestamp)
        {
            if (IsOpen || closeTimestamp != CloseTimestamp)
                return;

            // ethereal must be set to false for ethereal_check_for_collisions
            Ethereal = false;

            if (PropertyManager.GetBool("allow_door_hold").Item && PhysicsObj.ethereal_check_for_collisions())
            {
                // the source of this bug is EtherealHook for the door
                // physics engine set_ethereal() -> ethereal_check_for_collisions() -> CheckEthereal state

                // if fix_door_holding == true, the player can still hold doors for other nearby players
                // who already know about the door / have not been far away from the door for > 25s

                // fix_door_holding == true only fixes 'long holding'
                //Console.WriteLine($"{Name} ({Guid}).FinalizeClose()");
                Ethereal = true;

                var holdChain = new ActionChain();
                holdChain.AddDelaySeconds(1.0f);    // poll every second
                holdChain.AddAction(this, () => FinalizeClose(closeTimestamp));
                holdChain.EnqueueChain();
                return;
            }

            EnqueueBroadcastPhysicsState();
        }

        private void Reset(double useTimestamp)
        {
            if (useTimestamp != UseTimestamp) return;

            if (!DefaultOpen)
            {
                Close(ObjectGuid.Invalid);
                if (DefaultLocked)
                {
                    IsLocked = true;
                    var updateProperty = new GameMessagePublicUpdatePropertyBool(this, PropertyBool.Locked, IsLocked);
                    EnqueueBroadcast(updateProperty);
                }
            }
            else
                Open(ObjectGuid.Invalid);

            ResetTimestamp = Time.GetUnixTime();
        }

        /// <summary>
        /// Used for unlocking a door via lockpick, so contains a skill check
        /// player.Skills[Skill.Lockpick].Current should be sent for the skill check
        /// </summary>
        public UnlockResults Unlock(uint unlockerGuid, uint playerLockpickSkillLvl, ref int difficulty)
        {
            return LockHelper.Unlock(this, playerLockpickSkillLvl, ref difficulty);
        }

        /// <summary>
        /// Used for unlocking a door via a key
        /// </summary>
        public UnlockResults Unlock(uint unlockerGuid, Key key, string keyCode = null)
        {
            return LockHelper.Unlock(this, key, keyCode);
        }

        public override void SetLinkProperties(WorldObject wo)
        {
            wo.ActivationTarget = Guid.Full;
        }

        public override void OnCollideObject(WorldObject target)
        {
            if (IsOpen) return;

            // currently the only AI options appear to be 0 or 1,
            // 1 meaning able to open doors?
            var creature = target as Creature;
            if (creature == null || creature.AiOptions == 0)
                return;

            ActOnUse(target);
        }
    }
}
