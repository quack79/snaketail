#region License statement
/* SnakeTail is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3 of the License.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */
#endregion

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using System.Linq;

namespace SnakeTail
{
    partial class TailForm : Form, ITailForm
    {
        // Todo Implement inline keyword highlight when owner-drawn
        // Todo Filter Text File
        // Todo Consider keeping file handle open, even when file is renamed / deleted (show history)
        // Todo Jump Lists
        // Todo Use background workerthread when searching (First visual cache, new file-stream, lock scrolling and timers, extra step to check for new lines, allow cancel search)
        // Todo Cache search results when searching up
        // Todo Scroll to the same matching line in all views
        // Todo Use background workerthread when checking for new file (avoid network timeout)
        //  - It needs to be a seperate thread that both does the reading and file checking
        //  - The ListViewItem cache should reside in the GUI context
        //  - Make active thread object, which the GUI context can contact
        //      - Read next line (Polls the next line if one exist)
        //      - Total number of lines (Attempt to make guess ?)
        //  - Active thread keeps internal cache of 100 lines
        //      - Only when lines has been read, then it continues
        //  - There is also a LogStreamObject object in the GUI context for searching and reading backwards
        //      - We close / reopen the second file handle for each operation
        //          - This will slow down the operation when scrolling down
        //              - Another work around is to keep a version on the LogStreamObject, and increase every time it reopens
        //              - We could fix this if enabling dynamic file positioning
        //              - Maybe just implement a solution where it is slow first ?
        // Todo Implement log stream that can read backwards
        //  - Can start at the bottom and display at once without loading entire file
        //  - Open file, read BOM (If default specified)
        //  - Seek to bottom - 64K
        //  - Start seeking for newlines using the encoding from the BOM
        //  - Guess the total number of lines based on the lines in the 64K
        //  - When scrolling how to lookup the line numbers requested
        //      - Perform file seek based on percentage
        //  - When using page-up to reach top then it will read backwards in 64K blocks
        //      - Should always expect the total number of lines as a guess
        //      - If too many lines are guessed when we reach file-start before top of list (adjust guess)
        //      - If too few lines are guessed before we reach file-start we are at top of lies (adjust guess)
        //      - Must support that scrolling have been performed so it is always an adjusted guess
        // Todo Implement log stream that skips to the end at beginning
        //  - Only when requested to search the file backwards beyond the cache, then it reads the file from the beginning
        //  - Has to make a good guess on the number of lines in the file
        //      - Based on the number of the lines in the last X bytes of the file
        //  - When requesting a line outside the cache, then it performs a proper line calculation
        //      - How do we merge the correct line-count with the one guessed when using arrow-up ?
        //          - File position is not a good friend when using file cache
        //              - We can make the file reader use chunks of 64K
        //          - Who should be responsible for conversion?
        //              - Extend cache class so it wraps the two file-streams
        //                  - Scroll to bottoom
        //                  - Scroll to top
        //                  - Search
        //                  - Check tail (and new file)
        //                  - Readline
        //                  - Line Count
        //                  - Prepare Cache
        //                  - All methods can modify the line count
        LogFileCache _logFileCache = null;
        LogFileStream _logFileStream = null;
        LogFileStream _logTailStream = null;
        TaskMonitor _taskMonitor = null;
        bool _topIndexHack = false;
        bool _pauseMode = false;
        bool _formTitleMatchFilename = false;
        string _formTitle = "";
        string _formIconFile = "";
        DateTime _lastFormTitleUpdate = DateTime.Now;
        bool _formMinimizedAtBottom = false;
        Icon _formCustomIcon = null;
        Icon _formMaximizedIcon = null;
        string _configPath = "";
        List<TailKeywordConfig> _keywordHighlight;
        int _loghitCounter = -1;
        bool _displayTabIcon = false;
        List<ExternalToolConfig> _externalTools;
        Color _bookmarkTextColor = Color.Yellow;    // Default bookmark text color
        Color _bookmarkBackColor = Color.Green;     // Default bookmark background color
        List<int> _bookmarks = new List<int>();
        ThreadPoolQueue _threadPoolQueue = null;

        // Keyword filter and highlight fields
        string _quickKeyword = "";
        bool _quickHighlight = false;
        Color _quickHighlightColor = Color.Yellow;
        bool _quickFilter = false;
        bool _quickInverse = false;
        Dictionary<int, int> _filteredIndexMap = new Dictionary<int, int>(); // Maps filtered virtual indexes to actual line numbers
        int _filteredVirtualListSize = 0;
        int _originalVirtualListSize = 0; // Saved original list size
        int _savedLineNumber = 0; // Line number saved before clearing
        // Display plugin fields
        DisplayPluginManager _displayPluginManager = null;
        DisplayTextProcessor _displayTextProcessor = new DisplayTextProcessor();
        List<LoadedDisplayPlugin> _availableDisplayPlugins = new List<LoadedDisplayPlugin>();

        // In-line text selection: double-click selects a word, double-click again selects the whole line; Ctrl+C copies the selected text
        string _inlineSelectionText = "";
        int _inlineSelectionItemIndex = -1;
        int _inlineSelectionStart = 0;
        int _inlineSelectionLength = 0;
        int _lastDoubleClickItemIndex = -1;
        int _lastDoubleClickWordStart = -1;
        int _lastDoubleClickWordLength = -1;

        private TailFileConfig _currentTailConfig = null;
        DateTime _lastTailTickUtc = DateTime.MinValue;
        int _tailTickRunning = 0;
        int _watcherUiRefreshQueued = 0;

        public TailForm()
        {
            InitializeComponent();
            Icon = Properties.Resources.LogIcon;
            InitializeEncodingComboBox();
            InitializeKeywordToolStrip();
            Theme.Apply(this);
        }

        private void InitializeKeywordToolStrip()
        {
            _highlightColorButton.BackColor = _quickHighlightColor;
            _highlightColorButton.ForeColor = GetContrastColor(_quickHighlightColor);
        }

        /// <summary>
        /// Initializes display plugins and restores the current file's enabled state.
        /// </summary>
        private void InitializeDisplayPlugins(TailFileConfig tailConfig)
        {
            _displayPluginManager = new DisplayPluginManager(_configPath);
            string currentFilePath = _logTailStream != null ? _logTailStream.FilePathAbsolute : string.Empty;
            _displayPluginManager.Reload(currentFilePath);
            _availableDisplayPlugins = _displayPluginManager.AvailablePlugins.ToList();

            List<LoadedDisplayPlugin> enabledPlugins = new List<LoadedDisplayPlugin>();
            List<string> enabledPluginNames = tailConfig.EnabledDisplayPlugins ?? new List<string>();
            foreach (string pluginName in enabledPluginNames)
            {
                LoadedDisplayPlugin plugin = _displayPluginManager.FindByDisplayName(pluginName);
                if (plugin != null && plugin.IsAvailable)
                {
                    enabledPlugins.Add(plugin);
                }
                else if (plugin != null)
                {
                    AppLog.AppendDaily(AppLog.LevelWarn, string.Format("Display plugin referenced by config is missing a config file and was not enabled: Plugin={0}, File={1}, Reason={2}", pluginName, tailConfig.FilePath, plugin.UnavailableReason));
                }
                else
                {
                    AppLog.AppendDaily(AppLog.LevelWarn, string.Format("Display plugin referenced by config not found, ignored: Plugin={0}, File={1}", pluginName, tailConfig.FilePath));
                }
            }

            _displayTextProcessor.SetEnabledPlugins(enabledPlugins);
            SyncEnabledPluginsToConfig();
            RebuildPluginMenu();
        }

        /// <summary>
        /// Rebuilds the plugin menu items and syncs their checked state.
        /// </summary>
        private void RebuildPluginMenu()
        {
            _pluginsToolStripMenuItem.DropDownItems.Clear();
            _pluginsToolStripMenuItem.Enabled = _availableDisplayPlugins.Count > 0;

            foreach (LoadedDisplayPlugin plugin in _availableDisplayPlugins)
            {
                string pluginMenuText = plugin.IsAvailable
                    ? plugin.DisplayName
                    : string.Format("{0} (missing config)", plugin.DisplayName);
                ToolStripMenuItem pluginMenuItem = new ToolStripMenuItem(pluginMenuText);
                pluginMenuItem.CheckOnClick = true;
                pluginMenuItem.Checked = _displayTextProcessor.EnabledPlugins.Any(x => string.Equals(x.DisplayName, plugin.DisplayName, StringComparison.CurrentCultureIgnoreCase));
                pluginMenuItem.Tag = plugin.DisplayName;
                pluginMenuItem.Click += pluginMenuItem_Click;
                _pluginsToolStripMenuItem.DropDownItems.Add(pluginMenuItem);
            }
        }

        /// <summary>
        /// Plugin menu click: enables in click order; unchecking removes it.
        /// </summary>
        private void pluginMenuItem_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem menuItem = sender as ToolStripMenuItem;
            if (menuItem == null || menuItem.Tag == null)
                return;

            string pluginName = menuItem.Tag.ToString();
            List<LoadedDisplayPlugin> enabledPlugins = _displayTextProcessor.EnabledPlugins.ToList();
            LoadedDisplayPlugin targetPlugin = _displayPluginManager != null ? _displayPluginManager.FindByDisplayName(pluginName) : null;
            if (targetPlugin == null)
                return;

            // When config is missing, show a precise message and undo this enable.
            if (menuItem.Checked && !targetPlugin.IsAvailable)
            {
                menuItem.Checked = false;
                string unavailableReason = string.IsNullOrWhiteSpace(targetPlugin.UnavailableReason)
                    ? "The plugin's required config file is missing."
                    : targetPlugin.UnavailableReason;
                MessageBox.Show(this,
                    string.Format("Plugin \"{0}\" cannot be enabled.\n\nReason: {1}", targetPlugin.DisplayName, unavailableReason),
                    "Plugin Enable Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (menuItem.Checked)
            {
                if (!enabledPlugins.Any(x => string.Equals(x.DisplayName, pluginName, StringComparison.CurrentCultureIgnoreCase)))
                    enabledPlugins.Add(targetPlugin);
            }
            else
            {
                enabledPlugins.RemoveAll(x => string.Equals(x.DisplayName, pluginName, StringComparison.CurrentCultureIgnoreCase));
            }

            _displayTextProcessor.SetEnabledPlugins(enabledPlugins);
            SyncEnabledPluginsToConfig();
            InvalidateProcessedLineCache();
            if (_quickFilter && !string.IsNullOrEmpty(_quickKeyword))
                ApplyKeywordFilter();
            _tailListView.Invalidate();
            _tailListView.Update();
            RebuildPluginMenu();
        }

        /// <summary>
        /// Writes the current enabled plugin order back to the config object.
        /// </summary>
        private void SyncEnabledPluginsToConfig()
        {
            if (_currentTailConfig == null)
                return;

            _currentTailConfig.EnabledDisplayPlugins = _displayTextProcessor.EnabledPlugins
                .Select(x => x.DisplayName)
                .ToList();
        }

        /// <summary>
        /// Clears the processed text cache.
        /// </summary>
        private void InvalidateProcessedLineCache()
        {
            _displayTextProcessor.ClearCache();
        }

        /// <summary>
        /// Maps a virtual index to the actual display index.
        /// </summary>
        private int ResolveDisplayIndex(int virtualIndex)
        {
            if (_quickFilter && !string.IsNullOrEmpty(_quickKeyword))
            {
                if (_filteredIndexMap.TryGetValue(virtualIndex, out int displayIndex))
                    return displayIndex;
            }
            return virtualIndex;
        }

        /// <summary>
        /// Gets the raw line text for the given display index.
        /// </summary>
        private string GetRawLineTextByDisplayIndex(int displayIndex)
        {
            if (_logFileCache == null || _logFileStream == null)
                return string.Empty;

            ListViewItem cachedItem = _logFileCache.LookupCache(displayIndex);
            if (cachedItem != null)
                return cachedItem.Text ?? string.Empty;

            if (_logFileCache.FillCache(_logFileStream, displayIndex) != -1)
            {
                cachedItem = _logFileCache.LookupCache(displayIndex);
                if (cachedItem != null)
                    return cachedItem.Text ?? string.Empty;
            }

            int fileLineNumber = _savedLineNumber + displayIndex + 1;
            string rawLine = _logFileStream.ReadLine(fileLineNumber);
            if (rawLine == null)
                rawLine = string.Empty;

            ListViewItem newItem = new ListViewItem(rawLine);
            newItem.SubItems.Add(string.Empty);
            _logFileCache.SetLastCacheMiss(displayIndex, newItem);
            return rawLine;
        }

        /// <summary>
        /// Gets the processed text for the given virtual index.
        /// </summary>
        private string GetProcessedLineTextByVirtualIndex(int virtualIndex)
        {
            int displayIndex = ResolveDisplayIndex(virtualIndex);
            return GetProcessedLineTextByDisplayIndex(displayIndex);
        }

        /// <summary>
        /// Gets the processed text for the given display index.
        /// </summary>
        private string GetProcessedLineTextByDisplayIndex(int displayIndex)
        {
            string rawLine = GetRawLineTextByDisplayIndex(displayIndex);
            int lineKey = _savedLineNumber + displayIndex + 1;
            return _displayTextProcessor.GetProcessedLineText(lineKey, rawLine, ReadRawLineTextByLineKey);
        }

        /// <summary>
        /// Plain text search only searches the raw text; highlight search uses the display-side text.
        /// </summary>
        private string GetSearchLineTextByVirtualIndex(int virtualIndex, bool lineHighlights)
        {
            if (ShouldSearchProcessedText(lineHighlights))
                return GetProcessedLineTextByVirtualIndex(virtualIndex);

            return GetRawLineTextByVirtualIndex(virtualIndex);
        }

        /// <summary>
        /// Whether the search needs to use processed text.
        /// </summary>
        internal static bool ShouldSearchProcessedText(bool lineHighlights)
        {
            // Highlight search depends on the display pipeline; plain text search only looks at the raw text.
            return lineHighlights;
        }

        /// <summary>
        /// For plain text search with no filter, allow scanning the raw text sequentially.
        /// </summary>
        private bool CanUseDirectRawSearch(bool lineHighlights)
        {
            return !ShouldSearchProcessedText(lineHighlights)
                && !_quickFilter
                && _logFileStream != null;
        }

        /// <summary>
        /// During long searches, periodically let the UI process messages so the window doesn't appear frozen.
        /// </summary>
        private static void PumpSearchUiIfNeeded(ref int lastPumpTick)
        {
            int currentTick = Environment.TickCount;
            if (unchecked(currentTick - lastPumpTick) < 50)
                return;

            Application.DoEvents();
            lastPumpTick = currentTick;
        }

        /// <summary>
        /// Refreshes search progress infrequently to reduce UI update overhead.
        /// </summary>
        private void UpdateSearchProgressIfNeeded(int currentIndex, int endIndex, ref int lastPumpTick)
        {
            int currentTick = Environment.TickCount;
            if (unchecked(currentTick - lastPumpTick) < 50)
                return;

            SetStatusBar("Searching...", currentIndex, endIndex);
            Application.DoEvents();
            lastPumpTick = currentTick;
        }

        /// <summary>
        /// Reads raw text by absolute line number, so block plugins can collect the whole block that follows.
        /// </summary>
        private string ReadRawLineTextByLineKey(int lineKey)
        {
            if (lineKey <= 0)
                return string.Empty;

            string line = _logTailStream != null ? _logTailStream.ReadLine(lineKey) : null;
            if (line == null && _logFileStream != null)
                line = _logFileStream.ReadLine(lineKey);
            return line ?? string.Empty;
        }

        /// <summary>
        /// Gets the raw text for a virtual index (used for Ctrl+C).
        /// </summary>
        private string GetRawLineTextByVirtualIndex(int virtualIndex)
        {
            int displayIndex = ResolveDisplayIndex(virtualIndex);
            return GetRawLineTextByDisplayIndex(displayIndex);
        }

        private Color GetContrastColor(Color color)
        {
            // Compute a contrasting color for text display
            int brightness = (int)((color.R * 299 + color.G * 587 + color.B * 114) / 1000);
            return brightness > 128 ? Color.Black : Color.White;
        }

        private void InitializeEncodingComboBox()
        {
            _encodingComboBox.Items.Clear();
            _encodingComboBox.Items.Add(new EncodingComboBoxItem(Encoding.Default));
            _encodingComboBox.Items.Add(new EncodingComboBoxItem(new UTF8Encoding(false))); // UTF8 without BOM
            _encodingComboBox.Items.Add(new EncodingComboBoxItem(new UTF8Encoding(true)));  // UTF8 with BOM
            _encodingComboBox.Items.Add(new EncodingComboBoxItem(Encoding.ASCII));
            _encodingComboBox.Items.Add(new EncodingComboBoxItem(Encoding.Unicode));
            _encodingComboBox.Visible = true;
        }

        private class EncodingComboBoxItem
        {
            public Encoding Encoding { get; private set; }

            public EncodingComboBoxItem(Encoding encoding)
            {
                Encoding = encoding;
            }

            public override string ToString()
            {
                return EncodingHelper.GetEncodingDisplayName(Encoding);
            }

            public override bool Equals(object obj)
            {
                EncodingComboBoxItem other = obj as EncodingComboBoxItem;
                if (other == null)
                    return false;

                if (Encoding is UTF8Encoding && other.Encoding is UTF8Encoding)
                {
                    UTF8Encoding enc1 = (UTF8Encoding)Encoding;
                    UTF8Encoding enc2 = (UTF8Encoding)other.Encoding;
                    return enc1.GetPreamble().Length == enc2.GetPreamble().Length;
                }
                return Encoding == other.Encoding;
            }

            public override int GetHashCode()
            {
                return Encoding.GetHashCode();
            }
        }

        public bool Paused
        {
            get
            {
                return _pauseMode;
            }
            set
            {
                _pauseMode = value;
                if (_pauseMode)
                    SetStatusBar("Paused");
                else
                    SetStatusBar(null);
            }
        }

        public Form TailWindow { get { return this; } }

        /// <summary>Absolute path of the log file currently being tailed, used by single instance to tell whether it is already open.</summary>
        public string CurrentFilePathAbsolute => _logTailStream?.FilePathAbsolute;
        /// <summary>Reason for the most recent load failure, used by the batch open summary.</summary>
        public string LastLoadFailureReason { get; private set; }

        public void LoadFile(string filepath)
        {
            TailFileConfig tailConfig = new TailFileConfig();
            tailConfig.FilePath = filepath;
            LoadConfig(tailConfig, "");
        }

        public void LoadConfig(TailFileConfig tailConfig, string configPath)
        {
            // Clear the failure reason before each load so the state matches the current file.
            LastLoadFailureReason = null;
            _configPath = configPath;
            _currentTailConfig = tailConfig;
            if (_currentTailConfig.EnabledDisplayPlugins == null)
                _currentTailConfig.EnabledDisplayPlugins = new List<string>();

            try
            {
                new DirectoryInfo(Path.GetDirectoryName(Path.Combine(configPath, tailConfig.FilePath)));
            }
            catch (System.ArgumentException ex)
            {
                LastLoadFailureReason = string.Format("{0}: {1}", ex.GetType().FullName, ex.Message);
                MessageBox.Show(this, String.Format("Failed to open file:\n\n{0}\n\nError:{1}", tailConfig.FilePath, ex.Message), "Invalid filename", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                Close();
                return;
            }

            // Old config compatibility: fall back to stable defaults for missing or invalid values.
            if (tailConfig.FileCacheSize <= 0)
                tailConfig.FileCacheSize = TailFileConfig.DefaultFileCacheSize;
            if (tailConfig.FileCheckInterval <= 0)
                tailConfig.FileCheckInterval = TailFileConfig.DefaultFileCheckIntervalSeconds;
            if (tailConfig.FileChangeCheckInterval <= 0)
                tailConfig.FileChangeCheckInterval = TailFileConfig.DefaultFileChangeCheckIntervalMs;

            if (tailConfig.FormBackColor != null)
                _tailListView.BackColor = tailConfig.FormBackColor.Value;
            if (tailConfig.FormTextColor != null)
                _tailListView.ForeColor = tailConfig.FormTextColor.Value;
            // Default (system) colors follow the theme, user-chosen colors are kept as they are
            Theme.Apply(_tailListView);

            if (tailConfig.FormFont != null)
                _tailListView.Font = tailConfig.FormFont;

            if (tailConfig.FormBookmarkBackColor != null)
                _bookmarkBackColor = tailConfig.FormBookmarkBackColor.Value;
            if (tailConfig.FormBookmarkTextColor != null)
                _bookmarkTextColor = tailConfig.FormBookmarkTextColor.Value;

            _tailTimer.Interval = tailConfig.FileChangeCheckInterval;

            _externalTools = tailConfig.ExternalTools;
            externalToolsToolStripMenuItem.DropDownItems.Clear();
            externalToolsToolStripMenuItem.Enabled = false;
            if (_externalTools != null)
            {
                foreach (ExternalToolConfig externalTool in _externalTools)
                {
                    ToolStripMenuItem toolItem = externalToolsToolStripMenuItem.DropDownItems.Add(externalTool.Name) as ToolStripMenuItem;
                    if (toolItem != null)
                    {
                        toolItem.Tag = externalTool;
                        toolItem.Click += new EventHandler(externalToolMenuItem_Click);
                        if (externalTool.ShortcutKeyEnum.HasValue)
                            toolItem.ShortcutKeys = externalTool.ShortcutKeyEnum.Value;
                    }
                    externalToolsToolStripMenuItem.Enabled = true;
                }
            }

            _loghitCounter = -1;
            _keywordHighlight = tailConfig.KeywordHighlight;
            if (_keywordHighlight != null)
            {
                foreach (TailKeywordConfig keyword in _keywordHighlight)
                {
                    if (keyword.MatchRegularExpression)
                    {
                        if (keyword.MatchCaseSensitive)
                            keyword.KeywordRegex = new System.Text.RegularExpressions.Regex(keyword.Keyword);
                        else
                            keyword.KeywordRegex = new System.Text.RegularExpressions.Regex(keyword.Keyword, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    }
                    else
                        keyword.KeywordRegex = null;

                    if (keyword.LogHitCounter)
                        _loghitCounter = 0;

                    if (!string.IsNullOrEmpty(keyword.ExternalToolName))
                    {
                        keyword.ExternalToolConfig = _externalTools.Find((externalTool) => string.Compare(externalTool.Name, keyword.ExternalToolName) == 0);
                        if (_threadPoolQueue == null)
                            _threadPoolQueue = new ThreadPoolQueue(0);   // Prepare the threadpool for use
                    }
                }
            }

            Encoding fileEncoding = tailConfig.EnumFileEncoding;

            // Auto-detect encoding if not explicitly set or if file exists
            if (fileEncoding == Encoding.Default && !string.IsNullOrEmpty(tailConfig.FilePath))
            {
                string filePath = Path.Combine(configPath, tailConfig.FilePath);
                if (File.Exists(filePath))
                {
                    Encoding detectedEncoding = EncodingHelper.DetectFileEncoding(filePath);
                    if (detectedEncoding != null && detectedEncoding != Encoding.Default)
                    {
                        fileEncoding = detectedEncoding;
                        tailConfig.EnumFileEncoding = detectedEncoding;
                    }
                }
            }

            // Update encoding combo box
            UpdateEncodingComboBox(fileEncoding);

            if (_logTailStream != null)
                _logTailStream.Reset();

            if (_logFileStream == null || _logFileStream.FilePath != tailConfig.FilePath || _logFileStream.FileEncoding != fileEncoding || _logFileStream.FileCheckInterval != tailConfig.FileCheckInterval || _logFileStream.FileCheckPattern != tailConfig.FileCheckPattern)
            {
                if (_logFileStream != null)
                    _logFileStream.Dispose();
                _logFileStream = new LogFileStream(configPath, tailConfig.FilePath, fileEncoding, tailConfig.FileCheckInterval, tailConfig.FileCheckPattern);
            }
            if (_logTailStream == null || _logTailStream.FilePath != tailConfig.FilePath || _logTailStream.FileEncoding != fileEncoding || _logTailStream.FileCheckInterval != tailConfig.FileCheckInterval || _logTailStream.FileCheckPattern != tailConfig.FileCheckPattern)
            {
                if (_logTailStream != null)
                    _logTailStream.Dispose();
                _logTailStream = new LogFileStream(configPath, tailConfig.FilePath, fileEncoding, tailConfig.FileCheckInterval, tailConfig.FileCheckPattern);
                if (_logTailStream.Length > 500 * 1024 * 1024)
                {
                    if (MessageBox.Show(this, String.Format("The file is very large, sure you want to open it?\n\nFile Name: {0}\nFile Size: {1} Megabytes", _logTailStream.FilePath, _logTailStream.Length / 1024 / 1024), "Large file detected", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                    {
                        LastLoadFailureReason = string.Format("User canceled opening a very large file: File={0}, SizeMB={1}", _logTailStream.FilePath, _logTailStream.Length / 1024 / 1024);
                        Close();
                        return;
                    }
                }
                if (_logFileCache != null)
                {
                    _logFileCache.Reset();
                    _logFileCache = null;   // Reset Cache, as the file contents can have changed
                }
            }

            _logTailStream.FileReloadedEvent += new EventHandler(_logTailStream_FileReloadedEvent);
            _logTailStream.FileChangedEvent += new EventHandler(_logTailStream_FileChangedEvent);

            if (_logFileCache != null)
                _logFileCache.Reset();

            if (_logFileCache == null || _logFileCache.Items.Count != tailConfig.FileCacheSize)
            {
                _logFileCache = new LogFileCache(tailConfig.FileCacheSize);
                _logFileCache.LoadingFileEvent += new EventHandler(_logFileCache_LoadingFileEvent);
                _logFileCache.FillCacheEvent += new EventHandler(_logFileCache_FillCacheEvent);
                // Add loading of cache while counting lines in file
                int lineCount = _logFileCache.FillTailCache(_logTailStream);
                _tailListView.VirtualListSize = lineCount;
                _savedLineNumber = 0; // Reset the line number offset
                _originalVirtualListSize = 0; // Reset the original size
                if (_quickFilter)
                {
                    ApplyKeywordFilter();
                }
            }
            else
            {
                _logFileCache.LoadingFileEvent += new EventHandler(_logFileCache_LoadingFileEvent);
                _logFileCache.FillCacheEvent += new EventHandler(_logFileCache_FillCacheEvent);
            }

            if (_taskMonitor != null)
            {
                _taskMonitor.Dispose();
                _taskMonitor = null;
            }
            if (!string.IsNullOrEmpty(tailConfig.ServiceName))
                _taskMonitor = new TaskMonitor(tailConfig.ServiceName, tailConfig.ServiceMachineName);

            // Reload display plugins and restore the enabled order
            InitializeDisplayPlugins(tailConfig);
            InvalidateProcessedLineCache();

            _formTitleMatchFilename = tailConfig.TitleMatchFilename;
            if (_formTitleMatchFilename)
                _formTitle = Path.GetFileName(_logTailStream.Name);
            else if (tailConfig.Title != null)
                _formTitle = tailConfig.Title;
            else
                _formTitle = Path.GetFileName(tailConfig.FilePath);

            UpdateFormTitle(true);

            _displayTabIcon = tailConfig.DisplayTabIcon;

            if (!string.IsNullOrEmpty(tailConfig.IconFile))
            {
                _formIconFile = tailConfig.IconFile;
                string formIconFilePathAbsolute = System.IO.Path.Combine(configPath, _formIconFile);
                try
                {
                    _formCustomIcon = System.Drawing.Icon.ExtractAssociatedIcon(formIconFilePathAbsolute);
                    _formMaximizedIcon = Icon;
                    Icon = _formCustomIcon;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Failed to load icon\n\n   " + formIconFilePathAbsolute + "\n\n" + ex.Message, null, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            UpdateFormTitle(true);

            // Load the quick keyword settings
            // Set the field values first so events don't fire with stale values
            _quickKeyword = tailConfig.QuickKeyword ?? "";
            _quickHighlight = tailConfig.QuickHighlight;
            _quickFilter = tailConfig.QuickFilter;
            _quickInverse = tailConfig.QuickInverse;

            if (tailConfig.FormQuickHighlightColor.HasValue)
            {
                _quickHighlightColor = tailConfig.FormQuickHighlightColor.Value;
            }

            // Then update the UI controls
            if (_keywordTextBox != null)
            {
                _keywordTextBox.Text = _quickKeyword;
            }
            if (_highlightCheckBox != null)
            {
                _highlightCheckBox.Checked = _quickHighlight;
            }
            if (_filterCheckBox != null)
            {
                _filterCheckBox.Checked = _quickFilter;
            }
            if (_inverseCheckBox != null)
            {
                _inverseCheckBox.Checked = _quickInverse;
            }
            if (_highlightColorButton != null && tailConfig.FormQuickHighlightColor.HasValue)
            {
                _highlightColorButton.BackColor = _quickHighlightColor;
                _highlightColorButton.ForeColor = GetContrastColor(_quickHighlightColor);
            }

            // If filtering or highlighting is enabled, apply it
            if (_quickFilter && !string.IsNullOrEmpty(_quickKeyword))
            {
                ApplyKeywordFilter();
            }
            else if (_quickHighlight && !string.IsNullOrEmpty(_quickKeyword))
            {
                _tailListView.Invalidate();
            }

            if (Visible)
            {
                if (_tailListView.VirtualListSize > 0)
                {
                    _tailListView.EnsureVisible(_tailListView.VirtualListSize - 1);
                    _tailListView.FocusedItem = _tailListView.Items[_tailListView.VirtualListSize - 1];
                    _tailListView.SelectedIndices.Add(_tailListView.VirtualListSize - 1);
                    _tailListView.Invalidate();
                    _tailListView.Update();
                }
            }
        }

        void _logTailStream_FileReloadedEvent(object sender, EventArgs e)
        {
            // If new file has arrived, then update form-title to display this
            if (_formTitleMatchFilename)
            {
                string fileName = Path.GetFileName(_logTailStream.Name);
                if (!String.IsNullOrEmpty(fileName))
                    _formTitle = fileName;
                UpdateFormTitle(true);
            }

            // Reset bookmarks
            if (_bookmarks.Count > 0)
            {
                _bookmarks.Clear();
                _tailListView.Invalidate();
            }

            // The file was reloaded; reset the line number offset
            _savedLineNumber = 0;
            InvalidateProcessedLineCache();
            MarkTabChangedIfInactive();

            SetStatusBar(null);
        }

        void UpdateFormTitle(bool force)
        {
            if (!force && DateTime.Now.Subtract(_lastFormTitleUpdate) < TimeSpan.FromSeconds(1))
                return;

            string title = _formTitle;

            TabPage parentTab = this.Tag as TabPage;
            if (parentTab != null)
            {
                if (parentTab.Text != title)
                    parentTab.Text = title;

                string fileStreamPath = _logTailStream != null ? _logTailStream.Name : string.Empty;
                if (!String.IsNullOrEmpty(fileStreamPath))
                    parentTab.ToolTipText = fileStreamPath;
                else
                    parentTab.ToolTipText = _logTailStream != null ? _logTailStream.FilePathAbsolute : string.Empty;
            }

            StringBuilder sb = null;

            if (_loghitCounter != -1)
            {
                sb = sb != null ? sb : new StringBuilder(title);
                sb.Append(" Hits=").Append(_loghitCounter);
                _loghitCounter = 0;
            }

            if (_taskMonitor != null)
            {
                sb = sb != null ? sb : new StringBuilder(title);

                try
                {
                    float cpuUtilization = _taskMonitor.ProcessorUsage;
                    Process process = _taskMonitor.Process;
                    if (process != null)
                    {
                        sb.AppendFormat(" CPU={0:F0}", cpuUtilization);

                        process.Refresh();
                        sb.Append(" RAM=").Append(process.PrivateMemorySize64 / (1024 * 1024));
                        sb.Append(_taskMonitor.ServiceRunning ? " (Started)" : " (Stopped)");
                    }
                    else
                    {
                        sb.Append(" (Stopped)");
                    }
                }
                catch (Exception ex)
                {
                    sb.Append(" (").Append(ex.Message).Append(")");
                }
            }

            string logSizeText = GetFileSizeStatusText();
            if (!string.IsNullOrEmpty(logSizeText))
            {
                if (sb != null)
                {
                    sb.Append(" LogSize=");
                    sb.Append(logSizeText);
                }
                else
                {
                    title = string.Format("{0} LogSize={1}", title, logSizeText);
                }
            }

            _lastFormTitleUpdate = DateTime.Now;

            if (sb != null)
                title = sb.ToString();

            if (Text != title)
                Text = title;

            SetStatusBar(null);
        }

        private static string ConvertFileSize(long fileSize)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = fileSize;
            int order = 0;
            while (len >= 1024 && ++order < sizes.Length)
            {
                len = len / 1024;
            }
            if (order == 0)
                return string.Format("{0:0} {1}", len, sizes[order]);
            else if (order == 1)
                return string.Format("{0:0.0} {1}", len, sizes[order]);
            else
                return string.Format("{0:0.00} {1}", len, sizes[order]);
        }

        private string GetFileSizeStatusText()
        {
            long fileSize = _logTailStream != null ? _logTailStream.Position : 0;
            if (fileSize > 0)
                return ConvertFileSize(fileSize);
            else
                return string.Empty;
        }

        public void SetStatusBar(string text)
        {
            SetStatusBar(text, 0, 0);
        }

        public void SetStatusBar(string text, int progressValue, int progressMax)
        {
            if (_statusStrip.Visible)
            {
                _statusProgressBar.Maximum = progressMax;
                _statusProgressBar.Value = progressValue;
                if (progressMax == 0 && progressValue == 0)
                    _statusProgressBar.Visible = false;
                else
                    _statusProgressBar.Visible = true;

                if (text != _statusTextBar.Text || progressValue != 0 || progressMax != 0)
                {
                    _statusTextBar.Text = text;
                    _statusStrip.Invalidate();
                    _statusStrip.Update();
                }
            }
            else if (MainForm.Instance != null)
            {
                if (text == null)
                {
                    // While loading before the form is shown, our own status strip isn't visible yet and the
                    // "Loading file..." text went to the main window, so clear it there even though we aren't active yet
                    if (MainForm.Instance.ActiveMdiChild == this || !Visible)
                        MainForm.Instance.SetStatusBar(Paused ? "Paused" : "Ready", progressValue, progressMax);
                }
                else
                    MainForm.Instance.SetStatusBar(text, progressValue, progressMax);
            }
        }

        void _logFileCache_LoadingFileEvent(object sender, EventArgs e)
        {
            LogFileStream filestream = sender as LogFileStream;
            if (filestream != null)
            {
                double position = filestream.Length != 0 ? filestream.Position / (double)filestream.Length * 100 : 100;
                SetStatusBar("Loading file...", (int)position, 100);
            }
            else
            {
                SetStatusBar(null);
            }
        }

        void _logFileCache_FillCacheEvent(object sender, EventArgs e)
        {
            if (sender != null)
            {
                SetStatusBar("Loading file cache...");
            }
            else
            {
                SetStatusBar(null);
            }
        }

        private void UpdateEncodingComboBox(Encoding encoding)
        {
            if (encoding == null)
                encoding = Encoding.Default;

            _encodingComboBox.SelectedIndexChanged -= _encodingComboBox_SelectedIndexChanged;

            // Find matching encoding in combo box
            EncodingComboBoxItem targetItem = new EncodingComboBoxItem(encoding);
            foreach (object item in _encodingComboBox.Items)
            {
                EncodingComboBoxItem comboItem = item as EncodingComboBoxItem;
                if (comboItem != null && comboItem.Equals(targetItem))
                {
                    _encodingComboBox.SelectedItem = item;
                    _encodingComboBox.SelectedIndexChanged += _encodingComboBox_SelectedIndexChanged;
                    return;
                }
            }

            // If not found, select default
            _encodingComboBox.SelectedIndex = 0;
            _encodingComboBox.SelectedIndexChanged += _encodingComboBox_SelectedIndexChanged;
        }

        private void _encodingComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_encodingComboBox.SelectedItem == null || _currentTailConfig == null)
                return;

            EncodingComboBoxItem selectedItem = _encodingComboBox.SelectedItem as EncodingComboBoxItem;
            if (selectedItem == null)
                return;

            Encoding newEncoding = selectedItem.Encoding;
            if (newEncoding == null)
                return;

            // Check if encoding actually changed
            Encoding currentEncoding = _currentTailConfig.EnumFileEncoding;
            EncodingComboBoxItem currentItem = new EncodingComboBoxItem(currentEncoding);
            if (selectedItem.Equals(currentItem))
                return; // Same encoding

            // Update config
            _currentTailConfig.EnumFileEncoding = newEncoding;

            // Reload file with new encoding
            string filePath = _currentTailConfig.FilePath;
            string configPath = _configPath;

            // Dispose old streams
            if (_logFileStream != null)
            {
                _logFileStream.Dispose();
                _logFileStream = null;
            }
            if (_logTailStream != null)
            {
                _logTailStream.Dispose();
                _logTailStream = null;
            }

            // Clear cache
            if (_logFileCache != null)
            {
                _logFileCache.Reset();
                _logFileCache = null;
            }

            // Reload with new encoding
            _logFileStream = new LogFileStream(configPath, filePath, newEncoding, _currentTailConfig.FileCheckInterval, _currentTailConfig.FileCheckPattern);
            _logTailStream = new LogFileStream(configPath, filePath, newEncoding, _currentTailConfig.FileCheckInterval, _currentTailConfig.FileCheckPattern);
            _logTailStream.FileReloadedEvent += new EventHandler(_logTailStream_FileReloadedEvent);
            _logTailStream.FileChangedEvent += new EventHandler(_logTailStream_FileChangedEvent);

            // Recreate cache
            _logFileCache = new LogFileCache(_currentTailConfig.FileCacheSize);
            _logFileCache.LoadingFileEvent += new EventHandler(_logFileCache_LoadingFileEvent);
            _logFileCache.FillCacheEvent += new EventHandler(_logFileCache_FillCacheEvent);
            InvalidateProcessedLineCache();

            // Reload the view
            _tailListView.Items.Clear();
            _tailListView.VirtualListSize = 0;
            _savedLineNumber = 0; // Reset the line number offset
            _topIndexHack = true;
            Application.DoEvents();

            // Load file cache
            int lineCount = _logFileCache.FillTailCache(_logTailStream);
            _tailListView.VirtualListSize = lineCount;

            // Scroll to bottom
            if (_tailListView.Items.Count > 0)
            {
                _tailListView.TopItem = _tailListView.Items[_tailListView.Items.Count - 1];
            }

            SetStatusBar("Encoding changed to " + EncodingHelper.GetEncodingDisplayName(newEncoding));
        }

        public void SaveConfig(TailFileConfig tailConfig)
        {
            tailConfig.FormFont = _tailListView.Font;
            tailConfig.FormBackColor = Theme.UnthemedBackColor(_tailListView);
            tailConfig.FormTextColor = Theme.UnthemedForeColor(_tailListView);

            tailConfig.FormBookmarkBackColor = _bookmarkBackColor;
            tailConfig.FormBookmarkTextColor = _bookmarkTextColor;

            tailConfig.KeywordHighlight = _keywordHighlight;

            tailConfig.ExternalTools = _externalTools;

            tailConfig.FileCacheSize = _logFileCache.Items.Count;
            // Save encoding from combo box if available, otherwise from stream
            if (_encodingComboBox.SelectedItem != null)
            {
                EncodingComboBoxItem selectedItem = _encodingComboBox.SelectedItem as EncodingComboBoxItem;
                if (selectedItem != null && selectedItem.Encoding != null)
                {
                    tailConfig.EnumFileEncoding = selectedItem.Encoding;
                }
                else
                {
                    tailConfig.EnumFileEncoding = _logTailStream.FileEncoding;
                }
            }
            else
            {
                tailConfig.EnumFileEncoding = _logTailStream.FileEncoding;
            }
            tailConfig.FilePath = _logTailStream.FilePath;
            tailConfig.FileCheckInterval = _logTailStream.FileCheckInterval;
            tailConfig.FileChangeCheckInterval = _tailTimer.Interval;
            tailConfig.FileCheckPattern = _logTailStream.FileCheckPattern;
            tailConfig.TitleMatchFilename = _formTitleMatchFilename;
            tailConfig.Title = _formTitle;
            tailConfig.IconFile = _formIconFile;
            tailConfig.Modeless = MdiParent == null;
            tailConfig.WindowState = WindowState;
            if (WindowState == FormWindowState.Minimized)
            {
                tailConfig.WindowSize = RestoreBounds.Size;
                tailConfig.WindowPosition = RestoreBounds.Location;
            }
            else
            {
                tailConfig.WindowSize = Size;
                tailConfig.WindowPosition = DesktopLocation;
            }

            if (_taskMonitor != null)
            {
                tailConfig.ServiceName = _taskMonitor.ServiceName;
                tailConfig.ServiceMachineName = _taskMonitor.ServiceMachineName;
            }
            else
            {
                tailConfig.ServiceName = "";
                tailConfig.ServiceMachineName = "";
            }

            tailConfig.DisplayTabIcon = _displayTabIcon;

            // Save the quick keyword settings
            tailConfig.QuickKeyword = _quickKeyword;
            tailConfig.QuickHighlight = _quickHighlight;
            tailConfig.FormQuickHighlightColor = _quickHighlightColor;
            tailConfig.QuickFilter = _quickFilter;
            tailConfig.QuickInverse = _quickInverse;
            tailConfig.EnabledDisplayPlugins = _displayTextProcessor.EnabledPlugins.Select(x => x.DisplayName).ToList();
        }

        public void CopySelectionToClipboard()
        {
            try
            {
                // Prefer copying the text selected within the line (double-click word/line selection)
                if (!string.IsNullOrEmpty(_inlineSelectionText))
                {
                    ClipboardHelper.CopyToClipboard(_inlineSelectionText);
                    return;
                }
                // Otherwise copy the selected lines
                StringBuilder selection = new StringBuilder();
                foreach (int itemIndex in _tailListView.SelectedIndices)
                {
                    if (selection.Length > 0)
                        selection.AppendLine();
                    selection.Append(_tailListView.Items[itemIndex].Text);
                }
                ClipboardHelper.CopyToClipboard(selection.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to copy to clipboard, maybe another application is locking the clipboard.\n\n" + ex.Message);
            }
        }

        private bool MatchTextSearch(int lineNumber, string lineText, string searchText, bool matchCase, bool lineHighlights)
        {
            if (lineHighlights)
            {
                if (MatchesBookmark(lineNumber))
                    return true;

                if (MatchesKeyword(lineText, true, false) != null)
                    return true;

                return false;
            }
            else if (matchCase)
            {
                if (lineText.Contains(searchText))
                {
                    return true;
                }
            }
            else
            {
                if (0 <= lineText.IndexOf(searchText, StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private int SearchForTextForward(string searchText, bool matchCase, bool lineHighlights, int startIndex, int endIndex, ref LogFileCache searchFileCache)
        {
            int i = 0;
            int lastPumpTick = Environment.TickCount;

            try
            {
                if (CanUseDirectRawSearch(lineHighlights))
                {
                    return SearchForRawTextForward(searchText, matchCase, startIndex, endIndex, ref lastPumpTick);
                }

                for (i = startIndex; i < endIndex; ++i)
                {
                    if (i % _logFileCache.Items.Count == 0)
                    {
                        SetStatusBar("Searching...", i, endIndex);
                        PumpSearchUiIfNeeded(ref lastPumpTick);
                    }

                    // The search text uses raw or processed text depending on the enabled features.
                    string lineText = GetSearchLineTextByVirtualIndex(i, lineHighlights);

                    if (MatchTextSearch(i, lineText, searchText, matchCase, lineHighlights))
                        return i;

                    PumpSearchUiIfNeeded(ref lastPumpTick);
                }
            }
            catch (Exception ex)
            {
                string exceptionDetails = ex.Message + "\r\n";
                if (searchFileCache != null)
                {
                    exceptionDetails += "SearchFileCache = ";
                    if (searchFileCache.Items != null)
                        exceptionDetails += "Items = " + searchFileCache.Items.Count.ToString() + ", ";
                    else
                        exceptionDetails = "Items = null, ";
                }
                else
                {
                    exceptionDetails += "SearchFileCache = null, ";
                }
                if (_logFileCache != null)
                {
                    exceptionDetails += "LogFileCache = ";
                    if (searchFileCache != null && searchFileCache.Items != null)
                        exceptionDetails += "Items = " + searchFileCache.Items.Count.ToString() + ", ";
                    else
                        exceptionDetails = "Items = null, ";
                }
                else
                {
                    exceptionDetails += "LogFileCache = null, ";
                }
                exceptionDetails += "VirtualListSize = " + _tailListView.VirtualListSize.ToString() + ", ";
                exceptionDetails += "StartIndex = " + startIndex.ToString() + ", ";
                exceptionDetails += "EndIndex = " + endIndex.ToString() + ", ";
                exceptionDetails += "CurrentIndex = " + i.ToString() + ", ";
                throw new InvalidOperationException(exceptionDetails, ex);
            }
            return -1;
        }

        /// <summary>
        /// Scans the raw text sequentially, avoiding repeated trips through the cache and display pipeline during search.
        /// </summary>
        private int SearchForRawTextForward(string searchText, bool matchCase, int startIndex, int endIndex, ref int lastPumpTick)
        {
            int startLineNumber = _savedLineNumber + startIndex + 1;
            int endLineNumberExclusive = _savedLineNumber + endIndex + 1;
            int progressTick = lastPumpTick;
            int matchedLineNumber = _logFileStream.SearchTextRange(
                startLineNumber,
                endLineNumberExclusive,
                searchText,
                matchCase,
                findLastMatch: false,
                progressCallback: currentLineNumber =>
                {
                    int currentIndex = currentLineNumber - _savedLineNumber - 1;
                    UpdateSearchProgressIfNeeded(currentIndex, endIndex, ref progressTick);
                });
            UpdateSearchProgressIfNeeded(endIndex, endIndex, ref progressTick);
            lastPumpTick = progressTick;
            return matchedLineNumber == -1 ? -1 : matchedLineNumber - _savedLineNumber - 1;
        }

        public bool SearchForText(string searchText, bool matchCase, bool searchForward, bool lineHighlights, bool wrapAround)
        {
            if (_tailListView.VirtualListSize == 0)
                return false;

            if (lineHighlights && (_keywordHighlight == null || _keywordHighlight.Count == 0) && _bookmarks.Count == 0)
                return false;

            // Use selection if it is below top-index
            int startIndex = _tailListView.VirtualListSize - 1;
            if (_tailListView.SelectedIndices.Count > 0)
            {
                if (_tailListView.TopItem == null || _tailListView.TopItem.Index - 1 < _tailListView.SelectedIndices[0])
                    startIndex = _tailListView.SelectedIndices[0];
            }

            int matchFound = -1;
            if (!searchForward)
            {
                matchFound = SearchForTextBackward(searchText, matchCase, lineHighlights, 0, startIndex);
                //Retry if not found
                if (matchFound == -1 && wrapAround)
                {
                    matchFound = SearchForTextBackward(searchText, matchCase, lineHighlights, startIndex + 1, _tailListView.VirtualListSize);
                }
            }
            else
            {
                LogFileCache searchFileCache = null;
                matchFound = SearchForTextForward(searchText, matchCase, lineHighlights, startIndex + 1, _tailListView.VirtualListSize, ref searchFileCache);
                //Retry if not found
                if (matchFound == -1 && wrapAround)
                {
                    matchFound = SearchForTextForward(searchText, matchCase, lineHighlights, 0, startIndex + 1, ref searchFileCache);
                }

                if (matchFound != -1)
                {
                    // Swap cache before displaying search result
                    if (searchFileCache != null)
                        _logFileCache = searchFileCache;
                }
            }

            SetStatusBar(null);
            if (matchFound != -1)
            {
                _tailListView.SelectedIndices.Clear();
                _tailListView.EnsureVisible(matchFound);
                _tailListView.SelectedIndices.Add(matchFound);
                _tailListView.Items[matchFound].Focused = true;
                return true;
            }

            return false;
        }

        private int SearchForTextBackward(string searchText, bool matchCase, bool lineHighlights, int startIndex, int endIndex)
        {
            if (startIndex >= endIndex)
                return -1;

            // Reverse search now scans forward and records the last hit, instead of rereading from the start of the file for each decreasing line number.
            int lastPumpTick = Environment.TickCount;
            int lastMatchFound = -1;
            if (CanUseDirectRawSearch(lineHighlights))
            {
                int startLineNumber = _savedLineNumber + startIndex + 1;
                int endLineNumberExclusive = _savedLineNumber + endIndex + 1;
                int progressTick = lastPumpTick;
                int matchedLineNumber = _logFileStream.SearchTextRange(
                    startLineNumber,
                    endLineNumberExclusive,
                    searchText,
                    matchCase,
                    findLastMatch: true,
                    progressCallback: currentLineNumber =>
                    {
                        int currentIndex = currentLineNumber - _savedLineNumber - 1;
                        UpdateSearchProgressIfNeeded(currentIndex, endIndex, ref progressTick);
                    });
                UpdateSearchProgressIfNeeded(endIndex, endIndex, ref progressTick);
                lastPumpTick = progressTick;
                return matchedLineNumber == -1 ? -1 : matchedLineNumber - _savedLineNumber - 1;
            }

            for (int i = startIndex; i < endIndex; ++i)
            {
                UpdateSearchProgressIfNeeded(i, endIndex, ref lastPumpTick);

                string lineText = GetSearchLineTextByVirtualIndex(i, lineHighlights);
                if (MatchTextSearch(i, lineText, searchText, matchCase, lineHighlights))
                    lastMatchFound = i;
            }

            return lastMatchFound;
        }

        private void _tailListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            int displayIndex = ResolveDisplayIndex(e.ItemIndex);
            if (_quickFilter && !string.IsNullOrEmpty(_quickKeyword) && !_filteredIndexMap.ContainsKey(e.ItemIndex))
            {
                ListViewItem emptyItem = new ListViewItem(string.Empty);
                emptyItem.SubItems.Add(string.Empty);
                e.Item = emptyItem;
                return;
            }

            if (!_topIndexHack)
            {
                _topIndexHack = true;
                ListViewItem topItem = _tailListView.TopItem;
                _topIndexHack = false;
                int topIndex = topItem != null ? topItem.Index : 0;
                if (topItem == null || e.ItemIndex < topIndex || e.ItemIndex > topIndex + 1000)
                {
                    ListViewItem emptyItem = new ListViewItem(string.Empty);
                    emptyItem.SubItems.Add(string.Empty);
                    e.Item = emptyItem;
                    return;
                }
            }

            string rawLineText = GetRawLineTextByDisplayIndex(displayIndex);
            string processedText = GetProcessedLineTextByDisplayIndex(displayIndex);
            ListViewItem displayItem = new ListViewItem(processedText ?? string.Empty);
            displayItem.SubItems.Add(rawLineText ?? string.Empty);
            e.Item = displayItem;
        }

        private void TailForm_Load(object sender, EventArgs e)
        {
            if (MdiParent == null)
            {
                Icon = MainForm.Instance.Icon;
            }

            // Keep the top keyword bar and bottom status bar above the Dock.Fill content area,
            // so the ListView doesn't cover the toolbar when repainting (which showed up as it only reappearing bit by bit on mouse-over).
            _statusStrip.Visible = true;
            this.Controls.Remove(_keywordToolStrip);
            this.Controls.Remove(_statusStrip);
            this.Controls.Add(_keywordToolStrip);
            this.Controls.Add(_statusStrip);

            _tailTimer.Enabled = true;

            _tailListView.EnableDoubleBuffer();

            using (Graphics graphics = _tailListView.CreateGraphics())
            {
                string textstring = new string('X', 1001);
                _tailListView.Columns[1].Width = (int)graphics.MeasureString(textstring, _tailListView.Font).Width;
            }

            if (_tailListView.VirtualListSize > 0)
            {
                _tailListView.EnsureVisible(_tailListView.VirtualListSize - 1);
                _tailListView.FocusedItem = _tailListView.Items[_tailListView.VirtualListSize - 1];
                _tailListView.SelectedIndices.Add(_tailListView.VirtualListSize - 1);
                _tailListView.Invalidate();
                _tailListView.Update();
            }
        }

        private void TailForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Prevent event callbacks while we are closing down
            _tailTimer.Enabled = false;
            if (_logFileCache != null)
                _logFileCache.Reset();
        }

        private void TailForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                try
                {
                    // Try to get the file list to verify permissions
                    Array array = e.Data.GetData(DataFormats.FileDrop) as Array;
                    if (array != null && array.Length > 0)
                    {
                        // Check whether the first file is accessible (don't actually open it, just check the path)
                        string firstFile = array.GetValue(0).ToString();
                        if (!string.IsNullOrEmpty(firstFile) && System.IO.Path.IsPathRooted(firstFile))
                        {
                            e.Effect = DragDropEffects.Copy;
                            return;
                        }
                    }
                }
                catch
                {
                    // If the permission check fails, still allow the drag and let DragDrop handle the error
                }
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void TailForm_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                Array array = e.Data.GetData(DataFormats.FileDrop) as Array;
                if (array == null)
                    return;

                // Extract strings from array
                List<string> filenames = new List<string>();
                foreach (object filename in array)
                {
                    filenames.Add(filename.ToString());
                }

                this.Activate();        // in the case Explorer overlaps this form

                // Call OpenFile asynchronously.
                // Explorer instance from which file is dropped is not responding
                // all the time when DragDrop handler is active, so we need to return
                // immidiately (especially if OpenFile shows MessageBox).
                System.Threading.ThreadPool.QueueUserWorkItem(worker_DoWork, filenames.ToArray());
            }
            catch (Exception ex)
            {
                // don't show MessageBox here - Explorer is waiting !
                System.Diagnostics.Debug.WriteLine("Drag Drop Failed: " + ex.Message);
            }
        }

        void worker_DoWork(object param)
        {
            // Discovered a strange problem where the Windows Explorer would lock, eventhough I deferred the actual DragDrop operation using BeginInvoke().
            // The solution was to create a thread, that slept for 100 ms and then invoked the wanted method. If I removed the sleep from the new thread,
            // then Windows Explorer would lock again. Very strange indeed.
            System.Threading.Thread.Sleep(100);
            this.BeginInvoke(new Action<string[]>(delegate(string[] filenames)
            {
                if (MainForm.Instance != null)
                {
                    try
                    {
                        // Validate file paths and permissions
                        List<string> validFiles = new List<string>();
                        List<string> invalidFiles = new List<string>();

                        foreach (string filename in filenames)
                        {
                            try
                            {
                                if (string.IsNullOrEmpty(filename))
                                    continue;

                                // Check whether the path is valid
                                if (!System.IO.Path.IsPathRooted(filename))
                                {
                                    invalidFiles.Add(filename);
                                    continue;
                                }

                                // Try to access the file info to verify permissions
                                // For directories, check that they exist
                                if (System.IO.Directory.Exists(filename))
                                {
                                    // Directories aren't supported yet, skip
                                    continue;
                                }

                                // For files, check whether they exist or can be accessed
                                if (System.IO.File.Exists(filename))
                                {
                                    // Try to get the file info to verify permissions
                                    System.IO.FileInfo fileInfo = new System.IO.FileInfo(filename);
                                    // If the file exists but can't be accessed, OpenFileSelection handles it
                                    validFiles.Add(filename);
                                }
                                else
                                {
                                    // The file doesn't exist, but it may be a new file or need special permissions
                                    // Try checking the parent directory's permissions
                                    string dir = System.IO.Path.GetDirectoryName(filename);
                                    if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                                    {
                                        validFiles.Add(filename);
                                    }
                                    else
                                    {
                                        invalidFiles.Add(filename);
                                    }
                                }
                            }
                            catch (System.UnauthorizedAccessException)
                            {
                                invalidFiles.Add(filename + " (insufficient permissions)");
                            }
                            catch (System.Security.SecurityException)
                            {
                                invalidFiles.Add(filename + " (insufficient security permissions)");
                            }
                            catch (Exception ex)
                            {
                                invalidFiles.Add(filename + " (" + ex.Message + ")");
                            }
                        }

                        // Open the valid files
                        if (validFiles.Count > 0)
                        {
                            MainForm.Instance.OpenFileSelection(validFiles.ToArray());
                        }

                        // Show an error message for the invalid files
                        if (invalidFiles.Count > 0)
                        {
                            string errorMsg = "The following files could not be opened:\n\n" + string.Join("\n", invalidFiles.ToArray());
                            MessageBox.Show(this, errorMsg, "File Drop Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "An error occurred while opening the files:\n\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }), new object[] { param });
        }

        private void _tailListView_CacheVirtualItems(object sender, CacheVirtualItemsEventArgs e)
        {
            int startIndex = ResolveDisplayIndex(e.StartIndex);
            int endIndex = ResolveDisplayIndex(e.EndIndex);
            _logFileCache.PrepareCache(startIndex, endIndex, false);
        }

        private void _tailListView_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
        }

        private void _tailListView_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != 1)
                return;

            Color? highlightColor = null;
            string highlightKeyword = null;
            bool isBookmark = false;

            // Bookmark coloring has higher priority, than keyword highlight
            if (MatchesBookmark(e.ItemIndex))
            {
                highlightColor = _bookmarkBackColor;
                isBookmark = true;
            }
            else
            {
                // Quick keyword highlight (takes priority over configured keywords)
                if (_quickHighlight && !string.IsNullOrEmpty(_quickKeyword))
                {
                    if (MatchesQuickKeyword(e.Item.Text))
                    {
                        highlightColor = _quickHighlightColor;
                        highlightKeyword = _quickKeyword;
                    }
                }

                // Configured keyword highlight
                if (!highlightColor.HasValue)
                {
                    TailKeywordConfig keyword = MatchesKeyword(e.Item.Text, false, true);
                    if (keyword != null)
                    {
                        if (keyword.FormBackColor.HasValue)
                        {
                            highlightColor = keyword.FormBackColor.Value;
                            highlightKeyword = keyword.Keyword;
                        }
                    }
                }
            }

            // Pick the colors locally: assigning e.SubItem.BackColor/ForeColor invalidates the whole ListView,
            // which made every paint queue another paint (endless repaint loop, high CPU)
            Color backColor;
            Color foreColor;
            if (e.Item.Selected)
            {
                backColor = SystemColors.Highlight;
                foreColor = highlightColor.HasValue
                    ? Color.FromArgb(SystemColors.Highlight.A, (SystemColors.Highlight.R + 128) % 256, (SystemColors.Highlight.G + 128) % 256, (SystemColors.Highlight.B + 128) % 256)
                    : SystemColors.HighlightText;
            }
            else
            {
                backColor = e.Item.ListView.BackColor;
                foreColor = e.Item.ListView.ForeColor;
            }

            // Draw the background
            using (SolidBrush backBrush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(backBrush, e.Bounds);
            }
            e.DrawFocusRectangle(e.Bounds);

            // If highlighted, draw a color block at the far left of the line
            const int colorBlockWidth = 4;
            if (highlightColor.HasValue)
            {
                Rectangle colorBlockRect = new Rectangle(e.Bounds.Left, e.Bounds.Top, colorBlockWidth, e.Bounds.Height);
                using (SolidBrush brush = new SolidBrush(highlightColor.Value))
                {
                    e.Graphics.FillRectangle(brush, colorBlockRect);
                }
            }

            // Compute the text drawing area (leaving room for the color block)
            Rectangle textBounds = new Rectangle(
                e.Bounds.Left + colorBlockWidth + 2,
                e.Bounds.Top,
                e.Bounds.Width - colorBlockWidth - 2,
                e.Bounds.Height);

            string text = e.Item.Text.Length > 1000 ? e.Item.Text.Substring(0, 1000) : e.Item.Text;
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.ExpandTabs | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

            // If there is a keyword highlight and it isn't a bookmark, draw the highlighted text
            if (highlightColor.HasValue && !string.IsNullOrEmpty(highlightKeyword) && !isBookmark && !e.Item.Selected)
            {
                DrawTextWithHighlight(e.Graphics, text, e.Item.ListView.Font, textBounds, foreColor, highlightColor.Value, highlightKeyword, flags);
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, text, e.Item.ListView.Font, textBounds, foreColor, flags);
            }

            // In-line selection highlight: draw the selection background and text over the selected text
            if (e.ItemIndex == _inlineSelectionItemIndex && _inlineSelectionLength > 0 && _inlineSelectionStart < text.Length)
            {
                int start = Math.Min(_inlineSelectionStart, text.Length);
                int len = Math.Min(_inlineSelectionLength, text.Length - start);
                if (len > 0)
                {
                    string beforeSel = text.Substring(0, start);
                    string selText = text.Substring(start, len);
                    Size beforeSize = TextRenderer.MeasureText(e.Graphics, beforeSel, e.Item.ListView.Font, new Size(int.MaxValue, textBounds.Height), flags);
                    Size selSize = TextRenderer.MeasureText(e.Graphics, selText, e.Item.ListView.Font, new Size(int.MaxValue, textBounds.Height), flags);
                    Rectangle selRect = new Rectangle(textBounds.Left + beforeSize.Width, textBounds.Top, selSize.Width, textBounds.Height);
                    using (SolidBrush brush = new SolidBrush(SystemColors.Highlight))
                    {
                        e.Graphics.FillRectangle(brush, selRect);
                    }
                    TextRenderer.DrawText(e.Graphics, selText, e.Item.ListView.Font, selRect, SystemColors.HighlightText, flags);
                }
            }
        }

        /// <summary>
        /// Draws text with keyword highlighting
        /// </summary>
        private void DrawTextWithHighlight(Graphics g, string text, Font font, Rectangle bounds, Color textColor, Color highlightColor, string keyword, TextFormatFlags flags)
        {
            // Find all keyword positions
            List<int> keywordPositions = new List<int>();
            int pos = 0;
            while ((pos = text.IndexOf(keyword, pos, StringComparison.CurrentCultureIgnoreCase)) != -1)
            {
                keywordPositions.Add(pos);
                pos += keyword.Length;
            }

            if (keywordPositions.Count == 0)
            {
                // No keyword found; draw directly
                TextRenderer.DrawText(g, text, font, bounds, textColor, flags);
                return;
            }

            // Draw the text in segments
            int currentX = bounds.Left;
            int lastEnd = 0;
            Color highlightTextColor = GetContrastColor(highlightColor);

            foreach (int keywordPos in keywordPositions)
            {
                // Draw the plain text before the keyword
                if (keywordPos > lastEnd)
                {
                    string beforeText = text.Substring(lastEnd, keywordPos - lastEnd);
                    Size beforeSize = TextRenderer.MeasureText(g, beforeText, font, new Size(int.MaxValue, bounds.Height), flags);
                    Rectangle beforeRect = new Rectangle(currentX, bounds.Top, beforeSize.Width, bounds.Height);
                    TextRenderer.DrawText(g, beforeText, font, beforeRect, textColor, flags);
                    currentX += beforeSize.Width - 4; // Subtract a little to compensate for spacing
                }

                // Draw the highlighted keyword
                string keywordText = text.Substring(keywordPos, keyword.Length);
                Size keywordSize = TextRenderer.MeasureText(g, keywordText, font, new Size(int.MaxValue, bounds.Height), flags);
                Rectangle keywordRect = new Rectangle(currentX, bounds.Top, keywordSize.Width, bounds.Height);

                // Draw the keyword background
                using (SolidBrush brush = new SolidBrush(highlightColor))
                {
                    Rectangle bgRect = new Rectangle(currentX, bounds.Top + 1, keywordSize.Width - 4, bounds.Height - 2);
                    g.FillRectangle(brush, bgRect);
                }

                // Draw the keyword text
                TextRenderer.DrawText(g, keywordText, font, keywordRect, highlightTextColor, flags);
                currentX += keywordSize.Width - 4;
                lastEnd = keywordPos + keyword.Length;
            }

            // Draw the last segment of plain text
            if (lastEnd < text.Length)
            {
                string afterText = text.Substring(lastEnd);
                Rectangle afterRect = new Rectangle(currentX, bounds.Top, bounds.Right - currentX, bounds.Height);
                TextRenderer.DrawText(g, afterText, font, afterRect, textColor, flags);
            }
        }

        private bool MatchesBookmark(int lineNumber)
        {
            if (_bookmarks.Count == 0)
                return false;

            return _bookmarks.Contains(lineNumber);
        }

        private TailKeywordConfig MatchesKeyword(string line, bool onlyKeywordHighlight, bool onlyTextColoring)
        {
            TailKeywordConfig matchKeyword = null;
            if (_keywordHighlight != null && _keywordHighlight.Count > 0)
            {
                foreach (TailKeywordConfig keyword in _keywordHighlight)
                {
                    if (onlyTextColoring)
                    {
                        if (!keyword.TextColoring.Value)
                            continue;
                    }
                    else if (matchKeyword != null)
                    {
                        // Ignore keywords that doesn't add extra detail to the existing keyword-match
                        if ((matchKeyword.ExternalToolConfig != null || keyword.ExternalToolConfig == null)
                          && (matchKeyword.LogHitCounter || !keyword.LogHitCounter)
                          && (matchKeyword.AlertHighlight.Value || !keyword.AlertHighlight.Value)
                            )
                            continue;
                    }

                    if ((keyword.KeywordRegex != null && keyword.KeywordRegex.IsMatch(line))
                      || (keyword.KeywordRegex == null && keyword.MatchCaseSensitive && line.IndexOf(keyword.Keyword, StringComparison.CurrentCulture) != -1)
                      || (keyword.KeywordRegex == null && !keyword.MatchCaseSensitive && line.IndexOf(keyword.Keyword, StringComparison.CurrentCultureIgnoreCase) != -1)
                       )
                    {
                        if (onlyKeywordHighlight)
                        {
                            // If high priority match is performing text-coloring, then no line match
                            if (keyword.TextColoring.Value && !keyword.AlertHighlight.Value)
                                return null;
                            else if (keyword.AlertHighlight.Value)
                                return keyword;
                        }
                        else if (onlyTextColoring)
                        {
                            return keyword;
                        }
                        else if (matchKeyword != null)
                        {
                            // Add extra detail to the existing keyword-match
                            matchKeyword = new TailKeywordConfig() { ExternalToolConfig = matchKeyword.ExternalToolConfig, LogHitCounter = matchKeyword.LogHitCounter, AlertHighlight = matchKeyword.AlertHighlight };
                            if (matchKeyword.ExternalToolConfig == null && keyword.ExternalToolConfig != null)
                                matchKeyword.ExternalToolConfig = keyword.ExternalToolConfig;
                            if (!matchKeyword.LogHitCounter && keyword.LogHitCounter)
                                matchKeyword.LogHitCounter = keyword.LogHitCounter;
                            if (!matchKeyword.AlertHighlight.Value && keyword.AlertHighlight.Value)
                                matchKeyword.AlertHighlight = keyword.AlertHighlight;
                        }
                        else
                        {
                            matchKeyword = keyword;
                        }

                        if (matchKeyword.LogHitCounter && matchKeyword.AlertHighlight.Value && matchKeyword.ExternalToolConfig != null)
                            return matchKeyword;	// We have all the details we need
                    }
                }
            }
            return matchKeyword;
        }

        private bool MatchesQuickKeyword(string line)
        {
            if (string.IsNullOrEmpty(_quickKeyword))
                return false;

            bool matches = false;
            if (_quickInverse)
            {
                // Inverted match: does not contain the keyword
                matches = line.IndexOf(_quickKeyword, StringComparison.CurrentCultureIgnoreCase) == -1;
            }
            else
            {
                // Normal match: contains the keyword
                matches = line.IndexOf(_quickKeyword, StringComparison.CurrentCultureIgnoreCase) != -1;
            }
            return matches;
        }

        private void UpdateFilteredIndexMap()
        {
            _filteredIndexMap.Clear();
            _filteredVirtualListSize = 0;

            if (!_quickFilter || string.IsNullOrEmpty(_quickKeyword))
            {
                return;
            }

            // Save the original size (if not saved yet)
            if (_originalVirtualListSize == 0)
            {
                _originalVirtualListSize = _tailListView.VirtualListSize;
            }

            // Go through all lines and find the matching ones
            int virtualIndex = 0;
            int actualSize = _originalVirtualListSize > 0 ? _originalVirtualListSize : _tailListView.VirtualListSize;

            for (int i = 0; i < actualSize; i++)
            {
                // Filtering is always based on processed text
                string lineText = GetProcessedLineTextByDisplayIndex(i);

                if (lineText != null && MatchesQuickKeyword(lineText))
                {
                    _filteredIndexMap[virtualIndex] = i;
                    virtualIndex++;
                }
            }

            _filteredVirtualListSize = virtualIndex;
        }

        private void ApplyKeywordFilter()
        {
            if (_quickFilter && !string.IsNullOrEmpty(_quickKeyword))
            {
                UpdateFilteredIndexMap();
                ListViewUtil.SetVirtualListSizeWithoutRefresh(_tailListView, _filteredVirtualListSize);
                _tailListView.Invalidate();
            }
            else
            {
                // Filter canceled; restore the original size
                if (_filteredIndexMap.Count > 0 || _originalVirtualListSize > 0)
                {
                    _filteredIndexMap.Clear();
                    _filteredVirtualListSize = 0;
                    int restoreSize = _originalVirtualListSize > 0 ? _originalVirtualListSize : _tailListView.VirtualListSize;
                    ListViewUtil.SetVirtualListSizeWithoutRefresh(_tailListView, restoreSize);
                    _originalVirtualListSize = 0;
                    _tailListView.Invalidate();
                }
            }
        }

        private void _tailListView_MouseDown(object sender, MouseEventArgs e)
        {
            // Clear the in-line selection on single click, so a following Ctrl+C copies the whole line
            ClearInlineSelection();
        }

        private void _tailListView_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo hit = _tailListView.HitTest(e.Location);
            if (hit.Item == null || e.Button != MouseButtons.Left)
                return;

            int virtualIndex = hit.Item.Index;
            int displayIndex = _filteredVirtualListSize > 0 && _filteredIndexMap != null && _filteredIndexMap.ContainsKey(virtualIndex)
                ? _filteredIndexMap[virtualIndex]
                : virtualIndex;

            ListViewItem lvi = _logFileCache.LookupCache(displayIndex);
            if (lvi == null)
            {
                _logFileCache.PrepareCache(Math.Max(0, displayIndex - 50), displayIndex + 50, false);
                lvi = _logFileCache.LookupCache(displayIndex);
            }
            string lineText = lvi?.Text ?? "";
            if (lineText.Length == 0)
                return;

            const int textLeftOffset = 6; // colorBlockWidth + 2, consistent with DrawSubItem
            int clickX = e.X - textLeftOffset;
            if (clickX < 0)
                clickX = 0;
            int textWidth = Math.Max(1, _tailListView.Columns[1].Width - textLeftOffset);
            using (Graphics g = _tailListView.CreateGraphics())
            {
                TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.ExpandTabs | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                int charIndex = GetCharIndexAtPosition(g, lineText, _tailListView.Font, clickX, textWidth, flags);
                int wordStart, wordLen;
                GetWordAt(lineText, charIndex, out wordStart, out wordLen);

                bool isSecondDoubleClickOnSameWord = (_lastDoubleClickItemIndex == virtualIndex
                    && _lastDoubleClickWordStart >= 0
                    && charIndex >= _lastDoubleClickWordStart
                    && charIndex < _lastDoubleClickWordStart + _lastDoubleClickWordLength);

                if (isSecondDoubleClickOnSameWord)
                {
                    // Double-clicking an already selected word: select the whole line
                    _inlineSelectionText = lineText;
                    _inlineSelectionItemIndex = virtualIndex;
                    _inlineSelectionStart = 0;
                    _inlineSelectionLength = lineText.Length;
                    _lastDoubleClickItemIndex = -1;
                    _lastDoubleClickWordStart = -1;
                    _lastDoubleClickWordLength = -1;
                }
                else
                {
                    // First double-click: select the word
                    _inlineSelectionText = wordLen > 0 ? lineText.Substring(wordStart, wordLen) : "";
                    _inlineSelectionItemIndex = virtualIndex;
                    _inlineSelectionStart = wordStart;
                    _inlineSelectionLength = wordLen;
                    _lastDoubleClickItemIndex = virtualIndex;
                    _lastDoubleClickWordStart = wordStart;
                    _lastDoubleClickWordLength = wordLen;
                }
            }

            // When double-clicking selects text, drop the whole-line selection and keep only the in-line selection highlight
            _tailListView.SelectedIndices.Clear();
            _tailListView.Invalidate(_tailListView.GetItemRect(virtualIndex));
        }

        private void ClearInlineSelection()
        {
            if (_inlineSelectionItemIndex < 0)
                return;
            int prev = _inlineSelectionItemIndex;
            _inlineSelectionText = "";
            _inlineSelectionItemIndex = -1;
            _inlineSelectionStart = 0;
            _inlineSelectionLength = 0;
            _lastDoubleClickItemIndex = -1;
            _lastDoubleClickWordStart = -1;
            _lastDoubleClickWordLength = -1;
            if (prev >= 0 && prev < _tailListView.VirtualListSize)
                _tailListView.Invalidate(_tailListView.GetItemRect(prev));
        }

        private static int GetCharIndexAtPosition(Graphics g, string text, Font font, int xOffset, int maxWidth, TextFormatFlags flags)
        {
            if (string.IsNullOrEmpty(text) || xOffset <= 0)
                return 0;
            string truncated = text.Length > 1000 ? text.Substring(0, 1000) : text;
            for (int i = 1; i <= truncated.Length; i++)
            {
                string sub = truncated.Substring(0, i);
                Size sz = TextRenderer.MeasureText(g, sub, font, new Size(int.MaxValue, 100), flags);
                if (sz.Width > xOffset)
                    return i - 1;
            }
            return truncated.Length;
        }

        private static void GetWordAt(string text, int charIndex, out int wordStart, out int wordLength)
        {
            wordStart = 0;
            wordLength = 0;
            if (string.IsNullOrEmpty(text) || charIndex < 0 || charIndex >= text.Length)
                return;
            // Word: letters, digits, underscore
            int start = charIndex;
            while (start > 0 && IsWordChar(text[start - 1]))
                start--;
            int end = charIndex;
            while (end < text.Length && IsWordChar(text[end]))
                end++;
            wordStart = start;
            wordLength = end - start;
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private void _tailListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                if (!string.IsNullOrEmpty(_inlineSelectionText))
                {
                    try
                    {
                        ClipboardHelper.CopyToClipboard(_inlineSelectionText);
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                    catch { }
                    return;
                }
                // Otherwise copy the current line
                ListViewItem current = _tailListView.FocusedItem ?? (_tailListView.SelectedIndices.Count > 0 ? _tailListView.Items[_tailListView.SelectedIndices[0]] : null);
                if (current != null)
                {
                    string rawText = current.SubItems.Count > 1 ? current.SubItems[1].Text : GetRawLineTextByVirtualIndex(current.Index);
                    if (string.IsNullOrEmpty(rawText))
                        return;
                    try
                    {
                        ClipboardHelper.CopyToClipboard(rawText);
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                    catch { }
                }
            }

            if (e.Control && e.KeyCode == Keys.Add)
            {
                e.Handled = true;   // No auto resize
                return;
            }

            if (_tailListView.VirtualListSize > 0)
            {
                if (e.KeyCode == Keys.End)
                {
                    _logFileCache.PrepareCache(Math.Max(_tailListView.VirtualListSize - _logFileCache.Items.Count, 0), _tailListView.VirtualListSize, false);
                    _tailListView.EnsureVisible(_tailListView.VirtualListSize - 1);
                }

                // For some weird reason the cache request for page-down / page-up comes after the item-requests
                if (e.KeyCode == Keys.PageDown)
                {
                    int topIndex = _tailListView.TopItem.Index;
                    _logFileCache.PrepareCache(topIndex + _logFileCache.Items.Count / 2, topIndex + _logFileCache.Items.Count / 2, false);
                }

                if (e.KeyCode == Keys.PageUp)
                {
                    int topIndex = _tailListView.TopItem.Index;
                    _logFileCache.PrepareCache(topIndex - _logFileCache.Items.Count / 2, topIndex + 100, false);
                }
            }

            if (e.KeyCode == Keys.Pause)
            {
                Paused = !Paused;
                if (!Paused)
                {
                    if (_tailListView.VirtualListSize > 0)
                        _tailListView.EnsureVisible(_tailListView.VirtualListSize - 1);
                }
            }
            else
            {
                if (Paused)
                {
                    Paused = false;
                    if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                    {
                        if (_tailListView.VirtualListSize > 0)
                            _tailListView.EnsureVisible(_tailListView.VirtualListSize - 1);
                    }
                }
            }
        }

        private bool ListAtBottom()
        {
            if (Paused)
                return false;

            if (_tailListView.VirtualListSize <= 5)
                return true;

            return IsItemVisible(_tailListView.VirtualListSize - 5);
        }

        private bool IsItemVisible(int index)
        {
            if (_tailListView.VirtualListSize == 0)
                return false;

            ListViewItem topItem = _tailListView.TopItem;
            if (topItem == null || topItem.Index > index)
                return false;

            int heightOfFirstItem = topItem.Bounds.Height;
            if (heightOfFirstItem == 0)
                return false;

            int nVisibleLines = _tailListView.Height / heightOfFirstItem;
            int lastVisibleIndexInDetailsMode = topItem.Index + nVisibleLines;
            return lastVisibleIndexInDetailsMode >= index;
        }

        /// <summary>
        /// When the log content changes and this window isn't active, marks the corresponding tab as having unread changes.
        /// </summary>
        private void MarkTabChangedIfInactive()
        {
            if (MainForm.Instance == null)
                return;
            if (MainForm.Instance.IsMdiChildActive(this))
                return;
            MainForm.Instance.SetMdiTabChanged(this, true);
        }

        private void _tailTimer_Tick(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref _tailTickRunning, 1) == 1)
                return;
            try
            {
                if (!_tailTimer.Enabled)
                    return;
                if (_tailListView == null || _tailListView.IsDisposed)
                    return;
                DateTime nowUtc = DateTime.UtcNow;
                if (_lastTailTickUtc != DateTime.MinValue)
                {
                    _ = nowUtc.Subtract(_lastTailTickUtc).TotalMilliseconds;
                }
                _lastTailTickUtc = nowUtc;

                UpdateFormTitle(false);

                CheckExternalToolResults();

                int previousDisplayLineCount = _tailListView.VirtualListSize;
                bool isFilteringActive = _quickFilter && !string.IsNullOrEmpty(_quickKeyword);
            // When filtering is off, use the display line count directly so non-filtered cases refresh correctly too
            int displayLineCount = isFilteringActive
                ? (_originalVirtualListSize > 0 ? _originalVirtualListSize : previousDisplayLineCount)
                : previousDisplayLineCount;  // Current unfiltered line count
            int actualLineCount = _savedLineNumber + displayLineCount;  // Actual line number in the file
            bool listAtBottom = ListAtBottom();
            bool warningIcon = false;
            int appendedLines = 0;

                if (_displayTabIcon)
                {
                    TabPage parentTab = this.Tag as TabPage;
                    if (parentTab != null && parentTab.ImageIndex == 1)
                        warningIcon = true;
                }
                else
                    warningIcon = true;

            // Read new lines from the file's actual position
                string line = _logTailStream.ReadLine(actualLineCount + 1);
            while (line != null)
            {
                ++displayLineCount;
                ++actualLineCount;
                ++appendedLines;
                _logFileCache.AppendTailCache(line, displayLineCount);  // The cache uses display line numbers
                // Highlighting/tool triggers for new lines are always based on processed text
                string processedLine = _displayTextProcessor.GetProcessedLineText(actualLineCount, line, ReadRawLineTextByLineKey);
                TailKeywordConfig keywordMatch = MatchesKeyword(processedLine, false, false);
                if (keywordMatch != null)
                {
                    if (keywordMatch.LogHitCounter)
                        _loghitCounter++;
                    if (keywordMatch.ExternalToolConfig != null)
                    {
                        CheckExternalToolResults();
                        if (_threadPoolQueue != null)
                            _threadPoolQueue.QueueRequest(ExecuteExternalTool, GenerateExternalTool(keywordMatch.ExternalToolConfig, processedLine, actualLineCount, keywordMatch.Keyword));
                    }
                    if (keywordMatch.AlertHighlight.Value)
                        warningIcon = true;
                }
                line = _logTailStream.ReadLine(actualLineCount + 1);
                }

                if (isFilteringActive && displayLineCount != previousDisplayLineCount)
                    MarkTabChangedIfInactive();

                if (displayLineCount == previousDisplayLineCount)
                {
                if (displayLineCount == 0 && _savedLineNumber == 0 && _logTailStream.Length == 0)
                {
                    // The file is empty or can't be opened; show an error message
                    return;
                }
                else if (displayLineCount == 1 && _savedLineNumber == 0 && _logTailStream.Length == 0)
                {
                    // Check if the open file error has changed
                    if (GetRawLineTextByVirtualIndex(0) == _logTailStream.ReadLine(1))
                        return;
                }
                else if (_logTailStream.ValidLineCount(actualLineCount))
                {
                    return;
                }

                // The file was truncated or reloaded; reset the offset
                displayLineCount = 0;
                _savedLineNumber = 0;
                InvalidateProcessedLineCache();
                MarkTabChangedIfInactive();
                }

                if (_displayTabIcon)
                {
                TabPage parentTab = this.Tag as TabPage;
                if (parentTab != null && parentTab.Parent != null && parentTab.Parent.Visible && !parentTab.Visible)
                {
                    if (warningIcon)
                        parentTab.ImageIndex = 1;
                    else
                        parentTab.ImageIndex = 0;
                }
                }

                if (displayLineCount < _tailListView.VirtualListSize)
                {
                // The file was truncated; reset
                _logFileStream.CheckLogFile(true);
                _logFileCache = new LogFileCache(_logFileCache.Items.Count);
                _logFileCache.LineOffset = 0;  // Reset the line number offset
                _tailListView.TopItem = null;
                _tailListView.VirtualListSize = displayLineCount;
                _savedLineNumber = 0;  // Reset the offset
                _originalVirtualListSize = 0; // Reset the original size
                InvalidateProcessedLineCache();
                if (isFilteringActive)
                {
                    ApplyKeywordFilter();
                }
                _tailListView.Invalidate();
                }
                else
                {
                //_tailListView.VirtualListSize = linecount;
                if (!isFilteringActive)
                {
                    _originalVirtualListSize = 0; // Reset the original size
                    ListViewUtil.SetVirtualListSizeWithoutRefresh(_tailListView, displayLineCount);
                }
                else
                {
                    // If filtering is enabled, update the original size and reapply the filter
                    _originalVirtualListSize = displayLineCount;
                    ApplyKeywordFilter();
                }
                if (listAtBottom && _tailListView.VirtualListSize > 0)
                    _tailListView.EnsureVisible(_tailListView.VirtualListSize - 1);
                }
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _tailTickRunning, 0);
            }
        }

        void _logTailStream_FileChangedEvent(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            if (Interlocked.Exchange(ref _watcherUiRefreshQueued, 1) == 1)
                return;

            BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    _tailTimer_Tick(_tailTimer, EventArgs.Empty);
                }
                finally
                {
                    Interlocked.Exchange(ref _watcherUiRefreshQueued, 0);
                }
            });
        }

        private void CheckExternalToolResults()
        {
            if (_threadPoolQueue != null)
            {
                try
                {
                    _threadPoolQueue.CheckResult();
                }
                catch (ApplicationException ex)
                {
                    MessageBox.Show(this, ex.Message);
                }
            }
        }

        private void switchToModelessToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TailForm newform = new TailForm();
            TailFileConfig tailConfig = new TailFileConfig();
            SaveConfig(tailConfig);
            if (this.MdiParent == null)
            {
                newform.MdiParent = MainForm.Instance;
                newform.ShowInTaskbar = false;
            }
            else
                newform.ShowInTaskbar = true;

            Close();

            newform.Show();
            newform._logFileCache = _logFileCache;
            newform._logFileStream = _logFileStream;
            newform._logTailStream = _logTailStream;
            newform._tailListView.VirtualListSize = _tailListView.VirtualListSize;
            newform.LoadConfig(tailConfig, _configPath);
            newform.BringToFront();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Shift | Keys.F3))
            {
                SearchForm.Instance.SearchAgain(this, false, false);
                return true;
            }
            else if (keyData == Keys.F3)
            {
                SearchForm.Instance.SearchAgain(this, true, false);
                return true;
            }

            if (keyData == (Keys.Alt | Keys.Up))
            {
                SearchForm.Instance.SearchAgain(this, false, true);
                return true;
            }
            else if (keyData == (Keys.Alt | Keys.Down))
            {
                SearchForm.Instance.SearchAgain(this, true, true);
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnResize(EventArgs e)
        {
            bool listAtBottom = ListAtBottom();
            base.OnResize(e);
            _tailListView.Invalidate();
            if (WindowState == FormWindowState.Minimized)
            {
                if (listAtBottom)
                    _formMinimizedAtBottom = listAtBottom;
            }
            else
            {
                if (_formMinimizedAtBottom)
                    listAtBottom = true;
                _formMinimizedAtBottom = false;
            }

            if (listAtBottom)
            {
                if (_tailListView.VirtualListSize > 0)
                    _tailListView.EnsureVisible(_tailListView.VirtualListSize - 1);
            }
            _tailListView.Update();
        }

        private void startServiceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                if (_taskMonitor == null)
                    return;

                SetStatusBar("Starting service...", 1, 5);
                _taskMonitor.StartService();
                for (int i = 1; i <= 10; ++i)
                {
                    System.Threading.Thread.Sleep(500);
                    SetStatusBar("Starting service...", i / 2, 5);

                    if (_taskMonitor.ServiceRunning)
                        break;
                }
                SetStatusBar(null);
            }
            catch (Exception ex)
            {
                SetStatusBar(null);
                MessageBox.Show(this, "Start service failed: " + ex.Message);
            }
        }

        private void stopServiceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                if (_taskMonitor == null)
                    return;

                SetStatusBar("Stopping service...", 1, 5);
                _taskMonitor.StopService();
                for (int i = 1; i <= 10; ++i)
                {
                    System.Threading.Thread.Sleep(500);
                    SetStatusBar("Stopping service...", i / 2, 5);

                    if (!_taskMonitor.ServiceRunning)
                        break;
                }
                SetStatusBar(null);
            }
            catch (Exception ex)
            {
                SetStatusBar(null);
                MessageBox.Show(this, "Stop service failed: " + ex.Message);
            }
        }

        void externalToolMenuItem_Click(object sender, EventArgs e)
        {
            ToolStripItem toolItem = sender as ToolStripItem;
            if (toolItem != null)
            {
                ExternalToolConfig toolConfig = toolItem.Tag as ExternalToolConfig;
                if (toolConfig != null)
                {
                    ExternalTool tool = null;
                    if (_tailListView.FocusedItem != null)
                        tool = GenerateExternalTool(toolConfig, _tailListView.FocusedItem.Text, _tailListView.FocusedItem.Index + 1, string.Empty);
                    else
                        tool = GenerateExternalTool(toolConfig, string.Empty, null, string.Empty);

                    try
                    {
                        tool.Execute();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "External Tool '" + toolConfig.Name + "' failed: " + ex.Message);
                    }
                }
            }
        }

        private ExternalTool GenerateExternalTool(ExternalToolConfig toolConfig, string line, int? lineNumber, string keywordMatch)
        {
            Dictionary<ExternalTool.ParameterName, string> fileParameters = new Dictionary<ExternalTool.ParameterName, string>();
            fileParameters[ExternalTool.ParameterName.FilePath] = _logTailStream != null ? _logTailStream.Name : string.Empty;
            fileParameters[ExternalTool.ParameterName.FileDirectory] = Path.GetDirectoryName(fileParameters[ExternalTool.ParameterName.FilePath]);
            fileParameters[ExternalTool.ParameterName.FileName] = Path.GetFileName(fileParameters[ExternalTool.ParameterName.FilePath]);
            fileParameters[ExternalTool.ParameterName.ServiceName] = _taskMonitor != null ? _taskMonitor.ServiceName : string.Empty;
            fileParameters[ExternalTool.ParameterName.ServiceMachineName] = _taskMonitor != null ? _taskMonitor.ServiceMachineName : string.Empty;
            fileParameters[ExternalTool.ParameterName.SessionDirectory] = _configPath;
            fileParameters[ExternalTool.ParameterName.SessionPath] = MainForm.Instance.CurrenTailConfig;
            fileParameters[ExternalTool.ParameterName.SessionFileName] = Path.GetFileName(fileParameters[ExternalTool.ParameterName.SessionPath]);
            fileParameters[ExternalTool.ParameterName.SessionName] = Path.GetFileNameWithoutExtension(fileParameters[ExternalTool.ParameterName.SessionPath]);
            fileParameters[ExternalTool.ParameterName.ViewName] = _formTitle;
            fileParameters[ExternalTool.ParameterName.ProgramDirectory] = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            fileParameters[ExternalTool.ParameterName.LineText] = line != null ? line : string.Empty;
            fileParameters[ExternalTool.ParameterName.LineNumber] = lineNumber.HasValue ? lineNumber.Value.ToString() : string.Empty;
            fileParameters[ExternalTool.ParameterName.KeywordText] = keywordMatch != null ? keywordMatch : string.Empty;

            ExternalTool tool = new ExternalTool(toolConfig, fileParameters);
            return tool;
        }

        private void ExecuteExternalTool(object state)
        {
            ExternalTool tool = state as ExternalTool;

            try
            {
                if (tool != null)
                    tool.Execute();
            }
            catch (Exception ex)
            {
                throw new ApplicationException("External Tool '" + tool.ToolConfig.Name + "' failed: " + ex.Message, ex);
            }
        }

        private void pauseServiceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                if (_taskMonitor == null)
                    return;

                SetStatusBar("Pause service...", 1, 5);
                _taskMonitor.PauseService();
                SetStatusBar(null);
            }
            catch (Exception ex)
            {
                SetStatusBar(null);
                MessageBox.Show(this, "Pause service failed: " + ex.Message);
            }
        }

        private void resumeServiceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                if (_taskMonitor == null)
                    return;

                SetStatusBar("Continue service...", 1, 5);
                _taskMonitor.ContinueService();
                SetStatusBar(null);
            }
            catch (Exception ex)
            {
                SetStatusBar(null);
                MessageBox.Show(this, "Continue service failed: " + ex.Message);
            }
        }

        private void _contextMenuStrip_Opening(object sender, CancelEventArgs e)
        {
            _contextMenuStrip_Opening(sender, (EventArgs)e);

            // We steal the items from the main menu (we restore them when closing again)
            ToolStripItem[] items = new ToolStripItem[_activeWindowMenuItem.DropDownItems.Count];
            _activeWindowMenuItem.DropDownItems.CopyTo(items, 0);
            _contextMenuStrip.Items.Clear();            // Clear the dummy item
            _contextMenuStrip.Items.AddRange(items);
        }

        private void _contextMenuStrip_Closed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            // Restore the items back to the main menu when closing
            ToolStripItem[] items = new ToolStripItem[_contextMenuStrip.Items.Count];
            _contextMenuStrip.Items.CopyTo(items, 0);
            _activeWindowMenuItem.DropDownItems.AddRange(items);
            _contextMenuStrip.Items.Clear();
            _contextMenuStrip.Items.Add(new ToolStripSeparator());  // Dummy item so menu is shown the next time
        }

        private void _contextMenuStrip_Opening(object sender, EventArgs e)
        {
            bool pauseAndContinue = _taskMonitor != null && _taskMonitor.CanPauseAndContinue;
            RebuildPluginMenu();
            // Non-system logs (plain file logs) don't show the Start Service and Stop Service menu items
            startServiceToolStripMenuItem.Visible = false;
            stopServiceToolStripMenuItem.Visible = false;
            pauseServiceToolStripMenuItem.Visible = pauseAndContinue;
            resumeServiceToolStripMenuItem.Visible = pauseAndContinue;
            pauseWindowToolStripMenuItem.Checked = Paused;
        }

        private void TailForm_Activated(object sender, EventArgs e)
        {
            SearchForm.Instance.ActiveTailForm = this;
            // Once the current tab is activated, clear its unread-changes red dot.
            if (MainForm.Instance != null)
                MainForm.Instance.SetMdiTabChanged(this, false);
            TabPage parentTab = this.Tag as TabPage;
            if (parentTab != null)
                parentTab.ImageIndex = -1;
            _tailListView.Invalidate();
            _tailListView.Update();
            SetStatusBar(null);
            UpdateFormTitle(true);
        }

        private void TailForm_Resize(object sender, EventArgs e)
        {
            if (MdiParent != null)
            {
                if (WindowState == FormWindowState.Maximized)
                {
                    if (_formMaximizedIcon != null)
                        Icon = _formMaximizedIcon;
                }
                else
                {
                    if (_formCustomIcon != null)
                        Icon = _formCustomIcon;
                }
            }
        }

        private void _copyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Copy selected rows to clipboard
            CopySelectionToClipboard();
        }

        private void findToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SearchForm.Instance.StartSearch(this);
        }

        private void findNextToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SearchForm.Instance.Visible)
            {
                SearchForm.Instance.SearchAgain(this, true, false);
            }
        }

        private void configureViewToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TailFileConfig configFile = new TailFileConfig();
            SaveConfig(configFile);
            TailConfigForm configForm = new TailConfigForm(configFile, true);
            switch (configForm.ShowDialog(this))
            {
                case DialogResult.OK:
                    {
                        LoadConfig(configForm.TailFileConfig, _configPath);
                        break;
                    }

                case DialogResult.Retry:
                    {
                        // Apply Config To All
                        LoadConfig(configForm.TailFileConfig, _configPath);
                        configFile = new TailFileConfig();
                        SaveConfig(configFile);
                        TailConfigApplyAllForm configFormApply = new TailConfigApplyAllForm();
                        if (configFormApply.ShowDialog(this) == DialogResult.OK)
                        {
                            // Then we loop through all forms (includes free floating)
                            foreach (Form childForm in Application.OpenForms)
                            {
                                TailForm tailForm = childForm as TailForm;
                                if (tailForm != null && tailForm != this)
                                {
                                    TailFileConfig configFileOther = new TailFileConfig();
                                    tailForm.SaveConfig(configFileOther);
                                    if (configFormApply._checkBoxColors.Checked)
                                    {
                                        configFileOther.FormBackColor = configFile.FormBackColor;
                                        configFileOther.FormTextColor = configFile.FormTextColor;
                                        configFileOther.FormBookmarkBackColor = configFile.FormBookmarkBackColor;
                                        configFileOther.FormBookmarkTextColor = configFile.FormBookmarkTextColor;
                                    }
                                    if (configFormApply._checkBoxFont.Checked)
                                        configFileOther.FontInvariant = configFile.FontInvariant;
                                    if (configFormApply._checkboxKeywords.Checked)
                                        configFileOther.KeywordHighlight = configFile.KeywordHighlight;
                                    if (configFormApply._checkboxTools.Checked)
                                        configFileOther.ExternalTools = configFile.ExternalTools;
                                    tailForm.LoadConfig(configFileOther, _configPath);
                                }
                            }
                        }
                        break;
                    }
            }
        }

        private void gotoPreviousHighlightToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SearchForm.Instance.SearchAgain(this, false, true);
        }

        private void gotoNextHighlightToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SearchForm.Instance.SearchAgain(this, true, true);
        }

        private void pauseWindowToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!Paused)
            {
                Paused = true;
            }
            else
            {
                Paused = false;
                if (_tailListView.VirtualListSize > 0)
                    _tailListView.EnsureVisible(_tailListView.VirtualListSize - 1);
            }
        }

        private void toggleBookmarkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_tailListView.VirtualListSize == 0)
                return;

            ListViewItem focusedItem = _tailListView.FocusedItem;
            if (focusedItem == null)
                return;

            if (!_bookmarks.Contains(focusedItem.Index))
                _bookmarks.Add(focusedItem.Index);
            else
                _bookmarks.Remove(focusedItem.Index);
            _tailListView.Invalidate();
            _tailListView.Update();
        }

        private void clearBookmarksToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_bookmarks.Count > 0)
            {
                _bookmarks.Clear();
                _tailListView.Invalidate();
                _tailListView.Update();
            }
        }

        private void nextBookmarkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_tailListView.VirtualListSize == 0)
                return;

            if (_bookmarks.Count == 0)
                return;

            int startIndex = _tailListView.VirtualListSize - 1;

            ListViewItem focusedItem = _tailListView.FocusedItem;
            if (focusedItem != null)
                startIndex = focusedItem.Index;

            // Search for the bookmark that is larger but closest to the index
            int matchFound = int.MaxValue;
            foreach (int lineNumber in _bookmarks)
            {
                if (lineNumber > startIndex && lineNumber < matchFound)
                    matchFound = lineNumber;
            }
            if (matchFound != int.MaxValue)
            {
                if (matchFound >= _tailListView.VirtualListSize)
                {
                    _bookmarks.Remove(matchFound);
                    _tailListView.Invalidate();
                    _tailListView.Update();
                    return; // something very weird has happened
                }

                _tailListView.SelectedIndices.Clear();
                _tailListView.EnsureVisible(matchFound);
                _tailListView.SelectedIndices.Add(matchFound);   // Set selection after having scrolled to avoid top-index cache miss
                _tailListView.Items[matchFound].Focused = true;
            }
        }

        private void previousBookmarkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_tailListView.VirtualListSize == 0)
                return;

            if (_bookmarks.Count == 0)
                return;

            int startIndex = _tailListView.VirtualListSize - 1;

            ListViewItem focusedItem = _tailListView.FocusedItem;
            if (focusedItem != null)
                startIndex = focusedItem.Index;

            if (startIndex == 0)
                return;

            // Search for the bookmark that is larger but closest to the index
            int matchFound = -1;
            foreach (int lineNumber in _bookmarks)
            {
                if (lineNumber < startIndex && lineNumber > matchFound)
                    matchFound = lineNumber;
            }
            if (matchFound != -1)
            {
                if (matchFound >= _tailListView.VirtualListSize)
                {
                    _bookmarks.Remove(matchFound);
                    _tailListView.Invalidate();
                    _tailListView.Update();
                    return; // something very weird has happened
                }

                _tailListView.SelectedIndices.Clear();
                _tailListView.EnsureVisible(matchFound);
                _tailListView.SelectedIndices.Add(matchFound);   // Set selection after having scrolled to avoid top-index cache miss
                _tailListView.Items[matchFound].Focused = true;
            }
        }

        private void copyAsPathToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CopyPath();
        }

        public void CopyPath()
        {
            string actualFileName = string.Empty;
            if (_logTailStream != null)
            {
                actualFileName = _logTailStream.Name;
                if (string.IsNullOrEmpty(actualFileName))
                    actualFileName = _logTailStream.FilePathAbsolute;
            }
            try
            {
                ClipboardHelper.CopyToClipboard(actualFileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to copy to clipboard, maybe another application is locking the clipboard.\n\n" + ex.Message);
            }
        }

        private void openContainingFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenExplorer();
        }

        public void OpenExplorer()
        {
            try
            {
                string actualFileName = string.Empty;
                if (_logTailStream != null)
                {
                    actualFileName = _logTailStream.Name;
                    if (string.IsNullOrEmpty(actualFileName))
                        actualFileName = _logTailStream.FilePathAbsolute;
                    if (!string.IsNullOrEmpty(actualFileName))
                        Process.Start("explorer.exe", "/select," + actualFileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to open Windows Explorer.\n\n" + ex.Message);
            }
        }

        private void reopenFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TailFileConfig configFileOther = new TailFileConfig();
            SaveConfig(configFileOther);
            LoadConfig(configFileOther, _configPath);
        }

        private void _keywordTextBox_TextChanged(object sender, EventArgs e)
        {
            _quickKeyword = _keywordTextBox.Text;
            InvalidateProcessedLineCache();
            if (_quickFilter)
            {
                ApplyKeywordFilter();
            }
            else if (_quickHighlight)
            {
                _tailListView.Invalidate();
            }
        }

        private void _highlightCheckBox_Click(object sender, EventArgs e)
        {
            _quickHighlight = _highlightCheckBox.Checked;
            _tailListView.Invalidate();
        }

        private void _highlightColorButton_Click(object sender, EventArgs e)
        {
            ColorDialog colorDlg = new ColorDialog();
            colorDlg.Color = _quickHighlightColor;
            if (colorDlg.ShowDialog(this) == DialogResult.OK)
            {
                _quickHighlightColor = colorDlg.Color;
                _highlightColorButton.BackColor = _quickHighlightColor;
                _highlightColorButton.ForeColor = GetContrastColor(_quickHighlightColor);
                if (_quickHighlight)
                {
                    _tailListView.Invalidate();
                }
            }
        }

        private void _filterCheckBox_Click(object sender, EventArgs e)
        {
            _quickFilter = _filterCheckBox.Checked;
            InvalidateProcessedLineCache();
            ApplyKeywordFilter();
        }

        private void _inverseCheckBox_Click(object sender, EventArgs e)
        {
            _quickInverse = _inverseCheckBox.Checked;
            InvalidateProcessedLineCache();
            if (_quickFilter)
            {
                ApplyKeywordFilter();
            }
            else if (_quickHighlight)
            {
                _tailListView.Invalidate();
            }
        }

        private void clearLogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Only applies to file logs (TailForm), not event logs (EventLogForm)
            if (_logTailStream == null)
                return;

            // The clear-screen offset takes the larger of the "accumulated visible lines" and the "reader's actual line number read", so later reads don't rescan old lines from the start of the file.
            int visibleOffset = _savedLineNumber + _tailListView.VirtualListSize;
            int readerOffset = _logTailStream.LastReadLineNumber;
            _savedLineNumber = Math.Max(visibleOffset, readerOffset);

            // Clear the current log's display area
            if (_logFileCache != null)
            {
                _logFileCache.Reset();
            }

            // Recreate the cache and set the line number offset
            int cacheSize = _currentTailConfig != null && _currentTailConfig.FileCacheSize > 0
                ? _currentTailConfig.FileCacheSize
                : 1000;
            _logFileCache = new LogFileCache(cacheSize);
            _logFileCache.LineOffset = _savedLineNumber;  // Set the line number offset
            _logFileCache.LoadingFileEvent += new EventHandler(_logFileCache_LoadingFileEvent);
            _logFileCache.FillCacheEvent += new EventHandler(_logFileCache_FillCacheEvent);

            _tailListView.VirtualListSize = 0;
            _originalVirtualListSize = 0;
            _filteredIndexMap.Clear();
            _filteredVirtualListSize = 0;
            _bookmarks.Clear();
            InvalidateProcessedLineCache();
            _tailListView.Invalidate();
            _tailListView.Update();

            // Don't reread old content; the display area stays empty
            // New lines only appear once the timer detects them
        }
    }

    class LogFileListView : ListView
    {
        public LogFileListView()
        {
        }

        public void EnableDoubleBuffer()
        {
            DoubleBuffered = true;
        }
    }
}
