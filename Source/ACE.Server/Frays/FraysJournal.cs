using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Fray Journal (dreamweave-content 2000158): a book whose pages the server rewrites from the reader's
    /// own record each time it is opened, so the whole Frays record reads in the game's book window instead of chat -
    /// the dream companion, the collected Effigy forms (pets), the Codex sections and the achievements. Pages are
    /// written into the book itself (the game asks for each page by number) and never by the player.
    /// </summary>
    public static class FraysJournal
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(FraysJournal));

        public const uint Wcid = 2000158;

        /// <summary>The most text a page holds (the book's max_Num_Chars_Per_Page is 1000).</summary>
        public const int PageChars = 950;

        public const int MaxPages = 40;

        /// <summary>The Fray Journal, or the Snarl Trophy Wall sign, whose pages are also written on each read.</summary>
        public static bool IsJournal(WorldObject wo) => wo?.WeenieClassId == Wcid || FraysTrophyWall.Is(wo);

        /// <summary>/fray journal: a Journal into the pack, unless one is already there.</summary>
        public static string Give(Player player)
        {
            if (player.GetInventoryItemsOfWCID(Wcid).Count > 0)
                return "You already carry a Fray Journal. Open it to read your Frays record; it rewrites itself each time.";

            FraysLongTerm.Give(player, Wcid, 1, "A Fray Journal: open it to read your companion, pets, Codex and achievements.");
            return "Your Fray Journal is in your pack.";
        }

        /// <summary>
        /// The first talk with the Fray-Warden: a Fray Journal and a quest message, once per
        /// character. One already carried is kept (no second copy).
        /// </summary>
        /// <returns>True when the gift was given now (the first talk).</returns>
        public static bool WardenGift(Player player)
        {
            var rec = FraysManager.Record(player);

            if (rec.WardenJournalGiven)
                return false;

            rec.WardenJournalGiven = true;
            FraysManager.SaveRecord(player, rec);

            // quiet give: the one Warden line below says it (fewer messages at once)
            if (player.GetInventoryItemsOfWCID(Wcid).Count == 0)
                FraysLongTerm.Give(player, Wcid, 1, null);

            FraysManager.Shout(player, "Quest complete: Welcome to the Frays!");
            FraysManager.Tell(player, "Fray-Warden says, \"Take this Fray Journal - it writes itself as you go. My guide is open; /fg brings it back.\"");
            return true;
        }

        /// <summary>Rewrites the book's pages for <paramref name="player"/>. Called just before the game is sent the book.</summary>
        public static void Refresh(Book book, Player player)
        {
            if (book == null || player == null)
                return;

            try
            {
                var pages = (FraysTrophyWall.Is(book) ? FraysTrophyWall.Pages() : Pages(player)).Take(MaxPages).ToList();
                var data = pages.Select((text, i) => new PropertiesBookPageData
                {
                    AuthorId = 0xFFFFFFFF,
                    AuthorName = "",
                    AuthorAccount = "prewritten",
                    IgnoreAuthor = false,
                    PageText = text,
                }).ToList();

                book.BiotaDatabaseLock.EnterWriteLock();
                try
                {
                    book.Biota.PropertiesBookPageData ??= new List<PropertiesBookPageData>();
                    book.Biota.PropertiesBookPageData.Clear();
                    foreach (var page in data)
                        book.Biota.PropertiesBookPageData.Add(page);
                    book.Biota.PropertiesBook ??= new PropertiesBook();
                    book.Biota.PropertiesBook.MaxNumPages = Math.Max(1, data.Count);
                    book.Biota.PropertiesBook.MaxNumCharsPerPage = 1000;
                }
                finally
                {
                    book.BiotaDatabaseLock.ExitWriteLock();
                }

                book.SetProperty(PropertyInt.AppraisalPages, data.Count);
                book.SetProperty(PropertyInt.AppraisalMaxPages, Math.Max(1, data.Count));
                book.ChangesDetected = true;
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] writing {player.Name}'s Fray Journal threw", ex);
            }
        }

        /// <summary>The Journal's pages for this player, in order: cover, companion, pets, then each Codex section.</summary>
        public static List<string> Pages(Player player)
        {
            var rec = FraysManager.Record(player);
            var pages = new List<string>();

            pages.AddRange(Paginate(Cover(player, rec)));
            pages.AddRange(Paginate(CompanionPage(player)));

            foreach (var section in new[] { "forms", "families", "boons", "affixes", "achievements" })
                pages.AddRange(Paginate(FraysCodex.Readout(rec, section).Split('\n')));

            return pages;
        }

        private static IEnumerable<string> Cover(Player player, FraysPlayerRecord rec) => new[]
        {
            $"THE FRAY JOURNAL of {player.Name}",
            "",
            $"Heat rating: {rec.Rating} solo, {rec.GroupRating} group",
            $"Best Heat: {rec.BestHeat} solo, {rec.BestGroupHeat} group",
            $"Embers earned: {Math.Floor(rec.Embers):0} ({Math.Floor(rec.WeekEmbers):0} this week)",
            $"Campfire tier: {rec.FireTier}",
            $"Achievements: {rec.Achievements?.Count ?? 0} of {FraysCodex.Achievements.Count}",
            "",
            "Pages: companion, pets (forms), families, boons, weekly affixes, achievements.",
            "This book rewrites itself each time you open it.",
        };

        private static IEnumerable<string> CompanionPage(Player player) => new[]
        {
            "YOUR COMPANIONS",
            "",
            FraysLongTerm.Companion(player, Array.Empty<string>()),
            "",
            "Every collected form can be a companion with its own level.",
            "Give one a style (locked): /frays companion <form> warder, mender or striker.",
            "Kindling levels the active one (250 experience each, up to level 10). A Rebinding Ember changes a style.",
            "",
            "YOUR PETS",
            "",
            FraysEffigies.Pet(player, ""),
        };

        /// <summary>Lines into pages of at most PageChars characters; a page carries on the section's first line as its heading.</summary>
        public static List<string> Paginate(IEnumerable<string> lines)
        {
            var all = lines.ToList();
            var heading = all.FirstOrDefault() ?? "";
            var pages = new List<string>();
            var page = new List<string>();
            var size = 0;

            foreach (var raw in all)
            {
                var line = raw.Length > PageChars - heading.Length - 20 ? raw.Substring(0, PageChars - heading.Length - 20) : raw;

                if (size + line.Length + 1 > PageChars && page.Count > 0)
                {
                    pages.Add(string.Join("\n", page));
                    page = new List<string> { heading + " (continued)" };
                    size = page[0].Length + 1;
                }

                page.Add(line);
                size += line.Length + 1;
            }

            if (page.Count > 0)
                pages.Add(string.Join("\n", page));

            return pages;
        }
    }
}
