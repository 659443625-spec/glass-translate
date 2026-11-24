# 构建「鼠标翻译」桌面程序（使用 Windows 自带的 csc 编译器，无需安装任何 SDK）
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $dist, $assets | Out-Null

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '找不到 csc.exe（需要 .NET Framework 4.x）' }

$wpf = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
if (-not (Test-Path (Join-Path $wpf 'UIAutomationClient.dll'))) {
    $wpf = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\WPF'
}

# ---------- 1. 生成应用图标（多尺寸 ICO，无需外部素材） ----------
$icoPath = Join-Path $assets 'app.ico'
function New-AppIcon {
    param([string]$Path)
    Add-Type -AssemblyName System.Drawing
    $sizes = @(16, 32, 48, 64, 128, 256)
    $pngs = New-Object System.Collections.ArrayList

    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        # macOS App Icon：四周留白 + 圆角方形 + 投影
        $pad = [int]($s * 0.085)
        $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad))
        $radius = [int]($rect.Width * 0.235)
        $d = $radius * 2

        $path2 = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path2.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path2.AddArc(($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
        $path2.AddArc(($rect.Right - $d), ($rect.Bottom - $d), $d, $d, 0, 90)
        $path2.AddArc($rect.X, ($rect.Bottom - $d), $d, $d, 90, 90)
        $path2.CloseFigure()

        # 柔和投影（多层低透明度）
        if ($s -ge 48) {
            for ($i = 3; $i -ge 1; $i--) {
                $sh = New-Object System.Drawing.Rectangle(($rect.X - $i), ($rect.Y - $i + [int]($s * 0.012)), ($rect.Width + 2 * $i), ($rect.Height + 2 * $i))
                $shp = New-Object System.Drawing.Drawing2D.GraphicsPath
                $rd = ($radius + $i) * 2
                $shp.AddArc($sh.X, $sh.Y, $rd, $rd, 180, 90)
                $shp.AddArc(($sh.Right - $rd), $sh.Y, $rd, $rd, 270, 90)
                $shp.AddArc(($sh.Right - $rd), ($sh.Bottom - $rd), $rd, $rd, 0, 90)
                $shp.AddArc($sh.X, ($sh.Bottom - $rd), $rd, $rd, 90, 90)
                $shp.CloseFigure()
                $sb2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(10, 12, 16, 40))
                $g.FillPath($sb2, $shp)
                $sb2.Dispose(); $shp.Dispose()
            }
        }

        # 靛蓝 → 紫罗兰 对角渐变
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 108, 138, 255),
            [System.Drawing.Color]::FromArgb(255, 128, 92, 250),
            55)
        $g.FillPath($brush, $path2)

        # 顶部内高光（苹果图标的那道亮边）
        if ($s -ge 32) {
            $hlPath = New-Object System.Drawing.Drawing2D.GraphicsPath
            $hlPath.AddEllipse($rect.X, ($rect.Y - [int]($rect.Height * 0.55)), $rect.Width, [int]($rect.Height * 1.0))
            $st = $g.Save()
            $g.SetClip($hlPath)
            $hlBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(30, 255, 255, 255))
            $g.FillPath($hlBrush, $path2)
            $g.Restore($st)
            $hlBrush.Dispose(); $hlPath.Dispose()
        }

        # 细内描边
        $penEdge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(46, 255, 255, 255), [Math]::Max(1, $s * 0.006))
        $g.DrawPath($penEdge, $path2)
        $penEdge.Dispose()

        # 白色「文 A」标记（苹果式：符号而非文字）
        $em = [float]($rect.Width * 0.30)
        $font = New-Object System.Drawing.Font('Microsoft YaHei UI', $em, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

        $cy = $rect.Y + $rect.Height * 0.50
        $leftRect = New-Object System.Drawing.RectangleF(($rect.X - $rect.Width * 0.10), ($cy - $em * 0.72), ($rect.Width * 0.62), ($em * 1.44))
        $rightRect = New-Object System.Drawing.RectangleF(($rect.X + $rect.Width * 0.34), ($cy - $em * 0.72), ($rect.Width * 0.62), ($em * 1.44))
        $g.DrawString('文', $font, $white, $leftRect, $sf)
        $g.DrawString('A', $font, $white, $rightRect, $sf)

        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        [void]$pngs.Add($ms.ToArray())
        $ms.Dispose(); $bmp.Dispose(); $brush.Dispose(); $white.Dispose(); $font.Dispose()
    }
    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $b = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$b); $bw.Write([byte]$b)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Close(); $fs.Close()
    Write-Host "icon  ->  $Path"
}

if (-not (Test-Path $icoPath)) { New-AppIcon -Path $icoPath }

# ---------- 2. 编译 ----------
$refs = @(
    (Join-Path $wpf 'UIAutomationClient.dll'),
    (Join-Path $wpf 'UIAutomationTypes.dll'),
    (Join-Path $wpf 'WindowsBase.dll'),
    'System.Web.Extensions.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll',
    'System.Net.Http.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Core.dll',
    (Join-Path $root 'lib\Microsoft.Web.WebView2.Core.dll'),
    (Join-Path $root 'lib\Microsoft.Web.WebView2.WinForms.dll')
)

$cscArgs = New-Object System.Collections.ArrayList
[void]$cscArgs.AddRange(@('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001'))
[void]$cscArgs.Add('/win32icon:"' + $icoPath + '"')
[void]$cscArgs.Add('/resource:"' + (Join-Path $assets 'card.html') + '",MouseTranslate.card.html')
[void]$cscArgs.Add('/resource:"' + (Join-Path $assets 'records.html') + '",MouseTranslate.records.html')
[void]$cscArgs.Add('/out:"' + (Join-Path $dist 'MouseTranslate.exe') + '"')
foreach ($r in $refs) { if ($r -notmatch '[\\/]' -or (Test-Path $r)) { [void]$cscArgs.Add('/r:"' + $r + '"') } else { Write-Host ("跳过缺失引用: " + $r) } }
Get-ChildItem $src -Filter *.cs | Sort-Object Name | ForEach-Object {
    [void]$cscArgs.Add('"' + $_.FullName + '"')
}

Write-Host "编译中…（$((Get-ChildItem $src -Filter *.cs).Count) 个源文件）"
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

$exe = Join-Path $dist 'MouseTranslate.exe'
Write-Host ("完成  ->  {0}  ({1:N0} KB)" -f $exe, ((Get-Item $exe).Length / 1KB))

# ---------- 3. 随包附带运行时脚本 ----------
$scriptsSrc = Join-Path $root 'scripts'
$scriptsDst = Join-Path $dist 'scripts'
if (Test-Path $scriptsSrc) {
    if (Test-Path $scriptsDst) { Remove-Item $scriptsDst -Recurse -Force }
    Copy-Item $scriptsSrc $scriptsDst -Recurse -Force
    Write-Host ("脚本  ->  {0}" -f $scriptsDst)
}

# ---------- 4. WebView2 依赖 ----------
$wvLib = Join-Path $root 'lib'
if (Test-Path $wvLib) {
    Get-ChildItem $wvLib -Filter *.dll | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $dist $_.Name) -Force
    }
    Write-Host ("WebView2 -> {0}" -f $dist)
}
# ---------- 5. 离线词库 ----------
$dictSrc = Join-Path $root 'assets\dict.bin'
if (Test-Path $dictSrc) {
    Copy-Item $dictSrc (Join-Path $dist 'dict.bin') -Force
    Write-Host ("离线词库 -> {0} ({1:N1} MB)" -f $dist, ((Get-Item $dictSrc).Length / 1MB))
}