import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';
import { Badge } from './badge';

export type PillTone =
  | 'repair'
  | 'ai'
  | 'keep'
  | 'unsafe'
  | 'warn'
  | 'ok'
  | 'risk'
  | '';

export interface PillProps {
  tone?: PillTone;
  children: ReactNode;
  className?: string;
}

const toneStyles: Record<Exclude<PillTone, ''>, string> = {
  repair: 'bg-[var(--color-repair-soft)] text-[var(--color-repair)] border-[var(--color-repair-border)]',
  ai: 'bg-[var(--color-ai-soft)] text-[var(--color-ai)] border-[var(--color-ai-border)]',
  keep: 'bg-[var(--color-keep-soft)] text-[var(--color-keep)] border-[var(--color-keep-border)]',
  ok: 'bg-[var(--color-keep-soft)] text-[var(--color-keep)] border-[var(--color-keep-border)]',
  unsafe: 'bg-[var(--color-unsafe-soft)] text-[var(--color-unsafe)] border-[var(--color-unsafe-border)]',
  risk: 'bg-[var(--color-unsafe-soft)] text-[var(--color-unsafe)] border-[var(--color-unsafe-border)]',
  warn: 'bg-[var(--color-warn-soft)] text-[var(--color-warn)] border-[var(--color-warn-border)]'
};

export function Pill({ tone = '', children, className }: PillProps) {
  const toneClass = tone
    ? toneStyles[tone]
    : 'bg-[var(--color-surface-2)] text-[var(--color-text-soft)] border-[var(--color-line)]';

  return <Badge className={cn(toneClass, className)}>{children}</Badge>;
}
