using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// The mule partial-salvage combine: the vault-side consolidation pass that pours an account's
    /// class-stored PARTIAL salvage bags of one material (any workmanship) into as few bags as
    /// possible, as ONE atomic class exchange.
    /// </summary>
    public partial class AccountVaultStore
    {
        /// <summary>
        /// Upper bound on the steps one pass hands the exchange. The FIRST group a pass accepts is always
        /// taken whatever its size, so a pass always makes progress; later groups are only added while the
        /// running total stays at or under this. A group left out waits for the next partial deposit of its
        /// material. Bounds the number of rows one transaction locks FOR UPDATE.
        /// </summary>
        internal const int SalvageCombineMaxStepsPerPass = 200;

        /// <summary>
        /// Upper bound on the bags one group may expand into. A group above it is skipped and logged rather
        /// than planned, so a pathological row count can never turn one deposit into an unbounded
        /// allocation. Real accounts hold a handful of partials per material.
        /// </summary>
        internal const int SalvageCombineMaxBagsPerGroup = 10000;

        /// <summary>Test seam, null in production: runs just before the pass's exchange. See its call site.</summary>
        internal Action BeforeSalvageCombineExchangeForTest;

        /// <summary>One class row's state as the pass snapshotted it, raw ints only.</summary>
        private sealed class CombineRow
        {
            public string ClassKey;
            public uint Wcid;
            public long Count;
            public long TotalValue;
            public VaultItemClassOverrides Overrides;
            public int? Structure;
            public int? Iw;
            public int? Nim;
            public string Name;
            public string DisplayKey;
        }

        /// <summary>What the pass knows about one class key: its pre-state and, for an output key, how to create it.</summary>
        private sealed class CombineKeyInfo
        {
            public string ClassKey;
            public uint Wcid;
            public long ExpectedCount;
            public long ExpectedTotal;
            public string DisplayKey;
            public string Name;
            public AccountVaultClassSeed Seed;
        }

        /// <summary>
        /// Combines the account's class-stored partial salvage bags of the given wcids. Runs on the mutation
        /// queue and NEVER throws: anything unexpected is logged and answered with
        /// <see cref="SalvageCombineSummary.None"/> (and, if the exchange had already been attempted, the
        /// class map is re-read so no stale count is served).
        ///
        /// GATES, cheapest first, the first failure stops the pass with nothing touched:
        /// 1. server tunable account_vault_salvage_combine;
        /// 2. <see cref="ClassStorageEnabled"/>;
        /// 3. no open deposit window, and the store READY (which forces any pending class reload, so the
        ///    pre-state the exchange is built from is a fresh read and not a stale map);
        /// 4. THIS store's account setting (/mule autosalvage). A failed read SKIPS the pass - a setting that
        ///    silently read as ON would combine bags for a player who opted out. This is the OWNER's setting:
        ///    a grantee depositing into someone else's vault follows the owner's choice;
        /// 5. a registered market hook must have a TRUSTED listing index (<see cref="IsListedHookIndexTrusted"/>);
        ///    with no hook registered there is no market and the pass proceeds.
        ///
        /// SELECTION: class rows of a wcid in <paramref name="wcids"/> that is a salvage material
        /// (Player.MaterialSalvage), whose template resolves to MaxStructure 100 and is NOT a salvage tool,
        /// whose raw shape passes <see cref="SalvagePartialCombinePlanner.IsEligible"/> (which also refuses a
        /// renamed bag), and whose display line is not listed on the market. Grouped by wcid alone, acted on
        /// only when the group holds two or more BAGS.
        ///
        /// ONE EXCHANGE PER PASS, not one per group: the whole pass is then one all-or-nothing trade with one
        /// outcome to handle, and the size is bounded by <see cref="SalvageCombineMaxStepsPerPass"/>. Two groups can no longer share an output key: groups are per-wcid
        /// and VaultItemClass.ClassKey hashes the wcid, so cross-group netting is unreachable. Netting still
        /// matters WITHIN a group, where several identical full bags are one step with a CountDelta above 1.
        /// </summary>
        internal void ConsolidatePartialSalvageOnQueue(IReadOnlyCollection<uint> wcids, VaultActor actor, out SalvageCombineSummary summary)
        {
            summary = SalvageCombineSummary.None;

            var exchangeAttempted = false;

            try
            {
                AssertOnMutationQueue(nameof(ConsolidatePartialSalvageOnQueue));

                summary = ConsolidatePartialSalvageCore(wcids, actor, ref exchangeAttempted);
            }
            catch (Exception ex)
            {
                summary = SalvageCombineSummary.None;

                try
                {
                    log.Error($"[VAULT] account {AccountId}: the partial-salvage combine pass THREW and is reported as a no-op{(exchangeAttempted ? " AFTER its class exchange was attempted, so the class map is being re-read" : "; nothing was exchanged")}: {ex.GetFullMessage()}");

                    if (exchangeAttempted)
                        InvalidateClasses("a partial-salvage combine pass that threw after its exchange");
                }
                catch
                {
                    // Deliberately empty: this method's contract is that it never throws, including from
                    // its own failure handling.
                }
            }
        }

        private SalvageCombineSummary ConsolidatePartialSalvageCore(IReadOnlyCollection<uint> wcids, VaultActor actor, ref bool exchangeAttempted)
        {
            if (wcids == null || wcids.Count == 0)
                return SalvageCombineSummary.None;

            Touch();

            // ---- gates, cheapest first ----

            if (!PropertyManager.GetBool("account_vault_salvage_combine").Item)
                return SalvageCombineSummary.None;

            if (!ClassStorageEnabled)
                return SalvageCombineSummary.None;

            lock (stateLock)
            {
                if (depositWindow != null)
                    return SalvageCombineSummary.None;

                if (!CheckReadyLocked(out _))
                    return SalvageCombineSummary.None;
            }

            var enabled = TryGetSalvageCombineEnabled();

            if (enabled == null)
            {
                log.Warn($"[VAULT] account {AccountId}: skipping the partial-salvage combine because the account's setting could not be read.");
                return SalvageCombineSummary.None;
            }

            if (!enabled.Value)
                return SalvageCombineSummary.None;

            // A market is registered but its listing index did not load: every "not listed" it gives is a
            // guess, so NO group may be rewritten. A null hook (no market in this process) proceeds.
            if (!IsListingIndexTrustedForCombine())
            {
                var message = $"[VAULT] account {AccountId}: skipping the partial-salvage combine because the market's listing index is not trusted (it did not load at boot).";

                if (!salvageCombineUntrustedIndexWarned)
                {
                    salvageCombineUntrustedIndexWarned = true;
                    log.Warn(message);
                }
                else
                {
                    log.Debug(message);
                }

                return SalvageCombineSummary.None;
            }

            // ---- selection snapshot ----

            var materialWcids = new HashSet<uint>(Player.MaterialSalvage.Values.Where(v => v > 0).Select(v => (uint)v));

            var wanted = new HashSet<uint>(wcids.Where(materialWcids.Contains));

            if (wanted.Count == 0)
                return SalvageCombineSummary.None;

            var keyInfo = new Dictionary<string, CombineKeyInfo>(StringComparer.Ordinal);
            var candidates = new List<CombineRow>();
            int entryCount;

            // Every mutation of `classes` runs on this queue, and this pass IS the queue's current work item,
            // so the snapshot cannot go stale before the exchange below. Taken under the lock only because
            // readers on other threads hold it too.
            lock (stateLock)
            {
                foreach (var row in classes.Values.OrderBy(r => r.ClassKey, StringComparer.Ordinal))
                {
                    var displayKey = ClassDisplayKeyFor(row);

                    keyInfo[row.ClassKey] = new CombineKeyInfo
                    {
                        ClassKey = row.ClassKey,
                        Wcid = row.Wcid,
                        ExpectedCount = row.Count,
                        ExpectedTotal = row.TotalValue,
                        DisplayKey = displayKey,
                        Name = row.DisplayName,
                    };

                    if (!wanted.Contains(row.Wcid) || row.Overrides == null || row.Count <= 0)
                        continue;

                    candidates.Add(new CombineRow
                    {
                        ClassKey = row.ClassKey,
                        Wcid = row.Wcid,
                        Count = row.Count,
                        TotalValue = row.TotalValue,
                        Overrides = row.Overrides,
                        Structure = row.Overrides.GetInt(PropertyInt.Structure),
                        Iw = row.Overrides.GetInt(PropertyInt.ItemWorkmanship),
                        Nim = row.Overrides.GetInt(PropertyInt.NumItemsInMaterial),
                        Name = row.Overrides.GetString(PropertyString.Name),
                        DisplayKey = displayKey,
                    });
                }

                entryCount = EntryCountLocked();
            }

            if (candidates.Count == 0)
                return SalvageCombineSummary.None;

            var entryCap = EffectiveEntryCap;

            // Read ONCE per pass and clamped exactly as Player.AddSalvage clamps it (Player_Crafting.cs), so a
            // negative or oversized admin value cannot force a bag Value negative here either.
            long valueCap = (int)Math.Clamp(PropertyManager.GetLong("salvage_bag_value_cap").Item, 0L, int.MaxValue);

            // ---- per-wcid template, listing and eligibility ----

            var listedCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            var groups = new List<(uint Wcid, int MaxStructure, List<CombineRow> Rows)>();

            foreach (var byWcid in candidates.GroupBy(r => r.Wcid).OrderBy(g => g.Key))
            {
                var wcid = byWcid.Key;

                if (!TryReadSalvageTemplate(wcid, byWcid.First().Overrides, out var maxStructure))
                    continue;

                var eligible = new List<CombineRow>();

                foreach (var row in byWcid)
                {
                    if (row.Structure == null)
                        continue;

                    if (!SalvagePartialCombinePlanner.IsEligible(row.Structure.Value, row.Iw, row.Nim, row.Name, maxStructure))
                        continue;

                    if (!listedCache.TryGetValue(row.DisplayKey, out var listed))
                    {
                        listed = IsClassLineListedForCombine(wcid, row.DisplayKey);
                        listedCache[row.DisplayKey] = listed;
                    }

                    if (listed)
                        continue;

                    eligible.Add(row);
                }

                // One group per material: workmanship never splits a group. The planner's pour math carries the
                // item-weighted average workmanship into the merged bag.
                if (eligible.Count > 0)
                    groups.Add((wcid, maxStructure, eligible));
            }

            // ---- plan each group, accumulate accepted groups into one netted delta set ----

            var passDeltas = new Dictionary<string, (long Count, long Value)>(StringComparer.Ordinal);
            var provenOutputKeys = new HashSet<string>(StringComparer.Ordinal);

            int bagsIn = 0, fullOut = 0, partialOut = 0;
            long cappedOff = 0, roundingDelta = 0;
            var acceptedGroups = 0;

            foreach (var group in groups)
            {
                var bags = group.Rows.Sum(r => r.Count);

                // >= 2 partial BAGS, counted by items rather than by rows: one row of count 3 is three bags.
                if (bags < 2)
                    continue;

                if (bags > SalvageCombineMaxBagsPerGroup)
                {
                    WarnOncePerWcid(salvageCombineOversizeWarned, group.Wcid, $"[VAULT] account {AccountId}: skipping the partial-salvage combine of wcid {group.Wcid}: {bags} bags exceeds the per-group bound of {SalvageCombineMaxBagsPerGroup}.");
                    continue;
                }

                // Every member must agree on every whitelisted key the combine does NOT rewrite. Trivially
                // true for today's whitelist (all five keys are rewritten); it is what stops a future sixth key
                // being silently taken from whichever row happened to be first.
                var shapeFree = group.Rows.Select(r => ShapeFreeCanonicalForm(r.Wcid, r.Overrides)).Distinct(StringComparer.Ordinal).Count();

                if (shapeFree != 1)
                    continue;

                if (!TryExpandGroup(group.Rows, out var inputs))
                    continue;

                var plan = SalvagePartialCombinePlanner.Plan(inputs, group.MaxStructure, valueCap);

                if (plan.Aborted || plan.IsNoOp || plan.Outputs.Count == 0)
                    continue;

                var groupDeltas = new Dictionary<string, (long Count, long Value)>(StringComparer.Ordinal);

                foreach (var row in group.Rows)
                    AddDelta(groupDeltas, row.ClassKey, -row.Count, -row.TotalValue);

                var template = group.Rows[0].Overrides;
                var outputsHold = true;

                foreach (var output in plan.Outputs)
                {
                    var overrides = WithSalvageShape(template, output);

                    // THE SAME key derivation the deposit path uses (TryDepositBatch's classify block).
                    var classKey = VaultItemClass.ClassKey(group.Wcid, overrides, VaultItemClass.ValueExcluded);

                    if (!keyInfo.ContainsKey(classKey))
                    {
                        keyInfo[classKey] = new CombineKeyInfo
                        {
                            ClassKey = classKey,
                            Wcid = group.Wcid,
                            ExpectedCount = 0,
                            ExpectedTotal = 0,
                            DisplayKey = VaultCollapse.ClassDisplayGroupKey(group.Wcid, overrides),
                            Name = output.Name,
                        };
                    }

                    var info = keyInfo[classKey];

                    // A seed on every output key, so whichever step ends up expecting 0/0 can create its row.
                    info.Seed ??= new AccountVaultClassSeed
                    {
                        Wcid = group.Wcid,
                        ValueBandPct = VaultItemClass.ValueExcluded,
                        CanonicalForm = VaultItemClass.CanonicalForm(group.Wcid, overrides, VaultItemClass.ValueExcluded),
                    };

                    // A NEW key has never been proven reversible; an existing row was, by its own deposit.
                    if (info.ExpectedCount <= 0 && !provenOutputKeys.Contains(classKey))
                    {
                        if (!ClassRoundTripHolds(group.Wcid, overrides, classKey))
                        {
                            outputsHold = false;
                            break;
                        }

                        provenOutputKeys.Add(classKey);
                    }

                    AddDelta(groupDeltas, classKey, 1, output.Value);
                }

                if (!outputsHold)
                {
                    log.Warn($"[VAULT] account {AccountId}: skipping the partial-salvage combine of wcid {group.Wcid}: an output class does not round-trip. Nothing was changed.");
                    continue;
                }

                // Entry cap: refuse a group that would ADD panel lines and end over the cap. A combine that
                // keeps or reduces the line count is always allowed, even on an account already over its cap.
                var linesBefore = ClassLineCount(keyInfo, passDeltas, null);
                var linesAfter = ClassLineCount(keyInfo, passDeltas, groupDeltas);

                var entriesBefore = entryCount + (linesBefore - ClassLineCount(keyInfo, null, null));
                var entriesAfter = entryCount + (linesAfter - ClassLineCount(keyInfo, null, null));

                if (entriesAfter > entryCap && entriesAfter > entriesBefore)
                {
                    log.Info($"[VAULT] account {AccountId}: skipping the partial-salvage combine of wcid {group.Wcid}: it would take the vault to {entriesAfter} of {entryCap} entries.");
                    continue;
                }

                var merged = new Dictionary<string, (long Count, long Value)>(passDeltas, StringComparer.Ordinal);

                foreach (var kvp in groupDeltas)
                    AddDelta(merged, kvp.Key, kvp.Value.Count, kvp.Value.Value);

                if (acceptedGroups > 0 && NonZeroSteps(merged) > SalvageCombineMaxStepsPerPass)
                    continue;

                passDeltas = merged;
                acceptedGroups++;

                bagsIn += inputs.Count;
                fullOut += plan.Outputs.Count(o => o.IsFull);
                partialOut += plan.Outputs.Count(o => !o.IsFull);
                cappedOff += plan.ValueCappedOff;
                roundingDelta += plan.ValueRoundingDelta;
            }

            if (acceptedGroups == 0)
                return SalvageCombineSummary.None;

            var planned = new SalvageCombineSummary(bagsIn, fullOut, partialOut, cappedOff, roundingDelta, false);

            // ---- the exchange ----

            var steps = new List<AccountVaultClassExchangeStep>();

            foreach (var kvp in passDeltas.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kvp.Value.Count == 0 && kvp.Value.Value == 0)
                    continue;

                var info = keyInfo[kvp.Key];

                steps.Add(new AccountVaultClassExchangeStep(kvp.Key, info.ExpectedCount, info.ExpectedTotal, kvp.Value.Count, kvp.Value.Value, info.Seed));
            }

            if (steps.Count == 0)
                return SalvageCombineSummary.None;

            // Test seam: lets a test force a throw INSIDE the pass, after planning and before the exchange,
            // which no fake seam can reach (every world/backend call the pass makes is already guarded).
            BeforeSalvageCombineExchangeForTest?.Invoke();

            exchangeAttempted = true;

            var result = backend.TryExchangeAccountVaultClasses(AccountId, steps);

            if (result == AccountVaultStackAdjustResult.Applied || result == AccountVaultStackAdjustResult.AppliedCountUnknown)
            {
                foreach (var step in steps)
                {
                    var info = keyInfo[step.ClassKey];

                    WriteLog(AccountVaultAction.Combine, actor, info.Wcid, null, info.Name, step.CountDelta, step.ValueDelta, step.ClassKey);
                }

                backend.DeleteEmptyAccountVaultClasses(AccountId);

                lock (stateLock)
                {
                    // Pending FIRST, so a failed re-read leaves the store refusing rather than serving the
                    // pre-exchange map.
                    classReloadPending = true;

                    if (!TryReloadClassesLocked())
                        log.Error($"[VAULT] account {AccountId}: could not re-read the item-class ledger after an APPLIED partial-salvage combine. The store will refuse reads and mutations until a later read succeeds. THE COMBINE STANDS.");
                }

                Interlocked.Increment(ref version);

                log.Info($"[VAULT] account {AccountId}: combined {bagsIn} partial salvage bag(s) into {fullOut} full and {partialOut} partial across {acceptedGroups} group(s), {steps.Count} class row(s), for {actor.Name} (0x{actor.CharacterGuid:X8}).");

                if (cappedOff != 0 || roundingDelta != 0)
                    log.Info($"[VAULT] account {AccountId}: partial-salvage combine value accounting - {cappedOff} capped off at the per-bag cap of {valueCap}, {roundingDelta} lost to split rounding.");

                return planned.WithApplied(true);
            }

            if (result == AccountVaultStackAdjustResult.Failed)
            {
                var detail = string.Join("; ", steps.Select(s => $"class {s.ClassKey} (wcid {keyInfo[s.ClassKey].Wcid}) expected {s.ExpectedCount}/{s.ExpectedTotal} delta {s.CountDelta:+#;-#;0}/{s.ValueDelta:+#;-#;0}"));

                log.Error($"[VAULT] CLASS STATE UNKNOWN: account {AccountId}, partial-salvage combine of {bagsIn} bag(s) as ONE exchange of {steps.Count} step(s): {detail}. The exchange neither committed nor provably failed, and it is atomic, so EITHER every row is at its expected pre-state (nothing moved) OR every row is at expected + delta (the whole combine landed). To reconcile: read account_vault_class for this account and these keys and compare against the two states above. No account_vault_log Combine rows were written for this pass, so if the rows are at the post-state the combine happened without its audit rows and this line is the record.");

                InvalidateClasses("a partial-salvage combine exchange whose outcome is unknown");

                return planned;
            }

            log.Warn($"[VAULT] account {AccountId}: the class ledger REFUSED a partial-salvage combine of {bagsIn} bag(s) ({steps.Count} step(s)); the rows did not match the pre-state this store held. Nothing moved. Re-reading the class map; the combine will be tried again on the next partial deposit.");

            lock (stateLock)
            {
                classReloadPending = true;

                if (!TryReloadClassesLocked())
                    log.Error($"[VAULT] account {AccountId}: could not re-read the item-class ledger after a refused partial-salvage combine. The store will refuse reads and mutations until a later read succeeds.");
            }

            return planned;
        }

        /// <summary>
        /// Reads the TEMPLATE's MaxStructure and salvage-tool marker through the one materializer: a probe
        /// built from <paramref name="sample"/> replays only the whitelisted keys, so MaxStructure,
        /// SalvageToolCharges and ItemType on it are the weenie's own. False (skip this wcid) unless the
        /// template is TinkeringMaterial, is NOT a salvage tool, and resolves to MaxStructure
        /// <see cref="SalvageForge.DefaultMaxStructure"/>. The probe is destroyed either way.
        /// </summary>
        private bool TryReadSalvageTemplate(uint wcid, VaultItemClassOverrides sample, out int maxStructure)
        {
            maxStructure = 0;

            WorldObject probe = null;

            try
            {
                probe = world.MaterializeClass(wcid, sample, null);

                if (probe == null)
                    return false;

                if (probe.ItemType != ItemType.TinkeringMaterial)
                    return false;

                // A partly used hammer carries its charges on Structure and would otherwise read as a partial.
                if (SalvageTool.IsSalvageTool(probe))
                    return false;

                var max = probe.GetProperty(PropertyInt.MaxStructure);

                if (max != SalvageForge.DefaultMaxStructure)
                {
                    WarnOncePerWcid(salvageCombineMaxStructureWarned, wcid, $"[VAULT] account {AccountId}: not combining wcid {wcid}: its template's MaxStructure is {max?.ToString() ?? "absent"}, not {SalvageForge.DefaultMaxStructure}.");
                    return false;
                }

                maxStructure = max.Value;
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] account {AccountId}: reading the salvage template of wcid {wcid} for a combine threw; not combining it.", ex);
                return false;
            }
            finally
            {
                if (probe != null)
                {
                    try
                    {
                        world.DestroyItem(probe);
                    }
                    catch (Exception destroyEx)
                    {
                        log.Error($"[VAULT] account {AccountId}: could not destroy the salvage template probe for wcid {wcid}; a guid has been leaked.", destroyEx);
                    }
                }
            }
        }

        /// <summary>
        /// Whether a class display line is listed, for the COMBINE. Exactly three answers:
        /// - no <see cref="IsListedHook"/> (no market in this process): NOT listed, nothing to protect;
        /// - the hook answers: its answer. The pass has already refused to run at all unless
        ///   <see cref="IsListedHookIndexTrusted"/> vouched for the index (see <see cref="IsListingIndexTrustedForCombine"/>),
        ///   so a "not listed" here comes from an index that loaded;
        /// - the hook THROWS: LISTED, the line is left alone. This is the opposite of <see cref="IsListed"/>,
        ///   which reads a throw as "not listed": a combine is optional, so the cost here is one skipped group,
        ///   while rewriting the rows behind a live listing could strand it.
        /// </summary>
        /// <summary>
        /// False when a market is registered (<see cref="IsListedHook"/> set) but its listing index cannot be
        /// trusted: <see cref="IsListedHookIndexTrusted"/> is missing, answers false, or throws. True when no
        /// market hook is registered at all, or when the predicate vouches for the index. The pass skips every
        /// group on false, because an untrusted index answers "not listed" for lines that are listed.
        /// </summary>
        private bool IsListingIndexTrustedForCombine()
        {
            if (IsListedHook == null)
                return true;

            var trusted = IsListedHookIndexTrusted;

            if (trusted == null)
                return false;

            try
            {
                return trusted();
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] account {AccountId}: the market's listing-index trust check threw; treating the index as UNTRUSTED and skipping the partial-salvage combine: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// A combine skip that can repeat on every partial deposit of the same material: WARN the first time
        /// this store sees it for <paramref name="wcid"/>, DEBUG after that. Touched only from the mutation
        /// queue, so the set needs no lock of its own.
        /// </summary>
        private void WarnOncePerWcid(HashSet<uint> warned, uint wcid, string message)
        {
            if (warned.Add(wcid))
                log.Warn(message);
            else
                log.Debug(message);
        }

        /// <summary>Wcids this store has already WARNED about for the per-group bag bound. Mutation queue only.</summary>
        private readonly HashSet<uint> salvageCombineOversizeWarned = new HashSet<uint>();

        /// <summary>Wcids this store has already WARNED about for a template MaxStructure that is not 100. Mutation queue only.</summary>
        private readonly HashSet<uint> salvageCombineMaxStructureWarned = new HashSet<uint>();

        /// <summary>Whether this store has already WARNED that the market index is untrusted. Mutation queue only.</summary>
        private bool salvageCombineUntrustedIndexWarned;

        private bool IsClassLineListedForCombine(uint wcid, string displayKey)
        {
            var hook = IsListedHook;

            if (hook == null)
                return false;

            try
            {
                return hook(AccountId, null, wcid, VaultCollapse.ClassDisplayId(displayKey));
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] account {AccountId}: the market listing hook threw while checking a salvage line for the combine; treating it as LISTED and leaving it alone: {ex.GetFullMessage()}");
                return true;
            }
        }

        /// <summary>
        /// Expands rows into one input per bag, each carrying its PooledShare of the row's total - exactly the
        /// schedule a withdraw of the whole row would hand out (see TryPlanClassDrain), so the row's pool is
        /// conserved into the plan.
        ///
        /// ORDER, which Plan requires to be deterministic: Structure DESCENDING (the fullest bag is the first
        /// target, which minimises splits), then ItemWorkmanship, NumItemsInMaterial and Value ascending.
        /// False when a schedule does not sum to its row's total (it always should), so the group is skipped
        /// rather than planned on value that is not the row's.
        /// </summary>
        private bool TryExpandGroup(List<CombineRow> rows, out List<(int Structure, int Iw, int Nim, int Value)> inputs)
        {
            inputs = new List<(int Structure, int Iw, int Nim, int Value)>();

            foreach (var row in rows)
            {
                var remainingTotal = row.TotalValue;
                var remainingCount = row.Count;
                long sum = 0;

                for (long i = 0; i < row.Count; i++)
                {
                    var share = PooledShare(remainingTotal, remainingCount);

                    inputs.Add((row.Structure.Value, row.Iw.Value, row.Nim.Value, share));

                    sum += share;
                    remainingTotal -= share;
                    remainingCount--;
                }

                if (sum != row.TotalValue)
                {
                    log.Warn($"[VAULT] account {AccountId}: class {row.ClassKey} pays out {sum} against a total of {row.TotalValue}; not combining its group.");
                    return false;
                }
            }

            inputs = inputs
                .OrderByDescending(x => x.Structure)
                .ThenBy(x => x.Iw)
                .ThenBy(x => x.Nim)
                .ThenBy(x => x.Value)
                .ToList();

            return true;
        }

        /// <summary>
        /// <paramref name="template"/> with the four salvage-shape keys and Value rewritten to
        /// <paramref name="output"/>'s. Every other whitelisted key is carried over unchanged.
        /// </summary>
        private static VaultItemClassOverrides WithSalvageShape(VaultItemClassOverrides template, SalvagePartialCombinePlanner.PlanOutput output)
        {
            var ints = template.Ints.Select(kvp =>
            {
                switch (kvp.Key)
                {
                    case PropertyInt.Value: return new KeyValuePair<PropertyInt, int?>(kvp.Key, output.Value);
                    case PropertyInt.Structure: return new KeyValuePair<PropertyInt, int?>(kvp.Key, output.Structure);
                    case PropertyInt.ItemWorkmanship: return new KeyValuePair<PropertyInt, int?>(kvp.Key, output.Iw);
                    case PropertyInt.NumItemsInMaterial: return new KeyValuePair<PropertyInt, int?>(kvp.Key, output.Nim);
                    default: return kvp;
                }
            });

            var strings = template.Strings.Select(kvp =>
                kvp.Key == PropertyString.Name ? new KeyValuePair<PropertyString, string>(kvp.Key, output.Name) : kvp);

            return new VaultItemClassOverrides(ints, strings);
        }

        /// <summary>The canonical form with every key the combine rewrites blanked, for the group agreement check.</summary>
        private static string ShapeFreeCanonicalForm(uint wcid, VaultItemClassOverrides overrides)
        {
            var blank = new SalvagePartialCombinePlanner.PlanOutput(0, 0, 0, 0, string.Empty, false);

            return VaultItemClass.CanonicalForm(wcid, WithSalvageShape(overrides, blank), VaultItemClass.ValueExcluded);
        }

        private static void AddDelta(Dictionary<string, (long Count, long Value)> deltas, string classKey, long count, long value)
        {
            deltas.TryGetValue(classKey, out var current);
            deltas[classKey] = (current.Count + count, current.Value + value);
        }

        private static int NonZeroSteps(Dictionary<string, (long Count, long Value)> deltas)
            => deltas.Count(kvp => kvp.Value.Count != 0 || kvp.Value.Value != 0);

        /// <summary>
        /// Distinct class DISPLAY lines (the unit the entry cap counts, see EntryCountLocked) holding at least
        /// one item, after applying <paramref name="a"/> and <paramref name="b"/> (either may be null) to the
        /// snapshotted counts.
        /// </summary>
        private static int ClassLineCount(Dictionary<string, CombineKeyInfo> keyInfo,
                                          Dictionary<string, (long Count, long Value)> a,
                                          Dictionary<string, (long Count, long Value)> b)
        {
            var lines = new HashSet<string>(StringComparer.Ordinal);

            foreach (var info in keyInfo.Values)
            {
                var count = info.ExpectedCount;

                if (a != null && a.TryGetValue(info.ClassKey, out var da))
                    count += da.Count;

                if (b != null && b.TryGetValue(info.ClassKey, out var db))
                    count += db.Count;

                if (count > 0)
                    lines.Add(info.DisplayKey);
            }

            return lines.Count;
        }
    }
}
