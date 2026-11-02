using System;
using System.Collections.Generic;
using System.Globalization;

using log4net;

using ACE.Common;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Per-copy creation roll: a fixed weenie carries a PropertyString.CreationRollSpec, and every copy created
    /// from it rolls the named stats uniformly on a step grid (both ends inclusive).
    ///
    /// Grammar: <c>v1|&lt;entry&gt;;&lt;entry&gt;;...</c>, entry = <c>&lt;t&gt;&lt;id&gt;=&lt;min&gt;:&lt;max&gt;:&lt;step&gt;</c>,
    /// t = i (PropertyInt) or f (PropertyFloat), id decimal. Example: <c>v1|f29=1.13:1.27:0.01;f62=1.13:1.27:0.01</c>.
    ///
    /// A spec that fails to parse applies NOTHING (all-or-nothing). Either way the property is removed from the
    /// created object so it never persists on the instance. Called from WorldObjectFactory.CreateNewWorldObject(Weenie).
    /// </summary>
    public static class CreationRoll
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string VersionPrefix = "v1|";

        public sealed class Entry
        {
            public bool IsFloat;
            public int Id;
            public decimal Min;
            public decimal Max;
            public decimal Step;

            /// <summary>Number of grid points: k runs 0..GridCount-1, value = Min + k*Step.</summary>
            public int GridCount;

            public decimal ValueAt(int k)
            {
                var places = Math.Max(DecimalPlaces(Step), DecimalPlaces(Min));
                return Math.Round(Min + k * Step, places, MidpointRounding.AwayFromZero);
            }
        }

        private static readonly HashSet<uint> warned = new HashSet<uint>();
        private static readonly object warnLock = new object();

        private static int DecimalPlaces(decimal d)
        {
            return (decimal.GetBits(d)[3] >> 16) & 0xFF;
        }

        /// <summary>
        /// Pure parse. Returns null for any malformed spec (wrong version, bad entry, min &gt; max, step &lt;= 0,
        /// unknown type letter, int entry with non-integer values).
        /// </summary>
        public static List<Entry> Parse(string spec)
        {
            if (spec == null || !spec.StartsWith(VersionPrefix, StringComparison.Ordinal))
                return null;

            var body = spec.Substring(VersionPrefix.Length);
            if (body.Length == 0)
                return null;

            var result = new List<Entry>();

            foreach (var part in body.Split(';'))
            {
                var entry = ParseEntry(part);
                if (entry == null)
                    return null;
                result.Add(entry);
            }

            return result;
        }

        private static Entry ParseEntry(string s)
        {
            if (s.Length < 2)
                return null;

            bool isFloat;
            switch (s[0])
            {
                case 'i': isFloat = false; break;
                case 'f': isFloat = true; break;
                default: return null;
            }

            var eq = s.IndexOf('=');
            if (eq < 2)
                return null;

            if (!int.TryParse(s.Substring(1, eq - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                return null;

            // PropertyInt/PropertyFloat are ushort-backed; an unchecked cast would wrap (65565 -> 29)
            if (id > ushort.MaxValue)
                return null;

            var nums = s.Substring(eq + 1).Split(':');
            if (nums.Length != 3)
                return null;

            var vals = new decimal[3];
            for (var i = 0; i < 3; i++)
            {
                if (!decimal.TryParse(nums[i], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out vals[i]))
                    return null;
            }

            var min = vals[0];
            var max = vals[1];
            var step = vals[2];

            if (step <= 0 || min > max)
                return null;

            if (!isFloat && (min != decimal.Truncate(min) || max != decimal.Truncate(max) || step != decimal.Truncate(step)))
                return null;

            decimal count;
            try
            {
                count = Math.Round((max - min) / step, 0, MidpointRounding.AwayFromZero);
                // rounding up must never put the last grid point past max
                if (min + count * step > max)
                    count--;
            }
            catch (OverflowException)
            {
                return null;
            }

            if (count < 0 || count > 1000000)
                return null;

            return new Entry { IsFloat = isFloat, Id = id, Min = min, Max = max, Step = step, GridCount = (int)count + 1 };
        }

        /// <summary>
        /// Rolls the object's CreationRollSpec (if any) with the supplied generator, then removes the spec.
        /// </summary>
        public static void Apply(WorldObject wo, Random rng)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));

            Apply(wo, n => rng.Next(0, n + 1));
        }

        /// <summary>
        /// Rolls with the server's ThreadSafeRandom (whose Next(int, int) max is inclusive).
        /// </summary>
        public static void Apply(WorldObject wo)
        {
            Apply(wo, n => ThreadSafeRandom.Next(0, n));
        }

        /// <summary>
        /// nextInclusive(n) must return an integer in [0, n], both ends inclusive.
        /// </summary>
        private static void Apply(WorldObject wo, Func<int, int> nextInclusive)
        {
            if (wo == null)
                return;

            var spec = wo.GetProperty(PropertyString.CreationRollSpec);
            if (spec == null)
                return;

            wo.RemoveProperty(PropertyString.CreationRollSpec);

            var entries = Parse(spec);
            if (entries == null)
            {
                bool first;
                lock (warnLock)
                    first = warned.Add(wo.WeenieClassId);

                if (first)
                    log.Warn($"CreationRoll: invalid CreationRollSpec on wcid {wo.WeenieClassId}, no rolls applied: '{spec}'");
                return;
            }

            foreach (var e in entries)
            {
                var k = nextInclusive(e.GridCount - 1);
                var value = e.ValueAt(k);

                if (e.IsFloat)
                    wo.SetProperty((PropertyFloat)e.Id, (double)value);
                else
                    wo.SetProperty((PropertyInt)e.Id, (int)value);
            }
        }
    }
}
