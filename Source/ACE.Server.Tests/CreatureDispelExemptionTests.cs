using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A non-player creature's dispel must not strip the long-duration progression buffs in
    /// EnchantmentManager.CreatureDispelExempt; a player-cast dispel still removes them, and a non-exempt spell is
    /// still removed by either. Tested through ApplyCreatureDispelExemption (the filter SelectDispel applies), since
    /// SelectDispel itself needs a loaded Spell from the dat. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class CreatureDispelExemptionTests
    {
        private const int NonExemptSpell = 521;

        private static PropertiesEnchantmentRegistry Entry(int spellId) => new PropertiesEnchantmentRegistry { SpellId = spellId, Duration = 14400 };

        private static T Uninit<T>() where T : WorldObject => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

        private static List<int> Survivors(WorldObject caster, params int[] ids) =>
            EnchantmentManager.ApplyCreatureDispelExemption(ids.Select(Entry).ToList(), caster).Select(e => e.SpellId).ToList();

        private static readonly int[] Exempt =
        {
            (int)SpellId.AsheronsBenediction, (int)SpellId.BlackmoorsFavor, (int)SpellId.AsheronsLesserBenediction, (int)SpellId.CarraidasBenediction, 3204, 5451,
            (int)SpellId.AetheriaProcDamageBoost, (int)SpellId.AetheriaProcDamageReduction, (int)SpellId.AetheriaProcHealthOverTime,
        };

        [TestMethod]
        public void ExemptIds_MatchTheCatalogedSpellIds()
        {
            CollectionAssert.AreEquivalent(new uint[] { 3810, 3811, 4024, 3953, 3204, 5451, 5204, 5206, 5208 }, EnchantmentManager.CreatureDispelExempt.ToArray());
        }

        [TestMethod]
        public void CreatureCaster_CannotStripExemptSpells_ButStripsOthers()
        {
            var creature = Uninit<Creature>();

            var candidates = Exempt.Concat(new[] { NonExemptSpell }).ToArray();
            var survivors = Survivors(creature, candidates);

            CollectionAssert.AreEqual(new[] { NonExemptSpell }, survivors, "a creature dispel keeps the exempt spells out of the candidate list and leaves the non-exempt one");
        }

        [TestMethod]
        public void PlayerCaster_StillRemovesExemptSpells()
        {
            var player = Uninit<Player>();

            var survivors = Survivors(player, Exempt.Concat(new[] { NonExemptSpell }).ToArray());

            Assert.AreEqual(Exempt.Length + 1, survivors.Count, "player-cast dispels are unchanged");
        }

        [TestMethod]
        public void UnknownCaster_IsUnchanged()
        {
            Assert.AreEqual(Exempt.Length, Survivors(null, Exempt).Count);
        }

        private static List<int> Strippable(WorldObject attacker, params int[] ids) =>
            ACE.Server.MonsterEffects.Effects.DispelEffect.SelectStrippable(ids.Select(Entry).ToList(), (Creature)attacker).Select(e => e.SpellId).ToList();

        [TestMethod]
        public void MonsterDispelRider_CreatureAttacker_CannotSelectExemptSpells_ButSelectsOthers()
        {
            var selected = Strippable(Uninit<Creature>(), 3811, NonExemptSpell);

            CollectionAssert.AreEqual(new[] { NonExemptSpell }, selected);
        }

        [TestMethod]
        public void MonsterDispelRider_AllExemptCandidates_SelectsNothing()
        {
            Assert.AreEqual(0, Strippable(Uninit<Creature>(), Exempt).Count);
        }

        [TestMethod]
        public void PlayerAttacker_AndDispellingEdge_StillSelectExemptSpells()
        {
            CollectionAssert.AreEquivalent(new[] { 3811, NonExemptSpell }, Strippable(Uninit<Player>(), 3811, NonExemptSpell));

            // the shared selector the player Dispelling Edge and weapon-mod steal use is untouched
            var direct = ACE.Server.ClassAbilities.Abilities.DispellingEdgeAbility.SelectDispellable(new List<PropertiesEnchantmentRegistry> { Entry(3811), Entry(NonExemptSpell) });
            Assert.AreEqual(2, direct.Count);
        }

        [TestMethod]
        public void SelectDispel_CallsTheCreatureExemption()
        {
            var root = FindSourceRoot();
            var text = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "ACE.Server", "WorldObjects", "Managers", "EnchantmentManager.cs"));
            var sel = text.IndexOf("public List<SpellEnchantment> SelectDispel(Spell spell, WorldObject caster = null)", StringComparison.Ordinal);
            Assert.IsTrue(sel >= 0, "SelectDispel signature not found");
            var call = text.IndexOf("ApplyCreatureDispelExemption(filtered, caster)", sel, StringComparison.Ordinal);
            Assert.IsTrue(call > sel, "SelectDispel must call ApplyCreatureDispelExemption(filtered, caster)");
        }

        private static string FindSourceRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "ACE.Server", "WorldObjects", "Managers", "EnchantmentManager.cs");
                if (System.IO.File.Exists(candidate))
                    return dir.FullName;
                dir = dir.Parent;
            }
            Assert.Fail("Could not find Source/ACE.Server by walking up from " + AppContext.BaseDirectory);
            return null;
        }
    }
}
