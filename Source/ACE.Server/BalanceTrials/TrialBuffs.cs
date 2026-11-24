using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

namespace ACE.Server.BalanceTrials
{
    /// <summary>What the own-spellbook pick needs to know about one known spell, so the pick itself is pure.</summary>
    public readonly record struct TrialSpellFacts(uint Id, SpellCategory Category, uint Level, uint Power, bool Beneficial, bool Enchantment, bool SelfTargeted, bool ItemRedirect, bool Fellowship)
    {
        public static TrialSpellFacts Of(Spell s) => new TrialSpellFacts(
            s.Id, s.Category, s.Level, s.Power, s.IsBeneficial, s.MetaSpellType == SpellType.Enchantment, s.IsSelfTargeted,
            TrialBuffs.IsItemRedirectTarget(s.NonComponentTargetType), s.IsFellowshipSpell);
    }

    /// <summary>
    /// The archetype buff profile, applied at rep setup (Docs/BalanceTrials/DESIGN.md 6, rebuild step 3).
    ///
    /// "adminAll:&lt;n&gt;": the Sentinel buff set at level n, on the rep itself (Player.CreateSentinelBuffPlayers, which needs the
    /// rep's own access to be Sentinel or above; trial accounts are Developer).
    ///
    /// "ownSpellbook" (the default): every beneficial self or wielded-item enchantment the character itself knows, the
    /// highest-level one per spell category, cast at no mana cost through EnchantmentManager.Add plus HandleSpellHooks, the
    /// path @run uses (SentinelCommands.HandleRun). Item spells land on the equipped items whose ItemType the spell targets,
    /// the selection WorldObject.GetNonComponentTargetTypes makes for a redirected cast.
    /// </summary>
    public static class TrialBuffs
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string OwnSpellbook = "ownSpellbook";
        public const string AdminAllPrefix = "adminAll:";

        /// <summary>
        /// The profile rule Apply enforces, for the loaders: exactly ownSpellbook, or adminAll:1..8. Null or empty is not a
        /// valid explicit profile (Apply alone reads it as ownSpellbook, for an archetype built in code).
        /// </summary>
        public static bool IsValidProfile(string profile)
            => profile == OwnSpellbook || TryParseAdminAll(profile, out _);

        /// <summary>adminAll:&lt;level&gt; with level 1..8.</summary>
        public static bool TryParseAdminAll(string profile, out int level)
        {
            level = 0;
            return profile != null && profile.StartsWith(AdminAllPrefix, StringComparison.Ordinal)
                && int.TryParse(profile.AsSpan(AdminAllPrefix.Length), out level)
                && level >= 1 && level <= 8;
        }

        /// <summary>
        /// The profile a rep is set up with: the scenario's buffProfile when it names one, else the archetype's. A scenario
        /// override needs no master reseed, because masters are stamped with the archetype file's sha, not the scenario's.
        /// </summary>
        public static string EffectiveProfile(TrialScenario scenario, TrialArchetype archetype)
            => scenario?.BuffProfile ?? archetype?.BuffProfile;

        /// <summary>Applies <paramref name="profile"/> to <paramref name="p"/>. Returns the number of spells cast (adminAll counts its level). Room thread.</summary>
        public static int Apply(Player p, string profile)
        {
            if (p == null)
                return 0;

            profile = string.IsNullOrEmpty(profile) ? OwnSpellbook : profile;

            if (profile.StartsWith(AdminAllPrefix, StringComparison.Ordinal))
            {
                if (!TryParseAdminAll(profile, out var level))
                    throw new ArgumentException($"buff profile '{profile}' must be adminAll:1..8");

                CheckAdminAll(p, profile, level);
                p.CreateSentinelBuffPlayers(new[] { p }, true, (ulong)level);
                return level;
            }

            if (profile != OwnSpellbook)
                throw new ArgumentException($"buff profile '{profile}' must be {OwnSpellbook} or {AdminAllPrefix}<level>");

            var spells = new Dictionary<uint, Spell>();
            foreach (var id in p.Biota.GetKnownSpellsIds(p.BiotaDatabaseLock))
            {
                var spell = new Spell((uint)id);
                if (!spell.NotFound)
                    spells[spell.Id] = spell;
            }

            var cast = 0;

            foreach (var id in SelectOwnSpellbook(spells.Values.Select(TrialSpellFacts.Of)))
            {
                var spell = spells[id];

                foreach (var target in Targets(p, spell))
                {
                    var result = target.EnchantmentManager.Add(spell, p, null);

                    if (target is Player targetPlayer && result?.Enchantment != null)
                        targetPlayer.Session?.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(targetPlayer.Session, new Enchantment(targetPlayer, result.Enchantment)));
                }

                p.HandleSpellHooks(spell);
                cast++;
            }

            log.Debug($"[TRIAL] {p.Name} (0x{p.Guid.Full:X8}): ownSpellbook cast {cast} spell(s)");

            return cast;
        }

        /// <summary>Whether the level 8 spells are in the world database (the check CreateSentinelBuffPlayers makes). A test seam.</summary>
        internal static Func<bool> Level8Installed = () => DatabaseManager.World.GetCachedSpell((uint)SpellId.ArmorOther8) != null;

        /// <summary>Where CheckAdminAll's warnings go. A test seam.</summary>
        internal static Action<string> Warn = message => log.Warn(message);

        /// <summary>
        /// The adminAll preconditions, checked before CreateSentinelBuffPlayers, which returns WITHOUT an error and without
        /// casting anything when the caster's session is below Sentinel (Player_Spells.cs, its first line), and reads
        /// Session unguarded. Either would run the rep unbuffed or fail with a bare NullReferenceException, so both
        /// throw here with the reason: the throw refuses the rep (TrialStarter.PrepareRep turns a setup throw into a
        /// refusal and the session stays Ready), which is what a rep that cannot get its profile should do.
        ///
        /// Level 8 not installed is a warning, not a refusal: CreateSentinelBuffPlayers then casts the level 7 set, and it
        /// does so silently, so it is logged here, loudly, on every rep.
        /// </summary>
        internal static void CheckAdminAll(Player p, string profile, int level)
        {
            var refusal = AdminAllRefusal(p, profile);
            if (refusal != null)
                throw new InvalidOperationException(refusal);

            if (level == 8 && !Level8Installed())
                Warn($"[TRIAL] buff profile '{profile}' on 0x{p.Guid.Full:X8}: the level 8 spells are not installed, so CreateSentinelBuffPlayers casts the level 7 set instead");
        }

        /// <summary>Why <paramref name="p"/> cannot take an adminAll profile (no session, or below Sentinel), or null when it can.</summary>
        internal static string AdminAllRefusal(Player p, string profile)
        {
            var session = p.Session;

            if (session == null)
                return $"buff profile '{profile}' needs the rep (0x{p.Guid.Full:X8}) online with a session; nothing was cast";

            if (session.AccessLevel < AccessLevel.Sentinel)
                return $"buff profile '{profile}' needs the rep's account at Sentinel access or above (it is {session.AccessLevel}): CreateSentinelBuffPlayers would cast nothing";

            return null;
        }

        /// <summary>
        /// The refusal a rep gets BEFORE anything changes it (TrialStarter.TryStart and PreSpawn), or null: an invalid
        /// profile, or an adminAll profile the rep cannot take. Apply keeps the same checks as a backstop. Reads only.
        /// </summary>
        public static string Precheck(Player p, string profile)
        {
            if (string.IsNullOrEmpty(profile) || profile == OwnSpellbook)
                return null;

            if (!TryParseAdminAll(profile, out _))
                return $"buff profile '{profile}' must be {OwnSpellbook} or {AdminAllPrefix}1..8";

            return p == null ? "no rep character" : AdminAllRefusal(p, profile);
        }

        /// <summary>
        /// The own-spellbook pick: beneficial, an enchantment, not a fellowship spell, and either self-targeted or an item
        /// spell that lands on wielded gear. One per category: the highest level, then the highest power, then a
        /// self-targeted one, then the lowest id.
        /// </summary>
        public static IReadOnlyList<uint> SelectOwnSpellbook(IEnumerable<TrialSpellFacts> known)
            => known
                .Where(s => s.Beneficial && s.Enchantment && !s.Fellowship && (s.SelfTargeted || s.ItemRedirect))
                .GroupBy(s => s.Category)
                .Select(g => g.OrderByDescending(s => s.Level).ThenByDescending(s => s.Power).ThenByDescending(s => s.SelfTargeted).ThenBy(s => s.Id).First().Id)
                .OrderBy(id => id)
                .ToList();

        /// <summary>The NonComponentTargetType values a cast redirects onto equipped items (WorldObject.GetNonComponentTargetTypes).</summary>
        internal static bool IsItemRedirectTarget(ItemType t)
        {
            switch (t)
            {
                case ItemType.Vestements:
                case ItemType.Weapon:
                case ItemType.LockableMagicTarget:
                case ItemType.Caster:
                case ItemType.WeaponOrCaster:
                case ItemType.Item:
                    return true;
                default:
                    return false;
            }
        }

        private static IEnumerable<WorldObject> Targets(Player p, Spell spell)
        {
            if (IsItemRedirectTarget(spell.NonComponentTargetType))
                return p.EquippedObjects.Values.Where(i => (i.ItemType & spell.NonComponentTargetType) != 0 && (i.ValidLocations & EquipMask.Selectable) != 0 && i.IsEnchantable).ToList();

            return new WorldObject[] { p };
        }
    }
}
