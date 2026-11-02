using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.HazardMechanics;
using ACE.Server.Knots;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// The live <see cref="IPinnacleWorld"/>: one fight's copy. Every member that touches the copy runs on the
    /// fight's landblock thread (the core calls it only from there). The calls that reach the OWNER's Knot
    /// (<see cref="ChargeEntry"/>, <see cref="ReportBossKilled"/>, <see cref="ReportAttemptLost"/>) are marshalled
    /// onto the owner's thread, because KnotEncounterEvents needs the owner's thread and a live session.
    /// The hazard host half (<see cref="IHazardHost.OnEnded"/>) may fire on ANY thread and only enqueues onto the
    /// fight's landblock.
    /// </summary>
    public sealed class LivePinnacleWorld : IPinnacleWorld, IHazardHost
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The inert effect record that lets SetImmune work (attached before EnterWorld; precedent MlDigsiteSpawner).</summary>
        public const string ImmuneRecord = "immune";

        /// <summary>The nod: pitch angles (radians) at 0, 0.2 and 0.4 s. The last entry is the rest pose.</summary>
        internal static readonly (double DelaySeconds, float Pitch)[] BobSteps = { (0.0, -0.35f), (0.2, -0.17f), (0.4, 0.0f) };

        private readonly Landblock landblock;
        private readonly uint instance;
        private readonly uint ownerGuid;
        private readonly uint knotGuid;
        private readonly int knotLevel;
        private readonly string hazardPreset;

        private readonly Dictionary<int, WorldObject> objects = new Dictionary<int, WorldObject>();
        private readonly Dictionary<int, Quaternion> baseRotation = new Dictionary<int, Quaternion>();
        private readonly Dictionary<int, HazardRun> runs = new Dictionary<int, HazardRun>();
        private readonly Dictionary<int, Tolerance> authoredTolerance = new Dictionary<int, Tolerance>();

        /// <summary>Guid -> handle id of every prop spawned through <see cref="SpawnProp"/>. Concurrent: the door-use hook reads it on the user's thread (<see cref="TryGetHandle"/>), the fight thread writes it.</summary>
        private readonly ConcurrentDictionary<uint, int> propHandles = new ConcurrentDictionary<uint, int>();
        private int nextHandle = 1;

        private PinnacleFight fight;
        private bool cachePlacementFailureLogged;

        /// <summary>PinnacleScriptData.HeadFaceYawOffset: added to the head's turn; pi also flips the nod's pitch sign.</summary>
        private readonly float headFaceYawOffset;

        /// <summary>PinnacleScriptData.HeadCastHeight: the caster's WorldObject.ProjectileCastHeight at each cast; 0 = unchanged.</summary>
        private readonly float headCastHeight;

        /// <summary>PinnacleDef.RewardTable: the loot table rolled into the cache on a win; null rolls nothing.</summary>
        private readonly string rewardTable;

        /// <summary>Each caster's RadarColor before the first SetAttackable(true), restored by SetAttackable(false).</summary>
        private readonly Dictionary<int, RadarColor?> authoredRadar = new Dictionary<int, RadarColor?>();

        /// <summary>Who opened this copy and when (telemetry only); null when the creator supplied none.</summary>
        private readonly PinnacleAttemptContext attempt;

        /// <summary>PinnacleScriptData.CachePoint / ReturnPortalPoint: fixed spots for the reward and the way out; null = at the owner.</summary>
        private readonly PinnaclePoint? cachePoint;
        private readonly PinnaclePoint? returnPortalPoint;

        /// <summary>PinnacleScriptData.PlateFloor: the object floor a player is lifted back onto; null = no lift.</summary>
        private readonly PinnaclePlateFloor plateFloor;

        /// <summary>Per player guid: the monotonic times of their lifts inside the escalation window, oldest first (PinnaclePlateLift's rate limit and escalation). Fight thread.</summary>
        private readonly Dictionary<uint, List<double>> plateLifts = new Dictionary<uint, List<double>>();

        /// <summary>Per player guid: the monotonic times of their escalation teleports inside the cap window (PinnaclePlateLift.Decide). Fight thread.</summary>
        private readonly Dictionary<uint, List<double>> plateTeleports = new Dictionary<uint, List<double>>();

        /// <summary>Players the lift has given up on (past the teleport cap): never lifted again in this fight. Fight thread.</summary>
        private readonly HashSet<uint> plateLiftGivenUp = new HashSet<uint>();

        public LivePinnacleWorld(Landblock landblock, uint instance, uint ownerGuid, uint knotGuid, int knotLevel, string hazardPreset, float headFaceYawOffset = 0f, float headCastHeight = 0f, string rewardTable = null,
            PinnacleAttemptContext attempt = null, PinnaclePoint? cachePoint = null, PinnaclePoint? returnPortalPoint = null, PinnaclePlateFloor plateFloor = null)
        {
            this.plateFloor = plateFloor;
            this.attempt = attempt;
            this.cachePoint = cachePoint;
            this.returnPortalPoint = returnPortalPoint;
            this.headFaceYawOffset = headFaceYawOffset;
            this.headCastHeight = headCastHeight;
            this.rewardTable = rewardTable;
            this.landblock = landblock ?? throw new ArgumentNullException(nameof(landblock));
            this.instance = instance;
            this.ownerGuid = ownerGuid;
            this.knotGuid = knotGuid;
            this.knotLevel = knotLevel;
            this.hazardPreset = hazardPreset;
        }

        /// <summary>Connects the fight whose hazard endings this world routes. Called once, before the first tick.</summary>
        public void Bind(PinnacleFight fight) => this.fight = fight;

        public Landblock Landblock => landblock;

        /// <summary>The id minted for this copy (ties its telemetry rows together), or null when no context was supplied.</summary>
        public string AttemptId => attempt?.AttemptId;

        // ================================================================ state

        public bool IsLive => ReferenceEquals(LandblockManager.GetEphemeralLandblock(instance), landblock);

        public bool OwnerPresent => FindOwnerOnCopy() != null;

        // a cross-instance teleport puts the player on the copy's landblock at once; Teleporting stays set until the
        // client's arrival (OnTeleportComplete), the portal-space window in which they cannot move
        public bool OwnerSettled => FindOwnerOnCopy() is Player p && !p.Teleporting;

        public bool OwnerOnline => PlayerManager.GetOnlinePlayer(ownerGuid) != null;

        public bool OwnerAliveElsewhere
        {
            get
            {
                var owner = PlayerManager.GetOnlinePlayer(ownerGuid);

                return owner?.Location != null && !owner.IsDead && owner.Health.Current > 0 && owner.Location.Instance != instance;
            }
        }

        private Player FindOwnerOnCopy()
        {
            var players = landblock.PlayersOnLandblockThread;

            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];

                if (p != null && p.Guid.Full == ownerGuid && p.Health.Current > 0)
                    return p;
            }

            return null;
        }

        /// <summary>True when <paramref name="wo"/> is the object behind <paramref name="h"/> (the head-use routing test). Fight thread.</summary>
        public bool IsObject(PinnacleHandle h, WorldObject wo)
            => wo != null && objects.TryGetValue(h.Id, out var mine) && ReferenceEquals(mine, wo);

        // ================================================================ objects

        public PinnacleHandle Spawn(uint wcid, PinnaclePoint at, bool withImmuneRecord, PinnacleTier tier, bool leavesCorpse = false)
        {
            WorldObject wo = null;

            try
            {
                wo = WorldObjectFactory.CreateNewWorldObject(wcid);

                if (wo == null)
                {
                    log.Warn($"[PINNACLE] wcid {wcid} did not resolve to a weenie");
                    return PinnacleHandle.None;
                }

                // Dynamic guid, no generator: the heartbeat would rot it. The copy's unload ends it instead.
                wo.TimeToRot = -1;

                if (wo is Creature creature)
                {
                    // Order: scale (band uplift + tier) -> immune overlay -> StripKillRewards LAST -> EnterWorld.
                    PinnacleScaling.Apply(creature, knotLevel, tier);

                    // BEFORE EnterWorld: ApplyMonsterEffectOverlay allocates a fresh effect-state array.
                    if (withImmuneRecord && !creature.ApplyMonsterEffectOverlay(ImmuneRecord))
                        log.Warn($"[PINNACLE] immune record resolved to nothing on wcid {wcid}; SetImmune will have no effect on it");

                    // Round 4: no encounter creature leaves a corpse (they carry no loot and pay no XP; the reward is the
                    // cache). The head's corpse was an opaque full-size copy of the head that stood until it decayed.
                    // A script whose head should lie where it fell opts in (PinnacleScriptData.HeadLeavesCorpse); its
                    // weenie carries no treasure rows, so the corpse is empty. The weenie's own NoCorpse would still skip
                    // CreateCorpse (Creature_Death.CreateCorpse returns early on it), so the opt-in clears it too.
                    creature.PinnacleCorpseless = !leavesCorpse;

                    if (leavesCorpse)
                    {
                        creature.NoCorpse = false;
                        creature.PinnacleCorpseKept = true;
                    }

                    // R2: no encounter creature pays XP or luminance; the reward is the cache. LAST before EnterWorld,
                    // so nothing the scaling or the overlay might write can bring a kill reward back.
                    StripKillRewards(creature);
                }

                wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, instance);

                if (!wo.EnterWorld())
                {
                    wo.Destroy();
                    return PinnacleHandle.None;
                }

                var h = new PinnacleHandle(nextHandle++);
                objects[h.Id] = wo;
                baseRotation[h.Id] = new Quaternion(at.QX, at.QY, at.QZ, at.QW);
                return h;
            }
            catch (Exception ex)
            {
                log.Error($"[PINNACLE] spawn of wcid {wcid} threw", ex);

                try { wo?.Destroy(); } catch (Exception) { /* already failing */ }

                return PinnacleHandle.None;
            }
        }

        /// <summary>
        /// Zeroes the two values Creature.OnDeath_GrantXP pays from - XpOverride (times the damage share) and
        /// LuminanceAward - so the death-XP path grants nothing, on this instance only (both are per-biota properties).
        /// </summary>
        internal static void StripKillRewards(Creature creature)
        {
            creature.XpOverride = 0;
            creature.LuminanceAward = null;
        }

        private WorldObject Get(PinnacleHandle h) => !h.IsNone && objects.TryGetValue(h.Id, out var wo) ? wo : null;

        public bool IsAlive(PinnacleHandle h)
        {
            var wo = Get(h);

            if (wo == null || wo.IsDestroyed)
                return false;

            return !(wo is Creature c && c.IsDead);
        }

        public void Destroy(PinnacleHandle h)
        {
            var wo = Get(h);

            if (wo != null)
                propHandles.TryRemove(wo.Guid.Full, out _);

            if (wo != null && !wo.IsDestroyed)
                wo.Destroy();
        }

        public bool TryGetXY(PinnacleHandle h, out float x, out float y)
        {
            var wo = Get(h);

            if (wo?.Location == null)
            {
                x = y = 0f;
                return false;
            }

            x = wo.Location.PositionX;
            y = wo.Location.PositionY;
            return true;
        }

        /// <summary>
        /// The spell-proof immunity switch (the "immune" record reads P_DigsiteImmune). A real toggle also plays the
        /// grey shield cue the digsite immune phase uses (ImmunePhasesMechanic: ShieldUpGrey on, ShieldDownGrey off), so
        /// the player can see the change; setting the value it already has plays nothing.
        /// </summary>
        public void SetImmune(PinnacleHandle h, bool immune)
        {
            if (!(Get(h) is Creature c) || c.IsDestroyed)
                return;

            var changed = c.P_DigsiteImmune != immune;
            c.P_DigsiteImmune = immune;

            if (changed)
                ACE.Server.MlDigsite.MlDigsiteProps.PlayScriptOn(c, immune ? PlayScript.ShieldUpGrey : PlayScript.ShieldDownGrey);
        }

        /// <summary>
        /// Held: NoAttack on top of the creature's own Tolerance (it then neither wakes for a nearby player nor scans for
        /// one) and an awake one is put to sleep, which drops its target. Restored to the exact authored Tolerance.
        /// </summary>
        public void SetPeaceful(PinnacleHandle h, bool peaceful)
        {
            if (!(Get(h) is Creature c) || c.IsDestroyed)
                return;

            if (peaceful)
            {
                if (!authoredTolerance.ContainsKey(h.Id))
                    authoredTolerance[h.Id] = c.Tolerance;

                c.Tolerance |= Tolerance.NoAttack;

                if (c.IsAwake)
                    c.Sleep();
            }
            else if (authoredTolerance.TryGetValue(h.Id, out var authored))
            {
                authoredTolerance.Remove(h.Id);
                c.Tolerance = authored;
            }
        }

        public void SetAttackable(PinnacleHandle h, bool attackable)
        {
            var wo = Get(h);

            if (wo == null || wo.IsDestroyed)
                return;

            wo.Attackable = attackable;

            // The name and radar colour: the head is authored Yellow (RadarBlipColor 8, the NPC colour), and the weenie
            // header of every UpdateObject carries that value (WorldObject_Networking: RadarBlipColor is written whenever
            // RadarColor is set), so flipping Attackable alone left it yellow in the 2026-10-20 playtest. Attackable, it
            // takes RadarColor.Creature (Gold, the attackable-monster colour); not attackable again, its authored colour.
            if (attackable && !authoredRadar.ContainsKey(h.Id))
                authoredRadar[h.Id] = wo.RadarColor;

            var radar = RadarFor(attackable, authoredRadar.TryGetValue(h.Id, out var authored) ? authored : wo.RadarColor);

            if (radar != wo.RadarColor)
            {
                wo.RadarColor = radar;

                // the property update too, as AdvocateItem does for a live radar change
                wo.EnqueueBroadcast(true, new GameMessagePublicUpdatePropertyInt(wo, PropertyInt.RadarBlipColor, (int)(radar ?? RadarColor.Default)));
            }

            if (!attackable)
                authoredRadar.Remove(h.Id);

            if (wo is Creature c)
                c.SetMonsterState();

            // the full object description again: Attackable (ObjectDescriptionFlags) and the radar colour (weenie header)
            wo.EnqueueBroadcast(new GameMessageUpdateObject(wo));
        }

        /// <summary>The radar (and name) colour for an attackable flip: Creature (Gold) while attackable, else <paramref name="authored"/>.</summary>
        internal static RadarColor? RadarFor(bool attackable, RadarColor? authored) => attackable ? RadarColor.Creature : authored;

        public int GetHealth(PinnacleHandle h) => Get(h) is Creature c ? (int)c.Health.Current : 0;

        public void ClampHealth(PinnacleHandle h, int max)
        {
            var cap = (uint)Math.Max(0, max);

            if (Get(h) is Creature c && c.Health.Current > cap)
                c.Health.Current = cap;
        }

        // ================================================================ head: face and nod

        /// <summary>
        /// The magnitude of the TurnSpeed the head's turn command carries: the client turns at the motion table's turn
        /// omega times this (1006909's table 0x0900016A: 3.5 rad/s, about 200 degrees a second). The client turns in
        /// whole steps of omega / framerate (6.7 degrees for 0x0900016A) at framerate x speed steps a second, so the
        /// speed sets only how often a step is drawn, never how fine it is: 1 (30 steps a second) is the smoothest the
        /// table can draw; 0.5 would draw the same steps half as often (15 a second), a visibly jerkier turn.
        /// </summary>
        public const float HeadTurnSpeed = 1.0f;

        /// <summary>The head's turn and nod bookkeeping (pure, unit-tested in PinnacleFacingTests); this class only sends what it plans.</summary>
        private readonly PinnacleHeadTurns headTurns = new PinnacleHeadTurns(ACE.Common.Time.GetUnixTime);

        /// <summary>A dead head (lingering through its death animation) is neither turned nor nodded: it keeps its death pose.</summary>
        private static bool IsAliveHead(WorldObject wo) => !(wo is Creature c && c.IsDead);

        /// <summary>
        /// Turns the head to face the owner. With a motion table that has a TurnRight cycle (1006909 carries 0x0900016A)
        /// the CLIENT animates the turn: one UpdateMotion with a TurnRight command at a signed speed starts it, and a
        /// Ready motion after the computed duration stops it. Motion messages are not subject to the grounded-or-teleport
        /// rule that drops a floating body's plain UpdatePosition (see <see cref="ApplyRotation"/>), and a turn command
        /// needs no contact (MotionInterp.contact_allows_move admits TurnRight unconditionally). The client turns about
        /// the body's LOCAL up axis in whole animation-frame steps, so the bookkeeping (<see cref="PinnacleHeadTurns"/>)
        /// does the same: the head's up axis stays where its spawn rotation put it, and the turn is booked where the
        /// client lands, within half a step of facing the owner.
        ///
        /// The server's Location and physics frame take the booked rotation when the turn STARTS (no position is sent):
        /// a full object re-send during the turn (SetAttackable's UpdateObject at a phase change, or a player arriving)
        /// then carries where the head is going, not where it came from. A nod superseded after a pitched frame is put
        /// back at its rest rotation first. A dead head is left alone.
        ///
        /// No motion table, or no turn in it (content not applied yet): the previous snap, a teleport-sequenced update of
        /// the spawn rotation turned about world Z (<see cref="PinnacleFacing.FaceToward"/>).
        /// </summary>
        public void FaceOwner(PinnacleHandle h)
        {
            var wo = Get(h);
            var owner = FindOwnerOnCopy();

            if (wo == null || wo.IsDestroyed || !IsAliveHead(wo) || wo.Location == null || owner?.Location == null || !baseRotation.TryGetValue(h.Id, out var spawn))
                return;

            var from = new Vector2(wo.Location.PositionX, wo.Location.PositionY);
            var to = new Vector2(owner.Location.PositionX, owner.Location.PositionY);
            var (omegaZ, framerate) = TurnCycle(wo);

            var plan = headTurns.BeginTurn(h.Id, spawn, IsAliveHead(wo), from, to, headFaceYawOffset, omegaZ, framerate, HeadTurnSpeed);

            if (plan.Kind == PinnacleHeadTurns.TurnKind.Refused)
                return;

            if (plan.Kind == PinnacleHeadTurns.TurnKind.Snap)
            {
                // FaceToward subtracts the spawn yaw itself; an absolute yaw composed onto the spawn rotation would turn
                // the head a quarter turn past the target.
                var q = plan.Target;
                ApplyRotation(wo, q);
                return;
            }

            // a nod cut off after a pitched frame: back to its rest pose before turning
            if (plan.RestoreRest.HasValue)
                ApplyRotation(wo, plan.RestoreRest.Value);

            if (plan.Kind == PinnacleHeadTurns.TurnKind.Hold)
            {
                // already facing (within the client's resolution); a turn still in flight is stopped where it is
                if (plan.StopInFlight)
                    StopTurn(wo, plan.Target);

                return;
            }

            var generation = plan.Generation;
            var target = plan.Target;

            SetServerRotation(wo, target);

            var start = new Motion(wo, MotionCommand.Ready);
            start.SetTurnCommand(MotionCommand.TurnRight, plan.CommandSpeed);
            wo.EnqueueBroadcastMotion(start);

            var chain = new ActionChain();
            chain.AddDelaySeconds(plan.Seconds);
            chain.AddAction(landblock, () =>
            {
                // stale (a newer turn owns the head) or dead (its death motion must not be replaced by a Ready): nothing
                if (wo.IsDestroyed || !headTurns.TakeStop(h.Id, generation, IsAliveHead(wo)))
                    return;

                StopTurn(wo, target);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// The signed z omega (rad/s) of <paramref name="wo"/>'s TurnRight cycle in its motion table's default style and
        /// the framerate of the cycle's first animation (0 when it has none), or (0, 0) when it has no motion table, no
        /// such cycle, or the dats cannot be read.
        /// </summary>
        private static (float OmegaZ, float Framerate) TurnCycle(WorldObject wo)
        {
            if (wo.MotionTableId == 0)
                return (0f, 0f);

            try
            {
                var data = ACE.Server.Physics.Animation.MotionTable.GetMotionData(wo.MotionTableId, (uint)MotionCommand.TurnRight);

                if (data == null)
                    return (0f, 0f);

                return (data.Omega.Z, data.Anims.Count > 0 ? data.Anims[0].Framerate : 0f);
            }
            catch (Exception ex)
            {
                log.Warn($"[PINNACLE] motion table 0x{wo.MotionTableId:X8} turn cycle unreadable; the head snaps instead", ex);
                return (0f, 0f);
            }
        }

        /// <summary>Puts the server's Location and physics frame at <paramref name="q"/>, sending nothing.</summary>
        private static void SetServerRotation(WorldObject wo, Quaternion q)
        {
            wo.Location.Rotation = q;

            if (wo.PhysicsObj != null)
            {
                wo.PhysicsObj.Position.Frame.Orientation = q;
                wo.PhysicsObj.set_frame(wo.PhysicsObj.Position.Frame);
            }
        }

        /// <summary>Stops the client's turn (a Ready motion with no turn command) and keeps the server's rotation where the turn ends, sending no position.</summary>
        private static void StopTurn(WorldObject wo, Quaternion target)
        {
            wo.EnqueueBroadcastMotion(new Motion(wo, MotionCommand.Ready));
            SetServerRotation(wo, target);
        }

        /// <summary>
        /// The nod (<see cref="BobSteps"/>) as one action chain on the landblock, and <paramref name="then"/> as the
        /// chain's LAST action, so a cast waits for the nod to end. Nothing to nod (no head, or a dead one):
        /// <paramref name="then"/> runs at once. A turn in flight (<see cref="FaceOwner"/>) is waited out first, plus
        /// <see cref="PinnacleHeadTurns.SettleSeconds"/>, so the nod's teleport-sequenced frames never land on a body the
        /// client is still turning, and they are built from the turn's BOOKED rotation. A nod frame that is stale (a
        /// newer FaceOwner owns the head) or due on a head that has died is skipped; <paramref name="then"/> still runs.
        /// </summary>
        public void Bob(PinnacleHandle h, Action then = null)
        {
            var wo = Get(h);

            var spawn = baseRotation.TryGetValue(h.Id, out var b) ? b : Quaternion.Identity;
            var plan = wo == null || wo.IsDestroyed ? default : headTurns.BeginNod(h.Id, spawn, IsAliveHead(wo));

            if (wo == null || wo.IsDestroyed || plan.Refused)
            {
                then?.Invoke();
                return;
            }

            var rest = plan.Rest;
            var generation = plan.Generation;

            var chain = new ActionChain();

            if (plan.DelaySeconds > 0)
                chain.AddDelaySeconds(plan.DelaySeconds);

            var previous = 0.0;
            var pitchSign = NodPitchSign(headFaceYawOffset);

            for (var i = 0; i < BobSteps.Length; i++)
            {
                var (delay, pitch) = BobSteps[i];
                var isRest = i == BobSteps.Length - 1;

                if (delay > previous)
                    chain.AddDelaySeconds(delay - previous);

                previous = delay;

                var q = Quaternion.Normalize(rest * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch * pitchSign));
                chain.AddAction(landblock, () =>
                {
                    if (!wo.IsDestroyed && headTurns.TakeNodFrame(h.Id, generation, IsAliveHead(wo), isRest))
                        ApplyRotation(wo, q);
                });
            }

            // after the rest pose: the face, nod, THEN cast order of section 7 Q4
            if (then != null)
                chain.AddAction(landblock, () => then());

            chain.EnqueueChain();
        }

        /// <summary>
        /// The nod pitches about the model's local X axis, and BobSteps are authored for a face on local +Y. A face on
        /// the opposite side (yaw offset past a quarter turn) sees the same pitch lift instead of dip, so it flips.
        /// </summary>
        internal static float NodPitchSign(float faceYawOffset) => Math.Cos(faceYawOffset) < 0.0 ? -1f : 1f;

        /// <summary>
        /// Turns the object in place and tells the clients. THE UPDATE MUST ADVANCE THE TELEPORT SEQUENCE (adminMove):
        /// the client applies a remote object's UpdatePosition through CPhysicsObj::MoveOrTeleport (ported at
        /// PhysicsObj.MoveOrTeleport), which, for a teleport sequence that is not newer, applies the position only when
        /// the pack says the object is grounded (PositionFlags.IsGrounded, set by PositionPack.BuildFlags only for an
        /// OnWalkable body) and otherwise returns without applying it. The head has no gravity and floats 0.65 m above
        /// the block, so it is never OnWalkable: every plain facing and nod update was dropped, which is why the
        /// 2026-10-20 playtest saw the head never turn. A newer teleport sequence takes the SetPosition path instead,
        /// which applies the whole frame (heading, tilt and nod pitch) at once.
        /// </summary>
        private static void ApplyRotation(WorldObject wo, Quaternion q)
        {
            wo.Location.Rotation = q;

            if (wo.PhysicsObj != null)
            {
                wo.PhysicsObj.Position.Frame.Orientation = q;
                wo.PhysicsObj.set_frame(wo.PhysicsObj.Position.Frame);
            }

            wo.SendUpdatePosition(adminMove: true);
        }

        // ================================================================ casting, dispel, speech

        public void Cast(PinnacleHandle caster, uint spellId)
        {
            var wo = Get(caster);
            var owner = FindOwnerOnCopy();

            if (wo == null || wo.IsDestroyed || owner == null)
                return;

            // the launch height (PinnacleScriptData.HeadCastHeight): without it the head's 6.87 m column puts its bolts
            // against the ceiling and its arcs above it (see GraelData.HeadCastHeight)
            if (headCastHeight > 0f)
                wo.ProjectileCastHeight = headCastHeight;

            wo.TryCastSpell(new Spell(spellId), owner);
        }

        public int DispelOwnerBuffs(int count, Random rng)
        {
            var owner = FindOwnerOnCopy();

            if (owner == null)
                return 0;

            var enchants = owner.EnchantmentManager;
            var chosen = count > 0
                ? PinnacleDispel.Select(enchants.GetEnchantments_TopLayer(EnchantmentTypeFlags.Beneficial), count, rng)
                : new List<ACE.Entity.Models.PropertiesEnchantmentRegistry>();

            var names = new List<string>();

            foreach (var entry in chosen)
            {
                names.Add(new Spell(entry.SpellId).Name);
                enchants.Dispel(ACE.Server.ClassAbilities.Abilities.DispellingEdgeAbility.SelectAllLayers(entry, enchants.GetEnchantments(entry.SpellCategory)));
            }

            // the visual on every failed round, even when nothing was dispelled (pattern AdminCommands god-mode exit)
            owner.PlayParticleEffect(PlayScript.DispelAll, owner.Guid);

            if (owner.Session != null)
                foreach (var line in PinnacleDispel.FormatLines(names))
                    owner.Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Magic));

            return chosen.Count;
        }

        public void Tell(string line)
        {
            var owner = FindOwnerOnCopy();

            if (owner?.Session != null && !string.IsNullOrEmpty(line))
                owner.Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        // ================================================================ hazards (IHazardHost)

        public int StartHazard(string kind, string pattern, HazardArgs args, out string error)
        {
            if (!HazardRegionPresets.TryGet(hazardPreset, instance, out var region))
            {
                error = $"no hazard region preset '{hazardPreset}'";
                return 0;
            }

            var run = HazardManager.Start(region, kind, args, this, null, pattern, landblock, out error);

            if (run == null)
                return 0;

            runs[run.Id] = run;
            return run.Id;
        }

        public void StopHazard(int token)
        {
            if (runs.TryGetValue(token, out var run))
            {
                runs.Remove(token);
                HazardManager.Stop(run, HazardOutcome.Stopped, "pinnacle core");
            }
        }

        /// <summary>
        /// A boss fight puts players in front of its runs: no "[hazard #N]" developer text, only each mechanic's short
        /// player lines (review C12). The boss script names its own rounds.
        /// </summary>
        public bool PlayerFacing => true;

        /// <summary>ANY thread. Marshalled onto the fight's landblock; the fight never handles it on the calling thread.</summary>
        void IHazardHost.OnEnded(HazardRun run, HazardOutcome outcome)
        {
            var id = run.Id;
            var f = fight;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                runs.Remove(id);
                f?.OnHazardEnded(id, outcome);
            }));
        }

        // ================================================================ Knot bookkeeping (owner thread)

        public void ChargeEntry()
            => OnOwner(nameof(ChargeEntry), owner => KnotEncounterEvents.SpendEntry(owner, knotGuid));

        public void RefundEntry()
            => OnOwner(nameof(RefundEntry), owner => KnotEncounterEvents.RefundEntry(owner, knotGuid), KnotPendingEvents.Kind.Refund);

        public void ReportBossKilled()
            => OnOwner(nameof(ReportBossKilled), owner => KnotEncounterEvents.OnBossKilled(owner, knotGuid), KnotPendingEvents.Kind.BossKilled);

        // ================================================================ script API (Matron)

        /// <summary>
        /// The handle of a prop this world spawned (SpawnProp), looked up by its object. Any thread: the door-use hook calls it
        /// on the user's thread. False for anything that is not a live prop of this world.
        /// </summary>
        internal bool TryGetHandle(WorldObject wo, out PinnacleHandle h)
        {
            if (wo != null && propHandles.TryGetValue(wo.Guid.Full, out var id))
            {
                h = new PinnacleHandle(id);
                return true;
            }

            h = PinnacleHandle.None;
            return false;
        }

        /// <summary>
        /// A fight-scoped prop: the non-creature path of <see cref="Spawn"/> (dynamic guid, TimeToRot -1, no tier, no
        /// corpse, no kill-reward strip) plus HazardContext.Spawn's stamping (scale, translucency, effect script), all
        /// BEFORE EnterWorld so the create packet already carries them. A Door is ALWAYS sealed shut (Door.SealedShut):
        /// ActOnUse then refuses a creature's collision use and never schedules the Reset that would reopen or relock it, so
        /// only the script (SetDoorOpen) and the use hook (PinnacleFightManager.TryHandlePropUse) move it.
        /// </summary>
        public PinnacleHandle SpawnProp(PinnacleProp prop)
        {
            if (prop == null)
                return PinnacleHandle.None;

            WorldObject wo = null;

            try
            {
                wo = WorldObjectFactory.CreateNewWorldObject(prop.Wcid);

                if (wo == null)
                {
                    log.Warn($"[PINNACLE] prop wcid {prop.Wcid} did not resolve to a weenie");
                    return PinnacleHandle.None;
                }

                // Dynamic guid, no generator: the heartbeat would rot it. The fight's End (or the copy's unload) ends it.
                wo.TimeToRot = -1;

                if (wo is Door door)
                {
                    door.SealedShut = true;

                    if (prop.DoorOpen == true)
                        door.ForceOpenUnlocked();
                }

                var at = prop.At;
                wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, instance);

                if (float.IsFinite(prop.Scale) && prop.Scale > 0f && Math.Abs(prop.Scale - 1.0f) > 1e-6f)
                    wo.ObjScale = (wo.ObjScale ?? 1.0f) * prop.Scale;

                if (prop.Script != 0)
                    wo.VisualEffectScript = prop.Script;

                // Unset, not an explicit 0, below the wire threshold (precedent HazardContext.Spawn).
                if (prop.Translucency.HasValue && float.IsFinite(prop.Translucency.Value) && prop.Translucency.Value >= 0.001f)
                    wo.Translucency = Math.Min(1f, prop.Translucency.Value);

                if (!wo.EnterWorld())
                {
                    wo.Destroy();
                    return PinnacleHandle.None;
                }

                var h = new PinnacleHandle(nextHandle++);
                objects[h.Id] = wo;
                baseRotation[h.Id] = new Quaternion(at.QX, at.QY, at.QZ, at.QW);
                propHandles[wo.Guid.Full] = h.Id;
                return h;
            }
            catch (Exception ex)
            {
                log.Error($"[PINNACLE] spawn of prop wcid {prop.Wcid} threw", ex);

                try { wo?.Destroy(); } catch (Exception) { /* already failing */ }

                return PinnacleHandle.None;
            }
        }

        /// <summary>Opens or closes a prop door through the door's own Open/Close, which do not consult SealedShut (only ActOnUse does).</summary>
        public void SetDoorOpen(PinnacleHandle h, bool open)
        {
            if (!(Get(h) is Door door) || door.IsDestroyed)
                return;

            if (open)
                door.Open();
            else
                door.Close();
        }

        /// <summary>
        /// Broadcasts one motion. With <paramref name="hold"/> it also becomes the creature's CurrentMotionState, which is
        /// what every later UpdateObject carries (a player who arrives mid-hold sees the held pose) and what nothing on a
        /// peaceful, asleep creature overwrites (Monster_Tick returns before it touches motion while not awake). A held
        /// pose ends when the creature is engaged: its first awake tick re-enters the attack stance.
        /// </summary>
        public void PlayMotion(PinnacleHandle h, MotionStance stance, MotionCommand motion, bool hold)
        {
            var wo = Get(h);

            if (wo == null || wo.IsDestroyed || wo.PhysicsObj == null)
                return;

            var m = new Motion(stance, motion);
            wo.EnqueueBroadcastMotion(m);

            if (hold)
                wo.CurrentMotionState = m;
        }

        public void PlayEffect(PinnacleHandle h, PlayScript script)
        {
            var wo = Get(h);

            if (wo != null && !wo.IsDestroyed)
                ACE.Server.MlDigsite.MlDigsiteProps.PlayScriptOn(wo, script);
        }

        /// <summary>
        /// Walks a creature to a point through the creature's own MoveTo (it runs its own tick chain, so it does not need
        /// the monster AI awake, and Monster_Tick returns early for a creature that is not awake). The caller watches
        /// arrival through <see cref="TryGetPosition"/>: each step syncs Location. A walk the physics cannot make progress on
        /// cancels itself after five stalled steps; the script's timeout covers that.
        /// </summary>
        public void MoveTo(PinnacleHandle h, PinnaclePoint to)
        {
            if (!(Get(h) is Creature c) || c.IsDestroyed || c.IsDead || c.PhysicsObj == null)
                return;

            c.MoveTo(ToPosition(to), c.GetRunRate(), true);
        }

        /// <summary>
        /// The emote system's in-place turn (Creature.TurnTo(Position)): a TurnToHeading broadcast to the rotation's heading,
        /// with Location and the physics frame taking that rotation when the motion table's estimated delay runs out.
        /// Returns that delay; 0 for anything that is not a live creature.
        /// </summary>
        public double TurnTo(PinnacleHandle h, PinnaclePoint facing)
        {
            if (!(Get(h) is Creature c) || c.IsDestroyed || c.IsDead || c.PhysicsObj == null || c.Location == null)
                return 0.0;

            return c.TurnTo(ToPosition(facing));
        }

        public bool TryGetPosition(PinnacleHandle h, out PinnaclePoint at)
            => TryPointOf(Get(h), out at);

        public bool TryGetOwnerPosition(out PinnaclePoint at)
            => TryPointOf(FindOwnerOnCopy(), out at);

        private static bool TryPointOf(WorldObject wo, out PinnaclePoint at)
        {
            var loc = wo?.Location;

            if (loc == null || wo.IsDestroyed)
            {
                at = default;
                return false;
            }

            at = new PinnaclePoint(loc.Cell, loc.PositionX, loc.PositionY, loc.PositionZ, QW: loc.RotationW, QX: loc.RotationX, QY: loc.RotationY, QZ: loc.RotationZ);
            return true;
        }

        /// <summary>
        /// Hostile again, on the owner. SetPeaceful(false) restores the tolerance recorded at its first SetPeaceful(true),
        /// which for a creature authored NoAttack (the Matron weenie) is NoAttack itself, so NoAttack is cleared AFTER it:
        /// whatever was recorded, the creature leaves here hostile. A scripted walk still in flight is cancelled first, and
        /// the creature is woken with the owner as its target.
        /// </summary>
        public void Engage(PinnacleHandle h)
        {
            if (!(Get(h) is Creature c) || c.IsDestroyed || c.IsDead)
                return;

            SetPeaceful(h, false);
            c.Tolerance &= ~Tolerance.NoAttack;

            var move = c.PhysicsObj?.MovementManager?.MoveToManager;

            if (move != null && c.PhysicsObj.IsMovingTo())
                move.CancelMoveTo(WeenieError.ActionCancelled);

            var owner = FindOwnerOnCopy();

            if (owner == null)
                return;

            c.AttackTarget = owner;
            c.WakeUp(false);
        }

        public void SetHealthFloor(PinnacleHandle h, double? fraction)
        {
            if (!(Get(h) is Creature c) || c.IsDestroyed)
                return;

            c.P_HealthFloor = fraction.HasValue ? ACE.Server.SpeedCourses.SpeedBossPhaseRules.FloorFor(c.Health.MaxValue, fraction.Value) : 0u;
        }

        public bool IsAtHealthFloor(PinnacleHandle h)
            => Get(h) is Creature c && !c.IsDestroyed && c.P_HealthFloor > 0 && c.Health.Current <= c.P_HealthFloor;

        /// <summary>
        /// Kills a creature outright. The floor and the immunity go first: Creature.Die writes UpdateVital(Health, 0), and
        /// UpdateVital raises a write to the floor, so a creature still at a floor would never reach 0 health and never
        /// read as dead. Smite runs Die directly (no damage roll), so immunity would not block it; it is cleared so the
        /// death state is not left carrying it.
        /// </summary>
        public void Slay(PinnacleHandle h)
        {
            if (!(Get(h) is Creature c) || c.IsDestroyed || c.IsDead)
                return;

            c.P_HealthFloor = 0;
            c.P_DigsiteImmune = false;
            c.Smite(c);
        }

        public bool ControlHazard(int token, string key, double value)
            => runs.TryGetValue(token, out var run) && run.Control(key, value);

        public bool TryGetHazardLevel(int token, out double level)
        {
            if (runs.TryGetValue(token, out var run) && run.Mechanic is IHazardLevelSource source)
                return source.TryGetLevel(out level);

            level = 0;
            return false;
        }
        public void ReportAttemptLost()
            => OnOwner(nameof(ReportAttemptLost), owner => KnotEncounterEvents.OnAttemptLost(owner, knotGuid), KnotPendingEvents.Kind.AttemptLost);

        /// <summary>
        /// The attempt ended: writes its attempt_end telemetry row. Touches nothing on the landblock and never throws
        /// (KnotTelemetry swallows its own failures), so it is safe from any end path, including a copy that is gone.
        /// </summary>
        public void ReportEnded(PinnacleAttemptEnd e) => KnotTelemetry.RecordAttemptEnd(e, attempt);

        /// <summary>
        /// A delivered, charged, non-admin-jumped clear: records it as the owner's best Knot clear when it beats the stored
        /// one (KnotClearRecords.Record). The owner is present at delivery, so there is no offline deferral.
        /// </summary>
        public void ReportCleared(PinnacleClear clear)
        {
            if (clear == null)
                return;

            var key = attempt?.PinnacleKey;
            var centi = KnotClearRecords.ToCentiseconds(clear.Seconds);

            OnOwner(nameof(ReportCleared), owner =>
            {
                if (KnotClearRecords.Record(owner, key, clear.KnotLevel, centi))
                    owner.RushNextPlayerSave(5);
            });
        }

        /// <summary>
        /// Looks the owner up by guid at call time and runs <paramref name="act"/> on their thread. An offline owner:
        /// with <paramref name="whenOffline"/> the event is held in <see cref="KnotPendingEvents"/> and applied when they
        /// return (in memory only, lost on a restart); without one (the charge) it is logged and skipped.
        /// </summary>
        private void OnOwner(string what, Action<Player> act, KnotPendingEvents.Kind? whenOffline = null)
        {
            var owner = PlayerManager.GetOnlinePlayer(ownerGuid);

            if (DeferIfOffline(owner != null, ownerGuid, knotGuid, whenOffline))
                return;

            if (owner == null)
            {
                log.Info($"[PINNACLE] {what}: owner 0x{ownerGuid:X8} is offline; skipped");
                return;
            }

            owner.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    act(owner);
                }
                catch (Exception ex)
                {
                    log.Error($"[PINNACLE] {what} threw for owner 0x{ownerGuid:X8}", ex);
                }
            }));
        }

        /// <summary>
        /// The offline half of <see cref="OnOwner"/>: an offline owner with a deferrable event has it recorded in
        /// <see cref="KnotPendingEvents"/>; returns true when it did. Online, or nothing to defer: false.
        ///
        /// THE LOGIN RACE (review F1): the owner may come online between the offline read and the Record, after their
        /// login check (KnotPendingEvents.ScheduleApplyOnLogin) already found nothing pending. So the owner is looked up
        /// AGAIN after recording; if they are online now, their events are taken and handed to <paramref name="deliverNow"/>
        /// (by default KnotPendingEvents.DeliverToOnline, which queues them on the owner's own chain).
        /// <paramref name="isOnlineNow"/> and <paramref name="deliverNow"/> are test seams; null means the live ones.
        /// </summary>
        internal static bool DeferIfOffline(bool ownerOnline, uint ownerGuid, uint knotGuid, KnotPendingEvents.Kind? whenOffline,
            Func<uint, bool> isOnlineNow = null, Action<uint, IReadOnlyList<(uint KnotGuid, KnotPendingEvents.Kind Kind)>> deliverNow = null)
        {
            if (ownerOnline || !whenOffline.HasValue)
                return false;

            KnotPendingEvents.Record(ownerGuid, knotGuid, whenOffline.Value);

            if ((isOnlineNow ?? (g => PlayerManager.GetOnlinePlayer(g) != null))(ownerGuid))
                (deliverNow ?? KnotPendingEvents.DeliverToOnline)(ownerGuid, KnotPendingEvents.Take(ownerGuid));

            return true;
        }

        // ================================================================ reward and release

        public bool TryDeliverCache()
        {
            var owner = FindOwnerOnCopy();

            if (owner == null)
                return false;

            var rewards = PinnacleRewards.Roll(knotLevel, owner, rewardTable);

            // The core retries every tick: the placement-failure warning is logged once per fight, not per retry.
            var placed = cachePoint.HasValue
                ? PinnacleCache.TryPlaceAt(owner, landblock, ToPosition(cachePoint.Value), rewards, logFailure: !cachePlacementFailureLogged)
                : PinnacleCache.TryPlace(owner, landblock, instance, rewards, logFailure: !cachePlacementFailureLogged);

            if (placed)
                return true;

            cachePlacementFailureLogged = true;

            foreach (var item in rewards)
                item?.Destroy();

            return false;
        }

        /// <summary>This copy's position of a landblock-local point.</summary>
        private Position ToPosition(PinnaclePoint p) => new Position(p.Cell, p.X, p.Y, p.Z, p.QX, p.QY, p.QZ, p.QW, instance);

        /// <summary>The chat line the owner sees when the return portal stands at them.</summary>
        public const string ReturnPortalLine = "A return portal opens beside you.";

        /// <summary>The chat line the owner sees when the return portal stands at the script's ReturnPortalPoint.</summary>
        public const string ReturnPortalAtPointLine = "A return portal opens where the head fell.";

        /// <summary>The return portal's in-game name (the weenie's own is the Thread's "Thread Exit").</summary>
        public const string ReturnPortalName = "Knot Exit";

        /// <summary>
        /// The Thread's click-only summoned exit (ThreadDungeonRewardSpawner.SummonedExitWcid, 1003604), stood at the
        /// script's ReturnPortalPoint (else at the owner's own position) in this copy, renamed, TimeToRot -1 before EnterWorld (no generator: the heartbeat would rot it;
        /// the copy's unload ends it with everything else in it). Click-only because 1003604 clears ReportCollisions, so it
        /// cannot fire on the owner it materialises on. Its use routes to the owner's EphemeralRealmExitTo stamp, which
        /// PinnacleEncounter.Teleport wrote on every way in (PortalExitInstance, Portal.ActOnUse). It carries NO
        /// ThreadDungeonRunId: nothing of a Thread run keys on it. False (logged) when the owner is not on this copy or
        /// the portal cannot be made or entered.
        /// </summary>
        public bool SummonReturnPortal()
        {
            var owner = FindOwnerOnCopy();

            if (owner?.Location == null || owner.Location.Instance != instance)
            {
                log.Warn($"[PINNACLE] return portal: owner 0x{ownerGuid:X8} is not on copy 0x{instance:X8}; not summoned");
                return false;
            }

            WorldObject wo = null;

            try
            {
                wo = WorldObjectFactory.CreateNewWorldObject(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.SummonedExitWcid);

                if (!(wo is Portal portal))
                {
                    log.Error($"[PINNACLE] return portal wcid {ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.SummonedExitWcid} did not resolve to a Portal; not summoned");
                    wo?.Destroy();
                    return false;
                }

                portal.Location = returnPortalPoint.HasValue ? ToPosition(returnPortalPoint.Value) : new Position(owner.Location, instance);
                portal.TimeToRot = -1;

                // the name, and the appraisal line Portal.SetEphemeralValues built from the weenie's name at construction
                portal.Name = ReturnPortalName;
                portal.UpdatePortalDestination(portal.Destination);

                if (!portal.EnterWorld())
                {
                    log.Error($"[PINNACLE] return portal failed to enter the world at {portal.Location?.ToLOCString()}");
                    portal.Destroy();
                    return false;
                }

                log.Info($"[PINNACLE] return portal 0x{portal.Guid.Full:X8} summoned for {owner.Name} at {portal.Location.ToLOCString()}");
                owner.Session?.Network.EnqueueSend(new GameMessageSystemChat(returnPortalPoint.HasValue ? ReturnPortalAtPointLine : ReturnPortalLine, ChatMessageType.Broadcast));
                return true;
            }
            catch (Exception ex)
            {
                log.Error("[PINNACLE] return portal summon threw", ex);

                try { wo?.Destroy(); } catch (Exception) { /* already failing */ }

                return false;
            }
        }

        // ================================================================ plate floor

        /// <summary>
        /// Every player on this copy who is under the plate floor (<see cref="PinnaclePlateLift.ShouldLift"/>) is put straight
        /// back on top: same x and y, z = the floor's LiftZ, the cell resolved for that point as every dungeon teleport
        /// resolves it (<see cref="WorldObject.AdjustDungeonCells"/>).
        ///
        /// THREAD. The fight tick is an action on this landblock's own queue (PinnacleFightManager.QueueTick), which is what
        /// <see cref="Landblock.PlayersOnLandblockThread"/> requires; that queue runs in LandblockManager.Tick's multi-threaded
        /// work phase, after the physics phase, with this landblock on one thread. The owner is already touched from here
        /// (DispelOwnerBuffs, Cast). The move stays inside this landblock, so no other landblock's cells are touched (the
        /// cross-landblock case is why Player.EnqueuePhysicsResync routes through the world queue; this one does not need to).
        /// </summary>
        public void LiftPlayersUnderPlateFloor(double now)
        {
            if (plateFloor == null)
                return;

            var players = landblock.PlayersOnLandblockThread;

            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                var loc = p?.Location;

                if (loc == null || loc.Instance != instance || p.Session == null || plateLiftGivenUp.Contains(p.Guid.Full))
                    continue;

                if (!plateLifts.TryGetValue(p.Guid.Full, out var lifts))
                {
                    lifts = new List<double>();
                    plateLifts[p.Guid.Full] = lifts;
                }

                // only the escalation window is kept (it is far longer than the 2 s rate limit, so the last one survives)
                lifts.RemoveAll(t => now - t >= PinnaclePlateLift.EscalationWindowSeconds);

                double? last = lifts.Count > 0 ? lifts[lifts.Count - 1] : (double?)null;

                if (!PinnaclePlateLift.ShouldLift(plateFloor, loc.PositionX, loc.PositionY, loc.PositionZ, p.Teleporting, p.IsDead, last, now))
                    continue;

                if (!plateTeleports.TryGetValue(p.Guid.Full, out var teleports))
                {
                    teleports = new List<double>();
                    plateTeleports[p.Guid.Full] = teleports;
                }

                teleports.RemoveAll(t => now - t >= PinnaclePlateLift.TeleportCapWindowSeconds);

                var action = PinnaclePlateLift.Decide(lifts, teleports, now);

                if (action == PinnaclePlateLift.LiftAction.GiveUp)
                {
                    // teleports have not taken either: logged once, and this player is left alone for the rest of the fight
                    plateLiftGivenUp.Add(p.Guid.Full);
                    log.Error($"[PINNACLE] gave up lifting {p.Name} at {loc.ToLOCString()}: {PinnaclePlateLift.MaxTeleportsPerWindow} teleports in {PinnaclePlateLift.TeleportCapWindowSeconds:0} s did not take");
                    continue;
                }

                // stamped before the move, so a move that throws is still rate limited and still counts toward the escalation
                lifts.Add(now);

                if (action == PinnaclePlateLift.LiftAction.Teleport)
                    teleports.Add(now);

                try
                {
                    var from = loc.ToLOCString();

                    if (action == PinnaclePlateLift.LiftAction.Teleport)
                    {
                        // the forced position has not taken twice in the window: the client may be ignoring it while the
                        // server drops its under-plate reports, which leaves Location on the plates and this check silent.
                        // TeleportZ, not LiftZ: a teleport re-runs the client's placement, which steps a body at LiftZ down
                        // onto the block top when the plates are not loaded (the bug itself).
                        var to = LiftTarget(loc, plateFloor.TeleportZ);
                        var who = p;

                        log.Warn($"[PINNACLE] lift did not take for {p.Name}, teleporting onto the plates");

                        // WorldManager.ThreadSafeTeleport's own route (the world queue, then Player.Teleport, which handles
                        // fog itself; no follow-up is needed here), re-checked when it runs: the player may have died,
                        // logged out, started another teleport or left the copy since this tick.
                        WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                        {
                            if (who.Teleporting || who.IsDead || who.Session == null || who.Location == null || who.Location.Instance != instance)
                                return;

                            who.Teleport(to, source: TeleportSource.Other);
                        }));
                    }
                    else if (LiftTo(p, LiftTarget(loc, plateFloor.LiftZ)))
                        log.Info($"[PINNACLE] lifted {p.Name} from {from} onto the plates");
                    else
                        log.Warn($"[PINNACLE] could not lift {p.Name} from {from}: no cell loaded for the lift target; left in place");
                }
                catch (Exception ex)
                {
                    log.Error($"[PINNACLE] lifting {p.Name} onto the plates threw", ex);
                }
            }
        }

        /// <summary>The lift destination: <paramref name="from"/> at z <paramref name="z"/>, its dungeon cell re-resolved for the new point.</summary>
        internal static Position LiftTarget(Position from, float z)
        {
            var to = new Position(from);
            to.PositionZ = z;
            WorldObject.AdjustDungeonCells(to);
            return to;
        }

        /// <summary>
        /// A server-forced move of a player within its landblock, NOT a teleport. Physics copy first, exactly as
        /// Player.EnqueuePhysicsResync re-anchors it (set_request_pos, then set_current_pos, which changes the cell), so a
        /// FastTick player's server simulation starts from the new point; then Location, a new ObjectForcePosition sequence
        /// and the position, as the z-hack rubber-band (Player_Tick UpdatePlayerPosition) and the blink rubber-band
        /// (HandleBlinkDetection) send theirs: the new force-position sequence is what makes the client take the server's
        /// position for its own body. False, with NOTHING changed or sent, when the player has a physics copy and the
        /// target's cell is not loaded: moving Location alone would split it from the physics copy.
        /// </summary>
        private static bool LiftTo(Player p, Position to)
        {
            if (p.PhysicsObj != null)
            {
                var cell = ACE.Server.Physics.Common.LScape.get_landcell(to.Cell, to.Instance);

                if (cell == null)
                    return false;

                p.PhysicsObj.set_request_pos(to.Pos, to.Rotation, cell, to.LandblockId.Raw, to.Instance);
                p.PhysicsObj.set_current_pos(p.PhysicsObj.RequestPos, to.Instance);
            }

            p.Location = to;
            p.Sequences.GetNextSequence(ACE.Server.Network.Sequence.SequenceType.ObjectForcePosition);
            p.SendUpdatePosition();
            return true;
        }

        public void ReleaseInstance(bool ownerInside)
        {
            PinnacleFightManager.ReleaseHold(landblock);

            if (!ownerInside)
                LandblockManager.AddToDestructionQueue(landblock);
        }

        public void Log(string line) => log.Info($"[PINNACLE] {line}");

        public void LogError(string line) => log.Error($"[PINNACLE] {line}");
    }
}
