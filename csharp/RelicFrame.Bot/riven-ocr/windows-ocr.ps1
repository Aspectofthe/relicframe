# Windows PowerShell 5.1 supplies the WinRT bridge used by Windows.Media.Ocr.
# Input is a base64 image on stdin; output is JSON. Images stay in memory.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
try {
    $encoded = [Console]::In.ReadLine()
    if ($null -eq $encoded -or $encoded.Length -gt 16777216) { throw 'OCR input is missing or too large.' }
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.Streams.DataWriter, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
    [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    function Await-WinRt($operation, [Type]$resultType) {
        $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
        $task.GetAwaiter().GetResult()
    }
    $language = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages |
        Where-Object { $_.LanguageTag -like 'en-*' -or $_.LanguageTag -eq 'en' } | Select-Object -First 1
    if ($null -eq $language) {
        [Console]::Write('{"available":false,"reason":"English Windows OCR language pack is not installed."}')
        exit 0
    }
    $ocr = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
    if ($null -eq $ocr) { throw 'Windows OCR engine is unavailable.' }
    $imageBytes = [Convert]::FromBase64String($encoded)
    $stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
    $writer = New-Object Windows.Storage.Streams.DataWriter($stream)
    $writer.WriteBytes($imageBytes)
    Await-WinRt ($writer.StoreAsync()) ([uint32]) | Out-Null
    $writer.DetachStream() | Out-Null
    $writer.Dispose()
    $stream.Seek(0)
    $decoder = Await-WinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $longest = [Math]::Max($decoder.PixelWidth, $decoder.PixelHeight)
    $target = [Math]::Min(2200, [Windows.Media.Ocr.OcrEngine]::MaxImageDimension)
    $maxScale = if ($longest -lt 220) { 12.0 } else { 3.0 }
    $scale = [Math]::Min($maxScale, $target / [double][Math]::Max(1, $longest))
    $transform = New-Object Windows.Graphics.Imaging.BitmapTransform
    $transform.ScaledWidth = [uint32][Math]::Max(1, [Math]::Floor($decoder.PixelWidth * $scale))
    $transform.ScaledHeight = [uint32][Math]::Max(1, [Math]::Floor($decoder.PixelHeight * $scale))
    $bitmap = Await-WinRt ($decoder.GetSoftwareBitmapAsync(
        [Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,
        [Windows.Graphics.Imaging.BitmapAlphaMode]::Ignore, $transform,
        [Windows.Graphics.Imaging.ExifOrientationMode]::RespectExifOrientation,
        [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $result = Await-WinRt ($ocr.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $text = ($result.Lines | ForEach-Object { $_.Text }) -join "`n"
    [Console]::Write((@{ available = $true; text = $text } | ConvertTo-Json -Compress))
    $bitmap.Dispose()
    $stream.Dispose()
} catch {
    [Console]::Write('{"available":false,"reason":"Windows OCR could not process this image."}')
    exit 1
}
