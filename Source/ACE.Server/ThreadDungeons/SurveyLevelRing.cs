using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The character's rolling ring of the GEM LEVELS of their last <see cref="Depth"/> filed daily surveys,
    /// newest first, persisted as one PropertyString (DungeonSurveyLevels, 9016). This is what lets the
    /// Survey-Archivist pay a reward scaled to the difficulty actually run rather than to a constant derived
    /// from the level cap: a level-275 character farming cheap level-185 gems is paid at the level-185 rate.
    ///
    /// Format v1:  v1|lv=&lt;n&gt;,&lt;n&gt;,...   - 0..<see cref="Depth"/> entries, each 1..DungeonGemSpec.MaxLevel,
    /// NEWEST FIRST. Serialize ALWAYS emits the lv= field even when the ring is empty, so there is exactly one
    /// canonical on-character form (the same all-or-nothing codec discipline DungeonGemSpec uses).
    ///
    /// Owner ruling R1: the difficulty measure is the GEM level, not the levels of the creatures a run
    /// actually spawned. Owner ruling R2: this is a rolling last-N that spans windows, not a window-scoped
    /// running sum - tier 1 averages the newest 1 entry, tier 5 the newest 5, tier 10 the newest 10.
    /// Owner ruling R3: the depth is exactly 10.
    ///
    /// PURE by construction: no PropertyManager, no DatManager, no Player. PropertyManager reads throw under
    /// the unit-test harness and the harness cannot build a live Player, so an impure core is an untestable
    /// core. The one engine dependency is log4net, which is inert (and safe) without configuration.
    /// </summary>
    public sealed class SurveyLevelRing
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Owner ruling R3. The ONE place this number is written; nothing else may spell a literal 10.</summary>
        public const int Depth = 10;

        public const int CurrentVersion = 1;

        /// <summary>The field name carrying the comma-separated levels. Always emitted, even when empty.</summary>
        private const string LevelsField = "lv";

        /// <summary>The ring of a character who has never filed a survey, or whose stored value was unreadable.</summary>
        public static readonly SurveyLevelRing Empty = new SurveyLevelRing(Array.Empty<int>());

        /// <summary>Gem levels, NEWEST FIRST, at most <see cref="Depth"/> of them.</summary>
        public IReadOnlyList<int> Levels { get; }

        public int Count => Levels.Count;

        public bool IsEmpty => Levels.Count == 0;

        private SurveyLevelRing(IReadOnlyList<int> levels)
        {
            Levels = levels;
        }

        /// <summary>
        /// Builds a ring from levels given NEWEST FIRST. Each level is clamped into the legal range and the
        /// list is truncated to <see cref="Depth"/>, so a ring built this way can always be serialized and
        /// parsed back unchanged.
        /// </summary>
        public static SurveyLevelRing FromLevels(IEnumerable<int> newestFirst)
        {
            if (newestFirst == null)
                return Empty;

            var levels = newestFirst.Select(Clamp).Take(Depth).ToArray();

            return levels.Length == 0 ? Empty : new SurveyLevelRing(levels);
        }

        /// <summary>
        /// Reads a stored ring. NEVER throws and never refuses a character their reward:
        ///
        ///   null / empty / whitespace  -> empty ring, SILENTLY. This is the pre-feature character, and by
        ///                                 owner ruling R8 an empty ring pays the unscaled reward.
        ///   anything malformed         -> empty ring plus ONE warning. The next Push rewrites the property
        ///                                 clean, so a corrupt value is self-healing rather than sticky.
        ///   more than Depth entries    -> truncated to the newest Depth rather than refused, so LOWERING
        ///                                 the depth later cannot brick a character who stored more.
        /// </summary>
        public static SurveyLevelRing Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Empty;

            if (!TryParse(text, out var ring, out var error))
            {
                log.Warn($"[DYNDUNGEON] unreadable survey level ring '{text}': {error}; reading it as empty (the next filed survey rewrites it)");
                return Empty;
            }

            return ring;
        }

        /// <summary>
        /// The strict half of <see cref="Parse"/>, so tests can assert exactly WHY a value was rejected
        /// without reading a log. Returns false with a reason for any malformed value; an over-length but
        /// otherwise legal list is truncated and returns TRUE.
        /// </summary>
        public static bool TryParse(string text, out SurveyLevelRing ring, out string error)
        {
            ring = null;
            error = null;

            if (string.IsNullOrWhiteSpace(text)) { ring = Empty; return true; }

            var parts = text.Split('|');

            if (parts[0].Length < 2 || parts[0][0] != 'v'
                || !int.TryParse(parts[0].Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
                || version != CurrentVersion)
            { error = $"unsupported version '{parts[0]}'"; return false; }

            var kv = new Dictionary<string, string>();
            foreach (var part in parts.Skip(1))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) { error = $"malformed field '{part}'"; return false; }
                var key = part.Substring(0, eq);
                if (kv.ContainsKey(key)) { error = $"duplicate field '{key}'"; return false; }
                kv[key] = part.Substring(eq + 1);
            }

            foreach (var key in kv.Keys)
                if (key != LevelsField) { error = $"unknown field '{key}'"; return false; }

            if (!kv.TryGetValue(LevelsField, out var levelsText))
            { error = $"missing field '{LevelsField}'"; return false; }

            var levels = new List<int>();
            if (levelsText.Length > 0)
            {
                foreach (var item in levelsText.Split(','))
                {
                    if (!int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
                    { error = $"level '{item}' is not an integer"; return false; }

                    if (level < 1 || level > DungeonGemSpec.MaxLevel)
                    { error = $"level '{item}' must be 1..{DungeonGemSpec.MaxLevel}"; return false; }

                    levels.Add(level);
                }
            }

            // Over-length is TRUNCATED, not refused: entries are newest first, so keeping the head keeps the
            // most recent surveys, and a future depth reduction degrades instead of bricking the character.
            if (levels.Count > Depth)
                levels.RemoveRange(Depth, levels.Count - Depth);

            ring = levels.Count == 0 ? Empty : new SurveyLevelRing(levels.ToArray());
            return true;
        }

        /// <summary>
        /// Records one newly filed survey: the new level goes to the FRONT and the oldest entry falls off the
        /// back once the ring is full. Returns a new ring; this type is immutable.
        /// </summary>
        public SurveyLevelRing Push(int level)
        {
            var next = new List<int>(Depth) { Clamp(level) };

            for (var i = 0; i < Levels.Count && next.Count < Depth; i++)
                next.Add(Levels[i]);

            return new SurveyLevelRing(next.ToArray());
        }

        /// <summary>Always emits the v1 form, INCLUDING an empty lv=, so there is one canonical form.</summary>
        public string Serialize()
        {
            var sb = new StringBuilder();

            sb.Append('v').Append(CurrentVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append('|').Append(LevelsField).Append('=');
            sb.Append(string.Join(",", Levels.Select(l => l.ToString(CultureInfo.InvariantCulture))));

            return sb.ToString();
        }

        public override string ToString() => Serialize();

        private static int Clamp(int level) => Math.Clamp(level, 1, DungeonGemSpec.MaxLevel);
    }
}
