using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.BalanceTrials;
using ACE.Server.ClassAbilities;
using ACE.Server.Command.Handlers;
using ACE.Server.Network;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Task 7: the rep rebuild, the stamp check, the state digest, the drift rule and the hard-copy schedule. Every world
    /// lookup goes through a fake. Reads no PropertyManager key, so there is nothing to seed. The rep block ledger is
    /// process-static and is reset around every test.
    /// </summary>
    [TestClass]
    public class TrialRepSetupTests
    {
        private const string ArchSha = "aaaa000000000000000000000000000000000000000000000000000000000001";
        private const string CalSha = "cccc000000000000000000000000000000000000000000000000000000000002";

        private static readonly TrialArchetype Archetype = new TrialArchetype("archmage-400", 3, ClassAbilityClass.Archmage, 400, null, null, "ownSpellbook");

        private static uint nextGuid = 0x80700000;

        [TestInitialize]
        public void Init() => TrialRepSetup.ResetAllBlocksForTests();

        [TestCleanup]
        public void Cleanup() => TrialRepSetup.ResetAllBlocksForTests();

        // ================================================================ rebuild

        private sealed class FakeLookups
        {
            public HashSet<uint> Online = new HashSet<uint>();
            public Dictionary<uint, string> Stamps = new Dictionary<uint, string>();
            public Dictionary<string, uint> Accounts = new Dictionary<string, uint>();
            public bool PurgeOk = true;
            public bool? CopyResult = true;
            public List<uint> Purged = new List<uint>();
            public List<(uint Master, TrialRoster.RepSlot Rep, uint Account)> Copies = new List<(uint, TrialRoster.RepSlot, uint)>();

            /// <summary>One gate per fake, so a copy that never reports in one case cannot hold the next case busy (TrialPromoteTests covers the gate).</summary>
            public TrialCopyGate Gate = new TrialCopyGate(() => 0);

            public TrialRebuild.Lookups Build() => new TrialRebuild.Lookups
            {
                Gate = Gate,
                IsOnline = g => Online.Contains(g),
                MasterStamp = g => Stamps.TryGetValue(g, out var s) ? s : null,
                AccountId = n => Accounts.TryGetValue(n, out var id) ? id : (uint?)null,
                Purge = g => { Purged.Add(g); return PurgeOk; },
                Copy = (session, master, rep, account, onDone) =>
                {
                    Copies.Add((master, rep, account));
                    if (CopyResult.HasValue)
                        onDone(CopyResult.Value);
                },
            };
        }

        private static FakeLookups Healthy(int repIndex)
        {
            var f = new FakeLookups();
            f.Stamps[TrialRoster.MasterSlot(Archetype.Slot)] = $"v1|{ArchSha}|{CalSha}";
            f.Accounts[TrialRoster.RepSlots[repIndex - 1].AccountName] = 4242;
            return f;
        }

        [TestMethod]
        public void Rebuild_refuses_an_online_slot_a_stale_stamp_and_a_missing_account()
        {
            const int repIndex = 2;
            var rep = TrialRoster.RepSlots[repIndex - 1];
            var master = TrialRoster.MasterSlot(Archetype.Slot);

            // Control: the healthy case purges once and copies once, master -> rep, on the rep's own account.
            var ok = Healthy(repIndex);
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, ok.Build(), out var refusal), refusal);
            Assert.IsNull(refusal);
            CollectionAssert.AreEqual(new[] { rep.Guid }, ok.Purged);
            Assert.AreEqual(1, ok.Copies.Count);
            Assert.AreEqual(master, ok.Copies[0].Master);
            Assert.AreEqual(rep, ok.Copies[0].Rep);
            Assert.AreEqual(4242u, ok.Copies[0].Account);

            // A rep slot online.
            var online = Healthy(repIndex);
            online.Online.Add(rep.Guid);
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, online.Build(), out refusal));
            StringAssert.Contains(refusal, "online");
            Assert.AreEqual(0, online.Purged.Count);
            Assert.AreEqual(0, online.Copies.Count);

            // A master with no stamp.
            var noStamp = Healthy(repIndex);
            noStamp.Stamps.Clear();
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, noStamp.Build(), out refusal));
            StringAssert.Contains(refusal, "no stamp");
            Assert.AreEqual(0, noStamp.Purged.Count);

            // A stamp whose archetype sha differs from the file's.
            var stale = Healthy(repIndex);
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype, ArchSha.Replace("aaaa", "bbbb"), repIndex, stale.Build(), out refusal));
            StringAssert.Contains(refusal, "master is stale; re-run seed-trial-archetypes");
            Assert.AreEqual(0, stale.Purged.Count);

            // A stamp of an unknown format is refused, never read as stale or fresh.
            var garbled = Healthy(repIndex);
            garbled.Stamps[master] = "v2|" + ArchSha + "|" + CalSha;
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, garbled.Build(), out refusal));
            StringAssert.Contains(refusal, "stamp");
            Assert.AreEqual(0, garbled.Purged.Count);

            // The account RepSlots[i].AccountName missing.
            var noAccount = Healthy(repIndex);
            noAccount.Accounts.Clear();
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, noAccount.Build(), out refusal));
            StringAssert.Contains(refusal, rep.AccountName);
            Assert.AreEqual(0, noAccount.Purged.Count);

            // A purge failure: nothing is copied onto a partly purged slot.
            var purgeFails = Healthy(repIndex);
            purgeFails.PurgeOk = false;
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, purgeFails.Build(), out refusal));
            StringAssert.Contains(refusal, "purge");
            Assert.AreEqual(1, purgeFails.Purged.Count);
            Assert.AreEqual(0, purgeFails.Copies.Count);
            Assert.IsTrue(TrialRepSetup.NeedsRebuild(rep.Guid), "a failed purge owes a rebuild across sessions");
            TrialRepSetup.ResetAllBlocksForTests();

            // Rep index outside 1..8.
            foreach (var bad in new[] { 0, 9 })
            {
                var f = Healthy(repIndex);
                Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype, ArchSha, bad, f.Build(), out refusal));
                StringAssert.Contains(refusal, "1..8");
            }
        }

        [TestMethod]
        public void Rebuild_rechecks_online_immediately_before_the_purge_and_resets_the_rep_block()
        {
            const int repIndex = 5;
            var rep = TrialRoster.RepSlots[repIndex - 1];

            // The online check is made again right before the purge: a rep that logs in between the checks is never purged.
            var f = Healthy(repIndex);
            var calls = 0;
            var lookups = f.Build();
            var inner = lookups.IsOnline;
            lookups.IsOnline = g => ++calls >= 2 || inner(g);
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, lookups, out var refusal));
            StringAssert.Contains(refusal, "online");
            Assert.AreEqual(2, calls, "online is checked up front and again right before the purge");
            Assert.AreEqual(0, f.Purged.Count);

            // A queued copy starts a new block for that rep: the hard-copy counter and the block's first enchantments go.
            var setup = NewSetup(rep.Guid, hardCopyEvery: 0, masterCore: "M");
            setup.Before(() => { }, () => new TrialDigest("M", "E1"));
            setup.OnRepEnded();
            Assert.AreEqual((1, "E1"), TrialRepSetup.BlockState(rep.Guid));

            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, Healthy(repIndex).Build(), out refusal), refusal);
            Assert.AreEqual((0, (string)null), TrialRepSetup.BlockState(rep.Guid));
        }

        [TestMethod]
        public void Stamp_parse_takes_the_archetype_field()
        {
            Assert.IsTrue(TrialRebuild.TryParseStamp($"v1|{ArchSha}|{CalSha}", out var arch, out var cal));
            Assert.AreEqual(ArchSha, arch);
            Assert.AreEqual(CalSha, cal);

            foreach (var bad in new[] { null, "", "v1", $"v1|{ArchSha}", $"v2|{ArchSha}|{CalSha}", $"v1|{ArchSha}|{CalSha}|x", $"v1||{CalSha}", $"v1|{ArchSha}|" })
                Assert.IsFalse(TrialRebuild.TryParseStamp(bad, out _, out _), $"'{bad}' parsed");
        }

        private static readonly DateTime PromotedAt = new DateTime(2026, 11, 22, 13, 5, 9, DateTimeKind.Utc);

        [TestMethod]
        public void Promoted_stamp_parses_with_its_time_and_the_builder_round_trips()
        {
            var promoted = $"v1|{ArchSha}|{CalSha}|promoted|2026-11-22T13:05:09Z";

            Assert.IsTrue(TrialRebuild.TryParseStamp(promoted, out var arch, out var cal, out var at));
            Assert.AreEqual(ArchSha, arch);
            Assert.AreEqual(CalSha, cal);
            Assert.AreEqual(PromotedAt, at);
            Assert.AreEqual(DateTimeKind.Utc, at.Value.Kind);

            // The 3-out overload still accepts it (the archetype field is what rebuild's older callers read).
            Assert.IsTrue(TrialRebuild.TryParseStamp(promoted, out arch, out cal));
            Assert.AreEqual(ArchSha, arch);

            // A plain stamp has no promotion time.
            Assert.IsTrue(TrialRebuild.TryParseStamp($"v1|{ArchSha}|{CalSha}", out _, out _, out at));
            Assert.IsNull(at);

            // The builder emits exactly the parsed shape, from any DateTimeKind's UTC instant.
            Assert.AreEqual(promoted, TrialRebuild.PromotedStamp(ArchSha, CalSha, PromotedAt));
            Assert.AreEqual(promoted, TrialRebuild.PromotedStamp(ArchSha, CalSha, PromotedAt.ToLocalTime()));
            Assert.IsTrue(TrialRebuild.TryParseStamp(TrialRebuild.PromotedStamp(ArchSha, CalSha, PromotedAt), out _, out _, out at));
            Assert.AreEqual(PromotedAt, at);
        }

        [TestMethod]
        public void Promoted_stamp_rejects_wrong_field_counts_tokens_and_times()
        {
            var bad = new[]
            {
                $"v1|{ArchSha}|{CalSha}|promoted",                              // 4 fields
                $"v1|{ArchSha}|{CalSha}|promoted|2026-11-22T13:05:09Z|x",       // 6 fields
                $"v1|{ArchSha}|{CalSha}|Promoted|2026-11-22T13:05:09Z",         // capitalised token
                $"v1|{ArchSha}|{CalSha}|PROMOTED|2026-11-22T13:05:09Z",
                $"v1|{ArchSha}|{CalSha}||2026-11-22T13:05:09Z",
                $"v1|{ArchSha}|{CalSha}|promoted|2026-11-22 13:05:09Z",         // wrong separator
                $"v1|{ArchSha}|{CalSha}|promoted|2026-11-22T13:05:09",          // no Z
                $"v1|{ArchSha}|{CalSha}|promoted|2026-11-22T13:05:09.000Z",     // fractional seconds
                $"v1|{ArchSha}|{CalSha}|promoted|2026-11-22T13:05:09+00:00",
                $"v1|{ArchSha}|{CalSha}|promoted|2026-13-22T13:05:09Z",         // not a date
                $"v1|{ArchSha}|{CalSha}|promoted|",
                $"v2|{ArchSha}|{CalSha}|promoted|2026-11-22T13:05:09Z",         // version
                $"v1||{CalSha}|promoted|2026-11-22T13:05:09Z",
                $"v1|{ArchSha}||promoted|2026-11-22T13:05:09Z",
            };

            foreach (var stamp in bad)
            {
                Assert.IsFalse(TrialRebuild.TryParseStamp(stamp, out _, out _, out var at), $"'{stamp}' parsed");
                Assert.IsNull(at);
                Assert.IsFalse(TrialRebuild.TryParseStamp(stamp, out _, out _), $"'{stamp}' parsed by the 3-out overload");
            }
        }

        [TestMethod]
        public void Rebuild_accepts_a_promoted_master_whose_archetype_sha_differs_and_notes_it()
        {
            const int repIndex = 2;
            var staleSha = ArchSha.Replace("aaaa", "bbbb");

            // Control: the same mismatch on a plain stamp is still the stale refusal (nothing purged, nothing copied).
            var plain = Healthy(repIndex);
            plain.Stamps[TrialRoster.MasterSlot(Archetype.Slot)] = $"v1|{staleSha}|{CalSha}";
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, plain.Build(), out var refusal, out var note));
            StringAssert.Contains(refusal, "master is stale");
            Assert.IsNull(note);
            Assert.AreEqual(0, plain.Purged.Count);
            Assert.AreEqual(0, plain.Copies.Count);

            // Promoted, sha differs: accepted, purged and copied once, with a note that names the archetype file's sha.
            var promoted = Healthy(repIndex);
            promoted.Stamps[TrialRoster.MasterSlot(Archetype.Slot)] = TrialRebuild.PromotedStamp(staleSha, CalSha, PromotedAt);
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, promoted.Build(), out refusal, out note), refusal);
            Assert.IsNull(refusal);
            Assert.AreEqual(1, promoted.Purged.Count);
            Assert.AreEqual(1, promoted.Copies.Count);
            StringAssert.Contains(note, "promoted");
            StringAssert.Contains(note, "2026-11-22T13:05:09Z");
            StringAssert.Contains(note, ArchSha);

            // The 6-arg internal overload (no note) behaves the same.
            var again = Healthy(repIndex);
            again.Stamps[TrialRoster.MasterSlot(Archetype.Slot)] = TrialRebuild.PromotedStamp(staleSha, CalSha, PromotedAt);
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, again.Build(), out refusal), refusal);

            // Promoted with the SAME sha: accepted, still noted (the copy is the tuned build), no mismatch clause.
            var same = Healthy(repIndex);
            same.Stamps[TrialRoster.MasterSlot(Archetype.Slot)] = TrialRebuild.PromotedStamp(ArchSha, CalSha, PromotedAt);
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, same.Build(), out refusal, out note), refusal);
            StringAssert.Contains(note, "promoted");
            Assert.IsFalse(note.Contains("differs"), note);

            // A plain, matching stamp carries no note.
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, Healthy(repIndex).Build(), out refusal, out note), refusal);
            Assert.IsNull(note);
        }

        [TestMethod]
        public void PurgeSlot_visibility_change_only()
        {
            var m = typeof(TesterRosterCommands).GetMethod("PurgeSlot", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "PurgeSlot is a non-public static method");
            Assert.IsTrue(m.IsAssembly, "PurgeSlot is internal");
            Assert.AreEqual(typeof(bool), m.ReturnType);
            CollectionAssert.AreEqual(new[] { typeof(uint) }, m.GetParameters().Select(p => p.ParameterType).ToArray());

            // The live rebuild purges through that exact method.
            Assert.AreEqual(m, TrialRebuild.Live().Purge.Method);
        }

        // ================================================================ digest

        private static TrialDigestInput BaseInput() => new TrialDigestInput(
            new[]
            {
                new TrialDigestEquipped(1001, (int)EquipMask.ChestArmor, 10, 10, 5000),
                new TrialDigestEquipped(1002, (int)EquipMask.MeleeWeapon, null, null, 3000),
            },
            new Dictionary<uint, long> { [2001] = 25, [2002] = 1 },
            new Dictionary<string, int> { ["frenzy"] = 3, ["manabarrier"] = 1 },
            1,
            new[] { new TrialDigestEnchantment("p", 2783, true), new TrialDigestEnchantment("p", 5000, false) });

        [TestMethod]
        public void Digest_is_stable_and_each_field_changes_it()
        {
            var a = TrialDigest.Of(BaseInput());
            var b = TrialDigest.Of(BaseInput());
            Assert.AreEqual(a, b);
            Assert.AreEqual(64, a.Core.Length);
            Assert.AreEqual(64, a.Enchantments.Length);

            // Order does not matter: equipped, pack, ranks and enchantments are all sorted.
            var i = BaseInput();
            var shuffled = i with
            {
                Equipped = i.Equipped.Reverse().ToArray(),
                PackStacks = i.PackStacks.Reverse().ToDictionary(k => k.Key, k => k.Value),
                ClassAbilityRanks = i.ClassAbilityRanks.Reverse().ToDictionary(k => k.Key, k => k.Value),
                Enchantments = i.Enchantments.Reverse().ToArray(),
            };
            Assert.AreEqual(a, TrialDigest.Of(shuffled));

            TrialDigestEquipped[] EquippedWith(Func<TrialDigestEquipped, TrialDigestEquipped> f) => new[] { f(i.Equipped[0]), i.Equipped[1] };

            // One mutation per Core field; Enchantments unchanged by each.
            var coreMutations = new (string Field, TrialDigestInput Input)[]
            {
                ("equipped wcid", i with { Equipped = EquippedWith(e => e with { Wcid = 9999 }) }),
                ("equipped location", i with { Equipped = EquippedWith(e => e with { Location = (int)EquipMask.HeadWear }) }),
                ("equipped structure", i with { Equipped = EquippedWith(e => e with { Structure = 9 }) }),
                ("equipped max structure", i with { Equipped = EquippedWith(e => e with { MaxStructure = 11 }) }),
                ("equipped max mana", i with { Equipped = EquippedWith(e => e with { ItemMaxMana = 5001 }) }),
                ("equipped item removed", i with { Equipped = new[] { i.Equipped[0] } }),
                ("pack stack count", i with { PackStacks = new Dictionary<uint, long> { [2001] = 24, [2002] = 1 } }),
                ("pack wcid", i with { PackStacks = new Dictionary<uint, long> { [2001] = 25, [2003] = 1 } }),
                ("class ability rank", i with { ClassAbilityRanks = new Dictionary<string, int> { ["frenzy"] = 2, ["manabarrier"] = 1 } }),
                ("class ability name", i with { ClassAbilityRanks = new Dictionary<string, int> { ["frenzy"] = 3, ["manarush"] = 1 } }),
                ("facet slot", i with { ActiveFacetSlot = 2 }),
            };

            foreach (var (field, input) in coreMutations)
            {
                var d = TrialDigest.Of(input);
                Assert.AreNotEqual(a.Core, d.Core, $"{field} must change Core");
                Assert.AreEqual(a.Enchantments, d.Enchantments, $"{field} must not change Enchantments");
            }

            var enchMutations = new (string Field, TrialDigestInput Input)[]
            {
                ("spell id", i with { Enchantments = new[] { new TrialDigestEnchantment("p", 2784, true), i.Enchantments[1] } }),
                ("caster is self", i with { Enchantments = new[] { new TrialDigestEnchantment("p", 2783, false), i.Enchantments[1] } }),
                ("target", i with { Enchantments = new[] { new TrialDigestEnchantment("i" + (int)EquipMask.ChestArmor, 2783, true), i.Enchantments[1] } }),
                ("enchantment removed", i with { Enchantments = new[] { i.Enchantments[0] } }),
            };

            foreach (var (field, input) in enchMutations)
            {
                var d = TrialDigest.Of(input);
                Assert.AreNotEqual(a.Enchantments, d.Enchantments, $"{field} must change Enchantments");
                Assert.AreEqual(a.Core, d.Core, $"{field} must not change Core");
            }
        }

        private static Clothing Armor(int curMana, int maxMana, int? structure = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1001,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.ChestArmor },
                    { PropertyInt.ItemCurMana, curMana },
                    { PropertyInt.ItemMaxMana, maxMana },
                },
            };

            if (structure.HasValue)
            {
                weenie.PropertiesInt[PropertyInt.Structure] = structure.Value;
                weenie.PropertiesInt[PropertyInt.MaxStructure] = 10;
            }

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void Digest_ignores_current_item_mana()
        {
            var full = TrialDigest.EquippedEntry(Armor(curMana: 5000, maxMana: 5000));
            var drained = TrialDigest.EquippedEntry(Armor(curMana: 12, maxMana: 5000));
            Assert.AreEqual(full, drained, "ItemCurMana is not part of an equipped entry");

            var i = BaseInput();
            Assert.AreEqual(TrialDigest.Of(i with { Equipped = new[] { full } }), TrialDigest.Of(i with { Equipped = new[] { drained } }));

            // Control: the read reaches the item at all (max mana and structure do change the entry).
            Assert.AreEqual(5000, full.ItemMaxMana);
            Assert.AreNotEqual(full, TrialDigest.EquippedEntry(Armor(curMana: 5000, maxMana: 4999)));
            Assert.AreNotEqual(TrialDigest.EquippedEntry(Armor(5000, 5000, structure: 10)), TrialDigest.EquippedEntry(Armor(5000, 5000, structure: 9)));
        }

        // ================================================================ drift and schedule

        private static TrialMasterSnapshot Snapshot(TrialDigest d) => new TrialMasterSnapshot(d, new Dictionary<uint, long>());

        private static TrialRepSetup NewSetup(uint repGuid, int hardCopyEvery, string masterCore)
        {
            var setup = new TrialRepSetup(repGuid, 0x59000003, hardCopyEvery, () => 0.0,
                (guid, callback) => callback(masterCore == null ? null : Snapshot(new TrialDigest(masterCore, "master-enchantments"))),
                action => { action(); return true; });

            setup.RequestMasterDigest();
            return setup;
        }

        private static TrialRepSetup NewSetupWithProfile(uint repGuid, string buffProfile)
        {
            var setup = new TrialRepSetup(repGuid, 0x59000003, 0, () => 0.0,
                (guid, callback) => callback(Snapshot(new TrialDigest("M", "master-enchantments"))),
                action => { action(); return true; }, "S1", buffProfile);

            setup.RequestMasterDigest();
            return setup;
        }

        /// <summary>
        /// Review F3: the block outlives the session, so a later session on the same rep that casts another buff profile (a
        /// scenario override) would read its first rep as drift against the old profile's cast set.
        /// </summary>
        [TestMethod]
        public void A_buff_profile_change_records_the_first_rep_enchantments_again()
        {
            var repGuid = TrialRoster.RepSlots[2].Guid;

            var own = NewSetupWithProfile(repGuid, "ownSpellbook");
            var r1 = own.Before(() => { }, () => new TrialDigest("M", "E-own"));
            Assert.IsFalse(r1.Drift, r1.DriftReason);
            own.OnRepEnded();

            // A second session on the same rep, with the scenario's adminAll:8: a new cast set, not drift.
            var admin = NewSetupWithProfile(repGuid, "adminAll:8");
            var r2 = admin.Before(() => { }, () => new TrialDigest("M", "E-admin"));
            Assert.IsFalse(r2.Drift, r2.DriftReason);
            Assert.AreEqual("E-admin", TrialRepSetup.BlockState(repGuid).FirstEnchantments);
            admin.OnRepEnded();

            var r3 = admin.Before(() => { }, () => new TrialDigest("M", "E-admin"));
            Assert.IsFalse(r3.Drift, r3.DriftReason);
            admin.OnRepEnded();

            // Same profile, changed enchantments: still drift.
            var r4 = admin.Before(() => { }, () => new TrialDigest("M", "E-other"));
            Assert.IsTrue(r4.Drift);
            StringAssert.Contains(r4.DriftReason, "enchantments");

            // And the profile never excuses Core.
            TrialRepSetup.ResetBlock(repGuid);
            var r5 = NewSetupWithProfile(repGuid, "ownSpellbook").Before(() => { }, () => new TrialDigest("X", "E-own"));
            Assert.IsTrue(r5.Drift);
            StringAssert.Contains(r5.DriftReason, "core");
        }

        /// <summary>Review F4: a buff profile the rep cannot take is refused before the soft reset touches it, and at trial start.</summary>
        [TestMethod]
        public void A_buff_profile_refusal_comes_before_the_soft_reset_and_at_start()
        {
            Assert.IsNull(TrialBuffs.Precheck(null, "ownSpellbook"));
            Assert.IsNull(TrialBuffs.Precheck(null, null), "no profile: ownSpellbook");
            StringAssert.Contains(TrialBuffs.Precheck(null, "adminAll:9"), "adminAll:9");

            var guid = TrialRoster.RepSlots[6].Guid;
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            GuidField(player) = new ObjectGuid(guid);
            StringAssert.Contains(TrialBuffs.Precheck(player, "adminAll:8"), "session", "no session: refused");

            var session = new TrialSession("u", new TrialScenario("s", "r", null, 1, null, null, 1, 60, 0, "adminAll:8"), "sha", null, "asha", null, guid,
                new TrialSessionWorld(), _ => null);
            player.TrialSession = session;

            var setup = NewSetupWithProfile(guid, "adminAll:8");

            try
            {
                TrialManager.OnlinePlayer = g => g == guid ? player : null;

                var r = TrialStarter.PreSpawn(session, setup, Archetype);
                StringAssert.Contains(r.Refusal, "session", "the scenario's adminAll:8 wins over the archetype's ownSpellbook, and is refused");
                Assert.AreEqual((0, (string)null), TrialRepSetup.BlockState(guid), "Before never ran");
            }
            finally
            {
                TrialManager.ResetForTests();
                player.TrialSession = null;
            }

            var pre = Body(StarterPath, PreSpawnSig);
            Assert.IsTrue(pre.IndexOf("TrialBuffs.Precheck(player, buffProfile)", StringComparison.Ordinal) < pre.IndexOf("setup.Before(", StringComparison.Ordinal));
            StringAssert.Contains(Body(StarterPath, TryStartSig),
                "var buffProfile = TrialBuffs.EffectiveProfile(scenario, archetype); var buffRefusal = TrialBuffs.Precheck(player, buffProfile); if (buffRefusal != null) return Refuse(buffRefusal, out refusal);");
        }

        [TestMethod]
        public void Drift_rule_core_vs_master_and_enchantments_vs_block()
        {
            // The pure rule.
            Assert.IsFalse(TrialDigest.IsDrift(new TrialDigest("M", "E"), "M", "E", out var reason), reason);
            Assert.IsTrue(TrialDigest.IsDrift(new TrialDigest("X", "E"), "M", "E", out reason));
            StringAssert.Contains(reason, "core");
            Assert.IsTrue(TrialDigest.IsDrift(new TrialDigest("M", "F"), "M", "E", out reason));
            StringAssert.Contains(reason, "enchantments");

            // The master's own (unbuffed) enchantments never count: Core only is compared to the master.
            var repGuid = TrialRoster.RepSlots[0].Guid;
            var setup = NewSetup(repGuid, hardCopyEvery: 0, masterCore: "M");

            var r1 = setup.Before(() => { }, () => new TrialDigest("M", "E1"));
            Assert.IsNull(r1.Refusal);
            Assert.IsFalse(r1.Drift, r1.DriftReason);
            Assert.AreEqual("M", r1.Digest.Core);
            setup.OnRepEnded();

            // Same core, same enchantments as the block's first rep: no drift.
            var r2 = setup.Before(() => { }, () => new TrialDigest("M", "E1"));
            Assert.IsFalse(r2.Drift, r2.DriftReason);
            setup.OnRepEnded();

            // Enchantments differ from the block's first rep: drift (the rep still runs, flagged).
            var r3 = setup.Before(() => { }, () => new TrialDigest("M", "E2"));
            Assert.IsTrue(r3.Drift);
            Assert.IsNull(r3.Refusal);
            Assert.IsFalse(r3.RebuildRequired, "drift flags this rep; the rebuild is for the NEXT one");

            // r3's drift owes a rebuild: the next rep is refused until one completes, then a new block starts.
            Assert.IsTrue(setup.Before(() => { }, () => new TrialDigest("M", "E1")).RebuildRequired);
            TrialRepSetup.ResetBlock(repGuid);

            // Core differs from the master: drift.
            var r4 = setup.Before(() => { }, () => new TrialDigest("X", "E1"));
            Assert.IsTrue(r4.Drift);

            // A drift rep runs flagged, and the session refuses the next rep until a rebuild.
            var w = new TrialSessionWorld();
            var drift = true;
            var session = new TrialSession("u", new TrialScenario("s", "r", null, 1, null, null, 3, 60, 0), "sha", null, "asha", null, 1u, w,
                n => new TrialRep { Waves = new[] { (IReadOnlyList<DungeonSpawnPlanEntry>)new List<DungeonSpawnPlanEntry>() }, StateDigest = "M", DigestDrift = drift });
            session.Tick(0);
            Assert.IsTrue(session.TryNext(1, out var refusal), refusal);
            Assert.IsTrue(session.CurrentRep.DigestDrift);
            Assert.IsTrue(session.RebuildRequired);
            foreach (var g in w.Spawned) w.Dead.Add(g);
            session.OnCreatureDied(0, 2);
            Assert.AreEqual(TrialPhase.Ready, session.Phase);
            drift = false;
            Assert.IsFalse(session.TryNext(3, out refusal));
            StringAssert.Contains(refusal, "rebuild");
        }

        [TestMethod]
        public void Pending_or_failed_master_digest_refuses_the_rep_before_any_reset()
        {
            var repGuid = TrialRoster.RepSlots[1].Guid;
            var now = 0.0;
            var requests = 0;
            Action<TrialMasterSnapshot> pendingCallback = null;

            var setup = new TrialRepSetup(repGuid, 0x59000003, 0, () => now,
                (guid, callback) => { requests++; pendingCallback = callback; },
                action => { action(); return true; });
            setup.RequestMasterDigest();
            Assert.AreEqual(1, requests);

            var resets = 0;
            var r = setup.Before(() => resets++, () => new TrialDigest("M", "E"));
            StringAssert.Contains(r.Refusal, "master digest pending");
            Assert.AreEqual(0, resets, "a pending master refuses before the soft reset");
            Assert.AreEqual(1, requests, "inside the timeout, no second request");

            // No callback at all (a swallowed shard read): after the timeout the next rep asks again.
            now = TrialRepSetup.MasterDigestTimeoutSeconds + 1;
            r = setup.Before(() => resets++, () => new TrialDigest("M", "E"));
            StringAssert.Contains(r.Refusal, "master digest");
            Assert.AreEqual(2, requests);

            // A null answer (master online or missing) is unavailable, and asks again next rep.
            pendingCallback(null);
            r = setup.Before(() => resets++, () => new TrialDigest("M", "E"));
            StringAssert.Contains(r.Refusal, "master digest unavailable");
            Assert.AreEqual(3, requests);

            // The answer arrives: the rep runs.
            Assert.IsNull(setup.MasterConsumables, "no master consumables before the snapshot");
            pendingCallback(new TrialMasterSnapshot(new TrialDigest("M", "master"), new Dictionary<uint, long> { [691] = 50 }));
            IReadOnlyDictionary<uint, long> seenAtReset = null;
            r = setup.Before(() => { resets++; seenAtReset = setup.MasterConsumables; }, () => new TrialDigest("M", "E"));
            Assert.IsNull(r.Refusal);
            Assert.AreEqual(1, resets);
            Assert.IsFalse(r.Drift, r.DriftReason);
            Assert.AreEqual(50, seenAtReset[691], "the soft reset sees the master's consumable counts");
        }

        [TestMethod]
        public void HardCopyEvery_sets_RebuildRequired_on_schedule()
        {
            var repGuid = TrialRoster.RepSlots[2].Guid;
            var setup = NewSetup(repGuid, hardCopyEvery: 2, masterCore: "M");
            var resets = 0;

            TrialRepSetupResult Run() => setup.Before(() => resets++, () => new TrialDigest("M", "E"));

            var r = Run();
            Assert.IsFalse(r.RebuildRequired);
            setup.OnRepEnded();
            r = Run();
            Assert.IsFalse(r.RebuildRequired);
            setup.OnRepEnded();

            // Two reps done since the last copy: the third is refused for a rebuild, before any reset.
            r = Run();
            Assert.IsTrue(r.RebuildRequired);
            StringAssert.Contains(r.Refusal, "hard copy");
            Assert.AreEqual(2, resets);

            // A rebuild starts a new block.
            TrialRepSetup.ResetBlock(repGuid);
            Assert.IsFalse(Run().RebuildRequired);

            // HardCopyEvery = 0: re-copy on drift only, never on schedule.
            var never = NewSetup(TrialRoster.RepSlots[3].Guid, hardCopyEvery: 0, masterCore: "M");
            for (var k = 0; k < 20; k++)
            {
                Assert.IsFalse(never.Before(() => { }, () => new TrialDigest("M", "E")).RebuildRequired);
                never.OnRepEnded();
            }

            // The block outlives the session: one rep ended in the old session, one in a new session on the same rep.
            setup.OnRepEnded();
            var again = NewSetup(repGuid, hardCopyEvery: 2, masterCore: "M");
            Assert.IsFalse(again.Before(() => { }, () => new TrialDigest("M", "E")).RebuildRequired);
            again.OnRepEnded();
            Assert.IsTrue(again.Before(() => { }, () => new TrialDigest("M", "E")).RebuildRequired);
        }

        // ================================================================ reset and buff selectors

        [TestMethod]
        public void Soft_reset_selectors_split_cooldowns_harmful_and_kept_entries()
        {
            var cooldown = new PropertiesEnchantmentRegistry { SpellId = 0x8000 | 100, SpellCategory = (SpellCategory)0x8000, StatModType = EnchantmentTypeFlags.Cooldown, Duration = 45 };
            var harmful = new PropertiesEnchantmentRegistry { SpellId = 1500, StatModType = EnchantmentTypeFlags.Skill, Duration = 60 };
            var beneficial = new PropertiesEnchantmentRegistry { SpellId = 2783, StatModType = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Beneficial, Duration = 3600 };
            var itemSpell = new PropertiesEnchantmentRegistry { SpellId = 1600, StatModType = EnchantmentTypeFlags.Skill, Duration = -1 };

            Assert.IsTrue(TrialReset.IsCooldownEntry(cooldown));
            Assert.IsFalse(TrialReset.IsCooldownEntry(harmful));
            Assert.IsFalse(TrialReset.IsCooldownEntry(beneficial));

            // F1b: every carried timed entry goes, beneficial included; cooldowns, permanent entries and worn-item casts stay.
            const uint wornGuid = 0x80FF0001;
            var worn = new HashSet<uint> { wornGuid };
            var wornCast = new PropertiesEnchantmentRegistry { SpellId = 1700, StatModType = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Beneficial, Duration = 30, CasterObjectId = wornGuid };
            var potion = new PropertiesEnchantmentRegistry { SpellId = 2783, StatModType = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Beneficial, Duration = 900, CasterObjectId = 0x80FF0099 };

            Assert.IsTrue(TrialReset.IsCarriedTimedEnchantment(harmful, worn));
            Assert.IsTrue(TrialReset.IsCarriedTimedEnchantment(beneficial, worn), "a carried beneficial buff goes too");
            Assert.IsTrue(TrialReset.IsCarriedTimedEnchantment(potion, worn), "a potion's buff (its caster is gone) goes");
            Assert.IsFalse(TrialReset.IsCarriedTimedEnchantment(cooldown, worn));
            Assert.IsFalse(TrialReset.IsCarriedTimedEnchantment(itemSpell, worn), "an item's own spell stays while the item is worn");
            Assert.IsFalse(TrialReset.IsCarriedTimedEnchantment(wornCast, worn), "an entry cast by a worn item is item-sourced");

            // Cooldown entries never reach the Enchantments digest.
            var entries = TrialDigest.EnchantmentEntries("p", new[] { cooldown, beneficial }, 0x50000001);
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(2783, entries[0].SpellId);
        }

        [TestMethod]
        public void Own_spellbook_takes_the_highest_beneficial_self_or_item_spell_per_category()
        {
            TrialSpellFacts F(uint id, SpellCategory cat, uint level, bool beneficial = true, bool enchantment = true, bool self = true, bool item = false, bool fellow = false)
                => new TrialSpellFacts(id, cat, level, level * 10, beneficial, enchantment, self, item, fellow);

            var picked = TrialBuffs.SelectOwnSpellbook(new[]
            {
                F(1, SpellCategory.StrengthRaising, 6),
                F(2, SpellCategory.StrengthRaising, 7),                      // the highest of its category
                F(3, SpellCategory.StrengthRaising, 8, self: false),         // Other: not self, not an item spell
                F(4, SpellCategory.ArmorValueRaising, 7, self: false, item: true), // impen on own armor
                F(5, SpellCategory.StrengthLowering, 7, beneficial: false),     // harmful
                F(6, SpellCategory.HealthRaising, 7, enchantment: false),    // a boost, not an enchantment
                F(7, SpellCategory.QuicknessRaising, 7, fellow: true),       // fellowship
            });

            CollectionAssert.AreEquivalent(new uint[] { 2, 4 }, picked.ToArray());
        }


        // ================================================================ fix round 1

        [TestMethod]
        public void Rebuild_failed_or_silent_copy_leaves_the_block_and_the_flag()
        {
            const int repIndex = 6;
            var rep = TrialRoster.RepSlots[repIndex - 1];

            TrialRepSetup.MarkRebuildRequired(rep.Guid, "drift");
            var setup = NewSetup(rep.Guid, hardCopyEvery: 0, masterCore: "M");
            setup.OnRepEnded();
            Assert.AreEqual((1, (string)null), TrialRepSetup.BlockState(rep.Guid));

            // The copy reports failure: the purge ran, but the block and the owed rebuild are unchanged.
            var failing = Healthy(repIndex);
            failing.CopyResult = false;
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, failing.Build(), out var refusal), refusal);
            Assert.AreEqual(1, failing.Copies.Count);
            Assert.AreEqual((1, (string)null), TrialRepSetup.BlockState(rep.Guid));
            Assert.IsTrue(TrialRepSetup.NeedsRebuild(rep.Guid));

            // The copy never reports (a swallowed shard read): unchanged too.
            var silent = Healthy(repIndex);
            silent.CopyResult = null;
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, silent.Build(), out refusal), refusal);
            Assert.IsTrue(TrialRepSetup.NeedsRebuild(rep.Guid));
            Assert.AreEqual((1, (string)null), TrialRepSetup.BlockState(rep.Guid));

            // Control: a copy that lands resets the block and clears the flag.
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype, ArchSha, repIndex, Healthy(repIndex).Build(), out refusal), refusal);
            Assert.IsFalse(TrialRepSetup.NeedsRebuild(rep.Guid));
            Assert.AreEqual((0, (string)null), TrialRepSetup.BlockState(rep.Guid));
        }

        [TestMethod]
        public void Drift_owes_a_rebuild_that_a_new_session_cannot_bypass()
        {
            var repGuid = TrialRoster.RepSlots[4].Guid;

            var first = NewSetup(repGuid, hardCopyEvery: 0, masterCore: "M");
            var r = first.Before(() => { }, () => new TrialDigest("X", "E"));
            Assert.IsTrue(r.Drift);
            Assert.IsNull(r.Refusal, "the drifted rep itself runs, flagged");
            first.OnRepEnded();

            // The rep relogs: a new session for the same rep guid. Its rep 1 is refused, before any reset.
            var resets = 0;
            var second = NewSetup(repGuid, hardCopyEvery: 0, masterCore: "M");
            r = second.Before(() => resets++, () => new TrialDigest("M", "E"));
            Assert.IsTrue(r.RebuildRequired);
            StringAssert.Contains(r.Refusal, "rebuild required");
            StringAssert.Contains(r.Refusal, "drift");
            Assert.AreEqual(0, resets);

            // Only a completed rebuild (ResetBlock) clears it.
            TrialRepSetup.ResetBlock(repGuid);
            r = second.Before(() => resets++, () => new TrialDigest("M", "E"));
            Assert.IsFalse(r.RebuildRequired);
            Assert.IsNull(r.Refusal);
            Assert.AreEqual(1, resets);
        }

        [TestMethod]
        public void Master_digest_loads_never_stack()
        {
            var now = 0.0;
            var requests = 0;
            var setup = new TrialRepSetup(TrialRoster.RepSlots[6].Guid, 0x59000003, 0, () => now, (g, cb) => requests++, a => { a(); return true; });

            setup.RequestMasterDigest();
            setup.RequestMasterDigest();
            setup.Before(() => { }, () => new TrialDigest("M", "E"));
            setup.Before(() => { }, () => new TrialDigest("M", "E"));
            Assert.AreEqual(1, requests, "one load in flight at a time");

            now = TrialRepSetup.MasterDigestTimeoutSeconds + 1;
            setup.RequestMasterDigest();
            Assert.AreEqual(2, requests, "past the timeout the load counts as dead");
            setup.RequestMasterDigest();
            Assert.AreEqual(2, requests);
        }

        [TestMethod]
        public void Digest_is_culture_invariant()
        {
            var i = BaseInput() with
            {
                // Negative values are where a culture's sign and digit forms would differ.
                Equipped = new[] { new TrialDigestEquipped(1001, (int)EquipMask.ChestArmor, -5, 10, -1) },
                PackStacks = new Dictionary<uint, long> { [2001] = -25, [2002] = 1234567 },
            };

            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                var invariant = TrialDigest.Of(i);
                var invariantText = TrialDigest.CoreText(i);

                foreach (var name in new[] { "de-DE", "ar-SA" })
                {
                    CultureInfo.CurrentCulture = new CultureInfo(name);
                    Assert.AreEqual(invariantText, TrialDigest.CoreText(i), name);
                    Assert.AreEqual(invariant, TrialDigest.Of(i), name);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        // ================================================================ final review fix wave

        private const uint CompWcid = 691;      // a spell component
        private const uint ArrowWcid = 300;     // wielded ammunition
        private const uint LiftWcid = 2002;     // an archetype consumable whose weenie type is not a consumable type
        private const uint GemWcid = 2001;      // an ordinary pack item

        private static readonly IReadOnlyCollection<uint> LiftWcids = new HashSet<uint> { LiftWcid };

        /// <summary>A digest input from possessions, the way Project builds one: equipped entries plus the stack split.</summary>
        private static TrialDigestInput InputFrom(IEnumerable<TrialDigestEquipped> equipped, IEnumerable<TrialDigestStack> stacks)
        {
            var (core, consumables) = TrialDigest.SplitStacks(stacks, LiftWcids);
            return new TrialDigestInput(equipped.ToList(), core, new Dictionary<string, int> { ["frenzy"] = 3 }, 1,
                new[] { new TrialDigestEnchantment("p", 2783, true) }, consumables);
        }

        private static readonly TrialDigestEquipped[] Kit =
        {
            new TrialDigestEquipped(1001, (int)EquipMask.ChestArmor, 10, 10, 5000),
            new TrialDigestEquipped(1002, (int)EquipMask.MeleeWeapon, null, null, 3000),
        };

        [TestMethod]
        public void Consumable_burn_and_restock_never_drift_but_a_kit_change_does()
        {
            var master = InputFrom(Kit, new[]
            {
                new TrialDigestStack(GemWcid, WeenieType.Generic, 1),
                new TrialDigestStack(CompWcid, WeenieType.SpellComponent, 50),
                new TrialDigestStack(ArrowWcid, WeenieType.Ammunition, 200),
            });
            var masterDigest = TrialDigest.Of(master);

            // The rep after a fight: components burned, arrows shot, food eaten, the archetype's consumable topped up.
            var burned = InputFrom(Kit, new[]
            {
                new TrialDigestStack(GemWcid, WeenieType.Generic, 1),
                new TrialDigestStack(CompWcid, WeenieType.SpellComponent, 47),
                new TrialDigestStack(ArrowWcid, WeenieType.Ammunition, 12),
                new TrialDigestStack(4000, WeenieType.Food, 3),
                new TrialDigestStack(LiftWcid, WeenieType.Generic, 10),
            });

            // The same rep after the soft reset restocked to the master's counts.
            var restocked = InputFrom(Kit, new[]
            {
                new TrialDigestStack(GemWcid, WeenieType.Generic, 1),
                new TrialDigestStack(CompWcid, WeenieType.SpellComponent, 30),
                new TrialDigestStack(CompWcid, WeenieType.SpellComponent, 20),
                new TrialDigestStack(ArrowWcid, WeenieType.Ammunition, 200),
                new TrialDigestStack(LiftWcid, WeenieType.Generic, 10),
            });

            foreach (var (name, rep) in new[] { ("burned", burned), ("restocked", restocked) })
            {
                var d = TrialDigest.Of(rep);
                Assert.IsFalse(TrialDigest.IsDrift(d, masterDigest.Core, d.Enchantments, out var reason), $"{name}: {reason}");
            }

            // The master's consumable counts ride beside the digest, for the soft reset to restore to.
            Assert.AreEqual(50, master.Consumables[CompWcid]);
            Assert.AreEqual(200, master.Consumables[ArrowWcid]);
            Assert.AreEqual(50, restocked.Consumables[CompWcid], "stacks are summed by wcid");
            Assert.IsFalse(master.PackStacks.ContainsKey(CompWcid));
            Assert.IsTrue(master.PackStacks.ContainsKey(GemWcid), "an ordinary pack item is still Core");

            // A non-consumable change still drifts: a different wielded item, and a lost pack item.
            var otherWeapon = InputFrom(new[] { Kit[0], Kit[1] with { Wcid = 1003 } }, new[]
            {
                new TrialDigestStack(GemWcid, WeenieType.Generic, 1),
                new TrialDigestStack(CompWcid, WeenieType.SpellComponent, 50),
            });
            Assert.IsTrue(TrialDigest.IsDrift(TrialDigest.Of(otherWeapon), masterDigest.Core, masterDigest.Enchantments, out var why));
            StringAssert.Contains(why, "core");

            var lostGem = InputFrom(Kit, new[] { new TrialDigestStack(CompWcid, WeenieType.SpellComponent, 50) });
            Assert.IsTrue(TrialDigest.IsDrift(TrialDigest.Of(lostGem), masterDigest.Core, masterDigest.Enchantments, out _));
        }

        /// <summary>A salvage bag the way the shard holds one: weenie type CraftTool (44), ItemType TinkeringMaterial, units in Structure.</summary>
        private static WorldObject SalvageBag(uint wcid, int structure)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.CraftTool,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial },
                    { PropertyInt.StackSize, 1 },
                    { PropertyInt.MaxStructure, 100 },
                    { PropertyInt.Structure, structure },
                },
            };

            return new Stackable(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject PackStack(uint wcid, int? stackSize)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Misc } },
            };

            if (stackSize.HasValue)
                weenie.PropertiesInt[PropertyInt.StackSize] = stackSize.Value;

            return new Stackable(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>
        /// Stage, 2026-11-22: a fresh copy drifted on its first rep. Between login and the first rep the rep's client
        /// combined its partial salvage bags (Player.HandleSalvaging, the only path that makes salvage bags out of salvage
        /// bags), so every material held the same units in fewer bags, and Core, which counted one per bag, differed from
        /// the master's. The bag splits here are the stage master's and rep's, for granite (20985) and steel (20993).
        /// </summary>
        [TestMethod]
        public void Salvage_repack_with_equal_units_is_not_drift_but_a_unit_change_is()
        {
            const uint Granite = 20985, Steel = 20993;

            IEnumerable<TrialDigestStack> Holding(int[] granite, int[] steel, int gemStack = 1)
                => granite.Select(s => SalvageBag(Granite, s)).Concat(steel.Select(s => SalvageBag(Steel, s)))
                    .Append(PackStack(GemWcid, gemStack)).Select(TrialDigest.StackOf).ToList();

            var fullSteel = Enumerable.Repeat(100, 5);

            var master = InputFrom(Kit, Holding(new[] { 4, 22, 54, 91, 100 }, new[] { 8, 16, 18, 69 }.Concat(fullSteel).ToArray()));
            var masterDigest = TrialDigest.Of(master);

            var repacked = InputFrom(Kit, Holding(new[] { 71, 100, 100 }, new[] { 11 }.Concat(Enumerable.Repeat(100, 6)).ToArray()));
            var d = TrialDigest.Of(repacked);
            Assert.IsFalse(TrialDigest.IsDrift(d, masterDigest.Core, d.Enchantments, out var reason), reason);
            Assert.AreEqual(271L, master.PackStacks[Granite], "a material counts its total units, not its bags");
            Assert.AreEqual(611L, repacked.PackStacks[Steel]);

            // One unit of granite fewer is drift: the material itself changed, not just its packing.
            var lostUnit = InputFrom(Kit, Holding(new[] { 70, 100, 100 }, new[] { 11 }.Concat(Enumerable.Repeat(100, 6)).ToArray()));
            Assert.IsTrue(TrialDigest.IsDrift(TrialDigest.Of(lostUnit), masterDigest.Core, masterDigest.Enchantments, out var why));
            StringAssert.Contains(why, "core");

            // Everything that is not salvage still counts its StackSize (1 when it has none).
            Assert.AreEqual(5L, TrialDigest.StackOf(PackStack(GemWcid, 5)).Stack);
            Assert.AreEqual(1L, TrialDigest.StackOf(PackStack(GemWcid, null)).Stack);
            var moreGems = InputFrom(Kit, Holding(new[] { 4, 22, 54, 91, 100 }, new[] { 8, 16, 18, 69 }.Concat(fullSteel).ToArray(), gemStack: 2));
            Assert.IsTrue(TrialDigest.IsDrift(TrialDigest.Of(moreGems), masterDigest.Core, masterDigest.Enchantments, out _));
        }

        [TestMethod]
        public void Consumable_types_and_archetype_wcids_are_the_consumables()
        {
            foreach (var t in new[] { WeenieType.SpellComponent, WeenieType.Food, WeenieType.Healer, WeenieType.ManaStone, WeenieType.Ammunition })
                Assert.IsTrue(TrialDigest.IsConsumableType(t), t.ToString());
            foreach (var t in new[] { WeenieType.Generic, WeenieType.Clothing, WeenieType.MeleeWeapon, WeenieType.Caster, WeenieType.Container, WeenieType.Gem })
                Assert.IsFalse(TrialDigest.IsConsumableType(t), t.ToString());

            // The archetype's LiftUp.Consumables wcids are what the digest and the reset receive.
            var a = Archetype with
            {
                LiftUp = new TrialLiftUp(null, null, null, null, null, new[] { new TrialConsumable(LiftWcid, 10), new TrialConsumable(CompWcid, 25) }),
            };
            CollectionAssert.AreEquivalent(new[] { LiftWcid, CompWcid }, TrialDigest.ConsumableWcids(a).ToArray());
            Assert.AreEqual(0, TrialDigest.ConsumableWcids(Archetype).Count, "no LiftUp, no extra consumables");
            Assert.IsTrue(TrialDigest.IsConsumable(LiftWcid, WeenieType.Generic, TrialDigest.ConsumableWcids(a)));
            Assert.IsFalse(TrialDigest.IsConsumable(LiftWcid, WeenieType.Generic, TrialDigest.ConsumableWcids(Archetype)));
        }

        [TestMethod]
        public void Consumable_targets_restore_to_the_master_and_lift_up_wins()
        {
            var master = new Dictionary<uint, long> { [CompWcid] = 50, [ArrowWcid] = 200, [LiftWcid] = 3 };
            var targets = TrialReset.ConsumableTargets(new[] { new TrialConsumable(LiftWcid, 10) }, master, new uint[] { CompWcid, 4000 });

            CollectionAssert.AreEqual(new[]
            {
                new TrialConsumableTarget(ArrowWcid, 200, true),   // the master's count, trimmed when over
                new TrialConsumableTarget(CompWcid, 50, true),
                new TrialConsumableTarget(LiftWcid, 10, false),    // the archetype's stack, top-up only, over the master's 3
                new TrialConsumableTarget(4000, 0, true),          // food the master does not hold: trimmed to nothing
            }, targets.ToArray());
        }

        [TestMethod]
        public void Seed_walk_outlives_the_session_and_a_rebuild()
        {
            var repGuid = TrialRoster.RepSlots[2].Guid;
            var gem = new TrialGem(400, 6, "filos_doom", "f", null, 100, TrialSeedMode.Step);

            TrialRepSetup Session(string sha) => new TrialRepSetup(repGuid, 0x59000003, 3, () => 0.0, (g, cb) => { }, a => { a(); return true; }, sha);

            var first = Session("S1");
            Assert.AreEqual(1, first.SeedRep());
            Assert.AreEqual(1, first.SeedRep(), "reading the index does not step it");
            first.OnRepEnded();
            first.OnRepEnded();
            Assert.AreEqual(3, first.SeedRep());

            // hardCopyEvery forces a rebuild: the block resets (the session ends with the logout), the walk does not.
            TrialRepSetup.ResetBlock(repGuid);
            var second = Session("S1");
            var k = second.SeedRep();
            Assert.AreEqual(3, k, "a second session carries on where the first stopped");
            Assert.AreEqual(100 + (k - 1), TrialGemRoller.RepSeed(gem, k), "the second session rolls seed + k");
            Assert.AreEqual((0, (string)null), TrialRepSetup.BlockState(repGuid), "control: the block itself did reset");

            // A different scenario starts its own walk at 1; another rep character is untouched.
            var other = Session("S2");
            Assert.AreEqual(1, other.SeedRep());
            other.OnRepEnded();
            Assert.AreEqual(2, other.SeedRep());
            Assert.AreEqual(1, new TrialRepSetup(TrialRoster.RepSlots[3].Guid, 0x59000003, 0, () => 0.0, (g, cb) => { }, a => { a(); return true; }, "S1").SeedRep());
        }

        // ---- source pins: the consumer sites of the trial data fields (comments stripped, whitespace collapsed)

        private static string Norm(string body) => System.Text.RegularExpressions.Regex.Replace(Code(body), @"\s+", " ");

        private static string Body(string path, string signature) => Norm(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(path), signature));

        private const string StarterPath = "Source/ACE.Server/BalanceTrials/TrialStarter.cs";
        private const string ResetPath = "Source/ACE.Server/BalanceTrials/TrialReset.cs";
        private const string TryStartSig = "public static bool TryStart(Player player, string scenarioId, string archetypeId, out string refusal)";
        private const string PrepareRepSig = "internal static TrialRep PrepareRep(TrialScenario scenario, TrialRoom room, int n, string tag, Func<TrialRepSetupResult> preSpawn = null, int? seedRep = null)";
        private const string PreSpawnSig = "internal static TrialRepSetupResult PreSpawn(TrialSession session, TrialRepSetup setup, TrialArchetype archetype)";
        private const string SoftSig = "public static TrialSoftResetSummary Soft(Player p, TrialArchetype a, IReadOnlyDictionary<uint, long> masterConsumables, string buffProfile)";

        [TestMethod]
        public void Room_exclude_rows_reach_the_private_copy()
            => StringAssert.Contains(Body(StarterPath, TryStartSig),
                "landblock = RealmManager.GetNewEphemeralLandblock(new LandblockId(((uint)room.Landblock << 16) | 0xFFFF), player, realmId: 0, openToFellowship: false, excludedRows: room.ExcludeRows);");

        [TestMethod]
        public void Scenario_hard_copy_every_and_seed_walk_reach_the_rep_setup()
        {
            var start = Body(StarterPath, TryStartSig);
            StringAssert.Contains(start, "var consumableWcids = TrialDigest.ConsumableWcids(archetype); var repSetup = new TrialRepSetup(player.Guid.Full, TrialRoster.MasterSlot(archetype.Slot), scenario.HardCopyEvery, TrialManager.Now, (master, callback) => MasterSnapshotLoader(master, consumableWcids, callback), action => TrialManager.Enqueue(session, action), scenarioSha, buffProfile);");
            StringAssert.Contains(start, "n => PrepareRep(scenario, room, n, tag, () => PreSpawn(session, repSetup, archetype), repSetup.SeedRep()));");
            StringAssert.Contains(Body(StarterPath, PrepareRepSig), "var gem = TrialGemRoller.RollLive(scenario.Gem, seedRep ?? n);");
        }

        [TestMethod]
        public void Archetype_consumables_and_master_counts_reach_the_soft_reset_and_digest()
        {
            StringAssert.Contains(Body(StarterPath, PreSpawnSig),
                "var consumableWcids = TrialDigest.ConsumableWcids(archetype); return setup.Before(() => TrialReset.Soft(player, archetype, setup.MasterConsumables, buffProfile), () => TrialDigest.Compute(player, consumableWcids));");

            var soft = Body(ResetPath, SoftSig);
            StringAssert.Contains(soft, "var consumableWcids = TrialDigest.ConsumableWcids(a); var held = p.GetAllPossessions().Where(i => TrialDigest.IsConsumable(i.WeenieClassId, i.WeenieType, consumableWcids)).Select(i => i.WeenieClassId);");
            StringAssert.Contains(soft, "foreach (var t in ConsumableTargets(a.LiftUp?.Consumables, masterConsumables, held)) { var (added, removed) = p.RestoreTrialConsumable(t.Wcid, t.Target, t.Trim);");
        }

        [TestMethod]
        public void Buff_profile_reaches_the_soft_reset_after_every_carried_enchantment_is_removed()
        {
            var soft = Body(ResetPath, SoftSig);
            var removal = "var carried = registry.Where(e => IsCarriedTimedEnchantment(e, wornGuids)).ToList(); if (carried.Count > 0) p.EnchantmentManager.Dispel(carried);";
            var apply = "var cast = TrialBuffs.Apply(p, buffProfile);";
            StringAssert.Contains(soft, removal);
            StringAssert.Contains(soft, apply);
            Assert.IsTrue(soft.IndexOf(removal, StringComparison.Ordinal) < soft.IndexOf(apply, StringComparison.Ordinal), "carried enchantments go before the profile is cast");
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Biota>k__BackingField")]
        private static extern ref Biota BiotaField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BiotaDatabaseLock")]
        private static extern ref System.Threading.ReaderWriterLockSlim LockField(WorldObject wo);

        [TestMethod]
        public void Pre_spawn_step_makes_a_non_attackable_rep_attackable()
        {
            var guid = TrialRoster.RepSlots[7].Guid;
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            GuidField(player) = new ObjectGuid(guid);
            BiotaField(player) = new Biota { PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.Attackable] = false } };
            LockField(player) = new System.Threading.ReaderWriterLockSlim();
            Assert.IsFalse(player.Attackable, "fixture: the elevated Admin rep starts non-attackable");

            var session = new TrialSession("u", new TrialScenario("s", "r", null, 1, null, null, 1, 60, 0), "sha", null, "asha", null, guid,
                new TrialSessionWorld(), _ => null);
            player.TrialSession = session;

            // The master digest never answers, so the step refuses before the soft reset (which needs a full Player).
            var setup = new TrialRepSetup(guid, 0x59000003, 0, () => 0.0, (g, cb) => { }, a => { a(); return true; });
            setup.RequestMasterDigest();

            try
            {
                TrialManager.OnlinePlayer = g => g == guid ? player : null;

                var r = TrialStarter.PreSpawn(session, setup, Archetype);
                StringAssert.Contains(r.Refusal, "master digest pending");
                Assert.IsTrue(player.Attackable, "the pre-spawn step forces the rep attackable");

                // Trial start forces it too.
                var start = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/BalanceTrials/TrialStarter.cs"),
                    "public static bool TryStart(Player player, string scenarioId, string archetypeId, out string refusal)"));
                StringAssert.Contains(start, "TrialReset.EnsureAttackable(player);");
            }
            finally
            {
                TrialManager.ResetForTests();
                player.TrialSession = null;
            }

            // The helper itself: an attackable creature is left alone.
            Assert.IsFalse(TrialReset.EnsureAttackable(player));
        }

        private static string Code(string body) => System.Text.RegularExpressions.Regex.Replace(body, @"//[^\n]*", "");

        /// <summary>A minimal ITrialWorld for the drift-to-session check.</summary>
        private sealed class TrialSessionWorld : ITrialWorld
        {
            public bool RoomLoaded => true;
            public bool PlayerOnline => true;
            public bool PlayerInRoom => true;
            public HashSet<uint> Dead = new HashSet<uint>();
            public List<uint> Spawned = new List<uint>();
            private uint _next = 500;

            public int LiveForeignCreatures() => 0;
            public bool IsAlive(uint guid) => !Dead.Contains(guid);
            public void DestroyAll() { }
            public void ReturnPlayerToStart() { }

            public IReadOnlyList<uint> SpawnWave(IReadOnlyList<DungeonSpawnPlanEntry> wave, TrialRep rep)
            {
                var g = _next++;
                Spawned.Add(g);
                return new[] { g };
            }
        }
    }
}
