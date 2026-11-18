using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using System.Xml;

class Verify
{
    static int checks;
    static void Check(bool value, string name) { if (!value) { Console.WriteLine("FAIL: " + name); Environment.Exit(1); } checks++; }
    static object Call(MethodInfo method, params object[] values) { return method.Invoke(null, values); }
    static string Hash(Stream stream) { using (stream) using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(stream)); }
    static string ResourceText(Assembly app, string name)
    {
        using (var reader = new StreamReader(app.GetManifestResourceStream(name)))
            return reader.ReadToEnd().Replace("\r\n","\n");
    }
    static void CheckWindowControls(Assembly original, Assembly updated, string name)
    {
        var before = new XmlDocument();
        var after = new XmlDocument();
        before.LoadXml(ResourceText(original,name));
        after.LoadXml(ResourceText(updated,name));
        var controls = after.SelectNodes("//*[@Name]").Cast<XmlElement>().ToArray();
        foreach (XmlElement control in before.SelectNodes("//*[@Name]"))
            Check(controls.Any(n=>n.LocalName==control.LocalName && n.GetAttribute("Name")==control.GetAttribute("Name")),
                name+" keeps "+control.GetAttribute("Name"));
    }
    // The folded-rail mark is drawn as a tinted shape, so only its transparency matters:
    // a solid half, a lighter half, and empty corners.
    static void CheckDeskMark(Assembly app)
    {
        using(var stream=app.GetManifestResourceStream("desk-mark.png"))
        using(var mark=new System.Drawing.Bitmap(stream))
        {
            int side=mark.Width, solid=0, light=0;
            Check(side==mark.Height && side>=96,"full-size square folded mark");
            for(int y=0;y<side;y++)
                for(int x=0;x<side;x++)
                {
                    int a=mark.GetPixel(x,y).A;
                    if(a>230) solid++;
                    else if(a>70 && a<190) light++;
                }
            Check(mark.GetPixel(0,0).A==0 && mark.GetPixel(side-1,side-1).A==0,"folded mark has clear corners");
            Check(solid>side*side*.12 && light>side*side*.12,"folded mark shows two tones");
        }
    }
    static void CheckComparisonMark(Assembly app)
    {
        using(var stream=app.GetManifestResourceStream("compare-mark.png"))
        using(var mark=new System.Drawing.Bitmap(stream))
        {
            int side=mark.Width;
            Check(side==mark.Height && side>=96,"full-size square comparison artwork");
            Check(Enumerable.Range(0,side).All(x=>mark.GetPixel(x,side-1-x).A==0),"open bottom-left to top-right cut");
            int upper=0,lower=0;
            for(int y=0;y<side;y++)
                for(int x=0;x<side;x++)
                    if(mark.GetPixel(x,y).A>64)
                    {
                        if(x+y<side-1) upper++;
                        else lower++;
                    }
            Check(Math.Min(upper,lower)>side*side*.07,"visible artwork in both diagonal halves");
        }
    }
    [STAThread]
    static void Main(string[] args)
    {
        var oldApp = Assembly.LoadFile(Path.GetFullPath(args[0]));
        var newApp = Assembly.LoadFile(Path.GetFullPath(args[1]));
        Console.WriteLine("Testing " + newApp.FullName + " at " + newApp.Location);
        foreach (var name in oldApp.GetManifestResourceNames())
        {
            if (name == "Settings.xaml" || name == "ExitMenu.xaml" || name == "Ask.xaml")
            {
                CheckWindowControls(oldApp,newApp,name);
                if (name == "Settings.xaml")
                    Check(ResourceText(newApp,name).Contains("在侧栏也可并排或还原窗口"), "settings sidebar hint");
            }
            else if(name!="compare-mark.png" && name!="desk-mark.png") Check(Hash(oldApp.GetManifestResourceStream(name)) == Hash(newApp.GetManifestResourceStream(name)), "resource: " + name);
        }
        CheckComparisonMark(newApp);
        CheckDeskMark(newApp);
        // Types removed on purpose. The usage popup went when each app's allowance moved onto its own icon.
        string[] retired = { "AISwitchDesk.UsagePopup" };
        foreach (var oldType in oldApp.GetTypes().Where(t => !t.IsNested && t.Namespace == "AISwitchDesk" && !retired.Contains(t.FullName)))
            Check(newApp.GetType(oldType.FullName) != null, "type: " + oldType.FullName);
        Check(newApp.GetType("AISwitchDesk.UsagePopup") == null && !Enum.GetNames(newApp.GetType("AISwitchDesk.RailAction")).Contains("Usage"),
            "no separate usage button or popup");
        // Claude's usage page as it reads today, in order: text, and progress bars
        // with the percentage used. Three allowances, then sections that are not.
        var bars = newApp.GetType("AISwitchDesk.PlanUsage").GetMethod("ParseClaudeBars", BindingFlags.Static | BindingFlags.NonPublic);
        Check(bars != null, "Claude usage is read from the page's progress bars");
        string[] pageText = { "您的用量", "当前会话", "5:00 重置", "当前会话", "已使用 29%", "星期六21:00 重置", "本周", "已使用 26%",
            "Fable", "最多可使用您每周限额的 50%", "星期六21:00 重置", "Fable", "已使用 49%",
            "限额重置", "完全重置", "补满您的 5 小时限额和 Fable 额度。不会重置您每周限额中的全部用量。", "用量额度",
            "本周各产品的用量", "Claude Code", "Claude Code", "74%", "对话", "对话", "0%" };
        double[] pageUsed = { -1, -1, -1, 29, -1, -1, 26, -1, -1, -1, -1, 49, -1, -1, -1, -1, -1, -1, -1, 74, -1, -1, 0, -1 };
        var read = new DateTime(2026, 11, 16, 1, 57, 0);
        Func<object,string,object> field = (target, name) => target.GetType().GetField(name).GetValue(target);
        var rows = ((System.Collections.IEnumerable)field(Call(bars, pageText, pageUsed, read), "Rows")).Cast<object>().ToArray();
        Check(rows.Select(r => (string)field(r, "Window")).SequenceEqual(new[] { "当前会话", "每周", "仅 Fable" }), "Claude allowances: session, week, one model; no per-product rows");
        Check(rows.Select(r => (double)field(r, "Remaining")).SequenceEqual(new[] { 71.0, 74.0, 51.0 }), "Claude bars hold the percentage used");
        Func<DateTime,long> epoch = local => new DateTimeOffset(local).ToUnixTimeSeconds();
        Check((long)field(rows[0], "ResetEpoch") == epoch(new DateTime(2026, 11, 16, 5, 0, 0)), "a bare time resets at its next occurrence");
        Check((long)field(rows[1], "ResetEpoch") == epoch(new DateTime(2026, 11, 21, 21, 0, 0)), "a weekday and time resets on that day");
        // The usage panel in Code words them differently: the session is "5 小时限额",
        // and each weekly bar starts with "每周", whether it is the week's or one model's.
        string[] panelText = { "21:30 重置", "5 小时限额", "已使用 17%", "周六21:00 重置", "每周限额", "已使用 42%", "周六21:00 重置", "每周 Fable 限额", "已使用 82%" };
        double[] panelUsed = { -1, 17, -1, -1, 42, -1, -1, 82, -1 };
        var panel = ((System.Collections.IEnumerable)field(Call(bars, panelText, panelUsed, read), "Rows")).Cast<object>().ToArray();
        Check(panel.Select(r => (string)field(r, "Window")).SequenceEqual(new[] { "当前会话", "每周", "仅 Fable" }), "the same three allowances under their other names");
        Check(panel.Select(r => (double)field(r, "Remaining")).SequenceEqual(new[] { 83.0, 58.0, 18.0 }), "one model's weekly share is not the week's allowance");
        var windowName = newApp.GetType("AISwitchDesk.PlanUsage").GetMethod("ClaudeWindow", BindingFlags.Static | BindingFlags.NonPublic);
        Check((string)Call(windowName, "All models") == "每周" && (string)Call(windowName, "每周限额（所有模型）") == "每周" &&
            (string)Call(windowName, "Sonnet only") == "Sonnet only" && (string)Call(windowName, "5-hour limit") == "当前会话", "allowance names across layouts");
        // A conversation handed to the other app: tidy text, and no send time under a person's message.
        var transcript = newApp.GetType("AISwitchDesk.Transcript");
        var tidy = transcript.GetMethod("Clean", BindingFlags.Static | BindingFlags.NonPublic);
        var noTime = transcript.GetMethod("WithoutTime", BindingFlags.Static | BindingFlags.NonPublic);
        Check(tidy != null && noTime != null, "conversations can be handed to the other app");
        Check((string)Call(tidy, "  第一行 \r\n\r\n\r\n\r\n第二行￼\n") == "第一行\n\n第二行", "conversation text is tidied");
        Check((string)Call(noTime, "帮我看看这段代码\n13:45") == "帮我看看这段代码" && (string)Call(noTime, "帮我看看\n昨天 下午 3:20") == "帮我看看", "the send time is not part of a message");
        Check((string)Call(noTime, "会议改到\n明天 13:45 开始") == "会议改到\n明天 13:45 开始" && (string)Call(noTime, "13:45") == "13:45", "a message that mentions a time keeps it");
        var turnType = newApp.GetType("AISwitchDesk.ChatTurn");
        var turnList = (System.Collections.IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(turnType));
        foreach (var pair in new[] { new object[] { false, "你好" }, new object[] { true, "你好，有什么可以帮你？" } })
        {
            object one = Activator.CreateInstance(turnType, true);
            turnType.GetField("Assistant", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(one, pair[0]);
            turnType.GetField("Text", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(one, pair[1]);
            turnList.Add(one);
        }
        string exported = (string)Call(transcript.GetMethod("Markdown", BindingFlags.Static | BindingFlags.NonPublic), "Claude", turnList, read);
        Check(exported.StartsWith("# Claude 对话记录\n") && exported.Contains("共 2 条消息") && exported.Contains("\n## 用户\n\n你好\n") &&
            exported.Contains("\n## Claude\n\n你好，有什么可以帮你？\n"), "a conversation is written as a Markdown file with each side named");
        var resetAt = newApp.GetType("AISwitchDesk.PlanUsage").GetMethod("ResetEpoch", BindingFlags.Static | BindingFlags.NonPublic);
        Check((long)Call(resetAt, "Resets in 3 hr 12 min", read) == epoch(read.AddMinutes(192)), "relative reset time");
        Check((long)Call(resetAt, "Resets Sat 9:00 PM", read) == epoch(new DateTime(2026, 11, 21, 21, 0, 0)), "English weekday reset time");
        Check((long)Call(resetAt, "Resets Nov 21 at 9:00 PM", read) == 0, "an unread date form stays unknown rather than wrong");
        var method = newApp.GetType("AISwitchDesk.ChatUi").GetMethod("NewDraftState", BindingFlags.Static | BindingFlags.NonPublic);
        Check(method != null,"new-chat state verifier is available");
        Func<string,string,string,bool,bool,bool,bool,bool> state = (app,oldPage,page,editorChanged,oldHistory,history,draft) =>
            (bool)Call(method,app,oldPage,page,editorChanged,oldHistory,history,draft);
        var claude = "https://claude.ai/chat/11111111-1111-1111-1111-111111111111";
        var gpt = "https://chatgpt.com/c/11111111-1111-1111-1111-111111111111";
        var codex = "app://-/local/11111111-1111-1111-1111-111111111111";
        Check(state("Claude", claude, "https://claude.ai/new", true, true, false, false), "Claude new page");
        Check(!state("Claude", claude, claude, true, true, false, false), "Claude stayed in old chat");
        Check(!state("Claude", claude, "https://example.com/new", true, true, false, false), "wrong host");
        Check(!state("Claude", claude, "https://claude.ai/new", true, true, false, true), "new page has draft");
        Check(state("ChatGPT",gpt,"https://chatgpt.com/",true,true,false,false), "ChatGPT new page");
        Check(!state("ChatGPT",gpt,gpt,true,true,false,false), "old conversation editor rerendered");
        Check(!state("ChatGPT",gpt,"https://chatgpt.com/",true,true,true,false), "history still present");
        Check(!state("ChatGPT",gpt,"https://chatgpt.com/",true,true,false,true), "draft appeared during navigation");
        Check(state("ChatGPT",codex,"app://-/new",true,true,false,false), "Codex new page");
        Check(!state("ChatGPT",codex,codex,true,true,false,false), "Codex old thread still selected");
        Check(state("ChatGPT","app://-/index.html","app://-/index.html",false,true,false,false), "generic page history cleared");
        Check(state("ChatGPT","app://-/index.html","app://-/index.html",true,false,false,false), "generic page editor replaced");
        Check(!state("ChatGPT","app://-/index.html","app://-/index.html",false,false,false,false), "unconfirmed generic navigation");
        Check(!state("ChatGPT",gpt,"",true,true,false,false), "missing page");
        Check(!state("other",gpt,"https://chatgpt.com/",true,true,false,false), "unknown app");
        Check(state("ChatGPT","https://chatgpt.com/","https://chatgpt.com/",false,false,false,false), "already blank ChatGPT");
        var choose = newApp.GetType("AISwitchDesk.ChatUi").GetMethod("ChooseEditorIndex",BindingFlags.Static | BindingFlags.NonPublic);
        Check(choose != null, "two visible edit areas require composer selection; old code rejects both");
        Func<int[],bool[],bool[],int> editor = (depth,focus,prompt) => (int)Call(choose,depth,focus,prompt);
        int unknown = int.MaxValue;
        Check(editor(new[]{unknown,unknown},new[]{true,false},new[]{false,false}) == 0,"reported duplicate editors: focused composer selected");
        Check(editor(new[]{unknown,unknown},new[]{false,true},new[]{false,false}) == 1,"focus on second editor");
        Check(editor(new[]{unknown,2},new[]{true,false},new[]{false,false}) == 1,"own attachment area beats unrelated page focus");
        Check(editor(new[]{2,unknown},new[]{false,false},new[]{false,false}) == 0,"background composer without keyboard focus");
        Check(editor(new[]{2,2},new[]{false,false},new[]{false,false}) == -1,"two indistinguishable chat composers stay blocked");
        Check(editor(new[]{2,2},new[]{false,false},new[]{false,true}) == 1,"known chat textarea id");
        Check(editor(new[]{unknown},new[]{false},new[]{false}) == 0,"single editor compatibility");
        Check(editor(new int[0],new bool[0],new bool[0]) == -1,"missing editor remains blocked");
        var editorName = newApp.GetType("AISwitchDesk.ChatUi").GetMethod("IsChatEditorName",BindingFlags.Static | BindingFlags.NonPublic);
        Check(editorName != null,"main composer must be distinguished from question-card reply editor");
        Check((bool)Call(editorName,"和 Codex 一起工作"),"actual Codex composer label");
        Check(!(bool)Call(editorName,"或自行撰写回复"),"question-card input is not chat composer");
        Check((bool)Call(editorName,"Message Claude"),"Claude composer compatibility");
        var draftText = newApp.GetType("AISwitchDesk.ChatUi").GetMethod("NormalizeDraftText",BindingFlags.Static | BindingFlags.NonPublic);
        Check(draftText != null,"new-chat placeholder must not be mistaken for an unsent draft");
        Check((string)Call(draftText,"向 Codex 下达任意指令\u200b","向 Codex 下达任意指令\u200b","") == "","actual new-chat placeholder with zero-width suffix");
        Check((string)Call(draftText,"帮我检查这个文件","向 Codex 下达任意指令\u200b","") == "帮我检查这个文件","real draft stays protected");
        Check((string)Call(draftText,"和 Codex 一起工作","和 Codex 一起工作","") == "","existing-chat empty placeholder compatibility");
        Check((string)Call(draftText,"随心输入","随心输入","") == "","reported ChatGPT new-chat placeholder is not a four-character draft");
        var uploadEntry = newApp.GetType("AISwitchDesk.ChatUi").GetMethod("IsUploadEntry",BindingFlags.Static | BindingFlags.NonPublic);
        Check(uploadEntry != null,"file menu must support list-item accessibility role");
        Check((bool)Call(uploadEntry,"ControlType.ListItem","文件和文件夹"),"Codex file menu option");
        Check((bool)Call(uploadEntry,"ControlType.MenuItem","Upload files"),"standard upload menu option");
        Check(!(bool)Call(uploadEntry,"ControlType.ListItem","Figma"),"plugin menu options must not be invoked");
        var byBounds = newApp.GetType("AISwitchDesk.ChatUi").GetMethod("ChooseEditorByBounds",BindingFlags.Static | BindingFlags.NonPublic);
        Check(byBounds != null,"menu rerender must rebind the same chat composer instead of treating it as a new chat");
        Func<double[],double[],double[],double[],double[],int> locate = (x,y,w,h,b) => (int)Call(byBounds,x,y,w,h,b);
        Check(locate(new[]{10.0,500.0},new[]{200.0,200.0},new[]{400.0,200.0},new[]{40.0,40.0},new[]{10.0,200.0,400.0,40.0}) == 0,"recreated composer at same location");
        Check(locate(new[]{10.0,10.0},new[]{200.0,200.0},new[]{400.0,400.0},new[]{40.0,40.0},new[]{10.0,200.0,400.0,40.0}) == -1,"duplicate overlapping composers remain ambiguous");
        Check(locate(new[]{10.0},new[]{500.0},new[]{400.0},new[]{40.0},new[]{10.0,200.0,400.0,40.0}) == -1,"moved composer cannot be rebound by old location");
        var chatType = newApp.GetType("AISwitchDesk.ChatUi");
        var pickerGuard = chatType.GetMethod("Guard",BindingFlags.Instance | BindingFlags.NonPublic,null,new[]{typeof(bool)},null);
        Check(pickerGuard != null,"native file dialog must not requery stale disabled chat editor");
        using (var testWindow = new Form())
        {
            var chat = Activator.CreateInstance(chatType,BindingFlags.Instance | BindingFlags.NonPublic,null,
                new object[]{testWindow.Handle,new Func<bool>(() => true)},null);
            int checkedEditor = 0;
            chatType.GetField("CheckDraftTarget",BindingFlags.Instance | BindingFlags.NonPublic).SetValue(chat,new Action(()=>checkedEditor++));
            pickerGuard.Invoke(chat,new object[]{true});
            Check(checkedEditor == 0,"modal file picker uses its owner instead of disabled editor");
            pickerGuard.Invoke(chat,new object[]{false});
            Check(checkedEditor == 1,"normal attachment work still verifies chat editor");
            var freshAction = chatType.GetMethod("ActFresh", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(freshAction != null, "menu action must reacquire its target after chat verification can rerender it");
            int resolvedAfterChecks = 0;
            var automationType = Assembly.LoadFrom(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Microsoft.NET\\Framework64\\v4.0.30319\\WPF\\UIAutomationClient.dll")).GetType("System.Windows.Automation.AutomationElement");
            var resolveMethod = typeof(Verify).GetMethod("ResolveMissing", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(automationType);
            Action observed = () => resolvedAfterChecks = checkedEditor;
            var resolver = Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(automationType), observed, resolveMethod);
            try { freshAction.Invoke(chat, new object[]{resolver}); }
            catch (TargetInvocationException error) { if (!(error.InnerException is InvalidOperationException)) throw; }
            Check(resolvedAfterChecks == 2,"chat guard runs before resolving the menu option, never afterward");
        }
        Check(newApp.GetType("AISwitchDesk.DropChoice") == null, "no extra choice dialog");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string[] testPaths = { "C:\\test.pdf", "C:\\test.png" };
        var data = new DataObject(DataFormats.FileDrop, testPaths);
        var drop = new DragEventArgs(data,1,0,0,DragDropEffects.Copy,DragDropEffects.None);
        var railType = newApp.GetType("AISwitchDesk.RailButton");
        var action = Enum.Parse(newApp.GetType("AISwitchDesk.RailAction"),"ChatGPT");
        using (var rail = (Control)Activator.CreateInstance(railType,flags,null,new object[] { action, "ChatGPT" },null))
        {
            int dropped = 0;
            Action<string[]> callback = paths => { if(paths.SequenceEqual(testPaths)) dropped++; };
            railType.GetEvent("FilesDropped",flags).GetAddMethod(true).Invoke(rail,new object[] { callback });
            railType.GetMethod("OnDragEnter",flags).Invoke(rail,new object[] { drop });
            railType.GetMethod("OnDragDrop",flags).Invoke(rail,new object[] { drop });
            Check(dropped == 1, "icon directly accepts file batch");
            var text = new DataObject(DataFormats.Text,"not a file");
            var invalid = new DragEventArgs(text,1,0,0,DragDropEffects.Copy,DragDropEffects.None);
            railType.GetMethod("OnDragDrop",flags).Invoke(rail,new object[] { invalid });
            Check(dropped == 1, "icon rejects non-file drag");
        }
        var targetType = newApp.GetType("AISwitchDesk.DropTargets");
        using (var targets = (Form)Activator.CreateInstance(targetType,flags,null,new object[0],null))
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            var icon = new System.Drawing.Rectangle(area.Right-62,area.Top+170,58,48);
            targetType.GetMethod("ShowTargets",flags).Invoke(targets,new object[]{null,"Drop geometry test",icon,icon.Location});
            Application.DoEvents();
            Check(!targets.Bounds.IntersectsWith(icon),"drop popup must never cover the AI icon after window DPI scaling");
            targetType.GetMethod("HideTargets",flags).Invoke(targets,new object[0]);
            int fresh = 0, current = 0;
            Action<bool,string[]> callback = (newChat,paths) => { if(paths.SequenceEqual(testPaths)) { if(newChat) fresh++; else current++; } };
            targetType.GetEvent("FilesDropped",flags).GetAddMethod(true).Invoke(targets,new object[] { callback });
            var currentZone=targets.Controls.Find("DropIntoCurrent",true).Single();
            var freshZone=targets.Controls.Find("DropIntoNewChat",true).Single();
            typeof(Control).GetMethod("OnDragDrop",flags).Invoke(currentZone,new object[] { drop });
            typeof(Control).GetMethod("OnDragDrop",flags).Invoke(freshZone,new object[] { drop });
            Check(current == 1 && fresh == 1, "drop zones select current or new in one drag");
            var move = new DragEventArgs(data,1,0,0,DragDropEffects.Move,DragDropEffects.None);
            typeof(Control).GetMethod("OnDragDrop",flags).Invoke(freshZone,new object[] { move });
            Check(fresh == 1, "file move is not accepted as upload");
        }
        Console.WriteLine("PASS: " + checks + " resource, type, navigation, and drop-event checks");
    }
    static T ResolveMissing<T>(Action observed) where T : class { observed(); return null; }
}
