using System;
using System.Reflection;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.MlTreasure;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Player-facing preference and admin probe commands for the Aun Relaria repeat-kill aura
    /// (Player_RelariaAura.cs, MlRelariaTrophy.TryAwardRepeatKillRewards), plus the /relariaspawn
    /// admin test-spawn command, which creates a dig-marked Aun Relaria for testing the kill/trophy
    /// path without a real treasure-map dig.
    /// </summary>
    public static class MlRelariaCommands
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// wcid 1004120, Content/sql/weenies/1004120 Aun Relaria the Unburied.sql - the same boss the
        /// dig-map spawn (MlRelariaSpawner.TrySpawn) creates. Hardcoded rather than taking a parameter:
        /// this command exists to test the Relaria kill/trophy path specifically, not to spawn arbitrary
        /// creatures - /create already covers that.
        /// </summary>
        public const uint RelariaWcid = 1004120;

        /// <summary>
        /// Per-character toggle for the Relaria repeat-kill aura's visual pulse. Does not affect whether
        /// the aura was earned - only whether it is currently shown.
        /// </summary>
        [CommandHandler("relaria", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Toggle the Aun Relaria repeat-kill aura's visual effect.", "aura on | off")]
        public static void HandleRelaria(Session session, params string[] parameters)
        {
            var player = session.Player;
            if (player == null)
                return;

            void Msg(string text) => session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            if (parameters.Length == 0 || !string.Equals(parameters[0], "aura", StringComparison.OrdinalIgnoreCase))
            {
                Msg("Usage: /relaria aura on | off");
                return;
            }

            if (!player.HasRelariaAura)
            {
                Msg("The Unburied's light has not touched you yet.");
                return;
            }

            if (parameters.Length < 2)
            {
                Msg($"Your Relaria aura is currently {(player.RelariaAuraEnabled ? "on" : "off")}. Use /relaria aura {(player.RelariaAuraEnabled ? "off" : "on")} to turn it {(player.RelariaAuraEnabled ? "off" : "on")}.");
                if (player.RelariaAuraEnabled && !PropertyManager.GetBool("ml_relaria_pulse_effects_enabled").Item)
                    Msg("(The aura pulse effect is currently disabled server-wide.)");
                return;
            }

            switch (parameters[1].ToLowerInvariant())
            {
                case "on":
                    player.RelariaAuraEnabled = true;
                    player.ArmRelariaAuraPulseIfEligible();
                    Msg("Your Relaria aura is now on.");
                    if (!PropertyManager.GetBool("ml_relaria_pulse_effects_enabled").Item)
                        Msg("(The aura pulse effect is currently disabled server-wide.)");
                    break;

                case "off":
                    player.RelariaAuraEnabled = false;
                    player.ArmRelariaAuraPulseIfEligible();
                    Msg("Your Relaria aura is now off.");
                    break;

                default:
                    Msg("Usage: /relaria aura on | off");
                    break;
            }
        }

        /// <summary>
        /// Admin probe: applies a PlayScript to the caller so the owner can compare Aun/light-themed
        /// candidates live before committing one as ml_relaria_aura_script. Two modes:
        /// - "pulse" (default): a single one-shot GameMessageScript, exactly what the shipping aura
        ///   pulse plays (Player_RelariaAura.FireRelariaAuraPulse) - safe to spam, nothing persists.
        /// - "default": sets PropertyDataId.DefaultScriptId and forces a full visual resend
        ///   (EnqueueBroadcastUpdateObject), so the owner can see how the client's own looping/idle
        ///   script table (PhysicsObj.PhysicsScriptTable) handles it, if at all - unverified whether
        ///   any candidate here actually loops through that path, which is exactly what this mode is
        ///   for checking.
        /// </summary>
        [CommandHandler("relariaprobe", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 1,
            "Applies a PlayScript to yourself to compare Aun/light-themed aura candidates live.",
            "<scriptId> [default|pulse]")]
        public static void HandleRelariaProbe(Session session, params string[] parameters)
        {
            var player = session.Player;
            if (player == null)
                return;

            void Msg(string text) => session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            if (!uint.TryParse(parameters[0], out var scriptId))
            {
                Msg("Could not parse the script id. Usage: /relariaprobe <scriptId> [default|pulse]");
                return;
            }

            if (!Enum.IsDefined(typeof(PlayScript), (PlayScript)scriptId))
            {
                Msg($"{scriptId} is not a defined PlayScript member.");
                return;
            }

            var script = (PlayScript)scriptId;
            var mode = parameters.Length > 1 ? parameters[1].ToLowerInvariant() : "pulse";

            switch (mode)
            {
                case "default":
                    player.DefaultScriptId = scriptId;
                    player.EnqueueBroadcastUpdateObject();
                    Msg($"Set DefaultScriptId to {scriptId} ({script}) and forced a visual resend.");
                    break;

                case "pulse":
                    player.ApplyVisualEffects(script);
                    Msg($"Played one pulse of {scriptId} ({script}).");
                    break;

                default:
                    Msg("Usage: /relariaprobe <scriptId> [default|pulse]");
                    break;
            }
        }

        /// <summary>
        /// Admin test-spawn: creates a dig-marked Aun Relaria the Unburied (wcid 1004120) in front of the
        /// caller, stamped with the exact same pre-EnterWorld setup the real dig path applies
        /// (MlRelariaSpawner.PrepareBoss) - so this is a live test of the kill/trophy path, not a
        /// lookalike. Before this command, staff testing with /create 1004120 got an unmarked creature:
        /// MlRelariaTrophy.TryDropTrophy reads the same marker off the dying creature and refuses to drop
        /// a trophy at 0, so no trophy ever dropped. (MlRelariaChargeTrophy's own drop side was removed
        /// entirely by owner ruling 2026-09-24 - see MlRelariaChargeTrophy.cs.)
        ///
        /// Deliberately skips three checks MlRelariaSpawner.CanSpawn makes for the real dig path, because
        /// they gate the DIG mechanic, not the kill/trophy mechanic this command is for testing:
        ///   - ml_treasure_enabled (the feature master switch) - a tester turning the feature off to test
        ///     something else should not also lose the ability to spawn a test boss;
        ///   - siteIsBossOk (the dig-site catalogue match) - there is no dig site here at all, so there is
        ///     nothing to match against;
        ///   - the town-exclusion near-metres margin (TownExclusionMarginMetres) - that margin exists only
        ///     to keep a DUG-UP boss out of a town plaza; an admin explicitly summoning a test boss is not
        ///     subject to it.
        /// It does NOT skip the indoors check some might expect from CanSpawn: indoors is refused there
        /// only because the dig's terrain snap (Position.AdjustMapCoords) needs an outdoor cell to snap
        /// to, and this command follows /create's own placement instead (InFrontOf, no terrain snap), so
        /// indoors is fine here.
        ///
        /// The one check this command does NOT skip is the Marae Lassel realm/landblock gate itself
        /// (MlTreasureLandblock.IsMaraeLassel) - Creature_Death.cs:842 wraps the entire trophy-drop call in
        /// that same gate, so a Relaria killed off Marae Lassel would never reach MlRelariaTrophy at all
        /// regardless of the marker. Spawning one elsewhere would be a dead end dressed up as a working
        /// test, so this command refuses outright with a message naming the requirement. The check runs on
        /// the BOSS's own placed Location, not the caller's - InFrontOf(5f) can push the spawn point
        /// across a landblock boundary near the edge of the ML box, so the player's landblock is not a
        /// reliable stand-in for where the boss (and later its corpse) actually ends up - and the refusal
        /// destroys the not-yet-EnterWorld'd boss so nothing is left half-created.
        ///
        /// The tether MlRelariaSpawner.PrepareBoss stamps (when ml_treasure_boss_tether_radius applies)
        /// anchors from wherever the creature's Location already points when EnterWorld runs
        /// (WorldObject.AddPhysicsObj, WorldObject.cs:246) - not from any dig-site coordinate - so setting
        /// Location to the caller's spawn position before calling PrepareBoss, exactly as done below,
        /// anchors the tether there with no separate write.
        /// </summary>
        [CommandHandler("relariaspawn", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 0,
            "Spawns a dig-marked Aun Relaria the Unburied for testing the kill/trophy path.")]
        public static void HandleRelariaSpawn(Session session, params string[] parameters)
        {
            var player = session.Player;
            if (player == null)
                return;

            void Msg(string text) => session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            var wo = WorldObjectFactory.CreateNewWorldObject(RelariaWcid);

            if (wo == null)
            {
                Msg($"Could not create wcid {RelariaWcid}. Is Content/sql/weenies/1004120 Aun Relaria the Unburied.sql applied to this world database?");
                log.Error($"[ML_TREASURE] /relariaspawn: wcid {RelariaWcid} failed to create for {player.Name}");
                return;
            }

            if (!(wo is Creature boss))
            {
                Msg($"wcid {RelariaWcid} is a {wo.WeenieType}, not a Creature; nothing spawned.");
                wo.Destroy();
                return;
            }

            // Same placement /create uses for a Creature (AdminCommands.CreateObjectForCommand): in front
            // of the caller, facing them.
            boss.Location = player.Location.InFrontOf(5f, true);
            boss.Location.LandblockId = new LandblockId(boss.Location.GetCell());

            // The one gate this command does not skip: MlRelariaTrophy.TryDropTrophy is only ever reached
            // from inside this same IsMaraeLassel check at the Creature_Death.cs:842 call site, so a
            // Relaria whose CORPSE lands off Marae Lassel could be fought to death and would still never
            // drop a trophy - refuse before EnterWorld rather than let a tester discover that the hard
            // way. Checked on the BOSS's own placed Location, not the player's: InFrontOf(5f) can push the
            // spawn point across a landblock boundary near the edge of the ML box, so the player's own
            // landblock is not a reliable stand-in for where the boss (and later its corpse) actually
            // ends up. Nothing has been created/entered yet at this point except the not-yet-EnterWorld'd
            // boss itself, which is destroyed on refusal so nothing is left half-created.
            var landblock = boss.Location.LandblockId.Landblock;
            var realm = boss.Location.RealmID;

            if (!MlTreasureLandblock.IsMaraeLassel(landblock, realm))
            {
                Msg("The spawn point must be on Marae Lassel (realm 1) - the trophy drop only fires from a kill on that realm/landblock gate. Move away from the edge of the island and try again.");
                boss.Destroy();
                return;
            }

            MlRelariaSpawner.PrepareBoss(boss, (int)RelariaWcid);

            if (!boss.EnterWorld())
            {
                Msg($"wcid {RelariaWcid} failed to enter the world.");
                boss.Destroy();
                return;
            }

            try
            {
                boss.EmoteManager.OnGeneration();
            }
            catch (Exception ex)
            {
                log.Error($"[ML_TREASURE] /relariaspawn intro emote threw for wcid {RelariaWcid} (0x{boss.Guid.Full:X8})", ex);
            }

            Msg("Spawned a dig-marked Aun Relaria the Unburied. Trophies drop from this kill; the Tally of the Unburied gains a charge per completed ML treasure map, not per kill.");

            PlayerManager.BroadcastToAuditChannel(player, $"{player.Name} has created a test Aun Relaria the Unburied (0x{boss.Guid.Full:X8}) at {boss.Location.ToLOCString()}.");

            log.Info($"[ML_TREASURE] {player.Name} used /relariaspawn to create Relaria (0x{boss.Guid.Full:X8}) at {boss.Location.ToLOCString()}");
        }
    }
}
