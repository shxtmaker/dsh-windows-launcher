namespace DshLauncher.Core.Attachments;

/// <summary>
/// D17：由叶名推断 <c>file-begin.mime</c> 的本地映射（纯函数）。
/// 线协议只接受叶名，不接受路径；页面最终按内容与 MIME 自行校验，这里给出的是保守的声明值。
/// </summary>
public static class RemoteBridgeMime
{
    /// <summary>未知扩展名的保守类型。</summary>
    public const string Default = "application/octet-stream";

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".log"] = "text/plain",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yml"] = "application/yaml",
        [".yaml"] = "application/yaml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "text/javascript",
        [".ts"] = "text/plain",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".7z"] = "application/x-7z-compressed",
        [".tar"] = "application/x-tar",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".exe"] = "application/vnd.microsoft.portable-executable",
        [".dll"] = "application/vnd.microsoft.portable-executable",
    };

    /// <summary>按叶名后缀给 MIME；没有后缀或未知后缀给 <see cref="Default"/>。</summary>
    public static string FromLeafName(string? leafName)
    {
        if (string.IsNullOrWhiteSpace(leafName))
        {
            return Default;
        }

        var dot = leafName.LastIndexOf('.');
        if (dot < 0 || dot == leafName.Length - 1)
        {
            return Default;
        }

        return Map.TryGetValue(leafName[dot..], out var mime) ? mime : Default;
    }
}
