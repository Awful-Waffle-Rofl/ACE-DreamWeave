using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

using ACE.Database.Models.World;

namespace ACE.Server.Entity
{
    /// <summary>
    /// WaffleACE fork: the PURE layout math behind server-generated sky decor. Everything here is a
    /// function of its arguments - no clock, no database, no landblock, no static mutable state - so
    /// the whole placement algorithm is unit-testable without a world, and so that two servers running
    /// the same content agree on where every cloud is.
    ///
    /// Determinism is the point of this class, and it rests on two deliberate choices:
    ///  - the per-landblock seed comes from <see cref="StableHash"/>, an explicit FNV-1a mixer.
    ///    NOT HashCode.Combine and NOT string.GetHashCode: both are randomized per process, so a
    ///    layout built on either would differ between two runs of the same server.
    ///  - the PRNG is <see cref="SkyDecorRandom"/>, a 12-line xorshift written out here rather than
    ///    System.Random, whose seeded sequence is a runtime implementation detail.
    ///
    /// The RNG draw order below is part of the contract: adding a draw in the middle reshuffles every
    /// existing region. Append new draws at the end of the per-cloud block, or bump a region's version.
    /// </summary>
    public static class SkyDecorLayout
    {
        /// <summary>Terrain cells per landblock side (LandDefs.BlockSide).</summary>
        public const int CellSide = 8;

        /// <summary>Metres per terrain cell (LandDefs.CellLength).</summary>
        public const float CellLength = 24.0f;

        /// <summary>Metres per landblock side.</summary>
        public const float BlockLength = CellSide * CellLength;

        /// <summary>Maximum horizontal jitter from a cell centre, metres. Half the cell, so a cloud never leaves the cell it was assigned.</summary>
        public const float CellJitter = 6.0f;

        /// <summary>
        /// Disc radius per unit of ObjScale: half the rig's 1.45 m swirl quad. This is what the
        /// no-overlap cull measures with, so scale 240 is a 174 m radius - nearly a whole landblock.
        /// </summary>
        public const float DiscRadiusPerScale = 0.725f;

        /// <summary>Metres a 'mast' origin is held above the terrain. Never zero - see <see cref="OriginZMast"/>.</summary>
        public const float MastOriginClearance = 1.0f;

        /// <summary>How far a neighbour search may reach, in landblocks, whatever the region asks for.</summary>
        public const int MaxNeighbourBlockRadius = 3;

        public const string RigInverted = "inverted";
        public const string RigMast = "mast";

        /// <summary>Anything that is not 'mast' (case-insensitive) is the original inverted rig.</summary>
        public static bool IsMast(string rig)
        {
            return string.Equals(rig, RigMast, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------------------------
        // Stable hashing
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// FNV-1a over the four little-endian int32s. Stable across processes, machines and .NET
        /// versions - that is the entire reason it is written out instead of calling a framework hash.
        /// </summary>
        public static uint StableHash(int seed, int version, int realmId, int landblockId)
        {
            unchecked
            {
                var h = 2166136261u;

                h = MixInt32(h, seed);
                h = MixInt32(h, version);
                h = MixInt32(h, realmId);
                h = MixInt32(h, landblockId);

                return h;
            }
        }

        private static uint MixInt32(uint h, int value)
        {
            unchecked
            {
                var v = (uint)value;

                for (var i = 0; i < 4; i++)
                {
                    h ^= (v >> (i * 8)) & 0xFF;
                    h *= 16777619u;
                }

                return h;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Palette
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Parses a palette CSV. Each token is `colour` or `wcid`, optionally followed by `:weight`.
        /// A missing weight is 1; a zero or negative weight is kept as 0, which means the entry is parsed
        /// but never picked (that is how a colour is switched off without editing the list out).
        /// Unparseable entries are dropped.
        ///
        /// Colour NAMES (purple, red, orange, ...) resolve through <see cref="SkyDecorColours"/>, which is
        /// the single owner of the name-to-wcid table. Names and raw wcids may be mixed freely in one
        /// palette, and a name is matched case-insensitively.
        /// </summary>
        public static List<SkyDecorPaletteEntry> ParsePalette(string csv)
        {
            return ParsePalette(csv, null);
        }

        /// <summary>
        /// <see cref="ParsePalette(string)"/>, additionally collecting a human-readable line for every
        /// token it had to drop and for every entry that is accepted but discouraged.
        ///
        /// Two callers need those lines and need them differently, which is why they are RETURNED rather
        /// than logged from here: `/sky-decor set` refuses the whole edit and shows them to the player,
        /// while the database load path skips the bad token and logs once (SkyDecorColours.WarnAboutPalette).
        /// Logging in here would make the strict caller unable to be strict, and would fire per landblock.
        /// </summary>
        /// <param name="problems">appended to; may be null, which skips the reporting entirely</param>
        public static List<SkyDecorPaletteEntry> ParsePalette(string csv, List<string> problems)
        {
            var entries = new List<SkyDecorPaletteEntry>();

            if (string.IsNullOrWhiteSpace(csv))
                return entries;

            foreach (var raw in csv.Split(','))
            {
                var token = raw.Trim();

                if (token.Length == 0)
                    continue;

                var nameText = token;
                var weight = 1;

                var colon = token.IndexOf(':');

                if (colon >= 0)
                {
                    nameText = token.Substring(0, colon).Trim();

                    if (!int.TryParse(token.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out weight))
                    {
                        problems?.Add($"'{token}': '{token.Substring(colon + 1).Trim()}' is not a whole-number weight");
                        continue;
                    }

                    if (weight < 0)
                        weight = 0;
                }

                // A name first, then a raw wcid. Names cannot collide with wcids (one is not a number),
                // so the order is a readability choice rather than a precedence rule.
                var colour = SkyDecorColours.ByName(nameText);

                if (colour != null)
                {
                    if (colour.Retired)
                        problems?.Add($"'{colour.Name}' ({colour.WeenieClassId}) is {colour.Note}");

                    entries.Add(new SkyDecorPaletteEntry(colour.WeenieClassId, weight));

                    continue;
                }

                if (!uint.TryParse(nameText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wcid) || wcid == 0)
                {
                    problems?.Add($"'{nameText}' is not a wcid or a known colour name. Names: {SkyDecorColours.NameList}");
                    continue;
                }

                var byWcid = SkyDecorColours.ByWcid(wcid);

                if (byWcid != null && byWcid.Retired)
                    problems?.Add($"'{byWcid.Name}' ({wcid}) is {byWcid.Note}");

                entries.Add(new SkyDecorPaletteEntry(wcid, weight));
            }

            return entries;
        }

        // ---------------------------------------------------------------------------------------
        // Geometry
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Where the OBJECT ORIGIN goes so that the DISC CENTRE ends up <paramref name="height"/> metres
        /// above <paramref name="groundZ"/>. The rig hangs part 0 at partOffset * scale along the object's
        /// local +Z; the object is rolled (180 - tilt) degrees about a horizontal axis, so the vertical
        /// component of that offset is partOffset * scale * cos(180 - tilt). At tilt 0 the disc hangs
        /// straight down and the origin sits partOffset * scale ABOVE it.
        /// </summary>
        public static float OriginZ(float groundZ, float height, float partOffset, float scale, float tiltDeg)
        {
            var theta = (180.0 - tiltDeg) * Math.PI / 180.0;

            return (float)(groundZ + height - partOffset * scale * Math.Cos(theta));
        }

        /// <summary>
        /// The 'mast' equivalent: the disc sits ABOVE the origin, so the origin is pinned near the ground
        /// instead of riding high above the disc. That is the whole point of the rig - the client only
        /// animates an object whose ORIGIN is within roughly 60-90 m of the character, and an inverted
        /// disc at these scales parks its origin 44-110 m up where nothing ever spins.
        ///
        /// The origin is never put below the terrain, and not merely as tidiness: the spinning-scenery
        /// doctrine records a below-terrain origin being snapped up to the surface and dragging its disc
        /// up with it, which silently discards the intended height. So the origin is held at least
        /// <see cref="MastOriginClearance"/> above the ground, and the disc centre therefore lands at
        /// max(rolled height, partOffset * scale * cos(tilt) + clearance) - at scale 96-240 the size term
        /// wins outright and the height columns go nearly inert.
        /// </summary>
        public static float OriginZMast(float groundZ, float height, float partOffset, float scale, float tiltDeg)
        {
            var theta = tiltDeg * Math.PI / 180.0;

            var lift = partOffset * scale * Math.Cos(theta);

            return (float)(groundZ + Math.Max(MastOriginClearance, height - lift));
        }

        /// <summary>
        /// qz(yaw) * qx(180 - tilt), in ACE's W,X,Y,Z order. At tilt 0 this is the approved "flat" pose:
        /// W = 0, and (X, Y) is the yawed horizontal axis - the disc hangs below the origin, parallel to
        /// the ground, with the swirl's arm handedness preserved by the full 180 degree mirror.
        /// </summary>
        public static void Orientation(float yawDeg, float tiltDeg, out float w, out float x, out float y, out float z)
        {
            Orientation(yawDeg, tiltDeg, false, out w, out x, out y, out z);
        }

        /// <summary>
        /// qz(yaw) * qx(theta), where theta is (180 - tilt) for the 'inverted' rig and plain `tilt` for
        /// 'mast'. The two rigs are the same construction with one term flipped, which is what keeps the
        /// inverted path bit-for-bit identical to what it was before 'mast' existed.
        /// </summary>
        public static void Orientation(float yawDeg, float tiltDeg, bool mast, out float w, out float x, out float y, out float z)
        {
            var yaw = yawDeg * Math.PI / 180.0;
            var theta = (mast ? tiltDeg : 180.0 - tiltDeg) * Math.PI / 180.0;

            var c1 = Math.Cos(yaw / 2.0);
            var s1 = Math.Sin(yaw / 2.0);
            var c2 = Math.Cos(theta / 2.0);
            var s2 = Math.Sin(theta / 2.0);

            w = (float)(c1 * c2);
            x = (float)(c1 * s2);
            y = (float)(s1 * s2);
            z = (float)(s1 * c2);
        }

        /// <summary>
        /// The unit vector from a disc's ORIGIN to its disc CENTRE - the object's local +Z after the
        /// orientation quaternion is applied. Because the flat pose is a 180 degree roll, this points
        /// DOWN-ish: straight down at tilt 0, leaning by `tilt` degrees away from vertical otherwise.
        ///
        /// Everything about the pair geometry is expressed along this vector, which is what keeps the two
        /// discs parallel and the gap between their planes equal to pair_gap however the pair is tilted.
        /// Deriving it here rather than rotating a vector by the quaternion keeps it exact and cheap:
        /// qz(yaw) * qx(180 - tilt) applied to (0, 0, 1) is
        /// (sin(theta) * sin(yaw), -sin(theta) * cos(yaw), cos(theta)) with theta = 180 - tilt.
        /// </summary>
        public static Vector3 DiscNormal(float yawDeg, float tiltDeg)
        {
            return DiscNormal(yawDeg, tiltDeg, false);
        }

        /// <summary>
        /// The rig-aware disc normal. 'inverted' points DOWN-ish (disc below the origin); 'mast' points
        /// UP-ish (disc above it). Everything about the pair geometry runs along this vector for both.
        /// </summary>
        public static Vector3 DiscNormal(float yawDeg, float tiltDeg, bool mast)
        {
            var yaw = yawDeg * Math.PI / 180.0;
            var theta = (mast ? tiltDeg : 180.0 - tiltDeg) * Math.PI / 180.0;

            var sinTheta = Math.Sin(theta);

            return new Vector3(
                (float)(sinTheta * Math.Sin(yaw)),
                (float)(-sinTheta * Math.Cos(yaw)),
                (float)Math.Cos(theta));
        }

        /// <summary>
        /// Weighted palette pick. <paramref name="roll01"/> is a uniform draw in [0, 1); it is scaled by
        /// the ELIGIBLE weight inside, so an exclusion re-normalises rather than leaving a dead band that
        /// would bias the pick toward the last entry.
        ///
        /// <paramref name="excludeWcid"/> of 0 excludes nothing. When excluding leaves no weight at all
        /// (a one-colour palette, or every other entry at weight 0) the excluded wcid is returned, which
        /// is how a single-entry palette gives a partner the same colour as its primary.
        /// </summary>
        public static uint PickFromPalette(List<SkyDecorPaletteEntry> palette, double roll01, uint excludeWcid)
        {
            var eligible = 0;

            foreach (var entry in palette)
            {
                if (entry.WeenieClassId != excludeWcid)
                    eligible += entry.Weight;
            }

            if (eligible <= 0)
                return excludeWcid;

            var pick = roll01 * eligible;
            var running = 0.0;

            var last = 0u;

            foreach (var entry in palette)
            {
                if (entry.WeenieClassId == excludeWcid || entry.Weight <= 0)
                    continue;

                last = entry.WeenieClassId;

                running += entry.Weight;

                if (pick < running)
                    return entry.WeenieClassId;
            }

            // only reachable through floating-point slop at the very top of the range
            return last;
        }

        /// <summary>
        /// The landcell id for a landblock-local point, matching Physics.Common.Landblock.GetCell:
        /// (landblock &lt;&lt; 16) | (cellX * 8 + cellY + 1).
        /// </summary>
        public static uint CellId(ushort landblockId, float x, float y)
        {
            var cellX = Math.Clamp((int)(x / CellLength), 0, CellSide - 1);
            var cellY = Math.Clamp((int)(y / CellLength), 0, CellSide - 1);

            return ((uint)landblockId << 16) | (uint)(cellX * CellSide + cellY + 1);
        }

        // ---------------------------------------------------------------------------------------
        // Planning
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Priority for the no-overlap cull. Lower wins.
        ///
        /// Keyed on (seed, version, landblock, index) and NOT on the region's database id, deliberately:
        /// `id` is an autoincrement surrogate that changes every time the content unit is re-applied
        /// (observed going 1 -> 2 between two applies of the same file), which would silently reshuffle
        /// which discs survive across the whole region. Seed and version are the region's content
        /// identity and are what every other part of this layout already keys on. Regions never compare
        /// candidates with each other - the cull runs per region - so seed+version is distinct enough.
        ///
        /// The salt keeps this stream clear of the per-block RNG seed, which mixes the same first terms.
        /// </summary>
        public static uint CandidatePriority(int seed, int version, ushort landblockId, int index)
        {
            unchecked
            {
                var h = 2166136261u;

                h = MixInt32(h, unchecked((int)0x5C1D3C0Du));
                h = MixInt32(h, seed);
                h = MixInt32(h, version);
                h = MixInt32(h, landblockId);
                h = MixInt32(h, index);

                return h;
            }
        }

        /// <summary>
        /// Per-cloud opacity roll. Uniform in [0, 1), same conversion SkyDecorRandom.NextDouble uses.
        ///
        /// This is a SIDE CHANNEL, not an `rng` draw, and that is the entire reason it exists as its own
        /// hash rather than another rng.Range call in the per-cloud block: the RNG draw order is a
        /// contract (see the class header), and the shipped Marae region is deliberately not having its
        /// version bumped, so every existing cloud has to stay exactly where, how big, and what colour it
        /// already is. Reading this dial off an independent hash means turning translucency on or tuning
        /// its range can never move a single existing cloud, however it is wired into PlanCandidates.
        ///
        /// The salt is its own distinct constant (not CandidatePriority's 0x5C1D3C0D) so the two side
        /// channels cannot collide into the same stream by accident.
        /// </summary>
        public static double TranslucencyRoll(int seed, int version, ushort landblockId, int index)
        {
            unchecked
            {
                var h = 2166136261u;

                h = MixInt32(h, unchecked((int)0x9B1F1A6Bu));
                h = MixInt32(h, seed);
                h = MixInt32(h, version);
                h = MixInt32(h, landblockId);
                h = MixInt32(h, index);

                return (h >> 8) * (1.0 / 16777216.0);
            }
        }

        /// <summary>
        /// How many landblocks out the cull has to look, derived rather than fixed at the 8 adjacents.
        ///
        /// The 8 adjacents are NOT enough at these sizes: a scale-240 disc has a 174 m radius, so two of
        /// them conflict up to 348 m apart while a landblock is only 192 m. Two candidates two blocks
        /// apart genuinely can overlap, and a fixed 3x3 search would let that pair through while still
        /// reporting the region as overlap-free. Capped at <see cref="MaxNeighbourBlockRadius"/> so a
        /// pathological row cannot make one landblock activation plan hundreds of blocks.
        /// </summary>
        public static int NeighbourBlockRadius(SkyDecorRegion region)
        {
            if (region == null || region.MinSeparation <= 0.0f)
                return 0;

            var maxRadius = DiscRadiusPerScale * Math.Max(region.ScaleMin, region.ScaleMax);

            var conflictDistance = region.MinSeparation * 2.0f * maxRadius;

            var blocks = (int)Math.Ceiling(conflictDistance / BlockLength);

            return Math.Clamp(blocks, 1, MaxNeighbourBlockRadius);
        }

        /// <summary>
        /// Everything one landblock's RNG produces BEFORE any terrain is consulted: positions, sizes,
        /// colours and the pairing decision, but no ground height and no water rejection.
        ///
        /// Split out from the cloud building precisely so a landblock can generate its NEIGHBOURS'
        /// candidates for the overlap cull without needing their terrain. It is pure - the same region row
        /// and landblock id give the same list anywhere - which is what lets two adjacent blocks reach the
        /// same verdict about a pair of discs straddling their boundary.
        /// </summary>
        public static List<SkyDecorCandidate> PlanCandidates(SkyDecorRegion region, ushort landblockId)
        {
            var candidates = new List<SkyDecorCandidate>();

            if (region == null)
                return candidates;

            var palette = ParsePalette(region.Palette);

            var totalWeight = 0;

            foreach (var entry in palette)
                totalWeight += entry.Weight;

            if (totalWeight <= 0)
                return candidates;

            var rng = new SkyDecorRandom(StableHash(region.Seed, region.Version, region.RealmId, landblockId));

            // Count: the whole part always, the fractional part as a per-block probability - density 2.5
            // gives two clouds in every block and a third in about half of them, decided once per block
            // and stable forever for a given seed/version.
            //
            // The draw is taken FIRST, before any per-cloud draw, so a density edit does not reshuffle
            // the clouds a block already had. It is also ALWAYS consumed, even at a whole density where
            // its result cannot matter - `rng.NextDouble() < frac` evaluates the call before the compare.
            // That keeps the stream position of every later draw independent of the density value, so
            // moving 2 -> 2.5 adds a third cloud without moving the first two.
            var density = region.Density;

            if (density <= 0.0f)
                return candidates;

            var whole = (int)Math.Floor(density);
            var frac = density - whole;

            var count = whole + (rng.NextDouble() < frac ? 1 : 0);

            // One cloud per terrain cell at most - "distinct cell" is what keeps them spread out.
            count = Math.Min(count, CellSide * CellSide);

            if (count <= 0)
                return candidates;

            // FULL Fisher-Yates over the 64 cells, then take the first `count`. Distinct by
            // construction, and no rejection loop whose cost depends on how full the block is.
            //
            // Shuffling ALL of them, not just the first `count`, is what makes density a clean dial: the
            // block's cell ORDER is now fixed regardless of how many clouds are taken from it, and each
            // cloud consumes a fixed number of draws afterwards, so raising density strictly ADDS clouds
            // instead of reshuffling the ones already there. A partial shuffle consumed `count` draws and
            // therefore shifted every later draw in the block - moving 2 -> 2.5 rebuilt the whole sky
            // rather than adding one disc to half the blocks. The extra 63 xorshift steps per block per
            // region are nothing against one landblock activation.
            var cells = new int[CellSide * CellSide];

            for (var i = 0; i < cells.Length; i++)
                cells[i] = i;

            for (var i = 0; i < cells.Length - 1; i++)
            {
                var j = i + rng.NextInt(cells.Length - i);

                (cells[i], cells[j]) = (cells[j], cells[i]);
            }

            var scaleMin = Math.Min(region.ScaleMin, region.ScaleMax);
            var scaleMax = Math.Max(region.ScaleMin, region.ScaleMax);
            var heightMin = Math.Min(region.HeightMin, region.HeightMax);
            var heightMax = Math.Max(region.HeightMin, region.HeightMax);
            var spinMin = Math.Min(region.SpinMin, region.SpinMax);
            var spinMax = Math.Max(region.SpinMin, region.SpinMax);
            var tiltMax = Math.Max(0.0f, region.TiltMaxDeg);

            var blockX = landblockId >> 8;
            var blockY = landblockId & 0xFF;

            for (var i = 0; i < count; i++)
            {
                var cellX = cells[i] / CellSide;
                var cellY = cells[i] % CellSide;

                // Every draw for this cloud happens up front and unconditionally, so the RNG stream - and
                // therefore every other cloud in the block - is identical whether or not this one is later
                // dropped for water or for overlapping a neighbour. Skipping a draw would make the layout
                // depend on terrain, and the overlap cull depend on the order landblocks activate in.
                var jitterX = rng.Range(-CellJitter, CellJitter);
                var jitterY = rng.Range(-CellJitter, CellJitter);
                var scale = rng.Range(scaleMin, scaleMax);
                var height = rng.Range(heightMin, heightMax);
                var spin = rng.Range(spinMin, spinMax);
                var tilt = rng.Range(0.0, tiltMax);
                var yaw = rng.Range(0.0, 360.0);
                var pick = rng.NextDouble();

                // The partner's two draws are APPENDED to the per-cloud block, never interleaved into it,
                // so turning pairing on or off cannot move a single primary disc. Both are drawn
                // unconditionally - including when the pair roll loses - so that pair_chance changes the
                // outcome without changing the stream.
                var pairRoll = rng.NextDouble();
                var partnerPick = rng.NextDouble();

                var wcid = PickFromPalette(palette, pick, 0);

                var x = (float)(cellX * CellLength + CellLength / 2.0 + jitterX);
                var y = (float)(cellY * CellLength + CellLength / 2.0 + jitterY);

                // Deliberately OUTSIDE the block of rng. draws above: TranslucencyRoll is an independent
                // hash, not a stream draw, so it cannot shift a single later `rng.` call. See the doc
                // comment on TranslucencyRoll for why that separation is load-bearing here.
                var translucencyMin = Math.Min(region.TranslucencyMin, region.TranslucencyMax);
                var translucencyMax = Math.Max(region.TranslucencyMin, region.TranslucencyMax);
                var translucencyRoll = TranslucencyRoll(region.Seed, region.Version, landblockId, i);
                var translucency = Math.Clamp(translucencyMin + (translucencyMax - translucencyMin) * translucencyRoll, 0.0, 1.0);

                candidates.Add(new SkyDecorCandidate
                {
                    LandblockId = landblockId,
                    Index = i,
                    Priority = CandidatePriority(region.Seed, region.Version, landblockId, i),
                    CellX = cellX,
                    CellY = cellY,
                    X = x,
                    Y = y,
                    WorldX = blockX * BlockLength + x,
                    WorldY = blockY * BlockLength + y,
                    Radius = DiscRadiusPerScale * (float)scale,
                    Scale = (float)scale,
                    Height = (float)height,
                    Spin = (float)spin,
                    TiltDeg = (float)tilt,
                    YawDeg = (float)yaw,
                    WeenieClassId = wcid,
                    Paired = pairRoll < region.PairChance,
                    PartnerWeenieClassId = PickFromPalette(palette, partnerPick, wcid),
                    Translucency = (float)translucency,
                });
            }

            return candidates;
        }

        /// <summary>
        /// True when two candidates are too close to coexist: their XY centres are nearer than
        /// min_separation times the sum of their PRIMARY disc radii. A pair counts as one unit for this -
        /// the partner shares the primary's XY and is smaller, so the primary's radius bounds both.
        /// Compared in world metres, so the test is frame-independent and exactly symmetric.
        /// </summary>
        public static bool Conflicts(SkyDecorRegion region, SkyDecorCandidate a, SkyDecorCandidate b)
        {
            if (region.MinSeparation <= 0.0f)
                return false;

            var dx = a.WorldX - b.WorldX;
            var dy = a.WorldY - b.WorldY;

            var needed = region.MinSeparation * (a.Radius + b.Radius);

            return dx * dx + dy * dy < needed * needed;
        }

        /// <summary>
        /// Strict total order on candidates: lower priority hash wins, ties broken by landblock then
        /// index. Without the tie-break a hash collision would leave two candidates each believing the
        /// other outranked it, and BOTH would drop out.
        /// </summary>
        public static bool Beats(SkyDecorCandidate a, SkyDecorCandidate b)
        {
            if (a.Priority != b.Priority)
                return a.Priority < b.Priority;

            if (a.LandblockId != b.LandblockId)
                return a.LandblockId < b.LandblockId;

            return a.Index < b.Index;
        }

        /// <summary>
        /// The no-overlap cull. A candidate survives iff NO higher-priority candidate conflicts with it.
        ///
        /// That exact rule - rather than a greedy "keep it if it clears everything kept so far" - is what
        /// makes the verdict globally consistent without a global view. A candidate's fate depends only on
        /// the candidates inside its own conflict radius and their priorities, never on whether those
        /// competitors themselves survived, so there is no chain to follow and every landblock that can
        /// see a given pair reaches the same answer about it. Greedy would need the chain, and the chain
        /// can run off the edge of any finite search window. The price is that it culls more than greedy
        /// would: if C loses to B and B loses to A, C stays dead even though B is gone.
        ///
        /// One consequence is worth knowing before tuning a region, because it looks like a bug: survivor
        /// count is NOT monotonic in density. Measured on the shipped Marae values (scale 96-240,
        /// min_separation 1.0), density 2.5 keeps 9 of 64 candidates while density 5.0 keeps 7 of 125 -
        /// more candidates means more SUPPRESSORS, and a suppressor that is itself culled still counts.
        /// When a region is separation-bound, the dial that adds discs is a smaller scale or a smaller
        /// min_separation, never a bigger density.
        /// </summary>
        public static List<SkyDecorCandidate> Cull(SkyDecorRegion region, ushort landblockId, List<SkyDecorCandidate> own)
        {
            if (region.MinSeparation <= 0.0f || own.Count == 0)
                return own;

            var radius = NeighbourBlockRadius(region);

            var universe = new List<SkyDecorCandidate>(own);

            var blockX = landblockId >> 8;
            var blockY = landblockId & 0xFF;

            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    var nx = blockX + dx;
                    var ny = blockY + dy;

                    if (nx < 0 || nx > 0xFF || ny < 0 || ny > 0xFF)
                        continue;

                    var neighbour = (ushort)((nx << 8) | ny);

                    // A block the region does not cover has no clouds, so nothing to compete with.
                    if (!region.Covers(neighbour, region.RealmId))
                        continue;

                    universe.AddRange(PlanCandidates(region, neighbour));
                }
            }

            var survivors = new List<SkyDecorCandidate>();

            foreach (var candidate in own)
            {
                var keep = true;

                foreach (var other in universe)
                {
                    if (other.LandblockId == candidate.LandblockId && other.Index == candidate.Index)
                        continue;

                    if (!Beats(other, candidate))
                        continue;

                    if (Conflicts(region, candidate, other))
                    {
                        keep = false;
                        break;
                    }
                }

                if (keep)
                    survivors.Add(candidate);
            }

            return survivors;
        }

        /// <summary>
        /// Plans one landblock's worth of clouds for one region, with the counts /sky-decor info reports.
        /// </summary>
        /// <param name="region">the region row; only its values are read, nothing is written back</param>
        /// <param name="landblockId">(x &lt;&lt; 8) | y</param>
        /// <param name="groundZ">terrain height at a landblock-local (x, y). Never null in the server; a
        /// constant is fine in tests.</param>
        /// <param name="isWater">true when terrain cell (cellX, cellY) is water. Consulted only when the
        /// region has over_water = 0. May be null, which reads as "nothing is water".</param>
        public static SkyDecorPlan PlanBlock(SkyDecorRegion region, ushort landblockId, Func<float, float, float> groundZ, Func<int, int, bool> isWater)
        {
            var plan = new SkyDecorPlan();

            if (region == null || groundZ == null)
                return plan;

            var own = PlanCandidates(region, landblockId);

            plan.Candidates = own.Count;

            if (own.Count == 0)
                return plan;

            // Overlap first, water second, and that order is deliberate. The cull has to be symmetric
            // across a boundary, and a block only knows its OWN terrain - it cannot tell whether a
            // neighbour's candidate was going to be dropped for water. Culling against the pre-water
            // candidate set is therefore the only version both sides of a boundary can agree on. The cost
            // is that a candidate later dropped for water can still have suppressed a neighbour; that
            // cannot happen at all while over_water is 1, which is how the Marae row runs.
            var survivors = Cull(region, landblockId, own);

            plan.CulledBySeparation = own.Count - survivors.Count;

            var mast = IsMast(region.Rig);

            foreach (var candidate in survivors)
            {
                if (!region.OverWater && isWater != null && isWater(candidate.CellX, candidate.CellY))
                {
                    plan.CulledByWater++;
                    continue;
                }

                plan.Clouds.Add(BuildCloud(region, candidate, groundZ, mast));
            }

            return plan;
        }

        /// <summary>
        /// Convenience wrapper over <see cref="PlanBlock"/>: just the clouds, for the callers and tests
        /// that do not care how many candidates were culled to get them.
        /// </summary>
        public static List<SkyDecorCloud> Plan(SkyDecorRegion region, ushort landblockId, Func<float, float, float> groundZ, Func<int, int, bool> isWater)
        {
            return PlanBlock(region, landblockId, groundZ, isWater).Clouds;
        }

        /// <summary>
        /// Turns a surviving candidate into a placed cloud, consulting the terrain for the first time.
        /// </summary>
        private static SkyDecorCloud BuildCloud(SkyDecorRegion region, SkyDecorCandidate candidate, Func<float, float, float> groundZ, bool mast)
        {
            var ground = groundZ(candidate.X, candidate.Y);

            Orientation(candidate.YawDeg, candidate.TiltDeg, mast, out var aw, out var ax, out var ay, out var az);

            var originZ = mast
                ? OriginZMast(ground, candidate.Height, region.PartOffset, candidate.Scale, candidate.TiltDeg)
                : OriginZ(ground, candidate.Height, region.PartOffset, candidate.Scale, candidate.TiltDeg);

            var n = DiscNormal(candidate.YawDeg, candidate.TiltDeg, mast);

            // Inverted keeps the ROLLED height verbatim rather than reconstructing it from originZ, so
            // that path stays bit-for-bit what it was before the mast rig existed: OriginZ is defined as
            // the exact inverse of this expression, but (ground + h - k) + k is not guaranteed to give
            // back h in float. Mast pins the ORIGIN instead of the height, so there the height genuinely
            // is a derived quantity and gets derived.
            var height = mast
                ? originZ + region.PartOffset * candidate.Scale * n.Z - ground
                : candidate.Height;

            var cloud = new SkyDecorCloud
            {
                WeenieClassId = candidate.WeenieClassId,
                CellX = candidate.CellX,
                CellY = candidate.CellY,
                X = candidate.X,
                Y = candidate.Y,
                GroundZ = ground,
                Scale = candidate.Scale,
                Height = height,
                Spin = candidate.Spin,
                TiltDeg = candidate.TiltDeg,
                YawDeg = candidate.YawDeg,
                OriginZ = originZ,
                AnglesW = aw,
                AnglesX = ax,
                AnglesY = ay,
                AnglesZ = az,
                Translucency = candidate.Translucency,
            };

            if (candidate.Paired)
                cloud.Partner = MakePartner(region, cloud, candidate.PartnerWeenieClassId, groundZ, mast);

            return cloud;
        }

        /// <summary>
        /// Builds the PARTNER disc for a primary: a different colour, smaller, faster, sitting just below
        /// the primary's disc and exactly parallel to it.
        ///
        /// All of the geometry runs along n, the primary's disc normal (see <see cref="DiscNormal"/>).
        /// Sharing n is what keeps the discs parallel, and what makes pair_gap the distance between the
        /// two PLANES rather than a vertical drop that would shear as the pair tilts. n points DOWN under
        /// the inverted rig and UP under mast, and "just below the primary" has to mean below in both:
        ///   inverted:  D2 = D1 + gap * n     further along n, which is downward
        ///   mast:      D2 = D1 - gap * n     back along n, which is downward
        /// With D1 = O1 + partOffset * S1 * n and O2 = D2 - partOffset * S2 * n, both collapse to
        ///   O2 = O1 + (partOffset * (S1 - S2) -/+ gap) * n
        /// Under mast that bracket is positive at any real scale (partOffset * (1 - ratio) * S1 passes a
        /// 0.6 m gap by S1 = 5.3), so the partner's origin sits ABOVE the primary's, which is what keeps
        /// it clear of the ground the mast rig deliberately parks the primary origin 1 m over.
        ///
        /// The partner's Height/GroundZ are re-derived from the terrain under its OWN x/y, so the
        /// "OriginZ reconstructs a disc centre at GroundZ + Height" invariant holds for it too.
        /// </summary>
        private static SkyDecorCloud MakePartner(SkyDecorRegion region, SkyDecorCloud primary, uint partnerWcid, Func<float, float, float> groundZ, bool mast)
        {
            var ratio = region.PairScaleRatio;

            if (ratio <= 0.0f)
                return null;

            var partnerScale = primary.Scale * ratio;

            if (partnerScale <= 0.0f)
                return null;

            var n = DiscNormal(primary.YawDeg, primary.TiltDeg, mast);

            var along = region.PartOffset * (primary.Scale - partnerScale) + (mast ? -region.PairGap : region.PairGap);

            // Clamped only as a backstop for a pathological row (a huge tilt_max with a huge scale could
            // otherwise walk a partner off the edge of its landblock). Inert at any sane tilt: at the 5
            // degree cap the horizontal component of `along` is under a metre, and a primary's own x/y is
            // never closer than 6 m to the edge.
            var px = Math.Clamp(primary.X + along * n.X, 0.5f, BlockLength - 0.5f);
            var py = Math.Clamp(primary.Y + along * n.Y, 0.5f, BlockLength - 0.5f);
            var pz = primary.OriginZ + along * n.Z;

            var partnerGround = groundZ(px, py);

            // The disc centre is partOffset * S2 along n from the origin; Height is that centre's height
            // over the ground beneath it.
            var partnerHeight = pz + region.PartOffset * partnerScale * n.Z - partnerGround;

            return new SkyDecorCloud
            {
                WeenieClassId = partnerWcid,
                CellX = Math.Clamp((int)(px / CellLength), 0, CellSide - 1),
                CellY = Math.Clamp((int)(py / CellLength), 0, CellSide - 1),
                X = px,
                Y = py,
                GroundZ = partnerGround,
                Scale = partnerScale,
                Height = partnerHeight,
                Spin = primary.Spin * region.PairSpeedRatio,
                TiltDeg = primary.TiltDeg,
                YawDeg = primary.YawDeg,
                OriginZ = pz,
                AnglesW = primary.AnglesW,
                AnglesX = primary.AnglesX,
                AnglesY = primary.AnglesY,
                AnglesZ = primary.AnglesZ,

                // Inherited verbatim, not re-rolled: the two discs sit 0.6 m apart and exactly parallel,
                // so two different alphas on the same pair would read as a rendering fault (one disc
                // "wrong") rather than as intentional variety.
                Translucency = primary.Translucency,
            };
        }
    }

    /// <summary>One entry of a region's palette CSV.</summary>
    public readonly struct SkyDecorPaletteEntry
    {
        public readonly uint WeenieClassId;
        public readonly int Weight;

        public SkyDecorPaletteEntry(uint weenieClassId, int weight)
        {
            WeenieClassId = weenieClassId;
            Weight = weight;
        }
    }

    /// <summary>One planned cloud. Landblock-local metres; nothing here is a live object.</summary>
    public class SkyDecorCloud
    {
        public uint WeenieClassId;

        public int CellX;
        public int CellY;

        public float X;
        public float Y;

        /// <summary>terrain height under (X, Y) when the plan was made</summary>
        public float GroundZ;

        /// <summary>where the OBJECT ORIGIN goes; the disc hangs below it under the inverted rig and stands above it under mast</summary>
        public float OriginZ;

        public float Scale;

        /// <summary>disc CENTRE height above the terrain, metres</summary>
        public float Height;

        public float Spin;
        public float TiltDeg;
        public float YawDeg;

        public float AnglesW;
        public float AnglesX;
        public float AnglesY;
        public float AnglesZ;

        /// <summary>PropertyFloat.Translucency to apply, [0, 1]. 0 = opaque, 1 = fully invisible.</summary>
        public float Translucency;

        /// <summary>
        /// The smaller, faster disc of a different colour hanging just below this one, or null when this
        /// cloud is unpaired. A partner is itself an ordinary cloud - same spawn path, same persistence
        /// and decay treatment - and never has a Partner of its own, so the nesting is exactly one deep.
        /// </summary>
        public SkyDecorCloud Partner;

        /// <summary>This cloud and its partner if it has one. The order a landblock spawns them in.</summary>
        public IEnumerable<SkyDecorCloud> WithPartner()
        {
            yield return this;

            if (Partner != null)
                yield return Partner;
        }
    }

    /// <summary>
    /// A cloud after the dice but before the terrain: everything the RNG decides, and nothing that needs
    /// to know what the ground looks like. This is the form the no-overlap cull works in, because a
    /// landblock has to be able to generate its NEIGHBOURS' candidates - whose terrain it cannot sample -
    /// to reach the same verdict about a straddling pair that they will.
    /// </summary>
    public class SkyDecorCandidate
    {
        public ushort LandblockId;

        /// <summary>position in its own block's draw order; with LandblockId this identifies a candidate</summary>
        public int Index;

        /// <summary>lower wins the cull; see SkyDecorLayout.CandidatePriority</summary>
        public uint Priority;

        public int CellX;
        public int CellY;

        /// <summary>landblock-local metres</summary>
        public float X;
        public float Y;

        /// <summary>absolute metres across the whole map, so two blocks measure the same distance</summary>
        public float WorldX;
        public float WorldY;

        /// <summary>primary disc radius in metres, 0.725 x scale</summary>
        public float Radius;

        public float Scale;
        public float Height;
        public float Spin;
        public float TiltDeg;
        public float YawDeg;

        public uint WeenieClassId;

        public bool Paired;

        /// <summary>drawn whether or not <see cref="Paired"/> won, so the stream never moves</summary>
        public uint PartnerWeenieClassId;

        /// <summary>PropertyFloat.Translucency to apply, [0, 1]. 0 = opaque. See SkyDecorLayout.TranslucencyRoll.</summary>
        public float Translucency;
    }

    /// <summary>
    /// One landblock's plan for one region, with the bookkeeping /sky-decor info reports. Candidates is
    /// what the dice produced; Clouds is what is left after the two cull passes, and its count plus the
    /// two culled counts always equals Candidates.
    /// </summary>
    public class SkyDecorPlan
    {
        public List<SkyDecorCloud> Clouds = new List<SkyDecorCloud>();

        public int Candidates;

        public int CulledBySeparation;

        public int CulledByWater;
    }

    /// <summary>
    /// A 32-bit xorshift PRNG, written out so the layout is reproducible on any runtime. System.Random's
    /// seeded sequence is an implementation detail of the framework; this one is an implementation detail
    /// of this repository, which is the difference that matters for content that must look identical on
    /// two machines.
    /// </summary>
    public struct SkyDecorRandom
    {
        private uint state;

        public SkyDecorRandom(uint seed)
        {
            // xorshift32 is stuck at zero, so a zero seed takes the golden-ratio constant instead.
            state = seed == 0 ? 0x9E3779B9u : seed;
        }

        public uint NextUInt()
        {
            unchecked
            {
                var x = state;

                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;

                state = x;

                return x;
            }
        }

        /// <summary>Uniform in [0, 1). 24 significant bits, taken from the high end.</summary>
        public double NextDouble()
        {
            return (NextUInt() >> 8) * (1.0 / 16777216.0);
        }

        /// <summary>Uniform in [0, maxExclusive).</summary>
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 1)
                return 0;

            return (int)(NextUInt() % (uint)maxExclusive);
        }

        /// <summary>Uniform in [min, max). Returns min when the range is empty or inverted.</summary>
        public double Range(double min, double max)
        {
            if (max <= min)
                return min;

            return min + (max - min) * NextDouble();
        }
    }
}
