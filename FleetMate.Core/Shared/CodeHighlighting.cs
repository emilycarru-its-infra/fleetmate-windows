using System.Text.RegularExpressions;

namespace FleetMate.Core.Shared;

// Ported from FleetMate for Mac, which adapted MunkiStudio's ScriptLanguage
// and ScriptHighlighter (Apache-2.0). The same lightweight approach —
// comments, strings, keywords and numbers by regular expression, no grammar —
// with detection by file extension, and comments and strings resolved in one
// left-to-right pass so a quote inside a comment (or a comment marker inside
// a string) never paints the wrong span.

/// <summary>A language the editor colours.</summary>
public enum CodeLanguage { Shell, Python, Ruby, Perl, Swift, CSharp, PowerShell, JavaScript, Go, Hcl, Yaml, Json, Xml, PlainText }

public static class CodeLanguages
{
    /// <summary>By file name first, then by shebang or an XML prolog.</summary>
    public static CodeLanguage Detect(string path, string source)
    {
        var name = Path.GetFileName(path.Replace('\\', '/')).ToLowerInvariant();
        var ext = Path.GetExtension(name).TrimStart('.');
        switch (ext)
        {
            case "sh" or "bash" or "zsh" or "command": return CodeLanguage.Shell;
            case "py": return CodeLanguage.Python;
            case "rb": return CodeLanguage.Ruby;
            case "pl" or "pm": return CodeLanguage.Perl;
            case "swift": return CodeLanguage.Swift;
            case "cs" or "csx": return CodeLanguage.CSharp;
            case "ps1" or "psm1" or "psd1": return CodeLanguage.PowerShell;
            case "js" or "jsx" or "ts" or "tsx" or "mjs" or "cjs": return CodeLanguage.JavaScript;
            case "go": return CodeLanguage.Go;
            case "tf" or "tfvars" or "hcl": return CodeLanguage.Hcl;
            case "yml" or "yaml": return CodeLanguage.Yaml;
            case "json" or "jsonc": return CodeLanguage.Json;
            case "xml" or "plist" or "csproj" or "props" or "targets" or "xaml" or "wxs" or "wixproj" or "recipe"
                or "pkginfo" or "mobileconfig" or "config" or "manifest": return CodeLanguage.Xml;
            case "md" or "markdown" or "txt": return CodeLanguage.PlainText;
        }
        if (name is "makefile" or "dockerfile" || (name.StartsWith('.') && !name[1..].Contains('.'))) return CodeLanguage.Shell;
        var head = source.Length > 200 ? source[..200] : source;
        var firstLine = head.Split('\n', 2)[0];
        if (firstLine.StartsWith("#!"))
        {
            var lower = firstLine.ToLowerInvariant();
            if (lower.Contains("python")) return CodeLanguage.Python;
            if (lower.Contains("ruby")) return CodeLanguage.Ruby;
            if (lower.Contains("perl")) return CodeLanguage.Perl;
            if (lower.Contains("swift")) return CodeLanguage.Swift;
            if (lower.Contains("pwsh")) return CodeLanguage.PowerShell;
            if (lower.Contains("node")) return CodeLanguage.JavaScript;
            return CodeLanguage.Shell;
        }
        var trimmed = head.Trim();
        if (trimmed.StartsWith("<?xml") || trimmed.StartsWith("<!DOCTYPE") || trimmed.StartsWith("<plist")) return CodeLanguage.Xml;
        return CodeLanguage.PlainText;
    }

    public static string? LineCommentPrefix(this CodeLanguage language) => language switch
    {
        CodeLanguage.Shell or CodeLanguage.Python or CodeLanguage.Ruby or CodeLanguage.Perl
            or CodeLanguage.PowerShell or CodeLanguage.Yaml or CodeLanguage.Hcl => "#",
        CodeLanguage.Swift or CodeLanguage.CSharp or CodeLanguage.JavaScript or CodeLanguage.Go => "//",
        _ => null,
    };

    public static IReadOnlyList<string> Keywords(this CodeLanguage language) => language switch
    {
        CodeLanguage.Shell => new[] { "if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac", "function",
            "return", "in", "until", "exit", "export", "local", "readonly", "set", "unset", "shift", "source" },
        CodeLanguage.Python => new[] { "def", "class", "if", "elif", "else", "for", "while", "try", "except", "finally", "with", "as",
            "import", "from", "return", "yield", "pass", "break", "continue", "raise", "lambda", "and", "or",
            "not", "in", "is", "True", "False", "None", "global", "nonlocal", "async", "await" },
        CodeLanguage.Ruby => new[] { "def", "class", "module", "if", "elsif", "else", "unless", "case", "when", "while", "until", "for",
            "in", "do", "end", "begin", "rescue", "ensure", "raise", "return", "yield", "self", "nil", "true",
            "false", "and", "or", "not", "require", "include" },
        CodeLanguage.Perl => new[] { "use", "my", "our", "local", "sub", "if", "elsif", "else", "unless", "while", "until", "for",
            "foreach", "do", "return", "die", "warn", "print", "printf" },
        CodeLanguage.Swift => new[] { "import", "func", "var", "let", "class", "struct", "enum", "protocol", "extension", "if", "else",
            "guard", "for", "while", "do", "try", "throws", "throw", "return", "switch", "case", "default", "break",
            "continue", "true", "false", "nil", "self", "Self", "async", "await", "actor", "where", "in",
            "private", "public", "internal", "fileprivate", "static", "final", "init", "some", "any" },
        CodeLanguage.CSharp => new[] { "using", "namespace", "class", "struct", "interface", "enum", "record", "public", "private",
            "protected", "internal", "static", "readonly", "const", "var", "new", "return", "if", "else", "for",
            "foreach", "while", "do", "switch", "case", "default", "break", "continue", "try", "catch", "finally",
            "throw", "async", "await", "true", "false", "null", "this", "void", "string", "int", "bool", "in" },
        CodeLanguage.PowerShell => new[] { "function", "param", "if", "elseif", "else", "foreach", "for", "while", "do", "switch", "return",
            "try", "catch", "finally", "throw", "begin", "process", "end", "in" },
        CodeLanguage.JavaScript => new[] { "import", "export", "from", "const", "let", "var", "function", "return", "if", "else", "for", "while",
            "do", "switch", "case", "default", "break", "continue", "try", "catch", "finally", "throw", "new",
            "class", "extends", "async", "await", "true", "false", "null", "undefined", "this", "type", "interface" },
        CodeLanguage.Go => new[] { "package", "import", "func", "var", "const", "type", "struct", "interface", "map", "chan", "if",
            "else", "for", "range", "switch", "case", "default", "return", "go", "defer", "select", "true",
            "false", "nil" },
        CodeLanguage.Hcl => new[] { "resource", "data", "variable", "output", "locals", "module", "provider", "terraform", "for_each",
            "count", "depends_on", "lifecycle", "dynamic", "true", "false", "null", "for", "in", "if" },
        CodeLanguage.Yaml or CodeLanguage.Json => new[] { "true", "false", "null" },
        _ => Array.Empty<string>(),
    };
}

public enum CodeTokenKind { Comment, String, Keyword, Number }

/// <summary>A highlighted span: <c>Start</c> and <c>Length</c> index the source string.</summary>
public readonly record struct CodeToken(CodeTokenKind Kind, int Start, int Length);

/// <summary>Finds the spans the editor colours. Pure, so it is tested without a view.</summary>
public static class CodeHighlighter
{
    /// <summary>Sources larger than this are shown uncoloured: re-colouring as you type has to stay instant.</summary>
    public const int SizeLimit = 300_000;

    private static readonly Regex Numbers = new(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled);

    public static List<CodeToken> Tokens(string source, CodeLanguage language)
    {
        var tokens = new List<CodeToken>();
        if (language == CodeLanguage.PlainText || source.Length > SizeLimit) return tokens;
        var claimed = new bool[source.Length];

        // Comments and strings in one pass: the leftmost match wins, so a quote
        // inside a comment and a comment marker inside a string are both read correctly.
        var alternatives = new List<string> { "\"(?:\\\\.|[^\"\\\\\\n])*\"", "'(?:\\\\.|[^'\\\\\\n])*'" };
        if (language.LineCommentPrefix() is { } prefix) alternatives.Insert(0, Regex.Escape(prefix) + "[^\\n]*");
        if (language == CodeLanguage.Xml) alternatives.Insert(0, @"<!--[\s\S]*?-->");
        if (language is CodeLanguage.Swift or CodeLanguage.CSharp or CodeLanguage.JavaScript or CodeLanguage.Go)
            alternatives.Insert(0, @"/\*[\s\S]*?\*/");
        var combined = new Regex(string.Join("|", alternatives.Select(a => "(" + a + ")")));
        foreach (Match match in combined.Matches(source))
        {
            var isString = match.Value.StartsWith('"') || match.Value.StartsWith('\'');
            tokens.Add(new CodeToken(isString ? CodeTokenKind.String : CodeTokenKind.Comment, match.Index, match.Length));
            Array.Fill(claimed, true, match.Index, match.Length);
        }

        void AddUnclaimed(Regex regex, CodeTokenKind kind)
        {
            foreach (Match match in regex.Matches(source))
            {
                if (Array.IndexOf(claimed, true, match.Index, match.Length) >= 0) continue;
                tokens.Add(new CodeToken(kind, match.Index, match.Length));
            }
        }
        var keywords = language.Keywords();
        if (keywords.Count > 0) AddUnclaimed(new Regex(@"\b(" + string.Join("|", keywords.Select(Regex.Escape)) + @")\b"), CodeTokenKind.Keyword);
        AddUnclaimed(Numbers, CodeTokenKind.Number);
        return tokens.OrderBy(t => t.Start).ToList();
    }
}
