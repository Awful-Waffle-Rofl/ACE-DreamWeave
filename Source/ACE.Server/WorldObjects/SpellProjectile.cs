using System;
using System.Numerics;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Rules;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects.Entity;

using log4net;

namespace ACE.Server.WorldObjects
{
    public class SpellProjectile : WorldObject
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public Spell Spell;
        public ProjectileSpellType SpellType { get; set; }

        public Position SpawnPos { get; set; }
        public float DistanceToTarget { get; set; }
        public uint LifeProjectileDamage { get; set; }

        public SpellProjectileInfo Info { get; set; }

        /// <summary>
        /// Only set to true when this spell was launched by using the built-in spell on a caster
        /// </summary>
        public bool IsWeaponSpell { get; set; }

        /// <summary>
        /// If a spell projectile is from a proc source,
        /// make sure there is no attempt to re-proc again when the spell projectile hits
        /// </summary>
        public bool FromProc { get; set; }

        /// <summary>
        /// Class ability "Spell AOE": TRUE when this projectile is a secondary copy radiated from a
        /// struck target to a neighbor. Such children never re-trigger the blast (no cascade) and
        /// never fire item procs (they also carry FromProc).
        /// </summary>
        public bool IsClassAbilityAoeChild { get; set; }

        /// <summary>
        /// TRUE when this projectile was itself spawned by a class ability as a SECONDARY copy - today only a
        /// Spell AOE radiated copy. The general no-cascade guard: Spell AOE, the projectile-spawning spell-hit
        /// skill, skips a projectile carrying this flag so a spawned copy never spawns further copies.
        /// Non-spawning effects (Elemental Rend, Pinning Shot, Dispelling Edge) deliberately ignore it, so AOE
        /// copies can still apply their effect.
        ///
        /// An Echo Cast wisp's projectile does not carry this flag: a wisp cast is the player's own first-hand
        /// cast, so Spell AOE radiates off it. Echo Cast no longer reacts to landed hits at all (the wisp rework
        /// retired the spell-hit recast), so no "never echo again" flag is needed; what follows a wisp cast to
        /// impact time is <see cref="IsEchoWispCast"/>.
        /// </summary>
        public bool IsClassAbilitySpawned { get; set; }

        /// <summary>
        /// TRUE when this projectile was launched by an Echo Cast wisp cast (Player.CastAsEchoWisp), or is a
        /// Spell AOE child radiated off one. Stamped in WorldObject_Magic.LaunchSpellProjectiles BEFORE
        /// AddObject, from the Player.EchoWispCast latch, and carries the wisp rules to IMPACT time, when the
        /// latch is no longer armed:
        ///  - Blood Price 1.0 and the Adrenaline window left unspent (GetClassAbilitySpellDamageMod);
        ///  - no Blood Charge grant (<see cref="GrantsLifeProjectileCharge"/>);
        ///  - it lands NOTHING on a player (<see cref="EchoWispBlocksHit"/>): rings and walls are target-blind,
        ///    and class abilities never reach PvP.
        ///
        /// NOT a derived emission: a wisp cast is the player's own first-hand cast, so FromProc stays false,
        /// ProjectileSource stays the player and <see cref="IncomingDamageOrigin"/> deliberately ignores this.
        /// </summary>
        public bool IsEchoWispCast { get; set; }

        /// <summary>
        /// The PvP guard for an Echo Cast wisp's projectile: TRUE when it must land nothing on what it touched.
        /// Pure so the rule is unit-testable.
        /// </summary>
        public static bool EchoWispBlocksHit(bool isEchoWispCast, bool targetIsPlayer)
        {
            return isEchoWispCast && targetIsPlayer;
        }

        /// <summary>
        /// The Echo Cast wisp mark a Spell AOE child inherits from the projectile it radiated off, so the child of
        /// a wisp cast follows the same wisp rules (no Blood Charge, no Adrenaline spend, never lands on a player).
        /// Public and static so the inheritance rule is unit-testable on two plain projectiles.
        /// </summary>
        public static void InheritEchoWispMark(SpellProjectile child, SpellProjectile parent)
        {
            if (child == null || parent == null)
                return;

            child.IsEchoWispCast = parent.IsEchoWispCast;
        }

        /// <summary>
        /// The launching cast's Blood Mage per-cast terms (Blood Price, the Blood Charge ramp or Exsanguinate
        /// burst, and which of the two it resolved to), snapshotted in WorldObject_Magic.LaunchSpellProjectiles
        /// and read at impact by the life-projectile branch of CalculateDamage. Same capture-at-launch pattern as
        /// <see cref="LifeProjectileDamage"/> and <see cref="SpellweaveDamageMod"/>, for the same reason: the
        /// player-side fields belong to whatever the caster cast most recently. A Spell AOE or Cascade child takes its
        /// PARENT's snapshot (<see cref="CopyPerCastStamps"/>). See ClassAbilities.LifeProjectileCastStamp.
        /// </summary>
        public LifeProjectileCastStamp LifeProjectileStamp { get; set; } = LifeProjectileCastStamp.None;

        /// <summary>
        /// Whether a landed life projectile may grant the caster a Blood Charge: only the AIMED projectile of
        /// the cast (<paramref name="hasProjectileTarget"/> - only i == 0 of a launch gets a ProjectileTarget,
        /// so a Raven Fury ring grants one charge, not eight), and never an Echo Cast wisp cast
        /// (<paramref name="isEchoWispCast"/>): wisp casts build no pool. Pure so the rule is unit-testable.
        /// </summary>
        public static bool GrantsLifeProjectileCharge(bool hasProjectileTarget, bool isEchoWispCast)
        {
            return hasProjectileTarget && !isEchoWispCast;
        }

        /// <summary>
        /// Copies the per-cast stamps a child projectile must inherit from the projectile it was spawned
        /// from, rather than from whatever the live player fields hold by then (which may belong to a later
        /// cast): <see cref="SpellweaveDamageMod"/>, <see cref="LifeProjectileDamage"/>,
        /// <see cref="LifeProjectileStamp"/>, <see cref="ProcDamageScale"/> and <see cref="RendSourceDamageType"/>. Every child-spawn site - a Spell AOE child, a
        /// Cascade child - must call this, because an unstamped <see cref="LifeProjectileDamage"/> defaults to 0
        /// and a life-projectile spell (Martyr's Hecatomb, Curse of Raven Fury) then lands for 0 damage.
        ///
        /// Deliberately does NOT copy <see cref="VolatileWallCounter"/> (owner ruling 2026-10-24): Volatile Wall
        /// counts only the wall's OWN projectiles, so a Spell AOE child or a Cascade re-fire neither ramps nor
        /// advances the parent's counter.
        ///
        /// Public and static so the inheritance rule is unit-testable on two plain projectiles.
        /// </summary>
        public static void CopyPerCastStamps(SpellProjectile child, SpellProjectile parent)
        {
            if (child == null || parent == null)
                return;

            child.SpellweaveDamageMod = parent.SpellweaveDamageMod;
            child.LifeProjectileDamage = parent.LifeProjectileDamage;
            child.LifeProjectileStamp = parent.LifeProjectileStamp;
            child.ProcDamageScale = parent.ProcDamageScale;
            child.RendSourceDamageType = parent.RendSourceDamageType;
        }

        /// <summary>
        /// Volatile Wall (Archmage T3): the landed-hit counter SHARED by every projectile of the player wall cast
        /// that launched this one, created once per cast in WorldObject_Magic.LaunchSpellProjectiles. Null for
        /// any projectile that is not one of a player wall cast's own projectiles - including a Spell AOE child
        /// or a Cascade re-fire spawned off one, since CopyPerCastStamps never copies it. Read and advanced only by
        /// Player.ApplyVolatileWall, from the war/void branch of CalculateDamage. See VolatileWallAbility.
        /// </summary>
        public VolatileWallAbility.CastCounter VolatileWallCounter { get; set; }

        /// <summary>
        /// The health damage DamageTarget actually applied to the target's vital on this projectile's hit (after
        /// every reduction and the clamp at zero health), 0 until it lands or for a stamina/mana drain. Set before
        /// the post-hit class-ability dispatch (ApplySpellHitClassAbilities), so an ISpellHitAbility handler can
        /// read it - Elemental Proficiency sizes its Acid DoT from it.
        /// </summary>
        public uint LastAppliedDamage { get; private set; }

        /// <summary>
        /// The Summoner wielder cut (SummonerFocus.WielderDamageMod) this projectile's health hit took, 1 when none, set at
        /// the same hit as <see cref="LastAppliedDamage"/>. A follow-on effect sized from LastAppliedDamage whose own damage
        /// lands through a sink that cuts again (the Elemental Proficiency Acid burn, through Creature.TakeDamage) divides
        /// it back out first, so the effect carries the cut once.
        /// </summary>
        public float LastAppliedWielderDamageMod { get; private set; } = 1.0f;

        /// <summary>
        /// TRUE when this projectile is a derived/secondary emission aimed at something other than what the
        /// caster targeted, so it must not record a summon-assist target (Player.OnAttackMonster's
        /// RecordAttackedMonster) when it lands - doing so would yank the player's combat pets off the
        /// creature the player actually cast at onto whatever secondary victim this projectile happens to hit.
        ///
        /// THIS IS NOT A SECOND SPELLING OF <see cref="IsClassAbilitySpawned"/> OR <see cref="FromProc"/> -
        /// those carry unrelated design rulings (ruling Q10 deliberately leaves some children UNflagged on
        /// IsClassAbilitySpawned so Elemental Rend still fires off them) and must not be reused for
        /// this. A projectile can be a "spawned" child for cascade purposes while still being aimed
        /// at the primary target, and conversely this flag is
        /// about targeting intent alone, not chain provenance.
        /// </summary>
        public bool SuppressPetAssist { get; set; }

        /// <summary>
        /// How many class-ability CHAIN HOPS this projectile is removed from the cast that started it.
        /// 0 for anything cast normally (including a first-generation Spellsword proc); 1 for a Cascade
        /// child of that proc; and so on. SPELLSWORD-DESIGN.md sec 5f / Q13.
        ///
        /// THIS IS NOT A SECOND SPELLING OF <see cref="IsClassAbilitySpawned"/>, and the two are deliberately
        /// independent. The boolean means "NEVER chain again" - it is a one-way door, used by Spell AOE,
        /// which has no notion of depth and wants none. The counter means "chain at most N more
        /// times", which is a different guarantee and the only one Cascade can be built on.
        ///
        /// Cascade needed the counter because ruling Q10 leaves proc projectiles UNflagged on the boolean, so
        /// that Elemental Rend still fires off them ("I want this class to be a firework"). A
        /// cascade child is therefore itself a landed proc, and reusing the boolean to stop it chaining would
        /// have made the child behave visibly differently from the proc that spawned it - no rend -
        /// which is exactly what Q13 rejected. The counter stops the chain without changing what the child IS.
        ///
        /// An Echo Cast wisp cast is built through the normal cast path (CreateSpellProjectiles), so it starts at
        /// generation 0 and is not a class-ability proc. Nothing resets this to 0 except creating a fresh
        /// projectile through the normal cast path.
        /// </summary>
        public int ClassAbilityGeneration { get; set; }

        /// <summary>
        /// TRUE when this projectile was launched by a CLASS ABILITY's proc - a Spellsword weapon proc, or a
        /// Cascade child of one - rather than by an ordinary cast, an item's cast-on-strike, or a cloak proc.
        ///
        /// WHY NOT JUST USE <see cref="FromProc"/>. FromProc answers "was this cast triggered by something
        /// procing", which is true of item cast-on-strike spells and cloak procs as well.
        /// Cascade needs the narrower question - "is this one of MY class's procs" - because its dispatch
        /// site fires for every landed war-spell hit, so without a narrow signal it would chain off ordinary
        /// war casts and off any proc weapon the player happens to swing.
        ///
        /// Stamped once, in WorldObject_Magic.LaunchSpellProjectiles, from the player-side latch
        /// Player.ClassAbilityProcCastActive - which is armed only for the duration of one synchronous proc
        /// cast (Player.CastClassAbilityProc), so there is no in-flight drift to reason about: the value is
        /// decided while the projectile is being constructed. Defaults to false, so every existing caster and
        /// every future one that does not opt in is unaffected.
        /// </summary>
        public bool IsClassAbilityProc { get; set; }

        /// <summary>
        /// True on a projectile of an Echo Cast wisp spawned off a Spellsword class-ability proc (stamped in
        /// WorldObject_Magic.LaunchSpellProjectiles from EchoWispCastScope.InheritsProcCasterSkill). The wisp recasts
        /// the same proc spell, so it reads the proc's caster skill, but it is deliberately NOT flagged
        /// <see cref="IsClassAbilityProc"/>: that flag is also Cascade's gate, and a wisp cast must not start a
        /// cascade. Skill selection only.
        /// </summary>
        public bool InheritsProcCasterSkill { get; set; }

        /// <summary>
        /// The caster skill a Spellsword class-ability proc (<see cref="IsClassAbilityProc"/>, Cascade children
        /// and Echo Cast wisp casts off a proc included - see <see cref="InheritsProcCasterSkill"/>) reads in place of the
        /// spell's school for its resist roll, imbue scaling (attackSkill) and skill damage bonus: Item
        /// Enchantment. Null for every other projectile, which keeps reading the spell's own school. The single
        /// source of truth for both the resist call and <see cref="GetCasterMagicCreatureSkill"/>, so the three
        /// sites cannot drift. Proficiency credit, the PvP hit profiles and the school-keyed class-ability
        /// multipliers deliberately keep Spell.School.
        /// </summary>
        public Skill? CasterSkillOverride => IsClassAbilityProc || InheritsProcCasterSkill ? Skill.ItemEnchantment : (Skill?)null;

        /// <summary>
        /// The caster's <see cref="CreatureSkill"/> named by <see cref="CasterSkillOverride"/>, else the spell's school.
        /// </summary>
        public CreatureSkill GetCasterMagicCreatureSkill(Creature caster) =>
            PickCasterSkill(caster, CasterSkillOverride, CasterSkillOverride.HasValue ? default : Spell.School);

        /// <summary>
        /// The pure selection behind <see cref="GetCasterMagicCreatureSkill"/>: the override skill when there is
        /// one, otherwise exactly the lookup an ordinary cast always did (GetCreatureSkill(school)).
        /// </summary>
        public static CreatureSkill PickCasterSkill(Creature caster, Skill? skillOverride, MagicSchool school) =>
            skillOverride.HasValue ? caster.GetCreatureSkill(skillOverride.Value) : caster.GetCreatureSkill(school);

        /// <summary>
        /// Spellweave (Spellsword T1): the spell-damage multiplier of the CAST that launched this projectile
        /// (1.0 = none), captured once in WorldObject_Magic.LaunchSpellProjectiles from
        /// Player.GetSpellweaveSpellDamageMod and read at impact by SpellProjectile.CalculateDamage.
        ///
        /// WHY CAPTURED AND NOT READ AT IMPACT. The player-side stamp is overwritten by every cast. Read at
        /// impact, a projectile still in flight would take the stamp of whatever the caster cast MOST RECENTLY:
        /// a charged manual cast followed by an uncharged war proc would lose its bonus mid-flight, and an
        /// uncharged cast overtaken by a charged one would gain a bonus it never paid for. Capturing it here
        /// ties each projectile to its own cast - the same shape as LifeProjectileDamage and
        /// <see cref="IsClassAbilityProc"/>, both decided while the projectile is built.
        ///
        /// CHILDREN INHERIT THE PARENT'S VALUE, never the live field: SpawnClassAbilityAoeChild copies it, and
        /// CascadeAbility overwrites the live stamp CreateSpellProjectiles gave its children with the parent
        /// projectile's capture.
        /// </summary>
        public float SpellweaveDamageMod { get; set; } = 1.0f;

        /// <summary>
        /// Spellsword Nether parity (owner ruling 2026-10-27): the damage scale of the class-ability proc CAST
        /// that launched this projectile, 1.0 for everything else. A Nether weapon's Spellblade / Runeblade /
        /// Spellstorm proc fires the void ladder, whose retail rungs hit 1.19-2.04x the elemental ones; the
        /// proc handler computes Fire-rung average / Nether-rung average x class_ability_spellsword_nether_parity
        /// (SpellswordSpellTables.NetherParityScale) and Player.CastClassAbilityProc hands it to
        /// LaunchSpellProjectiles, which stamps it here. Read once in the war/void branch of CalculateDamage.
        ///
        /// Captured at launch for the same reason as <see cref="SpellweaveDamageMod"/>, and inherited by every
        /// child (Cascade, Spell AOE) through <see cref="CopyPerCastStamps"/>: each of those re-fires
        /// the SAME spell, so the parent's scale is the right one. An elemental proc is never stamped, and a
        /// multiply by exactly 1.0f is the identity, so its damage is bit-identical.
        /// </summary>
        public float ProcDamageScale { get; set; } = 1.0f;

        /// <summary>
        /// Volatile Elements (Spellsword T2, owner ruling 2026-10-30): the ORIGINAL damage type of the weapon hit
        /// whose Spellblade / Runeblade / Spellstorm proc launched this projectile, when Volatile Elements re-typed
        /// that hit; null for everything else. The proc fires the ROLLED element's spell, so the war/void branch
        /// of CalculateDamage takes the better of the launcher's rend for the spell's own type and its rend for
        /// this type - a fire-rend weapon whose hit rolled cold keeps its fire rend on the cold proc.
        ///
        /// Stamped at launch from Player.ClassAbilityProcRendSourceDamageType, armed by
        /// Player.CastClassAbilityProc(cast, scale, rendSourceDamageType) for exactly one synchronous proc cast,
        /// and inherited by every child (Cascade, Spell AOE) through <see cref="CopyPerCastStamps"/>,
        /// for the same reason as <see cref="ProcDamageScale"/>. Null leaves the rend lookup exactly as before.
        /// </summary>
        public DamageType? RendSourceDamageType { get; set; }

        /// <summary>
        /// Class ability "Spell AOE": damage scale applied to this projectile's landed hit. 1.0 for a
        /// normal cast; a fraction (server tunable, default 0.5) for a radiated secondary blast so the
        /// AOE hits softer than the primary.
        /// </summary>
        public float ClassAbilityAoeDamageMultiplier { get; set; } = 1.0f;

        /// <summary>
        /// What this projectile's landed damage counts as at a monster's incoming-damage filters (see
        /// MonsterEffects.IncomingDamageOrigin): DirectHit only for a projectile the caster cast directly -
        /// every projectile of an ordinary cast, whether or not it is the aimed one. Secondary for every
        /// derived emission, each excluded on its own flag so no single flag's ruling has to cover the others:
        ///   FromProc               - item cast-on-strike, cloak procs, Spell AOE children
        ///   IsClassAbilityProc     - a Spellsword weapon proc and its Cascade children
        ///   ClassAbilityGeneration - any Cascade hop (> 0), even one that somehow lost the proc flag
        ///   IsClassAbilityAoeChild / IsClassAbilitySpawned - a Spell AOE splash radiated off another target
        /// An Echo Cast wisp cast is NOT here: it is the player's own first-hand cast (IsEchoWispCast is deliberately ignored).
        /// </summary>
        public IncomingDamageOrigin IncomingDamageOrigin =>
            FromProc || IsClassAbilityProc || ClassAbilityGeneration > 0 || IsClassAbilityAoeChild || IsClassAbilitySpawned
                ? IncomingDamageOrigin.Secondary
                : IncomingDamageOrigin.DirectHit;

        public int DebugVelocity;

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public SpellProjectile(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public SpellProjectile(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            // Override weenie description defaults
            ValidLocations = null;
            DefaultScriptId = null;
        }

        /// <summary>
        /// Perfroms additional set up of the spell projectile based on the spell id or its derived type.
        /// </summary>
        public void Setup(Spell spell, ProjectileSpellType spellType)
        {
            Spell = spell;
            SpellType = spellType;

            InitPhysicsObj();

            // Runtime changes to default state
            ReportCollisions = true;
            Missile = true;
            AlignPath = true;
            PathClipped = true;
            IgnoreCollisions = false;

            // FIXME: use data here
            if (!Spell.Name.Equals("Rolling Death"))
                Ethereal = false;

            if (SpellType == ProjectileSpellType.Bolt || SpellType == ProjectileSpellType.Streak
                || SpellType == ProjectileSpellType.Arc || SpellType == ProjectileSpellType.Volley || SpellType == ProjectileSpellType.Blast
                || WeenieClassId == 7276 || WeenieClassId == 7277 || WeenieClassId == 7279 || WeenieClassId == 7280)
            {
                DefaultScriptId = (uint)PlayScript.ProjectileCollision;
                DefaultScriptIntensity = 1.0f;
            }

            // Some wall spells don't have scripted collisions
            if (WeenieClassId == 7278 || WeenieClassId == 7281 || WeenieClassId == 7282 || WeenieClassId == 23144)
            {
                ScriptedCollision = false;
            }

            AllowEdgeSlide = false;

            // No need to send an ObjScale of 1.0f over the wire since that is the default value
            if (ObjScale == 1.0f)
                ObjScale = null;

            if (SpellType == ProjectileSpellType.Ring)
            {
                if (spell.Id == 3818)
                {
                    DefaultScriptId = (uint)PlayScript.Explode;
                    DefaultScriptIntensity = 1.0f;
                    ScriptedCollision = true;
                }
                else
                {
                    ScriptedCollision = false;
                }
            }

            // Projectiles with RotationSpeed get omega values and "align path" turned off which
            // creates the nice swirling animation
            if ((RotationSpeed ?? 0) != 0)
            {
                AlignPath = false;
                PhysicsObj.Omega = new Vector3((float)(Math.PI * 2 * RotationSpeed), 0, 0);
            }
        }

        public static ProjectileSpellType GetProjectileSpellType(uint spellID)
        {
            var spell = new Spell(spellID);

            if (spell.Wcid == 0)
                return ProjectileSpellType.Undef;

            if (spell.NumProjectiles == 1)
            {
                if (spell.Category >= SpellCategory.AcidStreak && spell.Category <= SpellCategory.SlashingStreak ||
                         spell.Category == SpellCategory.NetherStreak || spell.Category == SpellCategory.Fireworks)
                    return ProjectileSpellType.Streak;

                else if (spell.NonTracking)
                    return ProjectileSpellType.Arc;

                else
                    return ProjectileSpellType.Bolt;
            }

            if (spell.Category >= SpellCategory.AcidRing && spell.Category <= SpellCategory.SlashingRing || spell.SpreadAngle == 360)
                return ProjectileSpellType.Ring;

            if (spell.Category >= SpellCategory.AcidBurst && spell.Category <= SpellCategory.SlashingBurst ||
                spell.Category == SpellCategory.NetherDamageOverTimeRaising3)
                return ProjectileSpellType.Blast;

            // 1481 - Flaming Missile Volley
            if (spell.Category >= SpellCategory.AcidVolley && spell.Category <= SpellCategory.BladeVolley || spell.Name.Contains("Volley"))
                return ProjectileSpellType.Volley;

            if (spell.Category >= SpellCategory.AcidWall && spell.Category <= SpellCategory.SlashingWall)
                return ProjectileSpellType.Wall;

            if (spell.Category >= SpellCategory.AcidStrike && spell.Category <= SpellCategory.SlashingStrike)
                return ProjectileSpellType.Strike;

            return ProjectileSpellType.Undef;
        }

        public float GetProjectileScriptIntensity(ProjectileSpellType spellType)
        {
            if (spellType == ProjectileSpellType.Wall)
            {
                return 0.4f;
            }
            if (spellType == ProjectileSpellType.Ring)
            {
                if (Spell.Level == 6 || Spell.Id == 3818)
                    return 0.4f;
                if (Spell.Level == 7)
                    return 1.0f;
            }

            // Bolt, Blast, Volley, Streak and Arc all seem to use this scale
            // TODO: should this be based on spell level, or power of first scarab?
            // ie. can this use Spell.Formula.ScarabScale?
            switch (Spell.Level)
            {
                case 1:
                    return 0f;
                case 2:
                    return 0.2f;
                case 3:
                    return 0.4f;
                case 4:
                    return 0.6f;
                case 5:
                    return 0.8f;
                case 6:
                case 7:
                case 8:
                    return 1.0f;
                default:
                    return 0f;
            }
        }

        public bool WorldEntryCollision { get; set; }

        public void ProjectileImpact()
        {
            //Console.WriteLine($"{Name}.ProjectileImpact()");

            ReportCollisions = false;
            Ethereal = true;
            IgnoreCollisions = true;
            NoDraw = true;
            Cloaked = true;
            LightsStatus = false;

            PhysicsObj.set_active(false);

            if (PhysicsObj.entering_world)
            {
                // this path should only happen if spell_projectile_ethereal = false
                EnqueueBroadcast(new GameMessageScript(Guid, PlayScript.Launch, GetProjectileScriptIntensity(SpellType)));
                WorldEntryCollision = true;
            }

            EnqueueBroadcast(new GameMessageSetState(this, PhysicsObj.State));
            EnqueueBroadcast(new GameMessageScript(Guid, PlayScript.Explode, GetProjectileScriptIntensity(SpellType)));

            // this should only be needed for spell_projectile_ethereal = true,
            // however it can also fix a display issue on client in default mode,
            // where GameMessageSetState updates projectile to ethereal before it has actually collided on client,
            // causing a 'ghost' projectile to continue to sail through the target

            PhysicsObj.Velocity = Vector3.Zero;
            EnqueueBroadcast(new GameMessageVectorUpdate(this));

            ActionChain selfDestructChain = new ActionChain();
            selfDestructChain.AddDelaySeconds(5.0);
            selfDestructChain.AddAction(this, () => Destroy());
            selfDestructChain.EnqueueChain();
        }

        /// <summary>
        /// Called from Destroy while this projectile is still in its landblock. The DeleteObject broadcast
        /// (Landblock.RemoveWorldObjectInternal) reaches only players on this projectile's known-players list, but the
        /// player it was aimed at can drop off that list (ObjectMaint.DestroyObjects, 25 s after the projectile left
        /// that player's server-side view) while its own client still holds the projectile, for example stopped
        /// against the player. Nothing would ever remove that solid projectile from that client, so the target gets
        /// the delete directly.
        ///
        /// Players never on the list are left alone: their clients were never sent the projectile. A DeleteObject for
        /// a guid a client may not have is already sent elsewhere (Container.SendDeletesForMyInventory), and a dynamic
        /// guid is not reissued for 360 minutes (GuidManager recycleTime), so this cannot remove a different object.
        /// </summary>
        public void DeleteFromUntrackedTarget()
        {
            if (!(ProjectileTarget is Player target) || target.Session == null || PhysicsObj == null || CurrentLandblock == null)
                return;

            if (Visibility && !target.Adminvision)
                return;

            var stillKnown = PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer().Contains(target);
            var sameInstance = target.Location != null && Location != null && target.Location.Instance == Location.Instance;

            if (!Physics.PlayerPhysicsResync.ShouldDeleteForUntrackedTarget(stillKnown, sameInstance))
                return;

            if (target.TryTakeUntrackedProjectileDeleteWarn())
                log.Warn($"WARNING: sent DeleteObject for projectile {Name} ({Guid}) directly to untracked target {target.Name}");

            target.Session.Network.EnqueueSend(new GameMessageDeleteObject(this));
        }

        /// <summary>
        /// Handles collision with scenery or other static objects that would block a projectile from reaching its target,
        /// in which case the projectile should be removed with no further processing.
        /// </summary>
        public override void OnCollideEnvironment()
        {
            //Console.WriteLine($"{Name}.OnCollideEnvironment()");

            if (Info != null && ProjectileSource is Player player && player.DebugSpell)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{Name}.OnCollideEnvironment()", ChatMessageType.Broadcast));
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(Info.ToString(), ChatMessageType.Broadcast));
            }

            ProjectileImpact();
        }

        public override void OnCollideObject(WorldObject target)
        {
            //Console.WriteLine($"{Name}.OnCollideObject({target.Name})");

            var player = ProjectileSource as Player;

            if (Info != null && player != null && player.DebugSpell)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{Name}.OnCollideObject({target?.Name} ({target?.Guid}))", ChatMessageType.Broadcast));
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(Info.ToString(), ChatMessageType.Broadcast));
            }

            ProjectileImpact();

            // a cosmetic projectile (a Reflect return bolt) only shows the flight: the bounce's damage is
            // resolved by Player.ApplyReflectBounce when it arrives, so whatever this touches takes nothing
            if (IsCosmeticProjectile) return;

            // ensure valid creature target
            var creatureTarget = target as Creature;
            if (creatureTarget == null || target == ProjectileSource)
                return;

            // Echo Cast wisp: a wisp's projectile (rings and walls included, which are target-blind) lands nothing
            // on a player - no damage, no enchantment, no PK message. Class abilities never reach PvP.
            if (EchoWispBlocksHit(IsEchoWispCast, creatureTarget is Player))
                return;

            if (player != null)
                player.LastHitSpellProjectile = Spell;
            
            // ensure caster can damage target
            var sourceCreature = ProjectileSource as Creature;
            if (sourceCreature != null && !sourceCreature.CanDamage(creatureTarget))
                return;

            // Open World Threads R12: a projectile whose source (a player, or a player's pet judged at its own position)
            // stands past R lands NOTHING on a run creature - no damage, no enchantment, no class-ability or weapon-mod
            // hit, no OnAttackMonster. DamageTarget keeps its own gate as a backstop.
            if (creatureTarget.P_OutdoorPerimeter != null && creatureTarget.RefusesOutdoorPerimeterDamage(ProjectileSource))
                return;

            // if player target, ensure matching PK status
            var targetPlayer = creatureTarget as Player;

            var pkError = ProjectileSource?.CheckPKStatusVsTarget(creatureTarget, Spell);
            if (pkError != null)
            {
                // A spawn-protection refusal already sent its own throttled line to the attacker and says nothing to the target.
                if (ACE.Server.Pvp.PvpArenaHookSettings.IsSpawnProtectedRefusal(pkError))
                    return;

                if (player != null)
                    player.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(player.Session, pkError[0], creatureTarget.Name));

                if (targetPlayer != null)
                    targetPlayer.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(targetPlayer.Session, pkError[1], ProjectileSource.Name));

                return;
            }

            // PropertyBool.PveOnly (PvpPveOnlyRule.BlocksDamage): a spell launched by a PvE-only weapon lands nothing on a player.
            if (player != null && player.PveOnlyBlocksSpellHit(creatureTarget, ProjectileLauncher))
                return;

            var critical = false;
            var critDefended = false;
            var overpower = false;

            var damage = CalculateDamage(ProjectileSource, creatureTarget, ref critical, ref critDefended, ref overpower);

            // survival arena engagement-stall test: a monster's spell projectile reaching a player counts whether
            // it landed or was resisted (see Player.NoteSurvivalEngagement). One field read outside a run.
            if (targetPlayer != null && player == null)
                targetPlayer.NoteSurvivalEngagement(sourceCreature);

            // Reflect (Vanguard T3): set when the target player bounced this projectile back at its caster. Read
            // again at the item-proc site below - nothing of a bounced shot reaches the player, the caster's
            // on-hit item procs included.
            var reflectedProjectile = false;

            if (damage != null)
            {
                if (Spell.MetaSpellType == ACE.Entity.Enum.SpellType.EnchantmentProjectile)
                {
                    // handle EnchantmentProjectile successfully landing on target
                    ProjectileSource.CreateEnchantment(creatureTarget, ProjectileSource, ProjectileLauncher, Spell, false, FromProc, procSourced: IncomingDamageOrigin == IncomingDamageOrigin.Secondary);
                }
                else
                {
                    // Spell AOE secondary blasts land at a reduced fraction (1.0 = no scaling for a normal cast)
                    var spellDamage = damage.Value * ClassAbilityAoeDamageMultiplier;

                    // Reflect (Vanguard T3): a MONSTER's war or void projectile bounces straight back at its
                    // caster. Decided BEFORE the monster-effect dispatch below, so a bounced bolt's riders
                    // (debuff, dispel, castspell) never touch this player; the bounce then resolves against
                    // the caster's own defenses, keeping the magic-damage ramp as part of the attack
                    // (Player.LaunchReflectedSpell). `player == null` keeps this to a monster caster, so a
                    // reflected projectile can never resolve against another player; the player-side hooks
                    // below are already `player != null` gated and are therefore unreachable on this branch.
                    reflectedProjectile = targetPlayer != null && sourceCreature != null && player == null
                        && targetPlayer.TryReflectProjectile(sourceCreature, CombatType.Magic, Spell.DamageType, spellDamage, IncomingDamageOrigin);

                    if (reflectedProjectile)
                    {
                        targetPlayer.LaunchReflectedSpell(sourceCreature, this, critical, spellDamage);
                    }
                    else
                    {
                        // Monster combat effects: a monster caster's landed spell hit. Dispatched BEFORE the hit
                        // is applied, because the hook scales the damage by ref (a magic-damage ramp); the player
                        // mirror below runs after the hit instead, since nothing it does changes the number.
                        if (sourceCreature != null && player == null)
                            sourceCreature.ApplySpellHitMonsterEffects(creatureTarget, this, ref spellDamage);

                        DamageTarget(creatureTarget, spellDamage, critical, critDefended, overpower);
                    }

                    // Class ability hook: a player's landed damaging-spell hit can radiate (Spell AOE),
                    // apply Vulnerability (Elemental Rend), chain (Cascade), pin
                    // (Pinning Shot), strip a buff and cleanse the caster (Dispelling Dance) or apply
                    // Sundermark's vulnerability.
                    //
                    // NO SCHOOL TEST as of 2026-09-13. It was `Spell.School == MagicSchool.WarMagic` until
                    // then; war, void and life projectiles now all dispatch, and every handler on the hook
                    // widened together by design.
                    //
                    // WHAT THIS SITE STILL GUARANTEES, because the handlers rely on it: this is the
                    // non-EnchantmentProjectile branch of `if (damage != null)`, so it fires only for a
                    // projectile that actually landed damage on a creature. A life spell that produces NO
                    // projectile - Harm, Drain Health - resolves in WorldObject_Magic.HandleCastSpell_Boost
                    // / _Transfer and never reaches here, so it cannot proc any of these. That is an
                    // accepted limit, not a gap to close by adding a second dispatch to the boost/transfer
                    // path, and the player-facing descriptions say "damaging spell projectiles" for exactly
                    // this reason.
                    //
                    // Each handler still owns its own rules: Spell AOE's Arc-only test, Spell AOE
                    // self-excluding on AOE children via IsClassAbilitySpawned to avoid cascade, and Cascade's
                    // generation counter. Echo Cast is not on this hook (the wisp rework triggers it at cast
                    // commit instead, Player.TryTriggerEchoWisp), so no landed hit ever spawns a wisp. Rend, Pinning Shot and
                    // Dispelling Dance (internal name DispellingEdge) and Sundermark fire on every landed copy.
                    if (player != null)
                        player.ApplySpellHitClassAbilities(creatureTarget, this);

                    // Tier B weapon mods carried by the equipped WAND: the three leeches, taking their
                    // fraction of the damage this hit is applying. Deliberately not school-gated, unlike the
                    // class-ability dispatch above - a leech has no school in it. Gated on
                    // weapon_mods_enabled inside. See Player_WeaponMods.cs.
                    if (player != null)
                        player.ApplyWeaponModSpellHit(creatureTarget, damage.Value * ClassAbilityAoeDamageMultiplier);
                    // Summoner casters: a pet's landed damaging projectile rolls its owner's focus Siphon once, as a
                    // player's does with a Siphon wand; a derived emission (proc, AOE child, Cascade hop) never rolls
                    else if (sourceCreature is CombatPet siphonPet && damage.Value > 0.0f && IncomingDamageOrigin == IncomingDamageOrigin.DirectHit)
                        SummonerFocus.TryPetSiphon(siphonPet, creatureTarget);
                }

                // if this SpellProjectile has a TargetEffect, play it on successful hit
                DoSpellEffects(Spell, ProjectileSource, creatureTarget, true);

                if (player != null)
                    Proficiency.OnSuccessUse(player, player.GetCreatureSkill(Spell.School), Spell.PowerMod);

                // handle target procs
                // note that for untargeted multi-projectile spells,
                // ProjectileTarget will be null here, so procs will not apply

                // TODO: instead of ProjectileLauncher is Caster, perhaps a SpellProjectile.CanProc bool that defaults to true,
                // but is set to false if the source of a spell is from a proc, to prevent multi procs?

                if (sourceCreature != null && ProjectileTarget != null && !FromProc && !reflectedProjectile)
                {
                    // TODO figure out why cross-landblock group operations are happening here. We shouldn't need this code Mag-nus 2021-02-09
                    bool threadSafe = true;

                    if (LandblockManager.CurrentlyTickingLandblockGroupsMultiThreaded)
                    {
                        // Ok... if we got here, we're likely in the parallel landblock physics processing.
                        if (sourceCreature.CurrentLandblock == null || creatureTarget.CurrentLandblock == null || sourceCreature.CurrentLandblock.CurrentLandblockGroup != creatureTarget.CurrentLandblock.CurrentLandblockGroup)
                            threadSafe = false;
                    }

                    if (threadSafe)
                        // This can result in spell projectiles being added to either sourceCreature or creatureTargets landblock.
                        sourceCreature.TryProcEquippedItems(sourceCreature, creatureTarget, false, ProjectileLauncher);
                    else
                    {
                        // sourceCreature and creatureTarget are now in different landblock groups.
                        // What has likely happened is that sourceCreature sent a projectile toward creatureTarget. Before impact, sourceCreature was teleported away.
                        // To perform this fully thread safe, we would enqueue the work onto worldManager.
                        // WorldManager.EnqueueAction(new ActionEventDelegate(() => sourceCreature.TryProcEquippedItems(creatureTarget, false)));
                        // But, to keep it simple, we will just ignore it and not bother with TryProcEquippedItems for this particular impact.
                    }
                }
            }

            // also called on resist
            if (player != null && targetPlayer == null)
                player.OnAttackMonster(creatureTarget, !SuppressPetAssist);

            if (player == null && targetPlayer == null)
            {
                // check for faction combat
                if (sourceCreature != null && creatureTarget != null && (sourceCreature.AllowFactionCombat(creatureTarget) || sourceCreature.PotentialFoe(creatureTarget)))
                    sourceCreature.MonsterOnAttackMonster(creatureTarget);
            }
        }

        /// <summary>
        /// Calculates the damage for a spell projectile
        /// Used by war magic, void magic, and life magic projectiles
        /// </summary>
        public float? CalculateDamage(WorldObject source, Creature target, ref bool criticalHit, ref bool critDefended, ref bool overpower)
        {
            var sourcePlayer = source as Player;
            var targetPlayer = target as Player;

            if (source == null || !target.IsAlive || targetPlayer != null && targetPlayer.Invincible)
                return null;

            // check lifestone protection
            if (targetPlayer != null && targetPlayer.UnderLifestoneProtection)
            {
                if (sourcePlayer != null)
                    sourcePlayer.Session.Network.EnqueueSend(new GameMessageSystemChat($"The Lifestone's magic protects {targetPlayer.Name} from the attack!", ChatMessageType.Magic));

                targetPlayer.HandleLifestoneProtection();
                return null;
            }

            var critDamageBonus = 0.0f;
            var weaponCritDamageMod = 1.0f;
            var weaponResistanceMod = 1.0f;
            var resistanceMod = 1.0f;

            // life magic
            var lifeMagicDamage = 0.0f;

            // war/void magic
            var baseDamage = 0;
            var skillBonus = 0.0f;
            var finalDamage = 0.0f;

            var resistanceType = Creature.GetResistanceType(Spell.DamageType);

            var sourceCreature = source as Creature;
            if (sourceCreature?.Overpower != null)
                overpower = Creature.GetOverpower(sourceCreature, target);

            var weapon = ProjectileLauncher;

            var resistSource = IsWeaponSpell ? weapon : source;

            var resisted = source.TryResistSpell(target, Spell, resistSource, true, CasterSkillOverride);
            if (resisted && !overpower)
                return null;

            CreatureSkill attackSkill = null;
            if (sourceCreature != null)
                attackSkill = GetCasterMagicCreatureSkill(sourceCreature);

            // PvP via the one classifier. A self hit cannot reach here: OnCollideObject, this method's only
            // caller, returns when target == ProjectileSource, so this equals the old both-players test.
            bool isPVP = PvpClassifier.IsPvp(source, target);

            // critical hit
            var criticalChance = GetWeaponMagicCritFrequency(weapon, sourceCreature, attackSkill, target);

            // PvP rules choke point CC2 (Docs/Pvp/DESIGN.md "PvP rules (levers)", context tuning): the arena / battleground
            // war magic crit chance multiplier, applied to the final chance here at the call site because
            // GetWeaponMagicCritFrequency returns early when there is no wand. A no-op unless source and target are
            // two distinct players in the same Live arena or battleground match and the projectile is war magic.
            criticalChance = PvpContextTuning.ApplyCritChance(PvpChokePoint.CC2, source, target, PvpHitProfile.ForSpell(Spell.School, SpellType), criticalChance);

            // Elemental Proficiency Slash (Archmage T3): +5/10/15 percentage points of crit chance on a Slash spell
            // against a monster. A separate line so the PvpContextTuning line above stays as pinned. Zero for a
            // non-player caster, rank 0, a non-Slash spell or an ineligible target.
            if (sourceCreature is Player proficiencyCritCaster)
                criticalChance += proficiencyCritCaster.GetElementalProficiencySlashCritBonus(Spell.DamageType, target);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) < criticalChance)
            {
                if (targetPlayer != null && targetPlayer.AugmentationCriticalDefense > 0)
                {
                    // inside targetPlayer != null, so "source is a player" is exactly isPVP
                    var criticalDefenseMod = isPVP ? 0.05f : 0.25f;
                    var criticalDefenseChance = targetPlayer.AugmentationCriticalDefense * criticalDefenseMod;

                    if (criticalDefenseChance > ThreadSafeRandom.Next(0.0f, 1.0f))
                        critDefended = true;
                }

                if (!critDefended)
                    criticalHit = true;
            }

            var absorbMod = GetAbsorbMod(target);

            //http://acpedia.org/wiki/Announcements_-_2014/01_-_Forces_of_Nature - Aegis is 72% effective in PvP
            if (isPVP && (target.CombatMode == CombatMode.Melee || target.CombatMode == CombatMode.Missile))
            {
                absorbMod = 1 - absorbMod;
                absorbMod *= 0.72f;
                absorbMod = 1 - absorbMod;
            }

            // PvP rules choke point AB1 (Docs/Pvp/DESIGN.md "PvP rules (levers)"): pvp_magic_absorb_mod scales the
            // absorb REDUCTION fraction, AFTER the retail 0.72 above so that still applies first. A no-op unless
            // source and target are two distinct players and something is absorbing.
            absorbMod = PvpRules.ApplyMagicAbsorbMod(source, target, absorbMod);

            if (isPVP && Spell.IsHarmful)
                Player.UpdatePKTimers(sourcePlayer, targetPlayer);

            var elementalDamageMod = GetCasterElementalDamageModifier(weapon, sourceCreature, target, Spell.DamageType);

            // Possible 2x + damage bonus for the slayer property
            var slayerMod = GetWeaponCreatureSlayerModifier(weapon, sourceCreature, target);

            // life magic projectiles: ie., martyr's hecatomb
            if (Spell.MetaSpellType == ACE.Entity.Enum.SpellType.LifeProjectile)
            {
                lifeMagicDamage = LifeProjectileDamage * Spell.DamageRatio;

                // could life magic projectiles crit?
                // if so, did they use the same 1.5x formula as war magic, instead of 2.0x?
                if (criticalHit)
                {
                    // verify: CriticalMultiplier only applied to the additional crit damage,
                    // whereas CD/CDR applied to the total damage (base damage + additional crit damage)
                    weaponCritDamageMod = GetWeaponCritDamageMod(weapon, sourceCreature, attackSkill, target);

                    // Weapon mods v3 Tier B (2026-08-06): Execution's spell half, the same multiplier as
                    // DamageEvent.cs's physical path - see WeaponModRegistry.cs's Tier B v3 remarks. The line
                    // after it is a pet's share from its owner's Summoner focus (0 for every other caster).
                    weaponCritDamageMod *= 1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Execution, sourceCreature);
                    weaponCritDamageMod *= 1.0f + (float)SummonerFocus.PetFocusModValue(sourceCreature, target, WeaponModId.Execution);

                    critDamageBonus = lifeMagicDamage * 0.5f * weaponCritDamageMod;
                }

                weaponResistanceMod = GetWeaponResistanceModifier(weapon, sourceCreature, attackSkill, Spell.DamageType, target);

                // if attacker/weapon has IgnoreMagicResist directly, do not transfer to spell projectile
                // only pass if SpellProjectile has it directly, such as 2637 - Invoking Aun Tanua

                resistanceMod = (float)Math.Max(0.0f, target.GetResistanceMod(resistanceType, this, null, weaponResistanceMod));

                // Jewelry Aspects Penetration (life projectiles): a player's spell against a non-player, resisted mods only
                if (sourceCreature is Player lifePenetrationCaster && target is not Player)
                {
                    var lifePenetrationP = lifePenetrationCaster.GetPenetrationAgainst(target);
                    resistanceMod = JewelryAspectMath.Penetrate(resistanceMod, lifePenetrationP, lifePenetrationP > 0 ? lifePenetrationCaster.GetPenetrationAmplify() : 0.0);
                }

                // Reflect (Vanguard T3): the caster's own attack half, before anything about THIS target
                // scales it, for CalculateReflectedDamage
                ReflectAttackDamage = (lifeMagicDamage + critDamageBonus) * elementalDamageMod * slayerMod;
                ReflectWeaponResistanceMod = weaponResistanceMod;

                finalDamage = (lifeMagicDamage + critDamageBonus) * elementalDamageMod * slayerMod * resistanceMod * absorbMod;

                // class abilities: the Blood Mage life-strike package on the two life projectiles -
                // Martyr's Hecatomb and Curse of Raven Fury. Blood Price (already paid at cast time), the
                // Sanguine Reserve charge ramp or its Exsanguinate burst, and the burst's resistance-ignore.
                // PvE only, the same condition the war/void branch below uses.
                //
                // THE BURST IS NOT DECIDED HERE, AND MUST NOT BE. User ruling, live test 2026-08-03:
                // "Exsanguinate should be specifically for Hecatomb or Raven Fury", then "whole ring on
                // raven fury + exsanguinate" - so all EIGHT projectiles of a Raven Fury cast carry the
                // burst, not one of them. That supersedes BLOOD-MAGE-DESIGN sec 3's "Harm or Hecatomb" row.
                //
                // The pool is emptied by whoever consumes it, so a per-projectile read here would give the
                // burst to whichever of the eight collided first and an empty pool to the other seven -
                // arbitrary, and different every cast. The decision is therefore made ONCE PER CAST, in
                // Player.ApplyLifeProjectileBloodCharge at the cast site, and every projectile of that cast
                // reads the stamped answer. Do not "simplify" this back into a pool read at this site.
                //
                // grantCharge is still per-projectile and still limited to the AIMED bolt (only i == 0 gets
                // a ProjectileTarget - see the launch loop in WorldObject_Magic), so one cast is one charge
                // rather than eight against a 3-5 stack cap. Unlike the burst it belongs at this site: it is
                // earned by a LANDED hit, and a cast that connects with nothing should build nothing.
                //
                // Hecatomb is a single aimed projectile and behaves as it always did.
                //
                // The per-cast terms (Blood Price, the Blood Charge ramp or Exsanguinate burst) come from THIS
                // projectile's launch-time snapshot, LifeProjectileStamp - not the player's live fields, which
                // a later cast may already have overwritten. An Echo Cast wisp cast carries the neutral stamp
                // (WorldObject.ResolveLaunchStamps) and never grants the charge: wisp casts build no pool.
                if (sourceCreature is Player lifeClassAbilityCaster && targetPlayer == null)
                {
                    finalDamage *= lifeClassAbilityCaster.ApplyLifeProjectileClassAbilityDamage(target,
                        grantCharge: GrantsLifeProjectileCharge(ProjectileTarget != null, IsEchoWispCast), castStamp: LifeProjectileStamp,
                        spellweaveDamageMod: SpellweaveDamageMod);

                    // FORK: Focus spell damage (magic_damage_per_focus_point). A direct cast only: a proc, cascade,
                    // or AOE child is Secondary and never earns it. PvE only (targetPlayer == null).
                    if (Player.QualifiesForFocusSpellDamage(IncomingDamageOrigin == IncomingDamageOrigin.DirectHit, targetPlayer != null, FromProc, IsClassAbilityProc))
                        finalDamage *= lifeClassAbilityCaster.GetFocusSpellDamageMod();
                }
                // FORK: Focus spell damage for a caster summon (its own buffed Focus), under the same gates
                else if (sourceCreature is CombatPet { IsCasterPet: true } && targetPlayer == null
                    && Player.QualifiesForFocusSpellDamage(IncomingDamageOrigin == IncomingDamageOrigin.DirectHit, false, FromProc, IsClassAbilityProc))
                {
                    finalDamage *= CombatPet.FocusSpellDamageModFor(sourceCreature);
                }
            }
            // war/void magic projectiles
            else
            {
                var spellMinDamage = Spell.MinDamage;
                var spellMaxDamage = Spell.MaxDamage;

                if (criticalHit)
                {
                    // Original:
                    // http://acpedia.org/wiki/Announcements_-_2002/08_-_Atonement#Letter_to_the_Players

                    // Critical Strikes: In addition to the skill-based damage bonus, each projectile spell has a 2% chance of causing a critical hit on the target and doing increased damage.
                    // A magical critical hit is similar in some respects to melee critical hits (although the damage calculation is handled differently).
                    // While a melee critical hit automatically does twice the maximum damage of the weapon, a magical critical hit will do an additional half the minimum damage of the spell.
                    // For instance, a magical critical hit from a level 7 spell, which does 110-180 points of damage, would add an additional 55 points of damage to the spell.

                    // Later updated for PvE only:

                    // http://acpedia.org/wiki/Announcements_-_2004/07_-_Treaties_in_Stone#Letter_to_the_Players

                    // Currently when a War Magic spell scores a critical hit, it adds a multiple of the base damage of the spell to a normal damage roll.
                    // Starting in July, War Magic critical hits will instead add a multiple of the maximum damage of the spell.
                    // No more crits that do less damage than non-crits!

                    if (isPVP) // PvP: 50% of the MIN damage added to normal damage roll
                        critDamageBonus = spellMinDamage * 0.5f;
                    else   // PvE: 50% of the MAX damage added to normal damage roll
                        critDamageBonus = spellMaxDamage * 0.5f;

                    // verify: CriticalMultiplier only applied to the additional crit damage,
                    // whereas CD/CDR applied to the total damage (base damage + additional crit damage)
                    weaponCritDamageMod = GetWeaponCritDamageMod(weapon, sourceCreature, attackSkill, target);

                    // Weapon mods v3 Tier B (2026-08-06): Execution's spell half - see the life magic branch
                    // above for the same hook.
                    weaponCritDamageMod *= 1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Execution, sourceCreature);
                    weaponCritDamageMod *= 1.0f + (float)SummonerFocus.PetFocusModValue(sourceCreature, target, WeaponModId.Execution);

                    critDamageBonus *= weaponCritDamageMod;
                }

                /* War Magic skill-based damage bonus
                 * http://acpedia.org/wiki/Announcements_-_2002/08_-_Atonement#Letter_to_the_Players
                 */
                if (sourcePlayer != null)
                {
                    var magicSkill = GetCasterMagicCreatureSkill(sourcePlayer).Current;

                    if (magicSkill > Spell.Power)
                    {
                        var percentageBonus = (magicSkill - Spell.Power) / 1000.0f;

                        skillBonus = spellMinDamage * percentageBonus;
                    }
                }
                baseDamage = ThreadSafeRandom.Next(spellMinDamage, spellMaxDamage);

                // Volatile Elements: a proc launched off a re-typed weapon hit keeps the better of the launcher's
                // rend for the spell's own type and for the hit's original type (RendSourceDamageType). Null
                // (every other projectile) is the plain lookup.
                weaponResistanceMod = VolatileElementsMath.ResolveWeaponResistanceMod(weapon, sourceCreature, attackSkill, RendSourceDamageType, Spell.DamageType, target);

                // if attacker/weapon has IgnoreMagicResist directly, do not transfer to spell projectile
                // only pass if SpellProjectile has it directly, such as 2637 - Invoking Aun Tanua

                // Elemental Inversion + Arcane Hollow (Archmage T1/T2, PvE only, war/void magic): a replacement
                // for the target's INNATE resist term only. The lookup stays on resistanceType, so the original
                // type's rend, vulnerabilities and protections all still apply. Null (unchanged) for a player
                // target or a type outside the seven (Nether, a life health bolt). One roll per projectile hit.
                // The same call decides Elemental Proficiency Pierce for this hit (ignore, folded in here, or
                // flat, applied below); the final-damage site reads that one decision.
                var elementalPierceShape = ElementalProficiencySpellMath.PierceShape.None;
                var archmageInnate = sourceCreature is Player archmageCaster
                    ? archmageCaster.GetArchmageInnateOverride(target, resistanceType, true, out elementalPierceShape)
                    : null;

                resistanceMod = (float)Math.Max(0.0f, target.GetResistanceMod(resistanceType, this, null, weaponResistanceMod, archmageInnate));

                // Jewelry Aspects Penetration (war/void projectiles): before the void PvP modifier below, which is PvP only
                if (sourceCreature is Player warPenetrationCaster && target is not Player)
                {
                    var warPenetrationP = warPenetrationCaster.GetPenetrationAgainst(target);
                    resistanceMod = JewelryAspectMath.Penetrate(resistanceMod, warPenetrationP, warPenetrationP > 0 ? warPenetrationCaster.GetPenetrationAmplify() : 0.0);
                }

                if (isPVP && Spell.DamageType == DamageType.Nether)
                {
                    // for direct damage from void spells in pvp,
                    // apply void_pvp_modifier *on top of* the player's natural resistance to nether

                    // this supposedly brings the direct damage from void spells in pvp closer to retail
                    resistanceMod *= (float)PropertyManager.GetDouble("void_pvp_modifier").Item;
                }

                finalDamage = baseDamage + critDamageBonus + skillBonus;

                // Spellsword Nether parity: a void proc's whole roll (base, crit bonus, skill bonus) scaled to
                // the elemental rung's average. Applied here, before Reflect's snapshot and every multiplier
                // below, so the proc behaves everywhere as if it were the weaker spell. 1.0 for anything that is
                // not a Nether class-ability proc. See ProcDamageScale.
                finalDamage *= ProcDamageScale;
                if (FromProc && weapon != null && Cloak.IsCloak(weapon) && weapon.ProcSpell == Spell.Id) finalDamage *= (float)CloakMantleMath.AmplifierFactor(weapon, isPVP);

                // Reflect (Vanguard T3): the caster's own attack half, as in the life branch above
                ReflectAttackDamage = finalDamage * elementalDamageMod * slayerMod;
                ReflectWeaponResistanceMod = weaponResistanceMod;

                finalDamage *= elementalDamageMod * slayerMod * resistanceMod * absorbMod;

                // class abilities: flat void/war spell-damage multipliers (PvE only). Applies to radiated
                // Spell AOE children and Echo Cast wisp casts too, since those route through here as well.
                if (sourceCreature is Player classAbilityCaster && target is not Player)
                {
                    finalDamage *= classAbilityCaster.GetClassAbilitySpellDamageMod(Spell, SpellweaveDamageMod, IsEchoWispCast);

                    // FORK: Focus spell damage (magic_damage_per_focus_point), direct casts only - a proc, cascade,
                    // or AOE child is Secondary and never earns it. PvE only (the enclosing gate).
                    if (Player.QualifiesForFocusSpellDamage(IncomingDamageOrigin == IncomingDamageOrigin.DirectHit, target is Player, FromProc, IsClassAbilityProc))
                        finalDamage *= classAbilityCaster.GetFocusSpellDamageMod();

                    // Volatile Wall (Archmage T3): each landed projectile of one wall cast after the first hits
                    // harder (1 + priorLanded x b). Reads the cast's shared counter, then records this hit on it.
                    // 1.0 and no count for anything that is not a player wall cast.
                    finalDamage *= classAbilityCaster.ApplyVolatileWall(this, target);

                    // Elemental Proficiency (Archmage T3) instant damage effects: Fire x (1 + fire magnitude), and
                    // Pierce x (1 + pierce magnitude) when this hit's Pierce decision (the override call above)
                    // chose the flat shape over the resistance ignore. Applies to Spell AOE and Echo copies like
                    // any landed spell. 1.0 for rank 0 or an ineligible target.
                    finalDamage *= classAbilityCaster.GetElementalProficiencyDamageMod(Spell.DamageType, target, elementalPierceShape);

                    // Sanguine Reserve on a LIFE HEALTH BOLT (Bloodstone Bolt): a harmful life spell of
                    // MetaSpellType Projectile lands HERE, not in the LifeProjectile branch above, so it takes
                    // the Blood Charge ramp and grants its charge at this site. The CHARGE TERM ONLY, from this
                    // projectile's launch-time stamp - Blood Price is already in the multiplier above. Returns
                    // 1.0 and grants nothing for any war or void spell. Same one-charge-per-cast rule as
                    // Hecatomb: only the aimed, non-wisp projectile can grant.
                    finalDamage *= classAbilityCaster.ApplyLifeHealthBoltBloodCharge(Spell,
                        aimedNonEchoProjectile: GrantsLifeProjectileCharge(ProjectileTarget != null, IsEchoWispCast), castStamp: LifeProjectileStamp);

                    // (Runic Ward's war-spell spend used to be read here; the 2026-10-24 Spellsword pass
                    // removed it - the ward now drains only by absorbing incoming damage.)
                }
                // FORK: Focus spell damage for a caster summon (its own buffed Focus), under the player's gates: a
                // direct, non-proc hit on a non-player. Any other monster (pet or not) is 1.0 here and never enters.
                else if (sourceCreature is CombatPet { IsCasterPet: true } && target is not Player
                    && Player.QualifiesForFocusSpellDamage(IncomingDamageOrigin == IncomingDamageOrigin.DirectHit, false, FromProc, IsClassAbilityProc))
                {
                    finalDamage *= CombatPet.FocusSpellDamageModFor(sourceCreature);
                }

                // Tier B weapon mods: Ambush's spell half, read off the equipped WAND. Same PvE-only
                // condition, and the same reason this site rather than the hit site - it is the only place a
                // spell's damage can still be scaled. Returns 1.0 unless the target is at full health.
                if (sourceCreature is Player weaponModCaster && target is not Player)
                    finalDamage *= weaponModCaster.GetWeaponModSpellDamageMod(target);

                // PvP rules choke point AM1 (Docs/Pvp/DESIGN.md "Tunables"): pvp_arena_dmg_mod_1v1, scoped to
                // EXACTLY the war/void magic projectile branch - mirrors Doctide's SpellProjectile.cs "Apply
                // Arena 1v1 Dmg Mod" placement, which sits inside this same war/void-only else (never the Life
                // projectile branch above, and never Harm/Drain - those never reach SpellProjectile at all). A
                // no-op unless source and target are both players bound to the SAME Live 1v1 match.
                finalDamage = Pvp.Rules.PvpArenaOneVOneRules.ApplyDmgMod1v1(source, target, finalDamage);

                // AM1 for FFA: pvp_arena_dmg_mod_ffa, same war/void-only branch, for a SAME Live FFA match.
                finalDamage = Pvp.Rules.PvpArenaOneVOneRules.ApplyDmgModFfa(source, target, finalDamage);

                // AM1 for battlegrounds: pvp_bg_dmg_mod, same war/void-only branch, for a SAME Live battleground match of any mode.
                finalDamage = Pvp.Rules.PvpArenaOneVOneRules.ApplyDmgModBg(source, target, finalDamage);
            }

            // AM1 for the Tugak Brawl ring: pvp_arena_ffa_ring_dmg, OUTSIDE the war/void-vs-life branch split above so a ring of
            // ANY school scales (Curse of Raven Fury is a LIFE ring that the branch-scoped pvp_arena_dmg_mod_ffa never reaches).
            // A no-op for a non-ring projectile and outside a SAME Live FFA match; applied before the C2 cap in DamageTarget.
            finalDamage = Pvp.Rules.PvpArenaOneVOneRules.ApplyFfaRingDmg(source, target, SpellType == ProjectileSpellType.Ring, finalDamage);

            // AM1 for the battleground ring: pvp_bg_ring_dmg, same position and the same any-school ring rule, for a SAME Live battleground
            // match of any mode. Mutually exclusive with the Tugak ring call above (a match has one mode key), so no ring is scaled by both.
            finalDamage = Pvp.Rules.PvpArenaOneVOneRules.ApplyBgRingDmg(source, target, SpellType == ProjectileSpellType.Ring, finalDamage);

            // show debug info
            if (sourceCreature != null && sourceCreature.DebugDamage.HasFlag(Creature.DebugDamageType.Attacker))
            {
                ShowInfo(sourceCreature, Spell, attackSkill, criticalChance, criticalHit, critDefended, overpower, weaponCritDamageMod, skillBonus, baseDamage, critDamageBonus, elementalDamageMod, slayerMod, weaponResistanceMod, resistanceMod, absorbMod, LifeProjectileDamage, lifeMagicDamage, finalDamage);
            }
            if (target.DebugDamage.HasFlag(Creature.DebugDamageType.Defender))
            {
                ShowInfo(target, Spell, attackSkill, criticalChance, criticalHit, critDefended, overpower, weaponCritDamageMod, skillBonus, baseDamage, critDamageBonus, elementalDamageMod, slayerMod, weaponResistanceMod, resistanceMod, absorbMod, LifeProjectileDamage, lifeMagicDamage, finalDamage);
            }
            return finalDamage;
        }

        /// <summary>
        /// Reflect (Vanguard T3): the ATTACK half of this projectile's last CalculateDamage - the rolled base
        /// (or life damage), plus the critical bonus and skill bonus, times the caster's elemental and slayer
        /// mods, before the target's resistance and absorb touched it. Never persisted; written on every
        /// CalculateDamage that reaches the damage branches and read only by
        /// <see cref="CalculateReflectedDamage"/>, immediately after, on the same collision.
        /// </summary>
        public float ReflectAttackDamage { get; private set; }

        /// <summary>The caster's weapon resistance-cleaving mod from the same CalculateDamage (1.0 = none).</summary>
        public float ReflectWeaponResistanceMod { get; private set; } = 1.0f;

        /// <summary>
        /// Reflect (Vanguard T3): the damage this projectile deals to its own CASTER when a player bounces it.
        /// The caster's attack half is kept (<see cref="ReflectAttackDamage"/>, times the Spell AOE fraction
        /// and the caster's magic-damage ramp, <paramref name="rampMultiplier"/>); the defender half is
        /// resolved against the CASTER, mirroring CalculateDamage and DamageTarget: its elemental resistance
        /// (with the same weapon resistance-cleaving mod), its magic absorb, the caster's own damage rating
        /// against its own damage resist rating, and the crit damage / crit damage resist ratings on a crit.
        ///
        /// NO RESIST ROLL - the bounce is not a new cast, and the player-side roll was already won. PvE only
        /// (Player.TryReflectProjectile refuses a Player caster), so the rating scales resolve to the neutral
        /// identity and no PK term exists; they are read through the scaler anyway, like every rating read in
        /// this file.
        /// </summary>
        internal float CalculateReflectedDamage(Creature firer, bool critical, double rampMultiplier)
        {
            // an Invincible caster takes nothing, matching DamageEvent.CalculateReflectedMissile
            if (firer == null || firer.Invincible || ReflectAttackDamage <= 0.0f)
                return 0.0f;

            var reflectResistanceType = Creature.GetResistanceType(Spell.DamageType);

            var reflectResistanceMod = (float)Math.Max(0.0f, firer.GetResistanceMod(reflectResistanceType, this, null, ReflectWeaponResistanceMod));

            var reflectAbsorbMod = GetAbsorbMod(firer);

            var damage = ReflectAttackDamage * ClassAbilityAoeDamageMultiplier * (float)rampMultiplier * reflectResistanceMod * reflectAbsorbMod;

            var ratingScales = PvpRatingScales.Resolve(PvpChokePoint.R2, firer, firer);

            var reflectDamageRatingMod = Creature.GetPositiveRatingMod(ratingScales.ScaleDamageRating(firer.GetDamageRating()));

            var reflectDamageResistRatingMod = firer.GetDamageResistRatingMod(CombatType.Magic, true, firer, ratingScales.DamageRatingScale, out _, out _);

            if (critical)
            {
                reflectDamageRatingMod = Creature.AdditiveCombine(reflectDamageRatingMod, Creature.GetPositiveRatingMod(ratingScales.ScaleCritDamageRating(firer.GetCritDamageRating())));
                reflectDamageResistRatingMod = Creature.AdditiveCombine(reflectDamageResistRatingMod, Creature.GetNegativeRatingMod(ratingScales.ScaleCritDamageResistRating(firer.GetCritDamageResistRating())));
            }

            return damage * reflectDamageRatingMod * reflectDamageResistRatingMod;
        }

        public float GetAbsorbMod(Creature target)
        {
            return AbsorbModCore(target, target.CombatMode, this, false);
        }

        /// <summary>
        /// <see cref="GetAbsorbMod"/>'s arithmetic with the two live reads passed in, writing nothing: <paramref name="stance"/>
        /// in place of target.CombatMode, and <paramref name="withinShieldArc"/> in place of the projectile's angle (which only
        /// the shield path reads). Character Details reads it with an explicit facing.
        /// </summary>
        public static float AbsorbModFor(Creature target, CombatMode stance, bool withinShieldArc)
        {
            return AbsorbModCore(target, stance, null, withinShieldArc);
        }

        /// <summary>
        /// The one absorb switch. The shield arc comes from <paramref name="arcFrom"/>'s angle when a projectile is given (the
        /// live hit: computed only on the shield path, nothing allocated), else from <paramref name="withinShieldArc"/>.
        /// </summary>
        private static float AbsorbModCore(Creature target, CombatMode stance, SpellProjectile arcFrom, bool withinShieldArc)
        {
            switch (stance)
            {
                case CombatMode.NonCombat:

                    // shield_applies_in_noncombat: a peace-mode shield absorbs exactly as it does in melee stance
                    if (Creature.ShieldAppliesInNonCombat)
                    {
                        var peaceShield = target.GetEquippedShield();
                        if (peaceShield != null && peaceShield.GetAbsorbMagicDamage() != null)
                            return ShieldAbsorbMod(target, peaceShield, arcFrom != null ? arcFrom.IsWithinShieldArc(target) : withinShieldArc);
                    }

                    break;

                case CombatMode.Melee:

                    // does target have shield equipped?
                    var shield = target.GetEquippedShield();
                    if (shield != null && shield.GetAbsorbMagicDamage() != null)
                        return ShieldAbsorbMod(target, shield, arcFrom != null ? arcFrom.IsWithinShieldArc(target) : withinShieldArc);

                    break;

                case CombatMode.Missile:

                    var missileLauncherOrShield = target.GetEquippedMissileLauncher() ?? target.GetEquippedShield();
                    if (missileLauncherOrShield != null && missileLauncherOrShield.GetAbsorbMagicDamage() != null)
                        return AbsorbMagicFor(target, missileLauncherOrShield);

                    break;

                case CombatMode.Magic:

                    var caster = target.GetEquippedWand();
                    if (caster != null && caster.GetAbsorbMagicDamage() != null)
                        return AbsorbMagicFor(target, caster);

                    break;
            }
            return 1.0f;
        }

        /// <summary>is spell projectile in front of creature target, within shield effectiveness area?</summary>
        private bool IsWithinShieldArc(Creature target)
        {
            var effectiveAngle = 180.0f;
            var angle = target.GetAngle(this);
            return Math.Abs(angle) <= effectiveAngle / 2.0f;
        }

        /// <summary>
        /// Calculates the amount of damage a shield absorbs from magic projectile, the frontal-arc test passed in. Writes nothing.
        /// </summary>
        public static float ShieldAbsorbMod(Creature target, WorldObject shield, bool withinShieldArc)
        {
            if (!withinShieldArc)
                return 1.0f;

            // https://asheron.fandom.com/wiki/Shield
            // The formula to determine magic absorption for shields is:
            // Reduction Percent = (cap * specMod * baseSkill * 0.003f) - (cap * specMod * 0.3f)
            // Cap = Maximum reduction
            // SpecMod = 1.0 for spec, 0.8 for trained
            // BaseSkill = 100 to 433 (above 433 base shield you always achieve the maximum %)

            var shieldSkill = target.GetCreatureSkill(Skill.Shield);
            // ensure trained?
            if (shieldSkill.AdvancementClass < SkillAdvancementClass.Trained || shieldSkill.Base < 100)
                return 1.0f;

            var baseSkill = Math.Min(shieldSkill.Base, 433);
            var specMod = shieldSkill.AdvancementClass == SkillAdvancementClass.Specialized ? 1.0f : 0.8f;
            var cap = (float)(shield.GetAbsorbMagicDamage() ?? 0.0f);

            // speced, 100 skill = 0%
            // trained, 100 skill = 0%
            // speced, 200 skill = 30%
            // trained, 200 skill = 24%
            // speced, 300 skill = 60%
            // trained, 300 skill = 48%
            // speced, 433 skill = 100%
            // trained, 433 skill = 80%

            var reduction = (cap * specMod * baseSkill * 0.003f) - (cap * specMod * 0.3f);

            var shieldMod = Math.Min(1.0f, 1.0f - reduction);
            return shieldMod;
        }

        /// <summary>
        /// Calculates the damage reduction modifier for bows and casters
        /// with 'Magic Absorbing' property
        /// </summary>
        public static float AbsorbMagicFor(Creature target, WorldObject item)
        {
            // https://asheron.fandom.com/wiki/Category:Magic_Absorbing

            // Tomes and Bows
            // The formula to determine magic absorption for Tomes and the Fetish of the Dark Idols:
            // - For a 25% maximum item: (magic absorbing %) = 25 - (0.1 * (319 - base magic defense))
            // - For a 10% maximum item: (magic absorbing %) = 10 - (0.04 * (319 - base magic defense))

            // wiki currently has what is likely a typo for the 10% formula,
            // where it has a factor of 0.4 instead of 0.04
            // with 0.4, the 10% items would not start to become effective until base magic defense 294
            // with 0.04, both formulas start to become effective at base magic defense 69

            // using an equivalent formula that produces the correct results for 10% and 25%,
            // and also produces the correct results for any %

            var absorbMagicDamage = item.GetAbsorbMagicDamage();

            if (absorbMagicDamage == null)
                return 1.0f;

            var maxPercent = absorbMagicDamage.Value;

            var baseCap = 319;
            var magicDefBase = target.GetCreatureSkill(Skill.MagicDefense).Base;
            var diff = Math.Max(0, baseCap - magicDefBase);

            var percent = maxPercent - maxPercent * diff * 0.004f;

            return Math.Min(1.0f, 1.0f - (float)percent);
        }

        /// <summary>
        /// Called for a spell projectile to damage its target
        /// </summary>
        public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)
        {
            var targetPlayer = target as Player;

            if (targetPlayer != null && targetPlayer.Invincible || target.IsDead)
                return;

            // Open World Threads R12: a projectile writes the target's vitals directly, never through TakeDamage, so it
            // is judged here: a player (or a player's pet) past an outdoor run's circle deals nothing on impact.
            if (target.P_OutdoorPerimeter != null && target.RefusesOutdoorPerimeterDamage(ProjectileSource))
                return;

            var sourceCreature = ProjectileSource as Creature;
            var sourcePlayer = ProjectileSource as Player;

            // DamageTarget's only caller is OnCollideObject, after its target == ProjectileSource return, so a
            // self hit cannot reach here and this equals the old both-players test
            var pkBattle = PvpClassifier.IsPvp(ProjectileSource, target);

            var amount = 0u;
            var percent = 0.0f;

            // the hit as it stood going INTO the player pre-write class-ability mitigation, as a fraction of
            // max health; stays null for every other path (non-player target, non-health category), where the
            // cloak spell proc keeps rolling on `percent`
            float? cloakProcPercent = null;

            var damageRatingMod = 1.0f;
            var heritageMod = 1.0f;
            var sneakAttackMod = 1.0f;
            var critDamageRatingMod = 1.0f;
            var pkDamageRatingMod = 1.0f;

            var damageResistRatingMod = 1.0f;
            var critDamageResistRatingMod = 1.0f;
            var pkDamageResistRatingMod = 1.0f;

            WorldObject equippedCloak = null;

            // TRUE when the cloak spell proc was ROLLED (phase one) ahead of Thorns in the health branch and its cast
            // (Cloak.CompleteSpellProc) is still owed at the bottom - see the roll site for why they are split
            var cloakSpellProcRolled = false;

            // whether the target had a live ward up BEFORE AbsorbMonsterEffectDamage ran - set only on the
            // monster-target branch below, since a ward is a monster combat effect and never carried by a
            // Player
            var wardWasUp = false;

            // handle life projectiles for stamina / mana
            if (Spell.Category == SpellCategory.StaminaLowering)
            {
                percent = damage / target.Stamina.MaxValue;
                amount = (uint)-target.UpdateVitalDelta(target.Stamina, (int)-Math.Round(damage));

                // World Events combat contact (2026-10-04 follow-up): a landed stamina-lowering bolt never
                // writes health, so it never reaches DamageHistory.Add.
                ACE.Server.WorldEvents.WorldEventHooks.OnCombatContact(target, ProjectileSource);
            }
            else if (Spell.Category == SpellCategory.ManaLowering)
            {
                percent = damage / target.Mana.MaxValue;
                amount = (uint)-target.UpdateVitalDelta(target.Mana, (int)-Math.Round(damage));

                // World Events combat contact, as above, for a mana-lowering bolt.
                ACE.Server.WorldEvents.WorldEventHooks.OnCombatContact(target, ProjectileSource);
            }
            else
            {
                // for possibly applying sneak attack to magic projectiles,
                // only do this for health-damaging projectiles?
                if (sourcePlayer != null)
                {
                    // TODO: use target direction vs. projectile position, instead of player position
                    // could sneak attack be applied to void DoTs?
                    sneakAttackMod = sourcePlayer.GetSneakAttackMod(target);
                    //Console.WriteLine("Magic sneak attack:  + sneakAttackMod);
                    heritageMod = sourcePlayer.GetHeritageBonus(sourcePlayer.GetEquippedWand()) ? 1.05f : 1.0f;
                }

                // PvP rules choke point R2 (Docs/Pvp/DESIGN.md "PvP rules (levers)"): the rating-scale lever, resolved
                // once per projectile. On a PvP hit it scales the summed damage / damage resist ratings by
                // pvp_damage_rating_scale and the crit damage / crit damage resist ratings by
                // pvp_crit_damage_rating_scale, signed, BEFORE each is turned into a mod. The PK ratings are not
                // scaled. Neutral (the identity, no setting read) unless ProjectileSource and target are two
                // distinct players.
                var ratingScales = PvpRatingScales.Resolve(PvpChokePoint.R2, ProjectileSource, target);

                var damageRating = ratingScales.ScaleDamageRating(sourceCreature?.GetDamageRating() ?? 0);
                damageRatingMod = Creature.AdditiveCombine(Creature.GetPositiveRatingMod(damageRating), heritageMod, sneakAttackMod);

                damageResistRatingMod = target.GetDamageResistRatingMod(CombatType.Magic, true, ProjectileSource, ratingScales.DamageRatingScale, out var rawDamageResistRating, out var scaledDamageResistRating);

                ratingScales.ReportIfChanged(rawDamageResistRating, scaledDamageResistRating);

                if (critical)
                {
                    critDamageRatingMod = Creature.GetPositiveRatingMod(ratingScales.ScaleCritDamageRating(sourceCreature?.GetCritDamageRating() ?? 0));
                    critDamageResistRatingMod = Creature.GetNegativeRatingMod(ratingScales.ScaleCritDamageResistRating(target.GetCritDamageResistRating()));

                    damageRatingMod = Creature.AdditiveCombine(damageRatingMod, critDamageRatingMod);
                    damageResistRatingMod = Creature.AdditiveCombine(damageResistRatingMod, critDamageResistRatingMod);
                }

                if (pkBattle)
                {
                    pkDamageRatingMod = Creature.GetPositiveRatingMod(sourceCreature?.GetPKDamageRating() ?? 0);
                    pkDamageResistRatingMod = Creature.GetNegativeRatingMod(target.GetPKDamageResistRating());

                    damageRatingMod = Creature.AdditiveCombine(damageRatingMod, pkDamageRatingMod);
                    damageResistRatingMod = Creature.AdditiveCombine(damageResistRatingMod, pkDamageResistRatingMod);
                }

                damage *= damageRatingMod * damageResistRatingMod;

                // class ability: Soul Tether reduces damage to its owner's combat pet. Spell damage is
                // applied straight to the vital here rather than through Creature.TakeDamage, so the pet's
                // melee/missile site (CombatPet.TakeDamage) does not cover this path and the mod is read
                // again - live, so a rank learned mid-fight applies immediately.
                if (target is CombatPet soulTetherPet && !soulTetherPet.IsHuntersEcho)
                    damage *= soulTetherPet.P_PetOwner?.GetSoulTetherDamageReductionMod() ?? 1.0f;

                // Hunter's Echo: no Soul Tether (it is not a summon); its owner-equivalent resistance and magic damage
                // resist rating snapshot instead, the spell twin of its CombatPet.TakeDamage branch
                if (target is CombatPet huntersEchoTarget && huntersEchoTarget.IsHuntersEcho)
                    damage *= huntersEchoTarget.HuntersEchoMagicMitigation(Spell.DamageType);

                // Summoner casters (Docs/SummonerCasters/DESIGN.md): the live focus multiplier, read at the hit; the launcher
                // the bolt left from also triggers the wielder cut, so swapping the focus off mid-flight does not escape it
                LastAppliedWielderDamageMod = SummonerFocus.WielderDamageMod(ProjectileSource, target, ProjectileLauncher);
                damage *= SummonerFocus.HitMod(ProjectileSource, target, ProjectileLauncher);

                // Pinning Shot damage reduction rider, read site 2 of 4 (PinReductionMath documents all four): a monster
                // carrying a live pin reduction deals less spell damage to a player or a combat pet. Health branch only (the
                // stamina and mana branches above never reach here), applied ONCE per projectile, ahead of the
                // PvP caps, the cloak proc and the pre-write mitigations below. No rounding of its own.
                if (PinReductionMath.AppliesTo(sourceCreature, target))
                    damage *= sourceCreature.GetPinOutgoingDamageMod();

                // PvP rules choke point C2 (Docs/Pvp/DESIGN.md "PvP rules (levers)"): the per-hit damage cap, after
                // the ratings and before the cloak proc and the pre-write mitigations below, so those reduce the
                // capped hit and the reported `amount` is the capped number. Health branch only - a stamina/mana
                // drain bolt is not health damage. Each projectile (every bolt of a volley, ring or echo) reaches
                // here on its own. A no-op unless ProjectileSource and target are two distinct players.
                // AM1 (pvp_arena_dmg_mod_1v1) is applied in CalculateDamage, scoped to exactly the war/void
                // branch there (mirroring Doctide) - never here, since this point in DamageTarget sees every
                // health-branch projectile (war, void AND life) with no way to tell them apart any more.
                // PvP rules choke point M2: pvp_war_magic_damage_mod on a WAR projectile only (void and life are
                // PvpDamageKind.Other, so only the crit mod can touch them), times pvp_crit_damage_mod on a crit,
                // BEFORE the C2 cap so a raised mod can still be capped. A no-op unless this is a PvP pair.
                damage = PvpRules.ApplyDamageMods(PvpChokePoint.M2, ProjectileSource, target, damage, PvpRules.ProjectileDamageKind(Spell.School), critical, PvpHitProfile.ForSpell(Spell.School, SpellType));

                damage = PvpRules.ApplyDamageCap(PvpChokePoint.C2, ProjectileSource, target, damage);

                // Arena overtime damage ramp OT2 (Docs/Pvp/DESIGN.md "Overtime"): directly AFTER the C2 cap and
                // before the cloak proc below. A no-op outside an arena match in overtime with a ramp above 0.
                damage = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT2, ProjectileSource, target, damage);

                percent = damage / target.Health.MaxValue;

                //Console.WriteLine($"Damage rating: " + Creature.ModToRating(damageRatingMod));

                equippedCloak = target.EquippedCloak;

                if (equippedCloak != null && Cloak.HasDamageProc(equippedCloak) && Cloak.RollProc(equippedCloak, percent))
                {
                    var reducedDamage = Cloak.GetReducedAmount(ProjectileSource, target, damage);

                    Cloak.ShowMessage(target, ProjectileSource, damage, reducedDamage);

                    damage = reducedDamage;
                    percent = damage / target.Health.MaxValue;
                }

                // World Events Hold: a run monster's bolt on a player takes its role's (or the finale boss's) multiplier, before
                // the class barrier like the physical path's. 1 after one field read for everyone else.
                if (targetPlayer != null)
                {
                    var holdMult = ACE.Server.WorldEvents.WorldEventHooks.HoldPlayerDamageMult(sourceCreature);

                    if (holdMult != 1.0)
                    {
                        damage = (float)(damage * holdMult);
                        percent = damage / target.Health.MaxValue;
                    }
                }

                // Pre-write class ability mitigation (Sanguine Ward, then Mana Barrier). Spell damage never
                // reaches Player.TakeDamage, so a mitigation wired only there covers melee and missile but
                // not the spell damage a caster actually faces - which is exactly the bug both of these
                // were reported for on 2026-09-03. This is one of the five sites the hook must be called
                // from; do not "simplify" it away.
                //
                // A REDUCTION applied before the vital write. `damage` is genuinely replaced and `percent`
                // recomputed, so everything below (the vital write, the reported number, the
                // target.IsAlive / OnDeath branch) sees the reduced hit, exactly as the physical path in
                // Player.TakeDamage does. The ONE exception is the cloak spell proc, which rolls on
                // `cloakProcPercent` - the hit going INTO this call - so an absorbed hit still counts as a
                // hit for proc chance. The victim's damage line therefore reports the
                // POST-mitigation number, which is intended: it plus the "absorbs N points" lines add up to
                // the hit that was thrown.
                //
                // IT USED TO SIT BELOW THE VITAL WRITE AS A REFUND, AND THAT WAS A BUG - the write clamps
                // at zero, so on an overkill bolt the barrier was handed the victim's remaining health
                // instead of the damage thrown, and refunded a share of that on top of an emptied health
                // bar. Running above the write removes the clamped number from the problem entirely.
                //
                // Placed AFTER the cloak damage proc, matching the physical path's order, and inside the
                // health branch so a Stamina/Mana Lowering drain is never absorbed - the barrier trades
                // Mana for Health, which would be nonsense against a mana drain.
                if (targetPlayer != null)
                {
                    var beforeMitigation = (uint)Math.Round(damage);
                    cloakProcPercent = damage / target.Health.MaxValue;
                    var afterMitigation = targetPlayer.ApplyPreWriteDamageClassAbilities(ProjectileSource, Spell.DamageType, beforeMitigation, isSpell: true);

                    if (afterMitigation != beforeMitigation)
                    {
                        damage = afterMitigation;
                        percent = damage / target.Health.MaxValue;
                    }
                }

                // Attack/Defend engaged defenders (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"): the spell-projectile twin of the call at the
                // top of Creature.TakeDamage, which this path never reaches. A REDUCTION before the vital write like the mitigations around it,
                // and before the monster combat effects below, the same order as Creature.TakeDamage; a no-op for every target that is not a
                // match crystal.
                if (targetPlayer == null && damage > 0.0f)
                {
                    var beforeDefenderReduction = damage;

                    damage = target.ApplyBattlegroundDefenderReduction(ProjectileSource, damage);

                    if (damage != beforeDefenderReduction)
                        percent = damage / target.Health.MaxValue;
                }

                // World Events Hold: the ward on a warded ranged wave monster (a spell is magic), then a run monster's bolt on
                // a knight re-sized like its melee hits - the twin of the call in Creature.TakeDamage, which this path never
                // reaches. Both unchanged after one field read for every creature with no Hold tag.
                if (targetPlayer == null && damage > 0.0f)
                {
                    var beforeHold = damage;

                    damage = ACE.Server.WorldEvents.WorldEventHooks.HoldWardDamage(target, ACE.Server.WorldEvents.Defs.WorldEventCombatKinds.Magic, damage);
                    damage = ACE.Server.WorldEvents.WorldEventHooks.ScaleHoldKnightHit(target, ProjectileSource, damage);

                    // Allegiance Gauntlet: the Avatar fighter's cap on a war bolt, which never reaches Creature.TakeDamage.
                    damage = ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter.CapGauntletFighterHit(target, damage);

                    if (damage != beforeHold)
                        percent = damage / target.Health.MaxValue;
                }

                // Monster combat effects: the non-player mirror of the Sanguine Ward call above, in the same
                // slot and with the same convention - a REDUCTION before the vital write, so everything
                // below sees the filtered hit. Spell damage is written straight to Health here and never
                // reaches Creature.TakeDamage, so a monster ward wired only there would stop melee and
                // missile but not the spell damage a caster monster actually faces.
                if (targetPlayer == null && damage > 0.0f)
                {
                    var beforeMonsterEffects = (uint)Math.Round(damage);

                    // captured BEFORE the absorb call, which can zero the pool as a side effect of this very
                    // hit - see the doc comment on TryGetMonsterWardRemaining for why the order matters
                    wardWasUp = target.TryGetMonsterWardRemaining(out _);

                    var afterMonsterEffects = target.AbsorbMonsterEffectDamage(ProjectileSource, Spell.DamageType, beforeMonsterEffects, IncomingDamageOrigin);

                    if (afterMonsterEffects != beforeMonsterEffects)
                    {
                        damage = afterMonsterEffects;
                        percent = damage / target.Health.MaxValue;
                    }
                }

                // THE CLOAK SPELL PROC IS ROLLED HERE, AHEAD OF THE HEALTH WRITE AND AHEAD OF THORNS, and cast at the
                // bottom. Ahead of Thorns since the Vanguard pass (2026-11-06): a successful roll opens Cloaked in Power's
                // Critical Thorns window and this hit's own reflect must see it open. Ahead of the WRITE since Hunter's
                // Echo (2026-11-10): a winning roll may hand this bolt to the Echo before it reaches Health. The cast
                // keeps its place after the IsAlive check, so a killing bolt still never casts.
                //
                // The gate is the old post-write target.IsAlive taken one statement earlier: the write clamps at zero,
                // so health after it is above zero exactly when it is above Math.Round(damage) now - and for a
                // non-player with a P_HealthFloor, the floor rule UpdateVital applies is modelled too, so a floored boss
                // still rolls on an overkill exactly as before (HuntersEchoMath.ShouldRollCloakProc / WouldSurviveWrite).
                // Only a PLAYER target can reserve an Echo absorb.
                var canReserveEchoAbsorb = targetPlayer != null && equippedCloak != null && Cloak.HasProcSpell(equippedCloak) && targetPlayer.CanReserveHuntersEchoAbsorb(ProjectileSource, isHazard: false);

                cloakSpellProcRolled = equippedCloak != null && Cloak.HasProcSpell(equippedCloak) && HuntersEchoMath.ShouldRollCloakProc(target.Health.Current, damage, targetPlayer == null ? target.P_HealthFloor : 0u, canReserveEchoAbsorb)
                    && Cloak.RollSpellProc(target, equippedCloak, cloakProcPercent ?? percent);

                // Hunter's Echo cloak absorb: the Echo is summoned (or renewed) first and takes the bolt as the player
                // would have, already mitigated; only then is the bolt zeroed here. A failed summon leaves it as it was.
                if (HuntersEchoMath.ShouldAbsorb(cloakSpellProcRolled, canReserveEchoAbsorb) && Math.Round(damage) > 0
                    && targetPlayer.TryHuntersEchoAbsorb(ProjectileSource, Spell.DamageType, (uint)Math.Round(damage), equippedCloak))
                {
                    damage = 0.0f;
                    percent = 0.0f;
                }

                amount = (uint)-target.UpdateVitalDelta(target.Health, (int)-Math.Round(damage));

                // the damage actually applied to the vital, for the Elemental Proficiency Acid DoT sizing
                LastAppliedDamage = amount;

                target.DamageHistory.Add(ProjectileSource, Spell.DamageType, amount, thrown: (uint)Math.Round(damage), kind: DamageAttackKind.Magic, isCritical: critical, spellId: Spell.Id);

                // Thorns on a direct spell hit (user ruling 2026-10-07): after the health write, the same slot
                // the physical hook occupies in Player.TakeDamage, so it fires on a killing bolt and on one
                // mitigated to 0. ProjectileSource - the caster - is the attacker, never the projectile. The
                // origin, attacker filter and close-range gate are all inside; a Secondary projectile (proc,
                // Cascade hop, AOE splash) never triggers it. Health branch only, like the pre-write
                // mitigations above. (The cloak spell proc was rolled above the health write - see there.)
                targetPlayer?.ApplyThornsOnMagicHit(ProjectileSource, Spell.DamageType, IncomingDamageOrigin);

                // summon damage feed ("/summondamage"): spell damage is written to the vital directly here,
                // so it never reaches the Creature.TakeDamage hook that covers pet melee and missile. Inside
                // the Health branch deliberately - the StaminaLowering / ManaLowering categories above return
                // before this point, and a mana drain reported as "hits X for N damage!" would be a lie.
                //
                // Umbral Siphon / Empowered Summons leech ride the same site, gated on targetPlayer == null -
                // unlike the melee/missile sink above, `target` here can genuinely be a Player, and a pet
                // must never leech off damage dealt to a person.
                if (ProjectileSource is Pet castingPet)
                {
                    castingPet.NotifyOwnerOfDamage(target, (int)amount, critical);

                    if (targetPlayer == null)
                    {
                        castingPet.ApplyUmbralSiphonLeech((int)amount);
                        castingPet.ApplyEmpoweredSummonsLeech((int)amount);
                    }
                }

                // World Events measured boss damage scaling (TECH-DESIGN 2.16). Spell damage is written
                // straight to the vital here and never reaches Player.TakeDamage, so this site is not
                // redundant with the one there. It must sit BEFORE the "full amount for debugging"
                // reassignment of `amount` below, which overwrites the applied number with the pre-vital
                // roll.
                ACE.Server.WorldEvents.WorldEventHooks.NoteBossHit(ProjectileSource, targetPlayer, (int)amount, critical);

                //if (targetPlayer != null && targetPlayer.Fellowship != null)
                    //targetPlayer.Fellowship.OnVitalUpdate(targetPlayer);
            }

            amount = (uint)Math.Round(damage);    // full amount for debugging

            // show debug info
            if (sourceCreature != null && sourceCreature.DebugDamage.HasFlag(Creature.DebugDamageType.Attacker))
            {
                ShowInfo(sourceCreature, heritageMod, sneakAttackMod, damageRatingMod, damageResistRatingMod, critDamageRatingMod, critDamageResistRatingMod, pkDamageRatingMod, pkDamageResistRatingMod, damage);
            }
            if (target.DebugDamage.HasFlag(Creature.DebugDamageType.Defender))
            {
                ShowInfo(target, heritageMod, sneakAttackMod, damageRatingMod, damageResistRatingMod, critDamageRatingMod, critDamageResistRatingMod, pkDamageRatingMod, pkDamageResistRatingMod, damage);
            }

            if (target.IsAlive)
            {
                string verb = null, plural = null;
                Strings.GetAttackVerb(Spell.DamageType, percent, ref verb, ref plural);
                var type = Spell.DamageType.GetName().ToLower();

                var critMsg = critical ? "Critical hit! " : "";
                var sneakMsg = sneakAttackMod > 1.0f ? "Sneak Attack! " : "";
                var overpowerMsg = overpower ? "Overpower! " : "";

                var nonHealth = Spell.Category == SpellCategory.StaminaLowering || Spell.Category == SpellCategory.ManaLowering;

                if (sourcePlayer != null)
                {
                    var critProt = critDefended ? " Your critical hit was avoided with their augmentation!" : "";

                    var attackerMsg = $"{critMsg}{overpowerMsg}{sneakMsg}You {verb} {target.Name} for {amount} points with {Spell.Name}.{critProt}";

                    // could these crit / sneak attack?
                    if (nonHealth)
                    {
                        var vital = Spell.Category == SpellCategory.StaminaLowering ? "stamina" : "mana";
                        attackerMsg = $"With {Spell.Name} you drain {amount} points of {vital} from {target.Name}.";
                    }
                    else if (wardWasUp)
                    {
                        // the target had a live ward up before this hit resolved - say so, or a hit reported
                        // as "for 0 points" gives the caster no clue why. Health-damage line only: never the
                        // stamina/mana drain line above, never a Player target (a ward is a monster combat
                        // effect and TryGetMonsterWardRemaining already refuses one).
                        attackerMsg += WardEffect.FormatHitSuffix(target.TryGetMonsterWardRemaining(out var wardRemaining) ? wardRemaining : 0);
                    }

                    if (!sourcePlayer.SquelchManager.Squelches.Contains(target, ChatMessageType.Magic))
                        sourcePlayer.Session.Network.EnqueueSend(new GameMessageSystemChat(attackerMsg, ChatMessageType.Magic));
                }

                if (targetPlayer != null)
                {
                    var critProt = critDefended ? " Your augmentation allows you to avoid a critical hit!" : "";

                    var defenderMsg = $"{critMsg}{overpowerMsg}{sneakMsg}{ProjectileSource.Name} {plural} you for {amount} points with {Spell.Name}.{critProt}";

                    if (nonHealth)
                    {
                        var vital = Spell.Category == SpellCategory.StaminaLowering ? "stamina" : "mana";
                        defenderMsg = $"{ProjectileSource.Name} casts {Spell.Name} and drains {amount} points of your {vital}.";
                    }

                    if (!targetPlayer.SquelchManager.Squelches.Contains(ProjectileSource, ChatMessageType.Magic))
                        targetPlayer.Session.Network.EnqueueSend(new GameMessageSystemChat(defenderMsg, ChatMessageType.Magic));

                    if (sourceCreature != null)
                        targetPlayer.SetCurrentAttacker(sourceCreature);
                }

                if (!nonHealth)
                {
                    if (cloakSpellProcRolled)
                        Cloak.CompleteSpellProc(target, ProjectileSource, equippedCloak);

                    target.EmoteManager.OnDamage(sourcePlayer);

                    if (critical)
                        target.EmoteManager.OnReceiveCritical(sourcePlayer);
                }
            }
            else
            {
                var lastDamager = ProjectileSource != null ? new DamageHistoryInfo(ProjectileSource) : null;
                target.OnDeath(lastDamager, Spell.DamageType, critical);
                target.Die();
            }
        }

        /// <summary>
        /// Sets the physics state for a launched projectile
        /// </summary>
        public void SetProjectilePhysicsState(WorldObject target, bool useGravity)
        {
            if (useGravity)
                GravityStatus = true;

            CurrentMotionState = null;
            Placement = null;

            // TODO: Physics description timestamps (sequence numbers) don't seem to be getting updated

            //Console.WriteLine("SpellProjectile PhysicsState: " + PhysicsObj.State);

            var pos = Location.Pos;
            var rotation = Location.Rotation;
            PhysicsObj.Position.Frame.Origin = pos;
            PhysicsObj.Position.Frame.Orientation = rotation;

            var velocity = Velocity;
            //velocity = Vector3.Transform(velocity, Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(rotation)));
            PhysicsObj.Velocity = velocity;

            if (target != null)
                PhysicsObj.ProjectileTarget = target.PhysicsObj;

            PhysicsObj.set_active(true);
        }

        /// <summary>
        /// Fixed launch speed (m/s) for a Spell AOE secondary projectile - it only travels the short
        /// hop from the struck target to an adjacent neighbor, so a per-spell velocity lookup isn't worth it.
        /// </summary>
        private const float ClassAbilityAoeChildSpeed = 15.0f;

        /// <summary>
        /// The launch vector for a Spell AOE secondary projectile: from the struck target's chest
        /// (its feet plus 75% of its height, where <see cref="SpawnClassAbilityAoeChild"/> spawns the child)
        /// to the neighbor's mid-body (its feet plus half its height).
        ///
        /// <paramref name="positionalOffset"/> is the raw creature-to-creature offset from
        /// <see cref="ACE.Server.Physics.Common.Position.GetOffset"/>, whose Z is the ELEVATION difference
        /// between the two sets of feet. That term must be KEPT and the body-height correction ADDED to it.
        /// Assigning over it instead - which this did until 2026-08-15 - silently flattens every blast to the
        /// struck target's own ground plane, so on a ramp, a stairwell, or any sloped ground the child flies
        /// over or under a neighbor that the caller's (spherical) range check had already accepted, and the
        /// blast lands on nobody. On flat ground the elevation term is 0, which is why it looked correct.
        ///
        /// Pure so the Z handling is unit-testable without a live landblock, mirroring
        /// <see cref="ACE.Server.ClassAbilities.CrimsonHarvestMath"/>. Returns <see cref="Vector3.Zero"/> for a
        /// degenerate (unnormalizable) aim - two creatures occupying the same point - which the caller drops.
        /// </summary>
        internal static Vector3 GetAoeChildAimOffset(Vector3 positionalOffset, float primaryHeight, float neighborHeight)
        {
            var offset = positionalOffset;

            offset.Z += (neighborHeight / 2.0f) - (primaryHeight * 0.75f);

            if (offset.LengthSquared() < 0.0001f)
                return Vector3.Zero;

            return offset;
        }

        /// <summary>
        /// Class ability "Spell AOE": launches one secondary copy of this spell from the struck target
        /// (<paramref name="primaryTarget"/>) at a neighboring creature, so it reads visually as the
        /// same spell radiating outward from the target. The child is attributed to the original
        /// <paramref name="caster"/> and runs the normal collision/damage path against its own target -
        /// an independent full-damage hit with its own resistance and crit rolls. It is flagged so it
        /// neither re-triggers the blast nor fires item procs. The caller owns target selection (range,
        /// filters); this method owns only the projectile mechanics.
        /// </summary>
        public void SpawnClassAbilityAoeChild(Player caster, Creature primaryTarget, Creature neighbor, float damageMult)
        {
            if (caster == null || primaryTarget?.PhysicsObj == null || neighbor?.PhysicsObj == null || Spell == null)
                return;

            // Aim first: a degenerate (zero-length) aim vector is not launchable, and resolving it before
            // anything is created keeps that case from leaking a world object / guid.
            var offset = GetAoeChildAimOffset(
                primaryTarget.PhysicsObj.Position.GetOffset(neighbor.PhysicsObj.Position),
                primaryTarget.Height,
                neighbor.Height);

            if (offset == Vector3.Zero)
                return;

            var spellType = GetProjectileSpellType(Spell.Id);

            var sp = WorldObjectFactory.CreateNewWorldObject(Spell.Wcid) as SpellProjectile;
            if (sp == null)
                return;

            sp.Setup(Spell, spellType);

            sp.IsClassAbilityAoeChild = true;   // never cascade into further blasts
            sp.IsClassAbilitySpawned = true;    // general no-cascade guard: never radiates further
            InheritEchoWispMark(sp, this);       // a child of a wisp cast keeps the wisp rules
            sp.SuppressPetAssist = true;         // aimed at neighbor, not what the caster targeted - must not steal pet assist
            sp.FromProc = true;               // never re-fire item procs
            sp.ClassAbilityAoeDamageMultiplier = damageMult;   // secondary blasts land softer

            // This child is built here rather than through LaunchSpellProjectiles, so its per-cast stamps
            // would otherwise default (SpellweaveDamageMod to 1.0, losing the bonus the parent's cast paid
            // for; LifeProjectileDamage to 0, which lands a life-projectile spell like Martyr's Hecatomb for
            // 0 damage). It inherits the PARENT's captured stamps - the cast that actually launched this -
            // never the live player fields.
            CopyPerCastStamps(sp, this);

            sp.ProjectileSource = caster;
            sp.ProjectileTarget = neighbor;
            sp.ProjectileLauncher = ProjectileLauncher;
            sp.ProjectileAmmo = ProjectileAmmo;
            sp.IsWeaponSpell = IsWeaponSpell;

            // spawn at the struck target's upper body
            var origin = new Position(primaryTarget.Location);
            var originPos = origin.Pos;
            originPos.Z += primaryTarget.Height * 0.75f;
            origin.Pos = originPos;
            sp.Location = origin;

            var dir = Vector3.Normalize(offset);
            sp.PhysicsObj.Velocity = dir * ClassAbilityAoeChildSpeed;

            sp.PhysicsObj.Position.Frame.set_vector_heading(dir);
            sp.Location.Rotation = sp.PhysicsObj.Position.Frame.Orientation;

            sp.SetProjectilePhysicsState(neighbor, false);
            sp.SpawnPos = new Position(sp.Location);

            if (!LandblockManager.AddObject(sp))
            {
                sp.Destroy();
                return;
            }

            if (sp.WorldEntryCollision)
                return;

            sp.EnqueueBroadcast(new GameMessageScript(sp.Guid, PlayScript.Launch, sp.GetProjectileScriptIntensity(spellType)));
        }

        public static void ShowInfo(Creature observed, Spell spell, CreatureSkill skill, float criticalChance, bool criticalHit, bool critDefended, bool overpower, float weaponCritDamageMod,
            float magicSkillBonus, int baseDamage, float critDamageBonus, float elementalDamageMod, float slayerMod,
            float weaponResistanceMod, float resistanceMod, float absorbMod,
            float lifeProjectileDamage, float lifeMagicDamage, float finalDamage)
        {
            var observer = PlayerManager.GetOnlinePlayer(observed.DebugDamageTarget);
            if (observer == null)
            {
                observed.DebugDamage = Creature.DebugDamageType.None;
                return;
            }

            var info = $"Skill: {skill.Skill.ToSentence()}\n";
            info += $"CriticalChance: {criticalChance}\n";
            info += $"CriticalHit: {criticalHit}\n";

            if (critDefended)
                info += $"CriticalDefended: {critDefended}\n";

            info += $"Overpower: {overpower}\n";
        
            if (spell.MetaSpellType == ACE.Entity.Enum.SpellType.LifeProjectile)
            {
                // life magic projectile
                info += $"LifeProjectileDamage: {lifeProjectileDamage}\n";
                info += $"DamageRatio: {spell.DamageRatio}\n";
                info += $"LifeMagicDamage: {lifeMagicDamage}\n";
            }
            else
            {
                // war/void projectile
                var difficulty = Math.Min(spell.Power, 350);
                info += $"Difficulty: {difficulty}\n";

                if (magicSkillBonus != 0.0f)
                    info += $"SkillBonus: {magicSkillBonus}\n";

                info += $"BaseDamageRange: {spell.MinDamage} - {spell.MaxDamage}\n";
                info += $"BaseDamage: {baseDamage}\n";
                info += $"DamageType: {spell.DamageType}\n";
            }

            if (weaponCritDamageMod != 1.0f)
                info += $"WeaponCritDamageMod: {weaponCritDamageMod}\n";

            if (critDamageBonus != 0)
                info += $"CritDamageBonus: {critDamageBonus}\n";

            if (elementalDamageMod != 1.0f)
                info += $"ElementalDamageMod: {elementalDamageMod}\n";

            if (slayerMod != 1.0f)
                info += $"SlayerMod: {slayerMod}\n";

            if (weaponResistanceMod != 1.0f)
                info += $"WeaponResistanceMod: {weaponResistanceMod}\n";

            if (resistanceMod != 1.0f)
                info += $"ResistanceMod: {resistanceMod}\n";

            if (absorbMod != 1.0f)
                info += $"AbsorbMod: {absorbMod}\n";

            //observer.Session.Network.EnqueueSend(new GameMessageSystemChat(info, ChatMessageType.Broadcast));
            observer.DebugDamageBuffer += info;
        }

        public static void ShowInfo(Creature observed, float heritageMod, float sneakAttackMod, float damageRatingMod, float damageResistRatingMod,
            float critDamageRatingMod, float critDamageResistRatingMod, float pkDamageRatingMod, float pkDamageResistRatingMod, float damage)
        {
            var observer = PlayerManager.GetOnlinePlayer(observed.DebugDamageTarget);
            if (observer == null)
            {
                observed.DebugDamage = Creature.DebugDamageType.None;
                return;
            }
            var info = "";

            if (heritageMod != 1.0f)
                info += $"HeritageMod: {heritageMod}\n";

            if (sneakAttackMod != 1.0f)
                info += $"SneakAttackMod: {sneakAttackMod}\n";

            if (critDamageRatingMod != 1.0f)
                info += $"CritDamageRatingMod: {critDamageRatingMod}\n";

            if (pkDamageRatingMod != 1.0f)
                info += $"PkDamageRatingMod: {pkDamageRatingMod}\n";

            if (damageRatingMod != 1.0f)
                info += $"DamageRatingMod: {damageRatingMod}\n";

            if (critDamageResistRatingMod != 1.0f)
                 info += $"CritDamageResistRatingMod: {critDamageResistRatingMod}\n";

            if (pkDamageResistRatingMod != 1.0f)
                info += $"PkDamageResistRatingMod: {pkDamageResistRatingMod}\n";

            if (damageResistRatingMod != 1.0f)
                info += $"DamageResistRatingMod: {damageResistRatingMod}\n";

            info += $"Final damage: {damage}";

            observer.Session.Network.EnqueueSend(new GameMessageSystemChat(observer.DebugDamageBuffer + info, ChatMessageType.Broadcast));

            observer.DebugDamageBuffer = null;
        }
    }
}
