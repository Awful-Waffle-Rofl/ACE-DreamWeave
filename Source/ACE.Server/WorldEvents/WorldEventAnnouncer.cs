using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Player-facing text for a run (TECH-DESIGN 2.6, PLAN 2.10). The line composers are pure string
    /// builders, one per message kind, all unit-testable (D6). The delivery methods are the only
    /// non-pure part: each is try/catch-wrapped so a broadcast failure can never take the event down with
    /// it.
    /// </summary>
    public static class WorldEventAnnouncer
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The prefix every GLOBAL announcement carries (WP-18 item 4). Server-wide event text has to read
        /// as a server event rather than as ambient flavour, and a fixed bracketed tag is what makes it
        /// scannable in a chat window that is also carrying combat spam. Applied exactly once, by
        /// <see cref="Global"/>, so no composer can double it.
        ///
        /// The per-wave LOCAL line deliberately does NOT carry it - that one IS ambient flavour.
        /// </summary>
        public const string AnnouncePrefix = "[World Event] ";

        /// <summary>
        /// The ChatMessageType global announcements fall back to when the tunable is unset or invalid:
        /// 0x05 System, RGB(255,126,255) PINK/MAGENTA.
        ///
        /// Chosen against the client-verified palette in ACE.Entity/Enum/ChatMessageType.cs:15-58, and
        /// picked by the repo owner in-game after seeing it beside the orange it replaces (0x12 Allegiance,
        /// WP-18). The original 0x14 WorldBroadcast is RGB(127,255,126) pale green - byte-identical to
        /// Appraisal (0x10), Recall (0x17), Craft (0x18) and Salvaging (0x19), which is why the
        /// announcements "blended in with the other battle text". Magenta is unique in the palette: nothing
        /// else renders RGB(255,126,255), and it collides with nothing that appears during a fight -
        /// Combat/CombatEnemy (0x06/0x15) red, CombatSelf (0x16) salmon, Magic/Spellcasting (0x07/0x11)
        /// bright blue.
        /// </summary>
        public const ChatMessageType DefaultAnnounceChatType = ChatMessageType.System;

        /// <summary>Set once the tunable has been reported bad, so a wrong value warns once rather than per announcement.</summary>
        private static bool badChatTypeLogged;

        // ---- pure composers -------------------------------------------------------------------------

        /// <summary>
        /// Wraps one global announcement body in <see cref="AnnouncePrefix"/> (WP-18 item 4, D6 - pure).
        /// Every global composer returns through here, which is what guarantees exactly one prefix.
        /// </summary>
        public static string Global(string body)
        {
            return AnnouncePrefix + (body ?? "");
        }

        /// <summary>
        /// The ChatMessageType to announce with (WP-18 item 4, D6 - pure): the configured value when it is a
        /// defined ChatMessageType member, otherwise <see cref="DefaultAnnounceChatType"/>. An undefined
        /// value is refused rather than cast blindly, because the client picks its colour from the type byte
        /// and an unknown one can render as nothing at all.
        /// </summary>
        public static ChatMessageType ResolveAnnounceChatType(long configured, out bool valid)
        {
            valid = configured >= 0 && configured <= uint.MaxValue
                    && Enum.IsDefined(typeof(ChatMessageType), (ChatMessageType)(uint)configured);

            return valid ? (ChatMessageType)(uint)configured : DefaultAnnounceChatType;
        }

        /// <summary>
        /// The theme's StartFlavour with {anchor} replaced, plus a "(family displayName)" tag.
        ///
        /// Takes the display NAME rather than a FamilyDef (two-family composition, 2026-08-29): a run can
        /// now be composed of two families, and what an announcement wants is the one joined string
        /// (WorldEventComposition.FamilyDisplayName, "the Blackwing and the Drudges"), not a def it would
        /// have to pick a winner from. Null or blank still drops the tag entirely.
        /// </summary>
        public static string StartLine(SourceThemeDef theme, string familyDisplayName, string anchorName)
        {
            var flavour = theme?.StartFlavour ?? "";

            if (!string.IsNullOrEmpty(flavour))
                flavour = flavour.Replace("{anchor}", anchorName ?? "somewhere");

            if (string.IsNullOrWhiteSpace(familyDisplayName))
                return Global(flavour);

            return Global(string.IsNullOrEmpty(flavour) ? $"({familyDisplayName})" : $"{flavour} ({familyDisplayName})");
        }

        /// <summary>
        /// The generic teaser flavour pool (2026-09-03, TECH-DESIGN teaser phase), drawn from uniformly at
        /// random when a theme's <see cref="SourceThemeDef.TeaserFlavour"/> is null/blank. Deliberately
        /// ambient rather than mechanical - a player should read one of these as "something's up", not as a
        /// countdown.
        /// </summary>
        public static readonly IReadOnlyList<string> TeaserFlavours = new[]
        {
            "A low rumble can be felt coming from {anchor}.",
            "The ground will not settle near {anchor}.",
            "Something is stirring at {anchor}, and it is not the wind.",
            "Word is spreading of strange lights over {anchor}.",
            "The air has turned wrong around {anchor}. Travellers are turning back.",
            "Birds have scattered from {anchor} and will not return.",
            "Thunder rolls from the direction of {anchor}, and the sky is clear.",
            "The stones underfoot are humming near {anchor}.",
        };

        /// <summary>
        /// The pre-event teaser broadcast (D6 - pure), fired <see cref="WorldEvent.TeaserLeadSeconds"/>
        /// seconds before <see cref="WorldEvent.Stage"/>. Flavour source: <paramref name="theme"/>'s own
        /// TeaserFlavour when non-blank, otherwise one line drawn uniformly at random from
        /// <see cref="TeaserFlavours"/>. {anchor} substitutes <paramref name="anchorName"/>, falling back to
        /// "somewhere" like <see cref="StartLine"/>. A <see cref="TeaserTimeHint"/> is appended after a
        /// space. Never throws; the caller (<see cref="WorldEvent.Begin"/>) still wraps the broadcast so a
        /// failure here can never abort the run.
        /// </summary>
        public static string TeaserLine(SourceThemeDef theme, string anchorName, int leadSeconds, Random rng)
        {
            var flavour = theme?.TeaserFlavour;

            if (string.IsNullOrWhiteSpace(flavour))
            {
                rng = rng ?? new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));
                flavour = TeaserFlavours[rng.Next(TeaserFlavours.Count)];
            }

            flavour = flavour.Replace("{anchor}", anchorName ?? "somewhere");

            return Global($"{flavour} {TeaserTimeHint(leadSeconds)}");
        }

        /// <summary>
        /// The ambient time-remaining clause appended to <see cref="TeaserLine"/> (D6 - pure): >=90s rounds
        /// to whole minutes, [45, 90) reads as "about a minute", and anything under 45s (including 0 and
        /// negative) reads as "moments away" rather than a precise countdown.
        /// </summary>
        public static string TeaserTimeHint(int leadSeconds)
        {
            if (leadSeconds >= 90)
                return $"Roughly {(int)Math.Round(leadSeconds / 60.0)} minutes out.";

            if (leadSeconds >= 45)
                return "Roughly a minute out.";

            return "Moments away.";
        }

        /// <summary>
        /// Asheron's Protection (WaffleACE) start line - Asheron speaking, not event telemetry, so this is
        /// an EXACT fixed string, never routed through <see cref="Global"/> and never carrying
        /// <see cref="AnnouncePrefix"/>. Broadcast once, immediately after the ordinary start announcement,
        /// gated on world_event_death_protection being on for this run.
        /// </summary>
        public const string DeathProtectionStartLine =
            "Asheron says, \"Hear me, people of Dereth. A threat gathers that no lone hand can turn aside; " +
            "band together and meet it as one. I have bent my arts to hold death's toll at bay for all who " +
            "stand upon that ground, so fight without fear.\"";

        /// <summary>
        /// Asheron's Protection (WaffleACE) end line - see <see cref="DeathProtectionStartLine"/>. Broadcast
        /// only when the start line was actually sent for this run (WorldEvent latches that), so toggling the
        /// property mid-run can never produce an end line with no matching start line.
        /// </summary>
        public const string DeathProtectionEndLine =
            "Asheron says, \"It is done. My arts are spent, and death reclaims its due; guard yourselves as before.\"";

        /// <summary>
        /// The goal name an announcement uses (2026-08-19, D6 - pure): <paramref name="sourceOverride"/> when
        /// it is non-blank, otherwise the goal's own DisplayName. This function does not itself know about
        /// DestroySource scoping - the CALLER decides whether to pass an override at all (see
        /// <see cref="WorldEventComposition.GoalDisplayName"/>, which only surfaces the source's override for
        /// a DestroySource goal). One goal id can therefore read as "Shatter the Pillars" on an element
        /// portal under destroy_source and "Defend Against the Waves" on the same portal under kill_count,
        /// with no second goal id behind it. Every announcement that names the goal goes through here.
        /// </summary>
        public static string GoalName(GoalDef goal, string sourceOverride)
        {
            return !string.IsNullOrWhiteSpace(sourceOverride) ? sourceOverride : goal?.DisplayName;
        }

        /// <summary>
        /// Substitutes the outcome-line tokens (2026-08-19, D6 - pure) into a successTemplate/failTemplate:
        /// {goal}, {anchor}, {boss} (falls back to "the champion"), {noun}/{nounPlural} (fall back to the
        /// shipped rift/rifts defaults), and {reason} (fail only - left untouched on a null reason, so a
        /// success template that happens to contain the literal text "{reason}" is not corrupted). Any other
        /// brace token is left literal - there is no unknown-token diagnostic at announce time.
        /// </summary>
        private static string SubstituteTokens(string template, string goal, string anchor, string boss,
            string noun, string nounPlural, string reason)
        {
            var result = template ?? "";

            result = result.Replace("{goal}", goal ?? "");
            result = result.Replace("{anchor}", anchor ?? "");
            result = result.Replace("{boss}", string.IsNullOrWhiteSpace(boss) ? "the champion" : boss);
            result = result.Replace("{noun}", string.IsNullOrWhiteSpace(noun) ? SourceThemeDef.DefaultObjectiveNoun : noun);
            result = result.Replace("{nounPlural}",
                string.IsNullOrWhiteSpace(nounPlural) ? SourceThemeDef.DefaultObjectiveNounPlural : nounPlural);

            if (reason != null)
                result = result.Replace("{reason}", reason);

            return result;
        }

        /// <summary>
        /// e.g. "[World Event] Kill Count at the gates of Holtburg - the Emberwrought pour in!"
        ///
        /// <paramref name="goalDisplayName"/> is the source's goalDisplayName override; null (the default,
        /// and every theme that declares none) keeps the goal's own name - see <see cref="GoalName"/>.
        ///
        /// <paramref name="familyDisplayName"/> is the joined display name of every composed family (see
        /// <see cref="StartLine"/>), so a two-family run reads "... - the Blackwing and the Drudges pour in!".
        /// </summary>
        public static string ActiveLine(SourceThemeDef theme, string familyDisplayName, GoalDef goal, string anchorName,
            string goalDisplayName = null)
        {
            return Global($"{GoalName(goal, goalDisplayName)} at {anchorName} - {familyDisplayName} pour in!");
        }

        public static string WaveLine(SourceThemeDef theme) => theme?.WaveFlavour ?? "";

        public static string WarnLine(int seconds) => Global($"{seconds} seconds remain!");

        /// <summary>
        /// The periodic Active-phase progress line (2026-08-29): "Event should continue updating global
        /// messages as it continues." <paramref name="progressText"/> is <see cref="WorldEvent.ProgressText"/>
        /// (the objective's own sentence, e.g. "12 of 40 slain." or "Vhaleth still stands."). The Wave and
        /// remain clauses are each independently optional (D6 - pure): <paramref name="wave"/> &lt;= 0 drops
        /// the Wave clause entirely (no cadence has produced a real wave yet, or the goal has none), and
        /// <paramref name="secondsRemaining"/> &lt; 0 drops the remain clause entirely (no deadline).
        /// </summary>
        public static string ProgressLine(GoalDef goal, string goalDisplayName, string anchorName, string progressText,
            int wave, int secondsRemaining)
        {
            var body = $"{GoalName(goal, goalDisplayName)} at {anchorName}: {progressText}";

            if (wave > 0)
                body += $" Wave {wave}.";

            if (secondsRemaining >= 0)
            {
                var minutes = secondsRemaining / 60;
                var seconds = secondsRemaining % 60;

                body += $" {minutes}m{seconds:D2}s remain.";
            }

            return Global(body);
        }

        /// <summary>
        /// A NAMED boss's one-time arrival line (2026-08-29), fired the instant its placement lands
        /// (<see cref="WorldEvent.ChampionTickDecision.Placed"/>). Never for a FamilyChampion or a goal with
        /// no boss at all - the caller decides that; this only composes the text. Falls back to "the
        /// champion" like every other boss-name composer in this file (see <see cref="SubstituteTokens"/>).
        /// </summary>
        public static string BossArrivedLine(string bossDisplayName, string anchorName)
        {
            var name = string.IsNullOrWhiteSpace(bossDisplayName) ? "the champion" : bossDisplayName;

            return Global($"{name} has arrived at {anchorName}!");
        }

        /// <summary>
        /// A NAMED boss's health-milestone line (2026-08-29), fired once each as its current health first
        /// drops to or below 75%, 50%, 25% of its (possibly ratcheted) max - see
        /// <see cref="WorldEvent.TickBossHealthMilestones"/> for the once-per-tier latch.
        /// </summary>
        public static string BossHealthMilestoneLine(string bossDisplayName, int pct)
        {
            var name = string.IsNullOrWhiteSpace(bossDisplayName) ? "the champion" : bossDisplayName;

            return Global($"{name} is at {pct}% health!");
        }

        /// <summary>
        /// PLAN 1.8. Success carries the MVP sentence and the kill/participant summary; every Failed*
        /// names its own reason and mentions the consolation cache; every Aborted* is a bare cancellation.
        ///
        /// <paramref name="successTemplate"/>/<paramref name="failTemplate"/> (2026-08-19) let a goal or a
        /// DestroySource-scoped source flavour the Success/Failed* body via
        /// <see cref="SubstituteTokens"/>({goal}, {anchor}, {boss}, {noun}, {nounPlural}, and - fail only -
        /// {reason}); null/blank (every call site before this change, and SuccessBossAbsent/Aborted*, which
        /// never take a template at all) keeps the fixed "{goal} at {anchor} succeeded!"/"failed - {reason}."
        /// shape. In both cases the existing suffixes - the MVP sentence and kill/participant summary on
        /// Success, the consolation-cache sentence on every Failed* - are appended unchanged.
        /// <paramref name="noun"/>/<paramref name="nounPlural"/> default to the shipped rift/rifts wording.
        /// </summary>
        public static string OutcomeLine(WorldEventOutcome outcome, GoalDef goal, string anchorName,
            WorldEventMvp mvp, ParticipantRecord topKiller, int participants, string bossName = null,
            string goalDisplayName = null, string successTemplate = null, string failTemplate = null,
            string noun = null, string nounPlural = null)
        {
            var goalName = GoalName(goal, goalDisplayName);

            switch (outcome)
            {
                case WorldEventOutcome.Success:
                {
                    var line = !string.IsNullOrWhiteSpace(successTemplate)
                        ? Global(SubstituteTokens(successTemplate, goalName, anchorName, bossName, noun, nounPlural, null))
                        : Global($"{goalName} at {anchorName} succeeded!");

                    var mvpSentence = MvpSentence(mvp);

                    if (!string.IsNullOrEmpty(mvpSentence))
                        line += " " + mvpSentence;

                    var killerName = topKiller?.Name ?? "none";
                    var killerKills = topKiller?.Kills ?? 0;

                    line += $" Most kills: {killerName} ({killerKills}). {participants} defenders took part.";

                    return line;
                }

                case WorldEventOutcome.SuccessBossAbsent:
                {
                    var killerName = topKiller?.Name ?? "none";
                    var killerKills = topKiller?.Kills ?? 0;

                    return Global($"{goalName} at {anchorName} ended: {bossName ?? "the champion"} never " +
                        $"showed and the defenders hold the field. Most kills: {killerName} ({killerKills}). " +
                        $"{participants} defenders took part.");
                }

                case WorldEventOutcome.FailedTimeout:
                    return FailedLine(goalName, anchorName, "time ran out", failTemplate, bossName, noun, nounPlural);

                case WorldEventOutcome.FailedWipe:
                    return FailedLine(goalName, anchorName, "the defenders were wiped out", failTemplate, bossName, noun, nounPlural);

                case WorldEventOutcome.FailedNoParticipants:
                    return FailedLine(goalName, anchorName, "nobody answered the call", failTemplate, bossName, noun, nounPlural);

                case WorldEventOutcome.AbortedAdmin:
                case WorldEventOutcome.AbortedShutdown:
                case WorldEventOutcome.AbortedError:
                    return Global($"The event at {anchorName} was cancelled.");

                default:
                    return Global($"The event at {anchorName} ended.");
            }
        }

        private static string FailedLine(string goalName, string anchorName, string reason, string failTemplate = null,
            string bossName = null, string noun = null, string nounPlural = null)
        {
            var body = !string.IsNullOrWhiteSpace(failTemplate)
                ? SubstituteTokens(failTemplate, goalName, anchorName, bossName, noun, nounPlural, reason)
                : $"{goalName} at {anchorName} failed - {reason}.";

            return Global($"{body} A consolation cache remains for a short while.");
        }

        /// <summary>
        /// One line from a NAMED boss's failLines pool (BOSS-STANDARD.md section 2, BOSS-LINES.md "Win"),
        /// drawn uniformly at random - the boss's own voice, gloating, sent as a second global line after
        /// the outcome line (D6 - pure).
        ///
        /// Null - meaning "say nothing" - for every case that is not a named boss with something to say:
        /// no boss, boss kind None or FamilyChampion (a roster pick has no authored voice), and an empty
        /// or all-blank pool. The CALLER decides which outcomes are allowed to speak; this only decides
        /// whether there is anything to say.
        ///
        /// Returns through <see cref="Global"/>, like every other global composer, so the announce prefix
        /// is applied exactly once.
        /// </summary>
        public static string BossFailLine(BossDef boss, Random rng)
        {
            if (boss == null || boss.Kind != BossKind.Named)
                return null;

            var pool = boss.FailLines;

            if (pool == null || pool.Count == 0)
                return null;

            var usable = new List<string>();

            foreach (var line in pool)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    usable.Add(line);
            }

            if (usable.Count == 0)
                return null;

            rng = rng ?? new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));

            return Global(usable[rng.Next(usable.Count)]);
        }

        /// <summary>
        /// [?] The exact Reason text WP-06's objectives set on WorldEventMvp is not yet built (only
        /// IWorldEventObjective.cs and the factory exist in this wave) - IWorldEventObjective.cs documents
        /// "most kills" and "killing blow" as example Reason text, and TECH-DESIGN 2.6's OutcomeLine spec
        /// separately shows "killing blow" and "most damage" sentences. This maps every reason text seen
        /// in either doc; anything else falls back to the raw Reason text so a new WP-06 rule still reads
        /// sensibly rather than silently vanishing.
        /// </summary>
        private static string MvpSentence(WorldEventMvp mvp)
        {
            if (!mvp.HasMvp)
                return null;

            var reason = mvp.Reason?.Trim().ToLowerInvariant();

            string verb;

            switch (reason)
            {
                case "killing blow":
                    verb = "struck the final blow";
                    break;

                case "most damage":
                case "top damage":
                    verb = "dealt the most damage";
                    break;

                case "most kills":
                    verb = "secured the most kills";
                    break;

                default:
                    verb = string.IsNullOrEmpty(mvp.Reason) ? "was the MVP" : mvp.Reason;
                    break;
            }

            return $"{mvp.Name} {verb}.";
        }

        // ---- delivery (thin, untested; every method is try/catch-wrapped) ---------------------------

        /// <summary>
        /// The configured announcement ChatMessageType, read fresh per call so the repo owner can retune the
        /// colour live with "/modifylong world_events_announce_chat_type" and see it on the next
        /// announcement without a rebuild or a restart. A bad value warns ONCE and then silently uses the
        /// orange default.
        /// </summary>
        private static ChatMessageType AnnounceChatType()
        {
            var configured = PropertyManager.GetLong("world_events_announce_chat_type").Item;

            var type = ResolveAnnounceChatType(configured, out var valid);

            if (!valid && !badChatTypeLogged)
            {
                badChatTypeLogged = true;

                log.Warn($"[WORLDEVENT] world_events_announce_chat_type {configured} is not a defined ChatMessageType; using {(uint)type} ({type})");
            }

            return type;
        }

        /// <summary>
        /// Test seam (2026-08-29), following <see cref="WorldEventRosterSelector.DialSource"/>'s pattern: the
        /// real send, swappable so a WorldEvent tick test can capture what would have gone out (recipients
        /// enumeration needs a live PlayerManager a unit test never has) without touching production wiring.
        /// A test must restore this in a finally block.
        /// </summary>
        internal static Action<string> BroadcastSink = SendBroadcast;

        /// <summary>
        /// Test seam for the Discord half of a relayed broadcast, same contract as
        /// <see cref="BroadcastSink"/>: swappable so a tick test can assert what would have been relayed
        /// without a live DiscordRelayManager, and a test must restore it in a finally block.
        ///
        /// It exists for a second reason too: DiscordRelayManager reads discord_relay_enabled through
        /// PropertyManager, which throws NullReferenceException in ACE.Server.Tests for an uncached key
        /// (no ambient shard config). <see cref="Relay"/> catches that, but a test that wants to assert
        /// relay behaviour swaps this rather than relying on the catch.
        /// </summary>
        internal static Action<string> DiscordRelaySink = DiscordRelayManager.QueueWorldEvent;

        /// <summary>
        /// <paramref name="relayToDiscord"/> (2026-09-05) additionally pushes the line to the Discord
        /// Events webhook. It is opt-in per call site, and only the three beats an offline player would
        /// want pushed at them take it: the teaser, the run going Active, and the outcome. Every other
        /// global - the 30s Announced line, the 60s/30s countdown warnings, the boss's parting line - is
        /// in-game atmosphere and stays out of Discord.
        ///
        /// The in-game send happens FIRST and the relay cannot affect it: the relay is queued, never
        /// posted inline, and is wrapped so a relay fault can never cost a player the announcement.
        /// </summary>
        public static void Broadcast(string msg, bool relayToDiscord = false)
        {
            if (string.IsNullOrEmpty(msg))
                return;

            BroadcastSink(msg);

            if (relayToDiscord)
                Relay(msg);
        }

        private static void Relay(string msg)
        {
            try
            {
                DiscordRelaySink(msg);
            }
            catch (Exception ex)
            {
                log.Warn("[WORLDEVENT] Discord relay of announcement failed", ex);
            }
        }

        /// <summary>
        /// Server-wide chat, matching AdminCommands.cs's @gamecast (HandleGamecast).
        ///
        /// Iterates GetAllOnline itself rather than calling PlayerManager.BroadcastToAll, for one reason:
        /// BroadcastToAll returns void, and WP-18 item 4 needs the RECIPIENT COUNT logged so a future
        /// "the announcements are missing" report can be settled from the log alone - recipients=0 is a
        /// delivery problem, recipients=N with the text present is a client-display problem. The send is
        /// otherwise identical (one GameMessageSystemChat enqueued per online session).
        /// </summary>
        private static void SendBroadcast(string msg)
        {
            try
            {
                var type = AnnounceChatType();
                var message = new GameMessageSystemChat(msg, type);

                var recipients = 0;

                foreach (var player in PlayerManager.GetAllOnline())
                {
                    if (player?.Session?.Network == null)
                        continue;

                    player.Session.Network.EnqueueSend(message);
                    recipients++;
                }

                // Logged unconditionally, unlike PlayerManager.LogBroadcastChat, which drops the text
                // entirely unless the chat_log_global property is on - which is why the last run's log had
                // no record of announcements that had in fact been sent.
                log.Info($"[WORLDEVENT] broadcast recipients={recipients} type={(uint)type} text={msg}");

                PlayerManager.LogBroadcastChat(Channel.AllBroadcast, null, msg);
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] broadcast failed", ex);
            }
        }

        /// <summary>
        /// Per-wave chatter is local (PLAN 1.10): every online player within the theme's RewardRadius of
        /// the anchor, same landblock instance. A no-op without a resolvable anchor/radius - a directly
        /// constructed WorldEvent in a unit test never reaches here because delivery is never called by a
        /// pure test. Delegates to <see cref="LocalAt"/> for the actual radius broadcast.
        /// </summary>
        public static void Local(WorldEvent evt, string msg)
        {
            if (evt == null || string.IsNullOrEmpty(msg))
                return;

            try
            {
                var anchor = evt.Composition?.AnchorPosition;
                var radius = evt.Composition?.Source?.RewardRadius ?? 0f;

                LocalAt(anchor, radius, msg, $"run={evt.RunId} ");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} local announce failed", ex);
            }
        }

        /// <summary>
        /// General-purpose local/radius broadcast (2026-09-18, ML digsite feedback item E): every online
        /// player within <paramref name="radius"/> metres of <paramref name="anchor"/>, same landblock
        /// instance, same as <see cref="Local"/>'s WorldEvent-scoped radius send but callable from anything
        /// that has a Position and a radius rather than a live WorldEvent run - the digsite start cue does
        /// not start a real WorldEvent (TreasureMapHandler/MlDigsiteManager never construct one). A no-op
        /// without a resolvable anchor/radius/message.
        /// </summary>
        public static void LocalAt(Position anchor, float radius, string msg, string logTag = "")
        {
            if (anchor == null || radius <= 0 || string.IsNullOrEmpty(msg))
                return;

            try
            {
                var type = AnnounceChatType();
                var recipients = 0;

                foreach (var player in PlayerManager.GetAllOnline())
                {
                    var loc = player?.Location;

                    if (loc == null || loc.Instance != anchor.Instance)
                        continue;

                    if (loc.DistanceTo(anchor) > radius)
                        continue;

                    player.SendMessage(msg, type);
                    recipients++;
                }

                log.Info($"[WORLDEVENT] {logTag}local recipients={recipients} radius={radius:F1} type={(uint)type} text={msg}");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] {logTag}local announce failed", ex);
            }
        }

        /// <summary>Matches PlayerManager.BroadcastToAuditChannel's own signature; kept public for WP-07's commands.</summary>
        public static void Audit(Player issuer, string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;

            try
            {
                PlayerManager.BroadcastToAuditChannel(issuer, msg);
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] audit broadcast failed", ex);
            }
        }
    }
}
