using System;
using System.Text.RegularExpressions;

using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Source pins for the PLAYER half of the Gauntlet death wiring (Task 9; the creature half is GauntletDeathHookSiteTests).
    /// A Player cannot run the death chain in this suite, so every branch that reads the latch is pinned on its exact if-head:
    /// the latch in Die(), then the vitae, enchantment purge, corpse, item-loss, Olthoi and death-teleport gates, the clear
    /// at the end of the death sequence, the release steps and the 1 Hz tick. Each pin is a checker returning the first
    /// fault, run once over the real source (null expected) and again over mutated copies of it (each must fault), so every
    /// pin is mutation-proved in the test itself. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class GauntletPlayerDeathHookSiteTests
    {
        private const string DeathPath = "Source/ACE.Server/WorldObjects/Player_Death.cs";
        private const string GauntletPath = "Source/ACE.Server/WorldObjects/Player_Gauntlet.cs";
        private const string TickPath = "Source/ACE.Server/WorldObjects/Player_Tick.cs";

        private const string DieSig = "protected override void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)";
        private const string TeleportSig = "public void ThreadSafeTeleportOnDeath()";
        private const string ItemsSig = "public List<WorldObject> CalculateDeathItems(Corpse corpse)";
        private const string OlthoiSig = "public List<WorldObject> CalculateDeathItems_Olthoi(Corpse corpse, bool hadVitae, bool killerIsOlthoiPlayer, bool killerIsPkPlayer)";
        private const string ReleaseSig = "internal void ReleaseFromGauntletHold(Position entry)";
        private const string HoldTickSig = "private void GauntletHoldTick(double currentUnixTime)";
        private const string PlayerTickSig = "public void Player_Tick(double currentUnixTime)";

        internal const string Latch = "gauntletDeathInProgress = LatchGauntletDeath();";
        internal const string PinnacleLatch = "pinnacleDeathInProgress = ACE.Server.Pinnacles.PinnacleFightManager.IsInsidePinnacleCopy(Location.Instance);";

        internal const string VitaeHead =
            "if (!arenaDeath && !IsPKLiteDeath(topDamager) && !IsMule && !worldEventDeathInProgress && !ThreadDungeonDeathInProgress\n" +
            "                && !PinnacleDeathInProgress\n" +
            "                && !GauntletDeathInProgress\n" +
            "                && !PvpPlayerRules.WaivesVitae(PvpMatchDeathInProgress, pvpMatchDeathWaiver))\n" +
            "                InflictVitaePenalty();";

        internal const string PurgeGate = "if (!GauntletDeathInProgress)\n            {\n                if (!arenaDeath && !worldEventDeathInProgress && !PvpPlayerRules.WaivesEnchantmentPurge(PvpMatchDeathInProgress, pvpMatchDeathWaiver))\n                {";

        internal const string CorpseHead = "if (!PvpBattlegroundDeathLatched && !GauntletDeathInProgress)\n                    CreateCorpse(topDamager, hadVitae);";

        internal const string ItemTerm = "\n                || GauntletDeathInProgress\n";

        internal const string PenHead = "else if (GauntletDeathInProgress && GauntletDeathPen() is Position gauntletPen)";
        internal const string PenFallbackHead = "else if (GauntletDeathInProgress)\n";
        internal const string Clear = "gauntletDeathInProgress = false;";

        private static string Death() => PooledLootSourceText.Read(DeathPath);
        private static string Gauntlet() => PooledLootSourceText.Read(GauntletPath);
        private static string Tick() => PooledLootSourceText.Read(TickPath);

        private static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

        private static string Body(string source, string sig) => PooledLootSourceText.MethodBody(source, sig);

        /// <summary>The latched head of a guard: from its first line to the return it guards.</summary>
        private static string Guard(string body, string start, string end)
        {
            var at = body.IndexOf(start, StringComparison.Ordinal);
            if (at < 0)
                return null;

            var stop = body.IndexOf(end, at, StringComparison.Ordinal);
            return stop < 0 ? null : body.Substring(at, stop - at + end.Length);
        }

        private static void Mutated(Func<string, string> checker, string source, string from, string to, string what)
        {
            var mutated = source.Replace(from, to);
            Assert.AreNotEqual(source, mutated, $"guard: the mutation '{what}' applied");
            Assert.IsNotNull(checker(mutated), $"mutation: {what}");
        }

        // ---- checkers -----------------------------------------------------------------------------------------------------

        /// <summary>The latch: once in Die, after the Pinnacle latch, before the first penalty site; assigned only there and at the clear.</summary>
        internal static string LatchFault(string death)
        {
            var die = Body(death, DieSig);

            if (Count(die, Latch) != 1)
                return "the latch is not taken exactly once in Die";

            if (Regex.Matches(death, @"\bgauntletDeathInProgress\s*=(?!=)").Count != 2)
                return "the latch field is assigned somewhere other than the latch and the clear";

            var at = die.IndexOf(Latch, StringComparison.Ordinal);
            var pinnacle = die.IndexOf(PinnacleLatch, StringComparison.Ordinal);
            var vitae = die.IndexOf("InflictVitaePenalty();", StringComparison.Ordinal);

            if (pinnacle < 0 || at < pinnacle)
                return "the latch is not beside the others (after the Pinnacle latch)";

            if (vitae < at)
                return "the latch is taken after the first penalty site";

            if (death.Contains("GauntletManager.", StringComparison.Ordinal) || death.Contains("LiveGauntletDeathHooks", StringComparison.Ordinal))
                return "a death site reads the run live instead of the latch";

            return null;
        }

        /// <summary>The vitae gate: the exact if-head, once, reading the latched getter.</summary>
        internal static string VitaeFault(string death)
            => Count(Body(death, DieSig), VitaeHead) != 1 ? "the vitae if-head does not read the latch" : null;

        /// <summary>The purge: one outer gate on the latch, and both purge calls inside it.</summary>
        internal static string PurgeFault(string death)
        {
            var die = Body(death, DieSig);

            if (Count(die, PurgeGate) != 1)
                return "the purge is not behind the latch's outer gate";

            var gate = die.IndexOf(PurgeGate, StringComparison.Ordinal);
            // The outer gate's closing brace: the first one back at its own indentation.
            var close = die.IndexOf("\n            }\n", gate, StringComparison.Ordinal);

            foreach (var call in new[] { "EnchantmentManager.RemoveAllEnchantments();", "EnchantmentManager.RemoveAllBadEnchantments();" })
            {
                if (Count(die, call) != 1)
                    return $"{call} is not called exactly once in Die";

                var at = die.IndexOf(call, StringComparison.Ordinal);
                if (at < gate || close < 0 || at > close)
                    return $"{call} is outside the latch's gate";
            }

            return null;
        }

        /// <summary>The corpse: the one CreateCorpse call, behind the exact head that reads the latch.</summary>
        internal static string CorpseFault(string death)
        {
            var die = Body(death, DieSig);

            if (Count(die, CorpseHead) != 1)
                return "the corpse head does not read the latch";

            if (Count(die, "CreateCorpse(") != 1)
                return "more than one CreateCorpse call in Die";

            return null;
        }

        /// <summary>Both item-loss guards carry the latch term, inside the guard that returns no items.</summary>
        internal static string ItemsFault(string death)
        {
            foreach (var (sig, after) in new[] { (ItemsSig, "var numItemsDropped = GetNumItemsDropped(corpse);"), (OlthoiSig, "if (killerIsOlthoiPlayer)") })
            {
                var body = Body(death, sig);
                var guard = Guard(body, "if (PropertyManager.GetBool(\"player_death_no_item_loss\").Item", "return new List<WorldObject>();");

                if (guard == null)
                    return $"{sig}: the no-loss guard is missing";

                if (Count(guard, ItemTerm) != 1)
                    return $"{sig}: the no-loss guard does not read the latch";

                if (body.IndexOf(after, StringComparison.Ordinal) < body.IndexOf(guard, StringComparison.Ordinal) + guard.Length)
                    return $"{sig}: the no-loss guard is not ahead of the loss";
            }

            return null;
        }

        /// <summary>
        /// The death teleport: the pen branch, keeping the stamp, ahead of the Trials and challenge-exit branches; then its
        /// fallback (the run ended mid-animation), which spends the stamp.
        /// </summary>
        internal static string TeleportFault(string death)
        {
            var tod = Body(death, TeleportSig);

            if (Count(tod, PenHead) != 1)
                return "the pen branch head is missing";

            var pen = tod.IndexOf(PenHead, StringComparison.Ordinal);
            var fallback = tod.IndexOf(PenFallbackHead, pen, StringComparison.Ordinal);
            var trial = tod.IndexOf("else if (TrialDeathInProgress && TrialManager.StartPositionFor(this) is Position trialStart)", StringComparison.Ordinal);
            var arena = tod.IndexOf("else if (SurvivalChallengeDeathInProgress || WaveChallengeDeathInProgress", StringComparison.Ordinal);
            var match = tod.IndexOf("else if (PvpMatchDeathInProgress)", StringComparison.Ordinal);

            if (fallback < 0)
                return "the fallback branch is missing";

            if (match < 0 || pen < match)
                return "the pen branch is not after the PvP branches";

            if (trial < fallback || arena < fallback)
                return "the Gauntlet branches are not ahead of the Trials and challenge branches";

            var penBranch = tod.Substring(pen, fallback - pen);

            if (!penBranch.Contains("newPosition = gauntletPen;", StringComparison.Ordinal))
                return "the pen branch does not go to the pen";

            if (penBranch.Contains("SetPosition(PositionType.EphemeralRealmExitTo", StringComparison.Ordinal))
                return "the pen branch touches the exit stamp";

            var fallbackBranch = tod.Substring(fallback, trial - fallback);

            if (!fallbackBranch.Contains("SetPosition(PositionType.EphemeralRealmExitTo, null);", StringComparison.Ordinal))
                return "the fallback does not spend the stamp";

            return null;
        }

        /// <summary>The clear: once, inside the delayed arrival action, after the death process ends.</summary>
        internal static string ClearFault(string death)
        {
            var tod = Body(death, TeleportSig);

            if (Count(tod, Clear) != 1)
                return "the latch is not cleared exactly once at the end of the death sequence";

            var arrival = tod.IndexOf("teleportChain.AddAction(this, () =>", StringComparison.Ordinal);
            var done = tod.IndexOf("IsInDeathProcess = false;", StringComparison.Ordinal);
            var clear = tod.IndexOf(Clear, StringComparison.Ordinal);

            if (arrival < 0 || done < arrival || clear < done)
                return "the clear is not inside the arrival action after the death process ends";

            return null;
        }

        /// <summary>The release: protection cleared, then full vitals, then the teleport to the entry it was handed.</summary>
        internal static string ReleaseFault(string gauntlet)
        {
            var body = Body(gauntlet, ReleaseSig);

            const string shieldCall = "SetGauntletArrivalShield(ACE.Common.Time.GetUnixTime());";
            const string followUp = "WorldManager.ThreadSafeTeleport(this, entry, new ActionEventDelegate(() =>\n                {\n                    " + shieldCall;

            var vitals = body.IndexOf("PvpPlayerRules.RestoreRespawnVitals(v => Vitals[v].MaxValue, (v, max) => UpdateVital(Vitals[v], max));", StringComparison.Ordinal);
            var teleport = body.IndexOf(followUp, StringComparison.Ordinal);
            var calls = Count(body, shieldCall);
            var noEntry = body.IndexOf("else\n            {\n                " + shieldCall, StringComparison.Ordinal);

            if (vitals < 0)
                return "the release does not fill the vitals";

            if (teleport < 0)
                return "the shield is not set inside the teleport's follow-up (Player.Teleport dispels protection set before it)";

            if (teleport < vitals)
                return "the release does not teleport to the entry after the vitals";

            if (calls != 2 || noEntry < teleport)
                return "the shield is not set exactly in the follow-up and in the no-entry branch";

            var shield = Body(gauntlet, "internal void SetGauntletArrivalShield(double nowUnix)");

            if (!shield.Contains("UnderLifestoneProtection = true;", StringComparison.Ordinal)
                || !shield.Contains("Volatile.Write(ref gauntletArrivalShieldUntil, nowUnix + GauntletArrivalShieldSeconds);", StringComparison.Ordinal))
                return "the shield step does not stamp its own expiry";

            var tick = Body(gauntlet, "private void GauntletHoldTick(double currentUnixTime)");
            var gate = tick.IndexOf("nextGauntletHoldCheckTime = currentUnixTime + 1.0;", StringComparison.Ordinal);
            var expiry = tick.IndexOf("GauntletArrivalShieldExpiry(currentUnixTime);", StringComparison.Ordinal);
            var early = tick.IndexOf("if (hooks.Holds.Count == 0)", StringComparison.Ordinal);

            if (expiry < gate || early < expiry)
                return "the shield expiry is not checked at the 1 Hz check, ahead of the nobody-held return";

            return null;
        }

        /// <summary>The 1 Hz check: called from Player_Tick, throttled to once a second, skipped while busy.</summary>
        internal static string TickFault(string tick, string gauntlet)
        {
            if (Count(Body(tick, PlayerTickSig), "GauntletHoldTick(currentUnixTime);") != 1)
                return "Player_Tick does not call the hold check exactly once";

            var hold = Body(gauntlet, HoldTickSig);

            if (!hold.Contains("if (nextGauntletHoldCheckTime > currentUnixTime)\n                return;\n\n            nextGauntletHoldCheckTime = currentUnixTime + 1.0;", StringComparison.Ordinal))
                return "the check is not throttled to 1 Hz";

            if (!hold.Contains("var busy = IsDead || IsInDeathProcess || Teleporting || !FirstEnterWorldDone;\n                hooks.TickPlayer(this, Guid.Full, Location, busy);", StringComparison.Ordinal))
                return "the check does not skip a busy player";

            return null;
        }

        // ---- tests ---------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Die_LatchesOnce_BesideTheOthers_BeforeThePenalties()
        {
            var src = Death();
            Assert.IsNull(LatchFault(src));

            Mutated(LatchFault, src, Latch, "gauntletDeathInProgress = false;", "the latch forced false");
            Mutated(LatchFault, src, Latch, "", "the latch removed");
            Mutated(LatchFault, src, "            " + Latch + "\n", "", "the latch line removed");

            // Moved below the vitae site, which would then read an unset latch.
            var moved = src.Replace("            " + Latch + "\n", "").Replace("                InflictVitaePenalty();\n", "                InflictVitaePenalty();\n\n            " + Latch + "\n");
            Assert.AreNotEqual(src, moved, "guard: the move mutation applied");
            Assert.IsNotNull(LatchFault(moved), "mutation: the latch taken after the vitae site");

            Mutated(LatchFault, src, "            var hadVitae = HasVitae;\n", "            var hadVitae = HasVitae;\n            var live = ACE.Server.Avatars.Gauntlet.GauntletManager.ForInstance(Location.Instance);\n", "a live run read in Die");
        }

        [TestMethod]
        public void Vitae_IfHead_ReadsTheLatch()
        {
            var src = Death();
            Assert.IsNull(VitaeFault(src));

            Mutated(VitaeFault, src, "                && !GauntletDeathInProgress\n                && !PvpPlayerRules.WaivesVitae", "                && !PvpPlayerRules.WaivesVitae", "the term dropped");
            Mutated(VitaeFault, src, "                && !GauntletDeathInProgress\n                && !PvpPlayerRules.WaivesVitae", "                && GauntletDeathInProgress\n                && !PvpPlayerRules.WaivesVitae", "the term inverted");
        }

        [TestMethod]
        public void Purge_IsBehindTheLatchGate()
        {
            var src = Death();
            Assert.IsNull(PurgeFault(src));

            Mutated(PurgeFault, src, PurgeGate, PurgeGate.Replace("if (!GauntletDeathInProgress)\n", "if (GauntletDeathInProgress)\n"), "the gate inverted");
            Mutated(PurgeFault, src, PurgeGate, PurgeGate.Replace("if (!GauntletDeathInProgress)\n", "if (true)\n"), "the gate dropped");

            // A purge call moved out of the gate, after it.
            var moved = src.Replace("                        EnchantmentManager.RemoveAllEnchantments();\n", "")
                .Replace("            var dieChain = new ActionChain();", "            EnchantmentManager.RemoveAllEnchantments();\n            var dieChain = new ActionChain();");
            Assert.AreNotEqual(src, moved, "guard: the move mutation applied");
            Assert.IsNotNull(PurgeFault(moved), "mutation: a purge call outside the gate");
        }

        [TestMethod]
        public void Corpse_IfHead_ReadsTheLatch()
        {
            var src = Death();
            Assert.IsNull(CorpseFault(src));

            Mutated(CorpseFault, src, CorpseHead, "if (!PvpBattlegroundDeathLatched)\n                    CreateCorpse(topDamager, hadVitae);", "the term dropped");
            Mutated(CorpseFault, src, CorpseHead, "if (!PvpBattlegroundDeathLatched && GauntletDeathInProgress)\n                    CreateCorpse(topDamager, hadVitae);", "the term inverted");
            Mutated(CorpseFault, src, CorpseHead, CorpseHead + "\n                CreateCorpse(topDamager, hadVitae);", "a second, ungated corpse");
        }

        [TestMethod]
        public void ItemLoss_AndOlthoi_GuardsReadTheLatch()
        {
            var src = Death();
            Assert.IsNull(ItemsFault(src));

            var items = Body(src, ItemsSig);
            var olthoi = Body(src, OlthoiSig);

            Mutated(ItemsFault, src, items, items.Replace(ItemTerm, "\n"), "the item-loss term dropped");
            Mutated(ItemsFault, src, items, items.Replace(ItemTerm, "\n                || !GauntletDeathInProgress\n"), "the item-loss term inverted");
            Mutated(ItemsFault, src, olthoi, olthoi.Replace(ItemTerm, "\n"), "the Olthoi term dropped");
            Mutated(ItemsFault, src, olthoi, olthoi.Replace(ItemTerm, "\n                || !GauntletDeathInProgress\n"), "the Olthoi term inverted");
        }

        [TestMethod]
        public void DeathTeleport_GoesToThePen_KeepingTheStamp()
        {
            var src = Death();
            Assert.IsNull(TeleportFault(src));

            Mutated(TeleportFault, src, PenHead, "else if (!GauntletDeathInProgress && GauntletDeathPen() is Position gauntletPen)", "the head inverted");
            Mutated(TeleportFault, src, "                newPosition = gauntletPen;\n", "                newPosition = gauntletPen;\n                SetPosition(PositionType.EphemeralRealmExitTo, null);\n", "the pen branch spends the stamp");
            Mutated(TeleportFault, src, "                newPosition = gauntletPen;\n", "                newPosition = Sanctuary;\n", "the pen branch goes elsewhere");

            var tod = Body(src, TeleportSig);
            var fallback = tod.IndexOf(PenFallbackHead, StringComparison.Ordinal);
            var trial = tod.IndexOf("            else if (TrialDeathInProgress && TrialManager.StartPositionFor(this) is Position trialStart)", StringComparison.Ordinal);
            var fallbackBlock = tod.Substring(fallback, trial - fallback);
            Mutated(TeleportFault, src, fallbackBlock, fallbackBlock.Replace("                SetPosition(PositionType.EphemeralRealmExitTo, null);\n", ""), "the fallback keeps a stamp into a dead copy");

            // Both Gauntlet branches moved below the Trials branch.
            var block = tod.Substring(tod.IndexOf("            " + PenHead, StringComparison.Ordinal), trial - tod.IndexOf("            " + PenHead, StringComparison.Ordinal));
            var arena = "            else if (SurvivalChallengeDeathInProgress || WaveChallengeDeathInProgress";
            var moved = src.Replace(block, "").Replace(arena, block + arena);
            Assert.AreNotEqual(src, moved, "guard: the move mutation applied");
            Assert.IsNotNull(TeleportFault(moved), "mutation: the Gauntlet branches after the Trials branch");
        }

        [TestMethod]
        public void DeathSequenceEnd_ClearsTheLatch()
        {
            var src = Death();
            Assert.IsNull(ClearFault(src));

            Mutated(ClearFault, src, "                    " + Clear + "\n", "", "the clear removed");
            Assert.IsNotNull(LatchFault(src.Replace("                    " + Clear + "\n", "")), "mutation: the clear removed is also a latch fault");

            var moved = src.Replace("                    " + Clear + "\n", "").Replace("                SetLifestoneProtection();\n", "                SetLifestoneProtection();\n                " + Clear + "\n");
            Assert.AreNotEqual(src, moved, "guard: the move mutation applied");
            Assert.IsNotNull(ClearFault(moved), "mutation: cleared before the arrival action");
        }

        [TestMethod]
        public void Release_ClearsProtection_FillsVitals_ThenTeleports()
        {
            var src = Gauntlet();
            Assert.IsNull(ReleaseFault(src));

            Mutated(ReleaseFault, src, "                    SetGauntletArrivalShield(ACE.Common.Time.GetUnixTime());\n                    SendWeenieError", "                    SendWeenieError", "the shield taken out of the follow-up");
            Mutated(ReleaseFault, src, "        internal void ReleaseFromGauntletHold(Position entry)\n        {\n", "        internal void ReleaseFromGauntletHold(Position entry)\n        {\n            SetGauntletArrivalShield(ACE.Common.Time.GetUnixTime());\n", "the shield set before the teleport as well");
            Mutated(ReleaseFault, src, "            else\n            {\n                SetGauntletArrivalShield(ACE.Common.Time.GetUnixTime());\n            }\n", "", "no shield when there is no entry");
            Mutated(ReleaseFault, src, "            PvpPlayerRules.RestoreRespawnVitals(v => Vitals[v].MaxValue, (v, max) => UpdateVital(Vitals[v], max));\n", "", "the vitals removed");
            Mutated(ReleaseFault, src, "WorldManager.ThreadSafeTeleport(this, entry,", "WorldManager.ThreadSafeTeleport(this, Sanctuary,", "the teleport goes elsewhere");
            Mutated(ReleaseFault, src, "            Volatile.Write(ref gauntletArrivalShieldUntil, nowUnix + GauntletArrivalShieldSeconds);\n", "", "no expiry stamp");
            Mutated(ReleaseFault, src, "            GauntletArrivalShieldExpiry(currentUnixTime);\n", "", "the expiry never checked");
        }

        /// <summary>Every site that sets or ends an ordinary lifestone protection drops the shield's stamp first, and so does the Gauntlet death latch.</summary>
        internal static string StampClearFault(string death, string gauntlet)
        {
            const string clear = "ClearGauntletArrivalShieldStamp();";

            if (!Body(death, "public void SetLifestoneProtection()").TrimStart('{', ' ', '\r', '\n').StartsWith(clear, StringComparison.Ordinal))
                return "SetLifestoneProtection does not drop the shield stamp first";

            if (!Body(death, "public void LifestoneProtectionDispel()").TrimStart('{', ' ', '\r', '\n').StartsWith(clear, StringComparison.Ordinal))
                return "LifestoneProtectionDispel does not drop the shield stamp first";

            var latch = Body(gauntlet, "private bool LatchGauntletDeath()");
            var gotRun = latch.IndexOf("if (run == null)\n                    return false;", StringComparison.Ordinal);
            var cleared = latch.IndexOf(clear, StringComparison.Ordinal);

            if (gotRun < 0 || cleared < gotRun)
                return "the Gauntlet death latch does not drop the shield stamp once the death is latched";

            return null;
        }

        [TestMethod]
        public void ShieldStamp_DroppedByEveryOrdinaryProtectionSite_AndTheDeathLatch()
        {
            var death = Death();
            var src = Gauntlet();
            Assert.IsNull(StampClearFault(death, src));

            Mutated(d => StampClearFault(d, src), death, "        public void SetLifestoneProtection()\n        {\n            ClearGauntletArrivalShieldStamp();\n", "        public void SetLifestoneProtection()\n        {\n", "SetLifestoneProtection keeps the stamp");
            Mutated(d => StampClearFault(d, src), death, "        public void LifestoneProtectionDispel()\n        {\n            ClearGauntletArrivalShieldStamp();\n", "        public void LifestoneProtectionDispel()\n        {\n", "the dispel keeps the stamp");
            Mutated(g => StampClearFault(death, g), src, "                ClearGauntletArrivalShieldStamp();\n\n                Session?.Network", "\n                Session?.Network", "the death latch keeps the stamp");
        }

        /// <summary>The shield stamp is a plain double read and written only through Volatile (no torn reads across threads).</summary>
        internal static string StampAccessFault(string gauntlet)
        {
            if (!gauntlet.Contains("        private double gauntletArrivalShieldUntil;\n", StringComparison.Ordinal))
                return "the stamp is not a plain double";

            var all = Count(gauntlet, "gauntletArrivalShieldUntil");
            var volatileAccess = Count(gauntlet, "Volatile.Read(ref gauntletArrivalShieldUntil)") + Count(gauntlet, "Volatile.Write(ref gauntletArrivalShieldUntil,");

            // the declaration aside, every use is a Volatile access
            return all - 1 == volatileAccess ? null : $"{all - 1 - volatileAccess} plain access(es) to the stamp";
        }

        [TestMethod]
        public void ShieldStamp_IsAVolatileDouble()
        {
            var src = Gauntlet();
            Assert.IsNull(StampAccessFault(src));
            Mutated(StampAccessFault, src, "        private double gauntletArrivalShieldUntil;\n", "        private double? gauntletArrivalShieldUntil;\n", "a nullable stamp");
            Mutated(StampAccessFault, src, "            var until = Volatile.Read(ref gauntletArrivalShieldUntil);", "            var until = gauntletArrivalShieldUntil;", "a plain read");
        }

        /// <summary>
        /// The relog reseed: called from the 1 Hz check after its gate, only once the login is done and no teleport runs, and
        /// before the expiry; and its Gauntlet test reads both the copy and the hold.
        /// </summary>
        internal static string LoginShieldFault(string gauntlet)
        {
            var tick = Body(gauntlet, "private void GauntletHoldTick(double currentUnixTime)");
            const string guard = "if (!gauntletLoginShieldChecked && FirstEnterWorldDone && !Teleporting)\n                GauntletLoginShieldCheck(currentUnixTime, InGauntletForShield(), CachedHeartbeatInterval);";

            var gate = tick.IndexOf("nextGauntletHoldCheckTime = currentUnixTime + 1.0;", StringComparison.Ordinal);
            var call = tick.IndexOf(guard, StringComparison.Ordinal);
            var expiry = tick.IndexOf("GauntletArrivalShieldExpiry(currentUnixTime);", StringComparison.Ordinal);

            if (call < 0 || Count(tick, "GauntletLoginShieldCheck(") != 1)
                return "the 1 Hz check does not call the relog reseed exactly once, behind the not-busy guard";

            if (call < gate)
                return "the relog reseed runs ahead of the 1 Hz gate";

            if (expiry < call)
                return "the relog reseed runs after the expiry";

            var lookup = Body(gauntlet, "private bool InGauntletForShield()");

            if (!lookup.Contains("(instance != 0 && LiveGauntletDeathHooks.RunForInstance(instance) != null)", StringComparison.Ordinal))
                return "the reseed's Gauntlet test does not read the copy";

            if (!lookup.Contains("|| LiveGauntletDeathHooks.Instance.Holds.RunOf(Guid.Full) != null;", StringComparison.Ordinal))
                return "the reseed's Gauntlet test does not read the hold";

            return null;
        }

        [TestMethod]
        public void RelogReseed_WiredAfterTheGate_NotBusy_BeforeTheExpiry()
        {
            var src = Gauntlet();
            Assert.IsNull(LoginShieldFault(src));

            const string call = "            if (!gauntletLoginShieldChecked && FirstEnterWorldDone && !Teleporting)\n                GauntletLoginShieldCheck(currentUnixTime, InGauntletForShield(), CachedHeartbeatInterval);\n";
            const string expiry = "            GauntletArrivalShieldExpiry(currentUnixTime);\n";

            Mutated(LoginShieldFault, src, call, "", "the reseed call removed");
            Mutated(LoginShieldFault, src, "if (!gauntletLoginShieldChecked && FirstEnterWorldDone && !Teleporting)", "if (!gauntletLoginShieldChecked)", "the busy guard dropped");
            Mutated(LoginShieldFault, src, "GauntletLoginShieldCheck(currentUnixTime, InGauntletForShield(), CachedHeartbeatInterval);", "GauntletLoginShieldCheck(currentUnixTime, true, CachedHeartbeatInterval);", "the Gauntlet test stubbed");
            Mutated(LoginShieldFault, src, "            nextGauntletHoldCheckTime = currentUnixTime + 1.0;\n", "            nextGauntletHoldCheckTime = currentUnixTime + 1.0;\n" + expiry, "an expiry ahead of the reseed");
            Mutated(LoginShieldFault, src, "(instance != 0 && LiveGauntletDeathHooks.RunForInstance(instance) != null)", "(false)", "the copy term stubbed");
            Mutated(LoginShieldFault, src, "|| LiveGauntletDeathHooks.Instance.Holds.RunOf(Guid.Full) != null;", "|| false;", "the hold term stubbed");
        }

        [TestMethod]
        public void HoldCheck_RunsFromPlayerTick_At1Hz_SkippingBusyPlayers()
        {
            var tick = Tick();
            var gauntlet = Gauntlet();
            Assert.IsNull(TickFault(tick, gauntlet));

            var droppedCall = tick.Replace("            GauntletHoldTick(currentUnixTime);\n", "");
            Assert.AreNotEqual(tick, droppedCall, "guard: the call mutation applied");
            Assert.IsNotNull(TickFault(droppedCall, gauntlet), "mutation: the call removed");

            Mutated(g => TickFault(tick, g), gauntlet, "nextGauntletHoldCheckTime = currentUnixTime + 1.0;", "nextGauntletHoldCheckTime = currentUnixTime;", "the throttle removed");
            Mutated(g => TickFault(tick, g), gauntlet, "IsDead || IsInDeathProcess || Teleporting || !FirstEnterWorldDone", "IsDead || Teleporting || !FirstEnterWorldDone", "a dying player checked");
        }
    }
}
