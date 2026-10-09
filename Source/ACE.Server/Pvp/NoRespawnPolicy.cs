namespace ACE.Server.Pvp
{
    /// <summary>Every v1 arena mode's respawn policy: "no respawn in every arena mode" (design's Rulings).</summary>
    public sealed class NoRespawnPolicy : IRespawnPolicy
    {
        public DeathDisposition OnDeath(IMatchView m, PvpParticipant p) => DeathDisposition.Eliminate;
    }
}
