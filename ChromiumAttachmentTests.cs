using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const string Original = "原始内容 · UTF-8\n";
    const string RevisedPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jvV0AAAAASUVORK5CYII=";

    static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static string Title(IntPtr window)
    {
        var text = new StringBuilder(300);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    static void CheckHandoff(Assembly assembly, object chat, string folder, IntPtr window)
    {
        var store = assembly.GetType("AISwitchDesk.RelayStore");
        store.GetField("Root", Static).SetValue(null, folder);
        store.GetField("Jobs", Static).SetValue(null, Path.Combine(folder, "handoffs"));
        var create = store.GetMethod("Create", Static);
        var parameters = create.GetParameters();
        var args = parameters.Select(p => p.IsOptional ? p.DefaultValue : null).ToArray();
        args[0] = "ChatGPT"; args[1] = "原件 A.txt"; args[2] = "校对"; args[4] = Original;
        args[Array.FindIndex(parameters, p => p.Name == "roundTrip")] = true;
        var job = create.Invoke(null, args);
        var type = job.GetType();
        var id = type.GetField("Id").GetValue(job);
        var load = store.GetMethod("Load", Static);
        var save = store.GetMethod("Save", Static);
        var revision = type.GetField("Revision");
        int initial = (int)revision.GetValue(job);
        string stateFile = Path.Combine(folder, "handoffs", (string)id, "state.json");
        using (File.Open(stateFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                save.Invoke(null, new[] { job });
                throw new Exception("A checkpoint replaced a locked state file.");
            }
            catch (TargetInvocationException error)
            {
                if (!(error.GetBaseException() is IOException) || (int)revision.GetValue(job) != initial) throw;
            }
        }
        using (var held = File.Open(stateFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var release = new Thread(() => { Thread.Sleep(200); held.Close(); });
            release.Start();
            try { save.Invoke(null, new[] { job }); }
            finally { release.Join(); }
        }
        Assert((int)revision.GetValue(load.Invoke(null, new[] { id })) == initial + 1, "Retry did not persist exactly one revision.");
        args[Array.FindIndex(parameters, p => p.Name == "fixedId")] = id;
        Assert((string)type.GetField("Id").GetValue(create.Invoke(null, args)) == (string)id, "Identical request was not idempotent.");
        args[Array.FindIndex(parameters, p => p.Name == "roundTrip")] = false;
        try
        {
            create.Invoke(null, args);
            throw new Exception("Changing the return option reused an existing job.");
        }
        catch (TargetInvocationException error)
        {
            if (!error.GetBaseException().Message.Contains("回传设置")) throw;
        }

        var attach = chat.GetType().GetMethod("Attach", Instance);
        var advance = assembly.GetType("AISwitchDesk.RelayPump").GetMethod("Advance", Static);
        var editor = (AutomationElement)chat.GetType().GetMethod("Editor", Instance).Invoke(chat, null);
        var value = (ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern);
        value.SetValue("现有草稿");
        try
        {
            attach.Invoke(chat, new[] { job });
            throw new Exception("An unrelated draft was accepted.");
        }
        catch (TargetInvocationException error)
        {
            if (!(error.GetBaseException() is InvalidOperationException) || value.Current.Value != "现有草稿") throw;
        }
        value.SetValue("");
        var toggle = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "other-file"));
        var toggleFile = (InvokePattern)toggle.GetCurrentPattern(InvokePattern.Pattern);
        var hasFile = chat.GetType().GetMethod("HasDraftFile", Instance);
        for (int leg = 1; leg <= 2; leg++)
        {
            attach.Invoke(chat, new[] { job });
            job = load.Invoke(null, new[] { id }); // resume from the on-disk checkpoint
            if (leg == 1)
            {
                toggleFile.Invoke();
                Assert(SpinWait.SpinUntil(() => (bool)hasFile.Invoke(chat, new object[] { "其他附件.txt" }), 3000), "Missing unrelated fixture chip.");
                try
                {
                    chat.GetType().GetMethod("VerifyAttachment", Instance).Invoke(chat, new[] { job });
                    throw new Exception("An unrelated attachment passed the pre-send check.");
                }
                catch (TargetInvocationException error)
                {
                    if (!(error.GetBaseException() is InvalidOperationException)) throw;
                }
                toggleFile.Invoke();
                Assert(SpinWait.SpinUntil(() => !(bool)hasFile.Invoke(chat, new object[] { "其他附件.txt" }), 3000), "Fixture chip still present.");
            }
            attach.Invoke(chat, new[] { job });
            Assert(SpinWait.SpinUntil(() => Title(window).EndsWith("|" + leg + "/" + (leg-1) + "/0"), 3000), "Repeated drop, wrong bytes, or Add menu opened: " + Title(window));
            advance.Invoke(null, new[] { job, chat });
            job = load.Invoke(null, new[] { id });
            Assert((string)type.GetField("Stage").GetValue(job) == (leg == 1 ? "await-result" : "done"), "Missing delivery receipt on leg " + leg);
            Assert(SpinWait.SpinUntil(() => Title(window).EndsWith("|" + leg + "/" + leg + "/0"), 3000), "Wrong send count: " + Title(window));
            if (leg == 1)
            {
                using (var bytes = new MemoryStream(Convert.FromBase64String(RevisedPng)))
                    job = assembly.GetType("AISwitchDesk.RoundTrips").GetMethod("Accept", Static)
                        .Invoke(null, new object[] { id, "修改稿 B.png", bytes });
                Assert((string)type.GetField("Stage").GetValue(job) == "return-ready", "Return payload not checkpointed.");
            }
        }
    }

    [MTAThread]
    static void Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string folder = Path.Combine(Path.GetTempPath(), "AI-GO-chromium-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string title = "AI Switch Attachment Fixture " + Process.GetCurrentProcess().Id;
        string html = Path.Combine(folder, "fixture.html");
        File.WriteAllText(html, "<!doctype html><meta charset=utf-8><title>" + title + "</title>" + @"
<style>body{padding:20px}textarea{display:block;width:85%;height:40px}</style>
<section role=group>
  <textarea id='prompt-textarea' class='ProseMirror' aria-label='随心输入'></textarea>
  <button aria-label='添加文件等内容' onclick='menus++;update()'>+</button>
  <button id='send-button' aria-label='发送' onclick='sendDraft()'>发送</button>
  <span id='chip' role='status'></span><span id='other' role='status'></span>
</section><button id='other-file' onclick='toggleOther()'>切换测试附件</button><div id='history'></div>
<script>
const base=document.title, editor=document.querySelector('textarea'), chip=document.querySelector('#chip');
let drops=0, sends=0, menus=0;
function update(){document.title=base+'|'+drops+'/'+sends+'/'+menus}
function toggleOther(){const other=document.querySelector('#other'); other.textContent=other.textContent?'':'其他附件.txt'}
function sendDraft(){
  if(!chip.textContent || !editor.value) return;
  const posted=document.createElement('p'); posted.textContent=editor.value;
  document.querySelector('#history').appendChild(posted);
  editor.value=''; chip.textContent=''; sends++; update();
}
for(const event of ['dragenter','dragover']) document.addEventListener(event,e=>{e.preventDefault();e.dataTransfer.dropEffect='copy'});
document.addEventListener('drop',async e=>{
  e.preventDefault(); const f=e.dataTransfer.files[0];
  if(!e.isTrusted || e.dataTransfer.files.length!==1) return;
  drops++; update();
  const valid=f.name==='原件 A.txt' ? await f.text()==='原始内容 · UTF-8\n' :
        f.name==='修改稿 B.png' && btoa(String.fromCharCode(...new Uint8Array(await f.arrayBuffer())))==='" + RevisedPng + @"';
  if(valid) chip.textContent=f.name;
});
update();
</script>",new UTF8Encoding(false));
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe");
        if (!File.Exists(executable))
            executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        var launch = new ProcessStartInfo(executable,
            "--no-first-run --no-default-browser-check --disable-background-networking --disable-sync --window-size=520,360 --window-position=40,60 --force-renderer-accessibility --user-data-dir=\"" +
            Path.Combine(folder,"profile") + "\" --app=\"" + new Uri(html).AbsoluteUri + "\"")
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
                CheckHandoff(assembly, chat, folder, window);
                Assert(guards > 2, "Draft identity checks were not exercised.");
                Console.WriteLine("PASS: two byte-verified native drops, two posted receipts, no duplicate or picker, existing draft preserved" +
                    (behind != null ? " behind another window" : ""));
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
                var fixture = new DirectoryInfo(Path.GetFullPath(folder));
                if (browser.WaitForExit(4000) && fixture.Parent != null &&
                    string.Equals(fixture.Parent.FullName.TrimEnd('\\'), Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                    fixture.Name.StartsWith("AI-GO-chromium-", StringComparison.Ordinal))
                {
                    try { fixture.Delete(true); }
                    catch (IOException) { Console.WriteLine("Fixture files still open: " + fixture.FullName); }
                    catch (UnauthorizedAccessException) { Console.WriteLine("Fixture files still open: " + fixture.FullName); }
                }
            }
        }
    }
}
