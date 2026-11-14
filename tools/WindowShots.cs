// Opens the built sidebar's own windows with sample data and saves a picture
// of each: the rail, the four settings pages, the usage popup, the file drop
// targets and the app menu, in a light and a dark theme. Nothing is read from
// or written to the installed sidebar's data; a temporary folder stands in.
//
//   .\preview.ps1 -Windows
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Wpf = System.Windows;
using WpfMedia = System.Windows.Media;

internal static class WindowShots
{
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    static string output;
    static readonly List<string> problems = new List<string>();

    static void Pump(int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            Application.DoEvents();
            Wpf.Threading.Dispatcher.CurrentDispatcher.Invoke(Wpf.Threading.DispatcherPriority.ApplicationIdle, new Action(delegate { }));
            System.Threading.Thread.Sleep(10);
        }
        while (clock.ElapsedMilliseconds < milliseconds);
    }

    static void Save(Control control, string name)
    {
        if (control.Width < 1 || control.Height < 1) { problems.Add(name + ": empty"); return; }
        using (var image = new Bitmap(control.Width, control.Height))
        {
            control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size));
            image.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
        }
    }

    static void Save(Wpf.Window window, string name)
    {
        var root = window.Content as Wpf.FrameworkElement;
        if (root == null || root.ActualWidth < 1 || root.ActualHeight < 1) { problems.Add(name + ": empty"); return; }
        const double scale = 2;
        var image = new WpfMedia.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
            (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, WpfMedia.PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new WpfMedia.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(WpfMedia.Imaging.BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(file);
    }

    static void Try(string name, Action shot)
    {
        try { shot(); }
        catch (Exception error) { problems.Add(name + ": " + (error.InnerException ?? error).Message); }
    }

    // Sample data, so the pages have something on them.
    static void Seed(string root)
    {
        long now = DateTime.UtcNow.Ticks;
        long hour = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds(), week = DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds();
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(root, "quota-ChatGPT.json"), "{\"Source\":\"OpenAI · Codex\",\"ReadAt\":" + now +
            ",\"Rows\":[{\"Window\":\"5 小时\",\"Remaining\":62,\"ResetEpoch\":" + hour + "},{\"Window\":\"7 天\",\"Remaining\":34,\"ResetEpoch\":" + week + "}]}", utf8);
        File.WriteAllText(Path.Combine(root, "quota-Claude.json"), "{\"Source\":\"Claude · 官方用量页\",\"ReadAt\":" + (now - TimeSpan.FromHours(2).Ticks) +
            ",\"Rows\":[{\"Window\":\"当前会话\",\"Remaining\":18,\"Reset\":\"3 小时后重置\"},{\"Window\":\"每周\",\"Remaining\":71,\"Reset\":\"周四 09:00 重置\"}]}", utf8);
        File.WriteAllText(Path.Combine(root, "upload-queue.json"),
            "[{\"Batch\":\"a1\",\"Path\":\"C:\\\\Users\\\\me\\\\Desktop\\\\季度报告.pdf\",\"Client\":1,\"Error\":\"聊天已切换，请回到拖入时的 Claude 对话再重试。\"}," +
            "{\"Batch\":\"a2\",\"Path\":\"C:\\\\Users\\\\me\\\\Desktop\\\\方案 A.md\",\"Client\":0}]", utf8);
        string[][] jobs = {
            new[] { "产品方案.md", "done", "Claude 的修改稿已送回发起任务的 GPT 对话。", "true" },
            new[] { "周报.docx", "await-result", "Claude 已收到原件，修改完成后将自动回到原 GPT 对话。", "true" },
            new[] { "接口说明.md", "ready", "目标输入框已有草稿。请先处理草稿，再点继续。", "false" }
        };
        for (int i = 0; i < jobs.Length; i++)
        {
            string id = Guid.NewGuid().ToString("N");
            string folder = Path.Combine(root, "handoffs", id);
            Directory.CreateDirectory(Path.Combine(folder, "file"));
            File.WriteAllText(Path.Combine(folder, "state.json"), "{\"Id\":\"" + id + "\",\"From\":\"ChatGPT\",\"To\":\"Claude\",\"Name\":\"" + jobs[i][0] +
                "\",\"Instruction\":\"请润色并指出问题。\",\"Stage\":\"" + jobs[i][1] + "\",\"Detail\":\"" + jobs[i][2] + "\",\"Background\":true,\"RoundTrip\":" + jobs[i][3] +
                ",\"Held\":" + (i == 2 ? "true" : "false") + ",\"Created\":" + (now - i * TimeSpan.FromMinutes(7).Ticks) + ",\"Updated\":" + now + "}", utf8);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        var app = Assembly.LoadFile(Path.GetFullPath(args[0]));
        output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        string root = Path.Combine(Path.GetTempPath(), "ai-go-shots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Type store = app.GetType("AISwitchDesk.RelayStore");
        store.GetField("Root", Static).SetValue(null, root);
        store.GetField("Jobs", Static).SetValue(null, Path.Combine(root, "handoffs"));
        Seed(root);

        Application.EnableVisualStyles();
        Type railType = app.GetType("AISwitchDesk.DeskForm");
        Type themeType = app.GetType("AISwitchDesk.DeskTheme");
        using (var rail = (Form)Activator.CreateInstance(railType, Members, null, new object[0], null))
        {
            rail.Show();
            ((Timer)railType.GetField("poll", Members).GetValue(rail)).Stop();
            Pump(400);
            Func<string, object[], object> call = delegate(string name, object[] values)
            {
                return railType.GetMethod(name, Members).Invoke(rail, values);
            };
            foreach (string id in new[] { "codex-light", "github-dark" })
            {
                string tag = id.EndsWith("dark") ? "dark" : "light";
                object current = themeType.GetProperty("Current", Static).GetValue(null, null);
                Array choices = (Array)themeType.GetField("Choices", Static).GetValue(null);
                object preset = choices.Cast<object>().First(p => (string)p.GetType().GetField("Id", Members).GetValue(p) == id);
                object theme = themeType.GetMethod("WithPreset", Members).Invoke(current, new[] { preset });
                call("SetTheme", new[] { theme });
                Pump(250);
                Try("rail-" + tag, delegate { Save(rail, "rail-" + tag); });

                Try("settings-" + tag, delegate
                {
                    call("OpenSettings", null);
                    Pump(500);
                    object settings = railType.GetField("settings", Members).GetValue(rail);
                    var window = (Wpf.Window)settings.GetType().GetField("window", Members).GetValue(settings);
                    foreach (string page in new[] { "Appearance", "Use", "Log", "Queue" })
                    {
                        ((Wpf.Controls.RadioButton)window.FindName(page + "Tab")).IsChecked = true;
                        Pump(450);
                        Save(window, "settings-" + page.ToLowerInvariant() + "-" + tag);
                    }
                    call("OpenSettings", null);
                    Pump(200);
                });

                Try("usage-" + tag, delegate
                {
                    call("OpenUsage", null);
                    Pump(350);
                    var popup = (ToolStripDropDown)railType.GetField("usagePopup", Members).GetValue(rail);
                    Save((Control)popup.GetType().GetField("card", Members).GetValue(popup), "usage-" + tag);
                    popup.Close();
                    Pump(150);
                });

                Try("drop-" + tag, delegate
                {
                    call("ShowDropTargets", new object[] { 1 });
                    Pump(300);
                    var targets = (Form)railType.GetField("dropTargets", Members).GetValue(rail);
                    Save(targets, "drop-" + tag);
                    targets.GetType().GetMethod("HideTargets", Members).Invoke(targets, null);
                    Pump(150);
                });

                Try("menu-" + tag, delegate
                {
                    call("ShowExitMenu", new object[] { 0, new Point(rail.Right + 8, rail.Top + 40), true });
                    Pump(350);
                    object menu = railType.GetField("exitMenu", Members).GetValue(rail);
                    Save((Wpf.Window)menu.GetType().GetField("window", Members).GetValue(menu), "menu-" + tag);
                    call("CloseExitMenu", null);
                    Pump(150);
                });
            }
            rail.Close();
        }
        foreach (string problem in problems) Console.WriteLine("MISSING " + problem);
        Console.WriteLine("Wrote " + Directory.GetFiles(output, "*.png").Length + " pictures to " + output);
        return 0;
    }
}
