using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class CompareButtonTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    // Use a separate, non-interactive desktop so that a layout test never
    // rearranges the user's windows or takes focus away from their work.
    [DllImport("user32.dll", CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct Startup
    {
        internal int Size;
        internal IntPtr Reserved;
        internal string Desktop;
        internal IntPtr Title;
        internal uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags;
        internal ushort Show,ExtraBytes;
        internal IntPtr Extra,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Started { internal IntPtr Process,Thread; internal int Id,ThreadId; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CreateProcess(string program,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,
        bool inherit,uint flags,IntPtr environment,string directory,ref Startup startup,out Started started);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process,uint code);

    private static void Check(bool ok, string failure)
    {
        if (!ok) throw new InvalidOperationException(failure);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args[0]=="--read-theme")
        {
            var app=Assembly.LoadFile(args[1]);
            app.GetType("AISwitchDesk.RelayStore").GetField("Root",Static).SetValue(null,args[2]);
            var themes=app.GetType("AISwitchDesk.DeskTheme");
            object current=themes.GetProperty("Current",Static).GetValue(null,null);
            return (string)themes.GetField("Id",Members).GetValue(current)==args[3] ? 0 : 1;
        }
        if (args[0]=="--inside" || args[0]=="--inside-files")
        {
            try { var app=Assembly.LoadFile(args[1]); if(args[0]=="--inside-files") DualUploadTests.Run(app); else Run(app); return 0; }
            catch (Exception e) { File.WriteAllText(args[2],e.GetBaseException().ToString()); return 1; }
        }
        string name="AI-Switch-Compare-"+Process.GetCurrentProcess().Id;
        string exe=Assembly.GetExecutingAssembly().Location;
        string result=Path.Combine(Path.GetDirectoryName(exe),name+".txt");
        IntPtr desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x1ff,IntPtr.Zero);
        try
        {
            if(desktop==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"CreateDesktop");
            var startup=new Startup{Size=Marshal.SizeOf(typeof(Startup)),Desktop=name};
            bool files=args.Length>1 && args[1]=="--files";
            var command=new StringBuilder("\""+exe+"\" "+(files?"--inside-files":"--inside")+" \""+Path.GetFullPath(args[0])+"\" \""+result+"\"");
            Started child;
            if(!CreateProcess(exe,command,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,null,ref startup,out child))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if(WaitForSingleObject(child.Process,files?170000u:35000u)!=0){ TerminateProcess(child.Process,1); throw new Exception("fixture timed out"); }
                uint code;
                if(!GetExitCodeProcess(child.Process,out code) || code!=0) throw new Exception(File.Exists(result)?File.ReadAllText(result):"fixture failed");
            }
            finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
            Console.WriteLine(files?"PASS dual drafts, FIFO drops, file-level receipts and retries, feedback and recovery":"PASS split artwork and accent slash, hover and tap motion, single file sweep, compare and restore, six themes and accents, settings focus and restart");
            return 0;
        }
        catch(Exception e) { Console.WriteLine("FAIL "+e.GetBaseException()); return 1; }
        finally { if(desktop!=IntPtr.Zero) CloseDesktop(desktop); }
    }

    private static void Run(Assembly assembly)
    {
        Application.EnableVisualStyles();
        string temp=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"compare-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Type store=assembly.GetType("AISwitchDesk.RelayStore");
        store.GetField("Root",Static).SetValue(null,temp);
        store.GetField("Jobs",Static).SetValue(null,Path.Combine(temp,"handoffs"));
        Type type=assembly.GetType("AISwitchDesk.DeskForm");
        using (Form a=new Form{FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,Bounds=new Rectangle(40,70,330,240)})
        using (Form b=new Form{FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,Bounds=new Rectangle(380,100,360,260)})
        using (Form rail=(Form)Activator.CreateInstance(type,Members,null,new object[0],null))
        {
            a.Show(); b.Show(); rail.Show();
            ((Timer)type.GetField("poll",Members).GetValue(rail)).Stop();
            Rectangle originalA=a.Bounds, originalB=b.Bounds;
            var clients=(Array)type.GetField("clients",Members).GetValue(rail);
            int i=0;
            foreach (var form in new[]{a,b})
            {
                object client=clients.GetValue(i++);
                client.GetType().GetMethod("Remember",Members).Invoke(client,new object[]{form.Handle});
                Check((bool)client.GetType().GetMethod("IsRemembered",Members).Invoke(client,new object[]{form.Handle}),"test window not recognized");
            }
            var buttons=rail.Controls.OfType<Button>().OrderBy(button=>button.TabIndex).ToArray();
            Check(buttons.Select(button=>button.AccessibleName).SequenceEqual(new[]{"ChatGPT","Claude","并排对照","套餐用量","设置","收起侧栏"}),"button order or accessibility labels");
            Check(buttons.Take(3).All(button=>button.AllowDrop) && buttons.Skip(3).All(button=>!button.AllowDrop),"app and compare file targets");
            var compare=buttons[2];
            foreach (int zoom in new[]{80,84,100,110,160})
            {
                type.GetMethod("SetZoom",Members).Invoke(rail,new object[]{zoom});
                Check(buttons.All(button=>button.Visible&&button.Enabled&&rail.ClientRectangle.Contains(button.Bounds)),"clipped or disabled button at "+zoom);
                for(i=1;i<buttons.Length;i++) Check(buttons[i-1].Bottom<=buttons[i].Top,"buttons overlap at "+zoom);
                CheckComparisonArtwork(compare,temp,"compare-"+zoom);
                if(zoom==84)
                {
                    var data=new DataObject(DataFormats.FileDrop,new[]{Path.Combine(temp,"preview.txt")});
                    var drag=new DragEventArgs(data,1,0,0,DragDropEffects.Copy,DragDropEffects.None);
                    compare.GetType().GetMethod("OnDragEnter",Members).Invoke(compare,new object[]{drag});
                    System.Threading.Thread.Sleep(185);
                    Application.DoEvents();
                    Check(drag.Effect==DragDropEffects.Copy,"comparison target rejected file drag");
                    CheckComparisonArtwork(compare,temp,"compare-84-drag");
                    compare.GetType().GetMethod("OnDragLeave",Members).Invoke(compare,new object[]{EventArgs.Empty});
                    System.Threading.Thread.Sleep(185);
                    Application.DoEvents();
                }
            }
            CheckComparisonMotion(assembly,b,compare,temp);
            Action clickGrip=delegate {
                var e=new MouseEventArgs(MouseButtons.Left,1,rail.ClientSize.Width/2,9,0);
                type.GetMethod("OnMouseDown",Members).Invoke(rail,new object[]{e});
                type.GetMethod("OnMouseUp",Members).Invoke(rail,new object[]{e});
            };
            clickGrip();
            Check(a.Bounds==originalA && b.Bounds==originalB,"clicking the grip arranged windows");
            compare.PerformClick();
            Rectangle work=Screen.FromHandle(rail.Handle).WorkingArea;
            int middle=work.Left+work.Width/2;
            Check(a.Bounds==Rectangle.FromLTRB(work.Left,work.Top,middle,work.Bottom) && b.Bounds==Rectangle.FromLTRB(middle,work.Top,work.Right,work.Bottom),"first click did not arrange left and right");
            Check(compare.AccessibleName=="还原窗口" && (bool)compare.GetType().GetProperty("Selected",Members).GetValue(compare,null),"restore action not shown");
            clickGrip();
            Check(compare.AccessibleName=="还原窗口","clicking the grip restored windows");
            compare.PerformClick();
            Check(a.Bounds==originalA && b.Bounds==originalB && compare.AccessibleName=="并排对照","second click did not restore both original bounds");
            buttons.Last().PerformClick();
            type.GetMethod("FinishFold",Members).Invoke(rail,null);
            Check(!compare.Visible && !compare.Enabled && buttons.Last().AccessibleName=="展开侧栏","folded rail has a live compare target");
            buttons.Last().PerformClick();
            type.GetMethod("FinishFold",Members).Invoke(rail,null);
            Check(compare.Visible && compare.Enabled,"compare button missing after expanding");
            type.GetMethod("SetZoom",Members).Invoke(rail,new object[]{100});
            CheckSelectionMotion(assembly,rail);
            CheckAppearance(assembly,rail,temp);
        }
    }

    private static void CheckComparisonArtwork(Button button,string folder,string name)
    {
        using(var image=new Bitmap(button.Width,button.Height))
        {
            button.DrawToBitmap(image,button.ClientRectangle);
            double scale=image.Width/44.0;
            if(!name.EndsWith("-drag"))
            {
                var themes=button.GetType().Assembly.GetType("AISwitchDesk.DeskTheme");
                var theme=themes.GetProperty("Current",Static).GetValue(null,null);
                var accent=(Color)themes.GetField("AccentLine",Members).GetValue(theme);
                foreach(int step in new[]{-8,-4,0,4,8})
                {
                    Color pixel=image.GetPixel((int)(image.Width/2.0+step*scale),(int)(image.Height/2.0-step*scale));
                    Check(Math.Max(Math.Abs(pixel.R-accent.R),Math.Max(Math.Abs(pixel.G-accent.G),Math.Abs(pixel.B-accent.B)))<65,
                        name+" missing the accent slash at "+step);
                }
            }
            foreach(int offset in new[]{-6,6})
            {
                double centerX=image.Width/2.0+offset*scale;
                double centerY=image.Height/2.0+offset*scale;
                int first=(int)Math.Ceiling(centerX-5*scale);
                int last=(int)Math.Floor(centerX+5*scale);
                int detailed=0;
                for(int x=first;x<=last;x++)
                {
                    int low=255, high=0;
                    for(int y=(int)Math.Ceiling(centerY-5*scale);y<=centerY+5*scale;y++)
                    {
                        int brightness=(int)(image.GetPixel(x,y).GetBrightness()*255);
                        low=Math.Min(low,brightness);
                        high=Math.Max(high,brightness);
                    }
                    if(high-low>=35) detailed++;
                }
                Check(detailed>(last-first)/2,name+" missing "+(offset<0?"upper GPT":"lower Claude")+" detail");
            }
            image.Save(Path.Combine(folder,name+".png"),System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static void CheckComparisonMotion(Assembly app,Form host,Button compare,string folder)
    {
        var type=compare.GetType();
        var action=type.GetField("Action",Members).GetValue(compare);
        var themeType=app.GetType("AISwitchDesk.DeskTheme");
        var theme=themeType.GetProperty("Current",Static).GetValue(null,null);
        var blue=themeType.GetMethod("WithAccent",Members).Invoke(theme,new[]{((Array)themeType.GetField("Accents",Static).GetValue(null)).GetValue(0)});
        bool preview=Environment.GetEnvironmentVariable("AISWITCH_COMPARE_PREVIEW")=="1";
        bool moving=(bool)app.GetType("AISwitchDesk.RailMotion").GetProperty("Enabled",Static).GetValue(null,null);
        string frames=Path.Combine(folder,"motion");
        if(preview) Directory.CreateDirectory(frames);
        using(var button=(Button)Activator.CreateInstance(type,Members,null,new[]{action,"并排对照"},null))
        {
            button.Bounds=new Rectangle(10,10,132,129);
            button.TabStop=false;
            host.Controls.Add(button);
            var clock=(Stopwatch)type.GetField("cutEffect",Members).GetValue(button);
            var timer=(Timer)type.GetField("motion",Members).GetValue(button);
            Func<float> offset=()=>(float)type.GetField("cutOffset",Members).GetValue(button);
            Action<string,object> raise=(name,e)=>type.GetMethod(name,Members).Invoke(button,new[]{e});
            int frame=0;
            Action<int> sample=count=>{
                var time=Stopwatch.StartNew();
                for(int i=0;i<count;i++)
                {
                    Application.DoEvents();
                    if(preview)
                    {
                        using(var bmp=new Bitmap(button.Width,button.Height))
                        {
                            button.DrawToBitmap(bmp,button.ClientRectangle);
                            bmp.Save(Path.Combine(frames,(frame++).ToString("D3")+".png"),System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    while(time.ElapsedMilliseconds<(i+1)*30) { Application.DoEvents(); System.Threading.Thread.Sleep(3); }
                }
                Application.DoEvents();
            };
            try
            {
                if(preview) themeType.GetMethod("Save",Members).Invoke(blue,null);
                sample(8);
                float rest=offset();
                raise("OnMouseEnter",EventArgs.Empty);
                sample(10);
                Check(moving ? offset()>rest+1 : offset()==rest,"hover did not open the two halves correctly");
                int clicks=0;
                button.Click+=delegate { clicks++; };
                IntPtr center=(IntPtr)((button.Height/2<<16) | button.Width/2);
                SendMessage(button.Handle,0x201,(IntPtr)1,center);
                sample(4);
                Check(moving ? offset()<rest-.3f : offset()==rest,"press did not bring both halves together");
                SendMessage(button.Handle,0x202,IntPtr.Zero,center);
                Check(clicks==1 && clock.IsRunning==moving,"click delivery or tap cue");
                sample(9);
                Check(!clock.IsRunning && !timer.Enabled,"tap motion did not settle");
                raise("OnMouseLeave",EventArgs.Empty);
                sample(10);
                var data=new DataObject(DataFormats.FileDrop,new[]{Path.Combine(folder,"preview.txt")});
                var drag=new DragEventArgs(data,1,0,0,DragDropEffects.Copy,DragDropEffects.None);
                raise("OnDragEnter",drag);
                Check(clock.IsRunning==moving && drag.Effect==DragDropEffects.Copy,"accepted drag did not start the sweep");
                sample(6);
                long before=clock.ElapsedMilliseconds;
                raise("OnDragOver",drag);
                Check(!moving || clock.ElapsedMilliseconds>=before,"drag-over restarted the sweep");
                sample(14);
                raise("OnDragOver",drag);
                Check(!clock.IsRunning && !timer.Enabled && (!moving || offset()>rest+1),"sweep kept running over a stationary file");
                raise("OnDragLeave",EventArgs.Empty);
                sample(10);
                Check(offset()==rest && !timer.Enabled,"drag leave did not return to rest");
                var text=new DragEventArgs(new DataObject(DataFormats.Text,"preview"),1,0,0,DragDropEffects.Copy,DragDropEffects.None);
                raise("OnDragEnter",text);
                Check(!clock.IsRunning && text.Effect==DragDropEffects.None,"text drag started a file animation");
            }
            finally { if(preview) themeType.GetMethod("Save",Members).Invoke(theme,null); }
        }
    }

    private static void CheckSelectionMotion(Assembly app,Form rail)
    {
        Type type=rail.GetType();
        var change=type.GetMethod("SetSelected",Members);
        bool moving=(bool)app.GetType("AISwitchDesk.RailMotion").GetProperty("Enabled",Static).GetValue(null,null);
        var position=type.GetField("selectionPosition",Members);
        change.Invoke(rail,new object[]{1});
        bool intermediate=false;
        var clock=Stopwatch.StartNew();
        while((float)position.GetValue(rail)!=1f && clock.ElapsedMilliseconds<1200)
        {
            Application.DoEvents();
            float p=(float)position.GetValue(rail);
            intermediate |= p>0 && p<1;
            System.Threading.Thread.Sleep(6);
        }
        Check((float)position.GetValue(rail)==1f && (!moving || intermediate),"selection marker did not travel to Claude");
        change.Invoke(rail,new object[]{0});
        clock.Restart();
        while((float)position.GetValue(rail)!=0f && clock.ElapsedMilliseconds<1200)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(6);
        }
        Check((float)position.GetValue(rail)==0f,"selection marker did not return to GPT");
    }

    private static void CheckAppearance(Assembly app, Form rail, string folder)
    {
        Type type=rail.GetType();
        type.GetMethod("ShowDropTargets",Members).Invoke(rail,new object[]{0});
        var targets=(Form)type.GetField("dropTargets",Members).GetValue(rail);
        targets.GetType().GetMethod("HideTargets",Members).Invoke(targets,null);
        type.GetMethod("OpenSettings",Members).Invoke(rail,null);
        object settings=type.GetField("settings",Members).GetValue(rail);
        var window=(System.Windows.Window)settings.GetType().GetField("window",Members).GetValue(settings);
        var footer=(System.Windows.Controls.Button)window.FindName("Compare");
        var glyph=(System.Windows.Controls.Border)window.FindName("CompareGlyph");
        var upper=(System.Windows.Controls.Border)window.FindName("CompareUpper");
        var lower=(System.Windows.Controls.Border)window.FindName("CompareLower");
        var slash=(System.Windows.Shapes.Line)window.FindName("CompareSlash");
        var mask=upper.OpacityMask as System.Windows.Media.ImageBrush;
        Check(mask!=null && mask.ImageSource!=null && mask.IsFrozen && lower.OpacityMask==mask && glyph.Width==glyph.Height &&
            upper.Clip!=null && lower.Clip!=null && slash.X2>slash.X1 && slash.Y1>slash.Y2 && slash.StrokeThickness>=2,
            "two clipped settings marks and visible ascending slash");
        foreach(string expectedAction in new[]{"还原窗口","并排对照"})
        {
            footer.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(!window.IsVisible,"settings comparison action did not run");
            type.GetMethod("OpenSettings",Members).Invoke(rail,null);
            Check(System.Windows.Automation.AutomationProperties.GetName(footer)==expectedAction && upper.OpacityMask==mask && lower.OpacityMask==mask,
                "comparison icon or action name lost on settings reopen");
        }
        CheckSettingsFocus(window,delegate { type.GetMethod("OpenSettings",Members).Invoke(rail,null); });
        var choices=(System.Windows.Controls.Primitives.UniformGrid)window.FindName("ThemeChoices");
        var radios=choices.Children.OfType<System.Windows.Controls.RadioButton>().ToArray();
        var accentPanel=(System.Windows.Controls.Primitives.UniformGrid)window.FindName("AccentChoices");
        var accents=accentPanel.Children.OfType<System.Windows.Controls.RadioButton>().ToArray();
        var reset=(System.Windows.Controls.Button)window.FindName("OriginalAccent");
        var value=(System.Windows.Controls.TextBlock)window.FindName("AccentValue");
        string[] presetIds={"codex-light","github-dark","catppuccin-latte","dracula","solarized-light","everforest-dark"};
        string[] accentIds={"blue","green","yellow","pink","orange","purple"};
        int[] accentRgb={0x3A83F7,0x53B559,0xF6C543,0xF077AF,0xEE7C37,0xA67DF2};
        Check(radios.Length==6 && radios[0].IsChecked==true && radios.Count(r=>r.IsChecked==true)==1,"six themes and initial selection");
        Check(accents.Length==6 && accents.All(r=>r.IsChecked!=true) && !reset.IsEnabled && value.Text=="主题原色","six accents and theme default");
        Check(radios.Select(r=>(string)r.Tag.GetType().GetField("Id",Members).GetValue(r.Tag)).SequenceEqual(presetIds),"theme order and variants");
        Check(accents.Select(r=>(string)r.Tag.GetType().GetField("Id",Members).GetValue(r.Tag)).SequenceEqual(accentIds),"accent choices");
        var exit=Activator.CreateInstance(app.GetType("AISwitchDesk.AppExitMenu"),Members,null,
            new object[]{rail,new Action<int>(i=>{})},null);
        type.GetField("exitMenu",Members).SetValue(rail,exit);
        var exitWindow=(System.Windows.Window)exit.GetType().GetField("window",Members).GetValue(exit);
        var usage=(ToolStripDropDown)type.GetField("usagePopup",Members).GetValue(rail);
        var themeType=app.GetType("AISwitchDesk.DeskTheme");
        var surface=(System.Windows.Controls.Border)window.FindName("Surface");
        var appearance=(System.Windows.FrameworkElement)window.FindName("AppearancePage");
        var preference=Path.Combine(folder,"theme.txt");
        Func<object> current=()=>themeType.GetProperty("Current",Static).GetValue(null,null);
        foreach(var radio in radios.Skip(1).Concat(radios.Take(1)))
        {
            radio.IsChecked=true;
            window.UpdateLayout();
            var theme=current();
            string id=(string)themeType.GetField("Id",Members).GetValue(theme);
            Color paper=(Color)themeType.GetField("Surface",Members).GetValue(theme);
            Color bottom=(Color)themeType.GetField("RailBottom",Members).GetValue(theme);
            var bg=((System.Windows.Media.SolidColorBrush)surface.Background).Color;
            var exitBrush=((System.Windows.Media.SolidColorBrush)((System.Windows.Controls.Border)exitWindow.Content).Background).Color;
            Check(ReferenceEquals(radio.Tag,themeType.GetField("Preset",Members).GetValue(theme)) && File.ReadAllText(preference)==id,"selection did not persist");
            Check(bg.R==paper.R && bg.G==paper.G && bg.B==paper.B && exitBrush==bg,"WPF surfaces did not update");
            Check(rail.BackColor==bottom && targets.BackColor==paper && usage.BackColor==paper,"WinForms surfaces did not update");
            Check(!rail.Region.IsVisible(0,0) && rail.Region.IsVisible(rail.Width/2,2),"rail corner or top cutout");
            Check(!targets.Region.IsVisible(0,0) && targets.Region.IsVisible(targets.Width/2,1),"upload corner or top cutout");
            var end=accentPanel.TranslatePoint(new System.Windows.Point(accentPanel.ActualWidth,accentPanel.ActualHeight),appearance);
            Check(end.Y<=appearance.ActualHeight+1 && accents.All(a=>a.ActualWidth>=50 && a.ActualHeight>=28),"appearance controls clipped");
            SavePreview(rail,targets,surface,folder,id);
        }
        for(int p=0;p<radios.Length;p++)
        {
            radios[p].IsChecked=true;
            if(p>0) Check(accents.Last().IsChecked==true && File.ReadAllText(preference)==presetIds[p]+"|purple","switching theme lost the accent");
            for(int a=0;a<accents.Length;a++)
            {
                accents[a].IsChecked=true;
                window.UpdateLayout();
                object theme=current();
                var fill=(Color)themeType.GetField("Accent",Members).GetValue(theme);
                var foreground=(Color)themeType.GetField("OnAccent",Members).GetValue(theme);
                var stroke=(Color)themeType.GetField("AccentLine",Members).GetValue(theme);
                var paper=(Color)radios[p].Tag.GetType().GetField("Surface",Members).GetValue(radios[p].Tag);
                string wanted=presetIds[p]+"|"+accentIds[a];
                string saved=File.ReadAllText(preference);
                int checkedAccents=accents.Count(r=>r.IsChecked==true);
                Check(radios[p].IsChecked==true && checkedAccents==1 && reset.IsEnabled &&
                    value.Text==accents[a].Content.ToString() && saved==wanted,
                    "wanted "+wanted+", stored "+saved+", preset selected "+radios[p].IsChecked+", accents checked "+
                    checkedAccents+", reset enabled "+reset.IsEnabled+", label "+value.Text);
                Check((fill.ToArgb()&0xFFFFFF)==accentRgb[a] && ResourceRgb(window,"ThemeAccent")==accentRgb[a] &&
                    ResourceRgb(exitWindow,"ThemeAccent")==accentRgb[a],"accent does not match GPT");
                Check(ResourceRgb(window,"ThemeSurface")==(paper.ToArgb()&0xFFFFFF) && targets.BackColor==paper &&
                    usage.BackColor==paper,"overriding accent also changed the base palette");
                Check(Contrast(foreground,fill)>=4.5 && Contrast(stroke,paper)>=3.0,"indicator contrast");
                var slashColor=((System.Windows.Media.SolidColorBrush)slash.Stroke).Color;
                Check(slashColor.R==stroke.R && slashColor.G==stroke.G && slashColor.B==stroke.B,"settings slash lost the current accent");
            }
        }
        radios[3].IsChecked=true;
        accents[2].IsChecked=true;
        window.UpdateLayout();
        SavePreview(rail,targets,surface,folder,"dracula-yellow");
        CheckReadAfterRestart(app,folder,"dracula|yellow");
        reset.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(radios[3].IsChecked==true && accents.All(r=>r.IsChecked!=true) && !reset.IsEnabled &&
            ResourceRgb(window,"ThemeAccent")==0xFF79C6 && File.ReadAllText(preference)=="dracula","restoring original theme accent");
        CheckReadAfterRestart(app,folder,"dracula");
        using(var reader=new FileStream(preference,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var release=System.Threading.Tasks.Task.Run(delegate { System.Threading.Thread.Sleep(75); reader.Dispose(); });
            accents[0].IsChecked=true;
            release.Wait();
            Check(File.ReadAllText(preference)=="dracula|blue" && accents[0].IsChecked==true,"preference held by a short-lived reader");
        }
        using(var reader=new FileStream(preference,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            accents[1].IsChecked=true;
            Check(File.ReadAllText(preference)=="dracula|blue" && accents[0].IsChecked==true && accents[1].IsChecked!=true &&
                ResourceRgb(window,"ThemeAccent")==accentRgb[0],"failed save should restore the visible selection");
        }
        ((System.Windows.Controls.RadioButton)window.FindName("UseTab")).IsChecked=true;
        Check(((System.Windows.UIElement)window.FindName("UsePage")).IsVisible,"usage tab");
        ((System.Windows.Controls.RadioButton)window.FindName("LogTab")).IsChecked=true;
        Check(((System.Windows.UIElement)window.FindName("LogPage")).IsVisible,"handoff tab");
        ((System.Windows.Controls.RadioButton)window.FindName("AppearanceTab")).IsChecked=true;
        string[] legacy={"mist","graphite","pine","sand","unknown-from-another-version","dracula|unknown"};
        string[] expected={"codex-light","github-dark","everforest-dark","solarized-light","codex-light","dracula"};
        for(int i=0;i<legacy.Length;i++)
        {
            File.WriteAllText(preference,legacy[i]);
            CheckReadAfterRestart(app,folder,expected[i]);
        }
    }

    private static void CheckSettingsFocus(System.Windows.Window window, Action reopen)
    {
        var outline=window.Resources[System.Windows.SystemParameters.FocusVisualStyleKey] as System.Windows.Style;
        Check(outline!=null,"settings still use the Windows dotted focus visual");
        string[] controls={"UseTab","LogTab","QueueTab","AppearanceTab","Alert20","Refresh","AutoSend","LogList",
            "Resume","ClearUploads","OriginalAccent","Smaller","Larger","Compare","ExitRail"};
        foreach(string name in controls)
            Check(((System.Windows.Controls.Control)window.FindName(name)).FocusVisualStyle==outline,"focus style on "+name);
        foreach(string name in new[]{"ThemeChoices","AccentChoices"})
        {
            var panel=(System.Windows.Controls.Panel)window.FindName(name);
            Check(panel.Children.OfType<System.Windows.Controls.RadioButton>().All(r=>r.FocusVisualStyle==outline),"focus style on "+name);
        }
        var list=(System.Windows.Controls.ListBox)window.FindName("LogList");
        var entry=new System.Windows.Controls.ListBoxItem();
        list.Items.Add(entry);
        Check(entry.FocusVisualStyle==outline,"focus style on new list items");
        list.Items.Remove(entry);

        var close=(System.Windows.Controls.Button)window.FindName("CloseTop");
        Check(close.Focusable && close.IsTabStop && close.FocusVisualStyle!=null && close.FocusVisualStyle!=outline,
            "close button requires its own keyboard cue");
        window.UpdateLayout();
        var glyph=(System.Windows.Shapes.Path)close.Template.FindName("glyph",close);
        var bounds=glyph.RenderedGeometry.Bounds;
        var center=glyph.TranslatePoint(new System.Windows.Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2),close);
        Check(Math.Abs(center.X-close.ActualWidth/2)<.8 && Math.Abs(center.Y-close.ActualHeight/2)<.8,"close glyph off center");
        // Ask WPF to display real keyboard adorners on the non-interactive test desktop.
        var navigation=typeof(System.Windows.Input.KeyboardNavigation);
        var keyboardCues=navigation.GetProperty("AlwaysShowFocusVisual",Static);
        var showCue=navigation.GetMethod("ShowFocusVisual",Static,null,Type.EmptyTypes,null);
        object previous=keyboardCues.GetValue(null,null);
        try
        {
            keyboardCues.SetValue(null,true,null);
            var themePanel=(System.Windows.Controls.Panel)window.FindName("ThemeChoices");
            var themeChoice=(System.Windows.Controls.Control)themePanel.Children[0];
            foreach(var control in new[]{themeChoice,(System.Windows.Controls.Control)window.FindName("AppearanceTab"),close})
            {
                control.Focus();
                showCue.Invoke(null,null);
                window.UpdateLayout();
                var layer=System.Windows.Documents.AdornerLayer.GetAdornerLayer(control);
                var cues=layer.GetAdorners(control);
                Check(cues!=null && cues.Length==1,"expected one visible keyboard indicator");
                var host=(System.Windows.Controls.Control)System.Windows.Media.VisualTreeHelper.GetChild(cues[0],0);
                host.ApplyTemplate();
                var mark=(System.Windows.Controls.Border)System.Windows.Media.VisualTreeHelper.GetChild(host,0);
                var paint=(System.Windows.Media.SolidColorBrush)(control==close?mark.Background:mark.BorderBrush);
                Check(paint!=null && paint.Color==((System.Windows.Media.SolidColorBrush)window.Resources["ThemeAccentLine"]).Color,
                    "keyboard indicator did not render the active theme color");
                Check(control!=close || (mark.ActualHeight==2 && mark.ActualWidth<=14 && mark.BorderThickness.Left==0),
                    "close focus should draw a short line only");
            }
        }
        finally { keyboardCues.SetValue(null,previous,null); }

        // Check native and WPF hit testing on the padding, then exercise the button's invoke action.
        var handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var local=new System.Windows.Point(3,close.ActualHeight/2);
        var hit=window.InputHitTest(close.TranslatePoint(local,window)) as System.Windows.DependencyObject;
        while(hit!=null && hit!=close) hit=System.Windows.Media.VisualTreeHelper.GetParent(hit);
        Check(hit==close,"close button padding does not accept the pointer");
        var screen=close.PointToScreen(local);
        var packed=new IntPtr(((int)Math.Round(screen.Y)<<16)|(ushort)(int)Math.Round(screen.X));
        Check(SendMessage(handle,0x0084,IntPtr.Zero,packed).ToInt32()==1,"window chrome intercepted the close button");
        var peer=System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(close);
        var action=(System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
        action.Invoke();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,new Action(delegate{}));
        Check(!window.IsVisible,"close action did not hide settings");

        foreach(var key in new[]{System.Windows.Input.Key.Enter,System.Windows.Input.Key.Space,System.Windows.Input.Key.Escape})
        {
            reopen();
            close.Focus();
            Check(close.IsKeyboardFocused,"close button cannot take keyboard focus");
            var source=System.Windows.PresentationSource.FromVisual(close);
            var down=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,source,Environment.TickCount,key);
            down.RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent;
            close.RaiseEvent(down);
            if(!down.Handled)
            {
                down.RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent;
                close.RaiseEvent(down);
            }
            if(window.IsVisible)
            {
                var up=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,source,Environment.TickCount,key);
                up.RoutedEvent=System.Windows.Input.Keyboard.KeyUpEvent;
                close.RaiseEvent(up);
            }
            Check(!window.IsVisible,"settings did not close with "+key);
        }
        reopen();
    }

    private static int ResourceRgb(System.Windows.Window window,string name)
    {
        var c=((System.Windows.Media.SolidColorBrush)window.Resources[name]).Color;
        return (c.R<<16) | (c.G<<8) | c.B;
    }

    private static double Contrast(Color a, Color b)
    {
        Func<byte,double> linear=v=>v<=10 ? v/(255.0*12.92) : Math.Pow((v/255.0+.055)/1.055,2.4);
        Func<Color,double> luminance=c=>.2126*linear(c.R)+.7152*linear(c.G)+.0722*linear(c.B);
        double first=luminance(a)+.05, second=luminance(b)+.05;
        return Math.Max(first,second)/Math.Min(first,second);
    }

    private static void CheckReadAfterRestart(Assembly app,string folder,string expected)
    {
        var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
            "--read-theme \""+app.Location+"\" \""+folder+"\" "+expected){UseShellExecute=false,CreateNoWindow=true};
        using(var child=Process.Start(start))
            Check(child.WaitForExit(5000) && child.ExitCode==0,"restarted application selected the wrong skin");
    }

    private static void SavePreview(Form rail,Form targets,System.Windows.FrameworkElement settings,string folder,string id)
    {
        foreach(var item in new[]{rail,targets})
        {
            using(var bitmap=new Bitmap(item.Width,item.Height))
            {
                item.DrawToBitmap(bitmap,item.ClientRectangle);
                bitmap.Save(Path.Combine(folder,id+(item==rail?"-rail.png":"-drop.png")));
            }
        }
        var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(settings.ActualWidth),
            (int)Math.Ceiling(settings.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(settings);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using(var file=File.Create(Path.Combine(folder,id+"-settings.png"))) encoder.Save(file);
    }
}
