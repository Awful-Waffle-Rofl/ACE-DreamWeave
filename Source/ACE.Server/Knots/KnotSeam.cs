using System;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Templates;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Knots
{
    /// <summary>
    /// Thin glue between Gem and the pure decisions: reads state, calls <see cref="PatternFill"/> /
    /// <see cref="KnotUse"/>, applies the result. Every refusal consumes nothing.
    /// </summary>
    public static class KnotSeam
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Pattern used on a target: a Fiber from the pack goes into the Pattern, which keeps the running average of the
        /// Fiber levels. The caller sends use-done afterwards on every exit.
        /// </summary>
        public static void UsePatternOnTarget(Player player, WorldObject pattern, WorldObject target)
        {
            if (player == null || pattern == null || target == null)
                return;

            var inPack = player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) != null;
            var store = ThreadDungeonManager.Store;
            var def = store.GetPinnacle(pattern.GetProperty(PropertyString.PinnacleKey));

            var decision = PatternFill.Decide(inPack, KnotItems.IsFiber(target), target.GetProperty(PropertyInt.KnotLevel) ?? 0,
                pattern.GetProperty(PropertyInt.KnotFiberCount) ?? 0, pattern.GetProperty(PropertyInt.KnotFiberLevelSum) ?? 0, def,
                KnotPinnacles.IsAvailable(def, KnotPinnacles.EncounterRegistered));

            if (!decision.Accepted)
            {
                Say(player, decision.Refusal);
                return;
            }

            if (decision.TakesFiber)
            {
                // Consume the Fiber first. If that fails nothing has changed (no dupe, no half fill); the Pattern is as it was.
                if (!player.TryConsumeFromInventoryWithNetworking(target, 1))
                {
                    log.Error($"[KNOTS] Fiber 0x{target.Guid.Full:X8} could not be consumed for {player.Name}; Pattern 0x{pattern.Guid.Full:X8} left unchanged");
                    Say(player, KnotItems.KnotEncounterRefused);
                    return;
                }

                player.UpdateProperty(pattern, PropertyInt.KnotFiberCount, decision.NewCount);
                player.UpdateProperty(pattern, PropertyInt.KnotFiberLevelSum, decision.NewSum);
                player.UpdateProperty(pattern, PropertyInt.KnotLevel, decision.NewAverage);
                player.UpdateProperty(pattern, PropertyString.Name, KnotItems.PatternName(def.Name, decision.NewCount, decision.NewAverage));
                player.EnqueueBroadcast(new GameMessageUpdateObject(pattern));

                if (!decision.Completes)
                {
                    Say(player, KnotItems.PatternFiberAdded(decision.NewCount, decision.NewAverage));
                    return;
                }
            }

            TryCompletePattern(player, pattern, def);
        }

        /// <summary>
        /// A Pattern double-clicked with no target. True when it was a full Pattern (the retry after making room for its
        /// Knot), in which case the completion has been tried and said its line; false when the Pattern is not full yet and
        /// the caller refuses with <see cref="KnotItems.PatternNeedsTarget"/>. Either way nothing is consumed unless the
        /// Pattern turns into its Knot.
        /// </summary>
        public static bool TryCompleteFullPattern(Player player, WorldObject pattern)
        {
            if (player == null || pattern == null || (pattern.GetProperty(PropertyInt.KnotFiberCount) ?? 0) < KnotItems.FibersPerKnot)
                return false;

            var def = ThreadDungeonManager.Store.GetPinnacle(pattern.GetProperty(PropertyString.PinnacleKey));
            var refusal = PatternFill.CompletionRefusal(def, KnotPinnacles.IsAvailable(def, KnotPinnacles.EncounterRegistered));

            if (refusal != null)
            {
                Say(player, refusal);
                return true;
            }

            TryCompletePattern(player, pattern, def);
            return true;
        }

        /// <summary>
        /// A full Pattern becomes a Knot of its pinnacle: place an unbound Knot at the Pattern's level, bind it, then
        /// consume the Pattern. No room leaves the Pattern full to be used again. If the Pattern cannot be consumed
        /// after the Knot was placed, the Knot is taken back out (no dupe) and the Pattern stays full.
        /// </summary>
        internal static void TryCompletePattern(Player player, WorldObject pattern, PinnacleDef def)
        {
            var level = pattern.GetProperty(PropertyInt.KnotLevel) ?? 0;

            if (!KnotWeaving.TryPlaceKnot(player, level, out var knot))
            {
                Say(player, KnotItems.PatternNoRoom);
                return;
            }

            Bind(player, knot, def);

            if (!player.TryConsumeFromInventoryWithNetworking(pattern, 1))
            {
                log.Error($"[KNOTS] Pattern 0x{pattern.Guid.Full:X8} could not be consumed for {player.Name}; Knot 0x{knot.Guid.Full:X8} is being taken back");

                if (!player.TryConsumeFromInventoryWithNetworking(knot))
                    log.Error($"[KNOTS] Knot 0x{knot.Guid.Full:X8} could not be taken back from {player.Name} after the Pattern consume failed; a Game Master must remove one of the two");

                Say(player, KnotItems.KnotEncounterRefused);
                return;
            }

            log.Info($"[KNOTS] {player.Name} (0x{player.Guid.Full:X8}) completed Pattern 0x{pattern.Guid.Full:X8} into Knot 0x{knot.Guid.Full:X8} ({def.Key}, Level {level})");
            KnotTelemetry.RecordWeave(player, knot, level, KnotTelemetry.Result.Ok, KnotTelemetry.WeaveReasonPattern, null);
            KnotTelemetry.RecordBind(player, knot, def, pattern);

            Say(player, KnotItems.PatternCompleted(def.Name, level));
        }

        /// <summary>Knot double-click. Returns true on every path so the generic consume never fires.</summary>
        public static bool TryHandleUse(WorldObject knot, Player player)
        {
            return RunUse(knot, player, false);
        }

        /// <summary>
        /// The whole use. <paramref name="confirmed"/> is true only on the Yes answer of <see cref="Confirmation_KnotUse"/>
        /// (via <see cref="OnUseConfirmed"/>): the use is then run again from scratch (every gate and decision below
        /// re-reads live state) and the start question is not asked a second time. Private so no other caller can skip it.
        /// </summary>
        private static bool RunUse(WorldObject knot, Player player, bool confirmed)
        {
            if (!KnotItems.IsKnot(knot))
                return false;

            // Bookkeeping an encounter held while this owner was offline (a refund, a loss, a kill) lands before anything
            // is read from the Knot, so the decision below sees the true entry count. A held loss or kill may consume
            // this very Knot: the use then ends with the ordinary spent line.
            if (KnotPendingEvents.Apply(player) > 0 && player.FindObject(knot.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                Say(player, KnotItems.KnotSpent);
                return true;
            }

            var gate = KnotUse.Gate(player.CombatMode != CombatMode.NonCombat, player.PKTimerActive, player.Location.IsEphemeralRealm);
            switch (gate)
            {
                case KnotGate.InCombat: Say(player, KnotItems.KnotInCombat(knot.Name)); return true;
                case KnotGate.PkTimer: player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouHaveBeenInPKBattleTooRecently)); return true;
                case KnotGate.InsideInstance: Say(player, KnotItems.KnotInsideInstance); return true;
            }

            var store = ThreadDungeonManager.Store;
            var key = knot.GetProperty(PropertyString.PinnacleKey);

            var boundDef = store.GetPinnacle(key);
            var bound = KnotState.IsBound(key);
            IPinnacleEncounter resumeEncounter = null;
            var resume = bound && PinnacleEncounters.TryGet(boundDef?.Destination?.Kind, out resumeEncounter)
                ? resumeEncounter.ResumableAttempt(player, knot)
                : KnotResume.None;

            var decision = KnotUse.Decide(key, knot.GetProperty(PropertyInt.Structure), boundDef, store.EnabledPinnacles,
                n => ThreadSafeRandom.Next(0, n - 1), kind => PinnacleEncounters.TryGet(kind, out _), resume: resume);

            if (!decision.Allowed)
            {
                // A bound Knot with no entries is dead weight: either nothing is live on it (the owner was offline when
                // the attempt ended, so OnAttemptLost could not act), or a held attempt cannot be paid for (owner
                // ruling R1: the attempt is lost). Either way it is cleaned up here; an attempt it forfeits is ended
                // first, through the encounter's own loss path. A won attempt (its reward owed or being collected) never lands here
                // (Decide refuses it without NoEntries): that Knot is kept.
                if (bound && decision.NoEntries)
                {
                    if (decision.ForfeitsAttempt)
                        resumeEncounter?.ForfeitAttempt(player, knot);

                    player.TryConsumeFromInventoryWithNetworking(knot);
                    KnotTelemetry.RecordKnotSpent(player, knot, decision.ForfeitsAttempt ? KnotTelemetry.Result.Forfeit : KnotTelemetry.Result.NoEntries);
                    Say(player, decision.ForfeitsAttempt ? decision.Refusal : KnotItems.KnotSpent);
                    return true;
                }

                Say(player, decision.Refusal);
                return true;
            }

            if (!PinnacleEncounters.TryGet(decision.Def.Destination.Kind, out var encounter))
            {
                Say(player, KnotItems.NoEncounterRegistered);
                return true;
            }

            // A Knot always opens a solo copy, so a fresh start has no group offer to confirm it: ask here, before
            // TryEnter, which is the first thing that changes anything (the run and its clock start inside it).
            // Nothing has been spent, bound or consumed yet, so No, a timeout and a late answer leave the Knot as it was.
            if (ShouldAskToStart(resume, confirmed, ACE.Server.Pinnacles.PinnacleTunables.Enabled))
            {
                AskToStart(knot, player);
                return true;
            }

            // Read before TryEnter: an encounter that charges itself spends a paid return INSIDE TryEnter, and the line
            // below reports what actually happened to the count, not what the decision expected.
            var entriesBefore = knot.GetProperty(PropertyInt.Structure) ?? knot.GetProperty(PropertyInt.MaxStructure) ?? 0;

            if (!encounter.TryEnter(player, knot, decision.Def, knot.GetProperty(PropertyInt.KnotLevel) ?? 0, knot.GetProperty(PropertyInt.KnotPhase) ?? 0, out var refusal))
            {
                Say(player, string.IsNullOrEmpty(refusal) ? KnotItems.KnotEncounterRefused : refusal);
                return true;
            }

            // Admitted: only now does anything change on the Knot. An unbound Knot takes its random pinnacle
            // (and that pinnacle entry count) here rather than before the attempt, so a refused entry does not
            // fix the choice.
            if (decision.NeedsBind)
            {
                Bind(player, knot, decision.Def);
                KnotTelemetry.RecordBind(player, knot, decision.Def, null);
                Say(player, KnotItems.KnotBoundRandomly(decision.Def.Name));
            }

            // An encounter that charges at its own point (SeamChargesEntry false) spends through
            // KnotEncounterEvents.SpendEntry (its first entry at its charge point, every paid return inside TryEnter)
            // and keeps the Knot until the attempt ends.
            if (!encounter.SeamChargesEntry)
            {
                var entriesAfter = knot.GetProperty(PropertyInt.Structure) ?? entriesBefore;
                Say(player, KnotItems.EncounterEntryLine(resume, entriesAfter < entriesBefore, entriesAfter));
                return true;
            }

            var left = KnotState.EntriesAfterSpend(decision.Entries);
            if (left <= 0)
            {
                player.TryConsumeFromInventoryWithNetworking(knot);
                KnotTelemetry.RecordEntry(player, knot, left, KnotTelemetry.Result.Seam);
                KnotTelemetry.RecordKnotSpent(player, knot, KnotTelemetry.Result.LastEntry, left);
                Say(player, KnotItems.KnotSpent);
            }
            else
            {
                player.UpdateProperty(knot, PropertyInt.Structure, left);
                KnotTelemetry.RecordEntry(player, knot, left, KnotTelemetry.Result.Seam);
                Say(player, KnotItems.KnotEntryUsed(left));
            }

            return true;
        }

        /// <summary>
        /// True when a use must be confirmed before it starts a run: a fresh start (no live attempt on the Knot) that
        /// has not been confirmed yet, while pinnacles are enabled (disabled, a Yes could only say so). A return to a live attempt (Paid, Owed, Free) starts no new run and is not asked.
        /// </summary>
        internal static bool ShouldAskToStart(KnotResume resume, bool confirmed, bool pinnaclesEnabled)
            => !confirmed && resume == KnotResume.None && pinnaclesEnabled;

        private static void AskToStart(WorldObject knot, Player player)
        {
            // The player has one Yes_No slot (ConfirmationManager refuses a second of the same type), so two rapid
            // uses cannot queue two questions: the second finds the slot taken, is told so, and starts nothing.
            if (!player.ConfirmationManager.EnqueueSend(new Confirmation_KnotUse(player.Guid, knot.Guid.Full, DateTime.UtcNow), KnotItems.UsePrompt(knot.Name)))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        /// <summary>
        /// The Yes answer, on the owner's own queue. The dialog can stand for up to 30 s, so the Knot is re-found in the
        /// pack and the busy refusals Gem.ActOnUse gives are repeated; <see cref="RunUse"/>
        /// then re-validates everything else (combat, PK timer, instance, entries, pinnacle) and runs the unchanged start.
        /// </summary>
        internal static void OnUseConfirmed(Player player, uint knotGuid)
        {
            if (player == null || player.IsDead)
                return;

            var knot = player.FindObject(knotGuid, Player.SearchLocations.MyInventory);

            if (knot == null || !KnotItems.IsKnot(knot))
            {
                Say(player, KnotItems.KnotGoneAtAnswer);
                return;
            }

            // Everything between the double-click and Gem.ActOnUse, repeated: the original use passed these before the
            // dialog was sent, and any of them can have changed while it stood. Player_Use.HandleActionUseItem gates:
            // PKLogout, the trade window, the PvP template gate; WorldObject.OnActivate: Active, CheckUseRequirements;
            // Gem.ActOnUse: busy, jumping. (The later gates of the use itself are re-run by RunUse.)
            if (player.PKLogout)
            {
                player.SendWeenieError(WeenieError.YouHaveBeenInPKBattleTooRecently);
                return;
            }

            if (player.IsTrading && knot.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow))
            {
                player.SendWeenieError(WeenieError.TradeItemBeingTraded);
                return;
            }

            var templateRefusal = player.PvpTemplateBlocked(PvpTemplateAction.Use, knot);
            if (templateRefusal != null)
            {
                player.SendPvpTemplateRefusal(templateRefusal);
                return;
            }

            if (!knot.Active)
                return;

            var requirements = knot.CheckUseRequirements(player);
            if (!requirements.Success)
            {
                if (requirements.Message != null)
                    player.Session.Network.EnqueueSend(requirements.Message);
                return;
            }

            if (player.IsBusy || player.Teleporting || player.suicideInProgress)
            {
                player.SendWeenieError(WeenieError.YoureTooBusy);
                return;
            }

            if (player.IsJumping)
            {
                player.SendWeenieError(WeenieError.YouCantDoThatWhileInTheAir);
                return;
            }

            RunUse(knot, player, true);
        }

        private static void Bind(Player player, WorldObject knot, PinnacleDef def)
        {
            var level = knot.GetProperty(PropertyInt.KnotLevel) ?? 0;

            player.UpdateProperty(knot, PropertyString.PinnacleKey, def.Key);
            player.UpdateProperty(knot, PropertyString.Name, KnotItems.BoundKnotName(def.Name, level));
            player.UpdateProperty(knot, PropertyInt.MaxStructure, def.Entries);
            player.UpdateProperty(knot, PropertyInt.Structure, def.Entries);
            player.EnqueueBroadcast(new GameMessageUpdateObject(knot));
        }

        private static void Say(Player player, string message)
            => player.Session?.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
    }

    /// <summary>
    /// The Yes/No asked before a Knot use starts a fresh run. Modelled on
    /// <see cref="ACE.Server.ThreadDungeons.Confirmation_ThreadGroupStart"/> and for the same reasons: it carries state
    /// (which Knot, when it was asked), hops onto the player's own action queue before acting, and honours the timeout
    /// rule - ConfirmationManager's 30 s abort only sends a message and the client's automatic reply then arrives as a
    /// plain No, so an answer flagged as a timeout or arriving late does nothing
    /// (<see cref="ACE.Server.ThreadDungeons.Confirmation_ThreadGroupStart.IsLate"/>, reused rather than copied).
    /// Nothing is spent or bound when the dialog is sent, so No, a timeout and a late answer leave the Knot untouched
    /// and say nothing.
    /// </summary>
    public class Confirmation_KnotUse : Confirmation
    {
        public uint KnotGuid { get; }

        public DateTime SentUtc { get; }

        public Confirmation_KnotUse(ObjectGuid playerGuid, uint knotGuid, DateTime sentUtc)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            KnotGuid = knotGuid;
            SentUtc = sentUtc;
        }

        /// <summary>True only for a Yes that is neither a timeout nor late.</summary>
        internal static bool Accepts(bool response, bool timeout, DateTime sentUtc, DateTime nowUtc)
            => response && !ACE.Server.ThreadDungeons.Confirmation_ThreadGroupStart.IsLate(sentUtc, nowUtc, timeout);

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;
            if (player == null)
                return;

            if (!Accepts(response, timeout, SentUtc, DateTime.UtcNow))
                return;

            var knotGuid = KnotGuid;

            player.EnqueueAction(new ActionEventDelegate(() => KnotSeam.OnUseConfirmed(player, knotGuid)));
        }
    }
}
