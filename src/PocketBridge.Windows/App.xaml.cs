using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PocketBridge.Windows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var previewIndex = Array.IndexOf(e.Args, "--render-preview");
        var preview = previewIndex >= 0;
        var window = new MainWindow(preview);
        MainWindow = window;
        if (preview)
        {
            if (previewIndex + 1 >= e.Args.Length) { Shutdown(2); return; }
            var path = Path.GetFullPath(e.Args[previewIndex + 1]);
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Top = -10000;
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var view = window.PreviewSurface;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                    Shutdown(0);
                }
                catch { Shutdown(3); }
            };
        }
        window.Show();
    }
}
