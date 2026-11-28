using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Entity;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.Avatars.Gauntlet.GauntletFighterFixtures;

using DatSpellBase = ACE.DatLoader.Entity.SpellBase;
using DbSpell = ACE.Database.Models.World.Spell;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Players can heal and buff the Avatar fighter: the fighter is RubberGlue, and Player.CheckPKStatusVsTarget refuses
    /// every spell (beneficial too) from a caster of a different PK status against a non-NPK creature. A beneficial spell
    /// on the fighter now skips that PK-type rule; harmful spells and every other target are decided as before. Dat-free,
    /// no PropertyManager key read.
    /// </summary>
    [TestClass]
    public class GauntletFighterPkTypeTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => EnsureInitialized();

        [TestMethod]
        public void Beneficial_OnFighter_IsAllowed_ForNpkAndPkCasters()
        {
            var fighter = Fighter();
            Assert.AreEqual(PlayerKillerStatus.RubberGlue, fighter.PlayerKillerStatus, "fixture: the fighter is RubberGlue");

            foreach (var status in new[] { PlayerKillerStatus.NPK, PlayerKillerStatus.PK })
            {
                var caster = SeededPlayer(status);
                Assert.IsNull(caster.CheckPKStatusVsTarget(fighter, FakeSpell(beneficial: true)), $"{status} caster: a heal or buff on the fighter passes the PK rules");
            }
        }

        [TestMethod]
        public void Harmful_OnFighter_StaysRefused()
        {
            var fighter = Fighter();

            foreach (var status in new[] { PlayerKillerStatus.NPK, PlayerKillerStatus.PK })
            {
                var caster = SeededPlayer(status);
                Assert.IsNotNull(caster.CheckPKStatusVsTarget(fighter, FakeSpell(beneficial: false)), $"{status} caster: a harmful spell on the fighter is refused");
                Assert.IsNotNull(caster.CheckPKStatusVsTarget(fighter, null), $"{status} caster: a melee or missile probe (null spell) is refused");
            }
        }

        [TestMethod]
        public void NonFighterRubberGlueCreature_IsUnchanged()
        {
            var monster = Monster();
            monster.PlayerKillerStatus = PlayerKillerStatus.RubberGlue;

            var caster = SeededPlayer(PlayerKillerStatus.NPK);
            Assert.IsNotNull(caster.CheckPKStatusVsTarget(monster, FakeSpell(beneficial: true)), "any other non-NPK creature still refuses a beneficial spell from a different status");
        }

        [TestMethod]
        public void Seam_IsTrueOnlyForAFighterAndABeneficialSpell()
        {
            Assert.IsTrue(GauntletAvatarFighter.SkipsPkTypeRule(Fighter(), FakeSpell(beneficial: true)));
            Assert.IsFalse(GauntletAvatarFighter.SkipsPkTypeRule(Fighter(), FakeSpell(beneficial: false)));
            Assert.IsFalse(GauntletAvatarFighter.SkipsPkTypeRule(Fighter(), null));
            Assert.IsFalse(GauntletAvatarFighter.SkipsPkTypeRule(Monster(), FakeSpell(beneficial: true)));
            Assert.IsFalse(GauntletAvatarFighter.SkipsPkTypeRule(null, FakeSpell(beneficial: true)));
        }

        [TestMethod]
        public void SourcePin_SeamRunsBeforeTheCreaturePkBranch_Once()
        {
            const string path = "Source/ACE.Server/WorldObjects/Player_Combat.cs";
            const string sig = "public override List<WeenieErrorWithString> CheckPKStatusVsTarget(WorldObject target, Spell spell)";
            const string hook = "if (ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter.SkipsPkTypeRule(targetCreature, spell))";
            const string before = "var targetPlayer = target as Player;";
            const string after = "if (targetCreature.PlayerKillerStatus != PlayerKillerStatus.NPK && PlayerKillerStatus != targetCreature.PlayerKillerStatus)";

            var src = PooledLootSourceText.Read(path);
            var body = PooledLootSourceText.MethodBody(src, sig);

            Assert.AreEqual(1, CountOf(body, hook), "the seam is called exactly once");
            Assert.IsTrue(SeamInOrder(body, hook, before, after));

            // mutation: moved after the creature branch, or removed
            var moved = body.Replace(hook, "if (false)") + "\n" + hook;
            Assert.IsFalse(SeamInOrder(moved, hook, before, after), "discrimination: a seam after the creature branch fails the pin");
            Assert.AreNotEqual(1, CountOf(body.Replace(hook, "if (false)"), hook), "discrimination: without the seam the count fails");
        }

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

        // ---- fixtures ----

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

        private static Spell FakeSpell(bool beneficial)
        {
            var spellBase = (DatSpellBase)RuntimeHelpers.GetUninitializedObject(typeof(DatSpellBase));

            SetBackingField(spellBase, "Name", beneficial ? "Test Heal Other" : "Test Harm Other");
            SetBackingField(spellBase, "School", MagicSchool.LifeMagic);
            SetBackingField(spellBase, "Category", SpellCategory.Undef);
            SetBackingField(spellBase, "MetaSpellId", 9999u);
            SetBackingField(spellBase, "MetaSpellType", SpellType.Boost);
            SetBackingField(spellBase, "Bitfield", beneficial ? (uint)SpellFlags.Beneficial : 0u);
            SetBackingField(spellBase, "NonComponentTargetType", (uint)ItemType.Creature);

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;
            spell._spell = new DbSpell { Boost = beneficial ? 50 : -50, BoostVariance = 0, DamageType = (int)DamageType.Health };

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
