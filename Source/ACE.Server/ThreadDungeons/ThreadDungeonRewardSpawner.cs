using System;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Factories.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The three things a run hands its owner at the end of a fight: the boss cache that forms when the boss
    /// dies, the completion visual, and the click-to-enter exit portal that opens beside the owner when the
    /// run clears.
    ///
    /// Its own file rather than more of ThreadDungeonManager, because none of it is registry work: the
    /// manager owns which runs exist and when they end, and everything here is object spawning on a
    /// landblock. The manager keeps only the two call sites and the player-facing wording, exactly as it does
    /// for the gem destroyer and the survey recorder.
    ///
    /// Threading. Both entry points run on a LANDBLOCK tick thread - <see cref="TrySpawnBossCache"/> from
    /// Creature.Die() through ThreadDungeonManager.OnRunCreatureDied, and <see cref="OnRunCleared"/> from
    /// AnnounceCleared, which is reached from that same death path or from the spawner's own action queue.
    /// Everything placed here lands on the run's OWN landblock, which is the landblock whose thread is
    /// running: the cache goes to the boss anchor inside the copy, and the portal is refused outright unless
    /// the owner is standing in the copy. So LandblockManager.AddObject is never reached for a landblock
    /// other than the caller's own.
    ///
    /// Exactly-once is the run's job, not this class's: <see cref="ThreadDungeonRun.TryClaimBossChest"/> and
    /// <see cref="ThreadDungeonRun.TryClaimClearedAnnouncement"/> both latch under the run's own lock.
    /// </summary>
    public static class ThreadDungeonRewardSpawner
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Thread Cache - the boss chest. Content/sql/weenies/1003603 Thread Cache.sql.</summary>
        public const uint BossCacheWcid = 1003603;

        /// <summary>Thread Exit - the click-to-enter summoned exit. Content/sql/weenies/1003604 Thread Exit (Summoned).sql.</summary>
        public const uint SummonedExitWcid = 1003604;

        /// <summary>
        /// Trade Note (250,000), the "MMD". Retail wcid, WeenieType Stackable, MaxStackSize 1000 - verified
        /// live against ace_world.weenie (class_Id 20630, class_Name tradenote250000, type 51).
        /// </summary>
        public const uint TradeNoteWcid = 20630;

        /// <summary>How many items a cache rolls before the run's loot-quantity multiplier is applied.</summary>
        public const long DefaultBossCacheLootCount = 10;

        public const long DefaultMmdLowLevel = 185;
        public const long DefaultMmdLowMax = 2;
        public const long DefaultMmdHighLevel = 300;
        public const long DefaultMmdHighMax = 5;

        // ------------------------------------------------------------------------------------------------
        // Pure arithmetic. Every tunable arrives as a PARAMETER and nothing here reads PropertyManager, so
        // the unit tests can assert the shipped numbers without a world (PropertyManager reads throw under
        // the test harness). The call sites below are the only place a tunable is read.
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The TOP of the cache's MMD stack roll, from the gem's level. The stack itself is a uniform
        /// 1..this, inclusive, so a level-185 gem still sometimes pays a single note.
        ///
        /// A straight line through two anchors: (lowLevel, lowMax) and (highLevel, highMax), floored to an
        /// integer. At the shipped anchors - 185 -> 2 and 300 -> 5 - that reads 2 at level 185, 4 at 275,
        /// 5 at 300 and 6 at 375 (the current DungeonGemSpec.MaxLevel): 2 + 190 * 3/115 = 6.956, floored.
        ///
        /// Clamped BELOW at <paramref name="lowMax"/> and deliberately NOT clamped above. The floor is what
        /// keeps a gem below the low anchor from paying zero (a level-100 gem reads 2, not -1); the missing
        /// ceiling is what makes a gem-level raise keep scaling on its own rather than silently flattening at
        /// the high anchor. The 2026-09-09 raise from 275 to 375 is the first one to exercise that, and it did
        /// so with no edit here - which is what the missing ceiling was for.
        ///
        /// A degenerate span (highLevel == lowLevel) has no slope to interpolate along, so it returns
        /// <paramref name="lowMax"/>: the low anchor is already the function's floor everywhere else, which
        /// makes it the one answer that cannot surprise a caller who mis-set the pair.
        /// </summary>
        public static int MmdMaxForLevel(int gemLevel, int lowLevel, int lowMax, int highLevel, int highMax)
        {
            if (highLevel == lowLevel)
                return lowMax;

            var span = (double)(highLevel - lowLevel);
            var raw = lowMax + (highMax - lowMax) * (gemLevel - lowLevel) / span;

            var floored = (int)Math.Floor(raw);

            return Math.Max(lowMax, floored);
        }

        /// <summary>
        /// How many loot rolls a cache takes: ceil(baseCount * lootQuantityMult).
        ///
        /// The multiplier is the RUN's own - what DungeonPopulationBuilder handed BuildProfile, carried on
        /// the run since ThreadDungeonSpawner stamped it - so a gem that made its creatures drop more also
        /// makes its cache hold more, at the identical rate, without this file re-reading a single tunable
        /// that could have moved since the copy was populated.
        ///
        /// The 1e-9 subtraction before Math.Ceiling is the same float-error absorber
        /// ThreadDungeonRun.ClearTargetLocked uses, and for the same reason: a product that should land
        /// exactly on an integer (10 * 3.0) can come out a hair over and push Ceiling to the next integer.
        ///
        /// Capped at 100 items, comfortably under the cache's 120-slot ItemsCapacity, so a mis-set tunable
        /// cannot ask the loot factory for an unbounded number of rolls on a landblock thread.
        /// </summary>
        public static int ScaledLootCount(long baseCount, double lootQuantityMult)
        {
            if (baseCount <= 0)
                return 0;

            var mult = double.IsNaN(lootQuantityMult) || lootQuantityMult <= 0 ? 1.0 : lootQuantityMult;

            var scaled = Math.Ceiling(baseCount * mult - 1e-9);

            return (int)Math.Clamp(scaled, 0, 100);
        }

        // ------------------------------------------------------------------------------------------------
        // Boss cache
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Spawns the run's Thread Cache at the boss's AUTHORED anchor - run.Dungeon.BossAnchor, the curated
        /// deepest point the boss was placed on - and not wherever the boss actually died. A boss that was
        /// pulled halfway across the dungeon would otherwise leave its cache in a corridor, and a boss killed
        /// standing in a hazard would leave it inside one.
        ///
        /// Called from ThreadDungeonManager.OnRunCreatureDied for a boss death only, BEFORE the clear
        /// announcement, so the player reads "a Thread Cache has formed" ahead of the clear line on the kill
        /// that does both.
        /// </summary>
        public static void TrySpawnBossCache(ThreadDungeonRun run)
        {
            if (run == null)
                return;

            // Explicit fallback, not GetBool's built-in one: GetBool defaults to FALSE before
            // PropertyManager.DoWork has seeded a row from DefaultBooleanProperties, and a switch shipped
            // default-true that reads false until the next sync would contradict its own default. Same
            // reasoning as dynamic_dungeons_uplift_rename at ThreadDungeonSpawner.cs:166.
            if (!PropertyManager.GetBool("dynamic_dungeons_boss_chest_enabled", true).Item)
                return;

            var anchor = run.Dungeon?.BossAnchor;

            if (anchor == null)
            {
                log.Warn($"[DYNDUNGEON] {run} boss died but the dungeon has no bossAnchor; no cache spawned");
                return;
            }

            if (!run.TryClaimBossChest())
                return;

            // Argument order copied from ThreadDungeonSpawner.TryPlace: the Position constructor takes its
            // rotation w LAST, while the spawn-point def stores it first (qw qx qy qz, the /loc order).
            var position = new Position(anchor.Cell, anchor.X, anchor.Y, anchor.Z, anchor.QX, anchor.QY, anchor.QZ, anchor.QW, run.Instance);

            // The same guard the creature placement path takes. A point outside the copy's own landblock
            // would drag the cache into a block nothing is keeping awake or cleaning up - and, because this
            // runs on the run landblock's tick thread, would reach LandblockManager.AddObject for a
            // landblock this thread does not own.
            if (position.LandblockId.Landblock != run.Dungeon.Landblock)
            {
                log.Warn($"[DYNDUNGEON] {run} bossAnchor {position.ToLOCString()} is not on landblock 0x{run.Dungeon.Landblock:X4}; no cache spawned");
                return;
            }

            var wo = WorldObjectFactory.CreateNewWorldObject(BossCacheWcid);

            if (!(wo is Chest chest))
            {
                log.Error($"[DYNDUNGEON] {run} boss cache wcid {BossCacheWcid} did not resolve to a Chest; no cache spawned");
                wo?.Destroy();
                return;
            }

            chest.Location = position;

            // The run stamp. What actually keeps this cache out of the shard is the copy being EPHEMERAL:
            // Landblock.SaveDB returns on `if (IsEphemeral)` (Landblock.cs:1863-1868) before it iterates
            // worldObjects at all, so IsDynamicThatShouldPersistToShard - and therefore this stamp - is never
            // consulted for anything standing inside a Thread copy. The stamp is defense in depth for the
            // case that early return does not cover, an object that ends up on a NON-ephemeral landblock,
            // where WorldObject_Database.cs:178 does read it.
            //
            // Neither mechanism covers a DIRECT SaveBiotaToDatabase call, which writes unconditionally and
            // checks no stamp (WorldObject_Database.cs:49-65) - that is how an item a player picks OUT of
            // this cache persists correctly (Player_Inventory.cs:186), which is the behaviour we want, and
            // it is also why the cache's contents are deliberately left unstamped.
            //
            // Written BEFORE EnterWorld, following ThreadDungeonSpawner.TryPlace's ordering rule.
            chest.SetProperty(PropertyInt.ThreadDungeonRunId, (int)run.RunId);

            // Owner-only access. Read by Chest.CheckUseRequirements, which is the gate every use path goes
            // through; null on every other chest in the game, which is what makes the check inert for them.
            chest.P_DungeonCacheOwnerGuid = run.OwnerGuid;

            // Mandatory. A hand-spawned object has a dynamic guid and no Generator back-reference, so
            // IsDecayable() is true for it and the landblock heartbeat would Destroy it at DefaultTimeToRot -
            // with the run's whole reward still inside. -1 ties it to the copy instead: it dies when the
            // landblock unloads, which is what should end it.
            chest.TimeToRot = -1;

            // Chest.Open schedules Reset(resetTimestamp) ChestResetInterval seconds after the first open
            // UNLESS the interval is positive infinity (Chest.cs:221), and Reset calls
            // ClearUnmanagedInventory (Chest.cs:287), which DELETES hand-added loot. Everything in this
            // cache is hand-added, so without this the player's reward vanishes two minutes after they open
            // it - ResetInterval is null on the weenie, and ChestResetInterval then reads its 120s default.
            //
            // It has to be set in code because MySQL cannot store infinity in a DOUBLE. Checked against
            // every other consumer of ResetInterval before committing to it: Chest.ChestRegenOnClose's
            // "<= 5" test is false for infinity; Container.cs:765 excludes Chest outright; and Door,
            // PressurePlate, Switch and Vendor each read the property on their own type, never on a Chest.
            // Storage sets the same value through Default_ChestResetInterval (Storage.cs:18), so an
            // infinite chest interval is already a shipped, exercised state rather than a novel one.
            chest.SetProperty(PropertyFloat.ResetInterval, double.PositiveInfinity);

            var notes = FillMmd(run, chest);
            var loot = FillLoot(run, chest);

            // Filled BEFORE EnterWorld, which is the engine's own order for a corpse: Creature_Death
            // calls GenerateTreasure (:719) and only then corpse.EnterWorld() (:750). TryAddToInventory
            // nulls the item's Location and needs no landblock, so nothing here depends on being in world.
            if (!chest.EnterWorld())
            {
                log.Error($"[DYNDUNGEON] {run} boss cache failed to enter the world at {position.ToLOCString()}");
                chest.Destroy();
                return;
            }

            log.Info($"[DYNDUNGEON] {run} boss cache 0x{chest.Guid.Full:X8} at {position.ToLOCString()} notes={notes} loot={loot}");

            var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);
            owner?.Session?.Network.EnqueueSend(new GameMessageSystemChat("A Thread Cache has formed where the boss fell.", ChatMessageType.Broadcast));
        }

        /// <summary>
        /// One stack of Trade Notes, 1..MmdMaxForLevel(gem level), inclusive.
        ///
        /// Scaled by the gem's LEVEL alone. Deliberately untouched by the loot-quantity multiplier and by
        /// the gem-level reward-scale ratio, both of which already move the loot half of the cache: a note
        /// is flat currency, so letting the same two scalars compound onto it would make a maximally
        /// modified high-level gem a pyreal faucet rather than a better dungeon.
        /// </summary>
        /// <returns>The stack size actually placed; 0 if nothing was.</returns>
        private static int FillMmd(ThreadDungeonRun run, Chest chest)
        {
            var max = MmdMaxForLevel(run.Spec.Level,
                ClampToInt(PropertyManager.GetLong("dynamic_dungeons_boss_chest_mmd_low_level", DefaultMmdLowLevel).Item),
                ClampToInt(PropertyManager.GetLong("dynamic_dungeons_boss_chest_mmd_low_max", DefaultMmdLowMax).Item),
                ClampToInt(PropertyManager.GetLong("dynamic_dungeons_boss_chest_mmd_high_level", DefaultMmdHighLevel).Item),
                ClampToInt(PropertyManager.GetLong("dynamic_dungeons_boss_chest_mmd_high_max", DefaultMmdHighMax).Item));

            if (max <= 0)
                return 0;

            var count = ThreadSafeRandom.Next(1, max);

            var note = WorldObjectFactory.CreateNewWorldObject(TradeNoteWcid);

            if (note == null)
            {
                log.Error($"[DYNDUNGEON] {run} could not create trade note wcid {TradeNoteWcid} for the boss cache");
                return 0;
            }

            // SetStackSize also recomputes Value and EncumbranceVal from the stack unit values, which is why
            // it is used in place of a bare StackSize write (WorldObject_Properties.cs:3181).
            note.SetStackSize(count);

            if (chest.TryAddToInventory(note))
                return count;

            note.Destroy();
            return 0;
        }

        /// <summary>
        /// The cache's loot, rolled from the RUN's own profile at the run's own loot-quantity multiplier -
        /// both stamped on the run by ThreadDungeonSpawner at populate time, never recomputed here.
        ///
        /// MagicItem only, and one roll per item, following the ciloot developer command
        /// (DeveloperCommands.cs:2672-2678) rather than the corpse path's single profile draw: a cache is a
        /// fixed count of good items, not another creature's death roll.
        /// </summary>
        /// <returns>How many items actually landed in the cache.</returns>
        private static int FillLoot(ThreadDungeonRun run, Chest chest)
        {
            var profile = run.LootProfile;

            if (profile == null)
            {
                // The run was populated by the plan-build-threw path, which marks it populated with nothing
                // and never stamps a profile. There is no boss in such a run, so this is very nearly
                // unreachable; it is a warning rather than an error because an empty cache is still correct.
                log.Warn($"[DYNDUNGEON] {run} boss cache has no run loot profile; notes only");
                return 0;
            }

            var count = ScaledLootCount(
                PropertyManager.GetLong("dynamic_dungeons_boss_chest_loot_count", DefaultBossCacheLootCount).Item,
                run.LootQuantityMult);

            var placed = 0;

            for (var i = 0; i < count; i++)
            {
                var item = LootGenerationFactory.CreateRandomLootObjects(profile, TreasureItemCategory.MagicItem);

                // Null is a normal outcome of a roll that found no wcid for the rolled item type, not an
                // error - the ciloot command logs it, but a cache that rolled nine of ten is still a cache.
                if (item == null)
                    continue;

                if (chest.TryAddToInventory(item))
                    placed++;
                else
                    item.Destroy();
            }

            return placed;
        }

        // ------------------------------------------------------------------------------------------------
        // Clear-time rewards
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The completion visual and the summoned exit, both owner-only. Called from
        /// ThreadDungeonManager.AnnounceCleared after the clear line, through the same swappable-delegate
        /// seam GemDestroyer and SurveyRecorder use.
        ///
        /// The null-owner early return is FIRST on purpose and is load-bearing beyond tidiness: AnnounceCleared
        /// is reached directly by ThreadDungeonManagerRulesTests through OnRunPopulated, where
        /// PlayerManager.GetOnlinePlayer returns null and any PropertyManager read would throw. Nothing here
        /// touches a tunable until an owner has been resolved.
        /// </summary>
        public static void OnRunCleared(ThreadDungeonRun run, Player owner)
        {
            if (run == null || owner == null)
                return;

            TryPlayClearEffect(owner);
            TrySummonExit(run, owner);
        }

        /// <summary>
        /// The cloak/aetheria level-up burst, played on the owner. Verbatim the call Player_Xp.cs:667 makes
        /// when a trinket levels: the guid is the PLAYER's, so the effect plays on them rather than on the
        /// dungeon.
        /// </summary>
        private static void TryPlayClearEffect(Player owner)
        {
            if (!PropertyManager.GetBool("dynamic_dungeons_clear_effect_enabled", true).Item)
                return;

            owner.EnqueueBroadcast(new GameMessageScript(owner.Guid, PlayScript.AetheriaLevelUp));
        }

        /// <summary>
        /// A click-to-enter Thread Exit beside the owner, so a cleared run never asks the player to walk back
        /// to a static exit. It does NOT replace those: the ThreadDungeonContentFilter portal substitution
        /// still stands every retail portal in the copy up as an exit, and this is one more of them.
        ///
        /// It is click-only rather than walk-through (1003604 clears ReportCollisions, where 1003601 sets
        /// it) because it materialises ON the player. A walk-through portal at the owner's own feet would
        /// fire its collision the instant it entered the world and eject them from the dungeon they had just
        /// finished, with their corpse loot still on the floor.
        ///
        /// Two ways the owner is not somewhere a portal may be summoned, and both skip SILENTLY - the static
        /// exits still exist, so there is nothing to apologise for:
        ///   * they are online but standing outside the copy (evicted by death, or logged in elsewhere), in
        ///     which case a portal at their feet would be a Thread Exit sitting in a town;
        ///   * they have no Location at all, mid-teleport.
        /// The offline case never reaches here: OnRunCleared returns on a null owner.
        /// </summary>
        private static void TrySummonExit(ThreadDungeonRun run, Player owner)
        {
            if (!PropertyManager.GetBool("dynamic_dungeons_clear_portal_enabled", true).Item)
                return;

            if (owner.Location == null || owner.Location.Instance != run.Instance)
                return;

            var wo = WorldObjectFactory.CreateNewWorldObject(SummonedExitWcid);

            if (!(wo is Portal portal))
            {
                log.Error($"[DYNDUNGEON] {run} summoned exit wcid {SummonedExitWcid} did not resolve to a Portal; not summoned");
                wo?.Destroy();
                return;
            }

            // A copy of the owner's own position, re-stamped with the run instance. The instance is already
            // equal by the guard above; re-stamping makes that explicit rather than inherited.
            portal.Location = new Position(owner.Location, run.Instance);

            // Same two stamps, for the same two reasons, as the cache above: the run stamp is the
            // defense-in-depth half of the save exclusion - the primary one is Landblock.SaveDB's
            // `if (IsEphemeral)` early return at Landblock.cs:1863-1868, and this portal is only ever
            // summoned inside the copy - and TimeToRot -1 stops the landblock heartbeat destroying it. Both
            // before EnterWorld.
            portal.SetProperty(PropertyInt.ThreadDungeonRunId, (int)run.RunId);
            portal.TimeToRot = -1;

            if (!portal.EnterWorld())
            {
                log.Error($"[DYNDUNGEON] {run} summoned exit failed to enter the world at {portal.Location?.ToLOCString()}");
                portal.Destroy();
                return;
            }

            log.Info($"[DYNDUNGEON] {run} summoned exit 0x{portal.Guid.Full:X8} at {portal.Location.ToLOCString()}");

            owner.Session?.Network.EnqueueSend(new GameMessageSystemChat("A Thread Exit opens beside you.", ChatMessageType.Broadcast));
        }

        /// <summary>Narrows a long tunable to int without wrapping; the tunables here are all small.</summary>
        private static int ClampToInt(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }
}
