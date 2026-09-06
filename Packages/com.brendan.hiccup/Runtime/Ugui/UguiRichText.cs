using System.Text;
using System.Text.RegularExpressions;

namespace Hiccup.Ugui
{
    /// <summary>
    /// Turns Unity rich text (the subset shared by <c>UnityEngine.UI.Text</c> and TextMesh Pro) into HTML.
    /// Known tags map to elements or spans; everything else is escaped as literal text. Unknown tags are dropped,
    /// which is what a browser would do to a sprite or a material tag anyway.
    /// </summary>
    internal static class UguiRichText
    {
        private static readonly Regex s_tag = new Regex(@"<(/?)([a-zA-Z][a-zA-Z0-9-]*)(?:=([^>]*))?\s*/?>", RegexOptions.Compiled);

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            var sb = new StringBuilder(s.Length + 16);
            Escape(s, 0, s.Length, sb);
            return sb.ToString();
        }

        private static void Escape(string s, int from, int to, StringBuilder sb)
        {
            for (int i = from; i < to; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }
        }

        public static string Convert(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            var sb = new StringBuilder(s.Length + 32);
            int last = 0;
            int open = 0;   // spans we emitted and still owe a close for
            foreach (Match m in s_tag.Matches(s))
            {
                Escape(s, last, m.Index, sb);
                last = m.Index + m.Length;
                bool closing = m.Groups[1].Value == "/";
                string name = m.Groups[2].Value.ToLowerInvariant();
                string arg = m.Groups[3].Success ? m.Groups[3].Value.Trim().Trim('"', '\'') : null;

                switch (name)
                {
                    case "b": case "i": case "u": case "s":
                        sb.Append(closing ? "</" : "<").Append(name).Append('>');
                        break;
                    case "br":
                        if (!closing)
                            sb.Append("<br>");
                        break;
                    case "color":
                        if (closing)
                        {
                            if (open > 0)
                            {
                                sb.Append("</span>");
                                open--;
                            }
                        }
                        else
                        {
                            sb.Append("<span style=\"color:").Append(CssColor(arg)).Append("\">");
                            open++;
                        }
                        break;
                    case "size":
                        if (closing)
                        {
                            if (open > 0)
                            {
                                sb.Append("</span>");
                                open--;
                            }
                        }
                        else
                        {
                            sb.Append("<span style=\"font-size:").Append(CssSize(arg)).Append("\">");
                            open++;
                        }
                        break;
                    case "mark":
                        if (closing)
                        {
                            if (open > 0)
                            {
                                sb.Append("</span>");
                                open--;
                            }
                        }
                        else
                        {
                            sb.Append("<span style=\"background:").Append(CssColor(arg)).Append("\">");
                            open++;
                        }
                        break;
                    case "sup": case "sub":
                        sb.Append(closing ? "</" : "<").Append(name).Append('>');
                        break;
                    case "nobr":
                        if (closing)
                        {
                            if (open > 0)
                            {
                                sb.Append("</span>");
                                open--;
                            }
                        }
                        else
                        {
                            sb.Append("<span style=\"white-space:nowrap\">");
                            open++;
                        }
                        break;
                    default:
                        // sprite, material, link, align, indent, voffset, cspace, font, ...: no HTML equivalent here.
                        break;
                }
            }
            Escape(s, last, s.Length, sb);
            while (open-- > 0)
                sb.Append("</span>");
            return sb.ToString();
        }

        private static string CssColor(string arg)
        {
            if (string.IsNullOrEmpty(arg))
                return "inherit";
            if (arg[0] == '#')
            {
                // Unity: #RGB, #RGBA, #RRGGBB, #RRGGBBAA. CSS accepts the same four forms.
                return arg.Length == 4 || arg.Length == 5 || arg.Length == 7 || arg.Length == 9 ? arg : "inherit";
            }
            switch (arg.ToLowerInvariant())
            {
                case "aqua": case "black": case "blue": case "brown": case "cyan": case "darkblue": case "fuchsia":
                case "green": case "grey": case "gray": case "lightblue": case "lime": case "magenta": case "maroon":
                case "navy": case "olive": case "orange": case "purple": case "red": case "silver": case "teal":
                case "white": case "yellow":
                    return arg;
                default:
                    return "inherit";
            }
        }

        private static string CssSize(string arg)
        {
            if (string.IsNullOrEmpty(arg))
                return "inherit";
            if (arg.EndsWith("%"))
                return arg;
            if (arg.EndsWith("em"))
                return arg;
            if (arg[0] == '+' || arg[0] == '-')
            {
                // TMP relative sizes are in points; approximate as pixels on top of the inherited size.
                return "calc(1em " + arg[0] + " " + arg.Substring(1) + "px)";
            }
            return arg + "px";
        }
    }
}
