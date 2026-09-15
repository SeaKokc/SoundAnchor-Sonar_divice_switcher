using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("SoundAnchor")]
[assembly: System.Reflection.AssemblyDescription("Keeps preferred Windows audio devices selected")]
[assembly: System.Reflection.AssemblyCompany("SoundAnchor")]
[assembly: System.Reflection.AssemblyProduct("SoundAnchor")]
[assembly: System.Reflection.AssemblyVersion("0.9.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.9.0.0")]

namespace SoundAnchor
{
    internal static class Program
    {
        private const string MutexName = @"Local\SoundAnchor.SingleInstance";

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            if (Array.Exists(args, delegate(string value) { return string.Equals(value, "--uninstall", StringComparison.OrdinalIgnoreCase); }))
            {
                SelfInstaller.Uninstall();
                return;
            }

            bool portable = Array.Exists(args, delegate(string value) { return string.Equals(value, "--portable", StringComparison.OrdinalIgnoreCase); });
            if (!portable && !SelfInstaller.IsInstalledLocation())
            {
                DialogResult choice = MessageBox.Show(
                    "Установить SoundAnchor для текущего пользователя?\r\n\r\n" +
                    "Программа будет добавлена в автозапуск и появится в списке установленных приложений. Права администратора не нужны.\r\n\r\n" +
                    "«Нет» — запустить портативную версию.",
                    "Установка SoundAnchor", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
                if (choice == DialogResult.Cancel) return;
                if (choice == DialogResult.Yes)
                {
                    try { SelfInstaller.Install(); }
                    catch (Exception ex) { MessageBox.Show("Не удалось установить SoundAnchor.\r\n\r\n" + ex.Message, "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    return;
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("SoundAnchor уже запущен и находится в области уведомлений.",
                        "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SoundAnchorContext());
                GC.KeepAlive(mutex);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }

    internal sealed class SoundAnchorContext : ApplicationContext
    {
        private readonly AudioDeviceService audio = new AudioDeviceService();
        private readonly NotifyIcon trayIcon;
        private readonly System.Windows.Forms.Timer enforcementTimer;
        private SettingsForm settingsForm;
        private AppConfiguration configuration;
        private int corrections;
        private bool paused;
        private bool isExiting;

        public SoundAnchorContext()
        {
            configuration = AppSettings.Load();

            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть настройки", null, delegate { ShowSettings(); });
            menu.Items.Add("Проверить сейчас", null, delegate { EnforceNow(true); });
            var pauseItem = new ToolStripMenuItem("Приостановить защиту");
            pauseItem.CheckOnClick = true;
            pauseItem.CheckedChanged += delegate { paused = pauseItem.Checked; UpdateStatus(paused ? "Защита приостановлена" : "Защита активна", !paused); };
            menu.Items.Add(pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { ExitApplication(); });

            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Information,
                Text = "SoundAnchor",
                ContextMenuStrip = menu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { ShowSettings(); };

            enforcementTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            enforcementTimer.Tick += delegate { EnforceNow(false); };
            enforcementTimer.Start();

            bool justInstalled = Array.Exists(Environment.GetCommandLineArgs(), delegate(string value) { return string.Equals(value, "--installed", StringComparison.OrdinalIgnoreCase); });
            if (!configuration.IsConfigured || justInstalled)
                ShowSettings();
            else
                EnforceNow(false);
        }

        private void ShowSettings()
        {
            if (settingsForm == null || settingsForm.IsDisposed)
            {
                settingsForm = new SettingsForm(audio, configuration);
                settingsForm.SettingsSaved += OnSettingsSaved;
                settingsForm.FormClosed += delegate { settingsForm = null; };
            }

            settingsForm.RefreshDevices();
            settingsForm.Show();
            settingsForm.WindowState = FormWindowState.Normal;
            settingsForm.Activate();
        }

        private void OnSettingsSaved(object sender, AppConfiguration saved)
        {
            configuration = saved;
            EnforceNow(true);
        }

        private void EnforceNow(bool showFailure)
        {
            if (paused || !configuration.IsConfigured)
                return;

            try
            {
                var missing = new List<string>();
                bool changed = false;
                if (configuration.OutputEnabled)
                {
                    if (audio.DeviceExists(configuration.OutputId))
                        changed |= audio.EnsureDefaultForAllRoles(configuration.OutputId, EDataFlow.Render);
                    else
                        missing.Add(configuration.OutputName);
                }
                if (configuration.InputEnabled)
                {
                    if (audio.DeviceExists(configuration.InputId))
                        changed |= audio.EnsureDefaultForAllRoles(configuration.InputId, EDataFlow.Capture);
                    else
                        missing.Add(configuration.InputName);
                }

                if (missing.Count > 0)
                {
                    string waiting = "Ожидание: " + string.Join(", ", missing.ToArray());
                    UpdateStatus(waiting, false);
                    if (showFailure)
                        MessageBox.Show("Некоторые выбранные устройства сейчас не подключены.\r\nSoundAnchor продолжит ждать их в фоне.", "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (changed) corrections++;
                UpdateStatus(changed ? "Устройства восстановлены" : "Всё работает · исправлений: " + corrections, true);
            }
            catch (Exception ex)
            {
                UpdateStatus("Ошибка переключения", false);
                if (showFailure)
                    MessageBox.Show("Не удалось изменить устройство вывода.\r\n\r\n" + ex.Message,
                        "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateStatus(string message, bool healthy)
        {
            string tooltip = "SoundAnchor — " + message;
            trayIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
            if (settingsForm != null && !settingsForm.IsDisposed)
                settingsForm.SetStatus(message, healthy);
        }

        private void ExitApplication()
        {
            isExiting = true;
            enforcementTimer.Stop();
            trayIcon.Visible = false;
            if (settingsForm != null && !settingsForm.IsDisposed)
                settingsForm.AllowCloseAndClose();
            audio.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !isExiting)
            {
                enforcementTimer.Dispose();
                trayIcon.Dispose();
                audio.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    #if false
    internal sealed class SettingsForm : Form
    {
        private readonly AudioDeviceService audio;
        private readonly ComboBox deviceBox;
        private readonly CheckBox startupBox;
        private readonly Label statusLabel;
        private string selectedDeviceId;
        private string selectedDeviceName;
        private bool allowClose;

        public event EventHandler<AppConfiguration> SettingsSaved;

        public SettingsForm(AudioDeviceService audio, string selectedDeviceId, string selectedDeviceName)
        {
            this.audio = audio;
            this.selectedDeviceId = selectedDeviceId;
            this.selectedDeviceName = selectedDeviceName;

            Text = "SoundAnchor — устройство вывода";
            ClientSize = new Size(540, 275);
            MinimumSize = new Size(556, 314);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Information;
            MaximizeBox = false;

            var title = new Label
            {
                Text = "Закрепить устройство вывода",
                Font = new Font(Font.FontFamily, 15, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(24, 20)
            };
            var description = new Label
            {
                Text = "SoundAnchor будет каждые 2 секунды возвращать выбранные наушники\r\n" +
                       "как устройство Windows по умолчанию, даже если SteelSeries Sonar его изменит.",
                AutoSize = true,
                Location = new Point(27, 58)
            };
            var deviceLabel = new Label
            {
                Text = "Устройство вывода:",
                AutoSize = true,
                Location = new Point(27, 106)
            };

            deviceBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(30, 127),
                Size = new Size(395, 26),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var refreshButton = new Button
            {
                Text = "Обновить",
                Location = new Point(435, 126),
                Size = new Size(80, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            refreshButton.Click += delegate { RefreshDevices(); };

            startupBox = new CheckBox
            {
                Text = "Запускать вместе с Windows",
                Checked = string.IsNullOrEmpty(selectedDeviceId) || AppSettings.IsStartupEnabled(),
                AutoSize = true,
                Location = new Point(30, 169)
            };

            statusLabel = new Label
            {
                Text = "Выберите устройство и сохраните настройки.",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(30, 204)
            };

            var saveButton = new Button
            {
                Text = "Сохранить и применить",
                Location = new Point(340, 225),
                Size = new Size(175, 32),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            saveButton.Click += SaveClicked;
            AcceptButton = saveButton;

            Controls.Add(title);
            Controls.Add(description);
            Controls.Add(deviceLabel);
            Controls.Add(deviceBox);
            Controls.Add(refreshButton);
            Controls.Add(startupBox);
            Controls.Add(statusLabel);
            Controls.Add(saveButton);

            FormClosing += OnFormClosing;
        }

        public void RefreshDevices()
        {
            DeviceInfo previous = deviceBox.SelectedItem as DeviceInfo;
            string wantedId = previous != null ? previous.Id : selectedDeviceId;
            if (string.IsNullOrEmpty(wantedId))
                wantedId = audio.GetDefaultRenderDeviceId();
            IReadOnlyList<DeviceInfo> devices;

            try
            {
                devices = audio.GetActiveRenderDevices();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось получить список аудиоустройств.\r\n\r\n" + ex.Message,
                    "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            deviceBox.BeginUpdate();
            deviceBox.Items.Clear();
            foreach (DeviceInfo device in devices)
            {
                int index = deviceBox.Items.Add(device);
                if (string.Equals(device.Id, wantedId, StringComparison.OrdinalIgnoreCase))
                    deviceBox.SelectedIndex = index;
            }
            deviceBox.EndUpdate();

            if (deviceBox.SelectedIndex < 0 && deviceBox.Items.Count > 0)
                deviceBox.SelectedIndex = 0;

            if (devices.Count == 0)
                SetStatus("Активные устройства вывода не найдены.", false);
        }

        public void SetStatus(string text, bool healthy)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = healthy ? Color.DarkGreen : Color.DarkOrange;
        }

        public void AllowCloseAndClose()
        {
            allowClose = true;
            Close();
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            DeviceInfo device = deviceBox.SelectedItem as DeviceInfo;
            if (device == null)
            {
                MessageBox.Show("Сначала выберите устройство вывода.", "SoundAnchor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                AppSettings.SaveDevice(device.Id, device.Name);
                AppSettings.SetStartup(startupBox.Checked);
                selectedDeviceId = device.Id;
                selectedDeviceName = device.Name;
                EventHandler<DeviceInfo> handler = SettingsSaved;
                if (handler != null)
                    handler(this, device);
                Hide();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось сохранить настройки.\r\n\r\n" + ex.Message,
                    "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        }
    }

    #endif

    internal sealed class SettingsForm : Form
    {
        private static readonly Color Canvas = Color.FromArgb(246, 246, 248);
        private static readonly Color Ink = Color.FromArgb(29, 29, 31);
        private static readonly Color Secondary = Color.FromArgb(110, 110, 115);
        private static readonly Color Blue = Color.FromArgb(0, 113, 227);
        private readonly AudioDeviceService audio;
        private AppConfiguration configuration;
        private readonly AppleComboBox outputBox;
        private readonly AppleComboBox inputBox;
        private readonly ToggleSwitch outputToggle;
        private readonly ToggleSwitch inputToggle;
        private readonly ToggleSwitch startupToggle;
        private readonly Label outputState;
        private readonly Label inputState;
        private readonly Label overallStatus;
        private bool allowClose;

        public event EventHandler<AppConfiguration> SettingsSaved;

        public SettingsForm(AudioDeviceService audio, AppConfiguration configuration)
        {
            this.audio = audio;
            this.configuration = configuration;
            Text = "SoundAnchor";
            ClientSize = new Size(780, 650);
            MinimumSize = new Size(796, 689);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Canvas;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            Icon = AppIcon.Create();
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;

            var logo = new LogoMark { Location = new Point(34, 29), Size = new Size(48, 48) };
            var title = MakeLabel("SoundAnchor", 26f, FontStyle.Bold, Ink, 96, 25);
            var subtitle = MakeLabel("Ваш звук остаётся там, где вы его оставили.", 10.5f, FontStyle.Regular, Secondary, 98, 66);
            overallStatus = MakeLabel("●  Подготовка", 9.5f, FontStyle.Bold, Color.FromArgb(42, 138, 72), 600, 44);
            overallStatus.AutoSize = false;
            overallStatus.Size = new Size(145, 28);
            overallStatus.TextAlign = ContentAlignment.MiddleCenter;
            overallStatus.BackColor = Color.FromArgb(229, 246, 234);

            Label section = MakeLabel("ЗАЩИТА УСТРОЙСТВ", 8.5f, FontStyle.Bold, Secondary, 36, 105);

            RoundedPanel outputCard = CreateDeviceCard(true, 132, out outputBox, out outputToggle, out outputState);
            RoundedPanel inputCard = CreateDeviceCard(false, 304, out inputBox, out inputToggle, out inputState);
            outputToggle.CheckedChanged += delegate { SetDeviceState(outputState, outputBox.SelectedItem as DeviceInfo, outputToggle.Checked); };
            inputToggle.CheckedChanged += delegate { SetDeviceState(inputState, inputBox.SelectedItem as DeviceInfo, inputToggle.Checked); };

            var preferences = new RoundedPanel { Location = new Point(34, 476), Size = new Size(712, 92), BackColor = Color.White, Radius = 18 };
            preferences.Controls.Add(MakeLabel("Запускать вместе с Windows", 11f, FontStyle.Bold, Ink, 22, 18));
            preferences.Controls.Add(MakeLabel("Тихо запускается в трее и ждёт подключения устройств", 9f, FontStyle.Regular, Secondary, 22, 48));
            startupToggle = new ToggleSwitch { Location = new Point(638, 28), Checked = configuration.StartWithWindows };
            preferences.Controls.Add(startupToggle);

            var refresh = new AppleButton { Text = "Обновить устройства", Location = new Point(34, 590), Size = new Size(170, 38), SecondaryStyle = true };
            refresh.Click += delegate { RefreshDevices(); };
            var apply = new AppleButton { Text = "Сохранить", Location = new Point(606, 590), Size = new Size(140, 38) };
            apply.Click += SaveClicked;
            AcceptButton = apply;

            Controls.Add(logo); Controls.Add(title); Controls.Add(subtitle); Controls.Add(overallStatus); Controls.Add(section);
            Controls.Add(outputCard); Controls.Add(inputCard); Controls.Add(preferences); Controls.Add(refresh); Controls.Add(apply);
            FormClosing += OnFormClosing;
        }

        private RoundedPanel CreateDeviceCard(bool output, int y, out AppleComboBox combo, out ToggleSwitch toggle, out Label state)
        {
            var card = new RoundedPanel { Location = new Point(34, y), Size = new Size(712, 152), BackColor = Color.White, Radius = 20 };
            var glyph = new DeviceGlyph { IsMicrophone = !output, Location = new Point(20, 20), Size = new Size(48, 48) };
            card.Controls.Add(glyph);
            card.Controls.Add(MakeLabel(output ? "Вывод звука" : "Микрофон", 12f, FontStyle.Bold, Ink, 82, 19));
            card.Controls.Add(MakeLabel(output ? "Наушники, колонки или аудиоинтерфейс" : "Физический или виртуальный вход Sonar", 9f, FontStyle.Regular, Secondary, 82, 47));
            toggle = new ToggleSwitch { Location = new Point(638, 27), Checked = output ? configuration.OutputEnabled : configuration.InputEnabled };
            card.Controls.Add(toggle);
            combo = new AppleComboBox { Location = new Point(22, 91), Size = new Size(518, 32) };
            state = MakeLabel("Не настроено", 9f, FontStyle.Bold, Secondary, 558, 96);
            state.AutoSize = false; state.Size = new Size(130, 24); state.TextAlign = ContentAlignment.MiddleRight;
            card.Controls.Add(combo); card.Controls.Add(state);
            return card;
        }

        public void RefreshDevices()
        {
            try
            {
                Bind(outputBox, audio.GetActiveDevices(EDataFlow.Render), configuration.OutputId, configuration.OutputName, EDataFlow.Render);
                Bind(inputBox, audio.GetActiveDevices(EDataFlow.Capture), configuration.InputId, configuration.InputName, EDataFlow.Capture);
                SetDeviceState(outputState, outputBox.SelectedItem as DeviceInfo, outputToggle.Checked);
                SetDeviceState(inputState, inputBox.SelectedItem as DeviceInfo, inputToggle.Checked);
                SetStatus("●  Защита активна", true);
            }
            catch (Exception ex)
            {
                SetStatus("●  Нужна проверка", false);
                MessageBox.Show("Не удалось обновить аудиоустройства.\r\n\r\n" + ex.Message, "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Bind(ComboBox box, IReadOnlyList<DeviceInfo> devices, string savedId, string savedName, EDataFlow flow)
        {
            string wanted = savedId;
            DeviceInfo current = box.SelectedItem as DeviceInfo;
            if (current != null) wanted = current.Id;
            if (string.IsNullOrEmpty(wanted)) wanted = audio.GetDefaultDeviceId(flow);
            box.BeginUpdate(); box.Items.Clear();
            foreach (DeviceInfo device in devices)
            {
                int i = box.Items.Add(device);
                if (string.Equals(device.Id, wanted, StringComparison.OrdinalIgnoreCase)) box.SelectedIndex = i;
            }
            if (box.SelectedIndex < 0 && !string.IsNullOrEmpty(savedId))
                box.SelectedIndex = box.Items.Add(new DeviceInfo(savedId, string.IsNullOrEmpty(savedName) ? "Сохранённое устройство" : savedName, false));
            box.EndUpdate();
            if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private static void SetDeviceState(Label label, DeviceInfo device, bool enabled)
        {
            label.Text = !enabled ? "Выключено" : device == null || !device.IsAvailable ? "Не найдено" : "Готово";
            label.ForeColor = enabled && device != null && device.IsAvailable ? Color.FromArgb(42, 138, 72) : Secondary;
        }

        public void SetStatus(string text, bool healthy)
        {
            overallStatus.Text = healthy ? "●  Всё работает" : "●  Нужна проверка";
            overallStatus.ForeColor = healthy ? Color.FromArgb(42, 138, 72) : Color.FromArgb(181, 104, 0);
            overallStatus.BackColor = healthy ? Color.FromArgb(229, 246, 234) : Color.FromArgb(255, 244, 220);
            if (!healthy) overallStatus.Text = "●  " + text.Replace("Ожидание: ", "Ожидание ");
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            DeviceInfo output = outputBox.SelectedItem as DeviceInfo;
            DeviceInfo input = inputBox.SelectedItem as DeviceInfo;
            if (outputToggle.Checked && output == null) { MessageBox.Show("Выберите устройство вывода.", "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (inputToggle.Checked && input == null) { MessageBox.Show("Выберите микрофон.", "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            configuration = new AppConfiguration(
                output == null ? "" : output.Id, output == null ? "" : output.Name, outputToggle.Checked,
                input == null ? "" : input.Id, input == null ? "" : input.Name, inputToggle.Checked,
                startupToggle.Checked);
            try
            {
                AppSettings.Save(configuration);
                AppSettings.SetStartup(configuration.StartWithWindows);
                EventHandler<AppConfiguration> handler = SettingsSaved;
                if (handler != null) handler(this, configuration);
                SetStatus("Настройки сохранены", true);
            }
            catch (Exception ex) { MessageBox.Show("Не удалось сохранить настройки.\r\n\r\n" + ex.Message, "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        public void AllowCloseAndClose() { allowClose = true; Close(); }
        private void OnFormClosing(object sender, FormClosingEventArgs e) { if (!allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } }

        private Label MakeLabel(string text, float size, FontStyle style, Color color, int x, int y)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", size, style), ForeColor = color, BackColor = Color.Transparent, AutoSize = true, Location = new Point(x, y) };
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        public int Radius { get; set; }
        public RoundedPanel() { Radius = 18; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); using (GraphicsPath path = RoundRect(ClientRectangle, Radius)) Region = new Region(path); }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var pen = new Pen(Color.FromArgb(226, 226, 230))) using (GraphicsPath path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius)) e.Graphics.DrawPath(pen, path); base.OnPaint(e); }
        internal static GraphicsPath RoundRect(Rectangle r, int radius) { int d = radius * 2; var p = new GraphicsPath(); p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p; }
    }

    internal sealed class ToggleSwitch : Control
    {
        private bool isChecked;
        public event EventHandler CheckedChanged;
        public bool Checked { get { return isChecked; } set { if (isChecked == value) return; isChecked = value; Invalidate(); EventHandler handler = CheckedChanged; if (handler != null) handler(this, EventArgs.Empty); } }
        public ToggleSwitch() { Size = new Size(48, 28); Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.CheckButton; }
        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var b = new SolidBrush(Checked ? Color.FromArgb(52, 199, 89) : Color.FromArgb(210, 210, 214))) using (GraphicsPath p = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 14)) e.Graphics.FillPath(b, p); int x = Checked ? 23 : 3; using (var b = new SolidBrush(Color.White)) e.Graphics.FillEllipse(b, x, 3, 22, 22); }
    }

    internal sealed class AppleButton : Button
    {
        private bool pressed;
        public bool SecondaryStyle { get; set; }
        public AppleButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Font = new Font("Segoe UI", 9.5f, FontStyle.Bold); Cursor = Cursors.Hand; }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseLeave(EventArgs e) { pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Color fill = SecondaryStyle ? (pressed ? Color.FromArgb(238, 238, 242) : Color.White) : (pressed ? Color.FromArgb(0, 94, 190) : Color.FromArgb(0, 113, 227)); Color ink = SecondaryStyle ? Color.FromArgb(0, 102, 204) : Color.White; using (var b = new SolidBrush(fill)) using (GraphicsPath p = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 10)) e.Graphics.FillPath(b, p); if (SecondaryStyle) using (var pen = new Pen(Color.FromArgb(220, 220, 224))) using (GraphicsPath p = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 10)) e.Graphics.DrawPath(pen, p); TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
    }

    internal sealed class AppleComboBox : ComboBox
    {
        public AppleComboBox() { DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat; BackColor = Color.FromArgb(246, 246, 248); ForeColor = Color.FromArgb(29, 29, 31); Font = new Font("Segoe UI", 9.5f); }
    }

    internal sealed class LogoMark : Control
    {
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var b = new SolidBrush(Color.FromArgb(0, 113, 227))) e.Graphics.FillEllipse(b, ClientRectangle); using (var pen = new Pen(Color.White, 3)) { e.Graphics.DrawArc(pen, 12, 11, 24, 24, 205, 310); e.Graphics.DrawLine(pen, 24, 12, 24, 27); } }
    }

    internal sealed class DeviceGlyph : Control
    {
        public bool IsMicrophone { get; set; }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var b = new SolidBrush(Color.FromArgb(235, 244, 255))) e.Graphics.FillEllipse(b, ClientRectangle); using (var pen = new Pen(Color.FromArgb(0, 113, 227), 2.4f)) { if (IsMicrophone) { e.Graphics.DrawArc(pen, 17, 10, 14, 22, 0, 180); e.Graphics.DrawLine(pen, 14, 22, 14, 25); e.Graphics.DrawArc(pen, 14, 16, 20, 18, 0, 180); e.Graphics.DrawLine(pen, 24, 34, 24, 39); e.Graphics.DrawLine(pen, 19, 39, 29, 39); } else { e.Graphics.DrawArc(pen, 12, 12, 24, 25, 190, 160); e.Graphics.DrawLine(pen, 12, 25, 12, 34); e.Graphics.DrawLine(pen, 36, 25, 36, 34); e.Graphics.DrawLine(pen, 12, 34, 17, 34); e.Graphics.DrawLine(pen, 31, 34, 36, 34); } } }
    }

    internal static class AppIcon
    {
        public static Icon Create() { using (var bitmap = new Bitmap(32, 32)) using (Graphics g = Graphics.FromImage(bitmap)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent); using (var b = new SolidBrush(Color.FromArgb(0, 113, 227))) g.FillEllipse(b, 1, 1, 30, 30); using (var p = new Pen(Color.White, 2.2f)) { g.DrawArc(p, 8, 7, 16, 17, 205, 310); g.DrawLine(p, 16, 8, 16, 19); } IntPtr handle = bitmap.GetHicon(); try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); } } }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    }

    internal sealed class AppConfiguration
    {
        public AppConfiguration(string outputId, string outputName, bool outputEnabled, string inputId, string inputName, bool inputEnabled, bool startWithWindows) { OutputId = outputId; OutputName = outputName; OutputEnabled = outputEnabled; InputId = inputId; InputName = inputName; InputEnabled = inputEnabled; StartWithWindows = startWithWindows; }
        public string OutputId { get; private set; } public string OutputName { get; private set; } public bool OutputEnabled { get; private set; }
        public string InputId { get; private set; } public string InputName { get; private set; } public bool InputEnabled { get; private set; } public bool StartWithWindows { get; private set; }
        public bool IsConfigured { get { return (OutputEnabled && !string.IsNullOrEmpty(OutputId)) || (InputEnabled && !string.IsNullOrEmpty(InputId)); } }
    }

    internal sealed class DeviceInfo
    {
        public DeviceInfo(string id, string name) : this(id, name, true) { }
        public DeviceInfo(string id, string name, bool isAvailable)
        {
            Id = id;
            Name = name;
            IsAvailable = isAvailable;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public bool IsAvailable { get; private set; }
        public override string ToString() { return Name + (IsAvailable ? "" : " — не подключено"); }
    }

    internal static class AppSettings
    {
        private const string SettingsKey = @"Software\SoundAnchor";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "SoundAnchor";

        public static string SelectedDeviceId { get { return Read("DeviceId"); } }
        public static string SelectedDeviceName { get { return Read("DeviceName"); } }

        public static AppConfiguration Load()
        {
            string outputId = Read("OutputId");
            string outputName = Read("OutputName");
            if (string.IsNullOrEmpty(outputId)) { outputId = SelectedDeviceId; outputName = SelectedDeviceName; }
            return new AppConfiguration(
                outputId, outputName, ReadBool("OutputEnabled", !string.IsNullOrEmpty(outputId)),
                Read("InputId"), Read("InputName"), ReadBool("InputEnabled", false),
                IsStartupEnabled());
        }

        public static void Save(AppConfiguration value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                key.SetValue("OutputId", value.OutputId, RegistryValueKind.String);
                key.SetValue("OutputName", value.OutputName, RegistryValueKind.String);
                key.SetValue("OutputEnabled", value.OutputEnabled ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("InputId", value.InputId, RegistryValueKind.String);
                key.SetValue("InputName", value.InputName, RegistryValueKind.String);
                key.SetValue("InputEnabled", value.InputEnabled ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        public static void SaveDevice(string id, string name)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                key.SetValue("DeviceId", id, RegistryValueKind.String);
                key.SetValue("DeviceName", name, RegistryValueKind.String);
            }
        }

        public static bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                return key != null && key.GetValue(RunValue) != null;
            }
        }

        public static void SetStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null)
                    throw new InvalidOperationException("Не удалось открыть раздел автозапуска Windows.");

                if (enabled)
                    key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --startup", RegistryValueKind.String);
                else
                    key.DeleteValue(RunValue, false);
            }
        }

        private static string Read(string name)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
            {
                return key == null ? string.Empty : Convert.ToString(key.GetValue(name, string.Empty));
            }
        }

        private static bool ReadBool(string name, bool fallback)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
            {
                if (key == null || key.GetValue(name) == null) return fallback;
                return Convert.ToInt32(key.GetValue(name)) != 0;
            }
        }
    }

    internal sealed class AudioDeviceService : IDisposable
    {
        private readonly IMMDeviceEnumerator enumerator;

        public AudioDeviceService()
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }

        public IReadOnlyList<DeviceInfo> GetActiveRenderDevices()
        {
            return GetActiveDevices(EDataFlow.Render);
        }

        public IReadOnlyList<DeviceInfo> GetActiveDevices(EDataFlow flow)
        {
            var result = new List<DeviceInfo>();
            IMMDeviceCollection collection = null;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, DeviceState.Active, out collection));
                uint count;
                Marshal.ThrowExceptionForHR(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(collection.Item(i, out device));
                        string id;
                        Marshal.ThrowExceptionForHR(device.GetId(out id));
                        result.Add(new DeviceInfo(id, GetFriendlyName(device)));
                    }
                    finally
                    {
                        ReleaseCom(device);
                    }
                }
            }
            finally
            {
                ReleaseCom(collection);
            }

            result.Sort(delegate(DeviceInfo left, DeviceInfo right)
            {
                return string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        public bool DeviceExists(string deviceId)
        {
            IMMDevice device = null;
            try
            {
                int hr = enumerator.GetDevice(deviceId, out device);
                if (hr < 0 || device == null)
                    return false;
                DeviceState state;
                Marshal.ThrowExceptionForHR(device.GetState(out state));
                return (state & DeviceState.Active) == DeviceState.Active;
            }
            finally
            {
                ReleaseCom(device);
            }
        }

        public string GetDefaultRenderDeviceId()
        {
            return GetDefaultDeviceId(EDataFlow.Render);
        }

        public string GetDefaultDeviceId(EDataFlow flow)
        {
            IMMDevice device = null;
            try
            {
                int hr = enumerator.GetDefaultAudioEndpoint(flow, ERole.Multimedia, out device);
                if (hr < 0 || device == null)
                    return string.Empty;
                string id;
                Marshal.ThrowExceptionForHR(device.GetId(out id));
                return id;
            }
            finally
            {
                ReleaseCom(device);
            }
        }

        public bool EnsureDefaultForAllRoles(string deviceId)
        {
            return EnsureDefaultForAllRoles(deviceId, EDataFlow.Render);
        }

        public bool EnsureDefaultForAllRoles(string deviceId, EDataFlow flow)
        {
            bool changed = false;
            ERole[] roles = { ERole.Console, ERole.Multimedia, ERole.Communications };
            foreach (ERole role in roles)
            {
                if (!IsDefault(deviceId, flow, role))
                {
                    SetDefault(deviceId, role);
                    changed = true;
                }
            }
            return changed;
        }

        private bool IsDefault(string deviceId, EDataFlow flow, ERole role)
        {
            IMMDevice device = null;
            try
            {
                int hr = enumerator.GetDefaultAudioEndpoint(flow, role, out device);
                if (hr < 0 || device == null)
                    return false;
                string currentId;
                Marshal.ThrowExceptionForHR(device.GetId(out currentId));
                return string.Equals(currentId, deviceId, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                ReleaseCom(device);
            }
        }

        private static void SetDefault(string deviceId, ERole role)
        {
            object clientObject = new PolicyConfigClient();
            try
            {
                IPolicyConfig client = (IPolicyConfig)clientObject;
                Marshal.ThrowExceptionForHR(client.SetDefaultEndpoint(deviceId, role));
            }
            finally
            {
                ReleaseCom(clientObject);
            }
        }

        private static string GetFriendlyName(IMMDevice device)
        {
            IPropertyStore store = null;
            PropVariant value = new PropVariant();
            try
            {
                Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageAccess.Read, out store));
                PropertyKey key = PropertyKeys.DeviceFriendlyName;
                Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
                return value.GetString() ?? "Неизвестное устройство";
            }
            finally
            {
                value.Clear();
                ReleaseCom(store);
            }
        }

        private static void ReleaseCom(object value)
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }

        public void Dispose()
        {
            ReleaseCom(enumerator);
        }
    }

    internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [Flags]
    internal enum DeviceState : uint
    {
        Active = 0x00000001,
        Disabled = 0x00000002,
        NotPresent = 0x00000004,
        Unplugged = 0x00000008,
        All = 0x0000000F
    }

    internal enum StorageAccess : uint { Read = 0x00000000 }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(StorageAccess access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out DeviceState state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    internal static class PropertyKeys
    {
        public static readonly PropertyKey DeviceFriendlyName =
            new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] private ushort valueType;
        [FieldOffset(8)] private IntPtr pointerValue;

        public string GetString()
        {
            return valueType == 31 && pointerValue != IntPtr.Zero
                ? Marshal.PtrToStringUni(pointerValue)
                : null;
        }

        public void Clear()
        {
            PropVariantClear(ref this);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class PolicyConfigClient { }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, IntPtr defaultValue, IntPtr minimumValue);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    internal static class SelfInstaller
    {
        private const string ProductName = "SoundAnchor";
        private const string Version = "0.9.0-preview";
        private static string InstallDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName); } }
        private static string InstalledExecutable { get { return Path.Combine(InstallDirectory, ProductName + ".exe"); } }

        public static bool IsInstalledLocation()
        {
            return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase);
        }

        public static void Install()
        {
            Directory.CreateDirectory(InstallDirectory);
            File.Copy(Application.ExecutablePath, InstalledExecutable, true);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundAnchor"))
            {
                key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
                key.SetValue("DisplayVersion", Version, RegistryValueKind.String);
                key.SetValue("Publisher", "SoundAnchor", RegistryValueKind.String);
                key.SetValue("DisplayIcon", InstalledExecutable, RegistryValueKind.String);
                key.SetValue("InstallLocation", InstallDirectory, RegistryValueKind.String);
                key.SetValue("UninstallString", "\"" + InstalledExecutable + "\" --uninstall", RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", Math.Max(1, (int)(new FileInfo(InstalledExecutable).Length / 1024)), RegistryValueKind.DWord);
            }
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                run.SetValue(ProductName, "\"" + InstalledExecutable + "\" --startup", RegistryValueKind.String);

            Process.Start(new ProcessStartInfo(InstalledExecutable, "--installed") { UseShellExecute = true });
        }

        public static void Uninstall()
        {
            if (MessageBox.Show("Удалить SoundAnchor и его настройки?", "Удаление SoundAnchor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (Process process in Process.GetProcessesByName(ProductName))
            {
                if (process.Id != Process.GetCurrentProcess().Id)
                {
                    try { process.Kill(); } catch { }
                }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\SoundAnchor", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundAnchor", false); } catch { }
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                if (run != null) run.DeleteValue(ProductName, false);

            string command = "/c timeout /t 2 /nobreak > nul & del /f /q \"" + InstalledExecutable + "\" & rmdir \"" + InstallDirectory + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }
}
