using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record ScreenTradeChatMessage(string Id, string ObservedAt, string Text, string Source = "screen-ocr-incoming");
public sealed record ScreenTradeChatImport(int Added, int Duplicates);

public sealed class ScreenTradeChatArchive
{
    private const long MaximumBytes = 16 * 1024 * 1024;
    private readonly string path;
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public ScreenTradeChatArchive(string path)
    {
        this.path = Path.GetFullPath(path);
        if (!File.Exists(this.path)) return;
        try
        {
            foreach (var line in File.ReadLines(this.path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ScreenTradeChatMessage>(line, Json.Options) is { Id.Length: > 0 } row) ids.Add(row.Id);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public ScreenTradeChatImport Append(IEnumerable<string> messages, DateTimeOffset? observed = null)
    {
        var stamp = (observed ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var day = stamp.ToString("yyyy-MM-dd");
        var rows = messages.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value =>
        {
            var text = string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(day + "\0" + text.ToLowerInvariant()))).ToLowerInvariant()[..20];
            return new ScreenTradeChatMessage(id, stamp.ToString("O"), text);
        }).DistinctBy(row => row.Id).ToArray();
        lock (gate)
        {
            var added = rows.Where(row => ids.Add(row.Id)).ToArray();
            var lines = added.Select(row => JsonSerializer.Serialize(row, Json.Options) + "\n").ToArray();
            var bytes = lines.Sum(Encoding.UTF8.GetByteCount);
            if ((File.Exists(path) ? new FileInfo(path).Length : 0) + bytes > MaximumBytes)
            {
                foreach (var row in added) ids.Remove(row.Id);
                throw new InvalidDataException("Visible Trade Chat archive reached 16 MiB; move or remove it before collecting more.");
            }
            if (lines.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllLines(path, lines.Select(line => line.TrimEnd('\n')), Encoding.UTF8);
                }
                catch
                {
                    foreach (var row in added) ids.Remove(row.Id);
                    throw;
                }
            }
            return new(added.Length, rows.Length - added.Length);
        }
    }
}
