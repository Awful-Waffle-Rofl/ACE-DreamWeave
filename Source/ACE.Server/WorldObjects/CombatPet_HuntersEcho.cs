using System;
using System.Collections.Generic;

using log4net;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Hunter's Echo (Archer T3): a translucent copy of the archer that stands where the archer stood, pulls the
    /// archer's attackers onto itself and fires the archer's bow at them for a few seconds.
    ///
    /// A COMBATPET, NOT A NEW TYPE. The fork never subclasses WorldObject; the Echo is an ordinary CombatPet weenie
    /// (WorldObjectFactory maps WeenieType.CombatPet to CombatPet) flagged by <see cref="IsHuntersEcho"/>, which is
    /// set only by <see cref="InitAsHuntersEcho"/>. It NEVER runs Pet.Init: that path is bound to a pet device and
    /// the owner's pet slots, retires the owner's other combat pets, and arms Soul Tether. The Echo holds no slot,
    /// has no device, and its death never reaches Player.OnCombatPetDied.
    ///
    /// NEVER PERSISTED. WeenieType.CombatPet is refused by WorldObject.IsDynamicThatShouldPersistToShard, the Echo
    /// is created fresh from its weenie (no biota row exists to originate from), and its cloned bow and arrows are
    /// only ever WIELDED by it (TryEquipObject), never placed in an inventory that saves. Destroying the Echo
    /// destroys them (WorldObject.Destroy walks a creature's EquippedObjects). Nothing in this file calls
    /// SaveBiotaToDatabase; HuntersEchoWiringSourceScanTests pins that.
    ///
    /// PVE ONLY. A player can never damage it (Player.CanDamage refuses every CombatPet) and it can never damage a
    /// player: it only ever picks monsters (engaged with its owner unless the owner has /summonaggro on), Creature.CanDamage refuses a pet hitting
    /// a player, and its arrow impact refuses a Player target outright before the owner's DamageTarget runs.
    ///
    /// ITS ARROWS ARE THE OWNER'S. Each arrow resolves through the owner's own Player.DamageTarget with the owner's
    /// real launcher and ammo (see <see cref="FireHuntersEchoVolley"/>), so damage is live, and kills, XP, loot and
    /// quest credit go to the owner exactly as the owner's own arrows would. The owner's ITEM procs never ride an
    /// Echo arrow (nothing on this path calls TryProcEquippedItems), otherwise Echo uptime would feed itself.
    /// </summary>
    partial class CombatPet
    {
        private static readonly ILog echoLog = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The Echo's weenie (Content/sql/weenies/1008050 Hunters Echo.sql).</summary>
        public const uint HuntersEchoWcid = 1008050;

        /// <summary>
        /// How far from its owner the Echo may be before it ends, in metres. The Echo never moves, so this is how far
        /// the owner may walk away from it. Fixed, not a tunable.
        /// </summary>
        public const float HuntersEchoLeashDistance = 40.0f;

        /// <summary>TRUE for the Hunter's Echo; set only by InitAsHuntersEcho. Runtime only.</summary>
        public bool IsHuntersEcho { get; private set; }

        /// <summary>Timers.RunningTime at which the Echo ends. Runtime only.</summary>
        private double huntersEchoExpiresAt;

        /// <summary>The empty-pick backoff for this Echo's target pick (runtime only).</summary>
        private readonly HuntersEchoPickGate huntersEchoPickGate = new HuntersEchoPickGate();

        /// <summary>The owner's ObjDesc at spawn, replayed by CalculateObjDesc (the Corpse.CalculateObjDesc precedent).</summary>
        private ObjDesc huntersEchoObjDesc;

        /// <summary>Per damage type, the multiplier the owner's armor, resistances and damage resist rating would apply to a weapon hit.</summary>
        private Dictionary<DamageType, float> huntersEchoPhysicalMitigation;

        /// <summary>Per damage type, the multiplier the owner's resistances and magic damage resist rating would apply to a spell.</summary>
        private Dictionary<DamageType, float> huntersEchoMagicMitigation;

        private static readonly DamageType[] HuntersEchoDamageTypes =
        {
            DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Fire, DamageType.Cold,
            DamageType.Acid, DamageType.Electric, DamageType.Nether, DamageType.Health,
        };

        /// <summary>
        /// Creates the Echo for <paramref name="owner"/> from its weenie and puts it in the world on the owner's own
        /// spot for <paramref name="seconds"/>. Returns null when it could not be placed (nothing is left behind).
        /// </summary>
        internal static CombatPet SpawnHuntersEcho(Player owner, double seconds)
        {
            if (owner?.Location == null || owner.CurrentLandblock == null)
                return null;

            if (!(WorldObjectFactory.CreateNewWorldObject(HuntersEchoWcid) is CombatPet echo))
            {
                echoLog.Error($"[HUNTERS ECHO] weenie {HuntersEchoWcid} is missing or is not a CombatPet; no Echo for {owner.Name}");
                return null;
            }

            if (echo.InitAsHuntersEcho(owner, seconds))
                return echo;

            echo.Destroy();
            return null;
        }

        /// <summary>
        /// Shapes this freshly created pet into the owner's Echo and enters it into the world. NEVER calls Pet.Init
        /// (see the class comment). Returns FALSE when the Echo could not be placed.
        /// </summary>
        private bool InitAsHuntersEcho(Player owner, double seconds)
        {
            IsHuntersEcho = true;

            Name = owner.Name + "'s Echo";

            PetOwner = owner.Guid.Full;
            P_PetOwner = owner;

            NoCorpse = true;

            CopyHuntersEchoAppearance(owner);

            var translucency = HuntersEchoMath.Translucency(PropertyManager.GetDouble("class_ability_huntersecho_translucency").Item);
            Translucency = translucency;

            WieldHuntersEchoGear(owner, translucency);

            SnapshotHuntersEchoDefenses(owner);

            SnapshotHuntersEchoShot(owner);

            SnapshotHuntersEchoWeapons(owner);

            // same faction treatment as an ordinary combat pet (CombatPet.Init)
            Faction1Bits = owner.Faction1Bits;

            // layered over the owner: the owner's exact spot (pets are Ethereal, so sharing it never collides), then
            // the same spot lifted by the pet placement bump (Pet.GetLiftedOwnerSpotPlacement) as the one retry
            Location = GetOwnerSpotPlacement(owner);

            if (!EnterWorld())
            {
                Location = GetLiftedOwnerSpotPlacement(owner);

                if (!EnterWorld())
                {
                    echoLog.Debug($"[HUNTERS ECHO] could not place {Name} on {owner.Name}'s spot");
                    return false;
                }
            }

            SetCombatMode(GetEquippedMissileWeapon() != null ? CombatMode.Missile : CombatMode.Melee);

            MonsterState = State.Awake;
            IsAwake = true;

            SetHuntersEchoTimer(seconds);

            LogSummoned(owner, null, null);

            return true;
        }

        /// <summary>
        /// A second trigger while the Echo lives: health back to full, level, attributes, vitals and defenses
        /// snapshotted again, the owner's shot taken again, the timer restarted at <paramref name="seconds"/>, and the
        /// owner's attackers taunted again. Returns how many were taunted.
        /// </summary>
        internal int RefreshHuntersEcho(Player owner, double seconds)
        {
            // engage at once: a refresh never waits out an empty-pick backoff
            huntersEchoPickGate.Reset();

            SnapshotHuntersEchoDefenses(owner);

            SnapshotHuntersEchoShot(owner);

            SnapshotHuntersEchoWeapons(owner);

            SetHuntersEchoTimer(seconds);

            return TauntForHuntersEcho(owner);
        }

        /// <summary>
        /// A refresh's move back onto the owner: the Echo is layered on the player by design, so a re-trigger after the
        /// owner walked away must not leave it absorbing hits where the owner used to stand. QUEUED by
        /// Player.TriggerHuntersEcho on this Echo's own action queue (never inline from a collision callback), and only
        /// for the same landblock and instance, so the owner shares this thread here.
        ///
        /// Skipped outright when the Echo already stands within HuntersEchoMath.RepositionSkipDistance of the owner, so a
        /// refresh in place sends no teleport broadcast and no client snap. Otherwise the pet placement precedent
        /// (Pet.PlaceBesideOwner): the owner's spot, kept only if IsPlacedAt; then the lifted owner spot, kept only if
        /// IsPlacedAtLiftedOwnerSpot; and a rejected landing is moved back to where the Echo stood
        /// (MoveBackAfterRejectedLift). A move that fails leaves the live Echo where it is: FALSE, nothing replaced.
        /// </summary>
        internal bool RepositionHuntersEcho(Player owner)
        {
            if (owner?.Location == null || owner.PhysicsObj == null || PhysicsObj == null || Location == null)
                return false;

            if (!HuntersEchoMath.ShouldReposition(GetCylinderDistance(owner)))
                return true;

            StopMovingForTeleport();
            IsTurning = false;

            var before = new Physics.Common.Position(PhysicsObj.Position);
            var instance = Location.Instance;
            var moved = false;

            var ownerSpot = GetOwnerSpotPlacement(owner);

            if (PhysicsTeleport(ownerSpot) == Physics.Common.SetPositionError.OK)
            {
                moved = true;

                if (IsPlacedAt(ownerSpot))
                {
                    SyncTeleportedPosition();
                    return true;
                }
            }

            var lifted = GetLiftedOwnerSpotPlacement(owner);

            if (PhysicsTeleport(lifted) == Physics.Common.SetPositionError.OK)
            {
                moved = true;

                if (IsPlacedAtLiftedOwnerSpot(lifted, instance))
                {
                    SyncTeleportedPosition();
                    return true;
                }
            }

            // neither spot held: put the Echo back where it stood, and broadcast only if that also failed and it moved
            if (moved && !MoveBackAfterRejectedLift(before, instance))
                SyncTeleportedPosition();

            echoLog.Debug($"[HUNTERS ECHO] {Name} could not be moved back onto {owner.Name}; it stays where it stood");

            return false;
        }

        /// <summary>
        /// Starts (or restarts) the Echo's own clock. The pet heartbeat's Lifespan check is far too coarse for a
        /// 6-8 s life (it only runs on the heartbeat), so the Echo ends itself from its monster tick; Lifespan is kept a
        /// few seconds longer purely as a backstop should that tick ever stop.
        /// </summary>
        private void SetHuntersEchoTimer(double seconds)
        {
            huntersEchoExpiresAt = Timers.RunningTime + Math.Max(0.0, seconds);

            CreationTimestamp = (int)Time.GetUnixTime();
            Lifespan = (int)Math.Ceiling(Math.Max(0.0, seconds)) + 5;
        }

        /// <summary>Seconds the Echo has left, never negative.</summary>
        internal double HuntersEchoSecondsLeft => Math.Max(0.0, huntersEchoExpiresAt - Timers.RunningTime);

        /// <summary>
        /// The corpse recipe (Creature.CreateCorpse) applied to a living copy: the owner's setup, motion and sound tables,
        /// palette and clothing base and scale, plus a snapshot of the owner's full ObjDesc, which is what makes the Echo
        /// wear the owner's armor and face.
        /// </summary>
        private void CopyHuntersEchoAppearance(Player owner)
        {
            SetupTableId = owner.SetupTableId;
            MotionTableId = owner.MotionTableId;
            SoundTableId = owner.SoundTableId;
            PaletteBaseDID = owner.PaletteBaseDID;
            ClothingBase = owner.ClothingBase;
            PhysicsTableId = owner.PhysicsTableId;

            if (owner.ObjScale.HasValue)
                ObjScale = owner.ObjScale;
            if (owner.PaletteTemplate.HasValue)
                PaletteTemplate = owner.PaletteTemplate;
            if (owner.Shade.HasValue)
                Shade = owner.Shade;

            huntersEchoObjDesc = owner.CalculateObjDesc();
        }

        /// <summary>
        /// The Echo replays the owner's ObjDesc captured at spawn. Every other combat pet takes the Creature path.
        /// </summary>
        public override ObjDesc CalculateObjDesc()
        {
            var snapshot = huntersEchoObjDesc;

            if (snapshot == null)
                return base.CalculateObjDesc();

            var objDesc = new ObjDesc { PaletteID = snapshot.PaletteID };

            objDesc.SubPalettes.AddRange(snapshot.SubPalettes);
            objDesc.TextureChanges.AddRange(snapshot.TextureChanges);
            objDesc.AnimPartChanges.AddRange(snapshot.AnimPartChanges);

            return objDesc;
        }

        /// <summary>
        /// Transient clones of the owner's missile weapon and ammo, for the LOOK only: the Echo's arrows fly with the
        /// owner's real launcher and ammo (FireHuntersEchoVolley). Each clone is a fresh object from the item's weenie,
        /// translucent like the Echo, with its cast-on-strike proc stripped, wielded straight onto the Echo with
        /// TryEquipObject (no item spells are activated, no inventory is touched). The early return for an owner with
        /// no missile weapon is DEFENSIVE ONLY: since 2026-11-11 no Echo is summoned unless the owner holds a missile
        /// weapon (Player.CanTriggerHuntersEcho). Wielded at SPAWN only: a refresh re-snapshots the real weapon it fires
        /// (huntersEchoWeapons) but keeps the clones it already wears.
        /// </summary>
        private void WieldHuntersEchoGear(Player owner, float translucency)
        {
            var weapon = owner.GetEquippedMissileWeapon();

            if (weapon == null)
                return;

            WieldHuntersEchoClone(weapon, translucency);

            if (weapon.IsAmmoLauncher)
            {
                var ammo = owner.GetEquippedAmmo();

                if (ammo != null)
                    WieldHuntersEchoClone(ammo, translucency);
            }
        }

        private void WieldHuntersEchoClone(WorldObject source, float translucency)
        {
            var clone = WorldObjectFactory.CreateNewWorldObject(source.WeenieClassId);

            if (clone == null)
                return;

            if (source.PaletteTemplate.HasValue)
                clone.PaletteTemplate = source.PaletteTemplate;
            if (source.Shade.HasValue)
                clone.Shade = source.Shade;

            clone.Translucency = translucency;

            // a clone is never a proc source (and the Echo path never rolls one anyway)
            clone.ProcSpell = null;
            clone.ProcSpellRate = null;

            var location = source.CurrentWieldedLocation ?? (EquipMask)(source.ValidLocations ?? 0);

            if (location == 0 || !TryEquipObject(clone, location))
                clone.Destroy();
        }

        /// <summary>The six primary attributes, in the order the appraisal panel lists them.</summary>
        private static readonly PropertyAttribute[] HuntersEchoAttributes =
        {
            PropertyAttribute.Strength, PropertyAttribute.Endurance, PropertyAttribute.Coordination,
            PropertyAttribute.Quickness, PropertyAttribute.Focus, PropertyAttribute.Self,
        };

        /// <summary>
        /// The defense snapshot, taken at spawn and again on refresh: the owner's level, attributes, vitals and defense
        /// skills (<see cref="MatchHuntersEchoStats"/>), and a per-damage-type mitigation multiplier standing in for the
        /// owner's armor, resistances and damage resist rating.
        ///
        /// THE ARMOR TERM IS AN APPROXIMATION: it is the owner's chest coverage (armor layers plus body armor
        /// enchantments, through the same Creature.GetArmorMod curve a monster's hit on a player uses), not a per-body
        /// part roll. The Echo's own weenie carries no armor, every resist at 1.0 and no ratings, so the multiplier is
        /// the whole of its mitigation.
        /// </summary>
        private void SnapshotHuntersEchoDefenses(Player owner)
        {
            MatchHuntersEchoStats(owner);

            var physical = new Dictionary<DamageType, float>();
            var magic = new Dictionary<DamageType, float>();

            var armorLayers = owner.GetArmorLayers(owner, BodyPart.Chest);
            var meleeRating = owner.GetDamageResistRatingMod(CombatType.Melee, true, null);
            var magicRating = owner.GetDamageResistRatingMod(CombatType.Magic, true, null);

            foreach (var damageType in HuntersEchoDamageTypes)
            {
                // the same overload a monster's hit on the owner resolves through (protection or natural resistance,
                // vulnerability, resistance augmentations); no attacker or weapon, so no hollow or rending term
                var resist = owner.GetResistanceMod(damageType, null, null);

                var armor = damageType == DamageType.Health ? 1.0 : owner.GetArmorMod(owner, damageType, armorLayers, null);

                physical[damageType] = HuntersEchoMath.MitigationFactor(armor, resist, meleeRating);
                magic[damageType] = HuntersEchoMath.MitigationFactor(1.0, resist, magicRating);
            }

            huntersEchoPhysicalMitigation = physical;
            huntersEchoMagicMitigation = magic;
        }

        /// <summary>
        /// Makes this Echo's level, attributes, vitals and defense skills read as <paramref name="source"/>'s (the
        /// owner), which is also exactly what an appraisal of the Echo reports (CreatureProfile reads each attribute's
        /// Current and each vital's Current / MaxValue; the level is PropertyInt.Level). Current values, buffs
        /// included: the Echo carries no enchantments, so its Current is its Base.
        ///
        /// ORDER IS LOAD-BEARING. A vital's maximum and a skill's Current both add an attribute formula
        /// (AttributeFormula), so the attributes are written FIRST; each vital and skill is then matched against its
        /// value under the new attributes, which leaves maximum health, stamina and mana and Melee / Missile / Magic
        /// Defense equal to the owner's. Health, stamina and mana are filled. Internal so a test can drive it with two
        /// plain creatures; it reads nothing Player-only.
        /// </summary>
        internal void MatchHuntersEchoStats(Creature source)
        {
            Level = source.Level;

            foreach (var attribute in HuntersEchoAttributes)
            {
                var mine = Attributes[attribute];

                mine.StartingValue = HuntersEchoMath.MatchedInitLevel(mine.StartingValue, mine.Current, source.Attributes[attribute].Current);
            }

            foreach (var (mine, theirs) in new[] { (Health, source.Health), (Stamina, source.Stamina), (Mana, source.Mana) })
            {
                mine.StartingValue = HuntersEchoMath.MatchedStartingValue(mine.StartingValue, mine.MaxValue, theirs.MaxValue);
                mine.Current = mine.MaxValue;
            }

            foreach (var skill in new[] { Skill.MeleeDefense, Skill.MissileDefense, Skill.MagicDefense })
            {
                var mine = GetCreatureSkill(skill);
                var theirs = source.GetCreatureSkill(skill);

                if (mine == null || theirs == null)
                    continue;

                mine.InitLevel = HuntersEchoMath.MatchedInitLevel(mine.InitLevel, mine.Current, theirs.Current);
            }
        }

        /// <summary>
        /// The owner's shot this Echo fires with until the next refresh (HuntersEchoShot): its name for the hit line,
        /// the accuracy bar and the reload speed of the owner's most recent missile shot (or the full-bar fallback).
        /// Stamped on each arrow (Creature.LaunchProjectile) and read by Monster_Missile.LaunchMissile for the cadence.
        /// </summary>
        private HuntersEchoShot huntersEchoShot;

        /// <summary>The Echo's current shot, or null for every other combat pet (whose launches then read as before).</summary>
        internal HuntersEchoShot HuntersEchoShotStamp => IsHuntersEcho ? huntersEchoShot : null;

        /// <summary>A copy of where the Echo stands now, for the arrow it is launching (Long Draw's origin); null for any other pet.</summary>
        internal Position HuntersEchoLaunchOriginStamp => IsHuntersEcho && Location != null ? new Position(Location) : null;

        /// <summary>Takes the owner's shot (Player.CaptureHuntersEchoShot), at spawn and at refresh. Name is set first.</summary>
        private void SnapshotHuntersEchoShot(Player owner)
        {
            huntersEchoShot = owner.CaptureHuntersEchoShot(Name);
        }

        /// <summary>
        /// The owner's REAL missile weapon, launcher and ammo this Echo fires with (owner ruling 2026-11-11: it keeps the
        /// weapon held when it appeared, through any later swap). Taken at spawn and again at refresh (a refresh is a new
        /// trigger, which the gate only allows with a missile weapon wielded). Runtime only.
        /// </summary>
        private HuntersEchoWeapons huntersEchoWeapons;

        /// <summary>
        /// Takes the owner's weapon set (Player.CaptureHuntersEchoWeapons), at spawn and at refresh, with this Echo as its
        /// shooter (retaliation inside its volley lands on it), and clears the weapon-gone memory.
        /// </summary>
        private void SnapshotHuntersEchoWeapons(Player owner)
        {
            huntersEchoWeapons = owner.CaptureHuntersEchoWeapons()?.WithShooter(this);
            huntersEchoWeaponGoneUntil = 0.0;
        }

        /// <summary>
        /// Running time until which the snapshot weapon is taken as still gone without asking again
        /// (HuntersEchoMath.WeaponGoneRecheckSeconds after a failed check). 0 = ask. Cleared by every snapshot.
        /// </summary>
        private double huntersEchoWeaponGoneUntil;

        /// <summary>The owner-equivalent multiplier for a melee or missile hit of <paramref name="damageType"/> on the Echo.</summary>
        internal float HuntersEchoPhysicalMitigation(DamageType damageType) => LookupMitigation(huntersEchoPhysicalMitigation, damageType);

        /// <summary>The owner-equivalent multiplier for a spell (projectile or Harm) of <paramref name="damageType"/> on the Echo.</summary>
        internal float HuntersEchoMagicMitigation(DamageType damageType) => LookupMitigation(huntersEchoMagicMitigation, damageType);

        private static float LookupMitigation(Dictionary<DamageType, float> table, DamageType damageType)
        {
            if (table == null)
                return 1.0f;

            // a multi-type hit (Slash | Pierce) is looked up by its first listed type, the way SelectDamageType would
            // pick one; an unknown type takes no mitigation
            if (table.TryGetValue(damageType, out var factor))
                return factor;

            foreach (var kvp in table)
                if ((damageType & kvp.Key) != 0)
                    return kvp.Value;

            return 1.0f;
        }

        /// <summary>
        /// The hit that made the owner's cloak fire its spell proc, taken by the Echo INSTEAD of the owner. Already
        /// fully mitigated as the owner (it is the amount the owner would have written), so it is written straight to
        /// the Echo's health through the plain Creature path, with no Echo mitigation on top.
        /// </summary>
        internal void ApplyHuntersEchoAbsorbedHit(WorldObject source, DamageType damageType, uint amount)
        {
            if (amount == 0 || IsDead || IsDestroyed)
                return;

            base.TakeDamage(source, damageType, amount, false, IncomingDamageOrigin.Secondary);
        }

        /// <summary>
        /// The Echo's taunt, on spawn and on refresh: every monster whose AttackTarget is the owner or the Echo is taunted
        /// onto the Echo for the Echo's remaining life. A hold the OWNER has (from Taunt, which runs ahead of this
        /// ability in registry order) is handed to the Echo; a hold another creature has is respected. Only monsters in
        /// the Echo's own landblock group are written.
        ///
        /// ACCEPTED LIMITATION: Monster.FindNextTarget drops a taunt hold whose taunter it cannot see directly, so a
        /// monster that loses line of sight to the Echo goes back to its normal target choice.
        /// </summary>
        internal int TauntForHuntersEcho(Player owner)
        {
            if (owner?.PhysicsObj == null || CurrentLandblock == null)
                return 0;

            var duration = HuntersEchoSecondsLeft;

            if (duration <= 0.0)
                return 0;

            var taunted = 0;

            foreach (var obj in owner.PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj.WorldObject != null))
            {
                var monster = obj.WeenieObj.WorldObject as Creature;

                if (!IsHuntersEchoTauntCandidate(monster, owner, this))
                    continue;

                if (monster.CurrentLandblock == null || monster.CurrentLandblock.CurrentLandblockGroup != CurrentLandblock.CurrentLandblockGroup)
                    continue;

                if (ReferenceEquals(monster.TauntTarget, owner))
                    monster.TauntTarget = null;

                if (monster.ApplyTaunt(this, duration))
                    taunted++;
            }

            return taunted;
        }

        /// <summary>
        /// A monster the Echo taunts: a live, non-player, non-pet creature that is attacking the owner or the Echo.
        /// Pure over its arguments' fields.
        /// </summary>
        internal static bool IsHuntersEchoTauntCandidate(Creature monster, Creature owner, Creature echo)
        {
            if (monster == null || monster is Player || monster is CombatPet || monster.IsDead || monster.Teleporting)
                return false;

            var target = monster.AttackTarget;

            return target != null && (ReferenceEquals(target, owner) || ReferenceEquals(target, echo));
        }

        /// <summary>
        /// Why the Echo must end now, or null while it may stay: its time is up, or its owner is gone, dead, portaling,
        /// on another landblock or out of range, or has a newer Echo. A weapon swap never ends it (owner ruling 2026-11-11).
        /// </summary>
        internal string HuntersEchoEndReason(Player owner)
        {
            if (Timers.RunningTime >= huntersEchoExpiresAt)
                return "expire";

            if (owner == null || owner.IsDestroyed || owner.PhysicsObj == null || owner.Location == null)
                return "ownerlost";

            if (owner.IsDead)
                return "ownerdeath";

            if (owner.Teleporting)
                return "ownerportal";

            if (!ReferenceEquals(owner.HuntersEcho, this))
                return "superseded";

            if (owner.CurrentLandblock == null || !ReferenceEquals(owner.CurrentLandblock, CurrentLandblock))
                return "ownerlandblock";

            if (Location == null || owner.Location.Instance != Location.Instance || GetCylinderDistance(owner) > HuntersEchoLeashDistance)
                return "leash";

            return null;
        }

        /// <summary>Ends the Echo: logged with its reason, unlinked from its owner, and destroyed.</summary>
        internal void EndHuntersEcho(string reason)
        {
            // idempotent: a queued end (Player.QueueEndHuntersEcho) can arrive after the Echo already ended itself
            if (IsDestroyed)
                return;

            RemovalReason = reason;

            var owner = P_PetOwner;

            if (owner != null && ReferenceEquals(owner.HuntersEcho, this))
                owner.HuntersEcho = null;

            Destroy();
        }

        /// <summary>
        /// The Echo's whole think, run from Monster_Tick in place of the pet branches: end it when its time or its owner
        /// says so; otherwise keep (or pick, on this same tick) a monster engaged with its side (any valid monster when the
        /// owner has /summonaggro on), face it, and shoot when
        /// the attack is ready. It NEVER moves and NEVER runs a physics turn: it faces its target instantly
        /// (FaceTargetDirectly - a heading write plus a position broadcast), so it holds no IsTurning / IsMoving state
        /// that a turn which never completes could leave set. (Until 2026-11-10 it used StartTurn, whose TurnToObject
        /// held IsMoving until the physics finished the turn; every tick spent waiting on it was a tick the Echo neither
        /// picked a new target nor shot, out of a six second life.)
        /// </summary>
        internal void TickHuntersEcho()
        {
            var owner = P_PetOwner;

            var reason = HuntersEchoEndReason(owner);

            if (reason != null)
            {
                EndHuntersEcho(reason);
                return;
            }

            // stale turn / move state (nothing on the Echo path starts one any more): cleared, never waited on
            if (IsTurning || IsMoving)
                ClearHuntersEchoMovement();

            // with a bow: the outdoor radar range everywhere and a clear projectile path. Without one (defensive only: the trigger gate requires
            // the owner to hold a missile weapon, so a bow-less Echo is not reachable today): the old rule, it never shoots
            var hasBow = GetEquippedMissileWeapon() != null;
            var engageRange = hasBow ? HuntersEchoEngageRange() : float.MaxValue;

            // an Echo holding no target picks again only after the empty-pick backoff (HuntersEchoPickGate)
            if (!huntersEchoPickGate.ShouldPick(AttackTarget != null, Timers.RunningTime))
                return;

            var target = SelectHuntersEchoTarget(hasBow, engageRange);

            huntersEchoPickGate.OnPicked(target != null, Timers.RunningTime);

            if (target == null)
            {
                if (AttackTarget != null)
                {
                    AttackTarget = null;
                    ResetAttack();
                }

                return;
            }

            if (!ReferenceEquals(AttackTarget, target))
            {
                AttackTarget = target;
                ResetAttack();
            }

            // no bow (defensive only, unreachable from a trigger since 2026-11-11): never shoot
            if (!hasBow)
                return;

            if (!IsFacing(target))
                FaceTargetDirectly(target);

            if (CurrentAttack == null)
            {
                CurrentAttack = CombatType.Missile;
                MaxRange = engageRange;
            }

            // the snapshot weapon must still be the owner's to shoot (checked only when a shot is due, so the inventory
            // walk runs once per shot, not every tick); one that left the owner keeps the Echo up but silent
            if (AttackReady() && HuntersEchoWeaponStillOwned(owner))
                Attack();
        }

        /// <summary>
        /// Whether the weapon this Echo was summoned with is still its owner's (Player.OwnsHuntersEchoItem). A "no" is
        /// remembered for HuntersEchoMath.WeaponGoneRecheckSeconds, so an Echo whose weapon is gone does not walk the
        /// owner's side packs every tick; a refresh clears it (SnapshotHuntersEchoWeapons).
        /// </summary>
        private bool HuntersEchoWeaponStillOwned(Player owner)
        {
            if (owner == null)
                return false;

            var now = Timers.RunningTime;

            if (now < huntersEchoWeaponGoneUntil)
                return false;

            if (owner.OwnsHuntersEchoItem(huntersEchoWeapons?.Weapon))
                return true;

            huntersEchoWeaponGoneUntil = now + HuntersEchoMath.WeaponGoneRecheckSeconds;
            return false;
        }

        /// <summary>
        /// The Echo's shooting range: the outdoor radar range everywhere (HuntersEchoMath.RadarRange, owner ruling
        /// 2026-11-11), never less than its bow's own range. Whether it shoots a candidate in that range is the sight
        /// test's call (HuntersEchoSightTest). Echo only - no other creature's GetMaxRange is touched.
        /// </summary>
        private float HuntersEchoEngageRange() => HuntersEchoMath.EngageRange(HuntersEchoMath.RadarRange, GetMaxMissileRange());

        /// <summary>
        /// Drops any physics turn or move the Echo holds. CancelPhysicsMoveTo, not Creature.CancelMoveTo: that one also
        /// runs FindNextTarget and pushes NextMoveTime out (see CombatPet_Follow).
        /// </summary>
        private void ClearHuntersEchoMovement()
        {
            CancelPhysicsMoveTo();
            IsTurning = false;
        }

        /// <summary>
        /// The Echo keeps its current target while that target is still a legal one, else takes the nearest legal one in
        /// its visible targets (HuntersEchoMath.SelectTarget). Legal: a live monster in its visible targets that passes
        /// the ordinary pet target rule (AcceptsPetTarget) - never a Player, never a pet. While the Echo is held to the
        /// never-initiates rule (its weenie's SummonNeverInitiates, and the owner's /summonaggro is off) the monster must
        /// also be engaged with the owner's side (its AttackTarget is the owner or one of the owner's pets, the Echo
        /// included), so it never picks an idle monster; with /summonaggro on it takes the nearest valid monster, engaged
        /// or not, the way the Archer pet does. The toggle is read ONCE per pick (CombatPet.NeverInitiates takes the
        /// owner's biota lock), so one answer applies to every candidate. With a bow it must
        /// also be within <paramref name="engageRange"/> and in clear line of sight (IsDirectVisible, the missile-path
        /// sight test monster spell projectiles and multishot extra arrows use), and the held target is re-checked on
        /// every tick, so one that walks behind a wall or out of range is dropped for the next legal one at once.
        /// </summary>
        private Creature SelectHuntersEchoTarget(bool hasBow, float engageRange)
        {
            var now = Timers.RunningTime;

            // read once: the owner's /summonaggro setting is live and the same answer must apply to every candidate
            var neverInitiates = HuntersEchoNeverInitiates;

            return HuntersEchoMath.SelectTarget(AttackTarget as Creature, PhysicsObj.ObjMaint.GetVisibleTargetsValuesOfTypeCreature(),
                creature => IsHuntersEchoTarget(creature, neverInitiates) && IsVisibleTarget(creature),
                GetCylinderDistance,
                engageRange,
                hasBow ? (Func<Creature, bool>)(creature => HuntersEchoSightTest(creature, now)) : null,
                HuntersEchoMath.MaxSightTestsPerPick,
                creature => huntersEchoPickGate.SightRecentlyFailed(creature.Guid.Full, now));
        }

        /// <summary>One sight test (IsDirectVisible); a failure is remembered so the next picks skip it for a while.</summary>
        private bool HuntersEchoSightTest(Creature creature, double now)
        {
            if (IsDirectVisible(creature))
                return true;

            huntersEchoPickGate.RecordSightFailure(creature.Guid.Full, now);
            return false;
        }

        /// <summary>
        /// Whether the Echo is held to the never-initiates rule right now: always by design, whatever its weenie's
        /// SummonNeverInitiates row says (so a missing row can never release it), unless the owner has /summonaggro on.
        /// Takes the owner's biota read lock, so a pick reads it once.
        /// </summary>
        internal bool HuntersEchoNeverInitiates => PetEngagement.EffectiveNeverInitiates(true, P_PetOwner?.SummonAggro ?? false);

        internal bool IsHuntersEchoTarget(Creature creature, bool neverInitiates)
        {
            if (creature == null || creature is Player || creature is CombatPet || creature.IsDead || ReferenceEquals(creature, this))
                return false;

            // AcceptsPetTarget requires engagement with the owner's side exactly when neverInitiates holds
            return AcceptsPetTarget(creature, neverInitiates);
        }

        /// <summary>
        /// One Echo volley, from Monster_Missile.LaunchMissile's Echo branch: a cosmetic primary arrow at
        /// <paramref name="primary"/>, then the Multishot extra arrows (the weapon's own MultiShot count plus the owner's
        /// class-ability arrows, through the same FireMultiShotVolley a player volley uses), all with the owner's REAL
        /// weapon, launcher and ammo AS SNAPSHOTTED at spawn / refresh (<see cref="huntersEchoWeapons"/>, owner ruling
        /// 2026-11-11: it keeps firing the weapon held when it appeared), and ONE Pinning Shot roll for the whole volley
        /// (Player.RollClassAbilityVolleyPin). The owner's class-ability rolls run inside that weapon's scope
        /// (Player.WithHuntersEchoWeapons). Every arrow resolves on arrival in <see cref="ResolveHuntersEchoArrowImpact"/>.
        /// No ammo is spent (nothing is decremented and UpdateAmmoAfterLaunch is not called) and no item proc is rolled.
        /// Double Volley rolls once per volley as the owner's own launch does (Player.TryClassAbilityDoubleVolley); a
        /// success re-fires at the primary (in sight only) and repeats the extra-target spread, with the same weapons,
        /// stamps, Pinning roll and impact path, and never rolls again.
        ///
        /// FIRES NOTHING when the snapshot weapon is no longer the owner's (dropped, traded, sold, destroyed - anywhere in
        /// the owner's packs or wielded still counts, Player.OwnsHuntersEchoItem): the Echo stops firing but lives on. Its
        /// ammo is the snapshot stack while that is still the owner's and fits the launcher, else the ammo the owner has
        /// equipped now if that fits (HuntersEchoMath.SelectVolleyAmmo), else none and no volley.
        /// </summary>
        internal void FireHuntersEchoVolley(Creature primary, float projectileSpeed)
        {
            var owner = P_PetOwner;

            if (owner == null || primary == null || primary is Player || !primary.IsAlive)
                return;

            var snapshot = huntersEchoWeapons;
            var weapon = snapshot?.Weapon;

            if (weapon == null || !owner.OwnsHuntersEchoItem(weapon))
                return;

            var launcher = snapshot.Launcher;
            var volleyWeapons = snapshot;

            if (weapon.IsAmmoLauncher)
            {
                var equippedAmmo = owner.GetEquippedAmmo();

                var ammoSlot = HuntersEchoMath.SelectVolleyAmmo(snapshot.AmmoSlot, IsUsableHuntersEchoAmmo(owner, snapshot.AmmoSlot, weapon),
                    equippedAmmo, IsUsableHuntersEchoAmmo(owner, equippedAmmo, weapon));

                if (ammoSlot == null)
                    return;

                volleyWeapons = snapshot.WithAmmo(ammoSlot);
            }

            var ammo = volleyWeapons.Ammo;

            if (ammo == null)
                return;

            // fast enough to reach the primary: the Echo shoots out to the full radar range, past the bow's own
            // ballistic reach (HuntersEchoMath.ArrowSpeed). Speed changes the flight only, never the damage.
            projectileSpeed = HuntersEchoArrowSpeed(primary, projectileSpeed);

            var volleyPinRoll = owner.WithHuntersEchoWeapons(volleyWeapons, owner.RollClassAbilityVolleyPin);

            FireMultiShotArrow(launcher, ammo, primary, projectileSpeed, 1.0f, primaryTarget: true, volleyPinRoll: volleyPinRoll);

            var shotMultipliers = WeaponMultiShotMultipliers(weapon);

            owner.WithHuntersEchoWeapons(volleyWeapons, () => { owner.AddClassAbilityMissileShots(shotMultipliers); return true; });

            bool? primaryInSightCache = null;
            bool PrimaryInSight() => primaryInSightCache ??= IsDirectVisible(primary);

            // captured so a Double Volley re-fire repeats the exact same extra-target spread (as Player.LaunchMissile does)
            var volleyExtraShots = shotMultipliers.Count > 0
                ? FireMultiShotVolley(launcher, ammo, weapon, primary, projectileSpeed, shotMultipliers, PrimaryInSight, volleyPinRoll)
                : null;

            // Double Volley (owner ruling 2026-11-11: "Echo should have double volley"): the owner's own roll, ONCE per
            // Echo volley, under the weapon scope, mirroring Player.LaunchMissile - an immediate re-fire of a cosmetic
            // arrow at the primary (only while it is in sight) plus the same extra-target spread, sharing this volley's
            // one Pinning Shot roll, adding no new Multishot arrows and never rolling again (a re-fire is not a volley)
            if (!owner.WithHuntersEchoWeapons(volleyWeapons, owner.TryClassAbilityDoubleVolley))
                return;

            if (PrimaryInSight())
                FireMultiShotArrow(launcher, ammo, primary, projectileSpeed, 1.0f, primaryTarget: true, volleyPinRoll: volleyPinRoll);

            if (volleyExtraShots == null)
                return;

            foreach (var (extraTarget, mult, isPrimary) in volleyExtraShots)
                FireMultiShotArrow(launcher, ammo, extraTarget, projectileSpeed, mult, primaryTarget: isPrimary, volleyPinRoll: volleyPinRoll);
        }

        /// <summary>Ammo an Echo volley may fire from <paramref name="launcher"/>: the owner's, and of the launcher's ammo type.</summary>
        private static bool IsUsableHuntersEchoAmmo(Player owner, WorldObject ammo, WorldObject launcher) =>
            ammo != null && ammo.AmmoType == launcher.AmmoType && owner.OwnsHuntersEchoItem(ammo);

        /// <summary>
        /// The launch speed for this volley's arrows at <paramref name="primary"/>: the bow's own speed, raised just
        /// enough to reach it when it stands past the bow's ballistic range (HuntersEchoMath.ArrowSpeed), measured the
        /// way FaceTargetDirectly measures direction (global positions when both sides are indoors or both outdoors).
        /// </summary>
        private float HuntersEchoArrowSpeed(Creature primary, float baseSpeed)
        {
            if (Location == null || primary.Location == null)
                return baseSpeed;

            var delta = Location.Indoors == primary.Location.Indoors
                ? primary.Location.ToGlobal() - Location.ToGlobal()
                : primary.Location.Pos - Location.Pos;

            var (horizontal, height) = HuntersEchoMath.HorizontalAndHeight(delta);

            return HuntersEchoMath.ArrowSpeed(baseSpeed, horizontal, height, Physics.PhysicsGlobals.Gravity, Physics.PhysicsGlobals.MaxVelocity);
        }

        /// <summary>
        /// The queue an arrow's impact runs on. The Echo's arrows land on its OWNER's queue (they resolve through the
        /// owner's DamageTarget anyway), so an Echo that ends while its arrows fly - common at full radar range, where
        /// the flight takes seconds - no longer discards them (its own queue drops every action once it has left the
        /// world). Every other pet keeps the Creature default, its own queue.
        /// </summary>
        protected override IActor MultiShotImpactActor => IsHuntersEcho && P_PetOwner != null ? P_PetOwner : this;

        /// <summary>
        /// The Echo's multishot extra arrows search out to its engage range (full radar), the same reach as its primary
        /// arrow. Every other pet keeps the Creature default, the bow's GetMaxMissileRange.
        /// </summary>
        protected override float GetMultiShotMaxRange() => IsHuntersEcho ? HuntersEchoEngageRange() : base.GetMultiShotMaxRange();

        /// <summary>
        /// A combat pet's multishot arrow arriving. The Echo resolves its arrows through its owner's DamageTarget; every
        /// other pet keeps the Creature default.
        /// </summary>
        protected override void ResolveMultiShotArrowImpact(Creature target, WorldObject cosmeticProjectile, float damageMultiplier, bool primaryTarget)
        {
            if (IsHuntersEcho)
            {
                ResolveHuntersEchoArrowImpact(target, cosmeticProjectile, damageMultiplier, primaryTarget);
                return;
            }

            base.ResolveMultiShotArrowImpact(target, cosmeticProjectile, damageMultiplier, primaryTarget);
        }

        /// <summary>
        /// One Echo arrow landing: the owner's own Player.DamageTarget against the struck monster, so the damage, the class
        /// abilities, kill credit and Pinning Shot (from the volley roll the projectile carries) are the owner's. Three
        /// things are the arrow's own, read off the projectile: the accuracy bar the hit resolves with and the hit line
        /// naming the Echo (its HuntersEchoShot), and the weapon set the hit resolves with (its launcher and ammo,
        /// HuntersEchoWeapons.FromProjectile), whatever the owner holds at impact (Player.DamageTargetAsHuntersEcho). Never a Player, never a pet, never a dead target, never a dead or departed owner. Runs from the OWNER's
        /// action queue (<see cref="MultiShotImpactActor"/>), so an arrow still in flight when the Echo ends still lands:
        /// the Echo itself need not exist any more, and nothing here reads or writes it beyond its P_PetOwner field. The
        /// owner and the target must share one landblock group, or the hit is dropped (the
        /// Creature.ResolveMultiShotArrowImpact guard, applied to the owner, whose DamageTarget writes both). A logged
        /// out owner has no landblock, and that guard (owner.CurrentLandblock == null) is what drops its arrows.
        /// </summary>
        private void ResolveHuntersEchoArrowImpact(Creature target, WorldObject cosmeticProjectile, float damageMultiplier, bool primaryTarget)
        {
            var owner = P_PetOwner;

            if (owner == null || owner.IsDead || target == null || target is Player || target is CombatPet || !target.IsAlive)
                return;

            if (owner.CurrentLandblock == null || target.CurrentLandblock == null
                || owner.CurrentLandblock.CurrentLandblockGroup != target.CurrentLandblock.CurrentLandblockGroup)
                return;

            // the bar the arrow was FIRED with (stamped by LaunchProjectile), never the owner's live one at impact;
            // an arrow somehow carrying no shot takes the full-bar default rather than a stale or exhausted bar
            var accuracyLevel = cosmeticProjectile?.HuntersEchoShot?.AccuracyLevel ?? HuntersEchoMath.DefaultShotAccuracyLevel;

            // the weapon the arrow was FIRED with (its launcher and ammo, stamped by LaunchProjectile from the snapshot),
            // never what the owner holds at impact: every weapon getter answers from it during the hit
            var weapons = HuntersEchoWeapons.FromProjectile(cosmeticProjectile);

            owner.DamageTargetAsHuntersEcho(target, cosmeticProjectile, damageMultiplier, primaryTarget, accuracyLevel, weapons);
        }

        /// <summary>
        /// The owner's chat line for a spawn or a refresh.
        /// </summary>
        internal static string FormatHuntersEchoMessage(bool refreshed, bool absorbed, int taunted)
        {
            var lead = refreshed ? "Your Echo is renewed" : "Your Echo steps out of you";

            var absorb = absorbed ? " and takes the blow meant for you" : "";

            var taunt = taunted > 0 ? $", drawing {taunted} monster{(taunted == 1 ? "" : "s")} onto itself" : "";

            return $"{lead}{absorb}{taunt}!";
        }

        /// <summary>Sends the owner's chat line (CombatSelf).</summary>
        internal static void NotifyHuntersEcho(Player owner, bool refreshed, bool absorbed, int taunted)
        {
            owner?.Session?.Network.EnqueueSend(new GameMessageSystemChat(FormatHuntersEchoMessage(refreshed, absorbed, taunted), ChatMessageType.CombatSelf));
        }
    }
}
