using System;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Hold;

using static ACE.Server.Tests.Avatars.Gauntlet.GauntletFighterFixtures;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// A member's healing kit on the Avatar's fighter (plan Task 14, the owner's "allow heals and buffs" ruling for kits):
    /// the decision (health kits only, only inside a live copy), the conversion and the ten-second bucket on the fighter's own
    /// state, and a mutation-proved source pin on the one branch in Healer.HandleActionUseOnTarget. The Hold knight path is
    /// the model (WorldEventHooks.IsHoldKnightHealTarget, Creature.ApplyHoldKnightHeal). No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class GauntletKitTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => EnsureInitialized();

        private sealed class Owner : IGauntletFighterOwner
        {
            public bool Live = true;
            public bool IsLiveCopy => Live;
            public void OnFighterDied(GauntletAvatarFighter fighter) { }
        }

        private static GauntletAvatarFighter LiveFighter(uint max, uint current, Owner owner = null)
        {
            var fighter = Fighter(health: max);
            fighter.Owner = owner ?? new Owner();
            fighter.ApplyStamp(new GauntletFighterStamp { Level = 200, MaxHealth = max, MinDeathSeconds = 20, BurstSeconds = 5 }, fullHealth: true);
            Assert.AreEqual(max, fighter.Health.MaxValue, "fixture: the stamp set max health");
            fighter.Health.Current = current;
            return fighter;
        }

        // ---- the decision ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Fighter_AcceptsHealthKit_InLiveCopy()
        {
            var live = LiveFighter(1000, 500);

            Assert.IsTrue(GauntletAvatarFighter.IsFighterHealTarget(live), "Healer routes a fighter to its own kit path");
            Assert.IsFalse(GauntletAvatarFighter.IsFighterHealTarget(Monster()), "any other target is decided as before");
            Assert.IsFalse(GauntletAvatarFighter.IsFighterHealTarget(null));

            Assert.IsTrue(live.AcceptsHealthKit(PropertyAttribute2nd.Health));
            Assert.IsFalse(live.AcceptsHealthKit(PropertyAttribute2nd.Stamina), "a stamina kit is refused");
            Assert.IsFalse(live.AcceptsHealthKit(PropertyAttribute2nd.Mana), "a mana kit is refused");

            Assert.IsTrue(GauntletAvatarFighter.AcceptsKit(PropertyAttribute2nd.Health, acceptsBeneficial: true));
            Assert.IsFalse(GauntletAvatarFighter.AcceptsKit(PropertyAttribute2nd.Health, acceptsBeneficial: false));
        }

        [TestMethod]
        public void Fighter_RefusesKit_OutsideCopy()
        {
            var unbound = Fighter();
            Assert.IsFalse(unbound.AcceptsHealthKit(PropertyAttribute2nd.Health), "no run, no kit");

            var owner = new Owner();
            var fighter = LiveFighter(1000, 500, owner);
            owner.Live = false;
            Assert.IsFalse(fighter.AcceptsHealthKit(PropertyAttribute2nd.Health), "a run that left its live states refuses");

            var healed = fighter.ApplyKitHeal(100, 1000.0, out var outcome);
            Assert.AreEqual(0, healed);
            Assert.AreEqual(HoldHealOutcome.Refused, outcome);
            Assert.AreEqual(500u, fighter.Health.Current, "the health is untouched");
        }

        // ---- spell heals (playability review P13) ---------------------------------------------------------------------

        [TestMethod]
        public void SpellHeal_TakesTheKitConversionAndBucket()
        {
            var fighter = LiveFighter(1000, 100);
            var queued = 0;

            try
            {
                // Run the queued heal at once, as the fighter's landblock would.
                GauntletAvatarFighter.QueueHeal = (f, raw, reply) =>
                {
                    queued++;
                    var healed = f.ApplyKitHeal(raw, 1000.0, out var outcome);
                    reply(healed, outcome);
                    return true;
                };

                Assert.IsTrue(GauntletAvatarFighter.TryQueueSpellHeal(null, fighter, WorldEventHooks.TypicalPlayerHeal), "a fighter takes the spell heal");
                Assert.AreEqual(140u, fighter.Health.Current, "converted as a kit is: 4% of max, not the raw 100");

                // The bucket is the kits' bucket: 25% of max per ten seconds, 40 used, so a huge spell gets the 210 left.
                Assert.IsTrue(GauntletAvatarFighter.TryQueueSpellHeal(null, fighter, 1_000_000));
                Assert.AreEqual(350u, fighter.Health.Current, "a big spell is held by the shared ten-second bucket");
                Assert.AreEqual(0, fighter.ApplyKitHeal(WorldEventHooks.TypicalPlayerHeal, 1000.0, out var kit), "and a kit after it finds the bucket spent");
                Assert.AreEqual(HoldHealOutcome.BudgetSpent, kit);

                Assert.IsFalse(GauntletAvatarFighter.TryQueueSpellHeal(null, Monster(), 100), "any other target writes the heal as before");
                Assert.AreEqual(2, queued);
            }
            finally
            {
                GauntletAvatarFighter.QueueHeal = (f, raw, reply) => f.QueueKitHeal(raw, reply);
            }
        }

        [TestMethod]
        public void SpellHeal_BoostSite_SkipsTheDirectWrite_MutationProved()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs").Replace("\r\n", "\n");

            const string queue = "var gauntletHealQueued = !holdHealQueued && spell.VitalDamageType == DamageType.Health && tryBoost > 0 && player != null\n                && ACE.Server.Avatars.Gauntlet.GauntletHooks.TryQueueFighterSpellHeal(player, targetCreature, tryBoost);";
            const string skip = "                default:   // Health\n                    if (holdHealQueued || gauntletHealQueued)\n                    {\n                        boost = 0;";
            const string line = "if (player != null && !holdHealQueued && !gauntletHealQueued)";

            static string Fault(string s, string q, string k, string l)
            {
                var qi = s.IndexOf(q, StringComparison.Ordinal);
                var ki = s.IndexOf(k, StringComparison.Ordinal);
                if (qi < 0) return "no spell-heal queue";
                if (ki < 0) return "the Health write is not skipped";
                if (ki < qi) return "the skip comes before the queue";
                if (s.IndexOf(l, ki, StringComparison.Ordinal) < 0) return "the caster line is not skipped";
                return null;
            }

            Assert.IsNull(Fault(src, queue, skip, line), "the real source passes");
            Assert.IsNotNull(Fault(src.Replace(queue, "var gauntletHealQueued = false;"), queue, skip, line), "no queue faults");
            Assert.IsNotNull(Fault(src.Replace(skip, skip.Replace("holdHealQueued || gauntletHealQueued", "holdHealQueued")), queue, skip, line), "a direct write faults");
            Assert.IsNotNull(Fault(src.Replace(line, "if (player != null && !holdHealQueued)"), queue, skip, line), "a second caster line faults");
        }

        // ---- the heal -------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ApplyKitHeal_ConvertsToAShareOfMaxHealth_AndWritesIt()
        {
            var fighter = LiveFighter(1000, 100);

            // a typical 100-point kit restores HealShareOfTypicalHeal (4%) of max: 40 of 1000
            var healed = fighter.ApplyKitHeal(WorldEventHooks.TypicalPlayerHeal, 1000.0, out var outcome);

            Assert.AreEqual(40, healed);
            Assert.AreEqual(HoldHealOutcome.Healed, outcome);
            Assert.AreEqual(140u, fighter.Health.Current, "the vital got the converted amount");

            // a bigger kit scales with the raw heal
            Assert.AreEqual(80, fighter.ApplyKitHeal(2 * WorldEventHooks.TypicalPlayerHeal, 1000.0, out _));
            Assert.AreEqual(220u, fighter.Health.Current);
        }

        [TestMethod]
        public void ApplyKitHeal_NeverPastMax_AndFullIsRefused()
        {
            var nearly = LiveFighter(1000, 990);
            Assert.AreEqual(10, nearly.ApplyKitHeal(100_000, 1000.0, out var outcome), "clamped to the missing health");
            Assert.AreEqual(1000u, nearly.Health.Current);
            Assert.AreEqual(HoldHealOutcome.Healed, outcome);

            var full = LiveFighter(1000, 1000);
            Assert.AreEqual(0, full.ApplyKitHeal(100, 1000.0, out outcome));
            Assert.AreEqual(HoldHealOutcome.Full, outcome);
        }

        [TestMethod]
        public void ApplyKitHeal_BucketCapsAFlurryOfKits_ThenRefills()
        {
            // max 1000: the bucket holds 25% = 250 and refills 25 per second
            var fighter = LiveFighter(1000, 1);
            var total = 0;

            for (var i = 0; i < 20; i++)
                total += fighter.ApplyKitHeal(1000, 1000.0, out _); // 400 wanted each, in the same instant

            Assert.AreEqual((int)(1000 * GauntletAvatarFighter.KitHealMaxSharePerTenSeconds), total, "a crowd of kits in one instant heals the bucket and no more");

            var dry = fighter.ApplyKitHeal(1000, 1000.0, out var outcome);
            Assert.AreEqual(0, dry);
            Assert.AreEqual(HoldHealOutcome.BudgetSpent, outcome);

            Assert.AreEqual(25, fighter.ApplyKitHeal(1000, 1001.0, out _), "one second later, one second of refill");
        }

        // ---- the Healer branch (source pin, mutation-proved) ------------------------------------------------------------

        private const string HealerPath = "Source/ACE.Server/WorldObjects/Healer.cs";
        private const string UseSig = "public override void HandleActionUseOnTarget(Player healer, WorldObject target)";
        private const string KnightHead = "if (ACE.Server.WorldEvents.WorldEventHooks.IsHoldKnightHealTarget(target))";
        private const string FighterHead = "if (ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter.IsFighterHealTarget(target))";
        private const string FighterCall = "HealGauntletFighter(healer, (ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter)target);";
        private const string PlayerHead = "if (!(target is Player targetPlayer) || targetPlayer.Teleporting)";

        private static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

        /// <summary>The exact fighter if-head, once, returning through the fighter kit path, after the knight's head and ahead of the player-only refusal.</summary>
        internal static string HealerFault(string source)
        {
            var body = PooledLootSourceText.MethodBody(source, UseSig);

            if (Count(body, FighterHead) != 1)
                return "the fighter if-head is not in HandleActionUseOnTarget exactly once";

            var at = body.IndexOf(FighterHead, StringComparison.Ordinal);
            var knight = body.IndexOf(KnightHead, StringComparison.Ordinal);
            var player = body.IndexOf(PlayerHead, StringComparison.Ordinal);

            if (knight < 0 || player < 0 || at < knight || at > player)
                return "the fighter branch is not between the knight branch and the player-only refusal";

            var block = body.Substring(at, player - at);

            if (Count(block, FighterCall) != 1 || !Regex.IsMatch(block, Regex.Escape(FighterCall) + @"\s*\n\s*return;"))
                return "the fighter branch does not hand the target to the fighter kit path and return";

            return null;
        }

        [TestMethod]
        public void HealerBranch_Site_IsPinned_AndEveryMutationFaults()
        {
            var source = PooledLootSourceText.Read(HealerPath);
            Assert.IsNull(HealerFault(source), "the real source must satisfy the pin");

            Assert.IsNotNull(HealerFault(source.Replace(FighterHead, "if (false)")), "head removed");
            Assert.IsNotNull(HealerFault(source.Replace(FighterHead, "if (ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter.IsFighterHealTarget(healer))")), "wrong argument");
            Assert.IsNotNull(HealerFault(source.Replace(FighterCall + "\n                return;", FighterCall)), "no return after the call");

            // moved ahead of the knight branch
            var moved = source.Replace(FighterHead, "if (false)");
            moved = moved.Replace(KnightHead, FighterHead + "\n            {\n                HealGauntletFighter(healer, (ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter)target);\n                return;\n            }\n\n            " + KnightHead);
            Assert.IsNotNull(HealerFault(moved), "ahead of the knight's branch");
        }
    }
}
