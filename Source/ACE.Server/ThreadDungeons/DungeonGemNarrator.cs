using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Every player-facing string for gem-component feedback: the load-time line (what a component aims at)
    /// and the press-result breakdown (one line per dose, plus the finished gem's summary). Pure - plain
    /// arguments in, strings out. No PropertyManager read and no Player reference, so this is unit-testable
    /// without either (PropertyManager reads throw under the unit-test harness, and
    /// RawFragment/FragmentPressStation do the actual Say(...)).
    ///
    /// Everything here is presentation only. Loading only records a dose (RawFragmentRules.Load); the op
    /// selection and every op's effect happen at press time inside RawFragmentRules.Resolve. So the load-time
    /// line can only describe what a component AIMS at, never what it did - nothing has happened yet.
    ///
    /// THE LOAD LINE IS VAGUE BY RULING (owner, 2026-09-07), reversing PR #969 which had made it specific.
    /// A player loading a component learns the CATEGORY of what it is reaching for - monster, boss or bonus,
    /// per DungeonModifierCategories - and never the modifier's identity or its magnitude. Both are still
    /// shown in full on the FINISHED gem, which lists its real modifiers with their real effects; the
    /// asymmetry is the point, and it is deliberate: before the press you get hints, after it you get facts.
    /// So the specific wording paths were deleted rather than left unreachable, and with them the range
    /// formatter they were this class's only reason to call.
    ///
    /// It reaches ONE thing in ThreadDungeonGemHandler, and only from the post-press side:
    /// <see cref="ComposeSummary"/> words each of a finished gem's modifiers through
    /// ThreadDungeonGemHandler.RenderEffect, so the chat summary and the item panel describe the same
    /// modifier in the same words (owner ruling, 2026-09-07). That call is a MAGNITUDE formatter and carries
    /// no load-time vocabulary; nothing on the pre-press side of this file touches it.
    /// </summary>
    public static class DungeonGemNarrator
    {
        /// <summary>
        /// The wording a component's op gets when it has NO modifier to categorise - either the op does not
        /// name one, or it names one the store cannot resolve. Everything else is composed in
        /// <see cref="OpPhrase"/> from a verb plus a category word.
        ///
        /// The VERB per op is the owner's ruling: add_or_raise is "add" when the modifier is not already on
        /// the spec and "change" when it is; raise_random, raise_all, set_max, reroll_one and level_up are
        /// "change"; remove_one is "remove"; "nothing" keeps its pre-existing no-op wording.
        ///
        /// lock_one and add_entry were NOT covered by that ruling and are resolved here rather than forced
        /// into it, because neither is an add/change/remove of a modifier: lock_one pins a modifier without
        /// altering it, and add_entry does not touch a modifier at all. Each keeps its own verb, with
        /// lock_one's modifier NAME replaced by its category - which is what the ruling actually forbids.
        /// </summary>
        private const string UncategorisedModifier = "a modifier";

        /// <summary>The no-op wording, kept verbatim from before the vague-feedback ruling.</summary>
        private static readonly (string Single, string ListForm) NothingPhrase = ("does nothing", "do nothing");

        /// <summary>The op-named line for a dose that produced no visible modifier change (round 2): named
        /// from the op itself rather than guessed, since a guess like "eased the fragment" for level_up would
        /// tell the player something that did not happen. Any op not listed - including "nothing" - falls
        /// through to "nothing happened".
        ///
        /// Round 3: naming the op is not by itself proof the op DID anything. lock_one, level_up and add_entry
        /// each have a silent no-op branch, and none of the three shows up in the Before/After modifier diff,
        /// so those three are gated on DoseLogEntry's counter pairs (see OpChangedItsCounter) and fall back to
        /// "nothing happened" when their counter did not move. That gate carries MORE weight since instability
        /// was removed: Before/After no longer encodes an instability term either, so for those three ops the
        /// two strings are now equal on the success path as well and the counters are the only evidence.</summary>
        private static readonly Dictionary<string, string> NoModifierChangeLine = new Dictionary<string, string>
        {
            [AttunementOps.LockOne] = "locked a trait in place",
            [AttunementOps.LevelUp] = "raised the dungeon's level",
            [AttunementOps.AddEntry] = "added an entry",
            [AttunementOps.RemoveOne] = "found nothing to strip",

            // Press v2. set_difficulty moves the level and the entry count, neither of which the modifier
            // diff can see, so it is gated on the counter pairs exactly as level_up and add_entry are.
            [AttunementOps.SetDifficulty] = "set the dungeon's difficulty",
        };

        /// <summary>A modifier's display name, or the raw id when it does not resolve (an unknown or
        /// removed modifier should not drop the sentence).</summary>
        private static string Display(string id, IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            if (string.IsNullOrEmpty(id)) return id;
            return modifiers != null && modifiers.TryGetValue(id, out var def) && !string.IsNullOrEmpty(def.Display)
                ? def.Display
                : id;
        }

        /// <summary>
        /// The grammatical object for an op that names a modifier: "a monster modifier" / "a boss modifier" /
        /// "a bonus modifier", or the uncategorised "a modifier" when the store cannot resolve the id.
        ///
        /// THE MAGNITUDE IS ALWAYS ABSENT - no number of any kind reaches this string, for any modifier,
        /// which is the part of the vague ruling that has no exception.
        ///
        /// THE NAME IS ABSENT FOR EVERYTHING EXCEPT A SALVAGE AFFINITY, which prints its Display verbatim
        /// ("Obsidian Affinity"). <see cref="DungeonModifierCategories.IsNameable"/> is the single place that
        /// exception is decided and carries the reasoning; do not re-test the effect kind here.
        ///
        /// Display VERBATIM, with no article in front of it, and that is deliberate rather than terse: an
        /// article would have to be chosen per material ("a Steel Affinity", "an Obsidian Affinity") from
        /// content the code does not control, and it is how the pressed gem's own panel already prints the
        /// same modifier - so the fragment line and the finished gem read alike.
        /// </summary>
        /// <summary>Whether the id resolves to a modifier the powder exception lets this file name.</summary>
        private static bool IsNameable(string id, IReadOnlyDictionary<string, ModifierDef> modifiers)
            => !string.IsNullOrEmpty(id) && modifiers != null && modifiers.TryGetValue(id, out var def)
               && DungeonModifierCategories.IsNameable(def);

        private static string CategoryObject(string id, IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            if (string.IsNullOrEmpty(id) || modifiers == null || !modifiers.TryGetValue(id, out var def) || def == null)
                return UncategorisedModifier;

            if (DungeonModifierCategories.IsNameable(def))
                return Display(id, modifiers);

            return "a " + DungeonModifierCategories.Word(def) + " modifier";
        }

        /// <summary>
        /// One op as a bare verb phrase, e.g. "add a monster modifier". The Single form used for a one-op
        /// component is this same phrase behind "may", so the sentence stays honest: at load time the press
        /// has not run, and for several ops the component has not even chosen which one it will draw.
        ///
        /// Two forms are still carried explicitly rather than inflected, for the reason the round 2
        /// correction recorded: "do" is irregular, and round 1's attempt to splice a single form after "may"
        /// produced "It may works toward Savage". Only "nothing" now differs between the two forms, but the
        /// pair is kept so a future irregular verb cannot reintroduce that bug.
        /// </summary>
        private static (string Single, string ListForm) OpPhrase(OpDef op, DungeonGemSpec spec,
            IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            var key = op?.Op;

            switch (key)
            {
                case AttunementOps.AddOrRaise:
                {
                    // The one verb that depends on the SPEC rather than only on the op: a modifier already
                    // present is raised, not added. Both outcomes stay behind "may" because the press can
                    // still divert this op (Apply raises a random modifier instead when the cap is full).
                    var present = op.Modifier != null && spec != null
                        && spec.Modifiers.Any(m => m.Id == op.Modifier);
                    var verb = present ? "change" : "add";
                    return May(verb + " " + CategoryObject(op.Modifier, modifiers));
                }

                case AttunementOps.SetMax:
                    return May("change " + CategoryObject(op.Modifier, modifiers));

                case AttunementOps.RerollOne:
                    return May("change " + UncategorisedModifier);

                case AttunementOps.RaiseRandom:
                    return May("change one modifier the gem already has");

                case AttunementOps.RaiseAll:
                    return May("change every modifier the gem has");

                case AttunementOps.RemoveOne:
                    return May("remove " + UncategorisedModifier);

                case AttunementOps.LevelUp:
                    return May("change the dungeon's level");

                case AttunementOps.AddEntry:
                    return May("add an entry");

                case AttunementOps.LockOne:
                    return May("lock " + CategoryObject(op.Modifier, modifiers) + " in place");

                // ---- press v2 ----
                // A category word at most, never a magnitude and never a band - with the ONE exception the
                // owner carved for salvage affinities, which CategoryObject applies and
                // DungeonModifierCategories.IsNameable justifies. The exception is about the NAME only; no
                // number reaches any of these strings for any modifier.

                case AttunementOps.AddRandom:
                    // The op names no modifier by design, so there is nothing to categorise even in
                    // principle - the scope is a POOL, not a promise about which one is drawn.
                    //
                    // The salvage scope is the wildcard powder, and it says so. Under the powder exception a
                    // MAPPED powder names its material, so a wildcard rendered as the generic "add a
                    // modifier" would read as a different kind of component rather than as the same slot
                    // declining to choose. It still names no material, because it has not drawn one.
                    return op.Scope == AttunementScopes.Salvage
                        ? May("add a random salvage affinity")
                        : May("add " + UncategorisedModifier);

                case AttunementOps.Sharpen:
                    if (string.IsNullOrEmpty(op.Modifier))
                        return May("strengthen one modifier the gem already has");

                    // "strengthen a monster modifier the gem already has" needs the trailing clause to say
                    // WHICH monster modifier; "strengthen Obsidian Affinity" already has. Nothing shipped
                    // sharpens an affinity - the talismans all point at monster/boss modifiers - but the op
                    // is data-driven, so the sentence has to survive one being written.
                    return May(IsNameable(op.Modifier, modifiers)
                        ? "strengthen " + CategoryObject(op.Modifier, modifiers)
                        : "strengthen " + CategoryObject(op.Modifier, modifiers) + " the gem already has");

                case AttunementOps.RerollWeakest:
                    return May(op.Amount > 1
                        ? "re-roll the gem's weakest modifiers"
                        : "re-roll the gem's weakest modifier");

                case AttunementOps.SetDifficulty:
                    return May("set the dungeon's level and entry count");

                default:
                    // AttunementOps.Nothing, a null op, and anything the loader let through that this build
                    // does not know.
                    return NothingPhrase;
            }
        }

        /// <summary>Wraps a bare verb phrase into the two forms: "may {phrase}" alone, "{phrase}" in a list.</summary>
        private static (string Single, string ListForm) May(string phrase) => ("may " + phrase, phrase);

        /// <summary>
        /// Sentence 2 of the load-time line: the component's INTENT, never an outcome. One op is "It
        /// {Single}." - "It may add a monster modifier." Several are "It may {ListForm}, {ListForm} or
        /// {ListForm}.", comma-separated with "or" before the last (no comma before "or"), in the component's
        /// own op order. That two-shape structure is unchanged from before the vague-feedback ruling; only
        /// the phrases inside it changed.
        ///
        /// <paramref name="spec"/> is the fragment's spec AFTER the dose was recorded, and is read for one
        /// thing only: whether an add_or_raise op's modifier is already present, which is what decides
        /// "add" versus "change". Null is accepted and reads as "not present".
        ///
        /// IDENTICAL CLAUSES ARE COLLAPSED before the list is joined, keeping the FIRST occurrence so the
        /// component's own op order still decides what the player reads. Press v2 made this necessary and
        /// the vague ruling is what makes it invisible: several ops on one component now routinely differ
        /// only in a modifier NAME, which is exactly the thing the ruling forbids printing, so they render to
        /// the same words. Powdered Quartz - three ops, three different quartz affinities, one category -
        /// produced "It may add a bonus modifier, add a bonus modifier or add a bonus modifier." That is
        /// correct under the ruling and unshippable: it reads as a bug rather than as a hint (caught in
        /// review, 2026-09-07). Collapsing changes no information the player was entitled to, because the
        /// repeated clauses said nothing different.
        /// </summary>
        public static string ComposeIntentSentence(ComponentDef component, DungeonGemSpec spec,
            IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            if (component?.Ops == null || component.Ops.Count == 0)
                return "It " + NothingPhrase.Single + ".";

            // Deduped on the LIST form, but the pair is carried through so a collapse down to one clause can
            // still use that clause's SINGLE form - "It does nothing." rather than "It may do nothing." The
            // two forms differ only for the irregular verb in "nothing", which is the whole reason the pair
            // exists (see OpPhrase).
            var distinct = new List<(string Single, string ListForm)>();

            foreach (var op in component.Ops)
            {
                var phrase = OpPhrase(op, spec, modifiers);

                if (!distinct.Any(p => string.Equals(p.ListForm, phrase.ListForm, StringComparison.Ordinal)))
                    distinct.Add(phrase);
            }

            if (distinct.Count == 1)
                return "It " + distinct[0].Single + ".";

            var head = string.Join(", ", distinct.Take(distinct.Count - 1).Select(p => p.ListForm));
            return "It may " + head + " or " + distinct[distinct.Count - 1].ListForm + ".";
        }

        /// <summary>
        /// The whole load-time line (feature 1): "The fragment takes the X. It may add a monster modifier."
        ///
        /// The third sentence this used to carry - "This pressing will carry roughly N unsteadiness.", from
        /// ComputeProjectedInstability - went with the instability mechanic (owner ruling, 2026-09-07). The
        /// <paramref name="defs"/> parameter is kept on the signature even though only the component and the
        /// spec are read now, because RawFragment's call site has it to hand and a later component-model
        /// change is expected to need it again.
        /// </summary>
        public static string ComposeLoadMessage(ComponentDef component, DungeonGemSpec loadedSpec, AttunementDef defs,
            IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            var parts = new List<string>
            {
                RawFragment.ComposeTakesLine(component?.Name),
                ComposeIntentSentence(component, loadedSpec, modifiers),
            };

            return string.Join(" ", parts);
        }

        // ---- press breakdown (feature 2) ------------------------------------------------------------

        /// <summary>
        /// Parses RawFragmentRules' private Render() format ("mods=&lt;id:mag,...&gt;") back into a mod list.
        /// Render's OWN output format is never changed by this feature - the server log depends on it - this
        /// is a read-only mirror of it, kept here rather than in RawFragmentRules so the core stays untouched.
        ///
        /// The pipe split and the StartsWith test are kept even though "mods=" is now the only term, so a
        /// Render string written before instability was removed (one still carrying "|inst=&lt;n&gt;") parses
        /// to the same mod list rather than being mis-read - the dose log is written to the server log, and
        /// an old line pasted back through here should not silently produce a different answer.
        /// </summary>
        private static void ParseRender(string render, out List<(string Id, double Magnitude)> mods)
        {
            mods = new List<(string Id, double Magnitude)>();
            if (string.IsNullOrEmpty(render)) return;

            foreach (var part in render.Split('|'))
            {
                if (!part.StartsWith("mods=", StringComparison.Ordinal))
                    continue;

                var body = part.Substring("mods=".Length);
                if (body.Length == 0) continue;

                foreach (var item in body.Split(','))
                {
                    var colon = item.IndexOf(':');
                    if (colon <= 0) continue;
                    var id = item.Substring(0, colon);
                    if (double.TryParse(item.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var mag))
                        mods.Add((id, mag));
                }
            }
        }

        private static string FormatMagnitude(double magnitude) => magnitude.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>
        /// Whether an op whose effect the modifier diff cannot see actually moved the thing it aims at.
        /// Only the three counter-backed ops are gated; every other op returns true, so their existing
        /// NoModifierChangeLine wording is untouched. Reads DoseLogEntry's counter pairs, which Resolve
        /// samples around the Apply call alone.
        /// </summary>
        private static bool OpChangedItsCounter(DoseLogEntry dose, string op)
        {
            if (dose == null) return true;

            switch (op)
            {
                case AttunementOps.LockOne: return dose.LocksAfter != dose.LocksBefore;
                case AttunementOps.LevelUp: return dose.LevelAfter != dose.LevelBefore;
                case AttunementOps.AddEntry: return dose.EntriesAfter != dose.EntriesBefore;

                // Press v2's scarab op moves TWO counters and either one counts: a Lead Scarab that lowers
                // the level without changing the entry count has still done its job.
                case AttunementOps.SetDifficulty:
                    return dose.LevelAfter != dose.LevelBefore || dose.EntriesAfter != dose.EntriesBefore;

                default: return true;
            }
        }

        /// <summary>
        /// What changed between a dose's Before and After Render() strings, in prose: a modifier gained, lost
        /// or raised. When no modifier changed, the line is named from the OP via NoModifierChangeLine rather
        /// than guessed (round 2: "eased the fragment" was an invented, direction-only guess that could name
        /// an effect which never happened), and falls through to "nothing happened" for an op that has no
        /// entry there.
        ///
        /// Round 3: for lock_one, level_up and add_entry the op name alone is not evidence the op landed.
        /// Their effects never appear in Render's output, and each has a real silent no-op branch, so those
        /// three are gated on the counter pairs and fall back to the same honest "nothing happened" a genuine
        /// no-op already produces.
        ///
        /// Unlike before the instability removal, this method is now reached even when Before == After: that
        /// used to short-circuit to a bare "nothing happened" in the caller, which after the removal would
        /// have swallowed EVERY successful lock/level/entry dose, since none of the three moves the rendered
        /// modifier list and there is no longer an instability term to move either. The counter-gated
        /// NoModifierChangeLine lookup below is what keeps those three reportable, and it is the only reason
        /// the counters still exist.
        ///
        /// This is a press-time surface, describing what a press ACTUALLY did to the gem, and so is
        /// deliberately untouched by the vague-feedback ruling - that governs the pre-press fragment surfaces
        /// only, and this line names real modifiers with real magnitudes exactly as the finished gem does.
        /// </summary>
        private static string ComposeChange(DoseLogEntry dose, string op, IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            ParseRender(dose?.Before, out var beforeMods);
            ParseRender(dose?.After, out var afterMods);

            var modParts = new List<string>();

            foreach (var (id, afterMag) in afterMods)
            {
                var match = beforeMods.Where(m => m.Id == id).ToList();
                var display = Display(id, modifiers);
                if (match.Count == 0)
                    modParts.Add($"gained {display} {FormatMagnitude(afterMag)}");
                else if (Math.Abs(match[0].Magnitude - afterMag) > 0.0001)
                    modParts.Add($"{display} {FormatMagnitude(match[0].Magnitude)} -> {display} {FormatMagnitude(afterMag)}");
            }

            foreach (var (id, _) in beforeMods)
                if (!afterMods.Any(m => m.Id == id))
                    modParts.Add($"lost {Display(id, modifiers)}");

            return modParts.Count > 0
                ? string.Join(", ", modParts)
                : (NoModifierChangeLine.TryGetValue(op ?? string.Empty, out var named) && OpChangedItsCounter(dose, op)
                    ? named
                    : "nothing happened");
        }

        /// <summary>
        /// One dose line for the press breakdown (feature 2a): "  Red Taper: Savage 12 -&gt; Savage 22" or
        /// "  Red Taper: nothing happened".
        ///
        /// The WILD marker this line used to carry went with the wild draw itself (owner ruling, 2026-09-07):
        /// a press draws only from the component's own op list now, so there is no longer a case where the
        /// player would otherwise think the system is broken.
        ///
        /// The Before == After short circuit that used to sit here is GONE deliberately. It was correct while
        /// Render carried an instability term (equal strings then really did mean nothing happened at all),
        /// but with that term removed a successful lock_one, level_up or add_entry also leaves Before equal to
        /// After, and short-circuiting would have reported every one of them as "nothing happened".
        /// ComposeChange handles the equal case correctly via the counter-gated op lookup.
        /// </summary>
        public static string ComposeDoseLine(DoseLogEntry dose, IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            if (dose == null) return string.Empty;

            // Press v2 marks the slots the board filled in. Under the slot model a player who loaded one
            // taper gets nine dose lines back, and without the marker there is no way to tell which eight
            // components they never chose. Post-press, so naming them is a fact rather than a hint.
            var drawn = dose.Drawn ? " (drawn)" : string.Empty;

            return $"  {dose.Name}{drawn}: {ComposeChange(dose, dose.Op, modifiers)}";
        }

        /// <summary>All dose lines, in resolve order, for feature 2a. Empty when there were no doses.</summary>
        public static IReadOnlyList<string> ComposeDoseLines(IReadOnlyList<DoseLogEntry> doses, IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            var lines = new List<string>();
            if (doses == null) return lines;

            foreach (var dose in doses)
                lines.Add(ComposeDoseLine(dose, modifiers));

            return lines;
        }

        /// <summary>The whole player-facing answer when a press resolved with nothing loaded.</summary>
        public const string NothingSelectedLine = "Components selected: none. All slots drawn at random.";

        /// <summary>The second line, present whenever at least one slot was filled by the board.</summary>
        public const string RemainderDrawnLine = "Remaining slots drawn at random.";

        /// <summary>
        /// WHAT THE PLAYER IS TOLD ABOUT A PRESSING, and the whole of it (owner ruling, 2026-09-07: players
        /// should discover component effects by trial and error, not be told them). It replaces the per-dose
        /// breakdown <see cref="ComposeDoseLines"/> used to send to chat, which named every component - drawn
        /// ones included - and said exactly what each one did.
        ///
        ///     Components selected: Powdered Malachite, Red Taper, Colcothar
        ///     Remaining slots drawn at random.
        ///
        /// THREE RULES, all of them about not leaking what the ruling removed:
        ///   - only the components the player LOADED are named, in load order. A drawn component is never
        ///     named, because naming the eight the board picked would let a player learn a component's effect
        ///     by pressing bare fragments and watching what changes.
        ///   - no effect of any kind is stated, for a loaded component or a drawn one.
        ///   - an empty load says so in one line and stops (<see cref="NothingSelectedLine"/>): there is no
        ///     list to print, and a bare "Remaining slots" after "none" would read as a second thought.
        ///
        /// The list is composed by RawFragmentRules.ComposePressedLine, the SAME function that renders the
        /// fragment panel's "Pressed:" line, so the multiplicity convention ("Red Taper x2, Blue Taper") is
        /// one implementation rather than two that agree today. It is fed the fragment's own load rather than
        /// the dose log's non-drawn entries for the same reason: the load is what the player chose, in the
        /// order they chose it, whereas the log is in board order.
        ///
        /// <paramref name="doses"/> is read for ONE bit - whether any slot was drawn - which is what decides
        /// the second line. Nine loaded slots leave nothing to draw, so the line would be a lie. A null log
        /// suppresses the line rather than guessing, since nothing then attests that a slot was drawn.
        /// </summary>
        public static IReadOnlyList<string> ComposeSelectionLines(IReadOnlyList<(uint Wcid, int Doses)> load,
            Func<uint, string> nameOf, IReadOnlyList<DoseLogEntry> doses)
        {
            var lines = new List<string>();

            if (load == null || load.Count == 0)
            {
                lines.Add(NothingSelectedLine);
                return lines;
            }

            lines.Add("Components selected: " + RawFragmentRules.ComposePressedLine(load, nameOf));

            if (doses != null && doses.Any(d => d != null && d.Drawn))
                lines.Add(RemainderDrawnLine);

            return lines;
        }

        /// <summary>
        /// The finished gem's summary (feature 2b) - always shown: every modifier with what it DOES and its
        /// lock marker, or "The gem carries no modifiers." when there are none, then one line naming the level
        /// and the entry count.
        ///
        ///     The gem carries:
        ///       Bounteous - kills drop x1.27 loot
        ///       Green Garnet Affinity - 18% of kills leave green garnet to salvage
        ///       Stalwart - monsters have +35 damage resist rating, taking less damage from you
        ///     Level 245, 3 entries.
        ///
        /// EACH LINE IS WORDED BY ThreadDungeonGemHandler.RenderEffect, not here (owner ruling, 2026-09-07).
        /// This block used to print a display name and a bare magnitude - "  Bounteous 1.27" - which named the
        /// modifier without saying anything about what it does to the run, and left the player to learn the
        /// meaning of "1.27" somewhere else. The wording it now uses is literally the item panel's, because
        /// RenderEffect and its TryDescribeEffect are the single place MonsterEffectKind-to-wording knowledge
        /// lives; a second switch here would drift from the panel silently, which is the exact failure that
        /// method's own doc comment warns about. So this file deliberately owns no effect vocabulary at all.
        ///
        /// The stability word that closed the last line went with the instability mechanic (owner ruling,
        /// 2026-09-07). This is a POST-press surface describing a finished gem, so it keeps naming real
        /// modifiers at their real magnitudes; the vague-feedback ruling governs the pre-press fragment
        /// surfaces only.
        /// </summary>
        public static IReadOnlyList<string> ComposeSummary(DungeonGemSpec spec, int entries, IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            var lines = new List<string>();
            if (spec == null) return lines;

            if (spec.Modifiers.Count == 0)
            {
                lines.Add("The gem carries no modifiers.");
            }
            else
            {
                lines.Add("The gem carries:");
                foreach (var (id, magnitude) in spec.Modifiers)
                {
                    var locked = spec.Locks.Contains(id) ? " (locked)" : string.Empty;
                    var def = modifiers != null && modifiers.TryGetValue(id, out var found) ? found : null;

                    lines.Add($"  {Display(id, modifiers)} - {ThreadDungeonGemHandler.RenderEffect(def, magnitude)}{locked}");
                }
            }

            var entryWord = entries == 1 ? "entry" : "entries";
            lines.Add($"Level {spec.Level.ToString(CultureInfo.InvariantCulture)}, {entries.ToString(CultureInfo.InvariantCulture)} {entryWord}.");

            return lines;
        }
    }
}
