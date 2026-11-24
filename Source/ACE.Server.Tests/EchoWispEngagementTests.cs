using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Echo Cast wisp's widened engagement rule (EchoWispEngagement.IsEngaged, owner ruling 2026-11-23, design
    /// "Targeting" rule 3): a wisp may move on to a monster attacking the caster, a caster's pet, a fellowship member or a
    /// member's pet, or one the caster has damaged. A monster nobody in the caster's fight is fighting stays off-limits
    /// unless /summonaggro is on, and PetEngagement (Hunter's Echo, combat pets, the taunt pulse) keeps its narrower rule.
    ///
    /// Dat-free. Players are RuntimeHelpers.GetUninitializedObject Players with a biota, a biota lock and a guid (the
    /// damage history keys by guid and reads the attacker's name); nothing else on them is read. The dat-backed stage
    /// case, through the real PickTarget, is EchoWispPickDatTests.Pick_StartTargetDead_MovesOnToHostilesFightingAFellowOrDamagedByTheCaster.
    /// </summary>
    [TestClass]
    public class EchoWispEngagementTests
    {
        private static uint nextGuid = 0x7F6C0000;
        private static uint nextWcid = 995800;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Biota>k__BackingField")]
        private static extern ref ACE.Entity.Models.Biota BiotaField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BiotaDatabaseLock")]
        private static extern ref ReaderWriterLockSlim BiotaLockField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();

            // before anything here touches a Player-declared member (its static initializer reads the world database)
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        private static Player NewPlayer(Fellowship fellowship = null)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            BiotaField(player) = new ACE.Entity.Models.Biota();
            BiotaLockField(player) = new ReaderWriterLockSlim();
            GuidField(player) = new ObjectGuid(nextGuid++);
            player.Fellowship = fellowship;
            return player;
        }

        private static Fellowship NewFellowship() => (Fellowship)RuntimeHelpers.GetUninitializedObject(typeof(Fellowship));

        private static Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> Vitals() =>
            new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> { [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100, CurrentLevel = 100 } };

        private static CombatPet PetOf(Player owner)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Pet" } },
                PropertiesAttribute2nd = Vitals(),
            };

            return new CombatPet(weenie, new ObjectGuid(nextGuid++)) { P_PetOwner = owner };
        }

        private static Creature Monster(WorldObject attackTarget = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Monster" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesAttribute2nd = Vitals(),
            };

            return new Creature(weenie, new ObjectGuid(nextGuid++)) { AttackTarget = attackTarget };
        }

        /// <summary>Puts <paramref name="attacker"/> in <paramref name="monster"/>'s damage totals, as DamageHistory.Add does, without the hit's side effects.</summary>
        private static void Damage(Creature monster, WorldObject attacker) =>
            monster.DamageHistory.TotalDamage.Add(attacker.Guid, new DamageHistoryInfo(attacker, 50f));

        [TestMethod]
        public void Engaged_WhenAttackingTheCasterOrTheCastersPet()
        {
            var caster = NewPlayer();

            Assert.IsTrue(EchoWispEngagement.IsEngaged(Monster(caster), caster), "attacking the caster");
            Assert.IsTrue(EchoWispEngagement.IsEngaged(Monster(PetOf(caster)), caster), "attacking the caster's pet");
        }

        [TestMethod]
        public void Engaged_WhenAttackingAFellowOrAFellowsPet()
        {
            var fellowship = NewFellowship();
            var caster = NewPlayer(fellowship);
            var fellow = NewPlayer(fellowship);

            Assert.IsTrue(EchoWispEngagement.IsEngaged(Monster(fellow), caster), "attacking a fellowship member");
            Assert.IsTrue(EchoWispEngagement.IsEngaged(Monster(PetOf(fellow)), caster), "attacking a fellowship member's pet");

            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(Monster(fellow), caster),
                "PetEngagement is untouched: Hunter's Echo and combat pets still count only the owner's own side");
        }

        [TestMethod]
        public void Engaged_WhenTheCasterHasDamagedIt()
        {
            var caster = NewPlayer();
            var idle = Monster();

            Assert.IsFalse(EchoWispEngagement.IsEngaged(idle, caster), "control: an idle, undamaged monster is not engaged");

            Damage(idle, caster);
            Assert.IsTrue(EchoWispEngagement.IsEngaged(idle, caster), "the caster appears in its damage history");

            var fightingAStranger = Monster(NewPlayer());
            Damage(fightingAStranger, caster);
            Assert.IsTrue(EchoWispEngagement.IsEngaged(fightingAStranger, caster), "damaged by the caster, whoever it is attacking now");
        }

        [TestMethod]
        public void NotEngaged_WhenAttackingAPlayerOutsideTheFellowship_AndUndamagedByTheCaster()
        {
            var caster = NewPlayer(NewFellowship());
            var stranger = NewPlayer(NewFellowship());

            Assert.IsFalse(EchoWispEngagement.IsEngaged(Monster(stranger), caster), "another fellowship's member");
            Assert.IsFalse(EchoWispEngagement.IsEngaged(Monster(PetOf(stranger)), caster), "another fellowship's member's pet");
            Assert.IsFalse(EchoWispEngagement.IsEngaged(Monster(), caster), "nobody's fight");

            var damagedByStranger = Monster(stranger);
            Damage(damagedByStranger, stranger);
            Assert.IsFalse(EchoWispEngagement.IsEngaged(damagedByStranger, caster), "someone else's damage is not the caster's");

            // a caster in no fellowship never matches another unfellowed player (null == null is not a fellowship)
            var loner = NewPlayer();
            Assert.IsFalse(EchoWispEngagement.IsEngaged(Monster(NewPlayer()), loner));
            Assert.IsFalse(EchoWispEngagement.IsFellowSide(NewPlayer(), loner));

            Assert.IsFalse(EchoWispEngagement.IsEngaged(null, caster));
            Assert.IsFalse(EchoWispEngagement.IsEngaged(Monster(caster), null));
            Assert.IsFalse(EchoWispEngagement.IsFellowSide(caster, caster), "the caster itself is PetEngagement's arm, not a fellow");
        }

        [TestMethod]
        public void SummonAggro_LiftsTheRule_AndNothingElseChanges()
        {
            // the controller's rule over the engagement answer (LegalTargetVerdict)
            Assert.AreEqual(EchoWispIneligible.NotInCastersFight,
                EchoWispMath.IneligibleReason(EchoWispSpellKind.Damage, false, false, false, true, false, false, engagedWithOwner: false, summonAggro: false, false, false));
            Assert.AreEqual(EchoWispIneligible.None,
                EchoWispMath.IneligibleReason(EchoWispSpellKind.Damage, false, false, false, true, false, false, engagedWithOwner: false, summonAggro: true, false, false),
                "/summonaggro on: any attackable creature");
            Assert.AreEqual(EchoWispIneligible.Player,
                EchoWispMath.IneligibleReason(EchoWispSpellKind.Damage, true, false, false, true, false, false, engagedWithOwner: true, summonAggro: true, false, false),
                "players are never targets");
        }
    }
}
