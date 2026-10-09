using System;
using System.Collections.Generic;

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
using ACE.Server.WorldObjects.Entity;

using Position = ACE.Entity.Position;

namespace ACE.Server.WorldObjects
{
    public partial class Creature : Container
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public bool IsExhausted { get => Stamina.Current == 0; }

        /// <summary>
        /// Wave challenge (WaffleACE): the player whose wave-gauntlet run spawned this creature. Set by
        /// Player_WaveChallenge.SpawnWave on every roster creature that makes it into the world, and read by
        /// Creature.Die() to report the death back to the run. Purely in-memory and never persisted - wave
        /// creatures live and die inside one ephemeral instance (mirrors Pet.P_PetOwner).
        /// </summary>
        public Player P_WaveOwner;

        /// <summary>
        /// World events (WaffleACE): the run that spawned this creature. Set by WorldEventSpawner on every
        /// creature that makes it into the world, and read by Creature.Die() to report the death back to the
        /// run. Purely in-memory and NEVER persisted - it is nulled by the spawner's DestroyAll, and a
        /// creature that somehow outlives its run is filtered out by the reference check in
        /// WorldEventManager.OnEventCreatureDied (mirrors P_WaveOwner above).
        /// </summary>
        public ACE.Server.WorldEvents.WorldEvent P_WorldEvent;

        /// <summary>
        /// Threads (WaffleACE): the run that spawned this creature. Set by ThreadDungeonSpawner before
        /// EnterWorld, read by Creature.Die() to report the kill. Purely in-memory and NEVER persisted; the
        /// persisted twin is PropertyInt.ThreadDungeonRunId, and the death hook requires both (mirrors P_WorldEvent).
        /// </summary>
        public ACE.Server.ThreadDungeons.ThreadDungeonRun P_DungeonRun;

        /// <summary>
        /// ML digsite encounters (WaffleACE): the encounter that spawned this creature. Set by
        /// MlDigsiteSpawner before EnterWorld, read by Creature.Die() to report the kill. Purely in-memory
        /// and NEVER persisted; the persisted twin is PropertyInt.MlDigsiteEncounterId, and both the death
        /// hook and the corpse-suppression predicate require BOTH (mirrors P_WorldEvent and P_DungeonRun).
        /// Nulled by MlDigsiteManager's cleanup, so a creature that outlives its encounter is inert.
        /// </summary>
        public ACE.Server.MlDigsite.MlDigsiteEncounter P_DigsiteEncounter;

        /// <summary>
        /// Wave encounters (WaffleACE): the object-anchored wave encounter that spawned this creature. Set by
        /// WaveEncounterSpawner before EnterWorld, read by Creature.Die() to report the kill. Purely in-memory
        /// and NEVER persisted; the persisted twin is PropertyInt.WaveEncounterId, and the death hook requires
        /// BOTH (mirrors P_DigsiteEncounter). Nulled when the encounter ends, so a straggler is inert.
        /// </summary>
        public ACE.Server.WaveEncounters.WaveEncounter P_WaveEncounter;

        /// <summary>
        /// ML digsite encounters (WaffleACE): the on/off switch behind the "immune" monster effect, owned by
        /// the Boss Rush mechanic driver (MlDigsite/Mechanics/ImmunePhasesMechanic). Purely in-memory, NEVER
        /// persisted and never networked, exactly like <see cref="P_DigsiteEncounter"/>; cleared by the same
        /// cleanup, so a boss that outlives its encounter cannot be left immortal.
        ///
        /// The effect record is what actually filters the damage (MonsterEffects/Effects/ImmuneEffect), and
        /// this field is what it reads. PropertyBool.Invincible is deliberately NOT used: both spell-side
        /// checks gate on the target being a PLAYER (SpellProjectile.cs) while the melee check does not
        /// (DamageEvent.cs), so an Invincible boss would still be killed by war magic.
        /// </summary>
        public bool P_DigsiteImmune;

        /// <summary>Threads: the role this creature was placed as, for the kill ledger and XP rate.</summary>
        public ACE.Server.ThreadDungeons.DungeonRole? DungeonRole;

        /// <summary>
        /// Threads: the run's salvage affinities, as (material, base wcid, per-kill probability).
        /// Stamped by ThreadDungeonSpawner before EnterWorld and read once by Creature_Death.GenerateTreasure.
        /// Purely in-memory, never persisted and never networked, exactly like P_DungeonRun and DungeonRole.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> P_DungeonSalvageAffinities;

        /// <summary>
        /// Threads: an in-memory TreasureDeath profile that replaces the weenie's DeathTreasureType for
        /// this creature only (PLAN 7.2). Never persisted; built by DungeonRewardMath.BuildProfile.
        /// </summary>
        public ACE.Database.Models.World.TreasureDeath DeathTreasureOverride;

        /// <summary>
        /// World events sky-drop (WaffleACE, WP-17): true while this creature is falling in from a world-event
        /// wave spawned above the terrain (a theme with spawnDz greater than 0 - today only sky_rift). Set by
        /// WorldEventSpawner immediately after the creature is adopted, and cleared by OnSkyDropLanded (see
        /// Creature_SkyDrop.cs) the moment the physics tick sees it standing on walkable ground or the
        /// deadline below expires. Purely in-memory and NEVER persisted, exactly like P_WorldEvent above.
        ///
        /// While it is set, two rules change: WorldObject_Tick.UpdateObjectPhysics always runs the physics
        /// update for this creature (so it actually falls), and Monster_Tick returns immediately (so it makes
        /// no attack, move or target search until it lands).
        /// </summary>
        public bool WorldEventSkyDrop;

        /// <summary>
        /// The absolute ACE.Server.Physics.Common.PhysicsTimer.CurrentTime after which a sky drop is
        /// force-settled onto the terrain, even if the creature never reported standing on walkable ground.
        ///
        /// That clock and only that clock. It is NOT Timers.RunningTime: PhysicsTimer.CurrentTime is
        /// Timers.PortalYearTicks (Source/ACE.Server/Physics/Common/PhysicsTimer.cs:26), a different clock
        /// with a different origin, so a deadline armed on RunningTime and compared here would fire at an
        /// arbitrary offset. WorldEventSpawner.BeginSkyDrop arms it on PhysicsTimer.CurrentTime and
        /// WorldObject_Tick.UpdateObjectPhysics compares it against PhysicsTimer.CurrentTime; those are the
        /// only two places it is touched.
        ///
        /// Set with WorldEventSkyDrop, and meaningless while that flag is false. Runtime-only, never
        /// persisted (WP-17).
        /// </summary>
        public double WorldEventSkyDropDeadline;

        /// <summary>
        /// World Events (WaffleACE, WP-23): true when this creature's weenie carries PropertyBool 9026
        /// WorldEventObjective - a Rift or Element Portal pillar. These are Attackable HP sinks: an attack
        /// wakes them (IsMonster is true), but they must never think - no target search, no movement, no
        /// attack. Cached here at construction (SetEphemeralValues) rather than read from the property
        /// dictionary on every tick; Monster_Tick checks it once per tick to return immediately.
        /// </summary>
        public bool WorldEventObjective;

        protected QuestManager _questManager;

        public QuestManager QuestManager
        {
            get
            {
                if (_questManager == null)
                {
                    /*if (!(this is Player))
                        log.DebugFormat("Initializing non-player QuestManager for {0} (0x{1})", Name, Guid);*/

                    _questManager = new QuestManager(this);
                }

                return _questManager;
            }
        }

        /// <summary>
        /// A table of players who currently have their targeting reticule on this creature
        /// </summary>
        private Dictionary<uint, WorldObjectInfo> selectedTargets;

        /// <summary>
        /// Currently used to handle some edge cases for faction mobs
        /// DamageHistory.HasDamager() has the following issues:
        /// - if a player attacks a same-factioned mob but is evaded, the mob would quickly de-aggro
        /// - if a player attacks a same-factioned mob in a group of same-factioned mobs, the other nearby faction mobs should be alerted, and should maintain aggro, even without a DamageHistory entry
        /// - if a summoner attacks a same-factioned mob, should the summoned CombatPet possibly defend the player in that situation?
        /// </summary>
        //public HashSet<uint> RetaliateTargets { get; set; }

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Creature(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            InitializePropertyDictionaries();
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Creature(Biota biota) : base(biota)
        {
            InitializePropertyDictionaries();
            SetEphemeralValues();
        }

        private void InitializePropertyDictionaries()
        {
            if (Biota.PropertiesAttribute == null)
                Biota.PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>();
            if (Biota.PropertiesAttribute2nd == null)
                Biota.PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>();
            if (Biota.PropertiesBodyPart == null)
                Biota.PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>();
            if (Biota.PropertiesSkill == null)
                Biota.PropertiesSkill = new Dictionary<Skill, PropertiesSkill>();
        }

        private void SetEphemeralValues()
        {
            CombatMode = CombatMode.NonCombat;
            DamageHistory = new DamageHistory(this);

            WorldEventObjective = GetProperty(PropertyBool.WorldEventObjective) == true;

            BuildMonsterEffects();

            if (!(this is Player))
                GenerateNewFace();

            // If any of the vitals don't exist for this biota, one will be created automatically in the CreatureVital ctor
            Vitals[PropertyAttribute2nd.MaxHealth] = new CreatureVital(this, PropertyAttribute2nd.MaxHealth);
            Vitals[PropertyAttribute2nd.MaxStamina] = new CreatureVital(this, PropertyAttribute2nd.MaxStamina);
            Vitals[PropertyAttribute2nd.MaxMana] = new CreatureVital(this, PropertyAttribute2nd.MaxMana);

            // If any of the attributes don't exist for this biota, one will be created automatically in the CreatureAttribute ctor
            Attributes[PropertyAttribute.Strength] = new CreatureAttribute(this, PropertyAttribute.Strength);
            Attributes[PropertyAttribute.Endurance] = new CreatureAttribute(this, PropertyAttribute.Endurance);
            Attributes[PropertyAttribute.Coordination] = new CreatureAttribute(this, PropertyAttribute.Coordination);
            Attributes[PropertyAttribute.Quickness] = new CreatureAttribute(this, PropertyAttribute.Quickness);
            Attributes[PropertyAttribute.Focus] = new CreatureAttribute(this, PropertyAttribute.Focus);
            Attributes[PropertyAttribute.Self] = new CreatureAttribute(this, PropertyAttribute.Self);

            foreach (var kvp in Biota.PropertiesSkill)
                Skills[kvp.Key] = new CreatureSkill(this, kvp.Key, kvp.Value);

            if (Health.Current <= 0)
                Health.Current = Health.MaxValue;
            if (Stamina.Current <= 0)
                Stamina.Current = Stamina.MaxValue;
            if (Mana.Current <= 0)
                Mana.Current = Mana.MaxValue;

            if (!(this is Player))
            {
                GenerateWieldList();

                EquipInventoryItems();

                GenerateWieldedTreasure();

                EquipInventoryItems();

                GenerateInventoryTreasure();

                // TODO: fix tod data
                Health.Current = Health.MaxValue;
                Stamina.Current = Stamina.MaxValue;
                Mana.Current = Mana.MaxValue;
            }

            SetMonsterState();

            CurrentMotionState = new Motion(MotionStance.NonCombat, MotionCommand.Ready);

            selectedTargets = new Dictionary<uint, WorldObjectInfo>();
        }

        // verify logic
        public bool IsNPC => !(this is Player) && !Attackable && TargetingTactic == TargetingTactic.None;

        public void GenerateNewFace()
        {
            if (!Heritage.HasValue)
            {
                if (!string.IsNullOrEmpty(HeritageGroupName) && Enum.TryParse(HeritageGroupName.Replace("'", ""), true, out HeritageGroup heritage))
                    Heritage = (int)heritage;
            }

            if (!Gender.HasValue)
            {
                if (!string.IsNullOrEmpty(Sex) && Enum.TryParse(Sex, true, out Gender gender))
                    Gender = (int)gender;
            }

            if (!Heritage.HasValue || !Gender.HasValue)
            {
#if DEBUG
                //if (!(NpcLooksLikeObject ?? false))
                    //log.DebugFormat("Creature.GenerateNewFace: {0} (0x{1}) - wcid {2} - Heritage: {3} | HeritageGroupName: {4} | Gender: {5} | Sex: {6} - Data missing or unparsable, Cannot randomize face.", Name, Guid, WeenieClassId, Heritage, HeritageGroupName, Gender, Sex);
#endif
                return;
            }

            // deferred until after the early-out above, so faceless creatures (and unit tests)
            // can be constructed without the portal dat loaded
            var cg = DatManager.PortalDat.CharGen;

            if (!cg.HeritageGroups.TryGetValue((uint)Heritage, out var heritageGroup) || !heritageGroup.Genders.TryGetValue((int)Gender, out var sex))
            {
#if DEBUG
                log.DebugFormat("Creature.GenerateNewFace: {0} (0x{1}) - wcid {2} - Heritage: {3} | HeritageGroupName: {4} | Gender: {5} | Sex: {6} - Data invalid, Cannot randomize face.", Name, Guid, WeenieClassId, Heritage, HeritageGroupName, Gender, Sex);
#endif
                return;
            }

            PaletteBaseId = sex.BasePalette;

            var appearance = new Appearance
            {
                HairStyle = 1,
                HairColor = 1,
                HairHue = 1,

                EyeColor = 1,
                Eyes = 1,

                Mouth = 1,
                Nose = 1,

                SkinHue = 1
            };

            // Get the hair first, because we need to know if you're bald, and that's the name of that tune!
            if (sex.HairStyleList.Count > 1)
            {
                if (PropertyManager.GetBool("npc_hairstyle_fullrange").Item)
                    appearance.HairStyle = (uint)ThreadSafeRandom.Next(0, sex.HairStyleList.Count - 1);
                else
                    appearance.HairStyle = (uint)ThreadSafeRandom.Next(0, Math.Min(sex.HairStyleList.Count - 1, 8)); // retail range data compiled by OptimShi
            }
            else
                appearance.HairStyle = 0;

            if (sex.HairStyleList.Count < appearance.HairStyle)
            {
                log.Warn($"Creature.GenerateNewFace: {Name} (0x{Guid}) - wcid {WeenieClassId} - HairStyle = {appearance.HairStyle} | HairStyleList.Count = {sex.HairStyleList.Count} - Data invalid, Cannot randomize face.");
                return;
            }

            var hairstyle = sex.HairStyleList[Convert.ToInt32(appearance.HairStyle)];

            appearance.HairColor = (uint)ThreadSafeRandom.Next(0, sex.HairColorList.Count - 1);
            appearance.HairHue = ThreadSafeRandom.Next(0.0f, 1.0f);

            appearance.EyeColor = (uint)ThreadSafeRandom.Next(0, sex.EyeColorList.Count - 1);
            appearance.Eyes = (uint)ThreadSafeRandom.Next(0, sex.EyeStripList.Count - 1);

            appearance.Mouth = (uint)ThreadSafeRandom.Next(0, sex.MouthStripList.Count - 1);

            appearance.Nose = (uint)ThreadSafeRandom.Next(0, sex.NoseStripList.Count - 1);

            appearance.SkinHue = ThreadSafeRandom.Next(0.0f, 1.0f);

            //// Certain races (Undead, Tumeroks, Others?) have multiple body styles available. This is controlled via the "hair style".
            ////if (hairstyle.AlternateSetup > 0)
            ////    character.SetupTableId = hairstyle.AlternateSetup;

            if (!EyesTextureDID.HasValue)
                EyesTextureDID = sex.GetEyeTexture(appearance.Eyes, hairstyle.Bald);
            if (!DefaultEyesTextureDID.HasValue)
                DefaultEyesTextureDID = sex.GetDefaultEyeTexture(appearance.Eyes, hairstyle.Bald);
            if (!NoseTextureDID.HasValue)
                NoseTextureDID = sex.GetNoseTexture(appearance.Nose);
            if (!DefaultNoseTextureDID.HasValue)
                DefaultNoseTextureDID = sex.GetDefaultNoseTexture(appearance.Nose);
            if (!MouthTextureDID.HasValue)
                MouthTextureDID = sex.GetMouthTexture(appearance.Mouth);
            if (!DefaultMouthTextureDID.HasValue)
                DefaultMouthTextureDID = sex.GetDefaultMouthTexture(appearance.Mouth);
            if (!HeadObjectDID.HasValue)
                HeadObjectDID = sex.GetHeadObject(appearance.HairStyle);

            // Skin is stored as PaletteSet (list of Palettes), so we need to read in the set to get the specific palette
            var skinPalSet = DatManager.PortalDat.ReadFromDat<PaletteSet>(sex.SkinPalSet);
            if (!SkinPaletteDID.HasValue)
                SkinPaletteDID = skinPalSet.GetPaletteID(appearance.SkinHue);

            // Hair is stored as PaletteSet (list of Palettes), so we need to read in the set to get the specific palette
            var hairPalSet = DatManager.PortalDat.ReadFromDat<PaletteSet>(sex.HairColorList[Convert.ToInt32(appearance.HairColor)]);
            if (!HairPaletteDID.HasValue)
                HairPaletteDID = hairPalSet.GetPaletteID(appearance.HairHue);

            // Eye Color
            if (!EyesPaletteDID.HasValue)
                EyesPaletteDID = sex.EyeColorList[Convert.ToInt32(appearance.EyeColor)];
        }

        public virtual float GetBurdenMod()
        {
            return 1.0f;    // override for players
        }

        /// <summary>
        /// This will be false when creature is dead and waits for respawn
        /// </summary>
        public bool IsAlive { get => Health.Current > 0; }

        /// <summary>
        /// Sends the network commands to move a player towards an object
        /// </summary>
        public void MoveToObject(WorldObject target, float? useRadius = null)
        {
            var distanceToObject = useRadius ?? target.UseRadius ?? 0.6f;

            var moveToObject = new Motion(this, target, MovementType.MoveToObject);
            moveToObject.MoveToParameters.DistanceToObject = distanceToObject;

            // move directly to portal origin
            //if (target is Portal)
                //moveToObject.MoveToParameters.MovementParameters &= ~MovementParams.UseSpheres;

            SetWalkRunThreshold(moveToObject, target.Location);

            EnqueueBroadcastMotion(moveToObject);
        }

        /// <summary>
        /// Sends the network commands to move a player towards a position
        /// </summary>
        public void MoveToPosition(Position position)
        {
            var moveToPosition = new Motion(this, position);
            moveToPosition.MoveToParameters.DistanceToObject = 0.0f;

            SetWalkRunThreshold(moveToPosition, position);

            EnqueueBroadcastMotion(moveToPosition);
        }

        public void SetWalkRunThreshold(Motion motion, Position targetLocation)
        {
            // FIXME: WalkRunThreshold (default 15 distance) seems to not be used automatically by client
            // player will always walk instead of run, and if MovementParams.CanCharge is sent, they will always charge
            // to remedy this, we manually calculate a threshold based on WalkRunThreshold

            var dist = Location.DistanceTo(targetLocation);
            if (dist >= motion.MoveToParameters.WalkRunThreshold / 2.0f)     // default 15 distance seems too far, especially with weird in-combat walking animation?
            {
                motion.MoveToParameters.MovementParameters |= MovementParams.CanCharge;

                // TODO: find the correct runrate here
                // the default runrate / charge seems much too fast...
                //motion.RunRate = GetRunRate() / 4.0f;
                motion.RunRate = GetRunRate();
            }
        }

        /// <summary>
        /// This is raised by Player.HandleActionUseItem.<para />
        /// The item does not exist in the players possession.<para />
        /// If the item was outside of range, the player will have been commanded to move using DoMoveTo before ActOnUse is called.<para />
        /// When this is called, it should be assumed that the player is within range.
        /// 
        /// This is the OnUse method.   This is just an initial implemention.   I have put in the turn to action at this point.
        /// If we are out of use radius, move to the object.   Once in range, let's turn the creature toward us and get started.
        /// Note - we may need to make an NPC class vs monster as using a monster does not make them turn towrad you as I recall. Og II
        ///  Also, once we are reading in the emotes table by weenie - this will automatically customize the behavior for creatures.
        /// </summary>
        public override void ActOnUse(WorldObject worldObject)
        {
            // Drift Network class ability trainer / exchanger NPCs run their CAP purchase / refund interaction
            // here (flagged by weenie data - see ClassAbilities.ClassAbilityTrainer). Their greeting emote has
            // already fired in base.OnActivate -> EmoteManager.OnUse(). Every other NPC is emote-only.
            if (worldObject is Player player && ClassAbilities.ClassAbilityTrainer.TryHandleUse(this, player))
                return;

            // The Threads Survey-Archivist (flagged by PropertyBool.DungeonSurveyArchivist) pays its
            // daily-survey reward here. Unlike every other NPC it has NO Use emote set at all: the award has
            // to be computed from the player's own survey history, and base.OnActivate runs
            // EmoteManager.OnUse BEFORE this method, so an emote rig alongside this code would double-pay.
            if (worldObject is Player surveyor && ThreadDungeons.SurveyArchivistStation.TryHandleUse(this, surveyor))
                return;

            // The Thread-Guide (flagged by PropertyBool.ThreadGuideNpc) issues guide fragments here, for the same
            // reason as the Archivist above: no Use emote set, because emotes run before this method.
            if (worldObject is Player guided && ThreadDungeons.ThreadGuideStation.TryHandleUse(this, guided))
                return;

            // handled in base.OnActivate -> EmoteManager.OnUse()
        }

        public override void OnCollideObject(WorldObject target)
        {
            if (target.ReportCollisions == false)
                return;

            if (target is Door door)
                door.OnCollideObject(this);
            else if (target is Hotspot hotspot)
                hotspot.OnCollideObject(this);
        }

        /// <summary>
        /// Called when a player selects a target
        /// </summary>
        public bool OnTargetSelected(Player player)
        {
            return selectedTargets.TryAdd(player.Guid.Full, new WorldObjectInfo(player));
        }

        /// <summary>
        /// Called when a player deselects a target
        /// </summary>
        public bool OnTargetDeselected(Player player)
        {
            return selectedTargets.Remove(player.Guid.Full);
        }

        /// <summary>
        /// Called when a creature's health changes
        /// </summary>
        public void OnHealthUpdate()
        {
            foreach (var kvp in selectedTargets)
            {
                var player = kvp.Value.TryGetWorldObject() as Player;

                if (player?.Session != null)
                    QueryHealth(player.Session);
                else
                    selectedTargets.Remove(kvp.Key);
            }
        }
    }
}
