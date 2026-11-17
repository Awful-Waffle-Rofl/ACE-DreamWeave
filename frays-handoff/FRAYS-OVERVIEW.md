# The Frays: where it stands

## In short

The Frays are campfire camping for level 300+ characters, built so solo players and small groups can camp the way old AC
fellowships camped spawns. You light a campfire on the Ember Isles (a Drift copy of Vissidal Island in realm 1), and
creatures of the week's family rise around it. Killing them raises your **Heat**: creatures get tougher, pay gets better,
and every few Heat you pick an **Ember Boon**. A bar fills into a **Snarl** (a mini boss) and a server-wide bar fills
into the **Great Snarl**. XP and luminance are paid by kills and by the minute, and are tuned against a solo Threads
hour.

Everything is server side. There are no client or DAT changes. It is off until `frays_enabled` is set.

**Not ready to go live.** It still needs testing on loot rate, XP rate, luminance rate and brute vs swarm packs,
and the group finder (LFG) has not been tested. See "Needs testing before it goes live" below.

## Status at a glance

| Area | State |
|---|---|
| Core camping loop (fires, Heat, spawns, pay) | Not finished: it runs end to end, but Heat settling, the brute and swarm spawn mix, and loot balance are still being tuned (see Needs testing) |
| Village, NPCs, shops, portals | Working |
| Ember Boons (general and class boons) | Working |
| Snarl, Great Snarl, Lucid Night, weekly goal | Working |
| Campfire tiers, perks, Ascension | Working |
| Pets and companions | Working |
| Sky Fray group camps, Fray-wide fellowship | Working, lightly tested (needs real groups) |
| Group finder (`/fray lfg`, Fellowship Steward) | Not tested |
| Weekly layer (family, affix, climb score, tasks, Vault Keys, Sky Seals) | Working |

## How it plays

### Getting there
- The **Ember Isles portal** (2000075) stands in the Drift Network hall (realm 1), left of Drift Marae Lassel. Level 300.
- It lands in a walled village around the Temple of Xik Minru: a Great Hall, cottages, houses, a lifestone, braziers.
- Temple portals go to the **Western Shore** (Coast camps) and **Eastern Heights**; a blue portal goes to the **Sky Fray**.
- The Frays run in `fray_realm` 1. The base world Vissidal stays retail.

### Three Frays, 43 fires
| Fray | Fires | Type |
|---|---|---|
| Vissidal Coast (C8E2) | 18 | solo |
| Vissidal Heights (D5DF) | 20 | solo |
| Sky Fray (D5DA plateau) | 5 | group (3 members, 5 for the middle) |

`/fc` lists every fire, where it is and who holds it.

### A camp
- A solo fire is yours alone: its creatures only come to you, and only you can fight or loot them. One fire per IP.
- Your zone is a 90 m circle. A ring of wisps only you see marks the edge. Each pack is tethered 15 m from its own point.
- Each fire has 10 spread-out spawn points: brute points (1 to 2 tough creatures) and swarm points (6 to 8 small ones),
  alternating, each tethered to its point. Two **Gilded** points pay extra Embers, keys, Snarl bar and loot, and carry the week's
  ward (half damage from one type, more from its opposite). One point holds the **Ember Shrine** (Ember Fury: +10%
  damage for 3 minutes).
- Supply follows the camper: the points field what you clear plus 30%, so a faster build always has more to kill.
- Leave or log off and your fire waits 10 minutes. Die and you respawn beside your fire.
- The **Campfire Hearthstone** (and `/frays recall`) takes you back to your fire.

### Creatures
- One creature family a week for every Fray, drawn from the Threads species tables (`family_source` threads).
- Creatures are built the way a Thread builds them ("Threads-shaped"), at your level up to 410, then Heat makes them
  tougher through the Heat curves.
- Casters only appear from Heat 17 (`caster_min_heat`). Below that, creatures cast no spells and
  fight in melee. No Frays
  creature dispels or reflects.
- A creature hit takes a share of **your** max health (1.5% at Heat 0 to 16% at Heat 20, with a cap per hit), so danger
  scales the same at any level and armor.

### Heat
- Kills give Heat points by the health you removed. Heat runs 0 to 20 (Higher Flame perk: up to 25).
- From Heat 18 Heat burns down every minute, so only fast builds hold 19 and 20.
- A death costs 2 Heat. Two deaths within 60 minutes **settle** you: Heat holds 2 below the lowest Heat you died at for
  30 minutes (doubling to 120 on repeats), then climbs again. This keeps a camper from dying in a loop.
- `/frays stop` holds Heat, `/frays go` lets it climb. Holding 15 fighting minutes without dying sets your **Heat rating**;
  a camp starts 2 under it.

### Pay
- **XP and luminance**: set by level, times the Heat bonus; 70% is paid per kill (by health) and 30% per active
  minute. A typical camper earns about 70%, 75% and 80% of a solo Threads hour at Heat 0, 10 and 20. Never split, and no boon or perk adds XP.
- **Embers** (bonded currency, bankable with `/b d e`): per kill by health, plus 2 a minute by your fire. No weekly cap.
- **Vault Keys**: a key bar fills from Heat-weighted kills (Gilded count x3); each Snarl gives one too. Keys open the
  Ember Vault (tier 8 items). Speed past a soft knee fills the bar at a quarter rate.
- **Corpse loot**: tier 8, generous (more than a retail tier 8 mob), more with Heat. A corpse holds at most 25 items;
  the rest goes onto the owner's key bar (17 items make one Vault Key), so a brute pack is worth a swarm pack.

### The Snarl and the Great Snarl
- **Snarl**: your own mini boss of the week's family when your Snarl bar fills (about 2, 3 and 4 an hour at Heat 0, 10
  and 20). It drops a cache, a Vault Key, and chances at a Slayer Stone, Kindling, a Snarl Trophy and a Fray Effigy.
- **Great Snarl**: one server-wide bar from every camper. When it fills, every fire holder is warned and `/fray great`
  pulls them to it. Every camp is held while it is up. It leaves if not beaten in 15 minutes. Rewards: a tier 8 cache
  and Emberblessed (+10% Embers, +5% damage for 30 minutes).
- A **Snarl Monument** with finders' stones and a Champion's stone, and a **Snarl Trophy Wall**, record the kills.

### Ember Boons
- Every 3 Heat you are offered 3 boons for this camp (`/fp 1`, `/fp 2`, `/fp 3`, or your boon plan picks after 30 s).
- 18 general boons (pack damage, single target, sustain, Embers, Heat, Snarl) and class boons that raise your class
  abilities while camping. Every boon has a cost (for example "you take 3% more damage").
- Picked boons join your collection for good; Sigils from the Boonwright raise a collected boon's rank.
- Blood Mage has two AoE boons (Crimson Splash, Hecatomb's Reach) to bring single-target drains up to pack speed.

### Your campfire
- 10 tiers bought with Embers (27,500 total); each tier gives a perk point (Higher Flame, Ember Hearth, Snarl Lure,
  Bellows, Searing, Ash Guard). Presets spend points for you; one free reset a week.
- Name your fire, pick spark colours and an epithet.
- **Ascension**: at Heat rating 20, reset the rating for +1% damage and +1% Embers, up to 5 times.

### Pets and companions
- **Fray Effigies** (Snarl drops) unlock a family's form. `/fray pet <form>` calls a harmless follower.
- Each form takes a style for good: Warder (less damage taken), Mender (healing) or Striker (more damage), levelled with
  Kindling to 10. A Rebinding Ember changes the style.

### Groups
- **Sky Fray**: 4 Spire fires for 3+ members, Sky Hearth for 5+. Members vote on boons. Group runs score separately.
- **Fray-wide fellowship**: lighting a solo fire puts you in "The Frays" fellowship (up to the server fellowship size, then "The Frays 2"). It shares chat and the fellowship window only: XP and loot sharing are off.

### The weekly layer
- One family, one **weekly affix** (22 in rotation; affixes only make a week harder, never pay more).
- **Lucid Night**: when the week reaches 25 Great Snarls, the last 24 hours before the weekly reset give every camper +5% damage,
  +10% Embers, 2 more items in each Snarl cache and a Vault Key on
  their first kill.
- **Weekly Frays goal**: a shared bar paying 15 Vault Keys, a cache, a Slayer Stone, a Kindling and task XP to
  every camper of the week.
- **Climb score**: the week's best solo or group run pays Bronze, Silver or Gold Vault Key tiers (up to 6 keys) and
  score titles.
- **Kill tasks** from the village task-givers, including a weekly Kill the Great Snarl.
- **Sky Seals** from group camps (Snarls, Great Snarls, Heat 20, a weekly task) buy looks, Vault Key, Kindling and Sigil
  Shard bundles and the Sky Banner (+10% Embers and key bar for the group), priced to add about 10 to 15% to a group hour.
- **Fray Journal**, **Codex**, achievements, leaderboards (`/top fray ...`) and a camp report.
- **Custom dye**: `/dye` and the Prismatic Dye Pot (2000192), which locks to its first colour.

## Built around UCMing

The concept is focused around UCMing, and that is the way a lot of things are tuned toward. The rules are meant to
be fair to an unattended camper without paying speed without limit:
- Pay is by health cleared and capped by soft knees, so a faster build earns more but not linearly.
- The settle rule stops death loops. Respawn at the fire. No dispels or reflects. One fire per IP.

## Known gaps and open items

- **Settle rule**: a fix makes the settle cap take effect at once (it used to wait for the next climb). Not yet run live.
- **Changed but not yet run live**: `heat_settle_window_minutes` 20 to 60, Ember Fury 0.05 to 0.10, shrine buff
  120 s to 180 s. These are in the code defaults and in the settings file.
- **Tested by unit tests only**: the 25-item corpse cap with overflow to the key bar
  (`loot_corpse_cap` 110 to 25, new `loot_overflow_per_key` 17), and brutes at 1.3x size (was 1.2x). A server that
  still has the old default values moves them to the new ones at boot.
- **WCIDs and property ids** use the 2000000 range and the 9000s; check them against your own registry (see README).

## Needs testing before it goes live

- **Loot rate**: corpse items and Vault Keys an hour, across Heat.
- **XP rate** and **luminance rate**: an hour, across Heat and level, against a solo Threads hour.
- **Brute vs swarm packs**: whether a brute pack really pays the same as a swarm pack in play, and how often brute
  points come up.
- **Heat settling** over a long session.
- **Group finder (LFG)**: not tested.
- **Group camps**: need testing with real groups.

Expect the `frays_*` values for Heat, spawns and loot to change after this testing.
