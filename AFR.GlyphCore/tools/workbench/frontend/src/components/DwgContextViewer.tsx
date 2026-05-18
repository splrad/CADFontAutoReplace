import { useCallback, useEffect, useRef, useState, type PointerEvent, type ReactNode, type WheelEvent } from 'react';
import { Crosshair, Maximize2, ZoomIn, ZoomOut } from 'lucide-react';
import type { DBTextCluster, DwgNearbyText } from '@/types/bolt';

interface Props {
  cluster: DBTextCluster | null;
}

const LAYER_COLOR: Record<string, string> = {
  INSTRUMENT: '#38bdf8',
  PIPE: '#86efac',
  EQUIP: '#fbbf24',
  VALVE: '#f97316',
  LABEL: '#a3e635',
  TAGNO: '#e879f9',
  TITLE: '#94a3b8',
  MISC: '#94a3b8',
  ELEC: '#fb923c'
};

const layerColor = (layer: string) => LAYER_COLOR[layer] ?? '#cbd5e1';
const GRID_STEP = 100;

export default function DwgContextViewer({ cluster }: Props) {
  const svgRef = useRef<SVGSVGElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const [tx, setTx] = useState(0);
  const [ty, setTy] = useState(0);
  const [scale, setScale] = useState(1);
  const dragging = useRef(false);
  const lastPt = useRef({ x: 0, y: 0 });

  const fitView = useCallback(() => {
    if (!cluster || !containerRef.current) return;
    const { clientWidth: width, clientHeight: height } = containerRef.current;
    const all = [cluster.coord, ...cluster.nearbyTexts];
    const xs = all.map((point) => point.x);
    const ys = all.map((point) => point.y);
    const minX = Math.min(...xs);
    const maxX = Math.max(...xs);
    const minY = Math.min(...ys);
    const maxY = Math.max(...ys);
    const rangeX = Math.max(maxX - minX, 200);
    const rangeY = Math.max(maxY - minY, 200);
    const padding = 80;
    const nextScale = Math.min((width - padding * 2) / rangeX, (height - padding * 2) / rangeY, 4);
    const safeScale = Number.isFinite(nextScale) && nextScale > 0 ? nextScale : 1;
    const cx = (minX + maxX) / 2;
    const cy = (minY + maxY) / 2;
    setScale(safeScale);
    setTx(width / 2 - cx * safeScale);
    setTy(height / 2 - cy * safeScale);
  }, [cluster]);

  useEffect(() => {
    fitView();
  }, [fitView]);

  const onPointerDown = (event: PointerEvent<SVGSVGElement>) => {
    dragging.current = true;
    lastPt.current = { x: event.clientX, y: event.clientY };
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const onPointerMove = (event: PointerEvent<SVGSVGElement>) => {
    if (!dragging.current) return;
    setTx((value) => value + event.clientX - lastPt.current.x);
    setTy((value) => value + event.clientY - lastPt.current.y);
    lastPt.current = { x: event.clientX, y: event.clientY };
  };

  const onPointerUp = () => {
    dragging.current = false;
  };

  const onWheel = (event: WheelEvent<SVGSVGElement>) => {
    event.preventDefault();
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect) return;
    const mx = event.clientX - rect.left;
    const my = event.clientY - rect.top;
    const factor = event.deltaY < 0 ? 1.15 : 1 / 1.15;
    setScale((value) => Math.max(0.15, Math.min(12, value * factor)));
    setTx((value) => mx - (mx - value) * factor);
    setTy((value) => my - (my - value) * factor);
  };

  const zoomBy = (factor: number) => {
    if (!containerRef.current) return;
    const { clientWidth: width, clientHeight: height } = containerRef.current;
    setScale((value) => Math.max(0.15, Math.min(12, value * factor)));
    setTx((value) => width / 2 - (width / 2 - value) * factor);
    setTy((value) => height / 2 - (height / 2 - value) * factor);
  };

  const toScreen = (x: number, y: number) => ({
    sx: x * scale + tx,
    sy: -y * scale + ty
  });

  if (!cluster) {
    return (
      <div className="flex h-full flex-col overflow-hidden rounded border border-gray-700 bg-gray-950">
        <ViewerHeader title="图纸上下文" />
        <div className="flex flex-1 select-none items-center justify-center text-xs text-gray-600">
          <span>在左侧表格中点击任意记录以查看图纸上下文</span>
        </div>
      </div>
    );
  }

  const { coord, nearbyTexts, layer } = cluster;
  const containerW = containerRef.current?.clientWidth ?? 600;
  const containerH = containerRef.current?.clientHeight ?? 400;
  const dwgLeft = -tx / scale;
  const dwgRight = (containerW - tx) / scale;
  const dwgBottom = ty / scale;
  const dwgTop = (ty - containerH) / scale;
  const gridStartX = Math.floor(dwgLeft / GRID_STEP) * GRID_STEP;
  const gridEndX = Math.ceil(dwgRight / GRID_STEP) * GRID_STEP;
  const gridStartY = Math.floor(dwgTop / GRID_STEP) * GRID_STEP;
  const gridEndY = Math.ceil(dwgBottom / GRID_STEP) * GRID_STEP;

  return (
    <div ref={containerRef} className="flex h-full flex-col overflow-hidden rounded border border-gray-700 bg-gray-950">
      <ViewerHeader
        title="图纸上下文"
        subtitle={`${cluster.dwgFile}  ·  坐标 (${Math.round(coord.x)}, ${Math.round(coord.y)})  ·  ${layer}`}
        actions={
          <>
            <ToolBtn icon={<ZoomIn size={12} />} title="放大" onClick={() => zoomBy(1.3)} />
            <ToolBtn icon={<ZoomOut size={12} />} title="缩小" onClick={() => zoomBy(1 / 1.3)} />
            <ToolBtn icon={<Crosshair size={12} />} title="定位到当前" onClick={fitView} />
            <ToolBtn icon={<Maximize2 size={12} />} title="适配视图" onClick={fitView} />
          </>
        }
      />

      <svg
        ref={svgRef}
        className="w-full flex-1 cursor-grab select-none active:cursor-grabbing"
        style={{ touchAction: 'none' }}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerLeave={onPointerUp}
        onWheel={onWheel}
      >
        <defs>
          <filter id="glow" x="-40%" y="-40%" width="180%" height="180%">
            <feGaussianBlur stdDeviation="3" result="blur" />
            <feMerge>
              <feMergeNode in="blur" />
              <feMergeNode in="SourceGraphic" />
            </feMerge>
          </filter>
          <filter id="glow-strong" x="-80%" y="-80%" width="260%" height="260%">
            <feGaussianBlur stdDeviation="6" result="blur" />
            <feMerge>
              <feMergeNode in="blur" />
              <feMergeNode in="SourceGraphic" />
            </feMerge>
          </filter>
        </defs>

        <g opacity="0.12">
          {Array.from({ length: Math.ceil((gridEndX - gridStartX) / GRID_STEP) + 1 }, (_, index) => {
            const gx = gridStartX + index * GRID_STEP;
            const { sx } = toScreen(gx, 0);
            return <line key={`vg${gx}`} x1={sx} y1={0} x2={sx} y2={containerH} stroke="#475569" strokeWidth={0.5} />;
          })}
          {Array.from({ length: Math.ceil((gridEndY - gridStartY) / GRID_STEP) + 1 }, (_, index) => {
            const gy = gridStartY + index * GRID_STEP;
            const { sy } = toScreen(0, gy);
            return <line key={`hg${gy}`} x1={0} y1={sy} x2={containerW} y2={sy} stroke="#475569" strokeWidth={0.5} />;
          })}
        </g>

        {nearbyTexts.map((node) => (
          <NearbyNode key={node.id} node={node} toScreen={toScreen} scale={scale} />
        ))}

        {nearbyTexts.map((node) => {
          const { sx: cx, sy: cy } = toScreen(coord.x, coord.y);
          const { sx: nx, sy: ny } = toScreen(node.x, node.y);
          return <line key={`ln-${node.id}`} x1={cx} y1={cy} x2={nx} y2={ny} stroke="#334155" strokeWidth={1} strokeDasharray="4 3" />;
        })}

        <CenterNode cluster={cluster} toScreen={toScreen} scale={scale} />
      </svg>

      <LegendBar nearbyTexts={nearbyTexts} />
    </div>
  );
}

function ViewerHeader({ title, subtitle, actions }: { title: string; subtitle?: string; actions?: ReactNode }) {
  return (
    <div className="flex shrink-0 items-center gap-3 border-b border-gray-800 bg-gray-900 px-3 py-2">
      <span className="shrink-0 text-xs font-semibold text-gray-200">{title}</span>
      {subtitle && <span className="min-w-0 flex-1 truncate font-mono text-xs text-gray-500">{subtitle}</span>}
      {actions && <div className="ml-auto flex shrink-0 items-center gap-1">{actions}</div>}
    </div>
  );
}

function ToolBtn({ icon, title, onClick }: { icon: ReactNode; title: string; onClick: () => void }) {
  return (
    <button
      type="button"
      title={title}
      onClick={onClick}
      className="flex h-6 w-6 items-center justify-center rounded text-gray-400 transition-colors hover:bg-gray-700 hover:text-gray-100"
    >
      {icon}
    </button>
  );
}

function CenterNode({
  cluster,
  toScreen,
  scale
}: {
  cluster: DBTextCluster;
  toScreen: (x: number, y: number) => { sx: number; sy: number };
  scale: number;
}) {
  const { sx, sy } = toScreen(cluster.coord.x, cluster.coord.y);
  const fontSize = Math.max(9, Math.min(14, 11 * scale));
  const padX = 8;
  const padY = 5;
  const estimatedW = cluster.originalText.length * fontSize * 0.65 + padX * 2;
  const boxH = fontSize + padY * 2;

  return (
    <g>
      <circle cx={sx} cy={sy} r={28} fill="none" stroke="#f59e0b" strokeWidth={1.5} opacity={0.25} />
      <circle cx={sx} cy={sy} r={20} fill="none" stroke="#f59e0b" strokeWidth={1} opacity={0.4} />
      <circle cx={sx} cy={sy} r={3} fill="#f59e0b" opacity={0.9} />
      <rect
        x={sx - estimatedW / 2}
        y={sy - boxH / 2}
        width={estimatedW}
        height={boxH}
        rx={3}
        fill="#78350f"
        stroke="#f59e0b"
        strokeWidth={1.5}
        filter="url(#glow)"
      />
      <text x={sx} y={sy + fontSize * 0.35} textAnchor="middle" fontSize={fontSize} fontFamily="monospace" fill="#fde68a" filter="url(#glow)">
        {cluster.originalText}
      </text>
      <text x={sx} y={sy - boxH / 2 - 5} textAnchor="middle" fontSize={Math.max(8, fontSize * 0.75)} fontFamily="monospace" fill="#f59e0b" opacity={0.7}>
        {cluster.layer}
      </text>
    </g>
  );
}

function NearbyNode({
  node,
  toScreen,
  scale
}: {
  node: DwgNearbyText;
  toScreen: (x: number, y: number) => { sx: number; sy: number };
  scale: number;
}) {
  const { sx, sy } = toScreen(node.x, node.y);
  const color = layerColor(node.layer);
  const fontSize = Math.max(8, Math.min(13, 10 * scale));
  const padX = 6;
  const padY = 3;
  const estimatedW = node.text.length * fontSize * 0.62 + padX * 2;
  const boxH = fontSize + padY * 2;

  return (
    <g>
      <circle cx={sx} cy={sy} r={2.5} fill={color} opacity={0.6} />
      <rect
        x={sx - estimatedW / 2}
        y={sy - boxH / 2}
        width={estimatedW}
        height={boxH}
        rx={2}
        fill={node.isGarbled ? '#1e1b1b' : '#0f172a'}
        stroke={node.isGarbled ? '#ef4444' : color}
        strokeWidth={node.isGarbled ? 1 : 0.75}
        opacity={0.9}
      />
      <text x={sx} y={sy + fontSize * 0.35} textAnchor="middle" fontSize={fontSize} fontFamily="monospace" fill={node.isGarbled ? '#fca5a5' : color} opacity={0.85}>
        {node.text}
      </text>
    </g>
  );
}

function LegendBar({ nearbyTexts }: { nearbyTexts: DwgNearbyText[] }) {
  const layers = [...new Set(nearbyTexts.map((node) => node.layer))];
  const garbledCount = nearbyTexts.filter((node) => node.isGarbled).length;
  return (
    <div className="flex shrink-0 items-center gap-3 overflow-x-auto border-t border-gray-800 bg-gray-900 px-3 py-1.5">
      <span className="flex shrink-0 items-center gap-1.5">
        <span className="inline-block h-2.5 w-2.5 rounded-sm border border-amber-500 bg-amber-900" />
        <span className="text-xs font-medium text-amber-400">当前文本</span>
      </span>
      <span className="shrink-0 text-gray-700">|</span>
      {garbledCount > 0 && (
        <>
          <span className="flex shrink-0 items-center gap-1.5">
            <span className="inline-block h-2.5 w-2.5 rounded-sm border border-red-500 bg-gray-900" />
            <span className="text-xs text-red-400">乱码 ({garbledCount})</span>
          </span>
          <span className="shrink-0 text-gray-700">|</span>
        </>
      )}
      {layers.map((layer) => (
        <span key={layer} className="flex shrink-0 items-center gap-1">
          <span className="inline-block h-2 w-2 rounded-full" style={{ background: layerColor(layer) }} />
          <span className="text-xs text-gray-500">{layer}</span>
        </span>
      ))}
      <span className="ml-auto shrink-0 text-xs text-gray-600">{nearbyTexts.length} 个上下文节点  ·  滚轮缩放 / 拖拽平移</span>
    </div>
  );
}
