using System;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The combat, reward and consumable hooks that feed a rep's <see cref="TrialRecorder"/>. Each call site gates on ONE field
    /// read first (the trial creature's <see cref="Creature.P_TrialRep"/>, or the player's <see cref="Player.TrialSession"/>), so
    /// a hit, grant or use outside a trial costs that read and nothing else; these methods run only past the gate.
    ///
    /// An event is recorded only into the recorder of the rep it belongs to: a creature-side event into its creature's rep, a
    /// player-side event into the player's session's current rep, and an event between the two only when both name the same
    /// rep. A rep whose run row is written has a null Recorder, so a late event is dropped. Every hook swallows and logs its own
    /// throw: a recorder fault must never break combat.
    ///
    /// Threads: creature-side and combat events run on the room landblock's thread; player-side events run on the player's
    /// queue. The two never run at the same time (LandblockManager ticks the multi-threaded phase, then the single-threaded
    /// one). XP is applied on the recipient's own queue, but a fellow's luminance share calls GrantLuminance directly
    /// (Fellowship.SplitLuminance), so luminance can arrive on another landblock's thread; the recorder's grant accumulators
    /// are Interlocked for that reason.
    /// </summary>
    internal static class TrialHooks
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Test seam: the clock every recorded event is stamped with (the session clock).</summary>
        internal static Func<double> Now = TrialManager.Now;

        internal static void ResetForTests() => Now = TrialManager.Now;

        /// <summary>The live recorder of <paramref name="player"/>'s running rep, or null.</summary>
        private static TrialRecorder RunningRecorder(Player player)
        {
            var session = player?.TrialSession;

            if (session == null || session.Phase != TrialPhase.Running)
                return null;

            return session.CurrentRep?.Recorder;
        }

        /// <summary>True when <paramref name="player"/> is bound to the session whose current rep is <paramref name="rep"/>.</summary>
        private static bool PlaysRep(Player player, TrialRep rep)
            => rep != null && ReferenceEquals(player?.TrialSession?.CurrentRep, rep);

        // ================================================================ combat

        /// <summary>
        /// DamageHistory.Add(WorldObject, DamageType, uint), the one method both Add overloads pass through, before its zero
        /// return (a landed hit a ward absorbed to 0 is still a hit). Hit out: the victim is a trial creature and the attacker is
        /// not one. Hit in: the victim is a player bound to the rep of the trial creature that hit it.
        /// v1 limit: every damage record counts, so a damage-over-time tick is a hit too.
        /// </summary>
        public static void OnDamageRecorded(Creature victim, WorldObject attacker, DamageType damageType, uint amount)
        {
            try
            {
                var rep = victim?.P_TrialRep;

                if (rep != null)
                {
                    if (attacker is Creature { P_TrialRep: not null })
                        return;

                    rep.Recorder?.OnHitOut(victim.Guid.Full, victim.WeenieClassId, victim.P_TrialRole, damageType, amount, Now());
                    return;
                }

                if (victim is Player player && attacker is Creature creature && creature.P_TrialRep is TrialRep attackerRep && PlaysRep(player, attackerRep))
                    attackerRep.Recorder?.OnHitIn(creature.WeenieClassId, creature.P_TrialRole, damageType, amount, Now());
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnDamageRecorded threw for 0x{victim?.Guid.Full:X8}", ex);
            }
        }

        /// <summary>Player.DamageTarget's no-damage branch: the player's own swing or missile at a trial creature missed.</summary>
        public static void OnMissOut(Player attacker, Creature target, CombatType combatType)
        {
            try
            {
                var rep = target?.P_TrialRep;

                if (rep == null || !PlaysRep(attacker, rep))
                    return;

                rep.Recorder?.OnMissOut(target.WeenieClassId, target.P_TrialRole, combatType, Now());
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnMissOut threw for 0x{target?.Guid.Full:X8}", ex);
            }
        }

        /// <summary>The top of Player.OnEvade: the trial player evaded a trial creature's melee or missile attack.</summary>
        public static void OnEvadeIn(Player player, WorldObject attacker, CombatType combatType)
        {
            try
            {
                if (attacker is not Creature creature || creature.P_TrialRep is not TrialRep rep || !PlaysRep(player, rep))
                    return;

                rep.Recorder?.OnEvadeIn(creature.WeenieClassId, creature.P_TrialRole, combatType, Now());
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnEvadeIn threw for 0x{player?.Guid.Full:X8}", ex);
            }
        }

        /// <summary>
        /// TryResistSpell's resisted branch, both directions. Out: a trial creature resisted a spell from anyone but another
        /// trial creature. In: the trial player resisted a spell cast by a creature of its rep.
        /// </summary>
        public static void OnSpellResisted(Creature casterCreature, Creature target, uint spellId)
        {
            try
            {
                var targetRep = target?.P_TrialRep;

                if (targetRep != null)
                {
                    if (casterCreature?.P_TrialRep != null)
                        return;

                    targetRep.Recorder?.OnResist(true, target.WeenieClassId, target.P_TrialRole, spellId, Now());
                    return;
                }

                if (target is Player player && casterCreature?.P_TrialRep is TrialRep casterRep && PlaysRep(player, casterRep))
                    casterRep.Recorder?.OnResist(false, casterCreature.WeenieClassId, casterCreature.P_TrialRole, spellId, Now());
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnSpellResisted threw for 0x{target?.Guid.Full:X8}", ex);
            }
        }

        // ================================================================ rewards

        /// <summary>
        /// OnDeath_GrantXP's per-player loop, for a trial creature: the rounded XP and luminance share it paid
        /// <paramref name="player"/>. Kept on the creature for the kill event (TrialManager.OnTrialCreatureDied); only the trial's
        /// own player counts.
        /// </summary>
        public static void OnKillAward(Creature creature, Player player, long xp, long lum)
        {
            try
            {
                if (creature?.P_TrialRep is not TrialRep rep || !PlaysRep(player, rep))
                    return;

                creature.P_TrialKillXp += xp;
                creature.P_TrialKillLum += lum;
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnKillAward threw for 0x{creature?.Guid.Full:X8}", ex);
            }
        }

        /// <summary>
        /// Player.UpdateXpAndLevel, beside AnalyticsManager.RecordXp: the XP actually added. Not gated on the Running phase: the
        /// last kill's XP is applied through the player's queue after the rep has ended, and the run row is written on that same
        /// queue behind it (TrialRunLog), so it still counts. Dropped once the run row is written.
        /// </summary>
        public static void OnXpGranted(Player player, long amount)
        {
            try
            {
                player?.TrialSession?.CurrentRep?.Recorder?.OnXpGranted(amount);
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnXpGranted threw for 0x{player?.Guid.Full:X8}", ex);
            }
        }

        /// <summary>Player.AddLuminance, beside AnalyticsManager.RecordLuminance: the luminance actually added. Same rule as XP.</summary>
        public static void OnLumGranted(Player player, long amount)
        {
            try
            {
                player?.TrialSession?.CurrentRep?.Recorder?.OnLumGranted(amount);
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnLumGranted threw for 0x{player?.Guid.Full:X8}", ex);
            }
        }

        // ================================================================ consumables and procs

        /// <summary>A consumable used during a running rep: food or potion (Food.ApplyConsumable), healing kit (Healer.DoHealing), mana stone (ManaStone).</summary>
        public static void OnConsume(Player player, uint itemWcid)
        {
            try
            {
                RunningRecorder(player)?.OnConsume(itemWcid, Now());
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnConsume threw for 0x{player?.Guid.Full:X8}", ex);
            }
        }

        /// <summary>Player.CastClassAbilityProc: a class-ability proc cast during a running rep. The site does not know the spell.</summary>
        public static void OnProc(Player player)
        {
            try
            {
                RunningRecorder(player)?.OnProc(null, Now());
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnProc threw for 0x{player?.Guid.Full:X8}", ex);
            }
        }

        // ================================================================ session-side

        /// <summary>TrialManager's 0.5 s vital sample, on the room thread: routed to the running rep's recorder.</summary>
        public static void OnVitalSample(TrialSession session, TrialManager.VitalSample sample)
        {
            if (session == null || sample == null || session.Phase != TrialPhase.Running)
                return;

            session.CurrentRep?.Recorder?.OnVitals(sample.Health, sample.Stamina, sample.Mana, sample.At);
        }

        /// <summary>
        /// The room-thread half of a trial kill (TrialManager.OnTrialCreatureDied): the kill event with the award captured by
        /// OnKillAward, then the session advances. Nothing happens for a creature of an earlier rep.
        /// </summary>
        public static void RecordKillAndAdvance(TrialSession session, TrialRep rep, uint guid, uint wcid, ThreadDungeons.DungeonRole role, long xp, long lum, double now)
        {
            if (session == null || !ReferenceEquals(session.CurrentRep, rep))
                return;

            if (session.Phase == TrialPhase.Running)
                rep.Recorder?.OnKill(guid, wcid, role, xp, lum, now);

            session.OnCreatureDied(guid, now);
        }
    }
}
