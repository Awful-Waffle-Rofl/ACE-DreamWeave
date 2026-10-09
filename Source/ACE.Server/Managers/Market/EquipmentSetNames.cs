using System.Collections.Generic;

using ACE.Entity.Enum;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// GENERATED DATA. Retail item-set display names, extracted from acclient.exe's appraisal string
    /// table (a contiguous string pool at file offset 0x003B0628-0x003B1088, bytes 3868200-3870868 of
    /// `C:\Turbine\Asheron's Call\acclient.exe`, ending immediately before the "Set: " literal the
    /// appraisal panel prefixes a set name with) and matched to <see cref="EquipmentSet"/> members by
    /// normalized-name equality (lowercase, letters/digits only, with a trailing "Set"/"_New" suffix
    /// stripped from the enum member name before comparing).
    ///
    /// Extraction method: a scratch PowerShell script read the exe as Latin-1 text
    /// (Encoding.GetEncoding(28591)) and ran the regex [\x20-\x7E]{3,} over that byte range to recover
    /// every printable run. The same byte range was confirmed byte-identical (same SHA256 for the
    /// whole file) between `C:\Turbine\Asheron's Call\acclient.exe` and
    /// `C:\Turbine\_acclient-backup-2026-08-25\acclient.exe`.
    ///
    /// Most entries below are an EXACT normalized match. A handful are NOT exact and are called out
    /// individually because the mapping was inferred rather than read directly off matching text. Six
    /// of those were corroborated (2026-09-12) against local `ace_world`: each set id's actual items,
    /// grouped by weenie name (`weenie_properties_string` type 1 [Name]) on
    /// `weenie_properties_int` type 265 [EquipmentSetId]. Ids per `Source/ACE.Entity/Enum/EquipmentSet.cs`.
    ///
    ///   - EquipmentSet.Ninja_New -> "Shou-jen Shozoku": the enum's own comment ("// Shou-Jen Shozoku
    ///     Set") directly above the member already established this identity before this catalog
    ///     existed (MarketEquipmentSet.Label's pre-existing override used the same string).
    ///   - EquipmentSet.Ninja -> "Shou-jen": the exe carries a standalone "Shou-jen" string alongside
    ///     "Shou-jen Shozoku"; by the same base/upgraded-variant naming pattern as Ninja/Ninja_New,
    ///     "Shou-jen" is Ninja's un-upgraded counterpart. DB-corroborated: every set-8 item is a
    ///     "Shou-jen ..." piece (Jika-Tabi, Shozoku Jacket/Mask/Sleeve Gauntlets/Trousers).
    ///   - EquipmentSet.AetheriaDefense/AetheriaDestruction/AetheriaFury/AetheriaGrowth/AetheriaVigor
    ///     -> "Sigil of Defense"/"Destruction"/"Fury"/"Growth"/"Vigor": the exe has exactly one "Sigil
    ///     of X" string per Aetheria suffix word, a clean 5-for-5 pairing with no other candidate on
    ///     either side, but the prefix ("Sigil of" vs "Aetheria") does not match textually.
    ///   - EquipmentSet.RareDamageResistance -> "Heroic Protector", EquipmentSet.RareDamageBoost ->
    ///     "Heroic Destroyer": a semantic pairing (protector = resistance, destroyer = damage boost)
    ///     rather than a text match. DB-corroborated: every set-40 item is armor or a shield (Bracers/
    ///     Breastplate/Gauntlets/Girth/Greaves/Helm/Pauldrons/Tassets of Leikotha's Tears, Dread
    ///     Marauder Shield, Gelidite armor, Shield of Engorgement, ...); every set-41 item is a weapon
    ///     (2H Revenant Scythe, Black Cloud Bow, Bloodmark Crossbow, staves, axes, daggers, ...) - a
    ///     defense/offense split that matches "Protector"/"Destroyer" respectively.
    ///   - EquipmentSet.ArmorPerfectLight -> "Coat of Perfect Light": the exe has exactly two matching
    ///     strings ("Coat of Perfect Light", "Leggings of Perfect Light") with no textual way to tell
    ///     which id is which piece. DB-corroborated: set-11's only items are "Empowered Bracers/
    ///     Breastplate/Pauldrons of the Perfect Light" - a chest/arms/shoulders (coat) piece set.
    ///   - EquipmentSet.ArmorPerfectLight2 -> "Leggings of Perfect Light": set-12's only items are
    ///     "Empowered Girth/Greaves/Tassets of the Perfect Light" - a waist/legs (leggings) piece set,
    ///     confirming the other half of the ArmorPerfectLight/2 pairing.
    ///   - EquipmentSet.ColosseumClothing -> "Gladiatorial Clothing": set-31's only items are
    ///     "Gladiatorial Leggings"/"Gladiatorial Tunic" - a Colosseum is where gladiatorial combat
    ///     happens, and the DB confirms the item names literally say "Gladiatorial".
    ///   - EquipmentSet.GraveyardClothing -> "Ceremonial Clothing": set-32's only items are
    ///     "Ceremonial Leggings"/"Ceremonial Tunic" - confirmed by exact item-name match, though the
    ///     "Graveyard"/"Ceremonial" association itself is inferred (funerary rites), not textual.
    ///   - EquipmentSet.OlthoiClothing -> "Protective Clothing": set-33's only items are "Protective
    ///     Leggings"/"Protective Tunic" - confirmed by exact item-name match; the "Olthoi"/"Protective"
    ///     association is inferred, not textual (no item name mentions Olthoi).
    ///   - EquipmentSet.CarraidasBenediction -> "Carraida's Benediction": the exe string appears
    ///     immediately AFTER the "Set: " literal itself, inside "Set:    Carraida's Benediction  This
    ///     item adds %d Vitality" - originally excluded here as likely a documentation/example string
    ///     rather than a live data-table entry. Included on reconsideration: it is still an EXACT
    ///     normalized match on a real appraisal-string-pool string, and no local `ace_world` item
    ///     carries EquipmentSetId 4 to corroborate or refute it either way - the DB is silent on this
    ///     one, not contradictory.
    ///
    /// Deliberately EXCLUDED, with reasoning (do not add these later without re-verifying):
    ///   - EquipmentSet.SocietyArmor: local `ace_world`'s only set-30 items ("Awfully OP ...",
    ///     "Celestial Hand ...", "Eldrytch Web ...", "Meridian ...", "Radiant Blood ...") are fork
    ///     test/custom gear, not retail items - no retail name evidence exists for this id here.
    ///   - EquipmentSet.NoobieArmor: no retail name evidence.
    ///   - "Dedication" (the exe string): no enum member it can be justified against.
    ///
    /// Every other EquipmentSet member (ids 30, 34, 42-48, 57, 67, 74, 75, 77, 79, 131-140) has no
    /// candidate string anywhere in this region (or, for 30/34, no retail evidence) and keeps today's
    /// Label fallback (the Olthoi Armor / Shou-jen Shozoku overrides, then the spaced token) unchanged.
    /// </summary>
    public static class EquipmentSetNames
    {
        public static readonly IReadOnlyDictionary<EquipmentSet, string> Names = new Dictionary<EquipmentSet, string>
        {
            { EquipmentSet.CarraidasBenediction, "Carraida's Benediction" },
            { EquipmentSet.NobleRelic, "Noble Relic" },
            { EquipmentSet.AncientRelic, "Ancient Relic" },
            { EquipmentSet.AlduressaRelic, "Alduressa Relic" },
            { EquipmentSet.Ninja, "Shou-jen" },
            { EquipmentSet.EmpyreanRings, "Empyrean Rings" },
            { EquipmentSet.ArmMindHeart, "Arm, Mind, Heart" },
            { EquipmentSet.ArmorPerfectLight, "Coat of Perfect Light" },
            { EquipmentSet.ArmorPerfectLight2, "Leggings of Perfect Light" },

            { EquipmentSet.Soldiers, "Soldier's" },
            { EquipmentSet.Adepts, "Adept's" },
            { EquipmentSet.Archers, "Archer's" },
            { EquipmentSet.Defenders, "Defender's" },
            { EquipmentSet.Tinkers, "Tinker's" },
            { EquipmentSet.Crafters, "Crafter's" },
            { EquipmentSet.Hearty, "Hearty" },
            { EquipmentSet.Dexterous, "Dexterous" },
            { EquipmentSet.Wise, "Wise" },
            { EquipmentSet.Swift, "Swift" },
            { EquipmentSet.Hardened, "Hardened" },
            { EquipmentSet.Reinforced, "Reinforced" },
            { EquipmentSet.Interlocking, "Interlocking" },
            { EquipmentSet.ColosseumClothing, "Gladiatorial Clothing" },
            { EquipmentSet.GraveyardClothing, "Ceremonial Clothing" },
            { EquipmentSet.OlthoiClothing, "Protective Clothing" },
            { EquipmentSet.Flameproof, "Flame Proof" },
            { EquipmentSet.Acidproof, "Acid Proof" },
            { EquipmentSet.Coldproof, "Cold Proof" },
            { EquipmentSet.Lightningproof, "Lightning Proof" },

            { EquipmentSet.AetheriaDefense, "Sigil of Defense" },
            { EquipmentSet.AetheriaDestruction, "Sigil of Destruction" },
            { EquipmentSet.AetheriaFury, "Sigil of Fury" },
            { EquipmentSet.AetheriaGrowth, "Sigil of Growth" },
            { EquipmentSet.AetheriaVigor, "Sigil of Vigor" },
            { EquipmentSet.RareDamageResistance, "Heroic Protector" },
            { EquipmentSet.RareDamageBoost, "Heroic Destroyer" },

            { EquipmentSet.CloakAlchemy, "Weave of Alchemy" },
            { EquipmentSet.CloakArcaneLore, "Weave of Arcane Lore" },
            { EquipmentSet.CloakArmorTinkering, "Weave of Armor Tinkering" },
            { EquipmentSet.CloakAssessPerson, "Weave of Assess Person" },
            { EquipmentSet.CloakLightWeapons, "Weave of Light Weapons" },
            { EquipmentSet.CloakMissileWeapons, "Weave of Missile Weapons" },
            { EquipmentSet.CloakCooking, "Weave of Cooking" },
            { EquipmentSet.CloakCreatureEnchantment, "Weave of Creature Enchantment" },
            { EquipmentSet.CloakFinesseWeapons, "Weave of Finesse Weapons" },
            { EquipmentSet.CloakDeception, "Weave of Deception" },
            { EquipmentSet.CloakFletching, "Weave of Fletching" },
            { EquipmentSet.CloakHealing, "Weave of Healing" },
            { EquipmentSet.CloakItemEnchantment, "Weave of Item Enchantment" },
            { EquipmentSet.CloakItemTinkering, "Weave of Item Tinkering" },
            { EquipmentSet.CloakLeadership, "Weave of Leadership" },
            { EquipmentSet.CloakLifeMagic, "Weave of Life Magic" },
            { EquipmentSet.CloakLoyalty, "Weave of Loyalty" },
            { EquipmentSet.CloakMagicDefense, "Weave of Magic Defense" },
            { EquipmentSet.CloakMagicItemTinkering, "Weave of Magic Item Tinkering" },
            { EquipmentSet.CloakManaConversion, "Weave of Mana Conversion" },
            { EquipmentSet.CloakMeleeDefense, "Weave of Melee Defense" },
            { EquipmentSet.CloakMissileDefense, "Weave of Missile Defense" },
            { EquipmentSet.CloakSalvaging, "Weave of Salvaging" },
            { EquipmentSet.CloakHeavyWeapons, "Weave of Heavy Weapons" },
            { EquipmentSet.CloakTwoHandedCombat, "Weave of Two Handed Combat" },
            { EquipmentSet.CloakVoidMagic, "Weave of Void Magic" },
            { EquipmentSet.CloakWarMagic, "Weave of War Magic" },
            { EquipmentSet.CloakWeaponTinkering, "Weave of Weapon Tinkering" },
            { EquipmentSet.CloakAssessCreature, "Weave of Assess Creature" },
            { EquipmentSet.CloakDirtyFighting, "Weave of Dirty Fighting" },
            { EquipmentSet.CloakDualWield, "Weave of Dual Wield" },
            { EquipmentSet.CloakRecklessness, "Weave of Recklessness" },
            { EquipmentSet.CloakShield, "Weave of Shield" },
            { EquipmentSet.CloakSneakAttack, "Weave of Sneak Attack" },
            { EquipmentSet.Ninja_New, "Shou-jen Shozoku" },
            { EquipmentSet.CloakSummoning, "Weave of Summoning" },

            { EquipmentSet.ShroudedSoul, "Shrouded Soul" },
            { EquipmentSet.DarkenedMind, "Darkened Mind" },
            { EquipmentSet.CloudedSpirit, "Clouded Spirit" },

            { EquipmentSet.MinorStingingShroudedSoul, "Minor Stinging Shrouded Soul" },
            { EquipmentSet.MinorSparkingShroudedSoul, "Minor Sparking Shrouded Soul" },
            { EquipmentSet.MinorSmolderingShroudedSoul, "Minor Smoldering Shrouded Soul" },
            { EquipmentSet.MinorShiveringShroudedSoul, "Minor Shivering Shrouded Soul" },
            { EquipmentSet.MinorStingingDarkenedMind, "Minor Stinging Darkened Mind" },
            { EquipmentSet.MinorSparkingDarkenedMind, "Minor Sparking Darkened Mind" },
            { EquipmentSet.MinorSmolderingDarkenedMind, "Minor Smoldering Darkened Mind" },
            { EquipmentSet.MinorShiveringDarkenedMind, "Minor Shivering Darkened Mind" },
            { EquipmentSet.MinorStingingCloudedSpirit, "Minor Stinging Clouded Spirit" },
            { EquipmentSet.MinorSparkingCloudedSpirit, "Minor Sparking Clouded Spirit" },
            { EquipmentSet.MinorSmolderingCloudedSpirit, "Minor Smoldering Clouded Spirit" },
            { EquipmentSet.MinorShiveringCloudedSpirit, "Minor Shivering Clouded Spirit" },
            { EquipmentSet.MajorStingingShroudedSoul, "Major Stinging Shrouded Soul" },
            { EquipmentSet.MajorSparkingShroudedSoul, "Major Sparking Shrouded Soul" },
            { EquipmentSet.MajorSmolderingShroudedSoul, "Major Smoldering Shrouded Soul" },
            { EquipmentSet.MajorShiveringShroudedSoul, "Major Shivering Shrouded Soul" },
            { EquipmentSet.MajorStingingDarkenedMind, "Major Stinging Darkened Mind" },
            { EquipmentSet.MajorSparkingDarkenedMind, "Major Sparking Darkened Mind" },
            { EquipmentSet.MajorSmolderingDarkenedMind, "Major Smoldering Darkened Mind" },
            { EquipmentSet.MajorShiveringDarkenedMind, "Major Shivering Darkened Mind" },
            { EquipmentSet.MajorStingingCloudedSpirit, "Major Stinging Clouded Spirit" },
            { EquipmentSet.MajorSparkingCloudedSpirit, "Major Sparking Clouded Spirit" },
            { EquipmentSet.MajorSmolderingCloudedSpirit, "Major Smoldering Clouded Spirit" },
            { EquipmentSet.MajorShiveringCloudedSpirit, "Major Shivering Clouded Spirit" },
            { EquipmentSet.BlackfireStingingShroudedSoul, "Blackfire Stinging Shrouded Soul" },
            { EquipmentSet.BlackfireSparkingShroudedSoul, "Blackfire Sparking Shrouded Soul" },
            { EquipmentSet.BlackfireSmolderingShroudedSoul, "Blackfire Smoldering Shrouded Soul" },
            { EquipmentSet.BlackfireShiveringShroudedSoul, "Blackfire Shivering Shrouded Soul" },
            { EquipmentSet.BlackfireStingingDarkenedMind, "Blackfire Stinging Darkened Mind" },
            { EquipmentSet.BlackfireSparkingDarkenedMind, "Blackfire Sparking Darkened Mind" },
            { EquipmentSet.BlackfireSmolderingDarkenedMind, "Blackfire Smoldering Darkened Mind" },
            { EquipmentSet.BlackfireShiveringDarkenedMind, "Blackfire Shivering Darkened Mind" },
            { EquipmentSet.BlackfireStingingCloudedSpirit, "Blackfire Stinging Clouded Spirit" },
            { EquipmentSet.BlackfireSparkingCloudedSpirit, "Blackfire Sparking Clouded Spirit" },
            { EquipmentSet.BlackfireSmolderingCloudedSpirit, "Blackfire Smoldering Clouded Spirit" },
            { EquipmentSet.BlackfireShiveringCloudedSpirit, "Blackfire Shivering Clouded Spirit" },

            { EquipmentSet.ShimmeringShadowsSet, "Shimmering Shadows" },
        };
    }
}
