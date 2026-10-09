using System.Collections.Generic;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// One seat's position as sampled for the control-zone check. X, Y and Z are landblock-local metres, the same
    /// frame as <see cref="PvpSpawnPoint"/>. IpKey is the seat's snapshotted same-IP key, for the per-team dedupe.
    /// </summary>
    public sealed record BattlegroundZoneSample(
        uint CharacterId,
        int TeamIndex,
        string IpKey,
        bool InInstance,
        bool IsDead,
        bool IsTeleporting,
        double X,
        double Y,
        double Z);

    /// <summary>
    /// What an objective-mode tick handler may read and ask of a battleground match
    /// (Docs/Pvp/BATTLEGROUNDS.md "King of the Hill"). The coordinator implements it in a later step; every method is
    /// called on the world thread and none blocks.
    /// </summary>
    public interface IBattlegroundMatchContext : IMatchContext, IScoredMatchView
    {
        /// <summary>Every active seat (<see cref="BattlegroundSeats.IsActive(BattlegroundSeat)"/>), with team index and respawning flag.</summary>
        IReadOnlyList<BattlegroundSeat> ActiveSeats { get; }

        /// <summary>Samples each active, non-respawning, dispatched seat's instance, position, dead and teleporting state.</summary>
        IReadOnlyList<BattlegroundZoneSample> SampleZone();

        /// <summary>
        /// Asks the coordinator to drain a player's vitals on their own queue. When <paramref name="lethal"/> and health
        /// reaches 0, the player dies through the normal respawn flow with no killer credited.
        /// </summary>
        void RequestDrain(uint characterId, int health, int stamina, int mana, bool lethal);

        /// <summary>Sends a chat line to every seat in the match.</summary>
        void Announce(string text);

        /// <summary>
        /// The coin flip for a moving hill's tied-score move from the centre site: true for the west side. The coordinator
        /// answers from ThreadSafeRandom; the tick handler calls it only when it needs the answer.
        /// </summary>
        bool RollHillTieBreakWest();

        /// <summary>
        /// The hill moved: replace the zone-marker ring with one on <paramref name="zone"/>, best effort and never blocking
        /// (the old ring is removed, the new one placed through the same fixture seam). A no-op for a match with no markers.
        /// </summary>
        void ReplaceZoneMarkers(KothZone zone);
    }
}