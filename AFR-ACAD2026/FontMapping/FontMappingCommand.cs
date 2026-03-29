using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AFR_ACAD2026.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR_ACAD2026.FontMapping;

/// <summary>
/// 字体映射诊断命令。
/// </summary>
public class FontMappingCommand
{
    /// <summary>
    /// AFRMAP 命令: 显示当前文档所有文字样式的字体映射诊断信息。
    /// </summary>
    [CommandMethod("AFRMAP")]
    public void DiagnoseMapping()
    {
        var log = LogService.Instance;
        try
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            ed.WriteMessage("\n=== AFR 字体映射诊断 ===\n");

            // 检查默认映射
            string[] testFonts = ["@gbcbig", "@gbcbig.shx", "gbcbig", "gbenor", "txt"];
            foreach (var font in testFonts)
            {
                string mapped = FontMappingService.QueryMapping(font);
                bool same = string.Equals(font, mapped, StringComparison.OrdinalIgnoreCase);
                ed.WriteMessage($"  {font,-20} {(same ? "无映射" : $"→ {mapped}")}\n");
            }

            // 扫描当前文档的文字样式
            ed.WriteMessage("\n--- 当前文档文字样式 ---\n");
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var styleTable = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in styleTable)
                {
                    var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    string mainFont = style.FileName;
                    string bigFont = style.BigFontFileName;

                    if (string.IsNullOrEmpty(mainFont) && string.IsNullOrEmpty(bigFont))
                        continue;

                    string mainMapped = !string.IsNullOrEmpty(mainFont)
                        ? FontMappingService.QueryMapping(mainFont) : "";
                    string bigMapped = !string.IsNullOrEmpty(bigFont)
                        ? FontMappingService.QueryMapping(bigFont) : "";

                    ed.WriteMessage($"  样式: {style.Name}\n");
                    if (!string.IsNullOrEmpty(mainFont))
                    {
                        string mainStatus = string.Equals(mainFont, mainMapped, StringComparison.OrdinalIgnoreCase)
                            ? "" : $" → {mainMapped}";
                        ed.WriteMessage($"    主字体: {mainFont}{mainStatus}\n");
                    }
                    if (!string.IsNullOrEmpty(bigFont))
                    {
                        string bigStatus = string.Equals(bigFont, bigMapped, StringComparison.OrdinalIgnoreCase)
                            ? "" : $" → {bigMapped}";
                        ed.WriteMessage($"    大字体: {bigFont}{bigStatus}\n");
                    }
                }
                tr.Commit();
            }

            ed.WriteMessage("=== 诊断完成 ===\n");
        }
        catch (System.Exception ex)
        {
            log.Error("AFRMAP 命令执行失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }
}
