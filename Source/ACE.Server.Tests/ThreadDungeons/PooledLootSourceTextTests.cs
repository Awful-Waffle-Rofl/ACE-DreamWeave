using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers the guard in <see cref="PooledLootSourceText.MethodBody"/> that refuses to walk into the next
    /// member's body when the pinned signature turns out to belong to an expression-bodied member. See that
    /// helper's doc comment for the real edit this bit (an expression-bodied roster helper's pin silently
    /// matched the following private overload and failed with an unrelated-looking assertion).
    /// </summary>
    [TestClass]
    public class PooledLootSourceTextTests
    {
        [TestMethod]
        public void Expression_bodied_member_throws_instead_of_returning_the_next_members_body()
        {
            var source =
                "class C\n" +
                "{\n" +
                "    public static int CandidateFrom(int player) => player + 1;\n" +
                "\n" +
                "    private static int NextOverload(int player)\n" +
                "    {\n" +
                "        return player - 1;\n" +
                "    }\n" +
                "}\n";

            var ex = Assert.ThrowsExactly<AssertFailedException>(
                () => PooledLootSourceText.MethodBody(source, "public static int CandidateFrom(int player)"));

            StringAssert.Contains(ex.Message, "CandidateFrom");
            StringAssert.Contains(ex.Message, "expression-bodied");
            StringAssert.Contains(ex.Message, "ExpressionBody");
            StringAssert.DoesNotMatch(ex.Message, new System.Text.RegularExpressions.Regex("player - 1"));
        }

        [TestMethod]
        public void Brace_bodied_member_is_unchanged()
        {
            var source =
                "class C\n" +
                "{\n" +
                "    public static int Normal(int player)\n" +
                "    {\n" +
                "        return player + 1;\n" +
                "    }\n" +
                "}\n";

            var body = PooledLootSourceText.MethodBody(source, "public static int Normal(int player)");

            StringAssert.Contains(body, "return player + 1;");
            Assert.IsTrue(body.StartsWith("{"));
            Assert.IsTrue(body.EndsWith("}"));
        }

        [TestMethod]
        public void A_generic_constraint_clause_before_the_brace_still_resolves()
        {
            var source =
                "class C\n" +
                "{\n" +
                "    public static T Make<T>(int player)\n" +
                "        where T : new()\n" +
                "    {\n" +
                "        return new T();\n" +
                "    }\n" +
                "}\n";

            var body = PooledLootSourceText.MethodBody(source, "public static T Make<T>(int player)");

            StringAssert.Contains(body, "return new T();");
        }

        [TestMethod]
        public void An_attribute_before_the_brace_still_resolves()
        {
            var source =
                "class C\n" +
                "{\n" +
                "    [SomeAttribute]\n" +
                "    public static int Attributed(int player)\n" +
                "    {\n" +
                "        return player;\n" +
                "    }\n" +
                "}\n";

            var body = PooledLootSourceText.MethodBody(source, "public static int Attributed(int player)");

            StringAssert.Contains(body, "return player;");
        }

        [TestMethod]
        public void A_partial_signature_ending_mid_parameter_list_still_finds_the_real_closing_paren()
        {
            var source =
                "class C\n" +
                "{\n" +
                "    public static int Wide(int a, int b,\n" +
                "        int c)\n" +
                "    {\n" +
                "        return a + b + c;\n" +
                "    }\n" +
                "}\n";

            var body = PooledLootSourceText.MethodBody(source, "public static int Wide(int a, int b,");

            StringAssert.Contains(body, "return a + b + c;");
        }

        [TestMethod]
        public void Missing_signature_still_reports_signature_not_found()
        {
            var source = "class C\n{\n}\n";

            var ex = Assert.ThrowsExactly<AssertFailedException>(
                () => PooledLootSourceText.MethodBody(source, "public static int NoSuchMethod()"));

            StringAssert.Contains(ex.Message, "signature not found");
        }

        [TestMethod]
        public void ExpressionBody_returns_the_arrow_expression_up_to_its_semicolon()
        {
            var source =
                "class C\n" +
                "{\n" +
                "    public static int CandidateFrom(int player) => player + 1;\n" +
                "\n" +
                "    private static int NextOverload(int player)\n" +
                "    {\n" +
                "        return player - 1;\n" +
                "    }\n" +
                "}\n";

            var body = PooledLootSourceText.ExpressionBody(source, "public static int CandidateFrom(int player)");

            StringAssert.Contains(body, "=> player + 1;");
            StringAssert.DoesNotMatch(body, new System.Text.RegularExpressions.Regex("player - 1"));
        }

        // ---- StripComments -------------------------------------------------------------------------------

        private static string Strip(string s) => PooledLootSourceText.StripComments(s);

        [TestMethod]
        public void StripComments_blanks_line_and_block_comments_and_keeps_line_breaks()
        {
            var src = "int a; // Call(x)\nint b; /* Call(y)\n still */ int c;\n/// <summary>Call(z)</summary>\nint d;\n";
            var got = Strip(src);

            Assert.AreEqual(src.Length, got.Length);
            Assert.AreEqual(src.Count(ch => ch == '\n'), got.Count(ch => ch == '\n'));
            StringAssert.DoesNotMatch(got, new System.Text.RegularExpressions.Regex("Call"));
            StringAssert.Contains(got, "int a;");
            StringAssert.Contains(got, "int c;");
            StringAssert.Contains(got, "int d;");
        }

        [TestMethod]
        public void StripComments_keeps_comment_markers_inside_string_literals()
        {
            StringAssert.Contains(Strip("var u = \"http://x\"; // gone"), "\"http://x\"");
            StringAssert.Contains(Strip("var u = \"a /* not a comment */ b\";"), "/* not a comment */");
            StringAssert.DoesNotMatch(Strip("var u = \"http://x\"; // gone"), new System.Text.RegularExpressions.Regex("gone"));
        }

        [TestMethod]
        public void StripComments_handles_escaped_quotes_in_regular_strings()
        {
            var got = Strip("var s = \"a \\\" // still string\"; // gone\nint x;");

            StringAssert.Contains(got, "// still string");
            StringAssert.DoesNotMatch(got, new System.Text.RegularExpressions.Regex("gone"));
            StringAssert.Contains(got, "int x;");

            // string ending in an escaped backslash: the closing quote is real
            var got2 = Strip("var s = \"c:\\\\\"; // gone\nint y;");
            StringAssert.DoesNotMatch(got2, new System.Text.RegularExpressions.Regex("gone"));
            StringAssert.Contains(got2, "int y;");
        }

        [TestMethod]
        public void StripComments_handles_verbatim_strings()
        {
            var got = Strip("var s = @\"a \"\" // still string\"; // gone\nint x;");

            StringAssert.Contains(got, "// still string");
            StringAssert.DoesNotMatch(got, new System.Text.RegularExpressions.Regex("gone"));
            StringAssert.Contains(got, "int x;");

            // backslash is not an escape in a verbatim string
            var got2 = Strip("var s = @\"c:\\\"; // gone\nint y;");
            StringAssert.DoesNotMatch(got2, new System.Text.RegularExpressions.Regex("gone"));
            StringAssert.Contains(got2, "int y;");
        }

        [TestMethod]
        public void StripComments_handles_interpolated_strings_with_nested_strings_in_holes()
        {
            var got = Strip("var s = $\"a {(x ? \"}//\" : \"b\")} // text {y}\"; // gone\nint z;");

            StringAssert.Contains(got, "\"}//\"");
            StringAssert.Contains(got, "// text {y}");
            StringAssert.DoesNotMatch(got, new System.Text.RegularExpressions.Regex("gone"));
            StringAssert.Contains(got, "int z;");

            var got2 = Strip("var s = $@\"a {x} \"\" // text {{lit}}\"; // gone\nint z;");
            StringAssert.Contains(got2, "// text {{lit}}");
            StringAssert.DoesNotMatch(got2, new System.Text.RegularExpressions.Regex("gone"));
            StringAssert.Contains(got2, "int z;");

            // a comment INSIDE a hole is still a comment
            var got3 = Strip("var s = $\"a {x /* hole */} b\";");
            StringAssert.DoesNotMatch(got3, new System.Text.RegularExpressions.Regex("hole"));
            StringAssert.Contains(got3, "b\";");
        }

        [TestMethod]
        public void StripComments_handles_raw_string_literals()
        {
            var got = Strip("var s = \"\"\"\n  has \"quotes\" and // not a comment\n  \"\"\"; // gone\nint z;");

            StringAssert.Contains(got, "// not a comment");
            StringAssert.DoesNotMatch(got, new System.Text.RegularExpressions.Regex("gone"));
            StringAssert.Contains(got, "int z;");

            var got2 = Strip("var s = $\"\"\"\n  {x} // t {\"//\"}\n  \"\"\"; // gone\nint z;");
            StringAssert.Contains(got2, "// t");
            StringAssert.DoesNotMatch(got2, new System.Text.RegularExpressions.Regex("gone"));
            StringAssert.Contains(got2, "int z;");
        }

        [TestMethod]
        public void StripComments_handles_char_literals()
        {
            var got = Strip("var q = '\"'; var s = '/'; var t = '\\''; // gone\nint x = 1; // gone2\n");

            StringAssert.Contains(got, "'\"'");
            StringAssert.Contains(got, "'/'");
            StringAssert.Contains(got, "int x = 1;");
            StringAssert.DoesNotMatch(got, new System.Text.RegularExpressions.Regex("gone"));
        }

        [TestMethod]
        public void StripComments_comment_text_with_quotes_and_braces_does_not_confuse_the_scan()
        {
            var got = Strip("// he said \"hi {\nint a;\n/* \" { */ int b; // '\nint c;\n");

            StringAssert.Contains(got, "int a;");
            StringAssert.Contains(got, "int b;");
            StringAssert.Contains(got, "int c;");
            Assert.AreEqual(0, got.Count(ch => ch == '{' || ch == '"' || ch == '\''));
        }

        [TestMethod]
        public void MethodBody_ignores_a_brace_in_a_comment_and_a_call_that_only_survives_in_a_comment()
        {
            var source = "class C\n{\n    void M()\n    {\n        // Foo();\n        /* } */\n        Bar();\n    }\n}\n";
            var stripped = Strip(source);
            var body = PooledLootSourceText.MethodBody(stripped, "void M()");

            StringAssert.Contains(body, "Bar();");
            StringAssert.DoesNotMatch(body, new System.Text.RegularExpressions.Regex("Foo"));
        }

        [TestMethod]
        public void Read_strips_comments_by_default_and_ReadRaw_keeps_them()
        {
            // This helper's own source is a convenient file with comment text in it.
            const string path = "Source/ACE.Server.Tests/ThreadDungeons/PooledLootSourceText.cs";
            const string commentOnly = "Copies code from source[i..] to sb, blanking comments";

            var raw = PooledLootSourceText.ReadRaw(path);
            var read = PooledLootSourceText.Read(path);

            StringAssert.Contains(raw, commentOnly);
            StringAssert.DoesNotMatch(read, new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(commentOnly)));
            Assert.AreEqual(raw.Length, read.Length, "stripping keeps every offset");
            StringAssert.Contains(read, "public static string Read(string repoRelativePath)");
        }

        [TestMethod]
        public void Read_leaves_non_cs_files_verbatim()
        {
            // SQL has its own comment and quote rules (an apostrophe is not a char literal), so only .cs is stripped.
            const string path = "Source/property-registry.tsv";

            Assert.AreEqual(PooledLootSourceText.ReadRaw(path), PooledLootSourceText.Read(path));
        }

        [TestMethod]
        public void StripComments_verbatim_string_starting_with_an_escaped_quote_is_not_a_raw_string()
        {
            foreach (var literal in new[] { "@\"\"\"\"", "@\"\"\"a \"\" b\"", "$@\"\"\"{x}\"\"\"", "@\"\"\"\"\"\"" })
            {
                var got = Strip("var s = " + literal + "; // gone\nint z; /* gone2 */ int w;");

                StringAssert.Contains(got, literal, literal);
                StringAssert.DoesNotMatch(got, new System.Text.RegularExpressions.Regex("gone"), literal);
                StringAssert.Contains(got, "int z;", literal);
                StringAssert.Contains(got, "int w;", literal);
            }
        }

        [TestMethod]
        public void StripComments_unterminated_verbatim_string_at_end_of_input_does_not_throw_or_grow()
        {
            var src = "var s = @\"abc // not stripped";
            var got = Strip(src);

            Assert.AreEqual(src.Length, got.Length);
            Assert.AreEqual(src, got, "an unterminated string runs to EOF, so its text is left alone");
        }
    }
}
