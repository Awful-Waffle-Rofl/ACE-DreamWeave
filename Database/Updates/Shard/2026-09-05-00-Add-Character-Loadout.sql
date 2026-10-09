/* Player Loadouts - one stored build per character per slot.
 *
 * A loadout owns how a character's progression has been SPENT: which skills are trained and
 * specialized, which class abilities are learned, and which items were worn. It does NOT own the
 * progression itself - level, lifetime XP, attributes, vitals, augmentations and the spellbook stay
 * on the character and are shared by every slot. See Docs/Loadouts/DESIGN.md section 1.
 *
 * Slot 1 is the character's existing build. A slot that has never been visited has NO ROW here; the
 * fresh build is generated on first switch rather than pre-created at unlock time, so the absence of
 * a row is meaningful and must not be backfilled.
 *
 * The three spendable pools (available XP, skill credits, class ability points) are deliberately NOT
 * columns here. Storing them per slot would silently delete progression earned on the slot the player
 * was not standing on. Instead each pool is carried across a switch as a DELTA - release what the
 * outgoing build had committed, commit what the incoming build needs, refuse the switch if that goes
 * negative - so XP, credits and points earned while standing on one loadout are immediately available
 * on all of them. Deliberately NOT recomputed from the character's lifetime ledgers: neither ledger
 * sees every spend (class ability points are also a vendor currency), and a recompute refunds what it
 * cannot see. See ACE.Server/Entity/Loadouts/LoadoutPools.cs.
 *
 * `equip_Json` entries carry BOTH guid and wcid. That is not redundant: the account vault collapses
 * any item provably identical to its weenie template into an account_vault_stack ledger row and
 * DESTROYS its biota, rebuilding a new object with a NEW guid on withdraw. A guid-only equip set
 * therefore rots silently for any pristine item the player banks.
 *
 * No foreign key to `character`, matching character_speed_run and the account_vault* tables.
 *
 * Column casing intentionally matches ACE's existing shard tables (e.g. `biota_Id`).
 *
 * `updated_At` IS UTC. The server always writes it explicitly as DateTime.UtcNow. The
 * CURRENT_TIMESTAMP column default is a backstop for a row inserted with the value left unset, and it
 * stamps the DATABASE SERVER'S LOCAL time - any row that falls back to it is off by the local UTC
 * offset. Always write the column explicitly.
 *
 * Idempotent: safe to re-run. Only ever adds the table.
 */

CREATE TABLE IF NOT EXISTS `character_loadout` (
  `character_Id`    int unsigned    NOT NULL,
  `slot`            tinyint unsigned NOT NULL COMMENT 'Slot 1 is the base build; 2+ unlock by level',
  `name`            varchar(32)     NULL COMMENT 'Player-set display label, or NULL for unnamed',
  `skills_Json`     text            NOT NULL COMMENT 'Per-skill SAC / ranks / PP / InitLevel',
  `abilities_Json`  text            NOT NULL COMMENT 'Class ability id to rank',
  `equip_Json`      text            NOT NULL COMMENT 'Remembered worn set - guid AND wcid per entry',
  `updated_At`      datetime        NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`character_Id`, `slot`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Player Loadouts - one stored build per character per slot, no FK to character';
