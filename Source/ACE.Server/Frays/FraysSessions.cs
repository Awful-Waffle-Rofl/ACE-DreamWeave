using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using log4net;

using ACE.Common;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// What one camper did with their time, second by second, on the server: fighting, healing or
    /// buffing, looting, dead, walking, or standing idle. Plus damage dealt, the Heat held, and a snapshot of the
    /// build, so camps can be compared by build.
    /// </summary>
    public sealed class FraysPlayLog
    {
        public readonly Dictionary<string, double> Seconds = new Dictionary<string, double>();
        public readonly Dictionary<int, double> HeatSeconds = new Dictionary<int, double>();
        public double DamageDealt;
        /// <summary>The fighter's own damage by the weapon in hand when it landed: the snapshot names the top one, not whatever is in hand at the snapshot (a buffing wand).</summary>
        public readonly Dictionary<string, double> DamageByWeapon = new Dictionary<string, double>();
        public double LastDealtAt, LastHurtAt;
        public double LastX, LastY, LastZ;
        public uint LastCell;
        public bool HavePosition;
        public double NextSnapshotAt;
        public Dictionary<string, object> Build;
        public readonly HashSet<string> Buffs = new HashSet<string>();

        // Log version 2: where the damage came from, what was cast, and the pulls
        /// <summary>Damage dealt by source, by kind (FraysHitKinds: melee, missile, war, void, pets, procs: acid...).</summary>
        public readonly Dictionary<string, double> DamageBySource = new Dictionary<string, double>();
        /// <summary>Spells cast (each finished cast), by name.</summary>
        public readonly Dictionary<string, int> Casts = new Dictionary<string, int>();
        /// <summary>Fights: runs of fighting seconds with more than PullGapSeconds between them.</summary>
        public int Pulls;
        public double LastCombatSecondAt = double.MinValue;

        // Log version 3: where the damage went, and the XP. These split live damage between creatures that died and ones that did not
        // (reset, leashed, killed by someone else), and show heal-back (damage on a kill over its health).
        /// <summary>This camper's damage (pets counted) on creatures that died, from their damage history.</summary>
        public double DamageOnKills;
        /// <summary>Health of the creatures that died, times this camper's share of the damage on each.</summary>
        public double HealthKilled;
        /// <summary>Kills this camper did damage on but someone else got (top damage).</summary>
        public int AssistKills;
        /// <summary>XP paid per kill (the rest of XpPaid is per active minute).</summary>
        public long XpFromKills;

        // Log version 4, to see how much the Snarls slow the XP rate. The Snarls: how many
        // came, how long each took, how the time went while one was up, and what they paid.
        /// <summary>Snarls spawned, by kind (brute, swarm; snarl with one Snarl).</summary>
        public readonly Dictionary<string, int> SnarlsSpawned = new Dictionary<string, int>();
        /// <summary>Each Snarl killed: its kind and the seconds from its spawn to its kill (the last add's, for a swarm wave).</summary>
        public readonly List<(string Kind, double Seconds)> SnarlKills = new List<(string, double)>();
        /// <summary>Tracked seconds while a Snarl was up, by the same kinds as Seconds (combat, walking, idle...).</summary>
        public readonly Dictionary<string, double> SnarlUpSeconds = new Dictionary<string, double>();
        /// <summary>The per-kill XP the Snarls paid (a swarm wave: every add).</summary>
        public long XpFromSnarls;

        // How far the cleaves reach.
        /// <summary>The camper's own melee hits on the creature struck, and the extra hits a cleave landed on others.</summary>
        public int MeleeStrikes, CleaveHits;

        /// <summary>
        /// The camper's bow shots (one a volley).
        /// </summary>
        public int MissileShots;
    }

    /// <summary>One Snarl kill in a session line (log version 4).</summary>
    public sealed class FraysSnarlKill
    {
        public string Kind { get; set; }
        public double Seconds { get; set; }
    }

    /// <summary>One finished camp, as frays_session_log keeps it (one JSON line each).</summary>
    public sealed class FraysSessionRecord
    {
        public uint CharacterGuid { get; set; }
        public string Name { get; set; }
        public string Fire { get; set; }
        public double StartedAt { get; set; }
        public double EndedAt { get; set; }
        public double Minutes { get; set; }
        public double TrackedSeconds { get; set; }
        public int HeatStart { get; set; }
        public int HeatPeak { get; set; }
        public int HeatTypical { get; set; }
        public Dictionary<string, double> HeatSeconds { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public double DamageDealt { get; set; }
        public Dictionary<string, double> Seconds { get; set; }
        public double KillsPerHour { get; set; }
        public double DpsInCombat { get; set; }
        /// <summary>Embers this camp paid (kills, camping, the fire; for sizing the Ascension cost).</summary>
        public double EmbersEarned { get; set; }
        public double EmbersPerHour { get; set; }
        public Dictionary<string, object> Build { get; set; }
        public List<string> Buffs { get; set; }
        public string Reason { get; set; }

        // ---- log version 2; missing (null) on older lines ----

        /// <summary>The log version, 2 or more when the fields below are present; missing on older lines.</summary>
        public int? LogVersion { get; set; }
        /// <summary>Damage dealt by source, by kind (FraysHitKinds: melee, missile, war, void, pets, procs: acid...).</summary>
        public Dictionary<string, double> DamageBySource { get; set; }
        /// <summary>Finished casts by spell name.</summary>
        public Dictionary<string, int> CastsBySpell { get; set; }
        /// <summary>Fights (runs of fighting with more than 6 s between them), and the time each took.</summary>
        public int? Pulls { get; set; }
        /// <summary>The camper's own melee hits on the creature struck, and a cleave's extra hits (null on older lines).</summary>
        public int? MeleeStrikes { get; set; }
        public int? CleaveHits { get; set; }
        /// <summary>The camper's bow shots, one a volley (null on older lines), and shots a fighting second.</summary>
        public int? MissileShots { get; set; }
        public double? ShotsPerCombatSecond { get; set; }
        public double? PullsPerHour { get; set; }
        /// <summary>Tracked seconds a pull (fighting and everything between).</summary>
        public double? SecondsPerPull { get; set; }
        /// <summary>Fighting seconds a pull.</summary>
        public double? FightSecondsPerPull { get; set; }
        // ---- log version 3; missing (null) on older lines ----

        /// <summary>XP paid this camp (per kill and per active minute), and an hour of tracked time.</summary>
        public long? XpEarned { get; set; }
        public double? XpPerHour { get; set; }
        /// <summary>The per-kill part of XpEarned.</summary>
        public long? XpFromKills { get; set; }
        public long? LumEarned { get; set; }
        /// <summary>Health removed on kills, in Heat 0 standard creatures (work), by damage share.</summary>
        public double? WorkKilled { get; set; }
        /// <summary>Damage dealt on creatures that died (the rest of DamageDealt went on ones that did not).</summary>
        public double? DamageOnKills { get; set; }
        /// <summary>Health of the creatures that died, by damage share: DamageOnKills over it above 1 is heal-back.</summary>
        public double? HealthKilled { get; set; }
        /// <summary>Kills this camper did damage on that someone else got.</summary>
        public int? AssistKills { get; set; }

        // ---- log version 4; missing (null) on older lines ----

        /// <summary>Snarls spawned, by kind (brute, swarm; snarl with one Snarl).</summary>
        public Dictionary<string, int> SnarlsSpawned { get; set; }
        /// <summary>Each Snarl killed: kind and seconds from spawn to kill.</summary>
        public List<FraysSnarlKill> SnarlKills { get; set; }
        /// <summary>Average seconds from spawn to kill, by kind.</summary>
        public Dictionary<string, double> SnarlSecondsByKind { get; set; }
        /// <summary>Tracked seconds while a Snarl was up, by time kind (combat, walking, idle...).</summary>
        public Dictionary<string, double> SnarlUpSeconds { get; set; }
        /// <summary>Per-kill XP the Snarls paid.</summary>
        public long? XpFromSnarls { get; set; }

        public double Share(string kind) => TrackedSeconds > 0 && Seconds != null ? Seconds.GetValueOrDefault(kind) / TrackedSeconds : 0;
    }

    public static class FraysSessions
    {

        /// <summary>The weapon a camper fights with: melee, else missile, else wand.</summary>
        private static WorldObject CampWeapon(Player p) =>
            (WorldObject)p.GetEquippedMeleeWeapon() ?? (WorldObject)p.GetEquippedMissileWeapon() ?? p.GetEquippedWand();

        /// <summary>The class a camper has spent the most class ability points in (None: no ranks).</summary>
        private static ClassAbilityClass CampClass(Player p)
        {
            var spent = new Dictionary<ClassAbilityClass, int>();

            foreach (var def in ClassAbilityRegistry.Abilities.Values)
            {
                var rank = p.GetClassAbilityRank(def.Id);
                if (rank > 0 && def.AbilityClass != ClassAbilityClass.None)
                    spent[def.AbilityClass] = spent.GetValueOrDefault(def.AbilityClass) + def.CumulativeCost(rank);
            }

            return spent.Count == 0 ? ClassAbilityClass.None : spent.OrderByDescending(kv => kv.Value).First().Key;
        }
        private static readonly ILog log = LogManager.GetLogger(typeof(FraysSessions));

        public const string Combat = "combat", Support = "healing_buffing", Loot = "looting", Dead = "dead", Walk = "walking", Idle = "idle";
        public static readonly string[] Kinds = { Combat, Support, Loot, Dead, Walk, Idle };

        /// <summary>A hit dealt or taken this recently still counts as fighting.</summary>
        public const double CombatWindowSeconds = 3;

        /// <summary>A fighting second more than this long after the last one starts a new pull.</summary>
        public const double PullGapSeconds = 6;

        /// <summary>Most names kept per camp in the source and cast tables.</summary>
        public const int MaxNames = 100;

        /// <summary>The camp log's version: 2 adds damage by source, casts by spell and pulls; 3 adds XP, work killed and where the damage went.</summary>
        public const int LogVersionNow = 4;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // ---- the second-by-second log ----

        /// <summary>
        /// Which kind of second this was, by priority: dead, casting a non-harmful spell (healing or buffing), fighting
        /// (dealt or took damage in the last 3 seconds), a corpse open, moving, or idle. Pure, for tests.
        /// </summary>
        public static string Classify(bool dead, bool castingSupport, double sinceDealt, double sinceHurt, bool looting, bool moved)
        {
            if (dead) return Dead;
            if (castingSupport) return Support;
            if (sinceDealt <= CombatWindowSeconds || sinceHurt <= CombatWindowSeconds) return Combat;
            if (looting) return Loot;
            if (moved) return Walk;
            return Idle;
        }

        /// <summary>The camp tick (once a second, player present): counts this second, and refreshes the build snapshot every 5 minutes.</summary>
        public static void Tick(FraysCamper c, Player p, double now)
        {
            try
            {
                var play = c.Play;

                var pos = p.Location;
                var moved = false;
                if (pos != null)
                {
                    if (play.HavePosition)
                    {
                        var dx = pos.PositionX - play.LastX;
                        var dy = pos.PositionY - play.LastY;
                        var dz = pos.PositionZ - play.LastZ;
                        moved = pos.Cell != play.LastCell || dx * dx + dy * dy + dz * dz > 1.0;
                    }

                    play.LastX = pos.PositionX; play.LastY = pos.PositionY; play.LastZ = pos.PositionZ;
                    play.LastCell = pos.Cell;
                    play.HavePosition = true;
                }

                var spell = p.MagicState?.IsCasting == true ? p.MagicState.CastSpellParams?.Spell : null;
                var looting = p.LastOpenedContainerId != ACE.Entity.ObjectGuid.Invalid && p.CurrentLandblock?.GetObject(p.LastOpenedContainerId) is Corpse;

                var kind = Classify(p.IsDead, spell != null && !spell.IsHarmful, now - play.LastDealtAt, now - play.LastHurtAt, looting, moved);
                play.Seconds[kind] = play.Seconds.GetValueOrDefault(kind) + 1;
                if (c.SnarlAlive != null || c.SnarlAdds.ToArray().Any(a => a != null && !a.IsDestroyed && !a.IsDead))
                    play.SnarlUpSeconds[kind] = play.SnarlUpSeconds.GetValueOrDefault(kind) + 1;
                CountPull(play, kind, now);
                play.HeatSeconds[c.Level] = play.HeatSeconds.GetValueOrDefault(c.Level) + 1;

                if (now >= play.NextSnapshotAt)
                {
                    play.NextSnapshotAt = now + 300;
                    play.Build = Snapshot(p);
                    // the weapon that did the fighting, not the one in hand this second (a camper may rebuff with a wand)
                    if (play.DamageByWeapon.Count > 0)
                        play.Build["weapon"] = play.DamageByWeapon.OrderByDescending(kv => kv.Value).First().Key;
                    // the Ember Boons held: a Blood Mage's camps bought Hecatomb's Reach and Crimson Splash, a
                    // third of their life damage
                    if (c.Boons.Count > 0)
                        play.Build["boons"] = string.Join(",", c.Boons);

                    foreach (var e in ACE.Entity.Models.PropertiesEnchantmentRegistryExtensions.GetEnchantmentsTopLayer(p.Biota.PropertiesEnchantmentRegistry, p.BiotaDatabaseLock, SpellSet.SetSpells))
                    {
                        if (e.SpellId <= 0 || e.SpellId >= ACE.Server.WorldObjects.Managers.EnchantmentManager.SpellCategory_Cooldown)
                            continue;

                        var s = new Spell((uint)e.SpellId);
                        if (!s.NotFound && play.Buffs.Count < 200)
                            play.Buffs.Add(s.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS SESSION] tick threw", ex);
            }
        }

        /// <summary>A fighting second after a gap of more than PullGapSeconds starts a pull. Pure, for tests.</summary>
        public static void CountPull(FraysPlayLog play, string kind, double now)
        {
            if (kind != Combat)
                return;

            if (now - play.LastCombatSecondAt > PullGapSeconds)
                play.Pulls++;

            play.LastCombatSecondAt = now;
        }

        /// <summary>Adds to a by-name table, at most MaxNames names (the rest under "other").</summary>
        public static void Add<T>(Dictionary<string, T> into, string name, T amount, Func<T, T, T> plus)
        {
            name = string.IsNullOrWhiteSpace(name) ? "other" : name;
            if (!into.ContainsKey(name) && into.Count >= MaxNames)
                name = "other";
            into[name] = into.TryGetValue(name, out var had) ? plus(had, amount) : amount;
        }

        /// <summary>
        /// From Monster_Combat.TakeDamage and SpellProjectile: a hit on a fire creature, by source (FraysHitKinds). Credited to the camper who dealt it or owns the pet. Two field reads for any other hit.
        /// </summary>
        public static void OnSourcedHit(Creature victim, WorldObject source, uint amount, string kind)
        {
            if (amount == 0 || source == null || !(victim?.FraysTag?.Fire is FraysFire fire))
                return;

            try
            {
                var player = source as Player ?? (source as CombatPet)?.P_PetOwner ?? ((source as SpellProjectile)?.ProjectileSource as Player);
                if (player != null)
                    fire.NoteSource(player, kind, amount, cleave: kind == "melee" && source == player ? player.ResolvingMeleeCleaveHits : null);
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS SESSION] source note threw", ex);
            }
        }

        /// <summary>Player_Missile.LaunchMissile: a camper fired a shot (one a volley). One field read for anyone not camping.</summary>
        public static void OnShot(Player player)
        {
            if (!(player?.FraysMods?.Fire is FraysFire fire))
                return;

            try
            {
                fire.NoteShot(player);
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS SESSION] shot note threw", ex);
            }
        }

        /// <summary>Player.DoCastSpell_Inner: a camper finished casting a spell of their own (not an item's). One field read for anyone not camping.</summary>
        public static void OnCast(Player player, Spell spell)
        {
            if (spell == null || !(player?.FraysMods?.Fire is FraysFire fire))
                return;

            try
            {
                fire.NoteCast(player, spell.Name);
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS SESSION] cast note threw", ex);
            }
        }

        /// <summary>DamageHistory.Add: damage between a camper (or their pet) and a fire creature. Two field reads for any other hit.</summary>
        public static void OnDamage(Creature victim, WorldObject attacker, uint amount)
        {
            if (amount == 0 || victim == null || attacker == null)
                return;

            try
            {
                if (victim.FraysTag?.Fire is FraysFire fire)
                {
                    var player = attacker as Player ?? (attacker as CombatPet)?.P_PetOwner;
                    if (player != null)
                        fire.NoteDamage(player, amount, dealt: true, weapon: attacker is Player ? CampWeapon(player)?.Name : null);
                }
                else if (victim is Player hurt && attacker is Creature monster && monster.FraysTag?.Fire is FraysFire f2)
                    f2.NoteDamage(hurt, amount, dealt: false);
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS SESSION] damage note threw", ex);
            }
        }

        /// <summary>The build, as it was during the camp: level, class, CAP, class ability ranks, weapon and worn gear.</summary>
        public static Dictionary<string, object> Snapshot(Player p)
        {
            var ranks = ClassAbilityRegistry.Abilities.Values
                .Select(d => (d, r: p.GetClassAbilityRank(d.Id)))
                .Where(x => x.r > 0)
                .ToDictionary(x => x.d.DisplayName, x => (object)x.r);

            return new Dictionary<string, object>
            {
                ["level"] = p.Level ?? 0,
                ["class"] = CampClass(p).ToString(),
                ["cap_spent"] = p.ClassAbilityPointsSpentOnOwnedRanks(),
                ["cap_earned"] = p.TotalClassAbilityPointsEarned,
                ["cap_available"] = p.AvailableClassAbilityPoints,
                ["class_abilities"] = ranks,
                ["weapon"] = CampWeapon(p)?.Name,
                ["gear"] = p.EquippedObjects.Values.Select(i => i.Name).OrderBy(n => n).ToList(),
            };
        }

        // ---- the log file ----

        public static string LogPath => FraysSettings.Text(FraysSettings.SessionLog);

        /// <summary>The camp is over: one line in frays_session_log. Short camps (under a minute tracked) are skipped.</summary>
        public static FraysSessionRecord Save(FraysFire fire, FraysCamper c, double now, string reason)
        {
            try
            {
                var record = Record(fire.Def.Id, c, now, reason);

                if (record.TrackedSeconds < 60 || string.IsNullOrWhiteSpace(LogPath))
                    return record;

                var dir = Path.GetDirectoryName(Path.GetFullPath(LogPath));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(LogPath, JsonSerializer.Serialize(record, Json) + Environment.NewLine);

                return record;
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS SESSION] could not save a camp", ex);
                return null;
            }
        }

        /// <summary>A camper's record as it stands now. Pure apart from reading the camper.</summary>
        public static FraysSessionRecord Record(string fireId, FraysCamper c, double now, string reason)
        {
            var play = c.Play;
            var tracked = play.Seconds.Values.Sum();
            var combat = play.Seconds.GetValueOrDefault(Combat);

            return new FraysSessionRecord
            {
                CharacterGuid = c.Guid,
                Name = c.Name,
                Fire = fireId,
                StartedAt = c.StartedAt,
                EndedAt = now,
                Minutes = Math.Round((now - c.StartedAt) / 60, 1),
                TrackedSeconds = tracked,
                HeatStart = c.StartLevel,
                HeatPeak = c.PeakLevel,
                HeatTypical = play.HeatSeconds.Count > 0 ? play.HeatSeconds.OrderByDescending(kv => kv.Value).First().Key : c.Level,
                HeatSeconds = play.HeatSeconds.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                Kills = c.Kills,
                Deaths = c.Deaths,
                DamageDealt = Math.Round(play.DamageDealt),
                Seconds = Kinds.ToDictionary(k => k, k => play.Seconds.GetValueOrDefault(k)),
                KillsPerHour = tracked > 0 ? Math.Round(c.Kills / (tracked / 3600), 1) : 0,
                DpsInCombat = combat > 0 ? Math.Round(play.DamageDealt / combat, 1) : 0,
                EmbersEarned = Math.Round(c.EmbersEarned),
                EmbersPerHour = tracked > 0 ? Math.Round(c.EmbersEarned / (tracked / 3600)) : 0,
                Build = play.Build,
                Buffs = play.Buffs.OrderBy(b => b).ToList(),
                Reason = reason,
                LogVersion = LogVersionNow,
                DamageBySource = play.DamageBySource.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value)),
                CastsBySpell = play.Casts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
                Pulls = play.Pulls,
                MeleeStrikes = play.MeleeStrikes,
                CleaveHits = play.CleaveHits,
                MissileShots = play.MissileShots > 0 ? play.MissileShots : null,
                ShotsPerCombatSecond = play.MissileShots > 0 && combat > 0 ? Math.Round(play.MissileShots / combat, 3) : null,
                PullsPerHour = tracked > 0 ? Math.Round(play.Pulls / (tracked / 3600), 1) : 0,
                SecondsPerPull = play.Pulls > 0 ? Math.Round(tracked / play.Pulls, 1) : null,
                FightSecondsPerPull = play.Pulls > 0 ? Math.Round(combat / play.Pulls, 1) : null,
                XpEarned = c.XpPaid,
                XpPerHour = tracked > 0 ? Math.Round(c.XpPaid / (tracked / 3600)) : 0,
                XpFromKills = play.XpFromKills,
                LumEarned = c.LumPaid,
                WorkKilled = Math.Round(c.HealthRemoved, 1),
                DamageOnKills = Math.Round(play.DamageOnKills),
                HealthKilled = Math.Round(play.HealthKilled),
                AssistKills = play.AssistKills,
                SnarlsSpawned = new Dictionary<string, int>(play.SnarlsSpawned),
                SnarlKills = play.SnarlKills.Select(k => new FraysSnarlKill { Kind = k.Kind, Seconds = Math.Round(k.Seconds, 1) }).ToList(),
                SnarlSecondsByKind = play.SnarlKills.GroupBy(k => k.Kind).ToDictionary(g => g.Key, g => Math.Round(g.Average(k => k.Seconds), 1)),
                SnarlUpSeconds = Kinds.ToDictionary(k => k, k => play.SnarlUpSeconds.GetValueOrDefault(k)),
                XpFromSnarls = play.XpFromSnarls,
            };
        }

        /// <summary>Every saved camp, oldest first. Bad lines are skipped.</summary>
        public static List<FraysSessionRecord> Load()
        {
            var list = new List<FraysSessionRecord>();

            try
            {
                if (string.IsNullOrWhiteSpace(LogPath) || !File.Exists(LogPath))
                    return list;

                foreach (var line in File.ReadLines(LogPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var r = JsonSerializer.Deserialize<FraysSessionRecord>(line, Json);
                        if (r != null)
                            list.Add(r);
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS SESSION] could not read the session log", ex);
            }

            return list;
        }

        /// <summary>
        /// The downtime question (does low Heat earn more because less time goes to healing?): saved camps grouped by
        /// their typical Heat, with each kind's share of the time, kills an hour and deaths an hour.
        /// </summary>
        public static List<string> DowntimeByHeat(IEnumerable<FraysSessionRecord> sessions, int minMinutes)
        {
            var lines = new List<string>();
            var groups = sessions.Where(s => s.TrackedSeconds >= minMinutes * 60)
                .GroupBy(s => s.HeatTypical < 5 ? "0-4" : s.HeatTypical < 10 ? "5-9" : s.HeatTypical < 15 ? "10-14" : s.HeatTypical < 20 ? "15-19" : "20+")
                .OrderBy(g => g.Min(s => s.HeatTypical));

            foreach (var g in groups)
            {
                var hours = g.Sum(s => s.TrackedSeconds) / 3600;
                string Pct(string k) => $"{g.Sum(s => s.Seconds?.GetValueOrDefault(k) ?? 0) / Math.Max(1, g.Sum(s => s.TrackedSeconds)) * 100:0}%";

                lines.Add($"Heat {g.Key}: {g.Count()} camps, {hours:0.0} h. Fighting {Pct(Combat)}, healing/buffing {Pct(Support)}, looting {Pct(Loot)}, " +
                    $"walking {Pct(Walk)}, idle {Pct(Idle)}, dead {Pct(Dead)}. Kills/hr {g.Sum(s => s.Kills) / Math.Max(1e-6, hours):0}, deaths/hr {g.Sum(s => s.Deaths) / Math.Max(1e-6, hours):0.0}.");
            }

            return lines;
        }
    }
}
