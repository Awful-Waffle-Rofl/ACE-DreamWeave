using System;

using ACE.Server.Entity;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// Pure, static MVP attribution helpers shared by every WP-06 objective (TECH-DESIGN 2.5). Nothing here
    /// touches a live WorldObject: callers resolve names before calling in, which is also what keeps this
    /// unit-testable without a live Player/Session (D6).
    ///
    /// <see cref="MostKills"/> implements the "mostKills" goals.json rule and is used only by
    /// <see cref="KillCountObjective"/>, whose goal carries a real <c>mvpRule</c>. DestroySource and
    /// KillBoss are not driven by <c>mvpRule</c> at all (TECH-DESIGN 2.5 fixes their MVP shape directly to
    /// "killing blow on the last rift/boss, plus top damage if different") - both call
    /// <see cref="KillerPlusTopDamage"/>, the one shared implementation behind every "killer plus top
    /// damager, only if they differ" MVP shape in this file, with their own Reason wording.
    /// </summary>
    public static class WorldEventMvpResolver
    {
        /// <summary>
        /// A pet's damage/kill resolves to its owner, else the direct attacker - the same rule
        /// WorldEventParticipation.ResolvePlayer applies, but usable here without a live Player, since an
        /// objective only ever needs the display name.
        /// </summary>
        public static string ResolveName(DamageHistoryInfo info)
        {
            if (info == null)
                return null;

            return info.TryGetPetOwnerOrAttacker()?.Name ?? info.Name;
        }

        /// <summary>goals.json mvpRule "mostKills". Ledger's own tie-break (earliest FirstCredit) applies.</summary>
        public static WorldEventMvp MostKills(WorldEventParticipation ledger)
        {
            var top = ledger?.TopKiller();

            if (top == null || string.IsNullOrEmpty(top.Name))
                return WorldEventMvp.None;

            return new WorldEventMvp(top.Name, "most kills", top.Kills);
        }

        /// <summary>goals.json mvpRule "killingBlow".</summary>
        public static WorldEventMvp KillingBlow(string killerName)
        {
            if (string.IsNullOrEmpty(killerName))
                return WorldEventMvp.None;

            return new WorldEventMvp(killerName, "killing blow", 0);
        }

        /// <summary>goals.json mvpRule "killingBlowAndTopDamage". Reason text matches WorldEventAnnouncer's
        /// recognised "killing blow" sentence when the killer and top damager are the same person. When they
        /// differ there is no single recognised MvpSentence phrase for "killed it AND dealt the most
        /// damage", so this falls back to WorldEventAnnouncer.MvpSentence's raw-text default branch (which
        /// appends the Reason text verbatim after the MVP's name) - the combo Reason is therefore written as
        /// a full verb phrase ("struck the killing blow, top damage: X") rather than a bare noun phrase, so
        /// the fallback still reads as a sentence (code review finding on #602).</summary>
        public static WorldEventMvp KillingBlowAndTopDamage(string killerName, string topDamagerName)
        {
            return KillerPlusTopDamage(killerName, topDamagerName, "killing blow", "struck the killing blow, top damage: {0}");
        }

        /// <summary>
        /// Shared core: <paramref name="killerName"/> is the MVP; when <paramref name="topDamagerName"/> is
        /// present and different, the Reason names both via <paramref name="comboReasonFormat"/> (one
        /// "{0}" placeholder for the top damager's name). No MVP at all when there is no killer name.
        /// </summary>
        public static WorldEventMvp KillerPlusTopDamage(string killerName, string topDamagerName,
            string soloReason, string comboReasonFormat)
        {
            if (string.IsNullOrEmpty(killerName))
                return WorldEventMvp.None;

            if (string.IsNullOrEmpty(topDamagerName) || string.Equals(topDamagerName, killerName, StringComparison.Ordinal))
                return new WorldEventMvp(killerName, soloReason, 0);

            return new WorldEventMvp(killerName, string.Format(comboReasonFormat, topDamagerName), 0);
        }
    }
}
