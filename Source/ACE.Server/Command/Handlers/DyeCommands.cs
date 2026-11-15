using System;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Dye;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// "/dye": load the Prismatic Dye Pot with a colour, list colours, inspect an item's colour slots.
    /// Player-facing text uses ASCII hyphens only.
    /// </summary>
    public static class DyeCommands
    {
        private const string Usage = "/dye list [group] | /dye <colour> [slot] | /dye original [slot] | /dye info";

        [CommandHandler("dye", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Custom dye: load your Prismatic Dye Pot with a colour, then use the pot on your item.", Usage)]
        public static void HandleDye(Session session, params string[] parameters)
        {
            var player = session?.Player;
            if (player == null)
                return;

            var args = parameters ?? Array.Empty<string>();
            if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                CustomDye.Tell(player, "Custom dye: type /dye <colour> to load your Prismatic Dye Pot, then use the pot on armour or clothing you carry or wear. Add a slot number to dye one colour slot only, for example /dye navy 2. /dye info shows the slots of the item you last examined. /dye original removes dye for free. /dye list shows the colours.");
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "list":
                case "colours":
                case "colors":
                    List(player, args.Length > 1 ? string.Join(" ", args.Skip(1)) : null);
                    return;

                case "info":
                    var item = CommandHandlerHelper.GetLastAppraisedObject(session);
                    if (item == null)
                    {
                        CustomDye.Tell(player, "Examine an item first, then type /dye info.");
                        return;
                    }
                    CustomDye.Tell(player, CustomDye.WhyNot(item) ?? CustomDye.Describe(item));
                    return;
            }

            // /dye <colour words...> [slot]
            var slot = -1;
            var words = args.ToList();
            if (words.Count > 1)
            {
                var last = words[^1].ToLowerInvariant();
                if (last == "all")
                    words.RemoveAt(words.Count - 1);
                else if (int.TryParse(last, out var n))
                {
                    if (n < 1 || n > CustomDye.MaxSlots)
                    {
                        CustomDye.Tell(player, $"Colour slots are numbered 1 to {CustomDye.MaxSlots}. /dye info shows how many an item has.");
                        return;
                    }
                    slot = n - 1;
                    words.RemoveAt(words.Count - 1);
                }
            }

            var name = string.Join(" ", words);
            string key;
            string shown;
            if (DyeColours.Normalise(name) is "original" or "none" or "clear" or "remove")
            {
                key = CustomDye.OriginalKey;
                shown = "Original (removes dye)";
            }
            else
            {
                var colour = DyeColours.Find(name);
                if (colour == null)
                {
                    CustomDye.Tell(player, $"There is no dye colour called {name}. /dye list shows them all.");
                    return;
                }
                // The Sky colours are bought with Sky Seals.
                if (!Frays.FraysSky.DyeAllowed(player, colour.Group))
                {
                    CustomDye.Tell(player, $"{colour.Name} is a Sky colour, bought with Sky Seals: a Sky Dye Charter, from the Sky Steward on the Sky Fray plateau.");
                    return;
                }
                key = colour.Key;
                shown = colour.Name;
            }

            if (CustomDye.FindPot(player) == null)
            {
                CustomDye.Tell(player, "You need a Prismatic Dye Pot in your pack.");
                return;
            }

            // A pot locks to the colour of its first dye: load one locked to this colour, or a fresh one.
            var pot = CustomDye.FindPot(player, key);
            if (pot == null)
            {
                var locks = player.GetAllPossessions().Where(CustomDye.IsPot).Select(CustomDye.LockedColour).Where(l => l != null).Distinct()
                    .Select(l => DyeColours.Find(l)?.Name ?? l);
                CustomDye.Tell(player, $"Every Prismatic Dye Pot you carry is locked to another colour ({string.Join(", ", locks)}). A fresh pot takes any colour; its first dye locks it.");
                return;
            }

            CustomDye.Load(player, pot, key, slot);
            CustomDye.Tell(player, $"Your {pot.Name} now holds {shown}{(slot < 0 ? " for every colour slot" : $" for colour slot {slot + 1}")}. Use it on the item you want to dye.");
        }

        private static void List(Player player, string group)
        {
            if (group != null)
            {
                var colours = DyeColours.InGroup(group).ToList();
                if (colours.Count == 0)
                {
                    CustomDye.Tell(player, $"No colour group called {group}. Groups: {string.Join(", ", DyeColours.Groups)}.");
                    return;
                }
                CustomDye.Tell(player, $"{colours[0].Group}: {string.Join(", ", colours.Select(c => c.Name))}.");
                return;
            }

            CustomDye.Tell(player, $"{DyeColours.All.Count} dye colours:");
            foreach (var g in DyeColours.Groups)
                CustomDye.Tell(player, $"{g}: {string.Join(", ", DyeColours.InGroup(g).Select(c => c.Name))}.");
        }
    }
}
