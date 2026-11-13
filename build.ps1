# 一键打包：编译 Release → 生成便携版 zip → 生成安装包
# 用法（在项目根目录）：powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 1.2.3] [-NoInstaller]
# 版本号以 git 标签（vX.Y.Z）为准：不传 -Version 时取最近的标签。
# 正式版由 GitHub Actions 自动递增版本号、打标签并发布，平时不需要手动改版本号。
param(
    [string]$Version = "",
    [switch]$NoInstaller   # 只出便携版（没装 Inno Setup 时用）
)

$root = $PSScriptRoot
$appName = "声记"

if (-not $Version) {
    $Version = "0.0.0"
    try {
        $tag = (& git -C $root describe --tags --abbrev=0 --match "v[0-9]*" 2>$null)
        if ($LASTEXITCODE -eq 0 -and $tag) { $Version = "$tag".Trim().TrimStart('v') }
    } catch { }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号格式应为 X.Y.Z：$Version" }

$ErrorActionPreference = 'Stop'

# ---- 工具路径：优先用户目录安装，其次系统安装 / PATH ----
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$iscc = @($env:ISCC,
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

# ---- 1. 编译 ----
Write-Host "== 编译 Release $Version ==" -ForegroundColor Cyan
# CI 上开启确定性编译：exe 里内嵌的调试信息不带构建机的本地路径
$ci = if ($env:CI) { "-p:ContinuousIntegrationBuild=true" } else { "-p:ContinuousIntegrationBuild=false" }
& $dotnet build (Join-Path $root "src\ShengJi\ShengJi.csproj") -c Release --nologo $ci `
    "-p:Version=$Version" "-p:FileVersion=$Version.0" "-p:AssemblyVersion=$Version.0"
if ($LASTEXITCODE -ne 0) { throw "编译失败" }
$exe = Join-Path $root "src\ShengJi\bin\Release\ShengJi.exe"

# ---- 2. 整理输出 ----
$dist = Join-Path $root "dist"
$build = Join-Path $dist "build"
$portableDir = Join-Path $dist "portable\$appName"
foreach ($d in @($build, (Join-Path $dist "portable"))) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
}
New-Item -ItemType Directory -Path $build, $portableDir -Force | Out-Null

Copy-Item $exe (Join-Path $build "$appName.exe")
Copy-Item (Join-Path $root "docs\使用说明.txt") $build

# ---- 3. 便携版 ----
Write-Host "== 便携版 ==" -ForegroundColor Cyan
Copy-Item (Join-Path $build "$appName.exe") $portableDir
Copy-Item (Join-Path $root "docs\使用说明.txt") $portableDir
Copy-Item (Join-Path $root "docs\portable.txt") $portableDir
$zip = Join-Path $dist "${appName}_v${Version}_便携版.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# 手动写入条目，确保压缩包内路径使用标准的 “/” 分隔符，解压后是一个“声记”文件夹
$zs = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
$za = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create, $false, [System.Text.Encoding]::UTF8)
foreach ($f in Get-ChildItem $portableDir -File) {
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, "$appName/$($f.Name)",
        [System.IO.Compression.CompressionLevel]::Optimal)
}
$za.Dispose(); $zs.Dispose()
Write-Host "  $zip"

# ---- 4. 安装版 ----
if ($NoInstaller) {
    Write-Host "== 跳过安装版（-NoInstaller） ==" -ForegroundColor Yellow
} else {
    Write-Host "== 安装版 ==" -ForegroundColor Cyan
    if (-not $iscc) { throw "找不到 Inno Setup 6（ISCC.exe）。可安装 Inno Setup 6.5+，或用 -NoInstaller 只出便携版" }
    & $iscc /Q "/DAppVersion=$Version" (Join-Path $root "installer\ShengJi.iss")
    if ($LASTEXITCODE -ne 0) { throw "生成安装包失败" }
    Write-Host "  $(Join-Path $dist "${appName}_v${Version}_安装版.exe")"
}

# 清理中间目录，只保留最终产物
Remove-Item (Join-Path $dist "portable") -Recurse -Force
Write-Host "== 完成 ==" -ForegroundColor Green
Get-ChildItem $dist -File | Format-Table Name, @{n = 'Size(KB)'; e = { [int]($_.Length / 1KB) } } -AutoSize
