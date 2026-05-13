import type { ColumnDef } from '@tanstack/react-table';
import { RotateCcw, Search, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Button } from '@/components/ui/button';
import { DataTable } from '@/components/ui/DataTable';
import { NativeSelect, TextInput } from '@/components/ui/Controls';
import { Panel, PanelHeader, Metric } from '@/components/ui/Panel';
import { Pill } from '@/components/ui/Pill';
import {
  actionLabel,
  actionTone,
  compactText,
  formatDateTime,
  optionValues
} from '@/lib/utils';
import type { TrainingDatasetPayload, TrainingDatasetRecord } from '@/types/api';

interface TrainingDatasetPageProps {
  dataset: TrainingDatasetPayload;
  busy: boolean;
  onDeleteRecord: (record: TrainingDatasetRecord) => void;
}

export function TrainingDatasetPage({ dataset, busy, onDeleteRecord }: TrainingDatasetPageProps) {
  const records = dataset?.records || [];
  const [query, setQuery] = useState('');
  const [actionFilter, setActionFilter] = useState('all');
  const [layerFilter, setLayerFilter] = useState('all');
  const [fontFilter, setFontFilter] = useState('all');
  const [sortMode, setSortMode] = useState('entered-desc');

  const filters = useMemo(
    () => ({
      actions: optionValues(records, (record) => record.labelAction),
      layers: optionValues(records, (record) => record.layer),
      fonts: optionValues(records, (record) => record.font || record.textStyleName)
    }),
    [records]
  );

  const visibleRecords = useMemo(() => {
    const q = query.trim().toLowerCase();
    const matched = records.filter((record) => {
      const searchable = [
        record.currentText,
        record.labelText,
        record.candidateText,
        record.drawingFileName,
        record.drawingPath,
        record.handle,
        record.layer,
        record.ownerBlockName,
        record.textStyleName,
        record.font,
        record.bigFont,
        record.trainingSource
      ].join(' ').toLowerCase();
      if (q && !searchable.includes(q)) return false;
      if (actionFilter !== 'all' && record.labelAction !== actionFilter) return false;
      if (layerFilter !== 'all' && record.layer !== layerFilter) return false;
      if (fontFilter !== 'all' && (record.font || record.textStyleName) !== fontFilter) return false;
      return true;
    });
    return [...matched].sort((a, b) => {
      if (sortMode === 'entered-asc') {
        return String(a.enteredTrainingUtc || '').localeCompare(String(b.enteredTrainingUtc || ''));
      }
      if (sortMode === 'layer') {
        return String(a.layer || '').localeCompare(String(b.layer || ''), 'zh-Hans-CN');
      }
      if (sortMode === 'text') {
        return String(a.currentText || '').localeCompare(String(b.currentText || ''), 'zh-Hans-CN');
      }
      if (sortMode === 'action') {
        return String(a.labelAction || '').localeCompare(String(b.labelAction || ''), 'zh-Hans-CN');
      }
      return String(b.enteredTrainingUtc || '').localeCompare(String(a.enteredTrainingUtc || ''));
    });
  }, [records, query, actionFilter, layerFilter, fontFilter, sortMode]);

  const columns = useMemo<ColumnDef<TrainingDatasetRecord>[]>(
    () => [
      {
        id: 'action',
        header: '标注',
        size: 118,
        cell: ({ row }) => {
          const record = row.original;
          return (
            <div className="grid gap-1">
              <Pill tone={actionTone(record.labelAction)}>{actionLabel(record.labelAction)}</Pill>
              <span className="text-caption text-[var(--color-text-disabled)]">
                {record.featureRows || 0}f
              </span>
            </div>
          );
        }
      },
      {
        id: 'sourceText',
        header: '原文',
        size: 240,
        cell: ({ row }) => {
          const record = row.original;
          return (
            <div className="grid gap-1">
              <strong className="break-all text-body text-[var(--color-text)]">
                {record.currentText || '--'}
              </strong>
              <span className="break-all text-caption text-[var(--color-text-muted)]">
                候选：{compactText(record.candidateText || '--', 72)}
              </span>
            </div>
          );
        }
      },
      {
        id: 'labelText',
        header: '正确文本',
        size: 240,
        cell: ({ row }) => {
          const record = row.original;
          return (
            <div className="grid gap-1">
              <strong className="break-all text-body text-[var(--color-keep)]">
                {record.labelText || '--'}
              </strong>
              <span className="text-caption text-[var(--color-text-muted)]">
                {record.labelAction ? `动作：${actionLabel(record.labelAction)}` : '未标注动作'}
              </span>
            </div>
          );
        }
      },
      {
        id: 'source',
        header: '来源',
        size: 260,
        cell: ({ row }) => {
          const record = row.original;
          return (
            <div className="grid gap-1 text-body-sm">
              <strong className="truncate text-[var(--color-text)]">{record.drawingFileName || 'DWG'}</strong>
              <span className="text-[var(--color-text-soft)]">
                Handle {record.handle || '--'} / Layer {record.layer || '--'}
              </span>
              <span className="truncate text-caption text-[var(--color-text-muted)]">
                Block {record.ownerBlockName || '--'}
              </span>
            </div>
          );
        }
      },
      {
        id: 'font',
        header: '字体',
        size: 220,
        cell: ({ row }) => {
          const record = row.original;
          return (
            <div className="grid gap-1 text-body-sm">
              <strong className="truncate text-[var(--color-text)]">{record.textStyleName || '--'}</strong>
              <span className="truncate text-[var(--color-text-soft)]">
                {record.font || '--'} / {record.bigFont || '--'}
              </span>
              <span className="text-caption text-[var(--color-text-muted)]">
                {record.isFromExternalReference ? '外部参照' : '当前图纸'}
              </span>
            </div>
          );
        }
      },
      {
        id: 'entered',
        header: '入集',
        size: 170,
        cell: ({ row }) => {
          const record = row.original;
          return (
            <div className="grid gap-1">
              <strong className="text-body-sm text-[var(--color-text)]">
                {formatDateTime(record.enteredTrainingUtc)}
              </strong>
              <span className="text-caption text-[var(--color-text-muted)]">
                {record.trainingSource || 'reviewed-jsonl'}
              </span>
            </div>
          );
        }
      },
      {
        id: 'tools',
        header: '操作',
        size: 130,
        cell: ({ row }) => (
          <Button
            variant="danger"
            size="sm"
            icon={Trash2}
            disabled={busy}
            onClick={() => onDeleteRecord(row.original)}
          >
            删除回流
          </Button>
        )
      }
    ],
    [busy, onDeleteRecord]
  );

  function clearFilters() {
    setQuery('');
    setActionFilter('all');
    setLayerFilter('all');
    setFontFilter('all');
    setSortMode('entered-desc');
  }

  return (
    <div className="grid h-full min-h-0 grid-rows-[auto_minmax(0,1fr)] gap-4 p-4">
      <Panel>
        <PanelHeader
          eyebrow="Training Dataset"
          title="训练数据集"
          description="已进入训练集的记录会从复核页隐藏；删除后回流到复核队列。"
          actions={<Button variant="ghost" icon={RotateCcw} disabled={busy} onClick={clearFilters}>清空筛选</Button>}
        />
        <div className="grid gap-3 p-4">
          <div className="grid grid-cols-4 gap-3 max-[900px]:grid-cols-2 max-[560px]:grid-cols-1">
            <Metric label="训练集记录" value={dataset?.summary?.total || 0} tone="ai" />
            <Metric label="Feature 行" value={dataset?.summary?.featureRows || 0} tone="repair" />
            <Metric label="当前显示" value={visibleRecords.length} tone="keep" />
            <Metric label="动作类型" value={filters.actions.length} tone="warn" />
          </div>
          <div className="grid grid-cols-[minmax(220px,1fr)_repeat(4,180px)] gap-2 max-[1100px]:grid-cols-2 max-[560px]:grid-cols-1">
            <div className="relative">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-[var(--color-text-disabled)]" aria-hidden />
              <TextInput
                className="pl-9"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="原文 / 正确文本 / 图纸 / Handle / Layer"
              />
            </div>
            <NativeSelect value={actionFilter} onChange={(event) => setActionFilter(event.target.value)} aria-label="标注动作">
              <option value="all">全部动作</option>
              {filters.actions.map((value) => (
                <option key={value} value={value}>{actionLabel(value)}</option>
              ))}
            </NativeSelect>
            <NativeSelect value={layerFilter} onChange={(event) => setLayerFilter(event.target.value)} aria-label="图层">
              <option value="all">全部图层</option>
              {filters.layers.map((value) => (
                <option key={value} value={value}>{value}</option>
              ))}
            </NativeSelect>
            <NativeSelect value={fontFilter} onChange={(event) => setFontFilter(event.target.value)} aria-label="字体">
              <option value="all">全部字体</option>
              {filters.fonts.map((value) => (
                <option key={value} value={value}>{value}</option>
              ))}
            </NativeSelect>
            <NativeSelect value={sortMode} onChange={(event) => setSortMode(event.target.value)} aria-label="排序">
              <option value="entered-desc">入集时间新到旧</option>
              <option value="entered-asc">入集时间旧到新</option>
              <option value="layer">按图层</option>
              <option value="text">按原文</option>
              <option value="action">按动作</option>
            </NativeSelect>
          </div>
        </div>
      </Panel>

      <Panel className="flex min-h-0 flex-col overflow-hidden">
        <DataTable
          data={visibleRecords}
          columns={columns}
          getRowId={(record, index) => record.groupId || `training-${index}`}
          estimateRowHeight={104}
          emptyTitle="当前训练数据集没有匹配记录"
          emptyDescription="生成 Feature 后，已进入训练集的记录会出现在这里。"
        />
      </Panel>
    </div>
  );
}
