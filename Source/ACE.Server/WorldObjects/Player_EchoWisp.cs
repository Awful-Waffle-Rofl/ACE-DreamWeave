using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// The Echo Cast wisp cast now in progress, null at every other moment. The RAW latch: read sites never
        /// read it, they read <see cref="ActiveEchoWispCast"/>, which honours it only on the thread that armed it.
        /// Only <see cref="CastAsEchoWisp"/>'s reentrancy guard and <see cref="RunWithEchoWispSuspended{TArg, TResult}"/> touch
        /// it directly.
        ///
        /// LIFETIME. Armed ONLY inside one synchronous <see cref="CastAsEchoWisp"/> call and cleared in its finally,
        /// the <see cref="ClassAbilityProcCastActive"/> latch shape: read while the cast is being resolved and its
        /// projectiles BUILT, so there is no in-flight drift. It is SUSPENDED (reads null) across each projectile's
        /// LandblockManager.AddObject: a spawn that overlaps something - a Strike spell spawns on its target -
        /// collides and resolves its whole impact inside AddObject, and that impact's procs (weapon cast-on-strike,
        /// spell-hit abilities) must launch from the player with the player's own stamps, exactly as at the impact
        /// of a projectile that flew. Anything that must still know "this came from a wisp" at impact time reads
        /// SpellProjectile.IsEchoWispCast, stamped BEFORE AddObject.
        ///
        /// Read sites (with the latch null every one of them is exactly the pre-wisp code path):
        ///  - WorldObject.ProjectileGeometrySource: projectile origins, heading, velocity, line of sight, and the
        ///    caster effect in HandleCastSpell;
        ///  - HandleCastSpell: skips OnSpellweaveCast;
        ///  - HandleCastSpell_Projectile: the snapshotted Life basis instead of a vital drain;
        ///  - LaunchSpellProjectiles: stamps IsEchoWispCast, InheritsProcCasterSkill and the neutral launch stamps;
        ///  - ApplyHarmClassAbilityDamage and the Drain Blood Charge grant: the one-shot terms neutral.
        ///
        /// Not part of ResetClassAbilityTransientState, for the same reason as the proc latch: nothing outside the
        /// synchronous call can observe it armed.
        /// </summary>
        public EchoWispCastScope EchoWispCast { get; private set; }

        /// <summary>
        /// The latch as every read site sees it: <see cref="EchoWispCast"/> on the thread that armed it, null on
        /// any other thread (<see cref="EchoWispCastScope.ActiveOn"/>). No read site compares thread ids itself.
        /// With the latch null - every ordinary cast - it returns at once, without reading the thread id.
        /// </summary>
        public EchoWispCastScope ActiveEchoWispCast
        {
            get
            {
                var scope = EchoWispCast;
                if (scope == null)
                    return null;

                return EchoWispCastScope.ActiveOn(scope, Environment.CurrentManagedThreadId);
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/>(<paramref name="arg"/>) with the wisp latch suspended (null), restoring it
        /// in a finally even when the action throws. Used around LaunchSpellProjectiles' AddObject, where an
        /// overlapping spawn's impact runs synchronously - see the lifetime note on <see cref="EchoWispCast"/>.
        /// Takes the argument separately so the caller can pass a static lambda and capture nothing.
        /// </summary>
        internal TResult RunWithEchoWispSuspended<TArg, TResult>(TArg arg, Func<TArg, TResult> action)
        {
            var suspended = EchoWispCast;
            EchoWispCast = null;
            try
            {
                return action(arg);
            }
            finally
            {
                EchoWispCast = suspended;
            }
        }

        /// <summary>
        /// Casts <paramref name="spell"/> at <paramref name="target"/> as this player's own first-hand cast, launched
        /// from <paramref name="scope"/>'s Origin (an Echo Cast wisp). Mirrors the private CreatePlayerSpell body with
        /// no item caster and isWeaponSpell false - the resist roll, NonProjectileMagicImmune, HandleCastSpell,
        /// proficiency, target procs, and DoCastSpell_Inner's self-proc line - and skips everything a wisp cast does
        /// not pay or perform: mana, components, wind-up, the windup move cap, Blood Price, and the cast-school
        /// bookkeeping (LastSuccessCast_School/Time, which feeds the War/Void switch penalty on the player's OWN
        /// next cast).
        ///
        /// THE LATCH COVERS ONLY THE CAST ITSELF. Proficiency and the item procs run after it is cleared, so an item
        /// proc fired by a wisp's debuff behaves exactly as the same proc fired at a wisp projectile's impact (when
        /// the latch is long gone): launched by the player, from the player, with the player's live stamps.
        ///
        /// NEVER A PLAYER TARGET FOR A HARMFUL SPELL (class abilities never reach PvP), and not reentrant: a call made
        /// while a wisp cast is already armed is refused rather than nesting, because the inner finally would disarm the
        /// outer cast. The ONE exception is a Restore spell (Heal, Revitalize; owner, 2026-11-23), which lands ONLY on a
        /// player, and only on this player or a member of its fellowship who is in no PvP state
        /// (<see cref="EchoWispMath.CastTargetRefusal"/>, <see cref="EchoWispEngagement.IsPvpSafeRestoreTarget"/>).
        /// A heal on a fellow is not PvP; nothing harmful can take that arm, because Restore is beneficial by definition.
        ///
        /// Called only by <see cref="EchoWisp"/>, and only from the OWNER's action queue (EchoWisp.CastOnOwnerThread), so
        /// the latch is only ever armed on this player's own thread.
        /// </summary>
        internal EchoWispCastOutcome CastAsEchoWisp(EchoWispCastScope scope, Spell spell, Creature target)
        {
            // the RAW latch: armed on any thread means another cast owns it, so refuse rather than overwrite it
            if (scope?.Origin == null || spell == null)
                return Refuse(scope, "no origin or spell");

            if (EchoWispCast != null)
                return Refuse(scope, "another wisp cast is armed");

            // the origin must be a live world object: every launch reads its physics position and landblock
            if (scope.Origin.PhysicsObj == null || scope.Origin.CurrentLandblock == null)
                return Refuse(scope, "the wisp body is not in the world");

            var restore = EchoWispMath.IsRestore(spell.MetaSpellType, spell.IsBeneficial, EchoWisp.RestoreVitalOf(spell));

            var restoreTarget = target as Player;

            var targetRefusal = EchoWispMath.CastTargetRefusal(restore, target != null, restoreTarget != null,
                restoreTarget != null && EchoWispEngagement.IsCasterOrFellow(restoreTarget, this),
                restore && restoreTarget != null && EchoWispEngagement.IsPvpSafeRestoreTarget(restoreTarget, this));

            if (targetRefusal != null)
                return Refuse(scope, targetRefusal);

            // rings and walls launch untargeted; every other wisp cast needs a creature to land on
            if (target == null && !spell.IsProjectile)
                return Refuse(scope, "no target for a non-projectile spell");

            if (target != null && CheckPKStatusVsTarget(target, spell) != null)
                return Refuse(scope, "PK status");

            if (spell.MetaSpellType == SpellType.Dispel && !VerifyDispelPKStatus(this, target))
                return Refuse(scope, "dispel PK status");

            var caster = GetEquippedWand();

            // the launcher: a proc wisp's sword while the owner still holds it, else the wand (item procs keep the wand)
            var launcher = EchoWispLauncher(scope.ProcWeapon, caster);

            EchoWispCastOutcome outcome;

            scope.ArmingThreadId = Environment.CurrentManagedThreadId;
            EchoWispCast = scope;

            try
            {
                if (spell.School == MagicSchool.ItemEnchantment)
                {
                    // nothing for it to land on (no enchantable shield or named weapon): the target is done for this
                    // wisp, which judges "carries it" on the same item (ItemEnchantmentLandingItem)
                    if (ItemEnchantmentLandingItem(spell, target) == null)
                        outcome = EchoWispCastOutcome.Immune;
                    else
                    {
                        // resolves its own resist roll against the target (or its wielder) and cannot report it
                        TryCastItemEnchantment_WithRedirects(spell, target, null);
                        outcome = EchoWispCastOutcome.Cast;
                    }
                }
                else
                {
                    outcome = EchoWispCastOutcome.Cast;

                    if (!spell.IsProjectile)
                    {
                        // a Restore lands on this player or a fellow, never a monster (CreatePlayerSpell skips it for a player target)
                        if (!restore)
                            OnAttackMonster(target);

                        if (TryResistSpell(target, spell, null, casterSkillOverride: scope.InheritsProcCasterSkill ? Skill.ItemEnchantment : (Skill?)null))
                            outcome = EchoWispCastOutcome.Resisted;
                        else if (target.NonProjectileMagicImmune)
                        {
                            Session.Network.EnqueueSend(new GameMessageSystemChat($"You fail to affect {target.Name} with {spell.Name}", ChatMessageType.Magic));
                            outcome = EchoWispCastOutcome.Immune;
                        }
                    }

                    if (outcome == EchoWispCastOutcome.Cast && !HandleCastSpell(spell, target, null, launcher, false))
                    {
                        outcome = EchoWispCastOutcome.Refused;
                        scope.RefusedBecause = "HandleCastSpell refused (no live target, Threads perimeter, or an unhandled spell type)";
                    }
                }
            }
            finally
            {
                EchoWispCast = null;
            }

            if (outcome == EchoWispCastOutcome.Refused)
                return outcome;

            if (spell.School == MagicSchool.ItemEnchantment)
            {
                if (outcome == EchoWispCastOutcome.Cast)
                    Proficiency.OnSuccessUse(this, GetCreatureSkill(Skill.ItemEnchantment), spell.PowerMod);
            }
            else if (outcome == EchoWispCastOutcome.Cast && !spell.IsProjectile)
            {
                if (spell.IsHarmful)
                {
                    Proficiency.OnSuccessUse(this, GetCreatureSkill(spell.School), target.GetCreatureSkill(Skill.MagicDefense).Current);

                    // handle target procs
                    TryProcEquippedItems(this, target, false, caster);
                }
                else
                    Proficiency.OnSuccessUse(this, GetCreatureSkill(spell.School), spell.PowerMod);
            }

            // handle self procs (DoCastSpell_Inner: a harmful cast not aimed at the caster; a wisp never aims at it)
            if (spell.IsHarmful)
                TryProcEquippedItems(this, this, true, caster);

            return outcome;
        }

        /// <summary>Records why a wisp cast was refused on its scope (the per-cast DEBUG line) and returns Refused.</summary>
        private static EchoWispCastOutcome Refuse(EchoWispCastScope scope, string reason)
        {
            if (scope != null)
                scope.RefusedBecause = reason;

            return EchoWispCastOutcome.Refused;
        }

        // ---- the wisps (plan step 3) ---------------------------------------------------------------------

        /// <summary>
        /// This player's live Echo Cast wisps, keyed by body guid. Concurrent because a wisp ends on its body's thread
        /// (EchoWisp.Watch) as well as on this player's (a cast's re-check, logout); spawns happen only on this player's.
        /// </summary>
        private readonly ConcurrentDictionary<uint, EchoWisp> echoWisps = new ConcurrentDictionary<uint, EchoWisp>();

        /// <summary>How many wisps this player has out right now.</summary>
        public int ActiveEchoWispCount => echoWisps.Count;

        internal void AddEchoWisp(EchoWisp wisp)
        {
            if (wisp?.Body != null)
                echoWisps[wisp.Body.Guid.Full] = wisp;
        }

        internal void RemoveEchoWisp(EchoWisp wisp)
        {
            if (wisp?.Body != null)
                echoWisps.TryRemove(new System.Collections.Generic.KeyValuePair<uint, EchoWisp>(wisp.Body.Guid.Full, wisp));
        }

        /// <summary>
        /// The qualifying-cast gate of <see cref="TryTriggerEchoWisp"/> (<see cref="EchoWispMath.Qualifies"/>), on its own so a
        /// test can drive it without a live landblock: a harmful allowlisted spell at a non-player, non-pet creature (or an
        /// untargeted ring or wall), or a Restore spell at this player or a fellow in no PvP state
        /// (<see cref="EchoWispEngagement.IsCasterOrFellow"/> and <see cref="EchoWispEngagement.IsPvpSafeRestoreTarget"/>,
        /// the cast guard's own predicates). A beneficial Self spell arrives untargeted and is re-aimed at this player
        /// through <paramref name="target"/>.
        /// </summary>
        internal bool EchoWispTriggerQualifies(Spell spell, ref WorldObject target)
        {
            // a Self spell is aimed at the caster: the hand cast arrives untargeted (HandleCastSpell resolves it to the caster)
            if (spell.IsSelfTargeted && !spell.IsHarmful)
                target = this;

            var vital = EchoWisp.RestoreVitalOf(spell);

            return EchoWispMath.Qualifies(spell.MetaSpellType, spell.IsHarmful, spell.IsProjectile, target != null, target is Creature, target is Player, target is Pet,
                vital, target is Player casterOrFellow && EchoWispEngagement.IsCasterOrFellow(casterOrFellow, this) && EchoWispEngagement.IsPvpSafeRestoreTarget(casterOrFellow, this));
        }

        /// <summary>
        /// Where this player's live wisps stand, in the frame of <paramref name="casterPosition"/> (the caster's physics
        /// position), so a new wisp's spawn can skip a spot one of them holds (<see cref="EchoWispMath.IsSpotTaken"/>).
        /// GetOffset measures across a landblock seam too. A wisp that has ended, or whose body is not in the world, holds
        /// no spot. Wisps never move after spawning, so the read needs no lock.
        /// </summary>
        internal List<System.Numerics.Vector3> EchoWispSpots(ACE.Server.Physics.Common.Position casterPosition)
        {
            var spots = new List<System.Numerics.Vector3>();

            if (casterPosition == null)
                return spots;

            foreach (var wisp in echoWisps.Values)
            {
                var physics = wisp.Body?.PhysicsObj;

                if (wisp.IsEnded || physics?.Position == null || wisp.Body.IsDestroyed)
                    continue;

                spots.Add(casterPosition.Frame.Origin + casterPosition.GetOffset(physics.Position));
            }

            return spots;
        }

        /// <summary>Ends every live wisp with <paramref name="reason"/> (logout). Runs on this player's thread.</summary>
        internal void EndAllEchoWisps(string reason)
        {
            foreach (var wisp in echoWisps.Values.ToList())
                wisp.End(reason, onBodyThread: false);
        }

        /// <summary>
        /// The hand-cast Life damage basis note (plan section 3): armed by DoCastSpell_Inner around ONE committed hand
        /// cast, written by HandleCastSpell_Projectile with the basis it drained (first write wins, so a proc projectile
        /// cast synchronously inside the same call cannot overwrite it), and taken back by the trigger.
        /// </summary>
        internal EchoWispBasisNote EchoWispBasis { get; } = new EchoWispBasisNote();

        /// <summary>
        /// The launcher of one wisp cast (<see cref="EchoWispMath.Launcher"/>): <paramref name="procWeapon"/> while this
        /// player still owns it (wielded or in its packs, OwnsHuntersEchoItem - the Hunter's Echo test), else
        /// <paramref name="handCastLauncher"/>.
        /// </summary>
        internal WorldObject EchoWispLauncher(WorldObject procWeapon, WorldObject handCastLauncher) =>
            EchoWispMath.Launcher(procWeapon, procWeapon != null && OwnsHuntersEchoItem(procWeapon), handCastLauncher);

        /// <summary>
        /// This player's Echo Cast rank as every class ability reads it: TryGetClassAbility, so 0 with
        /// class_abilities_enabled off, or while the PK facet rule or a PvP arena mask suppresses class abilities.
        /// </summary>
        internal bool TryGetEchoCastRank(out int rank) => TryGetClassAbility(ClassAbilityId.EchoCast, out rank);

        /// <summary>
        /// Rolls Echo Cast for one committed cast and, on a success, summons a wisp carrying <paramref name="spell"/>
        /// (design "The wisp"). Called from exactly two kinds of site, both on this player's own thread and neither
        /// reachable from a projectile impact: DoCastSpell_Inner's Success branch (a hand cast) and the three Spellsword
        /// proc handlers (Spellblade, Runeblade, Spellstorm), straight after CastClassAbilityProc. A wisp cast never
        /// passes either, which is what makes "a wisp's cast never echoes" structural; the latch test below is a backstop.
        ///
        /// A Spellsword proc passes its <paramref name="procDamageScale"/> (Nether parity x power scale) and
        /// <paramref name="rendSourceDamageType"/> (Volatile Elements), so the wisp repeats the proc faithfully.
        ///
        /// Gates, in order: no wisp cast is armed; the player holds Echo Cast (<see cref="TryGetEchoCastRank"/>); the enabled switch; the spell qualifies
        /// (<see cref="EchoWispMath.Qualifies"/>: harmful, allowlisted type, never at a player or a pet; or a Restore spell at
        /// this player or a fellow, a Self spell counting as aimed at this player); under the cap;
        /// then the chance roll (<see cref="EchoCastAbility.EchoChance"/>, today's ladder). Returns the wisp or null.
        /// </summary>
        internal EchoWisp TryTriggerEchoWisp(Spell spell, WorldObject target, uint lifeProjectileDamage, bool inheritsProcCasterSkill,
            float procDamageScale = 1.0f, DamageType? rendSourceDamageType = null, WorldObject procWeapon = null)
        {
            if (EchoWispCast != null || spell == null)
                return null;

            // the suppressed rank read: honours class_abilities_enabled and the PK facet / arena mask
            if (!TryGetEchoCastRank(out var rank))
                return null;

            if (!PropertyManager.GetBool("class_ability_echocast_wisp_enabled").Item)
                return null;

            if (!EchoWispTriggerQualifies(spell, ref target))
                return null;

            if (!EchoWispMath.UnderCap(echoWisps.Count, PropertyManager.GetLong("class_ability_echocast_wisp_max_active").Item))
                return null;

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > EchoCastAbility.ChanceFor(this, rank))
                return null;

            return EchoWisp.TrySpawn(this, spell, target as Creature, lifeProjectileDamage, inheritsProcCasterSkill, procDamageScale, rendSourceDamageType, procWeapon,
                PropertyManager.GetDouble("class_ability_echocast_wisp_duration_seconds").Item,
                PropertyManager.GetDouble("class_ability_echocast_wisp_interval_min_seconds").Item,
                PropertyManager.GetDouble("class_ability_echocast_wisp_interval_max_seconds").Item,
                PropertyManager.GetDouble("class_ability_echocast_wisp_translucency").Item);
        }
    }
}
