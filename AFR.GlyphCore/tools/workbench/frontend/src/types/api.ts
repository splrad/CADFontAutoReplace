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
  rawCurrentText?: string;
  displayText?: string;
  candidateText?: string;
  rawCandidateText?: string;
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
  rawCurrentText?: string;
  displayText?: string;
  labelAction?: LabelAction;
  labelText?: string;
  rawLabelText?: string;
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
  rawCurrentText?: string;
  displayText?: string;
  candidateText?: string;
  rawCandidateText?: string;
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

export interface SimulationStatus {
  status?: 'idle' | 'pending' | 'running' | 'succeeded' | 'completed' | 'failed' | string;
  statusLabel?: string;
  label?: string;
  strategy?: string;
  seed?: number;
  minimumGroups?: number;
  sampleGroups?: number;
  sampledGroups?: number;
  availableGroups?: number;
  sampledFeatureRows?: number;
  mode?: string;
  lines?: string[];
  logPath?: string;
  returnCode?: number;
  [key: string]: unknown;
}

export interface TrainingOptions {
  maxRounds?: number;
  earlyStoppingRounds?: number;
  seed?: number;
  autoEarlyStopping?: boolean;
  bestIteration?: number;
  actualIterations?: number;
  stoppedEarly?: boolean;
  simulationMode?: string;
}

export interface ValidationSummary {
  groups?: number;
  expectedRepairs?: number;
  correctRepairs?: number;
  missedRepairs?: number;
  falseRepairs?: number;
  skipped?: number;
  correctDecisions?: number;
  repairRecall?: number;
  falseRepairRate?: number;
  decisionAccuracy?: number;
  [key: string]: number | undefined;
}

export interface ValidationDetail {
  groupId?: string;
  packageId?: string;
  exportId?: string;
  drawingFileName?: string;
  labelAction?: string;
  decision?: string;
  currentText?: string;
  bestText?: string;
  labelText?: string;
  score?: number;
  margin?: number;
  source?: string;
  severity?: string;
  [key: string]: unknown;
}

export interface ValidationReport {
  split?: string;
  summary?: ValidationSummary;
  details?: ValidationDetail[];
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
  testReport?: ValidationReport;
  blindReport?: ValidationReport;
  trainReport?: ValidationReport;
  validReport?: ValidationReport;
  summary?: Record<string, unknown>;
  trainingConfig?: TrainingOptions;
  splitSummary?: Record<string, unknown>;
  acceptance?: {
    status?: string;
    label?: string;
    canPublish?: boolean;
    blockers?: string[];
    [key: string]: unknown;
  };
  overfitting?: {
    status?: string;
    severe?: boolean;
    warning?: boolean;
    reasons?: string[];
    recallGap?: number;
    accuracyGap?: number;
    [key: string]: unknown;
  };
  simulation?: SimulationStatus;
  errorSamples?: ValidationDetail[];
  history?: Record<string, unknown>[];
  releaseCommand?: string;
  [key: string]: unknown;
}

export interface TrainingDatasetRecord {
  groupId?: string;
  currentText?: string;
  rawCurrentText?: string;
  displayText?: string;
  labelText?: string;
  rawLabelText?: string;
  candidateText?: string;
  rawCandidateText?: string;
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
  trainingConfig?: TrainingOptions;
  selectedPackages?: string[];
  selectedPackageCount?: number;
  trainingRecords?: number;
  featurePath?: string;
  [key: string]: unknown;
}

export interface StartSimulationTestResult {
  ok?: boolean;
  simulation?: SimulationStatus;
  report?: ReportPayload;
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

export interface DeletePackageResult {
  ok?: boolean;
  deletedPackageId?: string;
  deletedPaths?: Array<{ label?: string; path?: string }>;
  bootstrap?: BootstrapPayload;
  [key: string]: unknown;
}

export interface ResetReviewRowsResult {
  ok?: boolean;
  reset?: number;
  recordIds?: string[];
  data?: DataPayload;
  features?: FeatureStatus;
  reviewClusters?: ReviewClustersPayload;
  trainingDataset?: TrainingDatasetPayload;
  [key: string]: unknown;
}

export interface ImportTrainingDatasetResult {
  ok?: boolean;
  imported?: number;
  errors?: Array<{ line?: number; error?: string }>;
  data?: DataPayload;
  features?: FeatureStatus;
  reviewClusters?: ReviewClustersPayload;
  trainingDataset?: TrainingDatasetPayload;
  [key: string]: unknown;
}

export interface TrainingCancelResult {
  ok?: boolean;
  canceled?: boolean;
  training?: TrainingStatus;
  [key: string]: unknown;
}

export interface ResetModelResult {
  ok?: boolean;
  reset?: number;
  trashPath?: string;
  report?: ReportPayload;
  training?: TrainingStatus;
  [key: string]: unknown;
}
