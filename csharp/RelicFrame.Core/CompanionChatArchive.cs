using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public sealed record CompanionChatEntry(string Id, DateTimeOffset Timestamp, string Author, string Text, string[] SourceFiles, string[] Attachments);
public sealed record CompanionChatSummary(string Input, string Output, int Messages, int TextFiles, int Images);

public static class CompanionChatArchive
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

    public static CompanionChatSummary Build(string inputDirectory, string outputDirectory)
    {
        var root = Path.GetFullPath(inputDirectory); var output = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Input folder does not exist: {root}");
        if (output.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Output must not overwrite the input folder.");
        Directory.CreateDirectory(output); var assets = Path.Combine(output, "assets"); Directory.CreateDirectory(assets);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !IsWithin(path, output) && (TextExtensions.Contains(Path.GetExtension(path)) || ImageExtensions.Contains(Path.GetExtension(path)))).ToArray();
        var entries = files.GroupBy(path => (Parent: Path.GetRelativePath(root, Path.GetDirectoryName(path)!).ToLowerInvariant(), Stem: Path.GetFileNameWithoutExtension(path).ToLowerInvariant()))
            .Select(group => CreateEntry(root, assets, group.Order(StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderBy(entry => entry.Timestamp).ThenBy(entry => entry.SourceFiles[0], StringComparer.OrdinalIgnoreCase).ToArray();
        using (var jsonl = new StreamWriter(Path.Combine(output, "messages.jsonl"), false, new UTF8Encoding(false))) foreach (var entry in entries) jsonl.WriteLine(JsonSerializer.Serialize(entry, Json.Options));
        var transcript = new StringBuilder(); foreach (var entry in entries) { transcript.AppendLine($"[{entry.Timestamp:O}] {entry.Author}"); if (entry.Text.Length > 0) transcript.AppendLine(entry.Text); foreach (var attachment in entry.Attachments) transcript.AppendLine($"[Attachment: {attachment}]"); transcript.AppendLine(); }
        File.WriteAllText(Path.Combine(output, "transcript.txt"), transcript.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "index.html"), Render(entries, Path.GetFileName(root) + " — Imported Chat"), new UTF8Encoding(false));
        return new(root, output, entries.Length, entries.Sum(e => e.SourceFiles.Count(f => TextExtensions.Contains(Path.GetExtension(f)))), entries.Sum(e => e.Attachments.Length));
    }

    private static CompanionChatEntry CreateEntry(string root, string assetsDirectory, string[] files)
    {
        var relative = files.Select(path => Path.GetRelativePath(root, path)).ToArray();
        var text = string.Join("\n\n", files.Where(path => TextExtensions.Contains(Path.GetExtension(path))).Select(ReadText).Where(value => value.Length > 0));
        var copied = files.Where(path => ImageExtensions.Contains(Path.GetExtension(path))).Select(path =>
        {
            var rel = Path.GetRelativePath(root, path); var prefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rel)))[..10].ToLowerInvariant();
            var name = prefix + "-" + Regex.Replace(Path.GetFileName(path), @"[^A-Za-z0-9._-]+", "-").Trim('.', '-'); var target = Path.Combine(assetsDirectory, name); File.Copy(path, target, true); return "assets/" + name;
        }).ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', relative))))[..16].ToLowerInvariant();
        var parent = Path.GetDirectoryName(relative[0]); var author = string.IsNullOrEmpty(parent) ? "Imported" : Path.GetFileName(parent);
        return new(digest, files.Select(FileTime).Min(), author, text, relative, copied);
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Trim();
        if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2).Trim();
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff').Trim(); } catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes).Trim(); }
    }

    private static DateTimeOffset FileTime(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        foreach (var (pattern, yearFirst) in new[] { (@"(?<!\d)(20\d{2})[-_.](\d{1,2})[-_.](\d{1,2})(?:[ T_-](\d{1,2})[-_.:](\d{2})(?:[-_.:](\d{2}))?)?", true), (@"(?<!\d)(\d{1,2})[-_.](\d{1,2})[-_.](20\d{2})(?:[ T_-](\d{1,2})[-_.:](\d{2})(?:[-_.:](\d{2}))?)?", false) })
        {
            var match = Regex.Match(name, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250)); if (!match.Success) continue;
            var v = match.Groups.Cast<Group>().Skip(1).Select(g => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : 0).ToArray();
            try { return yearFirst ? new(v[0], v[1], v[2], v[3], v[4], v[5], TimeSpan.Zero) : new(v[2], v[0], v[1], v[3], v[4], v[5], TimeSpan.Zero); } catch (ArgumentOutOfRangeException) { }
        }
        return File.GetLastWriteTimeUtc(path);
    }

    private static bool IsWithin(string path, string directory)
    {
        var full = Path.GetFullPath(path); var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Render(IEnumerable<CompanionChatEntry> entries, string title)
    {
        var body = new StringBuilder(); string? day = null;
        foreach (var entry in entries)
        {
            var current = entry.Timestamp.UtcDateTime.ToString("MMMM dd, yyyy", CultureInfo.InvariantCulture); if (current != day) { body.Append($"<div class=day><span>{WebUtility.HtmlEncode(current)}</span></div>"); day = current; }
            var author = WebUtility.HtmlEncode(entry.Author); var text = WebUtility.HtmlEncode(entry.Text).Replace("\n", "<br>"); var media = string.Join("", entry.Attachments.Select(a => $"<a href=\"{WebUtility.HtmlEncode(a)}\"><img loading=lazy src=\"{WebUtility.HtmlEncode(a)}\" alt=\"Saved attachment\"></a>"));
            body.Append($"<article><div class=avatar>{WebUtility.HtmlEncode((entry.Author.FirstOrDefault() == default ? '?' : char.ToUpperInvariant(entry.Author[0])).ToString())}</div><div><strong>{author}</strong><time>{entry.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm} UTC</time><div>{text}</div><div class=attachments>{media}</div></div></article>");
        }
        return """<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>$TITLE$</title><style>body{margin:0;background:#313338;color:#dbdee1;font:16px/1.35 Arial}header{padding:16px 24px;background:#1e1f22;position:sticky;top:0}main{max-width:1100px;margin:auto;padding-bottom:60px}article{display:flex;gap:14px;padding:10px 24px}article:hover{background:#2e3035}.avatar{width:40px;height:40px;border-radius:50%;display:grid;place-items:center;background:#5865f2;font-weight:bold}time{font-size:12px;color:#949ba4;margin-left:8px}.attachments{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px}img{max-width:460px;max-height:520px;border-radius:8px;object-fit:contain}.day{text-align:center;color:#949ba4;margin:20px}</style></head><body><header><b>$TITLE$</b></header><main>$BODY$</main></body></html>"""
            .Replace("$TITLE$", WebUtility.HtmlEncode(title), StringComparison.Ordinal).Replace("$BODY$", body.ToString(), StringComparison.Ordinal);
    }
}
