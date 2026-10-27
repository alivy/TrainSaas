using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 公开（匿名）机构介绍页与购买意向提交。匿名请求无租户上下文：
/// <see cref="IntentService"/> 在公开路径上统一 IgnoreQueryFilters + 显式
/// 机构谓词读写（设计内的第二个过滤器豁免）。意向提交挂 "intent" 固定窗口
/// 限流（5 次/分钟/源 IP，超限 429，策略已在 Program 注册）。
/// </summary>
[ApiController]
[Route("api/public/orgs")]
[AllowAnonymous]
public class PublicOrgsController(IntentService intentService) : ControllerBase
{
    /// <summary>
    /// GET /api/public/orgs/{code} →
    /// {code,name,logoPath,introText,contactPhone,address,products:[{id,name,type,
    /// price,sessionCount,description,coverImagePath}]}（仅 IsPublished，按 Id
    /// 升序）。机构不存在或 Disabled → 404（响应一致，不泄露存在性）。
    /// </summary>
    [HttpGet("{code}")]
    public async Task<ActionResult<PublicOrgResponse>> GetOrg(string code) =>
        Ok(await intentService.GetPublicOrgAsync(code));

    /// <summary>
    /// POST /api/public/orgs/{code}/intents {name?,phone,productId?,message?} →
    /// 201 {id}。phone 必填且 <c>^1\d{10}$</c>；productId（若提供）必须是本机构
    /// 已发布产品；message ≤200，非法一律 400。同 IP 每分钟限 5 次，超限 429。
    /// </summary>
    [HttpPost("{code}/intents")]
    [EnableRateLimiting("intent")]
    public async Task<ActionResult<IntentCreatedResponse>> CreateIntent(
        string code, CreateIntentRequest request)
    {
        var intent = await intentService.CreateIntentAsync(code, request);
        return StatusCode(StatusCodes.Status201Created, new IntentCreatedResponse(intent.Id));
    }
}
