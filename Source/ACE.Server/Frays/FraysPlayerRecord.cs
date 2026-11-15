using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// A character's Frays record, saved on the player as JSON in PropertyString 9024 (FraysState): campfire mode and
    /// limit, Heat rating, Embers and when Last Ember last fired. A missing or unreadable record is a fresh one.
    /// </summary>
    public sealed class FraysPlayerRecord
    {
        /// <summary>Climb by default (push, rating + 1, is one command away for an unattended character).</summary>
        [JsonPropertyName("m")] public string ModeText { get; set; } = "climb";
        [JsonPropertyName("l")] public int Limit { get; set; }

        /// <summary>The Heat to start at on arriving at a fire (never above the rating at the time).</summary>
        [JsonPropertyName("s")] public int StartHeat { get; set; }
        /// <summary>The player picked their start Heat with /frays start (else camps start a little under their rating).</summary>
        [JsonPropertyName("ssc")] public bool StartHeatChosen { get; set; }

        /// <summary>The Heat this player asks to start at (their choice, or a little under their rating).</summary>
        public int WantedStart(bool group) =>
            FraysRules.WantedStart(StartHeatChosen || StartHeat > 0, StartHeat, RatingFor(group), FraysSettings.Int(FraysSettings.HeatStartBelowRating));
        [JsonPropertyName("r")] public int Rating { get; set; }
        /// <summary>The group camps' own Heat rating: earned and used only at group camps.</summary>
        [JsonPropertyName("gr")] public int GroupRating { get; set; }

        /// <summary>The rating that counts at a camp: the group rating at a group camp, else the solo one.</summary>
        public int RatingFor(bool group) => group ? GroupRating : Rating;
        [JsonPropertyName("e")] public double Embers { get; set; }

        /// <summary>The active Pattern item (its guid), locked into a camp when the fire is lit or joined.</summary>
        [JsonPropertyName("ap")] public uint ActivePattern { get; set; }

        // weekly counters (reset when the week number changes)
        [JsonPropertyName("wk")] public int Week { get; set; } = int.MinValue;
        [JsonPropertyName("we")] public double WeekEmbers { get; set; }
        [JsonPropertyName("wb")] public int WeekBestHeat { get; set; }
        /// <summary>Best run score this week and ever (FraysScore.RunScore).</summary>
        [JsonPropertyName("wrs")] public long WeekBestRunScore { get; set; }
        [JsonPropertyName("brs")] public long BestRunScore { get; set; }
        /// <summary>The weekly climb reward tier already paid this week (0 none, 1 Bronze, 2 Silver, 3 Gold).</summary>
        [JsonPropertyName("wst")] public int WeekScoreTier { get; set; }
        /// <summary>The Codex (FraysCodex): "family:key", "rare:name", "boon:key", "affix:name", "effigy:key" entries.</summary>
        [JsonPropertyName("cdx")] public System.Collections.Generic.HashSet<string> Codex { get; set; } = new System.Collections.Generic.HashSet<string>();
        /// <summary>How many times each Codex entry happened: kills of a family, rare or affix elite, and boons taken. Same keys as <see cref="Codex"/>.</summary>
        [JsonPropertyName("cdk")] public System.Collections.Generic.Dictionary<string, int> CodexCounts { get; set; } = new System.Collections.Generic.Dictionary<string, int>();
        /// <summary>Achievements earned (FraysCodex.Achievements), the Epithets they gave, and the one shown.</summary>
        [JsonPropertyName("ach")] public System.Collections.Generic.HashSet<string> Achievements { get; set; } = new System.Collections.Generic.HashSet<string>();
        [JsonPropertyName("eps")] public System.Collections.Generic.List<string> Epithets { get; set; } = new System.Collections.Generic.List<string>();
        [JsonPropertyName("ep")] public string Epithet { get; set; } = "";
        [JsonPropertyName("rk")] public int RaresKilled { get; set; }
        /// <summary>Perk points from achievements, on top of one a campfire tier.</summary>
        [JsonPropertyName("bpp")] public int BonusPerkPoints { get; set; }
        /// <summary>The campfire's own name (/fray name) and spark colour (/fray sparks; empty = none).</summary>
        [JsonPropertyName("fn")] public string FireName { get; set; } = "";
        [JsonPropertyName("spk")] public string Sparks { get; set; } = "";
        /// <summary>Sky Seal unlocks (FraysSky): "sparks", "dye", "epithet".</summary>
        [JsonPropertyName("sky")] public System.Collections.Generic.List<string> SkyUnlocks { get; set; } = new System.Collections.Generic.List<string>();
        /// <summary>The day this character last took the group Heat Sky Seals (FraysSky.OnGroupHeat, once a day).</summary>
        [JsonPropertyName("skh")] public int SkyHeatDay { get; set; } = -1;
        [JsonPropertyName("ws")] public double WeekBestSnarlSeconds { get; set; }

        // all-time bests (leaderboards)
        [JsonPropertyName("bh")] public int BestHeat { get; set; }
        /// <summary>Group camps keep their own boards: best shared Heat and run score, ever and this week.</summary>
        [JsonPropertyName("gbh")] public int BestGroupHeat { get; set; }
        [JsonPropertyName("gwb")] public int WeekBestGroupHeat { get; set; }
        [JsonPropertyName("gbr")] public long BestGroupRunScore { get; set; }
        [JsonPropertyName("gwr")] public long WeekBestGroupRunScore { get; set; }
        [JsonPropertyName("bs")] public double BestSnarlSeconds { get; set; }
        [JsonPropertyName("sk")] public int SnarlKills { get; set; }

        /// <summary>The last camp report, ready to print.</summary>
        [JsonPropertyName("rp")] public string LastReport { get; set; } = "";

        // long-term systems (FraysLongTerm)
        [JsonPropertyName("cr")] public string CompanionRole { get; set; } = "";
        [JsonPropertyName("cx")] public double CompanionXp { get; set; }
        /// <summary>Effigy companions (FraysCompanions): form key to its locked style and own experience.</summary>
        [JsonPropertyName("cmp")] public System.Collections.Generic.Dictionary<string, FraysCompanionForm> Companions { get; set; } = new System.Collections.Generic.Dictionary<string, FraysCompanionForm>();
        /// <summary>The form key of the active companion (its bonus is the one that applies).</summary>
        [JsonPropertyName("cma")] public string ActiveCompanion { get; set; } = "";
        [JsonPropertyName("ft")] public int FireTier { get; set; }

        /// <summary>Campfire perks: key to rank (FraysPerks), one point a campfire tier.</summary>
        [JsonPropertyName("pk")] public System.Collections.Generic.Dictionary<string, int> Perks { get; set; } = new System.Collections.Generic.Dictionary<string, int>();
        /// <summary>The week the free perk reset was last used.</summary>
        [JsonPropertyName("prw")] public int PerkResetWeek { get; set; } = int.MinValue;
        /// <summary>The boon plan (FraysBoons): boon tags in the order the server picks by when the player does not (empty = balanced).</summary>
        [JsonPropertyName("bpl")] public System.Collections.Generic.List<string> BoonPlan { get; set; } = new System.Collections.Generic.List<string>();
        /// <summary>The boon order (/fray boons window): boon keys, best first. An auto-pick takes the highest-listed offered boon; none listed falls back to BoonPlan.</summary>
        [JsonPropertyName("bor")] public System.Collections.Generic.List<string> BoonOrder { get; set; } = new System.Collections.Generic.List<string>();
        /// <summary>The perk preset (FraysPerks.AutoSpend): spends new perk points by itself unless "off".</summary>
        [JsonPropertyName("ppr")] public string PerkPreset { get; set; } = "balanced";
        /// <summary>The day of the Heat chests paid (HeatChestMask bits: Heat 10, 15, 20).</summary>
        [JsonPropertyName("hcd")] public int HeatChestDay { get; set; } = int.MinValue;
        [JsonPropertyName("hcm")] public int HeatChestMask { get; set; }
        [JsonPropertyName("as")] public int Ascensions { get; set; }
        /// <summary>The Frays week of the last Ascension and how many were made in it (ascension_per_week).</summary>
        [JsonPropertyName("asw")] public int AscendWeek { get; set; } = int.MinValue;
        [JsonPropertyName("asn")] public int AscendsThisWeek { get; set; }
        [JsonPropertyName("bd")] public int BountyDay { get; set; }
        [JsonPropertyName("bw")] public int BountyWeek { get; set; } = int.MinValue;
        [JsonPropertyName("bg")] public System.Collections.Generic.List<string> BountyGoals { get; set; } = new System.Collections.Generic.List<string>();
        [JsonPropertyName("bp")] public System.Collections.Generic.Dictionary<string, double> BountyProgress { get; set; } = new System.Collections.Generic.Dictionary<string, double>();
        [JsonPropertyName("bc")] public System.Collections.Generic.List<string> BountyPaid { get; set; } = new System.Collections.Generic.List<string>();
        /// <summary>The daily and weekly tasks picked up at the Huntmaster (tasks count only once taken).</summary>
        [JsonPropertyName("btk")] public System.Collections.Generic.List<string> BountyTaken { get; set; } = new System.Collections.Generic.List<string>();
        [JsonPropertyName("cl")] public System.Collections.Generic.List<uint> Claims { get; set; } = new System.Collections.Generic.List<uint>();
        [JsonPropertyName("lr")] public double LastRecallAt { get; set; }
        /// <summary>The Campfire Hearthstone has been handed over once (with its popup); later ones replace a lost stone quietly.</summary>
        [JsonPropertyName("hsg")] public bool HearthstoneGiven { get; set; }
        [JsonPropertyName("rt")] public string RerollTarget { get; set; } = "";
        /// <summary>The week this player last took a Lucid Night cache (FraysLongTerm.GrantLucidCache).</summary>
        [JsonPropertyName("lcw")] public int LucidCacheWeek { get; set; } = int.MinValue;
        [JsonPropertyName("wgp")] public bool WeekGoalPaid { get; set; }
        /// <summary>The Fray-Warden has handed over the Fray Journal (the first talk).</summary>
        [JsonPropertyName("wjg")] public bool WardenJournalGiven { get; set; }

        // Daily hunts (FraysHunts): the day they are for, the ones taken, their progress, the ones done today.
        [JsonPropertyName("hud")] public int HuntDay { get; set; } = int.MinValue;
        [JsonPropertyName("hut")] public System.Collections.Generic.List<string> HuntTaken { get; set; } = new System.Collections.Generic.List<string>();
        [JsonPropertyName("hup")] public System.Collections.Generic.Dictionary<string, double> HuntProgress { get; set; } = new System.Collections.Generic.Dictionary<string, double>();
        [JsonPropertyName("hdn")] public System.Collections.Generic.List<string> HuntDone { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>
        /// Finished tasks waiting to be handed in to their task-giver (the way the Marae Lassel
        /// kill task NPCs work). Each carries its own reward, so a daily reset never loses one.
        /// </summary>
        [JsonPropertyName("trd")] public System.Collections.Generic.List<FraysTaskReady> TaskReady { get; set; } = new System.Collections.Generic.List<FraysTaskReady>();

        // The boon collection (FraysCollection): each collected boon's rank, raised by Sigils (missing = rank 1).
        [JsonPropertyName("bnr")] public System.Collections.Generic.Dictionary<string, int> BoonRanks { get; set; } = new System.Collections.Generic.Dictionary<string, int>();

        /// <summary>Clears the weekly counters when <paramref name="week"/> is a new week.</summary>
        public bool RollWeek(int week)
        {
            if (Week == week)
                return false;

            Week = week;
            WeekEmbers = 0;
            WeekBestHeat = 0;
            WeekBestGroupHeat = 0;
            WeekBestGroupRunScore = 0;
            WeekBestRunScore = 0;
            WeekScoreTier = 0;
            WeekBestSnarlSeconds = 0;
            WeekGoalPaid = false;
            return true;
        }

        /// <summary>The one Heat switch players see: stopped holds Heat where it is (no climbing, no cooling); otherwise it climbs. Older saved modes (push, limit, auto) read as climbing.</summary>
        [JsonIgnore]
        public bool HeatStopped => Mode == FraysMode.Hold;

        /// <summary>"stopped" or "climbing", for player text.</summary>
        [JsonIgnore]
        public string HeatText => HeatStopped ? "Heat stopped" : "Heat climbing";

        [JsonIgnore]
        public FraysMode Mode
        {
            get => FraysRules.TryParseMode(ModeText, out var m) ? m : FraysMode.Climb;
            set => ModeText = value.ToString().ToLowerInvariant();
        }

        public static FraysPlayerRecord Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new FraysPlayerRecord();

            try
            {
                return JsonSerializer.Deserialize<FraysPlayerRecord>(json) ?? new FraysPlayerRecord();
            }
            catch (JsonException)
            {
                return new FraysPlayerRecord();
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this);

        public static FraysPlayerRecord Load(Player player) => Parse(player?.GetProperty(PropertyString.FraysState));

        public void Save(Player player)
        {
            if (player == null)
                return;

            player.SetProperty(PropertyString.FraysState, ToJson());
        }
    }

    /// <summary>A finished task not yet handed in: who takes it, what it says, and what it pays.</summary>
    public sealed class FraysTaskReady
    {
        [JsonPropertyName("g")] public uint Giver { get; set; }
        [JsonPropertyName("t")] public string Text { get; set; } = "";
        [JsonPropertyName("e")] public int Embers { get; set; }
        [JsonPropertyName("k")] public int Keys { get; set; }
        [JsonPropertyName("s")] public int Seals { get; set; }
    }
}
