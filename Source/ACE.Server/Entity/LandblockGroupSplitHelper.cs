using System;
using System.Collections;
using System.Collections.Generic;

namespace ACE.Server.Entity
{
    class LandblockGroupSplitHelper : IEnumerable<Landblock>
    {
        private readonly HashSet<Landblock> landblocks = new HashSet<Landblock>();

        public int Count => landblocks.Count;

        public void Add(Landblock landblock)
        {
            landblocks.Add(landblock);
        }

        public IEnumerator<Landblock> GetEnumerator()
        {
            return landblocks.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }


        public bool ShouldBeAddedToThisLandblockGroup(Landblock landblock)
        {
            // The same pairwise rule LandblockGroup grows by, so a split can never separate what growing joined.
            foreach (var value in landblocks)
            {
                if (LandblockGroupClustering.MayCluster(value, landblock))
                    return true;
            }

            return false;
        }
    }
}
