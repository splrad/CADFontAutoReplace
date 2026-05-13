import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@/styles/globals.css';
import { App } from '@/App';

const root = document.getElementById('root');
if (!root) throw new Error('[AFR] 找不到挂载节点 #root');

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>
);
