using System;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// Core -> world. Live: LivePinnacleWorld. Tests: FakePinnacleWorld. Called on the fight thread only.
    /// </summary>
    public interface IPinnacleWorld
    {
        /// <summary>The fight's copy is still the registered, loaded instance.</summary>
        bool IsLive { get; }

        /// <summary>The owner is on this copy and alive.</summary>
        bool OwnerPresent { get; }

        /// <summary>
        /// The owner is present AND out of portal space (the client has finished the teleport and can act). A held fight
        /// never resumes before this, so a hazard cannot hit a player who cannot move yet.
        /// </summary>
        bool OwnerSettled { get; }

        /// <summary>The owner has a live session (anywhere). False while logged out.</summary>
        bool OwnerOnline { get; }

        /// <summary>The owner is online, alive and NOT on this copy (walked out, recalled, portalled, or respawned elsewhere).</summary>
        bool OwnerAliveElsewhere { get; }

        /// <summary>
        /// Spawns <paramref name="wcid"/> at <paramref name="at"/>; None on failure. Creatures are scaled at spawn
        /// to the fight's Knot level (PinnacleFight.KnotLevel) by the live world, with the Threads level/stat
        /// scaling, and grant no XP or luminance on death. <paramref name="withImmuneRecord"/> attaches the inert
        /// "immune" record before EnterWorld. <paramref name="tier"/> picks the creature's tier scaling
        /// (PinnacleScaling.Apply); a non-creature ignores it. A creature leaves no corpse unless <paramref name="leavesCorpse"/>
        /// (PinnacleScriptData.HeadLeavesCorpse, for the head only); either way it carries no loot of its own.
        /// </summary>
        PinnacleHandle Spawn(uint wcid, PinnaclePoint at, bool withImmuneRecord, PinnacleTier tier, bool leavesCorpse = false);

        bool IsAlive(PinnacleHandle h);

        void Destroy(PinnacleHandle h);

        bool TryGetXY(PinnacleHandle h, out float x, out float y);

        /// <summary>Switches the spell-proof immunity; the live world plays a shield effect on each real toggle.</summary>
        void SetImmune(PinnacleHandle h, bool immune);

        void SetAttackable(PinnacleHandle h, bool attackable);

        /// <summary>
        /// True: the creature stops fighting (drops its target and ignores players until restored). False: its own
        /// authored temperament comes back. Used while the fight is Held, so a returning owner is not attacked.
        /// </summary>
        void SetPeaceful(PinnacleHandle h, bool peaceful);

        int GetHealth(PinnacleHandle h);

        void ClampHealth(PinnacleHandle h, int max);

        /// <summary>
        /// Turns <paramref name="h"/> to face the owner. The live world lets the client animate the turn when the body's
        /// motion table has one (about its own up axis, booked where the client lands), else snaps it by a
        /// teleport-sequenced update (yaw toward the owner composed with its spawn tilt). A dead head is not turned.
        /// </summary>
        void FaceOwner(PinnacleHandle h);

        /// <summary>
        /// Best effort: a short pitch dip and return on the landblock action queue, then <paramref name="then"/> on the
        /// fight thread once the nod has ended (at once when there is nothing to nod, or the head is dead). The live world
        /// waits out a turn in flight first (its remaining time plus 0.1 s), then nods over 0.4 s.
        /// </summary>
        void Bob(PinnacleHandle h, Action then = null);

        void Cast(PinnacleHandle caster, uint spellId);

        /// <summary>Token &gt; 0, 0 = refused (with <paramref name="error"/>).</summary>
        int StartHazard(string kind, string pattern, HazardArgs args, out string error);

        void StopHazard(int token);

        int DispelOwnerBuffs(int count, Random rng);

        void Tell(string line);

        void ChargeEntry();

        /// <summary>Gives back the entry <see cref="ChargeEntry"/> spent (a won fight whose cache was never delivered).</summary>
        void RefundEntry();

        /// <summary>False: retry next tick.</summary>
        bool TryDeliverCache();

        /// <summary>
        /// The click-to-use return portal (owner ruling 2026-10-20), stood at the owner's position in the copy once the
        /// cache is delivered; the owner must use it, it is never walked through. Called once per fight, after the
        /// delivery has been latched. False (logged) when it could not be summoned; the caller ignores the result for
        /// everything but its log, and a throw never reaches the delivery.
        /// </summary>
        bool SummonReturnPortal();

        void ReportBossKilled();

        // ---- script API: props, motion, movement, health floor, hazard steering ----

        /// <summary>Spawns a non-creature (or creature) prop at its point with its scale, translucency and effect script stamped before it enters the world; None on failure.</summary>
        PinnacleHandle SpawnProp(PinnacleProp prop);

        void SetDoorOpen(PinnacleHandle h, bool open);

        void PlayMotion(PinnacleHandle h, MotionStance stance, MotionCommand motion, bool hold);

        void PlayEffect(PinnacleHandle h, PlayScript script);

        void MoveTo(PinnacleHandle h, PinnaclePoint to);

        /// <summary>
        /// Turns a creature in place to the heading of <paramref name="facing"/>'s rotation (only the rotation is read) and
        /// returns the turn's estimated seconds, 0 when nothing turns. The live world uses Creature.TurnTo(Position), the
        /// emote system's turn: a TurnToHeading broadcast, with the server rotation set when the delay runs out.
        /// </summary>
        double TurnTo(PinnacleHandle h, PinnaclePoint facing);

        bool TryGetPosition(PinnacleHandle h, out PinnaclePoint at);

        bool TryGetOwnerPosition(out PinnaclePoint at);

        /// <summary>Hostile again (SetPeaceful(h, false)) and set on the owner.</summary>
        void Engage(PinnacleHandle h);

        void SetHealthFloor(PinnacleHandle h, double? fraction);

        bool IsAtHealthFloor(PinnacleHandle h);

        void Slay(PinnacleHandle h);

        bool ControlHazard(int token, string key, double value);

        bool TryGetHazardLevel(int token, out double level);

        void ReportAttemptLost();

        /// <summary>
        /// The attempt has ended, exactly once, after every cleanup step (PinnacleFight.End's finally). Telemetry only:
        /// a default no-op so a world that does not record anything needs nothing, and the core calls it inside its own
        /// try/catch so a throw never reaches the cleanup.
        /// </summary>
        void ReportEnded(PinnacleAttemptEnd e) { }

        /// <summary>
        /// A charged, non-admin-jumped win whose cache was just delivered (the Knot leaderboard's clear moment). Called once,
        /// after the return portal, inside the core's own try/catch. A default no-op so a world that records nothing needs nothing.
        /// </summary>
        void ReportCleared(PinnacleClear clear) { }

        /// <summary>
        /// Every live tick, in every state: puts any player on the copy who is under the script's plate floor
        /// (PinnacleScriptData.PlateFloor, PinnaclePlateLift) back on top of it. <paramref name="now"/> is the tick's
        /// monotonic clock (the per-player rate limit). A default no-op, so a world with no plate floor needs nothing; the
        /// core calls it inside its own try/catch, so a throw never ends the fight.
        /// </summary>
        void LiftPlayersUnderPlateFloor(double now) { }

        void ReleaseInstance(bool ownerInside);

        void Log(string line);

        /// <summary>
        /// A defect the fight survives but must not hide (a spawn that failed for good and was reported dead to keep
        /// the fight moving). The live world logs it at ERROR; <see cref="Log"/> is INFO.
        /// </summary>
        void LogError(string line);
    }
}
