import { useDeferredValue, useMemo, useRef, useState, type MouseEvent, type ReactNode } from 'react';
import { AlertTriangle, Archive, ChevronDown, Database, Download, Info, Search, Trash2, Upload, X } from 'lucide-react';
import { modeClass, modeLabel, packageViews, trainingRecordViews } from '@/lib/boltAdapters';
import { useVirtualRows } from '@/hooks/useVirtualRows';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';
import type { CorrectTextMode, DataPackage } from '@/types/bolt';

interface ConfirmModal {
  type: 'pkg' | 'records';
  pkg?: DataPackage;
  recordIds?: string[];
}

const TABLE_ROW_HEIGHT = 44;

export default function TrainingDatasetPage() {
  const { app, busy, deletePackage, deleteTrainingRecords, importTrainingDataset } = useWorkbenchStore();
  const fileRef = useRef<HTMLInputElement>(null);
  const records = useMemo(() => trainingRecordViews(app), [app]);
  const packages = useMemo(() => packageViews(app, records), [app, records]);
  const [search, setSearch] = useState('');
  const [fMode, setFMode] = useState<CorrectTextMode | 'all'>('all');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [activePkg, setActivePkg] = useState<string | null>(null);
  const [pkgSearch, setPkgSearch] = useState('');
  const [confirm, setConfirm] = useState<ConfirmModal | null>(null);
  const deferredSearch = useDeferredValue(search);
  const deferredPkgSearch = useDeferredValue(pkgSearch);

  const handleSelectPkg = (pkg: DataPackage) => {
    setActivePkg(activePkg === pkg.id ? null : pkg.id);
    setSelected(new Set());
    setSearch('');
    setFMode('all');
  };

  const askDeletePkg = (event: MouseEvent, pkg: DataPackage) => {
    event.stopPropagation();
    setConfirm({ type: 'pkg', pkg });
  };

  const askDeleteRecords = () => {
    setConfirm({ type: 'records', recordIds: [...selected] });
  };

  const confirmDelete = () => {
    if (!confirm) return;
    if (confirm.type === 'pkg' && confirm.pkg) {
      void deletePackage(confirm.pkg.id);
      if (activePkg === confirm.pkg.id) setActivePkg(null);
      setSelected(new Set());
    } else if (confirm.type === 'records' && confirm.recordIds) {
      void deleteTrainingRecords(confirm.recordIds);
      setSelected(new Set());
    }
    setConfirm(null);
  };

  const filtered = useMemo(
    () =>
      records.filter((record) => {
        if (activePkg && record.dataPackageId !== activePkg) return false;
        if (deferredSearch && !record.originalText.includes(deferredSearch) && !record.correctText.includes(deferredSearch)) return false;
        if (fMode !== 'all' && record.correctTextMode !== fMode) return false;
        return true;
      }),
    [activePkg, deferredSearch, fMode, records]
  );
  const virtualRows = useVirtualRows(filtered, TABLE_ROW_HEIGHT, 14);

  const toggle = (id: string) => setSelected((current) => toggleSet(current, id));
  const allChecked = filtered.length > 0 && filtered.every((record) => selected.has(record.id));
  const someChecked = !allChecked && filtered.some((record) => selected.has(record.id));
  const toggleAll = () => setSelected(allChecked ? new Set() : new Set(filtered.map((record) => record.id)));

  const counts = useMemo(() => {
    const result: Record<string, number> = {};
    records.forEach((record) => {
      result[record.correctTextMode] = (result[record.correctTextMode] || 0) + 1;
    });
    return result;
  }, [records]);

  const pkgCounts = useMemo(() => {
    const result: Record<string, number> = {};
    records.forEach((record) => {
      result[record.dataPackageId] = (result[record.dataPackageId] || 0) + 1;
    });
    return result;
  }, [records]);

  const visiblePkgs = useMemo(() => {
    const query = deferredPkgSearch.toLowerCase();
    return packages.filter((pkg) => pkg.name.toLowerCase().includes(query) || pkg.dwgFile.toLowerCase().includes(query));
  }, [deferredPkgSearch, packages]);

  const importFile = (file: File | undefined) => {
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      void importTrainingDataset(String(reader.result || ''));
    };
    reader.readAsText(file, 'utf-8');
  };

  return (
    <>
      <div className="flex h-full gap-3 overflow-hidden">
        <aside className="flex w-52 shrink-0 flex-col overflow-hidden rounded border border-gray-200 bg-white">
          <div className="flex shrink-0 items-center gap-2 border-b border-gray-100 px-3 py-2.5">
            <Database size={13} className="text-gray-500" />
            <span className="text-xs font-semibold text-gray-700">训练数据集</span>
            <span className="ml-auto text-xs text-gray-400">{packages.length} 个</span>
          </div>

          <div className="shrink-0 border-b border-gray-100 px-2.5 py-2">
            <div className="relative">
              <Search size={11} className="pointer-events-none absolute left-2 top-1/2 -translate-y-1/2 text-gray-400" />
              <input
                type="text"
                placeholder="搜索数据集..."
                value={pkgSearch}
                onChange={(event) => setPkgSearch(event.target.value)}
                className="w-full rounded border border-gray-200 bg-gray-50 py-1.5 pl-6 pr-2 text-xs focus:border-gray-400 focus:outline-none"
              />
            </div>
          </div>

          <div className="flex-1 overflow-y-auto">
            {visiblePkgs.length === 0 && <div className="py-8 text-center text-xs text-gray-400">无匹配数据集</div>}
            {visiblePkgs.map((pkg) => {
              const cnt = pkgCounts[pkg.id] || 0;
              const total = pkg.inTrainingSet || 1;
              const pct = Math.round((cnt / total) * 100);
              const isArchived = pkg.status === 'archived';
              const isActive = activePkg === pkg.id;
              return (
                <div key={pkg.id} className={`flex items-start border-b border-gray-100 transition-colors ${isActive ? 'bg-gray-900' : 'bg-white hover:bg-gray-50'}`}>
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
                        {cnt} 条
                      </span>
                    </div>
                    <div className="flex items-center justify-between">
                      <span className={`text-xs tabular-nums ${isActive ? 'text-gray-400' : 'text-gray-400'}`}>{pkg.inTrainingSet} 条入集</span>
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
                      isActive ? 'text-red-300 hover:bg-red-500/20 hover:text-red-100' : 'text-gray-300 hover:bg-red-50 hover:text-red-600'
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
          <div className="grid shrink-0 grid-cols-4 gap-2">
            <MCard label="总记录数" value={records.length} cls="text-gray-900" />
            <MCard label="原文" value={counts.original || 0} cls="text-gray-600" />
            <MCard label="候选" value={counts.candidate || 0} cls="text-blue-700" />
            <MCard label="手动" value={counts.manual || 0} cls="text-amber-700" />
          </div>

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

            <FSel value={fMode} onChange={(value) => setFMode(value as CorrectTextMode | 'all')}>
              <option value="all">全部来源</option>
              <option value="original">原文</option>
              <option value="candidate">候选</option>
              <option value="manual">手动</option>
            </FSel>

            <span className="rounded bg-gray-100 px-2 py-1 text-xs text-gray-600">
              共 <strong className="text-gray-900">{filtered.length}</strong> 条
            </span>

            <div className="ml-auto flex items-center gap-2">
              <button
                type="button"
                disabled={selected.size === 0 || busy}
                onClick={askDeleteRecords}
                className="flex items-center gap-1.5 rounded border border-red-200 px-3 py-1.5 text-xs font-medium text-red-600 transition-colors hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
              >
                <Trash2 size={12} />
                删除 ({selected.size})
              </button>
              <button type="button" disabled={busy} onClick={() => fileRef.current?.click()} className="flex items-center gap-1.5 rounded border border-gray-200 px-3 py-1.5 text-xs font-medium text-gray-600 transition-colors hover:bg-gray-50 disabled:opacity-40">
                <Upload size={12} />
                导入
              </button>
              <button type="button" onClick={() => { window.location.href = '/api/training-dataset/export?format=csv'; }} className="flex items-center gap-1.5 rounded border border-gray-200 px-3 py-1.5 text-xs font-medium text-gray-600 transition-colors hover:bg-gray-50">
                <Download size={12} />
                导出 CSV
              </button>
              <input
                ref={fileRef}
                type="file"
                accept=".jsonl,application/jsonl,text/plain"
                className="hidden"
                onChange={(event) => {
                  importFile(event.target.files?.[0]);
                  event.target.value = '';
                }}
              />
            </div>
          </div>

          <div ref={virtualRows.scrollRef} onScroll={virtualRows.onScroll} className="flex-1 overflow-auto rounded border border-gray-200 bg-white">
            <table className="w-full table-fixed border-collapse text-xs">
              <colgroup>
                <col className="w-[36%]" />
                <col className="w-[36%]" />
                <col className="w-[10%]" />
                <col className="w-[14%]" />
                <col className="w-[4%]" />
              </colgroup>
              <thead className="sticky top-0 z-10 bg-gray-50">
                <tr>
                  <th className="border-b border-gray-200 px-3 py-2.5 text-left text-xs font-semibold text-gray-500">原文</th>
                  <th className="border-b border-gray-200 px-3 py-2.5 text-left text-xs font-semibold text-gray-500">正确文本</th>
                  <th className="whitespace-nowrap border-b border-gray-200 px-3 py-2.5 text-left text-xs font-semibold text-gray-500">来源</th>
                  <th className="whitespace-nowrap border-b border-gray-200 px-3 py-2.5 text-left text-xs font-semibold text-gray-500">入集时间</th>
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
                    <td colSpan={5} style={{ height: virtualRows.topSpacerHeight, padding: 0, border: 0 }} />
                  </tr>
                )}
                {virtualRows.visibleItems.map((record) => {
                  const isChecked = selected.has(record.id);
                  return (
                    <tr key={record.id} style={{ height: TABLE_ROW_HEIGHT }} className={`border-b border-gray-100 transition-colors hover:bg-gray-50 ${isChecked ? 'bg-blue-50 hover:bg-blue-50' : ''}`}>
                      <td className="overflow-hidden px-3 py-2">
                        <span className="line-clamp-2 break-all font-mono leading-tight text-gray-700">{record.originalText}</span>
                      </td>
                      <td className="overflow-hidden px-3 py-2">
                        <span className="line-clamp-2 break-all leading-tight text-gray-900">{record.correctText}</span>
                      </td>
                      <td className="whitespace-nowrap px-3 py-2">
                        <span className={`inline-block rounded px-1.5 py-0.5 text-xs font-medium ${modeClass(record.correctTextMode)}`}>{modeLabel(record.correctTextMode)}</span>
                      </td>
                      <td className="whitespace-nowrap px-3 py-2 tabular-nums text-gray-400">{record.addedAt}</td>
                      <td className="px-2 py-2 text-center">
                        <input type="checkbox" className="cursor-pointer accent-gray-800" checked={isChecked} onChange={() => toggle(record.id)} />
                      </td>
                    </tr>
                  );
                })}
                {virtualRows.bottomSpacerHeight > 0 && (
                  <tr aria-hidden="true">
                    <td colSpan={5} style={{ height: virtualRows.bottomSpacerHeight, padding: 0, border: 0 }} />
                  </tr>
                )}
                {filtered.length === 0 && (
                  <tr>
                    <td colSpan={5} className="py-16 text-center text-xs text-gray-400">
                      <Info size={14} className="mr-1.5 inline opacity-60" />
                      无匹配记录
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      </div>

      {confirm && (
        <div className="fixed inset-0 z-50 flex items-center justify-center">
          <div className="absolute inset-0 bg-black/40" onClick={() => setConfirm(null)} />
          <div className="relative z-10 w-80 rounded-lg bg-white p-5 shadow-xl">
            <button type="button" onClick={() => setConfirm(null)} className="absolute right-3 top-3 rounded p-1 text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600">
              <X size={14} />
            </button>

            <div className="mb-4 flex items-start gap-3">
              <div className="mt-0.5 flex h-8 w-8 flex-shrink-0 items-center justify-center rounded-full bg-red-50">
                <AlertTriangle size={16} className="text-red-500" />
              </div>
              <div>
                <div className="mb-1 text-sm font-semibold text-gray-900">{confirm.type === 'pkg' ? '删除原始数据集' : '删除训练记录'}</div>
                {confirm.type === 'pkg' && confirm.pkg && (
                  <p className="text-xs leading-relaxed text-gray-500">
                    即将从本地磁盘删除数据集 <span className="font-medium text-gray-800">「{confirm.pkg.name}」</span>
                    及其所有训练记录（共 {pkgCounts[confirm.pkg.id] || 0} 条）和特征文件。此操作不会移动到 .trash。
                  </p>
                )}
                {confirm.type === 'records' && (
                  <p className="text-xs leading-relaxed text-gray-500">
                    即将删除已选中的 <span className="font-medium text-gray-800">{confirm.recordIds?.length} 条</span>训练记录，并回流到复核队列。
                  </p>
                )}
              </div>
            </div>

            <div className="flex items-center justify-end gap-2">
              <button type="button" onClick={() => setConfirm(null)} className="rounded border border-gray-200 px-3 py-1.5 text-xs font-medium text-gray-600 transition-colors hover:bg-gray-50">
                取消
              </button>
              <button type="button" onClick={confirmDelete} className="rounded bg-red-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-red-700">
                确认删除
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

function MCard({ label, value, cls }: { label: string; value: number; cls: string }) {
  return (
    <div className="rounded border border-gray-200 bg-white p-3">
      <div className="mb-1 text-xs text-gray-500">{label}</div>
      <div className={`text-2xl font-bold leading-none ${cls}`}>{value.toLocaleString()}</div>
    </div>
  );
}

function FSel({ value, onChange, children }: { value: string; onChange: (value: string) => void; children: ReactNode }) {
  return (
    <div className="relative">
      <select
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="appearance-none rounded border border-gray-200 bg-white px-2 py-1 pr-6 text-xs text-gray-700 focus:border-gray-400 focus:outline-none"
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
