using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    public static partial class HoldSettings
    {
        static partial void RegisterHealing(List<HoldSetting> r)
        {
            r.Add(Tog(GroupHealing, HoldKeys.Shrine, true, HoldApplies.Live, "protect.shrine", false,
                "The Shrines of Restoration. Off: using one does nothing (live); shrines are placed at the start. Not set: goals.json decides.",
                g => F(g.Protect?.Shrine != null)));

            r.Add(Num(GroupHealing, HoldKeys.ShrineHeal, 0.15, 0, 1, HoldApplies.Live, "protect.shrineHeal", false,
                "Share of max health a shrine use restores.", g => F(g.Protect?.ShrineHeal ?? 0)));

            r.Add(Num(GroupHealing, HoldKeys.ShrineCooldownSeconds, 45, 0, 600, HoldApplies.Live, "protect.shrineCooldownSeconds", false,
                "Seconds a shrine rests after a use (0 allowed when set here).", g => F(g.Protect?.EffectiveShrineCooldownSeconds ?? 0)));

            r.Add(Int(GroupHealing, HoldKeys.ShrineCount, ProtectDef.MaxProtectedTargets, 0, ProtectDef.MaxProtectedTargets, HoldApplies.NextEvent, null, true,
                "How many knights get a shrine (the first N). Not set: every knight.", g => g.Protect?.ShrineCount is int n ? F(n) : "every knight"));

            r.Add(Num(GroupHealing, HoldKeys.ShrineUseRadius, 4, 1, 10, HoldApplies.NextEvent, "protect.shrine.useRadius", false,
                "How far away a player may use a shrine (set on the shrine when it is placed).", g => F(g.Protect?.Shrine?.UseRadius ?? 0)));

            r.Add(Tog(GroupHealing, HoldKeys.ShrineHealsAll, false, HoldApplies.Live, null, true,
                "A shrine use heals every standing knight instead of only the one beside it."));

            r.Add(Num(GroupHealing, HoldKeys.ShrinePlayerCooldownSeconds, 0, 0, 600, HoldApplies.Live, null, true,
                "One player waits this long between any two shrine uses (0 = off)."));

            r.Add(Num(GroupHealing, HoldKeys.ShrineBindRange, ProtectDef.DefaultShrineBindRange, 2, 20, HoldApplies.Live, null, true,
                "A shrine restores the nearest standing knight within this many metres (was a constant)."));

            r.Add(Num(GroupHealing, HoldKeys.CorruptedKillHeal, 0, 0, 1, HoldApplies.Live, "protect.corruptedKillHeal", false,
                "Share of max health every standing knight regains when a corrupted monster dies.", g => F(g.Protect?.CorruptedKillHeal ?? 0)));

            r.Add(Num(GroupHealing, HoldKeys.RescueHeal, 0, 0, 1, HoldApplies.Live, "protect.rescueHeal", false,
                "Share a knight regains when a monster attacking it is killed by a player or pet.", g => F(g.Protect?.RescueHeal ?? 0)));

            r.Add(Num(GroupHealing, HoldKeys.RescueCapPerWave, 0.10, 0, 1, HoldApplies.Live, "protect.rescueCapPerWave", false,
                "The most a knight regains from rescues per wave.", g => F(g.Protect?.RescueCapPerWave ?? 0)));

            r.Add(Num(GroupHealing, HoldKeys.RegenPerSecond, 0, 0, 0.1, HoldApplies.Live, "protect.clearRegenPerSecond", false,
                "Share of max health a knight with no monster near regenerates per second (0 = off).", g => F(g.Protect?.ClearRegenPerSecond ?? 0)));

            r.Add(Num(GroupHealing, HoldKeys.RegenClearRadius, 10, 0, 50, HoldApplies.Live, "protect.clearRadius", false,
                "A knight regenerates only with no wave monster within this many metres.", g => F(g.Protect?.ClearRadius ?? 0)));

            r.Add(Num(GroupHealing, HoldKeys.RegenIntervalSeconds, 1, 1, 30, HoldApplies.Live, null, true,
                "Regeneration is applied every this many seconds (the same amount per second overall)."));

            r.Add(Tog(GroupHealing, HoldKeys.PlayerHeal, true, HoldApplies.Live, null, true,
                "Players may heal knights with heal spells and kits. Off: their heals restore nothing and the knights cannot be targeted by them."));

            r.Add(Num(GroupHealing, HoldKeys.PlayerHealCap, ProtectDef.DefaultMaxHealPerTenSeconds, 0, 1, HoldApplies.Live, "protect.maxHealPerTenSeconds", false,
                "The most player heals restore on one knight per 10 s, as a share of max health (0 = off).", g => F(g.Protect?.MaxHealPerTenSeconds ?? 0)));

            r.Add(Num(GroupHealing, HoldKeys.PlayerHealMult, 1, 0, 5, HoldApplies.Live, null, true,
                "Scales what one player heal is worth on a knight (100 raw = 4% at 1), before the cap."));

            r.Add(Num(GroupHealing, HoldKeys.HealReductionAfterHit, 0, 0, 1, HoldApplies.Live, "protect.healReductionAfterHit", false,
                "How much healing on a just-hit knight is reduced.", g => F(g.Protect?.HealReductionAfterHit ?? 0)));

            r.Add(Num(GroupHealing, HoldKeys.HealReductionSeconds, 5, 0, 60, HoldApplies.Live, "protect.healReductionSeconds", false,
                "How long after a hit the reduction lasts.", g => F(g.Protect?.HealReductionSeconds ?? 0)));

            r.Add(Num(GroupHealing, HoldKeys.HealBetweenWaves, 0, 0, 1, HoldApplies.Live, "protect.healBetweenWaves", false,
                "Share every standing knight regains when a wave is fully cleared.", g => F(g.Protect?.HealBetweenWaves ?? 0)));

            r.Add(Num(GroupHealing, HoldKeys.HealBudgetPerWave, 0, 0, 1, HoldApplies.Live, "protect.healBudgetPerWave", false,
                "The most a knight regains per wave from every source together (0 = no budget).", g => F(g.Protect?.HealBudgetPerWave ?? 0)));
        }
    }
}
