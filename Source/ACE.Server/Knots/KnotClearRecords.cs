using System;

using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Knots
{
    /// <summary>
    /// The per-character best Knot clear behind /top knot: one record per character, written by
    /// <see cref="Record"/> from a delivered clear (LivePinnacleWorld.ReportCleared, on the owner's thread).
    ///
    /// THE THREE PROPERTIES MOVE TOGETHER. BestKnotClearLevel, BestKnotClearTimeCenti and BestKnotClearPinnacleKey are
    /// written in one call and only when the new clear is better (<see cref="IsBetter"/>), so the level, time and Knot
    /// type on the board always come from the same clear, never from different ones.
    /// </summary>
    public static class KnotClearRecords
    {
        /// <summary>
        /// True when a clear at <paramref name="level"/> in <paramref name="centi"/> centiseconds beats the stored record:
        /// a higher level wins; at the same level only a strictly faster time wins; a character with no record yet
        /// (<paramref name="storedLevel"/> null or 0 or less) always loses to any clear. A stored level with no usable stored time
        /// (null or 0 or less) is beaten by any clear of the same level.
        /// </summary>
        public static bool IsBetter(int level, long centi, int? storedLevel, long? storedCenti)
        {
            if (!storedLevel.HasValue || storedLevel.Value <= 0)
                return true;

            if (level != storedLevel.Value)
                return level > storedLevel.Value;

            if (!storedCenti.HasValue || storedCenti.Value <= 0)
                return true;

            return centi < storedCenti.Value;
        }

        /// <summary>
        /// Whole centiseconds for a duration in seconds. NaN, negative and infinite input return 0, which Record refuses (never
        /// an unbeatable 1 cs record); a finite positive sub-centisecond duration rounds up to 1.
        /// </summary>
        public static long ToCentiseconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                return 0;

            return Math.Max(1L, (long)Math.Round(seconds * 100.0, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Writes the clear to <paramref name="player"/> when it is better than the stored record; all three properties or
        /// none. Returns true when the record was replaced. Call on the player's own thread.
        /// </summary>
        public static bool Record(IPlayer player, string pinnacleKey, int level, long centi)
        {
            if (player == null || level <= 0 || centi <= 0)
                return false;

            if (!IsBetter(level, centi, player.GetProperty(PropertyInt.BestKnotClearLevel), player.GetProperty(PropertyInt64.BestKnotClearTimeCenti)))
                return false;

            player.SetProperty(PropertyInt.BestKnotClearLevel, level);
            player.SetProperty(PropertyInt64.BestKnotClearTimeCenti, centi);

            if (string.IsNullOrEmpty(pinnacleKey))
                player.RemoveProperty(PropertyString.BestKnotClearPinnacleKey);
            else
                player.SetProperty(PropertyString.BestKnotClearPinnacleKey, pinnacleKey);

            return true;
        }

        /// <summary>The Knot type's display name: the pinnacle's content name, else its key, else "Knot". Null-safe when no store is loaded.</summary>
        public static string KnotName(string pinnacleKey)
        {
            string name = null;

            try
            {
                name = ThreadDungeonManager.Store?.GetPinnacle(pinnacleKey)?.Name;
            }
            catch (Exception)
            {
                // a board render never fails over a name lookup
            }

            if (!string.IsNullOrWhiteSpace(name))
                return name;

            return string.IsNullOrWhiteSpace(pinnacleKey) ? "Knot" : pinnacleKey;
        }

        /// <summary>
        /// The board's per-line detail text, e.g. " - Grael - 4:37.20", or null when the character has no usable time.
        /// <paramref name="nameOf"/> is a test seam; null means <see cref="KnotName"/>.
        /// </summary>
        public static string FormatDetail(string pinnacleKey, long? centi, Func<string, string> nameOf = null)
        {
            if (!centi.HasValue || centi.Value <= 0)
                return null;

            return $" - {(nameOf ?? KnotName)(pinnacleKey)} - {Player.FormatWaveClearTime(centi.Value)}";
        }
    }
}
