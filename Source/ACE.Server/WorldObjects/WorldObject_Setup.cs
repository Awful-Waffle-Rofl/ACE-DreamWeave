using System;
using System.Collections.Concurrent;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Setup (PropertyDataId.Setup) validation.
    ///
    /// A world object whose Setup DID does not name a parseable SetupModel used to take the entire
    /// world thread down: CSetup called straight into DatDatabase.ReadFromDat&lt;SetupModel&gt;(), and a
    /// DID that is not a 0x02 setup file (a GfxObj id, for instance) makes SetupModel.Unpack read past
    /// the end of the buffer and throw EndOfStreamException. That escaped Landblock.AddWorldObject ->
    /// ActionQueue.RunActions -> LandblockManager.TickMultiThreadedWork and stopped the world.
    ///
    /// The verdict for an id is memoized here, on the server side, and never in the dat layer's shared
    /// FileCache: that cache is keyed by file id alone, with no notion of type, so caching a fallback
    /// SetupModel under a GfxObj's id would hand a SetupModel back to a later ReadFromDat&lt;GfxObj&gt;()
    /// for the same id and turn one bad row into an InvalidCastException somewhere else entirely.
    /// </summary>
    partial class WorldObject
    {
        /// <summary>
        /// Memoized per-id verdict: true when the id reads back as a SetupModel.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, bool> validatedSetupIds = new ConcurrentDictionary<uint, bool>();

        /// <summary>
        /// Handed back by CSetup for an unusable Setup DID, so that every CSetup read site stays total:
        /// no parts, no physics BSP, no default animation or script. Downstream code already meets this
        /// shape today, because the dat loader produces an equally empty SetupModel whenever it has
        /// nothing to unpack.
        /// </summary>
        private static readonly SetupModel invalidSetupFallback = SetupModel.CreateSimpleSetup();

        /// <summary>
        /// Report-once memo, keyed by report site plus wcid. A bad Setup DID is otherwise re-reported on
        /// every spawn attempt, and a generator wired to such a wcid retries every regen tick forever.
        /// Returns true only the first time a given site sees a given wcid.
        /// </summary>
        private static readonly ConcurrentDictionary<(string, uint), byte> reportedBadSetups = new ConcurrentDictionary<(string, uint), byte>();

        /// <summary>
        /// True the first time <paramref name="site"/> is asked to report <paramref name="wcid"/>.
        /// Callers log at ERROR when it returns true, and at Debug afterwards.
        /// </summary>
        public static bool ShouldReportBadSetupId(string site, uint wcid)
        {
            return reportedBadSetups.TryAdd((site, wcid), 0);
        }

        /// <summary>
        /// True when setupId can be used as a SetupModel. Never throws.
        ///
        /// 0 means "no setup" and is reported valid: that is the existing behavior for an object with no
        /// PropertyDataId.Setup at all, and it is physics, not this check, that decides what to do with it.
        /// </summary>
        public static bool IsValidSetupId(uint setupId)
        {
            if (setupId == 0)
                return true;

            // dats not loaded (content tooling, unit tests) - nothing to validate against, so do not reject
            if (DatManager.PortalDat == null)
                return true;

            return validatedSetupIds.GetOrAdd(setupId, ValidateSetupId);
        }

        private static bool ValidateSetupId(uint setupId)
        {
            // Setup files are the 0x02 range of client_portal.dat. Anything else (0x01 GfxObj, 0x09 palette, ...)
            // is a different file format and will not unpack as a SetupModel. Reject it without reading, so the
            // wrong-typed read never reaches the dat layer's type-agnostic FileCache.
            if ((setupId >> 24) != 0x02)
                return false;

            // A 0x02 id that is simply not in the dat is NOT rejected here. ReadFromDat returns an
            // empty never-unpacked SetupModel for it (DatDatabase.cs:64-84 skips Unpack when the reader
            // is null), which is what it did before this guard existed, and the object spawns as a
            // degraded placeholder. That is pre-existing behavior, not the crash being fixed, so
            // rejecting it here would be an unrelated regression.
            try
            {
                return DatManager.PortalDat.ReadFromDat<SetupModel>(setupId) != null;
            }
            catch (Exception)
            {
                // truncated / malformed setup, or a FileCache entry of another type for this id
                return false;
            }
        }

        /// <summary>
        /// Reads setupId as a SetupModel, substituting a neutral empty model when it cannot be read.
        /// Never returns null and never throws.
        /// </summary>
        public static SetupModel GetSetupModel(uint setupId)
        {
            if (!IsValidSetupId(setupId))
                return invalidSetupFallback;

            try
            {
                return DatManager.PortalDat?.ReadFromDat<SetupModel>(setupId) ?? invalidSetupFallback;
            }
            catch (Exception)
            {
                return invalidSetupFallback;
            }
        }

        /// <summary>
        /// Reads setupId as a SetupModel. Returns false, with the neutral fallback model, when the id is unusable.
        /// </summary>
        public static bool TryGetSetup(uint setupId, out SetupModel setup)
        {
            var valid = IsValidSetupId(setupId);

            setup = GetSetupModel(setupId);

            return valid;
        }
    }
}
