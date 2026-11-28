using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PodBridge.App.Ui;

/// <summary>
/// The 3D-rendered AirPods artwork (one look for every model) and the fancy/performance switch.
/// Rendered offline from a CC BY 4.0 model (see THIRD-PARTY-NOTICES.md and tools/render3d), so at
/// runtime it's just pictures: a still for Settings, and a "lid opens, buds rise" frame animation
/// (1 s) for the case-open card.
/// </summary>
internal static class Artwork
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(1);

    public static bool Fancy { get; set; } = true;

    private static BitmapImage? _hero;
    private static BitmapSource[]? _frames;
    private static Task<BitmapSource[]>? _loading;

    public static BitmapImage Hero => _hero ??= Load("Assets/airpods-hero.png");

    private static BitmapImage Load(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri($"pack://application:,,,/PodBridge;component/{path}");
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>
    /// Decodes every frame once, off the UI thread, so playback never stalls on decoding.
    /// Counts the frames that are built in (0 = none: the still is used instead).
    /// </summary>
    public static Task<BitmapSource[]> PreloadAsync() => _loading ??= Task.Run(() =>
    {
        var list = new List<BitmapSource>();
        for (int i = 0; ; i++)
        {
            System.Windows.Resources.StreamResourceInfo? res;
            try { res = Application.GetResourceStream(new Uri($"pack://application:,,,/PodBridge;component/Assets/anim/f{i:00}.png")); }
            catch (System.IO.IOException) { break; }
            if (res is null) break;
            using var stream = res.Stream;
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            list.Add(frame);
        }
        return _frames = list.ToArray();
    });

    /// <summary>The first frame (closed case) to show while the card appears, or null.</summary>
    public static BitmapSource? FirstFrame => _frames is { Length: > 0 } f ? f[0] : null;

    /// <summary>
    /// Plays the case-open animation once and stops on the final pose. Call when the card is on
    /// screen (ContentRendered): timing follows the display's frames (CompositionTarget.Rendering),
    /// so it's as smooth as the screen allows and never slows down; it skips frames instead.
    /// Respects Windows' "Animation effects" setting.
    /// </summary>
    public static async void PlayCaseOpen(Image target)
    {
        var frames = _frames ?? await PreloadAsync();
        if (frames.Length == 0) { target.Source = Hero; return; }
        if (!SystemParameters.ClientAreaAnimation) { target.Source = frames[^1]; return; }

        TimeSpan? start = null;
        int shown = -1;
        void OnRendering(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            start ??= now;
            double progress = (now - start.Value) / Duration;
            int i = Math.Clamp((int)Math.Round(progress * (frames.Length - 1)), 0, frames.Length - 1);
            if (i != shown) { target.Source = frames[i]; shown = i; }
            if (i == frames.Length - 1 || !target.IsVisible) CompositionTarget.Rendering -= OnRendering;
        }
        target.Source = frames[0];
        CompositionTarget.Rendering += OnRendering;
    }

    public const string Credit = "3D model “AirPods 4” by Falah3D, CC BY 4.0";
    public const string CreditUrl = "https://sketchfab.com/3d-models/airpods-4-e44730974131402eb496352cab19c82e";
}
