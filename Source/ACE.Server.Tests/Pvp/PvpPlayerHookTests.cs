using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The player-side hooks of PR C1 driven on a real (seeded, uninitialized) Player: the combined predicates,
    /// the damage gate inside Player.CheckPKStatusVsTarget, the INERT path with no binding, and ExitPvpMatchNow.
    ///
    /// SEEDING. The Player is RuntimeHelpers.GetUninitializedObject plus only the INHERITED WorldObject state these
    /// paths read (Biota, BiotaDatabaseLock, the two position caches). Nothing here touches a static field Player
    /// declares, or sets a Player-declared field by reflection: either runs Player's type initializer, which reads
    /// the World database and would poison Player for the rest of the run (MuleSummonTests explains). The binding
    /// goes in through the internal SetPvpBindingForTests seam, the friendly-fire tunable through
    /// PvpArenaHookSettings.FriendlyFireSource, and the PK facet rule through FacetTunables.PkRulesSource.
    ///
    /// PROPERTYMANAGER. ExitPvpMatchNow's RushNextPlayerSave reads player_save_interval, which throws when
    /// uncached here; it is seeded to its registered default in TestInitialize and put back in TestCleanup. No
    /// other key is read by the paths under test (the gate reads friendly fire through the seam).
    /// </summary>
    [TestClass]
    public class PvpPlayerHookTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private Func<bool> savedFriendlyFire;
        private Func<bool> savedPkRules;
        private long savedSaveInterval;

        [TestInitialize]
        public void Setup()
        {
            savedFriendlyFire = PvpArenaHookSettings.FriendlyFireSource;
            savedPkRules = FacetTunables.PkRulesSource;

            Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey("player_save_interval"), "player_save_interval is not registered");
            savedSaveInterval = DefaultPropertyManager.DefaultLongProperties["player_save_interval"].Item;
            Assert.IsTrue(PropertyManager.ModifyLong("player_save_interval", savedSaveInterval));

            PvpArenaHookSettings.FriendlyFireSource = () => false;
            FacetTunables.PkRulesSource = () => false;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.FriendlyFireSource = savedFriendlyFire;
            FacetTunables.PkRulesSource = savedPkRules;
            PropertyManager.ModifyLong("player_save_interval", savedSaveInterval);
        }

        // ================= fixtures =================

        private static Player SeededPlayer(PlayerKillerStatus status)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(player, "positionCache", new Dictionary<PositionType, Position>());
            SetInherited(player, "ephemeralPositions", new Dictionary<PositionType, Position>());

            // ExitPvpMatchNow builds a GameMessagePublicUpdatePropertyInt, which reads Sequences (a skipped field
            // initializer); EnqueueBroadcast itself then no-ops, since there is no PhysicsObj
            SetInherited(player, "<Sequences>k__BackingField", new ACE.Server.Network.Sequence.SequenceManager());

            player.PlayerKillerStatus = status;

            // one shared cell, so the retail path's CheckHouseRestrictions passes on its first line
            player.Location = new Position { LandblockId = new LandblockId(0x00900100), PositionX = 50f, PositionY = 50f, RotationW = 1f };

            return player;
        }

        /// <summary>Reflection on WorldObject-declared fields only (never Player-declared ones - see the class remarks).</summary>
        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static PvpMatch NewMatch()
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500), new PvpParticipant(2, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), ArenaMapCatalog.TwoVTwoKey, teams, Now);
        }

        private static PvpPlayerBinding Bind(PvpMatch match, int team, PvpMatchState state = PvpMatchState.Live, bool masks = true) =>
            new PvpPlayerBinding(match, team, state, masks, masks, masks, masks, masks);

        private static void AssertNotSamePkTypeRefusal(List<WeenieErrorWithString> result, string why)
        {
            Assert.IsNotNull(result, why);
            Assert.AreEqual(2, result.Count, why);
            Assert.AreEqual(WeenieErrorWithString.YouFailToAffect_NotSamePKType, result[0], why);
            Assert.AreEqual(WeenieErrorWithString._FailsToAffectYou_NotSamePKType, result[1], why);
        }

        // ================= INERT: no binding, master's behaviour =================

        /// <summary>
        /// INERT. Reaches Player_PvpArena.cs's six Is* predicates and the four combined predicates on a Player with
        /// no binding and off the PK facet: every one is false, so every mask site behaves exactly as master.
        /// </summary>
        [TestMethod]
        public void Inert_NoBinding_EveryPredicateIsFalse()
        {
            var player = SeededPlayer(PlayerKillerStatus.PK);

            Assert.IsNull(player.PvpBinding);
            Assert.IsFalse(player.IsInPvpMatch);
            Assert.IsFalse(player.IsPvpClassAbilityMaskActive);
            Assert.IsFalse(player.IsPvpEquipmentModMaskActive);
            Assert.IsFalse(player.IsPvpWeaponModMaskActive);
            Assert.IsFalse(player.IsPvpPickupBoonMaskActive);
            Assert.IsFalse(player.IsPvpTurnSpeedMaskActive);

            Assert.IsFalse(player.ClassAbilitySuppressed);
            Assert.IsFalse(player.EquipmentModSuppressed);
            Assert.IsFalse(player.WeaponModSuppressed);
            Assert.IsFalse(player.PickupBoonSuppressed);
            Assert.IsFalse(player.TurnSpeedSuppressed);
        }

        /// <summary>
        /// INERT. Reaches Player.CheckPKStatusVsTarget with neither player bound: the gate returns NotApplicable and
        /// the RETAIL rules decide. Two PKs may fight (null), an NPK attacker gets retail's "you are not PK" pair -
        /// an error the arena gate never produces, so it proves the retail branch ran.
        /// </summary>
        [TestMethod]
        public void Inert_NoBinding_CheckPKStatusVsTarget_FollowsRetail()
        {
            var pkA = SeededPlayer(PlayerKillerStatus.PK);
            var pkB = SeededPlayer(PlayerKillerStatus.PK);

            Assert.IsNull(pkA.CheckPKStatusVsTarget(pkB, null), "retail: two PKs may fight");

            var npk = SeededPlayer(PlayerKillerStatus.NPK);
            var result = npk.CheckPKStatusVsTarget(pkB, null);

            Assert.IsNotNull(result, "retail: an NPK cannot attack a player");
            Assert.AreEqual(WeenieErrorWithString.YouFailToAffect_YouAreNotPK, result[0]);
        }

        // ================= the combined predicates on a live Player =================

        /// <summary>
        /// Reaches each combined predicate (ClassAbilitySuppressed, EquipmentModSuppressed, WeaponModSuppressed,
        /// PickupBoonSuppressed, TurnSpeedSuppressed) through the arena term alone: bound, masks on, off the PK
        /// facet. Every one turns true, so deleting the arena term from any of them fails here.
        /// </summary>
        [TestMethod]
        public void Bound_ArenaMaskAlone_TurnsEveryCombinedPredicateOn()
        {
            var player = SeededPlayer(PlayerKillerStatus.PKLite);
            player.SetPvpBindingForTests(Bind(NewMatch(), 0));

            Assert.IsFalse(player.IsPkFacetRuleActive, "fixture: the facet term must be off, or this proves nothing about the arena term");

            Assert.IsTrue(player.IsInPvpMatch);
            Assert.IsTrue(player.ClassAbilitySuppressed);
            Assert.IsTrue(player.EquipmentModSuppressed);
            Assert.IsTrue(player.WeaponModSuppressed);
            Assert.IsTrue(player.PickupBoonSuppressed);
            Assert.IsTrue(player.TurnSpeedSuppressed);
        }

        /// <summary>Bound but with every mask flag off (the tunables off): in a match, yet nothing suppressed.</summary>
        [TestMethod]
        public void Bound_MasksOff_SuppressesNothing()
        {
            var player = SeededPlayer(PlayerKillerStatus.PKLite);
            player.SetPvpBindingForTests(Bind(NewMatch(), 0, masks: false));

            Assert.IsTrue(player.IsInPvpMatch);
            Assert.IsFalse(player.ClassAbilitySuppressed);
            Assert.IsFalse(player.EquipmentModSuppressed);
            Assert.IsFalse(player.WeaponModSuppressed);
            Assert.IsFalse(player.PickupBoonSuppressed);
            Assert.IsFalse(player.TurnSpeedSuppressed);
        }

        /// <summary>
        /// The facet term alone, no binding: on the PK facet with the facet PK rule on, every combined predicate is
        /// true - the PK facet behaves exactly as it did when each site read IsPkFacetRuleActive itself.
        /// </summary>
        [TestMethod]
        public void PkFacetRuleAlone_TurnsEveryCombinedPredicateOn()
        {
            var player = SeededPlayer(PlayerKillerStatus.PK);
            player.SetProperty(PropertyInt.ActiveFacetSlot, Player.PkFacetSlot);
            FacetTunables.PkRulesSource = () => true;

            Assert.IsTrue(player.IsPkFacetRuleActive, "fixture: the facet rule must be on");
            Assert.IsFalse(player.IsInPvpMatch);

            Assert.IsTrue(player.ClassAbilitySuppressed);
            Assert.IsTrue(player.EquipmentModSuppressed);
            Assert.IsTrue(player.WeaponModSuppressed);
            Assert.IsTrue(player.PickupBoonSuppressed);
            Assert.IsTrue(player.TurnSpeedSuppressed);
        }

        // ================= the damage gate (H6) =================

        /// <summary>
        /// THE DISCRIMINATING GATE TEST. Reaches Player.CheckPKStatusVsTarget's arena gate (TryPvpArenaGate) with two
        /// PK players: retail alone would let them fight (see the INERT test above). Bound to the SAME team of a Live
        /// match with friendly fire off, the gate refuses - so removing the gate from CheckPKStatusVsTarget fails here.
        /// </summary>
        [TestMethod]
        public void Gate_Teammates_FriendlyFireOff_Refused_WhereRetailWouldAllow()
        {
            var match = NewMatch();
            var attacker = SeededPlayer(PlayerKillerStatus.PK);
            var teammate = SeededPlayer(PlayerKillerStatus.PK);

            attacker.SetPvpBindingForTests(Bind(match, 0));
            teammate.SetPvpBindingForTests(Bind(match, 0));

            AssertNotSamePkTypeRefusal(attacker.CheckPKStatusVsTarget(teammate, null), "teammate harm with friendly fire off must be refused");
        }

        /// <summary>Control: the same teammates with pvp_arena_friendly_fire on are allowed.</summary>
        [TestMethod]
        public void Gate_Teammates_FriendlyFireOn_Allowed()
        {
            PvpArenaHookSettings.FriendlyFireSource = () => true;

            var match = NewMatch();
            var attacker = SeededPlayer(PlayerKillerStatus.PKLite);
            var teammate = SeededPlayer(PlayerKillerStatus.PKLite);

            attacker.SetPvpBindingForTests(Bind(match, 0));
            teammate.SetPvpBindingForTests(Bind(match, 0));

            Assert.IsNull(attacker.CheckPKStatusVsTarget(teammate, null));
        }

        /// <summary>
        /// Reaches the gate with a bound attacker and an UNBOUND PK target: refused, although retail alone would allow
        /// two PKs. Nobody in a match can harm anyone outside it.
        /// </summary>
        [TestMethod]
        public void Gate_BoundVsUnbound_Refused_WhereRetailWouldAllow()
        {
            var attacker = SeededPlayer(PlayerKillerStatus.PK);
            var outsider = SeededPlayer(PlayerKillerStatus.PK);

            attacker.SetPvpBindingForTests(Bind(NewMatch(), 0));

            AssertNotSamePkTypeRefusal(attacker.CheckPKStatusVsTarget(outsider, null), "bound attacker vs unbound target");
            AssertNotSamePkTypeRefusal(outsider.CheckPKStatusVsTarget(attacker, null), "unbound attacker vs bound target");
        }

        /// <summary>Reaches the gate before the match is Live (Countdown): opponents are refused.</summary>
        [TestMethod]
        public void Gate_Opponents_BeforeLive_Refused()
        {
            var match = NewMatch();
            var a = SeededPlayer(PlayerKillerStatus.PKLite);
            var b = SeededPlayer(PlayerKillerStatus.PKLite);

            a.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));
            b.SetPvpBindingForTests(Bind(match, 1, PvpMatchState.Countdown));

            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, null), "opponents during the countdown");
        }

        /// <summary>
        /// Reaches the gate with opponents in a Live match whose stored statuses retail would REFUSE (PK vs NPK): the
        /// gate allows, proving it decides instead of the retail rules rather than merely before them.
        /// </summary>
        [TestMethod]
        public void Gate_Opponents_Live_Allowed_EvenWhereRetailWouldRefuse()
        {
            var match = NewMatch();
            var a = SeededPlayer(PlayerKillerStatus.PK);
            var b = SeededPlayer(PlayerKillerStatus.NPK);

            a.SetPvpBindingForTests(Bind(match, 0));
            b.SetPvpBindingForTests(Bind(match, 1));

            Assert.IsNull(a.CheckPKStatusVsTarget(b, null));
        }

        /// <summary>Two different matches: refused.</summary>
        [TestMethod]
        public void Gate_DifferentMatches_Refused()
        {
            var a = SeededPlayer(PlayerKillerStatus.PKLite);
            var b = SeededPlayer(PlayerKillerStatus.PKLite);

            a.SetPvpBindingForTests(Bind(NewMatch(), 0));
            b.SetPvpBindingForTests(Bind(NewMatch(), 1));

            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, null), "players in two different matches");
        }

        // ================= ExitPvpMatchNow =================

        /// <summary>
        /// Reaches Player.ExitPvpMatchNow on a bound player carrying the 9075 marker: the binding clears, the
        /// pre-match status comes back from the marker, the marker is removed, and LastPkAttackTimestamp returns to
        /// its entry value (0 here). A second call finds nothing to do (idempotent).
        /// </summary>
        [TestMethod]
        public void Exit_RestoresStatusFromMarker_ClearsBindingAndMarker_AndIsIdempotent()
        {
            var player = SeededPlayer(PlayerKillerStatus.PKLite);
            player.SetPvpBindingForTests(Bind(NewMatch(), 0));
            player.PvpMatchReturnPkStatus = (int)PlayerKillerStatus.NPK;
            player.LastPkAttackTimestamp = 1_000_000;

            Assert.IsTrue(player.ExitPvpMatchNow("test"), "first exit must do the work");

            Assert.IsNull(player.PvpBinding);
            Assert.IsFalse(player.IsInPvpMatch);
            Assert.AreEqual(PlayerKillerStatus.NPK, player.PlayerKillerStatus);
            Assert.IsNull(player.PvpMatchReturnPkStatus);
            Assert.AreEqual(0.0, player.LastPkAttackTimestamp, "the in-match PK timer must not follow the player out");

            Assert.IsFalse(player.ExitPvpMatchNow("test again"), "a second exit must be a no-op");
            Assert.AreEqual(PlayerKillerStatus.NPK, player.PlayerKillerStatus);
        }

        /// <summary>INERT: ExitPvpMatchNow on a player who was never in a match changes nothing.</summary>
        [TestMethod]
        public void Exit_WhenNeverEntered_IsANoOp()
        {
            var player = SeededPlayer(PlayerKillerStatus.PK);
            player.LastPkAttackTimestamp = 1_000_000;

            Assert.IsFalse(player.ExitPvpMatchNow("never entered"));
            Assert.AreEqual(PlayerKillerStatus.PK, player.PlayerKillerStatus);
            Assert.AreEqual(1_000_000.0, player.LastPkAttackTimestamp);
        }

        // ================= facet switch (CheckFacetGates, TrySwitchFacet's own first step) =================

        /// <summary>
        /// Dials under which an unbound NPK player at level 0 passes every gate for slot 2: enabled, no level
        /// threshold, no allowlist. Swapped in through FacetTunables.DialSource and restored by the caller.
        /// </summary>
        private static FacetDials OpenDials() => new FacetDials(true, 0, 0, 0, ACE.Server.Realms.LandblockRealmList.Empty(), "anywhere", true, 150);

        [TestMethod]
        public void FacetSwitch_Bound_IsRefusedWithTheArenaLine()
        {
            var savedDials = FacetTunables.DialSource;

            try
            {
                FacetTunables.DialSource = OpenDials;

                var player = SeededPlayer(PlayerKillerStatus.NPK);
                player.SetPvpBindingForTests(Bind(NewMatch(), 0));

                Assert.IsFalse(player.CheckFacetGates(2, out var refusal));
                Assert.AreEqual(PvpArenaText.FacetSwitchRefused, refusal);
            }
            finally
            {
                FacetTunables.DialSource = savedDials;
            }
        }

        /// <summary>
        /// CONTROL for the test above: the same player and dials, unbound, pass every gate. So the refusal above is
        /// the binding's doing, not a gate the fixture happened to fail.
        /// </summary>
        [TestMethod]
        public void FacetSwitch_Unbound_IsAllowed()
        {
            var savedDials = FacetTunables.DialSource;

            try
            {
                FacetTunables.DialSource = OpenDials;

                var player = SeededPlayer(PlayerKillerStatus.NPK);

                Assert.IsTrue(player.CheckFacetGates(2, out var refusal), $"an unbound player was refused: {refusal}");
                Assert.IsNull(refusal);
            }
            finally
            {
                FacetTunables.DialSource = savedDials;
            }
        }

        // ================= Countdown vulnerabilities: gate wiring, side effects (2026-10-06 batch) =================

        /// <summary>A Spell carrying only the dat fields the arena gate reads (school, category, harmful flag); new Spell(id) needs the portal dat.</summary>
        private static ACE.Server.Entity.Spell FakeSpell(MagicSchool school, SpellCategory category, bool harmful = true)
        {
            var spellBase = (ACE.DatLoader.Entity.SpellBase)RuntimeHelpers.GetUninitializedObject(typeof(ACE.DatLoader.Entity.SpellBase));

            void Set(string name, object value)
            {
                var field = typeof(ACE.DatLoader.Entity.SpellBase).GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, $"SpellBase.{name}'s backing field was not found by reflection - has it been renamed?");
                field.SetValue(spellBase, value);
            }

            Set("School", school);
            Set("Category", category);
            Set("Bitfield", harmful ? 0u : (uint)SpellFlags.Beneficial);

            var spell = (ACE.Server.Entity.Spell)RuntimeHelpers.GetUninitializedObject(typeof(ACE.Server.Entity.Spell));
            spell._spellBase = spellBase;
            return spell;
        }

        /// <summary>
        /// WIRING (item 2): through the real Player.CheckPKStatusVsTarget, two PK opponents in Countdown. A real
        /// defense-lowering vuln is allowed (null); a war spell, another debuff and a non-harmful-by-category spell are refused.
        /// Removing the vuln flag from the TryPvpArenaGate call fails the first assertion.
        /// </summary>
        [TestMethod]
        public void Gate_Countdown_RealSpells_VulnAllowed_WarSpellRefused()
        {
            var match = NewMatch();
            var a = SeededPlayer(PlayerKillerStatus.PK);
            var b = SeededPlayer(PlayerKillerStatus.PK);

            a.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));
            b.SetPvpBindingForTests(Bind(match, 1, PvpMatchState.Countdown));

            foreach (var category in new[] { SpellCategory.MeleeDefenseLowering, SpellCategory.MissileDefenseLowering, SpellCategory.MagicDefenseLowering })
                Assert.IsNull(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.CreatureEnchantment, category)), $"{category} in Countdown must be allowed");

            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.WarMagic, SpellCategory.FireStreak)), "a war spell in Countdown");
            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.CreatureEnchantment, SpellCategory.StrengthLowering)), "a non-defense debuff in Countdown");
            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, null), "a weapon attack in Countdown");
        }

        // ================= Countdown vulnerabilities widened: Life elemental vulns and Imperil (2026-10-08 owner ruling) =================

        /// <summary>The seven Life Magic elemental vulnerability categories plus Armor Lowering (Imperil), as the client dat files them.</summary>
        private static readonly SpellCategory[] LifeCountdownVulnCategories =
        {
            SpellCategory.AcidVulnerability, SpellCategory.BludgeonVulnerability, SpellCategory.ColdVulnerability, SpellCategory.ElectricVulnerability,
            SpellCategory.FireVulnerability, SpellCategory.PierceVulnerability, SpellCategory.SlashVulnerability, SpellCategory.ArmorLowering
        };

        /// <summary>
        /// WIRING (playtest 2026-10-08, "You fail to affect X because you are not the same sort of player killer"). Call site reached:
        /// Player.CheckPKStatusVsTarget (Player_Combat.cs) -> Player.TryPvpArenaGate (Player_PvpArena.cs), whose vuln flag comes from
        /// PvpArenaSpellRules.IsCountdownVuln and is handed to PvpArenaGate.Evaluate; the same call DoCastSpell_Inner makes before a
        /// cast lands. Two PK enemies in a 2v2 Countdown: Fire Vulnerability Other (Life, FireVulnerability), Blade Vulnerability
        /// Other (Life, SlashVulnerability), Imperil Other (Life, ArmorLowering) and the other Life elemental vulns are allowed
        /// (null). Before the fix each returned the NotSamePKType pair from TryPvpArenaGate's refusal tail.
        /// </summary>
        [TestMethod]
        public void Gate_Countdown_LifeElementalVulnsAndImperil_OnMatchEnemy_Allowed()
        {
            var match = NewMatch();
            var a = SeededPlayer(PlayerKillerStatus.PK);
            var b = SeededPlayer(PlayerKillerStatus.PK);

            a.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));
            b.SetPvpBindingForTests(Bind(match, 1, PvpMatchState.Countdown));

            Assert.IsNull(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.LifeMagic, SpellCategory.FireVulnerability)), "Fire Vulnerability Other in Countdown must be allowed");
            Assert.IsNull(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.LifeMagic, SpellCategory.SlashVulnerability)), "Blade Vulnerability Other in Countdown must be allowed");
            Assert.IsNull(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.LifeMagic, SpellCategory.ArmorLowering)), "Imperil Other in Countdown must be allowed");

            foreach (var category in LifeCountdownVulnCategories)
                Assert.IsNull(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.LifeMagic, category)), $"Life {category} in Countdown must be allowed");

            // one Countdown and one Live (the bell rang for one side first) is allowed too, as for the defense-lowering vulns
            b.SetPvpBindingForTests(Bind(match, 1, PvpMatchState.Live));
            Assert.IsNull(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.LifeMagic, SpellCategory.FireVulnerability)), "Countdown caster onto a Live enemy");
        }

        /// <summary>
        /// The widening is scoped to the vulns: through the real gate, a war damage spell, a Life damage/drain spell (HealthLowering),
        /// a Life spell that only shares a category name with a Creature vuln, melee/missile, a teammate and a battleground
        /// Countdown all stay refused with the NotSamePKType pair.
        /// </summary>
        [TestMethod]
        public void Gate_Countdown_DamageAndOtherHarm_StillRefused_AfterTheWidening()
        {
            var match = NewMatch();
            var a = SeededPlayer(PlayerKillerStatus.PK);
            var b = SeededPlayer(PlayerKillerStatus.PK);
            var mate = SeededPlayer(PlayerKillerStatus.PK);

            a.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));
            b.SetPvpBindingForTests(Bind(match, 1, PvpMatchState.Countdown));
            mate.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));

            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.WarMagic, SpellCategory.FireStreak)), "a war bolt in Countdown");
            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.WarMagic, SpellCategory.AcidVulnerability)), "a WAR spell filed under a vuln category (Acid Spit Vulnerability) in Countdown");
            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.LifeMagic, SpellCategory.HealthLowering)), "a Life harm spell in Countdown");
            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.CreatureEnchantment, SpellCategory.FireVulnerability)), "the category alone is not enough, the school must be Life Magic");
            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, null), "a weapon attack in Countdown");

            foreach (var category in LifeCountdownVulnCategories)
                AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(mate, FakeSpell(MagicSchool.LifeMagic, category)), $"Life {category} on a teammate in Countdown");

            var bgTeams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) })
            };
            var bg = new PvpMatch(Guid.NewGuid(), ACE.Server.Pvp.Battlegrounds.BattlegroundModes.KothModeKey, bgTeams, Now);

            a.SetPvpBindingForTests(Bind(bg, 0, PvpMatchState.Countdown));
            b.SetPvpBindingForTests(Bind(bg, 1, PvpMatchState.Countdown));
            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.LifeMagic, SpellCategory.FireVulnerability)), "battleground Countdown Life vuln");
        }

        /// <summary>A Countdown vuln onto a player who is NOT in the caster's match (unbound, or bound to another match) stays refused, both directions.</summary>
        [TestMethod]
        public void Gate_Countdown_Vuln_OnNonMatchPlayer_StillRefused()
        {
            var match = NewMatch();
            var a = SeededPlayer(PlayerKillerStatus.PK);
            var outsider = SeededPlayer(PlayerKillerStatus.PK);
            var otherMatchPlayer = SeededPlayer(PlayerKillerStatus.PK);

            a.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));
            otherMatchPlayer.SetPvpBindingForTests(Bind(NewMatch(), 1, PvpMatchState.Countdown));

            foreach (var spell in new[] { FakeSpell(MagicSchool.LifeMagic, SpellCategory.FireVulnerability), FakeSpell(MagicSchool.LifeMagic, SpellCategory.ArmorLowering), FakeSpell(MagicSchool.CreatureEnchantment, SpellCategory.MeleeDefenseLowering) })
            {
                AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(outsider, spell), $"{spell.School}/{spell.Category} onto an unbound player");
                AssertNotSamePkTypeRefusal(outsider.CheckPKStatusVsTarget(a, spell), $"{spell.School}/{spell.Category} from an unbound player");
                AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(otherMatchPlayer, spell), $"{spell.School}/{spell.Category} onto a player in another match");
            }
        }

        /// <summary>
        /// Tugak Brawl (mode key ffa) is unchanged: the gate now lets a Life vuln or Imperil through in Countdown, but the arena spell rule
        /// (PvpArenaSpellRules.IsRefused, FFA branch) refuses it with the CannotAffectAnyone pair, exactly as it does when Live. A 2v2
        /// Live Life vuln stays allowed (IsRefused's Creature branch is untouched by the widening).
        /// </summary>
        [TestMethod]
        public void Gate_TugakBrawl_LifeVuln_RefusedByTheSpellRule_InCountdownAndLive_TwoVTwoLiveUnchanged()
        {
            var ffaTeams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) }),
                new PvpTeam(2, new List<PvpParticipant> { new PvpParticipant(3, 1500) })
            };
            var ffa = new PvpMatch(Guid.NewGuid(), ArenaMapCatalog.FfaKey, ffaTeams, Now);
            var a = SeededPlayer(PlayerKillerStatus.PK);
            var b = SeededPlayer(PlayerKillerStatus.PK);

            foreach (var state in new[] { PvpMatchState.Countdown, PvpMatchState.Live })
            {
                a.SetPvpBindingForTests(Bind(ffa, 0, state));
                b.SetPvpBindingForTests(Bind(ffa, 1, state));

                foreach (var category in new[] { SpellCategory.FireVulnerability, SpellCategory.ArmorLowering })
                {
                    var result = a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.LifeMagic, category));
                    Assert.IsNotNull(result, $"Tugak Brawl {state} Life {category}");
                    Assert.AreEqual(WeenieErrorWithString.YouFailToAffect_YouCannotAffectAnyone, result[0], $"Tugak Brawl {state} Life {category}: the FFA spell rule, not the gate");
                }
            }

            var match = NewMatch();
            a.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Live));
            b.SetPvpBindingForTests(Bind(match, 1, PvpMatchState.Live));

            foreach (var category in LifeCountdownVulnCategories)
                Assert.IsNull(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.LifeMagic, category)), $"2v2 Live Life {category} stays allowed");

            AssertCannotAffectAnyone(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.CreatureEnchantment, SpellCategory.StrengthLowering)), "2v2 Live non-defense Creature debuff stays refused by the spell rule");
        }

        private static void AssertCannotAffectAnyone(List<WeenieErrorWithString> result, string why)
        {
            Assert.IsNotNull(result, why);
            Assert.AreEqual(WeenieErrorWithString.YouFailToAffect_YouCannotAffectAnyone, result[0], why);
        }

        /// <summary>Owner ruling: a battleground Countdown refuses even a defense-lowering vuln, through the real gate.</summary>
        [TestMethod]
        public void Gate_BattlegroundCountdown_RealVuln_StaysRefused()
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500) })
            };
            var match = new PvpMatch(Guid.NewGuid(), ACE.Server.Pvp.Battlegrounds.BattlegroundModes.KothModeKey, teams, Now);

            var a = SeededPlayer(PlayerKillerStatus.PK);
            var b = SeededPlayer(PlayerKillerStatus.PK);

            a.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));
            b.SetPvpBindingForTests(Bind(match, 1, PvpMatchState.Countdown));

            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(b, FakeSpell(MagicSchool.CreatureEnchantment, SpellCategory.MeleeDefenseLowering)), "battleground Countdown vuln");
        }

        /// <summary>A vuln on a teammate in Countdown is refused through the real gate.</summary>
        [TestMethod]
        public void Gate_Countdown_RealVuln_OnTeammate_Refused()
        {
            var match = NewMatch();
            var a = SeededPlayer(PlayerKillerStatus.PK);
            var mate = SeededPlayer(PlayerKillerStatus.PK);

            a.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));
            mate.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));

            AssertNotSamePkTypeRefusal(a.CheckPKStatusVsTarget(mate, FakeSpell(MagicSchool.CreatureEnchantment, SpellCategory.MeleeDefenseLowering)), "teammate vuln in Countdown");
        }

        private static string RepoSource(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "property-registry.tsv")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "could not find the repo root by walking up from the test directory");

            return File.ReadAllText(Path.Combine(new[] { dir.FullName, "Source", "ACE.Server" }.Concat(relative).ToArray()));
        }

        /// <summary>Source-scan pins for the wiring no unit host can drive end to end (a real damage computation needs the client dat).</summary>
        [TestMethod]
        public void CallSites_PassTheVulnFlag_AndApplyTheFfaMod_BesideThe1v1Mod()
        {
            var arena = RepoSource("WorldObjects", "Player_PvpArena.cs");
            StringAssert.Contains(arena, "PvpArenaGate.Evaluate(mine, theirs, PvpArenaHookSettings.FriendlyFireSource(), isCountdownVuln, PvpArenaHookSettings.UtcNow(), targetPlayer.Location);");
            StringAssert.Contains(arena, "PvpArenaSpellRules.IsCountdownVuln(spell.School, spell.Category)");

            var combat = RepoSource("WorldObjects", "Player_Combat.cs");
            StringAssert.Contains(combat, "PvpArenaOneVOneRules.ApplyDmgMod1v1(this, target, damageEvent.Damage);");
            StringAssert.Contains(combat, "PvpArenaOneVOneRules.ApplyDmgModFfa(this, target, damageEvent.Damage);");
            StringAssert.Contains(combat, "PvpArenaOneVOneRules.ApplyDmgModBg(this, target, damageEvent.Damage);");

            var projectile = RepoSource("WorldObjects", "SpellProjectile.cs");
            StringAssert.Contains(projectile, "PvpArenaOneVOneRules.ApplyDmgMod1v1(source, target, finalDamage);");
            StringAssert.Contains(projectile, "PvpArenaOneVOneRules.ApplyDmgModFfa(source, target, finalDamage);");
            StringAssert.Contains(projectile, "PvpArenaOneVOneRules.ApplyDmgModBg(source, target, finalDamage);");

            StringAssert.Contains(combat, "!Pvp.PvpPlayerRules.RareStripDeferred(pvpBinding, opponent?.pvpBinding) && Pvp.Rules.PvpRules.ShouldStripRareBuffs()");
            StringAssert.Contains(combat, "attacker.UpdatePKTimer(defender);");
            StringAssert.Contains(combat, "defender.UpdatePKTimer(attacker);");
        }

        /// <summary>
        /// pvp_arena_ffa_ring_dmg is applied in SpellProjectile.CalculateDamage AFTER the war/void-vs-life branch split (so a Life ring
        /// like Curse of Raven Fury is covered), is fed the projectile's own ring shape, and sits before the debug readout (the C2 cap
        /// is in DamageTarget, later still). Moving it back inside the war/void else-branch would silently drop the Life ring.
        /// </summary>
        [TestMethod]
        public void CallSite_FfaRingDmg_SitsAfterTheBranchSplit_InCalculateDamage()
        {
            var projectile = RepoSource("WorldObjects", "SpellProjectile.cs").Replace("\r\n", "\n");

            const string call = "PvpArenaOneVOneRules.ApplyFfaRingDmg(source, target, SpellType == ProjectileSpellType.Ring, finalDamage);";
            StringAssert.Contains(projectile, call);

            var callAt = projectile.IndexOf(call, StringComparison.Ordinal);
            var bgAt = projectile.IndexOf("PvpArenaOneVOneRules.ApplyDmgModBg(source, target, finalDamage);", StringComparison.Ordinal);
            var lifeBranchAt = projectile.IndexOf("Spell.MetaSpellType == ACE.Entity.Enum.SpellType.LifeProjectile", StringComparison.Ordinal);
            var debugAt = projectile.IndexOf("// show debug info", callAt, StringComparison.Ordinal);

            Assert.IsTrue(bgAt >= 0 && lifeBranchAt >= 0 && debugAt > callAt, "anchors must exist");
            Assert.IsTrue(callAt > bgAt, "the ring call must come after the war/void branch (and its AM1 calls), outside it");

            var returnAt = projectile.IndexOf("return finalDamage;", callAt, StringComparison.Ordinal);
            Assert.IsTrue(returnAt > callAt, "CalculateDamage must still return finalDamage after the ring call");
            Assert.IsFalse(projectile.Substring(callAt, returnAt - callAt).Contains("return "), "no early return may sit between the ring call and the final return");

            // Outside the else: the closing brace of the war/void branch sits between the BG call and the ring call.
            var between = projectile.Substring(bgAt, callAt - bgAt);
            StringAssert.Contains(between, "\n            }\n", "the war/void else-branch must close before the ring call");
        }

        /// <summary>
        /// F2: a hit between two Countdown players (a pre-cast vuln) still stamps both PK timestamps and so applies the
        /// dispel-vuln lock, but does not reach the rare-buff strip (RB1 is never even consulted). Live is unchanged: RB1 is consulted.
        /// </summary>
        [TestMethod]
        public void CountdownVuln_KeepsTimestampAndDispelLock_ButDefersTheRareStrip_LiveUnchanged()
        {
            var savedDials = ACE.Server.Pvp.Rules.PvpRuleTunables.DialSource;
            ACE.Server.Pvp.Rules.PvpRuleTunables.DialSource = () => ACE.Server.Pvp.Rules.PvpRuleTunables.Defaults;

            try
            {
                var match = NewMatch();
                var a = SeededPlayer(PlayerKillerStatus.PK);
                var b = SeededPlayer(PlayerKillerStatus.PK);

                a.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));
                b.SetPvpBindingForTests(Bind(match, 1, PvpMatchState.Countdown));

                var rb1Before = ACE.Server.Pvp.Rules.PvpRules.GetAppliedCount(ACE.Server.Pvp.Rules.PvpChokePoint.RB1);

                Player.UpdatePKTimers(a, b);

                Assert.IsTrue(a.LastPkAttackTimestamp > 0 && b.LastPkAttackTimestamp > 0, "both timestamps are set in Countdown");
                Assert.AreEqual(rb1Before, ACE.Server.Pvp.Rules.PvpRules.GetAppliedCount(ACE.Server.Pvp.Rules.PvpChokePoint.RB1), "the rare strip is deferred in Countdown");

                // the dispel-vuln lock applies: a vuln cast by another player is excluded from the dispel candidates
                var vuln = new ACE.Entity.Models.PropertiesEnchantmentRegistry { SpellCategory = SpellCategory.MeleeDefenseLowering, CasterObjectId = 0x50000099u };
                var candidates = new List<ACE.Entity.Models.PropertiesEnchantmentRegistry> { vuln };

                var kept = ACE.Server.Pvp.Rules.PvpRules.ApplyDispelVulnLock(b, candidates, ACE.Server.Pvp.Rules.PvpChokePoint.D1);
                Assert.AreEqual(0, kept.Count, "the pre-cast vuln cannot be instantly dispelled");

                // The Live legs cannot run UpdatePKTimers end to end here (the strip's rare-spell lookup reads the world
                // database), so the Live / mixed / unbound outcomes are pinned on the predicate the call site consults,
                // and the call site itself by CallSites_PassTheVulnFlag_AndApplyTheFfaMod_BesideThe1v1Mod.
                var liveA = Bind(match, 0, PvpMatchState.Live);
                var liveB = Bind(match, 1, PvpMatchState.Live);
                var cdB = Bind(match, 1, PvpMatchState.Countdown);

                Assert.IsTrue(PvpPlayerRules.RareStripDeferred(liveA, cdB), "either side in Countdown defers the strip");
                Assert.IsTrue(PvpPlayerRules.RareStripDeferred(cdB, liveA), "either side in Countdown defers the strip (reversed)");
                Assert.IsFalse(PvpPlayerRules.RareStripDeferred(liveA, liveB), "both Live: the strip runs, as before");
                Assert.IsFalse(PvpPlayerRules.RareStripDeferred(null, null), "open-world PK: the strip runs, as before");
                Assert.IsFalse(PvpPlayerRules.RareStripDeferred(null, liveB), "an unbound self against a Live opponent: the strip runs");
                Assert.IsFalse(PvpPlayerRules.RareStripDeferred(Bind(match, 0, PvpMatchState.Staging), liveB), "Staging is not Countdown");            }
            finally
            {
                ACE.Server.Pvp.Rules.PvpRuleTunables.DialSource = savedDials;
            }
        }    }
}
