using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-10-18 evening round, from a real solo run's log: the rescue heal that threw on every kill, ranged
    /// monsters that stood still (melee monsters picked as ranged, and the hold cancelling its own turn), ranged
    /// borrowed from a family of the same theme, the corrupted assault, the shrine's model and cue, and effects that
    /// could not show on bodies whose effect table lacks them. Shares the builders in WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        // ---- the rescue heal ------------------------------------------------------------------------------------

        [TestMethod]
        public void Rescue_KillerThatIsNotAPet_HasNoPetOwner_InsteadOfThrowing()
        {
            // Every monster death in the 2026-10-18 run logged "hold monster death heal threw" from TryGetPetOwner:
            // the owner reference is only made for pets, and a player's kill had none.
            var killer = new DamageHistoryInfo(BuildHoldCreature());

            Assert.IsNull(killer.PetOwner);
            Assert.IsNull(killer.TryGetPetOwner(), "no pet, no owner, no exception");
            Assert.AreSame(killer, DamageHistoryInfo.ResolvePetOwnerAsKiller(killer));
        }

        // ---- ranged: only an attack the monster actually uses ------------------------------------------------

        // Spell ids and spellbook chances copied from the world database for monsters drawn in that run.
        private const int AcidStreamVI = 63, LightningBoltVI = 80, LightningBoltV = 79, LightningBlastV = 113, HarmOther = 4308;
        private const int VulnOtherVI = 234, HealSelfVI = 1161, DrainHealthVI = 1242, WeaknessV = 1342, ImperilOther = 4312;

        private static readonly HashSet<int> OffensiveFixture = new HashSet<int> { AcidStreamVI, LightningBoltVI, LightningBoltV, LightningBlastV, HarmOther };

        private static bool IsOffensiveFixture(uint spellId) => OffensiveFixture.Contains((int)spellId);

        /// <summary>Grievver Shredder (44476): two 3% bolts in a book of debuffs. Melee, stood still in a ranged slot.</summary>
        private static Dictionary<int, float> ShredderBook() => new Dictionary<int, float>
        {
            { AcidStreamVI, 2.03f }, { LightningBoltVI, 2.03f }, { VulnOtherVI, 2.02f }, { HealSelfVI, 2.01f }, { DrainHealthVI, 2.01f }, { WeaknessV, 2.02f }
        };

        /// <summary>Burrowing Grievver (31235): two 5% bolts. Still melee.</summary>
        private static Dictionary<int, float> BurrowerBook() => new Dictionary<int, float>
        {
            { LightningBoltV, 2.05f }, { LightningBlastV, 2.05f }, { WeaknessV, 2.06f }
        };

        /// <summary>Infectious Rat (49586): a 25% Harm. A real caster.</summary>
        private static Dictionary<int, float> InfectiousRatBook() => new Dictionary<int, float>
        {
            { HarmOther, 2.25f }, { ImperilOther, 2.2f }
        };

        [TestMethod]
        public void SpellChance_ReadsTheBookAsTheMonsterAiDoes()
        {
            Assert.AreEqual(0.03, WorldEventRangedMembers.SpellChance(2.03f), 1e-5, "2 plus the chance");
            Assert.AreEqual(0.25, WorldEventRangedMembers.SpellChance(2.25f), 1e-5);
            Assert.AreEqual(0.02, WorldEventRangedMembers.SpellChance(2.0f), 1e-5, "a bare 2.0 is a 2% chance");
            Assert.AreEqual(0, WorldEventRangedMembers.SpellChance(0f));
            Assert.AreEqual(0, WorldEventRangedMembers.SpellChance(float.NaN));
        }

        [TestMethod]
        public void OffensiveCastChance_MeleeGrievversAreNotCasters_TheInfectiousRatIs()
        {
            var shredder = WorldEventRangedMembers.OffensiveCastChance(ShredderBook(), IsOffensiveFixture);
            var burrower = WorldEventRangedMembers.OffensiveCastChance(BurrowerBook(), IsOffensiveFixture);
            var rat = WorldEventRangedMembers.OffensiveCastChance(InfectiousRatBook(), IsOffensiveFixture);

            Assert.AreEqual(1 - 0.97 * 0.97, shredder, 1e-5, "only the two bolts count, not the debuffs, heal or drain");
            Assert.IsFalse(WorldEventRangedMembers.HasRealRangedAttack(false, false, false, shredder), "Grievver Shredder: melee");
            Assert.IsFalse(WorldEventRangedMembers.HasRealRangedAttack(false, false, false, burrower), "Burrowing Grievver: melee");
            Assert.IsTrue(WorldEventRangedMembers.HasRealRangedAttack(false, false, false, rat), "Infectious Rat: a caster");
            Assert.AreEqual(0, WorldEventRangedMembers.OffensiveCastChance(new Dictionary<int, float> { { VulnOtherVI, 2.5f } }, IsOffensiveFixture), "debuffs alone");
        }

        [TestMethod]
        public void PickOffensiveSpell_NeverABuffOrDebuff_WeightedByChance()
        {
            var book = new Dictionary<int, float> { { VulnOtherVI, 2.9f }, { LightningBoltV, 2.30f }, { AcidStreamVI, 2.10f }, { HealSelfVI, 2.5f } };

            var picks = Enumerable.Range(0, 100).Select(i => WorldEventRangedMembers.PickOffensiveSpell(book, IsOffensiveFixture, i / 100.0)).ToList();

            Assert.IsTrue(picks.All(id => id == LightningBoltV || id == AcidStreamVI), "only the offensive spells");
            Assert.IsTrue(Math.Abs(picks.Count(id => id == LightningBoltV) - 75) <= 1, "30% against 10%: three quarters");
            Assert.AreEqual(0, WorldEventRangedMembers.PickOffensiveSpell(new Dictionary<int, float> { { VulnOtherVI, 2.9f } }, IsOffensiveFixture, 0.5));
            Assert.AreEqual(0, WorldEventRangedMembers.PickOffensiveSpell(null, IsOffensiveFixture, 0.5));
        }

        [TestMethod]
        public void AmmoFits_ArrowsForABow_NotBoltsForABow()
        {
            const int Arrow = 1, Bolt = 2, Atlatl = 4;

            Assert.IsTrue(WorldEventRangedMembers.AmmoFits(Arrow, Arrow));
            Assert.IsFalse(WorldEventRangedMembers.AmmoFits(Arrow, Bolt), "a bow with crossbow bolts does not fire");
            Assert.IsFalse(WorldEventRangedMembers.AmmoFits(Atlatl, Arrow));
            Assert.IsTrue(WorldEventRangedMembers.AmmoFits(null, Bolt), "no ammo type on the launcher: fits, as the stock AI would fire it");
        }

        private const uint BowWcid = 900001, ArrowWcid = 900002, BoltWcid = 900003, AxeWcid = 900004;

        private static Weenie ItemWeenie(uint wcid)
        {
            switch (wcid)
            {
                case BowWcid: return new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.MissileLauncher, PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.AmmoType, 1 } } };
                case ArrowWcid: return new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Ammunition, PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.AmmoType, 1 } } };
                case BoltWcid: return new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Ammunition, PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.AmmoType, 2 } } };
                case AxeWcid: return new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Missile };
                default: return null;
            }
        }

        private static Weenie MonsterWeenie(uint wcid, Dictionary<int, float> book, params (uint Wcid, DestinationType Where)[] createList)
        {
            return new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Creature,
                PropertiesSpellBook = book,
                PropertiesCreateList = createList.Select(c => new PropertiesCreateList { WeenieClassId = c.Wcid, DestinationType = c.Where }).ToList()
            };
        }

        [TestMethod]
        public void WeenieRule_LauncherNeedsFittingAmmo_ThrownIsEnough_CastersNeedRealChances()
        {
            bool Ranged(Weenie w) => WorldEventRangedMembers.WeenieHasRealRangedAttack(w, ItemWeenie, IsOffensiveFixture);

            Assert.IsTrue(Ranged(MonsterWeenie(1, null, (BowWcid, DestinationType.Wield), (ArrowWcid, DestinationType.Wield))), "bow and arrows");
            Assert.IsFalse(Ranged(MonsterWeenie(2, null, (BowWcid, DestinationType.Wield), (BoltWcid, DestinationType.Wield))), "bow and bolts");
            Assert.IsFalse(Ranged(MonsterWeenie(3, null, (BowWcid, DestinationType.Wield))), "bow, no ammo");
            Assert.IsFalse(Ranged(MonsterWeenie(4, null, (BowWcid, DestinationType.Contain), (ArrowWcid, DestinationType.Wield))), "a bow in its pack is not wielded");
            Assert.IsTrue(Ranged(MonsterWeenie(5, null, (AxeWcid, DestinationType.Wield))), "throwing axes");
            Assert.IsFalse(Ranged(MonsterWeenie(44476, ShredderBook())), "Grievver Shredder");
            Assert.IsTrue(Ranged(MonsterWeenie(49586, InfectiousRatBook())), "Infectious Rat");
            Assert.IsFalse(Ranged(null));
        }

        // ---- every ranged pick has a working ranged attack ------------------------------------------------------

        /// <summary>A catalog with the real books of the run's monsters, plus a wasp caster, a Virindi caster and an archer.</summary>
        private static (Dictionary<string, IReadOnlyList<FamilyMember>> Catalog, Dictionary<uint, Weenie> Weenies) RangedFixtureCatalog()
        {
            var weenies = new Dictionary<uint, Weenie>
            {
                [44476] = MonsterWeenie(44476, ShredderBook()),
                [31235] = MonsterWeenie(31235, BurrowerBook()),
                [46408] = MonsterWeenie(46408, null),                                     // Carrion Rat: no spells
                [49586] = MonsterWeenie(49586, InfectiousRatBook()),
                [800001] = MonsterWeenie(800001, new Dictionary<int, float> { { AcidStreamVI, 2.3f } }),  // a wasp caster
                [800002] = MonsterWeenie(800002, new Dictionary<int, float> { { LightningBoltVI, 2.4f } }),// a Virindi caster
                [800003] = MonsterWeenie(800003, null, (BowWcid, DestinationType.Wield), (ArrowWcid, DestinationType.Wield)), // a tumerok archer
                [800004] = MonsterWeenie(800004, null, (BowWcid, DestinationType.Wield), (BoltWcid, DestinationType.Wield)),  // a bow with bolts
            };

            FamilyMember M(uint wcid, string family, int level) => new FamilyMember { Wcid = wcid, FamilyId = family, Level = level, Name = $"m{wcid}" };

            var catalog = new Dictionary<string, IReadOnlyList<FamilyMember>>
            {
                ["grievver"] = new List<FamilyMember> { M(44476, "grievver", 180), M(31235, "grievver", 150) },
                ["rat"] = new List<FamilyMember> { M(46408, "rat", 160), M(49586, "rat", 190) },
                ["phyntos_wasp"] = new List<FamilyMember> { M(800001, "phyntos_wasp", 170) },
                ["virindi"] = new List<FamilyMember> { M(800002, "virindi", 170) },
                ["tumerok"] = new List<FamilyMember> { M(800003, "tumerok", 170), M(800004, "tumerok", 170) },
            };

            return (catalog, weenies);
        }

        /// <summary>
        /// What the hold can do with a picked monster (pure over its weenie): fire a launcher with fitting ammo or a thrown
        /// weapon, or cast one of its offensive spells, whichever spell the pick rolls.
        /// </summary>
        private static bool HoldHasAWorkingAttack(Weenie w)
        {
            var launcher = w.PropertiesCreateList?.Select(c => ItemWeenie(c.WeenieClassId)).FirstOrDefault(i => i?.WeenieType == WeenieType.MissileLauncher);
            var ammo = w.PropertiesCreateList?.Select(c => ItemWeenie(c.WeenieClassId)).Where(i => i?.WeenieType == WeenieType.Ammunition).ToList();

            if (launcher != null && ammo != null && ammo.Any(a => WorldEventRangedMembers.AmmoFits(launcher.GetProperty(PropertyInt.AmmoType), a.GetProperty(PropertyInt.AmmoType))))
                return true;

            if (w.PropertiesCreateList?.Any(c => ItemWeenie(c.WeenieClassId)?.WeenieType == WeenieType.Missile) == true)
                return true;

            // Every roll of the hold's pick lands on an offensive spell.
            return Enumerable.Range(0, 50).Select(i => WorldEventRangedMembers.PickOffensiveSpell(w.PropertiesSpellBook, IsOffensiveFixture, i / 50.0))
                .All(id => id != 0 && IsOffensiveFixture((uint)id));
        }

        [TestMethod]
        public void EveryRangedPick_HasAWorkingRangedAttack()
        {
            var (catalog, weenies) = RangedFixtureCatalog();
            bool IsRanged(FamilyMember m) => WorldEventRangedMembers.WeenieHasRealRangedAttack(weenies[m.Wcid], ItemWeenie, IsOffensiveFixture);

            var picks = new List<uint>();

            foreach (var band in new[] { new LevelBand(150, 225), new LevelBand(100, 140), new LevelBand(1, 20) })
            {
                foreach (var roster in new[] { new[] { "grievver", "rat" }, new[] { "grievver" }, new[] { "rat" }, new[] { "tumerok" } })
                {
                    var members = roster.SelectMany(f => catalog[f]).ToList();
                    var pool = WorldEventRangedMembers.Pool(members, band, IsRanged);

                    if (pool.Count == 0)
                        pool = WorldEventRangedMembers.CompatiblePool(roster, catalog, band, out _, IsRanged, new Random(1));

                    picks.AddRange(pool);
                }
            }

            Assert.IsTrue(picks.Count > 0);

            foreach (var wcid in picks.Distinct())
            {
                Assert.IsTrue(IsRanged(new FamilyMember { Wcid = wcid }), $"wcid {wcid} passes the rule");
                Assert.IsTrue(HoldHasAWorkingAttack(weenies[wcid]), $"wcid {wcid}: the hold has an attack it will actually use");
            }

            CollectionAssert.DoesNotContain(picks, 44476u, "Grievver Shredder never stands in a ranged slot again");
            CollectionAssert.DoesNotContain(picks, 31235u, "nor the Burrowing Grievver");
            CollectionAssert.DoesNotContain(picks, 800004u, "nor a bow with the wrong ammo");
        }

        [TestMethod]
        public void RangedPool_TheRunsFamilies_OnlyTheInfectiousRat()
        {
            var (catalog, weenies) = RangedFixtureCatalog();
            bool IsRanged(FamilyMember m) => WorldEventRangedMembers.WeenieHasRealRangedAttack(weenies[m.Wcid], ItemWeenie, IsOffensiveFixture);

            var members = catalog["grievver"].Concat(catalog["rat"]);

            CollectionAssert.AreEqual(new[] { 49586u }, WorldEventRangedMembers.Pool(members, new LevelBand(150, 225), IsRanged));
        }

        [TestMethod]
        public void CompatiblePool_GrievversBorrowFromWasps_NeverFromTheVirindi()
        {
            var (catalog, weenies) = RangedFixtureCatalog();
            bool IsRanged(FamilyMember m) => WorldEventRangedMembers.WeenieHasRealRangedAttack(weenies[m.Wcid], ItemWeenie, IsOffensiveFixture);

            Assert.AreEqual(0, WorldEventRangedMembers.Pool(catalog["grievver"], new LevelBand(150, 225), IsRanged).Count, "no grievver is ranged");

            for (var seed = 0; seed < 20; seed++)
            {
                var pool = WorldEventRangedMembers.CompatiblePool(new[] { "grievver" }, catalog, new LevelBand(150, 225), out var from, IsRanged, new Random(seed));

                Assert.IsTrue(from == "phyntos_wasp" || from == "rat", $"a wild family, not {from}");
                Assert.IsFalse(pool.Contains(800002u), "never a Virindi");
                Assert.IsFalse(pool.Contains(800003u), "never a tumerok archer beside beasts");
            }

            // Out of band everywhere: the nearest few, still only from the same theme.
            var far = WorldEventRangedMembers.CompatiblePool(new[] { "grievver" }, catalog, new LevelBand(1, 20), out var farFrom, IsRanged, new Random(1));
            Assert.IsTrue(far.Count > 0 && far.All(w => w == 800001u || w == 49586u), farFrom);

            // A family with no theme borrows from nobody.
            Assert.AreEqual(0, WorldEventRangedMembers.CompatiblePool(new[] { "testclone" }, catalog, new LevelBand(150, 225), out var none, IsRanged).Count);
            Assert.IsNull(none);
        }

        [TestMethod]
        public void FamilyThemes_SameThemeOnly()
        {
            Assert.IsTrue(WorldEventFamilyThemes.Compatible("rat", "grievver"));
            Assert.IsTrue(WorldEventFamilyThemes.Compatible("grievver", "phyntos_wasp"));
            Assert.IsTrue(WorldEventFamilyThemes.Compatible("slithis", "remoran"), "slithis are wild and of the shore");
            Assert.IsFalse(WorldEventFamilyThemes.Compatible("grievver", "virindi"));
            Assert.IsFalse(WorldEventFamilyThemes.Compatible("skeleton", "drudge"));
            Assert.IsFalse(WorldEventFamilyThemes.Compatible("rat", "rat"), "a family does not borrow from itself");
            Assert.IsFalse(WorldEventFamilyThemes.Compatible("testclone", "rat"));
            Assert.AreEqual(0, WorldEventFamilyThemes.ThemesOf(null).Count);
        }

        [TestMethod]
        public void FamilyThemes_CoverTheShippedFamilies_WhenPresent()
        {
            var dir = FindContentDirectory(System.IO.Path.Combine("ace-content", "events", "axes", "species"));

            if (dir == null)
                Assert.Inconclusive("ace-content species tables not found next to the repo");

            var ids = System.IO.Directory.GetFiles(dir, "*.json").Select(System.IO.Path.GetFileNameWithoutExtension).ToList();

            foreach (var id in ids)
                Assert.IsTrue(WorldEventFamilyThemes.ThemesOf(id).Count > 0, $"species table {id} has a theme");

            foreach (var known in WorldEventFamilyThemes.KnownFamilies)
                Assert.IsTrue(ids.Contains(known) || known == "sleech", $"theme entry {known} names a real family");
        }

        private static string FindContentDirectory(string relative)
        {
            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var path = System.IO.Path.Combine(dir.FullName, relative);

                if (System.IO.Directory.Exists(path))
                    return path;
            }

            return null;
        }

        // ---- the corrupted assault ------------------------------------------------------------------------------

        [TestMethod]
        public void AssaultWaves_EveryOtherWave_FromTheSecond_NeverTheFinale()
        {
            var assaults = Enumerable.Range(1, 7).Where(w => WorldEventHoldRules.IsAssaultWave(w, 2, 2, w == 7)).ToList();

            CollectionAssert.AreEqual(new[] { 2, 4, 6 }, assaults);
            Assert.IsFalse(WorldEventHoldRules.IsAssaultWave(2, 0, 2, false), "off");
            CollectionAssert.AreEqual(new[] { 1, 4, 7 }, Enumerable.Range(1, 9).Where(w => WorldEventHoldRules.IsAssaultWave(w, 3, 1, false)).ToList());
        }

        [TestMethod]
        public void AssaultWave_TwoCorruptedOnTop_TheWaveItselfUnchanged()
        {
            var mix = new WaveMixDef { MinBreach = 2 };
            var off = WorldEventHoldRules.AssaultMix(mix.Shares, mix.Minimums, mix.Maximums, true);

            foreach (var baseSize in new[] { 4, 6, 9, 14, 26 })
            {
                var plainCounts = WorldEventHoldRules.WaveMixCounts(baseSize, off.Shares, off.Minimums, off.Maximums);
                Assert.AreEqual(0, plainCounts[(int)HoldRole.Corrupted], $"an ordinary wave of {baseSize}: no corrupted in assault mode");

                for (var seed = 0; seed < 10; seed++)
                {
                    var keep = new HashSet<int> { 0 };   // an elite in slot 0
                    var roles = WorldEventHoldRules.AssignAssaultWave(baseSize + 2, 2, mix.Shares, mix.Minimums, mix.Maximums, keep, new Random(seed));

                    Assert.AreEqual(2, roles.Count(r => r == HoldRole.Corrupted), $"assault wave of {baseSize}+2: two corrupted");
                    Assert.AreNotEqual(HoldRole.Corrupted, roles[0], "never the elite's slot");
                    Assert.AreNotEqual(HoldRole.Ranged, roles[0], "the elite keeps its own monster");
                    Assert.IsTrue(roles.Count(r => r == HoldRole.Breach) >= 2, $"assault wave of {baseSize}+2 keeps its breach squad");

                    for (var r = 0; r < plainCounts.Length; r++)
                        if (r != (int)HoldRole.Corrupted)
                            Assert.AreEqual(plainCounts[r], roles.Count(x => (int)x == r), $"wave of {baseSize}: role {(HoldRole)r} as in an ordinary wave");
                }
            }

            // Assaults off: the waveMix shares stand as they are.
            var plain = WorldEventHoldRules.AssaultMix(mix.Shares, mix.Minimums, mix.Maximums, false);
            CollectionAssert.AreEqual(mix.Shares, plain.Shares);
            CollectionAssert.AreEqual(mix.Minimums, plain.Minimums);
        }

        [TestMethod]
        public void AssaultTargets_DifferentKnights_WhileEnoughStand()
        {
            for (var seed = 0; seed < 30; seed++)
            {
                var two = WorldEventHoldRules.AssaultTargets(3, 2, new Random(seed));
                Assert.AreEqual(2, two.Count);
                Assert.AreNotEqual(two[0], two[1], "two corrupted, two knights");
                Assert.IsTrue(two.All(i => i >= 0 && i < 3));
            }

            CollectionAssert.AreEqual(new[] { 0, 0 }, WorldEventHoldRules.AssaultTargets(1, 2, new Random(1)), "one knight left: both on it");
            Assert.AreEqual(0, WorldEventHoldRules.AssaultTargets(0, 2, new Random(1)).Count);
        }

        [TestMethod]
        public void AssaultLine_NamesTheMonsterAndItsKnight()
        {
            Assert.AreEqual("A Corrupted Grievver Shredder marches on Sir Draithon!",
                WorldEventHoldRules.AssaultLine(null, "Corrupted Grievver Shredder", "Sir Draithon"));
            Assert.AreEqual("Sir Adarl is the mark of a Corrupted Rat!",
                WorldEventHoldRules.AssaultLine("{target} is the mark of a {monster}!", "Corrupted Rat", "Sir Adarl"));
            AssertAscii(WorldEventAnnouncer.Global(WorldEventHoldRules.AssaultLine(null, "Corrupted X", "Sir Y")));
        }

        [TestMethod]
        public void AssaultMonster_HitsCountAgainstTheDeathCap()
        {
            // The live run of 2026-10-19: Sir Draithon died 26 s after wave 2 brought a corrupted assault monster (selfMult 3)
            // onto him beside two breachers, against a 60 s floor, because the assault monster's hits skipped the cap.
            double SecondsToKill(bool assaultMode)
            {
                var (evt, protect) = BuildTimeScaledRun();
                protect.WaveMix = new WaveMixDef();
                protect.Corrupted = new CorruptedDef { AssaultEveryWaves = assaultMode ? 2 : 0, SelfMult = 3.0, EmpowerMult = 1.5, EmpowerRadius = 50 };

                var target = BuildFreshTarget(evt);
                var crowd = Enumerable.Range(0, 6).Select(_ => NewRunMonster(evt)).ToList();
                var corrupted = NewRunMonster(evt);
                corrupted.WorldEventCorrupted = true;

                for (var t = 0.0; t <= 600; t += 2)
                {
                    foreach (var m in crowd)
                        target.UpdateVitalDelta(target.Health, -(int)target.ScaleWorldEventProtectedHit(m, 50f, T0 + t + 0.5));

                    target.UpdateVitalDelta(target.Health, -(int)target.ScaleWorldEventProtectedHit(corrupted, 50f, T0 + t + 0.6));

                    if (target.Health.Current == 0)
                        return t + 0.6;
                }

                return double.PositiveInfinity;
            }

            foreach (var assault in new[] { true, false })
            {
                var seconds = SecondsToKill(assault);

                // The bucket starts full (DefaultCapBurstSeconds), so the floor is minDeathSeconds less that burst.
                Assert.IsTrue(seconds >= 60 - WorldEventHoldRules.DefaultCapBurstSeconds - 1,
                    $"assault={assault}: a knight under a crowd and a corrupted monster lasts the 60 s floor (died in {seconds:0.0} s)");
            }
        }

        [TestMethod]
        public void HoldRole_AssaultMonster_LockedOnItsKnight()
        {
            var plan = new HoldWavePlan { Type = HoldWaveType.Mixed, BreachTargetGuid = 0x80000001, Assault = true, CorruptedTargets = new Dictionary<int, uint> { [3] = 0x80000002 } };

            var marching = BuildHoldCreature();
            WorldEventSpawner.ApplyHoldRole(marching, HoldRole.Corrupted, plan, 3);

            Assert.IsTrue(marching.WorldEventCorrupted);
            Assert.IsTrue(marching.WorldEventSeeker);
            Assert.AreEqual(0x80000002u, marching.WorldEventSeekForcedGuid, "goes only for its knight");

            var plain = BuildHoldCreature();
            WorldEventSpawner.ApplyHoldRole(plain, HoldRole.Corrupted, plan, 4);
            Assert.AreEqual(0u, plain.WorldEventSeekForcedGuid, "no knight named for this slot: it spreads as before");
            Assert.AreEqual(0x80000001u, plain.WorldEventSeekAvoidGuid);
        }

        [TestMethod]
        public void GoalsJson_AssaultAndShrineCue_Validate()
        {
            void Refused(string fragment, string expected)
            {
                var json = SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, " + fragment);
                var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(json, holdSeconds: 480), null, null);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), fragment);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(expected)), $"{fragment}: {string.Join(" | ", store.Diagnostics)}");
            }

            Refused(@"""corrupted"": { ""assaultEveryWaves"": -1 },", "protect.corrupted assault");
            Refused(@"""corrupted"": { ""assaultEveryWaves"": 2, ""assaultCount"": 0 },", "protect.corrupted assault");
            Refused(@"""corrupted"": { ""selfMult"": 11 },", "protect.corrupted aura");
            Refused(@"""shrine"": { ""readyPulseSeconds"": 2 },", "protect.shrine.readyPulseSeconds");

            var ok = SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""corrupted"": { ""selfMult"": 6, ""assaultEveryWaves"": 2, ""assaultCount"": 2, " +
                @"""assaultAnnounce"": [ ""A {monster} marches on {target}!"", "" "" ] }, ""shrine"": { ""readyPulseSeconds"": 20 },");
            var good = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(ok, holdSeconds: 480), null, null);

            Assert.IsTrue(good.Goals.ContainsKey("hold_wards"), string.Join(" | ", good.Diagnostics));
            var p = good.Goals["hold_wards"].Protect;
            Assert.IsTrue(p.Corrupted.AssaultMode);
            Assert.AreEqual(1, p.Corrupted.AssaultAnnounce.Count, "the blank line is dropped");
            Assert.AreEqual(20, p.Shrine.ReadyPulseSeconds, 1e-9);
        }

        [TestMethod]
        public void HoldWavePlan_LogNamesTheAssault()
        {
            var plan = new HoldWavePlan { Type = HoldWaveType.Mixed, Roles = new[] { HoldRole.Corrupted }, Assault = true,
                AssaultCalls = new List<(string, string)> { ("Corrupted Rat", "Sir Adarl") } };

            StringAssert.Contains(plan.ToString(), "assault=[Corrupted Rat -> Sir Adarl]");
            Assert.IsFalse(new HoldWavePlan { Type = HoldWaveType.Mixed, Roles = new HoldRole[0] }.ToString().Contains("assault="));
        }

        // ---- the shrine ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void ShrineDefaults_TheDreamWeaveShrine_NoConstantEffect()
        {
            var s = new ShrineDef();

            Assert.AreEqual(2000008u, s.Wcid, "the DreamWeave Restoration Shrine, not a retail altar");
            Assert.AreEqual(1.0, s.Scale, 1e-9, "normal size");
            Assert.AreEqual(0, s.ReadyPulseSeconds, 1e-9, "the ready cue plays once, never on a loop");
            Assert.AreNotEqual("RestrictionEffectGreen", s.ReadyEffect);
        }

        [TestMethod]
        public void ShrineReadyCue_OnceWhenReady_UnlessARepeatIsSet()
        {
            // Never used (readyAt 0): once at placement, then quiet.
            Assert.IsTrue(WorldEventHoldRules.ShrineReadyPulseDue(T0, 0, 0, 0));
            Assert.IsFalse(WorldEventHoldRules.ShrineReadyPulseDue(T0 + 30, 0, T0, 0), "no repeat");

            // Used, resting, then ready again: once more when its rest ends.
            Assert.IsFalse(WorldEventHoldRules.ShrineReadyPulseDue(T0 + 50, T0 + 90, T0, 0), "resting");
            Assert.IsTrue(WorldEventHoldRules.ShrineReadyPulseDue(T0 + 90, T0 + 90, T0, 0), "ready again");
            Assert.IsFalse(WorldEventHoldRules.ShrineReadyPulseDue(T0 + 120, T0 + 90, T0 + 90, 0));

            // With a repeat (the envoys use 20 s): a small cue every 20 s while ready.
            Assert.IsFalse(WorldEventHoldRules.ShrineReadyPulseDue(T0 + 10, 0, T0, 20));
            Assert.IsTrue(WorldEventHoldRules.ShrineReadyPulseDue(T0 + 20, 0, T0, 20));
        }

        // ---- effects that can show ------------------------------------------------------------------------------

        [TestMethod]
        public void EffectTables_FallbackOnlyWhenTheOwnTableCannotPlayIt()
        {
            // Tables as the dats have them: the grievver table (0x34000084) lacks EnchantUpPurple and has VitaeDownBlack;
            // the Penumbral table (0x34000043) is empty; the knights and the altar have none.
            bool? Has(uint table, PlayScript s)
            {
                switch (table)
                {
                    case 0x34000084: return s == PlayScript.VitaeDownBlack || s == PlayScript.ShieldUpPurple;
                    case 0x34000043: return false;
                    case WorldEventEffectTables.CreatureTable: return s != PlayScript.EnchantUpPurple;
                    case WorldEventEffectTables.HumanTable: return true;
                    default: return null;
                }
            }

            Assert.IsFalse(WorldEventEffectTables.NeedsFallback(0x34000084, WorldEventEffectTables.CreatureTable, PlayScript.VitaeDownBlack, Has), "its own table has it");
            Assert.IsTrue(WorldEventEffectTables.NeedsFallback(0x34000043, WorldEventEffectTables.CreatureTable, PlayScript.VitaeDownBlack, Has), "an empty table");
            Assert.IsTrue(WorldEventEffectTables.NeedsFallback(0, WorldEventEffectTables.HumanTable, PlayScript.HealthUpRed, Has), "no table at all (the knights)");
            Assert.IsFalse(WorldEventEffectTables.NeedsFallback(0x34000084, WorldEventEffectTables.CreatureTable, PlayScript.EnchantUpPurple, Has), "the fallback cannot either");
            Assert.IsFalse(WorldEventEffectTables.NeedsFallback(0x34009999, WorldEventEffectTables.CreatureTable, PlayScript.VitaeDownBlack, (t, s) => null), "dats not loaded: change nothing");
        }

        [TestMethod]
        public void EffectTables_EnsureCanPlay_GivesTheShrineATable()
        {
            var old = WorldEventEffectTables.TableHasLookup;

            try
            {
                WorldEventEffectTables.ClearCache();
                WorldEventEffectTables.TableHasLookup = (t, s) => t == WorldEventEffectTables.CreatureTable;

                var altar = BuildHoldCreature();
                altar.PhysicsTableId = 0;

                Assert.IsTrue(WorldEventEffectTables.EnsureCanPlay(altar, "SkillUpGreen", WorldEventEffectTables.CreatureTable));
                Assert.AreEqual(WorldEventEffectTables.CreatureTable, altar.PhysicsTableId);
                Assert.IsFalse(WorldEventEffectTables.EnsureCanPlay(altar, "none", WorldEventEffectTables.CreatureTable), "none plays nothing");
            }
            finally
            {
                WorldEventEffectTables.TableHasLookup = old;
                WorldEventEffectTables.ClearCache();
            }
        }
    }
}
