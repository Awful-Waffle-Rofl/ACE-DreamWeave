using System;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Pvp
{
    /// <summary>The kind of match a <see cref="PvpMatchFacts"/> came from, as the PvP dailies group modes.</summary>
    public enum PvpDailyMode
    {
        /// <summary>A mode no daily reads.</summary>
        Other,

        /// <summary>The 1v1 arena (ArenaMapCatalog.OneVOneKey).</summary>
        OneVOne,

        /// <summary>The 2v2 arena (ArenaMapCatalog.TwoVTwoKey). No daily reads it today.</summary>
        TwoVTwo,

        /// <summary>Tugak Brawl, the free-for-all (ArenaMapCatalog.FfaKey).</summary>
        Tugak,

        /// <summary>King of the Hill (BattlegroundModes.KothModeKey).</summary>
        Koth,

        /// <summary>Attack/Defend (BattlegroundModes.AttackDefendModeKey).</summary>
        AttackDefend,
    }

    /// <summary>
    /// What one ELIGIBLE seat did in one resolved match, as the PvP dailies read it (PvpDailyCatalog). Built ONCE by the coordinator in
    /// Resolve, beside the payouts, only for a seat that passes <see cref="PvpRewardEligibility"/>, so the existence of a facts record
    /// already means "completed": in the match to the end, win or lose. Plain data; dispatched onto the player's own action chain.
    /// </summary>
    /// <param name="Result">Win, loss or draw by the payout rule (<see cref="PvpArenaRewards.ResultFor"/>): in Tugak Brawl first place,
    /// shared first included, is a win.</param>
    /// <param name="Kills">Enemy kills credited to the seat (the saved participant Kills; teamkills never count).</param>
    /// <param name="HillSeconds">King of the Hill seconds on the point, from the performance ledger (0 without one).</param>
    /// <param name="CrystalDamage">Attack/Defend damage dealt to enemy crystals, from the performance ledger.</param>
    /// <param name="KillshotsNearCrystal">Cross-team killshots whose victim died near the vulnerable crystal, from the ledger.</param>
    /// <param name="KillshotsOnHill">Cross-team killshots whose victim died ON the hill, with no margin, from the ledger.</param>
    /// <param name="OpponentAccount">1v1 only: the opponent's repeat-pairing account key (never the character id unless the character
    /// has no account). Null in every other mode.</param>
    /// <param name="PeakRating">The highest stored rating Peak of this character across every ladder, read from the rating store at
    /// match end on the world thread (after this match's own rating was applied). 0 when unknown.</param>
    /// <param name="Forfeited">A FORFEIT fact, not an eligible seat's facts: the seat forfeited a 1v1 that went Live (no same-IP opponents).
    /// It counts as a loss for GladiatorsStreak only (the streak resets to 0) and advances nothing else; see <see cref="ForfeitOf"/>.</param>
    public sealed record PvpMatchFacts(
        Guid MatchId,
        uint CharacterId,
        PvpDailyMode Mode,
        int TeamIndex,
        PvpBloodResult Result,
        long Kills,
        long HillSeconds,
        long CrystalDamage,
        long KillshotsNearCrystal,
        long KillshotsOnHill,
        uint? OpponentAccount,
        int PeakRating,
        bool Forfeited = false)
    {
        /// <summary>
        /// The minimal forfeit fact for a seat that forfeited a Live 1v1: a loss, no counters, no opponent, no peak. PvpDailyProgress reads
        /// it as a GladiatorsStreak reset and nothing else.
        /// </summary>
        public static PvpMatchFacts ForfeitOf(Guid matchId, uint characterId, int teamIndex)
            => new PvpMatchFacts(matchId, characterId, PvpDailyMode.OneVOne, teamIndex, PvpBloodResult.Loss, 0, 0, 0, 0, 0, null, 0, Forfeited: true);

        public bool IsBattleground => Mode == PvpDailyMode.Koth || Mode == PvpDailyMode.AttackDefend;

        public bool Won => Result == PvpBloodResult.Win;

        /// <summary>Attack/Defend: the seat was on the attacking team (team 0, CrystalWinCondition.AttackerTeam).</summary>
        public bool IsAttacker => Mode == PvpDailyMode.AttackDefend && TeamIndex == CrystalWinCondition.AttackerTeam;

        /// <summary>Attack/Defend: the seat was on the defending team (team 1, CrystalWinCondition.DefenderTeam).</summary>
        public bool IsDefender => Mode == PvpDailyMode.AttackDefend && TeamIndex == CrystalWinCondition.DefenderTeam;

        /// <summary>The daily mode of a coordinator mode key; <see cref="PvpDailyMode.Other"/> for anything unknown.</summary>
        public static PvpDailyMode ModeOf(string modeKey)
        {
            switch (modeKey)
            {
                case ArenaMapCatalog.OneVOneKey: return PvpDailyMode.OneVOne;
                case ArenaMapCatalog.TwoVTwoKey: return PvpDailyMode.TwoVTwo;
                case ArenaMapCatalog.FfaKey: return PvpDailyMode.Tugak;
                case BattlegroundModes.KothModeKey: return PvpDailyMode.Koth;
                case BattlegroundModes.AttackDefendModeKey: return PvpDailyMode.AttackDefend;
                default: return PvpDailyMode.Other;
            }
        }
    }
}
