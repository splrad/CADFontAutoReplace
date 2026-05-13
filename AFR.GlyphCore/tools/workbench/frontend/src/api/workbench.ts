import { get, post } from '@/api/client';
import type {
  BootstrapPayload,
  BuildFeaturesResult,
  ConfirmReviewRowsPayload,
  ConfirmReviewRowsResult,
  DataPayload,
  DeleteTrainingRecordsResult,
  FeatureStatus,
  ReportPayload,
  ReviewClustersPayload,
  StartSimulationTestResult,
  StartTrainingResult,
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

export function confirmReviewRows(payload: ConfirmReviewRowsPayload) {
  return post<ConfirmReviewRowsResult>('/api/review-table/confirm', payload);
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

export function startSimulationTest() {
  return post<StartSimulationTestResult>('/api/simulate-test', {});
}

export function getReport() {
  return get<ReportPayload>('/api/report');
}
