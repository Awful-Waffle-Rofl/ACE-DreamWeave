using System;
using System.Globalization;

namespace ACE.Server.BalanceTrials
{
    /// <summary>The @trial argument parser and status-line formatter. Pure: no world access.</summary>
    public static class TrialCommandParser
    {
        public const string Usage = "@trial start <scenario> <archetype> | next | status | abort | rebuild <archetype> <1-8> | promote <archetype> <1-8> | unpromote <archetype> | list";

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
                    if (rest.Length != 2)
                        return Fail("usage: @trial start <scenario> <archetype>");
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
                        return Fail($"usage: @trial {verb} <archetype> <1-8>");

                    if (!int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > TrialRoster.RepCount)
                        return Fail($"rep index '{rest[1]}' must be a whole number 1..{TrialRoster.RepCount}");
                    break;

                case "unpromote":
                    if (rest.Length != 1)
                        return Fail("usage: @trial unpromote <archetype>");
                    break;

                default:
                    return Fail($"unknown verb '{args[0]}'; " + Usage);
            }

            return (verb, rest, null);
        }

        private static (string Verb, string[] Args, string Error) Fail(string error) => (null, Array.Empty<string>(), error);

        /// <summary>trial=&lt;uuid&gt; phase=&lt;Phase&gt; rep=&lt;done&gt;/&lt;reps&gt; alive=trash:&lt;n&gt; elite:&lt;n&gt; boss:&lt;n&gt; last=&lt;endState|-&gt; ready=&lt;bool&gt; rebuild=&lt;bool&gt;</summary>
        public static string FormatStatus(string trialUuid, TrialPhase phase, int repsDone, int reps, int trash, int elite, int boss,
            string lastEndState, bool ready, bool rebuildRequired)
            => $"trial={trialUuid} phase={phase} rep={repsDone}/{reps} alive=trash:{trash} elite:{elite} boss:{boss} "
             + $"last={(string.IsNullOrEmpty(lastEndState) ? "-" : lastEndState)} ready={Bool(ready)} rebuild={Bool(rebuildRequired)}";

        private static string Bool(bool b) => b ? "true" : "false";
    }
}
