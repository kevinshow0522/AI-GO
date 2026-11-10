using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Media;
using W = System.Windows;
using C = System.Windows.Controls;

internal static class DualUploadTests
{
    const BindingFlags I = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags S = BindingFlags.NonPublic | BindingFlags.Static;
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static object Field(object o, string n) { return o.GetType().GetField(n,I).GetValue(o); }
    static object Prop(object o, string n) { return o.GetType().GetProperty(n,I|BindingFlags.Public).GetValue(o,null); }
    static object Call(object o, string n, params object[] a) { return o.GetType().GetMethod(n,I).Invoke(o,a); }

    sealed class Host : IDisposable
    {
        internal Process Process;
        internal IntPtr Window;
        internal string Folder;
        internal Host(string app,string folder)
        {
            Folder=Path.Combine(folder,app); Directory.CreateDirectory(Folder);
            string exe=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"NewChatDropTests.exe");
            Process=System.Diagnostics.Process.Start(new ProcessStartInfo(exe,"--queue-host "+app+" \""+Folder+"\"") {
                UseShellExecute=false,CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardInput=true,RedirectStandardOutput=true
            });
            var ready=Process.StandardOutput.ReadLineAsync(); long h;
            if(!ready.Wait(25000) || !long.TryParse(ready.Result,out h))
            { Dispose(); throw new Exception("local WebView did not open for "+app); }
            Window=new IntPtr(h);
        }
        internal void Command(string command)
        {
            Process.StandardInput.WriteLine(command); var answer=Process.StandardOutput.ReadLineAsync();
            Check(answer.Wait(6000) && answer.Result=="changed","fixture navigation");
        }
        internal string[] Records() { return File.ReadAllLines(Path.Combine(Folder,"observed.txt")); }
        internal void WaitForPage(Assembly app, string suffix)
        {
            var chat=Activator.CreateInstance(app.GetType("AISwitchDesk.ChatUi"),I,null,new object[]{Window,new Func<bool>(()=>true)},null);
            Pump(()=>((string)Call(chat,"PageKey")).EndsWith(suffix),null,8000,"WebView did not expose the new page "+suffix);
        }
        public void Dispose()
        {
            if(!Process.HasExited) { Process.StandardInput.WriteLine("close"); if(!Process.WaitForExit(3500)) Process.Kill(); }
            Process.Dispose();
        }
    }

    static IEnumerable<W.DependencyObject> Tree(W.DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            yield return child;
            foreach(var desc in Tree(child)) yield return desc;
        }
    }

    static void Drag(Button button, params string[] files)
    {
        var data=new DataObject(); data.SetData(DataFormats.FileDrop,files);
        var e=new DragEventArgs(data,1,0,0,DragDropEffects.Copy,DragDropEffects.None);
        Call(button,"OnDragEnter",e); Call(button,"OnDragDrop",e);
        Check(e.Effect==DragDropEffects.Copy,"drag was rejected at "+button.AccessibleName);
    }

    static void Pump(Func<bool> until, Action observe, int milliseconds, string message)
    {
        var clock=Stopwatch.StartNew();
        do { Application.DoEvents(); if(observe!=null) observe(); if(until())return; Thread.Sleep(12); } while(clock.ElapsedMilliseconds<milliseconds);
        throw new TimeoutException(message);
    }

    static object[] Entries(object q) { return ((IEnumerable)Field(q,"Entries")).Cast<object>().ToArray(); }
    static string Summary(object q) { return string.Join(" / ", Entries(q).Select(r=>Prop(r,"Name")+"="+Prop(r,"Status"))); }
    static void Retry(Form rail,object queue,string name)
    {
        Call(rail,"OpenFileQueue");
        var window=(W.Window)Field(Field(rail,"settings"),"window");
        window.UpdateLayout();
        var list=(C.ItemsControl)window.FindName("UploadList");
        var retry=Tree(list).OfType<C.Button>().Single(b=>b.Visibility==W.Visibility.Visible && b.Tag!=null &&
            (string)Prop(b.Tag,"Name")==name && ((string)Prop(b.Tag,"Status")).StartsWith("Claude"));
        retry.RaiseEvent(new W.RoutedEventArgs(C.Primitives.ButtonBase.ClickEvent,retry));
        Pump(()=>Prop(queue,"Active")==null,null,30000,"retry: "+name);
    }

    internal static void Run(Assembly app)
    {
        Application.EnableVisualStyles();
        string dir=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"dual-upload-"+Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(dir);
        var store=app.GetType("AISwitchDesk.RelayStore");
        store.GetField("Root",S).SetValue(null,dir);
        store.GetField("Jobs",S).SetValue(null,Path.Combine(dir,"handoffs"));
        string[] names={"first.txt","partial.txt","late.txt","gpt-only.txt","claude-only.txt"};
        string[] paths=names.Select(n=>Path.Combine(dir,n)).ToArray();
        foreach(string file in paths)File.WriteAllText(file,"bytes-"+Path.GetFileName(file));
        using(var gpt=new Host("ChatGPT",dir)) using(var claude=new Host("Claude",dir))
        using(var rail=(Form)Activator.CreateInstance(app.GetType("AISwitchDesk.DeskForm"),I,null,new object[]{
            new Func<int,IntPtr>(i=>(i==0?gpt:claude).Window),
            new Func<int,System.Threading.Tasks.Task<IntPtr>>(i=>System.Threading.Tasks.Task.FromResult((i==0?gpt:claude).Window))
        },null))
        {
            rail.Show(); ((System.Windows.Forms.Timer)Field(rail,"poll")).Stop();
            var buttons=rail.Controls.OfType<Button>().OrderBy(b=>b.TabIndex).ToArray();
            object queue=Field(rail,"Uploads");
            Drag(buttons[2],paths.Take(3).ToArray());
            Drag(buttons[0],paths[3]); Drag(buttons[1],paths[4]);
            bool working=false,success=false,waiting=false;
            Pump(()=>Entries(queue).Length==8 && Prop(queue,"Active")==null,delegate {
                working |= Field(rail,"trailFrom").Equals(2) && buttons.Take(2).Any(b=>Field(b,"Feedback").ToString()=="Working");
                success |= buttons.Take(2).Any(b=>Field(b,"Feedback").ToString()=="Success");
                waiting |= Entries(queue).Any(r=>Field(r,"State").ToString()=="Waiting");
            },65000,"initial queue did not drain: "+dir);
            Check(working && waiting && success,"progress, queue or success indicator missing: "+Summary(queue));
            Check(Entries(queue).Count(r=>(bool)Prop(r,"CanRetry"))==2,"unexpected individual receipts: "+Summary(queue));
            Check(gpt.Records().SequenceEqual(names.Take(4).Select(n=>"file|"+n+"|bytes-"+n)),"GPT bytes or FIFO sequence");
            Check(claude.Records().SequenceEqual(names.Take(3).Concat(names.Skip(4)).Select(n=>"file|"+n+"|bytes-"+n)),"Claude bytes or FIFO sequence");
            Check(buttons[2].AccessibleName=="并排对照","dropping files toggled the layout");

            var restored=Activator.CreateInstance(queue.GetType(),I,null,new object[]{null,dir,new WindowsFormsSynchronizationContext()},null);
            Check(Entries(restored).Length==2 && Entries(restored).All(r=>(bool)Prop(r,"CanRetry") && Field(Field(r,"Destination"),"Address")!=null),"retry journal lost target or restored a completed item");
            // Use the visible queue shortcut, then its real WPF retry buttons.
            var ctx=Message.Create(buttons[2].Handle,0x7b,buttons[2].Handle,new IntPtr(-1));
            Call(buttons[2],"WndProc",ctx);
            var settings=(W.Window)Field(Field(rail,"settings"),"window");
            Check(((C.RadioButton)settings.FindName("QueueTab")).IsChecked==true,"right click did not open queue");
            claude.Command("away"); claude.WaitForPage(app,"333333333333"); Retry(rail,queue,"partial.txt");
            Check(claude.Records().Length==4 && Entries(queue).Any(r=>((string)Prop(r,"Status")).Contains("聊天已切换")),"retry landed in another chat");
            claude.Command("back"); claude.WaitForPage(app,"111111111111"); Retry(rail,queue,"partial.txt");
            claude.Command("late"); Retry(rail,queue,"late.txt");
            Check(Entries(queue).All(r=>Field(r,"State").ToString()=="Added"),Summary(queue));
            Check(gpt.Records().Length==4 && claude.Records().Length==5 && claude.Records().Last()=="file|partial.txt|bytes-partial.txt",
                "a successful file, other destination or late receipt was uploaded again");
            string delayed=Path.Combine(dir,"new-only.txt");
            Call(rail,"QueueFiles",1,new[]{delayed},true);
            Pump(()=>Prop(queue,"Active")==null,null,25000,"new-chat drop");
            Check(Entries(queue).Any(r=>(string)Prop(r,"Name")=="new-only.txt" && (bool)Prop(r,"CanRetry")),"missing file in new chat");
            File.WriteAllText(delayed,"new-file");
            Retry(rail,queue,"new-only.txt");
            Check(claude.Records().Last()=="file|new-only.txt|new-file" && File.ReadAllLines(Path.Combine(claude.Folder,"new.txt")).Length==1,
                "retry failed or created a second new chat");
            var handoffType=app.GetType("AISwitchDesk.Handoff");
            var job=Activator.CreateInstance(handoffType);
            foreach(var pair in new Dictionary<string,object>{{"Id","animation-fixture"},{"To","Claude"},{"From","ChatGPT"},{"RoundTrip",true},{"Stage","ready"},{"Updated",DateTime.UtcNow.Ticks}})
                handoffType.GetField(pair.Key).SetValue(job,pair.Value);
            Array rows=Array.CreateInstance(handoffType,1);rows.SetValue(job,0);
            Action observe=()=>Call(rail,"ObserveHandoffs",(object)rows);
            observe(); Check(Field(rail,"trailFrom").Equals(0)&&Field(rail,"trailTo").Equals(1),"outgoing direction");
            handoffType.GetField("Stage").SetValue(job,"await-result");observe();
            Check(Field(buttons[1],"Feedback").ToString()=="Success"&&Field(rail,"trailTo").Equals(-1),"outbound receipt");
            handoffType.GetField("Stage").SetValue(job,"return-confirm");observe();
            Check(Field(rail,"trailFrom").Equals(1)&&Field(rail,"trailTo").Equals(0),"return direction");
            handoffType.GetField("Stage").SetValue(job,"done");observe();
            Check(Field(buttons[0],"Feedback").ToString()=="Success"&&Field(rail,"trailTo").Equals(-1),"return receipt");
        }
    }
}
