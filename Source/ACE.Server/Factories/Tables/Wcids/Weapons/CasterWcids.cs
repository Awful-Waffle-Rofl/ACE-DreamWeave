using System;
using System.Collections.Generic;
using ACE.Database.Models.World;
using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Enum;

namespace ACE.Server.Factories.Tables.Wcids
{
    public static class CasterWcids
    {
        private static ChanceTable<WeenieClassName> T1_T2_Chances = new ChanceTable<WeenieClassName>()
        {
            (WeenieClassName.orb,     0.25f ),
            (WeenieClassName.sceptre, 0.25f ),
            (WeenieClassName.staff,   0.25f ),
            (WeenieClassName.wand,    0.25f ),
        };

        // FORK ADDITION (Sanguine caster family, 2026-08-02): driftwardensanguinewand/orb add a Health-
        // typed pair to every table an existing single-element wand/baton pair (e.g. wandfire /
        // ace31823_firebaton) already appears in, at HALF that element's per-table weight - deliberately
        // a smaller share than any established element. The added weight is taken from the 4
        // undifferentiated "plain" entries (orb/sceptre/staff/wand) proportionally, not from any existing
        // element, so fire/frost/acid/electric/nether stay untouched. Every table's weights still sum to
        // 1.0 (ChanceTable.VerifyTable logs an error, not a throw, if they do not - see ChanceTable.cs).
        // T3: plain 0.17 -> 0.165 (4 x -0.005 = -0.02, matches the 0.01 + 0.01 added below).
        private static ChanceTable<WeenieClassName> T3_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                    0.165f ),
            ( WeenieClassName.sceptre,                0.165f ),
            ( WeenieClassName.staff,                  0.165f ),
            ( WeenieClassName.wand,                   0.165f ),
            ( WeenieClassName.wandslashing,           0.02f ),
            ( WeenieClassName.wandpiercing,           0.02f ),
            ( WeenieClassName.wandblunt,              0.02f ),
            ( WeenieClassName.wandacid,               0.02f ),
            ( WeenieClassName.wandfire,               0.02f ),
            ( WeenieClassName.wandfrost,              0.02f ),
            ( WeenieClassName.wandelectric,           0.02f ),
            ( WeenieClassName.ace43381_nethersceptre, 0.02f ),
            ( WeenieClassName.ace31819_slashingbaton, 0.02f ),
            ( WeenieClassName.ace31825_piercingbaton, 0.02f ),
            ( WeenieClassName.ace31821_bluntbaton,    0.02f ),
            ( WeenieClassName.ace31820_acidbaton,     0.02f ),
            ( WeenieClassName.ace31823_firebaton,     0.02f ),
            ( WeenieClassName.ace31824_frostbaton,    0.02f ),
            ( WeenieClassName.ace31822_electricbaton, 0.02f ),
            ( WeenieClassName.ace43382_netherbaton,   0.02f ),
            ( WeenieClassName.driftwardensanguinewand, 0.01f ),
            ( WeenieClassName.driftwardensanguineorb,  0.01f ),
        };

        // T4: plain 0.13 -> 0.1225 (4 x -0.0075 = -0.03, matches 0.015 + 0.015 added below).
        private static ChanceTable<WeenieClassName> T4_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                    0.1225f ),
            ( WeenieClassName.sceptre,                0.1225f ),
            ( WeenieClassName.staff,                  0.1225f ),
            ( WeenieClassName.wand,                   0.1225f ),
            ( WeenieClassName.wandslashing,           0.03f ),
            ( WeenieClassName.wandpiercing,           0.03f ),
            ( WeenieClassName.wandblunt,              0.03f ),
            ( WeenieClassName.wandacid,               0.03f ),
            ( WeenieClassName.wandfire,               0.03f ),
            ( WeenieClassName.wandfrost,              0.03f ),
            ( WeenieClassName.wandelectric,           0.03f ),
            ( WeenieClassName.ace43381_nethersceptre, 0.03f ),
            ( WeenieClassName.ace31819_slashingbaton, 0.03f ),
            ( WeenieClassName.ace31825_piercingbaton, 0.03f ),
            ( WeenieClassName.ace31821_bluntbaton,    0.03f ),
            ( WeenieClassName.ace31820_acidbaton,     0.03f ),
            ( WeenieClassName.ace31823_firebaton,     0.03f ),
            ( WeenieClassName.ace31824_frostbaton,    0.03f ),
            ( WeenieClassName.ace31822_electricbaton, 0.03f ),
            ( WeenieClassName.ace43382_netherbaton,   0.03f ),
            ( WeenieClassName.driftwardensanguinewand, 0.015f ),
            ( WeenieClassName.driftwardensanguineorb,  0.015f ),
        };

        // T5/T6: plain 0.05 -> 0.0375 (4 x -0.0125 = -0.05, matches 0.025 + 0.025 added below).
        private static ChanceTable<WeenieClassName> T5_T6_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                    0.0375f ),
            ( WeenieClassName.sceptre,                0.0375f ),
            ( WeenieClassName.staff,                  0.0375f ),
            ( WeenieClassName.wand,                   0.0375f ),
            ( WeenieClassName.wandslashing,           0.05f ),
            ( WeenieClassName.wandpiercing,           0.05f ),
            ( WeenieClassName.wandblunt,              0.05f ),
            ( WeenieClassName.wandacid,               0.05f ),
            ( WeenieClassName.wandfire,               0.05f ),
            ( WeenieClassName.wandfrost,              0.05f ),
            ( WeenieClassName.wandelectric,           0.05f ),
            ( WeenieClassName.ace43381_nethersceptre, 0.05f ),
            ( WeenieClassName.ace31819_slashingbaton, 0.05f ),
            ( WeenieClassName.ace31825_piercingbaton, 0.05f ),
            ( WeenieClassName.ace31821_bluntbaton,    0.05f ),
            ( WeenieClassName.ace31820_acidbaton,     0.05f ),
            ( WeenieClassName.ace31823_firebaton,     0.05f ),
            ( WeenieClassName.ace31824_frostbaton,    0.05f ),
            ( WeenieClassName.ace31822_electricbaton, 0.05f ),
            ( WeenieClassName.ace43382_netherbaton,   0.05f ),
            ( WeenieClassName.driftwardensanguinewand, 0.025f ),
            ( WeenieClassName.driftwardensanguineorb,  0.025f ),
        };

        // T7: plain 0.04 -> 0.026875 (4 x -0.013125 = -0.0525, matches 0.0225 + 0.0225 + 0.0075 below).
        private static ChanceTable<WeenieClassName> T7_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                    0.026875f ),
            ( WeenieClassName.sceptre,                0.026875f ),
            ( WeenieClassName.staff,                  0.026875f ),
            ( WeenieClassName.wand,                   0.026875f ),
            ( WeenieClassName.wandslashing,           0.045f ),
            ( WeenieClassName.wandpiercing,           0.045f ),
            ( WeenieClassName.wandblunt,              0.045f ),
            ( WeenieClassName.wandacid,               0.045f ),
            ( WeenieClassName.wandfire,               0.045f ),
            ( WeenieClassName.wandfrost,              0.045f ),
            ( WeenieClassName.wandelectric,           0.045f ),
            ( WeenieClassName.ace43381_nethersceptre, 0.045f ),
            ( WeenieClassName.ace31819_slashingbaton, 0.045f ),
            ( WeenieClassName.ace31825_piercingbaton, 0.045f ),
            ( WeenieClassName.ace31821_bluntbaton,    0.045f ),
            ( WeenieClassName.ace31820_acidbaton,     0.045f ),
            ( WeenieClassName.ace31823_firebaton,     0.045f ),
            ( WeenieClassName.ace31824_frostbaton,    0.045f ),
            ( WeenieClassName.ace31822_electricbaton, 0.045f ),
            ( WeenieClassName.ace43382_netherbaton,   0.045f ),
            ( WeenieClassName.ace37223_slashingstaff, 0.015f ),
            ( WeenieClassName.ace37222_piercingstaff, 0.015f ),
            ( WeenieClassName.ace37225_bluntstaff,    0.015f ),
            ( WeenieClassName.ace37224_acidstaff,     0.015f ),
            ( WeenieClassName.ace37220_firestaff,     0.015f ),
            ( WeenieClassName.ace37221_froststaff,    0.015f ),
            ( WeenieClassName.ace37219_electricstaff, 0.015f ),
            ( WeenieClassName.ace43383_netherstaff,   0.015f ),
            ( WeenieClassName.driftwardensanguinewand,  0.0225f ),
            ( WeenieClassName.driftwardensanguineorb,   0.0225f ),
            ( WeenieClassName.driftwardensanguinestaff, 0.0075f ),
        };

        // T8: plain 0.036 -> 0.022625 (4 x -0.013375 = -0.0535, matches 0.018 + 0.018 + 0.0175 below).
        private static ChanceTable<WeenieClassName> T8_Chances = new ChanceTable<WeenieClassName>()
        {
            ( WeenieClassName.orb,                    0.022625f ),
            ( WeenieClassName.sceptre,                0.022625f ),
            ( WeenieClassName.staff,                  0.022625f ),
            ( WeenieClassName.wand,                   0.022625f ),
            ( WeenieClassName.wandslashing,           0.036f ),
            ( WeenieClassName.wandpiercing,           0.036f ),
            ( WeenieClassName.wandblunt,              0.036f ),
            ( WeenieClassName.wandacid,               0.036f ),
            ( WeenieClassName.wandfire,               0.036f ),
            ( WeenieClassName.wandfrost,              0.036f ),
            ( WeenieClassName.wandelectric,           0.036f ),
            ( WeenieClassName.ace43381_nethersceptre, 0.036f ),
            ( WeenieClassName.ace31819_slashingbaton, 0.036f ),
            ( WeenieClassName.ace31825_piercingbaton, 0.036f ),
            ( WeenieClassName.ace31821_bluntbaton,    0.036f ),
            ( WeenieClassName.ace31820_acidbaton,     0.036f ),
            ( WeenieClassName.ace31823_firebaton,     0.036f ),
            ( WeenieClassName.ace31824_frostbaton,    0.036f ),
            ( WeenieClassName.ace31822_electricbaton, 0.036f ),
            ( WeenieClassName.ace43382_netherbaton,   0.036f ),
            ( WeenieClassName.ace37223_slashingstaff, 0.035f ),
            ( WeenieClassName.ace37222_piercingstaff, 0.035f ),
            ( WeenieClassName.ace37225_bluntstaff,    0.035f ),
            ( WeenieClassName.ace37224_acidstaff,     0.035f ),
            ( WeenieClassName.ace37220_firestaff,     0.035f ),
            ( WeenieClassName.ace37221_froststaff,    0.035f ),
            ( WeenieClassName.ace37219_electricstaff, 0.035f ),
            ( WeenieClassName.ace43383_netherstaff,   0.035f ),
            ( WeenieClassName.driftwardensanguinewand,  0.018f ),
            ( WeenieClassName.driftwardensanguineorb,   0.018f ),
            ( WeenieClassName.driftwardensanguinestaff, 0.0175f ),
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

        public static bool Contains(WeenieClassName wcid)
        {
            return _combined.Contains(wcid);
        }
    }
}
