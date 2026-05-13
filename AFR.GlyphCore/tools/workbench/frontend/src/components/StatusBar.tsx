import { AlertTriangle, CheckCircle2, CircleDot, Loader2 } from 'lucide-react';
import { useFilteredGroups, useVisibleSelection } from '@/hooks/useFilters';
import { cn } from '@/lib/utils';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';

export function StatusBar() {
  const { message, error, busy, activeTab, groups } = useWorkbenchStore();
  const filteredGroups = useFilteredGroups();
  const { selectedReviewVisibleCount } = useVisibleSelection();

  const Icon = error ? AlertTriangle : busy ? Loader2 : message ? CheckCircle2 : CircleDot;
  const statusText = busy
    ? '处理中...'
    : error ||
      message ||
      (activeTab === 'review' ? `已选择 ${selectedReviewVisibleCount} 个文本簇` : '就绪');
  const visibleCount = filteredGroups.length;
  const clusterCount = (groups.clusters || groups.groups || []).length;

  return (
    <footer className="flex h-8 shrink-0 items-center gap-4 overflow-hidden border-t border-[var(--color-line)] bg-[var(--color-canvas)] px-4 text-caption text-[var(--color-text-muted)]">
      <span
        className={cn(
          'inline-flex min-w-0 items-center gap-2 truncate',
          error && 'text-[var(--color-unsafe)]',
          busy && 'text-[var(--color-warn)]',
          message && !error && !busy && 'text-[var(--color-keep)]'
        )}
      >
        <Icon className={cn('h-3.5 w-3.5 shrink-0', busy && 'animate-spin')} aria-hidden />
        <span className="truncate">{statusText}</span>
      </span>
      {activeTab === 'review' && (
        <>
          <span className="shrink-0">显示 {visibleCount} / {clusterCount}</span>
        <span className="min-w-0 truncate max-[640px]:hidden">保存会按文本簇展开并进入训练数据集</span>
        </>
      )}
      {activeTab === 'dataset' && (
        <span className="min-w-0 truncate">删除训练记录会回流复核队列并移除 Feature 行</span>
      )}
      {activeTab !== 'review' && activeTab !== 'dataset' && (
        <span className="min-w-0 truncate">数据、训练集和模型仅保存在本机 AFR.GlyphCore 目录</span>
      )}
    </footer>
  );
}
