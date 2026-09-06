using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hiccup.Editor.Cdp
{
    /// <summary>
    /// A small JSON reader/writer for DevTools Protocol traffic. JsonUtility cannot describe CDP's
    /// open-ended payloads, and the package deliberately has no third-party JSON dependency.
    /// Values come back as <see cref="Dictionary{TKey,TValue}"/>, <see cref="List{T}"/>, string, double, bool or null.
    /// </summary>
    internal static class Json
    {
        // ------------------------------------------------------------------ writing

        /// <summary>Encodes a string as a JSON literal, including the surrounding quotes.</summary>
        public static string Quote(string s)
        {
            var sb = new StringBuilder((s?.Length ?? 0) + 2);
            Quote(s, sb);
            return sb.ToString();
        }

        public static void Quote(string s, StringBuilder sb)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (var c in s)
                {
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
                            // Everything outside printable ASCII goes out as \uXXXX. That covers the control
                            // characters and U+2028/U+2029, which would otherwise terminate a JS string literal.
                            if (c < 0x20 || c > 0x7e)
                                sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else
                                sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        public static string Number(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ reading

        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            int i = 0;
            var value = ParseValue(json, ref i);
            return value;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
                return null;

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': i += 4; return true;
                case 'f': i += 5; return false;
                case 'n': i += 4; return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return d;
            }

            while (i < s.Length)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"')
                    break;
                var key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ':')
                    i++;
                d[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (i < s.Length && s[i] == '}')
                {
                    i++;
                    break;
                }
                break;
            }
            return d;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return list;
            }

            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (i < s.Length && s[i] == ']')
                {
                    i++;
                    break;
                }
                break;
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"')
                    break;
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (i >= s.Length)
                    break;

                char e = s[i++];
                switch (e)
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
                        if (i + 4 <= s.Length &&
                            ushort.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
                i++;
            var span = s.Substring(start, i - start);
            return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (object)0d;
        }

        // ------------------------------------------------------------------ accessors

        public static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;

        public static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static string Str(Dictionary<string, object> d, string key, string fallback = null)
            => d != null && d.TryGetValue(key, out var v) && v is string s ? s : fallback;

        public static double Num(Dictionary<string, object> d, string key, double fallback = 0)
            => d != null && d.TryGetValue(key, out var v) && v is double n ? n : fallback;

        public static int Int(Dictionary<string, object> d, string key, int fallback = 0)
            => (int)Num(d, key, fallback);

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback = false)
            => d != null && d.TryGetValue(key, out var v) && v is bool b ? b : fallback;
    }
}
