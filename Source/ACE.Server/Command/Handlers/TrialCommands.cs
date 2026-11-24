using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.BalanceTrials;
using ACE.Server.Network;

namespace ACE.Server.Command.Handlers
{
    /// <summary>The manual driver for Balance Trials on stage. The parsing and the status line live in TrialCommandParser.</summary>
    public static class TrialCommands
    {
        [CommandHandler("trial", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Drives a Balance Trial: start, next, status, abort, rebuild, promote, unpromote, list.",
            "start <scenario> <archetype> | next | status | abort | rebuild <archetype> <1-8> | promote <archetype> <1-8> | unpromote <archetype> | list\n" +
            "start opens the private room and teleports you to it. next runs the next rep (on the room's thread). status prints one line. abort ends the trial. " +
            "rebuild re-copies a rep from its archetype master (run it from a character that is not the rep; the copy finishes asynchronously). " +
            "promote copies a logged-out, hand-tuned rep over its archetype's master and stamps it promoted, so rebuilds copy the tuned build and the reseed skips it. " +
            "unpromote purges a promoted master so the next reseed fills the slot again. list names the scenarios, rooms and archetypes in the data dir.")]
        public static void HandleTrial(Session session, params string[] parameters)
        {
            var (verb, rest, error) = TrialCommandParser.Parse(parameters);

            if (error != null)
            {
                Say(session, error);
                return;
            }

            switch (verb)
            {
                case "start":
                    if (TrialStarter.TryStart(session.Player, rest[0], rest[1], out var startRefusal))
                        Say(session, $"trial started: scenario {rest[0]}, archetype {rest[1]}");
                    else
                        Say(session, $"trial refused: {startRefusal}");
                    break;

                case "next":
                    Next(session);
                    break;

                case "status":
                    Status(session);
                    break;

                case "abort":
                    Abort(session);
                    break;

                case "rebuild":
                    Rebuild(session, rest[0], int.Parse(rest[1], System.Globalization.CultureInfo.InvariantCulture));
                    break;

                case "promote":
                    foreach (var line in PromoteReply(session, rest[0], int.Parse(rest[1], System.Globalization.CultureInfo.InvariantCulture)))
                        Say(session, line);
                    break;

                case "unpromote":
                    Say(session, UnpromoteReply(rest[0]));
                    break;

                case "list":
                    List(session);
                    break;
            }
        }

        private static void Say(Session session, string text) => CommandHandlerHelper.WriteOutputInfo(session, text, ChatMessageType.Broadcast);

        private static bool TryGetSession(Session session, out TrialSession trial)
        {
            trial = null;

            if (session?.Player == null || !TrialManager.TryGetByPlayer(session.Player.Guid.Full, out trial))
            {
                Say(session, "you are not in a trial");
                return false;
            }

            return true;
        }

        private static void Next(Session session)
        {
            if (!TryGetSession(session, out var trial))
                return;

            // TryNext mutates the session and spawns: it runs on the room landblock's thread, never here.
            var queued = TrialManager.Enqueue(trial, () =>
            {
                if (trial.TryNext(TrialManager.Now(), out var refusal))
                    Say(session, $"rep {trial.RepsDone + 1}/{trial.Reps} started");
                else
                    Say(session, $"next refused: {refusal}");
            });

            if (!queued)
                Say(session, "the trial room is not available");
        }

        private static void Abort(Session session)
        {
            if (!TryGetSession(session, out var trial))
                return;

            var queued = TrialManager.Enqueue(trial, () =>
            {
                trial.Abort("aborted by @trial abort", TrialManager.Now());
                Say(session, $"trial {trial.TrialUuid} aborted");
            });

            if (!queued)
                Say(session, "the trial room is not available");
        }

        private static void Status(Session session)
        {
            if (!TryGetSession(session, out var trial))
                return;

            int trash = 0, elite = 0, boss = 0;

            if (trial.World is LiveTrialWorld live)
                (trash, elite, boss) = live.AliveByRole();

            var ready = trial.Phase == TrialPhase.Ready && !trial.RebuildRequired;

            Say(session, TrialCommandParser.FormatStatus(trial.TrialUuid, trial.Phase, trial.RepsDone, trial.Reps, trash, elite, boss,
                trial.LastEndState, ready, trial.RebuildRequired));
        }

        private static void Rebuild(Session session, string archetypeId, int repIndex) => Say(session, RebuildReply(session, archetypeId, repIndex));

        /// <summary>
        /// The rebuild verb, returning its one reply line. The kill-switch is checked first, as TrialStarter.TryStart checks
        /// it: with balance_trials_enabled off, nothing else is read and nothing is copied.
        /// </summary>
        internal static string RebuildReply(Session session, string archetypeId, int repIndex)
        {
            var off = TrialStarter.DisabledRefusal();

            if (off != null)
                return $"rebuild refused: {off}";

            var dataDir = TrialStarter.ResolveDataDir(out var reason);

            if (dataDir == null)
                return $"rebuild refused: {reason}";

            try
            {
                var archetypes = TrialArchetypeLoader.LoadDirectory(Path.Combine(dataDir, TrialStarter.ArchetypesFolder));
                var a = archetypes.FirstOrDefault(x => string.Equals(x.Archetype.Id, archetypeId, StringComparison.Ordinal));

                if (a.Archetype == null)
                    return $"rebuild refused: unknown archetype '{archetypeId}'";

                if (TrialRebuild.TryRebuild(session, a.Archetype, a.Sha256, repIndex, out var refusal, out var note))
                    return $"rebuild of rep {repIndex} from {archetypeId} queued; wait for the 'Successfully copied' line before using the rep" + (note == null ? "" : "; " + note);

                return $"rebuild refused: {refusal}";
            }
            catch (TrialArchetypeException ex)
            {
                return $"rebuild refused: trial data: {ex.Message}";
            }
        }

        /// <summary>
        /// The promote verb, returning its reply lines (a refusal is one line). Refusals 1 to 3 are the command's, in this
        /// order: the kill switch, the data directory, an unknown archetype. TrialPromote.TryPromote makes the rest.
        /// </summary>
        internal static IReadOnlyList<string> PromoteReply(Session session, string archetypeId, int repIndex)
        {
            if (!TryLoadArchetype("promote", archetypeId, out var archetype, out var refusal))
                return new[] { refusal };

            if (TrialPromote.TryPromote(session, archetype, repIndex, out refusal, out var reply))
                return reply;

            return new[] { $"promote refused: {refusal}" };
        }

        /// <summary>The unpromote verb, returning its one reply line. The same command head as promote, then TrialPromote.TryUnpromote.</summary>
        internal static string UnpromoteReply(string archetypeId)
        {
            if (!TryLoadArchetype("unpromote", archetypeId, out var archetype, out var refusal))
                return refusal;

            if (TrialPromote.TryUnpromote(archetype, out refusal, out var reply))
                return reply;

            return $"unpromote refused: {refusal}";
        }

        /// <summary>The kill switch, then the data directory, then the archetype by id. On false, <paramref name="refusal"/> is the whole reply line.</summary>
        private static bool TryLoadArchetype(string verb, string archetypeId, out TrialArchetype archetype, out string refusal)
        {
            archetype = null;

            var off = TrialStarter.DisabledRefusal();

            if (off != null)
            {
                refusal = $"{verb} refused: {off}";
                return false;
            }

            var dataDir = TrialStarter.ResolveDataDir(out var reason);

            if (dataDir == null)
            {
                refusal = $"{verb} refused: {reason}";
                return false;
            }

            try
            {
                var archetypes = TrialArchetypeLoader.LoadDirectory(Path.Combine(dataDir, TrialStarter.ArchetypesFolder));
                var a = archetypes.FirstOrDefault(x => string.Equals(x.Archetype.Id, archetypeId, StringComparison.Ordinal));

                if (a.Archetype == null)
                {
                    refusal = $"{verb} refused: unknown archetype '{archetypeId}'";
                    return false;
                }

                archetype = a.Archetype;
                refusal = null;
                return true;
            }
            catch (TrialArchetypeException ex)
            {
                refusal = $"{verb} refused: trial data: {ex.Message}";
                return false;
            }
        }

        private static void List(Session session)
        {
            var dataDir = TrialStarter.ResolveDataDir(out var reason);

            if (dataDir == null)
            {
                Say(session, $"list refused: {reason}");
                return;
            }

            try
            {
                var scenarios = TrialScenarioLoader.LoadDirectory(Path.Combine(dataDir, TrialStarter.ScenariosFolder));
                var rooms = TrialRoomLoader.LoadDirectory(Path.Combine(dataDir, TrialStarter.RoomsFolder));
                var archetypes = TrialArchetypeLoader.LoadDirectory(Path.Combine(dataDir, TrialStarter.ArchetypesFolder));

                Say(session, "scenarios: " + (scenarios.Count == 0 ? "-" : string.Join(", ", scenarios.Select(s => $"{s.Scenario.Id} (room {s.Scenario.Room})"))));
                Say(session, "rooms: " + (rooms.Count == 0 ? "-" : string.Join(", ", rooms.Select(r => r.Room.Id))));
                Say(session, "archetypes: " + (archetypes.Count == 0 ? "-" : string.Join(", ", archetypes.Select(a => $"{a.Archetype.Id} (slot {a.Archetype.Slot})"))));
            }
            catch (TrialArchetypeException ex)
            {
                Say(session, $"list refused: trial data: {ex.Message}");
            }
        }
    }
}
