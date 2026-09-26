using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace Barbaric.App;

/// <summary>Previous, play/pause and next buttons in the taskbar thumbnail.</summary>
public static class TaskbarButtons
{
    private static readonly ImageSource PreviousIcon = Icon("M3,3 H5 V13 H3 Z M13,3 V13 L6,8 Z");
    private static readonly ImageSource NextIcon = Icon("M11,3 H13 V13 H11 Z M3,3 V13 L10,8 Z");
    private static readonly ImageSource PlayIcon = Icon("M4,2 V14 L13,8 Z");
    private static readonly ImageSource PauseIcon = Icon("M4,3 H7 V13 H4 Z M9,3 H12 V13 H9 Z");

    public static void Attach(Window window, PlayerApi player)
    {
        var previous = new ThumbButtonInfo { ImageSource = PreviousIcon, Description = "Previous" };
        var playPause = new ThumbButtonInfo { ImageSource = PlayIcon, Description = "Play" };
        var next = new ThumbButtonInfo { ImageSource = NextIcon, Description = "Next" };
        previous.Click += (_, _) => player.Run(player.PreviousAsync);
        playPause.Click += (_, _) => player.Run(player.TogglePlayPauseAsync);
        next.Click += (_, _) => player.Run(player.NextAsync);

        window.TaskbarItemInfo = new TaskbarItemInfo { ThumbButtonInfos = [previous, playPause, next] };

        void Update()
        {
            var now = player.NowPlaying;
            var loaded = now.Path is not null;
            previous.IsEnabled = loaded;
            next.IsEnabled = now.HasNext;
            playPause.IsEnabled = loaded;
            playPause.ImageSource = now.Playing ? PauseIcon : PlayIcon;
            playPause.Description = now.Playing ? "Pause" : "Play";
        }

        player.Changed += (_, _) => Update();
        Update();
    }

    /// <summary>
    /// A 16×16 glyph. The clear square keeps every icon the same size, and the light fill with a dark
    /// edge reads on both the light and the dark taskbar.
    /// </summary>
    private static ImageSource Icon(string path)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
            new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 0.75),
            Geometry.Parse(path)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
