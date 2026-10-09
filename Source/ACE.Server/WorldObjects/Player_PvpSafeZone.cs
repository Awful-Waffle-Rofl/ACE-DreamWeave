using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The Marketplace combat-free zone (owner report: "I was able to shoot another PK in the Marketplace.
    /// Marketplace should be a combat free zone."; Docs/Runbook.md "pvp_safe_landblocks"). PvP harm is refused
    /// when either the attacker or the target stands in a landblock listed in pvp_safe_landblocks - landblock
    /// based, so it applies regardless of PlayerKillerStatus. Checked in Player.CheckPKStatusVsTarget right
    /// after the arena gate (DESIGN H6) and, like that gate, ahead of every retail PK rule below it: a
    /// zone-level refusal must never be reachable through a PK-status loophole.
    /// </summary>
    public partial class Player
    {
        private DateTime pvpSafeZoneNoticeAtUtc;

        /// <summary>
        /// Returns false when neither party's landblock is in the configured safe set (including when either
        /// party's landblock cannot be resolved) - not this rule's call, so the retail rules run unchanged.
        /// Returns true when it decided: <paramref name="result"/> is always the "cannot affect anyone" pair,
        /// since this rule only ever refuses, never allows something the retail rules would otherwise refuse.
        /// </summary>
        internal bool TryPvpSafeZoneGate(Player targetPlayer, out List<WeenieErrorWithString> result)
        {
            result = null;

            if (targetPlayer == null)
                return false;

            var attackerLandblock = Location?.LandblockShort;
            var targetLandblock = targetPlayer.Location?.LandblockShort;

            if (attackerLandblock == null || targetLandblock == null)
                return false;

            var safeLandblocks = PvpSafeZoneTunables.DialSource().SafeLandblocks;

            if (!PvpSafeZoneRules.IsPvpSafe(
                    (ushort)attackerLandblock.Value, Location.RealmID, Location.IsEphemeralRealm,
                    (ushort)targetLandblock.Value, targetPlayer.Location.RealmID, targetPlayer.Location.IsEphemeralRealm,
                    safeLandblocks))
            {
                return false;
            }

            SendPvpSafeZoneNotice();

            result = new List<WeenieErrorWithString>() { WeenieErrorWithString.YouFailToAffect_YouCannotAffectAnyone, WeenieErrorWithString._FailsToAffectYou_TheyCannotAffectAnyone };
            return true;
        }

        private void SendPvpSafeZoneNotice()
        {
            var now = DateTime.UtcNow;

            if ((now - pvpSafeZoneNoticeAtUtc).TotalSeconds < PvpSafeZoneText.NoticeIntervalSeconds)
                return;

            pvpSafeZoneNoticeAtUtc = now;

            Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpSafeZoneText.CombatFreeZone, ChatMessageType.Broadcast));
        }
    }
}
