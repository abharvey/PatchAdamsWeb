# Syntax highlighting support and roadmap

Syntax coloring is offline lexical highlighting, not a compiler, language server,
or semantic parser. Source and patch content is never executed. The filename
extension selects the coloring rules automatically; unknown extensions stay plain.

## Current inventory

Java now has its own keyword, primitive-type, annotation, literal, and identifier
rules. All other recognized languages use the original lightweight shared lexer.
Recognition is not a claim of complete grammar support.

| Language or format | Recognized extensions | Current level |
| --- | --- | --- |
| Java | `.java` | Java-specific, line-based coloring |
| C# | `.cs` | Basic shared lexer |
| C | `.c`, `.h` | Basic shared lexer |
| C++ | `.cc`, `.cpp`, `.hpp`; `.h` is shared with C | Basic shared lexer |
| Go | `.go` | Basic shared lexer |
| Rust | `.rs` | Basic shared lexer |
| Python | `.py` | Basic shared lexer |
| JavaScript / JSX | `.js`, `.jsx` | Basic shared lexer |
| TypeScript / TSX | `.ts`, `.tsx` | Basic shared lexer |
| Swift | `.swift` | Basic shared lexer |
| Zig | `.zig` | Basic shared lexer |
| Shell | `.sh`, `.bash` | Basic shared lexer |
| YAML | `.yaml`, `.yml` | Basic shared lexer |
| TOML | `.toml` | Basic shared lexer |
| JSON | `.json` | Basic shared lexer |
| Markdown | `.md`, `.markdown` | Generic token coloring, not Markdown-specific styling |
| CSS | `.css` | Basic shared lexer |
| HTML | `.html` | Basic shared lexer |
| XML | `.xml` | Basic shared lexer |

## Java coverage

- Reserved and contextual words, including records, sealed types, permits,
  pattern matching, modules, and switch expressions.
- Primitive types, capitalized type-name heuristics, annotations, and method calls.
- Strings with escaped quotes, character literals, and inline/line comments.
- Underscored numbers, integer suffixes, hexadecimal/binary/octal forms,
  decimal exponents, and hexadecimal floating-point forms.
- Case-insensitive extension detection and Java identifier boundaries, including
  `$` and Unicode letters.
- Coverage in `Fixtures/syntax/java/ReviewExample.java` and regression tests.

The lexer operates one displayed line at a time. Multiline comment continuation,
Java text blocks, Unicode escape preprocessing, and semantic resolution are not
implemented. Contextual words and capitalized identifiers are colored heuristically.
These limits also matter in a diff: removed and added lines can belong to different
source versions, so future multiline support must track the two sides separately.

## Requested language backlog

Java is the immediate addition. The following languages are requested for future
work; existing basic coloring should be improved rather than described as absent.

| Language | Next work |
| --- | --- |
| C++ | Dedicated vocabulary, templates, preprocessor directives, raw strings, multiline comments; aliases such as `.cxx`, `.hxx`, `.hh` |
| C | Dedicated vocabulary, primitive types, macros, preprocessor directives, multiline comments |
| VB.NET | Add `.vb`; case-insensitive vocabulary, apostrophe/REM comments, doubled-quote strings |
| Go | Dedicated vocabulary, built-in types, raw strings, multiline comments |
| Rust | Dedicated vocabulary, lifetimes, macros, raw strings, nested block comments |
| PHP | Add `.php` / `.phtml`; variables, PHP tags, comments, heredoc/nowdoc, mixed markup |
| C# | Dedicated vocabulary, attributes, generics, interpolated/verbatim/raw strings; consider `.csx` |
| Kotlin | Add `.kt` / `.kts`; nullable types, annotations, string templates, raw strings, nested comments |

Further candidates to prioritize later: SQL, PowerShell, Ruby, F#, Scala, Lua,
SCSS/Less, and filename-based recognition for Dockerfiles and Makefiles.

## Quality bar for future additions

Each addition should include a real source fixture, exact token-span checks,
comment/string precedence tests, identifier-boundary tests, and a code-view test.
Keep language-specific rules isolated, preserve unknown-file fallback, and use
the existing theme token colors. Multiline state must not leak between base and
post-patch versions or across collapsed regions.
