using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-10-18 in-game round: protected targets that took no damage (no body parts, high defense), heals,
    /// the finale that "won" before it landed, the faster cadence, guaranteed special waves, the finale boss
    /// fallback, runners and ranged rules, and the health readouts. Shares the builders in WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        // ---- damage: every protected WCID takes the hit -----------------------------------------------------

        /// <summary>
        /// Each protected WCID's combat shape, as ace_world has it (2026-10-18): body parts present or not, the
        /// trained melee defense, and quickness/coordination (which give an untrained defense too). 2000005 and
        /// 2000006 are listed twice: as first shipped (retail Sir Draithon and Sir Donovan have NO body parts,
        /// so every hit read as evaded) and as fixed.
        /// </summary>
        private static readonly (uint Wcid, string Name, bool BodyParts, uint MeleeDefense, uint Quickness, uint Coordination)[] ProtectedProfiles =
        {
            (0, "a plain low-defense NPC", true, 4, 50, 100),
            (2000005, "Sir Draithon, as first shipped", false, 0, 290, 290),
            (2000005, "Sir Draithon", true, 0, 290, 290),
            (2000006, "Sir Donovan, as first shipped", false, 0, 290, 290),
            (2000006, "Sir Donovan", true, 0, 290, 290),
            (2000007, "Sir Adarl (500 defense)", true, 500, 290, 290),
        };

        private static uint nextProfileGuid = 0x7EF00000;

        /// <summary>A protected-target stand-in shaped like one real WCID (see <see cref="ProtectedProfiles"/>), on its own body-part cache key.</summary>
        private static Creature BuildProfiledTarget((uint Wcid, string Name, bool BodyParts, uint MeleeDefense, uint Quickness, uint Coordination) profile)
        {
            TestGameTables.EnsureInitialized();

            var weenie = new Weenie
            {
                WeenieClassId = TestCreatures.NextWcid(),
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Level, 200 } },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Endurance, new PropertiesAttribute { InitLevel = 100 } },
                    { PropertyAttribute.Quickness, new PropertiesAttribute { InitLevel = profile.Quickness } },
                    { PropertyAttribute.Coordination, new PropertiesAttribute { InitLevel = profile.Coordination } },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = (uint)ProtectDef.TimeScaledMaxHealth } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>(),
            };

            if (profile.MeleeDefense > 0)
                weenie.PropertiesSkill[Skill.MeleeDefense] = new PropertiesSkill { InitLevel = profile.MeleeDefense, SAC = SkillAdvancementClass.Specialized };

            if (profile.BodyParts)
            {
                var part = new PropertiesBodyPart
                {
                    DType = DamageType.Bludgeon, DVal = 1, DVar = 0.5f, BaseArmor = 50,
                    HLF = 1, MLF = 1, LLF = 1, HRF = 1, MRF = 1, LRF = 1, HLB = 1, MLB = 1, LLB = 1, HRB = 1, MRB = 1, LRB = 1,
                };

                weenie.PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart> { { CombatBodyPart.Chest, part } };
            }

            var creature = new Creature(weenie, new ObjectGuid(nextProfileGuid++));
            creature.Location = new Position(0x00010064, 55.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            // DamageEvent reads the hit location through this cache (normally built from ace_world). A weenie with
            // no body parts builds the same empty table the server built for the first Sir Draithon.
            Creature.SetBodyPartTable(creature.WeenieClassId, new BodyPartTable(weenie));

            return creature;
        }

        private static (WorldEvent Evt, ProtectDef Protect) BuildTimeScaledRun()
        {
            var protect = BuildProtect();
            protect.Health = null;
            protect.UndefendedSeconds = 180;
            protect.MinDeathSeconds = 60;
            protect.MaxHealPerTenSeconds = 0.10;
            protect.HealReductionAfterHit = 0.5;
            protect.HealReductionSeconds = 5;

            return (BuildHoldEvent(BuildHoldGoal(protect: protect), null), protect);
        }

        [TestMethod]
        public void WaveMonsterHit_ReducesHealth_OnEveryProtectedWcid()
        {
            var (evt, _) = BuildTimeScaledRun();

            foreach (var profile in ProtectedProfiles)
            {
                var target = BuildProfiledTarget(profile);
                target.WorldEventProtected = true;
                target.P_WorldEvent = evt;

                // A low-level wave monster (attack skill 50) that would almost never land on a 500-defense target.
                var monster = TestCreatures.CreateAttacker(attackSkill: 50, overpower: false);
                monster.P_WorldEvent = evt;

                Assert.IsTrue(DamageEvent.IsWorldEventProtectedHit(monster, target), profile.Name);

                for (var swing = 0; swing < 50; swing++)
                {
                    var before = target.Health.Current;
                    var hit = DamageEvent.CalculateDamage(monster, target, null);

                    Assert.IsFalse(hit.Evaded, $"{profile.Name}: a run monster's hit on a protected target never misses");
                    Assert.IsTrue(hit.HasDamage && hit.Damage >= 1, $"{profile.Name}: the hit carries damage ({hit.Damage})");

                    target.TakeDamage(monster, hit.DamageType, hit.Damage, hit.IsCritical);

                    Assert.IsTrue(target.Health.Current < before, $"{profile.Name}: swing {swing} took no health ({before} -> {target.Health.Current})");

                    if (target.Health.Current < target.Health.MaxValue / 2)
                        break;
                }

                Assert.IsTrue(target.Health.Current < target.Health.MaxValue, profile.Name);
            }
        }

        [TestMethod]
        public void WaveMonsterHit_StockRulesStillApply_ToEverythingElse()
        {
            var (evt, _) = BuildTimeScaledRun();

            // The same shapes NOT protected: no body parts is always a miss, 500 defense dodges a weak monster.
            var noParts = BuildProfiledTarget((2000005, "unprotected, no body parts", false, 0, 290, 290));
            noParts.P_WorldEvent = evt;

            var dodger = BuildProfiledTarget((2000007, "unprotected, 500 defense", true, 500, 290, 290));
            dodger.P_WorldEvent = evt;

            var monster = TestCreatures.CreateAttacker(attackSkill: 50, overpower: false);
            monster.P_WorldEvent = evt;

            Assert.IsFalse(DamageEvent.IsWorldEventProtectedHit(monster, noParts));

            for (var i = 0; i < 20; i++)
                Assert.IsTrue(DamageEvent.CalculateDamage(monster, noParts, null).Evaded, "unchanged: no body part is a miss");

            var dodged = Enumerable.Range(0, 100).Count(_ => DamageEvent.CalculateDamage(monster, dodger, null).Evaded);
            Assert.IsTrue(dodged > 80, $"unchanged: 500 defense dodges most of a weak monster's swings ({dodged}/100)");

            // A monster from ANOTHER run gets no free hits on a protected target.
            var otherRun = BuildTimeScaledRun().Evt;
            var target = BuildProfiledTarget((2000005, "protected", false, 0, 290, 290));
            target.WorldEventProtected = true;
            target.P_WorldEvent = evt;

            var stranger = TestCreatures.CreateAttacker(attackSkill: 50, overpower: false);
            stranger.P_WorldEvent = otherRun;

            Assert.IsFalse(DamageEvent.IsWorldEventProtectedHit(stranger, target));
        }

        [TestMethod]
        public void UndefendedTarget_FallsInAboutUndefendedSeconds_AndNeverFasterThanTheCap()
        {
            var (evt, protect) = BuildTimeScaledRun();
            var target = BuildProfiledTarget(ProtectedProfiles.Last());
            target.WorldEventProtected = true;
            target.P_WorldEvent = evt;

            var max = target.Health.MaxValue;

            // One attacker swinging every 2 s: about 180 s.
            var one = SecondsUntilDown(target, 1, 2.0, protect, out _);
            Assert.IsTrue(one >= 170 && one <= 190, $"one attacker took {one} s");

            // A solo wave's seekers on one target (11 x 0.8 / 3 targets = 3): about 60 s.
            var three = SecondsUntilDown(BuildFreshTarget(evt), 3, 2.0, protect, out _);
            Assert.IsTrue(three >= 58 && three <= 66, $"three attackers took {three} s");

            // A whole wave on one target (a breach): still never under minDeathSeconds.
            var crowd = SecondsUntilDown(BuildFreshTarget(evt), 12, 2.0, protect, out _);
            Assert.IsTrue(crowd >= 58, $"twelve attackers took {crowd} s, the cap is 60");

            // Runners swing 1.5x as fast and hurt 1.5x as fast: one runner about 120 s.
            var runner = SecondsUntilDown(BuildFreshTarget(evt), 1, 2.0 / 1.5, protect, out _, attackSpeedMult: 1.5f);
            Assert.IsTrue(runner >= 112 && runner <= 128, $"one runner took {runner} s");

            Assert.AreEqual(max, BuildFreshTarget(evt).Health.MaxValue);
        }

        private static Creature BuildFreshTarget(WorldEvent evt)
        {
            var target = BuildProfiledTarget(ProtectedProfiles.Last());
            target.WorldEventProtected = true;
            target.P_WorldEvent = evt;
            return target;
        }

        /// <summary>Seconds until <paramref name="target"/> falls to <paramref name="attackers"/> run monsters each hitting every <paramref name="swingSeconds"/>, through the real scale and cap.</summary>
        private static double SecondsUntilDown(Creature target, int attackers, double swingSeconds, ProtectDef protect, out double lost, float attackSpeedMult = 1.0f,
            bool corrupt = false)
        {
            var monsters = Enumerable.Range(0, attackers).Select(_ =>
            {
                var m = BuildHoldCreature();
                m.P_WorldEvent = target.P_WorldEvent;
                m.WorldEventAttackSpeedMult = attackSpeedMult;
                m.WorldEventCorrupted = corrupt;
                return m;
            }).ToList();

            var health = (double)target.Health.MaxValue;
            lost = 0;

            for (var t = 0.0; t < 600; t += swingSeconds)
            {
                foreach (var m in monsters)
                {
                    var hit = target.ScaleWorldEventProtectedHit(m, 30f, T0 + t);
                    lost += hit;

                    if (lost >= health)
                        return t;
                }
            }

            return 600;
        }

        // ---- heals ------------------------------------------------------------------------------------------

        /// <summary>
        /// A heal spell, as Player.IsInvalidTarget and WorldObject_Magic run it on a protected target: the target
        /// check passes for a health Boost, the boost is converted and capped (ConvertWorldEventProtectedHeal),
        /// and UpdateVitalDelta writes it. A live Player and Spell cannot be built in this test project (they need
        /// the database and the client dat files), so the spell is given by its fields.
        /// </summary>
        [TestMethod]
        public void HealSpell_RaisesAProtectedTarget_WithinTheCap()
        {
            var (evt, protect) = BuildTimeScaledRun();
            var target = BuildFreshTarget(evt);
            var max = target.Health.MaxValue;

            target.UpdateVitalDelta(target.Health, -(int)(max / 2));
            var start = target.Health.Current;

            // Heal Other VI: a beneficial health Boost of about 100.
            var isHeal = Creature.IsWorldEventProtectedHealSpell(true, SpellType.Boost, DamageType.Health, 80, 120);
            Assert.IsTrue(isHeal);
            Assert.IsFalse(Creature.IsInvalidWorldEventProtectedSpellTarget(target.AcceptsWorldEventPlayerHeal, isHeal), "a heal spell is a valid target");
            Assert.IsTrue(Creature.IsInvalidWorldEventProtectedSpellTarget(target.AcceptsWorldEventPlayerHeal,
                Creature.IsWorldEventProtectedHealSpell(true, SpellType.Enchantment, DamageType.Health, 0, 0)), "a buff is not");

            var converted = target.ConvertWorldEventProtectedHeal(100, T0);
            var healed = target.UpdateVitalDelta(target.Health, converted);

            Assert.IsTrue(healed > 0 && target.Health.Current > start, $"healed {healed}");
            Assert.AreEqual((int)Math.Floor(max * 0.04), healed, "a typical heal is 4% of max");

            // Spamming heals at once: capped at maxHealPerTenSeconds (10%), which then refills over 10 s.
            for (var i = 0; i < 20; i++)
                target.UpdateVitalDelta(target.Health, target.ConvertWorldEventProtectedHeal(100, T0));

            Assert.IsTrue(target.Health.Current - start <= max * protect.MaxHealPerTenSeconds + 1,
                $"healed {target.Health.Current - start} in 10 s, the cap is {max * protect.MaxHealPerTenSeconds}");
        }

        [TestMethod]
        public void HealingKit_RaisesAProtectedTarget_HalvedRightAfterAHit()
        {
            var (evt, _) = BuildTimeScaledRun();

            // The kit path (Healer.DoHealingWorldEventProtected): a raw kit heal, converted, then written.
            var kitRaw = 300 * 1.0 * 0.35;   // skill 300, kit bonus 1, the middle of the 20-50% roll

            var calm = BuildFreshTarget(evt);
            calm.UpdateVitalDelta(calm.Health, -5000);
            var calmBefore = calm.Health.Current;
            var calmHealed = calm.UpdateVitalDelta(calm.Health, calm.ConvertWorldEventProtectedHeal(kitRaw, T0));

            Assert.IsTrue(calmHealed > 0 && calm.Health.Current > calmBefore, "the kit heals");

            // The same kit right after a run monster's hit: half (healReductionAfterHit 0.5 for 5 s).
            var hit = BuildFreshTarget(evt);
            hit.UpdateVitalDelta(hit.Health, -5000);

            var monster = BuildHoldCreature();
            monster.P_WorldEvent = evt;
            hit.ScaleWorldEventProtectedHit(monster, 30f, T0);

            var hitBefore = hit.Health.Current;
            var hitHealed = hit.UpdateVitalDelta(hit.Health, hit.ConvertWorldEventProtectedHeal(kitRaw, T0 + 1));

            Assert.IsTrue(hitHealed > 0 && hit.Health.Current > hitBefore, "still heals after a hit");
            Assert.AreEqual(calmHealed / 2.0, hitHealed, 1.0, "half right after a hit");

            // After the 5 s window: full again.
            var later = BuildFreshTarget(evt);
            later.UpdateVitalDelta(later.Health, -5000);
            later.ScaleWorldEventProtectedHit(monster, 30f, T0);
            Assert.AreEqual(calmHealed, later.UpdateVitalDelta(later.Health, later.ConvertWorldEventProtectedHeal(kitRaw, T0 + 6)), 1.0);
        }

        // ---- the finale must land before the run can be won --------------------------------------------------

        [TestMethod]
        public void WavesWon_WaitsForTheFinaleToBePlaced()
        {
            // The in-game run: the finale (wave 5) was started, nothing of it placed yet, every earlier wave cleared.
            var unplaced = new HoldWaveCounts(started: 5, landed: 4, cleared: 4, live: 0, resolved: 4);
            Assert.IsFalse(HoldObjective.WavesWon(unplaced, 5), "the finale has not landed: not won");

            var fighting = new HoldWaveCounts(5, 5, 4, 7, 5);
            Assert.IsFalse(HoldObjective.WavesWon(fighting, 5), "the finale is fighting");

            var beaten = new HoldWaveCounts(5, 5, 5, 0, 5);
            Assert.IsTrue(HoldObjective.WavesWon(beaten, 5), "the finale and its boss are dead");

            // A finale whose placement failed entirely still resolves, so the run can end.
            Assert.IsTrue(HoldObjective.WavesWon(new HoldWaveCounts(5, 4, 4, 0, 5), 5));
        }

        [TestMethod]
        public void ResolvedWaves_TheSpawnersCount_OrAllAfterTheGrace()
        {
            Assert.AreEqual(4, WorldEvent.HoldResolvedWaves(5, 4, T0 + 1, T0, 15), "just started: not resolved");
            Assert.AreEqual(5, WorldEvent.HoldResolvedWaves(5, 5, T0 + 1, T0, 15));
            Assert.AreEqual(5, WorldEvent.HoldResolvedWaves(5, 4, T0 + 15, T0, 15), "a batch that never reports back stops blocking after 15 s");
            Assert.AreEqual(0, WorldEvent.HoldResolvedWaves(0, 0, T0, 0, 15));
        }

        [TestMethod]
        public void Run_FinaleStartedButNotPlaced_DoesNotSucceed()
        {
            var goal = WaveHoldGoal(holdSeconds: 480, minSeconds: 0, wavesBase: 5, wavesPerParticipant: 0, wavesCap: 5);
            WorldEvent evt = null;
            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);
            evt = BuildHoldEvent(goal, harness.Objective, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);

            harness.Waves = new HoldWaveCounts(5, 4, 4, 0, 4);
            evt.Tick(T0 + 180);
            Assert.AreEqual(WorldEventState.Active, evt.State, "the 9 ms win from the log no longer happens");

            harness.Waves = new HoldWaveCounts(5, 5, 4, 9, 5);
            evt.Tick(T0 + 181);
            Assert.AreEqual(WorldEventState.Active, evt.State);

            harness.Waves = new HoldWaveCounts(5, 5, 5, 0, 5);
            evt.Tick(T0 + 220);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
        }

        // ---- cadence ----------------------------------------------------------------------------------------

        [TestMethod]
        public void HoldNextWaveDue_IntervalOrMostDead_NeverInsideTheGap()
        {
            Assert.IsFalse(WorldEventHoldRules.HoldNextWaveDue(T0 + 24, T0, 25, 8, 11, 11, 0.25), "the wave is fighting");
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 25, T0, 25, 8, 11, 11, 0.25), "the interval");
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 12, T0, 25, 8, 2, 11, 0.25), "2 of 11 left: most of it is dead");
            Assert.IsFalse(WorldEventHoldRules.HoldNextWaveDue(T0 + 12, T0, 25, 8, 3, 11, 0.25), "3 of 11 left is more than a quarter");
            Assert.IsFalse(WorldEventHoldRules.HoldNextWaveDue(T0 + 5, T0, 25, 8, 0, 11, 0.25), "the field is clear but the gap holds it");
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 8, T0, 25, 8, 0, 11, 0.25));
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 8, T0, 0, 8, 0, 0, 0.25), "no interval: a clear field");
        }

        // ---- guaranteed special waves -----------------------------------------------------------------------

        [TestMethod]
        public void MinSpecialWaves_AtLeastTwo_SpreadOverTheMiddleWaves()
        {
            var types = AllSpecials();

            for (var total = 4; total <= 10; total++)
            {
                for (var seed = 0; seed < 50; seed++)
                {
                    var rng = new Random(seed);
                    var previous = HoldWaveType.Normal;
                    var specials = 0;
                    var picked = new List<HoldWaveType>();

                    for (var wave = 1; wave <= total; wave++)
                    {
                        // Chance 0: only the guarantee makes waves special.
                        var type = WorldEventHoldRules.PickWaveType(wave, total, previous, 0.0, 2, types, rng, null, specials, 2);
                        picked.Add(type);

                        if (type != HoldWaveType.Normal && type != HoldWaveType.Finale)
                            specials++;

                        previous = type;
                    }

                    Assert.AreEqual(2, specials, $"{total} waves, seed {seed}: {string.Join(",", picked)}");
                    Assert.AreEqual(HoldWaveType.Normal, picked[0], "wave 1 is ordinary");
                    Assert.AreEqual(HoldWaveType.Finale, picked[total - 1], "the last is the finale");

                    for (var i = 1; i < picked.Count; i++)
                        Assert.IsFalse(picked[i] != HoldWaveType.Normal && picked[i] == picked[i - 1], "never the same type twice in a row");
                }
            }
        }

        [TestMethod]
        public void MinSpecialWaves_ChanceStillAddsMore_AndTheSlotsAreSpread()
        {
            CollectionAssert.AreEquivalent(new[] { 3, 5 }, WorldEventHoldRules.GuaranteedSpecialSlots(2, 5, 2).ToArray(), "6 waves: specials on 3 and 5");
            CollectionAssert.AreEquivalent(new[] { 4, 8 }, WorldEventHoldRules.GuaranteedSpecialSlots(2, 9, 2).ToArray(), "10 waves: on 4 and 8");
            CollectionAssert.AreEquivalent(new[] { 2 }, WorldEventHoldRules.GuaranteedSpecialSlots(2, 2, 2).ToArray(), "only one middle wave: one special");

            // Catch-up: a run that reached its last middle wave with none yet.
            Assert.IsTrue(WorldEventHoldRules.MustBeSpecial(5, 6, 2, true, 1, 2));
            Assert.IsFalse(WorldEventHoldRules.MustBeSpecial(5, 6, 2, true, 2, 2), "already has two");
            Assert.IsFalse(WorldEventHoldRules.MustBeSpecial(6, 6, 2, true, 0, 2), "never the finale");
            Assert.IsFalse(WorldEventHoldRules.MustBeSpecial(3, 6, 2, true, 0, 0), "0 means no guarantee");

            // With chance 1 every middle wave is special anyway.
            var rng = new Random(3);
            var count = 0;
            var prev = HoldWaveType.Normal;

            for (var wave = 1; wave <= 8; wave++)
            {
                var t = WorldEventHoldRules.PickWaveType(wave, 8, prev, 1.0, 2, AllSpecials(), rng, null, count, 2);

                if (t != HoldWaveType.Normal && t != HoldWaveType.Finale)
                    count++;

                prev = t;
            }

            Assert.AreEqual(6, count);
        }

        // ---- finale boss ------------------------------------------------------------------------------------

        [TestMethod]
        public void FinaleBossFallback_AlwaysFindsABoss()
        {
            // Only elites far above the band and no ordinary members: PickChampion finds nothing.
            var family = new FamilyDef
            {
                Id = "lofty",
                DisplayName = "the Lofty",
                Members = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 501, Name = "Lofty Elite", Level = 260, Role = 1 },
                    new FamilyMember { Wcid = 502, Name = "Lofty Elder", Level = 275, Role = 1 },
                    new FamilyMember { Wcid = 503, Name = "The Named One", Level = 50, Role = WorldEventRosterSelector.NamedBossRole },
                }
            };

            var est = new AudienceEstimate(1, 40, 40);

            Assert.AreEqual(0u, WorldEventRosterSelector.PickChampion(family, est).Wcid, "no champion in band");

            var boss = WorldEventRosterSelector.PickFinaleBossFallback(family, est);

            Assert.AreEqual(501u, boss.Wcid, "the member nearest the band, never the named boss");
            Assert.IsTrue(boss.Synthetic, "promoted: named Champion ..., more health");

            Assert.AreEqual(0u, WorldEventRosterSelector.PickFinaleBossFallback(new FamilyDef { Members = new List<FamilyMember>() }, est).Wcid);
        }

        [TestMethod]
        public void BossEffect_ParsesPlayScriptNamesOrNone()
        {
            Assert.IsTrue(WorldEventHoldRules.TryParseBossEffect("ShieldUpRed", out var red));
            Assert.AreEqual(PlayScript.ShieldUpRed, red);
            Assert.IsTrue(WorldEventHoldRules.TryParseBossEffect("shieldupred", out _), "case-insensitive");
            Assert.IsTrue(WorldEventHoldRules.TryParseBossEffect("none", out var none));
            Assert.AreEqual(PlayScript.Invalid, none);
            Assert.IsTrue(WorldEventHoldRules.TryParseBossEffect(null, out _));
            Assert.IsFalse(WorldEventHoldRules.TryParseBossEffect("Sparkly Glow", out _));
            Assert.IsFalse(WorldEventHoldRules.TryParseBossEffect("NotAScript", out _));
        }

        // ---- runners and ranged -----------------------------------------------------------------------------

        [TestMethod]
        public void RunnerSlice_ScalesWithAttackSpeed()
        {
            var plain = WorldEventHoldRules.ProtectedHitDamage(10000, 2.0, 180);
            var runner = WorldEventHoldRules.ProtectedHitDamage(10000, 2.0, 180, 1.5);

            Assert.AreEqual(plain * 1.5, runner, 2.0);
            Assert.AreEqual(plain, WorldEventHoldRules.ProtectedHitDamage(10000, 2.0, 180, 1.0));
            Assert.AreEqual(plain, WorldEventHoldRules.ProtectedHitDamage(10000, 2.0, 180, double.NaN), "a bad multiplier is ignored");
        }

        [TestMethod]
        public void RangedHold_OnlyForMonstersWithARangedAttack()
        {
            Assert.IsTrue(WorldEventHoldRules.HoldsRange(18, hasMissileWeapon: true, hasSpells: false));
            Assert.IsTrue(WorldEventHoldRules.HoldsRange(18, false, true));
            Assert.IsFalse(WorldEventHoldRules.HoldsRange(18, false, false), "a melee monster closes in");
            Assert.IsFalse(WorldEventHoldRules.HoldsRange(0, true, true), "no hold distance: stock AI");

            Assert.IsTrue(WorldEventHoldRules.RangedHoldShouldApproach(40, 18));
            Assert.IsFalse(WorldEventHoldRules.RangedHoldShouldApproach(19, 18), "inside the slack: stay and shoot");
            Assert.IsFalse(WorldEventHoldRules.RangedHoldShouldApproach(10, 18), "closer than the line: never walks in further");
        }

        [TestMethod]
        public void GoalsJson_NewSettings_Validate()
        {
            void Refused(string replace, string with, string expected)
            {
                var json = SampleProtectJson.Replace(replace, with);
                var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(json, holdSeconds: 480), null, null);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), $"{with} should drop the goal");
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(expected)), string.Join(" | ", store.Diagnostics));
            }

            Refused(@"""radius"": 6.0,", @"""radius"": 6.0, ""waveIntervalSeconds"": -1,", "protect.waveIntervalSeconds");
            Refused(@"""radius"": 6.0,", @"""radius"": 6.0, ""nextWaveAliveShare"": 2,", "protect.nextWaveAliveShare");
            Refused(@"""radius"": 6.0,", @"""radius"": 6.0, ""waveSize"": { ""base"": 0, ""perParticipant"": 1, ""cap"": 5 },", "protect.waveSize");
            Refused(@"""radius"": 6.0,", @"""radius"": 6.0, ""finaleBossScale"": 3,", "protect.finaleBossScale");
            Refused(@"""radius"": 6.0,", @"""radius"": 6.0, ""bossEffect"": ""Glitter"",", "protect.bossEffect");
            Refused(@"""radius"": 6.0,", @"""radius"": 6.0, ""minSpecialWaves"": 11,", "protect.minSpecialWaves");
        }

        // ---- health readouts and phase lines ----------------------------------------------------------------

        [TestMethod]
        public void HealthReadouts_PercentSummaryAndAssessLine()
        {
            Assert.AreEqual(64, WorldEventHoldRules.HealthPercent(6400, 10000));
            Assert.AreEqual(1, WorldEventHoldRules.HealthPercent(1, 10000), "a standing target never reads 0%");
            Assert.AreEqual(0, WorldEventHoldRules.HealthPercent(0, 10000));

            Assert.AreEqual("Sir Draithon 80%, Sir Donovan 64%",
                WorldEventHoldRules.TargetHealthSummary(new[] { ("Sir Draithon", 8000u, 10000u), ("Sir Donovan", 6400u, 10000u), ("Sir Adarl", 0u, 10000u) }),
                "the fallen are left out");

            var line = WorldEventHoldRules.ProtectedHealthLine("Sir Donovan", 6400, 10000);
            Assert.AreEqual("Sir Donovan is at 64% health.", line);
            AssertAscii(line);
        }

        [TestMethod]
        public void WaveStartLine_ShowsEachTargetsHealth()
        {
            var goal = WaveHoldGoal(wavesBase: 6, wavesPerParticipant: 0, wavesCap: 6, minSeconds: 0);
            var objective = new HoldObjective(goal, () => new uint[] { 1, 2, 3 }, () => new uint[] { 1, 2 }, () => T0, null,
                () => "the Drudges", () => default, () => "Sir Draithon 80%, Sir Donovan 64%");

            Assert.AreEqual("Wave 3 of 6 - 2 of 3 wards stand (Sir Draithon 80%, Sir Donovan 64%).", objective.WaveStartText(3));

            var noHealth = new HoldObjective(goal, () => new uint[] { 1 }, () => new uint[] { 1 }, () => T0, null);
            Assert.AreEqual("Wave 1 of 6 - 1 of 1 wards stand.", noHealth.WaveStartText(1));
        }

        [TestMethod]
        public void PhaseLines_BroadcastLabelAndAnnounce()
        {
            Assert.AreEqual("Wave 4 of 7", WorldEventHoldRules.PhaseLabel(4, 7, false));
            Assert.AreEqual("Final wave", WorldEventHoldRules.PhaseLabel(7, 7, true));

            var line = WorldEventAnnouncer.HoldPhaseLine("Wave 4 of 7", "They are massing on the east flank! To me, defenders!");
            StringAssert.EndsWith(line, "Wave 4 of 7: They are massing on the east flank! To me, defenders!");
            StringAssert.StartsWith(line, WorldEventAnnouncer.Global(""));

            // Every shipped announce line, as a broadcast, stays within three sentences and ASCII.
            foreach (var template in EnvoySpecialWaveLines)
            {
                var filled = WorldEventHoldRules.FillLine(WorldEventHoldRules.FillWaveTokens(template, "east", "Champion Drudge Ravener"),
                    "the Drudges", "envoy", "envoys", "Holtburg", 3, 3);
                var broadcast = WorldEventAnnouncer.HoldPhaseLine("Wave 4 of 7", filled);

                AssertAscii(broadcast);
                Assert.IsTrue(SentenceCount(filled) <= 3, filled);
                Assert.IsFalse(broadcast.IndexOf("warp", StringComparison.OrdinalIgnoreCase) >= 0, broadcast);
            }
        }
    }
}
