using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using ACE.Server.Managers;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Where a template reads its numbers from. A placeholder names a tunable by its FULL key, and the key's
    /// type (long or double) is decided by the DefaultPropertyManager dictionaries, so a reader never has to
    /// guess. <see cref="ClassAbilityText.Live"/> reads PropertyManager exactly the way the handlers do (a
    /// live @modifydouble shows on the next read), <see cref="ClassAbilityText.Defaults"/> reads the code
    /// defaults with no database and no PropertyManager (planner catalog, token SQL regen), and
    /// <see cref="DictionaryTunableValues"/> is the test source.
    /// </summary>
    public interface ITunableValues
    {
        long GetLong(string key);

        double GetDouble(string key);
    }

    /// <summary>
    /// PropertyManager-backed source. Reads the same calls the handlers make, so what a player is shown
    /// cannot differ from what the mechanic uses.
    /// </summary>
    public sealed class LiveTunableValues : ITunableValues
    {
        public long GetLong(string key) => PropertyManager.GetLong(key).Item;

        public double GetDouble(string key) => PropertyManager.GetDouble(key).Item;
    }

    /// <summary>
    /// Code-default source: reads DefaultPropertyManager's dictionaries directly. No database needed.
    /// </summary>
    public sealed class DefaultTunableValues : ITunableValues
    {
        public long GetLong(string key) => DefaultPropertyManager.DefaultLongProperties[key].Item;

        public double GetDouble(string key) => DefaultPropertyManager.DefaultDoubleProperties[key].Item;
    }

    /// <summary>
    /// Test source: overrides win, every other key falls through to the code defaults. Never touches
    /// PropertyManager, so it cannot leak state between test classes or reach the database.
    /// </summary>
    public sealed class DictionaryTunableValues : ITunableValues
    {
        private readonly IReadOnlyDictionary<string, double> overrides;

        public DictionaryTunableValues(IReadOnlyDictionary<string, double> overrides)
        {
            this.overrides = overrides ?? new Dictionary<string, double>();
        }

        public long GetLong(string key) =>
            overrides.TryGetValue(key, out var v) ? (long)Math.Round(v) : DefaultPropertyManager.DefaultLongProperties[key].Item;

        public double GetDouble(string key) =>
            overrides.TryGetValue(key, out var v) ? v : DefaultPropertyManager.DefaultDoubleProperties[key].Item;
    }

    /// <summary>
    /// Renders the player-facing text of a class ability from its templates and the CURRENT tunable values.
    ///
    /// TEMPLATE GRAMMAR. Literal text with placeholders of the form <c>{kind:args@sel|fmt;option=value}</c>. The
    /// vocabulary is closed; anything else in braces (or a stray brace) is a parse error, so raw braces can never
    /// reach a player. Keys are written out in full so a grep for a tunable finds every sentence that quotes it.
    /// The source-scan guard cannot prove WHICH ability reads a key when the declared helper file is shared
    /// (Player_ClassAbilityBuffs.cs satisfies dozens of keys), so the formula audit at review is the real check for those.
    ///
    /// "Rank-shaped" placeholders are ladder, per, ranks and fn: they have one value per rank. A description
    /// renders them over every rank ("6/12/18"); a rank tail renders that one rank's value.
    ///
    ///   {ladder:BASE_KEY,STEP_KEY[,CAP_KEY]|fmt}
    ///                                   base + (rank - 1) * step. With a CAP_KEY each rank's value is clamped to
    ///                                   [0, max(0, cap)], the same expression ManaBarrierAbility.DivertShare
    ///                                   applies, so the text never promises more than the mechanic grants.
    ///   {v:KEY[,CAP_KEY]|fmt}           one tunable, rank independent. With a CAP_KEY the value is clamped to
    ///                                   [0, max(0, cap)] like ladder's.
    ///   {per:KEY|fmt}                   key * rank (a per-rank amount).
    ///   {ranks:PREFIX[,CAP_KEY]|fmt}    the keys PREFIX1..PREFIXn, one per rank. With a CAP_KEY every rank's value
    ///                                   is clamped to [0, max(0, cap)] (one cap key for all ranks).
    ///   {fn:NAME|fmt}                   a rank-shaped value computed by the definition's TextFormulas[NAME]
    ///                                   (Func of ITunableValues and rank). The func must call the SAME pure method
    ///                                   the handler uses, so the text and the mechanic share one formula. NAME is
    ///                                   [a-z0-9_]; every {fn:NAME} must exist in TextFormulas and every
    ///                                   TextFormulas entry must be used (guard tests). Each entry declares the keys it
    ///                                   reads; Keys() reports them so the existing key guards cover them.
    ///                                   A formula is per RANK, not per PLAYER: it cannot see which other abilities the
    ///                                   player owns, so text that depends on such a pairing is exact only for the
    ///                                   pairing the formula assumes (document the assumption at the formula).
    ///   {maxrank}                       the definition's MaxRank. Takes no key, no format.
    ///   {rank}                          the rank number. RANK TAIL ONLY (a parse error in a description). No key, no format.
    ///   {an} / {An}                     the article "a" / "an" (capitalised "A" / "An" for {An}), chosen from the
    ///                                   rendered text of the NEXT placeholder, which must follow immediately or after
    ///                                   one whitespace-only literal ("with {an} {per:KEY|pct}%"). English for
    ///                                   numerals: "an" when the number is read starting with a vowel sound (8, 11, 18,
    ///                                   80-89, 8xx, 8xxx, 11xxx, 18xxx ...), else "a" (1, 100, 110, 0.5, 8.5 reads
    ///                                   "an": the integer part decides). A ladder ("8/16/24") uses its first value. No key, no format.
    ///
    /// SINGLE-RANK SELECTOR. A rank-shaped placeholder takes <c>@N</c> (N = 1..MaxRank) or <c>@max</c> right
    /// after its args: <c>{per:KEY@max|pct}</c> renders only that rank's value inside a description. An N outside
    /// 1..MaxRank, a leading zero (@01), or an N equal to MaxRank (write @max) is a parse error (FormatException
    /// from Keys / Describe / RankTail).
    ///
    /// FORMATTERS (all invariant culture, never a locale).
    ///   pct    value x 100, at most 2 decimals, trailing zeros trimmed. NO percent sign: write it after the
    ///          placeholder, so a ladder reads "6/12/18%".
    ///   pct1   value x 100 with exactly one decimal (0.285 -> "28.5", 0.1 -> "10.0").
    ///   pct3   value x 100, at most 3 decimals, trailing zeros trimmed (0.00025 -> "0.025", 0.0005 -> "0.05").
    ///   int    value rounded to a whole number.
    ///   uint   like int, clamped to [0, uint.MaxValue] the way ManaBarrierAbility.AbsorbDamage clamps a mana floor.
    ///   floor  value rounded DOWN to a whole number, clamped at 0 (a negative value shows 0), for a count the
    ///          handler floors (NetherBloomAbility.JumpCount), where int would round a fractional tunable up.
    ///   num    at most 2 decimals, trailing zeros trimmed.
    ///   f1     exactly one decimal (0.5 -> "0.5", 1 -> "1.0").
    ///   f2     exactly two decimals (2 -> "2.00", 3.1 -> "3.10").
    ///   s_ms   milliseconds shown as seconds, at most 2 decimals.
    ///   min    seconds shown as minutes (value / 60), at most 2 decimals, trailing zeros trimmed (360 -> "6"). Intended
    ///          for whole or half minutes: other values render a 2-decimal fraction (100 s -> "1.67", 45 s -> "0.75").
    ///   x      a multiplier: at most 2 decimals followed by "x".
    ///
    /// OPTIONS, after the formatter, separated by ';' (each at most once, order free).
    ///   join=slash|spaced|then   how a description joins the per-rank values of a rank-shaped placeholder:
    ///                            slash "5/10/15" (the default, byte-identical to a template with no option),
    ///                            spaced "5 / 10 / 15", then "0.5, then 1.0, then 1.5". Not allowed with @N/@max
    ///                            (nothing to join).
    ///   unit=%                   append "%" to EVERY value ("5% / 10% / 15%"), for ladders whose text repeats the
    ///                            sign. Allowed on {v}, {ladder}, {per}, {ranks}, {fn}.
    /// </summary>
    public static class ClassAbilityText
    {
        public static readonly ITunableValues Live = new LiveTunableValues();

        public static readonly ITunableValues Defaults = new DefaultTunableValues();

        public static readonly IReadOnlyList<string> Kinds = new[] { "ladder", "v", "per", "ranks", "fn", "maxrank", "rank", "an" };

        public static readonly IReadOnlyList<string> Formatters = new[] { "pct", "pct1", "pct3", "int", "uint", "floor", "num", "f1", "f2", "s_ms", "min", "x" };

        public static readonly IReadOnlyList<string> JoinStyles = new[] { "slash", "spaced", "then" };

        private enum Kind { Ladder, V, Per, Ranks, Fn, MaxRank, Rank, An }

        private sealed class Segment
        {
            public string Literal;          // non-null for a literal segment
            public Kind Kind;
            public string[] Args;
            public string Format;
            public int Select;              // 0 = none, -1 = @max, N = @N
            public string Join = "/";
            public string Unit = string.Empty;
            public bool HasJoin;
            public bool Capital;            // {An}

            public bool IsRankShaped => Kind == Kind.Ladder || Kind == Kind.Per || Kind == Kind.Ranks || Kind == Kind.Fn;
        }

        private static readonly ConcurrentDictionary<string, Segment[]> parsed =
            new ConcurrentDictionary<string, Segment[]>(StringComparer.Ordinal);

        // ---------------------------------------------------------------------------------------------
        // public surface
        // ---------------------------------------------------------------------------------------------

        /// <summary>The /abilities info and token lead text: every rank's value, "a/b/c".</summary>
        public static string Describe(ClassAbilityDefinition def, ITunableValues values)
        {
            if (def?.DescriptionTemplate == null)
                return null;

            return Render(def.DescriptionTemplate, def, null, values);
        }

        /// <summary>
        /// One rank's increment, the text a token shows after "This token teaches rank N: " (without the
        /// closing period). Null when the ability has no tail template.
        /// </summary>
        public static string RankTail(ClassAbilityDefinition def, int rank, ITunableValues values)
        {
            if (def?.RankTailTemplate == null)
                return null;

            if (rank < 1 || rank > def.MaxRank)
                throw new ArgumentOutOfRangeException(nameof(rank), rank, $"{def.Name} has ranks 1-{def.MaxRank}");

            return Render(def.RankTailTemplate.For(rank, def.MaxRank, def.Name), def, rank, values);
        }

        /// <summary>
        /// The full token LongDesc: "&lt;DisplayName&gt; - &lt;description&gt;" (followed by " Affinity: X." when
        /// the ability has an affinity) for a single-rank ability, or that plus
        /// " This token teaches rank N: &lt;tail&gt;." for a multi-rank one (just " This token teaches rank N."
        /// when it has no tail template). Null only when the ability has no description template: the caller
        /// then leaves the committed text alone. This is the one source of every token's LongDesc, generated
        /// families included.
        /// </summary>
        public static string TokenLongDesc(ClassAbilityDefinition def, int tier, ITunableValues values)
        {
            var description = Describe(def, values);
            if (description == null)
                return null;

            var lead = $"{def.DisplayName} - {description}";

            // The affinity line follows the description as one more sentence (a token's LongDesc is a single
            // space-joined line), so every consumer of the affinity reads it from AffinityLine.
            var affinity = AffinityLine(def);
            if (affinity != null)
                lead += $" {affinity}.";

            if (def.MaxRank == 1)
                return lead;

            // An ability with no tail template still gets a line: the description, then the rank the token teaches.
            var tail = RankTail(def, tier, values);
            if (tail == null)
                return $"{lead} This token teaches rank {tier}.";

            return $"{lead} This token teaches rank {tier}: {tail}.";
        }

        /// <summary>One "Rank N: tail" line per rank; empty for an ability with no tail template.</summary>
        public static IReadOnlyList<string> RankLines(ClassAbilityDefinition def, ITunableValues values)
        {
            var lines = new List<string>();

            if (def?.RankTailTemplate == null)
                return lines;

            for (var rank = 1; rank <= def.MaxRank; rank++)
                lines.Add($"Rank {rank}: {RankTail(def, rank, values)}");

            return lines;
        }

        /// <summary>
        /// The skills that scale the ability as display names: the primary affinity skill first, then any
        /// additional ones, deduplicated. Empty when the ability has no affinity. Skill display names only
        /// (never "Mastery"). The one source for the feed's affinity array and <see cref="AffinityLine"/>.
        /// </summary>
        public static IReadOnlyList<string> AffinityNames(ClassAbilityDefinition def)
        {
            var skills = new List<ACE.Entity.Enum.Skill>();
            if (def.AffinitySkill.HasValue) skills.Add(def.AffinitySkill.Value);
            if (def.AdditionalAffinitySkills != null) skills.AddRange(def.AdditionalAffinitySkills);
            return skills.Distinct().Select(s => ACE.Entity.Enum.SkillExtensions.ToSentence(s)).ToList();
        }

        /// <summary>
        /// The affinity line shown under an ability's description: "Affinity: Run" or "Affinity: Leadership,
        /// Loyalty". Null when the ability has no affinity, so a caller skips the line.
        /// </summary>
        public static string AffinityLine(ClassAbilityDefinition def)
        {
            if (def == null)
                return null;

            var names = AffinityNames(def);
            return names.Count == 0 ? null : "Affinity: " + string.Join(", ", names);
        }

        /// <summary>
        /// The AllowedLiterals entries no template uses any more: an entry is used only when its text appears
        /// inside a literal stretch of the description or a tail. A stale entry silently exempts that text from
        /// the bare-number guard if it is ever typed again, so a guard test requires this to be empty.
        /// </summary>
        public static IReadOnlyList<string> UnusedAllowedLiterals(ClassAbilityDefinition def)
        {
            var literals = Templates(def).SelectMany(t => Parse(t.Template)).Where(s => s.Literal != null).Select(s => s.Literal).ToList();

            return (def.AllowedLiterals ?? Array.Empty<(string Literal, string Reason)>())
                .Select(a => a.Literal)
                .Where(allowed => !literals.Any(text => text.Contains(allowed, StringComparison.Ordinal)))
                .ToList();
        }

        /// <summary>True when either template contains at least one placeholder.</summary>
        public static bool HasPlaceholders(ClassAbilityDefinition def) =>
            Templates(def).Any(t => Parse(t.Template).Any(s => s.Literal == null));

        /// <summary>
        /// Every tunable key the definition's templates name, for the guard tests. Throws FormatException
        /// when a template does not parse or fails the definition-aware checks (selector range, {rank} in a
        /// description, {fn} not in TextFormulas, {an} without a following placeholder).
        /// </summary>
        public static IReadOnlyList<string> Keys(ClassAbilityDefinition def)
        {
            var keys = new List<string>();

            foreach (var (template, isTail) in Templates(def))
            {
                var segments = Parse(template);
                Check(segments, def, isTail, template);

                foreach (var segment in segments.Where(s => s.Literal == null))
                {
                    switch (segment.Kind)
                    {
                        case Kind.Ladder:
                        case Kind.V:
                        case Kind.Per:
                            keys.AddRange(segment.Args);
                            break;
                        case Kind.Ranks:
                            for (var rank = 1; rank <= def.MaxRank; rank++)
                                keys.Add(segment.Args[0] + rank.ToString(CultureInfo.InvariantCulture));
                            keys.AddRange(segment.Args.Skip(1));
                            break;
                        case Kind.Fn:
                            keys.AddRange(def.TextFormulas[segment.Args[0]].Keys);
                            break;
                    }
                }
            }

            return keys.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// The names every {fn:NAME} in the definition's templates uses, for the guard tests (every one must exist
        /// in TextFormulas, and every TextFormulas entry must appear here).
        /// </summary>
        public static IReadOnlyList<string> FormulaNames(ClassAbilityDefinition def)
        {
            var names = new List<string>();

            foreach (var (template, _) in Templates(def))
                names.AddRange(Parse(template).Where(s => s.Literal == null && s.Kind == Kind.Fn).Select(s => s.Args[0]));

            return names.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Digit runs left in the LITERAL parts of the templates after removing the definition's declared
        /// allowed literals. A number typed into a migrated template is a copy of a tunable that will go
        /// stale, which is the whole defect this renderer exists to remove.
        /// </summary>
        public static IReadOnlyList<string> BareNumbers(ClassAbilityDefinition def)
        {
            var found = new List<string>();

            foreach (var (template, _) in Templates(def))
            {
                foreach (var segment in Parse(template).Where(s => s.Literal != null))
                {
                    var text = segment.Literal;

                    if (def.AllowedLiterals != null)
                    {
                        foreach (var allowed in def.AllowedLiterals)
                            text = text.Replace(allowed.Literal, string.Empty);
                    }

                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\d+"))
                        found.Add(m.Value);
                }
            }

            return found;
        }

        /// <summary>
        /// The article for a rendered number: "an" when it is read starting with a vowel sound, else "a". Only the
        /// leading number is read (so a ladder "8/16/24" answers for 8), and only its integer part.
        /// </summary>
        public static string Article(string renderedText)
        {
            return StartsWithVowelSound(renderedText) ? "an" : "a";
        }

        // ---------------------------------------------------------------------------------------------
        // rendering
        // ---------------------------------------------------------------------------------------------

        private static IEnumerable<(string Template, bool IsTail)> Templates(ClassAbilityDefinition def)
        {
            if (def?.DescriptionTemplate != null)
                yield return (def.DescriptionTemplate, false);

            if (def?.RankTailTemplate != null)
            {
                foreach (var tail in def.RankTailTemplate.All(def.MaxRank, def.Name))
                    yield return (tail, true);
            }
        }

        private static string Render(string template, ClassAbilityDefinition def, int? rank, ITunableValues values)
        {
            var segments = Parse(template);

            // Fast path: no placeholders, return the authored text untouched.
            if (segments.Length == 1 && segments[0].Literal != null)
                return segments[0].Literal;

            Check(segments, def, rank.HasValue, template);

            var outs = new string[segments.Length];

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];

                if (segment.Literal != null)
                {
                    outs[i] = segment.Literal;
                    continue;
                }

                switch (segment.Kind)
                {
                    case Kind.MaxRank:
                        outs[i] = def.MaxRank.ToString(CultureInfo.InvariantCulture);
                        break;
                    case Kind.Rank:
                        outs[i] = rank.Value.ToString(CultureInfo.InvariantCulture);
                        break;
                    case Kind.An:
                        break;      // second pass, once the following placeholder is rendered
                    case Kind.V:
                        outs[i] = Format(Capped(Read(values, segment.Args[0]), segment.Args, values), segment.Format) + segment.Unit;
                        break;
                    default:
                    {
                        int[] ranks;

                        if (segment.Select != 0)
                            ranks = new[] { segment.Select == -1 ? def.MaxRank : segment.Select };
                        else
                            ranks = rank.HasValue ? new[] { rank.Value } : Enumerable.Range(1, def.MaxRank).ToArray();

                        outs[i] = string.Join(segment.Join, ranks.Select(r => Format(RankValue(segment, def, r, values), segment.Format) + segment.Unit));
                        break;
                    }
                }
            }

            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i].Literal != null || segments[i].Kind != Kind.An)
                    continue;

                var next = NextValueIndex(segments, i);
                var article = Article(outs[next]);
                outs[i] = segments[i].Capital ? char.ToUpperInvariant(article[0]) + article.Substring(1) : article;
            }

            return string.Concat(outs);
        }

        // index of the placeholder an {an} at i modifies: the next segment, or the one after a whitespace-only literal
        private static int NextValueIndex(Segment[] segments, int i)
        {
            var j = i + 1;

            if (j < segments.Length && segments[j].Literal != null && segments[j].Literal.Trim().Length == 0)
                j++;

            return j;
        }

        // definition-aware checks the cached, definition-free parse cannot make
        private static void Check(Segment[] segments, ClassAbilityDefinition def, bool isTail, string template)
        {
            for (var i = 0; i < segments.Length; i++)
            {
                var s = segments[i];

                if (s.Literal != null)
                    continue;

                if (s.Kind == Kind.Rank && !isTail)
                    throw new FormatException($"{{rank}} is only valid in a rank tail, not a description, in class ability template [{template}]");

                if (s.Select == def.MaxRank)
                    throw new FormatException($"selector @{s.Select} is {def.Name}'s top rank: use @max in class ability template [{template}]");

                if (s.Select > def.MaxRank)
                    throw new FormatException($"selector @{s.Select} is outside ranks 1-{def.MaxRank} of {def.Name} in class ability template [{template}]");

                if (s.Kind == Kind.Fn && (def.TextFormulas == null || !def.TextFormulas.ContainsKey(s.Args[0])))
                    throw new FormatException($"{{fn:{s.Args[0]}}} has no entry in {def.Name}'s TextFormulas in class ability template [{template}]");

                if (s.Kind == Kind.An)
                {
                    var next = NextValueIndex(segments, i);

                    if (next >= segments.Length || segments[next].Literal != null || segments[next].Kind == Kind.An)
                        throw new FormatException($"{{an}} must be immediately followed by a value placeholder in class ability template [{template}]");
                }
            }
        }

        private static double Capped(double value, string[] args, ITunableValues values) =>
            args.Length > 1 ? Math.Clamp(value, 0.0, Math.Max(0.0, Read(values, args[1]))) : value;

        private static double RankValue(Segment segment, ClassAbilityDefinition def, int rank, ITunableValues values)
        {
            switch (segment.Kind)
            {
                case Kind.Ladder:
                {
                    var ladder = Read(values, segment.Args[0]) + (rank - 1) * Read(values, segment.Args[1]);
                    return segment.Args.Length > 2 ? Math.Clamp(ladder, 0.0, Math.Max(0.0, Read(values, segment.Args[2]))) : ladder;
                }
                case Kind.Per:
                    return Read(values, segment.Args[0]) * rank;
                case Kind.Ranks:
                    return Capped(Read(values, segment.Args[0] + rank.ToString(CultureInfo.InvariantCulture)), segment.Args, values);
                case Kind.Fn:
                {
                    var result = def.TextFormulas[segment.Args[0]].Compute(values, rank);

                    if (double.IsNaN(result) || double.IsInfinity(result))
                        throw new FormatException($"{def.Name}'s text formula '{segment.Args[0]}' returned {result} at rank {rank}");

                    return result;
                }
                default:
                    throw new InvalidOperationException(segment.Kind.ToString());
            }
        }

        /// <summary>
        /// Reads one tunable as a double. The key's type comes from the defaults dictionaries: a long key is
        /// read with GetLong, a double key with GetDouble. An unknown key throws, which the guard tests
        /// catch long before a player could.
        /// </summary>
        public static double Read(ITunableValues values, string key)
        {
            if (DefaultPropertyManager.DefaultLongProperties.ContainsKey(key))
                return values.GetLong(key);

            if (DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(key))
                return values.GetDouble(key);

            throw new ArgumentException($"class ability text names tunable '{key}', which is neither a long nor a double default");
        }

        public static string Format(double value, string format)
        {
            var inv = CultureInfo.InvariantCulture;

            switch (format)
            {
                case "pct":
                    return (Math.Round(value * 100.0, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("0.##", inv);
                case "pct1":
                    return (Math.Round(value * 100.0, 1, MidpointRounding.AwayFromZero) + 0.0).ToString("0.0", inv);
                case "pct3":
                    return (Math.Round(value * 100.0, 3, MidpointRounding.AwayFromZero) + 0.0).ToString("0.###", inv);
                case "int":
                    return (Math.Round(value, 0, MidpointRounding.AwayFromZero) + 0.0).ToString("0", inv);
                case "uint":
                    return (Math.Clamp(Math.Round(value, 0, MidpointRounding.AwayFromZero), 0.0, (double)uint.MaxValue) + 0.0).ToString("0", inv);
                case "floor":
                    return (Math.Clamp(Math.Floor(value), 0.0, (double)uint.MaxValue) + 0.0).ToString("0", inv);
                case "num":
                    return (Math.Round(value, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("0.##", inv);
                case "f1":
                    return (Math.Round(value, 1, MidpointRounding.AwayFromZero) + 0.0).ToString("0.0", inv);
                case "f2":
                    return (Math.Round(value, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("0.00", inv);
                case "s_ms":
                    return (Math.Round(value / 1000.0, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("0.##", inv);
                case "min":
                    return (Math.Round(value / 60.0, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("0.##", inv);
                case "x":
                    return (Math.Round(value, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("0.##", inv) + "x";
                default:
                    throw new FormatException($"unknown class ability text formatter '{format}'");
            }
        }

        // English reading of a numeral: does the integer part start with a vowel sound?
        // "eight", "eleven", "eighteen", "eighty-x", "eight hundred", "eight thousand", "eleven thousand" ... -> yes.
        private static bool StartsWithVowelSound(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            var i = 0;
            while (i < text.Length && (text[i] == '-' || text[i] == '+'))
                i++;

            // a signed number ("-8", "+8") is read "minus eight" / "plus eight": a consonant either way
            if (i > 0)
                return false;

            // 1100 / 1800 are read "one thousand one hundred", not "eleven hundred" (the thousands grouping decides),
            // so they take "a"; the same grouping makes 11xxx / 18xxx "eleven / eighteen thousand" -> "an".
            var start = i;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9')
                i++;

            var digits = text.Substring(start, i - start).TrimStart('0');

            if (digits.Length == 0)
                return false;

            // the leading group of the thousands grouping is what is spoken first
            var lead = digits.Length % 3 == 0 ? 3 : digits.Length % 3;
            var group = int.Parse(digits.Substring(0, lead), CultureInfo.InvariantCulture);

            if (group >= 100)
                return group / 100 == 8;

            if (group >= 10)
                return group == 11 || group == 18 || (group >= 80 && group <= 89);

            return group == 8;
        }

        // ---------------------------------------------------------------------------------------------
        // parsing
        // ---------------------------------------------------------------------------------------------

        private static Segment[] Parse(string template) => parsed.GetOrAdd(template, ParseUncached);

        private static Segment[] ParseUncached(string template)
        {
            var result = new List<Segment>();
            var literal = new StringBuilder();
            var i = 0;

            void FlushLiteral()
            {
                if (literal.Length > 0)
                {
                    result.Add(new Segment { Literal = literal.ToString() });
                    literal.Clear();
                }
            }

            while (i < template.Length)
            {
                var c = template[i];

                if (c == '}')
                    throw new FormatException($"stray '}}' at {i} in class ability template [{template}]");

                if (c != '{')
                {
                    literal.Append(c);
                    i++;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                    throw new FormatException($"unterminated '{{' at {i} in class ability template [{template}]");

                var body = template.Substring(i + 1, close - i - 1);
                if (body.Contains('{'))
                    throw new FormatException($"nested '{{' at {i} in class ability template [{template}]");

                FlushLiteral();
                result.Add(ParsePlaceholder(body, template));
                i = close + 1;
            }

            FlushLiteral();

            if (result.Count == 0)
                result.Add(new Segment { Literal = string.Empty });

            return result.ToArray();
        }

        private static Segment ParsePlaceholder(string body, string template)
        {
            var format = (string)null;
            string[] options = Array.Empty<string>();
            var pipe = body.IndexOf('|');
            if (pipe >= 0)
            {
                var tail = body.Substring(pipe + 1).Split(';');
                format = tail[0];
                options = tail.Skip(1).ToArray();
                body = body.Substring(0, pipe);
            }

            var select = 0;
            var at = body.IndexOf('@');
            if (at >= 0)
            {
                var sel = body.Substring(at + 1);
                body = body.Substring(0, at);

                if (sel == "max")
                    select = -1;
                else if (sel.Length > 0 && sel[0] != '0' && sel.All(char.IsDigit) && int.TryParse(sel, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1)
                    select = n;
                else
                    throw new FormatException($"selector '@{sel}' must be @max or @N with N >= 1 in class ability template [{template}]");
            }

            var colon = body.IndexOf(':');
            var kindName = colon >= 0 ? body.Substring(0, colon) : body;
            var args = colon >= 0 ? body.Substring(colon + 1).Split(',') : Array.Empty<string>();

            var capital = kindName == "An";
            var lookup = capital ? "an" : kindName;

            if (!Enum.TryParse<Kind>(lookup, true, out var kind) || !Kinds.Contains(lookup, StringComparer.Ordinal))
                throw new FormatException($"unknown placeholder kind '{kindName}' in class ability template [{template}]");

            var wantedArgs = kind == Kind.Ladder ? 2 : (kind == Kind.MaxRank || kind == Kind.Rank || kind == Kind.An) ? 0 : 1;
            var maxArgs = kind == Kind.Ladder ? 3 : (kind == Kind.V || kind == Kind.Ranks) ? 2 : wantedArgs;
            if (args.Length < wantedArgs || args.Length > maxArgs || args.Any(a => a.Length == 0 || a.Any(ch => !(char.IsLower(ch) || char.IsDigit(ch) || ch == '_'))))
                throw new FormatException($"placeholder '{kindName}' takes {(maxArgs == wantedArgs ? wantedArgs.ToString() : wantedArgs + "-" + maxArgs)} key(s) of [a-z0-9_] in class ability template [{template}]");

            var segment = new Segment { Kind = kind, Args = args, Format = format, Select = select, Capital = capital };

            if (kind == Kind.MaxRank || kind == Kind.Rank || kind == Kind.An)
            {
                if (format != null || select != 0)
                    throw new FormatException($"{{{kindName}}} takes no format or selector in class ability template [{template}]");
            }
            else if (format == null || !Formatters.Contains(format, StringComparer.Ordinal))
            {
                throw new FormatException($"placeholder '{kindName}' needs one of the formatters [{string.Join(", ", Formatters)}], got '{format}', in class ability template [{template}]");
            }

            if (select != 0 && !segment.IsRankShaped)
                throw new FormatException($"selector @ is only valid on ladder, per, ranks and fn, not '{kindName}', in class ability template [{template}]");

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var option in options)
            {
                var eq = option.IndexOf('=');
                var name = eq > 0 ? option.Substring(0, eq) : option;
                var value = eq > 0 ? option.Substring(eq + 1) : string.Empty;

                if (!seen.Add(name))
                    throw new FormatException($"option '{name}' given twice in class ability template [{template}]");

                switch (name)
                {
                    case "join":
                        if (!segment.IsRankShaped)
                            throw new FormatException($"join= is only valid on ladder, per, ranks and fn, not '{kindName}', in class ability template [{template}]");
                        if (select != 0)
                            throw new FormatException($"join= is meaningless with a single-rank selector in class ability template [{template}]");
                        segment.Join = value switch
                        {
                            "slash" => "/",
                            "spaced" => " / ",
                            "then" => ", then ",
                            _ => throw new FormatException($"join style '{value}' must be one of [{string.Join(", ", JoinStyles)}] in class ability template [{template}]"),
                        };
                        segment.HasJoin = true;
                        break;
                    case "unit":
                        if (value != "%")
                            throw new FormatException($"unit '{value}' must be % in class ability template [{template}]");
                        segment.Unit = value;
                        break;
                    default:
                        throw new FormatException($"unknown option '{name}' (known: join, unit) in class ability template [{template}]");
                }
            }

            return segment;
        }
    }
}
