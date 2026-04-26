using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AFR.Platform;
using AFR.Services;
using AFR.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if DEBUG

namespace AFR.Commands;

/// <summary>
/// MText 格式代码查看器的 AutoCAD 命令定义。
/// 仅在 Debug 构建时可用，用于开发调试时检查 MText 的内部格式代码。
/// </summary>
public class MTextEditorCommand
{
    private const int MaxBig5Samples = 200;

    /// <summary>
    /// AFRVIEW 命令：让用户选中一个多行文字或多重引线对象，然后打开格式代码查看器窗口。
    /// </summary>
    [CommandMethod("AFRVIEW")]
    public void ViewMText()
    {
        var log = LogService.Instance;
        try
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            ObjectId objectId;
            var implied = ed.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                objectId = implied.Value.GetObjectIds()[0];
            }
            else
            {
                var opts = new PromptEntityOptions("\n选择多行文字或多重引线 (MText/MLeader): ");
                opts.SetRejectMessage("\n只能选择多行文字或多重引线对象。");
                opts.AddAllowedClass(typeof(MText), true);
                opts.AddAllowedClass(typeof(MLeader), true);

                var result = ed.GetEntity(opts);
                if (result.Status != PromptStatus.OK) return;
                objectId = result.ObjectId;
            }

            string contents;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var entity = (Entity)tr.GetObject(objectId, OpenMode.ForRead);
                contents = entity switch
                {
                    MText mtext => mtext.Contents,
                    MLeader mleader => BuildMLeaderDiagnostics(mleader, entity, tr),
                    _ => $"不支持的对象类型: {entity.GetType().FullName}"
                };
                tr.Commit();
            }

            DiagnosticLogger.Log("AFRVIEW", $"已读取对象内容 (长度={contents.Length})");

            var window = new MTextEditorWindow(contents);
            PlatformManager.Host.ShowModalWindow(window);
        }
        catch (System.Exception ex)
        {
            log.Error("MText 查看失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }

    /// <summary>
    /// AFRBIG5DIAG 命令：扫描当前图纸中的单行文字，输出编码诊断样本。
    /// <para>
    /// 仅用于 DEBUG 调试，不修改图纸。重点采集 DBText 的底层文本、文字样式、DXF 文本组码和候选转码结果，
    /// 用于判断图纸是否存在 Big5/非简体编码对象。
    /// </para>
    /// </summary>
    [CommandMethod("AFRBIG5DIAG")]
    public void DiagnoseBig5Text()
    {
        var log = LogService.Instance;
        try
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            string report;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                report = BuildBig5Diagnostics(doc.Database, tr);
                tr.Commit();
            }

            DiagnosticLogger.Log("Big5诊断", report);
            log.Info("Big5 编码诊断已输出到调试日志。命令: AFRLOG");
            log.Flush();
        }
        catch (System.Exception ex)
        {
            log.Error("Big5 编码诊断失败", ex);
        }
    }

    /// <summary>
    /// AFRBIG5LEFT 命令：扫描自动修复后仍疑似残留乱码的单行文字。
    /// <para>
    /// 仅用于 DEBUG 调试，不修改图纸。用于定位 DBText 修复模块未覆盖的残留样本。
    /// </para>
    /// </summary>
    [CommandMethod("AFRBIG5LEFT")]
    public void DiagnoseResidualBig5Text()
    {
        var log = LogService.Instance;
        try
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            string report;
            using (doc.LockDocument())
            {
                report = DbTextEncodingRepairService.BuildResidualDiagnostics(doc.Database);
            }

            DiagnosticLogger.Log("Big5残留诊断", report);
            log.Info("Big5 残留诊断已输出到调试日志。命令: AFRLOG");
            log.Flush();
        }
        catch (System.Exception ex)
        {
            log.Error("Big5 残留诊断失败", ex);
        }
    }

    private static string BuildBig5Diagnostics(Database db, Transaction tr)
    {
        var sb = new StringBuilder(64 * 1024);
        int totalDbText = 0;
        int sampled = 0;
        int candidateCount = 0;

        sb.AppendLine("=== AFR Big5 单行文字诊断 ===");
        sb.AppendLine($"Database={db.Filename}");
        sb.AppendLine("字段: Entity|Handle|Block|Style|Main|Big|Raw|CodePoints|GBKBytesToBig5|Big5BytesToGBK|ScoreRaw|ScoreA|ScoreB|Decision");

        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId btrId in bt)
        {
            BlockTableRecord? btr = null;
            try { btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead); }
            catch { continue; }

            foreach (ObjectId entId in btr)
            {
                DBText? text = null;
                try { text = tr.GetObject(entId, OpenMode.ForRead) as DBText; }
                catch { continue; }
                if (text == null) continue;

                totalDbText++;
                string raw = text.TextString ?? string.Empty;
                bool isSuspicious = ShouldSampleRawText(raw);

                string styleName = string.Empty;
                string mainFont = string.Empty;
                string bigFont = string.Empty;
                try
                {
                    var style = (TextStyleTableRecord)tr.GetObject(text.TextStyleId, OpenMode.ForRead);
                    styleName = style.Name;
                    mainFont = style.FileName ?? string.Empty;
                    bigFont = style.BigFontFileName ?? string.Empty;
                }
                catch { }

                string candidateA = TryTranscode(raw, 936, 950);
                string candidateB = TryTranscode(raw, 950, 936);
                int rawScore = ScoreChineseText(raw);
                int scoreA = ScoreChineseText(candidateA);
                int scoreB = ScoreChineseText(candidateB);
                string decision = DecideEncoding(rawScore, scoreA, scoreB);
                if (decision != "None") candidateCount++;
                if (!isSuspicious && decision == "None") continue;

                sampled++;
                sb.AppendLine(string.Join("|",
                    "DBText",
                    text.Handle.ToString(),
                    EscapeDiag(btr.Name),
                    EscapeDiag(styleName),
                    EscapeDiag(mainFont),
                    EscapeDiag(bigFont),
                    EscapeDiag(raw),
                    EscapeDiag(ToCodePoints(raw)),
                    EscapeDiag(candidateA),
                    EscapeDiag(candidateB),
                    rawScore.ToString(),
                    scoreA.ToString(),
                    scoreB.ToString(),
                    decision));
            }

            if (sampled >= MaxBig5Samples) break;
        }

        sb.Insert(0, $"TotalDBText={totalDbText}, Sampled={sampled}, Candidate={candidateCount}\n");
        return sb.ToString();
    }

    private static bool ShouldSampleRawText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (raw.Length < 2) return false;
        if (raw.All(static c => c < 128 && (char.IsDigit(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c)))) return false;
        return raw.Any(static c => c > 127) || raw.Any(static c => c == '?' || c == '�');
    }

    private static string TryTranscode(string text, int sourceCodePage, int targetCodePage)
    {
        try
        {
            // .NET 8 的非 Unicode 代码页需要 System.Text.Encoding.CodePages 包。
            // 为保持单 DLL 分发，诊断命令通过反射尝试注册；若运行时未携带该包则安全降级为空候选。
            TryRegisterCodePagesProvider();

            var source = Encoding.GetEncoding(
                sourceCodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            var target = Encoding.GetEncoding(
                targetCodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            byte[] bytes = source.GetBytes(text);
            return target.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryRegisterCodePagesProvider()
    {
        try
        {
            var providerType = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
            var instance = providerType?.GetProperty("Instance")?.GetValue(null) as EncodingProvider;
            if (instance != null)
                Encoding.RegisterProvider(instance);
        }
        catch
        {
        }
    }

    private static int ScoreChineseText(string text)
    {
        if (string.IsNullOrEmpty(text)) return -1000;

        int score = 0;
        foreach (char c in text)
        {
            if (IsCjk(c)) score += 5;
            else if (c is '�' or '?') score -= 8;
            else if (char.IsControl(c)) score -= 10;
            else if (c > 127) score -= 1;
        }

        if (Regex.IsMatch(text, @"[\u4E00-\u9FFF]{2,}")) score += 10;
        return score;
    }

    private static string DecideEncoding(int rawScore, int scoreA, int scoreB)
    {
        int best = Math.Max(scoreA, scoreB);
        if (best - rawScore < 15) return "None";
        return scoreA >= scoreB ? "GBKBytesToBig5" : "Big5BytesToGBK";
    }

    private static bool IsCjk(char c)
        => c >= '\u4E00' && c <= '\u9FFF';

    private static string ToCodePoints(string text)
        => string.Join(" ", text.Select(static c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));

    private static string EscapeDiag(string value)
        => (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("|", "¦");

    private static string BuildMLeaderDiagnostics(MLeader mleader, Entity entity, Transaction tr)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"对象: MLeader Handle={mleader.Handle}");
        sb.AppendLine($"Layer='{entity.Layer}', Linetype='{entity.Linetype}', LinetypeId={entity.LinetypeId}");
        AppendLayerDiagnostics(sb, entity.LayerId, tr);
        AppendLinetypeDiagnostics(sb, "Entity.LinetypeId", entity.LinetypeId, tr);
        sb.AppendLine($"ContentType={mleader.ContentType}");
        sb.AppendLine($"MLeaderStyle={mleader.MLeaderStyle}");
        sb.AppendLine($"TextStyleId={mleader.TextStyleId}");
        AppendTextStyle(sb, "MLeader.TextStyleId", mleader.TextStyleId, tr);

        if (!mleader.MLeaderStyle.IsNull)
        {
            try
            {
                var style = tr.GetObject(mleader.MLeaderStyle, OpenMode.ForRead);
                sb.AppendLine();
                sb.AppendLine($"MLeaderStyle 类型={style.GetType().FullName}");
                AppendProperty(sb, style, "Name");
                AppendObjectIdProperty(sb, style, "TextStyleId", tr);
                AppendLineRelatedProperties(sb, style, tr);
            }
            catch (System.Exception ex)
            {
                sb.AppendLine($"读取 MLeaderStyle 失败: {ex.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("MText 内容:");
        try
        {
            sb.AppendLine(mleader.MText?.Contents ?? "<null>");
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"读取 MText 失败: {ex.Message}");
        }

        AppendGlobalShapeDiagnostics(sb, mleader.Database, tr);

        return sb.ToString();
    }

    private static void AppendGlobalShapeDiagnostics(StringBuilder sb, Database db, Transaction tr)
    {
        sb.AppendLine();
        sb.AppendLine("全图 ShapeFile / 复杂线型扫描:");
        AppendShapeTextStyles(sb, db, tr);
        AppendShapeLinetypes(sb, db, tr);
    }

    private static void AppendShapeTextStyles(StringBuilder sb, Database db, Transaction tr)
    {
        sb.AppendLine("TextStyleTable 中的 ShapeFile / ltypeshp 引用:");
        bool found = false;

        try
        {
            var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in styleTable)
            {
                try
                {
                    var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    string fileName = style.FileName ?? string.Empty;
                    string bigFont = style.BigFontFileName ?? string.Empty;
                    if (style.IsShapeFile
                        || fileName.IndexOf("ltypeshp", StringComparison.OrdinalIgnoreCase) >= 0
                        || bigFont.IndexOf("ltypeshp", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = true;
                        sb.AppendLine($"  Style='{style.Name}', FileName='{fileName}', BigFont='{bigFont}', IsShapeFile={style.IsShapeFile}, IsDependent={style.IsDependent}, Id={id}");
                    }
                }
                catch (System.Exception ex)
                {
                    sb.AppendLine($"  读取文字样式 {id} 失败: {ex.Message}");
                }
            }
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"  扫描 TextStyleTable 失败: {ex.Message}");
            return;
        }

        if (!found)
            sb.AppendLine("  <未发现>");
    }

    private static void AppendShapeLinetypes(StringBuilder sb, Database db, Transaction tr)
    {
        sb.AppendLine("LinetypeTable 中引用 ShapeStyle 的线型:");
        bool found = false;

        try
        {
            var linetypeTable = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            foreach (ObjectId id in linetypeTable)
            {
                try
                {
                    var linetype = (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    for (int i = 0; i < linetype.NumDashes; i++)
                    {
                        ObjectId shapeStyleId;
                        try
                        {
                            shapeStyleId = linetype.ShapeStyleAt(i);
                        }
                        catch
                        {
                            continue;
                        }

                        if (shapeStyleId.IsNull)
                            continue;

                        found = true;
                        string shapeNumber = "?";
                        string shapeOffset = "?";
                        string shapeScale = "?";
                        string textAt = string.Empty;

                        try { shapeNumber = linetype.ShapeNumberAt(i).ToString(); } catch { }
                        try { shapeOffset = linetype.ShapeOffsetAt(i).ToString(); } catch { }
                        try { shapeScale = linetype.ShapeScaleAt(i).ToString(); } catch { }
                        try { textAt = linetype.TextAt(i) ?? string.Empty; } catch { }

                        sb.AppendLine($"  Linetype='{linetype.Name}', Dash={i}, ShapeStyleId={shapeStyleId}, ShapeNumber={shapeNumber}, Offset={shapeOffset}, Scale={shapeScale}, Text='{textAt}'");
                        AppendTextStyle(sb, "    ShapeStyle", shapeStyleId, tr);
                    }
                }
                catch (System.Exception ex)
                {
                    sb.AppendLine($"  读取线型 {id} 失败: {ex.Message}");
                }
            }
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"  扫描 LinetypeTable 失败: {ex.Message}");
            return;
        }

        if (!found)
            sb.AppendLine("  <未发现>");
    }

    private static void AppendLayerDiagnostics(StringBuilder sb, ObjectId layerId, Transaction tr)
    {
        if (layerId.IsNull)
        {
            sb.AppendLine("LayerRecord: <Null>");
            return;
        }

        try
        {
            var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
            sb.AppendLine($"LayerRecord: Name='{layer.Name}', LinetypeObjectId={layer.LinetypeObjectId}");
            AppendLinetypeDiagnostics(sb, "Layer.LinetypeObjectId", layer.LinetypeObjectId, tr);
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"LayerRecord: 读取失败 {ex.Message}");
        }
    }

    private static void AppendLinetypeDiagnostics(StringBuilder sb, string label, ObjectId linetypeId, Transaction tr)
    {
        if (linetypeId.IsNull)
        {
            sb.AppendLine($"{label}: <Null>");
            return;
        }

        try
        {
            var linetype = (LinetypeTableRecord)tr.GetObject(linetypeId, OpenMode.ForRead);
            sb.AppendLine($"{label}: Name='{linetype.Name}', NumDashes={linetype.NumDashes}");
            for (int i = 0; i < linetype.NumDashes; i++)
            {
                ObjectId shapeStyleId = linetype.ShapeStyleAt(i);
                string shapeNumber = "?";
                string shapeOffset = "?";
                string shapeScale = "?";
                string textAt = string.Empty;

                try { shapeNumber = linetype.ShapeNumberAt(i).ToString(); } catch { }
                try { shapeOffset = linetype.ShapeOffsetAt(i).ToString(); } catch { }
                try { shapeScale = linetype.ShapeScaleAt(i).ToString(); } catch { }
                try { textAt = linetype.TextAt(i) ?? string.Empty; } catch { }

                sb.AppendLine($"  Dash[{i}]: ShapeStyle={shapeStyleId}, ShapeNumber={shapeNumber}, Offset={shapeOffset}, Scale={shapeScale}, Text='{textAt}'");
                if (!shapeStyleId.IsNull)
                    AppendTextStyle(sb, $"  Dash[{i}].ShapeStyle", shapeStyleId, tr);
            }
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"{label}: 读取失败 {ex.Message}");
        }
    }

    private static void AppendLineRelatedProperties(StringBuilder sb, object instance, Transaction tr)
    {
        sb.AppendLine();
        sb.AppendLine("MLeaderStyle 线型相关属性:");
        foreach (var property in instance.GetType().GetProperties())
        {
            string name = property.Name;
            if (name.IndexOf("line", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("type", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            try
            {
                object? value = property.GetValue(instance);
                sb.AppendLine($"{name}={value}");
                if (value is ObjectId id && !id.IsNull)
                {
                    if (name.IndexOf("style", StringComparison.OrdinalIgnoreCase) >= 0)
                        AppendTextStyle(sb, $"MLeaderStyle.{name}", id, tr);
                    else if (name.IndexOf("type", StringComparison.OrdinalIgnoreCase) >= 0)
                        AppendLinetypeDiagnostics(sb, $"MLeaderStyle.{name}", id, tr);
                }
            }
            catch (System.Exception ex)
            {
                sb.AppendLine($"{name}=<读取失败: {ex.Message}>");
            }
        }
    }

    private static void AppendTextStyle(StringBuilder sb, string label, ObjectId styleId, Transaction tr)
    {
        if (styleId.IsNull)
        {
            sb.AppendLine($"{label}: <Null>");
            return;
        }

        try
        {
            var style = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead);
            sb.AppendLine($"{label}: Name='{style.Name}', FileName='{style.FileName}', BigFont='{style.BigFontFileName}', IsShapeFile={style.IsShapeFile}, IsDependent={style.IsDependent}");
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"{label}: 读取失败 {ex.Message}");
        }
    }

    private static void AppendProperty(StringBuilder sb, object instance, string propertyName)
    {
        try
        {
            var property = instance.GetType().GetProperty(propertyName);
            sb.AppendLine(property == null
                ? $"{propertyName}=<无属性>"
                : $"{propertyName}={property.GetValue(instance)}");
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"{propertyName}=<读取失败: {ex.Message}>");
        }
    }

    private static void AppendObjectIdProperty(StringBuilder sb, object instance, string propertyName, Transaction tr)
    {
        try
        {
            var property = instance.GetType().GetProperty(propertyName);
            if (property == null)
            {
                sb.AppendLine($"{propertyName}=<无属性>");
                return;
            }

            var value = property.GetValue(instance);
            sb.AppendLine($"{propertyName}={value}");
            if (value is ObjectId id)
                AppendTextStyle(sb, $"{instance.GetType().Name}.{propertyName}", id, tr);
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"{propertyName}=<读取失败: {ex.Message}>");
        }
    }
}

#endif
