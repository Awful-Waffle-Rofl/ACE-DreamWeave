using System.Globalization;
using System.Linq;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    public static class ClassAbilityCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [CommandHandler("abilities", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Manage your class abilities",
            "list | all | enhanced [skills|attributes|vitals] | info <skill> | token [list|buy <skill> <tier>] | unlearn <skill> | points")]
        public static void HandleSkills(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Reply(session, "Class abilities are not currently enabled on this server.");
                return;
            }

            var subcommand = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "list";
            var arg = parameters.Length > 1 ? parameters[1] : null;

            switch (subcommand)
            {
                case "list":
                    HandleList(session);
                    break;
                case "all":
                    HandleAll(session);
                    break;
                case "enhanced":
                    HandleEnhanced(session, arg);
                    break;
                case "info":
                    HandleInfo(session, arg);
                    break;
                case "learn":
                    HandleLearn(session, arg);
                    break;
                case "unlearn":
                    HandleUnlearn(session, false, arg);
                    break;
                case "points":
                    HandlePoints(session);
                    break;
                case "buy":
                    HandleBuy(session, arg);
                    break;
                case "token":
                    HandleToken(session, parameters);
                    break;
                default:
                    Reply(session, "Usage: /abilities list | all | enhanced [skills|attributes|vitals] | info <skill> | token [list|buy <skill> <tier>] | unlearn <skill> | points");
                    break;
            }
        }

        private static void Reply(Session session, string message)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }

        private static string PointsSummary(Session session)
        {
            var player = session.Player;
            return $"Points: {player.AvailableClassAbilityPoints:N0} available, {player.TotalClassAbilityPointsEarned:N0} lifetime earned.";
        }

        private static void HandleList(Session session)
        {
            var owned = ClassAbilityRegistry.Abilities.Values
                .Select(skill => (skill, rank: session.Player.GetClassAbilityRank(skill.Id)))
                .Where(entry => entry.rank > 0)
                .OrderBy(entry => entry.skill.DisplayName)
                .ToList();

            if (owned.Count == 0)
                Reply(session, "You have not learned any class abilities. Use /abilities all to see what is available.");
            else
            {
                Reply(session, "Your class abilities:  (skill/affinity/gear)");
                foreach (var (skill, rank) in owned)
                {
                    var readout = ClassAbilityRegistry.GetHandler(skill.Id) is IAbilityReadout readoutSource
                        ? readoutSource.GetReadout(session.Player, rank)
                        : default;

                    Reply(session, FormatReadoutLine(skill, rank, readout));
                }
            }

            Reply(session, PointsSummary(session));
        }

        /// <summary>
        /// Builds one /abilities list line. The AC chat font is proportional, so this never pads for
        /// column alignment - only ClassAbilityReadout's own fields decide the content. HasValue false
        /// (no ClassAbilityReadout implementation, or one that legitimately has nothing to report) prints
        /// rank only, with no numbers at all.
        /// </summary>
        private static string FormatReadoutLine(ClassAbilityDefinition definition, int rank, ClassAbilityReadout readout)
        {
            var header = $"  {definition.DisplayName} {rank}/{definition.MaxRank}";

            if (!readout.HasValue)
                return header;

            var triple = $"({Num(readout.Skill)}/{Num(readout.Affinity)}/{Num(readout.Gear)})";
            var line = $"{header}  {Num(readout.Effective)}{readout.Unit}{readout.Per} {readout.Label}  {triple}";

            if (readout.Capped)
                line += $" capped: {readout.CapNote}";

            return line;
        }

        /// <summary>
        /// Formats a display number to at most 1 decimal place, suppressing a trailing ".0"
        /// (18.0 -&gt; "18", 36.65 -&gt; "36.7" or "36.6" depending on floating rounding, 2.7 -&gt; "2.7").
        /// </summary>
        private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        private static void HandleAll(Session session)
        {
            Reply(session, "Class abilities:");

            // Combat perks are listed in full; the large Enhanced <stat> families are collapsed to a
            // one-line summary each (browse them with /abilities enhanced) so this stays readable.
            foreach (var skill in ClassAbilityRegistry.Abilities.Values
                         .Where(s => s.Category == "Combat")
                         .OrderBy(s => s.DisplayName))
            {
                Reply(session, $"  {skill.DisplayName} ({skill.Name}) - {SkillStatus(session, skill)}");
            }

            foreach (var category in new[] { "Enhanced Skill", "Enhanced Attribute", "Enhanced Vital" })
            {
                var group = ClassAbilityRegistry.Abilities.Values.Where(s => s.Category == category).ToList();
                if (group.Count == 0)
                    continue;

                var owned = group.Count(s => session.Player.GetClassAbilityRank(s.Id) > 0);
                Reply(session, $"  {category}s: {group.Count} available, {owned} learned - see /abilities enhanced {EnhancedGroupToken(category)}");
            }

            Reply(session, PointsSummary(session));
        }

        /// <summary>
        /// Lists the Enhanced &lt;stat&gt; family, optionally filtered to skills / attributes / vitals.
        /// </summary>
        private static void HandleEnhanced(Session session, string filter)
        {
            string category = null;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                switch (filter.ToLowerInvariant())
                {
                    case "skill":
                    case "skills":
                        category = "Enhanced Skill"; break;
                    case "attribute":
                    case "attributes":
                    case "attrib":
                    case "attribs":
                        category = "Enhanced Attribute"; break;
                    case "vital":
                    case "vitals":
                        category = "Enhanced Vital"; break;
                    default:
                        Reply(session, "Usage: /abilities enhanced [skills|attributes|vitals]");
                        return;
                }
            }

            var categories = category != null
                ? new[] { category }
                : new[] { "Enhanced Skill", "Enhanced Attribute", "Enhanced Vital" };

            foreach (var cat in categories)
            {
                var group = ClassAbilityRegistry.Abilities.Values
                    .Where(s => s.Category == cat)
                    .OrderBy(s => s.DisplayName)
                    .ToList();

                if (group.Count == 0)
                    continue;

                Reply(session, $"-- {cat} ({group.Count(s => session.Player.GetClassAbilityRank(s.Id) > 0)}/{group.Count} learned) --");
                foreach (var skill in group)
                    Reply(session, $"  {skill.DisplayName} ({skill.Name}) - {SkillStatus(session, skill)}");
            }

            Reply(session, "Acquire an ability by using its training token from a class trainer; each token teaches one rank and spends class ability points.");
            Reply(session, PointsSummary(session));
        }

        private static string SkillStatus(Session session, ClassAbilityDefinition skill)
        {
            var rank = session.Player.GetClassAbilityRank(skill.Id);

            if (!skill.Implemented)
                return "[Coming soon]";
            if (rank >= skill.MaxRank)
                return $"rank {rank}/{skill.MaxRank} (max)";
            return $"rank {rank}/{skill.MaxRank}, next rank costs {skill.CostPerRank[rank]:N0} point{(skill.CostPerRank[rank] == 1 ? "" : "s")}";
        }

        private static string EnhancedGroupToken(string category)
        {
            switch (category)
            {
                case "Enhanced Skill": return "skills";
                case "Enhanced Attribute": return "attributes";
                case "Enhanced Vital": return "vitals";
                default: return "";
            }
        }

        private static void HandleInfo(Session session, string name)
        {
            if (!TryResolveSkill(session, name, out var skill))
                return;

            var rank = session.Player.GetClassAbilityRank(skill.Id);

            Reply(session, $"{skill.DisplayName} ({skill.Name}){(skill.Implemented ? "" : " [Coming soon]")}");
            Reply(session, $"  {skill.Description}");
            Reply(session, $"  Max rank: {skill.MaxRank}. Cost per rank: {string.Join(", ", skill.CostPerRank)}. Your rank: {rank}.");
        }

        private static void HandleLearn(Session session, string name)
        {
            // Direct learning is now admin/developer tooling: players acquire class abilities by using training
            // tokens (bought from a class trainer). The command still exists for testing/support.
            if (session.AccessLevel < AccessLevel.Developer)
            {
                Reply(session, "Class abilities are learned by using a training token, not this command. Visit a class trainer to buy one, then use it from your inventory.");
                return;
            }

            if (!TryResolveSkill(session, name, out var skill))
                return;

            if (!session.Player.LearnClassAbility(skill, out var error))
            {
                Reply(session, error);
                return;
            }

            var rank = session.Player.GetClassAbilityRank(skill.Id);
            Reply(session, $"You have learned {skill.DisplayName} rank {rank}/{skill.MaxRank}. {PointsSummary(session)}");
        }

        private static void HandleUnlearn(Session session, bool confirmed, string name)
        {
            if (!TryResolveSkill(session, name, out var skill))
                return;

            var player = session.Player;
            var rank = player.GetClassAbilityRank(skill.Id);

            if (rank <= 0)
            {
                Reply(session, $"You have not learned {skill.DisplayName}.");
                return;
            }

            // Developer testing bypass: unlearn immediately, no Luminance fee and no confirmation,
            // so respec doesn't get in the way of iterating on a skill (mirrors /grantabilitypoints).
            if (session.AccessLevel >= AccessLevel.Developer)
            {
                if (!player.UnlearnClassAbility(skill, out var devError, out var devRefunded, ignoreFee: true))
                {
                    Reply(session, devError);
                    return;
                }

                Reply(session, $"[testing] Unlearned {skill.DisplayName}, refunded {devRefunded:N0} class ability point{(devRefunded == 1 ? "" : "s")} (no Luminance fee). {PointsSummary(session)}");
                return;
            }

            var fee = PropertyManager.GetLong("class_ability_respec_lum_cost").Item;

            if (!confirmed)
            {
                if ((player.AvailableLuminance ?? 0) + player.BankedLuminance < fee)
                {
                    Reply(session, $"Unlearning a class ability costs {fee:N0} Luminance; you have {(player.AvailableLuminance ?? 0) + player.BankedLuminance:N0}.");
                    return;
                }

                var msg = $"Unlearn {skill.DisplayName} rank {rank}?\n\nThis refunds {skill.CumulativeCost(rank):N0} class ability points and costs {fee:N0} Luminance.";

                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(player.Guid, () => HandleUnlearn(session, true, name)), msg))
                    player.SendWeenieError(WeenieError.ConfirmationInProgress);

                return;
            }

            // everything is re-validated here - state may have changed while the confirmation was up
            if (!player.UnlearnClassAbility(skill, out var error, out var refunded))
            {
                Reply(session, error);
                return;
            }

            Reply(session, $"You have unlearned {skill.DisplayName} and recovered {refunded:N0} class ability point{(refunded == 1 ? "" : "s")}. {PointsSummary(session)}");
        }

        private static void HandlePoints(Session session)
        {
            var player = session.Player;
            var purchased = player.ClassAbilityPointsPurchasedWithLum;
            var nextCost = WorldObjects.Player.LumCostForClassAbilityPoints(purchased, 1);

            Reply(session, PointsSummary(session));
            Reply(session, $"Luminance purchases: {purchased:N0} bought so far; the next point costs {nextCost:N0} Luminance. Exchange it at the reckoning-stone by the Skillmaster in the Drift Network. Prices rise with each point and there is no cap.");
        }

        private static void HandleBuy(Session session, string arg)
        {
            // Buying points with this command is now admin/developer tooling: players exchange Luminance for
            // class ability points at the Arcane Pedestal (the Skillmaster's reckoning-stone) in the Drift Network.
            if (session.AccessLevel < AccessLevel.Developer)
            {
                Reply(session, "Class ability points are exchanged for Luminance at the reckoning-stone beside the Skillmaster in the Drift Network, not this command.");
                return;
            }

            var count = 1;

            if (arg != null && (!int.TryParse(arg, out count) || count < 1))
            {
                Reply(session, "Usage: /abilities buy [count] - count must be a positive number.");
                return;
            }

            if (!session.Player.TryBuyClassAbilityPoints(count, out var error))
                Reply(session, error);
            // success message comes from GrantClassAbilityPoints
        }

        private static void HandleToken(Session session, string[] parameters)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Reply(session, "Class abilities are not currently enabled on this server.");
                return;
            }

            var sub = parameters.Length > 1 ? parameters[1].ToLowerInvariant() : "list";
            switch (sub)
            {
                case "list":
                    HandleTokenList(session);
                    break;
                case "buy":
                    // Developer tooling only. Every token is prepaid - it applies its rank for free on use - so a
                    // Luminance-only purchase here would hand out a rank without touching the class ability point
                    // economy at all. Players buy tokens from a Drift Network trainer, which charges CAP.
                    if (session.AccessLevel < AccessLevel.Developer)
                    {
                        Reply(session, "Training tokens are bought from a Drift Network trainer, not this command. Exchange Luminance for class ability points at the reckoning-stone beside the Skillmaster.");
                        return;
                    }
                    HandleTokenBuy(session, parameters);
                    break;
                default:
                    Reply(session, "Usage: /abilities token list | buy <skill> <tier>");
                    break;
            }
        }

        private static void HandleTokenList(Session session)
        {
            var dev = session.AccessLevel >= AccessLevel.Developer;

            Reply(session, "Class ability tokens (bought from a Drift Network trainer; the point cost is paid at purchase, and using the token from your pack is free):");

            ClassAbilityId? last = null;
            foreach (var o in ClassAbilityTokenCatalog.AllOfferings())
            {
                if (last != o.SkillId)
                {
                    Reply(session, $"  {o.Definition.DisplayName} ({o.Definition.Name}) - your rank {session.Player.GetClassAbilityRank(o.SkillId)}/{o.Definition.MaxRank}");
                    last = o.SkillId;
                }

                var pts = o.Definition.CostPerRank[o.Tier - 1];
                var line = $"      tier {o.Tier}: {pts:N0} class ability point{(pts == 1 ? "" : "s")}";

                // The Luminance figure only prices the developer-only /abilities token buy shortcut.
                if (dev)
                    line += $" (dev buy: {ClassAbilityTokenCatalog.LumCost(o.Definition, o.Tier):N0} Luminance)";

                Reply(session, line);
            }

            if (dev)
                Reply(session, "[dev] Buy with /abilities token buy <skill> <tier>, e.g. /abilities token buy multishot 1.");

            Reply(session, PointsSummary(session));
        }

        private static void HandleTokenBuy(Session session, string[] parameters)
        {
            if (parameters.Length < 4)
            {
                Reply(session, "Usage: /abilities token buy <skill> <tier>, e.g. /abilities token buy multishot 1. See /abilities token list.");
                return;
            }

            if (!ClassAbilityRegistry.TryGetByName(parameters[2], out var skill))
            {
                Reply(session, $"Unknown class ability '{parameters[2]}'. Use /abilities token list to see buyable skills.");
                return;
            }

            if (!int.TryParse(parameters[3], out var tier) || tier < 1)
            {
                Reply(session, "Tier must be a positive number, e.g. /abilities token buy multishot 1.");
                return;
            }

            if (!ClassAbilityTokenCatalog.TryResolve(skill.Id, tier, out var offering))
            {
                Reply(session, $"There is no tier {tier} token for {skill.DisplayName} (it has {skill.MaxRank} rank{(skill.MaxRank == 1 ? "" : "s")}). Use /abilities token list.");
                return;
            }

            if (!session.Player.TryBuyClassAbilityToken(offering, out var error))
                Reply(session, error);
            // success message comes from TryBuyClassAbilityToken
        }

        /// <summary>
        /// Admin/developer tooling: grant class ability points to yourself (or another online player)
        /// without farming point items. Bypasses the lifetime earned cap so testing isn't blocked
        /// by the economy - the cap still applies to all player-facing sources.
        /// </summary>
        [CommandHandler("grantabilitypoints", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Grants class ability points to yourself or another online player, ignoring the lifetime cap",
            "<amount> [playerName]")]
        public static void HandleGrantSkillPoints(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Reply(session, "Class abilities are not currently enabled on this server. Enable with: /modifybool class_abilities_enabled true");
                return;
            }

            if (!int.TryParse(parameters[0], out var amount) || amount < 1)
            {
                Reply(session, "Usage: /grantabilitypoints <amount> [playerName] - amount must be a positive number.");
                return;
            }

            var target = session.Player;

            if (parameters.Length > 1)
            {
                var playerName = string.Join(" ", parameters.Skip(1));
                target = PlayerManager.GetOnlinePlayer(playerName);

                if (target == null)
                {
                    Reply(session, $"Player '{playerName}' is not online.");
                    return;
                }
            }

            // messages the target and saves their biota
            target.GrantClassAbilityPoints(amount, "an admin grant");

            if (target != session.Player)
                Reply(session, $"Granted {amount:N0} class ability point{(amount == 1 ? "" : "s")} to {target.Name} ({target.AvailableClassAbilityPoints:N0} now available).");
        }

        /// <summary>
        /// Developer diagnostic: dumps the running registry state and the player's live computed
        /// Enhanced/Battle Hardened values, to distinguish a read/compute bug from a display-only issue.
        /// </summary>
        [CommandHandler("csdiag", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 0,
            "Dumps class-ability diagnostic info for the current character")]
        public static void HandleCsDiag(Session session, params string[] parameters)
        {
            var player = session.Player;

            Reply(session, $"class_abilities_enabled = {PropertyManager.GetBool("class_abilities_enabled").Item}");
            Reply(session, $"Registry Skills.Count = {ClassAbilityRegistry.Abilities.Count}");

            foreach (var name in new[] { "enhanced_quickness", "enhanced_strength", "battlehardened", "thorns" })
            {
                if (ClassAbilityRegistry.TryGetByName(name, out var def))
                    Reply(session, $"  '{name}' -> id {(int)def.Id}, rank {player.GetClassAbilityRank(def.Id)}/{def.MaxRank}");
                else
                    Reply(session, $"  '{name}' -> NOT IN REGISTRY");
            }

            var q = player.Quickness;
            var line1 = $"Quickness: Start={q.StartingValue} Net={q.NetworkStartingValue} Ranks={q.Ranks} Base={q.Base} Current={q.Current} EnhancedBonus={player.GetEnhancedAttributeBonus(ACE.Entity.Enum.Properties.PropertyAttribute.Quickness)}";
            var line2 = $"Strength.Current={player.Strength.Current}; BattleHardened mod={player.GetBattleHardenedDamageResistMod():F4} (lower=more reduction)";
            var line3 = $"Ratings: DamageRating={player.GetDamageRating()} DamageResistRating={player.GetDamageResistRating()} CritRating={player.GetCritRating()} CritDamageRating={player.GetCritDamageRating()} CritResistRating={player.GetCritResistRating()}";
            Reply(session, line1);
            Reply(session, line2);
            Reply(session, line3);
            log.Warn($"[csdiag] {player.Name}: {line1} | {line2} | {line3}");
        }

        /// <summary>
        /// Admin/developer tooling: triggers a class ability's effect directly, without needing the
        /// skill learned or its normal trigger (e.g. an aetheria surge for Taunt).
        /// </summary>
        [CommandHandler("testskill", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Triggers a class ability's effect directly for testing, bypassing its normal trigger and the learned requirement",
            "<skill>")]
        public static void HandleTestSkill(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Reply(session, "Class abilities are not currently enabled on this server. Enable with: /modifybool class_abilities_enabled true");
                return;
            }

            if (!TryResolveSkill(session, parameters[0], out var skill))
                return;

            if (ClassAbilityRegistry.GetHandler(skill.Id) is ITestableClassAbility testable)
                Reply(session, testable.Test(session.Player));
            else
                Reply(session, $"{skill.DisplayName} has no direct test trigger - its effect is passive (an always-on stat bonus or a combat-path hook).");
        }

        private static bool TryResolveSkill(Session session, string name, out ClassAbilityDefinition skill)
        {
            skill = null;

            if (string.IsNullOrWhiteSpace(name))
            {
                Reply(session, "Please specify a class ability name, e.g. /abilities info multishot. Use /abilities all to see them.");
                return false;
            }

            if (!ClassAbilityRegistry.TryGetByName(name, out skill))
            {
                Reply(session, $"Unknown class ability '{name}'. Use /abilities all to see the available skills.");
                return false;
            }

            return true;
        }
    }
}
