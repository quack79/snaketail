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
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnakeTail
{
    public enum ThemeMode
    {
        Light,
        Dark,
        System,
    }

    /// <summary>
    /// Central light/dark theme handling. Only colors that are still system colors (the designer defaults) are
    /// swapped for the dark palette; explicit colors (user-chosen text/back colors, keyword highlights) are left alone.
    /// </summary>
    internal static class Theme
    {
        internal static readonly Color DarkWindow = Color.FromArgb(30, 30, 30);
        internal static readonly Color DarkWindowText = Color.FromArgb(220, 220, 220);
        internal static readonly Color DarkControl = Color.FromArgb(45, 45, 48);
        internal static readonly Color DarkControlText = Color.FromArgb(241, 241, 241);
        internal static readonly Color DarkControlLight = Color.FromArgb(62, 62, 66);
        internal static readonly Color DarkBorder = Color.FromArgb(85, 85, 85);
        internal static readonly Color DarkWorkspace = Color.FromArgb(37, 37, 38);
        internal static readonly Color DarkGrayText = Color.FromArgb(140, 140, 140);
        internal static readonly Color DarkButton = Color.FromArgb(60, 60, 60);
        internal static readonly Color DarkButtonHover = Color.FromArgb(80, 80, 80);
        internal static readonly Color DarkLink = Color.FromArgb(78, 161, 243);

        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private sealed class ControlState
        {
            public Color? BackSource;
            public Color? BackApplied;
            public Color? ForeSource;
            public Color? ForeApplied;
            public Color? LinkSource;
            public bool ButtonStyled;
            public bool ComboStyled;
            public bool TabPageStyled;
            public bool HandleHooked;
            public bool NativeDark;
            public TabStripPainter TabStripPainter;
        }

        /// <summary>
        /// Owner-drawn tabs only draw the tab items; this fills the rest of the tab strip, which Windows paints light.
        /// </summary>
        private sealed class TabStripPainter : NativeWindow
        {
            private const int WM_PAINT = 0x000F;
            private readonly TabControl _tabControl;

            public TabStripPainter(TabControl tabControl)
            {
                _tabControl = tabControl;
                if (tabControl.IsHandleCreated)
                    AssignHandle(tabControl.Handle);
                tabControl.HandleCreated += (sender, e) => AssignHandle(_tabControl.Handle);
                tabControl.HandleDestroyed += (sender, e) => ReleaseHandle();
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != WM_PAINT || !IsDark)
                    return;

                using (Graphics g = Graphics.FromHwnd(Handle))
                using (Region region = new Region(_tabControl.ClientRectangle))
                using (SolidBrush brush = new SolidBrush(DarkControl))
                {
                    for (int i = 0; i < _tabControl.TabCount; ++i)
                        region.Exclude(_tabControl.GetTabRect(i));
                    g.FillRegion(brush, region);
                }
            }
        }

        private static readonly ConditionalWeakTable<Control, ControlState> _states = new ConditionalWeakTable<Control, ControlState>();
        private static readonly List<Form> _forms = new List<Form>();
        private static bool _systemEventsHooked;

        public static ThemeMode Mode { get; private set; } = ThemeMode.Light;
        public static bool IsDark { get; private set; }

        public static ThemeMode ParseMode(string value)
        {
            ThemeMode mode;
            if (!string.IsNullOrEmpty(value) && Enum.TryParse(value.Trim(), true, out mode) && Enum.IsDefined(typeof(ThemeMode), mode))
                return mode;
            return ThemeMode.Light;
        }

        public static bool ResolveIsDark(ThemeMode mode, bool systemUsesLightTheme)
        {
            return mode == ThemeMode.Dark || (mode == ThemeMode.System && !systemUsesLightTheme);
        }

        public static bool SystemUsesLightTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    object value = key?.GetValue("AppsUseLightTheme");
                    if (value is int)
                        return (int)value != 0;
                }
            }
            catch
            {
                // Missing or unreadable key means the classic light theme
            }
            return true;
        }

        /// <summary>
        /// Sets the theme mode and re-themes all registered windows when the effective theme changes.
        /// </summary>
        public static void SetMode(ThemeMode mode)
        {
            Mode = mode;
            if (mode == ThemeMode.System && !_systemEventsHooked)
            {
                SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
                _systemEventsHooked = true;
            }
            Refresh(false);
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (Mode == ThemeMode.System)
                Refresh(false);
        }

        private static void Refresh(bool force)
        {
            bool isDark = ResolveIsDark(Mode, Mode != ThemeMode.System || SystemUsesLightTheme());
            if (isDark == IsDark && !force)
                return;

            IsDark = isDark;
            if (isDark)
                ToolStripManager.Renderer = new DarkToolStripRenderer();
            else
                ToolStripManager.RenderMode = ToolStripManagerRenderMode.Professional;

            foreach (Form form in _forms.ToArray())
            {
                if (!form.IsDisposed)
                    Apply(form);
            }
        }

        /// <summary>
        /// Maps a system color to its dark counterpart when the dark theme is active.
        /// </summary>
        public static Color Map(Color color)
        {
            return MapColor(color, IsDark);
        }

        internal static Color MapColor(Color color, bool dark)
        {
            if (!dark || !color.IsSystemColor)
                return color;

            switch (color.ToKnownColor())
            {
                case KnownColor.Window:
                    return DarkWindow;
                case KnownColor.WindowText:
                    return DarkWindowText;
                case KnownColor.ControlText:
                case KnownColor.MenuText:
                case KnownColor.InfoText:
                    return DarkControlText;
                case KnownColor.Control:
                case KnownColor.ButtonFace:
                case KnownColor.Menu:
                case KnownColor.MenuBar:
                    return DarkControl;
                case KnownColor.Info:
                case KnownColor.ControlLight:
                case KnownColor.ControlLightLight:
                case KnownColor.ButtonHighlight:
                    return DarkControlLight;
                case KnownColor.ControlDark:
                case KnownColor.ControlDarkDark:
                case KnownColor.ButtonShadow:
                    return DarkBorder;
                case KnownColor.AppWorkspace:
                    return DarkWorkspace;
                case KnownColor.GrayText:
                    return DarkGrayText;
                case KnownColor.HotTrack:
                    return DarkLink;
                default:
                    return color;
            }
        }

        /// <summary>
        /// The back color the control would have without the theme (used when saving user configuration).
        /// </summary>
        public static Color UnthemedBackColor(Control control)
        {
            ControlState state;
            if (_states.TryGetValue(control, out state) && state.BackSource.HasValue && state.BackApplied == control.BackColor)
                return state.BackSource.Value;
            return control.BackColor;
        }

        /// <summary>
        /// The text color the control would have without the theme (used when saving user configuration).
        /// </summary>
        public static Color UnthemedForeColor(Control control)
        {
            ControlState state;
            if (_states.TryGetValue(control, out state) && state.ForeSource.HasValue && state.ForeApplied == control.ForeColor)
                return state.ForeSource.Value;
            return control.ForeColor;
        }

        /// <summary>
        /// Applies the current theme to a window or control and all of its children.
        /// Forms are remembered, so they are re-themed when the theme changes.
        /// </summary>
        public static void Apply(Control control)
        {
            Form form = control as Form;
            if (form != null && !_forms.Contains(form))
            {
                _forms.Add(form);
                form.Disposed += (sender, e) => _forms.Remove((Form)sender);
            }

            control.SuspendLayout();
            try
            {
                ApplyTree(control);
            }
            finally
            {
                control.ResumeLayout();
            }
            control.Invalidate(true);
        }

        private static void ApplyTree(Control control)
        {
            ControlState state = _states.GetValue(control, key => new ControlState());

            // Tool strips are painted by the renderer; only their hosted controls are themed
            if (!(control is ToolStrip))
            {
                TabPage tabPage = control as TabPage;
                if (tabPage != null && IsDark && tabPage.UseVisualStyleBackColor)
                {
                    tabPage.UseVisualStyleBackColor = false;
                    state.TabPageStyled = true;
                }

                if (control is Button)
                    ApplyButton((Button)control, state);
                else
                    ApplyBackColor(control, state);
                ApplyForeColor(control, state);

                if (tabPage != null && !IsDark && state.TabPageStyled)
                {
                    tabPage.UseVisualStyleBackColor = true;
                    state.TabPageStyled = false;
                }

                ComboBox comboBox = control as ComboBox;
                if (comboBox != null)
                {
                    if (IsDark && comboBox.FlatStyle == FlatStyle.Standard)
                    {
                        comboBox.FlatStyle = FlatStyle.Flat;
                        state.ComboStyled = true;
                    }
                    else if (!IsDark && state.ComboStyled)
                    {
                        comboBox.FlatStyle = FlatStyle.Standard;
                        state.ComboStyled = false;
                    }
                }

                TabControl tabControl = control as TabControl;
                if (tabControl != null && tabControl.DrawMode == TabDrawMode.OwnerDrawFixed && state.TabStripPainter == null)
                    state.TabStripPainter = new TabStripPainter(tabControl);

                LinkLabel linkLabel = control as LinkLabel;
                if (linkLabel != null)
                {
                    if (IsDark && !state.LinkSource.HasValue)
                    {
                        state.LinkSource = linkLabel.LinkColor;
                        linkLabel.LinkColor = DarkLink;
                    }
                    else if (!IsDark && state.LinkSource.HasValue)
                    {
                        linkLabel.LinkColor = state.LinkSource.Value;
                        state.LinkSource = null;
                    }
                }
            }

            if (control is Form || control is ListView || control is TextBoxBase || control is ListBox || control is TreeView)
            {
                if (control.IsHandleCreated)
                    ApplyNativeTheme(control);
                if (!state.HandleHooked)
                {
                    // Handles can be recreated, so the native theme is applied every time one is created
                    control.HandleCreated += (sender, e) => ApplyNativeTheme((Control)sender);
                    state.HandleHooked = true;
                }
            }

            foreach (Control child in control.Controls)
                ApplyTree(child);
        }

        private static void ApplyBackColor(Control control, ControlState state)
        {
            Color current = control.BackColor;
            if (!state.BackSource.HasValue || state.BackApplied != current)
            {
                if (!current.IsSystemColor)
                {
                    // Explicit color (user choice or inherited from a themed parent)
                    state.BackSource = null;
                    state.BackApplied = null;
                    return;
                }
                state.BackSource = current;
            }

            Color target = Map(state.BackSource.Value);
            if (current != target)
                control.BackColor = target;
            state.BackApplied = control.BackColor;
        }

        private static void ApplyForeColor(Control control, ControlState state)
        {
            Color current = control.ForeColor;
            if (!state.ForeSource.HasValue || state.ForeApplied != current)
            {
                if (!current.IsSystemColor)
                {
                    state.ForeSource = null;
                    state.ForeApplied = null;
                    return;
                }
                state.ForeSource = current;
            }

            Color target = Map(state.ForeSource.Value);
            if (current != target)
                control.ForeColor = target;
            state.ForeApplied = control.ForeColor;
        }

        private static void ApplyButton(Button button, ControlState state)
        {
            if (IsDark)
            {
                if (!state.ButtonStyled && button.FlatStyle == FlatStyle.Standard && button.UseVisualStyleBackColor)
                {
                    // Visual style buttons ignore BackColor, so dark buttons are drawn flat
                    button.FlatStyle = FlatStyle.Flat;
                    button.BackColor = DarkButton;
                    button.FlatAppearance.BorderColor = DarkBorder;
                    button.FlatAppearance.MouseOverBackColor = DarkButtonHover;
                    state.ButtonStyled = true;
                }
            }
            else if (state.ButtonStyled)
            {
                button.FlatStyle = FlatStyle.Standard;
                button.FlatAppearance.BorderColor = Color.Empty;
                button.FlatAppearance.MouseOverBackColor = Color.Empty;
                button.ResetBackColor();
                button.UseVisualStyleBackColor = true;
                state.ButtonStyled = false;
            }
        }

        private static void ApplyNativeTheme(Control control)
        {
            // Light is the native default, so only undo what an earlier dark theme changed
            ControlState state = _states.GetValue(control, key => new ControlState());
            if (!IsDark && !state.NativeDark)
                return;
            state.NativeDark = IsDark;

            try
            {
                if (control is Form)
                {
                    int value = IsDark ? 1 : 0;
                    if (NativeMethods.DwmSetWindowAttribute(control.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                        NativeMethods.DwmSetWindowAttribute(control.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
                    if (control.Visible)
                        NativeMethods.SetWindowPos(control.Handle, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    return;
                }

                // Dark scroll bars (and list view column headers)
                NativeMethods.SetWindowTheme(control.Handle, IsDark ? "DarkMode_Explorer" : null, null);
                if (control is ListView)
                {
                    IntPtr header = NativeMethods.SendMessage(control.Handle, NativeMethods.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                    if (header != IntPtr.Zero)
                        NativeMethods.SetWindowTheme(header, IsDark ? "DarkMode_ItemsView" : null, null);
                }
            }
            catch
            {
                // Native theming is cosmetic; older Windows versions may not support it
            }
        }

        private sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
        {
            public DarkToolStripRenderer()
                : base(new DarkColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                // Keep explicit item colors (e.g. the highlight color button), map the default text colors
                if (!e.Item.Enabled)
                    e.TextColor = DarkGrayText;
                else if (e.Item.ForeColor.IsSystemColor || e.TextColor.IsSystemColor)
                    e.TextColor = DarkControlText;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item == null || e.Item.Enabled ? DarkControlText : DarkGrayText;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle rect = e.ImageRectangle;
                rect.Inflate(1, 1);
                using (SolidBrush brush = new SolidBrush(ColorTable.CheckBackground))
                    e.Graphics.FillRectangle(brush, rect);

                // Draw the check glyph in the text color instead of the black check image
                var oldMode = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(DarkControlText, Math.Max(1.5f, rect.Height / 9f)))
                {
                    float x = rect.Left + rect.Width * 0.25f;
                    float y = rect.Top + rect.Height * 0.52f;
                    e.Graphics.DrawLines(pen, new PointF[] {
                        new PointF(x, y),
                        new PointF(rect.Left + rect.Width * 0.42f, rect.Top + rect.Height * 0.70f),
                        new PointF(rect.Left + rect.Width * 0.76f, rect.Top + rect.Height * 0.30f) });
                }
                e.Graphics.SmoothingMode = oldMode;
            }
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            private static readonly Color Background = DarkControl;
            private static readonly Color DropDownBackground = Color.FromArgb(37, 37, 38);
            private static readonly Color Selected = Color.FromArgb(62, 62, 64);
            private static readonly Color Pressed = Color.FromArgb(0, 122, 204);
            private static readonly Color Border = Color.FromArgb(67, 67, 70);

            public DarkColorTable()
            {
                UseSystemColors = false;
            }

            public override Color ToolStripGradientBegin => Background;
            public override Color ToolStripGradientMiddle => Background;
            public override Color ToolStripGradientEnd => Background;
            public override Color ToolStripBorder => Background;
            public override Color ToolStripDropDownBackground => DropDownBackground;
            public override Color ToolStripContentPanelGradientBegin => Background;
            public override Color ToolStripContentPanelGradientEnd => Background;
            public override Color ToolStripPanelGradientBegin => Background;
            public override Color ToolStripPanelGradientEnd => Background;
            public override Color MenuStripGradientBegin => Background;
            public override Color MenuStripGradientEnd => Background;
            public override Color StatusStripGradientBegin => Background;
            public override Color StatusStripGradientEnd => Background;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Selected;
            public override Color MenuItemSelected => Selected;
            public override Color MenuItemSelectedGradientBegin => Selected;
            public override Color MenuItemSelectedGradientEnd => Selected;
            public override Color MenuItemPressedGradientBegin => DropDownBackground;
            public override Color MenuItemPressedGradientMiddle => DropDownBackground;
            public override Color MenuItemPressedGradientEnd => DropDownBackground;
            public override Color ImageMarginGradientBegin => DropDownBackground;
            public override Color ImageMarginGradientMiddle => DropDownBackground;
            public override Color ImageMarginGradientEnd => DropDownBackground;
            public override Color ButtonSelectedHighlight => Selected;
            public override Color ButtonSelectedHighlightBorder => Border;
            public override Color ButtonSelectedBorder => Border;
            public override Color ButtonSelectedGradientBegin => Selected;
            public override Color ButtonSelectedGradientMiddle => Selected;
            public override Color ButtonSelectedGradientEnd => Selected;
            public override Color ButtonPressedHighlight => Pressed;
            public override Color ButtonPressedHighlightBorder => Pressed;
            public override Color ButtonPressedBorder => Pressed;
            public override Color ButtonPressedGradientBegin => Pressed;
            public override Color ButtonPressedGradientMiddle => Pressed;
            public override Color ButtonPressedGradientEnd => Pressed;
            public override Color ButtonCheckedHighlight => Selected;
            public override Color ButtonCheckedHighlightBorder => Border;
            public override Color ButtonCheckedGradientBegin => Selected;
            public override Color ButtonCheckedGradientMiddle => Selected;
            public override Color ButtonCheckedGradientEnd => Selected;
            public override Color CheckBackground => Selected;
            public override Color CheckSelectedBackground => Pressed;
            public override Color CheckPressedBackground => Pressed;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
            public override Color GripDark => Border;
            public override Color GripLight => Background;
            public override Color OverflowButtonGradientBegin => Background;
            public override Color OverflowButtonGradientMiddle => Background;
            public override Color OverflowButtonGradientEnd => Background;
            public override Color RaftingContainerGradientBegin => Background;
            public override Color RaftingContainerGradientEnd => Background;
        }

        static class NativeMethods
        {
            public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
            public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            public const int LVM_GETHEADER = 0x1000 + 31;
            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOMOVE = 0x0002;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_FRAMECHANGED = 0x0020;

            [DllImport("dwmapi.dll")]
            public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

            [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
            public static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

            [DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        }
    }
}
