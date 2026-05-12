#Requires -Version 5.1
param(
    [string]$Package = "",
    [string]$Python = "",
    [string]$DatasetRoot = "",
    [string]$ModelRoot = "",
    [int]$Port = 0,
    [switch]$NoBrowser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ToolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ToolRoot "..\..\..")).Path

if ([string]::IsNullOrWhiteSpace($DatasetRoot)) {
    $DatasetRoot = Join-Path $RepoRoot "datasets\WenShu\DbText"
}
if ([string]::IsNullOrWhiteSpace($ModelRoot)) {
    $ModelRoot = Join-Path $RepoRoot "models\WenShu\DbText\Current"
}

New-Item -ItemType Directory -Force -Path $DatasetRoot, $ModelRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($Python)) {
    $VenvPython = Join-Path $ToolRoot ".venv\Scripts\python.exe"
    if (Test-Path $VenvPython) {
        $Python = $VenvPython
    } else {
        $Python = "python"
    }
}

$Server = Join-Path $ToolRoot "workbench\server.py"
$Arguments = @(
    $Server,
    "--tool-root", $ToolRoot,
    "--dataset-root", $DatasetRoot,
    "--model-root", $ModelRoot,
    "--port", $Port
)
if (-not [string]::IsNullOrWhiteSpace($Package)) {
    $Arguments += @("--package", $Package)
}
if ($NoBrowser) {
    $Arguments += "--no-open"
}

Write-Host "Starting AFR WenShu training workbench..." -ForegroundColor Cyan
Write-Host "Tool root: $ToolRoot" -ForegroundColor Cyan
Write-Host "Dataset root: $DatasetRoot" -ForegroundColor Cyan
Write-Host "Model root: $ModelRoot" -ForegroundColor Cyan
if (-not [string]::IsNullOrWhiteSpace($Package)) {
    Write-Host "Package: $Package" -ForegroundColor Cyan
}
& $Python @Arguments
exit $LASTEXITCODE
