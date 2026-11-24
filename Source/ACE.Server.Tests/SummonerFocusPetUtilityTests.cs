using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using DatSpellBase = ACE.DatLoader.Entity.SpellBase;
using DbSpell = ACE.Database.Models.World.Spell;

using static ACE.Server.Tests.SummonerTransferFixtures;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Summoner focus utility trio on the pets (owner ruling Q4, all Shared): Second Wind and Cleanse on a pet's
    /// killing blow, run on the PET's own queue, and Siphon on a pet's landed hit, stolen onto the pet. The golden pins
    /// hold the PLAYER's own Siphon and Cleanse behaviour as the pre-extraction code produced it. Spells are faked
    /// through Creature.WeaponModSpellResolver (this assembly loads neither the dat nor the world database).
    /// </summary>
    [TestClass]
    public class SummonerFocusPetUtilityTests
    {
        private const int BuffId = 3101;
        private const int DebuffId = 3102;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedKeys()
        {
            Seed();
            Creature.WeaponModSpellResolver = id => id == BuffId ? FakeEnchantment(BuffId, "Test Buff", SpellCategory.StrengthRaising, true)
                : id == DebuffId ? FakeEnchantment(DebuffId, "Test Debuff", SpellCategory.StrengthLowering, false)
                : Creature.DefaultWeaponModSpellResolver(id);
        }

        [TestCleanup]
        public void RestoreKeys()
        {
            Restore();
            Creature.WeaponModSpellResolver = Creature.DefaultWeaponModSpellResolver;
            ThreadSafeRandom.Reseed(Environment.TickCount);
        }

        // ---------------- fakes ----------------

        private static Spell FakeEnchantment(int id, string name, SpellCategory category, bool beneficial)
        {
            var spellBase = (DatSpellBase)RuntimeHelpers.GetUninitializedObject(typeof(DatSpellBase));
            SetBackingField(spellBase, "Name", name);
            SetBackingField(spellBase, "School", MagicSchool.CreatureEnchantment);
            SetBackingField(spellBase, "Category", category);
            SetBackingField(spellBase, "MetaSpellId", (uint)id);
            SetBackingField(spellBase, "MetaSpellType", SpellType.Enchantment);
            SetBackingField(spellBase, "Bitfield", (uint)(beneficial ? SpellFlags.Beneficial : 0));
            SetBackingField(spellBase, "Power", 300u);
            SetBackingField(spellBase, "Duration", 1800.0);

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;
            spell._spell = new DbSpell
            {
                StatModType = (uint)(EnchantmentTypeFlags.Attribute | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive | (beneficial ? EnchantmentTypeFlags.Beneficial : 0)),
                StatModKey = 1,
                StatModVal = beneficial ? 40.0f : -40.0f,
            };

            return spell;
        }

        private static void SetBackingField(object target, string propertyName, object value)
        {
            var field = target.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{target.GetType().Name}.<{propertyName}>k__BackingField was not found by reflection");
            field.SetValue(target, value);
        }

        /// <summary>Registers a first-cast entry of a faked spell on <paramref name="c"/>, cast by <paramref name="casterGuid"/>.</summary>
        private static void Give(Creature c, int spellId, uint casterGuid)
        {
            var spell = Creature.WeaponModSpellResolver(spellId);
            c.Biota.PropertiesEnchantmentRegistry.AddEnchantment(new PropertiesEnchantmentRegistry
            {
                SpellId = spellId,
                SpellCategory = spell.Category,
                PowerLevel = spell.Power,
                LayerId = 1,
                Duration = 1800.0,
                CasterObjectId = casterGuid,
                StatModType = spell.StatModType,
                StatModKey = spell.StatModKey,
                StatModValue = spell.StatModVal,
            }, c.BiotaDatabaseLock);
        }

        private static bool Has(Creature c, int spellId) =>
            c.EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Undef).Any(e => e.SpellId == spellId);

        /// <summary>A seed whose first NextDouble on this thread is below <paramref name="chance"/>: a forced Siphon roll.</summary>
        private static void ForceRollBelow(double chance)
        {
            for (var seed = 1; seed < 100000; seed++)
            {
                if (new Random(seed).NextDouble() < chance)
                {
                    ThreadSafeRandom.Reseed(seed);
                    return;
                }
            }

            Assert.Fail("no seed found");
        }

        /// <summary>A seed whose first NextDouble is at or above <paramref name="chance"/>: a forced Siphon miss.</summary>
        private static void ForceRollAbove(double chance)
        {
            for (var seed = 1; seed < 100000; seed++)
            {
                if (new Random(seed).NextDouble() >= chance)
                {
                    ThreadSafeRandom.Reseed(seed);
                    return;
                }
            }

            Assert.Fail("no seed found");
        }

        private static CombatPet FocusPet(Player owner, bool echo = false)
        {
            var pet = Pet(owner, echo: echo);
            return pet;
        }

        private static Caster UtilityFocus() => Wand(true, ImbuedEffectType.Undef, WeaponModId.SecondWind, WeaponModId.Cleanse, WeaponModId.Siphon);

        // ---------------- the shared Siphon body (Creature.SiphonOneBeneficialEnchantment) ----------------

        /// <summary>
        /// The body moved out of Player.TryWeaponModSiphon, on creatures (the player-only Dispel branch needs a Session and
        /// is not reached for a creature thief): a forced roll steals the one beneficial enchantment, a forced miss and
        /// every filtered target steal nothing, a harmful enchantment is never taken, and an item-sourced aura is never moved.
        /// </summary>
        [TestMethod]
        public void Siphon_body_steals_one_beneficial_enchantment_onto_the_thief()
        {
            var thief = Monster();

            var target = Monster();
            Give(target, BuffId, target.Guid.Full);
            ForceRollBelow(0.10);
            var (spell, added) = thief.SiphonOneBeneficialEnchantment(target, 0.10);
            Assert.AreEqual((uint)BuffId, spell?.Id, "the stolen spell is returned");
            Assert.IsNotNull(added?.Enchantment, "applied through EnchantmentManager.Add");
            Assert.IsFalse(Has(target, BuffId), "stripped off the target");
            Assert.IsTrue(Has(thief, BuffId), "onto the thief");

            var missed = Monster();
            Give(missed, BuffId, missed.Guid.Full);
            ForceRollAbove(0.10);
            Assert.IsNull(thief.SiphonOneBeneficialEnchantment(missed, 0.10).Spell, "a missed roll");
            Assert.IsTrue(Has(missed, BuffId));

            var debuffed = Monster();
            Give(debuffed, DebuffId, 0x50000001);
            ForceRollBelow(0.10);
            Assert.IsNull(thief.SiphonOneBeneficialEnchantment(debuffed, 0.10).Spell, "beneficial only");
            Assert.IsTrue(Has(debuffed, DebuffId));

            var pet = Pet(null);
            Give(pet, BuffId, pet.Guid.Full);
            ForceRollBelow(0.10);
            Assert.IsNull(thief.SiphonOneBeneficialEnchantment(pet, 0.10).Spell, "a Pet target is refused");

            ForceRollBelow(0.10);
            Assert.IsNull(thief.SiphonOneBeneficialEnchantment(thief, 0.10).Spell, "never self");
            Assert.IsNull(thief.SiphonOneBeneficialEnchantment(target, 0.0).Spell, "no chance, no roll");

            var aura = Monster();
            aura.Biota.PropertiesEnchantmentRegistry.AddEnchantment(new PropertiesEnchantmentRegistry
            {
                SpellId = BuffId, SpellCategory = SpellCategory.StrengthRaising, PowerLevel = 300, LayerId = 1, Duration = -1.0, CasterObjectId = aura.Guid.Full,
                StatModType = EnchantmentTypeFlags.Attribute | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.Beneficial, StatModKey = 1, StatModValue = 40,
            }, aura.BiotaDatabaseLock);
            ForceRollBelow(0.10);
            Assert.IsNull(thief.SiphonOneBeneficialEnchantment(aura, 0.10).Spell, "an item-sourced aura (Duration -1) is never moved");
            Assert.IsTrue(Has(aura, BuffId));
        }

        // ---------------- Siphon on a pet's landed hit ----------------

        [TestMethod]
        public void Pet_siphon_steals_onto_the_pet_with_the_focus_chance()
        {
            var focus = UtilityFocus();
            var chance = Mag(WeaponModId.Siphon, focus);
            var pet = FocusPet(Owner(focus));
            var monster = Monster();
            Give(monster, BuffId, monster.Guid.Full);

            ForceRollBelow(chance);
            SummonerFocus.TryPetSiphon(pet, monster);

            Assert.IsFalse(Has(monster, BuffId), "stripped off the monster");
            Assert.IsTrue(Has(pet, BuffId), "onto the PET");
            Assert.IsFalse(Has(pet.P_PetOwner, BuffId), "not onto the owner");
        }

        [TestMethod]
        public void Pet_siphon_through_the_melee_sink_on_a_DirectHit_only()
        {
            var focus = UtilityFocus();
            var chance = Mag(WeaponModId.Siphon, focus);
            var pet = FocusPet(Owner(focus));

            var direct = Monster();
            Give(direct, BuffId, direct.Guid.Full);
            ForceRollBelow(chance);
            direct.TakeDamage(pet, DamageType.Slash, 10f, false, IncomingDamageOrigin.DirectHit);
            Assert.IsTrue(Has(pet, BuffId), "a landed strike rolls Siphon in Creature.TakeDamage");

            var tick = Monster();
            Give(tick, BuffId, tick.Guid.Full);
            var other = FocusPet(Owner(focus));
            ForceRollBelow(chance);
            tick.TakeDamage(other, DamageType.Slash, 10f);
            Assert.IsTrue(Has(tick, BuffId), "a Secondary hit (tick, proc) never rolls");
        }

        [TestMethod]
        public void Pet_siphon_negatives()
        {
            var focus = UtilityFocus();
            var chance = Mag(WeaponModId.Siphon, focus);

            void Refused(CombatPet pet, Creature target, string because)
            {
                Give(target, BuffId, target.Guid.Full);
                ForceRollBelow(chance);
                SummonerFocus.TryPetSiphon(pet, target);
                Assert.IsTrue(Has(target, BuffId), because);
            }

            Refused(FocusPet(Owner(focus), echo: true), Monster(), "Echo");
            Refused(FocusPet(Owner(Wand(false, ImbuedEffectType.Undef, WeaponModId.Siphon))), Monster(), "plain wand");
            Refused(FocusPet(Owner(focus)), FocusPet(Owner(Wand(true))), "an owned pet target is Player-side");

            var pvpOwner = Owner(UtilityFocus());
            pvpOwner.SetPvpBindingForTests(SummonerFocusTests.LiveBinding());
            Refused(FocusPet(pvpOwner), Monster(), "owner in a PvP match");

            PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, 0.0);
            Refused(FocusPet(Owner(focus)), Monster(), "transfer 0");
        }

        // ---------------- the kill half: Second Wind and Cleanse on the pet's queue ----------------

        [TestMethod]
        public void Pet_kill_queues_on_the_pets_queue_and_restores_and_cleanses_the_pet()
        {
            var focus = UtilityFocus();
            var owner = Owner(focus);
            var pet = FocusPet(owner);
            var victim = Monster();

            pet.Health.Current = pet.Health.MaxValue / 2;
            Give(pet, DebuffId, victim.Guid.Full);
            Give(owner, DebuffId, victim.Guid.Full);

            var queued = new List<(CombatPet Target, ACE.Server.Entity.Actions.IAction Action)>();
            var saved = SummonerFocus.PetKillEnqueue;

            try
            {
                SummonerFocus.PetKillEnqueue = (p, a) => queued.Add((p, a));
                SummonerFocus.EnqueuePetKillUtility(pet, victim);
            }
            finally
            {
                SummonerFocus.PetKillEnqueue = saved;
            }

            Assert.AreEqual(1, queued.Count, "one queue entry per kill");
            Assert.AreSame(pet, queued[0].Target, "queued on the PET's own queue, not the owner's or the victim's");
            Assert.AreEqual(pet.Health.MaxValue / 2, pet.Health.Current, "nothing runs on the victim's thread");

            var before = pet.Health.Current;
            queued[0].Action.Act();

            var expected = (uint)Math.Min(pet.Health.MaxValue, before + WeaponModCombat.RestoreFromMax(pet.Health.MaxValue, Mag(WeaponModId.SecondWind, focus)));
            Assert.IsTrue(expected > before, "positive control: Second Wind restores something");
            Assert.AreEqual(expected, pet.Health.Current, "Second Wind: the focus fraction of the pet's max health");
            Assert.IsFalse(Has(pet, DebuffId), "Cleanse (chance 1.0 at a full roll) strips the pet's debuff");
            Assert.IsTrue(Has(owner, DebuffId), "the pet half cleanses the PET, never the owner");
        }

        [TestMethod]
        public void Pet_kill_half_scales_with_the_mod_transfer_percent()
        {
            var focus = UtilityFocus();
            var pet = FocusPet(Owner(focus));
            pet.Health.Current = 1;

            PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, 0.5);
            SummonerFocus.ApplyPetKillUtility(pet, Monster());

            Assert.AreEqual((uint)(1 + WeaponModCombat.RestoreFromMax(pet.Health.MaxValue, Mag(WeaponModId.SecondWind, focus) * 0.5)), pet.Health.Current, "half the Second Wind fraction");
        }

        [TestMethod]
        public void Pet_kill_half_negatives()
        {
            var focus = UtilityFocus();
            var victim = Monster();

            void Untouched(CombatPet pet, Creature v, string because)
            {
                pet.Health.Current = 1;
                Give(pet, DebuffId, 0x50000001);
                SummonerFocus.ApplyPetKillUtility(pet, v);
                Assert.AreEqual(1u, pet.Health.Current, because + ": no Second Wind");
                Assert.IsTrue(Has(pet, DebuffId), because + ": no Cleanse");
            }

            Untouched(FocusPet(Owner(focus), echo: true), victim, "Echo");
            Untouched(FocusPet(Owner(Wand(false, ImbuedEffectType.Undef, WeaponModId.SecondWind, WeaponModId.Cleanse))), victim, "plain wand");
            Untouched(FocusPet(Owner(focus)), PlayerTarget(), "a Player victim");
            Untouched(FocusPet(Owner(focus)), FocusPet(Owner(Wand(true))), "a Pet victim");

            var dead = FocusPet(Owner(focus));
            dead.Health.Current = 0;
            Give(dead, DebuffId, 0x50000001);
            Assert.IsTrue(dead.IsDead, "fixture");
            SummonerFocus.ApplyPetKillUtility(dead, victim);
            Assert.AreEqual(0u, dead.Health.Current, "a dead pet: no Second Wind");
            Assert.IsTrue(Has(dead, DebuffId), "a dead pet: no Cleanse");

            var queued = 0;
            var saved = SummonerFocus.PetKillEnqueue;

            try
            {
                SummonerFocus.PetKillEnqueue = (p, a) => queued++;
                SummonerFocus.EnqueuePetKillUtility(FocusPet(Owner(focus)), victim);
                Assert.AreEqual(1, queued, "positive control: a focus with Second Wind and Cleanse queues");
                SummonerFocus.EnqueuePetKillUtility(FocusPet(Owner(Wand(true))), victim);
                SummonerFocus.EnqueuePetKillUtility(FocusPet(Owner(Wand(true, ImbuedEffectType.Undef, WeaponModId.Siphon))), victim);
                SummonerFocus.EnqueuePetKillUtility(FocusPet(Owner(focus), echo: true), victim);
                SummonerFocus.EnqueuePetKillUtility(victim, Monster());
                SummonerFocus.EnqueuePetKillUtility(null, victim);
                SummonerFocus.EnqueuePetKillUtility(FocusPet(Owner(focus)), PlayerTarget());

                Assert.AreEqual(1, queued, "only the Second Wind / Cleanse focus queues: a focus without either, an Echo, a non-pet killer, no killer and a Player victim queue nothing");

                PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, 0.0);
                SummonerFocus.EnqueuePetKillUtility(FocusPet(Owner(focus)), victim);
                Assert.AreEqual(1, queued, "transfer 0: both reads are 0, nothing queues");
            }
            finally
            {
                SummonerFocus.PetKillEnqueue = saved;
            }
        }

        /// <summary>
        /// The death path queues the pet half and keeps the owner payout, each on its own queue, exactly once (source pin on
        /// the two statements; the queue target is proven above).
        /// </summary>
        private static string Source(params string[] parts) =>
            System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server" }.Concat(parts).ToArray())).Replace("\r\n", "\n");

        /// <summary>The production queue is the PET's own (the behavioural tests replace the seam, so only this pin holds its default).</summary>
        [TestMethod]
        public void Pet_kill_seam_default_is_the_pets_own_queue()
        {
            StringAssert.Contains(Source("Entity", "SummonerFocus.cs"),
                "\n        internal static Action<CombatPet, IAction> PetKillEnqueue = (pet, action) => pet.EnqueueAction(action);\n");
        }

        /// <summary>
        /// The spell-sink half of pet Siphon (SpellProjectile cannot be driven here: its Spell needs the dat): exact pin on the
        /// branch head, which is the player branch's else and carries the DirectHit gate, so a proc, AOE child or Cascade hop
        /// never rolls; and on its one call.
        /// </summary>
        [TestMethod]
        public void Spell_sink_siphon_branch_is_DirectHit_gated()
        {
            StringAssert.Contains(Source("WorldObjects", "SpellProjectile.cs"),
                "                        player.ApplyWeaponModSpellHit(creatureTarget, damage.Value * ClassAbilityAoeDamageMultiplier);\n"
                + "                    // Summoner casters: a pet's landed damaging projectile rolls its owner's focus Siphon once, as a\n"
                + "                    // player's does with a Siphon wand; a derived emission (proc, AOE child, Cascade hop) never rolls\n"
                + "                    else if (sourceCreature is CombatPet siphonPet && damage.Value > 0.0f && IncomingDamageOrigin == IncomingDamageOrigin.DirectHit)\n"
                + "                        SummonerFocus.TryPetSiphon(siphonPet, creatureTarget);\n");
        }

        [TestMethod]
        public void Death_path_keeps_the_owner_payout_and_adds_the_pet_half()
        {
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "WorldObjects", "Creature_Death.cs")).Replace("\r\n", "\n");

            StringAssert.Contains(src, "            else if (ResolveWeaponModPetOwner(lastDamager) is Player petOwner)\n                petOwner.EnqueueAction(new ActionEventDelegate(() => petOwner.ApplyWeaponModPetKill(this)));\n");
            StringAssert.Contains(src, "\n            SummonerFocus.EnqueuePetKillUtility(lastDamager?.TryGetAttacker(), this);\n");
        }
    }
}
