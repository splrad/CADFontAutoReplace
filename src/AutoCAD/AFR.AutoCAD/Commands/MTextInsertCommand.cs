using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AFR.Platform;
using AFR.Services;
using AFR.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if DEBUG

namespace AFR.Commands;

/// <summary>
/// MText 插入器的 AutoCAD 命令定义。
/// 仅在 Debug 构建时可用，用于插入包含各种格式代码的 MText 实体以测试字体替换模块。
/// </summary>
public static class MTextInsertCommand
{
    /// <summary>
    /// AFRINSERT 命令：打开 MText 插入器窗口，选择模板或输入自定义内容后插入到图纸中。
    /// </summary>
    [CommandMethod("AFRINSERT")]
    public static void InsertMText()
    {
        var log = LogService.Instance;
        try
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            // 打开 UI 窗口获取内容
            var window = new MTextInsertWindow();
            PlatformManager.Host.ShowModalWindow(window);
            if (window.DialogResult != true || string.IsNullOrEmpty(window.ResultContents))
                return;

            string contents = window.ResultContents!;

            // 提示用户选择插入点
            var pointOpts = new PromptPointOptions("\n指定 MText 插入点: ");
            var pointResult = ed.GetPoint(pointOpts);
            if (pointResult.Status != PromptStatus.OK) return;

            Point3d insertPoint = pointResult.Value;

            // 根据当前视口计算合适的文字高度
            double textHeight = CalculateTextHeight();

            // 插入 MText 实体
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var mtext = new MText
                {
                    Location = insertPoint,
                    Contents = contents,
                    TextHeight = textHeight,
                    Width = 0 // 自动宽度
                };

                btr.AppendEntity(mtext);
                tr.AddNewlyCreatedDBObject(mtext, true);
                tr.Commit();
            }

            DiagnosticLogger.Log("AFRINSERT",
                $"已插入 MText: 位置=({insertPoint.X:F1},{insertPoint.Y:F1}) 高度={textHeight:F2} 内容长度={contents.Length}");
            ed.WriteMessage($"\n已插入 MText（内容长度={contents.Length}，文字高度={textHeight:F2}）。");
        }
        catch (System.Exception ex)
        {
            log.Error("MText 插入失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }

    /// <summary>
    /// 根据当前视口大小计算合适的文字高度。
    /// 目标：文字高度约为视口高度的 1/50，确保插入后肉眼可见且不过大。
    /// </summary>
    private static double CalculateTextHeight()
    {
        try
        {
            double viewHeight = (double)AcadApp.GetSystemVariable("VIEWSIZE");
            double height = viewHeight / 50.0;

            // 限制在合理范围
            if (height < 0.5) height = 0.5;
            if (height > 100) height = 100;
            return height;
        }
        catch
        {
            return 2.5; // 安全默认值
        }
    }
}

#endif
