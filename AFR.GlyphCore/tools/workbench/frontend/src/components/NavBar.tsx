import { BarChart2, CheckSquare, Layers, PlayCircle, Table2 } from 'lucide-react';
import type { ReactNode } from 'react';
import type { TabId } from '@/types/view';

interface Tab {
  id: TabId;
  label: string;
  icon: ReactNode;
}

const TABS: Tab[] = [
  { id: 'annotation', label: '数据标注', icon: <CheckSquare size={13} /> },
  { id: 'dataset', label: '训练数据集', icon: <Table2 size={13} /> },
  { id: 'training', label: '模型训练', icon: <PlayCircle size={13} /> },
  { id: 'report', label: '模型报告', icon: <BarChart2 size={13} /> }
];

interface Props {
  activeTab: TabId;
  onTabChange: (tab: TabId) => void;
}

export default function NavBar({ activeTab, onTabChange }: Props) {
  return (
    <header className="fixed inset-x-0 top-0 z-50 border-b border-gray-200 bg-white">
      <div className="flex h-11 items-center gap-3 px-4">
        <div className="flex shrink-0 items-center gap-1.5">
          <Layers size={15} className="text-gray-900" />
          <span className="font-mono text-sm font-bold tracking-tight text-gray-900">AFR GLYPHCORE</span>
        </div>

        <span className="shrink-0 text-gray-200">|</span>

        <nav className="flex shrink-0 items-center gap-0.5">
          {TABS.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => onTabChange(tab.id)}
              className={`flex h-7 items-center gap-1.5 rounded px-3 text-xs font-medium transition-colors ${
                activeTab === tab.id ? 'bg-gray-900 text-white' : 'text-gray-600 hover:bg-gray-100 hover:text-gray-900'
              }`}
            >
              {tab.icon}
              {tab.label}
            </button>
          ))}
        </nav>
      </div>
    </header>
  );
}
