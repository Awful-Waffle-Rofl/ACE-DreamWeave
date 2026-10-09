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
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Phase B item 2 of Docs/Pvp/ATTACK-DEFEND.md "Who may damage it": Player.CheckPKStatusVsTarget consults the crystal gate
    /// FIRST. Pinned two ways: on source (the call binds the gate identifier and sits before the arena gate and every retail PK
    /// rule; the check is proven to discriminate by substitution), and driven through the real method on a seeded Player and a
    /// seeded, tagged Creature (the PvpPlayerHookTests seeding: uninitialized objects plus only the inherited WorldObject state
    /// these paths read; no Player static is touched).
    /// </summary>
    [TestClass]
    public class AttackDefendGateWiringTests
    {
        private const string Signature = "public override List<WeenieErrorWithString> CheckPKStatusVsTarget(WorldObject target, Spell spell)";

        private const string GateCall = "if (TryBattlegroundObjectiveGate(targetCreature, spell, out var objectiveResult))\n                return objectiveResult;";

        // ---------------- source ----------------

        /// <summary>Null when the crystal gate is called, after targetCreature resolves and before the arena gate and the first retail PK rule.</summary>
        private static string GateWiringFault(string body)
        {
            var resolved = body.IndexOf("if (targetCreature == null)\n                return null;", StringComparison.Ordinal);
            var gate = body.IndexOf(GateCall, StringComparison.Ordinal);
            var arena = body.IndexOf("TryPvpArenaGate(", StringComparison.Ordinal);
            var safeZone = body.IndexOf("TryPvpSafeZoneGate(", StringComparison.Ordinal);
            var retail = body.IndexOf("if (PlayerKillerStatus == PlayerKillerStatus.Free", StringComparison.Ordinal);

            if (gate < 0)
                return "the crystal gate is not called";

            if (resolved < 0 || resolved > gate)
                return "the crystal gate runs before targetCreature is resolved";

            if (arena < gate || safeZone < gate || retail < gate)
                return "the crystal gate does not run before the arena gate, the safe-zone gate and the retail rules";

            return null;
        }

        private static string Body() => PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Combat.cs"), Signature);

        [TestMethod]
        public void CheckPKStatusVsTarget_ConsultsTheCrystalGateBeforeEveryPkRule()
        {
            Assert.IsNull(GateWiringFault(Body()));
        }

        [TestMethod]
        public void TheWiringCheck_Discriminates_WhenTheGateIdentifierIsSubstituted()
        {
            var body = Body();

            Assert.IsNotNull(GateWiringFault(body.Replace("TryBattlegroundObjectiveGate(", "TryPvpSafeZoneGateX(")), "a different call must fail the check");

            // Moved below the retail rules: the same call, too late.
            var moved = body.Replace(GateCall, "") + "\n" + GateCall;
            Assert.IsNotNull(GateWiringFault(moved), "the gate after the retail rules must fail the check");
        }

        // ---------------- driven ----------------

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static T Seeded<T>(PlayerKillerStatus status) where T : Creature
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(wo, "positionCache", new Dictionary<PositionType, Position>());
            SetInherited(wo, "ephemeralPositions", new Dictionary<PositionType, Position>());

            wo.PlayerKillerStatus = status;
            wo.Location = new Position { LandblockId = new LandblockId(0x00900100), PositionX = 50f, PositionY = 50f, RotationW = 1f };

            return wo;
        }

        private static readonly DateTime Now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

        private static PvpMatch Match() => new PvpMatch(Guid.NewGuid(), BattlegroundModes.AttackDefendModeKey, new List<PvpTeam>
        {
            new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
            new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) }),
        }, Now);

        private static PvpPlayerBinding Bind(PvpMatch match, int team, PvpMatchState state = PvpMatchState.Live, bool respawning = false) =>
            new PvpPlayerBinding(match, team, state, true, true, true, true, true, respawning: respawning);

        private static (Player Attacker, Creature Crystal, PvpMatch Match) Rig(int attackerTeam, PvpMatchState state = PvpMatchState.Live, bool respawning = false)
        {
            var match = Match();
            var player = Seeded<Player>(PlayerKillerStatus.PKLite);
            player.SetPvpBindingForTests(Bind(match, attackerTeam, state, respawning));

            var crystal = Seeded<Creature>(PlayerKillerStatus.NPK);
            Assert.IsTrue(crystal.SetBattlegroundObjective(new BattlegroundObjectiveTag(match.MatchId, 0, CrystalWinCondition.DefenderTeam)));

            return (player, crystal, match);
        }

        /// <summary>A Spell with only the flags field set (new Spell(id) reads the portal dat, which tests never load).</summary>
        private static Spell FakeSpell(bool beneficial)
        {
            var spellBase = (ACE.DatLoader.Entity.SpellBase)RuntimeHelpers.GetUninitializedObject(typeof(ACE.DatLoader.Entity.SpellBase));
            var bitfield = typeof(ACE.DatLoader.Entity.SpellBase).GetField("<Bitfield>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(bitfield, "SpellBase.Bitfield's backing field was not found by reflection - has it been renamed?");
            bitfield.SetValue(spellBase, beneficial ? (uint)SpellFlags.Beneficial : 0u);

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;
            return spell;
        }

        /// <summary>The harmful mapping the gate is handed: a weapon attack (no spell) and a harmful spell are harmful; a beneficial spell is not.</summary>
        [TestMethod]
        public void IsHarmfulAction_WeaponAndHarmfulSpell_True_BeneficialSpell_False()
        {
            Assert.IsTrue(Player.IsHarmfulAction(null), "a weapon attack");
            Assert.IsTrue(Player.IsHarmfulAction(FakeSpell(beneficial: false)), "a harmful spell");
            Assert.IsFalse(Player.IsHarmfulAction(FakeSpell(beneficial: true)), "a beneficial spell");
        }

        /// <summary>Through the real gate: an attacker in the Live match may cast a harmful spell on the crystal, never a beneficial one.</summary>
        [TestMethod]
        public void AnAttacker_HarmfulSpellAllowed_BeneficialSpellRefused()
        {
            var (attacker, crystal, _) = Rig(CrystalWinCondition.AttackerTeam);

            Assert.IsNull(attacker.CheckPKStatusVsTarget(crystal, FakeSpell(beneficial: false)), "a harmful spell from an attacker");
            AssertRefused(attacker.CheckPKStatusVsTarget(crystal, FakeSpell(beneficial: true)), "a beneficial spell (a heal) is refused even from an attacker");
        }

        private static void AssertRefused(List<WeenieErrorWithString> result, string why)
        {
            Assert.IsNotNull(result, why);
            Assert.AreEqual(WeenieErrorWithString._IsAnInvalidTarget, result[0], why + " (the neutral line, not a PK-type line)");
            Assert.AreEqual(WeenieErrorWithString._FailsToAffectYou_NotSamePKType, result[1], why);
        }

        [TestMethod]
        public void AnAttackerInTheLiveMatch_MayHitTheCrystal()
        {
            var (attacker, crystal, _) = Rig(CrystalWinCondition.AttackerTeam);

            Assert.IsNull(attacker.CheckPKStatusVsTarget(crystal, null));
        }

        [TestMethod]
        public void ADefender_ARespawningAttacker_AndAnAttackerBeforeLive_AreRefused()
        {
            AssertRefused(Rig(CrystalWinCondition.AttackerTeam).Attacker.CheckPKStatusVsTarget(Rig(CrystalWinCondition.AttackerTeam).Crystal, null), "an attacker of another match");

            var own = Rig(CrystalWinCondition.DefenderTeam);
            AssertRefused(own.Attacker.CheckPKStatusVsTarget(own.Crystal, null), "the defender's own crystal");

            var pen = Rig(CrystalWinCondition.AttackerTeam, respawning: true);
            AssertRefused(pen.Attacker.CheckPKStatusVsTarget(pen.Crystal, null), "a respawning attacker");

            var countdown = Rig(CrystalWinCondition.AttackerTeam, PvpMatchState.Countdown);
            AssertRefused(countdown.Attacker.CheckPKStatusVsTarget(countdown.Crystal, null), "the countdown");
        }

        [TestMethod]
        public void AnUnboundPlayer_IsRefused_AndAnUntaggedCreatureKeepsTheRetailRules()
        {
            var (_, crystal, _) = Rig(CrystalWinCondition.AttackerTeam);
            var outsider = Seeded<Player>(PlayerKillerStatus.PK);

            AssertRefused(outsider.CheckPKStatusVsTarget(crystal, null), "a player outside the match");

            // Control: the same outsider against an untagged NPK creature takes the retail path, which allows it.
            Assert.IsNull(outsider.CheckPKStatusVsTarget(Seeded<Creature>(PlayerKillerStatus.NPK), null));
        }

        [TestMethod]
        public void TheTag_IsWriteOnce()
        {
            var crystal = Seeded<Creature>(PlayerKillerStatus.NPK);
            var first = new BattlegroundObjectiveTag(Guid.NewGuid(), 1, 1);

            Assert.IsTrue(crystal.SetBattlegroundObjective(first));
            Assert.IsFalse(crystal.SetBattlegroundObjective(new BattlegroundObjectiveTag(Guid.NewGuid(), 2, 1)));
            Assert.IsFalse(crystal.SetBattlegroundObjective(null));
            Assert.AreSame(first, crystal.BattlegroundObjective);
        }
    }
}
