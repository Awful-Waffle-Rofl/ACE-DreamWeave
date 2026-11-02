using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Pvp;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Open World Threads WP3b, R10-R14: the creature half of the perimeter. The binding (pure), its stamping before
    /// EnterWorld (driven on a seeded Creature), the target filter and R12's damage refusal (driven through the real
    /// Player.CheckPKStatusVsTarget on seeded objects, the AttackDefendGateWiringTests technique), and the monster-tick
    /// hooks that need a live world (pinned against source, each pin proven to discriminate).
    /// </summary>
    [TestClass]
    public class OutdoorCreaturePerimeterTests
    {
        private const ushort Primary = 0x5456;
        private const ushort East = 0x5556;
        private const double R = 60.0;

        private static OutdoorPerimeterSettings Def() => new OutdoorPerimeterSettings(
            OutdoorSiteTunables.DefaultWarningBandM, OutdoorSiteTunables.DefaultBackstopM, OutdoorSiteTunables.DefaultUnravelEnabled,
            OutdoorSiteTunables.DefaultUnravelStartPct, OutdoorSiteTunables.DefaultUnravelStepPct, OutdoorSiteTunables.DefaultUnravelLethal,
            OutdoorSiteTunables.DefaultUnravelGraceS, OutdoorSiteTunables.DefaultBackstopEnabled,
            OutdoorSiteTunables.DefaultContainmentMarginM, OutdoorSiteTunables.DefaultInteriorAwarenessM);

        /// <summary>Centre at (150, 96) on the primary, radius 60; the drop at (100, 96), 50 m west of the centre.</summary>
        private static DungeonEntryDef Site(string entry = "0x54560001 100 96 10") => new DungeonEntryDef
        {
            Id = "ow_test", Kind = DungeonEntryDef.KindOutdoor, Landblock = Primary, ExitPortalWcid = 1003604, Entry = entry,
            Outdoor = new OutdoorSiteDef
            {
                Primary = Primary, Footprint = new List<ushort> { Primary, East },
                Circle = new OutdoorCircleDef { Landblock = Primary, X = 150, Y = 96, Radius = R },
            },
        };

        private static OutdoorRunPerimeter RunPerimeter(double mob = 1.0) => OutdoorRunPerimeter.For(Site(), Def(), mob);

        private static DungeonSpawnPointDef Point(float x, float y, bool interior = false) => new DungeonSpawnPointDef
        {
            Cell = ((uint)Primary << 16) | (interior ? 0x105u : 0x0001u), X = x, Y = y, Curated = true,
            Kind = interior ? OutdoorPopulation.KindInterior : OutdoorPopulation.KindOutdoor,
        };

        /// <summary>A position <paramref name="metresEast"/> of the centre, on the primary or across the seam.</summary>
        private static Position East_Of_Centre(double metresEast)
        {
            var gx = 150.0 + metresEast;
            return gx < 192.0
                ? new Position(((uint)Primary << 16) | 0x0001u, (float)gx, 96f, 10f, 0f, 0f, 0f, 1f, 0u)
                : new Position(((uint)East << 16) | 0x0001u, (float)(gx - 192.0), 96f, 10f, 0f, 0f, 0f, 1f, 0u);
        }

        // ---- the binding, pure ----

        [TestMethod]
        public void Distances_are_horizontal_and_cross_landblock_seams()
        {
            var binding = RunPerimeter().Bind(Point(150, 96));
            Assert.AreEqual(47.0, binding.DistanceOf(East_Of_Centre(47)), 1e-3, "x = 5 on the east member is 47 m from the centre");
            Assert.AreEqual(10.0, binding.DistanceOf(new Position(((uint)Primary << 16) | 1u, 160f, 96f, 500f, 0f, 0f, 0f, 1f, 0u)), 1e-3, "z is ignored");
            Assert.AreEqual(double.PositiveInfinity, binding.DistanceOf(null), "no position reads as outside");
        }

        [TestMethod]
        public void The_binding_applies_the_rules_at_their_edges()
        {
            var binding = RunPerimeter().Bind(Point(150, 96));

            Assert.IsFalse(binding.ShouldReturnHome(East_Of_Centre(58)), "R - margin exactly: stays");
            Assert.IsTrue(binding.ShouldReturnHome(East_Of_Centre(58.01)), "past R - margin: home");

            Assert.IsTrue(binding.IsValidTarget(true, East_Of_Centre(60)), "a player at exactly R");
            Assert.IsFalse(binding.IsValidTarget(true, East_Of_Centre(60.01)), "a player past R");
            Assert.IsTrue(binding.IsValidTarget(false, East_Of_Centre(500)), "pets and creatures always");

            Assert.IsFalse(binding.ShouldRefuseDamage(true, East_Of_Centre(60)));
            Assert.IsTrue(binding.ShouldRefuseDamage(true, East_Of_Centre(60.01)));
            Assert.IsFalse(binding.ShouldRefuseDamage(false, East_Of_Centre(500)), "a non-player attacker is never refused");
        }

        [TestMethod]
        public void The_refusal_line_is_rate_limited_to_one_per_5_s()
        {
            Assert.IsTrue(OutdoorCreaturePerimeter.RefusedStrikeNoticeDue(100, double.NegativeInfinity), "the first refusal");
            Assert.IsFalse(OutdoorCreaturePerimeter.RefusedStrikeNoticeDue(104.9, 100));
            Assert.IsTrue(OutdoorCreaturePerimeter.RefusedStrikeNoticeDue(105, 100));
            Assert.AreEqual("Your strike unravels before it lands; the Thread holds only those within it.", OutdoorCreaturePerimeter.RefusedStrikeText);
        }

        [TestMethod]
        public void The_run_binding_exists_only_for_a_usable_outdoor_row()
        {
            Assert.IsNotNull(RunPerimeter());
            Assert.IsNull(OutdoorRunPerimeter.For(new DungeonEntryDef { Id = "d", Landblock = 0x0150 }, Def(), 1.0), "a dungeon row");
            Assert.IsNull(OutdoorRunPerimeter.For(Site("garbage"), Def(), 1.0), "no parseable entry");

            var noCircle = Site();
            noCircle.Outdoor.Circle = null;
            Assert.IsNull(OutdoorRunPerimeter.For(noCircle, Def(), 1.0), "no circle");
        }

        /// <summary>
        /// A booted server pre-seeds both keys (PropertyManager.Initialize -> LoadDefaultProperties), so this guards an ADMIN
        /// writing a bad value with /modifydouble, not an unset key.
        /// </summary>
        [TestMethod]
        public void An_admin_written_nan_zero_or_negative_tunable_falls_back_to_its_default()
        {
            var run = OutdoorRunPerimeter.For(Site(), Def() with { ContainmentMarginM = double.NaN, InteriorAwarenessM = 0 }, 1.0);
            Assert.AreEqual(OutdoorSiteTunables.DefaultContainmentMarginM, run.Settings.ContainmentMarginM);
            Assert.AreEqual(OutdoorSiteTunables.DefaultInteriorAwarenessM, run.Settings.InteriorAwarenessM);

            var negative = OutdoorRunPerimeter.For(Site(), Def() with { ContainmentMarginM = -1, InteriorAwarenessM = 20 }, 1.0);
            Assert.AreEqual(OutdoorSiteTunables.DefaultContainmentMarginM, negative.Settings.ContainmentMarginM);
            Assert.AreEqual(20.0, negative.Settings.InteriorAwarenessM, "a valid value is kept");
        }

        [TestMethod]
        public void The_awareness_clamp_works_on_the_effective_range_and_never_raises()
        {
            var s = Def();

            Assert.AreEqual(12.0, OutdoorRunPerimeter.ClampedAwarenessProperty(30, 18, 1.0, true, 100, s).Value, 1e-4, "interior: 12 m");
            Assert.AreEqual(6.0, OutdoorRunPerimeter.ClampedAwarenessProperty(30, 18, 2.0, true, 100, s).Value, 1e-4, "x2 multiplier: property 6 is 12 m effective");
            Assert.AreEqual(15.0, OutdoorRunPerimeter.ClampedAwarenessProperty(50, 18, 1.0, false, 20, s).Value, 1e-4, "R14: max(10, 20 - 5)");
            Assert.AreEqual(10.0, OutdoorRunPerimeter.ClampedAwarenessProperty(null, 18, 1.0, false, 8, s).Value, 1e-4, "an unauthored range clamps from the default; floor 10");

            Assert.IsNull(OutdoorRunPerimeter.ClampedAwarenessProperty(5, 18, 1.0, true, 100, s), "already under the cap: never raised");
            Assert.IsNull(OutdoorRunPerimeter.ClampedAwarenessProperty(30, 18, 0.0, true, 100, s), "no usable multiplier");
            Assert.IsNull(OutdoorRunPerimeter.ClampedAwarenessProperty(30, 18, double.NaN, true, 100, s));
        }

        // ---- the stamping, driven ----

        private static T Seeded<T>(PlayerKillerStatus status, Position at) where T : Creature
        {
            var wo = OutdoorContentFilterTests.Seeded<T>();
            wo.PlayerKillerStatus = status;
            wo.Location = at;
            return wo;
        }

        [TestMethod]
        public void BindToOutdoorPerimeter_sets_home_radius_2R_awareness_and_never_a_tether()
        {
            var creature = Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(0));
            creature.VisualAwarenessRange = 30;
            creature.AuralAwarenessRange = 40;

            // An interior point 50 m from the drop: interior cap 12 m.
            ThreadDungeonSpawner.BindToOutdoorPerimeter(creature, RunPerimeter(), Point(150, 96, interior: true));

            Assert.IsNotNull(creature.P_OutdoorPerimeter);
            Assert.IsTrue(creature.P_OutdoorPerimeter.Interior);
            Assert.AreEqual(2 * R, creature.HomeRadius.Value, 1e-6, "R10: HomeRadius = 2R");
            Assert.IsNull(creature.TetherRadius, "R10: TetherRadius is never set");
            Assert.AreEqual(12.0, creature.VisualAwarenessRange.Value, 1e-4, "R13");
            Assert.AreEqual(12.0, creature.AuralAwarenessRange.Value, 1e-4, "aural is held to the same cap");

            var outdoor = Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(0));
            outdoor.VisualAwarenessRange = 30;
            ThreadDungeonSpawner.BindToOutdoorPerimeter(outdoor, RunPerimeter(), Point(150, 96));
            Assert.IsFalse(outdoor.P_OutdoorPerimeter.Interior);
            Assert.AreEqual(30.0, outdoor.VisualAwarenessRange.Value, 1e-4, "50 m from the drop: the 45 m drop cap does not bite");
            Assert.IsNull(outdoor.AuralAwarenessRange, "an unauthored aural range is left alone");
        }

        [TestMethod]
        public void The_target_filter_drops_players_past_R_and_keeps_creatures()
        {
            var monster = Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(0));
            monster.P_OutdoorPerimeter = RunPerimeter().Bind(Point(150, 96));

            var inside = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(60));
            var outside = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(61));
            var farPet = Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(80));

            var filtered = monster.FilterOutdoorPerimeterTargets(new List<Creature> { inside, outside, farPet });
            CollectionAssert.AreEqual(new List<Creature> { inside, farPet }, filtered);

            monster.AttackTarget = outside;
            CollectionAssert.DoesNotContain(monster.FilterOutdoorPerimeterTargets(new List<Creature> { outside }), outside, "R11b applies to a held target too");
        }

        [TestMethod]
        public void An_unbound_creature_keeps_its_target_list_untouched()
        {
            var monster = Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(0));
            var list = new List<Creature> { Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(500)) };

            Assert.AreSame(list, monster.FilterOutdoorPerimeterTargets(list), "same reference, no allocation");
        }

        // ---- F1: line of sight gates acquisition only ----

        [TestMethod]
        public void An_interior_creature_never_drops_its_current_target_for_line_of_sight()
        {
            var interior = RunPerimeter().Bind(Point(150, 96, interior: true));
            var current = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(10));
            var newcomer = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(20));
            var visibleNewcomer = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(25));
            var heldButOutside = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(61));

            var visibilityAsked = new List<Creature>();
            bool IsVisible(Creature c) { visibilityAsked.Add(c); return ReferenceEquals(c, visibleNewcomer); }
            bool IsHeld(Creature c) => ReferenceEquals(c, current) || ReferenceEquals(c, heldButOutside);

            var filtered = Creature.FilterCandidates(interior, new List<Creature> { current, newcomer, visibleNewcomer, heldButOutside }, IsHeld, IsVisible);

            CollectionAssert.AreEqual(new List<Creature> { current, visibleNewcomer }, filtered,
                "the held attack target stays without line of sight; a new one needs it; a held player past R still goes");
            CollectionAssert.DoesNotContain(visibilityAsked, current, "a held target is never ray-cast");
            CollectionAssert.Contains(visibilityAsked, newcomer, "a new candidate is");

            // Positive control: an OUTDOOR creature never asks for line of sight at all.
            visibilityAsked.Clear();
            var outdoor = RunPerimeter().Bind(Point(150, 96));
            Assert.AreEqual(3, Creature.FilterCandidates(outdoor, new List<Creature> { current, newcomer, visibleNewcomer }, IsHeld, IsVisible).Count);
            Assert.AreEqual(0, visibilityAsked.Count);
        }

        [TestMethod]
        public void An_interior_creature_keeps_its_out_of_sight_attack_target_but_not_its_taunter()
        {
            // The live instance filter FindNextTarget calls. These seeded objects have no PhysicsObj, so IsDirectVisible is
            // false for every one of them: everything is out of sight.
            var monster = Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(0));
            monster.P_OutdoorPerimeter = RunPerimeter().Bind(Point(150, 96, interior: true));

            var attackTarget = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(10));
            var taunter = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(12));
            var newcomer = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(20));
            Assert.IsFalse(monster.IsDirectVisible(attackTarget), "precondition: the attack target is out of sight");

            monster.AttackTarget = attackTarget;
            monster.TauntTarget = taunter;

            var filtered = monster.FilterOutdoorPerimeterTargets(new List<Creature> { attackTarget, taunter, newcomer });
            CollectionAssert.AreEqual(new List<Creature> { attackTarget }, filtered,
                "the attack target stays; the out-of-sight taunter and the new candidate go");
        }

        // ---- F2: the alert path's line-of-sight throttle ----

        [TestMethod]
        public void The_alert_path_ray_casts_at_most_once_per_half_second()
        {
            var interior = RunPerimeter().Bind(Point(150, 96, interior: true));
            var near = East_Of_Centre(10);
            var casts = 0;
            var last = double.NegativeInfinity;

            var answers = new List<bool>();
            foreach (var now in new[] { 100.0, 100.1, 100.4, 100.49, 100.5, 100.9, 101.0, 101.2 })
                answers.Add(interior.AcceptsAlert(true, near, now, ref last, () => { casts++; return false; }));

            Assert.AreEqual(3, casts, "checks at 100.0, 100.5 and 101.0 only");
            Assert.IsTrue(answers.TrueForAll(a => !a), "a refusal stands between checks");

            // A visible target is accepted on the check that sees it.
            var seenLast = double.NegativeInfinity;
            Assert.IsTrue(interior.AcceptsAlert(true, near, 200.0, ref seenLast, () => true));

            // Positive controls: the past-R refusal never casts, and an outdoor creature never casts.
            var outsideCasts = 0;
            var outsideLast = double.NegativeInfinity;
            Assert.IsFalse(interior.AcceptsAlert(true, East_Of_Centre(61), 300.0, ref outsideLast, () => { outsideCasts++; return true; }));
            var outdoor = RunPerimeter().Bind(Point(150, 96));
            Assert.IsTrue(outdoor.AcceptsAlert(true, near, 300.0, ref outsideLast, () => { outsideCasts++; return false; }));
            Assert.AreEqual(0, outsideCasts);
        }

        // ---- F3: R12 at every damage intake ----

        private static Pet SeededPet(Player owner, Position at)
        {
            var pet = Seeded<CombatPet>(PlayerKillerStatus.NPK, at);
            pet.P_PetOwner = owner;
            return pet;
        }

        [TestMethod]
        public void A_direct_hit_or_proc_from_a_player_past_R_is_refused_and_from_inside_is_not()
        {
            // Direct melee/missile hits, acid ticks and poison procs all pass the player as TakeDamage's source.
            var monster = BoundMonster();
            Assert.IsTrue(monster.RefusesOutdoorPerimeterDamage(Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(61))), "past R");
            Assert.IsFalse(monster.RefusesOutdoorPerimeterDamage(Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(60))), "control: exactly R");
            Assert.IsFalse(monster.RefusesOutdoorPerimeterDamage(Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(500))), "a creature source is never refused");
            Assert.IsFalse(monster.RefusesOutdoorPerimeterDamage(null), "the combined DoT tick carries no source");
            Assert.IsFalse(Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(0)).RefusesOutdoorPerimeterDamage(Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(500))),
                "an unbound creature takes everything");
        }

        [TestMethod]
        public void A_pet_is_judged_by_its_own_position()
        {
            var monster = BoundMonster();
            var ownerOutside = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(80));
            var ownerInside = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(10));

            Assert.IsTrue(monster.RefusesOutdoorPerimeterDamage(SeededPet(ownerInside, East_Of_Centre(61))), "a pet past R deals nothing, even with its owner inside");
            Assert.IsFalse(monster.RefusesOutdoorPerimeterDamage(SeededPet(ownerOutside, East_Of_Centre(30))), "control: a pet inside fights for an owner outside");

            var unowned = Seeded<CombatPet>(PlayerKillerStatus.NPK, East_Of_Centre(500));
            Assert.IsFalse(monster.RefusesOutdoorPerimeterDamage(unowned), "a pet with no player owner is a creature source");
        }

        [TestMethod]
        public void A_dot_tick_is_judged_by_the_casters_position_at_the_tick()
        {
            // The DoT loop asks per caster, with the caster as it stands now: the same gate, re-asked each tick.
            var monster = BoundMonster();
            var caster = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(10));
            Assert.IsFalse(monster.RefusesOutdoorPerimeterDamage(caster), "control: the caster is inside at the first tick");

            caster.Location = East_Of_Centre(70);
            Assert.IsTrue(monster.RefusesOutdoorPerimeterDamage(caster), "the caster has walked out: the next tick is refused");
        }

        [TestMethod]
        public void A_harmful_spell_cast_by_a_pet_past_R_is_refused_and_from_inside_is_not()
        {
            // The non-projectile cast route (WorldObject.HandleCastSpell): enchantments, boosts, transfers, dispels.
            var monster = BoundMonster();
            var ownerInside = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(10));
            var ownerOutside = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(80));

            Assert.IsTrue(Creature.RefusesOutdoorPerimeterSpell(monster, SeededPet(ownerInside, East_Of_Centre(61)), harmful: true),
                "a pet past R casts nothing harmful on a run creature, even with its owner inside");
            Assert.IsFalse(Creature.RefusesOutdoorPerimeterSpell(monster, SeededPet(ownerOutside, East_Of_Centre(30)), harmful: true),
                "control: a pet inside R casts for an owner outside");
            Assert.IsFalse(Creature.RefusesOutdoorPerimeterSpell(monster, SeededPet(ownerInside, East_Of_Centre(61)), harmful: false),
                "a beneficial spell is never refused");
            Assert.IsFalse(Creature.RefusesOutdoorPerimeterSpell(Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(0)), SeededPet(ownerInside, East_Of_Centre(500)), harmful: true),
                "an unbound creature takes every spell");
            Assert.IsFalse(Creature.RefusesOutdoorPerimeterSpell(null, SeededPet(ownerInside, East_Of_Centre(500)), harmful: true),
                "an item target (no creature) is never refused");
        }

        internal static string SpellCastWiringFault(string worldObjectMagic)
        {
            var body = PooledLootSourceText.MethodBody(worldObjectMagic,
                "protected bool HandleCastSpell(Spell spell, WorldObject target, WorldObject itemCaster = null, WorldObject weapon = null, bool isWeaponSpell = false, bool fromProc = false, bool equip = false, bool keepExplicitTarget = false)");
            var gate = body.IndexOf("if (perimeterTarget?.P_OutdoorPerimeter != null && Creature.RefusesOutdoorPerimeterSpell(perimeterTarget, this, spell.IsHarmful))\n                return false;", StringComparison.Ordinal);
            var dispatch = body.IndexOf("switch (spell.MetaSpellType)", StringComparison.Ordinal);
            if (gate < 0 || dispatch < 0 || gate > dispatch) return "HandleCastSpell dispatches a spell before the perimeter gate (or the gate is not null-guarded first)";
            return null;
        }

        [TestMethod]
        public void Every_cast_asks_the_perimeter_gate()
        {
            var wom = Src("WorldObject_Magic.cs");
            Assert.IsNull(SpellCastWiringFault(wom));
            Assert.IsNotNull(SpellCastWiringFault(wom.Replace("this, spell.IsHarmful))", "this, false))")));
            Assert.IsNotNull(SpellCastWiringFault(wom.Replace("if (perimeterTarget?.P_OutdoorPerimeter != null && Creature.RefusesOutdoorPerimeterSpell(", "if (Creature.RefusesOutdoorPerimeterSpell(")),
                "the open-world null check must come first");
        }

        // ---- a projectile refused at impact lands nothing ----

        internal static string ProjectileImpactGateFault(string projectile)
        {
            var body = PooledLootSourceText.MethodBody(projectile, "public override void OnCollideObject(WorldObject target)");
            var canDamage = body.IndexOf("if (sourceCreature != null && !sourceCreature.CanDamage(creatureTarget))\n                return;", StringComparison.Ordinal);
            var gate = body.IndexOf("if (creatureTarget.P_OutdoorPerimeter != null && creatureTarget.RefusesOutdoorPerimeterDamage(ProjectileSource))\n                return;", StringComparison.Ordinal);
            if (canDamage < 0 || gate < 0 || gate < canDamage) return "OnCollideObject has no perimeter gate after the CanDamage return";

            // Everything a landed projectile does must come after the gate.
            foreach (var effect in new[] { "CheckPKStatusVsTarget(", "CalculateDamage(", "CreateEnchantment(", "ApplySpellHitMonsterEffects(", "DamageTarget(",
                "ApplySpellHitClassAbilities(", "ApplyWeaponModSpellHit(", "OnAttackMonster(", "DoSpellEffects(" })
            {
                var at = body.IndexOf(effect, StringComparison.Ordinal);
                if (at >= 0 && at < gate) return $"{effect} runs before the perimeter gate";
            }

            return null;
        }

        [TestMethod]
        public void A_projectile_refused_at_impact_lands_nothing()
        {
            var sp = Src("SpellProjectile.cs");
            Assert.IsNull(ProjectileImpactGateFault(sp));
            Assert.IsNotNull(ProjectileImpactGateFault(sp.Replace("if (creatureTarget.P_OutdoorPerimeter != null && creatureTarget.RefusesOutdoorPerimeterDamage(ProjectileSource))", "if (false)")));
            StringAssert.Contains(PooledLootSourceText.MethodBody(sp, "public override void OnCollideObject(WorldObject target)"), "OnAttackMonster(",
                "positive control: the OnAttackMonster side effect the gate must precede is still in this method");
        }

        // ---- a DoT tick whose every contribution is refused calls nothing ----

        [TestMethod]
        public void A_fully_refused_tick_on_a_run_creature_is_dropped_and_nothing_else_is()
        {
            Assert.IsTrue(EnchantmentManager.OutdoorTickFullyRefused(hasPerimeter: true, refused: 2, contributed: 0), "every contribution refused");
            Assert.IsFalse(EnchantmentManager.OutdoorTickFullyRefused(hasPerimeter: true, refused: 1, contributed: 1), "control: one caster inside still lands");
            Assert.IsFalse(EnchantmentManager.OutdoorTickFullyRefused(hasPerimeter: true, refused: 0, contributed: 0), "nothing refused: the existing behaviour");
            Assert.IsFalse(EnchantmentManager.OutdoorTickFullyRefused(hasPerimeter: false, refused: 2, contributed: 0), "an unbound target (PvP paths) is unchanged");
        }

        internal static string DotEarlyReturnFault(string enchantments)
        {
            var tick = PooledLootSourceText.MethodBody(enchantments, "public void ApplyDamageTick(List<PropertiesEnchantmentRegistry> enchantments, DamageType damageType, bool aetheria = false)");
            var count = tick.IndexOf("RefusesOutdoorPerimeterDamage(damager))\n                {\n                    outdoorRefused++;\n                    continue;", StringComparison.Ordinal);
            var early = tick.IndexOf("if (OutdoorTickFullyRefused(creature.P_OutdoorPerimeter != null, outdoorRefused, contributions.Count))\n                return;", StringComparison.Ordinal);
            var take = tick.IndexOf("creature.TakeDamageOverTime(", StringComparison.Ordinal);
            var history = tick.IndexOf("creature.DamageHistory.Add(", StringComparison.Ordinal);
            if (count < 0) return "the DoT loop does not count a refused contribution";
            if (early < 0 || take < 0 || history < 0 || early > take || early > history) return "a fully refused tick still reaches TakeDamageOverTime";
            return null;
        }

        [TestMethod]
        public void The_dot_tick_returns_before_TakeDamageOverTime_when_fully_refused()
        {
            var em = Src("Managers/EnchantmentManager.cs");
            Assert.IsNull(DotEarlyReturnFault(em));
            Assert.IsNotNull(DotEarlyReturnFault(em.Replace("outdoorRefused++;", ";")));
            Assert.IsNotNull(DotEarlyReturnFault(em.Replace("contributions.Count))\n                return;", "contributions.Count))\n                { }")));
        }

        internal static string IntakeWiringFault(string monsterCombat, string enchantments, string projectile)
        {
            var take = PooledLootSourceText.MethodBody(monsterCombat,
                "public virtual uint TakeDamage(WorldObject source, DamageType damageType, float amount, bool crit = false, IncomingDamageOrigin origin = IncomingDamageOrigin.Secondary)");
            var gate = take.IndexOf("if (P_OutdoorPerimeter != null && amount > 0 && RefusesOutdoorPerimeterDamage(source))", StringComparison.Ordinal);
            var write = take.IndexOf("UpdateVitalDelta(Health", StringComparison.Ordinal);
            if (gate < 0 || write < 0 || gate > write) return "TakeDamage does not refuse before the vital write";

            var tick = PooledLootSourceText.MethodBody(enchantments, "public void ApplyDamageTick(List<PropertiesEnchantmentRegistry> enchantments, DamageType damageType, bool aetheria = false)");
            var dotGate = tick.IndexOf("if (creature.P_OutdoorPerimeter != null && creature.RefusesOutdoorPerimeterDamage(damager))\n                {\n                    outdoorRefused++;\n                    continue;", StringComparison.Ordinal);
            var contribute = tick.IndexOf("contributions.Add(", StringComparison.Ordinal);
            if (dotGate < 0 || contribute < 0 || dotGate > contribute) return "the DoT loop does not drop a refused caster's contribution";

            var damage = PooledLootSourceText.MethodBody(projectile, "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)");
            var projGate = damage.IndexOf("if (target.P_OutdoorPerimeter != null && target.RefusesOutdoorPerimeterDamage(ProjectileSource))", StringComparison.Ordinal);
            var projWrite = damage.IndexOf("UpdateVitalDelta(", StringComparison.Ordinal);
            if (projGate < 0 || projWrite < 0 || projGate > projWrite) return "a spell projectile writes vitals before the perimeter gate";

            return null;
        }

        private static string Src(string relative) => PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/" + relative);

        [TestMethod]
        public void Every_damage_intake_asks_the_perimeter_gate()
        {
            var mc = Src("Monster_Combat.cs");
            var em = Src("Managers/EnchantmentManager.cs");
            var sp = Src("SpellProjectile.cs");

            Assert.IsNull(IntakeWiringFault(mc, em, sp));
            Assert.IsNotNull(IntakeWiringFault(mc.Replace("amount > 0 && RefusesOutdoorPerimeterDamage(source))", "amount > 0 && false)"), em, sp));
            Assert.IsNotNull(IntakeWiringFault(mc, em.Replace("creature.RefusesOutdoorPerimeterDamage(damager))", "false)"), sp));
            Assert.IsNotNull(IntakeWiringFault(mc, em, sp.Replace("target.RefusesOutdoorPerimeterDamage(ProjectileSource))", "false)")));
        }

        [TestMethod]
        public void The_acid_and_poison_procs_land_through_TakeDamage_with_the_player_as_source()
        {
            // The routes the review named: both reach the TakeDamage gate with the attacker as source, so the pin above covers them.
            StringAssert.Contains(Src("Player_ClassAbilityBuffs.cs"), "target.TakeDamage(this, DamageType.Base, dot.TickAmount, false, IncomingDamageOrigin.Secondary);");
            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/PoisonWeaponAbility.cs"),
                "target.TakeDamage(attacker, DamageType.Base, poison, false, IncomingDamageOrigin.Secondary);");
        }

        // ---- R12, driven through the real gate ----

        /// <summary>A Spell with only the flags field set (new Spell(id) reads the portal dat, which tests never load).</summary>
        private static Spell FakeSpell(bool beneficial)
        {
            var spellBase = (ACE.DatLoader.Entity.SpellBase)RuntimeHelpers.GetUninitializedObject(typeof(ACE.DatLoader.Entity.SpellBase));
            var bitfield = typeof(ACE.DatLoader.Entity.SpellBase).GetField("<Bitfield>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(bitfield, "SpellBase.Bitfield's backing field was not found by reflection - has it been renamed?");
            bitfield.SetValue(spellBase, beneficial ? (uint)SpellFlags.Beneficial : 0u);

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;
            return spell;
        }

        private static Creature BoundMonster()
        {
            var monster = Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(0));
            monster.P_OutdoorPerimeter = RunPerimeter().Bind(Point(150, 96));
            return monster;
        }

        [TestMethod]
        public void A_player_past_R_cannot_harm_a_run_creature()
        {
            var monster = BoundMonster();
            var outside = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(61));

            Assert.AreSame(PvpArenaHookSettings.SpawnProtectedRefusal, outside.CheckPKStatusVsTarget(monster, null), "a weapon hit");
            Assert.AreSame(PvpArenaHookSettings.SpawnProtectedRefusal, outside.CheckPKStatusVsTarget(monster, FakeSpell(beneficial: false)), "a harmful spell");
        }

        [TestMethod]
        public void A_player_inside_R_and_every_unbound_creature_keep_the_retail_rules()
        {
            var monster = BoundMonster();
            Assert.IsNull(Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(60)).CheckPKStatusVsTarget(monster, null), "exactly R is inside");

            var unbound = Seeded<Creature>(PlayerKillerStatus.NPK, East_Of_Centre(0));
            Assert.IsNull(Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(500)).CheckPKStatusVsTarget(unbound, null), "no binding, no gate");
        }

        [TestMethod]
        public void A_beneficial_spell_is_not_refused_by_the_perimeter_gate()
        {
            var monster = BoundMonster();
            var outside = Seeded<Player>(PlayerKillerStatus.PK, East_Of_Centre(61));
            Assert.AreNotSame(PvpArenaHookSettings.SpawnProtectedRefusal, outside.CheckPKStatusVsTarget(monster, FakeSpell(beneficial: true)));
        }

        internal static string DamageGateFault(string body)
        {
            var objective = body.IndexOf("TryBattlegroundObjectiveGate(targetCreature, spell, out var objectiveResult)", StringComparison.Ordinal);
            var gate = body.IndexOf("(spell == null || spell.IsHarmful) && TryOutdoorPerimeterDamageGate(targetCreature, out var perimeterResult)", StringComparison.Ordinal);
            var arena = body.IndexOf("TryPvpArenaGate(", StringComparison.Ordinal);
            var retail = body.IndexOf("if (PlayerKillerStatus == PlayerKillerStatus.Free", StringComparison.Ordinal);

            if (gate < 0) return "the perimeter gate is not called for harmful actions";
            if (objective < 0 || objective > gate) return "the crystal gate must stay first";
            if (arena < gate || retail < gate) return "the perimeter gate must run before the arena gate and the retail rules";
            return null;
        }

        private static string PkBody() => PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Combat.cs"),
            "public override List<WeenieErrorWithString> CheckPKStatusVsTarget(WorldObject target, Spell spell)");

        [TestMethod]
        public void CheckPKStatusVsTarget_asks_the_perimeter_gate_after_the_crystal_gate()
        {
            Assert.IsNull(DamageGateFault(PkBody()));
            Assert.IsNotNull(DamageGateFault(PkBody().Replace("TryOutdoorPerimeterDamageGate(", "TryPvpSafeZoneGateX(")));
        }

        // ---- the monster-tick hooks (source pins: they need a live landblock and physics) ----

        private static string Read(string file) => PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/" + file);

        internal static string HookFault(string navigation, string awareness, string combat, string partial)
        {
            var missHome = PooledLootSourceText.MethodBody(navigation, "public void CheckMissHome()");
            if (!missHome.Contains("if (P_OutdoorPerimeter != null && OutdoorPerimeterTick())\n                return;", StringComparison.Ordinal))
                return "R11a/b: CheckMissHome does not run the perimeter tick";

            var find = PooledLootSourceText.MethodBody(awareness, "public virtual bool FindNextTarget()");
            if (!find.Contains("FilterOutdoorPerimeterTargets(FilterTetheredTargets(GetAttackTargets()))", StringComparison.Ordinal))
                return "R11b/R13: FindNextTarget does not filter candidates";
            if (!find.Contains("if (P_OutdoorPerimeter != null && !visibleTargets.Contains(AttackTarget as Creature))", StringComparison.Ordinal))
                return "R11b: a tactic pick outside the filtered list is not snapped back";

            var sleep = PooledLootSourceText.MethodBody(awareness, "public virtual void Sleep()");
            if (!sleep.Contains("if (MonsterState == State.Return && P_OutdoorPerimeter != null)\n                OnOutdoorPerimeterHomeArrival();", StringComparison.Ordinal))
                return "R11c: arrival home does not restore health";
            if (sleep.IndexOf("OnOutdoorPerimeterHomeArrival", StringComparison.Ordinal) > sleep.IndexOf("SetCombatMode(", StringComparison.Ordinal))
                return "R11c: the home check must read MonsterState before Sleep resets it";

            var alert = PooledLootSourceText.MethodBody(combat, "public bool AlertMonster(Creature monster)");
            if (!alert.Contains("if (monster.P_OutdoorPerimeter != null && !monster.AcceptsOutdoorPerimeterAlert(this))", StringComparison.Ordinal))
                return "R11b/R13: a friendly alert can wake a run creature onto an invalid target (or skips the throttled check)";

            var alertGate = PooledLootSourceText.MethodBody(partial, "public bool AcceptsOutdoorPerimeterAlert(Creature alerter)");
            if (!alertGate.Contains("perimeter.AcceptsAlert(alerter is Player, alerter.Location, Timers.RunningTime, ref outdoorAlertLosCheckedAt,", StringComparison.Ordinal))
                return "F2: the alert path does not go through the line-of-sight throttle";

            var filter = PooledLootSourceText.MethodBody(partial, "public List<Creature> FilterOutdoorPerimeterTargets(List<Creature> targets)");
            if (!filter.Contains("FilterCandidates(perimeter, targets, t => ReferenceEquals(t, attackTarget), t => IsDirectVisible(t))", StringComparison.Ordinal))
                return "F1: the target filter must exempt exactly the attack target from line of sight";

            var tick = PooledLootSourceText.MethodBody(partial, "internal bool OutdoorPerimeterTick()");
            var home = tick.IndexOf("perimeter.ShouldReturnHome(Location)", StringComparison.Ordinal);
            var taunt = tick.IndexOf("TauntTarget = null;", StringComparison.Ordinal);
            var drop = tick.IndexOf("AttackTarget = null;", StringComparison.Ordinal);
            var retarget = tick.IndexOf("FindNextTarget()", StringComparison.Ordinal);
            if (home < 0 || taunt < 0 || drop < 0 || retarget < 0 || !(home < taunt && taunt < drop && drop < retarget))
                return "OutdoorPerimeterTick: containment, then the taunt and attack target drop, then the retarget";

            var arrival = PooledLootSourceText.MethodBody(partial, "internal void OnOutdoorPerimeterHomeArrival()");
            if (!arrival.Contains("UpdateVitalDelta(Health, missing)", StringComparison.Ordinal))
                return "R11c: arrival does not heal to full";

            return null;
        }

        [TestMethod]
        public void The_monster_hooks_are_wired()
        {
            Assert.IsNull(HookFault(Read("Monster_Navigation.cs"), Read("Monster_Awareness.cs"), Read("Creature_Combat.cs"), Read("Creature_OutdoorPerimeter.cs")));
        }

        [TestMethod]
        public void The_hook_check_discriminates()
        {
            var nav = Read("Monster_Navigation.cs");
            var aw = Read("Monster_Awareness.cs");
            var co = Read("Creature_Combat.cs");
            var pa = Read("Creature_OutdoorPerimeter.cs");

            Assert.IsNotNull(HookFault(nav.Replace("OutdoorPerimeterTick())", "false)"), aw, co, pa));
            Assert.IsNotNull(HookFault(nav, aw.Replace("FilterOutdoorPerimeterTargets(FilterTetheredTargets(GetAttackTargets()))", "FilterTetheredTargets(GetAttackTargets())"), co, pa));
            Assert.IsNotNull(HookFault(nav, aw.Replace("OnOutdoorPerimeterHomeArrival();", ";"), co, pa));
            Assert.IsNotNull(HookFault(nav, aw, co.Replace("!monster.AcceptsOutdoorPerimeterAlert(this)", "false"), pa));
            Assert.IsNotNull(HookFault(nav, aw, co, pa.Replace("TauntTarget = null;", ";")));
            Assert.IsNotNull(HookFault(nav, aw, co, pa.Replace("t => ReferenceEquals(t, attackTarget), t =>", "t => false, t =>")));
            Assert.IsNotNull(HookFault(nav, aw, co, pa.Replace("t => ReferenceEquals(t, attackTarget), t =>", "t => ReferenceEquals(t, attackTarget) || ReferenceEquals(t, TauntTarget), t =>")));
            Assert.IsNotNull(HookFault(nav, aw, co, pa.Replace("ref outdoorAlertLosCheckedAt,", "ref unthrottled,")));
        }
    }
}
