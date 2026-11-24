using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Entity;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.Avatars.Gauntlet.GauntletFighterFixtures;

using DatSpellBase = ACE.DatLoader.Entity.SpellBase;
using DbSpell = ACE.Database.Models.World.Spell;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The Avatar fighter (plan Task 8, per the Task 1A result), dat-free:
    /// <list type="bullet">
    /// <item>the spike's criteria pins, ported: admission through the real ObjectMaint.AddVisibleTargets over bare PhysicsObjs
    /// (the call PhysicsObj.handle_visible_cells_non_player makes), the real CanDamage overrides, the Lifespan strip, the
    /// persistence rule, the leash and the death shaping;</item>
    /// <item>the damage intake cap, through the real TakeDamage and as a source pin on each of the three damage paths
    /// (each pin mutation-proved in the test itself, the GauntletDeathHookSiteTests shape);</item>
    /// <item>the heal and buff opt-in, through the real Player.IsInvalidTarget with a faked Spell;</item>
    /// <item>the run wiring, through a real GauntletRun and the real LiveGauntletFighterHooks behind its world seams.</item>
    /// </list>
    /// The player is a bare uninitialised Player (the type initialiser primed first), never written to by reflection. No
    /// PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class GauntletFighterTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => EnsureInitialized();

        // ---- IsMonster ----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void IsMonster_FalseForFighter_PhysicsReadsItAsACombatPet()
        {
            var fighter = Fighter();

            Assert.IsTrue(fighter.IsMonster, "fixture: the Creature-level flag is true for every attackable creature, pets included (Monster.cs SetMonsterState)");
            Assert.IsTrue(fighter.PhysicsObj.WeenieObj.IsCombatPet, "the subclass is a CombatPet to physics");
            Assert.IsFalse(fighter.PhysicsObj.WeenieObj.IsMonster, "WeenieObject.IsMonster must be false for a CombatPet subclass");
            Assert.IsFalse(fighter.PhysicsObj.WeenieObj.IsFactionMob);
        }

        // ---- (b) fighter -> monster ----------------------------------------------------------------------------------------

        [TestMethod]
        public void B_FighterAdmits_SameInstanceMonster_AndPicksItAsATarget()
        {
            var fighter = Fighter();
            var monster = Monster();

            fighter.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { monster.PhysicsObj });

            Assert.AreEqual(1, fighter.PhysicsObj.ObjMaint.GetVisibleTargetsCount(), "the fighter tracks the same-instance monster");
            Assert.IsFalse(fighter.NeverInitiates, "a never-initiates fighter would wait forever for an owner's side to engage");
            CollectionAssert.AreEqual(new[] { monster }, fighter.GetNearbyMonsters(fighter.NeverInitiates), "and its own target filter accepts it with no owner");
            Assert.IsTrue(fighter.CanDamage(monster), "the fighter's hits land on a monster");
        }

        // ---- (c) monster -> fighter ----------------------------------------------------------------------------------------

        [TestMethod]
        public void C_MonsterAdmits_Fighter_AndCanDamageIt()
        {
            var monster = Monster();
            var fighter = Fighter();

            monster.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { fighter.PhysicsObj });

            Assert.AreEqual(1, monster.PhysicsObj.ObjMaint.GetVisibleTargetsCount(), "a monster tracks the fighter as it tracks any combat pet");
            Assert.AreEqual(1, fighter.PhysicsObj.ObjMaint.GetVisibleTargetsCount(), "and the inverse is kept: the fighter tracks the monster");
            Assert.IsTrue(monster.CanDamage(fighter), "a monster's hits land on the fighter");
        }

        // ---- (d) player -> fighter ---------------------------------------------------------------------------------------

        [TestMethod]
        public void D_PlayerCanDamage_RefusesFighter_ButNotAMonster()
        {
            var player = BarePlayer();

            Assert.IsTrue(player.CanDamage(Monster()), "control: the same bare player may damage an ordinary monster");
            Assert.IsFalse(player.CanDamage(Fighter()), "Player.CanDamage refuses every CombatPet, so it refuses the fighter, whatever the player's PK status");
        }

        // ---- (e) a player's pet -> fighter --------------------------------------------------------------------------------

        [TestMethod]
        public void E_PlayersCombatPet_DoesNotAdmitFighter_NorDamageIt()
        {
            var pet = PlayerPet();
            var fighter = Fighter();
            var monster = Monster();

            pet.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { fighter.PhysicsObj, monster.PhysicsObj });

            Assert.AreEqual(1, pet.PhysicsObj.ObjMaint.GetVisibleTargetsCount(), "the pet tracks the monster (control) and not the fighter");
            CollectionAssert.AreEqual(new[] { monster }, pet.GetNearbyMonsters(), "the fighter is never a pet target");
            Assert.IsFalse(pet.CanDamage(fighter), "pet-on-pet damage is refused (Creature.CanDamage), so splash cannot reach it either");
            Assert.IsFalse(fighter.CanDamage(pet), "and the fighter's splash cannot reach a player's pet");
        }

        // ---- (f) instance boundary ----------------------------------------------------------------------------------------

        [TestMethod]
        public void F_DifferentInstanceMonster_NotAdmitted_EitherWay()
        {
            var fighter = Fighter(InstanceA);
            var otherMonster = Monster(InstanceB);

            fighter.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { otherMonster.PhysicsObj });
            otherMonster.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { fighter.PhysicsObj });

            Assert.AreEqual(0, fighter.PhysicsObj.ObjMaint.GetVisibleTargetsCount(), "the fighter never tracks a monster in another instance");
            Assert.AreEqual(0, otherMonster.PhysicsObj.ObjMaint.GetVisibleTargetsCount(), "and that monster never tracks the fighter");

            // control: the same pair in one instance is admitted, so the zero above is the instance gate
            var sameMonster = Monster(InstanceA);
            fighter.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { sameMonster.PhysicsObj });
            Assert.AreEqual(1, fighter.PhysicsObj.ObjMaint.GetVisibleTargetsCount());
        }

        // ---- (a) no owner ---------------------------------------------------------------------------------------------------

        [TestMethod]
        public void NoOwner_NoOwnerlostDestroy()
        {
            var fighter = Fighter();

            Assert.IsNull(fighter.P_PetOwner, "fixture: no owner");
            Assert.IsFalse(fighter.IsPassivePet, "Monster_Tick calls Pet.Tick (and so SlowTick) only for a passive pet; the fighter never reaches it");

            ((Pet)fighter).SlowTick(1000.0);

            Assert.IsNull(fighter.RemovalReason, "no ownerlost and no leash reason");
            Assert.IsFalse(fighter.IsDestroyed, "not destroyed");
        }

        [TestMethod]
        public void NoOwner_Control_PlainCombatPetSlowTick_SendsOwnerlost()
        {
            // proves the seam above reaches the owner-lost branch: a plain CombatPet driven the same way takes it
            var pet = PlayerPet();

            try
            {
                ((Pet)pet).SlowTick(1000.0);
            }
            catch (Exception)
            {
                // Destroy() on an object that was never in a world may throw past the line that matters
            }

            Assert.AreEqual("ownerlost", pet.RemovalReason);
        }

        [TestMethod]
        public void NoLifespan_SoHeartbeatNeverDeletesIt()
        {
            var fighter = Fighter();

            Assert.IsNull(fighter.Lifespan, "every retail CombatPet weenie carries Lifespan 43; WorldObject.Heartbeat deletes a spent one");
            Assert.IsFalse(fighter.IsLifespanSpent);
        }

        [TestMethod]
        public void FighterIsNeverPersistedToTheShard()
        {
            var fighter = Fighter();

            Assert.IsTrue(fighter.Guid.IsDynamic(), "fixture: a dynamic guid");
            Assert.AreEqual(WeenieType.CombatPet, fighter.WeenieType);
            Assert.IsFalse(fighter.IsDynamicThatShouldPersistToShard(), "WeenieType CombatPet is refused by the shard persistence rule");
        }

        // ---- leash rule -----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Leash_TargetDropped_WhenFighterOrTargetLeavesTheCircle()
        {
            const float r = 60.0f;

            Assert.IsFalse(GauntletAvatarFighter.ShouldDropTarget(10.0, 59.9, r));
            Assert.IsFalse(GauntletAvatarFighter.ShouldDropTarget(60.0, 60.0, r), "the edge is inside");
            Assert.IsTrue(GauntletAvatarFighter.ShouldDropTarget(60.1, 10.0, r), "the fighter chased out");
            Assert.IsTrue(GauntletAvatarFighter.ShouldDropTarget(10.0, 60.1, r), "the target is outside");
        }

        // ---- (g) death: no corpse, no loot, no player credit ----------------------------------------------------------------

        [TestMethod]
        public void G_FighterWeenie_NoCorpse_NoDeathTreasure_NoDroppableCreateList()
        {
            var donor = Donor(withCreateList: true);
            var shaped = GauntletAvatarFighter.BuildFighterWeenie(donor, GauntletAvatarFighter.FighterName);

            Assert.AreEqual(WeenieType.CombatPet, shaped.WeenieType);
            Assert.IsTrue(shaped.PropertiesBool[PropertyBool.NoCorpse]);
            Assert.IsFalse(shaped.PropertiesDID.ContainsKey(PropertyDataId.DeathTreasureType), "a NoCorpse death still drops DeathTreasure on the ground (CreateCorpse)");
            CollectionAssert.AreEqual(new uint[] { 1001 }, shaped.PropertiesCreateList.Select(c => c.WeenieClassId).ToList(),
                "only wield-only rows survive; Contain and Treasure rows drop on a NoCorpse death");
            Assert.IsFalse(shaped.PropertiesInt.ContainsKey(PropertyInt.Lifespan));
            Assert.IsFalse(shaped.PropertiesBool.ContainsKey(PropertyBool.SummonNeverInitiates));
            Assert.IsFalse(shaped.PropertiesBool.ContainsKey(PropertyBool.SummonCasterBrain));
            Assert.IsFalse(shaped.PropertiesFloat.ContainsKey(PropertyFloat.SummonTauntInterval));
            Assert.IsNull(shaped.PropertiesEmote, "a death emote could reward the last damager");
            Assert.AreEqual(GauntletAvatarFighter.FighterName, shaped.PropertiesString[PropertyString.Name]);

            // the donor is the shared cached weenie in production: it must come out untouched
            Assert.AreEqual(43, donor.PropertiesInt[PropertyInt.Lifespan]);
            Assert.AreEqual(4, donor.PropertiesCreateList.Count);
            Assert.IsFalse(donor.PropertiesBool.ContainsKey(PropertyBool.AllegianceGauntletFighter));

            Assert.IsTrue(Fighter().NoCorpse, "the constructed fighter has NoCorpse whatever the weenie says");
        }

        [TestMethod]
        public void G_FighterKills_CreditNoPlayer()
        {
            var fighter = Fighter();
            var info = new DamageHistoryInfo(fighter, 100.0f);

            Assert.IsNull(info.PetOwner, "no owner, so no PetOwner reference");
            Assert.IsNull(info.TryGetPetOwner());
            Assert.AreSame(fighter, info.TryGetPetOwnerOrAttacker(), "every kill hook that resolves a player through the pet owner gets the fighter, not a player");
            Assert.IsNotInstanceOfType(info.TryGetPetOwnerOrAttacker(), typeof(Player));
            Assert.IsFalse(info.IsPlayer);
            Assert.AreSame(info, DamageHistoryInfo.ResolvePetOwnerAsKiller(info), "rare and treasure-map eligibility sees no player");

            // control: a player's pet does carry its owner, so the null above is the missing owner
            var pet = PlayerPet();
            pet.P_PetOwner = BarePlayer();
            Assert.IsNotNull(new DamageHistoryInfo(pet, 1.0f).PetOwner);
        }

        // ---- the damage intake cap ----------------------------------------------------------------------------------------------

        private static GauntletFighterStamp CapStamp(uint maxHealth) => new GauntletFighterStamp
        {
            Level = 200,
            MaxHealth = maxHealth,
            MinDeathSeconds = 20,
            BurstSeconds = 5,
        };

        [TestMethod]
        public void TakeDamage_CappedByMinDeathSeconds()
        {
            // max 1000 over 20 s: 50 per second, a 5 s burst of 250
            var fighter = Fighter(health: 1000);
            fighter.ApplyStamp(CapStamp(1000), fullHealth: true);
            Assert.AreEqual(1000u, fighter.Health.MaxValue, "fixture: the stamp set max health");

            Assert.AreEqual(250f, fighter.CapIncomingHit(10_000f, 1000.0), "the first hit takes the whole burst and no more");
            Assert.AreEqual(50f, fighter.CapIncomingHit(10_000f, 1001.0), "one second later: one second of refill");
            Assert.AreEqual(30f, fighter.CapIncomingHit(30f, 1100.0), "a hit under the bucket lands in full");
            Assert.AreEqual(220f, fighter.CapIncomingHit(10_000f, 1100.0), "the refill never passes the burst");
            Assert.AreEqual(-40f, fighter.CapIncomingHit(-40f, 1100.0), "a heal passes through");

            // the swarm: 200 hits of 1e6 inside 10 s take at most burst + 10 s of rate
            var other = Fighter(health: 1000);
            other.ApplyStamp(CapStamp(1000), fullHealth: true);
            var total = 0.0;
            for (var i = 0; i < 200; i++)
                total += other.CapIncomingHit(1e6f, 5000.0 + i * 0.05);
            Assert.AreEqual(250 + 50 * 9.95, total, 1.0, "max health over min_death_seconds at the fastest");

            // the real intake: Creature.TakeDamage (the CombatPet.TakeDamage slot) on a freshly stamped fighter
            var hit = Fighter(health: 1000);
            hit.ApplyStamp(CapStamp(1000), fullHealth: true);
            var dealt = hit.TakeDamage(Monster(), DamageType.Slash, 100_000f);
            Assert.AreEqual(250u, dealt, "a 100000 hit lands as the 250 burst through TakeDamage");
            Assert.AreEqual(750u, hit.Health.Current);
            Assert.IsFalse(hit.IsDead);

            // control: an ordinary creature is untouched by the hook
            Assert.AreEqual(100_000f, GauntletAvatarFighter.CapGauntletFighterHit(Monster(), 100_000f));
            Assert.AreEqual(100_000f, GauntletAvatarFighter.CapGauntletFighterHit(PlayerPet(), 100_000f), "a player's combat pet is untouched");
            Assert.AreEqual(100_000f, GauntletAvatarFighter.CapGauntletFighterHit(null, 100_000f));
        }

        [TestMethod]
        public void Restamp_RefillsTheCap()
        {
            var fighter = Fighter(health: 1000);
            fighter.ApplyStamp(CapStamp(1000), fullHealth: true);

            Assert.AreEqual(250f, fighter.CapIncomingHit(10_000f, 1000.0));
            Assert.AreEqual(0f, fighter.CapIncomingHit(10_000f, 1000.0), "the bucket is empty");

            fighter.ApplyStamp(CapStamp(1000));
            Assert.AreEqual(250f, fighter.CapIncomingHit(10_000f, 1000.0), "a restamp refills it");
        }

        // ---- the three damage paths (source pins, mutation-proved here) -------------------------------------------------------

        private const string HookType = "ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter.";

        internal sealed record HookSite(string Path, string Signature, string Before, string Hook, string After);

        internal static readonly HookSite TakeDamageSite = new HookSite(
            "Source/ACE.Server/WorldObjects/Monster_Combat.cs",
            "public virtual uint TakeDamage(WorldObject source, DamageType damageType, float amount, bool crit = false, IncomingDamageOrigin origin = IncomingDamageOrigin.Secondary)",
            "amount = ACE.Server.WorldEvents.WorldEventHooks.ScaleHoldKnightHit(this, source, amount);",
            "amount = " + HookType + "CapGauntletFighterHit(this, amount);",
            "var tryDamage = (int)Math.Round(amount);");

        internal static readonly HookSite ProjectileSite = new HookSite(
            "Source/ACE.Server/WorldObjects/SpellProjectile.cs",
            "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)",
            "damage = ACE.Server.WorldEvents.WorldEventHooks.ScaleHoldKnightHit(target, ProjectileSource, damage);",
            "damage = " + HookType + "CapGauntletFighterHit(target, damage);",
            "if (damage != beforeHold)");

        internal static readonly HookSite HarmSite = new HookSite(
            "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs",
            "private void HandleCastSpell_Boost(Spell spell, Creature targetCreature, bool fromProc = false)",
            "holdHarm = ACE.Server.WorldEvents.WorldEventHooks.ScaleHoldKnightHit(targetCreature, this, holdHarm);",
            "holdHarm = " + HookType + "CapGauntletFighterHit(targetCreature, holdHarm);",
            "if (holdHarm != -tryBoost)");

        internal static readonly HookSite SpellTargetSite = new HookSite(
            "Source/ACE.Server/WorldObjects/Player_Magic.cs",
            "protected bool IsInvalidTarget(Spell spell, WorldObject target)",
            "return holdInvalid;",
            "if (" + HookType + "IsInvalidFighterSpellTarget(targetCreature, spell, out var fighterInvalid))\n                return fighterInvalid;",
            "if (targetCreature != null && targetPlayer == null && spell.IsBeneficial)");

        /// <summary>The first fault of a site in <paramref name="source"/>, or null: the hook once, after Before and before After.</summary>
        internal static string SiteFault(string source, HookSite site)
        {
            var body = PooledLootSourceText.MethodBody(source, site.Signature);

            if (Regex.Matches(body, Regex.Escape(site.Hook)).Count != 1)
                return "the hook is not in the method exactly once";

            if (Regex.Matches(source, Regex.Escape(HookType)).Count != 1)
                return "the fighter hook appears elsewhere in the file";

            var before = body.IndexOf(site.Before, StringComparison.Ordinal);
            var hook = body.IndexOf(site.Hook, StringComparison.Ordinal);
            var after = body.IndexOf(site.After, StringComparison.Ordinal);

            if (before < 0 || after < 0)
                return "an anchor line is missing";

            if (hook < before)
                return "the hook runs before the Hold knight's line";

            if (hook > after)
                return "the hook runs after the damage is used";

            return null;
        }

        private static void AssertSiteAndMutations(HookSite site)
        {
            var source = PooledLootSourceText.Read(site.Path);

            Assert.IsNull(SiteFault(source, site), $"{site.Path}: {SiteFault(source, site)}");

            // mutation 1: the hook removed
            Assert.IsNotNull(SiteFault(source.Replace(site.Hook, "// removed"), site), "removing the hook must fault");

            // mutation 2: the hook moved past the line that uses the damage
            var moved = source.Replace(site.Hook, "// moved").Replace(site.After, site.After + "\n" + site.Hook);
            Assert.IsNotNull(SiteFault(moved, site), "moving the hook after the damage is used must fault");

            // mutation 3: the hook moved ahead of the knight's line
            var early = source.Replace(site.Hook, "// early").Replace(site.Before, site.Hook + "\n" + site.Before);
            Assert.IsNotNull(SiteFault(early, site), "moving the hook ahead of the knight's line must fault");

            // mutation 4: the hook pointed at another argument
            var wrongArg = source.Replace(site.Hook, site.Hook.Replace("(this,", "(source,").Replace("(target,", "(ProjectileSource,").Replace("(targetCreature,", "(this,"));
            Assert.IsNotNull(SiteFault(wrongArg, site), "a hook on another argument must fault");
        }

        [TestMethod]
        public void DamagePath1_TakeDamage_CallsTheCap() => AssertSiteAndMutations(TakeDamageSite);

        [TestMethod]
        public void DamagePath2_SpellProjectile_CallsTheCap() => AssertSiteAndMutations(ProjectileSite);

        [TestMethod]
        public void DamagePath3_LifeMagicHarm_CallsTheCap() => AssertSiteAndMutations(HarmSite);

        [TestMethod]
        public void SpellTarget_HookSite_IsAheadOfTheBeneficialRefusal() => AssertSiteAndMutations(SpellTargetSite);

        // ---- members' heals and buffs ----------------------------------------------------------------------------------------------

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "IsInvalidTarget")]
        private static extern bool IsInvalidTarget(Player player, Spell spell, WorldObject target);

        /// <summary>A faked spell (no DatManager): Life Magic, targeted at a creature, beneficial or not.</summary>
        private static Spell FakeSpell(bool beneficial)
        {
            var spellBase = (DatSpellBase)RuntimeHelpers.GetUninitializedObject(typeof(DatSpellBase));

            SetBackingField(spellBase, "Name", beneficial ? "Test Heal Other" : "Test Harm Other");
            SetBackingField(spellBase, "School", MagicSchool.LifeMagic);
            SetBackingField(spellBase, "Category", SpellCategory.Undef);
            SetBackingField(spellBase, "MetaSpellId", 9999u);
            SetBackingField(spellBase, "MetaSpellType", SpellType.Boost);
            SetBackingField(spellBase, "Bitfield", beneficial ? (uint)SpellFlags.Beneficial : 0u);
            SetBackingField(spellBase, "NonComponentTargetType", (uint)ItemType.Creature);

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;
            spell._spell = new DbSpell { Boost = beneficial ? 50 : -50, BoostVariance = 0, DamageType = (int)DamageType.Health };

            return spell;
        }

        private static void SetBackingField(object target, string propertyName, object value)
        {
            var field = target.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{target.GetType().Name}.<{propertyName}>k__BackingField was not found by reflection");
            field.SetValue(target, value);
        }

        [TestMethod]
        public void Fighter_AcceptsHealAndBuff_InLiveCopy()
        {
            var h = new FighterRunHarness();
            var run = h.Start();
            h.Advance(0.5);

            Assert.AreEqual(GauntletState.Staging, run.State, "fixture: the copy loaded");
            Assert.AreEqual(1, h.Placed.Count, "the fighter was placed at Staging");
            var fighter = h.Placed[0];

            var player = BarePlayer();
            var heal = FakeSpell(beneficial: true);

            Assert.IsTrue(fighter.AcceptsBeneficialSpells, "Staging is live");
            Assert.IsFalse(IsInvalidTarget(player, heal, fighter), "a member's heal or buff on the fighter is valid in Staging");

            h.Advance(h.Tuning.StagingSeconds + 0.5);
            Assert.AreEqual(GauntletState.Active, run.State);
            Assert.IsFalse(IsInvalidTarget(player, heal, fighter), "and in Active");

            Assert.IsTrue(IsInvalidTarget(player, FakeSpell(beneficial: false), fighter), "a harmful spell on the fighter is always refused");

            // control: the same beneficial spell on an ordinary monster is refused by the existing rule
            Assert.IsTrue(IsInvalidTarget(player, heal, Monster()), "beneficial on a non-player creature stays refused");

            // the pure core
            Assert.IsFalse(GauntletAvatarFighter.IsInvalidFighterSpell(acceptsBeneficial: true, isBeneficial: true));
            Assert.IsTrue(GauntletAvatarFighter.IsInvalidFighterSpell(acceptsBeneficial: true, isBeneficial: false));

            // no contact credit: a beneficial cast never reaches the contact hook, and the fighter is no Gauntlet creature,
            // so even a hit recorded against it reports nothing
            Assert.IsNull(fighter.P_Gauntlet, "the fighter carries no Gauntlet back-reference");
            GauntletHooks.OnContact(fighter, player);
            Assert.AreEqual(0, run.FoughtCount, "nothing credited");
        }

        [TestMethod]
        public void Fighter_RefusesBeneficial_OutsideCopy()
        {
            var player = BarePlayer();
            var heal = FakeSpell(beneficial: true);

            var unbound = Fighter();
            Assert.IsNull(unbound.Owner);
            Assert.IsFalse(unbound.AcceptsBeneficialSpells);
            Assert.IsTrue(IsInvalidTarget(player, heal, unbound), "a fighter with no run refuses a heal");

            Assert.IsTrue(GauntletAvatarFighter.IsInvalidFighterSpellTarget(unbound, heal, out var invalid), "decided by the fighter hook");
            Assert.IsTrue(invalid);
            Assert.IsFalse(GauntletAvatarFighter.IsInvalidFighterSpellTarget(Monster(), heal, out _), "any other target is decided as before");
            Assert.IsTrue(GauntletAvatarFighter.IsInvalidFighterSpell(acceptsBeneficial: false, isBeneficial: true));

            // a run that is over: the fighter refuses once the run leaves its live states, and once removed
            var h = new FighterRunHarness();
            var run = h.StartActive();
            var fighter = h.Placed.Single();
            Assert.IsFalse(IsInvalidTarget(player, heal, fighter), "fixture: live");

            h.Manager.AbortRun(FighterRunHarness.AvatarId, refund: false);
            h.Advance(0.5);
            Assert.IsTrue(GauntletStateMachine.IsTerminal(run.State) || run.State == GauntletState.Ending || run.State == GauntletState.Rewarding, $"fixture: the run is over ({run.State})");
            Assert.IsFalse(fighter.AcceptsBeneficialSpells);
            Assert.IsTrue(IsInvalidTarget(player, heal, fighter), "after the run, a heal is refused");
        }

        [TestMethod]
        public void Slot_IsLiveCopy_OnlyInStagingActiveIntermission()
        {
            var h = new FighterRunHarness();
            var run = h.Start();
            h.Advance(0.5);

            Assert.AreEqual(GauntletState.Staging, run.State);
            Assert.IsTrue(new LiveGauntletFighterHooks.Slot(run).IsLiveCopy, "control: Staging is live");

            h.Advance(h.Tuning.StagingSeconds + 0.5);
            Assert.AreEqual(GauntletState.Active, run.State);
            Assert.IsTrue(new LiveGauntletFighterHooks.Slot(run).IsLiveCopy, "control: Active is live");

            var removed = new LiveGauntletFighterHooks.Slot(run) { Removed = true };
            Assert.IsFalse(removed.IsLiveCopy, "a removed slot is never live");

            h.Manager.AbortRun(FighterRunHarness.AvatarId, refund: false);
            Assert.IsFalse(new LiveGauntletFighterHooks.Slot(run).IsLiveCopy, $"a run that is over is not live ({run.State})");
        }

        private sealed class RecordingOwner : IGauntletFighterOwner
        {
            public int Reports;
            public bool IsLiveCopy => true;
            public void OnFighterDied(GauntletAvatarFighter fighter) => Reports++;
        }

        [TestMethod]
        public void ReportDeathOnce_ReportsExactlyOnce()
        {
            var owner = new RecordingOwner();
            var fighter = Fighter();
            fighter.Owner = owner;

            Assert.IsTrue(fighter.ReportDeathOnce());
            Assert.IsFalse(fighter.ReportDeathOnce(), "a second death entry reports nothing");
            Assert.AreEqual(1, owner.Reports);

            var unbound = Fighter();
            Assert.IsFalse(unbound.ReportDeathOnce(), "no owner, nothing reported");
        }

        [TestMethod]
        public void Restamp_ClearsBuffs()
        {
            var h = new FighterRunHarness();
            var run = h.Start();
            h.Advance(0.5);
            var fighter = h.Placed.Single();

            Enchant(fighter, 2001, beneficial: true);
            Enchant(fighter, 2002, beneficial: true);
            Enchant(fighter, 2003, beneficial: false);
            Enchant(fighter, 2004, beneficial: true, duration: -1);
            Assert.IsTrue(HasEnchantment(fighter, 2001), "fixture: buffed in Staging");

            h.Advance(h.Tuning.StagingSeconds + 0.5);
            Assert.AreEqual(GauntletState.Active, run.State, "wave 1 started");

            // Staging stamped once at spawn: wave 1's stats arrive at its start WITHOUT clearing what members cast before it.
            Assert.IsTrue(HasEnchantment(fighter, 2001), "a buff cast during Staging carries into wave 1");
            Assert.IsTrue(HasEnchantment(fighter, 2002));

            // the wave 1 stats it received (the consumer pin: what the fighter received)
            var t = h.Tuning;
            Assert.AreEqual(new GauntletRules(t).LevelOf(1), fighter.Level, "the level shown is L(1)");
            Assert.AreEqual((uint)(2000 * (t.FighterHealthBase + t.FighterHealthPerLevel * h.AvatarLevel)), fighter.Health.MaxValue, "HealthCurveTarget x (10 + 0.4 A) at the live A");
            Assert.AreEqual(fighter.Health.MaxValue, fighter.Health.Current, "full health kept full");

            var offense = t.FighterOffenseBase + t.FighterOffensePerLevel * h.AvatarLevel;
            Assert.AreEqual(DungeonSkill(400, offense), fighter.GetCreatureSkill(Skill.LightWeapons, false).InitLevel, "the attack skill target");
            Assert.AreEqual(DungeonSkill(300, offense), fighter.GetCreatureSkill(Skill.MeleeDefense, false).InitLevel, "the defense skill target");
            Assert.IsNull(fighter.GetCreatureSkill(Skill.MissileDefense, false), "no skill is added that the weenie does not author");
            Assert.AreEqual(DungeonSkill(200, offense), fighter.Attributes[PropertyAttribute.Strength].StartingValue);

            var hit = (int)Math.Round(50 * (t.FighterDamageBase + t.FighterDamagePerLevel * h.AvatarLevel));
            Assert.AreEqual(hit, fighter.Biota.PropertiesBodyPart.Values.Max(p => p.DVal), "the best body part hits for PerHitMedian x the damage ratio");

            // Wave 1 cleared: the Intermission start restamps from wave 2's plan and clears the members' buffs.
            KillWave(h, run);
            Assert.AreEqual(GauntletState.Intermission, run.State, "fixture: wave 1 cleared");
            Assert.IsFalse(HasEnchantment(fighter, 2001), "a member's buff is cleared at the Intermission restamp");
            Assert.IsFalse(HasEnchantment(fighter, 2002));
            Assert.IsTrue(HasEnchantment(fighter, 2003), "a monster's debuff stays");
            Assert.IsTrue(HasEnchantment(fighter, 2004), "an item spell (duration -1) stays");
            Assert.AreEqual(new GauntletRules(t).LevelOf(2), fighter.Level, "stamped from wave 2's plan at the break's start");

            // A buff cast during the break carries into wave 2.
            Enchant(fighter, 2005, beneficial: true);
            h.Advance(t.IntermissionSeconds + 0.5);
            Assert.AreEqual(2, run.CurrentWave, "fixture: wave 2 started");
            Assert.IsTrue(HasEnchantment(fighter, 2005), "a buff cast in the break is kept when the wave starts");
        }

        [TestMethod]
        public void RefusedWave_MidWaveReplan_KeepsBuffs()
        {
            var wave2Calls = 0;
            var h = new FighterRunHarness();
            h.Plans = w =>
            {
                if (w != 2 || ++wave2Calls > 2)
                    return FighterRunHarness.DefaultPlan(w);

                var refused = FighterRunHarness.DefaultPlan(w);
                return new GauntletWavePlan { Wave = w, Level = refused.Level, TimeLimitSeconds = refused.TimeLimitSeconds, Plan = refused.Plan, Refusal = "picked none" };
            };

            var run = h.StartActive();
            var fighter = h.Placed.Single();
            KillWave(h, run);
            h.Advance(h.Tuning.IntermissionSeconds + 0.5);
            Assert.AreEqual(2, run.CurrentWave, "fixture: wave 2 started, refused (pre-plan and the start both refused)");

            Enchant(fighter, 2101, beneficial: true);
            h.Advance(GauntletRun.ReplanSeconds + 1);
            Assert.IsTrue(wave2Calls >= 3, "fixture: the mid-wave re-plan succeeded");

            Assert.IsTrue(HasEnchantment(fighter, 2101), "a mid-wave re-plan never clears buffs again");
            Assert.AreEqual(new GauntletRules(h.Tuning).LevelOf(2), fighter.Level, "it still carries wave 2's stats");
        }

        private static uint DungeonSkill(uint median, double ratio) => (uint)Math.Round(median * ratio, MidpointRounding.AwayFromZero);

        [TestMethod]
        public void Restamp_ReadsTheLiveAvatarLevel_AndKeepsTheHealthFraction()
        {
            var h = new FighterRunHarness { AvatarLevel = 0 };
            var run = h.StartActive();
            var fighter = h.Placed.Single();
            var t = h.Tuning;

            Assert.AreEqual((uint)(2000 * t.FighterHealthBase), fighter.Health.MaxValue, "A = 0 at wave 1");

            // half health, then the Avatar levels up mid-run: it takes effect at the next restamp, at the same fraction
            fighter.Health.Current = fighter.Health.MaxValue / 2;
            h.AvatarLevel = 50;

            KillWave(h, run);
            h.Advance(t.IntermissionSeconds + 0.5);
            Assert.AreEqual(2, run.CurrentWave, "fixture: wave 2 started");

            Assert.AreEqual((uint)(2000 * (t.FighterHealthBase + t.FighterHealthPerLevel * 50)), fighter.Health.MaxValue, "the level-up applied at the restamp");
            Assert.AreEqual(fighter.Health.MaxValue / 2, fighter.Health.Current, "the health fraction is kept");
            Assert.AreEqual(new GauntletRules(t).LevelOf(2), fighter.Level);
        }

        private static void KillWave(FighterRunHarness h, GauntletRun run)
        {
            // the harness spawner's creatures are never alive in its world: let the stream finish, then report each death
            h.Advance(5);
            foreach (var guid in Enumerable.Range(0, 64).Select(i => (uint)(0x7F2E0001 + i)))
                run.NoteCreatureDied(guid, run.CurrentWave);
            h.Advance(1.5);
        }

        // ---- death and respawn ----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Death_RespawnsAfterTimer_RunContinues()
        {
            var h = new FighterRunHarness();
            var run = h.StartActive();
            var first = h.Placed.Single();
            var stampBefore = first.Stamp;

            // the real death entry point: CombatPet.OnDeath, through the fighter's override
            first.OnDeath(null, DamageType.Slash);
            first.OnDeath(null, DamageType.Slash);

            Assert.AreEqual(1, h.Lines.Count(l => l.StartsWith("The Avatar has fallen.")), "reported once");
            Assert.IsNull(h.Hooks.SlotFor(run).Fighter, "the slot lets the dead fighter go");
            Assert.AreEqual(GauntletState.Active, run.State, "a fighter death never ends the run");

            h.Advance(h.Tuning.FighterRespawnSeconds - 1);
            Assert.AreEqual(1, h.Placed.Count, "held out for the respawn timer");
            Assert.AreEqual(GauntletState.Active, run.State);

            h.Advance(1.5);
            Assert.AreEqual(2, h.Placed.Count, "a fresh fighter after allegiance_gauntlet_fighter_respawn_seconds");
            Assert.IsTrue(h.Lines.Contains("The Avatar rejoins the fight."));

            var second = h.Placed[1];
            Assert.AreNotSame(first, second);
            Assert.AreSame(second, h.Hooks.SlotFor(run).Fighter);
            Assert.AreSame(stampBefore, second.Stamp, "it comes back with the wave's stamp");
            Assert.AreEqual(second.Health.MaxValue, second.Health.Current, "at full health");
            Assert.AreEqual(run.CentrePosition.PositionX, second.Location.PositionX, "at the centre");
            Assert.AreEqual(run.Instance, second.Location.Instance, "in the run's copy");
            Assert.IsNull(second.Anchor, "no leash in the Gauntlet: it follows its leader (owner ruling 2026-11-23)");
            Assert.IsNotNull(second.Participants, "it is handed the run's members to choose a leader from");
            Assert.IsTrue(second.AcceptsBeneficialSpells);
        }

        [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "<IsDestroyed>k__BackingField")]
        private static extern ref bool IsDestroyedField(WorldObject wo);

        [TestMethod]
        public void PlacedFighter_NeverRots()
        {
            var h = new FighterRunHarness();
            h.Start();
            h.Advance(0.5);
            var fighter = h.Placed.Single();

            Assert.IsTrue(fighter.Guid.IsDynamic(), "fixture: a dynamic guid, the case the heartbeat rots");
            Assert.IsFalse(fighter.IsDecayable(), "a placed fighter is never decayable");
            Assert.AreEqual(-1.0, fighter.TimeToRot);
        }

        [TestMethod]
        public void DestroyedNotDead_IsReplaced_DeadWaitsForTheTimer()
        {
            var h = new FighterRunHarness();
            var run = h.StartActive();
            var first = h.Placed.Single();

            // A heartbeat rot or an unload: a bare destroy, no death reported.
            IsDestroyedField(first) = true;
            h.Advance(GauntletRun.FighterCheckSeconds + 0.5);

            Assert.AreEqual(2, h.Placed.Count, "a fighter that left without dying is replaced");
            Assert.AreSame(h.Placed[1], h.Hooks.SlotFor(run).Fighter);
            Assert.IsFalse(h.Lines.Any(l => l.StartsWith("The Avatar has fallen.")), "no death is reported for it");

            // Control: a real death waits for the respawn timer.
            h.Placed[1].OnDeath(null, DamageType.Slash);
            h.Advance(GauntletRun.FighterCheckSeconds * 2);
            Assert.AreEqual(2, h.Placed.Count, "a dead fighter is not replaced early");
        }

        /// <summary>Half-second steps until <paramref name="done"/>, failing past <paramref name="limit"/> seconds.</summary>
        private static void AdvanceUntil(FighterRunHarness h, Func<bool> done, double limit = 20)
        {
            for (var t = 0.0; !done(); t += 0.5)
            {
                Assert.IsTrue(t < limit, "fixture: the awaited step never came");
                h.Advance(0.5);
            }
        }

        [TestMethod]
        public void FailedPlacement_IsRetried_ForALostFighterASpawnAndARespawn()
        {
            // A lost fighter whose replacement fails once.
            var h = new FighterRunHarness();
            var run = h.StartActive();
            var first = h.Placed.Single();

            IsDestroyedField(first) = true;
            h.FailPlacements = 1;
            AdvanceUntil(h, () => h.FailPlacements == 0);
            Assert.AreEqual(1, h.Placed.Count, "fixture: the first replacement was refused");
            Assert.AreSame(first, h.Hooks.SlotFor(run).Fighter, "the lost fighter is kept, so the next check tries again");

            h.Advance(GauntletRun.FighterCheckSeconds + 0.5);
            Assert.AreEqual(2, h.Placed.Count, "the retry placed a new fighter");
            Assert.AreSame(h.Placed[1], h.Hooks.SlotFor(run).Fighter);

            // A respawn whose placement fails once.
            h.Placed[1].OnDeath(null, DamageType.Slash);
            h.FailPlacements = 1;
            AdvanceUntil(h, () => h.FailPlacements == 0, h.Tuning.FighterRespawnSeconds + 10);
            Assert.AreEqual(2, h.Placed.Count, "fixture: the respawn placement was refused");
            Assert.IsNull(h.Hooks.SlotFor(run).Fighter);

            h.Advance(GauntletRun.FighterCheckSeconds + 0.5);
            Assert.AreEqual(3, h.Placed.Count, "the respawn is retried");
            Assert.AreSame(h.Placed[2], h.Hooks.SlotFor(run).Fighter);

            h.Advance(GauntletRun.FighterCheckSeconds * 3);
            Assert.AreEqual(3, h.Placed.Count, "a standing fighter is left alone");
            Assert.AreEqual(0, h.Hooks.SlotFor(run).PlaceFailures, "a success ends the failure streak");

            // A Staging spawn whose placement fails once.
            var s = new FighterRunHarness { FailPlacements = 1 };
            var staged = s.Start();
            AdvanceUntil(s, () => s.FailPlacements == 0);
            Assert.AreEqual(0, s.Placed.Count, "fixture: the spawn was refused");
            s.Advance(GauntletRun.FighterCheckSeconds + 0.5);
            Assert.AreEqual(1, s.Placed.Count, "the spawn is retried");
            Assert.AreSame(s.Placed[0], s.Hooks.SlotFor(staged).Fighter);
        }

        [TestMethod]
        public void FailedPlacement_WarnsOnce_ThenDebug_UntilASuccess()
        {
            var h = new FighterRunHarness();
            var run = h.StartActive();
            var slot = h.Hooks.SlotFor(run);
            var first = h.Placed.Single();

            IsDestroyedField(first) = true;
            h.FailPlacements = 3;
            AdvanceUntil(h, () => h.FailPlacements == 0, 60);
            Assert.AreEqual(3, slot.PlaceFailures, "three failures in a row counted");

            AdvanceUntil(h, () => h.Placed.Count == 2, 20);
            Assert.AreEqual(0, slot.PlaceFailures, "reset by the success");

            // The level choice itself: Warn on the first of a streak only.
            var fresh = new LiveGauntletFighterHooks.Slot(run);
            Assert.IsTrue(LiveGauntletFighterHooks.LogPlaceFailure(fresh, "test: first"), "the first failure warns");
            Assert.IsFalse(LiveGauntletFighterHooks.LogPlaceFailure(fresh, "test: second"), "the next is Debug");
            Assert.IsFalse(LiveGauntletFighterHooks.LogPlaceFailure(fresh, "test: third", new InvalidOperationException("test")), "an exception rides along");

            // Every failure path in PlaceNew goes through it.
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/LiveGauntletFighterHooks.cs"),
                "internal GauntletAvatarFighter PlaceNew(Slot slot)");
            Assert.AreEqual(4, System.Text.RegularExpressions.Regex.Matches(body, "LogPlaceFailure\\(slot, ").Count, "the four failure paths");
            Assert.IsFalse(body.Contains("log.Warn(", StringComparison.Ordinal) || body.Contains("log.Error(", StringComparison.Ordinal), "no failure path logs at Warn or Error directly");
            Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(body, "LogPlaceFailure\\(slot, [^;]*, ex\\);").Count, "both exception paths hand over the exception, for its stack trace");

            var helper = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/LiveGauntletFighterHooks.cs"),
                "internal static bool LogPlaceFailure(Slot slot, string message, Exception ex = null)");
            StringAssert.Contains(helper, "log.Warn(message, ex);");
            StringAssert.Contains(helper, "log.Debug(message, ex);");
        }

        [TestMethod]
        public void Remove_TakesTheFighterOut_WithoutReportingADeath()
        {
            var h = new FighterRunHarness();
            var run = h.StartActive();
            var fighter = h.Placed.Single();

            h.Hooks.Remove(run);
            h.Drain();

            CollectionAssert.Contains(h.Destroyed, fighter, "taken out of the world");
            Assert.IsNull(fighter.Owner, "unbound first");
            Assert.IsNull(h.Hooks.SlotFor(run));

            fighter.OnDeath(null, DamageType.Slash);
            Assert.IsFalse(h.Lines.Any(l => l.StartsWith("The Avatar has fallen.")), "a removed fighter's death is never reported");

            h.Hooks.Respawn(run);
            h.Hooks.Restamp(run, FighterRunHarness.DefaultPlan(2), clearBuffs: true);
            h.Drain();
            Assert.AreEqual(1, h.Placed.Count, "nothing comes back after Remove");
        }
    }
}
