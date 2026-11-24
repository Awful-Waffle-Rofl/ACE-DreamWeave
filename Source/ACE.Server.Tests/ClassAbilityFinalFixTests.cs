using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>Final-review fixes: Mana Rush scaled max, Spellsurge per-stack gear label, Empowered Summons two riders, 0 on a gear ceiling.</summary>
    [TestClass]
    public class ClassAbilityFinalFixTests
    {
        [TestInitialize]
        public void Init() => DefaultPropertyManager.LoadDefaultProperties();

        private static ClassAbilityDefinition Def(ClassAbilityId id) => ClassAbilityRegistry.Abilities[id];

        private static ITunableValues Dict(string key, double value) =>
            new DictionaryTunableValues(new Dictionary<string, double> { [key] = value });

        [TestMethod]
        public void ManaRush_StaticPaths_SayTheMaxIsRaisedByLoyalty()
        {
            var def = Def(ClassAbilityId.NetherRush);
            var v = ClassAbilityText.Defaults;
            var c = def.Ceilings.Single(x => x.Id == "total");

            StringAssert.Contains(ClassAbilityCeilingText.StaticPhrase(def, c, v), "max cast speed bonus 50%, raised by Loyalty");
            StringAssert.Contains(ClassAbilityCeilingText.InfoLines(def, 0, v)[0], "raised by Loyalty");
            StringAssert.Contains(ClassAbilityCeilingText.InfoLines(def, 2, v)[0], "raised by Loyalty");
            StringAssert.Contains(ClassAbilityCeilingText.Sentence(def, v), "raised by Loyalty");

            var feed = ClassAbilityDescriptionFeed.Build(v).Abilities.Single(a => a.Id == def.Name);
            var dto = feed.Ceilings.Single(x => x.Id == "total");
            StringAssert.Contains(dto.Text, "raised by Loyalty");
            Assert.AreEqual(true, dto.Scales);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(feed, MarketApiHost.Json));
            Assert.IsTrue(doc.RootElement.GetProperty("ceilings")[0].TryGetProperty("scales", out _));
        }

        [TestMethod]
        public void NonScalingCeiling_HasNoSuffix_AndNoScalesField()
        {
            var parry = Def(ClassAbilityId.Parry);
            var v = ClassAbilityText.Defaults;
            Assert.IsFalse(ClassAbilityCeilingText.Sentence(parry, v).Contains("raised by"));
            var dto = ClassAbilityDescriptionFeed.Build(v).Abilities.Single(a => a.Id == parry.Name).Ceilings[0];
            Assert.IsNull(dto.Scales);
            Assert.IsNull(ClassAbilityPanelModel.CeilingRows(parry, v)[0].Scales);
            Assert.AreEqual(true, ClassAbilityPanelModel.CeilingRows(Def(ClassAbilityId.NetherRush), v).Single(r => r.Id == "total").Scales);
        }

        [TestMethod]
        public void Spellsurge_GearCeiling_SaysPerStack()
        {
            var def = Def(ClassAbilityId.Spellsurge);
            var sentence = ClassAbilityCeilingText.Sentence(def, ClassAbilityText.Defaults);
            StringAssert.Contains(sentence, "max from gear per stack");
            Assert.IsTrue(def.Ceilings.All(c => c.Label.Length <= 40));
        }

        [TestMethod]
        public void EmpoweredSummons_RateLine_QuotesBothRiders_ThornsUnchanged()
        {
            var v = ClassAbilityText.Defaults;
            var line = ClassAbilityCeilingText.AffinityRateLine(Def(ClassAbilityId.EmpoweredSummons), v);
            StringAssert.Contains(line, "Leadership adds 9%");
            StringAssert.Contains(line, "Loyalty adds +1% pet duration per ");
            StringAssert.Contains(line, "skill (trained) / per ");
            Assert.IsTrue(line.IndexOf("Leadership") < line.IndexOf("Loyalty"), line);

            var thorns = ClassAbilityCeilingText.AffinityRateLine(Def(ClassAbilityId.Thorns), v);
            Assert.IsFalse(thorns.Contains(";"), thorns);
            Assert.IsFalse(thorns.Contains("pet duration"), thorns);
            StringAssert.StartsWith(thorns, ACE.Entity.Enum.SkillExtensions.ToSentence(Def(ClassAbilityId.Thorns).AffinitySkill.Value) + " adds +1% per ");

            // The feed's structured fields keep describing the explicit rider; text carries the joined line.
            var rates = ClassAbilityDescriptionFeed.Build(v).Abilities.Single(a => a.Id == Def(ClassAbilityId.EmpoweredSummons).Name).Rates;
            Assert.AreEqual("additive", rates.Model);
            Assert.AreEqual(line, rates.Text);
        }

        [TestMethod]
        public void GearCeiling_AtZeroPotencyScale_IsARealZeroBound_NotNoLimit()
        {
            var def = Def(ClassAbilityId.AcidProc);
            var zero = Dict("equipment_mod_potency_scale", 0.0);
            var gear = def.Ceilings.Single(c => c.Source == CeilingSource.GearStackCap);
            var reading = ClassAbilityCeilings.Read(def, gear.Id, 0.0, 1, zero);
            Assert.AreEqual(0.0, reading.Max, 1e-9);
            Assert.IsFalse(ClassAbilityCeilingText.IsUnlimited(def, reading));

            var readout = new ClassAbilityReadout { HasValue = true, Ceilings = new[] { reading } };
            StringAssert.Contains(ClassAbilityCeilingText.CompactReadingLine(def, readout), "0/0%");
            StringAssert.Contains(ClassAbilityCeilingText.ReadingLine(def, readout), "0% / 0%");

            var row = ClassAbilityPanelModel.ToReadoutRow(readout, def);
            Assert.AreEqual(0.0, row.Ceilings.Single().Max);
        }
    }
}
