namespace KeyboardWtf.Services;

using KeyboardWtf.Models;

public sealed class TaskConfidenceEvidence
{
    public IReadOnlyList<double> CandidateScores { get; init; } = Array.Empty<double>();
    public bool TargetExplicitlyNamed { get; init; }
    public bool FoundInSelectedContext { get; init; }
    public bool ChangesExternalState { get; init; }
    public bool PageChangedUnexpectedly { get; init; }
    public bool SupportedByMemory { get; init; }
    public bool AmbiguousField { get; init; }
}

public sealed class TaskConfidenceResult
{
    public TaskConfidence Confidence { get; init; }
    public double Score { get; init; }
    public double CandidateGap { get; init; }
    public bool ShouldClarify { get; init; }
    public string Reason { get; init; } = "";
}

public static class TaskConfidenceEvaluator
{
    public static TaskConfidenceResult Evaluate(
        TaskConfidenceEvidence evidence,
        AppSettings settings,
        bool actionIsRiskyOrImportant = false)
    {
        evidence ??= new TaskConfidenceEvidence();
        settings ??= new AppSettings();
        var scores = evidence.CandidateScores
            .Select(score => Math.Clamp(score, 0, 1))
            .OrderByDescending(score => score)
            .ToArray();
        var top = scores.Length > 0 ? scores[0] : 0;
        var gap = scores.Length > 1 ? top - scores[1] : top;

        var score = scores.Length == 0 ? 0.45 : 0.25 + (top * 0.55) + Math.Min(0.15, gap);
        if (evidence.TargetExplicitlyNamed)
            score += 0.12;
        if (evidence.FoundInSelectedContext)
            score += 0.12;
        if (evidence.SupportedByMemory)
            score += 0.08;
        if (evidence.AmbiguousField)
            score -= 0.24;
        if (evidence.PageChangedUnexpectedly)
            score -= 0.35;
        score = Math.Clamp(score, 0, 1);

        var confidence = scores.Length == 0 && !evidence.TargetExplicitlyNamed
            ? TaskConfidence.Unknown
            : score >= 0.82 && (scores.Length < 2 || gap >= 0.08)
                ? TaskConfidence.High
                : score >= 0.62
                    ? TaskConfidence.Medium
                    : TaskConfidence.Low;

        var shouldClarify = settings.TaskClarificationMode switch
        {
            TaskClarificationMode.AlwaysAskAmbiguous =>
                confidence != TaskConfidence.High || evidence.AmbiguousField,
            TaskClarificationMode.LetAiDecide =>
                confidence == TaskConfidence.Low
                || (confidence == TaskConfidence.Unknown && evidence.ChangesExternalState)
                || (confidence == TaskConfidence.Medium && actionIsRiskyOrImportant),
            _ =>
                confidence == TaskConfidence.Low
                || (confidence == TaskConfidence.Unknown && evidence.ChangesExternalState)
                || (confidence == TaskConfidence.Medium && actionIsRiskyOrImportant),
        };
        if (!settings.AskForClarificationWhenLowConfidence
            && !actionIsRiskyOrImportant
            && confidence is TaskConfidence.Low or TaskConfidence.Unknown)
        {
            shouldClarify = false;
        }

        var reason = evidence.PageChangedUnexpectedly
            ? "The page changed unexpectedly."
            : evidence.AmbiguousField
                ? "Multiple targets or meanings are plausible."
                : confidence switch
                {
                    TaskConfidence.High => "The target is explicit and clearly separated from alternatives.",
                    TaskConfidence.Medium => "The best target is plausible but not decisive.",
                    TaskConfidence.Low => "The available evidence is weak or conflicting.",
                    _ => "There is not enough evidence to choose safely.",
                };
        return new TaskConfidenceResult
        {
            Confidence = confidence,
            Score = score,
            CandidateGap = gap,
            ShouldClarify = shouldClarify,
            Reason = reason,
        };
    }
}
