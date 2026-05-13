import { Save, SendHorizonal } from 'lucide-react';
import { useMemo } from 'react';
import { Button } from '@/components/ui/button';
import { Panel, PanelHeader, Metric } from '@/components/ui/Panel';
import { ReviewCommandBar } from '@/components/review/ReviewCommandBar';
import { ReviewTable } from '@/components/review/ReviewTable';
import { useFilteredGroups, useVisibleSelection } from '@/hooks/useFilters';
import { compactText, riskLevel, riskLabel } from '@/lib/utils';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';

export function ReviewWorkspace() {
  const {
    app,
    reviewEdits,
    toggleReviewSelection,
    updateReviewEdit,
    saveSelectedReviews,
    saveAllVisibleReviews,
    batchProgress,
    busy
  } = useWorkbenchStore();

  const filteredGroups = useFilteredGroups();
  const { selectedReviewGroupIdSet, selectedVisibleGroupIds } = useVisibleSelection();

  const reviewed = app?.data?.reviewed || {};
  const records = app?.data?.records || [];
  const recordsById = useMemo(
    () => new Map(records.map((record) => [record.groupId, record])),
    [records]
  );
  const selectedGroups = useMemo(
    () => filteredGroups.filter((group) => selectedReviewGroupIdSet.has(group.id)),
    [filteredGroups, selectedReviewGroupIdSet]
  );
  const activeGroup = selectedGroups[0] || filteredGroups[0];
  const pendingCount = filteredGroups.filter((group) => Number(group.unreviewedCount || 0) > 0).length;
  const reviewedVisible = Math.max(0, filteredGroups.length - pendingCount);
  const highRisk = filteredGroups.filter((group) => riskLevel(group) === 'high').length;

  function handleSaveSelected() {
    saveSelectedReviews(selectedVisibleGroupIds);
  }

  function handleSaveAll() {
    saveAllVisibleReviews(filteredGroups);
  }

  return (
    <div className="grid h-full min-h-0 grid-cols-[320px_minmax(0,1fr)_320px] gap-4 p-4 max-[1100px]:grid-cols-[280px_minmax(0,1fr)] max-[760px]:grid-cols-1 max-[760px]:overflow-auto">
      <Panel className="min-h-0 overflow-hidden max-[760px]:min-h-[520px]">
        <ReviewCommandBar />
      </Panel>

      <Panel className="flex min-h-0 flex-col overflow-hidden max-[760px]:min-h-[620px]">
        <PanelHeader
          eyebrow="Cluster Table"
          title="文本簇复核"
          description="每一行代表一组同文本、同候选、同推荐动作的 DBText。保存时仍写回每个实体。"
          actions={
            <div className="hidden items-center gap-2 lg:flex">
              <Metric label="待审" value={pendingCount} tone="warn" className="py-2" />
              <Metric label="已审" value={reviewedVisible} tone="keep" className="py-2" />
              <Metric label="高风险" value={highRisk} tone="unsafe" className="py-2" />
            </div>
          }
        />
        <ReviewTable
          groups={filteredGroups}
          reviewedMap={reviewed}
          recordsById={recordsById}
          selectedReviewGroupIdSet={selectedReviewGroupIdSet}
          reviewEdits={reviewEdits}
          onToggleReviewSelection={toggleReviewSelection}
          onUpdateReviewEdit={updateReviewEdit}
        />
        {batchProgress && (
          <div className="shrink-0 border-t border-[var(--color-line)] bg-[var(--color-ai-soft)] px-4 py-2 text-body-sm text-[var(--color-ai)]">
            {batchProgress}
          </div>
        )}
      </Panel>

      <Panel className="flex min-h-0 flex-col overflow-hidden max-[1100px]:col-span-2 max-[760px]:col-span-1">
        <PanelHeader
          eyebrow="Batch Action"
          title="批量确认"
          description="优先保存已勾选文本簇；确认全部仅作用于当前筛选结果。"
        />
        <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-auto p-4">
          <div className="grid grid-cols-2 gap-2">
            <Metric label="当前筛选" value={filteredGroups.length} tone="ai" />
            <Metric label="已勾选" value={selectedVisibleGroupIds.length} tone="keep" />
          </div>

          <div className="rounded-[var(--radius-lg)] bg-[var(--color-warn-soft)] p-4 text-body-sm text-[var(--color-warn)]">
            <strong className="block text-body text-[var(--color-text)]">保存规则</strong>
            <span>当前行的正确文本和修复方式会应用到该文本簇下的全部 DBText 实体。</span>
          </div>

          {activeGroup && (
            <div className="rounded-[var(--radius-lg)] border border-[var(--color-line)] bg-[var(--color-surface-2)] p-4">
              <div className="text-caption text-[var(--color-text-muted)]">
                当前参考项 / {riskLabel(riskLevel(activeGroup))}
              </div>
              <div className="mt-2 break-all text-body-lg font-semibold text-[var(--color-text)]">
                {compactText(activeGroup.currentText || activeGroup.sourcePatternLabel || '--', 96)}
              </div>
              <div className="mt-3 grid gap-2 text-body-sm text-[var(--color-text-soft)]">
                <span>推荐：{compactText(activeGroup.candidateText || '--', 80)}</span>
                <span>来源：{activeGroup.candidateSource || activeGroup.encodingPath || '--'}</span>
                <span>影响数量：{Number(activeGroup.impactCount || activeGroup.count || 0)}</span>
              </div>
            </div>
          )}

          <div className="mt-auto grid gap-2">
            <Button
              variant="secondary"
              icon={Save}
              disabled={busy || selectedVisibleGroupIds.length <= 0}
              onClick={handleSaveSelected}
            >
              保存已选 ({selectedVisibleGroupIds.length})
            </Button>
            <Button
              variant="primary"
              icon={SendHorizonal}
              disabled={busy || filteredGroups.length <= 0}
              onClick={handleSaveAll}
            >
              应用到当前筛选 {filteredGroups.length} 条
            </Button>
          </div>
        </div>
      </Panel>
    </div>
  );
}
