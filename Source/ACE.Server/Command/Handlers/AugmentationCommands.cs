using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Read-only progress readouts for the augmentation tracks a character can advance:
    /// "/xpaugs" for the experience augmentations bought with augmentation gems, "/lumaugs" for the
    /// luminance auras bought from Nalicana and the Seers, and "/customaugs" for the fork's own Custom
    /// Dreamweave augmentations. "/augs" and "/aug" forward to any of them, and with no argument print the
    /// Custom Dreamweave section outright.
    ///
    /// The first two readouts split their catalog into Complete and Incomplete. Where several augmentations
    /// compete for one shared cap - the six innate attributes, the seven innate resistances, and the two
    /// auras a Seer offers - they are collapsed into a SINGLE entry carrying that shared cap, because
    /// the individual counts are not what limits the player. Everything else lists on its own line.
    /// </summary>
    public static class AugmentationCommands
    {
        /// <summary>
        /// One printed entry. An entry with Max 1 prints its name alone; anything repeatable prints
        /// "(current/max)" after the name. Details are indented sub-rows under it.
        /// </summary>
        private sealed class AugRow
        {
            public string Name;
            public string Effect;
            public int Current;
            public int Max;
            public List<string> Details;

            public bool Complete => Current >= Max;
        }

        // ====================================================================
        // ================== experience augmentations ========================
        // ====================================================================

        /// <summary>
        /// The six innate-attribute augmentations share ONE cap of 10 across all of them:
        /// AugmentationDevice.VerifyRequirements gates purchases on AugmentationInnateFamily, not on the
        /// per-attribute property, so that family counter is the number a player needs to see.
        /// </summary>
        internal static readonly (AugmentationType Type, string Label)[] InnateAttributes =
        {
            (AugmentationType.Strength,     "Strength"),
            (AugmentationType.Endurance,    "Endurance"),
            (AugmentationType.Coordination, "Coordination"),
            (AugmentationType.Quickness,    "Quickness"),
            (AugmentationType.Focus,        "Focus"),
            (AugmentationType.Self,         "Self"),
        };

        /// <summary>
        /// The seven innate-resistance augmentations likewise share ONE cap, of 2, tracked on
        /// AugmentationResistanceFamily. A player may take two different resistances, or the same one twice.
        /// </summary>
        internal static readonly (AugmentationType Type, string Label)[] InnateResistances =
        {
            (AugmentationType.ResistSlash,    "Slashing"),
            (AugmentationType.ResistPierce,   "Piercing"),
            (AugmentationType.ResistBludgeon, "Bludgeoning"),
            (AugmentationType.ResistAcid,     "Acid"),
            (AugmentationType.ResistFire,     "Fire"),
            (AugmentationType.ResistCold,     "Cold"),
            (AugmentationType.ResistElectric, "Electric"),
        };

        private const int InnateAttributeStep = 5;      // each attribute aug grants +5 innate points
        private const int InnateResistanceStep = 10;    // each resistance aug grants +10% innate resistance

        /// <summary>
        /// Every augmentation gem that is neither an attribute nor a resistance, i.e. everything that owns
        /// its cap outright. Names come from the gem weenies themselves; the caps are read live from
        /// AugmentationDevice.MaxAugs rather than restated here.
        /// </summary>
        internal static readonly (AugmentationType Type, string Name, string Effect)[] SingleXpAugs =
        {
            (AugmentationType.Salvage,            "Ciandra's Essence",            "specializes Salvaging"),
            (AugmentationType.ItemTinkering,      "Yoshi's Essence",              "specializes Item Tinkering"),
            (AugmentationType.ArmorTinkering,     "Jibril's Essence",             "specializes Armor Tinkering"),
            (AugmentationType.MagicItemTinkering, "Celdiseth's Essence",          "specializes Magic Item Tinkering"),
            (AugmentationType.WeaponTinkering,    "Koga's Essence",               "specializes Weapon Tinkering"),
            (AugmentationType.PackSlot,           "Shadow of the Seventh Mule",   "grants an eighth pack slot"),
            (AugmentationType.BurdenLimit,        "Might of the Seventh Mule",    "+20% carrying capacity each"),
            (AugmentationType.DeathItemLoss,      "Clutch of the Miser",          "5 fewer items lost on death each"),
            (AugmentationType.DeathSpellLoss,     "Enduring Enchantment",         "keep your enchantments through death"),
            (AugmentationType.CritProtect,        "Critical Protection",          "chance to nullify a critical hit"),
            (AugmentationType.BonusXP,            "Quick Learner",                "+5% experience from kills"),
            (AugmentationType.BonusSalvage,       "Ciandra's Fortune",            "+25% salvage yield each"),
            (AugmentationType.ImbueChance,        "Charmed Smith",                "+5% imbue success chance each"),
            (AugmentationType.RegenBonus,         "Innate Renewal",               "+100% vital regeneration while sleeping each"),
            (AugmentationType.SpellDuration,      "Archmage's Endurance",         "+20% spell duration each"),
            (AugmentationType.FociCreature,       "Infused Creature Magic",       "cast Creature Enchantment without components"),
            (AugmentationType.FociItem,           "Infused Item Magic",           "cast Item Enchantment without components"),
            (AugmentationType.FociLife,           "Infused Life Magic",           "cast Life Magic without components"),
            (AugmentationType.FociWar,            "Infused War Magic",            "cast War Magic without components"),
            (AugmentationType.FociVoid,           "Infused Void Magic",           "cast Void Magic without components"),
            (AugmentationType.CritChance,         "Eye of the Remorseless",       "+1 critical hit rating"),
            (AugmentationType.CritDamage,         "Hand of the Remorseless",      "+3 critical damage rating"),
            (AugmentationType.Melee,              "Master of the Steel Circle",   "+10 to your melee skills"),
            (AugmentationType.Missile,            "Master of the Focused Eye",    "+10 to your missile skills"),
            (AugmentationType.Magic,              "Master of the Five Fold Path", "+10 to your magic skills"),
            (AugmentationType.Damage,             "Frenzy of the Slayer",         "+3 damage rating"),
            (AugmentationType.DamageResist,       "Iron Skin of the Invincible",  "+3 damage reduction rating"),
            (AugmentationType.AllStats,           "Jack of All Trades",           "+5 to all of your skills"),
        };

        // ====================================================================
        // ============== custom dreamweave augmentations =====================
        // ====================================================================

        /// <summary>
        /// One row of the "Custom Dreamweave Augs" section. Unlike an AugRow this carries no Max: the three
        /// Custom Dreamweave augmentations are UNCAPPED by design (AugmentationDevice.MaxAugs holds
        /// int.MaxValue for each), so there is nothing to be complete against.
        ///
        /// Count and Effect are deferred delegates, not values, for two separate reasons. Count needs a
        /// Player. Effect reads the LIVE tunables - so the percentages printed here can never drift from the
        /// percentages the effects actually apply - and a PropertyManager read in a static field initializer
        /// would run at class load, which THROWS in a unit-test context on a cache miss (the same trap
        /// PickupSpeed.cs's class remark records). The tests below touch Type and Name only.
        /// </summary>
        internal sealed class CustomAugDefinition
        {
            /// <summary>
            /// The AugmentationType this row counts, or NULL for the shipped Quickhand pick-up boon, which
            /// is a quest boon (a PickupBoon_* quest registry row) rather than an augmentation and so has no
            /// AugmentationType and no MaxAugs entry. Every non-null Type here must be uncapped.
            /// </summary>
            public AugmentationType? Type;
            public string Name;
            public Func<Player, int> Count;
            public Func<string> Effect;
        }

        /// <summary>
        /// The four Custom Dreamweave rows, in the order they print. Three are the augmentations bought from
        /// Fi / Bo / Nacci; the fourth is the already-shipped Quickhand quest boon, listed here because it is
        /// the same pick-up speed number a player is trying to read and splitting it across two commands
        /// would be worse than listing a non-augmentation in an augmentation list.
        ///
        /// The mule row is the one hardcoded magnitude: 100 entries per augmentation is a code constant
        /// (CustomAugBroker.MuleSpaceEntriesPerAug), not a tunable, so it is read from there. Every other
        /// magnitude comes from its live tunable.
        /// </summary>
        internal static readonly CustomAugDefinition[] CustomDreamweaveAugs =
        {
            new CustomAugDefinition
            {
                Type = AugmentationType.MuleSpace,
                Name = "Nacci's Vault Expansion",
                // Account-wide in BOTH directions: every character on the account contributes to this count
                // and every character on the account gets the entries. CustomAugBroker.AccountAugCount is
                // the same sum the vault's own effective cap is computed from, so the two cannot disagree.
                Count = player => CustomAugBroker.AccountAugCount(player.Account?.AccountId ?? 0, PropertyInt.AugmentationMuleSpace),
                Effect = () => $"+{CustomAugBroker.MuleSpaceEntriesPerAug} mule vault entries each, account-wide",
            },
            new CustomAugDefinition
            {
                Type = null,
                Name = "Quickhand Boon",
                Count = player => player.PickupBoonCount,
                Effect = () => $"+{PickupSpeed.PercentBonus(1, PropertyManager.GetDouble("pickup_speed_quest_bonus").Item)}% pick-up speed each (quest boon)",
            },
            new CustomAugDefinition
            {
                Type = AugmentationType.PickupSpeedCustom,
                Name = "Bo's Quickened Grasp",
                Count = player => player.AugmentationPickupSpeed,
                Effect = () => $"+{PickupSpeed.PercentBonus(1, CustomAugBroker.PickupSpeedBonus)}% pick-up speed each",
            },
            new CustomAugDefinition
            {
                Type = AugmentationType.SpellDurationCustom,
                Name = "Fi's Lingering Casting",
                Count = player => player.AugmentationSpellDurationCustom,
                Effect = () => $"+{(int)Math.Round(SanitizeFraction(CustomAugBroker.SpellDurationBonus) * 100)}% spell duration each",
            },
        };

        /// <summary>
        /// Mirrors CustomAugmentations.SpellDurationMultiplier's treatment of a non-finite or negative
        /// per-augmentation fraction (both become 0), so the printed percentage matches the applied one even
        /// when the tunable is mis-set.
        /// </summary>
        private static double SanitizeFraction(double value)
        {
            if (!double.IsFinite(value) || value < 0)
                return 0;

            return value;
        }

        // ====================================================================
        // =================== luminance augmentations ========================
        // ====================================================================

        /// <summary>
        /// A Seer aura and its matching base aura SHARE one property: Nalicana raises it from 0 to 5, and
        /// the Seer continues the SAME property from 5 to 10 (verified against the world emote chains -
        /// e.g. Nalicana gates on LumAugDamageRating_0..5 while Liam of Gelid gates on _5-5.._10). So a
        /// base aura's own rank is the first 5 of that property, and the Seer's is whatever sits above 5.
        /// </summary>
        internal const int LumAuraMaxRank = 5;

        internal static readonly (string Name, PropertyInt Prop, int Max, string Effect)[] BaseLumAugs =
        {
            ("Aura of Valor",           PropertyInt.LumAugDamageRating,          LumAuraMaxRank, "+1 damage rating each"),
            ("Aura of Protection",      PropertyInt.LumAugDamageReductionRating, LumAuraMaxRank, "+1 damage reduction rating each"),
            ("Aura of Glory",           PropertyInt.LumAugCritDamageRating,      LumAuraMaxRank, "+1 critical damage rating each"),
            ("Aura of Temperance",      PropertyInt.LumAugCritReductionRating,   LumAuraMaxRank, "+1 critical damage reduction rating each"),
            ("Aura of Purity",          PropertyInt.LumAugHealingRating,         LumAuraMaxRank, "+1 healing rating each"),
            ("Aura of Aetheric Vision", PropertyInt.LumAugSurgeChanceRating,     LumAuraMaxRank, "better chance of an Aetheria surge"),
            ("Aura of Mana Flow",       PropertyInt.LumAugItemManaUsage,         LumAuraMaxRank, "your items consume less mana"),
            ("Aura of Mana Infusion",   PropertyInt.LumAugItemManaGain,          LumAuraMaxRank, "mana stones give your items more mana"),
            ("Aura of the Craftsman",   PropertyInt.LumAugSkilledCraft,          LumAuraMaxRank, "+1 to your crafting and tinkering skills each"),
            ("Aura of the World",       PropertyInt.LumAugAllSkills,             10,             "+1 to all of your skills each"),
        };

        /// <summary>
        /// Luminance spent on skill credits is tracked as quest solves rather than a property.
        /// </summary>
        private const string SkillCreditQuest = "LumAugSkillQuest";
        internal const int MaxSkillCreditAugs = 2;

        /// <summary>
        /// The four Seers, the two auras each offers, and the quest flag each stamps on a follower.
        /// Swearing to a Seer erases the other three flags (each Seer carries EraseQuest actions for its
        /// three rivals), so at most one of these is ever set.
        ///
        /// SharedWithBaseAura marks the auras that continue a Nalicana aura's property from 5 to 10;
        /// Aura of Specialization is Seer-only and runs 0 to 5 on its own property.
        /// </summary>
        internal sealed class SeerDefinition
        {
            public string QuestName;
            public string DisplayName;
            public (string Name, PropertyInt Prop, bool SharedWithBaseAura)[] Auras;
        }

        internal static readonly SeerDefinition[] Seers =
        {
            new SeerDefinition
            {
                QuestName = "LoyalToLiamOfGelid",
                DisplayName = "Liam of Gelid",
                Auras = new[]
                {
                    ("Aura of Destruction", PropertyInt.LumAugDamageRating,     true),
                    ("Aura of Retribution", PropertyInt.LumAugCritDamageRating, true),
                },
            },
            new SeerDefinition
            {
                QuestName = "LoyalToKahiri",
                DisplayName = "Ka'hiri",
                Auras = new[]
                {
                    ("Aura of Destruction",    PropertyInt.LumAugDamageRating, true),
                    ("Aura of Specialization", PropertyInt.LumAugSkilledSpec,  false),
                },
            },
            new SeerDefinition
            {
                QuestName = "LoyalToShadeOfLadyAdja",
                DisplayName = "Shade of Lady Adja",
                Auras = new[]
                {
                    ("Aura of Invulnerability", PropertyInt.LumAugDamageReductionRating, true),
                    ("Aura of Specialization",  PropertyInt.LumAugSkilledSpec,           false),
                },
            },
            new SeerDefinition
            {
                QuestName = "LoyalToLordTyragar",
                DisplayName = "Lord Tyragar",
                Auras = new[]
                {
                    ("Aura of Invulnerability", PropertyInt.LumAugDamageReductionRating, true),
                    ("Aura of Hardening",       PropertyInt.LumAugCritReductionRating,   true),
                },
            },
        };

        // ====================================================================
        // ========================= commands =================================
        // ====================================================================

        [CommandHandler("xpaugs", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Lists your experience augmentations, split into complete and incomplete",
            "")]
        public static void HandleXpAugs(Session session, params string[] parameters)
        {
            SendReadout(session, "Experience Augmentations", BuildXpAugRows(session.Player));
        }

        [CommandHandler("lumaugs", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Lists your luminance augmentations, split into complete and incomplete",
            "")]
        public static void HandleLumAugs(Session session, params string[] parameters)
        {
            SendReadout(session, "Luminance Augmentations", BuildLumAugRows(session.Player));
        }

        [CommandHandler("customaugs", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Lists your Custom Dreamweave augmentations",
            "")]
        public static void HandleCustomAugs(Session session, params string[] parameters)
        {
            SendUncappedReadout(session, "Custom Dreamweave Augs", BuildCustomAugRows(session.Player));
        }

        [CommandHandler("augs", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Shows an augmentation list. Use /augs xp, /augs lum or /augs custom.",
            "xp | lum | custom")]
        public static void HandleAugs(Session session, params string[] parameters)
        {
            HandleAugsForward(session, parameters);
        }

        [CommandHandler("aug", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Shows an augmentation list. Use /aug xp, /aug lum or /aug custom.",
            "xp | lum | custom")]
        public static void HandleAug(Session session, params string[] parameters)
        {
            HandleAugsForward(session, parameters);
        }

        /// <summary>
        /// "/augs" and "/aug" with an argument run the matching list. With NO argument they signpost the two
        /// long retail lists (which are too long to print unasked) and then print the Custom Dreamweave
        /// section in full - it is four lines, it is fork-specific, and a player has no reason to guess that
        /// a command for it exists.
        /// </summary>
        private static void HandleAugsForward(Session session, string[] parameters)
        {
            var which = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : null;

            switch (which)
            {
                case "xp":
                case "xpaugs":
                case "experience":
                    HandleXpAugs(session);
                    return;

                case "lum":
                case "lumaugs":
                case "luminance":
                    HandleLumAugs(session);
                    return;

                case "custom":
                case "customaugs":
                case "dreamweave":
                    HandleCustomAugs(session);
                    return;
            }

            Send(session, "Use /xpaugs for your experience augmentations, or /lumaugs for your luminance augmentations.");

            HandleCustomAugs(session);
        }

        // ====================================================================
        // ========================== rendering ===============================
        // ====================================================================

        private static void Send(Session session, string message)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }

        private static void SendReadout(Session session, string title, List<AugRow> rows)
        {
            // One defensive normalisation for the whole readout: nothing in normal play can put a
            // property outside [0, Max], but a dev command can, and "(11/10)" or "(-5/5)" in a player's
            // chat window is worse than the clamped truth. Both live splits that DO carry meaning - a
            // base aura's first 5 ranks, and a Seer aura's ranks above 5 - are computed before this.
            foreach (var row in rows)
                row.Current = Math.Clamp(row.Current, 0, row.Max);

            var complete = rows.Where(row => row.Complete).ToList();
            var incomplete = rows.Where(row => !row.Complete).ToList();

            Send(session, $"--- {title} --- {complete.Count} of {rows.Count} complete");

            SendSection(session, "Complete:", complete);
            SendSection(session, "Incomplete:", incomplete);
        }

        /// <summary>
        /// A FLAT readout with no Complete/Incomplete split and no clamp, for augmentations that have no
        /// cap to be complete against.
        ///
        /// It is a separate renderer rather than a flag on SendReadout deliberately. SendReadout clamps
        /// Current to Max and then splits on Current >= Max; with Max = int.MaxValue every Custom Dreamweave
        /// row would sit permanently under "Incomplete", which reads as a defect rather than as "uncapped".
        /// </summary>
        private static void SendUncappedReadout(Session session, string title, List<AugRow> rows)
        {
            Send(session, $"--- {title} ---");

            foreach (var row in rows)
            {
                var line = $"  {row.Name} ({row.Current})";

                if (!string.IsNullOrEmpty(row.Effect))
                    line += $" - {row.Effect}";

                Send(session, line);
            }
        }

        private static void SendSection(Session session, string heading, List<AugRow> rows)
        {
            if (rows.Count == 0)
                return;

            Send(session, heading);

            foreach (var row in rows)
            {
                // The AC chat font is proportional, so nothing here pads for column alignment.
                var line = $"  {row.Name}";

                if (row.Max > 1)
                    line += $" ({row.Current}/{row.Max})";

                if (!string.IsNullOrEmpty(row.Effect))
                    line += $" - {row.Effect}";

                Send(session, line);

                if (row.Details == null)
                    continue;

                foreach (var detail in row.Details)
                    Send(session, $"      {detail}");
            }
        }

        // ====================================================================
        // ======================== row building ==============================
        // ====================================================================

        private static int GetAugRank(Player player, AugmentationType type)
        {
            return player.GetProperty(AugmentationDevice.AugProps[type]) ?? 0;
        }

        private static List<AugRow> BuildXpAugRows(Player player)
        {
            var rows = new List<AugRow>();

            // Only attributes the player has actually augmented get a sub-row - a character with three
            // Strength augs wants to read "+15 Strength", not six lines of which five say zero.
            var attributeDetails = new List<string>();

            foreach (var (type, label) in InnateAttributes)
            {
                var ranks = GetAugRank(player, type);

                if (ranks > 0)
                    attributeDetails.Add($"+{ranks * InnateAttributeStep} {label}");
            }

            rows.Add(new AugRow
            {
                Name = "Innate Attributes",
                Effect = $"+{InnateAttributeStep} to an innate attribute each",
                Current = player.AugmentationInnateFamily,
                Max = AugmentationDevice.MaxAugs[AugmentationType.Strength],
                Details = attributeDetails,
            });

            var resistanceDetails = new List<string>();

            foreach (var (type, label) in InnateResistances)
            {
                var ranks = GetAugRank(player, type);

                if (ranks > 0)
                    resistanceDetails.Add($"+{ranks * InnateResistanceStep}% {label}");
            }

            rows.Add(new AugRow
            {
                Name = "Innate Resistances",
                Effect = $"+{InnateResistanceStep}% innate resistance each",
                Current = player.AugmentationResistanceFamily,
                Max = AugmentationDevice.MaxAugs[AugmentationType.ResistSlash],
                Details = resistanceDetails,
            });

            foreach (var (type, name, effect) in SingleXpAugs)
            {
                rows.Add(new AugRow
                {
                    Name = name,
                    Effect = effect,
                    Current = GetAugRank(player, type),
                    Max = AugmentationDevice.MaxAugs[type],
                });
            }

            // Asheron's Lesser Benediction is bought with experience like the gems above, but it applies a
            // permanent enchantment instead of setting an Augmentation* property, so it is read off the
            // enchantment registry. The two siblings listed beside it on the wiki - Asheron's Benediction
            // and Blackmoor's Favor - are not obtainable here and are deliberately absent from this list.
            rows.Add(new AugRow
            {
                Name = "Asheron's Lesser Benediction",
                Effect = "a permanent enchantment",
                Current = player.EnchantmentManager.HasSpell((uint)SpellId.AsheronsLesserBenediction) ? 1 : 0,
                Max = 1,
            });

            return rows;
        }

        /// <summary>
        /// Builds the Custom Dreamweave rows. Max is left at 0 and is never printed by SendUncappedReadout -
        /// these augmentations are uncapped, and a "(3/2147483647)" would be worse than no denominator.
        /// </summary>
        private static List<AugRow> BuildCustomAugRows(Player player)
        {
            var rows = new List<AugRow>();

            foreach (var definition in CustomDreamweaveAugs)
            {
                rows.Add(new AugRow
                {
                    Name = definition.Name,
                    Effect = definition.Effect(),
                    Current = definition.Count(player),
                });
            }

            return rows;
        }

        private static List<AugRow> BuildLumAugRows(Player player)
        {
            var rows = new List<AugRow>();

            foreach (var (name, prop, max, effect) in BaseLumAugs)
            {
                // Clamped, because the four ratings shared with a Seer aura carry that aura's ranks above
                // 5 in the same property - those belong to the Seer entry below, not to this one.
                var ranks = Math.Min(player.GetProperty(prop) ?? 0, max);

                rows.Add(new AugRow { Name = name, Effect = effect, Current = ranks, Max = max });
            }

            rows.Add(new AugRow
            {
                Name = "Skill Credit",
                Effect = "+1 skill credit each",
                Current = Math.Min(player.QuestManager.GetCurrentSolves(SkillCreditQuest), MaxSkillCreditAugs),
                Max = MaxSkillCreditAugs,
            });

            rows.Add(BuildSeerRow(player));

            return rows;
        }

        /// <summary>
        /// The Seer auras are one entry, not two: a character may follow exactly one Seer, and the pair of
        /// auras that Seer offers is only finished when both sit at 5 of 5.
        /// </summary>
        private static AugRow BuildSeerRow(Player player)
        {
            var seer = Seers.FirstOrDefault(candidate => player.QuestManager.HasQuest(candidate.QuestName));

            if (seer == null)
            {
                return new AugRow
                {
                    Name = "Seer Auras",
                    Effect = $"swear to one Seer for two more auras - {string.Join(", ", Seers.Select(candidate => candidate.DisplayName))}",
                    Current = 0,
                    Max = LumAuraMaxRank * 2,
                };
            }

            var details = new List<string>();
            var total = 0;

            foreach (var (name, prop, sharedWithBaseAura) in seer.Auras)
            {
                var value = player.GetProperty(prop) ?? 0;
                var ranks = sharedWithBaseAura ? value - LumAuraMaxRank : value;

                ranks = Math.Clamp(ranks, 0, LumAuraMaxRank);
                total += ranks;

                details.Add($"{name} ({ranks}/{LumAuraMaxRank})");
            }

            return new AugRow
            {
                Name = $"Seer Auras - {seer.DisplayName}",
                Current = total,
                Max = LumAuraMaxRank * seer.Auras.Length,
                Details = details,
            };
        }
    }
}
