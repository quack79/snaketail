using System.Text;
using Xunit;

namespace SnakeTail.Tests;

public class LogFileStreamSearchTests
{
    [Fact]
    public void SearchTextRange_should_return_minus_one_when_text_missing()
    {
        // Should return -1 when there is no match.
        string tempRoot = Path.Combine(Path.GetTempPath(), "snaketail-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string logPath = Path.Combine(tempRoot, "sample.log");
            File.WriteAllLines(logPath, ["alpha", "beta", "gamma"], new UTF8Encoding(false));

            using var stream = new LogFileStream(tempRoot, "sample.log", new UTF8Encoding(false), 10, false);
            int matchedLineNumber = stream.SearchTextRange(1, 4, "missing", matchCase: false, findLastMatch: false, progressCallback: null);

            Assert.Equal(-1, matchedLineNumber);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SearchTextRange_should_return_last_match_when_requested()
    {
        // Reverse search relies on returning the last matching line.
        string tempRoot = Path.Combine(Path.GetTempPath(), "snaketail-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string logPath = Path.Combine(tempRoot, "sample.log");
            File.WriteAllLines(logPath, ["foo", "bar", "foo"], new UTF8Encoding(false));

            using var stream = new LogFileStream(tempRoot, "sample.log", new UTF8Encoding(false), 10, false);
            int matchedLineNumber = stream.SearchTextRange(1, 4, "foo", matchCase: true, findLastMatch: true, progressCallback: null);

            Assert.Equal(3, matchedLineNumber);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
