import { useEffect } from 'react';
import NavBar from '@/components/NavBar';
import StatusBar from '@/components/StatusBar';
import { lastSavedAt, PAGE_STATUS, toApiTab, toBoltTab } from '@/lib/boltAdapters';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';
import AnnotationPage from '@/pages/AnnotationPage';
import ModelReportPage from '@/pages/ModelReportPage';
import ModelTrainingPage from '@/pages/ModelTrainingPage';
import TrainingDatasetPage from '@/pages/TrainingDatasetPage';

export function App() {
  const { activeTab, setActiveTab, refresh, refreshTrainingStatus, message, error, busy, app } = useWorkbenchStore();
  const tab = toBoltTab(activeTab);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    const trainingStatus = app?.training?.status;
    const simulationStatus = app?.report?.simulation?.status;
    if (tab !== 'training' && tab !== 'report' && trainingStatus !== 'running' && simulationStatus !== 'running') return;
    void refreshTrainingStatus();
    const timer = window.setInterval(
      () => { void refreshTrainingStatus(); },
      trainingStatus === 'running' || simulationStatus === 'running' ? 1500 : 4000
    );
    return () => window.clearInterval(timer);
  }, [app?.report?.simulation?.status, app?.training?.status, refreshTrainingStatus, tab]);

  const statusText = busy ? '处理中...' : error || message || PAGE_STATUS[tab];

  return (
    <div className="flex h-screen flex-col overflow-hidden bg-gray-100">
      <NavBar activeTab={tab} onTabChange={(nextTab) => setActiveTab(toApiTab(nextTab))} />

      <main className="overflow-hidden" style={{ marginTop: '44px', marginBottom: '28px', height: 'calc(100vh - 72px)' }}>
        <div className="h-full px-4 py-3">
          {tab === 'annotation' && <AnnotationPage />}
          {tab === 'dataset' && <TrainingDatasetPage />}
          {tab === 'training' && <ModelTrainingPage />}
          {tab === 'report' && <ModelReportPage />}
        </div>
      </main>

      <StatusBar message={statusText} lastSaved={lastSavedAt(app)} />
    </div>
  );
}
