using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GewCoordinator;

public partial class App : Application
{
    /// <summary>
    /// 動作確認用のヘッドレスモード:
    ///   GewCoordinator.exe --render out.png [--root DIR] [B=1 H=0 L=0 W=0 S=0] [pose=0] [frame=0] [mirror=1] [behind=WS]
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        int at = Array.IndexOf(e.Args, "--render");
        if (at < 0 || at + 1 >= e.Args.Length)
        {
            base.OnStartup(e);
            return;
        }

        int code = 0;
        try { HeadlessRender(e.Args, e.Args[at + 1]); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); code = 1; }
        Shutdown(code);
    }

    private static void HeadlessRender(string[] args, string output)
    {
        string root = @"C:\Vector\GODIUS";
        var sel = new Dictionary<char, int?>();
        int pose = 0, frame = 0;
        bool mirror = false;
        var behind = new HashSet<char>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--root" && i + 1 < args.Length) root = args[i + 1];
            var kv = args[i].Split('=');
            if (kv.Length != 2) continue;
            if (kv[0] == "pose") pose = int.Parse(kv[1]);
            else if (kv[0] == "frame") frame = int.Parse(kv[1]);
            else if (kv[0] == "mirror") mirror = kv[1] == "1";
            else if (kv[0] == "behind") foreach (var c in kv[1]) behind.Add(char.ToUpperInvariant(c));
            else if (kv[0].Length == 1) sel[char.ToUpperInvariant(kv[0][0])] = int.Parse(kv[1]);
        }

        var lib = new PartLibrary(root);
        foreach (var kind in PartLibrary.Kinds)
            if (!sel.ContainsKey(kind.Prefix)) sel[kind.Prefix] = lib.Items(kind.Prefix).Cast<int?>().FirstOrDefault();

        var px = lib.Compose(sel, pose, frame, behind, mirror);
        var bmp = BitmapSource.Create(Sprite.Width, Sprite.Height, 96, 96, PixelFormats.Bgra32, null, px, Sprite.Width * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(output);
        enc.Save(fs);
    }
}
