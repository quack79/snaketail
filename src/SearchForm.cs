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
using System.Drawing;
using System.Runtime.InteropServices;
using System.Media;
using System.Windows.Forms;

namespace SnakeTail
{
    partial class SearchForm : Form
    {
        private static SearchForm _instance = null;
        public static SearchForm Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new SearchForm();
                    return _instance;
                }
                return _instance;
            }
        }

        private ITailForm _activeTailForm = null;
        public ITailForm ActiveTailForm
        {
            get
            {
                if (_activeTailForm != null)
                    return _activeTailForm;
                else if (MainForm.Instance != null)
                    return MainForm.Instance.ActiveMdiChild as ITailForm;
                else
                    return null;
            }
            set
            {
                if (_activeTailForm != null && _activeTailForm.TailWindow != null && !_activeTailForm.TailWindow.IsDisposed)
                {
                    _activeTailForm.TailWindow.FormClosing -= _activeForm_FormClosing;
                }
                _activeTailForm = value;
                if (Visible)
                {
                    NativeMethods.SetWindowPos(this.Handle, NativeMethods.HWND_TOP, 0, 0, 0, 0, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);  // BringToFront without focus
                }
                if (_activeTailForm != null)
                    _activeTailForm.TailWindow.FormClosing += _activeForm_FormClosing;
            }
        }

        public void StartSearch(ITailForm activeTailForm)
        {
            if (!Visible)
            {
                if (activeTailForm != null && activeTailForm.TailWindow != null && activeTailForm.TailWindow.MdiParent == null)
                    Show(activeTailForm.TailWindow);
                else
                    Show(MainForm.Instance);
            }
            ActiveTailForm = activeTailForm;
            HideSearchFeedback();
            BringToFront();
            _searchTextBox.SelectAll();
            _searchTextBox.Focus();
        }

        public void SearchAgain(ITailForm activeTailForm, bool searchForward, bool keywordHighlights)
        {
            if (activeTailForm != null)
            {
                ActiveTailForm = activeTailForm;

                bool found = false;
                using (new HourGlass(this))
                {
                    using (new HourGlass(activeTailForm.TailWindow))
                    {
                        found = ActiveTailForm.SearchForText(_searchTextBox.Text, _matchCaseCheckBox.Checked, searchForward, keywordHighlights, keywordHighlights ? false : _wrapArroundcheckBox.Checked);
                    }
                }

                // Search results are now shown inline in the window, so an invisible modal dialog can't block the UI.
                if (found)
                    HideSearchFeedback();
                else if (keywordHighlights)
                    ShowSearchFeedback("Cannot find any highlighted lines");
                else
                    ShowSearchFeedback("Cannot find \"" + _searchTextBox.Text + "\"");
            }
        }

        private void ShowSearchFeedback(string message)
        {
            // The no-match message is shown directly in the search window and shows/hides with it.
            _resultLabel.Text = message;
            _resultLabel.Visible = true;
            SystemSounds.Asterisk.Play();
        }

        private void HideSearchFeedback()
        {
            // Clear the hint on a successful search, new input, or when the window closes.
            _resultLabel.Visible = false;
            _resultLabel.Text = string.Empty;
        }

        void _activeForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_activeTailForm != null && _activeTailForm.TailWindow != null)
            {
                _activeTailForm.TailWindow.FormClosing -= _activeForm_FormClosing;
                _activeTailForm = null;
            }
            if (MainForm.Instance != null)
                MainForm.Instance.Focus();
        }

        protected SearchForm()
        {
            InitializeComponent();
            _findNextBtn.Enabled = false;
            _resultLabel.ForeColor = Color.Firebrick;
        }

        static class NativeMethods
        {
            public static readonly IntPtr HWND_TOP = (IntPtr)0;
            public const int SWP_NOACTIVATE = 0x0010;
            public const int SWP_NOSIZE = 0x0001;
            public const int SWP_NOMOVE = 0x0002;

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern int SetWindowPos(IntPtr hWnd,
              IntPtr hWndInsertAfter,
              int x,
              int y,
              int cx,
              int cy,
              UInt32 uFlags);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F3)
            {
                SearchAgain(ActiveTailForm, !_upRadioBtn.Checked, false);
                return true;
            }
            if (keyData == (Keys.Shift | Keys.F3))
            {
                SearchAgain(ActiveTailForm, _upRadioBtn.Checked, false);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void _cancelBtn_Click(object sender, EventArgs e)
        {
            HideSearchFeedback();
            if (_activeTailForm != null && _activeTailForm.TailWindow != null && !_activeTailForm.TailWindow.IsDisposed && _activeTailForm.TailWindow.MdiParent == null)
                _activeTailForm.TailWindow.Focus();
            else if (MainForm.Instance != null)
                MainForm.Instance.Focus();
            if (!IsDisposed)
                Hide();
        }

        private void _searchTextBox_TextChanged(object sender, EventArgs e)
        {
            HideSearchFeedback();
            if (_searchTextBox.Text.Length == 0)
                _findNextBtn.Enabled = false;
            else
                _findNextBtn.Enabled = true;
        }

        private void _findNextBtn_Click(object sender, EventArgs e)
        {
            if (ActiveTailForm != null)
            {
                if ((Control.ModifierKeys & Keys.Shift) == Keys.None)
                    SearchAgain(ActiveTailForm, !_upRadioBtn.Checked, false);
                else
                    SearchAgain(ActiveTailForm, _upRadioBtn.Checked, false);
            }
        }

        private void SearchForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            HideSearchFeedback();
            _instance = null;
        }
    }
}
