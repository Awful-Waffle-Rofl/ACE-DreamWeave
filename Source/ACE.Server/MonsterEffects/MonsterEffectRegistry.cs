using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.MonsterEffects.Effects;

namespace ACE.Server.MonsterEffects
{
    /// <summary>
    /// Every monster effect handler, in registration order - which is also per-hook execution order.
    ///
    /// ADDING AN EFFECT IS ONE FILE AND ONE LINE: a handler class under MonsterEffects/Effects, and its
    /// constructor in <see cref="handWritten"/>. Its Kind becomes authorable in PropertyString 9015 the
    /// moment that line lands, because the kind vocabulary is derived from the handlers rather than
    /// maintained beside them.
    ///
    /// ALL SIXTEEN DESIGNED KINDS SHIP HERE NOW. Phase 0 shipped no handlers on purpose, listing every kind
    /// in <see cref="reservedKinds"/> so the parser would accept and report on them ahead of their handler;
    /// each phase since has added its registration line below and deleted the matching reserved name, and
    /// with the last five landed <see cref="reservedKinds"/> is empty - a static-ctor guard below throws if
    /// a kind is ever both registered and reserved at once, so the list cannot silently drift out of sync.
    /// </summary>
    public static class MonsterEffectRegistry
    {
        /// <summary>
        /// The shipped handlers, in the phase order they landed.
        /// </summary>
        private static readonly IMonsterEffect[] handWritten =
        {
            // Phase 1 - flat damage riders and vital theft on a landed hit
            new FlatDamageEffect(),
            new LeechEffect(),
            new ExecuteEffect(),

            // Phase 2 - incoming-damage filters
            new ReflectEffect(),
            new WardEffect(),
            new ManaBarrierEffect(),
            new RiposteEffect(),

            // Phase 3 - avoidance and speed
            new AvoidEffect(),
            new SpeedEffect(),

            // Phase 4 - periodic and applied effects
            new DotEffect(),
            new DebuffEffect(),
            new DispelEffect(),

            // Phase 5 - caster effects
            new CastspellEffect(),
            new RecastEffect(),

            // Phase 6 - accumulating and positional modifiers
            new RangeRampEffect(),
            new RampEffect(),
        };

        /// <summary>
        /// Kinds the design has specified but whose handler has not shipped. The parser accepts them, so a
        /// weenie may be authored ahead of the phase that runs it, and MonsterEffectSet keeps them as inert
        /// entries. Delete a name here in the same change that registers its handler above.
        /// </summary>
        private static readonly string[] reservedKinds =
        {
        };

        /// <summary>
        /// Every handler, in registration order. Per-hook buckets are built per MONSTER
        /// (<see cref="MonsterEffectSet"/>) rather than here, because a monster carries a handful of effects
        /// out of the whole catalogue and the hot paths must never walk the catalogue.
        /// </summary>
        public static readonly IReadOnlyList<IMonsterEffect> Handlers;

        /// <summary>
        /// Every kind the parser will accept: the shipped handlers plus <see cref="reservedKinds"/>.
        /// </summary>
        public static readonly IReadOnlyCollection<string> KnownKinds;

        private static readonly Dictionary<string, IMonsterEffect> handlersByKind;
        private static readonly HashSet<string> knownKinds;

        static MonsterEffectRegistry()
        {
            Handlers = handWritten.ToArray();

            handlersByKind = Handlers.ToDictionary(h => h.Kind, StringComparer.OrdinalIgnoreCase);

            knownKinds = new HashSet<string>(handlersByKind.Keys, StringComparer.OrdinalIgnoreCase);

            foreach (var kind in reservedKinds)
            {
                if (!knownKinds.Add(kind))
                    throw new InvalidOperationException($"MonsterEffectRegistry: kind '{kind}' is both registered and reserved. Delete the reserved name in the change that registers its handler.");
            }

            KnownKinds = knownKinds;
        }

        /// <summary>
        /// True for any kind the parser accepts, whether or not a handler runs it yet.
        /// </summary>
        public static bool IsKnownKind(string kind) => kind != null && knownKinds.Contains(kind);

        /// <summary>
        /// The handler for a kind. FALSE for a reserved kind that has no handler yet - the caller keeps the
        /// spec as an inert entry rather than treating it as an authoring error, which the parser has
        /// already ruled out.
        /// </summary>
        public static bool TryGetHandler(string kind, out IMonsterEffect handler)
        {
            if (kind == null)
            {
                handler = null;
                return false;
            }

            return handlersByKind.TryGetValue(kind, out handler);
        }
    }
}
