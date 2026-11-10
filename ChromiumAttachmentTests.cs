using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

// Integration fixture: an isolated browser profile and local test page only.
class ChromiumAttachmentTests
{
    delegate bool EnumWindow(IntPtr window, IntPtr unused);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr unused);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);

    [MTAThread]
    static void Main(string[] args)
    {
        string folder = Path.GetFullPath(Path.Combine("work", "ai-switch-desk", "build", "chromium-fixture-" + Process.GetCurrentProcess().Id));
        Directory.CreateDirectory(folder);
        string title = "AI Switch Attachment Fixture " + Process.GetCurrentProcess().Id;
        string html = Path.Combine(folder,"fixture.html");
        string file = Path.Combine(folder,"拖入测试 fixture.txt");
        File.WriteAllText(file,"Local attachment integration test. No network upload.");
        File.WriteAllText(html,"<!doctype html><meta charset=utf-8><title>" + title + "</title>" +
            "<style>body{font:20px sans-serif;padding:30px}#composer{padding:20px;border:1px solid #aaa}.ProseMirror{height:45px;border:1px solid #aaa}#menu{padding:10px}#option{padding:10px;background:#ddd}</style>" +
            "<section id=composer role=group><div id=prompt-textarea class=ProseMirror contenteditable=true role=textbox aria-label='随心输入'></div>" +
            "<button id=add aria-label='添加文件等内容' aria-haspopup=listbox aria-expanded=false onclick='menu.hidden=false;this.setAttribute(\"aria-expanded\",\"true\");let e=document.getElementById(\"prompt-textarea\");e.replaceWith(e.cloneNode(true))'>+</button>" +
            "<div id=menu role=listbox hidden><div id=option role=button tabindex=0 onpointerdown='document.title+=\" activated\";menu.hidden=true;add.setAttribute(\"aria-expanded\",\"false\");picker.click();document.title+=\" requested\"'>文件和文件夹</div></div>" +
            "<input id=picker type=file hidden onchange='attached.textContent=this.files[0].name'><span id=attached role=status></span></section>",new UTF8Encoding(false));
        var launch = new ProcessStartInfo("C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
            "--no-first-run --no-default-browser-check --force-renderer-accessibility --user-data-dir=\"" + Path.Combine(folder,"profile") + "\" --app=\"" + new Uri(html).AbsoluteUri + "\"")
            { UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden };
        IntPtr window = IntPtr.Zero;
        int fixturePid = 0;
        Process cover = null;
        IDisposable behind = null;
        using (var browser = Process.Start(launch))
        {
            try
            {
                var timeout = Stopwatch.StartNew();
                while (window == IntPtr.Zero && timeout.ElapsedMilliseconds < 15000)
                {
                    EnumWindows(delegate(IntPtr candidate, IntPtr unused) {
                        var text = new StringBuilder(300); GetWindowText(candidate,text,text.Capacity);
                        if (text.ToString().StartsWith(title,StringComparison.Ordinal)) { window = candidate; GetWindowThreadProcessId(candidate,out fixturePid); return false; }
                        return true;
                    }, IntPtr.Zero);
                    Thread.Sleep(100);
                }
                if (window == IntPtr.Zero) throw new Exception("isolated fixture browser did not open");
                Thread.Sleep(500);
                var assembly = Assembly.LoadFile(Path.GetFullPath(args[0]));
                var chatType = assembly.GetType("AISwitchDesk.ChatUi");
                var chat = Activator.CreateInstance(chatType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{window,new Func<bool>(()=>true)},null);
                chatType.GetMethod("WaitForEditor",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(chat,null);
                var editorMethod = chatType.GetMethod("Editor",BindingFlags.Instance|BindingFlags.NonPublic);
                var editor = (AutomationElement)editorMethod.Invoke(chat,null);
                var box = editor.Current.BoundingRectangle;
                chatType.GetField("DraftEditorId",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(chat,editor.GetRuntimeId());
                chatType.GetField("DraftEditorBox",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(chat,new[]{box.X,box.Y,box.Width,box.Height});
                int guards = 0;
                chatType.GetField("CheckDraftTarget",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(chat,new Action(delegate {
                    guards++;
                    var current = (AutomationElement)editorMethod.Invoke(chat,null);
                    if (!current.Current.IsEnabled) throw new Exception("guard incorrectly read the disabled main editor while a native picker was open");
                }));
                if (args.Length > 1 && args[1] == "--background")
                {
                    var coverStart = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"LegacyActionTests.exe"),"--fixture")
                    { UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden, RedirectStandardInput=true, RedirectStandardOutput=true };
                    cover = Process.Start(coverStart);
                    var coverHandle = new IntPtr(long.Parse(cover.StandardOutput.ReadLine().Split(',')[0]));
                    SetForegroundWindow(coverHandle);
                    behind = (IDisposable)Activator.CreateInstance(assembly.GetType("AISwitchDesk.BackgroundDrop"),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{window,coverHandle},null);
                }
                var method = chatType.GetMethod("AddDraftFile",BindingFlags.Instance|BindingFlags.NonPublic);
                method.Invoke(chat,new object[]{file});
                bool attached = (bool)chatType.GetMethod("HasFile",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(chat,new object[]{Path.GetFileName(file)});
                if (!attached) throw new Exception("local file never appeared in fixture composer");
                if (guards < 3) throw new Exception("draft identity checks were not exercised");
                Console.WriteLine("PASS: Chromium rerender -> guarded composer -> native picker -> Unicode file -> attachment visible" + (behind != null ? " behind another window" : ""));
            }
            catch (Exception error)
            {
                Console.WriteLine("FAIL: " + error);
                EnumWindows(delegate(IntPtr candidate, IntPtr unused) {
                    int pid; GetWindowThreadProcessId(candidate,out pid);
                    if (pid == fixturePid) { var text = new StringBuilder(250); var cls = new StringBuilder(100); GetWindowText(candidate,text,text.Capacity);GetClassName(candidate,cls,cls.Capacity); Console.WriteLine("fixture window="+candidate+" owner="+GetWindow(candidate,4)+" visible="+IsWindowVisible(candidate)+" class="+cls+" title="+text); }
                    return true;
                },IntPtr.Zero);
                Environment.ExitCode = 1;
                if (Array.IndexOf(args,"--inspect") >= 0) Thread.Sleep(45000);
            }
            finally
            {
                if (behind != null) behind.Dispose();
                if (window != IntPtr.Zero)
                {
                    // Only this fixture's browser process and its owned dialogs.
                    EnumWindows(delegate(IntPtr owned, IntPtr unused) { int pid; GetWindowThreadProcessId(owned,out pid); if(pid==fixturePid) PostMessage(owned,0x10,IntPtr.Zero,IntPtr.Zero); return true; },IntPtr.Zero);
                }
                if (cover != null) { cover.StandardInput.WriteLine("close"); if(!cover.WaitForExit(2000))cover.Kill(); cover.Dispose(); }
            }
        }
    }
}
