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
        private readonly ToolStripMenuItem openItem;
        private readonly ToolStripMenuItem checkItem;
        private readonly ToolStripMenuItem pauseItem;
        private readonly ToolStripMenuItem exitItem;

        public SoundAnchorContext()
        {
            configuration = AppSettings.Load();

            var menu = new ContextMenuStrip();
            openItem = new ToolStripMenuItem(); openItem.Click += delegate { ShowSettings(); };
            checkItem = new ToolStripMenuItem(); checkItem.Click += delegate { EnforceNow(true); };
            pauseItem = new ToolStripMenuItem();
            pauseItem.CheckOnClick = true;
            pauseItem.CheckedChanged += delegate { paused = pauseItem.Checked; UpdateStatus(paused ? L("Защита приостановлена", "Protection paused") : L("Защита активна", "Protection active"), !paused); };
            exitItem = new ToolStripMenuItem(); exitItem.Click += delegate { ExitApplication(); };
            menu.Items.Add(openItem); menu.Items.Add(checkItem); menu.Items.Add(pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            ApplyTrayLanguage();

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
                settingsForm.UiLanguageChanged += delegate { ApplyTrayLanguage(); };
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
                    string waiting = L("Ожидание: ", "Waiting for ") + string.Join(", ", missing.ToArray());
                    UpdateStatus(waiting, false);
                    if (showFailure)
                        MessageBox.Show(L("Некоторые выбранные устройства сейчас не подключены.\r\nSoundAnchor продолжит ждать их в фоне.", "Some selected devices are disconnected.\r\nSoundAnchor will keep waiting in the background."), "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (changed) corrections++;
                UpdateStatus(changed ? L("Устройства восстановлены", "Devices restored") : L("Всё работает · исправлений: ", "All good · corrections: ") + corrections, true);
                if (showFailure)
                {
                    trayIcon.BalloonTipTitle = "SoundAnchor";
                    trayIcon.BalloonTipText = changed ? L("Выбранные устройства восстановлены.", "Selected devices were restored.") : L("Все выбранные устройства уже активны.", "All selected devices are already active.");
                    trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                    trayIcon.ShowBalloonTip(1800);
                }
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

        private string L(string russian, string english) { return AppSettings.Language == "ru" ? russian : english; }

        private void ApplyTrayLanguage()
        {
            openItem.Text = L("Открыть настройки", "Open settings");
            checkItem.Text = L("Проверить сейчас", "Check now");
            pauseItem.Text = L("Приостановить защиту", "Pause protection");
            exitItem.Text = L("Выход", "Exit");
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
        public event EventHandler UiLanguageChanged;

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
        private readonly StatusPill overallStatus;
        private readonly ToastBanner feedback;
        private readonly System.Windows.Forms.Timer feedbackTimer;
        private bool lastRefreshSucceeded;
        private string language;
        private bool darkMode;
        private readonly CustomTitleBar titleBar;
        private bool allowClose;

        public event EventHandler<AppConfiguration> SettingsSaved;
        public event EventHandler UiLanguageChanged;

        public SettingsForm(AudioDeviceService audio, AppConfiguration configuration)
        {
            this.audio = audio;
            this.configuration = configuration;
            language = AppSettings.Language;
            darkMode = AppSettings.DarkMode;
            Text = "SoundAnchor";
            ClientSize = new Size(780, 692);
            MinimumSize = new Size(780, 692);
            MaximumSize = new Size(780, 692);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Canvas;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            Icon = AppIcon.Create();
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.Dpi;

            titleBar = new CustomTitleBar(this, language, darkMode) { Location = new Point(0, 0), Size = new Size(780, 44) };
            titleBar.LanguageChanged += delegate { language = language == "ru" ? "en" : "ru"; AppSettings.SaveUi(language, darkMode); titleBar.SetPreferences(language, darkMode); ApplyLanguage(); EventHandler handler = UiLanguageChanged; if (handler != null) handler(this, EventArgs.Empty); };
            titleBar.ThemeChanged += delegate { darkMode = !darkMode; AppSettings.SaveUi(language, darkMode); titleBar.SetPreferences(language, darkMode); ApplyTheme(); };

            var logo = new LogoMark { Location = new Point(34, 62), Size = new Size(48, 48) };
            var title = MakeLabel("SoundAnchor", 26f, FontStyle.Bold, Ink, 96, 58); title.Name = "mainTitle";
            var subtitle = MakeLabel("Ваш звук остаётся там, где вы его оставили.", 10.5f, FontStyle.Regular, Secondary, 98, 99); subtitle.Name = "subtitle";
            overallStatus = new StatusPill { Text = "●  Подготовка", Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.FromArgb(42, 138, 72), SurfaceColor = Color.FromArgb(229, 246, 234), Location = new Point(600, 77), Size = new Size(145, 28) };

            Label section = MakeLabel("ЗАЩИТА УСТРОЙСТВ", 8.5f, FontStyle.Bold, Secondary, 36, 138); section.Name = "section";

            RoundedPanel outputCard = CreateDeviceCard(true, 165, out outputBox, out outputToggle, out outputState);
            RoundedPanel inputCard = CreateDeviceCard(false, 337, out inputBox, out inputToggle, out inputState);
            outputToggle.CheckedChanged += delegate { SetDeviceState(outputState, outputBox.SelectedItem as DeviceInfo, outputToggle.Checked); };
            inputToggle.CheckedChanged += delegate { SetDeviceState(inputState, inputBox.SelectedItem as DeviceInfo, inputToggle.Checked); };

            var preferences = new RoundedPanel { Location = new Point(34, 509), Size = new Size(712, 92), SurfaceColor = Color.White, Radius = 18 };
            var startupTitle = MakeLabel("Запускать вместе с Windows", 11f, FontStyle.Bold, Ink, 22, 18); startupTitle.Name = "startupTitle"; preferences.Controls.Add(startupTitle);
            var startupSubtitle = MakeLabel("Тихо запускается в трее и ждёт подключения устройств", 9f, FontStyle.Regular, Secondary, 22, 48); startupSubtitle.Name = "startupSubtitle"; preferences.Controls.Add(startupSubtitle);
            startupToggle = new ToggleSwitch { Location = new Point(638, 28), Checked = configuration.StartWithWindows };
            preferences.Controls.Add(startupToggle);

            var refresh = new AppleButton { Name = "refreshButton", Text = "Обновить устройства", Location = new Point(34, 634), Size = new Size(180, 38), SecondaryStyle = true };
            refresh.Click += delegate { RefreshDevices(); ShowFeedback(lastRefreshSucceeded ? Tr("Список устройств обновлён", "Device list updated") : Tr("Не удалось обновить устройства", "Couldn't update devices"), lastRefreshSucceeded); };
            var apply = new AppleButton { Name = "saveButton", Text = "Сохранить", Location = new Point(606, 634), Size = new Size(140, 38) };
            apply.Click += SaveClicked;
            AcceptButton = apply;

            feedback = new ToastBanner { Location = new Point(225, 630), Size = new Size(360, 44), Visible = false };
            feedbackTimer = new System.Windows.Forms.Timer { Interval = 2400 };
            feedbackTimer.Tick += delegate { feedbackTimer.Stop(); feedback.Visible = false; };

            Controls.Add(logo); Controls.Add(title); Controls.Add(subtitle); Controls.Add(overallStatus); Controls.Add(section);
            Controls.Add(outputCard); Controls.Add(inputCard); Controls.Add(preferences); Controls.Add(refresh); Controls.Add(apply); Controls.Add(feedback); Controls.Add(titleBar);
            FormClosing += OnFormClosing;
            FormClosed += delegate { feedbackTimer.Dispose(); };
            ApplyLanguage();
            ApplyTheme();
        }

        private RoundedPanel CreateDeviceCard(bool output, int y, out AppleComboBox combo, out ToggleSwitch toggle, out Label state)
        {
            var card = new RoundedPanel { Location = new Point(34, y), Size = new Size(712, 152), SurfaceColor = Color.White, Radius = 20 };
            var glyph = new DeviceGlyph { IsMicrophone = !output, Location = new Point(20, 20), Size = new Size(48, 48) };
            card.Controls.Add(glyph);
            var cardTitle = MakeLabel(output ? "Вывод звука" : "Микрофон", 12f, FontStyle.Bold, Ink, 82, 19); cardTitle.Name = output ? "outputTitle" : "inputTitle"; card.Controls.Add(cardTitle);
            var cardSubtitle = MakeLabel(output ? "Наушники, колонки или аудиоинтерфейс" : "Физический или виртуальный вход Sonar", 9f, FontStyle.Regular, Secondary, 82, 47); cardSubtitle.Name = output ? "outputSubtitle" : "inputSubtitle"; card.Controls.Add(cardSubtitle);
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
                lastRefreshSucceeded = true;
            }
            catch (Exception ex)
            {
                lastRefreshSucceeded = false;
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

        private void SetDeviceState(Label label, DeviceInfo device, bool enabled)
        {
            label.Text = !enabled ? Tr("Выключено", "Off") : device == null || !device.IsAvailable ? Tr("Не найдено", "Unavailable") : Tr("Готово", "Ready");
            label.ForeColor = enabled && device != null && device.IsAvailable ? Color.FromArgb(42, 138, 72) : Secondary;
        }

        public void SetStatus(string text, bool healthy)
        {
            overallStatus.Text = healthy ? Tr("●  Всё работает", "●  All good") : Tr("●  Нужна проверка", "●  Check needed");
            overallStatus.ForeColor = healthy ? (darkMode ? Color.FromArgb(92, 214, 124) : Color.FromArgb(42, 138, 72)) : (darkMode ? Color.FromArgb(255, 184, 77) : Color.FromArgb(181, 104, 0));
            overallStatus.SurfaceColor = healthy ? (darkMode ? Color.FromArgb(32, 67, 43) : Color.FromArgb(229, 246, 234)) : (darkMode ? Color.FromArgb(79, 58, 28) : Color.FromArgb(255, 244, 220));
            if (!healthy) overallStatus.Text = "●  " + text.Replace("Ожидание: ", Tr("Ожидание ", "Waiting for "));
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            DeviceInfo output = outputBox.SelectedItem as DeviceInfo;
            DeviceInfo input = inputBox.SelectedItem as DeviceInfo;
            if (outputToggle.Checked && output == null) { MessageBox.Show(Tr("Выберите устройство вывода.", "Select an output device."), "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (inputToggle.Checked && input == null) { MessageBox.Show(Tr("Выберите микрофон.", "Select a microphone."), "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

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
                ShowFeedback(Tr("Настройки сохранены и применены", "Settings saved and applied"), true);
            }
            catch (Exception ex) { ShowFeedback(Tr("Не удалось сохранить настройки", "Couldn't save settings"), false); MessageBox.Show(Tr("Не удалось сохранить настройки.", "Couldn't save settings.") + "\r\n\r\n" + ex.Message, "SoundAnchor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ShowFeedback(string message, bool success)
        {
            feedbackTimer.Stop();
            feedback.ShowMessage(message, success);
            feedback.BringToFront();
            feedbackTimer.Start();
        }

        private string Tr(string russian, string english) { return language == "ru" ? russian : english; }

        private void ApplyLanguage()
        {
            SetText("subtitle", Tr("Ваш звук остаётся там, где вы его оставили.", "Your audio stays exactly where you left it."));
            SetText("section", Tr("ЗАЩИТА УСТРОЙСТВ", "DEVICE PROTECTION"));
            SetText("outputTitle", Tr("Вывод звука", "Audio output"));
            SetText("outputSubtitle", Tr("Наушники, колонки или аудиоинтерфейс", "Headphones, speakers, or an audio interface"));
            SetText("inputTitle", Tr("Микрофон", "Microphone"));
            SetText("inputSubtitle", Tr("Физический или виртуальный вход Sonar", "Physical input or a Sonar virtual microphone"));
            SetText("startupTitle", Tr("Запускать вместе с Windows", "Launch with Windows"));
            SetText("startupSubtitle", Tr("Тихо запускается в трее и ждёт подключения устройств", "Starts quietly in the tray and waits for your devices"));
            SetText("refreshButton", Tr("Обновить устройства", "Refresh devices"));
            SetText("saveButton", Tr("Сохранить", "Save"));
            SetDeviceState(outputState, outputBox.SelectedItem as DeviceInfo, outputToggle.Checked);
            SetDeviceState(inputState, inputBox.SelectedItem as DeviceInfo, inputToggle.Checked);
            SetStatus("", true);
        }

        private void SetText(string name, string value)
        {
            Control[] found = Controls.Find(name, true);
            if (found.Length > 0) found[0].Text = value;
        }

        private void ApplyTheme()
        {
            Color canvas = darkMode ? Color.FromArgb(24, 24, 26) : Canvas;
            Color surface = darkMode ? Color.FromArgb(38, 38, 41) : Color.White;
            Color primary = darkMode ? Color.FromArgb(245, 245, 247) : Ink;
            Color secondary = darkMode ? Color.FromArgb(166, 166, 173) : Secondary;
            BackColor = canvas;
            ApplyThemeTo(Controls, surface, primary, secondary);
            titleBar.DarkMode = darkMode;
            SetStatus("", true);
            Invalidate(true);
        }

        private void ApplyThemeTo(Control.ControlCollection controls, Color surface, Color primary, Color secondary)
        {
            foreach (Control control in controls)
            {
                if (control is RoundedPanel) { RoundedPanel panel = (RoundedPanel)control; panel.SurfaceColor = surface; panel.BorderColor = darkMode ? Color.FromArgb(58, 58, 62) : Color.FromArgb(226, 226, 230); }
                if (control is Label && Convert.ToString(control.Tag) == "primary") control.ForeColor = primary;
                if (control is Label && Convert.ToString(control.Tag) == "secondary") control.ForeColor = secondary;
                if (control is AppleComboBox) { control.BackColor = darkMode ? Color.FromArgb(49, 49, 53) : Color.FromArgb(246, 246, 248); control.ForeColor = primary; }
                if (control is AppleButton) ((AppleButton)control).DarkMode = darkMode;
                if (control is DeviceGlyph) ((DeviceGlyph)control).DarkMode = darkMode;
                ApplyThemeTo(control.Controls, surface, primary, secondary);
            }
        }

        public void AllowCloseAndClose() { allowClose = true; Close(); }
        private void OnFormClosing(object sender, FormClosingEventArgs e) { if (!allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } }

        private Label MakeLabel(string text, float size, FontStyle style, Color color, int x, int y)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", size, style), ForeColor = color, BackColor = Color.Transparent, AutoSize = true, Location = new Point(x, y), Tag = color == Ink ? "primary" : color == Secondary ? "secondary" : "status" };
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams parameters = base.CreateParams; parameters.ClassStyle |= 0x00020000; return parameters; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int preference = 2;
            try { DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int)); } catch { }
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }

    internal sealed class CustomTitleBar : Control
    {
        private readonly Form owner;
        private readonly ChromeButton languageButton;
        private readonly ChromeButton themeButton;
        private readonly ChromeButton minimizeButton;
        private readonly ChromeButton closeButton;
        private bool darkMode;
        public event EventHandler LanguageChanged;
        public event EventHandler ThemeChanged;

        public CustomTitleBar(Form owner, string language, bool dark)
        {
            this.owner = owner;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            var caption = new Label { Text = "SoundAnchor", Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoSize = true, Location = new Point(18, 14), BackColor = Color.Transparent };
            caption.MouseDown += DragWindow;
            Controls.Add(caption);
            languageButton = new ChromeButton { Location = new Point(532, 7), Size = new Size(54, 30) };
            themeButton = new ChromeButton { Location = new Point(592, 7), Size = new Size(54, 30), IsTheme = true };
            minimizeButton = new ChromeButton { Text = "—", Location = new Point(674, 7), Size = new Size(42, 30), AccessibleName = "Minimize" };
            closeButton = new ChromeButton { Text = "×", Location = new Point(722, 7), Size = new Size(42, 30), IsClose = true, AccessibleName = "Close" };
            languageButton.Click += delegate { EventHandler handler = LanguageChanged; if (handler != null) handler(this, EventArgs.Empty); };
            themeButton.Click += delegate { EventHandler handler = ThemeChanged; if (handler != null) handler(this, EventArgs.Empty); };
            minimizeButton.Click += delegate { owner.WindowState = FormWindowState.Minimized; };
            closeButton.Click += delegate { owner.Close(); };
            Controls.Add(languageButton); Controls.Add(themeButton); Controls.Add(minimizeButton); Controls.Add(closeButton);
            MouseDown += DragWindow;
            SetPreferences(language, dark);
        }

        public bool DarkMode
        {
            get { return darkMode; }
            set
            {
                darkMode = value;
                BackColor = value ? Color.FromArgb(31, 31, 34) : Color.FromArgb(250, 250, 252);
                ForeColor = value ? Color.FromArgb(235, 235, 240) : Color.FromArgb(55, 55, 58);
                foreach (Control control in Controls) { control.ForeColor = ForeColor; if (control is ChromeButton) ((ChromeButton)control).DarkMode = value; }
                Invalidate(true);
            }
        }

        public void SetPreferences(string language, bool dark)
        {
            languageButton.Text = language == "ru" ? "RU" : "EN";
            languageButton.AccessibleName = language == "ru" ? "Switch to English" : "Переключить на русский";
            themeButton.Text = "";
            themeButton.ThemeIsDark = dark;
            themeButton.AccessibleName = dark ? "Light theme" : "Dark theme";
            DarkMode = dark;
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(owner.Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    }

    internal sealed class ChromeButton : Button
    {
        private bool hover;
        public bool DarkMode { get; set; }
        public bool IsClose { get; set; }
        public bool IsTheme { get; set; }
        public bool ThemeIsDark { get; set; }
        public ChromeButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; UseVisualStyleBackColor = false; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; FlatAppearance.MouseOverBackColor = Color.Transparent; FlatAppearance.MouseDownBackColor = Color.Transparent; Font = new Font("Segoe UI", 9f, FontStyle.Bold); Cursor = Cursors.Hand; TabStop = true; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Color fill = hover ? (IsClose ? Color.FromArgb(232, 73, 73) : (DarkMode ? Color.FromArgb(58, 58, 62) : Color.FromArgb(230, 230, 234))) : (DarkMode ? Color.FromArgb(42, 42, 46) : Color.FromArgb(240, 240, 243)); Color ink = hover && IsClose ? Color.White : (DarkMode ? Color.FromArgb(240, 240, 243) : Color.FromArgb(55, 55, 58)); using (var brush = new SolidBrush(fill)) using (GraphicsPath path = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 9)) e.Graphics.FillPath(brush, path); if (IsTheme) DrawThemeIcon(e.Graphics, fill, ink); else TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
        private void DrawThemeIcon(Graphics graphics, Color background, Color ink)
        {
            int cx = Width / 2, cy = Height / 2;
            if (ThemeIsDark)
            {
                using (var pen = new Pen(ink, 1.7f)) { graphics.DrawEllipse(pen, cx - 4, cy - 4, 8, 8); for (int i = 0; i < 8; i++) { double angle = i * Math.PI / 4; graphics.DrawLine(pen, cx + (int)(Math.Cos(angle) * 7), cy + (int)(Math.Sin(angle) * 7), cx + (int)(Math.Cos(angle) * 9), cy + (int)(Math.Sin(angle) * 9)); } }
            }
            else
            {
                using (var brush = new SolidBrush(ink)) graphics.FillEllipse(brush, cx - 6, cy - 7, 13, 13);
                using (var brush = new SolidBrush(background)) graphics.FillEllipse(brush, cx - 2, cy - 9, 12, 12);
            }
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        public int Radius { get; set; }
        public Color BorderColor { get; set; }
        private Color surfaceColor;
        public Color SurfaceColor { get { return surfaceColor; } set { surfaceColor = value; Invalidate(); } }
        public RoundedPanel() { Radius = 18; surfaceColor = Color.White; BorderColor = Color.FromArgb(226, 226, 230); SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
        protected override void OnPaintBackground(PaintEventArgs e) { base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var brush = new SolidBrush(surfaceColor)) using (GraphicsPath path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius)) e.Graphics.FillPath(brush, path); }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var pen = new Pen(BorderColor)) using (GraphicsPath path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius)) e.Graphics.DrawPath(pen, path); base.OnPaint(e); }
        internal static GraphicsPath RoundRect(Rectangle r, int radius) { int d = radius * 2; var p = new GraphicsPath(); p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p; }
    }

    internal sealed class ToggleSwitch : Control
    {
        private bool isChecked;
        public event EventHandler CheckedChanged;
        public bool Checked { get { return isChecked; } set { if (isChecked == value) return; isChecked = value; Invalidate(); EventHandler handler = CheckedChanged; if (handler != null) handler(this, EventArgs.Empty); } }
        public ToggleSwitch() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; Size = new Size(48, 28); Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.CheckButton; }
        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var b = new SolidBrush(Checked ? Color.FromArgb(52, 199, 89) : Color.FromArgb(210, 210, 214))) using (GraphicsPath p = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 14)) e.Graphics.FillPath(b, p); int x = Checked ? 23 : 3; using (var b = new SolidBrush(Color.White)) e.Graphics.FillEllipse(b, x, 3, 22, 22); }
    }

    internal sealed class AppleButton : Button
    {
        private bool pressed;
        public bool DarkMode { get; set; }
        public bool SecondaryStyle { get; set; }
        public AppleButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; UseVisualStyleBackColor = false; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; FlatAppearance.MouseOverBackColor = Color.Transparent; FlatAppearance.MouseDownBackColor = Color.Transparent; Font = new Font("Segoe UI", 9.5f, FontStyle.Bold); Cursor = Cursors.Hand; }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseLeave(EventArgs e) { pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Color secondaryFill = DarkMode ? (pressed ? Color.FromArgb(65, 65, 70) : Color.FromArgb(46, 46, 50)) : (pressed ? Color.FromArgb(238, 238, 242) : Color.White); Color fill = SecondaryStyle ? secondaryFill : (pressed ? Color.FromArgb(0, 94, 190) : Color.FromArgb(0, 113, 227)); Color ink = SecondaryStyle ? (DarkMode ? Color.FromArgb(90, 170, 255) : Color.FromArgb(0, 102, 204)) : Color.White; using (var b = new SolidBrush(fill)) using (GraphicsPath p = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 10)) e.Graphics.FillPath(b, p); if (SecondaryStyle) using (var pen = new Pen(DarkMode ? Color.FromArgb(72, 72, 76) : Color.FromArgb(220, 220, 224))) using (GraphicsPath p = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 10)) e.Graphics.DrawPath(pen, p); TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
    }

    internal sealed class ToastBanner : Control
    {
        private string message = "";
        private bool success;
        public ToastBanner() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; Font = new Font("Segoe UI", 9.5f, FontStyle.Bold); AccessibleRole = AccessibleRole.Alert; }
        public void ShowMessage(string value, bool isSuccess) { message = value; success = isSuccess; AccessibleName = value; Visible = true; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = success ? Color.FromArgb(29, 29, 31) : Color.FromArgb(184, 45, 45);
            using (var brush = new SolidBrush(fill)) using (GraphicsPath path = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 13)) e.Graphics.FillPath(brush, path);
            using (var pen = new Pen(Color.White, 2f))
            {
                if (success) { e.Graphics.DrawLine(pen, 17, 22, 21, 26); e.Graphics.DrawLine(pen, 21, 26, 28, 17); }
                else { e.Graphics.DrawLine(pen, 19, 17, 27, 25); e.Graphics.DrawLine(pen, 27, 17, 19, 25); }
            }
            TextRenderer.DrawText(e.Graphics, message, Font, new Rectangle(40, 0, Width - 52, Height), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class StatusPill : Control
    {
        private Color surfaceColor = Color.FromArgb(229, 246, 234);
        public Color SurfaceColor { get { return surfaceColor; } set { surfaceColor = value; Invalidate(); } }
        public StatusPill() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var brush = new SolidBrush(surfaceColor)) using (GraphicsPath path = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2)) e.Graphics.FillPath(brush, path); TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
    }

    internal sealed class AppleComboBox : ComboBox
    {
        public AppleComboBox() { DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat; BackColor = Color.FromArgb(246, 246, 248); ForeColor = Color.FromArgb(29, 29, 31); Font = new Font("Segoe UI", 9.5f); }
    }

    internal sealed class LogoMark : Control
    {
        public LogoMark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var b = new SolidBrush(Color.FromArgb(0, 113, 227))) e.Graphics.FillEllipse(b, ClientRectangle); using (var pen = new Pen(Color.White, 3)) { e.Graphics.DrawArc(pen, 12, 11, 24, 24, 205, 310); e.Graphics.DrawLine(pen, 24, 12, 24, 27); } }
    }

    internal sealed class DeviceGlyph : Control
    {
        public bool IsMicrophone { get; set; }
        public bool DarkMode { get; set; }
        public DeviceGlyph() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var b = new SolidBrush(DarkMode ? Color.FromArgb(32, 55, 80) : Color.FromArgb(235, 244, 255))) e.Graphics.FillEllipse(b, ClientRectangle); using (var pen = new Pen(Color.FromArgb(35, 140, 255), 2.4f)) { if (IsMicrophone) { e.Graphics.DrawArc(pen, 17, 10, 14, 22, 0, 180); e.Graphics.DrawLine(pen, 14, 22, 14, 25); e.Graphics.DrawArc(pen, 14, 16, 20, 18, 0, 180); e.Graphics.DrawLine(pen, 24, 34, 24, 39); e.Graphics.DrawLine(pen, 19, 39, 29, 39); } else { e.Graphics.DrawArc(pen, 12, 12, 24, 25, 190, 160); e.Graphics.DrawLine(pen, 12, 25, 12, 34); e.Graphics.DrawLine(pen, 36, 25, 36, 34); e.Graphics.DrawLine(pen, 12, 34, 17, 34); e.Graphics.DrawLine(pen, 31, 34, 36, 34); } } }
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
        public static string Language
        {
            get
            {
                string value = Read("Language").ToLowerInvariant();
                if (value == "ru" || value == "en") return value;
                return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
            }
        }
        public static bool DarkMode
        {
            get
            {
                string saved = Read("DarkMode");
                if (!string.IsNullOrEmpty(saved)) return saved == "1";
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                    return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
            }
        }

        public static void SaveUi(string language, bool darkMode)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                key.SetValue("Language", language, RegistryValueKind.String);
                key.SetValue("DarkMode", darkMode ? 1 : 0, RegistryValueKind.DWord);
            }
        }

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
