/**
 * AFR 文枢工作台根组件
 * 
 * 按 activeTab 分发视图：review / dataset / workflow tabs。
 * 包裹 AppShell 与 ToastProvider。
 */

import { useState, useEffect } from 'react';
import { AppShell } from '@/components/layout/AppShell';
import { ReviewWorkspace } from '@/components/review/ReviewWorkspace';
import { TrainingDatasetPage } from '@/components/dataset/TrainingDatasetPage';
import { WorkflowViews } from '@/components/workflow/WorkflowViews';
import { ToastProvider, type ToastItem } from '@/components/ui/Toast';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';

const WORKFLOW_TABS = new Set(['packages', 'features', 'training', 'report']);

export function App() {
  const { activeTab, app, refresh, refreshTrainingStatus, message, error, busy, deleteTrainingRecord } =
    useWorkbenchStore();

  const [toasts, setToasts] = useState<ToastItem[]>([]);

  // 初始加载
  useEffect(() => {
    refresh();
  }, []);

  useEffect(() => {
    const status = app?.training?.status;
    if (activeTab !== 'training' && status !== 'running') return;
    refreshTrainingStatus();
    const timer = window.setInterval(() => {
      refreshTrainingStatus();
    }, status === 'running' ? 1500 : 4000);
    return () => window.clearInterval(timer);
  }, [activeTab, app?.training?.status, refreshTrainingStatus]);

  // 将 error / message 推入 Toast 队列（最多保留 5 条）
  useEffect(() => {
    if (!error) return;
    setToasts((prev) => [
      ...prev.slice(-4),
      { id: `err-${Date.now()}`, message: error, tone: 'error' as const }
    ]);
  }, [error]);

  useEffect(() => {
    if (!message) return;
    setToasts((prev) => [
      ...prev.slice(-4),
      { id: `msg-${Date.now()}`, message, tone: 'success' as const }
    ]);
  }, [message]);

  function removeToast(id: string) {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }

  const dataset = app?.data?.trainingDataset ?? { records: [], summary: {} };

  function renderContent() {
    if (activeTab === 'review') return <ReviewWorkspace />;
    if (activeTab === 'dataset') {
      return (
        <TrainingDatasetPage
          dataset={dataset}
          busy={busy}
          onDeleteRecord={deleteTrainingRecord}
        />
      );
    }
    if (WORKFLOW_TABS.has(activeTab)) return <WorkflowViews />;
    return null;
  }

  return (
    <ToastProvider toasts={toasts} onClose={removeToast}>
      <AppShell>
        {renderContent()}
      </AppShell>
    </ToastProvider>
  );
}
