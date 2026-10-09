using System;

namespace ACE.Server.Pvp.Rating
{
    /// <summary>
    /// Standard Elo math (Docs/Pvp/DESIGN.md "Rating" > "Elo"). Pure functions only - no state, no
    /// PropertyManager reads. Initial rating (1500) and every K value are tunables, passed in by callers.
    /// </summary>
    public static class EloRating
    {
        /// <summary>Expected score for the "self" side against "opponent", in [0, 1]. Symmetric: ExpectedScore(a,b) + ExpectedScore(b,a) == 1.</summary>
        public static double ExpectedScore(double ratingSelf, double ratingOpponent)
        {
            return 1.0 / (1.0 + Math.Pow(10.0, (ratingOpponent - ratingSelf) / 400.0));
        }

        /// <summary>
        /// K is kProvisional for a player's first provisionalGames games, and kEstablished from then on
        /// (design: "K is 40 for a player's first 10 games and 24 after that").
        /// </summary>
        public static int SelectK(int gamesPlayed, int provisionalGames, int kProvisional, int kEstablished)
        {
            return gamesPlayed < provisionalGames ? kProvisional : kEstablished;
        }

        /// <summary>The standard Elo delta: K * (actual score - expected score), rounded to the nearest integer.</summary>
        public static int Delta(int k, double score, double expected)
        {
            return (int)Math.Round(k * (score - expected), MidpointRounding.AwayFromZero);
        }
    }
}
