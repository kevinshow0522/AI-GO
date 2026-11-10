using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class NativeDropTests
{
    private static readonly string[] Names = { "后台 文件 A.txt", "示例 B.txt" };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--receiver") return Receiver();
        var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var files = Names.Select(name => Path.Combine(folder, name)).ToArray();
        for (int i = 0; i < files.Length; i++) File.WriteAllText(files[i], "drop-test-" + i);
        var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--receiver") {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        using (var receiver = Process.Start(start))
        {
            try
            {
                var info = receiver.StandardOutput.ReadLine().Split(',');
                var asm = Assembly.LoadFrom(Path.GetFullPath(args[0]));
                var sender = asm.GetType("AISwitchDesk.ComposerFileDrop", true);
                var method = sender.GetMethod("Deliver", BindingFlags.Static | BindingFlags.NonPublic);
                method.Invoke(null, new object[]{new IntPtr(long.Parse(info[0])),int.Parse(info[1]),int.Parse(info[2]),files,new Action(delegate {})});
                var done = receiver.StandardOutput.ReadLineAsync();
                if (!done.Wait(4000) || done.Result != "RECEIVED") throw new Exception("receiver did not read the expected files");
                Console.WriteLine("PASS separate process received two files and verified their contents");
                return 0;
            }
            catch (Exception e) { Console.WriteLine("FAIL " + e.GetBaseException()); return 1; }
            finally { receiver.StandardInput.WriteLine("close"); if (!receiver.WaitForExit(3000)) receiver.Kill(); }
        }
    }

    private static int Receiver()
    {
        var form = new Form { Text="AI Switch local drop test", ClientSize=new Size(280,110), ShowInTaskbar=false, StartPosition=FormStartPosition.Manual, Location=new Point(30,80) };
        var panel = new Panel { Dock=DockStyle.Fill, AllowDrop=true };
        panel.DragEnter += delegate(object sender, DragEventArgs e) {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        };
        panel.DragOver += delegate(object sender, DragEventArgs e) { e.Effect=DragDropEffects.Copy; };
        panel.DragDrop += delegate(object sender, DragEventArgs e) {
            var list=(string[]) e.Data.GetData(DataFormats.FileDrop);
            var expected=list.Select(Path.GetFileName).SequenceEqual(Names) &&
                list.Select(File.ReadAllText).SequenceEqual(new[]{"drop-test-0","drop-test-1"});
            e.Effect=DragDropEffects.Copy;
            Console.WriteLine(expected ? "RECEIVED" : "WRONG FILES");
        };
        form.Controls.Add(panel);
        form.Shown += delegate {
            var point=panel.PointToScreen(new Point(25,25));
            Console.WriteLine(form.Handle.ToInt64()+","+point.X+","+point.Y);
            var closer = new Thread(delegate() { Console.ReadLine(); form.BeginInvoke(new Action(form.Close)); }) { IsBackground=true };
            closer.Start();
        };
        Application.Run(form);
        return 0;
    }
}
