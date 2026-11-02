using System;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// What a boss script may do. Implemented by PinnacleFight; faked in tests. A script touches ONLY this
    /// interface: no WorldObject, PropertyManager, DateTime or new Random.
    ///
    /// Every call is made on the fight thread, from inside <see cref="IPinnacleBossScript"/> callbacks.
    /// </summary>
    public interface IPinnacleFightPort
    {
        /// <summary>Fight clock, seconds. FROZEN while Held.</summary>
        double Now { get; }

        /// <summary>The fight's seeded generator. The only randomness a script may use.</summary>
        Random Rng { get; }

        int Phase { get; }

        /// <summary>The Knot's level (PinnacleFight.KnotLevel); 0 or below means the fight is unscaled.</summary>
        int Level { get; }

        /// <summary>Persistent; spawned by the core from Data; never cleared by a phase change.</summary>
        PinnacleHandle Head { get; }

        /// <summary>
        /// Phase-scoped: destroyed by the core on the next phase change. Creature adds carry the inert "immune"
        /// record so <see cref="SetImmune"/> can switch it later. A spawn that fails is retried once by the core,
        /// at once, inside this call; if the retry fails too this returns None and the script receives
        /// AddDied(tag, SpawnFailed) after the current callback returns. <paramref name="tier"/> picks the add's
        /// Knot-level tier scaling.
        /// </summary>
        PinnacleHandle SpawnAdd(string tag, uint wcid, PinnaclePoint at, PinnacleTier tier);

        bool IsAlive(PinnacleHandle h);

        void SetImmune(PinnacleHandle h, bool immune);

        void SetAttackable(PinnacleHandle h, bool attackable);

        /// <summary>Turns the caster to face the owner, nods it, then casts <paramref name="spellId"/> at the owner.</summary>
        void CastAtOwner(PinnacleHandle caster, uint spellId);

        /// <summary>
        /// Starts a hazard run under <paramref name="tag"/>. origin fills cx/cy from that object's position. The
        /// script sees exactly one HazardEnded(tag) per call, carrying the FINAL outcome after the core's one retry.
        /// </summary>
        void StartHazard(string tag, string kind, string pattern, HazardArgs args, PinnacleHandle origin = default);

        /// <summary>Stops every hazard run of this fight. No HazardEnded is delivered for runs stopped this way.</summary>
        void StopHazards();

        /// <summary>
        /// Stops and forgets every run of this fight started under <paramref name="tag"/> (and drops its pending retry).
        /// No HazardEnded is delivered for a run stopped this way; one the core had ALREADY queued still arrives.
        /// </summary>
        void StopHazard(string tag);

        /// <summary>Fires TimerFired(tag) after <paramref name="seconds"/> of FIGHT clock. Re-setting a tag replaces it.</summary>
        void SetTimer(string tag, double seconds);

        void CancelTimer(string tag);

        /// <summary>Strips up to <paramref name="count"/> random beneficial enchantments from the owner; returns how many.</summary>
        int DispelOwnerBuffs(int count);

        void Say(string line);

        /// <summary>
        /// Fight-scoped: NOT an add. Only the fight's End (or <see cref="DestroyProp"/>) destroys it; a phase change
        /// leaves it, a Win leaves it, it is never reported dead (no AddDied) and the hold never pacifies or clamps it.
        /// Retried once like <see cref="SpawnAdd"/>; None when both attempts fail (nothing is raised).
        /// </summary>
        PinnacleHandle SpawnProp(string tag, PinnacleProp prop);

        /// <summary>Destroys a prop this fight spawned and forgets it; any other handle is ignored.</summary>
        void DestroyProp(PinnacleHandle h);

        void SetDoorOpen(PinnacleHandle h, bool open);

        /// <summary>Plays a motion on a creature; <paramref name="hold"/> keeps it as the creature's resting motion.</summary>
        void PlayMotion(PinnacleHandle h, MotionStance stance, MotionCommand motion, bool hold);

        void PlayEffect(PinnacleHandle h, PlayScript script);

        /// <summary>Walks a creature to a point (the live world uses its run rate).</summary>
        void MoveTo(PinnacleHandle h, PinnaclePoint to);

        /// <summary>
        /// Turns a creature in place to the heading of <paramref name="facing"/>'s rotation (its position is ignored) and
        /// returns how many seconds the turn takes, 0 when nothing turns (no such creature, or the fight has ended).
        /// </summary>
        double TurnTo(PinnacleHandle h, PinnaclePoint facing);

        bool TryGetPosition(PinnacleHandle h, out PinnaclePoint at);

        bool TryGetOwnerPosition(out PinnaclePoint at);

        /// <summary>True: the creature stops fighting and ignores players; false: its authored temperament returns.</summary>
        void SetPeaceful(PinnacleHandle h, bool peaceful);

        /// <summary>Makes a creature hostile and sets it on the owner.</summary>
        void Engage(PinnacleHandle h);

        /// <summary>Pins the creature's health floor to this fraction of its max; null clears it; 0.0 = 1 HP.</summary>
        void SetHealthFloor(PinnacleHandle h, double? fraction);

        bool IsAtHealthFloor(PinnacleHandle h);

        /// <summary>Kills a creature outright (floor and immunity cleared first).</summary>
        void Slay(PinnacleHandle h);

        /// <summary>Sends a control key to the first live run started under <paramref name="tag"/>; false when there is none.</summary>
        bool ControlHazard(string tag, string key, double value);

        /// <summary>Reads the surface level of the first live run started under <paramref name="tag"/>; false when there is none or it has no level.</summary>
        bool TryGetHazardLevel(string tag, out double level);

        /// <summary>Applied after the current event; while Held, applied at resume as Enter(next).</summary>
        void AdvancePhase();

        void Win();
    }
}
