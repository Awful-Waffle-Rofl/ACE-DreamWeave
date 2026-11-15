using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Factories;
using ACE.Server.Factories.Tables;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Ember Vault's bonus: on top of its tier 8 gear (treasure_death 2000153), each
    /// fill of the vault (the first open after it relocks) adds Embers and a full salvage bag (a Diamond Scarab and
    /// Pyreal Motes only if their settings are above 0). No dye pots (the Ember Trader sells them). The vault's reset clears them with the rest of its contents.
    /// </summary>
    public static class FraysVault
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const uint VaultWcid = 2000153;
        public const uint DiamondScarab = 7299;

        /// <summary>The retail Pyreal Mote. It does not stack, so each is its own item.</summary>
        public const uint PyrealMote = 6353;

        /// <summary>The most Vault Keys one use opens, so players do not have to use a key over and over to get the loot.</summary>
        public const int MaxKeysAtOnce = 10;

        /// <summary>Fills a vault owes on its next open beyond its own, by the vault's guid (TakeMoreKeys).</summary>
        private static readonly ConcurrentDictionary<uint, int> moreFills = new ConcurrentDictionary<uint, int>();

        /// <summary>Pure: how many more keys one use takes when the player holds <paramref name="held"/> Vault Keys in all (the used one counted).</summary>
        public static int MoreKeys(int held) => Math.Clamp(held - 1, 0, MaxKeysAtOnce - 1);

        /// <summary>
        /// A Vault Key just unlocked an Ember Vault (UnlockerHelper.UseUnlocker, before the key's own use is taken): up to 9
        /// more of the player's Vault Keys go too, from the used keyring first, then other keyrings and loose keys, and the
        /// vault holds that many more fills when it opens, all in the one chest (the same items as opening them one by one).
        /// </summary>
        public static void TakeMoreKeys(Player player, WorldObject usedKey, Chest vault)
        {
            if (player == null || usedKey == null || vault == null)
                return;

            try
            {
                var others = player.GetInventoryItemsOfWCID(FraysLongTerm.VaultKeyring).Concat(player.GetInventoryItemsOfWCID(FraysLongTerm.DreamKey))
                    .Where(k => k != null && k.Guid != usedKey.Guid && (k.Structure ?? 0) > 0).ToList();
                var held = (usedKey.Structure ?? 1) + others.Sum(k => k.Structure ?? 0);
                var want = MoreKeys(held);
                var taken = 0;

                // the used key keeps one use for UnlockerHelper.ConsumeUnlocker
                var fromUsed = Math.Min(want, Math.Max(0, (usedKey.Structure ?? 1) - 1));

                if (fromUsed > 0)
                {
                    usedKey.Structure = (ushort)(usedKey.Structure.Value - fromUsed);
                    usedKey.Value = Math.Max(0, (usedKey.Value ?? 0) - usedKey.StructureUnitValue * fromUsed);
                    taken += fromUsed;
                }

                foreach (var key in others)
                {
                    if (taken >= want)
                        break;

                    var has = key.Structure ?? 0;
                    var take = Math.Min(want - taken, has);

                    if (take >= has)
                    {
                        if (!player.TryConsumeFromInventoryWithNetworking(key, 1))
                            continue;
                    }
                    else
                    {
                        key.Structure = (ushort)(has - take);
                        key.Value = Math.Max(0, (key.Value ?? 0) - key.StructureUnitValue * take);
                        player.Session?.Network.EnqueueSend(new GameMessagePublicUpdatePropertyInt(key, PropertyInt.Structure, (int)key.Structure));
                    }

                    taken += take;
                }

                if (taken <= 0)
                    return;

                moreFills.AddOrUpdate(vault.Guid.Full, taken, (_, n) => n + taken);
                FraysManager.Tell(player, $"{taken + 1} Vault Keys open the Ember Vault at once: {taken + 1} fills of loot in the one chest (up to {MaxKeysAtOnce} keys a use).");
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] taking more Vault Keys for {player.Name} threw", ex);
            }
        }

        /// <summary>Called as a vault opens with fresh contents (Chest.Open, before its reset is scheduled): its bonus, and the fills more keys paid for.</summary>
        public static void OnFreshOpen(Chest vault)
        {
            if (vault == null)
                return;

            var embers = AddBonus(vault);

            if (moreFills.TryRemove(vault.Guid.Full, out var more) && more > 0)
            {
                try
                {
                    // room for every fill: its gear, the salvage bag and the rest
                    vault.ItemCapacity = byte.MaxValue;
                    var treasure = DatabaseManager.World.GetCachedDeathTreasure(VaultWcid);

                    for (var i = 0; i < more; i++)
                    {
                        if (treasure != null)
                            foreach (var item in LootGenerationFactory.CreateRandomLootObjects(treasure) ?? new List<WorldObject>())
                                if (!vault.TryAddToInventory(item))
                                    item.Destroy();

                        embers += AddBonus(vault);
                    }
                }
                catch (Exception ex)
                {
                    log.Error("[FRAYS] vault extra fills threw", ex);
                }
            }

            // every fill's Embers in one pile
            var max = Math.Max(1, DatabaseManager.World.GetCachedWeenie(FraysDefs.EmberWcid)?.GetProperty(PropertyInt.MaxStackSize) ?? 1000);

            for (var left = embers; left > 0;)
            {
                var stack = Math.Min(left, max);
                Add(vault, FraysDefs.EmberWcid, stack);
                left -= stack;
            }
        }

        /// <summary>One fill's bonus items (Embers, a salvage bag and the rest). The fill's Embers are returned, not added, so every fill's pile up as one.</summary>
        public static int AddBonus(Chest vault)
        {
            if (vault == null || !FraysSettings.Toggle(FraysSettings.VaultBonusEnabled))
                return 0;

            var vaultEmbers = 0;

            try
            {
                vaultEmbers = ThreadSafeRandom.Next(FraysSettings.Int(FraysSettings.VaultEmbersMin), FraysSettings.Int(FraysSettings.VaultEmbersMax));
                FraysMetrics.AddPositive(FraysMetrics.EmbersVault, vaultEmbers);

                // A full salvage bag (the Vault matches a Threads boss cache). A Threads cache's bag is
                // its gem's material; the Frays have no gem, so the material is drawn from every salvage bag there is.
                if (Roll(FraysSettings.Number(FraysSettings.VaultSalvageChance)))
                    AddSalvageBag(vault);

                if (Roll(FraysSettings.Number(FraysSettings.VaultScarabChance)))
                    Add(vault, DiamondScarab, 1);

                if (Roll(FraysSettings.Number(FraysSettings.VaultMoteChance)))
                {
                    var motes = ThreadSafeRandom.Next(1, Math.Max(1, FraysSettings.Int(FraysSettings.VaultMotesMax)));

                    for (var i = 0; i < motes; i++)
                        Add(vault, PyrealMote, 1);
                }
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] vault bonus threw", ex);
            }

            return vaultEmbers;
        }

        /// <summary>Pure: the salvage bag wcids a Vault can give (every material with a bag; the category rows are 0).</summary>
        public static uint[] SalvageBagWcids() => Player.MaterialSalvage.Values.Where(w => w > 0).Distinct().Select(w => (uint)w).ToArray();

        /// <summary>One full bag of a random material, tier 8 workmanship, named as every other bag (as ThreadDungeonRewardSpawner.BuildSalvageBags).</summary>
        private static void AddSalvageBag(Chest vault)
        {
            var bags = SalvageBagWcids();

            if (bags.Length == 0)
                return;

            var bag = WorldObjectFactory.CreateNewWorldObject(bags[ThreadSafeRandom.Next(0, bags.Length - 1)]);

            if (bag == null)
                return;

            bag.Structure = bag.MaxStructure ?? 100;
            bag.Name = $"Salvage ({bag.Structure})";
            bag.NumItemsInMaterial = 1;
            bag.ItemWorkmanship = Math.Clamp(WorkmanshipChance.Roll(8), 1, 10);

            if (!vault.TryAddToInventory(bag))
                bag.Destroy();
        }

        private static bool Roll(double chance) => chance > 0 && ThreadSafeRandom.Next(0.0f, 1.0f) < chance;

        private static void Add(Chest vault, uint wcid, int amount)
        {
            if (wcid == 0 || amount <= 0)
                return;

            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (wo == null)
                return;

            if (amount > 1)
                wo.SetStackSize(Math.Min(amount, wo.MaxStackSize ?? amount));

            if (!vault.TryAddToInventory(wo))
                wo.Destroy();
        }
    }
}
