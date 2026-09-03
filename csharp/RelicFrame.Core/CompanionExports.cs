using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public sealed record CompanionAttachment(string Url, string Filename, long? FileSizeBytes = null, string LocalPath = "", bool Exists = false);
public sealed record CompanionExportMessage(string MessageId, string Channel, string SourceFile, string Author, string UserId,
    string Timestamp, string Text, CompanionAttachment[] Attachments, bool Forwarded = false);
public sealed record CompanionPriceAmount(double Low, double High, string Raw);
public sealed record CompanionImportSummary(int Messages, int Attachments, int PriceEvidence, int DeduplicatedPriceEvidence,
    IReadOnlyDictionary<string, int> Classifications);

/// <summary>Read-only, offline importer for DiscordChatExporter HTML/JSON companion-sales archives.</summary>
public static partial class CompanionExports
{
    private const string Number = @"(?:\d{1,3}(?:,\d{3})+|\d{1,6})(?:\.\d{1,2})?";
    [GeneratedRegex(@"(?<![\w])(?<low>" + Number + @")(?<low_k>k)?(?:\s*(?:-|–|—|to|/)\s*(?<high>" + Number + @")(?<high_k>k)?)?\s*(?:p|pl|plat|platinum)\b", RegexOptions.IgnoreCase, 500)]
    private static partial Regex PriceRegex();
    [GeneratedRegex(@"(?<![\w])(?<low>" + Number + @")\s*k\b", RegexOptions.IgnoreCase, 500)] private static partial Regex KPriceRegex();
    [GeneratedRegex(@"\b(?:sold|bought|purchased|paid|went\s+for|sale\s+for|buyer\s+paid)\b", RegexOptions.IgnoreCase, 500)] private static partial Regex SaleRegex();
    [GeneratedRegex(@"\b(?:wts|wtb|selling|buying|asking|offer(?:ing)?|auction|reserve)\b", RegexOptions.IgnoreCase, 500)] private static partial Regex ListingRegex();
    [GeneratedRegex(@"\b(?:price\s*check|pc|worth|value|valued|price|apprais(?:al|e|ed))\b|how much", RegexOptions.IgnoreCase, 500)] private static partial Regex AppraisalRegex();
    [GeneratedRegex(@"<[^>]*>|[^<]+", RegexOptions.Singleline, 1000)] private static partial Regex HtmlTokenRegex();
    [GeneratedRegex(@"(?<key>[\w:-]+)(?:\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s>]+)))?", RegexOptions.Singleline, 500)] private static partial Regex AttributeRegex();
    [GeneratedRegex(@"(?:Image|Video|Audio):\s*(.+?)(?:\s*\(|$)", RegexOptions.IgnoreCase, 500)] private static partial Regex AttachmentNameRegex();

    private static readonly Dictionary<string, string[]> TraitGroups = new(StringComparer.Ordinal)
    {
        ["species"] = ["kubrow", "kavat"],
        ["breed"] = ["chesa", "sunika", "huras", "raksa", "sahasa", "smeeta", "adarza", "vasca", "helminth"],
        ["pattern"] = ["striped", "stripe", "patchy", "hound", "domino", "merle", "lotus", "hyacinth"],
        ["build"] = ["skinny", "athletic", "bulky"],
        ["rarity"] = ["single rare", "double rare", "double same rare", "triple rare", "quad rare", "solid common", "solid uncommon", "solid rare", "quad solid", "common", "uncommon"],
        ["color"] = ["ash grey", "earth brown", "corpus grey", "hek green", "kril brown", "gallium grey", "grustrag grey", "saturn brown", "sedna grey", "derelict black", "mars red", "infested black", "void black", "darvo blue", "ordis grey", "mercury brown", "anyo grey", "ambulas black", "shadow grey", "sargas brown", "jupiter brown", "phorid red", "alad blue", "venus brown", "green", "light gold", "pink", "purple", "blue", "orange", "red", "lilac", "black", "gold", "cyan", "lime", "white"]
    };

    public static CompanionPriceAmount[] ExtractAmounts(string text)
    {
        var rows = new List<CompanionPriceAmount>(); var occupied = new List<(int Start, int End)>();
        foreach (Match match in PriceRegex().Matches(text))
        {
            var low = ParseAmount(match.Groups["low"].Value, match.Groups["low_k"].Success);
            var high = match.Groups["high"].Success ? ParseAmount(match.Groups["high"].Value, match.Groups["high_k"].Success) : low;
            rows.Add(new(low, high, match.Value)); occupied.Add((match.Index, match.Index + match.Length));
        }
        if (SaleRegex().IsMatch(text) || ListingRegex().IsMatch(text) || AppraisalRegex().IsMatch(text))
            foreach (Match match in KPriceRegex().Matches(text))
                if (!occupied.Any(span => span.Start <= match.Index && match.Index < span.End))
                { var value = ParseAmount(match.Groups["low"].Value, true); rows.Add(new(value, value, match.Value)); }
        return rows.ToArray();
    }

    private static double ParseAmount(string text, bool thousands) =>
        double.Parse(text.Replace(",", ""), CultureInfo.InvariantCulture) * (thousands ? 1000 : 1);

    public static Dictionary<string, string[]> ExtractTraits(string text)
    {
        var lowered = text.ToLowerInvariant(); var result = new Dictionary<string, string[]>();
        foreach (var (group, values) in TraitGroups)
        {
            var found = values.Where(value => Regex.IsMatch(lowered, @"\b" + Regex.Escape(value) + @"\b", RegexOptions.None, TimeSpan.FromMilliseconds(250))).ToArray();
            if (found.Length > 0) result[group] = found;
        }
        return result;
    }

    public static (string? Classification, CompanionPriceAmount[] Amounts) Classify(string text)
    {
        var amounts = ExtractAmounts(text);
        if (amounts.Length > 0 && SaleRegex().IsMatch(text)) return ("confirmed_sale", amounts);
        if (amounts.Length > 0 && ListingRegex().IsMatch(text)) return ("listing", amounts);
        if (AppraisalRegex().IsMatch(text) && (amounts.Length > 0 || Regex.IsMatch(text, @"\b(?:kubrow|kavat|imprint|print)\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)))) return ("appraisal", amounts);
        return amounts.Length > 0 ? ("price_mention", amounts) : (null, []);
    }

    public static string Fingerprint(CompanionExportMessage message)
    {
        var normalized = Regex.Replace(message.Text.Trim().ToLowerInvariant(), @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(250));
        var pieces = new[] { normalized }.Concat(message.Attachments.Select(a => $"{a.Filename.Trim().ToLowerInvariant()}:{a.FileSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? ""}").Order(StringComparer.Ordinal));
        var payload = string.Join('\0', pieces); return payload.Trim('\0').Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..20].ToLowerInvariant();
    }

    public static IEnumerable<CompanionExportMessage> Read(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch { ".json" => ReadJson(path), ".html" or ".htm" => ReadHtml(path), _ => throw new InvalidDataException("Only DiscordChatExporter HTML or JSON is supported.") };
    }

    public static CompanionExportMessage[] ReadJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 64 }); var root = doc.RootElement;
        var channel = root.Get("channel").Get("name").Text(Path.GetFileNameWithoutExtension(path)); var sourceDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return root.Get("messages").Rows().OrderBy(m => m.Get("timestamp").Text(), StringComparer.Ordinal).Select(raw =>
        {
            var forwarded = raw.Get("forwardedMessage"); var isForwarded = forwarded.ValueKind == JsonValueKind.Object;
            var text = string.Join("\n\n", new[] { raw.Get("content").Text().Trim(), isForwarded ? forwarded.Get("content").Text().Trim() : "" }.Where(s => s.Length > 0));
            var attachments = raw.Get("attachments").Rows().Concat(isForwarded ? forwarded.Get("attachments").Rows() : []).Select(item =>
            {
                var url = item.Get("url").Text(); var filename = item.Get("fileName").Text(url.Length > 0 ? Path.GetFileName(url) : ""); var local = url.Length > 0 ? Path.GetFullPath(Path.Combine(sourceDir, url.Replace('/', Path.DirectorySeparatorChar))) : "";
                return new CompanionAttachment(url, filename, (long?)item.Get("fileSizeBytes").Number(), local, local.Length > 0 && File.Exists(local));
            }).ToArray();
            var author = raw.Get("author");
            return new CompanionExportMessage(raw.Get("id").Text(), channel, Path.GetFileName(path), author.Get("nickname").Text(author.Get("name").Text("Unknown")), author.Get("id").Text(), raw.Get("timestamp").Text(), text, attachments, isForwarded);
        }).ToArray();
    }

    public static CompanionExportMessage[] ReadHtml(string path)
    {
        // This importer runs as a separate offline process; the always-on bot never holds an export in RAM.
        var html = File.ReadAllText(path, Encoding.UTF8); var channel = ChannelFromFilename(path); var source = Path.GetFileName(path);
        var messages = new List<CompanionExportMessage>(); var groupAuthor = "Unknown"; var groupUser = ""; var depth = 0; int groupDepth = -1, containerDepth = -1, contentDepth = -1, attachmentDepth = -1;
        string id = "", author = "Unknown", user = "", timestamp = "", href = ""; var text = new StringBuilder(); var attachments = new List<CompanionAttachment>(); bool captureAuthor = false; var authorText = new StringBuilder();
        foreach (Match token in HtmlTokenRegex().Matches(html))
        {
            var value = token.Value;
            if (!value.StartsWith('<')) { var decoded = WebUtility.HtmlDecode(value); if (captureAuthor) authorText.Append(decoded); if (contentDepth >= 0) text.Append(decoded); continue; }
            if (value.StartsWith("<!--", StringComparison.Ordinal) || value.StartsWith("<!", StringComparison.Ordinal)) continue;
            var closing = value.StartsWith("</", StringComparison.Ordinal); var tag = Regex.Match(value, @"^</?\s*([\w:-]+)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)).Groups[1].Value.ToLowerInvariant();
            if (closing)
            {
                if (tag == "span" && captureAuthor) { author = Clean(authorText.ToString()); if (author.Length == 0) author = "Unknown"; groupAuthor = author; groupUser = user; captureAuthor = false; }
                if (tag == "div")
                {
                    if (contentDepth == depth) contentDepth = -1;
                    if (attachmentDepth == depth) { attachmentDepth = -1; href = ""; }
                    if (containerDepth == depth) { messages.Add(new(id, channel, source, author, user, timestamp, Clean(text.ToString()), attachments.ToArray())); containerDepth = contentDepth = attachmentDepth = -1; }
                    if (groupDepth == depth) groupDepth = -1;
                    depth--;
                }
                continue;
            }
            var attrs = ParseAttributes(value); var classes = attrs.GetValueOrDefault("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            if (tag == "div")
            {
                depth++;
                if (classes.Contains("chatlog__message-group")) { groupDepth = depth; groupAuthor = "Unknown"; groupUser = ""; }
                if (classes.Contains("chatlog__message-container") && attrs.TryGetValue("data-message-id", out var messageId)) { containerDepth = depth; id = messageId; author = groupAuthor; user = groupUser; timestamp = ""; text.Clear(); attachments.Clear(); }
                if (containerDepth >= 0 && classes.Contains("chatlog__short-timestamp")) timestamp = NormalizeTimestamp(attrs.GetValueOrDefault("title", ""));
                if (containerDepth >= 0 && classes.Contains("chatlog__content")) { contentDepth = depth; text.Clear(); }
                if (containerDepth >= 0 && classes.Contains("chatlog__attachment")) { attachmentDepth = depth; href = ""; }
            }
            else if (tag == "span" && containerDepth >= 0)
            {
                if (classes.Contains("chatlog__author")) { captureAuthor = true; authorText.Clear(); user = attrs.GetValueOrDefault("data-user-id", ""); }
                if (classes.Contains("chatlog__timestamp") || classes.Contains("chatlog__short-timestamp")) timestamp = NormalizeTimestamp(attrs.GetValueOrDefault("title", ""));
            }
            else if (tag == "a" && attachmentDepth >= 0) href = attrs.GetValueOrDefault("href", "");
            else if (tag == "img" && containerDepth >= 0)
            {
                if (contentDepth >= 0 && classes.Contains("chatlog__emoji")) text.Append(attrs.GetValueOrDefault("alt", ""));
                if (attachmentDepth >= 0 && classes.Contains("chatlog__attachment-media")) { var url = href.Length > 0 ? href : attrs.GetValueOrDefault("src", ""); var title = attrs.GetValueOrDefault("title", ""); var match = AttachmentNameRegex().Match(title); var row = new CompanionAttachment(url, match.Success ? match.Groups[1].Value : ""); if (!attachments.Contains(row)) attachments.Add(row); }
            }
            else if (tag == "br" && contentDepth >= 0) text.Append('\n');
        }
        return messages.ToArray();
    }

    public static CompanionImportSummary BuildEvidence(IEnumerable<string> sources, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory); var allPath = Path.Combine(outputDirectory, "messages.jsonl"); var evidencePath = Path.Combine(outputDirectory, "price_evidence.jsonl"); var dedupPath = Path.Combine(outputDirectory, "price_evidence_deduplicated.jsonl");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal); var dedup = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal); int messageCount = 0, attachmentCount = 0, evidenceCount = 0;
        using var all = new StreamWriter(allPath, false, new UTF8Encoding(false)); using var evidence = new StreamWriter(evidencePath, false, new UTF8Encoding(false));
        foreach (var source in sources)
        {
            var previous = new Queue<CompanionExportMessage>();
            foreach (var message in Read(source))
            {
                all.WriteLine(JsonSerializer.Serialize(message, Json.Options)); messageCount++; attachmentCount += message.Attachments.Length;
                var (classification, amounts) = Classify(message.Text); if (classification is null) { Remember(previous, message); continue; }
                var traits = ExtractTraits(message.Text); var context = string.Join('\n', previous.Select(p => p.Text).Append(message.Text)); var contextTraits = ExtractTraits(context); var fingerprint = Fingerprint(message);
                var record = new Dictionary<string, object?> { ["message_id"] = message.MessageId, ["channel"] = message.Channel, ["source_file"] = message.SourceFile, ["author"] = message.Author, ["user_id"] = message.UserId, ["timestamp"] = message.Timestamp, ["text"] = message.Text, ["attachments"] = message.Attachments, ["forwarded"] = message.Forwarded, ["classification"] = classification, ["amounts"] = amounts, ["traits"] = traits, ["context_traits"] = contextTraits, ["content_fingerprint"] = fingerprint };
                evidence.WriteLine(JsonSerializer.Serialize(record, Json.Options)); evidenceCount++; counts[classification] = counts.GetValueOrDefault(classification) + 1;
                var key = fingerprint.Length > 0 ? fingerprint : $"{message.SourceFile}:{message.MessageId}"; dedup[key] = record; Remember(previous, message);
            }
        }
        all.Flush(); evidence.Flush(); using (var output = new StreamWriter(dedupPath, false, new UTF8Encoding(false))) foreach (var record in dedup.Values) output.WriteLine(JsonSerializer.Serialize(record, Json.Options));
        return new(messageCount, attachmentCount, evidenceCount, dedup.Count, counts);
    }

    private static void Remember(Queue<CompanionExportMessage> queue, CompanionExportMessage message) { queue.Enqueue(message); while (queue.Count > 3) queue.Dequeue(); }
    private static string ChannelFromFilename(string path) { var match = Regex.Match(Path.GetFileName(path), @" - ([^\[]+?)\s*\[\d+\]\.html$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)); return match.Success ? match.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(path); }
    private static string NormalizeTimestamp(string value) { foreach (var format in new[] { "dddd, MMMM d, yyyy h:mm tt", "MMMM d, yyyy h:mm tt" }) if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture); return value; }
    private static string Clean(string value) { value = WebUtility.HtmlDecode(value).Replace("\r", ""); value = Regex.Replace(value, @"[ \t]+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(250)); value = Regex.Replace(value, @" *\n *", "\n", RegexOptions.None, TimeSpan.FromMilliseconds(250)); return Regex.Replace(value, @"\n{3,}", "\n\n", RegexOptions.None, TimeSpan.FromMilliseconds(250)).Trim(); }
    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var first = true;
        foreach (Match match in AttributeRegex().Matches(tag.Trim('<', '>', '/'))) { if (first) { first = false; continue; } var value = match.Groups["dq"].Success ? match.Groups["dq"].Value : match.Groups["sq"].Success ? match.Groups["sq"].Value : match.Groups["bare"].Value; result[match.Groups["key"].Value] = WebUtility.HtmlDecode(value); }
        return result;
    }
}
