using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// THE SHIPPED-CONTENT INVARIANT behind the press's finished-gem summary (owner ruling, 2026-09-07):
    /// EVERY modifier in Content/dungeons/dynamic/modifiers.json must render a DESCRIBED effect, never
    /// ThreadDungeonGemHandler.RenderEffect's raw-number fallback.
    ///
    /// WHY THIS FILE EXISTS AT ALL, since the ruling was about wording. The summary now says what each
    /// modifier DOES rather than printing a bare magnitude, and it gets that wording from the one place the
    /// knowledge lives (TryDescribeEffect). That method answers "I cannot word this" by returning false, and
    /// both its callers then print the raw number - which is the right behaviour for a modifier row a future
    /// build adds before the switch catches up, and is exactly the wrong thing to SHIP. A row that falls
    /// through does not fail, does not log, and does not look broken: it prints "  Teeming - 1.4", which is
    /// precisely the line the ruling replaced. So the failure mode of this feature is silent regression to
    /// its own predecessor, and the only thing that catches it is enumerating the shipped file.
    ///
    /// It enumerates rather than lists. A test naming the twenty-two rows shipped today would pass forever
    /// while a twenty-third went undescribed, which is the case that actually needs catching - the switch is
    /// keyed on monsterEffectKind, and a new kind is one JSON field away.
    /// </summary>
    [TestClass]
    public class DungeonGemSummaryEffectTests
    {
        private static IReadOnlyDictionary<string, ModifierDef> ShippedModifiers()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();

            Assert.IsTrue(store.Modifiers.Count > 0, "the shipped modifier file loaded no rows");

            return store.Modifiers;
        }

        /// <summary>
        /// The invariant itself. Every shipped row is describable, asserted through the same predicate
        /// RenderEffect branches on rather than by inspecting its output, so the failure message names the row
        /// and its kind instead of quoting a string someone then has to reverse-engineer.
        /// </summary>
        [TestMethod]
        public void Every_shipped_modifier_renders_a_described_effect()
        {
            var undescribed = ShippedModifiers().Values
                .Where(def => !ThreadDungeonGemHandler.HasDescribedEffect(def))
                .Select(def => $"{def.Id} (monsterEffectKind '{def.MonsterEffectKind}')")
                .ToList();

            Assert.AreEqual(0, undescribed.Count,
                "these shipped modifiers fall through to the raw-magnitude fallback and need a case in "
                + "ThreadDungeonGemHandler.TryDescribeEffect:\n" + string.Join("\n", undescribed));
        }

        /// <summary>
        /// THE SAME CLAIM FROM THE OTHER SIDE, and it is not redundant with the one above: this one reads the
        /// rendered TEXT, so it also catches a case that returns true and then words the effect as nothing but
        /// the number anyway. Every row is rendered at its own minimum, midpoint and maximum, because a
        /// formatValue can be number-only at one magnitude and not at another (a rounded percentage of a small
        /// fraction being the obvious way).
        ///
        /// "Not the raw fallback" is tested as "the line contains a letter", which is what distinguishes
        /// "monsters attack with +35 damage rating, hitting harder" from "35".
        /// </summary>
        [TestMethod]
        public void No_shipped_modifier_renders_as_a_bare_number()
        {
            foreach (var def in ShippedModifiers().Values)
            {
                foreach (var magnitude in Magnitudes(def))
                {
                    var rendered = ThreadDungeonGemHandler.RenderEffect(def, magnitude);

                    Assert.IsTrue(rendered.Any(char.IsLetter),
                        $"{def.Id} at magnitude {magnitude.ToString(CultureInfo.InvariantCulture)} rendered as "
                        + $"'{rendered}', which is the raw-magnitude fallback rather than a described effect");

                    Assert.AreNotEqual(magnitude.ToString("0.##", CultureInfo.InvariantCulture), rendered,
                        $"{def.Id} rendered as its bare magnitude");
                }
            }
        }

        /// <summary>
        /// No shipped row is numberless any longer. "hollow" rolled 1-100% (owner ruling 2026-09-17) lost its
        /// exemption along with its fixed magnitude, so this set is empty; it stays as a named set rather than
        /// being deleted outright so a future numberless row has an obvious place to register itself.
        /// </summary>
        private static readonly HashSet<string> NumberlessById = new HashSet<string>();

        /// <summary>
        /// EVERY RENDERED LINE CARRIES ITS MAGNITUDE. Not asserted by re-deriving the formatter's own output -
        /// that would be circular and would pass against a formatter that printed a constant - but by the two
        /// properties a magnitude-carrying line actually has: it contains a digit, and rendering the SAME row
        /// at two different magnitudes produces two different strings. The second is what proves the number in
        /// the text is this modifier's number rather than something baked into the wording.
        ///
        /// A row whose min equals its max cannot satisfy the second property and is exempt from it alone (it
        /// has one magnitude, so there is no pair to differ); no shipped row is exempt from the digit test.
        /// </summary>
        [TestMethod]
        public void Every_shipped_modifier_renders_its_magnitude()
        {
            foreach (var def in ShippedModifiers().Values)
            {
                var atMin = ThreadDungeonGemHandler.RenderEffect(def, def.MinMagnitude);
                var atMax = ThreadDungeonGemHandler.RenderEffect(def, def.MaxMagnitude);

                if (NumberlessById.Contains(def.Id))
                {
                    Assert.IsFalse(atMin.Any(char.IsDigit),
                        $"{def.Id} is the documented numberless row; if it now prints a number, this exemption is wrong");
                    continue;
                }

                Assert.IsTrue(atMin.Any(char.IsDigit), $"{def.Id} rendered '{atMin}' with no number in it");
                Assert.IsTrue(atMax.Any(char.IsDigit), $"{def.Id} rendered '{atMax}' with no number in it");

                if (def.MaxMagnitude > def.MinMagnitude)
                    Assert.AreNotEqual(atMin, atMax,
                        $"{def.Id} renders the same text at {def.MinMagnitude} and {def.MaxMagnitude}, so the "
                        + "magnitude in the line is not this modifier's magnitude");
            }
        }

        /// <summary>
        /// EVERY RENDERED LINE READS AS A COMPLETE CLAUSE, per kind, against the table below. This is the test
        /// that would have caught "48% elites" and "23% per kill" - both of which pass every structural check
        /// above (they contain letters, they contain the magnitude, they do not fall through) and still fail
        /// to tell a player what the effect is.
        ///
        /// A substring table rather than whole expected strings, because the magnitude sits inside the line
        /// and pinning the whole thing would make this a duplicate of
        /// The_summary_of_a_shipped_gem_reads_as_specified with 22 rows instead of 3. What each entry pins is
        /// the CLAUSE - the words that state the consequence - which is the part the ruling is about.
        ///
        /// Every kind present in the shipped file must appear here: an unlisted kind fails rather than being
        /// skipped, so adding a modifier of a new kind cannot quietly opt out of the wording standard.
        /// </summary>
        [TestMethod]
        public void Every_shipped_modifier_reads_as_a_complete_clause()
        {
            var clauseByKind = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DungeonRewardMath.HealthMult] = " health",
                [DungeonRewardMath.DamageRating] = " damage rating, hitting harder",
                [DungeonRewardMath.CritRating] = " critical strike rating, landing more critical hits",
                [DungeonRewardMath.CritDamageRating] = " critical damage rating, so ",
                [DungeonRewardMath.DamageResistRating] = " damage resist rating, taking less damage from you",
                [DungeonRewardMath.CountMult] = " more monsters",
                [DungeonRewardMath.EliteShare] = " of the run's monsters are elites",
                [DungeonRewardMath.RunSpeedMult] = "monsters move at ",
                [DungeonRewardMath.SalvageAffinity] = " of kills leave ",
                [DungeonRewardMath.IgnoreShield] = "monsters ignore ",
                [DungeonRewardMath.Hollow] = "monsters ignore ",
                [DungeonRewardMath.LootQuantity] = "kills drop ",

                // The reward-slope rows, which share monsterEffectKind "none" and are told apart by which
                // slope they carry - so they are keyed here the same way RenderEffect tells them apart.
                ["none:luminance"] = "kills pay ",
                ["none:experience"] = "kills pay ",
            };

            foreach (var def in ShippedModifiers().Values)
            {
                var kind = string.IsNullOrEmpty(def.MonsterEffectKind) ? "none" : def.MonsterEffectKind;

                if (kind == "none")
                    kind = def.RewardLumSlope != 0 ? "none:luminance" : "none:experience";

                Assert.IsTrue(clauseByKind.TryGetValue(kind, out var clause),
                    $"{def.Id} carries kind '{kind}', which this wording table does not cover - add its clause "
                    + "here as well as its case in ThreadDungeonGemHandler.TryDescribeEffect");

                // At the row's midpoint, so the clause is asserted around a real rolled magnitude rather than
                // at an edge a formatter might special-case.
                var midpoint = Math.Round((def.MinMagnitude + def.MaxMagnitude) / 2.0, 2);
                var rendered = ThreadDungeonGemHandler.RenderEffect(def, midpoint);

                StringAssert.Contains(rendered, clause, $"{def.Id} ('{kind}') rendered '{rendered}'");

                // A clause, not a label: the line must say something beyond the modifier's own display name.
                Assert.IsTrue(rendered.Split(' ').Length >= 3,
                    $"{def.Id} rendered '{rendered}', which is too terse to read as a clause");
            }
        }

        /// <summary>
        /// A SALVAGE AFFINITY NAMES ITS MATERIAL, over all eight shipped rows, and the name comes from the
        /// row's SalvageMaterial id rather than from its Display - so a row whose display text were reworded
        /// would still describe itself correctly, and a row whose material id were wrong would be caught here
        /// rather than reading plausibly forever.
        ///
        /// Asserted against MaterialType's own name for the row's id, lower-cased and space-split, so this
        /// cannot pass by the renderer echoing the Display back.
        /// </summary>
        [TestMethod]
        public void Every_shipped_affinity_names_the_material_from_its_own_material_id()
        {
            var affinities = ShippedModifiers().Values
                .Where(d => d.MonsterEffectKind == DungeonRewardMath.SalvageAffinity)
                .ToList();

            Assert.AreEqual(8, affinities.Count, "the shipped file carries eight salvage affinities");

            foreach (var def in affinities)
            {
                var expected = ExpectedMaterialWords(def.SalvageMaterial);
                var rendered = ThreadDungeonGemHandler.RenderEffect(def, 17);

                Assert.AreEqual($"17% of kills leave {expected} to salvage", rendered, def.Id);
            }
        }

        /// <summary>MaterialType's own member name for an id, lower-cased and split on camel case.</summary>
        private static string ExpectedMaterialWords(int materialType)
        {
            Assert.IsTrue(Enum.IsDefined(typeof(ACE.Entity.Enum.MaterialType), (uint)materialType),
                $"material {materialType} is not a defined MaterialType");

            var name = ((ACE.Entity.Enum.MaterialType)materialType).ToString();
            var words = System.Text.RegularExpressions.Regex.Replace(name, "(?<!^)([A-Z])", " $1");

            return words.ToLowerInvariant();
        }

        /// <summary>
        /// THE CONTROL, without which the two tests above could pass because HasDescribedEffect had been
        /// changed to return true for everything. A kind this build genuinely does not know must still return
        /// false and still fall back to the raw number - that path is load-bearing for a gem written by a
        /// later build, and deleting it would throw rather than degrade.
        /// </summary>
        [TestMethod]
        public void An_unknown_effect_kind_still_falls_back_to_the_raw_magnitude()
        {
            var unknown = new ModifierDef { Id = "future_mod", Display = "Future", MonsterEffectKind = "not_a_kind_this_build_knows" };

            Assert.IsFalse(ThreadDungeonGemHandler.HasDescribedEffect(unknown));
            Assert.AreEqual("1.27", ThreadDungeonGemHandler.RenderEffect(unknown, 1.27));

            // And a null def, which is what an unresolvable modifier id on a spec produces.
            Assert.IsFalse(ThreadDungeonGemHandler.HasDescribedEffect(null));
            Assert.AreEqual("1.27", ThreadDungeonGemHandler.RenderEffect(null, 1.27));
        }

        /// <summary>
        /// The six kinds the ruling called out by name are shipped and described, checked against the file
        /// rather than against a list of ids: if one of these kinds ever leaves modifiers.json the guard says
        /// so instead of passing vacuously.
        /// </summary>
        [TestMethod]
        public void The_kinds_named_in_the_ruling_are_present_in_the_shipped_file_and_described()
        {
            var byKind = ShippedModifiers().Values
                .GroupBy(d => d.MonsterEffectKind ?? "none", StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var named = new[]
            {
                DungeonRewardMath.SalvageAffinity, DungeonRewardMath.LootQuantity, DungeonRewardMath.CountMult,
                DungeonRewardMath.EliteShare, DungeonRewardMath.RunSpeedMult, DungeonRewardMath.DamageResistRating,
            };

            foreach (var kind in named)
            {
                Assert.IsTrue(byKind.ContainsKey(kind), $"no shipped modifier carries monsterEffectKind '{kind}'");

                foreach (var def in byKind[kind])
                    Assert.IsTrue(ThreadDungeonGemHandler.HasDescribedEffect(def), $"{def.Id} ('{kind}') is undescribed");
            }
        }

        /// <summary>
        /// The whole summary, end to end on shipped rows: what a player actually reads. Pinned as exact
        /// strings because this is the deliverable of the ruling, and a change to any of these three
        /// wordings should be a decision rather than a diff nobody noticed.
        /// </summary>
        [TestMethod]
        public void The_summary_of_a_shipped_gem_reads_as_specified()
        {
            var modifiers = ShippedModifiers();

            var spec = new DungeonGemSpec("any", 245, 5, "any", 1,
                new List<(string Id, double Magnitude)> { ("bounteous", 1.27), ("affinity_green_garnet", 17.6), ("stalwart", 35) },
                0, 0, 1, null, null);

            var lines = DungeonGemNarrator.ComposeSummary(spec, 3, modifiers);

            CollectionAssert.AreEqual(
                new[]
                {
                    "The gem carries:",
                    "  Bounteous - kills drop x1.27 loot",
                    "  Green Garnet Affinity - 18% of kills leave green garnet to salvage",
                    "  Stalwart - monsters have +35 damage resist rating, taking less damage from you",
                    "Level 245, 3 entries.",
                },
                lines.ToArray());
        }

        /// <summary>Minimum, midpoint and maximum of a row's declared range.</summary>
        private static IEnumerable<double> Magnitudes(ModifierDef def)
        {
            yield return def.MinMagnitude;
            yield return Math.Round((def.MinMagnitude + def.MaxMagnitude) / 2.0, 2);
            yield return def.MaxMagnitude;
        }
    }
}
