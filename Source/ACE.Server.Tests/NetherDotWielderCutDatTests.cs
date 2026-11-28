using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Server.Entity;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using DbSpell = ACE.Database.Models.World.Spell;
using Spell = ACE.Server.Entity.Spell;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The void curse wielder-cut fix end to end on a REAL spell: Incantation of Destructive Curse (5338) built through
    /// EnchantmentManager.Add from a player caster, once wielding a Summoner focus and once a plain wand. The world
    /// database fields the DoT path reads are copied from the local ace_world spell table, since ACE.Server.Tests has no
    /// database:
    ///   SELECT id, name, stat_Mod_Type, stat_Mod_Key, stat_Mod_Val, e_Type, dot_Duration, num_Projectiles FROM spell WHERE id = 5338
    /// run against ace_world on 2026-11-27 (ace_ro): 36868, 330 (NetherOverTime), 51, 1024 (Nether), 30, NULL.
    /// RequiresDatFiles: Inconclusive without ACE_DAT_PATH, skipped in CI; the dat-free pins are NetherDotWielderCutTests.
    /// </summary>
    [TestClass]
    public class NetherDotWielderCutDatTests
    {
        private const uint SpellId = 5338;
        private const string SpellName = "Incantation of Destructive Curse";

        // an empty rank cache: the caster has no class abilities (Malediction reads it in BuildEntry), and a bare Player has
        // no Character to build it from. UnsafeAccessor, not FieldInfo.SetValue, so Player's static initializer never runs.
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "classAbilityCache")]
        private static extern ref Dictionary<ClassAbilityId, int> ClassAbilityCache(Player player);

        private static Player PlayerWielding(WorldObject wand)
        {
            var player = SummonerFocusTests.OwnerWielding(wand);
            ClassAbilityCache(player) = new Dictionary<ClassAbilityId, int>();
            return player;
        }

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed() => PropertyManager.ModifyDouble(SummonerFocus.WielderDamageModKey, 0.25);

        [TestCleanup]
        public void Restore() =>
            PropertyManager.ModifyDouble(SummonerFocus.WielderDamageModKey, DefaultPropertyManager.DefaultDoubleProperties[SummonerFocus.WielderDamageModKey].Item);

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void RealVoidCurse_FromAFocusWielder_DamageIsCut_DebuffIsTheUncutCurses()
        {
            RequirePortalDat();

            var spell = Load();
            Assert.IsTrue(spell.IsDamageOverTime, "control: a DoT");
            Assert.AreEqual(ACE.Entity.Enum.SpellCategory.NetherDamageOverTimeRaising, spell.Category, "control: a void curse category");

            var focus = SummonerFocusTests.Wand(true, 1.0);
            var plain = SummonerFocusTests.Wand(false, 1.0);
            var focusCaster = PlayerWielding(focus);
            var plainCaster = PlayerWielding(plain);

            var focusTarget = TestCreatures.CreateDefender();
            var plainTarget = TestCreatures.CreateDefender();

            var cut = focusTarget.EnchantmentManager.Add(spell, focusCaster, focus).Enchantment;
            var uncut = plainTarget.EnchantmentManager.Add(spell, plainCaster, plain).Enchantment;

            Assert.IsTrue(uncut.StatModValue > 0.0f, "control: the uncut curse ticks");
            Assert.AreEqual(uncut.StatModValue * 0.25f, cut.StatModValue, 1e-3f, "the focus cut stays on the curse's damage");

            var expected = (int)Math.Round(uncut.StatModValue / 8.0f);
            Assert.AreNotEqual(expected, (int)Math.Round(cut.StatModValue / 8.0f), "control: sized from the cut value, the debuff would differ");

            Assert.AreEqual(expected, plainTarget.EnchantmentManager.GetNetherDotDamageRating(), "no focus: unchanged");
            Assert.AreEqual(expected, focusTarget.EnchantmentManager.GetNetherDotDamageRating(), "the focus cut must not shrink the debuff");
        }

        private static Spell Load()
        {
            var spell = new Spell(SpellId, false);
            Assert.IsNotNull(spell._spellBase, $"{SpellId} is in the portal dat");
            Assert.AreEqual(SpellName, spell._spellBase.Name, $"control: spell {SpellId} is {SpellName}");

            spell._spell = new DbSpell
            {
                Id = SpellId,
                StatModType = 36868,
                StatModKey = 330,
                StatModVal = 51,
                EType = 1024,
                DotDuration = 30,
            };

            return spell;
        }

        // ===================== the portal dat (the EchoWispSpellDataDatTests pattern) =====================

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        private static void RequirePortalDat()
        {
            lock (initLock)
            {
                if (!initialized)
                {
                    initialized = true;
                    skipReason = Initialize();
                }
            }

            if (skipReason != null)
                Assert.Inconclusive(skipReason);
        }

        private static string Initialize()
        {
            if (DatManager.PortalDat != null)
                return null;

            var candidates = new List<string>();
            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");

            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            return DatManager.PortalDat == null ? $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat." : null;
        }
    }
}
