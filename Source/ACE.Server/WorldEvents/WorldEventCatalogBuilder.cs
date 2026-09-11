using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The family roster, built by scanning the world weenie cache for flagged creature weenies
    /// (TECH-DESIGN 2.3, decision C2). Immutable once built; "/worldevent reload" replaces it wholesale.
    /// </summary>
    public sealed class WorldEventCatalog
    {
        /// <summary>
        /// The derived per-family flags a random composition pairs on (two-family composition, 2026-08-29),
        /// computed once per catalog build so <see cref="WorldEventFamilyPairing.CandidatePairs"/> never has
        /// to walk a roster.
        ///
        /// Every field here is computed over the family's members with
        /// <c>Role != WorldEventRosterSelector.NamedBossRole</c>. A named boss arrives only when the boss
        /// axis asks for it by id - it is never in a wave and never the family champion (see
        /// WorldEventRosterSelector.PickChampion) - so counting it here would let a family qualify as the
        /// low-level half of a pair, or as the pair's caster, on the strength of a monster the crowd can
        /// never meet.
        /// </summary>
        public sealed class FamilyProfile
        {
            public string Id { get; }

            /// <summary>Members excluding named bosses. 0 for a family whose only members are named bosses.</summary>
            public int MemberCount { get; }

            /// <summary>Lowest/highest member level, named bosses excluded. Both 0 when MemberCount is 0.</summary>
            public int MinLevel { get; }

            public int MaxLevel { get; }

            public bool HasCaster { get; }

            public int CasterCount { get; }

            public FamilyProfile(string id, int memberCount, int minLevel, int maxLevel, int casterCount)
            {
                Id = id;
                MemberCount = memberCount;
                MinLevel = minLevel;
                MaxLevel = maxLevel;
                CasterCount = casterCount;
                HasCaster = casterCount > 0;
            }
        }

        public IReadOnlyDictionary<string, IReadOnlyList<FamilyMember>> Families { get; }

        /// <summary>
        /// One <see cref="FamilyProfile"/> per entry in <see cref="Families"/>, same keys, computed once in
        /// the constructor. Never null, and never missing a key that Families has.
        /// </summary>
        public IReadOnlyDictionary<string, FamilyProfile> Profiles { get; }

        /// <summary>Human-readable authoring problems found during the scan. Never thrown, always reported.</summary>
        public IReadOnlyList<string> Diagnostics { get; }

        public int MemberCount { get; }

        public static readonly WorldEventCatalog Empty = new WorldEventCatalog(
            new Dictionary<string, IReadOnlyList<FamilyMember>>(), new List<string>());

        public WorldEventCatalog(IReadOnlyDictionary<string, IReadOnlyList<FamilyMember>> families,
            IReadOnlyList<string> diagnostics)
        {
            Families = families ?? new Dictionary<string, IReadOnlyList<FamilyMember>>();
            Diagnostics = diagnostics ?? new List<string>();
            MemberCount = Families.Values.Sum(m => m.Count);

            var profiles = new Dictionary<string, FamilyProfile>();

            foreach (var kvp in Families)
                profiles[kvp.Key] = BuildProfile(kvp.Key, kvp.Value);

            Profiles = profiles;
        }

        private static FamilyProfile BuildProfile(string id, IReadOnlyList<FamilyMember> members)
        {
            var crowd = (members ?? new List<FamilyMember>())
                .Where(m => m != null && m.Role != WorldEventRosterSelector.NamedBossRole)
                .ToList();

            if (crowd.Count == 0)
                return new FamilyProfile(id, 0, 0, 0, 0);

            return new FamilyProfile(id, crowd.Count, crowd.Min(m => m.Level), crowd.Max(m => m.Level),
                crowd.Count(m => m.Caster));
        }

        /// <summary>
        /// The profile for <paramref name="familyId"/>, or an empty one (MemberCount 0) when the catalog has
        /// no such family. Never null, so a caller never needs a TryGetValue plus a null check.
        /// </summary>
        public FamilyProfile Profile(string familyId)
        {
            var id = familyId == null ? null : familyId.Trim().ToLowerInvariant();

            if (id != null && Profiles.TryGetValue(id, out var profile))
                return profile;

            return new FamilyProfile(id, 0, 0, 0, 0);
        }

        /// <summary>
        /// One-line coverage summary for "/worldevent list families", e.g.
        /// "emberwrought: 8 members, levels 20-140, trash 5 elite 2 champion 1 named 0, flagged 8 table 0,
        /// casters 2, minLevel 20".
        ///
        /// The trailing "casters"/"minLevel" pair is the pairing profile (two-family composition,
        /// 2026-08-29), so both EXCLUDE named bosses and can therefore disagree with the "levels lo-hi"
        /// span earlier on the line, which counts every member. They are the two numbers
        /// <see cref="WorldEventFamilyPairing"/> actually pairs on, which is why they are shown rather
        /// than re-derived by eye.
        /// </summary>
        public string DescribeCoverage(string familyId)
        {
            var id = familyId == null ? null : familyId.Trim().ToLowerInvariant();

            if (id == null || !Families.TryGetValue(id, out var members) || members.Count == 0)
                return $"{familyId}: no members";

            var trash = members.Count(m => m.Role == 0);
            var elite = members.Count(m => m.Role == 1);
            var champion = members.Count(m => m.Role == 2);
            var named = members.Count(m => m.Role == WorldEventRosterSelector.NamedBossRole);

            var low = members.Min(m => m.Level);
            var high = members.Max(m => m.Level);

            var flaggedCount = members.Count(m => !m.FromTable);
            var tableCount = members.Count(m => m.FromTable);

            var profile = Profile(id);

            return $"{id}: {members.Count} members, levels {low}-{high}, " +
                   $"trash {trash} elite {elite} champion {champion} named {named}, " +
                   $"flagged {flaggedCount} table {tableCount}, " +
                   $"casters {profile.CasterCount}, minLevel {profile.MinLevel}";
        }
    }

    /// <summary>
    /// Builds a <see cref="WorldEventCatalog"/> from a weenie collection, optionally merged with species
    /// reference tables (TECH-DESIGN C15, 2026-08-16). Pure over its input, so it is testable with
    /// hand-built Weenie objects and needs no database (D6).
    ///
    /// A weenie joins the catalog either by carrying PropertyBool.WorldEventCreature (the scan, unchanged),
    /// or by being listed in a species table's members[] and passing the same eligibility rules. Everything
    /// else in the world database is skipped silently; a weenie that DOES carry the flag, or IS listed in a
    /// species table, but fails any other rule is dropped with a diagnostic naming the wcid and the rule,
    /// because that is an authoring mistake somebody needs to see. When the same wcid is both flagged and
    /// listed in a table, the flag wins and the table entry is dropped with a diagnostic - the flag is the
    /// custom-family author's explicit claim on that wcid.
    ///
    /// Also enforces species rule R9 (portal on death, see tools/we-species-tables/README.md) on both
    /// paths, but ONLY its DID half: a weenie carrying PropertyDataId.LinkedPortalOne or LinkedPortalTwo
    /// is dropped with a diagnostic. This builder is pure over its own candidate weenie (no database, D6),
    /// so it cannot see whether a Death emote's Generate action spawns a Portal/HousePortal weenie
    /// (R9's other mechanism) - that half is enforced only by the species-table generator at authoring
    /// time. R10 (guaranteed quest-item drop) is not enforced here at all, for the same reason: it
    /// requires reading the OWNED weenie's own create-list targets, off a different weenie than the
    /// candidate itself.
    /// </summary>
    public static class WorldEventCatalogBuilder
    {
        private static readonly Regex FamilyIdPattern = new Regex("^[a-z0-9_]+$", RegexOptions.Compiled);

        /// <summary>
        /// The caster predicate (two-family composition, 2026-08-29): a non-empty spellbook on the weenie
        /// itself. Pure over the candidate weenie, like every other rule in this builder - the spellbook is
        /// already on the cached weenie (WeenieConverter.cs:91-95), so this costs no extra database read.
        ///
        /// A monster casts iff its spellbook is non-empty: the magic branch of the monster attack roll sits
        /// behind HasKnownSpells (Monster_Combat.cs:108), which is Biota.HasKnownSpell
        /// (Monster_Magic.cs:52) over the same rows.
        /// </summary>
        private static bool IsCaster(Weenie weenie)
        {
            return weenie?.PropertiesSpellBook != null && weenie.PropertiesSpellBook.Count > 0;
        }

        public static WorldEventCatalog Build(IEnumerable<Weenie> weenies, out List<string> diagnostics)
        {
            return Build(weenies, null, out diagnostics);
        }

        /// <summary>
        /// The distinct union of flagged wcids and every member wcid referenced across the species tables,
        /// ordered ascending. Pure and null-safe: tolerates a null <paramref name="flaggedWcids"/>, a null
        /// <paramref name="speciesTables"/>, null table entries within it, and skips wcid 0. Used by
        /// WorldEventManager to build the minimal list of wcids that need a lazy weenie read, instead of
        /// caching every weenie in the world database (TECH-DESIGN C15, 2026-08-16).
        /// </summary>
        public static List<uint> CollectWcids(IEnumerable<uint> flaggedWcids, IEnumerable<SpeciesTableDef> speciesTables)
        {
            var wcids = new HashSet<uint>();

            if (flaggedWcids != null)
            {
                foreach (var wcid in flaggedWcids)
                {
                    if (wcid != 0)
                        wcids.Add(wcid);
                }
            }

            if (speciesTables != null)
            {
                foreach (var table in speciesTables)
                {
                    if (table?.Members == null)
                        continue;

                    foreach (var member in table.Members)
                    {
                        if (member != null && member.Wcid != 0)
                            wcids.Add(member.Wcid);
                    }
                }
            }

            return wcids.OrderBy(id => id).ToList();
        }

        public static WorldEventCatalog Build(IEnumerable<Weenie> weenies, IEnumerable<SpeciesTableDef> speciesTables,
            out List<string> diagnostics)
        {
            diagnostics = new List<string>();

            var grouped = new Dictionary<string, List<FamilyMember>>();

            if (weenies == null)
                return new WorldEventCatalog(new Dictionary<string, IReadOnlyList<FamilyMember>>(), diagnostics);

            var list = weenies as ICollection<Weenie> ?? weenies.ToList();

            var byWcid = new Dictionary<uint, Weenie>();
            var flaggedFamilyByWcid = new Dictionary<uint, string>();

            foreach (var weenie in list)
            {
                if (weenie == null)
                    continue;

                byWcid[weenie.WeenieClassId] = weenie;

                if (weenie.GetProperty(PropertyBool.WorldEventCreature) != true)
                    continue;

                var wcid = weenie.WeenieClassId;

                if (weenie.WeenieType != WeenieType.Creature)
                {
                    diagnostics.Add($"wcid {wcid}: WorldEventCreature is set but WeenieType is {weenie.WeenieType}, not Creature - dropped");
                    continue;
                }

                if (weenie.GetProperty(PropertyBool.WorldEventObjective) == true)
                {
                    diagnostics.Add($"wcid {wcid}: carries BOTH WorldEventCreature and WorldEventObjective - objective spawns (the Rift) are never catalog members - dropped");
                    continue;
                }

                if (weenie.GetProperty(PropertyBool.Attackable) != true)
                {
                    diagnostics.Add($"wcid {wcid}: WorldEventCreature is set but Attackable is not true - dropped");
                    continue;
                }

                if (weenie.GetProperty(PropertyDataId.LinkedPortalOne) != null || weenie.GetProperty(PropertyDataId.LinkedPortalTwo) != null)
                {
                    diagnostics.Add($"wcid {wcid}: summons a portal on death (LinkedPortalOne/Two, species rule R9) - dropped");
                    continue;
                }

                var family = weenie.GetProperty(PropertyString.WorldEventFamily);

                if (string.IsNullOrWhiteSpace(family))
                {
                    diagnostics.Add($"wcid {wcid}: WorldEventCreature is set but WorldEventFamily is missing or empty - dropped");
                    continue;
                }

                family = family.Trim();

                if (!FamilyIdPattern.IsMatch(family))
                {
                    diagnostics.Add($"wcid {wcid}: WorldEventFamily '{family}' is not a valid id (lowercase a-z, 0-9 and underscore only) - dropped");
                    continue;
                }

                var level = weenie.GetProperty(PropertyInt.Level);

                if (level == null)
                {
                    diagnostics.Add($"wcid {wcid}: WorldEventCreature is set but Level is missing - dropped");
                    continue;
                }

                var roleValue = weenie.GetProperty(PropertyInt.WorldEventRole);
                int role;

                if (roleValue == null)
                {
                    diagnostics.Add($"wcid {wcid}: WorldEventRole is missing - treated as 0 (trash)");
                    role = 0;
                }
                else if (roleValue.Value < 0 || roleValue.Value > WorldEventRosterSelector.NamedBossRole)
                {
                    diagnostics.Add($"wcid {wcid}: WorldEventRole {roleValue.Value} is outside 0..3 (0 trash / 1 elite / 2 champion / 3 named boss) - dropped");
                    continue;
                }
                else
                    role = roleValue.Value;

                if (!grouped.TryGetValue(family, out var members))
                {
                    members = new List<FamilyMember>();
                    grouped[family] = members;
                }

                members.Add(new FamilyMember
                {
                    Wcid = wcid,
                    Name = weenie.GetName() ?? weenie.ClassName,
                    Level = level.Value,
                    Role = role,
                    Caster = IsCaster(weenie),
                    FamilyId = family
                });

                flaggedFamilyByWcid[wcid] = family;
            }

            MergeSpeciesTables(speciesTables, byWcid, flaggedFamilyByWcid, grouped, diagnostics);

            var families = new Dictionary<string, IReadOnlyList<FamilyMember>>();

            foreach (var kvp in grouped)
            {
                var sorted = kvp.Value
                    .OrderBy(m => m.Level)
                    .ThenBy(m => m.Wcid)
                    .ToList();

                families[kvp.Key] = sorted;
            }

            return new WorldEventCatalog(families, diagnostics);
        }

        /// <summary>
        /// Joins species-table members into the catalog (TECH-DESIGN C15, 2026-08-16). Runs after the
        /// flag scan so <paramref name="flaggedFamilyByWcid"/> is complete - the flag always wins.
        /// </summary>
        private static void MergeSpeciesTables(IEnumerable<SpeciesTableDef> speciesTables,
            Dictionary<uint, Weenie> byWcid, Dictionary<uint, string> flaggedFamilyByWcid,
            Dictionary<string, List<FamilyMember>> grouped, List<string> diagnostics)
        {
            if (speciesTables == null)
                return;

            var seenWcids = new Dictionary<uint, string>();

            foreach (var table in speciesTables)
            {
                if (table?.Members == null)
                    continue;

                foreach (var member in table.Members)
                {
                    var wcid = member.Wcid;

                    if (!byWcid.TryGetValue(wcid, out var weenie))
                    {
                        diagnostics.Add($"species/{table.Id}: wcid {wcid} not found in the world database - dropped");
                        continue;
                    }

                    if (flaggedFamilyByWcid.TryGetValue(wcid, out var flagFamily))
                    {
                        diagnostics.Add($"species/{table.Id}: wcid {wcid} is already flagged WorldEventCreature (family '{flagFamily}'); the flag wins, table entry ignored");
                        continue;
                    }

                    if (seenWcids.TryGetValue(wcid, out var firstTableId))
                    {
                        diagnostics.Add(firstTableId == table.Id
                            ? $"species/{table.Id}: wcid {wcid} is listed twice in this file - dropped"
                            : $"species/{table.Id}: wcid {wcid} was already listed in species/{firstTableId} - dropped");
                        continue;
                    }

                    if (weenie.WeenieType != WeenieType.Creature)
                    {
                        diagnostics.Add($"species/{table.Id}: wcid {wcid} has WeenieType {weenie.WeenieType}, not Creature - dropped");
                        seenWcids[wcid] = table.Id;
                        continue;
                    }

                    if (weenie.GetProperty(PropertyBool.WorldEventObjective) == true)
                    {
                        diagnostics.Add($"species/{table.Id}: wcid {wcid} carries WorldEventObjective - objective spawns (the Rift) are never catalog members - dropped");
                        seenWcids[wcid] = table.Id;
                        continue;
                    }

                    if (weenie.GetProperty(PropertyBool.Attackable) != true)
                    {
                        diagnostics.Add($"species/{table.Id}: wcid {wcid} is not Attackable - dropped");
                        seenWcids[wcid] = table.Id;
                        continue;
                    }

                    if (weenie.GetProperty(PropertyDataId.LinkedPortalOne) != null || weenie.GetProperty(PropertyDataId.LinkedPortalTwo) != null)
                    {
                        diagnostics.Add($"species/{table.Id}: wcid {wcid} summons a portal on death (LinkedPortalOne/Two, species rule R9) - dropped");
                        seenWcids[wcid] = table.Id;
                        continue;
                    }

                    var level = weenie.GetProperty(PropertyInt.Level);

                    if (level == null)
                    {
                        diagnostics.Add($"species/{table.Id}: wcid {wcid} is missing Level - dropped");
                        seenWcids[wcid] = table.Id;
                        continue;
                    }

                    seenWcids[wcid] = table.Id;

                    if (!grouped.TryGetValue(table.Id, out var members))
                    {
                        members = new List<FamilyMember>();
                        grouped[table.Id] = members;
                    }

                    members.Add(new FamilyMember
                    {
                        Wcid = wcid,
                        Name = weenie.GetName() ?? weenie.ClassName,
                        Level = level.Value,
                        Role = member.Role,
                        FromTable = true,
                        Caster = IsCaster(weenie),
                        FamilyId = table.Id
                    });
                }
            }
        }
    }
}
