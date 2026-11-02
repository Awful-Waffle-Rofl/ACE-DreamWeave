using System.Collections.Generic;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Replacement bodies for donors whose own body draws nothing.
    ///
    /// WHY THIS EXISTS. Some retail creatures are invisible BY DESIGN: the Dark Sarcophagus (wcid
    /// 22116, Setup 0x02000E1B) is one clip-mapped quad plus sixteen fully translucent parts, so the
    /// client draws nothing but the floating name. A player who completed a Beast Effigy attuned to
    /// it ended up with an invisible vendor. The attunement gate (MuleFormToken) now refuses such a
    /// body, but the gate is attunement-only, so an already-saved form keeps loading; and the
    /// Sarcophagus is a form the owner wants to keep attunable. So its look is substituted at body-swap
    /// time with a visible stand-in model.
    ///
    /// A STAND-IN IS A BODY SUBSTITUTION, NOT A GATE. It changes only which model the summoned mule
    /// wears (see MuleFormBody.CloneWithDonorBody); the donor's CreatureType and name still drive
    /// appraisal. A donor with an entry here is also exempt from the "draws nothing" refusal at
    /// attunement, because the stand-in is what makes it visible.
    ///
    /// The table is code-side and keyed by DONOR wcid. The saved form is per-account by wcid and is
    /// read at every summon, so an entry here repairs an existing saved form with no data change.
    /// </summary>
    public static class MuleFormStandIn
    {
        /// <summary>
        /// A stand-in body. Every field is an authored client dat id except <see cref="DefaultScale"/>,
        /// which is explicit because the height-normalising scale the ordinary swap derives from the
        /// donor's Setup is meaningless for a stand-in chosen for its look, not for matching a donor.
        /// </summary>
        public readonly struct Body
        {
            public Body(uint setup, uint motionTable, uint soundTable, uint icon, double defaultScale)
            {
                Setup = setup;
                MotionTable = motionTable;
                SoundTable = soundTable;
                Icon = icon;
                DefaultScale = defaultScale;
            }

            public uint Setup { get; }
            public uint MotionTable { get; }
            public uint SoundTable { get; }
            public uint Icon { get; }
            public double DefaultScale { get; }
        }

        /// <summary>The Dark Sarcophagus weenie (mysterioussarcophagus).</summary>
        public const uint DarkSarcophagusWcid = 22116;

        /// <summary>
        /// The coffin stand-in's DefaultScale. ONE OBVIOUS CONSTANT: the coffin Setup is
        /// 2.13 x 0.91 x 0.63 m at scale 1, and the right size beside a mule is a call to make in game.
        /// </summary>
        public const double CoffinDefaultScale = 1.0;

        // The retail coffin used by Sarcophagus (wcid 167) and its coffin* siblings.
        private static readonly Body Coffin = new Body(
            setup: 0x020000CE,
            motionTable: 0x09000024,
            soundTable: 0x20000025,
            icon: 0x060012C7,
            defaultScale: CoffinDefaultScale);

        private static readonly Dictionary<uint, Body> byDonorWcid = new Dictionary<uint, Body>
        {
            { DarkSarcophagusWcid, Coffin },
        };

        /// <summary>The stand-in body for a donor wcid, or false when the donor wears its own body.</summary>
        public static bool TryGet(uint donorWcid, out Body body) => byDonorWcid.TryGetValue(donorWcid, out body);

        public static bool Has(uint donorWcid) => byDonorWcid.ContainsKey(donorWcid);
    }
}
