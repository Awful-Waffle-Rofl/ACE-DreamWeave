using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>The character a /pvptemplate kit names, as the host resolved it (null from the host: no such character).</summary>
    public sealed class PvpKitCharacter
    {
        public uint Guid { get; set; }
        public string Name { get; set; }
        public string AccountName { get; set; }
        public bool Deleted { get; set; }
        public bool HasRestoreRecord { get; set; }
        public int MainPackCapacity { get; set; } = PvpTemplateKitEdit.DefaultMainPackCapacity;
    }

    /// <summary>A created, not yet saved, object. <see cref="Handle"/> is the host's own (the live host: the WorldObject).</summary>
    public sealed class PvpKitNewItem
    {
        public uint Guid { get; set; }
        public PvpKitCreation Creation { get; set; }
        public object Handle { get; set; }
    }

    /// <summary>
    /// Everything PvpTemplateKitEditService touches outside itself, so the whole flow runs in tests against a fake. Callbacks may
    /// arrive on any thread; the service hops to <see cref="OnWorld"/> before it creates objects.
    /// </summary>
    public interface IPvpTemplateKitHost
    {
        /// <summary>pvp_template_account.</summary>
        string TemplateAccount { get; }

        /// <summary>The character by name, online or offline; null when there is none.</summary>
        PvpKitCharacter FindCharacter(string name);

        bool IsOnline(uint characterGuid);

        /// <summary>Claims the character against a login (SuitHolderMoveGuard.TryClaimForMove). False: entering the world, or already claimed.</summary>
        bool TryClaim(uint characterGuid);

        /// <summary>Releases the claim (SuitHolderMoveGuard.ReleaseMove).</summary>
        void Release(uint characterGuid);

        PvpKitWeenieInfo GetWeenie(uint wcid);

        /// <summary>The character's possessions through the shard queue, null when the read failed.</summary>
        void ReadPossessions(uint characterGuid, Action<List<PvpKitPossession>> callback);

        /// <summary>Builds one object (world thread). Null when it could not be built.</summary>
        PvpKitNewItem Create(PvpKitCreation creation);

        void Save(PvpKitNewItem item, Action<bool> callback);

        void Remove(IReadOnlyCollection<uint> guids, Action<bool> callback);

        void OnWorld(Action action);

        void LogInfo(string message);

        void LogError(string message, Exception ex = null);
    }

    /// <summary>
    /// /pvptemplate kit, the live half (Docs/Pvp/TEMPLATES.md "Commands"): reworks the items of an OFFLINE character on
    /// pvp_template_account without logging it in.
    ///
    /// Order: claim the character against a login (BEFORE the offline check, as the suit builder's holder moves do, so the offline
    /// answer cannot go stale) -> refuse on account, online, deleted, template restore record -> read the possessions through the
    /// shard queue -> plan (every rule first; a refused plan writes nothing; preview stops here) -> create every new object ->
    /// save every new object -> only when EVERY save succeeded, remove the old ones in one call -> release the claim.
    ///
    /// Failure: a create or save failure removes every new object again (rollback) and leaves the old ones untouched. A removal
    /// failure re-reads the possessions: when every old object is still there the new ones are rolled back, so the character is
    /// exactly as it was; when the removal landed part way, the new objects are KEPT (rolling back then would lose items for good), the
    /// leftovers are retried once (removal is idempotent), and whatever is still left is named by guid beside the new items. The plan's
    /// writes are logged at Info before the first one, so a shutdown between the saves and the removal is recoverable from the log. Created objects are dropped, never Destroy()ed: Destroy recycles the dynamic guid of an item
    /// whose row now persists. The claim is released on every path.
    /// </summary>
    public sealed class PvpTemplateKitEditService
    {
        private readonly IPvpTemplateKitHost host;

        public PvpTemplateKitEditService(IPvpTemplateKitHost host)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>The reminder every successful kit edit ends with.</summary>
        public static string SnapshotReminder(string characterName)
            => $"The template is unchanged until you re-snapshot: /pvptemplate snapshot <key> {characterName} | <display name>";

        /// <summary>
        /// Starts a kit edit. <paramref name="reply"/> gets every admin-facing line (the caller marshals to the world thread).
        /// <paramref name="done"/>, when given, runs once at the very end of every path, after the claim is released.
        /// </summary>
        public void Run(bool preview, string characterName, IReadOnlyList<PvpKitOp> ops, string adminName, Action<string> reply, Action done = null)
        {
            reply ??= _ => { };

            var finished = 0;

            void Finish()
            {
                if (Interlocked.Exchange(ref finished, 1) == 0)
                    done?.Invoke();
            }

            var account = host.TemplateAccount;

            if (string.IsNullOrWhiteSpace(account))
            {
                reply("pvp_template_account is empty, so no character can be edited. Set it to the template account's name first.");
                Finish();
                return;
            }

            var character = host.FindCharacter(characterName);

            if (character == null)
            {
                reply($"No character named {characterName} was found.");
                Finish();
                return;
            }

            if (!host.TryClaim(character.Guid))
            {
                reply($"{character.Name} is logging in or is already being edited. Try again once it is offline and idle.");
                Finish();
                return;
            }

            var claimHeld = 1;

            void ReleaseOnce()
            {
                if (Interlocked.Exchange(ref claimHeld, 0) == 1)
                    host.Release(character.Guid);
            }

            void End(string line)
            {
                // The claim is released and done runs even when the reply itself throws.
                try
                {
                    if (line != null)
                        reply(line);
                }
                finally
                {
                    ReleaseOnce();
                    Finish();
                }
            }

            // Set immediately before the first save or removal, so a failure message only says "nothing was written" when that is true.
            var wrote = 0;

            void MarkWrote() => Interlocked.Exchange(ref wrote, 1);

            try
            {
                var refusal = StateRefusal(character, account, host.IsOnline(character.Guid));

                if (refusal != null)
                {
                    End(refusal);
                    return;
                }

                host.ReadPossessions(character.Guid, possessions => host.OnWorld(() =>
                {
                    try
                    {
                        OnRead(preview, character, ops, adminName, possessions, reply, End, MarkWrote);
                    }
                    catch (Exception ex)
                    {
                        if (Volatile.Read(ref wrote) == 0)
                        {
                            host.LogError($"[PVPTEMPLATE] kit edit of {character.Name} (0x{character.Guid:X8}) threw before any write.", ex);
                            End("The kit edit failed before anything was written. See the server log.");
                        }
                        else
                        {
                            host.LogError($"[PVPTEMPLATE] kit edit of {character.Name} (0x{character.Guid:X8}) threw after its writes began; the planned writes were logged at Info before the first one.", ex);
                            End($"The kit edit failed after its writes began, so {character.Name} may be part way edited. The planned writes are in the server log; check the character before retrying.");
                        }
                    }
                }));
            }
            catch (Exception ex)
            {
                host.LogError($"[PVPTEMPLATE] kit edit of {character.Name} (0x{character.Guid:X8}) threw before the read.", ex);
                End("The kit edit failed before anything was written. See the server log.");
            }
        }

        /// <summary>Null when the character may be edited, else the refusal. Pure.</summary>
        public static string StateRefusal(PvpKitCharacter character, string account, bool online)
        {
            if (character.AccountName == null || !string.Equals(character.AccountName, account, StringComparison.OrdinalIgnoreCase))
                return $"{character.Name} is not on the template account ({account}), so it cannot be edited.";

            if (online)
                return $"{character.Name} is online. Log the character out before editing its kit.";

            if (character.Deleted)
                return $"{character.Name} is deleted or pending deletion.";

            if (character.HasRestoreRecord)
                return $"{character.Name} carries a template restore record (it is wearing a template). Log it in once so the restore runs, then log it out and try again.";

            return null;
        }

        private void OnRead(bool preview, PvpKitCharacter character, IReadOnlyList<PvpKitOp> ops, string adminName, List<PvpKitPossession> possessions,
            Action<string> reply, Action<string> end, Action markWrote)
        {
            if (possessions == null)
            {
                end($"Reading {character.Name}'s possessions failed. Nothing was written.");
                return;
            }

            if (host.IsOnline(character.Guid))
            {
                end($"{character.Name} logged in during the read. Nothing was written.");
                return;
            }

            var plan = PvpTemplateKitEdit.Plan(ops, character.Guid, character.MainPackCapacity, possessions, host.GetWeenie);

            if (!plan.Ok)
            {
                foreach (var problem in plan.Problems)
                    reply("  " + problem);

                end($"Refused: {plan.Problems.Count} problem(s) above. Nothing was written to {character.Name}.");
                return;
            }

            foreach (var line in plan.Lines)
                reply("  " + line);

            if (preview)
            {
                end($"Preview of {character.Name}: {plan.Creations.Count} to create, {plan.Removals.Count} to remove. Preview only: nothing was written.");
                return;
            }

            // Create every object before any save, so a build failure costs nothing.
            var created = new List<PvpKitNewItem>();

            foreach (var creation in plan.Creations)
            {
                PvpKitNewItem item = null;

                try
                {
                    item = host.Create(creation);
                }
                catch (Exception ex)
                {
                    host.LogError($"[PVPTEMPLATE] kit edit of {character.Name}: building wcid {creation.Wcid} threw.", ex);
                }

                if (item == null)
                {
                    // Nothing is saved yet: the built objects are dropped (not Destroy()ed) and nothing was written.
                    end($"Building wcid {creation.Wcid} failed. Nothing was written to {character.Name}.");
                    return;
                }

                created.Add(item);
            }

            // The wcid of every possession read, so a leftover after a part-way removal can be named with its wcid.
            var wcidOf = new Dictionary<uint, uint>();

            foreach (var p in possessions)
                wcidOf[p.Guid] = p.Wcid;

            SaveAll(character, plan, created, wcidOf, adminName, reply, end, markWrote);
        }

        private void SaveAll(PvpKitCharacter character, PvpKitPlan plan, List<PvpKitNewItem> created, IReadOnlyDictionary<uint, uint> wcidOf, string adminName,
            Action<string> reply, Action<string> end, Action markWrote)
        {
            // Logged BEFORE the first write, so a shutdown between the saves and the removal can be recovered from the log alone.
            var newItems = created.Count == 0 ? "none" : string.Join(", ", created.Select(c => $"0x{c.Guid:X8} wcid {c.Creation.Wcid}{(c.Creation.ReplacesGuid is uint r ? $" replacing 0x{r:X8}" : "")}"));
            host.LogInfo($"[PVPTEMPLATE] {adminName} kit edit of {character.Name} (0x{character.Guid:X8}) writing: new items {newItems}; old items to remove after the saves: {(plan.Removals.Count == 0 ? "none" : Guids(plan.Removals))}.");
            markWrote();

            if (created.Count == 0)
            {
                RemoveOld(character, plan, created, wcidOf, adminName, reply, end);
                return;
            }

            var pending = created.Count;
            var failed = 0;

            void Answer(PvpKitNewItem item, bool ok)
            {
                if (!ok)
                {
                    Interlocked.Increment(ref failed);
                    host.LogError($"[PVPTEMPLATE] kit edit of {character.Name}: the save of new 0x{item.Guid:X8} (wcid {item.Creation.Wcid}) failed.");
                }

                if (Interlocked.Decrement(ref pending) != 0)
                    return;

                if (Volatile.Read(ref failed) > 0)
                {
                    RollBack(character, created, $"{failed} of {created.Count} new item save(s) failed", reply, end);
                    return;
                }

                RemoveOld(character, plan, created, wcidOf, adminName, reply, end);
            }

            foreach (var item in created)
                Queue($"kit edit of {character.Name}: the save of 0x{item.Guid:X8}", cb => host.Save(item, cb), ok => Answer(item, ok), () => Answer(item, false));
        }

        /// <summary>
        /// Starts one queued write. A throw BEFORE the write answered runs <paramref name="onThrow"/>; a throw that comes after the answer
        /// (from code downstream of <paramref name="callback"/> on a host that answers synchronously) is only logged, because the write
        /// it would "fail" already happened: treating it as a queueing failure could roll back items that were committed.
        /// </summary>
        private void Queue(string what, Action<Action<bool>> start, Action<bool> callback, Action onThrow)
        {
            var answered = 0;

            try
            {
                start(ok =>
                {
                    Interlocked.Exchange(ref answered, 1);
                    callback(ok);
                });
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref answered, 1) == 0)
                {
                    host.LogError($"[PVPTEMPLATE] {what}: queueing it threw.", ex);
                    onThrow();
                }
                else
                {
                    host.LogError($"[PVPTEMPLATE] {what}: code after its answer threw.", ex);
                }
            }
        }

        private void RemoveOld(PvpKitCharacter character, PvpKitPlan plan, List<PvpKitNewItem> created, IReadOnlyDictionary<uint, uint> wcidOf, string adminName,
            Action<string> reply, Action<string> end)
        {
            if (plan.Removals.Count == 0)
            {
                Succeed(character, plan, created, adminName, reply, end);
                return;
            }

            // Removal is idempotent, so the leftovers of a part-way removal are retried once before anything is reported.
            void RetryLeftovers(List<uint> left)
            {
                void Retried(bool ok)
                {
                    if (ok)
                    {
                        host.LogInfo($"[PVPTEMPLATE] kit edit of {character.Name} (0x{character.Guid:X8}): the retry removed the leftovers {Guids(left)}.");
                        Succeed(character, plan, created, adminName, reply, end);
                        return;
                    }

                    ReRead(character.Guid, again =>
                    {
                        // A failed re-read leaves the retried guids as "may remain": the report is the same, only less certain.
                        var still = again == null ? left : left.Where(new HashSet<uint>(again.Select(p => p.Guid)).Contains).ToList();

                        if (still.Count == 0)
                        {
                            Succeed(character, plan, created, adminName, reply, end);
                            return;
                        }

                        ReportLeftovers(character, plan, created, still, again == null, wcidOf, end);
                    });
                }

                Queue($"kit edit of {character.Name}: the retry of the leftover removal", cb => host.Remove(left, cb), Retried, () => Retried(false));
            }

            void Removed(bool ok)
            {
                if (ok)
                {
                    Succeed(character, plan, created, adminName, reply, end);
                    return;
                }

                host.LogError($"[PVPTEMPLATE] kit edit of {character.Name}: removing {plan.Removals.Count} old item(s) failed; re-reading the possessions to see what is left.");

                ReRead(character.Guid, after =>
                {
                    if (after == null)
                    {
                        host.LogError($"[PVPTEMPLATE] kit edit of {character.Name} (0x{character.Guid:X8}): the removal failed AND the re-read failed; new items kept: {Guids(created.Select(c => c.Guid))}; old items meant to go: {Guids(plan.Removals)}.");
                        end($"Removing the old items failed and the character could not be re-read. The {created.Count} new item(s) were KEPT; some old items may remain beside them. Run /pvptemplate kit preview {character.Name} | ... to inspect, and see the server log.");
                        return;
                    }

                    var present = new HashSet<uint>(after.Select(p => p.Guid));
                    var left = plan.Removals.Where(present.Contains).ToList();

                    if (left.Count == 0)
                    {
                        // The removal answered false but every old item is gone.
                        Succeed(character, plan, created, adminName, reply, end);
                        return;
                    }

                    if (left.Count == plan.Removals.Count)
                    {
                        RollBack(character, created, "removing the old items failed (none were removed)", reply, end);
                        return;
                    }

                    host.LogError($"[PVPTEMPLATE] kit edit of {character.Name} (0x{character.Guid:X8}): the removal landed part way; new items kept; retrying the leftovers once: {Guids(left)}.");
                    RetryLeftovers(left);
                });
            }

            Queue($"kit edit of {character.Name}: the removal of the old items", cb => host.Remove(plan.Removals.ToList(), cb), Removed,
                () => RollBack(character, created, "queueing the removal of the old items threw", reply, end));
        }

        /// <summary>
        /// The leftovers of a part-way removal that the retry did not clear. Both sets are named by guid with their wcid. "-W" is advised
        /// only for a leftover wcid that no new item carries: for a ~W op (or any op whose target is a leftover's wcid) "-W" would delete
        /// the new copy as well.
        /// </summary>
        private void ReportLeftovers(PvpKitCharacter character, PvpKitPlan plan, List<PvpKitNewItem> created, List<uint> still, bool uncertain,
            IReadOnlyDictionary<uint, uint> wcidOf, Action<string> end)
        {
            string WcidText(uint guid) => wcidOf.TryGetValue(guid, out var w) ? w.ToString(CultureInfo.InvariantCulture) : "unknown";

            var leftText = string.Join(", ", still.Select(g => $"0x{g:X8} (wcid {WcidText(g)})"));
            var newText = string.Join(", ", created.Select(c => $"0x{c.Guid:X8} (wcid {c.Creation.Wcid})"));

            var createdWcids = new HashSet<uint>(created.Select(c => c.Creation.Wcid));
            var leftWcids = still.Where(wcidOf.ContainsKey).Select(g => wcidOf[g]).Distinct().OrderBy(w => w).ToList();
            var safe = leftWcids.Where(w => !createdWcids.Contains(w)).ToList();
            var shared = leftWcids.Where(createdWcids.Contains).ToList();

            var advice = new List<string>();

            if (safe.Count > 0)
                advice.Add($"Once checked, delete the leftovers whose wcid no new item carries with: {string.Join(" ", safe.Select(w => $"-{w}"))}.");

            if (shared.Count > 0 || still.Any(g => !wcidOf.ContainsKey(g)))
                advice.Add($"Do NOT use the - op for {(shared.Count > 0 ? "wcid(s) " + string.Join(", ", shared) : "the leftovers of unknown wcid")}: it deletes every possession of a wcid, the new copies included; remove those leftovers by hand (no kit op targets a single guid).");

            var state = uncertain ? "may still be present (the re-read failed)" : "are still present";

            host.LogError($"[PVPTEMPLATE] kit edit of {character.Name} (0x{character.Guid:X8}): the removal landed part way and the retry did not finish it; old items that {state}: {leftText}; new items kept: {newText}.");
            end($"Removing the old items landed part way, and one retry did not finish it: {plan.Removals.Count - still.Count} of {plan.Removals.Count} removed. Old items that {state}: {leftText}. The {created.Count} new item(s) were KEPT so nothing is lost: {newText}. {string.Join(" ", advice)}");
        }

        /// <summary>A re-read whose throw is answered as a failed read (null), so the claim is still released on that path.</summary>
        private void ReRead(uint characterGuid, Action<List<PvpKitPossession>> callback)
        {
            var answered = 0;

            try
            {
                host.ReadPossessions(characterGuid, rows =>
                {
                    Interlocked.Exchange(ref answered, 1);
                    callback(rows);
                });
            }
            catch (Exception ex)
            {
                host.LogError($"[PVPTEMPLATE] kit edit: queueing the re-read of 0x{characterGuid:X8} threw.", ex);

                if (Interlocked.Exchange(ref answered, 1) == 0)
                    callback(null);
            }
        }

        private void RollBack(PvpKitCharacter character, List<PvpKitNewItem> created, string why, Action<string> reply, Action<string> end)
        {
            var guids = created.Select(c => c.Guid).ToList();

            void RolledBack(bool ok)
            {
                if (ok)
                {
                    host.LogError($"[PVPTEMPLATE] kit edit of {character.Name} (0x{character.Guid:X8}) rolled back: {why}.");
                    end($"The kit edit failed ({why}) and was rolled back: the new items were removed and the old ones were never touched. {character.Name} is as it was.");
                    return;
                }

                host.LogError($"[PVPTEMPLATE] kit edit of {character.Name} (0x{character.Guid:X8}): {why}, AND the rollback failed; these new items may remain: {Guids(guids)}.");
                end($"The kit edit failed ({why}) and the rollback ALSO failed. The old items were never touched; these new items may remain beside them: {Guids(guids)}. See the server log.");
            }

            Queue($"kit edit of {character.Name}: the rollback", cb => host.Remove(guids, cb), RolledBack, () => RolledBack(false));
        }

        private void Succeed(PvpKitCharacter character, PvpKitPlan plan, List<PvpKitNewItem> created, string adminName, Action<string> reply, Action<string> end)
        {
            try
            {
                foreach (var line in plan.Lines)
                    host.LogInfo($"[PVPTEMPLATE] {adminName} kit edit of {character.Name} (0x{character.Guid:X8}): {line}");

                reply($"Kit edit of {character.Name} done: {created.Count} created, {plan.Removals.Count} removed.");
            }
            finally
            {
                end(SnapshotReminder(character.Name));
            }
        }

        private static string Guids(IEnumerable<uint> guids) => string.Join(", ", guids.Select(g => $"0x{g:X8}"));
    }
}
