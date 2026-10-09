using System;
using System.IO;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source pins for the WIRING of the portal double-use guard and the Defense arena integrity eject.
    /// PortalDoubleUseGuardTests and SurvivalArenaIntegrityTests only exercise the two pure decision functions, so
    /// deleting the line that calls one of them would leave those suites green while the guard is gone. Each pin
    /// here is scoped to the body of ONE method (found by its signature, then brace-matched) with comments
    /// stripped first, so a match in a doc comment or in some other method cannot satisfy it.
    /// <para/>
    /// Source files are located by walking up from AppContext.BaseDirectory, the same idiom as
    /// PropertyRegistryTests.FindInSourceTree - which is why this suite must run in-tree.
    /// </summary>
    [TestClass]
    public class DefenseArenaWiringSourceTests
    {
        private const string PortalPath = "Source/ACE.Server/WorldObjects/Portal.cs";
        private const string PlayerLocationPath = "Source/ACE.Server/WorldObjects/Player_Location.cs";
        private const string SurvivalPath = "Source/ACE.Server/WorldObjects/Player_SurvivalChallenge.cs";
        private const string MonsterMeleePath = "Source/ACE.Server/WorldObjects/Monster_Melee.cs";
        private const string ProjectileHelperPath = "Source/ACE.Server/WorldObjects/ProjectileCollisionHelper.cs";
        private const string SpellProjectilePath = "Source/ACE.Server/WorldObjects/SpellProjectile.cs";

        private const string PendingCheck = "IsPortalTeleportPending(player.PendingPortalTeleportTime";
        private const string MarkerStamp = "player.PendingPortalTeleportTime = Time.GetUnixTime();";
        private const string MarkerClear = "PendingPortalTeleportTime = null;";
        private const string EngagementCall = "NoteSurvivalEngagement(";

        // ---- portal double-use guard ----

        [TestMethod]
        public void CheckUseRequirements_RefusesWhilePortalTeleportPending()
        {
            var body = MethodBody(PortalPath, "public override ActivationResult CheckUseRequirements(WorldObject activator)");

            StringAssert.Contains(body, PendingCheck, "Portal.CheckUseRequirements must refuse while a portal teleport is pending");
        }

        [TestMethod]
        public void ActOnUse_ChecksPendingBeforeCreatingAnEphemeralInstance()
        {
            var body = MethodBody(PortalPath, "public override void ActOnUse(WorldObject activator)");

            var check = body.IndexOf(PendingCheck, StringComparison.Ordinal);
            var ephemeral = body.IndexOf("GetNewEphemeralLandblock(", StringComparison.Ordinal);

            Assert.IsTrue(check >= 0, "Portal.ActOnUse must re-check IsPortalTeleportPending (OnCastSpell reaches it without CheckUseRequirements)");
            Assert.IsTrue(ephemeral >= 0, "Portal.ActOnUse no longer creates an ephemeral landblock - this pin needs revisiting");
            Assert.IsTrue(check < ephemeral, "the pending check must run before the first GetNewEphemeralLandblock, or a duplicate use still creates a second instance");
        }

        [TestMethod]
        public void ActOnUse_StampsMarkerImmediatelyBeforeThreadSafeTeleport()
        {
            var body = MethodBody(PortalPath, "public override void ActOnUse(WorldObject activator)");

            var stamp = body.IndexOf(MarkerStamp, StringComparison.Ordinal);
            Assert.IsTrue(stamp >= 0, "Portal.ActOnUse must stamp PendingPortalTeleportTime");

            var afterStamp = body.Substring(stamp + MarkerStamp.Length).TrimStart();
            Assert.IsTrue(afterStamp.StartsWith("WorldManager.ThreadSafeTeleport(", StringComparison.Ordinal),
                "the PendingPortalTeleportTime stamp must be the statement immediately before WorldManager.ThreadSafeTeleport");
        }

        [TestMethod]
        public void Teleport_ClearsMarkerInTheRefusalBranch()
        {
            var body = MethodBody(PlayerLocationPath, "public void Teleport(Position _newPosition, bool fromPortal = false)");
            var refusal = BlockAfter(body, "if (rejection != InstanceRejection.None && _newPosition.IsEphemeralRealm)");

            StringAssert.Contains(refusal, MarkerClear, "a refused ephemeral teleport must clear the pending marker");
        }

        [TestMethod]
        public void Teleport_ClearsMarkerAfterTeleportingIsSet()
        {
            var body = MethodBody(PlayerLocationPath, "public void Teleport(Position _newPosition, bool fromPortal = false)");

            var teleporting = body.IndexOf("Teleporting = true;", StringComparison.Ordinal);
            Assert.IsTrue(teleporting >= 0, "Player.Teleport no longer sets Teleporting - this pin needs revisiting");

            var clear = body.IndexOf(MarkerClear, teleporting, StringComparison.Ordinal);
            var sendTeleport = body.IndexOf("new GameMessagePlayerTeleport(", teleporting, StringComparison.Ordinal);

            Assert.IsTrue(clear >= 0, "Player.Teleport must clear PendingPortalTeleportTime once Teleporting is set");
            Assert.IsTrue(sendTeleport < 0 || clear < sendTeleport, "the marker must be cleared as the teleport starts, before the client is told to teleport");
        }

        // ---- arena integrity eject ----

        [TestMethod]
        public void ScheduleSurvivalTier_RunsIntegrityEjectBeforeAdvancingTheTier()
        {
            var body = MethodBody(SurvivalPath, "private void ScheduleSurvivalTier(uint runInstance, int intervalSeconds, double rampRate, int ratingPerTier)");

            var eject = body.IndexOf("TryEjectUnusableSurvivalArena()", StringComparison.Ordinal);
            var tier = body.IndexOf("survivalChallengeTier++", StringComparison.Ordinal);

            Assert.IsTrue(eject >= 0, "the escalation tick must call TryEjectUnusableSurvivalArena");
            Assert.IsTrue(tier >= 0, "the escalation tick no longer advances survivalChallengeTier - this pin needs revisiting");
            Assert.IsTrue(eject < tier, "the integrity eject must run before the tier advances");
        }

        [TestMethod]
        public void MonsterMelee_NotesEngagement()
        {
            StringAssert.Contains(MethodBody(MonsterMeleePath, "public float MeleeAttack()"), EngagementCall);
        }

        [TestMethod]
        public void MonsterMissile_NotesEngagement()
        {
            StringAssert.Contains(MethodBody(ProjectileHelperPath, "public static void OnCollideObject(WorldObject worldObject, WorldObject target)"), EngagementCall);
        }

        [TestMethod]
        public void SpellProjectile_NotesEngagement()
        {
            StringAssert.Contains(MethodBody(SpellProjectilePath, "public override void OnCollideObject(WorldObject target)"), EngagementCall);
        }

        // ---- arrival-overlap portal guard ----

        private const string PlayerTickPath = "Source/ACE.Server/WorldObjects/Player_Tick.cs";

        [TestMethod]
        public void PortalCollision_SkipsArrivalOverlapBeforeActivating()
        {
            var body = MethodBody(PortalPath, "public virtual void OnCollideObject(Player player)");

            var guard = body.IndexOf("player.IsArrivalOverlapPortal(this)", StringComparison.Ordinal);
            var activate = body.IndexOf("OnActivate(player)", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0, "Portal.OnCollideObject(Player) must refuse a portal held by an arrival overlap");
            Assert.IsTrue(activate >= 0, "Portal.OnCollideObject(Player) no longer activates - this pin needs revisiting");
            Assert.IsTrue(guard < activate, "the arrival-overlap check must run before OnActivate");
        }

        [TestMethod]
        public void OnTeleportComplete_CapturesArrivalOverlapsWhenMaterialising()
        {
            var body = MethodBody(PlayerLocationPath, "public void OnTeleportComplete()");

            var materialise = body.IndexOf("Teleporting = false;", StringComparison.Ordinal);
            var capture = body.IndexOf("CaptureArrivalPortalOverlaps();", StringComparison.Ordinal);

            Assert.IsTrue(materialise >= 0, "OnTeleportComplete no longer clears Teleporting - this pin needs revisiting");
            Assert.IsTrue(capture > materialise, "OnTeleportComplete must capture arrival overlaps once the player materialises");
        }

        [TestMethod]
        public void UpdatePlayerPosition_ReleasesArrivalOverlapsAfterThePhysicsUpdate()
        {
            var body = MethodBody(PlayerTickPath, "public bool UpdatePlayerPosition(ACE.Entity.Position newPosition, bool forceUpdate = false)");

            var physics = body.IndexOf("PhysicsObj.update_object_server(", StringComparison.Ordinal);
            var release = body.IndexOf("ReleaseArrivalPortalOverlaps();", StringComparison.Ordinal);

            Assert.IsTrue(physics >= 0, "UpdatePlayerPosition no longer runs update_object_server - this pin needs revisiting");
            Assert.IsTrue(release > physics, "UpdatePlayerPosition must release walked-clear arrival overlaps after the physics update");
        }

        // ---- helpers ----

        private static string MethodBody(string relativePath, string signature)
        {
            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = StripComments(File.ReadAllText(path));

            var at = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"Signature not found in {relativePath}: {signature}");
            Assert.AreEqual(-1, code.IndexOf(signature, at + signature.Length, StringComparison.Ordinal), $"Signature is ambiguous in {relativePath}: {signature}");

            return BlockAfter(code, signature);
        }

        /// <summary>The brace-matched block that opens at the first '{' after <paramref name="marker"/>.</summary>
        private static string BlockAfter(string code, string marker)
        {
            var at = code.IndexOf(marker, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"Marker not found: {marker}");

            var open = code.IndexOf('{', at + marker.Length);
            Assert.IsTrue(open >= 0, $"No block after: {marker}");

            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{')
                    depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(open, i - open + 1);
            }

            Assert.Fail($"Unbalanced braces after: {marker}");
            return null;
        }

        /// <summary>
        /// Removes // and /* */ comments, leaving string and char literals intact (so a "//" inside a string is
        /// not mistaken for a comment). Interpolation holes are not parsed; their nested quotes pair up evenly,
        /// which is all comment detection needs.
        /// </summary>
        private static string StripComments(string code)
        {
            var sb = new StringBuilder(code.Length);
            var i = 0;

            while (i < code.Length)
            {
                var c = code[i];
                var next = i + 1 < code.Length ? code[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    while (i < code.Length && code[i] != '\n')
                        i++;
                }
                else if (c == '/' && next == '*')
                {
                    var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? code.Length : end + 2;
                }
                else if (c == '"' || c == '\'')
                {
                    var verbatim = c == '"' && i > 0 && code[i - 1] == '@';
                    sb.Append(c);
                    i++;

                    while (i < code.Length)
                    {
                        var s = code[i];
                        sb.Append(s);
                        i++;

                        if (!verbatim && s == '\\' && i < code.Length)
                        {
                            sb.Append(code[i]);
                            i++;
                        }
                        else if (s == c)
                        {
                            if (verbatim && i < code.Length && code[i] == '"')
                            {
                                sb.Append('"');
                                i++;
                            }
                            else
                                break;
                        }
                        else if (s == '\n' && !verbatim)
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            return sb.ToString();
        }

        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
