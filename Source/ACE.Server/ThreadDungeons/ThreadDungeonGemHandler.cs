using System;
using System.Globalization;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    public enum GemUseDecision
    {
        Refuse,
        StartRun,
        ReEnter,
        ConsumeDeadGem,
    }

    /// <summary>What a re-entry has to do about the copy's admission list before the player is teleported.</summary>
    public enum ReEntryAdmission
    {
        /// <summary>The realm already accepts this player; teleport as-is.</summary>
        Enter,

        /// <summary>The rightful owner is no longer the realm's Owner REFERENCE (they relogged); re-admit them.</summary>
        Admit,

        /// <summary>Not the owner and not accepted; refuse.</summary>
        Refuse,
    }

    /// <summary>
    /// Pure decision table for a Thread Gem use (PLAN 3.4, TECH-DESIGN S9). The min-player-level gate
    /// (section 3) applies ONLY to opening a fresh gem: a bound gem was already gated when it was
    /// opened, and gating re-entry too (ruling P2-R21) would strand a player below the CURRENT tunable
    /// value outside their own live run - evicted by death, or logged out at town - with a gem whose
    /// entries become permanently unspendable. So the check lives inside the unbound branch, not before
    /// it.
    /// </summary>
    public static class DungeonGemRules
    {
        public static GemUseDecision Decide(DungeonGemSpec spec, uint userGuid, int structure, bool runIsLive, bool enabled, int playerLevel, int minPlayerLevel, out string reason)
        {
            reason = null;

            if (!enabled) { reason = "Threads are not enabled on this server."; return GemUseDecision.Refuse; }

            if (!spec.IsBound)
            {
                if (playerLevel < minPlayerLevel) { reason = "The fragment does not answer you yet."; return GemUseDecision.Refuse; }
                return GemUseDecision.StartRun;
            }

            if (!runIsLive)
                return GemUseDecision.ConsumeDeadGem;

            if (spec.OwnerGuid != userGuid) { reason = "This gem is bound to another adventurer's dungeon."; return GemUseDecision.Refuse; }

            if (structure <= 0) { reason = "This gem has no entries left. It will crumble when the dungeon closes."; return GemUseDecision.Refuse; }

            return GemUseDecision.ReEnter;
        }

        public static int EntriesAfterUse(int structure) => Math.Max(0, structure - 1);

        /// <summary>
        /// The admission half of a re-entry, kept pure so it can be tested without a landblock.
        ///
        /// EphemeralRealm.Accepts compares the player to Owner by REFERENCE (EphemeralRealm.cs:69) and
        /// AllowedPlayers starts empty, while every login builds a NEW Player object (WorldManager.cs:266).
        /// So an owner who relogs stops being accepted by their own dungeon; without this the re-entry would
        /// pass the landblock check, spend an entry, and then be silently rerouted onto the PUBLIC landblock
        /// by ValidateInstanceDestination (InstanceRouting.cs:55-62). Guid ownership is the durable test, so
        /// the owner is re-admitted by guid and anyone else is refused.
        /// </summary>
        public static ReEntryAdmission DecideReEntry(bool accepted, bool ownerMatches, out string reason)
        {
            reason = null;

            if (accepted)
                return ReEntryAdmission.Enter;

            if (!ownerMatches)
            {
                reason = "This gem is bound to another adventurer's dungeon.";
                return ReEntryAdmission.Refuse;
            }

            return ReEntryAdmission.Admit;
        }

        /// <summary>
        /// TECH-DESIGN section 6 step 2: a gem cannot open (or lazily consume into) a dungeon from inside
        /// another ephemeral instance - that would nest a private copy inside a private copy. The one
        /// allowed ephemeral case is re-entering the gem's OWN bound run, which this refuses on its own,
        /// more specific terms ("already inside") rather than falling through to Decide - checked here,
        /// BEFORE Decide, so a zero-entry gem used inside its own run reports "already inside" instead of
        /// "no entries left".
        /// </summary>
        public static bool RefusesFromInstance(DungeonGemSpec spec, uint currentInstance, bool isEphemeral, out string reason)
        {
            reason = null;

            if (spec.IsBound && currentInstance == spec.RunId)
            {
                reason = "You are already inside this dungeon.";
                return true;
            }

            if (isEphemeral)
            {
                reason = "You cannot open a dungeon from inside another instance.";
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The Gem.UseGem branch for Thread Gems. Everything player-facing about a gem use lives here; the
    /// manager owns the run, the spawner owns the population.
    /// </summary>
    public static class ThreadDungeonGemHandler
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The Raw Fragment's flavour line, first line of its appraisal (ruling P2-R20). Composed rather than
        /// baked into the ten rung weenies alone, so it survives every load and every re-open; the shipped
        /// rung SQL carries the same text because that SQL is this composer's output verbatim.
        /// </summary>
        public const string FragmentFlavourLine = "Meridian issue. A fragment of a door with no wall behind it.";

        /// <returns>true when this was a dungeon gem (handled or refused); false when UseGem should continue.</returns>
        public static bool TryHandleUse(Gem gem, Player player)
        {
            var text = gem.GetProperty(PropertyString.DungeonGemSpec);
            if (string.IsNullOrEmpty(text))
                return false;

            if (!DungeonGemSpec.TryParse(text, out var spec, out var error))
            {
                log.Error($"[DYNDUNGEON] gem 0x{gem.Guid.Full:X8} held by {player.Name} has an unparseable spec '{text}': {error}");
                Say(player, "This gem's inscription is unreadable. Ask an admin.");
                return true;
            }

            // A Raw Fragment carries the same property but always has seed=0 (the press rolls the seed).
            // The client cannot send a target-less use for an item with a Usable Target bit (Gem.cs's
            // mule-token comment, seen live 2026-09-02), but a data error could, and an unpressed fragment
            // must never open a run. Safe against existing data: DungeonGemRoller rolls seed 1..int.MaxValue
            // and the shipped Thread Gem weenie carries seed=1.
            if (spec.Seed == 0)
            {
                Say(player, "This fragment is not finished. Take it to the Fragment Press.");
                return true;
            }

            if (player.CombatMode != CombatMode.NonCombat)
            {
                Say(player, $"You cannot use the {gem.Name} while in combat.");
                return true;
            }

            if (player.PKTimerActive)
            {
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return true;
            }

            if (DungeonGemRules.RefusesFromInstance(spec, player.Location.Instance, player.Location.IsEphemeralRealm, out var instanceRefusal))
            {
                Say(player, instanceRefusal);
                return true;
            }

            var run = spec.IsBound ? ThreadDungeonManager.GetRun(spec.RunId) : null;
            var runIsLive = run != null && run.GemGuid == gem.Guid.Full;
            var minPlayerLevel = (int)Math.Clamp(PropertyManager.GetLong("dynamic_dungeons_min_player_level").Item, 0, int.MaxValue);
            var decision = DungeonGemRules.Decide(spec, player.Guid.Full, gem.Structure ?? 0, runIsLive, ThreadDungeonManager.IsEnabled, player.Level ?? 0, minPlayerLevel, out var reason);

            switch (decision)
            {
                case GemUseDecision.Refuse:
                    Say(player, reason);
                    return true;

                case GemUseDecision.ConsumeDeadGem:
                    player.TryConsumeFromInventoryWithNetworking(gem);
                    Say(player, $"The {gem.Name} crumbles to dust; its dungeon is gone.");
                    log.Info($"[DYNDUNGEON] lazy-consumed dead gem 0x{gem.Guid.Full:X8} (run 0x{spec.RunId:X8}) from {player.Name}");
                    return true;

                case GemUseDecision.ReEnter:
                    // The "already inside" case is caught above by RefusesFromInstance, before Decide ever
                    // runs. What is caught here is the 15 s window between a run's landblock unloading and
                    // ThreadDungeonManager's reap: the run is still "live" in the registry (EndRun has not
                    // run yet), but LandblockManager has already torn the copy down, so a teleport into
                    // run.EntryPosition would fall through ValidateInstanceDestination onto the PUBLIC
                    // landblock at that cell instead. Same lookup the manager's own Tick/ShouldEnd reap uses.
                    var copy = LandblockManager.GetEphemeralLandblock(run.Instance);
                    var realm = copy?.InnerRealmInfo;
                    if (realm == null)
                    {
                        Say(player, "That dungeon has already closed.");
                        return true;
                    }

                    // Admission, BEFORE an entry is spent. See DungeonGemRules.DecideReEntry: a relogged owner
                    // is a different Player object than the one the copy's realm holds, so Accepts says no and
                    // the teleport below would be rerouted onto the public landblock with the entry already
                    // gone. Re-admit them by guid here; anyone else is refused.
                    var admission = DungeonGemRules.DecideReEntry(realm.Accepts(player), run.OwnerGuid == player.Guid.Full, out var admissionRefusal);
                    if (admission == ReEntryAdmission.Refuse)
                    {
                        Say(player, admissionRefusal);
                        return true;
                    }
                    if (admission == ReEntryAdmission.Admit)
                    {
                        realm.Admit(player);
                        log.Info($"[DYNDUNGEON] re-admitted owner {player.Name} (0x{player.Guid.Full:X8}) to {run} after a stale Owner reference");
                    }

                    player.UpdateProperty(gem, PropertyInt.Structure, DungeonGemRules.EntriesAfterUse(gem.Structure ?? 0));
                    player.UpdateProperty(gem, PropertyString.LongDesc, ComposeLongDesc(spec, run.Dungeon.Name, gem.Structure ?? 0, gem.MaxStructure ?? 0));
                    player.EnqueueBroadcast(new GameMessageUpdateObject(gem));
                    Teleport(player, run, $"You step back into {run.Dungeon.Name}. Entries left: {gem.Structure ?? 0}.");
                    return true;

                case GemUseDecision.StartRun:
                    return StartRun(gem, player, spec);
            }

            return true;
        }

        private static bool StartRun(Gem gem, Player player, DungeonGemSpec spec)
        {
            var store = ThreadDungeonManager.Store;
            Defs.DungeonEntryDef dungeon = null;

            if (spec.DungeonId != DungeonGemSpec.Any)
                store.Dungeons.TryGetValue(spec.DungeonId, out dungeon);
            else
            {
                var rng = new Random(spec.Seed);
                var candidates = new System.Collections.Generic.List<Defs.DungeonEntryDef>();
                foreach (var d in store.Dungeons.Values)
                    if (d.Enabled && spec.Level >= d.MinLevel && spec.Level <= d.MaxLevel)
                        candidates.Add(d);
                if (candidates.Count > 0)
                    dungeon = candidates[rng.Next(candidates.Count)];
            }

            if (dungeon == null)
            {
                Say(player, "No dungeon answers this gem's call right now.");
                return true;
            }

            if (!ThreadDungeonManager.TryStart(player, gem, spec, dungeon, out var run, out var reason))
            {
                Say(player, reason);
                return true;
            }

            var bound = spec.WithBinding(run.Instance, player.Guid.Full);
            player.UpdateProperty(gem, PropertyString.DungeonGemSpec, bound.Serialize());
            player.UpdateProperty(gem, PropertyString.LongDesc, ComposeLongDesc(bound, dungeon.Name, gem.Structure ?? 0, gem.MaxStructure ?? 0));
            player.EnqueueBroadcast(new GameMessageUpdateObject(gem));

            Teleport(player, run, $"The {gem.Name} opens a way into {dungeon.Name}. It will bring you back {gem.Structure ?? 0} more time(s) while the dungeon stands.");
            return true;
        }

        private static void Teleport(Player player, ThreadDungeonRun run, string message)
        {
            // TECH-DESIGN step 7': stamped from the player's CURRENT location on every entry (first use
            // and every re-entry alike), not run.ExitTo - a death return or the exit portal must drop the
            // player where they used the gem THIS time, not where they used it the first time. run.ExitTo
            // itself is left untouched: EndRun's eviction still reads it as the fallback return point.
            player.SetPosition(PositionType.EphemeralRealmExitTo, new Position(player.Location));
            Say(player, message);
            WorldManager.ThreadSafeTeleport(player, new Position(run.EntryPosition), new ActionEventDelegate(() =>
            {
                player.SendWeenieError(WeenieError.ITeleported);
            }), true);
        }

        /// <summary>
        /// Resolves modifier ids and component wcids through the live store; used by every real caller.
        ///
        /// This overload is also where the three reward-product CEILINGS, the two reward-product SCALES
        /// (owner report, 2026-09-06: "Wanted to confirm that the total xp/lum multiplier will be shown on
        /// the gem as well"), and the gem-level reward-scale RATIO (owner report, 2026-09-07: the LongDesc
        /// gap this fixes - see <see cref="ThreadDungeonSpawner.ResolveRewardScaleRatio"/>) are resolved, from
        /// the same tunables the population builder reads, so the gem quotes the number a run will actually
        /// pay rather than an uncapped, unscaled, un-leveled one. The pure core takes them all as parameters
        /// instead: a PropertyManager read throws under the unit-test harness, and the whole point of the
        /// split is that the core is testable without one.
        ///
        /// The two scales are read through <see cref="ThreadDungeonSpawner.SanitizeDoubleDial"/> - the SAME
        /// function <see cref="ThreadDungeonSpawner"/> runs the tunable through before it reaches
        /// DungeonSpawnPlan.XpScale/LumScale - rather than raw, unlike the three caps above (which ARE read
        /// raw here, but that is not a discrepancy: DungeonRewardMath.Product enforces the cap itself via
        /// Math.Min, so both sides already apply the identical clamp independently and symmetrically). The
        /// scale has no such self-enforcing consumer - XpForKill multiplies it in directly with no clamp of
        /// its own - so an unsanitized read here would let a misconfigured tunable (above
        /// DungeonPopulationLimits.MaxRewardScale, or negative, or NaN/Infinity) print a number on the item
        /// the run does not actually pay: BUG CAUGHT IN REVIEW, 2026-09-07 (an admin setting the tunable to 50
        /// would see runs clamped to 20 while every gem printed "Experience: x50.00"; a negative value would
        /// sanitize to the 2.0 default for gameplay while composing a nonsensical "Experience: x-2.70" on the
        /// item, since AddScaledMultiplierLine guards only NaN). Calling the spawner's own sanitizer rather
        /// than re-deriving the same clamp here is deliberate: a duplicated clamp is exactly how the two sides
        /// drifted apart in the first place.
        ///
        /// The RATIO is resolved through <see cref="ThreadDungeonSpawner.ResolveRewardScaleRatio"/> instead,
        /// the SAME method - not a re-derivation - <see cref="ThreadDungeonSpawner.TryPopulate"/> effectively
        /// uses to build a run's DungeonPopulationLimits, keyed off spec.Level: THE LONGDESC GAP FOUND IN
        /// REVIEW, 2026-09-07. Before this, the gem-level reward-scale curve landed in DungeonPopulationBuilder
        /// only, so a level-185 gem's description kept advertising the flat "Experience: x2.00" total even
        /// though the run it opened actually paid x1.25 - a description that was TRUE before that change and
        /// FALSE for every level away from the anchor after it. Passing the single resolved ratio (rather than
        /// re-threading all five underlying tunables through this call site) keeps the pure core's parameter
        /// list from doubling in size for a single derived number.
        /// </summary>
        public static string ComposeLongDesc(DungeonGemSpec spec, string dungeonName, int entries, int maxEntries)
            => ComposeLongDesc(spec, dungeonName, entries, maxEntries, LookupModifier, LookupComponentName,
                PropertyManager.GetDouble("dynamic_dungeons_xp_mult_cap").Item,
                PropertyManager.GetDouble("dynamic_dungeons_lum_mult_cap").Item,
                PropertyManager.GetDouble("dynamic_dungeons_loot_quantity_cap").Item,
                LookupComponent,
                ThreadDungeonSpawner.SanitizeDoubleDial(PropertyManager.GetDouble("dynamic_dungeons_xp_scale").Item,
                    DungeonPopulationLimits.DefaultXpScale, DungeonPopulationLimits.MaxRewardScale),
                ThreadDungeonSpawner.SanitizeDoubleDial(PropertyManager.GetDouble("dynamic_dungeons_lum_scale").Item,
                    DungeonPopulationLimits.DefaultLumScale, DungeonPopulationLimits.MaxRewardScale),
                ThreadDungeonSpawner.ResolveRewardScaleRatio(spec.Level));

        private static ModifierDef LookupModifier(string id)
            => ThreadDungeonManager.Store.Modifiers.TryGetValue(id, out var def) ? def : null;

        private static string LookupComponentName(uint wcid)
            => ThreadDungeonManager.Store.Attunement.DisplayName(wcid);

        private static ComponentDef LookupComponent(uint wcid)
            => ThreadDungeonManager.Store.Attunement.TryGet(wcid, out var component) ? component : null;

        /// <summary>
        /// Pure core, kept apart from the store-bound overload above so it is testable without
        /// ThreadDungeonManager. One property per line (the client renders \n in an appraisal description),
        /// exact layout:
        ///   Meridian issue. ...      (ONLY on a fragment, spec.Seed == 0; see FragmentFlavourLine)
        ///   Dungeon: {name}          (or "Any" when spec.DungeonId is "any" and the gem is still unbound)
        ///   Level: {level}
        ///   Loot Tier: {tier}
        ///   Modifiers:               (or "Modifiers: none" alone when there are no modifiers)
        ///   {Display}: {effect}      (one line per modifier, in spec order, " (locked)" when pinned)
        ///   Pressed: {components}    (ONLY on a fragment, spec.Seed == 0; "nothing" when nothing is loaded)
        ///   Possible effects:        (fragment only, ONLY when a loaded component's ops can add a modifier; see AddPossibleEffectLines)
        ///   {n} {category} modifier(s)  (one line per reachable CATEGORY, in load order; never a modifier name)
        ///   Experience: x{n.nn} (x{n.nn} from this gem)   (ONLY when the TOTAL, product * xpScale, differs from 1.00; parenthetical omitted when it would repeat the total - see AddScaledMultiplierLine)
        ///   Boss Experience: x{n.nn} (ONLY when the capped BOSS-target product differs from 1.00; NO scale, see the reward block)
        ///   Luminance: x{n.nn} (x{n.nn} from this gem)     (same total/parenthetical rule as Experience, using lumScale)
        ///   Loot Quantity: x{n.nn}   (ONLY when the gem-level-scaled capped product differs from 1.00; no separate xpScale/lumScale-style dial exists for this axis, only the reward-scale ratio)
        ///   Entries: {entries} of {maxEntries}
        ///   Bound to {name}. It crumbles when the dungeon is cleared or its last entry is spent; a death after the clear leaves no way back in. | Unbound. Use it to open a dungeon. | Unpressed. Load spell components onto it, then press it at the Fragment Press.
        ///
        /// The Pressed: line is emitted only for a fragment because a Thread Gem's load is always empty
        /// (DungeonGemFactory enforces it), so on a gem the line would be permanent noise. The ten shipped
        /// rung weenies' LongDesc is exactly this function's output for their shipped spec, prefix included
        /// (ruling P2-R20) - the flavour line is composed rather than being content-only, so it survives
        /// every load, every re-open, and any later rung.
        ///
        /// THE REWARD BLOCK (owner report, 2026-09-07: "It seemed like an experience bonus was in place but
        /// this was not apparent on the gem (it should be)"; follow-up the same day: "Wanted to confirm that
        /// the total xp/lum multiplier will be shown on the gem as well after these changes" - answered here
        /// by folding dynamic_dungeons_xp_scale / dynamic_dungeons_lum_scale into the Experience and Luminance
        /// lines). Four decisions, none of them arbitrary:
        ///
        /// 1. SUMMARY lines, never a per-modifier suffix. <see cref="RenderEffect"/> describes what a modifier
        ///    does to the MONSTERS and says nothing about the reward side, which is where the gap came from.
        ///    The fix is not to append a reward figure to each modifier line, because
        ///    DungeonRewardMath.XpMultiplier / LumMultiplier / LootQuantityMultiplier are CAPPED products
        ///    across all of a gem's modifiers: per-modifier figures would not compose to the number actually
        ///    applied, and they would diverge from it exactly when the cap binds - the one case where a player
        ///    most needs the truth. One line per axis, carrying the same value the run will pay, cannot lie
        ///    that way.
        ///
        ///    XP is TWO axes, not one, and that is why there are two experience lines. DungeonRewardMath
        ///    splits the XP product on the modifier's Target: XpMultiplier takes only the non-boss modifiers
        ///    (DungeonRewardMath.cs:98, filter d.Target != "boss") and applies to EVERY kill, while
        ///    BossXpMultiplier takes only the boss-target ones (:102) and XpForKill multiplies it on top for
        ///    the boss kill alone (:132). They must not be combined into a single figure: their product is a
        ///    number no single kill ever pays - trash pays the first, the boss pays both - so one merged line
        ///    would overstate every trash kill and be the only line a player could check it against.
        ///
        ///    Reporting only XpMultiplier was the original bug in this block, and it reproduced the exact
        ///    complaint the block exists to answer. Two shipped COMMON modifiers are boss-target and move XP:
        ///    boss_enraged (rewardXpSlope 0.005 over magnitude 20-60, so 1.10x to 1.30x) and boss_guarded
        ///    (rewardXpSlope 0.15 over magnitude 1.5-3.0, so 1.225x to 1.45x)
        ///    (Content/dungeons/dynamic/modifiers.json). A gem rolled with only boss_guarded has an
        ///    XpMultiplier of exactly 1.0, so the differs-from-1.00 rule suppressed the line entirely and the
        ///    gem advertised no experience bonus while the run paid up to 45% extra on the boss.
        ///
        ///    LUMINANCE and LOOT QUANTITY need no equivalent, and this was checked rather than assumed.
        ///    LumMultiplier passes NULL as its target filter (DungeonRewardMath.cs:106) and Product skips
        ///    filtering entirely when it is null (:88), so boss-target modifiers are already inside the single
        ///    luminance product; there is no BossLumMultiplier on the plan (DungeonPopulationBuilder.cs:197-199)
        ///    and LuminanceFor applies plan.LumMultiplier with no boss-specific factor (:601).
        ///    LootQuantityMultiplier has no target filter at all (:273-284). One line each is therefore
        ///    complete for both.
        ///
        /// 2. RoleRate is EXCLUDED from every line on purpose, XP and luminance alike.
        ///    <see cref="DungeonRewardMath.RoleRate"/> is 1.0 for a trash kill, 1.5 for an elite, 2.0 for the
        ///    boss (DungeonRewardMath.cs:70-76) - it describes WHICH CREATURE was killed, not anything about
        ///    this gem, and it is the same for every gem in the game. Folding it in would make the Experience
        ///    line mean three different things depending on which of a run's monsters the reader has in mind,
        ///    with no way to tell from the line itself which one. Excluding it means the Experience line reads
        ///    as "what an ordinary (trash-role) monster in this run pays, relative to one killed outside a
        ///    dungeon" - a single, fixed referent - and the same reading carries over to Luminance since
        ///    LuminanceFor multiplies by the identical RoleRate(role) term (DungeonPopulationBuilder.cs:601).
        ///
        /// 3. Experience and Luminance show the TOTAL - modifier product * the axis's global scale
        ///    (dynamic_dungeons_xp_scale / dynamic_dungeons_lum_scale, 2.0 by default) - with the gem's own
        ///    modifier product added as a parenthetical whenever showing it would say something the total does
        ///    not already say (see <see cref="AddScaledMultiplierLine"/> for the exact rounded-text
        ///    comparison): "Experience: x2.32 (x1.16 from this gem)" on a modified gem, "Experience: x2.00"
        ///    alone on a plain one (product == 1.00, nothing to add), and "Experience: x1.16" alone when the
        ///    scale itself is 1.0 (product and total are the same number, so the parenthetical would just
        ///    repeat the total). This REVERSES an earlier version of this comment, which argued the scale should never
        ///    appear on a gem because it is a server-wide constant and not a property of THIS gem. That
        ///    argument is still correct as far as it goes - the scale genuinely is shared across every gem -
        ///    but the owner's follow-up question showed the real requirement was the opposite of what it
        ///    concluded: a player reading "Experience: x1.16" cannot get from that number to what a kill
        ///    actually pays (baseXp * RoleRate * xpMultiplier * xpScale), because xpScale is not written down
        ///    anywhere the player can see. Showing the total answers that question directly, and keeping the
        ///    gem's own product as a parenthetical still lets a player compare two gems' modifiers against
        ///    each other without doing the division themselves.
        ///
        ///    Boss Experience is deliberately NOT given the same (product, scale) treatment as Experience/
        ///    Luminance, and printing it with a different base is the obvious thing for a later reader to
        ///    "fix" into consistency - do not. It is a factor XpForKill applies ON TOP of the (already-scaled)
        ///    Experience line for the boss kill alone (DungeonRewardMath.cs:133,
        ///    `xp = baseXp * RoleRate(role) * xpMultiplier * boss * scale` - xpMultiplier already carries
        ///    xpScale via the total computed here, and boss (BossXpMultiplier) multiplies on top of that
        ///    result, not on top of a second scale of its own). Folding xpScale into the Boss Experience line
        ///    too would double-count it: the boss kill would then read as paying scale^2 when it only pays
        ///    scale once. Loot Quantity has no xpScale/lumScale-style dial of its own (LootQuantityMultiplier
        ///    feeds DungeonPopulationBuilder with no analogous *Scale field), but it IS one of the axes the
        ///    gem-level reward-scale ratio (2026-09-07) scales multiplicatively - see AddRewardLines - so its
        ///    line prints the single already-scaled total with no separate parenthetical, never a bare
        ///    (product, scale) pair the way Experience/Luminance get one.
        ///
        /// 4. Emitted whenever the relevant value differs from 1.00 - for Boss Experience that is still the
        ///    gem's own capped product, unchanged from before; for Experience, Luminance and Loot Quantity it
        ///    is now the TOTAL (point 3 above and AddRewardLines), so with the shipped 2.0 default scale these
        ///    lines print on effectively every gem and fragment, including the ten shipped rung fragments whose
        ///    specs carry mods= empty (Content/sql/weenies/1003615..1003624) - their stored LongDesc rows were
        ///    regenerated alongside this change (ruling P2-R20) specifically because this rule now fires on
        ///    them where it did not before.
        ///
        ///    ONE CAVEAT, and it is a property of DungeonRewardMath rather than of this block. XpMultiplier
        ///    and LumMultiplier end in Math.Min(product, cap) with NO lower floor (DungeonRewardMath.cs:92),
        ///    so a cap tunable set BELOW 1.0 drags even an empty modifier set under 1.0 and this block then
        ///    prints a line on a gem that carries no reward modifier at all. That text is not a lie - the run
        ///    really would pay the reduced rate, since ThreadDungeonSpawner feeds the same live cap into the
        ///    population math - but it does defeat the "only this gem's modifiers" reading, and it would
        ///    reach the shipped rung fragments too. LootQuantityMultiplier does not share the behaviour: it
        ///    clamps to [1.0, max(1.0, cap)] (DungeonRewardMath.cs:281). The asymmetry predates this change
        ///    and is deliberately NOT resolved here, because flooring Product would alter what every run
        ///    actually PAYS, which is an economy decision rather than a description one. See
        ///    DungeonGemRewardLinesTests.A_cap_below_one_is_shown_because_the_run_really_pays_it.
        /// </summary>
        /// <param name="xpCap">
        /// dynamic_dungeons_xp_mult_cap. Passed in rather than read, because the pure core must stay callable
        /// with no live PropertyManager - a read throws under the unit-test harness. Defaulted to the compiled
        /// value the tunable itself is registered with, so an omitted argument is the shipped behaviour.
        /// </param>
        /// <param name="lumCap">dynamic_dungeons_lum_mult_cap; same contract as <paramref name="xpCap"/>.</param>
        /// <param name="lootQuantityCap">dynamic_dungeons_loot_quantity_cap; same contract as <paramref name="xpCap"/>.</param>
        /// <param name="componentLookup">
        /// Resolves a loaded component's wcid to its ComponentDef so "Possible effects:" (see below) can read
        /// its Ops; optional (defaults to null, which suppresses that block entirely) so every pre-existing
        /// caller of this six-argument overload keeps compiling unchanged.
        /// </param>
        /// <param name="xpScale">
        /// dynamic_dungeons_xp_scale, folded into the Experience line's TOTAL (point 3 above). Same
        /// pass-in-not-read contract as <paramref name="xpCap"/>, defaulted to the compiled value the tunable
        /// itself is registered with (DungeonPopulationLimits.DefaultXpScale) so there is no default drift
        /// between this signature and PropertyManager's registration.
        ///
        /// UNLIKE the three caps above, this pure core does NOT sanitize the value it is handed - it trusts
        /// the caller. Passing NaN suppresses the line (guarded explicitly in AddScaledMultiplierLine, the
        /// same way AddMultiplierLine already guarded the caps); anything else, including negative, zero, or
        /// a value past DungeonPopulationLimits.MaxRewardScale, is rendered exactly as given, with no clamp
        /// and no floor. The one caller this matters for is the store-bound overload below, which is
        /// responsible for handing this parameter an already-sanitized value (via
        /// ThreadDungeonSpawner.SanitizeDoubleDial, the SAME function the spawner itself runs the tunable
        /// through) rather than the raw PropertyManager read - a bug fixed in review, 2026-09-07, after a
        /// version of this overload read the tunable raw and could print a scale on the item that the run
        /// itself would never actually pay. See DungeonGemRewardLinesTests.
        /// An_out_of_range_scale_passed_to_the_pure_core_is_rendered_as_given for the test that pins this
        /// choice: the pure core is the wrong place to duplicate SanitizeDoubleDial's clamp, because a second
        /// copy of that clamp is exactly how the two sides drifted apart the first time.
        /// </param>
        /// <param name="lumScale">dynamic_dungeons_lum_scale; same contract as <paramref name="xpScale"/>, folded into the Luminance line.</param>
        /// <param name="rewardScaleRatio">
        /// The gem-level reward-scale ratio (owner requirement, 2026-09-07) for THIS gem's spec.Level, from
        /// <see cref="DungeonRewardMath.RewardScaleRatio"/> - the same value <see cref="DungeonPopulationBuilder.Build"/>
        /// computes to scale a run's actual rewards. Defaulted to 1.0, the ratio's own neutral no-op value, so
        /// every pre-existing caller of this overload (including every test written before this parameter
        /// existed) keeps composing the exact text it always did.
        ///
        /// Applied the SAME way <see cref="DungeonPopulationBuilder.Build"/> applies it to plan.XpScale/
        /// LumScale - multiplicatively, via <see cref="DungeonRewardMath.ApplyMultiplicativeScale"/>, to
        /// <paramref name="xpScale"/>/<paramref name="lumScale"/> and to the loot-quantity product - BEFORE
        /// those effective values reach <see cref="AddScaledMultiplierLine"/>/<see cref="AddMultiplierLine"/>,
        /// so the printed Experience/Luminance/Loot Quantity totals are the numbers a run at this gem's level
        /// actually pays, not the flat server-wide rate. A single derived ratio, not the four underlying curve
        /// dials, is threaded through here and into <see cref="AddRewardLines"/> - the pure core does not need
        /// to know anchor/exponent/floor/cap individually, only the one number they resolve to for this level,
        /// and passing five parameters end to end for one derived value would be needless duplication of the
        /// resolution ThreadDungeonSpawner.ResolveRewardScaleRatio already owns.
        ///
        /// UNLIKE xpScale/lumScale, there is no separate sanitizing pass for this parameter: RewardScaleRatio
        /// re-sanitizes its own four inputs (NaN, Infinity, a non-positive anchor, etc. all fall back to the
        /// compiled default), so any finite, non-negative value reaching this parameter is already safe to
        /// multiply straight in.
        /// </param>
        public static string ComposeLongDesc(DungeonGemSpec spec, string dungeonName, int entries, int maxEntries,
            Func<string, ModifierDef> lookup, Func<uint, string> componentName,
            double xpCap = DungeonPopulationLimits.DefaultXpCap,
            double lumCap = DungeonPopulationLimits.DefaultLumCap,
            double lootQuantityCap = DungeonPopulationLimits.DefaultLootQuantityCap,
            Func<uint, ComponentDef> componentLookup = null,
            double xpScale = DungeonPopulationLimits.DefaultXpScale,
            double lumScale = DungeonPopulationLimits.DefaultLumScale,
            double rewardScaleRatio = 1.0)
        {
            var lines = new System.Collections.Generic.List<string>();

            var isFragment = spec.Seed == 0;

            if (isFragment)
                lines.Add(FragmentFlavourLine);

            var dungeonLabel = (spec.DungeonId == DungeonGemSpec.Any && !spec.IsBound) ? "Any" : dungeonName;
            lines.Add($"Dungeon: {dungeonLabel}");
            lines.Add($"Level: {spec.Level.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"Loot Tier: {spec.Tier.ToString(CultureInfo.InvariantCulture)}");

            if (spec.Modifiers.Count == 0)
            {
                lines.Add("Modifiers: none");
            }
            else
            {
                lines.Add("Modifiers:");
                foreach (var (id, magnitude) in spec.Modifiers)
                {
                    var def = lookup?.Invoke(id);
                    var display = def?.Display ?? id;
                    var locked = spec.Locks.Contains(id) ? " (locked)" : string.Empty;
                    lines.Add($"{display}: {RenderEffect(def, magnitude)}{locked}");
                }
            }

            if (isFragment)
            {
                lines.Add($"Pressed: {RawFragmentRules.ComposePressedLine(spec.Load, componentName)}");
                AddPossibleEffectLines(lines, spec.Load, componentLookup, lookup);
            }

            AddRewardLines(lines, spec, lookup, xpCap, lumCap, lootQuantityCap, xpScale, lumScale, rewardScaleRatio);

            // The "Stability: {word}" line that used to sit here went with the instability mechanic (owner
            // ruling, 2026-09-07). Losing it changes the composed LongDesc, so the ten shipped rung fragment
            // weenies (Content/sql/weenies/1003615..1003624) must be regenerated from this composer to keep
            // ruling P2-R20 - see DungeonGemLongDescTests.Fragment_shows_Pressed_nothing_and_matches_the_shipped_rung_row,
            // which diffs the 1003615 row against this output byte for byte.
            lines.Add($"Entries: {entries.ToString(CultureInfo.InvariantCulture)} of {maxEntries.ToString(CultureInfo.InvariantCulture)}");

            if (isFragment)
                lines.Add("Unpressed. Load spell components onto it, then press it at the Fragment Press.");
            else
                lines.Add(spec.IsBound ? $"Bound to {dungeonName}. It crumbles when the dungeon is cleared or its last entry is spent; a death after the clear leaves no way back in." : "Unbound. Use it to open a dungeon.");

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Appends "Possible effects:" plus one COUNT PER CATEGORY of the modifiers a currently-loaded
        /// fragment could still gain - "1 monster modifier", "2 boss modifiers" - fragments only (a pressed
        /// gem already lists its REAL modifiers via the block above and must never gain a second, speculative
        /// one).
        ///
        /// VAGUE BY RULING (owner, 2026-09-07). This block shipped in PR #969 naming each reachable modifier
        /// and its magnitude range; the owner has since reversed that, so a fragment now tells the player only
        /// HOW MANY modifiers of WHICH CATEGORY are in reach, never which ones or how large. The range
        /// formatter that wording went through lost its last caller here and was deleted with it (see the
        /// tombstone note beside RenderEffect). The category comes from <see cref="DungeonModifierCategories"/>, the
        /// same classifier DungeonGemNarrator's chat load line uses, so the item description and a chat message
        /// about the same fragment still cannot disagree - that property is preserved, it is only the shared
        /// vocabulary that changed from a range wording to a category word.
        ///
        /// ONE EXCEPTION, ONE ENTRY: A SALVAGE AFFINITY IS NAMED, NOT COUNTED (owner ruling, 2026-09-07).
        /// A loaded powder contributes a line carrying the affinity's Display verbatim - "Obsidian Affinity" -
        /// or "A random salvage affinity" for a wildcard powder that has not drawn one yet.
        /// <see cref="DungeonModifierCategories.IsNameable"/> is the single place that exception is decided
        /// and carries the reasoning (in short: the powder to material mapping is arbitrary by ruling and the
        /// game teaches it nowhere, so a vague powder is not a choice at all, and a material reveals nothing
        /// about the run's difficulty). It is NAMED INSTEAD OF COUNTED, never as well: an affinity that also
        /// landed in the bonus tally would be advertised twice, once as itself and once as "1 bonus
        /// modifier". Everything else is unchanged and stays as vague as it was, and no magnitude or range is
        /// printed for the affinity either - the exception is about the name alone.
        ///
        /// AT MOST ONE affinity entry is expected today, and this block is DEFENDED TWICE against that
        /// changing, deliberately (review, 2026-09-07):
        ///
        ///   (a) The design invariant, in the loader. ThreadDungeonStore's lint rule 23 refuses an op that
        ///       reaches a salvage affinity on any component that is not a powder, and lint rule 22 pins the
        ///       powder limit to the board's single powder slot, so CanLoad refuses a second powder of ANY
        ///       wcid with OverLimit (RawFragmentRulesTests.A_second_powder_of_a_different_wcid_is_refused,
        ///       against the shipped file). That is what makes one entry the real ceiling.
        ///   (b) This renderer does NOT depend on it. It keeps a LIST and prints every entry.
        ///
        /// The reason both exist: the two conditions in (a) are not the same condition. CanLoad's refusal is
        /// per component TYPE, while DungeonModifierCategories.IsNameable keys on the modifier's effect KIND,
        /// so before rule 23 a taper row pointing at affinity_iron would have loaded beside a powder and the
        /// second affinity would have vanished from this block with no diagnostic and no failing test. Rule
        /// 23 closes that today; the list is what keeps the failure loud if anyone ever has a good reason to
        /// relax it.
        ///
        /// AN OP COUNTS only when it can introduce a modifier onto a gem that does not yet have it. Three
        /// qualify, and <see cref="TryAddableEntry"/> is the one place that judgement is made:
        ///   - add_or_raise and set_max, which name their modifier outright;
        ///   - add_random, which names NO modifier and takes its category from the op's scope instead. It was
        ///     missed when press v2 introduced it (caught in review, 2026-09-07), so a fragment carrying only
        ///     a Turquoise Taper printed no block at all - while adding a modifier is precisely what that
        ///     component does, and previewing that capability is what this block exists for.
        /// lock_one also names a modifier (its op.Modifier), but never adds one - it only locks a modifier
        /// already present - so it is deliberately excluded even though it shares OpDef.Modifier with the two
        /// ops that do add. Ops with no modifier argument and no adding power (raise_random, raise_all,
        /// remove_one, reroll_one, sharpen, reroll_weakest, set_difficulty, "nothing") contribute nothing.
        ///
        /// TWO DEDUP RULES, and BOTH are load-bearing. An op is counted only when it passes both:
        ///
        /// 1. ACROSS components, by modifier id: an id is counted ONCE however many loaded components reach
        ///    it, because only one copy of it can ever exist on the gem. Two components that both reach
        ///    "savage" contribute a single monster modifier. This rule predates press v2 and is unchanged.
        ///
        /// 2. WITHIN one component, by category: a component contributes at most ONE count per category. This
        ///    rule is new (review, 2026-09-07) and it follows from the slot board rather than from taste - a
        ///    component occupies exactly one slot and a slot draws exactly one op, so one component can add at
        ///    most one modifier however many alternatives its op list offers. Without it Powdered Quartz,
        ///    whose three ops are mutually exclusive alternatives for a single draw, advertised "3 bonus
        ///    modifiers" on a slot whose budget is 1. A component whose ops genuinely STRADDLE categories
        ///    still contributes more than one, because those are different budgets.
        ///
        /// ORDER is unchanged: the entries come out in the order their first qualifying op appears in load
        /// order, and the affinity entry takes its place in that same sequence rather than being pinned to
        /// the end - a powder loaded first is listed first.
        ///
        /// A modifier id the lookup cannot resolve is counted as "monster", per
        /// DungeonModifierCategories.Of's null contract - the id still names something a press could add, so
        /// dropping it would UNDERCOUNT, and monster is the least over-promising label available.
        ///
        /// Emits nothing at all - no header - when the load is empty or no loaded component's ops reach any
        /// modifier, so an empty-load fragment's description carries no trace of this block (this is also what
        /// keeps the ten shipped rung fragments' stored LongDesc free of it: every one of them ships with an
        /// empty load - see DungeonGemLongDescTests.ShippedRungLongDesc and its P2-R20 comment).
        ///
        /// PRESS v2 DOES NOT PREVIEW THE EMPTY SLOTS, and that is deliberate. Under the slot board (owner
        /// ruling, 2026-09-07) an empty slot resolves a randomly DRAWN component at press time, so a bare
        /// fragment presses into a full gem. Counting those slots here would mean previewing a component that
        /// has not been chosen and will not be chosen until the press rolls - the count would be right and
        /// every category in it would be a guess. This block therefore still counts only what the player
        /// LOADED, which is the only part of the outcome the fragment actually knows.
        ///
        /// Prints NO reward number of any kind, which the vague ruling makes trivially true but which was
        /// already the rule for its own reason, worth keeping recorded: DungeonRewardMath's XP/Lum/LootQuantity
        /// multipliers are PRODUCTS over whichever modifiers a press actually rolls, and this block counts
        /// modifiers that MIGHT be rolled - a combined figure over "might" modifiers would be a number no real
        /// press pays.
        /// </summary>
        private static void AddPossibleEffectLines(System.Collections.Generic.List<string> lines,
            System.Collections.Generic.IReadOnlyList<(uint Wcid, int Doses)> load, Func<uint, ComponentDef> componentLookup, Func<string, ModifierDef> lookup)
        {
            if (load == null || load.Count == 0 || componentLookup == null)
                return;

            // Dedup rule 1: a modifier id is counted once across the whole load.
            var seenModifiers = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            // Dedup rule 2: a component contributes at most one count per category.
            var seenComponentCategories = new System.Collections.Generic.HashSet<(uint Wcid, DungeonModifierCategory Category)>();

            // Insertion-ordered: the category list preserves first-appearance order in load order, and the
            // dictionary carries the running count. A plain Dictionary's enumeration order is not contractual,
            // so the order is held in its own list rather than read back out of the dictionary.
            var order = new System.Collections.Generic.List<DungeonModifierCategory>();
            var counts = new System.Collections.Generic.Dictionary<DungeonModifierCategory, int>();

            // The named-affinity entries, each with how many category entries had already appeared when it was
            // first seen - which is all that is needed to slot them back into first-appearance order at
            // render time.
            //
            // A LIST, EVEN THOUGH AT MOST ONE IS EXPECTED. That is belt and braces on purpose (review,
            // 2026-09-07). Lint rule 23 is the design invariant - only a powder reaches an affinity, and the
            // board has one powder slot - and it is why this list holds one entry today. This list is what
            // keeps the failure LOUD if that rule is ever deliberately relaxed: the previous version held a
            // single field and dropped a second entry silently, so a taper row pointing at an affinity would
            // have vanished from the panel with no diagnostic and no failing test. The two halves defend
            // different things and neither replaces the other.
            var affinityEntries = new System.Collections.Generic.List<(string Text, int After)>();

            foreach (var (wcid, _) in load)
            {
                var component = componentLookup(wcid);
                if (component?.Ops == null)
                    continue;

                foreach (var op in component.Ops)
                {
                    if (!TryAddableEntry(op, lookup, out var category, out var modifierId, out var affinityName))
                        continue;

                    // Rule 1 is tested BEFORE rule 2 registers anything, so a component whose first op names
                    // an already-counted modifier can still contribute through a LATER op that names a new
                    // one. add_random names no modifier and so has no rule-1 key; rule 2 alone bounds it,
                    // which is correct because it too adds at most one modifier.
                    if (modifierId != null && !seenModifiers.Add(modifierId))
                        continue;

                    if (!seenComponentCategories.Add((wcid, category)))
                        continue;

                    // NAMED INSTEAD OF COUNTED. Both dedup rules still ran, so a component whose several ops
                    // all reach affinities contributes one entry exactly as it would have contributed one
                    // count - which is what keeps a multi-op powder to a single line.
                    if (affinityName != null)
                    {
                        affinityEntries.Add((affinityName, order.Count));
                        continue;
                    }

                    if (counts.TryGetValue(category, out var count))
                    {
                        counts[category] = count + 1;
                    }
                    else
                    {
                        counts[category] = 1;
                        order.Add(category);
                    }
                }
            }

            if (order.Count == 0 && affinityEntries.Count == 0)
                return;

            lines.Add("Possible effects:");

            var nextAffinity = 0;

            for (var i = 0; i < order.Count; i++)
            {
                // Every affinity first seen before this category entry, in the order it was seen. The list is
                // already ordered by After, because it is appended to in load order and order.Count never
                // decreases.
                while (nextAffinity < affinityEntries.Count && affinityEntries[nextAffinity].After <= i)
                    lines.Add(affinityEntries[nextAffinity++].Text);

                var category = order[i];
                var count = counts[category];
                var noun = count == 1 ? "modifier" : "modifiers";

                lines.Add($"{count.ToString(CultureInfo.InvariantCulture)} {DungeonModifierCategories.Word(category)} {noun}");
            }

            // Whatever appeared after the last category entry, or when there were no category entries at all.
            while (nextAffinity < affinityEntries.Count)
                lines.Add(affinityEntries[nextAffinity++].Text);
        }

        /// <summary>
        /// Whether <paramref name="op"/> can ADD a modifier to a gem that does not have it, and if so how the
        /// preview should present it. The ONE place that judgement is made, so adding a new adding op means
        /// editing one method rather than hunting for every filter that enumerates op names.
        ///
        /// <paramref name="modifierId"/> comes back non-null only when the op NAMES its modifier statically;
        /// add_random does not, which is why the caller's cross-component dedup key is nullable.
        ///
        /// <paramref name="affinityName"/> non-null means the entry is NAMED rather than counted (the powder
        /// exception - see the block comment on <see cref="AddPossibleEffectLines"/>). The caller must not
        /// then also count <paramref name="category"/>. It is still set on that path, because both dedup
        /// rules key on it and a powder must not be able to slip a second entry past the per-component rule
        /// by being categorised differently from itself.
        /// </summary>
        private static bool TryAddableEntry(OpDef op, Func<string, ModifierDef> lookup,
            out DungeonModifierCategory category, out string modifierId, out string affinityName)
        {
            category = DungeonModifierCategory.Monster;
            modifierId = null;
            affinityName = null;

            if (op == null)
                return false;

            if (op.Op == AttunementOps.AddOrRaise || op.Op == AttunementOps.SetMax)
            {
                if (string.IsNullOrEmpty(op.Modifier))
                    return false;

                var def = lookup?.Invoke(op.Modifier);

                modifierId = op.Modifier;
                category = DungeonModifierCategories.Of(def);

                // Display verbatim, no article, for the reason recorded on DungeonGemNarrator.CategoryObject:
                // an article would have to be chosen per material from content the code does not control, and
                // this is how the pressed gem's own panel already prints the same modifier. An affinity whose
                // row carries no display falls back to its id rather than printing an empty line.
                if (DungeonModifierCategories.IsNameable(def))
                    affinityName = string.IsNullOrEmpty(def.Display) ? op.Modifier : def.Display;

                return true;
            }

            if (op.Op == AttunementOps.AddRandom)
            {
                category = ScopeCategory(op.Scope);

                // The wildcard powder. It has drawn nothing yet, so it names nothing - but it is still an
                // affinity entry rather than a bonus count, so the two powder shapes read as one slot.
                if (op.Scope == AttunementScopes.Salvage)
                    affinityName = "A random salvage affinity";

                return true;
            }

            return false;
        }

        /// <summary>
        /// The category to advertise for an op that draws from a SCOPE rather than naming a modifier.
        ///
        /// <see cref="AttunementScopes.Boss"/> and <see cref="AttunementScopes.Salvage"/> each narrow to one
        /// category outright - salvage to BONUS, which is what DungeonModifierCategories calls a salvage
        /// affinity (it is a reward to the player, not a difficulty knob), so the wildcard powders preview
        /// the same word a mapped powder does.
        ///
        /// "difficulty" deliberately reports MONSTER even though it spans monster and boss: it adds exactly
        /// one modifier and which category that lands in is not knowable before the press, so this follows
        /// DungeonModifierCategories.Of's own null contract - monster is the majority case and the least
        /// over-promising of the three. Advertising both would promise two modifiers where one arrives, and
        /// advertising "boss" would promise the rarer outcome.
        /// </summary>
        private static DungeonModifierCategory ScopeCategory(string scope)
        {
            if (scope == AttunementScopes.Boss) return DungeonModifierCategory.Boss;
            if (scope == AttunementScopes.Salvage) return DungeonModifierCategory.Bonus;
            return DungeonModifierCategory.Monster;
        }

        /// <summary>
        /// Appends the gem's reward-summary lines - Experience, Boss Experience, Luminance, Loot Quantity.
        /// See the reward block on <see cref="ComposeLongDesc"/> for why these are summaries rather than
        /// per-modifier suffixes, why XP takes two lines while luminance and loot quantity take one, why
        /// RoleRate is excluded from all of them, and why Experience/Luminance fold in the global reward
        /// scale (via <see cref="AddScaledMultiplierLine"/>) while Boss Experience does not (via the
        /// un-scaled <see cref="AddMultiplierLine"/>).
        ///
        /// DungeonRewardMath wants an IReadOnlyDictionary while the pure core is handed a lookup delegate, so
        /// the spec's own modifiers are materialised into one here. Only ids the lookup RESOLVES go in: an id
        /// the store does not know contributes no factor to the run's real reward either
        /// (DungeonRewardMath.Product skips a TryGetValue miss), so dropping it keeps the printed figure and
        /// the paid figure derived from exactly the same set.
        ///
        /// <paramref name="rewardScaleRatio"/> (the LongDesc gap closed 2026-09-07) is applied to xpScale,
        /// lumScale AND the loot-quantity product before they reach the lines below - all three are
        /// MULTIPLICATIVE axes under DungeonPopulationBuilder.Build (neutral value 1.0), so all three use
        /// DungeonRewardMath.ApplyMultiplicativeScale the same way Build does for plan.XpScale/LumScale and
        /// the profile's loot-quantity multiplier. Boss Experience is deliberately excluded - BossXpMultiplier
        /// is the gem's own rolled modifier product, never a "uniform server-wide scalar", and
        /// DungeonPopulationBuilder.Build never scales it either (see DungeonPopulationBuilder.cs's reward
        /// block). Loot Quality and salvage-affinity chance are the other two axes Build scales, but neither
        /// is ever printed as a number on the LongDesc (loot quality feeds TreasureDeath.LootQualityMod
        /// silently; a salvage affinity is named, never quantified - see AddPossibleEffectLines), so there is
        /// no third line to fix for those two.
        /// </summary>
        private static void AddRewardLines(System.Collections.Generic.List<string> lines, DungeonGemSpec spec,
            Func<string, ModifierDef> lookup, double xpCap, double lumCap, double lootQuantityCap, double xpScale, double lumScale,
            double rewardScaleRatio)
        {
            var defs = new System.Collections.Generic.Dictionary<string, ModifierDef>();

            foreach (var (id, _) in spec.Modifiers)
            {
                var def = lookup?.Invoke(id);

                if (def != null)
                    defs[id] = def;
            }

            var effectiveXpScale = DungeonRewardMath.ApplyMultiplicativeScale(rewardScaleRatio, xpScale);
            var effectiveLumScale = DungeonRewardMath.ApplyMultiplicativeScale(rewardScaleRatio, lumScale);

            AddScaledMultiplierLine(lines, "Experience", DungeonRewardMath.XpMultiplier(spec, defs, xpCap), effectiveXpScale);

            // Immediately after Experience, and a SEPARATE line rather than a combined one, because the two
            // factors apply to different kills - see the XP-is-two-axes paragraph on ComposeLongDesc. It uses
            // the same xpCap: BossXpMultiplier is capped by dynamic_dungeons_xp_mult_cap too
            // (DungeonPopulationBuilder.cs:304 passes limits.XpCap to both). Deliberately the UN-scaled
            // AddMultiplierLine, not AddScaledMultiplierLine: xpScale is already folded into the Experience
            // line above, and XpForKill multiplies BossXpMultiplier on top of that same scaled result for the
            // boss kill alone, so scaling this line too would double the scale on every boss kill's total.
            // The reward-scale ratio does not apply here either, for the same reason - see the summary above.
            AddMultiplierLine(lines, "Boss Experience", DungeonRewardMath.BossXpMultiplier(spec, defs, xpCap));

            AddScaledMultiplierLine(lines, "Luminance", DungeonRewardMath.LumMultiplier(spec, defs, lumCap), effectiveLumScale);

            var effectiveLootQuantityMultiplier = DungeonRewardMath.ApplyMultiplicativeScale(rewardScaleRatio,
                DungeonRewardMath.LootQuantityMultiplier(spec, defs, lootQuantityCap));
            AddMultiplierLine(lines, "Loot Quantity", effectiveLootQuantityMultiplier);
        }

        /// <summary>
        /// One reward line, or nothing at all when the axis is untouched. The 1.0 test is taken AFTER rounding
        /// to two decimals rather than before it, so a product that is only trivially off 1.0 (1.0000001 out
        /// of a chain of floating-point factors) cannot print the useless line "Experience: x1.00". Culture is
        /// pinned to invariant like every other number this description carries - a comma decimal separator
        /// would be stamped into an item's persisted text on a machine with a European locale.
        ///
        /// Used for Boss Experience and Loot Quantity. Both show a single already-final number rather than a
        /// (product, scale) pair, so there is no parenthetical breakdown here the way
        /// <see cref="AddScaledMultiplierLine"/> gives Experience/Luminance - Boss Experience because it truly
        /// has no global scale of its own (see AddRewardLines), Loot Quantity because its caller folds the
        /// gem-level reward-scale ratio directly into the number handed in here (via
        /// DungeonRewardMath.ApplyMultiplicativeScale) rather than passing the ratio through as a second
        /// argument - unlike Experience/Luminance's raw xpScale/lumScale, there is no "gem's own product" for
        /// Loot Quantity that would be worth showing alongside the total.
        /// </summary>
        private static void AddMultiplierLine(System.Collections.Generic.List<string> lines, string label, double multiplier)
        {
            if (double.IsNaN(multiplier))
                return;

            var text = multiplier.ToString("0.00", CultureInfo.InvariantCulture);

            if (text == "1.00")
                return;

            lines.Add($"{label}: x{text}");
        }

        /// <summary>
        /// Experience/Luminance's line: the TOTAL a kill actually pays on this axis - the gem's own capped
        /// modifier product times the axis's global scale (dynamic_dungeons_xp_scale / _lum_scale) - with the
        /// gem's own product appended as a parenthetical. See point 3 on <see cref="ComposeLongDesc"/> for
        /// why the total, not the bare product, is what these two lines show. Suppressed (like
        /// <see cref="AddMultiplierLine"/>) only when the TOTAL is 1.00, which with the shipped 2.0 default
        /// scale means these two lines print on effectively every gem.
        ///
        /// The parenthetical is shown only when it would say something the total does not already say:
        /// product text equal to "1.00" (no modifiers moved this axis - the "x2.00" alone case) or equal to
        /// the total's own text (scale == 1.0, where product and total are the same number and a
        /// "x1.16 (x1.16 from this gem)" line would just repeat itself) both suppress it, collapsing the line
        /// to the bare product exactly as it read before this axis gained a scale. Both comparisons are made
        /// on the ROUNDED two-decimal text, matching AddMultiplierLine's own rounding-then-compare order, so a
        /// product and total that round to the same display text (but differ in the 3rd decimal) still
        /// collapse rather than printing a parenthetical that looks identical to the total it qualifies.
        ///
        /// TRUSTS ITS CALLER on `scale`: only NaN is guarded here (the same bar AddMultiplierLine already
        /// held for the caps). A negative, zero, or absurdly large scale is rendered exactly as given - this
        /// method does not clamp it and must not, because ThreadDungeonSpawner.SanitizeDoubleDial already
        /// owns that clamp and a second copy of it here is exactly how the item's printed number and the
        /// run's actual payout drifted apart in the first place (see ComposeLongDesc's xpScale doc for the
        /// bug this refers to). It is ComposeLongDesc's store-bound overload's job to hand this method an
        /// already-sanitized scale; a test caller of the pure core is free to pass whatever it wants and see
        /// it rendered unclamped, which is the point of the test in
        /// DungeonGemRewardLinesTests.An_out_of_range_scale_passed_to_the_pure_core_is_rendered_as_given.
        /// </summary>
        private static void AddScaledMultiplierLine(System.Collections.Generic.List<string> lines, string label, double product, double scale)
        {
            if (double.IsNaN(product) || double.IsNaN(scale))
                return;

            var totalText = (product * scale).ToString("0.00", CultureInfo.InvariantCulture);

            if (totalText == "1.00")
                return;

            var productText = product.ToString("0.00", CultureInfo.InvariantCulture);
            var parenthetical = (productText == "1.00" || productText == totalText) ? string.Empty : $" (x{productText} from this gem)";

            lines.Add($"{label}: x{totalText}{parenthetical}");
        }

        /// <summary>
        /// The ONE place MonsterEffectKind-to-wording knowledge lives. Two switches on the same enum drift
        /// apart silently: the reward block above this method exists precisely because an earlier, separate
        /// switch reported only half of a boss modifier's XP effect. Adding a monsterEffectKind means adding
        /// ONE case here, not two.
        ///
        /// TWO CALLERS, and they are deliberately the same wording rather than two similar ones.
        /// <see cref="RenderEffect"/> renders a pressed gem's "Modifiers:" list on the item panel, and it is
        /// also what DungeonGemNarrator.ComposeSummary prints into the pressing player's chat - which is why
        /// RenderEffect is public rather than private. The chat summary used to print a bare display name and
        /// a bare number ("Bounteous 1.27"), which said nothing about what the modifier does; it was routed
        /// through here (owner ruling, 2026-09-07) instead of growing a wording map of its own.
        ///
        /// It had a THIRD caller until 2026-09-07 - a min/max range formatter behind the load-time wording -
        /// which the vague-feedback ruling removed along with every load-time surface that named a modifier. So
        /// this method is reached only AFTER a press, which is also the only time a magnitude exists. The
        /// prefix/formatValue/suffix split is kept rather than collapsed into one string: it is what a further
        /// caller would need, and it is what keeps the unit mark ("+", "x" or "%") attached to the number
        /// by formatValue rather than baked into the surrounding English.
        ///
        /// Returns false for a kind this method does not (or cannot yet) describe - an unmapped "none" with no
        /// reward slope, or any truly unrecognised string - so the caller can choose its own honest fallback
        /// (a raw number, in both callers). EVERY SHIPPED MODIFIER MUST RETURN TRUE HERE: that fallback exists
        /// for a modifier row a future build adds and this switch has not caught up with, never for a row in
        /// Content/dungeons/dynamic/modifiers.json, and DungeonGemSummaryEffectTests enumerates the shipped
        /// file to keep it that way.
        /// </summary>
        private static bool TryDescribeEffect(ModifierDef def, out string prefix, out Func<double, string> formatValue, out string suffix)
        {
            var kind = def?.MonsterEffectKind ?? "none";
            var isBoss = def?.Target == "boss";

            switch (kind)
            {
                case DungeonRewardMath.HealthMult:
                    prefix = isBoss ? "the boss has " : "monsters have ";
                    formatValue = Multiplier;
                    suffix = " health";
                    return true;

                case DungeonRewardMath.DamageRating:
                    prefix = isBoss ? "the boss attacks with " : "monsters attack with ";
                    formatValue = Rating;
                    suffix = " damage rating, hitting harder";
                    return true;

                case DungeonRewardMath.CritRating:
                    // No boss variant here, unlike DamageRating/CritDamageRating/HealthMult - that asymmetry
                    // is pre-existing (no shipped modifier pairs crit_rating with target "boss") and is
                    // preserved exactly rather than "fixed" as a drive-by.
                    prefix = "monsters have ";
                    formatValue = Rating;
                    suffix = " critical strike rating, landing more critical hits";
                    return true;

                case DungeonRewardMath.CritDamageRating:
                    // The trailing clause agrees in NUMBER with the subject the prefix chose. Sharing one
                    // suffix string across both targets is what produced "monsters have ... so ITS critical
                    // hits hurt more" in review; a boss variant is a different sentence, not the same one
                    // with a different noun.
                    prefix = isBoss ? "the boss has " : "monsters have ";
                    formatValue = Rating;
                    suffix = isBoss
                        ? " critical damage rating, so its critical hits hurt more"
                        : " critical damage rating, so their critical hits hurt more";
                    return true;

                case DungeonRewardMath.DamageResistRating:
                    // No boss variant here either - same pre-existing asymmetry as CritRating.
                    prefix = "monsters have ";
                    formatValue = Rating;
                    suffix = " damage resist rating, taking less damage from you";
                    return true;

                case DungeonRewardMath.CountMult:
                    // UNCHANGED by the 2026-09-07 detail pass, and named there as the model the other cases
                    // were reworded to follow: a number, then the consequence in plain words.
                    prefix = string.Empty;
                    formatValue = PercentAbove;
                    suffix = " more monsters";
                    return true;

                case DungeonRewardMath.EliteShare:
                    prefix = string.Empty;
                    formatValue = PercentOfWhole;
                    suffix = " of the run's monsters are elites";
                    return true;

                case DungeonRewardMath.RunSpeedMult:
                    // No boss variant here either - same pre-existing asymmetry as CritRating.
                    prefix = "monsters move at ";
                    formatValue = Multiplier;
                    suffix = " speed";
                    return true;

                case DungeonRewardMath.SalvageAffinity:
                {
                    // The magnitude IS the per-kill percent chance directly (DungeonRewardMath.SalvageAffinities
                    // divides by 100 for the roll but never rescales it), so this is the one kind whose
                    // formatValue needs no transform at all beyond rounding and a "%" mark.
                    //
                    // THE MATERIAL IS NAMED, reversing an owner correction of 2026-09-07 which had argued the
                    // Display already carries it ("Tourmaline Affinity") so the value text need not repeat it.
                    // That left "17% per kill", which is a percent chance of nothing the sentence states - the
                    // reader has to carry the material across from the line's own label and then guess what
                    // happens to it. The material is read from SalvageMaterial rather than parsed back out of
                    // the Display, so a row whose display text is reworded still describes itself correctly.
                    //
                    // An id that is not a defined MaterialType degrades to the unnamed form rather than
                    // printing the number: nothing shipped can reach that (ThreadDungeonStore's lint drops
                    // such a row with a diagnostic), but ComposeLongDesc is also handed hand-built defs by
                    // tests and by any future caller, and leaking "material 999999" at a player would be worse
                    // than saying less.
                    var material = SalvageMaterialName(def?.SalvageMaterial ?? 0);

                    prefix = string.Empty;
                    formatValue = PercentDirect;
                    suffix = material == null
                        ? " of kills leave extra salvage"
                        : " of kills leave " + material + " to salvage";
                    return true;
                }

                case DungeonRewardMath.IgnoreShield:
                    // The magnitude is the FRACTION of the defender's shield ignored, so it renders as a
                    // percentage of the thing it eats rather than as a rating. Named as "your shield"
                    // because this is the monster-attacks-player direction and the player is the defender.
                    prefix = "monsters ignore ";
                    formatValue = PercentOfWhole;
                    suffix = " of your shield";
                    return true;

                case DungeonRewardMath.Hollow:
                    // BINARY - see DungeonRewardMath.Hollow. There is no partial form and so no number to
                    // print; formatValue is empty rather than printing the row's fixed 1.0, which would read
                    // as a magnitude the player could compare against another gem's. It is therefore the ONE
                    // kind whose rendered line carries no digit, and DungeonGemSummaryEffectTests exempts it
                    // by name for that reason rather than by pattern.
                    prefix = "monsters ignore magic armor and magic resistance";
                    formatValue = _ => string.Empty;
                    suffix = string.Empty;
                    return true;

                case DungeonRewardMath.LootQuantity:
                    prefix = "kills drop ";
                    formatValue = Multiplier;
                    suffix = " loot";
                    return true;

                case "none":
                    // A row that writes no creature knob but moves a reward. Worded as what a KILL pays,
                    // which is the thing the multiplier is actually applied to, and deliberately parallel to
                    // loot_quantity's "kills drop x1.27 loot" - all three reward axes then read alike.
                    if (def != null && def.RewardLumSlope != 0)
                    {
                        prefix = "kills pay ";
                        formatValue = Multiplier;
                        suffix = " luminance";
                        return true;
                    }
                    if (def != null && def.RewardXpSlope != 0)
                    {
                        prefix = "kills pay ";
                        formatValue = Multiplier;
                        suffix = " experience";
                        return true;
                    }
                    prefix = null;
                    formatValue = null;
                    suffix = null;
                    return false;

                default:
                    prefix = null;
                    formatValue = null;
                    suffix = null;
                    return false;
            }
        }

        // The three value shapes, hoisted out of the switch so a case declares WHICH unit its number carries
        // and never how to format it. This is the prefix/formatValue/suffix split doing its job: the unit mark
        // stays welded to the number here, and the surrounding English above stays free of it.

        /// <summary>"x1.27" - a multiplier, two decimal places.</summary>
        private static readonly Func<double, string> Multiplier =
            m => "x" + m.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>"+35" - a rating, a whole signed number.</summary>
        private static readonly Func<double, string> Rating =
            m => "+" + Math.Round(m).ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// "41%" - a magnitude that is a MULTIPLIER, rendered as the percentage it adds (count_mult's 1.41).
        ///
        /// Kept separate from <see cref="PercentOfWhole"/> rather than folded into one formatter that guesses
        /// from whether the magnitude exceeds 1.0. That guess is true of every row shipped today and is a
        /// property of the CONTENT, not of the kind: a fraction row retuned past 1.0, or a multiplier row
        /// authored below it, would silently start rendering the other kind's meaning with no error anywhere.
        /// </summary>
        private static readonly Func<double, string> PercentAbove =
            m => Math.Round((m - 1.0) * 100).ToString(CultureInfo.InvariantCulture) + "%";

        /// <summary>"48%" - a magnitude that is a FRACTION of a whole (elite_share, ignore_shield).</summary>
        private static readonly Func<double, string> PercentOfWhole =
            m => Math.Round(m * 100).ToString(CultureInfo.InvariantCulture) + "%";

        /// <summary>"23%" - a magnitude that ALREADY IS a percentage (the salvage affinities).</summary>
        private static readonly Func<double, string> PercentDirect =
            m => Math.Round(m).ToString(CultureInfo.InvariantCulture) + "%";

        /// <summary>
        /// A salvage material's name as a player reads it - "serpentine", "green garnet" - from the
        /// MaterialType id on the modifier row, or NULL when that id is not a defined material.
        ///
        /// Lower case and space-separated, because every use site is mid-clause. The camel-case split is what
        /// turns GreenGarnet into two words; without it the one shipped two-word material would print as
        /// "greengarnet" and look like a bug in the middle of an otherwise ordinary sentence.
        ///
        /// The validity test is ThreadDungeonStore.IsSalvageMaterial, the same one the loader's lint uses, so
        /// a row this method declines to name is exactly a row the loader would have refused - see that
        /// method for why the range check in front of Enum.IsDefined is not redundant (MaterialType's
        /// underlying type is uint, and the non-generic IsDefined throws on a boxed int).
        /// </summary>
        private static string SalvageMaterialName(int materialType)
        {
            if (!ThreadDungeonStore.IsSalvageMaterial(materialType))
                return null;

            var name = ((MaterialType)materialType).ToString();
            var text = new System.Text.StringBuilder(name.Length + 4);

            for (var i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                    text.Append(' ');

                text.Append(char.ToLowerInvariant(name[i]));
            }

            return text.ToString();
        }

        /// <summary>
        /// A pressed gem's per-modifier effect text at its ONE rolled magnitude. Unknown/unmapped kinds fall
        /// back to the raw magnitude - pre-existing behaviour, unchanged, since every shipped modifier resolves
        /// to a known kind and this path is reached only for a not-yet-described kind.
        ///
        /// PUBLIC because the item panel is no longer the only surface that describes a finished gem: the
        /// press's chat summary (DungeonGemNarrator.ComposeSummary) calls this same method, so the two cannot
        /// word the same modifier differently. See <see cref="TryDescribeEffect"/>.
        /// </summary>
        public static string RenderEffect(ModifierDef def, double m)
            => TryDescribeEffect(def, out var prefix, out var formatValue, out var suffix)
                ? prefix + formatValue(m) + suffix
                : m.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>
        /// Whether <see cref="RenderEffect"/> can word this modifier's effect at all, or whether it would fall
        /// back to printing the bare magnitude. Exposed for the shipped-content invariant test rather than for
        /// production use - no caller branches on it, because both surfaces want the fallback to happen
        /// silently on the day someone adds a row this switch has not caught up with. What must not happen is
        /// that row SHIPPING, and this is what lets a test say so.
        /// </summary>
        public static bool HasDescribedEffect(ModifierDef def) => TryDescribeEffect(def, out _, out _, out _);

        // RenderEffectRange, the min/max counterpart of RenderEffect, was DELETED here on 2026-09-07. It
        // existed for exactly two callers - DungeonGemNarrator's chat load line and the "Possible effects:"
        // block - and the vague-feedback ruling made both of them category-only, leaving it with no call site
        // at all. It is not kept "for the redesign": a formatter with no caller has no test that exercises it
        // either, so it would rot silently and its own doc comment would be the only thing asserting it works.
        // If a range wording is wanted again, write it against the requirement of the day.

        private static void Say(Player player, string message)
            => player.Session?.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
    }
}
