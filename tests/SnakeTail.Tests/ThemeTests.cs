using System.Drawing;
using System.Windows.Forms;
using SnakeTail.Storage;
using Xunit;

namespace SnakeTail.Tests;

public class ThemeTests
{
    [Theory]
    [InlineData("Light", ThemeMode.Light)]
    [InlineData("Dark", ThemeMode.Dark)]
    [InlineData("System", ThemeMode.System)]
    [InlineData("dark", ThemeMode.Dark)]
    [InlineData(" System ", ThemeMode.System)]
    public void ParseMode_should_read_saved_values(string value, ThemeMode expected)
    {
        Assert.Equal(expected, Theme.ParseMode(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Purple")]
    [InlineData("7")]
    public void ParseMode_should_default_to_light_for_missing_or_invalid_values(string? value)
    {
        Assert.Equal(ThemeMode.Light, Theme.ParseMode(value!));
    }

    [Theory]
    [InlineData(ThemeMode.Light, true, false)]
    [InlineData(ThemeMode.Light, false, false)]
    [InlineData(ThemeMode.Dark, true, true)]
    [InlineData(ThemeMode.Dark, false, true)]
    [InlineData(ThemeMode.System, true, false)]
    [InlineData(ThemeMode.System, false, true)]
    public void ResolveIsDark_should_follow_mode_and_system_setting(ThemeMode mode, bool systemUsesLightTheme, bool expected)
    {
        Assert.Equal(expected, Theme.ResolveIsDark(mode, systemUsesLightTheme));
    }

    [Fact]
    public void MapColor_should_map_system_colors_only_in_dark_mode()
    {
        Assert.Equal(Theme.DarkWindow, Theme.MapColor(SystemColors.Window, true));
        Assert.Equal(Theme.DarkWindowText, Theme.MapColor(SystemColors.WindowText, true));
        Assert.Equal(Theme.DarkControl, Theme.MapColor(SystemColors.Control, true));
        Assert.Equal(SystemColors.Window, Theme.MapColor(SystemColors.Window, false));
        // Highlight keeps the system selection color in both themes
        Assert.Equal(SystemColors.Highlight, Theme.MapColor(SystemColors.Highlight, true));
    }

    [Fact]
    public void MapColor_should_keep_explicit_colors()
    {
        // User-chosen colors are never system colors, so they are kept in the dark theme
        Color userColor = Color.FromArgb(255, 0, 0, 0);
        Assert.Equal(userColor, Theme.MapColor(userColor, true));
        Assert.Equal(Color.Yellow, Theme.MapColor(Color.Yellow, true));
    }

    [Fact]
    public void Default_list_colors_should_round_trip_through_config_as_system_colors()
    {
        // Untouched configs store "window"/"windowtext", which must stay recognizable as theme defaults
        var config = new TailFileConfig();
        config.FormBackColor = SystemColors.Window;
        config.FormTextColor = SystemColors.WindowText;

        Assert.True(config.FormBackColor!.Value.IsSystemColor);
        Assert.True(config.FormTextColor!.Value.IsSystemColor);

        config.FormBackColor = Color.FromArgb(0, 0, 0);
        Assert.False(config.FormBackColor!.Value.IsSystemColor);
    }

    [Fact]
    public void UnthemedColors_should_return_original_system_colors_for_control_without_theme_state()
    {
        using var listView = new ListView();
        listView.BackColor = Color.FromArgb(10, 20, 30);

        Assert.Equal(Color.FromArgb(10, 20, 30), Theme.UnthemedBackColor(listView));
    }

    [Fact]
    public void Storage_should_persist_theme_mode()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), "SnakeTailThemeTest_" + Guid.NewGuid().ToString("N"), "xSnakeTail.db");
        try
        {
            using (var storage = new SnakeTailStorage(dbPath))
            {
                Assert.True(storage.IsAvailable);
                Assert.Equal(ThemeMode.Light, storage.LoadThemeMode());
                storage.SaveThemeMode(ThemeMode.System);
            }

            using (var storage = new SnakeTailStorage(dbPath))
            {
                Assert.Equal(ThemeMode.System, storage.LoadThemeMode());
                storage.SaveThemeMode(ThemeMode.Dark);
                Assert.Equal(ThemeMode.Dark, storage.LoadThemeMode());
            }
        }
        finally
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }
}
