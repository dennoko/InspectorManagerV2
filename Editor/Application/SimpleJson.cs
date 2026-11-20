using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DennokoWorks.InspectorManagerV2.Application
{
    /// <summary>
    /// Zero-dependency strict JSON serializer/parser compatible with .NET Standard 2.0, Unity 2022.3, Unity 6, and .NET 9.
    /// </summary>
    public static class SimpleJson
    {
        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value);
            return sb.ToString();
        }

        public static object Deserialize(string json)
        {
            if (json == null) throw new FormatException("JSON string is null.");
            var parser = new Parser(json);
            var result = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.IsAtEnd)
            {
                throw new FormatException("Unexpected trailing characters in JSON.");
            }
            return result;
        }

        public static Dictionary<string, object> DeserializeObject(string json)
        {
            var obj = Deserialize(json) as Dictionary<string, object>;
            if (obj == null)
            {
                throw new FormatException("Expected JSON root object.");
            }
            return obj;
        }

        public static string GetString(Dictionary<string, object> dict, string key, string fallback = "")
        {
            if (dict != null && dict.TryGetValue(key, out var val) && val != null)
            {
                return Convert.ToString(val, CultureInfo.InvariantCulture) ?? fallback;
            }
            return fallback;
        }

        public static bool GetBool(Dictionary<string, object> dict, string key, bool fallback = false)
        {
            if (dict != null && dict.TryGetValue(key, out var val) && val is bool b)
            {
                return b;
            }
            return fallback;
        }

        public static int GetInt(Dictionary<string, object> dict, string key, int fallback = 0)
        {
            if (dict != null && dict.TryGetValue(key, out var val) && val != null)
            {
                if (val is long l) return (int)l;
                if (val is double d) return (int)d;
                if (val is int i) return i;
            }
            return fallback;
        }

        public static long GetLong(Dictionary<string, object> dict, string key, long fallback = 0)
        {
            if (dict != null && dict.TryGetValue(key, out var val) && val != null)
            {
                if (val is long l) return l;
                if (val is int i) return i;
                if (val is double d) return (long)d;
            }
            return fallback;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.TryGetValue(key, out var val))
            {
                return val as Dictionary<string, object>;
            }
            return null;
        }

        public static List<object> GetList(Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.TryGetValue(key, out var val))
            {
                return val as List<object>;
            }
            return null;
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            switch (value)
            {
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    return;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    return;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("0");
                    else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case float f:
                    if (float.IsNaN(f) || float.IsInfinity(f)) sb.Append("0");
                    else sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case IDictionary dict:
                    sb.Append('{');
                    bool firstObj = true;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (!firstObj) sb.Append(',');
                        firstObj = false;
                        WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                        sb.Append(':');
                        WriteValue(sb, entry.Value);
                    }
                    sb.Append('}');
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstArr = true;
                    foreach (var item in list)
                    {
                        if (!firstArr) sb.Append(',');
                        firstArr = false;
                        WriteValue(sb, item);
                    }
                    sb.Append(']');
                    return;
                default:
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    return;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _pos;

            public bool IsAtEnd => _pos >= _json.Length;

            public Parser(string json)
            {
                _json = json;
                _pos = 0;
            }

            public void SkipWhitespace()
            {
                while (_pos < _json.Length && char.IsWhiteSpace(_json[_pos]))
                {
                    _pos++;
                }
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (IsAtEnd) throw new FormatException("Unexpected end of JSON.");

                char c = _json[_pos];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == 't') { ExpectLiteral("true"); return true; }
                if (c == 'f') { ExpectLiteral("false"); return false; }
                if (c == 'n') { ExpectLiteral("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();

                throw new FormatException("Unexpected token '" + c + "' at position " + _pos);
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                _pos++; // '{'
                SkipWhitespace();
                if (!IsAtEnd && _json[_pos] == '}')
                {
                    _pos++;
                    return dict;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (IsAtEnd || _json[_pos] != '"')
                    {
                        throw new FormatException("Expected string key at position " + _pos);
                    }
                    string key = ParseString();
                    SkipWhitespace();
                    if (IsAtEnd || _json[_pos] != ':')
                    {
                        throw new FormatException("Expected ':' at position " + _pos);
                    }
                    _pos++;
                    object val = ParseValue();
                    dict[key] = val;
                    SkipWhitespace();
                    if (IsAtEnd) throw new FormatException("Unterminated JSON object.");
                    if (_json[_pos] == '}')
                    {
                        _pos++;
                        return dict;
                    }
                    if (_json[_pos] != ',')
                    {
                        throw new FormatException("Expected ',' or '}' at position " + _pos);
                    }
                    _pos++;
                }
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                _pos++; // '['
                SkipWhitespace();
                if (!IsAtEnd && _json[_pos] == ']')
                {
                    _pos++;
                    return list;
                }

                while (true)
                {
                    list.Add(ParseValue());
                    SkipWhitespace();
                    if (IsAtEnd) throw new FormatException("Unterminated JSON array.");
                    if (_json[_pos] == ']')
                    {
                        _pos++;
                        return list;
                    }
                    if (_json[_pos] != ',')
                    {
                        throw new FormatException("Expected ',' or ']' at position " + _pos);
                    }
                    _pos++;
                }
            }

            private string ParseString()
            {
                _pos++; // '"'
                var sb = new StringBuilder();
                while (_pos < _json.Length)
                {
                    char c = _json[_pos++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (_pos >= _json.Length) throw new FormatException("Unterminated escape sequence.");
                        char esc = _json[_pos++];
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_pos + 4 > _json.Length) throw new FormatException("Invalid unicode escape.");
                                string hex = _json.Substring(_pos, 4);
                                sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                _pos += 4;
                                break;
                            default:
                                throw new FormatException("Invalid escape character '\\" + esc + "'.");
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                throw new FormatException("Unterminated JSON string.");
            }

            private object ParseNumber()
            {
                int start = _pos;
                if (_json[_pos] == '-') _pos++;
                while (_pos < _json.Length && char.IsDigit(_json[_pos])) _pos++;

                bool isFloat = false;
                if (_pos < _json.Length && _json[_pos] == '.')
                {
                    isFloat = true;
                    _pos++;
                    while (_pos < _json.Length && char.IsDigit(_json[_pos])) _pos++;
                }

                if (_pos < _json.Length && (_json[_pos] == 'e' || _json[_pos] == 'E'))
                {
                    isFloat = true;
                    _pos++;
                    if (_pos < _json.Length && (_json[_pos] == '+' || _json[_pos] == '-')) _pos++;
                    while (_pos < _json.Length && char.IsDigit(_json[_pos])) _pos++;
                }

                string token = _json.Substring(start, _pos - start);
                if (!isFloat && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                {
                    return l;
                }
                return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private void ExpectLiteral(string literal)
            {
                if (_pos + literal.Length > _json.Length
                    || string.Compare(_json, _pos, literal, 0, literal.Length, StringComparison.Ordinal) != 0)
                {
                    throw new FormatException("Expected '" + literal + "' at position " + _pos);
                }
                _pos += literal.Length;
            }
        }
    }
}
