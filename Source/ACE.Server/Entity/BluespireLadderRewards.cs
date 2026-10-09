using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The one-time-per-character currency payout for clearing a Bluespire ladder rung.
    ///
    /// WHY IT HANGS OFF THE CREATED-ROW BRANCH. QuestManager.Update and QuestManager.SetQuestCompletions
    /// each have a branch that runs exactly once per character per quest name - the instant the registry row
    /// is created. That is the only event on this server that means "this character has done this for the
    /// FIRST time, ever", and it is exactly the requirement: clearing a rung pays once, re-running the
    /// dungeon is allowed and pays nothing. No ledger of our own is needed, because the quest row IS the
    /// ledger.
    ///
    /// It is called as a SIBLING of player.HandleQuestStampRowCreated, never from inside it. That method is
    /// gated on the quest_stamps_enabled feature flag, and the ladder's reward must not silently stop paying
    /// because an unrelated feature was switched off.
    ///
    /// THE STAMP HAS ALREADY LANDED BY THE TIME THIS RUNS, so refusing is not an option: a full pack means
    /// the stack is dropped at the character's feet and logged. That is the owner ruling. A hold-and-retry-
    /// at-login mechanism is deliberately out of scope.
    /// </summary>
    public static class BluespireLadderRewards
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Most separate world objects one payout may create. Only reachable when the configured currency
        /// weenie is non-stackable or has a tiny MaxStackSize - both content bugs - and it exists so such a
        /// bug costs a logged shortfall rather than two hundred objects on the floor.
        /// </summary>
        public const int MaxPayoutObjects = 25;

        /// <summary>
        /// Quest registry name -> rung. A COMPILED CONSTANT TABLE, not a tunable: the clear quest name is
        /// what ties a rung's reward to its clear, and a live-editable mapping would let one typo detach the
        /// two with nothing to notice it. Built from BluespireLadderGate.ClearedQuestName so the gate and
        /// the payout can never disagree about a name.
        ///
        /// Case-insensitive because QuestManager itself compares quest names case-insensitively, so a
        /// content author who writes the name in another case must still be paid.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, int> RungByQuestName = BuildRungTable();

        private static Dictionary<string, int> BuildRungTable()
        {
            var table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var rung = 1; rung <= BluespireLadderGate.RungCount; rung++)
                table[BluespireLadderGate.ClearedQuestName(rung)] = rung;

            return table;
        }

        /// <summary>
        /// The rung <paramref name="questName"/> is the clear stamp for, or 0 when it is not a ladder quest
        /// at all. Pure, and the first thing <see cref="Pay"/> does - almost every quest row created on this
        /// server is not a ladder clear, so this dictionary miss is the whole cost on that path.
        /// </summary>
        public static int RungForQuestName(string questName)
        {
            if (string.IsNullOrWhiteSpace(questName))
                return 0;

            return RungByQuestName.TryGetValue(questName, out var rung) ? rung : 0;
        }

        /// <summary>
        /// True when a KILL TASK naming <paramref name="questName"/> may CREATE the character's registry row
        /// rather than only credit a row they already hold. Pure, so the whole rule is unit testable; the
        /// tunable read that produces <paramref name="ladderEnabled"/> stays at the call site, which is
        /// Creature.OnDeath_HandleKillTask and nowhere else.
        ///
        /// WHY A BYPASS IS NEEDED AT ALL - A BOOTSTRAP CYCLE. QuestManager.HandleKillTask returns early on
        /// `if (!HasQuest(questName))`, and Creature_Death gates both its damager and its fellowship branch
        /// on the same test, so a KillQuest can only ever INCREMENT a row somebody else created. Every retail
        /// kill task is armed first by the NPC that hands it out. The ladder's rung bosses have no such NPC,
        /// and the repo's usual cure - pre-arming the row with SetQuestCompletions - is CLOSED here, because
        /// arming the row IS the row creation that <see cref="Pay"/> fires on: a pre-armed character would be
        /// paid for walking into the dungeon instead of for clearing it. The boss's death is the one moment
        /// where "the row is created" and "the rung was cleared" are the same event, so that is where the row
        /// has to be created. Without this, rungs 4, 5 and 6 could never be cleared by anybody.
        ///
        /// NARROW BY CONSTRUCTION. The discriminator is <see cref="RungForQuestName"/> - the SAME compiled
        /// six-name table the payout keys off, built from BluespireLadderGate.ClearedQuestName - so the
        /// bypass and the reward cannot disagree about what a ladder quest is, and every other kill task in
        /// the game keeps its HasQuest gate untouched for one dictionary miss. It is deliberately NOT the
        /// BluespireLadderRung property (PropertyInt 9069): that id is registered on PORTAL weenies, no rung
        /// boss carries it, and reading it would make this fix depend on content changes the rung PRs do not
        /// ship. It is also not a name prefix or shape test, which would quietly capture a seventh name.
        ///
        /// <paramref name="ladderEnabled"/> false returns false, so the master kill switch turns a rung off
        /// in one piece. Stamping while the ladder is off would create a row that <see cref="Pay"/> then
        /// declines to pay, and because the row IS the ledger that clear could never be re-earned - an
        /// unpaid ledger entry is strictly worse than no entry.
        /// </summary>
        public static bool ShouldBootstrapClearRow(string questName, bool ladderEnabled)
        {
            return ladderEnabled && RungForQuestName(questName) != 0;
        }

        /// <summary>
        /// True when a kill-task stamp attempt should trigger the ladder's daily repeat payout - only for
        /// <see cref="QuestManager.KillStampOutcome.Restamped"/>, the outcome for a bootstrap row that
        /// already existed and was just incremented again after its cooldown elapsed. Pure, so the rule is
        /// unit testable on its own.
        ///
        /// NEVER Created: that outcome is a first-ever clear, already paid inside QuestManager.Update's
        /// created-row branch (TryPayBluespireLadderReward). Paying again here on Created would double-pay
        /// the very first clear. The caller (QuestManager.HandleKillTask) additionally gates this on
        /// bootstrapRow, so this method is only ever asked about the ladder's own six rung names in
        /// practice; it stays a plain outcome test rather than re-checking RungForQuestName itself, because
        /// that check already ran once to decide bootstrapRow.
        /// </summary>
        internal static bool ShouldPayOnKillStamp(QuestManager.KillStampOutcome outcome)
        {
            return outcome == QuestManager.KillStampOutcome.Restamped;
        }

        /// <summary>
        /// What one payout stack actually delivered, read back from the object AFTER
        /// <see cref="WorldObject.SetStackSize"/> was called on it - never the size that was requested.
        ///
        /// SetStackSize is a no-op unless the object's runtime type is Stackable
        /// (Source/ACE.Server/WorldObjects/WorldObject_Properties.cs:3196-3200), and that runtime type
        /// is fixed by the weenie's WeenieType at construction - so a currency wcid shipped with the
        /// wrong WeenieType (Generic instead of Stackable, say) silently keeps StackSize at whatever it
        /// was created with, however large a size <see cref="Pay"/> asked for. Trusting the REQUESTED
        /// size instead of this readback is exactly how that bug hid: the object handed to the player
        /// held 1, while delivered/dropped totals, the player-facing chat message and the log line all
        /// claimed the full configured reward. Real case (2026-09-18, playability review B2 on PR
        /// #1219): wcid 1005490 (Toa Sigil) shipped as WeenieType Generic, so every rung clear paid
        /// exactly 1 sigil no matter the configured amount.
        ///
        /// Pure and internal (InternalsVisibleTo ACE.Server.Tests, ACE.Server.csproj:15) so this
        /// invariant is unit-testable without a live WorldObjectFactory/Player, matching
        /// Vendor.GetStackUnitValue's own precedent for a small pure accounting helper pulled out of a
        /// stateful method.
        /// </summary>
        internal static int ResolveDeliveredSize(int? actualStackSizeAfterSetStackSize)
        {
            return Math.Max(1, actualStackSizeAfterSetStackSize ?? 1);
        }

        /// <summary>
        /// The name to show for <paramref name="count"/> units of the reward currency - <paramref
        /// name="singular"/> for exactly 1, <paramref name="plural"/> for everything else (0, negative,
        /// or 2+). Pure and internal (InternalsVisibleTo ACE.Server.Tests, ACE.Server.csproj:15), matching
        /// <see cref="ResolveDeliveredSize"/>'s own precedent for a small pure accounting helper pulled out
        /// of Pay so the singular/plural choice is unit-testable without a live WorldObjectFactory/Player.
        /// </summary>
        internal static string ResolveDisplayName(int count, string singular, string plural)
        {
            return count == 1 ? singular : plural;
        }

        /// <summary>
        /// Pays this character the reward for <paramref name="questName"/>, if it names a rung. Returns
        /// silently for a quest that is not a ladder clear, while the ladder is switched off, and when the
        /// amount is at or below 0 or the currency wcid is 0 - that last pair is a legitimate "off" state,
        /// not an error, and is how a rung's reward is disabled without touching the gate.
        /// </summary>
        /// <param name="firstClear">
        /// True for the character's first-ever clear of this rung (Update/SetQuestCompletions' created-row
        /// branch, the only callers that pass the default). False for the ladder's daily repeat payout
        /// (QuestManager.HandleKillTask on a post-cooldown Restamped outcome) - same amount, different
        /// player-facing wording and log line, so a re-clear reads as a re-clear rather than a duplicate
        /// first clear.
        /// </param>
        public static void Pay(Player player, string questName, bool firstClear = true)
        {
            if (player == null)
                return;

            var rung = RungForQuestName(questName);

            if (rung == 0)
                return;

            if (!BluespireLadder.Enabled)
                return;

            var amount = PropertyManager.GetLong($"bluespire_ladder_d{rung}_reward").Item;
            var wcid = PropertyManager.GetLong("bluespire_ladder_currency_wcid").Item;

            if (amount <= 0 || wcid <= 0 || wcid > uint.MaxValue)
                return;

            var remaining = (int)Math.Min(amount, int.MaxValue);

            var delivered = 0;
            var dropped = 0;
            var objects = 0;

            // Captured off the FIRST stack created, before it is handed away/dropped/destroyed below -
            // the name and plural name are weenie-level properties fixed by the currency wcid, not by
            // what happens to any one instance, so one read is as good as any other. Left null when no
            // stack is ever successfully created (the immediate-failure arm), in which case delivered
            // stays 0 and the naming is never needed.
            string itemName = null;
            string itemPluralName = null;

            while (remaining > 0 && objects < MaxPayoutObjects)
            {
                var stack = WorldObjectFactory.CreateNewWorldObject((uint)wcid);

                if (stack == null)
                {
                    log.Error($"[BLUESPIRE] {player.Name} cleared rung {rung} but currency wcid {wcid} failed to create; " +
                              $"{remaining} of {amount} unpaid. The clear stamp has already landed, so this reward cannot be re-earned.");

                    // BREAK, NEVER RETURN. Both failure arms of this loop must leave by the same door,
                    // because the tail below is the only thing that TELLS THE PLAYER anything - including
                    // the "it lies at your feet" notice. A return here would strand a stack that earlier
                    // iterations had already dropped on the floor: really there, and with nothing to say so.
                    // The clear stamp has already landed, so there is no second chance to mention it.
                    break;
                }

                if (itemName == null)
                {
                    itemName = stack.Name;
                    itemPluralName = stack.GetPluralName();
                }

                objects++;

                // A non-stackable weenie reports MaxStackSize 1 (Stackable's constructor defaults it), and
                // SetStackSize is a no-op on anything that is not Stackable - so this loop degrades to one
                // object per unit rather than silently paying a single coin.
                var perStack = Math.Max(1, (int)(stack.MaxStackSize ?? 1));
                var requestedSize = Math.Min(remaining, perStack);

                stack.SetStackSize(requestedSize);

                // NEVER TRUST requestedSize AFTER THIS POINT - see ResolveDeliveredSize's own doc.
                var actualSize = ResolveDeliveredSize(stack.StackSize);

                if (player.TryCreateInInventoryWithNetworking(stack))
                    delivered += actualSize;
                else if (TryDropAtFeet(player, stack))
                    dropped += actualSize;
                else
                {
                    stack.Destroy();

                    log.Error($"[BLUESPIRE] {player.Name} cleared rung {rung} but a stack of {actualSize} {(uint)wcid} " +
                              "could not be given OR dropped; it is lost. The clear stamp has already landed.");
                    break;
                }

                remaining -= actualSize;
            }

            if (remaining > 0)
                log.Error($"[BLUESPIRE] {player.Name} rung {rung} payout stopped {remaining} short of {amount} " +
                          $"after {objects} object(s) - check that currency wcid {wcid} is stackable with a sane MaxStackSize.");

            if (delivered > 0)
            {
                var tributeLine = firstClear
                    ? "This tribute is paid only once."
                    : "The trial ground pays its tribute again.";

                var deliveredName = ResolveDisplayName(delivered, itemName, itemPluralName);

                player.Session?.Network?.EnqueueSend(new GameMessageSystemChat(
                    $"The Bluespire yields up its due: {delivered} {deliveredName} for the {BluespireLadderGate.OrdinalWord(rung)} depth. {tributeLine}",
                    ChatMessageType.Broadcast));
            }

            if (dropped > 0)
            {
                var droppedName = ResolveDisplayName(dropped, itemName, itemPluralName);

                player.Session?.Network?.EnqueueSend(new GameMessageSystemChat(
                    $"Your pack is full, so {dropped} {droppedName} of your Bluespire tribute lies at your feet. Pick it up before you leave.",
                    ChatMessageType.Broadcast));

                log.Warn($"[BLUESPIRE] {player.Name} cleared rung {rung} with a full pack; {dropped} of {amount} " +
                         $"currency wcid {wcid} was dropped at {player.Location?.ToLOCString()}.");
            }

            log.Info($"[BLUESPIRE] {player.Name} {(firstClear ? "first-cleared" : "re-cleared")} rung {rung} ({questName}): paid {delivered}, " +
                     $"dropped {dropped}, unpaid {remaining} of {amount} (currency wcid {wcid}).");
        }

        /// <summary>
        /// Puts the stack on the ground where the character is standing - the same three writes the corpse
        /// decay pass uses when it pukes a corpse's contents onto the landblock (WorldObject_Decay). Returns
        /// false when there is no landblock to put it on, which is the caller's signal to destroy it rather
        /// than leave an orphan.
        /// </summary>
        private static bool TryDropAtFeet(Player player, WorldObject item)
        {
            var landblock = player.CurrentLandblock;

            if (landblock == null || player.Location == null)
                return false;

            try
            {
                item.Location = new ACE.Entity.Position(player.Location);
                item.Placement = ACE.Entity.Enum.Placement.Resting;

                if (!landblock.AddWorldObject(item))
                    return false;

                item.SaveBiotaToDatabase();

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[BLUESPIRE] dropping {player.Name}'s ladder tribute at their feet threw", ex);
                return false;
            }
        }
    }
}
