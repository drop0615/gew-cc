using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GewCoordinator;

public partial class MainWindow : Window
{
    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GewCoordinator", "root.txt");

    private const string NoneLabel = "(なし)";

    /// <summary>ポーズ名。0 は歩行と確認済み。1・6・7 は画像からの推定、2〜5 は素手の動作で用途は未確認。</summary>
    private static readonly Dictionary<int, string> PoseNames = new()
    {
        [0] = "歩行 (コマ1が立ち姿)",
        [1] = "攻撃",
        [2] = "動作A (素手)",
        [3] = "動作B (素手・腕を伸ばす)",
        [4] = "動作C (素手・押し出す)",
        [5] = "動作D (素手・腕を上げる)",
        [6] = "防御",
        [7] = "座り",
    };

    private sealed class PartRow
    {
        public required PartKind Kind { get; init; }
        public required CheckBox Visible { get; init; }
        /// <summary>装備の種類(ID 上位 3 桁)。</summary>
        public required ComboBox Types { get; init; }
        /// <summary>カラー(ID 下位 2 桁)。</summary>
        public required ComboBox Colors { get; init; }
        public CheckBox? Behind { get; init; }
    }

    private sealed class PartPreset
    {
        public int? Id { get; set; }
        public bool Visible { get; set; } = true;
        public bool Behind { get; set; }
    }

    private sealed class Preset
    {
        public Dictionary<string, PartPreset> Parts { get; set; } = new();
        public int Pose { get; set; }
        public int Direction { get; set; }
        public int Anim { get; set; }
        public bool Mirror { get; set; }
        /// <summary>0 = 男性、1 = 女性、2 = すべて。古い保存データにはないので「すべて」を既定にする。</summary>
        public int Sex { get; set; } = 2;
        /// <summary>前後の自動調整。古い保存データでは手動指定を保つため false。</summary>
        public bool AutoOrder { get; set; }
    }

    private readonly Dictionary<char, PartRow> _rows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(130) };
    private PartLibrary? _library;
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        BuildPartRows();
        _timer.Tick += (_, _) => AdvanceFrame();
        ZoomLabel.Text = $"x{(int)ZoomSlider.Value}";

        string? last = null;
        try { if (File.Exists(SettingsFile)) last = File.ReadAllText(SettingsFile).Trim(); } catch { }
        RootBox.Text = !string.IsNullOrEmpty(last) ? last : @"C:\Vector\GODIUS";
        LoadLibrary(RootBox.Text);
    }

    private void BuildPartRows()
    {
        foreach (var kind in PartLibrary.Kinds.Reverse())   // 手前のパーツを上に並べる
        {
            var check = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var label = new TextBlock { Text = kind.Label, Width = 36, VerticalAlignment = VerticalAlignment.Center };
            var types = new ComboBox { Width = 82, IsEditable = true, IsTextSearchEnabled = true, ToolTip = "装備の種類 (ID 上位3桁。直接入力できます)" };
            var colors = new ComboBox { Margin = new Thickness(4, 0, 0, 0), ToolTip = "カラーバリエーション (ID 下位2桁)" };
            var prev = new Button { Content = "◀", Width = 24, Margin = new Thickness(6, 0, 2, 0) };
            var next = new Button { Content = "▶", Width = 24 };
            CheckBox? behind = kind.Prefix is 'W' or 'S'
                ? new CheckBox { Content = "奥", ToolTip = "体より奥に描画する", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }
                : null;

            PartRow? row = null;
            prev.Click += (_, _) => Step(types, -1);
            next.Click += (_, _) => Step(types, +1);
            check.Click += (_, _) => Refresh();
            if (behind is not null) behind.Click += (_, _) => Refresh();
            types.SelectionChanged += (_, _) =>
            {
                if (_loading || row is null) return;
                UpdateColors(row, SelectedColor(row));
                Refresh();
            };
            colors.SelectionChanged += (_, _) => { if (!_loading) Refresh(); };

            var panel = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            DockPanel.SetDock(check, Dock.Left);
            DockPanel.SetDock(label, Dock.Left);
            DockPanel.SetDock(types, Dock.Left);
            if (behind is not null) { DockPanel.SetDock(behind, Dock.Right); panel.Children.Add(behind); }
            DockPanel.SetDock(next, Dock.Right);
            DockPanel.SetDock(prev, Dock.Right);
            panel.Children.Add(check);
            panel.Children.Add(label);
            panel.Children.Add(types);
            panel.Children.Add(next);
            panel.Children.Add(prev);
            panel.Children.Add(colors);
            PartsPanel.Children.Add(panel);

            row = new PartRow { Kind = kind, Visible = check, Types = types, Colors = colors, Behind = behind };
            _rows[kind.Prefix] = row;
        }
    }

    private static void Step(ComboBox combo, int delta)
    {
        if (combo.Items.Count == 0) return;
        combo.SelectedIndex = (combo.SelectedIndex + delta + combo.Items.Count) % combo.Items.Count;
    }

    /// <summary>現在選択中のカラー番号。未選択なら null。</summary>
    private static int? SelectedColor(PartRow row) =>
        row.Colors.SelectedItem is string s && s.Length >= 2 && int.TryParse(s.AsSpan(0, 2), out var c) ? c : null;

    /// <summary>選択中の種類で選べるカラーを並べ直す。同じカラー番号があれば維持する。</summary>
    private void UpdateColors(PartRow row, int? preferred)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            row.Colors.Items.Clear();
            if (_library is null || row.Types.SelectedItem is not string t || !int.TryParse(t, out var type))
            {
                row.Colors.IsEnabled = false;
                return;
            }
            var colors = _library.Colors(row.Kind.Prefix, type);
            foreach (var c in colors) row.Colors.Items.Add(PartLibrary.ColorName(row.Kind.Prefix, c));
            row.Colors.IsEnabled = colors.Count > 1;
            int index = preferred is int p ? colors.ToList().IndexOf(p) : -1;
            row.Colors.SelectedIndex = index >= 0 ? index : 0;
        }
        finally { _loading = was; }
    }

    /// <summary>
    /// キャラクターの性別。体 → 頭 → 脚 → 性別欄の順に、種類番号の先頭桁 (0 = 男性、1 = 女性) から判断する。
    /// </summary>
    private int CharacterSex(IReadOnlyDictionary<char, int?> sel)
    {
        foreach (var prefix in new[] { 'B', 'H', 'L' })
            if (sel.TryGetValue(prefix, out var id) && id is int v) return v / 10000;
        return Sex < 2 ? Sex : 0;
    }

    private void AutoOrder_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>自動調整が有効なら、性別に応じて「奥」チェックを設定し、手動操作を無効にする。</summary>
    private void SyncOrderUi(IReadOnlyDictionary<char, int?> sel)
    {
        bool auto = AutoOrderBox.IsChecked == true;
        bool female = CharacterSex(sel) == 1;
        foreach (var row in _rows.Values)
        {
            if (row.Behind is null) continue;
            row.Behind.IsEnabled = !auto;
            if (auto) row.Behind.IsChecked = row.Kind.Prefix == 'S' ? female : !female;   // 男性: 武器が奥 / 女性: 盾が奥
        }
    }

    /// <summary>性別の選択 (0 = 男性、1 = 女性、2 = すべて)。種類番号の先頭桁が 0 / 1 に対応する。</summary>
    private int Sex => Math.Max(SexBox.SelectedIndex, 0);

    private bool MatchesSex(int id) => Sex >= 2 || id / 10000 == Sex;

    private void PopulateTypes(PartRow row)
    {
        row.Types.Items.Clear();
        row.Types.Items.Add(NoneLabel);
        if (_library is null) return;
        foreach (var type in _library.Types(row.Kind.Prefix))
            if (MatchesSex(type * 100)) row.Types.Items.Add(type.ToString("D3"));
    }

    private void Sex_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _library is null) return;
        _loading = true;
        try
        {
            foreach (var row in _rows.Values)
            {
                int? id = SelectedId(row);
                PopulateTypes(row);
                // 同じ装備の別性別版 (種類の先頭桁だけ入れ替えた ID) があればそれに付け替える
                int? mapped = id is int v ? (Sex < 2 ? Sex * 10000 + v % 10000 : v) : null;
                bool exists = mapped is int m && row.Types.Items.Contains((m / 100).ToString("D3"));
                if (exists) SetSelection(row, mapped);
                else if (id is null || row.Kind.Prefix is 'W' or 'S' || row.Types.Items.Count <= 1) SetSelection(row, null);
                else { row.Types.SelectedIndex = 1; UpdateColors(row, 0); }
            }
        }
        finally { _loading = false; }
        Refresh();
    }

    /// <summary>種類とカラーの組(ID)をまとめて選択する。null は「なし」。</summary>
    private void SetSelection(PartRow row, int? id)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            int index = id is int v ? row.Types.Items.IndexOf((v / 100).ToString("D3")) : 0;
            row.Types.SelectedIndex = Math.Max(index, 0);
            UpdateColors(row, id is int w ? w % 100 : null);
        }
        finally { _loading = was; }
    }

    // ---- loading -----------------------------------------------------------------------------

    private void LoadLibrary(string root)
    {
        try
        {
            _library = new PartLibrary(root);
        }
        catch (Exception ex)
        {
            _library = null;
            StatusText.Text = ex.Message;
            Preview.Source = null;
            InfoText.Text = "";
            return;
        }

        _loading = true;
        try
        {
            foreach (var row in _rows.Values)
            {
                PopulateTypes(row);
                row.Types.SelectedIndex = row.Types.Items.Count > 1 ? 1 : 0;
                UpdateColors(row, 0);
            }

            PoseBox.Items.Clear();
            foreach (var pose in _library.AllPoses())
                PoseBox.Items.Add(new ComboBoxItem
                {
                    Content = PoseNames.TryGetValue(pose, out var name) ? $"{pose}: {name}" : $"{pose}",
                    Tag = pose,
                });
            PoseBox.SelectedIndex = PoseBox.Items.Count > 0 ? 0 : -1;
        }
        finally { _loading = false; }

        StatusText.Text = string.Join(" / ", PartLibrary.Kinds.Select(k => $"{k.Label} {_library.Items(k.Prefix).Count}"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, root);
        }
        catch { }
        Refresh();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Char と Data を含むクライアントフォルダを選択", InitialDirectory = RootBox.Text };
        if (dlg.ShowDialog(this) == true)
        {
            RootBox.Text = dlg.FolderName;
            LoadLibrary(dlg.FolderName);
        }
    }

    private void RootBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LoadLibrary(RootBox.Text.Trim());
    }

    // ---- state -------------------------------------------------------------------------------

    /// <summary>種類 × 100 + カラー = パーツ ID (ファイル名の 5 桁)。「なし」なら null。</summary>
    private static int? SelectedId(PartRow row) =>
        row.Types.SelectedItem is string t && int.TryParse(t, out var type) && SelectedColor(row) is int color
            ? type * 100 + color : null;

    private Dictionary<char, int?> Selection()
    {
        var sel = new Dictionary<char, int?>();
        foreach (var (prefix, row) in _rows)
            sel[prefix] = row.Visible.IsChecked == true ? SelectedId(row) : null;
        return sel;
    }

    private HashSet<char> BehindSet() =>
        _rows.Where(r => r.Value.Behind?.IsChecked == true).Select(r => r.Key).ToHashSet();

    private int CurrentPose => PoseBox.SelectedItem is ComboBoxItem { Tag: int p } ? p : 0;

    /// <summary>フレームは「向き × コマ」。12 枚なら 4 向き × 3 コマ。</summary>
    private static (int PerDirection, int Directions) Layout(int frames)
    {
        frames = Math.Max(frames, 1);
        int per = frames >= 3 && frames % 3 == 0 ? 3 : frames;
        return (per, Math.Max(frames / per, 1));
    }

    private int CurrentFrame()
    {
        var (per, _) = Layout(_library?.FrameCount(Selection(), CurrentPose) ?? 1);
        return Math.Max(DirectionBox.SelectedIndex, 0) * per + (int)FrameSlider.Value;
    }

    private BitmapSource? Render(int frame)
    {
        if (_library is null) return null;
        var pixels = _library.Compose(Selection(), CurrentPose, frame, BehindSet(), MirrorBox.IsChecked == true);
        var bmp = BitmapSource.Create(Sprite.Width, Sprite.Height, 96, 96, PixelFormats.Bgra32, null, pixels, Sprite.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    private void Refresh()
    {
        if (_library is null) return;
        var sel = Selection();
        int pose = CurrentPose;
        var (per, dirs) = Layout(_library.FrameCount(sel, pose));

        _loading = true;
        try
        {
            if (DirectionBox.Items.Count != dirs)
            {
                int keep = Math.Max(DirectionBox.SelectedIndex, 0);
                DirectionBox.Items.Clear();
                for (int i = 0; i < dirs; i++) DirectionBox.Items.Add($"向き {i + 1}");
                DirectionBox.SelectedIndex = Math.Min(keep, dirs - 1);
            }
            FrameSlider.Maximum = per - 1;
            if (FrameSlider.Value > per - 1) FrameSlider.Value = per - 1;
        }
        finally { _loading = false; }

        FrameLabel.Text = $"{(int)FrameSlider.Value + 1}/{per}";
        SyncOrderUi(sel);
        Preview.Source = Render(CurrentFrame());
        ApplyZoom();

        var missing = _library.MissingForPose(sel, pose);
        InfoText.Text = missing.Count == 0
            ? $"ポーズ {pose} / 向き {DirectionBox.SelectedIndex + 1} / コマ {(int)FrameSlider.Value + 1}"
            : $"このポーズには {string.Join("、", missing.Select(k => k.Label))} のデータがありません (表示されません)";
    }

    private void ApplyZoom()
    {
        int zoom = (int)ZoomSlider.Value;
        Preview.Width = Sprite.Width * zoom;
        Preview.Height = Sprite.Height * zoom;
        ZoomLabel.Text = $"x{zoom}";
    }

    /// <summary>ポーズごとのコマ再生順 (0 始まり)。指定がなければ 0,1,2,... の順。</summary>
    private static readonly Dictionary<int, int[]> PlaySequences = new()
    {
        [0] = new[] { 0, 1, 0, 2, 0 },   // 歩行: 1,2,1,3,1 の繰り返し
    };

    private int _sequencePos;

    private void AdvanceFrame()
    {
        int max = (int)FrameSlider.Maximum;
        if (PlaySequences.TryGetValue(CurrentPose, out var seq) && max >= 2)
        {
            _sequencePos = (_sequencePos + 1) % seq.Length;
            FrameSlider.Value = seq[_sequencePos];
        }
        else
        {
            FrameSlider.Value = FrameSlider.Value >= max ? 0 : FrameSlider.Value + 1;
        }
    }

    // ---- control events ----------------------------------------------------------------------

    private void Pose_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) Refresh(); }
    private void Direction_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) Refresh(); }
    private void Mirror_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Frame_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded && !_loading) Refresh(); }
    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) ApplyZoom(); }
    private void Play_Changed(object sender, RoutedEventArgs e)
    {
        _sequencePos = 0;
        _timer.IsEnabled = PlayBox.IsChecked == true;
    }

    private void Background_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Stage.Background = new SolidColorBrush(BackgroundBox.SelectedIndex switch
        {
            1 => Colors.White,
            2 => Color.FromRgb(0, 177, 64),
            _ => Color.FromRgb(0x3C, 0x3C, 0x46),
        });
    }

    private void Random_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null) return;
        var rng = Random.Shared;
        int pose = CurrentPose;
        _loading = true;
        try
        {
            foreach (var row in _rows.Values)
            {
                var ids = _library.Items(row.Kind.Prefix).Where(MatchesSex).ToList();
                if (ids.Count == 0) continue;
                // 武器と盾は半分の確率で「なし」
                if (row.Kind.Prefix is 'W' or 'S' && rng.Next(2) == 0) { SetSelection(row, null); continue; }
                // 現在のポーズにデータがあるパーツを優先して選ぶ
                int pick = ids[rng.Next(ids.Count)];
                for (int tries = 0; tries < 40; tries++)
                {
                    int candidate = ids[rng.Next(ids.Count)];
                    if (_library.Get(row.Kind.Prefix, candidate, pose) is not null) { pick = candidate; break; }
                }
                SetSelection(row, pick);
            }
        }
        finally { _loading = false; }
        Refresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try { foreach (var row in _rows.Values) SetSelection(row, null); }
        finally { _loading = false; }
        Refresh();
    }

    // ---- presets -----------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var preset = new Preset
        {
            Pose = CurrentPose,
            Direction = Math.Max(DirectionBox.SelectedIndex, 0),
            Anim = (int)FrameSlider.Value,
            Mirror = MirrorBox.IsChecked == true,
            Sex = Sex,
            AutoOrder = AutoOrderBox.IsChecked == true,
        };
        foreach (var (prefix, row) in _rows)
        {
            preset.Parts[prefix.ToString()] = new PartPreset
            {
                Id = SelectedId(row),
                Visible = row.Visible.IsChecked == true,
                Behind = row.Behind?.IsChecked == true,
            };
        }
        var dlg = new SaveFileDialog { Filter = "コーディネート (*.json)|*.json", FileName = "coordinate.json" };
        if (dlg.ShowDialog(this) == true)
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(preset, JsonOptions));
    }

    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null) return;
        var dlg = new OpenFileDialog { Filter = "コーディネート (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;

        Preset? preset;
        try { preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(dlg.FileName)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "読み込みに失敗しました", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (preset is null) return;

        _loading = true;
        try
        {
            SexBox.SelectedIndex = Math.Clamp(preset.Sex, 0, 2);
            AutoOrderBox.IsChecked = preset.AutoOrder;
            foreach (var row in _rows.Values) PopulateTypes(row);
            foreach (var (key, part) in preset.Parts)
            {
                if (key.Length != 1 || !_rows.TryGetValue(key[0], out var row)) continue;
                row.Visible.IsChecked = part.Visible;
                if (row.Behind is not null) row.Behind.IsChecked = part.Behind;
                SetSelection(row, part.Id);
            }
            for (int i = 0; i < PoseBox.Items.Count; i++)
                if (PoseBox.Items[i] is ComboBoxItem { Tag: int p } && p == preset.Pose) PoseBox.SelectedIndex = i;
            MirrorBox.IsChecked = preset.Mirror;
        }
        finally { _loading = false; }

        Refresh();
        _loading = true;
        try
        {
            DirectionBox.SelectedIndex = Math.Clamp(preset.Direction, 0, Math.Max(DirectionBox.Items.Count - 1, 0));
            FrameSlider.Value = Math.Clamp(preset.Anim, 0, (int)FrameSlider.Maximum);
        }
        finally { _loading = false; }
        Refresh();
    }

    // ---- export ------------------------------------------------------------------------------

    private static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void SaveFrame_Click(object sender, RoutedEventArgs e)
    {
        var bmp = Render(CurrentFrame());
        if (bmp is null) return;
        var dlg = new SaveFileDialog { Filter = "PNG 画像|*.png", FileName = "character.png" };
        if (dlg.ShowDialog(this) == true) SavePng(bmp, dlg.FileName);
    }

    /// <summary>列 = コマ、行 = 向き のシートを書き出す。</summary>
    private void SaveSheet_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null) return;
        var sel = Selection();
        int frames = Math.Max(_library.FrameCount(sel, CurrentPose), 1);
        var (per, dirs) = Layout(frames);
        var dlg = new SaveFileDialog { Filter = "PNG 画像|*.png", FileName = "character_sheet.png" };
        if (dlg.ShowDialog(this) != true) return;

        int width = Sprite.Width * per, height = Sprite.Height * dirs;
        var sheet = new byte[width * height * 4];
        int stride = width * 4;
        var behind = BehindSet();
        bool mirror = MirrorBox.IsChecked == true;
        for (int d = 0; d < dirs; d++)
        for (int k = 0; k < per; k++)
        {
            var px = _library.Compose(sel, CurrentPose, d * per + k, behind, mirror);
            for (int y = 0; y < Sprite.Height; y++)
                Buffer.BlockCopy(px, y * Sprite.Width * 4, sheet,
                    (d * Sprite.Height + y) * stride + k * Sprite.Width * 4, Sprite.Width * 4);
        }
        SavePng(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, sheet, stride), dlg.FileName);
    }
}
