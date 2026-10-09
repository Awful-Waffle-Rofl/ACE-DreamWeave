using System;

namespace ACE.Server.Pvp.Rating
{
    /// <summary>
    /// Read-time rating decay (Docs/Pvp/DESIGN.md "Rating" > "Decay"): "After a 14-day grace period, a
    /// player loses 25 points for each full week without a match, but never drops below 1500. Players
    /// already below 1500 do not decay." Worked out purely from the last-match timestamp - never a
    /// background job - and the decayed value is written back only when that player's next match writes
    /// the row (storage concern, PR B/C).
    /// </summary>
    public static class RatingDecay
    {
        public static int Apply(int rating, DateTime lastMatchAtUtc, DateTime nowUtc, int graceDays, int pointsPerWeek, int floor)
        {
            if (rating <= floor)
                return rating;

            var sinceLastMatch = nowUtc - lastMatchAtUtc;
            var pastGrace = sinceLastMatch - TimeSpan.FromDays(graceDays);

            if (pastGrace <= TimeSpan.Zero)
                return rating;

            var fullWeeks = (int)(pastGrace.TotalDays / 7.0);

            if (fullWeeks <= 0)
                return rating;

            var decayed = rating - fullWeeks * pointsPerWeek;

            return Math.Max(decayed, floor);
        }
    }
}
