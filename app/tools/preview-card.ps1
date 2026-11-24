param(
    [string]$Theme = "light",
    [string]$Text = "resilience",
    [string]$OutPath = '',
    [int]$WaitSeconds = 5
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $OutPath) { $OutPath = Join-Path (Join-Path $root 'app\docs') 'ui-card-word.png' }
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ECap {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(P cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Ri,B; }
}
"@
[void][ECap]::SetProcessDPIAware()

$exe = Join-Path $root 'app\dist\MouseTranslate.exe'
$p = Start-Process $exe -ArgumentList @('--theme', $(if($Theme){$Theme}else{'light'}), '--card', ('"' + $Text + '"')) -PassThru
Start-Sleep -Seconds $WaitSeconds

$script:list = @()
$cb = [ECap+P]{
    param($w, $l)
    $pid2 = 0
    [void][ECap]::GetWindowThreadProcessId($w, [ref]$pid2)
    if ($pid2 -eq $p.Id -and [ECap]::IsWindowVisible($w)) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][ECap]::GetWindowText($w, $sb, 256)
        $rr = New-Object ECap+R
        [void][ECap]::GetWindowRect($w, [ref]$rr)
        $script:list += [pscustomobject]@{ H = $w; T = $sb.ToString(); W = ($rr.Ri - $rr.L); Hh = ($rr.B - $rr.T); L = $rr.L; Tp = $rr.T }
    }
    return $true
}
[void][ECap]::EnumWindows($cb, [IntPtr]::Zero)

$card = $script:list | Where-Object { $_.T -eq '翻译' } | Select-Object -First 1
if (-not $card) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; throw '未找到卡片窗口' }

[void][ECap]::SetForegroundWindow($card.H)
Start-Sleep -Milliseconds 400
$bmp = New-Object System.Drawing.Bitmap($card.W, $card.Hh)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($card.L, $card.Tp, 0, 0, (New-Object System.Drawing.Size($card.W, $card.Hh)))
$g.Dispose()
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
"card $($card.W)x$($card.Hh) -> $OutPath"
