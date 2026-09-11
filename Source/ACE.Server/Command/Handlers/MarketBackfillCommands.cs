using System;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Managers.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// The market's one repair command: re-project the stored snapshot of Active listings so a
    /// snapshot field added after they were created stops reading null forever.
    ///
    /// DELIBERATELY NOT A /marketadmin SUBCOMMAND, for the same reason /vaultrestore is not one.
    /// That command's header states it is READ ONLY so it can sit at Sentinel; adding a write would
    /// either break that property or drag the whole investigation surface up to Admin. This one
    /// writes, so it lives apart and sits at Admin.
    ///
    /// Structured like MarketAdminCommands and VaultRestoreCommands: a parser returning a result, a
    /// dispatch switch, and an IMarketBackfillCommandTarget seam, so parsing and dispatch are
    /// testable with no Session and no shard.
    ///
    /// NO CONSOLE ARM. The pass runs inline against the live in-memory index and the live vaults, so
    /// it is only meaningful from inside a running world.
    /// </summary>
    public static class MarketBackfillCommands
    {
        [CommandHandler("marketbackfill", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "Re-project the stored snapshot of Active market listings.",
            MarketBackfillCommandParser.HelpText)]
        public static void HandleMarketBackfill(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            var result = MarketBackfillCommandParser.Parse(parameters);

            // Both real subcommands walk up to 500 listings, read each seller's vault and project
            // every listed item, inline on a world thread - the same shape and the same five seconds
            // as /marketadmin and /vaultrestore. Help and the usage error cost nothing, so gating
            // them would only make the cooldown message harder to understand.
            if (NeedsCooldown(result.Kind))
            {
                if ((DateTime.UtcNow - player.LastMarketBackfillCommandTime).TotalSeconds < CooldownSeconds)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat(
                        "[MARKETBACKFILL] You have used a market backfill command too recently. Try again in a few seconds.",
                        ChatMessageType.System));
                    return;
                }

                player.LastMarketBackfillCommandTime = DateTime.UtcNow;
            }

            Dispatch(result, new PlayerMarketBackfillCommandTarget(player));
        }

        /// <summary>Matches /marketadmin's cooldown; see that constant's remarks for the reasoning.</summary>
        internal const double CooldownSeconds = 5.0;

        private static bool NeedsCooldown(MarketBackfillCommandKind kind)
            => kind != MarketBackfillCommandKind.Help && kind != MarketBackfillCommandKind.UsageError;

        internal static void Dispatch(MarketBackfillCommandResult result, IMarketBackfillCommandTarget target)
        {
            switch (result.Kind)
            {
                case MarketBackfillCommandKind.Preview:
                    target.Preview(result.Max, result.AllVersions);
                    break;

                case MarketBackfillCommandKind.Run:
                    target.Run(result.Max, result.AllVersions);
                    break;

                case MarketBackfillCommandKind.Reset:
                    target.Reset();
                    break;

                case MarketBackfillCommandKind.Help:
                    target.ShowHelp();
                    break;

                case MarketBackfillCommandKind.UsageError:
                default:
                    target.UsageError(result.UsageError ?? MarketBackfillCommandParser.UsageMessage);
                    break;
            }
        }
    }

    internal enum MarketBackfillCommandKind
    {
        Preview,
        Run,
        Reset,
        Help,
        UsageError,
    }

    internal sealed class MarketBackfillCommandResult
    {
        public MarketBackfillCommandKind Kind;

        /// <summary>How many Active listings to walk, already defaulted and clamped by the parser.</summary>
        public int Max;

        /// <summary>
        /// True for the `all` arm, which re-projects regardless of snapshot version. The escape
        /// hatch for a projection bugfix shipped without a CurrentVersion bump - version-gated
        /// selection cannot see one of those, by construction.
        /// </summary>
        public bool AllVersions;

        public string UsageError;
    }

    internal static class MarketBackfillCommandParser
    {
        public const string HelpText =
            "preview [all] [n] | run [all] [n] | reset\n" +
            "/marketbackfill preview [n]     - report what a backfill would change, writing nothing\n" +
            "/marketbackfill run [n]         - re-project up to n out-of-date Active listings and store them\n" +
            "/marketbackfill preview all [n] - the same report, ignoring the snapshot version\n" +
            "/marketbackfill run all [n]     - re-project regardless of version (see below)\n" +
            "/marketbackfill reset           - send the next preview and run back to the start of the market\n" +
            "WITHOUT `all`, only listings whose stored snapshot predates the current projection are\n" +
            "walked, which is what the server itself runs after a world start. USE `all` AFTER FIXING\n" +
            "A BUG IN THE PROJECTION without bumping MarketSnapshot.CurrentVersion: version gating\n" +
            "cannot see a change like that, so the normal arm would report a clean market forever.\n" +
            "n defaults to 100 and is capped at 500. EACH RUN RESUMES where the last one stopped, so a\n" +
            "market larger than one pass is covered by repeating the same command until it says the\n" +
            "sweep is complete; preview and run keep separate places. Closed listings are never\n" +
            "touched: they keep the snapshot they captured. A listing whose vault or item cannot be\n" +
            "read right now is skipped and counted, never blanked.";

        public const string UsageMessage = "[MARKETBACKFILL] Type /marketbackfill for the list of commands.";

        /// <summary>Listings per invocation when none is given.</summary>
        public const int DefaultMax = 100;

        /// <summary>The ceiling, equal to the manager's own so the two cannot drift.</summary>
        public const int MaxMax = MarketManager.MaxBackfillRows;

        public static MarketBackfillCommandResult Parse(string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return new MarketBackfillCommandResult { Kind = MarketBackfillCommandKind.Help };

            switch (parameters[0].ToLowerInvariant())
            {
                case "help":
                case "?":
                    return new MarketBackfillCommandResult { Kind = MarketBackfillCommandKind.Help };

                case "preview":
                case "p":
                    return WithMax(MarketBackfillCommandKind.Preview, parameters);

                case "run":
                    return WithMax(MarketBackfillCommandKind.Run, parameters);

                case "reset":
                    return new MarketBackfillCommandResult { Kind = MarketBackfillCommandKind.Reset };

                default:
                    return Usage(UsageMessage);
            }
        }

        /// <summary>
        /// The shared `[n]` tail. Unparsable is an ERROR, never the default: this command WRITES, and
        /// silently walking 100 listings for somebody who asked for 5 does something they did not ask
        /// for. Over the cap is clamped rather than refused, which is the same trade /marketadmin
        /// makes for its own page size.
        /// </summary>
        private static MarketBackfillCommandResult WithMax(MarketBackfillCommandKind kind, string[] parameters)
        {
            var max = DefaultMax;

            // `all` sits between the verb and the count - `run all 250` - so the count keeps the one
            // position it has always had and an existing `run 250` cannot be reinterpreted.
            var next = 1;
            var allVersions = parameters.Length > next
                              && string.Equals(parameters[next], "all", StringComparison.OrdinalIgnoreCase);

            if (allVersions)
                next++;

            if (parameters.Length > next)
            {
                if (!int.TryParse(parameters[next], out max) || max < 1)
                    return Usage($"[MARKETBACKFILL] n must be 1 or more (capped at {MaxMax}).");

                if (max > MaxMax)
                    max = MaxMax;
            }

            return new MarketBackfillCommandResult { Kind = kind, Max = max, AllVersions = allVersions };
        }

        private static MarketBackfillCommandResult Usage(string message)
            => new MarketBackfillCommandResult { Kind = MarketBackfillCommandKind.UsageError, UsageError = message };
    }

    /// <summary>
    /// The seam Dispatch is driven through, for the same reason IMarketAdminCommandTarget exists: one
    /// method per kind, so a recording double proves exactly one distinct call per arm and a
    /// transposition of preview and run - which is the difference between a report and a write -
    /// fails the test.
    /// </summary>
    internal interface IMarketBackfillCommandTarget
    {
        void Preview(int max, bool allVersions);
        void Run(int max, bool allVersions);
        void Reset();
        void ShowHelp();
        void UsageError(string message);
    }

    /// <summary>
    /// Production target: drives MarketManager.BackfillSnapshots and renders its report.
    ///
    /// Every skip counter is printed even at zero. A backfill's whole value is knowing what it did
    /// NOT touch, and a report that hides its zeros makes "nothing was skipped" indistinguishable
    /// from "the counter was not rendered".
    /// </summary>
    internal sealed class PlayerMarketBackfillCommandTarget : IMarketBackfillCommandTarget
    {
        /// <summary>Rendered when the manager has no index to walk. Never confused with "nothing needed fixing".</summary>
        internal const string NotInitializedMessage =
            "The market index has not been built in this process, so there is nothing to re-project. " +
            "This is separate from the market_enabled kill switch, which does not block a backfill.";

        private readonly Player player;

        public PlayerMarketBackfillCommandTarget(Player player)
        {
            this.player = player;
        }

        private void Msg(string message)
            => player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[MARKETBACKFILL] {message}", ChatMessageType.System));

        public void Preview(int max, bool allVersions)
            => Msg(Render(MarketManager.BackfillSnapshots(max, true, allVersions), true, allVersions));

        public void Run(int max, bool allVersions)
            => Msg(Render(MarketManager.BackfillSnapshots(max, false, allVersions), false, allVersions));

        public void Reset()
        {
            MarketManager.ResetBackfillCursor();
            Msg("Both walks are back at the start of the market. The next preview and the next run each begin from the first Active listing.");
        }

        public void ShowHelp() => Msg(MarketBackfillCommandParser.HelpText);

        public void UsageError(string message) => Msg(message);

        internal static string Render(MarketBackfillReport report, bool dryRun, bool allVersions = false)
        {
            if (report == null)
                return NotInitializedMessage;

            if (report.NotInitialized)
                return NotInitializedMessage;

            var text = new StringBuilder(dryRun
                ? "Snapshot backfill PREVIEW - nothing was written.\n"
                : "Snapshot backfill complete.\n");

            if (allVersions)
                text.Append("  FULL SWEEP: the snapshot version was ignored, so up-to-date listings were re-projected too.\n");

            text.Append($"  considered:         {report.Considered} Active listing(s)\n");

            // Market-wide, and printed on BOTH arms. On a full sweep RemainingActive counts every
            // Active row rather than the out-of-date ones, so this is then the only number left that
            // answers "how much is legacy".
            text.Append($"  out of date:        {report.StaleActive} Active listing(s) market-wide\n");
            text.Append(dryRun
                ? $"  would rewrite:      {report.Rewritten}\n"
                : $"  rewritten:          {report.Rewritten}\n");
            text.Append($"  already current:    {report.Unchanged}\n");
            text.Append($"  vault not ready:    {report.StoreNotReady}\n");
            text.Append($"  item unresolved:    {report.ItemUnresolved}\n");
            text.Append($"  projection failed:  {report.ProjectionFailed}\n");
            text.Append($"  projection worse:   {report.ProjectionDegraded}\n");
            text.Append($"  closed mid pass:    {report.ClosedMidPass}\n");
            text.Append($"  write failed:       {report.WriteFailed}\n");
            text.Append($"  retried after fail: {report.RetriedAfterWriteFailure}");

            if (report.SampleRewrittenIds.Count > 0)
                text.Append($"\n  sample listing(s):  {string.Join(", ", report.SampleRewrittenIds)}");

            var command = dryRun ? "preview" : "run";

            if (report.ResumedFromListingId > 0 || report.ResumedFromAccountId > 0)
                text.Append($"\n  Resumed after listing #{report.ResumedFromListingId} (account {report.ResumedFromAccountId}).");

            if (report.RemainingActive > 0)
            {
                // Names the SAME command deliberately, because repeating it now advances: the walk
                // carries a cursor. The old wording told the operator to re-run with a larger n,
                // which at any market above the cap was a no-op - it re-walked the identical page.
                text.Append($"\n  {report.RemainingActive} Active listing(s) still ahead of this sweep.");
                text.Append($"\n  RUN THE SAME COMMAND AGAIN to continue from here - /marketbackfill {command} - and repeat until it reports the sweep complete. Each pass resumes; it does not restart.");
            }
            else
            {
                text.Append("\n  Sweep complete: this pass reached the end of the market, and the next one starts over from the beginning.");
            }

            if (report.WriteFailed > 0)
            {
                text.Append("\n  WRITE FAILURES: those rows are correct in memory and stale in the database, and they are NOT concurrent closes.");

                // Says "remembered", not "re-run and it will sort itself out". A failed row's memory
                // copy was already updated, so a later pass re-projects it to something identical
                // and would call it unchanged; only the remembered id gets it resubmitted. Saying
                // otherwise would be false reassurance on the one path that silently loses data.
                //
                // "the very next run" is a claim the walk has to honour: a remembered row is pulled
                // into the next page even when the cursor has already passed it. Without that, this
                // sentence would be true only on a market small enough to fit one page.
                text.Append("\n  They are REMEMBERED and will be resubmitted by the very next run, even though a re-projection now looks unchanged.");
                text.Append("\n  Check the log for the failing listing ids, then re-run once the shard is healthy.");
            }

            return text.ToString();
        }
    }
}
