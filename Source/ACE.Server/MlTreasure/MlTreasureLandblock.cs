namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// The Marae Lassel gate: a pure, allocation-free predicate over a raw landblock id and realm id.
    /// Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 4: bounding box x 0x0C-0x2E, y 0xAC-0xCA, AND
    /// realm 1 - retail Marae Lassel exists in realm 0, and realm 1 also carries non-ML landblocks, so
    /// neither check alone is sufficient. Intended to run on the landblock tick / creature death path
    /// (section 6's drop hook, step 7), so this stays two integer comparisons plus two byte masks - no
    /// allocation, no lookup.
    /// </summary>
    public static class MlTreasureLandblock
    {
        public const ushort MinLbX = 0x0C;
        public const ushort MaxLbX = 0x2E;
        public const ushort MinLbY = 0xAC;
        public const ushort MaxLbY = 0xCA;

        public const ushort MlRealmId = 1;

        /// <summary>True only when realm is 1 AND the landblock's high byte (x) falls in 0x0C-0x2E AND
        /// its low byte (y) falls in 0xAC-0xCA.</summary>
        public static bool IsMaraeLassel(ushort landblock, ushort realm)
        {
            if (realm != MlRealmId)
                return false;

            var lbX = (ushort)((landblock >> 8) & 0xFF);
            var lbY = (ushort)(landblock & 0xFF);

            return lbX >= MinLbX && lbX <= MaxLbX && lbY >= MinLbY && lbY <= MaxLbY;
        }
    }
}
