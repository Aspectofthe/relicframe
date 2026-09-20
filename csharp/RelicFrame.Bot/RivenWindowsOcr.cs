using System.Diagnostics;
using System.Text;
using System.Text.Json;

// PowerToys Text Extractor uses this same Windows.Media.Ocr engine. A separate
// Windows PowerShell process keeps WinRT out of the cross-platform bot binary.
internal sealed class RivenWindowsOcr
{
    private bool unavailable;

    public async Task<string?> RecognizeAsync(byte[] image, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || unavailable ||
            Environment.GetEnvironmentVariable("RELICFRAME_WINDOWS_OCR") == "0") return null;
        var script = Path.Combine(AppContext.BaseDirectory, "riven-ocr", "windows-ocr.ps1");
        if (!File.Exists(script)) { unavailable = true; return null; }
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.StandardInput.WriteLineAsync(Convert.ToBase64String(image).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var json = await output;
            await error; // Drain errors without logging screenshot text or paths.
            if (process.ExitCode != 0) return null;
            using var result = JsonDocument.Parse(json);
            if (!result.RootElement.GetProperty("available").GetBoolean())
            {
                unavailable = true;
                Console.WriteLine("[riven-ocr] Windows OCR unavailable; using existing local OCR. Install the English Windows OCR language pack to enable it.");
                return null;
            }
            return result.RootElement.GetProperty("text").GetString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine($"[riven-ocr] Windows OCR pass skipped ({exception.GetType().Name}); using existing local OCR.");
            return null;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
