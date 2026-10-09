using System;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using System.Collections.Generic;

using ACE.Server.Factories;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables;
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

        /// <summary>
        /// The retail Legendary Chest's own magic-item table (treasure_death id 241, treasure_Type 2001,
        /// magic_Item_Treasure_Type_Selection_Chances 22) - Weapon/Armor/Clothing/Jewelry/Cloak/PetDevice
        /// only. Used for the boss cache's loot rolls instead of the run's own creature profile (which is
        /// MagicItemTreasureTypeSelectionChances 8, and carries Scroll/Gem/ArtObject) so a cache never pays
        /// out the low-value item types a retail Legendary Chest never does either.
        /// </summary>
        public const int CacheMagicItemProfile = 22;

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
        /// 5 at 300 and 6 at 375 (DungeonGemSpec.MaxGemLevel): 2 + 190 * 3/115 = 6.956, floored. Above the gem
        /// ceiling it keeps climbing with the RUN level (DungeonGemSpec.MaxRunLevel, split 2026-10-08): 7 at a
        /// pressed 410 run (2 + 225 * 3/115 = 7.87) and 10 at 500 (2 + 315 * 3/115 = 10.2).
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
        /// Capped at <see cref="DefaultLootCountCap"/> items by default; see that constant for what the cap is
        /// protecting and when to raise it.
        /// </summary>
        public static int ScaledLootCount(long baseCount, double lootQuantityMult)
            => ScaledLootCount(baseCount, lootQuantityMult, DefaultLootCountCap);

        /// <summary>
        /// The roll cap, in items, for ONE cache's worth of loot.
        ///
        /// WHAT IT PROTECTS. Two things, and neither is a balance lever. It sits comfortably under the cache
        /// container's 120-slot ItemsCapacity, so a full roll can always fit; and it bounds how many times a
        /// mis-set tunable can ask <c>LootGenerationFactory.CreateRandomLootObjects</c> for an item on a landblock
        /// thread, which is the expensive half. `dynamic_dungeons_boss_chest_loot_count` and the run's
        /// LootQuantityMult multiply, so without a cap a pair of plausible values reaches the thousands.
        ///
        /// WHEN TO RAISE IT. Only when one call legitimately has to produce MORE THAN ONE cache's worth. Group
        /// Threads' pooled boss rolls are the one such caller: since 2026-09-17 they are built once for the whole
        /// deal rather than once per seat, so they pass <c>DefaultLootCountCap * seats</c> and the per-seat
        /// ceiling is unchanged in meaning. Left at 100 there, the cap would have started binding around eight
        /// seats and cost a full fellowship roughly a fifth of its boss items, silently.
        /// </summary>
        public const int DefaultLootCountCap = 100;

        /// <summary>
        /// <see cref="ScaledLootCount(long, double)"/> with an explicit cap. A cap below 0 reads as 0.
        /// </summary>
        public static int ScaledLootCount(long baseCount, double lootQuantityMult, int cap)
        {
            if (baseCount <= 0)
                return 0;

            var mult = double.IsNaN(lootQuantityMult) || lootQuantityMult <= 0 ? 1.0 : lootQuantityMult;

            var scaled = Math.Ceiling(baseCount * mult - 1e-9);

            return (int)Math.Clamp(scaled, 0, Math.Max(0, cap));
        }

        /// <summary>
        /// True when a Group Threads reward bonus B actually scales anything: a finite value above 1.0. B is
        /// never below 1.0 by construction (GroupScaling.RewardBonusFor), so NaN, infinity and anything at or below
        /// 1.0 read as "no bonus", and every caller then takes today's exact path with no extra arithmetic or draw.
        /// </summary>
        internal static bool IsScaledBonus(double rewardBonus)
            => !double.IsNaN(rewardBonus) && !double.IsInfinity(rewardBonus) && rewardBonus > 1.0;

        /// <summary>
        /// Ruling R14: a rolled Trade Note stack scaled by the group reward bonus, max(1, round(stack * B)) with
        /// midpoints rounded away from zero, saturating at int.MaxValue. A bonus that does not scale
        /// (<see cref="IsScaledBonus"/> false) returns the rolled stack, floored at 1. The note's own max stack is
        /// the caller's clamp.
        /// </summary>
        internal static int ScaledNoteStack(int rolledStack, double rewardBonus)
        {
            if (!IsScaledBonus(rewardBonus))
                return Math.Max(1, rolledStack);

            var scaled = Math.Round(rolledStack * rewardBonus, MidpointRounding.AwayFromZero);

            if (scaled >= int.MaxValue)
                return int.MaxValue;

            return Math.Max(1, (int)scaled);
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

            var chest = CreateCacheChest(run, "boss cache");

            if (chest == null)
                return;

            chest.Location = position;

            // Filled BEFORE EnterWorld, which is the engine's own order for a corpse: Creature_Death
            // calls GenerateTreasure (:719) and only then corpse.EnterWorld() (:750). TryAddToInventory
            // nulls the item's Location and needs no landblock, so nothing here depends on being in world.
            //
            // Order matters for visibility, not just content: nextPlacement is threaded through every Fill*
            // call and handed to TryAddToInventory as an explicit PlacementPosition, incremented only on a
            // successful add. Container.TryAddToInventory INCREMENTS every existing item at >= the position
            // it is handed, so adding in strictly increasing order always APPENDS and nothing already placed
            // is ever shifted. MMD goes first (slot 0), the targeted salvage bag(s) next, and the rolled
            // loot last - the two things a player most wants to see land in the first two slots
            // GameEventViewContents renders, ahead of whatever loot happened to roll.
            var nextPlacement = 0;
            var notes = FillMmd(run, chest, ref nextPlacement);
            var salvage = FillSalvage(run, chest, ref nextPlacement);
            var loot = FillLoot(run, chest, ref nextPlacement);

            if (!chest.EnterWorld())
            {
                log.Error($"[DYNDUNGEON] {run} boss cache failed to enter the world at {position.ToLOCString()}");
                chest.Destroy();
                return;
            }

            log.Info($"[DYNDUNGEON] {run} boss cache 0x{chest.Guid.Full:X8} at {position.ToLOCString()} notes={notes} salvage={salvage} loot={loot}");

            var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);
            owner?.Session?.Network.EnqueueSend(new GameMessageSystemChat("A Thread Cache has formed where the boss fell.", ChatMessageType.Broadcast));
        }

        /// <summary>
        /// A fresh, EMPTY Thread Cache for <paramref name="run"/>, with every stamp a cache needs and no
        /// Location. Shared by the boss-death cache and the pooled model's placement chain
        /// (ThreadCachePlacer), which needs a new object per placement attempt. Null, logged, when the wcid does
        /// not resolve to a Chest.
        /// </summary>
        /// <param name="purpose">Names the caller in the one error line; the boss path's "boss cache" keeps that line exactly as it was.</param>
        internal static Chest CreateCacheChest(ThreadDungeonRun run, string purpose)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(BossCacheWcid);

            if (!(wo is Chest chest))
            {
                log.Error($"[DYNDUNGEON] {run} {purpose} wcid {BossCacheWcid} did not resolve to a Chest; no cache spawned");
                wo?.Destroy();
                return null;
            }

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
            // Written before the caller's EnterWorld (this factory returns the chest with no Location and never enters it), following ThreadDungeonSpawner.TryPlace's ordering rule.
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

            // Owner ruling, 2026-09-17: "Chests should be labeled '[Name]'s Thread Cache' instead of just Thread
            // Cache. Players will know which are theirs." A group run can stand six caches within sight of each
            // other and every one of them refuses five of the six people looking at it, so the label is the only
            // thing that says which is which before a player clicks.
            //
            // BEFORE the caller's EnterWorld, which is the whole reason it is here rather than at the call sites:
            // this factory hands back a chest with no Location, so the name is already on the instance when the
            // client is first told the object exists (GameMessageCreateObject serialises Name from the biota). Set
            // after EnterWorld it would need an explicit update message to reach anyone.
            ApplyCacheName(chest, run.OwnerName);

            return chest;
        }

        /// <summary>
        /// The cache's unadorned name. MUST equal the Name row of weenie <see cref="BossCacheWcid"/> in
        /// Content/sql/weenies/1003603 Thread Cache.sql, which a test asserts against that file: the possessive
        /// name is rebuilt from this constant rather than from the chest's CURRENT name, so that the per-member
        /// overload can re-label a chest the two-argument factory has already labelled without stacking a second
        /// possessive onto the first.
        /// </summary>
        internal const string CacheBaseName = "Thread Cache";

        /// <summary>
        /// Labels a cache for its holder. Idempotent: it always composes from <see cref="CacheBaseName"/>, never
        /// from <paramref name="chest"/>'s current name, so calling it twice cannot produce
        /// "Member's Owner's Thread Cache".
        /// </summary>
        private static void ApplyCacheName(Chest chest, string holderName)
            => chest.SetProperty(PropertyString.Name, ComposeCacheName(holderName, CacheBaseName));

        /// <summary>
        /// "<c>Holder's Thread Cache</c>". Pure, so the possessive has one definition and its own tests rather
        /// than being spelled out at each call site.
        ///
        /// A holder whose name already ends in s or S takes a bare apostrophe ("Kess' Thread Cache") rather than
        /// "Kess's". Both are defensible English; this is the reading that does not put a double sibilant on an
        /// item label, and it is pinned by a test so the choice is explicit rather than incidental.
        ///
        /// A missing or blank holder falls back to <paramref name="baseName"/> unchanged. That branch is reachable
        /// - the per-member overload looks a guid up on the roster and can miss - and the thing it must never do
        /// is emit a headless "'s Thread Cache".
        /// </summary>
        internal static string ComposeCacheName(string holderName, string baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName))
                return baseName;

            var holder = holderName?.Trim();

            if (string.IsNullOrEmpty(holder))
                return baseName;

            var suffix = holder.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? "'" : "'s";

            return $"{holder}{suffix} {baseName}";
        }

        /// <summary>
        /// Group Threads: a cache for one roster member. The two-argument factory with every stamp, then the owner
        /// stamp overwritten with <paramref name="ownerGuid"/>, so Chest.CheckUseRequirements admits that member
        /// alone. Null when the factory returns null.
        ///
        /// The LABEL is overwritten with the owner stamp and for the same reason: the factory named it for the run
        /// owner, and this cache belongs to a member. Still before the caller's EnterWorld, since the factory never
        /// enters the chest. A guid that is not on the roster leaves the plain name rather than a wrong one.
        /// </summary>
        internal static Chest CreateCacheChest(ThreadDungeonRun run, string purpose, uint ownerGuid)
        {
            var chest = CreateCacheChest(run, purpose);

            if (chest != null)
            {
                chest.P_DungeonCacheOwnerGuid = ownerGuid;
                ApplyCacheName(chest, run.GetMember(ownerGuid)?.Name);
            }

            return chest;
        }

        /// <summary>
        /// One stack of Trade Notes, 1..MmdMaxForLevel(gem level), inclusive.
        ///
        /// Scaled by the gem's LEVEL alone. Deliberately untouched by the loot-quantity multiplier and by
        /// the gem-level reward-scale ratio, both of which already move the loot half of the cache: a note
        /// is flat currency, so letting the same two scalars compound onto it would make a maximally
        /// modified high-level gem a pyreal faucet rather than a better dungeon.
        ///
        /// Placed at <paramref name="nextPlacement"/> (slot 0 in practice, since this is always the first
        /// Fill* call) so the notes are the first thing GameEventViewContents renders. See the ordering note
        /// on <see cref="TrySpawnBossCache"/>.
        /// </summary>
        /// <returns>The stack size actually placed; 0 if nothing was.</returns>
        private static int FillMmd(ThreadDungeonRun run, Chest chest, ref int nextPlacement)
        {
            var note = BuildMmdNote(run, out var count);

            if (note == null)
                return 0;

            if (chest.TryAddToInventory(note, nextPlacement))
            {
                nextPlacement++;
                return count;
            }

            note.Destroy();
            return 0;
        }

        /// <summary>The Trade Note stack FillMmd places, or null (count 0) when the curve pays nothing or the wcid is missing.</summary>
        internal static WorldObject BuildMmdNote(ThreadDungeonRun run, out int count) => BuildMmdNote(run, out count, 1.0);

        /// <summary>
        /// <see cref="BuildMmdNote(ThreadDungeonRun, out int)"/> with the group reward bonus applied to the rolled
        /// stack (ruling R14): <see cref="ScaledNoteStack"/>, clamped to the note's max stack. The same single stack
        /// draw either way; an unscaled bonus changes nothing.
        /// </summary>
        internal static WorldObject BuildMmdNote(ThreadDungeonRun run, out int count, double rewardBonus)
        {
            count = 0;

            var max = MmdMaxForLevel(run.Spec.Level,
                ClampToInt(PropertyManager.GetLong("dynamic_dungeons_boss_chest_mmd_low_level", DefaultMmdLowLevel).Item),
                ClampToInt(PropertyManager.GetLong("dynamic_dungeons_boss_chest_mmd_low_max", DefaultMmdLowMax).Item),
                ClampToInt(PropertyManager.GetLong("dynamic_dungeons_boss_chest_mmd_high_level", DefaultMmdHighLevel).Item),
                ClampToInt(PropertyManager.GetLong("dynamic_dungeons_boss_chest_mmd_high_max", DefaultMmdHighMax).Item));

            if (max <= 0)
                return null;

            var stack = ThreadSafeRandom.Next(1, max);

            var note = WorldObjectFactory.CreateNewWorldObject(TradeNoteWcid);

            if (note == null)
            {
                log.Error($"[DYNDUNGEON] {run} could not create trade note wcid {TradeNoteWcid} for the boss cache");
                return null;
            }

            if (IsScaledBonus(rewardBonus))
            {
                stack = ScaledNoteStack(stack, rewardBonus);

                if (note.MaxStackSize is ushort maxStack && maxStack > 0 && stack > maxStack)
                    stack = maxStack;
            }

            // SetStackSize also recomputes Value and EncumbranceVal from the stack unit values, which is why
            // it is used in place of a bare StackSize write (WorldObject_Properties.cs:3181).
            note.SetStackSize(stack);

            count = stack;
            return note;
        }

        /// <summary>
        /// A copy of <paramref name="runProfile"/> with MagicItemTreasureTypeSelectionChances forced to
        /// <see cref="CacheMagicItemProfile"/> (the retail Legendary Chest's own table) and every other field
        /// carried over unchanged. Tier, quality, and the scaled item-count ceilings all still come from the
        /// run's own profile - only which magic-item TYPE table is consulted changes.
        ///
        /// A NEW instance, never a mutation of <paramref name="runProfile"/>: that same object is every run
        /// creature's Creature.DeathTreasureOverride (ThreadDungeonSpawner.cs, the plan.Profile stamp), so
        /// editing it in place would retarget every creature's own death-loot table along with the cache's.
        /// Null in, null out.
        /// </summary>
        public static ACE.Database.Models.World.TreasureDeath BuildCacheProfile(ACE.Database.Models.World.TreasureDeath runProfile)
        {
            if (runProfile == null)
                return null;

            return new ACE.Database.Models.World.TreasureDeath
            {
                Id = runProfile.Id,
                TreasureType = runProfile.TreasureType,
                Tier = runProfile.Tier,
                LootQualityMod = runProfile.LootQualityMod,
                UnknownChances = runProfile.UnknownChances,
                ItemChance = runProfile.ItemChance,
                ItemMinAmount = runProfile.ItemMinAmount,
                ItemMaxAmount = runProfile.ItemMaxAmount,
                ItemTreasureTypeSelectionChances = runProfile.ItemTreasureTypeSelectionChances,
                MagicItemChance = runProfile.MagicItemChance,
                MagicItemMinAmount = runProfile.MagicItemMinAmount,
                MagicItemMaxAmount = runProfile.MagicItemMaxAmount,
                MagicItemTreasureTypeSelectionChances = CacheMagicItemProfile,
                MundaneItemChance = runProfile.MundaneItemChance,
                MundaneItemMinAmount = runProfile.MundaneItemMinAmount,
                MundaneItemMaxAmount = runProfile.MundaneItemMaxAmount,
                MundaneItemTypeSelectionChances = runProfile.MundaneItemTypeSelectionChances,
                LastModified = runProfile.LastModified,
            };
        }

        /// <summary>
        /// The distinct salvage materials a cache should carry a full bag of: one per material the gem's
        /// affinities target, in the gem's own modifier order (see DungeonRewardMath.SalvageAffinities),
        /// skipping any material <paramref name="materialSalvage"/> has no bag wcid for (the category
        /// header rows, wcid 0). No affinities -> no materials -> no bags.
        /// </summary>
        /// <param name="materialSalvage">
        /// Defaults to <see cref="Player.MaterialSalvage"/>. Taken as a parameter, not read from the type
        /// directly, for the same reason MmdMaxForLevel/ScaledLootCount take their tunables as parameters:
        /// touching ANY static member of <see cref="Player"/> runs its static constructor, which loads a
        /// weenie through WorldDatabase and throws under the unit-test harness (no world DB configured) -
        /// the same class of trap PropertyManager reads are already documented as, just on a different type.
        /// A test can hand in a small literal map and stay pure; production leaves this null and gets the
        /// real table.
        /// </param>
        public static IReadOnlyList<int> CacheSalvageMaterials(IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> affinities,
            IReadOnlyDictionary<int, int> materialSalvage = null)
        {
            var materials = new List<int>();

            // The empty-collection check is deliberately BEFORE the map is resolved, not just the null
            // check: materialSalvage ?? Player.MaterialSalvage touches Player even for an empty affinities
            // list, and that alone is enough to run Player's static constructor - see the parameter doc.
            if (affinities == null || affinities.Count == 0)
                return materials;

            var map = materialSalvage ?? Player.MaterialSalvage;

            foreach (var affinity in affinities)
            {
                if (materials.Contains(affinity.MaterialId))
                    continue;

                if (!map.TryGetValue(affinity.MaterialId, out var wcid) || wcid == 0)
                    continue;

                materials.Add(affinity.MaterialId);
            }

            return materials;
        }

        /// <summary>
        /// One full bag of the run's own targeted salvage per distinct material its gem's affinities carry
        /// (ordinarily one, at the shipped RawFragmentRules.DefaultMaxSalvageMods of 1), so the gem's gimmick
        /// materializes as a guaranteed application's worth in the cache rather than only as a per-kill
        /// chance a player might never see land.
        ///
        /// Placed right after the MMD - see the ordering note on <see cref="TrySpawnBossCache"/> - so it is
        /// visible in the first slots without the player having to scroll the loot.
        /// </summary>
        /// <returns>How many bags actually landed in the cache.</returns>
        private static int FillSalvage(ThreadDungeonRun run, Chest chest, ref int nextPlacement)
        {
            var placed = 0;

            foreach (var bag in BuildSalvageBags(run))
            {
                if (chest.TryAddToInventory(bag, nextPlacement))
                {
                    nextPlacement++;
                    placed++;
                }
                else
                {
                    bag.Destroy();
                }
            }

            return placed;
        }

        /// <summary>
        /// One full bag per distinct targeted material, in affinity order. Built before any is added; adding
        /// consumes no random draw and no guid, so the result is identical to building and adding one at a time.
        /// </summary>
        internal static List<WorldObject> BuildSalvageBags(ThreadDungeonRun run)
        {
            var bags = new List<WorldObject>();
            var materials = CacheSalvageMaterials(run.SalvageAffinities);

            if (materials.Count == 0)
                return bags;

            var tier = run.LootProfile?.Tier ?? 1;

            foreach (var material in materials)
            {
                var wcid = (uint)Player.MaterialSalvage[material];
                var bag = WorldObjectFactory.CreateNewWorldObject(wcid);

                if (bag == null)
                {
                    log.Error($"[DYNDUNGEON] {run} could not create salvage bag wcid {wcid} for material {material}; skipped");
                    continue;
                }

                // Mirrors Player_Crafting.GetSalvageBag's own reset of the bugged TOD data (mahogany 20988 /
                // green garnet 21050 ship with a non-null Structure), then fills it FULL rather than leaving
                // it empty for a player to top up - this bag is meant to arrive complete.
                bag.Structure = bag.MaxStructure ?? 100;
                bag.NumItemsInMaterial = 1;
                bag.ItemWorkmanship = Math.Clamp(WorkmanshipChance.Roll(tier), 1, 10);

                bags.Add(bag);
            }

            return bags;
        }

        /// <summary>
        /// The cache's loot, rolled from the retail Legendary Chest's own item table
        /// (<see cref="BuildCacheProfile"/>) built off the RUN's own profile - tier, quality and the
        /// loot-quantity multiplier all come from the run, only the magic-item TYPE table changes - so the
        /// cache never pays out the low-value types (Scroll/Gem/ArtObject) the run's own creatures can drop.
        ///
        /// The cache profile is built ONCE, not per roll: LootGenerationFactory.CreateRandomLootObjects reads
        /// it fresh each call and nothing about it changes between rolls.
        ///
        /// MagicItem only, and one roll per item, following the ciloot developer command
        /// (DeveloperCommands.cs:2672-2678) rather than the corpse path's single profile draw: a cache is a
        /// fixed count of good items, not another creature's death roll.
        ///
        /// Placed last - see the ordering note on <see cref="TrySpawnBossCache"/> - so the MMD and any
        /// targeted salvage bag keep the first slots regardless of how many items roll.
        /// </summary>
        /// <returns>How many items actually landed in the cache.</returns>
        private static int FillLoot(ThreadDungeonRun run, Chest chest, ref int nextPlacement)
        {
            var placed = 0;

            foreach (var item in BuildCacheLoot(run))
            {
                if (chest.TryAddToInventory(item, nextPlacement))
                {
                    nextPlacement++;
                    placed++;
                }
                else
                {
                    item.Destroy();
                }
            }

            return placed;
        }

        /// <summary>The Legendary-table loot rolls FillLoot places. Empty, with the original warning, when the run has no profile.</summary>
        internal static List<WorldObject> BuildCacheLoot(ThreadDungeonRun run) => BuildCacheLoot(run, 1.0);

        /// <summary>
        /// <see cref="BuildCacheLoot(ThreadDungeonRun)"/> with the group reward bonus folded into the loot-quantity
        /// multiplier (ruling R14): ScaledLootCount(base, LootQuantityMult * B). An unscaled bonus multiplies by
        /// exactly 1.0, so the count, and therefore every loot roll, is today's.
        /// </summary>
        internal static List<WorldObject> BuildCacheLoot(ThreadDungeonRun run, double rewardBonus)
            => BuildCacheLoot(run, rewardBonus, DefaultLootCountCap);

        /// <summary>
        /// <see cref="BuildCacheLoot(ThreadDungeonRun, double)"/> with an explicit roll cap, for the one caller
        /// that builds more than one cache's worth in a single call: Group Threads' pooled boss rolls.
        /// </summary>
        internal static List<WorldObject> BuildCacheLoot(ThreadDungeonRun run, double rewardBonus, int cap)
        {
            var items = new List<WorldObject>();
            var runProfile = run.LootProfile;

            if (runProfile == null)
            {
                // The run was populated by the plan-build-threw path, which marks it populated with nothing
                // and never stamps a profile. There is no boss in such a run, so this is very nearly
                // unreachable; it is a warning rather than an error because an empty cache is still correct.
                log.Warn($"[DYNDUNGEON] {run} boss cache has no run loot profile; notes only");
                return items;
            }

            var cacheProfile = BuildCacheProfile(runProfile);

            var count = ScaledLootCount(
                PropertyManager.GetLong("dynamic_dungeons_boss_chest_loot_count", DefaultBossCacheLootCount).Item,
                run.LootQuantityMult * (IsScaledBonus(rewardBonus) ? rewardBonus : 1.0),
                cap);

            return RollCacheItems(count, () => LootGenerationFactory.CreateRandomLootObjects(cacheProfile, TreasureItemCategory.MagicItem), run);
        }

        /// <summary>
        /// The roll loop of <see cref="BuildCacheLoot(ThreadDungeonRun, double, int)"/>, with the loot factory
        /// injected so a throwing roll can be driven in a test.
        ///
        /// A throw from one roll costs THAT ROLL and nothing else. The guard is per roll rather than around the
        /// whole loop because of the pooled group boss bonus (2026-09-17): one call here now builds up to
        /// DefaultLootCountCap * seats rolls for the WHOLE fellowship, so a throw partway through would otherwise
        /// zero every seat's item share instead of one member's. Before the bonus was pooled, each seat built its
        /// own inside its own catch, and the blast radius was one seat by construction; this keeps it at one roll
        /// for the solo path too.
        ///
        /// It is the same tolerance the null case carries: a cache that rolled nine of ten is still a cache.
        /// </summary>
        internal static List<WorldObject> RollCacheItems(int count, Func<WorldObject> roll, object runLabel)
        {
            var items = new List<WorldObject>();

            if (roll == null)
                return items;

            for (var i = 0; i < count; i++)
            {
                WorldObject item;

                try
                {
                    item = roll();
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {runLabel} boss cache loot roll {i + 1} of {count} threw; that roll is skipped", ex);
                    continue;
                }

                // Null is a normal outcome of a roll that found no wcid for the rolled item type, not an
                // error - the ciloot command logs it, but a cache that rolled nine of ten is still a cache.
                if (item != null)
                    items.Add(item);
            }

            return items;
        }

        /// <summary>
        /// The boss bonus as loose items, for the pooled model (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md
        /// section 3): Trade Notes, then the targeted salvage bags, then the Legendary-table loot - the same
        /// three builders, in the same order, the boss-death cache fills from. Always added on a pooled boss
        /// death (user ruling 2026-09-14); the boss-chest switch governs only the OFF path, so nothing here
        /// reads it.
        /// </summary>
        public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run)
        {
            var items = new List<WorldObject>();

            if (run == null)
                return items;

            AddBonusTradeNotes(run, items);
            AddBonusSalvageBags(run, items);

            // The ruling named Trade Notes and salvage bags; the Legendary-table items are kept pending a
            // ruling. To drop them from the pooled bonus, delete this one line AND its twin in the two-argument
            // BuildBossBonusItems(run, rewardBonus) below; GroupLootBankTests pins that both step lists match.
            AddBonusLegendaryItems(run, items);

            return items;
        }

        /// <summary>
        /// Group Threads ruling R14: one member's boss bonus with the group reward bonus B applied. B scales the
        /// Trade Note stack (<see cref="ScaledNoteStack"/>, clamped to the note's max stack) and the Legendary-table
        /// loot count (LootQuantityMult * B); the salvage bags are not scaled. The steps and their order are the
        /// one-argument builder's.
        ///
        /// A bonus that does not scale (<see cref="IsScaledBonus"/> false, which includes exactly 1.0) returns the
        /// one-argument builder's result by calling it, so its call sequence and random draws are today's by
        /// construction. The delegation runs this way round, rather than the one-argument overload calling this one,
        /// because that overload's body is pinned by ThreadRewardBonusBuilderTests.
        /// </summary>
        public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run, double rewardBonus)
        {
            if (!IsScaledBonus(rewardBonus))
                return BuildBossBonusItems(run);

            var items = new List<WorldObject>();

            if (run == null)
                return items;

            AddBonusTradeNotes(run, items, rewardBonus);
            AddBonusSalvageBags(run, items);
            AddBonusLegendaryItems(run, items, rewardBonus);

            return items;
        }

        /// <summary>
        /// Owner ruling, 2026-09-17 ("each player should get the mmds and salvage. other loot should be split"):
        /// the DUPLICATED half of a group run's boss bonus, built once per deal seat. Trade Notes scaled by B,
        /// then the salvage bags. No Legendary-table rolls - those are pooled by
        /// <see cref="BuildBossBonusPooledRolls"/> and snake-dealt instead.
        ///
        /// Delegates to the same two step helpers <see cref="BuildBossBonusItems(ThreadDungeonRun, double)"/>
        /// uses, in the same order, so the currency and salvage a group member gets are byte-for-byte what they
        /// got before the split. An unscaled B takes the unscaled note helper, exactly as that overload does.
        /// </summary>
        public static List<WorldObject> BuildBossBonusPerSeatItems(ThreadDungeonRun run, double rewardBonus)
        {
            var items = new List<WorldObject>();

            if (run == null)
                return items;

            if (IsScaledBonus(rewardBonus))
                AddBonusTradeNotes(run, items, rewardBonus);
            else
                AddBonusTradeNotes(run, items);

            AddBonusSalvageBags(run, items);

            return items;
        }

        /// <summary>
        /// The FIXED half of a SOLO run's boss bonus: the Trade Notes and the targeted salvage bags, in that order,
        /// with no reward bonus (a solo run has none). Exactly the first two steps of
        /// <see cref="BuildBossBonusItems(ThreadDungeonRun)"/>, by delegation, so the currency and salvage a solo
        /// player gets are byte-for-byte what they were; the third step, the Legendary-table rolls, is counted and
        /// built a batch at a time by the delivery steps instead (ThreadCacheFiller, 2026-09-19), the way the group
        /// path has built its pooled half since 2026-09-17.
        /// </summary>
        public static List<WorldObject> BuildBossBonusFixedItems(ThreadDungeonRun run) => BuildBossBonusPerSeatItems(run, 1.0);

        /// <summary>
        /// The POOLED half of a group run's boss bonus: the Legendary-table rolls, built ONCE for the whole deal
        /// and snake-dealt across the seats, so a boss pays the same per-player multiplier the trash loot already
        /// pays instead of being the one term that scaled differently.
        ///
        /// <paramref name="factor"/> is seats * B, NOT run.BossLootFactor (E * B). E is computed from the LOCKED
        /// ROSTER while the deal divides over DealSeats(), and E carries the tunable g, so E * B / seats equals B
        /// only at default tuning with every member keyed. seats * B is B per player by construction.
        ///
        /// <paramref name="cap"/> is <see cref="DefaultLootCountCap"/> * seats: one call now legitimately produces
        /// several caches' worth. See DefaultLootCountCap for what the cap protects.
        ///
        /// ROUNDING: ScaledLootCount ceilings, so a seat can land one item either side of exactly B times solo
        /// (at 2 seats, ceil(base * q * 2B) is odd about half the time and the snake gives the spare to whichever
        /// seat the cursor favours). That is documented rather than engineered around: correcting it would mean
        /// per-seat counts, which is the very thing being removed.
        /// </summary>
        public static List<WorldObject> BuildBossBonusPooledRolls(ThreadDungeonRun run, double factor, int cap)
            => run == null ? new List<WorldObject>() : BuildCacheLoot(run, factor, cap);

        /// <summary>
        /// How many rolls <see cref="BuildBossBonusPooledRolls"/> would build for (<paramref name="factor"/>,
        /// <paramref name="cap"/>), without building any: BuildCacheLoot's own count, term for term. This is what lets
        /// the group deal build the pooled half across several delivery steps (ThreadGroupCacheDelivery.Deal): it
        /// counts once at the claim, then asks BuildBossBonusPooledRolls for one batch at a time with the batch size
        /// as the cap, which ScaledLootCount clamps the same total down to. 0 for a run with no loot profile, where
        /// BuildCacheLoot would build nothing either.
        /// </summary>
        public static int BossBonusPooledRollCount(ThreadDungeonRun run, double factor, int cap)
        {
            if (run?.LootProfile == null)
                return 0;

            return ScaledLootCount(
                PropertyManager.GetLong("dynamic_dungeons_boss_chest_loot_count", DefaultBossCacheLootCount).Item,
                run.LootQuantityMult * (IsScaledBonus(factor) ? factor : 1.0),
                cap);
        }

        private static void AddBonusTradeNotes(ThreadDungeonRun run, List<WorldObject> items)
        {
            var note = BuildMmdNote(run, out _);

            if (note != null)
                items.Add(note);
        }

        private static void AddBonusTradeNotes(ThreadDungeonRun run, List<WorldObject> items, double rewardBonus)
        {
            var note = BuildMmdNote(run, out _, rewardBonus);

            if (note != null)
                items.Add(note);
        }

        /// <summary>
        /// NO rewardBonus overload, and that is DELIBERATE-AS-SHIPPED rather than an oversight. The salvage bags
        /// have never responded to B: ruling R14 scaled the Trade Note stack and the Legendary roll count and
        /// stopped there, and every group path has run through this unscaled step since. Confirmed still intended
        /// on 2026-09-17, when the boss bonus was split and the question came up explicitly.
        ///
        /// Do not "fix" this by threading B through. A bag is a whole unit of one material, so scaling it is a step
        /// change rather than a smooth one, and whether a group should get more of them is a balance decision
        /// nobody has made. It needs a ruling, not a patch.
        /// </summary>
        private static void AddBonusSalvageBags(ThreadDungeonRun run, List<WorldObject> items) => items.AddRange(BuildSalvageBags(run));

        private static void AddBonusLegendaryItems(ThreadDungeonRun run, List<WorldObject> items) => items.AddRange(BuildCacheLoot(run));

        private static void AddBonusLegendaryItems(ThreadDungeonRun run, List<WorldObject> items, double rewardBonus) => items.AddRange(BuildCacheLoot(run, rewardBonus));

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
