using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 无依赖的小型 JSON 读写。Core 不能引用 Newtonsoft（需离线、可在 Unity/.NET 共用），
    /// 而真实模型与离线提供方的输出必须走同一条解析/校验路径，所以在这里自带。
    /// 解析结果：Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / double / bool / null。
    /// </summary>
    public static class MiniJson
    {
        public const int MaxDepth = 32;

        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("empty");
            var p = new Parser(text);
            p.SkipWs();
            object value = p.Value(0);
            p.SkipWs();
            if (!p.End) throw new FormatException("trailing characters at " + p.Pos);
            return value;
        }

        public static bool TryParseObject(string text, out Dictionary<string, object> obj, out string error)
        {
            obj = null; error = null;
            try
            {
                obj = Parse(text) as Dictionary<string, object>;
                if (obj == null) error = "not_object";
            }
            catch (FormatException e) { error = "parse_error: " + e.Message; }
            return obj != null;
        }

        public static string Write(object value, bool pretty = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, pretty, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v, bool pretty, int indent)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case float f:
                    if (float.IsNaN(f) || float.IsInfinity(f)) throw new FormatException("non-finite number");
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) throw new FormatException("non-finite number");
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case IDictionary<string, object> map:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in map)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        if (pretty) { sb.Append('\n').Append(' ', (indent + 1) * 2); }
                        WriteString(sb, kv.Key);
                        sb.Append(pretty ? ": " : ":");
                        WriteValue(sb, kv.Value, pretty, indent + 1);
                    }
                    if (pretty && !first) { sb.Append('\n').Append(' ', indent * 2); }
                    sb.Append('}');
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem) sb.Append(pretty ? ", " : ",");
                        firstItem = false;
                        WriteValue(sb, item, pretty, indent + 1);
                    }
                    sb.Append(']');
                    return;
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- 读取辅助（容错地取字段） ----------

        public static string Str(this Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) ? v as string : null;

        public static double? Num(this Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is double d ? d : (double?)null;

        public static Dictionary<string, object> Obj(this Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        /// <summary>取字符串数组；字段缺失返回空数组，元素不是字符串时返回 null（视为结构错误）。</summary>
        public static List<string> StrList(this Dictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return new List<string>();
            if (!(v is List<object> list)) return null;
            var result = new List<string>();
            foreach (var item in list)
            {
                if (!(item is string s)) return null;
                result.Add(s);
            }
            return result;
        }

        sealed class Parser
        {
            readonly string _s;
            public int Pos;
            public Parser(string s) { _s = s; }
            public bool End => Pos >= _s.Length;

            public void SkipWs()
            {
                while (Pos < _s.Length && char.IsWhiteSpace(_s[Pos])) Pos++;
            }

            char Peek() => Pos < _s.Length ? _s[Pos] : '\0';

            public object Value(int depth)
            {
                if (depth > MaxDepth) throw new FormatException("too deep");
                SkipWs();
                char c = Peek();
                switch (c)
                {
                    case '{': return Object(depth);
                    case '[': return Array(depth);
                    case '"': return String();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || char.IsDigit(c)) return Number();
                        throw new FormatException("unexpected '" + c + "' at " + Pos);
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw new FormatException("expected " + word);
                Pos += word.Length;
            }

            Dictionary<string, object> Object(int depth)
            {
                var o = new Dictionary<string, object>();
                Pos++;
                SkipWs();
                if (Peek() == '}') { Pos++; return o; }
                while (true)
                {
                    SkipWs();
                    if (Peek() != '"') throw new FormatException("expected key at " + Pos);
                    string key = String();
                    SkipWs();
                    if (Peek() != ':') throw new FormatException("expected ':' at " + Pos);
                    Pos++;
                    if (o.ContainsKey(key)) throw new FormatException("duplicate key: " + key);
                    o[key] = Value(depth + 1);
                    SkipWs();
                    char c = Peek();
                    Pos++;
                    if (c == '}') return o;
                    if (c != ',') throw new FormatException("expected ',' or '}' at " + (Pos - 1));
                }
            }

            List<object> Array(int depth)
            {
                var list = new List<object>();
                Pos++;
                SkipWs();
                if (Peek() == ']') { Pos++; return list; }
                while (true)
                {
                    list.Add(Value(depth + 1));
                    SkipWs();
                    char c = Peek();
                    Pos++;
                    if (c == ']') return list;
                    if (c != ',') throw new FormatException("expected ',' or ']' at " + (Pos - 1));
                }
            }

            string String()
            {
                var sb = new StringBuilder();
                Pos++;
                while (true)
                {
                    if (End) throw new FormatException("unterminated string");
                    char c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw new FormatException("bad escape");
                    char e = _s[Pos++];
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
                            if (Pos + 4 > _s.Length) throw new FormatException("bad unicode escape");
                            sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw new FormatException("bad escape \\" + e);
                    }
                }
            }

            double Number()
            {
                int start = Pos;
                if (Peek() == '-') Pos++;
                while (!End && (char.IsDigit(_s[Pos]) || _s[Pos] == '.' || _s[Pos] == 'e' || _s[Pos] == 'E' || _s[Pos] == '+' || _s[Pos] == '-')) Pos++;
                if (!double.TryParse(_s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    throw new FormatException("bad number at " + start);
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new FormatException("non-finite number");
                return d;
            }
        }
    }
}
