using System.Globalization;
using System.Text;

namespace ForestChamber.Core;

/// <summary>稳定身份工具：相同内容 ⇒ 相同身份；导入/导出往返后保持不变。</summary>
public static class StableId
{
    public static string Hash(string kind, string content)
    {
        Span<byte> hash = stackalloc byte[20];
        System.Security.Cryptography.SHA1.TryHashData(
            Encoding.UTF8.GetBytes(kind + "\u0000" + content), hash, out int written);
        var sb = new StringBuilder(written * 2);
        foreach (var b in hash[..written]) sb.Append(b.ToString("x2"));
        return kind + "-" + sb.ToString(16, 8);
    }

    public static string ProductionId(string grammarDocId, int ordinal, string lhs, string rhsKey)
        => Hash("prod", $"{grammarDocId}|{ordinal.ToString(CultureInfo.InvariantCulture)}|{lhs}|{rhsKey}");

    public static string SymbolNodeId(string symbol, int extentStart, int extentLength, string grammarDocId)
        => Hash("snode", $"{grammarDocId}|{symbol}|{extentStart}:{extentLength}");

    public static string FamilyId(string prodId, int pivotStart, IReadOnlyList<PackedChild> children)
    {
        var sb = new StringBuilder(prodId).Append('@').Append(pivotStart).Append('|');
        foreach (var c in children)
            sb.Append(c.NodeId).Append(';').Append(c.Slot).Append(';').Append(c.Start).Append(',');
        return Hash("fam", sb.ToString());
    }
}

/// <summary>token 坐标基准（全局统一）：code-unit offsets，左闭右开区间 [start, end)。</summary>
public sealed record TokenSpan(int Start, int Length)
{
    public int End => Start + Length;
    public string Label => $"[{Start},{End})";
    public bool IsEmpty => Length == 0;
}

/// <summary>packed node 中对某个子 SPPF 节点的引用（RHS 槽位 + 起点 + 节点身份）。</summary>
public sealed record PackedChild(int Slot, int Start, string NodeId)
{
    public string Label => $"slot{Slot}@{Start}→{NodeId}";
}

/// <summary>领域量：携带单位与坐标基准；导入层保留原始表示，派生层才尝试规范化。</summary>
public sealed record DomainQuantity(string Raw, string? Unit, double? NormalizedValue, string? Diagnostic)
{
    public bool Converted => Diagnostic is null && NormalizedValue is not null;
}
