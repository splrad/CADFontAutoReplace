using System.Globalization;
using AFR.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Hosting;

/// <summary>
/// 在插件执行期间保护 AutoCAD 对话框相关系统变量。
/// </summary>
internal sealed class CadDialogSystemVariableScope : IDisposable
{
    private const string FileDialogVariable = "FILEDIA";
    private const string CommandDialogVariable = "CMDDIA";

    private readonly int? _fileDialog;
    private readonly int? _commandDialog;
    private bool _disposed;

    private CadDialogSystemVariableScope(int? fileDialog, int? commandDialog)
    {
        _fileDialog = fileDialog;
        _commandDialog = commandDialog;
    }

    public static CadDialogSystemVariableScope Capture()
        => new(ReadInt(FileDialogVariable), ReadInt(CommandDialogVariable));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RestoreIfWasEnabled(FileDialogVariable, _fileDialog);
        RestoreIfWasEnabled(CommandDialogVariable, _commandDialog);
    }

    private static void RestoreIfWasEnabled(string variableName, int? originalValue)
    {
        if (originalValue != 1) return;

        int? currentValue = ReadInt(variableName);
        if (currentValue != 0) return;

        try
        {
            AcadApp.SetSystemVariable(variableName, 1);
            DiagnosticLogger.Ok(
                "CadDialogSystemVariableScope",
                "RestoreSystemVariable",
                "CAD 对话框系统变量已恢复",
                new Dictionary<string, object?> { ["variable"] = variableName });
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Fail(
                "CadDialogSystemVariableScope",
                "RestoreSystemVariable",
                "恢复 CAD 对话框系统变量失败",
                ex,
                new Dictionary<string, object?> { ["variable"] = variableName });
        }
    }

    private static int? ReadInt(string variableName)
    {
        try
        {
            object value = AcadApp.GetSystemVariable(variableName);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }
}
