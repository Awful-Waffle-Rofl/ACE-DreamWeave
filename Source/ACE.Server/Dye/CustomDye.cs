using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Dye
{
    /// <summary>
    /// Custom dye: a player recolours their own worn armour or clothing (not weapons, casters or shields) to any DyeColours entry,
    /// with no client patch.
    ///
    /// Flow: /dye &lt;colour&gt; [slot] loads the Prismatic Dye Pot (WCID 2000192) in the player's pack; using the pot
    /// on an item applies it and spends one use. /dye original loads "back to its own colour", which is free.
    ///
    /// Storage: PropertyString.DyeColours on the item, one entry per colour slot. It is a biota property, so it
    /// saves with the item, follows it through trade, drops and storage, and the stock ClothingTable look is
    /// untouched underneath. A slot is one CloSubPalette of the item's ClothingTable entry for its PaletteTemplate
    /// (most armour has 1 to 4: body, trim, and so on). At render time each slot's ranges get the palette
    /// DyeMatcher picks for that range.
    ///
    /// Player-facing text uses ASCII hyphens only (no em or en dashes).
    /// </summary>
    public static class CustomDye
    {
        public const uint PotWcid = 2000192;

        /// <summary>Loaded value meaning "back to the item's own colour".</summary>
        public const string OriginalKey = "original";

        public const int MaxSlots = 8;

        private const ItemType DyeableTypes = ItemType.Armor | ItemType.Clothing;

        // ---- reading an item's dye --------------------------------------------------------------------------------

        public static bool IsDyed(WorldObject wo) => !string.IsNullOrEmpty(wo?.GetProperty(PropertyString.DyeColours));

        public static string[] GetSlots(WorldObject wo)
        {
            var raw = wo?.GetProperty(PropertyString.DyeColours);
            return string.IsNullOrEmpty(raw) ? Array.Empty<string>() : raw.Split(',');
        }

        public static DyeColour ColourForSlot(WorldObject wo, int slot)
        {
            var slots = GetSlots(wo);
            return slot >= 0 && slot < slots.Length && slots[slot].Length > 0 ? DyeColours.Find(slots[slot]) : null;
        }

        /// <summary>
        /// The palette entries one ClothingTable range becomes on this item: the item's own palette when the slot is
        /// not dyed, else the matched palette. With protectBaseAppearance (an item worn by a creature), the part of
        /// the range below the skin, hair and eye block keeps the item's own palette, so a dye never recolours skin.
        /// </summary>
        public static IEnumerable<(uint pal, ushort offset, ushort length)> Segments(WorldObject item, int slot, uint itemPal, ushort offset, ushort length, bool protectBaseAppearance)
        {
            var colour = ColourForSlot(item, slot);
            if (colour == null)
            {
                yield return (itemPal, offset, length);
                yield break;
            }

            var end = offset + (length == 0 ? 256 : length);
            var baseEnd = ACE.Server.Entity.ClothingPartExclusions.BaseAppearancePaletteEnd;

            if (protectBaseAppearance && offset < baseEnd)
            {
                var keep = Math.Min(end, baseEnd);
                yield return (itemPal, offset, (ushort)(keep - offset));
                if (end <= baseEnd)
                    yield break;
                offset = baseEnd;
                length = (ushort)(end - baseEnd);
            }

            // A long range (some robes and coats use a large part of the palette in one range) holds several materials, and no one
            // palette fits them all; matched in chunks, each part gets its own closest palette.
            var total = length == 0 ? 256 : length;
            if (total <= LongRange)
            {
                yield return (DyeMatcher.Match(itemPal, offset, (ushort)total, colour), offset, (ushort)total);
                yield break;
            }
            for (int start = offset; start < offset + total; start += ChunkUnits)
            {
                var len = (ushort)Math.Min(ChunkUnits, offset + total - start);
                yield return (DyeMatcher.Match(itemPal, start, len, colour), (ushort)start, len);
            }
        }

        /// <summary>Ranges longer than this many 8-colour units are matched in ChunkUnits pieces.</summary>
        public const int LongRange = 24;
        public const int ChunkUnits = 16;

        // ---- what can be dyed ---------------------------------------------------------------------------------------

        /// <summary>The item's colour slots as rendered: its ClothingTable entry for its PaletteTemplate (or the first).</summary>
        public static CloSubPalEffect SubPalEffect(WorldObject item)
        {
            if (item?.ClothingBase == null)
                return null;
            ClothingTable table;
            try { table = DatManager.PortalDat.ReadFromDat<ClothingTable>((uint)item.ClothingBase); } catch { return null; }
            if (table == null || table.ClothingSubPalEffects.Count == 0)
                return null;
            var option = (uint)(item.PaletteTemplate ?? 0);
            return table.ClothingSubPalEffects.TryGetValue(option, out var eff) ? eff : table.ClothingSubPalEffects.Values.First();
        }

        public static int SlotCount(WorldObject item) => Math.Min(MaxSlots, SubPalEffect(item)?.CloSubPalettes.Count ?? 0);

        /// <summary>Null when the item can be dyed, else the reason it cannot (player-facing).</summary>
        public static string WhyNot(WorldObject item)
        {
            if (item == null)
                return "That is not something you can dye.";
            if ((item.ItemType & DyeableTypes) == 0)
                return $"The {item.Name} cannot be dyed. Dye works on armour and clothing only.";
            if (((item.ValidLocations ?? EquipMask.None) & EquipMask.Shield) != 0)
                return $"The {item.Name} cannot be dyed. Dye works on armour and clothing only.";
            if (item.Biota.PropertiesPalette.GetCount(item.BiotaDatabaseLock) > 0)
                return $"The {item.Name} has a special look that dye cannot change.";
            if (SlotCount(item) == 0)
                return $"The {item.Name} has no colours that dye can change.";
            return null;
        }

        // ---- applying -------------------------------------------------------------------------------------------------

        /// <summary>Sets one slot (0-based) or every slot (slot &lt; 0) to the colour key, or back to its own colour for OriginalKey.</summary>
        public static void Apply(WorldObject item, string key, int slot)
        {
            var count = SlotCount(item);
            var slots = GetSlots(item).Concat(Enumerable.Repeat("", MaxSlots)).Take(count).ToArray();
            var value = key == OriginalKey ? "" : key;

            for (int i = 0; i < count; i++)
                if (slot < 0 || i == slot)
                    slots[i] = value;

            if (slots.All(s => s.Length == 0))
                item.RemoveProperty(PropertyString.DyeColours);
            else
                item.SetProperty(PropertyString.DyeColours, string.Join(",", slots).TrimEnd(','));
        }

        /// <summary>Re-sends the item's look to everyone who can see it.</summary>
        public static void Refresh(Player player, WorldObject item)
        {
            if (item.CurrentWieldedLocation != null && player.EquippedObjects.ContainsKey(item.Guid))
            {
                player.EnqueueBroadcast(new GameMessageObjDescEvent(player));
                // Weapons and shields in hand are their own objects on the client.
                if (item.ParentLocation != null)
                    player.EnqueueBroadcast(new GameMessageObjDescEvent(item));
            }
            else if (item.CurrentLandblock != null)
                item.EnqueueBroadcast(new GameMessageObjDescEvent(item));
            else
                player.Session.Network.EnqueueSend(new GameMessageUpdateObject(item));
        }

        public static string Describe(WorldObject item)
        {
            var count = SlotCount(item);
            var parts = new List<string>();
            for (int i = 0; i < count; i++)
                parts.Add($"{i + 1}: {ColourForSlot(item, i)?.Name ?? "its own colour"}");
            return $"{item.Name} has {count} colour slot{(count == 1 ? "" : "s")}. {string.Join(", ", parts)}.";
        }

        // ---- the pot ----------------------------------------------------------------------------------------------------

        public static bool IsPot(WorldObject wo) => wo != null && wo.WeenieClassId == PotWcid;

        /// <summary>
        /// The pot's stored text: "key[:slot]" for what it holds, then "@colour" once it is locked (a
        /// pot has 15 uses and locks to the colour of its first use; /dye original stays free and always allowed).
        /// </summary>
        public static (string key, int slot) Loaded(WorldObject pot) => ParseLoaded(pot?.GetProperty(PropertyString.DyePotColour));

        /// <summary>The colour the pot is locked to (its first dye), or null while it is fresh.</summary>
        public static string LockedColour(WorldObject pot) => ParseLock(pot?.GetProperty(PropertyString.DyePotColour));

        public static string ParseLock(string raw)
        {
            var at = raw?.IndexOf('@') ?? -1;
            return at >= 0 && at < raw.Length - 1 ? raw[(at + 1)..] : null;
        }

        /// <summary>Pure: the stored text for a loaded colour and slot, keeping the lock.</summary>
        public static string Stored(string key, int slot, string locked) =>
            (key == null ? "" : slot < 0 ? key : $"{key}:{slot + 1}") + (string.IsNullOrEmpty(locked) ? "" : "@" + locked);

        /// <summary>Pure: may a pot locked to <paramref name="locked"/> take <paramref name="key"/>?</summary>
        public static bool CanLoad(string locked, string key) => key == OriginalKey || string.IsNullOrEmpty(locked) || locked == key;

        public static (string key, int slot) ParseLoaded(string raw)
        {
            var at = raw?.IndexOf('@') ?? -1;
            if (at >= 0)
                raw = raw[..at];
            if (string.IsNullOrEmpty(raw))
                return (null, -1);
            var bits = raw.Split(':');
            var slot = bits.Length > 1 && int.TryParse(bits[1], out var s) ? s - 1 : -1;
            return (bits[0], slot);
        }

        public static string LoadedText(WorldObject pot)
        {
            var (key, slot) = Loaded(pot);
            if (key == null)
                return "nothing";
            var name = key == OriginalKey ? "Original (removes dye)" : DyeColours.Find(key)?.Name ?? key;
            return slot < 0 ? name : $"{name}, colour slot {slot + 1}";
        }

        public static string LockText(WorldObject pot) =>
            LockedColour(pot) is string l ? $" It is locked to {DyeColours.Find(l)?.Name ?? l}." : " Its first dye locks it to that colour.";

        public static void Load(Player player, WorldObject pot, string key, int slot)
        {
            pot.SetProperty(PropertyString.DyePotColour, Stored(key, slot, LockedColour(pot)));
            player.Session.Network.EnqueueSend(new GameMessageUpdateObject(pot));
        }

        public static WorldObject FindPot(Player player) =>
            player.GetAllPossessions().FirstOrDefault(IsPot);

        /// <summary>The pot to load <paramref name="key"/> into: one locked to that colour, else a fresh one, else any (for original); null when every pot is locked to another colour.</summary>
        public static WorldObject FindPot(Player player, string key)
        {
            var pots = player.GetAllPossessions().Where(IsPot).ToList();
            return pots.FirstOrDefault(p => LockedColour(p) == key)
                ?? pots.FirstOrDefault(p => LockedColour(p) == null)
                ?? (key == OriginalKey ? pots.FirstOrDefault() : null);
        }

        public static void Tell(Player player, string text) =>
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        /// <summary>Using the pot on its own explains it. True when handled.</summary>
        public static bool TryUse(Player player, WorldObject item)
        {
            if (!IsPot(item))
                return false;
            Tell(player, $"The {item.Name} holds {LoadedText(item)}. Type /dye <colour> to load a colour (/dye list shows them all), then use the pot on your own armour or clothing. /dye original removes dye for free. Uses left: {item.Structure ?? 0}.{LockText(item)}");
            return true;
        }

        /// <summary>Using the pot on an item dyes it. True when handled (including refusals).</summary>
        public static bool TryUseOn(Player player, WorldObject source, WorldObject target)
        {
            if (!IsPot(source))
                return false;

            var (key, slot) = Loaded(source);
            if (key == null)
            {
                Tell(player, $"The {source.Name} is empty. Type /dye <colour> first, for example /dye crimson. /dye list shows every colour.");
                return true;
            }

            var owned = target != null && (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems) != null);
            if (!owned)
            {
                Tell(player, "You can only dye items you are carrying or wearing.");
                return true;
            }

            var why = WhyNot(target);
            if (why != null)
            {
                Tell(player, why);
                return true;
            }

            var count = SlotCount(target);
            if (slot >= count)
            {
                Tell(player, $"The {target.Name} only has {count} colour slot{(count == 1 ? "" : "s")}. Load the colour for slot 1 to {count}, or for all slots.");
                return true;
            }

            var free = key == OriginalKey;
            if (!free)
            {
                var uses = source.Structure ?? 0;
                if (uses <= 0)
                {
                    Tell(player, $"The {source.Name} is dry.");
                    return true;
                }

                if (!CanLoad(LockedColour(source), key))
                {
                    Tell(player, $"The {source.Name} is locked to {DyeColours.Find(LockedColour(source))?.Name ?? LockedColour(source)}.");
                    return true;
                }
            }

            Apply(target, key, slot);
            Refresh(player, target);

            var what = free ? "back to its own colour" : DyeColours.Find(key)?.Name ?? key;
            var where = slot < 0 ? "" : $" (colour slot {slot + 1})";

            if (free)
            {
                Tell(player, $"The {target.Name} is {what} again{where}.");
                return true;
            }

            // The first dye locks the pot to its colour.
            if (LockedColour(source) == null)
                source.SetProperty(PropertyString.DyePotColour, Stored(key, slot, key));

            source.Structure = (ushort)((source.Structure ?? 1) - 1);
            if (source.Structure <= 0)
            {
                player.TryConsumeFromInventoryWithNetworking(source, 1);
                Tell(player, $"You dye the {target.Name} {what}{where}. The {source.Name} is used up.");
            }
            else
            {
                player.Session.Network.EnqueueSend(new GameMessageUpdateObject(source));
                Tell(player, $"You dye the {target.Name} {what}{where}. Uses left: {source.Structure}.");
            }
            return true;
        }
    }
}
