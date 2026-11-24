using System;
using System.IO;
using System.Text;
using System.Text.Json;

using log4net;

using ACE.Server.CombatSimulator;
using ACE.Server.WorldObjects;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The player half of every spawn prediction, built ONCE per session from the OFFLINE master (the rep character is a copy of
    /// it): the master's primary attack and its defender profile. Null (no <see cref="TrialPlayerSide"/>) means the session runs
    /// without predictions. The attack is built with no target, so target-dependent terms (slayers) are not in it.
    /// </summary>
    public sealed record TrialPlayerSide(AttackerSpec Attack, DefenderProfile Defense, string Note);

    /// <summary>
    /// What the combat simulator predicts for the fight against one trial creature, made at spawn, after the creature is shaped
    /// and before it enters the world. PlayerHitsToKill is the player's landed hits to kill the creature (MonsterCombatSimulator);
    /// CreatureHitsToKillPlayer and CreatureHitChance are the creature's landed hits to kill the player and its hit chance
    /// (CombatSimulator). Both come straight from the analytic simulators; nothing here re-derives them. A non-finite number
    /// (zero damage: infinite hits to kill) is written as JSON null.
    /// </summary>
    public sealed record TrialPrediction(float PlayerHitsToKill, float CreatureHitsToKillPlayer, float CreatureHitChance, string Mode, string Note)
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string PlayerSource = "master_offline";

        /// <summary>The prediction, or null when there is none (no player side, or the creature has no attack).</summary>
        public static TrialPrediction For(Creature c, TrialPlayerSide side) => For(c, side, out _);

        /// <summary>
        /// Mode follows the engine (Monster.IsRanged): a creature wielding a missile weapon attacks with missile, else melee
        /// when it has a melee weapon or a combat body part, else there is no prediction and <paramref name="note"/> says why.
        /// The creature's combat mode is restored before returning, whatever happens.
        /// </summary>
        public static TrialPrediction For(Creature c, TrialPlayerSide side, out string note)
        {
            note = null;

            if (c == null)
                throw new ArgumentNullException(nameof(c));

            if (side == null)
            {
                note = "no player side";
                return null;
            }

            var previous = c.CombatMode;

            try
            {
                // Mode-only stance writes: the spec path's real SetCombatMode would also set CurrentMotionState, move
                // LastWeaponSwap and (missile) queue a reload chain on a creature that is about to enter the world.
                var (result, why) = AttackerTerms.WithModeOnlyStance(() =>
                {
                    var r = Measure(c, side, out var n);
                    return (r, n);
                });

                note = why;
                return result;
            }
            finally
            {
                c.SetCombatModeForSimulator(previous);
            }
        }

        private static TrialPrediction Measure(Creature c, TrialPlayerSide side, out string note)
        {
            note = null;

            string mode;

            if (c.GetEquippedMissileWeapon() != null)
                mode = "missile";
            else if (c.GetEquippedMeleeWeapon() != null || CreatureAttackerSpecs.CombatAttackParts(c).Count > 0)
                mode = "melee";
            else
            {
                note = $"wcid {c.WeenieClassId} has no melee weapon, combat body part or missile weapon";
                return null;
            }

            var attacker = mode == "missile" ? CreatureAttackerSpecs.Missile(c) : CreatureAttackerSpecs.Melee(c);

            var onCreature = MonsterCombatSimulator.Analytic(side.Attack, MonsterDefenderProfile.Build(c));
            var onPlayer = ACE.Server.CombatSimulator.CombatSimulator.Analytic(attacker, side.Defense);

            note = side.Note;

            return new TrialPrediction(onCreature.LandedHitsToKill, onPlayer.LandedHitsToKill, onPlayer.HitChance, mode, side.Note);
        }

        private static void Num(Utf8JsonWriter w, string name, float v)
        {
            if (float.IsFinite(v))
                w.WriteNumber(name, Math.Round((double)v, 4));
            else
                w.WriteNull(name);
        }

        /// <summary>The closed key set: mode, playerHitsToKill, creatureHitsToKillPlayer, creatureHitChance, playerSource, note.</summary>
        public string ToJson()
        {
            using var ms = new MemoryStream();

            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("mode", Mode);
                Num(w, "playerHitsToKill", PlayerHitsToKill);
                Num(w, "creatureHitsToKillPlayer", CreatureHitsToKillPlayer);
                Num(w, "creatureHitChance", CreatureHitChance);
                w.WriteString("playerSource", PlayerSource);

                if (Note == null)
                    w.WriteNull("note");
                else
                    w.WriteString("note", Note);

                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>The value for a trial_run pred_* column: null when the number is not finite.</summary>
        internal static double? Column(float v) => float.IsFinite(v) ? v : null;
    }
}
