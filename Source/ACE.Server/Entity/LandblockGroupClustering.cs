using System;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The one pairwise rule that decides whether two landblocks belong in the same LandblockGroup (ticked on the same
    /// thread). Both LandblockGroup.ShouldBeAddedToThisLandblockGroup (growing a group) and
    /// LandblockGroupSplitHelper.ShouldBeAddedToThisLandblockGroup (splitting one) call it, so growing and splitting
    /// can never disagree. Pure, on coordinates rather than Landblock objects, because a Landblock cannot be
    /// constructed in a unit test. See LandblockGroupClusteringTests.
    /// </summary>
    public static class LandblockGroupClustering
    {
        /// <summary>
        /// True when landblock A (x, y, dormancy, instance) and landblock B must share a tick group: they may share
        /// one at all (Realms.EphemeralInstanceGroup.MayShareTickGroup - an ephemeral instance only with itself, every
        /// persistent instance with every other) and they are closer than the spacing, which is
        /// LandblockGroup.LandblockGroupMinSpacingWhenDormant when either is dormant and
        /// LandblockGroup.LandblockGroupMinSpacing otherwise. Distance is Chebyshev, in landblocks.
        /// </summary>
        public static bool MayCluster(int ax, int ay, bool aDormant, uint aInstance, int bx, int by, bool bDormant, uint bInstance)
        {
            if (!Realms.EphemeralInstanceGroup.MayShareTickGroup(aInstance, bInstance))
                return false;

            var distance = Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

            var spacing = aDormant || bDormant
                ? LandblockGroup.LandblockGroupMinSpacingWhenDormant
                : LandblockGroup.LandblockGroupMinSpacing;

            return distance < spacing;
        }

        /// <summary>The Landblock form of <see cref="MayCluster(int, int, bool, uint, int, int, bool, uint)"/>.</summary>
        public static bool MayCluster(Landblock a, Landblock b) =>
            MayCluster(a.Id.LandblockX, a.Id.LandblockY, a.IsDormant, a.Instance, b.Id.LandblockX, b.Id.LandblockY, b.IsDormant, b.Instance);
    }
}
