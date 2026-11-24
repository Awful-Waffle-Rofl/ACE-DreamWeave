using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Managers.Analytics
{
    /// <summary>
    /// player_death_event telemetry: one row per player death, with the killing blow and the victim's protective /
    /// debuff state at that hit. Two halves:
    ///
    ///   * <see cref="CaptureKillingHit"/> runs from DamageHistory.Add (the thrown overload) on a hit that left a
    ///     PLAYER at 0 health, and stamps the attacker snapshot and the defensive snapshot onto the killing entry. It
    ///     runs before OnDeath because enchantments can expire or be stripped by the time the death handler runs.
    ///   * <see cref="RecordDeath"/> runs from Player.OnDeath, builds the row from the entry (never from a live
    ///     attacker, which may be gone) and queues it on AnalyticsManager. The database is never touched here.
    ///
    /// Recording only: nothing here changes damage, and every entry point swallows its own exceptions (warned once),
    /// because telemetry must never break a hit or a death. Inert while analytics is disabled, apart from the attacker
    /// snapshot, which is a few field reads on a killing hit only.
    /// </summary>
    public static class DeathTelemetry
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static int captureWarned;
        private static int recordWarned;

        /// <summary>The element-column spelling when the hit has no single resistance element.</summary>
        public const string NoElement = "none";

        // ---------------------------------------------------------------------------------------------------------
        // pure: damage type -> element -> spell categories
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The single resistance element a hit's damage type maps to: exactly one of Slash, Pierce, Bludgeon, Cold, Fire,
        /// Acid, Electric or Nether, with no other flag set. Anything else (Undef, Health, Stamina, Mana, Base, or a value
        /// carrying more than one flag) returns Undef, recorded as "none".
        /// </summary>
        public static DamageType ResistElement(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.Slash:
                case DamageType.Pierce:
                case DamageType.Bludgeon:
                case DamageType.Cold:
                case DamageType.Fire:
                case DamageType.Acid:
                case DamageType.Electric:
                case DamageType.Nether:
                    return damageType;
                default:
                    return DamageType.Undef;
            }
        }

        /// <summary>The lowercase element name for player_death_event.element (VARCHAR(8)): "slash" ... "nether", or "none".</summary>
        public static string ElementName(DamageType element)
        {
            switch (element)
            {
                case DamageType.Slash: return "slash";
                case DamageType.Pierce: return "pierce";
                case DamageType.Bludgeon: return "bludgeon";
                case DamageType.Cold: return "cold";
                case DamageType.Fire: return "fire";
                case DamageType.Acid: return "acid";
                case DamageType.Electric: return "electric";
                case DamageType.Nether: return "nether";
                default: return NoElement;
            }
        }

        /// <summary>
        /// The life-magic Vulnerability category for an element (SpellCategory 102..114, the same seven the Opportunist
        /// ability enumerates), or null: Nether has no Vulnerability category, and neither does "none".
        /// </summary>
        public static SpellCategory? VulnerabilityCategory(DamageType element)
        {
            switch (element)
            {
                case DamageType.Acid: return SpellCategory.AcidVulnerability;
                case DamageType.Bludgeon: return SpellCategory.BludgeonVulnerability;
                case DamageType.Cold: return SpellCategory.ColdVulnerability;
                case DamageType.Electric: return SpellCategory.ElectricVulnerability;
                case DamageType.Fire: return SpellCategory.FireVulnerability;
                case DamageType.Pierce: return SpellCategory.PierceVulnerability;
                case DamageType.Slash: return SpellCategory.SlashVulnerability;
                default: return null;
            }
        }

        /// <summary>
        /// The life-magic Protection categories for an element - the regular one (SpellCategory 101..113) and its rare
        /// variant - or an empty array for Nether (no category) and "none".
        /// </summary>
        public static SpellCategory[] ProtectionCategories(DamageType element)
        {
            switch (element)
            {
                case DamageType.Acid: return new[] { SpellCategory.AcidProtection, SpellCategory.AcidProtectionRare };
                case DamageType.Bludgeon: return new[] { SpellCategory.BludgeonProtection, SpellCategory.BludgeonProtectionRare };
                case DamageType.Cold: return new[] { SpellCategory.ColdProtection, SpellCategory.ColdProtectionRare };
                case DamageType.Electric: return new[] { SpellCategory.ElectricProtection, SpellCategory.ElectricProtectionRare };
                case DamageType.Fire: return new[] { SpellCategory.FireProtection, SpellCategory.FireProtectionRare };
                case DamageType.Pierce: return new[] { SpellCategory.PierceProtection, SpellCategory.PierceProtectionRare };
                case DamageType.Slash: return new[] { SpellCategory.SlashProtection, SpellCategory.SlashProtectionRare };
                default: return Array.Empty<SpellCategory>();
            }
        }

        /// <summary>Imperil. Read for every killing hit, whatever its kind.</summary>
        public static readonly SpellCategory[] ImperilCategories = { SpellCategory.ArmorLowering };

        /// <summary>Armor Self and its rare variant. Read for every killing hit, whatever its kind.</summary>
        public static readonly SpellCategory[] ArmorSelfCategories = { SpellCategory.ArmorRaising, SpellCategory.ArmorRaisingRare };

        /// <summary>
        /// The strongest enchantment across <paramref name="categories"/>: highest PowerLevel, ties to the higher LayerId
        /// (the newer cast). Null when none is present. A null list from the lookup counts as empty.
        /// </summary>
        public static PropertiesEnchantmentRegistry Strongest(IEnumerable<SpellCategory> categories, Func<SpellCategory, IEnumerable<PropertiesEnchantmentRegistry>> byCategory)
        {
            PropertiesEnchantmentRegistry best = null;

            foreach (var category in categories)
            {
                var list = byCategory(category);
                if (list == null)
                    continue;

                foreach (var e in list)
                {
                    if (e == null)
                        continue;

                    if (best == null || e.PowerLevel > best.PowerLevel || (e.PowerLevel == best.PowerLevel && e.LayerId > best.LayerId))
                        best = e;
                }
            }

            return best;
        }

        /// <summary>
        /// The defensive snapshot for a killing hit of <paramref name="damageType"/>, from an enchantment lookup by
        /// category (live: EnchantmentManager.GetEnchantments(SpellCategory); tests: a fake list). Pure. The three
        /// effective-mod fields are left null here; the live capture fills them from EnchantmentManager.
        /// </summary>
        public static DeathDefenseSnapshot BuildDefenseSnapshot(DamageType damageType, Func<SpellCategory, IEnumerable<PropertiesEnchantmentRegistry>> byCategory)
        {
            var element = ResistElement(damageType);
            var snapshot = new DeathDefenseSnapshot { Element = ElementName(element) };

            var vulnCategory = VulnerabilityCategory(element);
            if (vulnCategory.HasValue)
            {
                var vuln = Strongest(new[] { vulnCategory.Value }, byCategory);
                snapshot.HadVulnerability = vuln != null;
                snapshot.VulnerabilitySpellId = SpellIdOf(vuln);
            }

            var protCategories = ProtectionCategories(element);
            if (protCategories.Length > 0)
            {
                var prot = Strongest(protCategories, byCategory);
                snapshot.HadElementProtection = prot != null;
                snapshot.ElementProtectionSpellId = SpellIdOf(prot);
            }

            var imperil = Strongest(ImperilCategories, byCategory);
            snapshot.HadImperil = imperil != null;
            snapshot.ImperilSpellId = SpellIdOf(imperil);

            var armorSelf = Strongest(ArmorSelfCategories, byCategory);
            snapshot.HadArmorSelf = armorSelf != null;
            snapshot.ArmorSelfSpellId = SpellIdOf(armorSelf);

            return snapshot;
        }

        private static uint? SpellIdOf(PropertiesEnchantmentRegistry e) => e == null ? (uint?)null : (uint)e.SpellId;

        // ---------------------------------------------------------------------------------------------------------
        // the killing hit (called from DamageHistory.Add)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Stamps the attacker as it stands now onto <paramref name="entry"/>. A null attacker leaves the fields null.</summary>
        public static void SnapshotAttacker(DamageHistoryEntry entry, WorldObject attacker)
        {
            if (entry == null || attacker == null)
                return;

            entry.AttackerWcid = attacker.WeenieClassId;
            entry.AttackerName = attacker.Name;
            entry.AttackerLevel = (attacker as Creature)?.Level;
            entry.AttackerIsPlayer = attacker is Player;
        }

        /// <summary>
        /// A hit just left <paramref name="victim"/> at 0 health. Snapshots the attacker always, and the victim's defensive
        /// state while analytics is enabled. Never throws.
        /// </summary>
        public static void CaptureKillingHit(DamageHistoryEntry entry, Player victim, WorldObject attacker)
        {
            try
            {
                SnapshotAttacker(entry, attacker);

                if (!AnalyticsManager.IsEnabled || victim?.EnchantmentManager == null)
                    return;

                var em = victim.EnchantmentManager;
                var snapshot = BuildDefenseSnapshot(entry.DamageType, em.GetEnchantments);

                var element = ResistElement(entry.DamageType);
                if (element != DamageType.Undef)
                {
                    snapshot.ProtResistMod = em.GetProtectionResistanceMod(element);
                    snapshot.VulnResistMod = em.GetVulnerabilityResistanceMod(element);
                }

                snapshot.BodyArmorMod = em.GetBodyArmorMod();

                entry.Defense = snapshot;
            }
            catch (Exception ex)
            {
                if (System.Threading.Interlocked.Exchange(ref captureWarned, 1) == 0)
                    log.Warn("[DEATHTELEMETRY] killing-hit capture threw; the death row will lack the snapshot (warned once)", ex);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // the row (called from Player.OnDeath)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Fills the killing-blow and defensive columns of <paramref name="row"/> from <paramref name="killingBlow"/>
        /// (DamageHistory.KillingBlow), or marks the blow unknown when it is null. Reads only the entry, never a live
        /// attacker. Pure.
        /// </summary>
        internal static void ApplyKillingBlow(AnalyticsDatabase.PlayerDeathRow row, DamageHistoryEntry killingBlow)
        {
            if (killingBlow?.Thrown == null)
            {
                row.KillingBlowKnown = false;
                return;
            }

            row.KillingBlowKnown = true;
            row.Thrown = killingBlow.Thrown;
            row.HealthBefore = killingBlow.Amount < 0 ? (uint)-killingBlow.Amount : 0u;
            row.MaxHealth = killingBlow.MaxHealth;
            row.AttackKind = killingBlow.AttackKind.HasValue ? DamageAttackKindNames.Wire(killingBlow.AttackKind.Value) : null;
            row.DamageType = DamageTypeName(killingBlow.DamageType);
            row.IsCritical = killingBlow.IsCritical;
            row.SpellId = killingBlow.SpellId;
            row.KillerGuid = killingBlow.Attacker.Full;
            row.KillerWcid = killingBlow.AttackerWcid;
            row.KillerName = killingBlow.AttackerName;
            row.KillerLevel = killingBlow.AttackerLevel;
            row.KillerIsPlayer = killingBlow.AttackerIsPlayer;

            var d = killingBlow.Defense;
            if (d != null)
            {
                row.Element = d.Element;
                row.HadVulnerability = d.HadVulnerability;
                row.VulnerabilitySpellId = d.VulnerabilitySpellId;
                row.HadImperil = d.HadImperil;
                row.ImperilSpellId = d.ImperilSpellId;
                row.HadElementProtection = d.HadElementProtection;
                row.ElementProtectionSpellId = d.ElementProtectionSpellId;
                row.HadArmorSelf = d.HadArmorSelf;
                row.ArmorSelfSpellId = d.ArmorSelfSpellId;
                row.ProtResistMod = d.ProtResistMod;
                row.VulnResistMod = d.VulnResistMod;
                row.BodyArmorMod = d.BodyArmorMod;
            }
        }

        /// <summary>The damage type's display name ("Slashing", "Health"), or its flag list for a multi-flag value.</summary>
        public static string DamageTypeName(DamageType damageType) => damageType.GetName() ?? damageType.ToString();

        /// <summary>
        /// Queues one player_death_event row for <paramref name="player"/>, who is dying now. <paramref name="message"/>
        /// is the exact victim chat line (death text plus killing-blow suffix). Called from Player.OnDeath after that
        /// line is built. Never throws; a no-op while analytics is disabled.
        /// </summary>
        public static void RecordDeath(Player player, DamageHistoryInfo lastDamager, DamageType deathDamageType, string message)
        {
            if (!AnalyticsManager.IsEnabled || player == null)
                return;

            try
            {
                var row = new AnalyticsDatabase.PlayerDeathRow
                {
                    TsUtc = DateTime.UtcNow,
                    CharacterId = player.Guid.Full,
                    Name = player.Name,
                    Level = player.Level,
                    Landblock = player.Location != null ? (int)player.Location.LandblockId.Landblock : 0,
                    MaxHealth = player.Health.MaxValue,
                    Message = message ?? string.Empty,
                };

                var instance = player.CurrentLandblock?.Instance ?? 0;
                if (instance != 0)
                    row.InstanceId = instance;

                ApplyKillingBlow(row, player.DamageHistory?.KillingBlow);

                // No exact killing blow (a DoT tick, a fall, an unrecorded sink): the killer columns fall back to the death
                // handler's own last damager, and damage_type to the death message's damage type.
                if (!row.KillingBlowKnown)
                    ApplyFallbackKiller(row, lastDamager, deathDamageType);

                ApplyThreadRun(row, ThreadDungeonManager.GetRegisteredRun(instance));

                AnalyticsManager.RecordPlayerDeath(row);
            }
            catch (Exception ex)
            {
                if (System.Threading.Interlocked.Exchange(ref recordWarned, 1) == 0)
                    log.Warn("[DEATHTELEMETRY] building the death row threw; that death is not recorded (warned once)", ex);
            }
        }

        /// <summary>The killer columns from the death handler's last damager, for a death with no exact killing blow. Pure.</summary>
        internal static void ApplyFallbackKiller(AnalyticsDatabase.PlayerDeathRow row, DamageHistoryInfo lastDamager, DamageType deathDamageType)
        {
            row.DamageType = DamageTypeName(deathDamageType);

            if (lastDamager == null)
                return;

            row.KillerGuid = lastDamager.Guid.Full;
            row.KillerName = lastDamager.Name;
            row.KillerIsPlayer = lastDamager.IsPlayer;

            var live = lastDamager.TryGetAttacker();
            if (live != null)
            {
                row.KillerWcid = live.WeenieClassId;
                row.KillerLevel = (live as Creature)?.Level;
            }
        }

        /// <summary>The Thread run columns, when the player died inside a registered run's instance. Pure.</summary>
        internal static void ApplyThreadRun(AnalyticsDatabase.PlayerDeathRow row, ThreadDungeonRun run)
        {
            if (run == null)
                return;

            row.ThreadStartGroup = run.StartGroup;
            row.ThreadDungeonId = run.Dungeon?.Id;
            row.ThreadGemLevel = run.Spec?.Level;
        }
    }
}
