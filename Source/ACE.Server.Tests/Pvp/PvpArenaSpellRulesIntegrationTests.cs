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
using DatSpellBase = ACE.DatLoader.Entity.SpellBase;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Code-review follow-up: goes all the way THROUGH Player.CheckPKStatusVsTarget with a real Spell for two
    /// match-bound players, rather than calling PvpArenaSpellRulesTests' pure PvpArenaSpellRules.IsRefused
    /// directly - so a wiring mistake between the gate and the rule (wrong school/category/id passed, wrong
    /// error pair, wrong FFA detection) fails here even though the pure rule's own unit tests would not
    /// catch it.
    ///
    /// FAKING THE SPELL. `new Spell(spellId)` reads DatManager.PortalDat.SpellTable immediately in its own
    /// constructor (see MlDigsiteDrumSpellTests, MonsterEffectCastspellTests, MonsterEffectRecastTests for the
    /// same harness limit), and no dat file is loaded in this test assembly. Every path under test here reads
    /// only Spell.IsHarmful (-> _spellBase.Bitfield), Spell.School and Spell.Category (both plain _spellBase
    /// fields) and Spell.Id (-> _spellBase.MetaSpellId) - nothing that needs DatManager or the World database
    /// spell row (_spell). So both Spell and its backing SpellBase are built with
    /// RuntimeHelpers.GetUninitializedObject (skipping every constructor) and their private-setter
    /// auto-properties are populated by reflection on the compiler-generated backing fields, the same
    /// technique PvpPlayerHookTests already uses for Player's own private/internal state.
    ///
    /// SEEDING otherwise follows PvpPlayerHookTests exactly: a Player is RuntimeHelpers.GetUninitializedObject
    /// plus only the inherited WorldObject state CheckPKStatusVsTarget's arena branch reads, the binding goes
    /// in through SetPvpBindingForTests, and PropertyManager is never touched (RestrictSpells now comes off
    /// the binding, not a live tunable read).
    /// </summary>
    [TestClass]
    public class PvpArenaSpellRulesIntegrationTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private Func<bool> savedFriendlyFire;

        [TestInitialize]
        public void Setup()
        {
            savedFriendlyFire = PvpArenaHookSettings.FriendlyFireSource;
            PvpArenaHookSettings.FriendlyFireSource = () => false;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.FriendlyFireSource = savedFriendlyFire;
        }

        // ================= fixtures =================

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

        private static PvpMatch NewMatch(string modeKey) =>
            new PvpMatch(Guid.NewGuid(), modeKey, new List<PvpTeam>(), Now);

        private static PvpPlayerBinding Bind(PvpMatch match, int team, bool restrictSpells = true) =>
            new PvpPlayerBinding(match, team, PvpMatchState.Live, true, true, true, true, true, restrictSpells);

        /// <summary>
        /// A faked Spell: never touches DatManager (see class remarks). Only School, Category, MetaSpellId and
        /// the Beneficial bit are set - every field PvpArenaSpellRules.IsRefused or Spell.IsHarmful can read.
        /// </summary>
        private static Spell FakeSpell(MagicSchool school, SpellCategory category, uint metaSpellId, bool beneficial)
        {
            var spellBase = (DatSpellBase)RuntimeHelpers.GetUninitializedObject(typeof(DatSpellBase));

            SetBackingField(spellBase, "School", school);
            SetBackingField(spellBase, "Category", category);
            SetBackingField(spellBase, "MetaSpellId", metaSpellId);
            SetBackingField(spellBase, "Bitfield", beneficial ? (uint)SpellFlags.Beneficial : 0u);

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;

            return spell;
        }

        private static void SetBackingField(object target, string propertyName, object value)
        {
            var field = target.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{target.GetType().Name}.<{propertyName}>k__BackingField was not found by reflection - has the property been renamed or given an explicit backing field?");
            field.SetValue(target, value);
        }

        private static void AssertCannotAffectAnyoneRefusal(List<WeenieErrorWithString> result, string why)
        {
            Assert.IsNotNull(result, why);
            Assert.AreEqual(2, result.Count, why);
            Assert.AreEqual(WeenieErrorWithString.YouFailToAffect_YouCannotAffectAnyone, result[0], why);
            Assert.AreEqual(WeenieErrorWithString._FailsToAffectYou_TheyCannotAffectAnyone, result[1], why);
        }

        // ================= 1v1: attribute debuff refused, defense-lowering allowed =================

        [TestMethod]
        public void OneVOne_CreatureEnchantment_AttributeDebuff_Refused()
        {
            var match = NewMatch(ArenaMapCatalog.OneVOneKey);
            var attacker = SeededPlayer(PlayerKillerStatus.PK);
            var target = SeededPlayer(PlayerKillerStatus.PK);

            attacker.SetPvpBindingForTests(Bind(match, 0));
            target.SetPvpBindingForTests(Bind(match, 1));

            var spell = FakeSpell(MagicSchool.CreatureEnchantment, SpellCategory.StrengthLowering, metaSpellId: 1, beneficial: false);

            AssertCannotAffectAnyoneRefusal(attacker.CheckPKStatusVsTarget(target, spell), "a Creature Enchantment attribute debuff must be refused in the arena");
        }

        /// <summary>Control: the same match, a defense-lowering Creature Enchantment spell is allowed.</summary>
        [TestMethod]
        public void OneVOne_CreatureEnchantment_DefenseLowering_Allowed()
        {
            var match = NewMatch(ArenaMapCatalog.OneVOneKey);
            var attacker = SeededPlayer(PlayerKillerStatus.PK);
            var target = SeededPlayer(PlayerKillerStatus.PK);

            attacker.SetPvpBindingForTests(Bind(match, 0));
            target.SetPvpBindingForTests(Bind(match, 1));

            var spell = FakeSpell(MagicSchool.CreatureEnchantment, SpellCategory.MeleeDefenseLowering, metaSpellId: 2, beneficial: false);

            Assert.IsNull(attacker.CheckPKStatusVsTarget(target, spell), "a Melee Defense Lowering spell must stay legal in 1v1");
        }

        // ================= FFA: Life Magic refused =================

        [TestMethod]
        public void Ffa_LifeMagicSpell_Refused()
        {
            var match = NewMatch(ArenaMapCatalog.FfaKey);
            var attacker = SeededPlayer(PlayerKillerStatus.PK);
            var target = SeededPlayer(PlayerKillerStatus.PK);

            attacker.SetPvpBindingForTests(Bind(match, 0));
            target.SetPvpBindingForTests(Bind(match, 1));

            var spell = FakeSpell(MagicSchool.LifeMagic, SpellCategory.HealthLowering, metaSpellId: 3, beneficial: false);

            AssertCannotAffectAnyoneRefusal(attacker.CheckPKStatusVsTarget(target, spell), "FFA must refuse a Life Magic spell that is not CurseRavenFury");
        }

        /// <summary>Control: the same FFA match, CurseRavenFury itself is allowed (the one FFA exception).</summary>
        [TestMethod]
        public void Ffa_CurseRavenFury_Allowed()
        {
            var match = NewMatch(ArenaMapCatalog.FfaKey);
            var attacker = SeededPlayer(PlayerKillerStatus.PK);
            var target = SeededPlayer(PlayerKillerStatus.PK);

            attacker.SetPvpBindingForTests(Bind(match, 0));
            target.SetPvpBindingForTests(Bind(match, 1));

            // CurseRavenFury is verified Life Magic / HealthLowering against the client dat (Docs/Pvp/DESIGN.md
            // "Arena spell rules"), so this is also the realistic school/category for it, not an arbitrary pick.
            var spell = FakeSpell(MagicSchool.LifeMagic, SpellCategory.HealthLowering, metaSpellId: (uint)SpellId.CurseRavenFury, beneficial: false);

            Assert.IsNull(attacker.CheckPKStatusVsTarget(target, spell), "CurseRavenFury must be the one spell FFA allows");
        }

        // ================= pvp_arena_restrict_spells off (binding snapshot) =================

        /// <summary>
        /// RestrictSpells baked into the binding as false (as if pvp_arena_restrict_spells was off at match
        /// creation): the same attribute debuff that OneVOne_CreatureEnchantment_AttributeDebuff_Refused
        /// refuses is now allowed, proving TryPvpArenaGate reads the binding's snapshot and not a live tunable.
        /// </summary>
        [TestMethod]
        public void OneVOne_RestrictSpellsOffOnBinding_AttributeDebuff_Allowed()
        {
            var match = NewMatch(ArenaMapCatalog.OneVOneKey);
            var attacker = SeededPlayer(PlayerKillerStatus.PK);
            var target = SeededPlayer(PlayerKillerStatus.PK);

            attacker.SetPvpBindingForTests(Bind(match, 0, restrictSpells: false));
            target.SetPvpBindingForTests(Bind(match, 1, restrictSpells: false));

            var spell = FakeSpell(MagicSchool.CreatureEnchantment, SpellCategory.StrengthLowering, metaSpellId: 1, beneficial: false);

            Assert.IsNull(attacker.CheckPKStatusVsTarget(target, spell), "RestrictSpells=false on the binding must disable the arena spell rules entirely");
        }
    }
}
