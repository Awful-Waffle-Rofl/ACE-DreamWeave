using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The free spell grant at queue join, with an idempotent safety net before the template capture (Docs/Pvp/TEMPLATES.md "Free spells for PvP"): every level 7 War Magic and
    /// Life Magic spell the Omni-School Professor teaches, plus Curse of Raven Fury. Pure decision: the caller supplies how a spell is
    /// added (true only when newly added) and how the client is told, so it runs without a World database or a Session.
    /// </summary>
    internal static class PvpMatchSpellGrant
    {
        internal const int GrantedLevel = 7;
        internal static readonly string[] GrantedSchools = { "war", "life" };

        /// <summary>Where the empty-list warning goes. A seam so a test can capture it.</summary>
        internal static Action<string> WarnSink = message => PvpArenaHookSettings.Log.Warn(message);

        private static readonly HashSet<string> warnedSchools = new HashSet<string>();

        /// <summary>Forgets which schools were warned about (tests only).</summary>
        internal static void ResetWarned()
        {
            lock (warnedSchools)
                warnedSchools.Clear();
        }

        /// <summary>
        /// A professor list comes back empty when SpellTutor's offer for that school is null (missing TeachSpell or price data), and
        /// that is cached for the process, so it stays empty until a restart. Said once per school so the log is not flooded per join.
        /// </summary>
        private static void WarnEmptyOnce(string school)
        {
            lock (warnedSchools)
            {
                if (!warnedSchools.Add(school))
                    return;
            }

            WarnSink($"[PVP] level {GrantedLevel} {school} professor list is empty; the PvP grant teaches none of that school.");
        }

        /// <summary>The chat line for <paramref name="taught"/> newly taught spells (grammatical at 1).</summary>
        internal static string TaughtMessage(int taught)
            => $"You have been taught {taught} War and Life Magic {(taught == 1 ? "spell" : "spells")} for PvP. They stay in your spellbook.";

        /// <summary>The ids the grant covers, in order and distinct: the professors' level 7 lists, then Curse of Raven Fury.</summary>
        internal static List<uint> SpellIds(Func<string, int, IReadOnlyList<uint>> listSource)
        {
            var ids = new List<uint>();

            foreach (var school in GrantedSchools)
            {
                var list = listSource(school, GrantedLevel) ?? Array.Empty<uint>();

                if (list.Count == 0)
                    WarnEmptyOnce(school);

                ids.AddRange(list);
            }

            ids.Add((uint)SpellId.CurseRavenFury);

            return ids.Distinct().ToList();
        }

        /// <summary>
        /// Grants every covered spell not already known. Returns how many were newly added (0 when the setting is off).
        /// idempotent: a known spell is skipped by addKnown returning false.
        /// </summary>
        internal static int Grant(bool enabled, Func<string, int, IReadOnlyList<uint>> listSource, Func<uint, bool> addKnown, Action<uint> notify)
        {
            if (!enabled)
                return 0;

            var taught = 0;

            foreach (var id in SpellIds(listSource))
            {
                if (!addKnown(id))
                    continue;

                taught++;
                notify(id);
            }

            return taught;
        }
    }
}
