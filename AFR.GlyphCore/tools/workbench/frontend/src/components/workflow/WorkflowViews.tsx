import {
  AlertTriangle,
  ArrowDownToLine,
  ArrowUpToLine,
  BarChart3,
  BrainCircuit,
  CheckCircle2,
  FolderOpen,
  Play,
  RefreshCcw,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles
} from 'lucide-react';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Button, IconButton } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/Controls';
import { Input } from '@/components/ui/input';
import { Panel, PanelHeader, Metric } from '@/components/ui/Panel';
import { Progress } from '@/components/ui/progress';
import { formatDateTime, moduleDescription, pct, tabLabel, cn } from '@/lib/utils';
import { useWorkbenchStore, type App } from '@/store/useWorkbenchStore';
import type { TrainingOptions, ValidationDetail, ValidationSummary } from '@/types/api';

function WorkflowShell({
  eyebrow,
  title,
  description,
  icon: Icon,
  actions,
  contentClassName,
  children
}: {
  eyebrow: string;
  title: string;
  description: string;
  icon: typeof FolderOpen;
  actions?: ReactNode;
  contentClassName?: string;
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
      <div className={cn('min-h-0 min-w-0', contentClassName || 'overflow-auto scrollbar-thin')}>{children}</div>
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

function acceptanceTone(status: string | undefined): 'ai' | 'keep' | 'warn' | 'unsafe' {
  if (status === 'passed') return 'keep';
  if (status === 'overfit' || status === 'pending-simulation') return 'warn';
  if (status === 'failed') return 'unsafe';
  return 'ai';
}

function numberValue(value: unknown, fallback = 0) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function metricPct(summary: ValidationSummary | undefined, key: keyof ValidationSummary) {
  return pct(numberValue(summary?.[key]));
}

function gapPct(value: unknown) {
  const parsed = numberValue(value);
  return `${parsed >= 0 ? '+' : ''}${pct(parsed)}`;
}

function optionValue(options: TrainingOptions, key: keyof TrainingOptions, fallback: number) {
  const value = Number(options[key]);
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

const compactMetricClass =
  'px-3 py-2 [&_.text-headline]:text-[var(--font-size-body-lg)] [&_.text-headline]:leading-[var(--line-height-snug)]';

function severityLabel(severity: string | undefined) {
  if (!severity || severity === 'ok') return '正常';
  const labels: Record<string, string> = {
    'false-repair': '误修',
    'wrong-repair': '错修',
    'missed-repair': '漏修'
  };
  return labels[severity] || '问题';
}

function normalizedResultText(value: unknown) {
  return String(value ?? '').trim();
}

function hasAiManualMismatch(sample: ValidationDetail) {
  const aiText = normalizedResultText(sample.bestText);
  const labelText = normalizedResultText(sample.labelText);
  if (!aiText && !labelText) return false;
  return aiText !== labelText;
}

function sortSimulationResults(rows: ValidationDetail[]) {
  const severityRank: Record<string, number> = {
    'false-repair': 0,
    'wrong-repair': 1,
    'missed-repair': 2,
    ok: 3
  };
  return [...rows].sort((left, right) => {
    const mismatch = Number(!hasAiManualMismatch(left)) - Number(!hasAiManualMismatch(right));
    if (mismatch !== 0) return mismatch;

    const severity =
      (severityRank[String(left.severity || 'ok')] ?? 9) -
      (severityRank[String(right.severity || 'ok')] ?? 9);
    if (severity !== 0) return severity;

    const score = numberValue(right.score) - numberValue(left.score);
    if (score !== 0) return score;

    return String(left.groupId || '').localeCompare(String(right.groupId || ''));
  });
}

function reasonText(reason: string) {
  const labels: Record<string, string> = {
    'blind-set-has-few-repair-examples': '盲测修复样本偏少',
    'train-recall-much-higher-than-blind': '训练召回明显高于盲测',
    'train-recall-higher-than-blind': '训练召回高于盲测',
    'train-accuracy-much-higher-than-blind': '训练准确率明显高于盲测',
    'train-accuracy-higher-than-blind': '训练准确率高于盲测',
    'validation-recall-higher-than-blind': '验证召回高于盲测',
    'manual-simulation-required': '需要手动模拟测试'
  };
  return labels[reason] || reason;
}

function splitMetricCards(label: string, summary: ValidationSummary | undefined) {
  return (
    <div className="rounded-[var(--radius-lg)] border border-[var(--color-line)] bg-[var(--color-canvas)] p-3">
      <div className="text-eyebrow text-[var(--color-text-muted)]">{label}</div>
      <div className="mt-3 grid grid-cols-2 gap-2">
        <Metric label="样本簇" value={numberValue(summary?.groups)} tone="ai" className={compactMetricClass} />
        <Metric label="误修" value={numberValue(summary?.falseRepairs)} tone={numberValue(summary?.falseRepairs) > 0 ? 'unsafe' : 'keep'} className={compactMetricClass} />
        <Metric label="召回率" value={metricPct(summary, 'repairRecall')} tone="repair" className={compactMetricClass} />
        <Metric label="准确率" value={metricPct(summary, 'decisionAccuracy')} tone="keep" className={compactMetricClass} />
      </div>
    </div>
  );
}

export function WorkflowViews() {
  const { app, activeTab, buildFeatures, startTraining, startSimulationTest, selectPackage, busy } =
    useWorkbenchStore();
  const trainingLogRef = useRef<HTMLPreElement | null>(null);

  const packages = (app as App | null)?.packages || [];
  const packageSignature = packages
    .map((item: any) => `${item.id}:${Number(item.trainingDataset || 0)}:${Number(item.reviewed || 0)}:${item.active ? 1 : 0}`)
    .join('|');
  const [selectedTrainingPackageIds, setSelectedTrainingPackageIds] = useState<string[]>([]);
  const [trainingOptions, setTrainingOptions] = useState<TrainingOptions>({
    maxRounds: 650,
    earlyStoppingRounds: 60,
    seed: 20260512
  });
  const packageById = useMemo(
    () => new Map(packages.map((item: any) => [String(item.id), item])),
    [packages]
  );
  const selectedPackageIds = selectedTrainingPackageIds.filter((id) => packageById.has(id));
  const selectedPackages = selectedPackageIds.map((id) => packageById.get(id));
  const selectedTrainingRecords = selectedPackages.reduce(
    (sum, item: any) => sum + Number(item?.trainingDataset || 0) + Number(item?.reviewed || 0),
    0
  );
  const features = app?.features || {};
  const training = app?.training || { status: 'idle', lines: [] };
  const trainingDataset = app?.data?.trainingDataset || { records: [], summary: {} };
  const report = app?.report || {};
  const validation = (report.blindReport?.summary || report.testReport?.summary || {}) as ValidationSummary;
  const trainValidation = report.trainReport?.summary;
  const validValidation = report.validReport?.summary;
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
  const featureRows = Number(features.rows || 0);
  const trainingRecords = Number(features.trainingDatasetRows || trainingDataset.summary?.total || 0);
  const featureRefreshRows = features.exists
    ? features.stale
      ? trainingRecords
      : 0
    : trainingRecords;
  const featureButtonLabel = featureRefreshRows > 0 ? '刷新 Feature' : '重建 Feature';
  const featureProgress =
    trainingRecords > 0
      ? Math.min(100, Math.round((featureRows / Math.max(1, trainingRecords)) * 100))
      : 0;
  const falseRepairRate = Number(validation.falseRepairRate || 0);
  const recall = Number(validation.repairRecall || 0);
  const acceptance = report.acceptance || {};
  const overfitting = report.overfitting || {};
  const simulation = report.simulation || {};
  const reportTrainingConfig =
    report.trainingConfig || ((report.manifest?.trainingConfig as TrainingOptions | undefined) || {});
  const effectiveTrainingConfig = {
    ...(training.trainingConfig as TrainingOptions | undefined),
    ...reportTrainingConfig
  };
  const acceptanceStatus = String(acceptance.status || (report.exists ? 'passed' : 'idle'));
  const acceptanceLabel = String(acceptance.label || (report.exists ? '通过' : '未评估'));
  const simulationStatus = String(simulation.status || (acceptanceStatus === 'pending-simulation' ? 'pending' : validation.groups ? 'completed' : 'idle'));
  const simulationRunning = simulationStatus === 'running';
  const simulationLabel = String(simulation.statusLabel || simulation.label || trainingStatusLabel(simulationStatus));
  const canStartSimulation = Boolean(report.exists) && training.status !== 'running' && !simulationRunning;
  const errorSamples = (report.errorSamples || []) as ValidationDetail[];
  const simulationSourceRows = (report.blindReport?.details || report.testReport?.details || []) as ValidationDetail[];
  const simulationResults = useMemo(
    () => sortSimulationResults(simulationSourceRows.length > 0 ? simulationSourceRows : errorSamples),
    [simulationSourceRows, errorSamples]
  );
  const aiManualMismatchCount = simulationResults.filter(hasAiManualMismatch).length;
  const history = report.history || [];

  useEffect(() => {
    setSelectedTrainingPackageIds((currentSelection) => {
      const validIds = new Set(packages.map((item: any) => String(item.id)));
      const current = currentSelection.filter((id) => validIds.has(id));
      const preferred = packages
        .filter((item: any) => Number(item.trainingDataset || 0) + Number(item.reviewed || 0) > 0)
        .map((item: any) => String(item.id));
      const active = packages.filter((item: any) => item.active).map((item: any) => String(item.id));
      const next = current.length > 0 ? current : (preferred.length > 0 ? preferred : active);
      if (next.length === currentSelection.length && next.every((id, index) => id === currentSelection[index])) {
        return currentSelection;
      }
      return next;
    });
  }, [packageSignature, packages]);

  function toggleTrainingPackage(packageId: string, checked: boolean) {
    setSelectedTrainingPackageIds((current) => {
      if (checked) {
        return current.includes(packageId) ? current : [...current, packageId];
      }
      return current.filter((id) => id !== packageId);
    });
  }

  function updateTrainingOption(key: keyof TrainingOptions, value: string) {
    setTrainingOptions((current) => ({
      ...current,
      [key]: Number(value)
    }));
  }

  function scrollTrainingLog(position: 'top' | 'bottom') {
    const node = trainingLogRef.current;
    if (!node) return;
    node.scrollTo({
      top: position === 'top' ? 0 : node.scrollHeight,
      behavior: 'smooth'
    });
  }

  function confirmSimulationTest() {
    const groups = numberValue(simulation.availableGroups || simulation.sampledGroups || validation.groups || trainingRecords);
    const suffix = groups > 0 ? `\n\n预计测试样本簇：${groups}` : '';
    if (!window.confirm(`确认让 AI 对当前全量训练数据集执行模拟测试？${suffix}`)) return;
    startSimulationTest();
  }

  useEffect(() => {
    if (activeTab !== 'training' || training.status !== 'running') return;
    const node = trainingLogRef.current;
    if (!node) return;
    window.requestAnimationFrame(() => {
      node.scrollTop = node.scrollHeight;
    });
  }, [activeTab, training.status, training.lines?.length]);

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
              <Metric label="待刷新" value={featureRefreshRows} tone="warn" />
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
    const canStartTraining = selectedPackageIds.length > 0 && selectedTrainingRecords > 0;
    return (
      <WorkflowShell
        eyebrow="Model Training"
        title={activeLabel}
        description={moduleDescription(activeTab)}
        icon={BrainCircuit}
        contentClassName="overflow-hidden"
        actions={
          <Button
            variant="primary"
            icon={Play}
            disabled={busy || running || !canStartTraining}
            onClick={() => startTraining(selectedPackageIds, trainingOptions)}
          >
            {running ? '训练中' : starting ? '启动中' : '开始训练'}
          </Button>
        }
      >
        <div className="grid h-full min-h-0 min-w-0 gap-4 xl:grid-cols-[360px_minmax(0,1fr)]">
          <Panel className="flex min-h-0 flex-col overflow-hidden p-5">
            <div className="grid shrink-0 grid-cols-2 gap-3">
              <Metric label="训练状态" value={trainingStatusLabel(training.status, training.statusLabel)} tone={statusTone(training.status)} />
              <Metric label="模型版本" value={modelVersionValue} tone="ai" />
              <Metric label="训练结果" value={trainingResultLabel} tone={statusTone(trainingResultStatus)} />
              <Metric label="最佳轮次" value={numberValue(effectiveTrainingConfig.bestIteration, 0) || '--'} tone="repair" />
              <Metric label="早停" value={effectiveTrainingConfig.stoppedEarly ? '已触发' : '待观察'} tone={effectiveTrainingConfig.stoppedEarly ? 'keep' : 'ai'} />
              <Metric label="已选数据包" value={selectedPackageIds.length} tone="ai" />
              <Metric label="训练集" value={selectedTrainingRecords || trainingRecords} tone="keep" />
              <Metric label="Feature 行" value={featureRows} tone="repair" />
            </div>
            <details className="mt-4 shrink-0 rounded-[var(--radius-lg)] border border-[var(--color-line)] bg-[var(--color-canvas)]">
              <summary className="flex cursor-pointer items-center gap-2 px-4 py-3 text-body-sm font-semibold text-[var(--color-text)]">
                <SlidersHorizontal className="h-4 w-4" aria-hidden />
                高级训练参数
              </summary>
              <div className="grid gap-3 border-t border-[var(--color-line)] p-4">
                <label className="grid gap-1 text-body-sm text-[var(--color-text)]">
                  最大轮数
                  <Input
                    type="number"
                    min={1}
                    max={5000}
                    value={optionValue(trainingOptions, 'maxRounds', 650)}
                    onChange={(event) => updateTrainingOption('maxRounds', event.target.value)}
                  />
                </label>
                <label className="grid gap-1 text-body-sm text-[var(--color-text)]">
                  早停耐心值
                  <Input
                    type="number"
                    min={1}
                    max={500}
                    value={optionValue(trainingOptions, 'earlyStoppingRounds', 60)}
                    onChange={(event) => updateTrainingOption('earlyStoppingRounds', event.target.value)}
                  />
                </label>
                <label className="grid gap-1 text-body-sm text-[var(--color-text)]">
                  随机种子
                  <Input
                    type="number"
                    min={1}
                    value={optionValue(trainingOptions, 'seed', 20260512)}
                    onChange={(event) => updateTrainingOption('seed', event.target.value)}
                  />
                </label>
              </div>
            </details>
            <div className="mt-4 flex min-h-0 flex-1 flex-col gap-2">
              <div className="text-eyebrow text-[var(--color-text-muted)]">Training Packages</div>
              <div className="min-h-0 flex-1 overflow-auto rounded-[var(--radius-lg)] border border-[var(--color-line)] scrollbar-thin">
                {packages.length === 0 && (
                  <div className="p-4 text-body-sm text-[var(--color-text-soft)]">没有可用数据包</div>
                )}
                {packages.map((item: any) => {
                  const packageId = String(item.id);
                  const checked = selectedPackageIds.includes(packageId);
                  const trainCount = Number(item.trainingDataset || 0);
                  const reviewedCount = Number(item.reviewed || 0);
                  return (
                    <label
                      key={packageId}
                      className={cn(
                        'grid cursor-pointer grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-3 border-b border-[var(--color-line)] px-3 py-2 last:border-b-0',
                        checked ? 'bg-[var(--color-ai-soft)]' : 'bg-[var(--color-canvas)] hover:bg-[var(--color-hover)]'
                      )}
                    >
                      <Checkbox
                        checked={checked}
                        label={`选择训练数据包 ${packageId}`}
                        onCheckedChange={(value) => toggleTrainingPackage(packageId, value)}
                      />
                      <span className="min-w-0">
                        <strong className="block truncate text-body-sm text-[var(--color-text)]">
                          {packageId}
                        </strong>
                        <span className="block truncate text-caption text-[var(--color-text-soft)]">
                          {item.drawing?.fileName || 'DWG'}
                        </span>
                      </span>
                      <span className="grid justify-items-end gap-0.5 text-caption text-[var(--color-text-muted)]">
                        <span>Train {trainCount}</span>
                        {reviewedCount > 0 && <span>Reviewed {reviewedCount}</span>}
                      </span>
                    </label>
                  );
                })}
              </div>
            </div>
            <div className="mt-4 shrink-0 rounded-[var(--radius-lg)] bg-[var(--color-ai-soft)] p-4 text-body-sm text-[var(--color-ai)]">
              训练会按文本模式切分训练、验证集；全量模拟测试需要训练完成后手动确认启动。
            </div>
          </Panel>
          <Panel className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden">
            <PanelHeader
              eyebrow="Training Log"
              title="训练日志"
              description={String(training.logPath || '尚未开始训练')}
              actions={
                <>
                  <IconButton icon={ArrowUpToLine} label="日志到顶部" size="sm" onClick={() => scrollTrainingLog('top')} />
                  <IconButton icon={ArrowDownToLine} label="日志到底部" size="sm" onClick={() => scrollTrainingLog('bottom')} />
                </>
              }
            />
            <pre ref={trainingLogRef} className="min-h-0 overflow-auto whitespace-pre-wrap bg-[var(--color-inverse-canvas)] p-4 font-mono text-caption text-[var(--color-inverse-ink)] scrollbar-thin">
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
      contentClassName="overflow-hidden"
      actions={
        <Button
          variant="primary"
          icon={Play}
          disabled={busy || !canStartSimulation}
          onClick={confirmSimulationTest}
        >
          {simulationRunning ? '模拟测试中' : simulationStatus === 'completed' || simulationStatus === 'succeeded' ? '重新全量模拟' : '全量模拟测试'}
        </Button>
      }
    >
      <div className="grid h-full min-h-0 min-w-0 gap-4 max-lg:grid-rows-[minmax(0,1fr)_minmax(0,0.85fr)] lg:grid-cols-[minmax(0,1fr)_380px] 2xl:grid-cols-[minmax(0,1fr)_420px]">
        <div className="grid min-h-0 min-w-0 grid-rows-[minmax(0,0.72fr)_minmax(0,1.28fr)] gap-4">
          <Panel className="min-h-0 overflow-hidden">
            <div className="min-h-0 h-full overflow-auto p-4 scrollbar-thin">
              <div className={cn('rounded-[var(--radius-lg)] p-4', acceptanceStatus === 'passed' ? 'bg-[var(--color-keep-soft)]' : acceptanceStatus === 'overfit' ? 'bg-[var(--color-warn-soft)]' : 'bg-[var(--color-unsafe-soft)]')}>
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="min-w-0">
                    <div className="flex items-center gap-2 text-body-lg font-semibold text-[var(--color-text)]">
                      {acceptanceStatus === 'passed' ? (
                        <CheckCircle2 className="h-5 w-5 text-[var(--color-keep)]" aria-hidden />
                      ) : (
                        <AlertTriangle className="h-5 w-5 text-[var(--color-warn)]" aria-hidden />
                      )}
                      {acceptanceLabel}
                    </div>
                    <p className="mt-1 text-body-sm text-[var(--color-text-soft)]">
                      全量模拟误修数为 0 且无严重过拟合才允许发布。
                    </p>
                  </div>
                  <Metric label="可发布" value={acceptance.canPublish ? '是' : '否'} tone={acceptanceTone(acceptanceStatus)} className={compactMetricClass} />
                </div>
              </div>

              <div className="mt-3 grid grid-cols-2 gap-2 lg:grid-cols-4">
                <Metric label="模型版本" value={modelVersionValue} tone="ai" className={compactMetricClass} />
                <Metric label="模拟误修" value={numberValue(validation.falseRepairs)} tone={numberValue(validation.falseRepairs) > 0 ? 'unsafe' : 'keep'} className={compactMetricClass} />
                <Metric label="误修率" value={pct(falseRepairRate)} tone={falseRepairRate > 0 ? 'unsafe' : 'keep'} className={compactMetricClass} />
                <Metric label="召回率" value={pct(recall)} tone="repair" className={compactMetricClass} />
                <Metric label="最佳轮次" value={numberValue(effectiveTrainingConfig.bestIteration, 0) || '--'} tone="repair" className={compactMetricClass} />
                <Metric label="实际轮次" value={numberValue(effectiveTrainingConfig.actualIterations, 0) || '--'} tone="ai" className={compactMetricClass} />
                <Metric label="模拟测试" value={simulationLabel} tone={simulationRunning ? 'warn' : simulationStatus === 'failed' ? 'unsafe' : simulationStatus === 'completed' || simulationStatus === 'succeeded' ? 'keep' : 'ai'} className={compactMetricClass} />
                <Metric label="测试范围" value={numberValue(simulation.sampledGroups || simulation.sampleGroups || simulation.minimumGroups || validation.groups)} tone="repair" className={compactMetricClass} />
              </div>

              <div className="mt-3 grid gap-3 lg:grid-cols-3">
                {splitMetricCards('训练集', trainValidation)}
                {splitMetricCards('验证集', validValidation)}
                {splitMetricCards('全量模拟', validation)}
              </div>

              <div className="mt-3 rounded-[var(--radius-lg)] border border-[var(--color-line)] bg-[var(--color-canvas)] p-4">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div>
                    <div className="text-eyebrow text-[var(--color-text-muted)]">Generalization</div>
                    <h3 className="text-card-title text-[var(--color-text)]">过拟合检查</h3>
                  </div>
                  <Metric label="状态" value={overfitting.status === 'severe' ? '严重' : overfitting.status === 'warning' ? '警告' : '正常'} tone={overfitting.severe ? 'unsafe' : overfitting.warning ? 'warn' : 'keep'} className={compactMetricClass} />
                </div>
                <div className="mt-3 grid grid-cols-1 gap-2 sm:grid-cols-3">
                  <Metric label="召回差距" value={gapPct(overfitting.recallGap)} tone={numberValue(overfitting.recallGap) >= 0.2 ? 'unsafe' : numberValue(overfitting.recallGap) >= 0.1 ? 'warn' : 'keep'} className={compactMetricClass} />
                  <Metric label="准确率差距" value={gapPct(overfitting.accuracyGap)} tone={numberValue(overfitting.accuracyGap) >= 0.15 ? 'unsafe' : numberValue(overfitting.accuracyGap) >= 0.08 ? 'warn' : 'keep'} className={compactMetricClass} />
                  <Metric label="模拟样本簇" value={numberValue(validation.groups)} tone="ai" className={compactMetricClass} />
                </div>
                {Array.isArray(overfitting.reasons) && overfitting.reasons.length > 0 && (
                  <div className="mt-3 flex flex-wrap gap-2">
                    {overfitting.reasons.map((reason) => (
                      <span key={reason} className="rounded-full border border-[var(--color-warn-border)] bg-[var(--color-warn-soft)] px-3 py-1 text-caption text-[var(--color-warn)]">
                        {reasonText(String(reason))}
                      </span>
                    ))}
                  </div>
                )}
              </div>
            </div>
          </Panel>

          <Panel className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden">
            <PanelHeader
              eyebrow="Simulation Results"
              title="模拟测试结果"
              description={`共 ${simulationResults.length} 条，AI 与人工标注不一致 ${aiManualMismatchCount} 条，已置顶标红。`}
            />
            <div className="min-h-0 overflow-auto scrollbar-thin">
              {simulationResults.length === 0 ? (
                <div className="p-5 text-body-sm text-[var(--color-text-soft)]">暂无模拟测试结果。</div>
              ) : (
                <table className="w-full min-w-[920px] border-collapse text-left text-body-sm">
                  <thead className="sticky top-0 bg-[var(--color-canvas)] text-caption text-[var(--color-text-muted)]">
                    <tr>
                      <th className="border-b border-[var(--color-line)] px-3 py-2">类型</th>
                      <th className="border-b border-[var(--color-line)] px-3 py-2">原文</th>
                      <th className="border-b border-[var(--color-line)] px-3 py-2">AI 输出</th>
                      <th className="border-b border-[var(--color-line)] px-3 py-2">人工正确文本</th>
                      <th className="border-b border-[var(--color-line)] px-3 py-2">分数</th>
                      <th className="border-b border-[var(--color-line)] px-3 py-2">来源</th>
                      <th className="border-b border-[var(--color-line)] px-3 py-2">数据包</th>
                    </tr>
                  </thead>
                  <tbody>
                    {simulationResults.map((sample, index) => {
                      const mismatch = hasAiManualMismatch(sample);
                      return (
                        <tr
                          key={sample.groupId || `${sample.currentText}-${sample.bestText}-${index}`}
                          className={cn(
                            'border-b border-[var(--color-line)] last:border-b-0',
                            mismatch ? 'bg-[var(--color-unsafe-soft)]' : 'hover:bg-[var(--color-hover)]'
                          )}
                        >
                          <td className={cn('px-3 py-2', mismatch ? 'font-semibold text-[var(--color-unsafe)]' : 'text-[var(--color-text-soft)]')}>
                            {mismatch ? `不一致 / ${severityLabel(sample.severity)}` : severityLabel(sample.severity)}
                          </td>
                          <td className="max-w-[220px] px-3 py-2 [overflow-wrap:anywhere]">{sample.currentText || '--'}</td>
                          <td className={cn('max-w-[220px] px-3 py-2 [overflow-wrap:anywhere]', mismatch && 'font-semibold text-[var(--color-unsafe)]')}>{sample.bestText || '--'}</td>
                          <td className={cn('max-w-[220px] px-3 py-2 [overflow-wrap:anywhere]', mismatch && 'font-semibold text-[var(--color-unsafe)]')}>{sample.labelText || '--'}</td>
                          <td className="px-3 py-2">{numberValue(sample.score).toFixed(3)} / {numberValue(sample.margin).toFixed(3)}</td>
                          <td className="max-w-[160px] px-3 py-2 [overflow-wrap:anywhere]">{sample.source || '--'}</td>
                          <td className="max-w-[180px] px-3 py-2 [overflow-wrap:anywhere]">{sample.packageId || sample.drawingFileName || '--'}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              )}
            </div>
          </Panel>
        </div>

        <div className="grid min-h-0 gap-4 lg:grid-rows-[minmax(0,0.9fr)_minmax(0,0.7fr)_minmax(0,1fr)]">
          <Panel className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden">
            <PanelHeader
              eyebrow="Model Output"
              title={
                <span className="inline-flex items-center gap-2">
                  <ShieldCheck className="h-5 w-5 text-[var(--color-ai)]" aria-hidden />
                  模型输出
                </span>
              }
              className="px-4 py-3"
            />
            <div className="grid min-h-0 gap-2 overflow-auto p-4 text-body-sm text-[var(--color-text-soft)] scrollbar-thin">
              <span className="break-all">模型版本：{modelVersion}</span>
              <span className="break-all">训练结果：{trainingResultLabel}</span>
              {trainingResult.detail && <span className="break-all">结果说明：{String(trainingResult.detail)}</span>}
              <span className="break-all">模拟测试：{simulationLabel}</span>
              {simulation.seed && <span className="break-all">测试随机种子：{String(simulation.seed)}</span>}
              {simulation.logPath && <span className="break-all">Simulation log: {String(simulation.logPath)}</span>}
              <span className="break-all">ONNX: {String(report.onnxPath || '--')}</span>
              <span className="break-all">Manifest: {String(report.manifestPath || '--')}</span>
              <span className="break-all">Simulation report: {String(report.blindReportPath || report.testReportPath || '--')}</span>
            </div>
          </Panel>

          <Panel className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden">
            <PanelHeader eyebrow="Release Command" title="发布命令" description="只有报告显示通过时才用于发布资产。" />
            <pre className={cn('min-h-0 overflow-auto whitespace-pre-wrap p-4 font-mono text-caption scrollbar-thin', 'bg-[var(--color-surface-2)] text-[var(--color-text)]')}>
              {report.releaseCommand || '暂无发布命令'}
            </pre>
          </Panel>

          <Panel className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden">
            <PanelHeader eyebrow="Training History" title="训练历史" description="最近训练结果摘要。" />
            <div className="min-h-0 overflow-auto scrollbar-thin">
              {history.length === 0 ? (
                <div className="p-4 text-body-sm text-[var(--color-text-soft)]">暂无训练历史。</div>
              ) : (
                <div className="grid gap-2 p-3">
                  {history.map((item: any) => {
                    const blind = item.blindSummary || {};
                    const result = item.acceptance || {};
                    return (
                      <div key={String(item.id || item.modelVersion)} className="rounded-[var(--radius-md)] border border-[var(--color-line)] p-3">
                        <div className="truncate text-body-sm font-semibold text-[var(--color-text)]">{String(result.label || item.modelVersion || '--')}</div>
                        <div className="mt-1 grid gap-1 text-caption text-[var(--color-text-soft)]">
                          <span>{formatDateTime(String(item.createdUtc || ''))}</span>
                          <span>误修 {numberValue(blind.falseRepairs)} · 召回 {pct(numberValue(blind.repairRecall))}</span>
                          <span className="truncate">{String(item.modelVersion || '--')}</span>
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
          </Panel>
        </div>
      </div>
    </WorkflowShell>
  );
}
