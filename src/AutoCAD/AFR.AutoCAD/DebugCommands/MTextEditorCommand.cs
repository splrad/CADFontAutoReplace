#if DEBUG
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using System.Text;
using AFR.Platform;
using AFR.Services;
using AFR.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AFR.DebugCommands.MTextEditorCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// MText 格式代码查看器的 AutoCAD 命令定义。
/// 仅在 Debug 构建时可用，用于开发调试时检查 MText / MLeader 的内部格式代码。
/// </summary>
public class MTextEditorCommand
{
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
                        || fileName.IndexOf("ltypeshp", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || bigFont.IndexOf("ltypeshp", System.StringComparison.OrdinalIgnoreCase) >= 0)
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
            if (name.IndexOf("line", System.StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("type", System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            try
            {
                object? value = property.GetValue(instance);
                sb.AppendLine($"{name}={value}");
                if (value is ObjectId id && !id.IsNull)
                {
                    if (name.IndexOf("style", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        AppendTextStyle(sb, $"MLeaderStyle.{name}", id, tr);
                    else if (name.IndexOf("type", System.StringComparison.OrdinalIgnoreCase) >= 0)
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
