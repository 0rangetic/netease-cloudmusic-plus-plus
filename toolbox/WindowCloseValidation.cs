// STA console test. Args: toolbox EXE, optional --assert-stable.
// Exercises the real Close()/tray restore path without startup network tasks.
using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class WindowCloseValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Call(Form form, string name, params object[] args) { return form.GetType().GetMethod(name, Private).Invoke(form, args); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread]
    private static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ncm-close-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("NeteaseToolbox.ToolboxForm", true);
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                type.GetField("customAppearancePath", Private).SetValue(form, Path.Combine(directory, "custom.json"));
                type.GetField("themeSettingPath", Private).SetValue(form, Path.Combine(directory, "theme.txt"));
                var events = (System.ComponentModel.EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(form, null);
                foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
                    if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    object key = field.GetValue(null);
                    if (events[key] != null) events.RemoveHandler(key, events[key]);
                }
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Size = new Size(2560, 1500);
                Call(form, "ResetCustomAppearance");
                string imagePath = Path.Combine(directory, "background.png");
                using (var bitmap = new Bitmap(1280, 800)) {
                    using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.SteelBlue);
                    bitmap.Save(imagePath);
                }
                Call(form, "InstallWallpaper", imagePath);
                form.Show();
                Call(form, "NavigateTo", 0);
                var list = (ListView)form.Controls.Find("PendingSongList", true)[0];
                list.BeginUpdate();
                try { for (int row = 0; row < 1500; row++) list.Items.Add(new ListViewItem(new[] { "歌曲 " + row, "流行", "电子", "轻快", "网易云", "已分析", "来源" })); }
                finally { list.EndUpdate(); }
                var cached = form.Controls.Find("GenreSongList", true)[0];
                var cachedItems = (IList)cached.GetType().GetProperty("Items").GetValue(cached, null);
                cached.GetType().GetMethod("BeginUpdate").Invoke(cached, null);
                for (int row = 0; row < 1500; row++) cachedItems.Add(new ListViewItem(new[] { "缓存歌曲 " + row, "流行", "电子", "轻快", "网易云", "已分析", "来源" }));
                cached.GetType().GetMethod("EndUpdate").Invoke(cached, null);
                Call(form, "NavigateTo", 1);
                form.Refresh();
                int recreated = 0;
                form.HandleCreated += delegate { recreated++; };
                var originalHandle = form.Handle;
                var tableHandle = list.Handle;
                var cachedHandle = cached.Handle;
                int generation = (int)type.GetField("wallpaperCanvasGeneration", Private).GetValue(form);
                for (int iteration = 0; iteration < 3; iteration++) {
                    var watch = Stopwatch.StartNew();
                    form.Close();
                    watch.Stop();
                    Check(!form.Visible && !form.IsDisposed, "Close hides to tray without terminating capture");
                    Console.WriteLine("Close " + (iteration + 1) + ": " + watch.ElapsedMilliseconds + " ms; handle recreations=" + recreated);
                    if (args.Length > 1) {
                        Check(form.Handle == originalHandle && list.Handle == tableHandle && cached.Handle == cachedHandle && recreated == 0, "Close must retain main, native and cached table HWNDs");
                        Check((int)type.GetField("wallpaperCanvasGeneration", Private).GetValue(form) == generation, "Close must not rebuild wallpaper");
                        Check(!((Timer)cached.GetType().GetField("frameTimer", Private).GetValue(cached)).Enabled, "Hidden cached table must stop its render timer");
                    }
                    Call(form, "ShowMainWindow");
                    form.Location = new Point(-30000, -30000);
                    Check(form.Visible && list.Items.Count == 1500 && cachedItems.Count == 1500, "Tray restore retains page and rows in both table types");
                }
                Call(form, "ClearWallpaper");
                form.Close();
                Check(!form.Visible && !form.IsDisposed, "Plain theme also hides to tray");
                Call(form, "ShowMainWindow");
                form.Location = new Point(-30000, -30000);
                type.GetField("allowExit", Private).SetValue(form, true);
                var exit = Stopwatch.StartNew();
                form.Close();
                exit.Stop();
                Check(form.IsDisposed, "Explicit exit still disposes resources");
                Console.WriteLine("Explicit exit: " + exit.ElapsedMilliseconds + " ms. PASS: close, restore and exit.");
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(directory, true); }
    }
}
