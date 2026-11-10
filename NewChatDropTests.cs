using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// This is a self-contained WebView, not either AI client. All responses are
// generated in-process; no request can leave the fixture's private profile.
internal static class NewChatDropTests
{
    private const BindingFlags Instance=BindingFlags.NonPublic|BindingFlags.Instance;
    private const BindingFlags Static=BindingFlags.NonPublic|BindingFlags.Static;

    private sealed class FixtureWindow : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args[0]=="--host" || args[0]=="--queue-host") return Host(args[1],args[2],args[0]=="--queue-host");
        string app=args[1];
        string folder=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"new-chat-"+Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(folder);
        string first=Path.Combine(folder,"留在原草稿.001"),second=Path.Combine(folder,"放入新聊天.txt");
        File.WriteAllText(first,"old-file",new UTF8Encoding(false));
        File.WriteAllText(second,"new-file",new UTF8Encoding(false));
        var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--host "+app+" \""+folder+"\""){
            UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true
        };
        using(var host=Process.Start(start))
        {
            try
            {
                var ready=host.StandardOutput.ReadLineAsync();
                if(!ready.Wait(22000)) throw new Exception("fixture startup timed out");
                long hwnd;
                if(!long.TryParse(ready.Result,out hwnd)) throw new Exception("fixture did not open: "+ready.Result);
                var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
                var store=assembly.GetType("AISwitchDesk.RelayStore");
                store.GetField("Root",Static).SetValue(null,folder);
                store.GetField("Jobs",Static).SetValue(null,Path.Combine(folder,"handoffs"));
                var ui=assembly.GetType("AISwitchDesk.ChatUi");
                var drop=assembly.GetType("AISwitchDesk.DraftDrop").GetMethod("Attach",Static);
                object chat=Activator.CreateInstance(ui,Instance,null,new object[]{new IntPtr(hwnd),new Func<bool>(()=>true)},null);
                ui.GetMethod("WaitForEditor",Instance).Invoke(chat,null);
                var editor=ui.GetMethod("Editor",Instance);
                var hasAttachments=ui.GetMethod("HasAttachments",Instance);
                var area=(AutomationElement)ui.GetMethod("ComposerArea",Instance).Invoke(chat,new object[]{editor.Invoke(chat,null)});
                if (!area.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text))
                    .Cast<AutomationElement>().Any(n=>n.Current.Name=="Sonnet 5.5" && !n.Current.IsOffscreen))
                    throw new Exception("fixture did not expose the visible text of the model selector");
                if ((bool)hasAttachments.Invoke(chat,new object[]{editor.Invoke(chat,null),false}))
                    throw new Exception("model selector Sonnet 5.5 was mistaken for a draft attachment");
                if ((bool)ui.GetMethod("HasFile",Instance).Invoke(chat,new object[]{"Sonnet 5.5"}) ||
                    (bool)ui.GetMethod("HasDraftFile",Instance).Invoke(chat,new object[]{"Sonnet 5.5"}))
                    throw new Exception("a matching toolbar label was used to confirm a file");
                CheckAttached(drop,chat,app,first);
                store.GetField("Root",Static).SetValue(null,Path.Combine(folder,"no-draft-cache"));
                if (!(bool)hasAttachments.Invoke(chat,new object[]{editor.Invoke(chat,null),false}))
                    throw new Exception("an attached file with a numeric extension was missed");
                store.GetField("Root",Static).SetValue(null,folder);
                ui.GetMethod("OpenNewDraft",Instance).Invoke(chat,new object[]{app});
                CheckAttached(drop,chat,app,second);
                var done=host.StandardOutput.ReadLineAsync();
                if(!done.Wait(5000)||done.Result!="PASS") throw new Exception("previous draft or new draft contents mismatched");
                Console.WriteLine("PASS "+app+" fixture: existing unsent text and first attachment retained; native new-chat command, separate empty composer and second real file verified");
                return 0;
            }
            catch(Exception e){Console.WriteLine("FAIL "+app+" "+e.GetBaseException());return 1;}
            finally
            {
                if(!host.HasExited) {host.StandardInput.WriteLine("close");if(!host.WaitForExit(3400))host.Kill();}
            }
        }
    }

    private static void CheckAttached(MethodInfo attach,object chat,string app,string path)
    {
        var receipts=(Array)attach.Invoke(null,new object[]{chat,app,new[]{path},null,null,null,null});
        var single=receipts.GetValue(0);
        if(!(bool)single.GetType().GetField("Added",Instance).GetValue(single))
            throw new Exception("attach "+path+": "+single.GetType().GetField("Error",Instance).GetValue(single));
    }

    private static int Host(string app,string folder,bool queue)
    {
        string origin=app=="Claude"?"https://claude.ai":"https://chatgpt.com";
        string initial=app=="Claude"?"/chat/11111111-1111-4111-8111-111111111111":queue?"/c/22222222-2222-4222-8222-222222222222":"/";
        string other="/chat/33333333-3333-4333-8333-333333333333";
        string newPath=app=="Claude"?"/new":"/";
        string html="<!doctype html><meta charset=utf-8><title>Local new-chat fixture</title><style>body{font:16px sans-serif}.ProseMirror{height:55px;border:1px solid}.chip{display:inline-block}</style>" +
            "<button onclick='newChat()'>新聊天</button><section role=group aria-label='草稿输入区'><div id=chips></div><div class=ProseMirror role=textbox contenteditable aria-label='和 Codex 一起工作' id=prompt-textarea>保留原有文字</div><button aria-label='添加文件等内容'>+</button>" +
            "<div tabindex='0' role='button' aria-haspopup='menu' aria-label='模型：Sonnet 5.5 高' style='display:inline-flex;gap:4px'><span tabindex='-1' role='group'><span>Sonnet 5.5</span></span> <span>高</span></div></section>" +
            "<script>let old=null;const ed=()=>document.querySelector('.ProseMirror');function newChat(){old={text:ed().textContent,files:[...chips.children].map(x=>x.textContent)};history.pushState(null,'','"+newPath+"');chips.replaceChildren();let next=ed().cloneNode(false);next.setAttribute('aria-label','随心输入');ed().replaceWith(next)}" +
            "for(const k of ['dragenter','dragover'])document.addEventListener(k,e=>{e.preventDefault();e.dataTransfer.dropEffect='copy'});" +
            "function received(name,content){let chip=document.createElement('span');chip.className='chip';chip.textContent=name;chips.append(chip);if(old)chrome.webview.postMessage(old.text==='保留原有文字'&&old.files.join('')==='留在原草稿.001'&&ed().textContent===''&&chips.children.length===1&&name==='放入新聊天.txt'&&content==='new-file'?'PASS':'WRONG')}" +
            "document.addEventListener('drop',async e=>{e.preventDefault();const f=e.dataTransfer.files[0];if(e.isTrusted&&f)received(f.name,await f.text())});chrome.webview.addEventListener('message',e=>received(...e.data.split('|')));chrome.webview.postMessage('ready')</script>";
        var form=new FixtureWindow{Text="Local new chat test",ClientSize=new Size(420,230),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(30,70),AllowDrop=true};
        if(queue)
        {
            form.ShowInTaskbar=true;
            form.Location = new Point(app=="Claude"?495:35,95);
            string extra = "const openFresh=newChat;newChat=function(){chrome.webview.postMessage('new-draft');openFresh()};"+
                "let attempts=JSON.parse(localStorage.getItem('attempts')||'{}');"+
                "function chip(n){let c=document.createElement('span');c.className='chip';c.textContent=n;chips.append(c)}"+
                "let prev=JSON.parse(localStorage.getItem(location.pathname)||'[]');prev.forEach(chip);"+
                "function record(n,c){attempts[n]=(attempts[n]||0)+1;localStorage.setItem('attempts',JSON.stringify(attempts));chrome.webview.postMessage('file|'+n+'|'+c);"+
                (app=="Claude"?"if((n==='partial.txt'||n==='late.txt')&&attempts[n]===1)return;":"")+
                "chip(n);prev.push(n);localStorage.setItem(location.pathname,JSON.stringify(prev))}";
            int start=html.IndexOf("function received(",StringComparison.Ordinal);
            html=html.Substring(0,start)+extra+
                "document.addEventListener('drop',async e=>{e.preventDefault();if(e.isTrusted)for(const f of e.dataTransfer.files)record(f.name,await f.text())});"+
                "chrome.webview.addEventListener('message',e=>record(...e.data.split('|')));chrome.webview.postMessage('ready')</script>";
        }
        var view=new WebView2{Dock=DockStyle.Fill};
        form.DragEnter+=delegate(object sender,DragEventArgs e) { e.Effect=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None; };
        form.DragOver+=delegate(object sender,DragEventArgs e) { e.Effect=DragDropEffects.Copy; };
        form.DragDrop+=delegate(object sender,DragEventArgs e) {
            var files=(string[])e.Data.GetData(DataFormats.FileDrop);
            foreach(string path in files) view.CoreWebView2.PostWebMessageAsString(Path.GetFileName(path)+"|"+File.ReadAllText(path));
            e.Effect=DragDropEffects.Copy;
        };
        form.Controls.Add(view);
        form.Shown+=async delegate
        {
            try
            {
                var options=new CoreWebView2EnvironmentOptions("--force-renderer-accessibility --disable-background-networking");
                var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(folder,"profile"),options);
                await view.EnsureCoreWebView2Async(env);
                view.AllowExternalDrop=true;
                view.CoreWebView2.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                view.CoreWebView2.WebResourceRequested+=delegate(object sender,CoreWebView2WebResourceRequestedEventArgs e)
                {
                    bool allowed=e.ResourceContext==CoreWebView2WebResourceContext.Document && (e.Request.Uri==origin+initial || (queue && e.Request.Uri==origin+other));
                    var bytes=allowed?Encoding.UTF8.GetBytes(html):new byte[0];
                    e.Response=env.CreateWebResourceResponse(new MemoryStream(bytes),allowed?200:404,allowed?"OK":"Not found","Content-Type: text/html; charset=utf-8");
                };
                bool announced=false;
                view.CoreWebView2.WebMessageReceived+=delegate(object sender,CoreWebView2WebMessageReceivedEventArgs e)
                {
                    string text=e.TryGetWebMessageAsString();
                    if(queue && text.StartsWith("file|")) File.AppendAllText(Path.Combine(folder,"observed.txt"),text+Environment.NewLine);
                    else if(queue && text=="new-draft") File.AppendAllText(Path.Combine(folder,"new.txt"),"new"+Environment.NewLine);
                    else if(text=="ready") { Console.WriteLine(announced?"changed":form.Handle.ToInt64().ToString()); announced=true; }
                    else Console.WriteLine(text);
                };
                view.CoreWebView2.Navigate(origin+initial);
                new Thread(delegate(){
                    string command;
                    while(queue && (command=Console.ReadLine())!=null && command!="close")
                    {
                        string action=command;
                        form.BeginInvoke(new Action(async delegate {
                            if (action=="away" || action=="back") view.CoreWebView2.Navigate(origin+(action=="away"?other:initial));
                            else { await view.CoreWebView2.ExecuteScriptAsync("chip('late.txt')"); Console.WriteLine("changed"); }
                        }));
                    }
                    if(!queue) Console.ReadLine();
                    form.BeginInvoke(new Action(form.Close));
                }){IsBackground=true}.Start();
            }
            catch(Exception error){Console.WriteLine("HOST "+error);form.Close();}
        };
        Application.Run(form);
        return 0;
    }
}
