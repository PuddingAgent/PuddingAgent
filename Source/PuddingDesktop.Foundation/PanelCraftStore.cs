namespace PuddingDesktop.Foundation;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>一次生成/编辑的来源信息。用于「再加工」时追溯改了什么、基于什么指令。</summary>
public sealed record PanelCraftSource(
    string? PanelId,
    string? Generator,
    string? Prompt,
    IReadOnlyList<PanelAnnotation> Annotations);

/// <summary>
/// 一件制成品：用户**显式确认保存**的版本。
///
/// <para>
/// 与 <c>vision-artifact</c> 的区别是这里记录的全部是「人做的决定」：名字、血缘、
/// 当时用的指令。生成结果由机器自动落盘、无名无血缘；制成品是「我挑中的这一版」，
/// 也是再加工的起点。
/// </para>
/// </summary>
public sealed record PanelCraft(
    string Id,
    string Title,
    string FileName,
    string MimeType,
    int Width,
    int Height,
    string Sha256,
    long Bytes,
    long CreatedAt,
    string? Parent,
    PanelCraftSource? Source,
    string? Note)
{
    /// <summary>制成品 id 前缀。与 vision-* 区分开，避免两套 id 混用。</summary>
    public const string IdPrefix = "craft-";

    /// <summary>id 总长度：前缀 6 + 8 位十六进制。</summary>
    private const int IdLength = 14;

    public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 校验 id 形状。id 会拼进文件名，所以必须严格：
    /// 只允许前缀 + 8 位小写十六进制，杜绝任何路径字符。
    /// </summary>
    public static bool IsValidId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? id)
    {
        if (id is null || id.Length != IdLength || !id.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = IdPrefix.Length; i < id.Length; i++)
        {
            var ch = id[i];
            if (!char.IsAsciiHexDigitLower(ch))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// 制成品 JSON 的读写。**手写而非反射序列化**：Desktop 侧将来可能开 AOT，
/// 而 <c>JsonSerializer</c> 对记录类型的元数据依赖在裁剪下容易失效 ——
/// 那种失效的表现是运行期才抛，且在打包后的进程里很难复现。
/// </summary>
public static class PanelCraftJson
{
    public static string Write(PanelCraft craft)
    {
        ArgumentNullException.ThrowIfNull(craft);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", craft.Id);
            writer.WriteString("title", craft.Title);
            writer.WriteString("fileName", craft.FileName);
            writer.WriteString("mimeType", craft.MimeType);
            writer.WriteNumber("width", craft.Width);
            writer.WriteNumber("height", craft.Height);
            writer.WriteString("sha256", craft.Sha256);
            writer.WriteNumber("bytes", craft.Bytes);
            writer.WriteNumber("createdAt", craft.CreatedAt);

            if (craft.Parent is null)
            {
                writer.WriteNull("parent");
            }
            else
            {
                writer.WriteString("parent", craft.Parent);
            }

            if (craft.Source is null)
            {
                // 显式写 null 而不是省略：字段缺席会让"旧版本文件"和"新版本但无来源"
                // 变得无法区分，将来迁移时无从判断。
                writer.WriteNull("source");
            }
            else
            {
                writer.WriteStartObject("source");
                WriteNullableString(writer, "panelId", craft.Source.PanelId);
                WriteNullableString(writer, "generator", craft.Source.Generator);
                WriteNullableString(writer, "prompt", craft.Source.Prompt);
                writer.WriteStartArray("annotations");
                foreach (var annotation in craft.Source.Annotations)
                {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "kind",
                        annotation.Kind == PanelAnnotationKind.Box ? "box" : "point");
                    if (annotation.Kind == PanelAnnotationKind.Box)
                    {
                        writer.WriteNumber("x1", annotation.X1);
                        writer.WriteNumber("y1", annotation.Y1);
                        writer.WriteNumber("x2", annotation.X2);
                        writer.WriteNumber("y2", annotation.Y2);
                    }
                    else
                    {
                        writer.WriteNumber("x", annotation.X1);
                        writer.WriteNumber("y", annotation.Y1);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            if (craft.Note is null)
            {
                writer.WriteNull("note");
            }
            else
            {
                writer.WriteString("note", craft.Note);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>读一件制成品。<paramref name="json"/> 损坏或形状不对时返回 null（不抛）。</summary>
    public static PanelCraft? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var id = ReadString(root, "id");
            var fileName = ReadString(root, "fileName");
            var sha = ReadString(root, "sha256");
            if (!PanelCraft.IsValidId(id) || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(sha))
            {
                return null;
            }

            PanelCraftSource? source = null;
            if (root.TryGetProperty("source", out var sourceElement)
                && sourceElement.ValueKind == JsonValueKind.Object)
            {
                IReadOnlyList<PanelAnnotation> annotations = Array.Empty<PanelAnnotation>();
                if (sourceElement.TryGetProperty("annotations", out var annotationsElement)
                    && PanelAnnotation.TryParse(annotationsElement.GetRawText(), out var parsed, out _))
                {
                    annotations = parsed;
                }

                source = new PanelCraftSource(
                    ReadString(sourceElement, "panelId"),
                    ReadString(sourceElement, "generator"),
                    ReadString(sourceElement, "prompt"),
                    annotations);
            }

            return new PanelCraft(
                id,
                ReadString(root, "title") ?? string.Empty,
                fileName,
                ReadString(root, "mimeType") ?? "application/octet-stream",
                (int)ReadNumber(root, "width"),
                (int)ReadNumber(root, "height"),
                sha,
                ReadNumber(root, "bytes"),
                ReadNumber(root, "createdAt"),
                ReadString(root, "parent"),
                source,
                ReadString(root, "note"));
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var value)
            ? value
            : 0;
}

/// <summary>
/// 制成品目录。**本地目录是唯一真源**，不维护额外索引文件 ——
/// 索引与磁盘不一致时，修起来比重扫一遍目录贵得多。
/// </summary>
public sealed class PanelCraftStore
{
    /// <summary>删除不真删，先移到这里；由用户在资源管理器里清理。</summary>
    public const string TrashDirectoryName = "_trash";

    public PanelCraftStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        CraftsDirectory = Path.Combine(rootDirectory, "crafts");
    }

    /// <summary>制成品所在目录。构造时不创建，第一次写入才建。</summary>
    public string CraftsDirectory { get; }

    /// <summary>
    /// 保存一件制成品：把源图片复制进目录、算 sha256、写 JSON。
    ///
    /// <para>
    /// 不做 sha256 去重：两件 id 不同的制成品必须各自可独立删除，
    /// 共享文件会让"删了这件，那件也坏了"。空间由用户自己管理。
    /// </para>
    /// </summary>
    public PanelCraft Save(
        string sourceImagePath,
        string title,
        int width,
        int height,
        PanelCraftSource? source,
        string? parent,
        string? note,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);

        var fullSource = Path.GetFullPath(sourceImagePath);
        var info = new FileInfo(fullSource);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Source image was not found.", fullSource);
        }

        var extension = info.Extension;
        if (!PanelImageSource.IsSupportedExtension(extension))
        {
            throw new ArgumentException(
                $"'{extension}' is not a supported image type.", nameof(sourceImagePath));
        }

        var bytes = File.ReadAllBytes(fullSource);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        Directory.CreateDirectory(CraftsDirectory);

        var id = PanelCraft.NewId();
        var fileName = id + extension.ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(CraftsDirectory, fileName), bytes);

        var craft = new PanelCraft(
            id,
            string.IsNullOrWhiteSpace(title) ? id : title.Trim(),
            fileName,
            MimeFor(extension),
            width,
            height,
            sha256,
            bytes.Length,
            now.ToUnixTimeMilliseconds(),
            string.IsNullOrWhiteSpace(parent) ? null : parent.Trim(),
            source,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim());

        File.WriteAllText(
            Path.Combine(CraftsDirectory, id + ".json"),
            PanelCraftJson.Write(craft),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return craft;
    }

    /// <summary>
    /// 列出全部制成品，按创建时间倒序。
    ///
    /// <para>
    /// 单个 JSON 损坏只跳过它：一件坏文件不该让整个制品库消失 ——
    /// 那会让用户以为自己的东西全丢了。
    /// </para>
    /// </summary>
    public IReadOnlyList<PanelCraft> List()
    {
        if (!Directory.Exists(CraftsDirectory))
        {
            return Array.Empty<PanelCraft>();
        }

        var crafts = new List<PanelCraft>();
        foreach (var path in Directory.EnumerateFiles(CraftsDirectory, "*.json"))
        {
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException)
            {
                continue;
            }

            var craft = PanelCraftJson.Read(text);
            if (craft is not null)
            {
                crafts.Add(craft);
            }
        }

        crafts.Sort((left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
        return crafts;
    }

    /// <summary>制成品图片的绝对路径。文件缺失时仍然返回路径，由调用方判断存在性。</summary>
    public string ResolveImagePath(PanelCraft craft)
    {
        ArgumentNullException.ThrowIfNull(craft);
        return Path.Combine(CraftsDirectory, craft.FileName);
    }

    /// <summary>
    /// 删除：JSON 与图片一起移到 <c>_trash/{id}-{时间戳}/</c>。
    /// 不真删是刻意的 —— 误删一张改了很久的图，代价远大于占一点磁盘。
    /// </summary>
    public bool Delete(string id, DateTimeOffset now)
    {
        if (!PanelCraft.IsValidId(id))
        {
            return false;
        }

        var jsonPath = Path.Combine(CraftsDirectory, id + ".json");
        if (!File.Exists(jsonPath))
        {
            return false;
        }

        var craft = PanelCraftJson.Read(File.ReadAllText(jsonPath, Encoding.UTF8));
        if (craft is null)
        {
            return false;
        }

        var trashDirectory = Path.Combine(
            CraftsDirectory, TrashDirectoryName, $"{id}-{now.ToUnixTimeMilliseconds()}");
        Directory.CreateDirectory(trashDirectory);

        File.Move(jsonPath, Path.Combine(trashDirectory, Path.GetFileName(jsonPath)));

        var imagePath = ResolveImagePath(craft);
        if (File.Exists(imagePath))
        {
            File.Move(imagePath, Path.Combine(trashDirectory, craft.FileName));
        }

        return true;
    }

    /// <summary>
    /// 清理 <c>_trash</c> 中超过保留期的条目。
    ///
    /// <para>
    /// <see cref="Delete"/> 只把东西移进来，从不清理 —— 一个跑几个月的助手会让
    /// <c>_trash</c> 无限增长。<b>时间取自目录名里的时间戳</b>，不取文件系统 mtime：
    /// 后者会被复制、备份、解压工具改掉，而目录名是我们自己写的，语义明确。
    /// </para>
    ///
    /// <para>
    /// 名字不符合 <c>{id}-{unixMillis}</c> 的目录一律跳过 —— 用户可能自己往里放了东西，
    /// 「不认识就删」比「不删」危险得多。单个条目删除失败也只跳过，不中断整批。
    /// </para>
    /// </summary>
    /// <returns>实际删除的目录数；<c>_trash</c> 不存在时返回 0。</returns>
    public int PruneTrash(DateTimeOffset now, TimeSpan retention)
    {
        var trashDirectory = Path.Combine(CraftsDirectory, TrashDirectoryName);
        if (!Directory.Exists(trashDirectory))
        {
            return 0;
        }

        var cutoff = now.ToUnixTimeMilliseconds() - (long)retention.TotalMilliseconds;
        var removed = 0;

        foreach (var directory in Directory.EnumerateDirectories(trashDirectory))
        {
            var name = Path.GetFileName(directory);
            if (!TryReadTrashTimestamp(name, out var deletedAt) || deletedAt > cutoff)
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (IOException)
            {
                // 文件被占用（用户在资源管理器里开着）—— 下次再清，不阻断其余条目。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>
    /// 从 <c>{id}-{unixMillis}</c> 形式的目录名里取出删除时刻。
    /// <c>id</c> 本身就带连字符（<c>craft-xxxxxxxx</c>），所以从**最后一个**连字符往右解析。
    /// </summary>
    private static bool TryReadTrashTimestamp(string directoryName, out long unixMillis)
    {
        unixMillis = 0;

        var separator = directoryName.LastIndexOf('-');
        if (separator <= 0 || separator == directoryName.Length - 1)
        {
            return false;
        }

        var digits = directoryName.AsSpan(separator + 1);
        foreach (var ch in digits)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(digits, out unixMillis);
    }

    private static string MimeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
