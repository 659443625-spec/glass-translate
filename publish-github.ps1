# 一键发布到 GitHub（v1.0.0）
#
# 用法（在仓库根目录）：
#   1) 第一次先登录：   gh auth login
#   2) 运行本脚本：     powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-github.ps1
#
# 脚本会：替换 README 里的 OWNER 占位 → 提交 → 创建公开仓库 → 推送 → 打 v1.0.0 标签 → 创建 Release 并上传安装包与离线词库。
# 如果仓库已存在，则只推送与创建 Release（幂等，可重复运行）。

param(
    [string]$RepoName = "glass-translate",
    [string]$Description = "Windows 划词翻译 · Apple 玻璃质感 · 离线词库 · 自然人声朗读，无需配置开箱即用",
    [string]$Version = "v1.0.0"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

function Info($m) { Write-Host "  $m" }
function Fail($m) { Write-Host "  [失败] $m" -ForegroundColor Red; exit 1 }

$ghCandidates = @(
    "C:\Program Files\GitHub CLI\gh.exe",
    "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe",
    (Get-Command gh -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $ghCandidates) { Fail "未找到 GitHub CLI（gh）。请先安装：winget install GitHub.cli" }
$gh = $ghCandidates

$git = @("C:\Program Files\Git\cmd\git.exe", (Get-Command git -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)) |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $git) { Fail "未找到 git。请先安装：winget install Git.Git" }

Write-Host "== 1/7 检查 GitHub 登录 =="
$ErrorActionPreference = "Continue"   # gh/git 的 stderr 在 PS5.1 会抛错，这里降级为可判断
& $gh auth status 2>&1 | Out-Null
$ghLoggedIn = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = "Stop"
if (-not $ghLoggedIn) {
    Write-Host "  尚未登录 GitHub。请先执行下面这条，再重新运行本脚本：" -ForegroundColor Yellow
    Write-Host "      gh auth login" -ForegroundColor Cyan
    exit 2
}
$owner = (& $gh api user -q .login).Trim()
Info "已登录：$owner"

Write-Host "== 2/7 替换 README 占位符 =="
$readmePath = Join-Path $root "README.md"
$readme = [IO.File]::ReadAllText($readmePath, [Text.Encoding]::UTF8)
$before = $readme
$readme = $readme.Replace("OWNER/", "$owner/")
$readme = $readme.Replace("github.com/OWNER", "github.com/$owner")
# 删除给 Lead 的占位注释块与文末标记
$readme = [regex]::Replace($readme, "<!--[\s\S]*?上线前请全局替换[\s\S]*?-->", "")
$readme = $readme.Replace("<!-- LEAD:OWNER -->", "")
if ($readme -cmatch 'OWNER') { Fail "README 仍有 OWNER 占位（自动替换未覆盖），已中止发布：请先手动把 README.md 里的仓库地址替换为真实 owner" }
if ($readme -ne $before) {
    [IO.File]::WriteAllText($readmePath, $readme, (New-Object Text.UTF8Encoding($false)))
    Info "README 已指向 https://github.com/$owner/$RepoName"
} else {
    Info "README 无需替换（已是真实地址）"
}

Write-Host "== 3/7 提交改动 =="
& $git add -A
& $git -c user.name="MouseTranslate" -c user.email="noreply@localhost" commit -m "docs: point download links to $owner/$RepoName" *> $null
if ($LASTEXITCODE -ne 0) { Info "没有需要提交的改动" } else { Info "已提交" }

Write-Host "== 4/7 创建或复用远程仓库 =="
$exists = $false
$ErrorActionPreference = "Continue"
& $gh repo view "$owner/$RepoName" 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) { $exists = $true }
$ErrorActionPreference = "Stop"

if ($exists) {
    Info "仓库已存在，检查 remote"
    & $git remote get-url origin *> $null
    if ($LASTEXITCODE -ne 0) { & $git remote add origin "https://github.com/$owner/$RepoName.git" }
    & $git push -u origin main
} else {
    Info "创建公开仓库 $owner/$RepoName"
    & $gh repo create "$RepoName" --public --description $Description --source=. --remote=origin --push
    if ($LASTEXITCODE -ne 0) { Fail "创建仓库失败（可能重名或权限不足）" }
}

Write-Host "== 5/7 打标签 =="
& $git tag $Version *> $null
if ($LASTEXITCODE -ne 0) { Info "标签已存在，跳过" } else { & $git push origin $Version }

Write-Host "== 6/7 创建 Release 并上传附件 =="
$zip = Join-Path $root "release\MouseTranslate-v1.0.0-win64.zip"
$dict = Join-Path $root "app\assets\dict.bin"
$notes = Join-Path $root "RELEASE_NOTES.md"
$assets = @()
if (Test-Path $zip) { $assets += $zip } else { Info "缺少安装包 $zip（仍会创建 Release）" }
if (Test-Path $dict) { $assets += $dict }

$releaseArgs = @("release", "create", $Version)
if ($assets.Count -gt 0) { $releaseArgs += $assets }
$releaseArgs += @("--title", "$Version — 首个公开版本")
if (Test-Path $notes) { $releaseArgs += @("--notes-file", $notes) }
else { $releaseArgs += @("--notes", "首个公开版本：划词翻译 + 苹果玻璃质感 + 离线词库 + 自然语音朗读。") }
& $gh @releaseArgs
if ($LASTEXITCODE -ne 0) { Info "Release 可能已存在，尝试补传附件" }

Write-Host "== 6.5/7 校验 Release 附件 =="
if (Test-Path $zip) {
    $zipItem = Get-Item $zip
    $zipSha = (Get-FileHash $zip -Algorithm SHA256).Hash
    Info ("本地安装包：{0} 字节 / SHA-256 {1}" -f $zipItem.Length, $zipSha)
} else {
    Info "本地缺少安装包，无法校验附件大小"
}
$ErrorActionPreference = "Continue"
$assetsJson = (& $gh release view $Version --json assets 2>$null) -join "`n"
$viewOk = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = "Stop"
if ($viewOk -and $assetsJson) {
    $remoteAssets = @(($assetsJson | ConvertFrom-Json).assets)
    foreach ($a in $remoteAssets) { Info ("  附件：{0}  {1} 字节" -f $a.name, $a.size) }
    if (Test-Path $zip) {
        $zipName = Split-Path -Leaf $zip
        $remoteZip = $remoteAssets | Where-Object { $_.name -eq $zipName } | Select-Object -First 1
        if (-not $remoteZip) {
            Info ("[注意] Release 中没有 {0}，请手动上传" -f $zipName)
        } elseif ($remoteZip.size -ne (Get-Item $zip).Length) {
            Info ("[注意] 安装包大小不一致：本地 {0} 字节 / 远端 {1} 字节，请重新上传" -f (Get-Item $zip).Length, $remoteZip.size)
        } else {
            Info "安装包大小校验通过"
        }
    }
    if (Test-Path $dict) {
        $dictName = Split-Path -Leaf $dict
        if (-not ($remoteAssets | Where-Object { $_.name -eq $dictName })) { Info ("[注意] Release 中没有离线词库 {0}，请手动上传" -f $dictName) }
    }
} else {
    Info "无法读取 Release 附件信息，请到 Release 页面手动核对 zip 大小与 SHA-256"
}

Write-Host "== 7/7 完成 =="
$url = "https://github.com/$owner/$RepoName"
Info "仓库主页： $url"
Info "下载直链： $url/releases/latest"
Info "如果 README 顶部还有 OWNER 残留，请手动替换一次后 git push。"
