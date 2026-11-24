param([string]$OutPath = '')
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $OutPath) { $OutPath = Join-Path (Join-Path $root 'app\docs') 'ui-main4.png' }
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WC {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
[void][WC]::SetProcessDPIAware()   # 关键：否则拿到的窗口坐标是被 DPI 虚拟化过的小尺寸
$proc = Get-Process MouseTranslate -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw 'MouseTranslate 未运行' }
$script:h = [IntPtr]::Zero
$script:best = 0
$cb = [WC+EnumProc]{
    param($w, $l)
    $pid2 = 0
    [void][WC]::GetWindowThreadProcessId($w, [ref]$pid2)
    if ($pid2 -eq $proc.Id -and [WC]::IsWindowVisible($w)) {
        $r = New-Object WC+RECT
        [void][WC]::GetWindowRect($w, [ref]$r)
        $area = ($r.R - $r.L) * ($r.B - $r.T)
        if ($area -gt $script:best) { $script:best = $area; $script:h = $w }
    }
    return $true
}
[void][WC]::EnumWindows($cb, [IntPtr]::Zero)
if ($script:h -eq [IntPtr]::Zero) { throw '找不到窗口' }
$r = New-Object WC+RECT
[void][WC]::GetWindowRect($script:h, [ref]$r)
$w = $r.R - $r.L; $ht = $r.B - $r.T
[void][WC]::SetForegroundWindow($script:h)
Start-Sleep -Milliseconds 400
Start-Sleep -Milliseconds 350
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
$g.Dispose()
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"captured ${w}x${ht} -> $OutPath"
