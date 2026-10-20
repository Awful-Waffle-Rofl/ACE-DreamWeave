using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;

using ACE.Entity;
using ACE.Server.Entity;
using LScape = ACE.Server.Physics.Common.LScape;
using LandCell = ACE.Server.Physics.Common.LandCell;
using LandDefs = ACE.Server.Physics.Common.LandDefs;
using Position = ACE.Entity.Position;
using ACE.Server.WorldEvents.Defs;

using log4net;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// goals.json "chain": when a run of the goal succeeds, a second World Event starts at a different spot nearby, with a
    /// random compatible source, family and goal, after a countdown broadcast with its location. Only a success chains;
    /// a failed or aborted stage ends the chain, and it never runs longer than <see cref="MaxLength"/> stages.
    /// </summary>
    public class ChainDef
    {
        /// <summary>The most stages in one chain, the first included (2 to 5). Default 2.</summary>
        [JsonPropertyName("maxLength")]
        public int MaxLengthJson { get; set; } = 2;

        /// <summary>In force: its Hold setting (HoldKeys.ChainMaxLength) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public int MaxLength { get => HoldSettings.Integer(HoldKeys.ChainMaxLength, MaxLengthJson); set => MaxLengthJson = value; }

        /// <summary>Seconds from the broadcast to the next stage's start (60 to 600). Default 120.</summary>
        [JsonPropertyName("countdownSeconds")]
        public int CountdownSecondsJson { get; set; } = 120;

        /// <summary>In force: its Hold setting (HoldKeys.ChainCountdownSeconds) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public int CountdownSeconds { get => HoldSettings.Integer(HoldKeys.ChainCountdownSeconds, CountdownSecondsJson); set => CountdownSecondsJson = value; }

        /// <summary>The next stage is at least this far from the last one's centre, in metres (100 to 2000). Default 250.</summary>
        [JsonPropertyName("minDistance")]
        public double MinDistance { get; set; } = 250;

        /// <summary>And at most this far, in metres (above minDistance, up to 3000). Default 500.</summary>
        [JsonPropertyName("maxDistance")]
        public double MaxDistance { get; set; } = 500;

        /// <summary>
        /// The World Event broadcast, one picked at random: tokens {family}, {Family} (the next stage's family), {location}
        /// (nearest town and map coordinates) and {seconds}. Without lines: "{Family} regroup near {location}! They strike
        /// again in {seconds} seconds."
        /// </summary>
        [JsonPropertyName("announce")]
        public List<string> Announce { get; set; } = new List<string>();

        /// <summary>
        /// The next stage goes to a different town of the Town Network (WorldEventTownIndex), never the one the last stage
        /// was nearest: one of the <see cref="TownChoices"/> nearest towns that lie between townMinDistance and
        /// townMaxDistance from the last stage. True by default; false keeps the old "250 to 500 m away" spot.
        /// </summary>
        [JsonPropertyName("toAnotherTown")]
        public bool ToAnotherTownJson { get; set; } = true;

        /// <summary>In force: its Hold setting (HoldKeys.ChainTownNetwork) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public bool ToAnotherTown { get => HoldSettings.Toggle(HoldKeys.ChainTownNetwork, ToAnotherTownJson); set => ToAnotherTownJson = value; }

        /// <summary>A town counts as different only this far (metres) from the last stage, so one town's two portals are never two towns (200 to 5000). Default 800.</summary>
        [JsonPropertyName("townMinDistance")]
        public double TownMinDistanceJson { get; set; } = 800;

        /// <summary>In force: its Hold setting (HoldKeys.ChainTownMinDistance) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double TownMinDistance { get => HoldSettings.Number(HoldKeys.ChainTownMinDistance, TownMinDistanceJson); set => TownMinDistanceJson = value; }

        /// <summary>The farthest town to send players to, in metres (above townMinDistance, up to 30000). Default 8000.</summary>
        [JsonPropertyName("townMaxDistance")]
        public double TownMaxDistanceJson { get; set; } = 8000;

        /// <summary>In force: its Hold setting (HoldKeys.ChainTownMaxDistance) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double TownMaxDistance { get => HoldSettings.Number(HoldKeys.ChainTownMaxDistance, TownMaxDistanceJson); set => TownMaxDistanceJson = value; }

        /// <summary>How many of the nearest eligible towns the pick is made from (1 to 10). Default 4.</summary>
        [JsonPropertyName("townChoices")]
        public int TownChoicesJson { get; set; } = 4;

        /// <summary>In force: its Hold setting (HoldKeys.ChainTownChoices) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public int TownChoices { get => HoldSettings.Integer(HoldKeys.ChainTownChoices, TownChoicesJson); set => TownChoicesJson = value; }

        /// <summary>The event stands this far from the town's portal drop, in metres: on open ground at its edge, not among its buildings (20 to 400). Default 60.</summary>
        [JsonPropertyName("townSpotMinDistance")]
        public double TownSpotMinDistanceJson { get; set; } = 60;

        /// <summary>In force: its Hold setting (HoldKeys.ChainSpotMin) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double TownSpotMinDistance { get => HoldSettings.Number(HoldKeys.ChainSpotMin, TownSpotMinDistanceJson); set => TownSpotMinDistanceJson = value; }

        /// <summary>And at most this far (above townSpotMinDistance, up to 600). Default 160.</summary>
        [JsonPropertyName("townSpotMaxDistance")]
        public double TownSpotMaxDistanceJson { get; set; } = 160;

        /// <summary>In force: its Hold setting (HoldKeys.ChainSpotMax) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double TownSpotMaxDistance { get => HoldSettings.Number(HoldKeys.ChainSpotMax, TownSpotMaxDistanceJson); set => TownSpotMaxDistanceJson = value; }

        /// <summary>
        /// The reward profile (rewards.json id) each stage pays, first stage first; a stage past the end of the list uses
        /// the last entry. Each stage pays its own cache to the players who fought in it. Empty: every stage pays the
        /// default profile. The envoys: "standard" for stage 1, "chain_final" for stage 2.
        /// </summary>
        [JsonPropertyName("stageRewards")]
        public List<string> StageRewards { get; set; } = new List<string>();

        /// <summary>When each line of a stage change and of the chain's end goes out (seconds after the outcome line).</summary>
        [JsonPropertyName("pacing")]
        public ChainPacingDef Pacing { get; set; } = new ChainPacingDef();

        /// <summary>The story lines that link the stages (see <see cref="ChainStoryDef"/>).</summary>
        [JsonPropertyName("story")]
        public ChainStoryDef Story { get; set; } = new ChainStoryDef();

        /// <summary>The value that rolls an axis at random, as before 2026-10-19.</summary>
        public const string RandomAxis = "random";

        public const string DefaultNextSource = "sky_rift";
        public const string DefaultNextGoal = "kill_boss";
        public const string DefaultNextBoss = "auto";

        /// <summary>
        /// The source every later stage uses (sources.json id), or "random". Default "sky_rift": the second stage is a
        /// rift that drops a champion.
        /// </summary>
        [JsonPropertyName("nextSource")]
        public string NextSourceJson { get; set; } = DefaultNextSource;

        /// <summary>In force: its Hold setting (HoldKeys.ChainNextSource) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public string NextSource { get => HoldSettings.Text(HoldKeys.ChainNextSource, NextSourceJson); set => NextSourceJson = value; }

        /// <summary>The goal every later stage uses (goals.json id), or "random". Default "kill_boss": slay the champion.</summary>
        [JsonPropertyName("nextGoal")]
        public string NextGoalJson { get; set; } = DefaultNextGoal;

        /// <summary>In force: its Hold setting (HoldKeys.ChainNextGoal) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public string NextGoal { get => HoldSettings.Text(HoldKeys.ChainNextGoal, NextGoalJson); set => NextGoalJson = value; }

        /// <summary>
        /// The boss of every later stage (bosses.json id, "auto" for a named boss of the rolled family, "family_champion"),
        /// or "random". Default "auto". Named bosses spawn at the World Events boss scale.
        /// </summary>
        [JsonPropertyName("nextBoss")]
        public string NextBossJson { get; set; } = DefaultNextBoss;

        /// <summary>In force: its Hold setting (HoldKeys.ChainNextBoss) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public string NextBoss { get => HoldSettings.Text(HoldKeys.ChainNextBoss, NextBossJson); set => NextBossJson = value; }

        /// <summary>An axis value as the request takes it: null for "random" or blank, else the trimmed lowercase id.</summary>
        public static string Pinned(string axis)
        {
            if (string.IsNullOrWhiteSpace(axis))
                return null;

            var id = axis.Trim().ToLowerInvariant();

            return id == RandomAxis ? null : id;
        }

        /// <summary>The reward id for <paramref name="stage"/> (1-based), or null when the list is empty.</summary>
        public string RewardForStage(int stage)
        {
            if (StageRewards == null || StageRewards.Count == 0)
                return null;

            var id = StageRewards[Math.Clamp(stage - 1, 0, StageRewards.Count - 1)];

            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
    }

    /// <summary>
    /// goals.json "chain.pacing": seconds after a stage's outcome line at which each following line goes out, so a stage
    /// change reads in order instead of all at once. Each is 0 to 60.
    /// </summary>
    public class ChainPacingDef
    {
        /// <summary>A surviving envoy thanks the players and points them to the cache. Default 2.</summary>
        [JsonPropertyName("thanksSeconds")]
        public double ThanksSecondsJson { get; set; } = 2;

        /// <summary>In force: its Hold setting (HoldKeys.ChainThanksSeconds) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double ThanksSeconds { get => HoldSettings.Number(HoldKeys.ChainThanksSeconds, ThanksSecondsJson); set => ThanksSecondsJson = value; }

        /// <summary>The story beat that explains why the fight moves on. Default 5.</summary>
        [JsonPropertyName("storySeconds")]
        public double StorySecondsJson { get; set; } = 5;

        /// <summary>In force: its Hold setting (HoldKeys.ChainStorySeconds) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double StorySeconds { get => HoldSettings.Number(HoldKeys.ChainStorySeconds, StorySecondsJson); set => StorySecondsJson = value; }

        /// <summary>
        /// The next stage's announcement (chain.announce). The countdown it names starts here, so the next stage goes
        /// live countdownSeconds after this line. Default 8.
        /// </summary>
        [JsonPropertyName("announceSeconds")]
        public double AnnounceSecondsJson { get; set; } = 8;

        /// <summary>In force: its Hold setting (HoldKeys.ChainAnnounceSeconds) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double AnnounceSeconds { get => HoldSettings.Number(HoldKeys.ChainAnnounceSeconds, AnnounceSecondsJson); set => AnnounceSecondsJson = value; }

        /// <summary>After a later stage goes live, the line that ties it back to the first. Default 3.</summary>
        [JsonPropertyName("tieBackSeconds")]
        public double TieBackSecondsJson { get; set; } = 3;

        /// <summary>In force: its Hold setting (HoldKeys.ChainTieBackSeconds) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double TieBackSeconds { get => HoldSettings.Number(HoldKeys.ChainTieBackSeconds, TieBackSecondsJson); set => TieBackSecondsJson = value; }

        /// <summary>When the chain ends (last stage won, or any stage lost): the closing story line. Default 3.</summary>
        [JsonPropertyName("closingSeconds")]
        public double ClosingSecondsJson { get; set; } = 3;

        /// <summary>In force: its Hold setting (HoldKeys.ChainClosingSeconds) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double ClosingSeconds { get => HoldSettings.Number(HoldKeys.ChainClosingSeconds, ClosingSecondsJson); set => ClosingSecondsJson = value; }

        /// <summary>When the chain ends: Asheron's closing line ("It is done..."). Default 6.</summary>
        [JsonPropertyName("asheronSeconds")]
        public double AsheronSecondsJson { get; set; } = 6;

        /// <summary>In force: its Hold setting (HoldKeys.ChainAsheronSeconds) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double AsheronSeconds { get => HoldSettings.Number(HoldKeys.ChainAsheronSeconds, AsheronSecondsJson); set => AsheronSecondsJson = value; }
    }

    /// <summary>
    /// goals.json "chain.story": the lines that link the stages, one picked at random from a list. Each beat has an
    /// envoy list (spoken by a surviving envoy, shown as "Sir Adarl: ...") and a narrator list (used when no envoy
    /// survived). Tokens: {envoy} (the speaking envoy), {family} / {Family} (the first stage's attackers), {town} (the
    /// first stage's town, where the envoys were attacked), {nextTown} (the town the chain moved on to), {champion} (the later stage's named boss, "their champion" without one). An empty
    /// list skips that line, except thanks, which falls back to a built-in line so players always hear about the cache.
    /// </summary>
    public class ChainStoryDef
    {
        /// <summary>A stage was won and the chain goes on: the envoy thanks the players and points them to the cache.</summary>
        [JsonPropertyName("thanks")]
        public List<string> Thanks { get; set; } = new List<string>();

        [JsonPropertyName("thanksNarrator")]
        public List<string> ThanksNarrator { get; set; } = new List<string>();

        /// <summary>Why the fight moves on: the attack was meant to stop the envoys' message, and its champion is at {nextTown}.</summary>
        [JsonPropertyName("reveal")]
        public List<string> Reveal { get; set; } = new List<string>();

        [JsonPropertyName("revealNarrator")]
        public List<string> RevealNarrator { get; set; } = new List<string>();

        /// <summary>A later stage has gone live: the champion is the one who sent the first stage's attackers.</summary>
        [JsonPropertyName("tieBack")]
        public List<string> TieBack { get; set; } = new List<string>();

        [JsonPropertyName("tieBackNarrator")]
        public List<string> TieBackNarrator { get; set; } = new List<string>();

        /// <summary>The last stage was won: the chain is closed.</summary>
        [JsonPropertyName("closingSuccess")]
        public List<string> ClosingSuccess { get; set; } = new List<string>();

        [JsonPropertyName("closingSuccessNarrator")]
        public List<string> ClosingSuccessNarrator { get; set; } = new List<string>();

        /// <summary>A stage was lost: the chain ends there.</summary>
        [JsonPropertyName("closingFailure")]
        public List<string> ClosingFailure { get; set; } = new List<string>();

        [JsonPropertyName("closingFailureNarrator")]
        public List<string> ClosingFailureNarrator { get; set; } = new List<string>();
    }
}

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The World Events chain (2026-10-18): a successful run whose goal carries "chain" hands over to a second run nearby.
    /// The rules are pure and tested; WorldEventManager applies them (retiring the finished run into its claim window
    /// while the next one counts down) and <see cref="TryPickNearbyAnchor"/> finds the spot.
    /// </summary>
    public static class WorldEventChain
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventChain));

        /// <summary>What the manager does when a run ends (D6 - pure).</summary>
        public enum Decision
        {
            /// <summary>The run's goal has no chain, and it is not part of one.</summary>
            None,

            /// <summary>Start the next stage.</summary>
            Next,

            /// <summary>Part of a chain, but it failed or was aborted: the chain ends.</summary>
            EndedByOutcome,

            /// <summary>Part of a chain that reached its last stage: the chain ends.</summary>
            EndedAtMax
        }

        /// <summary>
        /// The chain rule (D6 - pure): a <see cref="WorldEventOutcome.Success"/> with a chain (the run's own goal, or the
        /// chain it is a stage of: <paramref name="maxLength"/> above 0) and stages left starts the next; anything else
        /// ends it.
        /// </summary>
        public static Decision Decide(WorldEventOutcome outcome, int stage, int maxLength)
        {
            if (maxLength <= 1)
                return Decision.None;

            if (outcome != WorldEventOutcome.Success)
                return Decision.EndedByOutcome;

            return stage >= maxLength ? Decision.EndedAtMax : Decision.Next;
        }

        /// <summary>
        /// The admin settings on a decision (D6 - pure): with world_events_chain off, or the chance roll missed
        /// (world_events_chain_chance below 1 and <paramref name="roll"/> at or above it), a stage that would go on ends the
        /// chain there instead (its closing lines play). Any other decision, and the defaults (on, chance 1), unchanged.
        /// </summary>
        public static Decision ApplySettings(Decision decision, int stage, bool chainOn, double chance, double roll)
        {
            if (decision != Decision.Next)
                return decision;

            if (!chainOn)
                return Decision.EndedAtMax;

            if (chance < 1 && roll >= Math.Clamp(chance, 0, 1))
                return Decision.EndedAtMax;

            return decision;
        }

        /// <summary>The chain length a run follows (D6 - pure): the one it inherited as a later stage, else its goal's, else none (0).</summary>
        public static int EffectiveMaxLength(int inherited, ChainDef goalChain)
        {
            if (inherited > 0)
                return inherited;

            return goalChain != null ? Math.Clamp(goalChain.MaxLength, 2, 5) : 0;
        }

        /// <summary>
        /// The next stage's lead times (D6 - pure): the teaser then the announce, adding up to the countdown, so the run
        /// goes Active <paramref name="countdownSeconds"/> after the broadcast.
        /// </summary>
        public static (int Teaser, int Announce) Leads(int countdownSeconds, int announceLead)
        {
            var countdown = Math.Max(0, countdownSeconds);
            var announce = Math.Clamp(announceLead, 0, countdown);

            return (countdown - announce, announce);
        }

        /// <summary>A candidate offset (D6 - pure): a random bearing, a distance between the two bounds, in metres east and north.</summary>
        public static (double Dx, double Dy) Offset(Random rng, double minDistance, double maxDistance)
        {
            rng ??= new Random();

            var lo = Math.Max(0, Math.Min(minDistance, maxDistance));
            var hi = Math.Max(lo, Math.Max(minDistance, maxDistance));

            var bearing = rng.NextDouble() * 2 * Math.PI;
            var distance = lo + rng.NextDouble() * (hi - lo);

            return (Math.Sin(bearing) * distance, Math.Cos(bearing) * distance);
        }

        /// <summary>The broadcast (D6 - pure): {family}, {Family}, {location} and {seconds} filled; the default when there is no template.</summary>
        public static string AnnounceLine(string template, string familyDisplayName, string location, int seconds)
        {
            return AnnounceLine(template, familyDisplayName, location, seconds, null);
        }

        /// <summary>
        /// <see cref="AnnounceLine(string, string, string, int)"/> with {town}, the next stage's town by name (the location's
        /// own name when no town was picked).
        /// </summary>
        public static string AnnounceLine(string template, string familyDisplayName, string location, int seconds, string town)
        {
            return AnnounceLine(template, familyDisplayName, location, seconds, town, null);
        }

        /// <summary>With {boss}, the next stage's champion by name ("their champion" when it has none).</summary>
        public static string AnnounceLine(string template, string familyDisplayName, string location, int seconds, string town, string boss)
        {
            var line = string.IsNullOrWhiteSpace(template) ? "{Family} regroup near {location}! They strike in {seconds} seconds." : template;
            var family = string.IsNullOrWhiteSpace(familyDisplayName) ? "the enemy" : familyDisplayName.Trim();
            var place = string.IsNullOrWhiteSpace(town) ? (string.IsNullOrWhiteSpace(location) ? "a town nearby" : location.Trim()) : town.Trim();

            return line.Replace("{town}", place)
                       .Replace("{boss}", string.IsNullOrWhiteSpace(boss) ? "their champion" : boss.Trim())
                       .Replace("{Family}", Capitalize(family))
                       .Replace("{family}", family)
                       .Replace("{location}", string.IsNullOrWhiteSpace(location) ? "here" : location.Trim())
                       .Replace("{seconds}", Math.Max(0, seconds).ToString());
        }

        /// <summary>The thank-you used when chain.story.thanks is empty: it must still point players to the cache.</summary>
        public const string DefaultThanks = "Well fought, friends! Take your reward from the cache.";

        /// <summary>The story beats (see <see cref="ChainStoryDef"/>).</summary>
        public enum StoryBeat { Thanks, Reveal, TieBack, ClosingSuccess, ClosingFailure }

        /// <summary>What fills a story line's tokens.</summary>
        public sealed class StoryTokens
        {
            public string Envoy;
            public string Family;
            public string Town;
            public string NextTown;
            public string Champion;
        }

        /// <summary>
        /// One story line, ready to broadcast (D6 - pure): with a speaking envoy, from the beat's envoy list as
        /// "[World Event] Sir Adarl: "..."", else from its narrator list as "[World Event] ...". Null when the list it
        /// needs is empty (thanks falls back to <see cref="DefaultThanks"/>, or a narrator version of it).
        /// </summary>
        public static string StoryLine(ChainStoryDef story, StoryBeat beat, StoryTokens tokens, Random rng)
        {
            story ??= new ChainStoryDef();
            tokens ??= new StoryTokens();
            rng ??= new Random();

            var envoy = string.IsNullOrWhiteSpace(tokens.Envoy) ? null : tokens.Envoy.Trim();

            var (spoken, narrated) = beat switch
            {
                StoryBeat.Thanks => (story.Thanks, story.ThanksNarrator),
                StoryBeat.Reveal => (story.Reveal, story.RevealNarrator),
                StoryBeat.TieBack => (story.TieBack, story.TieBackNarrator),
                StoryBeat.ClosingSuccess => (story.ClosingSuccess, story.ClosingSuccessNarrator),
                _ => (story.ClosingFailure, story.ClosingFailureNarrator)
            };

            var pool = envoy != null ? spoken : narrated;
            var template = WorldEventHoldRules.PickLine(pool, rng);

            if (template == null && beat == StoryBeat.Thanks)
                template = envoy != null ? DefaultThanks : "The battle is won. Your reward waits in the cache.";

            if (template == null)
                return null;

            var text = FillStory(template, tokens);

            return envoy != null ? WorldEventAnnouncer.ProtectedSpeechLine(envoy, text) : WorldEventAnnouncer.Global(text);
        }

        /// <summary>A story template with its tokens filled (D6 - pure); the first letter is capitalised.</summary>
        public static string FillStory(string template, StoryTokens tokens)
        {
            tokens ??= new StoryTokens();

            var family = string.IsNullOrWhiteSpace(tokens.Family) ? WorldEventHoldRules.DefaultFamilyText : tokens.Family.Trim();

            var line = (template ?? "")
                .Replace("{envoy}", string.IsNullOrWhiteSpace(tokens.Envoy) ? "the envoy" : tokens.Envoy.Trim())
                .Replace("{Family}", Capitalize(family))
                .Replace("{family}", family)
                .Replace("{nextTown}", string.IsNullOrWhiteSpace(tokens.NextTown) ? "the next town" : tokens.NextTown.Trim())
                .Replace("{town}", string.IsNullOrWhiteSpace(tokens.Town) ? "this place" : tokens.Town.Trim())
                .Replace("{champion}", string.IsNullOrWhiteSpace(tokens.Champion) ? "their champion" : tokens.Champion.Trim())
                .Trim();

            return Capitalize(line);
        }

        /// <summary>One line of a paced sequence: when it goes out (seconds after the outcome line) and its text.</summary>
        public readonly struct PacedLine
        {
            public PacedLine(double delay, string text, bool aboutNextStage = false)
            {
                Delay = delay;
                Text = text;
                AboutNextStage = aboutNextStage;
            }

            public double Delay { get; }
            public string Text { get; }

            /// <summary>The line speaks of the next stage, so it is dropped if that stage ends before it goes out.</summary>
            public bool AboutNextStage { get; }
        }

        /// <summary>
        /// A stage was won and the next one has started (D6 - pure): the thank-you with the cache, the story beat, then
        /// the next stage's announcement, at chain.pacing's times. The outcome line itself went out at 0 s. Lines with no
        /// text are left out.
        /// </summary>
        public static List<PacedLine> StageChangePlan(ChainDef chain, StoryTokens tokens, string announcement, Random rng)
        {
            chain ??= new ChainDef();
            var pacing = chain.Pacing ?? new ChainPacingDef();

            var plan = new List<PacedLine>
            {
                new PacedLine(pacing.ThanksSeconds, StoryLine(chain.Story, StoryBeat.Thanks, tokens, rng)),
                new PacedLine(pacing.StorySeconds, StoryLine(chain.Story, StoryBeat.Reveal, tokens, rng), true),
                new PacedLine(pacing.AnnounceSeconds, string.IsNullOrWhiteSpace(announcement) ? null : WorldEventAnnouncer.Global(announcement), true)
            };

            return plan.Where(l => !string.IsNullOrEmpty(l.Text)).OrderBy(l => l.Delay).ToList();
        }

        /// <summary>
        /// The chain has ended (D6 - pure). A win or a loss: the closing story line at chain.pacing.closingSeconds and
        /// Asheron's closing line (when his opening line went out) at asheronSeconds. An abort: only Asheron's line,
        /// at once. <paramref name="thanksFirst"/>: a won stage whose next stage could not start also gets the
        /// thank-you, and the closing lines move to the story and announce times.
        /// </summary>
        public static List<PacedLine> ChainEndPlan(ChainDef chain, WorldEventOutcome outcome, StoryTokens tokens, bool asheronSpoke,
            bool thanksFirst, Random rng)
        {
            chain ??= new ChainDef();
            var pacing = chain.Pacing ?? new ChainPacingDef();
            var plan = new List<PacedLine>();
            var asheron = asheronSpoke ? WorldEventAnnouncer.DeathProtectionEndLine : null;

            if (IsAbort(outcome))
            {
                plan.Add(new PacedLine(0, asheron));
            }
            else
            {
                var won = outcome == WorldEventOutcome.Success || outcome == WorldEventOutcome.SuccessBossAbsent;
                var closing = StoryLine(chain.Story, won ? StoryBeat.ClosingSuccess : StoryBeat.ClosingFailure, tokens, rng);

                if (thanksFirst && won)
                {
                    plan.Add(new PacedLine(pacing.ThanksSeconds, StoryLine(chain.Story, StoryBeat.Thanks, tokens, rng)));
                    plan.Add(new PacedLine(pacing.StorySeconds, closing));
                    plan.Add(new PacedLine(pacing.AnnounceSeconds, asheron));
                }
                else
                {
                    plan.Add(new PacedLine(pacing.ClosingSeconds, closing));
                    plan.Add(new PacedLine(pacing.AsheronSeconds, asheron));
                }
            }

            return plan.Where(l => !string.IsNullOrEmpty(l.Text)).OrderBy(l => l.Delay).ToList();
        }

        private static bool IsAbort(WorldEventOutcome outcome) =>
            outcome == WorldEventOutcome.AbortedAdmin || outcome == WorldEventOutcome.AbortedShutdown || outcome == WorldEventOutcome.AbortedError;

        private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>The goals.json check (D6 - pure). Null when valid.</summary>
        public static string Validate(ChainDef chain)
        {
            if (chain == null)
                return null;

            if (chain.MaxLength < 2 || chain.MaxLength > 5)
                return "has an invalid chain.maxLength (2 to 5)";

            if (chain.CountdownSeconds < 60 || chain.CountdownSeconds > 600)
                return "has an invalid chain.countdownSeconds (60 to 600)";

            if (!double.IsFinite(chain.MinDistance) || !double.IsFinite(chain.MaxDistance) || chain.MinDistance < 100
                || chain.MaxDistance <= chain.MinDistance || chain.MaxDistance > 3000)
                return "has an invalid chain distance (minDistance 100 or more, maxDistance above it and up to 3000)";

            if (!double.IsFinite(chain.TownMinDistance) || !double.IsFinite(chain.TownMaxDistance) || chain.TownMinDistance < 200
                || chain.TownMinDistance > 5000 || chain.TownMaxDistance <= chain.TownMinDistance || chain.TownMaxDistance > 30000)
                return "has an invalid chain town distance (townMinDistance 200 to 5000, townMaxDistance above it and up to 30000)";

            if (chain.TownChoices < 1 || chain.TownChoices > 10)
                return "has an invalid chain.townChoices (1 to 10)";

            if (!double.IsFinite(chain.TownSpotMinDistance) || !double.IsFinite(chain.TownSpotMaxDistance) || chain.TownSpotMinDistance < 20
                || chain.TownSpotMinDistance > 400 || chain.TownSpotMaxDistance <= chain.TownSpotMinDistance || chain.TownSpotMaxDistance > 600)
                return "has an invalid chain town spot distance (townSpotMinDistance 20 to 400, townSpotMaxDistance above it and up to 600)";

            var p = chain.Pacing;

            if (p != null && new[] { p.ThanksSeconds, p.StorySeconds, p.AnnounceSeconds, p.TieBackSeconds, p.ClosingSeconds, p.AsheronSeconds }
                    .Any(s => !double.IsFinite(s) || s < 0 || s > 60))
                return "has an invalid chain.pacing (every value 0 to 60 seconds)";

            return null;
        }

        /// <summary>
        /// The stage rewards' ids that rewards.json does not define (D6 - pure), for the load check.
        /// </summary>
        public static List<string> UnknownStageRewards(ChainDef chain, IEnumerable<string> rewardIds)
        {
            var known = new HashSet<string>(rewardIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            return (chain?.StageRewards ?? new List<string>()).Where(id => string.IsNullOrWhiteSpace(id) || !known.Contains(id.Trim())).ToList();
        }

        /// <summary>
        /// The next stage's town (D6 - pure): of the towns at least <see cref="ChainDef.TownMinDistance"/> from
        /// <paramref name="originX"/>/<paramref name="originY"/> (global metres) and not named <paramref name="originTown"/>, one of
        /// the <see cref="ChainDef.TownChoices"/> nearest within <see cref="ChainDef.TownMaxDistance"/>, at random; with none in
        /// that range, the nearest one past it. Null when no other town exists. <paramref name="exclude"/> names towns
        /// already tried.
        /// </summary>
        public static WorldEventTown? PickTown(IReadOnlyList<WorldEventTown> towns, double originX, double originY, string originTown,
            ChainDef chain, Random rng, ICollection<string> exclude = null)
        {
            chain ??= new ChainDef();
            rng ??= new Random();

            var others = (towns ?? Array.Empty<WorldEventTown>())
                .Where(t => !string.IsNullOrWhiteSpace(t.Name)
                            && !string.Equals(t.Name, originTown, StringComparison.OrdinalIgnoreCase)
                            && (exclude == null || !exclude.Contains(t.Name)))
                .Select(t => (Town: t, Distance: Math.Sqrt((t.GlobalX - originX) * (t.GlobalX - originX) + (t.GlobalY - originY) * (t.GlobalY - originY))))
                .Where(t => t.Distance >= chain.TownMinDistance)
                .OrderBy(t => t.Distance)
                .ToList();

            if (others.Count == 0)
                return null;

            var inRange = others.Where(t => t.Distance <= chain.TownMaxDistance).Take(Math.Max(1, chain.TownChoices)).ToList();

            if (inRange.Count == 0)
                return others[0].Town;

            return inRange[rng.Next(inRange.Count)].Town;
        }

        /// <summary>
        /// A spot at the edge of <paramref name="town"/> (live: reads the landscape): up to <paramref name="attempts"/> random
        /// spots between chain.townSpotMinDistance and townSpotMaxDistance from its portal drop, the first on open ground.
        /// </summary>
        public static bool TryPickNearTown(WorldEventTown town, Position reference, ChainDef chain, Random rng, out Position anchor, int attempts = 40)
        {
            chain ??= new ChainDef();

            return TryPickNearTown(town, reference, chain.TownSpotMinDistance, chain.TownSpotMaxDistance, rng, out anchor, attempts);
        }

        /// <summary><see cref="TryPickNearTown(WorldEventTown, Position, ChainDef, Random, out Position, int)"/> with explicit distances from the drop.</summary>
        public static bool TryPickNearTown(WorldEventTown town, Position reference, double minDistance, double maxDistance, Random rng,
            out Position anchor, int attempts = 40)
        {
            anchor = null;

            if (reference == null)
                return false;

            for (var i = 0; i < attempts; i++)
            {
                var (dx, dy) = Offset(rng, minDistance, maxDistance);
                var global = new Vector3(town.GlobalX + (float)dx, town.GlobalY + (float)dy, 0);

                if (global.X < 0 || global.Y < 0 || global.X >= 255 * Position.BlockLength || global.Y >= 255 * Position.BlockLength)
                    continue;

                try
                {
                    var candidate = reference.FromGlobal(global);
                    candidate.Instance = reference.Instance;

                    if (!IsOpenGround(candidate))
                        continue;

                    candidate.PositionZ = candidate.GetTerrainZ();
                    anchor = candidate;
                    return true;
                }
                catch (Exception ex)
                {
                    log.Debug($"[WORLDEVENT] chain town spot check threw near {town.Name}", ex);
                }
            }

            return false;
        }

        /// <summary>
        /// The stage settings' load check against the store (D6 - pure): an unknown source, goal or boss, or a goal the
        /// source does not support. Empty when valid.
        /// </summary>
        public static List<string> NextStageProblems(ChainDef chain, IReadOnlyDictionary<string, SourceThemeDef> sources,
            ICollection<string> goalIds, ICollection<string> bossIds)
        {
            var problems = new List<string>();

            if (chain == null)
                return problems;

            var source = ChainDef.Pinned(chain.NextSource);
            var goal = ChainDef.Pinned(chain.NextGoal);
            var boss = ChainDef.Pinned(chain.NextBoss);

            SourceThemeDef sourceDef = null;

            if (source != null && (sources == null || !sources.TryGetValue(source, out sourceDef)))
                problems.Add($"chain.nextSource '{source}' is not in sources.json");

            if (goal != null && (goalIds == null || !goalIds.Contains(goal)))
                problems.Add($"chain.nextGoal '{goal}' is not in goals.json");

            if (sourceDef != null && goal != null && !(sourceDef.CompatibleGoals ?? new List<string>()).Contains(goal))
                problems.Add($"chain.nextSource '{source}' does not support chain.nextGoal '{goal}' (its compatibleGoals)");

            if (boss != null && boss != WorldEventComposer.AutoBossId && boss != "family_champion" && boss != WorldEventComposer.DefaultBossId
                && (bossIds == null || !bossIds.Contains(boss)))
                problems.Add($"chain.nextBoss '{boss}' is not in bosses.json (or auto, family_champion, none, random)");

            return problems;
        }

        /// <summary>
        /// The next stage's request (D6 - pure), at <paramref name="anchor"/>, with the lead times of the countdown. The
        /// source, goal and boss are the chain's nextSource / nextGoal / nextBoss (by default a sky rift whose champion
        /// must be slain); an axis set to "random" rolls, and the family always rolls. The first stage's test settings
        /// (--level, --abandon) carry over, so a staff test chains the same way; a forced wave type does not.
        /// </summary>
        public static WorldEventRequest NextRequest(WorldEventRequest previous, ChainDef chain, int stage, int maxLength, uint rootRunId,
            uint previousRunId, Position anchor, string anchorLabel)
        {
            // The countdown runs from the announcement, which goes out chain.pacing.announceSeconds after the outcome line.
            var announceDelay = (int)Math.Round(Math.Clamp(chain?.Pacing?.AnnounceSeconds ?? 0, 0, 60));
            var leads = Leads((chain?.CountdownSeconds ?? 120) + announceDelay,
                HoldSettings.Integer(HoldKeys.ChainAnnounceLeadSeconds, WorldEvent.DefaultAnnounceLeadSeconds));

            // world_events_chain_arrival_seconds: the next stage's own time limit (else stage 1's); its champion arrives inside it.
            var maxDuration = HoldSettings.ChainArrivalLimit(previous?.MaxDurationSeconds ?? WorldEvent.DefaultMaxDurationSeconds);

            return new WorldEventRequest
            {
                Random = true,
                SourceId = chain != null ? ChainDef.Pinned(chain.NextSource) : null,
                GoalId = chain != null ? ChainDef.Pinned(chain.NextGoal) : null,
                BossId = chain != null ? ChainDef.Pinned(chain.NextBoss) : null,

                // world_events_chain_next_family: the next stage's monster family (not set or "random": it rolls, as before).
                FamilyIds = HoldSettings.ChainNextFamily(),
                TownReach = previous?.TownReach,
                AnchorPosition = anchor,
                AnchorLabel = anchorLabel,
                Invoker = $"chain from run {previousRunId}",
                AnnounceLeadSeconds = leads.Announce,
                TeaserLeadSeconds = leads.Teaser,
                // world_events_chain_champion_arrival_seconds: when the champion arrives (its stage's minimum duration).
                MinDurationSeconds = HoldSettings.ChampionArrival(previous?.MinDurationSeconds ?? WorldEvent.DefaultMinDurationSeconds, maxDuration),
                MaxDurationSeconds = maxDuration,
                AbandonAfterSeconds = previous?.AbandonAfterSeconds ?? WorldEvent.DefaultAbandonAfterSeconds,
                WipeGraceSeconds = previous?.WipeGraceSeconds ?? WorldEvent.DefaultWipeGraceSeconds,
                TestLevel = previous?.TestLevel,
                AllowedSourceIds = previous?.AllowedSourceIds,
                RewardId = chain?.RewardForStage(stage),
                ChainStage = stage,
                ChainMaxLength = maxLength,
                ChainRootRunId = rootRunId
            };
        }

        /// <summary>
        /// A spot for the next stage (live: reads the landscape): up to <paramref name="attempts"/> random spots between
        /// chain.minDistance and chain.maxDistance from <paramref name="origin"/>, the first that is outdoors, on land
        /// (no water), not inside a building, on a walkable slope and in the same instance. False, with the reason, when
        /// none is found or the origin is indoors.
        /// </summary>
        public static bool TryPickNearbyAnchor(Position origin, ChainDef chain, Random rng, out Position anchor, out string why, int attempts = 40)
        {
            anchor = null;
            why = null;

            if (origin == null || origin.Indoors)
            {
                why = "the first stage was indoors";
                return false;
            }

            var centre = origin.ToGlobal();

            for (var i = 0; i < attempts; i++)
            {
                var (dx, dy) = Offset(rng, chain?.MinDistance ?? 250, chain?.MaxDistance ?? 500);
                var global = new Vector3(centre.X + (float)dx, centre.Y + (float)dy, centre.Z);

                if (global.X < 0 || global.Y < 0 || global.X >= 255 * Position.BlockLength || global.Y >= 255 * Position.BlockLength)
                    continue;

                Position candidate;

                try
                {
                    candidate = origin.FromGlobal(global);
                    candidate.Instance = origin.Instance;

                    if (!IsOpenGround(candidate))
                        continue;

                    candidate.PositionZ = candidate.GetTerrainZ();
                }
                catch (Exception ex)
                {
                    log.Debug($"[WORLDEVENT] chain spot check threw at offset ({dx:F0}, {dy:F0})", ex);
                    continue;
                }

                anchor = candidate;
                return true;
            }

            why = $"no open ground found in {attempts} tries";
            return false;
        }

        /// <summary>Outdoors, on land, not a building's cell, walkable.</summary>
        private static bool IsOpenGround(Position p)
        {
            if (p == null || p.Indoors)
                return false;

            var cell = LScape.get_landcell(p.GetOutdoorCell(), p.Instance) as LandCell;

            if (cell == null || cell.WaterType != LandDefs.WaterType.NotWater || cell.has_building())
                return false;

            return p.IsWalkable();
        }
    }
}
