import * as React from 'react';
import * as TabsPrimitive from '@radix-ui/react-tabs';
import { cn } from '@/lib/utils';

function Tabs({
  className,
  ...props
}: React.ComponentProps<typeof TabsPrimitive.Root>) {
  return (
    <TabsPrimitive.Root
      data-slot="tabs"
      className={cn('flex flex-col gap-2', className)}
      {...props}
    />
  );
}

function TabsList({
  className,
  ...props
}: React.ComponentProps<typeof TabsPrimitive.List>) {
  return (
    <TabsPrimitive.List
      data-slot="tabs-list"
      className={cn(
        'inline-flex items-center gap-1 rounded-[var(--radius-pill)] border border-[var(--color-line)] bg-[var(--color-surface-2)] p-1',
        className
      )}
      {...props}
    />
  );
}

function TabsTrigger({
  className,
  ...props
}: React.ComponentProps<typeof TabsPrimitive.Trigger>) {
  return (
    <TabsPrimitive.Trigger
      data-slot="tabs-trigger"
      className={cn(
        'inline-flex h-9 shrink-0 items-center justify-center gap-2 rounded-[var(--radius-pill)] px-3 text-caption transition-colors',
        'text-[var(--color-text-soft)] hover:bg-[var(--color-hover)] hover:text-[var(--color-text)]',
        'data-[state=active]:bg-[var(--color-primary)] data-[state=active]:text-[var(--color-on-primary)]',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-focus-ring)] focus-visible:ring-offset-2',
        className
      )}
      {...props}
    />
  );
}

function TabsContent({
  className,
  ...props
}: React.ComponentProps<typeof TabsPrimitive.Content>) {
  return (
    <TabsPrimitive.Content
      data-slot="tabs-content"
      className={cn('flex-1 outline-none', className)}
      {...props}
    />
  );
}

export { Tabs, TabsList, TabsTrigger, TabsContent };
