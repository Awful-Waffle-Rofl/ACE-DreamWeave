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
            "Drives a Balance Trial: start, next, status, abort, rebuild, promote, unpromote, adopt, list, campaign.",
            "start <scenario> <archetype> [for <minutes> campaign <id> [driver <label>]] | next | status | abort | rebuild <archetype> <1-12> | promote <archetype> <1-12> | unpromote <archetype> | unpromote slot:<n> | adopt <id> <Class> <character name...> | list | campaign status|stop|report <id>\n" +
            "start opens the private room and teleports you to it. next runs the next rep (on the room's thread). status prints one line. abort ends the trial. " +
            "start ... for <minutes> campaign <id> joins (or creates) a timed campaign: reps run on their own until the deadline, then the reps are logged out; campaign status, stop and report read or end it. " +
            "rebuild re-copies a rep from its archetype master (run it from a character that is not the rep; the copy finishes asynchronously). " +
            "promote copies a logged-out, hand-tuned rep over its archetype's master and stamps it promoted, so rebuilds copy the tuned build and the reseed skips it. " +
            "unpromote purges a promoted master so the next reseed fills the slot again (for a curated archetype it removes the archetype and frees its slot); slot:<n> names the master slot directly, the recovery when two archetypes collide. " +
            "adopt copies a logged-out, hand-built character onto a free master slot as curated archetype <id> of <Class>, at the character's own level (the name may hold spaces; run it from another character). " +
            "list names the scenarios, rooms and archetypes, seeded and curated, and any collision.")]
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
                    if (rest.Length != 2)
                    {
                        // The campaign form (the parser has checked it): TrialCampaigns makes the reply.
                        TrialCommandParser.TryParseCampaignStart(rest, out var campaignStart, out _);
                        Say(session, TrialCampaigns.Join(session.Player.Guid.Full, rest[0], rest[1], campaignStart.Minutes, campaignStart.CampaignId, campaignStart.Driver));
                        break;
                    }

                    if (TrialStarter.TryStart(session.Player, rest[0], rest[1], out var startRefusal))
                        Say(session, $"trial started: scenario {rest[0]}, archetype {rest[1]}");
                    else
                        Say(session, $"trial refused: {startRefusal}");
                    break;

                case "campaign":
                    if (rest[0] == "stop")
                        Say(session, TrialCampaigns.Stop(rest[1]));
                    else
                        foreach (var line in TrialCampaigns.Describe(rest[1], report: rest[0] == "report"))
                            Say(session, line);
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

                case "adopt":
                    foreach (var line in AdoptReply(session, rest[0], rest[1], AdoptName(rest)))
                        Say(session, line);
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

            // The rotation's next family, from the same walk index the next rep rolls with (TrialRepSetup.SeedRep).
            var next = TrialRotation.NextLabel(trial.Scenario.Rotation, TrialRotation.NextRepIndex(TrialRepSetup.PeekSeedRep(trial.PlayerGuid, trial.ScenarioSha, trial.Archetype?.Id), trial.Phase));

            Say(session, TrialCommandParser.FormatStatus(trial.TrialUuid, trial.Phase, trial.RepsDone, trial.Reps, trash, elite, boss,
                trial.LastEndState, ready, trial.RebuildRequired, next));
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
                var a = TrialArchetypeResolver.Resolve(Path.Combine(dataDir, TrialStarter.ArchetypesFolder), archetypeId);

                if (a == null)
                    return $"rebuild refused: unknown archetype '{archetypeId}'";

                if (TrialRebuild.TryRebuild(session, a.Archetype, a.Sha, repIndex, out var refusal, out var note))
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

            if (TrialPromote.TryPromote(session, archetype.Archetype, repIndex, out refusal, out var reply))
                return reply;

            return new[] { $"promote refused: {refusal}" };
        }

        /// <summary>The unpromote verb, returning its one reply line. The same command head as promote, then TrialPromote.TryUnpromote.</summary>
        internal static string UnpromoteReply(string archetypeId)
        {
            // slot:<n> addresses the master directly, never through the resolver: it is the recovery when resolving an id refuses.
            if (archetypeId != null && archetypeId.StartsWith(TrialArchetypeResolver.SlotPrefix, StringComparison.Ordinal))
                return UnpromoteSlotReply(archetypeId);

            if (!TryLoadArchetype("unpromote", archetypeId, out var archetype, out var refusal))
                return refusal;

            if (TrialPromote.TryUnpromote(archetype.Archetype, out refusal, out var reply))
                return CuratedUnpromoteReply(archetype) ?? reply;

            return $"unpromote refused: {refusal}";
        }

        /// <summary>@trial unpromote slot:&lt;n&gt;: the kill switch, the data directory, then TrialPromote.TryUnpromoteSlot.</summary>
        private static string UnpromoteSlotReply(string argument)
        {
            var off = TrialStarter.DisabledRefusal();

            if (off != null)
                return $"unpromote refused: {off}";

            var dataDir = TrialStarter.ResolveDataDir(out var reason);
            if (dataDir == null)
                return $"unpromote refused: {reason}";

            if (!TrialArchetypeResolver.TryParseSlotArgument(argument, out var slot))
                return $"unpromote refused: '{argument}' is not a master slot ({TrialArchetypeResolver.SlotPrefix}0..{TrialArchetypeResolver.SlotPrefix}{TrialRoster.MasterCount - 1})";

            var lookups = TrialPromote.Live();

            // The seeded files' slots, so a leftover character row on a seeded slot is never cleared here (the reseed owns it).
            // Unreadable files read as unknown (null), which refuses that clear.
            lookups.SeededSlots = () =>
            {
                try
                {
                    return TrialArchetypeResolver.SeededSlots(Path.Combine(dataDir, TrialStarter.ArchetypesFolder));
                }
                catch (TrialArchetypeException)
                {
                    return null;
                }
            };

            if (TrialPromote.TryUnpromoteSlot(slot, lookups, out var refusal, out var reply))
                return reply;

            return $"unpromote refused: {refusal}";
        }

        /// <summary>The unpromote reply for a curated master, whose purge removes the archetype itself (it has no file to reseed from); null for a seeded one.</summary>
        internal static string CuratedUnpromoteReply(TrialResolvedArchetype archetype)
            => archetype?.Curated == true ? $"curated archetype {archetype.Archetype.Id} removed; slot {archetype.Archetype.Slot} is free" : null;

        /// <summary>The kill switch, then the data directory, then the archetype by id (seeded files, then curated masters). On false, <paramref name="refusal"/> is the whole reply line.</summary>
        private static bool TryLoadArchetype(string verb, string archetypeId, out TrialResolvedArchetype archetype, out string refusal)
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
                var a = TrialArchetypeResolver.Resolve(Path.Combine(dataDir, TrialStarter.ArchetypesFolder), archetypeId);

                if (a == null)
                {
                    refusal = $"{verb} refused: unknown archetype '{archetypeId}'";
                    return false;
                }

                archetype = a;
                refusal = null;
                return true;
            }
            catch (TrialArchetypeException ex)
            {
                refusal = $"{verb} refused: trial data: {ex.Message}";
                return false;
            }
        }

        /// <summary>The character name of an adopt: every argument after the id and the class, joined with single spaces.</summary>
        internal static string AdoptName(string[] rest) => string.Join(" ", rest.Skip(2));

        /// <summary>
        /// The adopt verb, returning its reply lines (a refusal is one line). The kill switch, then the data directory, are the
        /// command's (as for promote); TrialAdopt.TryAdopt makes the rest. Any throw before the copy is handed off refuses.
        /// </summary>
        internal static IReadOnlyList<string> AdoptReply(Session session, string id, string className, string name)
        {
            var off = TrialStarter.DisabledRefusal();

            if (off != null)
                return new[] { $"adopt refused: {off}" };

            var dataDir = TrialStarter.ResolveDataDir(out var reason);

            if (dataDir == null)
                return new[] { $"adopt refused: {reason}" };

            try
            {
                if (TrialAdopt.TryAdopt(session, Path.Combine(dataDir, TrialStarter.ArchetypesFolder), id, className, name, out var refusal, out var reply))
                    return reply;

                return new[] { $"adopt refused: {refusal}" };
            }
            catch (Exception ex)
            {
                return new[] { $"adopt refused: {ex.GetType().Name} ({ex.Message}); nothing was purged, check the server log" };
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
                var listing = TrialArchetypeResolver.All(Path.Combine(dataDir, TrialStarter.ArchetypesFolder));

                Say(session, "scenarios: " + (scenarios.Count == 0 ? "-" : string.Join(", ", scenarios.Select(s => $"{s.Scenario.Id} (room {s.Scenario.Room})"))));
                Say(session, "rooms: " + (rooms.Count == 0 ? "-" : string.Join(", ", rooms.Select(r => r.Room.Id))));
                Say(session, "archetypes: " + (listing.Archetypes.Count == 0 ? "-" : string.Join(", ", listing.Archetypes.Select(TrialArchetypeResolver.Describe))));

                foreach (var collision in listing.Collisions)
                    Say(session, "COLLISION: " + collision.Message);
            }
            catch (TrialArchetypeException ex)
            {
                Say(session, $"list refused: trial data: {ex.Message}");
            }
        }
    }
}
