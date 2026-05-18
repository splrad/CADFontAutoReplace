import { AlertTriangle, BarChart2, CheckCircle, RefreshCw, Upload, XCircle } from 'lucide-react';
import { modelReportView } from '@/lib/boltAdapters';
import { formatDateTime } from '@/lib/utils';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';
import type { SimulatedTestResult } from '@/types/bolt';

export default function ModelReportPage() {
  const { app, busy, startSimulationTest, resetModel } = useWorkbenchStore();
  const report = modelReportView(app);
  const mismatches: SimulatedTestResult[] = report.simulatedTests.filter((test) => !test.matched);
  const mismatchCount = mismatches.length;
  const history = (app?.report?.history || []).slice(0, 5);

  const runSimulation = () => {
    void startSimulationTest();
  };

  const reset = () => {
    if (!window.confirm('归档当前模型与报告产物并重置模型状态？文件会移动到 .trash，可手动恢复。')) return;
    void resetModel();
  };

  return (
    <div className="flex h-full gap-3 overflow-hidden">
      <div className="flex min-w-0 flex-1 flex-col gap-3 overflow-hidden">
        <div className="shrink-0 rounded border border-gray-200 bg-white">
          <div className="flex items-center border-b border-gray-100 px-4 py-2.5">
            <div className="flex items-center gap-2">
              <BarChart2 size={14} className="text-gray-600" />
              <span className="text-xs font-semibold text-gray-700">模型摘要 — {report.version}</span>
            </div>
          </div>
          <div className="p-4">
            <div className="grid grid-cols-6 gap-3">
              <MetricTile label="召回率" value={`${(report.recall * 100).toFixed(2)}%`} color="text-green-700" />
              <MetricTile label="精确率" value={`${(report.precision * 100).toFixed(2)}%`} color="text-green-700" />
              <MetricTile label="误修数" value={`${report.wrongFixes} 条`} color={report.wrongFixes > 5 ? 'text-red-600' : 'text-orange-600'} />
              <MetricTile label="最佳轮次" value={`第 ${report.bestEpoch || 0} 轮`} color="text-gray-900" />
              <MetricTile
                label="过拟合检查"
                value={report.overfitScore < 0.05 ? '通过' : '警告'}
                color={report.overfitScore < 0.05 ? 'text-green-700' : 'text-orange-600'}
                sub={`Δ=${report.overfitScore.toFixed(3)}`}
              />
              <MetricTile
                label="模拟测试"
                value={`${report.simulatedTests.length}/${mismatchCount}`}
                color={mismatchCount > 0 ? 'text-orange-600' : 'text-green-700'}
                sub={mismatchCount > 0 ? `${mismatchCount} 条不一致` : '全部通过'}
              />
            </div>
            {mismatchCount > 0 && (
              <div className="mt-3 flex items-center gap-1.5 rounded border border-orange-200 bg-orange-50 px-2 py-1.5 text-xs text-orange-700">
                <AlertTriangle size={11} />
                模拟测试存在 {mismatchCount} 条 AI 输出与正确文本不一致，已置顶高亮。
              </div>
            )}
          </div>
        </div>

        <div className="flex shrink-0 items-center gap-2">
          <span className="text-xs font-semibold uppercase tracking-wide text-gray-700">不一致记录</span>
          {mismatchCount > 0 && <span className="rounded bg-orange-100 px-1.5 py-0.5 text-xs font-medium text-orange-700">{mismatchCount} 条</span>}
        </div>
        <div className="flex-1 overflow-auto rounded border border-gray-200 bg-white">
          <table className="w-full table-fixed border-collapse text-xs">
            <colgroup>
              <col className="w-1/3" />
              <col className="w-1/3" />
              <col className="w-1/3" />
            </colgroup>
            <thead className="sticky top-0 z-10 bg-gray-50">
              <tr>
                {['原文', 'AI 输出', '正确文本'].map((heading) => (
                  <th key={heading} className="whitespace-nowrap border-b border-gray-200 px-3 py-2 text-left text-xs font-semibold text-gray-500">
                    {heading}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {mismatches.map((test) => (
                <tr key={test.id} className="border-b border-gray-100 bg-orange-50 transition-colors hover:bg-orange-100">
                  <td className="overflow-hidden px-3 py-1.5">
                    <span className="break-all font-mono leading-tight text-gray-700">{test.originalText}</span>
                  </td>
                  <td className="overflow-hidden px-3 py-1.5">
                    <span className="break-all font-medium leading-tight text-red-600">{test.aiOutput}</span>
                  </td>
                  <td className="overflow-hidden px-3 py-1.5">
                    <span className="break-all leading-tight text-gray-900">{test.correctText}</span>
                  </td>
                </tr>
              ))}
              {mismatches.length === 0 && (
                <tr>
                  <td colSpan={3} className="py-16 text-center text-xs text-gray-400">
                    <CheckCircle size={14} className="mr-1.5 inline text-green-500" />
                    全部测试通过，无不一致记录
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      <div className="flex w-56 shrink-0 flex-col gap-3 overflow-y-auto pb-2">
        <div className="rounded border border-gray-200 bg-white p-3">
          <div className="mb-3 text-xs font-semibold uppercase tracking-wide text-gray-700">操作</div>
          <div className="flex flex-col gap-2">
            <button
              type="button"
              disabled={busy || app?.report?.simulation?.status === 'running'}
              onClick={runSimulation}
              className="flex items-center justify-center gap-1.5 rounded bg-gray-900 px-3 py-2 text-xs font-medium text-white transition-colors hover:bg-gray-700 disabled:cursor-not-allowed disabled:opacity-40"
            >
              <Upload size={12} />
              模拟测试
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={reset}
              className="flex items-center justify-center gap-1.5 rounded border border-red-200 px-3 py-2 text-xs font-medium text-red-600 transition-colors hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
            >
              <XCircle size={12} />
              重置模型
            </button>
          </div>
        </div>

        <div className="flex min-h-0 flex-1 flex-col rounded border border-gray-200 bg-white p-3">
          <div className="mb-3 text-xs font-semibold uppercase tracking-wide text-gray-700">模拟测试日志</div>
          <div className="flex flex-1 flex-col gap-2 overflow-y-auto">
            {history.length === 0 && (app?.report?.simulation?.lines || []).length === 0 && <div className="text-xs text-gray-400">暂无模拟测试历史</div>}
            {(app?.report?.simulation?.lines || []).slice(-8).map((line, index) => (
              <div key={`${index}-${line}`} className="flex items-start gap-2">
                <span className="mt-0.5 shrink-0 text-blue-600">
                  <RefreshCw size={10} />
                </span>
                <div className="min-w-0">
                  <div className="break-all text-xs text-gray-800">{line}</div>
                  <div className="text-xs text-gray-400">当前任务</div>
                </div>
              </div>
            ))}
            {history.map((event, index) => {
              const status = String(event.status || event.result || event.action || '');
              const failed = status.includes('fail') || status.includes('失败');
              const action = String(event.label || event.action || event.status || '模拟测试完成');
              const date = String(event.createdUtc || event.finishedUtc || event.time || '');
              return (
                <div key={index} className="flex items-start gap-2">
                  <span className={`mt-0.5 shrink-0 ${failed ? 'text-red-500' : 'text-green-600'}`}>{failed ? <XCircle size={10} /> : <CheckCircle size={10} />}</span>
                  <div className="min-w-0">
                    <div className="text-xs text-gray-800">{action}</div>
                    <div className="text-xs text-gray-400">{formatDateTime(date)}</div>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </div>
  );
}

function MetricTile({ label, value, color, sub }: { label: string; value: string; color: string; sub?: string }) {
  return (
    <div className="rounded border border-gray-100 bg-gray-50 p-2.5">
      <div className="mb-1 text-xs text-gray-500">{label}</div>
      <div className={`text-base font-bold leading-none ${color}`}>{value}</div>
      {sub && <div className="mt-0.5 text-xs text-gray-400">{sub}</div>}
    </div>
  );
}
