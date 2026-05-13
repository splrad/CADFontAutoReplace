/**
 * AFR 文枢工作台通用工具函数
 * 
 * 从 main.jsx 迁移并加 TypeScript 类型注解。
 */

import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';
import type {
  Candidate,
  CandidateRecord,
  LabelAction,
  ReviewCluster,
  ReviewedRecord,
  RiskLevel,
  RiskSummary,
  TabId
} from '@/types/api';

// ========== Tailwind 类名合并工具 ==========

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

// ========== 常量 ==========

export const TABS = [
  ['packages', '数据包'],
  ['review', '人工复核'],
  ['dataset', '训练数据集'],
  ['features', '特征生成'],
  ['training', '模型训练'],
  ['report', '模型报告']
] as const;

export const LABEL_ACTIONS = ['repair', 'keep', 'unsafe', 'unknown', 'glyph-issue'] as const;
export type ReviewGroup = ReviewCluster;
export type {
  Candidate,
  CandidateRecord,
  LabelAction,
  ReviewedRecord,
  RiskLevel,
  RiskSummary,
  TabId
};

// ========== 文本处理 ==========

export function compactText(value: string | null | undefined, limit: number = 64): string {
  const text = String(value ?? '').replace(/\s+/g, ' ').trim();
  return text.length > limit ? `${text.slice(0, limit)}...` : text;
}

// ========== 时间格式化 ==========

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '--';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN', { hour12: false });
}

// ========== 风险等级 ==========

export function riskLevel(group: ReviewGroup): RiskLevel {
  const risk = group?.risk || group?.riskSummary || {};
  const highSignals =
    Number(risk.highRisk || 0) +
    Number(risk.currentUnsafe || 0) +
    Number(risk.candidateUnsafe || 0);
  if (highSignals > 0 || Number(group?.riskSignalCount || 0) > 0) return 'high';
  const mediumSignals =
    Number(risk.candidateConflict || 0) + Number(risk.hasNonRoundTrip || 0);
  if (mediumSignals > 0) return 'medium';
  if (
    group?.batchMode === 'sample' ||
    Number(group?.contextSummary?.uniqueContexts || 0) > 1
  )
    return 'medium';
  return 'low';
}

export function riskLabel(level: RiskLevel): string {
  const labels: Record<RiskLevel, string> = {
    high: '高风险',
    medium: '中风险',
    low: '低风险'
  };
  return labels[level] || '低风险';
}

// ========== 动作标签 ==========

export function actionLabel(action: LabelAction | string | null | undefined): string {
  const labels: Record<string, string> = {
    repair: '修复',
    keep: '保留',
    unsafe: '不安全',
    unknown: '未知',
    'glyph-issue': '字形问题'
  };
  return labels[action || ''] || action || '--';
}

export type ActionTone = 'ai' | 'ok' | 'risk' | 'warn' | '';

export function actionTone(action: LabelAction | string | null | undefined): ActionTone {
  const tones: Record<string, ActionTone> = {
    repair: 'ai',
    keep: 'ok',
    unsafe: 'risk',
    unknown: 'warn',
    'glyph-issue': 'warn'
  };
  return tones[action || ''] || '';
}

// ========== 选项值提取 ==========

export function optionValues<T>(
  groups: T[],
  selector: (item: T) => string | null | undefined
): string[] {
  return Array.from(
    new Set(
      (groups || [])
        .map((group) => String(selector(group) || '').trim())
        .filter(Boolean)
    )
  ).sort((a, b) => a.localeCompare(b, 'zh-Hans-CN'));
}

// ========== Tab 标签 ==========

export function tabLabel(id: string): string {
  return TABS.find(([tabId]) => tabId === id)?.[1] || id;
}

// ========== 模块描述 ==========

export function moduleDescription(id: string): string {
  const descriptions: Record<string, string> = {
    packages: '切换候选包并查看 DWG 来源、Reviewed 进度和数据规模。',
    review: '复核保存后直接进入训练数据集；从训练集删除后回流复核。',
    dataset: '查看、追溯和删除已经进入训练数据集的记录。',
    features: '基于当前训练数据集刷新特征表。',
    training: '多选数据包后完整重训本地模型并跟踪训练日志。',
    report: '查看模型验证指标、发布命令和当前模型清单。'
  };
  return descriptions[id] || '';
}

// ========== 初始 Tab ==========

export function initialTab(): TabId {
  const id = window.location.hash.replace(/^#/, '');
  return TABS.some(([tabId]) => tabId === id) ? (id as TabId) : 'review';
}

// ========== Cluster List 提取 ==========

export function clusterList(payload: any): ReviewGroup[] {
  return payload?.clusters || payload?.groups || [];
}

// ========== 百分比格式化 ==========

export function pct(value: number | null | undefined): string {
  const n = Number(value);
  return Number.isFinite(n) ? `${(n * 100).toFixed(2)}%` : '0%';
}

// ========== 候选选项生成 ==========

export interface CandidateOption {
  key: string;
  index: number | null;
  text: string;
  source: string;
  isNoOp: boolean;
  label: string;
}

export function candidateOptionsForGroup(
  group: ReviewGroup,
  saved: ReviewedRecord | null,
  recordsById: Map<string, CandidateRecord>
): CandidateOption[] {
  const sourceRecord = sourceRecordForGroup(group, recordsById);
  const options: CandidateOption[] = [];
  const seen = new Set<string>();

  function addOption(
    candidate: any,
    indexFallback: number | null,
    sourceFallback: string
  ) {
    const text = String(
      candidate?.text ?? candidate?.candidateText ?? candidate ?? ''
    );
    if (!text) return;
    if (seen.has(text)) return;
    seen.add(text);
    const index =
      candidate?.index !== undefined && Number.isInteger(candidate.index)
        ? candidate.index
        : Number.isInteger(indexFallback)
        ? indexFallback
        : null;
    const source = candidate?.source || candidate?.candidateSource || sourceFallback || '规则候选';
    const isNoOp = Boolean(candidate?.isNoOp);
    const order = options.length + 1;
    options.push({
      key: `${index === null ? 'text' : `idx-${index}`}-${order}`,
      index,
      text,
      source,
      isNoOp,
      label: `${order}. ${compactText(text)} · ${isNoOp ? '保留原文' : source}`
    });
  }

  (sourceRecord?.candidates || []).forEach((candidate, index) =>
    addOption(candidate, index, candidate?.source || '')
  );
  (saved?.candidates || []).forEach((candidate, index) =>
    addOption(
      candidate,
      candidate?.index !== undefined && Number.isInteger(candidate.index)
        ? candidate.index
        : index,
      candidate?.source || ''
    )
  );
  addOption(
    { text: group?.candidateText, source: group?.candidateSource || group?.encodingPath },
    group?.recommendedCandidateIndex ?? null,
    group?.candidateSource || group?.encodingPath || ''
  );
  addOption(
    { text: group?.currentText, source: '原文保留', isNoOp: true },
    null,
    '原文保留'
  );
  return options;
}

export function initialCandidateKey(
  options: CandidateOption[],
  saved: ReviewedRecord | null,
  group: ReviewGroup,
  finalText: string
): string {
  const byText = options.find((option) => option.text === finalText);
  if (byText) return byText.key;
  const savedIndex = saved?.selectedCandidateIndex;
  if (savedIndex !== undefined && Number.isInteger(savedIndex)) {
    const bySavedIndex = options.find((option) => option.index === savedIndex);
    if (bySavedIndex) return bySavedIndex.key;
  }
  const recommendedIndex = group?.recommendedCandidateIndex;
  if (recommendedIndex !== undefined && Number.isInteger(recommendedIndex)) {
    const byRecommendedIndex = options.find((option) => option.index === recommendedIndex);
    if (byRecommendedIndex) return byRecommendedIndex.key;
  }
  if (finalText) return '__manual__';
  return options[0]?.key || '__manual__';
}

// ========== 辅助函数 ==========

export function sourceRecordForGroup(
  group: ReviewGroup,
  recordsById: Map<string, CandidateRecord>
): CandidateRecord | null {
  if (!group || !recordsById) return null;
  const ids = [
    group.representativeRecords?.[0]?.groupId,
    group.sampleRecords?.[0]?.groupId,
    ...(group.recordIds || [])
  ].filter(Boolean) as string[];
  for (const id of ids) {
    const record = recordsById.get(id);
    if (record) return record;
  }
  return (group.representativeRecords?.[0] as CandidateRecord) || (group.sampleRecords?.[0] as CandidateRecord) || null;
}

export function reviewedForGroup(
  group: ReviewGroup,
  reviewedMap: { [key: string]: ReviewedRecord }
): ReviewedRecord | null {
  if (!group || !reviewedMap) return null;
  for (const id of group.recordIds || []) {
    if (reviewedMap[id]) return reviewedMap[id];
  }
  for (const item of group.alreadyReviewedRecords || []) {
    const id = item?.groupId;
    if (id && reviewedMap[id]) return reviewedMap[id];
  }
  return null;
}
