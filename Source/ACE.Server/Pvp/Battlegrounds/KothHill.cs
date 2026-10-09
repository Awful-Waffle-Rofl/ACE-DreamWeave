using System;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>Which side of the map a King of the Hill site is on: the centre site, or the west or east member of a mirrored pair.</summary>
    public enum KothSide
    {
        Centre,
        West,
        East
    }

    /// <summary>
    /// One King of the Hill hill site: <see cref="Pair"/> 0 is the centre site (side Centre); pair k at least 1 is the k-th
    /// mirrored pair, on the West or East side (Docs/Pvp/BATTLEGROUNDS.md "Moving hill"). A side room is always pair 1 today
    /// (bg_016c has one pair); the schedule never leaves the centre for a pair other than 1.
    /// </summary>
    public readonly record struct KothSiteChoice(int Pair, KothSide Side)
    {
        /// <summary>The centre site the match starts on.</summary>
        public static readonly KothSiteChoice Start = new KothSiteChoice(0, KothSide.Centre);
    }

    /// <summary>
    /// One mirrored pair of hill sites, given by its WEST member only: the east member is the exact x-mirror about the
    /// layout's zone axis, same y (<see cref="BattlegroundLayout.SiteFor"/>), so the two can never disagree.
    /// </summary>
    public sealed record KothSitePair(float WestX, float Y);

    /// <summary>
    /// The pure "where does the hill go next" rules (Docs/Pvp/BATTLEGROUNDS.md "Moving hill"). No clock, no random, no
    /// match state: every input is a parameter.
    /// </summary>
    public static class KothHillSchedule
    {
        /// <summary>The most moves pvp_bg_koth_hill_moves allows. The hill alternates between the centre and the two side rooms, so a move count is not bounded by the number of site pairs.</summary>
        public const int MaxMoves = 3;

        /// <summary>
        /// The next site, or null when the hill stays. With N = <paramref name="moves"/> moves allowed and
        /// <paramref name="movesMade"/> already made, step k = movesMade + 1 fires when the LEADING team's score reaches k/(N+1)
        /// of <paramref name="target"/>, or when <paramref name="elapsedSeconds"/> reaches k/(N+1) of
        /// <paramref name="timeLimitSeconds"/> (ignored when not positive), whichever comes first. Only the next step is ever
        /// considered, so a jump past two thresholds still moves one step per call, each step once.
        ///
        /// <para/>
        /// The destination is the TRAILING team's room (team 0 west, team 1 east): the lower score picks its own side room.
        /// If the hill is already in that room it returns to the centre instead, so every move is a real change. On a tied
        /// score the hill leaves a side room for the OPPOSITE room, and leaves the centre for the west room when
        /// <paramref name="tieBreakWest"/> returns true, else the east. The flip is a callback, invoked only on that tied-from-centre branch and only once the move is known to fire, so the caller's random source is never touched otherwise. A side room is pair 1,
        /// the centre pair 0.
        /// </summary>
        public static KothSiteChoice? NextSite(int westScore, int eastScore, int target, double elapsedSeconds, double timeLimitSeconds,
            int movesMade, int moves, KothSide currentSide, Func<bool> tieBreakWest)
        {
            if (moves <= 0 || movesMade < 0 || movesMade >= moves)
                return null;

            var k = (long)movesMade + 1;
            var parts = (long)moves + 1;

            var leader = Math.Max(westScore, eastScore);
            var byScore = target > 0 && (long)leader * parts >= k * target;
            var byTime = timeLimitSeconds > 0 && elapsedSeconds * parts >= k * timeLimitSeconds;

            if (!byScore && !byTime)
                return null;

            KothSide room;

            if (westScore < eastScore)
                room = KothSide.West;
            else if (eastScore < westScore)
                room = KothSide.East;
            else if (currentSide == KothSide.West)
                room = KothSide.East;
            else if (currentSide == KothSide.East)
                room = KothSide.West;
            else
                room = tieBreakWest() ? KothSide.West : KothSide.East;

            // The trailing team's room is where the hill already is: back to the centre, so the move is never a no-op.
            if (room == currentSide)
                return KothSiteChoice.Start;

            return new KothSiteChoice(1, room);
        }

        /// <summary>
        /// A compass word for the offset (dx, dy) from the map centre, in the landblock frame (+x east, +y north): north,
        /// north-east, east and so on in 45 degree sectors, or "centre" within one metre. Pure.
        /// </summary>
        public static string Compass(double dx, double dy)
        {
            if (Math.Sqrt(dx * dx + dy * dy) < 1.0)
                return "centre";

            var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI; // 0 = east, 90 = north
            var sector = (int)Math.Floor((angle + 22.5) / 45.0);
            sector = ((sector % 8) + 8) % 8;

            return sector switch
            {
                0 => "east",
                1 => "north-east",
                2 => "north",
                3 => "north-west",
                4 => "west",
                5 => "south-west",
                6 => "south",
                _ => "south-east"
            };
        }
    }
}