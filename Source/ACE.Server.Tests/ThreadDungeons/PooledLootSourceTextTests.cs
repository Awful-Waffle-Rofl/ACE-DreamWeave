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
    }
}
