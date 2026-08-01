using System.Globalization;
using System.Text;

namespace Shubbak.Config.Kdl;

/// <summary>The result of parsing a KDL document.</summary>
/// <param name="Document">The parsed document; empty when parsing failed.</param>
/// <param name="Diagnostics">Everything found, errors and warnings alike.</param>
public readonly record struct KdlParseResult(KdlDocument Document, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasErrors
    {
        get
        {
            foreach (Diagnostic d in Diagnostics)
                if (d.Severity == DiagnosticSeverity.Error) return true;

            return false;
        }
    }
}

/// <summary>
/// A recursive-descent parser for the subset of KDL that Shubbak's config uses.
/// </summary>
/// <remarks>
/// <para>
/// Supports nodes, positional arguments, <c>key=value</c> properties, children
/// blocks, quoted and raw strings, numbers in all four bases, booleans, null,
/// line and block comments, <c>/-</c> slashdash comments, and line continuations.
/// </para>
/// <para>
/// It <b>collects</b> diagnostics instead of throwing, and recovers by skipping to
/// the next plausible node boundary. Reporting one error per run would make fixing
/// a config a slow game of whack-a-mole; reporting all of them at once is the whole
/// point of writing a parser rather than reaching for a generic format.
/// </para>
/// </remarks>
public sealed class KdlParser
{
    private readonly string _source;
    private readonly List<Diagnostic> _diagnostics = [];

    private int _offset;
    private int _line = 1;
    private int _column = 1;

    private KdlParser(string source) => _source = source;

    /// <summary>Parses a KDL document.</summary>
    public static KdlParseResult Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var parser = new KdlParser(source);
        List<KdlNode> nodes = parser.ParseNodes(depth: 0);

        return new KdlParseResult(new KdlDocument { Nodes = nodes }, parser._diagnostics);
    }

    // ---- character helpers -------------------------------------------------

    private bool AtEnd => _offset >= _source.Length;

    private char Current => _offset < _source.Length ? _source[_offset] : '\0';

    private char Peek(int ahead = 1) =>
        _offset + ahead < _source.Length ? _source[_offset + ahead] : '\0';

    private TextPosition Position => new(_line, _column, _offset);

    private void Advance()
    {
        if (AtEnd) return;

        if (_source[_offset] == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }

        _offset++;
    }

    private TextSpan SpanFrom(TextPosition start) => new(start, _offset - start.Offset);

    private void Report(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    // ---- trivia ------------------------------------------------------------

    /// <summary>Skips whitespace and comments, optionally including newlines.</summary>
    private void SkipTrivia(bool includeNewlines)
    {
        while (!AtEnd)
        {
            char c = Current;

            if (c is ' ' or '\t' or '\r')
            {
                Advance();
                continue;
            }

            if (c == '\n')
            {
                if (!includeNewlines) return;
                Advance();
                continue;
            }

            // Line continuation: a backslash makes the following newline invisible,
            // which is how a long node is wrapped across lines.
            if (c == '\\')
            {
                int save = _offset;
                Advance();
                while (!AtEnd && Current is ' ' or '\t' or '\r') Advance();

                if (Current == '\n')
                {
                    Advance();
                    continue;
                }

                _offset = save;
                return;
            }

            if (c == '/' && Peek() == '/')
            {
                while (!AtEnd && Current != '\n') Advance();
                continue;
            }

            if (c == '/' && Peek() == '*')
            {
                SkipBlockComment();
                continue;
            }

            return;
        }
    }

    private void SkipBlockComment()
    {
        TextPosition start = Position;
        Advance(); // '/'
        Advance(); // '*'

        // KDL block comments nest, unlike C's.
        int depth = 1;

        while (!AtEnd && depth > 0)
        {
            if (Current == '/' && Peek() == '*')
            {
                depth++;
                Advance();
                Advance();
            }
            else if (Current == '*' && Peek() == '/')
            {
                depth--;
                Advance();
                Advance();
            }
            else
            {
                Advance();
            }
        }

        if (depth > 0)
            Report(Diagnostic.Error("SHB0001", "Unterminated block comment.", SpanFrom(start)));
    }

    // ---- nodes -------------------------------------------------------------

    private List<KdlNode> ParseNodes(int depth)
    {
        List<KdlNode> nodes = [];

        while (true)
        {
            SkipTrivia(includeNewlines: true);

            if (AtEnd) break;

            if (Current == '}')
            {
                if (depth > 0) break;

                Report(Diagnostic.Error(
                    "SHB0002", "Unexpected '}' with no matching '{'.", new TextSpan(Position, 1)));
                Advance();
                continue;
            }

            if (Current == ';')
            {
                Advance();
                continue;
            }

            // '/-' comments out the entire next node.
            bool suppressed = false;
            if (Current == '/' && Peek() == '-')
            {
                Advance();
                Advance();
                SkipTrivia(includeNewlines: true);
                suppressed = true;
            }

            KdlNode? node = ParseNode(depth);
            if (node is not null && !suppressed) nodes.Add(node);
        }

        return nodes;
    }

    private KdlNode? ParseNode(int depth)
    {
        TextPosition start = Position;
        int before = _diagnostics.Count;

        string? name = ParseIdentifier(out TextSpan nameSpan);
        if (name is null)
        {
            if (!ReportedSince(before))
            {
                Report(Diagnostic.Error(
                    "SHB0003",
                    $"Expected a node name but found '{Describe(Current)}'.",
                    new TextSpan(Position, 1)));
            }

            RecoverToNextNode();
            return null;
        }

        List<KdlValue> arguments = [];
        Dictionary<string, KdlValue> properties = new(StringComparer.Ordinal);
        List<KdlNode> children = [];

        while (true)
        {
            SkipTrivia(includeNewlines: false);

            if (AtEnd || Current is '\n' or ';') break;

            if (Current == '}') break;

            if (Current == '{')
            {
                Advance();
                children = ParseNodes(depth + 1);

                SkipTrivia(includeNewlines: true);
                if (Current == '}')
                {
                    Advance();
                }
                else
                {
                    Report(Diagnostic.Error(
                        "SHB0004", "Unterminated block: expected '}'.", new TextSpan(start, 1)));
                }

                break;
            }

            // '/-' comments out the next argument, property or child block.
            bool suppressed = false;
            if (Current == '/' && Peek() == '-')
            {
                Advance();
                Advance();
                SkipTrivia(includeNewlines: false);
                suppressed = true;
            }

            if (!ParseEntry(arguments, properties, suppressed)) break;
        }

        if (!AtEnd && Current == ';') Advance();

        return new KdlNode
        {
            Name = name,
            NameSpan = nameSpan,
            Span = SpanFrom(start),
            Arguments = arguments,
            Properties = properties,
            Children = children,
        };
    }

    /// <summary>Parses one argument or property. Returns false to end the node.</summary>
    private bool ParseEntry(
        List<KdlValue> arguments, Dictionary<string, KdlValue> properties, bool suppressed)
    {
        TextPosition start = Position;
        int before = _diagnostics.Count;

        // A bare identifier followed by '=' is a property; otherwise it is a string
        // argument. Lookahead is needed because the two are indistinguishable until
        // the '=' appears.
        if (IsIdentifierStart(Current) && Current != '"')
        {
            int save = _offset;
            int saveLine = _line;
            int saveColumn = _column;

            string? identifier = ParseIdentifier(out TextSpan identifierSpan);

            // Allow whitespace around '=', which strict KDL does not. `title = "x"`
            // reads considerably better than `title="x"` in a matcher block, and
            // there is no ambiguity: '=' can never begin a value.
            SkipTrivia(includeNewlines: false);

            if (identifier is not null && Current == '=')
            {
                Advance();
                SkipTrivia(includeNewlines: false);

                KdlValue? value = ParseValue();
                if (value is null)
                {
                    Report(Diagnostic.Error(
                        "SHB0005",
                        $"Property '{identifier}' has no value.",
                        identifierSpan,
                        $"Write it as {identifier}=<value>, for example {identifier}=\"text\" or {identifier}=42."));

                    return false;
                }

                if (!suppressed)
                {
                    if (properties.ContainsKey(identifier))
                    {
                        Report(Diagnostic.Warning(
                            "SHB0006",
                            $"Property '{identifier}' is set more than once; the last value wins.",
                            identifierSpan));
                    }

                    properties[identifier] = value;
                }

                return true;
            }

            // Not a property after all, so rewind to before the identifier and let
            // it be re-read as a value.
            _offset = save;
            _line = saveLine;
            _column = saveColumn;
        }

        KdlValue? argument = ParseValue();
        if (argument is null)
        {
            // Only add a generic complaint if the value parser did not already say
            // something more specific. Cascading diagnostics - one real error
            // followed by three vague ones - make a config harder to fix, not easier.
            if (!ReportedSince(before))
            {
                Report(Diagnostic.Error(
                    "SHB0007",
                    $"Expected a value but found '{Describe(Current)}'.",
                    new TextSpan(start, 1)));
            }

            RecoverToNextNode();
            return false;
        }

        if (!suppressed) arguments.Add(argument);
        return true;
    }

    /// <summary>Whether any diagnostic has been reported since the mark.</summary>
    private bool ReportedSince(int mark) => _diagnostics.Count > mark;

    // ---- identifiers and values --------------------------------------------

    private static bool IsIdentifierStart(char c) =>
        !char.IsWhiteSpace(c) && c is not ('\0' or '{' or '}' or '=' or ';' or '"' or '(' or ')' or ',');

    private static bool IsIdentifierChar(char c) =>
        !char.IsWhiteSpace(c) && c is not ('\0' or '{' or '}' or '=' or ';' or '"' or '(' or ')' or ',');

    private string? ParseIdentifier(out TextSpan span)
    {
        TextPosition start = Position;

        // A quoted identifier lets a node or property name contain anything.
        if (Current == '"')
        {
            string? quoted = ParseQuotedString();
            span = SpanFrom(start);
            return quoted;
        }

        if (!IsIdentifierStart(Current))
        {
            span = new TextSpan(start, 0);
            return null;
        }

        var builder = new StringBuilder();
        while (!AtEnd && IsIdentifierChar(Current))
        {
            // A comment terminates a bare identifier.
            if (Current == '/' && Peek() is '/' or '*' or '-') break;
            builder.Append(Current);
            Advance();
        }

        span = SpanFrom(start);
        return builder.Length == 0 ? null : builder.ToString();
    }

    private KdlValue? ParseValue()
    {
        TextPosition start = Position;

        if (AtEnd) return null;

        // Matcher operator tokens: = ~= ^= $= *=
        //
        // These appear where a value is expected, in constructs like
        // `title ~= "regex"`. They cannot be lexed as identifiers because '=' is
        // excluded from identifier characters (it separates properties), so they
        // are recognised explicitly. Property syntax is unaffected: ParseEntry
        // tries the `key=value` form first, and only falls through to here when the
        // token genuinely sits in value position.
        if (ParseOperatorToken(start) is { } operatorToken) return operatorToken;

        if (Current == '"')
        {
            string? text = ParseQuotedString();
            if (text is null) return null;

            TextSpan span = SpanFrom(start);
            return new KdlValue
            {
                Kind = KdlValueKind.Text,
                Span = span,
                Raw = _source[start.Offset.._offset],
                StringValue = text,
            };
        }

        // Raw string: r"..." or r#"..."# with any number of hashes, so the content
        // needs no escaping. Invaluable for regexes, which are otherwise a thicket
        // of double backslashes.
        if (Current == 'r' && (Peek() == '"' || Peek() == '#'))
        {
            string? raw = ParseRawString();
            if (raw is not null)
            {
                return new KdlValue
                {
                    Kind = KdlValueKind.Text,
                    Span = SpanFrom(start),
                    Raw = _source[start.Offset.._offset],
                    StringValue = raw,
                };
            }
        }

        if (!IsIdentifierStart(Current)) return null;

        var builder = new StringBuilder();
        while (!AtEnd && IsIdentifierChar(Current))
        {
            if (Current == '/' && Peek() is '/' or '*' or '-') break;
            builder.Append(Current);
            Advance();
        }

        string token = builder.ToString();
        if (token.Length == 0) return null;

        TextSpan tokenSpan = SpanFrom(start);
        return Classify(token, tokenSpan);
    }

    /// <summary>
    /// Recognises a comparison operator sitting in value position.
    /// </summary>
    private KdlValue? ParseOperatorToken(TextPosition start)
    {
        char c = Current;
        char next = Peek();

        string? token = c switch
        {
            '=' when next != '=' => "=",
            '=' => "==",
            '~' when next == '=' => "~=",
            '^' when next == '=' => "^=",
            '$' when next == '=' => "$=",
            '*' when next == '=' => "*=",
            _ => null,
        };

        if (token is null) return null;

        for (int i = 0; i < token.Length; i++) Advance();

        return new KdlValue
        {
            Kind = KdlValueKind.Text,
            Span = SpanFrom(start),
            Raw = token,
            StringValue = token,
        };
    }

    /// <summary>Turns a bare token into a typed value.</summary>
    private static KdlValue Classify(string token, TextSpan span)
    {
        // KDL 2.0 spells keywords with a '#'; KDL 1.0 does not. Both are accepted,
        // because the distinction is invisible to a user copying an example.
        switch (token)
        {
            case "true" or "#true":
                return new KdlValue { Kind = KdlValueKind.Boolean, Span = span, Raw = token, BooleanValue = true };
            case "false" or "#false":
                return new KdlValue { Kind = KdlValueKind.Boolean, Span = span, Raw = token, BooleanValue = false };
            case "null" or "#null":
                return new KdlValue { Kind = KdlValueKind.Null, Span = span, Raw = token };
        }

        if (TryParseNumber(token, out double number, out bool isInteger))
        {
            return new KdlValue
            {
                Kind = KdlValueKind.Number,
                Span = span,
                Raw = token,
                NumberValue = number,
                IsInteger = isInteger,
            };
        }

        // Anything else is a bare string. Keeping this permissive is what lets
        // workspace names like `-` and `\` be written without quotes.
        return new KdlValue
        {
            Kind = KdlValueKind.Text,
            Span = span,
            Raw = token,
            StringValue = token,
        };
    }

    private static bool TryParseNumber(string token, out double value, out bool isInteger)
    {
        value = 0;
        isInteger = false;

        if (token.Length == 0) return false;

        // Underscores are legal digit separators in KDL.
        string cleaned = token.Replace("_", "", StringComparison.Ordinal);
        if (cleaned.Length == 0) return false;

        bool negative = cleaned[0] == '-';
        if (negative || cleaned[0] == '+') cleaned = cleaned[1..];

        if (cleaned.Length == 0) return false;

        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return TryParseRadix(cleaned[2..], 16, negative, ref value, ref isInteger);

        if (cleaned.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            return TryParseRadix(cleaned[2..], 8, negative, ref value, ref isInteger);

        if (cleaned.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return TryParseRadix(cleaned[2..], 2, negative, ref value, ref isInteger);

        if (long.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out long integer))
        {
            value = negative ? -integer : integer;
            isInteger = true;
            return true;
        }

        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
        {
            value = negative ? -real : real;
            isInteger = false;
            return true;
        }

        return false;
    }

    private static bool TryParseRadix(string digits, int radix, bool negative, ref double value, ref bool isInteger)
    {
        if (digits.Length == 0) return false;

        long accumulator = 0;

        foreach (char c in digits)
        {
            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };

            if (digit < 0 || digit >= radix) return false;

            accumulator = (accumulator * radix) + digit;
        }

        value = negative ? -accumulator : accumulator;
        isInteger = true;
        return true;
    }

    private string? ParseQuotedString()
    {
        TextPosition start = Position;
        Advance(); // opening quote

        var builder = new StringBuilder();

        while (true)
        {
            if (AtEnd)
            {
                Report(Diagnostic.Error(
                    "SHB0008", "Unterminated string.", SpanFrom(start),
                    "Add a closing double quote."));
                return null;
            }

            char c = Current;

            if (c == '"')
            {
                Advance();
                return builder.ToString();
            }

            if (c == '\n')
            {
                Report(Diagnostic.Error(
                    "SHB0009", "A string cannot span lines.", SpanFrom(start),
                    "Use a raw string r\"...\" or escape the newline as \\n."));
                return null;
            }

            if (c == '\\')
            {
                Advance();
                if (AtEnd) continue;

                char escape = Current;
                Advance();

                switch (escape)
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case '\\': builder.Append('\\'); break;
                    case '"': builder.Append('"'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 's': builder.Append(' '); break;
                    case 'u': AppendUnicodeEscape(builder); break;
                    default:
                        Report(Diagnostic.Warning(
                            "SHB0010",
                            $"Unknown escape sequence '\\{escape}'; the backslash is kept literally.",
                            new TextSpan(Position, 2),
                            "Use a raw string r\"...\" if you meant a literal backslash."));
                        builder.Append('\\').Append(escape);
                        break;
                }

                continue;
            }

            builder.Append(c);
            Advance();
        }
    }

    private void AppendUnicodeEscape(StringBuilder builder)
    {
        // KDL spells these \u{1F600}.
        if (Current != '{')
        {
            Report(Diagnostic.Warning(
                "SHB0011", "Expected '{' after \\u.", new TextSpan(Position, 1),
                "Unicode escapes are written \\u{1F600}."));
            return;
        }

        Advance();
        var hex = new StringBuilder();

        while (!AtEnd && Current != '}' && hex.Length < 6)
        {
            hex.Append(Current);
            Advance();
        }

        if (Current == '}') Advance();

        if (int.TryParse(hex.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint) &&
            codePoint is >= 0 and <= 0x10FFFF)
        {
            builder.Append(char.ConvertFromUtf32(codePoint));
        }
        else
        {
            Report(Diagnostic.Warning(
                "SHB0012", $"Invalid Unicode escape '\\u{{{hex}}}'.", new TextSpan(Position, hex.Length)));
        }
    }

    private string? ParseRawString()
    {
        TextPosition start = Position;
        Advance(); // 'r'

        int hashes = 0;
        while (Current == '#')
        {
            hashes++;
            Advance();
        }

        if (Current != '"')
        {
            // Not a raw string after all - rewind so it parses as a bare token.
            _offset = start.Offset;
            _line = start.Line;
            _column = start.Column;
            return null;
        }

        Advance(); // opening quote

        var builder = new StringBuilder();

        while (true)
        {
            if (AtEnd)
            {
                Report(Diagnostic.Error(
                    "SHB0013", "Unterminated raw string.", SpanFrom(start)));
                return null;
            }

            if (Current == '"')
            {
                // The closing delimiter is a quote followed by the same number of
                // hashes that opened it.
                int save = _offset;
                Advance();

                int seen = 0;
                while (seen < hashes && Current == '#')
                {
                    seen++;
                    Advance();
                }

                if (seen == hashes) return builder.ToString();

                _offset = save;
                builder.Append('"');
                Advance();
                continue;
            }

            builder.Append(Current);
            Advance();
        }
    }

    // ---- recovery ----------------------------------------------------------

    /// <summary>
    /// Skips to the next line or block boundary after an error.
    /// </summary>
    /// <remarks>
    /// Recovering rather than aborting is what allows every error in a file to be
    /// reported in one run, instead of forcing the user through one fix-and-retry
    /// cycle per mistake.
    /// </remarks>
    private void RecoverToNextNode()
    {
        while (!AtEnd && Current is not ('\n' or '}' or ';')) Advance();
        if (!AtEnd && Current == ';') Advance();
    }

    private static string Describe(char c) => c switch
    {
        '\0' => "end of file",
        '\n' => "end of line",
        '\t' => "tab",
        ' ' => "space",
        _ => c.ToString(),
    };
}
