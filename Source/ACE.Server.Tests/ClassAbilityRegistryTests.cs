using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    [TestClass]
    public class ClassAbilityRegistryTests
    {
        [TestMethod]
        public void EveryDefinitionIsInternallyConsistent()
        {
            Assert.IsTrue(ClassAbilityRegistry.Abilities.Count > 0);

            foreach (var skill in ClassAbilityRegistry.Abilities.Values)
            {
                Assert.AreEqual(skill.Id, ClassAbilityRegistry.Get(skill.Id).Id);

                Assert.IsFalse(string.IsNullOrWhiteSpace(skill.Name), $"{skill.Id}: Name is empty");
                Assert.AreEqual(skill.Name, skill.Name.ToLowerInvariant(), $"{skill.Id}: Name must be lowercase");
                Assert.IsFalse(skill.Name.Contains(' '), $"{skill.Id}: Name must be a single token (used in /abilities commands)");
                Assert.IsFalse(string.IsNullOrWhiteSpace(skill.DisplayName), $"{skill.Id}: DisplayName is empty");
                Assert.IsFalse(string.IsNullOrWhiteSpace(skill.Description), $"{skill.Id}: Description is empty");

                Assert.IsTrue(skill.MaxRank >= 1, $"{skill.Id}: MaxRank must be >= 1");
                Assert.IsNotNull(skill.CostPerRank, $"{skill.Id}: CostPerRank is null");
                Assert.AreEqual(skill.MaxRank, skill.CostPerRank.Length, $"{skill.Id}: CostPerRank length must equal MaxRank");
                Assert.IsTrue(skill.CostPerRank.All(c => c > 0), $"{skill.Id}: every rank must cost at least 1 point");
            }
        }

        [TestMethod]
        public void NamesAreUniqueAndLookupsRoundTrip()
        {
            var names = ClassAbilityRegistry.Abilities.Values.Select(s => s.Name).ToList();
            Assert.AreEqual(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "duplicate skill names");

            foreach (var skill in ClassAbilityRegistry.Abilities.Values)
            {
                Assert.IsTrue(ClassAbilityRegistry.TryGetByName(skill.Name, out var byName));
                Assert.AreEqual(skill.Id, byName.Id);

                // case-insensitive command input
                Assert.IsTrue(ClassAbilityRegistry.TryGetByName(skill.Name.ToUpperInvariant(), out var byUpper));
                Assert.AreEqual(skill.Id, byUpper.Id);

                // quest registry key round-trip
                Assert.IsTrue(ClassAbilityRegistry.TryGetByQuestKey(ClassAbilityRegistry.QuestKey(skill), out var byKey));
                Assert.AreEqual(skill.Id, byKey.Id);
            }

            Assert.IsFalse(ClassAbilityRegistry.TryGetByName("notaskill", out _));
            Assert.IsFalse(ClassAbilityRegistry.TryGetByName(null, out _));
            Assert.IsFalse(ClassAbilityRegistry.TryGetByQuestKey("SomeOtherQuest", out _));
            Assert.IsFalse(ClassAbilityRegistry.TryGetByQuestKey(null, out _));
        }

        [TestMethod]
        public void EverySkillHasAHandlerAndImplementedSkillsHookSomething()
        {
            Assert.AreEqual(ClassAbilityRegistry.Abilities.Count, ClassAbilityRegistry.Handlers.Count);

            foreach (var handler in ClassAbilityRegistry.Handlers)
            {
                // handler <-> definition round-trip
                Assert.AreSame(handler, ClassAbilityRegistry.GetHandler(handler.Definition.Id));
                Assert.AreSame(handler.Definition, ClassAbilityRegistry.Get(handler.Definition.Id));

                var hooks = 0;
                if (handler is IOutgoingDamageAbility) hooks++;
                if (handler is IIncomingDamageAbility) hooks++;
                if (handler is IMissileVolleyAbility) hooks++;
                if (handler is IItemProcAbility) hooks++;
                if (handler is ISpellHitAbility) hooks++;
                if (handler is ICreatureDeathAbility) hooks++;

                // passive stat skills (Enhanced X) deliberately hook nothing - their effect is read
                // directly off the player, not dispatched from a combat site
                if (handler is IPassiveStatAbility)
                {
                    Assert.AreEqual(0, hooks, $"{handler.Definition.Id}: a passive stat skill must not implement a combat hook");
                    continue;
                }

                // an Implemented combat skill with no hook does nothing;
                // an unimplemented one shouldn't be wired into combat paths yet
                if (handler.Definition.Implemented)
                    Assert.IsTrue(hooks > 0, $"{handler.Definition.Id}: Implemented but implements no hook interface");
                else
                    Assert.AreEqual(0, hooks, $"{handler.Definition.Id}: not Implemented but implements a hook interface");
            }
        }

        [TestMethod]
        public void HookBucketsCoverAllHandlers()
        {
            foreach (var handler in ClassAbilityRegistry.Handlers)
            {
                Assert.AreEqual(handler is IOutgoingDamageAbility, ClassAbilityRegistry.OutgoingDamageAbilities.Contains(handler as IOutgoingDamageAbility));
                Assert.AreEqual(handler is IIncomingDamageAbility, ClassAbilityRegistry.IncomingDamageAbilities.Contains(handler as IIncomingDamageAbility));
                Assert.AreEqual(handler is IMissileVolleyAbility, ClassAbilityRegistry.MissileVolleyAbilities.Contains(handler as IMissileVolleyAbility));
                Assert.AreEqual(handler is IItemProcAbility, ClassAbilityRegistry.ItemProcAbilities.Contains(handler as IItemProcAbility));
                Assert.AreEqual(handler is ISpellHitAbility, ClassAbilityRegistry.SpellHitAbilities.Contains(handler as ISpellHitAbility));
                Assert.AreEqual(handler is ICreatureDeathAbility, ClassAbilityRegistry.CreatureDeathAbilities.Contains(handler as ICreatureDeathAbility));
            }
        }

        [TestMethod]
        public void SpellAoe_IsImplementedAndWiredAsASpellHitSkill()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.SpellAoe);

            Assert.IsInstanceOfType(handler, typeof(ISpellHitAbility), "SpellAoe must implement the ISpellHitAbility hook");
            Assert.IsTrue(handler.Definition.Implemented, "SpellAoe is wired up and should be learnable");
            Assert.IsTrue(ClassAbilityRegistry.SpellHitAbilities.Contains((ISpellHitAbility)handler), "SpellAoe must be in the SpellHitAbilities bucket");

            // it is the only combat hook it participates in - the blast is its whole effect
            Assert.IsFalse(handler is IOutgoingDamageAbility);
            Assert.IsFalse(handler is IIncomingDamageAbility);
            Assert.IsFalse(handler is IMissileVolleyAbility);
            Assert.IsFalse(handler is IItemProcAbility);
        }

        [TestMethod]
        public void CumulativeCostSumsRanks()
        {
            var skill = ClassAbilityRegistry.Abilities.Values.First(s => s.MaxRank >= 2);

            Assert.AreEqual(0, skill.CumulativeCost(0));
            Assert.AreEqual(skill.CostPerRank[0], skill.CumulativeCost(1));
            Assert.AreEqual(skill.CostPerRank.Sum(), skill.CumulativeCost(skill.MaxRank));

            // out-of-range ranks clamp rather than throw
            Assert.AreEqual(skill.CostPerRank.Sum(), skill.CumulativeCost(skill.MaxRank + 5));
            Assert.AreEqual(0, skill.CumulativeCost(-1));
        }
    }
}
