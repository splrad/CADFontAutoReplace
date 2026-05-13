# AFR GlyphCore DBText Training Workspace

This folder contains the public GlyphCore DBText training tools: browser workbench,
schema files, feature builders, LightGBM training scripts, and helper package.
Training assets under `AFR.GlyphCore/datasets` and model artifacts under
`AFR.GlyphCore/models` are developer-private local files and must not be
committed or uploaded to GitHub.

Raw DWG files remain local-only under `AFR.GlyphCore/raw-dwg`.

## Layout

```text
AFR.GlyphCore/tools/
  Start-GlyphCoreWorkbench.ps1
  Invoke-GlyphCoreTraining.ps1
  workbench/server.py
  workbench/frontend/
  training/*.py
  afr_glyphcore/
  schemas/

AFR.GlyphCore/datasets/
  ExtractedCandidates/    # local-only
  ReviewedLabels/         # local-only
  TrainingSets/           # local-only
  Reports/                # local-only

AFR.GlyphCore/models/
  AFR.GlyphCore.Model.onnx             # local-only
  AFR.GlyphCore.ModelManifest.json     # local-only
  AFR.GlyphCore.Model.txt              # local-only
  *_validation_report.json            # local-only
```

## Browser Workbench

Open the local workbench:

```powershell
.\AFR.GlyphCore\tools\Start-GlyphCoreWorkbench.ps1
```

Open a specific extracted package:

```powershell
.\AFR.GlyphCore\tools\Start-GlyphCoreWorkbench.ps1 `
    -Package ".\AFR.GlyphCore\datasets\ExtractedCandidates\<package>"
```

The workbench reads packages from `AFR.GlyphCore/datasets/ExtractedCandidates`,
writes human-reviewed labels to `ReviewedLabels`, generates feature CSV files under
`TrainingSets`, and trains the current model into
`AFR.GlyphCore/models`. The browser UI is built from the React/Vite app
under `workbench/frontend`; the Python server continues to be the local API and
static-file host.

## Manual table review workflow

The browser workbench now uses a pure human table workflow:

1. Select a package.
2. Open `人工复核`.
3. Filter rows by `未审核`, `已审核`, or `全部`.
4. Review the original text and rule candidate in the table.
5. Edit the final action/text inline when needed.
6. Check the rows to save and click `保存已选`.
7. Generate features only after records have been written to `ReviewedLabels`.

The table has only two review states: `未审核` and `已审核`. Already reviewed
rows remain editable; saving them again overwrites the corresponding reviewed
JSONL records.

For 10k+ text packages, the default review workflow is text-cluster
propagation. The workbench groups records by current text, recommended
candidate text, candidate source, and recommended action. Layer, text style,
font/bigfont, owner block, xref state, conflict flags, and high-risk flags stay
server-side metadata. The reviewer works from the table's original text and
final text columns; the server expands each saved table row to the matching
DBText entities in that cluster.

Propagation still writes one reviewed JSONL record per DBText entity, so feature
generation and training stay compatible. Propagation audit fields (`batchId`,
`batchRule`, `clusterReviewedSampleIds`, `appliedByCluster`,
`propagationClusterId`, `propagationSignature`, `clusterRiskSummary`,
`clusterContextSummary`, `sourceRepresentativeHandle`, `propagationScope`, and
`propagationRule`) are metadata only and do not write back to the DWG. Legacy
`propagationGroupId` metadata remains for compatibility with older reviewed
files.

Rebuild the browser assets after changing the React source:

```powershell
cd .\AFR.GlyphCore\tools\workbench\frontend
npm install --cache .npm-cache
npm run build
```

## Command-Line Training

Create a local virtual environment when dependencies are missing:

```powershell
$py = "C:\Path\To\python.exe"
& $py -m venv .\AFR.GlyphCore\tools\.venv
.\AFR.GlyphCore\tools\.venv\Scripts\python.exe -m pip install -r .\AFR.GlyphCore\tools\requirements.txt
```

Run the standard pipeline:

```powershell
.\AFR.GlyphCore\tools\Invoke-GlyphCoreTraining.ps1 `
    -Python .\AFR.GlyphCore\tools\.venv\Scripts\python.exe `
    -ReviewedInput ".\AFR.GlyphCore\datasets\ReviewedLabels\<exportId>_reviewed.jsonl"
```

For smoke testing, omit `-ReviewedInput` to generate synthetic seed labels:

```powershell
.\AFR.GlyphCore\tools\Invoke-GlyphCoreTraining.ps1 `
    -Python .\AFR.GlyphCore\tools\.venv\Scripts\python.exe `
    -SyntheticCount 2000 `
    -SkipTraining
```

## DWG Export Flow

1. Put user-provided raw DWG files under `AFR.GlyphCore/raw-dwg`.
2. Open a DWG in an AutoCAD Debug build with AFR loaded.
3. Run `AFRGLYPHCOREEXPORT`.
4. Review and train from the browser workbench.

Release builds do not expose `AFRGLYPHCOREEXPORT` or the training workbench.
Release model embedding should point at a private local model directory such as
`AFR.GlyphCore/models`; the model files are not part of the GitHub repo.
