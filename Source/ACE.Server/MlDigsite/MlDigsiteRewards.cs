using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Factories.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// THE one chest builder and THE one XP/luminance payer (invariant 5). Every shape pays through
    /// <see cref="Deliver"/>, and every amount it pays is scaled from ONE number,
    /// MlDigsiteRules.EncounterPayoutFraction (the Waves tier reached, the Boss Rush health removed, or the
    /// binary Corruption result), by the same MlDigsiteRules.ScaleReward rule.
    ///
    /// WHO IS PAID. The owner (the digging player) always; plus every helper who dealt damage credit above
    /// zero to this encounter's creatures (the encounter's participation ledger, pets credited to their
    /// owner), is online, and was seen alive at the site within ml_digsite_presence_window_seconds
    /// (MlDigsiteRules.SelectEligible). NOT "present at the end": a wipe ends with nobody alive at the site,
    /// and that rule would pay nobody for the fight they just lost. A passer-by who dealt no damage is paid
    /// nothing.
    ///
    /// WHAT THEY GET:
    ///   * ONE CHEST EACH, stamped with its own P_DungeonCacheOwnerGuid, so only that player can open it
    ///     (Chest.CheckUseRequirements). The owner's stands at the anchor and pays the full fraction, plus
    ///     every Kept Siraluun feather, unscaled. Each helper's is scattered near it and pays the fraction x
    ///     MlDigsiteRules.NonOwnerShare(helpers).
    ///   * XP AND LUMINANCE directly, the same amount to every paid player, owner and helper alike - the
    ///     helper share scales chests only. A FullClear pays the full amounts flat (round 15 owner ruling,
    ///     MlDigsiteRules.XpLuminanceFraction); every other outcome pays the fraction of them.
    /// </summary>
    public static class MlDigsiteRewards
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Content/sql/weenies/1004150 Unearthed Cache.sql - the digsite reward chest.</summary>
        public const uint ChestWcid = 1004150;

        /// <summary>
        /// The Marae Lassel Doubloon, the ML treasure currency. Named from MlTreasure rather than redeclared,
        /// so the encounter and the dig payout can never disagree about which coin the island trades in.
        /// </summary>
        public const uint DoubloonWcid = MlTreasure.TreasureMapHandler.TreasureCurrencyWcid;

        /// <summary>
        /// Trade Note (250,000), the "MMD". THE GENUINE RETAIL WCID, never a clone: 20630 is matched by
        /// hardcoded literal in several places in this fork, so a cloned currency would be accepted by the
        /// vendor paths that compare the wcid and silently refused by the ones that do not.
        /// </summary>
        public const uint TradeNoteWcid = 20630;

        /// <summary>Metres from the anchor a helper's chest is scattered within, so the chests do not stack.</summary>
        private const float HelperChestScatterMetres = 4.0f;

        /// <summary>One chest to build: whose it is, what share it pays, where it stands, and what else it holds.</summary>
        internal sealed class ChestPlan
        {
            public uint OwnerGuid;
            public bool IsDigger;
            public double Fraction;
            public Position Position;
            public IReadOnlyList<PropertiesCreateList> KeptSiraluunDrops;
        }

        /// <summary>
        /// Pays out a finished encounter. Called from MlDigsiteManager.Finish and nowhere else - so always on
        /// the world thread, after MarkEnded - behind the encounter's own
        /// <see cref="MlDigsiteEncounter.TryClaimReward"/> latch, so it pays exactly once.
        ///
        /// THE KEPT SIRALUUN DROPS ARE READ HERE, after MarkEnded: MlDigsiteEncounter.NoteCreatureDeath records
        /// them under the same lock MarkEnded takes and refuses once Ended, so this snapshot is complete and
        /// final - nothing can be added to it afterwards.
        ///
        /// Never throws: the caller is mid-Finish and must still reach its cleanup (invariant 4), so every
        /// failure here is logged and swallowed rather than allowed to strand an encounter's creatures in the
        /// world.
        /// </summary>
        public static void Deliver(MlDigsiteEncounter encounter, MlDigsiteResult result)
        {
            if (encounter == null)
                return;

            if (!encounter.TryClaimReward())
                return;

            List<uint> eligible;
            double fraction;
            List<PropertiesCreateList> keptDrops;

            try
            {
                var snapshot = encounter.PayoutSnapshot(result);

                fraction = MlDigsiteRules.EncounterPayoutFraction(encounter.Type, snapshot, MlDigsiteTunables.Payout);

                // A bail before any wave was cleared pays NOTHING (MlDigsiteRules.WavesPayoutFraction): no
                // currency, no loot, no XP, no luminance, and no helper chest. Say so to the owner rather than
                // leave them looking for a cache that is not coming.
                //
                // ONE EXCEPTION (orchestrator ruling 2026-09-18): a recorded Kept Siraluun feather ALWAYS
                // reaches the owner. When any were recorded, the owner still gets a chest holding only them.
                if (fraction <= 0.0)
                {
                    var feathers = encounter.KeptSiraluunDropsSnapshot();

                    log.Info($"[ML_DIGSITE] {encounter} {result} pays nothing (waves={snapshot.HighestWave} cleared={snapshot.HighestWaveCleared} bailed={snapshot.Bailed} feathers={feathers.Count})");

                    if (snapshot.Bailed)
                        PlayerManager.GetOnlinePlayer(encounter.DiggerGuid)?.Session?.Network.EnqueueSend(
                            new GameMessageSystemChat(MlDigsiteRules.ZeroPayoutMessage(feathers.Count), ChatMessageType.Broadcast));

                    if (feathers.Count > 0)
                    {
                        try
                        {
                            SpawnChests(encounter, result, new[] { FeatherOnlyChestPlan(encounter, feathers) });
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[ML_DIGSITE] {encounter} feather-only chest threw", ex);
                        }
                    }

                    return;
                }

                var now = DateTime.UtcNow;

                eligible = MlDigsiteRules.SelectEligible(encounter.DiggerGuid, encounter.DamageCreditSnapshot(),
                    guid => PlayerManager.GetOnlinePlayer(guid) != null,
                    guid => encounter.LastPresentUtc(guid) is DateTime at ? now - at : (TimeSpan?)null,
                    MlDigsiteTunables.PresenceWindow);

                keptDrops = encounter.KeptSiraluunDropsSnapshot();

                log.Info($"[ML_DIGSITE] {encounter} {result} pays fraction={fraction:0.###} (waves={snapshot.HighestWave} cleared={snapshot.HighestWaveCleared} bailed={snapshot.Bailed} checkpoints={snapshot.CheckpointKills} bossMinHp={snapshot.BossMinHealthFraction:0.###} bossKilled={snapshot.BossKilled}) to {eligible.Count} player(s)");
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} payout planning threw; nothing paid", ex);
                return;
            }

            try
            {
                PayParticipants(encounter, fraction, result, eligible);
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} XP/luminance payout threw", ex);
            }

            try
            {
                SpawnChests(encounter, result, PlanChests(encounter, fraction, eligible, keptDrops));
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} reward chests threw", ex);
            }
        }

        // ---- XP and luminance ------------------------------------------------------------------------------

        /// <summary>
        /// Grants every eligible player the same XP and luminance: the full amounts, flat, on a FullClear, and
        /// <paramref name="fraction"/> of them on any other outcome (MlDigsiteRules.XpLuminanceFraction). Only players who are online now can be granted anything; an offline owner still gets their
        /// chest.
        ///
        /// WHICH MODIFIERS APPLY, because the two halves differ and the difference is deliberate:
        ///   * XP goes through Player.EarnXP, so the server's own xp_modifier applies, and because it is
        ///     typed XpType.Quest the quest_xp_modifier applies on top of it (Player_Xp.cs:28-32). That is
        ///     the same treatment every other scripted reward in the game gets, which is the point - an
        ///     operator's global XP dials should move a digsite payout like they move everything else.
        ///   * Luminance goes through Player.GrantLuminance, which is documented as granting "without any
        ///     additional luminance modifiers", so the luminance number is paid exactly as configured.
        ///
        /// ShareType.None on both, and that is NOT an oversight. Every eligible player is paid their own full
        /// share directly by this loop, so letting the grant ALSO enter the fellowship split would pay a
        /// fellowship member once for fighting and again for their fellow fighting.
        ///
        /// A player standing at the site who dealt no damage is NOT paid - they are not in
        /// <paramref name="eligible"/> (the pre-group-rewards rule paid anyone in the audience radius).
        /// </summary>
        private static void PayParticipants(MlDigsiteEncounter encounter, double fraction, MlDigsiteResult result, IReadOnlyList<uint> eligible)
        {
            // Round 15: a full clear pays the full amounts flat; anything else its payout fraction of them.
            var grantFraction = MlDigsiteRules.XpLuminanceFraction(result, fraction);

            var xp = MlDigsiteRules.ScaleReward(MlDigsiteTunables.FullXp, grantFraction);
            var luminance = MlDigsiteRules.ScaleReward(MlDigsiteTunables.FullLuminance, grantFraction);

            if (xp <= 0 && luminance <= 0)
                return;

            var paid = 0;

            foreach (var guid in eligible)
            {
                var player = PlayerManager.GetOnlinePlayer(guid);

                if (player == null)
                    continue;

                try
                {
                    if (xp > 0)
                        player.EarnXP(xp, XpType.Quest, ShareType.None);

                    if (luminance > 0)
                        player.GrantLuminance(luminance, XpType.Quest, ShareType.None);

                    paid++;
                }
                catch (Exception ex)
                {
                    // One player's grant failing must not cost everyone else theirs.
                    log.Error($"[ML_DIGSITE] {encounter} reward grant threw for {player.Name}", ex);
                }
            }

            log.Info($"[ML_DIGSITE] {encounter} {result} paid xp={xp} luminance={luminance} to {paid} player(s)");
        }

        // ---- the chests ------------------------------------------------------------------------------------

        /// <summary>
        /// One plan per eligible player, owner first. The owner's chest stands at the anchor, pays the full
        /// fraction and carries every Kept Siraluun feather UNSCALED; each helper's is scattered within
        /// <see cref="HelperChestScatterMetres"/> and pays fraction x NonOwnerShare(helpers). Feathers go into
        /// the owner's chest ONLY - never split, never copied into a helper's.
        /// </summary>
        private static List<ChestPlan> PlanChests(MlDigsiteEncounter encounter, double fraction, IReadOnlyList<uint> eligible,
            IReadOnlyList<PropertiesCreateList> keptDrops)
        {
            var plans = new List<ChestPlan>();
            var anchor = encounter.Anchor;

            if (anchor == null || eligible == null || eligible.Count == 0)
                return plans;

            var helpers = eligible.Count - 1;

            var share = MlDigsiteRules.NonOwnerShare(helpers, MlDigsiteTunables.GroupSharePool,
                MlDigsiteTunables.GroupShareMin, MlDigsiteTunables.GroupShareMax);

            var scatter = helpers > 0
                ? WorldEventGeometry.Disc(anchor, HelperChestScatterMetres, helpers, new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1)))
                : new List<Position>();

            for (var i = 0; i < eligible.Count; i++)
            {
                var isDigger = i == 0;

                plans.Add(new ChestPlan
                {
                    OwnerGuid = eligible[i],
                    IsDigger = isDigger,
                    Fraction = isDigger ? fraction : fraction * share,
                    Position = isDigger || i - 1 >= scatter.Count ? new Position(anchor) : new Position(scatter[i - 1]),
                    KeptSiraluunDrops = isDigger ? keptDrops : null,
                });
            }

            return plans;
        }

        /// <summary>
        /// The owner's chest for an encounter that pays nothing but recorded a Kept Siraluun feather. Fraction 0
        /// is what makes it feathers-only: ScaleReward returns 0 for a zero fraction, and FillStack and FillLoot
        /// both place nothing for a count of 0, so BuildChest adds the feathers and nothing else. It goes to
        /// the digger by guid, with no eligibility test - a recorded feather always reaches the owner.
        /// </summary>
        internal static ChestPlan FeatherOnlyChestPlan(MlDigsiteEncounter encounter, IReadOnlyList<PropertiesCreateList> feathers)
        {
            var anchor = encounter?.Anchor;

            return new ChestPlan
            {
                OwnerGuid = encounter?.DiggerGuid ?? 0,
                IsDigger = true,
                Fraction = 0.0,
                Position = anchor == null ? null : new Position(anchor),
                KeptSiraluunDrops = feathers,
            };
        }

        /// <summary>
        /// Stands every planned chest at the digsite.
        ///
        /// THE CHESTS ARE DELIBERATELY NOT ADDED TO THE ENCOUNTER'S HELD LIST, and this is the one considered
        /// departure from "everything placed goes through the held list". The held list is precisely the set
        /// of objects the encounter DESTROYS when it ends - and these chests are placed as the encounter is
        /// ending, so holding them would destroy the reward at the moment of paying it. Their owner is their
        /// FINITE TimeToRot instead (ml_digsite_chest_ttl_seconds, 10 minutes by default), which is why that
        /// tunable must never be set to -1: a digsite stands on a live, shared, persistent outdoor landblock,
        /// where -1 would leave an immortal chest on the island.
        ///
        /// Each still carries the encounter stamp, so the persistence exclusion and the orphan filter both
        /// refuse it exactly as they refuse a creature - an abandoned chest expires, it never persists.
        ///
        /// THREADING. Each build is handed to the ANCHOR landblock's own action queue, and the whole of it is,
        /// not merely the EnterWorld call. This is reached from MlDigsiteManager.Finish, which runs on the
        /// world thread (the tick consuming an end request), and the world thread owns no landblock:
        /// Landblock.AddWorldObjectInternal detects an entry from a foreign thread and logs "entered
        /// AddWorldObjectInternal in a cross-thread operation", adding that it "may still crash". This is the
        /// same discipline MlDigsiteManager.DestroyHeld already applies to its own writes.
        ///
        /// Queueing the ENTIRE body, rather than just the world entry, means no thread other than the owning
        /// one ever touches a chest: it is created, stamped, filled and entered in one place, and every
        /// early return still destroys it. The write-once TryClaimReward latch deliberately stays OUTSIDE, in
        /// Deliver, so a build that runs a tick later still cannot double-spawn a reward.
        /// </summary>
        private static void SpawnChests(MlDigsiteEncounter encounter, MlDigsiteResult result, IReadOnlyList<ChestPlan> plans)
        {
            var anchor = encounter.Anchor;

            if (anchor == null || plans == null || plans.Count == 0)
                return;

            if (!LandblockManager.IsLoaded(anchor.LandblockId, anchor.Instance))
            {
                // Only reachable when the encounter ended BECAUSE its landblock unloaded (or finished after it
                // did). Forcing a persistent landblock to load just to stand objects that rot in ChestTtlSeconds
                // would be the very cross-thread entry this guard exists to remove.
                log.Info($"[ML_DIGSITE] {encounter} {result} anchor landblock is not loaded; no reward chests placed ({plans.Count} planned)");
                return;
            }

            var landblock = LandblockManager.GetLandblock(anchor.LandblockId, anchor.Instance, false);

            if (landblock == null)
            {
                log.Error($"[ML_DIGSITE] {encounter} {result} could not resolve the anchor landblock; no reward chests placed");
                return;
            }

            foreach (var plan in plans)
            {
                var target = plan;

                landblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    // Deliver's try/catch does NOT cover this: the queued work runs after Deliver has returned,
                    // so an escape here would land in the landblock tick instead.
                    try
                    {
                        BuildChest(encounter, result, target);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[ML_DIGSITE] {encounter} reward chest build for 0x{target.OwnerGuid:X8} threw on the landblock queue", ex);
                    }
                }));
            }
        }

        /// <summary>
        /// One chest build, which ALWAYS runs on the anchor landblock's own thread - see
        /// <see cref="SpawnChests"/>, which is the only caller and the only thing that decides that. Every
        /// early return destroys the chest rather than leaving a stamped object stranded outside the world.
        /// </summary>
        private static void BuildChest(MlDigsiteEncounter encounter, MlDigsiteResult result, ChestPlan plan)
        {
            if (plan?.Position == null)
                return;

            var wo = WorldObjectFactory.CreateNewWorldObject(ChestWcid);

            if (wo == null)
            {
                log.Error($"[ML_DIGSITE] {encounter} chest wcid {ChestWcid} failed to create; is Content/sql/weenies/1004150 Unearthed Cache.sql applied to this world database?");
                return;
            }

            if (!(wo is Chest chest))
            {
                log.Error($"[ML_DIGSITE] {encounter} chest wcid {ChestWcid} is a {wo.WeenieType}, not a Chest; no reward placed");
                wo.Destroy();
                return;
            }

            var position = new Position(plan.Position);

            try
            {
                position.AdjustMapCoords();
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} chest terrain snap failed at {position.ToLOCString()}", ex);
                chest.Destroy();
                return;
            }

            chest.Location = position;

            // ---- every stamp, before EnterWorld ------------------------------------------------------------

            // Keeps the chest out of the shard by the same two mechanisms as a creature.
            chest.SetProperty(PropertyInt.MlDigsiteEncounterId, (int)encounter.EncounterId);

            // Owner-only access: THIS chest's player, and nobody else. Read by Chest.CheckUseRequirements.
            chest.P_DungeonCacheOwnerGuid = plan.OwnerGuid;

            // FINITE, unlike every other digsite object. See SpawnChests.
            chest.TimeToRot = MlDigsiteTunables.ChestTtlSeconds;

            // Chest.Open schedules Reset() ChestResetInterval seconds after the first open unless the
            // interval is positive infinity, and Reset calls ClearUnmanagedInventory, which DELETES
            // hand-added loot - which is everything in this chest. Set in code because MySQL cannot store
            // infinity in a DOUBLE, exactly as the Thread boss cache does.
            chest.SetProperty(PropertyFloat.ResetInterval, double.PositiveInfinity);

            // Filled BEFORE EnterWorld, the engine's own order for a corpse. TryAddToInventory nulls the
            // item's Location and needs no landblock, so nothing here depends on being in world.
            //
            // Placement order is threaded through so the two currencies land in the first slots the client's
            // contents panel renders, ahead of however many loot items rolled.
            var placement = 0;
            var fraction = plan.Fraction;
            // ScaleCurrency, not ScaleReward: floored at 1 whenever the fraction is positive (round 15).
            var doubloons = FillStack(encounter, chest, DoubloonWcid, MlDigsiteRules.ScaleCurrency(MlDigsiteTunables.ChestDoubloons, fraction), ref placement);
            var notes = FillStack(encounter, chest, TradeNoteWcid, MlDigsiteRules.ScaleCurrency(MlDigsiteTunables.ChestTradeNotes, fraction), ref placement);
            var loot = FillLoot(encounter, chest, fraction, ref placement);
            var keptSiraluun = FillKeptSiraluunDrops(encounter, chest, plan.KeptSiraluunDrops, ref placement);

            bool entered;

            try
            {
                entered = chest.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} chest EnterWorld threw at {position.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                log.Error($"[ML_DIGSITE] {encounter} chest failed to enter the world at {position.ToLOCString()}");
                chest.Destroy();
                return;
            }

            log.Info($"[ML_DIGSITE] {encounter} {result} chest 0x{chest.Guid.Full:X8} for 0x{plan.OwnerGuid:X8} (digger={plan.IsDigger}) at {position.ToLOCString()} fraction={fraction:0.###} doubloons={doubloons} notes={notes} loot={loot} keptSiraluun={keptSiraluun} ttl={chest.TimeToRot}s");

            // Only a chest that actually holds something is announced as a cache (round 15).
            if (!MlDigsiteRules.ChestHasContents(doubloons, notes, loot, keptSiraluun))
                return;

            var owner = PlayerManager.GetOnlinePlayer(plan.OwnerGuid);

            owner?.Session?.Network.EnqueueSend(new GameMessageSystemChat(plan.IsDigger
                    ? "Something buried at the dig gives way, and a cache settles into the loose earth. It answers only to you."
                    : "For your part in the fight, a smaller cache settles into the loose earth at the dig. It answers only to you.",
                ChatMessageType.Broadcast));
        }

        /// <summary>
        /// One stack of <paramref name="wcid"/>, sized <paramref name="count"/>. Returns the stack size
        /// actually placed, or 0 when nothing was.
        /// </summary>
        private static int FillStack(MlDigsiteEncounter encounter, Chest chest, uint wcid, long count, ref int placement)
        {
            if (count <= 0)
                return 0;

            var item = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (item == null)
            {
                log.Error($"[ML_DIGSITE] {encounter} could not create wcid {wcid} for the reward chest");
                return 0;
            }

            var size = (int)Math.Clamp(count, 1, item.MaxStackSize ?? 1);

            item.SetStackSize(size);

            if (!chest.TryAddToInventory(item, placement))
            {
                log.Warn($"[ML_DIGSITE] {encounter} could not add wcid {wcid} to the reward chest; destroyed");
                item.Destroy();
                return 0;
            }

            placement++;

            return size;
        }

        /// <summary>
        /// The chest's rolled loot, from the retail Legendary Chest's own treasure profile
        /// (ml_digsite_chest_treasure_death_id, treasure_Type 2001 by default - row id 241, which is NOT the
        /// key GetCachedDeathTreasure looks up) so a digsite cache never pays out the
        /// Scroll/Gem/ArtObject types an ordinary creature corpse can roll.
        ///
        /// MagicItem only, one roll per item, following the ciloot developer command rather than the corpse
        /// path's single profile draw: a cache is a fixed count of good items, not another creature's death
        /// roll. A null from a roll is a NORMAL outcome (the rolled item type found no wcid), not an error -
        /// a chest that rolled fourteen of fifteen is still a chest.
        /// </summary>
        private static int FillLoot(MlDigsiteEncounter encounter, Chest chest, double fraction, ref int placement)
        {
            var count = (int)MlDigsiteRules.ScaleReward(MlDigsiteTunables.ChestLootCount, fraction);

            if (count <= 0)
                return 0;

            var profileId = MlDigsiteTunables.ChestTreasureDeathId;
            var profile = DatabaseManager.World?.GetCachedDeathTreasure(profileId);

            if (profile == null)
            {
                log.Error($"[ML_DIGSITE] {encounter} treasure_death profile {profileId} did not resolve; the chest carries currency only");
                return 0;
            }

            var placed = 0;

            for (var i = 0; i < count; i++)
            {
                WorldObject item;

                try
                {
                    item = LootGenerationFactory.CreateRandomLootObjects(profile, TreasureItemCategory.MagicItem);
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} loot roll {i + 1}/{count} threw", ex);
                    continue;
                }

                if (item == null)
                    continue;

                if (chest.TryAddToInventory(item, placement))
                {
                    placement++;
                    placed++;
                }
                else
                {
                    item.Destroy();
                }
            }

            return placed;
        }

        /// <summary>
        /// Adds the Kept Siraluun create_list drops to a chest - only ever the OWNER's (PlanChests passes null
        /// for every helper's), and on EVERY outcome, full, partial or fail alike. A Kept Siraluun leaves no
        /// corpse (the same as any other digsite creature - IsDigsiteCorpselessDeath,
        /// Creature_LootRolls.cs:113-114), so GenerateTreasure never runs for it and this chest is the ONLY
        /// place its create_list drop can ever reach a player.
        ///
        /// Deliberately NOT scaled by the chest's fraction the way FillStack/FillLoot are: a Kept Siraluun's
        /// drop is not a share of the digsite's own payout, it is that creature's own death roll, already
        /// resolved once at the moment it died (MlDigsiteRules.ResolveCreateListDrops, called from
        /// MlDigsiteManager.ResolveKeptSiraluunDrops). A killed Kept Siraluun always pays its feather.
        ///
        /// WorldObjectFactory.CreateNewWorldObject(PropertiesCreateList) is the SAME overload
        /// GenerateTreasure itself calls (Creature_Death.cs:956), so stack size and palette come out
        /// identically to what the ordinary corpse path would have produced.
        /// </summary>
        private static int FillKeptSiraluunDrops(MlDigsiteEncounter encounter, Chest chest, IReadOnlyList<PropertiesCreateList> drops, ref int placement)
        {
            if (drops == null || drops.Count == 0)
                return 0;

            var placed = 0;

            foreach (var row in drops)
            {
                WorldObject item;

                try
                {
                    item = WorldObjectFactory.CreateNewWorldObject(row);
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} Kept Siraluun create_list wcid {row.WeenieClassId} threw on create; feather not delivered", ex);
                    continue;
                }

                if (item == null)
                {
                    log.Error($"[ML_DIGSITE] {encounter} Kept Siraluun create_list wcid {row.WeenieClassId} failed to create; feather not delivered");
                    continue;
                }

                if (chest.TryAddToInventory(item, placement))
                {
                    placement++;
                    placed++;
                }
                else
                {
                    log.Warn($"[ML_DIGSITE] {encounter} could not add Kept Siraluun drop wcid {row.WeenieClassId} to the reward chest; destroyed");
                    item.Destroy();
                }
            }

            return placed;
        }
    }
}
