using System.Reflection;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// The Aun Relaria CAP trophy drop (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Trophy",
    /// step 11). A dug-up Relaria leaves one trophy on its corpse the FIRST time a character kills it.
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
        /// <param name="killedByPlayer">true only for a non-Olthoi player killer, matching the map drop</param>
        /// <param name="alreadyClaimed">the killer already carries the <see cref="ClaimQuestName"/> stamp</param>
        /// <param name="killerHoldsTrophy">the killer already has an unspent trophy in their inventory;
        /// without this clause a player who banked the first trophy instead of using it would collect one
        /// per kill, which owner decision 14 rules out</param>
        public static bool ShouldDropTrophy(int bossMarker, bool killedByPlayer, bool alreadyClaimed, bool killerHoldsTrophy)
        {
            if (bossMarker <= 0)
                return false;

            if (!killedByPlayer)
                return false;

            if (alreadyClaimed)
                return false;

            if (killerHoldsTrophy)
                return false;

            return true;
        }

        /// <summary>
        /// What a repeat kill's aura roll produces, over already-read values so it is testable without a
        /// live Player or PropertyManager. Called only for the genuine repeat-kill case (the killer has
        /// already claimed the one-time trophy) - the guaranteed doubloon payout itself is unconditional
        /// and does not go through this decision.
        /// </summary>
        public enum RepeatKillAuraOutcome
        {
            /// <summary>The roll failed. No aura, no bonus doubloons - just the base repeat-kill payout.</summary>
            NoChange,

            /// <summary>The roll succeeded and the killer did not already have the aura: stamp it.</summary>
            GrantAura,

            /// <summary>The roll succeeded but the killer already has the aura: pay bonus doubloons instead
            /// of stamping a quest row that is already stamped.</summary>
            BonusDoubloons,
        }

        /// <param name="auraChance">ml_relaria_repeat_aura_chance</param>
        /// <param name="roll">a uniform draw in [0, 1) - ThreadSafeRandom.Next(0.0f, 1.0f)</param>
        /// <param name="alreadyHasAura">the killer already carries <see cref="RepeatAuraQuestName"/></param>
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
        /// Puts one trophy on <paramref name="corpse"/> when <paramref name="dying"/> was a dug-up Relaria
        /// and the crediting player has neither claimed nor already holds one. Called from
        /// Creature_Death.CreateCorpse, inside the same Marae Lassel gate as the map drop, so a death
        /// anywhere else never reaches this file.
        ///
        /// <paramref name="killer"/> is CreateCorpse's lootKiller: the top damager, resolved through
        /// DamageHistoryInfo.ResolvePetOwnerAsKiller so a pet kill already reads as its owner's here (both
        /// the killedByPlayer test below and TryGetPetOwnerOrAttacker see the owner directly) - the same
        /// player the corpse's own looting rights are keyed to, so the trophy lands on a corpse that player
        /// may actually open.
        /// </summary>
        public static void TryDropTrophy(Creature dying, Corpse corpse, DamageHistoryInfo killer)
        {
            // First and cheapest: only a creature the dig spawned carries the marker. An ordinary Marae
            // Lassel kill stops here on one property-table read, already behind the realm/box gate at the
            // call site.
            var bossMarker = dying.GetProperty(MlRelariaSpawner.BossMarker) ?? 0;

            if (bossMarker <= 0)
                return;

            // Same "killed by a player" test the map drop uses: killer non-null, a player, not an Olthoi
            // player (an Olthoi kill takes GenerateTreasure_Olthoi rather than ordinary treasure).
            var killedByPlayer = killer != null && killer.IsPlayer && !killer.IsOlthoiPlayer;

            if (!killedByPlayer)
                return;

            // TryGetPetOwnerOrAttacker, not TryGetAttacker: killer's PetOwner is already null here (it was
            // resolved to the owner's own DamageHistoryInfo upstream, by CreateCorpse's lootKiller, before
            // this ever ran), so this always falls through to TryGetAttacker and simply returns that player.
            // Kept rather than switched to TryGetAttacker so this still degrades safely if TryDropTrophy is
            // ever called with an unresolved (raw pet) killer.
            if (!(killer.TryGetPetOwnerOrAttacker() is Player killerPlayer))
            {
                log.Warn($"[ML_TREASURE] Relaria (0x{dying.Guid.Full:X8}) was killed by {killer.Name} (0x{killer.Guid.Full:X8}), which reports IsPlayer but no longer resolves to a live Player; no trophy dropped");
                return;
            }

            // Tally of the Unburied (MlRelariaChargeTrophy) no longer drops here - owner ruling
            // 2026-09-24. It used to be a SECOND, independent trophy dropped in addition to this relic on
            // every kill; that call is removed, but the weenie, its charge-side code (TryAddMapCharge,
            // FindChargeable) and its quest names stay, so a Tally a player already holds keeps working.

            var alreadyClaimed = killerPlayer.QuestManager.HasQuest(ClaimQuestName);
            var killerHoldsTrophy = killerPlayer.GetInventoryItemsOfWCID(TrophyWcid).Count > 0;

            if (!ShouldDropTrophy(bossMarker, killedByPlayer, alreadyClaimed, killerHoldsTrophy))
            {
                log.Info($"[ML_TREASURE] {killerPlayer.Name} killed Relaria (0x{dying.Guid.Full:X8}) but no trophy dropped (alreadyClaimed={alreadyClaimed}, holdsTrophy={killerHoldsTrophy})");

                // A genuine repeat kill (the killer already claimed the one-time trophy, not merely
                // banking a spare one) now pays the repeat-kill rewards below. killerHoldsTrophy alone
                // never reaches here - that killer has not claimed yet and is still on the first-kill path.
                if (alreadyClaimed)
                    TryAwardRepeatKillRewards(dying, corpse, killerPlayer);

                return;
            }

            var trophy = WorldObjectFactory.CreateNewWorldObject(TrophyWcid);

            if (trophy == null)
            {
                log.Error($"[ML_TREASURE] trophy wcid {TrophyWcid} failed to create for {killerPlayer.Name}; is Content/sql/weenies/1004121 Relic of the Unburied.sql applied to this world database?");
                return;
            }

            // The real corpse-add pattern in this codebase (Creature_Death.GenerateTreasure). There is no
            // TryAddToCorpse - the reference design's call by that name does not exist here
            // (TREASURE-HUNT-PLAN.md section 5).
            if (!corpse.TryAddToInventory(trophy))
            {
                log.Warn($"[ML_TREASURE] could not add the Relaria trophy 0x{trophy.Guid.Full:X8} to the corpse of {corpse.Name} (0x{corpse.Guid.Full:X8}); trophy destroyed");
                trophy.Destroy();
                return;
            }

            log.Info($"[ML_TREASURE] {killerPlayer.Name} killed Relaria (0x{dying.Guid.Full:X8}); trophy 0x{trophy.Guid.Full:X8} placed on the corpse");
        }

        /// <summary>
        /// Repeat-kill rewards for a killer who has already claimed the one-time CAP trophy: a guaranteed
        /// handful of ML Doubloons (ml_relaria_repeat_doubloons, unconditional) plus a low-chance roll
        /// (ml_relaria_repeat_aura_chance) at the permanent aura. On a successful roll for a character who
        /// already has the aura, the roll pays bonus doubloons instead - see
        /// <see cref="DecideRepeatKillAuraOutcome"/>.
        /// </summary>
        private static void TryAwardRepeatKillRewards(Creature dying, Corpse corpse, Player killerPlayer)
        {
            var doubloonCount = (int)System.Math.Clamp(PropertyManager.GetLong("ml_relaria_repeat_doubloons", DefaultRepeatDoubloons).Item, 0, int.MaxValue);

            if (doubloonCount > 0)
                AddDoubloonsToCorpse(corpse, doubloonCount, killerPlayer, dying);

            var auraChance = PropertyManager.GetDouble("ml_relaria_repeat_aura_chance", DefaultRepeatAuraChance).Item;
            var roll = ThreadSafeRandom.Next(0.0f, 1.0f);
            var alreadyHasAura = killerPlayer.QuestManager.HasQuest(RepeatAuraQuestName);

            var outcome = DecideRepeatKillAuraOutcome(auraChance, roll, alreadyHasAura);

            switch (outcome)
            {
                case RepeatKillAuraOutcome.GrantAura:
                    killerPlayer.QuestManager.Stamp(RepeatAuraQuestName);
                    killerPlayer.ArmRelariaAuraPulseIfEligible();
                    killerPlayer.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        "The Unburied's light clings to you. It will not fade.", ChatMessageType.Broadcast));
                    log.Info($"[ML_TREASURE] {killerPlayer.Name} repeat-killed Relaria (0x{dying.Guid.Full:X8}) and rolled the aura ({RepeatAuraQuestName} stamped)");
                    break;

                case RepeatKillAuraOutcome.BonusDoubloons:
                    if (doubloonCount > 0)
                        AddDoubloonsToCorpse(corpse, doubloonCount, killerPlayer, dying);
                    killerPlayer.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        "The light already clings to you; the Unburied leaves a richer offering instead.", ChatMessageType.Broadcast));
                    log.Info($"[ML_TREASURE] {killerPlayer.Name} repeat-killed Relaria (0x{dying.Guid.Full:X8}), rolled the aura again but already has it; paid {doubloonCount} bonus doubloons instead");
                    break;

                case RepeatKillAuraOutcome.NoChange:
                    break;
            }
        }

        /// <summary>
        /// Adds <paramref name="count"/> ML Doubloons (TreasureMapHandler.TreasureCurrencyWcid) to
        /// <paramref name="corpse"/>, the same corpse-add pattern <see cref="TryDropTrophy"/> uses for the
        /// trophy itself.
        ///
        /// wcid 1004101 is not yet a Stackable weenie on this branch (another branch is converting it -
        /// WaffleACE feature/doubloon-stackable at the time this was written); SetStackSize is a silent
        /// no-op on a non-Stackable object, so a single created item would pay 1 doubloon instead of
        /// <paramref name="count"/>. This checks `is Stackable` and falls back to creating
        /// <paramref name="count"/> separate items when it is not, so the payout is correct either way and
        /// this code needs no follow-up edit once the other branch merges.
        /// </summary>
        private static void AddDoubloonsToCorpse(Corpse corpse, int count, Player killerPlayer, Creature dying)
        {
            if (count <= 0)
                return;

            var first = WorldObjectFactory.CreateNewWorldObject(TreasureMapHandler.TreasureCurrencyWcid);

            if (first == null)
            {
                log.Error($"[ML_TREASURE] {killerPlayer.Name}'s Relaria repeat-kill (0x{dying.Guid.Full:X8}) doubloon payout failed: wcid {TreasureMapHandler.TreasureCurrencyWcid} would not create");
                return;
            }

            if (first is Stackable)
            {
                first.SetStackSize(count);

                if (!corpse.TryAddToInventory(first))
                {
                    log.Warn($"[ML_TREASURE] could not add {count} doubloons (0x{first.Guid.Full:X8}) to the corpse of {corpse.Name} (0x{corpse.Guid.Full:X8}); destroyed");
                    first.Destroy();
                }

                return;
            }

            // Non-Stackable fallback: one item already created above, plus (count - 1) more.
            if (!corpse.TryAddToInventory(first))
            {
                log.Warn($"[ML_TREASURE] could not add a doubloon (0x{first.Guid.Full:X8}) to the corpse of {corpse.Name} (0x{corpse.Guid.Full:X8}); destroyed");
                first.Destroy();
            }

            for (var i = 1; i < count; i++)
            {
                var extra = WorldObjectFactory.CreateNewWorldObject(TreasureMapHandler.TreasureCurrencyWcid);

                if (extra == null)
                {
                    log.Error($"[ML_TREASURE] {killerPlayer.Name}'s Relaria repeat-kill (0x{dying.Guid.Full:X8}) doubloon payout: wcid {TreasureMapHandler.TreasureCurrencyWcid} would not create on item {i + 1} of {count}");
                    continue;
                }

                if (!corpse.TryAddToInventory(extra))
                {
                    log.Warn($"[ML_TREASURE] could not add a doubloon (0x{extra.Guid.Full:X8}) to the corpse of {corpse.Name} (0x{corpse.Guid.Full:X8}); destroyed");
                    extra.Destroy();
                }
            }
        }
    }
}
