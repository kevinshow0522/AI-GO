using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

// Exercises the actual attachment entry on a private local Chromium page.
// The test fails if the page sees an Add click, a picker or an untrusted drop.
internal static class BrowserDropTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private delegate bool Visitor(IntPtr window, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visitor visitor, IntPtr unused);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr w, StringBuilder title, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr w, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr w, out int pid);

    private static string Title(IntPtr window)
    {
        var text = new StringBuilder(300);
        GetWindowText(window,text,text.Capacity);
        return text.ToString();
    }

    [MTAThread]
    private static int Main(string[] args)
    {
        string title = "Local draft drop " + Process.GetCurrentProcess().Id;
        string folder = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"browser-drop-"+Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(folder);
        string html = Path.Combine(folder,"fixture.html");
        string[] files = {Path.Combine(folder,"方案 A.txt"),Path.Combine(folder,"平面 B.txt")};
        for (int i=0; i<files.Length; i++) File.WriteAllText(files[i],"probe-"+i,new UTF8Encoding(false));
        File.WriteAllText(html,"<!doctype html><meta charset=utf-8><title>"+title+"</title>" +
            "<style>body{font:16px sans-serif}#prompt-textarea{height:52px;border:1px solid}.chip{display:inline-block}</style>" +
            "<section role=group aria-label='聊天'><article aria-label='旧消息'><span>方案 A.txt</span></article><section role=group aria-label='输入区'><div id=chips aria-live=polite></div><div id=prompt-textarea class=ProseMirror role=textbox aria-label='和 Codex 一起工作' contenteditable>待发送原草稿</div>" +
            "<button aria-label='添加文件等内容' onclick='document.title=String(0)'>+</button></section></section>" +
            "<script>const base=document.title,ed=document.querySelector('[contenteditable]');let count=0;" +
            "for(const type of ['dragenter','dragover'])ed.addEventListener(type,e=>{e.preventDefault();e.dataTransfer.dropEffect='copy'});" +
            "ed.ondrop=async e=>{e.preventDefault();const f=[...e.dataTransfer.files]; if(!e.isTrusted||++count!==1||f.map(x=>x.name).join('|')!=='方案 A.txt|平面 B.txt'){document.title=base+' BAD-DROP';return}" +
            "const contents=await Promise.all(f.map(x=>x.text()));if(contents.join('|')!=='probe-0|probe-1'||ed.textContent!=='待发送原草稿'){document.title=base+' BAD-CONTENT';return}" +
            "for(const file of f){const c=document.createElement('span');c.className='chip';c.textContent=file.name;chips.append(c)}document.title=base+' PASS'};</script>",new UTF8Encoding(false));
        string chrome=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Google","Chrome","Application","chrome.exe");
        if(!File.Exists(chrome)) chrome=@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        string browserArgs="--no-first-run --no-default-browser-check --disable-background-networking --disable-component-update --disable-sync --force-renderer-accessibility --window-size=520,360 --window-position=40,60 --user-data-dir=\""+Path.Combine(folder,"profile")+"\" --app=\""+new Uri(html).AbsoluteUri+"\"";
        IntPtr window = IntPtr.Zero, initial=GetForegroundWindow();
        Process cover=null;
        using (var browser=Process.Start(new ProcessStartInfo(chrome,browserArgs) { UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden }))
        {
            try
            {
                var clock=Stopwatch.StartNew();
                while (window == IntPtr.Zero && clock.ElapsedMilliseconds<16000)
                {
                    EnumWindows(delegate(IntPtr w,IntPtr unused){ if(IsWindowVisible(w) && GetWindow(w,4)==IntPtr.Zero && Title(w).StartsWith(title)){window=w;return false;}return true;},IntPtr.Zero);
                    Thread.Sleep(90);
                }
                if (window == IntPtr.Zero) throw new Exception("fixture did not start");
                var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
                var type=assembly.GetType("AISwitchDesk.ChatUi");
                var chat=Activator.CreateInstance(type,Instance,null,new object[]{window,new Func<bool>(()=>true)},null);
                type.GetMethod("WaitForEditor",Instance).Invoke(chat,null);
                var editor=(AutomationElement)type.GetMethod("Editor",Instance).Invoke(chat,null);
                if ((bool)type.GetMethod("HasDraftFile",Instance).Invoke(chat,new object[]{Path.GetFileName(files[0])})) throw new Exception("filename in past message was counted as a draft attachment");
                var box=editor.Current.BoundingRectangle;
                Point point=new Point((int)(box.X+box.Width/2),(int)(box.Y+box.Height/2));
                var start=new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"LegacyActionTests.exe"),"--fixture"){
                    UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true
                };
                cover=Process.Start(start);
                IntPtr coverHandle=new IntPtr(long.Parse(cover.StandardOutput.ReadLine().Split(',')[0]));
                SetWindowPos(coverHandle,IntPtr.Zero,point.X-50,point.Y-40,140,110,0x40);
                SetForegroundWindow(coverHandle);
                Thread.Sleep(160);
                var foreground=GetForegroundWindow();
                var hit=WindowFromPoint(point);
                if (foreground==window || hit==window || IsChild(window,hit)) throw new Exception("test target must be covered and in background");
                uint clipboard=GetClipboardSequenceNumber();
                type.GetMethod("DropDraftFiles",Instance).Invoke(chat,new object[]{files,null});
                clock.Restart();
                while (!Title(window).Contains(" PASS") && clock.ElapsedMilliseconds<5000) Thread.Sleep(75);
                if (!Title(window).Contains(" PASS")) throw new Exception("page result: "+Title(window));
                clock.Restart();
                bool chips=false;
                while (!chips && clock.ElapsedMilliseconds<7000)
                {
                    chips=files.All(f=>(bool)type.GetMethod("HasDraftFile",Instance).Invoke(chat,new object[]{Path.GetFileName(f)}));
                    if (!chips) Thread.Sleep(100);
                }
                if (!chips) throw new Exception("composer chips not confirmed");
                if (GetForegroundWindow()!=foreground || GetClipboardSequenceNumber()!=clipboard) throw new Exception("foreground or clipboard changed during transfer");
                Console.WriteLine("PASS covered background Chromium composer received a single trusted drop, two Unicode filenames and exact contents; both chips visible, text, foreground and clipboard preserved");
                return 0;
            }
            catch(Exception ex)
            {
                Console.WriteLine("FAIL "+ex.GetBaseException());
                if(window!=IntPtr.Zero)
                {
                    Console.WriteLine("fixture HWND "+window+" "+Title(window));
                    var nodes=AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants,Condition.TrueCondition);
                    foreach(AutomationElement node in nodes.Cast<AutomationElement>().Where(e=>!e.Current.IsOffscreen).Take(34)) Console.WriteLine(node.Current.ControlType.ProgrammaticName+" "+node.Current.Name);
                }
                return 1;
            }
            finally
            {
                if (cover!=null) {cover.StandardInput.WriteLine("close"); if(!cover.WaitForExit(2200))cover.Kill();cover.Dispose();}
                if (window!=IntPtr.Zero) PostMessage(window,0x10,IntPtr.Zero,IntPtr.Zero);
                if (GetForegroundWindow()==window && initial!=IntPtr.Zero) SetForegroundWindow(initial);
            }
        }
    }
}
