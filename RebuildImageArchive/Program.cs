using System.IO.Compression;
using System.IO.Enumeration;
using SkiaSharp;

// コマンドライン引数をパース（ワイルドカードは展開する）
var zipPaths = new List<string>();
foreach (string arg in args)
{
    string[] matches = ExpandWildcards(arg).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (matches.Length == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"警告: '{arg}' に一致するファイルが見つかりません。");
        Console.ResetColor();
        Console.WriteLine();
    }

    zipPaths.AddRange(matches.Where(path => File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));
}

// 重複を除去
zipPaths = zipPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

// ZIPファイルパスが0の場合は、警告と使い方を表示して終了
if (zipPaths.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("警告: 処理するZIPファイルが見つかりません。");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine("使い方: ResizeImagesInArchive2 <ZIPファイルパス> [ZIPファイルパス2] ...");
    Console.WriteLine("例: ResizeImagesInArchive2 images.zip backup.zip");
    Console.WriteLine("     ResizeImagesInArchive2 \"*.zip\"");
    Console.WriteLine("     ResizeImagesInArchive2 \"C:\\data\\*.zip\" \"backup\\*.zip\"");
    return;
}

Console.WriteLine($"処理対象のZIPファイル: {zipPaths.Count}個\n");

// 各ZIPファイルパスに対して処理を行う
foreach (string zipPath in zipPaths)
{
    try
    {
        Console.WriteLine($"処理中: {zipPath}");
        await ProcessZipFileAsync(zipPath);
        Console.WriteLine($"✓ 完了: {zipPath}\n");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✗ エラー: {zipPath} - {ex.Message}");
        Console.ResetColor();
        Console.WriteLine();
    }
}

Console.WriteLine("すべてのZIPファイルの処理が完了しました。");

/// <summary>
/// ZIPファイル内の画像をWebP形式に変換・縮小して処理
/// </summary>
async Task ProcessZipFileAsync(string zipPath)
{
    // ZIP内のファイルリストを取得
    var fileList = new List<ZipArchiveEntry>();
    using (var zipArchive = ZipFile.OpenRead(zipPath))
    {
        fileList = zipArchive.Entries.Where(e => !e.FullName.EndsWith("/")).ToList();
    }

    if (fileList.Count == 0)
    {
        Console.WriteLine("  → スキップ: ZIPファイルが空です");
        return;
    }

    // ほぼすべてが画像ファイルか判定
    var imageExtensions = new HashSet<string> { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".ico" };
    var imageFiles = fileList.Where(e => imageExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant())).ToList();
    double imageRatio = (double)imageFiles.Count / fileList.Count;

    if (imageRatio < 0.8) // ほぼ全て = 80%以上が画像
    {
        Console.WriteLine($"  → スキップ: 画像ファイルが {imageRatio:P0} (80%未満)");
        return;
    }

    // 画像ファイルの拡張子がすべてwebpか確認
    var allWebp = imageFiles.All(e => Path.GetExtension(e.Name).Equals(".webp", StringComparison.OrdinalIgnoreCase));
    if (allWebp)
    {
        Console.WriteLine("  → スキップ: すべてのファイルが既にWebP形式です");
        return;
    }

    // 一時フォルダを作成
    string tempDir = Path.Combine(Path.GetTempPath(), $"ResizeImages_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try
    {
        // ZIPファイルを解凍
        Console.WriteLine("  → 解凍中...");
        ZipFile.ExtractToDirectory(zipPath, tempDir);

        // 画像ファイルを処理（マルチスレッド）
        Console.WriteLine("  → 画像処理中...");
        await ProcessImagesInDirectoryAsync(tempDir);

        // ZIPに再圧縮
        Console.WriteLine("  → 再圧縮中...");
        string newZipPath = $"{tempDir}.zip";
        CreateZipArchive(tempDir, newZipPath);

        // バックアップフォルダを作成
        string zipDir = Path.GetDirectoryName(zipPath)!;
        string backupDir = Path.Combine(zipDir, "BACKUP");
        Directory.CreateDirectory(backupDir);

        // 元のZIPファイルをバックアップ
        string backupZipPath = Path.Combine(backupDir, Path.GetFileName(zipPath));
        if (File.Exists(backupZipPath))
        {
            File.Delete(backupZipPath);
        }
        File.Move(zipPath, backupZipPath);

        // 新しいZIPファイルを元の場所に移動＆リネーム
        File.Move(newZipPath, zipPath, overwrite: true);

        Console.WriteLine("  → バックアップ完了: {0}", backupZipPath);
    }
    finally
    {
        // 一時フォルダを削除
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }

        // 仮ZIPファイルがあれば削除
        string tempZipPath = $"{tempDir}.zip";
        if (File.Exists(tempZipPath))
        {
            File.Delete(tempZipPath);
        }
    }
}

/// <summary>
/// ディレクトリ内のすべての画像ファイルを処理（マルチスレッド）
/// </summary>
async Task ProcessImagesInDirectoryAsync(string dirPath)
{
    var imageExtensions = new HashSet<string> { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".ico" };
    var imageFiles = Directory.EnumerateFiles(dirPath, "*.*", SearchOption.AllDirectories)
        .Where(f => imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .ToList();

    // マルチスレッド処理
    var tasks = imageFiles.Select(filePath => ProcessImageFileAsync(filePath)).ToArray();
    await Task.WhenAll(tasks);
}

/// <summary>
/// 単一の画像ファイルを処理（WebP変換＆縮小）
/// </summary>
Task ProcessImageFileAsync(string imagePath)
{
    return Task.Run(() =>
    {
        try
        {
            bool isWebP = Path.GetExtension(imagePath).Equals(".webp", StringComparison.OrdinalIgnoreCase);

            // 画像を読み込む
            using var original = SKBitmap.Decode(imagePath);
            if (original == null)
            {
                Console.WriteLine($"  ⚠ 警告: {Path.GetFileName(imagePath)} をデコードできませんでした");
                return;
            }

            bool needsResize = original.Height > 1280;

            // 既にWebPで縮小も不要な場合は処理しない
            if (isWebP && !needsResize)
            {
                return;
            }

            // 保存用ビットマップを準備
            SKBitmap? resized = null;
            SKBitmap saveTarget;
            if (needsResize)
            {
                int newHeight = 1280;
                int newWidth = (int)(original.Width * (newHeight / (double)original.Height));
                resized = original.Resize(new SKImageInfo(newWidth, newHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                saveTarget = resized!;
            }
            else
            {
                saveTarget = original;
            }

            try
            {
                // WebP形式で保存
                string webpPath = Path.Combine(Path.GetDirectoryName(imagePath)!, Path.GetFileNameWithoutExtension(imagePath) + ".webp");
                using var image = SKImage.FromBitmap(saveTarget);
                using var data = image.Encode(SKEncodedImageFormat.Webp, 75);
                using var stream = File.OpenWrite(webpPath);
                data.SaveTo(stream);

                // 元のファイルを削除（WebP以外の場合、または既存ファイルと異なる場合）
                if (!imagePath.Equals(webpPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(imagePath);
                }
            }
            finally
            {
                resized?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠ 警告: {Path.GetFileName(imagePath)} の処理に失敗 - {ex.Message}");
            Console.ResetColor();
        }
    });
}

/// <summary>
/// ディレクトリをZIPアーカイブに圧縮
/// </summary>
void CreateZipArchive(string sourceDir, string zipPath)
{
    if (File.Exists(zipPath))
    {
        File.Delete(zipPath);
    }

    using var zipArchive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

    foreach (var filePath in Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories))
    {
        string relativePath = Path.GetRelativePath(sourceDir, filePath);
        zipArchive.CreateEntryFromFile(filePath, relativePath);
    }
}

/// <summary>
/// ワイルドカード（* と ?）を含むパターンを展開する。含まれない場合はそのまま返す。
/// </summary>
IEnumerable<string> ExpandWildcards(string pattern)
{
    if (!HasWildcard(pattern))
    {
        yield return pattern;
        yield break;
    }

    string leaf = Path.GetFileName(pattern);
    if (leaf.Length == 0)
    {
        yield break;
    }

    string dirPart = Path.GetDirectoryName(pattern) ?? "";
    bool bare = dirPart.Length == 0; // ディレクトリ指定がない場合はファイル名のみ返す

    var results = new List<string>();
    foreach (string dir in ExpandDirectoryPart(dirPart))
    {
        string searchDir = dir.Length == 0 ? "." : dir;
        if (!Directory.Exists(searchDir))
        {
            continue;
        }

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(searchDir))
            {
                if (FileSystemName.MatchesSimpleExpression(leaf, Path.GetFileName(entry), ignoreCase: true))
                {
                    results.Add(bare ? Path.GetFileName(entry) : entry);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // アクセスできないディレクトリはスキップ
        }
    }

    results.Sort(StringComparer.OrdinalIgnoreCase);
    foreach (string result in results)
    {
        yield return result;
    }
}

/// <summary>
/// パターンのディレクトリ部分に含まれるワイルドカードを展開する
/// </summary>
IEnumerable<string> ExpandDirectoryPart(string dir)
{
    if (dir.Length == 0 || !HasWildcard(dir))
    {
        yield return dir;
        yield break;
    }

    string parent = Path.GetDirectoryName(dir) ?? "";
    string leaf = Path.GetFileName(dir);

    foreach (string parentDir in ExpandDirectoryPart(parent))
    {
        string searchDir = parentDir.Length == 0 ? "." : parentDir;
        if (!Directory.Exists(searchDir))
        {
            continue;
        }

        var matches = new List<string>();
        try
        {
            foreach (string subDir in Directory.EnumerateDirectories(searchDir))
            {
                if (FileSystemName.MatchesSimpleExpression(leaf, Path.GetFileName(subDir), ignoreCase: true))
                {
                    matches.Add(subDir);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // アクセスできないディレクトリはスキップ
        }

        foreach (string match in matches)
        {
            yield return match;
        }
    }
}

/// <summary>
/// パターンがワイルドカードを含むかどうか
/// </summary>
static bool HasWildcard(string pattern) => pattern.Contains('*') || pattern.Contains('?');
