import type { HTMLAttributes, ReactNode } from 'react';
import { cn } from '@/lib/utils';
import { Card, CardHeader } from './card';

export function Panel({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <Card {...props} className={className} />;
}

export interface PanelHeaderProps extends Omit<HTMLAttributes<HTMLDivElement>, 'title'> {
  eyebrow?: string;
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
}

export function PanelHeader({
  eyebrow,
  title,
  description,
  actions,
  className,
  ...props
}: PanelHeaderProps) {
  return (
    <CardHeader
      {...props}
      className={cn(
        'flex items-start justify-between gap-4 border-b border-[var(--color-line)] px-5 py-4 max-[640px]:flex-col',
        className
      )}
    >
      <div className="min-w-0">
        {eyebrow && <div className="text-eyebrow text-[var(--color-text-muted)]">{eyebrow}</div>}
        <h2 className="text-card-title text-[var(--color-text)]">{title}</h2>
        {description && (
          <p className="mt-1 max-w-full break-words text-body-sm text-[var(--color-text-soft)] [overflow-wrap:anywhere]">
            {description}
          </p>
        )}
      </div>
      {actions && <div className="flex shrink-0 flex-wrap items-center gap-2 max-[640px]:w-full">{actions}</div>}
    </CardHeader>
  );
}

export interface MetricProps {
  label: string;
  value: ReactNode;
  tone?: 'default' | 'ai' | 'repair' | 'keep' | 'warn' | 'unsafe';
  className?: string;
}

const metricTone: Record<NonNullable<MetricProps['tone']>, string> = {
  default: 'bg-[var(--color-surface-2)] text-[var(--color-text)] border-[var(--color-line)]',
  ai: 'bg-[var(--color-ai-soft)] text-[var(--color-ai)] border-[var(--color-ai-border)]',
  repair: 'bg-[var(--color-repair-soft)] text-[var(--color-repair)] border-[var(--color-repair-border)]',
  keep: 'bg-[var(--color-keep-soft)] text-[var(--color-keep)] border-[var(--color-keep-border)]',
  warn: 'bg-[var(--color-warn-soft)] text-[var(--color-warn)] border-[var(--color-warn-border)]',
  unsafe: 'bg-[var(--color-unsafe-soft)] text-[var(--color-unsafe)] border-[var(--color-unsafe-border)]'
};

export function Metric({ label, value, tone = 'default', className }: MetricProps) {
  return (
    <Card className={cn('rounded-[var(--radius-md)] border px-4 py-3 shadow-none', metricTone[tone], className)}>
      <div className="text-caption opacity-75">{label}</div>
      <div className="mt-1 text-headline">{value}</div>
    </Card>
  );
}
