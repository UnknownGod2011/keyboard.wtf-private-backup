using System.Buffers.Binary;
using System.Text;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class SmartWritingAudioTests
{
    [Theory]
    [InlineData(10, 400)]
    [InlineData(30, 1200)]
    [InlineData(300, 12000)]
    public void Valid_pcm_wav_is_accepted_and_conservatively_estimated(
        int seconds,
        long expectedTokens)
    {
        var audio = new InlineAudioRequest("audio/wav", Convert.ToBase64String(CreateWav(seconds)));

        var validated = AiUsageService.ValidateAudio(audio);

        Assert.InRange(validated.Seconds, seconds - 0.01, seconds + 0.01);
        Assert.Equal(expectedTokens, validated.EstimatedTokens);
    }

    [Fact]
    public void Unsupported_or_malformed_audio_fails_before_provider_dispatch()
    {
        Assert.Throws<ArgumentException>(() => AiUsageService.ValidateAudio(
            new InlineAudioRequest("audio/mpeg", Convert.ToBase64String(CreateWav(1)))));
        Assert.Throws<ArgumentException>(() => AiUsageService.ValidateAudio(
            new InlineAudioRequest("audio/wav", "not-base64")));
    }

    [Fact]
    public void Audio_over_five_minutes_is_rejected()
    {
        var audio = new InlineAudioRequest("audio/wav", Convert.ToBase64String(CreateWav(301)));

        Assert.Throws<ArgumentException>(() => AiUsageService.ValidateAudio(audio));
    }

    private static byte[] CreateWav(int seconds)
    {
        var dataLength = checked(seconds * 16_000 * 2);
        var bytes = new byte[44 + dataLength];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)(bytes.Length - 8)));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 16_000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 32_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), checked((uint)dataLength));
        return bytes;
    }
}
