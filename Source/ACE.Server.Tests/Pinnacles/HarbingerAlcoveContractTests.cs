using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The round 2 alcove contract (A5 the crystal's ward, A6 its scripted casts, B1 the ward link and the attunement, A7 the
    /// health multipliers in Data) against the recording fake port. The port applies nothing: a test delivers every AddDied and
    /// TimerFired itself. Reads no PropertyManager value (the script takes its settings record).
    /// </summary>
    [TestClass]
    public class HarbingerAlcoveContractTests
    {
        private static readonly HarbingerSettings S = HarbingerSettings.Defaults;

        // ================================================================ harness

        private static (HarbingerScript S, FakePinnacleFightPort P) Started(HarbingerSettings settings = null)
        {
            var p = new FakePinnacleFightPort(1);
            // round 4: these tests are about the crystal's cast cadence and the ward link, not the arrival grace (HarbingerSignatureTests pins that)
            var s = new HarbingerScript(settings ?? S with { AlcoveGraceSeconds = 0 });
            s.OnStart(p);
            s.EnterPhase(1, p);
            return (s, p);
        }

        private static void Fire(HarbingerScript s, FakePinnacleFightPort p, string tag)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.TimerFired, Tag = tag }, p);

        private static void Died(HarbingerScript s, FakePinnacleFightPort p, string tag, PinnacleHandle h)
        {
            p.Kill(h);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = tag, Handle = h }, p);
        }

        private static void SpawnFailed(HarbingerScript s, FakePinnacleFightPort p, string tag)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = tag, Handle = PinnacleHandle.None, SpawnFailed = true }, p);

        private static PinnaclePoint Pt(float x, float y, float z) => new PinnaclePoint(0u, x, y, z);

        /// <summary>The phase of <paramref name="e"/> at its threshold: the alcove is up.</summary>
        private static void ToAlcove(HarbingerScript s, FakePinnacleFightPort p, HarbingerElement e)
        {
            if (p.Phase != (int)e + 1)
            {
                p.Phase = (int)e + 1;
                s.EnterPhase(p.Phase, p);
            }

            p.AtFloor.Add(p.Head);
            Fire(s, p, HarbingerScript.TimerPoll);
            p.AtFloor.Remove(p.Head);
        }

        private static PinnacleHandle Crystal(FakePinnacleFightPort p, HarbingerElementDef def) => p.Spawns.Last(x => x.Tag == def.CrystalTag).Result;

        private static PinnacleHandle[] Guards(FakePinnacleFightPort p, HarbingerElementDef def) => p.Spawns.Where(x => x.Tag == def.GuardTag).Select(x => x.Result).TakeLast(2).ToArray();

        /// <summary>The owner stands in the middle of <paramref name="e"/>'s alcove, and the poll sees the arrival.</summary>
        private static void Arrive(HarbingerScript s, FakePinnacleFightPort p, HarbingerElement e)
        {
            var c = HarbingerData.AlcoveOf(e).Centre;
            p.OwnerAt = Pt(c.X, c.Y, 0f);
            Fire(s, p, HarbingerScript.TimerPoll);
        }

        /// <summary>The fake's read of "can this crystal be damaged": not immune, and no health floor.</summary>
        private static bool Damageable(FakePinnacleFightPort p, PinnacleHandle h)
            => !p.Immune.GetValueOrDefault(h, false) && p.Floors.GetValueOrDefault(h, null) == null;

        private static IEnumerable<(PinnacleHandle Caster, uint SpellId)> CastsBy(FakePinnacleFightPort p, PinnacleHandle caster) => p.NowCasts.Where(c => c.Caster == caster);

        private static double LastInterval(FakePinnacleFightPort p) => p.TimerSets.Last(t => t.Tag == HarbingerScript.TimerCrystalCast).Seconds;

        // ================================================================ A5: the ward

        [TestMethod]
        public void Crystal_SpawnsShielded_ImmuneAtAFullFloor_NeverNonAttackable()
        {
            foreach (var def in HarbingerData.Elements)
            {
                var (s, p) = Started();
                ToAlcove(s, p, def.Element);
                var crystal = Crystal(p, def);

                Assert.IsTrue(p.Immune[crystal], def.Tag);
                Assert.AreEqual(1.0, p.Floors[crystal], def.Tag);
                Assert.IsFalse(p.Attackable.ContainsKey(crystal), $"{def.Tag}: SetAttackable(false) would make the crystal an NPC");
                Assert.IsFalse(Damageable(p, crystal), def.Tag);
            }
        }

        [TestMethod]
        public void Crystal_NotDamageable_UntilBothGuardsDie_ThenTheWardBreaksOnce()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Frost);
            ToAlcove(s, p, def.Element);
            var crystal = Crystal(p, def);
            var guards = Guards(p, def);

            Died(s, p, def.GuardTag, guards[0]);
            Assert.IsFalse(Damageable(p, crystal), "one guard still holds the ward");
            CollectionAssert.DoesNotContain(p.Said, HarbingerLines.LineWardBroken);

            p.ClearRecordings();
            Died(s, p, def.GuardTag, guards[1]);

            Assert.IsTrue(Damageable(p, crystal), "both guards down");
            Assert.AreEqual(HarbingerLines.LineWardBroken, p.Said.Single());
            Assert.AreEqual(1, p.CallsNamed(nameof(IPinnacleFightPort.SetImmune)).Count(c => (PinnacleHandle)c.Args[0] == crystal && (bool)c.Args[1] == false),
                "one SetImmune(false): the core plays the shield-down cue on that toggle");

            // a stray guard event afterwards changes nothing
            p.ClearRecordings();
            Died(s, p, def.GuardTag, guards[1]);
            Assert.AreEqual(0, p.Said.Count);
            Assert.AreEqual(0, p.CallsNamed(nameof(IPinnacleFightPort.SetImmune)).Count());
        }

        [TestMethod]
        public void LineWardBroken_IsTheOrchestratorText_Ascii()
        {
            Assert.AreEqual("My wardens fall, and the crystal stands bare. Strike it, if your hands still answer you.", HarbingerLines.LineWardBroken);
            Assert.IsTrue(HarbingerLines.LineWardBroken.All(ch => ch >= 0x20 && ch < 0x7F));
        }

        [TestMethod]
        public void GuardSpawnFailed_CountsAsDead()
        {
            // the portal and the crystal spawn; the first guard fails (the core's retry failed too), the second spawns
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Acid);
            p.AtFloor.Add(p.Head);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.TimerFired, Tag = HarbingerScript.TimerPoll }, new FailingGuardPort(p));
            var crystal = Crystal(p, def);
            var live = p.Spawns.Where(x => x.Tag == def.GuardTag).ToList();
            Assert.AreEqual(1, live.Count(x => x.Result.IsNone), "control: one guard spawn failed");

            SpawnFailed(s, p, def.GuardTag);
            Assert.IsFalse(Damageable(p, crystal), "the failed guard is down, the live one still holds");

            Died(s, p, def.GuardTag, live.Single(x => !x.Result.IsNone).Result);
            Assert.IsTrue(Damageable(p, crystal));
            Assert.AreEqual(HarbingerLines.LineWardBroken, p.Said.Last());
        }

        [TestMethod]
        public void BothGuardSpawnsFailed_TheirSpawnFailedEventsBreakTheWard()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Lightning);
            p.Phase = 2;
            s.EnterPhase(2, p);
            p.AtFloor.Add(p.Head);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.TimerFired, Tag = HarbingerScript.TimerPoll }, new FailingGuardPort(p, failEveryGuard: true));
            var crystal = Crystal(p, def);

            SpawnFailed(s, p, def.GuardTag);
            Assert.IsFalse(Damageable(p, crystal));
            SpawnFailed(s, p, def.GuardTag);
            Assert.IsTrue(Damageable(p, crystal));

            // a third SpawnFailed has no None entry left to match
            p.ClearRecordings();
            SpawnFailed(s, p, def.GuardTag);
            Assert.AreEqual(0, p.Said.Count);
        }

        [TestMethod]
        public void RestartPhase_Alcove_ReShieldsTheNewCrystal_WithTwoFreshGuards()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Fire);
            ToAlcove(s, p, def.Element);
            var guards = Guards(p, def);
            Died(s, p, def.GuardTag, guards[0]);
            Died(s, p, def.GuardTag, guards[1]);
            Assert.IsTrue(Damageable(p, Crystal(p, def)), "control: the ward was broken before the restart");

            s.RestartPhase(p);
            var crystal = Crystal(p, def);
            var fresh = Guards(p, def);

            Assert.IsFalse(Damageable(p, crystal), "the respawned crystal is shielded");
            CollectionAssert.AreNotEquivalent(guards, fresh);

            // an AddDied for an old guard (destroyed at the restart) does not count
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = def.GuardTag, Handle = guards[0] }, p);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = def.GuardTag, Handle = guards[1] }, p);
            Assert.IsFalse(Damageable(p, crystal));

            Died(s, p, def.GuardTag, fresh[0]);
            Died(s, p, def.GuardTag, fresh[1]);
            Assert.IsTrue(Damageable(p, crystal));
        }

        [TestMethod]
        public void Arrival_ReplaysTheShieldUpCue_OnlyWhileShielded()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Acid);
            ToAlcove(s, p, def.Element);
            var crystal = Crystal(p, def);

            Arrive(s, p, def.Element);
            Assert.AreEqual(1, p.Effects.Count(x => x.Handle == crystal && x.Script == PlayScript.ShieldUpGrey));

            // a fresh fight whose ward is already broken before the owner arrives: no shield-up cue
            (s, p) = Started();
            ToAlcove(s, p, def.Element);
            crystal = Crystal(p, def);
            foreach (var g in Guards(p, def))
                Died(s, p, def.GuardTag, g);
            Arrive(s, p, def.Element);
            Assert.AreEqual(0, p.Effects.Count(x => x.Handle == crystal && x.Script == PlayScript.ShieldUpGrey));
        }

        [TestMethod]
        public void GuardDeath_OutsideTheAlcoveStage_IsIgnored()
        {
            var (s, p) = Started();
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = "guard-acid", SpawnFailed = true }, p);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = "guard-acid", Handle = new PinnacleHandle(900) }, p);

            Assert.AreEqual(0, p.Said.Count(l => l == HarbingerLines.LineWardBroken));
            Assert.AreEqual(0, p.Immune.Keys.Count(h => h != p.Head));
        }

        // ================================================================ A6: the crystal's casts, B1: the ward link

        [TestMethod]
        public void CrystalCast_ArmedAtArrival_VulnFirstThenEveryFifth_OnItsOwnCounter()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Lightning);
            ToAlcove(s, p, def.Element);
            var crystal = Crystal(p, def);

            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == HarbingerScript.TimerCrystalCast), "not armed before the owner arrives");

            Arrive(s, p, def.Element);
            Assert.AreEqual(S.CrystalCastSeconds, LastInterval(p));

            for (var i = 0; i < 11; i++)
                Fire(s, p, HarbingerScript.TimerCrystalCast);

            var casts = CastsBy(p, crystal).Select(c => c.SpellId).ToList();
            Assert.AreEqual(11, casts.Count);
            Assert.AreEqual(4483u, casts[0], "the vulnerability first");
            Assert.AreEqual(4483u, casts[5]);
            Assert.AreEqual(4483u, casts[10]);

            foreach (var i in new[] { 1, 2, 3, 4, 6, 7, 8, 9 })
                CollectionAssert.Contains(def.WarSpells.ToList(), casts[i], $"cast {i + 1}");

            // the head's own counter is untouched: its first cast (owner on the floor) is still the vulnerability
            p.OwnerAt = Pt(10, -20, -36f);
            Fire(s, p, HarbingerScript.TimerCast);
            Assert.AreEqual((p.Head, 4483u), p.NowCasts.Last());
            StringAssert.Contains(s.Describe(), "crystal_casts=11");
            StringAssert.Contains(s.Describe(), "casts=1 ");
        }

        [TestMethod]
        public void CrystalCast_HeadCastsDoNotAdvanceTheCrystalsCounter()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Acid);

            p.OwnerAt = Pt(10, -20, -36f);
            for (var i = 0; i < 3; i++)
                Fire(s, p, HarbingerScript.TimerCast);

            ToAlcove(s, p, def.Element);
            Arrive(s, p, def.Element);
            Fire(s, p, HarbingerScript.TimerCrystalCast);

            Assert.AreEqual(def.VulnSpell, CastsBy(p, Crystal(p, def)).Single().SpellId, "the crystal's first cast is its vulnerability, whatever the head cast");
        }

        [TestMethod]
        public void CrystalCast_NoCastWhileTheOwnerIsOutsideTheAlcove_ButTheTimerKeepsGoing()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Frost);
            ToAlcove(s, p, def.Element);
            var crystal = Crystal(p, def);
            Arrive(s, p, def.Element);
            var c = HarbingerData.AlcoveOf(def.Element).Centre;

            foreach (var outside in new PinnaclePoint?[] { Pt(10, -20, -36f), Pt(c.X + 6f, c.Y, 0f), Pt(c.X, c.Y, -3f), null })
            {
                p.OwnerAt = outside;
                var sets = p.TimerSets.Count(t => t.Tag == HarbingerScript.TimerCrystalCast);
                Fire(s, p, HarbingerScript.TimerCrystalCast);

                Assert.AreEqual(0, CastsBy(p, crystal).Count(), $"owner at {outside?.ToString() ?? "unreadable"}");
                Assert.AreEqual(sets + 1, p.TimerSets.Count(t => t.Tag == HarbingerScript.TimerCrystalCast), "re-armed while the alcove lasts");
            }

            p.OwnerAt = Pt(c.X + 1f, c.Y, 0f);
            Fire(s, p, HarbingerScript.TimerCrystalCast);
            Assert.AreEqual(def.VulnSpell, CastsBy(p, crystal).Single().SpellId, "the first real cast is still the vulnerability");
        }

        [TestMethod]
        public void CrystalCast_NotInTheFightingStage_NorAfterTheCrystalDies_NorInTheFinal()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Acid);
            var alcove = HarbingerData.AlcoveOf(def.Element);

            // Fighting: a stray fire casts nothing and is not re-armed
            p.OwnerAt = Pt(alcove.Centre.X, alcove.Centre.Y, 0f);
            Fire(s, p, HarbingerScript.TimerCrystalCast);
            Assert.AreEqual(0, p.NowCasts.Count);
            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == HarbingerScript.TimerCrystalCast));

            // the crystal dies: its timer is cancelled, a queued fire casts nothing and is not re-armed
            ToAlcove(s, p, def.Element);
            Arrive(s, p, def.Element);
            var crystal = Crystal(p, def);
            Died(s, p, def.CrystalTag, crystal);
            CollectionAssert.Contains(p.CancelledTimers, HarbingerScript.TimerCrystalCast);

            var sets = p.TimerSets.Count(t => t.Tag == HarbingerScript.TimerCrystalCast);
            Fire(s, p, HarbingerScript.TimerCrystalCast);
            Assert.AreEqual(0, CastsBy(p, crystal).Count());
            Assert.AreEqual(sets, p.TimerSets.Count(t => t.Tag == HarbingerScript.TimerCrystalCast));

            // the final: no crystal, no guardian, no crystal timer, no ward, no attunement
            Assert.AreEqual(1, p.OwnerBuffs.Count, "control: the crystal's death above granted its attunement");
            p.ClearRecordings();
            p.Phase = 5;
            s.EnterPhase(5, p);
            Fire(s, p, HarbingerScript.TimerPoll);
            Fire(s, p, HarbingerScript.TimerCrystalCast);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = def.GuardTag, SpawnFailed = true }, p);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = def.CrystalTag, SpawnFailed = true }, p);
            s.RestartPhase(p);

            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == HarbingerScript.TimerCrystalCast));
            Assert.IsFalse(p.Spawns.Any(x => HarbingerData.Elements.Any(e => x.Tag == e.CrystalTag || x.Tag == e.GuardTag)));
            Assert.AreEqual(0, p.NowCasts.Count(x => x.Caster != p.Head));
            Assert.AreEqual(0, p.OwnerBuffs.Count);
            CollectionAssert.DoesNotContain(p.Said, HarbingerLines.LineWardBroken);
        }

        [TestMethod]
        public void WardLink_FirstGuardDeathHalvesTheInterval_ForTheRestOfTheAlcove()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Fire);
            ToAlcove(s, p, def.Element);
            Arrive(s, p, def.Element);
            var guards = Guards(p, def);

            Fire(s, p, HarbingerScript.TimerCrystalCast);
            Assert.AreEqual(S.CrystalCastSeconds, LastInterval(p), "full interval while both guards stand");

            var sets = p.TimerSets.Count;
            Died(s, p, def.GuardTag, guards[0]);
            Assert.AreEqual((HarbingerScript.TimerCrystalCast, S.CrystalCastSeconds / 2.0), p.TimerSets.Skip(sets).Single(),
                "the first guard's death re-arms the cast timer at the halved interval at once");
            Fire(s, p, HarbingerScript.TimerCrystalCast);
            Assert.AreEqual(S.CrystalCastSeconds / 2.0, LastInterval(p), "halved after the first guard");

            sets = p.TimerSets.Count;
            Died(s, p, def.GuardTag, guards[1]);
            Assert.AreEqual(sets, p.TimerSets.Count, "the second guard re-arms nothing");
            Fire(s, p, HarbingerScript.TimerCrystalCast);
            Assert.AreEqual(S.CrystalCastSeconds / 2.0, LastInterval(p), "the second guard does not halve it again");

            // a resume respawns the alcove: a fresh ward link, the full interval again, armed at the next arrival
            s.RestartPhase(p);
            CollectionAssert.Contains(p.CancelledTimers, HarbingerScript.TimerCrystalCast);
            Arrive(s, p, def.Element);
            Assert.AreEqual(S.CrystalCastSeconds, LastInterval(p));
            Assert.AreEqual(def.VulnSpell, FireAndLastCrystalCast(s, p, def), "a new crystal starts with its vulnerability");
        }

        [TestMethod]
        public void WardLink_AGuardSpawnFailed_DoesNotHalve_ButTheLiveGuardsDeathDoes()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Acid);
            p.AtFloor.Add(p.Head);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.TimerFired, Tag = HarbingerScript.TimerPoll }, new FailingGuardPort(p));
            p.AtFloor.Remove(p.Head);
            Arrive(s, p, def.Element);

            var sets = p.TimerSets.Count;
            SpawnFailed(s, p, def.GuardTag);
            Assert.AreEqual(sets, p.TimerSets.Count, "a failed spawn re-arms nothing");
            Fire(s, p, HarbingerScript.TimerCrystalCast);
            Assert.AreEqual(S.CrystalCastSeconds, LastInterval(p), "a guardian that never stood sets no ward link");
            Assert.IsFalse(s.Describe().Contains(" linked"));

            // the live guard's real death sets it (and, being the last one down, also breaks the ward)
            var live = p.Spawns.Single(x => x.Tag == def.GuardTag && !x.Result.IsNone).Result;
            Died(s, p, def.GuardTag, live);
            Assert.AreEqual((HarbingerScript.TimerCrystalCast, S.CrystalCastSeconds / 2.0), p.TimerSets.Last());
            Assert.IsTrue(Damageable(p, Crystal(p, def)));
        }

        [TestMethod]
        public void WardLink_BeforeTheOwnerArrives_HalvesWithoutArmingTheTimer_ThenArrivalArmsItHalved()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Lightning);
            ToAlcove(s, p, def.Element);

            Died(s, p, def.GuardTag, Guards(p, def)[0]);
            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == HarbingerScript.TimerCrystalCast), "no cast timer before the owner arrives");

            Arrive(s, p, def.Element);
            Assert.AreEqual(S.CrystalCastSeconds / 2.0, LastInterval(p));
        }

        [TestMethod]
        public void CrystalCastSeconds_OverrideIsTheCadence()
        {
            var (s, p) = Started(S with { CrystalCastSeconds = 12 });
            var def = HarbingerData.Element(HarbingerElement.Acid);
            ToAlcove(s, p, def.Element);
            Arrive(s, p, def.Element);
            Assert.AreEqual(12.0, LastInterval(p));

            Died(s, p, def.GuardTag, Guards(p, def)[1]);
            Fire(s, p, HarbingerScript.TimerCrystalCast);
            Assert.AreEqual(6.0, LastInterval(p));
        }

        private static uint FireAndLastCrystalCast(HarbingerScript s, FakePinnacleFightPort p, HarbingerElementDef def)
        {
            Fire(s, p, HarbingerScript.TimerCrystalCast);
            return CastsBy(p, Crystal(p, def)).Last().SpellId;
        }

        // ================================================================ B1: the attunement

        [TestMethod]
        public void Attunement_CrystalDeath_GrantsTheElementsWard_For120Seconds()
        {
            var expected = new Dictionary<HarbingerElement, uint>
            {
                [HarbingerElement.Acid] = 6080, [HarbingerElement.Lightning] = 6079, [HarbingerElement.Frost] = 6083, [HarbingerElement.Fire] = 6082,
            };

            foreach (var def in HarbingerData.Elements)
            {
                var (s, p) = Started();
                ToAlcove(s, p, def.Element);
                Assert.AreEqual(0, p.OwnerBuffs.Count, $"{def.Tag}: nothing before the crystal breaks");

                foreach (var g in Guards(p, def))
                    Died(s, p, def.GuardTag, g);
                Assert.AreEqual(0, p.OwnerBuffs.Count, $"{def.Tag}: the ward breaking grants nothing");

                Died(s, p, def.CrystalTag, Crystal(p, def));

                Assert.AreEqual((expected[def.Element], 120.0), p.OwnerBuffs.Single(), def.Tag);
                Assert.AreEqual(expected[def.Element], def.AttuneSpell, def.Tag);
            }
        }

        [TestMethod]
        public void Attunement_NotOnAFailedCrystalSpawn_NorASkippedAlcove_NorAStrayDeath()
        {
            // the crystal's spawn failed
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Lightning);
            SpawnFailed(s, p, "crystal-lightning");
            Assert.AreEqual(HarbingerStage.Landing, s.Stage, "control: the element ended");
            Assert.AreEqual(0, p.OwnerBuffs.Count);

            // no portal: the alcove is skipped
            (s, p) = Started();
            p.AtFloor.Add(p.Head);
            p.FailNextSpawns = 1;
            Fire(s, p, HarbingerScript.TimerPoll);
            Assert.AreEqual(HarbingerStage.Landing, s.Stage, "control: the alcove was skipped");
            Assert.AreEqual(0, p.OwnerBuffs.Count);

            // a crystal death outside the Alcove stage
            (s, p) = Started();
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = "crystal-acid", Handle = new PinnacleHandle(900) }, p);
            Assert.AreEqual(0, p.OwnerBuffs.Count);
        }

        [TestMethod]
        public void Attunement_Seconds_ComesFromTheSettings_ZeroGrantsNothing()
        {
            var (s, p) = Started(S with { AttuneSeconds = 45 });
            ToAlcove(s, p, HarbingerElement.Acid);
            Died(s, p, "crystal-acid", Crystal(p, HarbingerData.Element(HarbingerElement.Acid)));
            Assert.AreEqual((6080u, 45.0), p.OwnerBuffs.Single());

            (s, p) = Started(S with { AttuneSeconds = 0 });
            ToAlcove(s, p, HarbingerElement.Acid);
            Died(s, p, "crystal-acid", Crystal(p, HarbingerData.Element(HarbingerElement.Acid)));
            Assert.AreEqual(0, p.OwnerBuffs.Count);
        }

        // ================================================================ A7: Data

        [TestMethod]
        public void Data_HealthMultipliers_HeadAndFourCrystals_FromTheSettings()
        {
            var s = new HarbingerScript(S with { HealthMultiplier = 3.5, HeadHealthMultiplier = 7.0 });
            var crystals = HarbingerData.Elements.Select(e => e.CrystalWcid).ToArray();

            CollectionAssert.AreEquivalent(crystals.Append(HarbingerData.HeadWcid).ToArray(), s.Data.HealthMultipliers.Keys.ToArray());
            Assert.AreEqual(7.0, s.Data.HealthMultiplierFor(HarbingerData.HeadWcid), "round 4: the head has its own multiplier");
            Assert.IsTrue(crystals.All(c => s.Data.HealthMultiplierFor(c) == 3.5), "the crystals keep the shared one");

            foreach (var e in HarbingerData.Elements)
            {
                Assert.AreEqual(1.0, s.Data.HealthMultiplierFor(e.GuardianWcid), e.Tag);
                Assert.AreEqual(1.0, s.Data.HealthMultiplierFor(e.SummonWcid), e.Tag);
            }

            Assert.AreEqual(20.0, new HarbingerScript().Data.HealthMultiplierFor(HarbingerData.HeadWcid), "the head default is 20.0 (10x the old 2.0)");
            Assert.AreEqual(2.0, new HarbingerScript().Data.HealthMultiplierFor(crystals[0]), "the crystal default stays 2.0");
        }

        [TestMethod]
        public void Overrides_HealthMultiplier_ReachesTheScriptsData()
        {
            Assert.IsTrue(HarbingerOverrides.TryCreateScript(S, HazardArgs.Parse("health_multiplier=4 head_health_multiplier=9 crystal_cast_seconds=6 attune_seconds=30"), out var script, out var error), error);

            Assert.AreEqual(9.0, script.Data.HealthMultiplierFor(HarbingerData.HeadWcid));
            Assert.AreEqual(4.0, script.Data.HealthMultiplierFor(HarbingerData.Elements[2].CrystalWcid));
            Assert.AreEqual(6.0, script.Settings.CrystalCastSeconds);
            Assert.AreEqual(30.0, script.Settings.AttuneSeconds);
        }

        /// <summary>
        /// A port that fails the guardian spawns (one, or every one) of the next alcove, as the core does after its retry fails
        /// (None returned, the SpawnFailed raised later by the test). Everything else is the wrapped fake.
        /// </summary>
        private sealed class FailingGuardPort : IPinnacleFightPort
        {
            private readonly FakePinnacleFightPort inner;
            private readonly bool failEveryGuard;
            private bool failedOne;

            public FailingGuardPort(FakePinnacleFightPort inner, bool failEveryGuard = false)
            {
                this.inner = inner;
                this.failEveryGuard = failEveryGuard;
            }

            public PinnacleHandle SpawnAdd(string tag, uint wcid, PinnaclePoint at, PinnacleTier tier)
            {
                if (tag.StartsWith("guard-") && (failEveryGuard || !failedOne))
                {
                    failedOne = true;
                    inner.FailNextSpawns = 1;
                }

                return inner.SpawnAdd(tag, wcid, at, tier);
            }

            public double Now => inner.Now;
            public System.Random Rng => inner.Rng;
            public int Phase => inner.Phase;
            public int Level => inner.Level;
            public PinnacleHandle Head => inner.Head;
            public bool IsAlive(PinnacleHandle h) => inner.IsAlive(h);
            public void SetImmune(PinnacleHandle h, bool immune) => inner.SetImmune(h, immune);
            public void SetAttackable(PinnacleHandle h, bool attackable) => inner.SetAttackable(h, attackable);
            public void CastAtOwner(PinnacleHandle caster, uint spellId) => inner.CastAtOwner(caster, spellId);
            public void StartHazard(string tag, string kind, string pattern, HazardArgs args, PinnacleHandle origin = default, PinnacleHazardOptions options = null) => inner.StartHazard(tag, kind, pattern, args, origin, options);
            public bool IsOwnerGrounded() => inner.IsOwnerGrounded();
            public int DestroyAdds(string tag) => inner.DestroyAdds(tag);
            public void CastAtOwnerNow(PinnacleHandle caster, uint spellId) => inner.CastAtOwnerNow(caster, spellId);
            public void SetMeleeReach(PinnacleHandle h, float? metres) => inner.SetMeleeReach(h, metres);
            public void StopHazards() => inner.StopHazards();
            public void StopHazard(string tag) => inner.StopHazard(tag);
            public void SetTimer(string tag, double seconds) => inner.SetTimer(tag, seconds);
            public void CancelTimer(string tag) => inner.CancelTimer(tag);
            public int DispelOwnerBuffs(int count) => inner.DispelOwnerBuffs(count);
            public bool GrantOwnerBuff(uint spellId, double seconds) => inner.GrantOwnerBuff(spellId, seconds);
            public void Say(string line) => inner.Say(line);
            public PinnacleHandle SpawnProp(string tag, PinnacleProp prop) => inner.SpawnProp(tag, prop);
            public void DestroyProp(PinnacleHandle h) => inner.DestroyProp(h);
            public void SetDoorOpen(PinnacleHandle h, bool open) => inner.SetDoorOpen(h, open);
            public void PlayMotion(PinnacleHandle h, MotionStance stance, MotionCommand motion, bool hold) => inner.PlayMotion(h, stance, motion, hold);
            public void PlayEffect(PinnacleHandle h, PlayScript script) => inner.PlayEffect(h, script);
            public void MoveTo(PinnacleHandle h, PinnaclePoint to) => inner.MoveTo(h, to);
            public double TurnTo(PinnacleHandle h, PinnaclePoint facing) => inner.TurnTo(h, facing);
            public bool TryGetPosition(PinnacleHandle h, out PinnaclePoint at) => inner.TryGetPosition(h, out at);
            public bool TryGetOwnerPosition(out PinnaclePoint at) => inner.TryGetOwnerPosition(out at);
            public void SetPeaceful(PinnacleHandle h, bool peaceful) => inner.SetPeaceful(h, peaceful);
            public void Engage(PinnacleHandle h) => inner.Engage(h);
            public void SetHealthFloor(PinnacleHandle h, double? fraction) => inner.SetHealthFloor(h, fraction);
            public bool IsAtHealthFloor(PinnacleHandle h) => inner.IsAtHealthFloor(h);
            public void Slay(PinnacleHandle h) => inner.Slay(h);
            public bool ControlHazard(string tag, string key, double value) => inner.ControlHazard(tag, key, value);
            public bool TryGetHazardLevel(string tag, out double level) => inner.TryGetHazardLevel(tag, out level);
            public void AdvancePhase() => inner.AdvancePhase();
            public void Win() => inner.Win();
        }
    }
}
