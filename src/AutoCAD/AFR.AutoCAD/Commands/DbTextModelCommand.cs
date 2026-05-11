using System;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AFR.Constants;
using AFR.DbTextRepairModel;
using AFR.Services.DbTextRepair;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Commands;

public sealed class DbTextModelCommand
{
    [CommandMethod(CommandNames.DbTextModel, CommandFlags.Modal)]
    public static void Execute()
    {
        var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null)
            return;

        string operation = PromptOperation(ed);
        if (string.Equals(operation, "Train", StringComparison.OrdinalIgnoreCase))
        {
            Train(ed);
            return;
        }

        if (string.Equals(operation, "Eval", StringComparison.OrdinalIgnoreCase))
        {
            Eval(ed);
            return;
        }

        ShowStatus(ed);
    }

    private static string PromptOperation(Editor ed)
    {
        var options = new PromptKeywordOptions("\n选择 DBText 模型操作 [Status/Train/Eval] <Status>: ");
        options.Keywords.Add("Status");
        options.Keywords.Add("Train");
        options.Keywords.Add("Eval");
        options.AllowNone = true;

        PromptResult result = ed.GetKeywords(options);
        if (result.Status != PromptStatus.OK || string.IsNullOrEmpty(result.StringResult))
            return "Status";

        return result.StringResult;
    }

    private static void ShowStatus(Editor ed)
    {
        var report = DbTextRepairModelStore.ForceMerge();
        var index = DbTextRepairModelStore.LoadIndex(out _);

        ed.WriteMessage("\n=== AFR DBText Repair Model ===\n");
        ed.WriteMessage($"ActiveDirectory: {DbTextRepairModelStore.ActiveDirectory}\n");
        ed.WriteMessage($"CanonicalPath: {DbTextRepairModelStore.CanonicalPath}\n");
        ed.WriteMessage($"Labels: {index.LabelCount}\n");
        ed.WriteMessage($"Conflicts: {index.ConflictCount}\n");
        ed.WriteMessage($"TrainingDataHash: {index.TrainingDataHash}\n");
        ed.WriteMessage($"NeuralParams: {index.NeuralParameterRecordCount}\n");
        ed.WriteMessage($"ActiveNeuralParams: {index.HasActiveNeuralParameters}\n");
        ed.WriteMessage($"LastMerge: {report.ToSummary()}\n");
    }

    private static void Train(Editor ed)
    {
        var report = DbTextRepairModelStore.ForceMerge();
        var index = DbTextRepairModelStore.LoadIndex(out _);
        DbTextNeuralTrainingResult result = DbTextNeuralTrainer.Train(index.Labels, BuildSourceSetId());
        ed.WriteMessage("\n=== AFR DBText Neural Train ===\n");
        ed.WriteMessage($"Model: {DbTextRepairModelStore.CanonicalPath}\n");
        ed.WriteMessage($"Merge: {report.ToSummary()}\n");

        if (!result.Success || result.Record == null)
        {
            ed.WriteMessage($"TrainFailed: {result.Error}\n");
            return;
        }

        DbTextRepairModelStore.AppendRecord(result.Record);
        var updatedIndex = DbTextRepairModelStore.LoadIndex(out _);
        ed.WriteMessage($"TrainingDataHash: {result.Record.TrainingDataHash}\n");
        ed.WriteMessage($"TrainingRecordCount: {result.Record.TrainingRecordCount}\n");
        ed.WriteMessage($"Summary: {result.Summary}\n");
        ed.WriteMessage($"ActiveNeuralParams: {updatedIndex.HasActiveNeuralParameters}\n");
    }

    private static void Eval(Editor ed)
    {
        DbTextRepairModelStore.ForceMerge();
        var index = DbTextRepairModelStore.LoadIndex(out _);
        ed.WriteMessage("\n=== AFR DBText Neural Eval ===\n");
        ed.WriteMessage($"Model: {DbTextRepairModelStore.CanonicalPath}\n");
        ed.WriteMessage($"TrainingDataHash: {index.TrainingDataHash}\n");

        string error = "no-active-parameters";
        if (!index.TryGetActiveNeuralParameters(out DbTextRepairModelRecord parameters)
            || !DbTextNeuralRanker.TryCreate(parameters, out DbTextNeuralRanker? ranker, out error)
            || ranker == null)
        {
            ed.WriteMessage($"NeuralParams: unavailable ({error})\n");
            return;
        }

        ed.WriteMessage($"ValidationSummary: {parameters.ValidationSummaryJson}\n");
        ed.WriteMessage($"CurrentEval: {DbTextNeuralTrainer.Evaluate(index.Labels, ranker)}\n");
    }

    private static string BuildSourceSetId()
    {
        string machine = Environment.MachineName ?? "MACHINE";
        string user = Environment.UserName ?? "USER";
        return "nn-" + DbTextRepairModelJsonl.ComputeTextHash(machine + "\u001F" + user).Substring(0, 10);
    }
}
