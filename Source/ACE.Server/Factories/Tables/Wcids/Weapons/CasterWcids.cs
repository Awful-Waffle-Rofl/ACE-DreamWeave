using System;
using System.Collections.Generic;
using ACE.Database.Models.World;
using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Enum;

namespace ACE.Server.Factories.Tables.Wcids
{
    public static class CasterWcids
    {
        // FORK CHANGE (Sanguine = ninth element, 2026-08-25). Retail organises loot casters into three
        // SHAPE families - Sceptre, Baton, Staff - and puts all eight damage types (the seven physical/
        // elemental ones plus Nether) in each. Alongside them sit four undifferentiated "plain" casters
        // (orb/sceptre/staff/wand) with DamageType Undef, which is why there is no elemental orb and no
        // elemental wand anywhere in retail: those two shapes are the non-elemental ones. Note the enum
        // names mislead here - wandfire (29262) is in-game "Fire Sceptre", not a wand.
        //
        // The 2026-08-02 build of the Sanguine family did not follow that structure. It occupied the
        // wand and orb shapes at HALF an element's weight, and paid for itself out of the plain four,
        // which made plain casters rarer than retail. It is now a full ninth element: a Sanguine
        // sceptre (1000242), baton (1000247) and staff (1000244), each at the same weight as every
        // other element's member of that shape, and the orb is gone from the pool entirely.
        //
        // FUNDING: the elements each take a fraction of the retail per-member weight, so total elemental
        // supply is unchanged and the plain casters are not squeezed. T3-T6 carry NINE elements (eight
        // retail plus Sanguine) at 8/9 of retail; there the plain four sit within 0.4% of their retail
        // weights, because they absorb the residual rounding. T7-T8 carry TEN elements (the nine plus the
        // Summoner family, 2026-11-21) at 8/10 of retail, which reproduces exactly: the plain four are at
        // their exact retail weights and nothing is left to absorb. T7 is 0.16 plain + 0.72 sceptre/baton
        // + 0.12 staff, T8 0.144 + 0.576 + 0.28. The Summoner sceptre (1008550), baton (1008551) and staff
        // (1008552) drop in T7 and T8 ONLY (owner ruling), each at the same weight as the Sanguine member
        // of its shape.
        //
        // Every table must sum to exactly 1.0. ChanceTable.Roll walks a cumulative sum against a
        // uniform [0,1) draw and hands any shortfall to the LAST non-zero entry, and VerifyTable only
        // log.Errors a mismatch rather than throwing - so a table that does not add up fails silently
        // in favour of whatever happens to be last. All weights below carry at most 7 significant
        // digits, so the (decimal) conversion VerifyTable does is exact and the sums are exact.

        private static ChanceTable<WeenieClassName> T1_T2_Chances = new ChanceTable<WeenieClassName>()
        {
            (WeenieClassName.orb,     0.25f ),
            (WeenieClassName.sceptre, 0.25f ),
            (WeenieClassName.staff,   0.25f ),
            (WeenieClassName.wand,    0.25f ),
        };

        // T3: plain 4 x 0.1699 = 0.6796, elements 18 x 0.0178 = 0.3204. (retail: 0.17 plain / 0.02 each)
        private static ChanceTable<WeenieClassName> T3_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                        0.1699f ),
            ( WeenieClassName.sceptre,                    0.1699f ),
            ( WeenieClassName.staff,                      0.1699f ),
            ( WeenieClassName.wand,                       0.1699f ),
            ( WeenieClassName.wandslashing,               0.0178f ),
            ( WeenieClassName.wandpiercing,               0.0178f ),
            ( WeenieClassName.wandblunt,                  0.0178f ),
            ( WeenieClassName.wandacid,                   0.0178f ),
            ( WeenieClassName.wandfire,                   0.0178f ),
            ( WeenieClassName.wandfrost,                  0.0178f ),
            ( WeenieClassName.wandelectric,               0.0178f ),
            ( WeenieClassName.ace43381_nethersceptre,     0.0178f ),
            ( WeenieClassName.driftwardensanguinesceptre, 0.0178f ),
            ( WeenieClassName.ace31819_slashingbaton,     0.0178f ),
            ( WeenieClassName.ace31825_piercingbaton,     0.0178f ),
            ( WeenieClassName.ace31821_bluntbaton,        0.0178f ),
            ( WeenieClassName.ace31820_acidbaton,         0.0178f ),
            ( WeenieClassName.ace31823_firebaton,         0.0178f ),
            ( WeenieClassName.ace31824_frostbaton,        0.0178f ),
            ( WeenieClassName.ace31822_electricbaton,     0.0178f ),
            ( WeenieClassName.ace43382_netherbaton,       0.0178f ),
            ( WeenieClassName.driftwardensanguinebaton,   0.0178f ),
        };

        // T4: plain 4 x 0.12985 = 0.5194, elements 18 x 0.0267 = 0.4806. (retail: 0.13 plain / 0.03 each)
        private static ChanceTable<WeenieClassName> T4_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                        0.12985f ),
            ( WeenieClassName.sceptre,                    0.12985f ),
            ( WeenieClassName.staff,                      0.12985f ),
            ( WeenieClassName.wand,                       0.12985f ),
            ( WeenieClassName.wandslashing,               0.0267f ),
            ( WeenieClassName.wandpiercing,               0.0267f ),
            ( WeenieClassName.wandblunt,                  0.0267f ),
            ( WeenieClassName.wandacid,                   0.0267f ),
            ( WeenieClassName.wandfire,                   0.0267f ),
            ( WeenieClassName.wandfrost,                  0.0267f ),
            ( WeenieClassName.wandelectric,               0.0267f ),
            ( WeenieClassName.ace43381_nethersceptre,     0.0267f ),
            ( WeenieClassName.driftwardensanguinesceptre, 0.0267f ),
            ( WeenieClassName.ace31819_slashingbaton,     0.0267f ),
            ( WeenieClassName.ace31825_piercingbaton,     0.0267f ),
            ( WeenieClassName.ace31821_bluntbaton,        0.0267f ),
            ( WeenieClassName.ace31820_acidbaton,         0.0267f ),
            ( WeenieClassName.ace31823_firebaton,         0.0267f ),
            ( WeenieClassName.ace31824_frostbaton,        0.0267f ),
            ( WeenieClassName.ace31822_electricbaton,     0.0267f ),
            ( WeenieClassName.ace43382_netherbaton,       0.0267f ),
            ( WeenieClassName.driftwardensanguinebaton,   0.0267f ),
        };

        // T5/T6: plain 4 x 0.0502 = 0.2008, elements 18 x 0.0444 = 0.7992. (retail: 0.05 plain / 0.05 each)
        private static ChanceTable<WeenieClassName> T5_T6_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                        0.0502f ),
            ( WeenieClassName.sceptre,                    0.0502f ),
            ( WeenieClassName.staff,                      0.0502f ),
            ( WeenieClassName.wand,                       0.0502f ),
            ( WeenieClassName.wandslashing,               0.0444f ),
            ( WeenieClassName.wandpiercing,               0.0444f ),
            ( WeenieClassName.wandblunt,                  0.0444f ),
            ( WeenieClassName.wandacid,                   0.0444f ),
            ( WeenieClassName.wandfire,                   0.0444f ),
            ( WeenieClassName.wandfrost,                  0.0444f ),
            ( WeenieClassName.wandelectric,               0.0444f ),
            ( WeenieClassName.ace43381_nethersceptre,     0.0444f ),
            ( WeenieClassName.driftwardensanguinesceptre, 0.0444f ),
            ( WeenieClassName.ace31819_slashingbaton,     0.0444f ),
            ( WeenieClassName.ace31825_piercingbaton,     0.0444f ),
            ( WeenieClassName.ace31821_bluntbaton,        0.0444f ),
            ( WeenieClassName.ace31820_acidbaton,         0.0444f ),
            ( WeenieClassName.ace31823_firebaton,         0.0444f ),
            ( WeenieClassName.ace31824_frostbaton,        0.0444f ),
            ( WeenieClassName.ace31822_electricbaton,     0.0444f ),
            ( WeenieClassName.ace43382_netherbaton,       0.0444f ),
            ( WeenieClassName.driftwardensanguinebaton,   0.0444f ),
        };

        // T7: plain 4 x 0.04 = 0.16, sceptres+batons 20 x 0.036 = 0.72, staves 10 x 0.012 = 0.12.
        // (retail: 0.04 plain / 0.045 sceptre+baton / 0.015 staff)
        private static ChanceTable<WeenieClassName> T7_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                        0.04f ),
            ( WeenieClassName.sceptre,                    0.04f ),
            ( WeenieClassName.staff,                      0.04f ),
            ( WeenieClassName.wand,                       0.04f ),
            ( WeenieClassName.wandslashing,               0.036f ),
            ( WeenieClassName.wandpiercing,               0.036f ),
            ( WeenieClassName.wandblunt,                  0.036f ),
            ( WeenieClassName.wandacid,                   0.036f ),
            ( WeenieClassName.wandfire,                   0.036f ),
            ( WeenieClassName.wandfrost,                  0.036f ),
            ( WeenieClassName.wandelectric,               0.036f ),
            ( WeenieClassName.ace43381_nethersceptre,     0.036f ),
            ( WeenieClassName.driftwardensanguinesceptre, 0.036f ),
            ( WeenieClassName.summonersceptre,            0.036f ),
            ( WeenieClassName.ace31819_slashingbaton,     0.036f ),
            ( WeenieClassName.ace31825_piercingbaton,     0.036f ),
            ( WeenieClassName.ace31821_bluntbaton,        0.036f ),
            ( WeenieClassName.ace31820_acidbaton,         0.036f ),
            ( WeenieClassName.ace31823_firebaton,         0.036f ),
            ( WeenieClassName.ace31824_frostbaton,        0.036f ),
            ( WeenieClassName.ace31822_electricbaton,     0.036f ),
            ( WeenieClassName.ace43382_netherbaton,       0.036f ),
            ( WeenieClassName.driftwardensanguinebaton,   0.036f ),
            ( WeenieClassName.summonerbaton,              0.036f ),
            ( WeenieClassName.ace37223_slashingstaff,     0.012f ),
            ( WeenieClassName.ace37222_piercingstaff,     0.012f ),
            ( WeenieClassName.ace37225_bluntstaff,        0.012f ),
            ( WeenieClassName.ace37224_acidstaff,         0.012f ),
            ( WeenieClassName.ace37220_firestaff,         0.012f ),
            ( WeenieClassName.ace37221_froststaff,        0.012f ),
            ( WeenieClassName.ace37219_electricstaff,     0.012f ),
            ( WeenieClassName.ace43383_netherstaff,       0.012f ),
            ( WeenieClassName.driftwardensanguinestaff,   0.012f ),
            ( WeenieClassName.summonerstaff,              0.012f ),
        };

        // T8: plain 4 x 0.036 = 0.144, sceptres+batons 20 x 0.0288 = 0.576, staves 10 x 0.028 = 0.28.
        // (retail: 0.036 plain / 0.036 sceptre+baton / 0.035 staff)
        private static ChanceTable<WeenieClassName> T8_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                        0.036f ),
            ( WeenieClassName.sceptre,                    0.036f ),
            ( WeenieClassName.staff,                      0.036f ),
            ( WeenieClassName.wand,                       0.036f ),
            ( WeenieClassName.wandslashing,               0.0288f ),
            ( WeenieClassName.wandpiercing,               0.0288f ),
            ( WeenieClassName.wandblunt,                  0.0288f ),
            ( WeenieClassName.wandacid,                   0.0288f ),
            ( WeenieClassName.wandfire,                   0.0288f ),
            ( WeenieClassName.wandfrost,                  0.0288f ),
            ( WeenieClassName.wandelectric,               0.0288f ),
            ( WeenieClassName.ace43381_nethersceptre,     0.0288f ),
            ( WeenieClassName.driftwardensanguinesceptre, 0.0288f ),
            ( WeenieClassName.summonersceptre,            0.0288f ),
            ( WeenieClassName.ace31819_slashingbaton,     0.0288f ),
            ( WeenieClassName.ace31825_piercingbaton,     0.0288f ),
            ( WeenieClassName.ace31821_bluntbaton,        0.0288f ),
            ( WeenieClassName.ace31820_acidbaton,         0.0288f ),
            ( WeenieClassName.ace31823_firebaton,         0.0288f ),
            ( WeenieClassName.ace31824_frostbaton,        0.0288f ),
            ( WeenieClassName.ace31822_electricbaton,     0.0288f ),
            ( WeenieClassName.ace43382_netherbaton,       0.0288f ),
            ( WeenieClassName.driftwardensanguinebaton,   0.0288f ),
            ( WeenieClassName.summonerbaton,              0.0288f ),
            ( WeenieClassName.ace37223_slashingstaff,     0.028f ),
            ( WeenieClassName.ace37222_piercingstaff,     0.028f ),
            ( WeenieClassName.ace37225_bluntstaff,        0.028f ),
            ( WeenieClassName.ace37224_acidstaff,         0.028f ),
            ( WeenieClassName.ace37220_firestaff,         0.028f ),
            ( WeenieClassName.ace37221_froststaff,        0.028f ),
            ( WeenieClassName.ace37219_electricstaff,     0.028f ),
            ( WeenieClassName.ace43383_netherstaff,       0.028f ),
            ( WeenieClassName.driftwardensanguinestaff,   0.028f ),
            ( WeenieClassName.summonerstaff,              0.028f ),
        };

        private static readonly List<ChanceTable<WeenieClassName>> casterTiers = new List<ChanceTable<WeenieClassName>>()
        {
            T1_T2_Chances,
            T1_T2_Chances,
            T3_Chances,
            T4_Chances,
            T5_T6_Chances,
            T5_T6_Chances,
            T7_Chances,
            T8_Chances
        };

        public static WeenieClassName Roll(int tier)
        {
            return casterTiers[tier - 1].Roll();
        }

        private static readonly HashSet<WeenieClassName> _combined = new HashSet<WeenieClassName>();

        static CasterWcids()
        {
            foreach (var casterTier in casterTiers)
            {
                foreach (var entry in casterTier)
                    _combined.Add(entry.result);
            }
        }

        /// <summary>Every wcid the loot tables can roll. Read by the loot-catalog tool.</summary>
        public static WeenieClassName[] AllWcids() => System.Linq.Enumerable.ToArray(_combined);

        public static bool Contains(WeenieClassName wcid)
        {
            return _combined.Contains(wcid);
        }
    }
}
