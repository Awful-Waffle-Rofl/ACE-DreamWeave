using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

using log4net;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Managers
{
    /// <summary>
    /// The OPERATOR path for the counted class tier's migration: drain every account's foldable stored
    /// biotas into class rows in one maintenance window, and then say plainly whether the fleet is
    /// finished.
    ///
    /// THIS IS THE ONLY WAY A FOLD HAPPENS. A background rotation on the world heartbeat used to fold at
    /// most one store per second, server-wide, skipping any store that was not already warm - so on a
    /// live shard most passes landed on a cold store and did nothing: measured on prod over five
    /// consecutive minutes on 2026-09-25 it examined 625 candidates and folded 0, with 15,423 still
    /// waiting. It was the right shape for background drift and the wrong shape for finishing, and it was
    /// removed once this command had finished the migration on prod (2026-09-26, 132 of 132 accounts,
    /// 15,901 items folded). Re-running this command is the migration route from now on, and it is
    /// re-runnable: the residual inputs are a deposit taken while account_vault_class_storage was false, a
    /// barrel restore of a pre-migration bag, and a shard restored from a pre-migration backup.
    ///
    /// THREADING, and the precedent for it. This runs on its own background thread, exactly as
    /// <see cref="AccountVaultBarrelReaper"/> does, and reaches the vault through the same door: the
    /// store's mutation queue, via AccountVaultStore.Enqueue. The reaper already destroys vault biotas
    /// off the world loop that way (AccountVaultBarrelReaper.cs:160 -> AccountVaultStore.TryPurgeFromBarrel
    /// -> IAccountVaultWorldSource.DestroyItem -> WorldObject.Destroy), so this is not new ground; see
    /// AccountVaultStore.FoldSomeStoredItemsOnQueue's remarks for the branch-by-branch reason
    /// WorldObject.Destroy is inert for a vault item and for the doc comment this corrected.
    ///
    /// It is deliberately SERIAL - one account at a time, one batch at a time - because the store's
    /// queue serializes per account anyway and because a maintenance window wants predictable load, not
    /// the maximum. Each account is wrapped in its own try/catch, again like the reaper, so one broken
    /// account costs that account and not the rest of the fleet.
    ///
    /// NOTHING HERE READS PropertyManager OFF A PATH THAT HAS ONE. The store reads its own tunables on
    /// the mutation queue as it always has; this class reads account_vault_class_storage through
    /// AccountVaultStore.ClassStorageEnabled, which every fold pass already reads, so a process without
    /// a shard fails in the same place it always did rather than in a new one.
    /// </summary>
    public static class AccountVaultFoldMigration
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// How many stored biotas one batch may try when the operator does not say.
        ///
        /// The same 25 the retired background rotation shipped with, deliberately, even though this batch
        /// is NOT on the world loop. What bounds it is not the world tick but the store's own mutation
        /// queue: a player opening
        /// their vendor window on an account being migrated waits behind whatever batch is in flight, and
        /// a fold costs far more per item than a refusal. 25 keeps that wait in the low tens of
        /// milliseconds at the rates the stage run measured.
        /// </summary>
        public const int DefaultBatchSize = 25;

        /// <summary>
        /// Upper bound on the batch size. The COMMAND rejects a larger ask by name rather than clamping,
        /// because an operator who typed 2000 and silently got 200 would report the wrong number in the
        /// window's notes; <see cref="ClampBatch"/> is the belt for programmatic callers.
        /// </summary>
        public const int MaxBatchSize = 200;

        /// <summary>
        /// How long one account's store is given to finish loading before it is reported
        /// <see cref="VaultFoldAccountTerminal.NotWarm"/> and skipped.
        ///
        /// A vault's containers load their inventories ASYNCHRONOUSLY (AccountVaultStore R4), so reading
        /// IsLoaded once is not enough: the honest answer for a cold store is "not yet". Polling is what
        /// turns that into a wait instead of a false negative, and the timeout is what stops one
        /// unloadable account from holding the whole fleet.
        ///
        /// Mutable, and internal rather than private, ONLY so a test can shorten it. A test that changes
        /// it must restore it in a finally: this is process-static state.
        /// </summary>
        internal static TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Gap between two IsLoaded polls. Same test-only mutability caveat as <see cref="ReadyTimeout"/>.</summary>
        internal static TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(25);

        /// <summary>
        /// Hard ceiling on the batches one account may take before it is reported
        /// <see cref="VaultFoldAccountTerminal.BatchLimit"/>.
        ///
        /// A SAFETY NET, NOT A BUDGET. The per-account loop runs until the store reports a terminal other
        /// than MoreToDo, and the states that produce MoreToDo are all monotone - an item either folds
        /// (and is gone) or is refused (and is remembered) or the pass releases its target class, after
        /// which the next pass cannot release one again - so the loop terminates. This bound exists
        /// because that argument is about code that will be edited, and an unbounded loop on an
        /// operator's thread is the wrong thing to be wrong about. At the default batch size it allows
        /// two and a half million items on one account.
        ///
        /// Same test-only mutability caveat as <see cref="ReadyTimeout"/>.
        /// </summary>
        internal static int MaxBatchesPerAccount = 100000;

        // ---- one run at a time ----

        private static int running;

        /// <summary>Whether a migration run is in flight, server-wide.</summary>
        public static bool IsRunning => Interlocked.CompareExchange(ref running, 0, 0) != 0;

        private static volatile bool stopRequested;

        /// <summary>
        /// Asks the running migration to stop after the batch in flight. Idempotent, and a no-op when
        /// nothing is running.
        ///
        /// A stop is NOT a rollback and cannot lose an item: every batch either folded an item outright
        /// or put it back, and the report that follows says INCOMPLETE with each account's terminal.
        /// </summary>
        public static void RequestStop() => stopRequested = true;

        /// <summary>
        /// Takes the run flag after checking every precondition, or refuses with a reason an operator can
        /// act on. <see cref="EndRun"/> releases it.
        ///
        /// THE PRECONDITIONS ARE THE POINT OF THIS CLASS, not paperwork in front of it. With
        /// account_vault_class_storage off, every batch returns
        /// <see cref="VaultFoldBatchTerminal.StorageDisabled"/> having examined nothing, so an
        /// unguarded run would walk the whole fleet, fold zero, and - if completion were inferred from
        /// "nothing folded" the way it once was - report the migration finished. That is not a
        /// hypothetical: a stage run declared completion when the log roll-ups went quiet while the two
        /// largest vaults had folded zero. The switch is checked here so the answer is a named refusal
        /// instead of a clean-looking zero.
        /// </summary>
        internal static bool TryBeginRun(out string refusal)
        {
            refusal = null;

            if (!AccountVaultStore.ClassStorageEnabled)
            {
                refusal = StorageDisabledRefusal;
                return false;
            }

            // Checked BEFORE the flag is taken, so a refusal never leaves the flag set.
            if (VaultClassCommands.ScanInFlight)
            {
                refusal = "A vault class scan (/vaultclassdryrun or /vaultclassinspect) is running. Wait for it to finish - it walks the same stores this would be destroying biotas out of, and its report would describe no moment in particular.";
                return false;
            }

            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                refusal = "A vault fold migration is already running.";
                return false;
            }

            stopRequested = false;

            return true;
        }

        /// <summary>Releases the run flag. Safe to call when it was never taken.</summary>
        internal static void EndRun() => Interlocked.Exchange(ref running, 0);

        /// <summary>
        /// The refusal text for the kill switch, in one place because both
        /// <see cref="TryBeginRun"/> and <see cref="RunSynchronously"/> give it and a Runbook quotes it.
        /// It names the tunable, because "the tier is off" without the key is a message that sends an
        /// operator to read source.
        /// </summary>
        internal const string StorageDisabledRefusal =
            "account_vault_class_storage is OFF, so nothing can fold. Turn it on before migrating; " +
            "running now would visit every account, fold nothing, and prove nothing.";

        /// <summary>
        /// The report from the most recently FINISHED run in this process, or null when none has finished.
        ///
        /// It exists so the result survives the operator. A migration runs on a background thread while the
        /// admin who started it can relog, crash out or be disconnected, and a result that only ever
        /// existed in one chat window is a result nobody can go back to. /vaultclassfold status reprints
        /// this, and the log line is the other copy.
        /// </summary>
        public static VaultFoldMigrationReport LastReport => Volatile.Read(ref lastReport);

        private static VaultFoldMigrationReport lastReport;

        /// <summary>
        /// Starts a WHOLE-FLEET migration on a background thread, or refuses with a reason.
        ///
        /// <paramref name="accountLimit"/> at or below zero means every account the index names;
        /// a positive value takes the first that many, the same truncation /vaultclassdryrun all n does.
        /// A truncated run can never report COMPLETE, because accountsVisited then falls short of
        /// accountsOwningAVault - which is the completion guard doing exactly what it is for.
        ///
        /// Both callbacks arrive on the WORKER thread, not the caller's, so a session-bound callback has
        /// to re-check that its session is still attached. <paramref name="onProgress"/> is called after
        /// every batch and after every account, so it must be cheap and must do its own rate limiting.
        /// </summary>
        public static bool TryStartFleetRun(int batch, int accountLimit, out string refusal,
                                           Action<VaultFoldMigrationProgress> onProgress = null,
                                           Action<VaultFoldMigrationReport> onFinished = null)
        {
            if (!TryBeginRun(out refusal))
                return false;

            var size = ClampBatch(batch);

            StartWorker(() => RunFleet(size, accountLimit, onProgress), onFinished);

            return true;
        }

        /// <summary>
        /// Starts a migration over ONE account. <paramref name="scope"/> is how the report names what it
        /// covered, and it is required rather than derived because only the caller knows the operator's
        /// spelling of the account.
        ///
        /// A single-account run can report COMPLETE, and that COMPLETE means only that this one account is
        /// migrated. The report says so in as many words (see VaultFoldMigrationReport.FleetWide), because
        /// the headline is the string a Runbook tells an operator to grep for and a scoped COMPLETE must
        /// not be mistakable for the fleet's.
        /// </summary>
        public static bool TryStartAccountRun(uint accountId, string scope, int batch, out string refusal,
                                              Action<VaultFoldMigrationProgress> onProgress = null,
                                              Action<VaultFoldMigrationReport> onFinished = null)
        {
            refusal = null;

            if (accountId == 0)
            {
                refusal = "Account id 0 is not a real account.";
                return false;
            }

            if (!TryBeginRun(out refusal))
                return false;

            var size = ClampBatch(batch);

            StartWorker(() =>
            {
                var report = RunSynchronously(new[] { accountId }, AccountVaultManager.GetStore, size,
                                              () => stopRequested,
                                              scope ?? $"account {accountId}",
                                              fleetWide: false,
                                              onProgress: onProgress);

                LogReport(report);

                return report;
            }, onFinished);

            return true;
        }

        /// <summary>
        /// The thread, and the one place the run flag is released and <see cref="LastReport"/> is set.
        ///
        /// Modelled on AccountVaultBarrelReaper.Start: a bare background thread rather than a timer, so a
        /// throw cannot be swallowed the way a Timers.Timer Elapsed handler's is - and then caught here
        /// anyway, because a migration that stopped with nothing in the log is indistinguishable from one
        /// that finished, which is the failure this whole class is shaped against.
        /// </summary>
        private static void StartWorker(Func<VaultFoldMigrationReport> body, Action<VaultFoldMigrationReport> onFinished)
        {
            var worker = new Thread(() =>
            {
                VaultFoldMigrationReport report = null;

                try
                {
                    report = body();
                }
                catch (Exception ex)
                {
                    // Recorded as a REFUSED report rather than left null, so /vaultclassfold status can
                    // answer "it died, and here is why" instead of "nothing has run".
                    report = new VaultFoldMigrationReport
                    {
                        Refusal = $"The migration failed outright: {ex.GetFullMessage()}",
                        FinishedUtc = DateTime.UtcNow,
                    };

                    log.Error($"[VAULT] the fold migration failed outright: {ex.GetFullMessage()}");
                }
                finally
                {
                    Volatile.Write(ref lastReport, report);

                    EndRun();
                }

                try
                {
                    onFinished?.Invoke(report);
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] the fold migration's completion callback threw: {ex.GetFullMessage()}");
                }
            })
            {
                IsBackground = true,
                Name = "AccountVaultFoldMigration",
            };

            worker.Start();
        }

        /// <summary>Clamps an operator-supplied batch size into [1, <see cref="MaxBatchSize"/>].</summary>
        public static int ClampBatch(int batch)
        {
            if (batch <= 0)
                return DefaultBatchSize;

            return batch > MaxBatchSize ? MaxBatchSize : batch;
        }

        /// <summary>
        /// Writes the completion statement to log4net UNCONDITIONALLY, at Info when complete and Warn when
        /// not. The log is the copy that outlives the session that asked for the run.
        /// </summary>
        private static void LogReport(VaultFoldMigrationReport report)
        {
            var rendered = report.Render();

            if (report.IsComplete)
                log.Info(rendered);
            else
                log.Warn(rendered);
        }

        /// <summary>
        /// Reads the fleet list and runs the migration over it, logging the completion statement.
        ///
        /// A NULL index read is a REFUSAL, never an empty fleet - the same discipline
        /// VaultClassCommands.HandleVaultClassDryRun keeps, and for the same reason: reading a database
        /// blip as "no account owns a vault" would turn it into a decisive COMPLETE over an untouched
        /// shard.
        /// </summary>
        private static VaultFoldMigrationReport RunFleet(int batch, int accountLimit, Action<VaultFoldMigrationProgress> onProgress)
        {
            var accountIds = DatabaseManager.Shard.BaseDatabase.GetAllAccountVaultAccountIds();

            if (accountIds == null)
            {
                var refused = new VaultFoldMigrationReport
                {
                    Batch = batch,
                    Scope = "whole fleet",
                    Refusal = "The account_vault index could not be read, so the fleet is unknown. Nothing was migrated.",
                    FinishedUtc = DateTime.UtcNow,
                };

                log.Error(refused.Render());

                return refused;
            }

            // The TRUNCATION is applied to the visited list while AccountsOwningAVault still counts the
            // whole index, which is what makes `all n` report INCOMPLETE by construction. Trimming the
            // denominator instead would let a 25-account sample of a 15,000-account fleet say COMPLETE.
            var selected = accountLimit > 0 ? accountIds.Take(accountLimit).ToList() : accountIds;

            var fleetWide = selected.Count == accountIds.Count;

            var scope = fleetWide
                ? "whole fleet"
                : $"the first {selected.Count} of {accountIds.Count} account(s) that own a vault";

            var report = RunSynchronously(selected, AccountVaultManager.GetStore, batch,
                                          () => stopRequested, scope, fleetWide, onProgress);

            report.AccountsOwningAVault = accountIds.Count;

            LogReport(report);

            return report;
        }

        /// <summary>
        /// The testable half, and the one that does all the work.
        ///
        /// <paramref name="accounts"/> is THE INDEX READ: its count is what the report calls
        /// accountsOwningAVault, and the completion statement is false unless every one of them was
        /// visited. <paramref name="storeFor"/> is the same seam AccountVaultBarrelReaper takes, and it
        /// may answer NULL - so a test drives this over fake-backed stores with no thread and no
        /// database, and a production run never CREATES a store for an account nobody is using.
        /// <paramref name="shouldStop"/> may be null.
        ///
        /// It NEVER throws for one account's sake: a store that cannot be resolved, will not load,
        /// refuses work or throws becomes that account's terminal and the fleet walk continues.
        /// </summary>
        internal static VaultFoldMigrationReport RunSynchronously(IEnumerable<uint> accounts,
                                                                 Func<uint, AccountVaultStore> storeFor,
                                                                 int batch,
                                                                 Func<bool> shouldStop)
        {
            return RunSynchronously(accounts, storeFor, batch, shouldStop, scope: null, fleetWide: true, onProgress: null);
        }

        /// <summary>
        /// <see cref="RunSynchronously(IEnumerable{uint}, Func{uint, AccountVaultStore}, int, Func{bool})"/>
        /// with the two things only a production caller knows.
        ///
        /// <paramref name="scope"/> and <paramref name="fleetWide"/> are how the report says WHAT IT
        /// COVERED, and they are not decoration: the headline word is the string a Runbook tells an
        /// operator to grep for, so a COMPLETE over one account must be distinguishable at a glance from a
        /// COMPLETE over the fleet. The four-argument overload defaults fleetWide to true because its only
        /// callers are tests handing it the entire list they intend to be the fleet.
        ///
        /// <paramref name="onProgress"/> is called after EVERY batch and after every account, so it must be
        /// cheap and must do its own rate limiting. It is invoked inside a try/catch: a progress consumer
        /// that throws must not be able to abort a migration.
        /// </summary>
        internal static VaultFoldMigrationReport RunSynchronously(IEnumerable<uint> accounts,
                                                                 Func<uint, AccountVaultStore> storeFor,
                                                                 int batch,
                                                                 Func<bool> shouldStop,
                                                                 string scope,
                                                                 bool fleetWide,
                                                                 Action<VaultFoldMigrationProgress> onProgress)
        {
            if (storeFor == null)
                throw new ArgumentNullException(nameof(storeFor));

            var report = new VaultFoldMigrationReport
            {
                Batch = batch,
                Scope = scope,
                FleetWide = fleetWide,
            };

            var fleet = (accounts ?? Enumerable.Empty<uint>()).ToList();

            // Recorded BEFORE the refusal below, deliberately. A refused report then fails the completion
            // test on TWO independent clauses - a refusal is present AND zero of N accounts were visited -
            // rather than on the refusal alone. One clause guarding the worst outcome this class has is
            // one too few.
            report.AccountsOwningAVault = fleet.Count;

            // Re-checked HERE and not only in TryBeginRun, because this is the method a test and a
            // future caller both reach directly. A run with the switch off examines nothing on every
            // account, and the one thing it must not do is look like a finished migration.
            if (!AccountVaultStore.ClassStorageEnabled)
            {
                report.Refusal = StorageDisabledRefusal;
                report.FinishedUtc = DateTime.UtcNow;
                return report;
            }

            var timer = Stopwatch.StartNew();

            // Running totals rather than re-summing report.Accounts, because the progress callback fires
            // after every batch and a Sum over the fleet on each one would make progress reporting itself
            // O(accounts) per batch.
            var totals = new VaultFoldRunningTotals();

            foreach (var accountId in fleet)
            {
                if (shouldStop != null && shouldStop())
                {
                    // Every account after this one is simply not visited, which is exactly what makes the
                    // headline INCOMPLETE: accountsVisited falls short of accountsOwningAVault.
                    report.StoppedEarly = true;
                    break;
                }

                var result = new VaultFoldAccountResult { AccountId = accountId };

                AccountVaultStore store = null;

                try
                {
                    store = storeFor(accountId);
                }
                catch (Exception ex)
                {
                    result.Note = ex.GetFullMessage();
                }

                if (store == null)
                {
                    // NOT counted as visited. An account whose store could not be resolved has had
                    // nothing done to it, and counting it would let the fleet guard pass over an account
                    // the migration never opened.
                    result.Terminal = VaultFoldAccountTerminal.StoreUnavailable;
                    report.Accounts.Add(result);
                    continue;
                }

                report.AccountsVisited++;

                // Per account, exactly as AccountVaultBarrelReaper.TryPurgeRow does per row: one
                // account's failure costs that account.
                try
                {
                    RunAccount(store, batch, shouldStop, result, totals, onProgress, report, timer);
                }
                catch (Exception ex)
                {
                    result.Terminal = VaultFoldAccountTerminal.Threw;
                    result.Note = ex.GetFullMessage();

                    log.Error($"[VAULT] the fold migration threw on account {accountId} and moved on: {ex.GetFullMessage()}");
                }

                report.Accounts.Add(result);

                if (result.IsComplete)
                    totals.AccountsComplete++;

                ReportProgress(onProgress, report, totals, accountId, timer);
            }

            report.Elapsed = timer.Elapsed;
            report.FinishedUtc = DateTime.UtcNow;

            return report;
        }

        /// <summary>
        /// Fleet-wide counters carried alongside the report so the progress callback does not have to sum
        /// the per-account list on every batch.
        /// </summary>
        private sealed class VaultFoldRunningTotals
        {
            public int Folded;
            public int Examined;
            public int Batches;
            public int AccountsComplete;
        }

        /// <summary>
        /// Hands one progress snapshot to the consumer, swallowing anything it throws.
        ///
        /// The swallow is deliberate and is the same judgement AccountVaultBarrelReaper's per-row catch
        /// makes: a chat send that fails, a session that went away mid-write, or a log appender that
        /// throws must cost the progress line and NOT the migration. Losing the run over a status update
        /// would be the worse failure by a wide margin, because the fold is one-way.
        /// </summary>
        private static void ReportProgress(Action<VaultFoldMigrationProgress> onProgress,
                                           VaultFoldMigrationReport report,
                                           VaultFoldRunningTotals totals,
                                           uint currentAccountId,
                                           Stopwatch timer)
        {
            if (onProgress == null)
                return;

            try
            {
                onProgress(new VaultFoldMigrationProgress
                {
                    Scope = report.Scope,
                    AccountsOwningAVault = report.AccountsOwningAVault,
                    AccountsVisited = report.AccountsVisited,
                    AccountsComplete = totals.AccountsComplete,
                    CurrentAccountId = currentAccountId,
                    Folded = totals.Folded,
                    Examined = totals.Examined,
                    Batches = totals.Batches,
                    Elapsed = timer.Elapsed,
                });
            }
            catch (Exception ex)
            {
                log.Warn($"[VAULT] a fold migration progress consumer threw and was ignored: {ex.GetFullMessage()}");
            }
        }

        /// <summary>
        /// One account: pin it, wait for it to load, then batch until the store says something other
        /// than MoreToDo.
        ///
        /// AddWindow/RemoveWindow bracket the whole account rather than each batch, and that is not
        /// tidiness. AccountVaultManager's idle sweep can retire a store between two batches, and a
        /// retired store refuses work - AccountVaultStore.Enqueue returns false, which would show up
        /// here as a truncated account that nothing explains. A window pins it (AccountVaultStore.TryEvict
        /// declines on OpenWindows &gt; 0), and the RemoveWindow is in a finally so a throw cannot leave
        /// an account pinned in memory for the life of the process.
        ///
        /// AN ACCOUNT WHOSE STORE ALREADY HAS A WINDOW OPEN IS SKIPPED, not folded, and the ORDER of that
        /// check against AddWindow below is the whole of it: the check must run BEFORE this method opens
        /// its own window, or the migration reads its own pin as somebody else's and skips every account
        /// in the fleet. There is no way to tell the two apart afterwards, because OpenWindows is a count
        /// and not an owner list.
        ///
        /// Why skip at all, since the fold is correct either way. An open window is a player's mule panel,
        /// and that panel is PULL-ONLY: PersonalVendor.ViewIsStale has exactly one consumer, on the
        /// REFUSED-buy branch of Player_Commerce.HandleActionBuyItem (Player_Commerce.cs:80), so nothing
        /// pushes a rebuild when the store's Version moves. A player watching their vault fold keeps
        /// seeing pre-fold rows until a click fails, and the failed click is what rebuilds the panel. That
        /// much would be tolerable on its own. What is not is that a fold ADDS to a class row rather than
        /// removing one, so a stale panel can UNDERSTATE a count rather than merely name a row that is
        /// gone, and whether a buy against a stale ledger display object withdraws the DISPLAYED quantity
        /// or re-reads the row was never traced. Skipping makes that question unreachable, which is worth
        /// more than the answer: this is a one-time migration and the cost of the skip is one re-run.
        /// </summary>
        private static void RunAccount(AccountVaultStore store,
                                       int batch,
                                       Func<bool> shouldStop,
                                       VaultFoldAccountResult result,
                                       VaultFoldRunningTotals totals,
                                       Action<VaultFoldMigrationProgress> onProgress,
                                       VaultFoldMigrationReport report,
                                       Stopwatch timer)
        {
            // BEFORE AddWindow, and never after it. See this method's remarks: after the line below this
            // store has a window of our own on it and the count can no longer answer the question.
            if (store.OpenWindows > 0)
            {
                result.Terminal = VaultFoldAccountTerminal.WindowOpen;
                return;
            }

            store.AddWindow();

            try
            {
                if (!TryWaitUntilLoaded(store, shouldStop, out var stoppedWhileWaiting))
                {
                    result.Terminal = stoppedWhileWaiting
                        ? VaultFoldAccountTerminal.Stopped
                        : VaultFoldAccountTerminal.NotWarm;

                    return;
                }

                while (true)
                {
                    if (shouldStop != null && shouldStop())
                    {
                        result.Terminal = VaultFoldAccountTerminal.Stopped;
                        return;
                    }

                    if (result.Batches >= MaxBatchesPerAccount)
                    {
                        result.Terminal = VaultFoldAccountTerminal.BatchLimit;

                        log.Error($"[VAULT] the fold migration gave up on account {store.AccountId} after {result.Batches} batch(es) without the store reporting it was finished. This is a safety net, not an expected state - the store kept answering MoreToDo.");

                        return;
                    }

                    VaultFoldBatchResult batchResult = null;

                    // Enqueue returns false only when the idle sweep retired this store, which the open
                    // window above is meant to prevent. It is still checked, because the alternative is
                    // reading "nothing ran" as "nothing left to do".
                    if (!store.Enqueue(() => store.FoldSomeStoredItemsOnQueue(Time.GetUnixTime(), batch, out batchResult), out var thrown))
                    {
                        result.Terminal = VaultFoldAccountTerminal.StoreRetired;
                        return;
                    }

                    if (thrown != null)
                    {
                        // A throw is NOT a refusal: the batch got some unknown distance in. The account
                        // is reported as Threw and left for a later run, which is safe because the fold
                        // is idempotent - a folded item is gone and a refused one is remembered.
                        result.Terminal = VaultFoldAccountTerminal.Threw;
                        result.Note = thrown.GetFullMessage();

                        log.Error($"[VAULT] the fold migration's batch threw on account {store.AccountId} after {result.Batches} batch(es): {thrown.GetFullMessage()}");

                        return;
                    }

                    if (batchResult == null)
                    {
                        // Unreachable through the store's own seam, which always assigns its out
                        // parameter. Guarded anyway rather than dereferenced on faith, because the wrong
                        // answer to a null here would be an infinite loop.
                        result.Terminal = VaultFoldAccountTerminal.Threw;
                        result.Note = "the batch reported no result";
                        return;
                    }

                    result.Batches++;
                    result.Examined += batchResult.Examined;
                    result.Folded += batchResult.Folded;
                    result.RefusedPredicate += batchResult.RefusedPredicate;
                    result.RefusedRoundTrip += batchResult.RefusedRoundTrip;
                    result.RefusedTakeOut += batchResult.RefusedTakeOut;
                    result.WrongClass += batchResult.WrongClass;
                    result.Aborted += batchResult.Aborted;

                    totals.Batches++;
                    totals.Examined += batchResult.Examined;
                    totals.Folded += batchResult.Folded;

                    // Per BATCH as well as per account, because one large vault can be minutes of work on
                    // its own and an operator watching a silent window cannot tell it apart from a wedged
                    // run. The consumer rate-limits.
                    ReportProgress(onProgress, report, totals, store.AccountId, timer);

                    if (batchResult.Terminal == VaultFoldBatchTerminal.MoreToDo)
                        continue;

                    result.Terminal = FromBatch(batchResult.Terminal);
                    return;
                }
            }
            finally
            {
                store.RemoveWindow();
            }
        }

        /// <summary>
        /// Polls AccountVaultStore.IsLoaded until it is true or <see cref="ReadyTimeout"/> runs out.
        ///
        /// IsLoaded rather than IsWarm, and READING it is what performs the load - the same call
        /// AccountVaultManager's warm queue makes. That is the whole reason this runs off the world
        /// loop: the load is a synchronous index read, a stack ledger read, a class ledger read and one
        /// biota read per vault, and on this thread none of it is on a tick.
        ///
        /// The first check happens BEFORE any sleep and before <paramref name="shouldStop"/> is
        /// consulted, so an already-warm store costs one property read and no stop call. That ordering is
        /// what lets a test tell a timeout apart from a store that was never asked.
        /// </summary>
        private static bool TryWaitUntilLoaded(AccountVaultStore store, Func<bool> shouldStop, out bool stopped)
        {
            stopped = false;

            var waited = Stopwatch.StartNew();

            while (true)
            {
                if (store.IsLoaded)
                    return true;

                if (shouldStop != null && shouldStop())
                {
                    stopped = true;
                    return false;
                }

                if (waited.Elapsed >= ReadyTimeout)
                    return false;

                Thread.Sleep(ReadyPollInterval);
            }
        }

        /// <summary>
        /// Maps a batch terminal onto an account terminal. Total over
        /// <see cref="VaultFoldBatchTerminal"/> - a switch expression with no default, so adding a member
        /// to that enum is a compile error here rather than a silently wrong report.
        /// </summary>
        private static VaultFoldAccountTerminal FromBatch(VaultFoldBatchTerminal terminal)
        {
            switch (terminal)
            {
                case VaultFoldBatchTerminal.MoreToDo:
                    return VaultFoldAccountTerminal.MoreToDo;

                case VaultFoldBatchTerminal.Exhausted:
                    return VaultFoldAccountTerminal.Exhausted;

                case VaultFoldBatchTerminal.NotWarm:
                    return VaultFoldAccountTerminal.NotWarm;

                case VaultFoldBatchTerminal.StorageDisabled:
                    return VaultFoldAccountTerminal.StorageDisabled;

                case VaultFoldBatchTerminal.BudgetZero:
                    return VaultFoldAccountTerminal.BudgetZero;

                case VaultFoldBatchTerminal.Aborted:
                    return VaultFoldAccountTerminal.Aborted;

                default:
                    throw new ArgumentOutOfRangeException(nameof(terminal), terminal, "unmapped fold batch terminal");
            }
        }
    }

    /// <summary>
    /// How one account's migration ended.
    ///
    /// A superset of <see cref="VaultFoldBatchTerminal"/>, because an account can end for reasons no
    /// batch ever saw: the store could not be resolved, it never finished loading, the operator stopped
    /// the run, or it was retired mid-account.
    /// </summary>
    public enum VaultFoldAccountTerminal
    {
        /// <summary>
        /// This account still has foldable items. FIRST so that it is default(T), for the same reason
        /// <see cref="VaultFoldBatchTerminal.MoreToDo"/> is: the safe direction for a forgotten
        /// assignment is "not finished".
        /// </summary>
        MoreToDo,

        /// <summary>
        /// The store reported it has nothing left to try. THE ONLY terminal that counts towards
        /// completion.
        /// </summary>
        Exhausted,

        /// <summary>The store did not finish loading inside AccountVaultFoldMigration.ReadyTimeout.</summary>
        NotWarm,

        /// <summary>account_vault_class_storage went off mid-run.</summary>
        StorageDisabled,

        /// <summary>The batch size reached the store as zero or less. A bug, not a state, but it is reported rather than hidden.</summary>
        BudgetZero,

        /// <summary>A batch stopped on a class-ledger refusal or outage.</summary>
        Aborted,

        /// <summary>The operator asked the run to stop, and it stopped in this account.</summary>
        Stopped,

        /// <summary>AccountVaultManager could not hand back a store for this account, so nothing was done to it.</summary>
        StoreUnavailable,

        /// <summary>The idle sweep retired the store mid-account and it refused further work.</summary>
        StoreRetired,

        /// <summary>A batch, or the account's own setup, threw. See the result's Note.</summary>
        Threw,

        /// <summary>The per-account batch ceiling was hit. A safety net; see AccountVaultFoldMigration.MaxBatchesPerAccount.</summary>
        BatchLimit,

        /// <summary>
        /// The store already had a window open when the migration reached it - in practice a player with
        /// their own mule panel up - so NOTHING was done to this account. Not an error and not complete:
        /// a fleet run that hits one reports INCOMPLETE and names the account, and a re-run clears it.
        ///
        /// See AccountVaultFoldMigration.RunAccount's remarks for why an open panel is a reason to stand
        /// off rather than a condition to fold through.
        /// </summary>
        WindowOpen,
    }

    /// <summary>One account's line in the migration report. Data only.</summary>
    public sealed class VaultFoldAccountResult
    {
        public uint AccountId;

        public VaultFoldAccountTerminal Terminal;

        /// <summary>Batches that actually RAN. Zero distinguishes an account that was never folded from one that folded nothing.</summary>
        public int Batches;

        public int Examined;
        public int Folded;
        public int RefusedPredicate;
        public int RefusedRoundTrip;
        public int RefusedTakeOut;
        public int WrongClass;

        /// <summary>Candidates a batch aborted on. See VaultFoldBatchResult.Aborted; at most one per batch.</summary>
        public int Aborted;

        /// <summary>
        /// The sum of every outcome bucket, which must equal <see cref="Examined"/>. Same reconciliation
        /// identity VaultFoldBatchResult.Accounted carries, summed over this account's batches.
        /// </summary>
        public int Accounted => Folded + RefusedPredicate + RefusedRoundTrip + RefusedTakeOut + WrongClass + Aborted;

        /// <summary>A throw's message, or why a store could not be resolved. Null when there is nothing to add.</summary>
        public string Note;

        /// <summary>An account is complete when, and only when, its store said it had nothing left.</summary>
        public bool IsComplete => Terminal == VaultFoldAccountTerminal.Exhausted;

        public override string ToString()
        {
            var text = new StringBuilder();

            text.Append($"account {AccountId}: {Terminal}");
            text.Append($", {Batches} batch(es), {Examined} examined, {Folded} folded");

            if (RefusedPredicate > 0)
                text.Append($", {RefusedPredicate} not classifiable");

            if (RefusedRoundTrip > 0)
                text.Append($", {RefusedRoundTrip} failed the round trip");

            if (RefusedTakeOut > 0)
                text.Append($", {RefusedTakeOut} could not be taken out");

            if (WrongClass > 0)
                text.Append($", {WrongClass} of another class");

            if (Aborted > 0)
                text.Append($", {Aborted} aborted on the class ledger");

            if (!string.IsNullOrEmpty(Note))
                text.Append($" [{Note}]");

            return text.ToString();
        }
    }

    /// <summary>
    /// The migration's completion statement, and the reason this class is the deliverable rather than
    /// the worker.
    ///
    /// COMPLETION IS NEVER INFERRED FROM "ZERO FOLDED". <see cref="IsComplete"/> is true only when every
    /// account the index named was VISITED and every one of them reported
    /// <see cref="VaultFoldAccountTerminal.Exhausted"/>. Both halves have to be there:
    ///
    /// - "every terminal is Exhausted" alone would pass a run that walked three of four accounts and
    ///   found the three it looked at finished. The fourth would never appear in the report at all.
    /// - "every account was visited" alone would pass a run that visited all of them with the kill
    ///   switch off, folding nothing on each.
    ///
    /// The failure this guards against already happened: a stage run was called complete because the
    /// log roll-ups went quiet, while the two largest vaults on the shard had folded zero. Quiet is
    /// what an idling fold and a finished migration have in common.
    ///
    /// The headline word is uppercase and the line begins `[VAULT] fold migration ` so a Runbook can
    /// tell an operator to grep for exactly that.
    /// </summary>
    public sealed class VaultFoldMigrationReport
    {
        /// <summary>Accounts the account_vault index named. The denominator of the completion test.</summary>
        public int AccountsOwningAVault;

        /// <summary>Accounts a store was resolved for and batches were attempted on.</summary>
        public int AccountsVisited;

        /// <summary>Per-account results, in fleet order, including accounts whose store could not be resolved.</summary>
        public readonly List<VaultFoldAccountResult> Accounts = new List<VaultFoldAccountResult>();

        /// <summary>Why the run did not start at all, or null when it ran. A refused run is never complete.</summary>
        public string Refusal;

        /// <summary>True when the fleet walk was cut short by a stop request rather than running out of accounts.</summary>
        public bool StoppedEarly;

        /// <summary>The batch size the run used, for the record.</summary>
        public int Batch;

        /// <summary>
        /// What this run COVERED, in the operator's terms: "whole fleet", a capped fleet, or one account.
        /// Rendered right after the headline word, because the headline is what a Runbook greps for and a
        /// COMPLETE over one account must not read as a COMPLETE over the shard.
        /// </summary>
        public string Scope;

        /// <summary>
        /// Whether this run's scope was EVERY account the index named. False for a single-account run and
        /// for a capped `all n`.
        ///
        /// It changes nothing about <see cref="IsComplete"/> - a scoped run that finished its scope really
        /// did finish it - and everything about what COMPLETE is evidence FOR. Only a fleet-wide COMPLETE
        /// is evidence that the migration is done, and <see cref="Render"/> says so out loud on any other
        /// run rather than leaving the reader to notice the scope.
        /// </summary>
        public bool FleetWide = true;

        /// <summary>When the run finished, so /vaultclassfold status can say how old its answer is.</summary>
        public DateTime FinishedUtc;

        public TimeSpan Elapsed;

        public int AccountsComplete => Accounts.Count(a => a.IsComplete);

        public int Folded => Accounts.Sum(a => a.Folded);

        public int Examined => Accounts.Sum(a => a.Examined);

        public int Batches => Accounts.Sum(a => a.Batches);

        /// <summary>
        /// The whole point. See the class remarks for why both clauses are load-bearing and neither is
        /// sufficient.
        /// </summary>
        public bool IsComplete =>
            Refusal == null
            && !StoppedEarly
            && AccountsVisited == AccountsOwningAVault
            && AccountsComplete == AccountsOwningAVault;

        /// <summary>Exactly COMPLETE or INCOMPLETE, uppercase. Nothing else is ever emitted here.</summary>
        public string Headline => IsComplete ? "COMPLETE" : "INCOMPLETE";

        /// <summary>
        /// One log block, with every non-complete account named. See
        /// <see cref="Render(int)"/> for the truncating form.
        /// </summary>
        public string Render() => Render(int.MaxValue);

        /// <summary>
        /// One block. The first line is the grep target; everything after it is detail, and every
        /// non-complete account is named individually rather than summarised, because "3 accounts did not
        /// finish" is not something an operator can act on.
        ///
        /// <paramref name="maxAccountLines"/> caps the per-account lines for a CHAT copy. The prod fleet is
        /// large enough that a fully expanded INCOMPLETE report is past what a client will render, and a
        /// report the client silently cut is worse than one that says it was cut - so the cut is counted
        /// and named, and the log copy (int.MaxValue) is always whole.
        /// </summary>
        public string Render(int maxAccountLines)
        {
            var text = new StringBuilder();

            text.Append($"[VAULT] fold migration {Headline}: ");
            text.Append($"{Scope ?? "the accounts given"}: ");
            text.Append($"{AccountsComplete} of {AccountsOwningAVault} account(s) exhausted, ");
            text.Append($"{AccountsVisited} visited, {Folded} item(s) folded from {Examined} examined ");
            text.Append($"in {Batches} batch(es) over {Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s (batch size {Batch}).");

            // Said on EVERY scoped run, complete or not, and never left to the reader to infer from the
            // scope string. The completion criterion an operator is told to look for is a fleet-wide
            // COMPLETE, so a scoped one has to disclaim itself in the same block.
            if (!FleetWide)
                text.Append("\n  SCOPED RUN: this covered only the scope above, NOT the whole fleet, so its headline says nothing about whether the migration is finished. Only `/vaultclassfold all` with no count can answer that.");

            if (Refusal != null)
            {
                text.Append($"\n  REFUSED: {Refusal}");
                return text.ToString();
            }

            if (StoppedEarly)
                text.Append($"\n  The run was STOPPED before the fleet was walked, so {AccountsOwningAVault - AccountsVisited} account(s) were never visited.");

            var unfinished = Accounts.Where(a => !a.IsComplete).ToList();

            if (unfinished.Count == 0 && AccountsVisited == AccountsOwningAVault)
                return text.ToString();

            text.Append("\n  not complete:");

            var shown = 0;

            foreach (var account in unfinished)
            {
                if (shown >= maxAccountLines)
                {
                    text.Append($"\n    ... and {unfinished.Count - shown} more not-complete account(s). The server log carries the whole list; grep it for: [VAULT] fold migration");
                    break;
                }

                text.Append($"\n    {account}");
                shown++;
            }

            if (AccountsVisited != AccountsOwningAVault)
                text.Append($"\n    plus {AccountsOwningAVault - AccountsVisited} account(s) the index named that this run never visited. Re-run to cover them.");

            return text.ToString();
        }
    }

    /// <summary>
    /// A snapshot of a migration in flight, handed to the progress consumer after every batch and every
    /// account.
    ///
    /// IT IS NOT A COMPLETION STATEMENT AND CARRIES NO HEADLINE, deliberately. Progress is the thing an
    /// operator watches while waiting, and the temptation this type exists to remove is reading a
    /// plateau in it as a finish - which is exactly the inference that let the original bug reach prod.
    /// Only <see cref="VaultFoldMigrationReport"/> answers whether a migration is done.
    /// </summary>
    public sealed class VaultFoldMigrationProgress
    {
        /// <summary>What the run covers, copied from the report so a progress line can name its own scope.</summary>
        public string Scope;

        public int AccountsOwningAVault;
        public int AccountsVisited;
        public int AccountsComplete;

        /// <summary>The account being worked on when this snapshot was taken.</summary>
        public uint CurrentAccountId;

        public int Folded;
        public int Examined;
        public int Batches;

        public TimeSpan Elapsed;

        /// <summary>One line, with no verdict in it.</summary>
        public string Render()
        {
            return $"fold migration in progress ({Scope ?? "the accounts given"}): "
                 + $"{AccountsVisited} of {AccountsOwningAVault} account(s) visited, {AccountsComplete} exhausted, "
                 + $"{Folded} folded from {Examined} examined in {Batches} batch(es) "
                 + $"over {Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s; on account {CurrentAccountId}.";
        }
    }
}
