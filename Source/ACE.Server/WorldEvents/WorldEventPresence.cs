using System;
using System.Collections.Generic;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// One online player as seen by a presence sampling tick: who, and whether they are inside the arena.
    /// </summary>
    public readonly struct PresenceSample
    {
        public uint Guid { get; }
        public string Name { get; }
        public bool Inside { get; }

        public PresenceSample(uint guid, string name, bool inside)
        {
            Guid = guid;
            Name = name;
            Inside = inside;
        }
    }

    /// <summary>
    /// The presence ledger: how much of an event's window each player spent inside the arena. Pure - it is
    /// fed samples and timestamps and never touches a Player or Session - so the accrual rules are unit
    /// testable with a fake clock.
    ///
    /// RULES. The window runs from <see cref="Begin"/> to <see cref="Close"/>. Each <see cref="Sample"/>
    /// advances the total by dt = clamp(now - lastSample, 0, <see cref="MaxTickSeconds"/>); a stalled tick
    /// therefore cannot credit a long gap to anyone. Every player in the sample list earns at most dt that
    /// tick: dt if Inside, or dt if they are outside but their death grace has not expired. A player absent
    /// from the list (offline) earns nothing, and being absent ENDS their grace.
    ///
    /// GRACE. <see cref="OnDeath"/> for a death inside the arena starts (or restarts, never stacks) a grace
    /// period during which the player keeps accruing while outside - the corpse run. Re-entering the arena
    /// clears it.
    ///
    /// FRACTION. p = min(1, present / total), snapped to 1 when the shortfall is at most
    /// max(<see cref="SnapFloorSeconds"/>, snapPct% of total), so a player present for essentially the whole
    /// window is not shaved by a tick of rounding.
    ///
    /// THREADING. OnDeath arrives on landblock threads while Sample runs on the world thread, so every
    /// member takes <see cref="sync"/>, a private lock held only for plain dictionary work, and readers hand
    /// out copies. Same pattern as WorldEventParticipation.
    /// </summary>
    public sealed class WorldEventPresence
    {
        /// <summary>Longest span one Sample may credit, in seconds.</summary>
        public const double MaxTickSeconds = 5.0;

        /// <summary>Absolute floor on the snap-to-100% shortfall, in seconds.</summary>
        public const double SnapFloorSeconds = 3.0;

        private readonly object sync = new object();

        private readonly double graceSeconds;
        private readonly double snapPct;

        private readonly Dictionary<uint, double> present = new Dictionary<uint, double>();
        private readonly Dictionary<uint, DateTime> graceUntil = new Dictionary<uint, DateTime>();
        private readonly Dictionary<uint, DateTime> lastInside = new Dictionary<uint, DateTime>();

        /// <summary>Guids that have been sampled OUTSIDE during their current grace; only their next Inside sample ends it.</summary>
        private readonly HashSet<uint> graceLeft = new HashSet<uint>();

        private bool begun;
        private bool closed;
        private DateTime lastSample;
        private double total;

        public WorldEventPresence(double graceSeconds = 120, double snapPct = 2)
        {
            this.graceSeconds = Math.Max(0, graceSeconds);
            this.snapPct = Math.Max(0, snapPct);
        }

        /// <summary>Opens the window. A second call is ignored, as is a call after <see cref="Close"/>.</summary>
        public void Begin(DateTime now)
        {
            lock (sync)
            {
                if (begun || closed)
                    return;

                begun = true;
                lastSample = now;
            }
        }

        /// <summary>
        /// One sampling tick. Does nothing before <see cref="Begin"/> or after <see cref="Close"/>.
        /// </summary>
        public void Sample(DateTime now, IEnumerable<PresenceSample> online)
        {
            lock (sync)
            {
                if (!begun || closed)
                    return;

                AccrueLocked(now, online);
            }
        }

        private void AccrueLocked(DateTime now, IEnumerable<PresenceSample> online)
        {
            var dt = (now - lastSample).TotalSeconds;

            if (dt < 0 || double.IsNaN(dt))
                dt = 0;
            else if (dt > MaxTickSeconds)
                dt = MaxTickSeconds;

            if (now > lastSample)
                lastSample = now;

            total += dt;

            var seen = new HashSet<uint>();

            if (online != null)
            {
                foreach (var s in online)
                {
                    if (!seen.Add(s.Guid))
                        continue;

                    if (s.Inside)
                    {
                        Add(s.Guid, dt);

                        // Grace ends on RE-ENTRY only: the corpse still lies inside for the death animation
                        // and the lifestone teleport delay, and those Inside samples must not cancel a grace
                        // that has not been used yet. graceLeft records that the player has been outside
                        // during this grace, so an Inside sample after that is a genuine return.
                        if (graceLeft.Remove(s.Guid))
                            graceUntil.Remove(s.Guid);

                        lastInside[s.Guid] = now;
                    }
                    else if (graceUntil.TryGetValue(s.Guid, out var until) && now <= until)
                    {
                        Add(s.Guid, dt);
                        graceLeft.Add(s.Guid);
                    }
                    else if (!present.ContainsKey(s.Guid))
                    {
                        present[s.Guid] = 0;
                    }
                }
            }

            // Logging out ends grace: a guid holding grace that is absent from this sample loses it.
            if (graceUntil.Count > 0)
            {
                List<uint> gone = null;

                foreach (var g in graceUntil.Keys)
                {
                    if (!seen.Contains(g))
                        (gone ?? (gone = new List<uint>())).Add(g);
                }

                if (gone != null)
                {
                    foreach (var g in gone)
                    {
                        graceUntil.Remove(g);
                        graceLeft.Remove(g);
                    }
                }
            }
        }

        private void Add(uint guid, double dt)
        {
            present.TryGetValue(guid, out var cur);
            present[guid] = cur + dt;
        }

        /// <summary>
        /// A participant died. Counts only while the window is open and only for a death INSIDE the arena;
        /// starts or restarts the grace period (the later expiry wins, graces never stack).
        /// </summary>
        public void OnDeath(uint guid, DateTime now, bool insideArena)
        {
            lock (sync)
            {
                if (!begun || closed || !insideArena)
                    return;

                var until = now.AddSeconds(graceSeconds);

                graceDeaths++;

                // A repeat death restarts cleanly: the new grace has not been left yet.
                graceLeft.Remove(guid);

                if (!graceUntil.TryGetValue(guid, out var existing) || until > existing)
                    graceUntil[guid] = until;
            }
        }

        private int graceDeaths;

        /// <summary>How many deaths inside the arena, while the window was open, started or restarted a grace.</summary>
        public int GraceDeaths
        {
            get
            {
                lock (sync)
                    return graceDeaths;
            }
        }

        /// <summary>How many tracked players read as fully present (fraction 1.0 after the snap rule). 0 while unknown.</summary>
        public int FullCount
        {
            get
            {
                lock (sync)
                {
                    if (!begun || total <= 0)
                        return 0;

                    var count = 0;

                    foreach (var pres in present.Values)
                    {
                        if (FractionLocked(pres) >= 1.0)
                            count++;
                    }

                    return count;
                }
            }
        }

        private double FractionLocked(double pres)
        {
            var p = Math.Min(1.0, pres / total);

            // Never snaps a player who earned nothing: with a window shorter than the snap floor, a guid never
            // inside would otherwise read as fully present and turn an owner-ruled coal into a full crate.
            if (pres > 0 && total - pres <= Math.Max(SnapFloorSeconds, snapPct / 100.0 * total))
                p = 1.0;

            return p;
        }

        /// <summary>
        /// Closes the window: nothing accrues afterwards. Idempotent. The final span since the last sample
        /// is NOT credited here; the caller takes a closing <see cref="Sample"/> first if it wants one.
        /// </summary>
        public void Close(DateTime now)
        {
            lock (sync)
            {
                closed = true;
                graceUntil.Clear();
                graceLeft.Clear();
            }
        }

        /// <summary>
        /// The player's presence fraction. False (unknown) when the window never began or no time has
        /// accrued. A guid never seen yields true with p = 0.
        /// </summary>
        public bool TryGetFraction(uint guid, out double p)
        {
            lock (sync)
            {
                p = 0;

                if (!begun || total <= 0)
                    return false;

                present.TryGetValue(guid, out var pres);

                p = FractionLocked(pres);

                return true;
            }
        }

        /// <summary>
        /// The player's accrued present seconds (inside, or outside within a death grace). False for a guid the
        /// ledger has never sampled; a guid sampled only outside yields true with 0. Readable before Begin and
        /// after Close; Close freezes the value.
        /// </summary>
        public bool TryGetPresentSeconds(uint guid, out double seconds)
        {
            lock (sync)
                return present.TryGetValue(guid, out seconds);
        }

        /// <summary>A copy of the last time each player was seen inside the arena.</summary>
        public IReadOnlyDictionary<uint, DateTime> LastInside
        {
            get
            {
                lock (sync)
                    return new Dictionary<uint, DateTime>(lastInside);
            }
        }

        /// <summary>How many distinct players the ledger has seen.</summary>
        public int TrackedCount
        {
            get
            {
                lock (sync)
                    return present.Count;
            }
        }

        /// <summary>Total accrued window seconds.</summary>
        public double TotalSeconds
        {
            get
            {
                lock (sync)
                    return total;
            }
        }
    }
}