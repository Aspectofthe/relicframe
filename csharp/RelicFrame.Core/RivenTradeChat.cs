using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public sealed record TradeChatOffer(string OfferId, string ObservedAt, string Action, string Seller, string Weapon,
    int? Price, string RawText, string Source = "manual");
public sealed record TradeChatImport(TradeChatOffer[] Added, int DuplicateCount, int UnparsedLineCount);
public sealed record TradeChatSummary(int Observations, int WtsCount, double? WtsMin, double? WtsMedian,
    int WtbCount, double? WtbMax, double? WtbMedian, double? PossibleSpread);

public sealed class RivenTradeChat
{
    private static readonly Regex ActionPattern = new("\\b(WTS|WTB|WTT)\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex PricePattern = new("(?<!\\d)(\\d{1,6})\\s*(?:p|pl|plat|platinum)\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TimePrefix = new("^\\s*(?:\\[\\s*\\d{1,2}:\\d{2}(?::\\d{2})?\\s*\\]\\s*)+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Brackets = new("\\[([^\\[\\]]{2,160})\\]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private readonly string path;
    private readonly Func<IReadOnlyList<string>> weaponSource;
    private readonly object weaponGate = new();
    private IReadOnlyList<string>? weaponSnapshot;
    private string[] weapons = [];
    private readonly object gate = new();
    private readonly List<TradeChatOffer> cached = [];
    private readonly HashSet<string> cachedIds = new(StringComparer.Ordinal);
    private bool loaded;
    public RivenTradeChat(string path, IEnumerable<string> weapons)
    {
        this.path = path; var fixedWeapons = weapons.ToArray(); weaponSource = () => fixedWeapons;
        weaponSnapshot = fixedWeapons; this.weapons = NormalizeWeapons(fixedWeapons);
    }
    public RivenTradeChat(string path, Func<IReadOnlyList<string>> weaponSource)
    { this.path = path; this.weaponSource = weaponSource ?? throw new ArgumentNullException(nameof(weaponSource)); }
    private static string[] NormalizeWeapons(IEnumerable<string> source) => source.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(w => Key(w).Length).ToArray();
    private string[] CurrentWeapons()
    {
        var snapshot = weaponSource();
        if (ReferenceEquals(snapshot, Volatile.Read(ref weaponSnapshot))) return Volatile.Read(ref weapons);
        lock (weaponGate)
        {
            if (!ReferenceEquals(snapshot, weaponSnapshot))
            {
                Volatile.Write(ref weapons, NormalizeWeapons(snapshot));
                Volatile.Write(ref weaponSnapshot, snapshot);
            }
            return weapons;
        }
    }
    private static string Key(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
    private string? WeaponIn(string value)
    {
        var normalized = " " + Key(value) + " ";
        return CurrentWeapons().FirstOrDefault(w => normalized.Contains(" " + Key(w) + " ", StringComparison.Ordinal)
            || normalized.Trim().StartsWith(Key(w) + " ", StringComparison.Ordinal));
    }
    private static string Seller(string line, int actionStart)
    {
        var prefix = TimePrefix.Replace(line[..actionStart], "").Trim(' ', ':', '-', '|', '>');
        if (prefix.Contains(':')) prefix = prefix[(prefix.LastIndexOf(':') + 1)..].Trim();
        prefix = Regex.Replace(prefix, "^[^A-Za-z0-9_.-]+", ""); return prefix.Length == 0 ? "Unknown" : prefix[^Math.Min(32, prefix.Length)..];
    }
    public (TradeChatOffer[] Offers, int Unparsed) Parse(string text, DateTimeOffset? observed = null, string source = "manual")
    {
        if (text.Length > 1_000_000) throw new InvalidDataException("One manual trade-chat import is limited to 1 MiB.");
        var stamp = (observed ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var timestamp = stamp.Ticks % TimeSpan.TicksPerSecond == 0 ? stamp.ToString("yyyy-MM-dd'T'HH:mm:sszzz") : stamp.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz");
        var offers = new List<TradeChatOffer>(); var unparsed = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = string.Join(' ', raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); if (line.Length == 0) continue;
            var action = ActionPattern.Match(line); if (!action.Success) { unparsed++; continue; }
            var kind = action.Groups[1].Value.ToUpperInvariant(); var seller = Seller(line, action.Index); var brackets = Brackets.Matches(line).Cast<Match>().ToArray();
            var found = new List<(string Weapon, int? Price)>();
            for (var i = 0; i < brackets.Length; i++)
            {
                var weapon = WeaponIn(brackets[i].Groups[1].Value); if (weapon is null) continue;
                var end = i + 1 < brackets.Length ? brackets[i + 1].Index : line.Length; var near = PricePattern.Match(line, brackets[i].Index + brackets[i].Length, end - brackets[i].Index - brackets[i].Length);
                found.Add((weapon, near.Success ? int.Parse(near.Groups[1].Value) : null));
            }
            if (found.Count == 0 && WeaponIn(line[(action.Index + action.Length)..]) is { } fallbackWeapon)
            { var match = PricePattern.Match(line, action.Index + action.Length); found.Add((fallbackWeapon, match.Success ? int.Parse(match.Groups[1].Value) : null)); }
            if (found.Count == 0) { unparsed++; continue; }
            var global = PricePattern.Match(line, action.Index + action.Length);
            foreach (var item in found)
            {
                var price = item.Price ?? (global.Success ? int.Parse(global.Groups[1].Value) : null);
                var identity = string.Join('\0', stamp.ToString("yyyy-MM-dd"), kind, Key(seller), Key(item.Weapon), price?.ToString() ?? "None", Key(line));
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..20];
                offers.Add(new(id, timestamp, kind, seller, item.Weapon, price, line, source));
            }
        }
        return (offers.ToArray(), unparsed);
    }
    private void EnsureLoaded()
    {
        if (loaded) return;
        if (File.Exists(path)) foreach (var line in File.ReadLines(path))
        {
            try
            {
                if (JsonSerializer.Deserialize<TradeChatOffer>(line, Json.Options) is not { } row || !cachedIds.Add(row.OfferId)) continue;
                cached.Add(row);
            }
            catch (JsonException) { }
        }
        loaded = true;
    }
    public TradeChatOffer[] Load()
    {
        lock (gate) { EnsureLoaded(); return cached.ToArray(); }
    }
    public TradeChatImport Import(string text, DateTimeOffset? observed = null, string source = "manual")
    {
        var parsed = Parse(text, observed, source);
        lock (gate)
        {
            EnsureLoaded(); var added = parsed.Offers.Where(o => cachedIds.Add(o.OfferId)).ToArray();
            var payload = added.Select(o => JsonSerializer.Serialize(o, Json.Options) + "\n").ToArray(); var bytes = payload.Sum(Encoding.UTF8.GetByteCount);
            if ((File.Exists(path) ? new FileInfo(path).Length : 0) + bytes > 16 * 1024 * 1024)
            {
                foreach (var row in added) cachedIds.Remove(row.OfferId);
                throw new InvalidDataException("Trade-chat log reached 16 MiB; archive it before importing more. Existing data is unchanged.");
            }
            if (added.Length > 0) try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var output = new StreamWriter(path, true, new UTF8Encoding(false)); foreach (var line in payload) output.Write(line);
            }
            catch
            {
                foreach (var row in added) cachedIds.Remove(row.OfferId);
                throw;
            }
            cached.AddRange(added);
            return new(added, parsed.Offers.Length - added.Length, parsed.Unparsed);
        }
    }
    public TradeChatOffer[] Recent(string? weapon = null, int days = 30)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, days)); var key = Key(weapon ?? "");
        return Load().Where(o => DateTimeOffset.TryParse(o.ObservedAt, out var stamp) && stamp >= cutoff && (key.Length == 0 || Key(o.Weapon) == key)).OrderByDescending(o => o.ObservedAt, StringComparer.Ordinal).ToArray();
    }
    public static TradeChatSummary Summary(IEnumerable<TradeChatOffer> rows)
    {
        var all = rows.ToArray(); var wts = all.Where(o => o.Action == "WTS" && o.Price.HasValue).Select(o => (double)o.Price!.Value).Order().ToArray();
        var wtb = all.Where(o => o.Action == "WTB" && o.Price.HasValue).Select(o => (double)o.Price!.Value).Order().ToArray();
        static double? Median(double[] a) => a.Length == 0 ? null : a.Length % 2 > 0 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        return new(all.Length, wts.Length, wts.FirstOrDefault(double.NaN) is var min && double.IsFinite(min) ? min : null, Median(wts),
            wtb.Length, wtb.LastOrDefault(double.NaN) is var max && double.IsFinite(max) ? max : null, Median(wtb), wts.Length > 0 && wtb.Length > 0 ? wtb.Max() - wts.Min() : null);
    }
}
