/**
 * AFR 文枢工作台指标卡 (Info)
 * 
 * 用于展示统计指标、数据摘要等信息。
 */

import { cn } from '@/lib/utils';

export interface InfoProps {
  label: string;
  value: React.ReactNode;
  className?: string;
  valueClassName?: string;
}

export function Info({ label, value, className, valueClassName }: InfoProps) {
  return (
    <div className={cn('flex flex-col gap-0.5', className)}>
      <span className="text-caption text-[var(--color-text-soft)]">
        {label}
      </span>
      <span
        className={cn(
          'text-body-lg font-semibold text-[var(--color-text)]',
          valueClassName
        )}
      >
        {value}
      </span>
    </div>
  );
}
