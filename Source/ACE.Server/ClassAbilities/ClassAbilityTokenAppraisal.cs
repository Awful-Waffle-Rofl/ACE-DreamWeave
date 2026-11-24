using System.Collections.Generic;

using ACE.Entity.Enum.Properties;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Makes a class ability token's appraisal text follow the live tunables. The committed token weenie
    /// carries a LongDesc snapshot taken from the code defaults; at appraisal time this swaps it for the same
    /// text rendered from the CURRENT values, so a live @modifydouble shows on the next appraisal. Pure: it
    /// edits only the appraisal's own string dictionary and never touches the token's biota.
    /// </summary>
    public static class ClassAbilityTokenAppraisal
    {
        /// <summary>
        /// Replaces <see cref="PropertyString.LongDesc"/> in <paramref name="propertiesString"/> with the live
        /// token text. Returns true when it did. Leaves the dictionary alone (returns false) when:
        ///  - the id names no registered ability (a retired token, a bad id);
        ///  - the tier is outside 1..MaxRank.
        /// Every registered ability renders, generated families and tail-less abilities included, so the
        /// appraisal text and the committed token SQL both come from <see cref="ClassAbilityText.TokenLongDesc"/>.
        /// </summary>
        public static bool Apply(IDictionary<PropertyString, string> propertiesString, int tokenId, int tier, ITunableValues values)
        {
            if (propertiesString == null || tokenId <= 0)
                return false;

            if (!TryRender(tokenId, tier, values, out var text))
                return false;

            propertiesString[PropertyString.LongDesc] = text;
            return true;
        }

        /// <summary>
        /// The text any server-rendered surface should show for a token's LongDesc: the live rendering when
        /// the token is eligible (the same rules as <see cref="Apply"/>), otherwise <paramref name="committed"/>
        /// untouched. The shared helper behind the hooked-item appraisal text and the market listing text.
        /// </summary>
        public static string LongDesc(string committed, int tokenId, int tier, ITunableValues values) =>
            tokenId > 0 && TryRender(tokenId, tier, values, out var text) ? text : committed;

        private static bool TryRender(int tokenId, int tier, ITunableValues values, out string text)
        {
            text = null;

            if (tokenId <= 0)
                return false;

            var id = (ClassAbilityId)tokenId;

            if (!ClassAbilityRegistry.Abilities.TryGetValue(id, out var def))
                return false;


            if (tier < 1 || tier > def.MaxRank)
                return false;

            text = ClassAbilityText.TokenLongDesc(def, tier, values);
            if (text == null)
                return false;

            // The ceiling sentence rides on the appraisal only: TokenLongDesc is pinned against the committed token SQL.
            var limits = ClassAbilityCeilingText.Sentence(def, values);
            if (limits != null)
                text += " " + limits;

            return true;
        }
    }
}
