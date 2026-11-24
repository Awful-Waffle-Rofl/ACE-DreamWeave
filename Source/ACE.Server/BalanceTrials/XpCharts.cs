using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.Entity;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The one source of rank and level charts for the trial pipeline: calibrate's maxedTraitShare and trialseed's
    /// level lift both read the same dat through <see cref="Live"/>. Level-up math is never re-implemented here;
    /// the level totals are the live <see cref="EnlightenmentXpCurve"/> output, and the rank charts are the dat's
    /// own lists (the ones Player_Skills, Player_Attributes and Player_Vitals read).
    /// </summary>
    public sealed record XpCharts(
        IReadOnlyList<ulong> LevelTotals,
        IReadOnlyList<uint> LevelCredits,
        IReadOnlyList<uint> TrainedSkillXp,
        IReadOnlyList<uint> SpecializedSkillXp,
        IReadOnlyList<uint> AttributeXp,
        IReadOnlyList<uint> VitalXp,
        IReadOnlyDictionary<Skill, int> TrainedCost)
    {
        /// <summary>Reads the charts from the initialized portal dat and the live extended level curve.</summary>
        public static XpCharts Live()
        {
            var portal = DatManager.PortalDat ?? throw new InvalidOperationException("XpCharts.Live needs DatManager.PortalDat; initialize the dats first.");
            var table = portal.XpTable;

            var trainedCost = portal.SkillTable.SkillBaseHash
                .ToDictionary(kv => (Skill)kv.Key, kv => kv.Value.TrainedCost);

            return new XpCharts(
                EnlightenmentXpCurve.ExtendedTotals,
                table.CharacterLevelSkillCreditList,
                table.TrainedSkillXpList,
                table.SpecializedSkillXpList,
                table.AttributeXpList,
                table.VitalXpList,
                trainedCost);
        }

        /// <summary>
        /// Builds the charts from a retail-shaped base level chart, extending it with the live
        /// <see cref="EnlightenmentXpCurve.Extend"/> and the live overflow cap. For dat-free tests.
        /// </summary>
        public static XpCharts FromBase(
            IReadOnlyList<ulong> baseTotals,
            IReadOnlyList<uint> levelCredits,
            IReadOnlyList<uint> trainedSkillXp,
            IReadOnlyList<uint> specializedSkillXp,
            IReadOnlyList<uint> attributeXp,
            IReadOnlyList<uint> vitalXp,
            IReadOnlyDictionary<Skill, int> trainedCost)
        {
            return new XpCharts(
                EnlightenmentXpCurve.Extend(baseTotals, EnlightenmentXpCurve.TotalCap),
                levelCredits, trainedSkillXp, specializedSkillXp, attributeXp, vitalXp, trainedCost);
        }
    }
}
