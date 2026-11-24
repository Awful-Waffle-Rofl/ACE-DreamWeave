using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ACE.Entity;
using ACE.Server.MlDigsite;
using ACE.Server.MlTreasure;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Discord bug report "Wave map seems to have gotten eaten" (2026-11-16). A treasure map dug within
    /// ml_digsite_separation_metres of another player's live digsite encounter used to be consumed by
    /// TreasureMapHandler.FinishDig BEFORE MlDigsiteManager.TryStart refused it for separation, so the map
    /// was eaten, opened nothing, and said nothing; the digger then fought the other player's wave and was
    /// paid as that encounter's helper.
    ///
    /// The fix runs the dig's payout-and-consume step as a commit INSIDE TryStart's admission lock, skipped
    /// for the separation refusal only. MlDigsiteManager.TryAdmit is the Player-free core and is driven
    /// directly here; FinishDig needs a Player, which this harness cannot construct, so its wiring is pinned
    /// against source text.
    ///
    /// Order-independent: no PropertyManager read (every tunable is a TryAdmit parameter). Every encounter a
    /// test registers sits in a private instance id and is removed again in a finally.
    /// </summary>
    [TestClass]
    public class MlDigsiteSeparationRefusalTests
    {
        private const double Separation = 80.0;
        private const long Cap = 1000;
        private const uint MlOutdoorCell = 0x1BB10015;

        private static readonly DateTime T0 = new DateTime(2026, 11, 16, 18, 0, 0, DateTimeKind.Utc);

        private static int nextGuid = 0x7D5E0000;
        private static int nextInstance = 0x5E00;

        private static uint NewGuid() => (uint)Interlocked.Increment(ref nextGuid);

        /// <summary>A short instance id no other test uses, so no stray live encounter can sit near these anchors.</summary>
        private static ushort NewInstance() => (ushort)Interlocked.Increment(ref nextInstance);

        private static Position At(ushort instance, float x, float y)
            => new Position(MlOutdoorCell, x, y, 0f, 0f, 0f, 0f, 1f, Position.InstanceIDFromVars(1, instance, false));

        private static MlDigsiteStartOutcome Admit(uint digger, Position anchor, Func<bool> commit,
            List<uint> registered, out string reason)
        {
            var outcome = MlDigsiteManager.TryAdmit(digger, "Digger", anchor, MlDigsiteType.WavesAndMiniBoss,
                MlTreasureZone.Unknown, T0, true, Cap, Separation, () => MlDigsiteType.WavesAndMiniBoss, commit,
                out var encounter, out reason);

            if (encounter != null)
            {
                lock (registered)
                    registered.Add(encounter.EncounterId);
            }

            return outcome;
        }

        private static void Forget(List<uint> registered)
        {
            foreach (var id in registered)
                MlDigsiteManager.ForgetEncounterForTest(id);
        }

        // ---- the pure gate ---------------------------------------------------------------------------------

        [TestMethod]
        public void Admit_names_each_gate_in_order()
        {
            Assert.AreEqual(MlDigsiteAdmission.Admitted, MlDigsiteRules.Admit(true, false, false, 0, 20, out _));
            Assert.AreEqual(MlDigsiteAdmission.Disabled, MlDigsiteRules.Admit(false, true, true, 20, 20, out _));
            Assert.AreEqual(MlDigsiteAdmission.DiggerAlreadyRunning, MlDigsiteRules.Admit(true, true, true, 20, 20, out _));
            Assert.AreEqual(MlDigsiteAdmission.TooCloseToLiveEncounter, MlDigsiteRules.Admit(true, false, true, 20, 20, out _));
            Assert.AreEqual(MlDigsiteAdmission.CapFull, MlDigsiteRules.Admit(true, false, false, 20, 20, out _));
        }

        [TestMethod]
        public void Only_the_separation_refusal_keeps_the_map()
        {
            foreach (MlDigsiteAdmission admission in Enum.GetValues(typeof(MlDigsiteAdmission)))
            {
                Assert.AreEqual(admission == MlDigsiteAdmission.TooCloseToLiveEncounter, MlDigsiteRules.KeepsMapOnRefusal(admission),
                    $"{admission}");
            }
        }

        // ---- TryAdmit: the reported case ------------------------------------------------------------------

        [TestMethod]
        public void A_dig_inside_the_separation_of_a_live_encounter_is_refused_without_paying_or_consuming()
        {
            var registered = new List<uint>();
            var instance = NewInstance();

            try
            {
                var firstCommits = 0;
                Assert.AreEqual(MlDigsiteStartOutcome.Started,
                    Admit(NewGuid(), At(instance, 60f, 110f), () => { firstCommits++; return true; }, registered, out _));
                Assert.AreEqual(1, firstCommits);

                // 40 m away: inside the 80 m separation, the bug report's "within roughly 80% of radar range".
                var secondCommits = 0;
                var outcome = Admit(NewGuid(), At(instance, 100f, 110f), () => { secondCommits++; return true; }, registered, out var reason);

                Assert.AreEqual(MlDigsiteStartOutcome.RefusedMapKept, outcome);
                Assert.AreEqual(0, secondCommits, "the second map must not be paid out or consumed");
                Assert.AreEqual(1, registered.Count, "the second dig must register no encounter");
                StringAssert.Contains(reason, "too close");
            }
            finally
            {
                Forget(registered);
            }
        }

        [TestMethod]
        public void A_dig_outside_the_separation_opens_its_own_independent_encounter()
        {
            var registered = new List<uint>();
            var instance = NewInstance();

            try
            {
                var commits = 0;
                Func<bool> commit = () => { commits++; return true; };

                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(NewGuid(), At(instance, 60f, 110f), commit, registered, out _));
                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(NewGuid(), At(instance, 160f, 110f), commit, registered, out _));

                Assert.AreEqual(2, commits);
                Assert.AreEqual(2, registered.Distinct().Count(), "each map owns its own encounter");
            }
            finally
            {
                Forget(registered);
            }
        }

        [TestMethod]
        public void The_same_spot_in_another_instance_is_not_too_close()
        {
            var registered = new List<uint>();

            try
            {
                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(NewGuid(), At(NewInstance(), 60f, 110f), () => true, registered, out _));
                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(NewGuid(), At(NewInstance(), 60f, 110f), () => true, registered, out _));
            }
            finally
            {
                Forget(registered);
            }
        }

        [TestMethod]
        public void A_refused_map_is_admitted_once_the_nearby_encounter_is_gone()
        {
            var registered = new List<uint>();
            var instance = NewInstance();

            try
            {
                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(NewGuid(), At(instance, 60f, 110f), () => true, registered, out _));

                var digger = NewGuid();
                Assert.AreEqual(MlDigsiteStartOutcome.RefusedMapKept, Admit(digger, At(instance, 100f, 110f), () => true, registered, out _));

                Forget(registered);
                registered.Clear();

                var commits = 0;
                Assert.AreEqual(MlDigsiteStartOutcome.Started,
                    Admit(digger, At(instance, 100f, 110f), () => { commits++; return true; }, registered, out _),
                    "the kept map digs again normally");
                Assert.AreEqual(1, commits);
            }
            finally
            {
                Forget(registered);
            }
        }

        // ---- TryAdmit: everything else is unchanged --------------------------------------------------------

        [TestMethod]
        public void Every_other_refusal_still_pays_and_consumes_before_refusing()
        {
            var registered = new List<uint>();

            try
            {
                // The digger already has a live encounter (far from this second dig).
                var digger = NewGuid();
                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(digger, At(NewInstance(), 60f, 110f), () => true, registered, out _));

                var commits = 0;
                Assert.AreEqual(MlDigsiteStartOutcome.Refused, Admit(digger, At(NewInstance(), 60f, 110f), () => { commits++; return true; }, registered, out _));
                Assert.AreEqual(1, commits, "already-running: consumed, as before the fix");

                // Disabled and cap-full.
                commits = 0;
                Assert.AreEqual(MlDigsiteStartOutcome.Refused, MlDigsiteManager.TryAdmit(NewGuid(), "Digger", At(NewInstance(), 60f, 110f),
                    MlDigsiteType.WavesAndMiniBoss, MlTreasureZone.Unknown, T0, false, Cap, Separation, () => MlDigsiteType.WavesAndMiniBoss,
                    () => { commits++; return true; }, out var disabledEncounter, out _));
                Assert.IsNull(disabledEncounter);
                Assert.AreEqual(1, commits, "disabled: consumed, as before the fix");

                commits = 0;
                Assert.AreEqual(MlDigsiteStartOutcome.Refused, MlDigsiteManager.TryAdmit(NewGuid(), "Digger", At(NewInstance(), 60f, 110f),
                    MlDigsiteType.WavesAndMiniBoss, MlTreasureZone.Unknown, T0, true, 0, Separation, () => MlDigsiteType.WavesAndMiniBoss,
                    () => { commits++; return true; }, out var cappedEncounter, out _));
                Assert.IsNull(cappedEncounter);
                Assert.AreEqual(1, commits, "cap full: consumed, as before the fix");
            }
            finally
            {
                Forget(registered);
            }
        }

        // ---- TryAdmit: the digger's own live encounter (owner ruling 2026-11-16) ---------------------------

        [TestMethod]
        public void Admit_routes_an_own_encounter_inside_the_radius_to_the_separation_refusal()
        {
            Assert.AreEqual(MlDigsiteAdmission.TooCloseToLiveEncounter, MlDigsiteRules.Admit(true, true, true, true, 0, 20, out var near));
            StringAssert.Contains(near, "too close");
            Assert.AreEqual(MlDigsiteAdmission.DiggerAlreadyRunning, MlDigsiteRules.Admit(true, true, false, false, 0, 20, out _));
            Assert.AreEqual(MlDigsiteAdmission.DiggerAlreadyRunning, MlDigsiteRules.Admit(true, true, false, true, 0, 20, out _),
                "another player's nearby encounter does not change the far-own-encounter case");
            Assert.AreEqual(MlDigsiteAdmission.Disabled, MlDigsiteRules.Admit(false, true, true, true, 0, 20, out _));
        }

        [TestMethod]
        public void A_dig_inside_the_separation_of_the_diggers_own_live_encounter_keeps_the_map()
        {
            var registered = new List<uint>();
            var instance = NewInstance();

            try
            {
                var digger = NewGuid();
                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(digger, At(instance, 60f, 110f), () => true, registered, out _));

                // 40 m from the digger's own live anchor: inside the 80 m separation.
                var commits = 0;
                var outcome = Admit(digger, At(instance, 100f, 110f), () => { commits++; return true; }, registered, out var reason);

                Assert.AreEqual(MlDigsiteStartOutcome.RefusedMapKept, outcome);
                Assert.AreEqual(0, commits, "the second map must not be paid out or consumed");
                Assert.AreEqual(1, registered.Count, "the second dig must register no encounter");
                StringAssert.Contains(reason, "too close");
            }
            finally
            {
                Forget(registered);
            }
        }

        [TestMethod]
        public void A_dig_far_from_the_diggers_own_live_encounter_still_consumes_the_map()
        {
            var registered = new List<uint>();
            var instance = NewInstance();

            try
            {
                var digger = NewGuid();
                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(digger, At(instance, 60f, 110f), () => true, registered, out _));

                // 100 m away in the same instance: outside the 80 m separation.
                var commits = 0;
                var outcome = Admit(digger, At(instance, 160f, 110f), () => { commits++; return true; }, registered, out var reason);

                Assert.AreEqual(MlDigsiteStartOutcome.Refused, outcome);
                Assert.AreEqual(1, commits, "already-running far away: consumed, as before");
                Assert.AreEqual(1, registered.Count);
                StringAssert.Contains(reason, "already has a live encounter");
            }
            finally
            {
                Forget(registered);
            }
        }

        [TestMethod]
        public void Another_players_nearby_encounter_does_not_keep_the_map_when_the_diggers_own_is_far()
        {
            var registered = new List<uint>();
            var instance = NewInstance();

            try
            {
                var digger = NewGuid();
                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(digger, At(instance, 60f, 110f), () => true, registered, out _));
                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(NewGuid(), At(instance, 300f, 110f), () => true, registered, out _));

                // 250 m from the digger's own anchor (outside 80 m), 10 m from the other player's (inside).
                var commits = 0;
                var outcome = Admit(digger, At(instance, 310f, 110f), () => { commits++; return true; }, registered, out var reason);

                Assert.AreEqual(MlDigsiteStartOutcome.Refused, outcome);
                Assert.AreEqual(1, commits, "own encounter far away: consumed, as before, even with another player's nearby");
                Assert.AreEqual(2, registered.Count);
                StringAssert.Contains(reason, "already has a live encounter");
            }
            finally
            {
                Forget(registered);
            }
        }

        [TestMethod]
        public void A_declined_commit_registers_nothing()
        {
            var registered = new List<uint>();
            var instance = NewInstance();

            try
            {
                Assert.AreEqual(MlDigsiteStartOutcome.CommitDeclined, Admit(NewGuid(), At(instance, 60f, 110f), () => false, registered, out _));
                Assert.AreEqual(0, registered.Count);

                // Nothing was registered, so the same spot is still free.
                Assert.AreEqual(MlDigsiteStartOutcome.Started, Admit(NewGuid(), At(instance, 60f, 110f), () => true, registered, out _));
            }
            finally
            {
                Forget(registered);
            }
        }

        // ---- the race the commit-inside-the-lock closes ------------------------------------------------------

        [TestMethod]
        public void Simultaneous_digs_at_one_spot_consume_exactly_one_map()
        {
            var registered = new List<uint>();
            var instance = NewInstance();
            const int diggers = 16;

            try
            {
                var commits = 0;
                var outcomes = new MlDigsiteStartOutcome[diggers];

                using (var gate = new ManualResetEventSlim(false))
                {
                    var tasks = Enumerable.Range(0, diggers).Select(i => Task.Run(() =>
                    {
                        var guid = NewGuid();
                        gate.Wait();
                        outcomes[i] = Admit(guid, At(instance, 60f + i, 110f), () => { Interlocked.Increment(ref commits); return true; }, registered, out _);
                    })).ToArray();

                    gate.Set();
                    Task.WaitAll(tasks);
                }

                Assert.AreEqual(1, commits, "exactly one map may be consumed");
                Assert.AreEqual(1, outcomes.Count(o => o == MlDigsiteStartOutcome.Started));
                Assert.AreEqual(diggers - 1, outcomes.Count(o => o == MlDigsiteStartOutcome.RefusedMapKept));
                Assert.AreEqual(1, registered.Count);
            }
            finally
            {
                Forget(registered);
            }
        }

        // ---- FinishDig wiring (needs a Player, so pinned against source) ------------------------------------

        private static string FinishDigBody()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlTreasure/TreasureMapHandler.cs");
            return PooledLootSourceText.MethodBody(src, "private static void FinishDig(WorldObject wo, Player player)");
        }

        [TestMethod]
        public void FinishDig_consumes_an_ordinary_map_only_inside_the_commit_TryStart_runs()
        {
            var body = FinishDigBody();

            var commitStart = body.IndexOf("bool PayAndConsume()", StringComparison.Ordinal);
            Assert.IsTrue(commitStart >= 0, "the ordinary branch must define its payout-and-consume step as the PayAndConsume commit");

            var commitBody = PooledLootSourceText.MethodBody(body, "bool PayAndConsume()");
            var commitEnd = commitStart + body.Substring(commitStart).IndexOf(commitBody, StringComparison.Ordinal) + commitBody.Length;

            // The boss-variant arm consumes on its own path before the ordinary branch begins; every consume
            // and every currency creation AFTER the ordinary branch's first line must sit inside the commit.
            var ordinary = body.IndexOf("if (TreasureCurrencyWcid == 0)", StringComparison.Ordinal);
            Assert.IsTrue(ordinary >= 0 && ordinary < commitStart);

            foreach (var call in new[] { "player.TryConsumeFromInventoryWithNetworking(wo, 1)", "player.TryCreateInInventoryWithNetworking(currency)" })
            {
                for (var at = body.IndexOf(call, ordinary, StringComparison.Ordinal); at >= 0; at = body.IndexOf(call, at + 1, StringComparison.Ordinal))
                    Assert.IsTrue(at > commitStart && at < commitEnd, $"{call} at {at} must be inside PayAndConsume ({commitStart}..{commitEnd})");
            }

            var start = body.IndexOf("MlDigsite.MlDigsiteManager.TryStart(player, forcedType, forcedMechanicSetId, zone, PayAndConsume, out startRefusal);", StringComparison.Ordinal);
            Assert.IsTrue(start > commitEnd, "TryStart must be handed the commit rather than called after it ran");
            Assert.IsFalse(body.Substring(commitEnd).Contains("PayAndConsume()"), "nothing may run the commit outside TryStart");
        }

        [TestMethod]
        public void FinishDig_tells_the_digger_their_map_was_kept()
        {
            var body = FinishDigBody();

            var kept = body.IndexOf("if (outcome == MlDigsite.MlDigsiteStartOutcome.RefusedMapKept)", StringComparison.Ordinal);
            var line = body.IndexOf("SeparationRefusalLine(MlDigsite.MlDigsiteTunables.SeparationMetres)", StringComparison.Ordinal);

            Assert.IsTrue(kept >= 0, "FinishDig must branch on the kept-map refusal");
            Assert.IsTrue(line > kept, "and send the separation line from it");

            // Exact text: ml_treasure_near_metres is 40, so the site itself is the only place the map digs -
            // the line must not tell the digger to move away.
            Assert.AreEqual(
                "Another dig is already under way within 80 metres of here, and the ground will not give up two at once. Your map is unharmed. Wait for that one to end, then dig here again.",
                TreasureMapHandler.SeparationRefusalLine(80.0));
        }

        [TestMethod]
        public void FinishDig_consumes_the_map_before_paying_and_declines_when_the_consume_fails()
        {
            var commit = CodeOnly(PooledLootSourceText.MethodBody(FinishDigBody(), "bool PayAndConsume()"));

            // The pack-room check: the exact guard, its pack-full line, and its decline, before the consume.
            var room = Find(commit,
                @"if \(currency != null && !player\.CanAddToInventory\(currency\)\)\s*\{\s*" +
                @"player\.Session\.Network\.EnqueueSend\(new GameMessageSystemChat\(\s*""Your pack is too full to hold the treasure\. Make room and dig again\."", ChatMessageType\.Broadcast\)\);\s*" +
                @"return false;\s*\}", "the pack-room check must decline the dig before anything moves");

            // The consume, tracked, with the charge on success and a told-and-declined arm on failure.
            var tracked = Find(commit, @"var consumed = false;\s*try\s*\{\s*consumed = player\.TryConsumeFromInventoryWithNetworking\(wo, 1\);",
                "the consume result must be tracked in `consumed` inside the try");
            var declined = Find(commit,
                @"if \(consumed\)\s*MlRelariaChargeTrophy\.TryAddMapCharge\(player\);\s*else\s*\{\s*log\.Warn\(\$""[^""]*""\);\s*" +
                @"player\.Session\.Network\.EnqueueSend\(new GameMessageSystemChat\(\s*MapGoneLine, ChatMessageType\.Broadcast\)\);\s*" +
                @"return false;\s*\}", "a consume that fails must tell the digger and return false (CommitDeclined)");

            // A throw before the consume succeeded declines too, instead of falling through to the payout.
            var caught = Find(commit, @"catch \(Exception ex\)\s*\{\s*log\.Error\(\$""[^""]*""(, ex)?\);\s*if \(!consumed\)\s*return false;\s*\}",
                "the catch must decline when the consume never succeeded");

            // The payout: delivered is latched inside the success arm, and the failure arm says the coins are lost.
            var paid = Find(commit, @"if \(player\.TryCreateInInventoryWithNetworking\(currency\)\)\s*\{\s*currencyDelivered = true;",
                "currencyDelivered must be set to true as the first statement of the successful add");
            var lost = Find(commit,
                @"else\s*\{\s*log\.Error\(\$""[^""]*""(, ex)?\);\s*player\.Session\.Network\.EnqueueSend\(new GameMessageSystemChat\(\s*PayoutLostLine, ChatMessageType\.Broadcast\)\);\s*\}",
                "a failed coin add after the consume must tell the digger the payout is lost");

            Assert.IsTrue(room < tracked && tracked < declined && declined < caught && caught < paid && paid < lost,
                "order: room check, consume, decline arm, catch, payout");

            // The factory call stays outside the admission lock: built before TryStart, not inside the commit.
            Assert.IsFalse(commit.Contains("WorldObjectFactory.CreateNewWorldObject("), "the currency is pre-built outside the commit");
        }

        [TestMethod]
        public void FinishDig_destroys_an_undelivered_payout_even_when_TryStart_throws()
        {
            var body = CodeOnly(FinishDigBody());

            var guarded = Find(body,
                @"try\s*\{\s*outcome = MlDigsite\.MlDigsiteManager\.TryStart\(player, forcedType, forcedMechanicSetId, zone, PayAndConsume, out startRefusal\);\s*\}\s*" +
                @"finally\s*\{\s*if \(currency != null && !currencyDelivered\)\s*currency\.Destroy\(\);\s*\}",
                "TryStart must sit in a try whose finally destroys an undelivered payout");

            var kept = body.IndexOf("if (outcome == MlDigsite.MlDigsiteStartOutcome.RefusedMapKept)", StringComparison.Ordinal);
            Assert.IsTrue(kept > guarded, "the destroy runs before the RefusedMapKept branch can return");
        }

        [TestMethod]
        public void The_consume_and_payout_failure_lines_are_exact()
        {
            Assert.AreEqual("The map is no longer in your pack, so the dig comes to nothing.", TreasureMapHandler.MapGoneLine);
            Assert.AreEqual("Your pack could not take the coins. The dig goes on, but the payout is lost.", TreasureMapHandler.PayoutLostLine);
        }

        /// <summary>Source with whole-line comments removed, so prose can neither satisfy nor trip a pin.</summary>
        private static string CodeOnly(string source)
            => string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        private static int Find(string source, string pattern, string message)
        {
            var match = System.Text.RegularExpressions.Regex.Match(source, pattern);
            Assert.IsTrue(match.Success, message);
            return match.Index;
        }

        // ---- TryStart(Player, ..., commit, ...) wiring -------------------------------------------------------

        [TestMethod]
        public void TryStart_hands_its_commit_to_TryAdmit_and_never_runs_it_elsewhere()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");
            var body = PooledLootSourceText.MethodBody(src,
                "public static MlDigsiteStartOutcome TryStart(Player digger, MlDigsiteType? forcedType, long forcedMechanicSetId,");

            // Code lines only, so prose in a comment cannot satisfy or trip the pin.
            var code = string.Join("\n", body.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

            var admit = code.IndexOf("TryAdmit(", StringComparison.Ordinal);
            Assert.IsTrue(admit >= 0, "TryStart must call TryAdmit");

            var admitEnd = code.IndexOf(");", admit, StringComparison.Ordinal);
            var admitCall = code.Substring(admit, admitEnd - admit + 2);
            StringAssert.Contains(admitCall, "commit, out var encounter, out reason);", "TryAdmit must be handed TryStart's own commit");

            // Outside that call, the commit is touched exactly once: the unreachable null-Location branch,
            // which keeps the pre-fix commit-then-refuse shape.
            var outside = code.Substring(0, admit) + code.Substring(admitEnd + 2);
            var uses = System.Text.RegularExpressions.Regex.Matches(outside, @"\bcommit\b").Count;
            Assert.AreEqual(1, uses, "the commit may appear outside TryAdmit only in the null-Location branch");

            var nullBranch = code.IndexOf("if (digger?.Location == null)", StringComparison.Ordinal);
            var invoke = code.IndexOf("commit?.Invoke();", StringComparison.Ordinal);
            var branchEnd = code.IndexOf("return MlDigsiteStartOutcome.Refused;", nullBranch, StringComparison.Ordinal);
            Assert.IsTrue(nullBranch >= 0 && invoke > nullBranch && invoke < branchEnd && branchEnd < admit,
                "that one use is the null-Location branch, before TryAdmit");
        }
    }
}
