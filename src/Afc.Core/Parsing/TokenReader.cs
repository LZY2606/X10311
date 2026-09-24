using System.Text.Json;
using Afc.Core.Model;

namespace Afc.Core.Parsing;

/// <summary>
/// token 导入支持两种形式：
/// 1) JSON：{"id","version","tokens":[{"kind","text","unit"?,"offset"?}]}
/// 2) 空白分隔的 "kind:text" 行（离线 fixture 用），坐标自动计算。
/// 原始表示始终保留；数值规范化失败只产生诊断。
/// </summary>
public static class TokenReader
{
    public static TokenDoc ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var docId = root.TryGetProperty("id", out var idEl) ? idEl.GetString()! : "tokens";
        var version = root.TryGetProperty("version", out var vEl) ? vEl.GetString()! : "v1";

        var tokens = new List<Token>();
        var diagnostics = new List<string>();
        var index = 0;
        var cursor = 0;
        var line = 1;
        var col = 1;

        foreach (var el in root.GetProperty("tokens").EnumerateArray())
        {
            var kind = el.GetProperty("kind").GetString()!;
            var text = el.GetProperty("text").GetString()!;
            string? unit = el.TryGetProperty("unit", out var uEl) ? uEl.GetString() : null;

            int startOffset, startLine, startCol;
            if (el.TryGetProperty("offset", out var offEl))
            {
                startOffset = offEl.GetInt32();
                startLine = el.TryGetProperty("line", out var lEl) ? lEl.GetInt32() : 1;
                startCol = el.TryGetProperty("column", out var cEl) ? cEl.GetInt32() : 1;
            }
            else
            {
                startOffset = cursor;
                startLine = line;
                startCol = col;
            }

            var (num, numDiag) = TryNormalizeNumber(text, unit);
            var diags = new List<string>();
            if (numDiag is not null) diags.Add(numDiag);

            var tokenId = el.TryGetProperty("id", out var tidEl) ? tidEl.GetString()! : $"t{index + 1}";
            tokens.Add(new Token(
                tokenId, index, kind, text,
                new Position(startOffset, startLine, startCol),
                new Position(startOffset + text.Length, startLine, startCol + text.Length),
                unit, num, diags));

            cursor = startOffset + text.Length + 1;
            col = startCol + text.Length + 1;
            index++;
        }

        return new TokenDoc(docId, version, tokens, json, diagnostics);
    }

    public static TokenDoc ParseSimple(string text, string docId = "tokens", string version = "v1")
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'));
        var parts = new List<string>();
        foreach (var ln in lines)
        {
            foreach (var p in ln.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                parts.Add(p);
        }

        var nodes = parts.Select(p =>
        {
            var colon = p.IndexOf(':');
            return colon < 0 ? ("TOK", p[1..], (string?)null) : (p[..colon], p[(colon + 1)..], (string?)null);
        }).ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = docId,
            version,
            tokens = nodes.Select(n => new { kind = n.Item1, text = n.Item2, unit = n.Item3 })
        }, new JsonSerializerOptions { WriteIndented = false });
        return ParseJson(json);
    }

    private static (double?, string?) TryNormalizeNumber(string text, string? unit)
    {
        var raw = text;
        var suffixStripped = false;
        if (unit is not null && raw.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[..^unit.Length];
            suffixStripped = true;
        }
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return (value, null);
        }
        return (null, suffixStripped
            ? $"unit-suffix '{unit}' 已剥离但 '{raw}' 仍无法转换为数值；原始表示保留"
            : $"token '{text}' 无法规范化为数值；原始表示保留");
    }
}
