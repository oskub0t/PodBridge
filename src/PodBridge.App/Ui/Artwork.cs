using System.Windows.Media.Imaging;

namespace PodBridge.App.Ui;

/// <summary>
/// The 3D-rendered AirPods artwork (one image for every model) and the fancy/performance switch.
/// Rendered offline from a CC BY 4.0 model (see THIRD-PARTY-NOTICES.md), so it costs no more at
/// runtime than any other picture.
/// </summary>
internal static class Artwork
{
    public static bool Fancy { get; set; } = true;

    private static BitmapImage? _hero;

    public static BitmapImage Hero
    {
        get
        {
            if (_hero is not null) return _hero;
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri("pack://application:,,,/PodBridge;component/Assets/airpods-hero.png");
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return _hero = img;
        }
    }

    public const string Credit = "3D model “AirPods 4” by Falah3D, CC BY 4.0";
    public const string CreditUrl = "https://sketchfab.com/3d-models/airpods-4-e44730974131402eb496352cab19c82e";
}
