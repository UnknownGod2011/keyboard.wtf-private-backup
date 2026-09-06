using System.Reflection;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class MicrophonePeakTests
{
    [Fact]
    public void Every_valid_pcm16_sample_has_a_finite_peak_without_dropping_audio()
    {
        var meter = typeof(GeminiLiveConversationService).GetMethod("PeakDb", BindingFlags.Static | BindingFlags.NonPublic);
        for (var value = (int)short.MinValue; value <= short.MaxValue; value++)
        {
            var bytes = BitConverter.GetBytes((short)value);
            var peak = (double)meter.Invoke(null, new object[] { bytes });
            var expected = value == 0 ? -96 : 20 * Math.Log10(Math.Abs(value) / 32768.0);
            Assert.Equal(expected, peak, 10);
        }
    }
}
