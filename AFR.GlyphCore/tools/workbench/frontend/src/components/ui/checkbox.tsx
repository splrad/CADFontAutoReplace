import * as React from 'react';
import * as CheckboxPrimitive from '@radix-ui/react-checkbox';
import { CheckIcon } from 'lucide-react';
import { cn } from '@/lib/utils';

function Checkbox({
  className,
  ...props
}: React.ComponentProps<typeof CheckboxPrimitive.Root>) {
  return (
    <CheckboxPrimitive.Root
      data-slot="checkbox"
      className={cn(
        'peer size-5 shrink-0 rounded-[var(--radius-sm)] border border-[var(--color-line-hover)] bg-[var(--color-canvas)] shadow-[var(--shadow-sm)] transition-colors',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-focus-ring)] focus-visible:ring-offset-2',
        'disabled:cursor-not-allowed disabled:opacity-50',
        'data-[state=checked]:border-[var(--color-primary)] data-[state=checked]:bg-[var(--color-primary)] data-[state=checked]:text-[var(--color-on-primary)]',
        className
      )}
      {...props}
    >
      <CheckboxPrimitive.Indicator
        data-slot="checkbox-indicator"
        className="flex items-center justify-center text-current"
      >
        <CheckIcon className="size-3.5" aria-hidden />
      </CheckboxPrimitive.Indicator>
    </CheckboxPrimitive.Root>
  );
}

export { Checkbox };
