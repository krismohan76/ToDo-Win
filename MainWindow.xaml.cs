using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FloatingTodo;

public class TodoItem
{
    public string Text { get; set; } = "";
}

public class AppData
{
    public List<string> Items { get; set; } = new();
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Opacity { get; set; } = 0.85;
    public bool Topmost { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool Minimized { get; set; } = false;
}

public partial class MainWindow : Window
{
    private static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FloatingTodo", "data.json");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly ObservableCollection<TodoItem> _items = new();
    private readonly AppData _data;
    private readonly DispatcherTimer _saveTimer;
    private bool _ready;

    // smooth scrolling state
    private double _scrollTarget;
    private bool _scrollAnimating;
    private int _scrollAnimId;

    // bubble click-vs-drag state
    private Point _pressPoint;
    private bool _pressed;

    private DateTime _popupClosedAt = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        bool isNew;
        _data = LoadData(out isNew);

        foreach (var t in _data.Items) _items.Add(new TodoItem { Text = t });
        ItemsList.ItemsSource = _items;
        _items.CollectionChanged += (s, e) =>
        {
            UpdateEmpty();
            RequestSave();
        };
        UpdateEmpty();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (s, e) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        Opacity = Math.Clamp(_data.Opacity, 0.25, 1.0);
        Topmost = _data.Topmost;
        TopmostCheck.IsChecked = _data.Topmost;
        OpacitySlider.Value = Opacity * 100;

        // First run: start with Windows by default (can be switched off in settings).
        if (isNew) _data.StartWithWindows = true;
        StartupCheck.IsChecked = _data.StartWithWindows;
        SetAutostart(_data.StartWithWindows);

        var area = SystemParameters.WorkArea;
        Left = _data.Left ?? area.Right - 320;
        Top = _data.Top ?? area.Top + 60;

        if (_data.Minimized)
        {
            PanelBorder.Visibility = Visibility.Collapsed;
            BubbleBorder.Visibility = Visibility.Visible;
        }

        Loaded += (s, e) => ClampToScreen();
        Closing += (s, e) => SaveNow();
        _ready = true;
    }

    // ---------- storage ----------

    private static AppData LoadData(out bool isNew)
    {
        isNew = !File.Exists(DataPath);
        try
        {
            if (!isNew)
                return JsonSerializer.Deserialize<AppData>(File.ReadAllText(DataPath)) ?? new AppData();
        }
        catch
        {
        }
        return new AppData();
    }

    private void RequestSave()
    {
        if (_saveTimer == null) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        try
        {
            _data.Items = _items.Select(i => i.Text).ToList();
            if (!double.IsNaN(Left) && !double.IsNaN(Top))
            {
                _data.Left = Left;
                _data.Top = Top;
            }
            _data.Minimized = BubbleBorder.Visibility == Visibility.Visible;
            Directory.CreateDirectory(Path.GetDirectoryName(DataPath));
            File.WriteAllText(DataPath, JsonSerializer.Serialize(_data));
        }
        catch
        {
        }
    }

    private static void SetAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
                key.SetValue("FloatingTodo", "\"" + Environment.ProcessPath + "\"");
            else
                key.DeleteValue("FloatingTodo", false);
        }
        catch
        {
        }
    }

    // ---------- window position / shrink / expand ----------

    private void ClampToScreen()
    {
        double w = ActualWidth, h = ActualHeight;
        double minL = SystemParameters.VirtualScreenLeft;
        double minT = SystemParameters.VirtualScreenTop;
        double maxL = minL + SystemParameters.VirtualScreenWidth - w;
        double maxT = minT + SystemParameters.VirtualScreenHeight - h;
        Left = Math.Max(minL, Math.Min(maxL, Left));
        Top = Math.Max(minT, Math.Min(maxT, Top));
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
        ClampToScreen();
        RequestSave();
    }

    private void Shrink_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        PanelBorder.Visibility = Visibility.Collapsed;
        BubbleBorder.Visibility = Visibility.Visible;
        RequestSave();
    }

    private void Expand()
    {
        BubbleBorder.Visibility = Visibility.Collapsed;
        PanelBorder.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(ClampToScreen), DispatcherPriority.Loaded);
        RequestSave();
    }

    private void Bubble_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _pressed = true;
        BubbleBorder.CaptureMouse();
    }

    private void Bubble_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(p.Y - _pressPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _pressed = false;
            BubbleBorder.ReleaseMouseCapture();
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
            ClampToScreen();
            RequestSave();
        }
    }

    private void Bubble_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        BubbleBorder.ReleaseMouseCapture();
        Expand();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ---------- settings ----------

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Avoid instantly re-opening when the click that closed the popup lands on this button.
        if ((DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 200) return;
        SettingsPopup.IsOpen = true;
    }

    private void SettingsPopup_Closed(object sender, EventArgs e)
    {
        _popupClosedAt = DateTime.UtcNow;
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        _data.Topmost = TopmostCheck.IsChecked == true;
        Topmost = _data.Topmost;
        RequestSave();
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        _data.StartWithWindows = StartupCheck.IsChecked == true;
        SetAutostart(_data.StartWithWindows);
        RequestSave();
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        Opacity = e.NewValue / 100.0;
        _data.Opacity = Opacity;
        RequestSave();
    }

    // ---------- adding / deleting ----------

    private void NewItemBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Hint == null) return;
        Hint.Visibility = string.IsNullOrEmpty(NewItemBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NewItemBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddItem();
            e.Handled = true;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        AddItem();
        NewItemBox.Focus();
    }

    private void AddItem()
    {
        var text = NewItemBox.Text.Trim();
        if (text.Length == 0) return;
        _items.Add(new TodoItem { Text = text });
        NewItemBox.Clear();

        // After layout: keep the window on screen and glide down to the new item.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ClampToScreen();
            SmoothScrollTo(Scroller.ScrollableHeight);
        }), DispatcherPriority.Loaded);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TodoItem item)
            _items.Remove(item);
    }

    private void UpdateEmpty()
    {
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- smooth scrolling ----------

    private static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.Register(
            "AnimatedOffset", typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, OnAnimatedOffsetChanged));

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MainWindow)d).Scroller.ScrollToVerticalOffset((double)e.NewValue);
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Scroller.ScrollableHeight <= 0) return;
        // If a glide is already running, add to its destination so fast wheel spins accumulate.
        double start = _scrollAnimating ? _scrollTarget : Scroller.VerticalOffset;
        SmoothScrollTo(start - e.Delta * 0.7);
    }

    private void SmoothScrollTo(double target)
    {
        target = Math.Max(0, Math.Min(Scroller.ScrollableHeight, target));
        _scrollTarget = target;
        int id = ++_scrollAnimId;
        _scrollAnimating = true;

        var anim = new DoubleAnimation(Scroller.VerticalOffset, target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (s, e) =>
        {
            if (id == _scrollAnimId) _scrollAnimating = false;
        };
        BeginAnimation(AnimatedOffsetProperty, anim);
    }
}
