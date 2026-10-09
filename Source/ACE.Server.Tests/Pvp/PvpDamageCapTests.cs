using System;
using System.Collections.Generic;
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
    /// The L1 per-hit PvP damage cap (Docs/Pvp/DESIGN.md "PvP rules (levers)"): hit = min(hit, pvp_damage_cap if
    /// &gt; 0, pvp_damage_cap_max_health_fraction * defender max health if &gt; 0), on PvP pairs only, reported to
    /// the observer under the choke point that applied it.
    ///
    /// The four choke points are exercised through PvpRules' own entry points with the observer naming each
    /// point; a real Player cannot be constructed without the world database, so which call site passes which
    /// point is pinned by a source-text test below (matched on CODE only, comments stripped), including that each
    /// cap sits BEFORE the defender's cloak proc at its site.
    ///
    /// SEEDING: no PropertyManager key is read - the dials come from a swapped PvpRuleTunables.DialSource, and the
    /// defender's max health from a swapped PvpRules.MaxHealthSource (seeded Players have no vitals). Every seam
    /// (DialSource, MaxHealthSource, Observer, PvpClassifier.ArenaScopeSource) is saved and restored per test.
    /// </summary>
    [TestClass]
    public class PvpDamageCapTests
    {
        private const double DefenderMaxHealth = 1000;

        private Func<PvpRuleDials> savedDialSource;
        private Func<Creature, double> savedMaxHealth;
        private Action<PvpChokePoint, double, double> savedObserver;
        private Func<Player, Player, bool> savedArenaScope;

        private List<(PvpChokePoint Point, double Before, double After)> observed;

        private static readonly PvpChokePoint[] CapPoints = { PvpChokePoint.C1, PvpChokePoint.C2, PvpChokePoint.C3, PvpChokePoint.C4 };

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpRuleTunables.DialSource;
            savedMaxHealth = PvpRules.MaxHealthSource;
            savedObserver = PvpRules.Observer;
            savedArenaScope = PvpClassifier.ArenaScopeSource;

            observed = new List<(PvpChokePoint, double, double)>();

            PvpRules.MaxHealthSource = c => DefenderMaxHealth;
            PvpRules.Observer = (p, b, a) => observed.Add((p, b, a));
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;
            UseDials(Dials(cap: 0, fraction: 0.5));
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedDialSource;
            PvpRules.MaxHealthSource = savedMaxHealth;
            PvpRules.Observer = savedObserver;
            PvpClassifier.ArenaScopeSource = savedArenaScope;
        }

        // ================= fixtures =================

        private static PvpRuleDials Dials(long cap, double fraction, bool enabled = true) =>
            PvpRuleTunables.Defaults with { Enabled = enabled, DamageCap = cap, DamageCapMaxHealthFraction = fraction };

        private static void UseDials(PvpRuleDials dials) => PvpRuleTunables.DialSource = () => dials;

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

        /// <summary>The value one choke point's entry returns for an 800-point hit from a to b (C4 uses the integer overload, as its site does).</summary>
        private static double Hit(PvpChokePoint point, WorldObject a, Creature b, uint amount = 800)
        {
            return point == PvpChokePoint.C4
                ? PvpRules.ApplyDamageCap(point, a, b, amount)
                : PvpRules.ApplyDamageCap(point, a, b, (float)amount);
        }

        // ================= the cap at each choke point =================

        [TestMethod]
        public void Cap_AppliesAtEachChokePoint_AndNamesItViaTheObserver()
        {
            var attacker = SeededPlayer();
            var defender = SeededPlayer();

            foreach (var point in CapPoints)
            {
                observed.Clear();

                var result = Hit(point, attacker, defender);

                Assert.AreEqual(500.0, result, 1e-9, $"{point}: 0.5 x 1000 max health caps an 800 hit at 500");
                Assert.AreEqual(1, observed.Count, $"{point}: exactly one observer report");
                Assert.AreEqual(point, observed[0].Point, "the observer must name the choke point that capped the hit");
                Assert.AreEqual(800.0, observed[0].Before, 1e-9);
                Assert.AreEqual(500.0, observed[0].After, 1e-9);
            }
        }

        [TestMethod]
        public void Cap_BumpsTheSinceBootCount_ForTheNamedPointOnly()
        {
            var before = new Dictionary<PvpChokePoint, long>();

            foreach (PvpChokePoint p in Enum.GetValues(typeof(PvpChokePoint)))
                before[p] = PvpRules.GetAppliedCount(p);

            Hit(PvpChokePoint.C3, SeededPlayer(), SeededPlayer());

            foreach (PvpChokePoint p in Enum.GetValues(typeof(PvpChokePoint)))
                Assert.AreEqual(before[p] + (p == PvpChokePoint.C3 ? 1 : 0), PvpRules.GetAppliedCount(p), $"count for {p}");
        }

        [TestMethod]
        public void Cap_AppliesInArenaScopeToo()
        {
            PvpClassifier.ArenaScopeSource = (x, y) => true;

            Assert.AreEqual(500.0, Hit(PvpChokePoint.C1, SeededPlayer(), SeededPlayer()), 1e-9);
        }

        // ================= fraction vs absolute =================

        [TestMethod]
        public void Cap_AbsoluteSmallerThanFraction_AbsoluteWins()
        {
            UseDials(Dials(cap: 300, fraction: 0.5));

            Assert.AreEqual(300.0, Hit(PvpChokePoint.C1, SeededPlayer(), SeededPlayer()), 1e-9);
        }

        [TestMethod]
        public void Cap_FractionSmallerThanAbsolute_FractionWins()
        {
            UseDials(Dials(cap: 700, fraction: 0.5));

            Assert.AreEqual(500.0, Hit(PvpChokePoint.C2, SeededPlayer(), SeededPlayer()), 1e-9);
        }

        [TestMethod]
        public void Cap_AbsoluteOnly_FractionOff()
        {
            UseDials(Dials(cap: 600, fraction: 0));

            var maxHealthReads = 0;
            PvpRules.MaxHealthSource = c => { maxHealthReads++; return DefenderMaxHealth; };

            Assert.AreEqual(600.0, Hit(PvpChokePoint.C3, SeededPlayer(), SeededPlayer()), 1e-9);
            Assert.AreEqual(0, maxHealthReads, "with the fraction cap off the defender's max health is never read");
        }

        [TestMethod]
        public void Cap_BothOff_IsUnchanged()
        {
            UseDials(Dials(cap: 0, fraction: 0));

            foreach (var point in CapPoints)
                Assert.AreEqual(800.0, Hit(point, SeededPlayer(), SeededPlayer()), 1e-9, $"{point}: both caps at 0 must be off");

            Assert.AreEqual(0, observed.Count, "an unchanged hit is never reported");
        }

        [TestMethod]
        public void Cap_HitAlreadyUnderTheCap_IsUnchanged_AndNotReported()
        {
            foreach (var point in CapPoints)
                Assert.AreEqual(400.0, Hit(point, SeededPlayer(), SeededPlayer(), 400), 1e-9, point.ToString());

            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void MasterSwitchOff_IsUnchanged()
        {
            UseDials(Dials(cap: 1, fraction: 0.0001, enabled: false));

            foreach (var point in CapPoints)
                Assert.AreEqual(800.0, Hit(point, SeededPlayer(), SeededPlayer()), 1e-9, $"{point}: pvp_rules_enabled false hands the hit back");

            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void SelfHit_IsNotCapped()
        {
            var player = SeededPlayer();

            UseDials(Dials(cap: 1, fraction: 0.0001));

            foreach (var point in CapPoints)
                Assert.AreEqual(800.0, Hit(point, player, player), 1e-9, $"{point}: a player hitting themself is not PvP");
        }

        [TestMethod]
        public void IntegerOverload_FloorsAFractionalCap()
        {
            PvpRules.MaxHealthSource = c => 333;   // 0.5 x 333 = 166.5

            Assert.AreEqual(166u, PvpRules.ApplyDamageCap(PvpChokePoint.C4, SeededPlayer(), SeededPlayer(), 800u));
        }

        // ================= drain gain =================

        [TestMethod]
        public void Drain_GainIsRecomputedFromTheCappedLoss()
        {
            // an uncapped drain of 800 at 30% loss would have paid the caster 560; capped to 500 it pays 350
            var cappedLoss = PvpRules.ApplyDamageCap(PvpChokePoint.C4, SeededPlayer(), SeededPlayer(), 800u);

            Assert.AreEqual(500u, cappedLoss);
            Assert.AreEqual(350u, PvpRules.RecomputeDrainGain(cappedLoss, priorGain: 560, lossPercent: 0.3f, boostMod: 1.0f));
        }

        [TestMethod]
        public void Drain_RecomputedGain_NeverExceedsThePriorBound()
        {
            // the caster was only missing 100 health, so the prior gain was already bounded at 100
            Assert.AreEqual(100u, PvpRules.RecomputeDrainGain(500, priorGain: 100, lossPercent: 0.3f, boostMod: 1.0f));
        }

        [TestMethod]
        public void Drain_RecomputedGain_AppliesTheBoostMod_AndFloorsAtZero()
        {
            Assert.AreEqual(175u, PvpRules.RecomputeDrainGain(500, priorGain: 1000, lossPercent: 0.3f, boostMod: 0.5f));
            Assert.AreEqual(0u, PvpRules.RecomputeDrainGain(500, priorGain: 1000, lossPercent: 1.5f, boostMod: 1.0f));
        }

        // ================= pure CapDamage =================

        [TestMethod]
        public void CapDamage_Pure()
        {
            Assert.AreEqual(500.0, PvpRules.CapDamage(800, 0, 0.5, 1000), 1e-9);
            Assert.AreEqual(300.0, PvpRules.CapDamage(800, 300, 0.5, 1000), 1e-9);
            Assert.AreEqual(800.0, PvpRules.CapDamage(800, 0, 0, 1000), 1e-9);
            Assert.AreEqual(800.0, PvpRules.CapDamage(800, -5, -0.5, 1000), 1e-9, "negative settings are off");
            Assert.AreEqual(800.0, PvpRules.CapDamage(800, 0, 0.5, 0), 1e-9, "no known max health: the fraction cap cannot apply");
            Assert.AreEqual(201.0, PvpRules.CapDamage(800, 0, 0.5, 403), 1e-9, "the fraction cap is floored: 0.5 x 403 = 201.5 -> 201");
        }

        /// <summary>
        /// C1/C2 Math.Round the capped float before it lands, so an unfloored 201.5 would land as 202 - more than
        /// half the bar. The float entry must hand back the floored 201.
        /// </summary>
        [TestMethod]
        public void FloatOverload_FloorsAFractionalCap()
        {
            PvpRules.MaxHealthSource = c => 403;

            Assert.AreEqual(201.0f, PvpRules.ApplyDamageCap(PvpChokePoint.C1, SeededPlayer(), SeededPlayer(), 800.0f));
        }

        // ================= call-site pins =================

        /// <summary>
        /// Each choke point is wired at exactly one call site, in the file its id names, and sits BEFORE that
        /// site's defender reduction (the cloak damage proc, or TakeDamage for C1). Matched on CODE only.
        /// </summary>
        [TestMethod]
        public void ChokePointCallSites_ArePinned_AndPrecedeTheCloakProc()
        {
            var root = FindSourceRoot();

            AssertSite(root, "ACE.Server/WorldObjects/Player_Combat.cs",
                "damageEvent.Damage = PvpRules.ApplyDamageCap(PvpChokePoint.C1, this, target, uncappedDamage);",
                "targetPlayer.TakeDamage(this, damageEvent);");

            AssertSite(root, "ACE.Server/WorldObjects/SpellProjectile.cs",
                "damage = PvpRules.ApplyDamageCap(PvpChokePoint.C2, ProjectileSource, target, damage);",
                "if (equippedCloak != null && Cloak.HasDamageProc(equippedCloak) && Cloak.RollProc(equippedCloak, percent))");

            // M1 / M2 (the damage mods) sit BEFORE the cap at their site, so a raised mod can still be capped;
            // M1 sits AFTER AM1, so the two multiply rather than one hiding the other. The M2 line also pins that
            // the kind comes from Spell.School (so void and life projectiles are never WarMagic) and the crit flag
            // from DamageTarget's own critical argument.
            AssertSite(root, "ACE.Server/WorldObjects/Player_Combat.cs",
                "damageEvent.Damage = Pvp.Rules.PvpArenaOneVOneRules.ApplyDmgMod1v1(this, target, damageEvent.Damage);",
                "damageEvent.Damage = PvpRules.ApplyDamageMods(PvpChokePoint.M1, this, target, damageEvent.Damage, PvpRules.WeaponDamageKind(damageEvent.CombatType), damageEvent.IsCritical, PvpHitProfile.ForWeapon(damageEvent.Weapon, damageEvent.CombatType));");

            AssertSite(root, "ACE.Server/WorldObjects/Player_Combat.cs",
                "damageEvent.Damage = PvpRules.ApplyDamageMods(PvpChokePoint.M1, this, target, damageEvent.Damage, PvpRules.WeaponDamageKind(damageEvent.CombatType), damageEvent.IsCritical, PvpHitProfile.ForWeapon(damageEvent.Weapon, damageEvent.CombatType));",
                "damageEvent.Damage = PvpRules.ApplyDamageCap(PvpChokePoint.C1, this, target, uncappedDamage);");

            AssertSite(root, "ACE.Server/WorldObjects/SpellProjectile.cs",
                "damage = PvpRules.ApplyDamageMods(PvpChokePoint.M2, ProjectileSource, target, damage, PvpRules.ProjectileDamageKind(Spell.School), critical, PvpHitProfile.ForSpell(Spell.School, SpellType));",
                "damage = PvpRules.ApplyDamageCap(PvpChokePoint.C2, ProjectileSource, target, damage);");

            // AB1 (the magic absorb mod) sits AFTER the retail PvP 0.72, and before absorbMod is used by either
            // the life or the war/void damage formula.
            AssertSite(root, "ACE.Server/WorldObjects/SpellProjectile.cs",
                "absorbMod *= 0.72f;",
                "absorbMod = PvpRules.ApplyMagicAbsorbMod(source, target, absorbMod);");

            AssertSite(root, "ACE.Server/WorldObjects/SpellProjectile.cs",
                "absorbMod = PvpRules.ApplyMagicAbsorbMod(source, target, absorbMod);",
                "if (Spell.MetaSpellType == ACE.Entity.Enum.SpellType.LifeProjectile)");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "var cappedHarm = PvpRules.ApplyDamageCap(PvpChokePoint.C3, this, targetCreature, harmDamage);",
                "var reduced = -Cloak.GetReducedAmount(this, targetCreature, -tryBoost);");

            // N3 / N4 (effective-HP normalization) sit BEFORE the C3 / C4 cap, so a raised hit can still be capped
            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "var normalizedHarm = PvpRules.ApplyHealthNormalization(PvpChokePoint.N3, this, targetCreature, harmDamage);",
                "var cappedHarm = PvpRules.ApplyDamageCap(PvpChokePoint.C3, this, targetCreature, harmDamage);");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "harmDamage = normalizedHarm;",
                "var cappedHarm = PvpRules.ApplyDamageCap(PvpChokePoint.C3, this, targetCreature, harmDamage);");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "var normalizedDrain = PvpRules.ApplyHealthNormalization(PvpChokePoint.N4, this, transferSource, srcVitalChange);",
                "var cappedDrain = PvpRules.ApplyDamageCap(PvpChokePoint.C4, this, transferSource, srcVitalChange);");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "srcVitalChange = normalizedDrain;",
                "var cappedDrain = PvpRules.ApplyDamageCap(PvpChokePoint.C4, this, transferSource, srcVitalChange);");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "var cappedDrain = PvpRules.ApplyDamageCap(PvpChokePoint.C4, this, transferSource, srcVitalChange);",
                "var reduced = Cloak.GetReducedAmount(this, targetCreature, srcVitalChange);");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "destVitalChange = PvpRules.RecomputeDrainGain(srcVitalChange, destVitalChange, spell.LossPercent, boostMod);",
                "var reduced = Cloak.GetReducedAmount(this, targetCreature, srcVitalChange);");

            // OT1-OT4 (the arena overtime damage ramp, Docs/Pvp/DESIGN.md "Overtime") sit directly AFTER their C cap,
            // so the ramp multiplies the capped hit, and BEFORE the defender's cloak proc (TakeDamage for OT1).
            AssertSite(root, "ACE.Server/WorldObjects/Player_Combat.cs",
                "damageEvent.Damage = PvpRules.ApplyDamageCap(PvpChokePoint.C1, this, target, uncappedDamage);",
                "damageEvent.Damage = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT1, this, target, damageEvent.Damage);");

            AssertSite(root, "ACE.Server/WorldObjects/Player_Combat.cs",
                "damageEvent.Damage = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT1, this, target, damageEvent.Damage);",
                "targetPlayer.TakeDamage(this, damageEvent);");

            AssertSite(root, "ACE.Server/WorldObjects/SpellProjectile.cs",
                "damage = PvpRules.ApplyDamageCap(PvpChokePoint.C2, ProjectileSource, target, damage);",
                "damage = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT2, ProjectileSource, target, damage);");

            AssertSite(root, "ACE.Server/WorldObjects/SpellProjectile.cs",
                "damage = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT2, ProjectileSource, target, damage);",
                "if (equippedCloak != null && Cloak.HasDamageProc(equippedCloak) && Cloak.RollProc(equippedCloak, percent))");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "var cappedHarm = PvpRules.ApplyDamageCap(PvpChokePoint.C3, this, targetCreature, harmDamage);",
                "var rampedHarm = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT3, this, targetCreature, (float)-tryBoost);");

            // the OT3 result is written back into BOTH tryBoost and boost, before the cloak proc reads tryBoost
            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "var rampedHarm = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT3, this, targetCreature, (float)-tryBoost);",
                "tryBoost = boost = -(int)Math.Min(int.MaxValue, Math.Floor(rampedHarm));");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "tryBoost = boost = -(int)Math.Min(int.MaxValue, Math.Floor(rampedHarm));",
                "var reduced = -Cloak.GetReducedAmount(this, targetCreature, -tryBoost);");

            // OT4 is the exception (code review on #1433): it sits at the drained player's Health write, AFTER every
            // line that derives the caster's gain from the loss - the C4 recompute, the cloak recompute, and HL5 -
            // so a ramped loss can never re-enter destVitalChange; and BEFORE the pre-write mitigations and the
            // vital write, so those still see the ramped loss. OT4AtTheHealthWrite_FollowsEveryGainDerivation pins
            // that no destVitalChange assignment follows it.
            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "destVitalChange = PvpRules.DrainGainFromLoss(srcVitalChange, spell.LossPercent, boostMod);",
                "srcVitalChange = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT4, this, transferSource, srcVitalChange);");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "destVitalChange = PvpRules.ApplyHealingModToRecipient(PvpChokePoint.HL5, destination, destVitalChange);",
                "srcVitalChange = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT4, this, transferSource, srcVitalChange);");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "srcVitalChange = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT4, this, transferSource, srcVitalChange);",
                "srcVitalChange = mitigationTarget.ApplyPreWriteDamageClassAbilities(this, DamageType.Health, srcVitalChange);");

            AssertSite(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "srcVitalChange = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT4, this, transferSource, srcVitalChange);",
                "srcVitalChange = (uint)-transferSource.UpdateVitalDelta(transferSource.Health, -(int)srcVitalChange);");
        }

        /// <summary>
        /// OT4 must never feed the caster's Drain gain: within HandleCastSpell_Transfer, no code line after the OT4
        /// line may assign destVitalChange from srcVitalChange (the destination vital writes, which only clamp
        /// destVitalChange to what was applied, are allowed). A move of OT4 back above the cloak proc's gain
        /// recompute - the #1433 review bug - fails this.
        /// </summary>
        [TestMethod]
        public void OT4AtTheHealthWrite_FollowsEveryGainDerivation()
        {
            var path = Path.Combine(FindSourceRoot(), "ACE.Server", "WorldObjects", "WorldObject_Magic.cs");
            var lines = File.ReadAllLines(path).Select(raw =>
            {
                var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                return (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();
            }).ToArray();

            var ot4 = Array.FindIndex(lines, l => l == "srcVitalChange = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT4, this, transferSource, srcVitalChange);");
            Assert.IsTrue(ot4 >= 0, "the OT4 line is missing");

            var methodStart = Array.FindLastIndex(lines, ot4, l => l.Contains("HandleCastSpell_Transfer("));
            Assert.IsTrue(methodStart >= 0, "OT4 must sit inside HandleCastSpell_Transfer");

            var nextMethod = Array.FindIndex(lines, ot4 + 1, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^(public|private|protected|internal)\b.*\("));
            var end = nextMethod >= 0 ? nextMethod : lines.Length;

            for (var i = ot4 + 1; i < end; i++)
                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"\bdestVitalChange\s*[-+*/]?=[^=]") && lines[i].Contains("srcVitalChange"), $"line {i + 1} derives destVitalChange from the (ramped) loss after OT4: `{lines[i]}`");
        }

        private static void AssertSite(string root, string file, string code, string laterCode)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var lines = File.ReadAllLines(path);
            var hits = new List<int>();
            var later = new List<int>();

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                var stripped = (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();

                if (stripped == code)
                    hits.Add(i);

                if (stripped == laterCode)
                    later.Add(i);
            }

            Assert.AreEqual(1, hits.Count, $"{file}: expected exactly one code line `{code}`");
            Assert.AreEqual(1, later.Count, $"{file}: expected exactly one code line `{laterCode}` to order against");
            Assert.IsTrue(hits[0] < later[0], $"{file}: `{code}` (line {hits[0] + 1}) must come before `{laterCode}` (line {later[0] + 1})");
        }

        /// <summary>
        /// AM1's scope (Docs/Pvp/DESIGN.md "Tunables"): pvp_arena_dmg_mod_1v1 must be applied inside
        /// SpellProjectile.CalculateDamage's "war/void magic projectiles" else-branch (mirroring Doctide's own
        /// placement exactly, SpellProjectile.cs "Apply Arena 1v1 Dmg Mod" inside its war/void-only else) and
        /// must NOT be applied inside the Life projectile branch above it. This is a STRUCTURAL placement check
        /// only, matched on code text with line numbers, not a behavioral damage-scaling test: CalculateDamage's
        /// Spell.MinDamage/MaxDamage/School/DamageType all read the client dat's SpellBase through Spell, which
        /// this test host has no dat for, so a real war/void vs Life projectile damage computation cannot be
        /// exercised here. PvpArenaOneVOneRulesTests pins the SCALING RULE itself (a Live 1v1 pair is scaled, a
        /// 2v2/no-match pair or a non-player pair is not) at the pure-function and choke-point-entry level.
        /// </summary>
        [TestMethod]
        public void AM1_IsScopedToTheWarVoidBranch_NotToLifeProjectiles()
        {
            var root = FindSourceRoot();
            var path = Path.Combine(root, "ACE.Server", "WorldObjects", "SpellProjectile.cs");
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var lines = File.ReadAllLines(path);

            var lifeProjectileIf = IndexOfCode(lines, "if (Spell.MetaSpellType == ACE.Entity.Enum.SpellType.LifeProjectile)");
            var warVoidElse = IndexOfCode(lines, "// war/void magic projectiles");
            var am1Call = IndexOfCode(lines, "finalDamage = Pvp.Rules.PvpArenaOneVOneRules.ApplyDmgMod1v1(source, target, finalDamage);");

            // A future re-add at the old, over-broad DamageTarget/C2 choke point (the mistake this pin was
            // written to catch) would leave the war/void call in place AND add a second one there - so the
            // placement asserts below are not enough by themselves; the count must stay exactly 1.
            var am1CallCount = lines.Count(l => l.Contains("Pvp.Rules.PvpArenaOneVOneRules.ApplyDmgMod1v1("));
            Assert.AreEqual(1, am1CallCount, "ApplyDmgMod1v1 must be called from exactly ONE site in SpellProjectile.cs - a second call (e.g. a duplicate re-added at the old C2 choke point) would double-apply the multiplier");

            Assert.IsTrue(lifeProjectileIf >= 0, "the Life projectile branch marker was not found - has CalculateDamage been restructured?");
            Assert.IsTrue(warVoidElse >= 0, "the war/void else-branch marker was not found - has CalculateDamage been restructured?");
            Assert.IsTrue(am1Call >= 0, "the AM1 (pvp_arena_dmg_mod_1v1) call was not found at all");

            Assert.IsTrue(lifeProjectileIf < warVoidElse, "the Life projectile branch must precede the war/void else-branch");
            Assert.IsTrue(am1Call > warVoidElse, "AM1 must be applied AFTER entering the war/void else-branch, not inside the Life projectile branch above it");

            // The war/void else-branch's closing brace: the first line at the SAME indentation as the
            // "else" line itself, after the AM1 call - proves AM1 sits INSIDE the branch, not after it.
            var elseIndent = lines[warVoidElse].IndexOf("//", StringComparison.Ordinal);
            var branchClose = -1;

            for (var i = am1Call + 1; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd() == new string(' ', elseIndent) + "}")
                {
                    branchClose = i;
                    break;
                }
            }

            Assert.IsTrue(branchClose >= 0, "could not find the war/void else-branch's closing brace after the AM1 call");
            Assert.IsTrue(am1Call < branchClose, "AM1 must be applied BEFORE the war/void else-branch closes");
        }

        private static int IndexOfCode(string[] lines, string code)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                var codeOnly = (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();
                var wholeLine = raw.Trim();

                if (codeOnly == code || wholeLine == code)
                    return i;
            }

            return -1;
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Entity", "DamageEvent.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Entity/DamageEvent.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
