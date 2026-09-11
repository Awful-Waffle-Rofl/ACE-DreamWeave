using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;


using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the landblock population observability fix.
    ///
    /// What this harness CANNOT do, and why the coverage below is shaped the way it is: a Landblock cannot
    /// be constructed here at all (its constructor reads CellLandblock/LandblockInfo out of the client dat
    /// via DatManager, and calls DBObj), and there is no live Player - the existing tests build one with
    /// FormatterServices.GetUninitializedObject, which has no Session, no CurrentLandblock and no working
    /// ActionChain (queued actions are never run). So the bounded wait in Player.OnTeleportComplete cannot
    /// be driven end-to-end, and neither can the faulted continuation in Landblock.Init.
    ///
    /// Reflection is no help either: both TimeSpan fields involved live on Player, and touching any
    /// static member of Player runs its type initializer, which evaluates MarketplaceDrop - a live
    /// world-database read that throws here. So what is left is source-level, and it is written to be
    /// honest about that. These are a tripwire against the fix being deleted or mis-tuned, NOT a
    /// substitute for a behavioural test: they check the ordering of the real declared bounds against
    /// each other, and they are scoped to the single method under test by brace matching, so an
    /// unrelated ContinueWith or log.Error elsewhere in these very large files cannot satisfy them.
    /// The one genuinely behavioural test here is the faulted-continuation semantics the fix relies on.
    /// </summary>
    [TestClass]
    public class LandblockInitObservabilityTests
    {
        #region source helpers

        /// <summary>
        /// Walks up from the test output directory looking for relativePath, so the lookup works
        /// regardless of the bin\Debug vs bin\x64\Debug output layout.
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        /// <summary>
        /// Returns the source text of one method body, by brace matching from the method's opening brace.
        /// Scoping the assertions to a single method is the point: a file-wide grep would be satisfied by
        /// any unrelated occurrence elsewhere in these very large files.
        /// </summary>
        private static string ExtractMethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not find '{signature}' in the source under test");

            var open = source.IndexOf('{', start);
            Assert.IsTrue(open >= 0, $"Could not find the opening brace of '{signature}'");

            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                        return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"Unbalanced braces while extracting '{signature}'");
            return null;
        }

        /// <summary>
        /// Given the index of the '(' opening a condition, returns the index of its matching ')'.
        /// </summary>
        private static int MatchParen(string source, int open)
        {
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '(')
                    depth++;
                else if (source[i] == ')')
                {
                    depth--;

                    if (depth == 0)
                        return i;
                }
            }

            Assert.Fail("Unbalanced parentheses while parsing a condition");
            return -1;
        }

        /// <summary>
        /// The span (start, endExclusive) of the statement or block a condition controls, given the index
        /// of the condition's closing ')'. A braced block is brace-matched; a single statement runs to its
        /// terminating semicolon, skipping semicolons that sit inside a string literal.
        /// </summary>
        private static Tuple<int, int> ControlledSpan(string source, int closeParen)
        {
            var i = closeParen + 1;

            while (i < source.Length && char.IsWhiteSpace(source[i]))
                i++;

            Assert.IsTrue(i < source.Length, "a condition controls nothing - the source under test is truncated");

            if (source[i] == '{')
            {
                var depth = 0;

                for (var j = i; j < source.Length; j++)
                {
                    if (source[j] == '{')
                        depth++;
                    else if (source[j] == '}')
                    {
                        depth--;

                        if (depth == 0)
                            return Tuple.Create(i, j + 1);
                    }
                }

                Assert.Fail("Unbalanced braces while measuring a controlled block");
                return null;
            }

            var inString = false;

            for (var j = i; j < source.Length; j++)
            {
                if (source[j] == '\\')
                {
                    j++;
                    continue;
                }

                if (source[j] == '"')
                    inString = !inString;
                else if (source[j] == ';' && !inString)
                    return Tuple.Create(i, j + 1);
            }

            Assert.Fail("Ran off the end while measuring a controlled statement");
            return null;
        }

        /// <summary>
        /// The condition text of the INNERMOST `if` whose controlled statement or block contains index,
        /// or null if index is not inside any `if`. This is what makes the structural assertions below
        /// mean something: checking only that two tokens appear in a given ORDER is satisfied just as
        /// happily by a fix whose comparison has been inverted, or whose guarded line has been lifted out
        /// of its guard.
        /// </summary>
        private static string EnclosingIfCondition(string source, int index)
        {
            // These files are heavily commented and some of those comments contain code. Scan a copy with
            // every // comment blanked to spaces - same length, so every index still lines up with the
            // original - so a commented-out `if (` can never be mistaken for a real one.
            var scan = Regex.Replace(source, @"//[^\r\n]*", m => new string(' ', m.Length));

            string innermost = null;
            var innermostStart = -1;

            foreach (Match m in Regex.Matches(scan, @"\bif\s*\("))
            {
                var open = scan.IndexOf('(', m.Index);
                var close = MatchParen(scan, open);
                var span = ControlledSpan(scan, close);

                if (index >= span.Item1 && index < span.Item2 && m.Index > innermostStart)
                {
                    innermostStart = m.Index;
                    innermost = source.Substring(open + 1, close - open - 1);
                }
            }

            return innermost;
        }

        #endregion

        #region the bounded pre-materialize wait

        /// <summary>
        /// Reads a `private static readonly TimeSpan Name = TimeSpan.FromX(n);` declaration out of source.
        ///
        /// Reflection would be the obvious way to get these, and it does not work: both fields live on
        /// Player, and touching ANY static member of Player runs its type initializer, which evaluates
        /// Player_Location.cs's MarketplaceDrop field - a live world-database read. In this harness that
        /// throws TypeInitializationException wrapping a MySqlException before any assertion runs. So the
        /// values are parsed from source instead; the ordering they are checked for is still the real one.
        /// </summary>
        private static TimeSpan ReadDeclaredTimeSpan(string sourceRelativePath, string fieldName)
        {
            var source = File.ReadAllText(FindInSourceTree(sourceRelativePath));

            var match = Regex.Match(source, fieldName + @"\s*=\s*TimeSpan\.From(Seconds|Minutes|Hours)\(\s*([0-9.]+)\s*\)");

            Assert.IsTrue(match.Success, $"Could not find a TimeSpan declaration for {fieldName} in {sourceRelativePath}");

            var value = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);

            switch (match.Groups[1].Value)
            {
                case "Seconds": return TimeSpan.FromSeconds(value);
                case "Minutes": return TimeSpan.FromMinutes(value);
                default: return TimeSpan.FromHours(value);
            }
        }

        private static TimeSpan MaxPreMaterializeWait =>
            ReadDeclaredTimeSpan(Path.Combine("Source", "ACE.Server", "WorldObjects", "Player_Location.cs"), "MaxPreMaterializeWait");

        private static TimeSpan MaximumTeleportTime =>
            ReadDeclaredTimeSpan(Path.Combine("Source", "ACE.Server", "WorldObjects", "Player_Tick.cs"), "MaximumTeleportTime");

        /// <summary>
        /// The wait has to give up BEFORE Player_Tick's MaximumTeleportTime backstop fires, because that
        /// backstop does not rescue the player - it logs them off, and relogging into the same landblock
        /// just repeats the wait. If the bound ever grows past it, this fix silently stops doing anything.
        /// </summary>
        [TestMethod]
        public void PreMaterializeWait_ResolvesBeforeTheTeleportLogoffBackstop()
        {
            var wait = MaxPreMaterializeWait;
            var backstop = MaximumTeleportTime;

            Assert.IsTrue(wait < backstop,
                $"MaxPreMaterializeWait ({wait}) must expire before MaximumTeleportTime ({backstop}), or the player is logged off instead of materialized");
        }

        /// <summary>
        /// The opposite failure: a bound short enough to cut a healthy-but-slow landblock load short would
        /// materialize players into a half-populated landblock, which is exactly what the wait exists to
        /// prevent. A normal population is a cached world-db read plus object construction - well under a
        /// second - so anything at or above ten seconds is comfortably clear of it.
        /// </summary>
        [TestMethod]
        public void PreMaterializeWait_IsFarLongerThanAHealthyLandblockLoad()
        {
            var wait = MaxPreMaterializeWait;

            Assert.IsTrue(wait >= TimeSpan.FromSeconds(10),
                $"MaxPreMaterializeWait ({wait}) is short enough to cut a healthy slow landblock load short");
        }

        /// <summary>
        /// The retry re-enqueue must sit inside the bound. Before the fix it was unconditional, which is
        /// what held a player in the pink bubble forever when a landblock's population task faulted.
        ///
        /// Deliberately NOT a token-order check. "MaxPreMaterializeWait appears before AddAction" is
        /// satisfied unchanged by an inverted comparison - retry once the wait is ALREADY over, which
        /// materializes instantly on a healthy load and then loops forever on a genuinely stuck one, the
        /// exact defect this branch exists to remove. So the assertion is on the innermost condition that
        /// actually controls the retry, and on its direction: the retry happens while the elapsed wait is
        /// LESS than the bound.
        /// </summary>
        [TestMethod]
        public void OnTeleportComplete_RetryIsGatedOnTheBound()
        {
            var path = FindInSourceTree(Path.Combine("Source", "ACE.Server", "WorldObjects", "Player_Location.cs"));
            var body = ExtractMethodBody(File.ReadAllText(path), "public void OnTeleportComplete()");

            var retryAt = body.IndexOf("AddAction(this, OnTeleportComplete)", StringComparison.Ordinal);

            Assert.IsTrue(retryAt >= 0, "OnTeleportComplete no longer re-enqueues itself - this test needs updating");

            var condition = EnclosingIfCondition(body, retryAt);

            Assert.IsNotNull(condition, "the retry re-enqueue is not inside any conditional - the wait is unbounded again");
            StringAssert.Contains(condition, "MaxPreMaterializeWait",
                $"the condition controlling the retry does not consult the bound: '{condition}'");

            var boundsTheRetry = Regex.IsMatch(condition, @"<\s*MaxPreMaterializeWait")
                              || Regex.IsMatch(condition, @"MaxPreMaterializeWait\s*>[^=]");

            Assert.IsTrue(boundsTheRetry,
                $"the retry must run while the elapsed wait is LESS than the bound; this condition retries on the wrong side of it: '{condition}'");
        }

        /// <summary>
        /// And the expiry has to be loud. A silent give-up would turn "stuck forever" into "standing in an
        /// empty landblock for no stated reason", which is barely better for whoever reads the logs.
        /// </summary>
        [TestMethod]
        public void OnTeleportComplete_LogsAnErrorWhenTheBoundExpires()
        {
            var path = FindInSourceTree(Path.Combine("Source", "ACE.Server", "WorldObjects", "Player_Location.cs"));
            var body = ExtractMethodBody(File.ReadAllText(path), "public void OnTeleportComplete()");

            StringAssert.Contains(body, "log.Error", "OnTeleportComplete must log an ERROR when the pre-materialize bound expires");
        }

        #endregion

        #region the observed population task

        /// <summary>
        /// The whole defect: Landblock.Init's population task was fire-and-forget, and on .NET 5+ an
        /// unobserved Task exception reaches nothing - there is no TaskScheduler.UnobservedTaskException
        /// handler in this solution (verified by grep over Source/), only Program.cs's
        /// AppDomain.UnhandledException hook, which a Task fault never triggers. So a throw in there was
        /// completely silent while leaving CreateWorldObjectsCompleted false forever.
        /// </summary>
        [TestMethod]
        public void LandblockInit_ObservesThePopulationTaskFault()
        {
            var path = FindInSourceTree(Path.Combine("Source", "ACE.Server", "Entity", "Landblock.cs"));
            var body = ExtractMethodBody(File.ReadAllText(path), "public void Init(bool reload = false)");

            var runAt = body.IndexOf("Task.Run(", StringComparison.Ordinal);

            Assert.IsTrue(runAt >= 0, "Landblock.Init no longer starts the population task with Task.Run - this test needs updating");

            var tail = body.Substring(runAt);

            StringAssert.Contains(tail, "ContinueWith", "the population task must be continued, or its exception is never observed");
            StringAssert.Contains(tail, "OnlyOnFaulted", "the continuation must be faulted-only so it does not run on the success path");
            StringAssert.Contains(tail, "log.Error", "the faulted continuation must log an ERROR");
        }

        /// <summary>
        /// A faulted-only continuation observes the antecedent's exception by reading Task.Exception, which
        /// is what keeps the fault from staying unobserved, and it is skipped (as cancelled) when the
        /// antecedent succeeds. Both halves are load-bearing for the fix and neither is obvious from
        /// reading it, so they are pinned here against the same construction Landblock.Init uses.
        /// </summary>
        [TestMethod]
        public void FaultedOnlyContinuation_RunsOnFaultAndIsSkippedOnSuccess()
        {
            var faultedRan = 0;
            AggregateException observed = null;

            // typed as Action so the Task.Run(Action) / Task.Run(Func<Task>) overloads cannot go ambiguous
            Action boom = () => throw new InvalidOperationException("LandblockInitObservabilityTests: deliberate population failure");

            var faulted = Task.Run(boom)
                .ContinueWith(t => { faultedRan++; observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

            faulted.Wait(TimeSpan.FromSeconds(10));

            Assert.AreEqual(1, faultedRan, "the faulted-only continuation did not run on a faulted antecedent");
            Assert.IsNotNull(observed, "the continuation must read Task.Exception, which is what observes the fault");

            var successRan = 0;

            Action noop = () => { };

            var succeeded = Task.Run(noop)
                .ContinueWith(t => successRan++, TaskContinuationOptions.OnlyOnFaulted);

            // a skipped OnlyOnFaulted continuation completes as Cancelled, so Wait() throws rather than returning
            try
            {
                succeeded.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
            }

            Assert.AreEqual(0, successRan, "the faulted-only continuation must not run when the population task succeeds");
        }

        #endregion

        #region the population debug trail

        /// <summary>
        /// The Debug lines are the forensic trail for "the room was empty" on a prod server running at
        /// DEBUG. Both must stay behind log.IsDebugEnabled so they cost nothing when they are off, and the
        /// completion line must count pendingAdditions as well as worldObjects, because AddWorldObjectInternal
        /// stages new objects in pendingAdditions - counting only worldObjects would report 0 every time.
        ///
        /// Counting guards and separately checking that the expression exists SOMEWHERE in the method is
        /// not enough: lift a Debug line out of its guard while two unrelated guards remain elsewhere and
        /// that check still passes, which is exactly the unconditional interpolation-and-allocation on the
        /// landblock load path the spec forbids. So each line is located and its enclosing condition read.
        /// </summary>
        [TestMethod]
        public void CreateWorldObjects_LogsThePopulationTrailUnderADebugGuard()
        {
            var path = FindInSourceTree(Path.Combine("Source", "ACE.Server", "Entity", "Landblock.cs"));
            var body = ExtractMethodBody(File.ReadAllText(path), "private void CreateWorldObjects()");

            AssertGuardedByIsDebugEnabled(body, "objects built",
                "the population Debug line (realm, row counts, objects built)");

            AssertGuardedByIsDebugEnabled(body, "worldObjects.Count + pendingAdditions.Count",
                "the completion Debug line - which must also count staged additions, or it reports 0 objects on every landblock");
        }

        /// <summary>
        /// Asserts that marker occurs in body and that the statement containing it is controlled by an
        /// `if (log.IsDebugEnabled)`.
        /// </summary>
        private static void AssertGuardedByIsDebugEnabled(string body, string marker, string description)
        {
            var at = body.IndexOf(marker, StringComparison.Ordinal);

            Assert.IsTrue(at >= 0, $"{description} is missing from CreateWorldObjects (looked for '{marker}')");

            var condition = EnclosingIfCondition(body, at);

            Assert.IsNotNull(condition, $"{description} is not inside any conditional - it now costs an interpolation on every landblock load");
            StringAssert.Contains(condition, "log.IsDebugEnabled",
                $"{description} is guarded by the wrong condition: '{condition}'");
        }

        #endregion
    }
}
