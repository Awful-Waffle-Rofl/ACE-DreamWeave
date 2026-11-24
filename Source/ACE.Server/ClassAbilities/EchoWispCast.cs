using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// One Echo Cast wisp cast (Archmage T3 rework, Docs/ClassAbilities/ARCHMAGE-ECHO-CAST-WISP-DESIGN.md), armed
    /// on the casting player as <see cref="Player.EchoWispCast"/> for exactly one synchronous call of
    /// <see cref="Player.CastAsEchoWisp"/> and null at every other moment.
    ///
    /// A WISP CAST IS A FIRST-HAND CAST BY THE PLAYER. The player stays the caster everywhere that matters
    /// (SpellProjectile.ProjectileSource, damage history, kill credit, resist rolls, procs) and FromProc stays
    /// false. The scope changes only three things, each read where it applies:
    ///  - the launch geometry: projectiles leave <see cref="Origin"/>, not the player
    ///    (WorldObject.ProjectileGeometrySource), and the cast's caster effect plays on it;
    ///  - the one-shot, paid-for per-cast terms are neutral: no mana, vital, Blood Price, Spellweave, Blood Charge
    ///    spend or grant, Adrenaline (SpellProjectile.IsEchoWispCast carries this past the synchronous call to
    ///    impact time, when the latch is no longer armed);
    ///  - a Life projectile carries <see cref="LifeProjectileDamage"/>, the ORIGINAL cast's damage basis, instead
    ///    of draining the player again.
    ///
    /// Immutable apart from <see cref="ArmingThreadId"/> and the three diagnostic fields the cast fills in for the
    /// wisp's per-cast DEBUG line (<see cref="RefusedBecause"/>, <see cref="ProjectilesLaunched"/>,
    /// <see cref="ProjectilesBlocked"/>): a wisp builds one per cast (EchoWisp.CastOnOwnerThread). None of the three
    /// is read by any rule; they only report what the cast did.
    /// </summary>
    public sealed class EchoWispCastScope
    {
        public EchoWispCastScope(WorldObject origin, uint lifeProjectileDamage, bool inheritsProcCasterSkill,
            float procDamageScale = 1.0f, DamageType? rendSourceDamageType = null, WorldObject procWeapon = null)
        {
            Origin = origin;
            LifeProjectileDamage = lifeProjectileDamage;
            InheritsProcCasterSkill = inheritsProcCasterSkill;
            ProcDamageScale = procDamageScale;
            RendSourceDamageType = rendSourceDamageType;
            ProcWeapon = procWeapon;
        }

        /// <summary>
        /// The weapon a Spellsword proc wisp's proc launched from (DamageEvent.Weapon at the proc site), null for a hand-cast
        /// wisp. Its casts launch with it while the owner still holds it, so its rend, slayer and crit frequency apply as on
        /// the proc itself (orchestrator ruling on #2301); otherwise they use what a hand-cast wisp uses (Player.EchoWispLauncher).
        /// </summary>
        public WorldObject ProcWeapon { get; }

        /// <summary>
        /// The Spellsword proc's damage scale (Nether parity x power-scaled proc damage) for a wisp spawned off a proc,
        /// 1.0 for every other wisp. Stamped on the wisp's projectiles as SpellProjectile.ProcDamageScale (orchestrator
        /// ruling on #2301: a proc wisp repeats the proc faithfully, as the legacy echo did through CopyPerCastStamps).
        /// </summary>
        public float ProcDamageScale { get; }

        /// <summary>The proc's Volatile Elements original damage type (SpellProjectile.RendSourceDamageType), null otherwise.</summary>
        public DamageType? RendSourceDamageType { get; }

        /// <summary>The wisp body the cast launches from: projectile origins, heading, velocity and line of sight.</summary>
        public WorldObject Origin { get; }

        /// <summary>The Life projectile damage basis snapshotted from the cast that spawned the wisp (0 for any other spell).</summary>
        public uint LifeProjectileDamage { get; }

        /// <summary>
        /// TRUE for a wisp spawned off a Spellsword class-ability proc: its casts read the proc's caster skill (Item
        /// Enchantment) for the resist roll, imbue scaling and skill damage bonus, carried to the projectiles through
        /// SpellProjectile.InheritsProcCasterSkill. Never IsClassAbilityProc, which is also
        /// Cascade's gate.
        /// </summary>
        public bool InheritsProcCasterSkill { get; }

        /// <summary>
        /// The managed thread id that armed this scope, stamped by <see cref="Player.CastAsEchoWisp"/> just before it
        /// arms the latch (0 until then). The latch is honoured only on this thread (<see cref="ActiveOn"/>), so a
        /// read of Player.EchoWispCast from any other thread - a landblock tick that is not this cast's - sees null.
        /// Defence in depth: step 3 also runs every wisp cast through the owner's action queue.
        /// </summary>
        public int ArmingThreadId { get; internal set; }

        /// <summary>
        /// Why <see cref="Player.CastAsEchoWisp"/> refused this cast (null unless its outcome is Refused). Diagnostic only.
        /// </summary>
        public string RefusedBecause { get; internal set; }

        /// <summary>
        /// Projectiles this cast put into the world (WorldObject.LaunchSpellProjectiles, after a successful AddObject).
        /// Diagnostic only: a Cast outcome with 0 here means the spell was handled but nothing flew.
        /// </summary>
        public int ProjectilesLaunched { get; internal set; }

        /// <summary>
        /// Of <see cref="ProjectilesLaunched"/>, those culled at once because the wisp could not see them
        /// (WorldObject.IsProjectileVisible failed, so they hit the environment on the spot). Diagnostic only.
        /// </summary>
        public int ProjectilesBlocked { get; internal set; }

        /// <summary>
        /// The thread rule behind Player.ActiveEchoWispCast, the ONE accessor every read site goes through:
        /// <paramref name="scope"/> when it was armed on <paramref name="currentThreadId"/>, else null. Pure so the
        /// rule is unit-testable.
        /// </summary>
        public static EchoWispCastScope ActiveOn(EchoWispCastScope scope, int currentThreadId)
        {
            return scope != null && scope.ArmingThreadId == currentThreadId ? scope : null;
        }
    }

    /// <summary>
    /// The hand-cast Life damage basis handed to a wisp (plan section 3): commit-armed, first write wins. DoCastSpell_Inner
    /// arms it around one committed hand cast; HandleCastSpell_Projectile notes the basis that cast drained; the trigger
    /// takes it back (0 when nothing was noted: a war, void or non-projectile spell). The first write wins so a proc
    /// projectile cast synchronously inside the same call - an overlapping spawn's impact - cannot overwrite it, and a
    /// note outside an armed window is ignored. Touched only on the owning player's thread.
    /// </summary>
    public sealed class EchoWispBasisNote
    {
        private bool armed;
        private uint? noted;

        public void Arm()
        {
            armed = true;
            noted = null;
        }

        public void Note(uint lifeProjectileDamage)
        {
            if (armed && noted == null)
                noted = lifeProjectileDamage;
        }

        public uint Take()
        {
            var basis = noted ?? 0;
            armed = false;
            noted = null;
            return basis;
        }
    }

    /// <summary>What one <see cref="Player.CastAsEchoWisp"/> call did, for the wisp's stay-or-move-on rule.</summary>
    public enum EchoWispCastOutcome
    {
        /// <summary>The spell was cast (a projectile launched, or the non-projectile effect was applied).</summary>
        Cast,

        /// <summary>The target resisted a non-projectile spell.</summary>
        Resisted,

        /// <summary>The target cannot take the spell at all (NonProjectileMagicImmune).</summary>
        Immune,

        /// <summary>Nothing was cast: an invalid call, a player target, an origin not in the world, a PK refusal or a cast the handler refused.</summary>
        Refused,
    }
}
