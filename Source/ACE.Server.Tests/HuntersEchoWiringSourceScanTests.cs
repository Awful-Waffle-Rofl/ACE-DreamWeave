using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SOURCE SCANS for Hunter's Echo (Archer T3), named so. The Echo's live paths need a Player and a landblock,
    /// neither of which the test host can build, so these pin the structural facts its safety rests on as text:
    /// its arrows never roll the owner's item procs or spend ammo, every arrow refuses a player and checks the
    /// owner's landblock group, nothing on the Echo path saves a biota or runs Pet.Init, the cloak absorb happens
    /// BEFORE the health write at both sites it covers, and the Echo bypasses Soul Tether. Each assertion names the
    /// exact text it reads; a mutation proof for each lives in the PR that added them.
    ///
    /// A scan matches TEXT, so a call routed through an alias would slip past it; the failure being guarded against
    /// is an edit that moves or drops one of these lines, which is textual.
    /// </summary>
    [TestClass]
    public class HuntersEchoWiringSourceScanTests
    {
        private static string ReadServerFile(string relative)
        {
            var root = ClassAbilityAffinityDeclarationTests.RepoRoot();
            var path = Path.Combine(root, "Source", "ACE.Server", relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(path), $"{relative} does not exist under Source/ACE.Server.");

            // comments dropped so a comment that merely MENTIONS a call cannot satisfy or trip an assertion; line
            // endings normalised so the patterns below are CRLF-agnostic
            return Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty).Replace("\r\n", "\n");
        }

        /// <summary>The text of one method, signature to matching close brace.</summary>
        private static string MethodText(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"signature not found - has it changed? {signature}");

            var open = source.IndexOf('{', start + signature.Length);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }

        private static void AssertBefore(string text, string first, string second, string message)
        {
            var a = text.IndexOf(first, StringComparison.Ordinal);
            var b = text.IndexOf(second, StringComparison.Ordinal);

            Assert.IsTrue(a >= 0, $"[{first}] is missing. {message}");
            Assert.IsTrue(b >= 0, $"[{second}] is missing. {message}");
            Assert.IsTrue(a < b, $"[{first}] must come before [{second}]. {message}");
        }

        // ------------------------------------------------------------ the Echo's volley

        /// <summary>
        /// Monster_Missile.LaunchMissile: the Echo branch is taken AFTER the never-initiates shot gate and BEFORE the
        /// self-procs, and returns, so an Echo shot never reaches TryProcEquippedItems, LaunchProjectile's real arrow
        /// or UpdateAmmoAfterLaunch.
        /// </summary>
        [TestMethod]
        public void LaunchMissile_EchoBranch_ReturnsBeforeTheSelfProcsAndTheAmmo()
        {
            var body = MethodText(ReadServerFile("WorldObjects/Monster_Missile.cs"), "public void LaunchMissile()");

            var branch = Regex.Match(body,
                @"if \(this is CombatPet echoShooter && echoShooter\.IsHuntersEcho\)\s*\{(?<inner>.*?)\n\s*return;\s*\}",
                RegexOptions.Singleline);

            Assert.IsTrue(branch.Success, "the Echo branch [if (this is CombatPet echoShooter && echoShooter.IsHuntersEcho) { ... return; }] is missing or no longer returns");

            AssertBefore(body, "shooter.MayLooseAt(target)", "echoShooter.IsHuntersEcho", "the Echo keeps the never-initiates shot gate");
            AssertBefore(body, "echoShooter.IsHuntersEcho", "TryProcEquippedItems(this, this, true, weapon);", "an Echo shot must never roll the self-procs");

            var inner = branch.Groups["inner"].Value;

            StringAssert.Contains(inner, "echoShooter.FireHuntersEchoVolley(echoPrimary, projectileSpeed);");
            Assert.IsFalse(inner.Contains("TryProcEquippedItems"), "the Echo branch rolls an item proc");
            Assert.IsFalse(inner.Contains("UpdateAmmoAfterLaunch"), "the Echo branch spends ammo");
        }

        /// <summary>
        /// Nothing on the Echo's own file rolls an item proc, spends ammo, saves a biota or runs Pet.Init.
        /// </summary>
        [TestMethod]
        public void EchoFile_NeverProcsSpendsAmmoSavesOrRunsPetInit()
        {
            var text = ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs");

            Assert.IsFalse(text.Contains("TryProcEquippedItems"), "an Echo arrow must never roll the owner's item procs");
            Assert.IsFalse(text.Contains("UpdateAmmoAfterLaunch"), "an Echo volley must never spend ammo");
            Assert.IsFalse(text.Contains("SaveBiotaToDatabase"), "the Echo and its cloned gear are never persisted");
            Assert.IsFalse(Regex.IsMatch(text, @"(?<![A-Za-z_])Init\s*\("), "the Echo must never run Pet.Init (slots, device, Soul Tether)");
            Assert.IsFalse(text.Contains("TryCreateInInventory") || text.Contains("TryAddToInventory"), "cloned gear is wielded only, never placed in an inventory");
        }

        /// <summary>
        /// One Echo volley rolls Pinning Shot ONCE and passes that roll to every arrow, and adds the owner's class-ability
        /// arrows to the weapon's own (the Multishot rule a player volley follows).
        /// </summary>
        [TestMethod]
        public void FireHuntersEchoVolley_OnePinRoll_OwnerArrows()
        {
            var body = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "internal void FireHuntersEchoVolley(");

            Assert.AreEqual(1, Regex.Matches(body, @"\bRollClassAbilityVolleyPin\b").Count, "exactly one Pinning Shot roll per Echo volley");
            StringAssert.Contains(body, "var volleyPinRoll = owner.WithHuntersEchoWeapons(volleyWeapons, owner.RollClassAbilityVolleyPin);");
            StringAssert.Contains(body, "owner.WithHuntersEchoWeapons(volleyWeapons, () => { owner.AddClassAbilityMissileShots(shotMultipliers); return true; });");
            Assert.AreEqual(1, Regex.Matches(body, @"\bAddClassAbilityMissileShots\b").Count, "the owner's class-ability arrows are added once, inside the weapon scope");
            StringAssert.Contains(body, "var weapon = snapshot?.Weapon;");
            Assert.IsFalse(body.Contains("owner.GetEquippedMissileWeapon()"), "the volley fires the snapshot weapon, never the owner's current one");

            // Double Volley (owner ruling 2026-11-11): one roll per Echo volley under the weapon scope, then the owner's
            // re-fire shape (Player.LaunchMissile): the primary while in sight, then the captured extra-target spread, all
            // on the volley's one Pinning roll; nothing after it can roll again
            StringAssert.Contains(body, "var volleyExtraShots = shotMultipliers.Count > 0\n                ? FireMultiShotVolley(launcher, ammo, weapon, primary, projectileSpeed, shotMultipliers, PrimaryInSight, volleyPinRoll)\n                : null;");
            Assert.AreEqual(1, Regex.Matches(body, @"\bTryClassAbilityDoubleVolley\b").Count, "exactly one Double Volley roll per Echo volley");
            Assert.IsTrue(Regex.IsMatch(body,
                    @"if \(!owner\.WithHuntersEchoWeapons\(volleyWeapons, owner\.TryClassAbilityDoubleVolley\)\)\s*return;\s*" +
                    @"if \(PrimaryInSight\(\)\)\s*FireMultiShotArrow\(launcher, ammo, primary, projectileSpeed, 1\.0f, primaryTarget: true, volleyPinRoll: volleyPinRoll\);\s*" +
                    @"if \(volleyExtraShots == null\)\s*return;\s*" +
                    @"foreach \(var \(extraTarget, mult, isPrimary\) in volleyExtraShots\)\s*FireMultiShotArrow\(launcher, ammo, extraTarget, projectileSpeed, mult, primaryTarget: isPrimary, volleyPinRoll: volleyPinRoll\);\s*\}$"),
                "the Double Volley re-fire must be the method's tail, shaped as the owner's");
            AssertBefore(body, "AddClassAbilityMissileShots(", "TryClassAbilityDoubleVolley", "the re-fire adds no Multishot arrows of its own");
            AssertBefore(body, "volleyExtraShots = shotMultipliers.Count > 0", "TryClassAbilityDoubleVolley", "the spread is captured before the roll");

            // the owner's own re-fire, for the shape mirrored above (fails if Player.LaunchMissile changes it)
            var ownerLaunch = MethodText(ReadServerFile("WorldObjects/Player_Missile.cs"), "public void LaunchMissile(WorldObject target, int attackSequence, MotionStance stance, bool subsequent = false)");
            StringAssert.Contains(ownerLaunch, "if (cleaveVolley && TryClassAbilityDoubleVolley())");
            StringAssert.Contains(ownerLaunch, "FireMultiShotArrow(launcher, ammo, creature, projectileSpeed, 1.0f, primaryTarget: true, volleyPinRoll: volleyPinRoll);");
            StringAssert.Contains(ownerLaunch, "foreach (var (extraTarget, mult, isPrimary) in volleyExtraShots)");
            Assert.IsTrue(Regex.IsMatch(body, @"primary is Player"), "the volley refuses a player primary");
        }

        /// <summary>
        /// Each Echo arrow lands through the owner's DamageTarget, and only after refusing a player or pet target and
        /// confirming owner, Echo and target share one landblock group.
        /// </summary>
        [TestMethod]
        public void ArrowImpact_RefusesPlayersAndChecksTheOwnersLandblockGroup_BeforeDamageTarget()
        {
            var text = ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs");
            var body = MethodText(text, "private void ResolveHuntersEchoArrowImpact(");

            const string damage = "owner.DamageTargetAsHuntersEcho(target, cosmeticProjectile, damageMultiplier, primaryTarget, accuracyLevel, weapons);";

            AssertBefore(body, "target is Player", damage, "a player must be refused before the owner's DamageTarget");
            AssertBefore(body, "target is CombatPet", damage, "a pet must be refused before the owner's DamageTarget");
            AssertBefore(body, "owner.CurrentLandblock.CurrentLandblockGroup != target.CurrentLandblock.CurrentLandblockGroup", damage,
                "the owner's and the target's landblock groups must match before the owner's DamageTarget writes them");

            // review F1: the impact runs on the owner's queue and must not need the Echo to still be in the world
            var withoutOwnerAndTarget = Regex.Replace(body, @"(?<![\w.])(owner|target)\.CurrentLandblock(\.CurrentLandblockGroup)?(?!\w)", string.Empty);
            Assert.IsFalse(withoutOwnerAndTarget.Contains("CurrentLandblock"), "the impact must not read the Echo's own landblock (bare, this., or any other receiver)");

            // the override routes the Echo there and leaves every other pet on the Creature default
            var overrideBody = MethodText(text, "protected override void ResolveMultiShotArrowImpact(");
            AssertBefore(overrideBody, "if (IsHuntersEcho)", "base.ResolveMultiShotArrowImpact(", "only the Echo takes the owner's DamageTarget path");
        }

        // ------------------------------------------------------------ the Echo's think

        /// <summary>The Echo's tick branch runs before the !IsAwake return and before HandleFindTarget, and returns.</summary>
        [TestMethod]
        public void MonsterTick_EchoBranch_PrecedesTheAwakeCheckAndTargetSearch()
        {
            var body = MethodText(ReadServerFile("WorldObjects/Monster_Tick.cs"), "public void Monster_Tick(double currentUnixTime)");

            var branch = Regex.Match(body,
                @"if \(this is CombatPet huntersEcho && huntersEcho\.IsHuntersEcho\)\s*\{.*?huntersEcho\.TickHuntersEcho\(\);\s*return;\s*\}",
                RegexOptions.Singleline);

            Assert.IsTrue(branch.Success, "the Echo tick branch [if (this is CombatPet huntersEcho && huntersEcho.IsHuntersEcho) { ... huntersEcho.TickHuntersEcho(); return; }] is missing");

            AssertBefore(body, "huntersEcho.IsHuntersEcho", "if (!IsAwake)", "the Echo's expiry must run whatever its awake state");
            AssertBefore(body, "huntersEcho.IsHuntersEcho", "HandleFindTarget();", "the Echo never takes the monster target search");
        }

        // ------------------------------------------------------------ retarget fix (2026-11-10)

        /// <summary>
        /// The Echo never runs a physics turn and never waits on one: stale turn state is cleared (not waited on with
        /// UpdatePosition + return, the old stall), and it faces its target with FaceTargetDirectly instead of StartTurn.
        /// </summary>
        [TestMethod]
        public void TickHuntersEcho_FacesDirectly_AndNeverWaitsOnATurn()
        {
            var body = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "internal void TickHuntersEcho()");

            Assert.IsTrue(Regex.IsMatch(body, @"if \(IsTurning \|\| IsMoving\)\s*ClearHuntersEchoMovement\(\);"),
                "stale turn state must be cleared by [if (IsTurning || IsMoving) ClearHuntersEchoMovement();]");
            Assert.IsTrue(Regex.IsMatch(body, @"if \(!IsFacing\(target\)\)\s*FaceTargetDirectly\(target\);"),
                "the Echo must face its target with [if (!IsFacing(target)) FaceTargetDirectly(target);]");

            Assert.IsFalse(Regex.IsMatch(body, @"\bStartTurn\s*\("), "the Echo must never start a physics turn");
            Assert.IsFalse(Regex.IsMatch(body, @"\b(TurnTo|MoveTo|TurnToObject|MoveToObject)\s*\("), "the Echo must never turn or move through the physics");
            Assert.IsFalse(Regex.IsMatch(body, @"UpdatePosition\(\);\s*return;"), "the Echo must never park its think waiting on a turn");

            var clear = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "private void ClearHuntersEchoMovement()");
            StringAssert.Contains(clear, "CancelPhysicsMoveTo();");
            Assert.IsFalse(Regex.IsMatch(clear, @"(?<!Physics)CancelMoveTo\("), "Creature.CancelMoveTo runs FindNextTarget and delays the next move");
        }

        /// <summary>
        /// The pick runs every tick, ahead of the bow test, so a dead target is replaced on the next tick; with a bow its
        /// range is the full radar (never below the bow), and its sight test is IsDirectVisible.
        /// </summary>
        [TestMethod]
        public void TickHuntersEcho_PicksEveryTick_AtFullRadar_WithLineOfSight()
        {
            var text = ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs");
            var body = MethodText(text, "internal void TickHuntersEcho()");

            StringAssert.Contains(body, "var engageRange = hasBow ? HuntersEchoEngageRange() : float.MaxValue;");
            AssertBefore(body, "var target = SelectHuntersEchoTarget(hasBow, engageRange);", "if (!hasBow)\n                return;",
                "the pick must run before the no-bow return");
            Assert.IsTrue(Regex.IsMatch(body, @"if \(CurrentAttack == null\)\s*\{\s*CurrentAttack = CombatType\.Missile;\s*MaxRange = engageRange;\s*\}"),
                "the Echo's attack range must be its engage range, not GetMaxRange");
            Assert.IsTrue(Regex.IsMatch(body, @"if \(AttackReady\(\) && HuntersEchoWeaponStillOwned\(owner\)\)\s*Attack\(\);"), "the Echo shoots as soon as AttackReady allows, while its snapshot weapon is still the owner's");

            Assert.IsTrue(Regex.IsMatch(MethodText(text, "private bool HuntersEchoWeaponStillOwned(Player owner)"),
                    @"if \(now < huntersEchoWeaponGoneUntil\)\s*return false;\s*if \(owner\.OwnsHuntersEchoItem\(huntersEchoWeapons\?\.Weapon\)\)\s*return true;\s*huntersEchoWeaponGoneUntil = now \+ HuntersEchoMath\.WeaponGoneRecheckSeconds;\s*return false;\s*\}$"),
                "R3: a failed ownership check is remembered for WeaponGoneRecheckSeconds, a pass is never cached");

            StringAssert.Contains(text, "private float HuntersEchoEngageRange() => HuntersEchoMath.EngageRange(HuntersEchoMath.RadarRange, GetMaxMissileRange());");
            StringAssert.Contains(ReadServerFile("ClassAbilities/HuntersEchoMath.cs"), "public const float RadarRange = Player.MaxRadarRange_Outdoors;",
                "owner ruling 2026-11-11: the outdoor radar range everywhere");

            var select = MethodText(text, "private Creature SelectHuntersEchoTarget(bool hasBow, float engageRange)");
            StringAssert.Contains(select, "HuntersEchoMath.SelectTarget(AttackTarget as Creature, PhysicsObj.ObjMaint.GetVisibleTargetsValuesOfTypeCreature(),");
            StringAssert.Contains(select, "creature => IsHuntersEchoTarget(creature, neverInitiates) && IsVisibleTarget(creature),");
            Assert.IsTrue(Regex.IsMatch(select, @"GetCylinderDistance,\s*engageRange,\s*hasBow \? \(Func<Creature, bool>\)\(creature => HuntersEchoSightTest\(creature, now\)\) : null,\s*HuntersEchoMath\.MaxSightTestsPerPick,\s*creature => huntersEchoPickGate\.SightRecentlyFailed\(creature\.Guid\.Full, now\)\);"),
                "with a bow every pick and every held target must pass the sight test within the engage range, capped per pick, skipping recent failures");

            var sight = MethodText(text, "private bool HuntersEchoSightTest(Creature creature, double now)");
            Assert.IsTrue(Regex.IsMatch(sight, @"\{\s*if \(IsDirectVisible\(creature\)\)\s*return true;\s*huntersEchoPickGate\.RecordSightFailure\(creature\.Guid\.Full, now\);\s*return false;\s*\}$"),
                "the sight test must be IsDirectVisible, and remember every failure");
        }

        // ------------------------------------------------------------ review round (2026-11-10, F1-F4)

        /// <summary>F2a: the bow test that switches range and sight on is the real equipped-weapon read.</summary>
        [TestMethod]
        public void TickHuntersEcho_HasBow_IsTheEquippedMissileWeaponRead()
        {
            var body = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "internal void TickHuntersEcho()");

            StringAssert.Contains(body, "var hasBow = GetEquippedMissileWeapon() != null;");
            Assert.AreEqual(1, Regex.Matches(body, @"\bhasBow\s*=").Count, "hasBow must be assigned exactly once");
        }

        /// <summary>F2b: the arrow speed is the ArrowSpeed of the real offset split, the whole of the method after its guard.</summary>
        [TestMethod]
        public void HuntersEchoArrowSpeed_IsArrowSpeedOfTheRealOffset()
        {
            var body = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "private float HuntersEchoArrowSpeed(Creature primary, float baseSpeed)");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"\{\s*if \(Location == null \|\| primary\.Location == null\)\s*return baseSpeed;\s*" +
                    @"var delta = Location\.Indoors == primary\.Location\.Indoors\s*\? primary\.Location\.ToGlobal\(\) - Location\.ToGlobal\(\)\s*: primary\.Location\.Pos - Location\.Pos;\s*" +
                    @"var \(horizontal, height\) = HuntersEchoMath\.HorizontalAndHeight\(delta\);\s*" +
                    @"return HuntersEchoMath\.ArrowSpeed\(baseSpeed, horizontal, height, Physics\.PhysicsGlobals\.Gravity, Physics\.PhysicsGlobals\.MaxVelocity\);\s*\}$"),
                "HuntersEchoArrowSpeed must be exactly: guard, target-minus-Echo offset, HorizontalAndHeight, ArrowSpeed");
        }

        /// <summary>F2c: ClearHuntersEchoMovement is exactly the unconditional cancel and the turn flag clear.</summary>
        [TestMethod]
        public void ClearHuntersEchoMovement_IsAnUnconditionalCancel()
        {
            var body = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "private void ClearHuntersEchoMovement()");

            Assert.IsTrue(Regex.IsMatch(body, @"^private void ClearHuntersEchoMovement\(\)\s*\{\s*CancelPhysicsMoveTo\(\);\s*IsTurning = false;\s*\}$"),
                "ClearHuntersEchoMovement must be exactly [CancelPhysicsMoveTo(); IsTurning = false;]");
        }

        /// <summary>
        /// F1: a multishot arrow's impact is queued on MultiShotImpactActor (the shooter, by default), and the Echo
        /// overrides it with its owner, so an Echo that ends mid-flight does not discard its arrows.
        /// </summary>
        [TestMethod]
        public void ArrowImpact_RunsOnTheOwnersQueue_ForTheEchoOnly()
        {
            var missile = ReadServerFile("WorldObjects/Creature_Missile.cs");
            var fire = MethodText(missile, "protected void FireMultiShotArrow(");

            StringAssert.Contains(fire, "impactChain.AddAction(MultiShotImpactActor, () => ResolveMultiShotArrowImpact(target, cosmeticProjectile, damageMultiplier, primaryTarget));");
            Assert.IsFalse(fire.Contains("impactChain.AddAction(this,"), "the impact must not be pinned to the shooter's own queue");
            StringAssert.Contains(missile, "protected virtual IActor MultiShotImpactActor => this;");

            StringAssert.Contains(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"),
                "protected override IActor MultiShotImpactActor => IsHuntersEcho && P_PetOwner != null ? P_PetOwner : this;");
        }

        /// <summary>F3: an Echo with no target re-picks only after the empty-pick backoff, and a refresh clears it.</summary>
        [TestMethod]
        public void TickHuntersEcho_EmptyPickBackoff_AndRefreshReset()
        {
            var text = ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs");
            var body = MethodText(text, "internal void TickHuntersEcho()");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"if \(!huntersEchoPickGate\.ShouldPick\(AttackTarget != null, Timers\.RunningTime\)\)\s*return;\s*var target = SelectHuntersEchoTarget\(hasBow, engageRange\);\s*huntersEchoPickGate\.OnPicked\(target != null, Timers\.RunningTime\);"),
                "the pick must be gated by the backoff and record its outcome");

            var refresh = MethodText(text, "internal int RefreshHuntersEcho(Player owner, double seconds)");
            Assert.IsTrue(Regex.IsMatch(refresh, @"\{\s*huntersEchoPickGate\.Reset\(\);"), "a refresh must clear the backoff first");
        }

        /// <summary>F4: the multishot extra-target cone uses GetMultiShotMaxRange, which only the Echo widens.</summary>
        [TestMethod]
        public void MultiShotRange_IsVirtual_AndOnlyTheEchoWidensIt()
        {
            var missile = ReadServerFile("WorldObjects/Creature_Missile.cs");
            var targets = MethodText(missile, "public List<Creature> GetMultiShotTargets(Creature target, WorldObject weapon, int totalTargets)");

            StringAssert.Contains(targets, "var maxRange = GetMultiShotMaxRange();");
            Assert.IsFalse(targets.Contains("GetMaxMissileRange()"), "the cone must read GetMultiShotMaxRange, not the bow range directly");
            StringAssert.Contains(missile, "protected virtual float GetMultiShotMaxRange() => GetMaxMissileRange();");

            StringAssert.Contains(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"),
                "protected override float GetMultiShotMaxRange() => IsHuntersEcho ? HuntersEchoEngageRange() : base.GetMultiShotMaxRange();");
        }

        /// <summary>The Echo's target rule still refuses players and pets, and requires engagement with the owner's side exactly when the pet is held to never-initiates (/summonaggro off).</summary>
        [TestMethod]
        public void IsHuntersEchoTarget_RefusesPlayersAndPets_AndRequiresEngagement()
        {
            var body = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "internal bool IsHuntersEchoTarget(Creature creature, bool neverInitiates)");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"if \(creature == null \|\| creature is Player \|\| creature is CombatPet \|\| creature\.IsDead \|\| ReferenceEquals\(creature, this\)\)\s*return false;"),
                "the Echo must refuse a player, a pet, a dead creature and itself");
            StringAssert.Contains(body, "return AcceptsPetTarget(creature, neverInitiates);");
        }

        /// <summary>Each volley's arrows are launched fast enough to reach a primary out at the full radar range.</summary>
        [TestMethod]
        public void FireHuntersEchoVolley_RaisesTheArrowSpeedToReachThePrimary()
        {
            var body = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "internal void FireHuntersEchoVolley(");

            AssertBefore(body, "projectileSpeed = HuntersEchoArrowSpeed(primary, projectileSpeed);", "FireMultiShotArrow(",
                "the arrow speed must be raised before the first arrow is fired");
        }

        // ------------------------------------------------------------ the cloak absorb

        /// <summary>
        /// Player.TakeDamage: the cloak spell proc roll and the Echo absorb sit BEFORE the health write, the roll gate is
        /// HuntersEchoMath.ShouldRollCloakProc, and the absorb zeroes the hit only on TryHuntersEchoAbsorb's TRUE.
        /// </summary>
        [TestMethod]
        public void PlayerTakeDamage_RollsAndAbsorbsBeforeTheHealthWrite()
        {
            var body = MethodText(ReadServerFile("WorldObjects/Player_Combat.cs"), "public int TakeDamage(WorldObject source, DamageType damageType, float _amount, BodyPart bodyPart");

            const string write = "var damageTaken = (uint)-UpdateVitalDelta(Health, (int)-amount);";

            AssertBefore(body, "HuntersEchoMath.ShouldRollCloakProc(Health.Current, amount, canReserveEchoAbsorb)", write, "the roll gate must run before the write");
            AssertBefore(body, "Cloak.RollSpellProc(this, equippedCloak, cloakProcPercent)", write, "the roll must run before the write");
            AssertBefore(body, "TryHuntersEchoAbsorb(source, damageType, amount, equippedCloak)", write, "the absorb must run before the write");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"if \(HuntersEchoMath\.ShouldAbsorb\(cloakSpellProcRolled, canReserveEchoAbsorb\) && amount > 0\s*&& TryHuntersEchoAbsorb\(source, damageType, amount, equippedCloak\)\)\s*\{\s*amount = 0;"),
                "the hit must be zeroed only inside the TryHuntersEchoAbsorb success branch");

            // the bad-body-part return skips CompleteSpellProc, so it must drop the absorb mark
            Assert.IsTrue(Regex.IsMatch(body, @"avoided crash for bad damage location[^\n]*\n\s*ClearHuntersEchoAbsorb\(\);\s*return 0;"),
                "the bad-body-part early return must clear the absorb mark");
        }

        /// <summary>SpellProjectile.DamageTarget: the same, against target.Health, and only for a player target.</summary>
        [TestMethod]
        public void SpellProjectile_RollsAndAbsorbsBeforeTheHealthWrite_PlayersOnly()
        {
            var body = MethodText(ReadServerFile("WorldObjects/SpellProjectile.cs"), "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)");

            const string write = "amount = (uint)-target.UpdateVitalDelta(target.Health, (int)-Math.Round(damage));";

            AssertBefore(body, "HuntersEchoMath.ShouldRollCloakProc(target.Health.Current, damage, targetPlayer == null ? target.P_HealthFloor : 0u, canReserveEchoAbsorb)", write, "the roll gate must run before the write");
            AssertBefore(body, "Cloak.RollSpellProc(target, equippedCloak, cloakProcPercent ?? percent)", write, "the roll must run before the write");
            AssertBefore(body, "targetPlayer.TryHuntersEchoAbsorb(", write, "the absorb must run before the write");

            StringAssert.Contains(body, "var canReserveEchoAbsorb = targetPlayer != null && equippedCloak != null && Cloak.HasProcSpell(equippedCloak) && targetPlayer.CanReserveHuntersEchoAbsorb(ProjectileSource, isHazard: false);",
                "only a player target may reserve an Echo absorb");
        }

        /// <summary>
        /// A player can never reserve an absorb against another player or a pet: PvE only, on top of the EnemyCreature
        /// filter's own PvP refusal.
        /// </summary>
        [TestMethod]
        public void CanReserve_RefusesPlayerAndPetAttackers()
        {
            var body = MethodText(ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs"), "public bool CanReserveHuntersEchoAbsorb(WorldObject source, bool isHazard)");

            StringAssert.Contains(body, "ClassAbilityAttackerFilter.EnemyCreature(source, this) != null && !(source is Player) && !(source is CombatPet)");
            StringAssert.Contains(body, "class_ability_huntersecho_absorb_enabled");
        }

        // ------------------------------------------------------------ review round 1 (2026-11-10)

        /// <summary>
        /// F1: a hazard hit never reserves an Echo absorb (owner ruling R3: no ward soaks a hazard). The physical site
        /// passes its own isHazard through, the spell site passes false explicitly, and the reserve refuses a hazard
        /// first, ahead of every other test.
        /// </summary>
        [TestMethod]
        public void CanReserve_RefusesAHazardHit_AndThePhysicalSitePassesIsHazard()
        {
            var combat = ReadServerFile("WorldObjects/Player_Combat.cs");
            StringAssert.Contains(combat, "var canReserveEchoAbsorb = equippedCloak != null && Cloak.HasProcSpell(equippedCloak) && CanReserveHuntersEchoAbsorb(source, isHazard);",
                "Player.TakeDamage must pass isHazard to the reserve, after the cheap spell-proc-cloak test");

            StringAssert.Contains(ReadServerFile("WorldObjects/SpellProjectile.cs"), "targetPlayer.CanReserveHuntersEchoAbsorb(ProjectileSource, isHazard: false)");

            var body = MethodText(ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs"), "public bool CanReserveHuntersEchoAbsorb(WorldObject source, bool isHazard)");

            Assert.IsTrue(Regex.IsMatch(body, @"\{\s*if \(isHazard\)\s*return false;"), "the reserve must refuse a hazard hit first");

            // F6a: the cheap ownership read runs before any setting lookup
            AssertBefore(body, "GetClassAbilityRank(ClassAbilityId.HuntersEcho)", "PropertyManager.GetBool(", "ownership must be read before the settings");
        }

        /// <summary>
        /// The spell site zeroes the bolt ONLY inside the absorb branch, and that branch is gated on the winning roll
        /// (ShouldAbsorb) and on TryHuntersEchoAbsorb's TRUE.
        /// </summary>
        [TestMethod]
        public void SpellProjectile_AbsorbBranch_IsGatedAndZeroesTheBolt()
        {
            var body = MethodText(ReadServerFile("WorldObjects/SpellProjectile.cs"), "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"if \(HuntersEchoMath\.ShouldAbsorb\(cloakSpellProcRolled, canReserveEchoAbsorb\) && Math\.Round\(damage\) > 0\s*&& targetPlayer\.TryHuntersEchoAbsorb\(ProjectileSource, Spell\.DamageType, \(uint\)Math\.Round\(damage\), equippedCloak\)\)\s*\{\s*damage = 0\.0f;"),
                "the spell absorb branch must be gated on ShouldAbsorb and TryHuntersEchoAbsorb, and zero the bolt");
        }

        /// <summary>F3: the spell roll gate models the non-player health floor, so a floored boss still rolls on an overkill.</summary>
        [TestMethod]
        public void SpellProjectile_RollGate_ModelsTheHealthFloor()
        {
            StringAssert.Contains(ReadServerFile("WorldObjects/SpellProjectile.cs"),
                "HuntersEchoMath.ShouldRollCloakProc(target.Health.Current, damage, targetPlayer == null ? target.P_HealthFloor : 0u, canReserveEchoAbsorb)");
        }

        /// <summary>
        /// The arrow-impact override RETURNS after the Echo path (an Echo arrow must never also run the Creature default),
        /// the impact guard refuses a dead owner, and the owner's DamageTarget is the plain statement that follows the last
        /// guard - not text inside a dead branch.
        /// </summary>
        [TestMethod]
        public void ArrowImpact_OverrideReturns_DeadOwnerRefused_DamageTargetIsTheTail()
        {
            var text = ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs");

            var overrideBody = MethodText(text, "protected override void ResolveMultiShotArrowImpact(");
            Assert.IsTrue(Regex.IsMatch(overrideBody, @"ResolveHuntersEchoArrowImpact\([^)]*\);\s*return;"), "the override must return after the Echo path");

            var body = MethodText(text, "private void ResolveHuntersEchoArrowImpact(");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"if \(owner == null \|\| owner\.IsDead \|\| target == null \|\| target is Player \|\| target is CombatPet \|\| !target\.IsAlive\)\s*return;"),
                "the impact guard must refuse a missing or dead owner, a player, a pet and a dead target");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"owner\.CurrentLandblock\.CurrentLandblockGroup != target\.CurrentLandblock\.CurrentLandblockGroup\)\s*return;\s*" +
                    @"var accuracyLevel = cosmeticProjectile\?\.HuntersEchoShot\?\.AccuracyLevel \?\? HuntersEchoMath\.DefaultShotAccuracyLevel;\s*" +
                    @"var weapons = HuntersEchoWeapons\.FromProjectile\(cosmeticProjectile\);\s*" +
                    @"owner\.DamageTargetAsHuntersEcho\(target, cosmeticProjectile, damageMultiplier, primaryTarget, accuracyLevel, weapons\);\s*\}$"),
                "the owner's DamageTarget (with the arrow's own accuracy bar and weapons) must be the unconditional statement right after the landblock-group guard");
        }

        // ------------------------------------------------------------ owner parity (2026-11-11)

        /// <summary>
        /// Owner ruling 2026-11-11: the trigger gate refuses an owner not holding a missile weapon, after the cheap state
        /// checks and before the cooldown read; the shared gate covers summon, refresh and the cloak-absorb reservation.
        /// </summary>
        [TestMethod]
        public void CanTrigger_RequiresAHeldMissileWeapon_BeforeTheCooldown()
        {
            var text = ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs");
            var body = MethodText(text, "private bool CanTriggerHuntersEcho()");

            // owner ruling 2026-11-11 ("Bow in peace mode also counts."): the weapon alone, never the combat mode
            const string gate = "if (GetEquippedMissileWeaponIgnoringHuntersEchoScope() == null)\n                return false;";

            StringAssert.Contains(body.Replace("\r\n", "\n"), gate, "the held-missile-weapon gate [if (GetEquippedMissileWeaponIgnoringHuntersEchoScope() == null) return false;] (real equipment, never the Echo scope) is missing or changed");
            Assert.IsFalse(body.Contains("CombatMode"), "the gate must not read the combat mode: a bow held in peace mode counts");
            AssertBefore(body, "IsLoggingOut", gate, "the gate follows the cheap state checks");
            AssertBefore(body, gate, "HuntersEchoMath.CooldownReady(", "the gate precedes the cooldown read");

            StringAssert.Contains(MethodText(text, "public bool CanReserveHuntersEchoAbsorb(WorldObject source, bool isHazard)"), "if (!CanTriggerHuntersEcho())");
            StringAssert.Contains(MethodText(text, "internal CombatPet TriggerHuntersEcho(bool absorbed)"), "if (!CanTriggerHuntersEcho())");
        }

        /// <summary>
        /// Owner ruling 2026-11-11 ("Swapping post-spawn should not kill the echo"): no end reason reads the owner's
        /// weapon or combat mode, and an arrow in flight is never dropped for a swap.
        /// </summary>
        [TestMethod]
        public void WeaponSwap_NeverEndsTheEcho_NorDropsAnArrow()
        {
            var text = ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs");
            var end = MethodText(text, "internal string HuntersEchoEndReason(Player owner)");

            StringAssert.Contains(end, "return \"ownerdeath\";", "POSITIVE CONTROL: the end-reason method was found");
            Assert.IsFalse(text.Contains("\"ownerweapon\""), "the ownerweapon end reason is gone");
            Assert.IsFalse(end.Contains("GetEquipped"), "no end reason reads the owner's equipment");
            Assert.IsFalse(end.Contains("CombatMode"), "no end reason reads the owner's combat mode");

            var impact = MethodText(text, "private void ResolveHuntersEchoArrowImpact(");
            StringAssert.Contains(impact, "owner.DamageTargetAsHuntersEcho(", "POSITIVE CONTROL: the impact method was found");
            Assert.IsFalse(impact.Contains("GetEquipped"), "the impact never reads what the owner holds now");
            Assert.IsFalse(impact.Contains("CombatMode"), "the impact never reads the owner's combat mode");
        }

        /// <summary>
        /// The spawn weapon (owner ruling 2026-11-11): every Echo weapon read is answered from the snapshot. Each weapon
        /// getter opens with the scope check; the player's combat type reads it first; the scope is entered and restored
        /// in a finally; the Echo hit and the volley's class-ability rolls run inside it; spawn and refresh snapshot it.
        /// </summary>
        [TestMethod]
        public void WeaponScope_GettersCombatTypeAndCallers_AreWired()
        {
            var equipment = ReadServerFile("WorldObjects/Creature_Equipment.cs").Replace("\r\n", "\n");

            void Opens(string signature, string returned)
            {
                var body = MethodText(equipment, signature);
                Assert.IsTrue(Regex.IsMatch(body, @"^[^{]*\{\s*if \(HuntersEchoWeaponScope != null\)\s*return " + Regex.Escape(returned) + ";"),
                    $"{signature} must open with [if (HuntersEchoWeaponScope != null) return {returned};]");
            }

            Opens("public WorldObject GetEquippedWeapon(bool forceMainHand = false)", "HuntersEchoWeaponScope.Weapon");
            Opens("public WorldObject GetEquippedMeleeWeapon(bool forceMainHand = false)", "null");
            Opens("public WorldObject GetDualWieldWeapon()", "null");
            Opens("public WorldObject GetEquippedMissileWeapon()", "HuntersEchoWeaponScope.Weapon");
            Opens("public WorldObject GetEquippedMissileLauncher()", "HuntersEchoWeaponScope.Launcher");
            Opens("public WorldObject GetEquippedAmmo()", "HuntersEchoWeaponScope.AmmoSlot");

            StringAssert.Contains(equipment, "[ThreadStatic] private static Creature huntersEchoScopeCreature;");
            StringAssert.Contains(equipment, "huntersEchoScopeCreature != null && ReferenceEquals(huntersEchoScopeCreature, this) ? huntersEchoScopeWeapons : null;");

            var combatType = MethodText(ReadServerFile("WorldObjects/Player_Combat.cs"), "public override CombatType GetCombatType()");
            Assert.IsTrue(Regex.IsMatch(combatType, @"^[^{]*\{\s*(//[^\n]*\n\s*)*if \(HuntersEchoWeaponScope != null\)\s*return CombatType\.Missile;"),
                "GetCombatType must open with [if (HuntersEchoWeaponScope != null) return CombatType.Missile;]");

            var player = ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs");
            var with = MethodText(player, "internal T WithHuntersEchoWeapons<T>(HuntersEchoWeapons weapons, Func<T> action)");
            Assert.IsTrue(Regex.IsMatch(with, @"var previous = EnterHuntersEchoWeaponScope\(weapons\);\s*try\s*\{\s*return action\(\);\s*\}\s*finally\s*\{\s*RestoreHuntersEchoWeaponScope\(previous\);\s*\}"),
                "the scope must be entered, then restored in a finally");

            StringAssert.Contains(MethodText(player, "internal DamageEvent DamageTargetAsHuntersEcho("),
                "return WithHuntersEchoWeapons(weapons, () => DamageTarget(target, projectile, damageMultiplier, primaryTarget));");

            var volley = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "internal void FireHuntersEchoVolley(");
            Assert.IsTrue(Regex.IsMatch(volley, @"if \(weapon == null \|\| !owner\.OwnsHuntersEchoItem\(weapon\)\)\s*return;"),
                "a snapshot weapon that left the owner fires nothing");

            var pet = ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs");
            AssertBefore(MethodText(pet, "private bool InitAsHuntersEcho(Player owner, double seconds)"), "SnapshotHuntersEchoShot(owner);", "SnapshotHuntersEchoWeapons(owner);", "spawn snapshots the weapon");
            AssertBefore(MethodText(pet, "internal int RefreshHuntersEcho(Player owner, double seconds)"), "SnapshotHuntersEchoShot(owner);", "SnapshotHuntersEchoWeapons(owner);", "refresh re-snapshots the weapon");
        }

        /// <summary>
        /// Review round 3 R1: the owner's incoming-damage entries suspend the Echo scope (a Reflect monster's damage
        /// inside an Echo hit reads the owner's real defensive equipment), and the capture reads real equipment too.
        /// </summary>
        [TestMethod]
        public void IncomingDamage_AndCapture_SuspendTheEchoScope()
        {
            var combat = ReadServerFile("WorldObjects/Player_Combat.cs");

            var take = MethodText(combat, "public int TakeDamage(WorldObject source, DamageType damageType, float _amount, BodyPart bodyPart, bool crit = false, AttackConditions attackConditions = AttackConditions.None, bool sendDefenderNotification = true, bool isHazard = false, DamageAttackKind? attackKind = null)");
            Assert.IsTrue(Regex.IsMatch(take, @"^[^{]*\{\s*if \(HuntersEchoWeaponScope != null\)\s*return TakeDamageOutsideHuntersEchoScope\(source, damageType, _amount, bodyPart, crit, attackConditions, sendDefenderNotification, isHazard, attackKind\);\s*if \(Invincible \|\| IsDead\) return 0;"),
                "Player.TakeDamage must open by re-entering itself with the Echo scope suspended");

            var dot = MethodText(combat, "public override void TakeDamageOverTime(float _amount, DamageType damageType)");
            Assert.IsTrue(Regex.IsMatch(dot, @"^[^{]*\{\s*if \(HuntersEchoWeaponScope != null\)\s*\{\s*TakeDamageOverTimeOutsideHuntersEchoScope\(_amount, damageType\);\s*return;\s*\}\s*if \(Invincible \|\| IsDead\) return;"),
                "Player.TakeDamageOverTime must open by re-entering itself with the Echo scope suspended");

            var equipment = ReadServerFile("WorldObjects/Creature_Equipment.cs");
            var without = MethodText(equipment, "internal T WithoutHuntersEchoWeaponScope<T>(Func<T> action)");
            Assert.IsTrue(Regex.IsMatch(without, @"var previous = EnterHuntersEchoWeaponScope\(null\);\s*try\s*\{\s*return action\(\);\s*\}\s*finally\s*\{\s*RestoreHuntersEchoWeaponScope\(previous\);\s*\}"),
                "the suspend enters a null scope and restores in a finally");

            StringAssert.Contains(ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs"), "internal HuntersEchoWeapons CaptureHuntersEchoWeapons() => WithoutHuntersEchoWeaponScope(() =>");

            // review round 4 F2: the re-entries are separate methods (no closure on the hot unscoped path), each
            // suspending with a null scope and restoring in a finally
            Assert.IsTrue(Regex.IsMatch(MethodText(combat, "private int TakeDamageOutsideHuntersEchoScope("),
                    @"var previous = EnterHuntersEchoWeaponScope\(null\);\s*try\s*\{\s*return TakeDamage\(source, damageType, amount, bodyPart, crit, attackConditions, sendDefenderNotification, isHazard, attackKind\);\s*\}\s*finally\s*\{\s*RestoreHuntersEchoWeaponScope\(previous\);\s*\}"),
                "TakeDamage's scoped re-entry suspends, re-enters, restores");
            Assert.IsTrue(Regex.IsMatch(MethodText(combat, "private void TakeDamageOverTimeOutsideHuntersEchoScope("),
                    @"var previous = EnterHuntersEchoWeaponScope\(null\);\s*try\s*\{\s*TakeDamageOverTime\(amount, damageType\);\s*\}\s*finally\s*\{\s*RestoreHuntersEchoWeaponScope\(previous\);\s*\}"),
                "TakeDamageOverTime's scoped re-entry suspends, re-enters, restores");
            Assert.IsFalse(take.Contains("=>"), "no lambda in Player.TakeDamage (it would allocate a closure on every hit)");
            Assert.IsFalse(dot.Contains("=>"), "no lambda in Player.TakeDamageOverTime (it would allocate a closure on every tick)");

            var ignoring = MethodText(equipment, "internal WorldObject GetEquippedMissileWeaponIgnoringHuntersEchoScope()");
            Assert.IsTrue(Regex.IsMatch(ignoring,
                    @"\{\s*if \(HuntersEchoWeaponScope == null\)\s*return GetEquippedMissileWeapon\(\);\s*var previous = EnterHuntersEchoWeaponScope\(null\);\s*try\s*\{\s*return GetEquippedMissileWeapon\(\);\s*\}\s*finally\s*\{\s*RestoreHuntersEchoWeaponScope\(previous\);\s*\}\s*\}$"),
                "the gate's real-equipment read: unscoped fast path, else suspend and restore");
            Assert.IsFalse(ignoring.Contains("=>"), "no delegate on the gate's read");
        }

        /// <summary>Orchestrator ruling 2026-11-11: Echo hits get Recklessness as if the owner were in missile mode.</summary>
        [TestMethod]
        public void Recklessness_ReadsTheEchoMissileMode()
        {
            var combat = ReadServerFile("WorldObjects/Player_Combat.cs");
            const string gate = "if (RecklessnessCombatMode != CombatMode.Melee && RecklessnessCombatMode != CombatMode.Missile)";

            StringAssert.Contains(MethodText(combat, "public bool IsRecklessAttackQualifying()"), gate);
            StringAssert.Contains(MethodText(combat, "public float GetRecklessnessMod("), gate);
            StringAssert.Contains(ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs"),
                "internal CombatMode RecklessnessCombatMode => HuntersEchoWeaponScope != null ? CombatMode.Missile : CombatMode;");
        }

        /// <summary>
        /// Orchestrator rulings 2026-11-11 (round 4): an Echo arrow never builds Frenzy stacks; Long Draw (ability and gear
        /// mod) measures an Echo arrow from where the Echo launched it; a debuff answering an avoided Echo arrow follows
        /// the retaliation redirect.
        /// </summary>
        [TestMethod]
        public void Round4_Frenzy_LongDraw_DebuffOnAvoid()
        {
            StringAssert.Contains(ReadServerFile("WorldObjects/Player_ClassAbilityBuffs.cs"),
                "public bool IsFrenzyWeaponStyle => HuntersEchoWeaponScope == null && FrenzyAbility.IsQualifyingStance(CurrentMotionState?.Stance);",
                "Frenzy's stance gate must be false inside an Echo hit");

            var longDraw = ReadServerFile("ClassAbilities/Abilities/LongDrawAbility.cs");
            StringAssert.Contains(longDraw, "damageEvent?.DamageSource?.HuntersEchoLaunchOrigin ?? attacker?.Location;", "the origin is the Echo's launch spot, else the attacker");
            var hook = MethodText(longDraw, "public void ModifyOutgoingDamage(");
            StringAssert.Contains(hook, "var origin = ShotOrigin(attacker, damageEvent);");
            StringAssert.Contains(hook, "var distance = origin.DistanceTo(target.Location);");
            Assert.IsFalse(hook.Contains("attacker.Location.DistanceTo"), "the hook never measures from the attacker directly");

            var gear = ReadServerFile("WorldObjects/Player_EquipmentMods.cs");
            StringAssert.Contains(gear, "var longDrawOrigin = LongDrawAbility.ShotOrigin(this, damageEvent);");
            StringAssert.Contains(gear, "LongDrawAbility.DistanceBonus(longDrawOrigin.DistanceTo(target.Location), 1, longDraw,");

            StringAssert.Contains(ReadServerFile("WorldObjects/Creature_Missile.cs"), "proj.HuntersEchoLaunchOrigin = (this as CombatPet)?.HuntersEchoLaunchOriginStamp;");
            StringAssert.Contains(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"),
                "internal Position HuntersEchoLaunchOriginStamp => IsHuntersEcho && Location != null ? new Position(Location) : null;");

            var debuff = MethodText(ReadServerFile("MonsterEffects/Effects/DebuffEffect.cs"), "public void OnAvoided(");
            Assert.IsTrue(Regex.IsMatch(debuff, @"attacker = Creature\.ResolveHuntersEchoRetaliationTarget\(attacker, defender\);\s*if \(attacker == null\)\s*return;"),
                "the on=avoid debuff resolves its target through the retaliation redirect");
            AssertBefore(debuff, "ResolveHuntersEchoRetaliationTarget(", "Apply(defender, attacker,", "resolved before the debuff is applied");
        }

        /// <summary>Review round 3 R2: what counts as still owning the snapshot weapon, and which ammo fits.</summary>
        [TestMethod]
        public void Ownership_AndAmmoFit_AreExact()
        {
            var owns = MethodText(ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs"), "internal bool OwnsHuntersEchoItem(WorldObject item)");
            Assert.IsTrue(Regex.IsMatch(owns,
                    @"\{\s*if \(item == null \|\| item\.IsDestroyed\)\s*return false;\s*return ReferenceEquals\(GetEquippedItem\(item\.Guid\), item\) \|\| ReferenceEquals\(GetInventoryItem\(item\.Guid\), item\);\s*\}$"),
                "ownership must be [not null, not destroyed] then [wielded or in the inventory tree, as that very object]");

            StringAssert.Contains(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"),
                "ammo != null && ammo.AmmoType == launcher.AmmoType && owner.OwnsHuntersEchoItem(ammo);",
                "usable ammo must exist, fit the launcher and be the owner's");
        }

        /// <summary>
        /// Owner ruling 2026-11-11: "Reflect should hit the echo if it fired the shot, not the main player". Every
        /// retaliation site resolves its target first; the resolver hands back the Echo that fired, or nothing.
        /// </summary>
        [TestMethod]
        public void Retaliation_IsRedirectedToTheFiringEcho_AtEverySite()
        {
            var reflect = ReadServerFile("MonsterEffects/Effects/ReflectEffect.cs");

            var onHit = MethodText(reflect, "public uint OnIncomingDamage(");
            Assert.IsTrue(Regex.IsMatch(onHit, @"attacker = Creature\.ResolveHuntersEchoRetaliationTarget\(attacker, defender\);\s*if \(attacker == null\)\s*return amount;\s*if \(!CloseRangeReflect\.IsWithinRange\(defender, attacker,"),
                "reflect on hit resolves its target before the range check and the damage");
            AssertBefore(onHit, "ResolveHuntersEchoRetaliationTarget(", "TakeDamage(", "resolved before any damage");

            var onAvoid = MethodText(reflect, "public void OnAvoided(");
            Assert.IsTrue(Regex.IsMatch(onAvoid, @"attacker = Creature\.ResolveHuntersEchoRetaliationTarget\(attacker, defender\);\s*if \(attacker == null\)\s*return;"),
                "reflect on avoid resolves its target");
            AssertBefore(onAvoid, "ResolveHuntersEchoRetaliationTarget(", "TakeDamage(", "resolved before any damage");

            var riposte = MethodText(ReadServerFile("MonsterEffects/Effects/RiposteEffect.cs"), "public void OnAvoided(");
            Assert.IsTrue(Regex.IsMatch(riposte, @"attacker = Creature\.ResolveHuntersEchoRetaliationTarget\(attacker, defender\);\s*if \(attacker == null\)\s*return;"),
                "riposte resolves its target at trigger time, before the deferred swing captures it");
            AssertBefore(riposte, "ResolveHuntersEchoRetaliationTarget(", "new ActionChain()", "resolved before the swing is scheduled");

            // review round 4 F1 / F3: at swing time the deferred counter skips a dead or removed target, and an Echo
            // target that no longer shares the monster's landblock group
            var riposteNormalized = riposte.Replace("\r\n", "\n");
            StringAssert.Contains(riposteNormalized, "if (defender.IsDead || attacker.IsDead || attacker.IsDestroyed)\n                    return;",
                "the deferred swing's guard [if (defender.IsDead || attacker.IsDead || attacker.IsDestroyed) return;] is missing or changed");
            StringAssert.Contains(riposteNormalized,
                "if (attacker is CombatPet counterEcho && counterEcho.IsHuntersEcho && !Creature.SharesLandblockGroup(counterEcho, defender))\n                    return;",
                "the deferred swing must skip an Echo no longer in the monster's landblock group");
            AssertBefore(riposteNormalized, "!Creature.SharesLandblockGroup(counterEcho, defender)", "DealCounterDamage(defender, attacker, weapon, pct);", "the group check precedes the swing");

            StringAssert.Contains(ReadServerFile("WorldObjects/Creature_Equipment.cs").Replace("\r\n", "\n"),
                "a?.CurrentLandblock != null && b?.CurrentLandblock != null\n            && a.CurrentLandblock.CurrentLandblockGroup == b.CurrentLandblock.CurrentLandblockGroup;",
                "SharesLandblockGroup: both in the world and in one group");

            var resolve = MethodText(ReadServerFile("WorldObjects/Creature_Equipment.cs"), "internal static Creature ResolveHuntersEchoRetaliationTarget(Creature attacker, Creature retaliator)");
            Assert.IsTrue(Regex.IsMatch(resolve,
                    @"var scope = attacker\?\.HuntersEchoWeaponScope;\s*if \(scope == null\)\s*return attacker;\s*" +
                    @"if \(!\(scope\.Shooter is CombatPet echo\) \|\| !echo\.IsHuntersEcho \|\| echo\.IsDestroyed \|\| echo\.IsDead \|\| !ReferenceEquals\(echo\.P_PetOwner, attacker\)\)\s*return null;\s*" +
                    @"if \(!SharesLandblockGroup\(echo, retaliator\)\)\s*return null;\s*" +
                    @"return echo;\s*\}$"),
                "inside an Echo hit the resolver returns only a live Echo of this owner in the retaliator's landblock group, else null");

            var snapshot = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "private void SnapshotHuntersEchoWeapons(Player owner)");
            StringAssert.Contains(snapshot, "huntersEchoWeapons = owner.CaptureHuntersEchoWeapons()?.WithShooter(this);", "the volley's set names this Echo as its shooter");
            StringAssert.Contains(snapshot, "huntersEchoWeaponGoneUntil = 0.0;", "R3: a refresh clears the weapon-gone memory");

            StringAssert.Contains(ReadServerFile("ClassAbilities/HuntersEchoMath.cs"),
                "return launcher != null ? new HuntersEchoWeapons(launcher, launcher, ammo, projectile.ProjectileSource) : new HuntersEchoWeapons(ammo, null, null, projectile.ProjectileSource);",
                "an arrow's set names its source (the Echo) as the shooter");
        }

        /// <summary>
        /// Review F2: the owner-to-Echo shot data flow, line for line - what the owner records per shot, what the Echo
        /// captures from it, and that the Echo captures under its own name.
        /// </summary>
        [TestMethod]
        public void ShotFlow_OwnerRecords_EchoCapturesUnderItsName()
        {
            var owner = ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs");

            var record = MethodText(owner, "internal void RecordMissileShotForHuntersEcho(float accuracyLevel, float animSpeed)");
            Assert.IsTrue(Regex.IsMatch(record, @"\{\s*lastMissileShotAccuracy = accuracyLevel;\s*lastMissileShotAnimSpeed = animSpeed;\s*\}$"),
                "RecordMissileShotForHuntersEcho must store exactly the shot's bar and reload speed");

            var capture = MethodText(owner, "internal HuntersEchoShot CaptureHuntersEchoShot(string echoName)");
            Assert.IsTrue(Regex.IsMatch(capture,
                    @"\{\s*var liveAnimSpeed = lastMissileShotAnimSpeed == null \? GetAnimSpeed\(\) : 0\.0f;\s*return HuntersEchoShot\.Resolve\(echoName, lastMissileShotAccuracy, lastMissileShotAnimSpeed, liveAnimSpeed\);\s*\}$"),
                "CaptureHuntersEchoShot must resolve from the recorded shot, with the live speed only as the no-shot fallback");

            var echo = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "private void SnapshotHuntersEchoShot(Player owner)");
            Assert.IsTrue(Regex.IsMatch(echo, @"\{\s*huntersEchoShot = owner\.CaptureHuntersEchoShot\(Name\);\s*\}$"),
                "the Echo takes the owner's shot under its own Name");

            Assert.AreEqual(1, Regex.Matches(owner, @"lastMissileShotAccuracy = ").Count, "only the recorder writes the last-shot bar");
            Assert.AreEqual(1, Regex.Matches(owner, @"lastMissileShotAnimSpeed = ").Count, "only the recorder writes the last-shot speed");
        }

        /// <summary>
        /// The Echo reloads at its owner's shot speed and schedules its next shot by the player's repeat cycle at the
        /// owner's bar, and ONLY the Echo: every other creature keeps GetAnimSpeed and the MissileDelay schedule.
        /// </summary>
        [TestMethod]
        public void LaunchMissile_EchoCadence_IsTheOwnersShot_EchoOnly()
        {
            var body = MethodText(ReadServerFile("WorldObjects/Monster_Missile.cs"), "public void LaunchMissile()");

            StringAssert.Contains(body, "var echoShot = (this as CombatPet)?.HuntersEchoShotStamp;");
            StringAssert.Contains(body, "var animSpeed = echoShot != null ? echoShot.AnimSpeed : GetAnimSpeed();");
            StringAssert.Contains(body, "var readyTime = EnqueueMotion(actionChain, MotionCommand.Ready);");

            var branch = Regex.Match(body,
                @"if \(echoShot != null\)\s*\{\s*NextMoveTime = NextAttackTime = PrevAttackTime \+ HuntersEchoMath\.ShotCycleSeconds\(launchTime, reloadTime, readyTime, linkTime, echoShot\.AccuracyLevel\);\s*return;\s*\}");

            Assert.IsTrue(branch.Success, "the Echo schedule [if (echoShot != null) { NextMoveTime = NextAttackTime = PrevAttackTime + HuntersEchoMath.ShotCycleSeconds(...); return; }] is missing or changed");

            AssertBefore(body, "PrevAttackTime = Timers.RunningTime;", branch.Value, "the Echo schedule is measured from this shot's PrevAttackTime");
            AssertBefore(body, branch.Value, "ScaleWeakenedBloodSlowDuration(MissileDelay)", "the Echo returns before the monster MissileDelay schedule");
            StringAssert.Contains(body, "NextMoveTime = NextAttackTime = PrevAttackTime + timeOffset + ScaleWeakenedBloodSlowDuration(MissileDelay);");
        }

        /// <summary>Only the Echo has a shot stamp; LaunchProjectile stamps it on the arrow before the arrow enters the world.</summary>
        [TestMethod]
        public void LaunchProjectile_StampsTheEchoShot_BeforeAddObject_EchoOnly()
        {
            StringAssert.Contains(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"),
                "internal HuntersEchoShot HuntersEchoShotStamp => IsHuntersEcho ? huntersEchoShot : null;");

            var body = MethodText(ReadServerFile("WorldObjects/Creature_Missile.cs"), "public WorldObject LaunchProjectile(");

            AssertBefore(body, "proj.HuntersEchoShot = (this as CombatPet)?.HuntersEchoShotStamp;", "LandblockManager.AddObject(proj)",
                "the Echo shot must be on the arrow before it can collide during AddObject");
        }

        /// <summary>
        /// The owner's hit line: an arrow carrying an Echo shot sends the Echo's line, every other hit the attacker
        /// notification, both behind the same CombatSelf squelch.
        /// </summary>
        [TestMethod]
        public void DamageTarget_EchoArrow_SendsTheEchosHitLine_OthersTheAttackerNotification()
        {
            var body = MethodText(ReadServerFile("WorldObjects/Player_Combat.cs"), "public DamageEvent DamageTarget(Creature target, WorldObject damageSource,");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"if \(!SquelchManager\.Squelches\.Contains\(this, ChatMessageType\.CombatSelf\)\)\s*\{\s*var echoShot = damageSource\?\.HuntersEchoShot;\s*" +
                    @"if \(echoShot != null\)\s*Session\.Network\.EnqueueSend\(new GameMessageSystemChat\(HuntersEchoMath\.FormatEchoHitMessage\(echoShot\.EchoName, intDamage, damageEvent\.DamageType, target\.Name, damageEvent\.IsCritical, damageEvent\.AttackConditions\), ChatMessageType\.CombatSelf\)\);\s*" +
                    @"else\s*Session\.Network\.EnqueueSend\(new GameEventAttackerNotification\("),
                "the hit line must branch on the arrow's HuntersEchoShot inside the CombatSelf squelch, with the attacker notification as the else");
        }

        /// <summary>
        /// The Echo arrow's accuracy: AccuracyLevel reads the override first, and DamageTargetAsHuntersEcho sets it only
        /// around the owner's DamageTarget and restores it in a finally.
        /// </summary>
        [TestMethod]
        public void AccuracyLevel_ReadsTheEchoOverride_SetOnlyAroundTheEchoHit()
        {
            StringAssert.Contains(ReadServerFile("WorldObjects/Player_Missile.cs"),
                "get => huntersEchoAccuracyOverride ?? (IsExhausted ? 0.0f : _accuracyLevel);");

            var text = ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs");
            var body = MethodText(text, "internal DamageEvent DamageTargetAsHuntersEcho(");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"var previous = huntersEchoAccuracyOverride;\s*huntersEchoAccuracyOverride = accuracyLevel;\s*try\s*\{\s*return WithHuntersEchoWeapons\(weapons, \(\) => DamageTarget\(target, projectile, damageMultiplier, primaryTarget\)\);\s*\}\s*finally\s*\{\s*huntersEchoAccuracyOverride = previous;\s*\}"),
                "the override must wrap exactly the owner's DamageTarget and be restored in a finally");

            Assert.AreEqual(2, Regex.Matches(text, @"huntersEchoAccuracyOverride = ").Count, "nothing else writes the override");
        }

        /// <summary>Every player missile shot that reaches its reload records its bar and reload speed for the Echo.</summary>
        [TestMethod]
        public void PlayerLaunchMissile_RecordsTheShot_AtItsReloadSpeed()
        {
            var body = MethodText(ReadServerFile("WorldObjects/Player_Missile.cs"), "public void LaunchMissile(WorldObject target, int attackSequence, MotionStance stance, bool subsequent = false)");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"var animSpeed = GetAnimSpeed\(\);\s*RecordMissileShotForHuntersEcho\(AccuracyLevel, animSpeed\);\s*var reloadTime = EnqueueMotionPersist\(actionChain, stance, MotionCommand\.Reload, animSpeed\);"),
                "the shot is recorded with the same animSpeed its reload uses");
        }

        /// <summary>
        /// Stats: attributes are written before vitals and before defense skills (both add an attribute formula), the
        /// defense snapshot starts with the stats, and spawn and refresh both take the owner's shot after it.
        /// </summary>
        [TestMethod]
        public void Snapshot_AttributesFirst_ThenVitals_ThenDefenses_AndTheShotOnSpawnAndRefresh()
        {
            var text = ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs");

            var stats = MethodText(text, "internal void MatchHuntersEchoStats(Creature source)");

            const string attributes = "mine.StartingValue = HuntersEchoMath.MatchedInitLevel(mine.StartingValue, mine.Current, source.Attributes[attribute].Current);";
            const string vitals = "mine.StartingValue = HuntersEchoMath.MatchedStartingValue(mine.StartingValue, mine.MaxValue, theirs.MaxValue);";
            const string skills = "mine.InitLevel = HuntersEchoMath.MatchedInitLevel(mine.InitLevel, mine.Current, theirs.Current);";

            StringAssert.Contains(stats, "Level = source.Level;");
            AssertBefore(stats, attributes, vitals, "attributes before vitals: a vital's maximum adds an attribute formula");
            AssertBefore(stats, vitals, skills, "vitals before skills (attributes first either way)");
            StringAssert.Contains(stats, "foreach (var (mine, theirs) in new[] { (Health, source.Health), (Stamina, source.Stamina), (Mana, source.Mana) })");

            var defenses = MethodText(text, "private void SnapshotHuntersEchoDefenses(Player owner)");
            Assert.IsTrue(Regex.IsMatch(defenses, @"^private void SnapshotHuntersEchoDefenses\(Player owner\)\s*\{\s*MatchHuntersEchoStats\(owner\);"),
                "the defense snapshot must start with the stats match");

            AssertBefore(MethodText(text, "private bool InitAsHuntersEcho(Player owner, double seconds)"), "SnapshotHuntersEchoDefenses(owner);", "SnapshotHuntersEchoShot(owner);", "spawn takes the owner's shot after the stats");
            AssertBefore(MethodText(text, "internal int RefreshHuntersEcho(Player owner, double seconds)"), "SnapshotHuntersEchoDefenses(owner);", "SnapshotHuntersEchoShot(owner);", "refresh takes the owner's shot after the stats");
            AssertBefore(MethodText(text, "private bool InitAsHuntersEcho(Player owner, double seconds)"), "Name = owner.Name + \"'s Echo\";", "SnapshotHuntersEchoShot(owner);", "the shot carries the Echo's name, so Name is set first");
        }

        /// <summary>
        /// A proc that already summoned the Echo through the cloak absorb must not trigger it again: OnItemProc consumes
        /// the absorb mark, re-runs only the taunt, and returns.
        /// </summary>
        [TestMethod]
        public void OnItemProc_ConsumesTheAbsorbMark_AndOnlyReTaunts()
        {
            var root = ClassAbilityAffinityDeclarationTests.RepoRoot();
            var path = Path.Combine(root, "Source", "ACE.Server", "ClassAbilities", "Abilities", "HuntersEchoAbility.cs");
            var text = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty).Replace("\r\n", "\n");

            var body = MethodText(text, "public void OnItemProc(Player wielder, int rank, WorldObject procSource)");

            Assert.IsTrue(Regex.IsMatch(body,
                    @"if \(wielder\.ConsumeHuntersEchoAbsorb\(procSource\)\)\s*\{\s*wielder\.HuntersEcho\?\.TauntForHuntersEcho\(wielder\);\s*return;\s*\}"),
                "OnItemProc must consume the absorb mark and only re-taunt");

            AssertBefore(body, "wielder.ConsumeHuntersEchoAbsorb(procSource)", "wielder.TriggerHuntersEcho(absorbed: false);", "the absorb mark is checked before a trigger");
        }

        /// <summary>
        /// F5: an Echo end started on the OWNER's thread (logout, replace) drops the link at once and destroys the Echo
        /// on the Echo's own queue, with a direct end when it has no landblock (where an enqueued action is discarded).
        /// The Echo's own tick ends it as superseded should the queued action never run.
        /// </summary>
        [TestMethod]
        public void OwnerThreadEnds_GoThroughTheEchosQueue_WithFallbacks()
        {
            var text = ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs");

            var queue = MethodText(text, "internal void QueueEndHuntersEcho(CombatPet echo, string reason)");
            StringAssert.Contains(queue, "echo.EnqueueAction(new ActionEventDelegate(() => echo.EndHuntersEcho(reason)));");
            Assert.IsTrue(Regex.IsMatch(queue, @"if \(echo\.CurrentLandblock == null\)\s*\{\s*echo\.EndHuntersEcho\(reason\);\s*return;\s*\}"),
                "an Echo with no landblock must be ended directly");
            AssertBefore(queue, "HuntersEcho = null;", "echo.EnqueueAction(", "the owner's link is dropped before the destroy is queued");

            var end = MethodText(text, "internal void EndHuntersEcho(string reason)");
            StringAssert.Contains(end, "QueueEndHuntersEcho(echo, reason);");

            var trigger = MethodText(text, "internal CombatPet TriggerHuntersEcho(bool absorbed)");
            Assert.IsFalse(trigger.Contains("echo.EndHuntersEcho("), "the owner thread must not destroy an Echo directly");

            StringAssert.Contains(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "if (!ReferenceEquals(owner.HuntersEcho, this))\n                return \"superseded\";");
        }

        /// <summary>
        /// A refresh moves the Echo back onto the owner, but NEVER inline (F7): the health/timer/taunt refresh runs at
        /// once, and the move is queued on the Echo's own action queue, guarded so a stale action does nothing. A
        /// different landblock or instance still replaces the Echo. A move that fails leaves the Echo alone (F8).
        /// </summary>
        [TestMethod]
        public void Refresh_QueuesTheMoveOnTheEchosQueue_AndOnlyReplacesAcrossLandblocks()
        {
            var trigger = MethodText(ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs"), "internal CombatPet TriggerHuntersEcho(bool absorbed)");

            StringAssert.Contains(trigger, "HuntersEchoMath.RefreshAction(ReferenceEquals(echo.CurrentLandblock, CurrentLandblock),");
            StringAssert.Contains(trigger, "echo.Location.Instance == Location.Instance");

            Assert.IsTrue(Regex.IsMatch(trigger,
                    @"refreshedEcho\.EnqueueAction\(new ActionEventDelegate\(\(\) =>\s*\{\s*if \(!refreshedEcho\.IsDestroyed && ReferenceEquals\(HuntersEcho, refreshedEcho\) && ReferenceEquals\(refreshedEcho\.CurrentLandblock, CurrentLandblock\)\)\s*refreshedEcho\.RepositionHuntersEcho\(this\);\s*\}\)\);"),
                "the reposition must be queued on the Echo, guarded against a stale action");

            Assert.AreEqual(1, Regex.Matches(trigger, @"RepositionHuntersEcho\(").Count, "the reposition must only ever be the queued call");
            Assert.IsFalse(trigger.Contains("!echo.RepositionHuntersEcho("), "a failed reposition must not replace the Echo");
            AssertBefore(trigger, "taunted = echo.RefreshHuntersEcho(this, seconds);", "refreshedEcho.EnqueueAction(", "the refresh is inline, ahead of the queued move");
        }

        /// <summary>
        /// The move itself follows the pet placement precedent: skipped within RepositionSkipDistance (F10), the owner
        /// spot kept only if IsPlacedAt, the lifted spot only if IsPlacedAtLiftedOwnerSpot, and a rejected landing moved
        /// back where the Echo stood (F8).
        /// </summary>
        [TestMethod]
        public void Reposition_SkipsWhenClose_AcceptsOnlyRealLandings_MovesBackOtherwise()
        {
            var body = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "internal bool RepositionHuntersEcho(Player owner)");

            Assert.IsTrue(Regex.IsMatch(body, @"if \(!HuntersEchoMath\.ShouldReposition\(GetCylinderDistance\(owner\)\)\)\s*return true;"),
                "a refresh with the Echo already on the owner must skip the move");
            AssertBefore(body, "HuntersEchoMath.ShouldReposition(", "PhysicsTeleport(", "the skip runs before any teleport");

            StringAssert.Contains(body, "if (owner?.Location == null || owner.PhysicsObj == null || PhysicsObj == null || Location == null)",
                "GetCylinderDistance dereferences the owner's PhysicsObj, so the early return must check it");
            StringAssert.Contains(body, "if (PhysicsTeleport(ownerSpot) == Physics.Common.SetPositionError.OK)",
                "the owner-spot landing is judged only after an OK teleport");

            Assert.IsTrue(Regex.IsMatch(body, @"if \(IsPlacedAt\(ownerSpot\)\)\s*\{\s*SyncTeleportedPosition\(\);\s*return true;"),
                "the owner spot is kept only when the Echo really landed there");
            Assert.IsTrue(Regex.IsMatch(body, @"if \(IsPlacedAtLiftedOwnerSpot\(lifted, instance\)\)\s*\{\s*SyncTeleportedPosition\(\);\s*return true;"),
                "the lifted spot is kept only under the pet precedent's lifted acceptance");
            StringAssert.Contains(body, "if (moved && !MoveBackAfterRejectedLift(before, instance))");
        }

        /// <summary>F9: an Echo end is idempotent, so a queued end arriving after the Echo already ended is harmless.</summary>
        [TestMethod]
        public void EndHuntersEcho_IsIdempotent()
        {
            var body = MethodText(ReadServerFile("WorldObjects/CombatPet_HuntersEcho.cs"), "internal void EndHuntersEcho(string reason)");

            Assert.IsTrue(Regex.IsMatch(body, @"\{\s*if \(IsDestroyed\)\s*return;"), "EndHuntersEcho must return first when already destroyed");
        }

        /// <summary>F11: no Echo is summoned while the owner is logging out (the logout end has already run).</summary>
        [TestMethod]
        public void CanTrigger_RefusesWhileLoggingOut()
        {
            var body = MethodText(ReadServerFile("WorldObjects/Player_ClassAbilityHuntersEcho.cs"), "private bool CanTriggerHuntersEcho()");

            Assert.IsTrue(Regex.IsMatch(body, @"if \([^)]*\bIsLoggingOut\b[^)]*\)\s*return false;"), "CanTriggerHuntersEcho must refuse a logging-out owner");
        }
        // ------------------------------------------------------------ pet machinery bypasses

        /// <summary>The Echo takes its mitigation snapshot in place of Soul Tether, and never arms the Soul Tether resummon.</summary>
        [TestMethod]
        public void CombatPet_EchoBypassesSoulTether()
        {
            var text = ReadServerFile("WorldObjects/CombatPet.cs");

            var takeDamage = MethodText(text, "public override uint TakeDamage(WorldObject source, DamageType damageType, float amount");
            AssertBefore(takeDamage, "if (IsHuntersEcho)", "GetSoulTetherDamageReductionMod()", "the Echo must branch off before Soul Tether");
            StringAssert.Contains(takeDamage, "amount *= HuntersEchoPhysicalMitigation(damageType);");

            var onDeath = MethodText(text, "public override DeathMessage OnDeath(");
            AssertBefore(onDeath, "if (IsHuntersEcho)", "OnCombatPetDied()", "the Echo's death must not arm Soul Tether");

            var spell = ReadServerFile("WorldObjects/SpellProjectile.cs");
            StringAssert.Contains(spell, "if (target is CombatPet soulTetherPet && !soulTetherPet.IsHuntersEcho)");
        }

        /// <summary>A logout ends the Echo (it holds no pet slot, so the slot teardown does not reach it).</summary>
        [TestMethod]
        public void Logout_EndsTheEcho()
        {
            StringAssert.Contains(ReadServerFile("WorldObjects/Player.cs"), "EndHuntersEcho(\"ownerlogout\");");
        }

        /// <summary>
        /// Players can never damage the Echo: Player.CanDamage refuses every CombatPet. Pinned here because the Echo's
        /// "cannot be harmed by players" rule rests entirely on it.
        /// </summary>
        [TestMethod]
        public void Players_CannotDamageACombatPet()
        {
            var text = ReadServerFile("WorldObjects/Player_Combat.cs");

            Assert.IsTrue(Regex.IsMatch(text, @"public override bool CanDamage\(Creature target\)\s*\{[^}]*!\(target is CombatPet\)"),
                "Player.CanDamage no longer refuses a CombatPet target, so players could hit a Hunter's Echo");
        }
    }
}
