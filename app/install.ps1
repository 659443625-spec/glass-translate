# 一键安装：编译（如需要）→ 创建桌面和开始菜单快捷方式
param(
    [switch]$Rebuild,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'dist\MouseTranslate.exe'

if ($Rebuild -or -not (Test-Path $exe)) {
    Write-Host '正在编译…'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
    if (-not (Test-Path $exe)) { throw '编译失败：找不到 dist\MouseTranslate.exe' }
}

$ws = New-Object -ComObject WScript.Shell

# 桌面快捷方式
$desktop = [Environment]::GetFolderPath('Desktop')
$desktopLnk = Join-Path $desktop '鼠标翻译.lnk'
$s = $ws.CreateShortcut($desktopLnk)
$s.TargetPath = $exe
$s.WorkingDirectory = (Join-Path $root 'dist')
$s.IconLocation = $exe + ',0'
$s.Description = '鼠标翻译：在任意软件里划词或悬停，点图标出译文'
$s.Save()
Write-Host ('桌面快捷方式 -> ' + $desktopLnk)

# 开始菜单快捷方式
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) '鼠标翻译'
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
$startLnk = Join-Path $startMenu '鼠标翻译.lnk'
$s2 = $ws.CreateShortcut($startLnk)
$s2.TargetPath = $exe
$s2.WorkingDirectory = (Join-Path $root 'dist')
$s2.IconLocation = $exe + ',0'
$s2.Description = '鼠标翻译'
$s2.Save()
Write-Host ('开始菜单     -> ' + $startLnk)

if (-not $NoLaunch) {
    Start-Process $exe
    Write-Host '已启动。点窗口里的「启动翻译」即可开始使用。'
}
