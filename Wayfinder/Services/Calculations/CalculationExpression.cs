using System.Globalization;

namespace Wayfinder.Services.Calculations;

/// <summary>
/// Thrown for any parse or evaluation failure. Messages include enough position/name
/// context to be surfaced directly to blueprint authors.
/// </summary>
public sealed class CalculationException : Exception
{
    public CalculationException(string message) : base(message) { }
}

/// <summary>
/// Expression AST. The grammar (lowest to highest precedence):
///
///   or          ::= and ( "or" and )*
///   and         ::= not ( "and" not )*
///   not         ::= "not" not | comparison
///   comparison  ::= additive ( ("=" | "&lt;&gt;" | "&lt;" | "&lt;=" | "&gt;" | "&gt;=") additive )?
///   additive    ::= multiplicative ( ("+" | "-") multiplicative )*
///   multiplicative ::= unary ( ("*" | "/") unary )*
///   unary       ::= "-" unary | primary
///   primary     ::= number | string | "true" | "false" | identifier-path
///                 | identifier "(" args ")" | "(" or ")"
///
/// Identifier paths are dotted (member.age). Strings use single quotes. Numbers are
/// invariant decimals. That is the entire language: no assignment, no loops, no
/// indexing, no member calls — every expression terminates.
///
/// Prose reference (grammar, functions, tables/series, showWhen, worked example):
/// docs/guides/calculation-language.md.
/// </summary>
public abstract record CalcNode
{
    public sealed record Number(decimal Value) : CalcNode;
    public sealed record Text(string Value) : CalcNode;
    public sealed record Bool(bool Value) : CalcNode;
    public sealed record Identifier(string Path) : CalcNode;
    public sealed record Unary(string Op, CalcNode Operand) : CalcNode;
    public sealed record Binary(string Op, CalcNode Left, CalcNode Right) : CalcNode;
    public sealed record FunctionCall(string Name, IReadOnlyList<CalcNode> Args) : CalcNode;
}

/// <summary>Parses calculation expressions into <see cref="CalcNode"/> trees.</summary>
public static class CalculationExpressionParser
{
    private sealed record Token(string Kind, string Value, int Position);

    // The parser and evaluator are recursive, and a stack overflow cannot be caught in .NET: it ends
    // the process. A blueprint is untrusted input, so the shape of an expression is bounded before
    // any recursion starts. Real expressions are a tiny fraction of these (the longest in the
    // reference blueprints is ~120 characters). Mirrored in wayfinder-calculations.js.
    public const int MaxExpressionLength = 10_000;
    public const int MaxTokens = 1_000;
    public const int MaxNestingDepth = 64;

    public static CalcNode Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new CalculationException("Expression is empty.");
        }

        if (expression.Length > MaxExpressionLength)
        {
            throw new CalculationException(
                $"Expression is too long ({expression.Length} characters); the limit is {MaxExpressionLength}.");
        }

        var tokens = Tokenize(expression);
        EnforceShapeLimits(tokens);
        var index = 0;
        var node = ParseOr(tokens, ref index);
        if (index < tokens.Count)
        {
            throw new CalculationException(
                $"Unexpected '{tokens[index].Value}' at position {tokens[index].Position} in: {expression}");
        }

        return node;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new Token("number", text[start..i], start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new Token("identifier", text[start..i], start));
                continue;
            }

            if (c == '\'')
            {
                var start = ++i;
                while (i < text.Length && text[i] != '\'')
                {
                    i++;
                }

                if (i >= text.Length)
                {
                    throw new CalculationException($"Unterminated string starting at position {start - 1}.");
                }

                tokens.Add(new Token("string", text[start..i], start - 1));
                i++;
                continue;
            }

            if (c == '<' && i + 1 < text.Length && (text[i + 1] == '=' || text[i + 1] == '>'))
            {
                tokens.Add(new Token("op", text.Substring(i, 2), i));
                i += 2;
                continue;
            }

            if (c == '>' && i + 1 < text.Length && text[i + 1] == '=')
            {
                tokens.Add(new Token("op", ">=", i));
                i += 2;
                continue;
            }

            if ("+-*/()=<>,".Contains(c))
            {
                tokens.Add(new Token("op", c.ToString(), i));
                i++;
                continue;
            }

            throw new CalculationException($"Unexpected character '{c}' at position {i}.");
        }

        return tokens;
    }

    /// <summary>
    /// Rejects an expression whose token count, parenthesis nesting or run of prefix operators
    /// (<c>not not …</c>, <c>- - …</c>) would make the recursive parser or evaluator descend too deep.
    /// Binary operator chains are left-nested, so the token cap bounds their depth too.
    /// </summary>
    private static void EnforceShapeLimits(List<Token> tokens)
    {
        if (tokens.Count > MaxTokens)
        {
            throw new CalculationException(
                $"Expression is too long ({tokens.Count} tokens); the limit is {MaxTokens}.");
        }

        var depth = 0;
        var prefixRun = 0;
        foreach (var token in tokens)
        {
            if (token is { Kind: "op", Value: "(" })
            {
                depth++;
            }
            else if (token is { Kind: "op", Value: ")" })
            {
                depth--;
            }

            prefixRun = token is { Kind: "op", Value: "-" } or { Kind: "identifier", Value: "not" } ? prefixRun + 1 : 0;

            if (depth > MaxNestingDepth || prefixRun > MaxNestingDepth)
            {
                throw new CalculationException(
                    $"Expression is nested too deeply; the limit is {MaxNestingDepth} levels (at position {token.Position}).");
            }
        }
    }

    private static CalcNode ParseOr(List<Token> tokens, ref int index)
    {
        var left = ParseAnd(tokens, ref index);
        while (PeekIdentifier(tokens, index) == "or")
        {
            index++;
            left = new CalcNode.Binary("or", left, ParseAnd(tokens, ref index));
        }

        return left;
    }

    private static CalcNode ParseAnd(List<Token> tokens, ref int index)
    {
        var left = ParseNot(tokens, ref index);
        while (PeekIdentifier(tokens, index) == "and")
        {
            index++;
            left = new CalcNode.Binary("and", left, ParseNot(tokens, ref index));
        }

        return left;
    }

    private static CalcNode ParseNot(List<Token> tokens, ref int index)
    {
        if (PeekIdentifier(tokens, index) == "not")
        {
            index++;
            return new CalcNode.Unary("not", ParseNot(tokens, ref index));
        }

        return ParseComparison(tokens, ref index);
    }

    private static CalcNode ParseComparison(List<Token> tokens, ref int index)
    {
        var left = ParseAdditive(tokens, ref index);
        if (index < tokens.Count && tokens[index].Kind == "op"
            && tokens[index].Value is "=" or "<>" or "<" or "<=" or ">" or ">=")
        {
            var op = tokens[index].Value;
            index++;
            return new CalcNode.Binary(op, left, ParseAdditive(tokens, ref index));
        }

        return left;
    }

    private static CalcNode ParseAdditive(List<Token> tokens, ref int index)
    {
        var left = ParseMultiplicative(tokens, ref index);
        while (index < tokens.Count && tokens[index].Kind == "op" && tokens[index].Value is "+" or "-")
        {
            var op = tokens[index].Value;
            index++;
            left = new CalcNode.Binary(op, left, ParseMultiplicative(tokens, ref index));
        }

        return left;
    }

    private static CalcNode ParseMultiplicative(List<Token> tokens, ref int index)
    {
        var left = ParseUnary(tokens, ref index);
        while (index < tokens.Count && tokens[index].Kind == "op" && tokens[index].Value is "*" or "/")
        {
            var op = tokens[index].Value;
            index++;
            left = new CalcNode.Binary(op, left, ParseUnary(tokens, ref index));
        }

        return left;
    }

    private static CalcNode ParseUnary(List<Token> tokens, ref int index)
    {
        if (index < tokens.Count && tokens[index].Kind == "op" && tokens[index].Value == "-")
        {
            index++;
            return new CalcNode.Unary("-", ParseUnary(tokens, ref index));
        }

        return ParsePrimary(tokens, ref index);
    }

    private static CalcNode ParsePrimary(List<Token> tokens, ref int index)
    {
        if (index >= tokens.Count)
        {
            throw new CalculationException("Unexpected end of expression.");
        }

        var token = tokens[index];

        if (token.Kind == "number")
        {
            if (!decimal.TryParse(token.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            {
                throw new CalculationException($"Invalid number '{token.Value}' at position {token.Position}.");
            }

            index++;
            return new CalcNode.Number(number);
        }

        if (token.Kind == "string")
        {
            index++;
            return new CalcNode.Text(token.Value);
        }

        if (token.Kind == "identifier")
        {
            switch (token.Value)
            {
                case "true":
                    index++;
                    return new CalcNode.Bool(true);
                case "false":
                    index++;
                    return new CalcNode.Bool(false);
            }

            // Function call?
            if (index + 1 < tokens.Count && tokens[index + 1] is { Kind: "op", Value: "(" })
            {
                var name = token.Value;
                if (name.Contains('.'))
                {
                    throw new CalculationException($"'{name}' is not a valid function name.");
                }

                index += 2;
                var args = new List<CalcNode>();
                if (!(tokens[index] is { Kind: "op", Value: ")" }))
                {
                    while (true)
                    {
                        args.Add(ParseOr(tokens, ref index));
                        if (index < tokens.Count && tokens[index] is { Kind: "op", Value: "," })
                        {
                            index++;
                            continue;
                        }

                        break;
                    }
                }

                if (index >= tokens.Count || tokens[index] is not { Kind: "op", Value: ")" })
                {
                    throw new CalculationException($"Missing ')' for function '{name}'.");
                }

                index++;
                return new CalcNode.FunctionCall(name, args);
            }

            index++;
            return new CalcNode.Identifier(token.Value);
        }

        if (token is { Kind: "op", Value: "(" })
        {
            index++;
            var inner = ParseOr(tokens, ref index);
            if (index >= tokens.Count || tokens[index] is not { Kind: "op", Value: ")" })
            {
                throw new CalculationException("Missing closing ')'.");
            }

            index++;
            return inner;
        }

        throw new CalculationException($"Unexpected '{token.Value}' at position {token.Position}.");
    }

    private static string? PeekIdentifier(List<Token> tokens, int index) =>
        index < tokens.Count && tokens[index].Kind == "identifier" ? tokens[index].Value : null;
}
