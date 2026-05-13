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
  Start-GlyphCoreWorkbench.cmd
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

From Windows Explorer, double-click or right-click-run the command wrapper:

```cmd
AFR.GlyphCore\tools\Start-GlyphCoreWorkbench.cmd
```

The `.cmd` wrapper starts PowerShell with a process-scoped execution-policy
bypass, so it works on machines where direct right-click execution of `.ps1`
files is blocked by policy. It does not change the user or machine execution
policy.

From an existing PowerShell session, you can still run the script directly:

```powershell
.\AFR.GlyphCore\tools\Start-GlyphCoreWorkbench.ps1
```

Open a specific extracted package:

```cmd
AFR.GlyphCore\tools\Start-GlyphCoreWorkbench.cmd -Package ".\AFR.GlyphCore\datasets\ExtractedCandidates\<package>"
```

or from PowerShell:

```powershell
.\AFR.GlyphCore\tools\Start-GlyphCoreWorkbench.ps1 `
    -Package ".\AFR.GlyphCore\datasets\ExtractedCandidates\<package>"
```

The workbench reads packages from `AFR.GlyphCore/datasets/ExtractedCandidates`,
writes human-reviewed labels to `ReviewedLabels`, generates feature CSV files
under `TrainingSets`, and trains the current model into `AFR.GlyphCore/models`.
The browser UI is the React/Vite app under `workbench/frontend`; the Python
server is the local API and static-file host. The server no longer falls back to
the old embedded HTML workbench, so `dist/index.html` must exist before opening
the browser.

`Start-GlyphCoreWorkbench.ps1` checks the frontend automatically:

1. If `node_modules` is missing, it runs `npm install --cache .npm-cache`.
2. If `src`, `index.html`, `package.json`, `package-lock.json`, `vite.config.ts`,
   or `tsconfig.json` is newer than `dist/index.html`, it runs `npm run build`.
3. It then starts `workbench/server.py` and opens the local browser URL.

Pass `-NoInstallDeps` only when Python and frontend dependencies are already
installed. With that switch, missing `node_modules` is treated as a startup
error instead of installing packages.

## Manual table review workflow

The browser workbench uses shadcn/ui code components on top of the light
GlyphCore visual system: white canvas, black ink, pastel workflow blocks, pill
actions, low-contrast hairlines, and minimal shadows. It is organized as:

- `数据包`: package manager for exported DWG packages and reviewed/training counts.
- `人工复核`: left filters, central virtualized text-cluster table, and right
  batch confirmation panel.
- `训练数据集`: searchable/sortable TanStack table of records already promoted
  into the training dataset.
- `特征生成`, `模型训练`, `模型报告`: local training pipeline status, logs,
  validation metrics, model paths, and release command.

The review flow is:

1. Select a package.
2. Open `人工复核`.
3. Filter by text, state, risk, layer, font, or encoding path.
4. Review the original text and candidate text in the central table.
5. Choose `原文`, `候选`, or `手动`, then adjust the final text if needed.
6. Check rows and click `保存已选`, or apply the current table edits to all
   visible rows.
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

The frontend entrypoints are:

```text
workbench/frontend/src/types/api.ts       # shared API payload types
workbench/frontend/src/api/workbench.ts   # typed API client
workbench/frontend/components.json        # shadcn/ui configuration
workbench/frontend/src/components/ui/     # shadcn/ui components and AFR wrappers
workbench/frontend/src/store/             # Zustand state and actions
```

Add new UI primitives through shadcn-style files under
`workbench/frontend/src/components/ui/`. Keep component filenames lowercase
(`button.tsx`, `card.tsx`, `table.tsx`, etc.) because Windows treats
case-only names as the same path.

Validation after UI or API changes:

```powershell
cd .\AFR.GlyphCore\tools\workbench\frontend
npm run build

cd ..\
..\.venv\Scripts\python.exe -m unittest test_review_clusters.py
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

Training uses automatic early stopping by default. Advanced runs can cap the
maximum rounds and early-stopping patience:

```powershell
.\AFR.GlyphCore\tools\Invoke-GlyphCoreTraining.ps1 `
    -Python .\AFR.GlyphCore\tools\.venv\Scripts\python.exe `
    -ReviewedInput ".\AFR.GlyphCore\datasets\ReviewedLabels\<exportId>_reviewed.jsonl" `
    -MaxRounds 650 `
    -EarlyStoppingRounds 60 `
    -Seed 20260512
```

The trainer splits data by stable text pattern into train, validation, and blind
test sets. The blind test set is not used for training or early stopping; it is
used only after training to compare AI decisions with human labels. Model reports
include the conservative acceptance result, overfitting status, blind-test
metrics, and error samples.

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
