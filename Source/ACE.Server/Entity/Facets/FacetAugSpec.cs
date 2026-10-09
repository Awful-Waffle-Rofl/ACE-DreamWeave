using System;
using System.Collections.Generic;

using ACE.Entity.Enum;

namespace ACE.Server.Entity.Facets
{
    /// <summary>
    /// Reconciles a stored facet skill list against an augmentation-specialized skill the player holds
    /// GLOBALLY, but whose stored row predates the purchase.
    ///
    /// Augmentations (Player.AugmentationSpecialize*) are character-wide state, while a skill's
    /// SkillAdvancementClass is captured PER FACET (Player_Facets.CaptureFacetSkills). The engine
    /// specializes a held tinkering/Salvaging skill the moment the augmentation is bought or the skill is
    /// next trained (AugmentationDevice.DoAugmentation, Player_Skills.TrainSkill), which touches only the
    /// live skill - never any facet row the character is not currently standing on. A row captured before
    /// that moment still holds the skill Trained, and Player_Facets.ApplyFacetSkills writes stored rows
    /// verbatim, so switching back to that facet silently un-specializes a skill the player never
    /// untrained. This class is the fix: it rewrites a stale Trained entry to Specialized before it is
    /// priced, trimmed, applied or shown, so the switch never regresses a specialization the character
    /// actually holds.
    /// </summary>
    public static class FacetAugSpec
    {
        /// <summary>
        /// Returns a copy of <paramref name="skills"/> with every entry that is stale-Trained rewritten to
        /// Specialized. An entry qualifies only when it is currently <see cref="SkillAdvancementClass.Trained"/>
        /// AND <paramref name="heldAugSpec"/> reports the player holds that skill's augmentation - an
        /// Untrained entry is left alone (the aug only specializes a skill on the Untrained -> Trained
        /// transition, per Player_Skills.TrainSkill, so a build that never trained the skill was never
        /// specialized either), and an already-Specialized entry is already correct.
        ///
        /// A rewritten entry gets Sac = Specialized, InitLevel = 10 (matching
        /// Player_Skills.SpecializeSkill's resetSkill: false path) and Ranks recomputed from its Pp via
        /// <paramref name="specRanks"/>. Pp itself is NEVER changed - the augmentation specializes for
        /// free, it never grants experience, so the invested amount the row already carries is exactly
        /// right on either side of the Sac change. Because Pp does not move, this cannot alter what the
        /// build costs in skill credits (Player.LookupSkillCreditCost prices an aug-held upgrade at zero
        /// on both a Trained and a Specialized entry) or in experience (FacetPools.TotalPp sums Pp only).
        ///
        /// Never mutates <paramref name="skills"/> or its entries in place, matching FacetPools'
        /// copy-before-touch convention (see FacetPools.TrimSkillsToCover) - a caller holding the original
        /// list must see it unchanged.
        /// </summary>
        public static List<FacetSkillEntry> NormalizeHeldAugSpecSkills(
            IEnumerable<FacetSkillEntry> skills,
            Func<Skill, bool> heldAugSpec,
            Func<uint, ushort> specRanks)
        {
            var result = new List<FacetSkillEntry>();

            if (skills == null)
                return result;

            foreach (var entry in skills)
            {
                if (entry == null)
                {
                    result.Add(null);
                    continue;
                }

                var stale = entry.Sac == SkillAdvancementClass.Trained
                    && heldAugSpec != null && heldAugSpec(entry.Skill);

                result.Add(stale
                    ? new FacetSkillEntry
                    {
                        Skill = entry.Skill,
                        Sac = SkillAdvancementClass.Specialized,
                        Ranks = specRanks != null ? specRanks(entry.Pp) : entry.Ranks,
                        Pp = entry.Pp,
                        InitLevel = 10,
                    }
                    : new FacetSkillEntry
                    {
                        Skill = entry.Skill,
                        Sac = entry.Sac,
                        Ranks = entry.Ranks,
                        Pp = entry.Pp,
                        InitLevel = entry.InitLevel,
                    });
            }

            return result;
        }
    }
}
