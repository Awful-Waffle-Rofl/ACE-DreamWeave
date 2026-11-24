using System;
using System.Collections.Generic;

namespace ACE.Server.Managers
{
    /// <summary>
    /// A seeding layout: the fixed parked-source slot guids, the guid distance between one slot's item band and
    /// the next, and the name given to a parked source character. TesterRoster and TrialRoster each expose one, so
    /// the seed and verify tools can be driven by a layout instead of reading roster constants directly.
    /// </summary>
    public sealed record SeedLayout(string Name, IReadOnlyList<uint> SlotGuids, uint ItemBandStride, Func<int, string> ParkedName);
}
