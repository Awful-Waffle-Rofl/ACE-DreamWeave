using System;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// PropertyBool.PveOnly (9081) on the player side: the equip refusal, the match-entry clear and the player-target damage block.
    /// The decisions are the pure <see cref="PvpPveOnlyRule"/>; this file only feeds it and acts on the answer.
    /// </summary>
    partial class Player
    {
        /// <summary>The seconds between two "will not harm" lines to the same attacker.</summary>
        internal const double PveOnlyHarmNoticeIntervalSeconds = 10.0;

        /// <summary>Throttles the "will not harm another player" line: a cleave or a volley asks once per target. Per player, never static.</summary>
        private DateTime pveOnlyHarmNoticeAtUtc;

        /// <summary>True when the item carries PropertyBool.PveOnly on its own biota.</summary>
        internal static bool IsPveOnlyItem(WorldObject item)
            => item != null && (item.GetProperty(PropertyBool.PveOnly) ?? false);

        /// <summary>True when equipping <paramref name="item"/> must be refused for this player now: in a match (binding or issued kit) and the item is PvE-only.</summary>
        internal bool PveOnlyEquipRefuses(WorldObject item)
            => PvpPveOnlyRule.RefusesEquip(IsInPvpMatch || IsPvpTemplated, IsPveOnlyItem(item));

        /// <summary>
        /// The PvE-only item behind a hit through <see cref="DamageTarget"/>, or null. The weapon is resolved exactly as DamageEvent
        /// resolves its Weapon (the current melee weapon for a melee strike, the launcher or else the ammo for a missile); the damage
        /// source itself counts too when it is the weapon item.
        /// </summary>
        internal WorldObject PveOnlyHitItem(WorldObject damageSource)
        {
            if (IsPveOnlyItem(damageSource))
                return damageSource;

            var weapon = damageSource?.ProjectileSource == null ? GetEquippedMeleeWeapon() : (damageSource.ProjectileLauncher ?? damageSource.ProjectileAmmo);

            return IsPveOnlyItem(weapon) ? weapon : null;
        }

        /// <summary>
        /// The hit-side check for a weapon strike or missile impact on <paramref name="target"/>: TRUE (after the throttled attacker line) when
        /// the target is a player and the hit's weapon is PvE-only, so the caller drops the hit before any damage roll or on-hit effect.
        /// </summary>
        internal bool PveOnlyBlocksWeaponHit(Creature target, WorldObject damageSource)
        {
            // Test the target first: every PvE hit passes here, and the item lookup scans the equipped list.
            if (!(target is Player))
                return false;

            var item = PveOnlyHitItem(damageSource);

            if (!PvpPveOnlyRule.BlocksDamage(true, item != null))
                return false;

            NotifyPveOnlyHarmBlocked(item);
            return true;
        }
        /// <summary>The same check for a spell projectile whose launcher is <paramref name="launcher"/> (Spellsword procs and wand casts launch with the weapon).</summary>
        internal bool PveOnlyBlocksSpellHit(Creature target, WorldObject launcher)
        {
            if (!(target is Player))
                return false;

            if (!PvpPveOnlyRule.BlocksDamage(true, IsPveOnlyItem(launcher)))
                return false;

            NotifyPveOnlyHarmBlocked(launcher);
            return true;
        }

        /// <summary>Tells the attacker the item will not harm a player, at most once per <see cref="PveOnlyHarmNoticeIntervalSeconds"/>.</summary>
        internal void NotifyPveOnlyHarmBlocked(WorldObject item)
        {
            var now = DateTime.UtcNow;

            if ((now - pveOnlyHarmNoticeAtUtc).TotalSeconds < PveOnlyHarmNoticeIntervalSeconds)
                return;

            pveOnlyHarmNoticeAtUtc = now;

            Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.PveOnlyWillNotHarm(item?.Name), ChatMessageType.Broadcast));
        }

        /// <summary>
        /// Moves every wielded PvE-only item out of the match: called beside ClearPvpOffHandWeapon at match entry and at kit apply, once
        /// <see cref="IsInPvpMatch"/> or <see cref="IsPvpTemplated"/> is true. An issued kit item is consumed (the restore would destroy it
        /// anyway, and it needs no pack room); the player's own item goes to the pack, and is put back if the pack is full (the damage
        /// block still holds it harmless). Same mechanism and recovery as ClearPvpOffHandWeapon. Never throws. Player queue only.
        /// </summary>
        internal bool ClearPvpPveOnlyItems(string templateKey)
        {
            var any = false;

            try
            {
                foreach (var item in EquippedObjects.Values.Where(IsPveOnlyItem).ToList())
                {
                    if (!PveOnlyEquipRefuses(item))
                        continue;

                    var name = item.Name;
                    var slot = item.CurrentWieldedLocation ?? EquipMask.MeleeWeapon;
                    var issued = ACE.Server.Pvp.Templates.PvpTemplate.IsMarkedIssued(item);

                    if (!TryDequipObjectWithNetworking(item.Guid, out var dequipped, issued ? DequipObjectAction.ConsumeItem : DequipObjectAction.DequipToPack))
                    {
                        PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): could not take the PvE-only item {name} off (template {templateKey ?? "(none)"}); the damage block still applies.");
                        continue;
                    }

                    var gone = issued;

                    if (!issued)
                    {
                        var inPack = false;

                        try
                        {
                            inPack = TryCreateInInventoryWithNetworking(dequipped, out _, burdenCheck: false);
                        }
                        catch (Exception ex)
                        {
                            PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): putting the PvE-only item {name} in the pack threw (template {templateKey ?? "(none)"}).", ex);
                        }

                        if (!inPack)
                        {
                            bool reEquipped;

                            try
                            {
                                reEquipped = TryEquipObjectWithNetworking(dequipped, slot);
                            }
                            catch (Exception ex)
                            {
                                PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): re-equipping the PvE-only item {name} threw (template {templateKey ?? "(none)"}).", ex);
                                reEquipped = HasEquippedItem(dequipped.Guid);
                            }

                            if (reEquipped)
                                PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): no pack room for the PvE-only item {name} (template {templateKey ?? "(none)"}); put back on. The damage block still applies.");
                            else
                                PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): the PvE-only item {name} (0x{dequipped.Guid.Full:X8}) could not go to the pack AND could not be re-equipped (template {templateKey ?? "(none)"}). It is in NEITHER place - manual recovery required.");

                            continue;
                        }

                        gone = true;
                    }

                    if (gone)
                    {
                        any = true;
                        Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.PveOnlyWillNotBeRaised(name), ChatMessageType.Broadcast));
                        PvpArenaHookSettings.Log.Warn($"[PVP] {Name} (0x{Guid.Full:X8}): PvE-only item {name} removed from the match (template {templateKey ?? "(none)"}).");
                    }
                }
            }
            catch (Exception ex)
            {
                PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): clearing PvE-only items (template {templateKey ?? "(none)"}) threw.", ex);
            }

            return any;
        }
    }
}
