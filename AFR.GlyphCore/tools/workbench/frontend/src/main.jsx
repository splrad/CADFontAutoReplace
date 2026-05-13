import React, { useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './styles.css';

const TABS = [
  ['packages', '数据包'],
  ['review', '人工复核'],
  ['dataset', '训练数据集'],
  ['features', '特征生成'],
  ['training', '模型训练'],
  ['report', '模型报告']
];

async function api(path, options) {
  const response = await fetch(path, options);
  const data = await response.json();
  if (!response.ok || data.ok === false) {
    throw new Error(data.error || '请求失败');
  }
  return data;
}

function clusterList(payload) {
  return payload?.clusters || payload?.groups || [];
}

function initialTab() {
  const id = window.location.hash.replace(/^#/, '');
  return TABS.some(([tabId]) => tabId === id) ? id : 'review';
}

function App() {
  const [app, setApp] = useState(null);
  const [groups, setGroups] = useState({ groups: [], summary: {} });
  const [activeTab, setActiveTabState] = useState(initialTab);
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState('pending');
  const [riskFilter, setRiskFilter] = useState('all');
  const [layerFilter, setLayerFilter] = useState('all');
  const [fontFilter, setFontFilter] = useState('all');
  const [encodingFilter, setEncodingFilter] = useState('all');
  const reviewer = 'developer';
  const [selectedReviewGroupIds, setSelectedReviewGroupIds] = useState([]);
  const [reviewEdits, setReviewEdits] = useState({});
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [batchProgress, setBatchProgress] = useState('');

  useEffect(() => {
    refresh().catch(showError);
  }, []);

  function setActiveTab(tab) {
    setActiveTabState(tab);
    if (TABS.some(([id]) => id === tab)) {
      window.location.hash = tab;
    }
  }

  async function refresh() {
    const bootstrap = await api('/api/bootstrap');
    const reviewGroups = await api('/api/review-clusters');
    setApp(bootstrap);
    setGroups(reviewGroups);
  }

  function showError(err) {
    setError(err.message || String(err));
  }

  const data = app?.data || { records: [], reviewed: {}, summary: {}, manifest: {}, paths: {} };
  const records = data.records || [];
  const recordsById = useMemo(() => new Map(records.map((record) => [record.groupId, record])), [records]);
  const reviewed = data.reviewed || {};
  const summary = data.summary || {};
  const drawing = data.manifest?.drawing || {};

  const filteredGroups = useMemo(() => {
    const q = query.trim().toLowerCase();
    return clusterList(groups).filter((group) => {
      const variants = (group.sourceTextVariants || []).map((item) => item.text).join(' ');
      const layer = group.context?.layer || '';
      const font = group.context?.textStyleName || group.context?.textStyleFileName || '';
      const encoding = group.encodingPath || group.candidateSource || '';
      const text = [group.sourcePatternLabel, group.currentText, variants, group.candidateText, group.candidateSource, group.encodingPath, layer, font].join(' ').toLowerCase();
      if (q && !text.includes(q)) return false;
      if (filter === 'pending' && group.unreviewedCount <= 0) return false;
      if (filter === 'reviewed' && group.reviewStatus !== 'complete') return false;
      if (riskFilter !== 'all' && riskLevel(group) !== riskFilter) return false;
      if (layerFilter !== 'all' && layer !== layerFilter) return false;
      if (fontFilter !== 'all' && font !== fontFilter) return false;
      if (encodingFilter !== 'all' && encoding !== encodingFilter) return false;
      return true;
    });
  }, [groups, query, filter, riskFilter, layerFilter, fontFilter, encodingFilter]);
  const filterOptions = useMemo(() => {
    const allGroups = clusterList(groups);
    return {
      layers: optionValues(allGroups, (group) => group.context?.layer),
      fonts: optionValues(allGroups, (group) => group.context?.textStyleName || group.context?.textStyleFileName),
      encodings: optionValues(allGroups, (group) => group.encodingPath || group.candidateSource)
    };
  }, [groups]);
  const selectedReviewGroupIdSet = useMemo(() => new Set(selectedReviewGroupIds), [selectedReviewGroupIds]);
  const visibleGroupIdSet = useMemo(() => new Set(filteredGroups.map((group) => group.id)), [filteredGroups]);
  const selectedVisibleGroupIds = useMemo(
    () => selectedReviewGroupIds.filter((id) => visibleGroupIdSet.has(id)),
    [selectedReviewGroupIds, visibleGroupIdSet]
  );
  const selectedReviewVisibleCount = selectedVisibleGroupIds.length;
  const selectableReviewGroups = filteredGroups;
  function toggleReviewSelection(groupId) {
    if (!groupId) return;
    setSelectedReviewGroupIds((ids) => (
      ids.includes(groupId)
        ? ids.filter((id) => id !== groupId)
        : [...ids, groupId]
    ));
  }

  function clearReviewFilters() {
    setQuery('');
    setFilter('all');
    setRiskFilter('all');
    setLayerFilter('all');
    setFontFilter('all');
    setEncodingFilter('all');
  }

  function updateReviewEdit(groupId, patch) {
    if (!groupId) return;
    setSelectedReviewGroupIds((ids) => (ids.includes(groupId) ? ids : [...ids, groupId]));
    setReviewEdits((current) => ({
      ...current,
      [groupId]: {
        ...(current[groupId] || {}),
        ...patch
      }
    }));
  }

  async function refreshGroupsOnly() {
    try {
      const reviewGroups = await api('/api/review-clusters');
      setGroups(reviewGroups);
      return reviewGroups;
    } catch (err) {
      showError(err);
      return groups;
    }
  }

  async function selectPackage(id) {
    setBusy(true);
    try {
      const result = await api('/api/package', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ package: id })
      });
      const reviewGroups = await api('/api/review-clusters');
      setApp(result.bootstrap);
      setGroups(reviewGroups);
    } catch (err) {
      showError(err);
    } finally {
      setBusy(false);
    }
  }

  async function buildFeatures() {
    setBusy(true);
    try {
      const result = await api('/api/features', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
      setApp((value) => ({ ...value, data: result.data || value.data, features: result.features }));
      setGroups(result.reviewClusters || { groups: [], summary: {} });
      const promoted = Number(result.promoted || 0);
      const trainingRecords = Number(result.trainingRecords || result.features?.trainingDatasetRows || 0);
      setMessage(promoted > 0
        ? `已写入训练集 ${promoted} 条，Feature 已刷新`
        : `没有待入训练数据，已重建 ${trainingRecords} 条训练记录的 Feature`);
    } catch (err) {
      showError(err);
    } finally {
      setBusy(false);
    }
  }

  async function startTraining() {
    setBusy(true);
    try {
      const result = await api('/api/train', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
      const [dataResult, reviewGroups] = await Promise.all([api('/api/data'), api('/api/review-clusters')]);
      setApp((value) => ({ ...value, data: dataResult, features: result.features || value.features, training: result.training }));
      setGroups(reviewGroups);
      setActiveTab('training');
      setMessage(result.autoBuiltFeatures ? '已刷新 Feature 并开始训练' : '已开始训练');
    } catch (err) {
      showError(err);
    } finally {
      setBusy(false);
    }
  }

  async function deleteTrainingRecord(record) {
    const groupId = record?.groupId;
    if (!groupId || busy) return;
    const text = compactText(record.labelText || record.currentText || groupId, 36);
    if (!window.confirm(`从训练数据集中删除“${text}”，并回流到复核队列？`)) return;
    setBusy(true);
    setError('');
    try {
      const result = await api('/api/training-dataset/delete', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ groupIds: [groupId] })
      });
      setApp((value) => ({ ...value, data: result.data || value.data, features: result.features || value.features }));
      setGroups(result.reviewClusters || { groups: [], summary: {} });
      setMessage(`已删除 ${result.removed || 0} 条训练数据，并回流到复核队列`);
    } catch (err) {
      showError(err);
    } finally {
      setBusy(false);
    }
  }

  async function saveSelectedReviews(targetGroupIds = selectedVisibleGroupIds) {
    const targets = targetGroupIds;
    if (targets.length === 0 || busy) {
      setMessage('请先在表格中勾选需要保存的行');
      setBatchProgress('没有可保存的勾选行：请勾选右侧保存框，或先修改任意一行后再保存。');
      return;
    }
    setBusy(true);
    setError('');
    setMessage(`正在批量保存 ${targets.length} 个复核结果...`);
    setBatchProgress(`正在批量保存 ${targets.length} 个复核结果...`);
    try {
      const groupById = new Map(clusterList(groups).map((group) => [group.id, group]));
      const rows = targets.map((id) => {
        const group = groupById.get(id) || {};
        const edit = reviewEdits[id] || {};
        const saved = reviewedForGroup(group, reviewed);
        return {
          reviewGroupId: id,
          representativeGroupId: group.representativeRecords?.[0]?.groupId || group.sampleRecords?.[0]?.groupId || group.recordIds?.[0],
          labelAction: edit.labelAction || saved?.labelAction || group.recommendedAction || 'repair',
          labelText: edit.labelText ?? saved?.labelText ?? group.candidateText ?? group.currentText ?? '',
          candidateIndex: edit.candidateIndex ?? saved?.selectedCandidateIndex ?? group.recommendedCandidateIndex ?? 0
        };
      }).filter((row) => row.reviewGroupId);
      if (rows.length === 0) {
        setMessage('当前勾选行缺少可保存的文本簇信息');
        setBatchProgress('保存已取消：当前筛选下没有可提交的复核行。');
        return;
      }
      const result = await api('/api/review-table/confirm', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          rows,
          reviewer,
          note: 'manual-table-review',
          overwriteReviewed: true
        })
      });
      const [dataResult, reviewGroups] = await Promise.all([api('/api/data'), api('/api/review-clusters')]);
      setApp((value) => ({ ...value, data: dataResult }));
      setGroups(reviewGroups);
      const targetSet = new Set(targets);
      setSelectedReviewGroupIds((ids) => ids.filter((id) => !targetSet.has(id)));
      setReviewEdits((current) => Object.fromEntries(Object.entries(current).filter(([id]) => !targetSet.has(id))));
      setMessage(`已批量保存 ${result.reviewedGroups} 行，写入/更新 ${result.written} 条 reviewed`);
      setBatchProgress(result.errors?.length ? `已完成，另有 ${result.errors.length} 行未保存` : '批量保存完成');
    } catch (err) {
      showError(err);
      setBatchProgress(err.message || String(err));
    } finally {
      setBusy(false);
    }
  }

  function saveAllVisibleReviews() {
    const ids = selectableReviewGroups.map((group) => group.id);
    return saveSelectedReviews(ids);
  }

  useEffect(() => {
    if (activeTab !== 'training' || app?.training?.status !== 'running') return undefined;
    const timer = window.setInterval(async () => {
      const training = await api('/api/train');
      setApp((value) => ({ ...value, training }));
    }, 1200);
    return () => window.clearInterval(timer);
  }, [activeTab, app?.training?.status]);

  return (
    <div className="app-shell">
      <TopBar data={data} summary={summary} groupSummary={groups.summary || {}} drawing={drawing} activeTab={activeTab} setActiveTab={setActiveTab} />
      {activeTab === 'review' ? (
        <ReviewWorkspace
          filteredGroups={filteredGroups}
          query={query}
          filter={filter}
          riskFilter={riskFilter}
          layerFilter={layerFilter}
          fontFilter={fontFilter}
          encodingFilter={encodingFilter}
          filterOptions={filterOptions}
          setQuery={setQuery}
          setFilter={setFilter}
          setRiskFilter={setRiskFilter}
          setLayerFilter={setLayerFilter}
          setFontFilter={setFontFilter}
          setEncodingFilter={setEncodingFilter}
          reviewed={reviewed}
          recordsById={recordsById}
          batchProgress={batchProgress}
          selectedReviewGroupIdSet={selectedReviewGroupIdSet}
          selectedReviewVisibleCount={selectedReviewVisibleCount}
          selectableReviewCount={selectableReviewGroups.length}
          reviewEdits={reviewEdits}
          toggleReviewSelection={toggleReviewSelection}
          clearReviewFilters={clearReviewFilters}
          updateReviewEdit={updateReviewEdit}
          saveSelectedReviews={saveSelectedReviews}
          saveAllVisibleReviews={saveAllVisibleReviews}
          busy={busy}
        />
      ) : activeTab === 'dataset' ? (
        <main className="workspace review-workspace industrial-review-workspace dataset-workspace">
          <section className="center-stage industrial-review-stage">
            <TrainingDatasetPage
              dataset={data.trainingDataset || { records: [], summary: {} }}
              busy={busy}
              onDeleteRecord={deleteTrainingRecord}
            />
          </section>
        </main>
      ) : (
        <main className="module-workspace">
          <WorkflowViews
            activeTab={activeTab}
            app={app}
            groups={groups}
            onSelectPackage={selectPackage}
            onBuildFeatures={buildFeatures}
            onStartTraining={startTraining}
            busy={busy}
          />
        </main>
      )}
      <StatusBar
        message={message}
        error={error}
        busy={busy}
        activeTab={activeTab}
        selectedCount={selectedReviewVisibleCount}
        visibleCount={filteredGroups.length}
      />
    </div>
  );
}

function ReviewWorkspace(props) {
  const {
    filteredGroups,
    query,
    filter,
    riskFilter,
    layerFilter,
    fontFilter,
    encodingFilter,
    filterOptions,
    setQuery,
    setFilter,
    setRiskFilter,
    setLayerFilter,
    setFontFilter,
    setEncodingFilter,
    reviewed,
    recordsById,
    batchProgress,
    selectedReviewGroupIdSet,
    selectedReviewVisibleCount,
    selectableReviewCount,
    reviewEdits,
    toggleReviewSelection,
    clearReviewFilters,
    updateReviewEdit,
    saveSelectedReviews,
    saveAllVisibleReviews,
    busy
  } = props;
  return (
    <main className="workspace review-workspace industrial-review-workspace">
      <section className="center-stage industrial-review-stage">
        <div className="review-commandbar panel">
          <input
            className="global-search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="搜索：原文 / 正确文本 / 图层 / 字体 / 编码路径"
          />
          <div className="review-filters">
            <select value={filter} onChange={(event) => setFilter(event.target.value)} aria-label="状态">
              <option value="all">状态</option>
              <option value="pending">未审核</option>
              <option value="reviewed">已审核待入训练</option>
            </select>
            <select value={riskFilter} onChange={(event) => setRiskFilter(event.target.value)} aria-label="风险等级">
              <option value="all">风险等级</option>
              <option value="high">高风险</option>
              <option value="medium">中风险</option>
              <option value="low">低风险</option>
            </select>
            <select value={layerFilter} onChange={(event) => setLayerFilter(event.target.value)} aria-label="图层">
              <option value="all">图层</option>
              {(filterOptions.layers || []).map((value) => <option key={value} value={value}>{value}</option>)}
            </select>
            <select value={fontFilter} onChange={(event) => setFontFilter(event.target.value)} aria-label="字体">
              <option value="all">字体</option>
              {(filterOptions.fonts || []).map((value) => <option key={value} value={value}>{value}</option>)}
            </select>
            <select value={encodingFilter} onChange={(event) => setEncodingFilter(event.target.value)} aria-label="编码路径">
              <option value="all">编码路径</option>
              {(filterOptions.encodings || []).map((value) => <option key={value} value={value}>{value}</option>)}
            </select>
          </div>
          <div className="review-actions-row">
            <button type="button" disabled={busy} onClick={clearReviewFilters}>清空筛选</button>
            <button type="button" disabled={busy || selectedReviewVisibleCount <= 0} onClick={() => saveSelectedReviews()}>保存已选</button>
            <button type="button" className="primary solid" disabled={busy || selectableReviewCount <= 0} onClick={saveAllVisibleReviews}>
              应用到全部 {selectableReviewCount} 条
            </button>
          </div>
        </div>
        <ReviewTable
          reviewed={reviewed}
          recordsById={recordsById}
          groups={filteredGroups}
          selectedReviewGroupIdSet={selectedReviewGroupIdSet}
          reviewEdits={reviewEdits}
          onToggleReviewSelection={toggleReviewSelection}
          onUpdateReviewEdit={updateReviewEdit}
        />
        {batchProgress && <div className="batch-progress table-batch-progress">{batchProgress}</div>}
      </section>
    </main>
  );
}

function TopBar({ data, summary, groupSummary, drawing, activeTab, setActiveTab }) {
  const total = Number(summary.total || groupSummary.records || 0);
  const reviewedCount = Number(summary.reviewed || 0);
  const trainedCount = Number(summary.trained || data.trainingDataset?.summary?.total || 0);
  const packagePath = data.paths?.package || data.packageId || '未选择数据包';
  const drawingName = drawing.fileName || data.manifest?.drawing?.fileName || 'DWG 待载入';
  const stats = [
    ['待复核', Math.max(0, total - reviewedCount)],
    ['待入训练', reviewedCount],
    ['训练集', trainedCount]
  ];
  return (
    <header className="topbar">
      <div className="brand">
        <div className="logo">AFR</div>
        <div className="brand-copy">
          <div className="eyebrow">DBTEXT TRAINING</div>
          <div className="title">AFR 文枢训练工作台</div>
          <div className="meta">{packagePath} / {drawingName}</div>
        </div>
      </div>
      <nav className="tabs">
        {TABS.map(([id, label]) => (
          <button type="button" key={id} className={activeTab === id ? 'active' : ''} onClick={() => setActiveTab(id)}>{label}</button>
        ))}
      </nav>
      <div className="stats">
        {stats.map(([label, value]) => (
          <div className="stat" key={label}>
            <span>{label}</span>
            <strong>{value}</strong>
          </div>
        ))}
      </div>
    </header>
  );
}

function reviewedForGroup(group, reviewedMap) {
  if (!group || !reviewedMap) return null;
  for (const id of group.recordIds || []) {
    if (reviewedMap[id]) return reviewedMap[id];
  }
  for (const item of group.alreadyReviewedRecords || []) {
    const id = item?.groupId;
    if (id && reviewedMap[id]) return reviewedMap[id];
  }
  return null;
}

function sourceRecordForGroup(group, recordsById) {
  if (!group || !recordsById) return null;
  const ids = [
    group.representativeRecords?.[0]?.groupId,
    group.sampleRecords?.[0]?.groupId,
    ...(group.recordIds || [])
  ].filter(Boolean);
  for (const id of ids) {
    const record = recordsById.get(id);
    if (record) return record;
  }
  return group.representativeRecords?.[0] || group.sampleRecords?.[0] || null;
}

function compactText(value, limit = 64) {
  const text = String(value ?? '').replace(/\s+/g, ' ').trim();
  return text.length > limit ? `${text.slice(0, limit)}...` : text;
}

function candidateOptionsForGroup(group, saved, recordsById) {
  const sourceRecord = sourceRecordForGroup(group, recordsById);
  const options = [];
  const seen = new Set();
  function addOption(candidate, indexFallback, sourceFallback) {
    const text = String(candidate?.text ?? candidate?.candidateText ?? candidate ?? '');
    if (!text) return;
    if (seen.has(text)) return;
    seen.add(text);
    const index = Number.isInteger(candidate?.index)
      ? candidate.index
      : (Number.isInteger(indexFallback) ? indexFallback : null);
    const source = candidate?.source || candidate?.candidateSource || sourceFallback || '规则候选';
    const isNoOp = Boolean(candidate?.isNoOp);
    const order = options.length + 1;
    options.push({
      key: `${index === null ? 'text' : `idx-${index}`}-${order}`,
      index,
      text,
      source,
      isNoOp,
      label: `${order}. ${compactText(text)} · ${isNoOp ? '保留原文' : source}`
    });
  }

  (sourceRecord?.candidates || []).forEach((candidate, index) => addOption(candidate, index, candidate?.source));
  (saved?.candidates || []).forEach((candidate, index) => addOption(candidate, Number.isInteger(candidate?.index) ? candidate.index : index, candidate?.source));
  addOption({ text: group?.candidateText, source: group?.candidateSource || group?.encodingPath }, group?.recommendedCandidateIndex, group?.candidateSource || group?.encodingPath);
  addOption({ text: group?.currentText, source: '原文保留', isNoOp: true }, null, '原文保留');
  return options;
}

function initialCandidateKey(options, saved, group, finalText) {
  const byText = options.find((option) => option.text === finalText);
  if (byText) return byText.key;
  const savedIndex = saved?.selectedCandidateIndex;
  if (Number.isInteger(savedIndex)) {
    const bySavedIndex = options.find((option) => option.index === savedIndex);
    if (bySavedIndex) return bySavedIndex.key;
  }
  const recommendedIndex = group?.recommendedCandidateIndex;
  if (Number.isInteger(recommendedIndex)) {
    const byRecommendedIndex = options.find((option) => option.index === recommendedIndex);
    if (byRecommendedIndex) return byRecommendedIndex.key;
  }
  if (finalText) return '__manual__';
  return options[0]?.key || '__manual__';
}

function ReviewTable({ groups, reviewedMap, recordsById, selectedReviewGroupIdSet, reviewEdits, onToggleReviewSelection, onUpdateReviewEdit }) {
  const rows = groups || [];
  if (rows.length === 0) {
    return (
      <section className="review-table-section table-primary-section empty-grid">
        <strong>当前筛选没有数据</strong>
        <span>调整搜索或筛选条件后继续复核。</span>
      </section>
    );
  }
  return (
    <section className="review-table-section table-primary-section">
      <div className="review-table-wrap">
        <table className="review-table">
          <thead>
            <tr>
              <th>状态</th>
              <th>原文（文本簇）</th>
              <th>正确文本</th>
              <th>修复方式</th>
              <th>数量</th>
              <th className="col-check">☑</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((group, index) => {
              const edit = reviewEdits?.[group.id] || {};
              const saved = reviewedForGroup(group, reviewedMap);
              const selected = Boolean(selectedReviewGroupIdSet?.has(group.id));
              const baseFinalText = edit.labelText ?? saved?.labelText ?? group.candidateText ?? group.currentText ?? '';
              const candidateOptions = candidateOptionsForGroup(group, saved, recordsById);
              const candidateKey = edit.candidateKey || initialCandidateKey(candidateOptions, saved, group, baseFinalText);
              const selectedCandidateOption = candidateOptions.find((option) => option.key === candidateKey);
              const isManualText = candidateKey === '__manual__' || !selectedCandidateOption;
              const finalText = isManualText ? baseFinalText : selectedCandidateOption.text;
              const actionValue = edit.labelAction || saved?.labelAction || (selectedCandidateOption?.isNoOp ? 'keep' : (group.recommendedAction || 'repair'));
              const fixMode = edit.fixMode || (actionValue === 'keep' ? 'original' : (isManualText ? 'manual' : 'candidate'));
              const resolvedFixMode = fixMode === 'candidate' && candidateOptions.length === 0 ? 'manual' : fixMode;
              const sourceText = group.sourcePatternLabel || group.currentText || '--';
              const isReviewed = Number(group.unreviewedCount || 0) <= 0;
              const context = group.context || {};
              const layer = context.layer || context.baseLayer || '--';
              const font = context.textStyleName || context.textStyleFileName || 'current-noop';
              const encoding = group.encodingPath || group.candidateSource || '--';
              const impact = group.impactCount || group.count || 0;
              const risk = riskLevel(group);
              const candidateSelectValue = selectedCandidateOption ? candidateKey : (candidateOptions[0]?.key || '__manual__');
              function patchReview(patch) {
                if (!selected) onToggleReviewSelection?.(group.id);
                onUpdateReviewEdit?.(group.id, patch);
              }
              function chooseFinalText(value) {
                if (value === '__manual__') {
                  patchReview({
                    candidateMode: 'manual',
                    fixMode: 'manual',
                    candidateKey: '__manual__',
                    candidateIndex: null,
                    labelText: finalText || '',
                    labelAction: actionValue === 'keep' ? 'repair' : actionValue
                  });
                  return;
                }
                const option = candidateOptions.find((item) => item.key === value);
                if (!option) return;
                patchReview({
                  candidateMode: 'candidate',
                  fixMode: 'candidate',
                  candidateKey: option.key,
                  candidateIndex: option.index,
                  labelText: option.text,
                  labelAction: option.isNoOp ? 'keep' : 'repair'
                });
              }
              function setFixMode(value) {
                if (value === 'original') {
                  patchReview({
                    candidateMode: 'original',
                    fixMode: 'original',
                    candidateKey: '__original__',
                    candidateIndex: null,
                    labelText: group.currentText || sourceText,
                    labelAction: 'keep'
                  });
                  return;
                }
                if (value === 'manual') {
                  patchReview({
                    candidateMode: 'manual',
                    fixMode: 'manual',
                    candidateKey: '__manual__',
                    candidateIndex: null,
                    labelText: finalText || group.candidateText || group.currentText || '',
                    labelAction: actionValue === 'keep' ? 'repair' : actionValue
                  });
                  return;
                }
                const option = candidateOptions.find((item) => !item.isNoOp) || candidateOptions[0];
                if (!option) {
                  setFixMode('manual');
                  return;
                }
                patchReview({
                  candidateMode: 'candidate',
                  fixMode: 'candidate',
                  candidateKey: option.key,
                  candidateIndex: option.index,
                  labelText: option.text,
                  labelAction: option.isNoOp ? 'keep' : 'repair'
                });
              }
              return (
                <tr key={group.id} className={`${selected ? 'selected ' : ''}risk-${risk}`}>
                  <td className="status-cell">
                    <Pill tone={isReviewed ? 'ok' : 'warn'}>{isReviewed ? '已审核' : '未审核'}</Pill>
                    <small>#{index + 1}</small>
                  </td>
                  <td className="source-cell">
                    <div className="source-cluster" data-full={sourceText}>
                      <strong>{sourceText}</strong>
                      <small>编码路径：{encoding}　字体：{font}　图层：{layer}</small>
                    </div>
                  </td>
                  <td className="correct-text-cell">
                    {resolvedFixMode === 'manual' ? (
                      <input
                        className="inline-final manual"
                        value={finalText}
                        onChange={(event) => patchReview({
                          candidateMode: 'manual',
                          fixMode: 'manual',
                          candidateKey: '__manual__',
                          candidateIndex: null,
                          labelText: event.target.value,
                          labelAction: actionValue === 'keep' ? 'repair' : actionValue
                        })}
                      />
                    ) : resolvedFixMode === 'original' ? (
                      <div className="inline-final locked">
                        <span>{group.currentText || sourceText}</span>
                        <em>原文正确</em>
                      </div>
                    ) : (
                      <select
                        className="candidate-select inline-final"
                        value={candidateSelectValue}
                        onChange={(event) => chooseFinalText(event.target.value)}
                      >
                        {candidateOptions.map((option) => <option key={option.key} value={option.key}>{option.label}</option>)}
                        <option value="__manual__">候选都不对，手动输入</option>
                      </select>
                    )}
                    <small>{selectedCandidateOption?.source || group.candidateSource || group.encodingPath || '规则候选'}</small>
                  </td>
                  <td className="fix-mode-cell">
                    <div className="tiny-segmented">
                      <button type="button" className={resolvedFixMode === 'original' ? 'active' : ''} onClick={() => setFixMode('original')}>原文</button>
                      <button type="button" className={resolvedFixMode === 'candidate' ? 'active' : ''} onClick={() => setFixMode('candidate')}>候选</button>
                      <button type="button" className={resolvedFixMode === 'manual' ? 'active' : ''} onClick={() => setFixMode('manual')}>手动</button>
                    </div>
                  </td>
                  <td className="impact-cell">
                    <strong>{impact}</strong>
                    <i className={`risk-dot ${risk}`} title={riskLabel(risk)} />
                  </td>
                  <td className="select-cell">
                    <label className="save-check">
                      <input
                        type="checkbox"
                        checked={selected}
                        aria-label={`选择第 ${index + 1} 行`}
                        onChange={() => onToggleReviewSelection?.(group.id)}
                      />
                    </label>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function WorkflowViews({
  activeTab,
  app,
  groups,
  onSelectPackage,
  onBuildFeatures,
  onStartTraining,
  busy
}) {
  const packages = app?.packages || [];
  const features = app?.features || {};
  const training = app?.training || { status: 'idle', lines: [] };
  const trainingDataset = app?.data?.trainingDataset || { records: [], summary: {} };
  const report = app?.report || {};
  const validation = report.testReport?.summary || {};
  const activeLabel = tabLabel(activeTab);
  const pendingFeatureRows = Number(features.pendingReviewedRows || 0);
  const featureStatus = features.exists ? (features.stale ? '需刷新' : '已就绪') : '未生成';
  const featureButtonLabel = pendingFeatureRows > 0 ? '写入训练集 / 刷新 Feature' : '重建 Feature';
  return (
    <section className="module-view panel">
      <div className="module-header">
        <div>
          <div className="eyebrow">WORKFLOW MODULE</div>
          <h2>{activeLabel}</h2>
          <p>{moduleDescription(activeTab)}</p>
        </div>
        <div className="module-current">
          <span>当前数据包</span>
          <strong>{app?.data?.packageId || '未选择'}</strong>
        </div>
      </div>
      <div className="module-body">
        <div className="module-content">
          {activeTab === 'packages' && (
            <div className="package-grid">
              {packages.map((item) => (
                <button key={item.id} className="package-card" onClick={() => onSelectPackage(item.id)}>
                  <strong>{item.id}</strong>
                  <span>{item.drawing?.fileName || 'DWG'}</span>
                  <small>待入训练 {item.reviewed || 0} / 训练集 {item.trainingDataset || 0}</small>
                </button>
              ))}
            </div>
          )}
          {activeTab === 'features' && (
            <>
              <div className="metric-grid">
                <Info label="待入训练" value={pendingFeatureRows} />
                <Info label="训练集记录" value={features.trainingDatasetRows || trainingDataset.summary?.total || 0} />
                <Info label="Feature 状态" value={featureStatus} />
                <Info label="Feature 行数" value={features.rows || 0} />
                <Info label="正样本行数" value={features.positiveRows || 0} />
                <Info label="特征列数" value={features.featureColumns || 0} />
                <Info label="最后刷新" value={formatDateTime(features.modifiedUtc)} />
              </div>
              <div className="feature-sync-strip">
                <span>{pendingFeatureRows > 0 ? `待写入 ${pendingFeatureRows} 条` : '训练集已同步'}</span>
                <span>{features.stale ? 'Feature 需要重建' : 'Feature 匹配当前训练集'}</span>
              </div>
              <button className="primary wide" disabled={busy} onClick={onBuildFeatures}>{featureButtonLabel}</button>
            </>
          )}
          {activeTab === 'training' && (
            <>
              <div className="metric-grid">
                <Info label="状态" value={training.status || 'idle'} />
                <Info label="日志" value={training.logPath || '未开始'} />
              </div>
              <button className="primary wide" disabled={training.status === 'running'} onClick={onStartTraining}>开始训练</button>
              <pre className="log-box">{(training.lines || []).join('\n')}</pre>
            </>
          )}
          {activeTab === 'report' && (
            <>
              <div className="metric-grid">
                <Info label="误修率" value={pct(validation.falseRepairRate)} />
                <Info label="正确修复" value={validation.correctRepairs || 0} />
                <Info label="漏修" value={validation.missedRepairs || 0} />
                <Info label="召回率" value={pct(validation.repairRecall)} />
              </div>
              <pre className="log-box">{report.releaseCommand || '暂无发布命令'}</pre>
            </>
          )}
        </div>
      </div>
    </section>
  );
}

function TrainingDatasetPage({ dataset, busy, onDeleteRecord }) {
  const records = dataset?.records || [];
  const [query, setQuery] = useState('');
  const [actionFilter, setActionFilter] = useState('all');
  const [layerFilter, setLayerFilter] = useState('all');
  const [fontFilter, setFontFilter] = useState('all');
  const [sortMode, setSortMode] = useState('entered-desc');
  const tableScrollRef = useRef(null);
  const scrollFrameRef = useRef(0);
  const [scrollTop, setScrollTop] = useState(0);
  const [viewportHeight, setViewportHeight] = useState(720);
  const rowHeight = 36;
  const overscanRows = 18;
  const filters = useMemo(() => ({
    actions: optionValues(records, (record) => record.labelAction),
    layers: optionValues(records, (record) => record.layer),
    fonts: optionValues(records, (record) => record.font || record.textStyleName)
  }), [records]);
  const visibleRecords = useMemo(() => {
    const q = query.trim().toLowerCase();
    const matched = records.filter((record) => {
      const searchable = [
        record.currentText,
        record.labelText,
        record.candidateText,
        record.drawingFileName,
        record.drawingPath,
        record.handle,
        record.layer,
        record.ownerBlockName,
        record.textStyleName,
        record.font,
        record.bigFont,
        record.trainingSource
      ].join(' ').toLowerCase();
      if (q && !searchable.includes(q)) return false;
      if (actionFilter !== 'all' && record.labelAction !== actionFilter) return false;
      if (layerFilter !== 'all' && record.layer !== layerFilter) return false;
      if (fontFilter !== 'all' && (record.font || record.textStyleName) !== fontFilter) return false;
      return true;
    });
    return [...matched].sort((a, b) => {
      if (sortMode === 'entered-asc') return String(a.enteredTrainingUtc || '').localeCompare(String(b.enteredTrainingUtc || ''));
      if (sortMode === 'layer') return String(a.layer || '').localeCompare(String(b.layer || ''), 'zh-Hans-CN');
      if (sortMode === 'text') return String(a.currentText || '').localeCompare(String(b.currentText || ''), 'zh-Hans-CN');
      if (sortMode === 'action') return String(a.labelAction || '').localeCompare(String(b.labelAction || ''), 'zh-Hans-CN');
      return String(b.enteredTrainingUtc || '').localeCompare(String(a.enteredTrainingUtc || ''));
    });
  }, [records, query, actionFilter, layerFilter, fontFilter, sortMode]);
  const virtualStart = Math.max(0, Math.floor(scrollTop / rowHeight) - overscanRows);
  const virtualCount = Math.ceil(viewportHeight / rowHeight) + overscanRows * 2;
  const virtualEnd = Math.min(visibleRecords.length, virtualStart + virtualCount);
  const virtualRecords = visibleRecords.slice(virtualStart, virtualEnd);
  const topSpacerHeight = virtualStart * rowHeight;
  const bottomSpacerHeight = Math.max(0, (visibleRecords.length - virtualEnd) * rowHeight);
  useEffect(() => () => window.cancelAnimationFrame(scrollFrameRef.current), []);
  useEffect(() => {
    setScrollTop(0);
    if (tableScrollRef.current) {
      tableScrollRef.current.scrollTop = 0;
      setViewportHeight(tableScrollRef.current.clientHeight || 720);
    }
  }, [query, actionFilter, layerFilter, fontFilter, sortMode, records.length]);
  function handleDatasetScroll(event) {
    const nextScrollTop = event.currentTarget.scrollTop;
    const nextHeight = event.currentTarget.clientHeight || 720;
    window.cancelAnimationFrame(scrollFrameRef.current);
    scrollFrameRef.current = window.requestAnimationFrame(() => {
      setScrollTop(nextScrollTop);
      setViewportHeight(nextHeight);
    });
  }
  function clearFilters() {
    setQuery('');
    setActionFilter('all');
    setLayerFilter('all');
    setFontFilter('all');
    setSortMode('entered-desc');
  }

  return (
    <div className="training-dataset-page">
      <div className="review-commandbar dataset-commandbar panel">
        <input
          className="global-search"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="搜索：原文 / 正确文本 / 图纸 / Handle / Layer / Font"
        />
        <div className="review-filters dataset-filters">
          <select value={actionFilter} onChange={(event) => setActionFilter(event.target.value)} aria-label="标注动作">
            <option value="all">标注动作</option>
            {filters.actions.map((value) => <option key={value} value={value}>{actionLabel(value)}</option>)}
          </select>
          <select value={layerFilter} onChange={(event) => setLayerFilter(event.target.value)} aria-label="图层">
            <option value="all">图层</option>
            {filters.layers.map((value) => <option key={value} value={value}>{value}</option>)}
          </select>
          <select value={fontFilter} onChange={(event) => setFontFilter(event.target.value)} aria-label="字体">
            <option value="all">字体</option>
            {filters.fonts.map((value) => <option key={value} value={value}>{value}</option>)}
          </select>
          <select value={sortMode} onChange={(event) => setSortMode(event.target.value)} aria-label="排序">
            <option value="entered-desc">入集时间新到旧</option>
            <option value="entered-asc">入集时间旧到新</option>
            <option value="layer">按图层</option>
            <option value="text">按原文</option>
            <option value="action">按动作</option>
          </select>
        </div>
        <div className="review-actions-row dataset-actions-row">
          <span>训练集 {dataset?.summary?.total || 0}</span>
          <span>Feature {dataset?.summary?.featureRows || 0}</span>
          <span>显示 {visibleRecords.length}</span>
          <button type="button" disabled={busy} onClick={clearFilters}>清空筛选</button>
        </div>
      </div>
      {visibleRecords.length === 0 ? (
        <section className="review-table-section table-primary-section empty-grid training-empty">
          <strong>当前训练数据集没有匹配记录</strong>
          <span>生成 Feature 后，已进入训练集的记录会出现在这里。</span>
        </section>
      ) : (
        <section className="review-table-section table-primary-section training-table-section">
          <div className="review-table-wrap training-table-wrap" ref={tableScrollRef} onScroll={handleDatasetScroll}>
            <table className="review-table training-dataset-table">
              <thead>
                <tr>
                  <th>状态</th>
                  <th>原文</th>
                  <th>正确文本</th>
                  <th>来源图纸 / Handle / Layer</th>
                  <th>TextStyle / Font / BigFont</th>
                  <th>入集时间</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {topSpacerHeight > 0 && (
                  <tr className="dataset-spacer-row" aria-hidden="true">
                    <td colSpan={7} style={{ height: topSpacerHeight }} />
                  </tr>
                )}
                {virtualRecords.map((record, offset) => {
                  const index = virtualStart + offset;
                  return (
                  <tr key={record.groupId}>
                    <td className="status-cell training-status-cell">
                      <Pill tone={actionTone(record.labelAction)}>{actionLabel(record.labelAction)}</Pill>
                      <small>#{index + 1}</small>
                      <small>{record.featureRows || 0} feature</small>
                    </td>
                    <td className="source-cell training-text-cell">
                      <div className="source-cluster">
                        <strong>{record.currentText || '--'}</strong>
                        <small>Group：{record.groupId || '--'}</small>
                      </div>
                    </td>
                    <td className="source-cell training-correct-cell">
                      <div className="source-cluster">
                        <strong>{record.labelText || '--'}</strong>
                        <small>候选：{record.candidateText || '--'}</small>
                      </div>
                    </td>
                    <td className="source-cell training-meta-cell">
                      <div className="source-cluster">
                        <strong>{record.drawingFileName || 'DWG'}</strong>
                        <small>Handle：{record.handle || '--'}　Layer：{record.layer || '--'}</small>
                        <small>Block：{record.ownerBlockName || '--'}</small>
                      </div>
                    </td>
                    <td className="source-cell training-meta-cell">
                      <div className="source-cluster">
                        <strong>{record.textStyleName || '--'}</strong>
                        <small>Font：{record.font || '--'}　BigFont：{record.bigFont || '--'}</small>
                        <small>{record.isFromExternalReference ? '外部参照' : '当前图纸'}</small>
                      </div>
                    </td>
                    <td className="impact-cell training-time-cell">
                      <strong>{formatDateTime(record.enteredTrainingUtc)}</strong>
                      <small>{record.trainingSource || 'reviewed-jsonl'}</small>
                    </td>
                    <td className="training-action-cell">
                      <button type="button" className="risk" disabled={busy} onClick={() => onDeleteRecord?.(record)}>
                        删除并回流
                      </button>
                    </td>
                  </tr>
                  );
                })}
                {bottomSpacerHeight > 0 && (
                  <tr className="dataset-spacer-row" aria-hidden="true">
                    <td colSpan={7} style={{ height: bottomSpacerHeight }} />
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  );
}

function StatusBar({ message, error, busy, activeTab, selectedCount = 0, visibleCount = 0 }) {
  if (activeTab === 'review') {
    return (
      <footer className="statusbar">
        <span className={error ? 'status-error' : ''}>{busy ? '处理中...' : error || `已选择：${selectedCount} 项`}</span>
        <span>当前显示：{visibleCount}</span>
        <span className="status-ok">虚拟滚动模式</span>
        <span>已入训练集不在此页显示</span>
        {message && !error && <span className="status-message">{message}</span>}
      </footer>
    );
  }
  if (activeTab === 'dataset') {
    return (
      <footer className="statusbar">
        <span className={error ? 'status-error' : ''}>{busy ? '处理中...' : error || message || '就绪'}</span>
        <span>训练集记录来自当前 Feature/训练集清单</span>
        <span>删除后会从 Feature 中移除并回流复核</span>
      </footer>
    );
  }
  return (
    <footer className="statusbar">
      <span className={error ? 'status-error' : ''}>{busy ? '处理中...' : error || message || '就绪'}</span>
      <span>右侧勾选后批量保存</span>
      <span>正确文本优先使用候选下拉</span>
      <span>训练集页面管理已用于训练的数据</span>
    </footer>
  );
}

function Info({ label, value }) {
  return <div className="info"><span>{label}</span><strong>{value ?? '--'}</strong></div>;
}

function Pill({ tone = '', children }) {
  return <span className={`pill ${tone}`}>{children}</span>;
}

function pct(value) {
  const n = Number(value);
  return Number.isFinite(n) ? `${(n * 100).toFixed(2)}%` : '0%';
}

function optionValues(groups, selector) {
  return Array.from(new Set(
    (groups || [])
      .map((group) => String(selector(group) || '').trim())
      .filter(Boolean)
  )).sort((a, b) => a.localeCompare(b, 'zh-Hans-CN'));
}

function riskLevel(group) {
  const risk = group?.risk || group?.riskSummary || {};
  const highSignals = Number(risk.highRisk || 0) + Number(risk.currentUnsafe || 0) + Number(risk.candidateUnsafe || 0);
  if (highSignals > 0 || Number(group?.riskSignalCount || 0) > 0) return 'high';
  const mediumSignals = Number(risk.candidateConflict || 0) + Number(risk.hasNonRoundTrip || 0);
  if (mediumSignals > 0) return 'medium';
  if (group?.batchMode === 'sample' || Number(group?.contextSummary?.uniqueContexts || 0) > 1) return 'medium';
  return 'low';
}

function riskLabel(level) {
  return {
    high: '高风险',
    medium: '中风险',
    low: '低风险'
  }[level] || '低风险';
}

function actionLabel(action) {
  return {
    repair: '修复',
    keep: '保留',
    unsafe: '不安全',
    unknown: '未知',
    'glyph-issue': '字形问题'
  }[action] || action || '--';
}

function actionTone(action) {
  return {
    repair: 'ai',
    keep: 'ok',
    unsafe: 'risk',
    unknown: 'warn',
    'glyph-issue': 'warn'
  }[action] || '';
}

function formatDateTime(value) {
  if (!value) return '--';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN', { hour12: false });
}

function tabLabel(id) {
  return TABS.find(([tabId]) => tabId === id)?.[1] || id;
}

function moduleDescription(id) {
  return {
    packages: '切换候选包并查看 DWG 来源、Reviewed 进度和数据规模。',
    review: '只处理未审核、已审核待入训练和从训练集删除后回流的数据。',
    dataset: '查看、追溯和删除已经进入训练数据集的记录。',
    features: '将 reviewed JSONL 中待入训练记录写入训练集，并刷新 Feature CSV。',
    training: '基于当前 Feature 表启动本地模型训练并跟踪训练日志。',
    report: '查看模型验证指标、发布命令和当前模型清单。'
  }[id] || '';
}

createRoot(document.getElementById('root')).render(<App />);


