using System.Reflection;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// Tally of the Unburied (wcid 1006170) - a SECOND, chargeable trophy that USED TO drop alongside
    /// Aun Relaria's existing one-time CAP relic (MlRelariaTrophy, wcid 1004121). Owner ruling
    /// 2026-09-24: it no longer drops from a Relaria kill at all - MlRelariaTrophy.TryDropTrophy no
    /// longer calls into this class' drop side. The weenie, the charge side and the quest names all
    /// stay, so a Tally a player already holds keeps filling: the holder gains one charge every time
    /// THEY complete an ML treasure map (TreasureMapHandler.FinishDig, both the boss-variant and
    /// ordinary branches), and at full capacity (100) it grants a distinct permanent glow - its own
    /// quest stamp (<see cref="GlowQuestName"/>) and its own PlayScript, never
    /// <see cref="Player_RelariaAura"/>'s - and consumes itself.
    ///
    /// RECOGNIZED BY A FIXED WCID, NOT BY A MARKER PROPERTY. ACE.Server.Entity.KillFillVessel
    /// deliberately carries no wcid, creature type or landblock literal so one C# class can serve many
    /// differently-scoped vessels; this trophy has no such variety (there is only ever one Tally, and
    /// what fills it is "a map was completed", not a kill against a named creature type), so it is
    /// simplest and clearest to key recognition on <see cref="TrophyWcid"/> directly. The counter shape
    /// - Structure counting up to MaxStructure as capacity - IS the same shape KillFillVessel uses, and
    /// <see cref="KillFillVessel.NextCharges"/> is reused verbatim for the clamp rather than copied.
    /// </summary>
    public static class MlRelariaChargeTrophy
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Content/sql/weenies/1006170 Tally of the Unburied.sql.</summary>
        public const uint TrophyWcid = 1006170;

        /// <summary>
        /// The per-character quest registry row stamped the moment a Tally reaches 100 charges, BEFORE
        /// the glow itself is armed. Read by <see cref="TryAddMapCharge"/> to divert an already-completed
        /// character's stray Tally to DestroyStrayTrophies instead of charging it, and read by
        /// <see cref="Player_RelariaTallyGlow.HasRelariaTallyGlow"/> to gate the pulse.
        /// </summary>
        public const string CompleteQuestName = "MlRelariaTallyComplete";

        /// <summary>
        /// The per-character quest registry row that arms the permanent glow pulse. Stamped only after
        /// <see cref="CompleteQuestName"/> is confirmed to have actually taken (see the mule re-check in
        /// <see cref="TryAddMapCharge"/>), so a glow is never armed without the completion also having
        /// landed. Distinct from MlRelariaTrophy.RepeatAuraQuestName - this is a different reward with a
        /// different PlayScript, not a second way to earn the same one.
        /// </summary>
        public const string GlowQuestName = "MlRelariaTallyGlow";

        // ---- pure decision -------------------------------------------------------------------------

        /// <summary>What TryAddMapCharge does with a successfully consumed map, over already-read values.</summary>
        public enum MapChargeAction
        {
            /// <summary>Nothing to do: the player carries no chargeable Tally.</summary>
            None,

            /// <summary>The player already earned the glow (<see cref="GlowQuestName"/>) and has nothing
            /// left to fill a Tally toward - every Tally they hold is a stray that reached them by looting
            /// a Relaria corpse they did not land the killing blow on, and must be destroyed rather than
            /// charged.</summary>
            DestroyStray,

            /// <summary>Add one charge to the Tally the caller already found.</summary>
            Charge,
        }

        /// <summary>
        /// The whole map-charge decision over already-read values, so it is testable without a live
        /// Player. THE COMPLETED CHECK IS EVALUATED FIRST AND UNCONDITIONALLY - <paramref
        /// name="hasChargeableTrophy"/> is never even consulted when <paramref name="alreadyCompleted"/>
        /// is true, which is what makes DestroyStray win over Charge for a completed player who
        /// nonetheless still holds a (stray, un-full) Tally.
        /// </summary>
        /// <param name="alreadyCompleted">the player already carries <see cref="GlowQuestName"/></param>
        /// <param name="hasChargeableTrophy">the player carries a Tally with room (MlRelariaChargeTrophy.FindChargeable
        /// found one) - ignored when <paramref name="alreadyCompleted"/> is true</param>
        public static MapChargeAction DecideMapChargeAction(bool alreadyCompleted, bool hasChargeableTrophy)
        {
            if (alreadyCompleted)
                return MapChargeAction.DestroyStray;

            return hasChargeableTrophy ? MapChargeAction.Charge : MapChargeAction.None;
        }

        /// <summary>
        /// The first Tally of the Unburied in this container that still has room, searching side packs
        /// too - the same reach KillFillVessel.FindFillable uses, and for the identical reason: a player
        /// working out of a side pouch must not silently get nothing.
        /// </summary>
        public static WorldObject FindChargeable(Container container)
        {
            if (container == null)
                return null;

            foreach (var item in container.Inventory.Values)
            {
                if (IsChargeable(item))
                    return item;
            }

            foreach (var item in container.Inventory.Values)
            {
                if (item is Container sideContainer)
                {
                    var found = FindChargeable(sideContainer);

                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        /// <summary>TRUE for a Tally of the Unburied that is not already at capacity.</summary>
        private static bool IsChargeable(WorldObject item) =>
            item != null && item.WeenieClassId == TrophyWcid && (item.Structure ?? 0) < (item.MaxStructure ?? 0);

        // ---- runtime: charge side ------------------------------------------------------------------

        /// <summary>
        /// Adds one charge to <paramref name="player"/>'s Tally of the Unburied, if they carry one, on
        /// every treasure map they successfully consume - called from TreasureMapHandler.FinishDig right
        /// after each of its two consume calls succeeds (the boss-variant branch and the ordinary payout
        /// branch), wrapped there in a try/catch so a failure here never costs the player their map or
        /// their payout.
        ///
        /// THE COMPLETED CHECK RUNS FIRST, BEFORE ANY CHARGE WRITE. A player who already carries
        /// <see cref="GlowQuestName"/> has nothing left to fill a Tally toward - the glow is a one-time
        /// terminal reward - so this destroys every Tally they hold and stops, rather than charging one.
        /// This closes a route a killer-only drop gate could not: a second player can loot a Relaria
        /// corpse after HalfLife or via fellowship loot sharing (Corpse.cs), so a completed character can
        /// end up holding an extra, permanently-bonded Tally nobody meant to hand them directly. Without
        /// this check that extra Tally would fill over 100
        /// further map completions, re-stamp CompleteQuestName and GlowQuestName (harmless re-stamps, but
        /// only by luck of QuestManager.Stamp being idempotent) and re-send the completion line for a
        /// reward the player already has - and, since it is Bonded, they could never simply drop it to
        /// get rid of it.
        ///
        /// ORDER MATTERS FOR THE REST TOO: the charge is written and, only once it reaches capacity, the
        /// completion quest is stamped; ONLY THEN is that stamp re-read back (the QuestManager.Update mule
        /// guard silently drops the write for a mule character - QuestManager.cs's MuleBlocked check), and
        /// the glow is armed and the trophy consumed ONLY if that re-read confirms the stamp actually
        /// took. A mule therefore keeps a full, unconsumed Tally rather than silently losing it to a stamp
        /// that never happened - better a mule holds a maxed-out trophy forever than a normal character's
        /// identical item vanishes for a reason invisible from here.
        /// </summary>
        public static void TryAddMapCharge(Player player)
        {
            var alreadyCompleted = player.QuestManager.HasQuest(GlowQuestName);
            var trophy = alreadyCompleted ? null : FindChargeable(player);

            var action = DecideMapChargeAction(alreadyCompleted, trophy != null);

            if (action == MapChargeAction.DestroyStray)
            {
                DestroyStrayTrophies(player);
                return;
            }

            if (action == MapChargeAction.None)
                return;

            var capacity = trophy.MaxStructure ?? 0;
            var charges = KillFillVessel.NextCharges(trophy.Structure ?? 0, capacity);

            player.UpdateProperty(trophy, PropertyInt.Structure, charges);

            if (charges < capacity)
                return;

            player.QuestManager.Stamp(CompleteQuestName);

            if (!player.QuestManager.HasQuest(CompleteQuestName))
            {
                // Mule guard (QuestManager.Update -> MuleBlocked): the stamp silently did not take. Leave
                // the Tally full and unconsumed rather than granting a glow that was never actually earned,
                // or destroying an item whose completion was never recorded.
                log.Warn($"[ML_TREASURE] {player.Name}'s Tally of the Unburied (0x{trophy.Guid.Full:X8}) reached {capacity} charges but {CompleteQuestName} did not stamp (mule?); leaving it full and unconsumed");
                return;
            }

            player.QuestManager.Stamp(GlowQuestName);
            player.ArmRelariaTallyGlowPulseIfEligible();

            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(
                "The tally drinks the last of what you have unearthed and crumbles to dust. The Unburied's light is yours now.",
                ChatMessageType.Broadcast));

            player.TryConsumeFromInventoryWithNetworking(trophy, 1);
        }

        /// <summary>
        /// Destroys every Tally of the Unburied <paramref name="player"/> holds (main pack and side packs
        /// - GetInventoryItemsOfWCID already recurses into both) and sends the refusal chat line once,
        /// regardless of how many were destroyed. Called only from <see cref="TryAddMapCharge"/>'s
        /// already-completed guard, for a Tally that reached a completed character through a route
        /// nobody meant to hand them directly (looting a Relaria corpse the character did not land the
        /// killing blow on - HalfLife or fellowship loot sharing, Corpse.cs). Uses
        /// TryConsumeFromInventoryWithNetworking rather than WorldObject.Destroy directly, so the removal
        /// is reflected to the client (GameMessageInventoryRemoveObject, encumbrance update) exactly as an
        /// ordinary consume is - a raw Destroy() would desync the client's pack view.
        /// </summary>
        private static void DestroyStrayTrophies(Player player)
        {
            var held = player.GetInventoryItemsOfWCID(TrophyWcid);

            if (held.Count == 0)
                return;

            foreach (var extra in held)
                player.TryConsumeFromInventoryWithNetworking(extra, 1);

            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(
                "The tally crumbles in your hands. The Unburied's light is already yours.",
                ChatMessageType.Broadcast));

            log.Info($"[ML_TREASURE] {player.Name} already holds {GlowQuestName}; destroyed {held.Count} stray Tally(s) of the Unburied rather than charging one");
        }
    }
}
