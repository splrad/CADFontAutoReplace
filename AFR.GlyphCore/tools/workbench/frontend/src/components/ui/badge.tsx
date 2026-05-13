import * as React from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/utils';

const badgeVariants = cva(
  'inline-flex items-center gap-1 rounded-[var(--radius-pill)] border px-2.5 py-0.5 text-caption font-medium whitespace-nowrap transition-colors',
  {
    variants: {
      variant: {
        default:
          'border-[var(--color-line)] bg-[var(--color-surface-2)] text-[var(--color-text-soft)]',
        repair:
          'border-[var(--color-repair-border)] bg-[var(--color-repair-soft)] text-[var(--color-repair)]',
        ai:
          'border-[var(--color-ai-border)] bg-[var(--color-ai-soft)] text-[var(--color-ai)]',
        keep:
          'border-[var(--color-keep-border)] bg-[var(--color-keep-soft)] text-[var(--color-keep)]',
        unsafe:
          'border-[var(--color-unsafe-border)] bg-[var(--color-unsafe-soft)] text-[var(--color-unsafe)]',
        warn:
          'border-[var(--color-warn-border)] bg-[var(--color-warn-soft)] text-[var(--color-warn)]'
      }
    },
    defaultVariants: {
      variant: 'default'
    }
  }
);

function Badge({
  className,
  variant,
  ...props
}: React.ComponentProps<'span'> & VariantProps<typeof badgeVariants>) {
  return (
    <span
      data-slot="badge"
      className={cn(badgeVariants({ variant }), className)}
      {...props}
    />
  );
}

export { Badge, badgeVariants };
