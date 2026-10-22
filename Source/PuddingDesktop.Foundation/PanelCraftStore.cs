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
/// 制成品「继续编辑」画布的 panelId 约定。
///
/// <para>
/// 约定存在这里而不是散在两处字面量里：**来源就写在 panelId 上**，
/// 所以再保存时不需要额外记「这个 tab 是从哪件制成品打开的」——
/// 多一份内存状态就多一个「重启后就丢了」的失败面。
/// </para>
/// </summary>
public static class PanelCraftEditPanel
{
    /// <summary>编辑画布 panelId 的前缀。</summary>
    public const string Prefix = "edit-";

    /// <summary>由制成品得编辑画布 id。</summary>
    public static string IdOf(PanelCraft craft)
    {
        ArgumentNullException.ThrowIfNull(craft);
        return Prefix + craft.Id[PanelCraft.IdPrefix.Length..];
    }

    /// <summary>
    /// 由 panelId 反推制成品 id；不是编辑画布或形状不对时返回 <c>null</c>。
    ///
    /// <para>
    /// 必须走 <see cref="PanelCraft.IsValidId"/>：这个值会当 <c>parent</c> 写进记录，
    /// 任意字符串（比如 Agent 自己发的 <c>edit-xxx</c> 面板）不能被当成本地制成品。
    /// </para>
    /// </summary>
    public static string? CraftIdOf(string? panelId)
    {
        if (string.IsNullOrWhiteSpace(panelId) || !panelId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var candidate = PanelCraft.IdPrefix + panelId[Prefix.Length..];
        return PanelCraft.IsValidId(candidate) ? candidate : null;
    }
}

/// <summary>
/// 制成品血缘的读取：算一件制成品是「第几版」。
///
/// <para>
/// 纯函数，不发 IO：制品库列表已经有全量记录，算深度只需要它们。
/// 必须处理**成环**与**祖先已删**两种真实现场 —— 手写/手改过的
/// JSON 完全可能写出 <c>a→b→a</c>，而删除制成品不会去改子件的 parent。
/// </para>
/// </summary>
public static class PanelCraftLineage
{
    /// <summary>追溯深度上限。环与超长链都靠它兜底——界面文案“第 N 版”不需要更深。</summary>
    public const int MaxDepth = 32;

    /// <summary>算每件制品的版本号（无父 = 1）。不在集合内的 id 不返回。</summary>
    public static IReadOnlyDictionary<string, int> Depths(IEnumerable<PanelCraft> crafts)
    {
        ArgumentNullException.ThrowIfNull(crafts);

        var byId = crafts.ToDictionary(craft => craft.Id, StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(byId.Count, StringComparer.Ordinal);
        foreach (var craft in byId.Values)
        {
            depths[craft.Id] = Depth(byId, craft);
        }

        return depths;
    }

    private static int Depth(IReadOnlyDictionary<string, PanelCraft> byId, PanelCraft craft)
    {
        var depth = 1;
        var seen = new HashSet<string>(StringComparer.Ordinal) { craft.Id };
        var current = craft;

        while (current.Parent is { } parent)
        {
            if (!byId.TryGetValue(parent, out var next))
            {
                // 祖先已被删：链断了，但「它确实是从某一版改来的」是事实，算一层。
                return depth + 1;
            }

            if (!seen.Add(parent))
            {
                // 成环：停止，不自作聪明“修复”用户的数据。
                return depth;
            }

            depth++;
            if (depth >= MaxDepth)
            {
                return depth;
            }

            current = next;
        }

        return depth;
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
                // 标注形状只有一份实现：PanelCanvasRestore.BuildPayload（页面 load 与制成品共用）。
                // 自己再写一遍 kind 与字段名，就是给「保存成功、继续编辑却没有标注」留后门。
                writer.WritePropertyName("annotations");
                writer.WriteRawValue(PanelCanvasRestore.BuildPayload(craft.Source.Annotations));
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
            string.IsNullOrWhiteSpace(title) ? id : (NormalizeTitle(title) ?? id),
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

    /// <summary>制成品标题的字符上限。超长就截断：名字是人看的，长过一行就没意义了。</summary>
    public const int MaxTitleLength = 120;

    /// <summary>
    /// 标题规范化：去空白、超长截断；空白返回 <c>null</c>。
    ///
    /// <para>
    /// **保存与改名必须用同一套** —— 否则同一件制品会因为你从哪条路径进来，
    /// 而有不同的长度上限，用户看到的就是“改个名长度就变了”。
    /// </para>
    /// </summary>
    private static string? NormalizeTitle(string? title)
    {
        var cleaned = title?.Trim();
        if (string.IsNullOrEmpty(cleaned))
        {
            return null;
        }

        return cleaned.Length > MaxTitleLength ? cleaned[..MaxTitleLength] : cleaned;
    }

    /// <summary>
    /// 改名。**只改 title**，不碰图片、哈希与创建时间 —— 改名不是重新保存。
    ///
    /// <para>
    /// 空白名字一律拒绝（返回 <c>null</c>）而不是静默回落成旧值/占位值：
    /// 用户可能只是把输入框清空了，此时什么都不做比写进一个“无名”更接近他的意图。
    /// </para>
    /// </summary>
    /// <returns>更新后的记录；id 非法、记录不存在、标题为空或写盘失败时返回 <c>null</c>。</returns>
    public PanelCraft? Rename(string? id, string? title)
    {
        if (!PanelCraft.IsValidId(id))
        {
            return null;
        }

        var cleaned = NormalizeTitle(title);
        if (cleaned is null)
        {
            return null;
        }

        var path = Path.Combine(CraftsDirectory, id + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        var current = PanelCraftJson.Read(File.ReadAllText(path, Encoding.UTF8));
        if (current is null)
        {
            return null;
        }

        var updated = current with { Title = cleaned };
        File.WriteAllText(
            path,
            PanelCraftJson.Write(updated),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return updated;
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

    /// <summary>
    /// 尝试解析图片路径。**不安全就不解析**：<c>fileName</c> 来自可被编辑的 craft.json，
    /// <c>..\..\secrets.txt</c> 这种值会让 <see cref="Delete"/> 把库外文件搬进回收站。
    /// </summary>
    public bool TryResolveImagePath(PanelCraft craft, out string path)
    {
        path = string.Empty;
        ArgumentNullException.ThrowIfNull(craft);

        if (!IsSafeFileName(craft.FileName))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(CraftsDirectory);
            var combined = Path.GetFullPath(Path.Combine(root, craft.FileName));

            // 纵深防御：即使文件名检查漏了，也不允许解析结果落到库目录之外。
            if (!combined.StartsWith(WithSeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            path = combined;
            return true;
        }
        catch (ArgumentException)
        {
            // 非法路径字符：当作不可解析，不用一个差一点的路径继续。
            return false;
        }
    }

    /// <summary>制成品图片的绝对路径。文件缺失时仍然返回路径，由调用方判断存在性。</summary>
    /// <exception cref="InvalidOperationException">craft.json 里的 fileName 不安全。</exception>
    public string ResolveImagePath(PanelCraft craft)
    {
        ArgumentNullException.ThrowIfNull(craft);
        if (!TryResolveImagePath(craft, out var path))
        {
            throw new InvalidOperationException(
                $"craft '{craft.Id}' has an unsafe fileName: '{craft.FileName}'");
        }

        return path;
    }

    private static string WithSeparator(string directory) =>
        directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

    /// <summary>
    /// fileName 必须是一个**纯文件名**：无目录分隔符、无盘符/备用数据流冒号、
    /// 不是 <c>.</c> / <c>..</c>。
    /// </summary>
    private static bool IsSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
        {
            return false;
        }

        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(':'))
        {
            return false;
        }

        return string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal);
    }

    /// <summary>
    /// 删除：JSON 与图片一起移到 <c>_trash/{id}-{时间戳}/</c>。
    /// 不真删是刻意的 —— 误删一张改了很久的图，代价远大于占一点磁盘。
    /// </summary>
    /// <param name="retention">
    /// 传入时顺便清理超期回收站。为什么放在这里：<see cref="PruneTrash"/> 原本只在
    /// “制成品标签页重建”时被调 —— 一个从不打开那个 tab 的用户，删除的东西会永远堆着。
    /// 而 Delete 是 <c>_trash</c> **唯一**的增长入口，在这里清是对着源头做。
    /// </param>
    public bool Delete(string id, DateTimeOffset now, TimeSpan? retention = null)
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

        // fileName 来自可被编辑的 craft.json：**在所有移动之前**校验。
        // 否则 JSON 已经搬走、图片却指向库外，就是一个半完成状态。
        if (!TryResolveImagePath(craft, out var imagePath))
        {
            return false;
        }

        var trashDirectory = Path.Combine(
            CraftsDirectory, TrashDirectoryName, $"{id}-{now.ToUnixTimeMilliseconds()}");
        Directory.CreateDirectory(trashDirectory);

        File.Move(jsonPath, Path.Combine(trashDirectory, Path.GetFileName(jsonPath)));

        if (File.Exists(imagePath))
        {
            File.Move(imagePath, Path.Combine(trashDirectory, craft.FileName));
        }

        if (retention is { } keep)
        {
            PruneTrash(now, keep);
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

        // 左边必须是**完整的制品 id**。只校验「尾缀是数字」的话，
        // 用户自己建的 my-folder-1699999999 会被当成回收站条目连内容一起删掉。
        if (!PanelCraft.IsValidId(directoryName[..separator]))
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
