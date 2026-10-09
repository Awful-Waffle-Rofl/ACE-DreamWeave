using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.MonsterEffects
{
    /// <summary>
    /// The immutable, fully resolved effect list for ONE monster weenie: every parsed record paired with the
    /// handler that runs it, plus a prefiltered index array per hook.
    ///
    /// Shared by every creature of the same (wcid, authored string) - Creature_MonsterEffects caches it, so
    /// a landblock activating with two hundred drudges parses and resolves once. That sharing is exactly why
    /// nothing in here is mutable: each creature gets its own MonsterEffectState[] of the same length, and
    /// index i in that array belongs to entry i here.
    ///
    /// The hook indexes are the point of the type. A dispatch site iterates only the indexes for its own
    /// hook, so a monster carrying three effects, none of which touch avoidance, costs an empty-array walk
    /// at the avoidance site rather than three interface type checks.
    /// </summary>
    public sealed class MonsterEffectSet
    {
        /// <summary>
        /// One resolved effect. <see cref="Handler"/> is null when the kind is declared but its
        /// implementation phase has not shipped yet (MonsterEffectRegistry.reservedKinds) - such an entry
        /// holds its slot in the state array and appears in no hook bucket, so it is inert without shifting
        /// the indexes of the effects around it.
        /// </summary>
        public readonly struct Entry
        {
            public IMonsterEffect Handler { get; }

            public MonsterEffectSpec Spec { get; }

            public Entry(IMonsterEffect handler, MonsterEffectSpec spec)
            {
                Handler = handler;
                Spec = spec;
            }
        }

        private readonly Entry[] entries;

        /// <summary>
        /// Total entries, and therefore the required length of a creature's MonsterEffectState[].
        /// </summary>
        public int Count => entries.Length;

        /// <summary>
        /// Entries that have a live handler. Below <see cref="Count"/> only while some authored kind is still
        /// awaiting its implementation phase.
        /// </summary>
        public int ActiveCount { get; }

        public Entry this[int index] => entries[index];

        public IReadOnlyList<int> OutgoingHitIndexes { get; }
        public IReadOnlyList<int> IncomingDamageIndexes { get; }
        public IReadOnlyList<int> AvoidanceIndexes { get; }
        public IReadOnlyList<int> SpellHitIndexes { get; }
        public IReadOnlyList<int> SpeedModIndexes { get; }
        public IReadOnlyList<int> CastHookIndexes { get; }
        public IReadOnlyList<int> HeartbeatIndexes { get; }
        public IReadOnlyList<int> RampSourceIndexes { get; }

        private MonsterEffectSet(Entry[] authored)
        {
            // ONE sort, here in the constructor, so EVERY construction path gets it - Build and the
            // BuildFromHandlers test seam alike. Doing it in Build instead would let tests exercise an order
            // production never runs.
            //
            // Stable (OrderBy is), so records of equal DispatchOrder keep the order they were authored in:
            // the authored string still decides everything the ordering rule does not, which keeps a weenie
            // readable. What it no longer decides is whether leech heals off the pre- or post-execute damage
            // figure - see the ordering paragraph in MonsterEffectHooks.cs.
            //
            // The state array is allocated against THIS order and indexed identically, so a slot belongs to
            // the entry at the same index here, not to the record's position in the authored string.
            entries = authored
                .OrderBy(e => e.Handler?.DispatchOrder ?? MonsterEffectDispatchOrder.Neutral)
                .ToArray();

            ActiveCount = entries.Count(e => e.Handler != null);

            OutgoingHitIndexes    = IndexesOf<IMonsterOutgoingHit>(entries);
            IncomingDamageIndexes = IndexesOf<IMonsterIncomingDamage>(entries);
            AvoidanceIndexes      = IndexesOf<IMonsterAvoidance>(entries);
            SpellHitIndexes       = IndexesOf<IMonsterSpellHit>(entries);
            SpeedModIndexes       = IndexesOf<IMonsterSpeedMod>(entries);
            CastHookIndexes       = IndexesOf<IMonsterCastHook>(entries);
            HeartbeatIndexes      = IndexesOf<IMonsterHeartbeat>(entries);
            RampSourceIndexes     = IndexesOf<IMonsterRampSource>(entries);
        }

        private static int[] IndexesOf<THook>(Entry[] entries) where THook : class, IMonsterEffect =>
            Enumerable.Range(0, entries.Length).Where(i => entries[i].Handler is THook).ToArray();

        /// <summary>
        /// Resolves parsed specs against the registry. A spec whose handler rejects its args
        /// (<see cref="IMonsterEffect.Validate"/>) is DROPPED and described in <paramref name="errors"/>;
        /// a spec whose kind is known but unimplemented is kept as an inert entry. Returns null when nothing
        /// survives, which is what keeps Creature.MonsterEffects null for a monster with no usable effects.
        /// </summary>
        public static MonsterEffectSet Build(IReadOnlyList<MonsterEffectSpec> specs, List<string> errors)
        {
            if (specs == null || specs.Count == 0)
                return null;

            var entries = new List<Entry>(specs.Count);

            foreach (var spec in specs)
            {
                if (!MonsterEffectRegistry.TryGetHandler(spec.Kind, out var handler))
                {
                    // known kind, phase not shipped - keep the slot so state indexes stay stable
                    entries.Add(new Entry(null, spec));
                    continue;
                }

                if (!handler.Validate(spec, out var error))
                {
                    errors?.Add($"effect '{spec}' rejected: {error}");
                    continue;
                }

                entries.Add(new Entry(handler, spec));
            }

            return entries.Count == 0 ? null : new MonsterEffectSet(entries.ToArray());
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo, ACE.Server.csproj:15): builds a set from handler instances
        /// directly, bypassing the registry. <see cref="Build"/> resolves kinds through
        /// MonsterEffectRegistry, which ships no handlers yet, so this is the only way a test can exercise
        /// the dispatch sites without making the production registry mutable.
        /// </summary>
        internal static MonsterEffectSet BuildFromHandlers(params (IMonsterEffect Handler, MonsterEffectSpec Spec)[] entries) =>
            new MonsterEffectSet(entries.Select(e => new Entry(e.Handler, e.Spec)).ToArray());

        /// <summary>
        /// The monster's current multiplier on one ramp axis, as the product of every effect publishing on
        /// it. 1.0 when nothing ramps that axis.
        ///
        /// This is the seam that lets an effect ask "how ramped is this monster" without naming the effect
        /// that ramps - a caster effect reads MagicDamage and neither knows nor cares whether the weenie
        /// authored a ramp record at all. Uncapped here on purpose: the cap belongs to the reader's own axis
        /// (monster_effect_speed_cap for speed, monster_effect_proc_chance_cap for chance), applied where the
        /// number is consumed.
        /// </summary>
        public double GetRampMultiplier(MonsterRampAxis axis, MonsterEffectState[] state)
        {
            var mult = 1.0;

            for (var i = 0; i < RampSourceIndexes.Count; i++)
            {
                var index = RampSourceIndexes[i];
                var entry = entries[index];
                var source = (IMonsterRampSource)entry.Handler;

                if (!source.ProvidesRamp(axis, entry.Spec))
                    continue;

                mult *= source.GetRampMultiplier(axis, entry.Spec, ref state[index]);
            }

            return mult;
        }
    }
}
