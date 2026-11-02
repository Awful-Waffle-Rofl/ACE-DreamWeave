using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// /pvptemplate kit, the live flow (PvpTemplateKitEditService) against a fake store that holds the character's rows. The failure
    /// fakes fail PART WAY: earlier saves really land before the failing one, so a rollback that only skipped the remaining writes
    /// would leave rows behind and the "exactly as it was" assertions would catch it.
    /// </summary>
    [TestClass]
    public class PvpTemplateKitEditServiceTests
    {
        private const uint Char = 0x50000001;
        private const string Account = "templates";

        private sealed class FakeHost : IPvpTemplateKitHost
        {
            public PvpKitCharacter Character = new PvpKitCharacter { Guid = Char, Name = "Pvp Bow", AccountName = Account };
            public readonly Dictionary<uint, PvpKitPossession> Rows = new Dictionary<uint, PvpKitPossession>();
            public readonly Dictionary<uint, PvpKitWeenieInfo> Weenies = new Dictionary<uint, PvpKitWeenieInfo>();
            public readonly List<string> Calls = new List<string>();
            public bool Online;
            public bool ClaimRefused;
            public bool Claimed;
            public int Releases;
            public readonly List<string> Infos = new List<string>();
            /// <summary>1-based index of the read that answers null; 0 = none. Not sticky: the other reads answer normally.</summary>
            public int FailReadNumber;
            /// <summary>1-based index of the create that answers null / throws; 0 = none.</summary>
            public int CreateNullNumber;
            public int CreateThrowNumber;
            /// <summary>1-based index of the save that answers false (and writes nothing); 0 = none.</summary>
            public int FailSaveNumber;
            /// <summary>1-based index of the save call that throws (and writes nothing); 0 = none.</summary>
            public int SaveThrowNumber;
            /// <summary>Remove calls that fail: the first <see cref="PartialRemoveCount"/> ids still go, then false.</summary>
            public int FailRemoveCallNumber;
            public int PartialRemoveCount;
            /// <summary>More failing remove calls: call number -> how many of its ids still go before it answers false.</summary>
            public readonly Dictionary<int, int> FailRemoveCalls = new Dictionary<int, int>();
            /// <summary>1-based index of the remove call that throws (and removes nothing); 0 = none.</summary>
            public int RemoveThrowNumber;
            private uint nextGuid = 0x80001000;
            private int reads;
            private int creates;
            private int saves;
            private int removes;

            public string TemplateAccount => Account;
            public PvpKitCharacter FindCharacter(string name) { Calls.Add("find"); return string.Equals(name, Character.Name, StringComparison.OrdinalIgnoreCase) ? Character : null; }
            public bool IsOnline(uint guid) { Calls.Add("online?"); return Online; }
            public bool TryClaim(uint guid) { Calls.Add("claim"); if (ClaimRefused) return false; Claimed = true; return true; }
            public void Release(uint guid) { Calls.Add("release"); Claimed = false; Releases++; }
            public PvpKitWeenieInfo GetWeenie(uint wcid) => Weenies.TryGetValue(wcid, out var w) ? w : null;

            public void ReadPossessions(uint guid, Action<List<PvpKitPossession>> callback)
            {
                Calls.Add("read");
                reads++;
                callback(reads == FailReadNumber ? null : Rows.Values.Select(Clone).ToList());
            }

            public PvpKitNewItem Create(PvpKitCreation creation)
            {
                Calls.Add("create");
                creates++;

                if (creates == CreateThrowNumber)
                    throw new InvalidOperationException("create threw");

                if (creates == CreateNullNumber)
                    return null;

                return new PvpKitNewItem { Guid = nextGuid++, Creation = creation };
            }

            public void Save(PvpKitNewItem item, Action<bool> callback)
            {
                Calls.Add("save");
                saves++;

                if (saves == SaveThrowNumber)
                    throw new InvalidOperationException("save threw");

                if (saves == FailSaveNumber)
                {
                    callback(false);
                    return;
                }

                var c = item.Creation;
                Rows[item.Guid] = new PvpKitPossession { Guid = item.Guid, Wcid = c.Wcid, StackSize = c.StackSize, ContainerId = c.ContainerId, OwnerId = c.OwnerId, WielderId = c.WielderId, WieldLocation = c.WieldLocation, PlacementPosition = c.PlacementPosition };
                callback(true);
            }

            public void Remove(IReadOnlyCollection<uint> guids, Action<bool> callback)
            {
                Calls.Add("remove");
                removes++;

                if (removes == RemoveThrowNumber)
                    throw new InvalidOperationException("remove threw");

                var partial = removes == FailRemoveCallNumber ? PartialRemoveCount : (int?)null;

                if (FailRemoveCalls.TryGetValue(removes, out var more))
                    partial = more;

                if (partial.HasValue)
                {
                    foreach (var g in guids.Take(partial.Value).ToList())
                        Rows.Remove(g);

                    callback(false);
                    return;
                }

                foreach (var g in guids)
                    Rows.Remove(g);

                callback(true);
            }

            public void OnWorld(Action action) => action();
            /// <summary>1-based index of the LogInfo / LogError call that throws (after recording it); 0 = none.</summary>
            public int LogInfoThrowNumber;
            public int LogErrorThrowNumber;
            public readonly List<string> Errors = new List<string>();
            private int infos;
            private int errors;

            public void LogInfo(string message)
            {
                Calls.Add("info");
                Infos.Add(message);

                if (++infos == LogInfoThrowNumber)
                    throw new InvalidOperationException("LogInfo threw");
            }

            public void LogError(string message, Exception ex = null)
            {
                Calls.Add("error");
                Errors.Add(message);

                if (++errors == LogErrorThrowNumber)
                    throw new InvalidOperationException("LogError threw");
            }

            public static PvpKitPossession Clone(PvpKitPossession p) => new PvpKitPossession
            {
                Guid = p.Guid, Wcid = p.Wcid, Name = p.Name, StackSize = p.StackSize, ContainerId = p.ContainerId, OwnerId = p.OwnerId,
                WielderId = p.WielderId, WieldLocation = p.WieldLocation, PlacementPosition = p.PlacementPosition, IsContainer = p.IsContainer,
            };
        }

        /// <summary>A character with a worn wand, a stack of arrows and two potions; weenies for every op below.</summary>
        private static FakeHost Seeded()
        {
            var host = new FakeHost();

            host.Rows[1] = PvpTemplateKitEditTests.Worn(1, 24207, EquipMask.Held);
            host.Rows[2] = PvpTemplateKitEditTests.Pack(2, 1004133, stack: 80, placement: 0);
            host.Rows[3] = PvpTemplateKitEditTests.Pack(3, 300, placement: 1);
            host.Rows[4] = PvpTemplateKitEditTests.Pack(4, 300, placement: 2);

            foreach (var w in new[]
            {
                PvpTemplateKitEditTests.W(1006769, valid: EquipMask.Held),
                PvpTemplateKitEditTests.W(1006775, maxStack: 250),
                PvpTemplateKitEditTests.W(300, maxStack: 1),
                PvpTemplateKitEditTests.W(1007119),
            })
                host.Weenies[w.Wcid] = w;

            return host;
        }

        private const string FullOps = "24207=1006769 1004133=1006775x100 ~300 +1007119";

        private static (List<string> replies, bool done) Run(FakeHost host, string ops, bool preview = false, string name = "Pvp Bow")
        {
            Assert.IsTrue(PvpTemplateKitEdit.TryParseOps(ops, out var parsed, out var problems), string.Join("; ", problems));

            var replies = new List<string>();
            var done = false;

            new PvpTemplateKitEditService(host).Run(preview, name, parsed, "admin", replies.Add, () => done = true);

            return (replies, done);
        }

        private static string Snapshot(FakeHost host)
            => string.Join("|", host.Rows.Values.OrderBy(r => r.Guid).Select(r => $"{r.Guid}:{r.Wcid}:{r.StackSize}:{r.ContainerId}:{r.WieldLocation}:{r.PlacementPosition}"));

        private static void AssertReleased(FakeHost host, bool done)
        {
            Assert.IsTrue(done, "done ran");
            Assert.IsFalse(host.Claimed, "the claim was released");
            Assert.AreEqual(1, host.Releases, "released exactly once");
        }

        [TestMethod]
        public void Success_SavesNewThenRemovesOld_AndRemindsToSnapshot()
        {
            var host = Seeded();

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            StringAssert.Contains(replies.Last(), "/pvptemplate snapshot <key> Pvp Bow | <display name>");
            Assert.IsTrue(replies.Any(r => r.Contains("5 created, 4 removed")), string.Join("\n", replies));

            Assert.IsFalse(host.Rows.ContainsKey(1) || host.Rows.ContainsKey(2) || host.Rows.ContainsKey(3) || host.Rows.ContainsKey(4), "old rows gone");
            Assert.AreEqual((int)EquipMask.Held, host.Rows.Values.Single(r => r.Wcid == 1006769).WieldLocation, "the wand is worn in the same slot");
            Assert.AreEqual(100, host.Rows.Values.Single(r => r.Wcid == 1006775).StackSize);
            Assert.AreEqual(2, host.Rows.Values.Count(r => r.Wcid == 300), "both potions recreated");
            Assert.AreEqual(1, host.Rows.Values.Count(r => r.Wcid == 1007119));

            var lastSave = host.Calls.LastIndexOf("save");
            var firstRemove = host.Calls.IndexOf("remove");
            Assert.IsTrue(lastSave < firstRemove, "every save before any removal");
            Assert.AreEqual(5, host.Calls.Count(c => c == "info"), "the pre-write Info log plus one Info log per op");
        }

        [TestMethod]
        public void PlannedWrites_AreLoggedBeforeTheFirstSave()
        {
            var host = Seeded();

            Run(host, FullOps);

            var firstInfo = host.Calls.IndexOf("info");
            Assert.IsTrue(firstInfo >= 0 && firstInfo < host.Calls.IndexOf("save"), string.Join(",", host.Calls));

            var line = host.Infos[0];
            StringAssert.Contains(line, "admin");
            StringAssert.Contains(line, "Pvp Bow");
            StringAssert.Contains(line, "0x80001000 wcid 1006769 replacing 0x00000001");
            StringAssert.Contains(line, "0x80001004 wcid 1007119");
            StringAssert.Contains(line, "0x00000001, 0x00000002, 0x00000003, 0x00000004");
        }

        [TestMethod]
        public void Claim_IsTakenBeforeTheOfflineCheck()
        {
            var host = Seeded();

            Run(host, FullOps);

            Assert.IsTrue(host.Calls.IndexOf("claim") < host.Calls.IndexOf("online?"), string.Join(",", host.Calls));
            Assert.IsTrue(host.Calls.IndexOf("claim") < host.Calls.IndexOf("read"));
        }

        [TestMethod]
        public void Preview_WritesNothing()
        {
            var host = Seeded();
            var before = Snapshot(host);

            var (replies, done) = Run(host, FullOps, preview: true);

            AssertReleased(host, done);
            Assert.AreEqual(before, Snapshot(host));
            Assert.IsFalse(host.Calls.Contains("create") || host.Calls.Contains("save") || host.Calls.Contains("remove"));
            StringAssert.Contains(replies.Last(), "nothing was written");
            Assert.AreEqual(4, replies.Count - 1, "one line per op plus the summary");
        }

        [TestMethod]
        public void RefusedPlan_WritesNothing()
        {
            var host = Seeded();
            var before = Snapshot(host);

            var (replies, done) = Run(host, FullOps + " -777");

            AssertReleased(host, done);
            Assert.AreEqual(before, Snapshot(host));
            Assert.IsFalse(host.Calls.Contains("create") || host.Calls.Contains("save") || host.Calls.Contains("remove"));
            Assert.IsTrue(replies.Any(r => r.Contains("carries no wcid 777")));
        }

        [TestMethod]
        public void SaveFailsPartWay_RollsBack_CharacterExactlyAsItWas()
        {
            var host = Seeded();
            host.FailSaveNumber = 3; // saves 1 and 2 have LANDED by the time 3 fails
            var before = Snapshot(host);

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            Assert.AreEqual(5, host.Calls.Count(c => c == "save"), "every save is answered before deciding");
            Assert.AreEqual(1, host.Calls.Count(c => c == "remove"), "one remove: the rollback; the old items were never touched");
            Assert.AreEqual(before, Snapshot(host), "no new row is left, every old row is still there");
            StringAssert.Contains(replies.Last(), "rolled back");
        }

        [TestMethod]
        public void SaveFailsPartWay_Control_WithoutTheFailureTheRowsChange()
        {
            // Control for the rollback test: the same run with no failure does change the rows, so "unchanged" above is a real result.
            var host = Seeded();
            var before = Snapshot(host);

            Run(host, FullOps);

            Assert.AreNotEqual(before, Snapshot(host));
        }

        [TestMethod]
        public void RemovalFailsWithNothingRemoved_RollsBack_CharacterExactlyAsItWas()
        {
            var host = Seeded();
            host.FailRemoveCallNumber = 1;
            host.PartialRemoveCount = 0;
            var before = Snapshot(host);

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            Assert.AreEqual(before, Snapshot(host));
            StringAssert.Contains(replies.Last(), "rolled back");
        }

        [TestMethod]
        public void RemovalLandsPartWay_RetryAlsoFails_KeepsNewItems_AndNamesLeftovers()
        {
            var host = Seeded();
            host.FailRemoveCallNumber = 1;
            host.PartialRemoveCount = 2; // ids 1 and 2 go, 3 and 4 stay
            host.FailRemoveCalls[2] = 0; // the retry removes nothing

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            Assert.AreEqual(2, host.Calls.Count(c => c == "remove"), "the removal and exactly one retry");
            Assert.AreEqual(5, host.Rows.Values.Count(r => r.Guid >= 0x80001000), "the new items are kept: nothing is lost");
            Assert.IsTrue(host.Rows.ContainsKey(3) && host.Rows.ContainsKey(4));
            StringAssert.Contains(replies.Last(), "0x00000003 (wcid 300)");
            StringAssert.Contains(replies.Last(), "0x00000004 (wcid 300)");
            Assert.IsFalse(replies.Last().Contains("-300"), "-300 would delete the new copies too: " + replies.Last());
        }

        [TestMethod]
        public void Recreate_RemovalLandsPartWay_RetrySucceeds()
        {
            var host = Seeded();
            host.FailRemoveCallNumber = 1;
            host.PartialRemoveCount = 1; // 3 goes, 4 stays; the retry (call 2) removes 4

            var (replies, done) = Run(host, "~300");

            AssertReleased(host, done);
            Assert.AreEqual(2, host.Calls.Count(c => c == "remove"));
            Assert.IsFalse(host.Rows.ContainsKey(3) || host.Rows.ContainsKey(4), "both old copies are gone");
            Assert.AreEqual(2, host.Rows.Values.Count(r => r.Wcid == 300 && r.Guid >= 0x80001000), "both new copies are there");
            Assert.IsTrue(replies.Any(r => r.Contains("2 created, 2 removed")), string.Join("\n", replies));
            StringAssert.Contains(replies.Last(), "/pvptemplate snapshot");
        }

        [TestMethod]
        public void Recreate_RemovalLandsPartWay_RetryPartlyFails_NamesBothSetsByGuid_AndNeverAdvisesDeletingASharedWcid()
        {
            var host = Seeded();
            // Removals in op order: 2 (-1004133), 3 and 4 (~300), 1 (24207=...). Call 1 removes 2; the retry (call 2) removes 3.
            host.FailRemoveCallNumber = 1;
            host.PartialRemoveCount = 1;
            host.FailRemoveCalls[2] = 1;

            var (replies, done) = Run(host, "-1004133 ~300 24207=1006769");
            var last = replies.Last();

            AssertReleased(host, done);
            Assert.AreEqual(2, host.Calls.Count(c => c == "remove"), "one retry, no rollback");
            Assert.IsTrue(host.Rows.ContainsKey(4) && host.Rows.ContainsKey(1) && !host.Rows.ContainsKey(3) && !host.Rows.ContainsKey(2));

            StringAssert.Contains(last, "0x00000004 (wcid 300)");
            StringAssert.Contains(last, "0x00000001 (wcid 24207)");
            StringAssert.Contains(last, "0x80001000 (wcid 300)");
            StringAssert.Contains(last, "0x80001001 (wcid 300)");
            StringAssert.Contains(last, "0x80001002 (wcid 1006769)");
            Assert.IsFalse(last.Contains("-300"), "-300 would delete the new copies too: " + last);
            Assert.IsFalse(last.Contains("-<wcid>"), last);
            StringAssert.Contains(last, "Once checked, delete the leftovers whose wcid no new item carries with: -24207.");
            StringAssert.Contains(last, "Do NOT use the - op for wcid(s) 300: it deletes every possession of a wcid, the new copies included; remove those leftovers by hand (no kit op targets a single guid).");
        }

        [TestMethod]
        public void ThrowAfterTheFirstWrite_SaysTheCharacterMayBePartWayEdited_AndReleases()
        {
            // Save 1 throws while queueing; the first LogError (Queue's report of it) then throws too, which escapes SaveAll after
            // markWrote and reaches the after-read catch-all with "wrote" set.
            var host = Seeded();
            host.SaveThrowNumber = 1;
            host.LogErrorThrowNumber = 1;

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            StringAssert.Contains(replies.Last(), "failed after its writes began, so Pvp Bow may be part way edited");
            Assert.IsTrue(host.Calls.IndexOf("info") < host.Calls.IndexOf("save"), "the planned writes were logged before the throw");
        }

        [TestMethod]
        public void ThrowBeforeTheFirstWrite_SaysNothingWasWritten()
        {
            // Control for the test above: the same catch-all reached before markWrote (the pre-write LogInfo throws) keeps the old message.
            var host = Seeded();
            host.LogInfoThrowNumber = 1;

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            Assert.IsFalse(host.Calls.Contains("save") || host.Calls.Contains("remove"));
            StringAssert.Contains(replies.Last(), "failed before anything was written");
        }

        [TestMethod]
        public void LogInfoThrows_AtSuccess_StillReleasesTheClaimOnce()
        {
            var host = Seeded();
            host.LogInfoThrowNumber = 2; // 1 = the pre-write log; 2 = Succeed's first per-op line

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            Assert.IsFalse(host.Rows.ContainsKey(1), "the edit itself completed");
            Assert.AreEqual(5, host.Rows.Values.Count(r => r.Guid >= 0x80001000), "a throw after the removal answered must not roll back the committed new items");
            StringAssert.Contains(replies.Last(), "/pvptemplate snapshot");
        }

        [TestMethod]
        public void RemovalLandsPartWay_RetryFails_AndTheReReadFails_StillNamesTheRetriedLeftovers()
        {
            var host = Seeded();
            host.FailRemoveCallNumber = 1;
            host.PartialRemoveCount = 2;
            host.FailRemoveCalls[2] = 0;
            host.FailReadNumber = 3; // 1 = the plan read, 2 = after the removal, 3 = after the retry

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            StringAssert.Contains(replies.Last(), "may still be present");
            StringAssert.Contains(replies.Last(), "0x00000003 (wcid 300)");
        }

        [TestMethod]
        public void RemovalFails_AndTheReReadFails_KeepsNewItems_AndReleases()
        {
            var host = Seeded();
            host.FailRemoveCallNumber = 1;
            host.PartialRemoveCount = 0;
            host.FailReadNumber = 2; // the plan read succeeds; the re-read after the failed removal answers null

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            Assert.AreEqual(2, host.Calls.Count(c => c == "read"));
            StringAssert.Contains(replies.Last(), "could not be re-read");
            Assert.AreEqual(5, host.Rows.Values.Count(r => r.Guid >= 0x80001000), "the new items are kept");
        }

        [TestMethod]
        public void CreateReturnsNullOrThrows_WritesNothing_AndReleases()
        {
            foreach (var (label, setup) in new (string, Action<FakeHost>)[]
            {
                ("null", h => h.CreateNullNumber = 2),
                ("throw", h => h.CreateThrowNumber = 2),
            })
            {
                var host = Seeded();
                setup(host);
                var before = Snapshot(host);

                var (replies, done) = Run(host, FullOps);

                AssertReleased(host, done);
                Assert.AreEqual(before, Snapshot(host), label);
                Assert.IsFalse(host.Calls.Contains("save") || host.Calls.Contains("remove"), label);
                StringAssert.Contains(replies.Last(), "Building wcid 1006775 failed", label);
            }
        }

        [TestMethod]
        public void SaveThrows_RollsBack_AndReleases()
        {
            var host = Seeded();
            host.SaveThrowNumber = 2;
            var before = Snapshot(host);

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            Assert.AreEqual(before, Snapshot(host));
            StringAssert.Contains(replies.Last(), "rolled back");
        }

        [TestMethod]
        public void RemoveThrows_RollsBack_AndReleases()
        {
            var host = Seeded();
            host.RemoveThrowNumber = 1; // queueing the removal of the old items throws; the rollback (call 2) succeeds
            var before = Snapshot(host);

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            Assert.AreEqual(before, Snapshot(host));
            StringAssert.Contains(replies.Last(), "rolled back");
        }

        [TestMethod]
        public void RollbackRemoveThrows_IsReported_AndReleases()
        {
            var host = Seeded();
            host.FailSaveNumber = 1;
            host.RemoveThrowNumber = 1; // the rollback's remove

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            StringAssert.Contains(replies.Last(), "rollback ALSO failed");
            Assert.IsTrue(host.Rows.ContainsKey(1) && host.Rows.ContainsKey(2) && host.Rows.ContainsKey(3) && host.Rows.ContainsKey(4), "old items untouched");
        }

        [TestMethod]
        public void ReplyThrows_AtARefusal_StillReleasesTheClaimOnce()
        {
            var host = Seeded();
            host.Online = true;
            Assert.IsTrue(PvpTemplateKitEdit.TryParseOps(FullOps, out var parsed, out _));
            var done = false;

            var threw = false;

            try
            {
                new PvpTemplateKitEditService(host).Run(false, "Pvp Bow", parsed, "admin", _ => throw new InvalidOperationException("reply threw"), () => done = true);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.IsTrue(threw, "the reply's throw surfaces to the caller");
            AssertReleased(host, done);
        }

        [TestMethod]
        public void ReplyThrows_OnEveryLine_AfterTheRead_StillReleasesTheClaimOnce()
        {
            var host = Seeded();
            Assert.IsTrue(PvpTemplateKitEdit.TryParseOps(FullOps, out var parsed, out _));
            var done = false;

            try
            {
                new PvpTemplateKitEditService(host).Run(false, "Pvp Bow", parsed, "admin", _ => throw new InvalidOperationException("reply threw"), () => done = true);
            }
            catch (InvalidOperationException)
            {
                // The reply's own throw may surface; what matters is the claim.
            }

            AssertReleased(host, done);
            Assert.IsFalse(host.Calls.Contains("save"), "the first reply (a plan line) threw before any write");
        }

        [TestMethod]
        public void ReplyThrows_OnTheDoneLine_StillReleasesTheClaimOnce()
        {
            var host = Seeded();
            Assert.IsTrue(PvpTemplateKitEdit.TryParseOps(FullOps, out var parsed, out _));
            var done = false;

            void Reply(string line)
            {
                if (line.StartsWith("Kit edit of", StringComparison.Ordinal))
                    throw new InvalidOperationException("reply threw");
            }

            try
            {
                new PvpTemplateKitEditService(host).Run(false, "Pvp Bow", parsed, "admin", Reply, () => done = true);
            }
            catch (InvalidOperationException)
            {
            }

            AssertReleased(host, done);
            Assert.IsFalse(host.Rows.ContainsKey(1), "the edit itself completed");
            Assert.AreEqual(5, host.Rows.Values.Count(r => r.Guid >= 0x80001000), "a throw after the removal answered must not roll back the committed new items");
            Assert.AreEqual(1, host.Calls.Count(c => c == "remove"), "no rollback remove");
        }

        [TestMethod]
        public void RollbackItselfFails_IsReportedWithTheGuids()
        {
            var host = Seeded();
            host.FailSaveNumber = 2;
            host.FailRemoveCallNumber = 1; // the rollback's remove
            host.PartialRemoveCount = 0;

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            StringAssert.Contains(replies.Last(), "rollback ALSO failed");
            StringAssert.Contains(replies.Last(), "0x80001000");
            Assert.IsTrue(host.Rows.ContainsKey(1) && host.Rows.ContainsKey(2) && host.Rows.ContainsKey(3) && host.Rows.ContainsKey(4), "old items untouched");
        }

        [TestMethod]
        public void ReadFails_WritesNothing()
        {
            var host = Seeded();
            host.FailReadNumber = 1;

            var (replies, done) = Run(host, FullOps);

            AssertReleased(host, done);
            Assert.IsFalse(host.Calls.Contains("create"));
            StringAssert.Contains(replies.Last(), "Nothing was written");
        }

        [TestMethod]
        public void StateRefusals_ReadNothing_AndReleaseTheClaim()
        {
            foreach (var (label, setup, expect) in new (string, Action<FakeHost>, string)[]
            {
                ("account", h => h.Character.AccountName = "someone", "not on the template account"),
                ("online", h => h.Online = true, "is online"),
                ("deleted", h => h.Character.Deleted = true, "deleted"),
                ("restore record", h => h.Character.HasRestoreRecord = true, "restore record"),
            })
            {
                var host = Seeded();
                setup(host);

                var (replies, done) = Run(host, FullOps);

                AssertReleased(host, done);
                Assert.IsFalse(host.Calls.Contains("read"), label);
                StringAssert.Contains(replies.Single(), expect, label);
            }
        }

        [TestMethod]
        public void ClaimRefused_ReadsNothing()
        {
            var host = Seeded();
            host.ClaimRefused = true;

            var (replies, done) = Run(host, FullOps);

            Assert.IsTrue(done);
            Assert.AreEqual(0, host.Releases, "nothing to release");
            Assert.IsFalse(host.Calls.Contains("read") || host.Calls.Contains("online?"));
            StringAssert.Contains(replies.Single(), "logging in");
        }

        [TestMethod]
        public void UnknownCharacter_IsRefused()
        {
            var host = Seeded();

            var (replies, done) = Run(host, FullOps, name: "Nobody");

            Assert.IsTrue(done);
            Assert.IsFalse(host.Calls.Contains("claim"));
            StringAssert.Contains(replies.Single(), "No character named Nobody");
        }

        // ---- ToPossession: the runtime biota (ACE.Entity.Models.Biota) to a planner row ----

        private static ACE.Entity.Models.Biota Biota(uint guid, uint wcid, WeenieType type = WeenieType.Generic, string name = "Thing")
            => new ACE.Entity.Models.Biota
            {
                Id = guid,
                WeenieClassId = wcid,
                WeenieType = type,
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesIID = new Dictionary<PropertyInstanceId, uint>(),
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesString = name == null ? null : new Dictionary<PropertyString, string> { [PropertyString.Name] = name },
            };

        [TestMethod]
        public void ToPossession_Worn()
        {
            var b = Biota(0x80000010, 24207, name: "Wand");
            b.PropertiesIID[PropertyInstanceId.Wielder] = Char;
            b.PropertiesInt[PropertyInt.CurrentWieldedLocation] = (int)EquipMask.Held;

            var p = PvpTemplateKitLiveHost.ToPossession(b);

            Assert.AreEqual(0x80000010u, p.Guid);
            Assert.AreEqual(24207u, p.Wcid);
            Assert.AreEqual("Wand", p.Name);
            Assert.IsTrue(p.IsWorn);
            Assert.AreEqual((int)EquipMask.Held, p.WieldLocation);
            Assert.AreEqual(Char, p.WielderId);
            Assert.IsNull(p.ContainerId);
            Assert.IsFalse(p.IsContainer || p.RequiresPackSlot);
        }

        [TestMethod]
        public void ToPossession_WieldedLocationWithoutAWielder_IsNotWorn()
        {
            var b = Biota(0x80000011, 24207);
            b.PropertiesInt[PropertyInt.CurrentWieldedLocation] = (int)EquipMask.Held;
            b.PropertiesIID[PropertyInstanceId.Container] = Char;

            Assert.IsFalse(PvpTemplateKitLiveHost.ToPossession(b).IsWorn);
        }

        [TestMethod]
        public void ToPossession_MainPack_StackAndPlacement()
        {
            var b = Biota(0x80000012, 1004133);
            b.PropertiesIID[PropertyInstanceId.Container] = Char;
            b.PropertiesIID[PropertyInstanceId.Owner] = Char;
            b.PropertiesInt[PropertyInt.StackSize] = 80;
            b.PropertiesInt[PropertyInt.PlacementPosition] = 5;

            var p = PvpTemplateKitLiveHost.ToPossession(b);

            Assert.IsFalse(p.IsWorn);
            Assert.AreEqual(Char, p.ContainerId);
            Assert.AreEqual(Char, p.OwnerId);
            Assert.AreEqual(80, p.StackSize);
            Assert.AreEqual(5, p.PlacementPosition);
        }

        [TestMethod]
        public void ToPossession_SidePack_ItemKeepsItsPackAndOwner()
        {
            const uint side = 0x80000100;
            var b = Biota(0x80000013, 300, name: null);
            b.PropertiesIID[PropertyInstanceId.Container] = side;
            b.PropertiesIID[PropertyInstanceId.Owner] = Char;
            b.PropertiesInt[PropertyInt.StackSize] = 0;

            var p = PvpTemplateKitLiveHost.ToPossession(b);

            Assert.AreEqual(side, p.ContainerId);
            Assert.AreEqual(Char, p.OwnerId);
            Assert.AreEqual(1, p.StackSize, "a 0 stack reads as 1");
            Assert.IsNull(p.Name, "no PropertiesString: no name, no throw");
            Assert.IsNull(p.PlacementPosition);
        }

        [TestMethod]
        public void ToPossession_Focus_RequiresAPackSlot()
        {
            var b = Biota(0x80000014, 500);
            b.PropertiesIID[PropertyInstanceId.Container] = Char;
            b.PropertiesBool[PropertyBool.RequiresBackpackSlot] = true;

            var p = PvpTemplateKitLiveHost.ToPossession(b);

            Assert.IsTrue(p.RequiresPackSlot);
            Assert.IsFalse(p.IsContainer);
        }

        [TestMethod]
        public void ToPossession_Container_IsAPack()
        {
            var b = Biota(0x80000100, 136, type: WeenieType.Container, name: "Pack");
            b.PropertiesIID[PropertyInstanceId.Container] = Char;
            b.PropertiesIID[PropertyInstanceId.Wielder] = 0; // a 0 iid reads as absent

            var p = PvpTemplateKitLiveHost.ToPossession(b);

            Assert.IsTrue(p.IsContainer);
            Assert.IsNull(p.WielderId);
            Assert.IsFalse(p.IsWorn);
        }

        [TestMethod]
        public void LiveHost_ClaimUsesTheSuitHolderMoveGuard()
        {
            const uint guid = 0x50FFFF01;
            SuitHolderMoveGuard.ResetForTests();

            try
            {
                var live = new PvpTemplateKitLiveHost();

                Assert.IsTrue(live.TryClaim(guid));
                Assert.IsFalse(SuitHolderMoveGuard.TryBeginEnterWorld(guid), "a login is refused while the kit edit holds the character");
                Assert.IsFalse(live.TryClaim(guid), "one edit at a time");

                live.Release(guid);
                Assert.IsTrue(SuitHolderMoveGuard.TryBeginEnterWorld(guid), "released: the login goes in");
                Assert.IsFalse(live.TryClaim(guid), "and the edit is refused while that login is entering");
                SuitHolderMoveGuard.EndEnterWorld(guid);
            }
            finally
            {
                SuitHolderMoveGuard.ResetForTests();
            }
        }
    }
}
