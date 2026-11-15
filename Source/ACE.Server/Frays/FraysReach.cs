using System;
using System.Collections.Generic;

using log4net;

using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// How much of a pack live area hits reach: samples live Frays
    /// Spell AOE splashes, melee cleaves and multishot against the awake pack. Frays only, players only, one summary line a minute per
    /// player and kind.
    /// </summary>
    public static class FraysReach
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private sealed class Tally
        {
            public int Hits;
            public long Reached, Possible, Awake;
            public double Since;
        }

        private static readonly Dictionary<(uint, string), Tally> tallies = new Dictionary<(uint, string), Tally>();
        private static readonly object sync = new object();

        /// <summary>
        /// One area hit by <paramref name="p"/> off <paramref name="primary"/>: <paramref name="reached"/> other creatures
        /// struck, out of at most <paramref name="cap"/> (int.MaxValue for no cap).
        /// </summary>
        public static void Sample(Player p, Creature primary, string kind, int reached, int cap)
        {
            if (p == null || primary?.FraysTag?.Fire is not FraysFire fire || !FraysManager.OnFrays(p.Location))
                return;

            var awakeOthers = Math.Max(0, fire.AwakeFor(primary.FraysTag.ForPlayer) - 1);
            var possible = Math.Min(Math.Max(0, cap), awakeOthers);
            var now = ACE.Common.Time.GetUnixTime();

            lock (sync)
            {
                var key = (p.Guid.Full, kind);

                if (!tallies.TryGetValue(key, out var t))
                    tallies[key] = t = new Tally { Since = now };

                t.Hits++;
                t.Reached += Math.Min(reached, possible);
                t.Possible += possible;
                t.Awake += awakeOthers;

                if (now - t.Since < 60)
                    return;

                var share = t.Possible > 0 ? (double)t.Reached / t.Possible : 1;
                log.Info($"[FRAYS] reach {fire.Def.Id}: {p.Name} {kind}: {t.Hits} hits, {(double)t.Reached / t.Hits:0.0} other creatures struck of {(double)t.Possible / t.Hits:0.0} possible ({share:P0}); {(double)t.Awake / t.Hits:0.0} other awake pack creatures");
                tallies.Remove(key);
            }
        }
    }
}
