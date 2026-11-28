using System;
using System.Collections.Generic;
using System.IO;

using ACE.Entity.Enum;
using ACE.Server.BalanceTrials;
using ACE.Server.ClassAbilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Pins the archetype schema. Inline JSON only: this class is published to the public mirror and never reads tools/.
    /// Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class TrialArchetypeLoaderTests
    {
        private const string Minimal = @"{
  ""id"": ""archmage-400"", ""slot"": 0, ""class"": ""Archmage"", ""targetLevel"": 400,
  ""anchor"": { ""rule"": ""topByClassSpend"", ""class"": ""Archmage"", ""minLevel"": 350, ""maxLevel"": 400, ""rank"": 1, ""minShare"": 0.5 },
  ""liftUp"": {
    ""reference"": [""Rogue"", ""Berserker"", ""VoidSummon""],
    ""cap"": { ""percentile"": ""p50"", ""ranks"": {}, ""tolerance"": 0.1 },
    ""gear"": [], ""augs"": {}, ""facets"": ""keep"", ""consumables"": []
  },
  ""buffProfile"": ""ownSpellbook""
}";

        private static string With(string from, string to)
        {
            Assert.IsTrue(Minimal.Contains(from), $"fixture has no '{from}'");
            return Minimal.Replace(from, to);
        }

        private static void Rejects(string json, string fragment)
        {
            var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialArchetypeLoader.Parse(json));
            StringAssert.Contains(ex.Message, fragment);
        }

        private static string AnyAbilityName()
        {
            foreach (var d in ClassAbilityRegistry.Abilities.Values)
                return d.Name;
            throw new InvalidOperationException("no abilities");
        }

        [TestMethod]
        public void Rejects_a_class_with_a_trailing_newline()
        {
            // JSON "Archmage\n" is the class name plus a newline: the regex must anchor at the true end (\z, not $).
            Rejects(With("\"class\": \"Archmage\", \"targetLevel\"", "\"class\": \"Archmage\\n\", \"targetLevel\""), "is not a class");
        }

        [TestMethod]
        public void Rejects_a_gear_slot_with_a_trailing_newline()
        {
            string Gear(string slot) => With(@"""gear"": []", $@"""gear"": [{{ ""slot"": ""{slot}"", ""source"": ""any"", ""pick"": ""p50 armorLevel"" }}]");

            // Positive control: the plain member parses.
            Assert.AreEqual(EquipMask.HeadWear, TrialArchetypeLoader.Parse(Gear("HeadWear")).LiftUp.Gear[0].Slot);

            // JSON "HeadWear\n" is the member name plus a newline: the regex must anchor at the true end (\z, not $).
            Rejects(Gear(@"HeadWear\n"), "is not a single EquipMask member");
        }

        [TestMethod]
        public void Parses_the_minimal_archetype()
        {
            var a = TrialArchetypeLoader.Parse(Minimal);
            Assert.AreEqual("archmage-400", a.Id);
            Assert.AreEqual(0, a.Slot);
            Assert.AreEqual(ClassAbilityClass.Archmage, a.Class);
            Assert.AreEqual(400, a.TargetLevel);
            Assert.AreEqual("topByClassSpend", a.Anchor.Rule);
            Assert.AreEqual(ClassAbilityClass.Archmage, a.Anchor.Class);
            Assert.AreEqual(350, a.Anchor.MinLevel);
            Assert.AreEqual(400, a.Anchor.MaxLevel);
            Assert.AreEqual(1, a.Anchor.Rank);
            Assert.AreEqual(0.5, a.Anchor.MinShare);
            CollectionAssert.AreEqual(new[] { ClassAbilityClass.Rogue, ClassAbilityClass.Berserker, ClassAbilityClass.VoidSummon }, new List<ClassAbilityClass>(a.LiftUp.Reference));
            Assert.AreEqual("p50", a.LiftUp.Cap.Percentile);
            Assert.AreEqual(0, a.LiftUp.Cap.Ranks.Count);
            Assert.AreEqual(0.1, a.LiftUp.Cap.Tolerance);
            Assert.AreEqual(0, a.LiftUp.Gear.Count);
            Assert.AreEqual(0, a.LiftUp.Augs.Count);
            Assert.AreEqual("keep", a.LiftUp.Facets);
            Assert.AreEqual(0, a.LiftUp.Consumables.Count);
            Assert.AreEqual("ownSpellbook", a.BuffProfile);
        }

        [TestMethod]
        public void Parses_gear_augs_consumables_and_ranks()
        {
            var ability = AnyAbilityName();
            var json = Minimal
                .Replace(@"""ranks"": {}", $@"""ranks"": {{ ""{ability}"": 1 }}")
                .Replace(@"""gear"": []", @"""gear"": [{ ""slot"": ""ChestArmor"", ""source"": ""class:Archmage"", ""pick"": ""p75 armorLevel"" }, { ""slot"": ""Cloak"", ""source"": ""any"", ""pick"": ""p50 gearRatingSum"" }]")
                .Replace(@"""augs"": {}", @"""augs"": { ""AugmentationBonusXp"": 3, ""LumAugVitality"": 5 }")
                .Replace(@"""consumables"": []", @"""consumables"": [{ ""wcid"": 5, ""stack"": 20 }]")
                .Replace("ownSpellbook", "adminAll:8");
            var a = TrialArchetypeLoader.Parse(json);
            Assert.AreEqual(1, a.LiftUp.Cap.Ranks[ability]);
            Assert.AreEqual(EquipMask.ChestArmor, a.LiftUp.Gear[0].Slot);
            Assert.AreEqual("p50 gearRatingSum", a.LiftUp.Gear[1].Pick);
            Assert.AreEqual(3, a.LiftUp.Augs["AugmentationBonusXp"]);
            Assert.AreEqual(5u, a.LiftUp.Consumables[0].Wcid);
            Assert.AreEqual(20, a.LiftUp.Consumables[0].Stack);
            Assert.AreEqual("adminAll:8", a.BuffProfile);
        }

        [TestMethod]
        public void Rejects_unknown_class_slot_out_of_range_and_bad_percentile()
        {
            Rejects(With(@"""class"": ""Archmage"", ""targetLevel""", @"""class"": ""Wizard"", ""targetLevel"""), "class 'Wizard' is not a class");
            Rejects(With(@"""class"": ""Archmage"", ""targetLevel""", @"""class"": ""None"", ""targetLevel"""), "class 'None' is not a class");
            Rejects(With(@"""class"": ""Archmage"", ""targetLevel""", @"""class"": ""1"", ""targetLevel"""), "class '1' is not a class");
            Rejects(With(@"""class"": ""Archmage"", ""targetLevel""", @"""class"": "" Archmage"", ""targetLevel"""), "class ' Archmage' is not a class");
            Rejects(With(@"""Rogue""", @"""2"""), "liftUp.reference[0] '2' is not a class");
            Rejects(With(@"""slot"": 0", @"""slot"": 32"), "slot 32 is outside");
            Rejects(With(@"""slot"": 0", @"""slot"": -1"), "slot -1 is outside");
            Rejects(With(@"""percentile"": ""p50""", @"""percentile"": ""p51"""), "liftUp.cap.percentile 'p51'");
            Rejects(With(@"""Rogue""", @"""Wizard"""), "liftUp.reference[0] 'Wizard' is not a class");
        }

        [TestMethod]
        public void Rejects_comma_lists_and_signed_numbers_as_class_or_slot_names()
        {
            Rejects(With(@"""class"": ""Archmage"", ""targetLevel""", @"""class"": ""Archer,Rogue"", ""targetLevel"""), "class 'Archer,Rogue' is not a class");
            Rejects(With(@"""Rogue""", @"""Archer,Rogue"""), "liftUp.reference[0] 'Archer,Rogue' is not a class");
            Rejects(With(@"""gear"": []", @"""gear"": [{ ""slot"": ""ChestArmor"", ""source"": ""class:Archer,Rogue"", ""pick"": ""p75 armorLevel"" }]"), "source 'Archer,Rogue' is not a class");
            Rejects(With(@"""gear"": []", @"""gear"": [{ ""slot"": ""ChestArmor,HeadWear"", ""source"": ""any"", ""pick"": ""p75 armorLevel"" }]"), "slot 'ChestArmor,HeadWear' is not");
            Rejects(With(@"""class"": ""Archmage"", ""targetLevel""", @"""class"": "" Archmage"", ""targetLevel"""), "is not a class");
            Rejects(With(@"""class"": ""Archmage"", ""targetLevel""", @"""class"": ""+1"", ""targetLevel"""), "class '+1' is not a class");
            Rejects(With(@"""class"": ""Archmage"", ""targetLevel""", @"""class"": ""1"", ""targetLevel"""), "class '1' is not a class");
            TrialArchetypeLoader.Parse(With(@"""class"": ""Archmage"", ""targetLevel""", @"""class"": ""Archer"", ""targetLevel"""));
        }

        [TestMethod]
        public void Rejects_an_unknown_ability_in_ranks()
        {
            Rejects(With(@"""ranks"": {}", @"""ranks"": { ""NotAnAbility"": 1 }"), "unknown ability");
            Rejects(With(@"""ranks"": {}", $@"""ranks"": {{ ""{AnyAbilityName()}"": 999 }}"), $"liftUp.cap.ranks.{AnyAbilityName()} 999 is outside");
        }

        [TestMethod]
        public void Rejects_an_unknown_gear_slot_or_pick_metric()
        {
            string Gear(string slot, string pick) =>
                With(@"""gear"": []", $@"""gear"": [{{ ""slot"": ""{slot}"", ""source"": ""any"", ""pick"": ""{pick}"" }}]");

            TrialArchetypeLoader.Parse(Gear("ChestArmor", "p75 armorLevel"));
            Rejects(Gear("Chest", "p75 armorLevel"), "slot 'Chest' is not");
            Rejects(Gear("2", "p75 armorLevel"), "slot '2' is not");
            Rejects(Gear("ChestArmor, Cloak", "p75 armorLevel"), "slot 'ChestArmor, Cloak' is not");
            Rejects(Gear("ChestArmor", "p75 shininess"), "pick 'p75 shininess'");
            Rejects(Gear("ChestArmor", "p80 armorLevel"), "pick 'p80 armorLevel'");
            Rejects(Gear("ChestArmor", "armorLevel"), "pick 'armorLevel'");
            Rejects(With(@"""gear"": []", @"""gear"": [{ ""slot"": ""ChestArmor"", ""source"": ""guild"", ""pick"": ""p75 armorLevel"" }]"), "source 'guild'");
            var withClassAny = Gear("ChestArmor", "p75 armorLevel").Replace(@"""source"": ""any""", @"""source"": ""class:Archmage|any""");
            Assert.AreEqual("class:Archmage|any", TrialArchetypeLoader.Parse(withClassAny).LiftUp.Gear[0].Source);
            Assert.AreEqual("class:Archmage", TrialArchetypeLoader.Parse(Gear("ChestArmor", "p75 armorLevel").Replace(@"""source"": ""any""", @"""source"": ""class:Archmage""")).LiftUp.Gear[0].Source);
            Rejects(Gear("ChestArmor", "p75 armorLevel").Replace(@"""source"": ""any""", @"""source"": ""class:Archmage|other"""), "source 'Archmage|other' is not a class");
            Rejects(Gear("ChestArmor", "p75 armorLevel").Replace(@"""source"": ""any""", @"""source"": ""class:|any"""), "source '' is not a class");
        }

        [TestMethod]
        public void Rejects_an_aug_name_not_starting_Augmentation_or_LumAug()
        {
            Rejects(With(@"""augs"": {}", @"""augs"": { ""Strength"": 1 }"), "Augmentation or LumAug");
            TrialArchetypeLoader.Parse(With(@"""augs"": {}", @"""augs"": { ""LumAugVitality"": 1 }"));
        }

        [TestMethod]
        public void Rejects_unknown_keys_and_malformed_json()
        {
            Rejects(With(@"""slot"": 0,", @"""slot"": 0, ""checksum"": ""x"","), "has unknown key 'checksum'");
            Rejects(With(@"""facets"": ""keep""", @"""facets"": ""maybe"""), "liftUp.facets 'maybe'");
            Rejects(With(@"""ownSpellbook""", @"""adminAll:9"""), "buffProfile 'adminAll:9'");
            Rejects("{ nope", "not valid JSON");
        }

        [TestMethod]
        public void Rejects_MaxLevel_above_TargetLevel()
        {
            Rejects(With(@"""maxLevel"": 400", @"""maxLevel"": 401"), "anchor.maxLevel 401 is above targetLevel 400");
            TrialArchetypeLoader.Parse(With(@"""maxLevel"": 400", @"""maxLevel"": 399"));
        }

        [TestMethod]
        public void Rejects_MinLevel_below_275_or_more_than_50_under_target()
        {
            Assert.AreEqual(50, TrialArchetypeLoader.MaxLevelLift);
            Rejects(With(@"""minLevel"": 350", @"""minLevel"": 349"), "anchor.minLevel 349 is below 350");

            // Target 300: the 50-level rule would allow 250, the base-cap floor of 275 binds instead.
            var low = Minimal.Replace(@"""targetLevel"": 400", @"""targetLevel"": 300").Replace(@"""maxLevel"": 400", @"""maxLevel"": 300");
            Rejects(low.Replace(@"""minLevel"": 350", @"""minLevel"": 274"), "anchor.minLevel 274 is below 275");
            TrialArchetypeLoader.Parse(low.Replace(@"""minLevel"": 350", @"""minLevel"": 275"));
            var under = Minimal.Replace(@"""targetLevel"": 400", @"""targetLevel"": 270").Replace(@"""maxLevel"": 400", @"""maxLevel"": 270").Replace(@"""minLevel"": 350", @"""minLevel"": 270");
            Rejects(under, "targetLevel 270 is below 275");
        }

        [TestMethod]
        public void Accepts_an_anchor_window_below_target()
        {
            var a = TrialArchetypeLoader.Parse(Minimal);
            Assert.AreEqual(400, a.TargetLevel);
            Assert.AreEqual(350, a.Anchor.MinLevel);
            Assert.AreEqual(400, a.Anchor.MaxLevel);
        }

        [TestMethod]
        public void LoadDirectory_rejects_duplicate_slot_or_mismatched_file_name()
        {
            var dir = Path.Combine(Path.GetTempPath(), "trial-archetypes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "archmage-400.json"), Minimal);
                var loaded = TrialArchetypeLoader.LoadDirectory(dir);
                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual("archmage-400", loaded[0].Archetype.Id);
                Assert.AreEqual(TrialArchetypeLoader.Sha256(Minimal), loaded[0].Sha256);

                // Same slot, different id.
                File.WriteAllText(Path.Combine(dir, "archmage-401.json"), Minimal.Replace("archmage-400", "archmage-401"));
                var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialArchetypeLoader.LoadDirectory(dir));
                StringAssert.Contains(ex.Message, "slot 0 is used by both");

                // Different slot, file name differs from the id.
                File.WriteAllText(Path.Combine(dir, "archmage-401.json"), Minimal.Replace(@"""slot"": 0", @"""slot"": 1"));
                ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialArchetypeLoader.LoadDirectory(dir));
                StringAssert.Contains(ex.Message, "file name");

                File.Delete(Path.Combine(dir, "archmage-401.json"));
                File.WriteAllText(Path.Combine(dir, "other.json"), Minimal.Replace(@"""slot"": 0", @"""slot"": 1"));
                ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialArchetypeLoader.LoadDirectory(dir));
                StringAssert.Contains(ex.Message, "file name");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void Sha256_ignores_whitespace_and_key_order_but_not_values()
        {
            var a = @"{""a"": 1, ""b"": {""x"": [1, 2], ""y"": ""s""}}";
            var b = "{\n  \"b\": { \"y\": \"s\", \"x\": [1,2] },\n  \"a\": 1\n}";
            var c = @"{""a"": 2, ""b"": {""x"": [1, 2], ""y"": ""s""}}";
            var d = @"{""a"": 1, ""b"": {""x"": [2, 1], ""y"": ""s""}}";
            Assert.AreEqual(TrialArchetypeLoader.Sha256(a), TrialArchetypeLoader.Sha256(b));
            Assert.AreNotEqual(TrialArchetypeLoader.Sha256(a), TrialArchetypeLoader.Sha256(c));
            Assert.AreNotEqual(TrialArchetypeLoader.Sha256(a), TrialArchetypeLoader.Sha256(d));
            Assert.AreEqual(64, TrialArchetypeLoader.Sha256(a).Length);
            // Pinned golden of the canonical text {"a":1,"b":{"x":[1,2],"y":"s"}}.
            Assert.AreEqual(
                System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(@"{""a"":1,""b"":{""x"":[1,2],""y"":""s""}}"))).ToLowerInvariant(),
                TrialArchetypeLoader.Sha256(a));
        }
    }
}
