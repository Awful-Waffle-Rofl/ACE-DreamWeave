using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Summoner wielder cut (summoner_wielder_spell_damage_mod) cuts a void curse's DAMAGE but not its
    /// damage-resistance DEBUFF (owner ruling 2026-11-27). The cut stays baked into the entry's StatModValue at land
    /// (so it cannot be escaped by a weapon swap); NetherDotWielderCuts records it, and GetNetherDotDamageRating divides
    /// it back out. The behavior tests drive the real registry and GetNetherDotDamageRating on a factory-built object;
    /// the cast-time sizing needs a dat Spell, so its wiring is pinned as text, as SummonerFocusLifeMagicWiringTests does.
    /// </summary>
    [TestClass]
    public class NetherDotWielderCutTests
    {
        private const float Cut = (float)SummonerFocusMath.DefaultWielderDamageMod; // 0.25

        private const EnchantmentTypeFlags NetherDotType = EnchantmentTypeFlags.Int | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive;

        private static uint nextGuid = 0x7ED40000;

        private static WorldObject Target()
        {
            var weenie = new Weenie { WeenieClassId = 1008099, WeenieType = WeenieType.Generic };

            var wo = ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));

            wo.Biota.PropertiesEnchantmentRegistry ??= new List<PropertiesEnchantmentRegistry>();

            return wo;
        }

        /// <summary>A void curse as BuildEntry leaves it: StatModValue already carries the cut, and the cut is recorded.</summary>
        private static PropertiesEnchantmentRegistry Curse(WorldObject target, SpellCategory category, float uncutPerTick, float cut)
        {
            var entry = new PropertiesEnchantmentRegistry
            {
                SpellId = 5000 + (int)category,
                SpellCategory = category,
                LayerId = 1,
                Duration = 30,
                StatModType = NetherDotType,
                StatModKey = (uint)PropertyInt.NetherOverTime,
                StatModValue = uncutPerTick * cut,
            };

            NetherDotWielderCuts.Set(entry, cut);

            target.Biota.PropertiesEnchantmentRegistry.AddEnchantment(entry, target.BiotaDatabaseLock);

            return entry;
        }

        // ---- the record ------------------------------------------------------------------------------------

        [TestMethod]
        public void Record_DefaultsToOne_IsPerEntry_AndCopies()
        {
            var a = new PropertiesEnchantmentRegistry();
            var b = new PropertiesEnchantmentRegistry();

            Assert.AreEqual(1.0f, NetherDotWielderCuts.Get(a), "an unrecorded entry (e.g. one reloaded from the database) has no cut");
            Assert.AreEqual(1.0f, NetherDotWielderCuts.Get(null));

            NetherDotWielderCuts.Set(a, Cut);
            Assert.AreEqual(Cut, NetherDotWielderCuts.Get(a));
            Assert.AreEqual(1.0f, NetherDotWielderCuts.Get(b));

            NetherDotWielderCuts.Copy(a, b);
            Assert.AreEqual(Cut, NetherDotWielderCuts.Get(b), "Nether Bloom copies the record with the StatModValue");

            NetherDotWielderCuts.Set(a, 1.0f);
            Assert.AreEqual(1.0f, NetherDotWielderCuts.Get(a));
            Assert.AreEqual(Cut, NetherDotWielderCuts.Get(b), "records are independent after a copy");

            NetherDotWielderCuts.Set(null, Cut); // no throw
        }

        [TestMethod]
        public void DebuffPerTick_DividesTheCutBackOut()
        {
            Assert.AreEqual(80.0f, EnchantmentManager.NetherDotDebuffDamagePerTick(80.0f * Cut, Cut), 1e-4f);
            Assert.AreEqual(80.0f, EnchantmentManager.NetherDotDebuffDamagePerTick(80.0f, 1.0f), "no cut: unchanged");
            Assert.AreEqual(0.0f, EnchantmentManager.NetherDotDebuffDamagePerTick(0.0f, 0.0f), "a cut of 0 leaves nothing to recover");
        }

        // ---- damage cut, debuff uncut ----------------------------------------------------------------------

        [TestMethod]
        public void FocusCastCurse_DamageIsCut_DebuffEqualsTheUncutCurse()
        {
            var uncutTarget = Target();
            var cutTarget = Target();

            var uncut = Curse(uncutTarget, SpellCategory.NetherDamageOverTimeRaising, 80.0f, 1.0f);
            var cut = Curse(cutTarget, SpellCategory.NetherDamageOverTimeRaising, 80.0f, Cut);

            // the damage each tick deals is the stored StatModValue: cut x0.25
            Assert.AreEqual(80.0f, uncutTarget.EnchantmentManager.GetDamagePerTick(uncut), 1e-4f);
            Assert.AreEqual(80.0f * Cut, cutTarget.EnchantmentManager.GetDamagePerTick(cut), 1e-4f, "the focus cut stays on the damage");

            // the debuff is the uncut curse's: round(80 / 8) = 10 on both
            Assert.AreEqual(10, uncutTarget.EnchantmentManager.GetNetherDotDamageRating());
            Assert.AreEqual(10, cutTarget.EnchantmentManager.GetNetherDotDamageRating(), "the focus cut must not shrink the debuff");
        }

        [TestMethod]
        public void NonFocusCurse_DebuffUnchanged_AndMixedCursesSumUncut()
        {
            var plain = Target();
            Curse(plain, SpellCategory.NetherDamageOverTimeRaising, 40.0f, 1.0f);
            Assert.AreEqual(5, plain.EnchantmentManager.GetNetherDotDamageRating(), "no focus: round(40 / 8), as before");

            var mixed = Target();
            Curse(mixed, SpellCategory.NetherDamageOverTimeRaising, 80.0f, Cut);
            Curse(mixed, SpellCategory.NetherDamageOverTimeRaising2, 40.0f, 1.0f);
            Assert.AreEqual(15, mixed.EnchantmentManager.GetNetherDotDamageRating(), "round((80 + 40) / 8): each curse's own cut divided out");
        }

        [TestMethod]
        public void RefreshInPlace_KeepsTheRecord()
        {
            var target = Target();
            var entry = Curse(target, SpellCategory.NetherDamageOverTimeRaising, 80.0f, Cut);

            // EnchantmentManager.Add's refresh branch mutates the same entry (StartTime / Duration) and keeps StatModValue
            entry.StartTime = 0;
            entry.Duration = 60;

            Assert.AreEqual(Cut, NetherDotWielderCuts.Get(entry));
            Assert.AreEqual(10, target.EnchantmentManager.GetNetherDotDamageRating());
        }

        // ---- wiring (text pins) ----------------------------------------------------------------------------

        private const string MagicFile = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";
        private const string ManagerFile = "Source/ACE.Server/WorldObjects/Managers/EnchantmentManager.cs";
        private const string BloomFile = "Source/ACE.Server/ClassAbilities/Abilities/NetherBloomAbility.cs";

        private static int CountOf(string haystack, string needle)
        {
            var n = 0;

            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }

        [TestMethod]
        public void Wiring_OnlyTheNetherBranchRecordsTheCut_AfterItIsBakedIn()
        {
            var dot = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(MagicFile), "public float CalculateDotEnchantment_StatModValue(");

            Assert.AreEqual(2, CountOf(dot, "netherWielderCut = "), "the 1.0 default and the nether branch only");
            StringAssert.Contains(dot, "netherWielderCut = 1.0f;");

            var bake = dot.IndexOf("enchantment_statModVal *= skillMod * elementalDamageMod * damageRatingMod * summonerWielderMod;", StringComparison.Ordinal);
            var record = dot.IndexOf("netherWielderCut = summonerWielderMod;", StringComparison.Ordinal);
            Assert.IsTrue(bake >= 0 && record > bake, "the nether branch records the cut it baked into the damage");
        }

        [TestMethod]
        public void Wiring_BuildEntryRecords_TheRatingDividesOut_AndBloomCopies()
        {
            var manager = PooledLootSourceText.Read(ManagerFile);

            const string calc = "entry.StatModValue = caster.CalculateDotEnchantment_StatModValue(spell, WorldObject, weapon, entry.StatModValue, out var netherWielderCut);";
            const string record = "NetherDotWielderCuts.Set(entry, netherWielderCut);";

            Assert.AreEqual(1, CountOf(manager, calc));
            Assert.AreEqual(1, CountOf(manager, record));
            Assert.AreEqual(1, CountOf(manager, "NetherDotWielderCuts.Set("), "BuildEntry is the only place a cut is recorded");

            // the record must follow the call and pass the cut that call returned, inside BuildEntry
            var build = PooledLootSourceText.MethodBody(manager, "private PropertiesEnchantmentRegistry BuildEntry(");
            var calcAt = build.IndexOf(calc, StringComparison.Ordinal);
            var recordAt = build.IndexOf(record, StringComparison.Ordinal);
            Assert.IsTrue(calcAt >= 0, "BuildEntry sizes the DoT and takes the cut out");
            Assert.IsTrue(recordAt > calcAt, "BuildEntry records the cut AFTER CalculateDotEnchantment_StatModValue returns it");

            var rating = PooledLootSourceText.MethodBody(manager, "public virtual int GetNetherDotDamageRating(");
            StringAssert.Contains(rating, "totalBaseDamage += NetherDotDebuffDamagePerTick(GetDamagePerTick(netherDot, 5.0), NetherDotWielderCuts.Get(netherDot));");

            var bloom = PooledLootSourceText.Read(BloomFile);
            var copyValue = bloom.IndexOf("entry.StatModValue = source.StatModValue;", StringComparison.Ordinal);
            var copyRecord = bloom.IndexOf("NetherDotWielderCuts.Copy(source, entry);", StringComparison.Ordinal);
            Assert.IsTrue(copyValue >= 0 && copyRecord > copyValue, "the bloom copies the cut record with the value it copies");
        }
    }
}
