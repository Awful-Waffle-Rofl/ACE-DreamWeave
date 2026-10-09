using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using log4net;
using log4net.Config;

using ACE.Common;
using ACE.Database;
using ACE.Database.LoadTest.Scenarios;

namespace ACE.Database.LoadTest
{
    /// <summary>
    /// Standalone synthetic load / benchmark harness for the shard database layer. Runs a single scenario
    /// against a real ACE shard database - point Config.js at a disposable dev/test database, never production.
    /// </summary>
    class Program
    {
        private static readonly List<IScenario> Scenarios = new()
        {
            new IntegrityCheckScenario(),
            new QueueSaturationScenario(),
            new LandblockPopulationScenario(),
            new BiotaRoundTripScenario(),
            new CharacterListScalingScenario(),
            new SeedLandblockScenario(),
            new SeedCharactersScenario(),
            new SeedBulkBiotasScenario(),
            new TypeScanScenario(),
            new RetryCostScenario(),
            new LoginUnderDeleteLoadScenario(),
            new EquipBuffWriteBurstScenario(),
            new SaveBatchCrossoverScenario(),
            new QueueOrderingProbeScenario(),
            new DeepSaveBurstScenario(),
            new PossessionLoadScenario(),
            new AccountCharacterListScenario(),

            // Read-only, and reads the WORLD database (Config.js's MySql.World block), not the shard. Not in the
            // StandardSuite: it measures a world-DB code path the shard suite's reset-baseline fixture does not
            // touch, and its default run takes ~tens of seconds. Run it by hand.
            new WeenieBulkLoadScenario(),
        };

        /// <summary>
        /// Fixed, versioned list run by `suite` so "before" and "after" measurements are always apples-to-apples.
        /// Assumes seed-landblock/seed-characters/seed-bulk have already populated a known baseline (see
        /// reset-baseline.ps1) - suite does not seed for you, so results stay comparable run over run.
        ///
        /// retry-cost is deliberately not included here: it measures genuine MySQL/InnoDB lock-contention timing,
        /// which is inherently noisy run-to-run (observed 12ms-320ms across identical code in testing) and isn't
        /// suited to a tight automated regression threshold. Run it manually as a standalone diagnostic instead
        /// when investigating changes that touch write concurrency.
        ///
        /// type-scan is not included either, and was removed from this list on 2026-07-29: it records no metrics
        /// at all (no Metrics.Record and no LatencyStats.Report anywhere in TypeScanScenario), so it contributed
        /// nothing a compare could judge while costing 2.2s of every suite run. It remains available to run by
        /// hand. Anything added back here must record at least one metric, or it is pure cost.
        ///
        /// deepsave-burst is not included: at its defaults it runs for 74.7s, three times the entire rest of the
        /// suite, and it reports five metrics for each of 28 size/arm/cache combinations at --samples=3 - a
        /// roughly fivefold increase in metric count, almost none of it over the minimum-sample gate. It is a
        /// crossover-finding diagnostic like retry-cost, not a regression gate. Run it by hand.
        ///
        /// account-character-list is not included either, and this was measured rather than assumed. It does
        /// cover a method nothing else here calls - GetCharacters(accountId), which is NOT
        /// character-list-scaling's GetCharacter(characterId) - but against this baseline's fixture it cannot
        /// discriminate anything: seed-characters gives each of its 500 accounts a single character, so the call
        /// returns one row and lands at a p50 of 0.485ms, under the --minMs floor and therefore never flagged,
        /// with a p95 whose identical-build spread is 223.8%. It becomes worth adding only alongside a fixture
        /// with many characters on ONE account (seed-characters --perAccount=N), which would put the call
        /// somewhere the timer can resolve.
        /// </summary>
        private static readonly (string scenario, string[] args)[] StandardSuite =
        {
            ("integrity-check", Array.Empty<string>()),
            ("queue-saturation", new[] { "--count=500", "--concurrency=500", "--propertyGroups=7" }),
            ("landblock-population", new[] { "--landblock=0x7D64", "--iterations=20" }),
            ("biota-roundtrip", new[] { "--propertyGroups=7", "--iterations=50" }),
            ("character-list-scaling", new[] { "--startCharacterId=0x5F000000", "--count=500" }),

            // The genuinely cold possession read, and the real coverage for the path #352 rewrote. It is a
            // read-mode run ONLY: the chain is seeded by reset-baseline.ps1, in a separate process, which is the
            // entire point - a seed in this process would populate the biota cache and the "cold" read would be
            // served from memory. --samples stays at 1 because only the first read is cold; the scenario does not
            // evict between samples, so a second sample would be a cache hit averaged into the same number.
            // Measured cost: 1.6s here, plus 2.6s in reset-baseline.ps1.
            ("possession-load", new[] { "--mode=read", "--samples=1" }),

            // --samples=30, not the scenario's default 5. At 5 samples every metric this scenario produces sits
            // under compare's minimum-sample gate, so the ONLY coverage the login/possession batch-read path has
            // (GetPossessedBiotasInParallel, i.e. GetInventoryInParallel + GetWieldedItemsInParallel - no other
            // scenario calls them) gated nothing at all. Measured cost of 5 -> 30: 4.4s -> 9.9s.
            ("login-under-delete-load", new[] { "--packs=3", "--itemsPerPack=100", "--deletes=600", "--clusterSize=20", "--samples=30" }),

            ("equip-buff-write-burst", new[] { "--items=12", "--iterations=20", "--propertyGroups=7" }),

            // A reduced save-batch-crossover: the full 8-size sweep costs 13.2s, these three cost 9.2s and keep
            // the points that matter - N=1 (the single-item case SerializedShardDatabase deliberately does not
            // route through SaveBiotaBatch), N=12 (a realistic equip burst) and N=100 (exactly
            // ShardDatabase.MaxSaveBiotaBatchSize, the cap boundary a batching change moves first).
            // --iterations=20 sets the sample count behind all 12 of its metrics, comfortably over --minSamples.
            ("save-batch-crossover", new[] { "--sizes=1,12,100", "--iterations=20", "--propertyGroups=7" }),

            ("queue-ordering-probe", new[] { "--pairs=2000" }),
        };

        static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            if (args[0].Equals("suite", StringComparison.OrdinalIgnoreCase))
                return RunSuite(args.Skip(1).ToArray());

            if (args[0].Equals("compare", StringComparison.OrdinalIgnoreCase))
                return RunCompare(args.Skip(1).ToArray());

            if (args[0].Equals("calibrate", StringComparison.OrdinalIgnoreCase))
                return RunCalibrate(args.Skip(1).ToArray());

            return RunSingleScenario(args);
        }

        private static int RunSingleScenario(string[] args)
        {
            var scenario = Scenarios.FirstOrDefault(s => s.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
            if (scenario == null)
            {
                Console.WriteLine($"Unknown scenario '{args[0]}'.");
                PrintUsage();
                return 1;
            }

            var scenarioArgs = new ScenarioArgs(args.Skip(1).ToArray());

            Bootstrap();

            // Guard comes before the --yes/confirmation logic on purpose: --yes is meant to skip a "type YES"
            // prompt for a scenario the operator already knows writes to a real disposable database. It must
            // never be able to skip past a missing Config.js, which means Config.js.example is in play and that
            // file's Shard connection points at ace_shard - the real dev database.
            if (WritesData(scenario) && UsingExampleConfig)
            {
                PrintMissingConfigError();
                return 1;
            }

            if (WritesData(scenario) && !scenarioArgs.GetBool("yes", false) && !ConfirmDestructive())
            {
                Console.WriteLine("Aborted.");
                return 1;
            }

            Console.WriteLine($"Running scenario: {scenario.Name}");
            Console.WriteLine();

            DatabaseManager.Start();
            try
            {
                Metrics.SetScope(scenario.Name);
                scenario.Run(scenarioArgs);
            }
            finally
            {
                DatabaseManager.Stop();
            }

            return ReportFailedGates();
        }

        /// <summary>
        /// Turns any failed correctness gate recorded during this process into a nonzero exit code, and names the
        /// gates that failed.
        ///
        /// Scenarios signal correctness through a ".Passed" metric, which `compare` reads out of the JSON report.
        /// Nothing was translating that into the process exit status, so `integrity-check` printed three
        /// mismatches and still exited 0 - meaning anything wiring these scenarios into CI on exit status would
        /// have silently passed a corrupt shard.
        ///
        /// Deliberately keyed on the ".Passed" suffix rather than on specific scenario types, so it is one rule
        /// rather than a list to keep in sync: it already covers integrity-check's three gates and
        /// queue-ordering-probe's, and it will cover any correctness scenario added later for free. It is the
        /// same convention BenchmarkComparer uses to find gates, kept identical on purpose.
        ///
        /// It does NOT catch retry-cost, whose "failed" calls are the expected outcome it exists to measure
        /// rather than a scenario failure - that scenario records no gate, which is correct.
        /// </summary>
        private static int ReportFailedGates()
        {
            var failed = Metrics.Snapshot()
                .Where(kvp => kvp.Key.EndsWith(".Passed", StringComparison.Ordinal) && kvp.Value.Value != 1)
                .Select(kvp => kvp.Key)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            if (failed.Count == 0)
                return 0;

            Console.WriteLine();
            Console.WriteLine($"FAILED: {failed.Count} correctness gate(s) did not pass:");
            foreach (var key in failed)
                Console.WriteLine($"  - {key}");

            return 1;
        }

        private static bool WritesData(IScenario scenario) =>
            scenario is QueueSaturationScenario or BiotaRoundTripScenario or SeedLandblockScenario
                or SeedCharactersScenario or SeedBulkBiotasScenario or RetryCostScenario or IntegrityCheckScenario
                or LoginUnderDeleteLoadScenario or EquipBuffWriteBurstScenario or SaveBatchCrossoverScenario
                or QueueOrderingProbeScenario or DeepSaveBurstScenario or PossessionLoadScenario;

        /// <summary>
        /// Runs the fixed StandardSuite end to end and writes one JSON report. Args: --label=&lt;name&gt;
        /// (should be the git commit sha under test) --out=&lt;path&gt; (default results/&lt;label&gt;.json) --yes
        /// </summary>
        private static int RunSuite(string[] rawArgs)
        {
            var suiteArgs = new ScenarioArgs(rawArgs);
            var label = suiteArgs.GetString("label", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            var outPath = suiteArgs.GetString("out", Path.Combine("results", $"{label}.json"));

            Bootstrap();

            // The suite always writes data, so the missing-Config.js guard always applies here - see the comment
            // in RunSingleScenario for why this must come before --yes.
            if (UsingExampleConfig)
            {
                PrintMissingConfigError();
                return 1;
            }

            if (!suiteArgs.GetBool("yes", false) && !ConfirmDestructive())
            {
                Console.WriteLine("Aborted.");
                return 1;
            }

            Console.WriteLine($"Running standard suite, label='{label}'...");
            Console.WriteLine();

            Metrics.Reset();

            DatabaseManager.Start();
            try
            {
                foreach (var (scenarioName, scenarioArgTokens) in StandardSuite)
                {
                    var scenario = Scenarios.First(s => s.Name == scenarioName);

                    Console.WriteLine($"--- {scenarioName} ---");
                    Metrics.SetScope(scenarioName);
                    scenario.Run(new ScenarioArgs(scenarioArgTokens));
                    Console.WriteLine();
                }
            }
            finally
            {
                DatabaseManager.Stop();
            }

            var report = new BenchmarkReport
            {
                Label = label,
                TimestampUtc = DateTime.UtcNow,
                Metrics = new Dictionary<string, MetricValue>(Metrics.Snapshot())
            };

            var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine($"Wrote {outPath}");

            // The report is written either way - a failed gate must still produce a comparable report - but the
            // exit status reflects it, for the same reason a single scenario run does. calibrate deliberately
            // checks for the report file rather than this exit code, so a suite whose gate fails still yields a
            // usable calibration run.
            return ReportFailedGates();
        }

        /// <summary>
        /// Diffs two suite JSON reports. Does not touch the database - reads two files and prints deltas.
        /// Args: --baseline=&lt;path&gt; --candidate=&lt;path&gt; --threshold=&lt;percent, default 30&gt;
        /// --minMs=&lt;default 1&gt; --minSamples=&lt;default 10&gt; --minSamplesForP95=&lt;default 20&gt;
        /// --calibration=&lt;path|none&gt; --calibrationSafety=&lt;default 1.5&gt;
        ///
        /// All of the judging lives in BenchmarkComparer (pure, unit tested); this method only parses args,
        /// loads files and prints. Any ".Passed" metric that regresses from passing to failing is a hard fail
        /// regardless of threshold.
        /// </summary>
        private static int RunCompare(string[] rawArgs)
        {
            var compareArgs = new ScenarioArgs(rawArgs);
            var baselinePath = compareArgs.GetString("baseline", null);
            var candidatePath = compareArgs.GetString("candidate", null);

            if (baselinePath == null || candidatePath == null)
            {
                Console.WriteLine("Usage: compare --baseline=<path> --candidate=<path> [--threshold=30] [--minMs=1]");
                Console.WriteLine("               [--minSamples=10] [--minSamplesForP95=20] [--calibration=<path>|none] [--calibrationSafety=1.5]");
                return 1;
            }

            var options = new CompareOptions
            {
                ThresholdPercent = compareArgs.GetDouble("threshold", 30),
                MinMs = compareArgs.GetDouble("minMs", 1),
                MinSamples = compareArgs.GetInt("minSamples", 10),
                MinSamplesForP95 = compareArgs.GetInt("minSamplesForP95", 20),
                CalibrationSafetyFactor = compareArgs.GetDouble("calibrationSafety", 1.5),
                MinCalibratedThresholdPercent = compareArgs.GetDouble("minCalibratedThreshold", 5),
                CalibrationCanTighten = compareArgs.GetBool("calibrationCanTighten", false),
            };

            var calibrationPath = compareArgs.GetString("calibration", DefaultCalibrationPath);

            if (!string.Equals(calibrationPath, "none", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(calibrationPath))
                {
                    options.Calibration = JsonSerializer.Deserialize<CalibrationProfile>(File.ReadAllText(calibrationPath));
                }
                else if (compareArgs.GetString("calibration", null) != null)
                {
                    Console.WriteLine($"Calibration profile '{calibrationPath}' not found.");
                    return 1;
                }
            }

            var baseline = JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(baselinePath));
            var candidate = JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(candidatePath));

            var result = BenchmarkComparer.Compare(baseline, candidate, options);

            ComparisonPrinter.Print(result, options);

            return result.Passed ? 0 : 1;
        }

        private const string DefaultCalibrationPath = "results/calibration.json";

        /// <summary>
        /// Measures this machine's real noise floor: runs reset-baseline.ps1 + the standard suite K times against
        /// the SAME build, then writes the observed per-metric run-to-run spread to a calibration profile that
        /// `compare` uses in place of one flat percentage.
        ///
        /// Each run is a fresh child process, exactly as a human would run it - that keeps the calibration
        /// measuring the same thing an ordinary before/after measures (process startup, JIT, connection pool and
        /// InnoDB buffer state all included) instead of a warmer, quieter in-process repeat.
        ///
        /// The profile is built from EVERY run report accumulated under results/calibration-runs, across sittings,
        /// not just from the runs this invocation happened to take. That is deliberate and it is the fix for a
        /// measured defect: a single sitting's K back-to-back runs sample a window of a few minutes, while the
        /// machine drifts on a longer timescale, so a one-sitting profile understated the real spread by 1.0x to
        /// 5.2x (see CALIBRATION.md). Each sitting writes its own subdirectory so the history is additive.
        ///
        /// Args: --runs=&lt;K, default 5&gt; --out=&lt;path, default results/calibration.json&gt;
        /// --resetScript=&lt;path to reset-baseline.ps1&gt; --label=&lt;name&gt; --yes
        /// --aggregate (rebuild the profile from accumulated reports without running any suites)
        /// --from=&lt;dir, default results/calibration-runs&gt; (where accumulated reports are read from)
        ///
        /// The result is MACHINE SPECIFIC and goes stale: regenerate it after a hardware, MySQL or OS change, and
        /// whenever a scenario's arguments change (different sample counts mean a different noise floor) - and in
        /// that case clear the accumulated directory first, because runs taken under different scenario arguments
        /// are not comparable.
        /// </summary>
        private static int RunCalibrate(string[] rawArgs)
        {
            var calibrateArgs = new ScenarioArgs(rawArgs);
            var runs = calibrateArgs.GetInt("runs", 5);
            var outPath = calibrateArgs.GetString("out", DefaultCalibrationPath);
            var label = calibrateArgs.GetString("label", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            var aggregateOnly = calibrateArgs.GetBool("aggregate", false);

            var accumulatedRoot = calibrateArgs.GetString("from",
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".", "calibration-runs"));

            if (aggregateOnly)
                return BuildProfileFromAccumulated(accumulatedRoot, outPath, label);

            if (runs < 2)
            {
                Console.WriteLine("calibrate needs --runs=2 or more; a single run has no spread to measure.");
                return 1;
            }

            var resetScript = calibrateArgs.GetString("resetScript", null) ?? FindResetScript();

            if (resetScript == null || !File.Exists(resetScript))
            {
                Console.WriteLine(
                    "Could not find reset-baseline.ps1 (looked next to the executable and in every parent " +
                    "directory). Pass --resetScript=<path>.");
                return 1;
            }

            Bootstrap();

            if (!calibrateArgs.GetBool("yes", false) && !ConfirmDestructive())
            {
                Console.WriteLine("Aborted.");
                return 1;
            }

            var selfPath = Environment.ProcessPath;
            if (selfPath == null)
            {
                Console.WriteLine("Could not determine this executable's path; calibrate needs it to spawn suite runs.");
                return 1;
            }

            // One subdirectory per sitting, never overwritten, so profiles can be built across sittings and a
            // later cross-sitting analysis is still possible. Flattening these into one directory previously
            // destroyed the per-run history of every earlier calibration.
            var runDir = Path.Combine(accumulatedRoot, label);
            Directory.CreateDirectory(runDir);

            Console.WriteLine($"Calibrating over {runs} identical-build suite runs (reset-baseline before each).");
            Console.WriteLine($"Reset script: {resetScript}");
            Console.WriteLine();

            var reportPaths = new List<string>();

            for (var run = 1; run <= runs; run++)
            {
                var reportPath = Path.Combine(runDir, $"run{run}.json");

                Console.WriteLine($"=== run {run}/{runs}: reset-baseline ===");
                if (!RunChild("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", resetScript }))
                {
                    Console.WriteLine("reset-baseline.ps1 failed - aborting calibration.");
                    return 1;
                }

                Console.WriteLine($"=== run {run}/{runs}: suite ===");
                RunChild(selfPath, new[] { "suite", $"--label={label}-run{run}", $"--out={reportPath}", "--yes" });

                if (!File.Exists(reportPath))
                {
                    Console.WriteLine($"Suite run {run} produced no report at {reportPath} - aborting calibration.");
                    return 1;
                }

                reportPaths.Add(reportPath);
                Console.WriteLine();
            }

            Console.WriteLine($"This sitting's {reportPaths.Count} runs are in {runDir}.");
            Console.WriteLine();

            return BuildProfileFromAccumulated(accumulatedRoot, outPath, label);
        }

        /// <summary>
        /// Builds the calibration profile from every suite report found under `root`, recursively - all sittings,
        /// not just the latest. Files that are not suite reports are skipped rather than failing the build, so the
        /// directory can hold notes or a stray profile without breaking aggregation.
        /// </summary>
        private static int BuildProfileFromAccumulated(string root, string outPath, string label)
        {
            if (!Directory.Exists(root))
            {
                Console.WriteLine($"No accumulated calibration runs at '{root}'. Run `calibrate --runs=K --yes` first.");
                return 1;
            }

            var reports = new List<BenchmarkReport>();
            var sittings = new SortedSet<string>(StringComparer.Ordinal);
            var skipped = 0;

            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                BenchmarkReport report = null;

                try
                {
                    report = JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(path));
                }
                catch (JsonException)
                {
                    // Not a suite report - ignore it.
                }

                if (report?.Metrics == null || report.Metrics.Count == 0)
                {
                    skipped++;
                    continue;
                }

                reports.Add(report);
                sittings.Add(Path.GetFileName(Path.GetDirectoryName(path)) ?? "(root)");
            }

            if (reports.Count < 2)
            {
                Console.WriteLine($"Found only {reports.Count} usable report(s) under '{root}'; need at least 2 to measure a spread.");
                return 1;
            }

            var profile = CalibrationProfile.Build(reports, label, Environment.MachineName);

            var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            File.WriteAllText(outPath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine(
                $"Wrote {outPath}: {profile.Entries.Count} metric statistics from {reports.Count} accumulated runs " +
                $"across {sittings.Count} sitting(s) on '{profile.Machine}'." +
                (skipped > 0 ? $" Skipped {skipped} non-report file(s)." : ""));
            Console.WriteLine($"Sittings: {string.Join(", ", sittings)}");
            Console.WriteLine();
            Console.WriteLine("Widest observed noise floors (identical build, so all of this is noise):");

            foreach (var entry in profile.Entries.OrderByDescending(e => e.Value.ObservedSpreadPercent).Take(15))
            {
                Console.WriteLine(
                    $"  {entry.Key,-64} {entry.Value.ObservedSpreadPercent,8:N1}%  " +
                    $"(min {entry.Value.Min:N2}, median {entry.Value.Median:N2}, max {entry.Value.Max:N2}, n={entry.Value.SampleCount})");
            }

            Console.WriteLine();
            Console.WriteLine("This profile is machine specific. Regenerate it on any hardware/MySQL change or after");
            Console.WriteLine("changing a scenario's arguments, and keep the per-run reports in calibration-runs/ as evidence.");

            return 0;
        }

        /// <summary>
        /// Walks up from the executable towards the repository root looking for reset-baseline.ps1, which lives in
        /// the project directory rather than the build output (it resolves the exe relative to its own location).
        /// </summary>
        private static string FindResetScript()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "reset-baseline.ps1");

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Runs a child process, streaming its output through. Returns whether it exited 0 - note that suite runs
        /// are known to exit non-zero while printing a perfectly valid report, so callers check for the report
        /// file rather than trusting this.
        /// </summary>
        private static bool RunChild(string fileName, string[] arguments)
        {
            var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            process.WaitForExit();

            return process.ExitCode == 0;
        }

        private static bool ConfirmDestructive()
        {
            var shardConfig = ConfigManager.Config.MySql.Shard;
            Console.WriteLine($"This writes synthetic test rows to shard database " +
                               $"'{shardConfig.Database}' on {shardConfig.Host}:{shardConfig.Port}.");
            Console.Write("This must NOT be a production database. Type YES to continue: ");
            return Console.ReadLine() == "YES";
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  ACE.Database.LoadTest <scenario> [--key=value ...]           Run one scenario");
            Console.WriteLine("  ACE.Database.LoadTest suite [--label=x] [--out=path]         Run the fixed standard suite, write a JSON report");
            Console.WriteLine("  ACE.Database.LoadTest compare --baseline=a.json --candidate=b.json [--threshold=30] [--minMs=1]   Diff two reports, no DB needed");
            Console.WriteLine("      [--minSamples=10] [--minSamplesForP95=20] [--calibration=<path>|none] [--calibrationSafety=1.5]");
            Console.WriteLine("  ACE.Database.LoadTest calibrate [--runs=5] [--label=x] --yes | calibrate --aggregate   Measure/rebuild per-metric noise floors");
            Console.WriteLine();
            Console.WriteLine("Scenarios:");
            foreach (var scenario in Scenarios)
                Console.WriteLine($"  {scenario.Name,-24} {scenario.Description}");
            Console.WriteLine();
            Console.WriteLine("Pass --yes to skip the confirmation prompt on scenarios/suite runs that write data.");
            Console.WriteLine("Requires a Config.js next to the executable, pointed at a disposable dev/test shard database.");
        }

        // Set by Bootstrap() when Config.js was absent and it fell back to Config.js.example. That file points its
        // Shard connection at ace_shard - the real dev database - so any data-writing scenario run without a real
        // Config.js would silently write synthetic rows into it. Config.js is gitignored, so it is absent in any
        // fresh clone or git worktree; this is exactly the situation that guard exists for.
        private static bool UsingExampleConfig;

        private static void Bootstrap()
        {
            var exeLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            var log4netFileInfo = new FileInfo(Path.Combine(exeLocation!, "log4net.config"));
            if (!log4netFileInfo.Exists)
                log4netFileInfo = new FileInfo(Path.Combine(exeLocation!, "log4net.config.example"));

            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
            XmlConfigurator.ConfigureAndWatch(logRepository, log4netFileInfo);

            var configFile = Path.Combine(exeLocation!, "Config.js");
            UsingExampleConfig = !File.Exists(configFile);
            if (UsingExampleConfig)
                configFile = Path.Combine(exeLocation!, "Config.js.example");

            ConfigManager.Initialize(configFile);

            DatabaseManager.Initialize(autoRetry: false);

            if (DatabaseManager.InitializationFailure)
                throw new Exception("DatabaseManager failed to initialize - check the log and Config.js.");
        }

        private static void PrintMissingConfigError()
        {
            Console.WriteLine("ERROR: No Config.js found next to the executable.");
            Console.WriteLine("Config.js is gitignored, so a fresh clone or git worktree will not have one.");
            Console.WriteLine("This run fell back to Config.js.example, whose Shard connection points at");
            Console.WriteLine("'ace_shard' - the real dev shard database, not a disposable one.");
            Console.WriteLine();
            Console.WriteLine("Create a Config.js next to the executable pointed at a disposable test database");
            Console.WriteLine("(e.g. ace_shard_loadtest) before running a scenario that writes data.");
        }
    }
}
