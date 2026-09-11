using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Tables.Wcids;
using ACE.Server.WorldObjects;

namespace ACE.Server.RefireStations
{
    /// <summary>
    /// The Brewer's Cauldron: a Marketplace NPC (wcid 1002759, PropertyBool.BrewersCauldron). A player GIVES a
    /// piece of jewelry (ring, bracelet or necklace); it asks a yes/no confirmation to pay 25 Trade Notes
    /// (250,000, wcid 20630, MMDs) to boil away ONE random spell that is either a level VIII spell or a
    /// cantrip (any tier: minor/major/epic/legendary) and pour in ONE random tavern blessing in its place - a
    /// "beer buff" that grants +50 to a single attribute (Strength, Endurance, Coordination, Quickness, Focus
    /// or Self). The result is a gamble on BOTH ends: which eligible spell is removed and which of the six
    /// blessings is added are each drawn uniformly at random. The item is never taken from the player.
    ///
    /// ONE BLESSING PER PIECE: if the item already carries any of the six tavern-blessing spells, the station
    /// refuses outright - stacking two +50s to the same or different attributes on one piece is not offered.
    /// Spell count is preserved (one out, one in), so ItemSpellcraft/ItemMaxMana/ManaRate are left untouched.
    ///
    /// The six tavern-blessing spell ids (verified 2026-08-19 against local ace_world: SELECT id,name,
    /// stat_Mod_Type,stat_Mod_Key,stat_Mod_Val FROM spell WHERE id IN (3530,3531,3533,3862,3863,3864) - all
    /// six are stat_Mod_Type 36865, +50 to one attribute key). Deliberately excludes 3532 (Bobo's Focused
    /// Blessing), 2226 and 2227 - not on the owner's list.
    /// </summary>
    public static class BrewersCauldronStation
    {
        public const int CauldronCostNotes = 25;

        private const string InsufficientFundsMessage = "You need 25 Trade Notes (250,000), in your pack or as 6,250,000 banked pyreals, to use the Brewer's Cauldron.";

        /// <summary>
        /// The six tavern-blessing spell ids. Each is +50 to one attribute (stat_Mod_Type 36865).
        /// </summary>
        public static readonly int[] TavernBlessings =
        {
            3533, // Brighteyes' Favor - +50 Coordination (key 4)
            3864, // Zongo's Fist - +50 Strength (key 1)
            3531, // Bobo's Quickening - +50 Quickness (key 3)
            3862, // Duke Raoul's Pride - +50 Self (key 6)
            3863, // Hunter's Hardiness - +50 Endurance (key 2)
            3530, // Ketnan's Eye - +50 Focus (key 5)
        };

        // ---------------- pure helpers ----------------

        /// <summary>
        /// TRUE if <paramref name="spellId"/> is one of the six tavern-blessing spells.
        /// </summary>
        /// <summary>
        /// Owner rule (2026-08-19): the cauldron brews LOOT jewelry only - never quest jewelry. "Loot" is decided by
        /// the weenie class: a piece is brewable only if its wcid is one the treasure system itself rolls
        /// (JewelryWcids, the union of every tier table in Factories/Tables/Wcids/JewelryWcids.cs). Quest rewards
        /// are their own wcids, so they fall outside that set regardless of what spells they carry. Fork wcids are
        /// above ushort range and can never be in the retail loot enum, so they are rejected before the cast.
        /// </summary>
        public static bool IsLootJewelryWcid(uint wcid)
        {
            if (wcid > ushort.MaxValue)
                return false;

            return JewelryWcids.Contains((ACE.Server.Factories.Enum.WeenieClassName)wcid);
        }

        public static bool IsTavernBlessing(int spellId) => Array.IndexOf(TavernBlessings, spellId) >= 0;

        /// <summary>
        /// TRUE if <paramref name="spellbook"/> already carries any tavern blessing, and outs the FIRST one
        /// found (spellbook order is not guaranteed, but the item is only ever meant to carry one - VerifyEligible
        /// refuses before a second could ever be added).
        /// </summary>
        public static bool HasTavernBlessing(IEnumerable<int> spellbook, out int blessingId)
        {
            foreach (var id in spellbook)
            {
                if (IsTavernBlessing(id))
                {
                    blessingId = id;
                    return true;
                }
            }

            blessingId = 0;
            return false;
        }

        /// <summary>
        /// TRUE if <paramref name="spellId"/> is a level VIII spell or a cantrip (any tier). A tavern blessing
        /// itself is never brewable, even though HasTavernBlessing's outright refusal already runs first - this
        /// guard is a belt-and-suspenders check on the pure function alone.
        /// </summary>
        public static bool IsBrewable(int spellId, Func<int, int> levelOf, Func<int, bool> isCantrip)
        {
            if (IsTavernBlessing(spellId))
                return false;

            return levelOf(spellId) == 8 || isCantrip(spellId);
        }

        /// <summary>
        /// The brewable candidates in <paramref name="spellbook"/>, sorted ascending so a given spellbook
        /// yields a deterministic candidate list (the randomness lives only in PickRandom's index draw).
        /// </summary>
        public static List<int> BrewableSpells(IEnumerable<int> spellbook, Func<int, int> levelOf, Func<int, bool> isCantrip)
        {
            return spellbook
                .Where(id => IsBrewable(id, levelOf, isCantrip))
                .OrderBy(id => id)
                .ToList();
        }

        /// <summary>
        /// Draws one candidate uniformly from <paramref name="candidates"/> via <paramref name="next"/>, a
        /// (minInclusive, maxInclusive) random function - the live code passes ThreadSafeRandom.Next, which
        /// treats its max argument as INCLUSIVE, so the caller must pass count - 1, not count.
        /// </summary>
        public static int PickRandom(IReadOnlyList<int> candidates, Func<int, int, int> next)
        {
            var index = next(0, candidates.Count - 1);

            return candidates[index];
        }

        // ---------------- live-runtime lookups ----------------

        private static bool IsCantrip(int spellId) =>
            LootTables.MinorCantrips.Contains(spellId) ||
            LootTables.MajorCantrips.Contains(spellId) ||
            LootTables.EpicCantrips.Contains(spellId) ||
            LootTables.LegendaryCantrips.Contains(spellId);

        private static int LevelOf(int spellId) => SpellLevelCache.GetSpellLevel(spellId);

        private static List<int> GetSpellbookIds(WorldObject item)
        {
            item.BiotaDatabaseLock.EnterReadLock();
            try
            {
                return item.Biota.PropertiesSpellBook == null
                    ? new List<int>()
                    : item.Biota.PropertiesSpellBook.Keys.ToList();
            }
            finally
            {
                item.BiotaDatabaseLock.ExitReadLock();
            }
        }

        // ---------------- eligibility (needs a live Player/WorldObject) ----------------

        public static WeenieError VerifyEligible(Player player, WorldObject item, out string refusal)
        {
            refusal = null;

            if (item == null)
            {
                refusal = "The Brewer's Cauldron needs a piece of jewelry handed to it.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.FindObject(item.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                refusal = "The Brewer's Cauldron only works on jewelry in your pack, not a piece you are wearing.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (RefireStationCommon.IsStacked(item.StackSize))
            {
                refusal = "The Brewer's Cauldron cannot work on a stack - split it first.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (item.ItemType != ItemType.Jewelry)
            {
                refusal = "The Brewer's Cauldron cannot work on that: it only brews into jewelry (rings, bracelets, necklaces).";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }
            if (!IsLootJewelryWcid(item.WeenieClassId))
            {
                refusal = "The Brewer's Cauldron cannot work on that: it only brews into jewelry found as loot, never quest jewelry.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            var spellbook = GetSpellbookIds(item);

            if (HasTavernBlessing(spellbook, out var blessingId))
            {
                var blessingName = new Spell(blessingId).Name;
                refusal = $"The Brewer's Cauldron cannot work on that: it already carries a tavern blessing ({blessingName}), and one piece holds only one.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (BrewableSpells(spellbook, LevelOf, IsCantrip).Count == 0)
            {
                refusal = "The Brewer's Cauldron cannot work on that: it carries no level VIII spell or cantrip to brew away.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.IsBusy)
            {
                refusal = "You are too busy for that right now.";
                return WeenieError.YoureTooBusy;
            }

            return WeenieError.None;
        }

        // ---------------- entry point (Player_Inventory.GiveObjectToNPC) ----------------

        public static void HandleGive(Player player, WorldObject cauldron, WorldObject item)
        {
            RefireStationCommon.HandleGive(
                player, cauldron, item,
                enabledPropertyName: null,
                costNotes: CauldronCostNotes,
                insufficientFundsMessage: InsufficientFundsMessage,
                verify: VerifyEligible,
                buildPrompt: BuildPrompt,
                apply: Apply);
        }

        private static string BuildPrompt(Player player, WorldObject item)
        {
            return $"Brew away one of {item.Name}'s spells for 25 Trade Notes (250,000)? The Cauldron takes a random level VIII spell or cantrip off it and pours in a random tavern blessing (+50 to one attribute). Which spell goes and which blessing comes are both the luck of the draw.";
        }

        private static string Apply(Player player, WorldObject item)
        {
            var spellbook = GetSpellbookIds(item);
            var candidates = BrewableSpells(spellbook, LevelOf, IsCantrip);

            var oldSpellId = PickRandom(candidates, ThreadSafeRandom.Next);
            var newSpellId = PickRandom(TavernBlessings, ThreadSafeRandom.Next);

            var oldSpellName = new Spell(oldSpellId).Name;
            var newSpellName = new Spell(newSpellId).Name;

            item.Biota.TryRemoveKnownSpell(oldSpellId, item.BiotaDatabaseLock);
            item.Biota.GetOrAddKnownSpell(newSpellId, item.BiotaDatabaseLock, out _);

            item.ChangesDetected = true;
            item.SaveBiotaToDatabase();

            RefireStationCommon.UpdateObj(player, item);

            return $"The Brewer's Cauldron boils {oldSpellName} out of the {item.Name} and pours in {newSpellName}. 25 Trade Notes (250,000) spent.";
        }
    }
}
