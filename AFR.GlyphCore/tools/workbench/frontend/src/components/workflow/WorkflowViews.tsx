import {
  BarChart3,
  BrainCircuit,
  CheckCircle2,
  FolderOpen,
  Play,
  RefreshCcw,
  Sparkles
} from 'lucide-react';
import type { ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { Panel, PanelHeader, Metric } from '@/components/ui/Panel';
import { Progress } from '@/components/ui/progress';
import { formatDateTime, moduleDescription, pct, tabLabel, cn } from '@/lib/utils';
import { useWorkbenchStore, type App } from '@/store/useWorkbenchStore';

function WorkflowShell({
  eyebrow,
  title,
  description,
  icon: Icon,
  actions,
  children
}: {
  eyebrow: string;
  title: string;
  description: string;
  icon: typeof FolderOpen;
  actions?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className="grid h-full min-h-0 min-w-0 grid-rows-[auto_minmax(0,1fr)] gap-4 p-4 max-[640px]:p-3">
      <Panel>
        <PanelHeader
          eyebrow={eyebrow}
          title={
            <span className="inline-flex items-center gap-3">
              <span className="inline-flex h-10 w-10 items-center justify-center rounded-full bg-[var(--color-primary)] text-[var(--color-on-primary)]">
                <Icon className="h-5 w-5" aria-hidden />
              </span>
              {title}
            </span>
          }
          description={description}
          actions={actions}
        />
      </Panel>
      <div className="min-h-0 min-w-0 overflow-auto scrollbar-thin">{children}</div>
    </div>
  );
}

function ProgressBar({ value }: { value: number }) {
  return <Progress value={value} />;
}

function trainingStatusLabel(status: string | undefined, fallback?: string) {
  if (fallback) return fallback;
  const labels: Record<string, string> = {
    idle: '未开始训练',
    running: '训练中',
    succeeded: '训练完成',
    failed: '训练失败'
  };
  return labels[status || 'idle'] || status || '未知状态';
}

function statusTone(status: string | undefined): 'ai' | 'keep' | 'warn' | 'unsafe' {
  if (status === 'running') return 'warn';
  if (status === 'failed') return 'unsafe';
  if (status === 'succeeded') return 'keep';
  return 'ai';
}

export function WorkflowViews() {
  const { app, activeTab, buildFeatures, startTraining, selectPackage, busy } =
    useWorkbenchStore();

  const packages = (app as App | null)?.packages || [];
  const features = app?.features || {};
  const training = app?.training || { status: 'idle', lines: [] };
  const trainingDataset = app?.data?.trainingDataset || { records: [], summary: {} };
  const report = app?.report || {};
  const validation = report.testReport?.summary || {};
  const modelVersion =
    String(report.modelVersion || report.manifest?.modelVersion || report.summary?.modelVersion || '') ||
    '未生成模型';
  const trainingResult = report.trainingResult || {};
  const trainingResultStatus = String(trainingResult.status || training.status || 'idle');
  const trainingResultLabel = trainingStatusLabel(
    trainingResultStatus,
    trainingResult.label || training.statusLabel
  );
  const modelVersionValue = (
    <span className="block break-all text-body-sm leading-snug">{modelVersion}</span>
  );
  const activeLabel = tabLabel(activeTab);
  const pendingFeatureRows = Number(features.pendingReviewedRows || 0);
  const featureRows = Number(features.rows || 0);
  const trainingRecords = Number(features.trainingDatasetRows || trainingDataset.summary?.total || 0);
  const featureButtonLabel =
    pendingFeatureRows > 0 ? '写入训练集 / 刷新 Feature' : '重建 Feature';
  const featureProgress =
    trainingRecords > 0
      ? Math.min(100, Math.round((featureRows / Math.max(1, trainingRecords)) * 100))
      : 0;
  const falseRepairRate = Number(validation.falseRepairRate || 0);
  const recall = Number(validation.repairRecall || 0);

  if (activeTab === 'packages') {
    return (
      <WorkflowShell
        eyebrow="Package Manager"
        title={activeLabel}
        description={moduleDescription(activeTab)}
        icon={FolderOpen}
      >
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {packages.length === 0 && (
            <Panel className="p-6 text-body-sm text-[var(--color-text-soft)]">
              没有找到导出包。先在 AutoCAD Debug 构建中运行 AFRGLYPHCOREEXPORT。
            </Panel>
          )}
          {packages.map((item: any) => (
            <button
              key={item.id}
              type="button"
              onClick={() => selectPackage(item.id)}
              className="rounded-[var(--radius-lg)] border border-[var(--color-line)] bg-[var(--color-canvas)] p-5 text-left shadow-[var(--shadow-sm)] transition-colors hover:border-[var(--color-primary)] hover:bg-[var(--color-surface-2)]"
            >
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="text-eyebrow text-[var(--color-text-muted)]">EXPORT PACKAGE</div>
                  <strong className="mt-1 block truncate text-body-lg text-[var(--color-text)]">
                    {item.id}
                  </strong>
                  <span className="mt-1 block truncate text-body-sm text-[var(--color-text-soft)]">
                    {item.drawing?.fileName || 'DWG'}
                  </span>
                </div>
                <span className="rounded-full bg-[var(--color-ai-soft)] px-3 py-1 text-caption text-[var(--color-ai)]">
                  选择
                </span>
              </div>
              <div className="mt-5 grid grid-cols-1 gap-2 min-[420px]:grid-cols-3">
                <Metric label="Reviewed" value={Number(item.reviewed || 0)} tone="keep" />
                <Metric label="Train" value={Number(item.trainingDataset || 0)} tone="ai" />
                <Metric label="Rows" value={Number(item.records || 0)} tone="repair" />
              </div>
            </button>
          ))}
        </div>
      </WorkflowShell>
    );
  }

  if (activeTab === 'features') {
    return (
      <WorkflowShell
        eyebrow="Feature Build"
        title={activeLabel}
        description={moduleDescription(activeTab)}
        icon={Sparkles}
        actions={
          <Button variant="primary" icon={RefreshCcw} disabled={busy} onClick={buildFeatures}>
            {featureButtonLabel}
          </Button>
        }
      >
        <div className="grid min-w-0 gap-4 xl:grid-cols-[minmax(0,1fr)_360px]">
          <Panel className="p-5">
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <Metric label="待入训练" value={pendingFeatureRows} tone="warn" />
              <Metric label="训练集记录" value={trainingRecords} tone="ai" />
              <Metric label="Feature 行" value={featureRows} tone="repair" />
              <Metric label="特征列" value={Number(features.featureColumns || 0)} tone="keep" />
            </div>
            <div className="mt-6 rounded-[var(--radius-lg)] bg-[var(--color-repair-soft)] p-5">
              <div className="mb-3 flex items-center justify-between gap-3">
                <div>
                  <div className="text-eyebrow text-[var(--color-repair)]">Feature Freshness</div>
                  <div className="text-body-lg font-semibold text-[var(--color-text)]">
                    {features.exists ? features.stale ? '需要刷新' : '已就绪' : '未生成'}
                  </div>
                </div>
                <span className="shrink-0 text-headline text-[var(--color-repair)]">{featureProgress}%</span>
              </div>
              <ProgressBar value={featureProgress} />
              <p className="mt-3 text-body-sm text-[var(--color-text-soft)]">
                最后刷新：{formatDateTime(features.modifiedUtc as string | undefined)}
              </p>
            </div>
          </Panel>
          <Panel className="p-5">
            <div className="text-eyebrow text-[var(--color-text-muted)]">Paths</div>
            <div className="mt-3 grid gap-3 text-body-sm text-[var(--color-text-soft)]">
              <span className="break-all">Feature: {String(features.path || features.featurePath || '--')}</span>
              <span className="break-all">Training: {String(features.trainingDatasetPath || '--')}</span>
              <span>Stale: {features.stale ? 'yes' : 'no'}</span>
            </div>
          </Panel>
        </div>
      </WorkflowShell>
    );
  }

  if (activeTab === 'training') {
    const running = training.status === 'running';
    const starting = busy && !running;
    return (
      <WorkflowShell
        eyebrow="Model Training"
        title={activeLabel}
        description={moduleDescription(activeTab)}
        icon={BrainCircuit}
        actions={
          <Button variant="primary" icon={Play} disabled={busy || running} onClick={startTraining}>
            {running ? '训练中' : starting ? '启动中' : '开始训练'}
          </Button>
        }
      >
        <div className="grid min-w-0 gap-4 xl:grid-cols-[360px_minmax(0,1fr)]">
          <Panel className="p-5">
            <div className="grid gap-3">
              <Metric label="训练状态" value={trainingStatusLabel(training.status, training.statusLabel)} tone={statusTone(training.status)} />
              <Metric label="模型版本" value={modelVersionValue} tone="ai" />
              <Metric label="训练结果" value={trainingResultLabel} tone={statusTone(trainingResultStatus)} />
              <Metric label="训练集" value={trainingRecords} tone="keep" />
              <Metric label="Feature 行" value={featureRows} tone="repair" />
            </div>
            <div className="mt-5 rounded-[var(--radius-lg)] bg-[var(--color-ai-soft)] p-4 text-body-sm text-[var(--color-ai)]">
              训练会在本机执行，输出模型和验证报告到 AFR.GlyphCore/models。
            </div>
          </Panel>
          <Panel className="min-h-[480px] overflow-hidden">
            <PanelHeader eyebrow="Training Log" title="训练日志" description={String(training.logPath || '尚未开始训练')} />
            <pre className="h-[420px] overflow-auto whitespace-pre-wrap bg-[var(--color-inverse-canvas)] p-4 font-mono text-caption text-[var(--color-inverse-ink)] scrollbar-thin">
              {(training.lines || []).join('\n') || '尚无训练日志'}
            </pre>
          </Panel>
        </div>
      </WorkflowShell>
    );
  }

  return (
    <WorkflowShell
      eyebrow="Model Report"
      title={activeLabel}
      description={moduleDescription(activeTab)}
      icon={BarChart3}
    >
      <div className="grid min-w-0 gap-4 xl:grid-cols-[minmax(0,1fr)_420px]">
        <Panel className="p-5">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <Metric label="模型版本" value={modelVersionValue} tone="ai" />
            <Metric label="训练结果" value={trainingResultLabel} tone={statusTone(trainingResultStatus)} />
            <Metric label="误修率" value={pct(falseRepairRate)} tone={falseRepairRate > 0.001 ? 'unsafe' : 'keep'} />
            <Metric label="召回率" value={pct(recall)} tone="ai" />
          </div>
          <div className="mt-6 rounded-[var(--radius-lg)] bg-[var(--color-keep-soft)] p-5">
            <div className="mb-3 flex items-center gap-2 text-body-lg font-semibold text-[var(--color-text)]">
              <CheckCircle2 className="h-5 w-5 text-[var(--color-keep)]" aria-hidden />
              模型输出
            </div>
            <div className="grid gap-2 text-body-sm text-[var(--color-text-soft)]">
              <span className="break-all">模型版本：{modelVersion}</span>
              <span className="break-all">训练结果：{trainingResultLabel}</span>
              {trainingResult.detail && <span className="break-all">结果说明：{String(trainingResult.detail)}</span>}
              <span className="break-all">ONNX: {String(report.onnxPath || '--')}</span>
              <span className="break-all">Manifest: {String(report.manifestPath || '--')}</span>
              <span className="break-all">Test report: {String(report.testReportPath || '--')}</span>
            </div>
          </div>
        </Panel>
        <Panel className="overflow-hidden">
          <PanelHeader eyebrow="Release Command" title="发布命令" description="用于把验证后的模型并入发布资产。" />
          <pre className={cn('min-h-[260px] overflow-auto whitespace-pre-wrap p-4 font-mono text-caption scrollbar-thin', 'bg-[var(--color-surface-2)] text-[var(--color-text)]')}>
            {report.releaseCommand || '暂无发布命令'}
          </pre>
        </Panel>
      </div>
    </WorkflowShell>
  );
}
