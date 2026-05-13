import * as React from 'react';
import * as ProgressPrimitive from '@radix-ui/react-progress';
import { cn } from '@/lib/utils';

function Progress({
  className,
  value,
  ...props
}: React.ComponentProps<typeof ProgressPrimitive.Root>) {
  const safeValue = Math.max(0, Math.min(100, Number(value || 0)));

  return (
    <ProgressPrimitive.Root
      data-slot="progress"
      className={cn('relative h-3 w-full overflow-hidden rounded-full bg-[var(--color-surface-soft)]', className)}
      {...props}
    >
      <ProgressPrimitive.Indicator
        data-slot="progress-indicator"
        className="h-full w-full flex-1 rounded-full bg-[var(--color-primary)] transition-transform"
        style={{ transform: `translateX(-${100 - safeValue}%)` }}
      />
    </ProgressPrimitive.Root>
  );
}

export { Progress };
