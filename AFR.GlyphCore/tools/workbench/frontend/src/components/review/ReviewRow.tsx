/**
 * AFR 文枢工作台复核表格行
 * 
 * 包含候选下拉 / 手动输入 / 原文锁定三种正确文本编辑器和修复方式 segmented control。
 * 修复原 main.jsx 中 ReviewTable 接收 `reviewed` 但内部用 `reviewedMap` 的 prop 名不匹配 Bug。
 */

import { useMemo } from 'react';
import { Pill } from '@/components/ui/Pill';
import { Checkbox, NativeSelect, SegmentedControl, TextInput } from '@/components/ui/Controls';
import { candidateOptionsForGroup, initialCandidateKey, riskLevel, riskLabel, type ReviewGroup, type CandidateRecord, type ReviewedRecord } from '@/lib/utils';
import { cn } from '@/lib/utils';

interface ReviewRowProps {
  group: ReviewGroup;
  index: number;
  /** 已保存的复核记录（prop 统一命名为 reviewed，修复旧 reviewedMap/reviewed 不一致 Bug） */
  reviewed: ReviewedRecord | null;
  recordsById: Map<string, CandidateRecord>;
  selected: boolean;
  edit: Partial<{
    labelAction: string;
    labelText: string;
    candidateKey: string;
    candidateIndex: number | null;
    candidateMode: string;
    fixMode: string;
  }>;
  onToggleSelection: (id: string) => void;
  onUpdateEdit: (id: string, patch: { [key: string]: any }) => void;
}

export function ReviewRow({
  group,
  index,
  reviewed,
  recordsById,
  selected,
  edit,
  onToggleSelection,
  onUpdateEdit
}: ReviewRowProps) {
  const candidateOptions = useMemo(
    () => candidateOptionsForGroup(group, reviewed, recordsById),
    [group, reviewed, recordsById]
  );

  const baseFinalText =
    edit.labelText ?? reviewed?.labelText ?? group.candidateText ?? group.currentText ?? '';

  const candidateKey =
    edit.candidateKey ||
    initialCandidateKey(candidateOptions, reviewed, group, baseFinalText);

  const selectedOption = candidateOptions.find((o) => o.key === candidateKey);
  const isManualText = candidateKey === '__manual__' || !selectedOption;
  const finalText = isManualText ? baseFinalText : selectedOption!.text;

  const actionValue =
    edit.labelAction ||
    reviewed?.labelAction ||
    (selectedOption?.isNoOp ? 'keep' : group.recommendedAction || 'repair');

  const fixMode =
    edit.fixMode ||
    (actionValue === 'keep' ? 'original' : isManualText ? 'manual' : 'candidate');

  const resolvedFixMode =
    fixMode === 'candidate' && candidateOptions.length === 0 ? 'manual' : fixMode;

  const sourceText = group.sourcePatternLabel || group.currentText || '--';
  const isReviewed = Number(group.unreviewedCount || 0) <= 0;
  const context = group.context || {};
  const layer = context.layer || context.baseLayer || '--';
  const font = context.textStyleName || context.textStyleFileName || 'current-noop';
  const encoding = group.encodingPath || group.candidateSource || '--';
  const impact = group.impactCount || group.count || 0;
  const risk = riskLevel(group);
  const candidateSelectValue = selectedOption
    ? candidateKey
    : candidateOptions[0]?.key || '__manual__';

  function patch(p: Record<string, any>) {
    if (!selected) onToggleSelection(group.id);
    onUpdateEdit(group.id, p);
  }

  function chooseFinalText(value: string) {
    if (value === '__manual__') {
      patch({ candidateMode: 'manual', fixMode: 'manual', candidateKey: '__manual__', candidateIndex: null, labelText: finalText || '', labelAction: actionValue === 'keep' ? 'repair' : actionValue });
      return;
    }
    const option = candidateOptions.find((o) => o.key === value);
    if (!option) return;
    patch({ candidateMode: 'candidate', fixMode: 'candidate', candidateKey: option.key, candidateIndex: option.index, labelText: option.text, labelAction: option.isNoOp ? 'keep' : 'repair' });
  }

  function setFixMode(value: string) {
    if (value === 'original') {
      patch({ candidateMode: 'original', fixMode: 'original', candidateKey: '__original__', candidateIndex: null, labelText: group.currentText || sourceText, labelAction: 'keep' });
      return;
    }
    if (value === 'manual') {
      patch({ candidateMode: 'manual', fixMode: 'manual', candidateKey: '__manual__', candidateIndex: null, labelText: finalText || group.candidateText || group.currentText || '', labelAction: actionValue === 'keep' ? 'repair' : actionValue });
      return;
    }
    const option = candidateOptions.find((o) => !o.isNoOp) || candidateOptions[0];
    if (!option) { setFixMode('manual'); return; }
    patch({ candidateMode: 'candidate', fixMode: 'candidate', candidateKey: option.key, candidateIndex: option.index, labelText: option.text, labelAction: option.isNoOp ? 'keep' : 'repair' });
  }

  return (
    <tr
      className={cn(
        'border-b border-[var(--color-line-soft)] transition-colors hover:bg-[var(--color-surface-2)]',
        selected && 'bg-[var(--color-ai-soft)]',
        risk === 'high' && 'border-l-4 border-l-[var(--color-unsafe-border)]',
        risk === 'medium' && 'border-l-4 border-l-[var(--color-warn-border)]'
      )}
    >
      {/* 状态列 */}
      <td className="px-2 py-1.5 align-top whitespace-nowrap">
        <div className="flex flex-col gap-0.5">
          <Pill tone={isReviewed ? 'ok' : 'warn'}>{isReviewed ? '已审核' : '未审核'}</Pill>
          <span className="text-caption text-[var(--color-text-disabled)]">#{index + 1}</span>
        </div>
      </td>

      {/* 原文列 */}
      <td className="px-2 py-1.5 align-top max-w-52">
        <div className="flex flex-col gap-0.5">
          <strong className="text-body text-[var(--color-text)] break-all">{sourceText}</strong>
          <span className="text-caption text-[var(--color-text-soft)] break-all">
            编码：{encoding}　字体：{font}　图层：{layer}
          </span>
        </div>
      </td>

      {/* 正确文本列 */}
      <td className="px-2 py-1.5 align-top min-w-40 max-w-64">
        <div className="flex flex-col gap-1">
          {resolvedFixMode === 'manual' ? (
            <TextInput
              className="h-9 border-[var(--color-ai-border)]"
              value={finalText}
              onChange={(e) =>
                patch({ candidateMode: 'manual', fixMode: 'manual', candidateKey: '__manual__', candidateIndex: null, labelText: e.target.value, labelAction: actionValue === 'keep' ? 'repair' : actionValue })
              }
            />
          ) : resolvedFixMode === 'original' ? (
            <div className="flex h-9 w-full items-center justify-between rounded-[var(--radius-md)] border border-[var(--color-line)] bg-[var(--color-surface-2)] px-3 text-body-sm text-[var(--color-text-soft)]">
              <span className="truncate">{group.currentText || sourceText}</span>
              <em className="text-caption shrink-0 ml-1">原文正确</em>
            </div>
          ) : (
            <NativeSelect
              className="h-9"
              value={candidateSelectValue}
              onChange={(e) => chooseFinalText(e.target.value)}
            >
              {candidateOptions.map((o) => (
                <option key={o.key} value={o.key}>{o.label}</option>
              ))}
              <option value="__manual__">候选都不对，手动输入</option>
            </NativeSelect>
          )}
          <span className="text-caption text-[var(--color-text-disabled)]">
            {selectedOption?.source || group.candidateSource || group.encodingPath || '规则候选'}
          </span>
        </div>
      </td>

      {/* 修复方式列 */}
      <td className="px-2 py-1.5 align-top whitespace-nowrap">
        <SegmentedControl
          value={resolvedFixMode}
          ariaLabel="修复方式"
          options={[
            { value: 'original', label: '原文' },
            { value: 'candidate', label: '候选' },
            { value: 'manual', label: '手动' }
          ]}
          onChange={setFixMode}
        />
      </td>

      {/* 数量/风险列 */}
      <td className="px-2 py-1.5 align-top whitespace-nowrap text-right">
        <div className="flex flex-col items-end gap-0.5">
          <strong className="text-body-lg text-[var(--color-text)]">{impact}</strong>
          <span
            className={cn(
              'text-caption',
              risk === 'high' && 'text-[var(--color-unsafe)]',
              risk === 'medium' && 'text-[var(--color-warn)]',
              risk === 'low' && 'text-[var(--color-text-disabled)]'
            )}
            title={riskLabel(risk)}
          >
            {risk === 'high' ? '●' : risk === 'medium' ? '◐' : '○'}
          </span>
        </div>
      </td>

      {/* 选择列 */}
      <td className="px-2 py-1.5 align-top text-center">
        <Checkbox
          checked={selected}
          label={`选择第 ${index + 1} 行`}
          onCheckedChange={() => onToggleSelection(group.id)}
        />
      </td>
    </tr>
  );
}
