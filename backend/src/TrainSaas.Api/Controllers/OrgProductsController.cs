using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 课程产品管理。控制器整体挂 [AdminOrSales]（user_type=staff 且
/// role∈{Admin,Sales}）——只读面；写路由（POST/PUT/publish/unpublish）追加
/// [AdminOnly]，两个策略按 AND 组合，因此写操作实际仅 Admin 可达，
/// Teacher/FrontDesk 在所有产品路由上一律 403。数据面经租户过滤器天然限制
/// 在本机构，未知或跨机构的产品 id 一律 404。
/// </summary>
[ApiController]
[Route("api/org/products")]
[Authorize(Policy = AuthPolicies.AdminOrSales)]
public class OrgProductsController(ProductService productService) : ControllerBase
{
    /// <summary>
    /// GET /api/org/products?type&amp;published&amp;page&amp;pageSize →
    /// Paged&lt;{id,name,type,price,sessionCount,description,coverImagePath,
    /// isPublished,createdAt}&gt;。type/published 可选
    /// （Fixed/Flexible、true/false，非法值 400）。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Paged<ProductListItem>>> List(
        string? type, string? published, int? page, int? pageSize) =>
        Ok(await productService.ListAsync(type, published, page, pageSize));

    /// <summary>
    /// POST /api/org/products {name,type,price,sessionCount,description?} →
    /// 201 {id,name,type,price,sessionCount,description,coverImagePath,
    /// isPublished,createdAt}。新建默认未发布。
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<ActionResult<ProductCreatedResponse>> Create(CreateProductRequest request)
    {
        var product = await productService.CreateAsync(request);
        return StatusCode(StatusCodes.Status201Created, ToCreatedResponse(product));
    }

    /// <summary>PUT /api/org/products/{id}——仅更新非 null 字段；未知 id 404。</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Update(int id, UpdateProductRequest request)
    {
        await productService.UpdateAsync(id, request);
        return NoContent();
    }

    /// <summary>POST /api/org/products/{id}/publish → 204。</summary>
    [HttpPost("{id}/publish")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Publish(int id) =>
        await SetPublishedAsync(id, published: true);

    /// <summary>POST /api/org/products/{id}/unpublish → 204。</summary>
    [HttpPost("{id}/unpublish")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Unpublish(int id) =>
        await SetPublishedAsync(id, published: false);

    private async Task<IActionResult> SetPublishedAsync(int id, bool published)
    {
        await productService.SetPublishedAsync(id, published);
        return NoContent();
    }

    private static ProductCreatedResponse ToCreatedResponse(Product product) =>
        new(product.Id, product.Name, product.Type.ToString(), product.Price, product.SessionCount,
            product.Description, product.CoverImagePath, product.IsPublished, product.CreatedAt);
}
