using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.BalanceTrials;
using ACE.Server.ClassAbilities;
using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// @trial adopt (TrialAdopt): the refusal ladder (nothing purged on any refusal), the empty-slot and re-adopt paths, a failed
    /// copy (broken, resolves broken, rebuild refuses), the broken mark before the purge, the slot re-read under the hold, and the
    /// parser. Every world lookup is a fake; the gate is a fresh instance per fake with a fake clock. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class TrialAdoptTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);

        private const uint CallerGuid = 0x50000123;
        private const uint SourceGuid = 0x50004444;
        private const string SourceName = "Vexa Thorn";
        private const int SourceLevel = 412;

        private static readonly string FileSha = new string('f', 64);

        private sealed class Fake
        {
            public double Clock;
            public TrialCopyGate Gate;
            public HashSet<uint> Online = new HashSet<uint>();
            public HashSet<uint> TrialSessions = new HashSet<uint>();
            public HashSet<int> SlotSessions = new HashSet<int>();
            public Dictionary<int, string> Stamps = new Dictionary<int, string>();
            public HashSet<int> Characters = new HashSet<int>();
            public List<(TrialArchetype Archetype, string Sha256)> Seeded = new List<(TrialArchetype, string)>();
            public Dictionary<string, (uint Guid, string Name)> Names = new Dictionary<string, (uint, string)>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<uint, TrialAdopt.SourceFacts> Sources = new Dictionary<uint, TrialAdopt.SourceFacts>();
            public bool PurgeOk = true;
            public bool? CopyResult = true;
            public List<string> Calls = new List<string>();
            public List<CopyCharOptions> Options = new List<CopyCharOptions>();
            public List<Action<bool>> PendingDone = new List<Action<bool>>();
            public Func<uint, bool> OnlineOverride;

            /// <summary>Runs once, on the first stamp read after the gate is taken (the re-read under the hold).</summary>
            public Action AfterTake;

            /// <summary>What the gate said about the master at the moment of its purge.</summary>
            public List<(bool Broken, string Lineage)> AtPurge = new List<(bool, string)>();

            public string LandedStampFor;

            public Fake() => Gate = new TrialCopyGate(() => Clock);

            public int Purges => Calls.Count(c => c.StartsWith("purge", StringComparison.Ordinal));
            public int Copies => Calls.Count(c => c.StartsWith("copy", StringComparison.Ordinal));

            public TrialAdopt.Lookups Build() => new TrialAdopt.Lookups
            {
                Gate = Gate,
                IsOnline = g => OnlineOverride?.Invoke(g) ?? Online.Contains(g),
                HasTrialSession = g => TrialSessions.Contains(g),
                AnySessionForSlot = s => SlotSessions.Contains(s),
                UtcNow = () => Now,
                Seeded = () => Seeded,
                Stamps = () =>
                {
                    if (AfterTake != null && Gate.IsBusy(TrialRoster.MasterSlot(1)))
                    {
                        var a = AfterTake;
                        AfterTake = null;
                        a();
                    }

                    return new Dictionary<int, string>(Stamps);
                },
                CharacterSlots = () => Characters.ToList(),
                FindByName = n => Names.TryGetValue(n, out var v) ? v : null,
                Source = g => Sources.TryGetValue(g, out var f) ? f : default,
                Purge = g =>
                {
                    Calls.Add($"purge 0x{g:X8}");
                    Gate.TryGetBrokenLineage(g, out var lineage, out _);
                    AtPurge.Add((Gate.IsBroken(g, out _), lineage));

                    var slot = (int)(g - TrialRoster.SlotBase);
                    Stamps.Remove(slot);
                    Characters.Remove(slot);
                    return PurgeOk;
                },
                CopySource = (admin, source, sourceName, master, masterName, options, onDone) =>
                {
                    Calls.Add($"copy 0x{source:X8} -> 0x{master:X8} as {masterName}");
                    Options.Add(options);

                    if (CopyResult.HasValue)
                        Land(master, CopyResult.Value, onDone);
                    else
                        PendingDone.Add(ok => Land(master, ok, onDone));
                },
            };

            private void Land(uint master, bool ok, Action<bool> onDone)
            {
                if (ok && LandedStampFor != null)
                {
                    var slot = (int)(master - TrialRoster.SlotBase);
                    Stamps[slot] = LandedStampFor;
                    Characters.Add(slot);
                }

                onDone(ok);
            }
        }

        /// <summary>A healthy world: the seeded archmage-400 file on slot 0 (stamped), an offline source with caught-up counters.</summary>
        private static Fake Healthy()
        {
            var f = new Fake();
            f.Seeded.Add((new TrialArchetype("archmage-400", 0, ClassAbilityClass.Archmage, 400, null, null, TrialBuffs.OwnSpellbook), FileSha));
            f.Stamps[0] = $"v1|{FileSha}|{new string('c', 64)}";
            f.Characters.Add(0);
            f.Names[SourceName] = (SourceGuid, SourceName);
            f.Sources[SourceGuid] = Facts();
            return f;
        }

        private static TrialAdopt.SourceFacts Facts(int level = SourceLevel, int enlightenment = 0, int? milestones = null, int? enlightenmentPoints = null)
            => new TrialAdopt.SourceFacts(true, true, 20, level, enlightenment,
                milestones ?? ClassAbilityMilestones.EntitledCount(level), enlightenmentPoints ?? EnlightenmentCapMilestones.EntitledCount(enlightenment), 36);

        private static bool Adopt(Fake f, out string refusal, out IReadOnlyList<string> reply, out string stamp,
            string id = "void-tank", string cls = "VoidSummon", string name = SourceName, uint? caller = CallerGuid)
            => TrialAdopt.TryAdopt(null, caller, id, cls, name, f.Build(), out refusal, out reply, out stamp);

        private static void AssertNothingTouched(Fake f, string because)
        {
            Assert.AreEqual(0, f.Purges, $"{because}: nothing is purged");
            Assert.AreEqual(0, f.Copies, $"{because}: nothing is copied");

            for (var s = 0; s < TrialRoster.MasterCount; s++)
            {
                Assert.IsFalse(f.Gate.IsBusy(TrialRoster.MasterSlot(s)), $"{because}: slot {s} is left unheld");
                Assert.IsFalse(f.Gate.IsBroken(TrialRoster.MasterSlot(s), out _), $"{because}: slot {s} is not marked broken");
            }

            Assert.IsFalse(f.Gate.IsBusy(SourceGuid), $"{because}: the source is left unheld");
        }

        // ================================================================ the happy paths

        [TestMethod]
        public void Adopt_into_an_empty_slot_purges_it_then_copies_the_source_unpublished_with_the_curated_stamp()
        {
            var f = Healthy();

            Assert.IsTrue(Adopt(f, out var refusal, out var reply, out var stamp), refusal);

            var master = TrialRoster.MasterSlot(1);
            CollectionAssert.AreEqual(new[] { $"purge 0x{master:X8}", $"copy 0x{SourceGuid:X8} -> 0x{master:X8} as {TrialRoster.MasterName(1)}" }, f.Calls,
                "slot 0 is skipped, slot 1 is free: purged once, then the source copied onto it under the master's name");

            Assert.AreEqual(TrialCurated.Stamp("void-tank", ClassAbilityClass.VoidSummon, SourceLevel, Now), stamp, "the copy carries the curated stamp at the source's level");
            Assert.IsFalse(f.Options.Single().Publish, "a curated master is parked, never published");
            Assert.IsNotNull(f.Options.Single().BeforeSave, "Normalize runs in BeforeSave");

            var line = reply[0];
            StringAssert.Contains(line, "slot 1");
            StringAssert.Contains(line, $"level {SourceLevel}");
            StringAssert.Contains(line, "TotalClassAbilityPointsEarned 36");
            Assert.AreEqual(1, reply.Count);

            // The copy landed: the gate is free and nothing is broken.
            Assert.IsFalse(f.Gate.IsBusy(master));
            Assert.IsFalse(f.Gate.IsBusy(SourceGuid));
            Assert.IsFalse(f.Gate.IsBroken(master, out _));
        }

        [TestMethod]
        public void Adopt_enforces_no_level_and_no_cap_total()
        {
            // R3: any level, any CAP total; only the counters must have caught up.
            foreach (var level in new[] { 1, 126, 275, 400, 401, 650 })
            {
                var f = Healthy();
                f.Sources[SourceGuid] = Facts(level) with { TotalPointsEarned = 999 };

                Assert.IsTrue(Adopt(f, out var refusal, out var reply, out var stamp), $"{level}: {refusal}");
                Assert.AreEqual(TrialCurated.Stamp("void-tank", ClassAbilityClass.VoidSummon, level, Now), stamp);
                StringAssert.Contains(reply[0], "TotalClassAbilityPointsEarned 999");
            }
        }

        [TestMethod]
        public void Re_adopting_an_existing_curated_id_reuses_its_slot()
        {
            var f = Healthy();
            f.Stamps[7] = TrialCurated.Stamp("void-tank", ClassAbilityClass.Archmage, 380, Now.AddDays(-3));
            f.Characters.Add(7);

            Assert.IsTrue(Adopt(f, out var refusal, out var reply, out var stamp), refusal);

            var master = TrialRoster.MasterSlot(7);
            Assert.AreEqual($"purge 0x{master:X8}", f.Calls[0], "slot 7, not the lowest free slot");
            Assert.AreEqual(TrialCurated.Stamp("void-tank", ClassAbilityClass.VoidSummon, SourceLevel, Now), stamp, "the new class and level replace the old");
            Assert.IsTrue(reply.Any(l => l.Contains("already held curated archetype void-tank")), string.Join(" / ", reply));
        }

        // ================================================================ every refusal leaves nothing purged

        [TestMethod]
        public void Every_refusal_leaves_nothing_purged_copied_held_or_broken()
        {
            var cases = new List<(string Name, Action<Fake> Arrange, string Id, string Cls, string SourceName, uint? Caller, string Expect)>
            {
                ("bad id", _ => { }, "Void:Tank", "VoidSummon", SourceName, CallerGuid, "lower-case"),
                ("id too long", _ => { }, new string('a', 41), "VoidSummon", SourceName, CallerGuid, "at most 40"),
                ("seeded id", _ => { }, "archmage-400", "VoidSummon", SourceName, CallerGuid, "seeded archetype file"),
                ("ambiguous id", f => { f.Stamps[3] = TrialCurated.Stamp("void-tank", ClassAbilityClass.Rogue, 400, Now); f.Stamps[4] = f.Stamps[3]; f.Characters.Add(3); f.Characters.Add(4); }, "void-tank", "VoidSummon", SourceName, CallerGuid, "unpromote slot:"),
                ("bad class", _ => { }, "void-tank", "Wizard", SourceName, CallerGuid, "is not a class"),
                ("class with a newline", _ => { }, "void-tank", "VoidSummon\n", SourceName, CallerGuid, "is not a class"),
                ("no free slot", f => { for (var s = 1; s < TrialRoster.MasterCount; s++) f.Characters.Add(s); }, "void-tank", "VoidSummon", SourceName, CallerGuid, "no free master slot"),
                ("unknown name", _ => { }, "void-tank", "VoidSummon", "Nobody Here", CallerGuid, "no character is named"),
                ("no name", _ => { }, "void-tank", "VoidSummon", " ", CallerGuid, "name the character"),
                ("source is a master", f => f.Names["Trialmaster 9"] = (TrialRoster.MasterSlot(9), "Trialmaster 9"), "void-tank", "VoidSummon", "Trialmaster 9", CallerGuid, "is a trial master"),
                ("source is a rep", f => f.Names[TrialRoster.RepSlots[2].Name] = (TrialRoster.RepSlots[2].Guid, TrialRoster.RepSlots[2].Name), "void-tank", "VoidSummon", TrialRoster.RepSlots[2].Name, CallerGuid, "use @trial promote"),
                ("no caller", _ => { }, "void-tank", "VoidSummon", SourceName, null, "in-world character"),
                ("caller is the source", _ => { }, "void-tank", "VoidSummon", SourceName, SourceGuid, "you are"),
                ("source online", f => f.Online.Add(SourceGuid), "void-tank", "VoidSummon", SourceName, CallerGuid, "is online"),
                ("source in a trial", f => f.TrialSessions.Add(SourceGuid), "void-tank", "VoidSummon", SourceName, CallerGuid, "trial session"),
                ("session on the slot", f => f.SlotSessions.Add(1), "void-tank", "VoidSummon", SourceName, CallerGuid, "running archetype slot 1"),
                ("slot gained a stamp under the hold", f => f.AfterTake = () => f.Stamps[1] = TrialCurated.Stamp("other", ClassAbilityClass.Rogue, 400, Now), "void-tank", "VoidSummon", SourceName, CallerGuid, "gained a stamp"),
                ("slot gained a character under the hold", f => f.AfterTake = () => f.Characters.Add(1), "void-tank", "VoidSummon", SourceName, CallerGuid, "gained a character row"),
                ("no character row", f => f.Sources[SourceGuid] = Facts() with { HasCharacter = false }, "void-tank", "VoidSummon", SourceName, CallerGuid, "no character row"),
                ("no biota", f => f.Sources[SourceGuid] = Facts() with { HasBiota = false }, "void-tank", "VoidSummon", SourceName, CallerGuid, "no biota"),
                ("no possessions", f => f.Sources[SourceGuid] = Facts() with { Possessions = 0 }, "void-tank", "VoidSummon", SourceName, CallerGuid, "no possessions"),
                ("no level", f => f.Sources[SourceGuid] = Facts() with { Level = null }, "void-tank", "VoidSummon", SourceName, CallerGuid, "has no level"),
                ("level milestones behind", f => f.Sources[SourceGuid] = Facts(milestones: ClassAbilityMilestones.EntitledCount(SourceLevel) - 1), "void-tank", "VoidSummon", SourceName, CallerGuid, "have not caught up"),
                ("enlightenment milestones behind", f => f.Sources[SourceGuid] = Facts(enlightenment: 5, enlightenmentPoints: EnlightenmentCapMilestones.EntitledCount(5) - 1), "void-tank", "VoidSummon", SourceName, CallerGuid, "have not caught up"),
            };

            foreach (var c in cases)
            {
                var f = Healthy();
                c.Arrange(f);

                Assert.IsFalse(Adopt(f, out var refusal, out var reply, out var stamp, c.Id, c.Cls, c.SourceName, c.Caller), c.Name);
                StringAssert.Contains(refusal, c.Expect, c.Name);
                Assert.IsNull(reply, c.Name);
                Assert.IsNull(stamp, c.Name);
                AssertNothingTouched(f, c.Name);
            }

            // The source held exclusively by another copy: the take fails, refused, and that other hold is left as it was.
            var busy = Healthy();
            Assert.IsTrue(busy.Gate.TryTake(SourceGuid, 0, out var other, out _));
            Assert.IsFalse(Adopt(busy, out var busyRefusal, out _, out _));
            StringAssert.Contains(busyRefusal, "already in flight");
            Assert.AreEqual(0, busy.Purges);
            Assert.AreEqual(0, busy.Copies);
            Assert.IsFalse(busy.Gate.IsBusy(TrialRoster.MasterSlot(1)), "the master's hold was never kept");
            Assert.IsFalse(busy.Gate.IsBroken(TrialRoster.MasterSlot(1), out _));
            Assert.IsTrue(busy.Gate.Release(other), "the other hold is still live");

            // A master slot held by another copy is never picked at all: the adopt goes on to the next free slot.
            var held = Healthy();
            Assert.IsTrue(held.Gate.TryTake(TrialRoster.MasterSlot(1), 0, out _, out _));
            Assert.IsTrue(Adopt(held, out var heldRefusal, out var heldReply, out _), heldRefusal);
            StringAssert.Contains(heldReply[0], "slot 2");
        }

        [TestMethod]
        public void The_online_re_check_just_before_the_purge_refuses()
        {
            var f = Healthy();
            var reads = 0;
            f.OnlineOverride = g => g == SourceGuid && ++reads >= 2;   // offline at step 7, online at step 12

            Assert.IsFalse(Adopt(f, out var refusal, out _, out _));
            StringAssert.Contains(refusal, "came online");
            Assert.AreEqual(2, reads);
            AssertNothingTouched(f, "online re-check");
        }

        // ================================================================ the broken mark and the slot under the gate

        [TestMethod]
        public void The_slot_is_marked_broken_with_the_curated_lineage_before_its_purge()
        {
            var f = Healthy();

            Assert.IsTrue(Adopt(f, out var refusal, out _, out _), refusal);

            var (broken, lineage) = f.AtPurge.Single();
            Assert.IsTrue(broken, "broken before the purge runs");
            Assert.AreEqual(TrialCurated.Identity("void-tank", ClassAbilityClass.VoidSummon, SourceLevel), lineage, "with the curated identity as its lineage");
        }

        [TestMethod]
        public void A_second_adopt_after_a_hold_lapse_mid_copy_never_picks_the_slot_in_flight()
        {
            var f = Healthy();
            f.CopyResult = null;   // the first copy never reports
            f.Names["Brin Holt"] = (0x50005555, "Brin Holt");
            f.Sources[0x50005555] = Facts(390);

            Assert.IsTrue(Adopt(f, out var refusal, out _, out _), refusal);
            Assert.AreEqual($"purge 0x{TrialRoster.MasterSlot(1):X8}", f.Calls[0]);

            // The 300 s hold lapses with the copy still in flight: slot 1 has no stamp, no character row and no hold now.
            f.Clock += TrialCopyGate.HoldSeconds + 1;
            Assert.IsFalse(f.Gate.IsBusy(TrialRoster.MasterSlot(1)));

            Assert.IsTrue(Adopt(f, out refusal, out var reply, out _, id: "brin", cls: "Rogue", name: "Brin Holt"), refusal);
            StringAssert.Contains(reply[0], "slot 2", "slot 1 reads broken, so the second adopt passes over it");
            Assert.AreEqual($"purge 0x{TrialRoster.MasterSlot(2):X8}", f.Calls[2]);
        }

        [TestMethod]
        public void A_re_adopt_of_the_same_id_waits_for_an_unreported_copy_for_three_hold_lengths()
        {
            var f = Healthy();
            f.CopyResult = null;   // the first copy never reports

            Assert.IsTrue(Adopt(f, out var refusal, out _, out _), refusal);
            Assert.AreEqual(1, f.Purges);

            // +301 s: the hold has lapsed, the slot is broken with this id's lineage, so the same id goes back to it; the
            // copy may still land, so the shared tail refuses rather than purge under it.
            f.Clock = TrialCopyGate.HoldSeconds + 1;
            Assert.IsFalse(f.Gate.IsBusy(TrialRoster.MasterSlot(1)), "the hold has lapsed");
            Assert.IsFalse(Adopt(f, out refusal, out _, out _));
            StringAssert.Contains(refusal, "a copy onto this master started 301 s ago and has not reported; wait for it or restart");
            Assert.AreEqual(1, f.Purges, "nothing purged again");
            Assert.IsFalse(f.Gate.IsBusy(TrialRoster.MasterSlot(1)), "the refusal released the gate");
            Assert.IsFalse(f.Gate.IsBusy(SourceGuid));

            // +15 min +1 s: the mark is older than 3 x HoldSeconds, so the re-adopt proceeds.
            f.Clock = 3 * TrialCopyGate.HoldSeconds + 1;
            f.CopyResult = true;
            Assert.IsTrue(Adopt(f, out refusal, out var reply, out _), refusal);
            Assert.AreEqual(2, f.Purges);
            StringAssert.Contains(reply[0], "slot 1");
            Assert.IsFalse(f.Gate.IsBroken(TrialRoster.MasterSlot(1), out _), "the landed copy clears the mark");
        }

        [TestMethod]
        public void A_copy_that_lands_after_its_hold_lapsed_clears_its_own_mark_and_rebuild_accepts_the_master()
        {
            var f = Healthy();
            f.CopyResult = null;   // the copy reports late
            f.LandedStampFor = TrialCurated.Stamp("void-tank", ClassAbilityClass.VoidSummon, SourceLevel, Now);

            Assert.IsTrue(Adopt(f, out var refusal, out _, out _), refusal);
            var master = TrialRoster.MasterSlot(1);

            f.Clock = TrialCopyGate.HoldSeconds + 1;
            Assert.IsFalse(f.Gate.IsBusy(master), "the hold has lapsed");

            f.PendingDone.Single()(true);
            Assert.IsFalse(f.Gate.IsBroken(master, out _), "no newer mark: the late success clears its own");

            var r = TrialArchetypeResolver.Resolve(f.Seeded, f.Stamps, f.Gate, "void-tank");
            Assert.IsFalse(r.Broken);

            var copies = 0;
            var rebuild = new TrialRebuild.Lookups
            {
                Gate = f.Gate,
                IsOnline = _ => false,
                MasterStamp = g => g == master ? f.Stamps[1] : null,
                AccountId = _ => 4242,
                Purge = _ => true,
                Copy = (_, _, _, _, onDone) => { copies++; onDone(true); },
            };
            Assert.IsTrue(TrialRebuild.TryRebuild(null, r.Archetype, r.Sha, 1, rebuild, out refusal), refusal);
            Assert.AreEqual(1, copies);
        }

        [TestMethod]
        public void A_late_success_clears_nothing_when_a_newer_mark_was_made()
        {
            var f = Healthy();
            f.CopyResult = null;

            Assert.IsTrue(Adopt(f, out var refusal, out _, out _), refusal);
            var master = TrialRoster.MasterSlot(1);

            f.Clock = TrialCopyGate.HoldSeconds + 1;
            f.Gate.MarkBroken(master, "a newer operation's mark");

            f.PendingDone.Single()(true);
            Assert.IsTrue(f.Gate.IsBroken(master, out var why), "the stale success leaves the newer mark");
            Assert.AreEqual("a newer operation's mark", why);
        }

        [TestMethod]
        public void A_failed_copy_mark_does_not_hold_off_the_recovery()
        {
            // The copy reported (false): its mark is not an in-flight one, so the same-id re-adopt runs at once.
            var f = Healthy();
            f.CopyResult = false;
            Assert.IsTrue(Adopt(f, out var refusal, out _, out _), refusal);
            Assert.IsFalse(f.Gate.TryGetCopyInFlight(TrialRoster.MasterSlot(1), out _));

            f.CopyResult = true;
            Assert.IsTrue(Adopt(f, out refusal, out _, out _), refusal);
            Assert.AreEqual(2, f.Purges);
        }

        [TestMethod]
        public void The_counter_refusal_says_when_class_abilities_are_disabled()
        {
            var on = Healthy();
            on.Sources[SourceGuid] = Facts(milestones: 0);
            Assert.IsFalse(Adopt(on, out var refusal, out _, out _));
            StringAssert.Contains(refusal, "in once so the login catch-up pays them");
            Assert.IsFalse(refusal.Contains("disabled"), refusal);

            var off = Healthy();
            off.Sources[SourceGuid] = Facts(milestones: 0);
            var lookups = off.Build();
            lookups.ClassAbilitiesEnabled = () => false;
            Assert.IsFalse(TrialAdopt.TryAdopt(null, CallerGuid, "void-tank", "VoidSummon", SourceName, lookups, out refusal, out _, out _));
            StringAssert.Contains(refusal, "class abilities are disabled, so CAP milestones never catch up");
            Assert.IsFalse(refusal.Contains("login catch-up pays them"), refusal);
            AssertNothingTouched(off, "flag off");

            // With the counters caught up the flag does not matter: nothing refuses on it.
            var caughtUp = Healthy();
            var l2 = caughtUp.Build();
            l2.ClassAbilitiesEnabled = () => false;
            Assert.IsTrue(TrialAdopt.TryAdopt(null, CallerGuid, "void-tank", "VoidSummon", SourceName, l2, out refusal, out _, out _), refusal);
        }

        [TestMethod]
        public void Unpromote_slot_clears_a_leftover_character_row_but_never_a_seeded_slot()
        {
            var clock = 0.0;
            var gate = new TrialCopyGate(() => clock);
            var characters = new HashSet<uint> { TrialRoster.MasterSlot(0), TrialRoster.MasterSlot(9) };
            var purged = new List<uint>();
            var purgeOk = true;
            IReadOnlyCollection<int> seeded = new[] { 0 };

            TrialPromote.Lookups Lookups() => new TrialPromote.Lookups
            {
                Gate = gate,
                AnySessionForSlot = _ => false,
                Stamp = _ => null,
                HasCharacterRow = g => characters.Contains(g),
                SeededSlots = () => seeded,
                Purge = g => { purged.Add(g); if (purgeOk) characters.Remove(g); return purgeOk; },
            };

            // A seeded slot with a row and no stamp is the reseed's: refused, nothing purged.
            Assert.IsFalse(TrialPromote.TryUnpromoteSlot(0, Lookups(), out var refusal, out _));
            StringAssert.Contains(refusal, "seeded archetype file names it");
            Assert.AreEqual(0, purged.Count);
            Assert.IsFalse(gate.IsBusy(TrialRoster.MasterSlot(0)));
            Assert.IsFalse(gate.IsBroken(TrialRoster.MasterSlot(0), out _));

            // Seeded files unreadable: unknown, refused.
            seeded = null;
            Assert.IsFalse(TrialPromote.TryUnpromoteSlot(9, Lookups(), out refusal, out _));
            StringAssert.Contains(refusal, "could not be read");
            Assert.AreEqual(0, purged.Count);
            seeded = new[] { 0 };

            // An unreported copy's mark still holds it off (the in-flight guard).
            gate.MarkBroken(TrialRoster.MasterSlot(9), "x", copyInFlight: true);
            clock = TrialCopyGate.HoldSeconds + 1;
            Assert.IsFalse(TrialPromote.TryUnpromoteSlot(9, Lookups(), out refusal, out _));
            StringAssert.Contains(refusal, "has not reported");
            Assert.AreEqual(0, purged.Count);
            Assert.IsFalse(gate.IsBusy(TrialRoster.MasterSlot(9)));

            // Busy: refused.
            clock = TrialPromote.CopyInFlightWaitSeconds + 1;
            gate.ClearBroken(TrialRoster.MasterSlot(9));
            Assert.IsTrue(gate.TryTake(TrialRoster.MasterSlot(9), 0, out var hold, out _));
            Assert.IsFalse(TrialPromote.TryUnpromoteSlot(9, Lookups(), out refusal, out _));
            StringAssert.Contains(refusal, "already in flight");
            Assert.IsTrue(gate.Release(hold));

            // A failed purge leaves the slot broken.
            purgeOk = false;
            Assert.IsFalse(TrialPromote.TryUnpromoteSlot(9, Lookups(), out refusal, out _));
            StringAssert.Contains(refusal, "stays broken");
            Assert.IsTrue(gate.IsBroken(TrialRoster.MasterSlot(9), out _));
            Assert.IsFalse(gate.IsBusy(TrialRoster.MasterSlot(9)));

            // The leftover row on a slot no seeded file names is cleared: purged, the broken record gone, the slot free.
            purgeOk = true;
            Assert.IsTrue(TrialPromote.TryUnpromoteSlot(9, Lookups(), out refusal, out var reply), refusal);
            CollectionAssert.AreEqual(new[] { TrialRoster.MasterSlot(9), TrialRoster.MasterSlot(9) }, purged);
            StringAssert.Contains(reply, "a leftover character row with no stamp was cleared");
            Assert.IsFalse(gate.IsBroken(TrialRoster.MasterSlot(9), out _));
            Assert.IsFalse(gate.IsBusy(TrialRoster.MasterSlot(9)));
            CollectionAssert.DoesNotContain(characters.ToList(), TrialRoster.MasterSlot(9));

            // Nothing there at all (no row, no stamp, no record): unpromote proper refuses.
            Assert.IsFalse(TrialPromote.TryUnpromoteSlot(12, Lookups(), out refusal, out _));
            StringAssert.Contains(refusal, "not promoted");
            Assert.AreEqual(2, purged.Count);
        }

        [TestMethod]
        public void Promote_and_adopt_both_run_the_shared_tail()
        {
            var promote = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(
                ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/BalanceTrials/TrialPromote.cs"),
                "internal static bool TryPromote(Session admin, uint? callerGuid");
            var adopt = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(
                ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/BalanceTrials/TrialAdopt.cs"),
                "internal static bool TryAdopt(Session admin, uint? callerGuid");

            StringAssert.Contains(promote, "TryPurgeThenCopy(");
            StringAssert.Contains(adopt, "TrialPromote.TryPurgeThenCopy(");
            Assert.IsFalse(promote.Contains("lookups.Purge("), "promote purges only through the tail");
            Assert.IsFalse(adopt.Contains("lookups.Purge("), "adopt purges only through the tail");
        }

        [TestMethod]
        public void A_failed_copy_leaves_the_slot_broken_it_resolves_broken_and_rebuild_refuses_it()
        {
            var f = Healthy();
            f.CopyResult = false;

            Assert.IsTrue(Adopt(f, out var refusal, out _, out _), refusal);

            var master = TrialRoster.MasterSlot(1);
            Assert.IsTrue(f.Gate.IsBroken(master, out var why));
            StringAssert.Contains(why, "failed at its copy");
            Assert.IsFalse(f.Gate.IsBusy(master), "released");

            var r = TrialArchetypeResolver.Resolve(f.Seeded, f.Stamps, f.Gate, "void-tank");
            Assert.IsNotNull(r);
            Assert.IsTrue(r.Broken);
            Assert.AreEqual(1, r.Archetype.Slot);
            Assert.AreEqual(SourceLevel, r.Archetype.TargetLevel);

            var rebuild = new TrialRebuild.Lookups
            {
                Gate = f.Gate,
                IsOnline = _ => false,
                MasterStamp = _ => null,
                AccountId = _ => 4242,
                Purge = _ => throw new AssertFailedException("rebuild must not purge"),
                Copy = (_, _, _, _, _) => throw new AssertFailedException("rebuild must not copy"),
            };
            Assert.IsFalse(TrialRebuild.TryRebuild(null, r.Archetype, r.Sha, 1, rebuild, out refusal));
            StringAssert.Contains(refusal, "broken");

            // The recovery: adopt the same id again, which goes back to the broken slot and clears it when the copy lands.
            f.CopyResult = true;
            Assert.IsTrue(Adopt(f, out refusal, out var reply, out _), refusal);
            Assert.AreEqual($"purge 0x{master:X8}", f.Calls[2]);
            StringAssert.Contains(reply[0], "slot 1");
            Assert.IsFalse(f.Gate.IsBroken(master, out _));
        }

        [TestMethod]
        public void A_failed_purge_leaves_the_slot_broken_and_copies_nothing()
        {
            var f = Healthy();
            f.PurgeOk = false;

            Assert.IsFalse(Adopt(f, out var refusal, out _, out var stamp));
            StringAssert.Contains(refusal, "purge of master");
            Assert.IsNull(stamp);
            Assert.AreEqual(0, f.Copies);
            Assert.IsTrue(f.Gate.IsBroken(TrialRoster.MasterSlot(1), out _));
            Assert.IsFalse(f.Gate.IsBusy(TrialRoster.MasterSlot(1)));
        }

        // ================================================================ the parser

        [TestMethod]
        public void Parser_takes_every_argument_after_the_class_as_the_name()
        {
            var (verb, rest, error) = TrialCommandParser.Parse(new[] { "adopt", "void-tank", "VoidSummon", "Vexa", "the", "Thorn" });
            Assert.IsNull(error);
            Assert.AreEqual("adopt", verb);
            Assert.AreEqual("void-tank", rest[0]);
            Assert.AreEqual("VoidSummon", rest[1]);
            Assert.AreEqual("Vexa the Thorn", TrialCommands.AdoptName(rest));

            Assert.AreEqual("Vexa", TrialCommands.AdoptName(TrialCommandParser.Parse(new[] { "adopt", "a", "Rogue", "Vexa" }).Args));

            foreach (var bad in new[] { new[] { "adopt" }, new[] { "adopt", "a" }, new[] { "adopt", "a", "Rogue" } })
                StringAssert.StartsWith(TrialCommandParser.Parse(bad).Error, "usage: @trial adopt <id> <Class> <character name...>");

            StringAssert.Contains(TrialCommandParser.Usage, "adopt <id> <Class> <character name...>");
        }
    }
}
