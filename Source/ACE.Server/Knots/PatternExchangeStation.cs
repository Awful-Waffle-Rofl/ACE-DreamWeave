using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.RefireStations;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Knots
{
    /// <summary>
    /// The thin, impure half of the Pattern Exchanger (PropertyBool.PatternExchanger, wcid 1007006). Player_Inventory
    /// .GiveObjectToNPC bounces the given Pattern back into the pack with a ServerSaveFailed, then calls
    /// <see cref="HandleGive"/>, which gathers the inputs, asks <see cref="PatternExchange"/> for the decision, and
    /// does the inventory work. Same shape as FragmentPressStation / CustomAugBroker.
    ///
    /// Giving a Pattern only asks a Yes/No question (naming the Pattern); the trade runs on Yes, in <see cref="HandleConfirm"/>.
    ///
    /// ORDER, and why: decide everything (pure) -> check the player can receive one item -> consume the partner ->
    /// consume the given Pattern -> create and give the new one. Nothing is given before BOTH consumes succeeded, so
    /// a failure before that point costs the player nothing, and a failure after it is restored best-effort (see
    /// <see cref="Restore"/>) and logged with a [KNOTS] prefix.
    /// </summary>
    public static class PatternExchangeStation
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string FailedNothingTakenLine = "The exchange failed and nothing was taken.";
        public const string FailedRestoredLine = "The exchange failed. Your Patterns were handed back to you.";
        public const string FailedLostLine = "The exchange failed and I could not hand your Patterns back. The ledger has it; ask for staff.";

        /// <summary>What <see cref="TryGate"/> resolved: everything the prompt and the trade itself need.</summary>
        private sealed class Gate
        {
            public WorldObject Given;
            public WorldObject Partner;
            public string GivenKey;
            public PinnacleDef NewDef;
            public string OldDisplay;
            public List<string> CandidateNames;
        }

        /// <summary>
        /// Entry point. Runs every check (kind, bare, key, partner, another pinnacle, pack room) and, when all pass, asks the
        /// player to confirm. NOTHING is consumed here: the trade runs in <see cref="HandleConfirm"/>, which re-runs the gate.
        /// </summary>
        public static void HandleGive(Player player, WorldObject npc, WorldObject item)
        {
            if (player == null || npc == null || item == null)
                return;

            if (!TryGate(player, npc, item.Guid.Full, out var gate))
                return;

            var given = gate.Given;
            var prompt = PatternExchange.ConfirmPrompt(npc.Name, given.GetProperty(PropertyString.Name), gate.OldDisplay, gate.CandidateNames);
            var givenGuid = given.Guid.Full;

            // When the prompt names the pinnacle coming back, confirm must still land on that one.
            var namedKey = gate.CandidateNames.Count == 1 ? gate.NewDef.Key : null;

            if (!player.ConfirmationManager.EnqueueSend(
                    new Confirmation_Custom(player.Guid, () => HandleConfirm(player, npc, givenGuid, namedKey)),
                    prompt))
            {
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
            }
        }

        /// <summary>
        /// Gathers the inputs and runs every check up to and including the pack-room check, telling the matching refusal line
        /// on failure. Returns false (having told the player, or silently when the given item is gone) when the trade must not go ahead.
        /// </summary>
        private static bool TryGate(Player player, WorldObject npc, uint givenGuid, out Gate gate)
        {
            gate = null;

            // The give path has already bounced the item to the pack; refuse to act on anything not in the
            // player's own inventory (side packs included).
            var given = player.FindObject(givenGuid, Player.SearchLocations.MyInventory);

            if (given == null || given.Guid.Full != givenGuid)
                return false;

            var givenKind = KnotItems.KindOf(given);
            var givenFibers = given.GetProperty(PropertyInt.KnotFiberCount) ?? 0;
            var givenKey = given.GetProperty(PropertyString.PinnacleKey);

            // The given item is guarded against an open trade at Player_Inventory.GiveObjectToNPC; the partner is not,
            // so items in the trade window (or inside a side pack that is) are excluded from the candidates here.
            var held = EnumerateInventory(player).Select(e => Describe(e.Item, e.Traded)).ToList();
            var partnerGuid = PatternExchange.FindPartner(given.Guid.Full, givenKey, held);

            var store = ThreadDungeonManager.Store;
            var available = KnotPinnacles.Available(store);

            var decision = PatternExchange.Decide(givenKind, givenFibers, givenKey, partnerGuid != 0,
                available.Select(p => p.Key), count => ThreadSafeRandom.Next(0, count - 1));

            if (!decision.Accepted)
            {
                Tell(player, npc, decision.Refusal);
                return false;
            }

            // The partner must still be a distinct object in the player's own inventory.
            var partnerEntry = EnumerateInventory(player).FirstOrDefault(e => e.Item.Guid.Full == partnerGuid);
            var partner = partnerEntry.Item;

            // Re-checked just before consuming: still held, not the given item, and not in the trade window.
            if (partner == null || partner.Guid.Full == given.Guid.Full || partnerEntry.Traded)
            {
                Tell(player, npc, PatternExchange.NoPartnerLine);
                return false;
            }

            var newDef = available.First(p => p.Key == decision.NewKey);

            // Room for ONE item, checked BEFORE anything is consumed. The two outgoing Patterns are deliberately not
            // credited (ItemsToReceive.CreditOutgoing): either may sit in a side pack, and crediting a slot other than
            // the one the new Pattern lands in could pass a case that then fails. Same call FragmentPressStation makes.
            var itemsToReceive = new ItemsToReceive(player);
            itemsToReceive.Add(KnotItems.PatternWcid, 1);

            if (itemsToReceive.PlayerExceedsLimits)
            {
                Tell(player, npc, PatternExchange.NoRoomLine);
                return false;
            }

            var oldDef = store?.Pinnacles?.FirstOrDefault(p => p.Key == givenKey);

            // The names of the pinnacles a trade may land on, from the same filter Decide uses.
            var candidateNames = PatternExchange.Candidates(available.Select(p => p.Key), givenKey)
                .Select(k => available.First(p => p.Key == k))
                .Select(p => p.Name ?? p.Key)
                .ToList();

            gate = new Gate
            {
                Given = given,
                Partner = partner,
                GivenKey = givenKey,
                NewDef = newDef,
                OldDisplay = oldDef?.Name ?? givenKey,
                CandidateNames = candidateNames,
            };

            return true;
        }

        /// <summary>
        /// Runs on Yes. The dialog gives the player time to move Patterns, so the whole gate is re-run against current
        /// state (fresh lookup of the given item by guid, fresh partner, a fresh random pick) before anything is consumed.
        /// ORDER, and why: gate (pure decision, pack room) -> trade-window guard -> consume the partner -> consume the
        /// given Pattern -> create and give the new one. Nothing is given before BOTH consumes succeeded, so a failure
        /// before that point costs the player nothing, and a failure after it is restored best-effort (see
        /// <see cref="Restore"/>) and logged with a [KNOTS] prefix.
        /// </summary>
        private static void HandleConfirm(Player player, WorldObject npc, uint givenGuid, string namedKey)
        {
            if (player == null || npc == null)
                return;

            if (!TryGate(player, npc, givenGuid, out var gate))
            {
                // Give-time stays silent when the item is gone; after a Yes the player is owed an answer.
                if (player.FindObject(givenGuid, Player.SearchLocations.MyInventory) == null)
                    Tell(player, npc, FailedNothingTakenLine);

                return;
            }

            if (!PatternExchange.PromptStillValid(namedKey, gate.NewDef.Key))
            {
                Tell(player, npc, FailedNothingTakenLine);
                return;
            }

            var given = gate.Given;
            var partner = gate.Partner;
            var givenKey = gate.GivenKey;
            var newDef = gate.NewDef;
            var oldDisplay = gate.OldDisplay;
            // Everything needed for a restoration is captured now, because a consumed item is destroyed.
            var givenName = given.GetProperty(PropertyString.Name);
            var partnerName = partner.GetProperty(PropertyString.Name);
            var givenInscription = Inscription.Capture(given);
            var partnerInscription = Inscription.Capture(partner);
            var givenGuidFull = given.Guid.Full;
            var partnerGuidFull = partner.Guid.Full;

            var newDisplay = newDef.Name ?? newDef.Key;

            // Last guard before anything is consumed: an item may have entered the trade window since the scan.
            if (player.IsTrading && (given.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow)
                || partner.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow)))
            {
                Tell(player, npc, FailedNothingTakenLine);
                return;
            }

            if (!player.TryConsumeFromInventoryWithNetworking(partner))
            {
                log.Error($"[KNOTS] exchange: could not consume partner 0x{partnerGuidFull:X8} ({givenKey}) from {player.Name}; nothing taken");
                Tell(player, npc, FailedNothingTakenLine);
                return;
            }

            if (!player.TryConsumeFromInventoryWithNetworking(given))
            {
                // The partner is already gone: hand it back as a fresh identical Pattern.
                log.Error($"[KNOTS] exchange: partner 0x{partnerGuidFull:X8} ({givenKey}) consumed but given 0x{givenGuidFull:X8} could not be consumed from {player.Name}; restoring the partner");
                var restored = Restore(player, givenKey, partnerName, partnerInscription);
                Tell(player, npc, restored == 1 ? FailedRestoredLine : FailedLostLine);
                return;
            }

            // Both consumed. Mint the new Pattern and give it. NOTHING was given before this point.
            var created = KnotDrops.CreatePattern(newDef);

            if (created == null || !player.TryCreateInInventoryWithNetworking(created))
            {
                created?.Destroy();

                // Best-effort restoration: both Patterns come back as fresh copies carrying the key, the name and the
                // inscription (with its scribe fields) - a bare Pattern carries nothing else. Counts only what actually
                // returned, and the ledger line says so.
                var restored = Restore(player, givenKey, givenName, givenInscription) + Restore(player, givenKey, partnerName, partnerInscription);

                log.Error($"[KNOTS] exchange FAILED for {player.Name}: consumed 0x{givenGuidFull:X8} and 0x{partnerGuidFull:X8} ({givenKey}) but could not give a '{newDef.Key}' Pattern; restored {restored}/2");
                Tell(player, npc, restored == 2 ? FailedRestoredLine : FailedLostLine);
                return;
            }

            log.Info($"[KNOTS] exchange: {player.Name} traded 0x{givenGuidFull:X8} + 0x{partnerGuidFull:X8} ({givenKey}) for a '{newDef.Key}' Pattern 0x{created.Guid.Full:X8}");
            Tell(player, npc, PatternExchange.SuccessLine(oldDisplay, newDisplay));
        }

        private static PatternCandidate Describe(WorldObject i, bool traded)
            => new PatternCandidate(i.Guid.Full, KnotItems.KindOf(i), i.GetProperty(PropertyInt.KnotFiberCount) ?? 0, i.GetProperty(PropertyString.PinnacleKey), traded);

        /// <summary>
        /// Every item in the player's pack and in each side pack (equipped items excluded), each with whether it is in
        /// the open trade window directly or through a side pack that is.
        /// </summary>
        private static IEnumerable<(WorldObject Item, bool Traded)> EnumerateInventory(Player player)
            => EnumerateInventory(player, player.IsTrading ? player.ItemsInTradeWindow : null, false);

        private static IEnumerable<(WorldObject Item, bool Traded)> EnumerateInventory(Container root, HashSet<ObjectGuid> tradeWindow, bool parentTraded)
        {
            foreach (var i in root.Inventory.Values.ToList())
            {
                var traded = parentTraded || (tradeWindow != null && tradeWindow.Contains(i.Guid));

                yield return (i, traded);

                if (i is Container side)
                    foreach (var inner in EnumerateInventory(side, tradeWindow, traded))
                        yield return inner;
            }
        }

        /// <summary>The inscription fields of an item, so a restored Pattern keeps what the player wrote on it.</summary>
        private readonly record struct Inscription(string Text, string ScribeName, string ScribeAccount)
        {
            public static Inscription Capture(WorldObject i)
                => new Inscription(i.GetProperty(PropertyString.Inscription), i.GetProperty(PropertyString.ScribeName), i.GetProperty(PropertyString.ScribeAccount));

            public void Apply(WorldObject i)
            {
                if (Text != null) i.SetProperty(PropertyString.Inscription, Text);
                if (ScribeName != null) i.SetProperty(PropertyString.ScribeName, ScribeName);
                if (ScribeAccount != null) i.SetProperty(PropertyString.ScribeAccount, ScribeAccount);
            }
        }

        /// <summary>
        /// Recreates one bare Pattern for <paramref name="key"/> with its original display name and inscription and gives it back.
        /// Returns 1 when the player holds it again, else 0 (logged). Best effort: a failure here is the residual
        /// loss window the ERROR ledger lines exist for.
        /// </summary>
        private static int Restore(Player player, string key, string name, Inscription inscription)
        {
            var copy = KnotDrops.CreatePattern(new PinnacleDef { Key = key, Name = key });

            if (copy == null)
            {
                log.Error($"[KNOTS] exchange restore: Pattern '{key}' for {player.Name} did not create");
                return 0;
            }

            if (!string.IsNullOrEmpty(name))
                copy.SetProperty(PropertyString.Name, name);

            inscription.Apply(copy);

            if (!player.TryCreateInInventoryWithNetworking(copy))
            {
                copy.Destroy();
                log.Error($"[KNOTS] exchange restore: no room to hand {player.Name} back a '{key}' Pattern");
                return 0;
            }

            return 1;
        }

        private static void Tell(Player player, WorldObject npc, string line)
            => RefireStationCommon.SendStationTell(player, npc, line);
    }
}
