using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Reads repo source for the pooled-loot wiring pins. Several call sites (Creature.Die, EndRun, the exit
    /// gates) cannot run under this harness because they need a live world, a Player or PropertyManager, so
    /// their ORDER and PRESENCE are pinned against the source text instead. Walks up from the test output
    /// directory, the same way TestEnvironment.FindInSourceTree does, so an --artifacts-path run cannot find
    /// the files and fails loudly rather than passing.
    /// </summary>
    internal static class PooledLootSourceText
    {
        public static string Read(string repoRelativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, repoRelativePath);

                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).Replace("\r\n", "\n");
            }

            Assert.Fail($"Could not find {repoRelativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        public static string MethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"signature not found: {signature}");

            var open = FindOpenBrace(source, start, signature);

            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                        return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"unbalanced braces after: {signature}");
            return null;
        }

        /// <summary>
        /// Returns the text of an expression-bodied member (`=> expr;`, no brace at all), for pins that need to
        /// target one of those instead of a brace-bodied method. See the guard note on <see cref="FindOpenBrace"/>
        /// for why this can't just be folded into <see cref="MethodBody"/>: the two shapes return fundamentally
        /// different slices (a brace-delimited block vs. an arrow expression up to its terminating semicolon).
        /// </summary>
        public static string ExpressionBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"signature not found: {signature}");

            var afterSignature = SkipToBodyStart(source, start, signature, out var isArrow);
            Assert.IsTrue(isArrow, $"expected an expression-bodied member (=>) after: {signature}");

            var semicolon = source.IndexOf(';', afterSignature);
            Assert.IsTrue(semicolon >= 0, $"no terminating ';' after expression body for: {signature}");

            return source.Substring(start, semicolon - start + 1);
        }

        /// <summary>
        /// Locates the `{` that opens a method/member's body after its signature, and REFUSES rather than
        /// guessing when the member turns out to be expression-bodied (`=> expr;`, no brace at all). The naive
        /// version of this helper used to just take the next `{` in the file - for an expression-bodied member
        /// that brace belongs to the NEXT member, so the pin would silently walk into and assert against the
        /// wrong method's body while still reading green. That bit a real edit (a pin on an expression-bodied
        /// roster helper matched the following private overload's body instead and failed with a confusing
        /// "expected string to contain", not "signature not found"). A caller that actually wants to pin an
        /// expression-bodied member should call <see cref="ExpressionBody"/> instead, explicitly.
        /// </summary>
        private static int FindOpenBrace(string source, int start, string signature)
        {
            var open = SkipToBodyStart(source, start, signature, out var isArrow);

            if (isArrow)
            {
                Assert.Fail(
                    $"'{signature}' is expression-bodied (=> ...;), not brace-bodied - MethodBody cannot be used " +
                    "on it. Use PooledLootSourceText.ExpressionBody instead.");
            }

            Assert.IsTrue(open >= 0 && open < source.Length && source[open] == '{', $"no body after: {signature}");
            return open;
        }

        /// <summary>
        /// Scans forward from the end of a matched signature to the first token that can legally start a member
        /// body: `{` for a normal method, or `=>` for an expression-bodied one. Skips whitespace, line comments,
        /// attributes (`[...]`) and generic constraint clauses (`where T : ...`), any of which can legally sit
        /// between a signature's closing parenthesis and its body. `signature` may be a full, closed signature
        /// (ending in `)`) or a deliberately partial one (ending mid-parameter-list, as several pins in this
        /// project do to shorten a long signature) - either way, paren depth is tracked across the boundary
        /// between the matched text and the source that follows it, so the real closing parenthesis of the
        /// parameter list is found correctly in both cases.
        /// </summary>
        private static int SkipToBodyStart(string source, int start, string signature, out bool isArrow)
        {
            var i = start + signature.Length;
            var depth = 0;

            foreach (var c in signature)
            {
                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth--;
            }

            while (depth > 0 && i < source.Length)
            {
                if (source[i] == '(')
                    depth++;
                else if (source[i] == ')')
                    depth--;

                i++;
            }

            while (true)
            {
                while (i < source.Length && char.IsWhiteSpace(source[i]))
                    i++;

                if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                        i++;

                    continue;
                }

                if (i < source.Length && source[i] == '[')
                {
                    var bracketDepth = 0;

                    do
                    {
                        if (source[i] == '[')
                            bracketDepth++;
                        else if (source[i] == ']')
                            bracketDepth--;

                        i++;
                    }
                    while (i < source.Length && bracketDepth > 0);

                    continue;
                }

                if (i + 5 <= source.Length && string.CompareOrdinal(source, i, "where", 0, 5) == 0
                    && (i + 5 == source.Length || !char.IsLetterOrDigit(source[i + 5])))
                {
                    // A generic constraint clause runs up to whichever of '{' or '=>' starts the body; it can
                    // never legally contain either, so scanning ahead for the first one is safe.
                    while (i < source.Length && source[i] != '{' && !(source[i] == '=' && i + 1 < source.Length && source[i + 1] == '>'))
                        i++;

                    continue;
                }

                break;
            }

            if (i + 1 < source.Length && source[i] == '=' && source[i + 1] == '>')
            {
                isArrow = true;
                return i + 2;
            }

            isArrow = false;
            return i;
        }
    }
}
