param(
    [string]$ShotDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $ShotDir) { $ShotDir = Join-Path $root 'app\docs' }
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
public class E2E {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }

  public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
  public const int BM_CLICK = 0x00F5;

  public static void ForceForeground(IntPtr h) {
    ShowWindow(h, 9);
    int fgPid;
    IntPtr fg = GetForegroundWindow();
    uint fgThread = (uint)GetWindowThreadProcessId(fg, out fgPid);
    uint myThread = GetCurrentThreadId();
    AttachThreadInput(myThread, fgThread, true);
    SetForegroundWindow(h);
    AttachThreadInput(myThread, fgThread, false);
    Thread.Sleep(260);
  }

  public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
  public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

  public static void BringToFront(IntPtr h) {
    ShowWindow(h, 9);
    SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE|NOMOVE|NOACTIVATE
    Thread.Sleep(400);
  }

  public static void DropFromFront(IntPtr h) {
    SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
  }

  public static void Drag(int x1, int y1, int x2, int y2) {
    SetCursorPos(x1, y1);
    Thread.Sleep(200);
    mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
    int steps = 16;
    for (int i = 1; i <= steps; i++) {
      SetCursorPos(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
      Thread.Sleep(18);
    }
    Thread.Sleep(150);
    mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
  }

  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    Thread.Sleep(170);
    mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
    Thread.Sleep(80);
    mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
  }

  public static IntPtr FindChildButton(IntPtr parent, string text) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
      StringBuilder sb = new StringBuilder(256);
      GetWindowText(h, sb, 256);
      if (sb.ToString() == text) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@
[void][E2E]::SetProcessDPIAware()

function Get-Windows([int]$procId) {
    $script:out = @()
    $cb = [E2E+EnumProc]{
        param($w, $l)
        $pid2 = 0
        [void][E2E]::GetWindowThreadProcessId($w, [ref]$pid2)
        if ($procId -le 0 -or $pid2 -eq $procId) {
            if ([E2E]::IsWindowVisible($w)) {
                $sb = New-Object System.Text.StringBuilder 512
                [void][E2E]::GetWindowText($w, $sb, 512)
                $rr = New-Object E2E+RECT
                [void][E2E]::GetWindowRect($w, [ref]$rr)
                $script:out += [pscustomobject]@{
                    Pid = $pid2; Handle = $w; Title = $sb.ToString()
                    X = $rr.L; Y = $rr.T; W = ($rr.R - $rr.L); H = ($rr.B - $rr.T)
                }
            }
        }
        return $true
    }
    [void][E2E]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:out
}

function Save-Window($win, [string]$path) {
    if (-not $win -or $win.W -le 0 -or $win.H -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($win.W, $win.H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][E2E]::PrintWindow($win.Handle, $hdc, 2)
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$results = New-Object System.Collections.ArrayList
$app = $null
$chrome = $null

try {
    # ---------- 1. 造一个可拖选的英文页面 ----------
    $html = Join-Path $env:TEMP 'mt-e2e.html'
    @'
<!doctype html><html><head><meta charset="utf-8"><title>MT-E2E</title>
<style>body{font:24px/1.9 "Segoe UI",sans-serif;margin:40px;max-width:1100px}</style></head>
<body>
<p id="a">Reading in a second language is mostly a test of patience.</p>
<p id="b">If you stop at every unfamiliar word, you lose the thread of the argument.</p>
<p id="c">A good tool answers in a fraction of a second and then disappears.</p>
<p id="d">Resilience is the ability to recover quickly from difficulty.</p>
</body></html>
'@ | Set-Content -LiteralPath $html -Encoding UTF8

    $chromePath = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
    $chrome = Start-Process $chromePath -ArgumentList @('--new-window', ('"file:///' + ($html -replace '\\','/') + '"')) -PassThru
    Start-Sleep -Seconds 4

    $chWin = (Get-Windows 0) | Where-Object { $_.Title -like '*MT-E2E*' -and $_.W -gt 500 -and $_.H -gt 400 } | Select-Object -First 1
    if (-not $chWin) { throw '找不到 Chrome 测试页窗口' }
    [void]$results.Add("Chrome 窗口: $($chWin.W)x$($chWin.H) @ $($chWin.X),$($chWin.Y)")

    # ---------- 2. 启动程序，用 BM_CLICK 点「启动翻译」 ----------
    Get-Process MouseTranslate -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
    $app = Start-Process (Join-Path $root 'app\dist\MouseTranslate.exe') -PassThru
    Start-Sleep -Seconds 3

    $main = (Get-Windows $app.Id) | Where-Object { $_.Title -eq '鼠标翻译' } | Select-Object -First 1
    if (-not $main) { throw '主窗口未出现' }

    # 新版界面是自绘按钮（没有子窗口句柄），直接按布局坐标点击：
    # 主按钮在逻辑坐标 (22 + innerW/2, 170 + 24)，窗口是无边框的所以 client == window
    [void][E2E]::ForceForeground($main.Handle)
    Start-Sleep -Milliseconds 400
    $f = [E2E]::GetDpiForSystem() / 96.0
    $bx = $main.X + [int]((22 + (520 - 44) / 2) * $f)
    $by = $main.Y + [int]((170 + 24) * $f)
    [E2E]::Click($bx, $by)
    Start-Sleep -Seconds 2
    [void]$results.Add('① 已通过 BM_CLICK 触发『启动翻译』')

    Start-Sleep -Milliseconds 500
    $left = (Get-Windows $app.Id) | Where-Object { $_.Title -eq '鼠标翻译' -and $_.W -gt 200 }
    [void]$results.Add("② 面板是否自动收进托盘: " + $(if ($left) { '否 ✘' } else { '是 ✔' }))

    # ---------- 3. 到 Chrome 里真实拖选一行 ----------
    [E2E]::BringToFront($chWin.Handle)
    [E2E]::ForceForeground($chWin.Handle)
    Start-Sleep -Milliseconds 900
    $lineY = $chWin.Y + 190          # 第 2 段文字大约位置
    [E2E]::Drag($chWin.X + 90, $lineY, $chWin.X + 900, $lineY)
    Start-Sleep -Milliseconds 1800
    [void]$results.Add("③ 已在 Chrome 内拖选一行（y=$lineY）")
    Save-Window $chWin (Join-Path $ShotDir 'e2e-selection.png')

    # ---------- 4. 图标 ----------
    $icons = (Get-Windows $app.Id) | Where-Object { $_.W -ge 20 -and $_.W -le 90 -and $_.H -ge 20 -and $_.H -le 90 }
    if (-not $icons) {
        [void]$results.Add('④ 小图标: 未出现 ✘')
    }
    else {
        $ic = $icons | Select-Object -First 1
        [void]$results.Add("④ 小图标: 已出现 $($ic.W)x$($ic.H) @ $($ic.X),$($ic.Y) ✔")
        Save-Window $ic (Join-Path $ShotDir 'e2e-icon.png')

        [E2E]::Click([int]($ic.X + $ic.W / 2), [int]($ic.Y + $ic.H / 2))
        Start-Sleep -Seconds 3
        $card = (Get-Windows $app.Id) | Where-Object { $_.Title -eq '翻译' } | Select-Object -First 1
        if ($card) {
            [void]$results.Add("⑤ 翻译卡片: 已弹出 $($card.W)x$($card.H) ✔")
            Save-Window $card (Join-Path $ShotDir 'e2e-card.png')
        }
        else {
            [void]$results.Add('⑤ 翻译卡片: 未出现 ✘')
        }
    }
}
catch {
    [void]$results.Add('异常: ' + $_.Exception.Message)
}
finally {
    Write-Output ($results -join "`r`n")
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($chrome) { Stop-Process -Id $chrome.Id -Force -ErrorAction SilentlyContinue }
}
