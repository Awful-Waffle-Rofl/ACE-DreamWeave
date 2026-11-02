namespace ACE.Server.Knots
{
    /// <summary>
    /// Track B (Knotting Pouch) text. Kept in its own partial so the parallel Knots tracks never edit
    /// the same lines of KnotItems.cs.
    /// </summary>
    public static partial class KnotItems
    {
        /// <summary>A stack split aimed at a Pouch. Refused for every item, Fibers included.</summary>
        public const string PouchRefusesSplit = "Nothing can be split into the Knotting Pouch.";

        /// <summary>A stack merge aimed at an item inside a Pouch.</summary>
        public const string PouchRefusesMerge = "Nothing can be merged into the Knotting Pouch.";
    }
}
