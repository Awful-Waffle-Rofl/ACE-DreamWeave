using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// Sky Seals (group rates stay the same as solo, with no rate bonus. Instead each group Snarl
    /// gives every member a Sky Seal, a bonded currency that buys cosmetics only: fire looks, dye colours, titles and a
    /// Sky Fray trophy). The Sky Steward (dreamweave-content 2000200) sells the unlock tokens; using one unlocks its look
    /// for good. It also sells Vault Keys, Kindling, Sigil Shards and the Sky Banner, priced so a group's
    /// seals add about 10 to 15% on top of a solo hour, and seals come from more group feats (FraysSettings sky_*).
    /// </summary>
    public static class FraysSky
    {
        public const uint Seal = 2000195;
        public const uint Trophy = 2000196;
        public const uint SparksToken = 2000197;
        public const uint DyeToken = 2000198;
        public const uint EpithetToken = 2000199;
        public const uint Steward = 2000200;

        // The Sky Steward also sells Vault Keys x5, Kindling x2, Sigil Shards x5 and a Sky Banner,
        // priced so a group's seals add about 10 to 15% on top of a solo hour (1 seal is worth about 100 Embers).
        public const uint KeyBundle = 2000203;
        public const uint KindlingBundle = 2000204;
        public const uint ShardBundle = 2000205;
        public const uint Banner = 2000206;

        /// <summary>Unlock keys kept on the record (FraysPlayerRecord.SkyUnlocks).</summary>
        public const string SparksUnlock = "sparks";
        public const string DyeUnlock = "dye";
        public const string EpithetUnlock = "epithet";

        /// <summary>The spark colour the Skyfire Sparks token unlocks (/fray sparks sky), and the Sky dye group.</summary>
        public const string SkySpark = "sky";
        public const string DyeGroup = "Sky";
        public const string Epithet = "the Skybound";

        /// <summary>Pure: the unlock a token gives, or null for any other item.</summary>
        public static string UnlockOf(uint wcid) => wcid switch
        {
            SparksToken => SparksUnlock,
            DyeToken => DyeUnlock,
            EpithetToken => EpithetUnlock,
            _ => null,
        };

        public static bool Has(FraysPlayerRecord rec, string unlock) => rec?.SkyUnlocks != null && rec.SkyUnlocks.Contains(unlock);

        /// <summary>Pure: adds an unlock (the Skybound Epithet also goes in the Epithet list). False when already held.</summary>
        public static bool Unlock(FraysPlayerRecord rec, string unlock)
        {
            rec.SkyUnlocks ??= new List<string>();

            if (rec.SkyUnlocks.Contains(unlock))
                return false;

            rec.SkyUnlocks.Add(unlock);

            if (unlock == EpithetUnlock)
            {
                rec.Epithets ??= new List<string>();
                if (!rec.Epithets.Contains(Epithet))
                    rec.Epithets.Add(Epithet);
            }

            return true;
        }

        /// <summary>Pure: may this record pick the spark colour? Every colour but sky is free.</summary>
        public static bool SparkAllowed(FraysPlayerRecord rec, string colour) => colour != SkySpark || Has(rec, SparksUnlock);

        /// <summary>Pure: the spark colours this record may pick, for the /fray sparks help line.</summary>
        public static IEnumerable<string> SparkChoices(FraysPlayerRecord rec) => FraysCodex.SparkColours.Keys.Where(k => SparkAllowed(rec, k));

        /// <summary>Pure: may a dye of this colour group be loaded? Every group but Sky is free.</summary>
        public static bool DyeAllowed(FraysPlayerRecord rec, string group) => !string.Equals(group, DyeGroup, System.StringComparison.OrdinalIgnoreCase) || Has(rec, DyeUnlock);

        public static bool DyeAllowed(Player player, string group) =>
            !string.Equals(group, DyeGroup, System.StringComparison.OrdinalIgnoreCase) || player != null && Has(FraysManager.Record(player), DyeUnlock);

        /// <summary>Pure: Seals each member gets when a group camp's Snarl falls (0 on a solo camp).</summary>
        public static int SealsFor(bool groupCamp, int perSnarl) => groupCamp ? System.Math.Max(0, perSnarl) : 0;

        /// <summary>A group camp's Snarl fell: every member holding the camp gets Sky Seals.</summary>
        public static void OnGroupSnarl(IEnumerable<Player> members)
        {
            var n = SealsFor(true, FraysSettings.Int(FraysSettings.SkySealsPerSnarl));

            if (n <= 0)
                return;

            foreach (var p in members.Where(p => p != null).Distinct())
                FraysLongTerm.Give(p, Seal, n, $"The group Snarl falls: {(n == 1 ? "a Sky Seal" : $"{n} Sky Seals")} (spend them with the Sky Steward on the Sky Fray plateau).");
        }

        public static string UnlockText(string unlock) => unlock switch
        {
            SparksUnlock => "The white sky sparks are yours for good: /fray sparks sky.",
            DyeUnlock => "The Sky dye colours are yours for good: /dye list sky shows them.",
            EpithetUnlock => $"The Epithet {Epithet} is yours for good: /fray epithet {Epithet} shows it.",
            _ => "",
        };

        // ---- more ways to earn Sky Seals ---------------------------------------------------------------------------

        /// <summary>Gives <paramref name="n"/> Sky Seals with a line saying why.</summary>
        public static void GiveSeals(Player p, int n, string why)
        {
            if (p == null || n <= 0)
                return;

            FraysLongTerm.Give(p, Seal, n, $"{why}: {(n == 1 ? "a Sky Seal" : $"{n} Sky Seals")}.");
        }

        /// <summary>The Great Snarl paid a camper: Sky Seals when they hold a group camp.</summary>
        public static void OnGreatSnarl(Player p)
        {
            if (FraysManager.FireOf(p)?.Def.Group == true)
                GiveSeals(p, FraysSettings.Int(FraysSettings.SkySealsGreatSnarl), "The Great Snarl falls while you hold a group camp");
        }

        /// <summary>Pure: does a group climb from <paramref name="from"/> to <paramref name="to"/> cross the seal Heat for the first time this camp?</summary>
        public static bool HeatSealsDue(bool group, int peak, int from, int to, int sealHeat) =>
            group && sealHeat > 0 && Math.Max(peak, from) < sealHeat && to >= sealHeat;

        /// <summary>Pure: may this record take today's Heat seals (once a day, so relighting does not farm them)?</summary>
        public static bool HeatSealsFree(FraysPlayerRecord rec, int today) => rec != null && rec.SkyHeatDay != today;

        /// <summary>A group camp reached sky_seals_heat for the first time this camp: each member present, once a day.</summary>
        public static void OnGroupHeat(IEnumerable<Player> members)
        {
            var n = FraysSettings.Int(FraysSettings.SkySealsAtHeat);
            var heat = FraysSettings.Int(FraysSettings.SkySealsHeat);

            if (n <= 0)
                return;

            foreach (var p in members.Where(p => p != null).Distinct())
            {
                var rec = FraysManager.Record(p);

                if (!HeatSealsFree(rec, FraysLongTerm.Today))
                    continue;

                rec.SkyHeatDay = FraysLongTerm.Today;
                FraysManager.SaveRecord(p, rec);
                GiveSeals(p, n, $"Your group reaches Heat {heat}");
            }
        }

        // ---- the Sky Steward's bundles and the Sky Banner ----------------------------------------------------------

        /// <summary>Pure: what a bundle holds (the item and the setting for how many), or null for any other item.</summary>
        public static (uint Wcid, string CountSetting)? BundleOf(uint wcid) => wcid switch
        {
            KeyBundle => (FraysLongTerm.DreamKey, FraysSettings.SkyBundleKeys),
            KindlingBundle => (FraysLongTerm.Kindling, FraysSettings.SkyBundleKindling),
            ShardBundle => (FraysLongTerm.SigilShard, FraysSettings.SkyBundleShards),
            _ => null,
        };

        private static bool UseBundle(Player player, WorldObject item)
        {
            var b = BundleOf(item.WeenieClassId);

            if (b == null)
                return false;

            var n = FraysSettings.Int(b.Value.CountSetting);

            if (n <= 0)
            {
                FraysManager.Tell(player, $"The {item.Name} is empty for now.");
                return true;
            }

            if (player.GetInventoryItem(item.Guid.Full) == null || !player.TryConsumeFromInventoryWithNetworking(item, 1))
            {
                FraysManager.Tell(player, $"Carry the {item.Name} in your pack to use it.");
                return true;
            }

            var name = b.Value.Wcid == FraysLongTerm.DreamKey ? "Vault Key" : b.Value.Wcid == FraysLongTerm.Kindling ? "Kindling" : "Sigil Shard";
            FraysLongTerm.Give(player, b.Value.Wcid, n, $"You open the {item.Name}: {n} {name}{(n == 1 || name == "Kindling" ? "" : "s")}.");
            return true;
        }

        /// <summary>Pure: the Sky Banner's bonus on a camp's modifiers while it flies (group camps only).</summary>
        public static void ApplyBanner(FraysCampMods m, bool group, double now, double until)
        {
            if (!group || now >= until)
                return;

            m.Embers *= 1 + FraysSettings.Number(FraysSettings.SkyBannerEmbers);
            m.KeyBar *= 1 + FraysSettings.Number(FraysSettings.SkyBannerKeys);
        }

        public static string BannerText() =>
            $"+{FraysSettings.Number(FraysSettings.SkyBannerEmbers) * 100:0.#}% Embers and +{FraysSettings.Number(FraysSettings.SkyBannerKeys) * 100:0.#}% key bar for every member";

        private static bool UseBanner(Player player, WorldObject item)
        {
            var fire = FraysManager.FireOf(player);
            var minutes = FraysSettings.Number(FraysSettings.SkyBannerMinutes);

            if (fire == null || !fire.Def.Group || !fire.IsOwner(player))
            {
                FraysManager.Tell(player, "Raise the Sky Banner while you hold a group camp on the Sky Fray.");
                return true;
            }

            if (minutes <= 0)
            {
                FraysManager.Tell(player, "The Sky Banner will not fly for now.");
                return true;
            }

            var now = ACE.Common.Time.GetUnixTime();

            if (now < fire.SkyBannerUntil)
            {
                FraysManager.Tell(player, $"A Sky Banner already flies over this camp for {System.Math.Ceiling((fire.SkyBannerUntil - now) / 60):0} more minutes. Banners do not stack.");
                return true;
            }

            if (player.GetInventoryItem(item.Guid.Full) == null || !player.TryConsumeFromInventoryWithNetworking(item, 1))
            {
                FraysManager.Tell(player, $"Carry the {item.Name} in your pack to use it.");
                return true;
            }

            fire.SkyBannerUntil = now + minutes * 60;

            foreach (var g in fire.OwnerGuids)
                if (ACE.Server.Managers.PlayerManager.GetOnlinePlayer(g) is Player m)
                    FraysManager.Tell(m, $"{player.Name} raises a Sky Banner: {BannerText()} for {minutes:0} minutes.");

            return true;
        }

        /// <summary>A player used a Sky Steward item: true when this handled it.</summary>
        public static bool TryUse(Player player, WorldObject item)
        {
            if (item != null && item.WeenieClassId == Banner)
                return UseBanner(player, item);

            if (item != null && UseBundle(player, item))
                return true;

            var unlock = item == null ? null : UnlockOf(item.WeenieClassId);

            if (unlock == null)
                return false;

            var rec = FraysManager.Record(player);

            if (Has(rec, unlock))
            {
                FraysManager.Tell(player, $"You already have this. {UnlockText(unlock)}");
                return true;
            }

            if (player.GetInventoryItem(item.Guid.Full) == null || !player.TryConsumeFromInventoryWithNetworking(item, 1))
            {
                FraysManager.Tell(player, $"Carry the {item.Name} in your pack to use it.");
                return true;
            }

            Unlock(rec, unlock);
            FraysManager.SaveRecord(player, rec);
            FraysManager.Tell(player, UnlockText(unlock));
            return true;
        }
    }
}
