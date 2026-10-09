using System;
using System.IO;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Source-text pins for PR C1's call sites that no test here can drive (each needs a live, in-world Player):
    /// every class ability read site A1-A5 and the equipment-mod site reads the COMBINED predicate and never
    /// IsPkFacetRuleActive, the binding, or an arena flag directly (Docs/Pvp/DESIGN.md "Must stay identical");
    /// the spend gates refuse in a match; every death-penalty site reads the one latched PvpMatchDeathInProgress;
    /// the gate sits ahead of the retail rules; and the login restore runs before the PK Lite to NPK check.
    ///
    /// Same machinery as WeaponModSuppressionWiringTests: comments are stripped first (its StripComments, which
    /// that class proves), each pin is searched only inside its own method's brace-matched body (or, for an
    /// expression-bodied member, up to the first semicolon), and whitespace is ignored on both sides.
    /// </summary>
    [TestClass]
    public class PvpArenaWiringTests
    {
        private const string ClassAbilities = "Source/ACE.Server/WorldObjects/Player_ClassAbilities.cs";
        private const string Exsanguinate = "Source/ACE.Server/ClassAbilities/Abilities/ExsanguinateAbility.cs";
        private const string WorldObjectMagic = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";
        private const string EquipmentMods = "Source/ACE.Server/WorldObjects/Creature_EquipmentMods.cs";
        private const string PickupBoons = "Source/ACE.Server/WorldObjects/Player_PickupBoons.cs";
        private const string Location = "Source/ACE.Server/WorldObjects/Player_Location.cs";
        private const string WeaponMods = "Source/ACE.Server/WorldObjects/Player_WeaponMods.cs";
        private const string Tokens = "Source/ACE.Server/WorldObjects/Player_ClassAbilityTokens.cs";
        private const string Bank = "Source/ACE.Server/WorldObjects/Player_Bank.cs";
        private const string Death = "Source/ACE.Server/WorldObjects/Player_Death.cs";
        private const string Combat = "Source/ACE.Server/WorldObjects/Player_Combat.cs";
        private const string FacetPk = "Source/ACE.Server/WorldObjects/Player_FacetPk.cs";
        private const string PlayerFile = "Source/ACE.Server/WorldObjects/Player.cs";
        private const string PetDevice = "Source/ACE.Server/WorldObjects/PetDevice.cs";
        private const string WorldManager = "Source/ACE.Server/Managers/WorldManager.cs";
        private const string Facets = "Source/ACE.Server/WorldObjects/Player_Facets.cs";
        private const string Trainer = "Source/ACE.Server/ClassAbilities/ClassAbilityTrainer.cs";
        private const string PvpArena = "Source/ACE.Server/WorldObjects/Player_PvpArena.cs";
        private const string Weapon = "Source/ACE.Server/WorldObjects/WorldObject_Weapon.cs";

        /// <summary>No mask site may read any of these: each goes through a combined predicate instead.</summary>
        private static readonly string[] ForbiddenAtMaskSites =
        {
            "IsPkFacetRuleActive", "pvpBinding", "PvpBinding", "IsPvpClassAbilityMaskActive", "IsPvpEquipmentModMaskActive",
            "IsPvpWeaponModMaskActive", "IsPvpPickupBoonMaskActive", "IsPvpTurnSpeedMaskActive"
        };

        // ================= A1-A5 and the equipment-mod site =================

        [TestMethod]
        public void A1_BuildClassAbilityHookCache_ReadsTheCombinedPredicate() =>
            AssertMaskSite(ClassAbilities, "private (T skill, int rank)[] BuildClassAbilityHookCache<T>(IReadOnlyList<T> handlers) where T : IClassAbility",
                "if (ClassAbilitySuppressed) return Array.Empty<(T, int)>();");

        [TestMethod]
        public void A2_TryGetClassAbility_ReadsTheCombinedPredicate() =>
            AssertMaskSite(ClassAbilities, "public bool TryGetClassAbility(ClassAbilityId id, out int rank)",
                "if (rank > 0 && ClassAbilitySuppressed) rank = 0;");

        [TestMethod]
        public void A3_EnhancedSkillBonus_ReadsTheCombinedPredicate() =>
            AssertMaskSite(ClassAbilities, "public int GetEnhancedSkillBonus(Skill skill)", "if (ClassAbilitySuppressed) return 0;");

        [TestMethod]
        public void A3_EnhancedAttributeBonus_ReadsTheCombinedPredicate() =>
            AssertMaskSite(ClassAbilities, "public int GetEnhancedAttributeBonus(PropertyAttribute attribute)", "if (ClassAbilitySuppressed) return 0;");

        [TestMethod]
        public void A3_EnhancedVitalBonus_ReadsTheCombinedPredicate() =>
            AssertMaskSite(ClassAbilities, "public int GetEnhancedVitalBonus(PropertyAttribute2nd vital)", "if (ClassAbilitySuppressed) return 0;");

        [TestMethod]
        public void A4_GetClassAbilityRating_ReadsTheCombinedPredicate() =>
            AssertMaskSite(ClassAbilities, "public int GetClassAbilityRating(ClassAbilityId id)", "if (ClassAbilitySuppressed) return 0;");

        [TestMethod]
        public void A5_ExsanguinateReadout_RawRankRead_ReadsTheCombinedPredicate() =>
            AssertMaskSite(Exsanguinate, "public ClassAbilityReadout GetReadout(Player player, int rank)",
                "var reserveRank = player.ClassAbilitySuppressed ? 0 : player.GetClassAbilityRank(ClassAbilityId.SanguineReserve);");

        [TestMethod]
        public void A5_DrainTransfusion_RawRankRead_ReadsTheCombinedPredicate() =>
            AssertMaskSite(WorldObjectMagic, "private List<Creature> GetDrainSurplusFellows(",
                "var transfusionRank = caster == null || caster.ClassAbilitySuppressed ? 0 : caster.GetClassAbilityRank(ClassAbilityId.Transfusion);");

        [TestMethod]
        public void EquipmentModSite_ReadsTheCombinedPredicate() =>
            AssertMaskSite(EquipmentMods, "public double GetEquippedModValue(EquipmentModId modId)",
                "return FacetPk.EquipmentModValue(this is Player player && player.EquipmentModSuppressed,");

        [TestMethod]
        public void PickupSpeedSite_ReadsTheCombinedPredicate() =>
            AssertMaskSite(PickupBoons, "private float GetPickupAnimationSpeed()", "var suppressed = PickupBoonSuppressed;");

        [TestMethod]
        public void TurnSpeedSite_ReadsTheCombinedPredicate()
        {
            var body = ExpressionBody(Location, "public float TurnToSpeed =>");

            AssertContains(body, "FacetPk.TurnSpeed(TurnSpeedSuppressed,", "TurnToSpeed");
            AssertNoneOf(body, ForbiddenAtMaskSites, "TurnToSpeed");
        }

        /// <summary>WeaponModSuppressed IS the combined predicate, so it reads both terms - and only through the pure helper.</summary>
        [TestMethod]
        public void WeaponModSuppressed_OrsTheArenaTermThroughThePureHelper()
        {
            var body = ExpressionBody(WeaponMods, "public bool WeaponModSuppressed =>");

            AssertContains(body, "WeaponModSuppression.Suppressed(IsPkFacetRuleActive, IsPvpWeaponModMaskActive)", "WeaponModSuppressed");
        }

        // ================= spend gates S1-S5 refuse =================

        [TestMethod]
        public void S1_TryBuyClassAbilityToken_RefusesInAMatch() =>
            AssertContains(MethodBody(ClassAbilities, "public bool TryBuyClassAbilityToken(ClassAbilityTokenCatalog.Offering offering, out string error)"),
                "if (IsInPvpMatch) { error = PvpArenaText.AbilitySpendRefused; return false; }", "S1");

        [TestMethod]
        public void S2_ValidateClassAbilityRankPrerequisites_RefusesInAMatch() =>
            AssertContains(MethodBody(ClassAbilities, "private bool ValidateClassAbilityRankPrerequisites(ClassAbilityDefinition skill, out int rank, out string error)"),
                "if (IsInPvpMatch) { error = PvpArenaText.AbilitySpendRefused; return false; }", "S2");

        [TestMethod]
        public void S3_CanBuyClassAbilityToken_RefusesInAMatch() =>
            AssertContains(MethodBody(Tokens, "public bool CanBuyClassAbilityToken(ClassAbilityDefinition def, int tier, out string error)"),
                "if (IsInPvpMatch) { error = PvpArenaText.AbilitySpendRefused; return false; }", "S3");

        [TestMethod]
        public void S4_GetBankedAlternateCurrency_ReadsZeroPointsInAMatch() =>
            AssertContains(MethodBody(Bank, "public long GetBankedAlternateCurrency(uint wcid)"),
                "return FacetPk.CanSpendClassAbilityPoints(IsPkFacetRuleActive) && !IsInPvpMatch ? AvailableClassAbilityPoints : 0;", "S4");

        [TestMethod]
        public void S5_DebitBankedAlternateCurrency_RefusesPointsInAMatch() =>
            AssertContains(MethodBody(Bank, "public string DebitBankedAlternateCurrency(uint wcid, long amount, out bool stateUnknown)"),
                "if (wcid == ClassAbilityPointCurrencyWcid && (!FacetPk.CanSpendClassAbilityPoints(IsPkFacetRuleActive) || IsInPvpMatch)) return null;", "S5");

        /// <summary>S6: unlearning refuses in a match, as the very first thing after the out params are set.</summary>
        [TestMethod]
        public void S6_UnlearnClassAbility_RefusesInAMatch_BeforeAnythingMoves()
        {
            var body = Squash(MethodBody(ClassAbilities, "public bool UnlearnClassAbility(ClassAbilityDefinition skill, out string error, out int refunded, bool ignoreFee = false,"));

            Assert.IsTrue(body.StartsWith(Squash("{ error = null; refunded = 0; if (IsInPvpMatch) { error = PvpArenaText.AbilitySpendRefused; return false; }"), StringComparison.Ordinal),
                "UnlearnClassAbility must refuse in a match before any rank, refund or fee moves");
        }

        /// <summary>S6, the full respec: its flat fee is charged before the per-ability loop, so it refuses before the charge.</summary>
        [TestMethod]
        public void S6_FullRespec_RefusesInAMatch_BeforeTheFeeIsCharged()
        {
            var body = Squash(MethodBody(Trainer, "private static void HandleRespec(WorldObject npc, Player player)"));

            var refusal = body.IndexOf(Squash("if (player.IsInPvpMatch) { Send(player, PvpArenaText.AbilitySpendRefused); return; }"), StringComparison.Ordinal);
            var charge = body.IndexOf(Squash("if (!player.TrySpendLuminanceIncludingBank(currentFee))"), StringComparison.Ordinal);

            Assert.IsTrue(refusal >= 0, "the full respec's in-match refusal is missing");
            Assert.IsTrue(charge > refusal, "the full respec must refuse before it charges the fee");
        }

        // ================= facet switch =================

        /// <summary>CheckFacetGates refuses in a match first, and TrySwitchFacet runs CheckFacetGates before any state changes.</summary>
        [TestMethod]
        public void FacetSwitch_RefusedInAMatch_FirstGate_AndTrySwitchFacetGatesFirst()
        {
            var gates = Squash(MethodBody(Facets, "internal bool CheckFacetGates(int targetSlot, out string refusal)"));

            Assert.IsTrue(gates.StartsWith(Squash("{ refusal = null; if (IsInPvpMatch) { refusal = PvpArenaText.FacetSwitchRefused; return false; }"), StringComparison.Ordinal),
                "CheckFacetGates must refuse in a match before any other gate");

            var trySwitch = Squash(MethodBody(Facets, "public bool TrySwitchFacet(int targetSlot, bool confirmed, bool trim, out string refusal, out bool needsConfirmation, out bool awaitingVault)"));

            Assert.IsTrue(trySwitch.StartsWith(Squash("{ needsConfirmation = false; awaitingVault = false; if (!CheckFacetGates(targetSlot, out refusal)) return false;"), StringComparison.Ordinal),
                "TrySwitchFacet must run CheckFacetGates before any state changes");
        }

        // ================= PK kill credit =================

        /// <summary>
        /// HandlePKDeathBroadcast credits no PK or PK Lite kill for an in-match death: the gate reads the binding (set
        /// when this runs) and the latch (not yet set, see the order pin below), and comes before both credits.
        /// </summary>
        [TestMethod]
        public void HandlePKDeathBroadcast_InMatchDeath_CreditsNoKill()
        {
            var body = Squash(MethodBody(Death, "public void HandlePKDeathBroadcast(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)"));

            Assert.IsTrue(body.StartsWith(Squash("{ if (IsInPvpMatch || PvpMatchDeathInProgress) return;"), StringComparison.Ordinal),
                "HandlePKDeathBroadcast must return for an in-match death before anything else");
            Assert.IsTrue(body.IndexOf("PlayerKillsPkl++", StringComparison.Ordinal) > 0, "the PK Lite credit this gate guards has moved");
            Assert.IsTrue(body.IndexOf("PlayerKillsPk++", StringComparison.Ordinal) > 0, "the PK credit this gate guards has moved");

            AssertContains(MethodBody(Death, "public override DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)"),
                "HandlePKDeathBroadcast(lastDamager, topDamager);", "Player.OnDeath");
        }

        /// <summary>
        /// The call order the gate above relies on: every bare Die() call in ACE.Server is immediately preceded by an
        /// OnDeath call on the same receiver, so HandlePKDeathBroadcast (inside Player.OnDeath) runs BEFORE Die()
        /// takes the latch. While the binding is set at that moment, the gate holds; this pin catches a new death path
        /// that reverses the order. (The two-argument Die calls are the smite path, which calls OnDeath() first, and
        /// /die, which never calls OnDeath and so never reaches HandlePKDeathBroadcast at all.)
        /// </summary>
        [TestMethod]
        public void EveryBareDieCall_IsPrecededByOnDeath_OnTheSameReceiver()
        {
            var anchor = FindInSourceTree(PlayerFile);
            Assert.IsNotNull(anchor, $"Could not find {PlayerFile} by walking up from {AppContext.BaseDirectory}.");

            // Source/ACE.Server/WorldObjects/Player.cs -> Source/ACE.Server
            var root = Path.GetDirectoryName(Path.GetDirectoryName(anchor));

            var call = new System.Text.RegularExpressions.Regex(@"(?<![\w.])((?:[A-Za-z_]\w*\.)*)Die\(\s*\)\s*;");
            var sites = 0;

            foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var source = WeaponModSuppressionWiringTests.StripComments(File.ReadAllText(path));

                foreach (System.Text.RegularExpressions.Match m in call.Matches(source))
                {
                    sites++;

                    var previousEnd = source.LastIndexOf(';', m.Index - 1);
                    Assert.IsTrue(previousEnd > 0, $"{path}: no statement before {m.Value}");

                    var previousStart = Math.Max(source.LastIndexOfAny(new[] { ';', '{', '}' }, previousEnd - 1), 0);
                    var previous = source.Substring(previousStart, previousEnd - previousStart);

                    Assert.IsTrue(previous.Contains(m.Groups[1].Value + "OnDeath(", StringComparison.Ordinal),
                        $"{path}: '{m.Value.Trim()}' is not immediately preceded by {m.Groups[1].Value}OnDeath(...); previous statement:{previous}");
                }
            }

            Assert.IsTrue(sites >= 12, $"expected at least the 12 known bare Die() call sites, found {sites} - is the scan still reaching them?");
        }

        // ================= enter: foreign harm removed =================

        [TestMethod]
        public void EnterPvpMatchNow_RemovesHarmFromOtherPlayers_AfterTheMasksAreOn()
        {
            var body = Squash(MethodBody(PvpArena, "internal void EnterPvpMatchNow(PvpPlayerBinding binding, Position exitTo)"));

            var bind = body.IndexOf(Squash("pvpBinding = binding;"), StringComparison.Ordinal);
            var remove = body.IndexOf(Squash("RemoveHarmFromOtherPlayers();"), StringComparison.Ordinal);

            Assert.IsTrue(bind >= 0, "EnterPvpMatchNow no longer sets the binding the way this pin expects");
            Assert.IsTrue(remove > bind, "EnterPvpMatchNow must remove other players' harmful enchantments, after binding");

            AssertContains(MethodBody(PvpArena, "internal int RemoveHarmFromOtherPlayers()"),
                "if (PvpPlayerRules.IsHarmFromAnotherPlayer(entry, Guid.Full)) foreign.Add(entry);", "RemoveHarmFromOtherPlayers");
        }

        // ================= death (H4/H5) =================

        /// <summary>
        /// Player.Die: the latch is taken beside the other latches and BEFORE the first penalty site, and the vitae,
        /// purge and respite sites each read the latched getter through their waiver.
        /// </summary>
        [TestMethod]
        public void Die_LatchesFirst_ThenEveryPenaltySiteReadsTheLatch()
        {
            var body = MethodBody(Death, "protected override void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)");

            const string latch = "pvpMatchDeathInProgress = LatchPvpMatchDeath(lastDamager, out pvpMatchDeathWaiver);";
            const string vitae = "&& !PvpPlayerRules.WaivesVitae(PvpMatchDeathInProgress, pvpMatchDeathWaiver)) InflictVitaePenalty();";
            const string purge = "if (!arenaDeath && !worldEventDeathInProgress && !PvpPlayerRules.WaivesEnchantmentPurge(PvpMatchDeathInProgress, pvpMatchDeathWaiver))";
            const string respite = "if ((IsPKDeath(topDamager) || IsPKLiteDeath(topDamager)) && !PvpPlayerRules.WaivesRespite(PvpMatchDeathInProgress, pvpMatchDeathWaiver)) SetMinimumTimeSincePK();";

            AssertContains(body, latch, "Die latch");
            AssertContains(body, vitae, "Die vitae");
            AssertContains(body, purge, "Die purge");
            AssertContains(body, respite, "Die respite");

            var squashed = Squash(body);
            Assert.IsTrue(squashed.IndexOf(Squash(latch), StringComparison.Ordinal) < squashed.IndexOf(Squash(vitae), StringComparison.Ordinal),
                "the latch must be taken before the vitae site reads it");

            AssertNoLiveBindingRead(body, "Die");
        }

        [TestMethod]
        public void ThreadSafeTeleportOnDeath_ReturnsToTheStampedExit_AndClearsTheLatch()
        {
            var body = MethodBody(Death, "public void ThreadSafeTeleportOnDeath()");

            AssertContains(body, "|| ThreadDungeonDeathInProgress || PvpMatchDeathInProgress) { var exitTo = GetPosition(PositionType.EphemeralRealmExitTo);", "death return");
            AssertContains(body, "pvpMatchDeathInProgress = false; pvpMatchDeathWaiver = null;", "latch clear");
            AssertNoLiveBindingRead(body, "ThreadSafeTeleportOnDeath");
        }

        [TestMethod]
        public void CalculateDeathItems_BothPaths_ReadTheLatch()
        {
            foreach (var signature in new[]
            {
                "public List<WorldObject> CalculateDeathItems(Corpse corpse)",
                "public List<WorldObject> CalculateDeathItems_Olthoi(Corpse corpse, bool hadVitae, bool killerIsOlthoiPlayer, bool killerIsPkPlayer)"
            })
            {
                var body = MethodBody(Death, signature);

                AssertContains(body, "|| PvpPlayerRules.WaivesItemLoss(PvpMatchDeathInProgress, pvpMatchDeathWaiver)) return new List<WorldObject>();", signature);
                AssertNoLiveBindingRead(body, signature);
            }
        }

        // ================= the gate, the facet heartbeat, logout, pets, login =================

        /// <summary>The arena gate is checked ahead of every retail PK rule in CheckPKStatusVsTarget, harmful actions only.</summary>
        [TestMethod]
        public void CheckPKStatusVsTarget_GateRunsBeforeTheRetailRules()
        {
            var body = Squash(MethodBody(Combat, "public override List<WeenieErrorWithString> CheckPKStatusVsTarget(WorldObject target, Spell spell)"));

            var gate = body.IndexOf(Squash("if ((spell == null || spell.IsHarmful) && TryPvpArenaGate(targetCreature as Player, spell, out var arenaResult)) return arenaResult;"), StringComparison.Ordinal);
            var firstRetailRule = body.IndexOf(Squash("if (PlayerKillerStatus == PlayerKillerStatus.Free"), StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0, "the arena gate call is missing from CheckPKStatusVsTarget");
            Assert.IsTrue(firstRetailRule > gate, "the arena gate must run before the first retail PK rule");
        }

        /// <summary>
        /// Code-review follow-up: TryProcItem's arena-spell-rules guard must run before EITHER of the two
        /// branches that actually cast at `target` (Vestements and the plain else) - a cast-on-strike proc
        /// otherwise bypasses CheckPKStatusVsTarget entirely, the bug this guard exists to close (see
        /// Docs/Pvp/DESIGN.md "Cast-on-strike proc coverage fix"). Deleting the guard clause makes this fail,
        /// because the guard text this test pins would then be absent - see PROOF OF DISCRIMINATION below.
        /// </summary>
        [TestMethod]
        public void TryProcItem_ArenaSpellRulesGuard_RunsBeforeTheTargetedCastBranches()
        {
            var body = Squash(MethodBody(Weapon, "public void TryProcItem(WorldObject attacker, Creature target, bool selfTarget)"));

            var guard = body.IndexOf(Squash("if (spell.NonComponentTargetType != ItemType.None && attacker is Player attackerPlayer && target is Player targetPlayer && spell.IsHarmful && attackerPlayer.CheckPKStatusVsTarget(targetPlayer, spell) != null) { return; }"), StringComparison.Ordinal);
            var vestementsCall = body.IndexOf(Squash("attacker.TryCastSpell_WithRedirects(spell, target, itemCaster, itemCaster, true, true);"), StringComparison.Ordinal);
            var elseCall = body.IndexOf(Squash("attacker.TryCastSpell(spell, target, itemCaster, itemCaster, true, true);"), StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0, "the arena-spell-rules proc guard is missing from TryProcItem");
            Assert.IsTrue(vestementsCall > guard, "the guard must run before the Vestements branch's TryCastSpell_WithRedirects call");
            Assert.IsTrue(elseCall > guard, "the guard must run before the plain else branch's TryCastSpell call");

            // PROOF OF DISCRIMINATION (2026-09-26, run manually, not left wired in as a test-of-a-test): with
            // the guard's `if` block deleted from WorldObject_Weapon.cs (leaving only the None/Vestements/else
            // chain), `dotnet test --filter "FullyQualifiedName~PvpArenaWiringTests"` failed exactly this
            // method with "Assert.IsTrue(guard >= 0)" / "the arena-spell-rules proc guard is missing from
            // TryProcItem", 31 passed / 1 failed. Restoring the guard (this file's WorldObject_Weapon.cs edit)
            // brought it back to 32/32 passed. So this pin actually depends on the guard's presence, not a
            // vacuous match.
        }

        [TestMethod]
        public void EnforceFacetPkStatus_StandsDownInAMatch()
        {
            var body = Squash(MethodBody(FacetPk, "internal bool EnforceFacetPkStatus(string context, bool broadcast, bool warn)"));

            Assert.IsTrue(body.StartsWith(Squash("{ if (IsInPvpMatch) return false;"), StringComparison.Ordinal),
                "EnforceFacetPkStatus must return false for a player in a match before anything else");
        }

        [TestMethod]
        public void LogOut_ReportsTheForfeitBeforeThePkDeferral_AndLogOutInnerExits()
        {
            var logOut = Squash(MethodBody(PlayerFile, "public bool LogOut(bool clientSessionTerminatedAbruptly = false, bool forceImmediate = false)"));

            var report = logOut.IndexOf(Squash("ReportPvpLogoutForfeit();"), StringComparison.Ordinal);
            var deferral = logOut.IndexOf(Squash("if (PKLogoutActive && !forceImmediate)"), StringComparison.Ordinal);

            Assert.IsTrue(report >= 0 && deferral > report, "the forfeit must be reported before the PK logoff deferral");

            AssertContains(MethodBody(PlayerFile, "public void LogOut_Inner(bool clientSessionTerminatedAbruptly = false)"),
                "if (PvpBinding != null || IsPvpTemplated) ExitPvpMatch(\"logout\");", "LogOut_Inner");
        }

        /// <summary>
        /// PvP Template Facets: the death exit restores at the START of the arrival action, before the vitals reset,
        /// so the 75% vitals are computed from the player's own restored maximums. Exactly one call, and it is told whether
        /// the death went to a battleground pen (a respawn death, which does NOT end participation and so does not restore).
        /// </summary>
        [TestMethod]
        public void DeathArrival_RestoresTheTemplate_BeforeTheVitalsReset()
        {
            var body = Squash(MethodBody(Death, "public void ThreadSafeTeleportOnDeath()"));

            var addAction = body.IndexOf(Squash("teleportChain.AddAction(this, () =>"), StringComparison.Ordinal);
            var restore = body.IndexOf(Squash("PvpTemplateDeathArrival(wentToPen);"), StringComparison.Ordinal);
            var vitals = body.IndexOf(Squash("var newHealth = (uint)Math.Round(Health.MaxValue * 0.75f);"), StringComparison.Ordinal);

            Assert.IsTrue(addAction >= 0 && restore > addAction && vitals > restore, "PvpTemplateDeathArrival must open the arrival action, ahead of the vitals reset");
            Assert.AreEqual(-1, body.IndexOf(Squash("PvpTemplateDeathArrival(wentToPen);"), restore + 1, StringComparison.Ordinal), "called exactly once");
        }

        /// <summary>
        /// PvP Template Facets: the live gateway hands the apply's PlayerCaused and Busy flags on to the coordinator's
        /// EntryFailed intent (the lockout and the busy retry both key on them).
        /// </summary>
        [TestMethod]
        public void LiveGateway_CarriesTheEntryFailureFlags()
        {
            var body = Squash(MethodBody("Source/ACE.Server/Pvp/PvpLiveAdapters.cs", "public void EnterAndTeleport(uint characterId, PvpPlayerBinding binding, Position exitTo, Position spawn)"));

            Assert.IsTrue(body.Contains(Squash("reportFailure: (reason, playerCaused, retryable) =>"), StringComparison.Ordinal), "the report lambda must take both flags");
            Assert.IsTrue(body.Contains(Squash("PvpMatchManager.EntryFailed(characterId, matchId, DateTime.UtcNow, playerCaused, retryable)"), StringComparison.Ordinal), "the intent must carry both flags");
        }

        /// <summary>
        /// L6 (2026-10-03 review): the death arrival keeps the template exactly when the death went to a battleground pen
        /// (DeathArrival_RestoresTheTemplate_BeforeTheVitalsReset pins that wentToPen is what it is handed). This pins what
        /// wentToPen IS: the pen destination being set, computed once from PvpDeathPenDestination().
        /// </summary>
        [TestMethod]
        public void DeathArrival_WentToPen_IsExactlyThePenDestinationBeingSet()
        {
            var body = MethodBody(Death, "public void ThreadSafeTeleportOnDeath()");

            // Bind the identifiers: wentToPen is assigned once, from pvpPen != null, and pvpPen once, from the battleground
            // death destination. A different source (a binding read, a constant, a second assignment) fails here.
            var wentToPen = System.Text.RegularExpressions.Regex.Matches(body, @"\bwentToPen\s*=(?!=)\s*([^;]+);");
            Assert.AreEqual(1, wentToPen.Count, "wentToPen is assigned exactly once");
            Assert.AreEqual(Squash("pvpPen != null"), Squash(wentToPen[0].Groups[1].Value));

            var pvpPen = System.Text.RegularExpressions.Regex.Matches(body, @"\bpvpPen\s*=(?!=)\s*([^;]+);");
            Assert.AreEqual(1, pvpPen.Count, "pvpPen is assigned exactly once");
            Assert.AreEqual(Squash("PvpDeathPenDestination()"), Squash(pvpPen[0].Groups[1].Value));

            Assert.IsTrue(Squash(body).IndexOf(Squash("var pvpPen = PvpDeathPenDestination();"), StringComparison.Ordinal)
                < Squash(body).IndexOf(Squash("var wentToPen = pvpPen != null;"), StringComparison.Ordinal), "the destination is read before the flag");
        }

        /// <summary>
        /// M3 (2026-10-03 review): a battleground exit while the player is still dying defers the template restore to the end
        /// of the death sequence. Binds the call: ExitPvpMatch hands ExitPvpMatchNow the rule's answer for (returnFromPen,
        /// IsInDeathProcess), and only that.
        /// </summary>
        [TestMethod]
        public void ExitPvpMatch_PassesTheDyingPenDeferral()
        {
            var body = Squash(MethodBody(PvpArena, "public void ExitPvpMatch(string context, bool returnFromPen)"));

            Assert.IsTrue(body.Contains(Squash("var deferRestore = PvpPlayerRules.DefersTemplateRestoreToPenBackstop(returnFromPen, IsInDeathProcess);"), StringComparison.Ordinal));
            Assert.IsTrue(body.Contains(Squash("ExitPvpMatchNow(context, deferRestore);"), StringComparison.Ordinal));
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(body, @"ExitPvpMatchNow\(").Count, "one exit call");
        }

        /// <summary>
        /// PvP Template Facets (owner ruling 2026-10-03, templated by default): the live gateway's pre-template placement
        /// runs ONLY for a binding that says it opted out (Untemplated), and is checked before the template entry
        /// sequence. A binding that does not say so - arena or battleground - always goes through the template path.
        /// </summary>
        [TestMethod]
        public void LiveGateway_PlainPlacement_OnlyForAnOptedOutBinding()
        {
            var body = Squash(MethodBody("Source/ACE.Server/Pvp/PvpLiveAdapters.cs", "public void EnterAndTeleport(uint characterId, PvpPlayerBinding binding, Position exitTo, Position spawn)"));

            var guard = body.IndexOf(Squash("if (binding?.Match != null && binding.Untemplated)"), StringComparison.Ordinal);
            var plainEnter = body.IndexOf(Squash("p.EnterPvpMatch(binding, exitTo);"), StringComparison.Ordinal);
            var templated = body.IndexOf(Squash("PvpTemplateEntrySequence"), StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0, "the plain path is gated on the opt-out flag");
            Assert.IsTrue(plainEnter > guard && templated > plainEnter, "plain placement sits inside the opt-out guard, ahead of the template sequence");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(body, System.Text.RegularExpressions.Regex.Escape(Squash("p.EnterPvpMatch(binding, exitTo);"))).Count, "no other blind placement");
        }
        /// <summary>
        /// PvP Template Facets: a /pvptemplate snapshot with no display name carries the key's current name forward,
        /// so a re-snapshot never resets a name set with /pvptemplate name back to the key.
        /// </summary>
        [TestMethod]
        public void Snapshot_WithoutAName_KeepsTheExistingName()
        {
            var body = Squash(MethodBody("Source/ACE.Server/Command/Handlers/PvpTemplateAdminCommands.cs",
                "private static void HandleSnapshot(Session session, string rawKey, string characterName, string displayName, string adminName)"));

            Assert.IsTrue(body.Contains(Squash("DatabaseManager.Shard.GetPvpTemplate(key, (row, status) => OnWorld(() => Snapshot(row?.DisplayName)));"), StringComparison.Ordinal));
        }

        /// <summary>PvP Template Facets: TryParse (list, inspect, every catalog load) never warms the kit; formation is the one warm path.</summary>
        [TestMethod]
        public void TryParse_DoesNotWarmTheKit()
        {
            var body = MethodBody("Source/ACE.Server/Pvp/Templates/PvpTemplateSnapshotService.cs",
                "public static bool TryParse(PvpTemplateRecord row, out PvpTemplateDefinition definition, out string error)");

            Assert.IsFalse(body.Contains("WarmKitWeeniesAsync(", StringComparison.Ordinal));
            Assert.IsTrue(body.Contains("definition.Version = row.Version;", StringComparison.Ordinal), "sanity: this is the TryParse body");
        }

        /// <summary>PvP Template Facets: the apply's busy refusal is player-caused, busy and silent; its room refusal is player-caused.</summary>
        [TestMethod]
        public void Apply_BusyAndRoomRefusals_ArePlayerCaused()
        {
            var body = Squash(MethodBody("Source/ACE.Server/WorldObjects/Player_PvpTemplate.cs", "internal PvpTemplateApplyResult ApplyPvpTemplateNow(PvpTemplateDefinition definition, Guid matchId)"));

            Assert.IsTrue(body.Contains(Squash("return RefuseApply(PvpTemplateText.ApplyBusy, matchId, \"busy with gear to strip\", playerCaused: true, busy: true, announce: false);"), StringComparison.Ordinal), "busy");
            Assert.IsTrue(body.Contains(Squash("return RefuseApply(roomRefusal, matchId, \"no pack room\", playerCaused: true);"), StringComparison.Ordinal), "room");
        }

        /// <summary>
        /// PvP Template Facets: the restore is ExitPvpMatchNow's first step, ahead of the not-in-a-match early return. The
        /// one exception is the dying-pen deferral (M3, 2026-10-03 review), which skips it and nothing else.
        /// </summary>
        [TestMethod]
        public void ExitPvpMatchNow_RestoresBeforeAnythingElse()
        {
            var body = Squash(MethodBody(PvpArena, "internal bool ExitPvpMatchNow(string context, bool deferRestoreToPenBackstop = false)"));

            Assert.IsTrue(body.StartsWith(Squash("{ if (deferRestoreToPenBackstop)"), StringComparison.Ordinal), "the deferral test opens the method");

            var restore = body.IndexOf(Squash("else { try { PvpTemplateSettings.Restore(this, $\"match exit: {context}\"); }"), StringComparison.Ordinal);
            var earlyReturn = body.IndexOf(Squash("if (binding == null && marker == null) return false;"), StringComparison.Ordinal);

            Assert.IsTrue(restore >= 0 && earlyReturn > restore, "the template restore must run first, before the early return for an unbound player");
        }

        [TestMethod]
        public void PetDevice_RefusesASummonInAMatch_BeforeSummoning()
        {
            var body = Squash(MethodBody(PetDevice, "public override void ActOnUse(WorldObject activator)"));

            var refusal = body.IndexOf(Squash("if (player.IsInPvpMatch)"), StringComparison.Ordinal);
            var summon = body.IndexOf(Squash("SummonCreature(player, wcid"), StringComparison.Ordinal);

            Assert.IsTrue(refusal >= 0 && summon > refusal, "the in-match refusal must come before the summon");
            AssertContains(body, "PvpArenaText.SummonRefused", "PetDevice");
        }

        /// <summary>
        /// The login order (H2): the 9075 restore runs in DoPlayerEnterWorld_Inner BEFORE the dead-instance check and
        /// BEFORE PlayerEnterWorld - whose PK Lite to NPK conversion therefore sees the restored status, not PK Lite.
        /// </summary>
        [TestMethod]
        public void LoginRestore_RunsBeforeTheDeadInstanceCheck_AndBeforePlayerEnterWorld()
        {
            var body = Squash(MethodBody(WorldManager, "private static void DoPlayerEnterWorld_Inner(Session session, Character character, Biota playerBiota, PossessedBiotas possessedBiotas)"));

            var restore = body.IndexOf(Squash("session.Player.RestorePvpMatchStatusAtLogin();"), StringComparison.Ordinal);
            var deadInstance = body.IndexOf(Squash("session.Player.Location.ValidateInstanceDestination(session.Player)"), StringComparison.Ordinal);
            var enterWorld = body.IndexOf(Squash("session.Player.PlayerEnterWorld();"), StringComparison.Ordinal);

            Assert.IsTrue(restore >= 0, "the login restore call is missing");
            Assert.IsTrue(deadInstance > restore, "the restore must run before the dead-instance check");
            Assert.IsTrue(enterWorld > restore, "the restore must run before PlayerEnterWorld (and its PK Lite to NPK check)");
        }

        /// <summary>
        /// Owed Blood (Docs/Pvp/DESIGN.md "Rewards") reaches the player only through this login call, which no test can
        /// drive: DoPlayerEnterWorld_Inner must call session.Player.ScheduleOwedArenaBloodDelivery() - that exact receiver
        /// and that exact member, as a whole identifier - AFTER PlayerEnterWorld (the pack must be in the world first);
        /// and the scheduled member must itself call DeliverOwedArenaBlood.
        /// </summary>
        [TestMethod]
        public void LoginPath_SchedulesOwedBloodDelivery_AfterPlayerEnterWorld()
        {
            var raw = MethodBody(WorldManager, "private static void DoPlayerEnterWorld_Inner(Session session, Character character, Biota playerBiota, PossessedBiotas possessedBiotas)");

            Assert.IsTrue(ContainsToken(raw, "ScheduleOwedArenaBloodDelivery"), "DoPlayerEnterWorld_Inner: ScheduleOwedArenaBloodDelivery is not called (whole identifier, comments stripped)");

            var body = Squash(raw);
            var call = body.IndexOf(Squash("session.Player.ScheduleOwedArenaBloodDelivery();"), StringComparison.Ordinal);
            var enterWorld = body.IndexOf(Squash("session.Player.PlayerEnterWorld();"), StringComparison.Ordinal);

            Assert.IsTrue(call >= 0, "DoPlayerEnterWorld_Inner must call session.Player.ScheduleOwedArenaBloodDelivery(); on the logging-in player");
            Assert.AreEqual(-1, body.IndexOf(Squash("session.Player.ScheduleOwedArenaBloodDelivery();"), call + 1, StringComparison.Ordinal), "called exactly once");
            Assert.IsTrue(enterWorld >= 0 && call > enterWorld, "owed Blood must be scheduled after PlayerEnterWorld");

            var scheduled = MethodBody("Source/ACE.Server/WorldObjects/Player_PvpRewards.cs", "public void ScheduleOwedArenaBloodDelivery()");
            AssertContains(scheduled, "DeliverOwedArenaBlood();", "ScheduleOwedArenaBloodDelivery");
        }

        // ================= helpers =================

        private static void AssertMaskSite(string file, string signature, string expected)
        {
            var body = MethodBody(file, signature);

            AssertContains(body, expected, signature);
            AssertNoneOf(body, ForbiddenAtMaskSites, signature);
        }

        private static void AssertNoLiveBindingRead(string body, string where) =>
            AssertNoneOf(body, new[] { "IsInPvpMatch", "PvpBinding", "pvpBinding" }, where + " (death sites read the latch, never a live binding)");

        private static void AssertNoneOf(string body, string[] tokens, string where)
        {
            foreach (var token in tokens)
                Assert.IsFalse(ContainsToken(body, token), $"{where}: live code reads {token} directly");
        }

        /// <summary>Whole-identifier match, so IsPvpWeaponModMaskActive does not count as a read of PvpBinding and so on.</summary>
        private static bool ContainsToken(string body, string token)
        {
            var at = 0;

            while ((at = body.IndexOf(token, at, StringComparison.Ordinal)) >= 0)
            {
                var before = at == 0 ? ' ' : body[at - 1];
                var afterIndex = at + token.Length;
                var after = afterIndex >= body.Length ? ' ' : body[afterIndex];

                if (!IsIdentifierChar(before) && !IsIdentifierChar(after))
                    return true;

                at = afterIndex;
            }

            return false;
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static void AssertContains(string body, string expected, string where)
        {
            Assert.IsTrue(Squash(body).Contains(Squash(expected), StringComparison.Ordinal),
                $"{where}: expected live code (comments stripped) containing:\n  {expected}");
        }

        private static string LiveSource(string relativePath)
        {
            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            return WeaponModSuppressionWiringTests.StripComments(File.ReadAllText(path));
        }

        private static int FindOnce(string source, string relativePath, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"{relativePath}: declaration not found in live code: {signature}");
            Assert.AreEqual(-1, source.IndexOf(signature, at + signature.Length, StringComparison.Ordinal),
                $"{relativePath}: declaration appears more than once: {signature}");
            return at;
        }

        /// <summary>The brace-matched body of the member declared by <paramref name="signature"/>, comments stripped.</summary>
        private static string MethodBody(string relativePath, string signature)
        {
            var source = LiveSource(relativePath);
            var at = FindOnce(source, relativePath, signature);

            var open = source.IndexOf('{', at + signature.Length);
            Assert.IsTrue(open >= 0, $"{relativePath}: no body after {signature}");

            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"{relativePath}: unbalanced braces after {signature}");
            return null;
        }

        /// <summary>An expression-bodied member: from the signature up to the first semicolon, comments stripped.</summary>
        private static string ExpressionBody(string relativePath, string signature)
        {
            var source = LiveSource(relativePath);
            var at = FindOnce(source, relativePath, signature);

            var end = source.IndexOf(';', at);
            Assert.IsTrue(end > at, $"{relativePath}: no terminating semicolon after {signature}");

            return source.Substring(at + signature.Length, end - at - signature.Length + 1);
        }

        private static string Squash(string text)
        {
            var sb = new StringBuilder(text.Length);

            foreach (var c in text)
            {
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);
            }

            return sb.ToString();
        }

        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
