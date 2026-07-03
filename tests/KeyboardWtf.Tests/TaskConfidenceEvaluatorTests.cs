namespace KeyboardWtf.Tests;

using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

public sealed class TaskConfidenceEvaluatorTests
{
    [Fact]
    public void LowConfidenceExternalStateAsksForClarification()
    {
        var result = TaskConfidenceEvaluator.Evaluate(
            new TaskConfidenceEvidence
            {
                CandidateScores = [0.61, 0.59],
                ChangesExternalState = true,
                AmbiguousField = true,
            },
            new AppSettings
            {
                AskForClarificationWhenLowConfidence = true,
                TaskClarificationMode = TaskClarificationMode.SmartClarify,
            });

        Assert.NotEqual(TaskConfidence.High, result.Confidence);
        Assert.True(result.ShouldClarify);
    }

    [Fact]
    public void HighConfidenceSafeActionContinues()
    {
        var result = TaskConfidenceEvaluator.Evaluate(
            new TaskConfidenceEvidence
            {
                CandidateScores = [0.98, 0.55],
                TargetExplicitlyNamed = true,
                FoundInSelectedContext = true,
            },
            new AppSettings());

        Assert.Equal(TaskConfidence.High, result.Confidence);
        Assert.False(result.ShouldClarify);
    }

    [Fact]
    public void UnexpectedPageChangeForcesClarification()
    {
        var result = TaskConfidenceEvaluator.Evaluate(
            new TaskConfidenceEvidence
            {
                CandidateScores = [0.95],
                TargetExplicitlyNamed = true,
                PageChangedUnexpectedly = true,
                ChangesExternalState = true,
            },
            new AppSettings(),
            actionIsRiskyOrImportant: true);

        Assert.True(result.ShouldClarify);
    }
}
