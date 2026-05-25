#Requires -Version 5.1
<#
.SYNOPSIS
    自动构建所有 AutoCAD 插件 DLL，并生成 GitHub Release 发布资产。
.DESCRIPTION
    执行步骤：
      1. 以 Release 配置构建所有 AFR-ACAD20XX 项目
         - DLL 与 CAD 元数据 JSON 均保留在标准构建输出目录
           artifacts\bin\AFR-ACAD20XX\release\
      2. 校验每个插件的 Release DLL 与 CAD 元数据 JSON
      3. dotnet publish AFR.Deployer -> 自包含 .NET 10 单文件 EXE
         （已内置 .NET Desktop Runtime 所需组件；无需 Windows App Runtime 等外置依赖）
      4. 生成版本化发布资产到 artifacts\ReleaseAssets\
         - AFR-Deployer_vX.Y.Z.exe：部署器 EXE 本体
         - AFR-DLL_vX.Y.Z.zip：仅 AFR-ACAD*.dll 插件主 DLL
         - Fonts.zip：字体资源包
.OUTPUTS
    <RepoRoot>\publish\AFR.Deployer\AFR.Deployer.exe
    <RepoRoot>\artifacts\ReleaseAssets\AFR-Deployer_vX.Y.Z.exe
    <RepoRoot>\artifacts\ReleaseAssets\AFR-DLL_vX.Y.Z.zip
    <RepoRoot>\artifacts\ReleaseAssets\Fonts.zip
.EXAMPLE
    .\tools\Publish-ReleaseAssets.ps1
    .\tools\Publish-ReleaseAssets.ps1 -SkipPluginBuild   # 跳过插件构建，仅重新生成发布资产
#>
param(
    [switch]$SkipPluginBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 强制控制台按 UTF-8 处理外部进程输出。
# dotnet CLI 默认输出 UTF-8，而中文 Windows 上的 Windows PowerShell 5.1
# 常按系统代码页（通常为 GBK / CP936）解释文本，导致中文日志乱码。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8

# ── 路径常量 ──────────────────────────────────────────────────────────────
$RepoRoot       = (Resolve-Path "$PSScriptRoot\..").Path
$PluginsRoot    = Join-Path $RepoRoot "src\AutoCAD"
$PluginOutputRoot = Join-Path $RepoRoot "artifacts\bin"       # 标准构建输出根目录
$ReleaseAssetsDir = Join-Path $RepoRoot "artifacts\ReleaseAssets" # GitHub Release 上传资产目录
$ArchiveTempDir = Join-Path $RepoRoot "artifacts\release-archive-temp"
$DeployerCsproj = Join-Path $RepoRoot "src\AFR.Deployer\AFR.Deployer.csproj"
$PublishOutput  = Join-Path $RepoRoot "publish\AFR.Deployer"
$VersionProps    = Join-Path $RepoRoot "Version.props"
$FontsSourcePath = Join-Path $RepoRoot "chore\Fonts.zip"

# 自动发现 src\AutoCAD\AFR-ACAD*\*.csproj，避免新增 CAD 版本时手工维护列表。
# TFM 仅用于控制台日志展示；解析失败时回落为 "(unknown)"，不阻断发布流程。
function Get-PluginTfm([string]$csprojPath) {
    try {
        [xml]$xml = Get-Content -LiteralPath $csprojPath -Raw
        $tfm = $xml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($tfm)) {
            $tfm = $xml.Project.PropertyGroup.TargetFrameworks | Where-Object { $_ } | Select-Object -First 1
        }
        if ([string]::IsNullOrWhiteSpace($tfm)) { return '(unknown)' }
        return $tfm
    } catch {
        return '(unknown)'
    }
}

$Plugins = @(
    Get-ChildItem -Path $PluginsRoot -Directory -Filter 'AFR-ACAD*' |
        Sort-Object Name |
        ForEach-Object {
            $csproj = Join-Path $_.FullName "$($_.Name).csproj"
            if (Test-Path $csproj) {
                [pscustomobject]@{
                    Name = $_.Name
                    TFM  = Get-PluginTfm $csproj
                }
            }
        }
)

if ($Plugins.Count -eq 0) {
    Write-Host "未在 $PluginsRoot 下发现任何 AFR-ACAD*\*.csproj，终止发布。" -ForegroundColor Red
    exit 1
}

Write-Host "自动发现 $($Plugins.Count) 个插件项目：" -ForegroundColor DarkGray
$Plugins | ForEach-Object { Write-Host "    • $($_.Name) ($($_.TFM))" -ForegroundColor DarkGray }

# ── 工具函数 ──────────────────────────────────────────────────────────────
function Write-Step([string]$msg) { Write-Host "`n── $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "  ✓ $msg" -ForegroundColor Green }
function Write-Fail([string]$msg) { Write-Host "  ✗ $msg" -ForegroundColor Red }
function Get-PluginReleaseFile([pscustomobject]$plugin, [string]$extension) {
    return Join-Path $PluginOutputRoot "$($plugin.Name)\release\$($plugin.Name)$extension"
}

function Get-ReleaseVersion {
    try {
        [xml]$xml = Get-Content -LiteralPath $VersionProps -Raw
        $version = $xml.Project.PropertyGroup.PluginDisplayVersion | Where-Object { $_ } | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($version)) {
            throw "PluginDisplayVersion 为空"
        }
        return $version.Trim()
    } catch {
        Write-Fail "无法从 Version.props 读取 PluginDisplayVersion: $($_.Exception.Message)"
        exit 1
    }
}

$ReleaseVersion = Get-ReleaseVersion
$ReleaseTag = "v$ReleaseVersion"

# ── Step 1：构建所有插件 DLL ──────────────────────────────────────────────
# 仅在未指定 -SkipPluginBuild 时执行。
# 成功后，DLL/JSON 留在 artifacts\bin\AFR-ACAD20XX\release\ 标准输出目录。
if (-not $SkipPluginBuild) {
    Write-Step "构建所有 AutoCAD 插件 DLL (Release)"

    $buildErrors = @()
    foreach ($p in $Plugins) {
        $csproj = Join-Path $PluginsRoot "$($p.Name)\$($p.Name).csproj"
        Write-Host "  → 构建 $($p.Name) ($($p.TFM))..." -NoNewline

        # 构建单个版本壳项目；构建成功后会在标准输出目录生成 DLL 与 sidecar JSON。
        $output = dotnet build $csproj -c Release --nologo -v quiet 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "失败"
            $buildErrors += $p.Name
            Write-Host ($output | Out-String) -ForegroundColor DarkRed
        } else {
            Write-Ok "完成"
        }
    }

    if ($buildErrors.Count -gt 0) {
        Write-Host "`n以下项目构建失败，终止发布：" -ForegroundColor Red
        $buildErrors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        exit 1
    }
}

# ── Step 2：校验标准构建输出 ─────────────────────────────────────────────
# 部署器项目会直接从 artifacts\bin\AFR-ACAD20XX\release\ 嵌入这些文件。
Write-Step "校验插件 Release 输出"

$missingInputs = @()
foreach ($p in $Plugins) {
    $srcDll = Get-PluginReleaseFile $p ".dll"
    $srcJson = Get-PluginReleaseFile $p ".cad.json"

    if (Test-Path -LiteralPath $srcDll) {
        Write-Ok "$($p.Name).dll"
    } else {
        Write-Fail "$srcDll 不存在"
        $missingInputs += $srcDll
    }

    if (Test-Path -LiteralPath $srcJson) {
        Write-Ok "$($p.Name).cad.json"
    } else {
        Write-Fail "$srcJson 不存在（请检查 csproj 中的 CadBrand/CadVersion/CadRegistryBasePath 属性）"
        $missingInputs += $srcJson
    }
}

if ($missingInputs.Count -gt 0) {
    Write-Host "`n以下发布输入缺失，终止发布：" -ForegroundColor Red
    $missingInputs | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

# ── Step 3：发布 AFR.Deployer 单文件 EXE ─────────────────────────────────
# 此处只发布部署器本体；插件 DLL/JSON 由项目文件直接从标准输出目录嵌入。
Write-Step "发布 AFR.Deployer (自包含单文件)"
New-Item -ItemType Directory -Force -Path $PublishOutput | Out-Null

dotnet publish $DeployerCsproj -c Release -o $PublishOutput --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Fail "发布失败"
    exit 1
}

$exePath = Join-Path $PublishOutput "AFR-Deployer.exe"
$sizeMB  = [math]::Round((Get-Item $exePath).Length / 1MB, 1)

# ── Step 4：生成最终发布资产 ────────────────────────────────────────────
Write-Step "生成发布资产到 artifacts\ReleaseAssets\"
New-Item -ItemType Directory -Force -Path $ReleaseAssetsDir | Out-Null

$versionedExe = Join-Path $ReleaseAssetsDir "AFR-Deployer_$ReleaseTag.exe"
Copy-Item -LiteralPath $exePath -Destination $versionedExe -Force
Write-Ok "部署器 EXE → $versionedExe"

$versionedDllZip = Join-Path $ReleaseAssetsDir "AFR-DLL_$ReleaseTag.zip"
$releaseFontsZip = Join-Path $ReleaseAssetsDir "Fonts.zip"
if (Test-Path -LiteralPath $ArchiveTempDir) {
    Remove-Item -LiteralPath $ArchiveTempDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $ArchiveTempDir | Out-Null

try {
    foreach ($p in $Plugins) {
        $srcDll = Get-PluginReleaseFile $p ".dll"
        if (-not (Test-Path -LiteralPath $srcDll)) {
            Write-Fail "$srcDll 不存在，无法生成 DLL 压缩包"
            exit 1
        }

        Copy-Item -LiteralPath $srcDll -Destination (Join-Path $ArchiveTempDir "$($p.Name).dll") -Force
    }

    if (Test-Path -LiteralPath $versionedDllZip) {
        Remove-Item -LiteralPath $versionedDllZip -Force
    }

    Compress-Archive -Path (Join-Path $ArchiveTempDir "*.dll") -DestinationPath $versionedDllZip -Force
    Write-Ok "插件 DLL ZIP → $versionedDllZip"
} finally {
    if (Test-Path -LiteralPath $ArchiveTempDir) {
        Remove-Item -LiteralPath $ArchiveTempDir -Recurse -Force
    }
}

if (-not (Test-Path -LiteralPath $FontsSourcePath)) {
    Write-Fail "$FontsSourcePath 不存在，无法生成字体包发布资产"
    exit 1
}

Copy-Item -LiteralPath $FontsSourcePath -Destination $releaseFontsZip -Force
Write-Ok "字体包 → $releaseFontsZip"

Write-Host "`n✓ 发布完成！" -ForegroundColor Green
Write-Host "  输出：$exePath ($sizeMB MB)" -ForegroundColor Green
Write-Host "  发布资产：$versionedExe" -ForegroundColor Green
Write-Host "  发布资产：$versionedDllZip" -ForegroundColor Green
Write-Host "  发布资产：$releaseFontsZip" -ForegroundColor Green
