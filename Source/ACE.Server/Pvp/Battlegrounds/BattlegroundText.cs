using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// Every player-facing battleground line, authored by the orchestrator and used verbatim. Chat lines carry the
    /// "[Battleground] " prefix. Team 0 is named West and team 1 East, after the wing each one spawns in
    /// (Docs/Pvp/BATTLEGROUNDS.md "Map: 0x016C"). Plain ASCII.
    /// </summary>
    public static class BattlegroundText
    {
        public const string Prefix = "[Battleground] ";

        /// <summary>"West" for team 0, "East" for team 1, "Team N" for anything else.</summary>
        public static string TeamName(int teamIndex) => teamIndex switch
        {
            0 => "West",
            1 => "East",
            _ => $"Team {teamIndex}"
        };

        /// <summary>"West 120, East 45": both scores, West first.</summary>
        public static string ScorePair(int west, int east) => $"West {west}, East {east}";

        // ---------------- the hill ----------------

        public static string ZoneHeld(int teamIndex) => $"{Prefix}{TeamName(teamIndex)} holds the hill!";

        public static string ZoneContested() => $"{Prefix}The hill is contested!";

        public static string ZoneNeutral() => $"{Prefix}The hill stands empty.";

        /// <summary>Announced to every participant when the hill moves; the direction is a compass word from the map centre.</summary>
        public static string HillMoved(string direction) => $"{Prefix}The hill has moved! Head to the {direction}.";

        public static string Scores(int west, int east) => $"{Prefix}Score: {ScorePair(west, east)}.";

        // ---------------- deaths and respawn ----------------

        public static string Slain(string victim, string killer) => $"{Prefix}{victim} was slain by {killer}.";

        public static string SlainNoKiller(string victim) => $"{Prefix}{victim} has fallen.";

        public static string PenArrival(int seconds) => $"{Prefix}You will return to the fight in {seconds} seconds.";

        public static string PenCountdown(int seconds) => $"{Prefix}Returning in {seconds}...";

        // ---------------- results ----------------

        public static string ScoreWin(int teamIndex, int west, int east) => $"{Prefix}{TeamName(teamIndex)} has taken the hill! Final score: {ScorePair(west, east)}.";

        public static string TimeUpWin(int teamIndex, int west, int east) => $"{Prefix}Time is up. {TeamName(teamIndex)} wins, {ScorePair(west, east)}.";

        public static string TimeUpDraw(int west, int east) => $"{Prefix}Time is up. The match is a draw, {ScorePair(west, east)}.";

        // ---------------- status ----------------

        /// <summary>Appended to the /arena status in-match line for a battleground.</summary>
        public static string StatusScore(int west, int east, int target) => $" Score: {ScorePair(west, east)}, first to {target} wins.";

        // ---------------- mode labels ----------------

        /// <summary>The player-facing label for the battleground queue (/arena join bg), also the Crier's.</summary>
        public const string ModeLabelBattleground = "Battleground";

        /// <summary>The player-facing label for the King of the Hill mode a match is formed as.</summary>
        public const string ModeLabelKoth = "King of the Hill";

        /// <summary>The player-facing label for the Attack/Defend mode a match is formed as.</summary>
        public const string ModeLabelAttackDefend = "Attack/Defend";

        // ---------------- Arena Crier ----------------

        public const string CrierPeriodic = "Battleground queue: {count} queued, {needed} more needed. Type /arena join bg to play.";

        public const string CrierLastCall = "Battleground: 1 more player needed to start a match! Type /arena join bg now.";

        /// <summary>Announced when the queue enters its fill window (and, with pvp_arena_crier_announce_on_join, on each further join while it is open). Filled with seconds, count and room.</summary>
        public const string CrierFilling = "Battleground starting in about {seconds} seconds with {count} players - room for {room} more! Type /arena join bg to tag along.";

        // ---------------- team fellowships (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships") ----------------

        /// <summary>The server-built fellowship's name: "West Team" for team 0, "East Team" for team 1.</summary>
        public static string TeamFellowshipName(int teamIndex) => TeamFellowshipName(TeamName(teamIndex));

        /// <summary>The server-built fellowship's name for a team the mode has already named: "West Team", "Attackers Team".</summary>
        public static string TeamFellowshipName(string teamName) => $"{teamName} Team";

        /// <summary>Every player-driven fellowship change refused during a team-fellowship match.</summary>
        public const string TeamFellowshipFixed = "[Battleground] Your team fellowship is fixed for the rest of the match.";

        public const string TeamFellowshipLockedSkip = "[Battleground] You are in a locked fellowship, so you were not added to your team's fellowship.";

        public static string TeamFellowshipRejoined(string name) => $"{Prefix}You have rejoined your fellowship, {name}.";

        public static string TeamFellowshipReformed(string name) => $"{Prefix}Your fellowship {name} has been re-formed.";

        public static string TeamFellowshipEnded(string name) => $"{Prefix}Your previous fellowship, {name}, is no longer available.";

        // ---------------- Attack/Defend (Docs/Pvp/ATTACK-DEFEND.md) ----------------
        //
        // Each line comes as a *Body, the exact wording, and as the chat line the match sends, which is the body behind the
        // "[Battleground] " prefix every other battleground chat line carries. The /arena status suffix has no prefix, like StatusScore.

        /// <summary>Team 0's name in Attack/Defend.</summary>
        public const string TeamNameAttackers = "Attackers";

        /// <summary>Team 1's name in Attack/Defend.</summary>
        public const string TeamNameDefenders = "Defenders";

        /// <summary>"Attackers" for team 0, "Defenders" for team 1, "Team N" for anything else.</summary>
        public static string AttackDefendTeamName(int teamIndex) => teamIndex switch
        {
            0 => TeamNameAttackers,
            1 => TeamNameDefenders,
            _ => $"Team {teamIndex}"
        };

        /// <summary><paramref name="where"/> is the site's hint when it has one, else its compass word (<see cref="PlannedCrystal.Where"/>).</summary>
        public static string CrystalUnderAttackBody(string site, string where, int percentRemaining) =>
            $"The {site} crystal ({where}) is under attack - {percentRemaining}% remaining.";

        /// <summary>Sent to the defenders only.</summary>
        public static string CrystalUnderAttack(string site, string where, int percentRemaining) => Prefix + CrystalUnderAttackBody(site, where, percentRemaining);

        public static string CrystalDestroyedBody(string site, string where, int remaining, int total) =>
            $"The {site} crystal ({where}) has been destroyed! {remaining} of {total} remain.";

        /// <summary>Sent to everyone.</summary>
        public static string CrystalDestroyed(string site, string where, int remaining, int total) => Prefix + CrystalDestroyedBody(site, where, remaining, total);

        /// <summary>
        /// "Crystals: Great Hall (upper level, south-east of the octagon), Pit Hall." for the sites in play: a site without a hint is named alone.
        /// Empty for no sites.
        /// </summary>
        public static string CrystalList(IEnumerable<BattlegroundCrystalSite> sites)
        {
            var parts = (sites ?? Enumerable.Empty<BattlegroundCrystalSite>())
                .Select(s => string.IsNullOrEmpty(s.Hint) ? s.Name : $"{s.Name} ({s.Hint})")
                .ToList();

            return parts.Count == 0 ? "" : "Crystals: " + string.Join(", ", parts) + ".";
        }

        /// <summary>Sent to a player when their respawn is confirmed, with the live window length.</summary>
        public static string SpawnProtectedBody(int seconds) =>
            $"You are protected for {seconds} {(seconds == 1 ? "second" : "seconds")}. Attacking ends it early.";

        public static string SpawnProtected(int seconds) => Prefix + SpawnProtectedBody(seconds);

        /// <summary>Sent when protection is granted on a map with spawn rooms (match start and respawn): in-room protection, then the window after leaving.</summary>
        public static string SpawnProtectedRoomBody(int seconds) =>
            $"You are protected while in your spawn room and for {seconds} {(seconds == 1 ? "second" : "seconds")} after you leave. Attacking ends it.";

        public static string SpawnProtectedRoom(int seconds) => Prefix + SpawnProtectedRoomBody(seconds);

        /// <summary>Sent when a protected player's attack or harmful cast ends the window early. A natural expiry is never announced.</summary>
        public const string SpawnProtectionEndedBody = "Your spawn protection has ended.";

        public const string SpawnProtectionEnded = Prefix + SpawnProtectionEndedBody;

        /// <summary>Sent to an attacker whose hit was refused because the target is inside the window.</summary>
        public static string SpawnProtectedTargetBody(string name) => $"{name} is protected for a moment longer.";

        public static string SpawnProtectedTarget(string name) => Prefix + SpawnProtectedTargetBody(name);

        public const string AttackersWinBody = "The Attackers have destroyed every crystal!";

        public const string DefendersWinBody = "The Defenders held every crystal until time ran out!";

        public static string AttackersWin() => Prefix + AttackersWinBody;

        public static string DefendersWin() => Prefix + DefendersWinBody;

        /// <summary>The defenders' timeout win after at least one crystal fell.</summary>
        public static string DefendersHeldBody(int remaining, int total) => $"Time is up! The Defenders held {remaining} of {total} crystals.";

        public static string DefendersHeld(int remaining, int total) => Prefix + DefendersHeldBody(remaining, total);

        /// <summary>The defenders' timeout line: <see cref="DefendersWin"/> when no crystal fell, else <see cref="DefendersHeld"/>.</summary>
        public static string DefendersTimeoutWin(int destroyed, int total) =>
            destroyed <= 0 ? DefendersWin() : DefendersHeld(Math.Max(0, total - destroyed), total);

        /// <summary>Sent once to each attacker when the match goes Live; a single crystal reads "the Warding Crystal".</summary>
        /// <param name="total">The crystal count.</param>
        /// <param name="crystals">The <see cref="CrystalList"/> line, appended after a space when not empty.</param>
        public static string RoleAttackerBody(int total, string crystals = null) =>
            (total == 1
                ? "You are an Attacker. Destroy the Warding Crystal before time runs out!"
                : $"You are an Attacker. Destroy all {total} Warding Crystals before time runs out!")
            + (string.IsNullOrEmpty(crystals) ? "" : " " + crystals);

        public static string RoleAttacker(int total, string crystals = null) => Prefix + RoleAttackerBody(total, crystals);

        /// <summary>Sent once to each defender when the match goes Live; a single crystal reads "the Warding Crystal".</summary>
        public static string RoleDefenderBody(int total, string crystals = null) =>
            (total == 1
                ? "You are a Defender. Protect the Warding Crystal until time runs out!"
                : "You are a Defender. Protect the Warding Crystals until time runs out!")
            + (string.IsNullOrEmpty(crystals) ? "" : " " + crystals);

        public static string RoleDefender(int total, string crystals = null) => Prefix + RoleDefenderBody(total, crystals);

        /// <summary>"mm:ss" for a whole number of seconds (at least 0).</summary>
        public static string Clock(int seconds)
        {
            var s = seconds < 0 ? 0 : seconds;

            return $"{s / 60:D2}:{s % 60:D2}";
        }

        /// <param name="standing">The names of the crystals still standing; " Standing: A, B." is appended when there are any.</param>
        /// <param name="vulnerable">Sequential mode: the one crystal that takes damage; " Vulnerable: X." is appended when given.</param>
        public static string CrystalStatusBody(int destroyed, int total, int secondsRemaining, IEnumerable<string> standing = null, string vulnerable = null)
        {
            var names = (standing ?? Enumerable.Empty<string>()).ToList();

            return $"Crystals destroyed: {destroyed} of {total}. {Clock(secondsRemaining)} remaining."
                + (names.Count == 0 ? "" : $" Standing: {string.Join(", ", names)}.")
                + StatusVulnerable(vulnerable);
        }

        /// <summary>The periodic status announcement.</summary>
        public static string CrystalStatus(int destroyed, int total, int secondsRemaining, IEnumerable<string> standing = null, string vulnerable = null) => Prefix + CrystalStatusBody(destroyed, total, secondsRemaining, standing, vulnerable);

        /// <summary>Appended to the /arena status in-match line (which already names the time left): " Crystals destroyed: 1 of 3.".</summary>
        public static string StatusCrystals(int destroyed, int total) => $" Crystals destroyed: {destroyed} of {total}.";

        public const string RefusalOwnCrystalBody = "You cannot damage your own team's crystal.";

        public const string RefusalCrystalBody = "You cannot damage that crystal.";

        public const string RefusalOwnCrystal = Prefix + RefusalOwnCrystalBody;

        public const string RefusalCrystal = Prefix + RefusalCrystalBody;

        /// <summary>The refusal line for a gate reason: the own-team line for <see cref="ObjectiveGateReason.OwnTeam"/>, the generic one for every other refusal, null when allowed or the target is no crystal.</summary>
        public static string CrystalRefusal(ObjectiveGateReason reason) => reason switch
        {
            ObjectiveGateReason.Allowed => null,
            ObjectiveGateReason.NoTag => null,
            ObjectiveGateReason.OwnTeam => RefusalOwnCrystal,
            _ => RefusalCrystal
        };

        /// <summary>
        /// <see cref="CrystalRefusal(ObjectiveGateReason)"/> for a crystal that may be sealed: <see cref="ObjectiveGateReason.CrystalSealed"/>
        /// reads the sealed line naming both crystals (<see cref="CrystalSealed"/>), and falls back to the generic refusal when the match
        /// has no sequence to name them from; every other reason is unchanged.
        /// </summary>
        public static string CrystalRefusal(ObjectiveGateReason reason, CrystalSequence sequence, int crystalIndex)
        {
            if (reason != ObjectiveGateReason.CrystalSealed)
                return CrystalRefusal(reason);

            var sealedName = sequence?.NameOf(crystalIndex);
            var currentName = sequence?.CurrentName;

            // The cursor was read twice; if it moved between the reads, or nothing is left, "destroy X first" would name the crystal itself
            // or nothing, so say the plain refusal instead.
            if (sealedName == null || currentName == null || sealedName == currentName)
                return RefusalCrystal;

            return CrystalSealed(sealedName, currentName);
        }

        // ---------------- sequential crystals (Docs/Pvp/ATTACK-DEFEND.md "Sequential crystals") ----------------

        /// <summary>"The West Cavern crystal is sealed. Destroy the Great Hall crystal first." Sent to an attacker who hits a locked crystal.</summary>
        public static string CrystalSealedBody(string sealedSite, string currentSite) =>
            $"The {sealedSite} crystal is sealed. Destroy the {currentSite} crystal first.";

        /// <summary>The attacker's refusal line for a locked crystal.</summary>
        public static string CrystalSealed(string sealedSite, string currentSite) => Prefix + CrystalSealedBody(sealedSite, currentSite);

        /// <summary>"The West Cavern crystal (lower level, south from the octagon past the Defender room, then down the stairs) is now vulnerable!" Broadcast to everyone when the crystal before it falls.</summary>
        public static string CrystalUnlockedBody(string site, string where) =>
            string.IsNullOrEmpty(where) ? $"The {site} crystal is now vulnerable!" : $"The {site} crystal ({where}) is now vulnerable!";

        public static string CrystalUnlocked(string site, string where) => Prefix + CrystalUnlockedBody(site, where);

        /// <summary>
        /// "Destroy the Warding Crystals in order: 1. Great Hall (upper level, south-east of the octagon), 2. West Cavern (lower level, south from the octagon past the Defender room, then down the stairs)." for
        /// the sites in play, in order; <paramref name="verb"/> is "Destroy" for the attackers and "Protect" for the defenders. A site
        /// without a hint is named alone. Empty for no sites.
        /// </summary>
        public static string CrystalOrder(string verb, IEnumerable<BattlegroundCrystalSite> sites)
        {
            var parts = (sites ?? Enumerable.Empty<BattlegroundCrystalSite>())
                .Select((s, i) => $"{i + 1}. " + (string.IsNullOrEmpty(s.Hint) ? s.Name : $"{s.Name} ({s.Hint})"))
                .ToList();

            return parts.Count == 0 ? "" : $"{verb} the Warding Crystals in order: " + string.Join(", ", parts) + ".";
        }

        /// <summary>The attacker's role line in sequential mode: "You are an Attacker. Destroy the Warding Crystals in order: 1. ..., 2. ... before time runs out!"</summary>
        public static string RoleAttackerOrdered(IEnumerable<BattlegroundCrystalSite> sites) => Prefix + "You are an Attacker. " + CrystalOrder("Destroy", sites).TrimEnd('.') + " before time runs out!";

        /// <summary>The defender's role line in sequential mode: "You are a Defender. Protect the Warding Crystals in order: 1. ..., 2. ... until time runs out!"</summary>
        public static string RoleDefenderOrdered(IEnumerable<BattlegroundCrystalSite> sites) => Prefix + "You are a Defender. " + CrystalOrder("Protect", sites).TrimEnd('.') + " until time runs out!";

        /// <summary>"Vulnerable crystal: West Cavern (hint)." Sent to a player whose respawn is confirmed, in sequential mode.</summary>
        public static string VulnerableCrystal(string site, string where) =>
            Prefix + (string.IsNullOrEmpty(where) ? $"Vulnerable crystal: {site}." : $"Vulnerable crystal: {site} ({where}).");

        /// <summary>Appended to the /arena status in-match line in sequential mode: " Vulnerable: Great Hall.". Empty when no crystal is left.</summary>
        public static string StatusVulnerable(string site) => string.IsNullOrEmpty(site) ? "" : $" Vulnerable: {site}.";

        // ---------------- group join ----------------

        public const string GroupNeedsFellowship = "[Battleground] To queue as a group you must be in a fellowship of 2 to {max} players, and every member must be eligible.";

        public const string GroupMemberIneligible = "[Battleground] {member} cannot join the battleground right now.";

        public const string GroupNotSupportedForMode = "[Battleground] Only the battleground queue takes a group. Use /arena join bg group.";
    }
}
