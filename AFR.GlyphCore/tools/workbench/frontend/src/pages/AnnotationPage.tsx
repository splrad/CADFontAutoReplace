import { useDeferredValue, useEffect, useMemo, useRef, useState, type MouseEvent, type ReactNode } from 'react';
import {
  AlertTriangle,
  Archive,
  CheckCircle,
  ChevronDown,
  Database,
  Filter,
  Info,
  Pencil,
  RefreshCw,
  Search,
  Trash2,
  X
} from 'lucide-react';
import { packageViews, reviewClusterViews, reviewEditForPatch } from '@/lib/viewAdapters';
import { useVirtualRows } from '@/hooks/useVirtualRows';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';
import type { CorrectTextMode, DataPackage, DBTextCluster, ReviewStatus } from '@/types/view';

const STATUS_CFG: Record<ReviewStatus, { label: string; icon: ReactNode; cls: string }> = {
  pending: { label: '待标注', icon: <RefreshCw size={10} />, cls: 'text-orange-500' },
  confirmed: { label: '已确认', icon: <CheckCircle size={10} />, cls: 'text-green-600' }
};

const TABLE_ROW_HEIGHT = 48;

interface ConfirmDeletePkg {
  pkg: DataPackage;
}

export default function AnnotationPage() {
  const { app, groups, reviewEdits, busy, selectPackage, deletePackage, updateReviewEdit, saveSelectedReviews, resetReviewRows } = useWorkbenchStore();
  const [confirmDel, setConfirmDel] = useState<ConfirmDeletePkg | null>(null);
  const [search, setSearch] = useState('');
  const [fStatus, setFStatus] = useState<ReviewStatus | 'all'>('all');
  const [fEncoding, setFEncoding] = useState('all');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [activeClusterId, setActiveClusterId] = useState<string | null>(null);
  const deferredSearch = useDeferredValue(search);

  const packages = useMemo(() => packageViews(app), [app]);
  const clusters = useMemo(() => reviewClusterViews(app, groups, reviewEdits), [app, groups, reviewEdits]);
  const activePkg = app?.data?.packageId || null;
  const activeClusterBase = useMemo(() => clusters.find((cluster) => cluster.id === activeClusterId) ?? clusters[0] ?? null, [clusters, activeClusterId]);

  const filtered = useMemo(
    () =>
      clusters.filter((cluster) => {
        if (deferredSearch && !cluster.originalText.includes(deferredSearch) && !cluster.candidateTexts.some((text) => text.includes(deferredSearch))) return false;
        if (fStatus !== 'all' && cluster.status !== fStatus) return false;
        if (fEncoding !== 'all' && cluster.encodingPath !== fEncoding) return false;
        return true;
      }),
    [clusters, deferredSearch, fEncoding, fStatus]
  );
  const virtualRows = useVirtualRows(filtered, TABLE_ROW_HEIGHT, 12);
  const encodingOptions = useMemo(() => {
    const values = clusters.map((cluster) => cluster.encodingPath).filter(Boolean);
    return [...new Set(values)].sort((left, right) => left.localeCompare(right, 'zh-Hans-CN'));
  }, [clusters]);
  const statusCounts = useMemo(() => {
    const counts: Record<ReviewStatus, number> = { pending: 0, confirmed: 0 };
    for (const cluster of clusters) {
      counts[cluster.status] += 1;
    }
    return counts;
  }, [clusters]);

  const allChecked = filtered.length > 0 && filtered.every((cluster) => selected.has(cluster.id));
  const someChecked = !allChecked && filtered.some((cluster) => selected.has(cluster.id));
  const toggle = (id: string) => setSelected((current) => toggleSet(current, id));
  const toggleAll = () => setSelected(allChecked ? new Set() : new Set(filtered.map((cluster) => cluster.id)));

  const handleSelectPkg = (pkg: DataPackage) => {
    if (activePkg === pkg.id) return;
    void selectPackage(pkg.id);
    setSelected(new Set());
    setSearch('');
    setFStatus('all');
    setFEncoding('all');
    setActiveClusterId(null);
  };

  const askDeletePkg = (event: MouseEvent, pkg: DataPackage) => {
    event.stopPropagation();
    setConfirmDel({ pkg });
  };

  const confirmDeletePkg = () => {
    if (!confirmDel) return;
    void deletePackage(confirmDel.pkg.id);
    setSelected(new Set());
    setConfirmDel(null);
  };

  const saveSelected = () => {
    void saveSelectedReviews([...selected]);
    setSelected(new Set());
  };

  const resetSelected = () => {
    void resetReviewRows([...selected]);
    setSelected(new Set());
  };

  const updateCluster = (cluster: DBTextCluster, patch: Partial<Pick<DBTextCluster, 'correctTextMode' | 'selectedCandidate' | 'manualText'>>) => {
    setActiveClusterId(cluster.id);
    updateReviewEdit(cluster.id, reviewEditForPatch(cluster, patch));
  };

  return (
    <>
      <div className="flex h-full gap-3 overflow-hidden">
        <aside className="flex w-44 shrink-0 flex-col overflow-hidden rounded border border-gray-200 bg-white">
          <div className="flex shrink-0 items-center gap-2 border-b border-gray-100 px-3 py-2.5">
            <Database size={13} className="text-gray-500" />
            <span className="text-xs font-semibold text-gray-700">原始数据集</span>
            <span className="ml-auto text-xs text-gray-400">{packages.length} 个</span>
          </div>

          <div className="flex-1 overflow-y-auto">
            {packages.map((pkg) => {
              const pct = Math.round((pkg.inTrainingSet / Math.max(pkg.dataCount, 1)) * 100);
              const isArchived = pkg.inTrainingSet >= pkg.dataCount && pkg.dataCount > 0;
              const isActive = activePkg === pkg.id;
              return (
                <div
                  key={pkg.id}
                  className={`flex items-start border-b border-gray-100 transition-colors ${isActive ? 'bg-gray-900' : 'bg-white hover:bg-gray-50'}`}
                >
                  <button type="button" onClick={() => handleSelectPkg(pkg)} className="min-w-0 flex-1 px-3 py-3 text-left">
                    <div className={`mb-2 truncate text-xs font-medium ${isActive ? 'text-white' : 'text-gray-800'}`}>{pkg.name}</div>
                    <div className="mb-1.5 flex items-center gap-2">
                      <div className={`h-1.5 flex-1 overflow-hidden rounded-full ${isActive ? 'bg-gray-600' : 'bg-gray-200'}`}>
                        <div
                          className={`h-full rounded-full transition-all ${isArchived ? (isActive ? 'bg-green-400' : 'bg-green-500') : isActive ? 'bg-blue-400' : 'bg-blue-500'}`}
                          style={{ width: `${Math.min(pct, 100)}%` }}
                        />
                      </div>
                      <span className={`shrink-0 text-xs font-medium tabular-nums ${isActive ? (isArchived ? 'text-green-400' : 'text-gray-300') : isArchived ? 'text-green-600' : 'text-gray-500'}`}>
                        {pct}%
                      </span>
                    </div>
                    <div className="flex items-center justify-between">
                      <span className={`text-xs tabular-nums ${isActive ? 'text-gray-400' : 'text-gray-400'}`}>{pkg.dataCount} 条数据</span>
                      {isArchived && (
                        <span className={`flex items-center gap-0.5 text-xs ${isActive ? 'text-green-400' : 'text-green-600'}`}>
                          <Archive size={9} />
                          已归档
                        </span>
                      )}
                    </div>
                  </button>
                  <button
                    type="button"
                    onClick={(event) => askDeletePkg(event, pkg)}
                    title="删除数据集"
                    className={`mr-2 mt-2.5 shrink-0 rounded p-1 transition-colors ${
                      isActive ? 'bg-red-600 text-white shadow-sm ring-1 ring-red-300/70 hover:bg-red-500' : 'text-red-500 hover:bg-red-50 hover:text-red-700'
                    }`}
                  >
                    <Trash2 size={11} />
                  </button>
                </div>
              );
            })}
          </div>
        </aside>

        <div className="flex min-w-0 flex-1 flex-col gap-2 overflow-hidden">
          <div className="flex min-h-0 flex-1 flex-col gap-2 overflow-hidden">
            <div className="flex shrink-0 items-center gap-2 rounded border border-gray-200 bg-white px-3 py-2">
              <div className="relative">
                <Search size={11} className="absolute left-2 top-1/2 -translate-y-1/2 text-gray-400" />
                <input
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  placeholder="搜索原文 / 正确文本"
                  className="w-44 rounded border border-gray-200 py-1 pl-7 pr-2 text-xs placeholder-gray-400 focus:border-gray-400 focus:outline-none"
                />
              </div>

              <FSel value={fStatus} onChange={(value) => setFStatus(value as ReviewStatus | 'all')}>
                <option value="all">全部状态</option>
                <option value="pending">待标注</option>
                <option value="confirmed">已确认</option>
              </FSel>

              <FSel value={fEncoding} onChange={setFEncoding} className="w-40 truncate">
                <option value="all">全部编码路径</option>
                {encodingOptions.map((encoding) => (
                  <option key={encoding} value={encoding}>
                    {encoding}
                  </option>
                ))}
              </FSel>

              {(search || fStatus !== 'all' || fEncoding !== 'all') && (
                <button type="button" onClick={() => { setSearch(''); setFStatus('all'); setFEncoding('all'); }} className="whitespace-nowrap text-xs text-gray-400 transition-colors hover:text-gray-600">
                  清除
                </button>
              )}

              {selected.size > 0 && (
                <div className="ml-2 flex items-center gap-1.5">
                  <span className="text-xs text-gray-500">已选 {selected.size} 条</span>
                  <button type="button" disabled={busy} onClick={saveSelected} className="flex items-center gap-1 rounded bg-green-600 px-2 py-0.5 text-xs text-white transition-colors hover:bg-green-700 disabled:opacity-40">
                    <CheckCircle size={10} />
                    确认
                  </button>
                  <button type="button" disabled={busy} onClick={resetSelected} className="flex items-center gap-1 rounded bg-gray-200 px-2 py-0.5 text-xs text-gray-700 transition-colors hover:bg-gray-300 disabled:opacity-40">
                    <RefreshCw size={10} />
                    重置
                  </button>
                </div>
              )}

              <span className="ml-auto flex shrink-0 items-center gap-1 text-xs text-gray-500">
                <Filter size={10} />
                <strong className="text-gray-800">{filtered.length}</strong> 条
              </span>

              <div className="flex items-center gap-3 border-l border-gray-200 pl-3">
                {(['pending', 'confirmed'] as ReviewStatus[]).map((status) => {
                  const cfg = STATUS_CFG[status];
                  const count = statusCounts[status];
                  return (
                    <span key={status} className={`flex items-center gap-1 text-xs ${cfg.cls}`}>
                      {cfg.icon}
                      <span className="font-medium tabular-nums">{count}</span>
                    </span>
                  );
                })}
              </div>
            </div>

            <div ref={virtualRows.scrollRef} onScroll={virtualRows.onScroll} className="flex-1 overflow-auto rounded border border-gray-200 bg-white">
              <table className="w-full table-fixed border-collapse text-xs">
                <colgroup>
                  <col className="w-[28%]" />
                  <col className="w-[28%]" />
                  <col className="w-[16%]" />
                  <col className="w-[8%]" />
                  <col className="w-[16%]" />
                  <col className="w-[4%]" />
                </colgroup>
                <thead className="sticky top-0 z-10 bg-gray-50">
                  <tr>
                    <th className="border-b border-gray-200 px-3 py-2.5 text-left text-xs font-semibold text-gray-500">原文</th>
                    <th className="border-b border-gray-200 px-3 py-2.5 text-left text-xs font-semibold text-gray-500">正确文本</th>
                    <th className="whitespace-nowrap border-b border-gray-200 px-3 py-2.5 text-left text-xs font-semibold text-gray-500">来源</th>
                    <th className="whitespace-nowrap border-b border-gray-200 px-3 py-2.5 text-left text-xs font-semibold text-gray-500">状态</th>
                    <th className="whitespace-nowrap border-b border-gray-200 px-3 py-2.5 text-left text-xs font-semibold text-gray-500">编码路径</th>
                    <th className="border-b border-gray-200 px-2 py-2.5 text-center">
                      <input
                        type="checkbox"
                        className="cursor-pointer accent-gray-800"
                        checked={allChecked}
                        ref={(element) => {
                          if (element) element.indeterminate = someChecked;
                        }}
                        onChange={toggleAll}
                      />
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {virtualRows.topSpacerHeight > 0 && (
                    <tr aria-hidden="true">
                      <td colSpan={6} style={{ height: virtualRows.topSpacerHeight, padding: 0, border: 0 }} />
                    </tr>
                  )}
                  {virtualRows.visibleItems.map((cluster) => {
                    const sc = STATUS_CFG[cluster.status];
                    const isChecked = selected.has(cluster.id);
                    const isActive = activeClusterBase?.id === cluster.id;
                    return (
                      <tr
                        key={cluster.id}
                        onClick={() => setActiveClusterId(isActive ? null : cluster.id)}
                        style={{ height: TABLE_ROW_HEIGHT }}
                        className={`cursor-pointer border-b border-gray-100 transition-colors ${
                          isActive ? 'border-l-2 border-l-amber-400 bg-amber-50 hover:bg-amber-50' : isChecked ? 'bg-blue-50 hover:bg-blue-50' : 'hover:bg-gray-50'
                        }`}
                      >
                        <td className="overflow-hidden px-3 py-2">
                          <span className="line-clamp-2 break-all font-mono leading-tight text-gray-700">{cluster.originalText}</span>
                        </td>
                        <td className="overflow-hidden px-3 py-2" onClick={(event) => event.stopPropagation()}>
                          <CorrectTextCell cluster={cluster} onChange={(patch) => updateCluster(cluster, patch)} />
                        </td>
                        <td className="whitespace-nowrap px-3 py-2" onClick={(event) => event.stopPropagation()}>
                          <ModeBtns mode={cluster.correctTextMode} onChange={(mode) => updateCluster(cluster, { correctTextMode: mode })} />
                        </td>
                        <td className="whitespace-nowrap px-3 py-2">
                          <span className={`flex items-center gap-1 ${sc.cls}`}>
                            {sc.icon}
                            {sc.label}
                          </span>
                        </td>
                        <td className="overflow-hidden px-3 py-2 font-mono text-gray-500">
                          <span className="block truncate">{cluster.encodingPath}</span>
                        </td>
                        <td className="px-2 py-2 text-center" onClick={(event) => event.stopPropagation()}>
                          <input type="checkbox" className="cursor-pointer accent-gray-800" checked={isChecked} onChange={() => toggle(cluster.id)} />
                        </td>
                      </tr>
                    );
                  })}
                  {virtualRows.bottomSpacerHeight > 0 && (
                    <tr aria-hidden="true">
                      <td colSpan={6} style={{ height: virtualRows.bottomSpacerHeight, padding: 0, border: 0 }} />
                    </tr>
                  )}
                  {filtered.length === 0 && (
                    <tr>
                      <td colSpan={6} className="py-16 text-center text-xs text-gray-400">
                        <Info size={14} className="mr-1.5 inline opacity-60" />
                        {activePkg ? '该数据集暂无符合条件的记录' : '请在左侧选择一个数据集'}
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      </div>

      {confirmDel && (
        <div className="fixed inset-0 z-50 flex items-center justify-center">
          <div className="absolute inset-0 bg-black/40" onClick={() => setConfirmDel(null)} />
          <div className="relative z-10 w-80 rounded-lg bg-white p-5 shadow-xl">
            <button type="button" onClick={() => setConfirmDel(null)} className="absolute right-3 top-3 rounded p-1 text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600">
              <X size={14} />
            </button>
            <div className="mb-4 flex items-start gap-3">
              <div className="mt-0.5 flex h-8 w-8 flex-shrink-0 items-center justify-center rounded-full bg-red-50">
                <AlertTriangle size={16} className="text-red-500" />
              </div>
              <div>
                <div className="mb-1 text-sm font-semibold text-gray-900">删除原始数据集</div>
                <p className="text-xs leading-relaxed text-gray-500">
                  即将从本地磁盘删除数据集 <span className="font-medium text-gray-800">「{confirmDel.pkg.name}」</span>
                  及其标注、训练和特征文件。此操作不会移动到 .trash。
                </p>
              </div>
            </div>
            <div className="flex items-center justify-end gap-2">
              <button type="button" onClick={() => setConfirmDel(null)} className="rounded border border-gray-200 px-3 py-1.5 text-xs font-medium text-gray-600 transition-colors hover:bg-gray-50">
                取消
              </button>
              <button type="button" onClick={confirmDeletePkg} className="rounded bg-red-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-red-700">
                确认删除
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

type CorrectTextPatch = {
  correctTextMode?: CorrectTextMode;
  selectedCandidate?: string;
  manualText?: string;
};

function CorrectTextCell({ cluster, onChange }: { cluster: DBTextCluster; onChange: (patch: CorrectTextPatch) => void }) {
  const { id, correctTextMode, originalText, candidateTexts, selectedCandidate, manualText } = cluster;
  const [draft, setDraft] = useState(manualText);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    setDraft(manualText);
  }, [id, manualText]);

  useEffect(() => {
    if (correctTextMode === 'manual') inputRef.current?.focus();
  }, [correctTextMode]);

  const commitManual = () => onChange({ manualText: draft });

  return (
    <div className="min-w-0">
      {correctTextMode === 'original' && (
        <p title={originalText} className="line-clamp-2 break-all font-mono text-xs leading-relaxed text-gray-500">
          {originalText}
        </p>
      )}

      {correctTextMode === 'candidate' &&
        (candidateTexts.length > 1 ? (
          <div className="relative">
            <select
              value={selectedCandidate}
              onChange={(event) => onChange({ selectedCandidate: event.target.value })}
              className="w-full cursor-pointer appearance-none rounded border border-blue-200 bg-blue-50 py-1 pl-2 pr-6 text-xs text-gray-900 focus:border-blue-400 focus:outline-none"
            >
              {candidateTexts.map((text) => (
                <option key={text} value={text}>
                  {text}
                </option>
              ))}
            </select>
            <ChevronDown size={10} className="pointer-events-none absolute right-1.5 top-1/2 -translate-y-1/2 text-blue-400" />
          </div>
        ) : (
          <p title={selectedCandidate} className="line-clamp-2 break-all text-xs leading-relaxed text-gray-900">
            {selectedCandidate}
          </p>
        ))}

      {correctTextMode === 'manual' && (
        <div className="relative">
          <input
            ref={inputRef}
            value={draft}
            onChange={(event) => {
              const next = event.target.value;
              setDraft(next);
              onChange({ manualText: next });
            }}
            onBlur={commitManual}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                commitManual();
                inputRef.current?.blur();
              }
            }}
            placeholder="输入正确文本..."
            title={draft}
            className="w-full rounded border border-amber-300 bg-amber-50 py-1 pl-2 pr-6 text-xs text-gray-900 placeholder-gray-300 focus:border-amber-500 focus:outline-none"
          />
          <Pencil size={10} className="pointer-events-none absolute right-1.5 top-1/2 -translate-y-1/2 text-amber-400" />
        </div>
      )}
    </div>
  );
}

function ModeBtns({ mode, onChange }: { mode: CorrectTextMode; onChange: (mode: CorrectTextMode) => void }) {
  return (
    <div className="inline-flex items-center gap-1">
      {(['original', 'candidate', 'manual'] as CorrectTextMode[]).map((item) => {
        const active = mode === item;
        const cls = active ? 'bg-blue-600 text-white shadow-sm' : 'bg-gray-100 text-gray-400 hover:bg-gray-200 hover:text-gray-600';
        const label = item === 'original' ? '原文' : item === 'candidate' ? '候选' : '手动';
        return (
          <button
            key={item}
            type="button"
            onClick={() => onChange(item)}
            className={`flex h-8 min-w-11 items-center justify-center rounded-lg px-2 text-base font-semibold leading-none transition-colors ${cls}`}
          >
            {label}
          </button>
        );
      })}
    </div>
  );
}

function FSel({ value, onChange, children, className = '' }: { value: string; onChange: (value: string) => void; children: ReactNode; className?: string }) {
  return (
    <div className="relative">
      <select
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className={`appearance-none rounded border border-gray-200 bg-white px-2 py-1 pr-6 text-xs text-gray-700 focus:border-gray-400 focus:outline-none ${className}`}
      >
        {children}
      </select>
      <ChevronDown size={11} className="pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 text-gray-400" />
    </div>
  );
}

function toggleSet(current: Set<string>, id: string) {
  const next = new Set(current);
  next.has(id) ? next.delete(id) : next.add(id);
  return next;
}
