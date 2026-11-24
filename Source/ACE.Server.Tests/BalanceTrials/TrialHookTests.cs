using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.BalanceTrials;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Task 10: the combat, reward and consumable hooks that feed a rep's TrialRecorder, and the recorder's life cycle
    /// (TrialRunLog). Where a fixture can reach the real site (DamageHistory.Add on a real Creature, TrialSession's death and
    /// rep events), the test drives it; where it cannot (a bare Player cannot run DamageTarget, OnEvade, TryResistSpell,
    /// OnDeath_GrantXP's EarnXP, UpdateXpAndLevel, AddLuminance, Food/Healer/ManaStone use or CastClassAbilityProc), the test
    /// drives the TrialHooks method and an exact source pin proves the site calls it, gated on one field read. Every hook also
    /// has a no-fire test: an event that does not belong to the live rep leaves its recorder untouched.
    ///
    /// A Player is only ever an uninitialized object whose fields are written through UnsafeAccessor (never FieldInfo.SetValue).
    /// PropertyManager: no key is read by TrialHooks or TrialRunLog. Creature construction and DamageHistory.Add read only
    /// compiled defaults, which ClassInitialize loads (as TrialWiringTests does). Every static seam touched here is reset in
    /// TestCleanup.
    /// </summary>
    [TestClass]
    public class TrialHookTests
    {
        private const double Now = 10.0;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Biota>k__BackingField")]
        private static extern ref Biota BiotaField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BiotaDatabaseLock")]
        private static extern ref System.Threading.ReaderWriterLockSlim LockField(WorldObject wo);

        private static uint nextGuid = 0x7ED80000;

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Setup()
        {
            TrialHooks.Now = () => Now;
        }

        [TestCleanup]
        public void Cleanup()
        {
            TrialHooks.ResetForTests();
            TrialRunLog.ResetForTests();
            TrialManager.ResetForTests();
        }

        // ---- fixtures ----------------------------------------------------------------------------------------

        private static Player BarePlayer()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            GuidField(player) = new ObjectGuid(nextGuid++);
            BiotaField(player) = new Biota { PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Trial Hook Player" } };
            LockField(player) = new System.Threading.ReaderWriterLockSlim();
            return player;
        }

        private static Creature NewCreature(uint wcid = 0x7ED8)
        {
            TestGameTables.EnsureInitialized();

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = "trialhookprobe" + wcid,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 100 },
                PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.Attackable] = true },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Trial Hook Probe" },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 1000, CurrentLevel = 1000 },
                },
            };

            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        private static Creature TrialCreature(TrialRep rep, DungeonRole role = DungeonRole.Elite, uint wcid = 0x7ED8)
        {
            var c = NewCreature(wcid);
            c.P_TrialRep = rep;
            c.P_TrialRole = role;
            return c;
        }

        private sealed class FakeWorld : ITrialWorld
        {
            public bool RoomLoaded => true;
            public bool PlayerOnline => true;
            public bool PlayerInRoom => true;
            public bool Alive = true;
            private uint next = 900;

            public int LiveForeignCreatures() => 0;
            public bool IsAlive(uint guid) => Alive;
            public void DestroyAll() { }
            public void ReturnPlayerToStart() { }
            public IReadOnlyList<uint> SpawnWave(IReadOnlyList<DungeonSpawnPlanEntry> wave, TrialRep rep) => new[] { next++ };
        }

        private sealed class Live
        {
            public TrialSession Session;
            public TrialRep Rep;
            public FakeWorld World;
            public Player Player;
            public List<AnalyticsDatabase.TrialEventRow> Rows = new();
            public TrialRecorder Recorder => Rep.Recorder;
        }

        private static TrialRep OneWaveRep()
            => new TrialRep { Waves = new List<IReadOnlyList<DungeonSpawnPlanEntry>> { new List<DungeonSpawnPlanEntry>() } };

        /// <summary>A running one-wave rep, its recorder writing into Rows, bound to a bare player.</summary>
        private static Live Running()
        {
            var live = new Live { World = new FakeWorld(), Rep = OneWaveRep(), Player = BarePlayer() };
            live.Session = new TrialSession("uuid", new TrialScenario("s", "r", null, 1, null, "off", 3, 600, 0), "ssha", null, "asha",
                null, live.Player.Guid.Full, live.World, _ => live.Rep);
            Assert.IsTrue(live.Session.TryNext(1, out var refusal), refusal);
            live.Rep.Recorder = new TrialRecorder(live.Rep.RunKey, 1, new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc), live.Rows.Add);
            live.Player.TrialSession = live.Session;
            return live;
        }

        private static AnalyticsDatabase.TrialRunRow Finish(Live live)
            => live.Recorder.Finish(TrialEventKinds.EndCleared, 20, new DateTime(2026, 11, 18, 0, 0, 19, DateTimeKind.Utc), new TrialRunFacts());

        // ---- source pins --------------------------------------------------------------------------------------

        /// <summary>Comments stripped, every whitespace run collapsed to one space: an exact token sequence, immune to layout.</summary>
        private static string Norm(string body) => Regex.Replace(Regex.Replace(body, @"//[^\n]*", ""), @"\s+", " ");

        private static string Body(string path, string signature) => Norm(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(path), signature));

        private static int Count(string haystack, string needle) => Regex.Matches(haystack, Regex.Escape(needle)).Count;

        private const string DamageHistoryPath = "Source/ACE.Server/Entity/DamageHistory.cs";
        private const string PlayerCombatPath = "Source/ACE.Server/WorldObjects/Player_Combat.cs";
        private const string MagicPath = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";
        private const string CreatureDeathPath = "Source/ACE.Server/WorldObjects/Creature_Death.cs";
        private const string PlayerXpPath = "Source/ACE.Server/WorldObjects/Player_Xp.cs";
        private const string PlayerLumPath = "Source/ACE.Server/WorldObjects/Player_Luminance.cs";
        private const string FoodPath = "Source/ACE.Server/WorldObjects/Food.cs";
        private const string HealerPath = "Source/ACE.Server/WorldObjects/Healer.cs";
        private const string ManaStonePath = "Source/ACE.Server/WorldObjects/ManaStone.cs";
        private const string ProcPath = "Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs";
        private const string ManagerPath = "Source/ACE.Server/BalanceTrials/TrialManager.cs";
        private const string StarterPath = "Source/ACE.Server/BalanceTrials/TrialStarter.cs";

        private const string AddSig = "public void Add(WorldObject attacker, DamageType damageType, uint amount)";
        private const string Add4Sig = "public void Add(WorldObject attacker, DamageType damageType, uint amount, uint thrown, DamageAttackKind? kind = null, bool? isCritical = null, uint? spellId = null)";
        private const string DamageHook = "if (Creature.P_TrialRep != null || Creature is Player { TrialSession: not null }) ACE.Server.BalanceTrials.TrialHooks.OnDamageRecorded(Creature, attacker, damageType, amount);";

        // ================================================================ DamageHistory.Add

        [TestMethod]
        public void Hit_out_records_through_DamageHistory_Add()
        {
            var live = Running();
            var creature = TrialCreature(live.Rep, DungeonRole.Elite, 0x7ED9);

            creature.DamageHistory.Add(live.Player, DamageType.Fire, 37);

            Assert.AreEqual(1, live.Rows.Count);
            var r = live.Rows[0];
            Assert.AreEqual(TrialEventKinds.Hit, r.Kind);
            Assert.AreEqual(TrialEventKinds.DirOut, r.Dir);
            Assert.AreEqual(0x7ED9u, r.TargetWcid);
            Assert.AreEqual("Elite", r.ActorRole);
            Assert.AreEqual("Fire", r.DamageType);
            Assert.AreEqual(37L, r.Amount);
            Assert.AreEqual(9000, r.TMs, "stamped with the hook clock (10.0 - start 1.0)");
            Assert.AreEqual(37L, Finish(live).DmgDealt);
        }

        [TestMethod]
        public void A_4_arg_DamageHistory_hit_records_once()
        {
            var live = Running();
            var creature = TrialCreature(live.Rep);

            creature.DamageHistory.Add(live.Player, DamageType.Slash, 30, 45);

            Assert.AreEqual(1, live.Rows.Count, "the 4-argument overload passes through the 3-argument one, which holds the only hook");
            var row = Finish(live);
            Assert.AreEqual(1, row.HitsOut);
            Assert.AreEqual(30L, row.DmgDealt, "the applied amount, not the thrown one");
        }

        [TestMethod]
        public void A_zero_amount_landed_hit_records_as_a_hit()
        {
            var live = Running();
            TrialCreature(live.Rep).DamageHistory.Add(live.Player, DamageType.Cold, 0);

            Assert.AreEqual(1, Finish(live).HitsOut, "the hook precedes the zero return: a hit absorbed to 0 still landed");
        }

        [TestMethod]
        public void Hit_out_no_fire_outside_a_trial()
        {
            var live = Running();

            // A creature no trial owns, hit by the trial's own player.
            NewCreature().DamageHistory.Add(live.Player, DamageType.Slash, 50);

            // A creature of another (finished) rep.
            var other = OneWaveRep();
            TrialCreature(other).DamageHistory.Add(live.Player, DamageType.Slash, 50);

            // A trial creature hitting another trial creature.
            TrialCreature(live.Rep).DamageHistory.Add(TrialCreature(live.Rep), DamageType.Slash, 50);

            Assert.AreEqual(0, live.Rows.Count);
            Assert.AreEqual(0, Finish(live).HitsOut);
        }

        [TestMethod]
        public void Hit_in_records_for_the_bound_player()
        {
            var live = Running();
            var attacker = TrialCreature(live.Rep, DungeonRole.Boss, 0x7EDA);

            // The site's own call (a bare Player cannot run the rest of DamageHistory.Add: DamageHistoryEntry reads Health).
            TrialHooks.OnDamageRecorded(live.Player, attacker, DamageType.Acid, 44);

            Assert.AreEqual(1, live.Rows.Count);
            var r = live.Rows[0];
            Assert.AreEqual(TrialEventKinds.DirIn, r.Dir);
            Assert.AreEqual(0x7EDAu, r.ActorWcid);
            Assert.AreEqual("Boss", r.ActorRole);
            Assert.AreEqual(44L, r.Amount);
            var row = Finish(live);
            Assert.AreEqual(1, row.HitsIn);
            Assert.AreEqual(44L, row.DmgTaken);
        }

        [TestMethod]
        public void Hit_in_no_fire_outside_a_trial()
        {
            var live = Running();

            TrialHooks.OnDamageRecorded(BarePlayer(), TrialCreature(live.Rep), DamageType.Acid, 44);   // an unbound player
            TrialHooks.OnDamageRecorded(live.Player, NewCreature(), DamageType.Acid, 44);               // a creature no trial owns
            TrialHooks.OnDamageRecorded(live.Player, TrialCreature(OneWaveRep()), DamageType.Acid, 44); // a creature of another rep

            Assert.AreEqual(0, live.Rows.Count);
        }

        [TestMethod]
        public void DamageHistory_Add_hooks_once_before_the_zero_return()
        {
            var src = Norm(PooledLootSourceText.Read(DamageHistoryPath));
            var add = Body(DamageHistoryPath, AddSig);
            var add4 = Body(DamageHistoryPath, Add4Sig);

            Assert.AreEqual(1, Count(src, "TrialHooks."), "one trial hook in DamageHistory");
            Assert.AreEqual(1, Count(add, DamageHook), "the 3-argument Add holds it");
            Assert.AreEqual(0, Count(add4, "TrialHooks."), "the 4-argument Add does not (it calls the 3-argument one)");
            StringAssert.Contains(add4, "Add(attacker, damageType, amount);");
            Assert.IsTrue(add.IndexOf(DamageHook, StringComparison.Ordinal) < add.IndexOf("if (amount == 0)", StringComparison.Ordinal), "before the zero return");
            StringAssert.StartsWith(add, "{ " + DamageHook, "the first statement");
        }

        // ================================================================ miss out

        private const string DamageTargetSig = "public DamageEvent DamageTarget(Creature target, WorldObject damageSource, float damageMultiplier = 1.0f, bool primaryTarget = true, bool powerBarSwing = true)";
        private const string MissHook = "else { if (target.P_TrialRep != null) ACE.Server.BalanceTrials.TrialHooks.OnMissOut(this, target, damageEvent.CombatType); if (damageEvent.LifestoneProtection)";

        [TestMethod]
        public void Miss_out_records_for_a_trial_creature_target()
        {
            var live = Running();
            TrialHooks.OnMissOut(live.Player, TrialCreature(live.Rep, DungeonRole.Trash, 0x7EDB), CombatType.Missile);

            Assert.AreEqual(1, live.Rows.Count);
            Assert.AreEqual(TrialEventKinds.Miss, live.Rows[0].Kind);
            Assert.AreEqual("Missile", live.Rows[0].CombatType);
            Assert.AreEqual(0x7EDBu, live.Rows[0].TargetWcid);
            Assert.AreEqual(1, Finish(live).MissesOut);
        }

        [TestMethod]
        public void Miss_out_no_fire_outside_a_trial()
        {
            var live = Running();
            TrialHooks.OnMissOut(live.Player, NewCreature(), CombatType.Melee);
            TrialHooks.OnMissOut(BarePlayer(), TrialCreature(live.Rep), CombatType.Melee);
            Assert.AreEqual(0, live.Rows.Count);
        }

        [TestMethod]
        public void DamageTarget_miss_branch_calls_the_hook()
        {
            var body = Body(PlayerCombatPath, DamageTargetSig);
            Assert.AreEqual(1, Count(body, MissHook), "the no-damage else branch, first, gated on the target's P_TrialRep");
            Assert.AreEqual(1, Count(body, "TrialHooks."));
        }

        // ================================================================ evade in

        private const string EvadeSig = "public override void OnEvade(WorldObject attacker, CombatType attackType)";
        private const string EvadeHook = "if (TrialActive) ACE.Server.BalanceTrials.TrialHooks.OnEvadeIn(this, attacker, attackType);";

        [TestMethod]
        public void Evade_in_records_for_a_trial_creature_attacker()
        {
            var live = Running();
            TrialHooks.OnEvadeIn(live.Player, TrialCreature(live.Rep, DungeonRole.Trash, 0x7EDC), CombatType.Melee);

            Assert.AreEqual(1, live.Rows.Count);
            Assert.AreEqual(TrialEventKinds.Evade, live.Rows[0].Kind);
            Assert.AreEqual(TrialEventKinds.DirIn, live.Rows[0].Dir);
            Assert.AreEqual(0x7EDCu, live.Rows[0].ActorWcid);
            Assert.AreEqual(1, Finish(live).EvadedIn);
        }

        [TestMethod]
        public void Evade_in_no_fire_outside_a_trial()
        {
            var live = Running();
            TrialHooks.OnEvadeIn(live.Player, NewCreature(), CombatType.Melee);
            TrialHooks.OnEvadeIn(BarePlayer(), TrialCreature(live.Rep), CombatType.Melee);
            TrialHooks.OnEvadeIn(live.Player, BarePlayer(), CombatType.Melee);
            Assert.AreEqual(0, live.Rows.Count);
        }

        [TestMethod]
        public void OnEvade_calls_the_hook_first_before_the_lifestone_return()
        {
            var body = Body(PlayerCombatPath, EvadeSig);
            StringAssert.StartsWith(body, "{ " + EvadeHook, "the first statement");
            Assert.IsTrue(body.IndexOf(EvadeHook, StringComparison.Ordinal) < body.IndexOf("if (UnderLifestoneProtection) return;", StringComparison.Ordinal));
            Assert.AreEqual(1, Count(body, "TrialHooks."));
        }

        // ================================================================ resists

        private const string ResistSig = "public bool TryResistSpell(WorldObject target, Spell spell, WorldObject itemCaster = null, bool projectileHit = false, Skill? casterSkillOverride = null)";
        private const string ResistHook = "if (resisted) { if (targetCreature.P_TrialRep != null || targetPlayer?.TrialActive == true) ACE.Server.BalanceTrials.TrialHooks.OnSpellResisted(casterCreature, targetCreature, spell.Id); if (player != null)";

        [TestMethod]
        public void Resists_record_in_both_directions()
        {
            var live = Running();

            TrialHooks.OnSpellResisted(live.Player, TrialCreature(live.Rep, DungeonRole.Elite, 0x7EDD), 2001);   // out: the creature resisted
            TrialHooks.OnSpellResisted(TrialCreature(live.Rep, DungeonRole.Boss, 0x7EDE), live.Player, 2002);   // in: the player resisted

            Assert.AreEqual(2, live.Rows.Count);
            Assert.AreEqual(TrialEventKinds.DirOut, live.Rows[0].Dir);
            Assert.AreEqual(0x7EDDu, live.Rows[0].TargetWcid);
            Assert.AreEqual(2001u, live.Rows[0].SpellId);
            Assert.AreEqual(TrialEventKinds.DirIn, live.Rows[1].Dir);
            Assert.AreEqual(0x7EDEu, live.Rows[1].ActorWcid);
            Assert.AreEqual(2002u, live.Rows[1].SpellId);

            var row = Finish(live);
            Assert.AreEqual(1, row.ResistedOut);
            Assert.AreEqual(1, row.ResistedIn);
        }

        [TestMethod]
        public void Resists_no_fire_outside_a_trial()
        {
            var live = Running();
            TrialHooks.OnSpellResisted(live.Player, NewCreature(), 1);                       // a creature no trial owns
            TrialHooks.OnSpellResisted(TrialCreature(live.Rep), TrialCreature(live.Rep), 1); // trial creature on trial creature
            TrialHooks.OnSpellResisted(TrialCreature(live.Rep), BarePlayer(), 1);            // an unbound player
            TrialHooks.OnSpellResisted(NewCreature(), live.Player, 1);                       // a caster no trial owns
            Assert.AreEqual(0, live.Rows.Count);
        }

        [TestMethod]
        public void TryResistSpell_resisted_branch_calls_the_hook()
        {
            var body = Body(MagicPath, ResistSig);
            Assert.AreEqual(1, Count(body, ResistHook), "first inside if (resisted), gated on one field read per direction");
            Assert.AreEqual(1, Count(body, "TrialHooks."));
        }

        // ================================================================ kill award

        private const string GrantXpSig = "public void OnDeath_GrantXP()";
        private const string KillAwardBlock = "var xpAward = (long)Math.Round(totalXP); playerDamager.EarnXP(xpAward, XpType.Kill); var lumAward = 0L; if (LuminanceAward != null) { lumAward = (long)Math.Round(LuminanceAward.Value * damagePercent); playerDamager.EarnLuminance(lumAward, XpType.Kill); } if (P_TrialRep != null) ACE.Server.BalanceTrials.TrialHooks.OnKillAward(this, playerDamager, xpAward, lumAward);";

        [TestMethod]
        public void Kill_award_is_captured_on_the_creature_and_reaches_the_kill_event()
        {
            var live = Running();
            var creature = TrialCreature(live.Rep, DungeonRole.Boss, 0x7EDF);

            TrialHooks.OnKillAward(creature, live.Player, 1200, 34);

            Assert.AreEqual(1200L, creature.P_TrialKillXp);
            Assert.AreEqual(34L, creature.P_TrialKillLum);

            TrialHooks.RecordKillAndAdvance(live.Session, live.Rep, creature.Guid.Full, creature.WeenieClassId, creature.P_TrialRole, creature.P_TrialKillXp, creature.P_TrialKillLum, Now);

            var kill = live.Rows.Single(r => r.Kind == TrialEventKinds.Kill);
            Assert.AreEqual(0x7EDFu, kill.TargetWcid);
            Assert.AreEqual("Boss", kill.ActorRole);
            Assert.AreEqual(1200L, kill.Xp);
            Assert.AreEqual(34L, kill.Lum);
        }

        [TestMethod]
        public void Kill_award_no_fire_outside_a_trial()
        {
            var live = Running();

            var plain = NewCreature();
            TrialHooks.OnKillAward(plain, live.Player, 1200, 34);
            Assert.AreEqual(0L, plain.P_TrialKillXp, "a creature no trial owns");

            var stranger = TrialCreature(live.Rep);
            TrialHooks.OnKillAward(stranger, BarePlayer(), 1200, 34);
            Assert.AreEqual(0L, stranger.P_TrialKillXp, "a player not in the rep");

            // A kill of an earlier rep's creature neither records nor advances.
            var old = OneWaveRep();
            TrialHooks.RecordKillAndAdvance(live.Session, old, 1, 2, DungeonRole.Trash, 5, 5, Now);
            Assert.AreEqual(0, live.Rows.Count);
            Assert.AreEqual(TrialPhase.Running, live.Session.Phase);
        }

        [TestMethod]
        public void OnDeath_GrantXP_captures_the_award_in_the_per_player_loop()
        {
            var body = Body(CreatureDeathPath, GrantXpSig);
            Assert.AreEqual(1, Count(body, KillAwardBlock));
            var loop = body.IndexOf("foreach (var kvp in damageByPlayer)", StringComparison.Ordinal);
            Assert.IsTrue(loop >= 0 && body.IndexOf(KillAwardBlock, StringComparison.Ordinal) > loop, "inside the per-player loop");
        }

        [TestMethod]
        public void OnTrialCreatureDied_records_the_kill_with_the_captured_award()
        {
            var body = Body(ManagerPath, "public static void OnTrialCreatureDied(Creature creature)");
            StringAssert.Contains(body, "var wcid = creature.WeenieClassId; var role = creature.P_TrialRole; var xp = creature.P_TrialKillXp; var lum = creature.P_TrialKillLum;");
            StringAssert.Contains(body, "Enqueue(session, () => TrialHooks.RecordKillAndAdvance(session, rep, guid, wcid, role, xp, lum, Now()));");
            Assert.AreEqual(0, Count(body, "session.OnCreatureDied("), "the session advances only through RecordKillAndAdvance");
        }

        // ================================================================ XP and luminance at the grant site

        private const string UpdateXpSig = "private void UpdateXpAndLevel(long amount, XpType xpType)";
        private const string XpHook = "AnalyticsManager.RecordXp(this, addAmount); if (TrialActive) ACE.Server.BalanceTrials.TrialHooks.OnXpGranted(this, addAmount);";
        private const string AddLumSig = "private void AddLuminance(long amount, XpType xpType)";
        private const string LumHook = "AnalyticsManager.RecordLuminance(this, amount); if (TrialActive) ACE.Server.BalanceTrials.TrialHooks.OnLumGranted(this, amount);";

        [TestMethod]
        public void Xp_and_lum_grants_accumulate_into_the_run_row()
        {
            var live = Running();
            TrialHooks.OnXpGranted(live.Player, 1000);
            TrialHooks.OnXpGranted(live.Player, 500);
            TrialHooks.OnLumGranted(live.Player, 70);

            var row = Finish(live);
            Assert.AreEqual(1500L, row.XpEarned);
            Assert.AreEqual(70L, row.LumEarned);
        }

        [TestMethod]
        public void Xp_and_lum_no_fire_outside_a_trial()
        {
            var live = Running();
            TrialHooks.OnXpGranted(BarePlayer(), 1000);
            TrialHooks.OnLumGranted(BarePlayer(), 70);

            var row = Finish(live);
            Assert.AreEqual(0L, row.XpEarned);
            Assert.AreEqual(0L, row.LumEarned);
        }

        [TestMethod]
        public void Grant_sites_call_the_hooks_beside_analytics()
        {
            Assert.AreEqual(1, Count(Body(PlayerXpPath, UpdateXpSig), XpHook));
            Assert.AreEqual(1, Count(Body(PlayerLumPath, AddLumSig), LumHook));
        }

        [TestMethod]
        public void Xp_total_comes_from_the_grant_site_not_the_kill()
        {
            var live = Running();
            var runs = new List<AnalyticsDatabase.TrialRunRow>();
            var deferred = new List<Action>();
            TrialRunLog.RunSink = runs.Add;
            TrialRunLog.EventSink = live.Rows.Add;
            TrialRunLog.Defer = (_, write) => deferred.Add(write);
            TrialRunLog.Attach(live.Session);

            // The kill pays 1000 by the award, but the grant site adds 1500 (the player's own modifiers), and the session sees
            // the death before the player's queue applies the XP: the rep ends first.
            var boss = TrialCreature(live.Rep, DungeonRole.Boss);
            TrialHooks.OnKillAward(boss, live.Player, 1000, 0);
            live.World.Alive = false;
            TrialHooks.RecordKillAndAdvance(live.Session, live.Rep, boss.Guid.Full, boss.WeenieClassId, boss.P_TrialRole, boss.P_TrialKillXp, boss.P_TrialKillLum, Now);

            Assert.AreEqual(TrialPhase.Ready, live.Session.Phase, "the rep ended on the kill");
            Assert.AreEqual(0, runs.Count, "the run row waits behind the player's queue");
            Assert.AreEqual(1, deferred.Count);

            // The player's queue: the kill's XP lands, then the row is written.
            TrialHooks.OnXpGranted(live.Player, 1500);
            deferred[0]();

            Assert.AreEqual(1, runs.Count);
            Assert.AreEqual(1500L, runs[0].XpEarned, "xp_earned is the grant-site total");
            Assert.AreEqual(1000L, live.Rows.Single(r => r.Kind == TrialEventKinds.Kill).Xp, "the kill event keeps the award");
            Assert.AreEqual(TrialEventKinds.EndCleared, runs[0].EndState);
            Assert.IsNull(live.Rep.Recorder, "the row is written once and the recorder released");

            // A grant after the row is written goes nowhere, and a second write is a no-op.
            TrialHooks.OnXpGranted(live.Player, 99);
            deferred[0]();
            Assert.AreEqual(1, runs.Count);
        }

        // ================================================================ consumables

        [TestMethod]
        public void Consume_records_during_a_running_rep()
        {
            var live = Running();
            TrialHooks.OnConsume(live.Player, 0x1234);

            Assert.AreEqual(1, live.Rows.Count);
            Assert.AreEqual(TrialEventKinds.Consume, live.Rows[0].Kind);
            Assert.AreEqual(0x1234u, live.Rows[0].ItemWcid);
            Assert.AreEqual(1, Finish(live).ConsumablesUsed);
        }

        [TestMethod]
        public void Consume_no_fire_outside_a_running_rep()
        {
            var live = Running();
            TrialHooks.OnConsume(BarePlayer(), 0x1234);

            // Between reps: the rep ended, its recorder is still waiting for the deferred row, nothing counts.
            live.World.Alive = false;
            live.Session.OnCreatureDied(900, Now);
            Assert.AreEqual(TrialPhase.Ready, live.Session.Phase);
            TrialHooks.OnConsume(live.Player, 0x1234);
            TrialHooks.OnProc(live.Player);

            Assert.AreEqual(0, live.Rows.Count);
        }

        [TestMethod]
        public void Each_consumable_use_path_calls_the_hook()
        {
            const string FoodHook = "if (player.TrialActive) ACE.Server.BalanceTrials.TrialHooks.OnConsume(player, WeenieClassId); if (BoosterEnum != PropertyAttribute2nd.Undef)";
            Assert.AreEqual(1, Count(Body(FoodPath, "public void ApplyConsumable(Player player)"), FoodHook), "Food: after the checks, before the effects");

            const string HealerHook = "if (target.IsDead || target.Teleporting) return; if (healer.TrialActive) ACE.Server.BalanceTrials.TrialHooks.OnConsume(healer, WeenieClassId);";
            StringAssert.StartsWith(Body(HealerPath, "public void DoHealing(Player healer, Player target, uint missingVital)"), "{ " + HealerHook, "Healer: every use, a failed heal included");

            const string StoneHook = "player.Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast)); if (player.TrialActive) ACE.Server.BalanceTrials.TrialHooks.OnConsume(player, WeenieClassId); if (!DoDestroyDiceRoll(player) && !UnlimitedUse)";
            Assert.AreEqual(2, Count(Body(ManaStonePath, "public override void HandleActionUseOnTarget(Player player, WorldObject target)"), StoneHook), "ManaStone: both mana-giving branches");
        }

        // ================================================================ procs

        private const string ProcSig = "public void CastClassAbilityProc(Action cast)";
        private const string ProcHook = "if (cast == null) return; if (TrialActive) ACE.Server.BalanceTrials.TrialHooks.OnProc(this);";

        [TestMethod]
        public void Proc_records_during_a_running_rep()
        {
            var live = Running();
            TrialHooks.OnProc(live.Player);

            Assert.AreEqual(1, live.Rows.Count);
            Assert.AreEqual(TrialEventKinds.Proc, live.Rows[0].Kind);
            Assert.IsNull(live.Rows[0].SpellId, "the site does not know the spell");
            Assert.AreEqual(1, Finish(live).AbilityProcs);
        }

        [TestMethod]
        public void Proc_no_fire_outside_a_trial()
        {
            var live = Running();
            TrialHooks.OnProc(BarePlayer());
            Assert.AreEqual(0, live.Rows.Count);
        }

        [TestMethod]
        public void CastClassAbilityProc_calls_the_hook_once_for_every_overload()
        {
            var src = PooledLootSourceText.Read(ProcPath);
            StringAssert.StartsWith(Norm(PooledLootSourceText.MethodBody(src, ProcSig)), "{ " + ProcHook);

            // The two wider overloads forward to the one-argument method and hold no hook of their own.
            Assert.AreEqual(1, Count(Norm(src), "TrialHooks.OnProc("));
            StringAssert.Contains(Norm(PooledLootSourceText.MethodBody(src, "public void CastClassAbilityProc(Action cast, float procDamageScale)")), "CastClassAbilityProc(cast);");
            StringAssert.Contains(Norm(PooledLootSourceText.MethodBody(src, "public void CastClassAbilityProc(Action cast, float procDamageScale, DamageType? rendSourceDamageType)")), "() => CastClassAbilityProc(cast, procDamageScale));");
        }

        // ================================================================ death and vitals

        [TestMethod]
        public void Death_records_before_the_rep_ends()
        {
            var live = Running();
            live.Session.OnPlayerDied(Now);

            Assert.AreEqual(TrialEventKinds.Death, live.Rows.Single().Kind);
            Assert.AreEqual(TrialEventKinds.EndDied, live.Session.LastEndState);
            Assert.AreEqual(1, Finish(live).Deaths);

            // Not running: nothing more.
            live.Session.OnPlayerDied(Now + 1);
            Assert.AreEqual(1, live.Rows.Count);
        }

        [TestMethod]
        public void Vital_sample_reaches_the_recorder_only_while_running()
        {
            var live = Running();
            TrialHooks.OnVitalSample(live.Session, new TrialManager.VitalSample(0.2, 0.5, 0.75, Now));

            Assert.AreEqual(TrialEventKinds.VitalLow, live.Rows.Single().Kind);
            var row = Finish(live);
            Assert.AreEqual(0.2, row.HpLowPct);
            Assert.AreEqual(0.5, row.StamLowPct);
            Assert.AreEqual(0.75, row.ManaLowPct);

            // Not running: dropped.
            var idle = Running();
            idle.World.Alive = false;
            idle.Session.OnCreatureDied(900, Now);
            TrialHooks.OnVitalSample(idle.Session, new TrialManager.VitalSample(0.1, 0.1, 0.1, Now));
            Assert.AreEqual(0, idle.Rows.Count);
            Assert.IsNull(Finish(idle).HpLowPct);
        }

        [TestMethod]
        public void SampleVitals_routes_the_sample_to_the_hook()
        {
            var body = Body(ManagerPath, "private static void SampleVitals(Entry entry, double now)");
            StringAssert.Contains(body, "Volatile.Write(ref entry.LastVitals, sample); TrialHooks.OnVitalSample(entry.Session, sample);");
        }

        // ================================================================ life cycle

        [TestMethod]
        public void Rep_start_creates_the_recorder_and_rep_end_writes_one_run_row_with_the_facts()
        {
            var runs = new List<AnalyticsDatabase.TrialRunRow>();
            var events = new List<AnalyticsDatabase.TrialEventRow>();
            TrialRunLog.RunSink = runs.Add;
            TrialRunLog.EventSink = events.Add;
            TrialRunLog.Defer = (_, write) => write();
            TrialRunLog.UtcNow = () => new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
            TrialRunLog.PlayerLevel = _ => 275;

            var spec = new DungeonGemSpec("d01", 300, 6, "any", 77, new List<(string, double)>(), 0, 0);
            var plan = new DungeonSpawnPlan { FamilyId = "olthoi", XpMultiplier = 1.5, BossXpMultiplier = 2.0, LumMultiplier = 1.25 };
            var rep = new TrialRep
            {
                Gem = new TrialGemRoll(spec, 310, 77, "{\"mode\":\"rolled\"}"),
                Plan = plan,
                Waves = new List<IReadOnlyList<DungeonSpawnPlanEntry>> { new List<DungeonSpawnPlanEntry>() },
                StateDigest = "core",
                DigestDrift = true,
            };
            var world = new FakeWorld();
            var archetype = new TrialArchetype("arch", 1, default, 250, null, null, null);
            var session = new TrialSession("tuuid", new TrialScenario("scen", "r", null, 1, null, "off", 3, 600, 0), "ssha", archetype, "asha",
                null, 0x500000AA, world, _ => rep);
            TrialRunLog.Attach(session);

            Assert.IsTrue(session.TryNext(5, out var refusal), refusal);
            Assert.IsNotNull(rep.Recorder, "RepStarted created the recorder");

            var player = BarePlayer();
            player.TrialSession = session;
            TrialHooks.OnConsume(player, 42);
            Assert.AreEqual(1, events.Count, "the recorder's sink is the event sink");

            world.Alive = false;
            session.OnCreatureDied(900, 65);

            Assert.AreEqual(1, runs.Count);
            var row = runs[0];
            Assert.AreEqual(rep.RunKey, row.RunKey);
            Assert.AreEqual("tuuid", row.TrialUuid);
            Assert.AreEqual(1, row.Rep);
            Assert.AreEqual("scen", row.ScenarioId);
            Assert.AreEqual("ssha", row.ScenarioHash);
            Assert.AreEqual("arch", row.ArchetypeId);
            Assert.AreEqual("asha", row.ArchetypeChecksum);
            Assert.AreEqual("core", row.StateDigest);
            Assert.IsTrue(row.DigestDrift);
            Assert.AreEqual("manual", row.Driver);
            Assert.AreEqual(1, row.ClientCount);
            Assert.AreEqual(0x500000AAu, row.CharacterId);
            Assert.AreEqual(275, row.CharLevel);
            Assert.AreEqual(300, row.GemLevel);
            Assert.AreEqual(310, row.GemLevelPressed);
            Assert.AreEqual(6, row.Tier);
            Assert.AreEqual("d01", row.DungeonId);
            Assert.AreEqual("olthoi", row.Family, "the family the plan drew, not the spec's 'any'");
            Assert.AreEqual(77, row.Seed);
            Assert.AreEqual("{\"mode\":\"rolled\"}", row.ModsJson);
            Assert.AreEqual(1.5, row.XpMultiplier);
            Assert.AreEqual(2.0, row.BossXpMultiplier);
            Assert.AreEqual(1.25, row.LumMultiplier);
            Assert.AreEqual(TrialEventKinds.EndCleared, row.EndState);
            Assert.AreEqual(60.0, row.EngagedSecs + row.IdleSecs, 1e-9, "duration runs from StartedAt 5 to EndedAt 65");
            Assert.IsNull(row.PredPlayerHitsToKillBoss);
            Assert.IsNull(rep.Recorder);
        }

        [TestMethod]
        public void A_failed_first_wave_still_writes_a_forfeit_row()
        {
            var runs = new List<AnalyticsDatabase.TrialRunRow>();
            TrialRunLog.RunSink = runs.Add;
            TrialRunLog.EventSink = _ => { };
            TrialRunLog.Defer = (_, write) => write();
            TrialRunLog.PlayerLevel = _ => null;

            var rep = new TrialRep { Waves = new List<IReadOnlyList<DungeonSpawnPlanEntry>>() };   // no wave: the spawn fails
            var session = new TrialSession("t", new TrialScenario("s", "r", null, 1, null, "off", 3, 600, 0), "ssha", null, "asha",
                null, 1, new FakeWorld(), _ => rep);
            TrialRunLog.Attach(session);

            Assert.IsFalse(session.TryNext(1, out _));
            Assert.AreEqual(TrialEventKinds.EndForfeit, runs.Single().EndState);
        }

        [TestMethod]
        public void A_run_row_the_players_queue_never_writes_is_written_once_by_the_sweep()
        {
            var live = Running();
            var runs = new List<AnalyticsDatabase.TrialRunRow>();
            var captured = new List<Action>();
            TrialRunLog.RunSink = runs.Add;
            TrialRunLog.Defer = (_, write) => captured.Add(write);   // the player's queue never drains
            TrialRunLog.Attach(live.Session);
            TrialManager.LandblockLookup = _ => null;

            live.World.Alive = false;
            live.Session.OnCreatureDied(900, 50);                    // the rep ends at 50
            Assert.AreEqual(1, captured.Count);
            Assert.AreEqual(1, TrialRunLog.PendingCount);

            TrialManager.TickAt(50 + TrialRunLog.SweepAfterSecs);  // not yet overdue
            Assert.AreEqual(0, runs.Count);

            TrialManager.TickAt(50 + TrialRunLog.SweepAfterSecs + 0.5);
            Assert.AreEqual(1, runs.Count, "the sweep wrote the row");
            Assert.AreEqual(0, TrialRunLog.PendingCount);
            Assert.IsNull(live.Rep.Recorder);

            captured[0]();                                           // the queue drains late
            TrialManager.TickAt(100);
            Assert.AreEqual(1, runs.Count, "still exactly one row");
        }

        [TestMethod]
        public void A_queue_written_row_leaves_nothing_for_the_sweep()
        {
            var live = Running();
            var runs = new List<AnalyticsDatabase.TrialRunRow>();
            var captured = new List<Action>();
            TrialRunLog.RunSink = runs.Add;
            TrialRunLog.Defer = (_, write) => captured.Add(write);
            TrialRunLog.Attach(live.Session);
            TrialManager.LandblockLookup = _ => null;

            live.World.Alive = false;
            live.Session.OnCreatureDied(900, 50);
            captured[0]();
            Assert.AreEqual(0, TrialRunLog.PendingCount);

            TrialManager.TickAt(1000);
            Assert.AreEqual(1, runs.Count);
        }

        [TestMethod]
        public void Defer_writes_at_once_for_a_player_logging_out()
        {
            var player = BarePlayer();
            player.IsLoggingOut = true;
            TrialManager.OnlinePlayer = _ => player;

            var ran = 0;
            TrialRunLog.ResetForTests();                             // the live Defer
            TrialRunLog.Defer(player.Guid.Full, () => ran++);
            Assert.AreEqual(1, ran, "a logging-out player's queue is not trusted to drain");

            var src = Norm(PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/BalanceTrials/TrialRunLog.cs"), "private static void DeferToPlayer(uint guid, Action write)"));
            StringAssert.Contains(src, "if (player == null || player.IsLoggingOut) write(); else player.EnqueueAction(new ActionEventDelegate(write));");
        }

        [TestMethod]
        public void TickAt_runs_the_run_row_sweep()
        {
            StringAssert.StartsWith(Body(ManagerPath, "internal static void TickAt(double now)"), "{ try { TrialRunLog.SweepOverdue(now); }");
        }

        [TestMethod]
        public void TrialStarter_attaches_the_run_log_to_every_live_session()
        {
            var body = Body(StarterPath, "public static bool TryStart(Player player, string scenarioId, string archetypeId, out string refusal)");
            var attach = body.IndexOf("TrialRunLog.Attach(session);", StringComparison.Ordinal);
            Assert.IsTrue(attach > body.IndexOf("session = new TrialSession(", StringComparison.Ordinal), "after the session is built");
            Assert.IsTrue(attach < body.IndexOf("TrialManager.Register(session, landblock);", StringComparison.Ordinal), "before it is registered");
        }
    }
}
