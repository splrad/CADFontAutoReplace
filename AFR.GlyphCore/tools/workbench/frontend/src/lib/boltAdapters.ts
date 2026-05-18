import {
  candidateOptionsForGroup,
  clusterList,
  formatDateTime,
  reviewedForGroup,
  riskLevel,
  sourceRecordForGroup
} from '@/lib/utils';
import type {
  BootstrapPayload,
  CandidateRecord,
  LabelAction,
  PackageSummary,
  ReviewCluster,
  ReviewClustersPayload,
  ReviewEdit,
  ReviewedRecord,
  TabId as ApiTabId,
  TrainingDatasetRecord,
  ValidationDetail,
  ValidationSummary
} from '@/types/api';
import type {
  ActionType,
  CorrectTextMode,
  DataPackage,
  DBTextCluster,
  ModelReport,
  PublishStatus,
  RiskLevel as BoltRiskLevel,
  ReviewStatus,
  SimulatedTestResult,
  TabId,
  TrainingRecord,
  TrainingRun,
  TrainingStatus
} from '@/types/bolt';

export const PAGE_STATUS: Record<TabId, string> = {
  annotation: '数据标注 · 点击行查看参考项',
  dataset: '训练数据集管理',
  training: '模型训练',
  report: '模型报告'
};

export function toBoltTab(tab: ApiTabId): TabId {
  if (tab === 'dataset' || tab === 'training' || tab === 'report') return tab;
  return 'annotation';
}

export function toApiTab(tab: TabId): ApiTabId {
  return tab === 'annotation' ? 'review' : tab;
}

export function lastSavedAt(app: BootstrapPayload | null): string {
  const value =
    app?.data?.summary?.generatedUtc ||
    app?.data?.manifest?.createdUtc ||
    app?.report?.modelCreatedUtc ||
    app?.report?.manifest?.trainedUtc;
  return value ? formatDateTime(String(value)) : '';
}

export function packageViews(app: BootstrapPayload | null, records: TrainingRecord[] = []): DataPackage[] {
  const packages = app?.packages || [];
  const recordCounts = records.length > 0 ? trainingRecordCounts(records) : undefined;
  return packages.map((pkg) => packageView(pkg, recordCounts));
}

let reviewClusterCache:
  | {
      app: BootstrapPayload | null;
      payload: ReviewClustersPayload;
      edits: Record<string, ReviewEdit>;
      value: DBTextCluster[];
    }
  | null = null;

export function reviewClusterViews(
  app: BootstrapPayload | null,
  payload: ReviewClustersPayload,
  edits: Record<string, ReviewEdit>
): DBTextCluster[] {
  if (
    reviewClusterCache &&
    reviewClusterCache.app === app &&
    reviewClusterCache.payload === payload &&
    reviewClusterCache.edits === edits
  ) {
    return reviewClusterCache.value;
  }

  const records = app?.data?.records || [];
  const reviewed = app?.data?.reviewed || {};
  const recordsById = new Map(records.map((record) => [String(record.groupId || ''), record]));
  const groups = clusterList(payload);

  const value = groups.map((group) => {
    const saved = reviewedForGroup(group, reviewed);
    const source = sourceRecordForGroup(group, recordsById);
    const options = candidateOptionsForGroup(group, saved, recordsById);
    const edit = edits[group.id] || {};
    const rawOriginalText = String(group.rawCurrentText || source?.rawCurrentText || source?.currentText || group.currentText || '');
    const originalText = String(group.displayText || group.currentText || source?.displayText || source?.currentText || '');
    const savedLabelText = saved?.labelAction === 'keep' ? originalText : saved?.labelText;
    const finalText = String(edit.labelText ?? savedLabelText ?? group.candidateText ?? source?.candidateText ?? originalText);
    const mode = correctTextModeForReview(originalText, finalText, edit, saved, group);
    const candidateTexts = nonEmptyUnique([
      ...options.map((option) => displayCandidateOptionText(option.text, option.isNoOp, rawOriginalText, originalText)),
      displayCandidateOptionText(group.candidateText, false, rawOriginalText, originalText),
      displayCandidateOptionText(source?.candidateText, false, rawOriginalText, originalText),
      finalText,
      originalText
    ]);
    const selectedCandidate = selectedCandidateForMode(mode, finalText, originalText, candidateTexts);
    const candidateSources = candidateSourceMap(options, group, source, originalText, rawOriginalText);
    const context = source?.context || group.context || group.contextSummary || {};
    const dwgFile = drawingName(source, group, app);

    return {
      id: group.id,
      originalText,
      rawOriginalText,
      candidateTexts,
      selectedCandidate,
      action: actionToBolt(edit.labelAction || saved?.labelAction || group.recommendedAction),
      status: reviewStatus(group, saved),
      riskLevel: riskLevel(group) as BoltRiskLevel,
      layer: String(context.layer || 'MISC'),
      font: String(context.textStyleName || context.textStyleFileName || context.textStyleBigFontFileName || '--'),
      encodingPath: encodingPathForMode(mode, selectedCandidate, candidateSources),
      count: Number(group.impactCount || group.count || group.recordIds?.length || 1),
      confidence: Number(source?.candidates?.[0]?.targetScore || 0),
      dwgFile,
      reviewedAt: saved?.reviewedUtc,
      correctTextMode: mode,
      manualText: mode === 'manual' ? finalText : ''
    };
  });

  reviewClusterCache = { app, payload, edits, value };
  return value;
}

let trainingRecordsCache:
  | {
      source: TrainingDatasetRecord[];
      packages: PackageSummary[];
      value: TrainingRecord[];
    }
  | null = null;

export function trainingRecordViews(app: BootstrapPayload | null): TrainingRecord[] {
  const records = app?.data?.trainingDataset?.records || [];
  const packages = app?.packages || [];
  if (records.length === 0) return [];
  if (
    trainingRecordsCache &&
    trainingRecordsCache.source === records &&
    trainingRecordsCache.packages === packages
  ) {
    return trainingRecordsCache.value;
  }

  const value = records.map((record) => {
    const mode = correctTextModeForTraining(record);
    return {
      id: String(record.groupId || record.handle || `${record.currentText}-${record.enteredTrainingUtc}`),
      action: actionToBolt(record.labelAction),
      originalText: String(record.displayText || record.currentText || ''),
      correctText: String(
        record.labelAction === 'keep'
          ? record.displayText || record.currentText || ''
          : record.labelText || record.candidateText || record.currentText || ''
      ),
      correctTextMode: mode,
      source: String(record.trainingSource || record.labelAction || mode),
      font: String(record.font || record.bigFont || record.textStyleName || '--'),
      addedAt: formatDateTime(record.enteredTrainingUtc),
      dataPackageId: trainingRecordPackageId(record, packages)
    };
  });
  trainingRecordsCache = { source: records, packages, value };
  return value;
}

export function trainingRunView(app: BootstrapPayload | null, packages: DataPackage[]): TrainingRun {
  const training = app?.training || {};
  const report = app?.report || {};
  const config = report.trainingConfig || {};
  const status = trainingStatus(String(training.status || report.trainingResult?.status || 'idle'));
  const summary = report.testReport?.summary || report.blindReport?.summary || {};
  const dataPackages = packages.filter((pkg) => (pkg.trainingRecords || pkg.inTrainingSet) > 0).map((pkg) => pkg.id);
  return {
    id: String(training.logPath || report.modelDir || 'current'),
    version: String(report.modelVersion || report.manifest?.version || 'local-model'),
    status,
    startedAt: training.startedUtc ? formatDateTime(training.startedUtc) : undefined,
    completedAt: training.finishedUtc ? formatDateTime(training.finishedUtc) : undefined,
    epochs: Number(config.maxRounds || config.actualIterations || 650),
    batchSize: Number(config.earlyStoppingRounds || 50),
    learningRate: 0.05,
    dataPackages,
    logs: (training.lines || report.simulation?.lines || []).map(String),
    bestEpoch: Number(config.bestIteration || config.actualIterations || 0) || undefined,
    bestAccuracy: Number(summary.decisionAccuracy || 0) || undefined
  };
}

export function modelReportView(app: BootstrapPayload | null): ModelReport {
  const report = app?.report || {};
  const summary = report.testReport?.summary || report.blindReport?.summary || {};
  const testDetails = validationDetails(report.errorSamples, report.testReport?.details);
  const overfitScore = Math.max(
    Math.abs(Number(report.overfitting?.recallGap || 0)),
    Math.abs(Number(report.overfitting?.accuracyGap || 0))
  );
  return {
    id: String(report.modelDir || report.modelVersion || 'current-report'),
    version: String(report.modelVersion || report.manifest?.version || 'local-model'),
    publishStatus: publishStatus(report.acceptance?.status),
    publishedAt: report.modelCreatedUtc ? formatDateTime(report.modelCreatedUtc) : undefined,
    recall: numberFromSummary(summary, 'repairRecall'),
    precision: precisionFromSummary(summary),
    overfitScore,
    wrongFixes: visibleFalseRepairCount(testDetails, Number(summary.falseRepairs || 0)),
    bestEpoch: Number(report.trainingConfig?.bestIteration || report.trainingConfig?.actualIterations || 0),
    simulatedTests: simulationRows(testDetails)
  };
}

export function reviewEditForPatch(cluster: DBTextCluster, patch: Partial<Pick<DBTextCluster, 'correctTextMode' | 'selectedCandidate' | 'manualText'>>) {
  const mode = patch.correctTextMode || cluster.correctTextMode;
  const currentCandidate = cluster.selectedCandidate !== cluster.originalText ? cluster.selectedCandidate : '';
  const selectedCandidate = patch.selectedCandidate || currentCandidate || firstRepairCandidate(cluster.originalText, cluster.candidateTexts);
  const manualText = patch.manualText ?? cluster.manualText;
  if (mode === 'original') {
    return {
      labelAction: 'keep' as LabelAction,
      labelText: cluster.rawOriginalText || cluster.originalText,
      candidateKey: '__original__',
      candidateIndex: null
    };
  }
  if (mode === 'manual') {
    return {
      labelAction: 'repair' as LabelAction,
      labelText: String(manualText || selectedCandidate || cluster.originalText),
      candidateKey: '__manual__',
      candidateIndex: null
    };
  }
  const candidateIndex = cluster.candidateTexts.indexOf(selectedCandidate);
  return {
    labelAction: 'repair' as LabelAction,
    labelText: selectedCandidate,
    candidateKey: selectedCandidate,
    candidateIndex: candidateIndex >= 0 ? candidateIndex : null
  };
}

export function modeLabel(mode: CorrectTextMode): string {
  if (mode === 'original') return '原文';
  if (mode === 'candidate') return '候选';
  return '手动';
}

export function modeClass(mode: CorrectTextMode): string {
  if (mode === 'original') return 'bg-gray-100 text-gray-600';
  if (mode === 'candidate') return 'bg-blue-50 text-blue-700';
  return 'bg-amber-50 text-amber-700';
}

function packageView(pkg: PackageSummary, recordCounts?: Map<string, number>): DataPackage {
  const id = String(pkg.id || pkg.path || 'package');
  const name = String(pkg.drawing?.fileName || pkg.id || '未命名数据包');
  const inTrainingSet = Number(pkg.trainingDataset ?? recordCounts?.get(id) ?? 0);
  const dataCount = Number(pkg.records ?? pkg.reviewed ?? inTrainingSet ?? 0);
  return {
    id,
    name,
    dwgFile: String(pkg.drawing?.fileName || pkg.drawing?.path || pkg.path || id),
    createdAt: '',
    status: dataCount > 0 && inTrainingSet >= dataCount ? 'archived' : 'active',
    dataCount,
    inTrainingSet,
    trainingRecords: inTrainingSet
  };
}

function trainingRecordCounts(records: TrainingRecord[]): Map<string, number> {
  const counts = new Map<string, number>();
  for (const record of records) {
    counts.set(record.dataPackageId, (counts.get(record.dataPackageId) || 0) + 1);
  }
  return counts;
}

function reviewStatus(group: ReviewCluster, saved: ReviewedRecord | null): ReviewStatus {
  if (saved || group.reviewStatus === 'reviewed' || Number(group.unreviewedCount || 0) === 0) return 'confirmed';
  return 'pending';
}

function correctTextModeForReview(
  originalText: string,
  finalText: string,
  edit: ReviewEdit,
  saved: ReviewedRecord | null,
  group: ReviewCluster
): CorrectTextMode {
  if (edit.candidateKey === '__manual__' || edit.candidateMode === 'manual' || edit.fixMode === 'manual') return 'manual';
  const action = edit.labelAction || saved?.labelAction || group.recommendedAction;
  if (action === 'keep' || finalText === originalText) return 'original';
  if (saved?.origin === 'manual' || saved?.originDetail === 'manual') return 'manual';
  return 'candidate';
}

function selectedCandidateForMode(
  mode: CorrectTextMode,
  finalText: string,
  originalText: string,
  candidateTexts: string[]
): string {
  if (mode === 'candidate' && finalText !== originalText && candidateTexts.includes(finalText)) {
    return finalText;
  }
  return firstRepairCandidate(originalText, candidateTexts);
}

function firstRepairCandidate(originalText: string, candidateTexts: string[]): string {
  return candidateTexts.find((text) => text && text !== originalText) || candidateTexts[0] || originalText;
}

function displayCandidateOptionText(
  value: unknown,
  isNoOp: boolean,
  rawOriginalText: string | undefined,
  originalText: string
): string {
  const text = String(value || '');
  if (!text) return '';
  if (isNoOp || (rawOriginalText && text === rawOriginalText)) return originalText;
  return text;
}

function candidateSourceMap(
  options: ReturnType<typeof candidateOptionsForGroup>,
  group: ReviewCluster,
  source: CandidateRecord | null,
  originalText: string,
  rawOriginalText?: string
): Map<string, string> {
  const result = new Map<string, string>();
  for (const option of options) {
    const text = displayCandidateOptionText(option.text, option.isNoOp, rawOriginalText, originalText);
    const sourceText = sourceLabel(option.source);
    if (text && sourceText) result.set(text, option.isNoOp ? '原文' : sourceText);
  }

  const groupCandidateText = String(group.candidateText || '');
  const groupSource = sourceLabel(group.candidateSource || group.encodingPath || group.candidateReason || group.problemGate?.reason);
  if (groupCandidateText && groupSource && !result.has(groupCandidateText)) {
    result.set(groupCandidateText, groupSource);
  }

  for (const candidate of source?.candidates || []) {
    const text = displayCandidateOptionText(
      candidate.text || candidate.candidateText,
      Boolean(candidate.isNoOp),
      rawOriginalText,
      originalText
    );
    const sourceText = sourceLabel(candidate.source || candidate.candidateSource || candidate.reason || source?.problemGate?.reason);
    if (text && sourceText && !result.has(text)) {
      result.set(text, candidate.isNoOp ? '原文' : sourceText);
    }
  }

  result.set(originalText, '原文');
  if (rawOriginalText) result.set(rawOriginalText, '原文');
  return result;
}

function sourceLabel(value: unknown): string {
  const text = String(value || '').trim();
  return text && text !== '--' ? text : '';
}

function encodingPathForMode(
  mode: CorrectTextMode,
  selectedCandidate: string,
  candidateSources: Map<string, string>
): string {
  if (mode === 'original') return '原文';
  if (mode === 'manual') return '手动';
  return candidateSources.get(selectedCandidate) || '候选路径未标明';
}

function correctTextModeForTraining(record: TrainingDatasetRecord): CorrectTextMode {
  const source = String(record.trainingSource || record.origin || '').toLowerCase();
  if (record.labelAction === 'keep') return 'original';
  if (source.includes('manual')) return 'manual';
  return 'candidate';
}

function actionToBolt(action?: LabelAction | string): ActionType {
  if (action === 'keep') return 'keep';
  if (action === 'unsafe' || action === 'glyph-issue') return 'delete';
  if (action === 'unknown') return 'manual';
  return 'fix';
}

function trainingStatus(status: string): TrainingStatus {
  if (status === 'running') return 'running';
  if (status === 'succeeded' || status === 'completed' || status === 'success') return 'completed';
  if (status === 'failed' || status === 'canceled' || status === 'cancelled') return 'failed';
  return 'idle';
}

function publishStatus(status?: string): PublishStatus {
  if (status === 'passed' || status === 'success' || status === 'published') return 'published';
  if (status === 'deprecated') return 'deprecated';
  return 'draft';
}

function numberFromSummary(summary: ValidationSummary, key: keyof ValidationSummary): number {
  const value = Number(summary[key] || 0);
  return Number.isFinite(value) ? value : 0;
}

function precisionFromSummary(summary: ValidationSummary): number {
  const explicit = Number((summary as Record<string, unknown>).precision || 0);
  if (Number.isFinite(explicit) && explicit > 0) return explicit;
  const falseRate = Number(summary.falseRepairRate || 0);
  if (Number.isFinite(falseRate) && falseRate > 0) return Math.max(0, 1 - falseRate);
  const correct = Number(summary.correctRepairs || 0);
  const falseRepairs = Number(summary.falseRepairs || 0);
  if (correct + falseRepairs > 0) return correct / (correct + falseRepairs);
  return 0;
}

function simulationRows(details: ValidationDetail[]): SimulatedTestResult[] {
  return details.map((detail, index) => {
    const originalText = String(detail.currentText || '');
    const decision = String(detail.decision || '');
    const bestText = String(detail.bestText || '');
    const aiOutput = decision === 'repair' ? bestText : originalText;
    const correctText = String(detail.labelText || originalText);
    return {
      id: String(detail.groupId || `${originalText}-${index}`),
      originalText,
      aiOutput,
      correctText,
      matched: !hasMismatch(detail),
      dataPackageId: String(detail.packageId || detail.exportId || detail.drawingFileName || '')
    };
  });
}

function hasMismatch(detail: ValidationDetail): boolean {
  const explicitDecision = (detail as Record<string, unknown>).correctDecision;
  if (typeof explicitDecision === 'boolean') return !explicitDecision;
  const severity = String(detail.severity || '');
  if (severity) return severity !== 'ok';

  const action = String(detail.labelAction || '');
  const decision = String(detail.decision || '');
  const finalText = decision === 'repair' ? detail.bestText : detail.currentText;
  if (action === 'repair' && detail.bestText !== undefined && detail.labelText !== undefined) {
    return !visibleTextEqual(finalText, detail.labelText);
  }
  if (detail.decision !== undefined && action) return String(detail.decision) !== action;
  return true;
}

function visibleFalseRepairCount(details: ValidationDetail[], fallback: number): number {
  if (!Array.isArray(details) || details.length === 0) return fallback;
  return details.filter((detail) => {
    const severity = String(detail.severity || '');
    const falseRepair = Boolean((detail as Record<string, unknown>).falseRepair);
    return (falseRepair || severity === 'false-repair' || severity === 'wrong-repair') && hasMismatch(detail);
  }).length;
}

function validationDetails(errorSamples?: ValidationDetail[], fullDetails?: ValidationDetail[]): ValidationDetail[] {
  if (Array.isArray(errorSamples) && errorSamples.length > 0) return errorSamples;
  if (Array.isArray(fullDetails)) return fullDetails;
  return [];
}

function normalizedVisibleText(value: unknown): string {
  return normalizeShxNumberSignAliases(String(value ?? ''))
    .normalize('NFKC')
    .replace(/[\u200B-\u200D\uFEFF]/g, '');
}

function visibleTextEqual(left: unknown, right: unknown): boolean {
  const leftText = normalizedVisibleText(left);
  const rightText = normalizedVisibleText(right);
  if (leftText === rightText) return true;
  if (startsWithPlaceholderSpaceRun(leftText) || startsWithPlaceholderSpaceRun(rightText)) return false;
  return leftText.trim() === rightText.trim();
}

function startsWithPlaceholderSpaceRun(value: string): boolean {
  return value.length >= 2 && /^\s\s/.test(value);
}

function normalizeShxNumberSignAliases(value: string): string {
  if (!value.includes('井')) return value;
  const chars = Array.from(value);
  return chars
    .map((char, index) => (char === '井' && shouldRenderNumberSignAlias(chars, index) ? '#' : char))
    .join('');
}

function shouldRenderNumberSignAlias(chars: string[], index: number): boolean {
  if (index < 2 || index + 1 >= chars.length) return false;
  if (chars[index - 1] !== '-' && chars[index - 1] !== '－') return false;
  if (!isAsciiAlnum(chars[index + 1])) return false;
  let start = index - 2;
  while (start >= 0 && isAsciiAlnum(chars[start])) start -= 1;
  const prefix = chars.slice(start + 1, index - 1);
  return prefix.length >= 1 && prefix.length <= 8 && prefix.some(isAsciiAlpha);
}

function isAsciiAlnum(char: string): boolean {
  return isAsciiAlpha(char) || (char >= '0' && char <= '9');
}

function isAsciiAlpha(char: string): boolean {
  return (char >= 'A' && char <= 'Z') || (char >= 'a' && char <= 'z');
}

function trainingRecordPackageId(record: TrainingDatasetRecord, packages: PackageSummary[]): string {
  const explicit = record.packageId || record.dataPackageId || record.exportId;
  if (explicit) return String(explicit);
  const drawing = String(record.drawingFileName || record.drawingPath || '');
  return String(packages.find((pkg) => pkg.drawing?.fileName === drawing || pkg.drawing?.path === drawing)?.id || drawing || 'default');
}

function drawingName(source: CandidateRecord | null, group: ReviewCluster, app: BootstrapPayload | null): string {
  const sourceDrawing = source?.drawing as { fileName?: string; name?: string; path?: string } | undefined;
  return String(
    sourceDrawing?.fileName ||
      sourceDrawing?.name ||
      sourceDrawing?.path ||
      source?.context?.drawingFileName ||
      source?.context?.drawingPath ||
      app?.data?.manifest?.drawing?.fileName ||
      app?.data?.manifest?.drawing?.path ||
      group.packageId ||
      'DWG'
  );
}

function nonEmptyUnique(values: Array<string | undefined>): string[] {
  const seen = new Set<string>();
  for (const value of values) {
    const text = String(value || '').trim();
    if (text) seen.add(text);
  }
  return [...seen];
}
