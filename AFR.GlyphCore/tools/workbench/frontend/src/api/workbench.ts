import { get, post } from '@/api/client';
import type {
  BootstrapPayload,
  BuildFeaturesResult,
  ConfirmReviewRowsPayload,
  ConfirmReviewRowsResult,
  DataPayload,
  DeletePackageResult,
  DeleteTrainingRecordsResult,
  FeatureStatus,
  ImportTrainingDatasetResult,
  ReportPayload,
  ResetModelResult,
  ResetReviewRowsResult,
  ReviewClustersPayload,
  StartSimulationTestResult,
  StartTrainingResult,
  TrainingCancelResult,
  TrainingDatasetPayload,
  TrainingOptions,
  TrainingStatus
} from '@/types/api';

export function getBootstrap() {
  return get<BootstrapPayload>('/api/bootstrap');
}

export function getData() {
  return get<DataPayload>('/api/data');
}

export function getReviewClusters() {
  return get<ReviewClustersPayload>('/api/review-clusters');
}

export function selectPackage(packageId: string) {
  return post<{ ok: true; bootstrap: BootstrapPayload }>('/api/package', {
    package: packageId
  });
}

export function deletePackage(packageId: string) {
  return post<DeletePackageResult>('/api/package/delete', {
    packageId
  });
}

export function confirmReviewRows(payload: ConfirmReviewRowsPayload) {
  return post<ConfirmReviewRowsResult>('/api/review-table/confirm', payload);
}

export function resetReviewRows(reviewGroupIds: string[]) {
  return post<ResetReviewRowsResult>('/api/review-table/reset', {
    reviewGroupIds
  });
}

export function getTrainingDataset() {
  return get<TrainingDatasetPayload>('/api/training-dataset');
}

export function deleteTrainingRecords(groupIds: string[]) {
  return post<DeleteTrainingRecordsResult>('/api/training-dataset/delete', {
    groupIds
  });
}

export function getFeatureStatus() {
  return get<FeatureStatus>('/api/features');
}

export function buildFeatures() {
  return post<BuildFeaturesResult>('/api/features', {});
}

export function getTrainingStatus() {
  return get<TrainingStatus>('/api/train');
}

export function startTraining(packageIds?: string[], trainingOptions?: TrainingOptions) {
  return post<StartTrainingResult>('/api/train', {
    packageIds: packageIds || [],
    trainingOptions: trainingOptions || {}
  });
}

export function cancelTraining() {
  return post<TrainingCancelResult>('/api/train/cancel', {});
}

export function startSimulationTest() {
  return post<StartSimulationTestResult>('/api/simulate-test', {});
}

export function getReport() {
  return get<ReportPayload>('/api/report');
}

export function importTrainingDataset(content: string, mode: 'merge' | 'replace' = 'merge') {
  return post<ImportTrainingDatasetResult>('/api/training-dataset/import', {
    format: 'jsonl',
    mode,
    content
  });
}

export function resetModel() {
  return post<ResetModelResult>('/api/model/reset', {});
}
