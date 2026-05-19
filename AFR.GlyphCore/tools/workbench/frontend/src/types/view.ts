export type TabId = 'annotation' | 'dataset' | 'training' | 'report';

export type ReviewStatus = 'pending' | 'confirmed';
export type RiskLevel = 'low' | 'medium' | 'high' | 'critical';
export type ActionType = 'fix' | 'keep' | 'delete' | 'manual';
export type CorrectTextMode = 'original' | 'candidate' | 'manual';
export type TrainingStatus = 'idle' | 'running' | 'completed' | 'failed';
export type PackageStatus = 'active' | 'archived' | 'processing';
export type PublishStatus = 'draft' | 'published' | 'deprecated';

export interface DBTextCluster {
  id: string;
  originalText: string;
  rawOriginalText: string;
  candidateTexts: string[];
  selectedCandidate: string;
  action: ActionType;
  status: ReviewStatus;
  riskLevel: RiskLevel;
  layer: string;
  font: string;
  encodingPath: string;
  count: number;
  confidence: number;
  dwgFile: string;
  reviewedAt?: string;
  correctTextMode: CorrectTextMode;
  manualText: string;
}

export interface TrainingRecord {
  id: string;
  action: ActionType;
  originalText: string;
  correctText: string;
  correctTextMode: CorrectTextMode;
  source: string;
  font: string;
  addedAt: string;
  dataPackageId: string;
}

export interface FeatureStats {
  pendingRefresh: number;
  trainingRecords: number;
  featureRows: number;
  featureCols: number;
  freshness: number;
  featurePath: string;
  trainingPath: string;
  lastUpdated: string;
}

export interface TrainingRun {
  id: string;
  version: string;
  status: TrainingStatus;
  startedAt?: string;
  completedAt?: string;
  epochs: number;
  batchSize: number;
  learningRate: number;
  dataPackages: string[];
  logs: string[];
  bestEpoch?: number;
  bestAccuracy?: number;
}

export interface SimulatedTestResult {
  id: string;
  originalText: string;
  aiOutput: string;
  correctText: string;
  matched: boolean;
  dataPackageId: string;
}

export interface ModelReport {
  id: string;
  version: string;
  publishStatus: PublishStatus;
  publishedAt?: string;
  recall: number;
  precision: number;
  overfitScore: number;
  wrongFixes: number;
  bestEpoch: number;
  simulatedTests: SimulatedTestResult[];
}

export interface DataPackage {
  id: string;
  name: string;
  dwgFile: string;
  createdAt: string;
  status: PackageStatus;
  dataCount: number;
  inTrainingSet: number;
  trainingRecords?: number;
}

export interface AppStats {
  pendingReview: number;
  pendingRefresh: number;
  trainingSet: number;
  trainingStatus: TrainingStatus;
}
