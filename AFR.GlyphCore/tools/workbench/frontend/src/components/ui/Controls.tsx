import type { InputHTMLAttributes, ReactNode, SelectHTMLAttributes } from 'react';
import { cn } from '@/lib/utils';
import { Checkbox as ShadcnCheckbox } from './checkbox';
import { Input } from './input';
import { NativeSelect as ShadcnNativeSelect } from './native-select';

export function TextInput({ className, ...props }: InputHTMLAttributes<HTMLInputElement>) {
  return <Input className={className} {...props} />;
}

export function NativeSelect({ className, children, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <ShadcnNativeSelect className={className} {...props}>
      {children}
    </ShadcnNativeSelect>
  );
}

export interface SegmentedOption {
  value: string;
  label: ReactNode;
}

interface SegmentedControlProps {
  value: string;
  options: SegmentedOption[];
  onChange: (value: string) => void;
  ariaLabel: string;
  className?: string;
}

export function SegmentedControl({
  value,
  options,
  onChange,
  ariaLabel,
  className
}: SegmentedControlProps) {
  return (
    <div
      className={cn(
        'inline-flex rounded-[var(--radius-pill)] border border-[var(--color-line)] bg-[var(--color-surface-2)] p-1',
        className
      )}
      aria-label={ariaLabel}
      role="group"
    >
      {options.map((option) => {
        const active = option.value === value;
        return (
          <button
            key={option.value}
            type="button"
            onClick={() => onChange(option.value)}
            className={cn(
              'h-8 rounded-[var(--radius-pill)] px-3 text-caption transition-colors',
              active
                ? 'bg-[var(--color-primary)] text-[var(--color-on-primary)]'
                : 'text-[var(--color-text-soft)] hover:bg-[var(--color-hover)] hover:text-[var(--color-text)]'
            )}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

interface CheckboxProps {
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  label: string;
  className?: string;
}

export function Checkbox({ checked, onCheckedChange, label, className }: CheckboxProps) {
  return (
    <ShadcnCheckbox
      checked={checked}
      onCheckedChange={(value) => onCheckedChange(value === true)}
      aria-label={label}
      title={label}
      className={className}
    />
  );
}
