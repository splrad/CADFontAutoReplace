using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AFR.GlyphCore.TextRepair;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Services.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairService
{
    private const int DocumentFamilyMinimumSeedCount = 6;
    private const int DocumentFamilyMinimumRatioSeedCount = 3;
    private const double DocumentFamilyMinimumSeedRatio = 0.05;

    private static GlyphCoreTextRepairRunSummary _lastRunSummary;

    public static GlyphCoreTextRepairRunSummary LastRunSummary => _lastRunSummary;

    public static int Repair(Database db)
    {
        if (db == null)
            return 0;

        GlyphCoreTextRepairAdvisor? advisor = null;
        GlyphCoreDrawingIdentity drawing = GlyphCoreDrawingIdentity.FromDatabase(db);
        var counters = new GlyphCoreTextRepairCounters();
        var items = new List<GlyphCoreTextRepairItem>();
        var repairedSeeds = new List<GlyphCoreTextRepairItem>();

        using var tr = db.TransactionManager.StartTransaction();
        CollectItems(db, tr, drawing, items, counters);
        int promotedRawEvidence = GlyphCoreNativeDbTextEvidenceProjector.PromotePendingRawEquivalentEvidence(drawing, counters.Scanned);
        if (promotedRawEvidence > 0)
            RefreshNativeEvidenceDetections(drawing, items);

        List<GlyphCoreTextRepairItem> directEvidenceItems = items
            .Where(item => item.Detection.HasProblem)
            .ToList();
        ProcessCandidateGroups(tr, directEvidenceItems, ref advisor, counters, repairedSeeds);

        const int maxRipplePasses = 3;
        for (int pass = 1; pass <= maxRipplePasses && repairedSeeds.Count > 0; pass++)
        {
            List<GlyphCoreTextRepairItem> rippleItems = BuildRippleItems(items, repairedSeeds);
            if (rippleItems.Count == 0)
                break;

            int seedsBefore = repairedSeeds.Count;
            ProcessCandidateGroups(tr, rippleItems, ref advisor, counters, repairedSeeds);
            if (repairedSeeds.Count == seedsBefore)
                break;
        }

        List<GlyphCoreTextRepairItem> documentFamilyItems = BuildDocumentFamilyItems(items, repairedSeeds, counters.Scanned);
        if (documentFamilyItems.Count > 0)
        {
            counters.DocumentFamilyPromoted += documentFamilyItems.Count;
            ProcessCandidateGroups(tr, documentFamilyItems, ref advisor, counters, repairedSeeds);
        }

        tr.Commit();
        DiagnosticLogger.Log(
            "DBText文枢",
            $"扫描={counters.Scanned}, Hook强信号={counters.Problems}, 候选={counters.Candidates}, AI评分簇={counters.AiScored}, AI状态={counters.AiStatus}, " +
            $"阻塞={counters.Blocked}, 实际修复={counters.Repaired}, 等效强信号={counters.DocumentFamilyPromoted}, 错误={counters.Errors}, NativeEvidence={GlyphCoreNativeDbTextEvidenceProjector.GetSummary()}");
        _lastRunSummary = new GlyphCoreTextRepairRunSummary(
            counters.Scanned,
            counters.Problems,
            counters.Repaired,
            counters.ModelUnavailable,
            counters.AiStatus,
            counters.LastDecisionReason,
            counters.LastAiSummary);
        return counters.Repaired;
    }

    private static void CollectItems(
        Database db,
        Transaction tr,
        GlyphCoreDrawingIdentity drawing,
        List<GlyphCoreTextRepairItem> items,
        GlyphCoreTextRepairCounters counters)
    {
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        foreach (ObjectId blockId in blockTable)
        {
            BlockTableRecord block;
            try
            {
                block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                if (block.IsFromExternalReference || block.IsDependent)
                    continue;
            }
            catch
            {
                counters.Errors++;
                continue;
            }

            foreach (ObjectId entityId in block)
            {
                try
                {
                    if (tr.GetObject(entityId, OpenMode.ForRead, false, true) is not DBText dbText)
                        continue;

                    counters.Scanned++;
                    string current = dbText.TextString ?? string.Empty;
                    if (string.IsNullOrEmpty(current))
                        continue;

                    GlyphCoreTextRepairContext context = GlyphCoreTextRepairEntitySnapshotBuilder.BuildContext(db, tr, dbText, drawing);
                    GlyphCoreTextRepairProblemDetection detection = GlyphCoreTextRepairProblemDetector.Detect(context);
                    items.Add(new GlyphCoreTextRepairItem(
                        entityId,
                        context,
                        detection,
                        SafePosition(dbText),
                        SafeHeight(dbText)));
                }
                catch (Exception ex)
                {
                    counters.Errors++;
                    DiagnosticLogger.Log("DBText文枢", $"对象处理失败: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void RefreshNativeEvidenceDetections(
        GlyphCoreDrawingIdentity drawing,
        IReadOnlyList<GlyphCoreTextRepairItem> items)
    {
        foreach (GlyphCoreTextRepairItem item in items)
        {
            GlyphCoreNativeDecodeEvidenceStore.ApplyEvidence(drawing, item.Context);
            item.Detection = GlyphCoreTextRepairProblemDetector.Detect(item.Context);
        }
    }

    private static void ProcessCandidateGroups(
        Transaction tr,
        IReadOnlyList<GlyphCoreTextRepairItem> items,
        ref GlyphCoreTextRepairAdvisor? advisor,
        GlyphCoreTextRepairCounters counters,
        List<GlyphCoreTextRepairItem> repairedSeeds)
    {
        if (items.Count == 0)
            return;

        advisor ??= new GlyphCoreTextRepairAdvisor();
        counters.AiStatus = advisor.AiStatus;

        foreach (IGrouping<string, GlyphCoreTextRepairItem> group in items.GroupBy(BuildDecisionClusterKey))
        {
            List<GlyphCoreTextRepairItem> groupedItems = group.ToList();
            GlyphCoreTextRepairItem representative = groupedItems[0];
            IReadOnlyList<GlyphCoreTextRepairCandidate> generatedCandidates = representative.Detection.Candidates;
            counters.Problems += groupedItems.Count;
            counters.Candidates += groupedItems.Sum(item => item.Detection.Candidates.Count);

            if (!advisor.IsAiAvailable)
            {
                counters.ModelUnavailable = true;
                counters.Blocked += groupedItems.Count;
                MarkEvaluated(groupedItems);
                DiagnosticLogger.Log(
                    "DBText文枢",
                    $"簇={groupedItems.Count}, Hook强信号={representative.Detection.Reason}, 文枢模型不可用, AI状态={advisor.AiStatus}");
                continue;
            }

            GlyphCoreTextRepairDecision decision = advisor.Evaluate(
                representative.Context,
                generatedCandidates);
            if (generatedCandidates.Any(c => c.HasAiScore))
                counters.AiScored++;

            counters.LastDecisionReason = decision.Reason;
            counters.LastAiSummary = decision.AiSummary;

            if (decision.IsBlocked || !decision.ShouldRepair)
            {
                counters.Blocked += groupedItems.Count;
                MarkEvaluated(groupedItems);
                string action = decision.IsBlocked ? "阻断" : "未写回";
                DiagnosticLogger.Log(
                    "DBText文枢",
                    $"簇={groupedItems.Count}, Hook强信号={representative.Detection.Reason}, {action}={decision.Reason}, AI={decision.AiSummary}");
                continue;
            }

            foreach (GlyphCoreTextRepairItem item in groupedItems)
                ApplyDecision(tr, item, decision, counters, repairedSeeds);
        }
    }

    private static List<GlyphCoreTextRepairItem> BuildRippleItems(
        IReadOnlyList<GlyphCoreTextRepairItem> items,
        IReadOnlyList<GlyphCoreTextRepairItem> repairedSeeds)
    {
        var rippleItems = new List<GlyphCoreTextRepairItem>();
        foreach (GlyphCoreTextRepairItem item in items)
        {
            if (item.Evaluated || item.Repaired)
                continue;

            List<GlyphCoreTextRepairItem> nearbySeeds = repairedSeeds
                .Where(seed => CanRipple(seed, item))
                .ToList();
            if (nearbySeeds.Count == 0)
                continue;

            GlyphCoreTextRepairItem nearestSeed = nearbySeeds
                .OrderBy(seed => seed.Position.DistanceTo(item.Position))
                .First();
            GlyphCoreNativeDecodeEvidenceStore.ApplyRippleEvidence(
                item.Context,
                nearestSeed.Context,
                nearbySeeds.Count,
                RippleDistanceRatio(nearestSeed, item),
                SeedQuality(nearestSeed.Context));
            item.Detection = GlyphCoreTextRepairProblemDetector.Detect(item.Context);
            if (item.Detection.HasProblem)
                rippleItems.Add(item);
        }

        return rippleItems;
    }

    private static List<GlyphCoreTextRepairItem> BuildDocumentFamilyItems(
        IReadOnlyList<GlyphCoreTextRepairItem> items,
        IReadOnlyList<GlyphCoreTextRepairItem> repairedSeeds,
        int scanned)
    {
        var seedGroups = repairedSeeds
            .Where(IsDocumentFamilySeed)
            .GroupBy(seed => BuildDocumentFamilyKey(seed.Context))
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => new
            {
                Seeds = group.ToList(),
                Count = group.Count()
            })
            .Where(group => MeetsDocumentFamilyPromotionThreshold(group.Count, scanned))
            .OrderByDescending(group => group.Count)
            .ToList();
        if (seedGroups.Count == 0)
            return new List<GlyphCoreTextRepairItem>();

        var promoted = new List<GlyphCoreTextRepairItem>();
        foreach (GlyphCoreTextRepairItem item in items)
        {
            if (item.Evaluated || item.Repaired || item.Context.HasNativeDecodeEvidence)
                continue;
            if (IsShortPunctuationOrSymbolText(item.Context.CurrentText))
                continue;

            foreach (var seedGroup in seedGroups)
            {
                GlyphCoreTextRepairItem bestSeed = seedGroup.Seeds
                    .OrderByDescending(seed => SeedQuality(seed.Context))
                    .First();
                if (!HasDocumentFamilyRepairCandidate(item.Detection.Candidates, bestSeed.Context))
                    continue;

                float seedQuality = seedGroup.Seeds
                    .Select(seed => SeedQuality(seed.Context))
                    .DefaultIfEmpty(0.25f)
                    .Max();
                GlyphCoreNativeDecodeEvidenceStore.ApplyDocumentFamilyEvidence(
                    item.Context,
                    bestSeed.Context,
                    seedGroup.Count,
                    seedQuality);
                item.Detection = GlyphCoreTextRepairProblemDetector.Detect(item.Context);
                if (item.Detection.HasProblem && HasDocumentFamilyRepairCandidate(item.Detection.Candidates, bestSeed.Context))
                    promoted.Add(item);
                break;
            }
        }

        return promoted;
    }

    private static void ApplyDecision(
        Transaction tr,
        GlyphCoreTextRepairItem item,
        GlyphCoreTextRepairDecision decision,
        GlyphCoreTextRepairCounters counters,
        List<GlyphCoreTextRepairItem> repairedSeeds)
    {
        try
        {
            if (tr.GetObject(item.EntityId, OpenMode.ForRead, false, true) is not DBText dbText)
            {
                counters.Errors++;
                item.Evaluated = true;
                return;
            }

            string current = dbText.TextString ?? string.Empty;
            dbText.UpgradeOpen();
            dbText.TextString = decision.SelectedText;
            item.Context.CurrentText = decision.SelectedText;
            item.Repaired = true;
            item.Evaluated = true;
            item.RepairedText = decision.SelectedText;
            counters.Repaired++;
            repairedSeeds.Add(item);
            DiagnosticLogger.Log(
                "DBText文枢",
                $"Handle={item.Context.Handle}: '{Trim(current)}' -> '{Trim(decision.SelectedText)}', Hook强信号={item.Detection.Reason}, Reason={decision.Reason}, AI={decision.AiSummary}");
        }
        catch (Exception ex)
        {
            item.Evaluated = true;
            counters.Errors++;
            counters.LastDecisionReason = "write-failed";
            counters.LastAiSummary = ex.GetType().Name + ": " + ex.Message;
            DiagnosticLogger.Log("DBText文枢", $"Handle={item.Context.Handle}: 写回失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool CanRipple(GlyphCoreTextRepairItem seed, GlyphCoreTextRepairItem target)
    {
        if (!seed.Repaired || !seed.Context.HasNativeDecodeEvidence || !seed.Context.NativeDecodeFamilyMismatch)
            return false;

        if (!SameTextContext(seed.Context, target.Context))
            return false;

        double height = Math.Max(Math.Max(seed.Height, target.Height), 1.0);
        double maxDistance = height * 20.0;
        return seed.Position.DistanceTo(target.Position) <= maxDistance;
    }

    private static bool IsDocumentFamilySeed(GlyphCoreTextRepairItem seed)
    {
        return seed.Repaired
               && seed.Context.HasNativeDecodeEvidence
               && seed.Context.NativeDecodeFamilyMismatch
               && !string.Equals(seed.Context.NativeDecodeEvidenceScope, "document-family", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MeetsDocumentFamilyPromotionThreshold(int seedCount, int scanned)
    {
        if (seedCount >= DocumentFamilyMinimumSeedCount)
            return true;

        return seedCount >= DocumentFamilyMinimumRatioSeedCount
               && (double)seedCount / Math.Max(1, scanned) >= DocumentFamilyMinimumSeedRatio;
    }

    private static string BuildDocumentFamilyKey(GlyphCoreTextRepairContext context)
    {
        if (context == null || !context.NativeDecodeFamilyMismatch)
            return string.Empty;

        return string.Join("|", new[]
        {
            context.NativeDecodeSourceCodePageFamily ?? string.Empty,
            context.NativeDecodeAppliedCodePageFamily ?? string.Empty
        });
    }

    private static bool HasDocumentFamilyRepairCandidate(
        IReadOnlyList<GlyphCoreTextRepairCandidate> candidates,
        GlyphCoreTextRepairContext seedContext)
    {
        return candidates.Any(candidate => !candidate.IsNoOp
                                           && candidate.IsRoundTrip
                                           && CandidateSourceMatchesFamily(candidate.Source, seedContext));
    }

    private static bool CandidateSourceMatchesFamily(string candidateSource, GlyphCoreTextRepairContext seedContext)
    {
        if (string.IsNullOrWhiteSpace(candidateSource) || seedContext == null)
            return false;

        string sourceFamily = seedContext.NativeDecodeSourceCodePageFamily ?? string.Empty;
        string appliedFamily = seedContext.NativeDecodeAppliedCodePageFamily ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceFamily) || string.IsNullOrWhiteSpace(appliedFamily))
            return false;

        return candidateSource.IndexOf(sourceFamily, StringComparison.OrdinalIgnoreCase) >= 0
               && candidateSource.IndexOf(appliedFamily, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsShortPunctuationOrSymbolText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2)
            return false;

        for (int i = 0; i < text.Length; i++)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, i);
            if (category != UnicodeCategory.ConnectorPunctuation
                && category != UnicodeCategory.DashPunctuation
                && category != UnicodeCategory.OpenPunctuation
                && category != UnicodeCategory.ClosePunctuation
                && category != UnicodeCategory.InitialQuotePunctuation
                && category != UnicodeCategory.FinalQuotePunctuation
                && category != UnicodeCategory.OtherPunctuation
                && category != UnicodeCategory.MathSymbol
                && category != UnicodeCategory.CurrencySymbol
                && category != UnicodeCategory.ModifierSymbol
                && category != UnicodeCategory.OtherSymbol)
                return false;
        }

        return true;
    }

    private static bool SameTextContext(GlyphCoreTextRepairContext left, GlyphCoreTextRepairContext right)
    {
        return string.Equals(LayerSemanticClass(left.Layer), LayerSemanticClass(right.Layer), StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.OwnerBlockName, right.OwnerBlockName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.TextStyleName, right.TextStyleName, StringComparison.OrdinalIgnoreCase);
    }

    private static float RippleDistanceRatio(GlyphCoreTextRepairItem seed, GlyphCoreTextRepairItem target)
    {
        double height = Math.Max(Math.Max(seed.Height, target.Height), 1.0);
        double maxDistance = height * 20.0;
        if (maxDistance <= 0)
            return 1f;

        return (float)Math.Max(0.0, Math.Min(1.0, seed.Position.DistanceTo(target.Position) / maxDistance));
    }

    private static float SeedQuality(GlyphCoreTextRepairContext context)
    {
        float evidenceQuality = Math.Max(context.NativeDecodeObjectCorrelation, context.NativeDecodeClusterCorrelation);
        if (context.HasHookRawDecodeEvidence)
            evidenceQuality = Math.Max(evidenceQuality, context.HookRawConfidence);
        if (evidenceQuality <= 0)
            evidenceQuality = 0.5f;
        return Math.Max(0f, Math.Min(1f, evidenceQuality));
    }

    private static string BuildDecisionClusterKey(GlyphCoreTextRepairItem item)
    {
        GlyphCoreTextRepairContext context = item.Context;
        return string.Join("|", new[]
        {
            context.NativeDecodeSourceCodePageFamily,
            context.NativeDecodeAppliedCodePageFamily,
            context.NativeDecodeHookHitType,
            context.NativeDecodeEvidenceScope,
            LayerSemanticClass(context.Layer),
            context.OwnerBlockName,
            context.TextStyleName,
            BuildRecommendedActionSignature(item),
            context.CurrentText,
            BuildCandidateSignature(item.Detection.Candidates)
        });
    }

    private static string BuildRecommendedActionSignature(GlyphCoreTextRepairItem item)
    {
        if (!item.Detection.HasProblem)
            return "keep";

        return item.Detection.Candidates.Any(candidate => !candidate.IsNoOp)
            ? "repair-candidate"
            : "unknown";
    }

    private static string LayerSemanticClass(string layer)
    {
        string value = (layer ?? string.Empty).ToUpperInvariant();
        if (ContainsAny(value, "FIRE", "喷淋", "消防", "消火", "HYDRANT"))
            return "fire";
        if (ContainsAny(value, "WATER", "给水", "排水", "DRAIN", "PIPE", "PLUMB"))
            return "water";
        if (ContainsAny(value, "ELEC", "电气", "照明", "POWER"))
            return "electric";
        if (ContainsAny(value, "HVAC", "暖通", "风管", "AIR"))
            return "hvac";
        if (ContainsAny(value, "TEXT", "DIM", "标注", "文字"))
            return "annotation";
        return string.IsNullOrWhiteSpace(value) ? "unknown" : "general";
    }

    private static bool ContainsAny(string value, params string[] tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (value.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static string BuildCandidateSignature(IReadOnlyList<GlyphCoreTextRepairCandidate> candidates)
    {
        return string.Join(";", candidates.Select(candidate => candidate.Source + "=" + candidate.Text));
    }

    private static void MarkEvaluated(IEnumerable<GlyphCoreTextRepairItem> items)
    {
        foreach (GlyphCoreTextRepairItem item in items)
            item.Evaluated = true;
    }

    private static Point3d SafePosition(DBText dbText)
    {
        try { return dbText.Position; }
        catch { return Point3d.Origin; }
    }

    private static double SafeHeight(DBText dbText)
    {
        try { return Math.Max(0, dbText.Height); }
        catch { return 0; }
    }

    public static void WriteCommandLineSummary(GlyphCoreTextRepairRunSummary summary)
    {
        if (summary.Problems <= 0)
            return;

        var editor = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (editor == null)
            return;

        string message;
        if (summary.ModelUnavailable)
        {
            string status = string.IsNullOrWhiteSpace(summary.AiStatus) ? "unknown" : summary.AiStatus;
            message = $"[AFR 文枢] 当前 文枢 决策模型不可用（AI状态：{status}）；未执行 DBText 自动修复。";
        }
        else if (summary.Repaired > 0)
            message = $"[AFR 文枢] 检测到 DBText native 解码强信号；文枢 已完成 AI 决策并成功修复 {summary.Repaired} 项。";
        else
        {
            string detail = BuildDecisionDetail(summary);
            message = "[AFR 文枢] 检测到 DBText native 解码强信号；文枢 AI 选择不写回" + detail + "。";
        }

        string summaryLine = $"扫描={summary.Scanned}, Hook强信号={summary.Problems}, 修复={summary.Repaired}, 未修复={summary.Unrepaired}";

        DiagnosticLogger.Log("DBText文枢", summaryLine);
        DiagnosticLogger.Log("DBText文枢", message);
        DiagnosticLogger.Flush();

        editor.WriteMessage($"\n{message}\n");
    }

    private static string Trim(string text)
    {
        return text.Length <= 60 ? text : text.Substring(0, 60) + "...";
    }

    private static string BuildDecisionDetail(GlyphCoreTextRepairRunSummary summary)
    {
        if (string.IsNullOrWhiteSpace(summary.DecisionReason) && string.IsNullOrWhiteSpace(summary.AiSummary))
            return string.Empty;

        string reason = string.IsNullOrWhiteSpace(summary.DecisionReason)
            ? string.Empty
            : "原因：" + summary.DecisionReason;
        string ai = string.IsNullOrWhiteSpace(summary.AiSummary)
            ? string.Empty
            : "AI：" + Trim(summary.AiSummary);
        string separator = !string.IsNullOrEmpty(reason) && !string.IsNullOrEmpty(ai) ? "；" : string.Empty;
        return "（" + reason + separator + ai + "）";
    }

    private sealed class GlyphCoreTextRepairCounters
    {
        public int Scanned;
        public int Candidates;
        public int AiScored;
        public int Problems;
        public int Repaired;
        public int Blocked;
        public int Errors;
        public int DocumentFamilyPromoted;
        public bool ModelUnavailable;
        public string AiStatus = "not-invoked";
        public string LastDecisionReason = string.Empty;
        public string LastAiSummary = string.Empty;
    }

    private sealed class GlyphCoreTextRepairItem
    {
        public GlyphCoreTextRepairItem(
            ObjectId entityId,
            GlyphCoreTextRepairContext context,
            GlyphCoreTextRepairProblemDetection detection,
            Point3d position,
            double height)
        {
            EntityId = entityId;
            Context = context;
            Detection = detection;
            Position = position;
            Height = height;
        }

        public ObjectId EntityId { get; }
        public GlyphCoreTextRepairContext Context { get; }
        public GlyphCoreTextRepairProblemDetection Detection { get; set; }
        public Point3d Position { get; }
        public double Height { get; }
        public bool Evaluated { get; set; }
        public bool Repaired { get; set; }
        public string RepairedText { get; set; } = string.Empty;
    }
}

internal readonly record struct GlyphCoreTextRepairRunSummary(
    int Scanned,
    int Problems,
    int Repaired,
    bool ModelUnavailable,
    string AiStatus,
    string DecisionReason,
    string AiSummary)
{
    public int Unrepaired => Math.Max(0, Problems - Repaired);
}

