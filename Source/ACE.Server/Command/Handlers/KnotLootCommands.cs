using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.KnotLoot;
using ACE.Server.Network;

using log4net;

namespace ACE.Server.Command.Handlers
{
    /// <summary>The parsed /knotloot arguments. Pure: no world access, so the parser is unit-testable.</summary>
    public sealed class KnotLootCommandRequest
    {
        public const int MinLevel = 1;
        public const int MaxLevel = 999;
        public const int MaxCount = 100;
        public const int MaxTableEcho = 40;

        public const string Usage = "Usage: /knotloot <table> <level 1-999> [count 1-100 | give]";

        /// <summary>The lower-invariant table key.</summary>
        public string Table { get; private set; }

        public int Level { get; private set; }

        /// <summary>Rolls to print; 1 by default and for <see cref="Give"/>.</summary>
        public int Count { get; private set; } = 1;

        public bool Give { get; private set; }

        /// <summary>Null when the arguments parsed; otherwise the complete one-line reply.</summary>
        public string Error { get; private set; }

        public bool IsValid => Error == null;

        public static KnotLootCommandRequest Parse(string[] p)
        {
            if (p == null || p.Length < 2 || p.Length > 3 || string.IsNullOrWhiteSpace(p[0]))
                return Fail(Usage);

            if (!int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var level) || level < MinLevel || level > MaxLevel)
                return Fail(Usage);

            var request = new KnotLootCommandRequest { Level = level };

            if (p.Length == 3)
            {
                if (string.Equals(p[2], "give", StringComparison.OrdinalIgnoreCase))
                    request.Give = true;
                else if (int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 1 && count <= MaxCount)
                    request.Count = count;
                else
                    return Fail(Usage);
            }

            if (!KnotLootTables.TryGet(p[0], out var table))
            {
                var shown = p[0].Trim();
                if (shown.Length > MaxTableEcho)
                    shown = shown.Substring(0, MaxTableEcho);

                return Fail("Unknown knot loot table: " + shown);
            }

            request.Table = table.Key;

            return request;
        }

        private static KnotLootCommandRequest Fail(string error) => new KnotLootCommandRequest { Error = error };
    }

    /// <summary>/knotloot: developer roll of a Knot loot table at any level, printed or handed over for live testing.</summary>
    public static class KnotLootCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly LiveKnotLootSettings Settings = new LiveKnotLootSettings();

        [CommandHandler("knotloot", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 2,
            "Rolls a Knot loot table at a level: prints the rolls or gives you the items.",
            "<table> <level 1-999> [count 1-100 | give]\n\n" +
            "Prints one line per roll (count, default 1) and creates nothing, or with give rolls once and places\n" +
            "the items in your inventory (any that cannot be placed are destroyed).")]
        public static void HandleKnotLoot(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
            {
                Write(session, "/knotloot needs a player in the world.");
                return;
            }

            var request = KnotLootCommandRequest.Parse(parameters);

            if (!request.IsValid)
            {
                Write(session, request.Error);
                return;
            }

            try
            {
                if (!KnotLootTunables.TableEnabled(request.Table))
                {
                    Write(session, $"Knot loot table {request.Table} is disabled (knot_loot_{request.Table}_enabled).");
                    return;
                }

                if (request.Give)
                {
                    var items = KnotLootService.RollLive(request.Table, request.Level);
                    var result = PlaceOrDestroy(items, item => player.TryCreateInInventoryWithNetworking(item), item => item.Destroy());

                    if (result.Error != null)
                    {
                        log.Error("[KNOTLOOT] /knotloot give failed", result.Error);
                        Write(session, $"/knotloot failed: {result.Error.GetType().Name}. Gave {result.Placed} item(s); {result.Destroyed} destroyed.");
                        return;
                    }

                    Write(session, result.Destroyed == 0
                        ? $"Gave {result.Placed} item(s)."
                        : $"Gave {result.Placed} item(s); {result.Destroyed} could not be placed and were destroyed.");
                    return;
                }

                KnotLootTables.TryGet(request.Table, out var table);

                var window = KnotLootTunables.Window(table.Key);

                for (var n = 1; n <= request.Count; n++)
                {
                    var drops = KnotLootRoller.Roll(table, request.Level, Settings, () => ThreadSafeRandom.Next(0.0f, 1.0f));

                    Write(session, KnotLootSummary.FormatLine(table.Key, request.Level, n, drops, window,
                        definition => KnotLootTunables.Trait(definition.SettingPrefix, definition.Defaults)));
                }
            }
            catch (Exception ex)
            {
                log.Error("[KNOTLOOT] /knotloot failed", ex);
                Write(session, "/knotloot failed: " + ex.GetType().Name);
            }
        }

        /// <summary>The outcome of <see cref="PlaceOrDestroy{T}"/>. Error is the exception that stopped placement, if any.</summary>
        public readonly record struct PlaceResult(int Placed, int Destroyed, Exception Error);

        /// <summary>
        /// Every item ends up placed or destroyed. A false from <paramref name="tryPlace"/> destroys that item. If
        /// tryPlace throws on item k, items k..end are destroyed in a finally, items before k stay placed, and the
        /// exception is RETURNED in <see cref="PlaceResult.Error"/> (not rethrown) so the caller can report partial counts.
        /// A destroy that itself throws is swallowed so the remaining items are still destroyed.
        /// </summary>
        internal static PlaceResult PlaceOrDestroy<T>(IReadOnlyList<T> items, Func<T, bool> tryPlace, Action<T> destroy)
        {
            var placed = 0;
            var destroyed = 0;
            var i = 0;
            Exception error = null;

            try
            {
                for (; i < items.Count; i++)
                {
                    var item = items[i];

                    if (tryPlace(item))
                    {
                        placed++;
                    }
                    else
                    {
                        destroy(item);
                        destroyed++;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                for (; i < items.Count; i++)
                {
                    try { destroy(items[i]); destroyed++; }
                    catch (Exception) { }
                }
            }

            return new PlaceResult(placed, destroyed, error);
        }
        private static void Write(Session session, string text) => CommandHandlerHelper.WriteOutputInfo(session, text);
    }
}
