# The Frays: hand-off package

Campfire camping for level 300+ on the Ember Isles. `FRAYS-OVERVIEW.md` explains the feature and where it stands.
This file is how to install it. This folder sits on the `frays` branch; the code is the commit before the one that
added this folder. **It is not ready to go live yet**: loot, XP and luminance rates, brute vs swarm
packs and the group finder still need testing (see the overview).

Built and tested against **ACE-DreamWeave origin/master 28da77346** ("Refresh from WaffleACE at 14774cfab").
On top of that commit the server and tests build with 0 errors,
and the Frays, market, dye and config tests pass (1276 pass). Five tests fail on 28da77346 both with and without the
Frays code: they read content files and a `config-defaults.tsv` that are not in the public repo.

## What is here

| Path | What it is |
|---|---|
| `FRAYS-OVERVIEW.md` | What the Frays are, what works, what is open |
| `FRAYS-FEATURES.md` | Every feature in detail: boons, Heat, rewards and prices, tasks, groups, pets, affixes |
| `Content/sql/weenies/*.sql` | 146 weenies (2000029 to 2000206), one file each, written by ACE's WeenieSQLWriter |
| `Content/sql/frays-placements.sql` | The one fixed placement: the Ember Isles portal in the Drift Network hall (realm 1) |
| `Content/sql/frays-settings.sql` | The tuned `frays_*` settings from the test server, including the village layout |
| `WCIDS.md` | Every WCID with its name and class name |

The code commit also carries `Database/Updates/World/2026-11-08-00-Add-Frays-Ember-Vault.sql` (treasure_death 2000153, the
Ember Vault's loot table), and the new rows in `config-metadata.tsv`, `property-registry.tsv` and
`command-classification.tsv`.

## Install

1. **Code.** It is already on this branch: one commit on top of 28da77346 (163 files, about 31,740 lines added).
   Merge or rebase `frays` as usual, then build with `-p:Platform=x64`. The `Content/sql` paths below are inside this
   `frays-handoff` folder; `Database/` is at the repo root.
2. **World content.** Import into `ace_world`, in this order:
   - `Database/Updates/World/2026-11-08-00-Add-Frays-Ember-Vault.sql` (from the code commit; applied at startup if you auto
     apply updates)
   - every file in `Content/sql/weenies`
   - `Content/sql/frays-placements.sql`

   Each weenie file deletes and recreates its own wcid, so re-running is safe. They are self-contained (no
   INSERT...SELECT from retail rows).
3. **Settings.** Import `Content/sql/frays-settings.sql` into `ace_shard`. It writes values only
   (`ON DUPLICATE KEY UPDATE value`), so existing descriptions stay. It sets `frays_enabled` on: edit that line to 0
   to import dark. Live state (current Fray rotation, trophy walls, group runs) and local file paths are left out and
   start fresh.
4. **Restart** the server. On boot the log names the active Frays, the excluded and all-caster families.
5. **Check in game** as an admin: `/frays admin village check` (village pieces), `/frays admin portals` (drops a
   temporary Ember Isles portal beside you), then light a fire on the Coast (`/fc` lists them).

Without step 3 the server runs on the code defaults, which match the test server except for the village layout
(the built-in layout is used) and the house cover fixes.

## Things to check against your own registries

- **WCIDs**: all new ones are in the 2000000 range (list in `WCIDS.md`). If any clash with yours, they need renumbering
  in the SQL and in the code (most are constants in `Source/ACE.Server/Frays/FraysDefs.cs`).
- **Realm instance guid**: the portal placement uses guid `0x70007000` in realm 1, landblock 0x0007.
- **Property ids**: see "New property ids" below; they are also in `property-registry.tsv`.
- **Settings**: every new setting is prefixed `frays_` and listed in `config-metadata.tsv`.

## Changes outside the Frays folder

Every file the code commit changes outside `Source/ACE.Server/Frays/`, `Source/ACE.Server.Tests/` and the .tsv files.
Table 1 is what runs on every server for every player, even with `frays_enabled` off. Table 2 only does something for
Frays objects, campers, or admin commands. The Frays and dye item hooks key off WCIDs
2000029 to 2000206, so any custom weenies of your own in that range would trigger them.

### Table 1: Always on (affects every player)

| File(s) | What changed |
|---|---|
| Command/Handlers/FraysCommands.cs (new) | `/fray`, `/frays`, `/fg`, `/fc`, `/fp` are always registered and answer with `frays_enabled` off. `/frays admin` needs Admin. |
| Physics/PhysicsObj.cs, Physics/PhysicsUpdateBudget.cs (new) | `update_object` stops its catch-up loop after 0.25 s of wall time in one call and drops the rest of the owed time, as it already did for a gap over HugeQuantum. Applies to every physics object. |
| WorldObjects/WorldObject_Tick.cs | A spell projectile is destroyed after a single physics update of 1 s or more (was 5 slow updates), and only while its PhysicsObj is still active. |
| WorldObjects/Player_Inventory.cs | General fix: when moving a wielded weapon to a pack fails or unwields nothing, the stance is set back to what the wielded items call for (a dual wielder could be left in hand stance). Adds an Info log line on each such move. |
| WorldObjects/Player_Combat.cs (logging part) | The `[COMBATMODE]` log line gains `wantStance`, `busy` and `hands`. Logging only. |
| Dye/*.cs, Command/Handlers/DyeCommands.cs (new), Player_Use.cs, WorldObject_Networking.cs, Creature_Networking.cs, Program.cs (dye parts) | A custom dye system, not tied to the Frays and with no on/off setting. `/dye` for players. The Prismatic Dye Pot (2000192) stores per-slot colours in PropertyString 9050; undyed items render as before. The palette match pool is built in the background at boot. |
| WorldObjects/Player_Bank.cs, Command/Handlers/PlayerCommands.cs (bank part) | Embers (2000032) become a bankable bonded currency: `/b d e`, `/b w e`, `/b d` includes them, transfers refused. Vendors priced in 2000032 can spend the banked balance. PropertyInt64 9029. Does nothing without Ember items. |
| PlayerCommands.cs (`/abilitymsgs`), Player_ClassAbilityBuffs.cs (mute part) | A player toggle that hides class ability buff chat lines. PropertyBool 9090, off by default. |
| PlayerCommands.cs (`/top fray`) | `/top` gains a `fray` sub-board. Empty with the Frays off. |
| Managers/WorldManager.cs | `UpdateGameWorld` calls `FraysManager.Tick()` every tick inside a try/catch. With the Frays off it only keeps the village doors shown. Fire, long-term and weekly work only runs with the Frays on. |
| Program.cs (Frays part) | At boot `FraysFamilies.WarmUp` builds the creature families (about 748 weenies) in the background when `frays_families_enabled` is on (default on), even with the Frays off. `FraysSettings.SyncOldDefaults` rewrites any `frays_*` setting still holding an old code default. |
| Managers/PropertyManager.cs, Managers/Market/ConfigMetadata.cs | All `frays_*` settings are registered as defaults under a new "The Frays" group. Adds a global bool `sp_damage_by_attack_height` (default true, only read on the Frays) and `TryGetCached*` helpers. |
| Managers/ServerMetrics.cs | Registers the Frays gauges and counters. Zero with the Frays off. |

### Table 2: Only when the Frays are on, or admin/test commands only

| File(s) | What changed | Gate |
|---|---|---|
| ClassAbilities/Abilities/*.cs, Player_ClassAbilityBuffs.cs, Player_ClassAbilityMalediction.cs, Player_ClassAbilityCombat.cs | Chance or rank bonuses times `FraysManager.AbilityBoost` (class boons). | Returns 1 unless the player is camping at a fire (`FraysMods`). |
| SpellAoeAbility.cs | Samples reach for the camp log. | A `FraysTag` target on the Frays. |
| Entity/DamageEvent.cs | Frays damage rules for player and monster hits, an attack skill floor for fire creatures. | `FraysTag` on attacker or defender. |
| Entity/DamageHistory.cs | Feeds the camp session log. | Victim has a `FraysTag`. |
| Entity/Landblock.cs | Skips retail spawns, generators and encounters on Fray landblocks, places campfires on load, puts them out on unload. | `frays_enabled`, realm `frays_fray_realm` (1), not ephemeral. |
| Monster_Awareness/Combat/Navigation/Tick.cs, Creature_Combat.cs, Creature_ClassAbilityPin.cs, Creature_Death.cs, Corpse.cs, CombatPet.cs, Pet_Monster.cs | Fire creatures target and are harmed only by their owners; leash, return speed, pack linking, kill and corpse reports, owner-only corpses. | `FraysTag`, Great Snarl or loot owner fields on the creature or corpse. |
| Creature_Frays.cs, WorldObject_Frays.cs (new) | Runtime-only fields (never saved), and two virtual properties read while building create messages. | Defaults change nothing. |
| Player_Tracking.cs, WorldObject_Networking.cs (non-dye part) | Per-viewer create messages for fire creatures (attackable to owners only), owner-only objects hidden from others, house doors created ethereal then solid. | `FraysTag`, `FraysOnlyFor`, `FraysCreateEthereal`. |
| Player_Combat.cs (CanDamage, CheckPKStatusVsTarget, GetDamageType) | Only a fire's owners may harm its creatures; slash/pierce weapons pierce on a low attack and slash on a medium one. | `FraysTag` target; the attack height rule needs `sp_damage_by_attack_height` and being on the Frays. |
| SpellProjectile.cs, WorldObject_Magic.cs | Frays damage for bolts and Harm spells, the Crimson Splash boon, the camp log's damage by school. | `FraysTag`, `FraysMods`. |
| Player_ClassAbilities.cs | Blood Mage boons on spell hit. | `FraysMods`. |
| Player_Magic.cs, Player_Melee.cs, Player_Missile.cs, Creature_Missile.cs | Camp log casts and shots, cleave and multishot reach sampling. | `FraysMods`, or `FraysTag` on the Frays. One dictionary lookup per attack otherwise. |
| Player_Death.cs | The owner's fire cools; respawn beside your own solo fire. | Owning a lit fire, `death_respawn_at_fire`. |
| Player_Fellowship.cs | Refuses disband, lock, rename, open, recruit, dismiss and new leader in the Frays fellowship. | The fellowship is a Frays fellowship. |
| WorldObject_Database.cs | Campfires and fire creatures are never saved. | `FraysTransient`. |
| Player_Use.cs (Frays part), Book.cs, Player_Book.cs, Chest.cs, Key.cs, Lock.cs, Player.cs | Hooks for Frays items: campfires, shrines, Journal, Ember Vault, Vault Keys, Snarl Monument text. | Frays WCIDs (2000029 to 2000206) or fire-placed objects; not tied to `frays_enabled`. |
| WorldEvents/WorldEventCombatCreditHook.cs | Calls `FraysManager.OnCombatContact`. | `FraysTag` on victim or attacker. |
| Creature_MonsterEffects.cs | New `RemoveMonsterEffects` and `FilterMonsterEffectSpec` (no dispels or reflects on fire creatures). | Only Frays fires call them. |
| Managers/Market/MarketApiHost_Admin.cs | Config edit reply warns when a `frays_*` value was clamped or unreadable. | `frays_*` keys only. |

### New property ids

| Enum | Id | Name |
|---|---|---|
| PropertyBool | 9089 | FraysEmbered |
| PropertyBool | 9090 | ClassAbilityChatMuted |
| PropertyInt64 | 9029 | BankedEmbers |
| PropertyString | 9034 | FraysState |
| PropertyString | 9050 | DyeColours |
| PropertyString | 9051 | DyePotColour |

### New commands outside FraysCommands

- Player: `/abilitymsgs`, `/dye`
- New subcommands: `/top fray ...`, `/b d e`, `/b w e`
