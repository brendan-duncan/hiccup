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

        /// <summary>
        /// Appends <paramref name="s"/> as a JSON string literal. With <paramref name="nested"/> the literal is
        /// written as it must appear inside another JSON string, so a JavaScript string literal can be placed in
        /// <c>Runtime.evaluate</c>'s expression in one pass instead of being escaped twice.
        /// </summary>
        public static void Quote(string s, StringBuilder sb, bool nested = false)
        {
            var quote = nested ? "\\\"" : "\"";
            sb.Append(quote);
            Escape(s, sb, nested);
            sb.Append(quote);
        }

        /// <summary>
        /// Appends the body of a JSON string literal, without the quotes. Only the characters JSON requires are
        /// escaped, plus U+2028/U+2029, which would otherwise terminate a JS string literal; everything else,
        /// including non-ASCII, goes out as UTF-8. With <paramref name="nested"/> every escape's backslash is
        /// doubled, which is what the same text looks like one string level deeper.
        /// </summary>
        public static void Escape(string s, StringBuilder sb, bool nested = false)
        {
            if (s == null)
                return;
            var slash = nested ? "\\\\" : "\\";
            var quote = nested ? "\\\"" : "\"";
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append(slash).Append(quote); break;
                    case '\\': sb.Append(slash).Append(slash); break;
                    case '\b': sb.Append(slash).Append('b'); break;
                    case '\f': sb.Append(slash).Append('f'); break;
                    case '\n': sb.Append(slash).Append('n'); break;
                    case '\r': sb.Append(slash).Append('r'); break;
                    case '\t': sb.Append(slash).Append('t'); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                            sb.Append(slash).Append('u').Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }

        public static string Number(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ reading

        // Scratch for strings that contain escapes. The parser runs on the receive thread; a per-thread builder
        // keeps it safe to call from anywhere.
        [ThreadStatic] private static StringBuilder t_scratch;

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
            i++; // opening quote
            int start = i;

            // Most strings (every key, nearly every value) contain no escapes and are a plain substring.
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"')
                {
                    i++;
                    return s.Substring(start, i - 1 - start);
                }
                if (c == '\\')
                    break;
                i++;
            }
            if (i >= s.Length)
                return s.Substring(start);

            var sb = t_scratch ??= new StringBuilder();
            sb.Clear();
            sb.Append(s, start, i - start);
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
                            ushort.TryParse(s.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
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
            return double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (object)0d;
        }

        // ------------------------------------------------------------------ accessors

        public static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static string Str(Dictionary<string, object> d, string key, string fallback = null)
            => d != null && d.TryGetValue(key, out var v) && v is string s ? s : fallback;

        public static double Num(Dictionary<string, object> d, string key, double fallback = 0)
            => d != null && d.TryGetValue(key, out var v) && v is double n ? n : fallback;

        public static int Int(Dictionary<string, object> d, string key, int fallback = 0)
            => (int)Num(d, key, fallback);
    }
}
