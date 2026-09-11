using System;
using System.Collections.Concurrent;

using log4net;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Mule Form Token: the decisions behind the Beast Effigy
    /// (Docs/MuleVendor/2026-09-01-mule-form-token-design.md).
    ///
    /// A static handler, following the fork's rule that new item behaviour adds no WeenieType and no
    /// WorldObject subclass. Everything here is pure or takes only WorldObjects; nothing touches
    /// Player or Session, because Player.UpdateProperty's SendNetwork dereferences Session with no
    /// null check and so cannot run in a unit test. The Player-side glue that does the writing lives
    /// in Player_MuleFormToken.cs and is deliberately as thin as it can be.
    ///
    /// NO WCID IS A LITERAL HERE. Recognition is PropertyBool.MuleFormToken and attunement is
    /// PropertyInt.MuleFormWcid, so a second token weenie - different art, a different kill count - is
    /// pure content with no code change.
    ///
    /// THE PLAYER-FACING STRINGS BELOW ARE THE SPEC'S, VERBATIM (design section 3). They are owned
    /// here, in one place, so a caller cannot re-word one; if a string is wrong it is fixed once.
    /// </summary>
    public static class MuleFormToken
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Used with no target while still unattuned.</summary>
        public const string NeedsATargetMessage = "Use the effigy on a living creature to attune it.";

        /// <summary>Used on something that cannot become a mule body. One string for every rule.</summary>
        public const string CannotTakeThatShapeMessage = "The effigy cannot take that shape.";

        /// <summary>Used on any target once already attuned. There is no re-attunement, by design.</summary>
        public const string AlreadyAttunedMessage = "The effigy is already attuned.";

        /// <summary>
        /// Appended to a refused summon on the completed-token path. The look is saved BEFORE the
        /// summon is attempted and stays saved when the summon is refused, so a player who earned a
        /// form is never told it was wasted (design section 8).
        /// </summary>
        public const string LookSavedSuffix = "The form is saved and your mule will wear it at your next /mule.";

        /// <summary>TRUE for an item carrying the marker bool. The wcid is never consulted.</summary>
        public static bool IsToken(WorldObject item) => item?.GetProperty(PropertyBool.MuleFormToken) == true;

        /// <summary>The wcid this token is attuned to, or null while it is unattuned.</summary>
        public static uint? GetFormWcid(WorldObject token)
        {
            var wcid = token?.GetProperty(PropertyInt.MuleFormWcid);

            return wcid > 0 ? (uint?)wcid.Value : null;
        }

        public static bool IsAttuned(WorldObject token) => GetFormWcid(token) != null;

        /// <summary>Kills recorded so far, read off Structure. 0 when absent.</summary>
        public static int GetStructure(WorldObject token) => token?.Structure ?? 0;

        /// <summary>Kills required, read off MaxStructure. 0 when absent.</summary>
        public static int GetMaxStructure(WorldObject token) => token?.MaxStructure ?? 0;

        /// <summary>
        /// TRUE for an attuned token that has taken all the kills it will hold.
        ///
        /// A token with no MaxStructure at all is NOT complete. That is malformed content rather than
        /// a finished token, and reporting it complete would let it write a look it never earned.
        /// </summary>
        public static bool IsComplete(WorldObject token)
        {
            var max = GetMaxStructure(token);

            return IsAttuned(token) && max > 0 && GetStructure(token) >= max;
        }

        /// <summary>TRUE for an attuned token of this wcid that still has room.</summary>
        public static bool HasRoom(WorldObject token, uint victimWcid)
        {
            var max = GetMaxStructure(token);

            return IsToken(token) && GetFormWcid(token) == victimWcid && max > 0 && GetStructure(token) < max;
        }

        /// <summary>
        /// The token's UNDECORATED name, read off the weenie template rather than the instance.
        ///
        /// This has to come from the weenie: the instance's name is rewritten on attune and rewritten
        /// again on completion, so composing the completed name from the instance would produce
        /// "Beast Effigy (Tusker Guard) of the Tusker Guard". The weenie is the shared cached template
        /// and is never mutated by anything in this feature, so it is the stable source.
        /// </summary>
        public static string GetBaseName(WorldObject token)
        {
            var fromWeenie = token?.Weenie?.GetProperty(PropertyString.Name);

            return string.IsNullOrWhiteSpace(fromWeenie) ? token?.Name : fromWeenie;
        }

        /// <summary>
        /// The first token anywhere in this container that is attuned to <paramref name="victimWcid"/>
        /// and is not yet full, or null.
        ///
        /// Side packs are searched too, and in a SECOND pass, exactly like
        /// KillFillVessel.FindFillable: Container.Inventory holds only the items directly in that
        /// container, so a main-pack-only search would make the feature depend on where the player
        /// happened to stow the token, working in the backpack and silently doing nothing in a side
        /// pouch with no message either way. Running direct items first also makes "first found" mean
        /// the same thing in both features.
        ///
        /// Exactly ONE token fills per kill, the first found (design section 3). A completed token is
        /// skipped rather than ending the search, so a second token attuned to the same wcid starts
        /// filling once the first is done.
        ///
        /// The mule's own inventory is not searched: user ruling, pack and side packs only.
        /// </summary>
        public static WorldObject FindFillableToken(Container container, uint victimWcid)
        {
            if (container == null)
                return null;

            foreach (var item in container.Inventory.Values)
            {
                if (HasRoom(item, victimWcid))
                    return item;
            }

            foreach (var item in container.Inventory.Values)
            {
                if (item is Container sideContainer)
                {
                    var found = FindFillableToken(sideContainer, victimWcid);

                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        /// <summary>
        /// The kill count after one qualifying kill, clamped to capacity. Separate from the write so
        /// the clamp is testable without a Player.
        /// </summary>
        public static int NextStructure(int structure, int maxStructure)
        {
            if (structure >= maxStructure)
                return maxStructure;

            return structure + 1;
        }

        /// <summary>
        /// Whether this target can become a mule body.
        ///
        /// The WorldObject overload; the decision itself is the primitive one below, so every branch
        /// is testable without constructing a Creature (whose ephemeral setup reaches for the world
        /// database and the client dat files). This is also the ONLY place that reads
        /// mule_form_max_effect_scale off PropertyManager - the primitive overload takes the threshold
        /// as a plain float so it stays reachable with no database (see that overload's doc comment).
        /// </summary>
        public static bool IsValidAttunementTarget(WorldObject target, Func<uint, float> setupHeight,
                                                   Func<uint, float> setupEffectScale, out string refusal)
        {
            if (target == null)
            {
                refusal = CannotTakeThatShapeMessage;
                return false;
            }

            return IsValidAttunementTarget(
                target is Creature,
                target is Player,
                target is Pet,
                target.WeenieType,
                target.Attackable,
                target.SetupTableId,
                setupHeight,
                setupEffectScale,
                (float)PropertyManager.GetDouble("mule_form_max_effect_scale").Item,
                out refusal);
        }

        /// <summary>
        /// The rules from design section 4, over primitives.
        ///
        /// <paramref name="setupHeight"/> answers the donor model's height for a Setup id and must
        /// answer 0 or less for one it cannot measure. It is injected rather than read from
        /// DatManager because WorldObject.IsValidSetupId returns TRUE when no dat is loaded (which is
        /// every unit test and every content tool), so the height is the only check that can actually
        /// discriminate in a test host - and a zero height is exactly what makes the scale formula in
        /// MuleFormBody undefined.
        ///
        /// <paramref name="setupEffectScale"/> answers the largest particle-emitter scale (StartScale
        /// or FinalScale) baked into the Setup's own DefaultScript, or 0f when it bakes no script.
        /// Also injected, for the same testability reason as the height, and for a second one:
        /// <paramref name="maxEffectScale"/> is a live PropertyManager tunable
        /// (mule_form_max_effect_scale), and PropertyManager reads THROW in a unit test host with no
        /// database - so this primitive overload must never touch PropertyManager itself. The
        /// WorldObject overload above is the one place that reads it.
        ///
        /// WHY A THRESHOLD, NOT A BOOLEAN, AND WHY IT EXISTS AT ALL. MuleFormBody.CloneWithDonorBody
        /// scales the donor's MESH to the mule's own height, but a client Setup can additionally bake
        /// a DefaultScript the client always plays when it builds the object - and particle emitter
        /// scale inside that script is absolute dat data, unrelated to the object's own scale, so it
        /// cannot be suppressed or resized from the server. Verified in game 2026-09-07: a captured
        /// Shadow Vortex (wcid 44629) rendered a full-height black cloud around a vendor whose MESH had
        /// scaled correctly to 0.5225, and two otherwise-identical props differing only in their
        /// authored script rendered the same, ruling out any server-side suppression. A scan of every
        /// creature Setup that bakes a script found the overwhelming majority under a max emitter scale
        /// of 8, with a long thin tail up to 30 (the Shadow Vortex is the single worst offender) - so a
        /// flat ban on "bakes any script" would refuse the majority of eligible donors for a defect
        /// that only bites the tail, and a threshold is the fork's answer.
        ///
        /// WHY THERE IS NO MATCHING CHECK AT SUMMON TIME. Owner ruling: this gate is ATTUNEMENT-ONLY,
        /// by design, not an oversight to complete later. A player who already captured an oversized
        /// form before this shipped keeps it rendering - deliberate goodwill toward the beta group -
        /// and MuleFormBody.CloneWithDonorBody and MuleSummonHandler are untouched by this feature. Do
        /// not add a summon-side mirror of this check; that scope was explicitly declined.
        ///
        /// Bosses and event spawns are deliberately allowed (user ruling). Attunement records the
        /// WeenieClassId, so a Tusker Guard and a Tusker are different forms even though they share a
        /// CreatureType.
        /// </summary>
        public static bool IsValidAttunementTarget(bool isCreature, bool isPlayer, bool isPet, WeenieType weenieType,
                                                   bool attackable, uint setupId, Func<uint, float> setupHeight,
                                                   Func<uint, float> setupEffectScale, float maxEffectScale,
                                                   out string refusal)
        {
            refusal = CannotTakeThatShapeMessage;

            if (!isCreature || isPlayer || isPet)
                return false;

            if (!attackable)
                return false;

            // Excludes PersonalVendor with it: a mule wearing another mule is not a shape.
            if (weenieType == WeenieType.Vendor)
                return false;

            if (setupId == 0 || !WorldObject.IsValidSetupId(setupId))
                return false;

            if (setupHeight == null || !(setupHeight(setupId) > 0f))
                return false;

            if (setupEffectScale == null || setupEffectScale(setupId) > maxEffectScale)
                return false;

            refusal = null;
            return true;
        }

        /// <summary>
        /// The token's name for a given state. Design section 3, verbatim:
        /// "Beast Effigy (Tusker Guard)" while filling, "Beast Effigy of the Tusker Guard" once
        /// complete.
        /// </summary>
        public static string ComposeName(string baseName, string donorName, bool complete)
        {
            if (complete)
                return $"{baseName} of the {donorName}";

            return $"{baseName} ({donorName})";
        }

        /// <summary>
        /// The token's ITEM description (the LongDesc property). Owner-ruled 2026-09-02: the item
        /// description carries the full instructions, so a filling token restates both the current
        /// count and how to finish it, and a complete token points at the Marketplace.
        /// </summary>
        public static string ComposeLongDesc(string donorName, int structure, int maxStructure)
        {
            if (maxStructure > 0 && structure >= maxStructure)
                return $"Attuned to {donorName}. Complete. Use in the Marketplace to change the look of your /mule.";

            return $"{ComposeStatus(donorName, structure, maxStructure)} {ComposeFillingInstructions(maxStructure)}";
        }

        /// <summary>
        /// The token's CHAT status line: the short form used on attunement confirmation and on a
        /// status check with no target. Owner-ruled 2026-09-02: chat gets only this short line, never
        /// the full instructions that the item description carries.
        /// </summary>
        public static string ComposeStatus(string donorName, int structure, int maxStructure)
        {
            if (maxStructure > 0 && structure >= maxStructure)
                return $"Attuned to {donorName}. Complete.";

            return $"Attuned to {donorName}. {structure} of {maxStructure} slain.";
        }

        /// <summary>
        /// The instructions for finishing a filling token, appended to the status line in the item
        /// description. A method, not a literal, so the kill count always matches this token's actual
        /// <paramref name="maxStructure"/> rather than a hard-coded 100.
        /// </summary>
        public static string ComposeFillingInstructions(int maxStructure) =>
            $"Strike the killing blow on {maxStructure} of its kind to fill it, then use the filled effigy in the Marketplace to change the look of your /mule. Your /mule can be reshaped again as you collect more.";

        /// <summary>The chat line on the kill that completes a token. Design section 3, verbatim.</summary>
        public static string ComposeFullMessage(string baseName) => $"Your {baseName} has taken all it will hold.";

        /// <summary>
        /// The production height lookup, out of the client dat. The ONE impure member of this class,
        /// and it is here rather than duplicated at each call site so the attunement gate and the
        /// summon-time scale formula can never disagree about what "measurable" means.
        ///
        /// 0 for anything unreadable. TryGetSetup never throws and its fallback model has no height,
        /// so an unreadable body is refused by the caller rather than producing a NaN scale. Tests
        /// inject their own delegate instead of calling this.
        /// </summary>
        public static float DatSetupHeight(uint setupId)
        {
            if (!WorldObject.TryGetSetup(setupId, out var setup))
                return 0f;

            return setup?.Height ?? 0f;
        }

        /// <summary>
        /// Per-setup memo for <see cref="DatSetupEffectScale"/>. Only 835 creature Setups exist in the
        /// local ace_world, but this runs on a player action (Gem.HandleMuleFormTokenUseOnTarget), and
        /// each miss otherwise re-reads a PhysicsScript plus one ParticleEmitterInfo per hook.
        ///
        /// FINITE RESULTS ONLY. A float.PositiveInfinity answer means a dat READ THREW - a transient
        /// disk hiccup or similar - not that the body is genuinely unmeasurable, so it must never be
        /// written here: a static, process-lifetime, no-eviction cache would otherwise bar a perfectly
        /// good donor until the server restarts, with nothing in the log to explain why. Leaving a
        /// failure uncached gives it the same self-healing posture DatSetupHeight already has - the
        /// next attempt just retries.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, float> setupEffectScaleCache = new ConcurrentDictionary<uint, float>();

        /// <summary>
        /// The production effect-scale lookup, out of the client dat: the largest particle emitter
        /// scale (StartScale or FinalScale) anywhere in the PhysicsScript this Setup bakes as its
        /// DefaultScript. The ONE impure member added for this gate, kept here beside
        /// <see cref="DatSetupHeight"/> for the same reason - the attunement gate and any future reader
        /// of "how big is this donor's baked effect" should never disagree about the answer.
        ///
        /// The contract, in full:
        /// - TryGetSetup fails (the Setup id itself is bad) -&gt; float.PositiveInfinity. We know
        ///   nothing about this body. In practice the height check already refuses first for the same
        ///   Setup, so this is belt and braces.
        /// - DefaultScript is 0 (bakes nothing) -&gt; 0f. By far the common case, and it must pass the
        ///   gate.
        /// - The script id, or an emitter info id it references, does NOT resolve in the dat -&gt; 0f,
        ///   NOT a refusal. ReadFromDat never returns null for a missing id - it logs and hands back a
        ///   fresh, never-unpacked instance - so an id absent from the dat is a script or emitter that
        ///   plain does not exist there. An id the SERVER cannot resolve is one the CLIENT cannot
        ///   resolve either, and a script the client cannot resolve is one it cannot draw, so scoring
        ///   it 0f (no effect) is the correct answer, not a conservative one.
        /// - Reading or unpacking throws -&gt; float.PositiveInfinity, logged, and never cached (see
        ///   setupEffectScaleCache). This is the one genuinely unmeasurable case: a transient dat
        ///   problem, not a hole in the content.
        ///
        /// Never throws. Tests inject their own delegate instead of calling this.
        /// </summary>
        public static float DatSetupEffectScale(uint setupId) => GetOrComputeEffectScale(setupId, ReadSetupEffectScale);

        /// <summary>
        /// The cache-or-compute seam behind <see cref="DatSetupEffectScale"/>, taking the compute
        /// delegate as a parameter so a test can inject one that fails once and succeeds the next call
        /// and observe that the failure was never written to <see cref="setupEffectScaleCache"/> - the
        /// production path always passes <see cref="ReadSetupEffectScale"/>, which needs real dat data
        /// no unit test host has. Internal rather than private for exactly that reason; never called
        /// with a substitute delegate outside a test.
        /// </summary>
        internal static float GetOrComputeEffectScale(uint setupId, Func<uint, float> compute)
        {
            if (setupEffectScaleCache.TryGetValue(setupId, out var cached))
                return cached;

            var scale = compute(setupId);

            if (float.IsFinite(scale))
                setupEffectScaleCache[setupId] = scale;

            return scale;
        }

        private static float ReadSetupEffectScale(uint setupId)
        {
            if (!WorldObject.TryGetSetup(setupId, out var setup) || setup == null)
                return float.PositiveInfinity;

            if (setup.DefaultScript == 0)
                return 0f;

            // Existence check only - AllFiles is a plain Dictionary lookup, unlike GetReaderForFile,
            // which would build a DatReader and pull the buffer just to answer the same yes/no.
            if (!DatManager.PortalDat.AllFiles.ContainsKey(setup.DefaultScript))
                return 0f;

            PhysicsScript script;
            try
            {
                script = DatManager.PortalDat.ReadFromDat<PhysicsScript>(setup.DefaultScript);
            }
            catch (Exception ex)
            {
                log.Warn($"[MULEFORM] DatSetupEffectScale: setup 0x{setupId:X8} script 0x{setup.DefaultScript:X8} unreadable: {ex}");
                return float.PositiveInfinity;
            }

            var maxScale = 0f;

            foreach (var scriptData in script.ScriptData)
            {
                if (!(scriptData.Hook is ACE.DatLoader.Entity.AnimationHooks.CreateParticleHook hook))
                    continue;

                if (!DatManager.PortalDat.AllFiles.ContainsKey(hook.EmitterInfoId))
                    continue;   // an emitter the client cannot resolve either draws nothing

                ParticleEmitterInfo emitterInfo;
                try
                {
                    emitterInfo = DatManager.PortalDat.ReadFromDat<ParticleEmitterInfo>(hook.EmitterInfoId);
                }
                catch (Exception ex)
                {
                    log.Warn($"[MULEFORM] DatSetupEffectScale: setup 0x{setupId:X8} script 0x{setup.DefaultScript:X8} emitterInfo 0x{hook.EmitterInfoId:X8} unreadable: {ex}");
                    return float.PositiveInfinity;
                }

                if (emitterInfo.StartScale > maxScale)
                    maxScale = emitterInfo.StartScale;

                if (emitterInfo.FinalScale > maxScale)
                    maxScale = emitterInfo.FinalScale;
            }

            return maxScale;
        }

        /// <summary>
        /// TEST-ONLY seam: drops the effect-scale memo for one Setup id, so a test can force
        /// DatSetupEffectScale to recompute rather than answer from a value cached by an earlier test
        /// or an earlier call in the same process. Never called from production code - the whole point
        /// of the cache is that production never needs to invalidate it (see setupEffectScaleCache).
        /// </summary>
        internal static void ClearEffectScaleCacheForTest(uint setupId)
        {
            setupEffectScaleCache.TryRemove(setupId, out _);
        }
    }
}
