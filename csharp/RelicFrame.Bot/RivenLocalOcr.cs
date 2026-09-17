using System.Net;
using Discord;
using RelicFrame.Core;
using TesseractOCR;
using TesseractOCR.Enums;
using PixImage = TesseractOCR.Pix.Image;

internal sealed class RivenLocalOcr : IDisposable
{
    private const int MaximumBytes = 12 * 1024 * 1024;
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private Engine? engine;

    public async Task<RivenOcrDraft> AnalyzeAsync(string imageUrl, string filename, int reportedSize, IReadOnlyList<string> weapons, CancellationToken ct)
    {
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff"))
            throw new ArgumentException("Attach a PNG, JPG, WEBP, GIF, BMP, or TIFF image.");
        if (reportedSize > MaximumBytes) throw new ArgumentException("The Riven screenshot must be 12 MB or smaller.");
        using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new ArgumentException("The Riven screenshot must be 12 MB or smaller.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024]; int count;
        while ((count = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new ArgumentException("The Riven screenshot must be 12 MB or smaller.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        return await AnalyzeBytesAsync(buffer.ToArray(), weapons, ct);
    }

    internal async Task<RivenOcrDraft> AnalyzeBytesAsync(byte[] bytes, IReadOnlyList<string> weapons, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            engine ??= CreateEngine();
            using var original = PixImage.LoadFromMemory(bytes);
            if (original.Width < 220 || original.Height < 220) throw new ArgumentException("That image is too small for reliable Riven text recognition. Upload the original screenshot or a clearer crop.");
            var scale = Math.Clamp(2200f / Math.Max(original.Width, original.Height), 1.25f, 3f);
            using var enlarged = original.Scale(scale, scale);
            using var gray = enlarged.Depth >= 24 ? enlarged.ConvertRGBToGray() : enlarged.Clone();
            using var purple = enlarged.Depth >= 24 ? enlarged.ConvertRGBToGray(.45f, .10f, .45f) : enlarged.Clone();
            using var deskewed = gray.Deskew();
            using var otsu = gray.BinarizeOtsuAdaptiveThreshold(48, 48, 1, 1, .1f);
            using var purpleOtsu = purple.BinarizeOtsuAdaptiveThreshold(32, 32, 1, 1, .08f);
            using var sauvola = gray.BinarizeSauvolaTiled(24, .34f, 1, 1);
            var passes = new List<(string Text, float Confidence)>();
            Read(enlarged, PageSegMode.Auto, passes);
            Read(gray, PageSegMode.SingleColumn, passes);
            Read(purple, PageSegMode.SingleColumn, passes);
            Read(deskewed, PageSegMode.SingleBlock, passes);
            Read(otsu, PageSegMode.SparseText, passes);
            Read(purpleOtsu, PageSegMode.SparseText, passes);
            Read(sauvola, PageSegMode.SparseText, passes);
            var aspect = original.Width / (double)original.Height;
            if (aspect < .60)
            {
                // Phone screenshots often surround a centered card with navigation and
                // empty space. Region passes keep that UI from dominating page layout.
                Read(gray, Region(gray, .07, .15, .86, .68), PageSegMode.SingleColumn, passes);
                Read(purple, Region(purple, .14, .38, .72, .42), PageSegMode.SparseText, passes);
                Read(gray, Region(gray, .12, .53, .76, .14), PageSegMode.SingleLine, passes);
                Read(purple, Region(purple, .12, .53, .76, .14), PageSegMode.SparseText, passes);
                Read(gray, Region(gray, .53, .55, .30, .11), PageSegMode.SingleLine, passes);
                Read(purple, Region(purple, .53, .55, .30, .11), PageSegMode.SingleLine, passes);
            }
            else if (aspect > 1.20)
            {
                // Full-screen arsenal/Cycle views place the inspected Riven in the center.
                Read(gray, Region(gray, .25, .08, .50, .84), PageSegMode.SingleColumn, passes);
                Read(purple, Region(purple, .34, .18, .32, .70), PageSegMode.SparseText, passes);
            }
            else
            {
                // Tight card crops place MR and rerolls in a narrow footer. Dedicated
                // passes stop a stray footer glyph from outvoting the real reroll count.
                Read(gray, Region(gray, .04, .76, .92, .20), PageSegMode.SingleLine, passes);
                Read(purple, Region(purple, .04, .76, .92, .20), PageSegMode.SparseText, passes);
            }
            ct.ThrowIfCancellationRequested();
            var draft = RivenOcrText.Parse(passes, weapons);
            // The in-game card represents mod rank with decorative pips, not reliable
            // text. Never let a hallucinated "rank N/8" from border art affect Endo;
            // the editable confirmation asks the user for this field instead.
            if (draft.ModRank.HasValue)
                draft = draft with { ModRank = null, Notes = draft.Notes.Append("Mod rank needs verification; card pips are not inferred as text.").Distinct(StringComparer.Ordinal).ToArray() };
            return draft;
        }
        finally { gate.Release(); }
    }

    private void Read(PixImage image, PageSegMode mode, ICollection<(string Text, float Confidence)> destination)
    {
        using var page = engine!.Process(image, mode);
        if (!string.IsNullOrWhiteSpace(page.Text)) destination.Add((page.Text, page.MeanConfidence));
    }

    private void Read(PixImage image, Rect region, PageSegMode mode, ICollection<(string Text, float Confidence)> destination)
    {
        using var page = engine!.Process(image, region, mode);
        if (!string.IsNullOrWhiteSpace(page.Text)) destination.Add((page.Text, page.MeanConfidence));
    }

    private static Rect Region(PixImage image, double x, double y, double width, double height) => new(
        (int)Math.Round(image.Width * x), (int)Math.Round(image.Height * y),
        Math.Max(1, (int)Math.Round(image.Width * width)), Math.Max(1, (int)Math.Round(image.Height * height)));

    private static Engine CreateEngine()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(data, "eng.traineddata"))) throw new InvalidOperationException("English OCR data is missing from the bot output.");
        var result = new Engine(data, Language.English, EngineMode.LstmOnly);
        result.SetVariable("preserve_interword_spaces", "1");
        result.SetVariable("user_defined_dpi", "300");
        return result;
    }

    public void Dispose() { engine?.Dispose(); gate.Dispose(); }
}
