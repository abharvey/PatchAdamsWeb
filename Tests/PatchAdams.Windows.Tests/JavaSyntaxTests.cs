using PatchAdams.Core;

static class JavaSyntaxTests
{
    public static void FixtureUsesJavaVocabularyAndLiterals()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fixtures", "syntax", "java", "ReviewExample.java")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Java syntax fixture not found.");
        var fixture = Path.Combine(root.FullName, "Fixtures", "syntax", "java", "ReviewExample.java");
        var language = SyntaxHighlighter.LanguageForPath(fixture);
        Assert.Equal(".java", language, "Java extension detected");
        Assert.Equal(".java", SyntaxHighlighter.LanguageForPath("src/Review.JAVA"), "extension detection ignores case");
        var tokens = File.ReadAllLines(fixture).SelectMany(line =>
        {
            var parsed = SyntaxHighlighter.TokenizeLine(line, language);
            Assert.True(parsed.All(token => token.Start >= 0 && token.Length > 0 && token.Start + token.Length <= line.Length),
                "tokens stay inside their source line");
            Assert.True(parsed.Zip(parsed.Skip(1)).All(pair => pair.First.Start + pair.First.Length <= pair.Second.Start),
                "tokens are sorted and nonoverlapping");
            return parsed.Select(token => (token.Kind, Text: line.Substring(token.Start, token.Length)));
        }).ToArray();
        foreach (var word in new[] { "sealed", "permits", "implements", "throws", "assert", "instanceof", "synchronized", "transient", "volatile", "record", "default", "@interface" })
            Has(SyntaxTokenKind.Keyword, word);
        foreach (var type in new[] { "boolean", "int", "long", "double", "float", "char", "void", "List", "String", "@Deprecated", "@Override", "Überprüfung" })
            Has(SyntaxTokenKind.Type, type);
        foreach (var number in new[] { "1_000", "0xCA_FE_BA_BEL", "0b1010_0011", ".5e+2D", "0x1.fp2F", "077" })
            Has(SyntaxTokenKind.Number, number);
        Has(SyntaxTokenKind.String, "\"https://example.invalid/review\"");
        Has(SyntaxTokenKind.String, "\"say \\\"// not a comment\\\"\"");
        Has(SyntaxTokenKind.String, "'\\''");
        Has(SyntaxTokenKind.Comment, "// Preserve the original failure.");
        Has(SyntaxTokenKind.Function, "compare");
        Has(SyntaxTokenKind.Function, "$helper");
        Has(SyntaxTokenKind.Function, "isReady");
        Assert.True(!tokens.Any(token => token.Kind == SyntaxTokenKind.Comment && token.Text.Contains("example.invalid", StringComparison.Ordinal)),
            "URL string does not become a comment");

        void Has(SyntaxTokenKind kind, string text) =>
            Assert.True(tokens.Contains((kind, text)), $"{kind}: {text}");
    }

    public static void JavaRulesStayIsolatedAndRespectBoundaries()
    {
        Assert.True(SyntaxHighlighter.TokenizeLine("implements", ".JAVA").Any(token => token.Kind == SyntaxTokenKind.Keyword),
            "direct Java tokenization ignores extension case");
        Assert.True(!SyntaxHighlighter.TokenizeLine("implements", ".cs").Any(token => token.Kind == SyntaxTokenKind.Keyword),
            "Java-specific words do not leak into C#");
        const string identifiers = "publicValue public$ intValue int$ $class 12value";
        Assert.True(!SyntaxHighlighter.TokenizeLine(identifiers, ".java").Any(token =>
            token.Kind is SyntaxTokenKind.Keyword or SyntaxTokenKind.Number),
            "keywords and numbers do not match fragments inside Java identifiers");
        const string comment = "/* public int fake() { return 100; } */";
        var tokens = SyntaxHighlighter.TokenizeLine(comment, ".java");
        Assert.Equal(1, tokens.Count, "block comment consumes embedded tokens");
        Assert.Equal(SyntaxTokenKind.Comment, tokens[0].Kind, "block comment token");
        Assert.Equal(1, SyntaxHighlighter.TokenizeLine("// \"text\" class Fake {}", ".java").Count,
            "line comments consume embedded strings and keywords");
        Assert.Equal(0, SyntaxHighlighter.TokenizeLine("public class Plain {}", ".txt").Count,
            "unsupported extensions stay plain text");
        Assert.True(SyntaxHighlighter.TokenizeLine("public record Result(int value) {}", ".java")
            .Any(token => token.Kind == SyntaxTokenKind.Keyword), "modern record declaration colors");
    }

    public static void RecognizedLanguagesRemainAvailable()
    {
        foreach (var extension in new[]
                 {
                     ".java", ".cs", ".c", ".h", ".cc", ".cpp", ".hpp", ".go", ".rs",
                     ".py", ".swift", ".zig", ".js", ".jsx", ".ts", ".tsx", ".sh", ".bash",
                     ".yaml", ".yml", ".toml", ".json", ".md", ".markdown", ".css", ".html", ".xml"
                 })
            Assert.Equal(extension, SyntaxHighlighter.LanguageForPath("file" + extension), extension + " recognized");
        foreach (var extension in new[] { ".kt", ".kts", ".vb", ".php" })
            Assert.Equal("text", SyntaxHighlighter.LanguageForPath("file" + extension), extension + " remains a documented backlog item");
    }
}
