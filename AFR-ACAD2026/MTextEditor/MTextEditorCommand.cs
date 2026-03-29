using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AFR_ACAD2026.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 查看器的 AutoCAD 命令定义。
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

            log.Info($"AFRVIEW: 已读取 MText 内容 (长度={contents.Length})");

            var window = new MTextEditorWindow(contents);
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(window);
        }
        catch (System.Exception ex)
        {
            log.Error("AFRVIEW 命令执行失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }
}
