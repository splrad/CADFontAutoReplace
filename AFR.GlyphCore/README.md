# AFR.GlyphCore / 文枢

`AFR.GlyphCore` is the local DBText AI workspace for CADFontAutoReplace. It
contains public tooling code, schemas, and the React/Vite workbench source. User
DWG files, extracted datasets, reviewed labels, training sets, reports, and
model artifacts remain local-only and must not be committed.

## Current Scope

- Runtime repair lives in `src/AFR.Core/GlyphCore/TextRepair` and
  `src/AutoCAD/AFR.AutoCAD/Services/GlyphCore/TextRepair`.
- Developer tools live under `AFR.GlyphCore/tools`.
- Local datasets live under `AFR.GlyphCore/datasets`.
- Local models live under `AFR.GlyphCore/models`.
- Raw user DWG files should stay under `AFR.GlyphCore/raw-dwg` when needed for
  private reproduction.

## Repair Policy

DBText repair is gated by native DBCS/code page Hook evidence, or by equivalent
evidence derived from already repaired strong-evidence seeds. Text appearance,
font-missing records, candidate conversion success, or training-set matches must
not start the AI path by themselves.

`LdFileHook` belongs to the font-load/font-redirection chain. DBText native
evidence is produced by the dedicated DBText Hook chain and is consumed by
`GlyphCoreNativeDecodeEvidenceStore`; the hooks only register evidence and do
not directly change text or native code page values.

## Data Identity

The workbench and training pipeline must preserve the exact text currently
visible to CAD APIs. Do not normalize drawing-specific aliases such as `井` and
`#` in export, display, feature generation, or simulation. A label like `FL-井1`
can be correct in one drawing while `FL-#1` is correct in another, so the model
must learn from reviewed data and drawing context rather than hard-coded
substitution rules.

## Entry Documents

- [GlyphCore tools README](tools/README.md)
- [DBText runtime repair guide](../docs/debugging/DBText-Repair-Model.md)
- [Repository memory](../.github/copilot-instructions.md)
