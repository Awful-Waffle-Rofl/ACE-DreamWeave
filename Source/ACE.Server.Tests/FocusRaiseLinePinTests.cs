using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-scan pin for the Focus raise chat line wiring in Player_Attributes.cs. The pure formatter is pinned
    /// by FocusSpellDamageTests; this pins that the raise handler actually calls it with the BUFFED Focus
    /// (Focus.Current) and the live tunable, after the "Your base ... is now" message. Whole trimmed lines are
    /// compared, never substrings.
    /// </summary>
    [TestClass]
    public class FocusRaiseLinePinTests
    {
        private const string BaseMessageLine =
            "var msg = new GameMessageSystemChat($\"Your base {attribute} is now {creatureAttribute.Base}{suffix}!\", ChatMessageType.Advancement);";

        private const string IfHead = "if (attribute == PropertyAttribute.Focus)";

        private const string CallLine =
            "var focusLine = FormatFocusSpellDamageLine(Focus.Current, PropertyManager.GetDouble(\"magic_damage_per_focus_point\").Item);";

        [TestMethod]
        public void RaiseHandler_SendsFocusLine_FromBuffedFocus_AfterBaseMessage()
        {
            var lines = File.ReadAllLines(FindSource("ACE.Server", "WorldObjects", "Player_Attributes.cs"))
                .Select(l => l.Trim()).ToArray();

            var baseIdx = Array.IndexOf(lines, BaseMessageLine);
            Assert.AreEqual(1, lines.Count(l => l == BaseMessageLine), "expected exactly one base-attribute message line");

            Assert.AreEqual(1, lines.Count(l => l == IfHead), $"expected exactly one line `{IfHead}`");
            var ifIdx = Array.IndexOf(lines, IfHead);

            Assert.AreEqual(1, lines.Count(l => l == CallLine), $"expected exactly one line `{CallLine}`");
            var callIdx = Array.IndexOf(lines, CallLine);

            Assert.IsTrue(ifIdx > baseIdx, "the Focus if-head must come after the base message line");
            Assert.IsTrue(callIdx > ifIdx && callIdx - ifIdx <= 4, "the FormatFocusSpellDamageLine call must sit inside the Focus if-block");
        }

        private static string FindSource(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(new[] { dir.FullName, "Source" }.Concat(relative).ToArray());
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            Assert.Fail($"Could not find {string.Join("/", relative)} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
