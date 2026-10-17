using Xunit;

namespace SnakeTail.Tests;

public class DisplayTextProcessorTests
{
    [Fact]
    public void Processor_should_stop_when_previous_plugin_handled()
    {
        // Verify later plugins don't run when an earlier plugin returns Handled=true
        var firstPlugin = new CountingPlugin("P1", canProcess: true, handled: true, output: "first");
        var secondPlugin = new CountingPlugin("P2", canProcess: true, handled: true, output: "second");
        var processor = new DisplayTextProcessor();
        processor.SetEnabledPlugins(
        [
            new LoadedDisplayPlugin("A", "A", "P1", firstPlugin, true, string.Empty),
            new LoadedDisplayPlugin("B", "B", "P2", secondPlugin, true, string.Empty)
        ]);

        string output = processor.GetProcessedLineText(10, "raw");

        Assert.Equal("first", output);
        Assert.Equal(1, firstPlugin.TryProcessCallCount);
        Assert.Equal(0, secondPlugin.TryProcessCallCount);
    }

    [Fact]
    public void Processor_should_hit_cache_with_same_signature()
    {
        // Verify the cache is hit for the same line and the same plugin signature
        var plugin = new CountingPlugin("P1", canProcess: true, handled: true, output: "cached");
        var processor = new DisplayTextProcessor();
        processor.SetEnabledPlugins([new LoadedDisplayPlugin("A", "A", "P1", plugin, true, string.Empty)]);

        string output1 = processor.GetProcessedLineText(20, "raw");
        string output2 = processor.GetProcessedLineText(20, "raw");

        Assert.Equal("cached", output1);
        Assert.Equal("cached", output2);
        Assert.Equal(1, plugin.TryProcessCallCount);

        // After the plugin order signature changes, the cache should be invalidated and recomputed
        processor.SetEnabledPlugins([]);
        processor.SetEnabledPlugins([new LoadedDisplayPlugin("A", "A", "P1", plugin, true, string.Empty)]);
        _ = processor.GetProcessedLineText(20, "raw");
        Assert.Equal(2, plugin.TryProcessCallCount);
    }

    [Fact]
    public void Processor_should_prefer_block_plugin_input_when_plugin_ordered_first()
    {
        // Verify that when a block plugin comes first, it collects the whole block first and blocks later single-line plugins.
        var blockPlugin = new BlockCollectPlugin();
        var linePlugin = new CountingPlugin("P2", canProcess: true, handled: true, output: "line");
        var processor = new DisplayTextProcessor();
        processor.SetEnabledPlugins(
        [
            new LoadedDisplayPlugin("A", "A", "BlockFirst", blockPlugin, true, string.Empty),
            new LoadedDisplayPlugin("B", "B", "LineSecond", linePlugin, true, string.Empty)
        ]);

        var lines = new Dictionary<int, string>
        {
            [100] = "2026-04-05 14:50:15.208\txxxx",
            [101] = "xxxx",
            [102] = "2026-04-05 15:50:15.208\tyyy"
        };

        string output = processor.GetProcessedLineText(
            100,
            lines[100],
            lineKey => lines.TryGetValue(lineKey, out string? text) ? text : string.Empty);

        Assert.Equal("BLOCK_OK", output);
        Assert.Equal(1, blockPlugin.TryProcessCallCount);
        Assert.Equal(0, linePlugin.TryProcessCallCount);
    }

    [Fact]
    public void Processor_should_keep_multiline_block_output_line_by_line()
    {
        // Verify that when a block plugin outputs multiple lines, the results are cached one-to-one by raw line number.
        var blockPlugin = new TwoLineRewriteBlockPlugin();
        var processor = new DisplayTextProcessor();
        processor.SetEnabledPlugins(
        [
            new LoadedDisplayPlugin("A", "A", "TwoLineRewrite", blockPlugin, true, string.Empty)
        ]);

        var lines = new Dictionary<int, string>
        {
            [500] = "\"skills\": [",
            [501] = "  10013001,",
            [502] = "]"
        };

        string line500 = processor.GetProcessedLineText(
            500,
            lines[500],
            lineKey => lines.TryGetValue(lineKey, out string? text) ? text : string.Empty);
        string line501 = processor.GetProcessedLineText(
            501,
            lines[501],
            lineKey => lines.TryGetValue(lineKey, out string? text) ? text : string.Empty);
        string line502 = processor.GetProcessedLineText(
            502,
            lines[502],
            lineKey => lines.TryGetValue(lineKey, out string? text) ? text : string.Empty);

        Assert.Equal("\"skills\": [", line500);
        Assert.Equal("  10013001 Dragon Breath,", line501);
        Assert.Equal("]", line502);
        Assert.Equal(1, blockPlugin.TryProcessCallCount);
    }

    private sealed class CountingPlugin : ILogDisplayPlugin
    {
        private readonly bool _canProcess;
        private readonly bool _handled;
        private readonly string _output;

        public CountingPlugin(string name, bool canProcess, bool handled, string output)
        {
            Name = name;
            _canProcess = canProcess;
            _handled = handled;
            _output = output;
        }

        public string Name { get; }
        public int TryProcessCallCount { get; private set; }

        public void Initialize(PluginContext context)
        {
            // Test plugin needs no initialization logic
        }

        public bool CanProcess(string line)
        {
            return _canProcess;
        }

        public PluginProcessResult TryProcess(string line)
        {
            TryProcessCallCount++;
            return new PluginProcessResult { Handled = _handled, Output = _output };
        }
    }

    private sealed class BlockCollectPlugin : ILogDisplayPlugin, ILogDisplayBlockPlugin
    {
        public string Name => "BlockCollect";
        public int TryProcessCallCount { get; private set; }

        public void Initialize(PluginContext context)
        {
            // Test plugin needs no initialization logic
        }

        public bool TryCollectBlock(int lineKey, string currentLine, Func<int, string> readLineByLineKey, out string blockText)
        {
            string secondLine = readLineByLineKey(lineKey + 1);
            blockText = string.Join(Environment.NewLine, currentLine, secondLine);
            return true;
        }

        public bool CanProcess(string line)
        {
            return line.Contains(Environment.NewLine);
        }

        public PluginProcessResult TryProcess(string line)
        {
            TryProcessCallCount++;
            return new PluginProcessResult { Handled = true, Output = "BLOCK_OK" };
        }
    }

    private sealed class TwoLineRewriteBlockPlugin : ILogDisplayPlugin, ILogDisplayBlockPlugin
    {
        public string Name => "TwoLineRewrite";
        public int TryProcessCallCount { get; private set; }

        public void Initialize(PluginContext context)
        {
            // Test plugin needs no initialization logic
        }

        public bool TryCollectBlock(int lineKey, string currentLine, Func<int, string> readLineByLineKey, out string blockText)
        {
            if (lineKey != 500)
            {
                blockText = string.Empty;
                return false;
            }

            string secondLine = readLineByLineKey(lineKey + 1);
            string thirdLine = readLineByLineKey(lineKey + 2);
            blockText = string.Join(Environment.NewLine, currentLine, secondLine, thirdLine);
            return true;
        }

        public bool CanProcess(string line)
        {
            return line.Contains(Environment.NewLine);
        }

        public PluginProcessResult TryProcess(string line)
        {
            TryProcessCallCount++;
            return new PluginProcessResult
            {
                Handled = true,
                Output = "\"skills\": [" + Environment.NewLine + "  10013001 Dragon Breath," + Environment.NewLine + "]"
            };
        }
    }
}
