using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Frays
{
    /// <summary>
    /// One Ember Boon: a power a camp picks on its climb. A generic boon is as strong as the player's rank in it (their
    /// collection, raised by Sigils); a class boon makes one of the player's class abilities stronger, more so the higher
    /// their rank in that ability. Every boon has a cost, which does not grow with rank.
    /// </summary>
    public sealed class FraysBoonDef
    {
        public string Key;
        public string Name;
        /// <summary>What the boon plan sorts by: aoe, single, sustain, embers, heat, snarl.</summary>
        public string Tag;
        public bool Rare;

        /// <summary>The effect text; "{0}" is the value at the rank (a percent). Text is the rank 1 text.</summary>
        public string Effect;

        /// <summary>Its value at rank 1 (0.10 = 10%); 0 for an on/off boon.</summary>
        public double Base;

        /// <summary>Puts the boon on the modifiers at a value (Base at rank 1, more at higher ranks).</summary>
        public Action<FraysCampMods, double> Grow;

        /// <summary>The cost text ("you take 3% more damage") and its effect on the modifiers; the same at every rank.</summary>
        public string Cost;
        public Action<FraysCampMods> Pay;

        /// <summary>A class boon: the class ability it strengthens (offered only to a player who has it). Null for a generic boon.</summary>
        public ClassAbilityId? Ability;

        /// <summary>The highest rank Sigils can raise it to: 4 for a common, 2 for a rare, 1 for an on/off one or a class boon.</summary>
        public int MaxRank = 4;

        public bool IsClass => Ability != null;

        /// <summary>
        /// A class boon's effect as the player sees it in their ability (a bare "+60% to summons" reads as far
        /// more than it is, since the percent is of the ability's own bonus): from the player's rank in the ability and the
        /// boon's value, "Executioner's damage ... goes from +21% to +33.6%". Null for a boon without one.
        /// </summary>
        public Func<int, double, string> Real;

        /// <summary>Generic boons: rank 1. Class boons: at the ability's top rank, which the text names.</summary>
        public string Text => IsClass && Real != null ? TextForAbilityRank(AbilityMaxRank, named: true) : TextAt(1);

        public int AbilityMaxRank => Ability != null && ClassAbilityRegistry.Abilities.TryGetValue(Ability.Value, out var def) ? Math.Max(1, def.MaxRank) : 3;

        public double ValueAt(int rank) => Base * (1 + 0.5 * (Math.Clamp(rank, 1, Math.Max(1, MaxRank)) - 1));

        /// <summary>The text at a rank: a generic boon's collection rank, or a class boon's band (shown at the band's top ability rank).</summary>
        public string TextAt(int rank)
        {
            if (IsClass && Real != null)
            {
                var max = AbilityMaxRank;
                var top = Enumerable.Range(1, max).Where(r => FraysBoons.ClassBand(r, max) == Math.Clamp(rank, 1, 3)).DefaultIfEmpty(max).Max();
                return TextForAbilityRank(top);
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture, Effect, (ValueAt(rank) * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%")
                + (string.IsNullOrEmpty(Cost) ? "" : "; but " + Cost);
        }

        /// <summary>A class boon's text for a player with <paramref name="abilityRank"/> in its ability.</summary>
        public string TextForAbilityRank(int abilityRank, bool named = false)
        {
            if (!IsClass || Real == null)
                return TextAt(1);

            var r = Math.Clamp(abilityRank, 1, AbilityMaxRank);
            var effect = Real(r, ValueAt(FraysBoons.ClassBand(r, AbilityMaxRank)));
            return (named ? $"at {FraysBoons.AbilityName(this)} rank {r}, " : "") + effect + (string.IsNullOrEmpty(Cost) ? "" : "; but " + Cost);
        }

        public void Apply(FraysCampMods m, int rank = 1)
        {
            Grow?.Invoke(m, ValueAt(rank));
            Pay?.Invoke(m);
        }
    }

    /// <summary>
    /// Ember Boons: a Torghast-like climb, pushing toward the next level and unlocking perks
    /// along the way. Every boon_every Heat levels a camp is offered three it does not hold: one or two class boons for
    /// class abilities the player has (when they have any), the rest generic. A player at the keyboard picks with /fp 1, 2
    /// or 3; after boon_auto_seconds the server picks from their boon order and plan, or "balanced", which takes the class
    /// boon for their highest-rank ability, so an unattended camp never waits. Boons last until the camp ends. Rare boons are
    /// offered from rare_boon_heat. No boon touches XP or luminance (those are tuned against Threads).
    ///
    /// For a Torghast feel without the snowball, every boon has a cost, and the costs are sized
    /// so each boon is worth about the same. Boon damage and reduction stay inside boon_bonus_cap. Effects
    /// are passive; none moves the character. Old saved boons keep their keys, so saved camps, orders and Sigil ranks load as
    /// they were.
    /// </summary>
    public static class FraysBoons
    {
        public static readonly string[] Tags = { "aoe", "single", "sustain", "embers", "heat", "snarl" };

        private static FraysBoonDef B(string key, string name, string tag, double v, string effect, Action<FraysCampMods, double> grow,
            string cost, Action<FraysCampMods> pay, bool rare = false, int max = 0) =>
            new FraysBoonDef { Key = key, Name = name, Tag = tag, Base = v, Effect = effect, Grow = grow, Cost = cost, Pay = pay, Rare = rare, MaxRank = max > 0 ? max : rare ? 2 : 4 };

        /// <summary>A class boon: <paramref name="v"/> more of one ability at the lowest rank band, 1.5x and 2x at the higher ones.</summary>
        private static FraysBoonDef C(string key, string name, string tag, ClassAbilityId ability, double v, string effect, (string Text, Action<FraysCampMods> Pay) cost,
            Func<int, double, string> real = null) =>
            new FraysBoonDef
            {
                Key = key, Name = name, Tag = tag, Base = v, Effect = effect, Ability = ability, MaxRank = 3,
                Grow = (m, x) => m.AbilityBoost[ability] = (m.AbilityBoost.TryGetValue(ability, out var had) ? had : 1) * (1 + x),
                Cost = cost.Text,
                Pay = cost.Pay,
                Real = real,
            };

        /// <summary>A tunable's value, or its default when the settings are not loaded (tests).</summary>
        private static double Prop(string key, double fallback)
        {
            try { return ACE.Server.Managers.PropertyManager.GetDouble(key, fallback).Item; }
            catch { return fallback; }
        }

        /// <summary>"Executioner's damage ... goes from +21% to +33.6%": a value as the ability has it and with the boon.</summary>
        private static string Rise(string what, double value, double boon, bool plus = true)
        {
            string F(double v) => (plus ? "+" : "") + (v * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
            return $"{what} goes from {F(value)} to {F(value * (1 + boon))}";
        }

        // Class boon costs: each has its own, so the offers do not all read "you take more damage". Sized like the generic boons' costs. Most
        // land at +1 to +3% kills an hour; Scatter Sparks (+4 to +6%) and Void Ember (+4%) took bigger costs, and Blood Embers
        // (-2.5% on a build that takes no damage) went to a damage-taken cost, which only bites where its healing matters.
        // Crimson Splash (+23%) and Hecatomb's Reach (+49%) stay above the rest: the Blood Mage catch-up.
        private static (string, Action<FraysCampMods>) TakenCost(double c) => ($"you take {P(c)} more damage", Taken(c));
        private static (string, Action<FraysCampMods>) DealtCost(double c) => ($"{P(c)} less damage dealt", Dealt(c));
        private static (string, Action<FraysCampMods>) PacksCost(double c) => ($"{P(c)} less damage to pack creatures", ToPacks(c));
        private static (string, Action<FraysCampMods>) GildedCost(double c) => ($"{P(c)} less damage to Gilded creatures", ToGilded(c));
        private static (string, Action<FraysCampMods>) SnarlCost(double c) => ($"{P(c)} less damage to the Snarl", ToSnarl(c));
        private static (string, Action<FraysCampMods>) EmbersCost(double c) => ($"{P(c)} fewer Embers", FewerEmbers(c));

        // The costs, by their size.
        private static Action<FraysCampMods> Taken(double c) => m => m.DamageTaken *= 1 + c;
        private static Action<FraysCampMods> Dealt(double c) => m => m.DamageDealt *= 1 - c;
        private static Action<FraysCampMods> ToGilded(double c) => m => m.GildedDamage *= 1 - c;
        private static Action<FraysCampMods> ToPacks(double c) => m => m.PackDamage *= 1 - c;
        private static Action<FraysCampMods> ToSnarl(double c) => m => m.SnarlDamage *= 1 - c;
        private static Action<FraysCampMods> FewerEmbers(double c) => m => m.Embers *= 1 - c;

        private static string P(double c) => $"{c * 100:0.#}%";

        public static readonly IReadOnlyList<FraysBoonDef> All = new List<FraysBoonDef>
        {
            // Damage and reduction boons: added into the Frays pools, where boons give at most
            // boon_bonus_cap (+10%) against any one kind of target and 10% reduction together.
            B("wildfire", "Wildfire Heart", "aoe", 0.05 / 2.5, "+{0} damage to pack creatures", (m, v) => m.BoonPack += v,
                $"{P(0.02)} less damage to Gilded creatures", ToGilded(0.02)),
            B("packmaster", "Packmaster", "aoe", 0.03, "about 10% more creatures an hour, +{0} Embers", (m, v) => { m.PackShare += FraysSettings.Number(FraysSettings.PackmasterPackShare); m.Embers *= 1 + v; },
                $"{P(0.05)} less damage dealt", Dealt(0.05)),   // as Bellows, out of the rest scaling; 5% (10% was a loss on most builds)
            B("kindledblade", "Kindled Blade", "single", 0.03 / 2.5, "+{0} damage", (m, v) => m.BoonDamage += v,
                $"you take {P(0.01)} more damage", Taken(0.01)),
            B("huntercoal", "Hunter's Coal", "single", 0.10 / 2.5, "+{0} damage to Gilded creatures", (m, v) => m.BoonGilded += v,
                $"{P(0.01)} less damage to pack creatures", ToPacks(0.01)),
            B("emberskin", "Ember Skin", "sustain", 0.05 / 2.5, "{0} less damage taken", (m, v) => m.BoonReduction += v,
                $"{P(0.01)} less damage dealt", Dealt(0.01)),
            B("ironember", "Iron Ember", "sustain", 0.05 / 2.5, "{0} less physical damage taken", (m, v) => m.BoonPhysicalReduction += v,
                $"{P(0.008)} less damage dealt", Dealt(0.008)),
            B("ashenmend", "Ashen Mend", "sustain", 0.03, "heals {0} of your health every 10 seconds", (m, v) => m.HealPer10s += v,
                $"{P(0.02)} less damage dealt", Dealt(0.02)),
            B("brightwick", "Bright Wick", "sustain", 0.01, "restores {0} of your mana every 5 seconds", (m, v) => m.ManaPer5s += v,
                $"{P(0.01)} less damage dealt", Dealt(0.01)),
            B("coalpurse", "Coal Purse", "embers", 0.08, "+{0} Embers", (m, v) => m.Embers *= 1 + v,
                $"{P(0.05)} less damage dealt", Dealt(0.05)),   // 5%: a 10% cost cost about twice what its +8% Embers pay back
            B("slowcoals", "Slow Coals", "heat", 0.30, "Heat decays {0} slower", (m, v) => m.DecayMult *= Math.Max(0, 1 - v),
                $"{P(0.03)} fewer Embers", FewerEmbers(0.03)),
            B("snarlbait", "Snarl Bait", "snarl", 0.15, "your Snarl's bar fills {0} faster", (m, v) => m.SnarlBarSpeed *= 1 + v,
                $"{P(0.04)} fewer Embers", FewerEmbers(0.04)),   // costs Embers: a less-damage-to-the-Snarl cost would make each Snarl fight longer and cancel the faster bar
            B("grudgefire", "Grudge Fire", "snarl", 0.10 / 2.5, "+{0} damage to the Snarl", (m, v) => m.BoonSnarl += v,
                $"{P(0.01)} less damage to pack creatures", ToPacks(0.01)),

            B("infernocore", "Inferno Core", "single", 0.05 / 1.5, "+{0} damage", (m, v) => m.BoonDamage += v,
                $"you take {P(0.02)} more damage", Taken(0.02), rare: true),
            B("firestorm", "Firestorm", "aoe", 0.10 / 1.5, "+{0} damage to pack creatures", (m, v) => m.BoonPack += v,
                $"{P(0.03)} less damage to Gilded creatures", ToGilded(0.03), rare: true),
            B("phoenixcoal", "Phoenix Coal", "sustain", 0, "a death costs one Heat less", (m, v) => m.DeathDropLevels -= 1,
                $"you take {P(0.03)} more damage", Taken(0.03), rare: true, max: 1),
            B("goldencinders", "Golden Cinders", "embers", 0.15, "+{0} Embers", (m, v) => m.Embers *= 1 + v,
                $"{P(0.07)} less damage dealt", Dealt(0.07), rare: true),   // no loot step; a damage-taken cost never bit below Heat 17, and 15% was too steep, so 7%
            B("undyingflame", "Undying Flame", "heat", 0, "your Heat never decays", (m, v) => m.NoDecay = true,
                $"{P(0.05)} fewer Embers", FewerEmbers(0.05), rare: true, max: 1),
            B("snarlsbane", "Snarl's Bane", "snarl", 0.35, "your Snarl's bar fills {0} faster", (m, v) => m.SnarlBarSpeed *= 1 + v,
                $"{P(0.07)} fewer Embers", FewerEmbers(0.07), rare: true),   // costs Embers: a 15% less-damage-to-the-Snarl cost gave slow Snarl killers no extra Snarls

            // Class boons: offered only for a class ability the player has; the value grows with their rank in it.
            // Sizing: Venom Wick, Spellfire Edge and Scatter Sparks gave +13
            // to +32% kills an hour at 0.25 and Runefire +7 to +10%, so they come down to about +3% at the top band.
            C("c_frenzy", "Kindled Frenzy", "single", ClassAbilityId.Frenzy, 0.25, "Frenzy's attack speed per stack +{0}", TakenCost(0.03),
                (r, x) => Rise("Frenzy's attack speed at full stacks", Prop("class_ability_frenzy_percent_per_stack", 0.025) * ACE.Server.ClassAbilities.Abilities.FrenzyAbility.StackCap(r), x)),
            C("c_bloodlust", "Blood Embers", "sustain", ClassAbilityId.Bloodlust, 0.30, "Bloodlust's healing +{0}", TakenCost(0.03),
                (r, x) => Rise("the share of your melee damage Bloodlust heals", r * Prop("class_ability_bloodlust_percent_per_rank", 0.03), x, plus: false)),
            C("c_executioner", "Coal Executioner", "single", ClassAbilityId.Executioner, 0.30, "Executioner's bonus +{0}", SnarlCost(0.03),
                (r, x) => Rise("Executioner's damage against creatures under 25% health", r * Prop("class_ability_executioner_percent_per_rank", 0.07), x)),
            C("c_poison", "Venom Wick", "single", ClassAbilityId.PoisonWeapon, 0.03, "Poison Weapon's damage +{0}", PacksCost(0.01),
                (r, x) => { var a = r * Prop("class_ability_poisonweapon_damage_per_rank", 12.0); return $"Poison Weapon's damage a hit goes from {a:0.#} to {a * (1 + x):0.#}, and Acid Proc's ticks with it"; }),
            C("c_acid", "Caustic Ash", "single", ClassAbilityId.AcidProc, 0.25, "Acid Proc's chance +{0}", GildedCost(0.02),
                (r, x) => Rise("Acid Proc's chance", Prop("class_ability_acidproc_chance_base", 0.30), x, plus: false)),
            C("c_opportunist", "Ember Opening", "single", ClassAbilityId.Opportunist, 0.30, "Opportunist's bonus +{0}", EmbersCost(0.03),
                (r, x) => Rise("Opportunist's damage against debuffed or held creatures", r * Prop("class_ability_opportunist_percent_per_rank", 0.02), x)),
            C("c_multishot", "Scatter Sparks", "aoe", ClassAbilityId.Multishot, 0.04, "Multishot's extra arrows hit +{0} harder", GildedCost(0.06),
                (r, x) => Rise("each extra Multishot arrow's damage (of a full arrow)", Prop("class_ability_multishot_damage_mult", 0.6), x, plus: false)),
            C("c_thorns", "Burning Thorns", "sustain", ClassAbilityId.Thorns, 0.30, "Thorns reflect +{0}", DealtCost(0.01),
                (r, x) => Rise("the share of your shield's armor Thorns reflects", r * Prop("class_ability_thorns_percent_per_rank", 0.05), x, plus: false)),
            C("c_echocast", "Echoing Flame", "aoe", ClassAbilityId.EchoCast, 0.25, "Echo Cast's chance +{0}", GildedCost(0.03),
                (r, x) => Rise("Echo Cast's chance", Prop("class_ability_echocast_chance_base", 0.08) + (r - 1) * Prop("class_ability_echocast_chance_step", 0.08), x, plus: false)),
            C("c_cascade", "Cascading Cinders", "aoe", ClassAbilityId.Cascade, 0.25, "Cascade's chance +{0}", EmbersCost(0.03),
                (r, x) => Rise("Cascade's chance", Prop($"class_ability_cascade_chance_r{Math.Clamp(r, 1, 3)}", 0.05 * Math.Clamp(r, 1, 3)), x, plus: false)),
            C("c_spellblade", "Spellfire Edge", "single", ClassAbilityId.Spellblade, 0.04, "Spellblade's chance +{0}", PacksCost(0.01),
                (r, x) => Rise("Spellblade's chance", Prop("class_ability_spellblade_chance", 0.25), x, plus: false)),
            C("c_runeblade", "Runefire", "single", ClassAbilityId.Runeblade, 0.09, "Runeblade's chance +{0}", TakenCost(0.03),
                (r, x) => Rise("Runeblade's chance", Prop("class_ability_runeblade_chance", 0.22), x, plus: false)),
            C("c_sundermark", "Sundered Coal", "single", ClassAbilityId.Sundermark, 0.14, "Sundermark's chance +{0}", SnarlCost(0.03),
                (r, x) => Rise("Sundermark's chance", Prop("class_ability_sundermark_chance_base", 0.07) + (r - 1) * Prop("class_ability_sundermark_chance_step", 0.06), x, plus: false)),
            C("c_voiddamage", "Void Ember", "single", ClassAbilityId.VoidDamage, 0.30, "Void Damage's bonus +{0}", PacksCost(0.02),
                (r, x) => Rise("Void Damage's bonus to void spells", r * Prop("class_ability_voiddamage_percent_per_rank", 0.08), x)),
            C("c_hemomancy", "Blood Kindling", "single", ClassAbilityId.Hemomancy, 0.30, "Hemomancy's bonus +{0}", EmbersCost(0.03),
                (r, x) => Rise("Hemomancy's damage bonus", r * Prop("class_ability_hemomancy_damage_percent_per_rank", 0.02), x)),
            // A Blood Mage with summons earned a third of the others' XP and was offered no
            // class boon at all, so these are boons for the Blood Mage and summoner abilities such a caster holds. Live only while camping.
            C("c_sanguine", "Banked Embers", "single", ClassAbilityId.SanguineReserve, 0.30, "each Blood Charge's damage +{0}", TakenCost(0.03),
                (r, x) => Rise("the life magic damage a full Blood Charge pool adds", (Math.Clamp(r, 1, 3) + 2) * Prop("class_ability_bloodmage_charge_per_stack", 0.07), x)),
            C("c_maledict", "Smoldering Curse", "single", ClassAbilityId.Malediction, 0.30, "Malediction's intensity +{0}", SnarlCost(0.03),
                (r, x) => Rise("how much stronger Malediction makes your Vulnerabilities and Imperils", Prop("class_ability_malediction_intensity_base", 0.10) + (r - 1) * Prop("class_ability_malediction_intensity_step", 0.10), x)),
            C("c_bloodprice", "Ember Tithe", "single", ClassAbilityId.BloodPrice, 0.30, "Blood Price's bonus +{0}", GildedCost(0.02),
                (r, x) => Rise("the spell damage Blood Price adds", Prop($"class_ability_bloodprice_damage_r{Math.Clamp(r, 1, 3)}", 0.10 * Math.Clamp(r, 1, 3)), x)),
            C("c_thralls", "Kindled Thralls", "aoe", ClassAbilityId.EmpoweredSummons, 0.30, "Empowered Summons' bonus +{0}", GildedCost(0.03),
                (r, x) => Rise("your summons' health, damage and defenses", r * Prop("class_ability_empoweredsummons_percent_per_rank", 0.0833), x)),
            // Blood Mage AoE (Blood Mage falls outside the equal-worth rule to catch up). Sanguine
            // Reserve, the Blood Mage's first game-changer, is what offers them. See FraysBloodBoons.
            new FraysBoonDef
            {
                Key = "c_bloodsplash", Name = "Crimson Splash", Tag = "aoe", Base = 1, Ability = ClassAbilityId.SanguineReserve, MaxRank = 3,
                Effect = "your drains, Bloodstone Bolts and Hecatombs also strike nearby creatures (1, 2 or 3 more by your Sanguine Reserve rank)",
                Grow = (m, x) => m.BloodSplash = Math.Max(m.BloodSplash, (int)Math.Round(2 * x - 1)),
                Cost = "you take 3% more damage", Pay = Taken(0.03),
            },
            new FraysBoonDef
            {
                Key = "c_hecatomb", Name = "Hecatomb's Reach", Tag = "aoe", Base = 0, Ability = ClassAbilityId.SanguineReserve, MaxRank = 3,
                Effect = "Martyr's Hecatomb and Curse of Raven Fury also strike every creature near their target",
                Grow = (m, x) => m.HecatombReach = true,
                Cost = "3% less damage to Gilded creatures", Pay = ToGilded(0.03),
            },
        };

        private static readonly Dictionary<string, FraysBoonDef> byKey = All.ToDictionary(b => b.Key);

        public static FraysBoonDef Find(string key) => key != null && byKey.TryGetValue(key, out var b) ? b : null;

        /// <summary>
        /// Pure: a class boon's band from the player's rank in its ability: 1 to 3, the top band at the ability's max rank
        /// (a 3-rank ability: its rank; a 5-rank one: 1, 2-3, 4-5). 0 when they do not have it.
        /// </summary>
        public static int ClassBand(int abilityRank, int abilityMax)
        {
            if (abilityRank <= 0)
                return 0;

            var max = Math.Max(1, abilityMax);
            return Math.Clamp((int)Math.Ceiling(3.0 * Math.Min(abilityRank, max) / max), 1, 3);
        }

        /// <summary>The player's band in a class boon's ability (0 without it), from <paramref name="abilityRank"/> (their rank lookup).</summary>
        public static int ClassBand(FraysBoonDef b, Func<ClassAbilityId, int> abilityRank)
        {
            if (b?.Ability == null || abilityRank == null)
                return 0;

            var max = ClassAbilityRegistry.Abilities.TryGetValue(b.Ability.Value, out var def) ? def.MaxRank : 3;
            return ClassBand(abilityRank(b.Ability.Value), max);
        }

        /// <summary>The rank a boon works at: a class boon's band, or the collection rank for a generic one.</summary>
        public static int RankFor(FraysBoonDef b, Func<string, int> collectionRank, Func<ClassAbilityId, int> abilityRank) =>
            b == null ? 0 : b.IsClass ? ClassBand(b, abilityRank) : Math.Max(1, collectionRank?.Invoke(b.Key) ?? 1);

        /// <summary>Pure: a climb from <paramref name="from"/> to <paramref name="to"/> crosses a boon level (every <paramref name="every"/>).</summary>
        /// <summary>Pure: the offers a camp starting at <paramref name="start"/> is owed for the boon levels at or below it.</summary>
        public static int StartOffers(int start, int every) => OffersCrossed(0, start, every);

        public static int OffersCrossed(int from, int to, int every) => every <= 0 || to <= from ? 0 : Math.Max(0, to / every - Math.Max(0, from) / every);

        /// <summary>
        /// Pure: three boons to offer (fewer when few are left), none already held; rares only from <paramref name="rareFrom"/>.
        /// One or two are class boons for abilities the player has (<paramref name="abilityRank"/> above 0), the rest generic;
        /// with no such ability, all three are generic.
        /// </summary>
        public static List<string> Offer(IReadOnlyCollection<string> held, int heat, int rareFrom, Random rng, Func<ClassAbilityId, int> abilityRank = null)
        {
            var classPool = All.Where(b => b.IsClass && !held.Contains(b.Key) && ClassBand(b, abilityRank) > 0).ToList();
            var pool = All.Where(b => !b.IsClass && !held.Contains(b.Key) && (!b.Rare || heat >= rareFrom)).ToList();
            var pick = new List<string>();

            void Take(List<FraysBoonDef> from, int n)
            {
                while (n-- > 0 && from.Count > 0 && pick.Count < 3)
                {
                    var b = from[rng.Next(from.Count)];
                    pick.Add(b.Key);
                    from.Remove(b);
                }
            }

            Take(classPool, classPool.Count == 0 ? 0 : rng.Next(1, 3));
            Take(pool, 3 - pick.Count);
            Take(classPool, 3 - pick.Count);   // too few generic left: more class boons fill in

            // shuffled, so a class boon is not always first
            return pick.OrderBy(_ => rng.Next()).ToList();
        }

        /// <summary>
        /// Pure: the "balanced" choice from an offer: the class boon for the player's highest-rank ability (its rank as a
        /// share of the ability's max, then the band), then a rare, then the first offered.
        /// </summary>
        public static string BalancedPick(IReadOnlyList<string> offer, Func<ClassAbilityId, int> abilityRank)
        {
            var defs = offer.Select(Find).Where(b => b != null).ToList();

            double Share(FraysBoonDef b)
            {
                if (!b.IsClass || abilityRank == null)
                    return -1;

                var max = ClassAbilityRegistry.Abilities.TryGetValue(b.Ability.Value, out var def) ? def.MaxRank : 3;
                var r = abilityRank(b.Ability.Value);
                return r <= 0 ? -1 : (double)Math.Min(r, max) / Math.Max(1, max);
            }

            return defs.OrderByDescending(Share).ThenByDescending(b => b.Rare).Select(b => b.Key).FirstOrDefault() ?? (offer.Count > 0 ? offer[0] : null);
        }

        /// <summary>
        /// Pure: the boon the plan picks from an offer. The first tag in the plan that any offered boon has wins (a rare, then
        /// the class boon for the highest-rank ability, over the rest of that tag); with no plan (or no match), "balanced".
        /// </summary>
        public static string PlanPick(IReadOnlyList<string> offer, IReadOnlyList<string> plan, Func<ClassAbilityId, int> abilityRank = null)
        {
            if (offer == null || offer.Count == 0)
                return null;

            foreach (var tag in plan ?? Array.Empty<string>())
            {
                var tagged = offer.Where(k => Find(k)?.Tag == tag).ToList();

                if (tagged.Count == 0)
                    continue;

                var rare = tagged.FirstOrDefault(k => Find(k).Rare);
                return rare ?? BalancedPick(tagged, abilityRank);
            }

            return BalancedPick(offer, abilityRank);
        }

        /// <summary>Pure: a plan from what a player typed ("aoe, sustain embers"); unknown words are dropped. Empty = balanced.</summary>
        public static List<string> ParsePlan(IEnumerable<string> words) =>
            (words ?? Array.Empty<string>()).SelectMany(w => w.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(w => w.Trim().ToLowerInvariant()).Where(w => Tags.Contains(w)).Distinct().ToList();

        public static string PlanText(IReadOnlyList<string> plan) => plan == null || plan.Count == 0 ? "balanced" : string.Join(", ", plan);

        /// <summary>
        /// Pure: the boon an auto-pick takes (the boon order window). The highest-listed boon in
        /// <paramref name="order"/> that is offered wins; with none of them offered, the type plan (PlanPick) decides.
        /// </summary>
        public static string AutoPick(IReadOnlyList<string> offer, IReadOnlyList<string> order, IReadOnlyList<string> plan, Func<ClassAbilityId, int> abilityRank = null)
        {
            if (offer == null || offer.Count == 0)
                return null;

            foreach (var key in order ?? Array.Empty<string>())
                if (offer.Contains(key))
                    return key;

            return PlanPick(offer, plan, abilityRank);
        }

        /// <summary>The rule an auto-pick follows, for the offer lines: "your boon order, then aoe, sustain".</summary>
        public static string PickRuleText(IReadOnlyList<string> order, IReadOnlyList<string> plan) =>
            order != null && order.Count > 0 ? $"your boon order, then {PlanText(plan)}" : PlanText(plan);

        /// <summary>
        /// Pure: an order from what a player typed: window numbers (1 to All.Count, as /fray boons lists them), boon keys or
        /// names ("wildfire", "kindled blade"). Unknown words and repeats are dropped. Numbers and names can be mixed.
        /// </summary>
        public static List<string> ParseOrder(IEnumerable<string> words)
        {
            var text = string.Join(" ", words ?? Array.Empty<string>()).Replace(",", " ");
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim().ToLowerInvariant()).ToList();
            var order = new List<string>();

            void Add(string key)
            {
                if (key != null && !order.Contains(key))
                    order.Add(key);
            }

            for (var i = 0; i < tokens.Count; i++)
            {
                if (int.TryParse(tokens[i], out var n))
                {
                    if (n >= 1 && n <= All.Count)
                        Add(All[n - 1].Key);
                    continue;
                }

                // A name may be several words ("hunter's coal"): try the longest run that names a boon.
                FraysBoonDef hit = null;
                var used = 0;

                for (var len = Math.Min(3, tokens.Count - i); len >= 1 && hit == null; len--)
                {
                    var joined = string.Concat(tokens.Skip(i).Take(len)).Replace("'", "");
                    hit = All.FirstOrDefault(b => b.Key == joined || b.Name.ToLowerInvariant().Replace(" ", "").Replace("'", "") == joined);
                    used = len;
                }

                if (hit != null)
                {
                    Add(hit.Key);
                    i += used - 1;
                }
            }

            return order;
        }

        /// <summary>"1. Wildfire Heart (aoe): +4% damage to pack creatures; but ..." - one line, at the window's number.</summary>
        public static string Line(FraysBoonDef b) =>
            $"{All.ToList().IndexOf(b) + 1}. {b.Name} ({b.Tag}{(b.Rare ? ", rare" : "")}{(b.IsClass ? ", class: " + AbilityName(b) : "")}): {b.Text}";

        public static string AbilityName(FraysBoonDef b) =>
            b?.Ability != null && ClassAbilityRegistry.Abilities.TryGetValue(b.Ability.Value, out var def) ? def.DisplayName : b?.Ability?.ToString();

        /// <summary>The order as a list for the window and chat: "1st Wildfire Heart, 2nd Ember Skin", or "none set".</summary>
        public static string OrderText(IReadOnlyList<string> order) =>
            order == null || order.Count == 0 ? "none set" : string.Join(", ", order.Select((k, i) => $"{i + 1}. {Find(k)?.Name ?? k}"));

        /// <summary>
        /// The camp's boons on its modifiers (each camp tick, after the long-term ones): a generic boon at the player's
        /// collection rank, a class boon at their band in its ability (none when they do not have the ability).
        /// </summary>
        public static void Apply(IEnumerable<string> held, FraysCampMods m, Func<string, int> rankOf = null, Func<ClassAbilityId, int> abilityRank = null)
        {
            foreach (var key in held ?? Enumerable.Empty<string>())
            {
                var b = Find(key);

                if (b == null)
                    continue;

                var rank = RankFor(b, rankOf, abilityRank);

                if (rank > 0)
                    b.Apply(m, rank);
            }
        }

        /// <summary>The centre-screen line for an open offer: the three names and the seconds left.</summary>
        public static string ShortOffer(IReadOnlyList<string> offer, double secondsLeft) =>
            "Ember Boon: " + string.Join(" | ", offer.Select((k, i) => $"/fp {i + 1} {Find(k)?.Name ?? k}")) + $" ({Math.Max(0, secondsLeft):0} s)";

        /// <summary>"1. Name (rare): text.  2. ..." for an offer, each at the rank it would work at for this player.</summary>
        public static string OfferText(IReadOnlyList<string> offer, Func<string, int> rankOf = null, Func<ClassAbilityId, int> abilityRank = null) =>
            string.Join("  ", offer.Select((k, i) => Find(k) is FraysBoonDef b
                ? $"{i + 1}. {b.Name}{(b.Rare ? " (rare)" : "")}{(b.IsClass ? $" ({AbilityName(b)})" : "")}: {(b.IsClass && abilityRank != null ? b.TextForAbilityRank(abilityRank(b.Ability.Value)) : b.TextAt(Math.Max(1, RankFor(b, rankOf, abilityRank))))}."
                : ""));
    }
}
