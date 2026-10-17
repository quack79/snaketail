using System;

namespace SnakeTail
{
    /// <summary>
    /// Log display plugin interface: only processes text in the display pipeline; never modifies the raw log file.
    /// </summary>
    public interface ILogDisplayPlugin
    {
        /// <summary>
        /// Plugin display name; when empty, the host falls back to the plugin directory name.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Initializes the plugin context.
        /// </summary>
        void Initialize(PluginContext context);

        /// <summary>
        /// Determines whether the current line needs processing.
        /// </summary>
        bool CanProcess(string line);

        /// <summary>
        /// Tries to process the current line; Handled=true means the plugin takes over and later plugins are skipped.
        /// </summary>
        PluginProcessResult TryProcess(string line);
    }

    /// <summary>
    /// Optional block extraction interface: expands the current line into a "whole text block" before plugin processing.
    /// </summary>
    public interface ILogDisplayBlockPlugin
    {
        /// <summary>
        /// Tries to collect a complete block starting at the current line; when it returns true the host passes the block text to the plugin.
        /// </summary>
        bool TryCollectBlock(int lineKey, string currentLine, Func<int, string> readLineByLineKey, out string blockText);
    }

    /// <summary>
    /// Plugin context: provides read-only directories and host information.
    /// </summary>
    public sealed class PluginContext
    {
        public PluginContext(string pluginDirectoryAbsolutePath, string configRootDirectoryAbsolutePath, string currentLogFilePath, string hostVersion)
        {
            PluginDirectoryAbsolutePath = pluginDirectoryAbsolutePath ?? string.Empty;
            ConfigRootDirectoryAbsolutePath = configRootDirectoryAbsolutePath ?? string.Empty;
            CurrentLogFilePath = currentLogFilePath ?? string.Empty;
            HostVersion = hostVersion ?? string.Empty;
        }

        public string PluginDirectoryAbsolutePath { get; }
        public string ConfigRootDirectoryAbsolutePath { get; }
        public string CurrentLogFilePath { get; }
        public string HostVersion { get; }
    }

    /// <summary>
    /// Plugin processing result.
    /// </summary>
    public struct PluginProcessResult
    {
        public bool Handled { get; set; }
        public string Output { get; set; }
        public string ErrorMessage { get; set; }
    }
}
