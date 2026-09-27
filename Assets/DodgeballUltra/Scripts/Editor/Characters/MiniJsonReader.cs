using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// Small, strict, allocation-conscious JSON reader (RFC 8259) used for <c>Tools/rocketbox_manifest.json</c>.
    /// <para>
    /// Why not <c>JsonUtility</c>: the manifest stores avatars as a JSON <em>object keyed by avatar name</em>
    /// (<c>"avatars": { "Sports_Male_02": {...}, ... }</c>). <c>JsonUtility</c> cannot deserialize dictionaries, and
    /// rewriting the text with regular expressions is fragile, so the document is parsed into plain CLR values:
    /// objects -> <see cref="Dictionary{TKey,TValue}"/> (string -> object, insertion order preserved by
    /// <see cref="JsonObject"/>), arrays -> <see cref="List{T}"/> of object, strings -> <see cref="string"/>,
    /// numbers -> <see cref="double"/>, true/false -> <see cref="bool"/>, null -> <c>null</c>.
    /// </para>
    /// <para>Pure C# (no UnityEngine) so it can be unit-tested outside the editor.</para>
    /// </summary>
    public static class MiniJsonReader
    {
        /// <summary>Maximum nesting depth accepted (guards against stack overflow on malformed input).</summary>
        public const int MaxDepth = 64;

        /// <summary>Parses <paramref name="json"/>. Throws <see cref="FormatException"/> with line/column on invalid input.</summary>
        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var parser = new Parser(json);
            parser.SkipWhitespace();
            object value = parser.ReadValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd) throw parser.Error("unexpected trailing characters");
            return value;
        }

        /// <summary>Non-throwing variant of <see cref="Parse"/>.</summary>
        public static bool TryParse(string json, out object value, out string error)
        {
            try
            {
                value = Parse(json);
                error = null;
                return true;
            }
            catch (FormatException ex)
            {
                value = null;
                error = ex.Message;
                return false;
            }
        }

        // ---------------------------------------------------------------------------------------------- typed accessors

        /// <summary>Member <paramref name="key"/> of a parsed object as a string (null when absent or not a string).</summary>
        public static string GetString(IDictionary<string, object> obj, string key)
            => obj != null && obj.TryGetValue(key, out object v) ? v as string : null;

        /// <summary>Member <paramref name="key"/> of a parsed object as an object (null when absent or not an object).</summary>
        public static IDictionary<string, object> GetObject(IDictionary<string, object> obj, string key)
            => obj != null && obj.TryGetValue(key, out object v) ? v as IDictionary<string, object> : null;

        /// <summary>Member <paramref name="key"/> as a list of strings (non-string entries are skipped; empty when absent).</summary>
        public static List<string> GetStringList(IDictionary<string, object> obj, string key)
        {
            var result = new List<string>();
            if (obj == null || !obj.TryGetValue(key, out object v) || !(v is List<object> list)) return result;
            for (int i = 0; i < list.Count; i++)
                if (list[i] is string s) result.Add(s);
            return result;
        }

        /// <summary>
        /// A JSON object: a dictionary that also remembers the order in which keys appeared in the document
        /// (<see cref="Keys"/> enumerates in document order), which keeps manifest listings stable.
        /// </summary>
        public sealed class JsonObject : Dictionary<string, object>
        {
            private readonly List<string> _order = new List<string>();

            /// <summary>Keys in document order.</summary>
            public IReadOnlyList<string> OrderedKeys => _order;

            internal void Set(string key, object value)
            {
                if (!ContainsKey(key)) _order.Add(key);
                this[key] = value; // RFC 8259: duplicate names -> last one wins (common behaviour)
            }
        }

        // ------------------------------------------------------------------------------------------------------ parser

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            private readonly StringBuilder _sb = new StringBuilder(64);

            public Parser(string s)
            {
                _s = s;
                // Tolerate a UTF-8 BOM decoded as U+FEFF.
                _i = s.Length > 0 && s[0] == '﻿' ? 1 : 0;
            }

            public bool AtEnd => _i >= _s.Length;

            public void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                    else break;
                }
            }

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Error("nesting too deep");
                if (AtEnd) throw Error("unexpected end of input");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject(depth + 1);
                    case '[': return ReadArray(depth + 1);
                    case '"': return ReadString();
                    case 't': ExpectLiteral("true"); return true;
                    case 'f': ExpectLiteral("false"); return false;
                    case 'n': ExpectLiteral("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error($"unexpected character '{c}'");
                }
            }

            private JsonObject ReadObject(int depth)
            {
                var obj = new JsonObject();
                _i++; // '{'
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _i++;
                    return obj;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw Error("expected a string key");
                    string key = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':') throw Error("expected ':' after key");
                    _i++;
                    SkipWhitespace();
                    obj.Set(key, ReadValue(depth));
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',')
                    {
                        _i++;
                        continue;
                    }
                    if (c == '}')
                    {
                        _i++;
                        return obj;
                    }
                    throw Error("expected ',' or '}' in object");
                }
            }

            private List<object> ReadArray(int depth)
            {
                var list = new List<object>();
                _i++; // '['
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _i++;
                    return list;
                }

                while (true)
                {
                    SkipWhitespace();
                    list.Add(ReadValue(depth));
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',')
                    {
                        _i++;
                        continue;
                    }
                    if (c == ']')
                    {
                        _i++;
                        return list;
                    }
                    throw Error("expected ',' or ']' in array");
                }
            }

            private string ReadString()
            {
                _i++; // opening quote
                _sb.Length = 0;
                int runStart = _i;
                while (true)
                {
                    if (AtEnd) throw Error("unterminated string");
                    char c = _s[_i];
                    if (c == '"')
                    {
                        _sb.Append(_s, runStart, _i - runStart);
                        _i++;
                        return _sb.ToString();
                    }
                    if (c < 0x20) throw Error("control character in string");
                    if (c != '\\')
                    {
                        _i++;
                        continue;
                    }

                    // Escape sequence: flush the literal run first.
                    _sb.Append(_s, runStart, _i - runStart);
                    _i++;
                    if (AtEnd) throw Error("unterminated escape");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': _sb.Append('"'); break;
                        case '\\': _sb.Append('\\'); break;
                        case '/': _sb.Append('/'); break;
                        case 'b': _sb.Append('\b'); break;
                        case 'f': _sb.Append('\f'); break;
                        case 'n': _sb.Append('\n'); break;
                        case 'r': _sb.Append('\r'); break;
                        case 't': _sb.Append('\t'); break;
                        case 'u': _sb.Append(ReadHex4()); break; // surrogate pairs arrive as two \u escapes
                        default: throw Error($"invalid escape '\\{e}'");
                    }
                    runStart = _i;
                }
            }

            private char ReadHex4()
            {
                if (_i + 4 > _s.Length) throw Error("truncated \\u escape");
                int value = 0;
                for (int k = 0; k < 4; k++)
                {
                    char h = _s[_i++];
                    int d = h >= '0' && h <= '9' ? h - '0'
                        : h >= 'a' && h <= 'f' ? h - 'a' + 10
                        : h >= 'A' && h <= 'F' ? h - 'A' + 10
                        : -1;
                    if (d < 0) throw Error("invalid hex digit in \\u escape");
                    value = (value << 4) | d;
                }
                return (char)value;
            }

            private double ReadNumber()
            {
                int start = _i;
                if (Peek() == '-') _i++;
                if (Peek() == '0') _i++;
                else if (IsDigit(Peek())) while (IsDigit(Peek())) _i++;
                else throw Error("invalid number");
                if (Peek() == '.')
                {
                    _i++;
                    if (!IsDigit(Peek())) throw Error("digit expected after '.'");
                    while (IsDigit(Peek())) _i++;
                }
                if (Peek() == 'e' || Peek() == 'E')
                {
                    _i++;
                    if (Peek() == '+' || Peek() == '-') _i++;
                    if (!IsDigit(Peek())) throw Error("digit expected in exponent");
                    while (IsDigit(Peek())) _i++;
                }
                return double.Parse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private void ExpectLiteral(string literal)
            {
                if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0) throw Error($"expected '{literal}'");
                _i += literal.Length;
            }

            private char Peek() => _i < _s.Length ? _s[_i] : '\0';

            private static bool IsDigit(char c) => c >= '0' && c <= '9';

            public FormatException Error(string message)
            {
                int line = 1, col = 1;
                for (int k = 0; k < _i && k < _s.Length; k++)
                {
                    if (_s[k] == '\n')
                    {
                        line++;
                        col = 1;
                    }
                    else col++;
                }
                return new FormatException($"JSON: {message} at line {line}, column {col}");
            }
        }
    }
}
