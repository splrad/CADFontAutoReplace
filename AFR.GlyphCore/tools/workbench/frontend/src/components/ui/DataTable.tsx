import {
  flexRender,
  getCoreRowModel,
  useReactTable,
  type ColumnDef,
  type Row
} from '@tanstack/react-table';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useRef } from 'react';
import { cn } from '@/lib/utils';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow
} from './table';

interface DataTableProps<T> {
  data: T[];
  columns: ColumnDef<T>[];
  getRowId?: (row: T, index: number) => string;
  estimateRowHeight?: number;
  emptyTitle: string;
  emptyDescription?: string;
  className?: string;
  rowClassName?: (row: Row<T>, index: number) => string;
}

export function DataTable<T>({
  data,
  columns,
  getRowId,
  estimateRowHeight = 56,
  emptyTitle,
  emptyDescription,
  className,
  rowClassName
}: DataTableProps<T>) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const table = useReactTable({
    data,
    columns,
    getRowId,
    getCoreRowModel: getCoreRowModel()
  });
  const rows = table.getRowModel().rows;
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => estimateRowHeight,
    overscan: 12
  });

  if (data.length === 0) {
    return (
      <div className="flex h-full min-h-0 flex-1 flex-col items-center justify-center gap-2 text-center text-[var(--color-text-soft)]">
        <strong className="text-body-lg text-[var(--color-text)]">{emptyTitle}</strong>
        {emptyDescription && <span className="text-body-sm">{emptyDescription}</span>}
      </div>
    );
  }

  const virtualItems = virtualizer.getVirtualItems();
  const topPadding = virtualItems.length ? virtualItems[0].start : 0;
  const bottomPadding = virtualItems.length
    ? virtualizer.getTotalSize() - virtualItems[virtualItems.length - 1].end
    : 0;
  const tableMinWidth = table.getAllLeafColumns().reduce(
    (total, column) => total + column.getSize(),
    0
  );

  return (
    <div ref={scrollRef} className={cn('min-h-0 flex-1 overflow-auto scrollbar-thin', className)}>
      <Table className="table-fixed" style={{ minWidth: tableMinWidth }}>
        <TableHeader>
          {table.getHeaderGroups().map((headerGroup) => (
            <TableRow key={headerGroup.id} className="hover:bg-transparent">
              {headerGroup.headers.map((header) => (
                <TableHead
                  key={header.id}
                  style={{ width: header.getSize() }}
                  className="sticky top-0 z-10 border-b border-[var(--color-line)] bg-[var(--color-canvas)] px-3 py-2 text-left text-caption text-[var(--color-text-muted)]"
                >
                  {header.isPlaceholder
                    ? null
                    : flexRender(header.column.columnDef.header, header.getContext())}
                </TableHead>
              ))}
            </TableRow>
          ))}
        </TableHeader>
        <TableBody>
          {topPadding > 0 && (
            <TableRow aria-hidden>
              <TableCell colSpan={columns.length} style={{ height: topPadding }} />
            </TableRow>
          )}
          {virtualItems.map((virtualRow) => {
            const row = rows[virtualRow.index];
            return (
              <TableRow
                key={row.id}
                className={cn(
                  'border-b border-[var(--color-line-soft)] align-top transition-colors hover:bg-[var(--color-surface-2)]',
                  rowClassName?.(row, virtualRow.index)
                )}
              >
                {row.getVisibleCells().map((cell) => (
                  <TableCell key={cell.id} className="px-3 py-2 align-top">
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </TableCell>
                ))}
              </TableRow>
            );
          })}
          {bottomPadding > 0 && (
            <TableRow aria-hidden>
              <TableCell colSpan={columns.length} style={{ height: bottomPadding }} />
            </TableRow>
          )}
        </TableBody>
      </Table>
    </div>
  );
}
