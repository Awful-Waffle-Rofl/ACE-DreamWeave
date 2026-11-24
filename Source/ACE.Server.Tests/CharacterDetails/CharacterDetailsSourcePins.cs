using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.CharacterDetails
{
    /// <summary>
    /// Source-text helpers for the Character Details pins: the body of ONE member (from its exact signature line to the
    /// next member declaration at the same indent), and an exact code-line match inside it, comments stripped.
    /// </summary>
    internal static class CharacterDetailsSourcePins
    {
        internal static string Path(string relative)
            => System.IO.Path.Combine(ACE.Server.Tests.ThreadDungeons.BandUpliftExtractionTests.FindSourceRoot(),
                relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        /// <summary>The code lines (comments stripped, trimmed) of the member whose declaration line is exactly <paramref name="signature"/>.</summary>
        internal static List<string> Body(string relativeFile, string signature)
        {
            var path = Path(relativeFile);
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var lines = File.ReadAllLines(path);
            var start = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() == signature)
                {
                    Assert.AreEqual(-1, start, $"signature `{signature}` appears more than once in {relativeFile}");
                    start = i;
                }
            }

            Assert.AreNotEqual(-1, start, $"signature `{signature}` not found in {relativeFile} - re-aim this pin");

            var indent = lines[start].Length - lines[start].TrimStart().Length;
            var body = new List<string>();

            for (var i = start + 1; i < lines.Length; i++)
            {
                var raw = lines[i];
                var trimmed = raw.TrimStart();
                var lineIndent = raw.Length - trimmed.Length;

                // the next member at the same indent ends this one (its doc comment or its declaration)
                if (lineIndent == indent && trimmed.Length > 0 && trimmed != "{" && trimmed != "}")
                    break;

                var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                var code = (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();

                if (code.Length > 0)
                    body.Add(code);
            }

            return body;
        }

        internal static void AssertLineOnce(string relativeFile, string signature, string code)
        {
            var hits = Body(relativeFile, signature).Count(l => l == code);
            Assert.AreEqual(1, hits, $"expected exactly 1 code line `{code}` in `{signature}` ({relativeFile})");
        }

        internal static void AssertNoLineContains(string relativeFile, string signature, string fragment)
        {
            var hit = Body(relativeFile, signature).FirstOrDefault(l => l.Contains(fragment, StringComparison.Ordinal));
            Assert.IsNull(hit, $"`{signature}` ({relativeFile}) must not contain `{fragment}`, found `{hit}`");
        }

        /// <summary>The FIRST if-head in the member is exactly <paramref name="ifHead"/>.</summary>
        internal static void AssertFirstIf(string relativeFile, string signature, string ifHead)
        {
            var first = Body(relativeFile, signature).FirstOrDefault(l => l.StartsWith("if (", StringComparison.Ordinal));
            Assert.AreEqual(ifHead, first, $"first if-head of `{signature}` ({relativeFile})");
        }
    }
}
