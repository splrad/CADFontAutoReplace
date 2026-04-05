using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AFR.Platform;
using AFR.Services;
using AFR.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if DEBUG

namespace AFR.Commands;

/// <summary>
/// MText 查看器的 AutoCAD 命令定义。仅在 Debug 构建时可用。
/// </summary>
public class MTextEditorCommand
{
    /// <summary>
    /// AFRVIEW 命令: 选中多行文字 (MText) 并打开格式代码查看器。
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

            var opts = new PromptEntityOptions("\n选择多行文字 (MText): ");
            opts.SetRejectMessage("\n只能选择多行文字对象。");
            opts.AddAllowedClass(typeof(MText), true);

            var result = ed.GetEntity(opts);
            if (result.Status != PromptStatus.OK) return;

            string contents;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var mtext = (MText)tr.GetObject(result.ObjectId, OpenMode.ForRead);
                contents = mtext.Contents;
                tr.Commit();
            }

            DiagnosticLogger.Log("AFRVIEW", $"已读取 MText 内容 (长度={contents.Length})");

            var window = new MTextEditorWindow(contents);
            window.ParentWindowHandle = AcadApp.MainWindow.Handle;
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
}

#endif
