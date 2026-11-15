using System;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Frays;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// "/frays": a player's campfire mode and status; "/frays admin ..." for staff (settings, zones, placement check,
    /// test aids). Player-facing text uses ASCII hyphens only.
    /// </summary>
    public static class FraysCommands
    {
        private const string Usage = "/frays status | bar | bonuses | fire [upgrade] | perk [name|reset] | stop | go | start <n> | week | camps | report | tasks [take <n>|all, drop <n>] | collection | sigil <boon> | lfg [join|leave] | great | recall | claim | admin ... (staff)";

        // Short forms: /fray camps (/fc) for every camp's coords, /fray prog (/fp) for the Heat, Snarl
        // and Great Snarl bars. Anything else after /fray works as /frays does.
        [CommandHandler("fray", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "The Frays: /fray camps (/fc), /fray prog (/fp), /fp 1|2|3 picks an Ember Boon, /fray setup, boons, perks", FrayUsage)]
        public static void HandleFray(Session session, params string[] parameters)
        {
            var args = parameters ?? Array.Empty<string>();
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "prog";
            var player = session?.Player;

            if (player == null)
                return;

            switch (sub)
            {
                case "prog": case "progress": case "p":
                    HandleFrayProgress(session, args.Skip(1).ToArray());
                    return;
                case "camps": case "camp": case "c":
                    HandleFrays(session, "camps");
                    return;
                case "1": case "2": case "3": case "pick":
                    HandleFrayProgress(session, sub == "pick" ? args.Skip(1).ToArray() : args);
                    return;
                case "setup": case "loadout":
                    Reply(session, SetupText(player));
                    return;
                case "bonuses": case "bonus": case "caps":
                    Reply(session, BonusesFor(player));
                    return;
                case "boons": case "boon": case "plan":
                    Reply(session, SetBoonPlan(player, args.Skip(1).ToArray()));
                    return;
                case "perks" when args.Length > 1 && (FraysPerks.Presets.ContainsKey(args[1].ToLowerInvariant()) || args[1].Equals("off", StringComparison.OrdinalIgnoreCase)):
                    Reply(session, SetPerkPreset(player, args[1].ToLowerInvariant()));
                    return;
                case "journal":
                    Reply(session, FraysJournal.Give(player));
                    return;
                case "pet": case "pets":
                    Reply(session, FraysEffigies.Pet(player, string.Join(" ", args.Skip(1))));
                    return;
                case "codex": case "achievements":
                    Reply(session, FraysCodex.Readout(FraysManager.Record(player), args.Length > 1 ? args[1] : (args[0].Equals("achievements", StringComparison.OrdinalIgnoreCase) ? "achievements" : null)));
                    return;
                case "name":
                    Reply(session, SetFireLook(player, "name", string.Join(" ", args.Skip(1))));
                    return;
                case "sparks":
                    Reply(session, SetFireLook(player, "sparks", args.ElementAtOrDefault(1)));
                    return;
                case "epithet":
                    Reply(session, SetFireLook(player, "epithet", string.Join(" ", args.Skip(1))));
                    return;
                case "guide":
                {
                    // The Fray-Warden's guide as popup pages.
                    var said = FraysGuide.Show(player, args.ElementAtOrDefault(1), args.ElementAtOrDefault(2));
                    if (said != null)
                        Reply(session, said);
                    return;
                }
                case "help":
                    Reply(session, FrayUsage);
                    return;
                default:
                    HandleFrays(session, args);
                    return;
            }
        }

        // One command a line (a giant blurb is hard to read).
        private const string FrayUsage = "--- Fray commands ---\n"
            + "  /fg - the Fray-Warden's guide (popup pages); /fg how, heat, snarls, camps, week, progress\n"
            + "  /frays stop or /frays go - hold your Heat where it is, or let it climb\n"
            + "  /fray journal - a Fray Journal: your companion, pets, Codex and achievements in a book\n"
            + "  /fc - every campfire and its coords\n"
            + "  /fp - your Heat, Snarl and Great Snarl bars\n"
            + "  /fp 1, 2 or 3 - pick an Ember Boon\n"
            + "  /fray setup - your whole loadout\n"
            + "  /fray boons <aoe single sustain embers heat snarl> - your boon plan\n"
            + "  /fray perks balanced, aoe, single, embers or off - how perk points spend\n"
            + "  /fray codex [section] - your collection and achievements\n"
            + "  /fray pet [form] or /fray pet off - your follower\n"
            + "  /fray name <text> - name your campfire\n"
            + "  /fray sparks <colour> - spark colour\n"
            + "  /fray epithet <name> or none - the Epithet on your fire\n"
            + "Anything else works as /frays does.";

        /// <summary>/fg: the Fray-Warden's guide (/fray guide how is /fg how). The Codex is /fray codex.</summary>
        [CommandHandler("fg", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "The Frays: the Fray-Warden's guide; /fg <page>", "/fg [how|heat|snarls|camps|week|progress]")]
        public static void HandleFrayGuide(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            var args = parameters ?? Array.Empty<string>();
            var said = FraysGuide.Show(player, args.ElementAtOrDefault(0), args.ElementAtOrDefault(1));

            if (said != null)
                Reply(session, said);
        }

        [CommandHandler("fc", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "The Frays: every campfire and its coordinates", "/fc")]
        public static void HandleFrayCamps(Session session, params string[] parameters) => HandleFrays(session, "camps");

        /// <summary>/fp: the bars; /fp 1, 2 or 3: picks from the open Ember Boon offer.</summary>
        [CommandHandler("fp", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "The Frays: your Heat, Snarl and Great Snarl bars; /fp 1|2|3 picks an Ember Boon", "/fp [1|2|3]")]
        public static void HandleFrayProgress(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            if (parameters != null && parameters.Length > 0 && int.TryParse(parameters[0], out var choice))
            {
                var fire = FraysManager.FireOf(player);
                var said = fire == null ? "You have no camp. Light a free campfire (/fc lists them)." : fire.ChooseBoon(player, choice);

                if (said != null)
                    Reply(session, said);

                return;
            }

            HandleFrays(session, "bar");
        }

        /// <summary>/fray name, /fray sparks, /fray epithet: the campfire's look. A lit fire changes at once.</summary>
        private static string SetFireLook(WorldObjects.Player player, string what, string value)
        {
            var rec = FraysManager.Record(player);
            value = (value ?? "").Trim();
            string said;

            switch (what)
            {
                case "name":
                    if (value.Length == 0)
                        return $"Your campfire's name: {(string.IsNullOrEmpty(rec.FireName) ? "none (Frays Campfire)" : rec.FireName)}. /fray name <text> (3 to 24 letters, spaces, hyphens) or /fray name clear.";

                    if (value.Equals("clear", StringComparison.OrdinalIgnoreCase))
                    {
                        rec.FireName = "";
                        said = "Your campfire's own name is cleared.";
                        break;
                    }

                    var clean = FraysCodex.CleanFireName(value);

                    if (clean == null)
                        return "That name can't be used: 3 to 24 letters, spaces, hyphens or apostrophes, and nothing rude.";

                    rec.FireName = clean;
                    said = $"Your campfire is now {clean}.";
                    break;

                case "sparks":
                    if (value.Length == 0)
                        return $"Your sparks: {(string.IsNullOrEmpty(rec.Sparks) ? "none" : rec.Sparks)}. /fray sparks {string.Join(" | ", FraysSky.SparkChoices(rec))} | off.";

                    if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
                        rec.Sparks = "";
                    else if (FraysCodex.SparkColours.ContainsKey(value.ToLowerInvariant()) && !FraysSky.SparkAllowed(rec, value.ToLowerInvariant()))
                        return "The white sky sparks are bought with Sky Seals: Skyfire Sparks, from the Sky Steward on the Sky Fray plateau.";
                    else if (FraysCodex.SparkColours.ContainsKey(value.ToLowerInvariant()))
                        rec.Sparks = value.ToLowerInvariant();
                    else
                        return $"No such colour. /fray sparks {string.Join(" | ", FraysSky.SparkChoices(rec))} | off.";

                    said = string.IsNullOrEmpty(rec.Sparks) ? "Your sparks are off." : $"Your campfire now throws {rec.Sparks} sparks.";
                    break;

                default:
                    if (value.Length == 0)
                        return rec.Epithets.Count == 0 ? "No Epithets yet: achievements give them (/fray codex)." : $"Your Epithets: {string.Join(", ", rec.Epithets)}. Shown: {(string.IsNullOrEmpty(rec.Epithet) ? "none" : rec.Epithet)}. /fray epithet <name> | none.";

                    if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
                        rec.Epithet = "";
                    else if (rec.Epithets.FirstOrDefault(e => e.Equals(value, StringComparison.OrdinalIgnoreCase) || e.Equals("the " + value, StringComparison.OrdinalIgnoreCase)) is string ep)
                        rec.Epithet = ep;
                    else
                        return $"You have not earned that one. Yours: {(rec.Epithets.Count == 0 ? "none yet" : string.Join(", ", rec.Epithets))}.";

                    said = string.IsNullOrEmpty(rec.Epithet) ? "No Epithet shown." : $"You are shown as {player.Name} {rec.Epithet}.";
                    break;
            }

            FraysManager.SaveRecord(player, rec);
            FraysManager.FireOwnedBySolo(player.Guid.Full)?.RefreshLook();
            return said;
        }

        private static string SetBoonPlan(WorldObjects.Player player, string[] args)
        {
            var rec = FraysManager.Record(player);

            var summary = $"Your boon order: {FraysBoons.OrderText(rec.BoonOrder)}. Then your type plan: {FraysBoons.PlanText(rec.BoonPlan)}.";

            // No words opens the boon order window (FraysBoonOrderWindow).
            if (args.Length == 0)
                return FraysBoonOrderWindow.Open(player)
                    ? $"{summary} The boon order window is open: Yes to choose a new order one boon at a time. Typed: /fray boons order <numbers or names>, /fray boons list, /fray boons clear (the order), /fray boons reset (order and plan), or /fray boons aoe sustain embers for the type plan."
                    : $"{summary} Close the window you have open first, then /fray boons again.";

            var first = args[0].ToLowerInvariant();

            if (first == "list")
                return "The Ember Boons (numbers for /fray boons order):\n" + string.Join("\n", FraysBoons.All.Select(FraysBoons.Line));


            // Resets the boon path (if it is too hard, or to change it): order and type plan both
            if (first == "reset")
            {
                rec.BoonOrder = new System.Collections.Generic.List<string>();
                rec.BoonPlan = new System.Collections.Generic.List<string>();
                FraysManager.SaveRecord(player, rec);
                return "Boon path reset: no boon order, and the type plan is balanced (the class boon for your highest-rank ability).";
            }

            if (first == "clear")
            {
                rec.BoonOrder = new System.Collections.Generic.List<string>();
                FraysManager.SaveRecord(player, rec);
                return $"Boon order cleared. Your type plan picks: {FraysBoons.PlanText(rec.BoonPlan)}.";
            }

            if (first == "order")
            {
                var order = FraysBoons.ParseOrder(args.Skip(1));

                if (order.Count == 0)
                    return $"{summary} Set it: /fray boons order 3 5 1 (numbers from /fray boons list) or names, best first.";

                rec.BoonOrder = order;
                FraysManager.SaveRecord(player, rec);
                return $"Boon order set: {FraysBoons.OrderText(rec.BoonOrder)}. Boons left out fall back to your type plan ({FraysBoons.PlanText(rec.BoonPlan)}).";
            }

            rec.BoonPlan = args.Any(a => a.Equals("balanced", StringComparison.OrdinalIgnoreCase)) ? new System.Collections.Generic.List<string>() : FraysBoons.ParsePlan(args);
            FraysManager.SaveRecord(player, rec);
            return $"Boon plan set: {FraysBoons.PlanText(rec.BoonPlan)}.";
        }

        private static string CompanionSetupText(FraysPlayerRecord rec)
        {
            if (FraysCompanions.Active(rec) is { } act)
                return $"{FraysCompanions.FamilyName(act.Key)}, {act.Form.Role}, level {FraysCompanions.Level(act.Form.Xp)}";

            return rec.Companions.Count == 0 && !string.IsNullOrEmpty(rec.CompanionRole) ? $"old {rec.CompanionRole}, level {FraysCompanions.Level(rec.CompanionXp)}" : "none";
        }

        private static string SetPerkPreset(WorldObjects.Player player, string preset)
        {
            var rec = FraysManager.Record(player);
            rec.PerkPreset = preset;
            var bought = FraysPerks.AutoSpend(rec);
            FraysManager.SaveRecord(player, rec);

            return preset == "off"
                ? "Perk preset off: spend your own points with /frays perk <name>."
                : $"Perk preset {preset}: new perk points spend themselves.{(bought.Count > 0 ? " Spent now: " + string.Join(", ", bought) + "." : "")}";
        }

        /// <summary>The camp's damage reduction from Frays sources, before and after damage_reduction_cap (ASCII only).</summary>
        public static string DefenseText(FraysCampMods m) => BonusesText(m);

        /// <summary>
        /// /frays bonuses (a player can add up their bonuses and see the cap): the two
        /// Frays pools against their caps, e.g. "Damage +35% of +50%, Reduction 20% of 35%". ASCII only.
        /// </summary>
        public static string BonusesText(FraysCampMods m)
        {
            var dmgCap = FraysSettings.Number(FraysSettings.DamageBonusCap);
            var redCap = FraysSettings.Number(FraysSettings.DamageReductionCap);
            var boonCap = FraysSettings.Number(FraysSettings.BoonBonusCap);
            var (dmg, red) = m.PoolsBeforeCap();
            string Pct(double v) => $"{Math.Round(Math.Max(0, v) * 100, 1):0.#}%";
            string Over(double v, double cap) => v > cap + 1e-9 ? $" ({Pct(v)} before the cap)" : "";

            var boonDmg = Math.Min(boonCap, Math.Max(0, m.BoonDamage + Math.Max(m.BoonPack, Math.Max(m.BoonGilded, m.BoonSnarl))));
            var boonRed = Math.Min(boonCap, Math.Max(0, m.BoonReduction + m.BoonPhysicalReduction));

            return $"Frays bonuses: Damage +{Pct(Math.Min(dmg, dmgCap))} of +{Pct(dmgCap)}{Over(dmg, dmgCap)}, Reduction {Pct(Math.Min(red, redCap))} of {Pct(redCap)}{Over(red, redCap)}."
                + $" Boons give +{Pct(boonDmg)} of their +{Pct(boonCap)} damage (best target) and {Pct(boonRed)} of their {Pct(boonCap)} reduction."
                + " Outside the caps: the Slayer Stone, class abilities, ratings and gear (proof sets).";
        }

        /// <summary>/frays bonuses: the readout for the camper's own camp.</summary>
        private static string BonusesFor(WorldObjects.Player player)
        {
            var me = FraysManager.FireOf(player)?.CamperOf(player);

            if (me?.Mods == null)
                return "Frays bonuses count while you camp. Light or join a fire, then /frays bonuses. Caps: Damage +"
                    + $"{FraysSettings.Number(FraysSettings.DamageBonusCap) * 100:0}%, Reduction {FraysSettings.Number(FraysSettings.DamageReductionCap) * 100:0}%.";

            return BonusesText(me.Mods);
        }

        /// <summary>/fray setup: the whole loadout in one block, each line with the command that changes it.</summary>
        private static string SetupText(WorldObjects.Player player)
        {
            var rec = FraysManager.Record(player);
            var fire = FraysManager.FireOf(player);
            var me = fire?.CamperOf(player);
            var lines = new System.Collections.Generic.List<string>
            {
                "Your Fray loadout (everything has a default - lighting a fire is enough):",
                $"  Heat: {(rec.HeatStopped ? "stopped where it is" : me != null && me.AutoCap >= 0 ? $"held at {me.AutoCap} or below for {Math.Max(1, (me.AutoCapUntil - ACE.Common.Time.GetUnixTime()) / 60):0} more minutes (you fell too often), then climbing" : "climbing as you kill")} (/frays stop, /frays go)",
                $"  Boon plan: {FraysBoons.PickRuleText(rec.BoonOrder, rec.BoonPlan)} (/fray boons opens the order window)",
                $"  Perks: preset {rec.PerkPreset}, {FraysPerks.Spent(rec)} of {FraysPerks.Points(rec)} points spent, campfire tier {rec.FireTier} (/fray perks <preset>, /frays fire)",
                $"  Companion: {CompanionSetupText(rec)} (/frays companion <form> [style])",
                $"  This week: {FraysWards.WeekText(FraysWeek.Current)} - bring the gear for it.",
            };

            if (me != null)
            {
                lines.Add($"This camp: Heat {me.Level}, {me.Boons.Count} Ember Boon{(me.Boons.Count == 1 ? "" : "s")}{(me.Boons.Count > 0 ? ": " + string.Join(", ", me.Boons.Select(k => FraysBoons.Find(k)?.Name)) : "")}" +
                    $"{(me.Offer.Count > 0 ? "; a boon waits: /fp 1, 2 or 3" : "")}.");

                if (me.Mods != null)
                    lines.Add("  " + DefenseText(me.Mods));
            }

            return string.Join("\n", lines);
        }

        [CommandHandler("frays", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "The Frays: your campfire, Heat and status", Usage)]
        public static void HandleFrays(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            var args = parameters ?? Array.Empty<string>();
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

            try
            {
                switch (sub)
                {
                    case "status":
                        Reply(session, Status(player));
                        return;

                    case "bonuses":
                    case "bonus":
                    case "caps":
                        Reply(session, BonusesFor(player));
                        return;

                    case "bar":
                    case "bars":
                        Reply(session, BarText(player));
                        return;

                    case "stop":
                        Reply(session, SetHeatStopped(player, true));
                        return;

                    case "go":
                    case "climb":
                        Reply(session, SetHeatStopped(player, false));
                        return;

                    case "mode":
                        Reply(session, "Modes are gone. /frays stop holds your Heat where it is; /frays go lets it climb again.");
                        return;

                    case "start":
                        Reply(session, SetStart(player, args.Skip(1).ToArray()));
                        return;

                    case "reroll":
                        Reply(session, FraysLongTerm.SetRerollTarget(player, args.Skip(1).ToArray()));
                        return;

                    case "week":
                        Reply(session, WeekText());
                        return;

                    case "companion":
                        Reply(session, FraysLongTerm.Companion(player, args.Skip(1).ToArray()));
                        return;

                    case "fire":
                        if (args.Length > 1 && args[1].Equals("upgrade", StringComparison.OrdinalIgnoreCase))
                            Reply(session, FraysLongTerm.UpgradeFire(player));
                        else
                            Reply(session, FraysPerks.Text(FraysManager.Record(player), FraysWeek.Current));
                        return;

                    case "perk":
                    case "perks":
                    {
                        var rec = FraysManager.Record(player);

                        if (args.Length < 2)
                        {
                            Reply(session, FraysPerks.Text(rec, FraysWeek.Current));
                            return;
                        }

                        var said = args[1].Equals("reset", StringComparison.OrdinalIgnoreCase)
                            ? FraysPerks.Reset(rec, FraysWeek.Current)
                            : FraysPerks.Spend(rec, string.Join("", args.Skip(1)));
                        FraysManager.SaveRecord(player, rec);
                        Reply(session, said);
                        return;
                    }


                    case "ascend":
                        Reply(session, FraysLongTerm.Ascend(player, args.Length > 1 && args[1].Equals("confirm", StringComparison.OrdinalIgnoreCase)));
                        return;

                    case "great":
                    case "greatsnarl":
                    case "gs":
                        Reply(session, FraysFrayWide.TravelToGreat(player));
                        return;

                    case "lfg":
                    case "group":
                    {
                        var lfgSub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                        Reply(session, lfgSub == "leave" || lfgSub == "quit" ? FraysLfg.Leave(player) : lfgSub.Length > 0 ? FraysLfg.Join(player) : FraysLfg.Text());
                        return;
                    }

                    case "collection":
                    case "collect":
                        FraysCollection.Show(player);
                        Reply(session, "Your boon collection is open in a popup.");
                        return;

                    case "sigil":
                    case "sigils":
                        Reply(session, args.Length > 1 ? FraysCollection.UseSigil(player, string.Join(" ", args.Skip(1))) : FraysCollection.Text(FraysManager.Record(player)));
                        return;

                    case "hunt":
                    case "hunts":
                    case "task":
                    case "tasks":
                    case "bounty":
                    case "bounties":
                    {
                        var huntSub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                        var num = args.Length > 2 && int.TryParse(args[2], out var hn) ? hn : 0;
                        Reply(session, huntSub == "take" ? "Tasks are picked up from the task-givers in the Frays village: the Fray Huntmaster, Snarl-Tracker, Heat-Warden and Week-Warden." : huntSub == "drop" ? FraysHunts.Drop(player, num) : FraysHunts.Board(player));
                        return;
                    }

                    case "recall":
                        Reply(session, FraysLongTerm.Recall(player));
                        return;

                    case "claim":
                        Reply(session, FraysLongTerm.Claim(player));
                        return;

                    case "camps":
                    case "camp":
                    case "frays":   // the old /frays frays (now /fray camps or /fc)
                        Reply(session, string.Join("\n", FraysManager.CampsLines(player).Append(FraysLongTerm.LucidText())));
                        return;

                    case "report":
                        var last = FraysManager.Record(player).LastReport;
                        Reply(session, string.IsNullOrEmpty(last) ? "No camp report yet: it is written when a camp ends." : last);
                        return;

                    case "admin":
                        if (session.AccessLevel < AccessLevel.Admin)
                        {
                            Reply(session, "Only staff can use /frays admin.");
                            return;
                        }

                        Reply(session, Admin(session, player, args.Skip(1).ToArray()));
                        return;

                    default:
                        Reply(session, Usage);
                        return;
                }
            }
            catch (Exception ex)
            {
                Reply(session, $"/frays failed: {ex.Message}");
            }
        }

        private static void Reply(Session session, string text)
        {
            foreach (var line in (text ?? "").Split('\n'))
                session.Network.EnqueueSend(new GameMessageSystemChat(line.TrimEnd('\r'), ChatMessageType.Broadcast));
        }

        /// <summary>/frays stop and /frays go (one Heat switch in place of the five modes).</summary>
        private static string SetHeatStopped(WorldObjects.Player player, bool stopped)
        {
            var rec = FraysManager.Record(player);
            rec.Mode = stopped ? FraysMode.Hold : FraysMode.Climb;
            FraysManager.SaveRecord(player, rec);

            return stopped
                ? "Heat stopped: it holds where it is, with no climbing and no cooling. Stopped Heat does not count toward your Heat rating. /frays go to climb again."
                : "Heat climbing: it rises as you kill and cools when you stop. A fall costs 2. /frays stop to hold it where it is.";
        }

        private static string WeekText()
        {
            var lines = new System.Collections.Generic.List<string> { FraysWeek.Summary() };
            lines.AddRange(FraysFamilies.WeekLines());
            return string.Join("\n", lines);
        }

        private static string SetStart(WorldObjects.Player player, string[] args)
        {
            var rec = FraysManager.Record(player);
            var below = FraysSettings.Int(FraysSettings.HeatStartBelowRating);

            if (args.Length > 0 && args[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                rec.StartHeat = 0;
                rec.StartHeatChosen = false;
                FraysManager.SaveRecord(player, rec);
                return $"Your camps start {below} Heat under your rating (now Heat {rec.WantedStart(false)}). Saved for this character.";
            }

            if (args.Length == 0 || !int.TryParse(args[0], out var n) || n < 0)
                return $"Use: /frays start <n> or /frays start auto. You can start at any Heat up to your rating ({rec.Rating}). Now: "
                    + (rec.StartHeatChosen || rec.StartHeat > 0 ? $"Heat {rec.StartHeat}." : $"auto, {below} under your rating (Heat {rec.WantedStart(false)}).");

            var start = Math.Min(n, rec.Rating);
            rec.StartHeat = start;
            rec.StartHeatChosen = true;
            FraysManager.SaveRecord(player, rec);

            return n > rec.Rating
                ? $"Your rating is {rec.Rating}, so you will start at Heat {start}. Saved for this character."
                : $"You will start at Heat {start} when you join a camp. Saved for this character.";
        }

        /// <summary>/frays bar: every bar at once - your Heat, your Snarl, the Great Snarl and the weekly goal.</summary>
        private static string BarText(WorldObjects.Player player)
        {
            var now = ACE.Common.Time.GetUnixTime();
            var fire = FraysManager.FireOf(player);
            var me = fire?.CamperOf(player);

            if (fire == null || me == null)
            {
                // No camp: the Fray's own bars still show when you are in one (or near one).
                var near = FraysManager.NearestFire(player);
                var head = "You have no camp, so no Heat or Snarl yet. Light a free campfire (/fc lists them).";
                return near == null ? head : head + "\n" + string.Join("\n", FraysFrayWide.BarLines(near.Def.FrayKey, now));
            }

            var (have, need) = FraysFire.ProgressOf(me);
            var grace = FraysSettings.Number(FraysSettings.HeatDecayGraceSeconds);
            var idle = now - me.LastKillAt;
            var cooling = me.Limit.Frozen ? "held steady" : idle >= grace ? "dropping - kill to stop it" : $"warm ({Math.Max(0, grace - idle):0} s before it cools)";
            var lines = new System.Collections.Generic.List<string>
            {
                $"Heat {me.Level} {FraysRules.Bar((double)have / need)} ({have}/{need}), {cooling}. Limit {me.Limit.Level}, peak {me.PeakLevel}.",
                me.Snarl == SnarlPact.Off ? $"Your {fire.SnarlName(false)}: off."
                    : me.SnarlAlive != null ? $"Your {fire.SnarlName(false)}: up now, kill it for its cache."
                    : $"Your {fire.SnarlName(false)}: {FraysRules.Bar(me.SnarlBar)}{(fire.TwoSnarls ? " (brute packs fill it)" : "")}",
            };

            // snarl_adds: swarm pack kills fill the Snarl adds bar.
            if (me.Snarl != SnarlPact.Off && fire.TwoSnarls)
                lines.Add(me.SnarlAdds.Count > 0 ? $"Your Swarm Snarl: up now, {me.SnarlAdds.Count(a => a != null && !a.IsDead)} left, kill them all for the cache." : $"Your Swarm Snarl: {FraysRules.Bar(me.SnarlAddsBar)} (swarm packs fill it)");

            if (me.Offer.Count > 0)
                lines.Add($"Ember Boon waiting - /fp 1, 2 or 3: {FraysBoons.OfferText(me.Offer, k => FraysCollection.Rank(FraysManager.Record(player), k), player.GetClassAbilityRank)}");
            else if (me.Boons.Count > 0)
                lines.Add($"Ember Boons this camp: {string.Join(", ", me.Boons.Select(k => FraysBoons.Find(k)?.Name))}.");

            lines.AddRange(FraysFrayWide.BarLines(fire.Def.FrayKey, now));
            return string.Join("\n", lines);
        }

        private static string Status(WorldObjects.Player player)
        {
            var rec = FraysManager.Record(player);
            var lines = new System.Collections.Generic.List<string>
            {
                $"Frays: {rec.HeatText}, start Heat {Math.Min(rec.WantedStart(false), rec.Rating)}, Heat rating {rec.Rating}, Embers earned {Math.Floor(rec.Embers):0}."
                    + (rec.Ascensions > 0 ? $" Ascension {FraysLongTerm.Roman(rec.Ascensions)}, {FraysLongTerm.AscensionTitle(rec.Ascensions)}." : "")
            };

            var fire = FraysManager.FireOf(player);
            var me = fire?.CamperOf(player);

            if (fire != null && me != null)
            {
                var (have, need) = FraysFire.ProgressOf(me);
                var active = FraysRules.IsActive(me.LastActiveAt, ACE.Common.Time.GetUnixTime(), FraysSettings.Number(FraysSettings.ActivitySeconds));
                var ratingNeed = FraysSettings.Number(FraysSettings.RatingHoldMinutes);

                lines.Add($"Your fire: {fire.Def.RoomName}, owned by {fire.OwnerName}. Your Heat {me.Level} {FraysRules.Bar((double)have / need)} ({have}/{need} to the next), limit {me.Limit.Level}{(me.Limit.Frozen ? ", held steady" : "")}.");
                lines.Add($"Experience: {(active ? "earning" : "paused - fight to earn")}. Rating progress: {me.RatingMinutes} of {ratingNeed:0} fighting minutes at Heat {me.RatingLevel}{(me.Limit.Frozen ? " (stopped Heat does not count)" : "")}.");
                if (fire.Def.Outdoor)
                {
                    var (waiting, resting) = fire.ZoneCounts(me, ACE.Common.Time.GetUnixTime());
                    var hotQ = FraysRules.HotQuadrant(ACE.Common.Time.GetUnixTime(), fire.Def.Id, FraysSettings.Number(FraysSettings.HotSpotMinutes));
                    lines.Add($"Your zone: {waiting} creatures waiting, {resting} points resting{(hotQ >= 0 ? $", the {FraysRules.QuadrantNames[hotQ]} flares" : "")}. Nests (big packs) rest {FraysSettings.Number(FraysSettings.NestRestSeconds) / 60:0} minutes; quick points {FraysSettings.Number(FraysSettings.QuickRestSeconds):0} seconds.");

                    // The fixed layout (FraysLayout): the same spots every visit, so a route keeps working.
                    if (fire.Layout is FraysCampLayout layout)
                    {
                        var now = ACE.Common.Time.GetUnixTime();
                        var buff = FraysLayout.BuffForWeek(FraysWeek.Current);
                        var shrineText = layout.Shrine < 0 ? "" : now < me.ShrineReadyAt
                            ? $" The Ember Shrine burns again in {Math.Ceiling(me.ShrineReadyAt - now):0} seconds."
                            : $" The Ember Shrine is ready: {FraysLayout.BuffName(buff)}, {FraysLayout.BuffText(buff)} for {FraysSettings.Number(FraysSettings.ShrineBuffSeconds):0} seconds.";
                        lines.Add($"This zone keeps the same layout every visit: {layout.Rich.Count} Gilded packs (x{FraysSettings.Number(FraysSettings.RichMult):0.#} Embers and key bar, back {FraysSettings.Number(FraysSettings.RichRestFightMult):0.#} times as long as they took to kill, at least {FraysSettings.Number(FraysSettings.RichRestSeconds) / 60:0.#} minutes after they fall, a gold glow just before).{shrineText}");
                    }
                }

                lines.Add($"This camp: {me.Kills} kills, {me.Deaths} deaths, {me.XpPaid:N0} xp, {me.LumPaid:N0} lum, {Math.Floor(me.EmbersEarned):0} Embers.");
                if (me.Mods != null)
                    lines.Add(DefenseText(me.Mods));
                lines.Add(
                    (me.SnarlAlive != null ? $"The {fire.SnarlName(false)} is up!" : $"{fire.SnarlName(false)}: {Math.Min(100, me.SnarlBar * 100):0}%.") + (fire.TwoSnarls ? (me.SnarlAdds.Count > 0 ? " The Swarm Snarl is up!" : $" Swarm Snarl: {Math.Min(100, me.SnarlAddsBar * 100):0}%.") : ""));

                var others = fire.CampersSnapshot().Where(c => c.Guid != me.Guid).Select(c => $"{c.Name} {c.Level}").ToList();

                if (others.Count > 0)
                    lines.Add($"Fellows here (Heat): {string.Join(", ", others)}.");
            }
            else if (fire != null)
                lines.Add($"Your fellowship holds {fire.Def.RoomName}. Go to the fire to join the camp.");
            else if (FraysManager.NearestFire(player) is FraysFire near)
            {
                lines.Add(near.Lit
                    ? $"Nearest fire: {near.Def.RoomName}, owned by {near.OwnerName}. Its monsters are not yours to fight."
                    : $"Nearest fire: {near.Def.RoomName}, unlit. Use it to light it.");
            }

            if (fire != null)
            {
                var (great, goal) = FraysFrayWide.Bars(fire.Def.FrayKey);
                lines.Add($"Great Snarl {great:0}%, the Frays' weekly goal {goal:0}%. {FraysLongTerm.LucidText()}");
            }

            rec.RollWeek(FraysWeek.Current);
            var emberCap = FraysSettings.Number(FraysSettings.EmberWeeklyCap);
            lines.Add(emberCap > 0
                ? $"This week: {Math.Floor(rec.WeekEmbers):0} of {emberCap:0} Embers at the full rate. Affix: {FraysWeek.Name(FraysWeek.Shift)}."
                : $"This week: {Math.Floor(rec.WeekEmbers):0} Embers earned. Affix: {FraysWeek.Name(FraysWeek.Shift)}.");

            return string.Join("\n", lines);
        }

        /// <summary>Beacon candidates to try: retail models that might read as a beam or a tall marker.</summary>
        private static readonly (string Key, uint Setup, string What)[] BeaconCandidates =
        {
            ("aerbax", 0x02001312, "Aerbax Beam"),
            ("beacon", 0x0200125B, "Beacon"),
            ("spire", 0x020015CE, "Dark Crystal Spire"),
            ("acid", 0x02001733, "Pillar of Acid"),
        };

        private static readonly System.Collections.Generic.List<WorldObjects.WorldObject> beaconPreviews = new System.Collections.Generic.List<WorldObjects.WorldObject>();

        /// <summary>
        /// /frays admin beacon: drops candidate beacon models beside you to choose from (not saved), clears them,
        /// or applies the chosen beacon settings to every loaded campfire.
        /// </summary>
        private static string BeaconPreview(WorldObjects.Player player, string[] a)
        {
            var what = a.Length > 0 ? a[0].ToLowerInvariant() : "";
            var list = string.Join(", ", BeaconCandidates.Select(c => $"{c.Key} ({c.What}, 0x{c.Setup:X8})"));

            if (what == "clear")
            {
                foreach (var wo in beaconPreviews.Where(w => w != null && !w.IsDestroyed).ToList())
                {
                    FraysHouses.RemoveCovers(wo);   // a town house's covers, even ones put up again after a hand fix
                    if (!wo.IsDestroyed)
                        wo.Destroy();
                }

                beaconPreviews.Clear();
                return "Beacon previews removed.";
            }

            if (what == "apply")
            {
                var n = 0;

                foreach (var fire in FraysManager.Fires)
                {
                    fire.Landblock.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(fire.RefreshMarkers));
                    n++;
                }

                return $"Refreshing markers on {n} loaded campfires (beacon_mode {FraysSettings.Text(FraysSettings.BeaconMode)}, beacon_setup {FraysSettings.Text(FraysSettings.BeaconSetup)}).";
            }

            if (what.Length == 0)
                return $"Use: /frays admin beacon <all | {string.Join(" | ", BeaconCandidates.Select(c => c.Key))} | 0x02xxxxxx> [scale] | clear | apply. Candidates: {list}. " +
                    "Pick one with /frays admin set beacon_setup 0x..., then /frays admin beacon apply.";

            var scale = a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : (float)FraysSettings.Number(FraysSettings.BeaconScale);
            var picks = what == "all" ? BeaconCandidates.ToList()
                : BeaconCandidates.Where(c => c.Key == what).ToList();

            if (picks.Count == 0)
            {
                var setup = FraysRules.ParseSetup(what);

                if (setup == 0)
                    return $"Not a candidate or a 0x02 setup id. Candidates: {list}.";

                picks.Add((what, setup, $"setup 0x{setup:X8}"));
            }

            var placed = new System.Collections.Generic.List<string>();

            for (var i = 0; i < picks.Count; i++)
            {
                // A row 12 m apart, 15 m in front of you (north-east of where you stand).
                var at = new ACE.Entity.Position(player.Location);
                at.PositionX += 15 + i * 12;
                at.PositionY += 15;

                // Outdoors the point must sit in its own 24 m land cell, on the ground (a marker keeps no gravity).
                if ((at.LandblockId.Raw & 0xFFFF) < 0x100)
                {
                    at.PositionX = Math.Clamp(at.PositionX, 1f, 191f);
                    at.PositionY = Math.Clamp(at.PositionY, 1f, 191f);
                    at.LandblockId = new ACE.Entity.LandblockId((at.LandblockId.Raw & 0xFFFF0000) | FraysRules.OutdoorCell(true, 0, at.Pos));
                    at.PositionZ = at.GetTerrainZ();
                }

                var wo = FraysFire.MakeMarker(picks[i].Setup, scale, at, $"Beacon preview: {picks[i].What}");
                log4net.LogManager.GetLogger(typeof(FraysCommands)).Info($"[FRAYS] beacon preview {picks[i].What} at {at.ToLOCString()}: {(wo == null ? "NOT placed" : "placed")}");

                if (wo != null)
                {
                    beaconPreviews.Add(wo);
                    placed.Add($"{picks[i].What} (0x{picks[i].Setup:X8})");
                }
            }

            return placed.Count == 0 ? "No preview could be placed here (try open ground)."
                : $"Placed at scale {scale}, west to east: {string.Join(", ", placed)}. /frays admin beacon clear removes them.";
        }

        private static string Admin(Session session, WorldObjects.Player player, string[] a)
        {
            var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "";
            string Arg(int i) => a.Length > i ? a[i] : null;

            switch (sub)
            {
                // Testing pets and the companion: /frays admin effigy <family | all>, /frays admin kindling [n].
                case "effigy":
                {
                    var key = (Arg(1) ?? "").ToLowerInvariant();
                    var picks = key == "all" ? FraysFamilies.All.ToList()
                        : FraysFamilies.All.Where(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || f.Name.ToLowerInvariant().Contains(key)).Take(1).ToList();

                    if (key.Length == 0 || picks.Count == 0)
                        return $"Use: /frays admin effigy <family | all>. Families: {string.Join(", ", FraysFamilies.All.Select(f => f.Key))}.";

                    var given = picks.Count(f => FraysEffigies.Give(player, f, "staff test"));
                    return $"{given} Fray Effig{(given == 1 ? "y" : "ies")} in your pack. Use one to collect its form, then /fray pet <family>.";
                }
                case "kindling":
                {
                    var n = int.TryParse(Arg(1), out var k) ? Math.Clamp(k, 1, 100) : 10;
                    FraysLongTerm.Give(player, FraysLongTerm.Kindling, n, $"{n} Kindling for testing the companion.");
                    return $"{n} Kindling in your pack. Give a collected form a style with /frays companion <form> warder|mender|striker, then use the Kindling: it levels your active companion.";
                }
                case "settings":
                    return FraysAdmin.Settings(Arg(1));
                case "set":
                    return FraysAdmin.Set(Arg(1), string.Join(" ", a.Skip(2)), session);
                case "reset":
                    return FraysAdmin.Reset(Arg(1), session);
                case "fire":
                    return FraysAdmin.FireOverride(Arg(1) ?? "", Arg(2)?.ToLowerInvariant(), Arg(3), Arg(4), session);
                case "zone":
                    return FraysAdmin.Zone(player, Arg(1), Arg(2));
                case "check":
                    return FraysAdmin.Check(Arg(1));
                case "fires":
                    return FraysAdmin.Fires();

                case "goto":
                {
                    var fire = FraysManager.FindFire(player, Arg(1) ?? "");

                    if (fire == null)
                        return "No such fire loaded. /frays admin fires lists them.";

                    var pos = fire.FirePosition;
                    pos.PositionX += 2;
                    player.Teleport(pos);
                    return $"Teleporting to {fire.Def.Id} ({fire.Def.RoomName}).";
                }

                case "heat":
                {
                    var fire = FraysManager.FindFire(player, Arg(1) ?? "");

                    if (fire == null || !int.TryParse(Arg(2), out var level))
                        return "Use: /frays admin heat <fire> <level> (sets your own Heat at a lit fire where you are camping).";

                    return fire.AdminSetHeat(player, level) ? $"Your Heat at {fire.Def.Id} is now {fire.LevelOf(player)}." : "You have no camp at that fire (light it and stand at it first).";
                }

                case "snarl":
                {
                    // Testing aid: your Snarl now. Fire optional: the one you camp at.
                    var fire = Arg(1) != null ? FraysManager.FindFire(player, Arg(1)) : FraysManager.FireOwnedBySolo(player.Guid.Full);
                    return fire == null ? "Use: /frays admin snarl [fire] at a fire where you are camping." : fire.AdminSnarl(player);
                }

                case "houses":
                {
                    // The town houses, a picked list of 17 with all their doors
                    // and windows blocked: Town House 1 to 17 (WCIDs 2000124-2000140) with their doors and windows covered, in three
                    // rows north of you, 26 m apart. /frays admin houses clear removes them.
                    if (Arg(1) == "clear")
                        return BeaconPreview(player, new[] { "clear" });

                    BeaconPreview(player, new[] { "clear" });
                    var here = player.Location;
                    var lbHere = player.CurrentLandblock;

                    if ((here.Cell & 0xFFFF) >= 0x100 || lbHere == null)
                        return "Stand outdoors.";

                    var shown = 0;
                    var wcids = FraysHouses.Wcids.ToArray();
                    // The grid (6 across, 26 m apart) starts north of you, moved as needed to fit inside the landblock (standing
                    // near an edge, rows past it would be skipped and only 5 houses would show).
                    var x0 = System.Math.Clamp(here.PositionX - 65, 13f, 179f - 130f);
                    var y0 = System.Math.Clamp(here.PositionY + 26, 13f, 179f - 52f);

                    for (var i = 0; i < wcids.Length; i++)
                    {
                        var x = x0 + 26 * (i % 6);
                        var y = y0 + 26 * (i / 6);

                        if (x < 12 || x > 180 || y < 12 || y > 180)
                            continue;

                        var cell = FraysRules.OutdoorCell(true, 0, new System.Numerics.Vector3(x, y, 0));
                        var house = FraysManager.PlaceFixture(lbHere, wcids[i], cell, new System.Numerics.Vector3(x, y, 0), FraysVillage.Facing(0), exact: true);

                        if (house == null)
                            continue;

                        beaconPreviews.Add(house);
                        beaconPreviews.AddRange(FraysHouses.PlaceCovers(lbHere, house, 0, 1));
                        shown++;
                    }

                    return $"Placed {shown} of {wcids.Length} town houses and city walls in three rows north of you (26 m apart; moved to fit if you stand near the edge of the area). Town Houses 1 to 11 are wcids {FraysHouses.FirstWcid} to {FraysHouses.FirstWcid + 10}, City Walls 12 to 17 are {FraysHouses.FirstWcid + 11} to {FraysHouses.FirstWcid + 16}. Click a door or window and use /frays admin cover to fix it (help: /frays admin cover help). /frays admin houses clear removes them all.";
                }

                case "catalogue":
                {
                    // Preview, to find more buildings like the cottage (more detail on the shell, fewer
                    // windows and doors): every town building model on the map, each on a solid town piece (the Cottage's object,
                    // its model swapped). Two rows north of you, 24 m apart, named by model. /frays admin catalogue clear.
                    if (Arg(1) == "clear")
                        return BeaconPreview(player, new[] { "clear" });

                    // The whole catalogue (FraysTownModels: every town building on the map, most used first), 14 a page.
                    var pages = (FraysTownModels.All.Length + 13) / 14;
                    var page = int.TryParse(Arg(1), out var pg) ? System.Math.Clamp(pg, 1, pages) : 1;
                    var models = FraysTownModels.All.Skip((page - 1) * 14).Take(14).ToArray();
                    BeaconPreview(player, new[] { "clear" });   // the last page's houses go first
                    var me = player.Location;
                    var placedHouses = 0;

                    if ((me.Cell & 0xFFFF) >= 0x100)
                        return "Stand outdoors.";

                    for (var i = 0; i < models.Length; i++)
                    {
                        var x = me.PositionX - 72 + 24 * (i % 7);
                        var y = me.PositionY + 24 + 26 * (i / 7);

                        if (x < 1 || x > 191 || y < 1 || y > 191)
                            continue;

                        if (ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject(FraysDefs.ArwicCottage) is not ACE.Server.WorldObjects.WorldObject house)
                            continue;

                        house.Biota.PropertiesAnimPart = new System.Collections.Generic.List<ACE.Entity.Models.PropertiesAnimPart> { new ACE.Entity.Models.PropertiesAnimPart { Index = 0, AnimationId = models[i] } };
                        house.Name = $"House 0x{models[i]:X8}";
                        var cell = FraysRules.OutdoorCell(true, 0, new System.Numerics.Vector3(x, y, 0));
                        house.Location = new ACE.Entity.Position((me.Cell & 0xFFFF0000) | cell, x, y, 0, 0, 0, 0, 1, me.Instance);
                        house.Location.PositionZ = ACE.Server.Entity.PositionExtensions.GetTerrainZ(house.Location);
                        house.FraysTransient = true;
                        house.TimeToRot = -1;

                        if (house.EnterWorld())
                        {
                            beaconPreviews.Add(house);
                            placedHouses++;
                        }
                        else
                            house.Destroy();
                    }

                    return $"Page {page} of {pages}: placed {placedHouses} houses in two rows north of you (24 m apart; hover for each one's model; stand with open ground to the north). Next: /frays admin catalogue {System.Math.Min(page + 1, pages)}. /frays admin catalogue clear removes them.";
                }

                case "structures":
                {
                    // Preview: the open structures the map places as objects (tents, canopies, stalls: two to four
                    // posts, low), in a row east of you, 16 m apart, each named by its setup. /frays admin beacon clear removes them.
                    if (Arg(1) == "clear")
                        return BeaconPreview(player, new[] { "clear" });

                    var ids = new uint[] { 0x02000395, 0x02000A56, 0x02000A51, 0x02000A50, 0x02000B65, 0x02000B66, 0x02000B67, 0x02000B68,
                        0x020006C8, 0x020010B0, 0x020010B1, 0x020010B2, 0x020015B1 };
                    var placed = 0;

                    for (var i = 0; i < ids.Length; i++)
                    {
                        var me = player.Location;
                        var x = me.PositionX + 8 + 16 * i;

                        if (x > 191 || (me.Cell & 0xFFFF) >= 0x100)
                            break;

                        var cell = FraysRules.OutdoorCell(true, 0, new System.Numerics.Vector3(x, me.PositionY, 0));
                        var at = new ACE.Entity.Position((me.Cell & 0xFFFF0000) | cell, x, me.PositionY, me.PositionZ, 0, 0, 0, 1, me.Instance);
                        at.PositionZ = ACE.Server.Entity.PositionExtensions.GetTerrainZ(at);

                        var wo = FraysFire.MakeMarker(ids[i], 1f, at, $"Structure 0x{ids[i]:X8}");

                        if (wo != null)
                        {
                            beaconPreviews.Add(wo);
                            placed++;
                        }
                    }

                    return $"Placed {placed} structures in a row east of you (16 m apart; hover for each one's id; the row stops at the landblock edge, so stand at its west side). /frays admin structures clear removes them.";
                }

                case "cover":
                case "covers":
                    // Hand fixes to the town houses' door and window covers: click one, then push, slide, size or swap it.
                    return FraysHouses.Command(session, player, a.Skip(1).ToArray());

                case "piece":
                case "village":
                    // The in-game village tool: select a piece, then move, turn, add or remove it.
                    return FraysVillage.Command(session, player, a.Skip(1).ToArray());

                case "firetier":
                {
                    // Testing aid: your campfire tier (each tier is a perk point; side flames from 5 and 10).
                    var max = FraysSettings.Int(FraysSettings.FireTierMax);

                    if (!int.TryParse(Arg(1), out var tier) || tier < 0 || tier > max)
                        return $"Use: /frays admin firetier <0 to {max}>. Each tier is a perk point; tiers 5 and 10 add side flames to your lit fire.";

                    var rec = FraysManager.Record(player);
                    rec.FireTier = tier;
                    var spent = FraysPerks.AutoSpend(rec);
                    FraysManager.SaveRecord(player, rec);
                    FraysManager.FireOwnedBySolo(player.Guid.Full)?.RefreshLook();
                    return $"Your campfire is tier {tier}.{(spent.Count > 0 ? " Your perk preset spent: " + string.Join(", ", spent) + "." : "")} /frays perk shows your perks.";
                }

                case "boon":
                {
                    // Testing aid: an Ember Boon offer now.
                    var fire = Arg(1) != null ? FraysManager.FindFire(player, Arg(1)) : FraysManager.FireOwnedBySolo(player.Guid.Full);
                    return fire == null ? "Use: /frays admin boon [fire] at a fire where you are camping." : fire.AdminBoon(player);
                }

                case "ascfx":
                {
                    // Preview the Ascension camp spark: ascfx <1-5> plays it on your fire's flames (or on you).
                    if (!int.TryParse(Arg(1), out var n) || n < 1 || n > 5)
                        return "Use: /frays admin ascfx <1-5>: the camp spark of Ascension I to V, on your campfire's flames (or on you without one). "
                            + "Scripts: I two low sparks, II four low, III four low and two mid, IV four low and four mid, V low, mid and high (PlayScript 0x67 to 0x72).";

                    var flames = FraysManager.FireOwnedBySolo(player.Guid.Full)?.FlameObject;
                    FraysLongTerm.PlaySpark(flames ?? (WorldObjects.WorldObject)player, n);
                    return $"Playing the Ascension {FraysLongTerm.Roman(n)} spark on {(flames != null ? "your campfire" : "you")}: {string.Join(", ", FraysLongTerm.AscensionSparkScripts(n))}.";
                }

                case "heatfx":
                {
                    // Preview the Heat level-up looks: heatfx lists them, heatfx <n> plays one on you.
                    if (!int.TryParse(Arg(1), out var n))
                    {
                        var now = FraysSettings.Int(FraysSettings.HeatUpEffect);
                        return "--- Heat level-up looks ---\n"
                            + string.Join("\n", FraysManager.HeatUpLooks.Select((l, i) => $"  {i + 1}. {l.Name}{(i + 1 == now ? "  (in use)" : "")}"))
                            + "\nTry one: /frays admin heatfx <number>\nUse one: /frays admin set heat_up_effect <number>";
                    }

                    return FraysManager.PlayHeatLook(player, n) ? $"Playing look {n}: {FraysManager.HeatUpLooks[n - 1].Name}." : $"No look {n} (1 to {FraysManager.HeatUpLooks.Count}).";
                }

                case "greatsnarl":
                {
                    // Testing aid: the Fray's Great Snarl now (after its warning).
                    var fire = Arg(1) != null ? FraysManager.FindFire(player, Arg(1)) : FraysManager.FireOwnedBySolo(player.Guid.Full);
                    return fire == null ? "Use: /frays admin greatsnarl [fire] (a fire in the Fray to call it to)." : FraysFrayWide.AdminGreatSnarl(fire);
                }

                case "out":
                {
                    var fire = FraysManager.FindFire(player, Arg(1) ?? "");

                    if (fire == null)
                        return "No such fire loaded.";

                    fire.Landblock.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() => fire.Extinguish("put out by staff")));
                    return $"Putting out {fire.Def.Id}.";
                }

                case "firename":
                {
                    // Staff: clear a player's campfire name.
                    var who = string.Join(" ", a.Skip(1));

                    if (!(Managers.PlayerManager.GetOnlinePlayer(who) is WorldObjects.Player t))
                        return "Use: /frays admin firename <online player> - clears their campfire's name.";

                    var trec = FraysManager.Record(t);
                    trec.FireName = "";
                    FraysManager.SaveRecord(t, trec);
                    FraysManager.FireOwnedBySolo(t.Guid.Full)?.RefreshLook();
                    FraysManager.Tell(t, "Staff cleared your campfire's name.");
                    return $"Cleared {t.Name}'s campfire name.";
                }

                case "beacon":
                    return BeaconPreview(player, a.Skip(1).ToArray());

                case "curve":
                    return FraysCurve.Describe(int.TryParse(Arg(1), out var lvl) && lvl > 0 ? lvl : player.Level ?? 300);

                case "portals":
                {
                    // Testing aid: both Fray portals beside you, until the landblock unloads (their Drift Network spot is not chosen yet).
                    var made = 0;

                    foreach (var (wcid, dx) in new[] { (2000075u, 2f) })
                    {
                        if (ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject(wcid) is not ACE.Server.WorldObjects.WorldObject portal)
                            continue;

                        // Staff testing aid: no quest lock (the Watcher of the Deep flag) on this one.
                        portal.RemoveProperty(ACE.Entity.Enum.Properties.PropertyString.QuestRestriction);
                        portal.RemoveProperty(ACE.Entity.Enum.Properties.PropertyString.Quest);
                        var pos = new ACE.Entity.Position(player.Location);
                        pos.PositionX += dx;
                        portal.Location = pos;
                        portal.FraysTransient = true;

                        if (portal.EnterWorld())
                            made++;
                        else
                            portal.Destroy();
                    }

                    return $"{made} Ember Isles portal placed beside you (not saved; it leads to the Drift copy of Vissidal, realm 1).";
                }

                case "rating":
                {
                    if (!int.TryParse(Arg(1), out var n) || n < 0)
                        return "Use: /frays admin rating <n> (sets your own Heat rating, for testing).";

                    var rec = FraysManager.Record(player);
                    rec.Rating = n;
                    FraysManager.SaveRecord(player, rec);
                    return $"Your Heat rating is now {n}.";
                }

                default:
                    return FraysAdmin.Usage;
            }
        }
    }
}
