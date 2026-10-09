using System.Globalization;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Database.Models.Shard;
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

        private const string Usage = "list | all | enhanced [skills|attributes|vitals] | info <skill> | learn <skill> | unlearn <skill> | points | buy <skill> | buyxp [count] | token [list|buy <skill> <tier>]";

        [CommandHandler("abilities", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Manage your class abilities",
            Usage)]
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
                case "buyxp":
                    HandleBuyXp(session, arg);
                    break;
                case "token":
                    HandleToken(session, parameters);
                    break;
                default:
                    Reply(session, $"Usage: /abilities {Usage}");
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
                Reply(session, "Your class abilities:  [skill/affinity/gear]");
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
        /// column alignment - only ClassAbilityReadout's own fields decide the content. HasValue false on
        /// the primary readout (no ClassAbilityReadout implementation, or one that legitimately has
        /// nothing to report) prints rank only, with no numbers at all - UNLESS the ability still carries
        /// one or more Secondary scalars (e.g. a jump-count ability whose only live-varying magnitude is a
        /// gear-scaled proc chance), in which case the header is followed by those secondary segments with
        /// no primary segment in front of them. Every secondary present, if any, is appended after the
        /// primary segment (or in its place) via the same per-segment formatting <see cref="FormatSegment"/>
        /// uses for both.
        /// </summary>
        private static string FormatReadoutLine(ClassAbilityDefinition definition, int rank, ClassAbilityReadout readout)
        {
            var header = $"  {definition.DisplayName} {rank}/{definition.MaxRank}";

            var hasSecondaries = readout.Secondary != null && readout.Secondary.Count > 0;

            if (!readout.HasValue && !hasSecondaries)
                return header;

            var line = header;

            if (readout.HasValue)
                line += FormatSegment(readout);

            if (hasSecondaries)
            {
                foreach (var secondary in readout.Secondary)
                    line += FormatSegment(secondary);
            }

            return line;
        }

        /// <summary>
        /// Formats one scalar segment - "  {Prefix}{Effective}{Unit}{Per} {Label}  [s/a/g]" plus its own
        /// " capped: {CapNote}" when it bit - shared by the primary readout and every entry in
        /// <see cref="ClassAbilityReadout.Secondary"/> so they render identically.
        /// </summary>
        private static string FormatSegment(ClassAbilityReadout readout)
        {
            var triple = $"[{Num(readout.Skill)}/{Num(readout.Affinity)}/{Num(readout.Gear)}]";
            var segment = $"  {readout.Prefix}{Num(readout.Effective)}{readout.Unit}{readout.Per} {readout.Label}  {triple}";
            // readout.Prefix is null unless the handler opted in (e.g. EnhancedStatAbility's "+"); the
            // interpolation above already renders a null as "", so no null-coalescing is needed here.

            if (readout.Capped)
                segment += $" capped: {readout.CapNote}";

            return segment;
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

            Reply(session, "Buy a rank from a class trainer and confirm the price; it is learned immediately and spends class ability points.");
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
            // Direct learning is now admin/developer tooling: players acquire class abilities at a
            // Drift Network class trainer (instant-learn confirmation on Buy - no training token is
            // created any more). The command still exists for testing/support.
            if (session.AccessLevel < AccessLevel.Developer)
            {
                Reply(session, "Class abilities are learned from a class trainer, not this command. Visit a trainer in the Drift Network and buy the rank you want.");
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

            // the xp lane runs its own counter and its own curve, so both prices are quoted (XP-LANE-SPEC sec 3.3)
            var xpPurchased = player.ClassAbilityPointsPurchasedWithXp;
            var nextXpCost = WorldObjects.Player.XpCostForClassAbilityPoints(xpPurchased, 1);
            var minLevel = WorldObjects.Player.ClassAbilityXpMinLevel;

            if ((player.Level ?? 1) < minLevel)
                Reply(session, $"Experience purchases: unlocked at level {minLevel:N0} (you are {player.Level ?? 1:N0}).");
            else
                Reply(session, $"Experience purchases: {xpPurchased:N0} bought so far; the next point costs {nextXpCost:N0} experience and you have {player.AvailableExperience ?? 0:N0} unassigned. Prices rise with each point and there is no cap.");
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

        /// <summary>
        /// Exchanges unassigned EXPERIENCE for class ability points. Unlike /abilities buy this is a PLAYER
        /// command - the xp lane is the replacement for the enlightenment lane, and its in-game front end is
        /// the companion exchange stone in the Drift Network (XP-LANE-SPEC sec 3.7). The level gate and the
        /// price both live in TryBuyClassAbilityPointsWithXp, so this only parses the count.
        /// </summary>
        private static void HandleBuyXp(Session session, string arg)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Reply(session, "Class abilities are not currently enabled on this server.");
                return;
            }

            var count = 1;

            if (arg != null && (!int.TryParse(arg, out count) || count < 1))
            {
                Reply(session, "Usage: /abilities buyxp [count] - count must be a positive number.");
                return;
            }

            if (!session.Player.TryBuyClassAbilityPointsWithXp(count, out var error))
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
        /// without farming point items. There is NO lifetime cap to bypass - the no-caps redesign
        /// (DESIGN.md sec 1) limits power by the Luminance price curve rather than a wall, and
        /// <see cref="Player.GrantClassAbilityPoints"/>, which this calls, succeeds for any positive
        /// amount from any source. This command exists purely to skip the earning, not a cap.
        /// </summary>
        [CommandHandler("grantabilitypoints", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Grants class ability points to yourself or another online player, skipping the normal earning",
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
            target.GrantClassAbilityPoints(amount, "an admin grant", CapLedgerReason.GrantAdmin);

            if (target != session.Player)
                Reply(session, $"Granted {amount:N0} class ability point{(amount == 1 ? "" : "s")} to {target.Name} ({target.AvailableClassAbilityPoints:N0} now available).");
        }

        /// <summary>
        /// CAP audit ledger, round 3: staff-facing observability over `character_cap_audit` and
        /// `character_cap_ledger`. With no argument, lists every character currently flagged out of
        /// balance (unexplained &lt;&gt; 0 or rank_Divergences &lt;&gt; 0). With a name, shows that
        /// character's audit row (found by id via GetCapAudit, whether balanced or not, or a distinct
        /// "never audited" message if none has ever been recorded) plus their last 20 ledger rows.
        ///
        /// A NULL read result means the shard ledger read FAILED - this is reported distinctly from
        /// "no rows", per the read-failure contract on ShardDatabase_CapLedger.cs. Never confuse the
        /// two: reporting "no rows" on a failed read would tell an admin a character's ledger is clean
        /// when nothing was actually examined.
        /// </summary>
        [CommandHandler("caaudit", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "Shows CAP audit ledger status: with no name, every character flagged out of balance; with a name, that character's audit row plus their last 20 ledger rows",
            "[playerName]")]
        public static void HandleCapAudit(Session session, params string[] parameters)
        {
            if (parameters.Length == 0)
            {
                DatabaseManager.Shard.GetCapAuditFailures(ShardDatabase.MaxCapLedgerRows, rows =>
                {
                    if (rows == null)
                    {
                        Reply(session, "The shard ledger is unavailable - the CAP audit read failed.");
                        return;
                    }

                    if (rows.Count == 0)
                    {
                        Reply(session, "No character is currently flagged out of balance.");
                        return;
                    }

                    Reply(session, $"{rows.Count} character(s) flagged out of balance:");
                    foreach (var row in rows)
                        Reply(session, FormatAuditRow(row));
                });
                return;
            }

            var playerName = string.Join(" ", parameters);
            var target = PlayerManager.FindByName(playerName);

            if (target == null)
            {
                Reply(session, $"No character named '{playerName}' was found.");
                return;
            }

            var characterId = target.Guid.Full;
            var characterName = target.Name;

            DatabaseManager.Shard.GetCapAudit(characterId, (row, found) =>
            {
                if (!found)
                {
                    Reply(session, "The shard ledger is unavailable - the CAP audit read failed.");
                }
                else if (row == null)
                {
                    Reply(session, $"{characterName} (0x{characterId:X8}) has no recorded CAP audit row yet.");
                }
                else
                {
                    Reply(session, FormatAuditRow(row));
                }
            });

            DatabaseManager.Shard.GetCapLedger(characterId, 20, ledgerRows =>
            {
                if (ledgerRows == null)
                {
                    Reply(session, "The shard ledger is unavailable - the CAP ledger read failed.");
                    return;
                }

                if (ledgerRows.Count == 0)
                {
                    Reply(session, $"{characterName} (0x{characterId:X8}) has no recorded CAP ledger history.");
                    return;
                }

                Reply(session, $"{characterName} (0x{characterId:X8}) - last {ledgerRows.Count} ledger row(s), newest first:");
                foreach (var ledgerRow in ledgerRows)
                    Reply(session, FormatLedgerRow(ledgerRow));
            });
        }

        private static string FormatAuditRow(CharacterCapAudit row)
        {
            var firstDetected = row.FirstDetectedAt.HasValue ? row.FirstDetectedAt.Value.ToString("u") : "n/a";
            return $"  {row.CharacterName} (0x{row.CharacterId:X8}): totalEarned {row.TotalEarned}, available {row.Available}, " +
                $"ownedCost {row.OwnedCost}, sinkSpend {row.SinkSpend}, unexplained {row.Unexplained}, orphanRows {row.OrphanRows}, " +
                $"rankDivergences {row.RankDivergences}, firstDetected {firstDetected}, lastChecked {row.LastCheckedAt:u}.";
        }

        private static string FormatLedgerRow(CharacterCapLedger row)
        {
            var line = $"  #{row.Id} {row.Ts:u} {row.Reason} deltaAvailable {row.DeltaAvailable} deltaTotal {row.DeltaTotal} " +
                $"availableAfter {row.AvailableAfter} totalAfter {row.TotalAfter} ownedCostAfter {row.OwnedCostAfter}";

            if (row.Ability != null)
                line += $" ability {row.Ability} rankAfter {(row.RankAfter.HasValue ? row.RankAfter.Value.ToString() : "n/a")}";

            if (row.BatchId != null)
                line += $" batch {row.BatchId}";

            if (row.Detail != null)
                line += $" - {row.Detail}";

            return line;
        }

        /// <summary>
        /// CAP audit ledger, round 3: the manual remediation route for a CAP-shortfall incident, and
        /// the durable tool for the next one. Applies a signed correction to an ONLINE player's
        /// AvailableClassAbilityPoints through <see cref="Player.TryAdminAdjustClassAbilityPoints"/> -
        /// the ONLY public route onto the CAP mutator - so the correction is ledgered (reason
        /// admin_correct) exactly like every other CAP write site.
        ///
        /// NEVER touches TotalClassAbilityPointsEarned: <see cref="Player.GrantClassAbilityPoints"/>
        /// raises both counters, and Total is what MeetsClassAbilityTierUnlock reads
        /// (Player_ClassAbilities.cs's `TotalClassAbilityPointsEarned &lt; cspRequired` check) to gate
        /// Tier 2/3 class-ability access, so crediting a correction through it would silently hand out
        /// tier access along with the point. This command's mutator only ever changes Available.
        /// </summary>
        [CommandHandler("capadjust", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 3,
            "Applies a signed correction to an online player's available class ability points, ledgered as an admin correction. Never grants lifetime-earned points or tier access.",
            "<playerName> <signed amount> <reason text>")]
        public static void HandleCapAdjust(Session session, params string[] parameters)
        {
            if (parameters.Length < 3)
            {
                Reply(session, "Usage: /capadjust <playerName> <signed amount> <reason text>");
                return;
            }

            var playerName = parameters[0];
            var target = PlayerManager.GetOnlinePlayer(playerName);

            if (target == null)
            {
                Reply(session, $"Player '{playerName}' is not online.");
                return;
            }

            if (!int.TryParse(parameters[1], NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite, CultureInfo.InvariantCulture, out var amount) || amount == 0)
            {
                Reply(session, "Usage: /capadjust <playerName> <signed amount> <reason text> - amount must be a nonzero signed number, e.g. +1 or -2.");
                return;
            }

            var reasonText = string.Join(" ", parameters.Skip(2));

            if (string.IsNullOrWhiteSpace(reasonText))
            {
                Reply(session, "Usage: /capadjust <playerName> <signed amount> <reason text> - a reason is required.");
                return;
            }

            var detail = $"{session.Player.Name}: {reasonText}";

            if (!target.TryAdminAdjustClassAbilityPoints(amount, detail, out var availableAfter))
            {
                Reply(session, $"Refused: {target.Name} has {target.AvailableClassAbilityPoints:N0} available; that adjustment would take it below zero.");
                return;
            }

            target.SaveBiotaToDatabase();

            var sign = amount > 0 ? "+" : "";
            Reply(session, $"Adjusted {target.Name} (0x{target.Guid.Full:X8}) by {sign}{amount:N0} class ability point{(System.Math.Abs(amount) == 1 ? "" : "s")}. Available is now {availableAfter:N0}. Reason: {reasonText}");

            if (target != session.Player)
                Reply(target.Session, $"An admin adjusted your available class ability points by {sign}{amount:N0}. Available is now {availableAfter:N0}.");
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
