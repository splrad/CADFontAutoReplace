/**
 * AFR 文枢工作台应用外壳
 * 
 * 三行 grid 布局：TopBar / 工作区 / StatusBar。
 */

import { TopBar } from '@/components/TopBar';
import { StatusBar } from '@/components/StatusBar';

interface AppShellProps {
  children: React.ReactNode;
}

export function AppShell({ children }: AppShellProps) {
  return (
    <div className="flex h-screen w-screen flex-col overflow-hidden bg-[var(--color-page)] text-[var(--color-text)]">
      <TopBar />
      <main className="min-h-0 flex-1 overflow-hidden">
        {children}
      </main>
      <StatusBar />
    </div>
  );
}
