using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common.Extensions;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers.Market;
using ACE.Server.Managers.Market.Suit;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// HOLDER LINES: "move this item out of my other character's pack into the one I am playing". The owner's ruling
    /// (all characters on one account belong to the same player) permits it for unattuned, unbonded items only.
    ///
    /// THE MOVE IS A RE-PARENT, NOT A COPY. The item keeps its guid and stays one database row: the holder's
    /// possessions are read fresh from the shard queue (the read login uses), the one row is converted to a runtime
    /// biota on the shard worker, a WorldObject is built from it on the target's action queue, added to the target's
    /// pack with TryCreateInInventoryWithNetworking (which queues its own save), and saved once more with a callback
    /// the unit waits for. Nothing is written to the holder: its burden and placement are rebuilt from its
    /// possessions at its next login (Container's EncumbranceVal is ephemeral, Container.SetEphemeralValues).
    ///
    /// THE HOLDER MUST BE OFFLINE, and stays offline for the whole unit: SuitHolderMoveGuard is claimed BEFORE the
    /// offline check and before the read is queued, and released only once the save has called back, so the holder
    /// cannot enter the world (and queue its own possession read) while the item is in flight. See the guard's
    /// remarks for why the one-session-per-account rule does not cover this on its own.
    ///
    /// FAILURE SEMANTICS. Any refusal before the add is Refused and nothing was written. An add that fails discards
    /// the built object (dropped, NEVER Destroy()ed - Destroy would delete the holder's row) and the row still names
    /// the holder. A read that never calls back is Refused at <see cref="HolderReadTimeoutSeconds"/>, and an abandon
    /// token turns its late callback into a no-op. A save that answers false, throws, or never answers is Threw: the
    /// job stops, and the holder is PINNED out of the world (SuitHolderMoveGuard.PinUnconfirmed) because the item is
    /// live on the target while the row may still name the holder. Never a retry.
    /// </summary>
    internal sealed partial class SuitTransferService
    {
        /// <summary>No more distinct holder characters per transfer: one fewer than ACE's 11 character slots. The roster check bounds it too.</summary>
        public const int MaxHolders = 10;

        /// <summary>A holder read that has not called back within this is abandoned and its line refused.</summary>
        internal const double HolderReadTimeoutSeconds = 15.0;

        internal const string CharacterMovesOffMessage = "Moving items between your characters is turned off right now, so nothing was moved from that character.";
        internal const string HolderSaveStopMessage = "A save error stopped the rest of the transfer.";

        // ---- the world-heartbeat sweep ----

        /// <summary>The process's one service (Program.cs sets it next to MarketApiOptions.SuitTransfers), for the world heartbeat's sweep. Null until wired.</summary>
        internal static SuitTransferService Live;

        /// <summary>How often the heartbeat runs <see cref="Sweep"/>: well inside the 15 s read timeout and the 60 s stall window.</summary>
        internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

        private DateTime nextSweepAt = DateTime.MinValue;

        /// <summary>
        /// Runs the watchdog (<see cref="ExpireLocked"/>) with no web request needed. Without it the watchdog ran only from
        /// Submit and GetStatus, so a transfer whose character logged out mid-unit (its action queue stopped ticking, so its
        /// read timeout and delivery never ran) held its holder claim until the browser happened to poll again. The sweep
        /// abandons such a unit after StallTimeout: a read is refused and its claim released, a save is Threw and its
        /// holder pinned.
        /// </summary>
        public void Sweep()
        {
            lock (gate)
                ExpireLocked(world.UtcNow);
        }

        /// <summary>
        /// WorldManager.UpdateGameWorld's call, every heartbeat, beside AccountVaultManager.Tick: self-rate-limited to one
        /// <see cref="Sweep"/> per <see cref="SweepInterval"/>, so it costs one clock read otherwise.
        /// </summary>
        internal static void SweepLive()
        {
            var service = Live;

            if (service == null)
                return;

            var now = service.world.UtcNow;

            if (now < service.nextSweepAt)
                return;

            service.nextSweepAt = now + SweepInterval;
            service.Sweep();
        }

        internal static string HolderPhrase(string name) => string.IsNullOrWhiteSpace(name) ? "that character" : name;

        private static string HolderPackPhrase(LineState line) => $"{HolderPhrase(line.HolderName)}'s pack";

        internal static string LogOutFirstMessage(string name) => $"Log out of {HolderPhrase(name)} first.";

        private enum HolderPhase
        {
            Reading,
            Delivering,
            Saving,
        }

        /// <summary>One holder unit in flight. Its identity is the abandon token: a callback for a replaced or cleared one is a no-op.</summary>
        private sealed class HolderPending
        {
            public int LineIndex;
            public uint HolderGuid;
            public HolderPhase Phase;
            public SuitHolderRead Read;
            public WorldObject Item;
            public string Label;
        }

        // ---- request thread ----

        /// <summary>
        /// Submit's holder checks, after the shape: the kill switch, then every holder on the CALLER's roster (fail
        /// closed). Answers the roster name of each holder for the line wording.
        /// </summary>
        private MarketError ResolveHolders(SuitTransferRequest request, out Dictionary<uint, string> names)
        {
            names = new Dictionary<uint, string>();

            var holders = request.Items.Where(l => l.HolderGuid != null).Select(l => l.HolderGuid.Value).Distinct().ToList();

            if (holders.Count == 0)
                return MarketError.None;

            try
            {
                if (!world.CharacterMovesEnabled)
                    return MarketError.InvalidTransfer;

                foreach (var holder in holders)
                {
                    if (!world.TryResolveRosterCharacter(request.AccountId, holder, out var name))
                        return MarketError.NotOwner;

                    names[holder] = name;
                }
            }
            catch (Exception ex)
            {
                log.Warn($"[SUIT] the holder roster check threw for account {request.AccountId}: {ex.GetFullMessage()}");
                return MarketError.ServerError;
            }

            return MarketError.None;
        }

        // ---- character action queue: start a unit ----

        /// <summary>
        /// The first half of one holder unit, on the target's action queue: the target's gates, the claim, the holder's
        /// state, the pack estimate, then the read. Returns Pending once the read is queued (the read's callback and its
        /// timeout carry the job on), Continue when the line was refused, Closed when the job was.
        /// </summary>
        private StepNext StepHolder(Job job, int lineIndex)
        {
            var line = job.Lines[lineIndex];
            var holder = line.Request.HolderGuid.Value;

            if (!world.IsStillEligible(job.Character))
            {
                lock (gate)
                    CompleteLocked(job, MarketError.CharacterOffline, "The character went offline, so the rest was not moved.");

                return StepNext.Closed;
            }

            var gateError = world.CheckGates(job.Character, out var gateMessage, job.HolderOnly);

            if (gateError != MarketError.None)
            {
                TellSafe(job.Character, gateMessage + " The rest of your transfer was stopped.");

                lock (gate)
                    CompleteLocked(job, gateError, gateMessage);

                return StepNext.Closed;
            }

            if (!world.CharacterMovesEnabled)
            {
                RefuseLineUnlocked(line, CharacterMovesOffMessage);
                return StepNext.Continue;
            }

            // CLAIM FIRST: from here until the claim is released, the holder cannot begin entering the world, so the
            // offline answer below cannot go stale before the read is queued behind any logout save it depends on.
            if (!world.TryClaimHolder(holder))
            {
                RefuseLineUnlocked(line, LogOutFirstMessage(line.HolderName));
                return StepNext.Continue;
            }

            var claimHeld = true;

            try
            {
                var refusal = HolderStateRefusal(job, line);

                if (refusal != null)
                {
                    RefuseLineUnlocked(line, refusal);
                    return StepNext.Continue;
                }

                // The plan's fit is a snapshot: a unit that no longer fits stops the transfer before anything is read.
                if (world.FreeMainPackSlots(job.Character) < 1 || Math.Max(0, world.TemplateBurden(line.Request.Wcid)) > world.AvailableBurden(job.Character))
                {
                    StopPackFull(job, world.FreeMainPackSlots(job.Character) < 1);
                    return StepNext.Closed;
                }

                var pending = new HolderPending { LineIndex = lineIndex, HolderGuid = holder, Phase = HolderPhase.Reading };

                lock (gate)
                {
                    job.Pending = pending;
                    job.LastProgressAt = world.UtcNow;
                }

                try
                {
                    world.ReadHolderItem(holder, line.Request.ItemGuid.Value, read => OnHolderRead(job, pending, read));
                }
                catch (Exception ex)
                {
                    log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}: queueing the read of holder 0x{holder:X8} threw; nothing was moved. {ex.GetFullMessage()}");

                    lock (gate)
                    {
                        if (job.Pending == pending)
                            job.Pending = null;

                        FinalizeLineLocked(line, SuitTransferOutcome.Refused, $"{Capitalize(HolderPackPhrase(line))} could not be read, so nothing was moved from it.");
                    }

                    return StepNext.Continue;
                }

                // The claim now belongs to the unit in flight: its completion (or its abandonment) releases it.
                claimHeld = false;

                if (!TrySchedule(job, HolderReadTimeoutSeconds, () => OnHolderReadTimeout(job, pending)))
                {
                    // Without a timeout the unit could wait forever; abandon it now (the read's late callback is a no-op).
                    // If the read already came back on the worker, the unit is past reading and its delivery carries on.
                    if (!AbandonRead(job, pending, $"{Capitalize(HolderPackPhrase(line))} could not be read, so nothing was moved from it."))
                        return StepNext.Pending;

                    lock (gate)
                        CompleteLocked(job, MarketError.ServerError, "The transfer could not continue.");

                    return StepNext.Closed;
                }

                return StepNext.Pending;
            }
            finally
            {
                if (claimHeld)
                    world.ReleaseHolder(holder);
            }
        }

        // ---- shard worker: the read came back ----

        private void OnHolderRead(Job job, HolderPending pending, SuitHolderRead read)
        {
            lock (gate)
            {
                // Abandoned (timed out, or the job closed): a late read moves nothing.
                if (job.Pending != pending || pending.Phase != HolderPhase.Reading)
                    return;

                pending.Phase = HolderPhase.Delivering;
                pending.Read = read;
                job.LastProgressAt = world.UtcNow;
            }

            if (!TrySchedule(job, 0.0, () => DeliverHolder(job, pending)))
            {
                bool mine;

                lock (gate)
                {
                    mine = job.Pending == pending;

                    if (mine)
                    {
                        job.Pending = null;
                        FinalizeLineLocked(job.Lines[pending.LineIndex], SuitTransferOutcome.Refused, "The transfer could not continue. Nothing was moved for this.");
                        CompleteLocked(job, MarketError.ServerError, "The transfer could not continue.");
                    }
                }

                if (mine)
                    world.ReleaseHolder(pending.HolderGuid);
            }
        }

        // ---- character action queue: the read timed out ----

        private void OnHolderReadTimeout(Job job, HolderPending pending)
        {
            var line = job.Lines[pending.LineIndex];

            if (!AbandonRead(job, pending, $"{Capitalize(HolderPackPhrase(line))} could not be read in time, so nothing was moved from it."))
                return;

            log.Warn($"[SUIT] transfer {job.Id} account {job.AccountId}: the read of holder 0x{pending.HolderGuid:X8} did not call back within {HolderReadTimeoutSeconds:N0} s; the line is refused and a late answer will be ignored.");

            ScheduleNextOrFinish(job);
        }

        /// <summary>True when this call abandoned a still-Reading unit (line refused, claim released); false when it was already past reading or gone.</summary>
        private bool AbandonRead(Job job, HolderPending pending, string message)
        {
            lock (gate)
            {
                if (job.Pending != pending || pending.Phase != HolderPhase.Reading)
                    return false;

                job.Pending = null;
                FinalizeLineLocked(job.Lines[pending.LineIndex], SuitTransferOutcome.Refused, message);
            }

            world.ReleaseHolder(pending.HolderGuid);
            return true;
        }

        // ---- character action queue: deliver ----

        private void DeliverHolder(Job job, HolderPending pending)
        {
            lock (gate)
            {
                if (job.State != SuitTransferState.Running || job.Pending != pending || pending.Phase != HolderPhase.Delivering)
                    return;

                job.InStep = true;
                job.LastProgressAt = world.UtcNow;
            }

            var next = StepNext.Closed;

            try
            {
                next = DeliverHolderCore(job, pending);
            }
            catch (Exception ex)
            {
                // DeliverHolderCore guards the add and everything after it itself, so a throw that reaches here came from
                // BEFORE the add: nothing was written.
                log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}: delivering from holder 0x{pending.HolderGuid:X8} threw before the add; nothing was moved. Stopping the transfer. {ex.GetFullMessage()}");

                bool mine;

                lock (gate)
                {
                    mine = job.Pending == pending;

                    if (mine)
                    {
                        job.Pending = null;
                        FinalizeLineLocked(job.Lines[pending.LineIndex], SuitTransferOutcome.Refused, $"Something went wrong before this was moved; it is still in {VaultPhrase(job.Lines[pending.LineIndex])}.");
                        CompleteLocked(job, MarketError.ServerError, "The transfer could not continue.");
                    }
                }

                if (mine)
                    world.ReleaseHolder(pending.HolderGuid);

                next = StepNext.Closed;
            }
            finally
            {
                lock (gate)
                    job.InStep = false;
            }

            if (next == StepNext.Continue)
                ScheduleNextOrFinish(job);
        }

        /// <summary>
        /// Everything from the re-checks to the queued save. Before the add every exit releases the claim and writes
        /// nothing. The add and the save are each guarded here, because after the add a throw cannot mean "nothing moved".
        /// </summary>
        private StepNext DeliverHolderCore(Job job, HolderPending pending)
        {
            var line = job.Lines[pending.LineIndex];
            var holder = pending.HolderGuid;

            // Every exit before the add: the line's own outcome (or the job's), then the claim goes back.
            StepNext RefuseBeforeAdd(string message)
            {
                lock (gate)
                {
                    if (job.Pending == pending)
                        job.Pending = null;

                    FinalizeLineLocked(line, SuitTransferOutcome.Refused, message);
                }

                world.ReleaseHolder(holder);
                return StepNext.Continue;
            }

            StepNext CloseBeforeAdd(MarketError reason, string message)
            {
                lock (gate)
                {
                    if (job.Pending == pending)
                        job.Pending = null;

                    CompleteLocked(job, reason, message);
                }

                world.ReleaseHolder(holder);
                return StepNext.Closed;
            }

            // 1. The target, exactly as every vault unit re-checks it.
            if (!world.IsStillEligible(job.Character))
                return CloseBeforeAdd(MarketError.CharacterOffline, "The character went offline, so the rest was not moved.");

            var gateError = world.CheckGates(job.Character, out var gateMessage, job.HolderOnly);

            if (gateError != MarketError.None)
            {
                TellSafe(job.Character, gateMessage + " The rest of your transfer was stopped.");
                return CloseBeforeAdd(gateError, gateMessage);
            }

            if (!world.CharacterMovesEnabled)
                return RefuseBeforeAdd(CharacterMovesOffMessage);

            // 2. The holder, again (the claim keeps a login out; this catches anything that bypassed it).
            var refusal = HolderStateRefusal(job, line);

            if (refusal != null)
                return RefuseBeforeAdd(refusal);

            // 3. Where the fresh read found the item.
            var read = pending.Read;

            switch (read?.Status)
            {
                case SuitHolderReadStatus.Found when read.ItemBiota != null:
                    break;

                case SuitHolderReadStatus.Worn:
                    return RefuseBeforeAdd($"{HolderPhrase(line.HolderName)} is wearing that item. Take it off first; worn gear is not moved.");

                case SuitHolderReadStatus.NotFound:
                    return RefuseBeforeAdd($"That item is no longer in {HolderPackPhrase(line)}.");

                default:
                    log.Error($"[SUIT] transfer {job.Id} account {job.AccountId}: the read of holder 0x{holder:X8} failed: {read?.Error ?? "no answer"}.");
                    return RefuseBeforeAdd($"{Capitalize(HolderPackPhrase(line))} could not be read, so nothing was moved from it.");
            }

            // 4. Build the object. Nothing registers it anywhere; dropping the reference discards it.
            var item = world.CreateHolderObject(read.ItemBiota);

            if (item == null)
                return RefuseBeforeAdd($"{Capitalize(HolderPackPhrase(line))} could not be read, so nothing was moved from it.");

            // 5. The projection's own predicates, on the live object.
            var itemRefusal = HolderItemRefusal(item, line, job.CharacterGuid);

            if (itemRefusal != null)
                return RefuseBeforeAdd(itemRefusal);

            // 6. The pack, against the real object.
            var freeSlots = world.FreeMainPackSlots(job.Character);

            if (freeSlots < 1 || Math.Max(0, item.EncumbranceVal ?? 0) > world.AvailableBurden(job.Character))
            {
                lock (gate)
                {
                    if (job.Pending == pending)
                        job.Pending = null;
                }

                world.ReleaseHolder(holder);
                StopPackFull(job, freeSlots < 1);
                return StepNext.Closed;
            }

            var label = HolderLabel(item);

            // 7. The add. From here on a throw is NOT "nothing moved".
            bool added;

            try
            {
                added = world.AddToPack(job.Character, item);
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] holder 0x{holder:X8} -> 0x{job.CharacterGuid:X8}: adding 0x{item.Guid.Full:X8} (wcid {item.WeenieClassId}) threw; its state is UNKNOWN. Holder pinned out of the world until restart; transfer {job.Id} stopped. {ex.GetFullMessage()}");

                lock (gate)
                {
                    if (job.Pending == pending)
                        job.Pending = null;

                    FinalizeLineLocked(line, SuitTransferOutcome.Threw, $"Something went wrong moving {label}, and where it ended up could not be confirmed. Contact staff; do not retry.");
                    CompleteLocked(job, MarketError.ServerError, HolderSaveStopMessage);
                }

                world.PinHolder(holder);
                return StepNext.Closed;
            }

            if (!added)
            {
                // Discarded WITHOUT Destroy: Destroy would delete the row that still names the holder.
                return RefuseBeforeAdd($"Your pack had no room for {label}; it was left in {HolderPackPhrase(line)}.");
            }

            // 8. The forced save, awaited.
            lock (gate)
            {
                pending.Phase = HolderPhase.Saving;
                pending.Item = item;
                pending.Label = label;
                job.LastProgressAt = world.UtcNow;
            }

            try
            {
                world.SaveHolderItem(item, ok => OnHolderSaved(job, pending, ok));
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] holder 0x{holder:X8} -> 0x{job.CharacterGuid:X8}: queueing the save of 0x{item.Guid.Full:X8} (wcid {item.WeenieClassId}) threw; it is in the pack but NOT confirmed saved. Holder pinned out of the world until restart; transfer {job.Id} stopped. {ex.GetFullMessage()}");

                bool mine;

                lock (gate)
                {
                    mine = job.Pending == pending;

                    if (mine)
                    {
                        job.Pending = null;
                        FinalizeLineLocked(line, SuitTransferOutcome.Threw, UnconfirmedSaveMessage(label));
                        CompleteLocked(job, MarketError.ServerError, HolderSaveStopMessage);
                    }
                }

                world.PinHolder(holder);
                return StepNext.Closed;
            }

            return StepNext.Pending;
        }

        private static string UnconfirmedSaveMessage(string label) => $"{label} reached your pack but its save could not be confirmed. Contact staff; do not retry.";

        // ---- shard worker: the save came back ----

        private void OnHolderSaved(Job job, HolderPending pending, bool ok)
        {
            var line = job.Lines[pending.LineIndex];
            var itemGuid = pending.Item?.Guid.Full ?? 0;
            bool current;

            lock (gate)
            {
                current = job.Pending == pending && pending.Phase == HolderPhase.Saving;

                if (current)
                {
                    job.Pending = null;
                    job.LastProgressAt = world.UtcNow;

                    if (ok)
                    {
                        line.Delivered++;
                        FinalizeLineLocked(line, SuitTransferOutcome.Delivered, null);
                    }
                    else
                    {
                        FinalizeLineLocked(line, SuitTransferOutcome.Threw, UnconfirmedSaveMessage(pending.Label));
                        CompleteLocked(job, MarketError.ServerError, HolderSaveStopMessage);
                    }
                }
            }

            if (ok)
            {
                // Confirmed: the row now names the target. A login of the holder from here reads it without the item.
                world.ReleaseHolder(pending.HolderGuid);
                log.Info($"[SUIT] holder 0x{pending.HolderGuid:X8} -> 0x{job.CharacterGuid:X8}: moved 0x{itemGuid:X8} (wcid {line.Request.Wcid}), transfer {job.Id}{(current ? "" : " (a LATE save confirmation after the transfer had closed)")}.");
            }
            else
            {
                world.PinHolder(pending.HolderGuid);
                log.Error($"[SUIT] holder 0x{pending.HolderGuid:X8} -> 0x{job.CharacterGuid:X8}: the save of 0x{itemGuid:X8} (wcid {line.Request.Wcid}) answered FALSE; it is in the target's pack but the row may still name the holder. Holder pinned out of the world until restart; transfer {job.Id} stopped.");
            }

            if (current && ok)
            {
                TellSafe(job.Character, $"{pending.Label} moved from {HolderPackPhrase(line)} to your pack.");
                ScheduleNextOrFinish(job);
            }
        }

        /// <summary>
        /// The stall watchdog's half (ExpireLocked, gate held): a unit still reading is refused and its claim released (a
        /// late read is a no-op); a unit still saving is Threw and its holder pinned (a late confirmed save unpins it).
        /// </summary>
        private void AbandonHolderPendingLocked(Job job)
        {
            var pending = job.Pending;

            if (pending == null)
                return;

            job.Pending = null;

            var line = job.Lines[pending.LineIndex];

            if (pending.Phase == HolderPhase.Saving)
            {
                FinalizeLineLocked(line, SuitTransferOutcome.Threw, UnconfirmedSaveMessage(pending.Label));
                world.PinHolder(pending.HolderGuid);
                log.Error($"[SUIT] holder 0x{pending.HolderGuid:X8} -> 0x{job.CharacterGuid:X8}: the save of wcid {line.Request.Wcid} never called back; holder pinned out of the world until restart or a late confirmation. Transfer {job.Id}.");
            }
            else
            {
                FinalizeLineLocked(line, SuitTransferOutcome.Refused, "The transfer stopped before this was moved.");
                world.ReleaseHolder(pending.HolderGuid);
            }
        }

        // ---- shared checks ----

        private void StopPackFull(Job job, bool noSlot)
        {
            var full = noSlot
                ? "Your pack filled up, so the rest of your transfer was stopped."
                : "You are carrying too much for the next item, so the rest of your transfer was stopped.";

            TellSafe(job.Character, full);

            lock (gate)
                CompleteLocked(job, MarketError.PackFull, full);
        }

        /// <summary>Null when the holder may be moved from right now; else the line's refusal. Reads PlayerManager only.</summary>
        private string HolderStateRefusal(Job job, LineState line)
        {
            var state = world.GetHolderState(line.Request.HolderGuid.Value);
            var name = line.HolderName ?? state.Name;

            if (!state.Exists || state.AccountId != job.AccountId)
                return $"{Capitalize(HolderPhrase(name))} is no longer one of your characters, so nothing was moved from it.";

            if (state.Deleted)
                return $"{Capitalize(HolderPhrase(name))} is deleted or pending deletion, so nothing was moved from it.";

            if (state.Online)
                return LogOutFirstMessage(name);

            return null;
        }

        /// <summary>
        /// The suit projection's predicates, applied to the object built from the fresh read: the same item the line
        /// named, suit-relevant (SuitItemProjector.IsSuitRelevant), not a pack-slot object, a single object, not attuned
        /// and not bonded (>= 1, the SuitItem.Attuned / Bonded flags), not issued by a PvP template (PvpTemplate.IsIssued,
        /// the vault's own template test), and wieldable by the target (AllowedWielder unset or the target).
        /// </summary>
        internal static string HolderItemRefusalFor(WorldObject item, uint expectedGuid, uint expectedWcid, string holderName, uint targetGuid)
        {
            if (item == null || item.Guid.Full != expectedGuid || item.WeenieClassId != expectedWcid)
                return $"That item is no longer in {HolderPhrase(holderName)}'s pack.";

            if (!SuitItemProjector.IsSuitRelevant(item))
                return "The suit builder only moves armor, clothing and jewelry between characters.";

            if (item.UseBackpackSlot || item is Container)
                return "A pack cannot be moved between characters by the suit builder.";

            if ((item.StackSize ?? 1) != 1)
                return "Only a single item can be moved between characters by the suit builder.";

            if ((item.GetProperty(PropertyInt.Attuned) ?? 0) >= 1)
                return "That item is attuned and cannot be moved between characters.";

            if ((item.GetProperty(PropertyInt.Bonded) ?? 0) >= 1)
                return "That item is bonded and cannot be moved between characters.";

            if (PvpTemplate.IsIssued(item))
                return "That item was issued by a PvP template and cannot be moved.";

            var allowed = item.GetProperty(PropertyInstanceId.AllowedWielder);

            if (allowed != null && allowed.Value != 0 && allowed.Value != targetGuid)
                return "That item can only be wielded by its owner and cannot be moved to another character.";

            return null;
        }

        private static string HolderItemRefusal(WorldObject item, LineState line, uint targetGuid)
            => HolderItemRefusalFor(item, line.Request.ItemGuid.Value, line.Request.Wcid, line.HolderName, targetGuid);

        private static string HolderLabel(WorldObject item)
        {
            try
            {
                var name = item?.NameWithMaterial;

                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            catch (Exception)
            {
                // A name that cannot be read must not stand between the add and its bookkeeping.
            }

            return "That item";
        }
    }
}
