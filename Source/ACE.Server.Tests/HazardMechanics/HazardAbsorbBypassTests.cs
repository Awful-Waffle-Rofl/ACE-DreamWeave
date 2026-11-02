using System;
using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Owner rulings R3 and "hazards bypass all wards" (2026-10-19): a hazard-library hit (HazardContext.HitPlayers,
    /// admin /hazard runs included) bypasses every ward - Sanguine Ward, Mana Barrier, Runic Ward and Reflect Magic's
    /// immunity window - and nothing else on the pre-write hook (Soul Jump is a death save; Adrenaline and Kinetic
    /// Charge are damage-neutral). The absorbs are called with a NULL defender: the bypass must return before the
    /// defender is touched, and the positive controls prove the same call reaches the defender when the flag is off,
    /// so the null is what discriminates. No PropertyManager key is read on the bypass paths.
    /// </summary>
    [TestClass]
    public class HazardAbsorbBypassTests
    {
        private static PreWriteDamageContext Hit(bool isHazard, bool dot = false, ACE.Server.WorldObjects.Creature attacker = null,
            ACE.Server.WorldObjects.WorldObject source = null, uint enemyShare = 0)
            => new PreWriteDamageContext
            {
                DamageType = DamageType.Fire, IsHazard = isHazard, IsDamageOverTime = dot, Attacker = attacker,
                Source = source ?? attacker, SpellDamageFromEnemyCreature = enemyShare, Damage = 100,
            };

        [ClassInitialize]
        public static void Setup(TestContext context) => ACE.Server.Managers.DefaultPropertyManager.LoadDefaultProperties();

        [TestMethod]
        public void RunicWard_HazardHit_IsNotAbsorbed()
        {
            // Runic Ward only absorbs a hit with a filtered creature attacker, so the attacker is set: the null defender
            // is then the only thing standing between the call and an absorb
            var context = Hit(isHazard: true, attacker: ACE.Server.Tests.TestCreatures.CreateDefender(maxHealth: 500));

            new RunicWardAbility().ModifyIncomingDamage(null, 1, context);

            Assert.AreEqual(100u, context.Damage, "the runic ward does not soak a hazard hit");
        }

        [TestMethod]
        public void PositiveControl_RunicWard_NonHazardHit_ReachesTheDefender()
            => Assert.ThrowsExactly<NullReferenceException>(() => new RunicWardAbility().ModifyIncomingDamage(null, 1,
                Hit(isHazard: false, attacker: ACE.Server.Tests.TestCreatures.CreateDefender(maxHealth: 500))));

        [TestMethod]
        public void ReflectMagic_HazardGuard_IsTheFirstStatement()
        {
            // Reflect returns early for a null defender, so a call cannot discriminate; the guard order is pinned instead
            var src = PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/ReflectMagicAbility.cs");
            var body = PooledLootSourceText.MethodBody(src, "public void ModifyIncomingDamage(");
            var guard = body.IndexOf("if (context.IsHazard)", StringComparison.Ordinal);
            var window = body.IndexOf("ApplyReflectImmunityWindow(", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0 && window > guard && body.IndexOf("if (defender == null", StringComparison.Ordinal) > guard);
        }

        [TestMethod]
        public void EveryWardSkipsHazards_FirstThingInItsHandler()
        {
            foreach (var file in new[] { "SanguineWardAbility.cs", "ManaBarrierAbility.cs", "RunicWardAbility.cs", "ReflectMagicAbility.cs" })
            {
                var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/" + file), "public void ModifyIncomingDamage(");
                var code = string.Join("\n", body.Split('\n').Skip(1).Where(l => l.Trim().Length > 0 && l.Trim() != "{" && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                Assert.IsTrue(code.TrimStart().StartsWith("if (context.IsHazard)", StringComparison.Ordinal), file + ": the hazard guard is the first statement");
            }
        }

        [TestMethod]
        public void SanguineWard_HazardHit_IsNotAbsorbed()
        {
            var context = Hit(isHazard: true);

            new SanguineWardAbility().ModifyIncomingDamage(null, 1, context);

            Assert.AreEqual(100u, context.Damage, "the ward does not soak a hazard hit");
        }

        [TestMethod]
        public void ManaBarrier_HazardHit_IsNotDiverted()
        {
            // the DoT branch is the one that needs no attacker, so it is the branch a null defender can expose
            var context = Hit(isHazard: true, dot: true);

            new ManaBarrierAbility().ModifyIncomingDamage(null, 1, context);

            Assert.AreEqual(100u, context.Damage, "the barrier does not divert a hazard hit");
        }

        [TestMethod]
        public void PositiveControl_NonHazardHit_ReachesTheDefender()
        {
            // the same calls with the flag off go on to the defender (null here), so the tests above discriminate.
            // Sanguine Ward needs an attacker since the 2026-10-24 Spellsword pass made its absorb PvE-only (a hit
            // with no attributed creature attacker returns before the defender), same as Runic Ward above
            Assert.ThrowsExactly<NullReferenceException>(() => new SanguineWardAbility().ModifyIncomingDamage(null, 1,
                Hit(isHazard: false, attacker: ACE.Server.Tests.TestCreatures.CreateDefender(maxHealth: 500))));
            Assert.ThrowsExactly<NullReferenceException>(() => new ManaBarrierAbility().ModifyIncomingDamage(null, 1, Hit(isHazard: false, dot: true)));
        }

        // ---- Sanguine Ward's PvE gate (Spellsword pass 2026-10-24) ------------------------------------------------
        //
        // The ruling excludes PvP and self-damage ONLY. Reaching the absorb is observed as the null defender's
        // NullReferenceException; leaving the hit alone is observed as no throw and the damage unchanged.

        [TestMethod]
        public void SanguineWard_DotTick_WithAnEnemyCreatureShare_ReachesTheAbsorb()
            => Assert.ThrowsExactly<NullReferenceException>(() =>
                new SanguineWardAbility().ModifyIncomingDamage(null, 1, Hit(isHazard: false, dot: true, enemyShare: 40)));

        [TestMethod]
        public void SanguineWard_DotTick_WithNoEnemyCreatureShare_IsNotAbsorbed()
        {
            var context = Hit(isHazard: false, dot: true, enemyShare: 0);

            new SanguineWardAbility().ModifyIncomingDamage(null, 1, context);

            Assert.AreEqual(100u, context.Damage, "a tick that is all PvP or self damage leaves the ward alone");
        }

        [TestMethod]
        public void SanguineWard_PlayerSource_IsNotAbsorbed()
        {
            // PvP and self-damage both arrive with a Player source (the defender is a Player)
            var player = (ACE.Server.WorldObjects.Player)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ACE.Server.WorldObjects.Player));
            var context = Hit(isHazard: false, source: player);

            new SanguineWardAbility().ModifyIncomingDamage(null, 1, context);

            Assert.AreEqual(100u, context.Damage, "the ward does not soak a PvP or self-inflicted hit");
        }

        [TestMethod]
        public void SanguineWard_DeadCreatureSource_IsStillAbsorbed()
        {
            // a monster that died mid-swing: the dispatch leaves Attacker null, but it is still monster damage
            var dead = ACE.Server.Tests.TestCreatures.CreateDefender(maxHealth: 500);
            dead.Health.Current = 0;
            Assert.IsTrue(dead.IsDead, "harness control: the source really is dead");

            Assert.ThrowsExactly<NullReferenceException>(() =>
                new SanguineWardAbility().ModifyIncomingDamage(null, 1, Hit(isHazard: false, source: dead)));
        }

        [TestMethod]
        public void HitPlayers_FlagsItsDamageAsHazard_AndOnlyTheWardsReadTheFlag()
        {
            var ctx = PooledLootSourceText.Read("Source/ACE.Server/HazardMechanics/HazardContext.cs");
            // the one place a hazard damages a player is the shared tail ApplyHit; every entry point (every ball/wisp hit, the per-player zones hit and the flat fill hit) goes through it
            var hit = PooledLootSourceText.MethodBody(ctx, "private int ApplyHit(");
            StringAssert.Contains(hit, "player.TakeDamage(source.Object, damageType, amount, BodyPart.Foot, isHazard: true)");
            StringAssert.Contains(PooledLootSourceText.MethodBody(ctx, "private int DamagePlayer("), "ApplyHit(");
            var flat = PooledLootSourceText.MethodBody(ctx, "public int HitPlayerFlat(");
            StringAssert.Contains(flat, "ApplyHit(");
            StringAssert.Contains(flat, "GetResistanceMod(damageType, source.Object, null)");
            StringAssert.Contains(PooledLootSourceText.MethodBody(ctx, "public int HitPlayers("), "DamagePlayer(source, player, fraction, damageType, text)");
            StringAssert.Contains(PooledLootSourceText.MethodBody(ctx, "public int HitPlayer("), "DamagePlayer(source, player, pct, damageType, text)");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(ctx, @"\.TakeDamage\(").Count, "HazardContext has exactly one TakeDamage call");

            var combat = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Combat.cs");
            StringAssert.Contains(combat, "ApplyPreWriteDamageClassAbilities(source, damageType, amount, isHazard: isHazard)");

            var abilities = PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/ClassAbilityHooks.cs");
            StringAssert.Contains(abilities, "public bool IsHazard { get; init; }");

            var dir = Path.Combine(RepoRoot(), "Source", "ACE.Server", "ClassAbilities", "Abilities");
            var readers = Directory.GetFiles(dir, "*.cs")
                .Where(f => File.ReadAllText(f).Contains("context.IsHazard"))
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            // Berserk Rage joined the readers deliberately (Berserker pass 2026-10-24): a hazard neither triggers the rage
            // nor is blocked by its immunity window (BerserkerPassMechanicsTests pins both behaviours).
            CollectionAssert.AreEqual(new[] { "BerserkRageAbility.cs", "ManaBarrierAbility.cs", "ReflectMagicAbility.cs", "RunicWardAbility.cs", "SanguineWardAbility.cs" }, readers,
                "every ward and Berserk Rage read the flag; Soul Jump, Adrenaline and Kinetic Charge deliberately do not");
        }

        [TestMethod]
        public void DigsiteBossMechanics_FlagTheirDamageAsHazard()
        {
            // owner ruling 2026-10-24: the Marae Lassel digsite boss mechanics are hazards (wards skip them, Berserk Rage
            // neither triggers on nor blocks them). Hit is the file's one TakeDamage call.
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteBossMechanics.cs");
            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "public void Hit("), "player.TakeDamage(source, damageType, amount, BodyPart.Foot, isHazard: true)");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(src, @"\.TakeDamage\(").Count, "MlDigsiteBossMechanics has exactly one TakeDamage call");
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.sln")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "Could not find Source/ACE.sln by walking up from " + AppContext.BaseDirectory);
            return dir.FullName;
        }
    }
}
