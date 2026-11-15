using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Fray-Warden's guide as popup pages, easier to read than one long block of text. Talking to the Warden opens the menu page; /fray guide [page] opens any
    /// page. A popup stays on screen till dismissed, so nothing scrolls away under combat.
    /// </summary>
    public static class FraysGuide
    {
        public static readonly IReadOnlyList<(string Key, string Title)> Pages = new List<(string, string)>
        {
            ("how", "How the Frays work"),
            ("heat", "Heat and boons"),
            ("snarls", "Snarls and the Great Snarl"),
            ("camps", "Campfires and coords"),
            ("week", "This week"),
            ("progress", "Your progress"),
        };

        /// <summary>The menu: what each page is, and how to open it.</summary>
        public static string Menu() =>
            "THE FRAY-WARDEN\n\nThe Weave is thin here and the Drift pours through. Light a campfire and burn it back.\n\n"
            + "Open a page with /fg <page>:\n\n"
            + string.Join("\n", Pages.Select(p => $"  /fg {p.Key} - {p.Title}"))
            + "\n\nThe sign beside me has the short version.";

        /// <summary>A page's text, or null when there is no such page.</summary>
        public static string Page(Player player, string key)
        {
            var rec = FraysManager.Record(player);

            switch ((key ?? "").Trim().ToLowerInvariant())
            {
                case "how":
                    return "HOW THE FRAYS WORK\n\n"
                        // Four steps only, to avoid information overload; the rest is learned as it happens
                        + "1. /fc - find a free campfire.\n"
                        + "2. Use it to light it. It is yours alone.\n"
                        + "3. Kill the creatures inside the ring of wisps.\n"
                        + "4. Kills raise your Heat: tougher creatures, more XP, Embers and loot.\n\n"
                        + "/fg opens the full guide.";

                case "heat":
                    return "HEAT AND BOONS\n\n"
                        + "Heat climbs as you kill. Stop for 2 and a half minutes and it cools. Dying costs Heat.\n\n"
                        + $"Ember Boons: every {FraysSettings.Int(FraysSettings.BoonEvery)} Heat, a choice of three boons for this camp. Pick with /fp 1, 2 or 3, or your plan picks after {FraysSettings.Int(FraysSettings.BoonAutoSeconds)} seconds (/fray boons opens a window to put the boons in your order).\n\n"
                        + "/fp shows your Heat and what comes next.";

                case "snarls":
                    return "SNARLS AND THE GREAT SNARL\n\n"
                        + "Your kills fill your Snarl bar. Full, your Snarl comes: a big creature with a red glow. Kill it for a cache of loot, and maybe a Fray Effigy.\n\n"
                        + "Everyone in the Fray fills the Great Snarl bar together. Full, the whole Fray is told where it comes. It glows purple and is twice the size. Everyone who fights it is blessed (more Embers and damage for 30 minutes).\n\n"
                        + "Your camp keeps while you go to fight it.";

                case "camps":
                    return "CAMPFIRES AND COORDS\n\n" + string.Join("\n", FraysManager.CampsLines(player).Select(l => l.Trim())) + "\n\n/fc shows this any time.";

                case "week":
                    return "THIS WEEK\n\n" + FraysWeek.Summary() + "\n\n" + string.Join("\n\n", FraysFamilies.WeekLines());

                case "progress":
                    return "YOUR PROGRESS\n\n"
                        + $"Heat rating: {rec.Rating} (group camps: {rec.GroupRating})\n"
                        + $"Best Heat: {rec.BestHeat}\n"
                        + $"Embers earned: {Math.Floor(rec.Embers):0} ({Math.Floor(rec.WeekEmbers):0} this week)\n"
                        + $"Campfire tier: {rec.FireTier}\n"
                        + $"Achievements: {rec.Achievements?.Count ?? 0} of {FraysCodex.Achievements.Count}\n\n"
                        + "/fray codex - your collection\n/frays report - your last camp";

                default:
                    return null;
            }
        }

        /// <summary>Opens a guide page (or the menu) as a popup.</summary>
        public static string Show(Player player, string key, string section = null)
        {
            var lower = (key ?? "").Trim().ToLowerInvariant();

            // the Codex is not in the guide (/fg); it points to /fray codex instead
            if (lower == "codex" || lower == "achievements")
                return "The Codex and achievements are /fray codex [section] (or /fray journal for the book).";

            var text = string.IsNullOrWhiteSpace(key) ? Menu() : Page(player, key);

            if (text == null)
                return $"No guide page '{key}'. Pages: {string.Join(", ", Pages.Select(p => p.Key))}.";

            player.Session?.Network.EnqueueSend(new GameEventPopupString(player.Session, text));
            return null;
        }
    }
}
