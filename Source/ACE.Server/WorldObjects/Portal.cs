using System.Linq;
using System.Numerics;

using log4net;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Realms;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    public partial class Portal : WorldObject
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Portal(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Portal(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        protected void SetEphemeralValues()
        {
            ObjectDescriptionFlags |= ObjectDescriptionFlag.Portal;

            ActivationResponse |= ActivationResponse.Use;

            UpdatePortalDestination(Destination);
        }

        public override bool EnterWorld()
        {
            var success = base.EnterWorld();

            if (!success)
            {
                log.Error($"{Name} ({Guid}) failed to spawn @ {Location?.ToLOCString()}");
                return false;
            }

            if (RelativeDestination != null && Location != null && Destination == null)
            {
                var relativeDestination = new Position(Location);
                relativeDestination.Pos += new Vector3(RelativeDestination.PositionX, RelativeDestination.PositionY, RelativeDestination.PositionZ);
                relativeDestination.Rotation = new Quaternion(RelativeDestination.RotationX, relativeDestination.RotationY, relativeDestination.RotationZ, relativeDestination.RotationW);
                relativeDestination.LandblockId = new LandblockId(relativeDestination.GetCell());

                UpdatePortalDestination(relativeDestination);
            }

            return true;
        }

        public void UpdatePortalDestination(Position destination)
        {
            Destination = destination;

            if (PortalShowDestination ?? true)
            {
                AppraisalPortalDestination = Name;

                if (Destination != null)
                {
                    var destCoords = Destination.GetMapCoordStr();
                    if (destCoords != null)
                        AppraisalPortalDestination += $" ({destCoords}).";
                }
            }
        }

        public override void SetLinkProperties(WorldObject wo)
        {
            if (wo.IsLinkSpot)
                SetPosition(PositionType.Destination, new Position(wo.Location));
        }

        public bool IsGateway { get => WeenieClassId == 1955; }

        //public override void OnActivate(WorldObject activator)
        //{
        //    if (activator is Creature creature)
        //        EmoteManager.OnUse(creature);

        //    base.OnActivate(activator);
        //}

        public virtual void OnCollideObject(Player player)
        {
            OnActivate(player);
        }

        public override void OnCastSpell(WorldObject activator)
        {
            if (SpellDID.HasValue)
                base.OnCastSpell(activator);
            else
                ActOnUse(activator);
        }

        /// <summary>
        /// If a player tries to use 2 portals in under this amount of time,
        /// they receive an error message
        /// </summary>
        private const float minTimeSinceLastPortal = 3.5f;

        public override ActivationResult CheckUseRequirements(WorldObject activator)
        {
            if (!(activator is Player player))
                return new ActivationResult(false);

            if (player.Teleporting)
                return new ActivationResult(false);

            if (Destination == null)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Portal destination for portal ID {WeenieClassId} not yet implemented!", ChatMessageType.System));
                return new ActivationResult(false);
            }

            if (player.LastPortalTeleportTimestamp != null)
            {
                var currentTime = Time.GetUnixTime();

                var timeSinceLastPortal = currentTime - player.LastPortalTeleportTimestamp.Value;

                if (timeSinceLastPortal < minTimeSinceLastPortal)
                {
                    // prevent message spam
                    if (player.LastPortalTeleportTimestampError != null)
                    {
                        var timeSinceLastPortalError = currentTime - player.LastPortalTeleportTimestampError.Value;

                        if (timeSinceLastPortalError < minTimeSinceLastPortal)
                            return new ActivationResult(false);
                    }

                    player.LastPortalTeleportTimestampError = currentTime;

                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.YouHaveBeenTeleportedTooRecently));
                }
            }

            if (player.PKTimerActive && !PortalIgnoresPkAttackTimer)
            {
                return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
            }

            if (!player.IgnorePortalRestrictions)
            {
                if (player.Level < MinLevel)
                {
                    // You are not powerful enough to interact with that portal!
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.YouAreNotPowerfulEnoughToUsePortal));
                }

                if (player.Level > MaxLevel && MaxLevel != 0 && PropertyManager.GetBool("use_portal_max_level_requirement").Item)
                {
                    // You are too powerful to interact with that portal!
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.YouAreTooPowerfulToUsePortal));
                }

                //var playerPkLevel = player.PkLevel;

                //if (PropertyManager.GetBool("pk_server").Item)
                //    playerPkLevel = PKLevel.PK;
                //else if (PropertyManager.GetBool("pkl_server").Item)
                //    playerPkLevel = PKLevel.PKLite;

                if (PortalRestrictions == PortalBitmask.Undef)
                {
                    // Players may not interact with that portal.
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.PlayersMayNotUsePortal));
                }

                if (PortalRestrictions.HasFlag(PortalBitmask.NoPk) && player.PlayerKillerStatus == PlayerKillerStatus.PK)
                {
                    // Player killers may not interact with that portal!
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.PKsMayNotUsePortal));
                }

                if (PortalRestrictions.HasFlag(PortalBitmask.NoPKLite) && player.PlayerKillerStatus == PlayerKillerStatus.PKLite)
                {
                    // Lite Player Killers may not interact with that portal!
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.PKLiteMayNotUsePortal));
                }

                if (PortalRestrictions.HasFlag(PortalBitmask.NoNPK) && player.PlayerKillerStatus == PlayerKillerStatus.NPK)
                {
                    // Non-player killers may not interact with that portal!
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.NonPKsMayNotUsePortal));
                }

                if (PortalRestrictions.HasFlag(PortalBitmask.OnlyOlthoiPCs) && !player.IsOlthoiPlayer)
                {
                    // Only Olthoi may pass through this portal!
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.OnlyOlthoiMayUsePortal));
                }

                if ((PortalRestrictions.HasFlag(PortalBitmask.NoOlthoiPCs) || IsGateway) && player.IsOlthoiPlayer)
                {
                    // Olthoi may not pass through this portal!
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.OlthoiMayNotUsePortal));
                }

                if (PortalRestrictions.HasFlag(PortalBitmask.NoVitae) && player.HasVitae)
                {
                    // You may not pass through this portal while Vitae weakens you!
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.YouMayNotUsePortalWithVitae));
                }

                if (PortalRestrictions.HasFlag(PortalBitmask.NoNewAccounts) && !player.Account15Days)
                {
                    // This character must be two weeks old or have been created on an account at least two weeks old to use this portal!
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.YouMustBeTwoWeeksOldToUsePortal));
                }

                if (player.AccountRequirements < AccountRequirements)
                {
                    // You must purchase Asheron's Call -- Throne of Destiny to use this portal.
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.MustPurchaseThroneOfDestinyToUsePortal));
                }

                if ((AdvocateQuest ?? false) && !player.IsAdvocate)
                {
                    // You must be an Advocate to interact with that portal.
                    return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.YouMustBeAnAdvocateToUsePortal));
                }
            }

            if (QuestRestriction != null && !player.IgnorePortalRestrictions)
            {
                var hasQuest = player.QuestManager.HasQuest(QuestRestriction);
                var canSolve = player.QuestManager.CanSolve(QuestRestriction);

                var success = hasQuest && !canSolve;

                if (!success)
                {
                    player.QuestManager.HandlePortalQuestError(QuestRestriction);
                    return new ActivationResult(false);
                }
            }

            // Proving Grounds portal (PortalBlocksRareGems): refuse entry while carrying any RARE GEM. Rare
            // armor/weapons/jewelry also carry a RareId, so the Gem weenie-type check is the load-bearing filter -
            // only rare *gems* are blocked. Nested containers are covered by GetAllPossessions.
            if (GetProperty(PropertyBool.PortalBlocksRareGems) == true && !player.IgnorePortalRestrictions)
            {
                var rareGem = player.GetAllPossessions()
                    .FirstOrDefault(i => i.GetProperty(PropertyInt.RareId) != null && i.WeenieType == WeenieType.Gem);

                if (rareGem != null)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"You may not carry {rareGem.Name} into the Proving Grounds. Bank your rare gems first.",
                        ChatMessageType.System));
                    return new ActivationResult(false);
                }
            }

            // handle quest initial flagging
            if (Quest != null)
            {
                EmoteManager.OnQuest(player);
            }

            return new ActivationResult(true);
        }

        /// <summary>
        /// Applies a PortalRealm override to an already instance-resolved destination: the position is rebound
        /// to the named realm's default instance, where that realm's content overrides apply. An unknown realm
        /// id is logged and ignored, leaving the destination as-is.
        /// </summary>
        private static Position ApplyPortalRealm(WorldObject source, Position dest)
        {
            var portalRealm = source.GetProperty(PropertyInt.PortalRealm);

            if (portalRealm == null)
                return dest;

            var realm = RealmManager.GetRealm((ushort)portalRealm.Value);

            if (realm == null)
            {
                log.Warn($"Portal {source.WeenieClassId} has PortalRealm {portalRealm.Value}, which is not in the realm registry - using normal destination");
                return dest;
            }

            return new Position(dest, realm.DefaultInstanceID);
        }

        /// <summary>
        /// The standard portal destination resolution: route the destination to the default instance of the
        /// player's home realm, then let an explicit PortalRealm on the source object override which realm
        /// that is. Shared by <see cref="ActOnUse"/> and Gem.UsePortalGem so portal gems land exactly where an
        /// equivalent portal would - the two must not drift apart. Callers that need the house-portal
        /// "stay in the player's current instance" behavior resolve the instance themselves and then apply
        /// <see cref="ApplyPortalRealm"/>.
        /// </summary>
        internal static Position ResolvePortalDestination(WorldObject source, Player player, Position dest)
        {
            return ApplyPortalRealm(source, dest.AsInstancedPosition(player, PlayerInstanceSelectMode.HomeRealmDefault));
        }

        public override void ActOnUse(WorldObject activator)
        {
            var player = activator as Player;
            if (player == null) return;

#if DEBUG
            // player.Session.Network.EnqueueSend(new GameMessageSystemChat("Portal sending player to destination", ChatMessageType.System));
#endif
            var portalDest = new Position(Destination);
            AdjustDungeon(portalDest);

            // resolve which instance the destination lands in: house portals stay in
            // the player's current instance; everything else routes to the default
            // instance of the player's home realm. Either way an explicit PortalRealm
            // wins (see ResolvePortalDestination / ApplyPortalRealm)
            if (this is HousePortal)
                portalDest = ApplyPortalRealm(this, portalDest.AsInstancedPosition(player, PlayerInstanceSelectMode.Same));
            else
                portalDest = ResolvePortalDestination(this, player, portalDest);

            // DPS-challenge portal: DpsChallengeDuration > 0 drops the player into a strictly single-player
            // arena and arms a timed damage trial that starts when they land (see Player_DpsChallenge.cs).
            var dpsChallengeDuration = GetProperty(PropertyInt.DpsChallengeDuration) ?? 0;
            var isDpsChallenge = dpsChallengeDuration > 0;
            var armDpsChallenge = false;

            // Survival-challenge portal (WaffleACE): SurvivalChallengeInterval > 0 drops the player into a strictly
            // single-player arena and arms a survival run that escalates every interval until they die (see
            // Player_SurvivalChallenge.cs).
            var survivalChallengeInterval = GetProperty(PropertyInt.SurvivalChallengeInterval) ?? 0;
            var isSurvivalChallenge = survivalChallengeInterval > 0;
            var armSurvivalChallenge = false;

            // Wave-challenge portal (WaffleACE): WaveChallengeWaves > 0 drops the player into a strictly
            // single-player arena and arms a wave-by-wave gauntlet that starts when they land (see
            // Player_WaveChallenge.cs).
            var waveChallengeWaves = GetProperty(PropertyInt.WaveChallengeWaves) ?? 0;
            var isWaveChallenge = waveChallengeWaves > 0;
            var armWaveChallenge = false;

            // instanced portal: each use creates a fresh ephemeral copy of the destination dungeon
            // (composed with PortalRealm: an ephemeral copy of the realm's content)
            if ((GetProperty(PropertyInt.PortalInstancing) ?? 0) == 1)
            {
                var destLandblockId = new LandblockId(portalDest.Cell | 0xFFFF);
                Position.ParseInstanceID(portalDest.Instance, out _, out var destRealmId, out _);
                // a DPS-, survival- or wave-challenge instance is never open to the owner's fellowship - the run is scored per player
                var ephemeralLandblock = RealmManager.GetNewEphemeralLandblock(destLandblockId, player, destRealmId, openToFellowship: !isDpsChallenge && !isSurvivalChallenge && !isWaveChallenge);

                if (ephemeralLandblock != null && ephemeralLandblock.IsDungeon)
                {
                    player.SetPosition(PositionType.EphemeralRealmExitTo, new Position(player.Location));
                    portalDest = new Position(portalDest, ephemeralLandblock.Instance);
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Entering a private instance (0x{ephemeralLandblock.Instance:X8})...", ChatMessageType.System));

                    if (isDpsChallenge)
                    {
                        // persist the armed flag before teleport so a mid-run logout is caught at next login
                        player.DpsChallengeActive = true;
                        player.RushNextPlayerSave(5);
                        armDpsChallenge = true;
                    }

                    if (isSurvivalChallenge)
                    {
                        // persist the armed flag before teleport so a mid-run logout is caught at next login
                        player.SurvivalChallengeActive = true;
                        player.RushNextPlayerSave(5);
                        armSurvivalChallenge = true;
                    }

                    if (isWaveChallenge)
                    {
                        // persist the armed flag before teleport so a mid-run logout is caught at next login.
                        // This MUST happen here, ahead of ThreadSafeTeleport, and StartWaveChallenge must run
                        // inside the teleport-completion delegate below: the completion delegate runs before
                        // OnTeleportComplete's exit reconciliation, so by the time that reconciliation reads the
                        // flag the run is already bound to the arena instance. Arming any later would make the
                        // reconciliation see an active-but-unbound run and forfeit it on arrival.
                        player.WaveChallengeActive = true;
                        player.RushNextPlayerSave(5);
                        armWaveChallenge = true;
                    }
                }
                else if (ephemeralLandblock != null)
                {
                    log.Warn($"Portal {WeenieClassId} has PortalInstancing but destination 0x{destLandblockId.Landblock:X4} is not a dungeon - using normal destination");
                    LandblockManager.AddToDestructionQueue(ephemeralLandblock);
                }
            }

            // exit-instance portal: used inside an ephemeral instance, its real destination is
            // wherever the player came from (EphemeralRealmExitTo), not its static Destination -
            // so one weenie exits correctly for every player. Destination is only the fallback,
            // for the case where nothing stamped an exit. This is what lets the DreamWeave
            // Loomstone drop each character into their own heritage's training hall.
            if ((GetProperty(PropertyInt.PortalExitInstance) ?? 0) == 1 && player.Location.IsEphemeralRealm)
            {
                // leaving via the in-arena exit portal is a SCORED finish of an in-progress survival run (records
                // seconds survived + announcements, no death penalties), marked finished before the teleport below
                // so OnTeleportComplete's instance-exit check sees an already-finished run and does not re-fire
                player.FinishSurvivalChallengeAtExit();

                // leaving via the in-arena exit portal ends an in-progress wave run, keeping the waves already
                // cleared as the score. Marked finished before the teleport below for the same reason as above.
                player.FinishWaveChallengeAtExit();

                var exitTo = player.GetPosition(PositionType.EphemeralRealmExitTo);

                if (exitTo != null)
                {
                    player.SetPosition(PositionType.EphemeralRealmExitTo, null);
                    portalDest = new Position(exitTo);
                }
                else
                    log.Warn($"Portal {WeenieClassId} has PortalExitInstance but the player has no EphemeralRealmExitTo - using its fallback destination");
            }

            WorldManager.ThreadSafeTeleport(player, portalDest, new ActionEventDelegate(() =>
            {
                // If the portal just used is able to be recalled to,
                // save the destination coordinates to the LastPortal character position save table
                if (!NoRecall)
                    player.LastPortalDID = OriginalPortal == null ? WeenieClassId : OriginalPortal; // if walking through a summoned portal

                EmoteManager.OnPortal(player);

                player.SendWeenieError(WeenieError.ITeleported);

                // the welcome popup was held back while this character was in the Loom, because it
                // sends them to the Society Greeter and the Greeter is here, not there. They have
                // just arrived, so now it means something.
                if (player.DeferredWelcomePopup)
                {
                    player.DeferredWelcomePopup = false;
                    WorldManager.SendWelcomePopup(player.Session);
                }

                // the player has just arrived in the arena instance - start the timed run so the
                // countdown begins on arrival
                if (armDpsChallenge)
                    player.StartDpsChallenge(dpsChallengeDuration);

                // survival arena: start the escalating run on arrival. rampRate scales the creatures' raw power
                // (attributes/skills) per tier; ratingPerTier is the flat per-tier DamageRating increment that
                // carries the uniform melee + spell damage growth.
                if (armSurvivalChallenge)
                    player.StartSurvivalChallenge(survivalChallengeInterval,
                        GetProperty(PropertyFloat.SurvivalChallengeRampRate) ?? 1.3,
                        (int)System.Math.Round(GetProperty(PropertyFloat.SurvivalChallengeRatingPerTier) ?? 15.0));

                // wave gauntlet: start the run on arrival. The roster weenie for wave N is
                // WaveChallengeRosterBaseWcid + N - 1; interWaveDelay is the breather between waves, stallTimeout
                // is how long zero damage against the live wave ends the run (0 disables), and waveTimeLimit is the
                // absolute deadline one wave may live for (0 disables).
                if (armWaveChallenge)
                    player.StartWaveChallenge(waveChallengeWaves,
                        (uint)(GetProperty(PropertyInt.WaveChallengeRosterBaseWcid) ?? 0),
                        GetProperty(PropertyFloat.WaveChallengeInterWaveDelay) ?? 10.0,
                        GetProperty(PropertyFloat.WaveChallengeStallTimeout) ?? 150.0,
                        GetProperty(PropertyFloat.WaveChallengeWaveTimeLimit) ?? 300.0);

                // a Proving Grounds portal strips rare-gem buffs on arrival. Only the Prodigal spells go: the
                // Incantations and Auras a player could have cast on themselves survive, because the strip set is
                // narrowed to spells with a rare SpellCategory. See StripRareGemBuffs.
                if (GetProperty(PropertyBool.PortalBlocksRareGems) == true)
                    player.StripRareGemBuffs();

            }), true);
        }
    }
}
