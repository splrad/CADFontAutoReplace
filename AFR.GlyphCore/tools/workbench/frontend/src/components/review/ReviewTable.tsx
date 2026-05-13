/**
 * AFR 文枢工作台复核主表格
 * 
 * 修复原 main.jsx 中 reviewedMap/reviewed prop 名不匹配 Bug。
 * 包含表头 + 虚拟滚动列表。
 */

import { useRef, useState, useEffect, useCallback } from 'react';
import { ReviewRow } from '@/components/review/ReviewRow';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow
} from '@/components/ui/table';
import { reviewedForGroup, type ReviewGroup, type CandidateRecord, type ReviewedRecord } from '@/lib/utils';
import { cn } from '@/lib/utils';

interface ReviewTableProps {
  groups: ReviewGroup[];
  /** 已审核记录映射表（统一命名 reviewedMap，修复旧代码 prop 不匹配 Bug） */
  reviewedMap: { [key: string]: ReviewedRecord };
  recordsById: Map<string, CandidateRecord>;
  selectedReviewGroupIdSet: Set<string>;
  reviewEdits: { [key: string]: any };
  onToggleReviewSelection: (id: string) => void;
  onUpdateReviewEdit: (id: string, patch: { [key: string]: any }) => void;
}

const ROW_HEIGHT = 88;
const OVERSCAN = 12;

export function ReviewTable({
  groups,
  reviewedMap,
  recordsById,
  selectedReviewGroupIdSet,
  reviewEdits,
  onToggleReviewSelection,
  onUpdateReviewEdit
}: ReviewTableProps) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const rafRef = useRef(0);
  const [scrollTop, setScrollTop] = useState(0);
  const [viewportHeight, setViewportHeight] = useState(600);

  const handleScroll = useCallback((e: React.UIEvent<HTMLDivElement>) => {
    const top = e.currentTarget.scrollTop;
    const height = e.currentTarget.clientHeight || 600;
    cancelAnimationFrame(rafRef.current);
    rafRef.current = requestAnimationFrame(() => {
      setScrollTop(top);
      setViewportHeight(height);
    });
  }, []);

  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = 0;
      setScrollTop(0);
      setViewportHeight(scrollRef.current.clientHeight || 600);
    }
  }, [groups.length]);

  useEffect(() => () => cancelAnimationFrame(rafRef.current), []);

  if (groups.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center flex-1 gap-2 text-[var(--color-text-soft)]">
        <strong className="text-body-lg text-[var(--color-text)]">当前筛选没有数据</strong>
        <span className="text-body">调整搜索或筛选条件后继续复核。</span>
      </div>
    );
  }

  const virtualStart = Math.max(0, Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN);
  const virtualCount = Math.ceil(viewportHeight / ROW_HEIGHT) + OVERSCAN * 2;
  const virtualEnd = Math.min(groups.length, virtualStart + virtualCount);
  const virtualGroups = groups.slice(virtualStart, virtualEnd);
  const topSpacer = virtualStart * ROW_HEIGHT;
  const bottomSpacer = Math.max(0, (groups.length - virtualEnd) * ROW_HEIGHT);

  const thClass = cn(
    'px-2 py-2 text-left text-caption font-medium',
    'text-[var(--color-text-muted)] bg-[var(--color-canvas)]',
    'border-b border-[var(--color-line)] sticky top-0 z-10'
  );

  return (
    <div
      ref={scrollRef}
      className="min-h-0 flex-1 overflow-auto scrollbar-thin"
      onScroll={handleScroll}
    >
      <Table className="table-fixed">
        <colgroup>
          <col className="w-24" />
          <col className="w-52" />
          <col />
          <col className="w-28" />
          <col className="w-16" />
          <col className="w-10" />
        </colgroup>
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <TableHead className={thClass}>状态</TableHead>
            <TableHead className={thClass}>原文（文本簇）</TableHead>
            <TableHead className={thClass}>正确文本</TableHead>
            <TableHead className={thClass}>修复方式</TableHead>
            <TableHead className={cn(thClass, 'text-right')}>数量</TableHead>
            <TableHead className={cn(thClass, 'text-center')}>☑</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {topSpacer > 0 && (
            <TableRow aria-hidden>
              <TableCell colSpan={6} style={{ height: topSpacer }} />
            </TableRow>
          )}
          {virtualGroups.map((group, offset) => {
            const idx = virtualStart + offset;
            const reviewed = reviewedForGroup(group, reviewedMap);
            return (
              <ReviewRow
                key={group.id}
                group={group}
                index={idx}
                reviewed={reviewed}
                recordsById={recordsById}
                selected={selectedReviewGroupIdSet.has(group.id)}
                edit={reviewEdits[group.id] || {}}
                onToggleSelection={onToggleReviewSelection}
                onUpdateEdit={onUpdateReviewEdit}
              />
            );
          })}
          {bottomSpacer > 0 && (
            <TableRow aria-hidden>
              <TableCell colSpan={6} style={{ height: bottomSpacer }} />
            </TableRow>
          )}
        </TableBody>
      </Table>
    </div>
  );
}
