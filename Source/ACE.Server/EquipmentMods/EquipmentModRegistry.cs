using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Factories.Entity;

namespace ACE.Server.EquipmentMods
{
    /// <summary>
    /// The equipment-mod table: one row per mod, and the only place a mod's magnitude is written down.
    ///
    /// Adding a mod is deliberately a ONE-ROW change - append a <see cref="PropertyFloat"/> in the reserved
    /// 8100-8199 band, append an <see cref="EquipmentModId"/>, add one row here. Machinery mods additionally
    /// read their term inside the ability handler that already fires for them; nothing is ever added to a core
    /// combat site per mod. That is the scalability contract (plan decision 13) and it is the reason this file
    /// is a flat table rather than a handler hierarchy like ClassAbilityRegistry.
    ///
    /// POTENCY, NOT MAGNITUDE. An item stores only a potency scalar in [0, 1] on the mod's PropertyFloat.
    /// <see cref="EquipmentModDefinition.MaxMagnitude"/> here is the effect at a perfect 100% roll, and the
    /// applied value is always resolved as potency x MaxMagnitude x equipment_mod_potency_scale through
    /// <see cref="EquipmentModValue.Resolve"/>. Consequences worth internalizing before editing this file:
    ///   - Retuning a MaxMagnitude below rescales every existing item in the world at once. No shard
    ///     migration, no contaminated rows, no re-authoring of live gear.
    ///   - The safety clamp is trivially [0, 1] and is applied at write AND at read, so a rolled-back or
    ///     hand-edited shard row can never produce an out-of-budget effect.
    ///   - Nothing here may be persisted onto an item. Ever.
    ///
    /// Budget rule (HARD): a single mod at 100% potency is worth no more than 1-2% of late-game player power.
    /// Unconditional damage-stream mods cap at 2.0%; conditional ones (peak-ramp, execute-range) at 3.0%,
    /// because uptime-weighted impact lands back in the 1-1.5% band. Re-run the power-assessor inverse
    /// assessment before changing any magnitude here.
    /// </summary>
    public static class EquipmentModRegistry
    {
        /// <summary>Inclusive lower bound of the PropertyFloat band reserved for equipment mods.</summary>
        public const int PropertyBandStart = 8100;

        /// <summary>Inclusive upper bound of the PropertyFloat band reserved for equipment mods.</summary>
        public const int PropertyBandEnd = 8199;

        private static readonly EquipmentModDefinition[] mods =
        {
            // --- Archer ---
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Deadeye,
                Property = PropertyFloat.GearModDeadeye,
                DisplayName = "Deadeye",
                LinkedAbility = ClassAbilityId.Deadeye,
                Standalone = true,
                MaxMagnitude = 0.028,
                HookKind = EquipmentModHookKind.StreamDamagePercentMissile,
                DisplayFormat = "+{0:0.##}% missile damage",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.EagleEye,
                Property = PropertyFloat.GearModEagleEye,
                DisplayName = "Eagle Eye",
                LinkedAbility = ClassAbilityId.EagleEye,
                Standalone = true,
                MaxMagnitude = 0.02,
                HookKind = EquipmentModHookKind.AbilityCompose,
                DisplayFormat = "+{0:0.##}% effective missile attack skill",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.LongDraw,
                Property = PropertyFloat.GearModLongDraw,
                DisplayName = "Long Draw",
                LinkedAbility = ClassAbilityId.LongDraw,
                Standalone = true,
                MaxMagnitude = 0.03,
                HookKind = EquipmentModHookKind.StreamDamagePercentMissile,
                DisplayFormat = "+{0:0.##}% missile damage at maximum range",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.HeavyDraw,
                Property = PropertyFloat.GearModHeavyDraw,
                DisplayName = "Heavy Draw",
                LinkedAbility = ClassAbilityId.HeavyDraw,
                Standalone = false,
                MaxMagnitude = 0.024,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}% missile damage while Heavy Draw is active",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Splitshot,
                Property = PropertyFloat.GearModSplitshot,
                DisplayName = "Splitshot",
                LinkedAbility = ClassAbilityId.Multishot,
                Standalone = false,
                MaxMagnitude = 0.025,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp extra-arrow damage multiplier",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.DoubleVolley,
                Property = PropertyFloat.GearModDoubleVolley,
                DisplayName = "Double Volley",
                LinkedAbility = ClassAbilityId.DoubleVolley,
                Standalone = false,
                MaxMagnitude = 0.025,
                // Proc-chance mod: capped alongside Acid Proc/Echo Cast/Elemental Rend/Nether Bloom so an
                // uncapped stack cannot push a chance-on-hit ability toward a guarantee. 3.0 = three perfect
                // rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp volley re-fire chance",
            },

            // --- Rogue ---
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Venom,
                Property = PropertyFloat.GearModVenom,
                DisplayName = "Venom",
                LinkedAbility = ClassAbilityId.PoisonWeapon,
                Standalone = true,
                MaxMagnitude = 4.1,
                // poison damage is dealt as an integer, so a roll worth less than half a point rounds away
                // to nothing. Quantization only needs 0.5/4.1 = 0.122 potency to survive half-up rounding;
                // the declared floor of 0.25 is now MORE generous than that, guaranteeing at least
                // 0.25 x 4.1 = 1.025 (rounds to 1.03) flat rather than sitting right at the quantization edge.
                MinPotency = 0.25,
                HookKind = EquipmentModHookKind.FlatPostMitigation,
                DisplayFormat = "+{0:0.##} poison damage per hit",
                DisplayScale = 1.0,
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.AcidProc,
                Property = PropertyFloat.GearModAcidProc,
                DisplayName = "Acid Proc",
                LinkedAbility = ClassAbilityId.AcidProc,
                Standalone = false,
                MaxMagnitude = 0.103,
                // Proc-chance mod: base Acid Proc chance is 40% (AcidProcAbility). Uncapped, an equipped
                // stack of this magnitude reaches 100% at SIX pieces (40% + 6 x 10.3pp = 102%), turning a
                // chance-on-hit ability into a guarantee. StackCap = 3.0 (three perfect rolls) holds the
                // cross-item sum to 40% + 3.0 x 10.3pp = 70.9%, well short of a guarantee at any stack depth.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp acid proc chance",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Caustic,
                Property = PropertyFloat.GearModCaustic,
                DisplayName = "Caustic",
                LinkedAbility = ClassAbilityId.AcidProc,
                Standalone = false,
                MaxMagnitude = 0.256,
                // an acid tick is an integer, and the tick this scales is the Poison Weapon flat (15 at the
                // rank-3 endgame anchor the +15% was priced against). Moving 15 by half a point needs
                // 0.5/15 = 0.0334 applied, i.e. potency 0.0334/0.256 = 0.130 at the new max - the declared
                // floor of 0.25 now comfortably exceeds what quantization requires (it was a tight 0.223
                // rounded up to 0.25 against the old 0.15 max). NOTE this secures the anchor build only: at
                // Poison Weapon rank 1 the base tick is 5, where no potency in [0, 1] can guarantee a nonzero
                // increase (it would need 0.10 applied = potency 0.39 at this max). See the PR body's
                // quantization audit.
                MinPotency = 0.25,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}% acid damage per tick",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Riposte,
                Property = PropertyFloat.GearModRiposte,
                DisplayName = "Riposte",
                LinkedAbility = ClassAbilityId.Riposte,
                Standalone = false,
                MaxMagnitude = 0.10,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp counter-strike weapon damage",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.AttackSpeed,
                Property = PropertyFloat.GearModAttackSpeed,
                DisplayName = "Attack Speed",
                LinkedAbility = ClassAbilityId.AttackSpeed,
                Standalone = true,
                MaxMagnitude = 0.025,
                HookKind = EquipmentModHookKind.AttackSpeed,
                DisplayFormat = "+{0:0.##}% attack speed",
            },

            // --- Vanguard ---
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Thorns,
                Property = PropertyFloat.GearModThorns,
                DisplayName = "Thorns",
                LinkedAbility = ClassAbilityId.Thorns,
                Standalone = true,
                MaxMagnitude = 0.01,
                // reflected damage is an integer. At ability rank 0 the mod is the WHOLE reflect, so a roll
                // worth less than half a point of the shield's armor level does nothing at all. Against the
                // power-assessment endgame anchor of 550 shield AL, half a point needs 0.5/550 = 0.00091
                // applied, i.e. potency 0.00091/0.01 = 0.091 - rounded up to 0.10. Below that anchor (a
                // weaker shield) a low roll can still round away; see the PR body's quantization audit.
                MinPotency = 0.10,
                HookKind = EquipmentModHookKind.ReflectShieldAL,
                DisplayFormat = "+{0:0.##}% of shield armor reflected",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.ShieldCheck,
                Property = PropertyFloat.GearModShieldCheck,
                DisplayName = "Shield Check",
                LinkedAbility = ClassAbilityId.ShieldCheck,
                Standalone = false,
                MaxMagnitude = 0.10,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp thorns-on-parry strength",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Bulwark,
                Property = PropertyFloat.GearModBulwark,
                DisplayName = "Bulwark",
                LinkedAbility = ClassAbilityId.BattleHardened,
                Standalone = true,
                MaxMagnitude = 0.0157,
                HookKind = EquipmentModHookKind.AbilityCompose,
                DisplayFormat = "+{0:0.##}% damage reduction",
            },

            // --- Berserker ---
            new EquipmentModDefinition
            {
                Id = EquipmentModId.FrenziedPace,
                Property = PropertyFloat.GearModFrenziedPace,
                DisplayName = "Frenzied Pace",
                LinkedAbility = ClassAbilityId.Frenzy,
                Standalone = false,
                MaxMagnitude = 0.003,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                // TWO decimals now suffice. This is still the one per-stack mod in the catalog, but raising
                // MaxMagnitude from 0.0015 to 0.003 doubled the whole display range in proportion: the floor
                // (still the catalog default 0.10 potency) now prints 0.03% and a perfect roll prints 0.3%,
                // both legible at two decimals. The old four-decimal format existed to keep the 0.015% floor
                // off "0.0", a problem that no longer exists at double the magnitude.
                DisplayFormat = "+{0:0.##}% attack speed per Frenzy stack",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.LingeringFury,
                Property = PropertyFloat.GearModLingeringFury,
                DisplayName = "Lingering Fury",
                LinkedAbility = ClassAbilityId.Frenzy,
                Standalone = false,
                MaxMagnitude = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}s Frenzy stack window",
                DisplayScale = 1.0,
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.SavageBlows,
                Property = PropertyFloat.GearModSavageBlows,
                DisplayName = "Savage Blows",
                LinkedAbility = ClassAbilityId.SavageBlows,
                Standalone = true,
                MaxMagnitude = 0.027,
                HookKind = EquipmentModHookKind.StreamDamagePercentMelee,
                DisplayFormat = "+{0:0.##}% melee damage",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.BloodFury,
                Property = PropertyFloat.GearModBloodFury,
                DisplayName = "Blood Fury",
                LinkedAbility = ClassAbilityId.BloodFury,
                Standalone = true,
                MaxMagnitude = 0.03,
                HookKind = EquipmentModHookKind.StreamDamagePercentMelee,
                DisplayFormat = "+{0:0.##}% melee damage at lowest health",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Executioner,
                Property = PropertyFloat.GearModExecutioner,
                DisplayName = "Executioner",
                LinkedAbility = ClassAbilityId.Executioner,
                Standalone = true,
                MaxMagnitude = 0.03,
                HookKind = EquipmentModHookKind.StreamDamagePercentMelee,
                DisplayFormat = "+{0:0.##}% melee damage against badly wounded targets",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Bloodlust,
                Property = PropertyFloat.GearModBloodlust,
                DisplayName = "Bloodlust",
                LinkedAbility = ClassAbilityId.Bloodlust,
                Standalone = true,
                MaxMagnitude = 0.005,
                // No DECLARED floor: Bloodlust takes EquipmentModRoller.DefaultMinPotency (0.10) like every
                // other undeclared row. It cannot carry a QUANTIZATION floor - healing is an integer and this
                // is a small percentage of one hit, so the floor that would guarantee a per-hit point is ~0.8
                // at the damage anchor, which would make the mod near-deterministic and gut the roll gamble.
                // What covers quantization here instead is accumulation: the sub-point remainder carries
                // across hits (BloodlustAbility.AccrueHeal), so every roll pays out eventually.
                // The default floor is orthogonal to that and does not disturb it - AccrueHeal is drift-free
                // for any positive gear fraction, so a larger fraction only makes the payout arrive sooner.
                // It is what keeps the appraisal line off "+0%": 0.10 x 0.005 x 100 renders as 0.05%.
                HookKind = EquipmentModHookKind.AbilityCompose,
                DisplayFormat = "+{0:0.##}% of melee damage returned as health",
            },

            // --- Archmage ---
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Overchannel,
                Property = PropertyFloat.GearModOverchannel,
                DisplayName = "Overchannel",
                LinkedAbility = ClassAbilityId.Overchannel,
                Standalone = true,
                MaxMagnitude = 0.028,
                HookKind = EquipmentModHookKind.StreamDamagePercentWar,
                DisplayFormat = "+{0:0.##}% war magic damage",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.EchoCast,
                Property = PropertyFloat.GearModEchoCast,
                DisplayName = "Echo Cast",
                LinkedAbility = ClassAbilityId.EchoCast,
                Standalone = false,
                MaxMagnitude = 0.025,
                // Proc-chance mod: capped alongside Acid Proc/Double Volley/Elemental Rend/Nether Bloom so an
                // uncapped stack cannot push a chance-on-hit ability toward a guarantee. 3.0 = three perfect
                // rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp recast chance",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.ElementalRend,
                Property = PropertyFloat.GearModElementalRend,
                DisplayName = "Elemental Rend",
                LinkedAbility = ClassAbilityId.ElementalRend,
                Standalone = false,
                MaxMagnitude = 0.02,
                // Proc-chance mod: capped alongside Acid Proc/Echo Cast/Double Volley/Nether Bloom so an
                // uncapped stack cannot push a chance-on-hit ability toward a guarantee. 3.0 = three perfect
                // rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp vulnerability proc chance",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Resonance,
                Property = PropertyFloat.GearModResonance,
                DisplayName = "Resonance",
                LinkedAbility = ClassAbilityId.SpellAoe,
                Standalone = false,
                MaxMagnitude = 0.02,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp radiated blast damage",
            },

            // --- Void / Summon ---
            new EquipmentModDefinition
            {
                Id = EquipmentModId.VoidDamage,
                Property = PropertyFloat.GearModVoidDamage,
                DisplayName = "Void Damage",
                LinkedAbility = ClassAbilityId.VoidDamage,
                Standalone = true,
                MaxMagnitude = 0.025,
                HookKind = EquipmentModHookKind.StreamDamagePercentVoid,
                DisplayFormat = "+{0:0.##}% void magic damage",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Withering,
                Property = PropertyFloat.GearModWithering,
                DisplayName = "Withering",
                LinkedAbility = ClassAbilityId.Withering,
                Standalone = true,
                MaxMagnitude = 0.028,
                HookKind = EquipmentModHookKind.StreamDamagePercentVoid,
                DisplayFormat = "+{0:0.##}% void damage per tick",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.EmpoweredSummons,
                Property = PropertyFloat.GearModEmpoweredSummons,
                DisplayName = "Empowered Summons",
                LinkedAbility = ClassAbilityId.EmpoweredSummons,
                Standalone = true,
                MaxMagnitude = 0.087,
                HookKind = EquipmentModHookKind.AbilityCompose,
                DisplayFormat = "+{0:0.##}% summoned pet health, damage and defenses",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.SoulTether,
                Property = PropertyFloat.GearModSoulTether,
                DisplayName = "Soul Tether",
                LinkedAbility = ClassAbilityId.SoulTether,
                Standalone = true,
                MaxMagnitude = 0.03,
                HookKind = EquipmentModHookKind.AbilityCompose,
                DisplayFormat = "+{0:0.##}pp combat pet damage reduction",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.NetherBloom,
                Property = PropertyFloat.GearModNetherBloom,
                DisplayName = "Nether Bloom",
                LinkedAbility = ClassAbilityId.NetherBloom,
                Standalone = false,
                MaxMagnitude = 0.25,
                // Proc-chance mod: capped alongside Acid Proc/Echo Cast/Double Volley/Elemental Rend so an
                // uncapped stack cannot push a chance-on-hit ability toward a guarantee. 3.0 = three perfect
                // rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}% chance of an additional bloom jump",
            },

            // --- Archmage (Mana Barrier, added alongside Void/Summon's Soul Tether and Nether Bloom) ---
            new EquipmentModDefinition
            {
                Id = EquipmentModId.ManaBarrier,
                Property = PropertyFloat.GearModManaBarrier,
                DisplayName = "Mana Barrier",
                LinkedAbility = ClassAbilityId.ManaBarrier,
                Standalone = true,
                MaxMagnitude = 0.03,
                HookKind = EquipmentModHookKind.AbilityCompose,
                DisplayFormat = "+{0:0.##}pp damage diverted from Health to Mana",
            },

            // --- Class-catalog reconciliation (2026-08-04): closes the BloodMage/Spellsword mod gap and
            // corrects Vanguard (Provoke, Bellow, Shield Wall). All 19 rows below are Standalone = false: every linked
            // ability returns 0 or the identity value at rank 0 (Chance() returns 0.0f for rank <= 0;
            // DamageMultiplier returns 1.0f; WardFraction/IntensityBonus return 0.0; TauntAbility.Activate
            // is reached only through an ownership-gated proc), so none of them has a rank-0 formula for a
            // standalone term to stand alone in. See Docs/EquipmentMods/DESIGN.md section 2.3.

            // --- Blood Mage ---
            new EquipmentModDefinition
            {
                Id = EquipmentModId.BloodCharge,
                Property = PropertyFloat.GearModBloodCharge,
                DisplayName = "Blood Charge",
                LinkedAbility = ClassAbilityId.SanguineReserve,
                Standalone = false,
                MaxMagnitude = 0.0054,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}% life magic damage per Blood Charge",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Transfusion,
                Property = PropertyFloat.GearModTransfusion,
                DisplayName = "Transfusion",
                LinkedAbility = ClassAbilityId.Transfusion,
                Standalone = false,
                MaxMagnitude = 0.10,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp of Drain surplus redistributed",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Hemorrhage,
                Property = PropertyFloat.GearModHemorrhage,
                DisplayName = "Hemorrhage",
                LinkedAbility = ClassAbilityId.WeakenedBlood,
                Standalone = false,
                MaxMagnitude = 0.05,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##} life vulnerability on marked targets",
                DisplayScale = 1.0,
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Deepen,
                Property = PropertyFloat.GearModDeepen,
                DisplayName = "Deepen",
                LinkedAbility = ClassAbilityId.Malediction,
                Standalone = false,
                MaxMagnitude = 0.02,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp debuff intensity",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Bloodletting,
                Property = PropertyFloat.GearModBloodletting,
                DisplayName = "Bloodletting",
                LinkedAbility = ClassAbilityId.CrimsonHarvest,
                Standalone = false,
                MaxMagnitude = 1.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}m Crimson Harvest radius",
                DisplayScale = 1.0,
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Sanguinate,
                Property = PropertyFloat.GearModSanguinate,
                DisplayName = "Sanguinate",
                LinkedAbility = ClassAbilityId.Exsanguinate,
                Standalone = false,
                MaxMagnitude = 0.117,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.###}x Exsanguinate burst multiplier",
                DisplayScale = 1.0,
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.BloodPrice,
                Property = PropertyFloat.GearModBloodPrice,
                DisplayName = "Blood Price",
                LinkedAbility = ClassAbilityId.BloodPrice,
                Standalone = false,
                MaxMagnitude = 0.025,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}% spell damage while paying the Blood Price",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Clotting,
                Property = PropertyFloat.GearModClotting,
                DisplayName = "Clotting",
                LinkedAbility = ClassAbilityId.SanguineWard,
                Standalone = false,
                MaxMagnitude = 0.10,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp of the Sanguine Ward absorb",
            },

            // --- Spellsword ---
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Spellblade,
                Property = PropertyFloat.GearModSpellblade,
                DisplayName = "Spellblade",
                LinkedAbility = ClassAbilityId.Spellblade,
                Standalone = false,
                MaxMagnitude = 0.036,
                // Proc-chance mod: capped alongside the other six new proc-chance rows (Runeblade,
                // Sundermark, Surge, Spellstorm, Cascade, Dispelling Edge) so an uncapped stack cannot push
                // a chance-on-hit ability toward a guarantee. 3.0 = three perfect rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp Spellblade proc chance",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Harmonics,
                Property = PropertyFloat.GearModHarmonics,
                DisplayName = "Harmonics",
                // NAMED Harmonics rather than Resonance: EquipmentModId.Resonance (id 24) is already taken
                // by the Archmage Spell AOE mod, and EquipmentModId is append-only, so the existing member
                // cannot be renumbered to free the name. LinkedAbility below is still correctly
                // ClassAbilityId.Resonance - the Spellsword ability of that name - there is no functional
                // collision, only a display-name one. See DESIGN.md section 2.2.
                LinkedAbility = ClassAbilityId.Resonance,
                Standalone = false,
                MaxMagnitude = 0.0048,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}% magic damage per Resonance stack",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Runeblade,
                Property = PropertyFloat.GearModRuneblade,
                DisplayName = "Runeblade",
                LinkedAbility = ClassAbilityId.Runeblade,
                Standalone = false,
                MaxMagnitude = 0.036,
                // Proc-chance mod: capped alongside Spellblade/Sundermark/Surge/Spellstorm/Cascade/
                // Dispelling Edge. 3.0 = three perfect rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp Runeblade proc chance",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Sundermark,
                Property = PropertyFloat.GearModSundermark,
                DisplayName = "Sundermark",
                LinkedAbility = ClassAbilityId.Sundermark,
                Standalone = false,
                MaxMagnitude = 0.015,
                // Proc-chance mod: capped alongside Spellblade/Runeblade/Surge/Spellstorm/Cascade/
                // Dispelling Edge. 3.0 = three perfect rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp Sundermark proc chance",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Surge,
                Property = PropertyFloat.GearModSurge,
                DisplayName = "Surge",
                LinkedAbility = ClassAbilityId.Spellsurge,
                Standalone = false,
                MaxMagnitude = 0.0051,
                // Proc-chance mod: capped alongside Spellblade/Runeblade/Sundermark/Spellstorm/Cascade/
                // Dispelling Edge. 3.0 = three perfect rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.###}pp war proc chance per Spellsurge stack",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Spellstorm,
                Property = PropertyFloat.GearModSpellstorm,
                DisplayName = "Spellstorm",
                LinkedAbility = ClassAbilityId.Spellstorm,
                Standalone = false,
                MaxMagnitude = 0.033,
                // Proc-chance mod: capped alongside Spellblade/Runeblade/Sundermark/Surge/Cascade/
                // Dispelling Edge. 3.0 = three perfect rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp Spellstorm proc chance",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Cascade,
                Property = PropertyFloat.GearModCascade,
                DisplayName = "Cascade",
                LinkedAbility = ClassAbilityId.Cascade,
                Standalone = false,
                MaxMagnitude = 0.025,
                // Proc-chance mod: capped alongside Spellblade/Runeblade/Sundermark/Surge/Spellstorm/
                // Dispelling Edge. 3.0 = three perfect rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp Cascade chain chance",
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.DispellingEdge,
                Property = PropertyFloat.GearModDispellingEdge,
                DisplayName = "Dispelling Edge",
                LinkedAbility = ClassAbilityId.DispellingEdge,
                Standalone = false,
                MaxMagnitude = 0.02,
                // Proc-chance mod: capped alongside Spellblade/Runeblade/Sundermark/Surge/Spellstorm/
                // Cascade. 3.0 = three perfect rolls.
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp Dispelling Edge proc chance",
            },

            // --- Vanguard correction (Provoke, Bellow) ---
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Provoke,
                Property = PropertyFloat.GearModProvoke,
                DisplayName = "Provoke",
                LinkedAbility = ClassAbilityId.Taunt,
                Standalone = false,
                MaxMagnitude = 2.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}s Taunt hold duration",
                DisplayScale = 1.0,
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.Bellow,
                Property = PropertyFloat.GearModBellow,
                DisplayName = "Bellow",
                LinkedAbility = ClassAbilityId.Taunt,
                Standalone = false,
                MaxMagnitude = 1.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}m Taunt radius",
                DisplayScale = 1.0,
            },
            new EquipmentModDefinition
            {
                Id = EquipmentModId.ShieldWall,
                Property = PropertyFloat.GearModShieldWall,
                DisplayName = "Shield Wall",
                LinkedAbility = ClassAbilityId.ShieldBlock,
                Standalone = false,
                MaxMagnitude = 0.0141,
                // Proc-chance mod: capped alongside the other proc-chance rows. 3.0 = three perfect rolls,
                // which is also what makes the headroom argument hold - a rank-3 Vanguard with the Armor
                // Tinkering rider fully capped sits at 20 + 20 = 40pp, and a full 3.0 stack of this row adds
                // 4.23pp for 44.23pp against the 50pp pooled avoidance cap. The mod stays live rather than
                // being absorbed, which is the whole reason ShieldBlockAbility.BlockChance had to gain its
                // affinity clamp before this row could ship (DESIGN.md sections 2.2/2.3).
                StackCap = 3.0,
                HookKind = EquipmentModHookKind.AbilityMachinery,
                DisplayFormat = "+{0:0.##}pp shield block chance",
            },
        };

        /// <summary>
        /// Every mod, in table order. Enumerated by the appraisal display and by the application flow's
        /// duplicate check - both of which must stay registry-driven so a new row needs no other edit.
        /// </summary>
        public static readonly IReadOnlyList<EquipmentModDefinition> AllMods;

        private static readonly Dictionary<EquipmentModId, EquipmentModDefinition> byId;
        private static readonly Dictionary<PropertyFloat, EquipmentModDefinition> byProperty;

        /// <summary>
        /// Uniform roll table over the whole catalog. Deliberately uniform in v1: mod type is rolled without
        /// regard to which material was used or which class the player plays (plan decision 3), so a perfect
        /// piece is intentionally near-impossible and trading is the pressure valve. Weighting a mod down here
        /// later is a one-line change.
        /// </summary>
        private static readonly ChanceTable<EquipmentModId> modChance;

        /// <summary>
        /// The roll table's entries, for the invariant test. Exposed rather than asserted through the log,
        /// because ChanceTable verifies itself only on a table's FIRST roll: once any other test has rolled,
        /// a log-scraping assertion would pass vacuously. A test can copy these weights into a fresh table
        /// to force a real verification pass.
        /// </summary>
        public static IReadOnlyList<(EquipmentModId result, float chance)> ModChanceTable => modChance;

        static EquipmentModRegistry()
        {
            AllMods = mods;

            byId = mods.ToDictionary(m => m.Id);
            byProperty = mods.ToDictionary(m => m.Property);

            // Uniform weights, with the final slot carrying the remainder so the table sums to exactly 1.0
            // and ChanceTable's self-verification stays quiet.
            //
            // THE REMAINDER MUST BE ACCUMULATED IN DECIMAL, NOT IN FLOAT. ChanceTable.VerifyTable() sums the
            // table in decimal, converting every stored float through (decimal)float - a conversion that
            // keeps only 7 significant digits. Doing the subtraction in float (the original form of this
            // loop) makes the table sum to 1.0 in FLOAT arithmetic only; the decimal sum of the very same 27
            // stored floats came out at 1.00000029, past ChanceTable's 1e-7 tolerance, so every single mod
            // roll logged an ERROR line on a routine player action. Accumulating the remainder in the
            // verifier's own domain leaves exactly ONE float rounding (the final cast, under 1 ulp) instead
            // of one per entry, which is orders of magnitude inside the tolerance at any catalog size.
            modChance = new ChanceTable<EquipmentModId>();

            var weight = (float)(1.0 / mods.Length);
            var remaining = 1.0M;

            for (var i = 0; i < mods.Length; i++)
            {
                var chance = i == mods.Length - 1 ? (float)remaining : weight;
                remaining -= (decimal)chance;

                modChance.Add((mods[i].Id, chance));
            }
        }

        public static EquipmentModDefinition Get(EquipmentModId id) => byId[id];

        public static bool TryGet(EquipmentModId id, out EquipmentModDefinition definition) => byId.TryGetValue(id, out definition);

        public static EquipmentModDefinition Get(PropertyFloat property) => byProperty[property];

        public static bool TryGet(PropertyFloat property, out EquipmentModDefinition definition) => byProperty.TryGetValue(property, out definition);

        /// <summary>
        /// Rolls one mod type uniformly at random from the catalog.
        /// </summary>
        public static EquipmentModId RollModType() => modChance.Roll();

        /// <summary>
        /// Rolls one mod type uniformly at random, excluding types the target already carries (no duplicate
        /// mod types on one item - duplicates only stack across separate equipped items). Returns null when
        /// every catalog entry is excluded.
        /// </summary>
        public static EquipmentModId? RollModType(ICollection<EquipmentModId> exclude)
        {
            if (exclude == null || exclude.Count == 0)
                return RollModType();

            var candidates = mods.Where(m => !exclude.Contains(m.Id)).ToList();

            if (candidates.Count == 0)
                return null;

            // small candidate sets are not worth a rebuilt ChanceTable per call; uniform index draw
            return candidates[ThreadSafeRandom.Next(0, candidates.Count - 1)].Id;
        }
    }
}
