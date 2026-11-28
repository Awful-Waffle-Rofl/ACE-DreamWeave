using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using DatSpellBase = ACE.DatLoader.Entity.SpellBase;
using DbSpell = ACE.Database.Models.World.Spell;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Players can heal and buff a Hold knight with spells. A knight is RubberGlue, and Player.CheckPKStatusVsTarget refused every
    /// spell (beneficial too) from a caster of a different PK status against a non-NPK creature; Player.IsInvalidTarget also refused
    /// every non-heal spell on a knight. A beneficial spell on a knight now skips the PK-type rule and a buff is a valid target
    /// spell; harmful spells and every other creature are decided as before, and a spell heal still takes the knight's heal
    /// conversion. Dat-free, no PropertyManager key read.
    /// </summary>
    [TestClass]
    public class WorldEventHoldKnightSpellTests
    {
        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        private static FakeHoldRunView Run(uint id = 41) =>
            new FakeHoldRunView(id) { Tuning = WorldEventHoldTuning.Defaults with { PlayerHealEnabled = true, PlayerHealCap = 1.0, PlayerHealMult = 1.0, HealBudgetPerWave = 0, HealReductionAfterHit = 0 } };

        private static Creature Knight(FakeHoldRunView run = null)
        {
            var knight = HoldFixtures.Tagged(run ?? Run(), HoldUnitKind.Knight);
            knight.PlayerKillerStatus = PlayerKillerStatus.RubberGlue;
            return knight;
        }

        // ---- the PK-type rule -----------------------------------------------------------------------------------

        [TestMethod]
        public void Beneficial_OnKnight_IsAllowed_ForNpkAndPkCasters()
        {
            var knight = Knight();

            foreach (var status in new[] { PlayerKillerStatus.NPK, PlayerKillerStatus.PK })
            {
                var caster = SeededPlayer(status);
                Assert.IsNull(caster.CheckPKStatusVsTarget(knight, FakeSpell(true, SpellType.Boost)), $"{status} caster: a heal on a knight passes the PK rules");
                Assert.IsNull(caster.CheckPKStatusVsTarget(knight, FakeSpell(true, SpellType.Enchantment)), $"{status} caster: a buff on a knight passes the PK rules");
            }
        }

        [TestMethod]
        public void Harmful_OnKnight_StaysRefused()
        {
            var knight = Knight();

            foreach (var status in new[] { PlayerKillerStatus.NPK, PlayerKillerStatus.PK })
            {
                var caster = SeededPlayer(status);
                Assert.IsNotNull(caster.CheckPKStatusVsTarget(knight, FakeSpell(false, SpellType.Boost)), $"{status} caster: a harmful spell on a knight is refused");
                Assert.IsNotNull(caster.CheckPKStatusVsTarget(knight, null), $"{status} caster: a melee or missile probe (null spell) is refused");
            }
        }

        [TestMethod]
        public void NonKnightRubberGlueCreature_IsUnchanged()
        {
            var monster = HoldFixtures.MakeCreature();
            monster.PlayerKillerStatus = PlayerKillerStatus.RubberGlue;

            var waveMonster = HoldFixtures.Tagged(Run(), HoldUnitKind.Monster);
            waveMonster.PlayerKillerStatus = PlayerKillerStatus.RubberGlue;

            var caster = SeededPlayer(PlayerKillerStatus.NPK);
            Assert.IsNotNull(caster.CheckPKStatusVsTarget(monster, FakeSpell(true, SpellType.Boost)), "an untagged RubberGlue creature still refuses a beneficial spell from a different status");
            Assert.IsNotNull(caster.CheckPKStatusVsTarget(waveMonster, FakeSpell(true, SpellType.Boost)), "so does a Hold wave monster");
        }

        [TestMethod]
        public void Seam_IsTrueOnlyForAKnightAndANonHarmfulSpell()
        {
            Assert.IsTrue(WorldEventHooks.SkipsPkTypeRule(Knight(), FakeSpell(true, SpellType.Boost)));
            Assert.IsFalse(WorldEventHooks.SkipsPkTypeRule(Knight(), FakeSpell(false, SpellType.Boost)));
            Assert.IsFalse(WorldEventHooks.SkipsPkTypeRule(Knight(), null));
            Assert.IsFalse(WorldEventHooks.SkipsPkTypeRule(HoldFixtures.Tagged(Run(), HoldUnitKind.Monster), FakeSpell(true, SpellType.Boost)));
            Assert.IsFalse(WorldEventHooks.SkipsPkTypeRule(HoldFixtures.MakeCreature(), FakeSpell(true, SpellType.Boost)));
            Assert.IsFalse(WorldEventHooks.SkipsPkTypeRule(null, FakeSpell(true, SpellType.Boost)));
        }

        [TestMethod]
        public void SourcePin_SeamRunsBeforeTheCreaturePkBranch_Once()
        {
            const string path = "Source/ACE.Server/WorldObjects/Player_Combat.cs";
            const string sig = "public override List<WeenieErrorWithString> CheckPKStatusVsTarget(WorldObject target, Spell spell)";
            const string hook = "if (ACE.Server.WorldEvents.WorldEventHooks.SkipsPkTypeRule(targetCreature, spell))";
            const string before = "var targetPlayer = target as Player;";
            const string after = "if (targetCreature.PlayerKillerStatus != PlayerKillerStatus.NPK && PlayerKillerStatus != targetCreature.PlayerKillerStatus)";

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(path), sig);

            Assert.AreEqual(1, CountOf(body, hook), "the seam is called exactly once");
            Assert.IsTrue(SeamInOrder(body, hook, before, after));

            // mutation: moved after the creature branch, or removed
            var moved = body.Replace(hook, "if (false)") + "\n" + hook;
            Assert.IsFalse(SeamInOrder(moved, hook, before, after), "discrimination: a seam after the creature branch fails the pin");
            Assert.AreNotEqual(1, CountOf(body.Replace(hook, "if (false)"), hook), "discrimination: without the seam the count fails");
        }

        // ---- the target gate (Player.IsInvalidTarget) --------------------------------------------------------------

        private static Spell Strength() => FakeSpell(true, SpellType.Enchantment, statModType: EnchantmentTypeFlags.Attribute, statModKey: (uint)PropertyAttribute.Strength);

        [TestMethod]
        public void TargetGate_HealNeedsPlayerHeal_BuffNeedsALiveKnightWithPlayerHeal_HarmfulNever()
        {
            var knight = Knight();
            var heal = FakeSpell(true, SpellType.Boost, health: true);
            var harm = FakeSpell(false, SpellType.Boost, health: true);

            Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(knight, heal, out var invalid) && !invalid, "a health heal is valid");
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(knight, Strength(), out invalid) && !invalid, "positive control: a Strength-style buff is valid (was refused before this change)");
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(knight, harm, out invalid) && invalid, "a harmful spell is invalid");

            // player_heal off (the Thread Defense pillar runs this way) refuses heals AND buffs
            var noHeal = Knight(new FakeHoldRunView(77) { Tuning = WorldEventHoldTuning.Defaults with { PlayerHealEnabled = false } });
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(noHeal, heal, out invalid) && invalid, "heal refused while player_heal is off");
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(noHeal, Strength(), out invalid) && invalid, "buff refused while player_heal is off");

            // a run that ended accepts neither
            var ended = Run(88);
            var endedKnight = Knight(ended);
            ended.IsActive = false;
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(endedKnight, Strength(), out invalid) && invalid, "run not active: buff refused");
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(endedKnight, heal, out invalid) && invalid, "run not active: heal refused");
        }

        [TestMethod]
        public void ThreadDefensePillar_AcceptsNoBuffs()
        {
            var view = new ThreadDefenseHoldView(ThreadDefenseHoldView.BuildTuning(240, 45));
            view.Activate();

            var pillar = HoldFixtures.MakeCreature("Pillar");
            Assert.IsTrue(ThreadDefenseHold.PreparePillar(pillar, view, 1000), "fixture: the real pillar tag");
            Assert.IsTrue(view.IsActive, "control: the defense is running, so only player_heal can be what refuses");
            Assert.IsFalse(view.Tuning.PlayerHealEnabled, "control: the pillar runs with player_heal off");

            Assert.IsFalse(pillar.AcceptsHoldBuff, "the pillar takes no buff");
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(pillar, Strength(), out var invalid) && invalid, "a buff on the pillar is refused");
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(pillar, FakeSpell(true, SpellType.Boost, health: true), out invalid) && invalid, "so is a heal");
        }

        [TestMethod]
        public void BuffWhitelist_EachRefusedKind_IsInvalid_AndEachPredicateIsLoadBearing()
        {
            var knight = Knight();

            var refused = new (string Name, Spell Spell)[]
            {
                ("HealOverTime", FakeSpell(true, SpellType.Enchantment, dot: true)),
                ("HealOverTime by stat key only (no DamageOverTime flag, the shape of 5171/5208/5314)", FakeSpell(true, SpellType.Enchantment, statModType: EnchantmentTypeFlags.Int | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive, statModKey: (uint)PropertyInt.HealOverTime)),
                ("MaxHealth enchantment", FakeSpell(true, SpellType.Enchantment, statModType: EnchantmentTypeFlags.SecondAtt, statModKey: (uint)PropertyAttribute2nd.MaxHealth)),
                ("HealthRate regen", FakeSpell(true, SpellType.Enchantment, statModType: EnchantmentTypeFlags.Float, statModKey: (uint)PropertyFloat.HealthRate)),
                ("Dispel", FakeSpell(true, SpellType.Dispel)),
                ("PortalSending", FakeSpell(true, SpellType.PortalSending)),
                ("FellowEnchantment", FakeSpell(true, SpellType.FellowEnchantment)),
                ("Transfer", FakeSpell(true, SpellType.Transfer)),
                ("beneficial stamina Boost", FakeSpell(true, SpellType.Boost)),
            };

            foreach (var (name, spell) in refused)
            {
                Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(knight, spell, out var invalid) && invalid, $"{name} on a knight is refused");
                Assert.IsFalse(WorldEventHoldRules.IsKnightBuffSpell(spell.IsBeneficial, spell.MetaSpellType, spell.IsDamageOverTime, spell.StatModType, spell.StatModKey), $"{name}: not a buff");
            }

            // allowed neighbours of each refused kind: the predicate discriminates on the exact field, not on the type alone
            Assert.IsTrue(WorldEventHoldRules.IsKnightBuffSpell(true, SpellType.Enchantment, false, EnchantmentTypeFlags.SecondAtt, (uint)PropertyAttribute2nd.MaxStamina), "a max-stamina enchantment is a buff");
            Assert.IsTrue(WorldEventHoldRules.IsKnightBuffSpell(true, SpellType.Enchantment, false, EnchantmentTypeFlags.Float, (uint)PropertyFloat.StaminaRate), "a stamina-regen enchantment is a buff");
            Assert.IsTrue(WorldEventHoldRules.IsKnightBuffSpell(true, SpellType.Enchantment, false, EnchantmentTypeFlags.Attribute, (uint)PropertyAttribute.Endurance), "Endurance stays allowed");
            Assert.IsFalse(WorldEventHoldRules.IsKnightBuffSpell(false, SpellType.Enchantment, false, EnchantmentTypeFlags.Attribute, (uint)PropertyAttribute.Strength), "harmful");
        }

        [TestMethod]
        public void TargetGate_PureCore()
        {
            Assert.IsFalse(WorldEventHooks.IsInvalidHoldKnightSpell(true, true, true, false), "heal, accepted");
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldKnightSpell(false, true, true, true), "heal refused when heals are off, even though it is also beneficial");
            Assert.IsFalse(WorldEventHooks.IsInvalidHoldKnightSpell(false, false, true, true), "buff accepted with heals off");
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldKnightSpell(true, false, false, true), "buff refused on a knight that does not accept buffs");
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldKnightSpell(true, false, true, false), "non-buff refused");
        }

        [TestMethod]
        public void SourcePin_DeathHeldMemberRefusalRunsBeforeTheHoldGate()
        {
            const string path = "Source/ACE.Server/WorldObjects/Player_Magic.cs";
            const string sig = "protected bool IsInvalidTarget(Spell spell, WorldObject target)";
            const string held = "GauntletHooks.RefusesHeldSupport(this, target, spell.IsBeneficial)";
            const string hold = "WorldEventHooks.IsInvalidHoldSpellTarget(targetCreature, spell, out var holdInvalid)";

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(path), sig);

            Assert.AreEqual(1, CountOf(body, held));
            Assert.AreEqual(1, CountOf(body, hold));
            Assert.IsTrue(body.IndexOf(held, StringComparison.Ordinal) < body.IndexOf(hold, StringComparison.Ordinal), "the held-member refusal comes first");

            // mutation: swapped order fails the pin
            var swapped = body.Replace(held, "@@H@@").Replace(hold, held).Replace("@@H@@", hold);
            Assert.IsFalse(swapped.IndexOf(held, StringComparison.Ordinal) < swapped.IndexOf(hold, StringComparison.Ordinal), "discrimination: the hold gate first fails the pin");
        }

        // ---- a spell heal still takes the knight's conversion ---------------------------------------------------

        [TestMethod]
        public void SpellHeal_LandsThroughTheKnightsConversion_NotRaw()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<Action>();

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add(a); return true; };

                var run = Run();
                var knight = Knight(run);
                knight.Health.Current = knight.Health.MaxValue / 2;
                var before = knight.Health.Current;
                const double raw = 200;

                // what WorldObject_Magic hands the hook for a player's heal spell
                Assert.IsTrue(WorldEventHooks.TryQueuePlayerHeal(null, knight, raw, false, null, ACE.Entity.Enum.ChatMessageType.Magic));
                Assert.AreEqual(before, knight.Health.Current, "queued, not written on the caster's thread");
                Assert.AreEqual(1, queued.Count);

                queued[0]();

                double tokens = 0, lastAt = 0;
                var expected = WorldEventHoldRules.ConvertPlayerHeal(raw, knight.Health.MaxValue, 1.0, ref tokens, ref lastAt, 1e9);
                var healed = knight.Health.Current - before;

                Assert.IsTrue(healed > 0, "positive control: the heal landed");
                Assert.AreNotEqual((uint)raw, healed, "not the raw spell number");
                Assert.AreEqual((uint)expected, healed, "exactly the converted amount (ConvertPlayerHeal)");
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        [TestMethod]
        public void SourcePin_SpellHealQueuesBeforeTheVitalWrite_AndSkipsTheRawBoost()
        {
            const string path = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";
            var src = PooledLootSourceText.Read(path);

            var queue = src.IndexOf("var holdHealQueued = spell.VitalDamageType == DamageType.Health && tryBoost > 0 && player != null", StringComparison.Ordinal);
            var call = src.IndexOf("WorldEventHooks.TryQueuePlayerHeal(player, targetCreature, tryBoost, false, null, ChatMessageType.Magic);", StringComparison.Ordinal);
            var write = src.IndexOf("boost = targetCreature.UpdateVitalDelta(targetCreature.Health, tryBoost);", StringComparison.Ordinal);
            var skip = src.IndexOf("if (holdHealQueued || gauntletHealQueued)", StringComparison.Ordinal);

            Assert.IsTrue(queue >= 0 && call > queue, "the player spell heal is handed to TryQueuePlayerHeal");
            Assert.IsTrue(skip > call, "a queued heal zeroes the raw boost");
            Assert.IsTrue(write > skip, "the raw vital write is reachable only after the queued-heal arm");
        }

        // ---- helpers ----------------------------------------------------------------------------------------------

        private static int CountOf(string text, string needle)
        {
            var n = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        private static bool SeamInOrder(string body, string hook, string before, string after)
        {
            var b = body.IndexOf(before, StringComparison.Ordinal);
            var h = body.IndexOf(hook, StringComparison.Ordinal);
            var a = body.IndexOf(after, StringComparison.Ordinal);
            return b >= 0 && h >= 0 && a >= 0 && h > b && h < a;
        }

        private static Player SeededPlayer(PlayerKillerStatus status)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(player, "positionCache", new Dictionary<PositionType, ACE.Entity.Position>());
            SetInherited(player, "ephemeralPositions", new Dictionary<PositionType, ACE.Entity.Position>());

            player.PlayerKillerStatus = status;
            player.Location = new ACE.Entity.Position { LandblockId = new LandblockId(0x00900100), PositionX = 50f, PositionY = 50f, RotationW = 1f };

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static Spell FakeSpell(bool beneficial, SpellType type, bool health = false, EnchantmentTypeFlags statModType = 0, uint statModKey = 0, bool dot = false)
        {
            var spellBase = (DatSpellBase)RuntimeHelpers.GetUninitializedObject(typeof(DatSpellBase));

            SetBackingField(spellBase, "Name", beneficial ? "Test Beneficial Other" : "Test Harmful Other");
            SetBackingField(spellBase, "School", MagicSchool.LifeMagic);
            SetBackingField(spellBase, "Category", SpellCategory.Undef);
            SetBackingField(spellBase, "MetaSpellId", 9999u);
            SetBackingField(spellBase, "MetaSpellType", type);
            SetBackingField(spellBase, "Bitfield", (beneficial ? (uint)SpellFlags.Beneficial : 0u) | (dot ? (uint)SpellFlags.DamageOverTime : 0u));
            SetBackingField(spellBase, "NonComponentTargetType", (uint)ItemType.Creature);

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;
            spell._spell = new DbSpell { StatModType = (uint)statModType, StatModKey = statModKey, Boost = beneficial ? 50 : -50, BoostVariance = 0, DamageType = (int)(health ? DamageType.Health : DamageType.Stamina) };

            return spell;
        }

        private static void SetBackingField(object target, string propertyName, object value)
        {
            var field = target.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{target.GetType().Name}.<{propertyName}>k__BackingField was not found by reflection");
            field.SetValue(target, value);
        }
    }
}
