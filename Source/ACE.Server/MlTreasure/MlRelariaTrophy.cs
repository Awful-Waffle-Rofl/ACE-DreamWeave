using System;
using System.Reflection;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// The Aun Relaria CAP trophy drop (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Trophy",
    /// step 11). A dug-up Relaria pays one trophy the FIRST time a character's dig is killed.
    ///
    /// WHO IS CREDITED (owner decision, Discord "ML Aun Relaria Map loot"): the player who DUG the map -
    /// recorded on the boss at spawn (<see cref="MlRelariaSpawner.DiggerGuidProperty"/>) - never the top
    /// damager and never the fellowship. Every "the character" below means the digger. When the digger is
    /// also the corpse's loot killer the reward goes on the corpse as before; when someone else out-damaged
    /// the digger it goes straight into the digger's inventory, since they cannot loot that corpse. Who killed the
    /// boss does not gate payment (owner decision "Pay digger anyway"): a monster, an ownerless pet, an Olthoi
    /// player or another player as top damager all pay the digger by inventory. A boss
    /// with no recorded digger (admin spawn, or one alive from before this change) or whose digger is
    /// offline pays no trophy, doubloons or aura. XP and luminance are untouched.
    ///
    /// Owner decisions 14 and 16 originally fixed every repeat kill to pay XP and luminance ONLY. That
    /// has since been revised (tester feedback, 2026-09-18): a repeat kill by a character who has
    /// already claimed the CAP trophy now ALSO pays a modest guaranteed handful of ML Doubloons
    /// (ml_relaria_repeat_doubloons) and rolls a low chance (ml_relaria_repeat_aura_chance) at a
    /// permanent cosmetic aura on the PLAYER - see the "repeat kill" section below and Player_RelariaAura.
    /// There is still never a second trophy: the boss weenie carries no DeathTreasureType and no
    /// create-list rows, and none may be added; XP and luminance still come from its XpOverride/
    /// LuminanceAward, which the ordinary death path already pays.
    ///
    /// The one-time gate itself lives on the CONSUME side, in the class-ability grant funnel
    /// (Player_ClassAbilityClaim), because that is where the point is actually paid. The drop-side check
    /// here is the same quest stamp read from the other end: a character who has already claimed never
    /// sees a second trophy at all, so the refused-second-trophy case is a fallback rather than the
    /// normal experience.
    /// </summary>
    public static class MlRelariaTrophy
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Content/sql/weenies/1004121 Relic of the Unburied.sql - a WeenieType.Gem carrying PropertyInt
        /// 9020 ClassAbilityPointValue 1 (the existing audited CAP funnel) and PropertyString 9017
        /// ClassAbilityGrantQuest (<see cref="ClaimQuestName"/>), which is what makes the grant one-time.
        /// </summary>
        public const uint TrophyWcid = 1004121;

        /// <summary>
        /// The per-character quest registry row that records the claim. One name, read on the drop side
        /// here and written on the consume side by Player.TryClaimOneTimeClassAbilityGrant, so the two
        /// halves cannot disagree. It must equal the PropertyString 9017 value authored on wcid 1004121.
        /// </summary>
        public const string ClaimQuestName = "MlRelariaTrophy";

        /// <summary>
        /// The per-character quest registry row that records a successful roll of the repeat-kill aura
        /// (see <see cref="DecideRepeatKillAuraOutcome"/>). Never cleared once stamped. Read by
        /// Player.HasRelariaAura (Player_RelariaAura.cs), which gates the aura pulse.
        /// </summary>
        public const string RepeatAuraQuestName = "MlRelariaAura";

        /// <summary>ml_relaria_repeat_doubloons default: doubloons paid on every repeat kill, and again
        /// (as a bonus) when the aura roll succeeds but the character already has the aura.</summary>
        public const long DefaultRepeatDoubloons = 3;

        /// <summary>ml_relaria_repeat_aura_chance default: a uniform draw below this wins the aura roll.</summary>
        public const double DefaultRepeatAuraChance = 0.05;

        // ---- pure decision -------------------------------------------------------------------------

        /// <summary>
        /// The whole trophy-drop decision over already-read values, so every clause is testable without a
        /// live Player, a Corpse or a world database. This is what the runtime path calls.
        /// </summary>
        /// <param name="bossMarker">PropertyInt 9066 off the dying creature - <see cref="MlRelariaSpawner.BossMarker"/>,
        /// stamped only by the dig spawn, so an ordinary ML creature reads 0 here</param>
        /// <param name="alreadyClaimed">the digger already carries the <see cref="ClaimQuestName"/> stamp</param>
        /// <param name="killerHoldsTrophy">the digger already has an unspent trophy in their inventory;
        /// without this clause a player who banked the first trophy instead of using it would collect one
        /// per kill, which owner decision 14 rules out</param>
        public static bool ShouldDropTrophy(int bossMarker, bool alreadyClaimed, bool killerHoldsTrophy)
        {
            if (bossMarker <= 0)
                return false;

            if (alreadyClaimed)
                return false;

            if (killerHoldsTrophy)
                return false;

            return true;
        }

        /// <summary>Who receives a Relaria reward and how it reaches them. See <see cref="DecideRecipient"/>.</summary>
        public enum RewardRecipient
        {
            /// <summary>No recorded digger: nobody is credited and nothing is awarded.</summary>
            None,

            /// <summary>The digger is also the corpse's loot killer: items go on the corpse, as always.</summary>
            DiggerViaCorpse,

            /// <summary>Someone else took the loot-killer slot: items go straight into the digger's inventory,
            /// because the digger may not be able to open that corpse.</summary>
            DiggerViaInventory,
        }

        /// <summary>
        /// Who is credited and by which route, over already-read guids so it is testable without a live
        /// Player. The credited player is ALWAYS the digger, whatever <paramref name="lootKillerGuid"/> is;
        /// the loot killer only chooses the delivery route, and a loot killer who is null, a non-player or an
        /// Olthoi player is simply "not the digger" and so takes the inventory route. This is what the runtime path calls.
        /// </summary>
        /// <param name="diggerGuid">the character guid recorded on the boss at spawn, null/0 when none</param>
        /// <param name="lootKillerGuid">the guid of CreateCorpse's lootKiller (the top damager), null when none</param>
        public static RewardRecipient DecideRecipient(uint? diggerGuid, uint? lootKillerGuid)
        {
            if (!diggerGuid.HasValue || diggerGuid.Value == 0)
                return RewardRecipient.None;

            return lootKillerGuid == diggerGuid ? RewardRecipient.DiggerViaCorpse : RewardRecipient.DiggerViaInventory;
        }

        /// <summary>
        /// Resolves the credited digger over delegates, so which lookup fires for which (digger, loot killer)
        /// pair is testable without a live Player. Corpse route: the digger IS the loot killer, so
        /// <paramref name="killerResolver"/> supplies them and the online lookup is never touched. Inventory
        /// route: only <paramref name="onlineLookup"/> is consulted, by the digger's guid. No digger: neither
        /// fires. A null result with a non-None recipient means the digger is offline or unresolvable.
        /// </summary>
        public static T ResolveDigger<T>(uint? diggerGuid, uint? lootKillerGuid, Func<T> killerResolver,
            Func<uint, T> onlineLookup, out RewardRecipient recipient) where T : class
        {
            recipient = DecideRecipient(diggerGuid, lootKillerGuid);

            switch (recipient)
            {
                case RewardRecipient.DiggerViaCorpse:
                    return killerResolver();
                case RewardRecipient.DiggerViaInventory:
                    return onlineLookup(diggerGuid.Value);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Runs the reward application on the right thread for the route: inline with toCorpse=true for the
        /// corpse route, queued on the digger's own action queue with toCorpse=false for the inventory route.
        /// </summary>
        public static void RouteRewards(RewardRecipient recipient, Action<bool> apply, Action<Action> enqueueOnDigger)
        {
            switch (recipient)
            {
                case RewardRecipient.DiggerViaCorpse:
                    apply(true);
                    break;
                case RewardRecipient.DiggerViaInventory:
                    enqueueOnDigger(() => apply(false));
                    break;
            }
        }

        /// <summary>What a death pays the digger: the one-time trophy, the repeat-kill rewards, or nothing.</summary>
        public enum RewardPath { None, Trophy, RepeatKill }

        public static RewardPath DecideRewardPath(int bossMarker, bool alreadyClaimed, bool killerHoldsTrophy)
        {
            // Not a dug-up Relaria: no reward path at all, claimed or not. WHO killed the boss is not a
            // clause here - the digger is paid whatever the top damager was (owner decision "Pay digger anyway").
            if (bossMarker <= 0)
                return RewardPath.None;

            if (ShouldDropTrophy(bossMarker, alreadyClaimed, killerHoldsTrophy))
                return RewardPath.Trophy;

            // A genuine repeat kill (the digger already claimed), not merely one banking a spare trophy.
            return alreadyClaimed ? RewardPath.RepeatKill : RewardPath.None;
        }

        public enum DeliveryResult { Corpse, Inventory, Ground, Failed }

        /// <summary>
        /// Where one reward item ends up, over delegates. Corpse route: corpse or fail. Inventory route:
        /// pack, else the ground at the digger's feet, else fail.
        /// </summary>
        public static DeliveryResult DeliverVia(bool toCorpse, Func<bool> addToCorpse, Func<bool> addToInventory, Func<bool> dropAtFeet)
        {
            if (toCorpse)
                return addToCorpse() ? DeliveryResult.Corpse : DeliveryResult.Failed;

            if (addToInventory())
                return DeliveryResult.Inventory;

            return dropAtFeet() ? DeliveryResult.Ground : DeliveryResult.Failed;
        }

        /// <summary>
        /// What a repeat kill's aura roll produces, over already-read values so it is testable without a
        /// live Player or PropertyManager. Called only for the genuine repeat-kill case (the digger has
        /// already claimed the one-time trophy) - the guaranteed doubloon payout itself is unconditional
        /// and does not go through this decision.
        /// </summary>
        public enum RepeatKillAuraOutcome
        {
            /// <summary>The roll failed. No aura, no bonus doubloons - just the base repeat-kill payout.</summary>
            NoChange,

            /// <summary>The roll succeeded and the digger did not already have the aura: stamp it.</summary>
            GrantAura,

            /// <summary>The roll succeeded but the digger already has the aura: pay bonus doubloons instead
            /// of stamping a quest row that is already stamped.</summary>
            BonusDoubloons,
        }

        /// <param name="auraChance">ml_relaria_repeat_aura_chance</param>
        /// <param name="roll">a uniform draw in [0, 1) - ThreadSafeRandom.Next(0.0f, 1.0f)</param>
        /// <param name="alreadyHasAura">the digger already carries <see cref="RepeatAuraQuestName"/></param>
        public static RepeatKillAuraOutcome DecideRepeatKillAuraOutcome(double auraChance, double roll, bool alreadyHasAura)
        {
            // Same "a chance of 0 must never fire, whatever the draw returns" guard MlTreasureDrop.ShouldDrop
            // uses - a roll of exactly 0.0 is a value ThreadSafeRandom.Next(0.0f, 1.0f) can return.
            if (auraChance <= 0.0)
                return RepeatKillAuraOutcome.NoChange;

            if (!(roll < auraChance))
                return RepeatKillAuraOutcome.NoChange;

            return alreadyHasAura ? RepeatKillAuraOutcome.BonusDoubloons : RepeatKillAuraOutcome.GrantAura;
        }

        // ---- runtime -------------------------------------------------------------------------------

        /// <summary>
        /// Pays the Relaria rewards to the player who DUG the map when <paramref name="dying"/> was a dug-up
        /// Relaria. Called from Creature_Death.CreateCorpse, inside the same Marae Lassel gate as the map
        /// drop, so a death anywhere else never reaches this file.
        ///
        /// <paramref name="killer"/> is CreateCorpse's lootKiller: the top damager, resolved through
        /// DamageHistoryInfo.ResolvePetOwnerAsKiller. It is NOT who is credited - that is the digger recorded
        /// at spawn - it only decides the route (<see cref="DecideRecipient"/>): the digger's own corpse
        /// when they are the loot killer, otherwise their inventory, because a corpse keyed to somebody else
        /// is one the digger cannot loot.
        /// </summary>
        public static void TryDropTrophy(Creature dying, Corpse corpse, DamageHistoryInfo killer)
        {
            // First and cheapest: only a creature the dig spawned carries the marker. An ordinary Marae
            // Lassel kill stops here on one property-table read, already behind the realm/box gate at the
            // call site.
            var bossMarker = dying.GetProperty(MlRelariaSpawner.BossMarker) ?? 0;

            if (bossMarker <= 0)
                return;

            // No killer gate (owner decision "Pay digger anyway"): the top damager may be a monster, a pet with
            // no player owner, an Olthoi player or another player, or nobody at all, and the recorded digger is
            // paid regardless. A loot killer who is not the digger only ever selects the inventory route.
            var diggerGuid = dying.GetProperty(MlRelariaSpawner.DiggerGuidProperty);

            // Corpse route: the digger is the loot killer, resolved exactly as before (TryGetPetOwnerOrAttacker
            // degrades safely if called with an unresolved raw pet killer). Inventory route: the digger is
            // somebody else, so look them up online by guid.
            var digger = ResolveDigger(diggerGuid, killer?.Guid.Full,
                () => killer?.TryGetPetOwnerOrAttacker() as Player,
                guid => PlayerManager.GetOnlinePlayer(guid),
                out var recipient);

            if (recipient == RewardRecipient.None)
            {
                log.Info($"[ML_TREASURE] Relaria (0x{dying.Guid.Full:X8}) died with no recorded digger (admin spawn or spawned before digger tracking); no rewards awarded");
                return;
            }

            if (digger == null)
            {
                log.Warn($"[ML_TREASURE] Relaria (0x{dying.Guid.Full:X8}) died; its digger 0x{diggerGuid.Value:X8} is offline or not found (killer {killer?.Name ?? "none"} 0x{killer?.Guid.Full ?? 0:X8}); no rewards awarded");
                return;
            }

            // Inventory route: the digger is not the corpse's owner and may be on another landblock thread,
            // so all their reads and writes run on their own action queue.
            RouteRewards(recipient,
                toCorpse => ApplyRewards(dying, corpse, digger, bossMarker, toCorpse),
                run =>
                {
                    var chain = new ActionChain();
                    chain.AddAction(digger, run);
                    chain.EnqueueChain();
                });
        }

        private static void ApplyRewards(Creature dying, Corpse corpse, Player digger, int bossMarker, bool toCorpse)
        {
            // Tally of the Unburied (MlRelariaChargeTrophy) no longer drops here - owner ruling
            // 2026-09-24. It used to be a SECOND, independent trophy dropped in addition to this relic on
            // every kill; that call is removed, but the weenie, its charge-side code (TryAddMapCharge,
            // FindChargeable) and its quest names stay, so a Tally a player already holds keeps working.

            var alreadyClaimed = digger.QuestManager.HasQuest(ClaimQuestName);
            var killerHoldsTrophy = digger.GetInventoryItemsOfWCID(TrophyWcid).Count > 0;

            var path = DecideRewardPath(bossMarker, alreadyClaimed, killerHoldsTrophy);

            if (path != RewardPath.Trophy)
            {
                log.Info($"[ML_TREASURE] Relaria (0x{dying.Guid.Full:X8}) died; digger {digger.Name} gets no trophy (alreadyClaimed={alreadyClaimed}, holdsTrophy={killerHoldsTrophy})");

                if (path == RewardPath.RepeatKill)
                    TryAwardRepeatKillRewards(dying, corpse, digger, toCorpse);

                return;
            }

            var trophy = WorldObjectFactory.CreateNewWorldObject(TrophyWcid);

            if (trophy == null)
            {
                log.Error($"[ML_TREASURE] trophy wcid {TrophyWcid} failed to create for {digger.Name}; is Content/sql/weenies/1004121 Relic of the Unburied.sql applied to this world database?");
                return;
            }

            if (Deliver(trophy, corpse, digger, toCorpse, dying))
                log.Info($"[ML_TREASURE] {digger.Name} Relaria (0x{dying.Guid.Full:X8}) died; trophy 0x{trophy.Guid.Full:X8} delivered ({(toCorpse ? "corpse" : "inventory or feet")})");
        }

        /// <summary>
        /// Puts one reward item where <see cref="DecideRecipient"/> routed it (<see cref="DeliverVia"/>).
        /// Corpse: the real corpse-add pattern (Creature_Death.GenerateTreasure; there is no TryAddToCorpse).
        /// Inventory: Player.TryCreateInInventoryWithNetworking with no burden check (parity with ThreadEncounterPrize, ruling 2026-11-09: an over-burdened digger with a free slot still gets the item in pack); when the pack has no room, the item is
        /// dropped at the digger's feet through Creature.DropLootInWorld, the corpse-overflow helper the
        /// Thread encounter prize also uses. That helper takes a generator guid, not a pickup restriction,
        /// so the ground drop is not owner-locked. The digger is told in chat which of the three happened.
        /// Only when even the ground refuses is the item destroyed and logged.
        /// </summary>
        private static bool Deliver(WorldObject item, Corpse corpse, Player digger, bool toCorpse, Creature dying)
        {
            var stack = item.StackSize ?? 1;
            var itemName = item.GetNameWithMaterial(stack);
            var stackText = stack != 1 ? $"{stack:N0} " : "";

            var result = DeliverVia(toCorpse,
                () => corpse.TryAddToInventory(item),
                () => digger.TryCreateInInventoryWithNetworking(item, out _, burdenCheck: false),
                () => digger.Location != null && Creature.DropLootInWorld(new[] { item }, digger.Location, digger.Guid));

            switch (result)
            {
                case DeliveryResult.Corpse:
                    return true;

                case DeliveryResult.Inventory:
                    digger.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"Aun Relaria falls, and the Unburied leaves you {stackText}{itemName}.", ChatMessageType.Broadcast));
                    return true;

                case DeliveryResult.Ground:
                    log.Info($"[ML_TREASURE] digger {digger.Name}'s pack had no room for Relaria reward 0x{item.Guid.Full:X8} (Relaria 0x{dying.Guid.Full:X8}); placed at their feet");
                    digger.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"Aun Relaria falls, but you have no room to carry {stackText}{itemName}; it was placed at your feet.", ChatMessageType.Broadcast));
                    return true;

                default:
                    log.Warn($"[ML_TREASURE] could not deliver the Relaria reward 0x{item.Guid.Full:X8} ({(toCorpse ? "corpse " + corpse.Name : "digger " + digger.Name)}; Relaria 0x{dying.Guid.Full:X8}); destroyed");

                    if (!item.IsDestroyed)
                        item.Destroy();

                    if (!toCorpse)
                        digger.Session.Network.EnqueueSend(new GameMessageSystemChat(
                            $"Aun Relaria falls, but {stackText}{itemName} could not be delivered and is lost.", ChatMessageType.Broadcast));

                    return false;
            }
        }

        /// <summary>
        /// Repeat-kill rewards for a digger who has already claimed the one-time CAP trophy: a guaranteed
        /// handful of ML Doubloons (ml_relaria_repeat_doubloons, unconditional) plus a low-chance roll
        /// (ml_relaria_repeat_aura_chance) at the permanent aura. On a successful roll for a character who
        /// already has the aura, the roll pays bonus doubloons instead - see
        /// <see cref="DecideRepeatKillAuraOutcome"/>.
        /// </summary>
        private static void TryAwardRepeatKillRewards(Creature dying, Corpse corpse, Player digger, bool toCorpse)
        {
            var doubloonCount = (int)System.Math.Clamp(PropertyManager.GetLong("ml_relaria_repeat_doubloons", DefaultRepeatDoubloons).Item, 0, int.MaxValue);

            if (doubloonCount > 0)
                AddDoubloons(corpse, doubloonCount, digger, dying, toCorpse);

            var auraChance = PropertyManager.GetDouble("ml_relaria_repeat_aura_chance", DefaultRepeatAuraChance).Item;
            var roll = ThreadSafeRandom.Next(0.0f, 1.0f);
            var alreadyHasAura = digger.QuestManager.HasQuest(RepeatAuraQuestName);

            var outcome = DecideRepeatKillAuraOutcome(auraChance, roll, alreadyHasAura);

            switch (outcome)
            {
                case RepeatKillAuraOutcome.GrantAura:
                    digger.QuestManager.Stamp(RepeatAuraQuestName);
                    digger.ArmRelariaAuraPulseIfEligible();
                    digger.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        "The Unburied's light clings to you. It will not fade.", ChatMessageType.Broadcast));
                    log.Info($"[ML_TREASURE] digger {digger.Name} repeat-killed Relaria (0x{dying.Guid.Full:X8}) and rolled the aura ({RepeatAuraQuestName} stamped)");
                    break;

                case RepeatKillAuraOutcome.BonusDoubloons:
                    if (doubloonCount > 0)
                        AddDoubloons(corpse, doubloonCount, digger, dying, toCorpse);
                    digger.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        "The light already clings to you; the Unburied leaves a richer offering instead.", ChatMessageType.Broadcast));
                    log.Info($"[ML_TREASURE] digger {digger.Name} repeat-killed Relaria (0x{dying.Guid.Full:X8}), rolled the aura again but already has it; paid {doubloonCount} bonus doubloons instead");
                    break;

                case RepeatKillAuraOutcome.NoChange:
                    break;
            }
        }

        /// <summary>
        /// Delivers <paramref name="count"/> ML Doubloons (TreasureMapHandler.TreasureCurrencyWcid) through
        /// <see cref="Deliver"/>, the same route as the trophy.
        ///
        /// wcid 1004101 is not yet a Stackable weenie on this branch (another branch is converting it -
        /// WaffleACE feature/doubloon-stackable at the time this was written); SetStackSize is a silent
        /// no-op on a non-Stackable object, so a single created item would pay 1 doubloon instead of
        /// <paramref name="count"/>. This checks `is Stackable` and falls back to creating
        /// <paramref name="count"/> separate items when it is not, so the payout is correct either way and
        /// this code needs no follow-up edit once the other branch merges.
        /// </summary>
        private static void AddDoubloons(Corpse corpse, int count, Player digger, Creature dying, bool toCorpse)
        {
            if (count <= 0)
                return;

            var first = WorldObjectFactory.CreateNewWorldObject(TreasureMapHandler.TreasureCurrencyWcid);

            if (first == null)
            {
                log.Error($"[ML_TREASURE] {digger.Name}'s Relaria repeat-kill (0x{dying.Guid.Full:X8}) doubloon payout failed: wcid {TreasureMapHandler.TreasureCurrencyWcid} would not create");
                return;
            }

            if (first is Stackable)
            {
                first.SetStackSize(count);
                Deliver(first, corpse, digger, toCorpse, dying);
                return;
            }

            // Non-Stackable fallback: one item already created above, plus (count - 1) more.
            Deliver(first, corpse, digger, toCorpse, dying);

            for (var i = 1; i < count; i++)
            {
                var extra = WorldObjectFactory.CreateNewWorldObject(TreasureMapHandler.TreasureCurrencyWcid);

                if (extra == null)
                {
                    log.Error($"[ML_TREASURE] {digger.Name}'s Relaria repeat-kill (0x{dying.Guid.Full:X8}) doubloon payout: wcid {TreasureMapHandler.TreasureCurrencyWcid} would not create on item {i + 1} of {count}");
                    continue;
                }

                Deliver(extra, corpse, digger, toCorpse, dying);
            }
        }
    }
}
