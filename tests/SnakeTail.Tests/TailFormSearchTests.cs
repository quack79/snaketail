using Xunit;

namespace SnakeTail.Tests;

public class TailFormSearchTests
{
    [Fact]
    public void Text_search_should_only_use_raw_text()
    {
        // Plain text search only matches the raw log content.
        bool useProcessedText = TailForm.ShouldSearchProcessedText(lineHighlights: false);

        Assert.False(useProcessedText);
    }

    [Fact]
    public void Highlight_search_should_still_use_processed_text()
    {
        // Highlight search keeps the display pipeline semantics.
        bool useProcessedText = TailForm.ShouldSearchProcessedText(lineHighlights: true);

        Assert.True(useProcessedText);
    }
}
