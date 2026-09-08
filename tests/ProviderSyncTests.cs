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

    [STAThread]
    private static int Main()
    {
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
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
                        if (name.ToString() == "#32770") PostMessage(window, 0x0111, new IntPtr(1), IntPtr.Zero);
                        return true;
                    }, IntPtr.Zero);
                    if (elapsed.Elapsed.TotalSeconds > 20)
                    {
                        error = "UI repair timed out";
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
