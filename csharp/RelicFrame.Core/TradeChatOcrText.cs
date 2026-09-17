using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public static class TradeChatOcrText
{
    private static readonly Regex MessageStart = new(
        @"^\s*(?:\[\s*\d{1,2}:\d{2}(?::\d{2})?\s*\]\s*)?(?<seller>[A-Za-z0-9_.-]{2,32})\s*:\s*(?<body>.*)$",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TradeAction = new(@"\b(?:WTS|WTB|WTT)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static string[] Entries(string text)
        => Messages(text).Where(value => TradeAction.IsMatch(value)).ToArray();

    public static bool IsTradeOffer(string text) => TradeAction.IsMatch(text);

    public static string[] Messages(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var entries = new List<string>();
        string? current = null;
        foreach (var raw in text.Replace('\r', '\n').Split('\n'))
        {
            var line = string.Join(' ', raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (line.Length == 0) continue;
            var start = MessageStart.Match(line);
            if (start.Success)
            {
                Add(current);
                current = $"{start.Groups["seller"].Value}: {start.Groups["body"].Value}";
            }
            else if (current is not null && !LooksLikeInterfaceLabel(line)) current += " " + line;
            else if (TradeAction.IsMatch(line)) { Add(current); current = line; }
        }
        Add(current);
        return entries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) entries.Add(value.Trim());
        }
    }

    private static bool LooksLikeInterfaceLabel(string line)
        => line.Equals("TRADE", StringComparison.OrdinalIgnoreCase)
           || line.Equals("REGION", StringComparison.OrdinalIgnoreCase)
           || line.Equals("CLAN", StringComparison.OrdinalIgnoreCase)
           || line.Equals("SQUAD", StringComparison.OrdinalIgnoreCase)
           || line.Equals("RECRUITING", StringComparison.OrdinalIgnoreCase);
}
