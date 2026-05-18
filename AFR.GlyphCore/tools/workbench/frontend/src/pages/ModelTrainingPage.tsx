import { useEffect, useMemo, useRef, useState } from 'react';
import {
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  CheckCircle,
  Clock,
  Database,
  PlayCircle,
  RefreshCw,
  Search,
  StopCircle,
  XCircle
} from 'lucide-react';
import { packageViews, trainingRecordViews, trainingRunView } from '@/lib/boltAdapters';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';
import type { DataPackage } from '@/types/bolt';

const STATUS_CFG = {
  idle: { label: '未训练', cls: 'bg-gray-100 text-gray-500', icon: <Clock size={12} /> },
  running: { label: '训练中', cls: 'bg-blue-100 text-blue-700', icon: <RefreshCw size={12} className="animate-spin" /> },
  completed: { label: '已完成', cls: 'bg-green-100 text-green-700', icon: <CheckCircle size={12} /> },
  failed: { label: '失败', cls: 'bg-red-100 text-red-700', icon: <XCircle size={12} /> }
};

export default function ModelTrainingPage() {
  const { app, busy, startTraining, cancelTraining } = useWorkbenchStore();
  const trainingRecords = useMemo(() => trainingRecordViews(app), [app]);
  const packages = useMemo(() => packageViews(app, trainingRecords), [app, trainingRecords]);
  const run = useMemo(() => trainingRunView(app, packages), [app, packages]);
  const [pkgs, setPkgs] = useState<string[]>([]);
  const [epochs, setEpochs] = useState(run.epochs);
  const [batch, setBatch] = useState(run.batchSize);
  const [lr, setLr] = useState(run.learningRate);
  const logRef = useRef<HTMLDivElement>(null);
  const packageIds = useMemo(() => run.dataPackages.join('|'), [run.dataPackages]);

  useEffect(() => {
    if (pkgs.length === 0 && run.dataPackages.length > 0) setPkgs(run.dataPackages);
  }, [packageIds, pkgs.length, run.dataPackages]);

  useEffect(() => {
    if (run.status !== 'running') {
      setEpochs(run.epochs);
      setBatch(run.batchSize);
      setLr(run.learningRate);
    }
  }, [run.batchSize, run.epochs, run.learningRate, run.status]);

  const toBottom = () => logRef.current?.scrollTo({ top: logRef.current.scrollHeight, behavior: 'smooth' });
  const toTop = () => logRef.current?.scrollTo({ top: 0, behavior: 'smooth' });

  useEffect(() => {
    if (run.status === 'running') toBottom();
  }, [run.logs, run.status]);

  const togglePkg = (id: string) => setPkgs((current) => (current.includes(id) ? current.filter((item) => item !== id) : [...current, id]));

  const start = () => {
    void startTraining(pkgs, {
      maxRounds: epochs,
      earlyStoppingRounds: batch,
      seed: 42
    });
  };

  const sc = STATUS_CFG[run.status];

  return (
    <div className="flex h-full gap-3 overflow-hidden">
      <div className="flex w-72 shrink-0 flex-col gap-3 overflow-y-auto pb-2">
        <div className="rounded border border-gray-200 bg-white p-3">
          <div className="mb-3 text-xs font-semibold uppercase tracking-wide text-gray-700">训练状态</div>
          <div className={`mb-3 flex items-center gap-2 rounded px-3 py-2 ${sc.cls}`}>
            {sc.icon}
            <span className="text-xs font-medium">{sc.label}</span>
          </div>
          <div className="flex flex-col gap-1.5 text-xs">
            <IRow label="模型版本" value={run.version} />
            {run.startedAt && <IRow label="开始时间" value={run.startedAt} />}
            {run.completedAt && <IRow label="完成时间" value={run.completedAt} />}
            {run.bestEpoch && <IRow label="最佳 Epoch" value={`第 ${run.bestEpoch} 轮`} />}
            {run.bestAccuracy && <IRow label="最佳准确率" value={`${(run.bestAccuracy * 100).toFixed(2)}%`} />}
          </div>
        </div>

        <div className="rounded border border-gray-200 bg-white p-3">
          <div className="mb-3 text-xs font-semibold uppercase tracking-wide text-gray-700">训练参数</div>
          <div className="flex flex-col gap-2">
            <PInput label="最大 Epoch" type="number" value={epochs} onChange={(value) => setEpochs(Number(value))} disabled={run.status === 'running'} />
            <PInput label="Batch Size" type="number" value={batch} onChange={(value) => setBatch(Number(value))} disabled={run.status === 'running'} />
            <PInput label="学习率" type="number" value={lr} onChange={(value) => setLr(Number(value))} step="0.0001" disabled={run.status === 'running'} />
          </div>
        </div>

        <DataPackageSelector packages={packages} pkgs={pkgs} onToggle={togglePkg} disabled={run.status === 'running'} />

        <div className="flex flex-col gap-2">
          <button
            type="button"
            disabled={busy || run.status === 'running' || pkgs.length === 0}
            onClick={start}
            className="flex items-center justify-center gap-2 rounded bg-gray-900 px-4 py-2 text-xs font-medium text-white transition-colors hover:bg-gray-700 disabled:cursor-not-allowed disabled:opacity-40"
          >
            <PlayCircle size={14} />
            开始训练
          </button>
          <button
            type="button"
            disabled={busy || run.status !== 'running'}
            onClick={() => { void cancelTraining(); }}
            className="flex items-center justify-center gap-2 rounded border border-red-200 px-4 py-2 text-xs font-medium text-red-600 transition-colors hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
          >
            <StopCircle size={14} />
            停止训练
          </button>
        </div>

        {pkgs.length === 0 && (
          <div className="flex items-center gap-1.5 rounded border border-yellow-200 bg-yellow-50 px-2 py-1.5 text-xs text-yellow-700">
            <AlertTriangle size={11} />
            至少选择一个数据包
          </div>
        )}
      </div>

      <div className="flex min-w-0 flex-1 flex-col overflow-hidden">
        <div className="mb-2 flex shrink-0 items-center justify-between">
          <span className="text-xs font-semibold uppercase tracking-wide text-gray-700">训练日志</span>
          <div className="flex items-center gap-1">
            <button type="button" onClick={toTop} title="回到顶部" className="rounded p-1.5 text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-700">
              <ArrowUp size={14} />
            </button>
            <button type="button" onClick={toBottom} title="回到底部" className="rounded p-1.5 text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-700">
              <ArrowDown size={14} />
            </button>
          </div>
        </div>
        <div ref={logRef} className="flex-1 overflow-auto rounded border border-gray-700 bg-gray-950 p-3 font-mono text-xs leading-5">
          {run.logs.map((line, index) => {
            const warn = line.includes('WARN') || line.includes('警告');
            const err = line.includes('ERROR') || line.includes('失败');
            const best = line.includes('★') || line.includes('最佳');
            return (
              <div key={`${index}-${line}`} className={err ? 'text-red-400' : warn ? 'text-yellow-400' : best ? 'font-medium text-green-400' : 'text-gray-400'}>
                {line}
              </div>
            );
          })}
          {run.status === 'running' && (
            <div className="mt-1 flex items-center gap-1.5 text-blue-400">
              <RefreshCw size={10} className="animate-spin" />
              训练进行中...
            </div>
          )}
          {run.logs.length === 0 && <div className="text-gray-600">等待训练启动...</div>}
        </div>
      </div>
    </div>
  );
}

function IRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-center justify-between">
      <span className="text-gray-500">{label}</span>
      <span className="font-medium text-gray-800">{value}</span>
    </div>
  );
}

function DataPackageSelector({
  packages,
  pkgs,
  onToggle,
  disabled
}: {
  packages: DataPackage[];
  pkgs: string[];
  onToggle: (id: string) => void;
  disabled: boolean;
}) {
  const [query, setQuery] = useState('');
  const available = packages.filter((pkg) => pkg.status !== 'processing');
  const filtered = available.filter((pkg) => pkg.name.toLowerCase().includes(query.toLowerCase()) || pkg.dwgFile.toLowerCase().includes(query.toLowerCase()));
  const totalRecords = available.filter((pkg) => pkgs.includes(pkg.id)).reduce((sum, pkg) => sum + (pkg.trainingRecords ?? pkg.inTrainingSet), 0);

  return (
    <div className="flex min-h-0 flex-col rounded border border-gray-200 bg-white">
      <div className="flex shrink-0 items-center justify-between border-b border-gray-100 px-3 py-2.5">
        <div className="flex items-center gap-1.5">
          <Database size={12} className="text-gray-500" />
          <span className="text-xs font-semibold uppercase tracking-wide text-gray-700">训练数据包</span>
        </div>
        <div className="flex items-center gap-1.5">
          <span className="text-xs text-gray-400">
            {pkgs.length}/{available.length} 选中
          </span>
          {pkgs.length > 0 && <span className="rounded bg-blue-50 px-1.5 py-0.5 text-xs font-medium text-blue-600">{totalRecords.toLocaleString()} 条</span>}
        </div>
      </div>

      <div className="shrink-0 border-b border-gray-100 px-2.5 py-2">
        <div className="relative">
          <Search size={11} className="pointer-events-none absolute left-2 top-1/2 -translate-y-1/2 text-gray-400" />
          <input
            type="text"
            placeholder="搜索数据包..."
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            className="w-full rounded border border-gray-200 bg-gray-50 py-1.5 pl-6 pr-2 text-xs focus:border-gray-400 focus:outline-none"
          />
        </div>
      </div>

      <div className="flex shrink-0 items-center gap-2 border-b border-gray-100 px-3 py-1.5">
        <button
          type="button"
          disabled={disabled}
          onClick={() => available.forEach((pkg) => { if (!pkgs.includes(pkg.id)) onToggle(pkg.id); })}
          className="text-xs text-blue-600 transition-colors hover:text-blue-800 disabled:cursor-not-allowed disabled:opacity-40"
        >
          全选
        </button>
        <span className="text-gray-200">|</span>
        <button
          type="button"
          disabled={disabled}
          onClick={() => pkgs.forEach((id) => onToggle(id))}
          className="text-xs text-gray-500 transition-colors hover:text-gray-800 disabled:cursor-not-allowed disabled:opacity-40"
        >
          清空
        </button>
        <span className="ml-auto text-xs text-gray-400">{filtered.length} 个结果</span>
      </div>

      <div className="overflow-y-auto" style={{ maxHeight: '220px' }}>
        {filtered.length === 0 ? (
          <div className="py-6 text-center text-xs text-gray-400">无匹配数据包</div>
        ) : (
          filtered.map((pkg) => {
            const checked = pkgs.includes(pkg.id);
            const records = pkg.trainingRecords ?? pkg.inTrainingSet;
            return (
              <label
                key={pkg.id}
                className={`flex cursor-pointer items-center gap-2.5 border-b border-gray-50 px-3 py-2 transition-colors last:border-0 ${
                  disabled ? 'cursor-not-allowed opacity-60' : 'hover:bg-gray-50'
                } ${checked ? 'bg-blue-50/40' : ''}`}
              >
                <input type="checkbox" className="shrink-0 accent-gray-800" checked={checked} onChange={() => onToggle(pkg.id)} disabled={disabled} />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center justify-between gap-1">
                    <span className={`truncate text-xs ${checked ? 'font-medium text-gray-900' : 'text-gray-700'}`}>{pkg.name}</span>
                    <span className="shrink-0 text-xs text-gray-400">{records.toLocaleString()} 条</span>
                  </div>
                  <div className="mt-0.5 truncate text-xs text-gray-400">{pkg.dwgFile}</div>
                </div>
              </label>
            );
          })
        )}
      </div>
    </div>
  );
}

function PInput({
  label,
  type,
  value,
  onChange,
  step,
  disabled
}: {
  label: string;
  type: string;
  value: number;
  onChange: (value: string) => void;
  step?: string;
  disabled?: boolean;
}) {
  return (
    <div>
      <label className="mb-1 block text-xs text-gray-500">{label}</label>
      <input
        type={type}
        value={value}
        step={step}
        onChange={(event) => onChange(event.target.value)}
        disabled={disabled}
        className="w-full rounded border border-gray-200 px-2 py-1.5 text-xs focus:border-gray-400 focus:outline-none disabled:bg-gray-50 disabled:text-gray-400"
      />
    </div>
  );
}
