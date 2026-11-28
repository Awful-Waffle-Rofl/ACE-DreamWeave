using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.BalanceTrials;
using ACE.Server.ClassAbilities;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// The archetype resolver: seeded files first, then the curated master stamps, the broken overlay and the consistency
    /// refusals. Every fixture is built in memory (the live scan needs a database), with its own TrialCopyGate. Reads no
    /// PropertyManager key.
    /// </summary>
    [TestClass]
    public class TrialArchetypeResolverTests
    {
        private static readonly DateTime At = new DateTime(2026, 11, 24, 9, 30, 15, DateTimeKind.Utc);

        private static readonly string FileSha = new string('f', 64);

        private static TrialArchetype Seeded(string id, int slot, int level = 400)
            => new TrialArchetype(id, slot, ClassAbilityClass.Archmage, level, null, null, TrialBuffs.OwnSpellbook);

        private static List<(TrialArchetype Archetype, string Sha256)> Files(params TrialArchetype[] a) => a.Select(x => (x, FileSha)).ToList();

        private static TrialCopyGate Gate() => new TrialCopyGate(() => 0);

        [TestMethod]
        public void A_seeded_file_resolves_as_before()
        {
            var r = TrialArchetypeResolver.Resolve(Files(Seeded("archmage-400", 0)), new Dictionary<int, string>(), Gate(), "archmage-400");

            Assert.IsNotNull(r);
            Assert.IsFalse(r.Curated);
            Assert.IsFalse(r.Broken);
            Assert.AreEqual(FileSha, r.Sha);
            Assert.AreEqual(0, r.Archetype.Slot);
        }

        [TestMethod]
        public void A_curated_stamp_resolves_with_field_1_as_its_sha()
        {
            var stamp = TrialCurated.Stamp("void-tank", ClassAbilityClass.VoidSummon, 412, At);
            var r = TrialArchetypeResolver.Resolve(Files(Seeded("archmage-400", 0)), new Dictionary<int, string> { [3] = stamp }, Gate(), "void-tank");

            Assert.IsNotNull(r);
            Assert.IsTrue(r.Curated);
            Assert.IsFalse(r.Broken);
            Assert.AreEqual(new TrialArchetype("void-tank", 3, ClassAbilityClass.VoidSummon, 412, null, null, "ownSpellbook"), r.Archetype);
            Assert.AreEqual(TrialCurated.Identity("void-tank", ClassAbilityClass.VoidSummon, 412), r.Sha);

            // Rebuild compares the master stamp's field 1 to this sha: they must agree.
            Assert.IsTrue(TrialRebuild.TryParseStamp(stamp, out var field1, out _, out _));
            Assert.AreEqual(field1, r.Sha);
        }

        [TestMethod]
        public void A_curated_id_equal_to_a_seeded_files_id_is_flagged_not_shadowed()
        {
            // The same id as a file (slot 0) and as a curated master (slot 4): never silently the file, always a collision.
            var stamps = new Dictionary<int, string> { [4] = TrialCurated.Stamp("dup", ClassAbilityClass.Rogue, 380, At) };

            var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialArchetypeResolver.Resolve(Files(Seeded("dup", 0)), stamps, Gate(), "dup"));
            StringAssert.Contains(ex.Message, "slot 0");
            StringAssert.Contains(ex.Message, "slot 4");
            StringAssert.Contains(ex.Message, "@trial unpromote slot:4");

            // Another file resolves normally, and list reports the collision without throwing.
            Assert.IsNotNull(TrialArchetypeResolver.Resolve(Files(Seeded("dup", 0), Seeded("other", 7)), stamps, Gate(), "other"));
            var listing = TrialArchetypeResolver.All(Files(Seeded("dup", 0)), stamps, Gate());
            Assert.AreEqual(1, listing.Collisions.Count);
            CollectionAssert.AreEqual(new[] { "dup" }, listing.Collisions[0].Ids.ToArray());
        }

        [TestMethod]
        public void Unknown_ids_and_non_curated_stamps_resolve_to_null()
        {
            var stamps = new Dictionary<int, string>
            {
                [0] = "v1|" + new string('a', 64) + "|" + new string('b', 64),                           // a seeded master
                [1] = TrialRebuild.PromotedStamp(new string('a', 64), new string('b', 64), At),          // seeded, then promoted
                [2] = "garbage",
                [3] = "v1|adopted:lost:Rogue:400|adopted",                                               // 3 fields: not curated
            };

            Assert.IsNull(TrialArchetypeResolver.Resolve(Files(Seeded("archmage-400", 0)), stamps, Gate(), "lost"));
            Assert.IsNull(TrialArchetypeResolver.Resolve(Files(), stamps, Gate(), "nope"));
            Assert.IsNull(TrialArchetypeResolver.Resolve(Files(), stamps, Gate(), ""));
            var listing = TrialArchetypeResolver.All(Files(Seeded("archmage-400", 0)), stamps, Gate());
            Assert.AreEqual(1, listing.Archetypes.Count, "only the file");
            Assert.AreEqual(0, listing.Collisions.Count);
        }

        [TestMethod]
        public void A_seeded_files_slot_holding_a_curated_stamp_refuses_only_the_two_ids_it_involves()
        {
            var stamps = new Dictionary<int, string> { [0] = TrialCurated.Stamp("cur-a", ClassAbilityClass.Rogue, 400, At) };
            var files = Files(Seeded("archmage-400", 0), Seeded("other", 7));

            // The file and the curated master on its slot are both ambiguous: refused, naming the slot recovery.
            foreach (var id in new[] { "archmage-400", "cur-a" })
            {
                var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialArchetypeResolver.Resolve(files, stamps, Gate(), id), id);
                StringAssert.Contains(ex.Message, "slot 0");
                StringAssert.Contains(ex.Message, "archmage-400");
                StringAssert.Contains(ex.Message, "cur-a");
                StringAssert.Contains(ex.Message, "@trial unpromote slot:0");
            }

            // An unrelated id resolves normally.
            var other = TrialArchetypeResolver.Resolve(files, stamps, Gate(), "other");
            Assert.IsNotNull(other);
            Assert.AreEqual(7, other.Archetype.Slot);
            Assert.IsNull(TrialArchetypeResolver.Resolve(files, stamps, Gate(), "nobody"));

            // list reports it as a line, without throwing.
            var listing = TrialArchetypeResolver.All(files, stamps, Gate());
            Assert.AreEqual(1, listing.Collisions.Count);
            CollectionAssert.AreEquivalent(new[] { "archmage-400", "cur-a" }, listing.Collisions[0].Ids.ToArray());
            StringAssert.Contains(listing.Collisions[0].Message, "@trial unpromote slot:0");

            // Control: the same stamp one slot over is fine.
            var ok = TrialArchetypeResolver.Resolve(files, new Dictionary<int, string> { [1] = stamps[0] }, Gate(), "cur-a");
            Assert.AreEqual(1, ok.Archetype.Slot);
        }

        [TestMethod]
        public void One_curated_id_in_two_slots_refuses_that_id_only()
        {
            var stamp = TrialCurated.Stamp("cur-a", ClassAbilityClass.Rogue, 400, At);
            var stamps = new Dictionary<int, string> { [2] = stamp, [5] = stamp };
            var files = Files(Seeded("archmage-400", 0));

            var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialArchetypeResolver.Resolve(files, stamps, Gate(), "cur-a"));
            StringAssert.Contains(ex.Message, "slots 2, 5");
            StringAssert.Contains(ex.Message, "slot:2");
            StringAssert.Contains(ex.Message, "slot:5");

            // The seeded file still resolves.
            var file = TrialArchetypeResolver.Resolve(files, stamps, Gate(), "archmage-400");
            Assert.IsNotNull(file);
            Assert.IsFalse(file.Curated);
        }

        [TestMethod]
        public void Unpromote_slot_clears_one_duplicate_and_the_id_resolves_again()
        {
            var stamp = TrialCurated.Stamp("cur-a", ClassAbilityClass.Rogue, 400, At);
            var stamps = new Dictionary<int, string> { [2] = stamp, [5] = stamp };
            var gate = Gate();
            var purged = new List<uint>();

            var lookups = new TrialPromote.Lookups
            {
                Gate = gate,
                AnySessionForSlot = _ => false,
                Stamp = g => stamps.TryGetValue((int)(g - TrialRoster.SlotBase), out var s) ? s : null,
                Purge = g => { purged.Add(g); stamps.Remove((int)(g - TrialRoster.SlotBase)); return true; },
            };

            Assert.ThrowsExactly<TrialArchetypeException>(() => TrialArchetypeResolver.Resolve(Files(), stamps, gate, "cur-a"));

            Assert.IsTrue(TrialArchetypeResolver.TryParseSlotArgument("slot:5", out var slot));
            Assert.IsTrue(TrialPromote.TryUnpromoteSlot(slot, lookups, out var refusal, out var reply), refusal);
            CollectionAssert.AreEqual(new[] { TrialRoster.MasterSlot(5) }, purged, "exactly the named slot is purged");
            StringAssert.Contains(reply, "master slot 5");
            Assert.IsFalse(gate.IsBusy(TrialRoster.MasterSlot(5)));
            Assert.IsFalse(gate.IsBroken(TrialRoster.MasterSlot(5), out _));

            var r = TrialArchetypeResolver.Resolve(Files(), stamps, gate, "cur-a");
            Assert.IsNotNull(r);
            Assert.AreEqual(2, r.Archetype.Slot);

            // A seeded (not promoted) master is never purged by slot either, and a slot out of range is refused.
            stamps[9] = "v1|" + new string('a', 64) + "|" + new string('b', 64);
            Assert.IsFalse(TrialPromote.TryUnpromoteSlot(9, lookups, out refusal, out _));
            StringAssert.Contains(refusal, "not promoted");
            Assert.IsFalse(TrialPromote.TryUnpromoteSlot(TrialRoster.MasterCount, lookups, out _, out _));
            CollectionAssert.AreEqual(new[] { TrialRoster.MasterSlot(5) }, purged);
        }

        [TestMethod]
        public void Unpromote_slot_clears_a_broken_only_duplicate_every_suggested_slot_is_clearable()
        {
            // One curated id twice: stamped in slot 2, and broken-only in slot 5 (an adopt that purged and never landed).
            var stamps = new Dictionary<int, string> { [2] = TrialCurated.Stamp("cur-a", ClassAbilityClass.Rogue, 400, At) };
            var gate = Gate();
            gate.MarkBroken(TrialRoster.MasterSlot(5), "an adopt's copy failed", TrialCurated.Identity("cur-a", ClassAbilityClass.Rogue, 400), TrialCurated.CalibrationToken);
            var purged = new List<uint>();

            TrialPromote.Lookups Lookups() => new TrialPromote.Lookups
            {
                Gate = gate,
                AnySessionForSlot = _ => false,
                Stamp = g => stamps.TryGetValue((int)(g - TrialRoster.SlotBase), out var s) ? s : null,
                Purge = g => { purged.Add(g); stamps.Remove((int)(g - TrialRoster.SlotBase)); return true; },
            };

            var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialArchetypeResolver.Resolve(Files(), stamps, gate, "cur-a"));
            StringAssert.Contains(ex.Message, "slot:2");
            StringAssert.Contains(ex.Message, "slot:5");

            // The broken-only slot the message suggests is clearable: purged, the broken record gone, the gate free.
            Assert.IsTrue(TrialPromote.TryUnpromoteSlot(5, Lookups(), out var refusal, out var reply), refusal);
            CollectionAssert.AreEqual(new[] { TrialRoster.MasterSlot(5) }, purged);
            StringAssert.Contains(reply, "broken curated archetype cur-a was cleared");
            Assert.IsFalse(gate.IsBroken(TrialRoster.MasterSlot(5), out _));
            Assert.IsFalse(gate.IsBusy(TrialRoster.MasterSlot(5)));

            var r = TrialArchetypeResolver.Resolve(Files(), stamps, gate, "cur-a");
            Assert.AreEqual(2, r.Archetype.Slot, "the stamped copy is the one left");
            Assert.IsFalse(r.Broken);

            // The other suggestion, the stamped slot, is clearable too (through unpromote proper).
            gate.MarkBroken(TrialRoster.MasterSlot(5), "again", TrialCurated.Identity("cur-a", ClassAbilityClass.Rogue, 400), TrialCurated.CalibrationToken);
            Assert.IsTrue(TrialPromote.TryUnpromoteSlot(2, Lookups(), out refusal, out _), refusal);
            CollectionAssert.AreEqual(new[] { TrialRoster.MasterSlot(5), TrialRoster.MasterSlot(2) }, purged);

            // A broken-only slot with a copy hold still live is not cleared.
            Assert.IsTrue(gate.TryTake(TrialRoster.MasterSlot(5), 0, out var hold, out _));
            Assert.IsFalse(TrialPromote.TryUnpromoteSlot(5, Lookups(), out refusal, out _));
            StringAssert.Contains(refusal, "already in flight");
            Assert.AreEqual(2, purged.Count);
            Assert.IsTrue(gate.Release(hold));

            // A broken record with a seeded lineage is not a curated slot: unpromote proper refuses it (no promoted stamp).
            var seededGate = Gate();
            seededGate.MarkBroken(TrialRoster.MasterSlot(7), "a seeded promote failed", new string('a', 64), new string('b', 64));
            var l = Lookups();
            l.Gate = seededGate;
            Assert.IsFalse(TrialPromote.TryUnpromoteSlot(7, l, out refusal, out _));
            StringAssert.Contains(refusal, "not promoted");
        }

        [TestMethod]
        public void Slot_argument_parses_only_slot_0_to_31_in_one_spelling()
        {
            Assert.IsTrue(TrialArchetypeResolver.TryParseSlotArgument("slot:0", out var s));
            Assert.AreEqual(0, s);
            Assert.IsTrue(TrialArchetypeResolver.TryParseSlotArgument("slot:31", out s));
            Assert.AreEqual(31, s);

            foreach (var bad in new[] { null, "", "slot:", "slot:32", "slot:05", "slot:-1", "slot:+3", "slot: 3", "Slot:3", "archmage-400", "slot:3x" })
                Assert.IsFalse(TrialArchetypeResolver.TryParseSlotArgument(bad, out _), bad ?? "null");
        }

        [TestMethod]
        public void A_slot_with_no_stamp_and_a_curated_broken_lineage_resolves_broken()
        {
            var gate = Gate();
            var master = TrialRoster.MasterSlot(6);
            gate.MarkBroken(master, "an adopt's copy failed", TrialCurated.Identity("cur-a", ClassAbilityClass.Berserker, 405), TrialCurated.CalibrationToken);

            var r = TrialArchetypeResolver.Resolve(Files(Seeded("archmage-400", 0)), new Dictionary<int, string>(), gate, "cur-a");

            Assert.IsNotNull(r);
            Assert.IsTrue(r.Curated);
            Assert.IsTrue(r.Broken);
            Assert.AreEqual(6, r.Archetype.Slot);
            Assert.AreEqual(405, r.Archetype.TargetLevel);
            Assert.AreEqual(ClassAbilityClass.Berserker, r.Archetype.Class);

            // Start and rebuild refuse it through the gate they already ask.
            StringAssert.Contains(TrialStarter.MasterCopyRefusal(gate, TrialRoster.MasterSlot(r.Archetype.Slot)), "broken");

            // A seeded lineage on an empty slot is not a curated archetype, and an empty slot with no record is nothing.
            var other = Gate();
            other.MarkBroken(master, "a seeded promote failed", new string('a', 64), new string('b', 64));
            Assert.IsNull(TrialArchetypeResolver.Resolve(Files(), new Dictionary<int, string>(), other, "cur-a"));
            Assert.IsNull(TrialArchetypeResolver.Resolve(Files(), new Dictionary<int, string>(), Gate(), "cur-a"));

            // A stamp present wins over a broken record (mid-adopt, before the purge): it resolves from the stamp, not broken.
            var live = TrialArchetypeResolver.Resolve(Files(), new Dictionary<int, string> { [6] = TrialCurated.Stamp("cur-a", ClassAbilityClass.Berserker, 405, At) }, gate, "cur-a");
            Assert.IsFalse(live.Broken);
        }

        [TestMethod]
        public void All_lists_files_then_curated_and_Describe_names_slot_class_and_level()
        {
            var gate = Gate();
            gate.MarkBroken(TrialRoster.MasterSlot(9), "x", TrialCurated.Identity("cur-b", ClassAbilityClass.Archer, 390), TrialCurated.CalibrationToken);

            var all = TrialArchetypeResolver.All(Files(Seeded("archmage-400", 0)),
                new Dictionary<int, string> { [4] = TrialCurated.Stamp("cur-a", ClassAbilityClass.Rogue, 401, At) }, gate);

            CollectionAssert.AreEqual(new[]
            {
                "archmage-400 (slot 0)",
                "cur-a (slot 4, curated Rogue L401)",
                "cur-b (slot 9, curated Archer L390, broken)",
            }, all.Archetypes.Select(TrialArchetypeResolver.Describe).ToArray());
            Assert.AreEqual(0, all.Collisions.Count);
        }

        [TestMethod]
        public void Unpromote_of_a_curated_master_says_the_archetype_is_removed()
        {
            var curated = TrialArchetypeResolver.Resolve(Files(), new Dictionary<int, string> { [4] = TrialCurated.Stamp("cur-a", ClassAbilityClass.Rogue, 401, At) }, Gate(), "cur-a");
            Assert.AreEqual("curated archetype cur-a removed; slot 4 is free", ACE.Server.Command.Handlers.TrialCommands.CuratedUnpromoteReply(curated));

            var seeded = TrialArchetypeResolver.Resolve(Files(Seeded("archmage-400", 0)), new Dictionary<int, string>(), Gate(), "archmage-400");
            Assert.IsNull(ACE.Server.Command.Handlers.TrialCommands.CuratedUnpromoteReply(seeded), "a seeded master keeps the reseed reply");
        }

        [TestMethod]
        public void The_stamp_scan_throwing_is_a_trial_data_refusal()
        {
            var saved = TrialArchetypeResolver.StampScan;
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "trialresolver-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);

            try
            {
                TrialArchetypeResolver.StampScan = () => throw new InvalidOperationException("db down");
                var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialArchetypeResolver.Resolve(dir, "x"));
                StringAssert.Contains(ex.Message, "stamp scan failed");

                // Fresh on every call: the next resolve reads the new answer, nothing is cached.
                var calls = 0;
                TrialArchetypeResolver.StampScan = () => { calls++; return new Dictionary<int, string> { [2] = TrialCurated.Stamp("x", ClassAbilityClass.Rogue, 400, At) }; };
                Assert.IsNotNull(TrialArchetypeResolver.Resolve(dir, "x"));
                TrialArchetypeResolver.StampScan = () => { calls++; return new Dictionary<int, string>(); };
                Assert.IsNull(TrialArchetypeResolver.Resolve(dir, "x"), "the curated master was unpromoted: gone at once");
                Assert.AreEqual(2, calls);
            }
            finally
            {
                TrialArchetypeResolver.StampScan = saved;
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }

        [TestMethod]
        public void Live_stamp_scan_is_one_narrow_shard_read_of_type_9034_over_the_master_guids()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/BalanceTrials/TrialArchetypeResolver.cs");
            var body = PooledLootSourceText.MethodBody(source, "private static IReadOnlyDictionary<int, string> LiveStampScan()");

            StringAssert.Contains(body, "DatabaseManager.Shard.BaseDatabase.GetBiotaStringPropertyInRange((ushort)PropertyString.TrialArchetypeStamp, TrialRoster.SlotBase, TrialRoster.SlotBase + (uint)TrialRoster.MasterCount - 1)");
            Assert.IsFalse(body.Contains("GetBiota("), "no biota is loaded, so nothing reaches the biota cache");
            Assert.IsFalse(body.Contains("PlayerManager"), "masters are parked and never in PlayerManager");

            var db = PooledLootSourceText.Read("Source/ACE.Database/ShardDatabase_TrialMasters.cs");
            StringAssert.Contains(db, "using (var context = new ShardDbContext())");
            StringAssert.Contains(db, ".AsNoTracking()");
            Assert.IsFalse(PooledLootSourceText.Read("Source/ACE.Database/ShardDatabaseWithCaching.cs").Contains("GetBiotaStringPropertyInRange"), "the caching layer does not wrap it");
        }

        [TestMethod]
        public void Every_trial_verb_resolves_through_the_resolver()
        {
            var commands = PooledLootSourceText.Read("Source/ACE.Server/Command/Handlers/TrialCommands.cs");
            var starter = PooledLootSourceText.Read("Source/ACE.Server/BalanceTrials/TrialStarter.cs");

            Assert.IsFalse(commands.Contains("TrialArchetypeLoader.LoadDirectory"), "rebuild, promote, unpromote and list go through the resolver");
            Assert.IsFalse(starter.Contains("TrialArchetypeLoader.LoadDirectory"), "start goes through the resolver");
            Assert.AreEqual(2, CountOf(commands, "TrialArchetypeResolver.Resolve("), "rebuild, and the promote/unpromote head");
            Assert.AreEqual(1, CountOf(commands, "TrialArchetypeResolver.All("), "list");
            Assert.AreEqual(1, CountOf(starter, "TrialArchetypeResolver.Resolve("), "start");
        }

        private static int CountOf(string text, string what)
        {
            var n = 0;
            for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal))
                n++;
            return n;
        }
    }
}
