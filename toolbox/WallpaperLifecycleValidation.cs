// Separate STA console test: pass the toolbox EXE. Uses isolated preferences.
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class WallpaperLifecycleValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private static object Call(object instance, string method, params object[] args) { return instance.GetType().GetMethod(method, Private).Invoke(instance, args); }
    [STAThread]
    private static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ncm-lifecycle-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("NeteaseToolbox.ToolboxForm", true);
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                var handle = form.Handle;
                type.GetField("customAppearancePath", Private).SetValue(form, Path.Combine(directory, "custom.json"));
                type.GetField("themeSettingPath", Private).SetValue(form, Path.Combine(directory, "theme.txt"));
                Call(form, "ResetCustomAppearance");
                string path = Path.Combine(directory, "background.png");
                using (var image = new Bitmap(80, 80)) { using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.CornflowerBlue); image.Save(path); }
                Call(form, "InstallWallpaper", path);
                var events = (System.ComponentModel.EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(form, null);
                foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
                    if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    object key = field.GetValue(null);
                    if (events[key] != null) events.RemoveHandler(key, events[key]);
                }
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                Call(form, "NavigateTo", 0);
                var list = (ListView)form.Controls.Find("PendingSongList", true)[0];
                for (int index = 0; index < 100; index++) list.Items.Add(new ListViewItem(new[] { "歌曲", "曲风", "其他", "情绪", "来源", "状态", "目录" }));
                // Native rows can disappear before managed collection bookkeeping
                // finishes. Force this transition and synchronously repaint it.
                SendMessage(list.Handle, 0x1009, IntPtr.Zero, IntPtr.Zero);
                Call(list, "WndProc", Message.Create(list.Handle, 0xF, IntPtr.Zero, IntPtr.Zero));
                list.Items.Clear();
                for (int iteration = 0; iteration < 50; iteration++) {
                    list.BeginUpdate();
                    try { list.Items.Clear(); list.Items.Add(new ListViewItem("临时行")); Call(list, "WndProc", Message.Create(list.Handle, 0xF, IntPtr.Zero, IntPtr.Zero)); }
                    finally { list.EndUpdate(); }
                    Call(form, "NavigateTo", iteration % 5);
                    Call(form, "SetTheme", iteration % 2 == 0);
                    if (iteration % 10 == 0) { Call(form, "ClearWallpaper"); Call(form, "InstallWallpaper", path); }
                    form.Update();
                }
                Call(form, "NavigateTo", 0);
                list.Dispose();
                // The disposed control must pass messages through without
                // allocating a fresh handle or reading its item collection.
                Call(list, "WndProc", Message.Create(IntPtr.Zero, 0xF, IntPtr.Zero, IntPtr.Zero));
            }
            Console.WriteLine("PASS: native clear before managed state update, batch refresh, theme/background transitions and disposal repaint.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(directory, true); }
    }
}
