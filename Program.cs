using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FileMover;

static class Program
{
    [STAThread]
    static void Main()
    {
        var app = new Application();
        app.Run(new MainWindow());
    }
}

// ═══════════════════════════ 配置（保存在程序所在目录的 settings.json） ═══════════════════════════

sealed class AppSettings
{
    public string OutputRoot { get; set; } = "";
    public double Width { get; set; }
    public double Height { get; set; }

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options)); }
        catch { }
    }
}

// ═══════════════════════════ 列表项 ═══════════════════════════

enum ItemState { Pending, Moving, Done, Skipped, Failed }

sealed class FileItem : INotifyPropertyChanged
{
    static readonly Brush BPending = Make("#9B9BAA");
    static readonly Brush BMoving = Make("#4C8DFF");
    static readonly Brush BDone = Make("#4CD18C");
    static readonly Brush BSkip = Make("#E6B84C");
    static readonly Brush BFail = Make("#FF6B6B");

    static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg", ".rmvb", ".3gp"
    };

    static Brush Make(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public FileItem(string path, bool isFolder)
    {
        FullPath = path;
        IsFolder = isFolder;
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        Name = string.IsNullOrEmpty(name) ? path : name;
        Detail = isFolder ? "文件夹" : Describe(path);
        Glyph = isFolder ? "📁" : (VideoExt.Contains(Path.GetExtension(path)) ? "🎬" : "📄");
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsFolder { get; }
    public string Detail { get; }
    public string Glyph { get; }

    ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; Raise(); }
    }

    public ItemState State { get; private set; } = ItemState.Pending;
    public string Message { get; private set; } = "";

    public Brush StatusBrush => State switch
    {
        ItemState.Moving => BMoving,
        ItemState.Done => BDone,
        ItemState.Skipped => BSkip,
        ItemState.Failed => BFail,
        _ => BPending
    };

    public void SetState(ItemState state, string message = "")
    {
        State = state;
        Message = message;
        Raise(nameof(State));
        Raise(nameof(Message));
        Raise(nameof(StatusBrush));
    }

    static string Describe(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var ext = fi.Extension.TrimStart('.').ToUpperInvariant();
            return ext.Length > 0 ? $"{FormatSize(fi.Length)} · {ext}" : FormatSize(fi.Length);
        }
        catch { return "文件"; }
    }

    static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ═══════════════════════════ Shell 缩略图（共用资源管理器的本地缩略图缓存） ═══════════════════════════

static class ShellThumbnail
{
    const int ThumbSize = 256;

    static readonly BlockingCollection<(string Path, Action<BitmapSource?> Done)> Jobs = new();

    static ShellThumbnail()
    {
        for (int i = 0; i < 3; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = "ShellThumb" + i };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    public static void Request(string path, Action<BitmapSource?> done) => Jobs.Add((path, done));

    static void Worker()
    {
        foreach (var (path, done) in Jobs.GetConsumingEnumerable())
        {
            BitmapSource? bmp = null;
            try { bmp = Get(path); } catch { }
            try { done(bmp); } catch { }
        }
    }

    // 顺序：缓存命中 → 让 Shell 生成缩略图 → 回退到 Shell 图标（文件夹通常走这里）
    static BitmapSource? Get(string path)
    {
        var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        try
        {
            var attempts = new[]
            {
                SIIGBF.ThumbnailOnly | SIIGBF.InCacheOnly | SIIGBF.BiggerSizeOk,
                SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk,
                SIIGBF.BiggerSizeOk
            };
            foreach (var flags in attempts)
            {
                if (factory.GetImage(new SIZE { cx = ThumbSize, cy = ThumbSize }, flags, out var hbmp) == 0 && hbmp != IntPtr.Zero)
                {
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHBitmap(
                            hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                    finally { DeleteObject(hbmp); }
                }
            }
        }
        finally { Marshal.ReleaseComObject(factory); }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx; public int cy; }

    [Flags]
    enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage([In, MarshalAs(UnmanagedType.Struct)] SIZE size, [In] SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);
}

// ═══════════════════════════ 移动逻辑 ═══════════════════════════

sealed class SkipException : Exception
{
    public SkipException(string message) : base(message) { }
}

static class Mover
{
    /// <summary>移动到 destDir 下，重名自动加 (1)、(2)…，返回最终路径。</summary>
    public static string Move(string src, bool isFolder, string destDir)
    {
        var srcFull = Path.GetFullPath(src).TrimEnd('\\', '/');
        var destFull = Path.GetFullPath(destDir).TrimEnd('\\', '/');
        var name = Path.GetFileName(srcFull);
        if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("不能移动磁盘根目录");

        var parent = (Path.GetDirectoryName(srcFull) ?? "").TrimEnd('\\', '/');
        if (string.Equals(parent, destFull, StringComparison.OrdinalIgnoreCase))
            throw new SkipException("已在目标文件夹中");

        if (isFolder &&
            (string.Equals(srcFull, destFull, StringComparison.OrdinalIgnoreCase) ||
             destFull.StartsWith(srcFull + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("不能把文件夹移动到它自己的内部");

        if (!isFolder && !File.Exists(srcFull)) throw new FileNotFoundException("源文件不存在");
        if (isFolder && !Directory.Exists(srcFull)) throw new DirectoryNotFoundException("源文件夹不存在");

        Directory.CreateDirectory(destFull);
        var target = Unique(Path.Combine(destFull, name), isFolder);

        if (!isFolder)
        {
            File.Move(srcFull, target); // 跨磁盘时 .NET 自动 复制+删除
        }
        else if (string.Equals(Path.GetPathRoot(srcFull), Path.GetPathRoot(destFull), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(srcFull, target); // 同盘：直接重命名，瞬间完成
        }
        else
        {
            // 跨盘：Directory.Move 不支持，改为 复制 → 成功后删除原文件夹
            try { CopyDirectory(srcFull, target); }
            catch
            {
                try { Directory.Delete(target, true); } catch { }
                throw;
            }
            Directory.Delete(srcFull, true);
        }
        return target;
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), false);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    static string Unique(string path, bool isDir)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}

// ═══════════════════════════ 主窗口（界面 XAML 内嵌，启动时解析） ═══════════════════════════

sealed class MainWindow : Window
{
    readonly ObservableCollection<FileItem> _items = new();
    readonly AppSettings _settings = AppSettings.Load();
    readonly FrameworkElement _root;
    bool _busy;

    readonly Border InputCard, DropZone;
    readonly TextBox OutputRootBox;
    readonly TextBlock StatusText;
    readonly ListBox FileList;
    readonly StackPanel EmptyHint, LeftButtons;
    readonly Button BrowseBtn, AddFilesBtn, AddFoldersBtn, RemoveBtn, ClearBtn, OpenBtn, StartBtn;
    readonly ProgressBar Progress;

    T F<T>(string name) where T : class => (T)_root.FindName(name);

    public MainWindow()
    {
        Title = "文件移动工具";
        Width = 1000; Height = 720; MinWidth = 780; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF6));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        FontSize = 14;
        AllowDrop = true;

        _root = (FrameworkElement)XamlReader.Parse(Xaml);
        Content = _root;

        InputCard = F<Border>("InputCard"); DropZone = F<Border>("DropZone");
        OutputRootBox = F<TextBox>("OutputRootBox");
        StatusText = F<TextBlock>("StatusText");
        FileList = F<ListBox>("FileList");
        EmptyHint = F<StackPanel>("EmptyHint"); LeftButtons = F<StackPanel>("LeftButtons");
        BrowseBtn = F<Button>("BrowseBtn");
        AddFilesBtn = F<Button>("AddFilesBtn"); AddFoldersBtn = F<Button>("AddFoldersBtn");
        RemoveBtn = F<Button>("RemoveBtn"); ClearBtn = F<Button>("ClearBtn");
        OpenBtn = F<Button>("OpenBtn"); StartBtn = F<Button>("StartBtn");
        Progress = F<ProgressBar>("Progress");

        // 事件
        DragEnter += Window_DragOver;
        DragOver += Window_DragOver;
        DragLeave += (_, _) => ResetDropBorder();
        Drop += Window_Drop;
        Closing += (_, _) => SaveSettings();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();

        BrowseBtn.Click += BrowseOutput_Click;
        AddFilesBtn.Click += AddFiles_Click;
        AddFoldersBtn.Click += AddFolders_Click;
        RemoveBtn.Click += (_, _) => RemoveSelected();
        ClearBtn.Click += (_, _) => { if (!_busy) _items.Clear(); };
        OpenBtn.Click += OpenOutput_Click;
        StartBtn.Click += Start_Click;
        FileList.PreviewKeyDown += FileList_KeyDown;
        FileList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnTileButton));

        FileList.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => RefreshState();

        LoadWindowIcon();
        OutputRootBox.Text = _settings.OutputRoot;
        if (_settings.Width >= MinWidth && _settings.Height >= MinHeight)
        {
            Width = _settings.Width;
            Height = _settings.Height;
        }
        RefreshState();
    }

    // ───────────── 窗口外观 ─────────────

    /// <summary>从程序目录读取 app.ico 作为窗口/任务栏图标；换图标只需替换这个文件。</summary>
    void LoadWindowIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path))
                Icon = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch { }
    }

    void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
            int caption = 0x00181414; // #141418 (BGR)
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    Brush Res(string key) => (Brush)_root.FindResource(key);

    // ───────────── 拖放 ─────────────

    void Window_DragOver(object sender, DragEventArgs e)
    {
        bool ok = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.BorderBrush = Res(ok ? "AccentBrush" : "LineBrush");
        e.Handled = true;
    }

    void ResetDropBorder() => DropZone.BorderBrush = Res("LineBrush");

    void Window_Drop(object sender, DragEventArgs e)
    {
        ResetDropBorder();
        if (_busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
        e.Handled = true;
    }

    // ───────────── 添加 / 移除 ─────────────

    void AddPaths(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(_items.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            string full;
            try { full = Path.GetFullPath(raw); } catch { continue; }

            bool isDir = Directory.Exists(full);
            if (!isDir && !File.Exists(full)) continue;
            if (!existing.Add(full)) continue;

            var item = new FileItem(full, isDir);
            _items.Add(item);
            ShellThumbnail.Request(full, bmp =>
            {
                if (bmp != null)
                    Dispatcher.BeginInvoke(new Action(() => item.Thumbnail = bmp));
            });
        }
    }

    void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择视频或文件",
            Multiselect = true,
            Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.mpg;*.mpeg;*.rmvb|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    void AddFolders_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要移动的文件夹", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FolderNames);
    }

    void OnTileButton(object sender, RoutedEventArgs e)
    {
        if (!_busy && e.OriginalSource is Button b && b.Tag as string == "remove" && b.DataContext is FileItem item)
            _items.Remove(item);
    }

    void RemoveSelected()
    {
        if (_busy) return;
        foreach (var item in FileList.SelectedItems.Cast<FileItem>().ToList()) _items.Remove(item);
    }

    void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { FileList.SelectAll(); e.Handled = true; }
    }

    // ───────────── 输出位置 ─────────────

    void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择输出文件夹" };
        var cur = OutputRootBox.Text.Trim().Trim('"');
        if (Directory.Exists(cur)) dlg.InitialDirectory = cur;
        if (dlg.ShowDialog(this) == true) OutputRootBox.Text = dlg.FolderName;
    }

    string? GetDestination(out string error)
    {
        error = "";
        var root = OutputRootBox.Text.Trim().Trim('"');
        if (root.Length == 0) { error = "请先设置输出位置"; return null; }
        try
        {
            return Path.GetFullPath(root);
        }
        catch { error = "输出位置不是有效路径"; return null; }
    }

    // ───────────── 移动 ─────────────

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        var dest = GetDestination(out var err);
        if (dest == null)
        {
            MessageBox.Show(this, err, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pending = _items.Where(i => i.State is ItemState.Pending or ItemState.Failed).ToList();
        if (pending.Count == 0) return;

        SetBusy(true);
        Progress.Value = 0;
        int ok = 0, skipped = 0, failed = 0, done = 0;

        foreach (var item in pending)
        {
            item.SetState(ItemState.Moving, "移动中…");
            StatusText.Text = $"正在移动 {done + 1}/{pending.Count}：{item.Name}";
            try
            {
                var src = item.FullPath;
                var isFolder = item.IsFolder;
                await Task.Run(() => Mover.Move(src, isFolder, dest));
                item.SetState(ItemState.Done, "✓ 已移动");
                ok++;
            }
            catch (SkipException ex)
            {
                item.SetState(ItemState.Skipped, ex.Message);
                skipped++;
            }
            catch (Exception ex)
            {
                item.SetState(ItemState.Failed, "✗ " + ex.Message);
                failed++;
            }
            done++;
            Progress.Value = done * 100.0 / pending.Count;
        }

        SaveSettings();
        SetBusy(false);

        StatusText.Text = $"完成：成功 {ok} 项" +
                          (skipped > 0 ? $"，跳过 {skipped} 项" : "") +
                          (failed > 0 ? $"，失败 {failed} 项（可再次点击开始重试）" : "");
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        InputCard.IsEnabled = !busy;
        LeftButtons.IsEnabled = !busy;
        FileList.IsHitTestVisible = !busy;
        StartBtn.IsEnabled = !busy && PendingCount() > 0;
    }

    int PendingCount() => _items.Count(i => i.State is ItemState.Pending or ItemState.Failed);

    void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var dest = GetDestination(out var err);
        if (dest == null)
        {
            MessageBox.Show(this, err, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dir = dest;
        while (!Directory.Exists(dir))
        {
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent)) return;
            dir = parent;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ───────────── 状态 / 配置 ─────────────

    void RefreshState()
    {
        if (EmptyHint == null) return;
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int pending = PendingCount();
        RemoveBtn.IsEnabled = ClearBtn.IsEnabled = _items.Count > 0;
        StartBtn.IsEnabled = !_busy && pending > 0;
        if (!_busy)
            StatusText.Text = _items.Count == 0 ? "尚未添加任何项目" : $"共 {_items.Count} 项，待移动 {pending} 项";
    }

    void SaveSettings()
    {
        _settings.OutputRoot = OutputRootBox.Text.Trim().Trim('"');
        if (WindowState == WindowState.Normal)
        {
            _settings.Width = ActualWidth;
            _settings.Height = ActualHeight;
        }
        _settings.Save();
    }

    // ───────────── 界面 XAML ─────────────

    const string Xaml = """
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Margin="22">
  <Grid.Resources>

    <SolidColorBrush x:Key="BgBrush" Color="#141418"/>
    <SolidColorBrush x:Key="CardBrush" Color="#1E1E25"/>
    <SolidColorBrush x:Key="Card2Brush" Color="#2A2A33"/>
    <SolidColorBrush x:Key="InputBrush" Color="#17171D"/>
    <SolidColorBrush x:Key="LineBrush" Color="#34343F"/>
    <SolidColorBrush x:Key="TextBrush" Color="#F2F2F6"/>
    <SolidColorBrush x:Key="SubBrush" Color="#9B9BAA"/>
    <SolidColorBrush x:Key="AccentBrush" Color="#4C8DFF"/>

    <Style TargetType="Button">
      <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
      <Setter Property="Background" Value="{StaticResource Card2Brush}"/>
      <Setter Property="BorderBrush" Value="{StaticResource LineBrush}"/>
      <Setter Property="Padding" Value="16,9"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Grid>
              <Border x:Name="bd" Background="{TemplateBinding Background}"
                      BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"
                      CornerRadius="8" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <Border x:Name="hl" Background="#18FFFFFF" CornerRadius="8" Opacity="0" IsHitTestVisible="False"/>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="hl" Property="Opacity" Value="1"/>
              </Trigger>
              <Trigger Property="IsPressed" Value="True">
                <Setter TargetName="bd" Property="Opacity" Value="0.8"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Opacity" Value="0.4"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="AccentButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
      <Setter Property="Background" Value="{StaticResource AccentBrush}"/>
      <Setter Property="BorderBrush" Value="Transparent"/>
      <Setter Property="Foreground" Value="White"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>

    <Style x:Key="IconButton" TargetType="Button">
      <Setter Property="Foreground" Value="White"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="FontSize" Value="11"/>
      <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="bd" Background="#B0000000" CornerRadius="11">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#E0FF5C5C"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType="TextBox">
      <Setter Property="Background" Value="{StaticResource InputBrush}"/>
      <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
      <Setter Property="CaretBrush" Value="{StaticResource TextBrush}"/>
      <Setter Property="SelectionBrush" Value="{StaticResource AccentBrush}"/>
      <Setter Property="Padding" Value="10,9"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="TextBox">
            <Border x:Name="bd" Background="{TemplateBinding Background}"
                    BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="8">
              <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsKeyboardFocused" Value="True">
                <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Opacity" Value="0.5"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType="ScrollBar">
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="Width" Value="12"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ScrollBar">
            <Grid Background="Transparent">
              <Track x:Name="PART_Track" IsDirectionReversed="True">
                <Track.Thumb>
                  <Thumb>
                    <Thumb.Template>
                      <ControlTemplate TargetType="Thumb">
                        <Border Background="#55FFFFFF" CornerRadius="4" Margin="3,2"/>
                      </ControlTemplate>
                    </Thumb.Template>
                  </Thumb>
                </Track.Thumb>
              </Track>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property="Orientation" Value="Horizontal">
                <Setter Property="Width" Value="Auto"/>
                <Setter Property="Height" Value="12"/>
                <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="TileItem" TargetType="ListBoxItem">
      <Setter Property="Margin" Value="6"/>
      <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ListBoxItem">
            <Border x:Name="bd" Background="{StaticResource BgBrush}" BorderBrush="Transparent"
                    BorderThickness="2" CornerRadius="12" Padding="8">
              <ContentPresenter/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="bd" Property="Background" Value="{StaticResource Card2Brush}"/>
              </Trigger>
              <Trigger Property="IsSelected" Value="True">
                <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
                <Setter TargetName="bd" Property="Background" Value="{StaticResource Card2Brush}"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <DataTemplate x:Key="TileTemplate">
      <Grid x:Name="root" Width="148" Background="Transparent" ToolTip="{Binding FullPath}">
        <Grid.RowDefinitions>
          <RowDefinition Height="104"/>
          <RowDefinition Height="Auto"/>
          <RowDefinition Height="Auto"/>
          <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <Border CornerRadius="8" Background="#15151B" ClipToBounds="True">
          <Grid>
            <TextBlock Text="{Binding Glyph}" FontSize="40" Opacity="0.6"
                       HorizontalAlignment="Center" VerticalAlignment="Center">
              <TextBlock.Style>
                <Style TargetType="TextBlock">
                  <Setter Property="Visibility" Value="Collapsed"/>
                  <Style.Triggers>
                    <DataTrigger Binding="{Binding Thumbnail}" Value="{x:Null}">
                      <Setter Property="Visibility" Value="Visible"/>
                    </DataTrigger>
                  </Style.Triggers>
                </Style>
              </TextBlock.Style>
            </TextBlock>
            <Image Source="{Binding Thumbnail}" Margin="6" Stretch="Uniform"
                   RenderOptions.BitmapScalingMode="HighQuality"/>
          </Grid>
        </Border>

        <TextBlock Grid.Row="1" Text="{Binding Name}" Margin="2,8,2,0" FontSize="13" FontWeight="SemiBold"
                   TextWrapping="Wrap" TextTrimming="CharacterEllipsis" MaxHeight="40"/>
        <TextBlock Grid.Row="2" Text="{Binding Detail}" Margin="2,2,2,0" FontSize="11"
                   Foreground="{StaticResource SubBrush}"/>
        <TextBlock Grid.Row="3" Text="{Binding Message}" Margin="2,2,2,0" FontSize="11"
                   Foreground="{Binding StatusBrush}" TextTrimming="CharacterEllipsis"/>

        <Button x:Name="rm" Tag="remove" Style="{StaticResource IconButton}" Content="✕" Width="22" Height="22"
                HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,6,6,0"
                Visibility="Collapsed" ToolTip="从列表移除"/>
      </Grid>
      <DataTemplate.Triggers>
        <Trigger SourceName="root" Property="IsMouseOver" Value="True">
          <Setter TargetName="rm" Property="Visibility" Value="Visible"/>
        </Trigger>
      </DataTemplate.Triggers>
    </DataTemplate>

  </Grid.Resources>

  <Grid.RowDefinitions>
    <RowDefinition Height="Auto"/>
    <RowDefinition Height="*"/>
    <RowDefinition Height="Auto"/>
    <RowDefinition Height="Auto"/>
  </Grid.RowDefinitions>

  <!-- 0: 标题 -->
  <StackPanel Grid.Row="0" Margin="0,0,0,14">
    <TextBlock Text="文件移动工具" FontSize="26" FontWeight="SemiBold"/>
    <TextBlock Text="拖入视频或文件夹，选好输出位置后一键移动" Margin="0,4,0,0"
               Foreground="{StaticResource SubBrush}"/>
  </StackPanel>

  <!-- 1: 拖放区 -->
  <Border x:Name="DropZone" Grid.Row="1" Background="{StaticResource CardBrush}" CornerRadius="14"
          BorderThickness="2" BorderBrush="{StaticResource LineBrush}">
    <Grid>
      <ListBox x:Name="FileList" Background="Transparent" BorderThickness="0" Padding="10"
               SelectionMode="Extended"
               ScrollViewer.HorizontalScrollBarVisibility="Disabled"
               ItemTemplate="{StaticResource TileTemplate}"
               ItemContainerStyle="{StaticResource TileItem}">
        <ListBox.ItemsPanel>
          <ItemsPanelTemplate>
            <WrapPanel/>
          </ItemsPanelTemplate>
        </ListBox.ItemsPanel>
      </ListBox>

      <StackPanel x:Name="EmptyHint" HorizontalAlignment="Center" VerticalAlignment="Center"
                  IsHitTestVisible="False">
        <TextBlock Text="📥" FontSize="54" HorizontalAlignment="Center" Opacity="0.7"/>
        <TextBlock Text="把视频或文件夹拖到这里" FontSize="18" FontWeight="SemiBold"
                   HorizontalAlignment="Center" Margin="0,10,0,0"/>
        <TextBlock Text="也可以点击下方按钮选择" Foreground="{StaticResource SubBrush}"
                   HorizontalAlignment="Center" Margin="0,6,0,0"/>
      </StackPanel>
    </Grid>
  </Border>

  <!-- 2: 操作栏 -->
  <Grid Grid.Row="2" Margin="0,14,0,14">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="Auto"/>
      <ColumnDefinition Width="*"/>
      <ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>

    <StackPanel x:Name="LeftButtons" Orientation="Horizontal">
      <Button x:Name="AddFilesBtn" Content="＋ 添加视频/文件" Margin="0,0,8,0"/>
      <Button x:Name="AddFoldersBtn" Content="＋ 添加文件夹" Margin="0,0,8,0"/>
      <Button x:Name="RemoveBtn" Content="移除所选" Margin="0,0,8,0"/>
      <Button x:Name="ClearBtn" Content="清空"/>
    </StackPanel>

    <StackPanel Grid.Column="1" Margin="20,0" VerticalAlignment="Center">
      <TextBlock x:Name="StatusText" Foreground="{StaticResource SubBrush}" TextTrimming="CharacterEllipsis"/>
      <ProgressBar x:Name="Progress" Height="4" Margin="0,6,0,0" Minimum="0" Maximum="100"
                   Foreground="{StaticResource AccentBrush}" Background="{StaticResource LineBrush}"
                   BorderThickness="0"/>
    </StackPanel>

    <StackPanel Grid.Column="2" Orientation="Horizontal">
      <Button x:Name="OpenBtn" Content="打开输出文件夹" Margin="0,0,8,0"/>
      <Button x:Name="StartBtn" Content="开始移动" Style="{StaticResource AccentButton}" Padding="30,9"/>
    </StackPanel>
  </Grid>

  <!-- 3: 输出位置卡片 -->
  <Border x:Name="InputCard" Grid.Row="3" Background="{StaticResource CardBrush}"
          CornerRadius="14" Padding="18">
    <Grid>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="80"/>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
      </Grid.ColumnDefinitions>

      <TextBlock Grid.Row="0" Text="输出位置" VerticalAlignment="Center"/>
      <TextBox x:Name="OutputRootBox" Grid.Row="0" Grid.Column="1" Margin="0,0,10,0"/>
      <Button x:Name="BrowseBtn" Grid.Row="0" Grid.Column="2" Content="浏览…"/>
    </Grid>
  </Border>
</Grid>
""";
}