using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class ChatGPTApiOnly
{
    private static string StartupFailureMessage(string stage, Exception exception)
    {
        try
        {
            string directory = Path.Combine(ConfigStore.ConfigDirectory, "launcher-profiles", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "launcher.log"),
                String.Format("[{0:O}] stage={1}{2}{3}{4}", DateTime.Now, stage, Environment.NewLine, exception, Environment.NewLine), Encoding.UTF8);
        }
        catch { }
        return String.Format("启动失败（{0}）：{1}{2}详细诊断已写入 .codex\\launcher-profiles\\logs\\launcher.log", stage, exception.Message, Environment.NewLine);
    }
    private const string PackageRegistryPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private const string ResolverRules =
        "MAP chatgpt.com 0.0.0.0, " +
        "MAP *.chatgpt.com 0.0.0.0, " +
        "MAP chat.openai.com 0.0.0.0, " +
        "MAP *.openai.com 0.0.0.0, " +
        "MAP *.oaistatic.com 0.0.0.0";

    [STAThread]
    private static void Main(string[] args)
    {
#if PROVIDER_SYNC_TEST
        if (args.Length == 1 && args[0] == "--test-loading-ui")
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LoadingForm(ConfigStore.Load(), true));
            return;
        }

        if (args.Length == 1 && args[0] == "--test-provider-sync")
        {
            try
            {
                string progressPath = Environment.GetEnvironmentVariable("CHATGPT_API_ONLY_PROGRESS_FILE");
                IProgress<ProviderSyncProgress> progress = String.IsNullOrWhiteSpace(progressPath)
                    ? null
                    : new InlineProgress<ProviderSyncProgress>(delegate(ProviderSyncProgress value)
                    {
                        File.AppendAllText(progressPath,
                            String.Format("{0}/{1}{2}", value.Completed, value.Total, Environment.NewLine));
                    });
                ProviderSynchronizer.Synchronize(ConfigStore.ConfigDirectory, "custom", progress);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Environment.ExitCode = 1;
            }
            return;
        }
#endif

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!EnsureClientInstalled(null)) return;
        ConfigData config = ConfigStore.Load();
        if (!config.IsValid)
        {
            using (var form = new ConfigForm(config))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return;
                }
            }
            config = ConfigStore.Load();
        }

        Application.Run(new LoadingForm(config));
    }

    private static Icon LoadApplicationIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    private static ProcessStartInfo ClientStartInfo(string executable, ConfigData config)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = config.OfficialMode ? String.Empty : Quote("--host-resolver-rules=" + ResolverRules),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false
        };
        if (config.OfficialMode)
        {
            start.EnvironmentVariables.Remove("OPENAI_API_KEY");
            start.EnvironmentVariables.Remove("OPENAI_BASE_URL");
            string proxy = ConfigStore.NormalizeOfficialProxyUrl(config.OfficialProxyUrl);
            if (proxy.Length > 0)
            {
                start.Arguments = Quote("--proxy-server=" + proxy);
                foreach (var variable in OfficialProxyEnvironment(proxy))
                    start.EnvironmentVariables[variable.Key] = variable.Value;
            }
        }
        return start;
    }

    private static Dictionary<string, string> OfficialProxyEnvironment(string proxy)
    {
        return new Dictionary<string, string>
        {
            { "HTTP_PROXY", proxy }, { "HTTPS_PROXY", proxy }, { "ALL_PROXY", proxy },
            { "NO_PROXY", "localhost,127.0.0.1,::1" }, { "NODE_USE_ENV_PROXY", "1" }
        };
    }

    private static bool EnsureClientInstalled(IWin32Window owner)
    {
        string packageRoot;
        return EnsureClientInstalled(FindLatestChatGptExecutable(out packageRoot) != null,
            delegate
            {
                return MessageBox.Show(owner,
                    "\u672a\u68c0\u6d4b\u5230 ChatGPT \u5ba2\u6237\u7aef\u3002\u662f\u5426\u6253\u5f00 Microsoft Store \u5b89\u88c5\uff1f" +
                    Environment.NewLine + "\u5b89\u88c5\u5b8c\u6210\u540e\uff0c\u8bf7\u91cd\u65b0\u8fd0\u884c\u672c\u542f\u52a8\u5668\u3002",
                    "\u5b89\u88c5 ChatGPT", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            },
            delegate { ClientStore.Open(false, owner); });
    }

    private static bool EnsureClientInstalled(bool installed, Func<DialogResult> confirm, Action openStore)
    {
        if (installed) return true;
        if (confirm() == DialogResult.OK) openStore();
        return false;
    }

    private static class ClientStore
    {
        private const string ProductId = "9PLM9XGG6VKS";
        private const string WebUrl = "https://apps.microsoft.com/detail/" + ProductId;

        internal static Button CreateButton(bool updates, Point location, int tabIndex, IWin32Window owner)
        {
            string description = updates
                ? "\u6253\u5f00 Microsoft Store \u7684\u4e0b\u8f7d\u548c\u66f4\u65b0\u9875\uff0c\u5728\u5546\u5e97\u4e2d\u68c0\u67e5 ChatGPT \u66f4\u65b0\u3002"
                : "\u6253\u5f00 ChatGPT \u7684 Microsoft Store \u9875\u9762\uff0c\u53ef\u5b89\u88c5\u6216\u66f4\u65b0\u5ba2\u6237\u7aef\u3002";
            var button = new Button
            {
                Location = location,
                Size = new Size(112, 30),
                Text = updates ? "\u68c0\u67e5\u66f4\u65b0" : "\u6253\u5f00\u5e94\u7528\u5546\u5e97",
                TabIndex = tabIndex,
                AccessibleDescription = description
            };
            button.AccessibleName = button.Text;
            var tooltip = new ToolTip();
            tooltip.SetToolTip(button, description);
            button.Disposed += delegate { tooltip.Dispose(); };
            button.Click += delegate { Open(updates, owner); };
            return button;
        }

        internal static void Open(bool updates, IWin32Window owner)
        {
            try
            {
                OpenLink(updates, delegate(string uri)
                {
                    if (uri.StartsWith("ms-windows-store:", StringComparison.OrdinalIgnoreCase))
                    {
                        using (RegistryKey protocol = Registry.ClassesRoot.OpenSubKey("ms-windows-store"))
                        {
                            if (protocol == null) throw new InvalidOperationException("Microsoft Store \u672a\u5b89\u88c5\u3002");
                        }
                    }
                    Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
                });
            }
            catch (Exception exception)
            {
                MessageBox.Show(owner, exception.Message, "ChatGPT API Only", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void OpenLink(bool updates, Action<string> launch)
        {
            string uri = updates ? "ms-windows-store://downloadsandupdates"
                : "ms-windows-store://pdp/?ProductId=" + ProductId;
            try { launch(uri); }
            catch (Exception storeException)
            {
                try { launch(WebUrl); }
                catch (Exception browserException)
                {
                    throw new InvalidOperationException(
                        "\u65e0\u6cd5\u6253\u5f00\u5e94\u7528\u5546\u5e97\u6216\u6d4f\u89c8\u5668\u3002\u8bf7\u624b\u52a8\u8bbf\u95ee\uff1a" + Environment.NewLine + WebUrl +
                        Environment.NewLine + storeException.Message + Environment.NewLine + browserException.Message,
                        browserException);
                }
            }
        }
    }

    private sealed class LoadingForm : Form
    {
        private static readonly TimeSpan ExpectedStartupTime = TimeSpan.FromSeconds(4);

        private readonly Label statusLabel;
        private readonly ProgressBar progressBar;
        private readonly Timer pollTimer;
        private readonly Stopwatch elapsed;
        private ConfigData config;
        private string packageRoot;
        private int lastSecondsRemaining = -1;
        private bool openingConfiguration;

#if PROVIDER_SYNC_TEST
        private bool simulateStartup;
#endif

        internal LoadingForm(ConfigData initialConfig)
        {
            config = initialConfig;
            Text = "ChatGPT API Only";
            ClientSize = new Size(420, 164);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = true;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = SystemColors.Window;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            KeyPreview = true;
            AccessibleName = "ChatGPT API Only launcher";
            AccessibleDescription = "Shows startup progress. Press Space to configure the custom API.";
            KeyDown += LoadingFormOnKeyDown;

            Icon = LoadApplicationIcon();

            var iconBox = new PictureBox
            {
                Location = new Point(24, 22),
                Size = new Size(32, 32),
                SizeMode = PictureBoxSizeMode.Zoom,
                TabStop = false
            };
            if (Icon != null)
            {
                iconBox.Image = Icon.ToBitmap();
            }

            var titleLabel = new Label
            {
                AutoSize = true,
                Location = new Point(72, 20),
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = SystemColors.ControlText,
                Text = "ChatGPT API Only"
            };

            statusLabel = new Label
            {
                AutoEllipsis = true,
                Location = new Point(72, 47),
                Size = new Size(320, 22),
                ForeColor = SystemColors.GrayText,
                Text = "\u6b63\u5728\u542f\u52a8 ChatGPT\uff0c\u9884\u8ba1\u7ea6 4 \u79d2",
                AccessibleName = "Startup status"
            };

            progressBar = new ProgressBar
            {
                Location = new Point(24, 88),
                Size = new Size(368, 8),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous,
                AccessibleName = "ChatGPT startup progress"
            };

            var configureButton = new Button
            {
                Location = new Point(280, 112),
                Size = new Size(112, 30),
                Text = "\u8fde\u63a5\u8bbe\u7f6e",
                TabIndex = 2,
                AccessibleName = "\u8fde\u63a5\u8bbe\u7f6e",
                AccessibleDescription = "\u6253\u5f00\u81ea\u5b9a\u4e49 API \u914d\u7f6e\u3002\u4e5f\u53ef\u6309\u7a7a\u683c\u952e\u3002"
            };
            configureButton.Click += delegate { OpenConfiguration(); };

            Controls.Add(iconBox);
            Controls.Add(titleLabel);
            Controls.Add(statusLabel);
            Controls.Add(progressBar);
            Controls.Add(ClientStore.CreateButton(false, new Point(24, 112), 0, this));
            Controls.Add(ClientStore.CreateButton(true, new Point(144, 112), 1, this));
            Controls.Add(configureButton);
            ActiveControl = configureButton;

            elapsed = new Stopwatch();
            pollTimer = new Timer { Interval = 200 };
            pollTimer.Tick += PollTimerOnTick;
        }

#if PROVIDER_SYNC_TEST
        internal LoadingForm(ConfigData initialConfig, bool simulateStartup)
            : this(initialConfig)
        {
            this.simulateStartup = simulateStartup;
        }
#endif

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ResetProgress();
#if PROVIDER_SYNC_TEST
            if (simulateStartup) return;
#endif
            BeginInvoke(new MethodInvoker(StartChatGpt));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pollTimer.Dispose();
                progressBar.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ResetProgress()
        {
            elapsed.Reset();
            elapsed.Start();
            lastSecondsRemaining = -1;
            progressBar.Value = 0;
            progressBar.Style = config.OfficialMode ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            statusLabel.Text = config.OfficialMode ? "\u6b63\u5728\u542f\u52a8 ChatGPT\uff08\u5b98\u65b9\u8d26\u53f7\uff09" : "\u6b63\u5728\u542f\u52a8 ChatGPT\uff0c\u9884\u8ba1\u7ea6 4 \u79d2";
            pollTimer.Start();
        }

        private void StartChatGpt()
        {
            string stage = "查找 ChatGPT 安装";
            try
            {
                string executable = FindLatestChatGptExecutable(out packageRoot);
                if (executable == null)
                {
                    pollTimer.Stop();
                    EnsureClientInstalled(this);
                    Close();
                    return;
                }

                stage = "创建 ChatGPT 进程";
                try
                {
                    Process.Start(ClientStartInfo(executable, config));
                }
                catch (System.ComponentModel.Win32Exception exception)
                {
                    if (exception.NativeErrorCode != 5) throw;
                    stage = "通过 Windows 应用激活 ChatGPT";
                    Process.Start(ClientActivationInfo(packageRoot, config));
                }
            }
            catch (Exception exception)
            {
                pollTimer.Stop();
                progressBar.Value = 0;
                MessageBox.Show(this, StartupFailureMessage(stage, exception), "ChatGPT API Only",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void LoadingFormOnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Space || openingConfiguration || ActiveControl is Button)
            {
                return;
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
            OpenConfiguration();
        }

        private void OpenConfiguration()
        {
            if (openingConfiguration) return;
            openingConfiguration = true;
            pollTimer.Stop();
            statusLabel.Text = "\u6b63\u5728\u6253\u5f00\u8fde\u63a5\u8bbe\u7f6e\u2026";
            Refresh();

            Hide();

            config = ConfigStore.Load();
            using (var form = new ConfigForm(config))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    Close();
                    return;
                }
            }

            config = ConfigStore.Load();
            openingConfiguration = false;
            Show();
            Activate();
            ResetProgress();
            BeginInvoke(new MethodInvoker(StartChatGpt));
        }

        private void PollTimerOnTick(object sender, EventArgs e)
        {
#if PROVIDER_SYNC_TEST
            if (!simulateStartup)
            {
#endif
            if (FindVisibleChatGptWindow() != IntPtr.Zero)
            {
                pollTimer.Stop();
                progressBar.Value = 100;
                statusLabel.Text = "ChatGPT \u5df2\u542f\u52a8";
                Close();
                return;
            }
#if PROVIDER_SYNC_TEST
            }
#endif

            if (config.OfficialMode) return;
            double elapsedSeconds = elapsed.Elapsed.TotalSeconds;
            double expectedSeconds = ExpectedStartupTime.TotalSeconds;
            int progress = Math.Min(95, (int)Math.Round(elapsedSeconds / expectedSeconds * 100));
            progressBar.Value = Math.Max(progressBar.Value, progress);

            int secondsRemaining = Math.Max(0, (int)Math.Ceiling(expectedSeconds - elapsedSeconds));
            if (secondsRemaining != lastSecondsRemaining)
            {
                lastSecondsRemaining = secondsRemaining;
                statusLabel.Text = secondsRemaining > 0
                    ? String.Format("\u6b63\u5728\u542f\u52a8 ChatGPT\uff0c\u9884\u8ba1\u8fd8\u9700 {0} \u79d2", secondsRemaining)
                    : "\u6b63\u5728\u542f\u52a8 ChatGPT\uff0c\u5373\u5c06\u5b8c\u6210";
            }
        }
    }

    private sealed class ConfigForm : Form
    {
        private bool saveAndLaunch = true;
        private readonly TextBox providerNameTextBox;
        private readonly TextBox baseUrlTextBox;
        private readonly TextBox apiKeyTextBox;
        private readonly TextBox modelTextBox;
        private readonly ComboBox reasoningComboBox;
        private readonly ErrorProvider errors;
        private readonly Button saveButton;
        private readonly Button officialSaveButton;
        private readonly Button customSaveButton;
        private readonly Button cancelButton;
        private readonly Button repairButton;
        private readonly Button storeButton;
        private readonly Button updatesButton;
        private readonly Label repairProgressCaption;
        private readonly ProgressBar repairProgressBar;
        private readonly Label repairProgressLabel;
        private bool repairInProgress;
        private readonly TabControl modeTabs;
        private readonly TabPage officialTab;
        private readonly TabPage customTab;
        private readonly TextBox officialProxyTextBox;
        private readonly ComboBox officialAccountComboBox;
        private readonly ComboBox customProviderComboBox;
        private Dictionary<string, object> profileDraft;
        private bool updatingProfileSelectors;
        private readonly Label accountStatus;

        internal ConfigForm(ConfigData config)
        {
            Text = "\u914d\u7f6e\u81ea\u5b9a\u4e49 API";
            Icon = LoadApplicationIcon();
            ClientSize = new Size(572, 410);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = SystemColors.Window;
            AutoScaleMode = AutoScaleMode.Dpi;

            var heading = new Label
            {
                AutoSize = true,
                Location = new Point(24, 16),
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point),
                Text = "\u914d\u7f6e\u81ea\u5b9a\u4e49 API"
            };
            var intro = new Label
            {
                Location = new Point(24, 44),
                Size = new Size(520, 22),
                ForeColor = SystemColors.GrayText,
                Text = "\u4fdd\u5b58\u540e\u5c06\u7ee7\u7eed\u542f\u52a8 ChatGPT\u3002\u914d\u7f6e\u4fdd\u5b58\u5728\u7528\u6237\u76ee\u5f55\u7684 .codex \u6587\u4ef6\u5939\u3002"
            };

            providerNameTextBox = AddField("\u63d0\u4f9b\u8005\u540d\u79f0", 98,
                String.IsNullOrWhiteSpace(config.ProviderName) ? "custom" : config.ProviderName, 0);
            providerNameTextBox.Width = 286;
            repairButton = new Button
            {
                Location = new Point(444, 96),
                Size = new Size(104, 27),
                Text = "\u4fee\u590d\u5bf9\u8bdd",
                TabIndex = 1,
                AccessibleName = "\u4fee\u590d\u5bf9\u8bdd",
                AccessibleDescription = "\u5c06\u672c\u5730\u5386\u53f2\u5bf9\u8bdd\u4fee\u590d\u5230\u5f53\u524d API \u63d0\u4f9b\u8005\u3002"
            };
            repairButton.Click += RepairButtonOnClick;
            baseUrlTextBox = AddField("API \u5730\u5740", 146, config.BaseUrl ?? String.Empty, 2);
            apiKeyTextBox = AddField("API Key", 194, config.ApiKey ?? String.Empty, 3);
            modelTextBox = AddField("\u6a21\u578b\u540d", 242, config.Model ?? String.Empty, 4);
            reasoningComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
            reasoningComboBox.Items.AddRange(new object[] { "none", "minimal", "low", "medium", "high", "xhigh" });
            AddField("\u601d\u8003\u5c42\u7ea7", 290,
                String.IsNullOrWhiteSpace(config.ReasoningEffort) ? "medium" : config.ReasoningEffort, 5,
                reasoningComboBox);
            reasoningComboBox.AccessibleDescription = "\u9ed8\u8ba4 medium\uff08\u4e2d\u7b49\uff09\uff0c\u53ef\u9009\u62e9\u6216\u8f93\u5165\u6a21\u578b\u652f\u6301\u7684\u5c42\u7ea7\u3002";
            TextBox authModeTextBox = AddField("\u8ba4\u8bc1\u6a21\u5f0f", 338, "apikey", 6);
            authModeTextBox.Enabled = false;
            authModeTextBox.BackColor = SystemColors.Control;

            repairProgressCaption = new Label
            {
                AutoSize = true,
                Location = new Point(24, 378),
                Text = "\u5bf9\u8bdd\u4fee\u590d",
                Visible = false
            };
            repairProgressBar = new ProgressBar
            {
                Location = new Point(150, 376),
                Size = new Size(286, 18),
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                Style = ProgressBarStyle.Continuous,
                AccessibleName = "\u5bf9\u8bdd\u4fee\u590d\u8fdb\u5ea6",
                Visible = false
            };
            repairProgressLabel = new Label
            {
                AutoEllipsis = true,
                Location = new Point(444, 377),
                Size = new Size(104, 18),
                ForeColor = SystemColors.GrayText,
                Text = String.Empty,
                AccessibleName = "\u5bf9\u8bdd\u4fee\u590d\u72b6\u6001",
                Visible = false
            };

            saveButton = new Button
            {
                Location = new Point(368, 374),
                Size = new Size(112, 30),
                Text = "启动",
                TabIndex = 9
            };
            saveButton.Click += delegate { saveAndLaunch = true; SaveButtonOnClick(null, EventArgs.Empty); };

            officialSaveButton = new Button { Location = new Point(330, 300), Size = new Size(100, 30), Text = "保存配置", TabIndex = 10 };
            customSaveButton = new Button { Location = new Point(330, 300), Size = new Size(100, 30), Text = "保存配置", TabIndex = 10 };
            officialSaveButton.Click += delegate { saveAndLaunch = false; SaveButtonOnClick(null, EventArgs.Empty); };
            customSaveButton.Click += delegate { saveAndLaunch = false; SaveButtonOnClick(null, EventArgs.Empty); };

            cancelButton = new Button
            {
                Location = new Point(486, 374),
                Size = new Size(62, 30),
                Text = "\u53d6\u6d88",
                DialogResult = DialogResult.Cancel,
                TabIndex = 11
            };

            errors = new ErrorProvider { BlinkStyle = ErrorBlinkStyle.NeverBlink };
            errors.ContainerControl = this;
            AcceptButton = saveButton;
            CancelButton = cancelButton;
            storeButton = ClientStore.CreateButton(false, new Point(24, 374), 7, this);
            updatesButton = ClientStore.CreateButton(true, new Point(144, 374), 8, this);
            Controls.Add(heading);
            Controls.Add(intro);
            Controls.Add(repairButton);
            Controls.Add(repairProgressCaption);
            Controls.Add(repairProgressBar);
            Controls.Add(repairProgressLabel);
            Controls.Add(saveButton);
            Controls.Add(cancelButton);
            Controls.Add(storeButton);
            Controls.Add(updatesButton);

            // Reuse the existing API form inside its tab; shared actions stay outside.
            modeTabs = new TabControl { Location = new Point(16, 78), Size = new Size(540, 390), TabIndex = 0 };
            officialTab = new TabPage("\u5b98\u65b9\u8d26\u53f7") { UseVisualStyleBackColor = true };
            customTab = new TabPage("\u81ea\u5b9a\u4e49 API") { UseVisualStyleBackColor = true };
            profileDraft = ConfigStore.ReadEditableProfiles();
            officialAccountComboBox = new ComboBox { Location = new Point(20, 8), Size = new Size(310, 23), DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "官方账号配置" };
            customProviderComboBox = new ComboBox { Location = new Point(20, 8), Size = new Size(300, 23), DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "自定义 API 配置" };
            officialTab.Controls.Add(officialAccountComboBox);
            customTab.Controls.Add(customProviderComboBox);
            var addOfficial = new Button { Location = new Point(338, 8), Size = new Size(78, 24), Text = "添加账号" };
            var addCustom = new Button { Location = new Point(328, 8), Size = new Size(78, 24), Text = "添加" };
            var copyCustom = new Button { Location = new Point(412, 8), Size = new Size(78, 24), Text = "复制" };
            var deleteCustom = new Button { Location = new Point(20, 38), Size = new Size(78, 24), Text = "删除" };
            officialTab.Controls.Add(addOfficial); customTab.Controls.Add(addCustom); customTab.Controls.Add(copyCustom); customTab.Controls.Add(deleteCustom);
            PopulateProfileSelectors();
            addOfficial.Click += delegate { AddProfileFromUi(true, null); };
            addCustom.Click += delegate { AddProfileFromUi(false, null); };
            copyCustom.Click += delegate { AddProfileFromUi(false, SelectedProfileId(false)); };
            deleteCustom.Click += delegate { DeleteProfileFromUi(false); };
            var deleteOfficial = new Button { Location = new Point(20, 38), Size = new Size(78, 24), Text = "删除" };
            var renameOfficial = new Button { Location = new Point(104, 38), Size = new Size(78, 24), Text = "重命名" };
            var renameCustom = new Button { Location = new Point(104, 38), Size = new Size(78, 24), Text = "重命名" };
            officialTab.Controls.Add(deleteOfficial);
            officialTab.Controls.Add(renameOfficial);
            customTab.Controls.Add(renameCustom);
            deleteOfficial.Click += delegate { DeleteProfileFromUi(true); };
            renameOfficial.Click += delegate { RenameProfileFromUi(true); };
            renameCustom.Click += delegate { RenameProfileFromUi(false); };
            modeTabs.TabPages.Add(officialTab);
            modeTabs.TabPages.Add(customTab);
            officialTab.Controls.Add(officialSaveButton);
            customTab.Controls.Add(customSaveButton);
            var fields = new List<Control>();
            foreach (Control control in Controls)
                if (control.Top >= 96 && control.Top < 370) fields.Add(control);
            foreach (Control control in fields)
            {
                control.Location = new Point(control.Left - 20, control.Top - 18);
                customTab.Controls.Add(control);
            }
            accountStatus = new Label
            {
                Location = new Point(20, 74), Size = new Size(480, 32),
                Text = config.HasOfficialCredentials
                    ? "\u68c0\u6d4b\u5230\u5b98\u65b9\u767b\u5f55\u51ed\u8bc1"
                    : "\u5c1a\u672a\u68c0\u6d4b\u5230\u5b98\u65b9\u767b\u5f55\u51ed\u8bc1",
                Font = new Font(Font, FontStyle.Bold)
            };
            officialTab.Controls.Add(accountStatus);
            officialTab.Controls.Add(new Label
            {
                Location = new Point(20, 112), Size = new Size(480, 78),
                Text = "添加账号后点击“应用并启动”，在官方客户端完成登录。\r\n" +
                    "下次打开连接设置时自动识别并收录登录账号。\r\n\r\n" +
                    "切换模式会保留另一种模式的配置，不会修改历史对话。"
            });
            officialAccountComboBox.SelectedIndexChanged += delegate {
                if (!updatingProfileSelectors) profileDraft["selected_official"] = SelectedProfileId(true) ?? String.Empty;
                UpdateAccountStatus();
            };
            customProviderComboBox.SelectedIndexChanged += delegate
            {
                if (updatingProfileSelectors) return;
                StoreCustomFields();
                profileDraft["selected_custom"] = SelectedProfileId(false) ?? String.Empty;
                LoadCustomProfileIntoFields();
            };
            officialTab.Controls.Add(new Label
            {
                Location = new Point(20, 220), AutoSize = true,
                Text = "HTTP 代理（可选）"
            });
            officialProxyTextBox = new TextBox
            {
                Location = new Point(20, 244), Size = new Size(480, 23),
                Text = config.OfficialProxyUrl ?? String.Empty,
                AccessibleName = "官方账号 HTTP 代理", TabIndex = 0
            };
            officialTab.Controls.Add(officialProxyTextBox);
            officialTab.Controls.Add(new Label
            {
                Location = new Point(20, 278), Size = new Size(480, 66),
                Text = "地址格式：http://主机:端口；留空不设置独立代理。\r\n" +
                    "仅作用于官方客户端及其子进程，不修改系统代理。\r\n" +
                    "本地代理软件需保持运行。"
            });
            Text = heading.Text = "ChatGPT \u8fde\u63a5\u8bbe\u7f6e";
            intro.Text = "\u9009\u62e9\u767b\u5f55\u65b9\u5f0f\uff0c\u70b9\u51fb\u201c\u5e94\u7528\u5e76\u542f\u52a8\u201d\u540e\u751f\u6548\u3002";
            if (!String.IsNullOrEmpty(config.ProfileError))
            {
                intro.Text = "\u65e0\u6cd5\u8bfb\u53d6\u5df2\u4fdd\u5b58\u7684\u6a21\u5f0f\u914d\u7f6e\uff0c\u8bf7\u68c0\u67e5 launcher-profiles/modes.json\u3002";
                intro.ForeColor = Color.Firebrick;
            }
            saveButton.Text = "启动";
            modeTabs.SelectedTab = config.OfficialMode || config.AuthMode == "chatgpt" ? officialTab : customTab;
            Controls.Add(modeTabs);
            repairProgressCaption.Top += 100;
            repairProgressBar.Top += 100;
            repairProgressLabel.Top += 100;
            SetFooterTop(474);
            ClientSize = new Size(572, 510);
            UpdateAccountStatus();
        }

        private async void RepairButtonOnClick(object sender, EventArgs e)
        {
            ShowRepairProgress();
            SetRepairBusy(true);
            repairProgressBar.Maximum = 1;
            repairProgressBar.Value = 0;
            repairProgressLabel.Text = "\u6b63\u5728\u7edf\u8ba1\u2026";
            var progress = new LatestProviderSyncProgress();
            ProviderSyncProgress displayedProgress = null;
            var progressTimer = new Timer { Interval = 100 };
            progressTimer.Tick += delegate
            {
                ProviderSyncProgress value = progress.Latest;
                if (value == null || Object.ReferenceEquals(value, displayedProgress)) return;
                displayedProgress = value;
                repairProgressCaption.Text = value.Phase;
                int maximum = Math.Max(1, value.Total);
                if (repairProgressBar.Value > maximum) repairProgressBar.Value = 0;
                repairProgressBar.Maximum = maximum;
                repairProgressBar.Value = Math.Min(repairProgressBar.Maximum, value.Completed);
                repairProgressLabel.Text = String.Format("{0}/{1}", value.Completed, value.Total);
            };
            progressTimer.Start();
            Refresh();

            try
            {
                ProviderSyncResult result = await Task.Run(delegate
                {
                    return ProviderSynchronizer.Synchronize(
                        ConfigStore.ConfigDirectory, "custom", progress);
                });

                progressTimer.Stop();
                repairProgressCaption.Text = "\u4fee\u590d\u5b8c\u6210";
                repairProgressBar.Value = 0;
                repairProgressBar.Maximum = Math.Max(1, result.Total);
                repairProgressBar.Value = repairProgressBar.Maximum;
                repairProgressLabel.Text = String.Format("{0}/{1}", result.Total, result.Total);
                MessageBox.Show(this,
                    result.Total == 0
                        ? "\u5bf9\u8bdd\u65e0\u9700\u4fee\u590d\uff0c\u5f53\u524d\u5df2\u662f\u6b63\u786e\u72b6\u6001\u3002"
                        : String.Format("\u5bf9\u8bdd\u4fee\u590d\u5b8c\u6210\uff0c\u5df2\u5904\u7406 {0} \u9879\u3002", result.Total),
                    "ChatGPT API Only", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                progressTimer.Stop();
                repairProgressLabel.Text = "\u4fee\u590d\u5931\u8d25";
                MessageBox.Show(this,
                    "\u65e0\u6cd5\u4fee\u590d\u5bf9\u8bdd\uff1a" + Environment.NewLine + exception.Message,
                    "ChatGPT API Only", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                progressTimer.Dispose();
                SetRepairBusy(false);
                HideRepairProgress();
            }
        }

        private void ShowRepairProgress()
        {
            repairProgressCaption.Text = "\u626b\u63cf\u5bf9\u8bdd";
            SetFooterTop(514);
            ClientSize = new Size(572, 550);
            repairProgressCaption.Visible = true;
            repairProgressBar.Visible = true;
            repairProgressLabel.Visible = true;
        }

        private void HideRepairProgress()
        {
            repairProgressCaption.Visible = false;
            repairProgressBar.Visible = false;
            repairProgressLabel.Visible = false;
            repairProgressBar.Maximum = 1;
            repairProgressBar.Value = 0;
            repairProgressLabel.Text = String.Empty;
            SetFooterTop(474);
            ClientSize = new Size(572, 510);
        }

        private void SetFooterTop(int top)
        {
            foreach (Button button in new[] { storeButton, updatesButton, saveButton, officialSaveButton, customSaveButton, cancelButton })
                button.Top = top;
        }

        private void SetRepairBusy(bool busy)
        {
            repairInProgress = busy;
            modeTabs.Enabled = !busy;
            providerNameTextBox.Enabled = !busy;
            baseUrlTextBox.Enabled = !busy;
            apiKeyTextBox.Enabled = !busy;
            modelTextBox.Enabled = !busy;
            reasoningComboBox.Enabled = !busy;
            repairButton.Enabled = !busy;
            storeButton.Enabled = !busy;
            updatesButton.Enabled = !busy;
            saveButton.Enabled = !busy;
            cancelButton.Enabled = !busy;
            UseWaitCursor = busy;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (repairInProgress)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        private TextBox AddField(string labelText, int top, string value, int tabIndex)
        {
            var textBox = new TextBox();
            AddField(labelText, top, value, tabIndex, textBox);
            return textBox;
        }

        private void AddField(string labelText, int top, string value, int tabIndex, Control field)
        {
            var label = new Label
            {
                AutoSize = true,
                Location = new Point(24, top + 6),
                Text = labelText
            };
            field.Location = new Point(150, top);
            field.Size = new Size(398, 23);
            field.Text = value;
            field.TabIndex = tabIndex;
            field.AccessibleName = labelText;
            Controls.Add(label);
            Controls.Add(field);
        }

        private string SelectedProfileId(bool official)
        {
            ComboBox box = official ? officialAccountComboBox : customProviderComboBox;
            return box.SelectedItem == null ? null : ((ProfileChoice)box.SelectedItem).Id;
        }

        private void PopulateProfileSelectors()
        {
            updatingProfileSelectors = true;
            try
            {
            officialAccountComboBox.Items.Clear(); customProviderComboBox.Items.Clear();
            object value;
            if (profileDraft.TryGetValue("official_accounts", out value)) foreach (object item in (object[])value) officialAccountComboBox.Items.Add(ProfileChoice.From((Dictionary<string, object>)item));
            if (profileDraft.TryGetValue("custom_providers", out value)) foreach (object item in (object[])value) customProviderComboBox.Items.Add(ProfileChoice.From((Dictionary<string, object>)item));
            string selected = profileDraft["selected_official"] as string;
            for (int i = 0; i < officialAccountComboBox.Items.Count; i++) if (((ProfileChoice)officialAccountComboBox.Items[i]).Id == selected) officialAccountComboBox.SelectedIndex = i;
            selected = profileDraft["selected_custom"] as string;
            for (int i = 0; i < customProviderComboBox.Items.Count; i++) if (((ProfileChoice)customProviderComboBox.Items[i]).Id == selected) customProviderComboBox.SelectedIndex = i;
            }
            finally { updatingProfileSelectors = false; }
            UpdateAccountStatus();
        }

        private void UpdateAccountStatus()
        {
            if (accountStatus == null) return;
            var profile = ConfigStore.SelectedProfile(profileDraft, "official_accounts");
            accountStatus.Text = profile == null ? "尚未添加账号，点击“添加账号”开始。" :
                profile.ContainsKey("official_auth") ? "已保存本地登录凭据；有效性由官方客户端确认。" :
                "此账号尚未登录或已退出，应用后在客户端登录。";
        }

        private void LoadCustomProfileIntoFields()
        {
            var item = ConfigStore.SelectedProfile(profileDraft, "custom_providers");
            var data = item == null ? new ConfigData() : ConfigStore.ProfileData(item);
            providerNameTextBox.Text = data.ProviderName ?? "custom";
            baseUrlTextBox.Text = data.BaseUrl ?? String.Empty;
            apiKeyTextBox.Text = data.ApiKey ?? String.Empty;
            modelTextBox.Text = data.Model ?? String.Empty;
            reasoningComboBox.Text = data.ReasoningEffort ?? "medium";
        }

        private void StoreCustomFields()
        {
            var item = ConfigStore.SelectedProfile(profileDraft, "custom_providers");
            if (item == null) return;
            ConfigStore.UpdateCustomDraft(item, new ConfigData {
                ProviderName = providerNameTextBox.Text.Trim(), BaseUrl = baseUrlTextBox.Text.Trim(),
                ApiKey = apiKeyTextBox.Text.Trim(), Model = modelTextBox.Text.Trim(),
                ReasoningEffort = reasoningComboBox.Text.Trim()
            });
        }

        private void AddProfileFromUi(bool official, string copyId)
        {
            string name = PromptText(official ? "账号名称" : "API 配置名称", official ? "官方账号" : "自定义 API");
            if (name == null) return;
            try { StoreCustomFields(); ConfigStore.AddProfile(profileDraft, official, name, copyId); PopulateProfileSelectors(); LoadCustomProfileIntoFields(); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "ChatGPT API Only", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void DeleteProfileFromUi(bool official)
        {
            string id = SelectedProfileId(official); if (id == null) return;
            try
            {
                StoreCustomFields();
                ConfigStore.DeleteProfile(profileDraft, official, id);
                PopulateProfileSelectors();
                LoadCustomProfileIntoFields();
                var next = ConfigStore.SelectedProfile(profileDraft, official ? "official_accounts" : "custom_providers");
                MessageBox.Show(this, next == null ? "已从草稿删除最后一项配置。请添加配置，或选择另一种模式后应用。" :
                    "已从草稿删除，自动选择“" + next["name"] + "”。点击应用并启动后保存。",
                    "ChatGPT API Only", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "ChatGPT API Only", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void RenameProfileFromUi(bool official)
        {
            var profile = ConfigStore.SelectedProfile(profileDraft, official ? "official_accounts" : "custom_providers");
            if (profile == null) return;
            string name = PromptText("配置名称", profile["name"] as string);
            if (name == null) return;
            profile["name"] = name;
            PopulateProfileSelectors();
        }

        private static string PromptText(string title, string initial)
        {
            using (var form = new Form { Text = title, ClientSize = new Size(330, 105), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false })
            using (var input = new TextBox { Location = new Point(12, 12), Size = new Size(306, 23), Text = initial })
            using (var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(166, 58), Size = new Size(72, 28) })
            using (var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(246, 58), Size = new Size(72, 28) })
            { form.Controls.Add(input); form.Controls.Add(ok); form.Controls.Add(cancel); form.AcceptButton = ok; form.CancelButton = cancel; return form.ShowDialog() == DialogResult.OK && !String.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null; }
        }

        private sealed class ProfileChoice
        {
            internal string Id; internal string Name;
            public override string ToString() { return Name; }
            internal static ProfileChoice From(Dictionary<string, object> profile)
            {
                string name = profile.ContainsKey("name") ? profile["name"] as string : "未命名配置";
                string email = profile.ContainsKey("email") ? profile["email"] as string : null;
                string accountName = profile.ContainsKey("account_name") ? profile["account_name"] as string : null;
                string account = profile.ContainsKey("account_id") ? profile["account_id"] as string : null;
                string detail = email ?? accountName ?? account;
                return new ProfileChoice { Id = (string)profile["id"], Name = name + (String.IsNullOrEmpty(detail) || detail == name ? "" : " — " + detail) };
            }
        }

        private void SaveButtonOnClick(object sender, EventArgs e)
        {
            errors.Clear();
            if (modeTabs.SelectedTab == officialTab)
            {
                string stage = "校验官方代理";
                try
                {
                    string proxy = ConfigStore.NormalizeOfficialProxyUrl(officialProxyTextBox.Text);
                    if (SelectedProfileId(true) == null)
                    {
                        ConfigStore.AddProfile(profileDraft, true, "官方账号", null);
                        PopulateProfileSelectors();
                    }
                    stage = "校验模式切换";
                    StoreCustomFields();
                    profileDraft["official_proxy_url"] = proxy;
                    ConfigStore.ValidateProfiles(profileDraft, true, SelectedProfileId(true));
                    stage = "保存官方配置";
                    ConfigStore.ApplyProfiles(profileDraft, true, SelectedProfileId(true));
                    DialogResult = saveAndLaunch ? DialogResult.OK : DialogResult.Cancel;
                    Close();
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, StartupFailureMessage(stage, exception), "ChatGPT API Only", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }
            Control firstInvalid = null;

            ValidateRequired(providerNameTextBox, "\u8bf7\u8f93\u5165\u63d0\u4f9b\u8005\u540d\u79f0\u3002", ref firstInvalid);
            if (!ConfigStore.IsValidBaseUrl(baseUrlTextBox.Text))
            {
                errors.SetError(baseUrlTextBox, "API \u5730\u5740\u5fc5\u987b\u4ee5 https:// \u5f00\u5934\u5e76\u4ee5 /v1 \u7ed3\u5c3e\u3002");
                if (firstInvalid == null) firstInvalid = baseUrlTextBox;
            }
            ValidateRequired(apiKeyTextBox, "\u8bf7\u8f93\u5165 API Key\u3002", ref firstInvalid);
            ValidateRequired(modelTextBox, "\u8bf7\u8f93\u5165\u6a21\u578b\u540d\u3002", ref firstInvalid);
            ValidateRequired(reasoningComboBox, "\u8bf7\u9009\u62e9\u6216\u8f93\u5165\u601d\u8003\u5c42\u7ea7\u3002", ref firstInvalid);

            if (firstInvalid != null)
            {
                firstInvalid.Focus();
                return;
            }

            try
            {
                var data = new ConfigData
                {
                    ProviderName = providerNameTextBox.Text.Trim(),
                    BaseUrl = baseUrlTextBox.Text.Trim(),
                    ApiKey = apiKeyTextBox.Text.Trim(),
                    Model = modelTextBox.Text.Trim(),
                    ReasoningEffort = reasoningComboBox.Text.Trim(),
                    AuthMode = "apikey"
                };
                saveButton.Enabled = false;
                saveButton.Text = "\u6b63\u5728\u4fdd\u5b58\u2026";
                UseWaitCursor = true;
                Refresh();

                if (SelectedProfileId(false) == null)
                    ConfigStore.AddProfile(profileDraft, false, data.ProviderName, null);
                ConfigStore.UpdateCustomDraft(ConfigStore.SelectedProfile(profileDraft, "custom_providers"), data);
                ConfigStore.ValidateProfiles(profileDraft, false, profileDraft["selected_custom"] as string);
                ConfigStore.ApplyProfiles(profileDraft, false, profileDraft["selected_custom"] as string);
                DialogResult = saveAndLaunch ? DialogResult.OK : DialogResult.Cancel;
                Close();
            }
            catch (Exception exception)
            {
                UseWaitCursor = false;
                saveButton.Enabled = true;
                saveButton.Text = "启动";
                MessageBox.Show(this,
                    StartupFailureMessage("保存自定义配置或启动", exception),
                    "ChatGPT API Only", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ValidateRequired(Control textBox, string message, ref Control firstInvalid)
        {
            if (!String.IsNullOrWhiteSpace(textBox.Text)) return;
            errors.SetError(textBox, message);
            if (firstInvalid == null) firstInvalid = textBox;
        }
    }

    private sealed class ConfigData
    {
        internal string ProviderName;
        internal string BaseUrl;
        internal string ApiKey;
        internal string Model;
        internal string ReasoningEffort;
        internal string AuthMode;
        internal string WireApi;
        internal bool? RequiresOpenAiAuth;
        internal bool ConfigReadable;
        internal bool AuthReadable;
        internal bool OfficialMode;
        internal bool HasOfficialCredentials;
        internal bool ActiveOfficialCredentials;
        internal string CredentialsStore;
        internal string ProfileError;
        internal string OfficialProxyUrl;

        internal bool IsValid
        {
            get
            {
                if (!String.IsNullOrEmpty(ProfileError)) return false;
                if (!String.IsNullOrEmpty(CredentialsStore) && CredentialsStore != "file") return false;
                if (OfficialMode)
                {
                    try { ConfigStore.NormalizeOfficialProxyUrl(OfficialProxyUrl); }
                    catch { return false; }
                    return ConfigReadable && AuthReadable && AuthMode == "chatgpt" && ActiveOfficialCredentials;
                }
                bool authModeValid = String.Equals(AuthMode, "apikey", StringComparison.OrdinalIgnoreCase);
                return ConfigReadable && AuthReadable &&
                    !String.IsNullOrWhiteSpace(ProviderName) &&
                    ConfigStore.IsValidBaseUrl(BaseUrl) &&
                    !String.IsNullOrWhiteSpace(ApiKey) &&
                    !String.IsNullOrWhiteSpace(Model) &&
                    !String.IsNullOrWhiteSpace(ReasoningEffort) &&
                    String.Equals(WireApi, "responses", StringComparison.Ordinal) &&
                    RequiresOpenAiAuth == true && authModeValid;
            }
        }
    }

    private static class ConfigStore
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);

        internal static string ConfigDirectory
        {
            get
            {
                string testOverride = Environment.GetEnvironmentVariable("CHATGPT_API_ONLY_CONFIG_DIR");
                return String.IsNullOrWhiteSpace(testOverride)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                    : testOverride;
            }
        }

        private static string ConfigPath { get { return Path.Combine(ConfigDirectory, "config.toml"); } }
        private static string AuthPath { get { return Path.Combine(ConfigDirectory, "auth.json"); } }
        private static string DotEnvPath { get { return Path.Combine(ConfigDirectory, ".env"); } }
        private static string ProfilesPath { get { return Path.Combine(ConfigDirectory, "launcher-profiles", "modes.json"); } }

        internal static ConfigData Load()
        {
            var data = new ConfigData();
            LoadToml(data);
            LoadAuth(data);
            Dictionary<string, object> profiles;
            try { profiles = ReadProfiles(); }
            catch (Exception exception) { data.ProfileError = exception.Message; return data; }
            data.OfficialProxyUrl = ProfileString(profiles, "official_proxy_url");
            if (data.OfficialMode)
            {
                string savedProviders = SelectedValue(profiles, "model_providers_toml");
                if (!String.IsNullOrWhiteSpace(savedProviders))
                {
                    var saved = new ConfigData();
                    LoadTomlText(saved, savedProviders);
                    data.ProviderName = saved.ProviderName;
                    data.BaseUrl = saved.BaseUrl;
                    data.WireApi = saved.WireApi;
                    data.RequiresOpenAiAuth = saved.RequiresOpenAiAuth;
                }
                data.Model = SelectedValue(profiles, "custom_model");
                data.ReasoningEffort = SelectedValue(profiles, "custom_effort");
            }
            if (String.IsNullOrWhiteSpace(data.ApiKey))
                data.ApiKey = SelectedValue(profiles, "custom_key");
            if (!data.OfficialMode)
            {
                try
                {
                    data.HasOfficialCredentials = data.HasOfficialCredentials ||
                        HasOfficialTokens(ParseObject(SelectedValue(profiles, "official_auth")));
                }
                catch (Exception exception) { data.ProfileError = exception.Message; }
            }
            return data;
        }

        internal static bool IsValidBaseUrl(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!value.EndsWith("/v1", StringComparison.Ordinal)) return false;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                String.IsNullOrWhiteSpace(uri.Host) || !String.IsNullOrEmpty(uri.Query) ||
                !String.IsNullOrEmpty(uri.Fragment)) return false;
            return uri.AbsolutePath.EndsWith("/v1", StringComparison.Ordinal);
        }

        internal static string NormalizeOfficialProxyUrl(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            Uri uri;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttp ||
                String.IsNullOrEmpty(uri.Host) || uri.Port < 1 ||
                !String.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
                !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("代理地址须为 http://主机:端口，不包含账号密码、路径或查询参数。");
            return uri.GetLeftPart(UriPartial.Authority);
        }

        internal static void Save(ConfigData data)
        {
            var profiles = ReadEditableProfiles();
            var selected = SelectedProfile(profiles, "custom_providers");
            if (selected == null)
            {
                AddProfile(profiles, false, data.ProviderName, null);
                selected = SelectedProfile(profiles, "custom_providers");
            }
            UpdateCustomDraft(selected, data);
            ApplyProfiles(profiles, false, ProfileString(selected, "id"));
        }

        private static string ReadText(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }

        private static Dictionary<string, object> ParseObject(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
            var result = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            if (result == null) throw new InvalidOperationException("Invalid authentication or mode configuration.");
            return result;
        }

        private static Dictionary<string, object> ReadObject(string path) { return ParseObject(ReadText(path)); }

        private static string ProfileString(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) ? value as string : null;
        }

        private static string SelectedValue(Dictionary<string, object> values, string key)
        {
            Dictionary<string, object> selected = SelectedProfile(values, key.StartsWith("official_") ? "official_accounts" : "custom_providers");
            return selected == null ? null : ProfileString(selected, key);
        }

        internal static Dictionary<string, object> SelectedProfile(Dictionary<string, object> values, string collection)
        {
            object listValue, selectedValue;
            if (!values.TryGetValue(collection, out listValue) || !values.TryGetValue(collection == "official_accounts" ? "selected_official" : "selected_custom", out selectedValue)) return null;
            string selectedId = selectedValue as string;
            var list = listValue as object[];
            if (list == null || String.IsNullOrWhiteSpace(selectedId)) return null;
            foreach (object item in list)
            {
                var profile = item as Dictionary<string, object>;
                object id;
                if (profile != null && profile.TryGetValue("id", out id) && String.Equals(id as string, selectedId, StringComparison.Ordinal)) return profile;
            }
            return null;
        }

        private static void EnsureProfileSchema(Dictionary<string, object> values)
        {
            if (values.Count == 0)
            {
                values["official_accounts"] = new object[0];
                values["custom_providers"] = new object[0];
                values["selected_official"] = String.Empty;
                values["selected_custom"] = String.Empty;
            }
            foreach (string collection in new[] { "official_accounts", "custom_providers" })
            {
                object list;
                if (!values.TryGetValue(collection, out list) || !(list is object[]))
                    throw new InvalidOperationException("modes.json 配置结构不正确，未修改配置。");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (object item in (object[])list)
                {
                    var profile = item as Dictionary<string, object>;
                    string id = profile == null ? null : ProfileString(profile, "id");
                    if (String.IsNullOrWhiteSpace(id) || !ids.Add(id))
                        throw new InvalidOperationException("配置 ID 缺失或重复。");
                }
                string selected = ProfileString(values, collection == "official_accounts" ? "selected_official" : "selected_custom");
                if ((ids.Count == 0 && !String.IsNullOrEmpty(selected)) ||
                    (ids.Count > 0 && (selected == null || !ids.Contains(selected))))
                    throw new InvalidOperationException("选中的配置不存在。");
            }
        }

        private static Dictionary<string, object> EnsureSelectedProfile(Dictionary<string, object> values, string collection, string id)
        {
            EnsureProfileSchema(values);
            string selectedKey = collection == "official_accounts" ? "selected_official" : "selected_custom";
            var list = new List<object>((object[])values[collection]);
            foreach (object item in list)
            {
                var profile = item as Dictionary<string, object>; object existingId;
                if (profile != null && profile.TryGetValue("id", out existingId) && String.Equals(existingId as string, id, StringComparison.Ordinal)) { values[selectedKey] = id; return profile; }
            }
            var created = new Dictionary<string, object>();
            created["id"] = id;
            created["name"] = collection == "official_accounts" ? "官方账号" : "自定义 API";
            list.Add(created);
            values[collection] = list.ToArray();
            values[selectedKey] = id;
            return created;
        }

        // JWT claims are display hints only; decoding is not authentication validation.
        internal static Dictionary<string, object> AccountIdentity(string authText)
        {
            var identity = new Dictionary<string, object>();
            var auth = ParseObject(authText);
            object value;
            var tokens = auth.TryGetValue("tokens", out value) ? value as Dictionary<string, object> : null;
            if (tokens == null) return identity;
            identity["account_id"] = ProfileString(tokens, "account_id");
            string jwt = ProfileString(tokens, "id_token");
            try
            {
                if (String.IsNullOrEmpty(jwt)) return identity;
                string[] parts = jwt.Split('.');
                if (parts.Length != 3) return identity;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                var claims = ParseObject(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                identity["email"] = ProfileString(claims, "email");
                identity["account_name"] = ProfileString(claims, "name");
            }
            catch (FormatException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return identity;
        }

        internal static Dictionary<string, object> ReadProfiles()
        {
            var profiles = ReadObject(ProfilesPath);
            EnsureProfileSchema(profiles);
            return profiles;
        }

        internal static string AddProfile(Dictionary<string, object> profiles, bool official, string name, string copyId)
        {
            EnsureProfileSchema(profiles);
            if (String.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("请输入配置名称。");
            string collection = official ? "official_accounts" : "custom_providers";
            Dictionary<string, object> copy = null;
            if (copyId != null)
            {
                if (official) throw new InvalidOperationException("官方账号请通过登录添加。");
                foreach (object item in (object[])profiles[collection])
                {
                    var candidate = (Dictionary<string, object>)item;
                    if (ProfileString(candidate, "id") == copyId) copy = ParseObject(new JavaScriptSerializer().Serialize(candidate));
                }
                if (copy == null) throw new InvalidOperationException("待复制的配置不存在。");
            }
            string id = Guid.NewGuid().ToString("N");
            var created = EnsureSelectedProfile(profiles, collection, id);
            if (copy != null) foreach (var pair in copy) created[pair.Key] = pair.Value;
            created["id"] = id;
            created["name"] = name.Trim();
            return id;
        }

        internal static string DeleteProfile(Dictionary<string, object> profiles, bool official, string id)
        {
            EnsureProfileSchema(profiles);
            string collection = official ? "official_accounts" : "custom_providers";
            string selection = official ? "selected_official" : "selected_custom";
            var remaining = new List<object>();
            bool found = false;
            foreach (object item in (object[])profiles[collection])
            {
                var candidate = (Dictionary<string, object>)item;
                if (ProfileString(candidate, "id") == id) found = true;
                else remaining.Add(candidate);
            }
            if (!found) throw new InvalidOperationException("待删除的配置不存在。");
            profiles[collection] = remaining.ToArray();
            if (ProfileString(profiles, selection) == id)
                profiles[selection] = remaining.Count == 0 ? String.Empty : ProfileString((Dictionary<string, object>)remaining[0], "id");
            return ProfileString(profiles, selection);
        }

        internal static ConfigData ProfileData(Dictionary<string, object> profile)
        {
            var data = new ConfigData();
            LoadTomlText(data, ProfileString(profile, "model_providers_toml") ?? String.Empty);
            data.Model = ProfileString(profile, "custom_model");
            data.ReasoningEffort = ProfileString(profile, "custom_effort");
            data.ApiKey = ProfileString(profile, "custom_key");
            data.OfficialMode = false;
            data.AuthMode = "apikey";
            return data;
        }

        internal static void UpdateCustomDraft(Dictionary<string, object> profile, ConfigData data)
        {
            profile["custom_key"] = data.ApiKey;
            profile["custom_model"] = data.Model;
            profile["custom_effort"] = data.ReasoningEffort;
            string providers;
            SplitProviders(UpdateToml(ProfileString(profile, "model_providers_toml") ?? String.Empty, data), out providers);
            profile["model_providers_toml"] = providers;
        }

        internal static void ApplyProfiles(Dictionary<string, object> draft, bool official, string targetId)
        {
            PrepareProfiles(draft, official, targetId, true);
        }

        internal static void ValidateProfiles(Dictionary<string, object> draft, bool official, string targetId)
        {
            PrepareProfiles(draft, official, targetId, false);
        }

        private static void PrepareProfiles(Dictionary<string, object> draft, bool official, string targetId, bool commit)
        {
            // Work on a clone: failed writes must leave the form's draft available for retry.
            var next = ParseObject(new JavaScriptSerializer().Serialize(draft));
            EnsureProfileSchema(next);
            string collection = official ? "official_accounts" : "custom_providers";
            string selection = official ? "selected_official" : "selected_custom";
            next[selection] = targetId;
            EnsureProfileSchema(next);
            var target = SelectedProfile(next, collection);
            if (target == null) throw new InvalidOperationException("请先添加配置。");
            string proxy = official ? NormalizeOfficialProxyUrl(ProfileString(next, "official_proxy_url")) : ProfileString(next, "official_proxy_url");
            ValidateModeSwitch(official);
            UpdateProxyDotEnv(ReadText(DotEnvPath), official ? proxy : String.Empty);
            var current = new ConfigData();
            LoadToml(current);
            var saved = ReadProfiles();
            CaptureOfficialSession(next, saved, true);
            string originalToml = ReadText(ConfigPath) ?? String.Empty;
            string providers;
            string clean = SplitProviders(originalToml, out providers);
            string toml, auth;
            if (official)
            {
                next["official_proxy_url"] = proxy;
                auth = ProfileString(target, "official_auth");
                if (String.IsNullOrWhiteSpace(auth)) auth = "{\"auth_mode\":\"chatgpt\",\"OPENAI_API_KEY\":null}";
                var credentials = ParseObject(auth);
                if (ProfileString(credentials, "auth_mode") != "chatgpt") throw new InvalidOperationException("官方账号凭据格式不正确。");
                if (!String.IsNullOrWhiteSpace(ProfileString(credentials, "OPENAI_API_KEY")))
                {
                    credentials["OPENAI_API_KEY"] = null;
                    auth = new JavaScriptSerializer().Serialize(credentials);
                }
                if (current.OfficialMode && String.IsNullOrWhiteSpace(providers)) toml = originalToml;
                else
                {
                    var lines = new List<string>(Regex.Split(clean, "\\r?\\n"));
                    SetTopLevel(lines, "model_provider", QuoteToml("openai"));
                    RemoveTopLevel(lines, "model");
                    RemoveTopLevel(lines, "model_reasoning_effort");
                    string model = ProfileString(next, "official_model");
                    string effort = ProfileString(next, "official_effort");
                    if (!String.IsNullOrWhiteSpace(model)) SetTopLevel(lines, "model", QuoteToml(model));
                    SetTopLevel(lines, "model_reasoning_effort", QuoteToml(String.IsNullOrWhiteSpace(effort) ? "medium" : effort));
                    toml = String.Join(Environment.NewLine, lines.ToArray());
                }
            }
            else
            {
                ConfigData data = ProfileData(target);
                if (!IsValidBaseUrl(data.BaseUrl) || String.IsNullOrWhiteSpace(data.ApiKey) ||
                    String.IsNullOrWhiteSpace(data.Model) || String.IsNullOrWhiteSpace(data.ProviderName))
                    throw new InvalidOperationException("请填写完整的 API 配置后再应用。");
                toml = UpdateToml(clean.TrimEnd() + Environment.NewLine + (ProfileString(target, "model_providers_toml") ?? String.Empty), data);
                auth = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "auth_mode", "apikey" }, { "OPENAI_API_KEY", data.ApiKey } });
            }
            if (commit) CommitMode(toml, auth, next, official ? proxy : String.Empty);
        }

        private static bool HasOfficialTokens(Dictionary<string, object> auth)
        {
            object tokensValue;
            if (ProfileString(auth, "auth_mode") != "chatgpt" || !auth.TryGetValue("tokens", out tokensValue)) return false;
            var tokens = tokensValue as Dictionary<string, object>;
            return tokens != null && !String.IsNullOrWhiteSpace(ProfileString(tokens, "access_token")) &&
                !String.IsNullOrWhiteSpace(ProfileString(tokens, "refresh_token"));
        }

        // Refresh the in-memory library without writing on tab/list changes or cancellation.
        internal static Dictionary<string, object> ReadEditableProfiles()
        {
            var profiles = ReadProfiles();
            CaptureOfficialSession(profiles, profiles, false);
            var current = new ConfigData();
            LoadToml(current);
            if (!current.OfficialMode)
            {
                var selected = SelectedProfile(profiles, "custom_providers");
                if (selected == null)
                {
                    AddProfile(profiles, false, current.ProviderName ?? "自定义 API", null);
                    selected = SelectedProfile(profiles, "custom_providers");
                }
                string providers;
                SplitProviders(ReadText(ConfigPath) ?? String.Empty, out providers);
                selected["model_providers_toml"] = providers;
                selected["custom_model"] = current.Model;
                selected["custom_effort"] = current.ReasoningEffort;
                var auth = ReadObject(AuthPath);
                if (ProfileString(auth, "auth_mode") == "apikey")
                    selected["custom_key"] = ProfileString(auth, "OPENAI_API_KEY");
            }
            return profiles;
        }

        private static Dictionary<string, object> FindProfile(Dictionary<string, object> profiles, string collection, string key, string value)
        {
            if (String.IsNullOrEmpty(value)) return null;
            foreach (object item in (object[])profiles[collection])
            {
                var entry = (Dictionary<string, object>)item;
                if (ProfileString(entry, key) == value) return entry;
            }
            return null;
        }

        private static void CaptureOfficialSession(Dictionary<string, object> next, Dictionary<string, object> saved, bool applying)
        {
            var current = new ConfigData();
            LoadToml(current);
            var previous = SelectedProfile(saved, "official_accounts");
            string latest = ReadText(AuthPath);
            var auth = ParseObject(latest);
            bool hasTokens = ProfileString(auth, "auth_mode") == "chatgpt" && HasOfficialTokens(auth);
            if (!current.OfficialMode && !hasTokens) return;
            if (current.OfficialMode)
            {
                next["official_model"] = current.Model;
                next["official_effort"] = current.ReasoningEffort;
            }
            var entry = previous == null ? null : FindProfile(next, "official_accounts", "id", ProfileString(previous, "id"));
            // An explicit deletion wins over importing the still-active credentials.
            if (applying && previous != null && entry == null) return;
            if (!hasTokens)
            {
                if (current.OfficialMode && entry != null) entry.Remove("official_auth");
                return;
            }
            var identity = AccountIdentity(latest);
            string accountId = ProfileString(identity, "account_id");
            if (String.IsNullOrWhiteSpace(accountId))
                throw new InvalidOperationException("当前官方凭据缺少 account_id，无法区分账号；未改写配置。");
            var existing = FindProfile(next, "official_accounts", "account_id", accountId);
            if (existing != null) entry = existing;
            else if (entry == null || (!String.IsNullOrEmpty(ProfileString(entry, "account_id")) && ProfileString(entry, "account_id") != accountId))
            {
                string selected = ProfileString(next, "selected_official");
                AddProfile(next, true, ProfileString(identity, "email") ?? ProfileString(identity, "account_name") ?? "官方账号", null);
                entry = SelectedProfile(next, "official_accounts");
                if (applying && !String.IsNullOrEmpty(selected)) next["selected_official"] = selected;
            }
            entry["official_auth"] = latest;
            foreach (var pair in identity) entry[pair.Key] = pair.Value;
            if (!applying) next["selected_official"] = ProfileString(entry, "id");
        }

        internal static void SaveOfficial(string proxyUrl)
        {
            var profiles = ReadEditableProfiles();
            profiles["official_proxy_url"] = NormalizeOfficialProxyUrl(proxyUrl);
            if (SelectedProfile(profiles, "official_accounts") == null)
                AddProfile(profiles, true, "官方账号", null);
            ApplyProfiles(profiles, true, ProfileString(profiles, "selected_official"));
        }

        internal static void ValidateModeSwitch(bool official)
        {
            string text = ReadText(ConfigPath) ?? String.Empty;
            string providers;
            if (official) text = SplitProviders(text, out providers);
            string conflict = GetModeConflict(text, official);
            if (conflict != null)
                throw new InvalidOperationException("\u914d\u7f6e\u51b2\u7a81\uff1a" + conflict +
                    "\u3002\u8bf7\u5148\u6838\u5bf9 config.toml\uff1b\u672a\u4fee\u6539\u914d\u7f6e\u6216\u51ed\u8bc1\u3002");
        }

        // Keep provider tables verbatim, including unknown keys and nested tables.
        // Inline/dotted root assignments remain conflicts rather than being partially rewritten.
        private static string SplitProviders(string toml, out string providers)
        {
            var remaining = new StringBuilder();
            var saved = new StringBuilder();
            bool inProviders = false;
            string[] syntax = TomlSyntaxLines(toml);
            int lineIndex = 0;
            foreach (Match match in Regex.Matches(toml, "[^\\n]*\\n|[^\\n]+$"))
            {
                string raw = match.Value;
                string line = syntax[lineIndex++].Trim();
                if (line.StartsWith("["))
                {
                    inProviders = IsProviderTable(line);
                }
                if (inProviders) saved.Append(raw); else remaining.Append(raw);
            }
            providers = saved.ToString();
            return remaining.ToString();
        }

        private static bool IsProviderTable(string line)
        {
            return Regex.IsMatch(line, "^\\[{1,2}\\s*(?:model_providers|\"model_providers\"|'model_providers')\\s*(?:\\.|\\])");
        }

        // Hide comments and multiline contents for structural edits, preserving line indices.
        private static string[] TomlSyntaxLines(string toml)
        {
            char quote = '\0';
            bool multiline = false;
            int depth = 0;
            var result = new List<string>();
            foreach (string raw in Regex.Split(toml, "\\r?\\n"))
            {
                var line = new StringBuilder();
                bool continuation = multiline || depth > 0;
                bool value = depth > 0;
                for (int i = 0; i < raw.Length; i++)
                {
                    char c = raw[i];
                    if (quote == '\0')
                    {
                        if (c == '#') break;
                        if (c == '=') value = true;
                        if (value && (c == '[' || c == '{')) depth++;
                        if (value && (c == ']' || c == '}')) depth--;
                        if (c == '\'' || c == '"')
                        {
                            quote = c;
                            multiline = i + 2 < raw.Length && raw[i + 1] == c && raw[i + 2] == c;
                            if (multiline) { line.Append("\"\""); i += 2; continue; }
                        }
                        line.Append(c);
                    }
                    else
                    {
                        if (!multiline) line.Append(c);
                        if (quote == '"' && c == '\\')
                        {
                            if (++i < raw.Length && !multiline) line.Append(raw[i]);
                            continue;
                        }
                        if (c != quote) continue;
                        if (multiline)
                        {
                            int end = i;
                            while (end < raw.Length && raw[end] == quote) end++;
                            if (end - i < 3) continue;
                            i = end - 1;
                        }
                        quote = '\0';
                        multiline = false;
                    }
                }
                if (quote != '\0' && !multiline)
                    throw new InvalidOperationException("config.toml contains an unterminated string.");
                result.Add(continuation ? String.Empty : line.ToString());
            }
            if (multiline) throw new InvalidOperationException("config.toml contains an unterminated multiline string.");
            return result.ToArray();
        }

        private static string GetModeConflict(string toml, bool official)
        {
            bool topLevel = true;
            foreach (string raw in TomlSyntaxLines(toml))
            {
                string line = raw.Trim();
                if (line.StartsWith("["))
                {
                    topLevel = false;
                    if (official && IsProviderTable(line))
                        return "model_providers tables must be saved outside the official configuration";
                    continue;
                }
                if (!topLevel) continue;
                int equals = FindUnquotedEquals(line);
                if (equals < 1) continue;
                string key = line.Substring(0, equals).Trim().Trim('"', '\'');
                string value = line.Substring(equals + 1).Trim();
                if (key == "forced_login_method")
                    return "forced_login_method restricts authentication; remove it before switching modes";
                if (key == "cli_auth_credentials_store" && value != "\"file\"" && value != "'file'")
                    return "cli_auth_credentials_store \u4e0d\u662f file\uff0c\u65e0\u6cd5\u4ec5\u901a\u8fc7 auth.json \u5207\u6362\u51ed\u636e";
                if (key == "model_providers" || key.StartsWith("model_providers."))
                    return "model_providers must use TOML table headers";
                if (key == "profile" || (official && (key == "chatgpt_base_url" || key == "openai_base_url")))
                    return key + " \u53ef\u80fd\u8986\u76d6\u76ee\u6807\u8def\u7531";
            }
            return null;
        }

        private static string UpdateProxyDotEnv(string original, string proxy)
        {
            const string begin = "# BEGIN CHATGPT API ONLY PROXY";
            const string end = "# END CHATGPT API ONLY PROXY";
            string text = original ?? String.Empty;
            MatchCollection markers = Regex.Matches(text, "(?m)^# (?:BEGIN|END) CHATGPT API ONLY PROXY\\r?$");
            if (markers.Count != 0)
            {
                if (markers.Count != 2 || markers[0].Value.TrimEnd('\r') != begin ||
                    markers[1].Value.TrimEnd('\r') != end)
                    throw new InvalidOperationException(".env 中的启动器代理标记不完整或重复，请先核对该文件；未保存配置。");
                int start = markers[0].Index;
                // The separator belongs to our block, preserving the original final newline.
                if (start > 0 && text[start - 1] == '\n')
                {
                    start--;
                    if (start > 0 && text[start - 1] == '\r') start--;
                }
                int finish = markers[1].Index + markers[1].Length;
                if (finish < text.Length && text[finish] == '\n') finish++;
                string suffix = text.Substring(finish);
                text = text.Substring(0, start);
                if (text.Length > 0 && suffix.Length > 0 && !text.EndsWith("\n") && !suffix.StartsWith("\n") && !suffix.StartsWith("\r\n"))
                    text += "\n";
                text += suffix;
            }
            if (String.IsNullOrEmpty(proxy)) return original == null ? null : text;
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var block = new StringBuilder(text + newline + begin + newline);
            foreach (var variable in OfficialProxyEnvironment(proxy))
                block.Append(variable.Key).Append('=').Append(variable.Value).Append(newline);
            return block.Append(end).Append(newline).ToString();
        }

        private static void CommitMode(string toml, string auth, Dictionary<string, object> profiles, string proxy)
        {
            Directory.CreateDirectory(ConfigDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath));
            string[] paths = { DotEnvPath, ProfilesPath, AuthPath, ConfigPath };
            string[] original = { ReadText(DotEnvPath), ReadText(ProfilesPath), ReadText(AuthPath), ReadText(ConfigPath) };
            string[] updated = { UpdateProxyDotEnv(original[0], proxy), new JavaScriptSerializer().Serialize(profiles), auth, toml };
            int attempted = -1;
            try
            {
                for (int index = 0; index < paths.Length; index++)
                {
                    if (String.Equals(original[index], updated[index], StringComparison.Ordinal)) continue;
                    attempted = index;
                    WriteAtomic(paths[index], updated[index]);
                }
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                for (int index = attempted; index >= 0; index--)
                {
                    if (String.Equals(original[index], updated[index], StringComparison.Ordinal)) continue;
                    try
                    {
                        if (original[index] == null) { if (File.Exists(paths[index])) File.Delete(paths[index]); }
                        else WriteAtomic(paths[index], original[index]);
                    }
                    catch (Exception rollback) { errors.Add(rollback); }
                }
                throw new AggregateException("\u5207\u6362\u6a21\u5f0f\u5931\u8d25\uff0c\u672a\u542f\u52a8\u5ba2\u6237\u7aef\u3002", errors);
            }
        }

        private static void RemoveTopLevel(List<string> lines, string key)
        {
            string[] syntax = TomlSyntaxLines(String.Join(Environment.NewLine, lines.ToArray()));
            int end = Array.FindIndex(syntax, delegate(string line) { return line.TrimStart().StartsWith("["); });
            if (end < 0) end = lines.Count;
            for (int index = end - 1; index >= 0; index--)
                if (Regex.IsMatch(syntax[index], "^\\s*" + Regex.Escape(key) + "\\s*=")) lines.RemoveAt(index);
        }

        private static void LoadToml(ConfigData data)
        {
            data.OfficialMode = true;
            if (!File.Exists(ConfigPath)) return;
            try { LoadTomlText(data, File.ReadAllText(ConfigPath, Encoding.UTF8)); }
            catch { data.ConfigReadable = false; }
        }

        private static void LoadTomlText(ConfigData data, string text)
        {
            try
            {
                string section = String.Empty;
                foreach (string rawLine in TomlSyntaxLines(text))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = Regex.Replace(line.Substring(1, line.Length - 2), "[\\s\"']", String.Empty);
                        continue;
                    }
                    int equals = FindUnquotedEquals(line);
                    if (equals < 1) continue;
                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    if (section.Length == 0)
                    {
                        if (key == "model_provider") data.OfficialMode = ParseTomlString(value) == "openai";
                        else if (key == "cli_auth_credentials_store") data.CredentialsStore = ParseTomlString(value);
                        else if (key == "model") data.Model = ParseTomlString(value);
                        else if (key == "model_reasoning_effort") data.ReasoningEffort = ParseTomlString(value);
                    }
                    else if (section == "model_providers.custom")
                    {
                        if (key == "name") data.ProviderName = ParseTomlString(value);
                        else if (key == "base_url") data.BaseUrl = ParseTomlString(value);
                        else if (key == "wire_api") data.WireApi = ParseTomlString(value);
                        else if (key == "requires_openai_auth") data.RequiresOpenAiAuth = ParseTomlBoolean(value);
                    }
                }
                string all = text;
                if (GetModeConflict(all, data.OfficialMode) != null) { data.ConfigReadable = false; return; }
                if (data.OfficialMode)
                {
                    data.ConfigReadable = true;
                    return;
                }
                data.ConfigReadable = Regex.IsMatch(all, "(?m)^\\s*model_provider\\s*=\\s*\"custom\"\\s*(?:#.*)?$") &&
                    data.Model != null && data.ReasoningEffort != null && data.ProviderName != null &&
                    data.BaseUrl != null && data.WireApi != null && data.RequiresOpenAiAuth.HasValue;
            }
            catch { data.ConfigReadable = false; }
        }

        private static void LoadAuth(ConfigData data)
        {
            if (!File.Exists(AuthPath)) return;
            try
            {
                var auth = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(AuthPath, Encoding.UTF8))
                    as Dictionary<string, object>;
                if (auth == null) return;
                object value;
                if (auth.TryGetValue("OPENAI_API_KEY", out value)) data.ApiKey = value as string;
                if (auth.TryGetValue("auth_mode", out value))
                {
                    data.AuthMode = value as string;
                }
                data.AuthReadable = true;
                data.HasOfficialCredentials = HasOfficialTokens(auth);
                data.ActiveOfficialCredentials = data.HasOfficialCredentials;
            }
            catch { data.AuthReadable = false; }
        }

        private static string UpdateToml(string existing, ConfigData data)
        {
            var lines = new List<string>(Regex.Split(existing, "\\r?\\n"));
            SetTopLevel(lines, "model_provider", QuoteToml("custom"));
            SetTopLevel(lines, "model", QuoteToml(data.Model));
            SetTopLevel(lines, "model_reasoning_effort", QuoteToml(data.ReasoningEffort));
            SetSectionValue(lines, "model_providers.custom", "name", QuoteToml(data.ProviderName));
            SetSectionValue(lines, "model_providers.custom", "base_url", QuoteToml(data.BaseUrl));
            SetSectionValue(lines, "model_providers.custom", "wire_api", QuoteToml("responses"));
            SetSectionValue(lines, "model_providers.custom", "requires_openai_auth", "true");
            while (lines.Count > 0 && String.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.RemoveAt(lines.Count - 1);
            return String.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
        }

        private static void SetTopLevel(List<string> lines, string key, string value)
        {
            string[] syntax = TomlSyntaxLines(String.Join(Environment.NewLine, lines.ToArray()));
            int end = Array.FindIndex(syntax, delegate(string line) { return line.TrimStart().StartsWith("["); });
            if (end < 0) end = lines.Count;
            int found = -1;
            for (int i = 0; i < end; i++)
                if (Regex.IsMatch(syntax[i], "^\\s*" + Regex.Escape(key) + "\\s*=")) { found = i; break; }
            string replacement = key + " = " + value;
            if (found >= 0) lines[found] = replacement; else lines.Insert(end, replacement);
        }

        private static void SetSectionValue(List<string> lines, string section, string key, string value)
        {
            string[] syntax = TomlSyntaxLines(String.Join(Environment.NewLine, lines.ToArray()));
            int start = Array.FindIndex(syntax, delegate(string line) {
                return Regex.Replace(line, "[\\s\"']", String.Empty) == "[" + section + "]";
            });
            if (start < 0)
            {
                if (lines.Count > 0 && !String.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.Add(String.Empty);
                lines.Add("[" + section + "]");
                lines.Add(key + " = " + value);
                return;
            }
            int end = start + 1;
            while (end < lines.Count && !syntax[end].TrimStart().StartsWith("[")) end++;
            for (int i = start + 1; i < end; i++)
                if (Regex.IsMatch(syntax[i], "^\\s*" + Regex.Escape(key) + "\\s*="))
                { lines[i] = key + " = " + value; return; }
            lines.Insert(end, key + " = " + value);
        }

        private static int FindUnquotedEquals(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) quoted = !quoted;
                else if (line[i] == '=' && !quoted) return i;
            }
            return -1;
        }

        private static string ParseTomlString(string value)
        {
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
                return value.Substring(1, value.Length - 2);
            if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"') return null;
            string inner = value.Substring(1, value.Length - 2);
            return Regex.Unescape(inner);
        }

        private static bool? ParseTomlBoolean(string value)
        {
            if (value == "true") return true;
            if (value == "false") return false;
            return null;
        }

        private static string QuoteToml(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void WriteAtomic(string path, string content)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, content, Utf8WithoutBom);
            if (File.Exists(path))
            {
                string backup = path + ".bak";
                try { File.Replace(temp, path, backup, true); }
                catch
                {
                    File.Copy(path, backup, true);
                    File.Delete(path);
                    File.Move(temp, path);
                }
            }
            else File.Move(temp, path);
        }
    }

    private static class ProviderSynchronizer
    {
        private const int SqliteOk = 0;
        private const string SessionMetaType = "session_meta";

        private sealed class RolloutChange
        {
            internal string Path;
            internal string BackupPath;
            internal DateTime LastWriteTimeUtc;
        }

        internal static ProviderSyncResult Synchronize(string codexHome, string targetProvider,
            IProgress<ProviderSyncProgress> progress)
        {
            if (String.IsNullOrWhiteSpace(targetProvider) ||
                targetProvider.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("\u5bf9\u8bdd\u63d0\u4f9b\u8005 ID \u65e0\u6548\u3002");

            Directory.CreateDirectory(codexHome);
            List<RolloutChange> rolloutChanges = CollectRolloutChanges(codexHome, targetProvider, progress);
            ReportProgress(progress, 0, 0, "\u7edf\u8ba1\u6570\u636e\u5e93");
            List<string> databasePaths = FindDatabasePaths(codexHome);
            int databaseUpdateCount = 0;
            for (int index = 0; index < databasePaths.Count; index++)
            {
                databaseUpdateCount += CountDatabaseUpdates(databasePaths[index], targetProvider);
                ReportProgress(progress, index + 1, databasePaths.Count, "\u7edf\u8ba1\u6570\u636e\u5e93");
            }
            int total = rolloutChanges.Count + databaseUpdateCount;
            ReportProgress(progress, 0, total);
            if (total == 0) return new ProviderSyncResult(0);

            string backupDirectory = CreateBackup(codexHome, targetProvider, rolloutChanges, databasePaths, progress);
            var appliedRollouts = new List<RolloutChange>();
            int completed = 0;

            try
            {
                ReportProgress(progress, 0, total);
                foreach (RolloutChange change in rolloutChanges)
                {
                    appliedRollouts.Add(change);
                    RewriteRolloutFile(change.Path, targetProvider);
                    File.SetLastWriteTimeUtc(change.Path, change.LastWriteTimeUtc);
                    completed++;
                    ReportProgress(progress, completed, total);
                }

                foreach (string databasePath in databasePaths)
                {
                    completed += UpdateDatabase(databasePath, targetProvider);
                    ReportProgress(progress, completed, total);
                }
                return new ProviderSyncResult(total);
            }
            catch (Exception exception)
            {
                foreach (RolloutChange change in appliedRollouts)
                {
                    try
                    {
                        File.Copy(change.BackupPath, change.Path, true);
                        File.SetLastWriteTimeUtc(change.Path, change.LastWriteTimeUtc);
                    }
                    catch { }
                }
                throw new InvalidOperationException(
                    "\u5bf9\u8bdd\u4fee\u590d\u5931\u8d25\u3002\u5df2\u4fdd\u7559\u5907\u4efd\uff1a" + backupDirectory +
                    Environment.NewLine + exception.Message, exception);
            }
        }

        private static void ReportProgress(IProgress<ProviderSyncProgress> progress,
            int completed, int total, string phase = "\u4fee\u590d\u5bf9\u8bdd")
        {
            if (progress != null) progress.Report(new ProviderSyncProgress(completed, total, phase));
#if PROVIDER_SYNC_TEST
            int delayMilliseconds;
            if (Int32.TryParse(
                Environment.GetEnvironmentVariable("CHATGPT_API_ONLY_PROGRESS_DELAY_MS"),
                out delayMilliseconds) && delayMilliseconds > 0)
            {
                System.Threading.Thread.Sleep(Math.Min(delayMilliseconds, 100));
            }
#endif
        }

        private static List<RolloutChange> CollectRolloutChanges(string codexHome, string targetProvider,
            IProgress<ProviderSyncProgress> progress)
        {
            var changes = new List<RolloutChange>();
            var paths = new List<string>();
            foreach (string directoryName in new[] { "sessions", "archived_sessions" })
            {
                string root = Path.Combine(codexHome, directoryName);
                if (!Directory.Exists(root)) continue;
                paths.AddRange(Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories));
            }
            ReportProgress(progress, 0, paths.Count, "\u626b\u63cf\u5bf9\u8bdd");
            for (int index = 0; index < paths.Count; index++)
            {
                string path = paths[index];
                foreach (string line in ReadRolloutLines(path))
                {
                    if (String.Equals(line, RewriteSessionMetadata(line, targetProvider), StringComparison.Ordinal)) continue;
                    changes.Add(new RolloutChange
                    {
                        Path = path,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(path)
                    });
                    break;
                }
                ReportProgress(progress, index + 1, paths.Count, "\u626b\u63cf\u5bf9\u8bdd");
            }
            return changes;
        }

        private static IEnumerable<string> ReadRolloutLines(string path)
        {
            using (var reader = new StreamReader(path, Encoding.UTF8))
            {
                var buffer = new char[8192];
                var line = new StringBuilder();
                int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int start = 0;
                    for (int index = 0; index < count; index++)
                    {
                        if (buffer[index] != '\n') continue;
                        line.Append(buffer, start, index - start + 1);
                        yield return line.ToString();
                        line.Clear();
                        start = index + 1;
                    }
                    line.Append(buffer, start, count - start);
                }
                if (line.Length > 0) yield return line.ToString();
            }
        }

        private static void RewriteRolloutFile(string path, string targetProvider)
        {
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                {
                    foreach (string line in ReadRolloutLines(path))
                        writer.Write(RewriteSessionMetadata(line, targetProvider));
                }
                File.Replace(temporary, path, null);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static string RewriteSessionMetadata(string text, string targetProvider)
        {
            if (text.IndexOf(SessionMetaType, StringComparison.Ordinal) < 0 &&
                text.IndexOf("\\u", StringComparison.Ordinal) < 0) return text;
            var serializer = new JavaScriptSerializer();
            var output = new StringBuilder(text.Length);
            int position = 0;
            while (position < text.Length)
            {
                int newline = text.IndexOf('\n', position);
                int end = newline < 0 ? text.Length : newline;
                string lineEnding = newline < 0 ? String.Empty : "\n";
                string line = text.Substring(position, end - position);
                if (line.EndsWith("\r", StringComparison.Ordinal))
                {
                    line = line.Substring(0, line.Length - 1);
                    lineEnding = "\r\n";
                }

                string nextLine = line;
                try
                {
                    var record = serializer.DeserializeObject(line) as Dictionary<string, object>;
                    object typeValue;
                    object payloadValue;
                    if (record != null && record.TryGetValue("type", out typeValue) &&
                        String.Equals(typeValue as string, SessionMetaType, StringComparison.Ordinal) &&
                        record.TryGetValue("payload", out payloadValue))
                    {
                        var payload = payloadValue as Dictionary<string, object>;
                        object providerValue;
                        if (payload != null && (!payload.TryGetValue("model_provider", out providerValue) ||
                            !String.Equals(providerValue as string, targetProvider, StringComparison.Ordinal)))
                        {
                            payload["model_provider"] = targetProvider;
                            nextLine = serializer.Serialize(record);
                        }
                    }
                }
                catch { }

                output.Append(nextLine);
                output.Append(lineEnding);
                if (newline < 0) break;
                position = newline + 1;
            }
            return output.ToString();
        }

        private static List<string> FindDatabasePaths(string codexHome)
        {
            var paths = new List<string>();
            string sqliteDirectory = Path.Combine(codexHome, "sqlite");
            if (Directory.Exists(sqliteDirectory))
            {
                foreach (string path in Directory.GetFiles(sqliteDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    string extension = Path.GetExtension(path);
                    if (String.Equals(extension, ".db", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(extension, ".sqlite", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(extension, ".sqlite3", StringComparison.OrdinalIgnoreCase))
                    {
                        if (DatabaseHasProviderColumn(path)) paths.Add(path);
                    }
                }
            }
            string stateDatabase = Path.Combine(codexHome, "state_5.sqlite");
            if (File.Exists(stateDatabase) && DatabaseHasProviderColumn(stateDatabase)) paths.Add(stateDatabase);
            return paths;
        }

        private static bool DatabaseHasProviderColumn(string path)
        {
            IntPtr database;
            int result = sqlite3_open16(path, out database);
            if (result != SqliteOk)
            {
                string message = database == IntPtr.Zero ? "SQLite open failed" : SqliteError(database);
                if (database != IntPtr.Zero) sqlite3_close(database);
                throw new InvalidOperationException(Path.GetFileName(path) + ": " + message);
            }
            try
            {
                return HasColumn(database, "threads", "model_provider") ||
                    HasColumn(database, "local_thread_catalog", "model_provider");
            }
            finally { sqlite3_close(database); }
        }

        private static string CreateBackup(string codexHome, string targetProvider,
            List<RolloutChange> rolloutChanges, List<string> databasePaths,
            IProgress<ProviderSyncProgress> progress)
        {
            int total = rolloutChanges.Count + databasePaths.Count;
            int completed = 0;
            ReportProgress(progress, 0, total, "\u5907\u4efd\u5bf9\u8bdd");
            string root = Path.Combine(codexHome, "backups_state", "provider-sync");
            string name = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string backup = Path.Combine(root, name);
            for (int suffix = 1; Directory.Exists(backup); suffix++)
                backup = Path.Combine(root, name + "-" + suffix);
            Directory.CreateDirectory(backup);

            string configPath = Path.Combine(codexHome, "config.toml");
            if (File.Exists(configPath)) File.Copy(configPath, Path.Combine(backup, "config.toml"));

            foreach (string databasePath in databasePaths)
            {
                foreach (string source in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                {
                    if (!File.Exists(source)) continue;
                    string relative = MakeRelativePath(codexHome, source);
                    string destination = Path.Combine(backup, "db", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(source, destination);
                }
                ReportProgress(progress, ++completed, total, "\u5907\u4efd\u5bf9\u8bdd");
            }

            foreach (RolloutChange change in rolloutChanges)
            {
                string relative = MakeRelativePath(codexHome, change.Path);
                string destination = Path.Combine(backup, "sessions", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(change.Path, destination);
                change.BackupPath = destination;
                ReportProgress(progress, ++completed, total, "\u5907\u4efd\u5bf9\u8bdd");
            }

            var metadata = new Dictionary<string, object>();
            metadata["version"] = 1;
            metadata["namespace"] = "provider-sync";
            metadata["targetProvider"] = targetProvider;
            metadata["createdAt"] = DateTime.UtcNow.ToString("o");
            metadata["changedSessionFiles"] = rolloutChanges.Count;
            metadata["databaseFiles"] = databasePaths.Count;
            metadata["managedBy"] = "ChatGPT API Only provider sync (Codex++ compatible)";
            File.WriteAllText(Path.Combine(backup, "metadata.json"),
                new JavaScriptSerializer().Serialize(metadata) + Environment.NewLine,
                new UTF8Encoding(false));
            return backup;
        }

        private static string MakeRelativePath(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedPath = Path.GetFullPath(path);
            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(normalizedPath);
            return normalizedPath.Substring(normalizedRoot.Length);
        }

        private static int CountDatabaseUpdates(string path, string targetProvider)
        {
            IntPtr database;
            int openResult = sqlite3_open16(path, out database);
            if (openResult != SqliteOk)
            {
                string message = database == IntPtr.Zero ? "SQLite open failed" : SqliteError(database);
                if (database != IntPtr.Zero) sqlite3_close(database);
                throw new InvalidOperationException(Path.GetFileName(path) + ": " + message);
            }
            try
            {
                ExecuteSql(database, "PRAGMA busy_timeout=5000");
                string quotedProvider = "'" + targetProvider.Replace("'", "''") + "'";
                int count = 0;
                if (HasColumn(database, "threads", "model_provider"))
                    count += QueryInteger(database, "SELECT COUNT(*) FROM threads WHERE COALESCE(model_provider, '') <> " + quotedProvider);
                if (HasColumn(database, "local_thread_catalog", "model_provider"))
                    count += QueryInteger(database, "SELECT COUNT(*) FROM local_thread_catalog WHERE COALESCE(model_provider, '') <> " + quotedProvider);
                return count;
            }
            finally { sqlite3_close(database); }
        }

        private static int UpdateDatabase(string path, string targetProvider)
        {
            IntPtr database;
            int openResult = sqlite3_open16(path, out database);
            if (openResult != SqliteOk)
            {
                string message = database == IntPtr.Zero ? "SQLite open failed" : SqliteError(database);
                if (database != IntPtr.Zero) sqlite3_close(database);
                throw new InvalidOperationException(Path.GetFileName(path) + ": " + message);
            }
            try
            {
                ExecuteSql(database, "PRAGMA busy_timeout=5000");
                bool updateThreads = HasColumn(database, "threads", "model_provider");
                bool updateCatalog = HasColumn(database, "local_thread_catalog", "model_provider");
                if (!updateThreads && !updateCatalog) return 0;

                string quotedProvider = "'" + targetProvider.Replace("'", "''") + "'";
                int updated = 0;
                ExecuteSql(database, "BEGIN IMMEDIATE TRANSACTION");
                try
                {
                    if (updateThreads)
                    {
                        ExecuteSql(database, "UPDATE threads SET model_provider = " + quotedProvider +
                            " WHERE COALESCE(model_provider, '') <> " + quotedProvider);
                        updated += sqlite3_changes(database);
                    }
                    if (updateCatalog)
                    {
                        ExecuteSql(database, "UPDATE local_thread_catalog SET model_provider = " + quotedProvider +
                            " WHERE COALESCE(model_provider, '') <> " + quotedProvider);
                        updated += sqlite3_changes(database);
                    }
                    ExecuteSql(database, "COMMIT");
                    return updated;
                }
                catch
                {
                    try { ExecuteSql(database, "ROLLBACK"); } catch { }
                    throw;
                }
            }
            finally { sqlite3_close(database); }
        }

        private static int QueryInteger(IntPtr database, string sql)
        {
            int value = 0;
            SqliteCallback callback = delegate(IntPtr context, int count, IntPtr values, IntPtr names)
            {
                if (count > 0)
                {
                    IntPtr pointer = Marshal.ReadIntPtr(values, 0);
                    int.TryParse(pointer == IntPtr.Zero ? "0" : Marshal.PtrToStringAnsi(pointer), out value);
                }
                return 0;
            };
            ExecuteSql(database, sql, callback);
            return value;
        }

        private static bool HasColumn(IntPtr database, string table, string column)
        {
            bool found = false;
            SqliteCallback callback = delegate(IntPtr context, int count, IntPtr values, IntPtr names)
            {
                for (int index = 0; index < count; index++)
                {
                    IntPtr valuePointer = Marshal.ReadIntPtr(values, index * IntPtr.Size);
                    if (valuePointer == IntPtr.Zero) continue;
                    string value = Marshal.PtrToStringAnsi(valuePointer);
                    if (String.Equals(value, column, StringComparison.Ordinal)) found = true;
                }
                return 0;
            };
            ExecuteSql(database, "SELECT name FROM pragma_table_info('" + table.Replace("'", "''") + "')", callback);
            return found;
        }

        private static void ExecuteSql(IntPtr database, string sql)
        {
            ExecuteSql(database, sql, null);
        }

        private static void ExecuteSql(IntPtr database, string sql, SqliteCallback callback)
        {
            IntPtr error;
            int result = sqlite3_exec(database, sql, callback, IntPtr.Zero, out error);
            if (result == SqliteOk) return;
            string message = error == IntPtr.Zero ? SqliteError(database) : Marshal.PtrToStringAnsi(error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            throw new InvalidOperationException(message + " [" + sql + "]");
        }

        private static string SqliteError(IntPtr database)
        {
            IntPtr pointer = sqlite3_errmsg(database);
            return pointer == IntPtr.Zero ? "Unknown SQLite error" : Marshal.PtrToStringAnsi(pointer);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteCallback(IntPtr context, int count, IntPtr values, IntPtr names);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int sqlite3_open16(string filename, out IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_exec(IntPtr database, string sql, SqliteCallback callback,
            IntPtr context, out IntPtr errorMessage);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_changes(IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr pointer);
    }

    private sealed class ProviderSyncProgress
    {
        internal ProviderSyncProgress(int completed, int total, string phase)
        {
            Completed = completed;
            Total = total;
            Phase = phase;
        }

        internal int Completed { get; private set; }
        internal int Total { get; private set; }
        internal string Phase { get; private set; }
    }

    private sealed class LatestProviderSyncProgress : IProgress<ProviderSyncProgress>
    {
        private volatile ProviderSyncProgress latest;
        internal ProviderSyncProgress Latest { get { return latest; } }
        public void Report(ProviderSyncProgress value) { latest = value; }
    }

    private sealed class ProviderSyncResult
    {
        internal ProviderSyncResult(int total) { Total = total; }
        internal int Total { get; private set; }
    }

#if PROVIDER_SYNC_TEST
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> action;
        internal InlineProgress(Action<T> action) { this.action = action; }
        public void Report(T value) { action(value); }
    }
#endif

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static IntPtr FindVisibleChatGptWindow()
    {
        foreach (Process process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                IntPtr window = process.MainWindowHandle;
                if (window != IntPtr.Zero && IsWindowVisible(window)) return window;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return IntPtr.Zero;
    }

    private static void StopPackagedChatGptProcesses(string packageRoot, bool requireExit = false)
    {
        if (String.IsNullOrWhiteSpace(packageRoot)) return;
        string normalizedRoot = Path.GetFullPath(packageRoot).TrimEnd('\\') + "\\";
        foreach (string processName in new[] { "ChatGPT", "codex" })
        {
            foreach (Process target in Process.GetProcessesByName(processName))
            {
                bool matched = false;
                try
                {
                    string path = target.MainModule == null ? null : target.MainModule.FileName;
                    if (path == null || !path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    matched = true;
                    target.Kill();
                    if (requireExit && !target.WaitForExit(5000))
                        throw new InvalidOperationException("ChatGPT \u5c1a\u672a\u9000\u51fa\uff0c\u8bf7\u5173\u95ed\u5ba2\u6237\u7aef\u540e\u91cd\u8bd5\u3002");
                }
                catch (Exception exception)
                {
                    if (requireExit && (matched || processName == "ChatGPT"))
                        throw new InvalidOperationException("\u65e0\u6cd5\u505c\u6b62 ChatGPT\uff0c\u672a\u5207\u6362\u914d\u7f6e\u3002\u8bf7\u624b\u52a8\u9000\u51fa\u5ba2\u6237\u7aef\u540e\u91cd\u8bd5\u3002", exception);
                }
                finally { target.Dispose(); }
            }
        }
    }

    private static string FindLatestChatGptExecutable(out string packageRoot)
    {
        packageRoot = null;
        var candidates = new List<PackageCandidate>();
        using (RegistryKey packages = Registry.CurrentUser.OpenSubKey(PackageRegistryPath))
        {
            if (packages == null) return null;
            foreach (string packageName in packages.GetSubKeyNames())
            {
                if (!packageName.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) continue;
                using (RegistryKey package = packages.OpenSubKey(packageName))
                {
                    string root = package == null ? null : package.GetValue("PackageRootFolder") as string;
                    if (String.IsNullOrWhiteSpace(root)) continue;
                    string executable = Path.Combine(root, "app", "ChatGPT.exe");
                    if (File.Exists(executable)) candidates.Add(new PackageCandidate(ParseVersion(packageName), root, executable));
                }
            }
        }
        candidates.Sort(delegate(PackageCandidate left, PackageCandidate right) { return right.Version.CompareTo(left.Version); });
        if (candidates.Count == 0) return null;
        packageRoot = candidates[0].Root;
        return candidates[0].Executable;
    }

    private static ProcessStartInfo ClientActivationInfo(string packageRoot, ConfigData config)
    {
        string packageName = new DirectoryInfo(packageRoot).Name;
        Match match = Regex.Match(packageName, "^OpenAI\\.Codex_[^_]+_.*__([^_]+)$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("无法确定 ChatGPT 应用包标识。");
        var start = ClientStartInfo("explorer.exe", config);
        start.Arguments = "shell:AppsFolder\\OpenAI.Codex_" + match.Groups[1].Value + "!App";
        return start;
    }

    private static Version ParseVersion(string packageName)
    {
        const string prefix = "OpenAI.Codex_";
        int end = packageName.IndexOf('_', prefix.Length);
        string value = end < 0 ? packageName.Substring(prefix.Length) : packageName.Substring(prefix.Length, end - prefix.Length);
        Version version;
        return Version.TryParse(value, out version) ? version : new Version(0, 0, 0, 0);
    }

    private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }

    private sealed class PackageCandidate
    {
        internal PackageCandidate(Version version, string root, string executable)
        { Version = version; Root = root; Executable = executable; }
        internal Version Version { get; private set; }
        internal string Root { get; private set; }
        internal string Executable { get; private set; }
    }
}
