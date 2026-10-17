using System;
using System.Collections.Generic;
using System.Linq;

namespace SnakeTail
{
    /// <summary>
    /// Display text processor: runs plugins serially in enabled order and caches the results.
    /// </summary>
    internal sealed class DisplayTextProcessor
    {
        private readonly Dictionary<string, string> _lineCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<LoadedDisplayPlugin> _enabledPlugins = new List<LoadedDisplayPlugin>();

        public IReadOnlyList<LoadedDisplayPlugin> EnabledPlugins => _enabledPlugins;

        /// <summary>
        /// Updates the enabled plugin order.
        /// </summary>
        public void SetEnabledPlugins(IEnumerable<LoadedDisplayPlugin> plugins)
        {
            _enabledPlugins.Clear();
            if (plugins != null)
                _enabledPlugins.AddRange(plugins.Where(p => p != null));
            ClearCache();
        }

        /// <summary>
        /// Clears the processing cache.
        /// </summary>
        public void ClearCache()
        {
            _lineCache.Clear();
        }

        /// <summary>
        /// Gets the processed text; returns the original text when there are no plugins or none match.
        /// </summary>
        public string GetProcessedLineText(int lineKey, string rawLineText, Func<int, string> readLineByLineKey = null)
        {
            if (rawLineText == null)
                rawLineText = string.Empty;

            if (_enabledPlugins.Count == 0)
                return rawLineText;

            string signature = GetEnabledSignature();
            string cacheKey = string.Format("{0}|{1}", lineKey, signature);
            if (_lineCache.TryGetValue(cacheKey, out string cachedText))
                return cachedText;

            string currentText = rawLineText;
            foreach (LoadedDisplayPlugin loadedPlugin in _enabledPlugins)
            {
                ILogDisplayPlugin plugin = loadedPlugin.Instance;
                if (plugin == null)
                    continue;

                // Block plugins try to collect the whole block first, so the input granularity is decided by plugin order.
                string textForPlugin = currentText;
                if (plugin is ILogDisplayBlockPlugin blockPlugin && readLineByLineKey != null)
                {
                    try
                    {
                        if (blockPlugin.TryCollectBlock(lineKey, rawLineText, readLineByLineKey, out string blockText)
                            && !string.IsNullOrEmpty(blockText))
                        {
                            textForPlugin = blockText;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.AppendDaily(AppLog.LevelErr, string.Format("Plugin TryCollectBlock failed: Plugin={0}, LineKey={1}, Error={2}: {3}", loadedPlugin.DisplayName, lineKey, ex.GetType().FullName, ex.Message));
                    }
                }

                bool canProcess;
                try
                {
                    canProcess = plugin.CanProcess(textForPlugin);
                }
                catch (Exception ex)
                {
                    AppLog.AppendDaily(AppLog.LevelErr, string.Format("Plugin CanProcess failed: Plugin={0}, LineKey={1}, Error={2}: {3}", loadedPlugin.DisplayName, lineKey, ex.GetType().FullName, ex.Message));
                    continue;
                }

                if (!canProcess)
                    continue;

                PluginProcessResult result;
                try
                {
                    result = plugin.TryProcess(textForPlugin);
                }
                catch (Exception ex)
                {
                    AppLog.AppendDaily(AppLog.LevelErr, string.Format("Plugin TryProcess failed: Plugin={0}, LineKey={1}, Error={2}: {3}", loadedPlugin.DisplayName, lineKey, ex.GetType().FullName, ex.Message));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    AppLog.AppendDaily(AppLog.LevelErr, string.Format("Plugin returned an error: Plugin={0}, LineKey={1}, Error={2}", loadedPlugin.DisplayName, lineKey, result.ErrorMessage));
                }

                if (result.Handled)
                {
                    // When a block plugin returns multiple lines, write the output back to the cache line by line by the raw line count, keeping a one-to-one display.
                    if (TryCacheBlockOutputByLine(lineKey, signature, textForPlugin, result.Output, out string currentLineOutput))
                    {
                        _lineCache[cacheKey] = currentLineOutput;
                        return currentLineOutput;
                    }

                    if (result.Output != null)
                        currentText = result.Output;
                    break;
                }

                if (!string.IsNullOrEmpty(result.Output))
                    currentText = result.Output;
            }

            _lineCache[cacheKey] = currentText;
            return currentText;
        }

        /// <summary>
        /// When the block input and output have the same line count, writes them to the cache by line number and returns the current line's output.
        /// </summary>
        private bool TryCacheBlockOutputByLine(int startLineKey, string signature, string blockInput, string blockOutput, out string currentLineOutput)
        {
            currentLineOutput = string.Empty;
            if (string.IsNullOrEmpty(blockInput) || string.IsNullOrEmpty(blockOutput))
                return false;

            if (!blockInput.Contains(Environment.NewLine) || !blockOutput.Contains(Environment.NewLine))
                return false;

            string[] inputLines = blockInput.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            string[] outputLines = blockOutput.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            if (inputLines.Length != outputLines.Length)
                return false;

            for (int i = 0; i < outputLines.Length; i++)
            {
                int lineKey = startLineKey + i;
                string lineCacheKey = string.Format("{0}|{1}", lineKey, signature);
                _lineCache[lineCacheKey] = outputLines[i];
            }

            currentLineOutput = outputLines[0];
            return true;
        }

        /// <summary>
        /// Returns the signature of the current enabled plugin order.
        /// </summary>
        public string GetEnabledSignature()
        {
            if (_enabledPlugins.Count == 0)
                return string.Empty;
            return string.Join(">", _enabledPlugins.Select(x => x.DisplayName));
        }
    }
}
