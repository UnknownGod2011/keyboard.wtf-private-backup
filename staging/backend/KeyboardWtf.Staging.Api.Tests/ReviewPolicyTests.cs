using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ReviewPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Rating_outside_one_to_five_is_rejected(int rating) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReviewPolicy.ValidateRating(rating));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Rating_inside_one_to_five_is_accepted(int rating) =>
        ReviewPolicy.ValidateRating(rating);

    [Fact]
    public void Review_text_is_normalized_and_control_characters_removed()
    {
        var normalized = ReviewPolicy.NormalizeText("  Great\t\tapp!\u0000\r\n\r\n\r\nStill fast.  ");

        Assert.Equal("Great app!\n\nStill fast.", normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    public void Review_text_outside_bounds_is_rejected(string text) =>
        Assert.Throws<ArgumentException>(() => ReviewPolicy.NormalizeText(text));

    [Fact]
    public void Public_display_name_is_bounded_and_never_falls_back_to_email()
    {
        Assert.Equal("keyboard.wtf user", ReviewPolicy.NormalizeDisplayName("\u0000\t"));
        Assert.Equal("keyboard.wtf user", ReviewPolicy.NormalizeDisplayName("person@example.invalid"));
        Assert.Equal(60, ReviewPolicy.NormalizeDisplayName(new string('a', 80)).Length);
    }
}
