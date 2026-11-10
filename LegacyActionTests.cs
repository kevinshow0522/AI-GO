using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

// A self-contained provider/client integration test. It never targets a user app.
class LegacyActionTests
{
    sealed class FixtureWindow : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args[0] == "--fixture") { Fixture(); return; }
        var assembly = Assembly.LoadFile(Path.GetFullPath(args[0]));
        var type = assembly.GetType("AISwitchDesk.UiaAction");
        if (type == null) { Console.WriteLine("FAIL: no default-action support for selected upload option"); Environment.Exit(1); }
        var invoke = type.GetMethod("TryInvokeDefault", BindingFlags.Static | BindingFlags.NonPublic);
        var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--fixture")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
          RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var provider = Process.Start(start))
        {
            try
            {
                string[] handles = provider.StandardOutput.ReadLine().Split(',');
                var window = new IntPtr(long.Parse(handles[0]));
                var control = new IntPtr(long.Parse(handles[1]));
                var runtimeId = AutomationElement.FromHandle(control).GetRuntimeId();
                bool ok = (bool)invoke.Invoke(null, new object[] { window, runtimeId });
                var action = provider.StandardOutput.ReadLineAsync();
                if (!ok || !action.Wait(3000) || action.Result != "invoked")
                    throw new Exception("native default action did not execute the provider callback; returned=" + ok);
                bool wrong = (bool)invoke.Invoke(null, new object[] { window, new[] { 42, 999999, 999999 } });
                if (wrong) throw new Exception("unmatched runtime id acted on another control");
                Console.WriteLine("PASS: native default action executes provider callback; unmatched runtime id stays untouched");
            }
            catch (Exception error) { Console.WriteLine("FAIL: " + error); Environment.ExitCode = 1; }
            finally { provider.StandardInput.WriteLine("close"); if (!provider.WaitForExit(2000)) provider.Kill(); }
        }
    }

    static void Fixture()
    {
        using (var window = new FixtureWindow { Text = "AI Switch attachment action test", Width = 260, Height = 140, ShowInTaskbar = false })
        using (var option = new Button { Dock = DockStyle.Fill, Text = "Test attachment action" })
        {
            window.Controls.Add(option);
            option.Click += delegate { Console.WriteLine("invoked"); };
            window.Shown += delegate
            {
                Console.WriteLine(window.Handle.ToInt64() + "," + option.Handle.ToInt64());
                Task.Run(delegate { Console.ReadLine(); window.BeginInvoke(new Action(window.Close)); });
            };
            Application.Run(window);
        }
    }
}
