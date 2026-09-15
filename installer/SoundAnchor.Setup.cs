using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("SoundAnchor Setup")]
[assembly: AssemblyProduct("SoundAnchor")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace SoundAnchorSetup
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }

    internal sealed class SetupForm : Form
    {
        private static readonly Color Canvas = Color.FromArgb(246, 246, 248);
        private static readonly Color Ink = Color.FromArgb(29, 29, 31);
        private static readonly Color Secondary = Color.FromArgb(105, 105, 112);
        private static readonly Color Blue = Color.FromArgb(0, 113, 227);
        private readonly bool russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
        private readonly CheckBox desktopShortcut;
        private readonly SetupButton installButton;
        private readonly SetupButton closeButton;
        private readonly Label status;
        private bool installed;

        public SetupForm()
        {
            Text = "SoundAnchor Setup";
            ClientSize = new Size(640, 510);
            MinimumSize = MaximumSize = Size;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Canvas;
            Font = new Font("Segoe UI", 9.5f);

            Panel titleBar = new Panel { Location = new Point(0, 0), Size = new Size(640, 44), BackColor = Color.FromArgb(251, 251, 252) };
            Label caption = LabelFor("SoundAnchor", 9f, FontStyle.Bold, Ink, 18, 14);
            SetupButton windowClose = new SetupButton { Text = "×", Location = new Point(588, 7), Size = new Size(38, 30), SecondaryStyle = true };
            windowClose.Click += delegate { Close(); };
            titleBar.Controls.Add(caption); titleBar.Controls.Add(windowClose);
            titleBar.MouseDown += DragWindow; caption.MouseDown += DragWindow;

            LogoMark logo = new LogoMark { Location = new Point(38, 70), Size = new Size(52, 52) };
            Label title = LabelFor(T("Установка SoundAnchor", "Install SoundAnchor"), 24f, FontStyle.Bold, Ink, 108, 68);
            Label subtitle = LabelFor(T("Закрепите звук один раз — и забудьте о переключениях.", "Pin your audio once and forget about switching."), 10.5f, FontStyle.Regular, Secondary, 110, 105);

            RoundedCard card = new RoundedCard { Location = new Point(34, 150), Size = new Size(572, 212) };
            card.Controls.Add(LabelFor(T("Будет установлено", "What gets installed"), 12f, FontStyle.Bold, Ink, 24, 22));
            card.Controls.Add(LabelFor("✓  " + T("Приложение в меню «Пуск»", "App shortcut in the Start menu"), 10f, FontStyle.Regular, Ink, 25, 59));
            card.Controls.Add(LabelFor("✓  " + T("Автозапуск вместе с Windows", "Automatic launch with Windows"), 10f, FontStyle.Regular, Ink, 25, 88));
            card.Controls.Add(LabelFor("✓  " + T("Удаление через параметры Windows", "Uninstall entry in Windows Settings"), 10f, FontStyle.Regular, Ink, 25, 117));
            Label pathCaption = LabelFor(T("Папка установки", "Install location"), 8.5f, FontStyle.Bold, Secondary, 25, 158);
            Label path = LabelFor(Installer.InstallDirectory, 9f, FontStyle.Regular, Ink, 25, 179);
            card.Controls.Add(pathCaption); card.Controls.Add(path);

            desktopShortcut = new CheckBox { Text = T("Создать ярлык на рабочем столе", "Create a desktop shortcut"), Location = new Point(38, 385), AutoSize = true, Checked = true, FlatStyle = FlatStyle.Flat, ForeColor = Ink, BackColor = Color.Transparent };
            Label noAdmin = LabelFor(T("Без прав администратора", "No administrator rights required"), 8.5f, FontStyle.Regular, Secondary, 38, 416);

            closeButton = new SetupButton { Text = T("Отмена", "Cancel"), Location = new Point(34, 452), Size = new Size(132, 40), SecondaryStyle = true };
            closeButton.Click += delegate { Close(); };
            installButton = new SetupButton { Text = T("Установить", "Install"), Location = new Point(438, 452), Size = new Size(168, 40) };
            installButton.Click += InstallClicked;
            status = LabelFor("", 9.5f, FontStyle.Bold, Secondary, 184, 464);
            status.AutoSize = false; status.Size = new Size(235, 24); status.TextAlign = ContentAlignment.MiddleCenter;

            Controls.Add(titleBar); Controls.Add(logo); Controls.Add(title); Controls.Add(subtitle); Controls.Add(card);
            Controls.Add(desktopShortcut); Controls.Add(noAdmin); Controls.Add(closeButton); Controls.Add(installButton); Controls.Add(status);
        }

        private void InstallClicked(object sender, EventArgs e)
        {
            if (installed)
            {
                Process.Start(new ProcessStartInfo(Installer.InstalledExecutable, "--installed") { UseShellExecute = true });
                Close();
                return;
            }

            installButton.Enabled = false; closeButton.Enabled = false; desktopShortcut.Enabled = false;
            status.ForeColor = Secondary; status.Text = T("Устанавливаем…", "Installing…");
            Cursor = Cursors.WaitCursor; Refresh();
            try
            {
                Installer.Install(desktopShortcut.Checked);
                installed = true;
                status.ForeColor = Color.FromArgb(42, 138, 72);
                status.Text = T("✓ Установка завершена", "✓ Installation complete");
                installButton.Text = T("Открыть SoundAnchor", "Open SoundAnchor");
                closeButton.Text = T("Закрыть", "Close");
                installButton.Enabled = true; closeButton.Enabled = true;
            }
            catch (Exception ex)
            {
                status.ForeColor = Color.FromArgb(184, 45, 45); status.Text = T("Не удалось установить", "Installation failed");
                MessageBox.Show(T("Не удалось установить SoundAnchor.\r\n\r\n", "Couldn't install SoundAnchor.\r\n\r\n") + ex.Message, "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                installButton.Enabled = true; closeButton.Enabled = true; desktopShortcut.Enabled = true;
            }
            finally { Cursor = Cursors.Default; }
        }

        private string T(string ru, string en) { return russian ? ru : en; }
        private static Label LabelFor(string text, float size, FontStyle style, Color color, int x, int y) { return new Label { Text = text, Font = new Font("Segoe UI", size, style), ForeColor = color, BackColor = Color.Transparent, AutoSize = true, Location = new Point(x, y) }; }
        private void DragWindow(object sender, MouseEventArgs e) { if (e.Button != MouseButtons.Left) return; ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); int preference = 2; try { DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int)); } catch { } }
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }

    internal static class Installer
    {
        private const string ProductName = "SoundAnchor";
        private const string Version = "1.0.0";
        public static string InstallDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName); } }
        public static string InstalledExecutable { get { return Path.Combine(InstallDirectory, ProductName + ".exe"); } }
        public static string UninstallerExecutable { get { return Path.Combine(InstallDirectory, "Uninstall SoundAnchor.exe"); } }

        public static void Install(bool createDesktopShortcut)
        {
            foreach (Process process in Process.GetProcessesByName(ProductName))
            {
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1500))
                    {
                        process.Kill();
                        if (!process.WaitForExit(5000)) throw new IOException("SoundAnchor is still running. Close it and try again.");
                    }
                }
                catch (InvalidOperationException) { }
            }
            Directory.CreateDirectory(InstallDirectory);
            string resourceName = null;
            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (string name in assembly.GetManifestResourceNames()) if (name.EndsWith("SoundAnchor.exe", StringComparison.OrdinalIgnoreCase)) { resourceName = name; break; }
            if (resourceName == null) throw new InvalidOperationException("SoundAnchor payload is missing.");
            using (Stream source = assembly.GetManifestResourceStream(resourceName))
            using (FileStream destination = new FileStream(InstalledExecutable, FileMode.Create, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
            File.Copy(InstalledExecutable, UninstallerExecutable, true);
            File.WriteAllText(Path.Combine(InstallDirectory, "install-info.txt"), "SoundAnchor " + Version + Environment.NewLine + "Installed: " + DateTime.Now.ToString("u") + Environment.NewLine + "Location: " + InstallDirectory);

            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName);
            Directory.CreateDirectory(startMenu);
            CreateShortcut(Path.Combine(startMenu, ProductName + ".lnk"), InstalledExecutable, "", T("Открыть SoundAnchor", "Open SoundAnchor"));
            CreateShortcut(Path.Combine(startMenu, T("Удалить SoundAnchor.lnk", "Uninstall SoundAnchor.lnk")), UninstallerExecutable, "--uninstall", T("Удалить SoundAnchor", "Uninstall SoundAnchor"));
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk");
            if (createDesktopShortcut) CreateShortcut(desktop, InstalledExecutable, "", ProductName); else if (File.Exists(desktop)) File.Delete(desktop);

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundAnchor"))
            {
                key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
                key.SetValue("DisplayVersion", Version, RegistryValueKind.String);
                key.SetValue("Publisher", ProductName, RegistryValueKind.String);
                key.SetValue("DisplayIcon", InstalledExecutable + ",0", RegistryValueKind.String);
                key.SetValue("InstallLocation", InstallDirectory, RegistryValueKind.String);
                key.SetValue("UninstallString", "\"" + UninstallerExecutable + "\" --uninstall", RegistryValueKind.String);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", Math.Max(1, (int)(new FileInfo(InstalledExecutable).Length / 1024)), RegistryValueKind.DWord);
            }
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) run.SetValue(ProductName, "\"" + InstalledExecutable + "\" --startup", RegistryValueKind.String);

            if (!File.Exists(InstalledExecutable) || !File.Exists(UninstallerExecutable)) throw new IOException("Installed executables were not created.");
            using (RegistryKey check = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundAnchor", false)) if (check == null) throw new IOException("Windows uninstall registration failed.");
        }

        private static string T(string ru, string en) { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? ru : en; }
        private static void CreateShortcut(string shortcutPath, string target, string arguments, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Windows Script Host is unavailable.");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            Type type = shortcut.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
            type.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { InstallDirectory });
            type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
            type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { target + ",0" });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell);
        }
    }

    internal sealed class RoundedCard : Panel
    {
        public RoundedCard() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnPaintBackground(PaintEventArgs e) { base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (GraphicsPath path = Shapes.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 18)) { using (SolidBrush brush = new SolidBrush(Color.White)) e.Graphics.FillPath(brush, path); using (Pen pen = new Pen(Color.FromArgb(224, 224, 229))) e.Graphics.DrawPath(pen, path); } }
    }

    internal sealed class SetupButton : Button
    {
        private bool pressed;
        public bool SecondaryStyle { get; set; }
        public SetupButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; UseVisualStyleBackColor = false; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; FlatAppearance.MouseDownBackColor = Color.Transparent; FlatAppearance.MouseOverBackColor = Color.Transparent; Font = new Font("Segoe UI", 9.5f, FontStyle.Bold); Cursor = Cursors.Hand; }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseLeave(EventArgs e) { pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Color fill = SecondaryStyle ? (pressed ? Color.FromArgb(225, 225, 230) : Color.FromArgb(239, 239, 242)) : (pressed ? Color.FromArgb(0, 94, 190) : Color.FromArgb(0, 113, 227)); Color ink = SecondaryStyle ? Color.FromArgb(45, 45, 49) : Color.White; if (!Enabled) { fill = Color.FromArgb(190, 190, 195); ink = Color.White; } using (SolidBrush brush = new SolidBrush(fill)) using (GraphicsPath path = Shapes.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 10)) e.Graphics.FillPath(brush, path); TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); }
    }

    internal sealed class LogoMark : Control
    {
        public LogoMark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (SolidBrush brush = new SolidBrush(Color.FromArgb(0, 113, 227))) e.Graphics.FillEllipse(brush, ClientRectangle); using (Pen pen = new Pen(Color.White, 3f)) { e.Graphics.DrawArc(pen, 13, 12, 26, 26, 205, 310); e.Graphics.DrawLine(pen, 26, 13, 26, 29); } }
    }

    internal static class Shapes
    {
        public static GraphicsPath RoundRect(Rectangle rectangle, int radius) { int diameter = radius * 2; GraphicsPath path = new GraphicsPath(); path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90); path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90); path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90); path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path; }
    }
}
