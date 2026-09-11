using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The Marketplace combined spell tutor NPC (wcid 1002800, marked via PropertyBool.SpellTutor). Tells to
    /// it are intercepted in GameActionTalkDirect before the normal emote-based OnTalkDirect handling.
    ///
    /// This NPC has no emote data of its own. Instead it reads the five retail-release "professor" NPCs
    /// (53381 war, 53382 creature, 53383 item, 53384 life, 53385 void), which are DB-only content present in
    /// every ace_world, and re-serves their per-level spell lists, skill gates and prices from one combined
    /// front end. A player says e.g. "item 3" and is offered every level 3 Item Enchantment spell for a single
    /// price, paid in pyreals from pack then bank (see Player_Bank.TrySpendPyrealsIncludingBank).
    /// </summary>
    public static class SpellTutor
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // school key -> retail professor NPC wcid. Verified identical emote structure across all five
        // (ReceiveTalkDirect "level N" -> InqYesNo -> TestSuccess "lvlNyesno" InqSkillStat; TestSuccess
        // "lvlNskillcheck" InqOwnsItems for price; GotoSet "lvlN" TeachSpell for the spell list).
        private static readonly Dictionary<string, uint> SchoolProfessorWcid = new Dictionary<string, uint>
        {
            ["war"] = 53381,
            ["creature"] = 53382,
            ["item"] = 53383,
            ["life"] = 53384,
            ["void"] = 53385,
        };

        private static readonly Dictionary<string, string> SchoolDisplayName = new Dictionary<string, string>
        {
            ["war"] = "War Magic",
            ["creature"] = "Creature Enchantment",
            ["item"] = "Item Enchantment",
            ["life"] = "Life Magic",
            ["void"] = "Void Magic",
        };

        // Single-word and two-word phrases that resolve to a school key. Two-word forms let "war magic",
        // "creature enchantment", etc. parse the same as their short forms.
        private static readonly Dictionary<string, string> SchoolAliases = new Dictionary<string, string>
        {
            ["war"] = "war",
            ["war magic"] = "war",
            ["creature"] = "creature",
            ["creature enchantment"] = "creature",
            ["item"] = "item",
            ["item enchantment"] = "item",
            ["life"] = "life",
            ["life magic"] = "life",
            ["void"] = "void",
            ["void magic"] = "void",
        };

        private static readonly Dictionary<string, int> LevelWords = new Dictionary<string, int>
        {
            ["one"] = 1,
            ["two"] = 2,
            ["three"] = 3,
            ["four"] = 4,
            ["five"] = 5,
            ["six"] = 6,
            ["seven"] = 7,
        };

        private class Offer
        {
            public int SkillId;
            public uint SkillMin;
            public long Cost;
            public List<uint> SpellIds;
        }

        // (school, level) -> resolved offer, or null if that combination is currently unavailable (missing
        // professor/emote/note data). The world weenie cache is read-mostly, so this is safe to cache
        // indefinitely; a null entry also means the missing-data warning below only logs once per combination.
        private static readonly ConcurrentDictionary<(string school, int level), Offer> OfferCache = new ConcurrentDictionary<(string, int), Offer>();

        /// <summary>
        /// If <paramref name="npc"/> is flagged as the Marketplace spell tutor, handles the tell and returns
        /// true; otherwise returns false so the caller falls through to normal emote-based OnTalkDirect handling.
        /// </summary>
        public static bool TryHandleTalkDirect(Creature npc, Player player, string message)
        {
            if (npc == null || player == null)
                return false;

            if (!(npc.GetProperty(PropertyBool.SpellTutor) ?? false))
                return false;

            if (!TryParseRequest(message, out var school, out var level))
            {
                SendUsage(npc, player);
                return true;
            }

            HandleRequest(npc, player, school, level);
            return true;
        }

        /// <summary>
        /// Parses a tell into a school key ("war"/"creature"/"item"/"life"/"void") and a level (1-7). Accepts
        /// "&lt;school&gt; &lt;level&gt;", "&lt;school&gt; level &lt;level&gt;" and "level &lt;level&gt;
        /// &lt;school&gt;", where school is either the short form or its two-word retail name, and level is a
        /// digit 1-7 or the word one..seven. Case-insensitive. Pure - unit tested in SpellTutorParseTests.
        /// </summary>
        public static bool TryParseRequest(string message, out string school, out int level)
        {
            school = null;
            level = 0;

            if (string.IsNullOrWhiteSpace(message))
                return false;

            var tokens = message.Trim().ToLowerInvariant().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 2)
            {
                if (TryParseSchool(tokens[0], out school) && TryParseLevel(tokens[1], out level))
                    return true;

                school = null;
                level = 0;
                return false;
            }

            if (tokens.Length == 3)
            {
                // "<school> level <level>"
                if (tokens[1] == "level" && TryParseSchool(tokens[0], out school) && TryParseLevel(tokens[2], out level))
                    return true;

                // "level <level> <school>"
                if (tokens[0] == "level" && TryParseLevel(tokens[1], out level) && TryParseSchool(tokens[2], out school))
                    return true;

                // "<school word 1> <school word 2> <level>", e.g. "life magic 1"
                if (TryParseSchool(tokens[0] + " " + tokens[1], out school) && TryParseLevel(tokens[2], out level))
                    return true;

                school = null;
                level = 0;
                return false;
            }

            return false;
        }

        private static bool TryParseSchool(string token, out string school) => SchoolAliases.TryGetValue(token, out school);

        private static bool TryParseLevel(string token, out int level)
        {
            if (LevelWords.TryGetValue(token, out level))
                return true;

            if (int.TryParse(token, out level) && level >= 1 && level <= 7)
                return true;

            level = 0;
            return false;
        }

        /// <summary>
        /// Pure cost helper: note value (PropertyInt.Value on the trade note weenie) times the InqOwnsItems
        /// StackSize the professor's "lvlNskillcheck" emote requires. Widened to long here so the multiply
        /// cannot overflow int at the highest-priced levels (e.g. creature level 7 = 40 x 250,000 = 10,000,000).
        /// </summary>
        public static long ComputeCost(int noteValue, int stackSize) => (long)noteValue * stackSize;

        private static void SendUsage(Creature npc, Player player)
        {
            SendNpc(npc, player,
                $"To learn, send me a TELL with the school and the level, exactly like this: /tell {npc.Name}, item 3\n" +
                "Schools: war, creature, item, life, void. Levels: 1 to 7. So: 'war 1', 'creature 7', 'life 5', 'void 7'.\n" +
                "I will name the price; answer Yes and I take it in pyreals from your pack first, then your bank.");
        }

        private static void HandleRequest(Creature npc, Player player, string school, int level)
        {
            if (!TryGetOffer(school, level, out var offer))
            {
                SendNpc(npc, player, $"Level {level} {SchoolDisplayName[school]} is not available right now.");
                return;
            }

            if (!CheckSkillGate(npc, player, school, level, offer, out _))
                return;

            var newSpells = offer.SpellIds.Where(id => !player.SpellIsKnown(id)).ToList();
            if (newSpells.Count == 0)
            {
                SendNpc(npc, player, $"You already know every level {level} {SchoolDisplayName[school]} spell I teach.");
                return;
            }

            if (!CheckFunds(npc, player, school, level, offer))
                return;

            var known = offer.SpellIds.Count - newSpells.Count;
            var prompt =
                $"Learn all {offer.SpellIds.Count} level {level} {SchoolDisplayName[school]} spells from {npc.Name} for {offer.Cost:N0} pyreals?\n\n" +
                $"You know {known} of them already; {newSpells.Count} will be new. Payment is taken from your pack first, then your bank.";

            var confirmation = new Confirmation_SpellTutorChoice(player.Guid, accepted =>
            {
                if (!accepted)
                {
                    SendNpc(npc, player, "Perhaps another time.");
                    return;
                }

                // Re-validate here - skill, known spells and funds may have changed while the confirmation
                // (up to 30 s) was outstanding.
                if (!CheckSkillGate(npc, player, school, level, offer, out _))
                    return;

                var stillNewSpells = offer.SpellIds.Where(id => !player.SpellIsKnown(id)).ToList();
                if (stillNewSpells.Count == 0)
                {
                    SendNpc(npc, player, $"You already know every level {level} {SchoolDisplayName[school]} spell I teach.");
                    return;
                }

                if (!CheckFunds(npc, player, school, level, offer))
                    return;

                if (!player.TrySpendPyrealsIncludingBank(offer.Cost))
                {
                    SendFundsShortfall(npc, player, school, level, offer);
                    return;
                }

                var taught = 0;
                foreach (var spellId in offer.SpellIds)
                {
                    // AddKnownSpell returns whether it actually added the spell (false if already known), so
                    // taught is the real count, not a stale pre-check - the same effect LearnSpellWithNetworking
                    // (uiOutput: false) has after its own AddKnownSpell call (Player_Spells.cs:59-70), without
                    // its now-redundant ContainsKey/uiOutput branches.
                    if (player.AddKnownSpell(spellId))
                    {
                        taught++;
                        player.Session.Network.EnqueueSend(new GameEventMagicUpdateSpell(player.Session, (ushort)spellId));
                    }
                }

                SendNpc(npc, player, $"I have taught you {taught:N0} new level {level} {SchoolDisplayName[school]} spells for {offer.Cost:N0} pyreals.");
                player.ApplyVisualEffects(PlayScript.SkillUpPurple);
            });

            if (!player.ConfirmationManager.EnqueueSend(confirmation, prompt))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        private static bool CheckSkillGate(Creature npc, Player player, string school, int level, Offer offer, out uint current)
        {
            var creatureSkill = player.GetCreatureSkill((Skill)offer.SkillId);
            current = creatureSkill?.Current ?? 0;

            if (creatureSkill == null || current < offer.SkillMin)
            {
                SendNpc(npc, player, $"You are not skilled enough in {SchoolDisplayName[school]} for the level {level} spells (you need {offer.SkillMin:N0}, you have {current:N0}).");
                return false;
            }

            return true;
        }

        private static bool CheckFunds(Creature npc, Player player, string school, int level, Offer offer)
        {
            var spendable = (player.CoinValue ?? 0) + player.BankedPyreals;
            if (spendable < offer.Cost)
            {
                SendFundsShortfall(npc, player, school, level, offer);
                return false;
            }

            return true;
        }

        private static void SendFundsShortfall(Creature npc, Player player, string school, int level, Offer offer)
        {
            var spendable = (player.CoinValue ?? 0) + player.BankedPyreals;
            SendNpc(npc, player, $"Level {level} {SchoolDisplayName[school]} costs {offer.Cost:N0} pyreals; you have {spendable:N0} ({player.CoinValue ?? 0:N0} in your pack, {player.BankedPyreals:N0} in your bank).");
        }

        private static bool TryGetOffer(string school, int level, out Offer offer)
        {
            var key = (school, level);
            if (OfferCache.TryGetValue(key, out offer))
                return offer != null;

            offer = BuildOffer(school, level);
            OfferCache[key] = offer;
            return offer != null;
        }

        private static Offer BuildOffer(string school, int level)
        {
            if (!SchoolProfessorWcid.TryGetValue(school, out var professorWcid))
                return null;

            var professor = DatabaseManager.World.GetCachedWeenie(professorWcid);
            if (professor?.PropertiesEmote == null)
            {
                log.Warn($"SpellTutor: professor weenie {professorWcid} for school '{school}' is missing or has no emotes.");
                return null;
            }

            var yesNoQuest = $"lvl{level}yesno";
            var skillCheckQuest = $"lvl{level}skillcheck";
            var gotoSetQuest = $"lvl{level}";

            var yesNoEmote = professor.PropertiesEmote.FirstOrDefault(e => e.Category == EmoteCategory.TestSuccess && e.Quest == yesNoQuest);
            var inqSkillAction = yesNoEmote?.PropertiesEmoteAction.FirstOrDefault(a => a.Type == (uint)EmoteType.InqSkillStat);

            if (inqSkillAction?.Stat == null || inqSkillAction.Min == null)
            {
                log.Warn($"SpellTutor: professor {professorWcid} school '{school}' level {level}: missing/incomplete '{yesNoQuest}' InqSkillStat emote.");
                return null;
            }

            var skillCheckEmote = professor.PropertiesEmote.FirstOrDefault(e => e.Category == EmoteCategory.TestSuccess && e.Quest == skillCheckQuest);
            var inqOwnsAction = skillCheckEmote?.PropertiesEmoteAction.FirstOrDefault(a => a.Type == (uint)EmoteType.InqOwnsItems);

            if (inqOwnsAction?.WeenieClassId == null || inqOwnsAction.StackSize == null)
            {
                log.Warn($"SpellTutor: professor {professorWcid} school '{school}' level {level}: missing/incomplete '{skillCheckQuest}' InqOwnsItems emote.");
                return null;
            }

            var noteWeenie = DatabaseManager.World.GetCachedWeenie(inqOwnsAction.WeenieClassId.Value);
            var noteValue = noteWeenie?.GetProperty(PropertyInt.Value);

            if (noteValue == null)
            {
                log.Warn($"SpellTutor: professor {professorWcid} school '{school}' level {level}: trade note wcid {inqOwnsAction.WeenieClassId} is missing or has no Value.");
                return null;
            }

            // A non-positive note value or stack size would make ComputeCost <= 0, and TrySpendPyrealsIncludingBank
            // treats amount <= 0 as a no-op success - a bad world row must never turn into a free teach.
            if (noteValue.Value <= 0 || inqOwnsAction.StackSize.Value <= 0)
            {
                log.Warn($"SpellTutor: professor {professorWcid} school '{school}' level {level}: trade note wcid {inqOwnsAction.WeenieClassId} has non-positive Value ({noteValue.Value}) or StackSize ({inqOwnsAction.StackSize.Value}).");
                return null;
            }

            var gotoSetEmote = professor.PropertiesEmote.FirstOrDefault(e => e.Category == EmoteCategory.GotoSet && e.Quest == gotoSetQuest);
            var spellIds = gotoSetEmote?.PropertiesEmoteAction
                .Where(a => a.Type == (uint)EmoteType.TeachSpell && a.SpellId != null)
                .Select(a => (uint)a.SpellId.Value)
                .Distinct()
                .ToList();

            if (spellIds == null || spellIds.Count == 0)
            {
                log.Warn($"SpellTutor: professor {professorWcid} school '{school}' level {level}: '{gotoSetQuest}' GotoSet has no TeachSpell actions.");
                return null;
            }

            // The client's own spell table is the authority on which spell ids actually exist - a TeachSpell
            // action naming an id the client dat doesn't have would otherwise reach LearnSpellWithNetworking's
            // ContainsKey guard at teach time and silently no-op there, undercounting M for no visible reason.
            var spellTable = DatManager.PortalDat.SpellTable;
            var validSpellIds = spellIds.Where(id => spellTable.Spells.ContainsKey(id)).ToList();
            var droppedSpellIds = spellIds.Except(validSpellIds).ToList();

            if (droppedSpellIds.Count > 0)
                log.Warn($"SpellTutor: professor {professorWcid} school '{school}' level {level}: dropped {droppedSpellIds.Count} spell id(s) not in the client spell table: {string.Join(", ", droppedSpellIds)}.");

            if (validSpellIds.Count == 0)
            {
                log.Warn($"SpellTutor: professor {professorWcid} school '{school}' level {level}: no valid spell ids remain after client spell table validation.");
                return null;
            }

            return new Offer
            {
                SkillId = inqSkillAction.Stat.Value,
                SkillMin = (uint)Math.Max(0, inqSkillAction.Min.Value),
                Cost = ComputeCost(noteValue.Value, inqOwnsAction.StackSize.Value),
                SpellIds = validSpellIds,
            };
        }

        private static void Send(Player player, string message) =>
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));

        private static void SendNpc(Creature npc, Player player, string message) =>
            Send(player, $"{npc.Name}: {message}");

        /// <summary>
        /// A Yes/No confirmation that hands the response back to a callback, copied verbatim (under a local
        /// name) from ClassAbilities.ClassAbilityTrainer.Confirmation_ClassAbilityChoice. A timeout ends the
        /// interaction quietly.
        /// </summary>
        private class Confirmation_SpellTutorChoice : Confirmation
        {
            private readonly Action<bool> onResponse;

            public Confirmation_SpellTutorChoice(ObjectGuid playerGuid, Action<bool> onResponse)
                : base(playerGuid, ConfirmationType.Yes_No)
            {
                this.onResponse = onResponse;
            }

            public override void ProcessConfirmation(bool response, bool timeout = false)
            {
                if (Player == null || timeout)
                    return;

                onResponse(response);
            }
        }
    }
}
