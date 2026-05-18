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
  DwgCoord,
  DwgNearbyText,
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
    const originalText = String(group.currentText || source?.currentText || '');
    const finalText = String(edit.labelText ?? saved?.labelText ?? group.candidateText ?? source?.candidateText ?? originalText);
    const mode = correctTextModeForReview(originalText, finalText, edit, saved, group);
    const candidateTexts = nonEmptyUnique([
      ...options.map((option) => option.text),
      group.candidateText,
      source?.candidateText,
      originalText
    ]);
    const selectedCandidate = candidateTexts.includes(finalText) ? finalText : candidateTexts[0] || finalText;
    const context = source?.context || group.context || group.contextSummary || {};
    const coord = pointForRecord(source, group.id || originalText);
    const dwgFile = drawingName(source, group, app);

    return {
      id: group.id,
      originalText,
      candidateTexts,
      selectedCandidate,
      action: actionToBolt(edit.labelAction || saved?.labelAction || group.recommendedAction),
      status: reviewStatus(group, saved),
      riskLevel: riskLevel(group) as BoltRiskLevel,
      layer: String(context.layer || 'MISC'),
      font: String(context.textStyleName || context.textStyleFileName || context.textStyleBigFontFileName || '--'),
      encodingPath: String(group.encodingPath || group.candidateSource || source?.candidates?.[0]?.source || '--'),
      count: Number(group.impactCount || group.count || group.recordIds?.length || 1),
      confidence: Number(source?.candidates?.[0]?.targetScore || 0),
      dwgFile,
      reviewedAt: saved?.reviewedUtc,
      sourceGroupId: source?.groupId,
      correctTextMode: mode,
      manualText: mode === 'manual' ? finalText : '',
      coord,
      nearbyTexts: []
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
      originalText: String(record.currentText || ''),
      correctText: String(record.labelText || record.candidateText || record.currentText || ''),
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

export function clusterWithNearbyTexts(app: BootstrapPayload | null, cluster: DBTextCluster | null): DBTextCluster | null {
  if (!cluster) return null;
  return {
    ...cluster,
    nearbyTexts: nearbyTextViews(app?.data?.records || [], cluster.sourceGroupId || cluster.id)
  };
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
    wrongFixes: Number(summary.falseRepairs || 0),
    bestEpoch: Number(report.trainingConfig?.bestIteration || report.trainingConfig?.actualIterations || 0),
    simulatedTests: simulationRows(report.errorSamples || report.testReport?.details || [])
  };
}

export function reviewEditForPatch(cluster: DBTextCluster, patch: Partial<Pick<DBTextCluster, 'correctTextMode' | 'selectedCandidate' | 'manualText'>>) {
  const mode = patch.correctTextMode || cluster.correctTextMode;
  const selectedCandidate = patch.selectedCandidate || cluster.selectedCandidate || cluster.candidateTexts[0] || cluster.originalText;
  const manualText = patch.manualText ?? cluster.manualText;
  if (mode === 'original') {
    return {
      labelAction: 'keep' as LabelAction,
      labelText: cluster.originalText,
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
  return {
    labelAction: 'repair' as LabelAction,
    labelText: selectedCandidate,
    candidateKey: selectedCandidate,
    candidateIndex: Math.max(0, cluster.candidateTexts.indexOf(selectedCandidate))
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
    const aiOutput = String(detail.bestText || detail.decision || '');
    const correctText = String(detail.labelText || '');
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
  const action = String(detail.labelAction || '');
  if (action === 'repair' && detail.bestText !== undefined && detail.labelText !== undefined) {
    return String(detail.bestText) !== String(detail.labelText);
  }
  if (detail.decision !== undefined && action) return String(detail.decision) !== action;
  return true;
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

function nearbyTextViews(records: CandidateRecord[], activeGroupId: string): DwgNearbyText[] {
  const nearby: DwgNearbyText[] = [];
  for (const record of records) {
    if (record.groupId === activeGroupId || !record.currentText) continue;
    const point = pointForRecord(record, record.groupId || record.currentText || '');
    nearby.push({
      id: String(record.groupId || `${point.x}-${point.y}`),
      text: String(record.currentText || ''),
      x: point.x,
      y: point.y,
      layer: String(record.context?.layer || 'MISC'),
      isGarbled: Boolean(record.problemGate?.hasProblem || record.risk?.highRisk)
    });
    if (nearby.length >= 24) break;
  }
  return nearby;
}

function pointForRecord(record: CandidateRecord | null | undefined, fallback: string): DwgCoord {
  const geometry = record?.geometry as { position?: { x?: number; y?: number } } | undefined;
  const position = geometry?.position;
  const x = Number(position?.x);
  const y = Number(position?.y);
  if (Number.isFinite(x) && Number.isFinite(y)) return { x, y };
  const hash = fallback.split('').reduce((sum, char) => sum + char.charCodeAt(0), 0);
  return {
    x: 1000 + (hash % 900),
    y: 600 + ((hash * 17) % 500)
  };
}

function nonEmptyUnique(values: Array<string | undefined>): string[] {
  const seen = new Set<string>();
  for (const value of values) {
    const text = String(value || '').trim();
    if (text) seen.add(text);
  }
  return [...seen];
}
