using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace RelativisticRod;

internal sealed record Palette(Color True, Color Seen, Color OnSurface, Color Variant, Color Grid);

internal sealed record Readout(double SegmentPercent, double Delay, double Lag, double AngleDeg);

public sealed partial class MainWindow : Window
{
    // ---- State shared between the UI thread and the Win2D game-loop thread ------------------
    // Reference-typed snapshots are swapped atomically with Volatile.Write; nothing is locked.
    private SimParams _params = new(0.25, 0.60, -25, IsPlaying: true, ShowPhotons: true);
    private Palette _palette = new(
        Color.FromArgb(255, 0x25, 0x75, 0xFC), Color.FromArgb(255, 0xC8, 0x42, 0x88),
        Color.FromArgb(0xE4, 0, 0, 0), Color.FromArgb(0x9E, 0, 0, 0), Color.FromArgb(0x30, 0, 0, 0));
    private Readout _readout = new(50, 0, 0, 0);
    private double _hoverS = RodModel.RodLength / 2;

    // ---- Game-loop-thread-only data ---------------------------------------------------------
    private double _simTime;
    private readonly RodPoint[] _true = new RodPoint[RodModel.Samples + 1];
    private readonly RetardedPoint[] _seen = new RetardedPoint[RodModel.Samples + 1];
    private readonly RetardedPoint[] _photonSrc = new RetardedPoint[RodModel.PhotonCount];
    private readonly Vector2[] _truePts = new Vector2[RodModel.Samples + 1];
    private readonly Vector2[] _seenPts = new Vector2[RodModel.Samples + 1];

    private readonly CanvasStrokeStyle _dashedGrid = new() { CustomDashStyle = new float[] { 4, 4 } };
    private readonly CanvasStrokeStyle _dashedLink = new() { CustomDashStyle = new float[] { 2, 2 } };
    private readonly CanvasStrokeStyle _round = new()
    {
        LineJoin = CanvasLineJoin.Round,
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round
    };
    private readonly CanvasTextFormat _labelFormat = new()
    {
        FontSize = 11,
        FontFamily = "Segoe UI Variable",
        HorizontalAlignment = CanvasHorizontalAlignment.Center
    };

    // ---- UI-thread-only data ----------------------------------------------------------------
    private readonly DispatcherQueueTimer _readoutTimer;
    private readonly DispatcherQueueTimer _saveTimer;
    private Readout? _shownReadout;
    private bool _isPlaying = true;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1040, 980));

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        // Restore persisted state (replaces the HTML pageVault).
        var saved = StateStore.Load();
        VWaveSlider.Value = saved.VWave;
        FreqSlider.Value = saved.Freq;
        ObsSlider.Value = saved.ObsY;
        PhotonsToggle.IsChecked = saved.ShowPhotons;
        _isPlaying = saved.IsPlaying;
        UpdatePlayButton();

        var queue = DispatcherQueue.GetForCurrentThread();

        _readoutTimer = queue.CreateTimer();
        _readoutTimer.Interval = TimeSpan.FromMilliseconds(33);
        _readoutTimer.Tick += (_, _) => RefreshReadout();
        _readoutTimer.Start();

        _saveTimer = queue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();

        Root.ActualThemeChanged += (_, _) => RefreshPalette();
        Root.Loaded += (_, _) => RefreshPalette();
        RefreshPalette();

        Closed += OnClosed;

        _ready = true;
        PushParams();
    }

    // =========================================================================================
    //  Controls
    // =========================================================================================

    private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => PushParams();

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        _isPlaying = !_isPlaying;
        UpdatePlayButton();
        PushParams();
    }

    private void Photons_Click(object sender, RoutedEventArgs e) => PushParams();

    private void UpdatePlayButton()
    {
        PlayIcon.Glyph = _isPlaying ? "\uE769" : "\uE768"; // Pause / Play (Segoe Fluent Icons)
        PlayLabel.Text = _isPlaying ? "Pause" : "Play";
        AutomationProperties.SetName(PlayPauseButton, _isPlaying ? "Pause simulation" : "Play simulation");
    }

    private void PushParams()
    {
        if (!_ready) return; // ValueChanged fires during InitializeComponent

        var p = new SimParams(
            Math.Round(VWaveSlider.Value, 2),
            Math.Round(FreqSlider.Value, 2),
            Math.Round(ObsSlider.Value),
            _isPlaying,
            PhotonsToggle.IsChecked == true);

        Volatile.Write(ref _params, p);

        VWaveValue.Text = p.VWave.ToString("0.00");
        FreqValue.Text = p.Freq.ToString("0.00") + " Hz";
        ObsValue.Text = p.ObsY.ToString("0");

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        var p = Volatile.Read(ref _params);
        StateStore.Save(new PersistedState
        {
            VWave = p.VWave, Freq = p.Freq, ObsY = p.ObsY,
            IsPlaying = p.IsPlaying, ShowPhotons = p.ShowPhotons
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _readoutTimer.Stop();
        _saveTimer.Stop();
        SaveNow();
        RodCanvas.RemoveFromVisualTree(); // Win2D: release the swap chain / device resources
    }

    // =========================================================================================
    //  Pointer / keyboard inspection
    // =========================================================================================

    private void RodCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RodCanvas.Focus(FocusState.Pointer);
        RodCanvas_Pointer(sender, e);
    }

    private void RodCanvas_Pointer(object sender, PointerRoutedEventArgs e)
    {
        // Same mapping as the HTML: vertical pointer position -> rod parameter s.
        double y = e.GetCurrentPoint(RodCanvas).Position.Y;
        double w = RodCanvas.ActualWidth, h = RodCanvas.ActualHeight;
        double scale = Math.Min(w, h) / 100.0;
        if (scale <= 0) return;

        double s = Math.Clamp((h * 0.65 - y) / scale, 2.0, RodModel.RodLength);
        Volatile.Write(ref _hoverS, s);
    }

    private void RodCanvas_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        double step = e.Key switch { Windows.System.VirtualKey.Up => 1.0, Windows.System.VirtualKey.Down => -1.0, _ => 0.0 };
        if (step == 0.0) return;

        double s = Math.Clamp(Volatile.Read(ref _hoverS) + step, 2.0, RodModel.RodLength);
        Volatile.Write(ref _hoverS, s);
        e.Handled = true;
    }

    private void RefreshReadout()
    {
        var r = Volatile.Read(ref _readout);
        if (r == _shownReadout) return;
        _shownReadout = r;

        SegmentText.Text = $"{r.SegmentPercent:0}% L";
        DelayText.Text = $"{r.Delay:0.00} s";
        LagText.Text = $"{r.Lag:0.0} units";
        AngleText.Text = $"{r.AngleDeg:0.0}°";
    }

    // =========================================================================================
    //  Theme
    // =========================================================================================

    private void RefreshPalette()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var dict = (ResourceDictionary)Root.Resources.ThemeDictionaries[dark ? "Dark" : "Light"];

        Color trueC = ((SolidColorBrush)dict["TrueSeriesBrush"]).Color;
        Color seenC = ((SolidColorBrush)dict["SeenSeriesBrush"]).Color;

        // Fluent TextFillColorPrimary / Secondary and a subtle divider tone.
        var pal = dark
            ? new Palette(trueC, seenC, Color.FromArgb(0xFF, 255, 255, 255), Color.FromArgb(0xC5, 255, 255, 255), Color.FromArgb(0x30, 255, 255, 255))
            : new Palette(trueC, seenC, Color.FromArgb(0xE4, 0, 0, 0), Color.FromArgb(0x9E, 0, 0, 0), Color.FromArgb(0x30, 0, 0, 0));

        Volatile.Write(ref _palette, pal);
    }

    // =========================================================================================
    //  Simulation step (game-loop thread)
    // =========================================================================================

    private void Canvas_Update(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        var p = Volatile.Read(ref _params);

        if (p.IsPlaying)
            _simTime += Math.Min(0.05, args.Timing.ElapsedTime.TotalSeconds);

        RodModel.SampleCurves(_simTime, p, _true, _seen);

        if (p.ShowPhotons)
        {
            for (int k = 0; k < RodModel.PhotonCount; k++)
            {
                double s = (double)k / (RodModel.PhotonCount - 1) * RodModel.RodLength;
                _photonSrc[k] = RodModel.Retarded(s, _simTime, 0.0, p.ObsY, p.Freq, p.VWave);
            }
        }

        // Inspection readout for the hovered segment.
        double hoverS = Volatile.Read(ref _hoverS);
        int idx = Math.Clamp((int)Math.Round(hoverS / RodModel.RodLength * RodModel.Samples), 0, RodModel.Samples);
        var t = _true[idx];
        var seen = _seen[idx];

        Volatile.Write(ref _readout, new Readout(
            SegmentPercent: t.S / RodModel.RodLength * 100.0,
            Delay: seen.Delay,
            Lag: RodModel.Hypot(t.X - seen.X, t.Y - seen.Y),
            AngleDeg: seen.Angle * 180.0 / Math.PI));
    }

    // =========================================================================================
    //  Rendering (Win2D, game-loop thread)
    // =========================================================================================

    private static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255.0, 0, 255), c.R, c.G, c.B);

    private static CanvasGeometry BuildPolyline(ICanvasResourceCreator rc, Vector2[] pts)
    {
        using var pb = new CanvasPathBuilder(rc);
        pb.BeginFigure(pts[0]);
        for (int i = 1; i < pts.Length; i++) pb.AddLine(pts[i]);
        pb.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(pb);
    }

    private void Canvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var pal = Volatile.Read(ref _palette);
        var p = Volatile.Read(ref _params);

        float w = (float)sender.Size.Width, h = (float)sender.Size.Height;
        if (w <= 0 || h <= 0) return;

        // World -> screen (DIPs): origin at 50% width / 65% height, y up.
        float cx = w / 2f, cy = h * 0.65f, scale = MathF.Min(w, h) / 100f;
        Vector2 ToScreen(double x, double y) => new(cx + (float)x * scale, cy - (float)y * scale);

        // Round the content to the card's inner corner radius.
        using var clip = CanvasGeometry.CreateRoundedRectangle(sender, 0, 0, w, h, 7, 7);
        using var layer = ds.CreateLayer(1f, clip);

        Vector2 obs = ToScreen(0, p.ObsY);

        // Distance rings around the observer
        for (int r = 20; r <= 100; r += 20)
            ds.DrawCircle(obs, r * scale, pal.Grid, 1f, _dashedGrid);

        for (int i = 0; i <= RodModel.Samples; i++)
        {
            _truePts[i] = ToScreen(_true[i].X, _true[i].Y);
            _seenPts[i] = ToScreen(_seen[i].X, _seen[i].Y);
        }

        // True physical rod
        using (var g = BuildPolyline(sender, _truePts))
            ds.DrawGeometry(g, pal.True, 3f, _round);

        // Apparent (retarded) rod with a soft glow (layered strokes stand in for canvas shadowBlur)
        using (var g = BuildPolyline(sender, _seenPts))
        {
            ds.DrawGeometry(g, WithAlpha(pal.Seen, 0.10), 12f, _round);
            ds.DrawGeometry(g, WithAlpha(pal.Seen, 0.20), 7f, _round);
            ds.DrawGeometry(g, pal.Seen, 3.5f, _round);
        }

        // Photons streaming from the retarded rod position toward the observer
        if (p.ShowPhotons)
        {
            for (int k = 0; k < RodModel.PhotonCount; k++)
            {
                var src = _photonSrc[k];
                double phase = (_simTime * 0.8 + (double)k / RodModel.PhotonCount) % 1.0;
                Vector2 pos = ToScreen(src.X + (0.0 - src.X) * phase, src.Y + (p.ObsY - src.Y) * phase);
                ds.FillCircle(pos, 2.5f, pal.Seen);
            }
        }

        // Pivot
        ds.FillCircle(ToScreen(0, 0), 5f, pal.OnSurface);

        // Observer
        ds.FillCircle(obs, 7f, pal.OnSurface);
        ds.DrawCircle(obs, 7f, pal.Seen, 2f);
        ds.DrawText("Observer", new Rect(obs.X - 40, obs.Y + 7, 80, 16), pal.Variant, _labelFormat);

        // Inspected segment: true vs apparent position and the photon path
        double hoverS = Volatile.Read(ref _hoverS);
        int idx = Math.Clamp((int)Math.Round(hoverS / RodModel.RodLength * RodModel.Samples), 0, RodModel.Samples);
        Vector2 ptTrue = _truePts[idx], ptSeen = _seenPts[idx];

        ds.DrawLine(ptTrue, ptSeen, pal.Variant, 1.5f, _dashedLink);
        ds.DrawLine(ptSeen, obs, WithAlpha(pal.Seen, 0.4), 1.5f);
        ds.FillCircle(ptTrue, 5f, pal.True);
        ds.FillCircle(ptSeen, 5f, pal.Seen);
    }
}
