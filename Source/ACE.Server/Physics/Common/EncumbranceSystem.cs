namespace ACE.Server.Physics.Common
{
    public class EncumbranceSystem
    {
        /// <summary>
        /// The retail carrying-capacity formula. NOTE: this currently has no callers. Every site that used it
        /// was a player path, and all of them now route through Player.GetEncumbranceCapacity, which is the
        /// single authority for a Player's capacity (a mule overrides it outright, so a second implementation
        /// would silently disagree with the pickup gate). Kept because it is the reference formula and the
        /// natural home for a future non-Player creature that needs one. GetBurden / GetBurdenMod below are
        /// still very much in use.
        /// </summary>
        public static int EncumbranceCapacity(int strength, int numAugs)
        {
            if (strength <= 0) return 0;

            var bonusBurden = 30 * numAugs;

            if (bonusBurden >= 0)
            {
                if (bonusBurden > 150)
                    bonusBurden = 150;

                return 150 * strength + strength * bonusBurden;
            }
            else
                return 150 * strength;
        }

        public static float GetBurden(int capacity, int encumbrance)
        {
            if (capacity <= 0) return 3.0f;

            if (encumbrance >= 0)
                return (float)encumbrance / capacity;
            else
                return 0.0f;
        }

        public static float GetBurdenMod(float burden)
        {
            if (burden < 1.0f) return 1.0f;

            if (burden < 2.0f)
                return 2.0f - burden;
            else
                return 0.0f;
        }
    }
}
