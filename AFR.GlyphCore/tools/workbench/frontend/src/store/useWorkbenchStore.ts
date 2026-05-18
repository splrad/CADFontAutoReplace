/**
 * AFR 文枢工作台全局状态管理 (Zustand)
 * 
 * 整合原 `App` 组件中的所有 useState 和核心 action。
 */

import { create } from 'zustand';
import {
  buildFeatures as buildFeaturesRequest,
  cancelTraining as cancelTrainingRequest,
  confirmReviewRows,
  deletePackage as deletePackageRequest,
  deleteTrainingRecords as deleteTrainingRecordsRequest,
  getBootstrap,
  getFeatureStatus,
  getReviewClusters,
  getReport,
  importTrainingDataset as importTrainingDatasetRequest,
  resetModel as resetModelRequest,
  resetReviewRows as resetReviewRowsRequest,
  getTrainingStatus,
  selectPackage as selectPackageRequest,
  startSimulationTest as startSimulationTestRequest,
  startTraining as startTrainingRequest
} from '@/api/workbench';
import {
  clusterList,
  compactText,
  reviewedForGroup,
  type ReviewGroup,
  type TabId
} from '@/lib/utils';
import type {
  BootstrapPayload,
  ConfirmReviewRow,
  ReviewClustersPayload,
  ReviewEdit as ApiReviewEdit,
  TrainingDatasetRecord,
  TrainingOptions
} from '@/types/api';

// ========== 状态类型 ==========

export interface WorkbenchState {
  // 核心数据
  app: App | null;
  groups: GroupsPayload;

  // 筛选与导航
  activeTab: TabId;
  query: string;
  filter: 'all' | 'pending' | 'reviewed';
  riskFilter: 'all' | 'high' | 'medium' | 'low';
  layerFilter: string;
  fontFilter: string;
  encodingFilter: string;

  // 复核选择与编辑
  selectedReviewGroupIds: string[];
  reviewEdits: Record<string, ReviewEdit>;

  // UI 反馈
  message: string;
  error: string;
  busy: boolean;
  batchProgress: string;

  // Actions
  setActiveTab: (tab: TabId) => void;
  setQuery: (query: string) => void;
  setFilter: (filter: 'all' | 'pending' | 'reviewed') => void;
  setRiskFilter: (filter: 'all' | 'high' | 'medium' | 'low') => void;
  setLayerFilter: (layer: string) => void;
  setFontFilter: (font: string) => void;
  setEncodingFilter: (encoding: string) => void;
  clearReviewFilters: () => void;

  toggleReviewSelection: (groupId: string) => void;
  updateReviewEdit: (groupId: string, patch: Partial<ReviewEdit>) => void;

  setMessage: (message: string) => void;
  setError: (error: string) => void;
  showError: (err: any) => void;

  refresh: () => Promise<void>;
  refreshGroupsOnly: () => Promise<GroupsPayload>;
  refreshTrainingStatus: () => Promise<void>;
  selectPackage: (id: string) => Promise<void>;
  deletePackage: (id: string) => Promise<void>;
  buildFeatures: () => Promise<void>;
  startTraining: (packageIds?: string[], trainingOptions?: TrainingOptions) => Promise<void>;
  cancelTraining: () => Promise<void>;
  startSimulationTest: () => Promise<void>;
  resetModel: () => Promise<void>;
  deleteTrainingRecord: (record: TrainingRecord) => Promise<void>;
  deleteTrainingRecords: (groupIds: string[]) => Promise<void>;
  resetReviewRows: (reviewGroupIds: string[]) => Promise<void>;
  importTrainingDataset: (content: string) => Promise<void>;
  saveSelectedReviews: (targetGroupIds?: string[]) => Promise<void>;
  saveAllVisibleReviews: (selectableReviewGroups: ReviewGroup[]) => Promise<void>;
}

// ========== 辅助类型 ==========

export type App = BootstrapPayload;
export type GroupsPayload = ReviewClustersPayload;
export type ReviewEdit = ApiReviewEdit;
export type TrainingRecord = TrainingDatasetRecord;

// ========== Store 实现 ==========

export const useWorkbenchStore = create<WorkbenchState>((set, get) => ({
  // 初始状态
  app: null,
  groups: { groups: [], summary: {} },
  activeTab: (() => {
    const id = window.location.hash.replace(/^#/, '');
    const validTabs: TabId[] = ['review', 'dataset', 'training', 'report'];
    return validTabs.includes(id as TabId) ? (id as TabId) : 'review';
  })(),
  query: '',
  filter: 'pending',
  riskFilter: 'all',
  layerFilter: 'all',
  fontFilter: 'all',
  encodingFilter: 'all',
  selectedReviewGroupIds: [],
  reviewEdits: {},
  message: '',
  error: '',
  busy: false,
  batchProgress: '',

  // 筛选与导航 Actions
  setActiveTab: (tab) => {
    set({ activeTab: tab });
    window.location.hash = tab;
  },
  setQuery: (query) => set({ query }),
  setFilter: (filter) => set({ filter }),
  setRiskFilter: (riskFilter) => set({ riskFilter }),
  setLayerFilter: (layerFilter) => set({ layerFilter }),
  setFontFilter: (fontFilter) => set({ fontFilter }),
  setEncodingFilter: (encodingFilter) => set({ encodingFilter }),
  clearReviewFilters: () =>
    set({
      query: '',
      filter: 'all',
      riskFilter: 'all',
      layerFilter: 'all',
      fontFilter: 'all',
      encodingFilter: 'all'
    }),

  // 复核选择与编辑 Actions
  toggleReviewSelection: (groupId) => {
    if (!groupId) return;
    set((state) => ({
      selectedReviewGroupIds: state.selectedReviewGroupIds.includes(groupId)
        ? state.selectedReviewGroupIds.filter((id) => id !== groupId)
        : [...state.selectedReviewGroupIds, groupId]
    }));
  },

  updateReviewEdit: (groupId, patch) => {
    if (!groupId) return;
    set((state) => {
      const updatedEdits = {
        ...state.reviewEdits,
        [groupId]: { ...(state.reviewEdits[groupId] || {}), ...patch }
      };
      const updatedSelection = state.selectedReviewGroupIds.includes(groupId)
        ? state.selectedReviewGroupIds
        : [...state.selectedReviewGroupIds, groupId];
      return {
        reviewEdits: updatedEdits,
        selectedReviewGroupIds: updatedSelection
      };
    });
  },

  // UI 反馈 Actions
  setMessage: (message) => set({ message }),
  setError: (error) => set({ error }),
  showError: (err) => set({ error: err.message || String(err) }),

  // 数据加载 Actions
  refresh: async () => {
    const bootstrap = await getBootstrap();
    const reviewGroups = await getReviewClusters();
    set({ app: bootstrap, groups: reviewGroups });
  },

  refreshGroupsOnly: async () => {
    try {
      const reviewGroups = await getReviewClusters();
      set({ groups: reviewGroups });
      return reviewGroups;
    } catch (err: any) {
      get().showError(err);
      return get().groups;
    }
  },

  selectPackage: async (id) => {
    set({ busy: true });
    try {
      const result = await selectPackageRequest(id);
      const reviewGroups = await getReviewClusters();
      set({ app: result.bootstrap, groups: reviewGroups });
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  deletePackage: async (id) => {
    if (!id || get().busy) return;
    set({ busy: true, error: '', message: '正在删除本地数据包...' });
    try {
      const result = await deletePackageRequest(id);
      const bootstrap = result.bootstrap || await getBootstrap();
      let reviewGroups: GroupsPayload = { groups: [], summary: {} };
      if (bootstrap.data?.packageId) {
        try {
          reviewGroups = await getReviewClusters();
        } catch {
          reviewGroups = { groups: [], summary: {} };
        }
      }
      set({
        app: bootstrap,
        groups: reviewGroups,
        selectedReviewGroupIds: [],
        reviewEdits: {},
        activeTab: 'review',
        message: `已删除本地数据包 ${id}`
      });
      window.location.hash = 'review';
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  buildFeatures: async () => {
    set({ busy: true, error: '', message: '正在重建 Feature...' });
    try {
      const result = await buildFeaturesRequest();
      set((state) => ({
        app: {
          ...state.app,
          data: result.data || state.app?.data,
          features: result.features
        },
        groups: result.reviewClusters || { groups: [], summary: {} }
      }));
      const promoted = Number(result.promoted || 0);
      const trainingRecords = Number(
        result.trainingRecords || result.features?.trainingDatasetRows || 0
      );
      set({
        message:
          promoted > 0
            ? `已写入训练集 ${promoted} 条，Feature 已刷新`
            : `没有新增训练数据，已重建 ${trainingRecords} 条训练记录的 Feature`
      });
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  startTraining: async (packageIds, trainingOptions) => {
    set({ busy: true, error: '', message: '正在启动模型训练...' });
    try {
      const result = await startTrainingRequest(packageIds, trainingOptions);
      const [bootstrap, reviewGroups] = await Promise.all([
        getBootstrap(),
        getReviewClusters()
      ]);
      set((state) => ({
        app: {
          ...bootstrap,
          features: result.features || bootstrap.features || state.app?.features,
          training: result.training || bootstrap.training
        },
        groups: reviewGroups,
        activeTab: 'training',
        message: result.autoBuiltFeatures
          ? `已用 ${result.selectedPackageCount || packageIds?.length || 1} 个数据包刷新 Feature 并开始训练`
          : '已开始训练'
      }));
      window.location.hash = 'training';
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  cancelTraining: async () => {
    if (get().busy) return;
    set({ busy: true, error: '', message: '正在取消训练...' });
    try {
      const result = await cancelTrainingRequest();
      set((state) => ({
        app: {
          ...state.app,
          training: result.training || state.app?.training
        },
        message: '训练任务已取消'
      }));
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  startSimulationTest: async () => {
    set({ busy: true, error: '', message: '正在启动全量模拟测试...' });
    try {
      const result = await startSimulationTestRequest();
      const report = result.report || await getReport();
      set((state) => ({
        app: {
          ...state.app,
          report: {
            ...report,
            simulation: result.simulation || report.simulation
          }
        },
        activeTab: 'report',
        message: '已开始全量模拟测试'
      }));
      window.location.hash = 'report';
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  resetModel: async () => {
    if (get().busy) return;
    set({ busy: true, error: '', message: '正在归档当前模型...' });
    try {
      const result = await resetModelRequest();
      set((state) => ({
        app: {
          ...state.app,
          training: result.training || state.app?.training,
          report: result.report || state.app?.report
        },
        message: `模型输出已归档 ${Number(result.reset || 0)} 项`
      }));
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  refreshTrainingStatus: async () => {
    try {
      const [training, features, report] = await Promise.all([
        getTrainingStatus(),
        getFeatureStatus(),
        getReport()
      ]);
      set((state) => ({
        app: {
          ...state.app,
          training,
          features,
          report
        }
      }));
    } catch (err: any) {
      get().showError(err);
    }
  },

  deleteTrainingRecord: async (record) => {
    const groupId = record?.groupId;
    if (!groupId || get().busy) return;
    const text = compactText(
      record.labelText || record.currentText || groupId,
      36
    );
    if (
      !window.confirm(
        `从训练数据集中删除"${text}"，并回流到复核队列？`
      )
    )
      return;
    set({ busy: true, error: '' });
    try {
      const result = await deleteTrainingRecordsRequest([groupId]);
      set((state) => ({
        app: {
          ...state.app,
          data: result.data || state.app?.data,
          features: result.features || state.app?.features
        },
        groups: result.reviewClusters || { groups: [], summary: {} },
        message: `已删除 ${result.removed || 0} 条训练数据，并回流到复核队列`
      }));
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  deleteTrainingRecords: async (groupIds) => {
    const ids = groupIds.filter(Boolean);
    if (ids.length === 0 || get().busy) return;
    set({ busy: true, error: '', message: `正在删除 ${ids.length} 条训练数据...` });
    try {
      const result = await deleteTrainingRecordsRequest(ids);
      set((state) => ({
        app: {
          ...state.app,
          data: result.data || state.app?.data,
          features: result.features || state.app?.features
        },
        groups: result.reviewClusters || { groups: [], summary: {} },
        message: `已删除 ${result.removed || 0} 条训练数据，并回流到复核队列`
      }));
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  resetReviewRows: async (reviewGroupIds) => {
    const ids = reviewGroupIds.filter(Boolean);
    if (ids.length === 0 || get().busy) return;
    set({ busy: true, error: '', message: `正在重置 ${ids.length} 个复核项...` });
    try {
      const result = await resetReviewRowsRequest(ids);
      const resetIds = new Set(ids);
      set((state) => ({
        app: {
          ...state.app,
          data: result.data || state.app?.data,
          features: result.features || state.app?.features
        },
        groups: result.reviewClusters || state.groups,
        selectedReviewGroupIds: state.selectedReviewGroupIds.filter((id) => !resetIds.has(id)),
        reviewEdits: Object.fromEntries(
          Object.entries(state.reviewEdits).filter(([id]) => !resetIds.has(id))
        ),
        message: `已重置 ${result.reset || 0} 条复核/训练记录`
      }));
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  importTrainingDataset: async (content) => {
    if (!content.trim() || get().busy) return;
    set({ busy: true, error: '', message: '正在导入训练数据 JSONL...' });
    try {
      const result = await importTrainingDatasetRequest(content);
      if (result.ok === false) {
        const first = result.errors?.[0];
        set({
          error: first ? `导入失败：第 ${first.line || '?'} 行 ${first.error}` : '导入失败：JSONL 校验未通过'
        });
        return;
      }
      set((state) => ({
        app: {
          ...state.app,
          data: result.data || state.app?.data,
          features: result.features || state.app?.features
        },
        groups: result.reviewClusters || state.groups,
        message: `已导入 ${result.imported || 0} 条训练数据`
      }));
    } catch (err: any) {
      get().showError(err);
    } finally {
      set({ busy: false });
    }
  },

  saveSelectedReviews: async (targetGroupIds) => {
    const state = get();
    const reviewer = 'developer';
    const targets = targetGroupIds ?? state.selectedReviewGroupIds.filter((id) => {
      // 这里需要 filteredGroups，暂时简化处理
      return clusterList(state.groups).some((group) => group.id === id);
    });

    if (targets.length === 0 || state.busy) {
      set({
        message: '请先在表格中勾选需要保存的行',
        batchProgress: '没有可保存的勾选行：请勾选右侧保存框，或先修改任意一行后再保存。'
      });
      return;
    }

    set({
      busy: true,
      error: '',
      message: `正在批量保存 ${targets.length} 个复核结果...`,
      batchProgress: `正在批量保存 ${targets.length} 个复核结果...`
    });

    try {
      const groupById = new Map(
        clusterList(state.groups).map((group) => [group.id, group])
      );
      const reviewed = state.app?.data?.reviewed || {};
      const rows = targets.reduce<ConfirmReviewRow[]>((acc, id) => {
          const group: ReviewGroup | undefined = groupById.get(id);
          if (!group) return acc;
          const edit = state.reviewEdits[id] || {};
          const saved = reviewedForGroup(group, reviewed);
          acc.push({
            reviewGroupId: id,
            representativeGroupId:
              group.representativeRecords?.[0]?.groupId ||
              group.sampleRecords?.[0]?.groupId ||
              group.recordIds?.[0],
            labelAction:
              edit.labelAction ||
              saved?.labelAction ||
              group.recommendedAction ||
              'repair',
            labelText:
              edit.labelText ??
              saved?.labelText ??
              group.candidateText ??
              group.currentText ??
              '',
            candidateIndex:
              edit.candidateIndex ??
              saved?.selectedCandidateIndex ??
              group.recommendedCandidateIndex ??
              0
          });
          return acc;
        }, []);

      if (rows.length === 0) {
        set({
          message: '当前勾选行缺少可保存的文本簇信息',
          batchProgress: '保存已取消：当前筛选下没有可提交的复核行。'
        });
        return;
      }

      const result = await confirmReviewRows({
        rows,
        reviewer,
        note: 'manual-table-review',
        overwriteReviewed: true
      });

      const [bootstrap, reviewGroups] = await Promise.all([
        getBootstrap(),
        getReviewClusters()
      ]);

      set((prevState) => {
        const targetSet = new Set(targets);
        return {
          app: bootstrap,
          groups: reviewGroups,
          selectedReviewGroupIds: prevState.selectedReviewGroupIds.filter(
            (id) => !targetSet.has(id)
          ),
          reviewEdits: Object.fromEntries(
            Object.entries(prevState.reviewEdits).filter(
              ([id]) => !targetSet.has(id)
            )
          ),
          message: `已批量保存 ${result.reviewedGroups} 行，写入/更新 ${result.written} 条训练数据`,
          batchProgress: result.errors?.length
            ? `已完成，另有 ${result.errors.length} 行未保存`
            : '批量保存完成'
        };
      });
    } catch (err: any) {
      get().showError(err);
      set({ batchProgress: err.message || String(err) });
    } finally {
      set({ busy: false });
    }
  },

  saveAllVisibleReviews: async (selectableReviewGroups) => {
    const ids = selectableReviewGroups.map((group) => group.id);
    return get().saveSelectedReviews(ids);
  }
}));
