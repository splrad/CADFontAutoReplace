import * as React from 'react';
import { cn } from '@/lib/utils';

function NativeSelect({ className, children, ...props }: React.ComponentProps<'select'>) {
  return (
    <select
      data-slot="native-select"
      className={cn(
        'h-10 w-full rounded-[var(--radius-md)] border border-[var(--color-line)] bg-[var(--color-canvas)] px-3 text-body-sm transition-colors',
        'hover:border-[var(--color-line-hover)] focus:border-[var(--color-primary)] disabled:cursor-not-allowed disabled:opacity-50',
        className
      )}
      {...props}
    >
      {children}
    </select>
  );
}

export { NativeSelect };
