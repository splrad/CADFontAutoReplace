# AFR WenShu DBText Training Workspace

This folder contains the public WenShu DBText training tools: browser workbench,
schema files, feature builders, LightGBM training scripts, and helper package.
Training assets are now tracked under `datasets/WenShu/DbText` and current model
artifacts are tracked under `models/WenShu/DbText/Current`.

Raw DWG files remain local-only under `data/WenShu/DbText/RawDwg`.

## Layout

```text
tools/WenShu/DbText/
  Start-WenShuWorkbench.ps1
  Invoke-WenShuTraining.ps1
  workbench/server.py
  training/*.py
  wenshu_dbtext/
  schemas/

datasets/WenShu/DbText/
  ExtractedCandidates/
  ReviewedLabels/
  TrainingSets/
  Reports/

models/WenShu/DbText/Current/
  AFR.DBTextAI.Model.onnx
  AFR.DBTextAI.ModelManifest.json
  AFR.DBTextAI.Model.txt
  *_validation_report.json
```

## Browser Workbench

Open the local workbench:

```powershell
.\tools\WenShu\DbText\Start-WenShuWorkbench.ps1
```

Open a specific extracted package:

```powershell
.\tools\WenShu\DbText\Start-WenShuWorkbench.ps1 `
    -Package ".\datasets\WenShu\DbText\ExtractedCandidates\<package>"
```

The workbench reads packages from `datasets/WenShu/DbText/ExtractedCandidates`,
writes reviewed labels to `ReviewedLabels`, generates feature CSV files under
`TrainingSets`, and trains the current model into `models/WenShu/DbText/Current`.

## Command-Line Training

Create a local virtual environment when dependencies are missing:

```powershell
$py = "C:\Path\To\python.exe"
& $py -m venv .\tools\WenShu\DbText\.venv
.\tools\WenShu\DbText\.venv\Scripts\python.exe -m pip install -r .\tools\WenShu\DbText\requirements.txt
```

Run the standard pipeline:

```powershell
.\tools\WenShu\DbText\Invoke-WenShuTraining.ps1 `
    -Python .\tools\WenShu\DbText\.venv\Scripts\python.exe `
    -ReviewedInput ".\datasets\WenShu\DbText\ReviewedLabels\<exportId>_reviewed.jsonl"
```

For smoke testing, omit `-ReviewedInput` to generate synthetic seed labels:

```powershell
.\tools\WenShu\DbText\Invoke-WenShuTraining.ps1 `
    -Python .\tools\WenShu\DbText\.venv\Scripts\python.exe `
    -SyntheticCount 2000 `
    -SkipTraining
```

## DWG Export Flow

1. Put user-provided raw DWG files under `data/WenShu/DbText/RawDwg`.
2. Open a DWG in an AutoCAD Debug build with AFR loaded.
3. Run `AFRDBTEXTEXPORTAI`.
4. Review and train from the browser workbench.

Release builds do not expose `AFRDBTEXTEXPORTAI` or the training workbench.
Release model embedding defaults to `models/WenShu/DbText/Current`.
