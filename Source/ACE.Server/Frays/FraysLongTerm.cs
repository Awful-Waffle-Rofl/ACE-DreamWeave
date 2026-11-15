using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Database;
using ACE.Server.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Frays' long-term systems: slayer stones (Bane and Reroll), Dream Chests, the dream companion and Kindling,
    /// campfire tiers, Ascension, daily
    /// bounties, recall to your fire, claims (items waiting at the Fray-Warden), the weekly top-3 awards, Lucid Night,
    /// role-set pieces in Snarl caches and the daily Fray loot log.
    /// </summary>
    public static class FraysLongTerm
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(FraysLongTerm));

        public const uint BaneStone = 2000077;
        public const uint RerollStone = 2000078;
        public const uint DreamChest = 2000079;

        /// <summary>The Vault Key: opens one Ember Vault (the locked chest) by the Great Hall (formerly the Dream Key).</summary>
        public const uint DreamKey = 2000152;

        /// <summary>
        /// The Vault Keyring (dreamweave-content 2000190): single keys stack into it automatically. It is a Key with the vault's key code whose uses are the Vault Keys on it, so they take one slot.
        /// </summary>
        public const uint VaultKeyring = 2000190;
        public const uint SnarlTrophy = 2000080;
        public const uint Tapestry = 2000085;
        // 2000086 Fray Hearth: retired.
        public const uint Kindling = 2000087;
        /// <summary>The Campfire Hearthstone (dreamweave-content 2000093): returns its holder to their campfire, never used up.</summary>
        public const uint Hearthstone = 2000093;
        /// <summary>The Ember Cache (2000175): retired; a held one turns into a Vault Key when used.</summary>
        public const uint EmberCache = 2000175;


        // ---- giving items --------------------------------------------------------------------------------------


        /// <summary>Lighting a campfire: a Campfire Hearthstone for anyone who has none (lost, or their first fire).</summary>
        public static void GiveHearthstone(Player player)
        {
            // The gem comes the first time you use the fire, with a popup that explains it (it was a Heat 5 unlock). Given on every lighting when missing; the popup only the first time.
            if (player == null)
                return;

            var rec = FraysManager.Record(player);
            var first = !rec.HearthstoneGiven;

            if (player.GetInventoryItemsOfWCID(Hearthstone).Count == 0)
                Give(player, Hearthstone, 1, "A Campfire Hearthstone: use it to return to the campfire you hold, as often as you like.");

            if (!first)
                return;

            rec = FraysManager.Record(player);
            rec.HearthstoneGiven = true;
            FraysManager.SaveRecord(player, rec);
            player.Session?.Network.EnqueueSend(new ACE.Server.Network.GameEvent.Events.GameEventPopupString(player.Session, HearthstonePopup()));
        }

        /// <summary>The first-light popup about the Campfire Hearthstone (ASCII only).</summary>
        public static string HearthstonePopup()
        {
            var cooldown = FraysSettings.Number(FraysSettings.RecallCooldown);
            var away = FraysSettings.Number(FraysSettings.FireAwaySeconds);

            return "THE CAMPFIRE HEARTHSTONE\n\n"
                + "A Campfire Hearthstone is in your pack. Use it to return to the campfire you hold, from anywhere, as often as you like"
                + (cooldown > 0 ? $" (once every {cooldown / 60:0.#} minutes)" : "") + ". It is never used up.\n\n"
                + (FraysSettings.Toggle(FraysSettings.DeathAtFire)
                    ? "If you die, you wake beside your fire."
                    : "If you die, you wake at the lifestone. Use the Hearthstone to go straight back to your fire instead of running.")
                + (away > 0 ? $" Your fire goes out if no owner stands by it for {away / 60:0} minutes." : "") + "\n\n"
                + "/frays recall does the same. Lost it? Light a fire and you get a new one.";
        }
        /// <summary>Puts an item in the player's pack, or keeps it as a claim at the Fray-Warden when it will not fit.</summary>
        public static void Give(Player player, uint wcid, int amount = 1, string why = null)
        {
            if (player == null || amount <= 0)
                return;

            try
            {
                var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

                if (weenie == null)
                {
                    log.Warn($"[FRAYS] no weenie {wcid} to give {player.Name}");
                    return;
                }

                var given = 0;

                if (wcid == DreamKey && DatabaseManager.World.GetCachedWeenie(VaultKeyring) != null)
                    given = AddVaultKeys(player, amount);
                else if (weenie.WeenieType == WeenieType.Stackable)
                    given = player.FraysAddStackable(wcid, amount);
                else
                {
                    for (var i = 0; i < amount; i++)
                    {
                        var wo = WorldObjectFactory.CreateNewWorldObject(wcid);

                        if (wo != null && player.TryCreateInInventoryWithNetworking(wo))
                            given++;
                        else
                            wo?.Destroy();
                    }
                }

                if (given < amount)
                {
                    var rec = FraysManager.Record(player);

                    for (var i = given; i < amount; i++)
                        rec.Claims.Add(wcid);

                    FraysManager.SaveRecord(player, rec);
                    FraysManager.Tell(player, $"Your pack is full: {amount - given} {weenie.GetProperty(PropertyString.Name) ?? "items"} wait for you with the Fray-Warden (/frays claim).");
                }
                else if (why != null)
                    FraysManager.Tell(player, wcid == DreamKey && given > 0 ? $"{why} It goes onto your Vault Keyring." : why);
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] giving {wcid} to {player.Name} threw", ex);
            }
        }

        /// <summary>Keys a Vault Keyring holds (its MaxStructure, 1000 in the weenie).</summary>
        public static int KeyringMax(WorldObject ring) => Math.Max(1, (int)(ring?.MaxStructure ?? 1000));

        /// <summary>
        /// Puts <paramref name="amount"/> Vault Keys on the player's Vault Keyring (a new ring when they have none or it is
        /// full). Returns how many found a place; the rest wait as claims.
        /// </summary>
        public static int AddVaultKeys(Player player, int amount)
        {
            var given = 0;

            foreach (var ring in player.GetInventoryItemsOfWCID(VaultKeyring))
            {
                if (given >= amount)
                    break;

                var have = ring.Structure ?? 0;
                var add = Math.Min(amount - given, KeyringMax(ring) - have);

                if (add <= 0)
                    continue;

                ring.Structure = (ushort)(have + add);
                player.Session?.Network.EnqueueSend(new ACE.Server.Network.GameMessages.Messages.GameMessagePublicUpdatePropertyInt(ring, PropertyInt.Structure, (int)ring.Structure));
                given += add;
            }

            while (given < amount)
            {
                var ring = WorldObjectFactory.CreateNewWorldObject(VaultKeyring);

                if (ring == null)
                    break;

                var add = Math.Min(amount - given, KeyringMax(ring));
                ring.Structure = (ushort)add;

                if (!player.TryCreateInInventoryWithNetworking(ring))
                {
                    ring.Destroy();
                    break;
                }

                given += add;
            }

            return given;
        }

        /// <summary>
        /// Folds loose Vault Keys (bought, or held from before the keyring) onto the Vault Keyring. Called when a fire is lit
        /// and when a loose key is used. Returns a ring holding keys, or null when there are none.
        /// </summary>
        public static WorldObject FoldLooseKeys(Player player)
        {
            if (player == null || DatabaseManager.World.GetCachedWeenie(VaultKeyring) == null)
                return null;

            try
            {
                var loose = player.GetInventoryItemsOfWCID(DreamKey);
                var keys = 0;

                foreach (var key in loose)
                {
                    var uses = Math.Max(1, (int)(key.Structure ?? 1));

                    if (player.TryConsumeFromInventoryWithNetworking(key, 1))
                        keys += uses;
                }

                if (keys > 0)
                {
                    var given = AddVaultKeys(player, keys);

                    if (given < keys)
                    {
                        var rec = FraysManager.Record(player);

                        for (var i = given; i < keys; i++)
                            rec.Claims.Add(DreamKey);

                        FraysManager.SaveRecord(player, rec);
                    }

                    FraysManager.Tell(player, $"{keys} Vault Key{(keys == 1 ? " goes" : "s go")} onto your Vault Keyring."
                        + (given < keys ? $" {keys - given} wait with the Fray-Warden (/frays claim)." : ""));
                }

                return player.GetInventoryItemsOfWCID(VaultKeyring).FirstOrDefault(r => (r.Structure ?? 0) > 0);
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] folding Vault Keys for {player.Name} threw", ex);
                return null;
            }
        }

        public static string Claim(Player player)
        {
            var rec = FraysManager.Record(player);

            if (rec.Claims.Count == 0)
                return "Nothing is waiting for you.";

            var claims = rec.Claims.ToList();
            rec.Claims.Clear();
            FraysManager.SaveRecord(player, rec);

            foreach (var g in claims.GroupBy(w => w))
                Give(player, g.Key, g.Count());

            return $"You collect {claims.Count} item{(claims.Count == 1 ? "" : "s")}.";
        }

        // ---- using items ---------------------------------------------------------------------------------------

        public static bool TryUse(Player player, WorldObject item)
        {
            switch (item?.WeenieClassId)
            {
                case DreamChest:
                    // Retired: a held Dream Chest turns into a Vault Key for the Ember Vault.
                    if (player.TryConsumeFromInventoryWithNetworking(item, 1))
                        Give(player, DreamKey, 1, "The old Dream Chest crumbles into a Vault Key. Use it on an Ember Vault by the Great Hall.");
                    return true;
                case FraysEffigies.EffigyWcid:
                    FraysEffigies.OnUse(player, item);
                    return true;    // a plain use collects the form (no use-on-a-target, no /mule reshape)
                case EmberCache:
                    // Retired: the Ember Vault is the one chest. A held stack of Ember Caches turns
                    // into as many Vault Keys (a cache held the Vault's own roll, without its bonus).
                    {
                        var n = Math.Max(1, item.StackSize ?? 1);

                        if (player.GetInventoryItem(item.Guid.Full) != null && player.TryConsumeFromInventoryWithNetworking(item, n))
                            Give(player, DreamKey, n, $"The Ember Cache{(n == 1 ? " crumbles into a Vault Key" : $"s crumble into {n} Vault Keys")}. Use {(n == 1 ? "it" : "them")} on an Ember Vault by the Great Hall.");
                        else
                            FraysManager.Tell(player, "Carry the Ember Cache in your pack to use it.");
                    }
                    return true;
                case Hearthstone:
                    // An unlimited portal gem back to the campfire you control. Never used up.
                    FraysManager.Tell(player, Recall(player));
                    return true;
                case Kindling:
                    FeedCompanion(player, item);
                    return true;
                case SigilShard:
                    JoinShards(player, item);
                    return true;
                case FraysCompanions.RebindingEmber:
                    FraysManager.Tell(player, "Rebinding Ember: swaps one companion's style and keeps its level. Type /frays companion <form> <style> (warder, mender or striker). /frays companion lists your forms.");
                    return true;
                case FraysCollection.LesserSigil:
                case FraysCollection.GreaterSigil:
                case FraysCollection.GrandSigil:
                    FraysManager.Tell(player, $"A {item.Name} raises a boon in your collection one rank: /fray sigil <boon>, for example /fray sigil wildfire. /fray collection shows your ranks.");
                    return true;
                case BaneStone:
                case RerollStone:
                    FraysManager.Tell(player, $"Use the {item.Name} on a weapon or caster.");
                    return true;
                default:
                    return FraysSky.TryUse(player, item);
            }
        }

        public static bool TryUseOn(Player player, WorldObject source, WorldObject target)
        {
            if (source == null || (source.WeenieClassId != BaneStone && source.WeenieClassId != RerollStone))
                return false;

            var reroll = source.WeenieClassId == RerollStone;

            if (target == null || player.GetInventoryItem(target.Guid.Full) == null && player.EquippedObjects.Values.All(o => o != target)
                || (target.ItemType & (ItemType.MeleeWeapon | ItemType.MissileWeapon | ItemType.Caster)) == 0)
            {
                FraysManager.Tell(player, $"Use the {source.Name} on a weapon or caster you carry.");
                return true;
            }

            var current = target.SlayerCreatureType;
            var rec = FraysManager.Record(player);
            CreatureType type;

            if (!reroll)
            {
                // A Slayer Stone: a random family slayer, on a weapon that has none.
                if (current != null)
                {
                    FraysManager.Tell(player, $"The {target.Name} already slays {current}. A Reroll Stone rolls it to a new random family.");
                    return true;
                }

                type = RollSlayer(rec, null);
            }
            else
            {
                // A Reroll Stone: a lottery over every family, never the one it already slays (it can roll any family).
                if (current == null)
                {
                    FraysManager.Tell(player, $"The {target.Name} slays nothing yet. A Slayer Stone gives it one first.");
                    return true;
                }

                type = RollReroll(current, FraysFamilies.For(FraysDefs.VissidalCoast)?.Type, ThreadSafeRandom.Next(0.0f, 1.0f));
            }

            if (!player.TryConsumeFromInventoryWithNetworking(source, 1))
                return true;

            target.SlayerCreatureType = type;

            if ((target.GetProperty(PropertyFloat.SlayerDamageBonus) ?? 0) < FraysSettings.Number(FraysSettings.BaneSlayerBonus))
                target.SetProperty(PropertyFloat.SlayerDamageBonus, FraysSettings.Number(FraysSettings.BaneSlayerBonus));

            FraysManager.Tell(player, $"The {target.Name} now slays {type}.");
            return true;
        }

        /// <summary>A random family slayer, every family equally likely.</summary>
        public static CreatureType RollSlayer(FraysPlayerRecord rec, CreatureType? not)
        {
            var types = FraysFamilies.All.Select(f => f.Type).Where(t => t != not && t != CreatureType.Invalid).Distinct().ToList();

            return types[ThreadSafeRandom.Next(0, types.Count - 1)];
        }

        /// <summary>
        /// A Reroll Stone's roll: reroll_week_chance to land on this week's family (when the weapon
        /// does not slay it already), else any other family at random. Never the one it slays now.
        /// </summary>
        public static CreatureType RollReroll(CreatureType? current, CreatureType? week, double roll)
        {
            if (week is CreatureType w && w != CreatureType.Invalid && w != current
                && roll < FraysSettings.Number(FraysSettings.RerollWeekChance))
                return w;

            var types = FraysFamilies.All.Select(f => f.Type)
                .Where(t => t != CreatureType.Invalid && t != current && t != week).Distinct().ToList();

            if (types.Count == 0)
                return week is CreatureType only && only != current ? only : current ?? CreatureType.Invalid;

            return types[ThreadSafeRandom.Next(0, types.Count - 1)];
        }

        /// <summary>/frays reroll: retired. A Reroll Stone now rolls a random family, so there is nothing to choose.</summary>
        public static string SetRerollTarget(Player player, string[] args) =>
            $"There is nothing to choose: a Reroll Stone rolls a weapon's slayer to a random new family (any of {FraysFamilies.All.Select(f => f.Type).Where(t => t != CreatureType.Invalid).Distinct().Count()}).";

        // ---- the dream companion and campfire tiers -----------------------------------------------------------

        public static readonly string[] CompanionRoles = { "warder", "mender", "striker" };

        public static int CompanionLevel(double xp) => Math.Min(10, (int)Math.Floor(Math.Sqrt(Math.Max(0, xp) / 100)));

        private static void FeedCompanion(Player player, WorldObject kindling)
        {
            var rec = FraysManager.Record(player);

            // Before any form has a style, Kindling still feeds the old single companion (its experience moves later).
            if (rec.Companions.Count == 0 && !string.IsNullOrEmpty(rec.CompanionRole))
            {
                if (CompanionLevel(rec.CompanionXp) >= 10)
                {
                    FraysManager.Tell(player, "Your companion is already at its full strength (level 10).");
                    return;
                }

                if (!player.TryConsumeFromInventoryWithNetworking(kindling, 1))
                    return;

                rec.CompanionXp += KindlingXp;
                FraysManager.SaveRecord(player, rec);
                FraysManager.Tell(player, $"Your {rec.CompanionRole} takes the Kindling (level {CompanionLevel(rec.CompanionXp)}). Give a collected form a style with /frays companion <form> <style> and this experience moves to it.");
                return;
            }

            if (!FraysCompanions.Feed(rec, KindlingXp, out var message))
            {
                FraysManager.Tell(player, message);
                return;
            }

            if (!player.TryConsumeFromInventoryWithNetworking(kindling, 1))
            {
                FraysCompanions.Active(rec).Value.Form.Xp -= KindlingXp;
                return;
            }

            FraysManager.SaveRecord(player, rec);
            FraysManager.Tell(player, message);
        }

        /// <summary>One Kindling's companion experience.</summary>
        public const double KindlingXp = 250;

        public static string Companion(Player player, string[] args) => FraysCompanions.Command(player, args);

        public static string CompanionText(string role, int level) => role switch
        {
            "warder" => $"-{level}% damage taken",
            "mender" => $"heals {level * 0.5:0.#}% of your health every 10 seconds",
            "striker" => $"+{level}% damage",
            _ => "",
        };

        public static string UpgradeFire(Player player)
        {
            var rec = FraysManager.Record(player);
            var max = FraysSettings.Int(FraysSettings.FireTierMax);

            if (rec.FireTier >= max)
                return $"Your campfire is at its highest tier ({max}).";

            var cost = FraysSettings.Int(FraysSettings.FireTierCost) * (rec.FireTier + 1);

            // the pack first, then the bank
            if (!player.TrySpendEmbers(cost))
                return $"Tier {rec.FireTier + 1} costs {cost} Embers, in your pack or bank.";

            rec.FireTier++;
            var auto = FraysPerks.AutoSpend(rec);
            FraysManager.FireOwnedBySolo(player.Guid.Full)?.RefreshLook();   // the bigger blaze at once
            FraysManager.SaveRecord(player, rec);

            return auto.Count > 0
                ? $"Your campfire burns at tier {rec.FireTier}: a bigger flame and a perk point, spent by your {rec.PerkPreset} preset on {string.Join(", ", auto)}. /fray perks off to pick your own."
                : $"Your campfire burns at tier {rec.FireTier}: a bigger flame and a perk point - {FraysPerks.Unspent(rec)} to spend. /frays perk lists the perks.";
        }

        // ---- Ascension -----------------------------------------------------------------------------------------

        /// <summary>
        /// The prestige title each Ascension gives: an Epithet, shown on the campfire's name, on
        /// /top fray and in /frays status. Ascension I to V.
        /// </summary>
        public static readonly string[] AscensionTitles = { "the Kindled", "the Emberborn", "the Flamewarden", "the Pyrebound", "the Ashen Sovereign" };

        /// <summary>Pure: "I" to "X" for an Ascension number (its number past that).</summary>
        public static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", 10 => "X", _ => n.ToString() };

        /// <summary>Pure: the title for Ascension <paramref name="n"/> (the last one for any past the list; null at 0).</summary>
        public static string AscensionTitle(int n) => n <= 0 ? null : AscensionTitles[Math.Min(n, AscensionTitles.Length) - 1];

        /// <summary>Pure: Embers for Ascension <paramref name="next"/>: next times ascension_cost.</summary>
        public static long AscensionCostFor(int next, long baseCost) => Math.Max(0, next) * Math.Max(0, baseCost);

        /// <summary>Pure: Ascensions made this week (0 when the record's week is an old one).</summary>
        public static int AscendsThisWeek(FraysPlayerRecord rec, int week) => rec.AscendWeek == week ? rec.AscendsThisWeek : 0;

        /// <summary>
        /// The camp spark: sparks rise from an ascended camper's flames, more with each Ascension,
        /// seen by everyone near the fire. Retail client scripts only (PlayScript Spark*, 0x67 to 0x72): Ascension I is two
        /// low sparks, II four, III adds two mid, IV four mid, V adds the four high ones.
        /// </summary>
        public static PlayScript[] AscensionSparkScripts(int ascensions)
        {
            var low = new[] { PlayScript.SparkLowLeftFront, PlayScript.SparkLowRightBack, PlayScript.SparkLowRightFront, PlayScript.SparkLowLeftBack };
            var mid = new[] { PlayScript.SparkMidLeftFront, PlayScript.SparkMidRightBack, PlayScript.SparkMidRightFront, PlayScript.SparkMidLeftBack };
            var up = new[] { PlayScript.SparkUpLeftFront, PlayScript.SparkUpRightBack, PlayScript.SparkUpRightFront, PlayScript.SparkUpLeftBack };

            return Math.Clamp(ascensions, 0, 5) switch
            {
                0 => Array.Empty<PlayScript>(),
                1 => low.Take(2).ToArray(),
                2 => low,
                3 => low.Concat(mid.Take(2)).ToArray(),
                4 => low.Concat(mid).ToArray(),
                _ => low.Concat(mid).Concat(up).ToArray(),
            };
        }

        /// <summary>Seconds between Ascension sparks.</summary>
        public const double AscensionSparkSeconds = 5;

        /// <summary>Called from a fire's own tick: its owner's Ascension sparks every few seconds.</summary>
        public static void AscensionSpark(FraysFire fire, double now)
        {
            if (fire?.FlameObject == null || fire.FlameObject.IsDestroyed || now < fire.NextAscendSparkAt || !FraysSettings.Toggle(FraysSettings.HeatEffects))
                return;

            fire.NextAscendSparkAt = now + AscensionSparkSeconds;
            PlaySpark(fire.FlameObject, fire.OwnerAscensions);
        }

        /// <summary>Plays the sparks of <paramref name="ascensions"/> on <paramref name="target"/> (the admin preview too). False when there are none.</summary>
        public static bool PlaySpark(WorldObject target, int ascensions)
        {
            var scripts = AscensionSparkScripts(ascensions);

            if (target == null || scripts.Length == 0)
                return false;

            foreach (var s in scripts)
                target.EnqueueBroadcast(new GameMessageScript(target.Guid, s));

            return true;
        }

        public static string Ascend(Player player, bool confirm)
        {
            var rec = FraysManager.Record(player);
            var max = FraysSettings.Int(FraysSettings.AscensionMax);
            var week = FraysWeek.Current;
            var perWeek = FraysSettings.Int(FraysSettings.AscensionPerWeek);

            if (rec.Ascensions >= max)
                return $"You have ascended {max} times, the most there is.";

            // An active camp at Heat 20 now, not just a rating of 20
            var needHeat = FraysSettings.Int(FraysSettings.AscensionHeat);
            var campHeat = FraysManager.FireOf(player) is FraysFire camp ? camp.LevelOf(player) : -1;

            if (campHeat < 0)
                return $"Ascension needs an active camp at Heat {needHeat}. Light a campfire and climb.";

            if (campHeat < needHeat)
                return $"Ascension needs your camp at Heat {needHeat} (it is at Heat {campHeat}).";

            if (perWeek > 0 && AscendsThisWeek(rec, week) >= perWeek)
                return $"You have ascended {(perWeek == 1 ? "once" : $"{perWeek} times")} this week. The next Ascension opens at the weekly reset, {FraysWeek.NextReset:ddd d MMM HH:mm} UTC.";

            var next = rec.Ascensions + 1;
            var cost = AscensionCostFor(next, FraysSettings.Int(FraysSettings.AscensionCost));
            var title = AscensionTitle(next);

            if (!confirm)
                return $"Ascension {Roman(next)} costs {cost:N0} Embers (pack or bank) and resets your Heat rating to 0, for a permanent +1% damage and +1% Embers in the Frays, the title \"{title}\" and a brighter campfire. One Ascension a week. Type /frays ascend confirm.";

            // the pack first, then the bank, as a fire tier
            if (cost > 0 && !player.TrySpendEmbers(cost))
                return $"Ascension {Roman(next)} costs {cost:N0} Embers, in your pack or bank.";

            rec.Rating = 0;
            rec.StartHeat = 0;
            rec.Ascensions = next;
            rec.AscendsThisWeek = AscendsThisWeek(rec, week) + 1;
            rec.AscendWeek = week;

            rec.Epithets ??= new List<string>();
            if (title != null && !rec.Epithets.Contains(title))
                rec.Epithets.Add(title);
            if (title != null)
                rec.Epithet = title;   // the newest is shown; /fray epithet picks another

            FraysManager.SaveRecord(player, rec);

            if (FraysManager.FireOf(player) is FraysFire fire)
            {
                FraysManager.Feed(fire, $"{player.Name} has ascended: Ascension {Roman(next)}, {title}.");
                fire.RefreshLook();
            }

            return $"You ascend: Ascension {Roman(next)}, {title}. Your rating starts again at 0; your Ascensions give +{next}% damage and +{next}% Embers in the Frays, and your campfire burns brighter."
                + (next < max && perWeek > 0 ? $" The next one opens at the weekly reset, {FraysWeek.NextReset:ddd d MMM HH:mm} UTC." : "");
        }

        // ---- the camp's long-term modifiers -------------------------------------------------------------------

        /// <summary>Companion, campfire tier and Ascension, on top of a camper's modifiers.</summary>
        public static void Apply(Player player, FraysPlayerRecord rec, FraysCampMods m, FraysFamily family)
        {
            var (role, level) = FraysCompanions.Bonus(rec);

            switch (role)
            {
                case "warder": m.BonusReduction += 0.01 * level; break;   // the Frays pools: 10% at level 10
                case "striker": m.BonusDamage += 0.01 * level; break;
                case "mender": m.HealPer10s = 0.005 * level; break;
            }

            // The campfire's perks (one point a tier; FraysPerks). The old +1% Embers and Heat gain a tier is gone.
            FraysPerks.Apply(rec, m);

            var asc = Math.Min(rec.Ascensions, FraysSettings.Int(FraysSettings.AscensionMax));

            if (asc > 0)
            {
                m.BonusDamage += 0.01 * asc;
                m.Embers *= 1 + 0.01 * asc;
            }

            // Embered gear is gone: items that still carry the flag give nothing.

            // The Lucid Night buff: every camper while it lasts.
            if (LucidNight)
            {
                m.BonusDamage += FraysSettings.Number(FraysSettings.LucidDamage);
                m.Embers *= 1 + FraysSettings.Number(FraysSettings.LucidEmbers);
            }

        }

        // ---- daily and weekly tasks ---------------------------------------------------------------------------

        public sealed class BountyDef
        {
            public string Key;
            public string Text;
            public double Target;
            public bool Weekly;
        }

        public static readonly BountyDef[] BountyPool =
        {
            // daily
            new BountyDef { Key = "health", Text = "Remove 5,000,000 health", Target = 5_000_000 },
            new BountyDef { Key = "heat12", Text = "Fight 60 minutes at Heat 12 or higher", Target = 60 },
            new BountyDef { Key = "snarls", Text = "Kill 3 Snarls", Target = 3 },
            new BountyDef { Key = "heat8", Text = "Fight 90 minutes at Heat 8 or higher", Target = 90 },
            // weekly
            new BountyDef { Key = "w_kills", Text = "Kill 6,500 of this week's creatures (by size: a swarm one counts 0.4)", Target = 6_500, Weekly = true },
            new BountyDef { Key = "w_snarls", Text = "Kill 15 Snarls", Target = 15, Weekly = true },
            new BountyDef { Key = "w_great", Text = "Kill the Great Snarl", Target = 1, Weekly = true },   // replaces the daily Hard Snarl task
            new BountyDef { Key = "w_heat", Text = "Fight 5 hours at Heat 10 or higher", Target = 300, Weekly = true },
            new BountyDef { Key = "w_health", Text = "Remove 40,000,000 health", Target = 40_000_000, Weekly = true },
            new BountyDef { Key = "w_group", Text = "Camp 2 hours in a group camp on the Sky Fray", Target = 120, Weekly = true },   // another way to earn Sky Seals; pays Sky Seals
        };

        public static int Today => (int)Math.Floor((DateTime.UtcNow - new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays);

        /// <summary>The day's three tasks: the same for everyone, picked by the day number.</summary>
        public static List<BountyDef> BountiesFor(int day)
        {
            var rng = new Random(day * 7919 + 17);
            return BountyPool.Where(b => !b.Weekly).OrderBy(_ => rng.Next()).Take(3).ToList();
        }

        /// <summary>The weekly kill tasks (the kill task NPCs give dailies and 1 weekly).</summary>
        public static readonly string[] WeeklyKillKeys = { "w_kills", "w_snarls" };

        /// <summary>Weekly tasks every week has, besides the rolled kill task (Kill the Great Snarl, Camp 2 hours in a group camp).</summary>
        public static readonly string[] WeeklyAlways = { "w_great", "w_group" };

        /// <summary>The week's tasks: one kill task, the same for everyone, picked by the week number, and Kill the Great Snarl.</summary>
        public static List<BountyDef> WeeklyFor(int week)
        {
            var rng = new Random(week * 104729 + 31);
            return BountyPool.Where(b => b.Weekly && WeeklyKillKeys.Contains(b.Key)).OrderBy(_ => rng.Next()).Take(1)
                .Concat(BountyPool.Where(b => WeeklyAlways.Contains(b.Key))).ToList();
        }

        internal static FraysPlayerRecord RollBountyDay(FraysPlayerRecord rec)
        {
            var day = Today;
            var week = FraysWeek.Current;

            if (rec.BountyDay != day)
            {
                rec.BountyDay = day;
                var keep = rec.BountyGoals.Where(k => k.StartsWith("w_")).ToList();
                rec.BountyGoals = BountiesFor(day).Select(b => b.Key).Concat(keep).ToList();

                foreach (var k in rec.BountyProgress.Keys.Where(k => !k.StartsWith("w_")).ToList())
                    rec.BountyProgress.Remove(k);

                rec.BountyPaid = rec.BountyPaid.Where(k => k.StartsWith("w_")).ToList();
                rec.BountyTaken = rec.BountyTaken.Where(k => k.StartsWith("w_")).ToList();
            }

            if (rec.BountyWeek != week)
            {
                rec.BountyWeek = week;
                rec.BountyGoals = rec.BountyGoals.Where(k => !k.StartsWith("w_")).Concat(WeeklyFor(week).Select(b => b.Key)).ToList();

                foreach (var k in rec.BountyProgress.Keys.Where(k => k.StartsWith("w_")).ToList())
                    rec.BountyProgress.Remove(k);

                rec.BountyPaid = rec.BountyPaid.Where(k => !k.StartsWith("w_")).ToList();
                rec.BountyTaken = rec.BountyTaken.Where(k => !k.StartsWith("w_")).ToList();
            }

            // A record rolled while the week had two tasks (any of four) holds just this week's one kill task.
            rec.BountyGoals = rec.BountyGoals.Where(k => !k.StartsWith("w_")).Concat(WeeklyFor(week).Select(b => b.Key)).ToList();

            return rec;
        }

        /// <summary>
        /// Adds progress to today's and this week's tasks that track <paramref name="key"/> (a daily key, which also feeds
        /// the matching weekly one); pays each task in Embers when it completes.
        /// </summary>
        public static void Bounty(Player player, string key, double amount)
        {
            if (player == null || amount <= 0)
                return;

            var rec = RollBountyDay(FraysManager.Record(player));
            var keys = new List<string> { key };

            // The weekly task each daily measure also feeds.
            switch (key)
            {
                case "kills": keys.Add("w_kills"); break;
                case "snarls": keys.Add("w_snarls"); break;
                case "health": keys.Add("w_health"); break;
            }

            var changed = false;

            foreach (var k in keys)
            {
                if (!rec.BountyGoals.Contains(k) || !rec.BountyTaken.Contains(k) || rec.BountyPaid.Contains(k))
                    continue;

                var def = BountyPool.First(b => b.Key == k);
                rec.BountyProgress.TryGetValue(k, out var have);
                have += amount;
                rec.BountyProgress[k] = have;
                changed = true;

                if (have >= def.Target)
                {
                    rec.BountyPaid.Add(k);
                    var seals = TaskSeals(k);
                    var embers = k == "w_group" ? 0 : FraysSettings.Int(def.Weekly ? FraysSettings.WeeklyTaskEmbers : FraysSettings.BountyEmbers);
                    FraysMetrics.TasksCompleted.Add(1);
                    // Paid on hand-in at its task-giver (like the Marae Lassel kill tasks).
                    FraysHunts.Ready(player, rec, FraysHunts.GiverOf(new FraysHunts.TaskEntry { Key = k, Weekly = def.Weekly }), def.Text, embers, 0, seals);
                }
            }

            if (changed)
                FraysManager.SaveRecord(player, rec);
        }

        /// <summary>Pure: the Sky Seals a task pays in place of Embers (only the weekly group task).</summary>
        public static int TaskSeals(string key) => key == "w_group" ? Math.Max(0, FraysSettings.Int(FraysSettings.SkySealsWeeklyTask)) : 0;

        /// <summary>The daily and weekly tasks now live on the Huntmaster's task board.</summary>
        public static string BountyText(Player player) => FraysHunts.Board(player);

        // ---- recall to your fire -------------------------------------------------------------------------------

        public static string Recall(Player player)
        {
            var fire = FraysManager.FireOwnedBySolo(player.Guid.Full);

            if (fire == null)
                return "You hold no campfire to return to.";

            var rec = FraysManager.Record(player);
            var now = Time.GetUnixTime();
            var wait = FraysSettings.Number(FraysSettings.RecallCooldown) - (now - rec.LastRecallAt);

            if (wait > 0)
                return $"Your fire's pull returns in {wait:0} seconds.";

            if (player.IsDead || player.PKTimerActive)
                return "You cannot return to your fire right now.";

            rec.LastRecallAt = now;
            FraysManager.SaveRecord(player, rec);

            var pos = fire.FirePosition;
            pos.PositionX += 2;
            player.Teleport(pos);
            return $"Your campfire in {fire.Def.RoomName} pulls you home.";
        }

        /// <summary>
        /// Where a dead camper respawns: beside the lit solo campfire they hold (the Hearthstone's landing spot), or null
        /// for the normal lifestone. Without it, a camper ran back from the
        /// village on foot after a death and could get stuck in the rocks. Uses no recall cooldown.
        /// </summary>
        public static ACE.Entity.Position DeathRespawn(Player player)
        {
            if (player == null || !FraysSettings.Toggle(FraysSettings.DeathAtFire))
                return null;

            var fire = FraysManager.FireOwnedBySolo(player.Guid.Full);

            if (fire == null)
                return null;

            var pos = fire.FirePosition;
            pos.PositionX += 2;
            return pos;
        }

        // ---- after a corpse fills: caches, the loot log ---------------------------------------

        private static readonly Dictionary<string, int> lootToday = new Dictionary<string, int>();
        private static int lootDay = -1;

        /// <summary>The trade note an MMD drop holds (retail Trade Note (250,000)).</summary>
        public const uint TradeNoteMmd = 20630;

        /// <summary>The Sigil Shard (dreamweave-content 2000193): 10 join into a Lesser Sigil.</summary>
        public const uint SigilShard = 2000193;
        public const int ShardsPerSigil = 10;

        private static bool Chance(string key) => FraysSettings.Number(key) is var c && c > 0 && ThreadSafeRandom.Next(0.0f, 1.0f) < c;

        /// <summary>Puts <paramref name="amount"/> of <paramref name="wcid"/> in the corpse (one stack). False when nothing went in.</summary>
        private static bool AddToCorpse(Corpse corpse, uint wcid, int amount = 1)
        {
            if (wcid == 0 || amount <= 0)
                return false;

            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (wo == null)
                return false;

            if (amount > 1)
                wo.SetStackSize(Math.Min(amount, wo.MaxStackSize ?? amount));

            if (corpse.TryAddToInventory(wo))
                return true;

            wo.Destroy();
            return false;
        }

        /// <summary>
        /// A Great Snarl paid <paramref name="player"/>: great_snarl_aug_chance of one item from snarl_aug_wcids (a Token of
        /// the Augmentation Gem or a Stipend), into their pack (they are no longer in the Snarl cache).
        /// </summary>
        public static void GreatSnarlAug(Player player)
        {
            if (player == null || !Chance(FraysSettings.GreatSnarlAugChance))
                return;

            var augs = ParseWcids(FraysSettings.Text(FraysSettings.SnarlAugWcids));

            if (augs.Count == 0)
                return;

            var wcid = augs[ThreadSafeRandom.Next(0, augs.Count - 1)];
            var name = DatabaseManager.World.GetCachedWeenie(wcid)?.GetName() ?? "a rare prize";
            Give(player, wcid, 1, $"The Great Snarl also leaves you {name}.");
        }

        /// <summary>Pure: the wcids in a comma separated list (snarl_aug_wcids); bad entries are skipped.</summary>
        public static List<uint> ParseWcids(string text) =>
            (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => uint.TryParse(s, out var w) ? w : 0).Where(w => w > 0).ToList();

        /// <summary>
        /// A Snarl's cache, beyond its magic items: each prize on its own chance (every one a setting):
        /// Diamond Scarabs, MMDs, Embers, Sigil Shards and the Snarl Trophy at 1% (the augmentation gem and stipend items come from the Great Snarl).
        /// </summary>
        private static void AddSnarlExtras(Corpse corpse, FraysCreatureTag tag)
        {
            if (Chance(FraysSettings.SnarlTrophyChance) && AddToCorpse(corpse, SnarlTrophy))
            {
                var finder = PlayerManager.FindByGuid(tag.ForPlayer)?.Name ?? "Someone";
                var family = tag.Fire != null ? FraysFamilies.For(tag.Fire.Def.FrayKey)?.Name ?? "" : "";
                FraysTrophyWall.Add(finder, family);
            }

            if (Chance(FraysSettings.SnarlBaneChance))
                AddToCorpse(corpse, BaneStone);

            // Kindling for the companions: a rare drop from the Snarls.
            if (Chance(FraysSettings.SnarlKindlingChance))
                AddToCorpse(corpse, Kindling);

            if (Chance(FraysSettings.SnarlScarabChance))
                AddToCorpse(corpse, FraysVault.DiamondScarab);

            if (Chance(FraysSettings.SnarlMmdChance))
                AddToCorpse(corpse, TradeNoteMmd, FraysSettings.Int(FraysSettings.SnarlMmdCount));

            if (Chance(FraysSettings.SnarlEmberChance))
            {
                AddToCorpse(corpse, FraysDefs.EmberWcid, FraysSettings.Int(FraysSettings.SnarlEmberCount));
            }

            if (Chance(FraysSettings.SnarlShardChance))
                AddToCorpse(corpse, SigilShard);

            if (Chance(FraysSettings.SnarlAugChance) && ParseWcids(FraysSettings.Text(FraysSettings.SnarlAugWcids)) is var augs && augs.Count > 0)
                AddToCorpse(corpse, augs[ThreadSafeRandom.Next(0, augs.Count - 1)]);
        }

        /// <summary>Using Sigil Shards: 10 from the used stack join into a Lesser Sigil.</summary>
        private static void JoinShards(Player player, WorldObject shards)
        {
            var have = shards.StackSize ?? 1;

            if (have < ShardsPerSigil)
            {
                FraysManager.Tell(player, $"You need {ShardsPerSigil} Sigil Shards in one stack to join them into a Lesser Sigil. This stack has {have}.");
                return;
            }

            if (!player.TryConsumeFromInventoryWithNetworking(shards, ShardsPerSigil))
                return;

            Give(player, FraysCollection.LesserSigil, 1, $"{ShardsPerSigil} Sigil Shards join into a Lesser Sigil.");
        }

        /// <summary>A Fray corpse has its loot (a second after it is made): cache work, the loot log.</summary>
        public static void AfterLoot(Corpse corpse, FraysCreatureTag tag)
        {
            if (corpse == null || corpse.IsDestroyed || tag == null)
                return;

            try
            {
                if (tag.Snarl || tag.SnarlCache)
                    AddSnarlExtras(corpse, tag);

                if (tag.AttuneBestLoot)
                    FraysFire.AttuneBest(corpse);

                var kinds = new List<string>();

                foreach (var item in corpse.Inventory.Values.ToList())
                {
                    kinds.Add(FraysReport.LootKind(item.ItemType));

                    var kind = item.ItemType.ToString();

                    lock (lootToday)
                    {
                        lootToday.TryGetValue(kind, out var n);
                        lootToday[kind] = n + 1;
                    }
                }

                tag.Fire?.NoteLoot(tag.ForPlayer, kinds);
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] after-loot threw", ex);
            }
        }

        // ---- the server's side: week awards, Lucid Night, the loot log, banners -------------------------------

        private static int lastWeekSeen = int.MinValue;
        private static double lucidProgress;
        private static int lucidWeek = int.MinValue;
        private static bool lucidActive;
        private static double nextLucidSave;

        private static int LucidNeed => Math.Max(1, FraysSettings.Int(FraysSettings.LucidGreatSnarls));

        /// <summary>Hours left before the weekly reset.</summary>
        private static double HoursToReset => (FraysWeek.NextReset - DateTime.UtcNow).TotalHours;

        /// <summary>
        /// Lucid Night: only on the final night, the last lucid_hours before the weekly reset, and
        /// only when the server slew lucid_great_snarls Great Snarls this week.
        /// </summary>
        public static bool LucidNight => FraysSettings.Text(FraysSettings.LucidReward) != "off" && lucidWeek == FraysWeek.Current
            && FraysRules.LucidOn(lucidProgress, LucidNeed, HoursToReset, FraysSettings.Number(FraysSettings.LucidHours));

        /// <summary>A Fray kill while Lucid Night lasts: the camper's weekly Lucid Night cache on their first one.</summary>
        public static void LucidKill(Player player)
        {
            if (player != null && LucidNight && FraysSettings.Text(FraysSettings.LucidReward) != "off")
                GrantLucidCache(player);
        }

        /// <summary>
        /// A Great Snarl fell (Lucid Night fills from Great Snarl kills, not from all health removed):
        /// one more toward lucid_great_snarls for the week.
        /// </summary>
        public static void GreatSnarlKilled()
        {
            if (FraysSettings.Text(FraysSettings.LucidReward) == "off")
                return;

            AddLucidWork(1);
        }

        /// <summary>The Lucid Night cache (it comes with the Lucid Night buff): bonded Vault Keys, once a week, on a camper's first kill while it lasts.</summary>
        private static void GrantLucidCache(Player player)
        {
            var keys = FraysSettings.Int(FraysSettings.LucidCacheKeys);
            var rec = FraysManager.Record(player);

            if (keys <= 0 || rec.LucidCacheWeek == FraysWeek.Current)
                return;

            rec.LucidCacheWeek = FraysWeek.Current;
            FraysManager.SaveRecord(player, rec);
            Give(player, DreamKey, keys, $"Lucid Night cache: {keys} Vault Key{(keys == 1 ? "" : "s")} for the Ember Vault.");
        }

        private static void AddLucidWork(double work)
        {

            lock (lootToday)
            {
                if (lucidWeek != FraysWeek.Current)
                {
                    lucidWeek = FraysWeek.Current;
                    lucidProgress = 0;
                    lucidActive = false;
                }

                var before = lucidProgress;
                lucidProgress += work;

                // the week's count is reached: Lucid Night comes on the final night (Tick says when it starts)
                if (before < LucidNeed && lucidProgress >= LucidNeed && !LucidNight)
                    Broadcast($"The Frays have slain {lucidProgress:0} Great Snarls this week. Lucid Night comes on the final night, the last {FraysSettings.Number(FraysSettings.LucidHours):0} hours before the weekly reset.");
            }
        }

        /// <summary>Tick: tells the server when Lucid Night starts (lucidActive is "already told").</summary>
        private static void LucidTick()
        {
            var on = LucidNight;

            lock (lootToday)
            {
                if (on == lucidActive)
                    return;

                lucidActive = on;
            }

            if (on)
                Broadcast($"Lucid Night: the Frays slew {lucidProgress:0} Great Snarls this week. Until the weekly reset every camper deals {FraysSettings.Number(FraysSettings.LucidDamage) * 100:0.#}% more damage and earns {FraysSettings.Number(FraysSettings.LucidEmbers) * 100:0.#}% more Embers, every Snarl's cache holds more, and each camper's first kill brings a Vault Key.");
        }

        public static string LucidText()
        {
            if (FraysSettings.Text(FraysSettings.LucidReward) == "off")
                return "Lucid Night is off.";

            var have = lucidWeek == FraysWeek.Current ? lucidProgress : 0;
            var window = $"the last {FraysSettings.Number(FraysSettings.LucidHours):0} hours before the weekly reset";

            if (LucidNight)
                return $"Lucid Night is here until the weekly reset: +{FraysSettings.Number(FraysSettings.LucidDamage) * 100:0.#}% damage, +{FraysSettings.Number(FraysSettings.LucidEmbers) * 100:0.#}% Embers, bigger Snarl caches and a Vault Key on your first kill.";

            return have >= LucidNeed
                ? $"Lucid Night: {have:0} Great Snarls slain this week, enough. It comes on the final night, {window}."
                : $"Lucid Night: {have:0} of {LucidNeed} Great Snarls slain this week. With {LucidNeed}, it comes on the final night, {window}.";
        }

        public static void Tick(double now)
        {
            try
            {
                var week = FraysWeek.Current;

                if (lastWeekSeen == int.MinValue)
                {
                    lastWeekSeen = week;
                    LoadLucid();
                }
                else if (week != lastWeekSeen)
                {
                    // Version 1: no weekly top-3 cosmetic (AwardWeek is kept for a later decision).
                    lastWeekSeen = week;
                    Broadcast("The Frays turn over: " + FraysWeek.Summary());
                }

                var day = Today;

                lock (lootToday)
                {
                    if (lootDay == -1)
                        lootDay = day;
                    else if (day != lootDay)
                    {
                        log.Info($"[FRAYS] Fray loot on day {lootDay}: {string.Join(", ", lootToday.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
                        lootToday.Clear();
                        lootDay = day;
                    }
                }

                if (now >= nextLucidSave)
                {
                    nextLucidSave = now + 60;
                    LucidTick();
                    SaveLucid();
                }
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] long-term tick threw", ex);
            }
        }

        private static void LoadLucid()
        {
            var parts = FraysSettings.Text(FraysSettings.LucidState).Split('|');

            // week|Great Snarls|active|gs. A 3-part state is the old health-removed bar: its week and flag stay, its progress is dropped.
            if (parts.Length >= 3 && int.TryParse(parts[0], out var w) && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p))
            {
                lucidWeek = w;
                lucidProgress = parts.Length >= 4 && parts[3] == "gs" ? p : 0;
                lucidActive = parts[2] == "1";
            }
        }

        private static void SaveLucid()
        {
            if (lucidWeek == int.MinValue)
                return;

            var text = $"{lucidWeek}|{lucidProgress.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{(lucidActive ? 1 : 0)}|gs";

            if (text != FraysSettings.Text(FraysSettings.LucidState))
                PropertyManager.ModifyString(FraysSettings.Prefix + FraysSettings.LucidState, text);
        }

        /// <summary>The week just ended: the top 3 of each weekly board get a Fray Tapestry, waiting at the Fray-Warden.</summary>
        private static void AwardWeek(int week)
        {
            var exempt = LeaderboardExemptionManager.GetExemptAccountNames();
            var players = PlayerManager.GetAllPlayers().Where(p => !LeaderboardExemptionManager.IsExempt(p, exempt)).ToList();

            var boards = new (string Name, Func<FraysPlayerRecord, double> Score)[]
            {
                ("Heat", r => r.WeekBestHeat),
                ("group Heat", r => r.WeekBestGroupHeat),
                ("Guardian", r => r.WeekBestSnarlSeconds > 0 ? 1_000_000 / r.WeekBestSnarlSeconds : 0),
            };

            foreach (var (name, score) in boards)
            {
                var top = players.Select(p => (Player: p, Rec: FraysPlayerRecord.Parse(p.GetProperty(PropertyString.FraysState))))
                    .Where(x => x.Rec.Week == week && score(x.Rec) > 0)
                    .OrderByDescending(x => score(x.Rec)).Take(3).ToList();

                foreach (var (p, rec) in top)
                {
                    rec.Claims.Add(Tapestry);

                    if (PlayerManager.GetOnlinePlayer(p.Guid.Full) is Player online)
                    {
                        FraysManager.SaveRecord(online, rec);
                        FraysManager.Tell(online, $"You finished last week in the top 3 of the Fray {name} board. A Fray Tapestry waits for you at the Fray-Warden.");
                    }
                    else if (p is OfflinePlayer offline)
                    {
                        offline.SetProperty(PropertyString.FraysState, rec.ToJson());
                        offline.SaveBiotaToDatabase();
                    }
                }

                if (top.Count > 0)
                    log.Info($"[FRAYS] week {week} {name} top 3: {string.Join(", ", top.Select(t => t.Player.Name))}");
            }

            AwardScoreTitles(week, players);
        }

        /// <summary>Pure: the weekly score Epithets, first to third (score_titles).</summary>
        public static List<string> ScoreTitles() =>
            FraysSettings.Text(FraysSettings.ScoreTitles).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3).ToList();

        /// <summary>
        /// Pure: takes last week's score Epithets off a record and gives <paramref name="place"/>'s one (0 first; -1 none).
        /// True when the record changed.
        /// </summary>
        public static bool SetScoreTitle(FraysPlayerRecord rec, IReadOnlyList<string> titles, int place)
        {
            rec.Epithets ??= new List<string>();
            var changed = rec.Epithets.RemoveAll(e => titles.Contains(e)) > 0;

            if (titles.Contains(rec.Epithet ?? ""))
            {
                rec.Epithet = "";
                changed = true;
            }

            if (place >= 0 && place < titles.Count)
            {
                rec.Epithets.Add(titles[place]);
                rec.Epithet = titles[place];
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// The week just ended: the top 3 of the weekly Fray score board (/top fray score week) hold a
        /// weekly Epithet through the next week; last week's holders lose theirs.
        /// </summary>
        private static void AwardScoreTitles(int week, List<IPlayer> players)
        {
            var titles = ScoreTitles();

            if (titles.Count == 0)
                return;

            var all = players.Select(p => (Player: p, Rec: FraysPlayerRecord.Parse(p.GetProperty(PropertyString.FraysState)))).ToList();
            var top = all.Where(x => x.Rec.Week == week && x.Rec.WeekBestRunScore > 0)
                .OrderByDescending(x => x.Rec.WeekBestRunScore).Take(3).Select(x => x.Player.Guid.Full).ToList();

            foreach (var (p, rec) in all)
            {
                var place = top.IndexOf(p.Guid.Full);

                if (!SetScoreTitle(rec, titles, place))
                    continue;

                if (PlayerManager.GetOnlinePlayer(p.Guid.Full) is Player online)
                {
                    FraysManager.SaveRecord(online, rec);

                    if (place >= 0)
                        FraysManager.Tell(online, $"You finished last week number {place + 1} on the Fray score board: you are {online.Name} {titles[place]} this week.");
                }
                else if (p is OfflinePlayer offline)
                {
                    offline.SetProperty(PropertyString.FraysState, rec.ToJson());
                    offline.SaveBiotaToDatabase();
                }
            }

            if (top.Count > 0)
                log.Info($"[FRAYS] week {week} score titles: {string.Join(", ", all.Where(x => top.Contains(x.Player.Guid.Full)).Select(x => x.Player.Name))}");
        }

        private static void Broadcast(string text)
        {
            foreach (var p in PlayerManager.GetAllOnline())
                p.Session?.Network.EnqueueSend(new Network.GameMessages.Messages.GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
