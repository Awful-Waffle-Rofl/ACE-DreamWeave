using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using log4net;

namespace ACE.Server.Command.Web
{
    /// <summary>Wire values of the web admin panel's command buckets (PLAN-P4.md section 2).</summary>
    public static class WebCommandBuckets
    {
        public const string Web = "web";
        public const string InGameCharacter = "in_game_character";
        public const string InGameOnly = "in_game_only";
    }

    /// <summary>One reviewed command-classification.tsv row: one (command, handler) pair.</summary>
    public sealed class CommandClassificationRow
    {
        public string Command { get; init; }

        /// <summary>DeclaringType.FullName + "." + MethodName of the handler this row was reviewed against.</summary>
        public string Handler { get; init; }

        public string Bucket { get; init; }
        public string Basis { get; init; }
        public string Capture { get; init; }
        public IReadOnlyList<string> Flags { get; init; }
        public string Note { get; init; }

        public bool HasFlag(string flag) => Flags.Contains(flag, StringComparer.Ordinal);
    }

    /// <summary>What a live CommandHandlerInfo resolves to (PLAN-P4.md section 4.5).</summary>
    public sealed class ResolvedCommand
    {
        public string Bucket { get; init; }
        public string Capture { get; init; }

        /// <summary><see cref="CommandClassification.SourceServer"/> or <see cref="CommandClassification.SourceMod"/>.</summary>
        public string Source { get; init; }

        /// <summary>The matched reviewed row, or null when there is none (mod, or classification gap).</summary>
        public CommandClassificationRow Row { get; init; }

        public string Note { get; init; }
    }

    public sealed class CommandCoverageReport
    {
        /// <summary>Registered (command, handler) pairs with no row.</summary>
        public IReadOnlyList<string> Unclassified { get; init; }

        /// <summary>Rows whose (command, handler) pair is not registered.</summary>
        public IReadOnlyList<string> Orphaned { get; init; }

        public bool IsComplete => Unclassified.Count == 0 && Orphaned.Count == 0;
    }

    /// <summary>
    /// The parsed Source/ACE.Server/command-classification.tsv (PLAN-P4.md section 4), embedded into
    /// ACE.Server. Generated and kept honest by `ACE.Content.Tools commandclassify`; this class only
    /// parses the checked-in file and resolves live handlers against it. Resolution is by the live
    /// handler's identity, never by the command name alone, because several names are registered by more
    /// than one method and a mod can replace any entry at runtime. Everything that does not match a
    /// reviewed row exactly is in_game_only.
    /// </summary>
    public sealed class CommandClassification
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string EmbeddedResourceName = "ACE.Server.command-classification.tsv";
        public const string HeaderLine = "command\thandler\tbucket\tbasis\tcapture\tflags\tnote";

        public const string SourceServer = "server";
        public const string SourceMod = "mod";

        public const string FlagLongRunning = "long_running";
        public const string FlagSelfAudit = "self_audit";

        private static readonly HashSet<string> Buckets = new HashSet<string>(StringComparer.Ordinal) { WebCommandBuckets.Web, WebCommandBuckets.InGameCharacter, WebCommandBuckets.InGameOnly };
        private static readonly HashSet<string> Bases = new HashSet<string>(StringComparer.Ordinal) { "console_invoke", "requires_world", "scan_safe", "scan_unsafe", "console_read", "override" };
        private static readonly HashSet<string> Captures = new HashSet<string>(StringComparer.Ordinal) { "captured", "partial", "uncaptured" };
        private static readonly HashSet<string> KnownFlags = new HashSet<string>(StringComparer.Ordinal) { FlagLongRunning, FlagSelfAudit };

        private static readonly Assembly ServerAssembly = typeof(CommandManager).Assembly;

        private readonly Dictionary<string, CommandClassificationRow> rowsByKey;
        private readonly ConcurrentDictionary<string, byte> loggedGaps = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        private CommandClassification(Dictionary<string, CommandClassificationRow> rowsByKey)
        {
            this.rowsByKey = rowsByKey;
        }

        public IReadOnlyCollection<CommandClassificationRow> Rows => rowsByKey.Values;

        public static string Key(string command, string handler) => command + "\t" + handler;

        public static string HandlerId(MethodInfo method) => $"{method.DeclaringType?.FullName}.{method.Name}";

        public bool TryGetRow(string command, string handler, out CommandClassificationRow row) =>
            rowsByKey.TryGetValue(Key(command, handler), out row);

        public static CommandClassification LoadEmbedded()
        {
            using var stream = ServerAssembly.GetManifestResourceStream(EmbeddedResourceName);

            if (stream == null)
                throw new InvalidOperationException($"CommandClassification.LoadEmbedded: embedded resource '{EmbeddedResourceName}' was not found in {ServerAssembly.FullName}.");

            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return Parse(reader.ReadToEnd());
        }

        /// <summary>
        /// Strict: a wrong header, a malformed row, an unknown bucket/basis/capture/flag, a missing note
        /// where one is required, or a duplicate (command, handler) pair throws FormatException. A table
        /// that is not exactly what the tool writes is not trusted to put anything in the web bucket.
        /// Tolerates CRLF line endings.
        /// </summary>
        public static CommandClassification Parse(string text)
        {
            var rows = new Dictionary<string, CommandClassificationRow>(StringComparer.Ordinal);
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            if (lines.Length == 0 || lines[0].TrimStart('\uFEFF') != HeaderLine)
                throw new FormatException("command-classification.tsv: the first line is not the expected header.");

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0)
                    continue;

                var fields = line.Split('\t');
                if (fields.Length < 6 || fields.Length > 7)
                    throw new FormatException($"command-classification.tsv: line {i + 1} has {fields.Length} field(s), expected 7.");

                var flags = fields[5] == "-" ? Array.Empty<string>() : fields[5].Split(',');
                var row = new CommandClassificationRow
                {
                    Command = fields[0],
                    Handler = fields[1],
                    Bucket = fields[2],
                    Basis = fields[3],
                    Capture = fields[4],
                    Flags = flags,
                    Note = fields.Length > 6 ? fields[6] : string.Empty,
                };

                if (row.Command.Length == 0 || row.Handler.Length == 0)
                    throw new FormatException($"command-classification.tsv: line {i + 1} has an empty command or handler.");
                if (!Buckets.Contains(row.Bucket))
                    throw new FormatException($"command-classification.tsv: line {i + 1} has unknown bucket '{row.Bucket}'.");
                if (!Bases.Contains(row.Basis))
                    throw new FormatException($"command-classification.tsv: line {i + 1} has unknown basis '{row.Basis}'.");
                if (!Captures.Contains(row.Capture))
                    throw new FormatException($"command-classification.tsv: line {i + 1} has unknown capture '{row.Capture}'.");
                if (flags.Any(f => !KnownFlags.Contains(f)))
                    throw new FormatException($"command-classification.tsv: line {i + 1} has an unknown flag in '{fields[5]}'.");
                if ((row.Basis == "override" || flags.Length > 0) && string.IsNullOrWhiteSpace(row.Note))
                    throw new FormatException($"command-classification.tsv: line {i + 1} needs a note (basis override or a flag).");

                var key = Key(row.Command, row.Handler);
                if (rows.ContainsKey(key))
                    throw new FormatException($"command-classification.tsv: line {i + 1} duplicates {row.Command} {row.Handler}.");

                rows[key] = row;
            }

            return new CommandClassification(rows);
        }

        /// <summary>
        /// PLAN-P4.md section 4.5. A handler from any assembly other than ACE.Server is a mod (including a
        /// mod that overrode a built-in name) and is in_game_only. Otherwise the row must match the live
        /// (command, handler) pair exactly, or the command is in_game_only. A row that disagrees with the
        /// live flags (web with RequiresWorld, in_game_character with ConsoleInvoke) is also in_game_only:
        /// the live attribute can differ from the reviewed one when a mod re-registers a built-in method.
        /// </summary>
        public ResolvedCommand Resolve(CommandHandlerInfo live)
        {
            var method = live?.Handler?.Method;
            var attribute = live?.Attribute;

            if (method == null || attribute == null)
                return InGameOnly(SourceServer, null, "This command has no resolvable handler.");

            if (method.DeclaringType?.Assembly != ServerAssembly)
                return InGameOnly(SourceMod, null, "Registered by a server mod. Mod commands are never runnable from the web.");

            var handler = HandlerId(method);

            if (!TryGetRow(attribute.Command, handler, out var row))
            {
                if (loggedGaps.TryAdd(Key(attribute.Command, handler), 0))
                    log.Warn($"[MARKET][ADMIN] command classification gap: '{attribute.Command}' is registered by {handler}, which has no command-classification.tsv row. It is treated as in_game_only.");

                return InGameOnly(SourceServer, null, "No reviewed classification row matches this command's live handler.");
            }

            if (row.Bucket == WebCommandBuckets.Web && (attribute.Flags & CommandHandlerFlag.RequiresWorld) != 0)
                return InGameOnly(SourceServer, row, "The live handler requires the world, but its reviewed row says web.");

            if (row.Bucket == WebCommandBuckets.InGameCharacter && (attribute.Flags & CommandHandlerFlag.ConsoleInvoke) != 0)
                return InGameOnly(SourceServer, row, "The live handler is console-only, but its reviewed row says in_game_character.");

            return new ResolvedCommand
            {
                Bucket = row.Bucket,
                Capture = row.Capture,
                Source = SourceServer,
                Row = row,
                Note = row.Note ?? string.Empty,
            };
        }

        private static ResolvedCommand InGameOnly(string source, CommandClassificationRow row, string note) => new ResolvedCommand
        {
            Bucket = WebCommandBuckets.InGameOnly,
            Capture = row?.Capture ?? "uncaptured",
            Source = source,
            Row = row,
            Note = note,
        };

        /// <summary>Every (command, handler) pair CommandManager.Initialize would register from these types: type.GetMethods(), one entry per attribute.</summary>
        public static List<(string Command, string Handler, CommandHandlerAttribute Attribute, MethodInfo Method)> ReflectRegistered(IEnumerable<Type> types)
        {
            var result = new List<(string, string, CommandHandlerAttribute, MethodInfo)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var type in types)
            {
                foreach (var method in type.GetMethods())
                {
                    foreach (var attribute in method.GetCustomAttributes<CommandHandlerAttribute>())
                    {
                        var handler = HandlerId(method);
                        if (seen.Add(Key(attribute.Command, handler)))
                            result.Add((attribute.Command, handler, attribute, method));
                    }
                }
            }

            return result;
        }

        public static CommandCoverageReport CheckCoverage(IEnumerable<(string Command, string Handler)> registered, CommandClassification table)
        {
            var registeredKeys = new HashSet<string>(registered.Select(r => Key(r.Command, r.Handler)), StringComparer.Ordinal);

            return new CommandCoverageReport
            {
                Unclassified = registeredKeys.Where(k => !table.rowsByKey.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).Select(k => k.Replace('\t', ' ')).ToList(),
                Orphaned = table.rowsByKey.Keys.Where(k => !registeredKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).Select(k => k.Replace('\t', ' ')).ToList(),
            };
        }
    }
}
