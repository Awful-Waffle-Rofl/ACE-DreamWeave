using System;
using System.Collections.Generic;
using System.Numerics;

using log4net;

using ACE.Common;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Animation;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// A passive summonable creature
    /// </summary>
    public class Pet : Creature
    {
        public Player P_PetOwner;

        public PetDevice P_PetDevice;

        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public uint? PetDevice
        {
            get => GetProperty(PropertyInstanceId.PetDevice);
            set { if (value.HasValue) SetProperty(PropertyInstanceId.PetDevice, value.Value); else RemoveProperty(PropertyInstanceId.PetDevice); }
        }

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Pet(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Pet(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            Ethereal = true;
            RadarBehavior = ACE.Entity.Enum.RadarBehavior.ShowNever;
            ItemUseable = Usable.No;

            SuppressGenerateEffect = true;
        }

        /// <summary>
        /// Reports one hit this pet just landed to its own owner, as "&lt;pet&gt; hits &lt;target&gt; for N damage!".
        /// Opt-in per character via "/summondamage on|off" (Player.SummonDamageMessages), off by default.
        ///
        /// Only ever addressed to P_PetOwner, so a player never sees another player's summons. Zero and
        /// negative (healing) results are dropped so the feed carries hits only, and a squelched target is
        /// skipped for the same reason the player's own combat lines are.
        ///
        /// Called from the four places a pet's damage can reach a creature's health. Creature.TakeDamage is
        /// only the first of them - melee, via Monster_Melee, and missile, via ProjectileCollisionHelper.
        /// SpellProjectile.DamageTarget is the second: a spell projectile writes the vital directly and never
        /// reaches TakeDamage. The other two resolve with no projectile at all and so reach neither, and they
        /// are separate methods rather than one: WorldObject_Magic.HandleCastSpell_Boost's Health case is a
        /// Harm, and HandleCastSpell_Transfer's Health case is a Drain Health. Mana Barrier and Sanguine Ward
        /// each need a call at both of those sites for exactly the same reason, and sit beside these.
        ///
        /// Two deliberate gaps. Mana and stamina drains are not reported - "hits X for N damage!" would
        /// misdescribe them, and the spell-projectile and life-magic hooks both sit in the Health branch for
        /// that reason. Damage-over-time ticks are not reported either, and cannot be without a wider change:
        /// EnchantmentManager applies them through Creature.TakeDamageOverTime, whose signature carries no
        /// source at all, so by the time a tick lands there is nothing left identifying the pet that caused it.
        /// </summary>
        public void NotifyOwnerOfDamage(WorldObject target, int amount)
        {
            if (amount <= 0 || target == null)
                return;

            var owner = P_PetOwner;

            if (owner?.Session == null || !owner.SummonDamageMessages)
                return;

            if (owner.SquelchManager.Squelches.Contains(target, ChatMessageType.CombatSelf))
                return;

            owner.Session.Network.EnqueueSend(new GameMessageSystemChat(FormatDamageMessage(Name, target.Name, amount), ChatMessageType.CombatSelf));
        }

        /// <summary>
        /// The one place the feed's line is composed. Split out from NotifyOwnerOfDamage purely so the
        /// wording has regression coverage without a live Player - the same rationale as
        /// Player_Commerce.IsAcceptableToSell. The pet's Name already carries the owner's, because Pet.Init
        /// prefixes it, so the rendered line reads "Bob's Wisp hits Drudge Skulker for 42 damage!".
        /// </summary>
        internal static string FormatDamageMessage(string petName, string targetName, int amount)
        {
            return $"{petName} hits {targetName} for {amount} damage!";
        }

        /// <param name="spawnStagger">
        /// TRUE for the second pet of a Summon 2x activation: pushes the spawn point further out in front of
        /// the owner so the pair does not land on the same spot. Pets are Ethereal, so they never actually
        /// collide - this exists only so the player can see that two of them arrived.
        /// </param>
        public virtual bool? Init(Player player, PetDevice petDevice, bool spawnStagger = false)
        {
            var result = HandleCurrentActivePet(player);

            if (result == null || !result.Value)
                return result;

            // get physics radius of player and pet
            var playerRadius = player.PhysicsObj.GetPhysicsRadius();
            var petRadius = GetPetRadius();

            var spawnDist = playerRadius + petRadius + MinDistance;

            if (spawnStagger)
                spawnDist += petRadius * 2.0f;

            if (IsPassivePet)
            {
                Location = player.Location.InFrontOf(spawnDist, true);

                TimeToRot = -1;
            }
            else
            {
                Location = player.Location.InFrontOf(spawnDist, false);
            }

            Location.LandblockId = new LandblockId(Location.GetCell());

            Name = player.Name + "'s " + Name;

            PetOwner = player.Guid.Full;
            P_PetOwner = player;

            // All pets don't leave corpses, this maybe should have been in data, but isn't so lets make sure its true.
            NoCorpse = true;

            var success = EnterWorld();

            if (!success)
            {
                player.SendTransientError($"Couldn't spawn {Name}");
                return false;
            }

            // assign into the free pet slot - the primary normally, or the Summon 2x secondary slot when a
            // combat pet already occupies the primary (the gate above only permits this with Summon 2x)
            if (player.CurrentActivePet == null)
                player.CurrentActivePet = this;
            else
                player.SecondaryActivePet = this;

            // class ability: a live combat pet disarms Soul Tether's "pet died" resummon-cooldown skip
            if (this is CombatPet)
                player.OnCombatPetSummoned();

            petDevice.Pet = Guid.Full;
            PetDevice = petDevice.Guid.Full;
            P_PetDevice = petDevice;

            if (IsPassivePet)
                nextSlowTickTime = Time.GetUnixTime();

            return true;
        }

        public bool? HandleCurrentActivePet(Player player)
        {
            if (PropertyManager.GetBool("pet_stow_replace").Item)
                return HandleCurrentActivePet_Replace(player);
            else
                return HandleCurrentActivePet_Retail(player);
        }

        public bool HandleCurrentActivePet_Replace(Player player)
        {
            // original ace logic
            if (player.CurrentActivePet == null)
                return true;

            if (player.CurrentActivePet is CombatPet)
            {
                // class ability: Summon 2x permits a second concurrent combat pet
                if (this is CombatPet && player.CanSummonAdditionalCombatPet())
                    return true;

                // possibly add the ability to stow combat pets with passive pet devices here?
                player.SendTransientError($"{player.CurrentActivePet.Name} is already active");
                return false;
            }

            var stowPet = WeenieClassId == player.CurrentActivePet.WeenieClassId;

            // despawn passive pet
            player.CurrentActivePet.Destroy();

            return !stowPet;
        }

        public bool? HandleCurrentActivePet_Retail(Player player)
        {
            if (player.CurrentActivePet == null)
                return true;

            if (IsPassivePet)
            {
                // using a passive pet device
                // stow currently active passive/combat pet, as per retail
                // spawning the new passive pet requires another double click
                player.CurrentActivePet.Destroy();
            }
            else
            {
                // using a combat pet device
                if (player.CurrentActivePet is CombatPet)
                {
                    // class ability: Summon 2x permits a second concurrent combat pet
                    if (this is CombatPet && player.CanSummonAdditionalCombatPet())
                        return true;

                    player.SendTransientError($"{player.CurrentActivePet.Name} is already active");
                }
                else
                {
                    // stow currently active passive pet
                    // stowing the currently active passive pet w/ a combat pet device will unfortunately start the cooldown timer (and decrease the structure?) on the combat pet device, as per retail
                    // spawning the combat pet will require another double click in ~45s, as per retail
                    player.CurrentActivePet.Destroy();

                    return null;
                }
            }
            return false;
        }

        /// <summary>
        /// Called 5x per second for passive pets
        /// </summary>
        public void Tick(double currentUnixTime)
        {
            NextMonsterTickTime = currentUnixTime + monsterTickInterval;

            if (IsMoving)
            {
                PhysicsObj.update_object(Location.Instance);

                UpdatePosition_SyncLocation();

                SendUpdatePosition();
            }

            if (currentUnixTime >= nextSlowTickTime)
                SlowTick(currentUnixTime);
        }

        private const double slowTickSeconds = 1.0;
        private double nextSlowTickTime;

        /// <summary>
        /// Called 1x per second
        /// </summary>
        public void SlowTick(double currentUnixTime)
        {
            //Console.WriteLine($"{Name}.HeartbeatStatic({currentUnixTime})");

            nextSlowTickTime += slowTickSeconds;

            if (P_PetOwner?.PhysicsObj == null)
            {
                log.Error($"{Name} ({Guid}).SlowTick() - P_PetOwner: {P_PetOwner}, P_PetOwner.PhysicsObj: {P_PetOwner?.PhysicsObj}");
                Destroy();
                return;
            }

            var dist = GetCylinderDistance(P_PetOwner);

            if (dist > MaxDistance)
                Destroy();

            if (!IsMoving && dist > MinDistance)
                StartFollow();
        }

        // if the passive pet is between min-max distance to owner,
        // it will turn and start running torwards its owner

        private const float MinDistance = 2.0f;
        private const float MaxDistance = 192.0f;

        private void StartFollow()
        {
            // similar to Monster_Navigation.StartTurn()

            //Console.WriteLine($"{Name}.StartFollow()");

            IsMoving = true;

            // broadcast to clients
            MoveTo(P_PetOwner, RunRate);

            // perform movement on server
            var mvp = new MovementParameters();
            mvp.DistanceToObject = MinDistance;
            mvp.WalkRunThreshold = 0.0f;

            //mvp.UseFinalHeading = true;

            PhysicsObj.MoveToObject(P_PetOwner.PhysicsObj, mvp);

            // prevent snap forward
            PhysicsObj.UpdateTime = Physics.Common.PhysicsTimer.CurrentTime;
        }

        /// <summary>
        /// Broadcasts passive pet movement to clients
        /// </summary>
        public override void MoveTo(WorldObject target, float runRate = 1.0f)
        {
            if (!IsPassivePet)
            {
                base.MoveTo(target, runRate);
                return;
            }

            if (MoveSpeed == 0.0f)
                GetMovementSpeed();

            var motion = new Motion(this, target, MovementType.MoveToObject);

            motion.MoveToParameters.MovementParameters |= MovementParams.CanCharge;
            motion.MoveToParameters.DistanceToObject = MinDistance;
            motion.MoveToParameters.WalkRunThreshold = 0.0f;

            motion.RunRate = RunRate;

            CurrentMotionState = motion;

            EnqueueBroadcastMotion(motion);
        }

        /// <summary>
        /// Called when the MoveTo process has completed
        /// </summary>
        public override void OnMoveComplete(WeenieError status)
        {
            //Console.WriteLine($"{Name}.OnMoveComplete({status})");

            if (!IsPassivePet)
            {
                base.OnMoveComplete(status);
                return;
            }

            if (status != WeenieError.None)
                return;

            PhysicsObj.CachedVelocity = Vector3.Zero;
            IsMoving = false;
        }

        public static Dictionary<uint, float> PetRadiusCache = new Dictionary<uint, float>();

        private float GetPetRadius()
        {
            if (PetRadiusCache.TryGetValue(WeenieClassId, out var radius))
                return radius;

            var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(SetupTableId);

            var scale = ObjScale ?? 1.0f;

            return ProjectileRadiusCache[WeenieClassId] = setup.Spheres[0].Radius * scale;
        }
    }
}
