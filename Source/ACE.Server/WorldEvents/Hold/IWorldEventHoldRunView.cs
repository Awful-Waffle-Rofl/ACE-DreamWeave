using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>
    /// The read side of one Hold run, as every engine hook and per-creature path sees it (through WorldEventHoldTag.Run).
    ///
    /// THREADING: EVERY member is safe to call from ANY thread - the world thread, any landblock thread, a command thread.
    /// Hooks run on the landblock thread of the creature being hit, healed or moved, while the run itself is written on the
    /// world thread, so nothing here may assume which thread it is on. The implementation (WorldEventHoldRun) keeps this with
    /// volatile fields for single values, immutable arrays swapped atomically for lists, and a private lock for the small
    /// per-player and per-boss records. Nothing here ever writes a creature field: a caller that needs a creature changed
    /// enqueues the change on that creature's landblock.
    /// </summary>
    public interface IWorldEventHoldRunView
    {
        /// <summary>The run's id (WorldEvent.RunId). Fixed at construction.</summary>
        uint RunId { get; }

        /// <summary>True while the run is Active and has not finished. Volatile.</summary>
        bool IsActive { get; }

        /// <summary>The number of the wave in progress (0 before the first). The per-wave heal budget key. Volatile.</summary>
        int WaveNumber { get; }

        /// <summary>
        /// The run's current tuning: an immutable snapshot, swapped whole (volatile). Never null. Read it ONCE per hit into a
        /// local and take every value, the audience included (Tuning.Participants), from that one snapshot.
        /// </summary>
        WorldEventHoldTuning Tuning { get; }

        /// <summary>
        /// The latest audience sample (at least 1). Volatile. For display and logs only: PER-HIT MATHS MUST USE
        /// Tuning.Participants, never this. The two are published separately (this one on every re-sample, the tuning at
        /// its next rebuild, up to a second later), so mixing this count with a tuning value can pair numbers from two
        /// different moments.
        /// </summary>
        int ParticipantCount { get; }

        /// <summary>The knights placed for this run, standing or not. An immutable array, republished whole on change.</summary>
        IReadOnlyList<Creature> Knights { get; }

        /// <summary>The wave monsters (finale boss included) this run has placed and not yet forgotten. An immutable array, republished whole on change.</summary>
        IReadOnlyList<Creature> WaveMonsters { get; }

        /// <summary>Remembers that <paramref name="healer"/> healed a knight at <paramref name="now"/> (for hunters). Takes the run's lock.</summary>
        void NoteKnightHealer(Player healer, double now);

        /// <summary>Guids of the players who healed a knight within Tuning.HealerMemorySeconds of <paramref name="now"/>, most recent first. A new list each call.</summary>
        IReadOnlyList<uint> RecentHealers(double now);

        /// <summary>The finale boss landed a hit of <paramref name="dealt"/> on <paramref name="knight"/>. Takes the run's lock.</summary>
        void NoteFinaleBossHit(Creature boss, Creature knight, float dealt, double now);

        /// <summary>The finale boss's damage multiplier now: Tuning.FinaleEnrageMult once it has lived Tuning.FinaleEnrageSeconds, else 1.</summary>
        double FinaleEnrageMult(Creature boss, double now);

        /// <summary>
        /// A player used <paramref name="shrine"/>. True when it is one of this run's shrines and the use was handled here
        /// (<paramref name="reply"/> is what the player is told, possibly null); false for anything that is not this run's
        /// shrine, with <paramref name="reply"/> null.
        /// </summary>
        bool TryUseShrine(WorldObject shrine, Player player, double now, out string reply);

        /// <summary>
        /// H6: true when the character <paramref name="guid"/> takes part in this run, by the run's presence ledger (it has
        /// been inside the arena while the run was Active). Any thread. The default - a view with no ledger behind it - counts
        /// everyone, so a test fake needs no change.
        /// </summary>
        bool IsParticipant(uint guid) => true;
    }
}
