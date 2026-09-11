using System;

using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 3: a flat multiplier on this monster's attack or cast speed, authored per weenie. axis= reuses
    /// MonsterSpeedAxis directly (its members are named "attack"/"cast", matched case-insensitively by
    /// MonsterEffectSpec.GetEnum) rather than a private enum, since the two vocabularies are identical.
    ///
    /// NO RUNTIME CLAMP HERE - the one exception to invariant 4. Creature.GetMonsterEffectSpeedMultiplier
    /// (the dispatch site) takes the PRODUCT of every IMonsterSpeedMod a monster carries and clamps that
    /// product symmetrically against monster_effect_speed_cap ([1/cap, cap]) once, after every handler has
    /// run. Clamping a single handler's own contribution here as well would double-clamp and make a
    /// multi-effect monster's composed speed depend on clamp order rather than on the product, which the
    /// dispatch site's own doc comment is explicit about avoiding.
    /// </summary>
    public sealed class SpeedEffect : IMonsterEffect, IMonsterSpeedMod
    {
        public string Kind => "speed";

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var axisRaw = spec.GetString("axis");
            if (axisRaw == null || !Enum.TryParse<MonsterSpeedAxis>(axisRaw, true, out var axis) || !Enum.IsDefined(typeof(MonsterSpeedAxis), axis))
            {
                error = "speed requires a valid axis= (attack|cast)";
                return false;
            }

            var pct = spec.GetDouble("pct", 0.0);
            if (pct == 0.0)
            {
                error = "speed requires pct= != 0";
                return false;
            }

            if (pct <= -1.0)
            {
                error = "speed requires pct= > -1 (the resulting multiplier must stay positive)";
                return false;
            }

            error = null;
            return true;
        }

        public double GetSpeedMultiplier(Creature creature, MonsterSpeedAxis axis, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            // Validate already rejected a missing/invalid axis=, so the default here is never actually used
            var specAxis = spec.GetEnum("axis", MonsterSpeedAxis.Attack);
            if (specAxis != axis)
                return 1.0;

            var pct = spec.GetDouble("pct", 0.0);
            if (pct == 0.0)
                return 1.0;

            return 1.0 + pct;
        }
    }
}
