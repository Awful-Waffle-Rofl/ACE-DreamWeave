using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.SalvageRoller
{
    /// <summary>
    /// The crash windows of DESIGN 5.6. A test hook (<see cref="SalvageRollerManager.CrashHook"/>) is consulted at the
    /// START of each one; when it answers true the state machine stops right there, as a crashed process would, and
    /// leaves the database exactly as that window describes.
    /// </summary>
    internal enum SalvageRollerCrashPoint
    {
        /// <summary>The sale's detach is saved; no batch row yet.</summary>
        W0,
        /// <summary>The Escrowing rows (and the Sold/Escrowed audit rows) are written; no escrow save yet.</summary>
        W1,
        /// <summary>Every item is escrowed and saved; the confirmation is about to open.</summary>
        W2,
        /// <summary>Pending (and the payload) is committed; the Hammers are not destroyed yet.</summary>
        W3,
        /// <summary>Taking(k) is committed and the result applied in memory; the delivery save has not run.</summary>
        W4,
        /// <summary>The delivery save has landed; the batch rows are not deleted yet.</summary>
        W5,
    }

    public static partial class SalvageRollerManager
    {
        // ---------------- player-facing lines ----------------

        public const string StartFailedMessage = "The Salvage Roller could not start your batch, so your items were handed back.";

        public const string AbortedMessage = "Your Salvage Roller batch was cancelled and your items are back in your pack.";

        public const string AbortIncompleteMessage = "Your Salvage Roller batch was cancelled, but not every item could be returned yet. They are safe; make room in your pack and use the Salvage Roller again.";

        public const string RollFailedMessage = "The Salvage Roller could not roll that batch, so it was cancelled.";

        public const string RecordFailedMessage = "The Salvage Roller could not record that step, so it was not taken. Your items are safe; use the Salvage Roller again in a moment.";

        public const string NoBatchToTakeMessage = "You have no Salvage Roller results waiting.";

        public const string TakeNeedsRoomMessage = "You need room in your pack to take a result.";

        public const string DeliveryDeferredMessage = "Your chosen result could not be placed in your pack. It is safe, and the Salvage Roller will deliver it the next time you use it.";

        public const string AwaitingAnswerMessage = "Your Salvage Roller batch is waiting for your answer.";

        public const string NeedsAttentionMessage = "Your Salvage Roller batch could not be finished right now. Use the Salvage Roller again in a moment.";

        public const string ConfirmationBusyMessage = "Answer your other open confirmation first; your items were handed back.";

        public const string ConfirmationBusyIncompleteMessage = "Answer your other open confirmation first. Not every item could be returned yet; they are safe. Make room in your pack and use the Salvage Roller again.";

        // ---------------- process state ----------------

        /// <summary>
        /// Batches whose confirmation is open (or about to open) in THIS process, with the station that sold them (null
        /// under test) and when the question was put. Reconcile leaves a fresh entry alone, because aborting it would
        /// race the player's Yes; an entry older than <see cref="AbandonAfter"/> is one whose answer is never coming (a
        /// logout, a lost callback) and reconcile removes it and cancels the batch.
        /// </summary>
        private static readonly ConcurrentDictionary<ulong, (SalvageRollerVendor Station, DateTime SentUtc)> awaitingAnswer = new ConcurrentDictionary<ulong, (SalvageRollerVendor Station, DateTime SentUtc)>();

        /// <summary>
        /// How long an open confirmation is waited for: the answer counts as No at LateAfter, and the margin covers the
        /// client's automatic reply arriving and being run on the player's queue.
        /// </summary>
        internal static readonly TimeSpan AbandonAfter = Confirmation_ThreadGroupStart.LateAfter + TimeSpan.FromSeconds(5);

        /// <summary>The clock the awaiting-answer ages are measured on. A seam for the tests.</summary>
        internal static Func<DateTime> Clock = () => DateTime.UtcNow;

        /// <summary>
        /// Batch items that are live in memory with no parent: sold but not yet escrowed, or released and then neither
        /// delivered nor returned. Reconcile looks here BEFORE loading a parentless biota from the shard, so a second
        /// live copy of one guid is never made in-process.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, WorldObject> stranded = new ConcurrentDictionary<uint, WorldObject>();

        /// <summary>Test-only crash injection: answer true at a <see cref="SalvageRollerCrashPoint"/> to stop there. Null in production.</summary>
        internal static Func<SalvageRollerCrashPoint, bool> CrashHook;

        private static bool Crash(SalvageRollerCrashPoint point)
        {
            var hook = CrashHook;
            return hook != null && hook(point);
        }

        /// <summary>What a process restart forgets. Test-only: the fault-injection tests call it after a simulated crash.</summary>
        internal static void ForgetProcessStateForTest()
        {
            awaitingAnswer.Clear();
            stranded.Clear();
            waitingOpens.Clear();
        }

        internal static bool IsAwaitingAnswer(ulong batchId) => awaitingAnswer.ContainsKey(batchId);

        internal static int StrandedCount => stranded.Count;

        /// <summary>The fresh guid of a display row. A seam because the tests run without the dynamic guid pool.</summary>
        internal static Func<ObjectGuid> DisplayGuid = GuidManager.NewDynamicGuid;

        private static bool IsWeapon(SalvageRollerAction action) =>
            action == SalvageRollerAction.TourmalineReroll || action == SalvageRollerAction.AmethystSwap;

        /// <summary>
        /// True when nothing holds <paramref name="item"/>: no container, no wielder and no Location. An object with a
        /// Location is on a landblock, which loads it itself; loading it again from the shard would make a second live
        /// copy of the same guid. <paramref name="holder"/> says what holds it.
        /// </summary>
        internal static bool IsLoose(WorldObject item, out string holder)
        {
            if (item.ContainerId != null || item.WielderId != null)
            {
                holder = $"it is parented: container {item.ContainerId}, wielder {item.WielderId}";
                return false;
            }

            if (item.Location != null)
            {
                holder = $"it has a Location ({item.Location}): it is on a landblock, not unparented";
                return false;
            }

            holder = null;
            return true;
        }

        // =================================================================================================
        // sell: DESIGN 5.4 step 4
        // =================================================================================================

        internal delegate bool DetachedVerifier(IReadOnlyList<WorldObject> detached,
            out SalvageRollerAction action, out WorldObject target, out List<WorldObject> hammers, out string refusal);

        internal enum SellOutcome
        {
            /// <summary>Refused before any row existed; every item went back through the hand-back.</summary>
            HandedBack,
            /// <summary>Escrowed and the confirmation is open.</summary>
            Asked,
            /// <summary>Escrowed in memory; the confirmation opens once every escrow save has landed.</summary>
            Escrowing,
            /// <summary>Rows existed and the batch was cancelled (or is left for reconcile when that could not finish).</summary>
            Aborted,
            /// <summary>A test crash point stopped it.</summary>
            Halted,
        }

        /// <summary>The station's entry: the production host, the post-detach backstop, and the station's own hand-back.</summary>
        internal static SellOutcome Sell(Player player, SalvageRollerVendor station, IReadOnlyList<WorldObject> detached, Action<WorldObject> handBackLoose)
        {
            var host = new PlayerSalvageRollerHost(player);

            DetachedVerifier verify = (IReadOnlyList<WorldObject> items, out SalvageRollerAction action, out WorldObject target, out List<WorldObject> hammers, out string refusal) =>
                SalvageRollerEligibility.VerifyDetachedBasket(player, items, out action, out target, out hammers, out refusal);

            return Sell(host, detached, verify, handBackLoose, station);
        }

        /// <summary>
        /// The sell pipeline, after the sell path has detached the items and saved the detach:
        ///   4.1 the post-detach backstop; a refusal hands everything back, nothing else has moved.
        ///   4.2 the Escrowing batch row and its item rows in one SaveChanges, then the Sold and Escrowed audit rows.
        ///       A failed row write hands everything back. A failed audit write refuses the escrow and aborts.
        ///   4.3 every item into escrow on the store's queue, each one saved.
        ///   4.4 the forced-pick confirmation, opened only once EVERY escrow save has landed (a LandingGate over the
        ///       saves, then the player's own thread); a save that did not land cancels the batch instead. A Yes over an
        ///       escrow the shard has not recorded could commit a roll whose Hammers a crash then hands back. Its
        ///       answer arrives at <see cref="OnRollAnswered(ISalvageRollerHost, ulong, bool, SalvageRollerVendor)"/>.
        /// Once the rows exist, nothing is ever handed back loose: the abort path delivers each item and deletes the rows
        /// only after every delivery save has landed, so the purge exemption covers every item until it has a parent.
        /// </summary>
        internal static SellOutcome Sell(ISalvageRollerHost host, IReadOnlyList<WorldObject> detached, DetachedVerifier verify,
            Action<WorldObject> handBackLoose, SalvageRollerVendor station = null)
        {
            var items = (detached ?? Array.Empty<WorldObject>()).Where(i => i != null).ToList();

            // 4.1 the backstop. Skips only "in the pack" and "not busy", which the detach itself breaks.
            if (!verify(items, out var action, out var target, out var hammers, out var refusal))
            {
                Log.Warn($"[SALVAGE ROLLER] {host.CharacterName} (0x{host.CharacterId:X8}): the post-detach backstop refused a basket the pre-detach gate accepted ({refusal}); handing every item back.");
                HandBackLoose(host, items, handBackLoose);
                host.Tell(refusal);
                return SellOutcome.HandedBack;
            }

            if (Crash(SalvageRollerCrashPoint.W0))
                return SellOutcome.Halted;

            var batch = new CharacterSalvageBatch
            {
                CharacterId = host.CharacterId,
                AccountId = host.AccountId,
                ItemGuid = target.Guid.Full,
                ItemWcid = target.WeenieClassId,
                Action = (byte)action,
                State = (byte)SalvageBatchState.Escrowing,
                RollCount = SalvageRollerEligibility.RollCount(hammers),
                CreatedAt = DateTime.UtcNow,
            };

            batch.Items.Add(new CharacterSalvageBatchItem { Guid = target.Guid.Full, Role = (byte)SalvageBatchItemRole.Target });

            foreach (var hammer in hammers)
                batch.Items.Add(new CharacterSalvageBatchItem { Guid = hammer.Guid.Full, Role = (byte)SalvageBatchItemRole.Hammer });

            // 4.2 the rows FIRST
            bool added;

            try
            {
                added = host.AddBatch(batch);
            }
            catch (Exception ex)
            {
                Log.Error($"{RefundPrefix} AddBatch threw for {host.CharacterName} (0x{host.CharacterId:X8}): {ex.Message}");
                added = false;
            }

            if (!added || batch.Id == 0)
            {
                // No row exists, so no purge exemption covers these items: the only safe home is the pack, now.
                SalvageRollerAuditTrail.LogRefund(host, batch, "the batch rows could not be written, so every item was handed back loose.",
                    new JsonObject { ["target"] = SalvageRollerAuditTrail.ItemJson(target), ["hammers"] = SalvageRollerAuditTrail.ItemsJson(hammers) });

                HandBackLoose(host, items, handBackLoose);
                host.Tell(StartFailedMessage);
                return SellOutcome.HandedBack;
            }

            // From here every item is covered by a row. Until it is escrowed it is live in memory with no parent.
            foreach (var item in items)
                stranded[item.Guid.Full] = item;

            var sold = new JsonObject
            {
                ["target"] = SalvageRollerAuditTrail.ItemJson(target, "sold: detached and saved with no parent"),
                ["hammers"] = SalvageRollerAuditTrail.ItemsJson(hammers, "sold: detached and saved with no parent"),
                ["moves"] = SalvageRollerAuditTrail.Moves("in the character's pack", "sold to the Salvage Roller window; batch rows written (Escrowing)"),
            };

            if (!SalvageRollerAuditTrail.Write(host, SalvageRollerAuditEvent.Sold, batch, target.Guid.Full, sold))
                return AbortFromSell(host, batch, "the Sold audit row could not be written");

            var escrowing = new JsonObject
            {
                ["target"] = SalvageRollerAuditTrail.ItemJson(target, "about to be escrowed"),
                ["hammers"] = SalvageRollerAuditTrail.ItemsJson(hammers, "about to be escrowed"),
                ["moves"] = SalvageRollerAuditTrail.Moves("no parent", $"the account {host.AccountId} escrow containers"),
            };

            if (!SalvageRollerAuditTrail.Write(host, SalvageRollerAuditEvent.Escrowed, batch, target.Guid.Full, escrowing))
                return AbortFromSell(host, batch, "the Escrowed audit row could not be written");

            if (Crash(SalvageRollerCrashPoint.W1))
                return SellOutcome.Halted;

            // 4.3 into escrow, on the store's queue, each item saved
            var store = host.GetStore();

            string notReady = null;

            if (store == null || !store.IsEscrowReady(out notReady))
                return AbortFromSell(host, batch, "the escrow was not ready to take the items");

            string escrowFailure = null;
            var saves = new LandingGate();

            var queued = store.Enqueue(() =>
            {
                foreach (var item in items)
                {
                    var landed = saves.Expect();

                    if (store.TryEscrow(item, out _, out var why, landed))
                    {
                        stranded.TryRemove(item.Guid.Full, out _);
                        continue;
                    }

                    landed(false);     // a refusal never reaches the save, so its token is released here
                    escrowFailure = $"0x{item.Guid.Full:X8} ({item.Name}) could not be escrowed: {why}";
                    return;
                }
            }, out var thrown);

            if (!queued || thrown != null || escrowFailure != null)
                return AbortFromSell(host, batch, escrowFailure ?? (thrown != null ? $"the escrow threw: {thrown.Message}" : "the store refused the work"));

            // 4.4 the forced-pick confirmation, once every escrow save has landed. Registered BEFORE the gate is armed,
            // so reconcile treats the batch as answered-for from this moment (F3 ages the entry out if nothing comes).
            awaitingAnswer[batch.Id] = (station, Clock());

            SellOutcome? asked = null;

            saves.Arm(allLanded => host.RunOnOwnerThread(() => asked = AskOnceEscrowed(host, batch, target, hammers, allLanded)));

            return asked ?? SellOutcome.Escrowing;
        }

        /// <summary>
        /// 4.4, on the player's thread once the escrow saves have all reported: opens the confirmation, or cancels the
        /// batch when a save did not land. Does nothing when reconcile has already abandoned the batch.
        /// </summary>
        private static SellOutcome AskOnceEscrowed(ISalvageRollerHost host, CharacterSalvageBatch batch, WorldObject target, List<WorldObject> hammers, bool allLanded)
        {
            if (!awaitingAnswer.TryGetValue(batch.Id, out var entry))
            {
                Log.Warn($"[SALVAGE ROLLER] {host.CharacterName}: batch {batch.Id}'s escrow saves reported after reconcile abandoned it; not asking.");
                return SellOutcome.Aborted;
            }

            if (!allLanded)
            {
                awaitingAnswer.TryRemove(batch.Id, out _);
                return AbortFromSell(host, batch, "an escrow save did not land, so the roll was never offered");
            }

            if (Crash(SalvageRollerCrashPoint.W2))
                return SellOutcome.Halted;

            // the age reconcile measures starts when the question is put, not when the saves were queued
            if (!awaitingAnswer.TryUpdate(batch.Id, (entry.Station, Clock()), entry))
            {
                Log.Warn($"[SALVAGE ROLLER] {host.CharacterName}: batch {batch.Id} was abandoned while its question was being put; not asking.");
                return SellOutcome.Aborted;
            }

            var material = hammers[0].MaterialType ?? MaterialType.Unknown;
            var prompt = $"Roll {batch.RollCount} times on {target.Name} with {SalvageRollerEligibility.MaterialName(material)}? Every Hammer is spent, you must take one result, and the original is not returned.";

            bool asked;

            try
            {
                asked = host.AskToRoll(batch.Id, prompt);
            }
            catch (Exception ex)
            {
                Log.Error($"{RefundPrefix} {host.CharacterName}: the confirmation for batch {batch.Id} threw: {ex.Message}");
                asked = false;
            }

            if (!asked)
            {
                awaitingAnswer.TryRemove(batch.Id, out _);
                var aborted = AbortBatch(host, batch, SalvageRollerAuditEvent.Aborted, "the confirmation could not be shown");
                host.Tell(aborted == AbortResult.Done ? ConfirmationBusyMessage : ConfirmationBusyIncompleteMessage);
                return SellOutcome.Aborted;
            }

            return SellOutcome.Asked;
        }

        private static SellOutcome AbortFromSell(ISalvageRollerHost host, CharacterSalvageBatch batch, string why)
        {
            var result = AbortBatch(host, batch, SalvageRollerAuditEvent.Aborted, why);
            host.Tell(result == AbortResult.Done ? StartFailedMessage : AbortIncompleteMessage);
            return SellOutcome.Aborted;
        }

        /// <summary>Hands back items no row covers, one at a time: one failure (a throw included) never skips the rest.</summary>
        private static void HandBackLoose(ISalvageRollerHost host, List<WorldObject> items, Action<WorldObject> handBackLoose)
        {
            foreach (var item in items)
            {
                try
                {
                    handBackLoose(item);
                }
                catch (Exception ex)
                {
                    Log.Error($"{RefundPrefix} ORPHAN: 0x{item.Guid.Full:X8} (wcid {item.WeenieClassId}, {item.Name}) threw while being handed back to {host.CharacterName} (0x{host.CharacterId:X8}). It is detached and needs manual recovery.", ex);
                }
            }
        }

        // =================================================================================================
        // the answer: DESIGN 5.4 steps 4.5 and 5
        // =================================================================================================

        internal enum RollOutcome { Ignored, Aborted, Rolled, NotNow, Halted }

        /// <summary>The confirmation's production entry, on the player's action queue.</summary>
        internal static void OnRollAnswered(Player player, ulong batchId, bool yes)
        {
            awaitingAnswer.TryGetValue(batchId, out var entry);
            var station = entry.Station;

            var host = new PlayerSalvageRollerHost(player);
            var outcome = OnRollAnswered(host, batchId, yes, station);

            if (station == null || station.IsDestroyed)
                return;

            // the rows were set on the player's own view (ShowRows below); this sends them
            if (outcome == RollOutcome.Rolled)
                station.ApproachVendor(player);
        }

        /// <summary>A confirmation answered after its player logged out: the batch is left Escrowing for reconcile.</summary>
        internal static void OnRollAbandoned(ulong batchId)
        {
            awaitingAnswer.TryRemove(batchId, out _);
            Log.Warn($"[SALVAGE ROLLER] batch {batchId}: the confirmation was answered after its player logged out; reconcile will cancel it at the next station Use.");
        }

        /// <summary>
        /// No (or a timeout) aborts: every item back to the pack, then the rows. Yes rolls every charge on scratch copies,
        /// writes the Rolled audit row, commits Pending with the payload in one statement, then destroys the Hammers out
        /// of escrow and fills the seller's view on the station. A roll that throws InvalidOperationException aborts the batch.
        /// </summary>
        internal static RollOutcome OnRollAnswered(ISalvageRollerHost host, ulong batchId, bool yes, SalvageRollerVendor station = null)
        {
            if (!awaitingAnswer.TryRemove(batchId, out _))
            {
                Log.Warn($"[SALVAGE ROLLER] {host.CharacterName}: an answer for batch {batchId} arrived with no open confirmation; ignored.");
                return RollOutcome.Ignored;
            }

            if (!host.TryReadBatch(out var batch))
            {
                host.Tell(NeedsAttentionMessage);
                return RollOutcome.NotNow;
            }

            if (batch == null || batch.Id != batchId || batch.State != (byte)SalvageBatchState.Escrowing)
            {
                Log.Warn($"[SALVAGE ROLLER] {host.CharacterName}: batch {batchId} is no longer Escrowing (now {(batch == null ? "gone" : ((SalvageBatchState)batch.State).ToString())}); the answer is ignored.");
                return RollOutcome.Ignored;
            }

            if (!yes)
            {
                var aborted = AbortBatch(host, batch, SalvageRollerAuditEvent.Aborted, "the player answered No, or the confirmation timed out");
                host.Tell(aborted == AbortResult.Done ? AbortedMessage : AbortIncompleteMessage);
                return RollOutcome.Aborted;
            }

            var store = host.GetStore();

            string notReady = null;

            if (store == null || !store.IsEscrowReady(out notReady))
            {
                host.Tell(NeedsAttentionMessage);
                return RollOutcome.NotNow;
            }

            var target = store.FindEscrowed(batch.ItemGuid);

            if (target == null)
            {
                AbortBatch(host, batch, SalvageRollerAuditEvent.Aborted, "the target was not in escrow when the roll was confirmed");
                host.Tell(RollFailedMessage);
                return RollOutcome.Aborted;
            }

            var action = (SalvageRollerAction)batch.Action;

            List<SalvageRollerCandidate> candidates;

            try
            {
                candidates = SalvageRollerEngine.Roll(target, action, batch.RollCount);
            }
            catch (InvalidOperationException ex)
            {
                AbortBatch(host, batch, SalvageRollerAuditEvent.Aborted, $"the roll could not be made: {ex.Message}");
                host.Tell(RollFailedMessage);
                return RollOutcome.Aborted;
            }

            var payload = new SalvageRollerPayload(candidates).Serialize();

            var rolled = new JsonObject
            {
                ["target"] = SalvageRollerAuditTrail.ItemJson(target, "escrow"),
                ["payload"] = JsonNode.Parse(payload),
                ["moves"] = SalvageRollerAuditTrail.Moves("Escrowing", "Pending, with every candidate's result recorded; the Hammers are destroyed next"),
            };

            if (!SalvageRollerAuditTrail.Write(host, SalvageRollerAuditEvent.Rolled, batch, target.Guid.Full, rolled))
            {
                var aborted = AbortBatch(host, batch, SalvageRollerAuditEvent.Aborted, "the Rolled audit row could not be written, so the roll was not committed");
                host.Tell(aborted == AbortResult.Done ? RecordFailedMessage : AbortIncompleteMessage);
                return RollOutcome.Aborted;
            }

            if (!host.TryCommitPending(batch.Id, payload))
            {
                // A false here may be a commit that LANDED and lost its acknowledgement. Aborting then would hand back
                // the Hammers of a committed roll: a free roll. So the row is read again and decides.
                if (!host.TryReadBatch(out var reread))
                {
                    SalvageRollerAuditTrail.LogRefund(host, batch, "the Pending commit reported failure and the batch could not be re-read, so whether it committed is unknown; left for reconcile.", null);
                    host.Tell(NeedsAttentionMessage);
                    return RollOutcome.NotNow;
                }

                if (reread == null || reread.Id != batch.Id || reread.State != (byte)SalvageBatchState.Pending)
                {
                    var aborted = AbortBatch(host, batch, SalvageRollerAuditEvent.Aborted, "the Pending commit did not land");
                    host.Tell(aborted == AbortResult.Done ? RollFailedMessage : AbortIncompleteMessage);
                    return RollOutcome.Aborted;
                }

                Log.Warn($"[SALVAGE ROLLER] {host.CharacterName}: batch {batch.Id}'s Pending commit reported failure but the row reads Pending; rolling forward with the committed payload.");
                payload = reread.Payload;
            }

            batch.State = (byte)SalvageBatchState.Pending;
            batch.Payload = payload;

            if (Crash(SalvageRollerCrashPoint.W3))
                return RollOutcome.Halted;

            ConsumeHammers(host, store, batch, SalvageRollerAuditEvent.HammersConsumed);

            if (station != null && !station.IsDestroyed)
                ShowRows(station, host.CharacterId, batch, target);

            return RollOutcome.Rolled;
        }

        // =================================================================================================
        // the take: DESIGN 5.4 step 7
        // =================================================================================================

        internal enum TakeOutcome { Refused, Delivered, Deferred, Halted }

        internal readonly struct TakeResult
        {
            public TakeResult(TakeOutcome outcome, string message)
            {
                Outcome = outcome;
                Message = message;
            }

            public TakeOutcome Outcome { get; }

            public string Message { get; }
        }

        /// <summary>
        /// The station's TakeHandler. True once the result is in the pack. A delivered take empties the taker's view and
        /// re-approaches, so the client is sent an EMPTY panel instead of keeping the stale rows (a successful buy does
        /// not re-approach on its own). The view itself is kept, so the next sale into that still-open panel is served.
        /// False re-sends the panel.
        /// </summary>
        internal static bool TakeFromStation(Player player, SalvageRollerVendor station, int seq)
        {
            var host = new PlayerSalvageRollerHost(player);

            return FinishTake(player, station, Take(host, seq), host.Tell);
        }

        /// <summary>What the station does with a take's outcome, apart from the shard work. A seam for the tests.</summary>
        internal static bool FinishTake(Player player, SalvageRollerVendor station, TakeResult result, Action<string> tell)
        {
            switch (result.Outcome)
            {
                case TakeOutcome.Delivered:
                    // Only the taker's rows go; every other viewer's rows are untouched. The view is KEPT: the client's panel
                    // stays open, and dropping the view here refused the next sale into it with NoViewMessage.
                    station.ClearRows(player.Guid.Full);

                    // the kept view's search filter would otherwise hide the next batch's rows
                    station.SetSearchFilter(player.Guid.Full, null);

                    station.ApproachVendor(player, VendorType.Buy);
                    return true;

                case TakeOutcome.Deferred:
                    // the batch is Taking now; the rows on screen can no longer be taken
                    station.ClearRows(player.Guid.Full);
                    tell(result.Message);
                    return false;

                default:
                    tell(result.Message);
                    return false;
            }
        }

        /// <summary>
        /// Checks, in order, before any write: the batch is this character's and Pending, its payload reads and holds
        /// exactly roll_count candidates, result <paramref name="seq"/> exists, the target is in escrow, and the pack has
        /// room. Then the Taken audit row (with the full post-take snapshot), then Taking(k), then the move.
        /// </summary>
        internal static TakeResult Take(ISalvageRollerHost host, int seq)
        {
            if (!host.TryReadBatch(out var batch))
                return new TakeResult(TakeOutcome.Refused, StatusReadFailedMessage);

            if (batch == null || batch.CharacterId != host.CharacterId)
                return new TakeResult(TakeOutcome.Refused, NoBatchToTakeMessage);

            if (batch.State != (byte)SalvageBatchState.Pending)
                return new TakeResult(TakeOutcome.Refused, DescribeStatus(batch));

            if (!TryReadPayload(host, batch, out var payload))
                return new TakeResult(TakeOutcome.Refused, NeedsAttentionMessage);

            var candidate = payload.Find(seq);

            if (candidate == null)
                return new TakeResult(TakeOutcome.Refused, SalvageRollerVendor.UnknownRowMessage);

            var store = host.GetStore();

            string notReady = null;

            if (store == null || !store.IsEscrowReady(out notReady))
                return new TakeResult(TakeOutcome.Refused, notReady ?? AccountVaultStore.EscrowUnavailableMessage);

            var target = store.FindEscrowed(batch.ItemGuid);

            if (target == null)
            {
                SalvageRollerAuditTrail.LogRefund(host, batch, "a take found the target missing from a ready escrow.", new JsonObject { ["seq"] = seq });
                return new TakeResult(TakeOutcome.Refused, NeedsAttentionMessage);
            }

            if (!host.HasPackRoomFor(target))
                return new TakeResult(TakeOutcome.Refused, TakeNeedsRoomMessage);

            // the state the item will be in, on a WHOLE-biota scratch copy (instance ids and all), so the Taken row holds
            // the full post-take snapshot a refund can be rebuilt from
            var after = FullScratchCopy(target);
            SalvageRollerEngine.ApplyDiff(after, candidate.Diff);
            after.SetProperty(PropertyInt64.SalvageBatchApplied, (long)batch.Id);

            var taken = new JsonObject
            {
                ["chosen"] = SalvageRollerAuditTrail.CandidateJson(candidate),
                ["before"] = SalvageRollerAuditTrail.ItemJson(target, "escrow"),
                ["after"] = SalvageRollerAuditTrail.ItemJson(after, "the character's pack, once delivered", target.Guid.Full),
                ["moves"] = SalvageRollerAuditTrail.Moves("Pending; the target in escrow", $"Taking({seq}); result {seq} applied and stamped; the target in the character's pack"),
            };

            if (!SalvageRollerAuditTrail.Write(host, SalvageRollerAuditEvent.Taken, batch, batch.ItemGuid, taken))
                return new TakeResult(TakeOutcome.Refused, RecordFailedMessage);

            if (!host.TrySetTaking(batch.Id, seq))
                return new TakeResult(TakeOutcome.Refused, NeedsAttentionMessage);

            batch.State = (byte)SalvageBatchState.Taking;
            batch.ChosenSeq = seq;

            return ForwardTaking(host, store, batch, candidate, null);
        }

        /// <summary>
        /// Finishes a Taking batch, from the take itself or from reconcile (W4, W5). Finds the target wherever it is; when
        /// it does not yet carry this batch's stamp, applies result k and stamps it (capturing the weapon tier BEFORE the
        /// apply, so the transition sees the change); delivers it and saves it once; deletes the rows only after that
        /// save has landed. A delivery that fails returns the item to escrow with the result applied: the row stays
        /// Taking, and the stamp keeps the next attempt from applying twice.
        /// </summary>
        private static TakeResult ForwardTaking(ISalvageRollerHost host, AccountVaultStore store, CharacterSalvageBatch batch,
            SalvageRollerCandidate candidate, SalvageRollerAuditEvent? auditFirst)
        {
            var seq = batch.ChosenSeq ?? 0;
            var item = Locate(host, store, batch.ItemGuid, out var where, out _, out var holder);

            if (item == null)
            {
                SalvageRollerAuditTrail.LogRefund(host, batch, $"a Taking batch's target could not be found in the pack, the escrow or memory, and the shard says {Describe(where, holder)}.", new JsonObject { ["seq"] = seq });
                return new TakeResult(TakeOutcome.Refused, NeedsAttentionMessage);
            }

            var stamp = item.GetProperty(PropertyInt64.SalvageBatchApplied);
            var stamped = stamp == (long)batch.Id;

            if (auditFirst.HasValue)
            {
                var forward = new JsonObject
                {
                    ["chosen"] = SalvageRollerAuditTrail.CandidateJson(candidate),
                    ["found"] = SalvageRollerAuditTrail.ItemJson(item, where.ToString()),
                    ["alreadyApplied"] = stamped,
                    ["moves"] = SalvageRollerAuditTrail.Moves($"Taking({seq}); the target in {where}", where == Where.Pack && stamped
                        ? "rows deleted; the item was already delivered"
                        : $"result {seq} applied (unless already stamped); the target delivered to the character's pack; rows deleted"),
                };

                if (!SalvageRollerAuditTrail.Write(host, auditFirst.Value, batch, batch.ItemGuid, forward))
                    return new TakeResult(TakeOutcome.Refused, RecordFailedMessage);
            }

            if (where == Where.Pack)
            {
                if (!stamped)
                {
                    // In the pack WITHOUT this batch's stamp: nothing in this state machine puts it there, so do not guess.
                    SalvageRollerAuditTrail.LogRefund(host, batch, $"a Taking batch's target is in the pack without the batch stamp (stamp {stamp?.ToString() ?? "none"}); left for staff.", new JsonObject { ["found"] = SalvageRollerAuditTrail.ItemJson(item, "pack") });
                    return new TakeResult(TakeOutcome.Refused, NeedsAttentionMessage);
                }

                // W5, or a delivery whose save did not land: save it where it is, then finish. The tier transition ran
                // before the save that put it here, so it is not run again.
                var finish = new LandingGate();
                ConsumeHammers(host, store, batch, SalvageRollerAuditEvent.ReconcileForward, finish);
                SaveThenFinish(host, batch, item, finish);
                return new TakeResult(TakeOutcome.Delivered, null);
            }

            if (where == Where.Escrow)
            {
                WorldObject released = null;
                string why = null;
                var ok = false;

                store.Enqueue(() => ok = store.TryReleaseEscrow(batch.ItemGuid, out released, out why), out var thrown);

                if (!ok || released == null || thrown != null)
                {
                    Log.Error($"{RefundPrefix} batch {batch.Id}: the target 0x{batch.ItemGuid:X8} could not be released from escrow ({why ?? thrown?.Message}); the row stays Taking.");
                    return new TakeResult(TakeOutcome.Deferred, DeliveryDeferredMessage);
                }

                item = released;
            }

            // Released, stranded or loaded: live, unparented, ours.
            stranded[item.Guid.Full] = item;

            WeaponQualityTier? tierBefore = null;

            if (!stamped)
            {
                if (IsWeapon((SalvageRollerAction)batch.Action))
                    tierBefore = WeaponQualityTiers.Evaluate(item);

                SalvageRollerEngine.ApplyDiff(item, candidate.Diff);
                item.SetProperty(PropertyInt64.SalvageBatchApplied, (long)batch.Id);
            }
            else if (IsWeapon((SalvageRollerAction)batch.Action))
            {
                // Applied before a delivery that failed, so the tier transition never ran. The item's pre-take state
                // lives only in the Taken audit row now; the tier is measured from that.
                tierBefore = TierBeforeFromTakenAudit(host, batch);
            }

            if (Crash(SalvageRollerCrashPoint.W4))
                return new TakeResult(TakeOutcome.Halted, null);

            // The remaining Hammers go before the target leaves, so the final delete never orphans one in escrow. The
            // batch rows go only once the Hammers' removals AND the delivery save have all landed.
            var gate = new LandingGate();
            ConsumeHammers(host, store, batch, auditFirst.HasValue ? SalvageRollerAuditEvent.ReconcileForward : SalvageRollerAuditEvent.HammersConsumed, gate);

            if (!host.TryPutInPack(item))
            {
                gate.Arm(_ => { });     // the Hammer rows go in their own callbacks; the batch stays Taking
                var failed = new JsonObject
                {
                    ["held"] = SalvageRollerAuditTrail.ItemJson(item, "released, result applied, not delivered"),
                    ["moves"] = SalvageRollerAuditTrail.Moves($"Taking({seq}); delivery refused by the pack", "returned to escrow with the result applied; still Taking"),
                };

                if (SalvageRollerAuditTrail.Write(host, SalvageRollerAuditEvent.DeliveryFailed, batch, batch.ItemGuid, failed))
                {
                    var back = false;
                    string why = null;
                    store.Enqueue(() => back = store.TryReturnToEscrow(item, out why), out var thrown);

                    if (back && thrown == null)
                        stranded.TryRemove(item.Guid.Full, out _);
                    else
                        Log.Error($"{RefundPrefix} batch {batch.Id}: 0x{item.Guid.Full:X8} ({item.Name}) could not be returned to escrow ({why ?? thrown?.Message}); it is held in memory and the row stays Taking.");
                }

                return new TakeResult(TakeOutcome.Deferred, DeliveryDeferredMessage);
            }

            stranded.TryRemove(item.Guid.Full, out _);

            // The tier transition BEFORE the save, so whatever it sets on the item is in that same save.
            try
            {
                host.AfterTake(item, tierBefore);
            }
            catch (Exception ex)
            {
                Log.Error($"{RefundPrefix} batch {batch.Id}: the tier transition for 0x{item.Guid.Full:X8} threw: {ex.Message}");
            }

            // ONE save records the new parent, the result, the stamp and the tier transition together.
            SaveThenFinish(host, batch, item, gate);

            host.Chat($"You take result #{seq}: {item.Name}.");

            foreach (var line in candidate.Summary?.Lines ?? new List<string>())
                host.Chat($"  {line}");

            return new TakeResult(TakeOutcome.Delivered, null);
        }

        /// <summary>
        /// Saves the delivered target into <paramref name="gate"/> (which already counts the Hammers' removals), arms it,
        /// and deletes the rows once every one of them has landed (DESIGN 5.6 W5 comes between).
        /// </summary>
        private static void SaveThenFinish(ISalvageRollerHost host, CharacterSalvageBatch batch, WorldObject item, LandingGate gate)
        {
            var saved = gate.Expect();

            host.SaveItem(item, landed =>
            {
                if (!landed)
                    Log.Error($"{RefundPrefix} batch {batch.Id}: the delivery save of 0x{item.Guid.Full:X8} ({item.Name}) did not land. It is in {host.CharacterName}'s pack in memory, stamped; the row stays Taking and reconcile finishes it.");

                saved(landed);
            });

            gate.Arm(allLanded =>
            {
                if (!allLanded)
                {
                    Log.Warn($"[SALVAGE ROLLER] batch {batch.Id}: delivered, but a save or a Hammer's removal did not land; the rows stay until reconcile finishes them.");
                    return;
                }

                if (Crash(SalvageRollerCrashPoint.W5))
                    return;

                if (!host.DeleteBatch(batch.Id))
                    Log.Error($"{RefundPrefix} batch {batch.Id}: delivered, but the rows could not be deleted; reconcile deletes them (the stamp prevents a second apply).");
            });
        }

        /// <summary>
        /// The weapon tier the item had before result k was applied, measured on its pre-take state as the Taken audit
        /// row recorded it. For a delivery finished after the apply, when the item itself no longer shows it. None, with
        /// an error, when that row cannot be read.
        /// </summary>
        private static WeaponQualityTier TierBeforeFromTakenAudit(ISalvageRollerHost host, CharacterSalvageBatch batch)
        {
            try
            {
                if (host.TryReadAuditDetail(batch.Id, SalvageRollerAuditEvent.Taken, out var detail) && !string.IsNullOrEmpty(detail))
                {
                    var before = SalvageRollerAuditSnapshot.Deserialize(JsonNode.Parse(detail)?["before"]?["snapshot"]);

                    if (before != null)
                    {
                        before.Id = ObjectGuid.Invalid.Full;

                        var copy = WorldObjectFactory.CreateWorldObject(before);

                        if (copy != null)
                            return WeaponQualityTiers.Evaluate(copy);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"{RefundPrefix} batch {batch.Id}: the Taken row's pre-take snapshot could not be read back: {ex.Message}");
            }

            Log.Error($"{RefundPrefix} batch {batch.Id}: no readable Taken row; the tier transition runs from None.");
            return WeaponQualityTier.None;
        }

        /// <summary>
        /// A throwaway copy of the WHOLE item (every table, instance ids included), with guid ObjectGuid.Invalid so it
        /// takes nothing from the guid pool. The audit's post-take state is built on it.
        /// </summary>
        private static WorldObject FullScratchCopy(WorldObject item)
        {
            var biota = SalvageRollerAuditSnapshot.Deserialize(SalvageRollerAuditSnapshot.SerializeToNode(item));

            if (biota == null)
                throw new InvalidOperationException($"Salvage Roller: could not copy 0x{item.Guid.Full:X8} ({item.Name})");

            biota.Id = ObjectGuid.Invalid.Full;

            return WorldObjectFactory.CreateWorldObject(biota)
                ?? throw new InvalidOperationException($"Salvage Roller: could not build a scratch copy of 0x{item.Guid.Full:X8} ({item.Name})");
        }

        // =================================================================================================
        // reconcile: DESIGN 5.6
        // =================================================================================================

        internal enum ReconcileKind { NoBatch, Pending, Waiting, NotNow, Finished, Halted }

        internal readonly struct ReconcileResult
        {
            public ReconcileResult(ReconcileKind kind, CharacterSalvageBatch batch = null, string message = null)
            {
                Kind = kind;
                Batch = batch;
                Message = message;
            }

            public ReconcileKind Kind { get; }

            public CharacterSalvageBatch Batch { get; }

            public string Message { get; }
        }

        /// <summary>
        /// Brings the character's batch to a state the station can show, from whatever a crash left (DESIGN 5.6):
        ///   Escrowing (W1, W2): abort. Each item is found in the pack, in escrow, in memory or as a parentless biota,
        ///                       delivered to the pack, and the rows go once every delivery save has landed.
        ///   Pending (W3):       destroy the Hammers still held ("not held" is success) and delete their rows.
        ///   Taking (W4, W5):    finish the take; the stamp decides whether result k still needs applying.
        /// A batch whose confirmation is open in this process is left alone. Never runs without a ready escrow.
        /// </summary>
        internal static ReconcileResult Reconcile(ISalvageRollerHost host)
        {
            if (!host.TryReadBatch(out var batch))
                return new ReconcileResult(ReconcileKind.NotNow, null, StatusReadFailedMessage);

            if (batch == null)
                return new ReconcileResult(ReconcileKind.NoBatch);

            if (awaitingAnswer.TryGetValue(batch.Id, out var waiting))
            {
                if (Clock() - waiting.SentUtc < AbandonAfter)
                    return new ReconcileResult(ReconcileKind.Waiting, batch, AwaitingAnswerMessage);

                // Older than any answer can be: a logout, or a callback that never came. Abandoned, then cancelled
                // below like any other Escrowing batch; a late answer then finds no entry and is ignored.
                if (awaitingAnswer.TryRemove(batch.Id, out _))
                    Log.Warn($"[SALVAGE ROLLER] {host.CharacterName}: batch {batch.Id}'s confirmation has waited since {waiting.SentUtc:o} with no answer; abandoned.");
            }

            var store = host.GetStore();

            string notReady = null;

            if (store == null || !store.IsEscrowReady(out notReady))
                return new ReconcileResult(ReconcileKind.NotNow, batch, notReady ?? AccountVaultStore.EscrowUnavailableMessage);

            switch ((SalvageBatchState)batch.State)
            {
                case SalvageBatchState.Escrowing:
                {
                    var result = AbortBatch(host, batch, SalvageRollerAuditEvent.ReconcileAbort, "found Escrowing at reconcile: nothing was rolled or committed");

                    return result == AbortResult.Done
                        ? new ReconcileResult(ReconcileKind.Finished, batch, AbortedMessage)
                        : new ReconcileResult(ReconcileKind.NotNow, batch, AbortIncompleteMessage);
                }

                case SalvageBatchState.Pending:
                    ConsumeHammers(host, store, batch, SalvageRollerAuditEvent.ReconcileForward);
                    return new ReconcileResult(ReconcileKind.Pending, batch);

                case SalvageBatchState.Taking:
                {
                    if (!TryReadPayload(host, batch, out var payload))
                        return new ReconcileResult(ReconcileKind.NotNow, batch, NeedsAttentionMessage);

                    var candidate = payload.Find(batch.ChosenSeq ?? 0);

                    if (candidate == null)
                    {
                        SalvageRollerAuditTrail.LogRefund(host, batch, $"a Taking batch's chosen result #{batch.ChosenSeq} is not in its payload; left for staff.", null);
                        return new ReconcileResult(ReconcileKind.NotNow, batch, NeedsAttentionMessage);
                    }

                    var taken = ForwardTaking(host, store, batch, candidate, SalvageRollerAuditEvent.ReconcileForward);

                    switch (taken.Outcome)
                    {
                        case TakeOutcome.Delivered: return new ReconcileResult(ReconcileKind.Finished, batch);
                        case TakeOutcome.Halted: return new ReconcileResult(ReconcileKind.Halted, batch);
                        default: return new ReconcileResult(ReconcileKind.NotNow, batch, taken.Message);
                    }
                }

                default:
                    SalvageRollerAuditTrail.LogRefund(host, batch, $"the batch has an unknown state {batch.State}; left for staff.", null);
                    return new ReconcileResult(ReconcileKind.NotNow, batch, NeedsAttentionMessage);
            }
        }

        // =================================================================================================
        // shared steps
        // =================================================================================================

        internal enum AbortResult
        {
            Done,
            Incomplete,
            RecordFailed,
            /// <summary>A fresh read of the row did not show this batch Escrowing; nothing was touched.</summary>
            NotEscrowing,
        }

        internal enum Where
        {
            /// <summary>The shard has no biota for it: it no longer exists.</summary>
            Missing,
            Pack,
            Escrow,
            Stranded,
            /// <summary>A loose shard biota, rehydrated.</summary>
            Shard,
            /// <summary>A shard biota something else holds (a container, a wielder, a landblock); not rehydrated.</summary>
            Held,
            /// <summary>The shard could not be read, so nothing is known.</summary>
            Unreadable,
        }

        private static WorldObject Locate(ISalvageRollerHost host, AccountVaultStore store, uint guid, out Where where) =>
            Locate(host, store, guid, out where, out _, out _);

        /// <summary>
        /// The batch item with this guid, wherever it is: the pack (equipped included), escrow, memory, then the shard.
        /// Only a LOOSE shard biota (see <see cref="IsLoose"/>) is rehydrated; one something else holds comes back as
        /// <see cref="Where.Held"/> with no object, its container in <paramref name="heldIn"/> and what holds it in
        /// <paramref name="holder"/>, because loading it would make a second live copy. Logs nothing: whether a held
        /// biota is expected is the caller's call.
        /// </summary>
        private static WorldObject Locate(ISalvageRollerHost host, AccountVaultStore store, uint guid, out Where where, out uint? heldIn, out string holder)
        {
            heldIn = null;
            holder = null;

            var inPack = host.FindInPack(guid);

            if (inPack != null)
            {
                where = Where.Pack;
                return inPack;
            }

            var escrowed = store.FindEscrowed(guid);

            if (escrowed != null)
            {
                where = Where.Escrow;
                return escrowed;
            }

            if (stranded.TryGetValue(guid, out var held))
            {
                where = Where.Stranded;
                return held;
            }

            switch (host.LookUpShard(guid, out var loaded, out heldIn, out holder))
            {
                case ShardBiotaState.Loose when loaded != null && IsLoose(loaded, out holder):
                    where = Where.Shard;
                    return loaded;

                case ShardBiotaState.Loose:
                    // a host that reported a held biota as loose: treated as held, never used as a second copy
                    heldIn = loaded?.ContainerId;
                    holder ??= "the shard reported it loose without an object";
                    where = Where.Held;
                    return null;

                case ShardBiotaState.Held:
                    where = Where.Held;
                    return null;

                case ShardBiotaState.Absent:
                    where = Where.Missing;
                    return null;

                default:
                    where = Where.Unreadable;
                    return null;
            }
        }
        /// <summary>
        /// Cancels a batch that was never committed (Escrowing). Works from a FRESH read of the row and refuses, touching
        /// nothing, unless that read shows this batch Escrowing: a caller's copy may be stale, and cancelling a committed
        /// batch would hand back the Hammers of a roll already made.
        ///
        /// The audit row first, then every item home to the pack. Each item's OWN row is deleted in that item's own
        /// save-landed callback, so an item that is home is never still claimed by the batch; the batch row goes only once
        /// every item is home and every save has landed. An item that does not fit stays where it is (escrow, or held in
        /// memory) with its row, and the next station Use cancels the rest. A missing item keeps its row for staff.
        /// </summary>
        internal static AbortResult AbortBatch(ISalvageRollerHost host, CharacterSalvageBatch hint, SalvageRollerAuditEvent ev, string why)
        {
            var store = host.GetStore();

            string notReady = null;

            if (store == null || !store.IsEscrowReady(out notReady))
            {
                Log.Warn($"[SALVAGE ROLLER] batch {hint.Id}: cannot cancel yet ({notReady ?? "no store"}): {why}. Reconcile will.");
                return AbortResult.Incomplete;
            }

            if (!host.TryReadBatch(out var batch))
            {
                Log.Warn($"[SALVAGE ROLLER] batch {hint.Id}: cannot cancel yet (the row could not be read): {why}. Reconcile will.");
                return AbortResult.Incomplete;
            }

            if (batch == null || batch.Id != hint.Id || batch.State != (byte)SalvageBatchState.Escrowing)
            {
                SalvageRollerAuditTrail.LogRefund(host, hint, $"refused to cancel batch {hint.Id} ({why}): a fresh read shows {(batch == null ? "no batch" : batch.Id != hint.Id ? $"batch {batch.Id}" : ((SalvageBatchState)batch.State).ToString())}, not this batch Escrowing. Nothing was moved.", null);
                return AbortResult.NotEscrowing;
            }

            var located = new List<(CharacterSalvageBatchItem Row, WorldObject Item, Where Where, string Holder)>();

            foreach (var row in batch.Items.ToList())
                located.Add((row, Locate(host, store, row.Guid, out var where, out _, out var holder), where, holder));

            var detail = new JsonObject
            {
                ["why"] = why,
                ["items"] = new JsonArray(located.Select(l => (JsonNode)(l.Item != null
                    ? SalvageRollerAuditTrail.ItemJson(l.Item, l.Where.ToString())
                    : new JsonObject { ["guid"] = l.Row.Guid, ["hex"] = $"0x{l.Row.Guid:X8}", ["role"] = ((SalvageBatchItemRole)l.Row.Role).ToString(), ["where"] = l.Where == Where.Missing ? "MISSING" : l.Where.ToString(), ["holder"] = l.Holder })).ToArray()),
                ["moves"] = SalvageRollerAuditTrail.Moves($"{(SalvageBatchState)batch.State}; items as listed", "every item delivered to the character's pack, each item's row deleted once its save lands; the batch row once all have"),
            };

            if (!SalvageRollerAuditTrail.Write(host, ev, batch, batch.ItemGuid, detail))
                return AbortResult.RecordFailed;

            var gate = new LandingGate();
            var allHome = true;

            foreach (var (row, found, where, holder) in located)
            {
                if (found == null)
                {
                    SalvageRollerAuditTrail.LogRefund(host, batch, $"cancelling: 0x{row.Guid:X8} ({(SalvageBatchItemRole)row.Role}) is not in the pack, the escrow or memory, and the shard says {Describe(where, holder)}; its row stays for staff.", null);
                    allHome = false;
                    continue;
                }

                var item = found;

                if (where == Where.Pack)
                {
                    host.SaveItem(item, RowGoesWhenSaved(host, batch, row.Guid, gate.Expect()));
                    continue;
                }

                if (where == Where.Escrow)
                {
                    WorldObject released = null;
                    string relWhy = null;
                    var ok = false;

                    store.Enqueue(() => ok = store.TryReleaseEscrow(row.Guid, out released, out relWhy), out var thrown);

                    if (!ok || released == null || thrown != null)
                    {
                        Log.Error($"{RefundPrefix} batch {batch.Id}: cancelling, 0x{row.Guid:X8} could not leave escrow ({relWhy ?? thrown?.Message}); it stays there.");
                        allHome = false;
                        continue;
                    }

                    item = released;
                }

                if (host.TryPutInPack(item))
                {
                    stranded.TryRemove(item.Guid.Full, out _);
                    host.SaveItem(item, RowGoesWhenSaved(host, batch, row.Guid, gate.Expect()));
                    continue;
                }

                allHome = false;

                if (where == Where.Escrow)
                {
                    var back = false;
                    string backWhy = null;
                    store.Enqueue(() => back = store.TryReturnToEscrow(item, out backWhy), out var thrown);

                    if (back && thrown == null)
                        continue;

                    Log.Error($"{RefundPrefix} batch {batch.Id}: cancelling, 0x{item.Guid.Full:X8} ({item.Name}) fitted neither the pack nor back into escrow ({backWhy ?? thrown?.Message}); held in memory, its row kept.");
                }

                stranded[item.Guid.Full] = item;
            }

            var home = allHome;

            gate.Arm(allLanded =>
            {
                if (!home)
                    return;     // the delivered items' rows went in their own callbacks; the rest wait for the next Use

                if (!allLanded)
                {
                    Log.Error($"{RefundPrefix} batch {batch.Id}: cancelled, but a delivery save did not land; that item's row stays and reconcile re-saves it.");
                    return;
                }

                if (!host.DeleteBatch(batch.Id))
                    Log.Error($"{RefundPrefix} batch {batch.Id}: cancelled and delivered, but the batch row could not be deleted; reconcile deletes it.");
            });

            return allHome ? AbortResult.Done : AbortResult.Incomplete;
        }

        /// <summary>A save callback that deletes the item's own batch row once the save has landed, then reports to <paramref name="done"/>.</summary>
        private static Action<bool> RowGoesWhenSaved(ISalvageRollerHost host, CharacterSalvageBatch batch, uint guid, Action<bool> done)
        {
            return landed =>
            {
                if (landed)
                {
                    if (!host.DeleteBatchItem(guid))
                        Log.Error($"{RefundPrefix} batch {batch.Id}: 0x{guid:X8} is home and saved, but its row could not be deleted; the next station Use deletes it.");
                }
                else
                {
                    Log.Error($"{RefundPrefix} batch {batch.Id}: the save that brings 0x{guid:X8} home did not land; its row stays.");
                }

                done?.Invoke(landed);
            };
        }

        /// <summary>
        /// Destroys every Hammer the batch still lists and deletes each one's row in that Hammer's own removal-landed
        /// callback (W3 roll-forward, and the take). The audit row goes first. A Hammer in escrow is destroyed there; one
        /// found anywhere else - the pack, memory, a loose shard biota - has THAT copy destroyed, because a Hammer that
        /// still exists is not consumed and counting it would be a free roll. A Hammer whose biota the shard still records
        /// in this account's escrow, with no live copy in reach (its removal is queued, or failed), is removed from the
        /// shard by guid and its row goes in that removal's callback. Only a Hammer the shard has NO biota for counts as
        /// already consumed.
        ///
        /// <paramref name="gate"/>, when given, hears every removal, and is failed outright when the Hammers could not be
        /// consumed at all, so a caller deleting the batch row never does it over a Hammer still standing.
        /// </summary>
        private static void ConsumeHammers(ISalvageRollerHost host, AccountVaultStore store, CharacterSalvageBatch batch, SalvageRollerAuditEvent ev, LandingGate gate = null)
        {
            var rows = batch.Items.Where(i => i.Role == (byte)SalvageBatchItemRole.Hammer).ToList();

            if (rows.Count == 0)
                return;

            if (!store.IsEscrowReady(out var notReady))
            {
                Log.Warn($"[SALVAGE ROLLER] batch {batch.Id}: the Hammers wait for a ready escrow ({notReady}).");
                gate?.Fail();
                return;
            }

            var located = new List<(CharacterSalvageBatchItem Row, WorldObject Item, Where Where, uint? HeldIn, string Holder)>();

            foreach (var row in rows)
                located.Add((row, Locate(host, store, row.Guid, out var where, out var heldIn, out var holder), where, heldIn, holder));

            var detail = new JsonObject
            {
                ["hammers"] = new JsonArray(located.Where(l => l.Item != null).Select(l => (JsonNode)SalvageRollerAuditTrail.ItemJson(l.Item, l.Where.ToString())).ToArray()),
                ["alreadyGone"] = new JsonArray(located.Where(l => l.Where == Where.Missing).Select(l => (JsonNode)$"0x{l.Row.Guid:X8}").ToArray()),
                ["stillInShard"] = new JsonArray(located.Where(l => l.Item == null && l.Where != Where.Missing).Select(l => (JsonNode)new JsonObject { ["hex"] = $"0x{l.Row.Guid:X8}", ["where"] = l.Where.ToString(), ["container"] = l.HeldIn, ["holder"] = l.Holder }).ToArray()),
                ["moves"] = SalvageRollerAuditTrail.Moves($"{(SalvageBatchState)batch.State}; {rows.Count} Hammer row(s)", "every Hammer found destroyed where it was found, and its row deleted once its removal lands"),
            };

            if (!SalvageRollerAuditTrail.Write(host, ev, batch, batch.ItemGuid, detail))
            {
                gate?.Fail();
                return;
            }

            foreach (var (row, item, where, heldIn, holder) in located)
            {
                var guid = row.Guid;
                var removed = Once(RowGoesWhenSaved(host, batch, guid, gate?.Expect()));

                switch (where)
                {
                    case Where.Missing:
                        // the shard has no biota: consumed before a crash, or its removal has landed
                        removed(true);
                        break;

                    case Where.Held when heldIn.HasValue && store.IsEscrowContainer(heldIn.Value):
                        // Still recorded in this account's escrow with no live copy in reach: its in-memory copy was
                        // destroyed and the removal is still queued (a fast second Use), or that removal failed. Removing
                        // the biota is queued behind it in order and reports success when the row is already gone; the
                        // Hammer's row goes only in that callback. Expected, so no refund line.
                        host.RemoveShardBiota(guid, removed);
                        break;

                    case Where.Held:
                        SalvageRollerAuditTrail.LogRefund(host, batch, $"Hammer 0x{guid:X8} is not in this account's escrow but the shard says it is held ({holder}); it was not consumed and its row stays for staff.", null);
                        removed(false);
                        break;

                    case Where.Unreadable:
                        Log.Error($"{RefundPrefix} batch {batch.Id}: Hammer 0x{guid:X8} could not be read from the shard; its row stays for reconcile.");
                        removed(false);
                        break;

                    case Where.Escrow:
                        var queued = store.Enqueue(() =>
                        {
                            if (store.TryDestroyEscrowed(guid, out var why, removed))
                                return;

                            if (why == AccountVaultStore.EscrowItemGoneMessage)
                            {
                                // gone from memory between the lookup and now: the shard decides, in queue order
                                host.RemoveShardBiota(guid, removed);
                                return;
                            }

                            Log.Error($"{RefundPrefix} batch {batch.Id}: Hammer 0x{guid:X8} could not be destroyed ({why}); its row stays for reconcile.");
                            removed(false);
                        }, out var thrown);

                        if (!queued || thrown != null)
                        {
                            Log.Error($"{RefundPrefix} batch {batch.Id}: destroying Hammer 0x{guid:X8} {(thrown != null ? $"threw: {thrown.Message}" : "was refused by the store")}");
                            removed(false);
                        }
                        break;

                    default:
                        stranded.TryRemove(guid, out _);

                        try
                        {
                            host.DestroyItem(item, where == Where.Pack, removed);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"{RefundPrefix} batch {batch.Id}: destroying Hammer 0x{guid:X8} ({where}) threw: {ex.Message}");
                            removed(false);
                        }
                        break;
                }
            }
        }

        /// <summary>What the shard said about an item that is in neither the pack, the escrow nor memory, for a refund line.</summary>
        private static string Describe(Where where, string holder)
        {
            switch (where)
            {
                case Where.Missing: return "there is no such biota";
                case Where.Held: return $"it is held elsewhere ({holder})";
                case Where.Unreadable: return "it could not be read";
                default: return where.ToString();
            }
        }

        /// <summary>A callback that runs at most once, whichever path reaches it first.</summary>
        private static Action<bool> Once(Action<bool> inner)
        {
            var fired = 0;

            return landed =>
            {
                if (Interlocked.Exchange(ref fired, 1) == 0)
                    inner(landed);
            };
        }
        private static bool TryReadPayload(ISalvageRollerHost host, CharacterSalvageBatch batch, out SalvageRollerPayload payload)
        {
            if (!SalvageRollerPayload.TryRead(batch.Payload, out payload, out var error))
            {
                SalvageRollerAuditTrail.LogRefund(host, batch, $"the batch payload cannot be read ({error}); left for staff.", null);
                return false;
            }

            if (payload.Candidates.Count != batch.RollCount)
            {
                SalvageRollerAuditTrail.LogRefund(host, batch, $"the payload holds {payload.Candidates.Count} candidates but roll_count is {batch.RollCount}; left for staff.", null);
                payload = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Counts outstanding saves and runs a finisher once, when the last one lands and the caller has armed it.
        /// Save callbacks may run on the database thread, so the count is interlocked.
        /// </summary>
        private sealed class LandingGate
        {
            private int outstanding = 1;     // the arm token
            private int failures;
            private Action<bool> finisher;

            public Action<bool> Expect()
            {
                Interlocked.Increment(ref outstanding);

                return landed =>
                {
                    if (!landed)
                        Interlocked.Increment(ref failures);

                    Release();
                };
            }

            /// <summary>Counts a failure that has no callback of its own (work that could not even be started).</summary>
            public void Fail() => Interlocked.Increment(ref failures);

            public void Arm(Action<bool> onAllLanded)
            {
                finisher = onAllLanded;
                Release();
            }

            private void Release()
            {
                if (Interlocked.Decrement(ref outstanding) == 0)
                    finisher?.Invoke(Volatile.Read(ref failures) == 0);
            }
        }

        // =================================================================================================
        // the rows on screen: DESIGN 5.4 step 6
        // =================================================================================================

        /// <summary>Fills one viewer's view from a Pending batch: one display object per candidate, sorted. Returns the row count.</summary>
        internal static int ShowRows(SalvageRollerVendor station, uint viewerGuid, CharacterSalvageBatch batch, WorldObject escrowedTarget)
        {
            if (station == null || batch == null || escrowedTarget == null)
                return 0;

            if (!SalvageRollerPayload.TryRead(batch.Payload, out var payload, out var error) || payload.Candidates.Count != batch.RollCount)
            {
                Log.Error($"{RefundPrefix} batch {batch.Id}: the payload cannot be shown ({error ?? "candidate count differs from roll_count"}).");
                return 0;
            }

            var action = (SalvageRollerAction)batch.Action;
            var original = DuoGemSnapshot.Capture(escrowedTarget);

            return station.SetRows(viewerGuid, SortForDisplay(action, payload.Candidates), c => MaterializeDisplay(original, c, action));
        }

        /// <summary>
        /// Weapons: tier, then summed intensity, best first, then roll order. Obsidian: summed mod fractions, best first,
        /// then roll order. Green Jade: leading-type count (Summary.Tier), then second-highest count (Summary.Total),
        /// best first, then roll order. Serpentine: roll order.
        /// </summary>
        internal static List<SalvageRollerCandidate> SortForDisplay(SalvageRollerAction action, IEnumerable<SalvageRollerCandidate> candidates)
        {
            var list = candidates.Where(c => c != null).ToList();

            if (IsWeapon(action) || action == SalvageRollerAction.GreenJadeSwap)
                return list.OrderByDescending(c => c.Summary?.Tier ?? 0).ThenByDescending(c => c.Summary?.Total ?? 0).ThenBy(c => c.Seq).ToList();

            if (action == SalvageRollerAction.ObsidianConvert || action == SalvageRollerAction.ObsidianReroll)
                return list.OrderByDescending(c => c.Summary?.Total ?? 0).ThenBy(c => c.Seq).ToList();

            return list.OrderBy(c => c.Seq).ToList();
        }

        /// <summary>
        /// One display row: the escrowed target's snapshot plus the candidate's result, built through the WEENIE
        /// constructor with a fresh guid. Never from a shard biota: the biota constructor marks an object as having come
        /// from the database, and SalvageRollerVendor.SetRows refuses such an object (a display must be disposable).
        /// </summary>
        internal static WorldObject MaterializeDisplay(DuoGemSnapshot original, SalvageRollerCandidate candidate, SalvageRollerAction action)
        {
            var display = WorldObjectFactory.CreateWorldObject(WeenieOf(original), DisplayGuid());

            if (display == null)
                return null;

            SalvageRollerEngine.ApplyDiff(display, candidate.Diff);

            var name = display.Name;

            if (action == SalvageRollerAction.GreenJadeSwap)
                display.Name = $"{name} (#{candidate.Seq}, {AspectLeadSuffix(JewelryAspectMath.ReadCounts(display))})";
            else
                display.Name = IsWeapon(action)
                    ? $"{name} (#{candidate.Seq}, {(int)Math.Round(candidate.Summary?.Total ?? 0)}%)"
                    : $"{name} (#{candidate.Seq})";

            return display;
        }

        /// <summary>
        /// The leading-type part of a Green Jade row name: the type with the highest count and that count, e.g.
        /// "Defense 8". Tied leaders are all named, in Attack, Defense, Utility order: two-way "Attack 5 Defense 5",
        /// three-way "Attack 3 Defense 3 Utility 3" (reachable mid-fill at 3/3/3). A piece with no aspects (not
        /// reachable: eligibility refuses it, and a step only adds) reads "no aspects". The type words are what
        /// /salvageroller search defense matches.
        /// </summary>
        internal static string AspectLeadSuffix(AspectCounts counts)
        {
            var top = Math.Max(counts.Attack, Math.Max(counts.Defense, counts.Utility));

            if (top <= 0)
                return "no aspects";

            var parts = new List<string>();

            foreach (var type in new[] { AspectType.Attack, AspectType.Defense, AspectType.Utility })
            {
                if (counts[type] == top)
                    parts.Add($"{type} {top}");
            }

            return string.Join(" ", parts);
        }

        /// <summary>A Weenie carrying the snapshot's tables, for the non-database constructor path.</summary>
        internal static Weenie WeenieOf(DuoGemSnapshot snapshot)
        {
            var item = snapshot.Item;

            return new Weenie
            {
                WeenieClassId = item.WeenieClassId,
                WeenieType = (WeenieType)item.WeenieType,
                ClassName = $"salvage-roller-display-{item.WeenieClassId}",
                PropertiesInt = item.Ints.ToDictionary(k => (PropertyInt)k.Key, k => k.Value),
                PropertiesInt64 = item.Int64s.ToDictionary(k => (PropertyInt64)k.Key, k => k.Value),
                PropertiesBool = item.Bools.ToDictionary(k => (PropertyBool)k.Key, k => k.Value),
                PropertiesFloat = item.Floats.ToDictionary(k => (PropertyFloat)k.Key, k => k.Value),
                PropertiesString = item.Strings.ToDictionary(k => (PropertyString)k.Key, k => k.Value),
                PropertiesDID = item.DataIds.ToDictionary(k => (PropertyDataId)k.Key, k => k.Value),
                PropertiesIID = new Dictionary<PropertyInstanceId, uint>(),
                PropertiesSpellBook = new Dictionary<int, float>(item.SpellBook),
                PropertiesAnimPart = item.AnimParts.Select(p => new PropertiesAnimPart { Index = p.Index, AnimationId = p.AnimationId }).ToList(),
                PropertiesPalette = item.Palettes.Select(p => new PropertiesPalette { SubPaletteId = p.SubPaletteId, Offset = p.Offset, Length = p.Length }).ToList(),
                PropertiesTextureMap = item.TextureMaps.Select(t => new PropertiesTextureMap { PartIndex = t.PartIndex, OldTexture = t.OldTexture, NewTexture = t.NewTexture }).ToList(),
            };
        }

        // =================================================================================================
        // login: DESIGN 5.6 (a reminder only; reconcile runs at the station)
        // =================================================================================================

        /// <summary>One chat line at login when the character has a batch row. Never moves anything.</summary>
        public static void OnLogin(Player player)
        {
            if (player == null)
                return;

            try
            {
                if (new PlayerSalvageRollerHost(player).TryReadBatch(out var batch) && batch != null)
                    Chat(player, DescribeStatus(batch));
            }
            catch (Exception ex)
            {
                Log.Error($"{RefundPrefix} the login reminder for {player.Name} (0x{player.Guid.Full:X8}) threw: {ex.Message}");
            }
        }
    }
}
