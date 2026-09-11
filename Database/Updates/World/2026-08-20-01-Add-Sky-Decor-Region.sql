/* WaffleACE fork: sky decor regions.
 *
 * A row here describes a RECTANGLE OF LANDBLOCKS whose outdoor sky is dressed with spinning Sky Rift
 * discs, generated in code by ACE.Server.Entity.Landblock.SpawnSkyDecor when a landblock activates.
 * Nothing is persisted: the objects live only in the landblock's in-memory list, exactly like the
 * semi-randomized encounter spawns next to them, and neither the shard nor any world table ever sees
 * them. Volume, size, height, colour and spin are iterated by editing a row and running
 * `/sky-decor reload` - no landblock_instance rows, no restart, no new weenies.
 *
 * The clouds are placed once, at landblock activation, and never move: the objects keep the Sky Rift
 * weenies' own Static rig (PhysicsState 0x815) and nothing about sky decor runs on any tick.
 *
 * Discs never overlap: see `min_separation`. The cull runs across landblock boundaries, so a block's
 * layout depends on its neighbours' candidates as well as its own - still purely a function of the
 * region row and the landblock grid, with no load order or shared state involved.
 *
 * Every cloud is a PAIR by default: a primary disc plus a smaller, faster PARTNER disc of a different
 * colour hanging just below it and parallel to it. `density` therefore counts PAIRS, and a block holds
 * up to 2 x density objects. The partner follows the World Events layered-stack rule - a smaller layer
 * turns in a shorter period - so the two discs never look like one stiff object. Set pair_chance = 0
 * for single discs.
 *
 * The layout is a pure function of (seed, version, realm_id, landblock): the spawner mixes those four
 * integers with an explicit FNV-1a step (ACE.Server.Entity.SkyDecorLayout.StableHash) and drives its
 * own xorshift PRNG from the result, so it is stable across processes, machines and .NET versions.
 * Bump `version` to reshuffle a region without changing `seed`.
 *
 * Idempotent: safe to re-run. Only ever adds the table.
 */

CREATE TABLE IF NOT EXISTS `sky_decor_region` (
  `id`            INT UNSIGNED     NOT NULL AUTO_INCREMENT,
  `name`          VARCHAR(64)      NOT NULL,
  `enabled`       BIT(1)           NOT NULL DEFAULT b'1',

  /* 0 = the retail world. Only landblocks loaded in an instance of this realm get the decor. */
  `realm_id`      SMALLINT UNSIGNED NOT NULL DEFAULT 0,

  /* Inclusive landblock-grid rectangle. A landblock id is (x << 8) | y. */
  `lb_x_min`      TINYINT UNSIGNED NOT NULL,
  `lb_x_max`      TINYINT UNSIGNED NOT NULL,
  `lb_y_min`      TINYINT UNSIGNED NOT NULL,
  `lb_y_max`      TINYINT UNSIGNED NOT NULL,

  `seed`          INT              NOT NULL DEFAULT 0,
  `version`       INT              NOT NULL DEFAULT 1,

  /* Clouds per landblock. The fractional part is the probability of one extra: 2.5 = two clouds
   * always, plus a third in half the blocks. */
  `density`       FLOAT            NOT NULL DEFAULT 0,

  /* CSV of `colour` or `wcid`, each optionally followed by `:weight`, e.g. 'purple,red:2,orange'
   * or the equivalent '1002642,1002647:2,1002646'. Missing weight = 1; a zero weight is parsed but
   * never picked, which is how a colour is switched off without editing it out of the list.
   *
   * The eight Sky Rift colour NAMES, matched case-insensitively and freely mixable with raw wcids:
   *   purple 1002642   blue   1002643   yellow 1002644   green 1002645
   *   orange 1002646   red    1002647   white  1002648   grey  1002649  (also accepts 'gray')
   * `white` still parses and still spawns, but it is RETIRED from lineups - its sparse quad renders
   * pixelated at sky scale - so both the loader and /sky-decor set warn when a palette names it.
   * The names live in exactly one place in code, Source/ACE.Server/Entity/SkyDecorColours.cs, and
   * `/sky-decor colours` prints this table in game so nobody has to look a wcid up.
   *
   * An unrecognised token is SKIPPED here (with one log warning per distinct palette, not per
   * landblock) rather than refused, because refusing would leave the region with no palette at all
   * and therefore no clouds. /sky-decor set is the strict path and rejects the whole edit. */
  `palette`       VARCHAR(255)     NOT NULL,

  `scale_min`     FLOAT            NOT NULL DEFAULT 25,
  `scale_max`     FLOAT            NOT NULL DEFAULT 25,

  /* DISC CENTRE height above the terrain under it, in metres - not the object origin. */
  `height_min`    FLOAT            NOT NULL DEFAULT 50,
  `height_max`    FLOAT            NOT NULL DEFAULT 50,

  /* 0 = perfectly flat (disc parallel to the ground). Each cloud tilts by a uniform draw in
   * [0, tilt_max_deg] about a random horizontal axis. */
  `tilt_max_deg`  FLOAT            NOT NULL DEFAULT 0,

  /* PropertyFloat 9007 MotionSpeed multiplier: 1.0 = one turn per 4.0 s. */
  `spin_min`      FLOAT            NOT NULL DEFAULT 1,
  `spin_max`      FLOAT            NOT NULL DEFAULT 1,

  /* Rig constant: metres per scale unit from the object origin to the disc along the object's local
   * +Z. 0.455 for MotionTable 0x09000132. Data, not a literal, so a future rig is a row edit. */
  `part_offset`   FLOAT            NOT NULL DEFAULT 0.455,

  /* 0 = skip terrain cells whose corners are water (LandDefs.TerrainType >= 0x10 WaterRunning).
   * Applies to a PAIR as a unit: both discs share the primary's XY, so one water test covers both. */
  `over_water`    BIT(1)           NOT NULL DEFAULT b'1',

  /* Pairing. Each planned cloud is a primary disc plus a partner disc of a DIFFERENT palette colour
   * (the partner reuses the primary's colour only when the palette has a single usable entry).
   * 0 = no partners at all, 1 = every cloud paired. */
  `pair_chance`      FLOAT         NOT NULL DEFAULT 1.0,

  /* Partner ObjScale = primary ObjScale x this. */
  `pair_scale_ratio` FLOAT         NOT NULL DEFAULT 0.75,

  /* Metres between the two disc PLANES, partner on the ground side. The WE ground stack used 0.3 m
   * between layers; sky discs are an order of magnitude bigger, hence 0.6. */
  `pair_gap`         FLOAT         NOT NULL DEFAULT 0.6,

  /* Partner MotionSpeed = primary MotionSpeed x this. 1.3333 is the inverse of the 0.75 size ratio,
   * so the partner's period is 75% of the primary's - the smaller layer turns faster. */
  `pair_speed_ratio` FLOAT         NOT NULL DEFAULT 1.3333,

  /* No-overlap culling. The required XY distance between two clouds' centres, as a multiple of the sum
   * of their PRIMARY disc RADII (radius = 0.725 x scale - half the rig's 1.45 m quad):
   *   1.0 = discs may just touch but never overlap
   *   1.2 = the same plus a 20% gap
   *   0   = culling off entirely
   * A pair counts as ONE unit (the partner shares the primary's XY and is smaller).
   *
   * The cull is computed ACROSS BLOCKS and is symmetric: a candidate survives only if no
   * HIGHER-PRIORITY candidate conflicts with it, where priority is a StableHash of
   * (region id, landblock id, candidate index). Every landblock derives the identical verdict for any
   * given pair, so two neighbours can never both spawn the same overlapping pair, and neither can drop
   * both. Discs at scale_max 240 have a 174 m radius against a 192 m landblock, so the neighbourhood
   * searched is derived from scale_max and min_separation rather than fixed at the 8 adjacents. */
  `min_separation`   FLOAT         NOT NULL DEFAULT 1.0,

  /* Which way up the rig hangs.
   *   'inverted' - the original pose: the object is rolled 180 - tilt degrees, so the disc hangs
   *                part_offset x scale BELOW the origin and the origin rides high above the disc.
   *   'mast'     - the NON-inverted pose: rolled `tilt` degrees, so the disc sits part_offset x scale
   *                ABOVE the origin and the origin sits just off the ground (ground + 1 m). The spin
   *                handedness is the mirror of 'inverted', which per the weenie header still trails.
   *
   * 'mast' exists because the client only animates an object whose ORIGIN is within roughly 60-90 m of
   * the character. An 'inverted' disc at scale 96-240 puts its origin 44-110 m above the disc and far
   * higher above the player, so it never spins from the ground; a 'mast' origin is 1 m off the terrain
   * and stays inside that range.
   *
   * NOTE what 'mast' does to height_min/height_max: the origin is pinned to ground + 1, so the disc
   * centre lands at max(rolled height, part_offset x scale x cos(tilt) + 1). At scale 96-240 that
   * second term is 44-110 m and the rolled height rarely wins, so the height columns become nearly
   * inert. That is deliberate - a mast disc's height IS its size. */
  `rig`              VARCHAR(16)   NOT NULL DEFAULT 'inverted',

  `last_modified` DATETIME         NOT NULL DEFAULT CURRENT_TIMESTAMP,

  PRIMARY KEY (`id`),
  UNIQUE KEY `sky_decor_region_name_uidx` (`name`),
  KEY `sky_decor_region_realm_idx` (`realm_id`, `enabled`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
