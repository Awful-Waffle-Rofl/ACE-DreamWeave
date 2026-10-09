using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// The go/no-go measurement for the counted vault storage tier: how many stored biotas would
    /// actually collapse into class rows, and what stops the rest.
    ///
    /// READ ONLY, AND THAT IS THE WHOLE CONTRACT. It deposits nothing, withdraws nothing, destroys
    /// nothing, saves nothing and writes no property on any stored item. It exists to answer a question
    /// before anything is built, so if it ever needed to mutate something the answer it produced would
    /// be worthless anyway.
    ///
    /// TWO GETTERS ARE OFF LIMITS ON A STORED ITEM and they are named here because nothing in a
    /// signature warns you. WorldObject.Workmanship's GETTER WRITES: its legacy-recovery branch assigns
    /// ItemWorkmanship (WorldObject_Properties.cs:1638), and ItemWorkmanship is one of the very
    /// properties this command is measuring, so reading it would change the number being reported.
    /// Nothing below touches it. The same goes for anything that would call Destroy() or
    /// SaveBiotaToDatabase(); in particular this command deliberately never calls
    /// VaultItemClass.Materialize, which builds a real object and takes a dynamic guid.
    ///
    /// WHY IT CAN TAKE TWO RUNS. A vault store loads its index and ledger synchronously but its
    /// containers' inventories load asynchronously (AccountVaultStore R4), so the first time this
    /// command touches a cold account it starts that load and reports the account as NOT READY rather
    /// than reporting a falsely empty vault. Run it again and those accounts are counted. The report
    /// says how many were skipped, so a partial measurement can never be mistaken for a complete one.
    /// </summary>
    public static class VaultClassCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Accounts per fleet-wide run when none is given. Each cold one costs an index read, a ledger read and a biota read per vault, inline on a world thread.</summary>
        internal const int DefaultAccountLimit = 25;

        /// <summary>The ceiling on one fleet-wide run, so a slip of the keyboard cannot put thousands of synchronous shard reads on the tick.</summary>
        internal const int MaxAccountLimit = 500;

        /// <summary>Matches the neighbouring vault commands' cooldown; see VaultRestoreCommands.CooldownSeconds.</summary>
        internal const double CooldownSeconds = 10.0;

        private static long lastRunTicks;

        private static int running;

        /// <summary>
        /// Whether a /vaultclassdryrun or /vaultclassinspect scan is in flight, server-wide.
        ///
        /// Exposed for AccountVaultFoldMigration, which REFUSES TO START while one is running. Both put
        /// synchronous shard reads over the same account_vault tables in flight at once, and the scan
        /// additionally walks a store's entries while the migration is destroying biotas out of it - so
        /// the scan would report a number that was true of no moment in particular. Refusing is cheaper
        /// than explaining the report afterwards.
        ///
        /// CompareExchange with a no-op delta rather than a plain read, so the read carries the same
        /// barrier the two writers use.
        /// </summary>
        internal static bool ScanInFlight => Interlocked.CompareExchange(ref running, 0, 0) != 0;

        /// <summary>
        /// The reciprocal of <see cref="ScanInFlight"/>: true, with a message, when a fold migration is in
        /// flight and this read-only command must not run.
        ///
        /// WHY REFUSE RATHER THAN RUN. Both /vaultclassdryrun and /vaultclassinspect are MEASUREMENTS, and
        /// a migration is destroying stored biotas and writing account_vault_class rows out of the very
        /// stores they read. Overlapping them does not produce a slightly stale number, it produces a
        /// number that was true of no single moment - the walk would see some accounts pre-fold and some
        /// post-fold. A measurement nobody can stand behind is worse than no measurement, because it gets
        /// quoted.
        ///
        /// <paramref name="verb"/> is what the caller would have done, so the message names it.
        /// </summary>
        internal static bool TryRefuseWhileMigrating(string verb, out string refusal)
        {
            if (!AccountVaultFoldMigration.IsRunning)
            {
                refusal = null;
                return false;
            }

            refusal = $"A vault fold migration is running, and it is changing the stores this would {verb}. "
                    + "Wait for it to finish, then run this. /vaultclassfold status prints its result.";

            return true;
        }

        /// <summary>
        /// Test-only: holds the scan flag until the returned handle is disposed, so a test can drive the
        /// "a scan is already running" refusal without a session, a player or a shard.
        ///
        /// Returns null when the flag was ALREADY held, which a test must treat as a failure rather than
        /// as nothing to do - silently proceeding would leave the flag set for every later test class in
        /// the process, and this flag is process-static.
        /// </summary>
        internal static IDisposable TakeScanFlagForTest()
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                return null;

            return new ScanFlagHold();
        }

        private sealed class ScanFlagHold : IDisposable
        {
            public void Dispose() => Interlocked.Exchange(ref running, 0);
        }

        [CommandHandler("vaultclassdryrun", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "Measure how well the counted vault storage tier would work, without changing anything.",
            "<account or character> | all [accounts]\n" +
            "/vaultclassdryrun <name>        - measure one account's vault\n" +
            "/vaultclassdryrun all [n]       - measure up to n accounts (default 25, max 500)\n" +
            "Reports how many stored biotas would collapse into item classes, the distinct class count\n" +
            "at value band 0 (the shipping policy) and at bands 10 and 25 for comparison, and a\n" +
            "histogram of why the rest refuse. Nothing is written. A cold account is reported as NOT\n" +
            "READY on the run that warms it - run the command again to include it.")]
        public static void HandleVaultClassDryRun(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            void Msg(string message)
                => session.Network.EnqueueSend(new GameMessageSystemChat($"[VAULTCLASS] {message}", ChatMessageType.System));

            if (parameters == null || parameters.Length == 0)
            {
                Msg("/vaultclassdryrun <account or character> | all [accounts]");
                return;
            }

            // The reciprocal of AccountVaultFoldMigration's ScanInFlight check. See
            // TryRefuseWhileMigrating for why a scan overlapping a migration is refused rather than run.
            if (TryRefuseWhileMigrating("measure", out var migrationRefusal))
            {
                Msg(migrationRefusal);
                return;
            }

            var now = DateTime.UtcNow.Ticks;
            var previous = Interlocked.Read(ref lastRunTicks);

            if (previous != 0 && new TimeSpan(now - previous).TotalSeconds < CooldownSeconds)
            {
                Msg($"That scan blocks a world thread while it runs. Wait {CooldownSeconds:0} seconds between runs.");
                return;
            }

            // One at a time, server-wide. Two administrators running a fleet scan at once would double
            // the synchronous shard reads sitting on the tick for no extra information.
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                Msg("A vault class scan is already running. Wait for it to finish.");
                return;
            }

            try
            {
                Interlocked.Exchange(ref lastRunTicks, now);

                var report = Run(parameters, out var error);

                if (report == null)
                {
                    Msg(error ?? "The scan could not run.");
                    return;
                }

                Msg(report.Render());
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
            }
        }

        /// <summary>
        /// Resolves the arguments, walks the selected accounts and returns the finished report, or null
        /// with a reason. Split out from the handler so the shape of a run is readable in one screen.
        /// </summary>
        private static VaultClassDryRunReport Run(string[] parameters, out string error)
        {
            error = null;

            var report = new VaultClassDryRunReport();

            if (string.Equals(parameters[0], "all", StringComparison.OrdinalIgnoreCase))
            {
                var limit = DefaultAccountLimit;

                if (parameters.Length > 1)
                {
                    // Unparsable is an error rather than a fallback to the default: a scan that silently
                    // covered 25 accounts when somebody asked for 400 answers a question nobody asked.
                    if (!int.TryParse(parameters[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1)
                    {
                        error = $"accounts must be 1 or more (capped at {MaxAccountLimit}).";
                        return null;
                    }

                    if (limit > MaxAccountLimit)
                        limit = MaxAccountLimit;
                }

                var accountIds = DatabaseManager.Shard.BaseDatabase.GetAllAccountVaultAccountIds();

                // NULL means the READ failed. Reporting it as "no accounts own a vault" would turn a
                // database blip into a decisive zero, which is the one answer this command must never
                // give.
                if (accountIds == null)
                {
                    error = "The account_vault index could not be read. Try again in a moment.";
                    return null;
                }

                report.Scope = $"all accounts ({accountIds.Count} own a vault; scanning up to {limit})";
                report.AccountsAvailable = accountIds.Count;

                foreach (var accountId in accountIds.Take(limit))
                    ScanAccount(accountId, report);

                return report;
            }

            var name = parameters[0];

            if (!TryResolve(name, out var single, out var what))
            {
                error = $"No account or character called {name}.";
                return null;
            }

            report.Scope = what;
            report.AccountsAvailable = 1;

            ScanAccount(single, report);

            return report;
        }

        /// <summary>
        /// Same two-step resolution as /vaultrestore: the ACCOUNT name first, then a character name on
        /// it, because an administrator holding one of the two should not have to know which.
        /// </summary>
        private static bool TryResolve(string name, out uint accountId, out string what)
        {
            accountId = 0;
            what = null;

            if (string.IsNullOrWhiteSpace(name))
                return false;

            var byAccount = DatabaseManager.Authentication.GetAccountIdByName(name);

            if (byAccount != 0)
            {
                accountId = byAccount;
                what = $"account {name} ({byAccount})";
                return true;
            }

            if (AccountVaultManager.TryResolveCharacter(name, out _, out var canonical, out var resolved) && resolved != 0)
            {
                accountId = resolved;
                what = $"{canonical ?? name}'s account ({resolved})";
                return true;
            }

            return false;
        }

        /// <summary>
        /// One account. Reads its store through the ordinary public read path and classifies every
        /// stored biota behind every entry.
        ///
        /// GetEntries returns GROUP rows for equivalent salvage bags, so the members are flattened: the
        /// question here is how many BIOTAS would collapse, not how many panel rows there are.
        /// </summary>
        private static void ScanAccount(uint accountId, VaultClassDryRunReport report)
        {
            AccountVaultStore store;

            try
            {
                store = AccountVaultManager.GetStore(accountId);
            }
            catch (Exception)
            {
                store = null;
            }

            if (store == null)
            {
                report.AccountsUnavailable++;
                return;
            }

            // Triggers the index and ledger read if it has not happened, and starts the containers'
            // async inventory load. An account that is not ready is COUNTED AND SKIPPED, never scanned
            // partially: GetEntries returns an empty list rather than a short one in that state, and
            // counting an empty list as "this account has nothing classifiable" would quietly deflate
            // the ratio this whole command exists to measure.
            if (!store.TryCheckReady(out _))
            {
                report.AccountsNotReady++;
                return;
            }

            report.AccountsScanned++;

            IReadOnlyList<VaultEntry> entries;

            try
            {
                entries = store.GetEntries(0, -1);
            }
            catch (Exception)
            {
                report.AccountsUnavailable++;
                report.AccountsScanned--;
                return;
            }

            foreach (var entry in entries)
            {
                if (entry.Kind == VaultEntryKind.Ledger)
                {
                    report.LedgerRows++;
                    report.LedgerUnits += entry.Count;
                    continue;
                }

                // A counted CLASS row has already collapsed: it is the OUTCOME this command measures
                // the potential for, not an input to it. Counting its items as "would collapse" would
                // inflate the ratio with work already done, and it carries no biota to classify
                // anyway - its Members list is empty, so the loop below would silently do nothing.
                if (entry.Kind == VaultEntryKind.Class)
                {
                    // ClassMembers.Count, not 1: this number is compared against account_vault_class,
                    // and one drawn line can stand for several of its rows.
                    report.ClassRows += entry.ClassMembers.Count;
                    report.ClassItems += entry.Count;
                    continue;
                }

                foreach (var member in entry.Members)
                    report.Classify(member);
            }
        }

        // ------------------------------------------------------------------ inspect

        [CommandHandler("vaultclassinspect", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 1,
            "List one account's counted item-class rows and whether each one can still be rebuilt.",
            "<account or character>\n" +
            "/vaultclassinspect <name>       - list every account_vault_class row for that account\n" +
            "Shows each row's class key, wcid, item count, pooled total, the per-item share the next\n" +
            "withdraw would hand out, and whether the stored payload still PARSES and MATERIALIZES.\n" +
            "Reads the rows straight from the shard, so it can see rows the store itself refused to\n" +
            "load - which is the case it exists for. Nothing is written, repaired or folded.")]
        public static void HandleVaultClassInspect(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            void Msg(string message)
                => session.Network.EnqueueSend(new GameMessageSystemChat($"[VAULTCLASS] {message}", ChatMessageType.System));

            if (parameters == null || parameters.Length == 0)
            {
                Msg("/vaultclassinspect <account or character>");
                return;
            }

            // Same reciprocal guard as the dry run, and it matters more here: this reads
            // account_vault_class rows straight from the shard AND materializes a probe per row, while a
            // migration is inserting and updating those rows underneath it.
            if (TryRefuseWhileMigrating("inspect", out var migrationRefusal))
            {
                Msg(migrationRefusal);
                return;
            }

            // Shares the dry run's one-at-a-time flag rather than taking its own: both put synchronous
            // shard reads on a world thread, and there is no value in letting them overlap.
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                Msg("A vault class scan is already running. Wait for it to finish.");
                return;
            }

            try
            {
                if (!TryResolve(parameters[0], out var accountId, out var what))
                {
                    Msg($"No account or character called {parameters[0]}.");
                    return;
                }

                var rows = DatabaseManager.Shard.BaseDatabase.GetAccountVaultClasses(accountId);

                // NULL is a read FAILURE, never an empty ledger - the same discipline the DAO and the
                // store keep. Reporting "no class rows" over a database blip is the one answer a
                // command whose job is to find stranded rows must never give.
                if (rows == null)
                {
                    Msg("The account_vault_class rows could not be read. Try again in a moment.");
                    return;
                }

                Msg(BuildInspectReport(what, rows, ServedClassKeys(accountId),
                                       VaultItemClass.Materialize,
                                       probe => probe?.Destroy()).Render());
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
            }
        }

        /// <summary>
        /// The class keys the live store is actually SERVING, so the report can mark a row the store
        /// refused to load. Best effort: an unavailable or not-ready store yields null, which the
        /// report renders as "unknown" rather than as "not served" - a store that could not be read
        /// says nothing about whether a row is stranded.
        /// </summary>
        private static HashSet<string> ServedClassKeys(uint accountId)
        {
            try
            {
                var store = AccountVaultManager.GetStore(accountId);

                if (store == null || !store.TryCheckReady(out _))
                    return null;

                var served = new HashSet<string>(StringComparer.Ordinal);

                foreach (var entry in store.GetEntries(0, -1))
                {
                    if (entry.Kind != VaultEntryKind.Class)
                        continue;

                    // EVERY member, never entry.ClassKey alone. One drawn line can stand for several
                    // account_vault_class rows (the class key carries the raw workmanship pair while
                    // the panel buckets its quotient), and entry.ClassKey names only the
                    // representative - so reading it alone would report every other member of every
                    // group as a STRANDED row the store had refused to load.
                    foreach (var member in entry.ClassMembers)
                    {
                        if (member.ClassKey != null)
                            served.Add(member.ClassKey);
                    }
                }

                return served;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The pure half, so the whole report is unit-testable without a world database.
        ///
        /// IT IS READ ONLY, AND UNLIKE THE DRY RUN IT DOES MATERIALIZE. The dry run deliberately never
        /// builds an object because it walks thousands of stored items; this walks one account's class
        /// rows, and whether a row can still be BUILT is the exact question it is here to answer, which
        /// no amount of reading the payload can decide. Every probe is destroyed in a finally, so the
        /// only lasting effect is a handful of consumed dynamic guids.
        ///
        /// It repairs nothing. A row that cannot be parsed or built is REPORTED and left exactly as it
        /// is - the repair is a separate decision, and a command that quietly rewrote a payload would
        /// destroy the evidence of what went wrong.
        /// </summary>
        internal static VaultClassInspectReport BuildInspectReport(string scope,
                                                                  IReadOnlyList<ACE.Database.Models.Shard.AccountVaultClass> rows,
                                                                  HashSet<string> servedKeys,
                                                                  Func<uint, VaultItemClassOverrides, int?, WorldObject> materialize,
                                                                  Action<WorldObject> destroy)
        {
            var report = new VaultClassInspectReport { Scope = scope, ServedKeysKnown = servedKeys != null };

            foreach (var row in rows ?? Array.Empty<ACE.Database.Models.Shard.AccountVaultClass>())
            {
                var line = new VaultClassInspectRow
                {
                    ClassKey = row.ClassKey,
                    Wcid = row.Wcid,
                    Count = row.Count,
                    TotalValue = row.TotalValue,
                    ValueBandPct = row.ValueBandPct,
                    PerItemShare = AccountVaultStore.PooledShare(row.TotalValue, row.Count),
                    Served = servedKeys != null && row.ClassKey != null && servedKeys.Contains(row.ClassKey),
                };

                if (!VaultItemClass.TryParseCanonicalForm(row.CanonicalForm, out var parsedWcid, out var overrides, out _))
                {
                    line.Note = "payload does NOT parse";
                    report.Rows.Add(line);
                    continue;
                }

                line.Parses = true;

                if (parsedWcid != row.Wcid)
                {
                    // The denormalized column and the payload disagree, so neither can be trusted to
                    // say what these items are. Reported rather than resolved in favour of one of them.
                    line.Note = $"payload wcid {parsedWcid} disagrees with the row's wcid {row.Wcid}";
                    report.Rows.Add(line);
                    continue;
                }

                line.Name = overrides.GetString(PropertyString.Name);

                WorldObject probe = null;

                try
                {
                    probe = materialize(row.Wcid, overrides, line.PerItemShare);

                    if (probe == null)
                    {
                        line.Note = "payload parses but wcid will NOT instantiate";
                    }
                    else
                    {
                        line.Materializes = true;

                        if (string.IsNullOrEmpty(line.Name))
                            line.Name = probe.Name;
                    }
                }
                catch (Exception ex)
                {
                    line.Note = $"materialize threw: {ex.GetType().Name}";
                }
                finally
                {
                    if (probe != null)
                    {
                        try
                        {
                            destroy(probe);
                        }
                        catch (Exception)
                        {
                            // A probe that will not die is a leaked guid and nothing more; it must not
                            // take down a read-only report.
                        }
                    }
                }

                report.Rows.Add(line);
            }

            return report;
        }

        // ------------------------------------------------------------------ fold migration

        /// <summary>
        /// Gap between two progress lines to the operator, in seconds. The migration calls its progress
        /// consumer after every batch; this is what turns thousands of callbacks into a readable trickle.
        /// </summary>
        internal const double ProgressIntervalSeconds = 10.0;

        /// <summary>
        /// Most per-account lines the CHAT copy of a report carries. The log copy is never truncated.
        ///
        /// It exists because the fleet is large: on prod 15,423 stored biotas are spread over enough
        /// accounts that a fully expanded INCOMPLETE report is far past anything a chat window can render,
        /// and a truncated-by-the-client report is worse than an explicitly truncated one because nothing
        /// says it was cut.
        /// </summary>
        internal const int ChatAccountLineCap = 20;

        private const string FoldUsage = "/vaultclassfold <account or character> | all [accounts] [--batch=n] | status | stop";

        [CommandHandler("vaultclassfold", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 1,
            "Migrate already-stored vault salvage into counted item-class rows, and say whether it finished.",
            "<account or character> | all [accounts] [--batch=n] | status | stop\n" +
            "/vaultclassfold all                  - migrate every account that owns a vault\n" +
            "/vaultclassfold all 25               - migrate the first 25 only (a SAMPLE; can never report COMPLETE)\n" +
            "/vaultclassfold <name>               - migrate one account\n" +
            "/vaultclassfold all --batch=50       - items per batch (default 25, max 200)\n" +
            "/vaultclassfold status               - reprint the last finished run's report\n" +
            "/vaultclassfold stop                 - ask the running migration to stop after this batch\n" +
            "Runs on a BACKGROUND thread, not the world loop. Progress arrives every ~10s. The completion\n" +
            "statement is written to the server log whatever happens to your session, and begins\n" +
            "[VAULT] fold migration COMPLETE or INCOMPLETE.\n" +
            "COMPLETE means every account the account_vault index names was visited AND every one of them\n" +
            "reported nothing left to fold. It is NEVER inferred from a quiet log or from a batch folding\n" +
            "zero - a cold store, the kill switch, a zero budget and a refusing ledger all fold zero.\n" +
            "The fold is ONE WAY. Take a shard backup first.")]
        public static void HandleVaultClassFold(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            void Msg(string message)
                => session.Network.EnqueueSend(new GameMessageSystemChat($"[VAULTCLASS] {message}", ChatMessageType.System));

            if (parameters == null || parameters.Length == 0)
            {
                Msg(FoldUsage);
                return;
            }

            // Pulled out of the argument list first, in any position, so `all --batch=50 25` and
            // `all 25 --batch=50` mean the same thing.
            if (!TryTakeBatchArgument(parameters, out var positional, out var batch, out var batchError))
            {
                Msg(batchError);
                return;
            }

            var verb = positional.Count > 0 ? positional[0] : null;

            if (verb == null)
            {
                Msg(FoldUsage);
                return;
            }

            // The subcommands are matched BEFORE name resolution, so an account or character literally
            // called "status", "stop" or "all" is unreachable through this command. Accepted rather than
            // worked around: those three words are what an operator reaches for under pressure, and the
            // single-account arm is a convenience while `all` is the arm that matters. Use the account id
            // through /vaultclassfold's sibling commands if that ever comes up.
            if (string.Equals(verb, "status", StringComparison.OrdinalIgnoreCase))
            {
                ReportFoldStatus(Msg);
                return;
            }

            if (string.Equals(verb, "stop", StringComparison.OrdinalIgnoreCase))
            {
                if (!AccountVaultFoldMigration.IsRunning)
                {
                    Msg("No fold migration is running.");
                    return;
                }

                AccountVaultFoldMigration.RequestStop();

                Msg("Stop requested. The migration finishes the batch in flight and then stops; its report will say INCOMPLETE and name every account it did not reach. Nothing is rolled back - a stop cannot lose an item.");
                return;
            }

            if (string.Equals(verb, "all", StringComparison.OrdinalIgnoreCase))
            {
                // 0 means the whole fleet. Deliberately NOT defaulted to a sample the way
                // /vaultclassdryrun's 25 is: the dry run's default protects the world loop from a big
                // synchronous scan, while this runs off the loop and exists to FINISH. A default sample
                // here would make the ordinary invocation one that can never report COMPLETE.
                var limit = 0;

                if (positional.Count > 1)
                {
                    if (!int.TryParse(positional[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1)
                    {
                        Msg("accounts must be 1 or more, or leave it out to cover the whole fleet.");
                        return;
                    }
                }

                if (!AccountVaultFoldMigration.TryStartFleetRun(batch, limit, out var fleetRefusal,
                                                                FoldProgressReporter(session),
                                                                FoldCompletionReporter(session)))
                {
                    Msg(fleetRefusal);
                    return;
                }

                if (limit > 0)
                    Msg($"Fold migration STARTED over the first {limit} account(s) that own a vault, batch size {batch}. A capped run is a SAMPLE and can never report COMPLETE, because it leaves accounts unvisited by construction.");
                else
                    Msg($"Fold migration STARTED over the whole fleet, batch size {batch}.");

                Msg($"It runs on a background thread; progress every ~{ProgressIntervalSeconds:0}s. The completion statement goes to the server log whatever happens to this session - /vaultclassfold status reprints it, /vaultclassfold stop ends it early.");
                return;
            }

            if (!TryResolve(verb, out var accountId, out var what))
            {
                Msg($"No account or character called {verb}.");
                return;
            }

            if (!AccountVaultFoldMigration.TryStartAccountRun(accountId, what, batch, out var accountRefusal,
                                                              FoldProgressReporter(session),
                                                              FoldCompletionReporter(session)))
            {
                Msg(accountRefusal);
                return;
            }

            Msg($"Fold migration STARTED over {what}, batch size {batch}. A single-account run's COMPLETE means only that this account is migrated, and says nothing about the fleet.");
        }

        /// <summary>
        /// Reprints the last FINISHED run's report, which is how the result survives the operator: the
        /// migration outlives the session that asked for it, so a report that only ever reached one chat
        /// window is a report a relog destroys.
        ///
        /// A null snapshot is answered with what it actually means - nothing has finished IN THIS PROCESS -
        /// and explicitly not with anything that could be read as "the migration is done".
        /// </summary>
        private static void ReportFoldStatus(Action<string> msg)
        {
            if (AccountVaultFoldMigration.IsRunning)
                msg("A fold migration is RUNNING right now. Its completion statement lands in the server log and in this window when it finishes.");

            var report = AccountVaultFoldMigration.LastReport;

            if (report == null)
            {
                msg("No fold migration has FINISHED in this server process. That is not a statement about the migration - a restart clears this, and the server log is the durable copy. Grep it for: [VAULT] fold migration");
                return;
            }

            var age = DateTime.UtcNow - report.FinishedUtc;

            msg($"last run finished {age.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minute(s) ago:");
            msg(report.Render(ChatAccountLineCap));
        }

        /// <summary>
        /// Splits `--batch=n` out of the argument list and validates it.
        ///
        /// A value above the maximum is REJECTED BY NAME rather than clamped, which is the whole reason
        /// this is not just <see cref="AccountVaultFoldMigration.ClampBatch"/>: an operator who typed 2000
        /// and silently got 200 would write the wrong number in the maintenance window's notes, and the
        /// notes are what the next run is compared against.
        /// </summary>
        internal static bool TryTakeBatchArgument(string[] parameters, out List<string> positional, out int batch, out string error)
        {
            const string prefix = "--batch=";

            positional = new List<string>();
            batch = AccountVaultFoldMigration.DefaultBatchSize;
            error = null;

            var seen = false;

            foreach (var raw in parameters)
            {
                if (raw == null)
                    continue;

                if (!raw.StartsWith("--batch", StringComparison.OrdinalIgnoreCase))
                {
                    positional.Add(raw);
                    continue;
                }

                if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // `--batch 50` rather than `--batch=50`. Rejected rather than accepted loosely,
                    // because the loose reading would have to consume the next token, and the next token
                    // in `all --batch 50` is the account count.
                    error = $"Write the batch size as --batch=n, for example --batch=50 (default {AccountVaultFoldMigration.DefaultBatchSize}, max {AccountVaultFoldMigration.MaxBatchSize}).";
                    return false;
                }

                if (seen)
                {
                    error = "Give --batch at most once.";
                    return false;
                }

                seen = true;

                var value = raw.Substring(prefix.Length);

                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                {
                    error = $"--batch must be a whole number of 1 or more (max {AccountVaultFoldMigration.MaxBatchSize}).";
                    return false;
                }

                if (parsed > AccountVaultFoldMigration.MaxBatchSize)
                {
                    error = $"--batch={parsed} is above the maximum of {AccountVaultFoldMigration.MaxBatchSize}. Ask for {AccountVaultFoldMigration.MaxBatchSize} or less; nothing is clamped for you, because a run that quietly used a different batch size than you asked for would be measured against the wrong number.";
                    return false;
                }

                batch = parsed;
            }

            return true;
        }

        /// <summary>
        /// The progress consumer for one run: a rate limit, one INFO log line and one chat line per tick.
        ///
        /// IT ARRIVES ON THE MIGRATION'S WORKER THREAD, not a world thread, which is why the session write
        /// goes through <see cref="SendToOperator"/> and re-checks that the session is still in the world
        /// rather than trusting the capture. No lock: the migration is strictly serial, so exactly one
        /// thread ever runs this closure, and a lock here would imply a concurrency that does not exist.
        ///
        /// A throw from here cannot abort the migration - AccountVaultFoldMigration catches it - but there
        /// is nothing here that should throw, and the catch is its safety net rather than a licence.
        /// </summary>
        private static Action<VaultFoldMigrationProgress> FoldProgressReporter(Session session)
        {
            var sinceLastLine = Stopwatch.StartNew();

            return progress =>
            {
                if (progress == null || sinceLastLine.Elapsed.TotalSeconds < ProgressIntervalSeconds)
                    return;

                sinceLastLine.Restart();

                var line = progress.Render();

                // Both copies, every tick. The log line is the one that survives the session, and a
                // progress trail in the log is what makes a wedged run diagnosable after the fact.
                log.Info($"[VAULT] {line}");

                SendToOperator(session, line);
            };
        }

        /// <summary>
        /// The completion consumer for one run. It writes ONLY to the session.
        ///
        /// The log copy is not written here, and that is deliberate rather than an omission:
        /// AccountVaultFoldMigration writes it itself, unconditionally, before this callback runs, so the
        /// completion statement cannot be lost by a session that went away or by a callback that throws.
        /// Duplicating it here would double every report in the log.
        /// </summary>
        private static Action<VaultFoldMigrationReport> FoldCompletionReporter(Session session)
        {
            return report =>
            {
                if (report == null)
                    return;

                SendToOperator(session, report.Render(ChatAccountLineCap));
            };
        }

        /// <summary>
        /// Sends one line to the admin who started a run, IF their session is still in the world.
        ///
        /// The liveness test is the one CommandHandlerHelper uses (`State == WorldConnected &amp;&amp;
        /// Player != null`) rather than a null check on session.Network, which is not a logout signal -
        /// Network survives a session that has left the world.
        /// </summary>
        private static void SendToOperator(Session session, string message)
        {
            if (session == null)
                return;

            if (session.State != ACE.Server.Network.Enum.SessionState.WorldConnected || session.Player == null)
                return;

            try
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"[VAULTCLASS] {message}", ChatMessageType.System));
            }
            catch (Exception ex)
            {
                // A send that fails must cost the line and not the migration. This runs on the migration's
                // worker thread, and the run is one-way work that must not be lost over a status update.
                log.Warn($"[VAULT] could not send a fold migration line to the operator's session: {ex.GetFullMessage()}");
            }
        }
    }

    /// <summary>One account_vault_class row as the inspect report sees it. Data only.</summary>
    internal sealed class VaultClassInspectRow
    {
        public string ClassKey;
        public uint Wcid;
        public long Count;
        public long TotalValue;
        public int ValueBandPct;
        public int PerItemShare;

        /// <summary>The payload's Name, or the materialized probe's, for a row an operator has to recognise by sight.</summary>
        public string Name;

        public bool Parses;
        public bool Materializes;

        /// <summary>Whether the LIVE store is serving this row. Only meaningful when <see cref="VaultClassInspectReport.ServedKeysKnown"/> is true.</summary>
        public bool Served;

        /// <summary>Why this row is not fully healthy, or null when it is.</summary>
        public string Note;

        /// <summary>A row is STRANDED when its items exist on the row but nothing can hand them back.</summary>
        public bool Stranded => !Parses || !Materializes;
    }

    /// <summary>
    /// The inspect command's accumulator. Pure: it holds no session, reaches no database and writes
    /// nothing, so it is unit-testable on its own - the same split the dry run's report uses.
    /// </summary>
    internal sealed class VaultClassInspectReport
    {
        public string Scope = "(unset)";

        /// <summary>False when the live store could not be read, which makes every row's Served flag meaningless rather than false.</summary>
        public bool ServedKeysKnown;

        public readonly List<VaultClassInspectRow> Rows = new List<VaultClassInspectRow>();

        public int StrandedCount => Rows.Count(r => r.Stranded);

        public string Render()
        {
            var text = new StringBuilder();

            text.Append($"Vault item-class rows for {Scope}: {Rows.Count} row(s), {StrandedCount} stranded.");

            if (Rows.Count == 0)
            {
                text.Append("\n  (none)");
                return text.ToString();
            }

            foreach (var row in Rows.OrderByDescending(r => r.Stranded).ThenBy(r => r.ClassKey, StringComparer.Ordinal))
            {
                var health = row.Stranded ? "STRANDED" : "ok";

                var served = !ServedKeysKnown
                    ? "served unknown"
                    : row.Served ? "served" : "NOT served by the live store";

                text.Append($"\n  {row.ClassKey} wcid {row.Wcid} x{row.Count}");
                text.Append($" total {row.TotalValue}, share {row.PerItemShare}, band {row.ValueBandPct}");
                text.Append($"\n    {health}, {served}, parses {row.Parses}, materializes {row.Materializes}");

                if (!string.IsNullOrEmpty(row.Name))
                    text.Append($", \"{row.Name}\"");

                if (!string.IsNullOrEmpty(row.Note))
                    text.Append($"\n    note: {row.Note}");
            }

            if (StrandedCount > 0)
                text.Append("\n  A stranded row still holds its count, total and payload - nothing is lost. It cannot be withdrawn until the payload can be rebuilt. This command does not repair; that is a separate decision.");

            return text.ToString();
        }
    }

    /// <summary>
    /// The dry run's accumulator. Pure: it takes stored items, reads them, and counts. It holds no
    /// session, reaches no database and writes nothing, so it is unit-testable on its own.
    /// </summary>
    internal sealed class VaultClassDryRunReport
    {
        /// <summary>The comparison bands, alongside the shipping policy. 0 means Value is excluded from identity and pooled instead - the owner's ruling.</summary>
        internal static readonly int[] Bands = { VaultItemClass.ValueExcluded, 10, 25 };

        public string Scope = "(unset)";

        public int AccountsAvailable;
        public int AccountsScanned;
        public int AccountsNotReady;
        public int AccountsUnavailable;

        public long LedgerRows;
        public long LedgerUnits;

        /// <summary>Counted item-class rows already stored, and the items they stand for. Reported separately because they are this command's OUTCOME, never an input to its ratio.</summary>
        public long ClassRows;
        public long ClassItems;

        public long ItemsSeen;
        public long Classifiable;

        /// <summary>Refusal reason -> how many items gave it. The key is either a VaultCollapse.ClassRefusal token or the first non-whitelisted diff prefix.</summary>
        public readonly Dictionary<string, long> Refusals = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Distinct class keys per band, in the order <see cref="Bands"/> lists them.</summary>
        private readonly HashSet<string>[] keys = Bands.Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToArray();

        public int DistinctClasses(int bandIndex) => keys[bandIndex].Count;

        /// <summary>
        /// Runs the predicate over one stored item and folds the answer in.
        ///
        /// The item is only READ. TryDescribeClass takes the item's own BiotaDatabaseLock read lock and
        /// nothing here holds a lock when it is called, which is the rule that method's remarks state.
        /// </summary>
        public void Classify(WorldObject item)
        {
            if (item == null)
                return;

            ItemsSeen++;

            if (!VaultCollapse.TryDescribeClass(item, out var overrides, out var refusalReason))
            {
                var reason = string.IsNullOrEmpty(refusalReason) ? "unknown" : refusalReason;

                Refusals.TryGetValue(reason, out var count);
                Refusals[reason] = count + 1;

                return;
            }

            Classifiable++;

            for (var i = 0; i < Bands.Length; i++)
                keys[i].Add(VaultItemClass.ClassKey(item.WeenieClassId, overrides, Bands[i]));
        }

        /// <summary>One chat block. Ratios are printed to two decimals and never inverted, so "9.90x" always means items per class.</summary>
        public string Render()
        {
            var text = new StringBuilder();

            text.Append($"Vault item-class dry run over {Scope}.");
            text.Append($"\n  accounts: {AccountsScanned} scanned, {AccountsNotReady} not ready (re-run to include them), {AccountsUnavailable} unavailable");
            text.Append($"\n  stored biotas: {ItemsSeen} seen, {Classifiable} classifiable ({Percent(Classifiable, ItemsSeen)})");
            text.Append($"\n  existing collapsed ledger: {LedgerRows} rows, {LedgerUnits} units (already counted, not biotas)");
            text.Append($"\n  existing item classes: {ClassRows} rows, {ClassItems} items (already counted, not biotas)");

            for (var i = 0; i < Bands.Length; i++)
            {
                var band = Bands[i] == VaultItemClass.ValueExcluded ? "band 0 (Value pooled, SHIPPING)" : $"band {Bands[i]}";

                text.Append($"\n  {band}: {DistinctClasses(i)} distinct classes, {Ratio(Classifiable, DistinctClasses(i))} collapse");
            }

            if (Refusals.Count == 0)
            {
                text.Append("\n  refusals: none");
            }
            else
            {
                text.Append("\n  refusals, most common first:");

                foreach (var kvp in Refusals.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
                    text.Append($"\n    {kvp.Value,8} {kvp.Key}");
            }

            return text.ToString();
        }

        private static string Percent(long part, long whole)
        {
            if (whole <= 0)
                return "n/a";

            return (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        private static string Ratio(long items, int classes)
        {
            if (classes <= 0)
                return "n/a";

            return ((double)items / classes).ToString("0.00", CultureInfo.InvariantCulture) + "x";
        }
    }
}
