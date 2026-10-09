using ACE.Entity.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// An EXTERNALLY TOGGLED damage immunity: while the carrying creature's ephemeral
    /// <see cref="Creature.P_DigsiteImmune"/> flag is set, every point of incoming damage is filtered to
    /// zero; while it is clear, this effect is exactly a pass-through and costs one bool read per hit.
    ///
    /// WHY THIS EXISTS AT ALL, GIVEN PropertyBool.Invincible. Invincible does NOT stop spell damage on a
    /// monster. The melee/missile check is unconditional (DamageEvent: `if (defender.Invincible) return
    /// 0.0f;`), but BOTH spell-side checks gate on the target being a PLAYER
    /// (SpellProjectile.DamageTarget's `targetPlayer != null &amp;&amp; targetPlayer.Invincible`, and the same
    /// shape in its life-magic sibling), so a boss set Invincible would still be killed by war magic during
    /// an "immune" phase. IMonsterIncomingDamage is the one filter in this codebase deliberately wired at
    /// all THREE damage sinks - Creature.TakeDamage, SpellProjectile.DamageTarget and
    /// WorldObject.HandleCastSpell_Boost (see AbsorbMonsterEffectDamage's own doc comment) - which is
    /// precisely the coverage an immunity needs.
    ///
    /// WHY THE FLAG IS NOT IN MonsterEffectState. The phase that turns it on is owned by something outside
    /// this creature entirely: the digsite driver, which watches the boss's health, spawns the adds whose
    /// deaths end the phase, and runs the failsafe timeout. No hook in this framework fires on another
    /// creature's death, and MonsterEffectState is a deliberately fixed six-field struct. So the effect owns
    /// the FILTER and the driver owns the DECISION, and they meet at one ephemeral bool.
    ///
    /// WHY THE RECORD IS ATTACHED AT SPAWN AND NEVER MID-FIGHT. Creature.ApplyMonsterEffectOverlay allocates
    /// a fresh state array and its own doc comment says to call it before the creature enters the world;
    /// bolting an immunity on at 85% health would silently reset every other effect the boss carries. The
    /// record therefore ships inert with the boss and is switched on in place.
    ///
    /// DispatchOrder is DamageMutator: this CHANGES the figure every reader on the same hook sizes itself
    /// from (a leech healing off the damage, a reflect answering it), so it has to run ahead of them - and
    /// returning 0 first is what makes "immune" mean immune rather than "immune, but the reflect still fires".
    ///
    /// AUTHORING: `immune`, optionally `immune silent=true`. There are no required args; an immune record on
    /// a creature nothing ever toggles is permanently inert, which is exactly what it should be.
    /// </summary>
    public sealed class ImmuneEffect : IMonsterEffect, IMonsterIncomingDamage
    {
        public string Kind => "immune";

        /// <summary>
        /// Runs ahead of every reader on its hook: this effect CHANGES the damage figure they size themselves
        /// from. See the ordering paragraph in MonsterEffectHooks.cs.
        /// </summary>
        public int DispatchOrder => MonsterEffectDispatchOrder.DamageMutator;

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            // The only authorable arg. Checked rather than ignored because GetBool is total and would read a
            // typo ("silent=ture") as the default, leaving an author sure they had silenced something they
            // had not.
            if (spec.Has("silent") && spec.GetBool("silent", true) != spec.GetBool("silent", false))
            {
                error = "immune requires silent= to be true/false (or 1/0, yes/no)";
                return false;
            }

            error = null;
            return true;
        }

        public uint OnIncomingDamage(Creature defender, WorldObject source, DamageType damageType, uint amount, IncomingDamageOrigin origin,
            MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            // THE COMMON PATH, and the only cost this effect imposes on a boss that is not currently immune:
            // one field read. Deliberately first, before any arg is touched.
            if (defender == null || !defender.P_DigsiteImmune)
                return amount;

            if (amount == 0)
                return 0;

            Announce(defender, source, spec, ref state);

            return 0;
        }

        /// <summary>
        /// Unless silent=true, tells the attacking player once per this creature's lifetime (state.Announced)
        /// that their damage is doing nothing. A player whose damage is being zeroed with no feedback reads it
        /// as the server being broken, not as a mechanic - and the driver's own phase announcement only
        /// reaches whoever was inside the digsite audience radius when the phase started.
        ///
        /// "Once" here means once for as long as this effect's state persists (the creature's life), not once
        /// per phase: there is no fight-boundary hook to reset it against, the same limit ManaBarrierEffect's
        /// and WardEffect's own announcements already live with.
        /// </summary>
        private static void Announce(Creature defender, WorldObject source, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (state.Announced || spec.GetBool("silent", false))
                return;

            state.Announced = true;

            if (!(source is Player sourcePlayer) || sourcePlayer.Session == null)
                return;

            if (sourcePlayer.SquelchManager.Squelches.Contains(defender, ChatMessageType.CombatEnemy))
                return;

            sourcePlayer.Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"{defender.Name} is impervious - your attack does nothing!", ChatMessageType.CombatEnemy));
        }
    }
}
