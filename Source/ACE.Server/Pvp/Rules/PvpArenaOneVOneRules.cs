using System;

using ACE.Server.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// The arena 1v1 tunables (Docs/Pvp/DESIGN.md "Tunables"): pvp_arena_dmg_mod_1v1,
    /// pvp_arena_healkit_skill_cap_1v1 and pvp_arena_healkit_restoration_cap_1v1. Scoped to two players bound to
    /// the SAME Live 1v1 match (<see cref="PvpPlayerRules.InSameLive1v1Match"/>) - a 2v2 or FFA match, even Live,
    /// never reads these. Ported from Doctide `arenas_dmg_mod_1v1`, `arena_1v1_healkit_skill_bonus_cap` and
    /// `arena_1v1_healkit_restoration_bonus_cap`.
    /// </summary>
    public static class PvpArenaOneVOneRules
    {
        /// <summary>
        /// PURE. <paramref name="hit"/> unchanged unless <paramref name="inSameLive1v1"/>, in which case it is
        /// multiplied by <paramref name="dmgMod"/>. A NaN or negative mod is treated as 1.0 (unchanged), since
        /// the setting is not range-checked where it is read.
        /// </summary>
        public static float ApplyDmgMod1v1(bool inSameLive1v1, double dmgMod, float hit)
        {
            if (!inSameLive1v1)
                return hit;

            if (double.IsNaN(dmgMod) || double.IsInfinity(dmgMod) || dmgMod < 0.0)
                return hit;

            return hit * (float)dmgMod;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for AM1 (DamageEvent / SpellProjectile, applied BEFORE the PvP rules damage cap at
        /// C1/C2): classifies (<paramref name="source"/>, <paramref name="target"/>) as players first, then
        /// checks they are bound to the same Live 1v1 match, before reading pvp_arena_dmg_mod_1v1. Anything else
        /// - a non-player pair, self, a 2v2/FFA match, no match at all - returns <paramref name="hit"/> unchanged
        /// and reads no setting.
        /// </summary>
        public static float ApplyDmgMod1v1(WorldObject source, Creature target, float hit)
        {
            if (!(source is Player attacker) || !(target is Player defender) || ReferenceEquals(attacker, defender))
                return hit;

            if (!PvpPlayerRules.InSameLive1v1Match(attacker.PvpBinding, defender.PvpBinding))
                return hit;

            var dials = PvpTunables.DialSource?.Invoke() ?? PvpTunables.Defaults;

            var result = ApplyDmgMod1v1(true, dials.DmgMod1v1, hit);

            if (!(result == hit))
                PvpRules.Report(PvpChokePoint.AM1, hit, result);

            return result;
        }

        /// <summary>
        /// PURE. The FFA twin of the pure <see cref="ApplyDmgMod1v1(bool, double, float)"/>: <paramref name="hit"/>
        /// unchanged unless <paramref name="inSameLiveFfa"/>, then multiplied by <paramref name="dmgMod"/> with the same
        /// NaN / infinite / negative = unchanged rule.
        /// </summary>
        public static float ApplyDmgModFfa(bool inSameLiveFfa, double dmgMod, float hit) => ApplyDmgMod1v1(inSameLiveFfa, dmgMod, hit);

        /// <summary>
        /// CHOKE-POINT ENTRY for AM1 in FFA (called right beside <see cref="ApplyDmgMod1v1(WorldObject, Creature, float)"/>
        /// at the same two sites, so before the PvP rules damage cap): classifies the pair as two distinct players
        /// first, then requires the SAME Live FFA match before reading pvp_arena_dmg_mod_ffa. Anything else - a 1v1 or
        /// 2v2 match, no match, a non-player - returns <paramref name="hit"/> unchanged and reads no setting. Mutually
        /// exclusive with the 1v1 entry (a match has one mode key), so the two never both scale one hit.
        /// </summary>
        public static float ApplyDmgModFfa(WorldObject source, Creature target, float hit)
        {
            if (!(source is Player attacker) || !(target is Player defender) || ReferenceEquals(attacker, defender))
                return hit;

            if (!PvpPlayerRules.InSameLiveFfaMatch(attacker.PvpBinding, defender.PvpBinding))
                return hit;

            var dials = PvpTunables.DialSource?.Invoke() ?? PvpTunables.Defaults;

            var result = ApplyDmgModFfa(true, dials.DmgModFfa, hit);

            if (!(result == hit))
                PvpRules.Report(PvpChokePoint.AM1, hit, result);

            return result;
        }

        /// <summary>
        /// PURE. <paramref name="hit"/> unchanged unless <paramref name="isRing"/> and <paramref name="inSameLiveFfa"/>, then
        /// multiplied by <paramref name="ringMod"/> with the same NaN / infinite / negative = unchanged rule.
        /// </summary>
        public static float ApplyFfaRingDmg(bool inSameLiveFfa, bool isRing, double ringMod, float hit) => ApplyDmgMod1v1(inSameLiveFfa && isRing, ringMod, hit);

        /// <summary>
        /// CHOKE-POINT ENTRY for pvp_arena_ffa_ring_dmg (Tugak Brawl's own ring scaler), called from SpellProjectile.CalculateDamage
        /// AFTER the war/void-vs-life branch so it covers a ring of ANY school, and so before the PvP rules damage cap. A non-ring
        /// projectile, a non-player pair, or anything but a SAME Live FFA match returns <paramref name="hit"/> unchanged and reads no setting.
        /// </summary>
        public static float ApplyFfaRingDmg(WorldObject source, Creature target, bool isRing, float hit)
        {
            if (!isRing)
                return hit;

            if (!(source is Player attacker) || !(target is Player defender) || ReferenceEquals(attacker, defender))
                return hit;

            if (!PvpPlayerRules.InSameLiveFfaMatch(attacker.PvpBinding, defender.PvpBinding))
                return hit;

            var dials = PvpTunables.DialSource?.Invoke() ?? PvpTunables.Defaults;

            var result = ApplyFfaRingDmg(true, true, dials.FfaRingDmg, hit);

            if (!(result == hit))
                PvpRules.Report(PvpChokePoint.AM1, hit, result);

            return result;
        }

        /// <summary>
        /// PURE. The battleground twin of the pure <see cref="ApplyDmgModFfa(bool, double, float)"/>: <paramref name="hit"/> unchanged
        /// unless <paramref name="inSameLiveBattleground"/>, then multiplied by <paramref name="dmgMod"/> with the same NaN / infinite /
        /// negative = unchanged rule.
        /// </summary>
        public static float ApplyDmgModBg(bool inSameLiveBattleground, double dmgMod, float hit) => ApplyDmgMod1v1(inSameLiveBattleground, dmgMod, hit);

        /// <summary>
        /// CHOKE-POINT ENTRY for AM1 in a battleground (called right beside the 1v1 and FFA entries at the same two sites, so before the PvP
        /// rules damage cap): classifies the pair as two distinct players first, then requires the SAME Live battleground match, of ANY
        /// battleground mode, before reading pvp_bg_dmg_mod (BattlegroundTunables). Anything else - an arena match, no match, a crystal or
        /// any other non-player - returns <paramref name="hit"/> unchanged and reads no setting. Mutually exclusive with the 1v1 and FFA
        /// entries (a match has one mode key), so no hit is scaled by two of them.
        /// </summary>
        public static float ApplyDmgModBg(WorldObject source, Creature target, float hit)
        {
            if (!(source is Player attacker) || !(target is Player defender) || ReferenceEquals(attacker, defender))
                return hit;

            if (!PvpPlayerRules.InSameLiveBattlegroundMatch(attacker.PvpBinding, defender.PvpBinding))
                return hit;

            var dials = Battlegrounds.BattlegroundTunables.DialSource?.Invoke() ?? Battlegrounds.BattlegroundTunables.Defaults;

            var result = ApplyDmgModBg(true, dials.DmgMod, hit);

            if (!(result == hit))
                PvpRules.Report(PvpChokePoint.AM1, hit, result);

            return result;
        }

        /// <summary>PURE. min(boostValue, cap) - Doctide's exact clamp, never widening the boost.</summary>
        public static int CapHealkitSkillBonus(int boostValue, int cap) => Math.Min(boostValue, cap);

        /// <summary>PURE. min(healkitMod, cap) - Doctide's exact clamp, never widening the restoration bonus.</summary>
        public static double CapHealkitRestorationBonus(double healkitMod, double cap) => Math.Min(healkitMod, cap);

        /// <summary>
        /// CHOKE-POINT ENTRY for HK1 (Healer.DoSkillCheck): the healing-kit skill boost, capped to
        /// pvp_arena_healkit_skill_cap_1v1 while <paramref name="target"/> is bound to a Live 1v1 match - the
        /// same party Doctide gates this on (target only, not the healer). Anything else returns
        /// <paramref name="boostValue"/> unchanged and reads no setting.
        /// </summary>
        public static int ApplyHealkitSkillCap1v1(Player target, int boostValue)
        {
            if (!IsBoundToLive1v1(target))
                return boostValue;

            var dials = PvpTunables.DialSource?.Invoke() ?? PvpTunables.Defaults;

            var capped = CapHealkitSkillBonus(boostValue, dials.HealkitSkillCap1v1);

            if (capped != boostValue)
                PvpRules.Report(PvpChokePoint.HK1, boostValue, capped);

            return capped;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for HK2 (Healer.GetHealAmount): the healing-kit restoration mod, capped to
        /// pvp_arena_healkit_restoration_cap_1v1 while <paramref name="target"/> is bound to a Live 1v1 match -
        /// the same party Doctide gates this on (target only). Anything else returns
        /// <paramref name="healkitMod"/> unchanged and reads no setting.
        /// </summary>
        public static double ApplyHealkitRestorationCap1v1(Player target, double healkitMod)
        {
            if (!IsBoundToLive1v1(target))
                return healkitMod;

            var dials = PvpTunables.DialSource?.Invoke() ?? PvpTunables.Defaults;

            var capped = CapHealkitRestorationBonus(healkitMod, dials.HealkitRestorationCap1v1);

            if (!(capped == healkitMod))
                PvpRules.Report(PvpChokePoint.HK2, healkitMod, capped);

            return capped;
        }

        private static bool IsBoundToLive1v1(Player target)
            => target != null && PvpPlayerRules.InLiveMatch(target.PvpBinding) && target.PvpBinding.Match.ModeKey == ArenaMapCatalog.OneVOneKey;
    }
}
