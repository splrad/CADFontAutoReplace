export type LabelAction = 'repair' | 'keep' | 'unsafe' | 'unknown' | 'glyph-issue';
export type TabId = 'packages' | 'review' | 'dataset' | 'features' | 'training' | 'report';
export type RiskLevel = 'high' | 'medium' | 'low';

export interface ApiEnvelope<T = unknown> {
  ok?: boolean;
  error?: string;
  data?: T;
  [key: string]: unknown;
}

export interface DrawingSummary {
  fileName?: string;
  path?: string;
  [key: string]: unknown;
}

export interface ManifestPayload {
  exportId?: string;
  drawing?: DrawingSummary;
  commandName?: string;
  createdUtc?: string;
  [key: string]: unknown;
}

export interface Candidate {
  text?: string;
  candidateText?: string;
  index?: number;
  source?: string;
  candidateSource?: string;
  reason?: string;
  isNoOp?: boolean;
  isRoundTrip?: boolean;
  targetScore?: number;
  [key: string]: unknown;
}

export interface CandidateContext {
  handle?: string;
  layer?: string;
  baseLayer?: string;
  ownerBlockName?: string;
  textStyleName?: string;
  textStyleFileName?: string;
  textStyleBigFontFileName?: string;
  uniqueContexts?: number;
  isFromExternalReference?: boolean;
  [key: string]: unknown;
}

export interface RiskSummary {
  highRisk?: number | boolean;
  currentUnsafe?: number | boolean;
  candidateUnsafe?: number | boolean;
  candidateConflict?: number | boolean;
  hasNonRoundTrip?: number | boolean;
  [key: string]: unknown;
}

export interface CandidateRecord {
  groupId: string;
  currentText?: string;
  candidateText?: string;
  candidates?: Candidate[];
  labelText?: string;
  labelAction?: LabelAction;
  context?: CandidateContext;
  risk?: RiskSummary;
  problemGate?: {
    hasProblem?: boolean;
    reason?: string;
    [key: string]: unknown;
  };
  [key: string]: unknown;
}

export interface ReviewedRecord {
  schema?: string;
  groupId?: string;
  currentText?: string;
  labelAction?: LabelAction;
  labelText?: string;
  selectedCandidateIndex?: number;
  candidates?: Candidate[];
  reviewer?: string;
  reviewedUtc?: string;
  origin?: string;
  originDetail?: string;
  context?: CandidateContext;
  [key: string]: unknown;
}

export interface ReviewCluster {
  id: string;
  ruleVersion?: string;
  sourcePatternLabel?: string;
  currentText?: string;
  candidateText?: string;
  candidateSource?: string;
  encodingPath?: string;
  recommendedAction?: LabelAction;
  recommendedCandidateIndex?: number;
  reviewStatus?: string;
  groupType?: string;
  impactCount?: number;
  count?: number;
  unreviewedCount?: number;
  reviewedCount?: number;
  reviewRequiredCount?: number;
  humanReviewRequired?: boolean;
  canConfirm?: boolean;
  batchMode?: string;
  riskSignalCount?: number;
  risk?: RiskSummary;
  riskSummary?: RiskSummary;
  context?: CandidateContext;
  contextSummary?: CandidateContext;
  sourceTextVariants?: Array<{ text: string }>;
  recordIds?: string[];
  representativeRecords?: CandidateRecord[];
  sampleRecords?: CandidateRecord[];
  alreadyReviewedRecords?: Array<{ groupId: string }>;
  [key: string]: unknown;
}

export interface ReviewClustersPayload {
  ok?: boolean;
  groups?: ReviewCluster[];
  clusters?: ReviewCluster[];
  summary?: Record<string, unknown>;
}

export interface DataPayload {
  packageId?: string;
  manifest?: ManifestPayload;
  records?: CandidateRecord[];
  reviewed?: Record<string, ReviewedRecord>;
  trainingDataset?: TrainingDatasetPayload;
  summary?: Record<string, unknown>;
  paths?: Record<string, string>;
}

export interface PackageSummary {
  id: string;
  path?: string;
  drawing?: DrawingSummary;
  active?: boolean;
  reviewed?: number;
  trainingDataset?: number;
  records?: number;
  [key: string]: unknown;
}

export interface FeatureStatus {
  exists?: boolean;
  stale?: boolean;
  rows?: number;
  groups?: number;
  positiveRows?: number;
  featureColumns?: number;
  pendingReviewedRows?: number;
  trainingDatasetRows?: number;
  modifiedUtc?: string;
  path?: string;
  trainingDatasetPath?: string;
  staleReasons?: string[];
  labelActions?: Record<string, number>;
  [key: string]: unknown;
}

export interface TrainingStatus {
  status?: 'idle' | 'running' | 'succeeded' | 'failed' | string;
  statusLabel?: string;
  lines?: string[];
  logPath?: string;
  exitCode?: number;
  startedUtc?: string;
  finishedUtc?: string;
  [key: string]: unknown;
}

export interface ReportPayload {
  exists?: boolean;
  modelDir?: string;
  modelVersion?: string;
  modelCreatedUtc?: string;
  onnxPath?: string;
  manifestPath?: string;
  testReportPath?: string;
  trainingResult?: {
    status?: string;
    label?: string;
    detail?: string;
    [key: string]: unknown;
  };
  manifest?: Record<string, unknown>;
  testReport?: {
    summary?: Record<string, number>;
    details?: unknown[];
    [key: string]: unknown;
  };
  summary?: Record<string, unknown>;
  releaseCommand?: string;
  [key: string]: unknown;
}

export interface TrainingDatasetRecord {
  groupId?: string;
  currentText?: string;
  labelText?: string;
  candidateText?: string;
  labelAction?: LabelAction;
  layer?: string;
  font?: string;
  bigFont?: string;
  textStyleName?: string;
  drawingFileName?: string;
  drawingPath?: string;
  handle?: string;
  ownerBlockName?: string;
  enteredTrainingUtc?: string;
  trainingSource?: string;
  trainingFeatureBuildId?: string;
  featureRows?: number;
  isFromExternalReference?: boolean;
  [key: string]: unknown;
}

export interface TrainingDatasetPayload {
  ok?: boolean;
  schema?: string;
  path?: string;
  featurePath?: string;
  records?: TrainingDatasetRecord[];
  summary?: {
    total?: number;
    featureRows?: number;
    labelActions?: Record<string, number>;
    [key: string]: unknown;
  };
}

export interface BootstrapPayload {
  ok?: boolean;
  data?: DataPayload;
  features?: FeatureStatus;
  training?: TrainingStatus;
  report?: ReportPayload;
  packages?: PackageSummary[];
  packageId?: string;
}

export interface ReviewEdit {
  labelAction?: LabelAction;
  labelText?: string;
  candidateKey?: string;
  candidateIndex?: number | null;
  candidateMode?: string;
  fixMode?: string;
}

export interface ConfirmReviewRow {
  reviewGroupId: string;
  representativeGroupId?: string;
  labelAction: LabelAction;
  labelText: string;
  candidateIndex?: number | null;
}

export interface ConfirmReviewRowsPayload {
  rows: ConfirmReviewRow[];
  reviewer: string;
  note?: string;
  overwriteReviewed?: boolean;
}

export interface ConfirmReviewRowsResult {
  ok?: boolean;
  reviewedGroups?: number;
  written?: number;
  errors?: string[];
  [key: string]: unknown;
}

export interface BuildFeaturesResult {
  ok?: boolean;
  promoted?: number;
  trainingRecords?: number;
  data?: DataPayload;
  features?: FeatureStatus;
  reviewClusters?: ReviewClustersPayload;
  [key: string]: unknown;
}

export interface StartTrainingResult {
  ok?: boolean;
  autoBuiltFeatures?: boolean;
  features?: FeatureStatus;
  training?: TrainingStatus;
  selectedPackages?: string[];
  selectedPackageCount?: number;
  trainingRecords?: number;
  featurePath?: string;
  [key: string]: unknown;
}

export interface DeleteTrainingRecordsResult {
  ok?: boolean;
  removed?: number;
  data?: DataPayload;
  features?: FeatureStatus;
  reviewClusters?: ReviewClustersPayload;
  [key: string]: unknown;
}
