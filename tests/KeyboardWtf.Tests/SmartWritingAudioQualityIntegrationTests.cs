namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;
using Xunit.Abstractions;

public sealed class SmartWritingAudioQualityIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public SmartWritingAudioQualityIntegrationTests(ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public void InstalledVoskModelCanTranscribeFixedSmartWritingWav()
    {
        var wavPath = Environment.GetEnvironmentVariable("KEYBOARD_WTF_SMART_WRITING_WAV");
        if (string.IsNullOrWhiteSpace(wavPath))
            return;

        Assert.True(File.Exists(wavPath), $"WAV fixture does not exist: {wavPath}");
        Assert.True(Directory.Exists(ModelManager.VoskModelPath), "The installed Vosk model is unavailable.");

        using var vosk = new VoskRecognitionService();
        vosk.LoadModel(ModelManager.VoskModelPath);
        var transcript = vosk.RecognizeFromWav(File.ReadAllBytes(wavPath));

        _output.WriteLine(transcript);
        Assert.False(string.IsNullOrWhiteSpace(transcript));
    }
}
