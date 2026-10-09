
namespace ACE.Common
{
    public class PreloadedLandblocks
    {
        /// <summary>
        /// id of landblock to preload
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// description of landblock
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// whether or not this landblock is permaloaded
        /// </summary>
        public bool Permaload { get; set; } = false;

        /// <summary>
        /// whether or not this landblock should also load adjacents, if Permaload is true, the ajacent landblocks will also be marked permaload
        /// </summary>
        public bool IncludeAdjacents { get; set; } = false;

        /// <summary>
        /// whether or not this landblock is included for preload.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Realms: which realm's copy of this landblock to preload. 0 is the base world and
        /// is what every entry without this field means, so existing configs are unchanged.
        /// A nonzero value preloads the landblock in that realm's default (non-ephemeral)
        /// instance instead. To permaload both, add a second entry for the same Id.
        /// </summary>
        public ushort Realm { get; set; } = 0;
    }
}
