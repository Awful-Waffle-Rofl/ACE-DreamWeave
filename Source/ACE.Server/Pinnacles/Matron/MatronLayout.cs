using System;
using System.Collections.Generic;
using System.Numerics;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles.Matron
{
    /// <summary>
    /// Pure geometry of one opening's furniture, from the opening's Centre (c) and Outward (o) alone: no branch on
    /// which opening it is (fan-out invariant). Every function returns landblock-frame points whose cell is the
    /// opening's niche cell (things inside the niche) or the hall cell table's (things in the room);
    /// MatronRoomGeometryTests proves each against the dat. Rotation helpers are the hazard framework's own
    /// (<see cref="HazardPanels"/>, a panel's normal is its local +Z) and the yaw convention is MatronData's.
    /// </summary>
    public static class MatronLayout
    {
        /// <summary>The niche's depth from the opening plane to its back wall.</summary>
        public const float NicheDepth = 3.33f;

        /// <summary>
        /// The pit and north doors' scale. At scale 1 the Olthoi door (setup 0x020005F2) is 5.34 m wide and 3.75 m tall
        /// (see <see cref="DoorInset"/>); at 0.8 it is 3.0 m tall, filling the niche's 3.0 m opening, and 4.27 m wide, so its
        /// frame runs about 0.47 m into each side wall of the 3.33 m niche.
        /// </summary>
        public const float DoorScale = 0.8f;

        /// <summary>
        /// How far the door's ORIGIN sits in front of the niche's back wall. MEASURED 2026-10-27, `cells --landblock=0x586B
        /// --modelbounds=0x020005F2` plus a part-by-part dump of the Setup's placement frames (all three placements
        /// identical): at scale 1 the mesh spans x -2.67..2.67, y -1.47..0.94, z 0..3.75 about its origin. Local +Y is the
        /// front (the frame pillars and lintel are y +-0.94, the door leaves recede to y -1.47 behind them), so with the
        /// door facing into the room the leaves reach 1.47 m BEHIND the origin at scale 1, 1.47 x <see cref="DoorScale"/>
        /// at the spawned scale: an inset under that pushes them through the niche's back wall, and this puts the rearmost
        /// leaf exactly on the wall.
        /// </summary>
        public const float DoorInset = 1.47f * DoorScale;

        /// <summary>The door model's height at scale 1 (z 0..3.75 about its origin, see <see cref="DoorInset"/>).</summary>
        public const float DoorHeight = 3.75f;

        // ---- the drain: three layers at the room centre, "an olthoi door over a grate panel, over a black panel" ----
        //
        // Playtest 6 (owner): the bars were too coarse (about 3 across) and the iris too small, and the drain could not be selected.
        // The grate is now a 2 x 2 grid of half-scale tiles, the iris is scaled so its closed disc covers the grate, and the black
        // panel moved from the 10 m panel setup 0x02000E6E to the grate's own small ClipMap quad 0x02000E15 so that no UiHidden
        // layer's selection sphere encloses the iris's (see DrainIris and MatronLayoutTests.Drain_EveryHiddenLayersSelectionSphere_
        // LiesInsideTheIrisSelectionSphere for the hypothesis this rests on).

        /// <summary>The grate quad's native edge: setup 0x02000E15 (Base1ClipMap), +-4.03769 about its origin. [dat probe 2026-10-28]</summary>
        public const float GrateNativeSize = 8.07538f;

        /// <summary>The whole grate's scale, as one quad would be: 2.3014 m. 0.285. The tiles are this over <see cref="GrateTiles"/>.</summary>
        public const float GrateScale = 0.285f;

        /// <summary>The grate's edge in the room, the drain's footprint: <see cref="GrateNativeSize"/> x <see cref="GrateScale"/>, 2.3014 m.</summary>
        public const float GrateSize = GrateNativeSize * GrateScale;

        /// <summary>The drain's edge in the room: the black panel and the grate share this footprint exactly; the iris covers it.</summary>
        public const float DrainSize = GrateSize;

        /// <summary>
        /// The grate is this many tiles a side. The quad's UVs run 0.83 to 2.17 on both axes (1.34 texture repeats a quad), and the
        /// bars texture 0x05002795 (64 x 64 DXT5) is a plain lattice: an 8-pixel bar every 32 pixels both ways, no border or frame
        /// [dat probe 2026-10-28: `cells --texture-png=0x05002795`, alpha map]. So the density is set by the quad's size alone, and two
        /// tiles a side halve the bar spacing (about 2.7 bars across one tile, 5.4 across the drain). The tiles are NOT seamless:
        /// 1.34 is not a whole number of 0.5-UV lattice periods, so where two tiles meet the lattice phase jumps from 0.17 to 0.33 and
        /// one gap on each centre line is about 57% of a normal gap's width (0.215 UV against 0.375).
        /// </summary>
        public const int GrateTiles = 2;

        /// <summary>Each tile's scale: <see cref="GrateScale"/> / <see cref="GrateTiles"/>, 0.1425.</summary>
        public const float GrateTileScale = GrateScale / GrateTiles;

        /// <summary>Each tile's edge: 1.1507 m.</summary>
        public const float GrateTileSize = GrateNativeSize * GrateTileScale;

        /// <summary>
        /// The black panel's scale: the grate's own quad (0x02000E15) at the grate's footprint, so the two layers share their edges.
        /// The panel was the 10 m panel 0x02000E6E at 0.23 until playtest 6; that setup's selection sphere (r 9.495 about z 0.738)
        /// is 2.18 m at 0.23 and enclosed the whole iris.
        /// </summary>
        public const float DrainScale = GrateScale;

        /// <summary>
        /// The black panel's origin stands this far over the floor plane. Its setup's only sphere (r 0.1 at the origin, 0.0285 at
        /// <see cref="DrainScale"/>) then stays above the floor, and SpawnAtExactPosition (bool 9069) keeps it exactly here.
        /// </summary>
        public const float DrainLift = 0.03f;

        /// <summary>The black panel's origin z: -59.97. Its quad lies 0.0104 x scale BELOW the origin, so the black face is at -59.973.</summary>
        public const float DrainZ = RoomFloorZ + DrainLift;

        /// <summary>The grate tiles' origins stand this far over the floor plane: their faces (-59.9465) are 2.65 cm over the black face.</summary>
        public const float GrateLift = 0.055f;

        /// <summary>The grate tiles' origin z (their quads lie 1 cm x scale BELOW the origin): -59.945.</summary>
        public const float GrateZ = RoomFloorZ + GrateLift;

        /// <summary>
        /// The 0x02000E15 quad's SelectionSphere at scale 1: radius 0.51 about its origin [DatLoader probe 2026-10-28, SetupModel.SelectionSphere;
        /// pinned against the dat by MatronRoomGeometryTests]. Both the black panel and the grate tiles carry it.
        /// </summary>
        public const float QuadSelectionRadius = 0.51f;

        /// <summary>
        /// The Olthoi door's (0x020005F2) SelectionSphere at scale 1: radius 1.728 about local (0, 0, 1.64), the middle of the upright
        /// door [same probe]. Pitched face up it lies 1.64 x scale SOUTH of the origin, on the floor plane of the origin.
        /// </summary>
        public const float DoorSelectionRadius = 1.728f;

        /// <summary>The door's SelectionSphere centre height in the door's own frame at scale 1 (see <see cref="DoorSelectionRadius"/>).</summary>
        public const float DoorSelectionCentreZ = 1.64f;

        /// <summary>
        /// The iris is the Olthoi door (setup 0x020005F2) at this scale, its frame parts swapped for a null part. Sized so the CLOSED
        /// disc covers the grate's footprint: its unbroken covered radius (<see cref="IrisCoveredRadius"/> x scale) must reach 1.05 x
        /// half the grate's edge, 1.2082 m, which takes a scale of at least 0.7385; at 0.75 the covered disc is 2.454 m across, 1.066 x
        /// the 2.3014 m edge. A disc that covers the edge midpoints leaves the square's corners showing (the corners stand 1.627 m out).
        /// </summary>
        public const float IrisScale = 0.75f;

        /// <summary>
        /// The closed disc's unbroken covered radius at scale 1, about its centre: the shortest run, over 360 bearings, from the disc's
        /// centre to the first point no petal covers. MEASURED 2026-10-28, DatLoader probe (scratch `r6probe cover`): every petal polygon
        /// of parts 3 to 12 in frame 0 of anim 0x03000769 (the CLOSED pose; placement 0 is a half-open star), pitched +90 degrees about x
        /// and projected on the floor plane. The run is 1.641 at bearing 255 degrees with a 5 mm tolerance for the hairline cracks where
        /// two petals meet (without it the march stops in the first crack); 1.636 takes that tolerance back off.
        /// </summary>
        public const float IrisCoveredRadius = 1.636f;

        /// <summary>
        /// The closed disc's centre stands this far SOUTH of the iris's origin at scale 1 (the same probe, true vertex extents: y -3.3636
        /// to +0.0028, centre -1.6804; x -1.9109 to 1.9066, centre -0.0022, ignored). It scales with the iris.
        /// </summary>
        public const float IrisDiscCentreY = 1.6804f;

        /// <summary>The closed disc reaches this far BELOW the origin at scale 1 (z -0.2678; it reaches 0.2631 above). [same probe]</summary>
        public const float IrisDiscBelow = 0.2678f;

        /// <summary>The closed disc reaches this far ABOVE the origin at scale 1. [same probe]</summary>
        public const float IrisDiscAbove = 0.2631f;

        /// <summary>
        /// How far the iris's ORIGIN stands north of the drain point, so the closed disc is centred on it: <see cref="IrisDiscCentreY"/>
        /// x <see cref="IrisScale"/>, 1.2603. (At the old scale 0.5 this was the 0.84 of playtest 5.)
        /// </summary>
        public const float IrisOffsetY = IrisDiscCentreY * IrisScale;

        /// <summary>The closed disc reaches this far below the iris's origin at <see cref="IrisScale"/>: 0.2009.</summary>
        public const float IrisBelowOrigin = IrisDiscBelow * IrisScale;

        /// <summary>The closed disc's total height at <see cref="IrisScale"/>: 0.3982.</summary>
        public const float IrisHeight = (IrisDiscBelow + IrisDiscAbove) * IrisScale;

        /// <summary>
        /// The iris's origin z: its lowest point (<see cref="IrisBelowOrigin"/> under the origin) on the grate tiles' origin plane, so
        /// the disc rests on the bars and nothing goes below them. -59.7441.
        /// </summary>
        public const float IrisZ = GrateZ + IrisBelowOrigin;

        /// <summary>The door's origin is at its sill: the niche floor.</summary>
        public const float DoorZ = -48f;

        /// <summary>The acid back panel stands this far out from the opening, just inside the niche's back wall.</summary>
        public const float BackPanelOffset = 3.20f;

        public const float BackPanelZ = -46.5f;

        /// <summary>A 10 m panel scaled to a 3.333 m edge (the opening is 3.33 m wide).</summary>
        public const float PanelScale = 0.3333f;

        // ---- the wall strip under each opening: scrolling flow panels, a fog cloud at the foot, or both (WallFx) ----

        /// <summary><see cref="WallStrip"/> mode: the flow panels only.</summary>
        public const int WallFxPanels = 0;

        /// <summary><see cref="WallStrip"/> mode: the fog cloud only.</summary>
        public const int WallFxFog = 1;

        /// <summary><see cref="WallStrip"/> mode: the flow panels and the fog cloud.</summary>
        public const int WallFxBoth = 2;

        /// <summary>The shipped mode (the per-copy override key wall_fx, <see cref="MatronSettings.WallFx"/>, picks another).</summary>
        public const int WallFxDefault = WallFxBoth;

        /// <summary>
        /// Where the drawn face of a wall panel stands off the wall plane: where the old panel curtain's face stood (its origin at
        /// HazardPanels.FaceClearance, its 0x02000E6E quad 0.01 m up the normal at scale 1).
        /// </summary>
        public const float WallPanelFaceOffset = HazardPanels.FaceClearance + 0.01f * PanelScale;

        /// <summary>The flow setups' quad lies this far up the normal from the origin at scale 1 (local z 0.5, dat).</summary>
        public const float FlowQuadLift = 0.5f;

        /// <summary>
        /// The flow panels' ORIGIN stands this far into the room from the wall plane, so the drawn face lands at
        /// <see cref="WallPanelFaceOffset"/>: 0.2133 - 0.5 x 0.3333 = 0.0467 m. The flow setups (0x02000DA8 lava, 0x02000DA9 acid)
        /// have no collision sphere, so the engine places them by its 0.1 m stand-in ball 0.1 m up the normal: facing the room,
        /// the ball's back is 0.0467 m clear of the wall. MatronRoomGeometryTests proves every strip panel enters exactly here.
        /// </summary>
        public const float WallPanelOffset = WallPanelFaceOffset - FlowQuadLift * PanelScale;

        /// <summary>
        /// The flow panel rows' centres, sill to floor: four 3.333 m rows (the bottom row's last 1.33 m is under the floor). One
        /// room-facing panel per row: the flow quads are single-sided (dat: cull mode 0, no negative surface) and a wall strip
        /// is only seen from the room.
        /// </summary>
        public static readonly IReadOnlyList<float> WallPanelRowZ = new[] { -49.6667f, -53.0f, -56.3333f, -59.6667f };

        /// <summary>
        /// The flow texture's spin about the panel normal, degrees. At 0 the panel's local +Y points down the wall in every
        /// opening (<see cref="WallPanelFacing"/>). The setup scripts scroll U and V at the same rate (0x33000D3C lava 0.03,
        /// 0x33000D3D acid 0.05), and on the quad U grows with local +X and V with local -Y, so the scroll runs along a diagonal
        /// of the panel: at any multiple of 90 it is diagonal on the wall, and only a 45-degree spin makes it vertical (the
        /// square rows then turn into diamonds). Which way along the diagonal the texture moves is unverified: owner's eye.
        /// The flow weenies also replay a faster script (d_i_d 9000) on top of those defaults: both now 0x33000D34 (0.10, playtest 6
        /// "double it again"; lava was 0x33000D3D, 0.05). 0.10 is the fastest TextureVelocity hook in the dat: of the 11 scripts that
        /// carry one, the rest are 0.03 or 0.05 [scratch `r6probe scanvel` over every 0x33 script, 2026-10-28], so acid could not be
        /// doubled again by a script swap. Whether the client replaces or adds the texture velocity is unverified (the owner saw the
        /// round-5 speed-up): about 3.3x lava and 2x acid over the setup defaults if it replaces, 4.3x and 3x if it adds.
        /// </summary>
        public const float WallPanelSpinDeg = 0f;

        /// <summary>The fog cloud's host stands this far into the room from the wall plane, on the opening's centre line...</summary>
        public const float WallFogOffset = 1.0f;

        /// <summary>...at the floor (the effect host enters exactly here, MatronRoomGeometryTests).</summary>
        public const float WallFogZ = -59.995f;

        /// <summary>
        /// The shipped fog scripts (playtest 4: the fireworks-cloud variants 0x33000D35/0x33000D37 swirled, both Swarm emitters).
        /// Acid: 0x33000C0E, the one slow FALLING green fog in the dat (emitter 0x3200067D: LocalVelocity, falls 0.2 to 0.5 m/s,
        /// 4 s puffs, spawns up to 5 m off the host in a sheet across its local x and z). Lava (playtest 5: the dark red
        /// 0x330010F3 was too small and too red): 0x330007A6, the orange twin of the green one (emitter 0x3200043B, the same
        /// shape but rising 0.25 to 0.75 m/s). Particles ignore host scale. Dat sweep 2026-10-28.
        /// </summary>
        public const uint WallFogAcidScript = 0x33000C0E;

        public const uint WallFogLavaScript = 0x330007A6;

        /// <summary>
        /// The acid fog choices, by the per-copy override fog_acid (0 the shipped one): each with one host per lift (metres above
        /// the floor). Playtest 5: the falling sheet runs from the exit to the floor, denser, so two hosts stack up the wall's
        /// foot (each sheet reaches 5 m round its host: together the floor to about 1.5 m over the 12 m sill). The others sit on
        /// the floor: 1 = 0x33000A44, wide hanging green puffs drifting sideways; 2 = 0x330012DD, a narrow slow green column.
        /// </summary>
        public static readonly IReadOnlyList<(uint Script, float[] Lifts)> AcidFogs = new[] { (WallFogAcidScript, new[] { 4.5f, 8.5f }), (0x33000A44u, new[] { 0f }), (0x330012DDu, new[] { 0f }) };

        /// <summary>
        /// The lava fog choices, by fog_lava (0 the shipped one): 0 the orange haze on two hosts up the wall's foot; 1 = 0x330010F3,
        /// the round 4 dark red cloud barely rising; 2 = 0x3300131A, that cloud plus 8 m puffs and a fast column; 3 = 0x33000770,
        /// the largest and most static red cloud, very dim (may be near invisible). The alternates sit on the floor.
        /// </summary>
        public static readonly IReadOnlyList<(uint Script, float[] Lifts)> LavaFogs = new[] { (WallFogLavaScript, new[] { 1f, 5f }), (0x330010F3u, new[] { 0f }), (0x3300131Au, new[] { 0f }), (0x33000770u, new[] { 0f }) };

        /// <summary>The jump platforms per opening: four pads, five hops from the floor to the sill.</summary>
        public const int PlatformCount = 4;

        /// <summary>The platform slab (setup 0x02000DB3) is 2 x 2 m; this is half of it.</summary>
        public const float PlatformHalfSize = 1.0f;

        public const float PlatformThickness = 0.4f;

        /// <summary>The top pad's top stands this far under the sill: the last hop, onto the sill.</summary>
        public const float PlatformTopUnderSill = 1.3333f;

        /// <summary>The sill the platforms climb to, and the room floor they climb from.</summary>
        public const float SillZ = -48f;

        public const float RoomFloorZ = -60f;

        /// <summary>
        /// Each platform's top is this far above the one before, the lowest this far above the floor: the floor-to-top-pad climb
        /// split into <see cref="PlatformCount"/> even rises, (-49.3333 - -60) / 4 = 2.6667.
        /// </summary>
        public const float PlatformRise = (SillZ - PlatformTopUnderSill - RoomFloorZ) / PlatformCount;

        /// <summary>Horizontal edge-to-edge gap between consecutive platforms (they alternate sides of the opening's centreline).</summary>
        public const float PlatformGap = 2.0f;

        /// <summary>
        /// A platform's wall-side edge stands this far in front of the wall plane that holds the opening: flush. MEASURED
        /// 2026-10-27 against the dat: the wall face of every opening lies exactly on its centre plane at every pad height and at
        /// laterals out to 3 m, and every gap from 0 to 0.05 places all 20 pads exactly at their points. Placement does not refuse
        /// a slab that pokes into the wall (a gap of -0.01 also placed exactly), so the floor of 0 is set by the geometry: never
        /// let this go negative.
        /// </summary>
        public const float PlatformWallGap = 0f;

        /// <summary>The sill is 12 m above the floor.</summary>
        public const float SillHeight = 12f;

        /// <summary>
        /// The niche's acid floor is a 10 m panel at <see cref="PanelScale"/> (3.333 m, the niche's width and depth), this far over the
        /// sill. MEASURED 2026-10-27 (MatronRoomGeometryTests.NicheAcidFloor_EnterExactlyInEveryNiche): at 0.02 m the face-up panel
        /// comes to rest ON the sill (z -48.000, coplanar with it); at 0.05 m it rests exactly where put in all five niches...
        /// </summary>
        public const float NicheFloorLift = 0.05f;

        /// <summary>...centred this far out from the opening plane (half the niche's depth).</summary>
        public const float NicheFloorOffset = 1.67f;

        /// <summary>The panels in the niche floor's doubled pair (both face up, at the same point).</summary>
        public const int NicheFloorLayers = 2;

        /// <summary>
        /// The finale rock's origin stands this far out from the opening plane, on the sill. The rock (setup 0x02000597 at the
        /// weenie's DefaultScale 4) spans local y -1.56..1.48 about its origin; facing into the room it then runs from 0.12 m
        /// to 3.16 m into the 3.33 m niche, and its 3.24 m width fits the niche's 3.33 m.
        /// </summary>
        public const float RockOffset = 1.6f;

        /// <summary>The door: in the niche, on the sill, facing into the room.</summary>
        public static PinnaclePoint Door(MatronOpening o)
        {
            var at = o.Centre + o.Outward * (NicheDepth - DoorInset);

            return MatronData.Point(o.NicheCell, at.X, at.Y, DoorZ, MatronData.YawDegFacing(-o.Outward));
        }

        /// <summary>The opening's door prop: closed, at <see cref="DoorScale"/>.</summary>
        public static PinnacleProp DoorProp(MatronOpening o) => new PinnacleProp(MatronData.DoorWcid, Door(o), DoorScale, DoorOpen: false);

        /// <summary>
        /// The drain's black base layer (1007553): the grate's ClipMap quad wearing an opaque black texture, centred on the drain point,
        /// face up (the quad's normal is its local +Z, so no rotation), at the grate's footprint. Not usable.
        /// </summary>
        public static PinnacleProp DrainFloor()
        {
            var c = MatronData.DrainPoint;

            return new PinnacleProp(MatronData.DrainFloorWcid, new PinnaclePoint(c.Cell, c.X, c.Y, DrainZ), DrainScale);
        }

        /// <summary>
        /// The drain beacon choices, by the per-copy override beacon_fx (0 the shipped one): a tall beam that rises from the drain
        /// while the acid is deep and the drain unused. The fork's standard beam family (Setups 0x020006B3-0x020006BC, effect
        /// catalog Content/effects/effect_donor_catalog_v2.tsv; there is no yellow in it): 0 = 0x3300079A, the narrow blue beam (the
        /// puzzle gate "blue", PuzzleGateColours; the World Event frost marker, WorldEventHazardElements); 1 = 0x33000799, the narrow
        /// grey/white beam (the World Event earth marker, Setup 0x020006BB); 2 = 0x33000795, the wide, scattered grey beam (Setup
        /// 0x020006BC). These beams hang BELOW their host origin, so <see cref="DrainBeacon"/> turns the host (MemoryLayout.FloorOrientation).
        /// </summary>
        public static readonly IReadOnlyList<uint> DrainBeacons = new[] { 0x3300079Au, 0x33000799u, 0x33000795u };

        /// <summary>
        /// The beacon host stands this far over <see cref="DrainZ"/>. The Effect Host (wcid 1006952, Setup 0x02000176) has its collision
        /// sphere centre 0.136 m ABOVE its origin; turned 180 degrees (so a hanging beam rises) that centre falls 0.136 m BELOW the
        /// origin, and at <see cref="DrainZ"/> it would sit under the floor, so LivePinnacleWorld.SpawnProp's EnterWorld is refused and
        /// no beam appears. The hazard path lifts every turned grounded host by HazardGround.TurnedHostLift (HazardGround.cs:221-267,
        /// applied at HazardContext.cs:466-495); a pinned prop cannot reach that, so the lift is fixed here: 0.28 is over twice the
        /// 0.136 (the dat-free pin and MatronRoomGeometryTests measure it).
        /// </summary>
        public const float BeaconLift = 0.28f;

        /// <summary>
        /// The drain beacon (owner direction 2026-10-29: show a beam over the drain, while the acid is over head height and the drain
        /// unused, as the cue to the tool that helps): an invisible effect host at the drain's centre, at <see cref="DrainZ"/> plus
        /// <see cref="BeaconLift"/> (the turned host's collision sphere must clear the floor), carrying <paramref name="script"/> and
        /// turned by MemoryLayout.FloorOrientation so a hanging beam RISES from the floor.
        /// </summary>
        public static PinnacleProp DrainBeacon(uint script)
        {
            var c = MatronData.DrainPoint;

            return new PinnacleProp(MatronData.EffectHostWcid, MatronData.Point(c.Cell, c.X, c.Y, DrainZ + BeaconLift, MemoryLayout.FloorOrientation(script)), Script: script);
        }

        /// <summary>
        /// The drain's grate (1007552): <see cref="GrateTiles"/> x <see cref="GrateTiles"/> ClipMap quads at <see cref="GrateTileScale"/>,
        /// face up over the black panel, edge to edge so together they cover the same <see cref="GrateSize"/> square centred on the
        /// drain point. Row by row from the south-west tile. Plain static props, unselectable.
        /// </summary>
        public static IReadOnlyList<PinnacleProp> DrainGrate()
        {
            var c = MatronData.DrainPoint;
            var tiles = new List<PinnacleProp>(GrateTiles * GrateTiles);
            var first = -GrateSize / 2f + GrateTileSize / 2f;

            for (var row = 0; row < GrateTiles; row++)
            {
                for (var col = 0; col < GrateTiles; col++)
                {
                    var x = c.X + first + col * GrateTileSize;
                    var y = c.Y + first + row * GrateTileSize;
                    tiles.Add(new PinnacleProp(MatronData.GrateWcid, new PinnaclePoint(c.Cell, x, y, GrateZ), GrateTileScale));
                }
            }

            return tiles;
        }

        /// <summary>
        /// The drain's iris (1007557), the use target: the Olthoi door's petals at <see cref="IrisScale"/>, pitched +90 degrees about x so
        /// the door's front points up (face up), its origin <see cref="IrisOffsetY"/> north of the drain point so the closed disc is
        /// centred on it, <see cref="IrisZ"/> high so the disc rests on the grate. Closed while unspent, open once spent.
        /// SELECTION (playtest 6, "not selectable"): pitched face up, the door's selection sphere (<see cref="DoorSelectionRadius"/> at
        /// local z <see cref="DoorSelectionCentreZ"/>) lands 1.23 m south of the origin, 0.03 m from the disc's centre, radius 1.296:
        /// on the petals, not under the floor. What did enclose it was the black panel's 2.18 m sphere. HYPOTHESIS, unverified in the
        /// client: the client picks the object whose selection sphere the cursor ray enters first, and a UiHidden hit selects nothing.
        /// Every hidden drain layer's sphere now lies inside the iris's, so the ray meets the iris's first.
        /// </summary>
        public static PinnacleProp DrainIris(bool spent)
        {
            var c = MatronData.DrainPoint;
            var pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);

            return new PinnacleProp(MatronData.DrainIrisWcid, MatronData.Point(c.Cell, c.X, c.Y + IrisOffsetY, IrisZ, pitch), IrisScale, DoorOpen: spent);
        }

        /// <summary>The acid (or lava) panel behind the door, facing into the room.</summary>
        public static PinnacleProp BackPanel(MatronOpening o) => BackPanel(o, MatronData.AcidPanelWcid);

        public static PinnacleProp BackPanel(MatronOpening o, uint panelWcid)
        {
            var at = o.Centre + o.Outward * BackPanelOffset;

            return new PinnacleProp(panelWcid, MatronData.Point(o.NicheCell, at.X, at.Y, BackPanelZ, FaceAlong(-o.Outward)), PanelScale);
        }

        /// <summary>
        /// The wall strip under an opening (owner direction 2026-10-27, replacing the glow stack): with
        /// <paramref name="mode"/> <see cref="WallFxPanels"/> or <see cref="WallFxBoth"/>, the flow panels, row by row from
        /// the sill down (<see cref="WallPanelRowZ"/>), one room-facing panel per row at <see cref="PanelScale"/>, origin
        /// <see cref="WallPanelOffset"/> in front of the wall; then with <see cref="WallFxFog"/> or <see cref="WallFxBoth"/>, the fog cloud's host. The element picks the
        /// panel wcid and the fog script only. One code path for every opening.
        /// </summary>
        public static IReadOnlyList<PinnacleProp> WallStrip(MatronOpening o, bool lava, int mode, int fogPick = 0)
        {
            if (mode < WallFxPanels || mode > WallFxBoth)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "wall_fx is 0 (panels), 1 (fog) or 2 (both)");

            var fogs = lava ? LavaFogs : AcidFogs;

            if (fogPick < 0 || fogPick >= fogs.Count)
                throw new ArgumentOutOfRangeException(nameof(fogPick), fogPick, $"the fog pick is 0 to {fogs.Count - 1}");

            var props = new List<PinnacleProp>();

            if (mode != WallFxFog)
            {
                var wcid = lava ? MatronData.LavaFlowWcid : MatronData.AcidFlowWcid;
                var plane = o.Centre - o.Outward * WallPanelOffset;

                foreach (var z in WallPanelRowZ)
                    props.Add(new PinnacleProp(wcid, MatronData.Point(MatronData.HallCell(plane.X, plane.Y, z), plane.X, plane.Y, z, WallPanelFacing(-o.Outward)), PanelScale));
            }

            if (mode != WallFxPanels)
            {
                var at = o.Centre - o.Outward * WallFogOffset;
                var (script, lifts) = fogs[fogPick];

                foreach (var lift in lifts)
                {
                    var z = WallFogZ + lift;

                    // local +Y into the room: a sheet-spawning script then spreads across the wall, not out from it
                    props.Add(new PinnacleProp(MatronData.EffectHostWcid, MatronData.Point(MatronData.HallCell(at.X, at.Y, z), at.X, at.Y, z, MatronData.YawDegFacing(-o.Outward)),
                        Script: script));
                }
            }

            return props;
        }

        /// <summary>
        /// An upright flow panel's rotation, its normal (local +Z) turned to <paramref name="normal"/> and its local +Y pointing
        /// straight down, whichever way it faces, after the <see cref="WallPanelSpinDeg"/> spin about the normal (180 flips the
        /// scroll if the client's sign is the other way). Stand-up is -90 degrees about local x (w 0.7071, x -0.7071), then the
        /// yaw. So the texture runs the same way down every opening's wall.
        /// </summary>
        public static Quaternion WallPanelFacing(Vector2 normal)
        {
            var spin = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, WallPanelSpinDeg * MathF.PI / 180f);
            var stand = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f);         // normal +Z to +Y, local +Y to -Z
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(-normal.X, normal.Y));   // +Y to the normal

            return Quaternion.Concatenate(Quaternion.Concatenate(spin, stand), yaw);
        }
        /// <summary>
        /// The acid on the niche floor while the pit flows: a doubled pair of face-up acid panels (a panel's normal is its local
        /// +Z, so no rotation is face up) just over the sill, centred in the niche.
        /// </summary>
        public static IReadOnlyList<PinnacleProp> NicheFloor(MatronOpening o) => NicheFloor(o, MatronData.AcidPanelWcid, null);

        /// <summary>The same niche floor in another panel and translucency (the finale's north lava floor).</summary>
        public static IReadOnlyList<PinnacleProp> NicheFloor(MatronOpening o, uint panelWcid, float? translucency)
        {
            var at = o.Centre + o.Outward * NicheFloorOffset;
            var props = new PinnacleProp[NicheFloorLayers];

            for (var i = 0; i < props.Length; i++)
                props[i] = new PinnacleProp(panelWcid, new PinnaclePoint(o.NicheCell, at.X, at.Y, SillZ + NicheFloorLift), PanelScale, translucency);

            return props;
        }

        /// <summary>The finale rock: on the sill in the niche, at the opening, facing into the room (spawned as an add at scale 1).</summary>
        public static PinnaclePoint Rock(MatronOpening o)
        {
            var at = o.Centre + o.Outward * RockOffset;

            return MatronData.Point(o.NicheCell, at.X, at.Y, SillZ, MatronData.YawDegFacing(-o.Outward));
        }

        /// <summary>
        /// The jump platforms, k = 0 (lowest) .. PlatformCount - 1 (one hop under the sill): a switchback up the wall that holds
        /// the opening, every pad flush against the wall (wall-side edge <see cref="PlatformWallGap"/> off it). Every pad alternates
        /// sides of the opening's centreline at half a slab plus half the gap from it, so consecutive platforms are
        /// <see cref="PlatformGap"/> apart edge to edge across the centreline. The last one sits on the side that is
        /// <see cref="PlatformSide"/>(PlatformCount - 1). Top: <see cref="PlatformTop"/>; origin z = top - thickness (the slab's
        /// origin is its underside).
        /// </summary>
        public static IReadOnlyList<PinnaclePoint> Platforms(MatronOpening o)
        {
            var points = new List<PinnaclePoint>(PlatformCount);
            var along = Along(o);

            for (var k = 0; k < PlatformCount; k++)
            {
                var at = o.Centre - o.Outward * (PlatformWallGap + PlatformHalfSize) + along * (PlatformSide(k) * PlatformLateral);
                var z = PlatformTop(k) - PlatformThickness;

                points.Add(MatronData.Point(MatronData.HallCell(at.X, at.Y, z), at.X, at.Y, z, 0f));
            }

            return points;
        }

        /// <summary>A platform centre's distance from the opening's centreline, along the wall.</summary>
        public const float PlatformLateral = PlatformHalfSize + PlatformGap / 2f;

        /// <summary>+1 or -1: which side of the centreline (along <see cref="Along"/>) platform <paramref name="k"/> stands on. The last platform is on +1.</summary>
        public static int PlatformSide(int k) => (PlatformCount - 1 - k) % 2 == 0 ? 1 : -1;

        /// <summary>
        /// The top surface z of platform <paramref name="k"/> (0-based, 0 lowest): k + 1 rises above the floor, so the last is
        /// <see cref="PlatformTopUnderSill"/> under the sill.
        /// </summary>
        public static float PlatformTop(int k) => RoomFloorZ + PlatformRise * (k + 1);

        /// <summary>The unit vector along the wall that holds the opening: the outward direction turned a quarter turn counter-clockwise.</summary>
        public static Vector2 Along(MatronOpening o) => new Vector2(-o.Outward.Y, o.Outward.X);

        /// <summary>The rotation that turns an upright panel's normal to <paramref name="direction"/>, a unit axis vector (the opening's outward or its reverse).</summary>
        private static Quaternion FaceAlong(Vector2 direction)
            => Math.Abs(direction.X) > Math.Abs(direction.Y)
                ? HazardPanels.FaceAlongX(direction.X > 0f)
                : HazardPanels.FaceAlongY(direction.Y > 0f);
    }
}
