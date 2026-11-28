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
        /// <summary>
        /// Returns the file with every <c>//</c> and <c>/* */</c> comment (doc comments included) blanked to
        /// spaces, line breaks kept. This is the default so a pin cannot be satisfied by a call that survives
        /// only in a comment. Applies to <c>.cs</c> files only: SQL, JSON and TSV have other comment and quote
        /// rules (an apostrophe in SQL is not a char literal), so they come back verbatim. Use
        /// <see cref="ReadRaw"/> for the rare test that asserts on comment text itself.
        /// </summary>
        public static string Read(string repoRelativePath)
        {
            var text = ReadRaw(repoRelativePath);

            return repoRelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? StripComments(text) : text;
        }

        /// <summary>Returns the file verbatim (comments included). Only for pins on comment or doc-comment text.</summary>
        public static string ReadRaw(string repoRelativePath)
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

        /// <summary>
        /// Returns the C# source with every <c>//</c> and <c>/* */</c> comment replaced by spaces, keeping every
        /// line break (and so every line number and brace position relative to line starts). String and char
        /// literals are skipped correctly, so <c>"http://x"</c> survives: regular, verbatim (<c>@"..."</c>),
        /// interpolated (<c>$"..."</c>, <c>$@"..."</c>, including nested literals inside <c>{...}</c> holes) and
        /// raw (<c>"""..."""</c>) strings, and char literals such as <c>'"'</c> and <c>'/'</c>. Doc comments are comments.
        /// </summary>
        public static string StripComments(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            var i = 0;
            CopyCode(source, ref i, sb, untilCloseBrace: false);
            return sb.ToString();
        }

        // Copies code from source[i..] to sb, blanking comments. When untilCloseBrace is set (inside an
        // interpolation hole) it stops after the unmatched '}' and leaves i past it.
        private static void CopyCode(string s, ref int i, System.Text.StringBuilder sb, bool untilCloseBrace)
        {
            var depth = 0;

            while (i < s.Length)
            {
                var c = s[i];
                var next = i + 1 < s.Length ? s[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    while (i < s.Length && s[i] != '\n' && s[i] != '\r')
                    {
                        sb.Append(' ');
                        i++;
                    }
                }
                else if (c == '/' && next == '*')
                {
                    sb.Append("  ");
                    i += 2;

                    while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                    {
                        sb.Append(s[i] == '\n' || s[i] == '\r' ? s[i] : ' ');
                        i++;
                    }

                    if (i < s.Length)
                    {
                        sb.Append("  ");
                        i += 2;
                    }
                }
                else if (c == '\'')
                {
                    // char literal: 'x', '\'', '\\', 'A'
                    sb.Append(c);
                    i++;

                    while (i < s.Length && s[i] != '\'' && s[i] != '\n')
                    {
                        if (s[i] == '\\' && i + 1 < s.Length)
                        {
                            sb.Append(s[i]);
                            i++;
                        }

                        sb.Append(s[i]);
                        i++;
                    }

                    if (i < s.Length && s[i] == '\'')
                    {
                        sb.Append('\'');
                        i++;
                    }
                }
                else if (c == '"' || ((c == '$' || c == '@') && IsStringPrefix(s, i)))
                {
                    CopyString(s, ref i, sb);
                }
                else
                {
                    if (untilCloseBrace)
                    {
                        if (c == '{')
                            depth++;
                        else if (c == '}')
                        {
                            if (depth == 0)
                            {
                                sb.Append(c);
                                i++;
                                return;
                            }

                            depth--;
                        }
                    }

                    sb.Append(c);
                    i++;
                }
            }
        }

        // True when s[i] begins a $ / @ / $@ / @$ prefix that ends in a double quote.
        private static bool IsStringPrefix(string s, int i)
        {
            var j = i;

            while (j < s.Length && (s[j] == '$' || s[j] == '@'))
                j++;

            return j < s.Length && s[j] == '"' && j - i <= 3;
        }

        // Copies one whole string literal (any flavour) starting at its prefix or opening quote.
        private static void CopyString(string s, ref int i, System.Text.StringBuilder sb)
        {
            var dollars = 0;
            var verbatim = false;

            while (i < s.Length && (s[i] == '$' || s[i] == '@'))
            {
                if (s[i] == '$')
                    dollars++;
                else
                    verbatim = true;

                sb.Append(s[i]);
                i++;
            }

            var quotes = 0;

            // A raw string cannot carry an @ prefix, so a verbatim string consumes exactly one opening quote
            // (@"""" is a verbatim string holding one escaped quote, not a raw string).
            while (i < s.Length && s[i] == '"' && !(verbatim && quotes == 1))
            {
                quotes++;
                sb.Append('"');
                i++;
            }

            if (quotes == 2)
            {
                // empty string "" (the second quote closes it)
                return;
            }

            if (quotes >= 3)
            {
                // raw string: ends at a run of >= quotes double quotes. With N dollars, N braces open a hole.
                while (i < s.Length)
                {
                    if (s[i] == '"')
                    {
                        var run = 0;

                        while (i + run < s.Length && s[i + run] == '"')
                            run++;

                        sb.Append('"', run);
                        i += run;

                        if (run >= quotes)
                            return;
                    }
                    else if (dollars > 0 && s[i] == '{')
                    {
                        var run = 0;

                        while (i + run < s.Length && s[i + run] == '{')
                            run++;

                        sb.Append('{', run);
                        i += run;

                        if (run >= dollars)
                            CopyCode(s, ref i, sb, untilCloseBrace: true);
                    }
                    else
                    {
                        sb.Append(s[i]);
                        i++;
                    }
                }

                return;
            }

            // quotes == 1: regular, verbatim or interpolated
            while (i < s.Length)
            {
                var c = s[i];

                if (verbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"')
                        {
                            sb.Append("\"\"");
                            i += 2;
                            continue;
                        }

                        sb.Append('"');
                        i++;
                        return;
                    }
                }
                else
                {
                    if (c == '\\' && i + 1 < s.Length)
                    {
                        sb.Append(c);
                        sb.Append(s[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (c == '"')
                    {
                        sb.Append('"');
                        i++;
                        return;
                    }

                    if (c == '\n')
                    {
                        // unterminated regular string; bail out rather than eat the file
                        return;
                    }
                }

                if (dollars > 0 && c == '{')
                {
                    if (i + 1 < s.Length && s[i + 1] == '{')
                    {
                        sb.Append("{{");
                        i += 2;
                        continue;
                    }

                    sb.Append('{');
                    i++;
                    CopyCode(s, ref i, sb, untilCloseBrace: true);
                    continue;
                }

                sb.Append(c);
                i++;
            }
        }

        /// <summary>
        /// The same method body from the comment-stripped text and from the raw text, for a pin that finds a comment
        /// anchor in <c>Raw</c> and slices <c>Code</c> at that offset. Asserts once that the two have equal length,
        /// which is what makes the cross-indexing valid.
        /// </summary>
        public static (string Code, string Raw) MethodBodyBoth(string repoRelativePath, string signature)
        {
            var code = MethodBody(Read(repoRelativePath), signature);
            var raw = MethodBody(ReadRaw(repoRelativePath), signature);

            Assert.AreEqual(raw.Length, code.Length, $"stripped and raw bodies of {signature} must be the same length");
            return (code, raw);
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
