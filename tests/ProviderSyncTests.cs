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
        Call(store, "SaveOfficial", String.Empty);
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
        Call(store, "SaveOfficial", String.Empty);
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
        Call(store, "SaveOfficial", String.Empty);
        Check(!File.ReadAllText(authPath).Contains("example-access"), "Logout resurrected old token");
        Check(!(bool)Property(Call(store, "Load"), "IsValid"), "Missing official tokens reported valid");
        TestModeConflicts(root, customData, officialAuth);
        UseFixture(root, "oauth-first-run");
        Call(store, "SaveOfficial", String.Empty);
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
                "forced_login_method = \"api\"\n", "model_providers.example.name = \"example\"\n" })
            {
                bool routingOnly = entry.StartsWith("chatgpt_base_url") || entry.StartsWith("openai_base_url") || entry.StartsWith("[");
                if (!official && routingOnly) continue;
                string content = entry.StartsWith("[") ? baseline + "\n" + entry : entry + baseline;
                File.WriteAllText(config, content, Utf8);
                string beforeAuth = File.ReadAllText(auth);
                string beforeProfiles = File.ReadAllText(profiles);
                bool rejected = false;
                try { if (official) Call(store, "SaveOfficial", String.Empty); else Call(store, "Save", customData); }
                catch (TargetInvocationException exception) { rejected = exception.InnerException.Message.Contains("config.toml"); }
                Check(rejected && File.ReadAllText(config) == content && File.ReadAllText(auth) == beforeAuth &&
                    File.ReadAllText(profiles) == beforeProfiles, "Conflict changed files: " + entry);
                Check(!(bool)Property(Call(store, "Load"), "IsValid"), "Conflict accepted on startup");
            }
        }
        foreach (bool official in new[] { false, true })
        {
            File.WriteAllText(config, "cli_auth_credentials_store = \"file\" # example keep\n" + baseline, Utf8);
            if (official) Call(store, "SaveOfficial", String.Empty); else Call(store, "Save", customData);
            string updated = File.ReadAllText(config);
            Check(updated.Contains("cli_auth_credentials_store = \"file\" # example keep"), "File storage changed");
        }
        File.WriteAllText(config, "chatgpt_base_url = \"https://example.com\"\n" + baseline, Utf8);
        Call(store, "Save", customData);
        Check(File.ReadAllText(config).Contains("chatgpt_base_url = \"https://example.com\""), "Unrelated routing field deleted");
        Console.WriteLine("PASS: existing file backend preserved, conflicting routing/storage rejected without file changes");
    }

    private static void TestProviderProfiles(string root, object customData)
    {
        UseFixture(root, "provider-profiles");
        Type store = App.GetNestedType("ConfigStore", Flags);
        Call(store, "Save", customData);
        string config = Path.Combine(fixture, "config.toml");
        string profiles = Path.Combine(fixture, "launcher-profiles", "modes.json");
        string extras = "[model_providers.custom.http_headers]\nexample = 'value#example'\n" +
            "[model_providers.\"openai\"] # example\nbase_url = \"https://example.com/v1\"\n" +
            "['model_providers'.example]\nunknown = '''\n[not_a_table]\nexample\n'''\n" +
            "description = \"literal ''' example\"\n" +
            "array = [\n[\"example\"],\n[\"model_providers\"],\n]\n";
        string unrelated = "[features]\nexample = true\n";
        string initial = File.ReadAllText(config).Replace("[model_providers.custom]", "[model_providers.\"custom\"] # example") + extras + unrelated;
        File.WriteAllText(config, initial, Utf8);
        Call(store, "SaveOfficial", String.Empty);
        Check(!File.ReadAllText(config).Contains("model_providers") && File.ReadAllText(config).Contains(unrelated.Replace("\n", Environment.NewLine)),
            "Official config retained provider tables or lost unrelated tables");
        var saved = (Dictionary<string, object>)Call(store, "ReadProfiles");
        Check(((string)Call(store, "SelectedValue", saved, "model_providers_toml")).Contains(extras), "Provider snapshot lost unknown/nested/multiline values");
        string snapshot = (string)Call(store, "SelectedValue", saved, "model_providers_toml");
        Call(store, "SaveOfficial", String.Empty);
        saved = (Dictionary<string, object>)Call(store, "ReadProfiles");
        Check((string)Call(store, "SelectedValue", saved, "model_providers_toml") == snapshot, "Repeated official save erased providers");
        object loaded = Call(store, "Load");
        Check((string)Field(loaded, "BaseUrl") == "https://example.com/v1" && (string)Field(loaded, "ProviderName") == "example",
            "Official mode failed to populate custom form from snapshot");
        Call(store, "Save", loaded);
        string restored = File.ReadAllText(config);
        Check(restored.Contains(extras.Replace("\n", Environment.NewLine)) &&
            !restored.Contains("[model_providers.custom]"), "Restore lost providers or duplicated quoted custom table");
        Call(store, "Save", loaded);
        Check(File.ReadAllText(config) == restored, "Repeated custom save not idempotent");
        string auth = Path.Combine(fixture, "auth.json");
        string beforeAuth = File.ReadAllText(auth), beforeProfiles = File.ReadAllText(profiles);
        bool failed = false;
        using (var locked = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { Call(store, "SaveOfficial", String.Empty); } catch (TargetInvocationException) { failed = true; }
        }
        Check(failed && File.ReadAllText(config) == restored && File.ReadAllText(auth) == beforeAuth &&
            File.ReadAllText(profiles) == beforeProfiles, "Provider removal failure did not restore all files");
        Console.WriteLine("PASS: complete provider snapshot, clean official config, repeated saves, form restore, quoted/nested/multiline tables and rollback");
    }

    private static void TestOfficialProxy(string root, object customData)
    {
        UseFixture(root, "official-proxy");
        Type store = App.GetNestedType("ConfigStore", Flags);
        string proxy = "http://127.0.0.1:17890";
        Call(store, "SaveOfficial", proxy + "/");
        object official = Call(store, "Load");
        Check((string)Field(official, "OfficialProxyUrl") == proxy, "Proxy setting not persisted or normalized");
        var before = Environment.GetEnvironmentVariables();
        var start = (ProcessStartInfo)Call(App, "ClientStartInfo", "example.exe", official);
        Check(start.Arguments.Contains("--proxy-server=" + proxy) && !start.Arguments.Contains("host-resolver-rules"),
            "Official Chromium proxy missing or cloud endpoints blocked");
        foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
            Check(start.EnvironmentVariables[key] == proxy, "Backend proxy missing: " + key);
        Check(start.EnvironmentVariables["NODE_USE_ENV_PROXY"] == "1" &&
            start.EnvironmentVariables["NO_PROXY"] == "localhost,127.0.0.1,::1", "Node proxy or loopback bypass missing");
        foreach (System.Collections.DictionaryEntry entry in before)
            Check(Environment.GetEnvironmentVariable((string)entry.Key) == (string)entry.Value, "Parent environment changed");
        string config = Path.Combine(fixture, "config.toml"), auth = Path.Combine(fixture, "auth.json"),
            profiles = Path.Combine(fixture, "launcher-profiles", "modes.json");
        string originalConfig = File.ReadAllText(config), originalAuth = File.ReadAllText(auth), originalProfiles = File.ReadAllText(profiles);
        foreach (string invalid in new[] { "127.0.0.1:17890", "socks5://localhost:17890", "http://user:pass@localhost:17890",
            "http://localhost:17890/path", "http://localhost:17890/?x=1", "http://localhost:0" })
        {
            bool rejected = false;
            try { Call(store, "SaveOfficial", invalid); } catch (TargetInvocationException) { rejected = true; }
            Check(rejected && File.ReadAllText(config) == originalConfig && File.ReadAllText(auth) == originalAuth &&
                File.ReadAllText(profiles) == originalProfiles, "Invalid proxy changed mode files: " + invalid);
        }
        bool failed = false;
        using (var locked = new FileStream(profiles, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { Call(store, "SaveOfficial", "http://localhost:17891"); } catch (TargetInvocationException) { failed = true; }
        }
        Check(failed && File.ReadAllText(profiles) == originalProfiles && File.ReadAllText(auth) == originalAuth,
            "Failed proxy save did not roll back");
        Call(store, "Save", customData);
        object custom = Call(store, "Load");
        Check((string)Field(custom, "OfficialProxyUrl") == proxy, "Custom save erased official proxy");
        start = (ProcessStartInfo)Call(App, "ClientStartInfo", "example.exe", custom);
        Check(!start.Arguments.Contains("proxy-server") && start.Arguments.Contains("host-resolver-rules"),
            "Official proxy applied to custom mode");
        Check(start.EnvironmentVariables["HTTPS_PROXY"] == Environment.GetEnvironmentVariable("HTTPS_PROXY"),
            "Custom proxy environment changed");
        Call(store, "SaveOfficial", String.Empty);
        start = (ProcessStartInfo)Call(App, "ClientStartInfo", "example.exe", Call(store, "Load"));
        Check(start.Arguments == String.Empty && start.EnvironmentVariables["HTTPS_PROXY"] == Environment.GetEnvironmentVariable("HTTPS_PROXY"),
            "Empty proxy still injected launch settings");
        Console.WriteLine("PASS: scoped Chromium/backend/Node proxy, parent environment unchanged, validation, mode persistence, rollback and disabling");
    }

    private static void TestCliProxyDotEnv(string root, object customData)
    {
        Type store = App.GetNestedType("ConfigStore", Flags);
        const string proxy = "http://127.0.0.1:17890";
        foreach (string original in new[] { "", "# example\nEXAMPLE_VALUE='keep # text'", "EXAMPLE_VALUE=keep\r\n",
            "HTTPS_PROXY=http://example.invalid:8080\nNODE_USE_ENV_PROXY=0\nEXAMPLE_VALUE=keep\n" })
        {
            UseFixture(root, "dotenv-" + Guid.NewGuid().ToString("N"));
            string dotenv = Path.Combine(fixture, ".env");
            File.WriteAllText(dotenv, original, Utf8);
            var parent = Environment.GetEnvironmentVariables();
            Call(store, "SaveOfficial", proxy);
            string active = File.ReadAllText(dotenv);
            Check(active.StartsWith(original, StringComparison.Ordinal) &&
                active.Contains("HTTPS_PROXY=" + proxy) && active.Contains("HTTP_PROXY=" + proxy) &&
                active.Contains("ALL_PROXY=" + proxy) && active.Contains("NODE_USE_ENV_PROXY=1") &&
                active.Contains("NO_PROXY=localhost,127.0.0.1,::1"), "CLI proxy block missing or original dotenv changed");
            Check(Environment.GetEnvironmentVariables().Count == parent.Count, "Saving dotenv added parent variables");
            foreach (System.Collections.DictionaryEntry entry in parent)
                Check(Environment.GetEnvironmentVariable((string)entry.Key) == (string)entry.Value, "Saving dotenv changed parent environment");
            Call(store, "SaveOfficial", proxy);
            Check(File.ReadAllText(dotenv) == active, "Repeated proxy save duplicated or changed dotenv block");
            Call(store, "SaveOfficial", "http://localhost:17891");
            Check(!File.ReadAllText(dotenv).Contains("HTTPS_PROXY=" + proxy), "Changed proxy retained stale managed address");
            Call(store, "Save", customData);
            Check(File.ReadAllText(dotenv) == original, "Custom mode did not restore original dotenv");
            Check((string)Field(Call(store, "Load"), "OfficialProxyUrl") == "http://localhost:17891", "Custom mode erased proxy preference");
            Call(store, "SaveOfficial", proxy);
            Call(store, "SaveOfficial", String.Empty);
            Check(File.ReadAllText(dotenv) == original, "Disabling proxy changed user dotenv values");
        }
        UseFixture(root, "dotenv-rollback");
        string withSuffix = (string)Call(store, "UpdateProxyDotEnv", "EXAMPLE_FIRST=one", proxy) + "EXAMPLE_LAST=two\n";
        Check((string)Call(store, "UpdateProxyDotEnv", withSuffix, String.Empty) == "EXAMPLE_FIRST=one\nEXAMPLE_LAST=two\n",
            "Removing managed block joined user entries across its boundaries");
        string envPath = Path.Combine(fixture, ".env");
        Call(store, "Save", customData);
        Check(!File.Exists(envPath), "Custom mode created an unnecessary dotenv file");
        string config = Path.Combine(fixture, "config.toml"), auth = Path.Combine(fixture, "auth.json"),
            profiles = Path.Combine(fixture, "launcher-profiles", "modes.json");
        string beforeConfig = File.ReadAllText(config), beforeAuth = File.ReadAllText(auth), beforeProfiles = File.ReadAllText(profiles);
        bool failed = false;
        using (var locked = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { Call(store, "SaveOfficial", proxy); } catch (TargetInvocationException) { failed = true; }
        }
        Check(failed && !File.Exists(envPath) && File.ReadAllText(config) == beforeConfig &&
            File.ReadAllText(auth) == beforeAuth && File.ReadAllText(profiles) == beforeProfiles,
            "Late save failure did not remove new dotenv and restore all mode files");
        const string originalEnv = "# example\nEXAMPLE_KEEP=yes\n";
        File.WriteAllText(envPath, originalEnv, Utf8);
        failed = false;
        using (var locked = new FileStream(profiles, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { Call(store, "SaveOfficial", proxy); } catch (TargetInvocationException) { failed = true; }
        }
        Check(failed && File.ReadAllText(envPath) == originalEnv, "Failure did not restore existing dotenv");
        failed = false;
        using (var locked = new FileStream(envPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { Call(store, "SaveOfficial", proxy); } catch (TargetInvocationException) { failed = true; }
        }
        Check(failed && File.ReadAllText(envPath) == originalEnv && File.ReadAllText(config) == beforeConfig &&
            File.ReadAllText(auth) == beforeAuth && File.ReadAllText(profiles) == beforeProfiles,
            "Locked dotenv changed active mode files");
        foreach (string malformed in new[] { "# BEGIN CHATGPT API ONLY PROXY\n",
            "# END CHATGPT API ONLY PROXY\n", "# END CHATGPT API ONLY PROXY\n# BEGIN CHATGPT API ONLY PROXY\n",
            "# BEGIN CHATGPT API ONLY PROXY\n# BEGIN CHATGPT API ONLY PROXY\n# END CHATGPT API ONLY PROXY\n" })
        {
            File.WriteAllText(envPath, malformed, Utf8);
            var draft = Call(store, "ReadEditableProfiles");
            string id = (string)((Dictionary<string, object>)draft)["selected_custom"];
            failed = false;
            try { Call(store, "ValidateProfiles", draft, false, id); } catch (TargetInvocationException) { failed = true; }
            Check(failed && File.ReadAllText(envPath) == malformed && File.ReadAllText(config) == beforeConfig &&
                File.ReadAllText(auth) == beforeAuth && File.ReadAllText(profiles) == beforeProfiles,
                "Malformed dotenv block was not rejected before applying");
        }
        Console.WriteLine("PASS: CLI dotenv proxy, preservation, idempotence, mode switch, disable, preflight and four-file rollback");
    }

    private static void TestProfileLibrary(string root)
    {
        UseFixture(root, "profile-library");
        Type store = App.GetNestedType("ConfigStore", Flags);
        var profiles = (Dictionary<string, object>)Call(store, "ReadProfiles");
        string first = (string)Call(store, "AddProfile", profiles, false, "Example API", null);
        var selected = (Dictionary<string, object>)Call(store, "SelectedProfile", profiles, "custom_providers");
        selected["custom_key"] = "example-key";
        selected["model_providers_toml"] = "[model_providers.custom]\nunknown = 'example'\n";
        string second = (string)Call(store, "AddProfile", profiles, false, "Example copy", first);
        var copy = (Dictionary<string, object>)Call(store, "SelectedProfile", profiles, "custom_providers");
        Check(first != second && (string)copy["custom_key"] == "example-key" &&
            (string)copy["model_providers_toml"] == (string)selected["model_providers_toml"], "API copy incomplete");
        copy["custom_key"] = "example-copy-key";
        Check((string)selected["custom_key"] == "example-key", "Copy shares mutable state with source");
        Check((string)Call(store, "DeleteProfile", profiles, false, second) == first, "Deleting selected profile did not choose next");
        Check((string)Call(store, "DeleteProfile", profiles, false, first) == String.Empty, "Last deletion left dangling selection");
        string account = (string)Call(store, "AddProfile", profiles, true, "Example account", null);
        Check(account.Length > 0 && ((object[])profiles["official_accounts"]).Length == 1, "Unlogged account cannot be created");
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"example@example.com\",\"name\":\"Example User\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string auth = "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"account_id\":\"example-account\",\"id_token\":\"example." + payload + ".example\"}}";
        var identity = (Dictionary<string, object>)Call(store, "AccountIdentity", auth);
        Check((string)identity["account_id"] == "example-account" && (string)identity["email"] == "example@example.com" &&
            (string)identity["account_name"] == "Example User", "Account identification failed");
        identity = (Dictionary<string, object>)Call(store, "AccountIdentity", auth.Replace(payload, "invalid!"));
        Check((string)identity["account_id"] == "example-account" && !identity.ContainsKey("email"), "Malformed JWT lost stable identity");
        Check(!File.Exists(Path.Combine(fixture, "auth.json")) && !File.Exists(Path.Combine(fixture, "config.toml")) &&
            !File.Exists(Path.Combine(fixture, "launcher-profiles", "modes.json")), "Draft operations wrote active files");
        Console.WriteLine("PASS: profile draft add/copy/delete, independent keys, empty selection, account identity and read-only drafts");
        // Switching official entries must not rewrite model settings or restore a logged-out entry.
        File.WriteAllText(Path.Combine(fixture, "config.toml"), "model_provider = \"openai\"\nmodel = \"example-official\"\n", Utf8);
        var firstAccount = (Dictionary<string, object>)Call(store, "SelectedProfile", profiles, "official_accounts");
        firstAccount["official_auth"] = "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"account_id\":\"example-first\",\"access_token\":\"example-first-access\",\"refresh_token\":\"example-first-refresh\"}}";
        string secondAccount = (string)Call(store, "AddProfile", profiles, true, "Example second", null);
        var secondEntry = (Dictionary<string, object>)Call(store, "SelectedProfile", profiles, "official_accounts");
        secondEntry["official_auth"] = ((string)firstAccount["official_auth"]).Replace("example-first", "example-second");
        string configBefore = File.ReadAllText(Path.Combine(fixture, "config.toml"));
        DateTime configTimestamp = File.GetLastWriteTimeUtc(Path.Combine(fixture, "config.toml"));
        Call(store, "ApplyProfiles", profiles, true, account);
        Check(File.ReadAllText(Path.Combine(fixture, "auth.json")).Contains("example-first-access"), "First account not activated");
        profiles = (Dictionary<string, object>)Call(store, "ReadProfiles");
        File.WriteAllText(Path.Combine(fixture, "auth.json"), ((string)firstAccount["official_auth"]).Replace("example-first-refresh", "example-refreshed"), Utf8);
        Call(store, "ApplyProfiles", profiles, true, secondAccount);
        profiles = (Dictionary<string, object>)Call(store, "ReadProfiles");
        Check(File.ReadAllText(Path.Combine(fixture, "auth.json")).Contains("example-second-access") &&
            File.ReadAllText(Path.Combine(fixture, "config.toml")) == configBefore, "Official account switch altered routing/model or wrong credentials");
        Check(File.GetLastWriteTimeUtc(Path.Combine(fixture, "config.toml")) == configTimestamp,
            "Official account switch rewrote unchanged config file");
        Call(store, "ApplyProfiles", profiles, true, account);
        Check(File.ReadAllText(Path.Combine(fixture, "auth.json")).Contains("example-refreshed"), "Refresh token was not retained across account switch");
        profiles = (Dictionary<string, object>)Call(store, "ReadProfiles");
        File.Delete(Path.Combine(fixture, "auth.json"));
        Call(store, "ApplyProfiles", profiles, true, secondAccount);
        profiles = (Dictionary<string, object>)Call(store, "ReadProfiles");
        Call(store, "ApplyProfiles", profiles, true, account);
        Check(!File.ReadAllText(Path.Combine(fixture, "auth.json")).Contains("example-first-access"), "Logged-out account resurrected");
        Console.WriteLine("PASS: two official accounts, refreshed credentials, unchanged official config and logout invalidation");

        UseFixture(root, "multi-api-transactions");
        profiles = (Dictionary<string, object>)Call(store, "ReadProfiles");
        first = (string)Call(store, "AddProfile", profiles, false, "Example first", null);
        selected = (Dictionary<string, object>)Call(store, "SelectedProfile", profiles, "custom_providers");
        selected["custom_key"] = "example-first-key";
        selected["custom_model"] = "example-first-model";
        selected["custom_effort"] = "high";
        selected["model_providers_toml"] = "[model_providers.custom]\nname = 'example-first'\nbase_url = 'https://example.com/v1'\nunknown = 'example-preserved'\n";
        Call(store, "ApplyProfiles", profiles, false, first);
        profiles = (Dictionary<string, object>)Call(store, "ReadEditableProfiles");
        second = (string)Call(store, "AddProfile", profiles, false, "Example second", first);
        copy = (Dictionary<string, object>)Call(store, "SelectedProfile", profiles, "custom_providers");
        copy["custom_key"] = "example-second-key";
        copy["custom_model"] = "example-second-model";
        string history = Rollout("sessions", "example-history", "example-original");
        string historyBefore = File.ReadAllText(history);
        string configPath = Path.Combine(fixture, "config.toml");
        string authPath = Path.Combine(fixture, "auth.json");
        string profilesPath = Path.Combine(fixture, "launcher-profiles", "modes.json");
        string[] before = { File.ReadAllText(configPath), File.ReadAllText(authPath), File.ReadAllText(profilesPath) };
        Call(store, "ValidateProfiles", profiles, false, second);
        Check(File.ReadAllText(configPath) == before[0] && File.ReadAllText(authPath) == before[1] &&
            File.ReadAllText(profilesPath) == before[2], "Preflight validation wrote files");
        using (var locked = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false;
            try { Call(store, "ApplyProfiles", profiles, false, second); } catch (TargetInvocationException) { failed = true; }
            Check(failed, "Locked configuration did not fail");
        }
        Check(File.ReadAllText(configPath) == before[0] && File.ReadAllText(authPath) == before[1] &&
            File.ReadAllText(profilesPath) == before[2], "Multi-profile transaction failed to restore selections and credentials");
        Call(store, "ApplyProfiles", profiles, false, second);
        Check(File.ReadAllText(authPath).Contains("example-second-key") && File.ReadAllText(configPath).Contains("example-second-model"), "Second API not activated");
        profiles = (Dictionary<string, object>)Call(store, "ReadEditableProfiles");
        Call(store, "ApplyProfiles", profiles, false, first);
        Check(File.ReadAllText(authPath).Contains("example-first-key") && File.ReadAllText(configPath).Contains("example-first-model") &&
            File.ReadAllText(configPath).Contains("example-preserved"), "API switching lost independent settings or unknown TOML");
        profiles = (Dictionary<string, object>)Call(store, "ReadEditableProfiles");
        Call(store, "DeleteProfile", profiles, false, first);
        Call(store, "ApplyProfiles", profiles, false, second);
        Check(File.ReadAllText(authPath).Contains("example-second-key") && File.ReadAllText(history) == historyBefore,
            "Deleting active API did not switch or touched history");
        Console.WriteLine("PASS: independent API switching, preflight without writes, transaction rollback, active deletion and history preservation");
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
            TestProviderProfiles(root, data);
            TestOfficialProxy(root, data);
            TestCliProxyDotEnv(root, data);
            TestProfileLibrary(root);

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
