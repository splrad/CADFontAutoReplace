/**
 * AFR 文枢工作台候选选项 hooks
 * 
 * 封装 candidateOptionsForGroup 和 initialCandidateKey 的 hook 形式。
 */

import { useMemo } from 'react';
import {
  candidateOptionsForGroup,
  initialCandidateKey,
  type ReviewGroup,
  type CandidateRecord,
  type ReviewedRecord,
  type CandidateOption
} from '@/lib/utils';

/**
 * 为指定复核组生成候选选项
 * 
 * @param group 复核组
 * @param saved 已保存的复核记录
 * @param recordsById 实体记录映射表
 * @returns 候选选项数组
 */
export function useCandidateOptions(
  group: ReviewGroup | null | undefined,
  saved: ReviewedRecord | null | undefined,
  recordsById: Map<string, CandidateRecord>
): CandidateOption[] {
  return useMemo(() => {
    if (!group) return [];
    return candidateOptionsForGroup(group, saved ?? null, recordsById);
  }, [group, saved, recordsById]);
}

/**
 * 计算初始选中的候选 key
 * 
 * @param options 候选选项数组
 * @param saved 已保存的复核记录
 * @param group 复核组
 * @param finalText 最终文本
 * @returns 候选 key
 */
export function useInitialCandidateKey(
  options: CandidateOption[],
  saved: ReviewedRecord | null | undefined,
  group: ReviewGroup | null | undefined,
  finalText: string
): string {
  return useMemo(() => {
    if (!group || options.length === 0) return '__manual__';
    return initialCandidateKey(options, saved ?? null, group, finalText);
  }, [options, saved, group, finalText]);
}
