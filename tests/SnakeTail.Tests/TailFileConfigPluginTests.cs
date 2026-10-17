using System.Xml.Serialization;
using Xunit;

namespace SnakeTail.Tests;

public class TailFileConfigPluginTests
{
    [Fact]
    public void EnabledDisplayPlugins_xml_roundtrip_should_keep_order()
    {
        // Verify the plugin enable order is correctly saved to and restored from XML
        var config = new TailFileConfig
        {
            EnabledDisplayPlugins = new List<string> { "DragonMaid", "Example Plugin B" }
        };

        var serializer = new XmlSerializer(typeof(TailFileConfig));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        string xml = writer.ToString();

        using var reader = new StringReader(xml);
        var restored = (TailFileConfig)serializer.Deserialize(reader)!;

        Assert.NotNull(restored.EnabledDisplayPlugins);
        Assert.Equal(2, restored.EnabledDisplayPlugins.Count);
        Assert.Equal("DragonMaid", restored.EnabledDisplayPlugins[0]);
        Assert.Equal("Example Plugin B", restored.EnabledDisplayPlugins[1]);
    }
}
