using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The PvP defense sliders (Docs/Pvp/DESIGN.md "PvP rules (levers)"): pvp_melee_defense_mod and
    /// pvp_missile_defense_mod at DF1 (DamageEvent.GetEvadeChance), pvp_magic_defense_mod at DF2
    /// (WorldObject.TryResistSpell). Exercised through PvpRules' choke-point entries on a PvP pair of
    /// reflection-seeded Players, with the evade / resist chance derived from the same SkillCheck the real call
    /// sites use. Where the real call sites put the scaled value is pinned by a source scan at the bottom, because
    /// a real evade roll needs a full DamageEvent (client dat) this host does not have.
    /// </summary>
    [TestClass]
    public class PvpDefenseModsTests
    {
        private Func<PvpRuleDials> savedDialSource;
        private Func<Player, Player, bool> savedArenaScope;

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpRuleTunables.DialSource;
            savedArenaScope = PvpClassifier.ArenaScopeSource;
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedDialSource;
            PvpClassifier.ArenaScopeSource = savedArenaScope;
        }

        private static void UseDials(PvpRuleDials dials) => PvpRuleTunables.DialSource = () => dials;

        private static PvpRuleDials Mods(double melee = 1.0, double missile = 1.0, double magic = 1.0, bool enabled = true) =>
            PvpRuleTunables.Defaults with { Enabled = enabled, MeleeDefenseMod = melee, MissileDefenseMod = missile, MagicDefenseMod = magic };

        private static Player SeededPlayer()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            player.PlayerKillerStatus = PlayerKillerStatus.PK;

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        // The evade chance exactly as DamageEvent.GetEvadeChance derives it from the (possibly scaled) defense skill.
        private static double EvadeChance(uint attackSkill, uint defenseSkill) => 1.0 - SkillCheck.GetSkillChance(attackSkill, defenseSkill);

        // The resist chance exactly as WorldObject.MagicDefenseCheck derives it.
        private static double ResistChance(uint magicSkill, uint magicDefense) => 1.0 - SkillCheck.GetSkillChance((int)magicSkill, (int)magicDefense);

        [TestMethod]
        public void ScaleDefenseSkill_IsIdentityAtOne_ScalesAndSanitizes()
        {
            Assert.AreEqual(300u, PvpRules.ScaleDefenseSkill(300, 1.0));
            Assert.AreEqual(150u, PvpRules.ScaleDefenseSkill(300, 0.5));
            Assert.AreEqual(600u, PvpRules.ScaleDefenseSkill(300, 2.0));
            Assert.AreEqual(0u, PvpRules.ScaleDefenseSkill(300, 0.0), "0 is a legal value");
            Assert.AreEqual(300u, PvpRules.ScaleDefenseSkill(300, double.NaN));
            Assert.AreEqual(300u, PvpRules.ScaleDefenseSkill(300, -2.0));
            Assert.AreEqual((uint)int.MaxValue, PvpRules.ScaleDefenseSkill(uint.MaxValue, 5.0), "saturates at int.MaxValue: the skill-check callers cast to int");
            Assert.AreEqual((uint)int.MaxValue, PvpRules.ScaleDefenseSkill(300, 1e12));

            // a huge mod must make the defender nearly unhittable, not wrap negative and make them trivially hittable
            var huge = PvpRules.ScaleDefenseSkill(300, 1e12);
            Assert.IsTrue(SkillCheck.GetSkillChance(300u, huge) <= 0.001, "huge defense must not be hittable");
            Assert.IsTrue(SkillCheck.GetSkillChance(300, (int)huge) <= 0.001, "the int-cast overload callers use must agree");
        }

        [TestMethod]
        public void Pvp_MeleeMod_Half_LowersTheDefenderEvadeChance()
        {
            UseDials(Mods(melee: 0.5));

            var scaled = PvpRules.ApplyDefenseMod(SeededPlayer(), SeededPlayer(), CombatType.Melee, 300);

            Assert.AreEqual(150u, scaled);
            Assert.IsTrue(EvadeChance(300, scaled) < EvadeChance(300, 300), "halved defense must evade less");
        }

        [TestMethod]
        public void Pvp_MissileMod_AppliesToMissileOnly_AndMeleeModToMeleeOnly()
        {
            UseDials(Mods(melee: 0.5, missile: 2.0));

            Assert.AreEqual(150u, PvpRules.ApplyDefenseMod(SeededPlayer(), SeededPlayer(), CombatType.Melee, 300));
            Assert.AreEqual(600u, PvpRules.ApplyDefenseMod(SeededPlayer(), SeededPlayer(), CombatType.Missile, 300));
            Assert.AreEqual(300u, PvpRules.ApplyDefenseMod(SeededPlayer(), SeededPlayer(), CombatType.Magic, 300), "magic is DF2's, never DF1's");
        }

        [TestMethod]
        public void Pvp_MagicMod_Half_LowersTheTargetResistChance()
        {
            UseDials(Mods(magic: 0.5));

            var scaled = PvpRules.ApplyMagicDefenseMod(SeededPlayer(), SeededPlayer(), 300);

            Assert.AreEqual(150u, scaled);
            Assert.IsTrue(ResistChance(300, scaled) < ResistChance(300, 300), "halved magic defense must resist less");
        }

        [TestMethod]
        public void Identity_AtOne_ChangesNothing_AndReportsNothing()
        {
            UseDials(Mods());

            var before = PvpRules.GetAppliedCount(PvpChokePoint.DF1) + PvpRules.GetAppliedCount(PvpChokePoint.DF2);

            Assert.AreEqual(300u, PvpRules.ApplyDefenseMod(SeededPlayer(), SeededPlayer(), CombatType.Melee, 300));
            Assert.AreEqual(300u, PvpRules.ApplyDefenseMod(SeededPlayer(), SeededPlayer(), CombatType.Missile, 300));
            Assert.AreEqual(300u, PvpRules.ApplyMagicDefenseMod(SeededPlayer(), SeededPlayer(), 300));

            Assert.AreEqual(before, PvpRules.GetAppliedCount(PvpChokePoint.DF1) + PvpRules.GetAppliedCount(PvpChokePoint.DF2));
        }

        [TestMethod]
        public void MasterSwitchOff_ChangesNothing()
        {
            UseDials(Mods(melee: 0.5, missile: 0.5, magic: 0.5, enabled: false));

            Assert.AreEqual(300u, PvpRules.ApplyDefenseMod(SeededPlayer(), SeededPlayer(), CombatType.Melee, 300));
            Assert.AreEqual(300u, PvpRules.ApplyMagicDefenseMod(SeededPlayer(), SeededPlayer(), 300));
        }

        [TestMethod]
        public void Pve_IsUnaffected_AndReadsNoLever()
        {
            // Any read of the dials throws: a PvE interaction must classify first and never reach them.
            PvpRuleTunables.DialSource = () => throw new InvalidOperationException("a PvE path must not read a PvP lever");

            var player = SeededPlayer();
            var monster = TestCreatures.CreateDefender();

            // Player attacker vs monster defender, and (monster attacker, player defender) - neither is PvP.
            Assert.AreEqual(300u, PvpRules.ApplyDefenseMod(player, monster, CombatType.Melee, 300));
            Assert.AreEqual(300u, PvpRules.ApplyDefenseMod(monster, player, CombatType.Melee, 300));
            Assert.AreEqual(300u, PvpRules.ApplyMagicDefenseMod(player, monster, 300));
            Assert.AreEqual(300u, PvpRules.ApplyMagicDefenseMod(monster, player, 300));

            // Self-targeting is never PvP either.
            Assert.AreEqual(300u, PvpRules.ApplyMagicDefenseMod(player, player, 300));
        }

        // ---------------- where the real call sites put the scaled value ----------------

        private static string ServerSource(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "property-registry.tsv")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "could not find the repo root by walking up from the test directory");

            return File.ReadAllText(Path.Combine(new[] { dir.FullName, "Source", "ACE.Server" }.Concat(relative).ToArray()));
        }

        /// <summary>
        /// DF1: the evade roll consumes the scaled skill, and the stored EffectiveDefenseSkill (the only other
        /// reader, the debug dump) stays on the unscaled value. DF2: the scale lands before the resist roll.
        /// </summary>
        [TestMethod]
        public void CallSites_ScaleOnlyTheRoll_AndKeepTheStoredSkillUnscaled()
        {
            var evade = ServerSource("Entity", "DamageEvent.cs");

            StringAssert.Contains(evade, "EffectiveDefenseSkill = defender.GetEffectiveDefenseSkill(CombatType);");
            StringAssert.Contains(evade, "var rollDefenseSkill = PvpRules.ApplyDefenseMod(attacker, defender, CombatType, EffectiveDefenseSkill);");
            StringAssert.Contains(evade, "SkillCheck.GetSkillChance(EffectiveAttackSkill, rollDefenseSkill)");
            Assert.IsFalse(evade.Contains("EffectiveDefenseSkill = PvpRules"), "the stored skill must stay unscaled");

            var magic = ServerSource("WorldObjects", "WorldObject_Magic.cs");
            var scale = magic.IndexOf("difficulty = PvpRules.ApplyMagicDefenseMod(this, targetCreature, difficulty);", StringComparison.Ordinal);
            var roll = magic.IndexOf("MagicDefenseCheck(magicSkill, difficulty, out float resistChance)", StringComparison.Ordinal);

            Assert.IsTrue(scale >= 0 && roll > scale, "DF2 must scale the difficulty before the resist roll consumes it");
        }
    }
}