using System.Text.RegularExpressions;

namespace PatchAdams.Core;

public enum SyntaxTokenKind
{
    Keyword,
    Type,
    Function,
    String,
    Number,
    Comment
}

public sealed record SyntaxToken(int Start, int Length, SyntaxTokenKind Kind);

/// <summary>Small deterministic lexer used for code-view coloring; it does not parse or execute source.</summary>
public static class SyntaxHighlighter
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".go", ".rs", ".py", ".swift", ".zig", ".js", ".jsx", ".ts", ".tsx", ".c", ".h", ".cc",
        ".cpp", ".hpp", ".cs", ".sh", ".bash", ".yaml", ".yml", ".toml", ".json", ".md", ".markdown",
        ".css", ".html", ".xml", ".java"
    };

    private const string Keywords =
        "if|else|for|while|return|throw|try|catch|finally|async|await|func|function|def|class|struct|enum|type|interface|const|let|var|static|public|private|protected|internal|import|from|using|namespace|package|match|switch|case|break|continue|new|true|false|null|nil|None|self|this|impl|trait|fn|pub|mut|in|as|is|where|go|defer|select|range|map|chan|yield|with|pass|raise|lambda|export|extends|super";

    private const string JavaKeywords =
        "abstract|assert|break|case|catch|class|const|continue|default|do|else|enum|extends|final|finally|for|goto|if|implements|import|instanceof|interface|native|new|package|private|protected|public|return|static|strictfp|super|switch|synchronized|this|throw|throws|transient|try|volatile|while|true|false|null|exports|module|open|opens|provides|requires|to|transitive|uses|with|var|yield|record|sealed|non-sealed|permits|when|_";

    // Java gets its own vocabulary and literal rules rather than broadening every
    // other language's keyword set. Coloring is lexical and line-based, not semantic.
    private static readonly Regex JavaTokens = new(
        @"(?<comment>//.*$|/\*.*?(?:\*/|$))|" +
        @"(?<string>""(?:\\.|[^""\\])*""?|'(?:\\.|[^'\\])*'?)|" +
        @"(?<keyword>@interface(?![\w$]))|" +
        @"(?<type>@[\p{L}_$][\p{L}\p{Nd}_$]*(?:\.[\p{L}_$][\p{L}\p{Nd}_$]*)*)|" +
        @"(?<number>(?<![\w$])(?:0[xX][\da-fA-F_]+(?:\.[\da-fA-F_]*)?(?:[pP][+-]?\d[\d_]*)?[fFdDlL]?|0[bB][01_]+[lL]?|(?:\d[\d_]*(?:\.[\d_]*)?|\.\d[\d_]*)(?:[eE][+-]?\d[\d_]*)?[fFdDlL]?)(?![\w$]))|" +
        @"(?<type>(?<![\w$])(?:boolean|byte|char|short|int|long|float|double|void)(?![\w$]))|" +
        $@"(?<keyword>(?<![\w$])(?:{JavaKeywords})(?![\w$]))|" +
        @"(?<type>(?<![\w$])\p{Lu}[\p{L}\p{Nd}_$]*(?![\w$]))|" +
        @"(?<function>(?<![\w$])[\p{L}_$][\p{L}\p{Nd}_$]*(?=\s*\())",
        RegexOptions.CultureInvariant);

    public static string LanguageForPath(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedExtensions.Contains(extension) ? extension.ToLowerInvariant() : "text";
    }

    public static IReadOnlyList<SyntaxToken> TokenizeLine(string line, string language)
    {
        if (!SupportedExtensions.Contains(language))
            return [];
        if (language.Equals(".java", StringComparison.OrdinalIgnoreCase))
            return JavaTokens.Matches(line)
                .Select(match => new SyntaxToken(match.Index, match.Length, Classify(match))).ToArray();
        var comment = language is ".py" or ".sh" or ".bash" or ".yaml" or ".yml" or ".toml" or ".rb" or ".pl"
            ? @"(?<comment>\#.*$)|"
            : @"(?<comment>//.*$|/\*.*?\*/)|";
        var pattern = comment +
                      @"(?<string>@?""(?:\\.|[^""])*""|'(?:\\.|[^'])*'|`[^`]*`)|" +
                      @"(?<number>\b\d+(?:\.\d+)?\b)|" +
                      $@"(?<keyword>\b(?:{Keywords})\b)|" +
                      @"(?<type>\b[A-Z][A-Za-z0-9_]*\b)|" +
                      @"(?<function>\b[A-Za-z_][A-Za-z0-9_]*(?=\s*\())";
        return Regex.Matches(line, pattern, RegexOptions.CultureInvariant)
            .Select(match => new SyntaxToken(match.Index, match.Length, Classify(match)))
            .ToArray();
    }

    private static SyntaxTokenKind Classify(Match match)
    {
        if (match.Groups["comment"].Success) return SyntaxTokenKind.Comment;
        if (match.Groups["string"].Success) return SyntaxTokenKind.String;
        if (match.Groups["number"].Success) return SyntaxTokenKind.Number;
        if (match.Groups["keyword"].Success) return SyntaxTokenKind.Keyword;
        if (match.Groups["type"].Success) return SyntaxTokenKind.Type;
        return SyntaxTokenKind.Function;
    }
}
