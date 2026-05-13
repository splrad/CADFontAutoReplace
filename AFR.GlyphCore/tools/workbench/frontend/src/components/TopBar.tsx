import {
  BarChart3,
  BrainCircuit,
  ClipboardList,
  Database,
  FolderOpen,
  Layers3,
  WandSparkles
} from 'lucide-react';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { TABS, type TabId, cn } from '@/lib/utils';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';

const tabIcons: Record<TabId, typeof FolderOpen> = {
  packages: FolderOpen,
  review: ClipboardList,
  dataset: Database,
  features: WandSparkles,
  training: BrainCircuit,
  report: BarChart3
};

function Stat({ label, value, tone }: { label: string; value: number; tone?: string }) {
  return (
    <div className="min-w-20 rounded-[var(--radius-md)] border border-[var(--color-line)] bg-[var(--color-surface-2)] px-3 py-2">
      <div className="text-caption text-[var(--color-text-muted)]">{label}</div>
      <div className={cn('text-body-lg font-semibold', tone || 'text-[var(--color-text)]')}>
        {value}
      </div>
    </div>
  );
}

export function TopBar() {
  const { app, groups, activeTab, setActiveTab } = useWorkbenchStore();

  const data = app?.data || {};
  const summary = data.summary || {};
  const groupSummary = groups.summary || {};
  const drawing = data.manifest?.drawing || {};

  const total = Number(summary.total || groupSummary.records || 0);
  const reviewedCount = Number(summary.reviewed || 0);
  const remaining = Math.max(0, total - reviewedCount);
  const trainedCount = Number(
    summary.trained || data.trainingDataset?.summary?.total || 0
  );
  const packagePath =
    data.paths?.package || data.packageId || app?.packageId || '未选择数据包';
  const drawingName = drawing.fileName || 'DWG 待载入';
  const trainingStatus =
    app?.report?.trainingResult?.label ||
    app?.training?.statusLabel ||
    trainingStatusLabel(app?.training?.status);

  return (
    <header className="shrink-0 border-b border-[var(--color-line)] bg-[var(--color-canvas)]">
      <div className="flex min-h-16 items-center gap-3 px-4 py-2 max-[760px]:flex-col max-[760px]:items-stretch">
        <div className="flex min-w-0 w-[440px] max-w-[34vw] items-center gap-3 max-[760px]:w-full max-[760px]:max-w-none">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-[var(--radius-md)] bg-[var(--color-primary)] text-[var(--color-on-primary)] shadow-[var(--shadow-sm)]">
            <Layers3 className="h-5 w-5" aria-hidden />
          </div>
          <div className="min-w-0">
            <div className="text-eyebrow text-[var(--color-text-muted)]">AFR GLYPHCORE</div>
            <div className="truncate text-body-lg font-semibold text-[var(--color-text)]">
              AFR 文枢训练工作台
            </div>
            <div className="truncate text-caption text-[var(--color-text-muted)]">
              {drawingName} / {packagePath}
            </div>
          </div>
        </div>

        <Tabs
          value={activeTab}
          onValueChange={(value) => setActiveTab(value as TabId)}
          className="mx-2 min-w-0 flex-1 max-[760px]:mx-0 max-[760px]:w-full"
        >
          <TabsList
            className="flex min-w-0 overflow-x-auto scrollbar-thin max-[760px]:w-full"
            aria-label="工作台导航"
          >
            {TABS.map(([id, label]) => {
              const Icon = tabIcons[id];
              return (
                <TabsTrigger
                  key={id}
                  value={id}
                  className="shrink-0"
              >
                  <Icon className="h-4 w-4" aria-hidden />
                  {label}
                </TabsTrigger>
              );
            })}
          </TabsList>
        </Tabs>

        <div className="hidden shrink-0 items-center gap-2 xl:flex">
          <Stat label="待复核" value={remaining} tone="text-[var(--color-warn)]" />
          <Stat label="待入训练" value={reviewedCount} tone="text-[var(--color-ai)]" />
          <Stat label="训练集" value={trainedCount} tone="text-[var(--color-keep)]" />
          <div className="min-w-24 rounded-[var(--radius-md)] border border-[var(--color-line)] bg-[var(--color-surface-2)] px-3 py-2">
            <div className="text-caption text-[var(--color-text-muted)]">训练状态</div>
            <div className="truncate text-body-sm font-semibold text-[var(--color-text)]">
              {trainingStatus}
            </div>
          </div>
        </div>
      </div>
    </header>
  );
}

function trainingStatusLabel(status: string | undefined) {
  const labels: Record<string, string> = {
    idle: '未开始训练',
    running: '训练中',
    succeeded: '训练完成',
    failed: '训练失败'
  };
  return labels[status || 'idle'] || status || '未知状态';
}
