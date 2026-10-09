using System;
using System.Text.RegularExpressions;

using ACE.Server.MlDigsite;
using ACE.Server.MonsterEffects;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SOURCE-TEXT PINS for the Boss Rush mechanic wiring that cannot be driven under this harness: the parts
    /// that need a live landblock, a real creature in the world, or the digsite tick itself.
    ///
    /// These are deliberately structural rather than behavioural. Each one pins an ORDERING or an EXCLUSION
    /// whose violation is silent in play and expensive in the world - a mechanic step firing at objects that
    /// have already been destroyed, a prop entering the world before the encounter has adopted it, an
    /// immortal boss left standing after a teardown. MlDigsiteRulesTests states the same argument for the
    /// digsite's own single-exit-path pins, and this file follows its shape.
    ///
    /// A pin here breaking does NOT necessarily mean the code is wrong - it means the invariant moved and
    /// somebody has to say whether it was allowed to. That is the entire point of writing it down.
    /// </summary>
    [TestClass]
    public class MlDigsiteBossMechanicWiringTests
    {
        private static string Manager() => PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");

        // ---- the driver is actually hung off the tick -----------------------------------------------------

        [TestMethod]
        public void The_Boss_Rush_branch_of_Drive_runs_the_mechanic_driver_and_is_no_longer_a_no_op()
        {
            var drive = PooledLootSourceText.MethodBody(Manager(),
                "private static void Drive(MlDigsiteEncounter encounter, DateTime now)");

            StringAssert.Contains(drive, "case MlDigsiteType.BossRush:");
            StringAssert.Contains(drive, "MlDigsiteBossMechanics.Drive(encounter, now);");
            StringAssert.Contains(drive, "DriveTetherHeal(encounter);");
        }

        [TestMethod]
        public void The_mechanic_set_is_rolled_once_when_the_encounter_opens_not_per_tick()
        {
            // Rolling inside the tick would re-roll the set under a fight in progress. It is rolled in
            // OpenEncounter, and MlDigsiteEncounter.TryAttachBossMechanics refuses a second attach on top.
            var src = Manager();

            var rolls = Regex.Matches(src, @"MlDigsiteBossMechanics\.Roll\(").Count;

            Assert.AreEqual(1, rolls, "the set must be rolled from exactly one place");

            var open = PooledLootSourceText.MethodBody(src,
                "private static void Drive(MlDigsiteEncounter encounter, DateTime now)");

            Assert.IsFalse(open.Contains("MlDigsiteBossMechanics.Roll("), "and that place is not the tick");
        }

        [TestMethod]
        public void The_boss_spawn_composes_its_overlay_from_the_set_that_was_ALREADY_rolled()
        {
            // THE ORDERING THIS WHOLE FIX RESTS ON, and it is invisible in play if it breaks: the boss would
            // simply take full damage through an "immune" phase that announced itself normally. The set is
            // attached by MlDigsiteBossMechanics.Roll BEFORE the boss is placed, and the spawner reads it to
            // compose the immune filter record onto the boss's monster effects - which cannot be attached
            // later, because ApplyMonsterEffectOverlay allocates a fresh effect-state array and would reset
            // every other effect mid-fight.
            var open = PooledLootSourceText.MethodBody(Manager(),
                "private static void OpenEncounter(MlDigsiteEncounter encounter, long forcedMechanicSetId = 0)");

            var roll = open.IndexOf("MlDigsiteBossMechanics.Roll(encounter, rng, forcedMechanicSetId)", StringComparison.Ordinal);
            var spawn = open.IndexOf("MlDigsiteSpawner.TrySpawn(encounter, MlDigsiteRole.Boss, rng)", StringComparison.Ordinal);

            Assert.IsTrue(roll >= 0, "the BossRush branch must roll the set");
            Assert.IsTrue(spawn > roll, "and it must do so BEFORE the boss is placed, or the spawner reads a null set");

            var spawner = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteSpawner.cs"),
                "public static Creature TrySpawn(MlDigsiteEncounter encounter, MlDigsiteRole role, Random rng, int waveNumber = 1)");

            StringAssert.Contains(spawner, "if (role == MlDigsiteRole.Boss)");
            StringAssert.Contains(spawner, "MlDigsiteBossMechanicRules.ComposeBossOverlay(");
            StringAssert.Contains(spawner, "encounter.BossMechanics?.Set");
            StringAssert.Contains(spawner, "creature.ApplyMonsterEffectOverlay(mechanic)");

            // and the record it appends is a kind the effect registry actually resolves to a handler - a
            // record the parser accepts and the registry does not implement would attach an inert slot and
            // filter nothing, which is the same broken fight in a different disguise
            Assert.IsTrue(MonsterEffectRegistry.TryGetHandler(MlDigsiteBossMechanicRules.ImmuneRecord, out var handler),
                $"'{MlDigsiteBossMechanicRules.ImmuneRecord}' must resolve to a shipped monster-effect handler");
            Assert.IsInstanceOfType(handler, typeof(ACE.Server.MonsterEffects.Effects.ImmuneEffect));
        }

        // ---- teardown ------------------------------------------------------------------------------------

        [TestMethod]
        public void The_driver_is_stopped_BEFORE_anything_is_taken_out_of_the_world()
        {
            // THE LEAK AND CRASH GUARD IN ONE. A pending step run after the destroy pass would fire at a
            // position whose objects are gone, or apply damage on behalf of a boss that no longer exists.
            var destroy = PooledLootSourceText.MethodBody(Manager(),
                "private static void DestroyHeld(MlDigsiteEncounter encounter)");

            var stop = destroy.IndexOf("MlDigsiteBossMechanics.Stop(encounter);", StringComparison.Ordinal);
            var snapshot = destroy.IndexOf("encounter.HeldSnapshot()", StringComparison.Ordinal);
            var close = destroy.IndexOf("encounter.CloseHeld();", StringComparison.Ordinal);

            Assert.IsTrue(stop >= 0, "DestroyHeld must stop the mechanic driver");
            Assert.IsTrue(snapshot > stop, "the driver stops before the held list is even read");
            Assert.IsTrue(close > stop);
        }

        [TestMethod]
        public void The_immunity_flag_is_cleared_with_the_encounter_back_reference()
        {
            // An immune boss left standing by a dead or dying edge case would be an immortal monster with
            // nothing left alive to switch it back off.
            var destroy = PooledLootSourceText.MethodBody(Manager(),
                "private static void DestroyHeld(MlDigsiteEncounter encounter)");

            StringAssert.Contains(destroy, "creature.P_DigsiteEncounter = null;");
            StringAssert.Contains(destroy, "creature.P_DigsiteImmune = false;");
        }

        [TestMethod]
        public void Stop_clears_the_step_queue_which_is_the_only_scheduler_the_mechanics_have()
        {
            var stop = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteBossMechanics.cs"),
                "public static void Stop(MlDigsiteEncounter encounter)");

            StringAssert.Contains(stop, "state.ClearSteps()");
        }

        [TestMethod]
        public void No_mechanic_module_schedules_anything_outside_the_step_queue()
        {
            // An ActionChain, a Timer or a delayed landblock action would outlive an encounter that has
            // already destroyed the objects the step names, and Stop would not reach it. Timers.Timer
            // additionally swallows exceptions thrown from its Elapsed handler.
            foreach (var module in new[]
            {
                "VolatileAddsMechanic", "DrumCadenceMechanic", "ImmunePhasesMechanic",
                "InterruptObjectMechanic", "SafeZonesMechanic",
            })
            {
                var src = PooledLootSourceText.Read($"Source/ACE.Server/MlDigsite/Mechanics/{module}.cs");

                Assert.IsFalse(Regex.IsMatch(src, @"new ActionChain\("), $"{module} may not use an ActionChain");
                Assert.IsFalse(Regex.IsMatch(src, @"new Timer\(|Timers\.Timer"), $"{module} may not use a timer");
                Assert.IsFalse(Regex.IsMatch(src, @"\.EnqueueAction\("), $"{module} reaches the world through the context, not directly");
            }
        }

        [TestMethod]
        public void Every_module_sends_its_dig_wide_lines_on_the_same_channel()
        {
            // THE RECONCILIATION PIN. Four modules passed WorldBroadcast and the drum cadence alone took
            // Say's Broadcast default, so the same class of line - a telegraph a player has seconds to react
            // to - arrived on two different channels depending on which mechanic sent it. Each choice was
            // defensible alone; the inconsistency was only visible with the five files side by side, and
            // nothing but this test would surface it again.
            foreach (var module in new[]
            {
                "VolatileAddsMechanic", "DrumCadenceMechanic", "ImmunePhasesMechanic",
                "InterruptObjectMechanic", "SafeZonesMechanic",
            })
            {
                AssertDigWideLinesShareOneChannel(PooledLootSourceText.Read($"Source/ACE.Server/MlDigsite/Mechanics/{module}.cs"), module);
            }
        }

        [TestMethod]
        public void The_channel_pin_catches_a_call_the_per_call_regex_cannot_even_see()
        {
            // WHY THE COUNT EXISTS. The per-call regex below balances parentheses to depth 2 only, so a call
            // whose arguments nest three deep - one interpolation holding a call holding a call, which is an
            // ordinary thing to write in a chat line - does not MATCH AT ALL, and a loop over matches passes
            // it in silence. A pin that quietly stops covering the line it was written for is worse than no
            // pin, so the count is what actually holds the invariant and the per-call check stays as the
            // thing that says WHICH line is wrong.
            const string fixture =
                "ctx.Say($\"{Name(Format(Trim(x)))} falls.\");\n"
                + "ctx.Say(\"and this one is fine\", ChatMessageType.WorldBroadcast);\n";

            var matched = Regex.Matches(fixture, @"(\.Say|MlDigsiteManager\.Announce)\((?:[^()]|\([^()]*\))*\)").Count;

            Assert.AreEqual(1, matched, "the depth-3 call is invisible to the per-call regex - that is the whole point");

            Assert.ThrowsExactly<AssertFailedException>(
                () => AssertDigWideLinesShareOneChannel(fixture, "fixture"),
                "the count-based invariant must catch the untagged call the per-call regex skipped");
        }

        /// <summary>
        /// Both halves of the one-channel invariant. The COUNT is the invariant: every dig-wide call site in
        /// the file has to carry a WorldBroadcast argument, and comparing the two counts cannot be defeated
        /// by nesting the way a per-call regex can. The per-call loop is kept because a bare count failure
        /// says only "one of them is wrong", and this is a pin somebody has to act on.
        ///
        /// Two shapes count as dig-wide: ctx.Say on the driver tick, and MlDigsiteManager.Announce from the
        /// two landblock-thread entry points that have no context to reach Say through. ctx.Tell is NOT one
        /// of them - it is one line to one player about their own margin, and it keeps Say's default.
        /// </summary>
        private static void AssertDigWideLinesShareOneChannel(string src, string label)
        {
            var calls = Regex.Matches(src, @"(?:\.Say|MlDigsiteManager\.Announce)\(").Count;
            var tagged = Regex.Matches(src, @"ChatMessageType\.WorldBroadcast").Count;

            Assert.AreEqual(calls, tagged,
                $"{label}: {calls} dig-wide call site(s) but {tagged} WorldBroadcast argument(s) - every dig-wide line goes out on that channel "
                + "(and nothing else in the file may name the token, which is what makes the count meaningful)");

            foreach (Match call in Regex.Matches(src, @"(\.Say|MlDigsiteManager\.Announce)\((?:[^()]|\([^()]*\))*\)"))
            {
                StringAssert.Contains(call.Value, "ChatMessageType.WorldBroadcast",
                    $"{label} sends a dig-wide line on a different channel from its peers");
            }
        }

        // ---- the non-lethal guarantee, at its call site ----------------------------------------------------

        [TestMethod]
        public void The_non_lethal_cap_is_applied_inside_the_queued_write_against_CURRENT_health()
        {
            // "Room for 2 mistakes on instant-death hits" is a guarantee BY CONSTRUCTION, and the
            // construction is this ordering. MlDigsiteBossMechanicRules.NonLethalDamage is unit-tested on its
            // own, but a pure function nobody calls guarantees nothing - and the cap has to be taken against
            // the health the player has AT THE MOMENT OF THE WRITE, not against what it was when the
            // telegraph went up, or a player who was hit by something else in between still dies to a miss
            // that was supposed to be forgiven.
            var hit = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteBossMechanics.cs"),
                "public void Hit(Player player, double damage, DamageType damageType, bool nonLethal = false)");

            var queue = hit.IndexOf("player.EnqueueAction(new ActionEventDelegate(", StringComparison.Ordinal);
            var guard = hit.IndexOf("if (nonLethal)", StringComparison.Ordinal);
            var cap = hit.IndexOf("MlDigsiteBossMechanicRules.NonLethalDamage(amount, player.Health.Current)", StringComparison.Ordinal);
            var take = hit.IndexOf("player.TakeDamage(", StringComparison.Ordinal);

            Assert.IsTrue(queue >= 0, "the vital write is queued onto the player's own landblock");
            Assert.IsTrue(guard > queue, "and the cap is decided INSIDE that queued action, not before it");
            Assert.IsTrue(cap > guard, "through the rule, against the player's CURRENT health");
            Assert.IsTrue(take > cap, "and only then is the damage applied");
        }

        [TestMethod]
        public void A_forgiven_safe_zone_miss_is_the_one_that_asks_for_the_cap()
        {
            // The other half of the same guarantee: the decision of WHICH misses are forgiven comes from the
            // rule, and the answer is handed to Hit as nonLethal. An inverted flag here, or a Hit call that
            // simply forgot the argument, would be a silent instant-death mechanic with no forgiveness at all
            // - the failure this feature's whole safe-zone design exists to prevent.
            var resolve = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/Mechanics/SafeZonesMechanic.cs"),
                "private static void Resolve(MlDigsiteMechanicContext ctx)");

            var decide = resolve.IndexOf("var lethal = MlDigsiteBossMechanicRules.MissIsLethal(before, forgiveness);", StringComparison.Ordinal);
            var apply = resolve.IndexOf("nonLethal: !lethal", StringComparison.Ordinal);

            Assert.IsTrue(decide >= 0, "the lethality decision must come from the rule, not from a local guess");
            Assert.IsTrue(apply > decide, "and be handed to the hit as its non-lethal flag");

            // the miss count the warning names comes from the same record the decision was made on
            StringAssert.Contains(resolve, "MlDigsiteBossMechanicRules.MissWarning(before + 1, forgiveness)");
        }

        // ---- every object the driver places is owned by the encounter --------------------------------------

        [TestMethod]
        public void No_mechanic_module_creates_or_enters_a_world_object_itself()
        {
            // Creature adds go through MlDigsiteSpawner and props through MlDigsiteProps, which are the two
            // places that stamp MlDigsiteEncounterId, set TimeToRot = -1 and adopt the object into the held
            // list. An object built anywhere else would be a permanent leak on a live, shared, persistent
            // island landblock.
            foreach (var module in new[]
            {
                "VolatileAddsMechanic", "DrumCadenceMechanic", "ImmunePhasesMechanic",
                "InterruptObjectMechanic", "SafeZonesMechanic",
            })
            {
                var src = PooledLootSourceText.Read($"Source/ACE.Server/MlDigsite/Mechanics/{module}.cs");

                Assert.IsFalse(src.Contains("WorldObjectFactory"), $"{module} may not create world objects directly");
                Assert.IsFalse(src.Contains("EnterWorld("), $"{module} may not put objects into the world directly");
            }
        }

        [TestMethod]
        public void A_prop_is_stamped_and_adopted_BEFORE_it_enters_the_world()
        {
            // The same fixed order MlDigsiteSpawner follows, for the same reason: no landblock save and no
            // client create packet may ever see one of these as an ordinary persistable object, and there
            // must be no instant at which a live prop stands in the world without the encounter knowing it
            // has to destroy it.
            var build = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteProps.cs"),
                "private static WorldObject Build(MlDigsiteEncounter encounter, uint wcid, Position position, uint scriptId)");

            var stamp = build.IndexOf("wo.SetProperty(PropertyInt.MlDigsiteEncounterId, (int)encounter.EncounterId);", StringComparison.Ordinal);
            var rot = build.IndexOf("wo.TimeToRot = -1;", StringComparison.Ordinal);
            var held = build.IndexOf("if (!encounter.AddHeld(wo))", StringComparison.Ordinal);
            var enter = build.IndexOf("wo.EnterWorld()", StringComparison.Ordinal);

            Assert.IsTrue(stamp >= 0, "the orphan filter's key and the persistence exclusion");
            Assert.IsTrue(rot > stamp, "the encounter owns this object's cleanup, not the decay pass");
            Assert.IsTrue(held > rot, "adopted before it exists in the world");
            Assert.IsTrue(enter > held, "and only then does it enter");
        }

        [TestMethod]
        public void A_prop_that_cannot_be_adopted_is_destroyed_rather_than_stranded()
        {
            var build = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteProps.cs"),
                "private static WorldObject Build(MlDigsiteEncounter encounter, uint wcid, Position position, uint scriptId)");

            // Every early return taken once the object EXISTS has to take it back out again, or a stamped
            // prop is left alive outside the world with nothing holding a reference to it. Measured over the
            // text after the create call, the only bail with nothing to destroy is the one that fires because
            // the create itself returned null.
            var created = build.IndexOf("WorldObjectFactory.CreateNewWorldObject(wcid);", StringComparison.Ordinal);

            Assert.IsTrue(created >= 0, "the create call must be in this method");

            var afterCreate = build.Substring(created);

            var returns = Regex.Matches(afterCreate, @"return null;").Count;
            var destroys = Regex.Matches(afterCreate, @"wo\.Destroy\(\);").Count;

            Assert.AreEqual(returns - 1, destroys,
                $"every failure path after creation must destroy the object ({destroys} destroys for {returns} null returns, "
                + "the odd one out being the bail for a create that returned nothing)");
        }

        [TestMethod]
        public void A_failed_prop_build_still_calls_its_caller_back()
        {
            // Placement is asynchronous, so by the time a build fails the caller's own TryPlace call has long
            // since returned true. A caller that opened something against the object - the interrupt window -
            // can only close it again if it hears about the failure.
            var place = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteProps.cs"),
                "public static bool TryPlace(MlDigsiteEncounter encounter, uint wcid, Position position, uint scriptId = 0,");

            StringAssert.Contains(place, "onPlaced?.Invoke(placed);");
            Assert.IsFalse(place.Contains("if (placed != null)"),
                "the callback must fire for a failed build too, not only for a successful one");
        }

        [TestMethod]
        public void An_interrupt_window_whose_object_never_appeared_is_cancelled_rather_than_landing()
        {
            // The commonest way to reach this is a code deploy that outran its content deploy, leaving wcid
            // 1005950 unapplied. Counting down to the hit then punishes a dig for not using an object that
            // was never in the world.
            var built = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/Mechanics/InterruptObjectMechanic.cs"),
                "private static void OnObjectBuilt(MlDigsiteEncounter encounter, MlDigsiteBossMechanicState state, uint wcid, WorldObject wo)");

            var success = built.IndexOf("state.SetInterruptObject(wo);", StringComparison.Ordinal);
            var cancel = built.IndexOf("if (!state.TryCloseInterrupt(out _))", StringComparison.Ordinal);

            Assert.IsTrue(success >= 0, "a successful build still records the object");
            Assert.IsTrue(cancel > success, "and a failed one closes the window through the same single latch");
        }

        [TestMethod]
        public void A_prop_build_refuses_a_Creature_so_adds_cannot_bypass_the_spawner()
        {
            var props = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteProps.cs");

            StringAssert.Contains(props, "if (wo is Creature)");
        }

        // ---- add deaths ------------------------------------------------------------------------------------

        [TestMethod]
        public void An_add_death_is_routed_to_the_driver_and_pays_nothing()
        {
            // Adds are spawned by a mechanic, not by the encounter's pacing: they are worth nothing to the
            // payout, never end a wave and never win the fight. NoteCreatureDeath reports them as their own
            // kind precisely so this branch can return before any of that bookkeeping.
            var hook = PooledLootSourceText.MethodBody(Manager(),
                "public static void OnEncounterCreatureDied(Creature creature)");

            var branch = hook.IndexOf("if (kind == MlDigsiteDeathKind.Add)", StringComparison.Ordinal);
            var route = hook.IndexOf("MlDigsiteBossMechanics.OnAddDied(encounter, creature);", StringComparison.Ordinal);
            var wave = hook.IndexOf("encounter.ScheduleNextWave(", StringComparison.Ordinal);

            Assert.IsTrue(branch >= 0, "the death hook must recognise a mechanic add");
            Assert.IsTrue(route > branch);
            Assert.IsTrue(wave > route, "and return before the wave pacing, which an add must never advance");
        }

        [TestMethod]
        public void An_add_death_never_empties_a_wave()
        {
            // liveAdds is a separate dictionary from the wave's own live set for exactly this reason: an add
            // dying must not set waveNowEmpty and trigger the next wave.
            var note = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteEncounter.cs"),
                "public MlDigsiteDeathKind NoteCreatureDeath(uint guid, DateTime nowUtc, Creature dead,");

            var addBranch = note.IndexOf("liveAdds.Remove(guid)", StringComparison.Ordinal);

            Assert.IsTrue(addBranch >= 0, "an add death is recognised by its own live set");

            var tail = note.Substring(addBranch);
            var nextBrace = tail.IndexOf('}');

            Assert.IsTrue(nextBrace > 0);
            Assert.IsFalse(tail.Substring(0, nextBrace).Contains("waveNowEmpty = true"),
                "the add branch must not report the wave as cleared");
        }

        // ---- the milestone-line suppression ----------------------------------------------------------------

        [TestMethod]
        public void The_boss_health_milestone_line_gives_way_to_an_immune_phase_at_the_same_percent()
        {
            var milestones = PooledLootSourceText.MethodBody(Manager(),
                "private static void DriveBossHealthMilestones(MlDigsiteEncounter encounter)");

            var suppress = milestones.IndexOf("if (ImmunePhaseOwnsMilestone(encounter, pct))", StringComparison.Ordinal);
            var announce = milestones.IndexOf("Announce(encounter, $\"The boss is at {pct}% health!\");", StringComparison.Ordinal);

            Assert.IsTrue(suppress >= 0, "the suppression must exist");
            Assert.IsTrue(announce > suppress, "and it must come before the line it suppresses");

            // the milestone is still consumed, so it is suppressed once rather than resurfacing later
            StringAssert.Contains(milestones, "encounter.DueBossHealthMilestones(current, max)");
            StringAssert.Contains(milestones, "continue;");
        }

        [TestMethod]
        public void The_suppression_is_inert_for_every_encounter_that_is_not_a_Boss_Rush()
        {
            // BossMechanics is null for the Waves and Corruption shapes, which is the only path they can take
            // through this - no type check needed and none written.
            var owns = PooledLootSourceText.MethodBody(Manager(),
                "private static bool ImmunePhaseOwnsMilestone(MlDigsiteEncounter encounter, int milestonePercent)");

            StringAssert.Contains(owns, "var state = encounter.BossMechanics;");
            StringAssert.Contains(owns, "if (state == null)");
            StringAssert.Contains(owns, "return false;");
            StringAssert.Contains(owns, "MlDigsiteBossMechanicRules.ImmuneCoversMilestone(");
        }

        // ---- the leash heal ---------------------------------------------------------------------------------

        [TestMethod]
        public void The_leash_heal_is_scoped_to_Boss_Rush_and_to_its_own_tunable()
        {
            // Owner ruling 2026-09-20: the heal applies only to Boss Rush bosses, never to all digsite
            // creatures - and emphatically not to every monster in the world, which is what hooking
            // Monster.Sleep would have done.
            var heal = PooledLootSourceText.MethodBody(Manager(),
                "private static void DriveTetherHeal(MlDigsiteEncounter encounter)");

            StringAssert.Contains(heal, "encounter.Type != MlDigsiteType.BossRush");
            StringAssert.Contains(heal, "MlDigsiteTunables.TetherHealEnabled");
            StringAssert.Contains(heal, "boss.MonsterState == Creature.State.Return");
        }

        [TestMethod]
        public void The_leash_heal_writes_the_vital_on_the_bosss_own_landblock_and_refreshes_the_bar()
        {
            var heal = PooledLootSourceText.MethodBody(Manager(),
                "private static void DriveTetherHeal(MlDigsiteEncounter encounter)");

            var queue = heal.IndexOf("landblock.EnqueueAction(new ActionEventDelegate(", StringComparison.Ordinal);
            var write = heal.IndexOf("target.Health.Current = target.Health.MaxValue;", StringComparison.Ordinal);
            var refresh = heal.IndexOf("target.OnHealthUpdate();", StringComparison.Ordinal);

            Assert.IsTrue(queue >= 0, "a vital WRITE from the world thread goes through the landblock queue");
            Assert.IsTrue(write > queue, "and inside it, not before it");
            Assert.IsTrue(refresh > write, "without the push the client keeps showing the bar it last saw");
        }

        [TestMethod]
        public void A_boss_with_no_landblock_does_not_burn_the_one_heal_its_return_trip_gets()
        {
            // The latch is claimed only when there is somewhere to queue the write. Claiming first and then
            // finding no landblock would silently consume the heal for that whole return trip.
            var heal = PooledLootSourceText.MethodBody(Manager(),
                "private static void DriveTetherHeal(MlDigsiteEncounter encounter)");

            var guard = heal.IndexOf("if (returning && landblock == null)", StringComparison.Ordinal);
            var claim = heal.IndexOf("if (!encounter.TryClaimTetherHeal(returning))", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0, "the no-landblock guard must exist");
            Assert.IsTrue(claim > guard, "and it must come before the latch is claimed");
        }

        [TestMethod]
        public void The_leash_heal_is_never_silent()
        {
            // A boss that heals with no line reads as a bug to a player who merely died and is corpse-running
            // back to the pit.
            var heal = PooledLootSourceText.MethodBody(Manager(),
                "private static void DriveTetherHeal(MlDigsiteEncounter encounter)");

            StringAssert.Contains(heal, "Announce(encounter,");
        }

        // ---- the interrupt object's use hook -----------------------------------------------------------------

        [TestMethod]
        public void The_interrupt_drum_opts_in_from_GenericObject_ActOnUse()
        {
            var act = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/GenericObject.cs"),
                "public override void ActOnUse(WorldObject activator)");

            StringAssert.Contains(act, "MlDigsite.MlDigsiteInterruptObject.TryHandleUse(this, player)");
        }

        [TestMethod]
        public void The_use_handler_bails_on_the_encounter_stamp_before_anything_else()
        {
            // Every other Generic object in the game runs through this on every double-click, so the cost for
            // a non-digsite object has to be one property miss and nothing more.
            var handler = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteInterruptObject.cs"),
                "public static bool TryHandleUse(WorldObject wo, Player player)");

            var stamp = handler.IndexOf("PropertyInt.MlDigsiteEncounterId", StringComparison.Ordinal);
            var live = handler.IndexOf("MlDigsiteManager.FindLiveEncounter(", StringComparison.Ordinal);

            Assert.IsTrue(stamp >= 0, "the opt-in is the digsite stamp");
            Assert.IsTrue(live > stamp, "and the encounter lookup only happens for an object that carries it");
        }
    }
}
