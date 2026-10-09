using System;

using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// The L2 rating-scale lever (Docs/Pvp/DESIGN.md "PvP rules (levers)"), resolved ONCE per direct hit at choke
    /// points R1 (DamageEvent) and R2 (SpellProjectile).
    ///
    /// On a PvP hit with pvp_rules_enabled on, it scales the SUMMED integer ratings BEFORE they are converted to a
    /// damage multiplier: damage rating and damage resist rating by pvp_damage_rating_scale, crit damage rating and
    /// crit damage resist rating by pvp_crit_damage_rating_scale. The scaled rating is
    /// <c>(int)Math.Round(rating * scale)</c> on the SIGNED rating, so a net-negative rating (a void-DoT-drained
    /// DRR, a Weakness-drained damage rating) stays negative and only shrinks toward zero - it is never turned
    /// into protection the way a `Math.Abs(ModToRating(mod))` approach would. A DEFENDER's net-negative resist
    /// rating is also never deepened by a scale above 1.0 (<see cref="ScaleResistRating"/>).
    ///
    /// NOT scaled: the PK damage / PK damage resist ratings (they are PvP-only already), DoT ticks (they never pass
    /// R1/R2), stamina / mana drain bolts (no ratings).
    ///
    /// Everything else - PvE, self, pets, the master switch off - gets <see cref="Neutral"/>, whose scalers are the
    /// identity and which reads no setting: <see cref="Resolve"/> classifies BEFORE it reads the dials.
    /// </summary>
    public readonly struct PvpRatingScales
    {
        /// <summary>The identity: not applied, both scales 1.0, every Scale* returns its input and reports nothing.</summary>
        public static readonly PvpRatingScales Neutral = new PvpRatingScales(null, 1.0, 1.0);

        /// <summary>The choke point this lever applies at, or null when it does not apply to this hit.</summary>
        public PvpChokePoint? Point { get; }

        private readonly double damageRatingScale;
        private readonly double critDamageRatingScale;

        /// <summary>
        /// pvp_damage_rating_scale for this hit; 1.0 whenever the lever is not applied, INCLUDING
        /// default(PvpRatingScales), whose backing field is 0.0 - the getter self-guards so a default struct handed
        /// to the DRR overload is the identity, never a zeroing scale.
        /// </summary>
        public double DamageRatingScale => Point.HasValue ? damageRatingScale : 1.0;

        /// <summary>pvp_crit_damage_rating_scale for this hit; 1.0 whenever not applied (self-guarded as above).</summary>
        public double CritDamageRatingScale => Point.HasValue ? critDamageRatingScale : 1.0;

        /// <summary>TRUE for a PvP hit with the master switch on - the lever ran, even if both scales are 1.0.</summary>
        public bool Applied => Point.HasValue;

        private PvpRatingScales(PvpChokePoint? point, double damageRatingScale, double critDamageRatingScale)
        {
            Point = point;
            this.damageRatingScale = damageRatingScale;
            this.critDamageRatingScale = critDamageRatingScale;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for R1 / R2: classifies (<paramref name="source"/>, <paramref name="target"/>) first and
        /// reads the dials once, only for a PvP pair. Returns <see cref="Neutral"/> for a non-PvP pair or with
        /// pvp_rules_enabled off.
        /// </summary>
        public static PvpRatingScales Resolve(PvpChokePoint point, WorldObject source, Creature target)
        {
            var interaction = PvpClassifier.Classify(source, target);

            if (!interaction.IsPvp)
                return Neutral;

            return Resolve(point, interaction, PvpRules.ReadDials());
        }

        /// <summary>The lever for an already-classified interaction and an already-read snapshot.</summary>
        public static PvpRatingScales Resolve(PvpChokePoint point, PvpInteraction interaction, PvpRuleDials dials)
        {
            if (!interaction.IsPvp || dials == null || !dials.Enabled)
                return Neutral;

            return new PvpRatingScales(point, dials.DamageRatingScale, dials.CritDamageRatingScale);
        }

        /// <summary>
        /// PURE. <c>(int)Math.Round(rating * scale)</c>, sign kept: a negative rating scales toward zero and stays
        /// negative (or reaches 0), it never flips. A scale of exactly 1.0 returns <paramref name="rating"/> itself
        /// (int * 1.0 is exact in double, so this is the identity for every int even without the short-circuit).
        /// Guards, since the setting is not range-checked where it is read: a NaN or infinite scale leaves the
        /// rating unchanged, a negative scale counts as 0 (it would otherwise invert the rating's sign), and the
        /// product is clamped to the int range.
        /// </summary>
        public static int ScaleRating(int rating, double scale)
        {
            if (scale == 1.0 || rating == 0)
                return rating;

            if (double.IsNaN(scale) || double.IsInfinity(scale))
                return rating;

            if (scale < 0.0)
                scale = 0.0;

            var scaled = Math.Round(rating * scale);

            if (scaled >= int.MaxValue)
                return int.MaxValue;

            if (scaled <= int.MinValue)
                return int.MinValue;

            return (int)scaled;
        }

        /// <summary>
        /// PURE. <see cref="ScaleRating"/> for a DEFENDER's resist rating (damage resist, crit damage resist). A
        /// net-negative resist rating is never deepened: its scale is capped at 1.0 (min(scale, 1.0)), so it can
        /// only shrink toward zero. Orchestrator ruling: the fork's steep negative curve (allow_negative_rating_curve,
        /// 100 / (100 + r) clamped at -99) would turn a -40 DRR at scale 3 into -99, a 100x damage multiplier. A
        /// zero or positive resist rating scales exactly as <see cref="ScaleRating"/>.
        /// </summary>
        public static int ScaleResistRating(int rating, double scale)
        {
            if (rating < 0 && scale > 1.0)
                scale = 1.0;

            return ScaleRating(rating, scale);
        }

        /// <summary>Damage rating (attacker) through pvp_damage_rating_scale; reported at <see cref="Point"/> when it changed.</summary>
        public int ScaleDamageRating(int rating) => Apply(rating, DamageRatingScale, resist: false);

        /// <summary>Crit damage rating (attacker) through pvp_crit_damage_rating_scale; reported when it changed.</summary>
        public int ScaleCritDamageRating(int rating) => Apply(rating, CritDamageRatingScale, resist: false);

        /// <summary>Crit damage resist rating (defender) through pvp_crit_damage_rating_scale, via <see cref="ScaleResistRating"/>; reported when it changed.</summary>
        public int ScaleCritDamageResistRating(int rating) => Apply(rating, CritDamageRatingScale, resist: true);

        /// <summary>
        /// Reports a rating the caller scaled elsewhere with <see cref="DamageRatingScale"/> (the defender's damage
        /// resist rating, scaled inside Creature.GetDamageResistRatingMod). Reports only when applied and changed.
        /// </summary>
        public void ReportIfChanged(int before, int after)
        {
            if (Point.HasValue && before != after)
                PvpRules.Report(Point.Value, before, after);
        }

        private int Apply(int rating, double scale, bool resist)
        {
            if (!Point.HasValue)
                return rating;

            var scaled = resist ? ScaleResistRating(rating, scale) : ScaleRating(rating, scale);

            ReportIfChanged(rating, scaled);

            return scaled;
        }
    }
}
