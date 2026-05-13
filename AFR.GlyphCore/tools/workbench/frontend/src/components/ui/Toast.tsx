/**
 * AFR 文枢工作台 Toast 通知系统
 * 
 * 基于 @radix-ui/react-toast，支持 success / error / info 三种 tone。
 */

import * as RadixToast from '@radix-ui/react-toast';
import { cn } from '@/lib/utils';

export type ToastTone = 'success' | 'error' | 'info';

export interface ToastItem {
  id: string;
  message: string;
  tone?: ToastTone;
}

interface ToastProps {
  item: ToastItem;
  onOpenChange: (open: boolean) => void;
}

const toneStyles: Record<ToastTone, string> = {
  success:
    'bg-[var(--color-keep-soft)] text-[var(--color-keep)] border-[var(--color-keep-border)]',
  error:
    'bg-[var(--color-unsafe-soft)] text-[var(--color-unsafe)] border-[var(--color-unsafe-border)]',
  info:
    'bg-[var(--color-ai-soft)] text-[var(--color-ai)] border-[var(--color-ai-border)]'
};

function Toast({ item, onOpenChange }: ToastProps) {
  const tone: ToastTone = item.tone ?? 'info';
  return (
    <RadixToast.Root
      defaultOpen
      duration={3500}
      onOpenChange={onOpenChange}
      className={cn(
        'flex items-start justify-between gap-4',
        'px-4 py-3 rounded-[var(--radius-lg)] border shadow-[var(--shadow-lg)]',
        'text-body-sm bg-[var(--color-canvas)]',
        'data-[state=open]:translate-y-0 data-[state=closed]:translate-x-3 data-[state=closed]:opacity-0 transition-all',
        toneStyles[tone]
      )}
    >
      <RadixToast.Description>{item.message}</RadixToast.Description>
      <RadixToast.Close
        className="shrink-0 opacity-60 hover:opacity-100 transition-opacity text-caption"
        aria-label="关闭"
      >
        ✕
      </RadixToast.Close>
    </RadixToast.Root>
  );
}

interface ToastProviderProps {
  toasts: ToastItem[];
  onClose: (id: string) => void;
  children?: React.ReactNode;
}

export function ToastProvider({ toasts, onClose, children }: ToastProviderProps) {
  return (
    <RadixToast.Provider swipeDirection="right">
      {children}
      {toasts.map((item) => (
        <Toast
          key={item.id}
          item={item}
          onOpenChange={(open) => {
            if (!open) onClose(item.id);
          }}
        />
      ))}
      <RadixToast.Viewport
        className={cn(
          'fixed bottom-10 right-4 z-50',
          'flex flex-col gap-2 w-80',
          'outline-none'
        )}
      />
    </RadixToast.Provider>
  );
}
