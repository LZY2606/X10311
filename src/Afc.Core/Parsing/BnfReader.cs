using Afc.Core.Model;

namespace Afc.Core.Parsing;

/// <summary>
/// 读取上下文无关文法文本。语法（以原始表示为中心）：
///   @id DOCID ; @version VERSION ; @start NONTERM
///   Lhs ::= 符号... {注解}  |  符号... {注解}
///   [PID] 可选显式产生式身份
/// 终结符："词面量" 或 $TOKEN-KIND；非终结符：裸标识符；ε/. 表示空右部；# 注释。
/// </summary>
public static class BnfReader
{
    public static GrammarDoc Parse(string text)
    {
        var docId = "grammar";
        var version = "v1";
        var start = "";
        var productions = new List<Production>();
        var order = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith('@'))
            {
                var body = line[1..].Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                var key = body[0].ToLowerInvariant();
                var val = body.Length > 1 ? body[1].Trim().TrimEnd(';').Trim() : "";
                if (key == "id") docId = val;
                else if (key == "version") version = val;
                else if (key == "start") start = val;
                continue;
            }

            var arrow = line.IndexOf("::=", StringComparison.Ordinal);
            if (arrow < 0)
                throw new FormatException($"无法识别的产生式行（缺少 ::=）：{line}");

            var lhs = line[..arrow].Trim();
            var idPrefix = "";
            if (lhs.StartsWith('['))
            {
                var close = lhs.IndexOf(']');
                if (close < 0) throw new FormatException($"产生式身份括号未闭合：{line}");
                idPrefix = lhs[1..close].Trim();
                lhs = lhs[(close + 1)..].Trim();
            }

            var altNo = 0;
            foreach (var alt in SplitAlternatives(line[(arrow + 3)..]))
            {
                var (symbols, annotations) = ParseAlternative(alt);
                var id = annotations.TryGetValue("id", out var explicitId)
                    ? explicitId
                    : idPrefix.Length > 0 ? (altNo == 0 ? idPrefix : $"{idPrefix}-{altNo}") : $"P{order + 1}";
                productions.Add(new Production(
                    id, lhs, symbols, rawLine.TrimEnd('\r'), order, annotations));
                order++;
                altNo++;
            }
        }

        if (start.Length == 0) start = productions.Count > 0 ? productions[0].Lhs : "S";

        return new GrammarDoc
        {
            DocId = docId,
            Version = version,
            StartSymbol = start,
            Productions = productions,
            RawText = text
        };
    }

    private static IEnumerable<string> SplitAlternatives(string rhs)
    {
        var inQuote = false;
        var depth = 0;
        var start = 0;
        for (var i = 0; i < rhs.Length; i++)
        {
            var c = rhs[i];
            if (c == '"') inQuote = !inQuote;
            else if (!inQuote && c == '{') depth++;
            else if (!inQuote && c == '}') depth--;
            else if (!inQuote && depth == 0 && c == '|')
            {
                yield return rhs[start..i];
                start = i + 1;
            }
        }
        yield return rhs[start..];
    }

    private static (List<string>, Dictionary<string, string>) ParseAlternative(string alt)
    {
        var symbols = new List<string>();
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokens = TokenizeAlt(alt);
        foreach (var tok in tokens)
        {
            if (tok.StartsWith('{') && tok.EndsWith('}'))
            {
                foreach (var pair in tok[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (kv.Length == 2) annotations[kv[0]] = kv[1];
                }
            }
            else if (tok is "ε" or ".")
            {
                // epsilon：不产生任何 RHS 符号
            }
            else
            {
                symbols.Add(NormalizeTokenRef(tok));
            }
        }
        return (symbols, annotations);
    }

    private static List<string> TokenizeAlt(string alt)
    {
        var result = new List<string>();
        var i = 0;
        while (i < alt.Length)
        {
            while (i < alt.Length && char.IsWhiteSpace(alt[i])) i++;
            if (i >= alt.Length) break;
            var c = alt[i];
            if (c == '"')
            {
                var j = i + 1;
                while (j < alt.Length && alt[j] != '"') j++;
                result.Add(alt[i..Math.Min(j + 1, alt.Length)]);
                i = j + 1;
            }
            else if (c == '{')
            {
                var depth = 1;
                var j = i + 1;
                while (j < alt.Length && depth > 0)
                {
                    if (alt[j] == '{') depth++;
                    else if (alt[j] == '}') depth--;
                    j++;
                }
                result.Add(alt[i..j]);
                i = j;
            }
            else
            {
                var j = i;
                while (j < alt.Length && !char.IsWhiteSpace(alt[j])) j++;
                result.Add(alt[i..j]);
                i = j;
            }
        }
        return result;
    }

    private static string NormalizeTokenRef(string tok)
    {
        if (tok.StartsWith('"')) return tok;
        if (tok.StartsWith('$')) return tok;
        return tok;
    }
}
