# AFR GlyphCore Workbench Design Notes

This document describes the production `AFR.GlyphCore/tools/workbench/frontend`
interface. It is not an external brand clone or a marketing page. The workbench
is a dense local tool for DBText labeling, training, and model validation.

## Layout

- The outer page is fixed to the viewport: no document-level scrolling.
- The top navigation is fixed and exposes exactly four tabs:
  `数据标注`, `训练数据集`, `模型训练`, `模型报告`.
- The bottom status bar stays visible and reports current package/model/task
  state.
- Large content areas scroll internally: tables, logs, report cards, and
  sidebars own their own overflow.
- Do not reintroduce the old preview window in `数据标注`; users judge labels
  from the table context, source selector, coordinates, package context, and
  reviewed text.

## Visual Style

- Use a calm white/gray workspace with high-density rows, restrained borders,
  compact controls, and strong contrast for destructive actions.
- Primary actions use blue; warnings and mismatches use orange/red.
- Mismatch records in model reports are sorted to the top and highlighted in red.
- Selected rows must keep icons legible. Never put delete/destructive icons on a
  same-color selected background without a contrasting foreground or surface.
- Cards are used only for repeated records, summaries, or framed tools. Do not
  nest cards inside cards.

## Data Annotation

- The `来源` selector uses three segmented choices: `原文`, `候选`, `手动`.
- Switching source must update both the chosen candidate text and the encoding
  path column.
- The encoding path column supports filtering.
- The correct text cell shows exactly what will be written into reviewed labels.
- The table preserves CAD current text. Do not normalize drawing-specific alias
  pairs such as `井` and `#`.

## Training Dataset

- The page lists records promoted into the training dataset and supports search,
  filtering, batch delete, import, and export.
- Deleting records must update the local dataset files and return deleted review
  groups to the pending review queue when applicable.
- CSV export is for human review only. Re-importable data must keep full JSONL
  training records or reviewed-style JSONL that can be matched to current
  package group IDs.

## Model Training

- Training starts from explicitly selected packages.
- Logs poll while training is running and keep start time, finish time, cancel
  state, and short result summaries visible.
- Cancel uses the backend train-cancel path instead of only hiding UI state.

## Model Report

- Full simulation should evaluate all available records, not a silent sample.
- Report metrics, simulation logs, status cards, and mismatch rows must agree on
  the same run result.
- Simulation should use coordinates and nearby context available in the dataset
  so the offline check approximates real CAD repair behavior.
- Resetting the model archives existing model/report artifacts before returning
  a refreshed empty state.
