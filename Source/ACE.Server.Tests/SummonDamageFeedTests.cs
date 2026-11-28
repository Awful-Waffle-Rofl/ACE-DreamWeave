using System;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The summon damage feed - the "/summondamage on|off" per-character chat toggle that reports each hit
    /// a player's OWN summoned pets land (Pet.NotifyOwnerOfDamage).
    ///
    /// SCOPE NOTE - why this file does not call Pet.NotifyOwnerOfDamage itself. No test in this project
    /// constructs a live Player; both Player constructors call DatabaseManager.Authentication.GetAccountById
    /// unconditionally, and the method's whole body is guards over Player state plus an EnqueueSend on a
    /// live Session. MuleCommerceTests.cs's class remarks record that constraint at length, including why
    /// FormatterServices.GetUninitializedObject was evaluated and rejected. This file follows the same
    /// convention those tests established: exercise the piece that was deliberately split out to be
    /// testable (Pet.FormatDamageMessage), and cover the rest structurally.
    ///
    /// The structural half is not decoration. A code review of the original commit found that the feed had
    /// silently missed a whole damage route - a pet's Harm / drain-health resolves with no projectile and
    /// writes the vital inside WorldObject_Magic, reaching neither of the two hooks that were wired. The
    /// call-site test below is aimed squarely at that failure mode: it fails if any of the three routes
    /// loses its hook to a future refactor, which is the one regression here that no behavioral test in
    /// this project could catch.
    /// </summary>
    [TestClass]
    public class SummonDamageFeedTests
    {
        /// <summary>
        /// Each entry is a source file that writes pet damage to a creature's health, and so must call
        /// Pet.NotifyOwnerOfDamage. Hooks is how many separate call sites that file carries - a count rather
        /// than a presence check, because WorldObject_Magic holds two of them in two different methods and a
        /// presence check would go green with either one deleted.
        /// </summary>
        private static readonly (string RelativePath, int Hooks, string Route)[] HookedDamageRoutes =
        {
            // melee (via Monster_Melee) and missile (via ProjectileCollisionHelper) both land in the shared
            // Creature.TakeDamage sink, so one hook covers both
            ("Source/ACE.Server/WorldObjects/Monster_Combat.cs", 1, "melee and missile, via Creature.TakeDamage"),

            // spell projectiles write the vital directly and never reach Creature.TakeDamage
            ("Source/ACE.Server/WorldObjects/SpellProjectile.cs", 1, "spell projectiles"),

            // two sites: HandleCastSpell_Boost's Health case is a Harm, HandleCastSpell_Transfer's is a Drain
            // Health. Both resolve with no projectile, so neither reaches either hook above, and a Drain is a
            // Transfer rather than a Boost so the Harm hook does not cover it
            ("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs", 2, "life magic Harm and Drain Health"),
        };

        [TestMethod]
        public void FormatDamageMessage_MatchesTheSpecifiedShape()
        {
            // the pet's Name already carries the owner's, because Pet.Init prefixes it at summon time
            var message = Pet.FormatDamageMessage("Bob's Anger Wisp", "Drudge Skulker", 42, false);

            Assert.AreEqual("Bob's Anger Wisp hits Drudge Skulker for 42 damage!", message);
        }

        [TestMethod]
        public void FormatDamageMessage_PrefixesACriticalHit()
        {
            Assert.AreEqual("Critical hit! Bob's Anger Wisp hits Drudge Skulker for 84 damage!",
                Pet.FormatDamageMessage("Bob's Anger Wisp", "Drudge Skulker", 84, true));
        }

        /// <summary>
        /// Every route hands the hit's real crit flag to the feed. Melee, missile and multi-shot reach it
        /// through Creature.TakeDamage's crit argument, which those pet strike sites used to hardcode to false;
        /// the four NotifyOwnerOfDamage calls then forward their own crit expression. A count of calls
        /// (EveryPetDamageRoute_CallsNotifyOwnerOfDamage) cannot catch a site quietly reverting to false, so
        /// this pins the argument text itself, comments stripped so a commented-out line cannot satisfy it.
        /// </summary>
        [TestMethod]
        public void EveryPetDamageRoute_PassesTheRealCritFlag()
        {
            var sites = new (string RelativePath, string Call)[]
            {
                ("Source/ACE.Server/WorldObjects/Monster_Melee.cs", "target.TakeDamage(this, damageEvent.DamageType, damageEvent.Damage, damageEvent.IsCritical, IncomingDamageOrigin.DirectHit);"),
                ("Source/ACE.Server/WorldObjects/ProjectileCollisionHelper.cs", "targetCreature.TakeDamage(sourceCreature, damageEvent.DamageType, damageEvent.Damage, damageEvent.IsCritical, IncomingDamageOrigin.DirectHit);"),
                ("Source/ACE.Server/WorldObjects/Creature_Missile.cs", "target.TakeDamage(this, damageEvent.DamageType, damageEvent.Damage, damageEvent.IsCritical, IncomingDamageOrigin.DirectHit);"),
                ("Source/ACE.Server/WorldObjects/Monster_Combat.cs", "damagingPet.NotifyOwnerOfDamage(this, damage, crit);"),
                ("Source/ACE.Server/WorldObjects/SpellProjectile.cs", "castingPet.NotifyOwnerOfDamage(target, (int)amount, critical);"),
                ("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs", "damagingPet.NotifyOwnerOfDamage(targetCreature, -boost, lifeCritMultiplier > 1.0f);"),
                ("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs", "drainingPet.NotifyOwnerOfDamage(transferSource, (int)srcVitalChange, drainCritCapMod > 1.0f);"),
            };

            var repoRoot = FindRepoRoot();

            foreach (var (relativePath, call) in sites)
            {
                var source = StripComments(File.ReadAllText(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))));

                StringAssert.Contains(source, call,
                    $"{relativePath} no longer carries `{call}`, so /summondamage may stop marking that route's critical hits.");
            }
        }

        /// <summary>Removes // line comments and /* */ block comments, so a commented-out call cannot match.</summary>
        private static string StripComments(string source)
        {
            var noBlock = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(noBlock, @"//[^\n]*", "");
        }

        [TestMethod]
        public void FormatDamageMessage_DoesNotGroupThousands()
        {
            // a raw integer, not "1,250" - the client's combat lines do not group either, and a separator
            // here would read oddly beside them
            Assert.AreEqual("Pet hits Target for 1250 damage!", Pet.FormatDamageMessage("Pet", "Target", 1250, false));
        }

        /// <summary>
        /// The guard for the coverage bug this feature actually shipped with. If a refactor moves or drops
        /// the hook on any one of the three routes, the feed goes quietly partial - the player simply sees
        /// fewer lines than they should, with nothing to indicate a route stopped reporting.
        /// </summary>
        [TestMethod]
        public void EveryPetDamageRoute_CallsNotifyOwnerOfDamage()
        {
            var repoRoot = FindRepoRoot();

            foreach (var (relativePath, expectedHooks, route) in HookedDamageRoutes)
            {
                var fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

                Assert.IsTrue(File.Exists(fullPath), $"{relativePath} not found at {fullPath}.");

                var source = File.ReadAllText(fullPath);

                var hooks = source.Split(new[] { "NotifyOwnerOfDamage(" }, StringSplitOptions.None).Length - 1;

                Assert.AreEqual(expectedHooks, hooks,
                    $"{relativePath} carries {hooks} call(s) to Pet.NotifyOwnerOfDamage, expected "
                    + $"{expectedHooks}. This file owns the summon damage feed's {route} route. If a call "
                    + "went missing the feed has gone quietly partial - the player just sees fewer lines. "
                    + "Either restore it, or, if the damage genuinely moved, hook it at its new site and "
                    + "update this list.");
            }
        }

        [TestMethod]
        public void SummonDamageMessages_IsTheRegisteredPropertyId()
        {
            // the id is reserved in Source/property-registry.tsv; PropertyRegistryTests is the collision gate
            Assert.AreEqual(9054, (int)PropertyBool.SummonDamageMessages);
        }

        [TestMethod]
        public void SummonDamageCommand_IsRegisteredForOrdinaryPlayers()
        {
            var handler = typeof(PlayerCommands)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                // GetCustomAttributes (plural) because a handler may carry several, one per alias, and the
                // singular overload throws AmbiguousMatchException on those. CommandManager reads them
                // the same way, so this walks exactly what the server registers.
                .SelectMany(m => m.GetCustomAttributes<CommandHandlerAttribute>(), (m, a) => new { Method = m, Attribute = a })
                .SingleOrDefault(x => string.Equals(x.Attribute.Command, "summondamage", StringComparison.OrdinalIgnoreCase));

            Assert.IsNotNull(handler, "No /summondamage command handler is declared on PlayerCommands.");

            Assert.AreEqual(AccessLevel.Player, handler.Attribute.Access,
                "/summondamage is a player-facing chat preference and must not require elevated access.");

            Assert.IsTrue(handler.Attribute.Flags.HasFlag(CommandHandlerFlag.RequiresWorld),
                "/summondamage reads and writes character state, so it needs a player in the world.");

            Assert.AreEqual(0, handler.Attribute.ParameterCount,
                "/summondamage with no argument reports the current setting, so it must not require one.");
        }

        /// <summary>
        /// Walks up from the test assembly to the repo root. Same approach as PropertyRegistryTests - and the
        /// same reason never to run these with --artifacts-path, which moves the assembly out of the tree and
        /// breaks the walk.
        /// </summary>
        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Source", "property-registry.tsv")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new AssertFailedException(
                $"Could not find the repo root by walking up from {AppContext.BaseDirectory}.");
        }
    }
}
