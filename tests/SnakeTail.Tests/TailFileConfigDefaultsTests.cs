using System.Xml.Serialization;
using Xunit;

namespace SnakeTail.Tests;

public class TailFileConfigDefaultsTests
{
    [Fact]
    public void TailFileConfig_ctor_should_set_safe_polling_defaults()
    {
        // New configs should use safe default polling, so the UI timer doesn't spin at 100ms.
        var config = new TailFileConfig();

        Assert.Equal(TailFileConfig.DefaultFileCacheSize, config.FileCacheSize);
        Assert.Equal(TailFileConfig.DefaultFileCheckIntervalSeconds, config.FileCheckInterval);
        Assert.Equal(TailFileConfig.DefaultFileChangeCheckIntervalMs, config.FileChangeCheckInterval);
    }

    [Fact]
    public void TailFileConfig_xml_without_interval_fields_should_keep_defaults()
    {
        // When an old config lacks the polling fields, the constructor defaults should be kept.
        const string xml = "<TailFileConfig />";
        var serializer = new XmlSerializer(typeof(TailFileConfig));
        using var reader = new StringReader(xml);
        var restored = (TailFileConfig)serializer.Deserialize(reader)!;

        Assert.Equal(TailFileConfig.DefaultFileCheckIntervalSeconds, restored.FileCheckInterval);
        Assert.Equal(TailFileConfig.DefaultFileChangeCheckIntervalMs, restored.FileChangeCheckInterval);
    }
}
