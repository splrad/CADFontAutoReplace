import { useCallback, useLayoutEffect, useMemo, useRef, useState, type RefObject } from 'react';

interface VirtualRowsResult<T> {
  scrollRef: RefObject<HTMLDivElement | null>;
  onScroll: () => void;
  visibleItems: T[];
  startIndex: number;
  topSpacerHeight: number;
  bottomSpacerHeight: number;
}

export function useVirtualRows<T>(items: T[], rowHeight: number, overscan = 12): VirtualRowsResult<T> {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [viewport, setViewport] = useState({ scrollTop: 0, height: rowHeight * 20 });

  const updateViewport = useCallback(() => {
    const node = scrollRef.current;
    if (!node) return;
    const next = { scrollTop: node.scrollTop, height: node.clientHeight || rowHeight * 20 };
    setViewport((current) =>
      current.scrollTop === next.scrollTop && current.height === next.height ? current : next
    );
  }, [rowHeight]);

  useLayoutEffect(() => {
    updateViewport();
    const node = scrollRef.current;
    if (!node || typeof ResizeObserver === 'undefined') return undefined;
    const observer = new ResizeObserver(updateViewport);
    observer.observe(node);
    return () => observer.disconnect();
  }, [updateViewport]);

  useLayoutEffect(() => {
    const node = scrollRef.current;
    if (!node) return;
    const maxScrollTop = Math.max(0, items.length * rowHeight - node.clientHeight);
    if (node.scrollTop > maxScrollTop) {
      node.scrollTop = maxScrollTop;
    }
    updateViewport();
  }, [items.length, rowHeight, updateViewport]);

  const onScroll = useCallback(() => {
    updateViewport();
  }, [updateViewport]);

  const startIndex = Math.max(0, Math.floor(viewport.scrollTop / rowHeight) - overscan);
  const endIndex = Math.min(
    items.length,
    Math.ceil((viewport.scrollTop + viewport.height) / rowHeight) + overscan
  );

  return useMemo(
    () => ({
      scrollRef,
      onScroll,
      visibleItems: items.slice(startIndex, endIndex),
      startIndex,
      topSpacerHeight: startIndex * rowHeight,
      bottomSpacerHeight: Math.max(0, (items.length - endIndex) * rowHeight)
    }),
    [endIndex, items, onScroll, rowHeight, startIndex]
  );
}
