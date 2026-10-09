using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using DatSpellBase = ACE.DatLoader.Entity.SpellBase;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Goes all the way THROUGH Player.CheckPKStatusVsTarget for the Marketplace combat-free zone
    /// (pvp_safe_landblocks, Docs/Runbook.md), the same way PvpArenaSpellRulesIntegrationTests exercises the
    /// arena spell rules through the real gate rather than calling PvpSafeZoneRules.IsPvpSafe directly - so a
    /// wiring mistake between CheckPKStatusVsTarget and the rule (wrong landblock resolved, wrong error pair,
    /// placed after the retail rules instead of before) fails here even though the pure rule's own unit tests
    /// would not catch it.
    ///
    /// No arena binding is ever set here, so TryPvpArenaGate always returns NotApplicable and every case below
    /// reaches the safe-zone gate. PvpSafeZoneTunables.DialSource is pinned to its shipped Defaults (01F5@1, the
    /// Marketplace in realm 1's copy of Aerfalle Keep) for every test and restored afterwards, rather than
    /// relying on an unseeded PropertyManager read throwing - another test class earlier in the same process
    /// could otherwise have cached a different pvp_safe_landblocks value. Realm 1 at landblock 0x01F5 is
    /// therefore "the safe zone" here, and realm 0 at the same landblock (the retail dungeon) is not.
    ///
    /// SEEDING follows PvpPlayerHookTests / PvpArenaSpellRulesIntegrationTests exactly: a Player is
    /// RuntimeHelpers.GetUninitializedObject plus only the inherited WorldObject state the paths under test
    /// read, and no Player-declared field or static is touched (that runs Player's type initializer, which
    /// reads the World database and poisons Player for the rest of the run - see MuleSummonTests).
    /// </summary>
    [TestClass]
    public class PvpSafeZoneIntegrationTests
    {
        private const uint MarketplaceLandblockId = 0x01F50100;
        private const ushort MarketplaceRealm = 1;
        private const uint ElsewhereLandblockId = 0x00900100;

        private Func<PvpSafeZoneDials> savedDialSource;

        [TestInitialize]
        public void PinShippedDefaults()
        {
            savedDialSource = PvpSafeZoneTunables.DialSource;
            PvpSafeZoneTunables.DialSource = () => PvpSafeZoneTunables.Defaults;
        }

        [TestCleanup]
        public void RestoreDialSource()
        {
            PvpSafeZoneTunables.DialSource = savedDialSource;
        }

        // ================= fixtures =================

        /// <summary>
        /// A Player at <paramref name="landblockId"/>. Players in the Marketplace landblock default to the
        /// Marketplace's realm; everyone else to realm 0. <paramref name="realm"/> overrides either.
        /// </summary>
        private static Player SeededPlayer(PlayerKillerStatus status, uint landblockId, bool isEphemeralRealm = false, ushort? realm = null)
        {
            var realmId = realm ?? (landblockId == MarketplaceLandblockId ? MarketplaceRealm : (ushort)0);

            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(player, "positionCache", new Dictionary<PositionType, Position>());
            SetInherited(player, "ephemeralPositions", new Dictionary<PositionType, Position>());

            player.PlayerKillerStatus = status;
            player.Location = new Position
            {
                LandblockId = new LandblockId(landblockId),
                PositionX = 50f,
                PositionY = 50f,
                RotationW = 1f,
                Instance = Position.InstanceIDFromVars(realmId, isEphemeralRealm ? (ushort)1 : (ushort)0, isEphemeralRealm)
            };

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        /// <summary>
        /// A faked beneficial Spell: never touches DatManager (see PvpArenaSpellRulesIntegrationTests' class
        /// remarks for why). Only the Beneficial bit is set - the only field Spell.IsHarmful reads.
        /// </summary>
        private static Spell FakeBeneficialSpell()
        {
            var spellBase = (DatSpellBase)RuntimeHelpers.GetUninitializedObject(typeof(DatSpellBase));
            SetBackingField(spellBase, "Bitfield", (uint)SpellFlags.Beneficial);

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

        // ================= shooting in from outside =================

        [TestMethod]
        public void AttackerOutside_TargetInMarketplace_MeleeRefused()
        {
            var attacker = SeededPlayer(PlayerKillerStatus.PK, ElsewhereLandblockId);
            var target = SeededPlayer(PlayerKillerStatus.PK, MarketplaceLandblockId);

            AssertCannotAffectAnyoneRefusal(attacker.CheckPKStatusVsTarget(target, null), "a PK standing outside the Marketplace must not be able to melee a target standing inside it");
        }

        // ================= shooting out from inside =================

        [TestMethod]
        public void AttackerInMarketplace_TargetOutside_MeleeRefused()
        {
            var attacker = SeededPlayer(PlayerKillerStatus.PK, MarketplaceLandblockId);
            var target = SeededPlayer(PlayerKillerStatus.PK, ElsewhereLandblockId);

            AssertCannotAffectAnyoneRefusal(attacker.CheckPKStatusVsTarget(target, null), "a PK standing inside the Marketplace must not be able to melee a target standing outside it");
        }

        // ================= control: neither in the safe zone falls through to retail rules =================

        /// <summary>
        /// Control. Neither player is in the Marketplace, so the safe-zone gate must not fire at all; retail
        /// rules then allow it (two PKs may fight). If the safe-zone gate were ever widened to fire outside the
        /// configured landblock, this test would start failing instead of the refusal tests above.
        /// </summary>
        [TestMethod]
        public void BothOutsideMarketplace_FallsThroughToRetailRules()
        {
            var attacker = SeededPlayer(PlayerKillerStatus.PK, ElsewhereLandblockId);
            var target = SeededPlayer(PlayerKillerStatus.PK, ElsewhereLandblockId);

            Assert.IsNull(attacker.CheckPKStatusVsTarget(target, null), "retail: two PKs outside the Marketplace may fight");
        }

        // ================= beneficial spells stay allowed inside the zone =================

        [TestMethod]
        public void BeneficialSpell_InsideMarketplace_Allowed()
        {
            var attacker = SeededPlayer(PlayerKillerStatus.PK, MarketplaceLandblockId);
            var target = SeededPlayer(PlayerKillerStatus.PK, MarketplaceLandblockId);

            var spell = FakeBeneficialSpell();

            Assert.IsNull(attacker.CheckPKStatusVsTarget(target, spell), "a beneficial spell must stay legal in the Marketplace - only harmful actions are gated");
        }

        // ================= ephemeral instance exclusion (code review 2026-09-26) =================

        /// <summary>
        /// Both players stand at the Marketplace's LandblockShort, but inside an EPHEMERAL instance (an arena
        /// match, a Proving Grounds run, a Thread dungeon reusing that landblock id) rather than the real,
        /// persistent Marketplace. The safe-zone gate must not fire - retail rules then allow it, same as the
        /// BothOutsideMarketplace control - proving the gate reads Location.IsEphemeralRealm and not just the
        /// bare landblock id.
        /// </summary>
        [TestMethod]
        public void BothInEphemeralCopyOfMarketplaceLandblock_FallsThroughToRetailRules()
        {
            var attacker = SeededPlayer(PlayerKillerStatus.PK, MarketplaceLandblockId, isEphemeralRealm: true);
            var target = SeededPlayer(PlayerKillerStatus.PK, MarketplaceLandblockId, isEphemeralRealm: true);

            Assert.IsNull(attacker.CheckPKStatusVsTarget(target, null), "an ephemeral instance copying the Marketplace's landblock id must not inherit its combat-free protection");
        }

        // ================= realm scoping (Marketplace move to 01F5@1, 2026-09-26) =================

        /// <summary>
        /// Both players stand at the Marketplace's landblock id, but in REALM 0 - the retail Aerfalle Keep
        /// dungeon, not the Marketplace. The realm-scoped default must not fire, so retail rules allow it. This
        /// is the case that fails if the gate ever goes back to comparing the landblock id alone (it would
        /// refuse, since 0x01F5 is listed).
        /// </summary>
        [TestMethod]
        public void BothInRealm0CopyOfMarketplaceLandblock_FallsThroughToRetailRules()
        {
            var attacker = SeededPlayer(PlayerKillerStatus.PK, MarketplaceLandblockId, realm: 0);
            var target = SeededPlayer(PlayerKillerStatus.PK, MarketplaceLandblockId, realm: 0);

            Assert.IsNull(attacker.CheckPKStatusVsTarget(target, null), "realm 0's Aerfalle Keep is the retail dungeon, not the Marketplace - PKs may fight there");
        }
    }
}
