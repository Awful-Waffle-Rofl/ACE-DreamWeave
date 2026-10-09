using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Spec section 7: exits are opt-in at their call sites. Every voluntary exit must ask the guard AFTER its own
    /// refusals, so a refused exit never prompts, and BEFORE its teleport or first side effect, so a prompt never
    /// follows a spent effect; no forced move (death, eviction, admin, emote) may reach it. A missed voluntary site
    /// is an unprompted exit, not a crash, so only a pin like this catches it.
    /// </summary>
    [TestClass]
    public class ThreadExitCallSiteTests
    {
        private const string Guard = "ThreadExitGuard.TryHoldExit(";

        private const string PlayerLocation = "Source/ACE.Server/WorldObjects/Player_Location.cs";
        private const string Magic = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";
        private const string CombatModeChange = "if (CombatMode != CombatMode.NonCombat)";

        /// <summary>
        /// Each gated exit: its method, text from its LAST refusal, and text from its FIRST effect after that refusal.
        /// Portal recall has two exits (lifestone, portal tie), so it has a row for each.
        /// </summary>
        private static readonly (string Path, string Signature, string LastRefusal, string FirstEffect)[] VoluntaryExits =
        {
            ("Source/ACE.Server/WorldObjects/Portal.cs", "public override ActivationResult CheckUseRequirements(WorldObject activator)", "SpeedSeasonManager.GetActiveSeason() == null", "EmoteManager.OnQuest(player);"),
            (PlayerLocation, "public void HandleActionTeleToHouse()", "WeenieError.YouMustOwnHouseToUseCommand", CombatModeChange),
            (PlayerLocation, "public void HandleActionTeleToLifestone()", "Your spirit has not been attuned to a sanctuary location.", "UpdateVital(Mana, Mana.Current / 2);"),
            (PlayerLocation, "public void HandleActionTeleToMarketPlace()", "WeenieError.YoureTooBusy", CombatModeChange),
            (PlayerLocation, "private void HandleActionTeleToNetworkHub(string networkName, Position drop, WorldRealm realm, string unavailableMessage)", "You must leave the network you are standing in", "EnqueueBroadcast("),
            (PlayerLocation, "public void HandleActionRecallAllegianceHometown()", "if (!VerifyRecallAllegianceHometown())", CombatModeChange),
            (PlayerLocation, "public void HandleActionTeleToMansion()", "if (allegianceHouse == null)", CombatModeChange),
            (PlayerLocation, "public void HandleActionTeleToPkArena()", "WeenieError.YoureTooBusy", CombatModeChange),
            (PlayerLocation, "public void HandleActionTeleToPklArena()", "WeenieError.YoureTooBusy", CombatModeChange),
            (Magic, "private void HandleCastSpell_PortalRecall(Spell spell, Creature targetCreature)", "if (recallDID == null)", "ActionChain lifestoneRecall = new ActionChain();"),
            (Magic, "private void HandleCastSpell_PortalRecall(Spell spell, Creature targetCreature)", "if (!result.Success)", "ActionChain portalRecall = new ActionChain();"),
            (Magic, "private void HandleCastSpell_PortalSending(Spell spell, Creature targetCreature, WorldObject itemCaster)", "WeenieError.YouHaveBeenInPKBattleTooRecently", "ActionChain portalSendingChain = new ActionChain();"),
            (Magic, "private bool HandleCastSpell_FellowPortalSending(Spell spell, Creature targetCreature, WorldObject itemCaster)", "if (distanceToTarget > maxRange)", "var portalSendingChain = new ActionChain();"),
            ("Source/ACE.Server/WorldObjects/Gem.cs", "private void UsePortalGem(Player player)", "It cannot be used this close to its destination's reflection.", "if (UseSound > 0)"),
            ("Source/ACE.Server/Command/Handlers/InstanceCommands.cs", "public static void HandleExitInstance(Session session, params string[] parameters)", "You are not inside an instance.", "player.SetPosition(PositionType.EphemeralRealmExitTo, null);"),
        };

        [TestMethod]
        public void Every_voluntary_exit_asks_after_its_own_refusals_and_before_its_first_effect()
        {
            foreach (var (path, signature, lastRefusal, firstEffect) in VoluntaryExits)
            {
                var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(path), signature);

                var refusal = body.IndexOf(lastRefusal, StringComparison.Ordinal);
                var effect = refusal < 0 ? -1 : body.IndexOf(firstEffect, refusal, StringComparison.Ordinal);
                var guard = refusal < 0 ? -1 : body.IndexOf(Guard, refusal, StringComparison.Ordinal);

                Assert.IsTrue(refusal >= 0 && effect > refusal, $"{path}: anchors moved in {signature}; re-read the method");
                Assert.IsTrue(guard > refusal && guard < effect, $"{path}: {signature} must ask after its last refusal ({lastRefusal}) and before its first effect ({firstEffect})");
            }
        }

        [TestMethod]
        public void The_portal_gate_holds_only_a_portal_standing_in_the_world()
        {
            // Open question 4: a portal recall rebuilds its portal from the weenie and runs CheckUseRequirements on it.
            // That recall has already asked in HandleCastSpell_PortalRecall, so the portal gate must not ask again.
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Portal.cs"), "public override ActivationResult CheckUseRequirements(WorldObject activator)");

            StringAssert.Contains(body, "CurrentLandblock != null && ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(player,");
        }

        [TestMethod]
        public void A_portal_spell_gem_asks_before_it_casts_so_a_held_exit_keeps_the_gem()
        {
            // Gem.ActOnUse(WorldObject) forwards to ActOnUse(activator, false), which runs UseGem; the spell cast and the
            // consume live in UseGem. The cast's own gate returns into UseGem, which would then consume the gem.
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Gem.cs"), "public void UseGem(Player player)");

            var dispel = body.IndexOf("VerifyDispelPKStatus", StringComparison.Ordinal);
            var would = dispel < 0 ? -1 : body.IndexOf("ThreadExitGuard.WouldHoldExit(player)", dispel, StringComparison.Ordinal);
            var rare = would < 0 ? -1 : body.IndexOf("if (RareUsesTimer)", would, StringComparison.Ordinal);
            var cast = rare < 0 ? -1 : body.IndexOf("player.TryCastSpell(spell, player, this", rare, StringComparison.Ordinal);
            // An earlier, returning branch (class ability points) consumes too, so search from the gate onward.
            var consume = cast < 0 ? -1 : body.IndexOf("TryConsumeFromInventoryWithNetworking", cast, StringComparison.Ordinal);

            Assert.IsTrue(dispel >= 0 && would > dispel && rare > would && cast > rare && consume > cast,
                "the gem gate must sit after the dispel PK refusal and before the rare broadcast, the cast and the consume");

            var gate = body.Substring(dispel, rare - dispel);
            StringAssert.Contains(gate, "SpellType.PortalSending");
            StringAssert.Contains(gate, "SpellType.PortalRecall");
            StringAssert.Contains(gate, "SpellType.FellowPortalSending");
            StringAssert.Contains(gate, Guard, "a held exit sends the prompt and re-runs the use on Yes");
        }

        [TestMethod]
        public void Spell_exit_gates_ask_only_for_a_player_caster()
        {
            // Spec section 7: emote and creature casts (EmoteManager CastSpell/CastSpellInstant) reach these handlers too
            // and must never prompt. A Player caster still asks, which keeps a fellow's Fellow Portal Sending and a gem
            // cast (cast as the player) gated, and also leaves developer /castspell prompting (ruled acceptable).
            foreach (var (signature, expected) in new[]
            {
                ("private void HandleCastSpell_PortalRecall(Spell spell, Creature targetCreature)", 2),
                ("private void HandleCastSpell_PortalSending(Spell spell, Creature targetCreature, WorldObject itemCaster)", 1),
                ("private bool HandleCastSpell_FellowPortalSending(Spell spell, Creature targetCreature, WorldObject itemCaster)", 1),
            })
            {
                var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Magic), signature);
                var found = 0;

                foreach (var line in body.Split('\n'))
                {
                    if (!line.Contains(Guard))
                        continue;

                    found++;
                    StringAssert.Contains(line, "if (this is Player && ", $"{signature}: every exit gate must require a Player caster");
                }

                Assert.AreEqual(expected, found, $"{signature}: gate count changed; re-read the method");
            }
        }

        [TestMethod]
        public void Forced_moves_never_reach_the_guard()
        {
            var teleport = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Location.cs"), "public void Teleport(Position _newPosition, bool fromPortal = false)");
            Assert.IsFalse(teleport.Contains(Guard), "Player.Teleport is the forced path; the gate is opt-in at call sites");

            var endRun = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs"), "public static void EndRun(ThreadDungeonRun run, string why)");
            Assert.IsFalse(endRun.Contains(Guard), "EndRun eviction");

            // Text absence only proves these files do not call the guard directly; a route through a gated handler is not
            // caught here. EmoteManager.cs is deliberately not listed: its CastSpell/CastSpellInstant emotes reach the spell
            // exit gates in WorldObject_Magic.cs by design, and those gates skip any non-Player caster (pinned by
            // Spell_exit_gates_ask_only_for_a_player_caster). DeveloperCommands.cs is not listed either: /castspell casts
            // as the developer's Player through TryCastSpell, reaches those same gates and does prompt (ruled acceptable).
            foreach (var path in new[]
            {
                "Source/ACE.Server/WorldObjects/Player_Death.cs",
                "Source/ACE.Server/Managers/WorldManager.cs",
                "Source/ACE.Server/WorldObjects/Player_House.cs",
                "Source/ACE.Server/Command/Handlers/AdminCommands.cs",
                "Source/ACE.Server/Command/Handlers/AdvocateCommands.cs",
            })
            {
                Assert.IsFalse(PooledLootSourceText.Read(path).Contains("ThreadExitGuard"), $"{path} must not call the exit guard directly");
            }
        }
    }
}
