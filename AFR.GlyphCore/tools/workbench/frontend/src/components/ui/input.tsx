import * as React from 'react';
import { cn } from '@/lib/utils';

function Input({ className, type, ...props }: React.ComponentProps<'input'>) {
  return (
    <input
      data-slot="input"
      type={type}
      className={cn(
        'h-10 w-full rounded-[var(--radius-md)] border border-[var(--color-line)] bg-[var(--color-canvas)] px-3 text-body-sm transition-colors',
        'placeholder:text-[var(--color-text-disabled)] hover:border-[var(--color-line-hover)] focus:border-[var(--color-primary)]',
        'disabled:cursor-not-allowed disabled:opacity-50',
        className
      )}
      {...props}
    />
  );
}

export { Input };
