using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

/// <summary>
/// Local stat-line OCR adapted from WFHelper's MIT-licensed YOLO + PaddleOCR
/// pipeline. The detector isolates text rows so elemental icons and card art do
/// not confuse recognition. Whole-card Tesseract remains the independent
/// title/footer reader and fallback.
/// </summary>
internal sealed class RivenOnnxOcr : IDisposable
{
    private const int DetectorSize = 640;
    private const int RecognizerHeight = 48;
    private const int MaximumRecognizerWidth = 4096;
    private const long MaximumTensorBytes = 48L * 1024 * 1024;
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "riven-ocr");
    private InferenceSession? detector;
    private InferenceSession? recognizer;
    private string[] dictionary = [];
    private bool unavailable;

    private sealed record Box(int X1, int Y1, int X2, int Y2, float Confidence);
    private sealed record Line(string Text, float Confidence);

    public IReadOnlyList<(string Text, float Confidence)> Recognize(byte[] bytes, CancellationToken ct)
    {
        if (!EnsureLoaded()) return [];
        using var image = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("The Riven image could not be decoded.");
        var found = new List<Line>();
        foreach (var region in CandidateRegions(image.Width, image.Height))
        {
            ct.ThrowIfCancellationRequested();
            using var crop = Crop(image, region);
            foreach (var box in Detect(crop, ct).Take(6))
            {
                var padded = Pad(box, crop.Width, crop.Height, 8);
                using var lineImage = Crop(crop, new SKRectI(
                    padded.X1, padded.Y1, padded.X2, padded.Y2));
                var line = ReadLine(lineImage, ct);
                if (line is { Text.Length: > 0 }) found.Add(line);
            }
        }
        return MergeSplitLines(found)
            .GroupBy(line => Normalize(line.Text), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(line => line.Confidence).First())
            .Where(line => line.Text.Any(char.IsDigit))
            .OrderByDescending(line => line.Confidence)
            .Take(8)
            .Select(line => (line.Text, line.Confidence)).ToArray();
    }

    private bool EnsureLoaded()
    {
        if (unavailable) return false;
        if (detector is not null && recognizer is not null) return true;
        var detectorPath = Path.Combine(root, "yolo", "stat_line_detector.onnx");
        var recognizerPath = Path.Combine(root, "paddle", "ch_PP-OCRv3_rec_infer.onnx");
        var dictionaryPath = Path.Combine(root, "paddle", "ch_dict.txt");
        if (!File.Exists(detectorPath) || !File.Exists(recognizerPath) || !File.Exists(dictionaryPath))
        {
            unavailable = true;
            return false;
        }
        if (!HasSha256(detectorPath, "D3C29D871AE1872D507E284607E657E7DDC05BC56343CD3BA9AF46F3E483160A")
            || !HasSha256(recognizerPath, "897A3EDEDB38FEE0DAE2C1CCEE38241F37DF202C9509E3ABCA02E9217C5EE615")
            || !HasSha256(dictionaryPath, "C084FA990ECF0CE7FCB6BD5A3B689382645EC454E6F6AF712B3D6BE0ADDFDAF5"))
        {
            Console.WriteLine("[riven-ocr] WFHelper OCR asset integrity check failed; using Tesseract fallback");
            unavailable = true;
            return false;
        }
        try
        {
            var cores = Environment.ProcessorCount;
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Clamp(cores - 4, 2, 4)
            };
            detector = new InferenceSession(detectorPath, options);
            recognizer = new InferenceSession(recognizerPath, options);
            dictionary = ["", .. File.ReadAllLines(dictionaryPath)];
            return true;
        }
        catch (Exception error) when (error is OnnxRuntimeException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[riven-ocr] ONNX unavailable ({error.GetType().Name}); using Tesseract fallback");
            unavailable = true;
            detector?.Dispose(); recognizer?.Dispose(); detector = null; recognizer = null;
            return false;
        }
    }
    private static bool HasSha256(string path, string expected)
    {
        using var source = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(source)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<Box> Detect(SKBitmap source, CancellationToken ct)
    {
        var scale = Math.Min(DetectorSize / (double)source.Width, DetectorSize / (double)source.Height);
        var resizedWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var resizedHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
        var padLeft = (DetectorSize - resizedWidth) / 2;
        var padTop = (DetectorSize - resizedHeight) / 2;
        using var resized = Resize(source, resizedWidth, resizedHeight);
        using var canvas = new SKBitmap(DetectorSize, DetectorSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var graphics = new SKCanvas(canvas))
        {
            graphics.Clear(new SKColor(114, 114, 114));
            graphics.DrawBitmap(resized, padLeft, padTop, new SKSamplingOptions(SKFilterMode.Linear), null);
        }
        var tensor = new DenseTensor<float>([1, 3, DetectorSize, DetectorSize]);
        for (var y = 0; y < DetectorSize; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < DetectorSize; x++)
            {
                var pixel = canvas.GetPixel(x, y);
                tensor[0, 0, y, x] = pixel.Red / 255f;
                tensor[0, 1, y, x] = pixel.Green / 255f;
                tensor[0, 2, y, x] = pixel.Blue / 255f;
            }
        }
        var input = NamedOnnxValue.CreateFromTensor(detector!.InputMetadata.Keys.First(), tensor);
        using var results = detector.Run([input]);
        var output = results.First().AsTensor<float>();
        var dimensions = output.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[1] < 5) return [];
        var count = dimensions[2];
        var boxes = new List<Box>();
        for (var index = 0; index < count; index++)
        {
            var confidence = output[0, 4, index];
            if (confidence < .25f) continue;
            var centerX = output[0, 0, index]; var centerY = output[0, 1, index];
            var width = output[0, 2, index]; var height = output[0, 3, index];
            var x1 = (int)Math.Round((centerX - width / 2 - padLeft) / scale);
            var y1 = (int)Math.Round((centerY - height / 2 - padTop) / scale);
            var x2 = (int)Math.Round((centerX + width / 2 - padLeft) / scale);
            var y2 = (int)Math.Round((centerY + height / 2 - padTop) / scale);
            x1 = Math.Clamp(x1, 0, source.Width); x2 = Math.Clamp(x2, 0, source.Width);
            y1 = Math.Clamp(y1, 0, source.Height); y2 = Math.Clamp(y2, 0, source.Height);
            if (x2 - x1 >= 20 && y2 - y1 is >= 4 and <= 120) boxes.Add(new(x1, y1, x2, y2, confidence));
        }
        var kept = new List<Box>();
        foreach (var box in boxes.OrderByDescending(box => box.Confidence))
            if (kept.All(other => IntersectionOverUnion(box, other) <= .5)) kept.Add(box);
        return kept.OrderBy(box => box.Y1);
    }

    private Line? ReadLine(SKBitmap source, CancellationToken ct)
    {
        var width = Math.Min(MaximumRecognizerWidth,
            Math.Max(1, (int)Math.Ceiling(RecognizerHeight * source.Width / (double)Math.Max(1, source.Height))));
        var outputBytes = (long)Math.Ceiling(width / 8d) * Math.Max(1, dictionary.Length) * sizeof(float);
        if (outputBytes > MaximumTensorBytes) return null;
        using var resized = Resize(source, width, RecognizerHeight);
        var tensor = new DenseTensor<float>([1, 3, RecognizerHeight, width]);
        for (var y = 0; y < RecognizerHeight; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var pixel = resized.GetPixel(x, y);
                tensor[0, 0, y, x] = pixel.Red / 127.5f - 1;
                tensor[0, 1, y, x] = pixel.Green / 127.5f - 1;
                tensor[0, 2, y, x] = pixel.Blue / 127.5f - 1;
            }
        }
        var input = NamedOnnxValue.CreateFromTensor(recognizer!.InputMetadata.Keys.First(), tensor);
        using var results = recognizer.Run([input]);
        var output = results.First().AsTensor<float>();
        var dimensions = output.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[0] != 1) return null;
        var text = new List<string>(); var confidence = new List<float>(); var previous = 0;
        for (var step = 0; step < dimensions[1]; step++)
        {
            var best = 0; var bestValue = output[0, step, 0];
            for (var character = 1; character < dimensions[2]; character++)
                if (output[0, step, character] > bestValue) { best = character; bestValue = output[0, step, character]; }
            if (best != 0 && best != previous && best < dictionary.Length)
            {
                text.Add(dictionary[best]); confidence.Add(bestValue);
            }
            previous = best;
        }
        if (text.Count == 0) return null;
        return new(Postprocess(string.Concat(text)), confidence.Average());
    }

    private static IEnumerable<SKRectI> CandidateRegions(int width, int height)
    {
        var aspect = width / (double)height;
        var targeted = aspect switch
        {
            < .60 => new[] { (.07, .15, .86, .68), (.14, .38, .72, .42) },
            > 1.20 => new[] { (.25, .08, .50, .84), (.34, .18, .32, .70) },
            _ => new[] { (.04, .28, .92, .58) }
        };
        // A stat-only crop is already the detector's ideal input. Layout-specific
        // crops then cover full screenshots and phone captures.
        return new[] { (0d, 0d, 1d, 1d) }.Concat(targeted).Select(value => new SKRectI(
            Math.Clamp((int)Math.Round(width * value.Item1), 0, width - 1),
            Math.Clamp((int)Math.Round(height * value.Item2), 0, height - 1),
            Math.Clamp((int)Math.Round(width * (value.Item1 + value.Item3)), 1, width),
            Math.Clamp((int)Math.Round(height * (value.Item2 + value.Item4)), 1, height)))
            .Select(region => new SKRectI(region.Left, region.Top,
                Math.Min(region.Right, width), Math.Min(region.Bottom, height)))
            .Distinct();
    }

    private static SKBitmap Crop(SKBitmap source, SKRectI region)
    {
        var result = new SKBitmap(region.Width, region.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var graphics = new SKCanvas(result);
        graphics.Clear(SKColors.Black);
        graphics.DrawBitmap(source, region, new SKRect(0, 0, result.Width, result.Height),
            new SKSamplingOptions(SKFilterMode.Linear), null);
        return result;
    }
    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var graphics = new SKCanvas(result);
        graphics.Clear(SKColors.Black);
        graphics.DrawBitmap(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
        return result;
    }

    private static Box Pad(Box box, int width, int height, int amount) => new(
        Math.Max(0, box.X1 - amount), Math.Max(0, box.Y1 - amount),
        Math.Min(width, box.X2 + amount), Math.Min(height, box.Y2 + amount), box.Confidence);
    private static double IntersectionOverUnion(Box left, Box right)
    {
        var intersectionWidth = Math.Max(0, Math.Min(left.X2, right.X2) - Math.Max(left.X1, right.X1));
        var intersectionHeight = Math.Max(0, Math.Min(left.Y2, right.Y2) - Math.Max(left.Y1, right.Y1));
        var intersection = intersectionWidth * intersectionHeight;
        var union = (left.X2 - left.X1) * (left.Y2 - left.Y1) + (right.X2 - right.X1) * (right.Y2 - right.Y1) - intersection;
        return union <= 0 ? 0 : intersection / (double)union;
    }
    private static string Postprocess(string value) => value
        .Replace("*-", "-", StringComparison.Ordinal)
        .Replace("Mmpact", "Impact", StringComparison.Ordinal)
        .Replace("Aditional", "Additional", StringComparison.Ordinal)
        .Replace("Damageto", "Damage to", StringComparison.Ordinal)
        .Replace(".,", ",", StringComparison.Ordinal)
        .Replace("..", ".", StringComparison.Ordinal);
    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9%+\-.,x]+", "");
    private static IReadOnlyList<Line> MergeSplitLines(IReadOnlyList<Line> source)
    {
        var result = new List<Line>();
        for (var index = 0; index < source.Count; index++)
        {
            var current = source[index];
            if (index + 1 < source.Count && Regex.IsMatch(current.Text, @"(?:critical chance|additional combo|chance to gain|heavy attack|magazine|fire rate|status|reload|ammo|weapon)\s*$", RegexOptions.IgnoreCase)
                && !source[index + 1].Text.Any(char.IsDigit))
            {
                result.Add(new(current.Text.TrimEnd() + " " + source[index + 1].Text.TrimStart(), Math.Min(current.Confidence, source[index + 1].Confidence)));
                index++;
            }
            else result.Add(current);
        }
        return result;
    }
    public void Dispose() { detector?.Dispose(); recognizer?.Dispose(); }
}
