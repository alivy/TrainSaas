using Microsoft.Extensions.Configuration;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 落盘/下发文件描述。RelativePath 是数据库存储值（正斜杠分隔，
/// Storage:UploadRoot 之下）；AbsolutePath 为当前上传根下的绝对路径（仅在
/// 即时读写下发的场合使用，绝不落库）；ContentType 按扩展名推断。
/// </summary>
public sealed record StoredFile(string RelativePath, string AbsolutePath, string ContentType);

/// <summary>
/// 本地磁盘图片存储（机构 Logo、产品封面、订单支付凭据）。
///
/// 配置惰性读取（Task 3 教训）：Storage:UploadRoot 在每次调用时从
/// IConfiguration 读取，绝不在构造或启动时捕获，WebApplicationFactory 的
/// 测试覆盖才能生效。
///
/// 安全设计：相对路径完全由服务端拼装（purpose/机构/月份/Guid），文件
/// 扩展名与 content type 经白名单双向校验，数据库只存服务端生成的相对
/// 路径；下发时再把相对路径锚回上传根并断言结果仍在根内（目录穿越
/// 防线，见 <see cref="ResolveForServing"/>）。不做魔数嗅探（计划裁定）。
/// </summary>
public class FileStorageService(IConfiguration configuration)
{
    private const long MaxImageBytes = 5 * 1024 * 1024;

    /// <summary>允许的图片 content type → 规范扩展名（小写带点；.jpeg 别名见 ValidateImageFileName）。</summary>
    private static readonly Dictionary<string, string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };

    /// <summary>扩展名 → 下发 content type（AllowedContentTypes 的反向映射，含 .jpeg 别名）。</summary>
    private static readonly Dictionary<string, string> ExtensionContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
    };

    /// <summary>允许的存储用途（相对路径第一级目录）。各路由的窄集合（logo/cover、voucher）由控制器校验。</summary>
    private static readonly HashSet<string> AllowedPurposes = ["voucher", "logo", "cover"];

    /// <summary>
    /// 校验上传文件名扩展名与声明的 content type 一致（.jpg/.jpeg↔jpeg、
    /// .png↔png、.webp↔webp），返回规范扩展名（小写带点）。contentType 不在
    /// 白名单或扩展名不匹配 → 400。文件名只存在于控制器一侧（IFormFile，
    /// SaveImageAsync 的签名不含它），故由控制器在调用
    /// <see cref="SaveImageAsync"/> 前调用本方法。
    /// </summary>
    public static string ValidateImageFileName(string? fileName, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)
            || !AllowedContentTypes.TryGetValue(contentType, out var canonicalExtension))
        {
            throw new BusinessRuleException("图片类型无效，仅支持 image/jpeg、image/png、image/webp");
        }

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        var matches = canonicalExtension switch
        {
            ".jpg" => extension is ".jpg" or ".jpeg",
            _ => extension == canonicalExtension,
        };
        if (!matches)
        {
            throw new BusinessRuleException("文件扩展名与图片类型不匹配");
        }

        return canonicalExtension;
    }

    /// <summary>
    /// 校验并落盘一张图片：contentType ∈ 白名单、contentLength ≤ 5MB、
    /// purpose ∈ 白名单，否则 400（先校验后落盘，拒绝的请求不留任何文件）。
    /// 写入 {Storage:UploadRoot}/{purpose}/{institutionId}/{yyyyMM}/{guid}{ext}
    /// （目录按需创建），返回 <see cref="StoredFile"/>——调用方只把
    /// RelativePath 落库，绝对路径在下发时按当次配置重新解析。
    /// </summary>
    public async Task<StoredFile> SaveImageAsync(
        Stream content, string contentType, long contentLength, int institutionId, string purpose)
    {
        if (string.IsNullOrWhiteSpace(contentType)
            || !AllowedContentTypes.TryGetValue(contentType, out var extension))
        {
            throw new BusinessRuleException("图片类型无效，仅支持 image/jpeg、image/png、image/webp");
        }

        if (contentLength > MaxImageBytes)
        {
            throw new BusinessRuleException("图片大小不能超过 5MB");
        }

        if (!AllowedPurposes.Contains(purpose))
        {
            throw new BusinessRuleException("上传用途无效");
        }

        // 惰性读取：上传根目录在调用时解析（配置源可能被测试/宿主覆盖）。
        var uploadRoot = ResolveUploadRoot();
        var relativePath = $"{purpose}/{institutionId}/{DateTime.Now:yyyyMM}/{Guid.NewGuid():N}{extension}";
        var absolutePath = Path.GetFullPath(Path.Combine(uploadRoot, relativePath));

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        // CreateNew：Guid 名重复属异常态，宁可报错也不覆盖他人文件。
        await using (var target = new FileStream(absolutePath, FileMode.CreateNew, FileAccess.Write))
        {
            await content.CopyToAsync(target);
        }

        return new StoredFile(relativePath, absolutePath, contentType);
    }

    /// <summary>
    /// 把数据库存储的相对路径解析为可下发文件：锚回当次配置的
    /// Storage:UploadRoot，断言解析后的绝对路径仍严格位于上传根内（穿越
    /// 防线——相对路径虽由服务端生成，仍不信任存储值），且文件物理存在、
    /// 扩展名在白名单内，否则一律 404（不区分原因，不泄露细节）。
    /// ContentType 按扩展名推断（数据库不存 content type 列）。
    /// </summary>
    public StoredFile ResolveForServing(string relativePath)
    {
        var uploadRoot = ResolveUploadRoot();
        var rootFullPath = Path.GetFullPath(Path.TrimEndingDirectorySeparator(uploadRoot));
        var fullPath = Path.GetFullPath(Path.Combine(uploadRoot, relativePath));

        if (!fullPath.StartsWith(rootFullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new NotFoundException("凭证文件不存在");
        }

        if (!File.Exists(fullPath))
        {
            throw new NotFoundException("凭证文件不存在");
        }

        if (!ExtensionContentTypes.TryGetValue(Path.GetExtension(fullPath), out var contentType))
        {
            throw new NotFoundException("凭证文件不存在");
        }

        return new StoredFile(relativePath, fullPath, contentType);
    }

    /// <summary>上传根目录（默认 uploads），调用时读取（惰性）。</summary>
    private string ResolveUploadRoot()
    {
        var uploadRoot = configuration["Storage:UploadRoot"];
        return string.IsNullOrWhiteSpace(uploadRoot) ? "uploads" : uploadRoot;
    }
}
