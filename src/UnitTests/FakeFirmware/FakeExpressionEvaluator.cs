using System;
using System.Globalization;
using System.Text;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Error of the fake expression evaluator, the counterpart of RRF's GCodeException
/// </summary>
/// <param name="message">Error message</param>
internal sealed class FakeExpressionException(string message) : Exception(message);

/// <summary>
/// Small evaluator for the meta G-code expressions the fake firmware tests need. It follows the operator priorities,
/// short-circuit evaluation and type rules of RRF's ExpressionParser::ParseInternal (src/GCodes/GCodeBuffer/ExpressionParser.cpp) for:
/// <list type="bullet">
/// <item>literals: integers, floats, "strings" (with "" escapes), true, false, null</item>
/// <item>identifiers: var.*, global.*, param.*, object model paths like move.axes[0].letter</item>
/// <item>unary operators - + ! # and brackets ( )</item>
/// <item>binary operators ? : ^ &amp; &amp;&amp; | || = == != &lt; &lt;= &gt; &gt;= + - * /</item>
/// <item>the function exists()</item>
/// </list>
/// Not supported: other functions, array literals, indexing into variables or strings, characters, DateTime values,
/// iterations/line, and the float precision handling of RRF (results are plain floats).
/// Error messages carry the RRF message text where known but no column information
/// </summary>
/// <param name="resolve">Resolve an identifier. Must throw a <see cref="FakeExpressionException"/> if it does not exist</param>
/// <param name="exists">Check if an identifier exists</param>
internal sealed class FakeExpressionEvaluator(Func<string, object?> resolve, Func<string, bool> exists)
{
    /// <summary>
    /// Binary operators and their priorities, see ExpressionParser::ParseInternal
    /// </summary>
    private const string Operators = "?^&|!=<>+-*/";
    private static readonly int[] Priorities = [1, 2, 3, 3, 4, 4, 4, 4, 5, 5, 6, 6];
    private const int UnaryPriority = 10;

    private string _expression = string.Empty;
    private int _pos;

    /// <summary>
    /// Evaluate an expression
    /// </summary>
    /// <param name="expression">Expression to evaluate</param>
    /// <returns>Result as int, float, bool, string or null, or a value from the object model</returns>
    /// <exception cref="FakeExpressionException">Failed to evaluate the expression</exception>
    public object? Evaluate(string expression)
    {
        _expression = expression.Trim();
        if (_expression.StartsWith('{') && _expression.EndsWith('}'))
        {
            // DSF encapsulates fully expanded expressions in curly braces
            _expression = _expression[1..^1];
        }
        _pos = 0;

        object? result = Parse(0, true);
        if (SkipWhiteSpace() != '\0')
        {
            throw new FakeExpressionException("unexpected characters after expression");
        }
        return result;
    }

    private char Current => (_pos < _expression.Length) ? _expression[_pos] : '\0';

    private char SkipWhiteSpace()
    {
        while (_pos < _expression.Length && char.IsWhiteSpace(_expression[_pos]))
        {
            _pos++;
        }
        return Current;
    }

    /// <summary>
    /// Parse an operand followed by binary operators with a higher priority than the given one
    /// </summary>
    /// <param name="priority">Stop before operators with this priority or lower</param>
    /// <param name="evaluate">Whether values are evaluated. Operands skipped by short-circuit evaluation are only parsed</param>
    private object? Parse(int priority, bool evaluate)
    {
        object? value = ParseOperand(evaluate);
        while (true)
        {
            char opChar = SkipWhiteSpace();
            int index = (opChar == '\0') ? -1 : Operators.IndexOf(opChar);
            if (index < 0 || Priorities[index] <= priority)
            {
                return value;
            }
            int opPriority = Priorities[index];
            _pos++;

            // Handle >= <= and !=
            bool invert = false;
            if (opChar == '!')
            {
                if (Current != '=')
                {
                    throw new FakeExpressionException("expected '='");
                }
                invert = true;
                _pos++;
                opChar = '=';
            }
            else if ((opChar == '>' || opChar == '<') && Current == '=')
            {
                invert = true;
                _pos++;
                opChar = (opChar == '>') ? '<' : '>';
            }

            // Allow == && || as alternatives to = & |
            if ((opChar == '=' || opChar == '&' || opChar == '|') && Current == opChar)
            {
                _pos++;
            }

            switch (opChar)
            {
                case '&':
                    {
                        bool left = ToBool(value, evaluate);
                        object? right = Parse(opPriority, evaluate && left);
                        value = left && ToBool(right, evaluate);
                        break;
                    }

                case '|':
                    {
                        bool left = ToBool(value, evaluate);
                        object? right = Parse(opPriority, evaluate && !left);
                        value = left || ToBool(right, evaluate && !left);
                        break;
                    }

                case '?':
                    {
                        bool condition = ToBool(value, evaluate);
                        object? whenTrue = Parse(opPriority, evaluate && condition);
                        if (SkipWhiteSpace() != ':')
                        {
                            throw new FakeExpressionException("expected ':'");
                        }
                        _pos++;
                        object? whenFalse = Parse(opPriority - 1, evaluate && !condition);
                        return condition ? whenTrue : whenFalse;
                    }

                default:
                    {
                        object? right = Parse(opPriority, evaluate);
                        value = evaluate ? Apply(opChar, invert, value, right) : null;
                        break;
                    }
            }
        }
    }

    /// <summary>
    /// Parse a unary operator or an operand
    /// </summary>
    private object? ParseOperand(bool evaluate)
    {
        char c = SkipWhiteSpace();
        switch (c)
        {
            case '"':
                return ParseQuotedString();

            case '(':
                {
                    _pos++;
                    object? value = Parse(0, evaluate);
                    if (SkipWhiteSpace() != ')')
                    {
                        throw new FakeExpressionException("expected ')'");
                    }
                    _pos++;
                    return value;
                }

            case '-':
            case '+':
                {
                    _pos++;
                    object? operand = Parse(UnaryPriority, evaluate);
                    if (!evaluate)
                    {
                        return null;
                    }
                    return operand switch
                    {
                        // The casts keep the int an int, else the switch expression converts every arm to float. RRF keeps the type (ExpressionParser.cpp:207-215)
                        int intValue => (object)((c == '-') ? -intValue : intValue),
                        float floatValue => (object)((c == '-') ? -floatValue : floatValue),
                        _ => throw new FakeExpressionException("expected numeric operand")
                    };
                }

            case '!':
                {
                    _pos++;
                    bool operand = ToBool(Parse(UnaryPriority, evaluate), evaluate);
                    return evaluate ? !operand : null;
                }

            case '#':
                {
                    _pos++;
                    object? operand = Parse(UnaryPriority, evaluate);
                    if (!evaluate)
                    {
                        return null;
                    }
                    return operand switch
                    {
                        string stringValue => stringValue.Length,
                        object?[] array => array.Length,
                        _ => throw new FakeExpressionException("expected object model value or string after '#'")
                    };
                }

            default:
                if (char.IsDigit(c) || c == '.')
                {
                    return ParseNumber();
                }
                if (char.IsLetter(c))
                {
                    return ParseIdentifierExpression(evaluate);
                }
                throw new FakeExpressionException((c == '\0') ? "unexpected end of expression" : $"unexpected character '{c}'");
        }
    }

    /// <summary>
    /// Parse a quoted string, see ExpressionParser::ParseQuotedString
    /// </summary>
    private string ParseQuotedString()
    {
        StringBuilder builder = new();
        _pos++;
        while (true)
        {
            if (_pos >= _expression.Length)
            {
                throw new FakeExpressionException("expected '\"'");
            }

            char c = _expression[_pos++];
            if (c == '"')
            {
                if (Current != '"')
                {
                    return builder.ToString();
                }
                _pos++;
            }
            builder.Append(c);
        }
    }

    /// <summary>
    /// Parse a number. Integers become ints and numbers with a decimal point or exponent floats, like in RRF
    /// </summary>
    private object ParseNumber()
    {
        int start = _pos;
        while (char.IsDigit(Current) || Current == '.')
        {
            _pos++;
        }
        if (Current is 'e' or 'E')
        {
            _pos++;
            if (Current is '+' or '-')
            {
                _pos++;
            }
            while (char.IsDigit(Current))
            {
                _pos++;
            }
        }

        string text = _expression[start.._pos];
        if (!text.Contains('.') && !text.Contains('e') && !text.Contains('E') && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
        {
            return intValue;
        }
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
        {
            return floatValue;
        }
        throw new FakeExpressionException($"invalid number '{text}'");
    }

    /// <summary>
    /// Parse an identifier: a keyword, a function call or a variable/object model path
    /// </summary>
    private object? ParseIdentifierExpression(bool evaluate)
    {
        string id = ParseIdentifier(evaluate);
        if (SkipWhiteSpace() == '(')
        {
            _pos++;
            if (id != "exists")
            {
                throw new FakeExpressionException("unknown function");
            }

            SkipWhiteSpace();
            string argument = ParseIdentifier(evaluate);
            if (SkipWhiteSpace() != ')')
            {
                throw new FakeExpressionException("expected ')'");
            }
            _pos++;
            return evaluate ? exists(argument) : null;
        }

        return id switch
        {
            "true" => true,
            "false" => false,
            "null" => null,
            _ => evaluate ? resolve(id) : null
        };
    }

    /// <summary>
    /// Parse an identifier including array indices, e.g. move.axes[1].letter. Index expressions are evaluated
    /// </summary>
    private string ParseIdentifier(bool evaluate)
    {
        StringBuilder builder = new();
        while (true)
        {
            char c = Current;
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
            {
                builder.Append(c);
                _pos++;
            }
            else if (c == '[')
            {
                _pos++;
                object? index = Parse(0, evaluate);
                if (SkipWhiteSpace() != ']')
                {
                    throw new FakeExpressionException("expected ']'");
                }
                _pos++;
                if (evaluate && index is not int)
                {
                    throw new FakeExpressionException("expected integer value");
                }
                builder.Append('[').Append(((index as int?) ?? 0).ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            else
            {
                break;
            }
        }

        if (builder.Length == 0)
        {
            throw new FakeExpressionException("expected identifier");
        }
        return builder.ToString();
    }

    /// <summary>
    /// Convert a value to a Boolean, see ExpressionParser::ConvertToBool
    /// </summary>
    private static bool ToBool(object? value, bool evaluate)
    {
        if (value is bool boolValue)
        {
            return boolValue;
        }
        if (evaluate)
        {
            throw new FakeExpressionException("expected Boolean operand");
        }
        return false;
    }

    /// <summary>
    /// Apply a binary operator that evaluates both operands
    /// </summary>
    private static object? Apply(char op, bool invert, object? left, object? right)
    {
        switch (op)
        {
            case '^':
                return ToText(left) + ToText(right);

            case '+':
            case '-':
            case '*':
                {
                    if (left is int leftInt && right is int rightInt)
                    {
                        return op switch { '+' => leftInt + rightInt, '-' => leftInt - rightInt, _ => leftInt * rightInt };
                    }
                    float leftFloat = ToFloat(left), rightFloat = ToFloat(right);
                    return op switch { '+' => leftFloat + rightFloat, '-' => leftFloat - rightFloat, _ => leftFloat * rightFloat };
                }

            case '/':
                return ToFloat(left) / ToFloat(right);

            case '<':
            case '>':
                {
                    bool result;
                    if (left is int leftInt && right is int rightInt)
                    {
                        result = (op == '<') ? leftInt < rightInt : leftInt > rightInt;
                    }
                    else if (left is bool leftBool && right is bool rightBool)
                    {
                        result = (op == '<') ? (!leftBool && rightBool) : (leftBool && !rightBool);
                    }
                    else if (IsNumeric(left) && IsNumeric(right))
                    {
                        result = (op == '<') ? ToFloat(left) < ToFloat(right) : ToFloat(left) > ToFloat(right);
                    }
                    else
                    {
                        throw new FakeExpressionException("expected numeric or Boolean operands to comparison operator");
                    }
                    return invert ? !result : result;
                }

            case '=':
                {
                    bool result;
                    if (left is null || right is null)
                    {
                        result = left is null && right is null;
                    }
                    else if (left is int leftInt && right is int rightInt)
                    {
                        result = leftInt == rightInt;
                    }
                    else if (IsNumeric(left) && IsNumeric(right))
                    {
                        result = ToFloat(left) == ToFloat(right);
                    }
                    else if (left is bool leftBool && right is bool rightBool)
                    {
                        result = leftBool == rightBool;
                    }
                    else if (left is string leftString && right is string rightString)
                    {
                        result = leftString == rightString;
                    }
                    else
                    {
                        throw new FakeExpressionException("incompatible operand types");
                    }
                    return invert ? !result : result;
                }

            default:
                throw new FakeExpressionException($"unsupported operator '{op}'");
        }
    }

    private static bool IsNumeric(object? value) => value is int or float or double or uint;

    private static float ToFloat(object? value) => value switch
    {
        int intValue => intValue,
        uint uintValue => uintValue,
        float floatValue => floatValue,
        double doubleValue => (float)doubleValue,
        _ => throw new FakeExpressionException("expected numeric operand")
    };

    private static string ToText(object? value) => value switch
    {
        null => "null",
        bool boolValue => boolValue ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
