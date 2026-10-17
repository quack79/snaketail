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
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

using SnakeTail.Storage;

namespace SnakeTail
{
    partial class MainForm : Form
    {
        private static MainForm _instance = null;
        public static MainForm Instance { get { return _instance; } }

        public string CurrenTailConfig { get { return _currenTailConfig != null ? _currenTailConfig : ""; } }

        private TailFileConfig _defaultTailConfig = null;
        private string _currenTailConfig = null;

        private JWC.MruStripMenu _mruMenu;
        private SnakeTailStorage _storage;
        // Records the reason for the most recent session load failure, for the multi-select summary and logging.
        private string _lastSessionLoadErrorReason = null;
        /// <summary>Tracks tabs with "unread changes", used to draw the red dot indicator.</summary>
        private HashSet<TabPage> _changedTabPages = new HashSet<TabPage>();

        public MainForm()
        {
            InitializeComponent();
            Icon = Properties.Resources.SnakeIcon;
            _trayIcon.Icon = Properties.Resources.SnakeIcon;
            _instance = this;

            _MDITabControl.ImageList = new ImageList();
            _MDITabControl.ImageList.ImageSize = new System.Drawing.Size(16, 16);
            _MDITabControl.ImageList.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit;
            _MDITabControl.ImageList.TransparentColor = System.Drawing.Color.Transparent;
            _MDITabControl.ImageList.Images.Add(new Bitmap(Properties.Resources.GreenBulletIcon.ToBitmap()));
            _MDITabControl.ImageList.Images.Add(new Bitmap(Properties.Resources.YellowBulletIcon.ToBitmap()));
            // Enable owner-drawn tabs to overlay the "unread changes" red dot.
            _MDITabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
            _MDITabControl.DrawItem += _MDITabControl_DrawItem;

            // Recently opened files use only xSnakeTail.db, not the registry
            _mruMenu = new JWC.MruStripMenuInline(recentFilesToolStripMenuItem, recentFile1ToolStripMenuItem, new JWC.MruStripMenu.ClickedHandler(OnMruFile), null, false, 10);
            try
            {
                _storage = new SnakeTailStorage(null);
                if (_storage.IsAvailable)
                {
                    List<string> recentFiles = _storage.GetRecentFiles(10);
                    foreach (string file in recentFiles)
                    {
                        _mruMenu.AddFile(file);
                    }
                }
            }
            catch (Exception ex)
            {
                _storage = null;
                MessageBox.Show(this, "Could not initialize the SQLite database; the recently opened files list will not be saved.\n\nError: " + ex.Message,
                    "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            Theme.SetMode(_storage != null ? _storage.LoadThemeMode() : ThemeMode.Light);
            UpdateThemeMenu();
            Theme.Apply(this);
        }

        private void UpdateThemeMenu()
        {
            lightThemeToolStripMenuItem.Checked = Theme.Mode == ThemeMode.Light;
            darkThemeToolStripMenuItem.Checked = Theme.Mode == ThemeMode.Dark;
            systemThemeToolStripMenuItem.Checked = Theme.Mode == ThemeMode.System;
        }

        private void themeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ThemeMode mode = ThemeMode.Light;
            if (sender == darkThemeToolStripMenuItem)
                mode = ThemeMode.Dark;
            else if (sender == systemThemeToolStripMenuItem)
                mode = ThemeMode.System;

            Theme.SetMode(mode);
            _storage?.SaveThemeMode(mode);
            UpdateThemeMenu();
        }

        private void UpdateTitle()
        {
            if (IsDisposed || Disposing)
                return;
            try
            {
                Text = Application.ProductName;
                if (_currenTailConfig != null)
                    Text += " - " + Path.GetFileNameWithoutExtension(_currenTailConfig);
            }
            catch
            {
                // Ignore errors updating the title while the window is closing
            }
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            Program.StartSingleInstancePipeServer();
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                int filesOpened = 0;
                for (int i = 1; i < args.Length; ++i)
                {
                    if (args[1].EndsWith(".xml", StringComparison.CurrentCultureIgnoreCase))
                    {
                        if (LoadSession(args[1]))
                            ++filesOpened;
                    }
                    else
                    {
                        filesOpened += OpenFileSelection(new string[] { args[i] });
                    }
                    if (filesOpened == 0 && i >= 2)
                        break;  // Stop attempting to open all arguements if the first two fails
                }
            }
            else
            {
                // If there are no command-line arguments, load the last session from xSnakeTail.db
                try
                {
                    LoadSessionFromDb();
                }
                catch
                {
                    // Ignore load errors and continue starting normally
                }
            }
        }

        public void SetStatusBar(string text, int progressValue, int progressMax)
        {
            _statusProgressBar.Maximum = progressMax;
            _statusProgressBar.Value = progressValue;
            if (progressMax == 0 && progressValue == 0)
                _statusProgressBar.Visible = false;
            else
                _statusProgressBar.Visible = true;

            if (text == null)
                text = "Ready";

            if (_statusTextBar.Text != text || progressMax != 0 || progressValue != 0)
            {
                _statusTextBar.Text = text;
                _statusStrip.Invalidate();
                _statusStrip.Update();
            }
        }

        private void MainForm_MdiChildActivate(object sender, EventArgs e)
        {
            closeItemToolStripMenuItem.Enabled = this.ActiveMdiChild != null;

            // If no any child form, hide tabControl
            if (this.ActiveMdiChild == null)
            {
                if (_MDITabControl.TabCount==0)
                    _MDITabControl.Visible = false;
            }
            else
            {
                // If child form is new and no has tabPage, create new tabPage
                if (this.ActiveMdiChild.Tag == null)
                {
                    // Add a tabPage to tabControl with child form caption
                    AddMdiChildTab(this.ActiveMdiChild);

                    if (MdiChildren.Length > 1 && _MDITabControl.Visible == false)
                        return;

                    // Child form always maximized
                    this.ActiveMdiChild.WindowState = FormWindowState.Maximized;

                    _MDITabControl.SelectedTab = this.ActiveMdiChild.Tag as TabPage;
                }
                else
                {
                    if (_MDITabControl.Visible == false)
                        return;

                    TabPage tp = this.ActiveMdiChild.Tag as TabPage;
                    if (tp != null)
                    {
                        // Child form always maximized
                        this.ActiveMdiChild.WindowState = FormWindowState.Maximized;

                        _MDITabControl.SelectedTab = tp;
                    }
                }

                if (!_MDITabControl.Visible)
                    _MDITabControl.Visible = true;
            }
        }

        void AddMdiChildTab(Form mdiChild)
        {
            TabPage tp = new TabPage(mdiChild.Text);
            tp.Tag = mdiChild;
            tp.ImageIndex = -1;
            tp.Parent = _MDITabControl;
            //AddOwnedForm(mdiChild);
            mdiChild.Tag = tp;
            mdiChild.FormClosed += new FormClosedEventHandler(ActiveMdiChild_FormClosed);
            mdiChild.SizeChanged += new EventHandler(ActiveMdiChild_SizeChanged);
            mdiChild.Shown += new EventHandler(ActiveMdiChild_Shown);
        }

        void ActiveMdiChild_Shown(object sender, EventArgs e)
        {
            // Fix the icon when starting MDI child in maximized state
            if ((sender as Form).WindowState == FormWindowState.Maximized)
            {
                ActivateMdiChild(null);
                ActivateMdiChild((sender as Form));
            }
        }

        void ActiveMdiChild_SizeChanged(object sender, EventArgs e)
        {
            // Disable tab-mode if the active MDI child changes WindowState
            if (this.ActiveMdiChild == sender && this.ActiveMdiChild.WindowState != FormWindowState.Maximized)
            {
                // Check if we are about to open / close a window
                if (MdiChildren.Length == _MDITabControl.TabCount)
                {
                    if (_MDITabControl.SelectedTab == null || this.ActiveMdiChild == _MDITabControl.SelectedTab.Tag)
                    {
                        _MDITabControl.Visible = false;
                        SetStatusBar(null, 0, 0);
                    }
                }
            }
        }

        private void ActiveMdiChild_FormClosed(object sender, FormClosedEventArgs e)
        {
            TabPage tabPage = (sender as Form).Tag as TabPage;
            if (tabPage != null)
            {
                lock (_changedTabPages)
                {
                    _changedTabPages.Remove(tabPage);
                }
                tabPage.Dispose();
            }
        }

        /// <summary>
        /// Sets the "unread changes" flag on the tab of the given MDI child form.
        /// </summary>
        public void SetMdiTabChanged(Form mdiChild, bool changed)
        {
            if (mdiChild == null || mdiChild.IsDisposed || IsDisposed)
                return;
            TabPage tabPage = mdiChild.Tag as TabPage;
            if (tabPage == null || tabPage.IsDisposed)
                return;

            bool stateChanged = false;
            lock (_changedTabPages)
            {
                if (changed)
                    stateChanged = _changedTabPages.Add(tabPage);
                else
                    stateChanged = _changedTabPages.Remove(tabPage);
            }
            if (stateChanged && _MDITabControl != null && !_MDITabControl.IsDisposed)
                _MDITabControl.Invalidate();
        }

        /// <summary>
        /// Determines whether the given MDI child form is the active tab.
        /// </summary>
        public bool IsMdiChildActive(Form mdiChild)
        {
            return mdiChild != null && !mdiChild.IsDisposed && ActiveMdiChild == mdiChild;
        }

        /// <summary>
        /// Queries whether a tab has unread changes.
        /// </summary>
        private bool IsTabChanged(TabPage tabPage)
        {
            lock (_changedTabPages)
            {
                return _changedTabPages.Contains(tabPage);
            }
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog fileDialog = new OpenFileDialog();
            fileDialog.Multiselect = true;
            fileDialog.Title = "Open Log File";
            fileDialog.Filter = "Default Filter|*.txt;*.text;*.log*;*.xlog|Log Files|*.log*;*.xlog|Text Files|*.txt;*.text|All Files|*.*";
            if (fileDialog.ShowDialog(this) != DialogResult.OK)
                return;

            OpenFileSelection(fileDialog.FileNames);
        }

        private void openWildcardMonitorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            EnsureDefaultTailConfig();

            // Reuse the default config and force "wildcard path monitoring" on.
            TailFileConfig tailConfig = CloneTailFileConfig(_defaultTailConfig);
            tailConfig.FileCheckPattern = true;

            TailConfigForm configForm = new TailConfigForm(tailConfig, true);
            configForm.Text = "Open Wildcard Monitor";
            DialogResult result = configForm.ShowDialog(this);
            if (result != DialogResult.OK && result != DialogResult.Retry)
                return;

            TailFileConfig selectedConfig = configForm.TailFileConfig;
            if (selectedConfig == null || string.IsNullOrWhiteSpace(selectedConfig.FilePath))
            {
                MessageBox.Show(this, "Enter the path to monitor (wildcards allowed, e.g. *.log).", "Open Wildcard Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // The menu entry always means "wildcard match monitoring within a directory".
            selectedConfig.FileCheckPattern = true;

            string configPath = string.Empty;
            try
            {
                string directoryPath = Path.GetDirectoryName(selectedConfig.FilePath);
                if (string.IsNullOrEmpty(directoryPath))
                    configPath = Directory.GetCurrentDirectory();
                else
                    configPath = directoryPath;
            }
            catch
            {
                configPath = string.Empty;
            }

            TailForm mdiForm = new TailForm();
            try
            {
                mdiForm.LoadConfig(selectedConfig, configPath);
            }
            catch (Exception ex)
            {
                string reason = BuildExceptionMessage(ex);
                AppLog.AppendDaily(AppLog.LevelErr, string.Format("Failed to open wildcard monitor: Path={0}, Error={1}", selectedConfig.FilePath, reason));
                MessageBox.Show(this, "Could not open the wildcard monitor configuration:\n\n" + selectedConfig.FilePath + "\n\n" + reason, "Open Wildcard Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (!mdiForm.IsDisposed)
                    mdiForm.Close();
                return;
            }

            if (mdiForm.IsDisposed)
            {
                string reason = string.IsNullOrEmpty(mdiForm.LastLoadFailureReason) ? "The window was closed during loading (no detailed reason returned)" : mdiForm.LastLoadFailureReason;
                AppLog.AppendDaily(AppLog.LevelWarn, string.Format("Opening wildcard monitor did not complete: Path={0}, Reason={1}", selectedConfig.FilePath, reason));
                MessageBox.Show(this, "Opening the wildcard monitor did not complete:\n\n" + selectedConfig.FilePath + "\n\n" + reason, "Open Wildcard Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            mdiForm.MdiParent = this;
            mdiForm.Show();

            string mruPath = selectedConfig.FilePath;
            try
            {
                mruPath = Path.GetFullPath(selectedConfig.FilePath);
            }
            catch
            {
            }

            if (_mruMenu != null)
                _mruMenu.AddFile(mruPath);
            if (_storage != null)
                _storage.AddFile(mruPath);
        }


        private void OnMruFile(int number, String filename)
        {
            bool openedFile = false;
            if (filename.EndsWith(".xml", StringComparison.CurrentCultureIgnoreCase))
                openedFile = LoadSession(filename);
            else
                openedFile = OpenFileSelection(new string[] { filename }) == 1;

            if (!openedFile)
            {
                MessageBox.Show(this, "The file '" + filename + "'cannot be opened and will be removed from the Recent list(s)", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (_mruMenu != null)
                {
                    _mruMenu.RemoveFile(number);
                }
                if (_storage != null)
                {
                    _storage.RemoveFile(filename);
                }
            }
        }

        public int OpenFileSelection(string[] filenames)
        {
            EnsureDefaultTailConfig();

            int filesOpened = 0;
            List<string> failedFiles = new List<string>();
            foreach (string filename in filenames)
            {
                string configPath = "";
                try
                {
                    if (string.IsNullOrEmpty(Path.GetDirectoryName(filename)))
                        configPath = Directory.GetCurrentDirectory();
                }
                catch
                {
                }

                TailForm mdiForm = new TailForm();
                try
                {
                    // Each file gets its own config copy, so multiple tabs sharing one object can't get crossed.
                    TailFileConfig tailConfig = CloneTailFileConfig(_defaultTailConfig);
                    tailConfig.FilePath = filename;
                    // Auto-detect encoding when opening a file
                    if (File.Exists(filename))
                    {
                        Encoding detectedEncoding = EncodingHelper.DetectFileEncoding(filename);
                        if (detectedEncoding != null)
                        {
                            tailConfig.EnumFileEncoding = detectedEncoding;
                        }
                    }
                    mdiForm.LoadConfig(tailConfig, configPath);
                }
                catch (Exception ex)
                {
                    string reason = BuildExceptionMessage(ex);
                    failedFiles.Add(string.Format("{0}\n  Reason: {1}", filename, reason));
                    AppLog.AppendDaily(AppLog.LevelErr, string.Format("Batch file open failed: File={0}, Error={1}", filename, reason));
                    if (!mdiForm.IsDisposed)
                        mdiForm.Close();
                    continue;
                }

                if (mdiForm.IsDisposed)
                {
                    string reason = string.IsNullOrEmpty(mdiForm.LastLoadFailureReason) ? "The window was closed during loading (no detailed reason returned)" : mdiForm.LastLoadFailureReason;
                    failedFiles.Add(string.Format("{0}\n  Reason: {1}", filename, reason));
                    AppLog.AppendDaily(AppLog.LevelWarn, string.Format("Batch file open did not complete: File={0}, Reason={1}", filename, reason));
                    continue;
                }

                try
                {
                    string fullPath = filename;
                    if (string.IsNullOrEmpty(configPath))
                    {
                        new DirectoryInfo(Path.GetDirectoryName(filename));
                    }
                    else
                    {
                        fullPath = Path.Combine(configPath, filename);
                    }

                    // Add to the menu
                    if (_mruMenu != null)
                    {
                        _mruMenu.AddFile(fullPath);
                    }

                    // Save to SQLite (if available)
                    if (_storage != null)
                    {
                        _storage.AddFile(fullPath);
                    }
                }
                catch
                {
                }

                mdiForm.MdiParent = this;
                mdiForm.Show();
                ++filesOpened;
                Application.DoEvents();
            }

            if (failedFiles.Count > 0)
            {
                string summary = string.Format("{0} file(s) selected: {1} succeeded, {2} failed.", filenames.Length, filesOpened, failedFiles.Count);
                string detail = string.Join("\n\n", failedFiles.ToArray());
                MessageBox.Show(this, summary + "\n\nFailure details:\n\n" + detail, "Batch Open Results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return filesOpened;
        }

        /// <summary>
        /// Ensures the default Tail config is initialized so the open logic can be reused.
        /// </summary>
        private void EnsureDefaultTailConfig()
        {
            if (_defaultTailConfig != null)
                return;

            TailConfig tailConfig = _storage?.LoadDefaultSession();
            if (tailConfig != null && tailConfig.TailFiles.Count > 0)
            {
                _defaultTailConfig = tailConfig.TailFiles[0];
                _defaultTailConfig.Title = null;
            }
            else
            {
                _defaultTailConfig = new TailFileConfig();
            }
        }

        /// <summary>
        /// Clones a Tail config so multiple windows don't share a mutable object.
        /// </summary>
        private static TailFileConfig CloneTailFileConfig(TailFileConfig source)
        {
            if (source == null)
                return new TailFileConfig();

            TailFileConfig clone = new TailFileConfig
            {
                FilePath = source.FilePath,
                FileEncoding = source.FileEncoding,
                FileCacheSize = source.FileCacheSize,
                FileCheckInterval = source.FileCheckInterval,
                FileChangeCheckInterval = source.FileChangeCheckInterval,
                FileCheckPattern = source.FileCheckPattern,
                TitleMatchFilename = source.TitleMatchFilename,
                TextColor = source.TextColor,
                BackColor = source.BackColor,
                Font = source.Font,
                FontInvariant = source.FontInvariant,
                BookmarkTextColor = source.BookmarkTextColor,
                BookmarkBackColor = source.BookmarkBackColor,
                Modeless = source.Modeless,
                Title = source.Title,
                WindowState = source.WindowState,
                WindowSize = source.WindowSize,
                WindowPosition = source.WindowPosition,
                ServiceName = source.ServiceName,
                ServiceMachineName = source.ServiceMachineName,
                IconFile = source.IconFile,
                DisplayTabIcon = source.DisplayTabIcon,
                ColumnFilterActive = source.ColumnFilterActive,
                QuickKeyword = source.QuickKeyword,
                QuickHighlight = source.QuickHighlight,
                QuickHighlightColor = source.QuickHighlightColor,
                QuickFilter = source.QuickFilter,
                QuickInverse = source.QuickInverse
            };

            // Copy the list so collection references aren't shared across windows.
            if (source.ColumnFilters != null)
            {
                clone.ColumnFilters = new List<List<string>>(source.ColumnFilters.Count);
                foreach (List<string> filters in source.ColumnFilters)
                    clone.ColumnFilters.Add(filters != null ? new List<string>(filters) : null);
            }

            if (source.KeywordHighlight != null)
            {
                clone.KeywordHighlight = new List<TailKeywordConfig>(source.KeywordHighlight.Count);
                foreach (TailKeywordConfig keyword in source.KeywordHighlight)
                    clone.KeywordHighlight.Add(keyword != null ? CloneKeyword(keyword) : null);
            }

            if (source.ExternalTools != null)
            {
                clone.ExternalTools = new List<ExternalToolConfig>(source.ExternalTools.Count);
                foreach (ExternalToolConfig tool in source.ExternalTools)
                    clone.ExternalTools.Add(tool != null ? CloneExternalTool(tool) : null);
            }

            if (source.EnabledDisplayPlugins != null)
                clone.EnabledDisplayPlugins = new List<string>(source.EnabledDisplayPlugins);

            return clone;
        }

        /// <summary>
        /// Clones a keyword config, isolating runtime fields.
        /// </summary>
        private static TailKeywordConfig CloneKeyword(TailKeywordConfig source)
        {
            return new TailKeywordConfig
            {
                Keyword = source.Keyword,
                MatchCaseSensitive = source.MatchCaseSensitive,
                MatchRegularExpression = source.MatchRegularExpression,
                LogHitCounter = source.LogHitCounter,
                ExternalToolName = source.ExternalToolName,
                TextColoring = source.TextColoring,
                AlertHighlight = source.AlertHighlight,
                TextColor = source.TextColor,
                BackColor = source.BackColor
            };
        }

        /// <summary>
        /// Clones an external tool config so the same instance isn't shared.
        /// </summary>
        private static ExternalToolConfig CloneExternalTool(ExternalToolConfig source)
        {
            return new ExternalToolConfig
            {
                Name = source.Name,
                Command = source.Command,
                Arguments = source.Arguments,
                InitialDirectory = source.InitialDirectory,
                ShortcutKey = source.ShortcutKey,
                RunAsAdmin = source.RunAsAdmin,
                HideWindow = source.HideWindow
            };
        }

        /// <summary>
        /// If the path is already open in a tab (TailForm) of the current window, activates that tab; otherwise opens it in a new tab.
        /// Used for single instance: called when a second process passes a path through the pipe.
        /// </summary>
        public void OpenFileOrActivateTab(string[] filenames)
        {
            if (filenames == null || filenames.Length == 0)
                return;
            var toOpen = new List<string>();
            foreach (string rawPath in filenames)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                    continue;
                string path;
                try
                {
                    path = Path.GetFullPath(rawPath.Trim());
                }
                catch
                {
                    toOpen.Add(rawPath.Trim());
                    continue;
                }
                if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    LoadSession(path);
                    continue;
                }
                bool found = false;
                foreach (Form child in MdiChildren)
                {
                    TailForm tailForm = child as TailForm;
                    if (tailForm == null || string.IsNullOrEmpty(tailForm.CurrentFilePathAbsolute))
                        continue;
                    if (string.Equals(tailForm.CurrentFilePathAbsolute, path, StringComparison.OrdinalIgnoreCase))
                    {
                        TabPage tp = child.Tag as TabPage;
                        if (tp != null && _MDITabControl.TabPages.Contains(tp))
                        {
                            _MDITabControl.SelectedTab = tp;
                            ActivateMdiChild(child);
                        }
                        else
                            ActivateMdiChild(child);
                        found = true;
                        break;
                    }
                }
                if (!found)
                    toOpen.Add(path);
            }
            if (toOpen.Count > 0)
                OpenFileSelection(toOpen.ToArray());
        }

        /// <summary>Called by the single-instance pipe thread: brings the main window to the front and opens or activates the given files.</summary>
        public void BringToFrontAndOpenOrActivateTab(string[] filenames)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string[]>(BringToFrontAndOpenOrActivateTab), new object[] { filenames });
                return;
            }
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
            if (filenames != null && filenames.Length > 0)
                OpenFileOrActivateTab(filenames);
        }

        private void openEventLogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenEventLogDialog openEventLogDlg = new OpenEventLogDialog();
            if (openEventLogDlg.ShowDialog(this) != DialogResult.OK)
                return;

            EventLogForm mdiForm = new EventLogForm();
            mdiForm.MdiParent = this;
            mdiForm.LoadFile(openEventLogDlg.EventLogFile);
            if (!mdiForm.IsDisposed)
                mdiForm.Show();
        }

        private void _MDITabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            if ((_MDITabControl.SelectedTab != null) && (_MDITabControl.SelectedTab.Tag != null))
            {
                // Switching to the tab counts as read; clear the red dot.
                SetMdiTabChanged(_MDITabControl.SelectedTab.Tag as Form, false);
                SuspendLayout();
                (_MDITabControl.SelectedTab.Tag as Form).SuspendLayout();
                Form activeMdiChild = this.ActiveMdiChild;
                if (activeMdiChild != null)
                    activeMdiChild.SuspendLayout();
                // Minimize flicker when switching between tabs, by changing to minimized state first
                if ((_MDITabControl.SelectedTab.Tag as Form).WindowState != FormWindowState.Maximized)
                    (_MDITabControl.SelectedTab.Tag as Form).WindowState = FormWindowState.Minimized;
                (_MDITabControl.SelectedTab.Tag as Form).Select();
                if (activeMdiChild != null && !activeMdiChild.IsDisposed)
                    activeMdiChild.ResumeLayout();
                (_MDITabControl.SelectedTab.Tag as Form).ResumeLayout();
                ResumeLayout();
                (_MDITabControl.SelectedTab.Tag as Form).Refresh();
            }
        }

        private void _MDITabControl_MouseClick(object sender, MouseEventArgs e)
        {
            var tabControl = sender as TabControl;
            TabPage tabPageCurrent = GetTabPageFromLocation(tabControl, e.Location);

            if (e.Button == MouseButtons.Middle)
            {
                if (tabPageCurrent != null)
                    (tabPageCurrent.Tag as Form).Close();
            }
            else if (e.Button == MouseButtons.Right)
            {
                var enablePath = tabPageCurrent.Tag is TailForm;
                _openFolderTabContext.Visible = enablePath;
                _copyPathTabContext.Visible = enablePath;
                _separatorTabContext.Visible = enablePath;

                _tabContextMenuStrip.Show(sender as TabControl, e.Location);
            }
        }

        private void _MDITabControl_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _MDITabControl.TabPages.Count)
                return;

            TabPage tabPage = _MDITabControl.TabPages[e.Index];
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool changed = !selected && IsTabChanged(tabPage);
            Rectangle bounds = e.Bounds;
            Color backgroundColor = Theme.Map(selected ? SystemColors.ControlLightLight : SystemColors.Control);

            using (SolidBrush backgroundBrush = new SolidBrush(backgroundColor))
            {
                e.Graphics.FillRectangle(backgroundBrush, bounds);
            }

            Rectangle contentRect = Rectangle.Inflate(bounds, -4, -2);
            int x = contentRect.X;
            if (_MDITabControl.ImageList != null && tabPage.ImageIndex >= 0 && tabPage.ImageIndex < _MDITabControl.ImageList.Images.Count)
            {
                Image tabImage = _MDITabControl.ImageList.Images[tabPage.ImageIndex];
                int imageY = contentRect.Y + (contentRect.Height - tabImage.Height) / 2;
                e.Graphics.DrawImage(tabImage, x, imageY, tabImage.Width, tabImage.Height);
                x += tabImage.Width + 4;
            }

            int textRightPadding = changed ? 12 : 2;
            Rectangle textRect = new Rectangle(
                x,
                contentRect.Y,
                Math.Max(0, contentRect.Right - x - textRightPadding),
                contentRect.Height);

            TextRenderer.DrawText(
                e.Graphics,
                tabPage.Text,
                _MDITabControl.Font,
                textRect,
                Theme.Map(SystemColors.ControlText),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (changed)
            {
                // The red dot means "file changes not yet viewed".
                int dotSize = 8;
                int dotX = bounds.Right - dotSize - 6;
                int dotY = bounds.Top + (bounds.Height - dotSize) / 2;
                Rectangle dotRect = new Rectangle(dotX, dotY, dotSize, dotSize);
                using (SolidBrush redDotBrush = new SolidBrush(Color.FromArgb(220, 53, 69)))
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.FillEllipse(redDotBrush, dotRect);
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
                }
            }
        }

        private void cascadeWindowsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            LayoutMdi(MdiLayout.Cascade);
        }

        private void tileWindowsHorizontallyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            LayoutMdi(MdiLayout.TileHorizontal);
        }

        private void tileWindowsVerticallyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            LayoutMdi(MdiLayout.TileVertical);
        }

        private void minimizeAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Form activeChild = ActiveMdiChild;
            foreach (Form childForm in MdiChildren)
            {
                if (childForm.WindowState != FormWindowState.Minimized)
                    childForm.WindowState = FormWindowState.Minimized;
            }
            if (activeChild != null && activeChild != ActiveMdiChild)
                activeChild.Select();
        }

        private void closeAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            _MDITabControl.Visible = false;
            FormCollection forms = Application.OpenForms;
            for (int i = forms.Count - 1; i >= 0; i--)
            {
                ITailForm tailForm = forms[i] as ITailForm;
                if (tailForm != null)
                    tailForm.TailWindow.Close();
            }
            if (SearchForm.Instance.Visible)
                SearchForm.Instance.Close();
            _currenTailConfig = null;
            UpdateTitle();
        }

        private void enableTabsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_MDITabControl.Visible)
            {
                _MDITabControl.Visible = false;
            }
            else
            if (this.ActiveMdiChild != null)
            {
                this.ActiveMdiChild.WindowState = FormWindowState.Maximized;
                _MDITabControl.Visible = true;
                _MDITabControl.SelectedTab = this.ActiveMdiChild.Tag as TabPage;
            }
        }

        private void saveSessionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog();
            if (!String.IsNullOrEmpty(_currenTailConfig))
            {
                saveFileDialog.FileName = Path.GetFileName(_currenTailConfig);
                saveFileDialog.InitialDirectory = Path.GetDirectoryName(_currenTailConfig);
            }
            saveFileDialog.Filter = "Xml files (*.xml)|*.xml|All files (*.*)|*.*";
            if (saveFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                SaveSession(saveFileDialog.FileName);
            }
        }

        private void loadSessionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            // Allow selecting multiple session files at once and loading them in order.
            openFileDialog.Multiselect = true;
            openFileDialog.Filter = "Xml files (*.xml)|*.xml|All files (*.*)|*.*";
            if (openFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                int loadedCount = 0;
                List<string> failedSessions = new List<string>();
                foreach (string sessionFile in openFileDialog.FileNames)
                {
                    if (LoadSession(sessionFile))
                    {
                        ++loadedCount;
                    }
                    else
                    {
                        string reason = string.IsNullOrEmpty(_lastSessionLoadErrorReason) ? "Session load failed (no detailed reason returned)" : _lastSessionLoadErrorReason;
                        failedSessions.Add(string.Format("{0}\n  Reason: {1}", sessionFile, reason));
                        AppLog.AppendDaily(AppLog.LevelErr, string.Format("Batch session open failed: Session={0}, Error={1}", sessionFile, reason));
                    }
                }

                if (failedSessions.Count > 0)
                {
                    string summary = string.Format("{0} session(s) selected: {1} succeeded, {2} failed.", openFileDialog.FileNames.Length, loadedCount, failedSessions.Count);
                    string detail = string.Join("\n\n", failedSessions.ToArray());
                    MessageBox.Show(this, summary + "\n\nFailure details:\n\n" + detail, "Batch Open Session Results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void SaveSession(string filepath)
        {
            TailConfig tailConfig = new TailConfig();
            if (_MDITabControl != null && _MDITabControl.Visible)
                tailConfig.SelectedTab = _MDITabControl.SelectedIndex;
            else
                tailConfig.SelectedTab = -1;
            tailConfig.WindowSize = Size;
            tailConfig.WindowPosition = DesktopLocation;
            tailConfig.MinimizedToTray = _trayIcon != null && _trayIcon.Visible;
            tailConfig.AlwaysOnTop = TopMost;

            List<Form> childForms = new List<Form>();

            // We first loop through the tabpages to store in proper TabPage order
            if (_MDITabControl != null && !_MDITabControl.IsDisposed)
            {
                foreach (TabPage tagPage in _MDITabControl.TabPages)
                {
                    Form tailForm = tagPage.Tag as Form;
                    if (tailForm != null)
                        childForms.Add(tailForm);
                }
            }

            // Then we loop through all forms (includes free floating)
            foreach (Form childForm in Application.OpenForms)
            {
                if (childForms.IndexOf(childForm) == -1)
                    childForms.Add(childForm);
            }

            // Save all forms and store in proper order
            foreach (Form childForm in childForms)
            {
                ITailForm tailForm = childForm as ITailForm;
                if (tailForm != null)
                {
                    TailFileConfig tailFile = new TailFileConfig();
                    tailForm.SaveConfig(tailFile);
                    tailConfig.TailFiles.Add(tailFile);
                }
            }

            SaveConfig(tailConfig, filepath);

            if (String.IsNullOrEmpty(_currenTailConfig))
            {
                if (_mruMenu != null)
                {
                    _mruMenu.AddFile(filepath);
                }
                if (_storage != null)
                {
                    _storage.AddFile(filepath);
                }
            }
            else if (_currenTailConfig != filepath)
            {
                if (_mruMenu != null)
                {
                    _mruMenu.RenameFile(_currenTailConfig, filepath);
                }
                if (_storage != null)
                {
                    _storage.RenameFile(_currenTailConfig, filepath);
                }
            }

            _currenTailConfig = filepath;

            UpdateTitle();
        }

        /// <summary>
        /// Saves the session to the given XML file (only used for "Save As")
        /// </summary>
        public void SaveConfig(TailConfig tailConfig, string filepath)
        {
            if (string.IsNullOrEmpty(filepath))
                return;

            XmlSerializer serializer = new XmlSerializer(typeof(TailConfig));
            try
            {
                using (XmlTextWriter writer = new XmlTextWriter(filepath, Encoding.UTF8))
                {
                    writer.Formatting = Formatting.Indented;
                    XmlSerializerNamespaces xmlnsEmpty = new XmlSerializerNamespaces();
                    xmlnsEmpty.Add("", "");
                    serializer.Serialize(writer, tailConfig, xmlnsEmpty);
                }

                _defaultTailConfig = null;
            }
            catch (System.Exception ex)
            {
                string errorMsg = ex.Message;
                while (ex.InnerException != null)
                {
                    ex = ex.InnerException;
                    errorMsg += "\n" + ex.Message;
                }
                MessageBox.Show(this, "Failed to save session xml file, please ensure it is valid location:\n\n   " + filepath + "\n\n" + errorMsg, "", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private TailConfig LoadSessionFile(string filepath)
        {
            _lastSessionLoadErrorReason = null;
            TailConfig tailConfig = null;
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(TailConfig));
                using (XmlTextReader reader = new XmlTextReader(filepath))
                {
                    _currenTailConfig = new Uri(reader.BaseURI).LocalPath;
                    tailConfig = serializer.Deserialize(reader) as TailConfig;
                }
                return tailConfig;
            }
            catch (Exception ex)
            {
                string errorMsg = BuildExceptionMessage(ex);
                _lastSessionLoadErrorReason = errorMsg;
                AppLog.AppendDaily(AppLog.LevelErr, string.Format("Failed to open session file: File={0}, Error={1}", filepath, errorMsg));
                MessageBox.Show(this, "Failed to open session xml file, please ensure it is valid file:\n\n   " + filepath + "\n\n" + errorMsg, "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        /// <summary>
        /// Concatenates the exception chain so the failure reason is precise and traceable.
        /// </summary>
        private static string BuildExceptionMessage(Exception ex)
        {
            if (ex == null)
                return "Unknown exception (null)";

            List<string> errors = new List<string>();
            Exception current = ex;
            while (current != null)
            {
                errors.Add(string.Format("{0}: {1}", current.GetType().FullName, current.Message));
                current = current.InnerException;
            }
            return string.Join("\n", errors.ToArray());
        }

        private bool LoadSession(string filepath, bool addToMru = true)
        {
            TailConfig tailConfig = LoadSessionFile(filepath);
            if (tailConfig == null)
                return false;

            if (addToMru)
            {
                if (_mruMenu != null)
                    _mruMenu.AddFile(filepath);
                if (_storage != null)
                    _storage.AddFile(filepath);
            }

            return LoadSessionWithConfig(tailConfig, Path.GetDirectoryName(filepath));
        }

        /// <summary>
        /// Loads the last session from xSnakeTail.db
        /// </summary>
        private void LoadSessionFromDb()
        {
            TailConfig tailConfig = _storage?.LoadDefaultSession();
            if (tailConfig == null)
                return;

            // At startup prefer config under the working directory; fall back to the exe directory if it doesn't exist.
            string basePath = ResolveStartupConfigBasePath();
            LoadSessionWithConfig(tailConfig, basePath);
        }

        /// <summary>
        /// Resolves the startup config base path: working directory first, then the exe directory.
        /// </summary>
        private static string ResolveStartupConfigBasePath()
        {
            // First check whether the current working directory contains config.
            string currentDirectory = Directory.GetCurrentDirectory();
            string currentConfigPath = Path.Combine(currentDirectory, "config");
            if (Directory.Exists(currentConfigPath))
                return currentDirectory;

            // If the working directory lacks it, check whether the exe's directory contains config.
            string executableDirectory = Path.GetDirectoryName(Application.ExecutablePath) ?? string.Empty;
            string executableConfigPath = Path.Combine(executableDirectory, "config");
            if (!string.IsNullOrEmpty(executableDirectory) && Directory.Exists(executableConfigPath))
                return executableDirectory;

            // If neither has it, keep the working directory behavior to avoid a sudden path change.
            return currentDirectory;
        }

        /// <summary>
        /// Saves the session to xSnakeTail.db
        /// </summary>
        private void SaveSessionToDb()
        {
            TailConfig tailConfig = BuildCurrentTailConfig();
            if (tailConfig == null || tailConfig.TailFiles.Count == 0)
                return;

            _storage?.SaveDefaultSession(tailConfig);
        }

        private TailConfig BuildCurrentTailConfig()
        {
            TailConfig tailConfig = new TailConfig();
            if (_MDITabControl != null && _MDITabControl.Visible)
                tailConfig.SelectedTab = _MDITabControl.SelectedIndex;
            else
                tailConfig.SelectedTab = -1;
            tailConfig.WindowSize = Size;
            tailConfig.WindowPosition = DesktopLocation;
            tailConfig.MinimizedToTray = _trayIcon != null && _trayIcon.Visible;
            tailConfig.AlwaysOnTop = TopMost;

            List<Form> childForms = new List<Form>();
            if (_MDITabControl != null && !_MDITabControl.IsDisposed)
            {
                foreach (TabPage tagPage in _MDITabControl.TabPages)
                {
                    Form tailForm = tagPage.Tag as Form;
                    if (tailForm != null)
                        childForms.Add(tailForm);
                }
            }
            foreach (Form childForm in Application.OpenForms)
            {
                if (childForms.IndexOf(childForm) == -1)
                    childForms.Add(childForm);
            }
            foreach (Form childForm in childForms)
            {
                ITailForm tailForm = childForm as ITailForm;
                if (tailForm != null)
                {
                    TailFileConfig tailFile = new TailFileConfig();
                    tailForm.SaveConfig(tailFile);
                    tailConfig.TailFiles.Add(tailFile);
                }
            }
            return tailConfig;
        }

        private bool LoadSessionWithConfig(TailConfig tailConfig, string configBasePath)
        {
            if (tailConfig == null)
                return false;

            if (!tailConfig.MinimizedToTray)
            {
                Size = tailConfig.WindowSize;
                DesktopLocation = tailConfig.WindowPosition;
            }

            UpdateTitle();

            List<string> eventLogFiles = EventLogForm.GetEventLogFiles();
            Application.DoEvents();

            foreach (TailFileConfig tailFile in tailConfig.TailFiles)
            {
                Form mdiForm = null;
                int index = eventLogFiles.FindIndex(delegate (string arrItem) { return arrItem.Equals(tailFile.FilePath); });
                if (index >= 0)
                    mdiForm = new EventLogForm();
                else
                    mdiForm = new TailForm();

                if (mdiForm != null)
                {
                    ITailForm tailForm = mdiForm as ITailForm;
                    mdiForm.Text = tailFile.Title;
                    if (!tailFile.Modeless)
                    {
                        mdiForm.MdiParent = this;
                        mdiForm.ShowInTaskbar = false;
                        AddMdiChildTab(mdiForm);
                        if (tailForm != null)
                            tailForm.LoadConfig(tailFile, configBasePath);
                        if (mdiForm.IsDisposed)
                        {
                            _MDITabControl.TabPages.Remove(mdiForm.Tag as TabPage);
                            continue;
                        }
                    }
                    mdiForm.Show();

                    if (tailConfig.SelectedTab == -1 || tailFile.Modeless)
                    {
                        if (tailFile.WindowState != FormWindowState.Maximized)
                        {
                            mdiForm.DesktopLocation = tailFile.WindowPosition;
                            mdiForm.Size = tailFile.WindowSize;
                        }
                        if (mdiForm.WindowState != tailFile.WindowState)
                            mdiForm.WindowState = tailFile.WindowState;
                    }

                    if (tailFile.Modeless && tailForm != null)
                        tailForm.LoadConfig(tailFile, configBasePath);
                }
                Application.DoEvents();
            }

            if (tailConfig.SelectedTab != -1 && _MDITabControl.TabPages.Count > 0)
            {
                foreach (Form childForm in MdiChildren)
                    childForm.WindowState = FormWindowState.Minimized;
                _MDITabControl.SelectedIndex = tailConfig.SelectedTab;
                _MDITabControl.Visible = true;
                (_MDITabControl.SelectedTab.Tag as Form).WindowState = FormWindowState.Maximized;
            }

            if (tailConfig.MinimizedToTray)
            {
                _trayIcon.Visible = true;
                WindowState = FormWindowState.Minimized;
                Visible = false;
            }
            else if (tailConfig.AlwaysOnTop)
            {
                alwaysOnTopToolStripMenuItem.Checked = true;
                TopMost = true;
            }

            return true;
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void windowToolStripMenuItem_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            ToolStripMenuItem menuItem = e.ClickedItem as ToolStripMenuItem;
            if (menuItem != null && menuItem.IsMdiWindowListEntry)
            {
                // If a minimized window is chosen from the list, then it is restored to normal state
                this.windowToolStripMenuItem.DropDownItemClicked -= windowToolStripMenuItem_DropDownItemClicked;
                e.ClickedItem.PerformClick();
                if (ActiveMdiChild != null && ActiveMdiChild.WindowState == FormWindowState.Minimized)
                    ActiveMdiChild.WindowState = FormWindowState.Normal;
                this.windowToolStripMenuItem.DropDownItemClicked += windowToolStripMenuItem_DropDownItemClicked;
            }
        }

        private void minimizeToTrayToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!_trayIcon.Visible)
            {
                _trayIcon.Visible = true;
                WindowState = FormWindowState.Minimized;
                Visible = false;
                minimizeToTrayToolStripMenuItem.Checked = true;
                _trayIcon.ShowBalloonTip(3, "Minimized to tray", "Double click the system tray icon to restore window", ToolTipIcon.Info);
            }
            else
            {
                Visible = true;
                WindowState = FormWindowState.Normal;
                _trayIcon.Visible = false;
                minimizeToTrayToolStripMenuItem.Checked = false;
            }
        }

        private void alwaysOnTopToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TopMost = !TopMost;
            alwaysOnTopToolStripMenuItem.Checked = TopMost;
        }

        private void _trayIcon_DoubleClick(object sender, EventArgs e)
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
         }

        private void windowToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
        {
            enableTabsToolStripMenuItem.Checked = _MDITabControl.Visible;
        }

        private void aboutToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            AboutBox aboutBox = new AboutBox();
            aboutBox.ShowDialog(this);
        }

        private void checkForUpdateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                using(new HourGlass(this))
                {
                    CheckForUpdates updateChecker = new CheckForUpdates();
                    updateChecker.PromptAlways = true;
                    updateChecker.Check();
                }
            }
            catch (Exception ex)
            {
                ThreadExceptionDialog dlg = new ThreadExceptionDialog(ex);
                dlg.Text = "Error checking for new updates";
                dlg.ShowDialog(this);
            }
        }

        private void _trayIconContextMenuStrip_Opening(object sender, CancelEventArgs e)
        {
            // We steal the items from the main menu (we restore them when closing again)
            ToolStripItem[] items = new ToolStripItem[fileToolStripMenuItem.DropDownItems.Count];
            fileToolStripMenuItem.DropDownItems.CopyTo(items, 0);
            _trayIconContextMenuStrip.Items.Clear();            // Clear the dummy item
            _trayIconContextMenuStrip.Items.AddRange(items);
            minimizeToTrayToolStripMenuItem.Checked = true;
            minimizeToTrayToolStripMenuItem.Font = new Font(minimizeToTrayToolStripMenuItem.Font, FontStyle.Bold);
        }

        private void _trayIconContextMenuStrip_Closed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            // Restore the items back to the main menu when closing
            ToolStripItem[] items = new ToolStripItem[_trayIconContextMenuStrip.Items.Count];
            _trayIconContextMenuStrip.Items.CopyTo(items, 0);
            fileToolStripMenuItem.DropDownItems.AddRange(items);
            _trayIconContextMenuStrip.Items.Clear();
            _trayIconContextMenuStrip.Items.Add(new ToolStripSeparator());  // Dummy item so menu is shown the next time
            minimizeToTrayToolStripMenuItem.Checked = false;
            minimizeToTrayToolStripMenuItem.Font = new Font(minimizeToTrayToolStripMenuItem.Font, FontStyle.Regular);
        }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
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

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                Array array = e.Data.GetData(DataFormats.FileDrop) as Array;
                if (array == null)
                    return;

                // Extract strings from array
                List<string> filenames = new List<string>();
                foreach(object filename in array)
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
                        OpenFileSelection(validFiles.ToArray());
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
            }), new object[] { param });
        }

        private void MainForm_SizeChanged(object sender, EventArgs e)
        {
            if (_trayIcon.Visible && WindowState == FormWindowState.Minimized)
                Visible = false;
        }

        private void _MDITabControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _MDITabControl.AllowDrop = true;
                _MDITabControl.DoDragDrop(_MDITabControl.SelectedTab, DragDropEffects.All);
                _MDITabControl.AllowDrop = false;
            }
        }

        private void _MDITabControl_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(TabPage)))
                e.Effect = DragDropEffects.Move;
            else
                e.Effect = DragDropEffects.None;
        }

        private void _MDITabControl_DragDrop(object sender, DragEventArgs e)
        {
            Point clientPoint = _MDITabControl.PointToClient(new Point(e.X, e.Y));
            for(int i = 0; i < _MDITabControl.TabPages.Count; ++i)
            {
                if (_MDITabControl.GetTabRect(i).Contains(clientPoint))
                {
                    if (_MDITabControl.TabPages[i] == _MDITabControl.SelectedTab)
                        break;  // No change

                    TabPage tabPage = _MDITabControl.SelectedTab;
                    _MDITabControl.TabPages.Remove(tabPage);
                    _MDITabControl.TabPages.Insert(i, tabPage);
                    _MDITabControl.SelectedIndex = i;
                    break;
                }
            }
        }

        private void clearListToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_mruMenu != null)
            {
                _mruMenu.RemoveAll();
            }
            if (_storage != null)
            {
                _storage.ClearAllRecentFiles();
            }
        }


        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                // Automatically save the current session to xSnakeTail.db (no longer written to XML)
                try
                {
                    bool hasOpenFiles = false;
                    foreach (Form childForm in Application.OpenForms)
                    {
                        ITailForm tailForm = childForm as ITailForm;
                        if (tailForm != null)
                        {
                            hasOpenFiles = true;
                            break;
                        }
                    }
                    if (hasOpenFiles)
                        SaveSessionToDb();
                }
                catch
                {
                    // Ignore session save errors; they must not block closing the program
                }

                // Remove records of files that no longer exist and release SQLite resources
                if (_storage != null)
                {
                    try
                    {
                        _storage.CleanupNonExistentFiles();
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }

                    try
                    {
                        _storage.Dispose();
                    }
                    catch
                    {
                        // Ignore release errors
                    }
                    finally
                    {
                        _storage = null;
                    }
                }
            }
            catch(Exception ex)
            {
                // Bind an owner during shutdown too, so the dialog doesn't lose focus
                try
                {
                    MessageBox.Show(DialogOwner.Resolve(), "Failed to save list of recently used files.\n\n" + ex.Message, "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch
                {
                    // If MessageBox fails too, ignore it completely
                }
            }
            finally
            {
                // Set _instance to null only at the very end, so exception handling can still access it
                _instance = null;
            }
        }

        private void closeItemToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (ActiveMdiChild != null)
            {
                ActiveMdiChild.Close();
            }
        }

        private TForm GetSelectedTabForm<TForm>(object sender) where TForm : Form
        {
            ToolStripItem item = (sender as ToolStripItem);
            if (item != null)
            {
                ContextMenuStrip owner = item.Owner as ContextMenuStrip;
                if (owner != null)
                {
                    var sourceControl = owner.SourceControl;
                    var tabControl = sourceControl as TabControl;
                    if (tabControl != null)
                    {
                        var relativeToScreen = tabControl.PointToClient(owner.Bounds.Location);
                        var tabPageCurrent = GetTabPageFromLocation(tabControl, relativeToScreen);
                        if (tabPageCurrent != null)
                        {
                            return tabPageCurrent.Tag as TForm;
                        }
                    }
                }
            }
            return null;
        }

        private TabPage GetTabPageFromLocation(TabControl tabControl, Point point)
        {
            for (var i = 0; i < tabControl.TabCount; i++)
            {
                if (!tabControl.GetTabRect(i).Contains(point))
                    continue;
                return tabControl.TabPages[i];
            }
            return null;
        }

        private void _copyFolderPathClick(object sender, EventArgs e)
        {
            TailForm tailForm = GetSelectedTabForm<TailForm>(sender);
            if (tailForm != null)
            {
                tailForm.CopyPath();
            }
        }

        private void _closeContextClick(object sender, EventArgs e)
        {
            Form form = GetSelectedTabForm<Form>(sender);
            if (form != null)
            {
                form.Close();
            }
        }

        private void _openContainingFolderClick(object sender, EventArgs e)
        {
            TailForm tailForm = GetSelectedTabForm<TailForm>(sender);
            if (tailForm != null)
            {
                tailForm.OpenExplorer();
            }
        }
    }
}
