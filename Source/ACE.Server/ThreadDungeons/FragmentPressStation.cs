using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.RefireStations;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// What giving an item to the Fragment Press should do. Same shape as GemUseDecision in
    /// ThreadDungeonGemHandler, and for the same reason: the decision is pure and the action is not.
    /// </summary>
    public enum PressGiveDecision
    {
        /// <summary>Say the refusal and change nothing.</summary>
        Refuse,

        /// <summary>Destroy the gem outright. Nothing comes back.</summary>
        Destroy,

        /// <summary>Destroy the gem AND end the run it opened. Nothing comes back.</summary>
        CloseRun,
    }

    /// <summary>
    /// The Fragment Press (PHASE-2-IMPLEMENTATION-PLAN.md task D5), wcid 1003614, marked by
    /// PropertyBool.DungeonGemPress and gated by dynamic_dungeons_press_enabled.
    ///
    /// Two gestures, two entry points:
    ///   PRESS   - the player uses a Raw Fragment ON the press. Gem.HandleActionUseOnTarget ->
    ///            RawFragment.UseObjectOnTarget -> <see cref="Press"/>. Costs
    ///            dynamic_dungeons_press_fee_notes Trade Notes, resolves every loaded dose, and hands back a
    ///            finished Thread Gem.
    ///   DESTROY - the player GIVES any Thread Gem to the press. Player_Inventory.GiveObjectToNPC ->
    ///            <see cref="HandleGive"/>. Confirmed, and destroys the gem outright: nothing comes back. If
    ///            the gem is the giver's own and its run is still live, the run is ended at the same time
    ///            ("closed by owner", or "closed by member" when it was a member's key - owner ruling
    ///            2026-09-17, reversing R7/R32), which is the only player-facing way out of a run that is Active but
    ///            finished with - a death and a walk away used to leave the start gate refusing the player's
    ///            next gem until an admin ran /dd end. Handing a fragment back used to be a free reroll of the
    ///            gem's modifiers (owner ruling, 2026-09-08); a player who wants a different gem now buys
    ///            another fragment.
    ///
    /// EVERY PropertyManager read in the whole attunement feature lives in this file. RawFragmentRules is a
    /// pure core and PropertyManager reads throw in unit tests, so the tunables are read here, once per
    /// pressing, and handed in as a PressLimits.
    ///
    /// The pure surface (<see cref="ComposeDoseLog"/>, <see cref="CanDestroy"/>,
    /// <see cref="DecideGive"/>) is what FragmentPressStationTests covers; the Player-driven branches need a
    /// live session and are on the live-verification list instead.
    /// </summary>
    public static class FragmentPressStation
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string EnabledProperty = "dynamic_dungeons_press_enabled";

        /// <summary>
        /// Gates the DETAILED per-dose breakdown - one narrated line per slot, naming the component and what
        /// it did.
        ///
        /// IT GATED A CHAT BLOCK UNTIL 2026-09-07 and now gates a SERVER LOG block, and that move is the
        /// whole of the owner's "players should discover component effects by trial and error" ruling as it
        /// touches this file. The breakdown was the one surface that told a player, in so many words, which
        /// component produced which effect - including for the components the BOARD drew, which is how a
        /// player could have read off the whole component roster from a handful of bare pressings. What the
        /// player gets instead is DungeonGemNarrator.ComposeSelectionLines: the components they chose, and
        /// the fact that the rest were random.
        ///
        /// The diagnostic was not deleted with the chat block. <see cref="ComposeDoseLog"/>'s compact
        /// Before/After line is still written for every dose unconditionally, and this tunable now adds the
        /// narrated form of the same doses beside it, so an operator reading the log still has both the
        /// diffable rendering and the prose one. Default true; a nine-slot pressing writes nine extra lines.
        /// </summary>
        public const string DoseLogProperty = "dynamic_dungeons_press_dose_log";

        /// <summary>
        /// dynamic_dungeons_press_aim_chance. Read once per pressing and handed to the pure core as part of
        /// <see cref="PressLimits"/>, like every other press tunable.
        /// </summary>
        public const string AimChanceProperty = "dynamic_dungeons_press_aim_chance";

        public const string BusyMessage = "You are too busy for that right now.";
        public const string NotAGemMessage = "The press only takes fragments and finished gems.";
        public const string AlreadyAFragmentMessage = "That is already a fragment.";
        public const string DestroyedMessage = "The press grinds the gem to dust. Nothing comes back.";

        /// <summary>
        /// The OWNER's close success line. Deliberately a second sentence rather than a reuse of
        /// <see cref="DestroyedMessage"/>: the two gestures differ in the one way the player cares about, which
        /// is that a run just ended.
        /// </summary>
        public const string ClosedMessage = "The press grinds the gem to dust and the way behind it closes.";

        /// <summary>
        /// A MEMBER's close success line. Separate from <see cref="ClosedMessage"/> because the consequence is
        /// different in the way that matters: a member's key closed the way for people other than themselves, and
        /// the line has to say so or they will not connect the two.
        /// </summary>
        public const string MemberClosedMessage = "The press grinds your key to dust and the way closes for your whole fellowship.";

        /// <summary>
        /// Verbatim from DungeonGemRules.Decide's spec.OwnerGuid != userGuid branch (ThreadDungeonGemHandler.cs).
        /// Someone else's bound gem gets the SAME sentence at the press as it does at a use, so a player who
        /// picked one up is not taught two different stories about the same item.
        /// </summary>
        public const string NotYourGemMessage = "This gem is bound to another adventurer's dungeon.";

        /// <summary>
        /// Appended to EITHER close prompt when the run still owes loot nobody has picked up.
        ///
        /// The entries a close forfeits were always stated; the LOOT it forfeits never was. The case that makes
        /// this matter is a CLEARED group run sitting in its loot-hold window (ruling on
        /// dynamic_dungeons_group_loot_hold_minutes): the ledger, the pending bonus, the overflow and every
        /// member's undelivered pile are all still owed, and EndRun -> ThreadLootPool.DisposeForRunEnd destroys
        /// the lot for the WHOLE roster. Since 2026-09-17 any member can trigger that, so any member has to be
        /// told. The owner could always trigger it and was equally untold, so the owner's prompt carries the same
        /// sentence.
        ///
        /// One constant for both prompts, so the two closers cannot be told different stories about the same loss.
        /// Deliberately does not itemise: the counts are a moving target, and "there is loot you have not
        /// collected" is the whole of what the player has to act on.
        /// </summary>
        public const string UnclaimedLootWarning = "Loot from this Thread is still waiting to be collected, and closing forfeits it.";

        /// <summary>
        /// The close's confirmation prompt. Names the entries because they are the thing being spent: a close
        /// forfeits every remaining entry, and a player who still wants them should walk them out instead.
        /// <paramref name="unclaimedLoot"/> adds <see cref="UnclaimedLootWarning"/>; see its doc comment.
        /// </summary>
        public static string ComposeCloseConfirm(string dungeonName, int entries, int maxEntries, bool unclaimedLoot = false)
            => $"End your run in {dungeonName} and destroy the gem? It has {entries} of {maxEntries} entries left, and nothing comes back."
               + (unclaimedLoot ? " " + UnclaimedLootWarning : string.Empty);

        /// <summary>
        /// The MEMBER's close prompt (owner ruling, 2026-09-17, reversing R7/R32). Says in plain words that this
        /// ends the Thread for everyone, not just for them, because that is the whole difference from the prompt a
        /// member used to get - which said only that their key would be destroyed, and was true at the time. Names
        /// the dungeon and the forfeited entries for the same reason <see cref="ComposeCloseConfirm"/> does, and
        /// takes the same <paramref name="unclaimedLoot"/> warning.
        /// </summary>
        public static string ComposeMemberCloseConfirm(string dungeonName, int entries, int maxEntries, bool unclaimedLoot = false)
            => $"Destroy your key and close {dungeonName} for your WHOLE FELLOWSHIP? Everyone inside is turned out, your key has {entries} of {maxEntries} entries left, and nothing comes back."
               + (unclaimedLoot ? " " + UnclaimedLootWarning : string.Empty);

        /// <summary>
        /// The plain destroy's confirmation prompt (an unbound gem, or a bound gem whose run is already
        /// dead). Names the entries for the same reason <see cref="ComposeCloseConfirm"/> does, even though
        /// no run is ending here: a spent-but-not-yet-dead gem can still carry entries the player has not
        /// walked, and this is the last chance to see that number before the gem is gone.
        /// </summary>
        public static string ComposeDestroyConfirm(int entries, int maxEntries)
            => $"Destroy this gem at the press? It has {entries} of {maxEntries} entries left, and nothing comes back.";

        /// <summary>
        /// The fee and the fragment are both gone and no gem came back. In-world voice (ruling P2-R29): the
        /// wing owes the player, and the ledger is the ERROR line this branch writes.
        /// </summary>
        public const string LostItAllMessage = "The press seized. The wing owes you its fee and a fragment; the ledger has it.";

        /// <summary>
        /// The narrower failure: the branch runs AFTER the charge but before anything was taken from the
        /// pack, so the fee is gone and the fragment is not. Kept separate from <see cref="LostItAllMessage"/>
        /// so neither line can claim a loss the player did not take (ruling P2-R36).
        /// </summary>
        public const string ConsumeFailedMessage = "The press seized after taking its fee and left the fragment in your pack; the ledger has it.";

        /// <summary>
        /// Refused BEFORE the fee is charged (ruling P2-R27), so a full pack costs the player nothing.
        /// </summary>
        public const string PackFullMessage = "Your pack is full. Make room and press again.";

        /// <summary>The per-dose INFO line. Pure so its exact shape can be pinned by a test.</summary>
        public static string ComposeDoseLog(string playerName, DoseLogEntry dose)
            => $"[DYNDUNGEON] press {playerName} {dose?.Name} {dose?.Before} -> {dose?.After}";

        public static string ComposeNotTakingWorkMessage(WorldObject press)
            => $"The {press?.Name ?? "Fragment Press"} is not taking work right now.";

        // "(250,000)" is the Trade Note item's own display name (Player_Bank.cs:46), a literal rather than a
        // computed total - the same shape every RefireStation's refusal uses. The banked figure IS a total.
        public static string ComposeInsufficientFundsMessage(int feeNotes)
            => $"You need {feeNotes} Trade Note{(feeNotes == 1 ? string.Empty : "s")} (250,000), in your pack or as "
               + $"{feeNotes * Player.MmdValue:N0} banked pyreals, to use the Fragment Press.";

        /// <summary>
        /// The press's one-line report. This used to close with RawFragmentRules.SteadinessWord(instability),
        /// a four-band vibe word; with instability removed (owner ruling, 2026-09-07) there is nothing left to
        /// vary on, so the word the calm band produced is kept as a literal rather than a new phrase being
        /// invented for the same sentence.
        /// </summary>
        public static string ComposePressedMessage()
            => "Pressed. It holds.";

        /// <summary>
        /// The two entry counts a give reports in its confirmation prompt. Pure, so the pair the give path
        /// hands the prompt composer is pinnable without a Player.
        /// </summary>
        public static (int Entries, int MaxEntries) GemEntries(int? structure, int? maxStructure)
        {
            var max = Math.Max(0, maxStructure ?? 0);

            return (Math.Clamp(structure ?? 0, 0, max), max);
        }

        /// <summary>
        /// Pure gate for the plain destroy: a fragment is already a fragment. A bound gem is answered by
        /// <see cref="DecideGive"/> above this, so there is nothing left for this to refuse on binding.
        /// Everything else the give path checks (the kill switch, whether the item carries a spec at all)
        /// needs a live item and stays in <see cref="HandleGive"/>.
        /// </summary>
        public static bool CanDestroy(DungeonGemSpec spec, int entries, int maxEntries, out string refusal)
        {
            refusal = null;

            if (spec == null) { refusal = NotAGemMessage; return false; }
            if (spec.Seed == 0) { refusal = AlreadyAFragmentMessage; return false; }

            return true;
        }

        /// <summary>
        /// What the press should do with a given item, once the caller has resolved whether the gem's run is
        /// still live. Three bound cases where <see cref="CanDestroy"/> had one flat refusal:
        ///
        ///   NOT the owner        -> refuse, in the same words a use of someone else's bound gem gets.
        ///   owner, run IS live   -> offer the close (a confirmation, then consume + EndRun; nothing back).
        ///   owner, run is DEAD   -> the ordinary destroy. A bound gem whose run is gone cannot be entered
        ///                           and cannot be closed; it is just a spent gem, so the press takes it.
        ///
        /// PURE, and that is load-bearing rather than stylistic: no PropertyManager (its reads THROW under the
        /// unit-test harness), no database, no WorldObject, no Player. Liveness is decided by the caller and
        /// handed in as <paramref name="runIsLive"/>; nothing here may go looking for a run.
        ///
        /// <see cref="CanDestroy"/> still answers everything that is not a bound gem, so the two non-gem
        /// refusals have one owner.
        /// </summary>
        public static PressGiveDecision DecideGive(DungeonGemSpec spec, uint userGuid, bool runIsLive, int entries,
            int maxEntries, out string refusal)
        {
            refusal = null;

            if (spec != null && spec.Seed != 0 && spec.IsBound)
            {
                if (spec.OwnerGuid != userGuid) { refusal = NotYourGemMessage; return PressGiveDecision.Refuse; }

                if (runIsLive)
                    return PressGiveDecision.CloseRun;

                return PressGiveDecision.Destroy;
            }

            return CanDestroy(spec, entries, maxEntries, out refusal) ? PressGiveDecision.Destroy : PressGiveDecision.Refuse;
        }

        /// <summary>
        /// After a destroyed item has left the player's pack, clears the player's key record on <paramref name="run"/>
        /// when that item WAS their recorded key. True only then. A run of null, an owner gem (the owner has no key
        /// record), someone else's key, or a guid of 0 changes nothing. Never ends or closes the run.
        ///
        /// THE LIVE-RUN BRANCH NO LONGER REACHES THIS. Under rulings R7/R32 a member key given to the press was
        /// destroyed and the run carried on, which is what this existed for; since the 2026-09-17 reversal a key on a
        /// live run resolves to CloseRun instead and the whole run ends. What still reaches it is the DEAD-run
        /// destroy: a key whose run expired, was closed by someone else, or ended between the prompt and the answer.
        /// The record must not outlive the item there either, which is why this is kept rather than deleted.
        /// </summary>
        internal static bool ReleaseMemberKey(ThreadDungeonRun run, uint userGuid, uint itemGuid)
        {
            if (run == null || itemGuid == 0 || run.MemberKeyGem(userGuid) != itemGuid)
                return false;

            run.SetMemberKey(userGuid, 0);
            return true;
        }

        /// <summary>
        /// Reward pre-flight, the same shape Player_Inventory.PreflightEmoteGive uses for an NPC turn-in:
        /// build an <see cref="ItemsToReceive"/> for the one item a gesture hands back and ask whether the
        /// player can hold it. Ruling P2-R27 - the press gesture consumes the player's fragment before
        /// creating the finished gem, so without this a full pack cost the fee AND the fragment.
        ///
        /// Only <see cref="Press"/> calls this now: the give path destroys the gem outright and hands back
        /// nothing, so it has no replacement item to pre-flight.
        ///
        /// The outgoing item is deliberately NOT credited (ItemsToReceive.CreditOutgoing), even though it is
        /// consumed first: the fragment may be sitting in a side pack, and crediting a slot that is not the
        /// one the replacement lands in would let the check pass a case that then fails. This over-refuses a
        /// player whose pack is exactly full, which costs one "make room" round trip and nothing else,
        /// because the refusal happens before any charge or consume.
        ///
        /// Fails OPEN when the weenie is missing from the world database: ItemsToReceive charges 0 slots for
        /// an unknown wcid, so a content gap refuses nothing here and is caught by DungeonGemFactory instead.
        /// </summary>
        private static bool HasRoomFor(Player player, uint wcid)
        {
            var itemsToReceive = new ItemsToReceive(player);

            itemsToReceive.Add(wcid, 1);

            return !itemsToReceive.PlayerExceedsLimits;
        }

        /// <summary>
        /// Reads the press tunables. The ONLY PropertyManager read the attunement core is ever given.
        /// dynamic_dungeons_press_wild_share and dynamic_dungeons_press_fracture_rate were read here too until
        /// the instability mechanic was removed (owner ruling, 2026-09-07); the aim chance was added the same
        /// day, so this is two reads plus two compiled budgets.
        ///
        /// The aim chance is clamped HERE as well as in PressLimits' constructor, and the pair is not
        /// redundant: this clamp is what makes the live tunable safe to set to anything, and that one is what
        /// makes a hand-built PressLimits in a test safe without a PropertyManager (whose reads throw under
        /// the unit-test harness).
        /// </summary>
        public static PressLimits BuildLimits(System.Collections.Generic.IEnumerable<string> guaranteedTypes = null)
            => ComposeLimits((int)PropertyManager.GetLong("dynamic_dungeons_press_max_mods").Item,
                PropertyManager.GetDouble(AimChanceProperty).Item, guaranteedTypes);

        /// <summary>
        /// The pure half of <see cref="BuildLimits"/>: everything but the two tunable reads, so the compiled
        /// budgets - the level ceiling in particular - are pinnable by a unit test without a PropertyManager.
        ///
        /// The level ceiling is the RUN one (owner ruling 2026-10-08): the scarab's set_difficulty offset may
        /// carry a 375 fragment past the gem ceiling, to 410 with the shipped Mana Scarab (+35). Before the split
        /// this read the single 375 ceiling and the same scarab on the same fragment pressed to 375 - the +35 was
        /// silently discarded. See DungeonGemSpec.MaxRunLevel.
        /// </summary>
        internal static PressLimits ComposeLimits(int maxMods, double aimChance, System.Collections.Generic.IEnumerable<string> guaranteedTypes = null)
            => new PressLimits(
                maxMods,
                DungeonGemSpec.MaxLocks,
                RawFragmentRules.MaxEntries,
                DungeonGemSpec.MaxRunLevel,
                aimChance: SanitizeAimChance(aimChance),
                guaranteedTypes: guaranteedTypes);

        /// <summary>
        /// The aim chance as a usable probability: NaN reads as the shipped default, everything else is
        /// clamped to [0, 1]. Pure, so the clamp is pinnable without a live PropertyManager.
        /// </summary>
        public static double SanitizeAimChance(double value)
            => double.IsNaN(value) ? PressLimits.DefaultAimChance : Math.Clamp(value, 0.0, 1.0);

        // ---------------- press (use-on) ----------------

        /// <summary>
        /// The use-on entry. The player has already been walked to the press by Player_Use, and
        /// Gem.HandleActionUseOnTarget sends the UseDone after this returns.
        ///
        /// ORDER MATTERS: the Trade Notes are charged BEFORE the doses are resolved, so a player who cannot
        /// pay has not consumed anything; and the fragment is consumed BEFORE the gem is created, so the pack
        /// slot it occupied is free and the create cannot fail for space. If the create still fails after
        /// that, the notes and the fragment are both gone - the same trade-off SalvageForgeStation documents,
        /// and the reason that branch says "contact staff" rather than pretending nothing happened.
        /// </summary>
        public static void Press(Player player, Gem fragment, WorldObject press)
        {
            if (player == null || fragment == null || press == null)
                return;

            if (!PropertyManager.GetBool(EnabledProperty).Item)
            {
                Say(player, ComposeNotTakingWorkMessage(press));
                return;
            }

            if (player.IsBusy)
            {
                Say(player, BusyMessage);
                return;
            }

            var text = fragment.GetProperty(PropertyString.DungeonGemSpec);

            if (!DungeonGemSpec.TryParse(text, out var spec, out var parseError))
            {
                log.Error($"[DYNDUNGEON] fragment 0x{fragment.Guid.Full:X8} held by {player.Name} has an unparseable spec '{text}': {parseError}");
                Say(player, RawFragment.UnreadableMessage);
                return;
            }

            if (spec.Seed != 0)
            {
                Say(player, RawFragment.AlreadyFinishedMessage);
                return;
            }

            var store = ThreadDungeonManager.Store;

            // The Thread-Guide gate (ThreadGuideFlow.DecidePress), BEFORE the room check and the fee: a stale guide
            // fragment and one missing its rung's required type are refused without costing anything. A guide
            // fragment presses free with the required type guaranteed; a normal fragment comes back unchanged
            // (the configured fee, no guarantee).
            var guide = ThreadGuideFlow.DecidePress(spec, player.Guid.Full, player.ThreadGuideGrantSerial,
                type => RawFragmentRules.DosesOfType(spec, store.Attunement, type),
                (int)PropertyManager.GetLong("dynamic_dungeons_press_fee_notes").Item);

            if (guide.Refusal != null)
            {
                Say(player, guide.Refusal);
                return;
            }

            // BEFORE the charge (ruling P2-R27): the fragment is consumed further down, so a player with no
            // room would otherwise pay the fee, lose the fragment, and get nothing back.
            if (!HasRoomFor(player, DungeonGemFactory.DungeonGemWcid))
            {
                Say(player, PackFullMessage);
                return;
            }

            var feeNotes = guide.Fee;

            if (feeNotes > 0 && !RefireStationCommon.TryCharge(player, feeNotes))
            {
                Say(player, ComposeInsufficientFundsMessage(feeNotes));
                return;
            }

            var result = RawFragmentRules.Resolve(new PressState(spec, fragment.Structure ?? 0), store.Attunement,
                store.Modifiers, BuildLimits(guide.GuaranteedTypes), new Random(), out var doses);

            foreach (var dose in doses)
                log.Info(ComposeDoseLog(player.Name, dose));

            // The modifier magnitude curve's exponent, read ONCE so the narrated dose lines and the summary
            // below print the same scaled numbers the run will apply (DungeonModifierLevelScale).
            var modifierLevelExponent = ThreadDungeonSpawner.ResolveModifierLevelExponent();

            // The narrated breakdown, to the LOG rather than to the player (see DoseLogProperty). Written
            // here, beside the compact lines above, so both renderings of one pressing sit together.
            if (PropertyManager.GetBool(DoseLogProperty).Item)
                foreach (var line in DungeonGemNarrator.ComposeDoseLines(doses, store.Modifiers, modifierLevelExponent))
                    log.Info($"[DYNDUNGEON] press {player.Name}{line}");

            if (!player.TryConsumeFromInventoryWithNetworking(fragment))
            {
                // Nothing has been created yet, so only the notes are gone. Say so plainly.
                log.Error($"[DYNDUNGEON] press could not consume fragment 0x{fragment.Guid.Full:X8} from {player.Name}");
                Say(player, ConsumeFailedMessage);
                return;
            }

            // A fresh pressing is always full, so entries and maxEntries are the same number: add_entry raises
            // the ceiling and the count together, and nothing has been spent yet (ruling P2-R19).
            var gem = DungeonGemFactory.CreateGem(result.Spec, result.Entries, result.Entries,
                DungeonGemFactory.ResolveDungeonName(result.Spec), out var createError);

            // A guide gem carries the guide tag through Resolve (every With* keeps it) and is bound like the
            // fragment it came from.
            if (guide.IsGuide)
                ThreadGuideItems.Bind(gem);

            if (gem == null || !player.TryCreateInInventoryWithNetworking(gem))
            {
                gem?.Destroy();
                log.Error($"[DYNDUNGEON] press failed to hand {player.Name} a gem for '{result.Spec.Serialize()}': {createError ?? "no room in pack"}");
                Say(player, LostItAllMessage);
                return;
            }

            Say(player, ComposePressedMessage());

            // What the player is told about the pressing itself: the components THEY chose, and nothing about
            // what any of them did (owner ruling, 2026-09-07). spec.Load, not result.Spec.Load - Resolve
            // clears the load on the pressed spec, so the choice only exists on the fragment's own spec.
            foreach (var line in DungeonGemNarrator.ComposeSelectionLines(spec.Load, store.Attunement.DisplayName, doses))
                Say(player, line);

            foreach (var line in DungeonGemNarrator.ComposeSummary(result.Spec, result.Entries, store.Modifiers, modifierLevelExponent))
                Say(player, line);

            // A special guide rung's lesson: what the component type it taught does, as a popup and in chat.
            var explanation = ThreadGuideText.ComposeExplain(guide.ExplainType);

            if (explanation != null)
            {
                ThreadGuideStation.Popup(player, explanation);
                Say(player, explanation);
            }
        }

        // ---------------- destroy (give) ----------------

        /// <summary>
        /// The give entry, called from Player_Inventory.GiveObjectToNPC after the item has been bounced back
        /// into the player's pack with GameEventInventoryServerSaveFailed. That bounce is what lets the press
        /// take an item where the refire stations cannot: the item is back in inventory, so the consume below
        /// is an ordinary inventory consume rather than a half-completed give.
        ///
        /// BOTH outcomes are confirmed: destroying an unbound or dead-bound gem outright, and closing a live
        /// run. Neither is free or reversible any more, so an accidental drag-give must not cost the player
        /// the gem. <see cref="HandleDestroyConfirm"/> is the confirmed half of both.
        /// </summary>
        public static void HandleGive(Player player, WorldObject press, WorldObject item)
        {
            if (player == null || press == null || item == null)
                return;

            if (!PropertyManager.GetBool(EnabledProperty).Item)
            {
                Say(player, ComposeNotTakingWorkMessage(press));
                return;
            }

            if (!TryGate(player, item, out var spec, out var run, out var decision))
                return;

            var (entries, maxEntries) = GemEntries(item.Structure, item.MaxStructure);
            var itemGuid = item.Guid.Full;
            var dungeonName = run?.Dungeon?.Name ?? DungeonGemFactory.ResolveDungeonName(spec);

            // Two close prompts, because the two closes are not the same act: the owner ends their own run, a member
            // ends everyone's. IsOwnerGem is the one that decides, not the spec, for the reason HandleDestroyConfirm
            // gives below. Both carry the unclaimed-loot warning when there is loot to lose.
            var unclaimedLoot = decision == PressGiveDecision.CloseRun && HasUnclaimedLoot(run);

            var prompt = decision != PressGiveDecision.CloseRun
                ? ComposeDestroyConfirm(entries, maxEntries)
                : IsOwnerGem(run, itemGuid)
                    ? ComposeCloseConfirm(dungeonName, entries, maxEntries, unclaimedLoot)
                    : ComposeMemberCloseConfirm(dungeonName, entries, maxEntries, unclaimedLoot);

            // BOTH outcomes are confirmed now. The re-open used to be free and reversible, so it needed no
            // confirmation; a destroy is neither, so an accidental drag-give must not cost the player the gem.
            //
            // The two-line confirmation shape CustomAugBroker uses, NOT RefireStationCommon.HandleGive: that
            // helper's apply delegate runs after TryCharge and this gesture has no fee.
            //
            // Only the GUID is captured. By the time the callback runs the player may have moved, dropped,
            // banked or spent the gem, and the run may have ended underneath it, so HandleDestroyConfirm
            // re-resolves the item and re-runs the whole gate rather than trusting anything decided here.
            if (!player.ConfirmationManager.EnqueueSend(
                    new Confirmation_Custom(player.Guid, () => HandleDestroyConfirm(player, press, itemGuid)),
                    prompt))
            {
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
            }
        }

        /// <summary>
        /// The confirmed half of the give. Re-resolves the gem by guid and re-runs the entire gate before
        /// touching anything - the same reason RefireStationCommon.HandleConfirm and CustomAugBroker.HandleConfirm
        /// re-verify.
        ///
        /// THE ORDER BELOW IS THE WHOLE CORRECTNESS ARGUMENT AND MUST NOT BE REARRANGED. For a CloseRun, the
        /// gem is consumed BEFORE EndRun. EndRun destroys the bound gem itself (OnRunEnded -> GemDestroyer ->
        /// DestroyGem), which enqueues a delegate onto the OWNER's action queue and re-resolves the gem by
        /// guid inside that delegate. With the gem already gone, that delegate's FindObject returns null and
        /// it returns: no double consume, no "crumbles to dust" line contradicting the one we just sent, and
        /// no latch needed here. EndRun is separately exactly-once through ThreadDungeonRun.MarkEnded, which
        /// returns true only for the caller that made the transition.
        ///
        /// Calling EndRun off the world thread is established rather than new: TryStart calls
        /// EndClearedRunsForOwner -> EndRun outside startLock, and documents that TryStart is not guaranteed
        /// to be on the world thread either.
        /// </summary>
        private static void HandleDestroyConfirm(Player player, WorldObject press, uint itemGuid)
        {
            if (player == null || press == null)
                return;

            if (!PropertyManager.GetBool(EnabledProperty).Item)
            {
                Say(player, ComposeNotTakingWorkMessage(press));
                return;
            }

            var item = player.FindObject(itemGuid, Player.SearchLocations.MyInventory);

            if (item == null)
            {
                Say(player, NotAGemMessage);
                return;
            }

            if (!TryGate(player, item, out var spec, out var run, out var decision))
                return;

            // The run ended while the dialog was open (a TTL expiry, an admin /dd end, the copy unloading).
            // The gem is now a dead bound gem, which is exactly the ordinary destroy, and that is what the
            // player asked for minus the part that already happened. Do it rather than refuse.
            if (decision != PressGiveDecision.CloseRun)
            {
                if (!player.TryConsumeFromInventoryWithNetworking(item))
                {
                    Say(player, NotAGemMessage);
                    return;
                }

                // The DEAD-run branch only (see ReleaseMemberKey's doc comment): a key whose run is still live now
                // reaches the close below instead. Kept because a run can end between the prompt and the answer, and
                // because the record must not outlive the item either way.
                if (ReleaseMemberKey(spec.IsBound ? ThreadDungeonManager.GetRun(spec.RunId) : null, player.Guid.Full, itemGuid))
                    log.Info($"[DYNDUNGEON] {player.Name} gave their key 0x{itemGuid:X8} to the press; key record cleared, run {spec.RunId} continues");

                log.Info($"[DYNDUNGEON] {player.Name} destroyed gem 0x{itemGuid:X8} at the press ('{spec.Serialize()}')");
                Say(player, DestroyedMessage);
                return;
            }

            // Belt and braces. The spec string is player-visible data written onto an item; the RUN is the authority
            // over whose gem this is and who EndRun is about to evict. Since a member key can close a run too, this
            // is no longer a bare owner comparison: it asks whether this exact item is this exact player's own gem
            // ON THIS RUN - the owner gem for the owner, that member's recorded key for a member. DecideGive has
            // already matched spec.OwnerGuid, so this can only fire on a spec that disagrees with its run.
            var ownerClose = IsOwnerGem(run, itemGuid);
            var claimed = ownerClose ? run.OwnerGuid == player.Guid.Full : run.MemberKeyGem(player.Guid.Full) == itemGuid;

            if (!claimed)
            {
                log.Error($"[DYNDUNGEON] press refused to close {run} for {player.Name} (0x{player.Guid.Full:X8}): gem 0x{itemGuid:X8} claims owner 0x{spec.OwnerGuid:X8} but the run does not");
                Say(player, NotYourGemMessage);
                return;
            }

            var (entries, maxEntries) = GemEntries(item.Structure, item.MaxStructure);

            // STEP ONE of two. Nothing has been ended yet, so a failed consume costs the player nothing.
            if (!player.TryConsumeFromInventoryWithNetworking(item))
            {
                log.Error($"[DYNDUNGEON] press could not consume bound gem 0x{itemGuid:X8} from {player.Name}; {run} left open");
                Say(player, NotAGemMessage);
                return;
            }

            // Everyone else on the roster is told WHO closed it, before EndRun fires. EndRun's own messages (the gem
            // crumbling, "the dungeon collapses around you") say what happened but not who did it, and on a group run
            // that is the only question the other five members will have. Not duplicated here.
            NotifyRosterOfClose(run, player, ownerClose ? DungeonRunTelemetry.EndReasons.ClosedByOwner : DungeonRunTelemetry.EndReasons.ClosedByMember);

            // STEP TWO. See the doc comment: the gem is already gone, so DestroyGem finds nothing.
            log.Info($"[DYNDUNGEON] {player.Name} closed {run} at the press as {(ownerClose ? "owner" : "member")} (gem 0x{itemGuid:X8} destroyed, {entries} of {maxEntries} entries forfeited)");
            ThreadDungeonManager.EndRun(run, ownerClose ? DungeonRunTelemetry.EndReasons.ClosedByOwner : DungeonRunTelemetry.EndReasons.ClosedByMember);

            Say(player, ownerClose ? ClosedMessage : MemberClosedMessage);
        }

        /// <summary>
        /// Is <paramref name="itemGuid"/> the run's OWNER gem, as opposed to one of its member keys? The one place
        /// the two closes are told apart, so the prompt, the log line, the end reason and the success line cannot
        /// disagree about which one happened.
        /// </summary>
        private static bool IsOwnerGem(ThreadDungeonRun run, uint itemGuid) => run != null && run.GemGuid == itemGuid;

        /// <summary>
        /// Does this run still owe loot that EndRun would destroy? Both halves are EXISTING read-only checks,
        /// reused rather than re-derived: <see cref="ThreadDungeonRun.HasUnclaimedLoot"/> covers the ledger, the
        /// pending bonus, the overflow, every member's undelivered pile and every delivery-wanted flag, and
        /// <see cref="ThreadLootPool.AnyCacheHoldsItems"/> covers a placed cache nobody emptied. Nothing here
        /// drains, claims or mutates anything, so asking the question cannot change the answer.
        ///
        /// Both are already called from a player's action queue by ThreadExitGuard and by the /dd player command,
        /// which is the same thread context the give and its confirmation run on.
        ///
        /// Run-wide rather than the per-member HasUnclaimedLootFor/AnyCacheHoldsItemsFor variants, and that is the
        /// point: a close discards what the WHOLE roster is owed, so a member who has collected their own share
        /// still has to be told about everyone else's.
        /// </summary>
        private static bool HasUnclaimedLoot(ThreadDungeonRun run)
            => run != null && (run.HasUnclaimedLoot || ThreadLootPool.AnyCacheHoldsItems(run));

        /// <summary>
        /// "{name} closed the way into {dungeon}." to every OTHER online roster member. Sent before EndRun so the
        /// attribution arrives ahead of the eviction rather than after it, and skipped entirely for a solo run,
        /// where the only member is the closer.
        /// </summary>
        private static void NotifyRosterOfClose(ThreadDungeonRun run, Player closer, string endReason)
        {
            foreach (var member in run.Roster)
            {
                if (member.Guid == closer.Guid.Full)
                    continue;

                var other = PlayerManager.GetOnlinePlayer(member.Guid);

                if (other != null)
                    Say(other, ComposeClosedByText(closer.Name, run.Dungeon?.Name ?? "the Thread"));
            }

            log.Info($"[DYNDUNGEON] {run} closed by {closer.Name} (0x{closer.Guid.Full:X8}), reason '{endReason}', roster of {run.Roster.Count} notified");
        }

        /// <summary>What the rest of the roster is told when someone closes the way. Pure, so its shape is pinnable.</summary>
        public static string ComposeClosedByText(string closerName, string dungeonName)
            => $"{closerName} closed the way into {dungeonName}.";

        /// <summary>
        /// Everything both give outcomes check, in one place so the confirmation re-runs EXACTLY what the give
        /// ran: parse the spec, resolve whether the gem's run is live, then run the pure gate. Returns false
        /// having already said the refusal.
        ///
        /// No pack pre-flight here any more (see <see cref="HasRoomFor"/>'s doc comment): the give destroys
        /// the gem and hands back nothing, so a full pack cannot cost the player anything.
        /// </summary>
        private static bool TryGate(Player player, WorldObject item, out DungeonGemSpec spec,
            out ThreadDungeonRun run, out PressGiveDecision decision)
        {
            spec = null;
            run = null;
            decision = PressGiveDecision.Refuse;

            var text = item.GetProperty(PropertyString.DungeonGemSpec);

            if (string.IsNullOrEmpty(text) || !DungeonGemSpec.TryParse(text, out spec, out _))
            {
                Say(player, NotAGemMessage);
                return false;
            }

            // The run's OWNER GEM OR ANY MEMBER'S KEY counts as live here, which is the same test
            // ThreadDungeonGemHandler uses to recognise one of a run's gems. REVERSAL of rulings R7/R32 (owner
            // ruling, 2026-09-17): a member handing their key to the press used to destroy only the key and leave
            // the run open, and the comment that stood here argued at length for exactly that. A member key now
            // closes the run for the whole fellowship, so the press is the way out for every member rather than
            // only for the owner.
            //
            // A key spec is bound to the MEMBER's guid (StartGroupRun's spec.WithBinding(run.Instance,
            // member.Guid)), so DecideGive's owner check already passes for its rightful holder and refuses anyone
            // else who picked it up. The run must also still be registered, so a recycled RunId with a different gem
            // is not live either.
            var candidate = spec.IsBound ? ThreadDungeonManager.GetRun(spec.RunId) : null;
            var runIsLive = candidate != null && candidate.IsRunGem(item.Guid.Full);

            decision = DecideGive(spec, player.Guid.Full, runIsLive, item.Structure ?? 0, item.MaxStructure ?? 0, out var refusal);

            if (decision == PressGiveDecision.Refuse)
            {
                Say(player, refusal);
                return false;
            }

            run = runIsLive ? candidate : null;

            return true;
        }

        private static void Say(Player player, string message)
            => player.Session?.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
    }
}
