using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

class Preview
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var app = Assembly.LoadFile(Path.Combine(folder, "AISwitchDesk.exe"));
        var type = app.GetType("AISwitchDesk.DropTargets");
        using (var form = (Form)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[0], null))
        {
            form.StartPosition = FormStartPosition.CenterScreen;
            form.ShowInTaskbar = true;
            Application.Run(form);
            var result = form.DialogResult;
            File.WriteAllText(Path.Combine(folder,"preview-result.txt"), result.ToString());
        }
    }
}
