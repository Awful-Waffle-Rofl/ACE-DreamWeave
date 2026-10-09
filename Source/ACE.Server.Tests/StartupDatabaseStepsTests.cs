using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="ACE.Server.Program.PlanStartupDatabaseSteps"/>, the pure decision helper
    /// that decides what boot-time database work runs and in what order (Program_DbUpdates.cs). No database
    /// or filesystem is touched by those tests - the helper is a pure function of its four inputs.
    ///
    /// Background (2026-09-17 stage outage): content auto-apply used to be nested inside
    /// AutoUpdateWorldDatabase, and content ran BEFORE schema migrations. Stage therefore had to leave the
    /// upstream world auto-updater on in order to get its own content applied; upstream published world
    /// v0.9.295, the boot updater reimported it, that import recreated the whole ace_world schema and dropped
    /// all seven fork-added tables, and the stale World applied-updates ledger then told the boot patcher
    /// that every migration which would have rebuilt them was already applied. The server died on the first
    /// query against ace_world.realm and restart-looped.
    ///
    /// The last test in this class is a CI pin on the deployed config templates rather than on the helper.
    /// </summary>
    [TestClass]
    public class StartupDatabaseStepsTests
    {
        private const string ProdTemplateRelativePath = "deploy/prod/Config.js.template";
        private const string StageTemplateRelativePath = "deploy/stage/Config.js.template";

        private const string WhyTheUpdaterStaysOff =
            "An unattended upstream base-world reimport recreates the entire ace_world schema and DROPS every "
            + "fork-added table: realm, realm_landblock_rule, landblock_instance_realm, "
            + "landblock_instance_link_realm, sky_decor_region, speed_season, content_apply_ledger. That is "
            + "what took stage down on 2026-09-17. It also silently moves an environment onto a base world "
            + "version the other environment is not running, which destroys stage's value as prod's rehearsal. "
            + "A base world upgrade is a deliberate maintenance operation in both environments. If you are "
            + "turning this flag back on on purpose, update this test on purpose as part of the same change.";

        private static List<Program.StartupDatabaseStep> Plan(bool autoUpdateWorldDatabase, bool autoApplyDatabaseUpdates, bool autoApplyWorldCustomizations, bool worldDatabaseWasReimported)
        {
            return Program.PlanStartupDatabaseSteps(autoUpdateWorldDatabase, autoApplyDatabaseUpdates, autoApplyWorldCustomizations, worldDatabaseWasReimported);
        }

        /// <summary>
        /// The decoupling, and stage's configuration after the 2026-09-17 fix: fork content is applied at
        /// boot while the upstream world auto-updater is off. Before the fix this combination applied no
        /// content at all, because customizations were nested inside AutoUpdateWorldDatabase.
        /// </summary>
        [TestMethod]
        public void CustomizationsRunWhenTheWorldAutoUpdaterIsOff_StageConfiguration()
        {
            var steps = Plan(autoUpdateWorldDatabase: false, autoApplyDatabaseUpdates: true, autoApplyWorldCustomizations: true, worldDatabaseWasReimported: false);

            CollectionAssert.AreEqual(
                new List<Program.StartupDatabaseStep>
                {
                    Program.StartupDatabaseStep.ApplyDatabaseUpdates,
                    Program.StartupDatabaseStep.ApplyWorldCustomizations
                },
                steps,
                "Stage keeps fork content auto-apply on with the upstream world auto-updater off. "
                + "AutoApplyWorldCustomizations must not depend on AutoUpdateWorldDatabase.");
        }

        /// <summary>
        /// Prod-safety pin. Prod runs AutoUpdateWorldDatabase false, AutoApplyWorldCustomizations false,
        /// AutoApplyDatabaseUpdates true, and this change must leave that combination doing exactly what it
        /// did before: schema migrations only, no reimport, no content apply, no ledger clear.
        /// </summary>
        [TestMethod]
        public void ProdConfiguration_RunsMigrationsOnly_NoCustomizationsAndNoLedgerClear()
        {
            var steps = Plan(autoUpdateWorldDatabase: false, autoApplyDatabaseUpdates: true, autoApplyWorldCustomizations: false, worldDatabaseWasReimported: false);

            CollectionAssert.AreEqual(
                new List<Program.StartupDatabaseStep> { Program.StartupDatabaseStep.ApplyDatabaseUpdates },
                steps,
                "Prod's flag combination must run the schema migrations and nothing else.");

            Assert.IsFalse(steps.Contains(Program.StartupDatabaseStep.ClearWorldAppliedUpdatesLedger), "Prod must never clear the World applied-updates ledger: with AutoUpdateWorldDatabase off, no upstream reimport can have invalidated it.");
            Assert.IsFalse(steps.Contains(Program.StartupDatabaseStep.ApplyWorldCustomizations), "Prod's content set comes from the prod content manifest, never from a boot-time auto-apply.");
        }

        [TestMethod]
        public void MigrationsPrecedeCustomizations_InEveryCombinationThatRunsBoth()
        {
            // Every (autoUpdateWorldDatabase, worldDatabaseWasReimported) pair, including the inconsistent
            // one, with both apply flags on.
            var inputPairs = new List<(bool AutoUpdate, bool Reimported)>
            {
                (false, false),
                (false, true),
                (true, false),
                (true, true)
            };

            foreach (var (autoUpdate, reimported) in inputPairs)
            {
                var steps = Plan(autoUpdate, autoApplyDatabaseUpdates: true, autoApplyWorldCustomizations: true, worldDatabaseWasReimported: reimported);

                var migrationsIndex = steps.IndexOf(Program.StartupDatabaseStep.ApplyDatabaseUpdates);
                var customizationsIndex = steps.IndexOf(Program.StartupDatabaseStep.ApplyWorldCustomizations);

                Assert.AreNotEqual(-1, migrationsIndex, $"Migrations must run for autoUpdate={autoUpdate}, reimported={reimported}.");
                Assert.AreNotEqual(-1, customizationsIndex, $"Customizations must run for autoUpdate={autoUpdate}, reimported={reimported}.");

                Assert.IsTrue(
                    migrationsIndex < customizationsIndex,
                    $"Schema migrations must be ordered before content customizations (autoUpdate={autoUpdate}, reimported={reimported}). "
                    + "Content that needs a fork table would otherwise run a boot ahead of the migration that creates it, "
                    + "which is what produced 68 failed-dependency content units on every stage boot.");
            }
        }

        [TestMethod]
        public void LedgerIsClearedWhenAndOnlyWhenAReimportHappened()
        {
            // The only case that clears it: the updater ran AND it actually reimported.
            Assert.IsTrue(
                Plan(autoUpdateWorldDatabase: true, autoApplyDatabaseUpdates: true, autoApplyWorldCustomizations: true, worldDatabaseWasReimported: true)
                    .Contains(Program.StartupDatabaseStep.ClearWorldAppliedUpdatesLedger),
                "A completed upstream reimport drops every fork table, so the World ledger listing those migrations as applied is stale and must be cleared.");

            // Updater ran, nothing was reimported (already current, no update needed, or the download failed).
            Assert.IsFalse(
                Plan(autoUpdateWorldDatabase: true, autoApplyDatabaseUpdates: true, autoApplyWorldCustomizations: true, worldDatabaseWasReimported: false)
                    .Contains(Program.StartupDatabaseStep.ClearWorldAppliedUpdatesLedger),
                "No reimport means the World schema is untouched and the ledger is still accurate.");

            // Updater disabled. The reimported flag cannot be true here; if a caller ever passes an
            // inconsistent pair, the plan must fail closed rather than clear a valid ledger.
            Assert.IsFalse(
                Plan(autoUpdateWorldDatabase: false, autoApplyDatabaseUpdates: true, autoApplyWorldCustomizations: true, worldDatabaseWasReimported: false)
                    .Contains(Program.StartupDatabaseStep.ClearWorldAppliedUpdatesLedger),
                "With the updater off there is no reimport, so there is nothing to clear.");

            Assert.IsFalse(
                Plan(autoUpdateWorldDatabase: false, autoApplyDatabaseUpdates: true, autoApplyWorldCustomizations: true, worldDatabaseWasReimported: true)
                    .Contains(Program.StartupDatabaseStep.ClearWorldAppliedUpdatesLedger),
                "With the updater off, a stray worldDatabaseWasReimported=true must not clear the ledger.");
        }

        [TestMethod]
        public void LedgerClearIsOrderedBeforeMigrations()
        {
            var steps = Plan(autoUpdateWorldDatabase: true, autoApplyDatabaseUpdates: true, autoApplyWorldCustomizations: true, worldDatabaseWasReimported: true);

            CollectionAssert.AreEqual(
                new List<Program.StartupDatabaseStep>
                {
                    Program.StartupDatabaseStep.ClearWorldAppliedUpdatesLedger,
                    Program.StartupDatabaseStep.ApplyDatabaseUpdates,
                    Program.StartupDatabaseStep.ApplyWorldCustomizations
                },
                steps,
                "After a reimport the ledger has to be cleared before the patcher reads it, or the migrations "
                + "that rebuild the fork schema are skipped for the rest of the boot.");
        }

        [TestMethod]
        public void LedgerIsStillClearedWhenMigrationsAndCustomizationsAreBothDisabled()
        {
            // Nothing rebuilds the schema this boot, but the ledger is provably wrong now, so it must not be
            // left behind claiming migrations are applied that the reimport just dropped.
            var steps = Plan(autoUpdateWorldDatabase: true, autoApplyDatabaseUpdates: false, autoApplyWorldCustomizations: false, worldDatabaseWasReimported: true);

            CollectionAssert.AreEqual(
                new List<Program.StartupDatabaseStep> { Program.StartupDatabaseStep.ClearWorldAppliedUpdatesLedger },
                steps);
        }

        [TestMethod]
        public void NothingRunsWhenEveryFlagIsOff()
        {
            var steps = Plan(autoUpdateWorldDatabase: false, autoApplyDatabaseUpdates: false, autoApplyWorldCustomizations: false, worldDatabaseWasReimported: false);

            Assert.AreEqual(0, steps.Count, "With every Offline flag off, boot does no database work at all.");
        }

        /// <summary>
        /// CI pin on the deployed configuration templates: neither environment may ship with the upstream
        /// world auto-updater on. AutoApplyWorldCustomizations is deliberately NOT pinned here - prod is
        /// false and stage is true, and that difference is policy, not an invariant.
        /// </summary>
        [TestMethod]
        public void DeployedConfigTemplatesKeepTheUpstreamWorldAutoUpdaterOff()
        {
            AssertWorldAutoUpdaterIsOff(ProdTemplateRelativePath);
            AssertWorldAutoUpdaterIsOff(StageTemplateRelativePath);
        }

        private static void AssertWorldAutoUpdaterIsOff(string relativePath)
        {
            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}. This test pins a deployed config value and must never pass without reading the file. {WhyTheUpdaterStaysOff}");

            // Skip comment lines so the prose above the setting cannot satisfy or confuse the match.
            var settingLines = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("//") && l.Contains("\"AutoUpdateWorldDatabase\""))
                .ToList();

            Assert.AreEqual(1, settingLines.Count, $"Expected exactly one \"AutoUpdateWorldDatabase\" setting line in {relativePath}, found {settingLines.Count}. A missing key is a failure, not a pass: the deployed template would fall back to whatever the server default happens to be. {WhyTheUpdaterStaysOff}");

            StringAssert.Contains(settingLines[0], "\"AutoUpdateWorldDatabase\": false", $"{relativePath} must ship with \"AutoUpdateWorldDatabase\": false. Found: {settingLines[0]}. {WhyTheUpdaterStaysOff}");
        }

        /// <summary>
        /// Walks up from the test output directory looking for relativePath, so the lookup works regardless
        /// of the bin\Debug vs bin\x64\Debug output layout (same idiom as DbUpdateScriptIdempotencyTests).
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
