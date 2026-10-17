using Xunit;

namespace SnakeTail.Tests;

public class LogFileStreamWatcherMatchTests
{
    [Fact]
    public void IsWatchEventMatch_exact_file_should_match_ignore_case()
    {
        // Exact file mode: case differences must not affect matching.
        bool match = LogFileStream.IsWatchEventMatch(
            configuredPathAbsolute: @"C:\logs\app.log",
            openedFilePath: @"C:\logs\app.log",
            eventPath: @"c:\LOGS\APP.LOG",
            fileCheckPattern: false);

        Assert.True(match);
    }

    [Fact]
    public void IsWatchEventMatch_exact_file_should_not_match_other_file()
    {
        // Exact file mode: different files must not match, to avoid spurious reloads.
        bool match = LogFileStream.IsWatchEventMatch(
            configuredPathAbsolute: @"C:\logs\app.log",
            openedFilePath: @"C:\logs\app.log",
            eventPath: @"C:\logs\other.log",
            fileCheckPattern: false);

        Assert.False(match);
    }

    [Fact]
    public void IsWatchEventMatch_pattern_mode_should_accept_any_event_path()
    {
        // Wildcard mode: path filtering is done by the watcher's filter, so everything passes here.
        bool match = LogFileStream.IsWatchEventMatch(
            configuredPathAbsolute: @"C:\logs\*.log",
            openedFilePath: @"C:\logs\current.log",
            eventPath: @"C:\logs\next.log",
            fileCheckPattern: true);

        Assert.True(match);
    }
}
