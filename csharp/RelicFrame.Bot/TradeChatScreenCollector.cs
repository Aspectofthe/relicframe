using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RelicFrame.Core;
using TesseractOCR;
using TesseractOCR.Enums;

sealed class TradeChatScreenCollector : IAsyncDisposable
{
    private readonly RivenTradeChat destination;
    private readonly ScreenTradeChatArchive archive;
    private readonly TimeSpan interval;
    private readonly ScreenRegion region;
    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task? worker;
    private string state = "stopped";
    private string previousFrame = "";
    private int captures;
    private int unchanged;
    private int imported;
    private int duplicates;
    private int unparsed;
    private int messages;
    private int repeatedMessages;
    private float confidence;

    public TradeChatScreenCollector(RivenTradeChat destination, string archivePath, string? regionText = null, int intervalSeconds = 5)
    {
        this.destination = destination;
        archive = new ScreenTradeChatArchive(archivePath);
        interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 3, 60));
        region = ScreenRegion.Parse(regionText);
    }

    public string Status
    {
        get
        {
            lock (gate)
                return $"{state}; {captures} changed captures/{unchanged} unchanged, OCR {confidence:P0}; messages {messages} new/{repeatedMessages} repeated; Riven offers {imported} new/{duplicates} duplicate, {unparsed} unparsed; region {region}";
        }
    }

    public void Start(CancellationToken lifetime)
    {
        lock (gate)
        {
            if (worker is { IsCompleted: false }) return;
            stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            state = "waiting for foreground Warframe";
            worker = Task.Run(() => RunAsync(stop.Token));
        }
    }

    public static (float Confidence, int CandidateMessages) ProfileFile(string path)
    {
        using var engine = CreateEngine();
        using var pixels = TesseractOCR.Pix.Image.LoadFromFile(Path.GetFullPath(path));
        using var page = engine.Process(pixels, PageSegMode.SparseText);
        return (page.MeanConfidence, TradeChatOcrText.Entries(page.Text).Length);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Engine? engine = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!TryCapture(out var bytes))
                    {
                        SetState("waiting for foreground Warframe");
                    }
                    else
                    {
                        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
                        if (fingerprint == previousFrame)
                        {
                            lock (gate) unchanged++;
                            SetState("Trade Chat visible; frame unchanged");
                        }
                        else
                        {
                            previousFrame = fingerprint;
                            engine ??= CreateEngine();
                            using var pixels = TesseractOCR.Pix.Image.LoadFromMemory(bytes);
                            using var page = engine.Process(pixels, PageSegMode.SparseText);
                            var visibleMessages = TradeChatOcrText.Messages(page.Text);
                            var archived = archive.Append(visibleMessages);
                            var entries = visibleMessages.Where(TradeChatOcrText.IsTradeOffer).ToArray();
                            var result = entries.Length == 0
                                ? new TradeChatImport([], 0, 0)
                                : destination.Import(string.Join('\n', entries), source: "screen-ocr-incoming");
                            lock (gate)
                            {
                                captures++;
                                confidence = page.MeanConfidence;
                                messages += archived.Added;
                                repeatedMessages += archived.Duplicates;
                                imported += result.Added.Length;
                                duplicates += result.DuplicateCount;
                                unparsed += result.UnparsedLineCount;
                            }
                            SetState(entries.Length == 0 ? "Trade Chat visible; no offers recognized" : "reading visible Trade Chat");
                        }
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    SetState("screen OCR unavailable: " + error.GetType().Name);
                }
                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            engine?.Dispose();
            SetState("stopped");
        }
    }

    private static Engine CreateEngine()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(data, "eng.traineddata")))
            throw new InvalidOperationException("English OCR data is missing from the bot output");
        var engine = new Engine(data, Language.English, EngineMode.LstmOnly);
        engine.SetVariable("preserve_interword_spaces", "1");
        return engine;
    }

    private bool TryCapture(out byte[] image)
    {
        image = [];
        if (!OperatingSystem.IsWindows()) return TryCaptureLinux(out image);
        return TryCaptureWindows(out image);
    }

    private bool TryCaptureWindows(out byte[] image)
    {
        image = [];
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || IsIconic(window) || !IsWarframe(window) || !GetClientRect(window, out var client)) return false;
        var clientWidth = client.Right - client.Left;
        var clientHeight = client.Bottom - client.Top;
        var crop = region.Pixels(clientWidth, clientHeight);
        if (crop.Width < 200 || crop.Height < 100) return false;
        image = CaptureClient(window, crop);
        return image.Length > 0;
    }

    private bool TryCaptureLinux(out byte[] image)
    {
        image = [];
        var captureFile = Environment.GetEnvironmentVariable("RELICFRAME_TRADE_OCR_CAPTURE_FILE");
        if (!string.IsNullOrWhiteSpace(captureFile) && File.Exists(captureFile))
        {
            var info = new FileInfo(captureFile);
            if (info.Length is <= 0 or > 16 * 1024 * 1024)
                throw new InvalidDataException("Trade OCR capture file must be between 1 byte and 16 MB.");
            image = File.ReadAllBytes(captureFile);
            return true;
        }

        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.Equals(session, "wayland", StringComparison.OrdinalIgnoreCase))
            return TryCaptureWayland(out image);
        return TryCaptureX11(out image);
    }

    private bool TryCaptureX11(out byte[] image)
    {
        image = [];
        var window = RunText("xdotool", "getactivewindow").Trim();
        if (window.Length == 0) return false;
        var title = RunText("xdotool", "getwindowname", window);
        if (!title.Contains("Warframe", StringComparison.OrdinalIgnoreCase)) return false;
        var geometry = RunText("xdotool", "getwindowgeometry", "--shell", window);
        var values = geometry.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("WIDTH", out var widthText) || !int.TryParse(widthText, out var width)
            || !values.TryGetValue("HEIGHT", out var heightText) || !int.TryParse(heightText, out var height)) return false;
        var crop = region.Pixels(width, height);
        if (crop.Width < 200 || crop.Height < 100) return false;
        var cropText = $"{crop.Width}x{crop.Height}+{crop.X}+{crop.Y}";
        try { image = RunBinary("magick", "import", "-window", window, "-crop", cropText, "png:-"); }
        catch (System.ComponentModel.Win32Exception) { image = RunBinary("import", "-window", window, "-crop", cropText, "png:-"); }
        return image.Length > 0;
    }

    private static bool TryCaptureWayland(out byte[] image)
    {
        image = [];
        if (!string.Equals(Environment.GetEnvironmentVariable("RELICFRAME_TRADE_OCR_ASSUME_WARFRAME"), "true", StringComparison.OrdinalIgnoreCase))
            return false;
        var geometry = Environment.GetEnvironmentVariable("RELICFRAME_TRADE_OCR_LINUX_GEOMETRY")?.Trim();
        if (string.IsNullOrWhiteSpace(geometry)) return false;
        var values = geometry.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 4 || values.Any(value => !int.TryParse(value, out _)))
            throw new InvalidDataException("RELICFRAME_TRADE_OCR_LINUX_GEOMETRY must be x,y,width,height in pixels.");
        var grimGeometry = $"{values[0]},{values[1]} {values[2]}x{values[3]}";
        image = RunBinary("grim", "-t", "png", "-g", grimGeometry, "-");
        return image.Length > 0;
    }

    private static string RunText(string executable, params string[] arguments) =>
        System.Text.Encoding.UTF8.GetString(RunBinary(executable, arguments));

    private static byte[] RunBinary(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        // Image capture can fill the OS pipe buffer. Drain stdout and stderr while
        // the capture process is running, otherwise WaitForExit can deadlock.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            Task.WhenAll(copy, error, process.WaitForExitAsync(timeout.Token))
                .WaitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{executable} did not finish within five seconds.");
        }
        var errorText = error.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} failed: {errorText}");
        if (output.Length > 16 * 1024 * 1024) throw new InvalidDataException($"{executable} returned an image larger than 16 MB.");
        return output.ToArray();
    }

    private static byte[] CaptureClient(IntPtr window, PixelRectangle crop)
    {
        var source = GetDC(window);
        if (source == IntPtr.Zero) return [];
        var target = CreateCompatibleDC(source);
        var bitmap = CreateCompatibleBitmap(source, crop.Width, crop.Height);
        if (target == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (target != IntPtr.Zero) DeleteDC(target);
            ReleaseDC(window, source);
            return [];
        }
        var previous = SelectObject(target, bitmap);
        try
        {
            if (!BitBlt(target, 0, 0, crop.Width, crop.Height, source, crop.X, crop.Y, 0x00CC0020)) return [];
            // GetDIBits requires the bitmap not to be selected into a DC.
            SelectObject(target, previous);
            var stride = ((crop.Width * 24 + 31) / 32) * 4;
            var pixels = new byte[stride * crop.Height];
            var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = 40, Width = crop.Width, Height = -crop.Height, Planes = 1, BitCount = 24, SizeImage = (uint)pixels.Length } };
            if (GetDIBits(target, bitmap, 0, (uint)crop.Height, pixels, ref info, 0) == 0) return [];
            using var output = new MemoryStream(54 + pixels.Length);
            using var writer = new BinaryWriter(output);
            writer.Write((ushort)0x4D42); writer.Write(54 + pixels.Length); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(crop.Width); writer.Write(-crop.Height); writer.Write((ushort)1); writer.Write((ushort)24);
            writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(pixels);
            return output.ToArray();
        }
        finally
        {
            SelectObject(target, previous);
            DeleteObject(bitmap);
            DeleteDC(target);
            ReleaseDC(window, source);
        }
    }

    private static bool IsWarframe(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Contains("Warframe", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private void SetState(string value) { lock (gate) state = value; }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? source;
        Task? task;
        lock (gate) { source = stop; task = worker; }
        source?.Cancel();
        if (task is not null) try { await task; } catch (OperationCanceledException) { }
        source?.Dispose();
    }

    private readonly record struct ScreenRegion(double X, double Y, double Width, double Height)
    {
        public static ScreenRegion Parse(string? value)
        {
            var parts = value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts is { Length: 4 }
                && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x)
                && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y)
                && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var width)
                && double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var height)
                && x >= 0 && y >= 0 && width > 0 && height > 0 && x + width <= 1 && y + height <= 1)
                return new(x, y, width, height);
            return new(0.00, 0.34, 0.72, 0.62);
        }

        public PixelRectangle Pixels(int width, int height) => new(
            (int)Math.Round(X * width), (int)Math.Round(Y * height),
            (int)Math.Round(Width * width), (int)Math.Round(Height * height));
        public override string ToString() => $"{X:0.##},{Y:0.##},{Width:0.##},{Height:0.##}";
    }

    [StructLayout(LayoutKind.Sequential)] private struct RectApi { public int Left, Top, Right, Bottom; }
    private readonly record struct PixelRectangle(int X, int Y, int Width, int Height);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[]? Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out RectApi rectangle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr deviceContext, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr deviceContext);
}
