/* Drop the retired PvP template colour variant table.
 *
 * The earlier migration that created `pvp_template_variant` (2026-10-15-02-Pvp-Template-Variants.sql) was
 * deleted together with the DAO and EF mapping, after the Red/Blue variant feature was retired in favour of
 * team hue (Docs/Pvp/TEMPLATES.md, "Team hue"). Shards that already ran it (stage) still carry the empty
 * table; this removes it. Shards that never ran it (prod) have no such table, so this is a no-op there.
 * Re-runnable.
 */

DROP TABLE IF EXISTS `pvp_template_variant`;
