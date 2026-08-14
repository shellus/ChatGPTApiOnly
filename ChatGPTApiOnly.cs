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

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // Explorer can still use the executable's embedded icon.
            }

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
                Text = "\u914d\u7f6e API",
                TabIndex = 0,
                AccessibleName = "\u914d\u7f6e API",
                AccessibleDescription = "\u6253\u5f00\u81ea\u5b9a\u4e49 API \u914d\u7f6e\u3002\u4e5f\u53ef\u6309\u7a7a\u683c\u952e\u3002"
            };
            configureButton.Click += delegate { OpenConfiguration(); };

            var shortcutLabel = new Label
            {
                AutoSize = true,
                Location = new Point(24, 120),
                ForeColor = SystemColors.GrayText,
                Text = "\u5feb\u6377\u952e\uff1a\u7a7a\u683c",
                AccessibleName = "\u914d\u7f6e API \u5feb\u6377\u952e\uff1a\u7a7a\u683c"
            };

            Controls.Add(iconBox);
            Controls.Add(titleLabel);
            Controls.Add(statusLabel);
            Controls.Add(progressBar);
            Controls.Add(shortcutLabel);
            Controls.Add(configureButton);

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
            statusLabel.Text = "\u6b63\u5728\u542f\u52a8 ChatGPT\uff0c\u9884\u8ba1\u7ea6 4 \u79d2";
            pollTimer.Start();
        }

        private void StartChatGpt()
        {
            try
            {
                string executable = FindLatestChatGptExecutable(out packageRoot);
                if (executable == null)
                {
                    throw new FileNotFoundException(
                        "The Microsoft Store OpenAI Codex / ChatGPT application is not installed."
                    );
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = Quote("--host-resolver-rules=" + ResolverRules),
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    UseShellExecute = false
                });
            }
            catch (Exception exception)
            {
                pollTimer.Stop();
                progressBar.Value = 0;
                MessageBox.Show(this, exception.Message, "ChatGPT API Only",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void LoadingFormOnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Space || openingConfiguration)
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
            statusLabel.Text = "\u6b63\u5728\u505c\u6b62 ChatGPT\u2026";
            Refresh();

            StopPackagedChatGptProcesses(packageRoot);
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
        private readonly TextBox providerNameTextBox;
        private readonly TextBox baseUrlTextBox;
        private readonly TextBox apiKeyTextBox;
        private readonly TextBox modelTextBox;
        private readonly TextBox reasoningTextBox;
        private readonly ErrorProvider errors;
        private readonly Button saveButton;
        private readonly Button cancelButton;
        private readonly Button repairButton;
        private readonly Label repairProgressCaption;
        private readonly ProgressBar repairProgressBar;
        private readonly Label repairProgressLabel;
        private bool repairInProgress;

        internal ConfigForm(ConfigData config)
        {
            Text = "\u914d\u7f6e\u81ea\u5b9a\u4e49 API";
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
                Location = new Point(24, 20),
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point),
                Text = "\u914d\u7f6e\u81ea\u5b9a\u4e49 API"
            };
            var intro = new Label
            {
                Location = new Point(24, 51),
                Size = new Size(520, 38),
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
            reasoningTextBox = AddField("\u63a8\u7406\u7ea7\u522b", 290,
                String.IsNullOrWhiteSpace(config.ReasoningEffort) ? "high" : config.ReasoningEffort, 5);
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
                Text = "\u4fdd\u5b58\u5e76\u542f\u52a8",
                TabIndex = 7
            };
            saveButton.Click += SaveButtonOnClick;

            cancelButton = new Button
            {
                Location = new Point(486, 374),
                Size = new Size(62, 30),
                Text = "\u53d6\u6d88",
                DialogResult = DialogResult.Cancel,
                TabIndex = 8
            };

            errors = new ErrorProvider { BlinkStyle = ErrorBlinkStyle.NeverBlink };
            errors.ContainerControl = this;
            AcceptButton = saveButton;
            CancelButton = cancelButton;
            Controls.Add(heading);
            Controls.Add(intro);
            Controls.Add(repairButton);
            Controls.Add(repairProgressCaption);
            Controls.Add(repairProgressBar);
            Controls.Add(repairProgressLabel);
            Controls.Add(saveButton);
            Controls.Add(cancelButton);
        }

        private async void RepairButtonOnClick(object sender, EventArgs e)
        {
            ShowRepairProgress();
            SetRepairBusy(true);
            repairProgressBar.Maximum = 1;
            repairProgressBar.Value = 0;
            repairProgressLabel.Text = "\u6b63\u5728\u7edf\u8ba1\u2026";

            try
            {
                var progress = new Progress<ProviderSyncProgress>(delegate(ProviderSyncProgress value)
                {
                    int maximum = Math.Max(1, value.Total);
                    repairProgressBar.Maximum = maximum;
                    repairProgressBar.Value = Math.Min(maximum, Math.Max(0, value.Completed));
                    repairProgressLabel.Text = String.Format("{0}/{1}", value.Completed, value.Total);
                });
                ProviderSyncResult result = await Task.Run(delegate
                {
                    return ProviderSynchronizer.Synchronize(
                        ConfigStore.ConfigDirectory, "custom", progress);
                });

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
                repairProgressLabel.Text = "\u4fee\u590d\u5931\u8d25";
                MessageBox.Show(this,
                    "\u65e0\u6cd5\u4fee\u590d\u5bf9\u8bdd\uff1a" + Environment.NewLine + exception.Message,
                    "ChatGPT API Only", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetRepairBusy(false);
                HideRepairProgress();
            }
        }

        private void ShowRepairProgress()
        {
            saveButton.Location = new Point(368, 414);
            cancelButton.Location = new Point(486, 414);
            ClientSize = new Size(572, 450);
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
            saveButton.Location = new Point(368, 374);
            cancelButton.Location = new Point(486, 374);
            ClientSize = new Size(572, 410);
        }

        private void SetRepairBusy(bool busy)
        {
            repairInProgress = busy;
            providerNameTextBox.Enabled = !busy;
            baseUrlTextBox.Enabled = !busy;
            apiKeyTextBox.Enabled = !busy;
            modelTextBox.Enabled = !busy;
            reasoningTextBox.Enabled = !busy;
            repairButton.Enabled = !busy;
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
            var label = new Label
            {
                AutoSize = true,
                Location = new Point(24, top + 6),
                Text = labelText
            };
            var textBox = new TextBox
            {
                Location = new Point(150, top),
                Size = new Size(398, 23),
                Text = value,
                TabIndex = tabIndex,
                AccessibleName = labelText
            };
            Controls.Add(label);
            Controls.Add(textBox);
            return textBox;
        }

        private void SaveButtonOnClick(object sender, EventArgs e)
        {
            errors.Clear();
            Control firstInvalid = null;

            ValidateRequired(providerNameTextBox, "\u8bf7\u8f93\u5165\u63d0\u4f9b\u8005\u540d\u79f0\u3002", ref firstInvalid);
            if (!ConfigStore.IsValidBaseUrl(baseUrlTextBox.Text))
            {
                errors.SetError(baseUrlTextBox, "API \u5730\u5740\u5fc5\u987b\u4ee5 https:// \u5f00\u5934\u5e76\u4ee5 /v1 \u7ed3\u5c3e\u3002");
                if (firstInvalid == null) firstInvalid = baseUrlTextBox;
            }
            ValidateRequired(apiKeyTextBox, "\u8bf7\u8f93\u5165 API Key\u3002", ref firstInvalid);
            ValidateRequired(modelTextBox, "\u8bf7\u8f93\u5165\u6a21\u578b\u540d\u3002", ref firstInvalid);
            ValidateRequired(reasoningTextBox, "\u8bf7\u8f93\u5165\u63a8\u7406\u7ea7\u522b\u3002", ref firstInvalid);

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
                    ReasoningEffort = reasoningTextBox.Text.Trim(),
                    AuthMode = "apikey"
                };
                saveButton.Enabled = false;
                saveButton.Text = "\u6b63\u5728\u4fdd\u5b58\u2026";
                UseWaitCursor = true;
                Refresh();

                ConfigStore.Save(data);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                UseWaitCursor = false;
                saveButton.Enabled = true;
                saveButton.Text = "\u4fdd\u5b58\u5e76\u542f\u52a8";
                MessageBox.Show(this,
                    "\u65e0\u6cd5\u4fdd\u5b58\u914d\u7f6e\uff1a" + Environment.NewLine + exception.Message,
                    "ChatGPT API Only", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ValidateRequired(TextBox textBox, string message, ref Control firstInvalid)
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
        internal bool AuthModePresent;
        internal string WireApi;
        internal bool? RequiresOpenAiAuth;
        internal bool ConfigReadable;
        internal bool AuthReadable;

        internal bool IsValid
        {
            get
            {
                bool authModeValid = !AuthModePresent ||
                    String.Equals(AuthMode, "apikey", StringComparison.OrdinalIgnoreCase);
                return ConfigReadable && AuthReadable &&
                    !String.IsNullOrWhiteSpace(ProviderName) &&
                    ConfigStore.IsValidBaseUrl(BaseUrl) &&
                    !String.IsNullOrWhiteSpace(ApiKey) &&
                    !String.IsNullOrWhiteSpace(Model) &&
                    !String.IsNullOrWhiteSpace(ReasoningEffort) &&
                    String.Equals(WireApi, "responses", StringComparison.Ordinal) &&
                    RequiresOpenAiAuth == false && authModeValid;
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

        internal static ConfigData Load()
        {
            var data = new ConfigData();
            LoadToml(data);
            LoadAuth(data);
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

        internal static void Save(ConfigData data)
        {
            Directory.CreateDirectory(ConfigDirectory);
            string existingToml = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath, Encoding.UTF8) : String.Empty;
            string updatedToml = UpdateToml(existingToml, data);
            WriteAtomic(ConfigPath, updatedToml);

            Dictionary<string, object> auth = new Dictionary<string, object>(StringComparer.Ordinal);
            if (File.Exists(AuthPath))
            {
                try
                {
                    var parsed = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(AuthPath, Encoding.UTF8))
                        as Dictionary<string, object>;
                    if (parsed != null) auth = parsed;
                }
                catch { }
            }
            auth["auth_mode"] = "apikey";
            auth["OPENAI_API_KEY"] = data.ApiKey;
            WriteAtomic(AuthPath, new JavaScriptSerializer().Serialize(auth) + Environment.NewLine);
        }

        private static void LoadToml(ConfigData data)
        {
            if (!File.Exists(ConfigPath)) return;
            try
            {
                string section = String.Empty;
                foreach (string rawLine in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    string line = StripTomlComment(rawLine).Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    int equals = FindUnquotedEquals(line);
                    if (equals < 1) continue;
                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    if (section.Length == 0)
                    {
                        if (key == "model_provider" && ParseTomlString(value) != "custom") data.ConfigReadable = false;
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
                string all = File.ReadAllText(ConfigPath, Encoding.UTF8);
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
                    data.AuthModePresent = true;
                    data.AuthMode = value as string;
                }
                data.AuthReadable = true;
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
            SetSectionValue(lines, "model_providers.custom", "requires_openai_auth", "false");
            while (lines.Count > 0 && String.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.RemoveAt(lines.Count - 1);
            return String.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
        }

        private static void SetTopLevel(List<string> lines, string key, string value)
        {
            int end = lines.FindIndex(delegate(string line) { return line.TrimStart().StartsWith("["); });
            if (end < 0) end = lines.Count;
            int found = -1;
            for (int i = 0; i < end; i++)
                if (Regex.IsMatch(lines[i], "^\\s*" + Regex.Escape(key) + "\\s*=")) { found = i; break; }
            string replacement = key + " = " + value;
            if (found >= 0) lines[found] = replacement; else lines.Insert(end, replacement);
        }

        private static void SetSectionValue(List<string> lines, string section, string key, string value)
        {
            int start = lines.FindIndex(delegate(string line) { return line.Trim() == "[" + section + "]"; });
            if (start < 0)
            {
                if (lines.Count > 0 && !String.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.Add(String.Empty);
                lines.Add("[" + section + "]");
                lines.Add(key + " = " + value);
                return;
            }
            int end = start + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;
            for (int i = start + 1; i < end; i++)
                if (Regex.IsMatch(lines[i], "^\\s*" + Regex.Escape(key) + "\\s*="))
                { lines[i] = key + " = " + value; return; }
            lines.Insert(end, key + " = " + value);
        }

        private static string StripTomlComment(string line)
        {
            bool quoted = false; bool escaped = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (escaped) { escaped = false; continue; }
                if (quoted && c == '\\') { escaped = true; continue; }
                if (c == '"') quoted = !quoted;
                else if (c == '#' && !quoted) return line.Substring(0, i);
            }
            return line;
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
            internal string OriginalText;
            internal string UpdatedText;
            internal DateTime LastWriteTimeUtc;
        }

        internal static ProviderSyncResult Synchronize(string codexHome, string targetProvider,
            IProgress<ProviderSyncProgress> progress)
        {
            if (String.IsNullOrWhiteSpace(targetProvider) ||
                targetProvider.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("\u5bf9\u8bdd\u63d0\u4f9b\u8005 ID \u65e0\u6548\u3002");

            Directory.CreateDirectory(codexHome);
            List<RolloutChange> rolloutChanges = CollectRolloutChanges(codexHome, targetProvider);
            List<string> databasePaths = FindDatabasePaths(codexHome);
            int databaseUpdateCount = 0;
            foreach (string databasePath in databasePaths)
                databaseUpdateCount += CountDatabaseUpdates(databasePath, targetProvider);
            int total = rolloutChanges.Count + databaseUpdateCount;
            ReportProgress(progress, 0, total);
            if (total == 0) return new ProviderSyncResult(0);

            string backupDirectory = CreateBackup(codexHome, targetProvider, rolloutChanges, databasePaths);
            var appliedRollouts = new List<RolloutChange>();
            int completed = 0;

            try
            {
                foreach (RolloutChange change in rolloutChanges)
                {
                    File.WriteAllText(change.Path, change.UpdatedText, new UTF8Encoding(false));
                    File.SetLastWriteTimeUtc(change.Path, change.LastWriteTimeUtc);
                    appliedRollouts.Add(change);
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
                        File.WriteAllText(change.Path, change.OriginalText, new UTF8Encoding(false));
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
            int completed, int total)
        {
            if (progress != null) progress.Report(new ProviderSyncProgress(completed, total));
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

        private static List<RolloutChange> CollectRolloutChanges(string codexHome, string targetProvider)
        {
            var changes = new List<RolloutChange>();
            foreach (string directoryName in new[] { "sessions", "archived_sessions" })
            {
                string root = Path.Combine(codexHome, directoryName);
                if (!Directory.Exists(root)) continue;
                foreach (string path in Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    string original = File.ReadAllText(path, Encoding.UTF8);
                    string updated = RewriteSessionMetadata(original, targetProvider);
                    if (String.Equals(original, updated, StringComparison.Ordinal)) continue;
                    changes.Add(new RolloutChange
                    {
                        Path = path,
                        OriginalText = original,
                        UpdatedText = updated,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(path)
                    });
                }
            }
            return changes;
        }

        private static string RewriteSessionMetadata(string text, string targetProvider)
        {
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
            string legacy = Path.Combine(codexHome, "state_5.sqlite");
            if (File.Exists(legacy) && DatabaseHasProviderColumn(legacy)) paths.Add(legacy);
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
            List<RolloutChange> rolloutChanges, List<string> databasePaths)
        {
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
            }

            foreach (RolloutChange change in rolloutChanges)
            {
                string relative = MakeRelativePath(codexHome, change.Path);
                string destination = Path.Combine(backup, "sessions", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllText(destination, change.OriginalText, new UTF8Encoding(false));
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
        internal ProviderSyncProgress(int completed, int total)
        {
            Completed = completed;
            Total = total;
        }

        internal int Completed { get; private set; }
        internal int Total { get; private set; }
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

    private static void StopPackagedChatGptProcesses(string packageRoot)
    {
        if (String.IsNullOrWhiteSpace(packageRoot)) return;
        string normalizedRoot = Path.GetFullPath(packageRoot).TrimEnd('\\') + "\\";
        foreach (string processName in new[] { "ChatGPT", "codex" })
        {
            foreach (Process target in Process.GetProcessesByName(processName))
            {
                try
                {
                    string path = target.MainModule == null ? null : target.MainModule.FileName;
                    if (path == null || !path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    target.Kill();
                }
                catch { }
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
