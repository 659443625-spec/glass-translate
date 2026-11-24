# 发布前精修证据图：在受控的玻璃底图（临时无边框渐变窗口）上截真实运行窗口。
# 用完即杀进程 + 关底图窗口，不启动任何常驻 GUI。
param(
    [string]$Mode = "card",
    [string]$Theme = "dark",
    [string]$Text = "resilience",
    [string]$Out = '',
    [int]$WaitMs = 3500,
    [int]$IconX = 1180,
    [int]$IconY = 460
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Out) { $Out = Join-Path (Join-Path $root 'app\docs') 'polish-card-word-dark.png' }
# 先清掉上次残留的进程（截图前保证屏幕/窗口唯一）
Get-Process MouseTranslate -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$exe = Join-Path $root 'app\dist\MouseTranslate.exe'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class PCap {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(P cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  public static void Wheel(int delta) { mouse_event(0x0800, 0, 0, (uint)delta, IntPtr.Zero); }
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
}
"@
[void][PCap]::SetProcessDPIAware()

function Pump([int]$ms) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 10
    }
}

function Get-Luma([System.Drawing.Bitmap]$bmp, [int]$x, [int]$y) {
    $c = $bmp.GetPixel($x, $y)
    return ($c.R * 299 + $c.G * 587 + $c.B * 114) / 1000.0
}

# ---------- 受控玻璃底图：中性渐变 + 两团柔光，保证玻璃透而干净（不会把卡片推到厚玻璃档） ----------
function New-Backdrop([int]$w, [int]$h, [bool]$dark) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    if ($dark) {
        $c1 = [System.Drawing.Color]::FromArgb(255, 116, 126, 144)
        $c2 = [System.Drawing.Color]::FromArgb(255, 38, 42, 51)
        $glows = @(
            @(0.18, 0.10, 0.85, 186, 198, 216, 120),
            @(0.80, 0.72, 0.95, 70, 96, 148, 70),
            @(0.46, 0.52, 0.55, 210, 220, 235, 40)
        )
    } else {
        $c1 = [System.Drawing.Color]::FromArgb(255, 203, 212, 226)
        $c2 = [System.Drawing.Color]::FromArgb(255, 140, 152, 172)
        $glows = @(
            @(0.16, 0.08, 0.85, 255, 255, 255, 170),
            @(0.82, 0.74, 0.95, 150, 180, 220, 90),
            @(0.42, 0.46, 0.55, 250, 236, 226, 70)
        )
    }
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 68)
    $g.FillRectangle($lg, $rect)
    $lg.Dispose()
    foreach ($gl in $glows) {
        $d = [int]($w * $gl[2])
        $x = [int]($w * $gl[0]) - [int]($d / 2)
        $y = [int]($h * $gl[1]) - [int]($d / 2)
        $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $gp.AddEllipse($x, $y, $d, $d)
        $pbr = New-Object System.Drawing.Drawing2D.PathGradientBrush($gp)
        $pbr.CenterColor = [System.Drawing.Color]::FromArgb($gl[6], $gl[3], $gl[4], $gl[5])
        $pbr.SurroundColors = [System.Drawing.Color[]]@([System.Drawing.Color]::FromArgb(0, $gl[3], $gl[4], $gl[5]))
        $g.FillPath($pbr, $gp)
        $pbr.Dispose(); $gp.Dispose()
    }
    $g.Dispose()
    return $bmp
}

function Show-Backdrop([int]$x, [int]$y, [int]$w, [int]$h, [bool]$dark) {
    $form = New-Object System.Windows.Forms.Form
    $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $form.ShowInTaskbar = $false
    $form.TopMost = $false
    $form.SetBounds($x, $y, $w, $h)
    $img = New-Backdrop $w $h $dark
    $form.BackgroundImage = $img
    $form.BackgroundImageLayout = [System.Windows.Forms.ImageLayout]::None
    $form.Show()
    Pump 260
    return @{ Form = $form; Bitmap = $img }
}

function Get-AppWindows([int]$procId) {
    $script:wins = @()
    $cb = [PCap+P]{
        param($hw, $lp)
        $p2 = 0
        [void][PCap]::GetWindowThreadProcessId($hw, [ref]$p2)
        if ($p2 -eq $procId -and [PCap]::IsWindowVisible($hw)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][PCap]::GetWindowText($hw, $sb, 256)
            $rr = New-Object PCap+R
            [void][PCap]::GetWindowRect($hw, [ref]$rr)
            $script:wins += [pscustomobject]@{
                H = $hw; T = $sb.ToString(); L = $rr.L; Tp = $rr.T
                W = ($rr.Ri - $rr.L); Hh = ($rr.B - $rr.T)
            }
        }
        return $true
    }
    [void][PCap]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:wins
}

function Save-Region([int]$x, [int]$y, [int]$w, [int]$h, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Save-Window([object]$win, [string]$path) {
    [void][PCap]::SetForegroundWindow($win.H)
    Pump 320
    Save-Region $win.L $win.Tp $win.W $win.Hh $path
}

$dark = ($Theme -eq "dark")
$proc = $null
$bd = $null
try {
    switch ($Mode) {
        "panel" {
            # 面板出现在屏幕中央：底图铺在中心区域（两张预览窗都覆盖）
            $bd = Show-Backdrop 780 400 1000 820 $dark
            $proc = Start-Process $exe -ArgumentList @('--theme', $Theme, '--webview-test') -PassThru
            Pump 5200
            $wins = Get-AppWindows $proc.Id
            $panel = $wins | Where-Object { $_.T -eq '鼠标翻译' -and $_.W -ge 700 } | Sort-Object Tp | Select-Object -First 1
            if (-not $panel) { throw "未找到面板窗口" }
            Save-Window $panel $Out
            "panel $($panel.W)x$($panel.Hh) @$($panel.L),$($panel.Tp) -> $Out"
        }
        "records" {
            # 记录窗口在 (180,90) 690x900（150% DPI）；悬停第二行 + 滚一格，展示 hover 高亮与滚动时才出现的细滚动条
            $bd = Show-Backdrop 130 55 800 990 $dark
            $proc = Start-Process $exe -ArgumentList @('--theme', $Theme, '--records') -PassThru
            Pump 3000
            $wins = Get-AppWindows $proc.Id
            $rec = $wins | Where-Object { $_.T -eq '翻译记录' } | Select-Object -First 1
            if (-not $rec) { throw "未找到记录窗口" }
            [void][PCap]::SetForegroundWindow($rec.H)
            Pump 260
            $hx = $rec.L + [int]($rec.W * 0.42)
            $hy = $rec.Tp + [int]($rec.Hh * 0.42)
            [void][PCap]::SetCursorPos($hx, $hy)     # 先让光标进列表，滚轮才会滚到列表
            Pump 150
            [PCap]::Wheel(-360)
            Pump 120
            [void][PCap]::SetCursorPos(($hx + 2), ($hy + 2))   # 再动 2px：让 Chromium 重算 :hover
            Pump 200
            Save-Region $rec.L $rec.Tp $rec.W $rec.Hh $Out
            "records $($rec.W)x$($rec.Hh) -> $Out"
        }
        "tooltip" {
            # 徽标 + 悬停 tooltip：底图铺在徽标/气泡区域
            $bd = Show-Backdrop ($(if ($IconX - 160 -lt 0) { 0 } else { $IconX - 160 })) ($(if ($IconY - 120 -lt 0) { 0 } else { $IconY - 120 })) 460 420 $dark
            $proc = Start-Process $exe -ArgumentList @('--theme', $Theme, '--icon-live', $IconX, $IconY, 30) -PassThru
            Pump 1800
            [void][PCap]::SetCursorPos($IconX + 30, $IconY + 30)
            Pump 900
            $cx = $IconX - 90
            $cy = $IconY - 40
            if ($cx -lt 0) { $cx = 0 }
            if ($cy -lt 0) { $cy = 0 }
            Save-Region $cx $cy 300 210 $Out
            "tooltip -> $Out"
        }
        default {
            # card / loading / error / copy：卡片固定在 (177,168)，宽 645
            $bd = Show-Backdrop 120 120 860 900 $dark
            $argText = '"' + $Text + '"'
            $proc = Start-Process $exe -ArgumentList @('--theme', $Theme, '--card', $argText) -PassThru
            $card = $null
            $deadline = [DateTime]::UtcNow.AddMilliseconds(9000)
            while ([DateTime]::UtcNow -lt $deadline) {
                Pump 40
                $wins = Get-AppWindows $proc.Id
                $card = $wins | Where-Object { $_.T -eq '翻译' } | Select-Object -First 1
                if ($card) {
                    if ($Mode -ne "loading") { break }
                    if ($card.Hh -ge 260) { break }   # 初始 140 DIP（210px）→ 加载态测量完成后约 320px
                }
            }
            if (-not $card) { throw "未找到卡片窗口" }
            if ($Mode -eq "loading") {
                # 加载态窗口很短（翻译 300~900ms 就回来，而且可能快过 WebView2 首帧）：
                # 连拍挑“已绘制、高度稳定、低于最终结果高度”的帧；抓不到就重启再试。
                $best = $null
                $argText = '"' + $Text + '"'
                $attempt = 0
                while ($attempt -lt 10 -and -not $best) {
                    $attempt++
                    if ($attempt -gt 1) {
                        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
                        Start-Sleep -Milliseconds 260
                        $card = $null
                        $proc = Start-Process $exe -ArgumentList @('--theme', $Theme, '--card', $argText) -PassThru
                        $deadline2 = [DateTime]::UtcNow.AddMilliseconds(9000)
                        while ([DateTime]::UtcNow -lt $deadline2 -and -not $card) {
                            Pump 30
                            $wins = Get-AppWindows $proc.Id
                            $card = $wins | Where-Object { $_.T -eq '翻译' } | Select-Object -First 1
                        }
                        if (-not $card) { continue }
                    }
                    $tmp = Join-Path $env:TEMP ("mt-loading-{0}" -f $attempt)
                    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
                    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
                    $frames = @()
                    $sw = [System.Diagnostics.Stopwatch]::StartNew()
                    $lastH = -1
                    while ($sw.ElapsedMilliseconds -lt 2800) {
                        Pump 25
                        $wins = Get-AppWindows $proc.Id
                        $card = $wins | Where-Object { $_.T -eq '翻译' } | Select-Object -First 1
                        if (-not $card) { continue }
                        $fp = Join-Path $tmp ("f{0:d3}.png" -f $frames.Count)
                        Save-Region $card.L $card.Tp $card.W $card.Hh $fp
                        $frames += [pscustomobject]@{ Path = $fp; H = $card.Hh; Stable = ($card.Hh -eq $lastH) }
                        $lastH = $card.Hh
                    }
                    if ($frames.Count -lt 3) { continue }
                    $finalH = $frames[$frames.Count - 1].H
                    for ($fi = 1; $fi -lt $frames.Count - 1; $fi++) {
                        $f = $frames[$fi]
                        $stable = ($f.H -eq $frames[$fi - 1].H) -or ($f.H -eq $frames[$fi + 1].H)
                        if (-not $stable) { continue }                     # 高度基本稳定
                        if ($f.H -le 230) { continue }                     # 初始空窗 210
                        if ($f.H -ge ($finalH - 8)) { continue }           # 最终结果高度
                        $bmp = [System.Drawing.Bitmap]::FromFile($f.Path)
                        # 撕裂检测：窗口底边/中部几处都必须是卡片自己的深色玻璃，不能是背后的底图（luma 100+）
                        $torn = $false
                        if ((Get-Luma $bmp ([int]($bmp.Width / 2)) ($bmp.Height - 8)) -gt 85) { $torn = $true }
                        if (-not $torn -and (Get-Luma $bmp ([int]($bmp.Width / 3)) ($bmp.Height - 8)) -gt 85) { $torn = $true }
                        if (-not $torn -and (Get-Luma $bmp ([int]($bmp.Width / 2)) 55) -gt 85) { $torn = $true }
                        if ($torn) { $bmp.Dispose(); continue }
                        # 已绘制检测：工具条以下、页脚以上应有正文亮像素（WebView2 首帧之前是纯 form 底色）
                        $bright = 0
                        for ($y = 55; $y -lt $bmp.Height - 95; $y += 13) {
                            for ($x = 8; $x -lt $bmp.Width - 8; $x += 13) {
                                $c = $bmp.GetPixel($x, $y)
                                $l = ($c.R * 299 + $c.G * 587 + $c.B * 114) / 1000.0
                                if ($l -gt 105) { $bright++ }
                            }
                        }
                        $bmp.Dispose()
                        if ($bright -lt 4) { continue }
                        $best = $f                                          # 取最后一个稳定加载帧
                    }
                }
                if (-not $best) { throw "10 次都没抓到加载态帧（翻译快过 WebView2 首帧）" }
                Copy-Item $best.Path $Out -Force
                "loading $($best.H)px attempt=$attempt -> $Out"
            }
            elseif ($Mode -eq "copy") {
                Pump $WaitMs
                $wins = Get-AppWindows $proc.Id
                $card = $wins | Where-Object { $_.T -eq '翻译' } | Select-Object -First 1
                [void][PCap]::SetForegroundWindow($card.H)
                Pump 200
                # 复制按钮：距右边缘 124 DIP、工具条中线 24 DIP（150% DPI）
                $cx = $card.L + $card.W - [int](124 * 1.5)
                $cy = $card.Tp + [int](24 * 1.5)
                [void][PCap]::SetCursorPos($cx, $cy)
                Pump 120
                [PCap]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
                Pump 60
                [PCap]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
                Pump 380
                Save-Region $card.L $card.Tp $card.W $card.Hh $Out
                "copy $($card.W)x$($card.Hh) -> $Out"
            } else {
                Pump $WaitMs
                $wins = Get-AppWindows $proc.Id
                $card = $wins | Where-Object { $_.T -eq '翻译' } | Select-Object -First 1
                if (-not $card) { throw "卡片窗口消失" }
                Save-Window $card $Out
                "card $($card.W)x$($card.Hh) -> $Out"
            }
        }
    }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    if ($bd) { try { $bd.Form.Close(); $bd.Form.Dispose(); $bd.Bitmap.Dispose() } catch { } }
    Start-Sleep -Milliseconds 150
}
