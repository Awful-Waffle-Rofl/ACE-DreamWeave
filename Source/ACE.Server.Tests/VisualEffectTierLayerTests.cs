using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A weapon-mod tier aura takes the single 9000 slot, but the weenie template's own 9000 element look must
    /// still reach clients: VisualEffectManager.PersistentScripts yields the instance script and then the
    /// template base script when they differ. These pin what is emitted, and that SendTo consumes it.
    /// The template is read through WeaponModManager.TemplateWeenieReader, stubbed and restored in finally.
    /// </summary>
    [TestClass]
    public class VisualEffectTierLayerTests
    {
        private const uint BaseScript = 0x33001154;

        private static uint nextGuid = 0x7E9B0000;
        private static uint nextWcid = 993950;

        private static (WorldObject weapon, Weenie template) MakeWeapon(uint? baseScript, uint? setup = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.CombatUse, (int)CombatUse.Melee },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Weapon" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint>(),
            };

            if (baseScript.HasValue)
                weenie.PropertiesDID[PropertyDataId.VisualEffectScript] = baseScript.Value;

            if (setup.HasValue)
                weenie.PropertiesDID[PropertyDataId.Setup] = setup.Value;

            return (new Clothing(weenie, new ObjectGuid(nextGuid++)), weenie);
        }

        private static List<uint> Scripts(WorldObject weapon, Weenie template) =>
            WithReader(wcid => wcid == template.WeenieClassId ? template : null, () => VisualEffectManager.PersistentScripts(weapon)).ToList();

        private static IReadOnlyList<uint> WithReader(Func<uint, Weenie> reader, Func<IReadOnlyList<uint>> body)
        {
            var original = WeaponModManager.TemplateWeenieReader;
            try
            {
                WeaponModManager.TemplateWeenieReader = reader;
                return body();
            }
            finally
            {
                WeaponModManager.TemplateWeenieReader = original;
            }
        }

        [TestMethod]
        public void TierAuraOverTemplateBase_RebuildEmitsAuraThenBase()
        {
            var (weapon, template) = MakeWeapon(BaseScript);
            var aura = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional).Value;
            Assert.AreNotEqual(BaseScript, aura);

            weapon.VisualEffectScript = aura;

            CollectionAssert.AreEqual(new List<uint> { aura, BaseScript }, Scripts(weapon, template));
        }

        [TestMethod]
        public void NoTier_BaseOnly_EmittedOnce()
        {
            var (weapon, template) = MakeWeapon(BaseScript);

            CollectionAssert.AreEqual(new List<uint> { BaseScript }, Scripts(weapon, template));
        }

        [TestMethod]
        public void NoTemplateScript_Unchanged_NoneWithoutTier()
        {
            var (weapon, template) = MakeWeapon(null);

            Assert.AreEqual(0, Scripts(weapon, template).Count);
        }

        [TestMethod]
        public void TierAuraWithNoTemplateScript_AuraOnly()
        {
            var (weapon, template) = MakeWeapon(null);
            var aura = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional).Value;
            weapon.VisualEffectScript = aura;

            CollectionAssert.AreEqual(new List<uint> { aura }, Scripts(weapon, template));
        }

        [TestMethod]
        public void NonTierStampedScript_DifferentFromTemplate_SendsOnlyItsOwn()
        {
            const uint stamped = 0x33000F00;
            Assert.IsFalse(WeaponQualityTiers.IsTierScript(stamped), "the stamped script must not be a tier aura or the test proves nothing");

            var (weapon, template) = MakeWeapon(BaseScript);
            weapon.VisualEffectScript = stamped;

            CollectionAssert.AreEqual(new List<uint> { stamped }, Scripts(weapon, template));
        }

        [TestMethod]
        public void TierAura_InstanceSetupDiffersFromTemplate_OnlyTheAura()
        {
            // A tailored weapon has a new look, so the template's element effect no longer belongs to it.
            var (weapon, template) = MakeWeapon(BaseScript, setup: 0x02000001);
            var aura = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional).Value;
            weapon.VisualEffectScript = aura;
            weapon.SetupTableId = 0x02000002;

            CollectionAssert.AreEqual(new List<uint> { aura }, Scripts(weapon, template));
        }

        [TestMethod]
        public void TierAura_InstanceSetupEqualsTemplate_LayersBase()
        {
            var (weapon, template) = MakeWeapon(BaseScript, setup: 0x02000001);
            var aura = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional).Value;
            weapon.VisualEffectScript = aura;

            CollectionAssert.AreEqual(new List<uint> { aura, BaseScript }, Scripts(weapon, template));
        }

        [TestMethod]
        public void TemplateScriptEqualsTheAura_SentOnce()
        {
            var aura = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional).Value;
            var (weapon, template) = MakeWeapon(aura);
            weapon.VisualEffectScript = aura;

            CollectionAssert.AreEqual(new List<uint> { aura }, Scripts(weapon, template));
        }

        [TestMethod]
        public void ZeroNineThousand_NothingEmitted_EvenWithATemplateScript()
        {
            var (weapon, template) = MakeWeapon(BaseScript);
            weapon.VisualEffectScript = 0;

            Assert.AreEqual(0, Scripts(weapon, template).Count);
        }

        [TestMethod]
        public void TemplateReaderReturnsNull_AuraOnly()
        {
            var (weapon, _) = MakeWeapon(BaseScript);
            var aura = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional).Value;
            weapon.VisualEffectScript = aura;

            var result = WithReader(wcid => null, () => VisualEffectManager.PersistentScripts(weapon));

            CollectionAssert.AreEqual(new List<uint> { aura }, result.ToList());
        }

        [TestMethod]
        public void TemplateReaderThrows_AuraOnly()
        {
            var (weapon, _) = MakeWeapon(BaseScript);
            var aura = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional).Value;
            weapon.VisualEffectScript = aura;

            var result = WithReader(wcid => throw new InvalidOperationException("no world db"), () => VisualEffectManager.PersistentScripts(weapon));

            CollectionAssert.AreEqual(new List<uint> { aura }, result.ToList());
        }

        [TestMethod]
        public void ScratchScripts_AreDedupedAgainstTheEmittedSet_AndOthersStillSend()
        {
            var (weapon, template) = MakeWeapon(BaseScript);
            var aura = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional).Value;
            const uint extra = 0x33000F11;
            weapon.VisualEffectScript = aura;

            try
            {
                VisualEffectManager.Attach(weapon.Guid.Full, BaseScript);
                VisualEffectManager.Attach(weapon.Guid.Full, aura);
                VisualEffectManager.Attach(weapon.Guid.Full, extra);

                var result = WithReader(wcid => wcid == template.WeenieClassId ? template : null, () => VisualEffectManager.ScriptsToEmit(weapon));

                CollectionAssert.AreEqual(new List<uint> { aura, BaseScript, extra }, result.ToList());
            }
            finally
            {
                VisualEffectManager.Clear(weapon.Guid.Full);
            }
        }

        [TestMethod]
        public void SendTo_ConsumesScriptsToEmit_InsideTheLoop()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Source", "ACE.Server", "Entity", "VisualEffectManager.cs")))
                dir = Path.GetDirectoryName(dir);
            Assert.IsNotNull(dir, "could not find the repo root by walking up from the test directory");

            var text = File.ReadAllText(Path.Combine(dir, "Source", "ACE.Server", "Entity", "VisualEffectManager.cs"));
            var start = text.IndexOf("public static void SendTo(", StringComparison.Ordinal);
            var end = text.IndexOf("public static void Forget(Session session, WorldObject wo)", StringComparison.Ordinal);
            Assert.IsTrue(start > 0 && end > start);
            var body = Regex.Replace(text.Substring(start, end - start), @"//[^\r\n]*", "");

            Assert.IsTrue(
                Regex.IsMatch(body, @"foreach\s*\(\s*var\s+scriptId\s+in\s+ScriptsToEmit\(wo\)\s*\)\s*session\.Network\.EnqueueSend\(\s*new\s+GameMessagePlayScriptId\(\s*wo\.Guid\s*,\s*scriptId\s*\)\s*\)\s*;"),
                "SendTo must emit ScriptsToEmit through one foreach whose body is the EnqueueSend, so the template base layers under a tier aura and scratch entries are deduped");
        }
    }
}
