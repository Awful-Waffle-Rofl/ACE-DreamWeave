using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// One row of the weapon-mod test kit: a base weenie, the state to force onto it, and the outcome the
    /// tester should see. Deliberately a plain data record with no WorldObject anywhere in it, so the whole
    /// table can be asserted over in ACE.Server.Tests without a live Player.
    /// </summary>
    public sealed class WeaponModKitEntry
    {
        /// <summary>Short case name. The spawned item is named "[WM] " + this.</summary>
        public string Case;

        /// <summary>The base weenie. Every wcid in this table was verified against ace_world on 2026-07-30.</summary>
        public uint Wcid;

        /// <summary>How many copies to spawn. Only the consumable salvage bags need more than one.</summary>
        public int Count = 1;

        /// <summary>What the tester should see when this item is used, or used on. Printed verbatim in chat.</summary>
        public string Expected;

        /// <summary>An extra line of context, printed under <see cref="Expected"/>. Optional.</summary>
        public string Note;

        /// <summary>TRUE for the two salvage bags, so "@weaponmodkit bags" can top them up on their own.</summary>
        public bool IsBag;

        /// <summary>Forced PropertyInt values. A NULL value means REMOVE the row (the no-workmanship case).</summary>
        public IReadOnlyList<(PropertyInt Property, int? Value)> Ints = new (PropertyInt, int?)[0];

        /// <summary>Forced PropertyFloat values. A NULL value means REMOVE the row.</summary>
        public IReadOnlyList<(PropertyFloat Property, double? Value)> Floats = new (PropertyFloat, double?)[0];

        /// <summary>Forced PropertyString values. A NULL value means REMOVE the row.</summary>
        public IReadOnlyList<(PropertyString Property, string Value)> Strings = new (PropertyString, string)[0];

        public string ItemName => WeaponModKitTable.NamePrefix + Case;

        /// <summary>The forced NumTimesTinkered, or null when this row does not force one.</summary>
        public int? ForcedTinkerCount =>
            Ints.Where(i => i.Property == PropertyInt.NumTimesTinkered).Select(i => i.Value).FirstOrDefault();

        /// <summary>The forced TinkerLog, or null when this row does not force one.</summary>
        public string ForcedTinkerLog =>
            Strings.Where(s => s.Property == PropertyString.TinkerLog).Select(s => s.Value).FirstOrDefault();

        /// <summary>
        /// How many SPECIAL modifier records this row forces - a Tier A or Tier B magnitude written straight
        /// onto the item. Those occupy slots that the tinker log does not carry, so the budget check is
        /// "log entries + specials = NumTimesTinkered", not "log entries = NumTimesTinkered".
        /// </summary>
        public int ForcedSpecialCount =>
            Floats.Count(f => f.Value != null && WeaponModRegistry.TryGet(f.Property, out _));
    }

    /// <summary>
    /// THE FLAT DEFINITION TABLE the test kit is built from - one row per verification case, holding the base
    /// wcid, the forced state and the expected outcome. Everything in here is pure data so a v2 pass extends
    /// the table rather than the spawn code, and so ACE.Server.Tests can assert over it directly.
    ///
    /// WCIDS AND WEENIE STATE, ALL VERIFIED AGAINST ace_world ON 2026-07-30 (nothing here is from recall):
    ///
    ///   53315   ace53315-stormwoodgreatsword  ItemType 1 MeleeWeapon, CombatUse 5 TwoHanded,
    ///                                         ItemWorkmanship 8, no NumItemsInMaterial row (so Workmanship
    ///                                         reads 8.0), Damage 45, WeaponTime 50, no TinkerLog,
    ///                                         no NumTimesTinkered, no ImbuedEffect
    ///   21964   bowphantom                    ItemType 256 MissileWeapon, CombatUse 2 Missile,
    ///                                         DamageMod 0.5, MaximumVelocity 50, NO ItemWorkmanship,
    ///                                         and ImbuedEffect 0x80000000 IgnoreAllArmor - see the row's Note
    ///   2472    wand                          ItemType 32768 Caster, no CombatUse, NO ItemWorkmanship,
    ///                                         ManaConversionMod 0, WeaponDefense 1
    ///   1000238 ace1000238-driftwardenshield   ItemType 2 Armor, CombatUse 4 Shield
    ///   300     arrow                         ItemType 256 MissileWeapon, CombatUse 3 Ammo
    ///   21082   materialtourmaline            ItemType 0x40000000 TinkeringMaterial, MaterialType 43,
    ///                                         MaxStructure 100, NO Structure row (so an unspawned bag is
    ///                                         EMPTY and this table has to fill it)
    ///   21036   materialamethyst              same shape, MaterialType 12
    ///
    /// THE IMBUE MATERIALS IN THE LOG ARE BLACK OPAL (16) AND SUNSTONE (41). Chosen because those are the two
    /// salvage materials whose live DAT mutation scripts actually write the two imbues the row forces:
    /// "38000023 - Black Opal.txt" sets ImbuedEffect = CriticalStrike and "38000025 - Sunstone.txt" sets
    /// ImbuedEffect = ArmorRending. Any other gem would leave the log inconsistent with the ImbuedEffect
    /// bitfield. Neither is a material WeaponTinkerTable owns, which is exactly what makes them reserve slots.
    /// </summary>
    public static class WeaponModKitTable
    {
        /// <summary>Every kit item is named with this prefix, so "@weaponmodkit clean" can find them all.</summary>
        public const string NamePrefix = "[WM] ";

        /// <summary>Salvage bags spawn full. The bag weenies ship MaxStructure 100 and no Structure row.</summary>
        public const int FullBagStructure = 100;

        /// <summary>A comma-separated MaterialType id list in the format RecipeManager.HandleTinkerLog writes.</summary>
        public static string BuildLog(params (MaterialType Material, int Count)[] parts) =>
            string.Join(",", parts.SelectMany(p => Enumerable.Repeat(((uint)p.Material).ToString(), p.Count)));

        private static readonly WeaponModKitEntry[] entries =
        {
            new WeaponModKitEntry
            {
                Case = "plain melee",
                Wcid = 53315,
                Expected = "reroll SUCCEEDS. Ten free slots, workmanship 8 - the baseline case.",
            },
            new WeaponModKitEntry
            {
                Case = "hand-tinkered melee",
                Wcid = 53315,
                Expected = "reroll and swap BOTH succeed. Ten Iron, all reversible, nothing reserved.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.NumTimesTinkered, 10),
                },
                Strings = new (PropertyString, string)[]
                {
                    (PropertyString.TinkerLog, BuildLog((MaterialType.Iron, 10))),
                },
            },
            new WeaponModKitEntry
            {
                Case = "imbued melee",
                Wcid = 53315,
                Expected = "SUCCEEDS with 2 slots reserved. CriticalStrike and ArmorRending must both survive.",
                Note = "log is Black Opal + Sunstone + eight Iron = 10 entries, matching NumTimesTinkered 10.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.ImbuedEffect, (int)(ImbuedEffectType.CriticalStrike | ImbuedEffectType.ArmorRending)),
                    (PropertyInt.NumTimesTinkered, 10),
                },
                Strings = new (PropertyString, string)[]
                {
                    (PropertyString.TinkerLog, BuildLog((MaterialType.BlackOpal, 1), (MaterialType.Sunstone, 1), (MaterialType.Iron, 8))),
                },
            },
            new WeaponModKitEntry
            {
                Case = "part-tinkered melee",
                Wcid = 53315,
                Expected = "reroll SUCCEEDS, swap REFUSES - a swap trades a slot, it cannot fill an empty one.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.NumTimesTinkered, 3),
                },
                Strings = new (PropertyString, string)[]
                {
                    (PropertyString.TinkerLog, BuildLog((MaterialType.Iron, 3))),
                },
            },
            new WeaponModKitEntry
            {
                Case = "ten-Oak melee",
                Wcid = 53315,
                Expected = "REFUSED both ways. All ten slots hold Oak, which this system cannot reverse.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.NumTimesTinkered, 10),
                },
                Strings = new (PropertyString, string)[]
                {
                    (PropertyString.TinkerLog, BuildLog((MaterialType.Oak, 10))),
                },
            },
            new WeaponModKitEntry
            {
                Case = "no workmanship",
                Wcid = 53315,
                Expected = "REFUSED. No workmanship means every special would resolve to nothing.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.ItemWorkmanship, null),
                },
            },
            new WeaponModKitEntry
            {
                Case = "low workmanship",
                Wcid = 53315,
                Expected = "SUCCEEDS, and any special that lands is visibly weak - magnitude scales with workmanship.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.ItemWorkmanship, 1),
                },
            },
            new WeaponModKitEntry
            {
                Case = "missile sub-1.0 DamageMod",
                Wcid = 21964,
                Expected = "SUCCEEDS. DamageMod must stay anchored to its 0.5 base - 0.5 plus 0.04 per Mahogany drawn - and must NEVER reach 1.0, however many times you reroll.",
                Note = "this weenie ships ImbuedEffect IgnoreAllArmor (0x80000000), so ONE slot is reserved and a reroll fills nine, not ten. That is the weenie's own data, not a bug.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.ItemWorkmanship, 8),
                },
            },
            new WeaponModKitEntry
            {
                Case = "caster",
                Wcid = 2472,
                Expected = "SUCCEEDS, drawing from the caster pool only - Green Garnet, Opal, Brass, Velvet. No Iron, no Mahogany, no Granite.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.ItemWorkmanship, 8),
                },
            },
            new WeaponModKitEntry
            {
                Case = "Tier B melee",
                Wcid = 53315,
                Expected = "EQUIP AND INSPECT. Carries Efficiency, Recovery and Mana Well at their MAXIMUM roll, so all three are visible on the appraisal panel without grinding rerolls.",
                Note = "Efficiency, Recovery and Mana Well (WeaponModId.Efficiency/Recovery/ManaWell, catalog v4, 2026-08-17) are REGISTERED BUT INERT this pass - no combat/vital hook reads any of them yet, so this row proves the record roll, tinker-log accounting and appraisal display, not a live effect. Needs @modifybool weapon_mods_enabled true for the appraisal block to render at all.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.NumTimesTinkered, 10),
                    (PropertyInt.WeaponModTinkerCount, 7),
                },
                Floats = new (PropertyFloat, double?)[]
                {
                    // 1.0 IS THE FULL ROLL FRACTION, NOT A MAGNITUDE. WeaponModCombat.ReadWeaponOnly (fixed
                    // 2026-08-07) resolves a Tier B record as a fraction of the catalog's MaxRoll, so writing
                    // the intended magnitude directly here (as the pre-2026-08-17 rows for this table did, e.g.
                    // "WeaponModLifeLeech, 0.04" for a MaxRoll of 0.04) actually rolls a tiny fraction of a
                    // fraction - a "max roll" kit item was really about 4% of max. 1.0 is the correct value for
                    // a maximum-roll test row regardless of what the modifier's own MaxRoll happens to be.
                    (PropertyFloat.WeaponModEfficiency, 1.0),
                    (PropertyFloat.WeaponModRecovery, 1.0),
                    (PropertyFloat.WeaponModManaWell, 1.0),
                },
                Strings = new (PropertyString, string)[]
                {
                    // three specials plus seven tinkers is the full ten-slot budget
                    (PropertyString.TinkerLog, BuildLog((MaterialType.Iron, 7))),
                    (PropertyString.WeaponModTinkerLog, BuildLog((MaterialType.Iron, 7))),
                },
            },
            new WeaponModKitEntry
            {
                Case = "Tier B missile",
                Wcid = 21964,
                Expected = "EQUIP AND INSPECT. Carries Panic Reload, Cleanse and Siphon at their MAXIMUM roll (see the melee row above for why the record value is 1.0, not the modifier's MaxRoll).",
                Note = "Panic Reload (missile-only), Cleanse and Siphon (WeaponModId.PanicReload/Cleanse/Siphon, catalog v4, 2026-08-17) are REGISTERED BUT INERT this pass - no combat hook reads any of them yet. This row is the only one in the table that exercises a missile-only v4 row.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.NumTimesTinkered, 10),
                    (PropertyInt.WeaponModTinkerCount, 7),
                },
                Floats = new (PropertyFloat, double?)[]
                {
                    (PropertyFloat.WeaponModPanicReload, 1.0),
                    (PropertyFloat.WeaponModCleanse, 1.0),
                    (PropertyFloat.WeaponModSiphon, 1.0),
                },
                Strings = new (PropertyString, string)[]
                {
                    (PropertyString.TinkerLog, BuildLog((MaterialType.Mahogany, 7))),
                    (PropertyString.WeaponModTinkerLog, BuildLog((MaterialType.Mahogany, 7))),
                },
            },
            new WeaponModKitEntry
            {
                Case = "Tier B caster",
                Wcid = 2472,
                Expected = "EQUIP THIS AND CAST. Carries Longevity, Quick Refresh and Arcane Defender at their MAXIMUM roll - the three caster-only v4 rows (see the melee row above for why the record value is 1.0, not the modifier's MaxRoll).",
                Note = "Longevity, Quick Refresh and Arcane Defender (WeaponModId.Longevity/QuickRefresh/ArcaneDefender, catalog v4, 2026-08-17) are REGISTERED BUT INERT this pass - no enchantment/combat hook reads any of them yet.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.ItemWorkmanship, 8),
                    (PropertyInt.NumTimesTinkered, 10),
                    (PropertyInt.WeaponModTinkerCount, 7),
                },
                Floats = new (PropertyFloat, double?)[]
                {
                    (PropertyFloat.WeaponModLongevity, 1.0),
                    (PropertyFloat.WeaponModQuickRefresh, 1.0),
                    (PropertyFloat.WeaponModArcaneDefender, 1.0),
                },
                Strings = new (PropertyString, string)[]
                {
                    (PropertyString.TinkerLog, BuildLog((MaterialType.GreenGarnet, 7))),
                    (PropertyString.WeaponModTinkerLog, BuildLog((MaterialType.GreenGarnet, 7))),
                },
            },
            new WeaponModKitEntry
            {
                Case = "shield",
                Wcid = 1000238,
                Expected = "REFUSED. A shield is not a weapon.",
            },
            new WeaponModKitEntry
            {
                Case = "arrows",
                Wcid = 300,
                Expected = "REFUSED. Ammunition is not a weapon.",
            },
            new WeaponModKitEntry
            {
                Case = "Tourmaline bag",
                Wcid = 21082,
                Count = 8,
                IsBag = true,
                Expected = "the REROLL material. One full bag is the price of one use.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.Structure, FullBagStructure),
                },
            },
            new WeaponModKitEntry
            {
                Case = "Amethyst bag",
                Wcid = 21036,
                Count = 6,
                IsBag = true,
                Expected = "the SWAP material. One full bag is the price of one use.",
                Ints = new (PropertyInt, int?)[]
                {
                    (PropertyInt.Structure, FullBagStructure),
                },
            },
        };

        public static IReadOnlyList<WeaponModKitEntry> Entries => entries;

        public static IReadOnlyList<WeaponModKitEntry> Bags => entries.Where(e => e.IsBag).ToList();
    }

    /// <summary>
    /// A test-environment harness for the Weapon Mods system: one command that spawns a fully configured kit
    /// of weapons and salvage bags in the caller's inventory.
    ///
    /// WHY THIS EXISTS. The only other way to build these cases is @setproperty, which writes to the LAST
    /// APPRAISED OBJECT. Configuring the thirteen weapons below by hand is roughly fifty appraise-then-set steps,
    /// and a single missed examine silently writes onto the PREVIOUS item with no error at all. That footgun is
    /// the whole reason for this command.
    ///
    /// DELIBERATELY NOT GATED ON weapon_mods_enabled. One of the three cases the runbook covers is "the gate is
    /// off and both bags fall through to ordinary tinkering", so the kit has to be buildable with the feature
    /// disabled. The command prints a warning instead.
    ///
    /// Test tooling only. Nothing under ACE.Server/WeaponMods is touched by this file.
    /// </summary>
    public static class WeaponModTestCommands
    {
        [CommandHandler("weaponmodkit", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 0,
            "Spawns a fully configured Weapon Mods test kit in your inventory. See Docs/WeaponMods/TEST-RUNBOOK.md",
            "[clean | bags]\n" +
            "  (no argument) - spawns the whole kit: 13 configured weapons plus full salvage bags\n" +
            "  bags          - tops up the salvage bags only\n" +
            "  clean         - destroys every '[WM] ' item in your inventory")]
        public static void HandleWeaponModKit(Session session, params string[] parameters)
        {
            var mode = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";

            switch (mode)
            {
                case "clean":
                    HandleClean(session);
                    return;

                case "bags":
                    Spawn(session, WeaponModKitTable.Bags, "Salvage bags topped up.");
                    return;

                case "":
                    Spawn(session, WeaponModKitTable.Entries, "Weapon Mods test kit built.");
                    return;

                default:
                    Send(session, $"Unknown option '{parameters[0]}'. Use @weaponmodkit, @weaponmodkit bags, or @weaponmodkit clean.");
                    return;
            }
        }

        private static void Spawn(Session session, IReadOnlyList<WeaponModKitEntry> entries, string header)
        {
            var player = session.Player;

            Send(session, header);

            if (!PropertyManager.GetBool("weapon_mods_enabled").Item)
            {
                Send(session, "WARNING: weapon_mods_enabled is FALSE. That single bool gates the WHOLE system. The kit is still usable, but a bag used on a weapon will fall through to ORDINARY tinkering, no reroll or swap will fire, every Tier B combat effect reads zero, and the appraisal block does not render. Turn it on with: @modifybool weapon_mods_enabled true");
            }

            foreach (var entry in entries)
            {
                var made = 0;

                for (var i = 0; i < Math.Max(1, entry.Count); i++)
                {
                    var wo = WorldObjectFactory.CreateNewWorldObject(entry.Wcid);

                    if (wo == null)
                    {
                        Send(session, $"FAILED: wcid {entry.Wcid} ({entry.Case}) could not be created. Is your ace_world up to date?");
                        break;
                    }

                    ApplyForcedState(wo, entry);

                    if (!player.TryCreateInInventoryWithNetworking(wo))
                    {
                        Send(session, $"FAILED: could not place {entry.ItemName} in your inventory. Free up some pack space and re-run.");
                        wo.Destroy();
                        break;
                    }

                    made++;
                }

                if (made == 0)
                    continue;

                var quantity = made > 1 ? $" x{made}" : "";

                Send(session, $"{entry.ItemName}{quantity} (wcid {entry.Wcid}) - {entry.Expected}");

                if (entry.Note != null)
                    Send(session, $"    note: {entry.Note}");
            }

            Send(session, "Crafting verb is /usewith, and you must be in PEACE mode. Full steps: Docs/WeaponMods/TEST-RUNBOOK.md");
        }

        /// <summary>
        /// Writes the row's forced state onto a freshly created item. A NULL value REMOVES the property, which
        /// is what the no-workmanship case needs: 53315 ships ItemWorkmanship 8, so that case is a removal
        /// rather than an assignment.
        /// </summary>
        private static void ApplyForcedState(WorldObject wo, WeaponModKitEntry entry)
        {
            wo.SetProperty(PropertyString.Name, entry.ItemName);

            foreach (var (property, value) in entry.Ints)
            {
                if (value == null)
                    wo.RemoveProperty(property);
                else
                    wo.SetProperty(property, value.Value);
            }

            foreach (var (property, value) in entry.Floats)
            {
                if (value == null)
                    wo.RemoveProperty(property);
                else
                    wo.SetProperty(property, value.Value);
            }

            foreach (var (property, value) in entry.Strings)
            {
                if (value == null)
                    wo.RemoveProperty(property);
                else
                    wo.SetProperty(property, value);
            }
        }

        /// <summary>
        /// Destroys every "[WM] " item the caller is carrying, so a re-run does not accumulate clutter.
        /// Equipped items cannot be consumed out of inventory, so they are counted and reported rather than
        /// silently skipped.
        /// </summary>
        private static void HandleClean(Session session)
        {
            var player = session.Player;

            var targets = player.GetAllPossessions()
                .Where(wo => wo.Name != null && wo.Name.StartsWith(WeaponModKitTable.NamePrefix, StringComparison.Ordinal))
                .ToList();

            var destroyed = 0;
            var skipped = 0;

            foreach (var wo in targets)
            {
                if (player.TryConsumeFromInventoryWithNetworking(wo))
                    destroyed++;
                else
                    skipped++;
            }

            Send(session, $"Weapon Mods test kit: destroyed {destroyed} item(s).");

            if (skipped > 0)
                Send(session, $"{skipped} item(s) could not be removed - unequip them and run @weaponmodkit clean again.");
        }

        private static void Send(Session session, string message)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }
    }

    /// <summary>
    /// A one-appraisal diagnostic probe for the client rendering-order question this repo cannot answer from
    /// source: AppraiseInfo.cs writes exactly three free-text PropertyString slots that could plausibly carry
    /// the "Property Details:" block - ShortDesc, LongDesc, Use - and where the client draws each one relative
    /// to the weapon's bonus lines and the Spells: section is a client-side layout fact, not a server fact.
    ///
    /// RESULT, 2026-07-30, run live by the repo owner: USE RENDERS HIGHEST of the three, and both of its marker
    /// lines rendered, so the slot preserves embedded newlines. The "Property Details:" block was moved onto
    /// PropertyString.Use on the strength of that (AppraiseInfo.WritePropertyDetails); it used to be appended to
    /// LongDesc, which the client draws at the bottom of the panel, after the spell list. The command is kept
    /// because the same question recurs for every new free-text surface.
    ///
    /// MECHANISM. All three slots are safe to probe by writing a plain stored PropertyString and letting
    /// AppraiseInfo's normal read-through carry it to the client - none of the three are wholesale replaced
    /// for an ordinary weapon (Corpse/Portal/SlumLord/Container/Storage/Hook/ManaStone all special-case one or
    /// more of these slots, but a MeleeWeapon/Missile/Caster hits none of those branches):
    ///   - ShortDesc: AppraiseInfo.cs never touches PropertyString.ShortDesc at all (the only two ShortDesc
    ///     references in the whole file are commented out, in the Hook branch). Whatever is stored reaches the
    ///     client completely unmodified.
    ///   - LongDesc: AppraiseInfo.cs no longer writes it at all (it did when this probe was first run, which is
    ///     why the marker below is still worth stamping - it now passes through completely unmodified).
    ///   - Use: AppraiseInfo.cs overwrites PropertyString.Use for `wo is ManaStone`, and WritePropertyDetails
    ///     APPENDS the "Property Details:" block to it, but only when propertyDetails.Count > 0 (an active
    ///     MultiShot/equipment-mod/weapon-mod line exists on this specific item). With no such line active, the
    ///     stored Use marker passes through unmodified, which is the common case for a plain probe target. If
    ///     the target DOES have an active mod line, the marker still appears - as the prefix the block is
    ///     appended to - and is never silently dropped. So a stored property genuinely works for all three
    ///     slots; no AppraiseInfo-layer flag was needed.
    ///
    /// Test tooling only. Nothing under ACE.Server/WeaponMods is touched by this file.
    /// </summary>
    public static class AppraiseProbeCommands
    {
        private const string ShortDescMarker = "<<<SHORTDESC MARKER LINE 1>>>\n<<<SHORTDESC MARKER LINE 2>>>";
        private const string LongDescMarker = "<<<LONGDESC MARKER LINE 1>>>\n<<<LONGDESC MARKER LINE 2>>>";
        private const string UseMarker = "<<<USE MARKER LINE 1>>>\n<<<USE MARKER LINE 2>>>";

        [CommandHandler("appraiseprobe", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 0,
            "Writes a distinct marker into ShortDesc, LongDesc, and Use on the last appraised object, so one examine reveals the client's render order for all three.",
            "[clear]\n" +
            "  (no argument) - writes the three markers onto the last appraised object\n" +
            "  clear         - removes all three markers from the last appraised object")]
        public static void HandleAppraiseProbe(Session session, params string[] parameters)
        {
            var obj = CommandHandlerHelper.GetLastAppraisedObject(session);
            if (obj == null)
                return;

            var clear = parameters.Length > 0 && parameters[0].Equals("clear", StringComparison.OrdinalIgnoreCase);

            if (clear)
            {
                session.Player.UpdateProperty(obj, PropertyString.ShortDesc, null, true);
                session.Player.UpdateProperty(obj, PropertyString.LongDesc, null, true);
                session.Player.UpdateProperty(obj, PropertyString.Use, null, true);

                Send(session, $"{obj.Name} ({obj.Guid}): cleared ShortDesc, LongDesc, and Use markers.");
                return;
            }

            session.Player.UpdateProperty(obj, PropertyString.ShortDesc, ShortDescMarker, true);
            session.Player.UpdateProperty(obj, PropertyString.LongDesc, LongDescMarker, true);
            session.Player.UpdateProperty(obj, PropertyString.Use, UseMarker, true);

            Send(session, $"{obj.Name} ({obj.Guid}): wrote markers to 3 slots. Examine it now and compare against the panel:");
            Send(session, "  PropertyString.ShortDesc -> \"<<<SHORTDESC MARKER LINE 1>>>\\n<<<SHORTDESC MARKER LINE 2>>>\"");
            Send(session, "  PropertyString.LongDesc  -> \"<<<LONGDESC MARKER LINE 1>>>\\n<<<LONGDESC MARKER LINE 2>>>\"");
            Send(session, "  PropertyString.Use       -> \"<<<USE MARKER LINE 1>>>\\n<<<USE MARKER LINE 2>>>\"");
            Send(session, "Run '@appraiseprobe clear' to remove all three markers.");
        }

        private static void Send(Session session, string message)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }
    }
}
