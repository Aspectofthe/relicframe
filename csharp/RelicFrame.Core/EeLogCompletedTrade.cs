using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public sealed record GameTradeItem(string Name, int Quantity);
public sealed record EeLogCompletedTrade(string Id, DateTimeOffset ObservedAt, int PlatinumReceived,
    IReadOnlyList<GameTradeItem> Given);

// EE.log includes the final confirmation's itemized offer and a separate success
// line. A confirmation by itself is never evidence that stock left the account.
public sealed class EeLogCompletedTradeParser
{
    private static readonly Regex Partner = new(@"and will receive from\s+.+?\s+the following:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Stack = new(@"^(.+?)\s+x\s+(\d{1,4})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Platinum = new(@"^Platinum(?:\s+x\s+(\d{1,7}))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private string? confirmation;
    private DateTimeOffset started;

    public EeLogCompletedTrade? Consume(string line, DateTimeOffset now)
    {
        if (confirmation is not null && now - started > TimeSpan.FromMinutes(1)) confirmation = null;
        if (line.Contains("Are you sure you want to accept this trade?", StringComparison.OrdinalIgnoreCase))
        {
            confirmation = line.Length <= 16_384 ? line : null;
            started = now;
            return null;
        }
        if (line.Contains("The trade was successful!", StringComparison.OrdinalIgnoreCase))
        {
            var source = confirmation;
            confirmation = null;
            return source is null ? null : Parse(source, now);
        }
        if (line.Contains("trade failed", StringComparison.OrdinalIgnoreCase) || line.Contains("Trade was cancelled", StringComparison.OrdinalIgnoreCase))
        {
            confirmation = null;
            return null;
        }
        if (confirmation is not null && line.Length > 0 && !Regex.IsMatch(line, @"^\d+\.\d+\s+\S+\s+\[(?:Info|Warning|Error)\]:", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            confirmation = confirmation.Length + line.Length < 16_384 ? confirmation + "\n" + line : null;
        return null;
    }

    private static EeLogCompletedTrade? Parse(string source, DateTimeOffset observedAt)
    {
        var start = source.IndexOf("You are offering:", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var description = source[start..];
        var divider = Partner.Match(description);
        if (!divider.Success) return null;
        var given = Items(description["You are offering:".Length..divider.Index], out var platinumOffered);
        _ = Items(description[(divider.Index + divider.Length)..], out var platinumReceived);
        if (platinumOffered > 0 || platinumReceived <= 0 || given.Count == 0) return null;
        var stamp = source.Split(' ', 2)[0];
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp + "|" + description)));
        return new(id, observedAt, platinumReceived, given);
    }

    private static IReadOnlyList<GameTradeItem> Items(string block, out int platinum)
    {
        platinum = 0;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in block.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var text = Regex.Replace(raw, @"\b(?:title|leftItem|rightItem)=.*$", "", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)).Trim().TrimEnd(',', ' ');
            if (text.Length == 0 || Regex.IsMatch(text, @"^\d+\.\d+\s", RegexOptions.None, TimeSpan.FromMilliseconds(100))) continue;
            text = Regex.Replace(text, @"[\uE000-\uF8FF\uFFFD]", "").Trim();
            if (text.Length == 0) continue;
            var money = Platinum.Match(text);
            if (money.Success)
            {
                platinum += money.Groups[1].Success && int.TryParse(money.Groups[1].Value, out var amount) ? amount : 1;
                continue;
            }
            var stack = Stack.Match(text);
            var name = (stack.Success ? stack.Groups[1].Value : text).Trim();
            var quantity = stack.Success && int.TryParse(stack.Groups[2].Value, out var parsed) ? parsed : 1;
            if (name.Length is < 3 or > 160 || quantity is < 1 or > 9999) continue;
            counts[name] = counts.GetValueOrDefault(name) + quantity;
        }
        return counts.Select(row => new GameTradeItem(row.Key, row.Value)).ToArray();
    }
}
