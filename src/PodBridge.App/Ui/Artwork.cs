using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PodBridge.App.Ui;

/// <summary>
/// The 3D-rendered AirPods artwork (one look for every model) and the fancy/performance switch.
/// Rendered offline from a CC BY 4.0 model (see THIRD-PARTY-NOTICES.md and tools/render3d), so at
/// runtime it's just pictures: a still for Settings, and a 30-frame "lid opens, buds rise"
/// animation for the case-open card.
/// </summary>
internal static class Artwork
{
    public const int FrameCount = 30;
    private static readonly TimeSpan FrameTime = TimeSpan.FromSeconds(1.0 / 30);

    public static bool Fancy { get; set; } = true;

    private static BitmapImage? _hero;

    public static BitmapImage Hero => _hero ??= Load("Assets/airpods-hero.png");

    public static BitmapImage Frame(int i) => Load($"Assets/anim/f{i:00}.png");

    private static bool? _hasFrames;

    /// <summary>False if the animation frames aren't built into this exe (then the still is used).</summary>
    public static bool HasFrames
    {
        get
        {
            if (_hasFrames is bool known) return known;
            try { _hasFrames = Application.GetResourceStream(new Uri("pack://application:,,,/PodBridge;component/Assets/anim/f00.png")) is not null; }
            catch (System.IO.IOException) { _hasFrames = false; }
            return _hasFrames.Value;
        }
    }

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
    /// Plays the case-open animation once into <paramref name="target"/> and stops on the final
    /// pose. Frames are decoded as they play (nothing large stays in memory). Respects Windows'
    /// "Animation effects" setting: if it's off, the final frame is shown straight away.
    /// </summary>
    public static void PlayCaseOpen(Image target)
    {
        if (!HasFrames) { target.Source = Hero; return; }
        if (!SystemParameters.ClientAreaAnimation)
        {
            target.Source = Frame(FrameCount - 1);
            return;
        }
        var started = DateTime.UtcNow;
        int shown = -1;
        void OnRendering(object? sender, EventArgs e)
        {
            // Time-based, so a slow machine skips frames instead of slowing the animation down.
            int i = Math.Min(FrameCount - 1, (int)((DateTime.UtcNow - started) / FrameTime));
            if (i != shown) { target.Source = Frame(i); shown = i; }
            if (i == FrameCount - 1 || !target.IsVisible && shown > 0) CompositionTarget.Rendering -= OnRendering;
        }
        target.Source = Frame(0);
        CompositionTarget.Rendering += OnRendering;
    }

    public const string Credit = "3D model “AirPods 4” by Falah3D, CC BY 4.0";
    public const string CreditUrl = "https://sketchfab.com/3d-models/airpods-4-e44730974131402eb496352cab19c82e";
}
