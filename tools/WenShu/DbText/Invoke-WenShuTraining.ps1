#Requires -Version 5.1
param(
    [string]$Python = "python",
    [int]$SyntheticCount = 2000,
    [string]$ReviewedInput = "",
    [string]$FeaturesOutput = "",
    [string]$DatasetRoot = "",
    [string]$ModelRoot = "",
    [switch]$InstallDeps,
    [switch]$SkipTraining
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

$Reviewed = Join-Path $DatasetRoot "ReviewedLabels\synthetic_seed_reviewed.jsonl"
$Features = Join-Path $DatasetRoot "TrainingSets\dbtext_ai_features_v1_seed.csv"
$Models = $ModelRoot

New-Item -ItemType Directory -Force -Path (Join-Path $DatasetRoot "ExtractedCandidates") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DatasetRoot "ReviewedLabels") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DatasetRoot "TrainingSets") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DatasetRoot "Reports") | Out-Null
New-Item -ItemType Directory -Force -Path $Models | Out-Null

if ($InstallDeps) {
    & $Python -m pip install -r (Join-Path $ToolRoot "requirements.txt")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not [string]::IsNullOrWhiteSpace($ReviewedInput)) {
    $Reviewed = (Resolve-Path $ReviewedInput).Path
    $BaseName = [System.IO.Path]::GetFileNameWithoutExtension($Reviewed)
    if ([string]::IsNullOrWhiteSpace($FeaturesOutput)) {
        $Features = Join-Path $DatasetRoot ("TrainingSets\" + $BaseName + "_features.csv")
    }
} else {
    & $Python (Join-Path $ToolRoot "training\generate_seed_data.py") `
        --count $SyntheticCount `
        --output $Reviewed
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not [string]::IsNullOrWhiteSpace($FeaturesOutput)) {
    $Features = $FeaturesOutput
}

& $Python (Join-Path $ToolRoot "training\build_features.py") `
    --input $Reviewed `
    --output $Features
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $SkipTraining) {
    & $Python (Join-Path $ToolRoot "training\train_lightgbm.py") `
        --features $Features `
        --output-dir $Models
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "WenShu DBText training pipeline finished." -ForegroundColor Green
Write-Host "Reviewed labels: $Reviewed" -ForegroundColor Green
Write-Host "Feature table:   $Features" -ForegroundColor Green
if (-not $SkipTraining) {
    Write-Host "Model outputs:   $Models" -ForegroundColor Green
}
