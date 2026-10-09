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

        /// <summary>
        /// Umbral Siphon (Void/Summon T1): heals THIS PET for a fraction of the damage it just landed - the
        /// pet is healed, never the caster. Called from the same four sites as NotifyOwnerOfDamage, and only
        /// ever with PvE damage - a call site must gate on the target NOT being a Player before reaching
        /// here (see Pet.NotifyOwnerOfDamage's doc comment for the four sites and why there are exactly
        /// four). A no-op when the owner is gone, the ability is unlearned, or amount is not positive.
        /// </summary>
        public void ApplyUmbralSiphonLeech(int amount)
        {
            if (amount <= 0)
                return;

            var owner = P_PetOwner;
            if (owner == null)
                return;

            var fraction = owner.GetUmbralSiphonLeechFraction();
            if (fraction <= 0.0)
                return;

            var healAmount = (int)Math.Round(amount * fraction);
            if (healAmount <= 0)
                return;

            UpdateVitalDelta(Health, healAmount);
        }

        /// <summary>
        /// Empowered Summons' leech (widened 2026-09-12): heals for a fraction of the damage a pet just
        /// landed. Called from the same four sites as NotifyOwnerOfDamage, PvE only - the call site gates on
        /// the target NOT being a Player before reaching here. A no-op when the owner is gone, the ability is
        /// unlearned, or amount is not positive.
        ///
        /// RECIPIENT IS AMBIGUOUS IN THE SIGNED-OFF DESIGN TEXT: "their hits leech 3/6/9% back as health"
        /// names no recipient for "their". Read here as the PET (the more natural subject, and the reading
        /// that keeps this whole widening about making pets stronger) - the repo owner is flagging the
        /// ambiguity upstream separately. This intentionally stacks on the same axis as Umbral Siphon's own
        /// pet-heal; that is acceptable for a T1 and a T2 in the same class and is not a bug to fix here.
        ///
        /// SINGLE SWITCH POINT: if the recipient is later decided to be the owner instead, change ONLY the
        /// line below (`recipient = this` -> `recipient = owner`) - nothing else in this method needs to
        /// change.
        /// </summary>
        public void ApplyEmpoweredSummonsLeech(int amount)
        {
            if (amount <= 0)
                return;

            var owner = P_PetOwner;
            if (owner == null)
                return;

            var fraction = owner.GetEmpoweredSummonsLeechFraction();
            if (fraction <= 0.0)
                return;

            var healAmount = (int)Math.Round(amount * fraction);
            if (healAmount <= 0)
                return;

            Creature recipient = this;   // <-- the one line to flip to redirect this ability's leech

            recipient.UpdateVitalDelta(recipient.Health, healAmount);
        }

        /// <summary>
        /// TRUE when the last Init got past the active-pet gate but could place this pet neither in front of its
        /// owner nor on the owner's own spot. Read by PetDevice.SummonCreature to tell a placement failure, which
        /// hands the activation cooldown back, apart from every other false Init returns (a gate refusal, a
        /// stow, a data error).
        /// </summary>
        public bool PlacementFailed { get; private set; }

        /// <summary>
        /// The owner's currently-active combat pet(s) a retirement (see <see cref="CombatPetRetireRules"/>) decided
        /// to dismiss in favor of this one. Populated by HandleCurrentActivePet_Replace/_Retail, which must NOT
        /// Destroy() them directly - see Init for why the dismissal has to wait.
        /// </summary>
        private List<Pet> petsPendingSwapDismissal;

        /// <summary>
        /// Queues <paramref name="pet"/> for dismissal once this pet's own placement has succeeded. No-op for
        /// a null or already-destroyed pet (nothing left to dismiss), or a duplicate of an already-queued pet.
        /// </summary>
        private void QueuePetForSwapDismissal(Pet pet)
        {
            if (pet == null || pet.IsDestroyed)
                return;

            petsPendingSwapDismissal ??= new List<Pet>();

            if (!petsPendingSwapDismissal.Contains(pet))
                petsPendingSwapDismissal.Add(pet);
        }

        /// <summary>
        /// The wcid to check against CombatPetRetireRules for one of the owner's active-pet slots: the pet's
        /// own WeenieClassId when it's a live (non-destroyed) CombatPet, null otherwise - null covers an empty
        /// slot, a passive pet, and a pet Destroy() already tore down but whose slot reference hasn't cleared
        /// yet (Destroy nulls the owner's slot synchronously, so that last case shouldn't occur, but a
        /// destroyed CombatPet is excluded defensively all the same).
        ///
        /// Internal (not private) so PetDevice.CheckUseRequirements can share this ONE definition rather than
        /// keeping a byte-identical copy of its own - both gates have to agree with CombatPetRetireRules on
        /// what counts as "active" or they could disagree with each other.
        /// </summary>
        internal static uint? ActiveCombatPetWcid(Pet pet)
        {
            if (pet is CombatPet combatPet && !combatPet.IsDestroyed)
                return combatPet.WeenieClassId;

            return null;
        }

        /// <param name="spawnStagger">
        /// TRUE for the second pet of a Summon 2x activation: pushes the spawn point further out in front of
        /// the owner so the pair does not land on the same spot. Pets are Ethereal, so they never actually
        /// collide - this exists only so the player can see that two of them arrived. A rejected staggered spot
        /// falls back to the owner's own spot exactly like any other summon.
        /// </param>
        public virtual bool? Init(Player player, PetDevice petDevice, bool spawnStagger = false)
        {
            var result = HandleCurrentActivePet(player, spawnStagger);

            if (result == null || !result.Value)
                return result;

            // get physics radius of the pet, kept for GetOwnerPlacementDistance
            var petRadius = GetPetRadius();
            placementPetRadius = petRadius;

            var spawnDist = GetOwnerPlacementDistance(player);

            if (spawnStagger)
                spawnDist += petRadius * 2.0f;

            if (IsPassivePet)
                TimeToRot = -1;

            // the spot in front of the owner, or null when it is not worth trying (owner indoors, lookup failed,
            // off the owner's landblock, inside a building) - the owner's own spot is used directly then
            var inFront = GetInFrontPlacement(player, spawnDist, IsPassivePet);

            Location = inFront != null ? new Position(inFront) : GetOwnerSpotPlacement(player);

            Name = player.Name + "'s " + Name;

            PetOwner = player.Guid.Full;
            P_PetOwner = player;

            // All pets don't leave corpses, this maybe should have been in data, but isn't so lets make sure its true.
            NoCorpse = true;

            var success = EnterWorld();

            // The spot in front of the owner is rejected whenever it lands inside a wall or off a ramp, so try
            // once more on the owner's own spot - a cell the owner already occupies. Pets are Ethereal, so
            // sharing it never collides. A failed EnterWorld leaves nothing behind to clean up first: it
            // destroys the physics object (unregistering it from ServerObjectManager), nulls CurrentLandblock,
            // and never reaches the landblock's pending additions. A copy, never the owner's own Position:
            // AddPhysicsObj adjusts Location in place.
            if (!success && inFront != null)
            {
                Location = GetOwnerSpotPlacement(player);

                success = EnterWorld();
            }
            else if (success && inFront != null && !IsPlacedAt(inFront))
            {
                // EnterWorld succeeded, but the Slide placement resolved the spot in front of the owner to
                // somewhere else - typically the far side of a wall. The pet is already in the world (and
                // announced to nearby clients), so move it with a physics teleport rather than a re-enter.
                RelocateToOwnerSpot(player);
            }

            // Last chance, and the only second attempt an INDOOR summon has ever had: the owner's own spot
            // lifted by PetFollowRules.OwnerSpotBumpHeight. Indoors GetInFrontPlacement always declines, so
            // inFront is null, the retry above is skipped, and a dungeon summon gets exactly one placement -
            // the owner's spot, Z included. A standing player's Z is a few millimetres above the floor plane
            // under them, and at that height the placement insert refuses a physics sphere bigger than a
            // player's while accepting the player, which is why a combat pet failed where its owner stood
            // (see OwnerSpotBumpHeight for the measurement). The lift is what Position.InFrontOf already gives
            // the outdoor spot for the same reason, so this closes the gap rather than adding a new mechanism.
            //
            // Placed AFTER the two blocks above and gated on !success, so neither the in-front placement, nor
            // the owner-spot retry, nor the RelocateToOwnerSpot correction is altered in any way: this runs
            // only where the summon already had nothing left but the error message. Re-entering is safe and is
            // what the retry above already relies on - a failed AddPhysicsObj nulls PhysicsObj
            // (WorldObject.cs:246, :262) and Landblock.AddWorldObjectInternal rebuilds it (Landblock.cs:1655).
            // That null is also why RelocateToOwnerSpot cannot be used here: it is a physics teleport of a pet
            // already in the world, and PhysicsTeleport would dereference the PhysicsObj this pet no longer
            // has.
            if (!success)
            {
                var bumped = GetOwnerSpotPlacement(player);
                bumped.PositionZ += PetFollowRules.OwnerSpotBumpHeight;

                // A copy, because AddPhysicsObj adjusts Location in place and the landing has to be judged
                // against what was asked for.
                var requested = new Position(bumped);

                Location = bumped;

                success = EnterWorld();

                // A Slide placement accepts whatever spot the transition resolves to, so an OK result is not
                // proof the pet is where it was sent. A pet that slid out of the owner's cell, past the
                // placement tolerances, or into another landblock instance is worse for the player than no pet
                // at all - they would hold a summon they cannot see or use, and get no error - so it is
                // destroyed and the clean failure below runs exactly as it does today. The landed instance is
                // read off the landblock the pet actually joined, not off Location, because AddPhysicsObj never
                // rewrites Location.Instance and reading it back would only restate what was asked for.
                var landedBlock = CurrentLandblock;
                var physics = PhysicsObj;

                if (success && !(landedBlock != null && physics != null
                        && PetFollowRules.AcceptBumpedOwnerSpot(requested.LandblockId.Raw, requested.Pos, requested.Instance,
                            physics.Position.ObjCellID, physics.Position.Frame.Origin, landedBlock.Instance, physics.CurCell != null)))
                {
                    log.Debug($"{Name} (0x{Guid}) summon placement on {player.Name}'s lifted spot reported OK but did not land there (requested 0x{requested.LandblockId.Raw:X8} {requested.Pos} instance {requested.Instance}, landed 0x{physics?.Position.ObjCellID ?? 0:X8} {physics?.Position.Frame.Origin} instance {landedBlock?.Instance}); refusing it");

                    // Destroy, not leave_world: the pet was announced to nearby clients by NotifyPlayers and
                    // has to be taken back off them. Idempotent, so PetDevice.SummonCreature's own Destroy on
                    // a false result stays a no-op, and it cannot touch an active-pet slot or the device
                    // because neither has been assigned yet (that happens below, only on success).
                    Destroy();

                    success = false;
                }
            }

            if (!success)
            {
                PlacementFailed = true;
                player.SendTransientError($"Couldn't spawn {Name}");
                return false;
            }

            // a combat-pet retirement (CombatPetRetireRules, queued by HandleCurrentActivePet_Replace/_Retail)
            // only dismisses the pet(s) it's replacing here, AFTER placement has actually succeeded - dismissing
            // earlier would leave the owner with no pet at all if this pet then failed to place, even on the
            // owner's-spot fallback. Destroy() nulls whichever of CurrentActivePet/SecondaryActivePet held
            // each dismissed pet, so the slot assignment right below sees a free primary slot again.
            if (petsPendingSwapDismissal != null)
            {
                foreach (var oldPet in petsPendingSwapDismissal)
                    oldPet.Destroy();

                petsPendingSwapDismissal = null;
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

        /// <summary>
        /// Puts the owner's OTHER combat pet - the Summon 2x sibling - on the same expiry clock as this
        /// freshly summoned one, so a single essence charge always buys a full-duration pair. See
        /// <see cref="PetLifespanSync"/> for why the two drift apart without this, and what the player sees
        /// when they do.
        ///
        /// Always aligns TOWARD the pet just summoned, and declines outright unless that actually leaves the
        /// sibling expiring no earlier than before (PetLifespanSync.ExtendsSibling) - the sync is a
        /// correction, never a nerf. Aligning to the newest pet is also what re-synchronises the pair after
        /// one of them is SLAIN rather than expiring, which is the other way they come apart (Soul Tether's
        /// free resummon makes that common). The two-pet cap is untouched: nothing here reads or writes a
        /// pet slot.
        ///
        /// The clock is THREE values because WorldObject.IsLifespanSpent compares CreationTimestamp +
        /// Lifespan against the wall clock, but only ever on this object's own heartbeat. So Lifespan has to
        /// travel too - Empowered Summons' Loyalty rider makes it per-summon rather than a constant 43 - and
        /// so does the heartbeat phase (NextHeartbeatTime), or the sibling still expires on a tick of its own
        /// up to one heartbeat interval away.
        ///
        /// Called from CombatPet.Init rather than Pet.Init, because it has to run AFTER the duration rider
        /// has set this pet's own Lifespan; there is nothing to sync for a passive pet either way.
        /// </summary>
        protected void SyncSiblingCombatPetLifespan(Player player)
        {
            var sibling = PetLifespanSync.SelectSibling(this, player.CurrentActivePet, player.SecondaryActivePet);

            if (!PetLifespanSync.ShouldSync(this is CombatPet, sibling != null, sibling is CombatPet, sibling?.IsDestroyed ?? false))
                return;

            // a pet with no lifespan at all has no clock to align, in either direction
            if (Lifespan == null || CreationTimestamp == null || sibling.Lifespan == null || sibling.CreationTimestamp == null)
                return;

            if (!PetLifespanSync.ExtendsSibling(CreationTimestamp.Value, Lifespan.Value, sibling.CreationTimestamp.Value, sibling.Lifespan.Value))
                return;

            var siblingBlock = sibling.CurrentLandblock;

            // Never reach into an object another landblock group's thread owns: both the writes below and the
            // heartbeat requeue race that thread, and neither the biota nor the queue is locked. The two pets
            // of one activation are always in the owner's own block, so this only ever declines for a sibling
            // that has wandered far enough to be grouped separately - see PetFollowRules.SharesTickThread,
            // which the follow and teleport paths gate on for the same reason.
            if (siblingBlock == null || CurrentLandblock == null
                || !PetFollowRules.SharesTickThread(siblingBlock == CurrentLandblock, siblingBlock.CurrentLandblockGroup, CurrentLandblock.CurrentLandblockGroup))
                return;

            sibling.CreationTimestamp = CreationTimestamp;
            sibling.Lifespan = Lifespan;
            sibling.NextHeartbeatTime = NextHeartbeatTime;

            // the queue is sorted by NextHeartbeatTime and drained only from its head, so an entry whose time
            // just moved has to be re-positioned - see Landblock.ResortWorldObjectIntoSortedHeartbeatList
            siblingBlock.ResortWorldObjectIntoSortedHeartbeatList(sibling);
        }

        /// <summary>
        /// This pet's physics radius as Init read it at summon time. Kept so GetOwnerPlacementDistance never
        /// has to read the dat again from a landblock tick thread.
        /// </summary>
        private float placementPetRadius;

        /// <summary>
        /// How far in front of <paramref name="owner"/> a pet is placed: the owner's physics radius, this pet's
        /// own (as read by Init), and MinDistance. The single definition of the summon spot - Init spawns the
        /// pet there, and CombatPet's stuck-follow teleport puts it back on the same spot.
        /// </summary>
        protected float GetOwnerPlacementDistance(Player owner)
        {
            return owner.PhysicsObj.GetPhysicsRadius() + placementPetRadius + MinDistance;
        }

        /// <summary>
        /// The spot <paramref name="distance"/> in front of <paramref name="owner"/>, with its cell resolved, or
        /// NULL when that spot is not worth trying and the caller should use the owner's own spot
        /// (PetFollowRules.InFrontPlacementAllowed): always null for an owner indoors, where the point ahead can
        /// resolve cleanly into the next room past a wall, and null when the owner has no line of sight to the
        /// spot (a fence or exterior wall between them). Also null outdoors when the point falls off the
        /// owner's landblock: InFrontOf offsets X/Y without carrying the point across a block edge, so it would
        /// fall outside 0..BlockLength and GetOutdoorCell would truncate it to a nonsense cell.
        /// </summary>
        protected static Position GetInFrontPlacement(Player owner, float distance, bool faceOwner)
        {
            var ownerLoc = owner.Location;

            if (ownerLoc.Indoors)
                return null;

            var dest = ownerLoc.InFrontOf(distance, faceOwner);

            if (dest.PositionX < 0 || dest.PositionX >= Position.BlockLength || dest.PositionY < 0 || dest.PositionY >= Position.BlockLength)
                return null;

            var cell = dest.GetCell();

            // the sight test needs the resolved cell on the candidate, so stamp it on a copy first
            var candidate = new Position(dest);
            candidate.LandblockId = new LandblockId(cell);

            // Line of sight from the owner to the spot catches the outdoor fence / exterior wall case, where the
            // pet would land exactly where it was sent on the wrong side. IsDirectVisible(Position) traces a
            // sight object FROM the spot TO the owner and reads only the owner's physics object, so the pet
            // need not be in the world or in position yet - the same pre-placement use ThreadCachePlacer makes
            // of it. Callers run on the owner's tick thread (Pet.Init from the owner's use action; the follow
            // teleport only when SharesTickThread holds).
            if (!PetFollowRules.InFrontPlacementAllowed(ownerLoc.LandblockId.Raw, cell, () => owner.IsDirectVisible(candidate)))
                return null;

            return candidate;
        }

        /// <summary>
        /// A copy of the owner's own position, cell included - a spot a creature stood on a moment ago. Pets
        /// are Ethereal and an ethereal mover passes through non-static objects (PhysicsObj.FindObjCollisions),
        /// so the owner standing there does not block it. Always a copy: placement adjusts Location in place.
        /// </summary>
        protected static Position GetOwnerSpotPlacement(Player owner)
        {
            return new Position(owner.Location);
        }

        /// <summary>
        /// Whether this pet's physics object actually ended up at <paramref name="requested"/> after a placement
        /// that reported OK (PetFollowRules.AcceptPlacement). A Slide placement accepts whatever spot the
        /// transition resolves to, so an OK result alone does not mean the pet is where it was sent.
        /// </summary>
        protected bool IsPlacedAt(Position requested)
        {
            var physicsObj = PhysicsObj;

            if (physicsObj == null || requested == null)
                return false;

            return PetFollowRules.AcceptPlacement(requested.LandblockId.Raw, requested.Pos,
                physicsObj.Position.ObjCellID, physicsObj.Position.Frame.Origin, physicsObj.CurCell != null);
        }

        /// <summary>
        /// A physics teleport of this pet, the Creature.ForceHome pattern: SetPosition with the Teleport flag, and
        /// Slide as well, like the admin move-object command, so a destination a little inside geometry resolves
        /// to the nearest valid spot. Moves the physics object only; the caller syncs Location and broadcasts
        /// with <see cref="SyncTeleportedPosition"/> once it has settled on a final spot, so a rejected attempt
        /// is never sent to clients.
        /// </summary>
        protected Physics.Common.SetPositionError PhysicsTeleport(Position destination)
        {
            var setPos = new Physics.Common.SetPosition(new Physics.Common.Position(destination),
                Physics.Common.SetPositionFlags.Teleport | Physics.Common.SetPositionFlags.Slide, Location.Instance);

            return PhysicsObj.SetPosition(setPos);
        }

        /// <summary>
        /// Syncs Location from physics after a teleport (which relocates the pet between landblocks when the block
        /// changed) and broadcasts it. adminMove advances ObjectTeleport so clients place the pet instead of
        /// sliding it across the gap.
        /// </summary>
        protected void SyncTeleportedPosition()
        {
            UpdatePosition_SyncLocation();

            SendUpdatePosition(true);
        }

        /// <summary>
        /// Pet.Init's second chance for a summon whose placement in front of the owner succeeded but landed
        /// somewhere else: teleports the already-entered pet onto the owner's own spot. When even that is
        /// refused the pet stays where the first placement put it - what every summon did before this check.
        /// </summary>
        private void RelocateToOwnerSpot(Player owner)
        {
            var result = PhysicsTeleport(GetOwnerSpotPlacement(owner));

            if (result != Physics.Common.SetPositionError.OK)
            {
                log.Debug($"{Name} (0x{Guid}) summon placement slid away from the spot in front of {owner.Name} and the owner's spot was refused: {result}");
                return;
            }

            SyncTeleportedPosition();

            SetPosition(PositionType.Home, new Position(Location));
        }

        public bool? HandleCurrentActivePet(Player player, bool spawnStagger)
        {
            if (PropertyManager.GetBool("pet_stow_replace").Item)
                return HandleCurrentActivePet_Replace(player, spawnStagger);
            else
                return HandleCurrentActivePet_Retail(player, spawnStagger);
        }

        public bool HandleCurrentActivePet_Replace(Player player, bool spawnStagger)
        {
            // Owner ruling (2026-09-23): checked BEFORE the "no active pet" early return below, and before
            // the Summon 2x "add a second" branch further down. The primary slot can be null while a LIVE
            // combat pet still occupies the secondary (Summon 2x) slot - WorldObject.Destroy only clears
            // whichever slot pointer matched the pet that died, so a primary's death leaves a live secondary
            // behind. The first summon of a fresh activation must retire that stale secondary too, not just
            // take the free primary slot and leave the old secondary running. Same essence or different,
            // Summon 2x or not - see CombatPetRetireRules' doc comment for the full ruling. Dismissal is only
            // QUEUED here, not performed - see Init for why it has to wait until this pet's own placement has
            // succeeded.
            if (this is CombatPet && CombatPetRetireRules.ShouldRetireActivePets(!spawnStagger,
                ActiveCombatPetWcid(player.CurrentActivePet), ActiveCombatPetWcid(player.SecondaryActivePet)))
            {
                QueuePetForSwapDismissal(player.CurrentActivePet);
                QueuePetForSwapDismissal(player.SecondaryActivePet);
                return true;
            }

            // original ace logic
            if (player.CurrentActivePet == null)
                return true;

            if (player.CurrentActivePet is CombatPet)
            {
                // class ability: Summon 2x permits the SECOND pet of this SAME activation into the free slot
                // alongside the one it just placed - never reached on the first summon, since the retirement
                // above always claims that case when any combat pet slot is populated.
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

        public bool? HandleCurrentActivePet_Retail(Player player, bool spawnStagger)
        {
            // Owner ruling (2026-09-23) - see the matching block in HandleCurrentActivePet_Replace for why
            // this runs before the "no active pet" early return below: the primary slot can be null while a
            // LIVE combat pet still occupies the secondary (Summon 2x) slot. Applied identically to both
            // pet_stow_replace branches so they can never disagree. "this is CombatPet" also keeps this from
            // ever firing for a passive-pet device (CombatPet is never IsPassivePet), so every passive-pet
            // path below is unaffected.
            if (this is CombatPet && CombatPetRetireRules.ShouldRetireActivePets(!spawnStagger,
                ActiveCombatPetWcid(player.CurrentActivePet), ActiveCombatPetWcid(player.SecondaryActivePet)))
            {
                QueuePetForSwapDismissal(player.CurrentActivePet);
                QueuePetForSwapDismissal(player.SecondaryActivePet);
                return true;
            }

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
                    // class ability: Summon 2x permits the SECOND pet of this SAME activation into the free
                    // slot alongside the one it just placed - never reached on the first summon, since the
                    // retirement above always claims that case when any combat pet slot is populated.
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
