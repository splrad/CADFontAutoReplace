#Requires -Version 5.1
<#
.SYNOPSIS
    自动构建所有 AutoCAD 插件 DLL，汇总到部署器资源目录，并发布 AFR.Deployer 单文件 EXE。
.DESCRIPTION
    执行步骤：
      1. 以 Release 配置构建所有 AFR-ACAD20XX 项目
         - Directory.Build.props 的 CopyDllToReleases Target 自动将 DLL 汇聚到
           artifacts\Releases\
      2. 从 artifacts\Releases\ 复制插件 DLL 与 CAD 元数据 JSON 到
         AFR.Deployer\Resources\
      3. dotnet publish AFR.Deployer -> 自包含 .NET 10 单文件 EXE
         （已内置 .NET 10 运行时；用户仅需额外安装 Windows App Runtime 1.8 (x64)，
           缺失时由 AFR.Deployer 启动期检测并弹原生对话框给出下载链接）
      4. 额外归档发布文件到仓库根目录 Releases\
         - AFR-Deployer_vX.Y.exe：仅部署器 EXE 本体
         - AFR-DLL_vX.Y.zip：仅 AFR-ACAD*.dll 插件主 DLL
.OUTPUTS
    <RepoRoot>\publish\AFR.Deployer\AFR.Deployer.exe
    <RepoRoot>\Releases\AFR-Deployer_vX.Y.exe
    <RepoRoot>\Releases\AFR-DLL_vX.Y.zip
.EXAMPLE
    .\Publish-Deployer.ps1
    .\Publish-Deployer.ps1 -SkipPluginBuild   # 跳过插件构建，仅重新发布 EXE
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
$RepoRoot       = (Resolve-Path "$PSScriptRoot\..\..").Path
$PluginsRoot    = Join-Path $RepoRoot "src\AutoCAD"
$ReleasesDir    = Join-Path $RepoRoot "artifacts\Releases"   # 插件构建产物的统一汇聚目录
$FinalReleasesDir = Join-Path $RepoRoot "Releases"            # 最终对外归档目录，仅放版本化发布产物
$ArchiveTempDir = Join-Path $RepoRoot "artifacts\ReleaseArchiveTemp"
$ResourcesDir   = Join-Path $PSScriptRoot "Resources"         # 部署器打包时嵌入的资源目录
$DeployerCsproj = Join-Path $PSScriptRoot "AFR.Deployer.csproj"
$PublishOutput  = Join-Path $RepoRoot "publish\AFR.Deployer"
$VersionProps    = Join-Path $RepoRoot "Version.props"

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
# 成功后，Directory.Build.props 中的 CopyDllToReleases 目标会把 DLL/JSON
# 自动汇聚到 artifacts\Releases\，供后续统一复制。
if (-not $SkipPluginBuild) {
    Write-Step "构建所有 AutoCAD 插件 DLL (Release)"

    $buildErrors = @()
    foreach ($p in $Plugins) {
        $csproj = Join-Path $PluginsRoot "$($p.Name)\$($p.Name).csproj"
        Write-Host "  → 构建 $($p.Name) ($($p.TFM))..." -NoNewline

        # 构建单个版本壳项目；构建成功后会自动触发产物汇聚。
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

# ── Step 2：从 artifacts\Releases\ 复制产物到 Resources\ ────────────────
# Resources\ 会被部署器项目作为嵌入资源打包，因此这里复制的是“发布输入”而非临时缓存。
Write-Step "复制 DLL 到 Resources\"
New-Item -ItemType Directory -Force -Path $ResourcesDir | Out-Null

$copyErrors = @()
foreach ($p in $Plugins) {
    $srcDll = Join-Path $ReleasesDir "$($p.Name).dll"
    $dstDll = Join-Path $ResourcesDir "$($p.Name).dll"

    if (Test-Path $srcDll) {
        Copy-Item -Path $srcDll -Destination $dstDll -Force
        Write-Ok "$($p.Name).dll → Resources\"
    } else {
        Write-Fail "$($p.Name).dll 未在 artifacts\Releases\ 中找到"
        $copyErrors += $p.Name
    }

    # 复制 CAD 元数据 sidecar。
    # 该 JSON 由 EmitCadDescriptorJson 目标生成，供部署器运行期识别品牌、版本与注册表路径。
    $srcJson = Join-Path $ReleasesDir "$($p.Name).cad.json"
    $dstJson = Join-Path $ResourcesDir "$($p.Name).cad.json"
    if (Test-Path $srcJson) {
        Copy-Item -Path $srcJson -Destination $dstJson -Force
        Write-Ok "$($p.Name).cad.json → Resources\"
    } else {
        Write-Fail "$($p.Name).cad.json 未生成（请检查 csproj 中的 CadBrand/CadVersion/CadRegistryBasePath 属性）"
        $copyErrors += "$($p.Name).cad.json"
    }
}

if ($copyErrors.Count -gt 0) {
    Write-Host "`n以下 DLL 缺失，终止发布：" -ForegroundColor Red
    $copyErrors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

# ── Step 3：发布 AFR.Deployer 单文件 EXE ─────────────────────────────────
# 此处只发布部署器本体；插件 DLL 已在上一步复制到 Resources\ 并随部署器一同打包。
Write-Step "发布 AFR.Deployer (自包含单文件)"
New-Item -ItemType Directory -Force -Path $PublishOutput | Out-Null

dotnet publish $DeployerCsproj -c Release -o $PublishOutput --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Fail "发布失败"
    exit 1
}

$exePath = Join-Path $PublishOutput "AFR-Deployer.exe"
$sizeMB  = [math]::Round((Get-Item $exePath).Length / 1MB, 1)

# ── Step 4：归档最终发布产物到 Releases\ ────────────────────────────────
Write-Step "归档发布产物到 Releases\"
New-Item -ItemType Directory -Force -Path $FinalReleasesDir | Out-Null

$versionedExe = Join-Path $FinalReleasesDir "AFR-Deployer_$ReleaseTag.exe"
Copy-Item -LiteralPath $exePath -Destination $versionedExe -Force
Write-Ok "部署器 EXE → $versionedExe"

$versionedDllZip = Join-Path $FinalReleasesDir "AFR-DLL_$ReleaseTag.zip"
if (Test-Path -LiteralPath $ArchiveTempDir) {
    Remove-Item -LiteralPath $ArchiveTempDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $ArchiveTempDir | Out-Null

try {
    foreach ($p in $Plugins) {
        $srcDll = Join-Path $ReleasesDir "$($p.Name).dll"
        if (-not (Test-Path -LiteralPath $srcDll)) {
            Write-Fail "$($p.Name).dll 未在 artifacts\Releases\ 中找到，无法生成 DLL 压缩包"
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

Write-Host "`n✓ 发布完成！" -ForegroundColor Green
Write-Host "  输出：$exePath ($sizeMB MB)" -ForegroundColor Green
Write-Host "  归档：$versionedExe" -ForegroundColor Green
Write-Host "  归档：$versionedDllZip" -ForegroundColor Green
