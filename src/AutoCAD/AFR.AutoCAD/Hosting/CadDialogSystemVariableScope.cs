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
            DiagnosticLogger.Log("系统变量", $"{variableName} 已恢复为 1");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"恢复 {variableName} 失败", ex);
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
