#if PROVIDER_SYNC_TEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class ProviderSyncTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly Type App = typeof(ChatGPTApiOnly);
    private static readonly Type Sync = App.GetNestedType("ProviderSynchronizer", Flags);
    private static readonly List<object> Reports = new List<object>();
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static string fixture;
    private static long retainedAtRepair;

    private static object Call(Type type, string method, params object[] args)
    {
        foreach (MethodInfo candidate in type.GetMethods(Flags))
            if (candidate.Name == method && candidate.GetParameters().Length == args.Length)
                return candidate.Invoke(null, args);
        throw new MissingMethodException(method);
    }

    private static object Field(object owner, string name)
    {
        return owner.GetType().GetField(name, Flags).GetValue(owner);
    }

    private static object Property(object owner, string name)
    {
        return owner.GetType().GetProperty(name, Flags).GetValue(owner, null);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Record(object value)
    {
        Reports.Add(value);
        if ((string)Property(value, "Phase") == "\u4fee\u590d\u5bf9\u8bdd" &&
            (int)Property(value, "Completed") == 0 && (int)Property(value, "Total") > 0)
            retainedAtRepair = Math.Max(retainedAtRepair, GC.GetTotalMemory(true));
    }

    private static object RunSync()
    {
        Reports.Clear();
        Type progressType = App.GetNestedType("ProviderSyncProgress", Flags);
        Type actionType = typeof(Action<>).MakeGenericType(progressType);
        Delegate action = Delegate.CreateDelegate(actionType, typeof(ProviderSyncTests).GetMethod("Record", Flags));
        Type inlineType = App.GetNestedType("InlineProgress`1", Flags).MakeGenericType(progressType);
        object progress = Activator.CreateInstance(inlineType, Flags, null, new object[] { action }, null);
        return Call(Sync, "Synchronize", fixture, "custom", progress);
    }

    private static void UseFixture(string root, string name)
    {
        fixture = Path.Combine(root, name);
        Directory.CreateDirectory(fixture);
        Environment.SetEnvironmentVariable("CHATGPT_API_ONLY_CONFIG_DIR", fixture);
    }

    private static string Rollout(string directory, string name, string provider)
    {
        string folder = Path.Combine(fixture, directory);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".jsonl");
        File.WriteAllText(path, "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":\"" + provider + "\"}}\r\n" +
            "{\"type\":\"event_msg\",\"payload\":\"example\"}\n" +
            "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":\"" + provider + "\"}}", Utf8);
        return path;
    }

    private static object Database(string sql, bool query)
    {
        object[] open = { Path.Combine(fixture, "state_5.sqlite"), IntPtr.Zero };
        Check((int)Call(Sync, "sqlite3_open16", open) == 0, "SQLite open failed");
        try { return Call(Sync, query ? "QueryInteger" : "ExecuteSql", open[1], sql); }
        finally { Call(Sync, "sqlite3_close", open[1]); }
    }

    private static void SeedDatabase(bool fail)
    {
        Database("CREATE TABLE threads (model_provider TEXT); INSERT INTO threads VALUES ('example-old');" +
            "CREATE TABLE local_thread_catalog (model_provider TEXT); INSERT INTO local_thread_catalog VALUES ('example-old');" +
            (fail ? "CREATE TRIGGER fail_update BEFORE UPDATE ON local_thread_catalog BEGIN SELECT RAISE(ABORT, 'example failure'); END;" : ""), false);
    }

    private static void TestClientStore()
    {
        int prompts = 0;
        int opens = 0;
        Func<DialogResult> accept = delegate { prompts++; return DialogResult.OK; };
        Func<DialogResult> cancel = delegate { prompts++; return DialogResult.Cancel; };
        Action open = delegate { opens++; };
        Check((bool)Call(App, "EnsureClientInstalled", true, accept, open), "Installed client blocked");
        Check(prompts == 0 && opens == 0, "Installed client triggered installation");
        Check(!(bool)Call(App, "EnsureClientInstalled", false, cancel, open), "Cancelled installation continued startup");
        Check(prompts == 1 && opens == 0, "Cancel opened Store");
        Check(!(bool)Call(App, "EnsureClientInstalled", false, accept, open), "Installation link treated as installed client");
        Check(prompts == 2 && opens == 1, "Confirmed installation did not open Store");

        Type store = App.GetNestedType("ClientStore", Flags);
        var links = new List<string>();
        Action<string> launch = delegate(string uri) { links.Add(uri); };
        Call(store, "OpenLink", false, launch);
        Call(store, "OpenLink", true, launch);
        Check(links.Count == 2 && links[0] == "ms-windows-store://pdp/?ProductId=9PLM9XGG6VKS" &&
            links[1] == "ms-windows-store://downloadsandupdates", "Incorrect Store destinations");
        foreach (bool updates in new[] { false, true })
        {
            links.Clear();
            Action<string> noStore = delegate(string uri)
            {
                links.Add(uri);
                if (uri.StartsWith("ms-windows-store:")) throw new InvalidOperationException("example unavailable");
            };
            Call(store, "OpenLink", updates, noStore);
            Check(links.Count == 2 && links[1] == "https://apps.microsoft.com/detail/9PLM9XGG6VKS", "Browser fallback missing");
        }
        bool failed = false;
        try
        {
            Action<string> unavailable = delegate { throw new InvalidOperationException("example unavailable"); };
            Call(store, "OpenLink", false, unavailable);
        }
        catch (TargetInvocationException exception)
        {
            failed = exception.InnerException.Message.Contains("https://apps.microsoft.com/detail/9PLM9XGG6VKS");
        }
        Check(failed, "Launch failure did not provide manual URL");
        Console.WriteLine("PASS: installed/missing client, installation confirm/cancel, Store/update URLs, browser fallback and failure");
    }

    private static void TestOAuthModes(string root, object customData)
    {
        UseFixture(root, "oauth");
        Type store = App.GetNestedType("ConfigStore", Flags);
        string configPath = Path.Combine(fixture, "config.toml");
        string authPath = Path.Combine(fixture, "auth.json");
        string profilesPath = Path.Combine(fixture, "launcher-profiles", "modes.json");
        string officialAuth = "{\"auth_mode\":\"chatgpt\",\"OPENAI_API_KEY\":null,\"tokens\":{\"access_token\":\"example-access\",\"refresh_token\":\"example-refresh\",\"id_token\":\"example-id\",\"account_id\":\"example-account\"},\"last_refresh\":\"example-time\"}";
        Call(store, "Save", customData);
        string rollout = Rollout("sessions", "example", "example-old");
        string originalRollout = File.ReadAllText(rollout);
        File.WriteAllText(authPath, officialAuth, Utf8);
        object mixed = Call(store, "Load");
        Check(!(bool)Field(mixed, "OfficialMode") && (bool)Field(mixed, "HasOfficialCredentials") &&
            !(bool)Property(mixed, "IsValid"), "Mixed OAuth/custom state was silently accepted");
        Call(store, "SaveOfficial");
        string officialConfig = File.ReadAllText(configPath);
        Check(officialConfig.Contains("model_provider = \"openai\"") &&
            !officialConfig.Contains("forced_login_method") && !officialConfig.Contains("cli_auth_credentials_store") &&
            !officialConfig.Contains("model = \"example\""), "Official route or model defaults incorrect");
        Check(File.ReadAllText(authPath) == officialAuth, "Official credentials changed");
        object official = Call(store, "Load");
        Check((bool)Property(official, "IsValid") && (bool)Field(official, "OfficialMode"), "Official mode not recognized");
        var start = (ProcessStartInfo)Call(App, "ClientStartInfo", "example.exe", official);
        Check(start.Arguments == "" && !start.EnvironmentVariables.ContainsKey("OPENAI_BASE_URL") &&
            !start.EnvironmentVariables.ContainsKey("OPENAI_API_KEY"), "Official start uses custom network parameters");

        string refreshed = officialAuth.Replace("example-refresh", "example-refreshed");
        File.WriteAllText(authPath, refreshed, Utf8);
        File.WriteAllText(configPath, "model = \"example-official\"\n" + officialConfig, Utf8);
        Call(store, "Save", customData);
        Check(!File.ReadAllText(authPath).Contains("tokens"), "OAuth tokens leaked into custom active auth");
        object custom = Call(store, "Load");
        Check((bool)Property(custom, "IsValid"), "Custom mode not restored");
        start = (ProcessStartInfo)Call(App, "ClientStartInfo", "example.exe", custom);
        Check(start.Arguments.Contains("host-resolver-rules"), "Custom startup acceleration lost");
        Call(store, "SaveOfficial");
        Check(File.ReadAllText(authPath) == refreshed, "Latest refreshed credentials not restored");
        Check(File.ReadAllText(configPath).Contains("model = \"example-official\""), "Official model setting not retained");
        Check((string)Field(Call(store, "Load"), "ApiKey") == "example", "Saved API key missing from custom form");
        Check(File.ReadAllText(rollout) == originalRollout, "Mode switch changed history");

        // Switching tabs and cancelling must be completely read-only.
        string beforeConfig = File.ReadAllText(configPath);
        string beforeAuth = File.ReadAllText(authPath);
        Type formType = App.GetNestedType("ConfigForm", Flags);
        using (var form = (Form)Activator.CreateInstance(formType, Flags, null, new[] { Call(store, "Load") }, null))
        {
            var tabs = (TabControl)Field(form, "modeTabs");
            Check(tabs.SelectedTab == (TabPage)Field(form, "officialTab"), "Official tab not selected");
            form.Show();
            Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(root, "official-mode.png"));
            }
            tabs.SelectedTab = (TabPage)Field(form, "customTab");
            Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(root, "custom-mode.png"));
            }
            tabs.SelectedTab = (TabPage)Field(form, "officialTab");
            form.Close();
        }
        Check(beforeConfig == File.ReadAllText(configPath) && beforeAuth == File.ReadAllText(authPath), "Tab selection wrote configuration");

        // A failed config replacement must restore both credentials and saved mode state.
        string beforeProfiles = File.ReadAllText(profilesPath);
        bool failed = false;
        using (var locked = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { Call(store, "Save", customData); } catch (TargetInvocationException) { failed = true; }
        }
        Check(failed && beforeAuth == File.ReadAllText(authPath) && beforeProfiles == File.ReadAllText(profilesPath) &&
            beforeConfig == File.ReadAllText(configPath), "Failed mode switch did not restore state");

        // A logout must not resurrect the saved official session.
        File.Delete(authPath);
        Call(store, "Save", customData);
        Call(store, "SaveOfficial");
        Check(!File.ReadAllText(authPath).Contains("example-access"), "Logout resurrected old token");
        Check(!(bool)Property(Call(store, "Load"), "IsValid"), "Missing official tokens reported valid");
        TestModeConflicts(root, customData, officialAuth);
        UseFixture(root, "oauth-first-run");
        Call(store, "SaveOfficial");
        Check((bool)Field(Call(store, "Load"), "OfficialMode") && !(bool)Property(Call(store, "Load"), "IsValid"),
            "First official launch should wait for client login");
        Console.WriteLine("PASS: mixed state, OAuth/custom switching, refreshed credentials, startup arguments, read-only tabs, rollback, logout, history preserved");
    }

    private static void TestModeConflicts(string root, object customData, string officialAuth)
    {
        Type store = App.GetNestedType("ConfigStore", Flags);
        UseFixture(root, "mode-conflicts");
        Call(store, "Save", customData);
        string config = Path.Combine(fixture, "config.toml");
        string auth = Path.Combine(fixture, "auth.json");
        string profiles = Path.Combine(fixture, "launcher-profiles", "modes.json");
        string baseline = File.ReadAllText(config);
        File.WriteAllText(auth, officialAuth, Utf8);
        foreach (bool official in new[] { false, true })
        {
            foreach (string entry in new[] {
                "profile = \"example\"\n", "cli_auth_credentials_store = \"keyring\"\n",
                "cli_auth_credentials_store = \"auto\"\n", "cli_auth_credentials_store = \"ephemeral\"\n",
                "chatgpt_base_url = \"https://example.com\"\n", "openai_base_url = \"https://example.com/v1\"\n",
                "[model_providers.openai]\nbase_url = \"https://example.com/v1\"\n",
                "[model_providers.\"openai\"] # example\nbase_url = \"https://example.com/v1\"\n" })
            {
                bool routingOnly = entry.StartsWith("chatgpt_base_url") || entry.StartsWith("openai_base_url") || entry.StartsWith("[");
                if (!official && routingOnly) continue;
                string content = entry.StartsWith("[") ? baseline + "\n" + entry : entry + baseline;
                File.WriteAllText(config, content, Utf8);
                string beforeAuth = File.ReadAllText(auth);
                string beforeProfiles = File.ReadAllText(profiles);
                bool rejected = false;
                try { if (official) Call(store, "SaveOfficial"); else Call(store, "Save", customData); }
                catch (TargetInvocationException exception) { rejected = exception.InnerException.Message.Contains("config.toml"); }
                Check(rejected && File.ReadAllText(config) == content && File.ReadAllText(auth) == beforeAuth &&
                    File.ReadAllText(profiles) == beforeProfiles, "Conflict changed files: " + entry);
                Check(!(bool)Property(Call(store, "Load"), "IsValid"), "Conflict accepted on startup");
            }
        }
        foreach (bool official in new[] { false, true })
        {
            File.WriteAllText(config, "forced_login_method = \"api\"\ncli_auth_credentials_store = \"file\" # example keep\n" + baseline, Utf8);
            if (official) Call(store, "SaveOfficial"); else Call(store, "Save", customData);
            string updated = File.ReadAllText(config);
            Check(!updated.Contains("forced_login_method") && updated.Contains("cli_auth_credentials_store = \"file\" # example keep"),
                "Legacy restriction not removed or file storage changed");
        }
        File.WriteAllText(config, "chatgpt_base_url = \"https://example.com\"\n" + baseline, Utf8);
        Call(store, "Save", customData);
        Check(File.ReadAllText(config).Contains("chatgpt_base_url = \"https://example.com\""), "Unrelated routing field deleted");
        Console.WriteLine("PASS: legacy restriction cleanup, existing file backend preserved, conflicting routing/storage rejected without file changes");
    }

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string root = Path.Combine(Path.GetTempPath(), "ChatGPTApiOnly-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            UseFixture(root, "success");
            string path = Rollout("sessions", "example", "example-old");
            Rollout("archived_sessions", "example", "example-old");
            string original = File.ReadAllText(path);
            SeedDatabase(false);
            Check((int)Property(RunSync(), "Total") == 4, "Wrong repair total");
            Check(File.ReadAllText(path) == original.Replace("example-old", "custom"), "Content or newline changed");
            Check((int)Database("SELECT COUNT(*) FROM threads WHERE model_provider='custom'", true) == 1, "Database not updated");
            string backup = Directory.GetDirectories(Path.Combine(fixture, "backups_state", "provider-sync"))[0];
            Check(File.ReadAllText(Path.Combine(backup, "sessions", "sessions", "example.jsonl")) == original, "Rollout backup missing");
            Check(File.Exists(Path.Combine(backup, "db", "state_5.sqlite")), "Database backup missing");
            var phases = new HashSet<string>();
            foreach (object report in Reports)
            {
                phases.Add((string)Property(report, "Phase"));
                Check((int)Property(report, "Completed") <= (int)Property(report, "Total"), "Invalid progress");
            }
            Check(phases.Count == 4, "Missing scan/database/backup/repair phases");
            Check((int)Property(RunSync(), "Total") == 0, "Repair not idempotent");
            Check(Directory.GetDirectories(Path.Combine(fixture, "backups_state", "provider-sync")).Length == 1, "No-op created backup");
            Console.WriteLine("PASS: success, exact backup, newline preservation, phase counts, idempotence");

            UseFixture(root, "rollback");
            path = Rollout("sessions", "example", "example-old");
            original = File.ReadAllText(path);
            SeedDatabase(true);
            bool failed = false;
            try { RunSync(); } catch (TargetInvocationException) { failed = true; }
            Check(failed && File.ReadAllText(path) == original, "Database failure did not restore rollout");
            Check((int)Database("SELECT COUNT(*) FROM threads WHERE model_provider='example-old'", true) == 1, "Transaction did not roll back");
            Check(Directory.GetDirectories(Path.Combine(fixture, "backups_state", "provider-sync")).Length == 1, "Failed operation has no backup");
            Console.WriteLine("PASS: database failure restores rollout and rolls back earlier table update");

            UseFixture(root, "save");
            path = Rollout("sessions", "example", "example-old");
            original = File.ReadAllText(path);
            Type dataType = App.GetNestedType("ConfigData", Flags);
            object data = Activator.CreateInstance(dataType, true);
            foreach (string name in new[] { "ProviderName", "ApiKey", "Model" }) dataType.GetField(name, Flags).SetValue(data, "example");
            dataType.GetField("BaseUrl", Flags).SetValue(data, "https://example.com/v1");
            dataType.GetField("ReasoningEffort", Flags).SetValue(data, "medium");
            Call(App.GetNestedType("ConfigStore", Flags), "Save", data);
            Check(File.ReadAllText(path) == original && !Directory.Exists(Path.Combine(fixture, "backups_state")), "Saving triggered repair");
            Console.WriteLine("PASS: saving configuration does not repair history");
            TestClientStore();
            TestOAuthModes(root, data);

            UseFixture(root, "large-history");
            string eventLine = "{\"type\":\"event_msg\",\"payload\":\"" + new string('x', 8192) + "\"}\n";
            long inputBytes = 0;
            for (int index = 0; index < 32; index++)
            {
                path = Rollout("sessions", "example-" + index, "example-old");
                using (var writer = new StreamWriter(path, true, Utf8))
                {
                    writer.Write('\n');
                    for (int line = 0; line < 128; line++) writer.Write(eventLine);
                }
                inputBytes += new FileInfo(path).Length;
            }
            retainedAtRepair = 0;
            Check((int)Property(RunSync(), "Total") == 32, "Large history repair failed");
            Check(retainedAtRepair < inputBytes, "Repair retains entire history in memory");
            Check(Directory.GetFiles(fixture, "*.tmp-*", SearchOption.AllDirectories).Length == 0, "Temporary rollout left behind");
            Console.WriteLine("PASS: {0} MiB history; retained managed memory {1} MiB", inputBytes / 1048576, retainedAtRepair / 1048576);

            UseFixture(root, "ui");
            for (int index = 0; index < 40; index++) Rollout("sessions", "example-" + index, "example-old");
            Environment.SetEnvironmentVariable("CHATGPT_API_ONLY_PROGRESS_DELAY_MS", "15");
            Type formType = App.GetNestedType("ConfigForm", Flags);
            using (var form = (Form)Activator.CreateInstance(formType, Flags, null, new object[] { data }, null))
            using (var timer = new Timer { Interval = 25 })
            {
                var seen = new HashSet<string>();
                var elapsed = Stopwatch.StartNew();
                int ticks = 0;
                bool started = false;
                bool screenshot = false;
                string error = null;
                timer.Tick += delegate
                {
                    ticks++;
                    bool busy = (bool)Field(form, "repairInProgress");
                    if (busy)
                    {
                        var caption = (Label)Field(form, "repairProgressCaption");
                        var bar = (ProgressBar)Field(form, "repairProgressBar");
                        if (bar.Value > 0 && bar.Value < bar.Maximum) seen.Add(caption.Text);
                        if (!screenshot && bar.Value > 0 && bar.Value < bar.Maximum)
                        {
                            using (var bitmap = new Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                                bitmap.Save(Path.Combine(root, "repair-progress.png"));
                            }
                            screenshot = true;
                        }
                    }
                    EnumThreadWindows(GetCurrentThreadId(), delegate(IntPtr window, IntPtr state)
                    {
                        var name = new StringBuilder(80);
                        GetClassName(window, name, name.Capacity);
                        if (name.ToString() == "#32770") PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                        return true;
                    }, IntPtr.Zero);
                    if (elapsed.Elapsed.TotalSeconds > 20)
                    {
                        error = String.Format("UI repair timed out: {0} ticks, {1} phases, {2} {3}",
                            ticks, seen.Count, ((Label)Field(form, "repairProgressCaption")).Text,
                            ((Label)Field(form, "repairProgressLabel")).Text);
                        formType.GetField("repairInProgress", Flags).SetValue(form, false);
                        form.Close();
                    }
                    else if (started && !busy) form.Close();
                };
                form.Shown += delegate
                {
                    started = true;
                    timer.Start();
                    ((Button)Field(form, "repairButton")).PerformClick();
                };
                Application.Run(form);
                Check(error == null && ticks > 15 && seen.Count >= 3, "UI did not refresh during all phases: " + error);
                Check(!(bool)Field(form, "repairInProgress"), "Busy state not cleared");
                Console.WriteLine("PASS: UI heartbeat {0} ticks; visible intermediate progress in {1} phases", ticks, seen.Count);
            }
            Console.WriteLine("Artifacts: " + root);
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Environment.SetEnvironmentVariable("CHATGPT_API_ONLY_PROGRESS_DELAY_MS", null); }
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
#endif
