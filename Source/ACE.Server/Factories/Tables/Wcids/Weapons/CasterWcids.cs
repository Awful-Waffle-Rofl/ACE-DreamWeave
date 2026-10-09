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
        // FUNDING: the plain four go back to their retail weights and the NINE elements each take 8/9
        // of the retail per-member weight, so total elemental supply is unchanged and every existing
        // element becomes ~11% rarer rather than the plain casters being squeezed. Residual rounding is
        // absorbed by the plain four, whose deviation from retail is at most 0.4%.
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

        // T7: plain 4 x 0.040075 = 0.1603, sceptres+batons 18 x 0.04 = 0.72, staves 9 x 0.0133 = 0.1197.
        // (retail: 0.04 plain / 0.045 sceptre+baton / 0.015 staff)
        private static ChanceTable<WeenieClassName> T7_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                        0.040075f ),
            ( WeenieClassName.sceptre,                    0.040075f ),
            ( WeenieClassName.staff,                      0.040075f ),
            ( WeenieClassName.wand,                       0.040075f ),
            ( WeenieClassName.wandslashing,               0.04f ),
            ( WeenieClassName.wandpiercing,               0.04f ),
            ( WeenieClassName.wandblunt,                  0.04f ),
            ( WeenieClassName.wandacid,                   0.04f ),
            ( WeenieClassName.wandfire,                   0.04f ),
            ( WeenieClassName.wandfrost,                  0.04f ),
            ( WeenieClassName.wandelectric,               0.04f ),
            ( WeenieClassName.ace43381_nethersceptre,     0.04f ),
            ( WeenieClassName.driftwardensanguinesceptre, 0.04f ),
            ( WeenieClassName.ace31819_slashingbaton,     0.04f ),
            ( WeenieClassName.ace31825_piercingbaton,     0.04f ),
            ( WeenieClassName.ace31821_bluntbaton,        0.04f ),
            ( WeenieClassName.ace31820_acidbaton,         0.04f ),
            ( WeenieClassName.ace31823_firebaton,         0.04f ),
            ( WeenieClassName.ace31824_frostbaton,        0.04f ),
            ( WeenieClassName.ace31822_electricbaton,     0.04f ),
            ( WeenieClassName.ace43382_netherbaton,       0.04f ),
            ( WeenieClassName.driftwardensanguinebaton,   0.04f ),
            ( WeenieClassName.ace37223_slashingstaff,     0.0133f ),
            ( WeenieClassName.ace37222_piercingstaff,     0.0133f ),
            ( WeenieClassName.ace37225_bluntstaff,        0.0133f ),
            ( WeenieClassName.ace37224_acidstaff,         0.0133f ),
            ( WeenieClassName.ace37220_firestaff,         0.0133f ),
            ( WeenieClassName.ace37221_froststaff,        0.0133f ),
            ( WeenieClassName.ace37219_electricstaff,     0.0133f ),
            ( WeenieClassName.ace43383_netherstaff,       0.0133f ),
            ( WeenieClassName.driftwardensanguinestaff,   0.0133f ),
        };

        // T8: plain 4 x 0.036025 = 0.1441, sceptres+batons 18 x 0.032 = 0.576, staves 9 x 0.0311 = 0.2799.
        // (retail: 0.036 plain / 0.036 sceptre+baton / 0.035 staff)
        private static ChanceTable<WeenieClassName> T8_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                        0.036025f ),
            ( WeenieClassName.sceptre,                    0.036025f ),
            ( WeenieClassName.staff,                      0.036025f ),
            ( WeenieClassName.wand,                       0.036025f ),
            ( WeenieClassName.wandslashing,               0.032f ),
            ( WeenieClassName.wandpiercing,               0.032f ),
            ( WeenieClassName.wandblunt,                  0.032f ),
            ( WeenieClassName.wandacid,                   0.032f ),
            ( WeenieClassName.wandfire,                   0.032f ),
            ( WeenieClassName.wandfrost,                  0.032f ),
            ( WeenieClassName.wandelectric,               0.032f ),
            ( WeenieClassName.ace43381_nethersceptre,     0.032f ),
            ( WeenieClassName.driftwardensanguinesceptre, 0.032f ),
            ( WeenieClassName.ace31819_slashingbaton,     0.032f ),
            ( WeenieClassName.ace31825_piercingbaton,     0.032f ),
            ( WeenieClassName.ace31821_bluntbaton,        0.032f ),
            ( WeenieClassName.ace31820_acidbaton,         0.032f ),
            ( WeenieClassName.ace31823_firebaton,         0.032f ),
            ( WeenieClassName.ace31824_frostbaton,        0.032f ),
            ( WeenieClassName.ace31822_electricbaton,     0.032f ),
            ( WeenieClassName.ace43382_netherbaton,       0.032f ),
            ( WeenieClassName.driftwardensanguinebaton,   0.032f ),
            ( WeenieClassName.ace37223_slashingstaff,     0.0311f ),
            ( WeenieClassName.ace37222_piercingstaff,     0.0311f ),
            ( WeenieClassName.ace37225_bluntstaff,        0.0311f ),
            ( WeenieClassName.ace37224_acidstaff,         0.0311f ),
            ( WeenieClassName.ace37220_firestaff,         0.0311f ),
            ( WeenieClassName.ace37221_froststaff,        0.0311f ),
            ( WeenieClassName.ace37219_electricstaff,     0.0311f ),
            ( WeenieClassName.ace43383_netherstaff,       0.0311f ),
            ( WeenieClassName.driftwardensanguinestaff,   0.0311f ),
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
