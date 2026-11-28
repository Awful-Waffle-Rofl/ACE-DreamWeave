using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ACE.Server.BalanceTrials
{
    /// <summary>The @trial argument parser and status-line formatter. Pure: no world access.</summary>
    public static class TrialCommandParser
    {
        public static readonly string Usage = $"@trial start <scenario> <archetype> [for <minutes> campaign <id> [driver <label>]] | next | status | abort | rebuild <archetype> <1-{TrialRoster.RepCount}> | promote <archetype> <1-{TrialRoster.RepCount}> | unpromote <archetype> | unpromote slot:<n> | adopt <id> <Class> <character name...> | list | campaign status|stop|report <id>";

        public const string StartUsage = "usage: @trial start <scenario> <archetype> [for <minutes> campaign <id> [driver <label>]]";

        public const string CampaignUsage = "usage: @trial campaign status|stop|report <id>";

        public const int MinCampaignMinutes = 1;
        public const int MaxCampaignMinutes = 720;

        /// <summary>The frozen campaign id pattern.</summary>
        public static readonly Regex CampaignIdPattern = new Regex("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant);

        /// <summary>The frozen driver label pattern (for example rynth-ui:1a2b3c4d).</summary>
        public static readonly Regex DriverLabelPattern = new Regex("^[a-z0-9:._-]{1,40}$", RegexOptions.CultureInvariant);

        /// <summary>The campaign half of a start: the duration, the campaign id and the driver label (null when none was given).</summary>
        public sealed record CampaignStart(int Minutes, string CampaignId, string Driver);

        /// <summary>
        /// The campaign tail of a start's arguments (<paramref name="rest"/> is everything after "start"):
        /// <c>&lt;scenario&gt; &lt;archetype&gt; for &lt;minutes&gt; campaign &lt;id&gt; [driver &lt;label&gt;]</c>. Minutes are a whole
        /// number 1..720, the id matches ^[a-z0-9-]{1,32}$ and the label ^[a-z0-9:._-]{1,40}$. False, with the reason, otherwise.
        /// </summary>
        public static bool TryParseCampaignStart(string[] rest, out CampaignStart start, out string error)
        {
            start = null;

            if (rest == null || (rest.Length != 6 && rest.Length != 8) || rest[2] != "for" || rest[4] != "campaign" || (rest.Length == 8 && rest[6] != "driver"))
            {
                error = StartUsage;
                return false;
            }

            if (!int.TryParse(rest[3], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes < MinCampaignMinutes || minutes > MaxCampaignMinutes)
            {
                error = $"minutes '{rest[3]}' must be a whole number {MinCampaignMinutes}..{MaxCampaignMinutes}";
                return false;
            }

            if (!CampaignIdPattern.IsMatch(rest[5]))
            {
                error = $"campaign id '{rest[5]}' must match {CampaignIdPattern}";
                return false;
            }

            string driver = null;

            if (rest.Length == 8)
            {
                if (!DriverLabelPattern.IsMatch(rest[7]))
                {
                    error = $"driver label '{rest[7]}' must match {DriverLabelPattern}";
                    return false;
                }

                driver = rest[7];
            }

            start = new CampaignStart(minutes, rest[5], driver);
            error = null;
            return true;
        }

        /// <summary>Verb is lower-cased. Error is null on success, else the reason (Verb and Args are then null/empty).</summary>
        public static (string Verb, string[] Args, string Error) Parse(string[] args)
        {
            if (args == null || args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
                return (null, Array.Empty<string>(), "usage: " + Usage);

            var verb = args[0].Trim().ToLowerInvariant();
            var rest = new string[args.Length - 1];
            Array.Copy(args, 1, rest, 0, rest.Length);

            switch (verb)
            {
                case "start":
                    if (rest.Length != 2 && !TryParseCampaignStart(rest, out _, out var startError))
                        return Fail(startError);
                    break;

                case "campaign":
                    if (rest.Length != 2)
                        return Fail(CampaignUsage);

                    rest[0] = rest[0].Trim().ToLowerInvariant();

                    if (rest[0] != "status" && rest[0] != "stop" && rest[0] != "report")
                        return Fail(CampaignUsage);

                    if (!CampaignIdPattern.IsMatch(rest[1]))
                        return Fail($"campaign id '{rest[1]}' must match {CampaignIdPattern}");
                    break;

                case "next":
                case "status":
                case "abort":
                case "list":
                    if (rest.Length != 0)
                        return Fail($"usage: @trial {verb}");
                    break;

                case "rebuild":
                case "promote":
                    if (rest.Length != 2)
                        return Fail($"usage: @trial {verb} <archetype> <1-{TrialRoster.RepCount}>");

                    if (!int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > TrialRoster.RepCount)
                        return Fail($"rep index '{rest[1]}' must be a whole number 1..{TrialRoster.RepCount}");
                    break;

                case "unpromote":
                    if (rest.Length != 1)
                        return Fail("usage: @trial unpromote <archetype>");
                    break;

                case "adopt":
                    // The character name may hold spaces: everything after the class is the name (TrialCommands.AdoptName).
                    if (rest.Length < 3)
                        return Fail("usage: @trial adopt <id> <Class> <character name...>");
                    break;

                default:
                    return Fail($"unknown verb '{args[0]}'; " + Usage);
            }

            return (verb, rest, null);
        }

        private static (string Verb, string[] Args, string Error) Fail(string error) => (null, Array.Empty<string>(), error);

        /// <summary>trial=&lt;uuid&gt; phase=&lt;Phase&gt; rep=&lt;done&gt;/&lt;reps&gt; alive=trash:&lt;n&gt; elite:&lt;n&gt; boss:&lt;n&gt; last=&lt;endState|-&gt; ready=&lt;bool&gt; rebuild=&lt;bool&gt;, then next=&lt;k&gt;:&lt;family&gt; when the scenario has a rotation</summary>
        public static string FormatStatus(string trialUuid, TrialPhase phase, int repsDone, int reps, int trash, int elite, int boss,
            string lastEndState, bool ready, bool rebuildRequired, string next = null)
            => $"trial={trialUuid} phase={phase} rep={repsDone}/{reps} alive=trash:{trash} elite:{elite} boss:{boss} "
             + $"last={(string.IsNullOrEmpty(lastEndState) ? "-" : lastEndState)} ready={Bool(ready)} rebuild={Bool(rebuildRequired)}"
             + (string.IsNullOrEmpty(next) ? "" : " " + next);

        private static string Bool(bool b) => b ? "true" : "false";
    }
}
