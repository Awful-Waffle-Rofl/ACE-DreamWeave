using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Entity.Enum;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>The four /pvptemplate kit operations (Docs/Pvp/TEMPLATES.md "Commands").</summary>
    public enum PvpKitOpKind
    {
        /// <summary><c>A=B[xN]</c>: every possession of wcid A becomes a fresh B in the same place.</summary>
        Replace,
        /// <summary><c>~W</c>: every possession of wcid W is rebuilt from the current weenie, in place.</summary>
        Recreate,
        /// <summary><c>+B[xN]</c>: fresh B into the main pack, split into full stacks.</summary>
        Add,
        /// <summary><c>-W</c>: every possession of wcid W is deleted.</summary>
        Delete,
    }

    /// <summary>
    /// One parsed op. <see cref="SourceWcid"/> is the wcid read from the character (0 for Add); <see cref="TargetWcid"/> the
    /// weenie created (0 for Delete; equal to SourceWcid for Recreate). <see cref="Count"/> is the xN, or null.
    /// </summary>
    public sealed record PvpKitOp(PvpKitOpKind Kind, uint SourceWcid, uint TargetWcid, int? Count, string Text);

    /// <summary>One possession of the character, as the planner sees it (read from the runtime biota, never the EF entity).</summary>
    public sealed class PvpKitPossession
    {
        public uint Guid { get; set; }
        public uint Wcid { get; set; }
        public string Name { get; set; }
        public int StackSize { get; set; } = 1;
        public uint? ContainerId { get; set; }
        public uint? OwnerId { get; set; }
        public uint? WielderId { get; set; }
        /// <summary>CurrentWieldedLocation when worn, else null.</summary>
        public int? WieldLocation { get; set; }
        public int? PlacementPosition { get; set; }
        /// <summary>WeenieType.Container: a side pack, which has contents of its own.</summary>
        public bool IsContainer { get; set; }
        /// <summary>RequiresBackpackSlot (a focus): it sits in a pack slot, not an item slot.</summary>
        public bool RequiresPackSlot { get; set; }

        public bool IsWorn => WieldLocation.HasValue && WieldLocation.Value != 0;
    }

    /// <summary>What the planner needs from a weenie.</summary>
    public sealed class PvpKitWeenieInfo
    {
        public uint Wcid { get; set; }
        public string Name { get; set; }
        /// <summary>MaxStackSize; null or below 2 means one per object.</summary>
        public int? MaxStackSize { get; set; }
        /// <summary>ValidLocations (EquipMask bits); 0 when it cannot be worn.</summary>
        public int ValidLocations { get; set; }
        public bool IsContainer { get; set; }
        public bool RequiresPackSlot { get; set; }

        public int StackLimit => MaxStackSize.HasValue && MaxStackSize.Value > 1 ? MaxStackSize.Value : 1;
    }

    /// <summary>
    /// One object to create. Worn: <see cref="WielderId"/> and <see cref="WieldLocation"/>. In a pack: <see cref="ContainerId"/>,
    /// <see cref="OwnerId"/> and <see cref="PlacementPosition"/>. <see cref="ReplacesGuid"/> is the possession it stands in for
    /// (null for an Add).
    /// </summary>
    public sealed class PvpKitCreation
    {
        public int OpIndex { get; set; }
        public uint Wcid { get; set; }
        public int StackSize { get; set; } = 1;
        public uint? ContainerId { get; set; }
        public uint? OwnerId { get; set; }
        public uint? WielderId { get; set; }
        public int? WieldLocation { get; set; }
        public int? PlacementPosition { get; set; }
        public uint? ReplacesGuid { get; set; }
    }

    /// <summary>
    /// The plan. <see cref="Problems"/> non-empty = refused, and a refused plan carries NO writes (Creations and Removals are
    /// emptied). <see cref="Lines"/> is one line per op in the order given.
    /// </summary>
    public sealed class PvpKitPlan
    {
        public List<string> Problems { get; } = new List<string>();
        public List<string> Lines { get; } = new List<string>();
        public List<PvpKitCreation> Creations { get; } = new List<PvpKitCreation>();
        public List<uint> Removals { get; } = new List<uint>();

        public bool Ok => Problems.Count == 0;
    }

    /// <summary>
    /// /pvptemplate kit, the pure half: the argument split, the op parser and the planner (ops + possessions + weenies -> planned
    /// removals and creations). Every rule is checked before anything is planned for writing, and a plan with any problem carries no
    /// writes at all. No database, no world, no clock: PvpTemplateKitEditService does the reading and writing.
    /// </summary>
    public static class PvpTemplateKitEdit
    {
        /// <summary>The largest xN accepted.</summary>
        public const int MaxCount = 1_000_000;

        /// <summary>The main pack's item slots when the character carries no ItemCapacity of its own (a player's weenie value).</summary>
        public const int DefaultMainPackCapacity = 102;

        /// <summary>
        /// Splits "[preview] character | ops". The character name may contain spaces; the ops follow the first "|". False when
        /// the character or the ops are empty or there is no "|".
        /// </summary>
        public static bool TrySplitArgs(string text, out bool preview, out string character, out string ops)
        {
            preview = false;
            character = null;
            ops = null;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            var bar = text.IndexOf('|');

            if (bar < 0)
                return false;

            var head = text.Substring(0, bar).Trim();
            ops = text.Substring(bar + 1).Trim();

            var space = head.IndexOf(' ');
            var first = space < 0 ? head : head.Substring(0, space);

            if (string.Equals(first, "preview", StringComparison.OrdinalIgnoreCase))
            {
                preview = true;
                head = space < 0 ? string.Empty : head.Substring(space + 1).Trim();
            }

            character = head;

            return character.Length > 0 && ops.Length > 0;
        }

        /// <summary>
        /// Parses the op list (whitespace separated). Every token is checked; <paramref name="problems"/> names each bad one. True only
        /// when there is at least one op and no problem.
        /// </summary>
        public static bool TryParseOps(string text, out List<PvpKitOp> ops, out List<string> problems)
        {
            ops = new List<PvpKitOp>();
            problems = new List<string>();

            var tokens = (text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                if (TryParseOp(token, out var op, out var problem))
                    ops.Add(op);
                else
                    problems.Add(problem);
            }

            if (tokens.Length == 0)
                problems.Add("No ops were given after the '|'.");

            return problems.Count == 0;
        }

        private static bool TryParseOp(string token, out PvpKitOp op, out string problem)
        {
            op = null;
            problem = null;

            var bad = $"'{token}' is not an op. Use <old>=<new>[xN], ~<wcid>, +<wcid>[xN] or -<wcid>.";

            if (token.StartsWith("~", StringComparison.Ordinal))
            {
                if (!TryWcid(token.Substring(1), out var w)) { problem = bad; return false; }
                op = new PvpKitOp(PvpKitOpKind.Recreate, w, w, null, token);
                return true;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                if (!TryWcid(token.Substring(1), out var w)) { problem = bad; return false; }
                op = new PvpKitOp(PvpKitOpKind.Delete, w, 0, null, token);
                return true;
            }

            if (token.StartsWith("+", StringComparison.Ordinal))
            {
                if (!TrySplitCount(token.Substring(1), out var body, out var count, out var countProblem)) { problem = countProblem != null ? $"'{token}': {countProblem}" : bad; return false; }
                if (!TryWcid(body, out var w)) { problem = bad; return false; }
                op = new PvpKitOp(PvpKitOpKind.Add, 0, w, count, token);
                return true;
            }

            var eq = token.IndexOf('=');

            if (eq > 0)
            {
                if (!TryWcid(token.Substring(0, eq), out var a)) { problem = bad; return false; }
                if (!TrySplitCount(token.Substring(eq + 1), out var body, out var count, out var countProblem)) { problem = countProblem != null ? $"'{token}': {countProblem}" : bad; return false; }
                if (!TryWcid(body, out var b)) { problem = bad; return false; }

                if (a == b)
                {
                    problem = $"'{token}' replaces {a} with itself. Use ~{a} to recreate it from the current weenie.";
                    return false;
                }

                op = new PvpKitOp(PvpKitOpKind.Replace, a, b, count, token);
                return true;
            }

            problem = bad;
            return false;
        }

        /// <summary>"1234" or "1234x56" (x or X). False with no problem for a malformed body; with a problem for a bad count.</summary>
        private static bool TrySplitCount(string text, out string body, out int? count, out string problem)
        {
            body = text;
            count = null;
            problem = null;

            var x = text.IndexOfAny(new[] { 'x', 'X' });

            if (x < 0)
                return true;

            body = text.Substring(0, x);
            var digits = text.Substring(x + 1);

            if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
                return false;

            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > MaxCount)
            {
                problem = $"the count must be 1 to {MaxCount:N0}.";
                return false;
            }

            count = n;
            return true;
        }

        private static bool TryWcid(string text, out uint wcid)
        {
            wcid = 0;

            return !string.IsNullOrEmpty(text) && text.All(char.IsAsciiDigit)
                && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out wcid) && wcid != 0;
        }

        /// <summary>
        /// Plans <paramref name="ops"/> against the character's possessions. Every rule is checked first: a wcid named as a source by more
        /// than one op, an unknown weenie, a pack (container) as source or target, a source wcid the character does not carry, a worn
        /// slot the new weenie cannot take, and main-pack room after the whole plan. Any problem empties the writes.
        /// </summary>
        public static PvpKitPlan Plan(IReadOnlyList<PvpKitOp> ops, uint characterGuid, int mainPackCapacity,
            IReadOnlyList<PvpKitPossession> possessions, Func<uint, PvpKitWeenieInfo> weenies)
        {
            var plan = new PvpKitPlan();
            possessions ??= Array.Empty<PvpKitPossession>();
            ops ??= Array.Empty<PvpKitOp>();

            if (ops.Count == 0)
                plan.Problems.Add("No ops were given.");

            // A wcid read by two ops would be planned twice (two replacements of one item, or a replace and a delete of it).
            foreach (var dup in ops.Where(o => o.Kind != PvpKitOpKind.Add).GroupBy(o => o.SourceWcid).Where(g => g.Count() > 1))
                plan.Problems.Add($"wcid {dup.Key} is named by more than one op ({string.Join(" ", dup.Select(o => o.Text))}). Name each source wcid once.");

            var weenieCache = new Dictionary<uint, PvpKitWeenieInfo>();

            PvpKitWeenieInfo Weenie(uint wcid)
            {
                if (!weenieCache.TryGetValue(wcid, out var info))
                    weenieCache[wcid] = info = weenies?.Invoke(wcid);

                return info;
            }

            var nextPlacement = possessions.Where(p => p.ContainerId == characterGuid && !p.IsWorn).Select(p => p.PlacementPosition ?? 0).DefaultIfEmpty(-1).Max() + 1;

            for (var i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                PvpKitWeenieInfo target = null;

                if (op.Kind != PvpKitOpKind.Delete)
                {
                    target = Weenie(op.TargetWcid);

                    if (target == null)
                    {
                        plan.Problems.Add($"{op.Text}: there is no weenie {op.TargetWcid}.");
                        plan.Lines.Add($"{op.Text}: REFUSED (no weenie {op.TargetWcid})");
                        continue;
                    }

                    if (target.IsContainer)
                    {
                        plan.Problems.Add($"{op.Text}: {Label(target)} is a pack; packs are not created by /pvptemplate kit.");
                        plan.Lines.Add($"{op.Text}: REFUSED (a pack)");
                        continue;
                    }
                }

                if (op.Kind == PvpKitOpKind.Add)
                {
                    var total = op.Count ?? 1;
                    var limit = target.StackLimit;

                    // Refused BEFORE expanding into per-stack objects: more stacks than the main pack has slots can never fit, even
                    // with every other item removed, and expanding first would allocate up to MaxCount objects for nothing. A target that
                    // RequiresPackSlot does not use an item slot, so (matching the capacity check after the plan) it is not counted here.
                    var stacksNeeded = ((long)total + limit - 1) / limit;

                    if (!target.RequiresPackSlot && stacksNeeded > mainPackCapacity)
                    {
                        plan.Problems.Add($"{op.Text}: {total.ToString("N0", CultureInfo.InvariantCulture)} {Label(target)} needs {stacksNeeded.ToString("N0", CultureInfo.InvariantCulture)} pack slots (max stack {limit.ToString("N0", CultureInfo.InvariantCulture)}), and the main pack has {mainPackCapacity} slots. Use a smaller count.");
                        plan.Lines.Add($"{op.Text}: REFUSED (more stacks than pack slots)");
                        continue;
                    }

                    var stacks = new List<int>();

                    for (var left = total; left > 0; left -= limit)
                        stacks.Add(Math.Min(limit, left));

                    foreach (var size in stacks)
                    {
                        plan.Creations.Add(new PvpKitCreation
                        {
                            OpIndex = i,
                            Wcid = target.Wcid,
                            StackSize = size,
                            ContainerId = characterGuid,
                            OwnerId = characterGuid,
                            PlacementPosition = nextPlacement++,
                        });
                    }

                    plan.Lines.Add(stacks.Count == 1
                        ? $"{op.Text}: add {Count(stacks[0], target)} to the main pack"
                        : $"{op.Text}: add {total:N0} {Label(target)} to the main pack as {stacks.Count} stacks (max stack {limit:N0})");
                    continue;
                }

                var matches = possessions.Where(p => p.Wcid == op.SourceWcid).OrderBy(p => p.Guid).ToList();

                if (matches.Count == 0)
                {
                    plan.Problems.Add($"{op.Text}: the character carries no wcid {op.SourceWcid}.");
                    plan.Lines.Add($"{op.Text}: REFUSED (none carried)");
                    continue;
                }

                var packs = matches.Where(m => m.IsContainer).ToList();

                if (packs.Count > 0)
                {
                    plan.Problems.Add($"{op.Text}: wcid {op.SourceWcid} is a pack ({packs[0].Name ?? "a container"}); its contents would be orphaned, so packs are never changed by /pvptemplate kit.");
                    plan.Lines.Add($"{op.Text}: REFUSED (a pack)");
                    continue;
                }

                var sourceName = matches[0].Name ?? $"wcid {op.SourceWcid}";

                if (op.Kind == PvpKitOpKind.Delete)
                {
                    plan.Removals.AddRange(matches.Select(m => m.Guid));
                    plan.Lines.Add($"{op.Text}: delete {matches.Count} {sourceName} ({Places(matches, characterGuid)})");
                    continue;
                }

                var notes = new List<string>();
                var refused = false;

                foreach (var m in matches)
                {
                    if (m.IsWorn && (target.ValidLocations & m.WieldLocation.Value) != m.WieldLocation.Value)
                    {
                        plan.Problems.Add($"{op.Text}: {sourceName} is worn at {(EquipMask)m.WieldLocation.Value}, and {Label(target)} cannot be worn there (valid: {(target.ValidLocations == 0 ? "none" : ((EquipMask)target.ValidLocations).ToString())}).");
                        refused = true;
                        continue;
                    }

                    var wanted = op.Count ?? Math.Max(1, m.StackSize);
                    var size = Math.Min(wanted, target.StackLimit);

                    if (size < wanted)
                        notes.Add($"stack {wanted:N0} clamped to {size:N0} (max stack of {Label(target)})");

                    plan.Removals.Add(m.Guid);
                    plan.Creations.Add(new PvpKitCreation
                    {
                        OpIndex = i,
                        Wcid = target.Wcid,
                        StackSize = size,
                        ContainerId = m.IsWorn ? null : m.ContainerId,
                        OwnerId = m.IsWorn ? null : (m.OwnerId ?? m.ContainerId),
                        WielderId = m.IsWorn ? (m.WielderId ?? characterGuid) : null,
                        WieldLocation = m.IsWorn ? m.WieldLocation : null,
                        PlacementPosition = m.IsWorn ? null : m.PlacementPosition,
                        ReplacesGuid = m.Guid,
                    });
                }

                if (refused)
                {
                    plan.Lines.Add($"{op.Text}: REFUSED (worn slot)");
                    continue;
                }

                var sizes = plan.Creations.Where(c => c.OpIndex == i).Select(c => c.StackSize).ToList();
                var sizeText = sizes.Distinct().Count() == 1 ? (sizes[0] > 1 ? $" x{sizes[0]:N0}" : "") : $" (stacks {string.Join(", ", sizes.Select(s => s.ToString("N0", CultureInfo.InvariantCulture)))})";
                var verb = op.Kind == PvpKitOpKind.Recreate ? $"recreate {matches.Count} {sourceName} from the current weenie" : $"replace {matches.Count} {sourceName} with {Label(target)}";

                plan.Lines.Add($"{op.Text}: {verb}{sizeText} in place ({Places(matches, characterGuid)}){(notes.Count > 0 ? "; " + string.Join("; ", notes.Distinct()) : "")}");
            }

            // Main-pack item slots after the whole plan (side packs keep their count: replacements there are one for one).
            bool InMainItemSlot(uint? containerId, bool worn, bool usesPackSlot) => !worn && containerId == characterGuid && !usesPackSlot;

            var removed = new HashSet<uint>(plan.Removals);
            var current = possessions.Count(p => InMainItemSlot(p.ContainerId, p.IsWorn, p.IsContainer || p.RequiresPackSlot));
            var freed = possessions.Count(p => removed.Contains(p.Guid) && InMainItemSlot(p.ContainerId, p.IsWorn, p.IsContainer || p.RequiresPackSlot));
            var added = plan.Creations.Count(c => InMainItemSlot(c.ContainerId, c.WieldLocation.HasValue, Weenie(c.Wcid)?.RequiresPackSlot ?? false));
            var after = current - freed + added;

            if (after > mainPackCapacity)
                plan.Problems.Add($"The main pack would hold {after} items after this, and it has {mainPackCapacity} slots ({current} now). Remove something first.");

            if (!plan.Ok)
            {
                plan.Creations.Clear();
                plan.Removals.Clear();
            }

            return plan;
        }

        private static string Label(PvpKitWeenieInfo w) => string.IsNullOrWhiteSpace(w?.Name) ? $"wcid {w?.Wcid}" : $"{w.Name} ({w.Wcid})";

        private static string Count(int n, PvpKitWeenieInfo w) => n > 1 ? $"{n:N0} x {Label(w)}" : Label(w);

        private static string Places(IReadOnlyList<PvpKitPossession> matches, uint characterGuid)
        {
            return string.Join(", ", matches.Select(m => m.IsWorn ? $"worn {(EquipMask)m.WieldLocation.Value}" : m.ContainerId == characterGuid ? "main pack" : $"pack 0x{m.ContainerId ?? 0:X8}")
                .GroupBy(s => s).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
        }
    }
}
