/**
 * AFR 文枢工作台筛选逻辑 hooks
 * 
 * 从 main.jsx 提取 filteredGroups、filterOptions、selectedVisibleGroupIds 等派生状态。
 */

import { useMemo } from 'react';
import {
  clusterList,
  riskLevel,
  optionValues,
  type ReviewGroup
} from '@/lib/utils';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';

/**
 * 筛选后的复核组列表
 */
export function useFilteredGroups(): ReviewGroup[] {
  const { groups, query, filter, riskFilter, layerFilter, fontFilter, encodingFilter } =
    useWorkbenchStore();

  return useMemo(() => {
    const q = query.trim().toLowerCase();
    return clusterList(groups).filter((group) => {
      const variants = (group.sourceTextVariants || [])
        .map((item) => item.text)
        .join(' ');
      const layer = group.context?.layer || '';
      const font =
        group.context?.textStyleName || group.context?.textStyleFileName || '';
      const encoding = group.encodingPath || group.candidateSource || '';
      const text = [
        group.sourcePatternLabel,
        group.currentText,
        variants,
        group.candidateText,
        group.candidateSource,
        group.encodingPath,
        layer,
        font
      ]
        .join(' ')
        .toLowerCase();

      if (q && !text.includes(q)) return false;
      if (filter === 'pending' && (group.unreviewedCount ?? 0) <= 0) return false;
      if (filter === 'reviewed' && group.reviewStatus !== 'complete') return false;
      if (riskFilter !== 'all' && riskLevel(group) !== riskFilter) return false;
      if (layerFilter !== 'all' && layer !== layerFilter) return false;
      if (fontFilter !== 'all' && font !== fontFilter) return false;
      if (encodingFilter !== 'all' && encoding !== encodingFilter) return false;

      return true;
    });
  }, [groups, query, filter, riskFilter, layerFilter, fontFilter, encodingFilter]);
}

/**
 * 筛选器选项值
 */
export function useFilterOptions(): {
  layers: string[];
  fonts: string[];
  encodings: string[];
} {
  const { groups } = useWorkbenchStore();

  return useMemo(() => {
    const allGroups = clusterList(groups);
    return {
      layers: optionValues(allGroups, (group) => group.context?.layer),
      fonts: optionValues(
        allGroups,
        (group) =>
          group.context?.textStyleName || group.context?.textStyleFileName
      ),
      encodings: optionValues(
        allGroups,
        (group) => group.encodingPath || group.candidateSource
      )
    };
  }, [groups]);
}

/**
 * 当前可见的已选 ID 集合
 */
export function useVisibleSelection() {
  const { selectedReviewGroupIds } = useWorkbenchStore();
  const filteredGroups = useFilteredGroups();

  const visibleGroupIdSet = useMemo(
    () => new Set(filteredGroups.map((group) => group.id)),
    [filteredGroups]
  );

  const selectedVisibleGroupIds = useMemo(
    () => selectedReviewGroupIds.filter((id) => visibleGroupIdSet.has(id)),
    [selectedReviewGroupIds, visibleGroupIdSet]
  );

  const selectedReviewGroupIdSet = useMemo(
    () => new Set(selectedReviewGroupIds),
    [selectedReviewGroupIds]
  );

  return {
    visibleGroupIdSet,
    selectedReviewGroupIdSet,
    selectedVisibleGroupIds,
    selectedReviewVisibleCount: selectedVisibleGroupIds.length,
    selectableReviewCount: filteredGroups.length
  };
}
