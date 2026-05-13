import * as React from 'react';
import { Slot } from '@radix-ui/react-slot';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/utils';

type IconType = React.ComponentType<{ className?: string; 'aria-hidden'?: boolean }>;

const buttonVariants = cva(
  [
    'inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-[var(--radius-pill)] border text-body-sm font-medium transition-colors',
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-focus-ring)] focus-visible:ring-offset-2',
    'disabled:pointer-events-none disabled:opacity-45',
    '[&_svg]:pointer-events-none [&_svg]:size-4 [&_svg]:shrink-0'
  ],
  {
    variants: {
      variant: {
        default:
          'border-[var(--color-primary)] bg-[var(--color-primary)] text-[var(--color-on-primary)] hover:bg-[#222]',
        primary:
          'border-[var(--color-primary)] bg-[var(--color-primary)] text-[var(--color-on-primary)] hover:bg-[#222]',
        secondary:
          'border-[var(--color-line)] bg-[var(--color-canvas)] text-[var(--color-text)] hover:border-[var(--color-primary)]',
        outline:
          'border-[var(--color-line)] bg-[var(--color-canvas)] text-[var(--color-text)] hover:bg-[var(--color-hover)]',
        ghost:
          'border-transparent bg-transparent text-[var(--color-text-soft)] hover:bg-[var(--color-hover)] hover:text-[var(--color-text)]',
        destructive:
          'border-[var(--color-unsafe-border)] bg-[var(--color-unsafe-soft)] text-[var(--color-unsafe)] hover:bg-[var(--color-unsafe-block)]',
        danger:
          'border-[var(--color-unsafe-border)] bg-[var(--color-unsafe-soft)] text-[var(--color-unsafe)] hover:bg-[var(--color-unsafe-block)]',
        pastel:
          'border-[var(--color-ai-border)] bg-[var(--color-ai-soft)] text-[var(--color-ai)] hover:bg-[var(--color-ai-block)]',
        link:
          'border-transparent bg-transparent text-[var(--color-ai)] underline-offset-4 hover:underline'
      },
      size: {
        default: 'h-10 px-4',
        sm: 'h-8 px-3 text-caption',
        md: 'h-10 px-4',
        lg: 'h-11 px-5 text-body',
        icon: 'size-10',
        iconSm: 'size-8'
      }
    },
    defaultVariants: {
      variant: 'secondary',
      size: 'default'
    }
  }
);

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  asChild?: boolean;
  icon?: IconType;
  trailingIcon?: IconType;
}

function Button({
  className,
  variant,
  size,
  asChild = false,
  icon: Icon,
  trailingIcon: TrailingIcon,
  children,
  ...props
}: ButtonProps) {
  const Comp = asChild ? Slot : 'button';

  return (
    <Comp
      data-slot="button"
      className={cn(buttonVariants({ variant, size, className }))}
      {...props}
    >
      {Icon && <Icon className="h-4 w-4 shrink-0" aria-hidden />}
      {children && <span className="min-w-0 truncate">{children}</span>}
      {TrailingIcon && <TrailingIcon className="h-4 w-4 shrink-0" aria-hidden />}
    </Comp>
  );
}

export interface IconButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  icon: IconType;
  label: string;
  variant?: ButtonProps['variant'];
  size?: 'sm' | 'md';
}

function IconButton({
  icon: Icon,
  label,
  variant = 'ghost',
  size = 'md',
  className,
  ...props
}: IconButtonProps) {
  return (
    <Button
      {...props}
      aria-label={label}
      title={label}
      variant={variant}
      size={size === 'sm' ? 'iconSm' : 'icon'}
      className={cn('rounded-full', className)}
    >
      <Icon className="h-4 w-4" aria-hidden />
    </Button>
  );
}

export { Button, IconButton, buttonVariants };
