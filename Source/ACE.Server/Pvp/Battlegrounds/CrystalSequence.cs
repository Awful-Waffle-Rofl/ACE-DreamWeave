using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The sequential-crystal state of one Attack/Defend match (Docs/Pvp/ATTACK-DEFEND.md "Sequential crystals"): which crystal is the
    /// only vulnerable one. Crystals are numbered by plan index; the vulnerable one is the lowest index not yet destroyed, or -1 once
    /// every crystal has fallen.
    ///
    /// <para/>
    /// One writer, many readers. The tick handler (world thread) calls <see cref="MarkDestroyed"/> when a crystal's death intent drains;
    /// the crystal damage gate reads <see cref="Current"/> from an attacker's landblock thread through the match the attacker's binding
    /// points at, with no lock, the same way it reads <c>PvpMatch.State</c>. <see cref="Current"/> is one int published with a volatile
    /// write, so a reader sees the old or the new value, never a torn one. The names are fixed at construction.
    /// </summary>
    public sealed class CrystalSequence
    {
        private readonly string[] _names;
        private readonly bool[] _destroyed;
        private int _current;

        /// <param name="names">The crystals' display names in order; the position is the plan index. An empty list is born finished.</param>
        public CrystalSequence(IEnumerable<string> names)
        {
            _names = (names ?? Enumerable.Empty<string>()).ToArray();
            _destroyed = new bool[_names.Length];
            _current = _names.Length == 0 ? -1 : 0;
        }

        /// <summary>How many crystals are in the sequence.</summary>
        public int Count => _names.Length;

        /// <summary>The plan index of the one vulnerable crystal, or -1 when none is left.</summary>
        public int Current => Volatile.Read(ref _current);

        /// <summary>True when <paramref name="index"/> is the vulnerable crystal. Every other index, valid or not, is sealed.</summary>
        public bool IsVulnerable(int index) => index >= 0 && index == Current;

        /// <summary>The display name at <paramref name="index"/>, or null for an index outside the sequence.</summary>
        public string NameOf(int index) => index >= 0 && index < _names.Length ? _names[index] : null;

        /// <summary>The vulnerable crystal's name, or null when none is left.</summary>
        public string CurrentName => NameOf(Current);

        /// <summary>
        /// Records that the crystal at <paramref name="index"/> fell and moves <see cref="Current"/> to the lowest crystal still standing.
        /// Returns true only when that changed <see cref="Current"/> (the vulnerable crystal fell). A repeat, or an index outside the
        /// sequence, changes nothing and returns false. A crystal destroyed out of order (it should not happen: the gate refuses the hit)
        /// is still recorded, so the cursor skips it later.
        /// </summary>
        public bool MarkDestroyed(int index)
        {
            if (index < 0 || index >= _destroyed.Length || _destroyed[index])
                return false;

            _destroyed[index] = true;

            var next = -1;

            for (var i = 0; i < _destroyed.Length; i++)
            {
                if (!_destroyed[i])
                {
                    next = i;
                    break;
                }
            }

            var before = _current;
            Volatile.Write(ref _current, next);

            return before != next;
        }
    }
}
