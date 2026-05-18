import { Info, Save } from 'lucide-react';

interface Props {
  message: string;
  selectedCount?: number;
  lastSaved?: string;
}

export default function StatusBar({ message, selectedCount = 0, lastSaved }: Props) {
  return (
    <footer className="fixed inset-x-0 bottom-0 z-50 flex h-7 items-center gap-4 border-t border-gray-200 bg-gray-50 px-4">
      <span className="flex items-center gap-1.5 text-xs text-gray-500">
        <Info size={11} className="shrink-0 text-gray-400" />
        {message}
      </span>
      {selectedCount > 0 && <span className="text-xs font-medium text-blue-600">已选 {selectedCount} 条</span>}
      <span className="ml-auto flex items-center gap-1.5 text-xs text-gray-400">
        <Save size={11} />
        本地数据自动保存{lastSaved ? ` · 上次保存 ${lastSaved}` : ''}
      </span>
    </footer>
  );
}
