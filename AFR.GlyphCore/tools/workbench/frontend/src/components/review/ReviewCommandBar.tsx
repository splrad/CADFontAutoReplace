import { FilterX, Search } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { NativeSelect, TextInput } from '@/components/ui/Controls';
import { Metric } from '@/components/ui/Panel';
import { useFilterOptions, useVisibleSelection } from '@/hooks/useFilters';
import { useWorkbenchStore } from '@/store/useWorkbenchStore';

export function ReviewCommandBar() {
  const {
    query,
    setQuery,
    filter,
    setFilter,
    riskFilter,
    setRiskFilter,
    layerFilter,
    setLayerFilter,
    fontFilter,
    setFontFilter,
    encodingFilter,
    setEncodingFilter,
    clearReviewFilters
  } = useWorkbenchStore();

  const filterOptions = useFilterOptions();
  const { selectedReviewVisibleCount, selectableReviewCount } = useVisibleSelection();

  return (
    <div className="flex h-full min-h-0 flex-col gap-4 p-4">
      <div>
        <div className="text-eyebrow text-[var(--color-text-muted)]">Review Queue</div>
        <h2 className="text-card-title text-[var(--color-text)]">复核筛选</h2>
        <p className="mt-1 text-body-sm text-[var(--color-text-soft)]">
          按文本、风险、图层、字体和编码路径收敛需要确认的文本簇。
        </p>
      </div>

      <div className="grid grid-cols-2 gap-2">
        <Metric label="可见文本簇" value={selectableReviewCount} tone="ai" />
        <Metric label="已勾选" value={selectedReviewVisibleCount} tone="keep" />
      </div>

      <label className="flex flex-col gap-2">
        <span className="text-caption text-[var(--color-text-muted)]">搜索</span>
        <div className="relative">
          <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-[var(--color-text-disabled)]" aria-hidden />
          <TextInput
            className="pl-9"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="原文 / 正确文本 / 图层 / 字体"
          />
        </div>
      </label>

      <div className="grid gap-3">
        <label className="flex flex-col gap-2">
          <span className="text-caption text-[var(--color-text-muted)]">状态</span>
          <NativeSelect value={filter} onChange={(e) => setFilter(e.target.value as any)}>
            <option value="pending">未审核</option>
            <option value="reviewed">已审核待入训练</option>
            <option value="all">全部</option>
          </NativeSelect>
        </label>

        <label className="flex flex-col gap-2">
          <span className="text-caption text-[var(--color-text-muted)]">风险等级</span>
          <NativeSelect value={riskFilter} onChange={(e) => setRiskFilter(e.target.value as any)}>
            <option value="all">全部风险</option>
            <option value="high">高风险</option>
            <option value="medium">中风险</option>
            <option value="low">低风险</option>
          </NativeSelect>
        </label>

        <label className="flex flex-col gap-2">
          <span className="text-caption text-[var(--color-text-muted)]">图层</span>
          <NativeSelect value={layerFilter} onChange={(e) => setLayerFilter(e.target.value)}>
            <option value="all">全部图层</option>
            {filterOptions.layers.map((value) => (
              <option key={value} value={value}>{value}</option>
            ))}
          </NativeSelect>
        </label>

        <label className="flex flex-col gap-2">
          <span className="text-caption text-[var(--color-text-muted)]">字体</span>
          <NativeSelect value={fontFilter} onChange={(e) => setFontFilter(e.target.value)}>
            <option value="all">全部字体</option>
            {filterOptions.fonts.map((value) => (
              <option key={value} value={value}>{value}</option>
            ))}
          </NativeSelect>
        </label>

        <label className="flex flex-col gap-2">
          <span className="text-caption text-[var(--color-text-muted)]">编码路径</span>
          <NativeSelect value={encodingFilter} onChange={(e) => setEncodingFilter(e.target.value)}>
            <option value="all">全部编码路径</option>
            {filterOptions.encodings.map((value) => (
              <option key={value} value={value}>{value}</option>
            ))}
          </NativeSelect>
        </label>
      </div>

      <Button variant="ghost" icon={FilterX} onClick={clearReviewFilters} className="mt-auto">
        清空筛选
      </Button>
    </div>
  );
}
