using System.Linq;
using System.Numerics;

using log4net;

using ACE.Common;
using ACE.Database;
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
            // A player who ARRIVED (by any teleport, or at login) already overlapping this portal is not
            // walking into it - see Player.CaptureArrivalPortalOverlaps. Collision-triggered activation only;
            // an explicit use goes through Player.TryUseItem -> OnActivate and never reaches here.
            if (player.IsArrivalOverlapPortal(this))
                return;

            OnActivate(player);
        }

        /// <summary>
        /// Signed cylinder distance (Physics.Common.Position.CylinderDistance: edge to edge, negative when the
        /// cylinders interpenetrate) at or under which a player who has just arrived counts as already standing
        /// in a portal. A small positive margin on top of "touching", because the collision that fires the portal
        /// is computed from the physics shapes, not from this bounding cylinder, and a player who arrives right at
        /// the edge must not be re-entered by their first step either.
        /// </summary>
        public const double ArrivalOverlapEnterDistance = 0.25;

        /// <summary>
        /// Signed cylinder distance a player must get beyond before an arrival overlap is released and the portal
        /// may fire on collision again. Larger than <see cref="ArrivalOverlapEnterDistance"/> so the release is
        /// hysteretic: jitter around the edge cannot release and re-collide on consecutive position reports.
        /// </summary>
        public const double ArrivalOverlapReleaseDistance = 1.0;

        /// <summary>
        /// True if a player arriving at <paramref name="cylinderDistance"/> from a portal is already overlapping it.
        /// Pure and static so the rule can be unit tested with no server.
        /// </summary>
        public static bool IsArrivalOverlap(double cylinderDistance)
        {
            return cylinderDistance <= ArrivalOverlapEnterDistance;
        }

        /// <summary>
        /// True once a player held by an arrival overlap at <paramref name="cylinderDistance"/> has walked clear
        /// of the portal, so a later collision with it is a genuine walk-in. Pure and static, as above.
        /// </summary>
        public static bool HasLeftArrivalOverlap(double cylinderDistance)
        {
            return cylinderDistance > ArrivalOverlapReleaseDistance;
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

        /// <summary>
        /// True while a portal use has been accepted and its teleport is still queued - the window between
        /// <see cref="ActOnUse"/> handing the move to WorldManager.ThreadSafeTeleport and Player.Teleport
        /// actually running on the next world tick. Neither Player.Teleporting nor LastPortalTeleportTimestamp
        /// is set until Player.Teleport runs, so without this a SECOND activation of the same portal inside
        /// that window - the move-to chain's use callback and the walk-in collision (Player.OnCollideObject
        /// -> Portal.OnCollideObject -> OnActivate) both firing, or a repeated use request - passes every
        /// guard in <see cref="CheckUseRequirements"/> and runs ActOnUse again. For an instanced portal that
        /// means a second ephemeral instance, a second teleport, and EphemeralRealmExitTo overwritten with a
        /// position inside the first instance.
        /// <para/>
        /// Bounded by the same 3.5 s as the post-teleport cooldown, and measured as an absolute difference, so
        /// a marker that is somehow never cleared (a queued teleport that never runs) can never lock a player
        /// out of portals for longer than the ordinary cooldown. Player.Teleport clears it both when the
        /// teleport starts and when it refuses an ephemeral destination.
        /// <para/>
        /// Pure and static so the rule can be unit tested with no server.
        /// </summary>
        public static bool IsPortalTeleportPending(double? pendingSince, double now)
        {
            return pendingSince.HasValue && System.Math.Abs(now - pendingSince.Value) < minTimeSinceLastPortal;
        }

        public override ActivationResult CheckUseRequirements(WorldObject activator)
        {
            if (!(activator is Player player))
                return new ActivationResult(false);

            if (player.Teleporting)
                return new ActivationResult(false);

            // an earlier use of a portal has already been accepted and its teleport is queued - see
            // IsPortalTeleportPending. Silent, like the Teleporting check above: this is a duplicate of a use
            // the player is already getting, not a refusal they need to be told about.
            if (IsPortalTeleportPending(player.PendingPortalTeleportTime, Time.GetUnixTime()))
                return new ActivationResult(false);

            // A speed run ends by picking up the season's objective, NOT by killing the boss or by reaching this
            // portal - so an instance-exiting portal used mid-run is a forfeit, and before this guard it was a
            // SILENT one: the player teleported out and only then learned the clear was gone. Refuse the first
            // use with a warning and let a second, deliberate use through. A finished run has already cleared
            // SpeedChallengeActive, so this cannot block the way out after a legitimate completion.
            if ((GetProperty(PropertyInt.PortalExitInstance) ?? 0) == 1 && !player.CheckSpeedChallengeExitConfirmed())
                return new ActivationResult(false);

            // Speed-challenge season portal (WaffleACE): the one portal in the game that carries no Destination
            // of its own. Its entry position IS the active speed_season row, resolved at use time by
            // SpeedSeasonManager (Docs/ProvingGroundsSpeed/DESIGN.md section 3.2) - which is what lets a
            // rotation be a database row rather than a content apply. The generic "destination not yet
            // implemented" refusal below must therefore not fire for it; its real refusal is the no-active-season
            // block further down, next to the other Proving Grounds portal check.
            var isSpeedChallengeEntry = GetProperty(PropertyBool.SpeedChallengeEntry) == true;

            if (Destination == null && !isSpeedChallengeEntry)
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

            // Bluespire ladder (PropertyInt 9069 BluespireLadderRung): the prerequisite gate on the six-rung
            // Marae Lassel dungeon ladder. PRESENCE of the rung property is what makes a portal a ladder
            // portal, so every other portal in the game pays exactly the null check below. There is no
            // level requirement to enter any depth (owner ruling, 2026-09-24, round 19 A2) - only the
            // rung-1 entry quest and each rung's previous-rung clear stamp gate entry.
            //
            // It sits HERE, after the PortalRestrictions block and beside the SpeedChallengeEntry clause,
            // and honours IgnorePortalRestrictions exactly as its neighbours do.
            //
            // The gate is re-evaluated on EVERY use and is never baked into an unlock stamp - that is the
            // ratified requirement, so that raising a rung's requirement re-locks the characters who
            // unlocked it at the old value. Deliberately NOT built on MinLevel (86) or QuestRestriction
            // (37): those are weenie properties, so a retune would be a content apply rather than a live
            // tune, and QuestRestriction holds one string where rung 1 needs two conditions.
            var bluespireRung = GetProperty(PropertyInt.BluespireLadderRung);

            if (bluespireRung != null && !player.IgnorePortalRestrictions)
            {
                var refusal = ACE.Server.Entity.BluespireLadder.CheckEntry(player, bluespireRung.Value);

                if (refusal != null)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(refusal, ChatMessageType.System));
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

            // Proving Grounds speed portal (SpeedChallengeEntry): with no active season there is no dungeon to
            // enter and nowhere to file a result, so the portal refuses rather than dumping the player somewhere
            // wrong. This is the PRIMARY refusal for this portal and it is an entirely normal state - the gap
            // between two seasons - so it gets a plain player-facing message rather than a WeenieError.
            if (isSpeedChallengeEntry && SpeedSeasonManager.GetActiveSeason() == null)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    "The trial grounds are being reset between seasons - no dungeon is set right now. Return when the next rotation begins.",
                    ChatMessageType.System));
                return new ActivationResult(false);
            }

            // Threads pooled loot (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 7): every portal in a Thread copy
            // leads out (ThreadDungeonContentFilter replaces them all with the exit), so leaving a Cleared run with
            // loot still pooled asks first, after every refusal above. Yes re-activates this same portal once, with a
            // bypass. Only a portal standing in the world: a portal recall rebuilds its portal from the weenie and
            // calls this method on it, and that recall has already asked (plan open question 4).
            if (CurrentLandblock != null && ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(player, () => { if (!IsDestroyed) OnActivate(player); }))
                return new ActivationResult(false);

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

        /// <summary>
        /// Decides whether a PortalSameInstance-flagged portal may safely resolve via
        /// PlayerInstanceSelectMode.Same (the player's current instance, unchanged) rather than the normal
        /// HomeRealmDefault route. That mode only makes sense when the portal's own Destination is in the
        /// SAME landblock the player is standing in: the player's current instance is - by construction -
        /// whatever instance already contains that landblock (it is where they are standing right now), so
        /// reusing it is safe. It is NOT safe when Destination names a DIFFERENT landblock: an ephemeral
        /// instance is a private copy of the one landblock it was spun up for (see
        /// LandblockManager.GetEphemeralLandblock / InstanceRouting.ValidateInstanceDestination), so
        /// nothing guarantees the player's current instance also contains a second, different landblock -
        /// routing there blind risks loading a landblock into an instance that was never meant to hold it,
        /// or a landblock that silently falls back to a shared/empty copy. Refusing here is what forces
        /// Portal.ActOnUse to fall back to the normal HomeRealmDefault resolution instead.
        /// <para/>
        /// Pure and static so this one rule can be unit tested with no server, mirroring
        /// Player_SpeedChallenge.IsSeasonObjective.
        /// </summary>
        public static bool CanUseSameInstanceForPortal(LandblockId destinationLandblock, LandblockId playerLandblock)
        {
            return destinationLandblock.Landblock == playerLandblock.Landblock;
        }

        /// <summary>
        /// Portal Recall remembers only a weenie id (LastPortalDID) and later resolves a portal rebuilt from that
        /// weenie. That is only faithful when this live object's PortalRealm matches the weenie's - a live-only
        /// /portal-realm override would otherwise recall into a different realm copy. Only looked up when this
        /// object carries a PortalRealm at all, so ordinary portals pay nothing. See Portal.SamePortalRealm.
        /// </summary>
        private bool RecallResolvesToSameRealm()
        {
            var livePortalRealm = GetProperty(PropertyInt.PortalRealm);

            if (livePortalRealm == null)
                return true;

            var weenie = DatabaseManager.World.GetCachedWeenie(OriginalPortal ?? WeenieClassId);

            return SamePortalRealm(livePortalRealm, weenie?.GetProperty(PropertyInt.PortalRealm));
        }

        public override void ActOnUse(WorldObject activator)
        {
            var player = activator as Player;
            if (player == null) return;

            // Re-checked here as well as in CheckUseRequirements because ActOnUse has a caller that skips that
            // gate (OnCastSpell, for a portal with no SpellDID). Everything below - the ephemeral instance, the
            // EphemeralRealmExitTo stamp, the challenge arming and the teleport - must happen at most once per
            // accepted use.
            if (IsPortalTeleportPending(player.PendingPortalTeleportTime, Time.GetUnixTime()))
            {
                log.Warn($"[PORTAL] {player.Name} (0x{player.Guid}) - ignored a duplicate use of portal {WeenieClassId} ({Guid}) while an earlier portal teleport is still queued.");
                return;
            }

#if DEBUG
            // player.Session.Network.EnqueueSend(new GameMessageSystemChat("Portal sending player to destination", ChatMessageType.System));
#endif
            // Speed-challenge season portal (WaffleACE): SpeedChallengeEntry drops the player into a strictly
            // single-player ephemeral copy of the CURRENT SEASON's dungeon and arms a timed run that starts when
            // they land (see Player_SpeedChallenge.cs). Resolved FIRST and exactly once, because unlike the three
            // arenas below this portal is not configured by properties on its own weenie: it carries no
            // Destination at all, and its entry position is the active speed_season row.
            var isSpeedChallenge = GetProperty(PropertyBool.SpeedChallengeEntry) == true;
            var speedSeason = isSpeedChallenge ? SpeedSeasonManager.GetActiveSeason() : null;
            var armSpeedChallenge = false;

            if (isSpeedChallenge && speedSeason == null)
            {
                // CheckUseRequirements already refused this case. A season can still rotate out in the window
                // between that check and here, so it is re-checked rather than falling through to a normal
                // teleport - there is no normal teleport to fall through TO, this weenie has no Destination.
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    "The trial grounds are being reset between seasons - no dungeon is set right now. Return when the next rotation begins.",
                    ChatMessageType.System));
                return;
            }

            Position portalDest;

            if (isSpeedChallenge)
            {
                // The season row is already an exact, realm-qualified entry position, so it is built straight
                // from the row and bound to that realm's DEFAULT instance (the same shape ApplyPortalRealm uses)
                // rather than going through AdjustDungeon / ResolvePortalDestination. Both are deliberately
                // skipped, and neither is an oversight:
                //   * ResolvePortalDestination would rebind the position to the PLAYER's home realm default
                //     instance (PlayerInstanceSelectMode.HomeRealmDefault) and discard season.RealmId, so a
                //     season authored against a realm copy would silently run the base world's content instead.
                //   * AdjustDungeonPos is a no-op for every dungeon as the code stands - AdjustPos.DungeonProfiles
                //     is empty, every profile in Physics/Util/AdjustPos.cs being commented out - and
                //     AdjustDungeonCells only corrects a cell id that disagrees with the position, which a row
                //     authored from a /loc dump does not have. What it WOULD do is load the landblock in whatever
                //     instance it is handed (LScape.get_landblock -> LandblockManager.GetLandblock), and the
                //     instance at this point is the realm default, not the ephemeral instance the player actually
                //     lands in - so its only reachable effect here is spinning up a shared-world copy of the
                //     season dungeon that nothing ever uses.
                // The realm binding itself is a hard requirement rather than a preference: an unknown realm id is
                // a content error in the season row, and entering the wrong realm's copy of the dungeon would run
                // the season against the wrong content without anything looking broken. Refuse instead.
                var seasonRealm = RealmManager.GetRealm(speedSeason.RealmId);

                if (seasonRealm == null)
                {
                    log.Error($"Portal {WeenieClassId} (SpeedChallengeEntry): speed season {speedSeason.Id} ({speedSeason.Name}) declares realm {speedSeason.RealmId}, which is not in the realm registry - refusing use rather than entering the wrong realm's dungeon.");
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        "This season's dungeon is misconfigured and cannot be entered. Please report this to an administrator.",
                        ChatMessageType.System));
                    return;
                }

                // rotation component order is X, Y, Z, W - the same obj_cell_id/origin/angles -> Position
                // conversion every other call site uses (e.g. DeveloperContentCommands.cs:2996)
                portalDest = new Position(speedSeason.ObjCellId,
                    speedSeason.OriginX, speedSeason.OriginY, speedSeason.OriginZ,
                    speedSeason.AnglesX, speedSeason.AnglesY, speedSeason.AnglesZ, speedSeason.AnglesW,
                    seasonRealm.DefaultInstanceID);

                // Deliberate addition beyond the three arenas. THEIR misconfiguration is merely inert: a DPS
                // portal missing PortalInstancing teleports to its own Destination and arms nothing. A season
                // portal missing it would drop the player into the SHARED-WORLD copy of the season dungeon,
                // un-armed and with no error raised anywhere, which is actively wrong. So it refuses - and only
                // refuses. Instancing is never forced on here, because silently instancing a portal the content
                // author did not mark would hide exactly the same mistake.
                if ((GetProperty(PropertyInt.PortalInstancing) ?? 0) != 1)
                {
                    log.Error($"Portal {WeenieClassId} carries SpeedChallengeEntry but not PortalInstancing - refusing use. A season portal MUST be instanced; without it the player would enter the shared-world copy of {speedSeason.DungeonName} with no run armed.");
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        "This portal is misconfigured and cannot be entered. Please report this to an administrator.",
                        ChatMessageType.System));
                    return;
                }
            }
            else
            {
                portalDest = new Position(Destination);
                AdjustDungeon(portalDest);

                // resolve which instance the destination lands in: house portals stay in
                // the player's current instance; everything else routes to the default
                // instance of the player's home realm. Either way an explicit PortalRealm
                // wins (see ResolvePortalDestination / ApplyPortalRealm)
                //
                // PortalSameInstance opts a plain portal into the same "stay in the player's current
                // instance" behavior HousePortal gets for free - the chute-back-to-hub case inside a
                // per-run ephemeral dungeon copy - but only when it is actually safe: CanUseSameInstanceForPortal
                // requires Destination to be in the landblock the player is currently standing in, because
                // that is the only landblock guaranteed to exist in the player's current instance. A portal
                // flagged for a DIFFERENT landblock is a content error (it would ask for a landblock an
                // arbitrary ephemeral instance was never built to hold), so it is logged and falls back to
                // the normal HomeRealmDefault route rather than routed blind.
                var useSameInstance = this is HousePortal;

                if (!useSameInstance && GetProperty(PropertyBool.PortalSameInstance) == true)
                {
                    if (CanUseSameInstanceForPortal(portalDest.LandblockId, player.Location.LandblockId))
                        useSameInstance = true;
                    else
                        log.Warn($"Portal {WeenieClassId} carries PortalSameInstance but Destination ({portalDest.LandblockId.Landblock:X4}) is a different landblock from the one the player is in ({player.Location.LandblockId.Landblock:X4}) - falling back to the player's home realm default instance rather than routing into an instance that may not contain that landblock.");
                }

                if (useSameInstance)
                    portalDest = ApplyPortalRealm(this, portalDest.AsInstancedPosition(player, PlayerInstanceSelectMode.Same));
                else
                    portalDest = ResolvePortalDestination(this, player, portalDest);
            }

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
                // a DPS-, survival-, wave- or speed-challenge instance is never open to the owner's fellowship - the run is scored per player
                var ephemeralLandblock = RealmManager.GetNewEphemeralLandblock(destLandblockId, player, destRealmId, openToFellowship: !isDpsChallenge && !isSurvivalChallenge && !isWaveChallenge && !isSpeedChallenge);

                if (ephemeralLandblock != null && ephemeralLandblock.IsDungeon)
                {
                    // Prove the instance we just created actually resolves, using the SAME check the
                    // teleport will run (Player.Teleport -> InstanceRouting.ValidateInstanceDestination),
                    // and do it BEFORE any side effect is applied. Everything below this point is a
                    // commitment: the exit position is stamped and the challenge flags are persisted ahead
                    // of the teleport on purpose, so that a mid-run logout is caught at next login. If the
                    // instance did not resolve, the teleport would refuse and those side effects would
                    // already be in place, so the cheapest correct order is to find out first.
                    //
                    // Refusing the portal outright, rather than falling through to the non-instanced
                    // destination, is the deliberate part. portalDest at this point is the arena's
                    // coordinates in the player's HOME REALM DEFAULT instance - the shared-world copy - and
                    // every landblock reached by an instanced portal keeps its content in a realm overlay,
                    // so that copy is empty geometry: no monsters, no exit portal, /die to leave. That
                    // exact outcome is the prod bug this guard was written for; silently arriving there is
                    // strictly worse for the player than not travelling at all.
                    var instancedDest = new Position(portalDest, ephemeralLandblock.Instance);

                    instancedDest.ValidateInstanceDestination(player, out var instanceRejection);

                    if (instanceRejection != InstanceRejection.None)
                    {
                        log.Error($"Portal {WeenieClassId}: the ephemeral instance 0x{ephemeralLandblock.Instance:X8} just created for landblock 0x{destLandblockId.Landblock:X4} does not validate ({instanceRejection}) - refusing use rather than sending {player.Name} into the shared-world copy of that landblock.");
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                            "The private instance could not be prepared. Please try again in a moment.",
                            ChatMessageType.System));
                        LandblockManager.AddToDestructionQueue(ephemeralLandblock);
                        return;
                    }

                    player.SetPosition(PositionType.EphemeralRealmExitTo, new Position(player.Location));
                    portalDest = instancedDest;
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

                    if (isSpeedChallenge)
                    {
                        // persist the armed flag before teleport so a mid-run logout is caught at next login.
                        // Same hard ordering constraint the wave branch above spells out: this MUST happen here,
                        // ahead of ThreadSafeTeleport, and StartSpeedChallenge must run inside the
                        // teleport-completion delegate below. The completion delegate runs before
                        // OnTeleportComplete's exit reconciliation (Player_Location.cs:903), so by the time
                        // CheckSpeedChallengeInstanceExit reads this flag the run is already bound to the season
                        // instance. Arming any later would make that reconciliation see an active-but-unbound run
                        // and forfeit it the instant the player arrives.
                        player.SpeedChallengeActive = true;
                        player.RushNextPlayerSave(5);
                        armSpeedChallenge = true;
                    }
                }
                else if (ephemeralLandblock != null)
                {
                    log.Warn($"Portal {WeenieClassId} has PortalInstancing but destination 0x{destLandblockId.Landblock:X4} is not a dungeon - using normal destination");
                    LandblockManager.AddToDestructionQueue(ephemeralLandblock);
                }
            }

            // Speed only, and the same deliberate addition as the PortalInstancing guard above. Reaching here
            // un-armed means no ephemeral instance was created - the season's landblock is not a dungeon, or the
            // instance allocation returned nothing - both of which are already logged just above. For the three
            // arenas falling through is merely inert (they arrive somewhere harmless with no run armed), but a
            // season portal's portalDest is the season entry position in the realm's DEFAULT instance, so falling
            // through would put the player inside the SHARED-WORLD copy of the season dungeon. Refuse instead.
            if (isSpeedChallenge && !armSpeedChallenge)
            {
                log.Error($"Portal {WeenieClassId} (SpeedChallengeEntry): no ephemeral instance was created for speed season {speedSeason.Id} ({speedSeason.Name}) at 0x{speedSeason.ObjCellId:X8} - refusing use rather than entering the shared-world copy of the dungeon.");
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    "This season's dungeon could not be prepared. Please report this to an administrator.",
                    ChatMessageType.System));
                return;
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

            // Same-landblock realm hop guard, on the FINAL destination and persistent-to-persistent only: a portal
            // standing in one realm copy of a landblock that leads to another realm copy of the same landblock
            // (realm 0's Aerfalle Keep and realm 1's Marketplace are both 0x01F5) renders as a blend of both.
            // Ephemeral ends are excluded (InstanceRouting.IsPersistentSameLandblockRealmHop), so an instanced
            // portal into a private copy of the player's own landblock, or an exit portal out of one, is
            // untouched - and every side effect above is ephemeral-only, so refusing here leaves nothing armed.
            if (InstanceRouting.IsPersistentSameLandblockRealmHop(player.Location, portalDest))
            {
                log.Warn($"Portal {WeenieClassId}: refusing {player.Name} a same-landblock realm hop from instance 0x{player.Location.Instance:X8} to 0x{portalDest.Instance:X8} at landblock 0x{portalDest.LandblockShort:X4}.");
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(InstanceRouting.SameLandblockRealmHopRefusal, ChatMessageType.System));
                return;
            }

            // Mark the teleport as pending BEFORE it is queued: ThreadSafeTeleport defers Player.Teleport to the
            // next world tick, and until then nothing else tells a second activation that this use was already
            // accepted (see IsPortalTeleportPending). Stamped only here, after every early return above, so a
            // refused use never sets it.
            player.PendingPortalTeleportTime = Time.GetUnixTime();

            WorldManager.ThreadSafeTeleport(player, portalDest, new ActionEventDelegate(() =>
            {
                // If the portal just used is able to be recalled to, remember WHICH portal it was:
                // LastPortalDID stores a weenie class id, not a position. Portal Recall rebuilds the
                // portal from that weenie and re-resolves its destination (see
                // WorldObject_Magic.HandleCastSpell_PortalRecall).
                if (!NoRecall && RecallResolvesToSameRealm())
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

                // speed trial: start the timed run on arrival, so the clock starts when the player LANDS rather
                // than when they clicked. The season resolved at the top of ActOnUse is carried in here rather
                // than re-read, so a rotation landing between the click and the landing cannot switch which
                // season the run is filed into (StartSpeedChallenge captures season.Id at arm time).
                if (armSpeedChallenge)
                    player.StartSpeedChallenge(speedSeason);

                // a Proving Grounds portal strips rare-gem buffs on arrival. Only the Prodigal spells go: the
                // Incantations and Auras a player could have cast on themselves survive, because the strip set is
                // narrowed to spells with a rare SpellCategory. See StripRareGemBuffs.
                if (GetProperty(PropertyBool.PortalBlocksRareGems) == true)
                    player.StripRareGemBuffs();

            }), true);
        }
    }
}
