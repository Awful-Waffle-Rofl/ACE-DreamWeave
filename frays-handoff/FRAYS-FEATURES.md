# The Frays: every feature

Everything below is what ships, with the values from `Content/sql/frays-settings.sql`. Setting names are given in
brackets without their `frays_` prefix so they can be found and tuned. Shop prices are what a player pays: the three
currency vendors sell at half the item's Value.

---

## 1. The idea

Campfire camping for level 300+ characters. You light a campfire on the Ember Isles and creatures of the week's family
rise around it. Killing them raises your **Heat**, which makes them tougher and pays better. Every few Heat you pick an
**Ember Boon**. Bars fill into a **Snarl** (your mini boss) and a server-wide **Great Snarl**.

The concept is focused around UCMing, and a lot of it is tuned that way: pay by health cleared with soft caps, no
dispels or reflects, a settle rule against death loops and respawn at your fire.

Solo players camp on 38 solo fires; groups of 3 to 5 camp on 5 group fires in the Sky Fray.

---

## 2. Getting there and the village

**The portal.** The Ember Isles portal (2000075) stands in the Drift Network hall (realm 1), left of Drift Marae Lassel.
Level 300 (`min_level`). It lands at the Temple of Xik Minru on the Ember Isles, a Drift copy of Vissidal Island
(`fray_realm` 1). Retail Vissidal is unchanged.

**The village.** A walled ring (Arwic walls and 12 watchtowers) round the temple, with three gates.
- Great Hall, a chapel, a cottage and town houses.
- Braziers, torches, banners and a Fray Tapestry on the south wall.
- A lifestone on the rise inside the east wall.
- 12 Ember Vaults in two rows beside the Great Hall.
- The Snarl Monument.

**Portals.**
| Portal | Goes to |
|---|---|
| Western Shore (in the temple) | the 18 solo Coast camps |
| Eastern Heights (in the temple) | the 20 solo Heights camps |
| Sky Fray (in the temple) | the floating plateau: 5 group camps |
| Drift Network (in the square) | back to the Drift Network hall |
| Ember Isles (3 return portals) | back to the village from the Shore, the Heights and the plateau |

**Town NPCs.**
| NPC | What they do |
|---|---|
| Fray-Warden | First talk: gives the Fray Journal, a "Welcome to the Frays" banner and the How guide page. After that: the week summary and guide menu. Hands out claims (items that did not fit, weekly Tapestry prizes). |
| Guide sign "THE FRAYS - START HERE" | A 6-page book: Start here, Heat, Snarls, Your camp, Rewards, Commands. |
| Ember Trader | Sells for Embers (section 11). |
| Boonwright | Sells Sigils and the Rebinding Ember for Embers. |
| Sky Steward | On the Sky Fray plateau, beside its return portal. Sells looks, Vault Key, Kindling and Sigil Shard bundles and the Sky Banner for Sky Seals. |
| Ember Apothecary | Pyreals: healing, stamina and mana kits and potions. |
| Ember Archmage | Pyreals: scarabs, spell components, mana stones, trade notes, dispel gems, portal gems, foci. |
| Ember Bowyer | Pyreals: bows, crossbows, atlatls, ammunition, fletching supplies. |
| Fellowship Steward | The group finder (section 10). |
| Fray Huntmaster | Daily family hunts. |
| Fray Snarl-Tracker | Daily Snarl and health tasks. |
| Fray Heat-Warden | Daily Heat tasks. |
| Fray Week-Warden | Weekly tasks. |

---

## 3. The Frays and the campfires

**Three Frays, 43 fires.**
| Fray | Fires | Type |
|---|---|---|
| Vissidal Coast | 18 (Ember Landing to Lantern Shore) | solo |
| Vissidal Heights | 20 (Cinder Bowl to Torch Rise) | solo |
| Sky Fray | 5: Western, Eastern, Southern, Northern Spire, Sky Hearth | group |

`/fc` lists every fire, its coordinates and who holds it.

**Lighting a fire.**
- Use an unlit campfire and confirm the yes/no ("You start at Heat N"). It is free.
- One fire per player, and one fire per IP (`one_per_ip`).
- Lighting puts you in the shared "The Frays" fellowship. Leaving that fellowship gives up the fire.
- The first time, you get a Campfire Hearthstone with a popup.

**Your camp zone (solo fires).**
- A 90 m circle (`zone_radius`). A ring of Fire Wisps only you can see marks the edge.
- The fire's creatures come only to you, and only you can fight or loot them.
- Creatures notice you at 10 m (`aggro_range`). Each pack is tethered 15 m from its own point (`pack_tether`); past it
  they walk home and heal.

**Spawn points, pathing and routes.** Every solo fire has the same fixed layout (`fixed_layout`):
- 10 points, spread out, at least 40 m from each other and from the fire. That is four times the aggro range, so
  there is a clear lane between packs and every pull is one pack.
- Plain points alternate **brute** and **swarm** going round the ring.
  - A brute pack is 1 tough creature (2 from Heat 10).
  - A swarm pack is 6 / 7 / 8 small creatures at Heat 0 / 10 / 20, each with 40% of a normal creature's health.
- 2 points are **Gilded** and 1 is the **Ember Shrine**. They stand on the rim, the two Gilded at least 120 degrees
  apart and the shrine at least 90 degrees from each. No single loop takes in all three, so the route is a choice.
- Hit or wake one creature and its whole pack comes, never a neighbouring pack (`pack_link`).
- **Supply follows the camper.** Points field what you clear plus 30% (`spawn_follow_extra`), never under an hourly
  floor (`spawn_target_by_heat`), so a faster build always has more to kill. Brute and swarm points each follow how
  fast you clear their own type, averaged over the last 90 s with 60% on top (`pack_type_follow`,
  `pack_type_follow_seconds`, `pack_type_follow_extra`), so a single-target camper who only kills brutes finds the
  next one back when they come round.
- Points rest about 2.5 minutes after a clear. Every 10 minutes a different quarter of the zone is a hot spot: its
  points rest half as long and field one more creature.
- When you take a camp, every point fields its pack at once. At most 15 creatures alive per player.

**Leaving and coming back.**
- Leave the zone or log off and the fire waits 10 minutes (`away_seconds`). The fire also goes out if no owner stands
  in its zone for 15 minutes (`fire_away_seconds`).
- After a camp ends you cannot take another fire for 5 minutes (`retake_cooldown_seconds`).
- The Campfire Hearthstone, or `/frays recall`, takes you to your fire (2 minute cooldown).
- A death respawns you beside your fire, not at the lifestone (`death_respawn_at_fire`).

---

## 4. Controlling your camp

| Command | What it does |
|---|---|
| `/frays stop` | Hold your Heat where it is: no climbing, no decay. Stopped minutes do not count toward your rating. |
| `/frays go` | Let your Heat climb again (the default). |
| `/frays start <n>` / `auto` | Start your next camp at any Heat up to your rating. Auto (default) is 2 under your rating. |
| `/fp 1`, `/fp 2`, `/fp 3` | Pick an Ember Boon. |
| `/fray boons ...` | Your boon order and plan, which pick for you after 30 s (section 6). |
| `/fray perks <preset>` | How perk points spend: balanced, aoe, single, embers or off. |
| `/frays fire`, `/frays fire upgrade`, `/frays perk <name>` | Campfire tiers and perks (section 7). |
| `/fray name`, `/fray sparks`, `/fray epithet` | Your fire's name, spark colour and title. |
| `/frays status` | Everything about your camp. |
| `/fp`, `/frays bar` | Your Heat, Snarl and Great Snarl bars. |
| `/fray setup` | Your whole loadout, with the command that changes each part. |
| `/fray bonuses` | Your Frays damage and reduction against their caps. |
| `/frays report` | Your last camp report. |

---

## 5. Heat

**What it is.** Your own difficulty and pay level at your fire, 0 to 20. The Higher Flame perk raises the cap by up
to 5, so 25 at most.

**Earning Heat.**
- Kills give Heat points by the health you removed. Level N needs 10 + N points, so 0 to 20 takes 390 points.
- With no Heat rating yet you climb 2.5 times as fast below Heat 5.
- The bar shows each 25% filled. Each level-up has a centre-screen line and a flame swirl.

**Losing Heat.**
- **No kills:** after 150 s with no kill you lose a point every 30 s, with a warning 10 s before.
- **Top-Heat burn:** from Heat 18 Heat burns every minute, killing or not: 3 points a minute at 18, 14 at 19, 15.5 at
  20. Only fast builds hold 19 and 20.
- **Death:** costs 2 Heat (1 with Phoenix Coal). Your creatures above the new Heat fade and their points refill at once
  at the new Heat. A Snarl stays but drops to the new Heat.
- **Settle rule (solo):** two deaths within 60 minutes settle you. Heat holds 2 below the lowest Heat you died at for
  30 minutes, then climbs again. Each further settle in the camp doubles it, up to 120 minutes. This stops death loops.

**Heat rating.** 15 counted fighting minutes without dying sets your rating to the lowest Heat in that run. A camp
starts 2 under it. It never goes down (except by Ascension). Solo and group ratings are separate.

**What Heat changes.**
| | Heat 0 | Heat 10 | Heat 20 |
|---|---|---|---|
| Creature health | x0.7 | x1.2 | x3 |
| Creature spell damage | x0.6 | x1.3 | x3.6 |
| A creature's weapon hit, as a share of your max health | 1.5% | 6% | 16% |
| Most one hit can take | 15% | 30% | 50% |
| XP and luminance bonus | +43.5% | +53.75% | +64% |
| Embers, keys, Snarl bar and loot per creature | x0.86 | x0.92 | x1.0 |
| Swarm pack size | 6 | 7 | 8 |
| Brute pack size | 1 | 2 | 2 |

Because a hit is a share of your own max health, danger scales the same at every level and armour (armour can cut a
hit by up to half). A creature always hits at the lower of its own Heat and yours. Fire creatures swing at no less
than your defense + 50 + 5 per Heat.

---

## 6. Ember Boons

**How you get them.**
- Every 3 Heat you reach for the first time in a camp (3, 6, 9, 12, 15, 18, and 21, 24 with Higher Flame) you are
  offered **3 boons**.
- A camp that starts high is owed the offers for the levels it skipped.
- Pick with `/fp 1`, `/fp 2` or `/fp 3`. A window lists the three and a centre-screen countdown shows the time left.
- After 30 s your boon order or plan picks for you (see "Your boon plan" below), so an unattended camp never waits.
- If you have a class ability with a boon, 1 or 2 of the 3 are class boons. Rare boons are offered from Heat 10.
- Each boon can be held once per camp, and boons last until the camp ends.
- Every boon has a cost, so every pick is a trade. No boon touches XP or luminance.
- Boon damage and reduction are capped at +10% each, inside the Frays pools (+50% damage, 35% reduction).

**General boons (18).** Values are rank 1, then the top rank.
| Boon | Type | Gives | Costs | Max rank |
|---|---|---|---|---|
| Wildfire Heart | aoe | +2% damage to pack creatures (5%) | 2% less damage to Gilded | 4 |
| Packmaster | aoe | about 10% more creatures an hour, +3% Embers (7.5%) | 5% less damage dealt | 4 |
| Kindled Blade | single | +1.2% damage (3%) | take 1% more damage | 4 |
| Hunter's Coal | single | +4% damage to Gilded (10%) | 1% less damage to packs | 4 |
| Ember Skin | sustain | 2% less damage taken (5%) | 1% less damage dealt | 4 |
| Iron Ember | sustain | 2% less physical damage taken (5%) | 0.8% less damage dealt | 4 |
| Ashen Mend | sustain | heal 3% of max health every 10 s (7.5%) | 2% less damage dealt | 4 |
| Bright Wick | sustain | restore 1% of max mana every 5 s (2.5%) | 1% less damage dealt | 4 |
| Coal Purse | embers | +8% Embers (20%) | 5% less damage dealt | 4 |
| Slow Coals | heat | Heat decays 30% slower (75%) | 3% fewer Embers | 4 |
| Snarl Bait | snarl | Snarl bar fills 15% faster (37.5%) | 4% fewer Embers | 4 |
| Grudge Fire | snarl | +4% damage to the Snarl (10%) | 1% less damage to packs | 4 |
| Inferno Core (rare) | single | +3.3% damage (5%) | take 2% more damage | 2 |
| Firestorm (rare) | aoe | +6.7% damage to packs (10%) | 3% less damage to Gilded | 2 |
| Phoenix Coal (rare) | sustain | a death costs 1 Heat, not 2 | take 3% more damage | 1 |
| Golden Cinders (rare) | embers | +15% Embers (22.5%) | 7% less damage dealt | 2 |
| Undying Flame (rare) | heat | your Heat never decays (the top-Heat burn still applies) | 5% fewer Embers | 1 |
| Snarl's Bane (rare) | snarl | Snarl bar fills 35% faster (52.5%) | 7% fewer Embers | 2 |

**Class boons (21).** Offered only if you have the ability. They raise the ability's own bonus, and grow with your rank
in it (x1, x1.5, x2), not with Sigils.
| Boon | Needs | Raises (rank 1 / 2 / 3) | Costs |
|---|---|---|---|
| Kindled Frenzy | Frenzy | attack speed per stack +25 / 37.5 / 50% | take 3% more damage |
| Blood Embers | Bloodlust | healing +30 / 45 / 60% | take 3% more damage |
| Coal Executioner | Executioner | bonus +30 / 45 / 60% | 3% less damage to the Snarl |
| Venom Wick | Poison Weapon | damage +3 / 4.5 / 6% | 1% less damage to packs |
| Caustic Ash | Acid Proc | chance +25 / 37.5 / 50% | 2% less damage to Gilded |
| Ember Opening | Opportunist | bonus +30 / 45 / 60% | 3% fewer Embers |
| Scatter Sparks | Multishot | each extra arrow +4 / 6 / 8% | 6% less damage to Gilded |
| Burning Thorns | Thorns | reflect +30 / 45 / 60% | 1% less damage dealt |
| Echoing Flame | Echo Cast | chance +25 / 37.5 / 50% | 3% less damage to Gilded |
| Cascading Cinders | Cascade | chance +25 / 37.5 / 50% | 3% fewer Embers |
| Spellfire Edge | Spellblade | chance +4 / 6 / 8% | 1% less damage to packs |
| Runefire | Runeblade | chance +9 / 13.5 / 18% | take 3% more damage |
| Sundered Coal | Sundermark | chance +14 / 21 / 28% | 3% less damage to the Snarl |
| Void Ember | Void Damage | bonus +30 / 45 / 60% | 2% less damage to packs |
| Blood Kindling | Hemomancy | damage bonus +30 / 45 / 60% | 3% fewer Embers |
| Banked Embers | Sanguine Reserve | each Blood Charge +30 / 45 / 60% | take 3% more damage |
| Smoldering Curse | Malediction | intensity +30 / 45 / 60% | 3% less damage to the Snarl |
| Ember Tithe | Blood Price | spell damage bonus +30 / 45 / 60% | 2% less damage to Gilded |
| Kindled Thralls | Empowered Summons | summons' bonus +30 / 45 / 60% | 3% less damage to Gilded |
| Crimson Splash | Sanguine Reserve | drains and Blood Mage bolts also hit 1 / 2 / 3 creatures within 8 m | take 3% more damage |
| Hecatomb's Reach | Sanguine Reserve | Hecatomb and Raven Fury hit every creature within 8 m (75%) | 3% less damage to Gilded |

Crimson Splash and Hecatomb's Reach bring single-target Blood Mages up to pack speed.

**Collecting and levelling boons.**
- Every boon you pick joins your collection for good. At a group camp it joins every member's collection.
- A general boon works at your collection rank in every camp. Ranks raise its value by half its first value each:
  x1, x1.5, x2, x2.5. Commons go to rank 4, rares to rank 2, Phoenix Coal and Undying Flame stay at 1.
- You raise a rank with a Sigil: `/fray sigil <boon>`.

| Sigil | Raises | Price (Boonwright) |
|---|---|---|
| Lesser Sigil | rank 1 to 2 | 1,500 Embers |
| Greater Sigil | rank 2 to 3 | 4,000 Embers |
| Grand Sigil | rank 3 to 4 | 10,000 Embers |

Sigil Shards drop from Snarl caches (25%); 10 join into a Lesser Sigil. `/frays collection` shows your collection.

**Your boon plan.**
- `/fray boons` opens a window to set your boon order, one boon at a time.
- `/fray boons order <numbers or names>` sets the order; `list`, `clear` and `reset` manage it.
- `/fray boons aoe single sustain embers heat snarl` sets a type plan instead. `balanced` picks your best class boon,
  then a rare.

---

## 7. Your campfire: tiers, perks, looks, Ascension

**Tiers.** `/frays fire upgrade`. Tier N costs 500 x N Embers: 500, 1,000 ... 5,000, 27,500 for all 10. Each tier
gives a perk point and makes the flame 6% bigger.

**Perks.** `/frays perk <name>`. One free reset a week (`/frays perk reset`). Up to 11 points (10 tiers plus one from
the Naturalist achievement).
| Perk | Ranks | Per rank |
|---|---|---|
| Higher Flame | 5 | +1 Heat cap (up to 25) |
| Ember Hearth | 5 | +5% Embers |
| Snarl Lure | 3 | Snarl bar 5% faster |
| Bellows | 1 | about 10% more creatures an hour |
| Searing | 5 | +2% damage |
| Ash Guard | 5 | 2% less damage taken |

Presets spend new points for you (`/fray perks balanced|aoe|single|embers|off`). No perk speeds Heat gain.

**Looks.**
- **Name:** `/fray name <text>`, 3 to 24 letters. The fire shows as "Name - Place (Owner Epithet)".
- **Sparks:** `/fray sparks red|orange|gold|green|blue|purple` are free; `sky` (white) needs the Skyfire Sparks unlock.
- **Epithet:** `/fray epithet <name>` from the titles you have earned (section 18).
- **Kindred flames:** two fires in one Fray lit within 60 s of each other share a sparkling colour.

**Ascension.** `/frays ascend confirm`, while camping at Heat 20.
- Costs 5,000 x N Embers: 5k, 10k, 15k, 20k, 25k (75,000 in all). One a week, five at most.
- Resets your Heat rating to 0.
- Each one gives +1% damage and +1% Embers for good, a title and brighter Ascension sparks on your fire.
- The titles are I "the Kindled", II "the Emberborn", III "the Flamewarden", IV "the Pyrebound", V "the Ashen Sovereign".

---

## 8. Creatures, Gilded and the Ember Shrine

**Creatures.**
- One creature family a week for every Fray, drawn from the Threads species tables (Olthoi Larvae left out). Melee
  creatures are picked far more often than casters and archers.
- Built the way a Thread builds them, at your level (up to 410). Heat then makes them tougher through the curves above.
- No caster spells below Heat 17: below that every creature's spell book is empty and casters fight in melee.
- No Frays creature dispels or reflects.
- Defense is capped (200 at level 300, +1.2 per level), so every family can be hit.
- Creatures give no XP of their own and drop none of their gear; pay comes from the Frays.
- Brute points are 30% bigger than their kind (`brute_scale`), so you can tell a brute pack from a swarm pack at a glance.

**Gilded points.** Four per fire, two brute points and two swarm points, with a gold glow and "Gilded" in their names.
- They pay x3 Embers, x3 key bar, x3 Snarl bar and x2 loot (XP is the same) and +5 climb score each.
- A Gilded point rests six times as long as its pack took to die (at least 3.5 minutes) and glows 15 s before it
  returns.
- When a Gilded pack falls you are told when it returns ("The Gilded pack falls. It returns in 3 minutes 30 seconds.").
- Each week every Gilded creature carries one ward (by week): Fireward, Bladeward, Acidward, Spikeward, Frostward,
  Maceward or Stormward. It takes half damage from one type and 150% from its opposite.

**The Ember Shrine.** One per fire, only you see it. Walk within 3 m or use it for **Ember Fury: +10% damage** for
180 s, ready again 180 s after you took it. Using it while it is cold tells you how many seconds are left.

---

## 9. The Snarl and the Great Snarl

**The Snarl.** Your own mini boss of the week's family.
- **How it spawns:** a Snarl bar fills from the health you remove (Gilded x3). When it fills, the Snarl walks out of a
  spawn point. On solo camps there are two bars: brute packs fill the **Brute Snarl** and swarm packs fill the
  **Swarm Snarl**, 6 adds that share one Snarl's health (one of them, the Snarl Brood, holds the cache).
- **How often:** about 2, 3 and 4 an hour at Heat 0, 10 and 20 for a steady camper; a faster killer calls more.
  Snarl speed bonuses together add at most 50%.
- **How tough:** 12 normal creatures' health at low Heat, about 4.8 at Heat 20, with x1.1 damage. About 2 minutes a
  fight at Heat 15 to 20.
- **What it gives:** XP and Embers by its health, 1 Vault Key, a 5% chance of a Fray Effigy, and a cache of 10 tier 8
  items (best one attuned) with these extra rolls:

| Extra | Chance |
|---|---|
| Slayer Stone | 25% |
| Sigil Shard | 25% |
| Kindling | 10% |
| Diamond Scarab | 5% |
| 2 Trade Notes (250,000) | 15% |
| 25 Embers | 50% |
| Snarl Trophy | 1% |

**The Great Snarl.** One server-wide boss.
- **How it spawns:** every camp's kills fill one shared bar (300 + 750 per camper in the Fray). At least 4 hours
  between Great Snarls, so about 5 a day. The Fray is warned and it walks in 60 s later at the Vissidal Coast.
- `/fray great` pulls any fire holder straight to it. Every camp is held while it is up, and for 20 minutes after.
- It leaves after 15 minutes if not beaten; its bar keeps half its progress.
- **What it gives** (to everyone who struck it or fed 5% of its bar):
  - a cache of 3 tier 8 items (the top 3 feeders get one more);
  - **Emberblessed** for 30 minutes: +10% Embers, +5% damage, one Heat at once and a fire that does not cool for 2
    minutes;
  - a 5% chance of an augmentation gem token or a Stipend;
  - a portal back to town for 2 minutes.
- It counts toward Lucid Night and the weekly "Kill the Great Snarl" task.

---

## 10. Group camps

**The Sky Fray.** A floating plateau with 5 group fires.
- Four Spires need a fellowship of 3; Sky Hearth in the middle needs 5. Members must be online and on different
  connections. One group fire per fellowship.
- Each zone is a 30 m circle (or the area past a wisp line on the plateau arms).

**How a group camp works.**
- One shared Heat. Any member's death costs the group 2 Heat. Group and solo ratings are separate.
- For each member past the first: creature health +50%, +1 creature per pack, points refill 50% faster.
- Kill XP, Embers and the key bar go by each member's damage share; minute pay is per member. Nothing is split.
- A corpse belongs to its top damage dealer. One shared Snarl: its key goes to the top damage dealer.
- **Boons are a vote:** members who fought in the last 2 minutes vote with `/fp 1|2|3`. The vote closes after 20 s or
  when all have voted; a tie goes to the keeper's boon plan. Every member gets the boon (and it joins every member's
  collection).

**The perk of a group.**
- **Sky Seals**, for group camps only: 1 per group Snarl, 2 for a Great Snarl while holding a group camp, 5 when a
  group camp first reaches Heat 20 (once a day each), and 5 for the weekly task "Camp 2 hours in a group camp". About 4
  to 5 an hour in a group. They are spent at the Sky Steward (section 11).
- Group runs have their own boards: group Heat, group score and a group runs list naming the members.
- Pay is the same as solo. What seals buy adds about 10 to 15% on top of a solo hour (1 seal is worth about 100
  Embers), plus the looks and the boards.
- **Sky Banner:** a member raises it in a group camp: +10% Embers and +10% key bar for every member for 60 minutes. Banners do
  not stack.

**The Frays fellowship.** Lighting a solo fire puts you in "The Frays" (then "The Frays 2" when full). It shares chat,
the fellowship window and the Fray feed. XP and loot sharing are off.

**The group finder.** `/fray lfg join` or the Fellowship Steward.
- When 3 or more are waiting, each gets a yes/no with 60 s to answer.
- Enough yeses form a "Fray Group" fellowship and teleport it to a free Spire, or Sky Hearth with 5.
- The queue lasts 60 minutes.

---

## 11. Rewards and what they cost

**XP and luminance.**
- An hour's pay is set by your level (`xp_by_level`, `lum_by_level`) times the Heat bonus.
  - At Heat 0: about 1.6 billion XP an hour at level 310, 3.7 billion at 370 and 6.2 billion at 410+.
  - At Heat 0: 290k luminance at 310 and 1.39 million at 410+.
- 30% is paid per active minute (you fought in the last 60 s); 70% is paid per kill, by the creature's health.
- Per-kill pay has a cap an hour, so speed pays more but not without limit.
- A steady camper earns about 70%, 75% and 80% of a solo Threads hour at Heat 0, 10 and 20; the fastest builds top
  out near 90%. Never split, and no boon or perk adds XP.

**Embers** (bonded currency).
- Per kill by health (x3 for Gilded), growing 3% per Heat, plus 2 a minute by your fire. No weekly cap; past the
  hourly soft ceiling (see Corpse loot) a kill pays 40% of its Embers.
- Bank them with `/b d e`, take them out with `/b w e <amount>`. Shops, tiers and Ascension spend from your pack, then
  the bank.
- Embers cannot be traded between characters.

**Corpse loot.**
- Every Frays corpse rolls tier 8 loot (`loot_tier_by_level` 0:8); Heat 8 and 16 add quality on top.
- A standard creature drops about 15 magic, 6 ordinary and 5 mundane items; a swarm creature about 40% of that
  (about 5 magic), which is already more than a retail tier 8 mob (4 to 5 magic at most). More with Heat.
- A brute pack drops what a whole swarm pack does: the one or two brutes carry the full pack's share. A Gilded
  pack drops twice as much.
- One corpse holds at most 25 items (`loot_corpse_cap`), keeping its mix. What it can't hold goes onto the corpse
  owner's key bar instead: every 17 items past the cap make one Vault Key (`loot_overflow_per_key`, about an Ember
  Vault's worth). A big brute or Gilded corpse loses nothing, and a brute pack is worth a swarm pack however it splits.
- Embers and corpse loot share one soft ceiling an hour (`earn_knee`: 400 standard creatures' worth of health cleared; a
  swarm creature counts 0.4). Under it every kill pays in full; past it a kill pays 40% (`earn_past_knee`), so killing
  faster still pays but no build runs far ahead. The fastest builds tested clear about 356 an hour, so a normal player
  never reaches it. Up to 5 minutes of it banks for a burst (`earn_bank_minutes`).

**Vault Keys and the Ember Vault.**
- A key bar fills from Heat-weighted kills (Gilded x3). Past a soft knee it fills at a quarter rate.
- It also fills from corpse overflow (see Corpse loot), which grows with Heat as corpses grow.
- About 4.8, 9 and 14 keys an hour at Heat 0, 10 and 20 for a steady camper from kills, counting each Snarl's key,
  before corpse overflow.
- Keys also come from Snarls, tasks, the climb score, the weekly goal, Lucid Night and the Ember Trader.
- Keys you earn go onto your Vault Keyring automatically (it holds 1,000).
- The 12 Ember Vaults in the village each hold 10 tier 8 magic items, 5 to 15 Embers and a full salvage bag per fill.
  One use spends up to 10 keys for 10 fills.

**Reward items.**
| Item | What it does | How you get it | Price |
|---|---|---|---|
| Slayer Stone | gives a weapon or caster a random Fray-family slayer (x1.5) | Snarl 25%, weekly goal | 4,000 Embers |
| Reroll Stone | rerolls a slayer to another family (25% this week's) | Ember Trader | 300 Embers |
| Vault Key | opens an Ember Vault | key bar, Snarls, tasks, goal | 500 Embers |
| Kindling | 250 companion XP | Snarl 10%, weekly goal, Field Notes | not sold |
| Rebinding Ember | changes a companion's style | Boonwright | 2,500 Embers |
| Sigils | raise a boon rank (section 6) | Boonwright, Sigil Shards | 1,500 / 4,000 / 10,000 Embers |
| Fray Effigy | unlocks a family's pet form and records it in your Codex | Snarl 5% | not sold |
| Snarl Trophy | mansion trophy, recorded on the Monument | Snarl 1% | not sold |
| Sky Seal | Sky Steward currency | group Snarls, group Great Snarls, group Heat 20, the weekly group task | not sold |
| Ember Stamina Kit | a stamina kit that never runs out | Ember Trader | 10,000 Embers |
| Prismatic Dye Pot | custom dye (section 20) | Ember Trader | 2,000 Embers |
| 9 retail dye pots | retail dyes | Ember Trader | half their retail value |
| Campfire Hearthstone | back to your fire | free on first light | free |

**Sky Steward (Sky Seals).**
| Item | Seals |
|---|---|
| Sky Shard Bundle (5 Sigil Shards) | 8 |
| Sky Banner (group camp: +10% Embers and key bar for 60 minutes) | 15 |
| Sky Key Bundle (5 Vault Keys) | 25 |
| Sky Kindling Bundle (2 Kindling) | 30 |
| Skyfire Sparks (white sparks) | 25 |
| Sky Dye Charter (Sky dye colours) | 30 |
| Sky Fray Trophy | 40 |
| Skybound Epithet ("the Skybound") | 50 |

---

## 12. Weapons in the Frays

- **Slayers:** a Slayer Stone gives a slayer for a random Fray family; a Reroll Stone moves it to another, with a 25%
  chance of this week's family. Slayer damage works as normal, outside the Frays damage pool.
- **Slash and pierce by attack height:** on the Frays, a melee weapon that does both slash and pierce does **pierce on
  a low attack and slash on a medium one**. A high attack keeps the normal rule. This is a global setting
  (`sp_damage_by_attack_height`, on) that only takes effect on the Frays.
- **Damage pools:** Frays damage bonuses (boons, perks, Shrine, companions, Ascension, Emberblessed, Lucid Night) add
  into one pool capped at +50%. Reductions share one pool capped at 35%.

---

## 13. Weekly affixes

**How they work.** One affix a week for every Fray, in a fixed rotation of 22 that turns over at the weekly reset
(Tuesday 00:00 UTC). An affix only makes the week harder, never pays more. Number affixes work at every Heat; mechanic
affixes start at Heat 5.

**Number affixes (15).**
| Affix | Effect |
|---|---|
| Heavy Thread | +40% creature health |
| Brittle Dream | -30% creature health, +15% damage |
| Unspooled Swarm | packs +1 |
| Teeming | packs +2 |
| Tangled Kings | packs -1, Gilded x1.5 health |
| Frayed Masses | pack creatures x0.8 health, Gilded x1.4 health |
| Champions | Gilded x2 health |
| Savage | +20 damage rating |
| Precise | +20 crit rating |
| Stalwart | +20 damage resist rating |
| Swift | creatures run 25% faster |
| Enraged Snarls | Snarls x1.4 damage |
| Guarded Snarls | Snarls x2 health |
| Fortified | pack creatures +20% health, +30% damage |
| Tyrannical | Gilded and Snarls +30% health, +15% damage |

**Mechanic affixes (7, from Heat 5).**
| Affix | Effect |
|---|---|
| Raging | below 30% health, creatures hit 25% harder |
| Grievous | while you are under 60% health, a bleed stacks up to 5 |
| Venomous | each creature hit adds a poison stack (up to 10) |
| Vampiric | creatures heal 30% of the damage they deal |
| Mana Burn | creature hits burn mana equal to 15% of the damage |
| Linked | 30% of your damage on a creature spreads to others within 8 m (never kills them) |
| Quaking | every 20 s, 5% of max health to each camper |

Each affix is UCM-safe: nothing needs dodging or reacting.

---

## 14. The weekly layer

- **One family and one affix a week** for every Fray.
- **Lucid Night:** if the server slays 25 Great Snarls in a week, the last 24 hours before the reset give every camper
  +5% damage, +10% Embers, 2 more items in every Snarl cache and one Vault Key on their first kill.
- **Weekly Frays goal:** one shared bar (40,000 creatures' health across all camps). When it fills, every camper of
  the week gets 15 Vault Keys, a 10-item tier 8 cache, a Slayer Stone, a Kindling and three tasks' worth of XP and
  luminance. Anyone who camps later that week is paid too.
- **Climb score:** each run scores 10 x peak Heat squared, +1 a kill, +50 a Snarl, +5 a Gilded, time points for fast
  Heat steps, -25 a death. The week's best run pays:

| Tier | Score | Vault Keys |
|---|---|---|
| Bronze | 2,500 | 2 |
| Silver | 4,500 | 4 |
| Gold | 6,000 | 6 |

- **Score titles:** the top 3 of the weekly score board hold "the Ember Champion", "the Ember Vanguard" and "the Ember
  Valiant" through the next week.
- **Weekly prizes:** the top 3 on the weekly Heat, group Heat and Guardian boards each get a Fray Tapestry from the
  Fray-Warden.

---

## 15. Kill tasks, daily and weekly quests

Take tasks by talking to the task-givers in the village; they say everything in tells. Kills count only after you
take a task. Talk again to hand it in. `/frays tasks` shows the board, `/frays tasks drop <n>` drops one. Dailies
reset at 00:00 UTC, weeklies at the weekly reset.

**Every finished task also pays** 3% of a level in XP (20 to 100 million) and 25,000 luminance.

| Giver | Task | Pays |
|---|---|---|
| Huntmaster (daily) | kill 400 of the week's family | 200 Embers |
| Huntmaster (daily) | three named creatures of the family, 30 to 150 each | 200 Embers each |
| Huntmaster (daily) | kill 50 Gilded of the family | 200 Embers |
| Heat-Warden (daily) | reach Heat 10 / 15 / 20 in a camp | 1 / 2 / 3 Vault Keys |
| Snarl-Tracker / Heat-Warden (daily bounties, 3 of 4 a day) | remove 5,000,000 health; kill 3 Snarls; fight 60 min at Heat 12+; fight 90 min at Heat 8+ | 300 Embers each |
| Week-Warden (weekly) | kill 6,500 of the week's creatures, or kill 15 Snarls (one a week) | 1,500 Embers |
| Week-Warden (weekly) | Kill the Great Snarl (every week) | 1,500 Embers |
| Week-Warden (weekly) | Camp 2 hours in a group camp on the Sky Fray (every week) | 5 Sky Seals |

---

## 16. Pets and companions

- **Getting one:** a Fray Effigy (Snarl, 5%) unlocks that family's form. Use it to record it in your Codex.
- **`/fray pet <form>`** calls a harmless follower in that form (`/fray pet off` sends it away). It cannot fight or be
  fought and takes the pet slot.
- **Styles:** `/frays companion <form> warder|mender|striker`. The first pick is free and then locked; a Rebinding
  Ember (2,500 Embers) changes it. Only your active companion's style applies.

| Style | At level L | At level 10 |
|---|---|---|
| Warder | L% less damage taken | 10% |
| Mender | heals L/2% of your health every 10 s | 5% |
| Striker | +L% damage | 10% |

- **Levelling:** feed it Kindling (250 XP each). Level 10 is the most. Total Kindling needed: 1, 2, 4, 7, 10, 15, 20,
  26, 33, 40 for levels 1 to 10. Each form keeps its own level.
- **Where Kindling comes from:** Snarl caches (10%), the weekly goal (1) and the Field Notes achievement (3).

---

## 17. Journal, Codex, guide and pop-ups

- **Fray Journal:** a book from the Fray-Warden (or `/fray journal`) that rewrites itself each time you open it: a
  cover page (ratings, best Heat, Embers, fire tier, achievements), your companions and pets, then the Codex.
- **Codex** (`/fray codex [section]`): 6 sections, each Complete and Incomplete: families fought, forms, companions,
  boons taken, affixes met, and achievements.
- **`/fg` guide:** a menu and 6 pop-up pages: How, Heat, Snarls, Camps, Week, Progress.
- **Guide sign:** a 6-page book by the Fray-Warden.
- **Pop-up windows (8):** guide pages, the first-light Hearthstone note, the light-the-campfire yes/no, the boon offer,
  the boon order window, the boon collection, the group finder "join?" and "a group is ready" yes/nos.
- **Centre-screen lines:** Heat level-ups, the Snarl and Great Snarl, "Welcome to the Frays!", Emberblessed, a new
  Effigy, the Shrine buff, each achievement, Fray-wide news and group messages.
- **Server-wide messages:** Lucid Night progress and start, and "The Frays turn over" at the weekly reset.

---

## 18. Achievements, titles and leaderboards

**Achievements (9).**
| Achievement | Condition | Reward |
|---|---|---|
| First Light | reach Heat 5 | - |
| Kindler | reach Heat 10 | Epithet "the Kindler" |
| Ashwalker | reach Heat 20 | Epithet "the Ashen" |
| Snarlbane | slay 25 Snarls | Epithet "Snarlbane" |
| Field Notes | fight 10 creature families | 3 Kindling |
| Naturalist | fight 25 families | +1 perk point |
| Loremaster | fight 50 families | Epithet "the Loremaster" |
| The Collector | an Effigy of every family | Epithet "the Collector" |
| Inferno Run | a run score of 10,000 | Epithet "the Inferno" |

**Other titles:** the 5 Ascension titles, "the Skybound" (Sky Seals) and the weekly score titles.

**Leaderboards** (`/top fray <board> [week]`; most have a weekly version, staff are left off):
| Board | Shows |
|---|---|
| heat | best solo Heat |
| guardian | fastest Snarl kill |
| score | best run score |
| group | best group Heat |
| groupscore | best group run score |
| groupruns | each group run with its members |
| rating | Heat rating |
| grouprating | group Heat rating |
| ascension | Ascensions |
| combined | each Fray's combined Heat now and its best this week |

---

## 19. Snarl Monument and Trophy Wall

- **Snarl Monument:** a statue in the village ringed by 12 Snarl Stones. Each stone is named for one of the latest
  Snarl Trophy finders ("Name, Olthoi Snarl, 9 Nov"), and a new trophy takes over the oldest stone. A larger
  **Champion's Stone** in front names whoever has found the most Snarl Trophies (a tie stays with whoever got there
  first). Appraise the statue for the full list.
- **Snarl Trophy Wall:** a sign listing the latest 10 Snarl Trophies (date, finder, family).

---

## 20. Custom dye

- Buy a **Prismatic Dye Pot** (2,000 Embers; its icon has a rainbow background). `/dye <colour> [slot|all]` loads a colour, then use the pot on armour or
  clothing. Items have up to 8 colour slots.
- A pot holds 15 dyes and locks to its first colour. `/dye original [slot]` removes dye for free. `/dye list [group]`
  shows the colours, `/dye info` an item's slots.
- 69 colours in 11 groups. The Sky group (4 colours) needs the Sky Dye Charter.

---

## 21. Banked Embers and other quality of life

- `/b d e` banks Embers, `/b w e <amount>` takes them out (10,000 at most at once). `/b d` banks them with everything
  else.
- `/abilitymsgs` hides class ability buff chat lines.
- `/frays claim` collects anything waiting at the Fray-Warden.

---

## 22. Built in but switched off

- Rotating Shrine buffs (Ward, Stoke, Mending): off, so the Shrine is always Ember Fury.
- Diamond Scarabs and Pyreal Motes in the Vault; augmentation items from normal Snarls.
- A weekly Ember cap.
