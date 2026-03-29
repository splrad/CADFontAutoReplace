using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AFR_ACAD2026.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 编辑器的 AutoCAD 命令定义。
/// </summary>
public class MTextEditorCommand
{
    /// <summary>
    /// AFREDIT 命令: 选中多行文字 (MText) 并打开格式代码编辑器。
    /// </summary>
    [CommandMethod("AFREDIT")]
    public void EditMText()
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

            ObjectId mtextId = result.ObjectId;
            string originalContents;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var mtext = (MText)tr.GetObject(mtextId, OpenMode.ForRead);
                originalContents = mtext.Contents;
                tr.Commit();
            }

            log.Info($"AFREDIT: 已读取 MText 内容 (长度={originalContents.Length})");

            var vm = new MTextEditorViewModel(originalContents);
            var window = new MTextEditorWindow(vm);
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(window);

            if (window.DialogResult == true && vm.RawContents != originalContents)
            {
                using (doc.LockDocument())
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var mtext = (MText)tr.GetObject(mtextId, OpenMode.ForWrite);
                    mtext.Contents = vm.RawContents;
                    tr.Commit();
                }
                ed.Regen();
                log.Info("AFREDIT: MText 内容已更新。");
            }
            else
            {
                log.Info("AFREDIT: 用户取消或内容未修改。");
            }
        }
        catch (System.Exception ex)
        {
            log.Error("AFREDIT 命令执行失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }
}
