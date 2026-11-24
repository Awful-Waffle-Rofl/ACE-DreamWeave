using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>One row of a shrine's boon table: the kind, its magnitude (already clamped to the run's cap by the caller or by TryUseShrine) and its draw weight.</summary>
    public readonly struct ShrineBoonRow
    {
        public ShrineBoonRow(ThreadBoonKind kind, double magnitude, double weight)
        {
            Kind = kind;
            Magnitude = magnitude;
            Weight = weight;
        }

        public ThreadBoonKind Kind { get; }
        public double Magnitude { get; }
        public double Weight { get; }
    }

    /// <summary>What one shrine use did (<see cref="ThreadDungeonRun.TryUseShrine"/>).</summary>
    public enum ShrineUseOutcome
    {
        /// <summary>The run has ended (or its blessings were released): nothing happened.</summary>
        RunOver,

        /// <summary>The user is not a roster member inside the run: nothing happened, the shrine is not spent.</summary>
        NotInside,

        /// <summary>Someone already used this shrine.</summary>
        Spent,

        /// <summary>The shrine is now spent and granted a blessing to every recipient.</summary>
        Granted,

        /// <summary>The shrine is now spent, but the run already holds every kind it can give: nothing granted.</summary>
        Silent,
    }

    /// <summary>A shrine's single-use latch. Read and written only under the run's stateLock (<see cref="ThreadDungeonRun.TryUseShrine"/>).</summary>
    public sealed class ShrineLatch
    {
        internal bool Spent;

        /// <summary>True once the shrine has been used (a locked read).</summary>
        public bool IsSpent(ThreadDungeonRun run) => run.IsShrineSpent(this);
    }

    /// <summary>
    /// Thread Shrine blessings (Docs/Threads/ENCOUNTERS.md "Shrine"): the run's in-memory boon ledger and its bonus-kill
    /// counter. Every mutable field lives under stateLock. Never persisted: a blessing is not an enchantment and not a
    /// property, so nothing about it can reach the shard or a client save.
    ///
    /// Owner rulings it encodes. A shrine grants ONE kind, to every roster member inside at the moment of use (a fixed
    /// set: nobody who arrives later gets it). A kind is granted at most once per RUN, so a second shrine draws only
    /// from the kinds the run does not hold yet and no member ever holds the same kind twice; the cap applies per
    /// shrine, at most <see cref="ShrineBoonCap"/>.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        public const double DefaultShrineBoonCap = 1.25;
        public const double ShrineBoonCapMin = 1.0;
        public const double ShrineBoonCapMax = 2.0;

        /// <summary>dynamic_dungeons_shrine_boon_cap as TryStart stamps it: NaN reads as the default, then clamped to [1.0, 2.0]. Pure.</summary>
        public static double ClampShrineBoonCap(double configured)
            => double.IsNaN(configured) ? DefaultShrineBoonCap : Math.Clamp(configured, ShrineBoonCapMin, ShrineBoonCapMax);

        /// <summary>dynamic_dungeons_shrine_boon_cap, read once by TryStart and stamped before publication. A run nobody stamped (tests) uses the shipped default.</summary>
        public double ShrineBoonCap { get; set; } = DefaultShrineBoonCap;

        private static readonly IReadOnlyDictionary<uint, IReadOnlyDictionary<ThreadBoonKind, double>> NoBoons
            = new Dictionary<uint, IReadOnlyDictionary<ThreadBoonKind, double>>();

        /// <summary>
        /// The member -> kind -> magnitude ledger as an IMMUTABLE snapshot. Written only under stateLock, by building a new
        /// dictionary and swapping the reference (copy on write: a shrine use is rare); read lock-free by the combat hooks
        /// (<see cref="BoonMagnitude"/>), so a rating read never takes the run lock. Never mutated after publication.
        /// </summary>
        private volatile IReadOnlyDictionary<uint, IReadOnlyDictionary<ThreadBoonKind, double>> boonSnapshot = NoBoons;

        private readonly HashSet<ThreadBoonKind> grantedBoonKinds = new HashSet<ThreadBoonKind>();
        private bool boonsCounted;
        private volatile bool boonsReleased;
        private int bonusKills;

        /// <summary>Lock-free hint for the hot path: has this run granted a blessing that is not released?</summary>
        private volatile bool holdsAnyBoon;

        /// <summary>A lock-free read of the run's state, for the hot path only (the state field is written under stateLock).</summary>
        private bool IsEndedLockFree
            => System.Threading.Volatile.Read(ref System.Runtime.CompilerServices.Unsafe.As<ThreadDungeonRunState, int>(ref state)) == (int)ThreadDungeonRunState.Ended;

        public bool HoldsAnyBoon => holdsAnyBoon;

        /// <summary>
        /// One shrine use, atomically. The latch, the run-level granted set and every recipient's ledger row change under
        /// one stateLock, so two players using the same shrine in the same tick produce exactly one grant, and two
        /// shrines used at once can never grant the same kind.
        /// </summary>
        /// <param name="userInside">Is the user a roster member inside the copy (ThreadRunPresence.IsMemberInside)? Checked first: a refused use does not spend the shrine.</param>
        /// <param name="recipients">The roster members inside at this moment (guids). The user is expected among them.</param>
        /// <param name="draw">A uniform draw in [0, 1]; called once, and only when there is more than one kind left.</param>
        public ShrineUseOutcome TryUseShrine(ShrineLatch latch, bool userInside, IReadOnlyCollection<uint> recipients, IReadOnlyList<ShrineBoonRow> table,
            Func<double> draw, out ThreadBoonKind kind, out double magnitude)
        {
            kind = default;
            magnitude = 1.0;

            if (latch == null)
                return ShrineUseOutcome.RunOver;

            lock (stateLock)
            {
                if (state == ThreadDungeonRunState.Ended || boonsReleased || encountersEnded)
                    return ShrineUseOutcome.RunOver;

                if (!userInside)
                    return ShrineUseOutcome.NotInside;

                if (latch.Spent)
                    return ShrineUseOutcome.Spent;

                latch.Spent = true;

                var cap = ClampShrineBoonCap(ShrineBoonCap);
                // A row is usable only if it still grants something after the cap (a cap of 1.0 grants nothing, and the
                // shrine type refuses to place under such a cap, ShrineEncounterType.Place).
                var available = (table ?? Array.Empty<ShrineBoonRow>())
                    .Where(r => !grantedBoonKinds.Contains(r.Kind) && r.Weight > 0 && double.IsFinite(r.Weight) && Math.Min(r.Magnitude, cap) > 1.0)
                    .GroupBy(r => r.Kind)
                    .Select(g => g.First())
                    .ToList();

                if (available.Count == 0)
                    return ShrineUseOutcome.Silent;

                var pick = available[0];

                if (available.Count > 1)
                {
                    var total = available.Sum(r => r.Weight);
                    var u = draw?.Invoke() ?? 0.0;
                    u = double.IsNaN(u) ? 0.0 : Math.Clamp(u, 0.0, 1.0);
                    var target = u * total;
                    var acc = 0.0;

                    pick = available[available.Count - 1];

                    foreach (var row in available)
                    {
                        acc += row.Weight;

                        if (target < acc)
                        {
                            pick = row;
                            break;
                        }
                    }
                }

                kind = pick.Kind;
                magnitude = Math.Min(pick.Magnitude, cap);

                grantedBoonKinds.Add(kind);

                // Copy on write: a new outer dictionary, a new inner one for every recipient, the rest shared (they are
                // never mutated after publication), then one reference swap.
                var next = new Dictionary<uint, IReadOnlyDictionary<ThreadBoonKind, double>>(boonSnapshot);

                foreach (var guid in recipients ?? Array.Empty<uint>())
                {
                    var held = next.TryGetValue(guid, out var prior)
                        ? new Dictionary<ThreadBoonKind, double>(prior)
                        : new Dictionary<ThreadBoonKind, double>();

                    held[kind] = magnitude;
                    next[guid] = held;
                }

                boonSnapshot = next;
                holdsAnyBoon = true;

                // The process-wide gate is raised INSIDE the lock, paired with ReleaseBoons' lowering (also inside it), so
                // a release that races this grant can never run between the two and leave the gate stuck up.
                if (!boonsCounted)
                {
                    boonsCounted = true;
                    ThreadBoons.OnRunGainedBoons();
                }
            }

            return ShrineUseOutcome.Granted;
        }

        internal bool IsShrineSpent(ShrineLatch latch)
        {
            lock (stateLock)
                return latch?.Spent ?? false;
        }

        /// <summary>The magnitude <paramref name="guid"/> holds for <paramref name="kind"/>; 1.0 when none, or once the run has ended or released its blessings.</summary>
        /// <remarks>Lock-free (the combat hot path): volatile reads of the release flag, the run state and the snapshot reference.</remarks>
        public double BoonMagnitude(uint guid, ThreadBoonKind kind)
        {
            if (!holdsAnyBoon || boonsReleased || IsEndedLockFree)
                return 1.0;

            var snapshot = boonSnapshot;

            return snapshot.TryGetValue(guid, out var held) && held.TryGetValue(kind, out var m) ? m : 1.0;
        }

        /// <summary>A snapshot of what <paramref name="guid"/> holds, in kind order (for /dd status). Empty after the run ended.</summary>
        public IReadOnlyList<(ThreadBoonKind Kind, double Magnitude)> BoonsHeldBy(uint guid)
        {
            if (boonsReleased || IsEndedLockFree || !boonSnapshot.TryGetValue(guid, out var held))
                return Array.Empty<(ThreadBoonKind, double)>();

            return held.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
        }

        /// <summary>The kinds this run has granted so far, in kind order.</summary>
        public IReadOnlyList<ThreadBoonKind> GrantedBoonKinds
        {
            get
            {
                lock (stateLock)
                    return grantedBoonKinds.OrderBy(k => k).ToList();
            }
        }

        /// <summary>
        /// Drops the ledger (the shrine type's OnRunEnded, any thread). Idempotent; once released no later use can grant.
        /// Releases this run's share of the process-wide hot-path gate.
        /// </summary>
        public void ReleaseBoons()
        {
            lock (stateLock)
            {
                if (boonsReleased)
                    return;

                boonsReleased = true;
                holdsAnyBoon = false;
                boonSnapshot = NoBoons;

                // Inside the lock, the mirror of TryUseShrine's raise: exactly one lowering per raise.
                if (boonsCounted)
                    ThreadBoons.OnRunReleasedBoons();
            }
        }

        // ---- bonus kills (shrine guardians) ------------------------------------------------------------------

        /// <summary>
        /// Counts one bonus-creature death (a creature shaped with CreatureShapeOptions.ExcludedFromClear). Never touches
        /// killed, the clear target or the clear progress: the denominator is set only by MarkPopulated.
        /// </summary>
        public void RecordBonusKill()
        {
            lock (stateLock)
                bonusKills++;
        }

        /// <summary>Bonus-creature deaths so far (for /dd and telemetry).</summary>
        public int BonusKills { get { lock (stateLock) return bonusKills; } }
    }
}
