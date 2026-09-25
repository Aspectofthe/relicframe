using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public sealed record EeLogCursor(long Offset, string HeadFingerprint);

public sealed class EeLogTradeChatCollector : IAsyncDisposable
{
    private static readonly Regex OutgoingPublicMessage = new(
        @"\bIRC\s+out:\s+PRIVMSG\s+#[^\s]+\s+:(?<message>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex TradeAction = new(@"\b(?:WTS|WTB|WTT)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private const int ReadBudget = 1024 * 1024;
    private readonly string logPath;
    private readonly string cursorPath;
    private readonly RivenTradeChat destination;
    private readonly Func<EeLogCompletedTrade, CancellationToken, Task>? completedTrade;
    private readonly bool collectOutgoing;
    private EeLogCompletedTradeParser tradeParser = new();
    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task? worker;
    private long offset;
    private string headFingerprint = "";
    private int imported;
    private int duplicates;
    private int unparsed;
    private string state = "stopped";

    public EeLogTradeChatCollector(string logPath, string cursorPath, RivenTradeChat destination,
        Func<EeLogCompletedTrade, CancellationToken, Task>? completedTrade = null, bool collectOutgoing = true)
    {
        this.logPath = Path.GetFullPath(logPath);
        this.cursorPath = Path.GetFullPath(cursorPath);
        this.destination = destination;
        this.completedTrade = completedTrade;
        this.collectOutgoing = collectOutgoing;
        // A newly enabled trade watcher must not replay older trades whose stock
        // is already reflected in AlecaFrame's snapshot.
        if (completedTrade is not null && !File.Exists(this.cursorPath) && File.Exists(this.logPath))
            offset = new FileInfo(this.logPath).Length;
        try
        {
            if (File.Exists(this.cursorPath))
            {
                var saved = Json.Read<EeLogCursor>(this.cursorPath);
                offset = Math.Max(0, saved.Offset);
                headFingerprint = saved.HeadFingerprint ?? "";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            offset = 0; headFingerprint = "";
        }
    }

    public string LogPath => logPath;
    public string Status
    {
        get
        {
            lock (gate)
                return $"{state}; outgoing offers {imported} new/{duplicates} duplicate, {unparsed} unparsed. Incoming public chat is not written by Warframe.";
        }
    }

    public void Start(CancellationToken lifetime)
    {
        lock (gate)
        {
            if (worker is { IsCompleted: false }) return;
            stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            state = "starting";
            worker = Task.Run(() => RunAsync(stop.Token));
        }
    }

    public async Task CollectOnceAsync(CancellationToken ct = default)
    {
        if (!File.Exists(logPath))
        {
            SetState("waiting for EE.log");
            return;
        }

        var info = new FileInfo(logPath);
        var currentHead = await FingerprintAsync(logPath, ct);
        if (info.Length < offset || (headFingerprint.Length > 0 && currentHead.Length > 0 && !headFingerprint.Equals(currentHead, StringComparison.Ordinal)))
        {
            offset = 0;
            tradeParser = new();
            headFingerprint = currentHead;
        }
        if (headFingerprint.Length == 0) headFingerprint = currentHead;
        if (info.Length <= offset)
        {
            SetState("watching EE.log");
            return;
        }

        byte[] bytes;
        await using (var input = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            input.Seek(offset, SeekOrigin.Begin);
            var wanted = (int)Math.Min(ReadBudget, input.Length - offset);
            bytes = new byte[wanted];
            var read = 0;
            while (read < wanted)
            {
                var count = await input.ReadAsync(bytes.AsMemory(read, wanted - read), ct);
                if (count == 0) break;
                read += count;
            }
            if (read != bytes.Length) Array.Resize(ref bytes, read);
        }

        var complete = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        if (complete == 0)
        {
            SetState("watching EE.log; waiting for a complete line");
            return;
        }
        var text = Encoding.UTF8.GetString(bytes, 0, complete);
        offset += complete;
        var messages = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (completedTrade is not null && tradeParser.Consume(line, DateTimeOffset.UtcNow) is { } trade)
            {
                try { await completedTrade(trade, ct); }
                catch
                {
                    // The cursor has not advanced yet. Replay the full chunk,
                    // including its confirmation, after a transient failure.
                    tradeParser = new();
                    throw;
                }
            }
            if (!collectOutgoing) continue;
            var match = OutgoingPublicMessage.Match(line);
            if (!match.Success) continue;
            var message = match.Groups["message"].Value.Trim();
            if (TradeAction.IsMatch(message)) messages.Add("Self: " + message);
        }
        if (messages.Count > 0)
        {
            var result = destination.Import(string.Join('\n', messages), source: "ee-log-outgoing");
            lock (gate)
            {
                imported += result.Added.Length;
                duplicates += result.DuplicateCount;
                unparsed += result.UnparsedLineCount;
            }
        }
        SaveCursor();
        SetState(info.Length - offset > 0 ? "catching up on EE.log" : "watching EE.log");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await CollectOnceAsync(ct); }
                catch (IOException error) { SetState("EE.log temporarily unavailable: " + error.GetType().Name); }
                catch (UnauthorizedAccessException) { SetState("cannot read EE.log"); }
                catch (Exception error) when (!ct.IsCancellationRequested && error is not OutOfMemoryException)
                { SetState("EE.log trade check failed: " + error.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { SetState("stopped"); }
    }

    private static async Task<string> FingerprintAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length < 4096) return "";
        var bytes = new byte[4096];
        var read = await input.ReadAsync(bytes, ct);
        return read == 0 ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, read))).ToLowerInvariant();
    }

    private void SaveCursor()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cursorPath)!);
        Json.WriteAtomic(cursorPath, new EeLogCursor(offset, headFingerprint));
    }

    private void SetState(string value) { lock (gate) state = value; }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? source; Task? task;
        lock (gate) { source = stop; task = worker; }
        source?.Cancel();
        if (task is not null) try { await task; } catch (OperationCanceledException) { }
        source?.Dispose();
    }
}
