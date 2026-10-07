using System.Text.RegularExpressions;

namespace GewCoordinator;

/// <summary>パーツ種別。ファイル名の先頭1文字に対応する。</summary>
public sealed record PartKind(char Prefix, string Label);

/// <summary>Char フォルダのスプライトを索引化し、デコード結果をキャッシュする。</summary>
public sealed class PartLibrary
{
    /// <summary>重ね順(奥 → 手前)。</summary>
    public static readonly PartKind[] Kinds =
    {
        new('L', "脚"),
        new('B', "体"),
        new('H', "頭"),
        new('W', "武器"),
        new('S', "盾"),
    };

    private static readonly Regex NamePattern =
        new(@"^([A-Za-z])(\d{5})(\d{2})\.spr(\.ncs)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<string, Sprite?> _cache = new(StringComparer.OrdinalIgnoreCase);

    // prefix -> item -> pose -> path
    private readonly Dictionary<char, SortedDictionary<int, SortedDictionary<int, string>>> _index = new();

    public Palette Palette { get; }

    public PartLibrary(string rootDir)
    {
        var charDir = Path.Combine(rootDir, "Char");
        if (!Directory.Exists(charDir))
            throw new DirectoryNotFoundException($"Char フォルダが見つかりません: {charDir}");
        var palPath = Path.Combine(rootDir, "Data", "Default.pal");
        if (!File.Exists(palPath))
            throw new FileNotFoundException($"パレットが見つかりません: {palPath}");
        Palette = Palette.Load(palPath);

        foreach (var path in Directory.EnumerateFiles(charDir))
        {
            var m = NamePattern.Match(Path.GetFileName(path));
            if (!m.Success) continue;
            char prefix = char.ToUpperInvariant(m.Groups[1].Value[0]);
            if (!Kinds.Any(k => k.Prefix == prefix)) continue;
            int item = int.Parse(m.Groups[2].Value);
            int pose = int.Parse(m.Groups[3].Value);
            if (!_index.TryGetValue(prefix, out var items))
                _index[prefix] = items = new();
            if (!items.TryGetValue(item, out var poses))
                items[item] = poses = new();
            poses[pose] = path;
        }
    }

    public IReadOnlyList<int> Items(char prefix) =>
        _index.TryGetValue(prefix, out var items) ? items.Keys.ToList() : new List<int>();

    /// <summary>装備の種類(ID の上位 3 桁)の一覧。</summary>
    public IReadOnlyList<int> Types(char prefix) =>
        Items(prefix).Select(i => i / 100).Distinct().ToList();

    /// <summary>指定した種類に存在するカラー番号(ID の下位 2 桁)の一覧。</summary>
    public IReadOnlyList<int> Colors(char prefix, int type) =>
        Items(prefix).Where(i => i / 100 == type).Select(i => i % 100).ToList();

    /// <summary>カラー番号の表示名。公式ファンサイトのカラーバリエーション表に基づく(体 5 色、靴 7 色)。</summary>
    public static string ColorName(char prefix, int color)
    {
        string[]? names = prefix switch
        {
            'B' => new[] { "ハニー", "ピュア", "ブリーチ", "クリーム", "ロイヤル" },
            'L' => new[] { "ゴールド", "シルバー", "ブロンズ", "レッド", "ブルー", "ブラウン", "アイアン" },
            _ => null,
        };
        return names is not null && color < names.Length ? $"{color:D2} {names[color]}" : $"{color:D2}";
    }

    /// <summary>全パーツに存在するポーズの和集合。</summary>
    public IReadOnlyList<int> AllPoses() =>
        _index.Values.SelectMany(i => i.Values).SelectMany(p => p.Keys).Distinct().OrderBy(x => x).ToList();

    public Sprite? Get(char prefix, int item, int pose)
    {
        if (!_index.TryGetValue(prefix, out var items) ||
            !items.TryGetValue(item, out var poses) ||
            !poses.TryGetValue(pose, out var path))
            return null;
        if (!_cache.TryGetValue(path, out var sprite))
        {
            try { sprite = Sprite.Load(path); }
            catch (Exception) { sprite = null; }
            _cache[path] = sprite;
        }
        return sprite;
    }

    /// <summary>
    /// 選択中パーツを重ねた BGRA(136x120)を返す。item が null のパーツは描画しない。
    /// <paramref name="behind"/> に含まれるパーツは、他のパーツより奥(先)に描画する。
    /// </summary>
    public byte[] Compose(IReadOnlyDictionary<char, int?> selection, int pose, int frame,
                          ISet<char>? behind = null, bool mirror = false)
    {
        var pixels = new uint[Sprite.Width * Sprite.Height];
        var order = Kinds.Where(k => behind?.Contains(k.Prefix) == true)
            .Concat(Kinds.Where(k => behind?.Contains(k.Prefix) != true));
        foreach (var kind in order)
        {
            if (!selection.TryGetValue(kind.Prefix, out var item) || item is null) continue;
            var sprite = Get(kind.Prefix, item.Value, pose);
            if (sprite is null || frame >= sprite.Frames.Length) continue;
            var src = sprite.Frames[frame];
            for (int i = 0; i < src.Length; i++)
                if (src[i] != Sprite.Transparent) pixels[i] = Palette.Colors[src[i]];
        }
        if (mirror)
        {
            for (int y = 0; y < Sprite.Height; y++)
                Array.Reverse(pixels, y * Sprite.Width, Sprite.Width);
        }
        var bytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>選択中パーツのうち、このポーズのデータが存在しないものを返す。</summary>
    public IReadOnlyList<PartKind> MissingForPose(IReadOnlyDictionary<char, int?> selection, int pose) =>
        Kinds.Where(k => selection.TryGetValue(k.Prefix, out var item) && item is not null
                         && Get(k.Prefix, item.Value, pose) is null).ToList();

    /// <summary>このポーズで利用できる最大フレーム数。</summary>
    public int FrameCount(IReadOnlyDictionary<char, int?> selection, int pose)
    {
        int max = 0;
        foreach (var kind in Kinds)
        {
            if (selection.TryGetValue(kind.Prefix, out var item) && item is not null)
                max = Math.Max(max, Get(kind.Prefix, item.Value, pose)?.Frames.Length ?? 0);
        }
        return max;
    }
}
