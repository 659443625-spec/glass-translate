# OCR 常驻子进程：从 stdin 收「图片路径」，向 stdout 回 JSON（base64 包裹）。
# 之所以用 PowerShell 而不是在 C# 里直接调 WinRT：本机没有 System.Runtime.WindowsRuntime
# 引用程序集，C# 侧编译 WinRT 投影会很脆；PowerShell 5.1 能直接吃 WinRT，且常驻后
# 单次 OCR 只要 40~150ms，完全够用。
#
# 注意：不要用 [Console]::In.ReadLineAsync() 做超时轮询 —— 在 .NET Framework 的
# 重定向管道上它是同步阻塞的，会导致看门狗失效、误留孤儿进程。这里用 BeginRead +
# AsyncWaitHandle.WaitOne 实现真正的可中断读取。
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Add-Type -AssemblyName System.Runtime.WindowsRuntime

[Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
[Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime] | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function Await($op, $resultType) {
    $asTask = $asTaskGeneric.MakeGenericMethod($resultType)
    $task = $asTask.Invoke($null, @($op))
    if (-not $task.Wait(15000)) { throw 'winrt-timeout' }
    $task.Result
}

# 英文优先；没有英文识别器就用系统语言兜底
$engine = $null
try {
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language('en-US')))
} catch { }
if (-not $engine) {
    try { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() } catch { }
}
if (-not $engine) {
    [Console]::Out.WriteLine('READY 0')
    [Console]::Out.Flush()
    exit 1
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
[Console]::Out.WriteLine('READY 1')
[Console]::Out.Flush()

function Convert-OcrToJson($result) {
    $lines = New-Object System.Collections.ArrayList

    foreach ($ln in $result.Lines) {
        $ws = @($ln.Words)
        if ($ws.Count -eq 0) { continue }

        $words = New-Object System.Collections.ArrayList
        $minX = [double]::MaxValue
        $minY = [double]::MaxValue
        $maxR = [double]::MinValue
        $maxB = [double]::MinValue

        foreach ($w in $ws) {
            $br = $w.BoundingRect
            $x = [double]$br.X
            $y = [double]$br.Y
            $ww = [double]$br.Width
            $hh = [double]$br.Height

            [void]$words.Add([ordered]@{
                t = [string]$w.Text
                x = [int][Math]::Round($x)
                y = [int][Math]::Round($y)
                w = [int][Math]::Round($ww)
                h = [int][Math]::Round($hh)
            })

            if ($x -lt $minX) { $minX = $x }
            if ($y -lt $minY) { $minY = $y }
            if (($x + $ww) -gt $maxR) { $maxR = $x + $ww }
            if (($y + $hh) -gt $maxB) { $maxB = $y + $hh }
        }

        [void]$lines.Add([ordered]@{
            t     = [string]$ln.Text
            x     = [int][Math]::Round($minX)
            y     = [int][Math]::Round($minY)
            w     = [int][Math]::Round($maxR - $minX)
            h     = [int][Math]::Round($maxB - $minY)
            words = @($words)
        })
    }

    $obj = [ordered]@{
        ok    = $true
        text  = [string]$result.Text
        lines = @($lines)
    }
    return ($obj | ConvertTo-Json -Depth 6 -Compress)
}

function Invoke-Line($line) {
    if ($line -eq 'PING') {
        [Console]::Out.WriteLine('PONG')
        [Console]::Out.Flush()
        return
    }
    if (-not $line.StartsWith('OCR ')) { return }

    $json = $null
    $stream = $null
    try {
        $path = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line.Substring(4)))

        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile])
        $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $sb = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $result = Await ($engine.RecognizeAsync($sb)) ([Windows.Media.Ocr.OcrResult])

        $json = Convert-OcrToJson $result
    }
    catch {
        $where = ''
        try { $where = ' @line ' + $_.InvocationInfo.ScriptLineNumber } catch { }
        $json = (@{ ok = $false; error = ([string]$_.Exception.Message + $where) } | ConvertTo-Json -Compress)
    }
    finally {
        if ($stream) { try { $stream.Dispose() } catch { } }
    }

    $b64 = [Convert]::ToBase64String($utf8.GetBytes($json))
    [Console]::Out.WriteLine('RES ' + $b64)
    [Console]::Out.Flush()
}

# ---------- 主循环：可中断读取 + 父进程看门狗 ----------
$parentPid = 0
if ($env:MT_PARENT_PID) {
    [int]::TryParse([string]$env:MT_PARENT_PID, [ref]$parentPid) | Out-Null
}

function Test-ParentAlive {
    if ($parentPid -le 0) { return $true }
    return $null -ne (Get-Process -Id $parentPid -ErrorAction SilentlyContinue)
}

$stdin = [Console]::OpenStandardInput()
$buf = New-Object byte[] 8192
$pending = New-Object System.Text.StringBuilder

while ($true) {
    $ar = $stdin.BeginRead($buf, 0, $buf.Length, $null, $null)
    while (-not $ar.AsyncWaitHandle.WaitOne(1200)) {
        if (-not (Test-ParentAlive)) { exit 0 }
    }
    $n = $stdin.EndRead($ar)
    if ($n -le 0) { break }

    [void]$pending.Append($utf8.GetString($buf, 0, $n))

    while ($true) {
        $text = $pending.ToString()
        $idx = $text.IndexOf("`n")
        if ($idx -lt 0) { break }

        $line = $text.Substring(0, $idx).Trim()
        [void]$pending.Remove(0, $idx + 1)
        if ($line.Length -eq 0) { continue }

        Invoke-Line $line
    }
}
