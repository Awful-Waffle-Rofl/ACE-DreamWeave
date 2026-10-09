using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Self-service progression grants for alpha testers on the STAGE shard.
    ///
    /// These are ordinary <see cref="AccessLevel.Player"/> commands - every player on the shard can use
    /// them - so the whole feature hangs on <see cref="SelfGrantsEnabled"/> being false everywhere except
    /// stage. Deliberately NOT solved by elevating tester accounts: AccessLevel is a strict linear ladder,
    /// and the equivalent dev commands (@grantxp, @addluminance, @grantabilitypoints) sit at Developer,
    /// which would also hand out @import-sql, @createinst, @teleallto and ~200 others.
    ///
    /// Every command here targets the calling player only. None of them take a player-name parameter,
    /// so a tester can never modify another character.
    /// </summary>
    public static class StageTestCommands
    {
        /// <summary>
        /// A single grant is clamped to this much XP - the cumulative total that takes a fresh character
        /// all the way to the level ceiling (<see cref="EnlightenmentXpCurve.HardCeilingLevel"/>, level
        /// 1445 / 4,590,249,062,099,211,814 XP, per Docs/ClassAbilities/XP-LANE-SPEC.md sec 2.2). It used
        /// to stop at 191,226,310,247 - the RETAIL cap of level 275 - which left a tester ~1170 levels
        /// short of the max this fork actually supports.
        ///
        /// Read off the synthesized chart rather than hard-coded, so it tracks the ceiling if the curve
        /// changes; the chart's own overflow guard keeps every total under long.MaxValue / 2, so this
        /// always fits a long. Not a const for that reason - it needs the loaded portal.dat.
        ///
        /// Player.UpdateXpAndLevel clamps the grant to the XP actually remaining below the ceiling, so
        /// asking for the full amount at any level simply tops the character out rather than banking
        /// a surplus.
        /// </summary>
        private static long MaxXpPerGrant =>
            (long)EnlightenmentXpCurve.GetTotalXPRequiredForLevel(EnlightenmentXpCurve.HardCeilingLevel);

        /// <summary>
        /// How many class ability points one /mylum grant is sized to buy from scratch. Luminance's only
        /// real sink is the CAP purchase curve, which is geometric with no cap of its own ("the only limit
        /// is the price", ClassAbilityLumCurve), so a flat round number stops meaning anything a few points
        /// up the curve. 40 is chosen to sit two full decades past the second breakpoint (20), deep in the
        /// steep r3 tail, which is exactly the region that was previously untestable.
        /// </summary>
        private const int LuminanceGrantCoversPoints = 40;

        /// <summary>
        /// A single /mylum grant is clamped to the cumulative Luminance cost of the first
        /// <see cref="LuminanceGrantCoversPoints"/> class ability points, read off the LIVE curve tunables,
        /// so it re-sizes itself when the curve is retuned rather than going stale.
        ///
        /// At the design defaults that is 72,751,548,490,990. The old cap was a flat 1,000,000,000, which
        /// did not even cover the first TWENTY points (1,299,815,225 cumulative) - the 41st point alone
        /// costs 54,563,438,373,361 - so testing anything on the steep part of the curve meant grinding
        /// the command dozens of times.
        ///
        /// Still four orders of magnitude clear of long.MaxValue, and the bank saturates rather than
        /// wrapping (Player_Bank.SaturatingAdd), so repeated grants cannot invert a balance.
        ///
        /// Floored at the old value so a server that tunes the curve DOWN never ends up with a smaller cap
        /// than it had before.
        /// </summary>
        private static long MaxLuminancePerGrant =>
            Math.Max(1_000_000_000, Player.LumCostForClassAbilityPoints(0, LuminanceGrantCoversPoints));

        private const int MaxAbilityPointsPerGrant = 1_000;

        /// <summary>
        /// wcid 20630, classname tradenote250000, "Trade Note (250,000)" - the stage-only currency item.
        /// </summary>
        private const uint MmdWcid = 20630;

        /// <summary>
        /// 250 is the weenie's own MaxStackSize - one invocation is at most one stack in one pack slot.
        /// </summary>
        private const int MaxMmdPerGrant = 250;

        /// <summary>
        /// wcid 43901, classname ace43901-promissorynote, "Promissory Note" - the Absalom Sarraf currency,
        /// same wcid the banking system uses (Player_Bank.PromissoryNoteWcid).
        /// </summary>
        private const uint PromissoryNoteWcid = 43901;

        /// <summary>
        /// 1000 is this weenie's own MaxStackSize, so the same one-stack-per-grant rule as MMDs applies.
        /// </summary>
        private const int MaxPromissoryNotesPerGrant = 1000;

        /// <summary>
        /// wcid 46423, classname ace46423-stipend, "Stipend" - the Society stipend currency, spent at a
        /// stipend vendor (Marid, wcid 46425). Attuned and bonded on the weenie, so a granted stack stays
        /// on the character that asked for it.
        /// </summary>
        private const uint StipendWcid = 46423;

        /// <summary>
        /// 1000 is this weenie's own MaxStackSize, so the same one-stack-per-grant rule as MMDs applies.
        /// </summary>
        private const int MaxStipendsPerGrant = 1000;

        /// <summary>
        /// wcid 29295, classname gemaugmentationblank, "Blank Augmentation Gem" - handed to an Augmentation
        /// Trainer (Donatello Linante 34259, Fiun Rehlyun 28698, both placed in the world) in exchange for an
        /// inscribed augmentation gem of the tester's choice.
        /// </summary>
        private const uint BlankAugGemWcid = 29295;

        /// <summary>
        /// The blank gem has no MaxStackSize, so every gem costs its own pack slot. Ten per grant is a
        /// deliberately low cap for that reason - augment testing needs a handful, not a pack.
        /// </summary>
        private const int MaxAugGemsPerGrant = 10;

        /// <summary>
        /// True when the self-grant commands are live. One switch: the shard config bool, toggled at
        /// runtime with "modifybool stage_self_grants_enabled true" and defaulting to false in
        /// PropertyManager. It is per-environment for free, because stage and prod run the same image
        /// against their own MySQL container and their own ace_shard (deploy/stage vs deploy/prod).
        ///
        /// KNOWN GAP, accepted deliberately: this bool lives in ace_shard, so restoring a stage shard
        /// dump onto a live server carries it along and arms these commands for every player there.
        /// Earlier revisions paired it with a second condition sourced from Config.js (first the world
        /// name, then a dedicated flag) precisely because no database dump travels with that file.
        /// If a stage-to-prod database restore ever becomes a real procedure, this needs a second
        /// condition again.
        /// </summary>
        public static bool SelfGrantsEnabled =>
            PropertyManager.GetBool("stage_self_grants_enabled").Item;

        [CommandHandler("myxp", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Grants yourself experience. Stage test shard only.",
            "<amount | max>\n" +
            "\"max\" grants enough to reach the level ceiling in one command; the grant is clamped to the XP you actually still need.")]
        public static void HandleMyXp(Session session, params string[] parameters)
        {
            if (!Available(session))
                return;

            if (!TryParseAmount(session, parameters[0], MaxXpPerGrant, out var amount))
                return;

            // ShareType.None so a self-grant never leaks into a fellowship split and skews what the
            // rest of the group is nominally earning.
            session.Player.GrantXP(amount, XpType.Admin, ShareType.None);

            session.Network.EnqueueSend(new GameMessageSystemChat($"{amount:N0} experience granted.", ChatMessageType.Advancement));

            PlayerManager.BroadcastToAuditChannel(session.Player, $"[STAGE] {session.Player.Name} self-granted {amount:N0} experience.");
        }

        [CommandHandler("mylum", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Grants yourself banked Luminance. Stage test shard only.",
            "<amount>")]
        public static void HandleMyLuminance(Session session, params string[] parameters)
        {
            if (!Available(session))
                return;

            if (!TryParseAmount(session, parameters[0], MaxLuminancePerGrant, out var amount))
                return;

            // Banked, not available. Available Luminance is capped by MaximumLuminance and does nothing
            // at all on a character that never unlocked Luminance; banked is uncapped and is what the
            // class ability token NPCs actually charge against.
            var balance = session.Player.AddBankedLuminance(amount);

            session.Network.EnqueueSend(new GameMessageSystemChat(
                $"{amount:N0} banked Luminance granted (bank balance: {balance:N0}). Spend it at a Class Trainer, or check it with /bank.", ChatMessageType.Advancement));

            PlayerManager.BroadcastToAuditChannel(session.Player, $"[STAGE] {session.Player.Name} self-granted {amount:N0} banked Luminance.");
        }

        [CommandHandler("myabilitypoints", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Grants yourself class ability points. Stage test shard only.",
            "<amount>")]
        public static void HandleMyAbilityPoints(Session session, params string[] parameters)
        {
            if (!Available(session))
                return;

            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("Class abilities are not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            if (!TryParseAmount(session, parameters[0], MaxAbilityPointsPerGrant, out var amount))
                return;

            // Messages the player and saves their biota itself. GrantAdmin rather than GrantItem: this
            // is a self-grant from a developer command on the stage test shard, which is the same kind
            // of event as /grantabilitypoints, and the sourceText lands in the ledger row's `detail` so
            // the two are still distinguishable at a MySQL prompt.
            session.Player.GrantClassAbilityPoints((int)amount, "a stage test grant", CapLedgerReason.GrantAdmin);

            PlayerManager.BroadcastToAuditChannel(session.Player, $"[STAGE] {session.Player.Name} self-granted {amount:N0} class ability points.");
        }

        [CommandHandler("mymmd", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Grants yourself Trade Notes (MMDs). Stage test shard only.",
            "<count>")]
        public static void HandleMyMmd(Session session, params string[] parameters)
        {
            GrantStack(session, parameters[0], MmdWcid, MaxMmdPerGrant, "MMD");
        }

        [CommandHandler("mypromnotes", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Grants yourself Promissory Notes. Stage test shard only.",
            "<count>")]
        public static void HandleMyPromissoryNotes(Session session, params string[] parameters)
        {
            GrantStack(session, parameters[0], PromissoryNoteWcid, MaxPromissoryNotesPerGrant, "Promissory Note");
        }

        [CommandHandler("mystipend", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Grants yourself Stipends. Stage test shard only.",
            "<count>")]
        public static void HandleMyStipend(Session session, params string[] parameters)
        {
            GrantStack(session, parameters[0], StipendWcid, MaxStipendsPerGrant, "Stipend");
        }

        [CommandHandler("myauggem", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Grants yourself Blank Augmentation Gems. Stage test shard only.",
            "[count]")]
        public static void HandleMyAugGem(Session session, params string[] parameters)
        {
            var countArg = parameters.Length > 0 ? parameters[0] : "1";

            GrantSingles(session, countArg, BlankAugGemWcid, MaxAugGemsPerGrant, "Blank Augmentation Gem");
        }

        /// <summary>
        /// Creates several separate copies of a NON-stackable weenie, one per pack slot. GrantStack cannot be
        /// used for these: SetStackSize on a weenie with no MaxStackSize does not produce a stack, so the
        /// count has to be spent on distinct objects instead.
        ///
        /// The free-slot check covers the whole grant up front, so the command either delivers every copy or
        /// creates nothing - a partial grant that silently stops halfway is the outcome worth avoiding.
        /// </summary>
        private static void GrantSingles(Session session, string countArg, uint wcid, int maxPerGrant, string label)
        {
            if (!Available(session))
                return;

            if (!TryParseAmount(session, countArg, maxPerGrant, out var count))
                return;

            if (session.Player.GetFreeInventorySlots() < count)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"You need {count} free pack slot(s) for that many. Free up some space and try again.", ChatMessageType.Broadcast));
                return;
            }

            var granted = 0;

            for (var i = 0; i < count; i++)
            {
                var item = WorldObjectFactory.CreateNewWorldObject(wcid);

                if (item == null)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"Failed to create the {label} - weenie {wcid} couldn't be found.", ChatMessageType.Broadcast));
                    break;
                }

                if (!session.Player.TryCreateInInventoryWithNetworking(item))
                {
                    item.Destroy();
                    session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"Failed to add the {label} to your inventory.", ChatMessageType.Broadcast));
                    break;
                }

                granted++;
            }

            if (granted == 0)
                return;

            session.Network.EnqueueSend(new GameMessageSystemChat($"{granted:N0} {label}(s) granted.", ChatMessageType.Advancement));

            PlayerManager.BroadcastToAuditChannel(session.Player, $"[STAGE] {session.Player.Name} self-granted {granted:N0} {label}(s).");
        }

        /// <summary>
        /// Creates one stack of a currency weenie in the player's pack. The per-grant cap is always the
        /// weenie's own MaxStackSize, so a single invocation is at most one stack in one pack slot and can
        /// never partially fail across several slots - repeat the command for more.
        /// </summary>
        private static void GrantStack(Session session, string countArg, uint wcid, int maxPerGrant, string label)
        {
            if (!Available(session))
                return;

            if (!TryParseAmount(session, countArg, maxPerGrant, out var count))
                return;

            // Checked before creating anything, so a full pack never leaves an orphaned object behind.
            if (session.Player.GetFreeInventorySlots() < 1)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("You don't have a free pack slot. Free up some space and try again.", ChatMessageType.Broadcast));
                return;
            }

            var stack = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (stack == null)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"Failed to create the {label} stack - weenie {wcid} couldn't be found.", ChatMessageType.Broadcast));
                return;
            }

            stack.SetStackSize((int)count);

            if (!session.Player.TryCreateInInventoryWithNetworking(stack))
            {
                stack.Destroy();
                session.Network.EnqueueSend(new GameMessageSystemChat($"Failed to add the {label} stack to your inventory.", ChatMessageType.Broadcast));
                return;
            }

            session.Network.EnqueueSend(new GameMessageSystemChat($"{count:N0} {label} granted.", ChatMessageType.Advancement));

            PlayerManager.BroadcastToAuditChannel(session.Player, $"[STAGE] {session.Player.Name} self-granted {count:N0} {label}.");
        }

        [CommandHandler("myrespec", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Untrains all of your retail skills, refunding skill credits and spent skill XP as unassigned experience. Stage test shard only.",
            "[confirm] [gear|skills]")]
        public static void HandleMyRespec(Session session, params string[] parameters)
        {
            if (!Available(session))
                return;

            var player = session.Player;

            var confirmed = parameters.Length > 0 && string.Equals(parameters[0], "confirm", StringComparison.OrdinalIgnoreCase);

            if (!confirmed)
            {
                RespecDryRun(session, player);
                return;
            }

            // Optional half-runs. A full respec does two independent things - it empties every equipment slot,
            // and it rewrites every skill - and sometimes you only want one: strip a character without losing
            // its build, or rebuild skills without disturbing worn gear. These also isolate the two halves if
            // either ever needs debugging on its own.
            var doGear = true;
            var doSkills = true;

            if (parameters.Length > 1)
            {
                if (string.Equals(parameters[1], "gear", StringComparison.OrdinalIgnoreCase))
                    doSkills = false;
                else if (string.Equals(parameters[1], "skills", StringComparison.OrdinalIgnoreCase))
                    doGear = false;
                else
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat(
                        "Second argument must be 'gear' or 'skills', or omitted to do both.", ChatMessageType.Broadcast));
                    return;
                }
            }

            RespecConfirm(session, player, doGear, doSkills);
        }

        private static void RespecDryRun(Session session, Player player)
        {
            var specializedCount = 0;
            var trainedCount = 0;
            var xpOnlyCount = 0;
            var estimatedCredits = 0;

            // This must predict exactly what RespecConfirm below will do, so every branch here mirrors one
            // there. Two of them refund NO credits at all, and counting those would overstate the estimate:
            // an AlwaysTrained skill keeps its rank (only its xp comes back), and an augmentation
            // specialization was never paid for with the specialization upgrade cost in the first place.
            foreach (var kvp in player.Skills)
            {
                if (!DatManager.PortalDat.SkillTable.SkillBaseHash.TryGetValue((uint)kvp.Key, out var skillBase))
                    continue;

                var untrainable = Player.IsSkillUntrainable(kvp.Key);

                if (kvp.Value.AdvancementClass == SkillAdvancementClass.Specialized)
                {
                    var specializedViaAugmentation = player.IsSkillSpecializedViaAugmentation(kvp.Key, out var hasAugmentation) && hasAugmentation;

                    if (!specializedViaAugmentation)
                    {
                        // A specialized skill is taken all the way down in TWO steps, and RespecConfirm counts
                        // it in both of its buckets, so this does too. The upgrade cost always comes back;
                        // the trained cost only does if the second step can actually untrain the skill, which
                        // an AlwaysTrained skill specialized with credits (Arcane Lore) cannot - it stops at
                        // Trained and recovers its xp only.
                        specializedCount++;
                        estimatedCredits += skillBase.UpgradeCostFromTrainedToSpecialized;

                        if (untrainable)
                        {
                            trainedCount++;
                            estimatedCredits += skillBase.TrainedCost;
                        }
                        else
                        {
                            xpOnlyCount++;
                        }
                    }
                    else if (untrainable)
                    {
                        // untrained outright, refunding only what was actually spent: the trained cost
                        trainedCount++;
                        estimatedCredits += skillBase.TrainedCost;
                    }
                    else
                    {
                        xpOnlyCount++;      // Salvaging: augmentation-specialized AND AlwaysTrained
                    }
                }
                else if (kvp.Value.AdvancementClass == SkillAdvancementClass.Trained)
                {
                    if (untrainable)
                    {
                        trainedCount++;
                        estimatedCredits += skillBase.TrainedCost;
                    }
                    else
                    {
                        xpOnlyCount++;
                    }
                }
            }

            var xpOnlyClause = xpOnlyCount > 0
                ? $", and recover the spent xp from {xpOnlyCount} skill(s) that cannot be lowered"
                : "";

            session.Network.EnqueueSend(new GameMessageSystemChat(
                $"This is a dry run - nothing has changed. Confirming would: unspecialize {specializedCount} skill(s), untrain {trainedCount} skill(s){xpOnlyClause}, " +
                $"refunding an estimated {estimatedCredits:N0} skill credits; refund all spent skill XP as unassigned experience; and move your " +
                $"{player.EquippedObjects.Count} currently-equipped item(s) to your pack. Type /myrespec confirm to proceed.", ChatMessageType.Broadcast));
        }

        private static void RespecConfirm(Session session, Player player, bool doGear, bool doSkills)
        {
            var equipped = doGear ? player.EquippedObjects.Values.ToList() : new List<WorldObject>();

            var freeSlots = player.GetFreeInventorySlots();

            if (freeSlots < equipped.Count)
            {
                var needed = equipped.Count - freeSlots;
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"You need {needed} more free pack slot(s) to hold your equipped items before respeccing. Free up space and try again.", ChatMessageType.Broadcast));
                return;
            }

            // 1. Unequip everything first, moving each item to the pack.
            //
            // This goes through HandleActionPutItemInContainer - the same entry point the client uses when a
            // player drags equipped gear into their pack. It routes to DoHandleActionPutItemInContainer, which
            // sees itemWasEquipped and does the dequip itself before placing the item.
            //
            // Do NOT "simplify" this to TryDequipObjectWithNetworking followed by TryCreateInInventoryWithNetworking.
            // That pair looks right (dequip does not place the item anywhere, so a create seems needed) but the
            // create sends GameMessageCreateObject for an object the client ALREADY has in its table, since the
            // item was equipped and visible. Issuing a create for a guid the client already holds is simply wrong,
            // whatever it does to any particular client build.
            var movesRequested = equipped.Count;

            foreach (var item in equipped)
                player.HandleActionPutItemInContainer(item.Guid.Full, player.Guid.Full);

            // 2. Untrain each skill, replicating SkillAlterationDevice.AlterSkill's Gem of Forgetfulness sequence.
            var skillsToProcess = doSkills ? player.Skills.Keys.ToList() : new List<Skill>();

            var unspecializedCount = 0;
            var untrainedCount = 0;
            var alwaysTrainedNames = new List<string>();
            var augSpecNames = new List<string>();

            foreach (var skill in skillsToProcess)
            {
                if (!DatManager.PortalDat.SkillTable.SkillBaseHash.TryGetValue((uint)skill, out var skillBase))
                    continue; // retired/unimplemented skill - not in the skill table

                var creatureSkill = player.GetCreatureSkill(skill);

                if (creatureSkill.AdvancementClass == SkillAdvancementClass.Specialized)
                {
                    var specializedViaAugmentation = player.IsSkillSpecializedViaAugmentation(skill, out var playerHasAugmentation) && playerHasAugmentation;

                    if (specializedViaAugmentation)
                    {
                        // mirrors the gem: an augmentation specialization has no Trained rung to step down to,
                        // so untrain outright and refund the trained cost - the only credits ever spent on it.
                        if (player.UntrainSkill(skill, skillBase.TrainedCost, true))
                        {
                            session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(player, creatureSkill));

                            if (Player.IsSkillUntrainable(skill))
                                untrainedCount++;
                            else if (!augSpecNames.Contains(skill.ToSentence()))
                                augSpecNames.Add(skill.ToSentence());   // Salvaging: aug-specialized AND AlwaysTrained
                        }
                    }
                    else if (player.UnspecializeSkill(skill, skillBase.UpgradeCostFromTrainedToSpecialized))
                    {
                        session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(player, creatureSkill));

                        unspecializedCount++;
                    }
                }

                // re-read: unspecializing above may have dropped this skill to Trained
                creatureSkill = player.GetCreatureSkill(skill);

                if (creatureSkill.AdvancementClass == SkillAdvancementClass.Trained)
                {
                    var untrainable = Player.IsSkillUntrainable(skill);

                    if (player.UntrainSkill(skill, skillBase.TrainedCost))
                    {
                        session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(player, creatureSkill));

                        if (untrainable)
                            untrainedCount++;
                        else if (!alwaysTrainedNames.Contains(skill.ToSentence()))
                            alwaysTrainedNames.Add(skill.ToSentence());
                    }
                }
            }

            if (doSkills)
                session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.AvailableSkillCredits, player.AvailableSkillCredits ?? 0));

            player.SaveBiotaToDatabase();

            var scope = doGear && doSkills ? "Respec complete" : doGear ? "Respec (gear half only)" : "Respec (skills half only)";

            // movesRequested, not a completed count: HandleActionPutItemInContainer queues the move as a player
            // action rather than completing inline, so there is no honest success count to report here.
            var summary = $"{scope}: {unspecializedCount} skill(s) unspecialized, {untrainedCount} skill(s) untrained, " +
                          $"{player.AvailableSkillCredits ?? 0:N0} skill credits now available, {movesRequested} item(s) sent to your pack.";

            if (alwaysTrainedNames.Count > 0)
                summary += $" Always-trained skills stay Trained (spent XP refunded only): {string.Join(", ", alwaysTrainedNames)}.";

            if (augSpecNames.Count > 0)
                summary += $" Always-trained skills specialized via augmentation stay Specialized (spent XP refunded only): {string.Join(", ", augSpecNames)}.";


            session.Network.EnqueueSend(new GameMessageSystemChat(summary, ChatMessageType.Advancement));

            PlayerManager.BroadcastToAuditChannel(player, $"[STAGE] {player.Name} self-respecced: {unspecializedCount} unspecialized, {untrainedCount} untrained, {movesRequested} item(s) sent to pack.");
        }

        /// <summary>
        /// The login banner listing what this shard hands out. Returns null when the feature is off, so
        /// the production login path sends nothing extra.
        /// </summary>
        public static string GetLoginMessage()
        {
            if (!SelfGrantsEnabled)
                return null;

            return "This is the DreamWeave TEST shard. You can grant yourself progression here:\n" +
                   "  /myxp <amount|max>         - experience; \"max\" takes you to the level ceiling\n" +
                   "  /mylum <amount>            - banked Luminance, spendable at Class Trainers\n" +
                   "  /myabilitypoints <amount>  - class ability points, spend with /abilities\n" +
                   "  /mymmd <count>             - Trade Notes (MMDs), up to a full stack per grant\n" +
                   "  /mypromnotes <count>       - Promissory Notes, up to a full stack per grant\n" +
                   "  /mystipend <count>         - Stipends, spendable at a stipend vendor\n" +
                   "  /myauggem [count]          - Blank Augmentation Gems, for an Augmentation Trainer\n" +
                   "  /myrespec [confirm]        - untrain all skills, refunding credits and XP\n" +
                   "These commands affect only your own character, and exist only on this shard.\n";
        }

        private static bool Available(Session session)
        {
            // PvP template (progression lock): a self-grant would land on a templated character and be duplicated by the restore.
            if (session?.Player != null && session.Player.PvpTemplateRefuses(ACE.Server.Pvp.Templates.PvpTemplateAction.Experience))
                return false;

            if (SelfGrantsEnabled)
                return true;

            session.Network.EnqueueSend(new GameMessageSystemChat("That command is not enabled on this server.", ChatMessageType.Broadcast));
            return false;
        }

        /// <summary>
        /// "max" is accepted in place of a number and means the whole per-grant cap. It exists for
        /// /myxp, whose cap is the cumulative total to the level ceiling - a 19-digit number nobody is
        /// going to type correctly - but it costs nothing to honour on the other grants too.
        /// </summary>
        private static bool TryParseAmount(Session session, string input, long max, out long amount)
        {
            if (input.Equals("max", StringComparison.OrdinalIgnoreCase))
            {
                amount = max;
                return true;
            }

            if (!long.TryParse(input, out amount) || amount < 1)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"Amount must be a positive number (or \"max\"), up to {max:N0}.", ChatMessageType.Broadcast));
                return false;
            }

            amount = Math.Min(amount, max);
            return true;
        }
    }
}
