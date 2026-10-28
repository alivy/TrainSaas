# TrainSaas 培训机构 SaaS（后端）

多租户培训机构管理 SaaS 的后端 API：平台/机构两级账号体系，机构侧覆盖员工与角色权限、课程产品（课时包）、学员与归属销售、订单（凭证上传/确认/取消/作废）、固定班排课与点名扣课、随到随上核销与冲正、销售业绩报表、公开机构页与购买意向收集；家长端可查孩子、课时包余量、扣课流水与课表。

> 前端为计划 2，尚未开始；本仓库当前仅包含后端 API。

## 技术栈

- .NET 10 / ASP.NET Core Web API（Controller 路由）
- EF Core 10 + SQLite（多租户全局查询过滤器 `InstitutionId`，迁移见 `Data/Migrations`）
- JWT Bearer 认证：平台超管 / 机构员工 / 家长三种令牌，固定声明名（`user_type`、`institution_id`、`staff_id`、`role`、`phone`、`name`）
- 本地磁盘图片存储（Logo / 封面 / 订单凭证）
- xUnit + `WebApplicationFactory` 集成测试（真实 HTTP）+ 单元测试

## 目录结构

```
backend/
  src/TrainSaas.Api/
    Controllers/    # 路由层（platform / org / parent / public / files）
    Services/       # 业务层（订单、排课、扣课、报表、家长端等）
    Domain/         # 实体（Staff、Student、Product、Order、SessionPack…）
    Data/           # AppDbContext（租户过滤）+ SeedData（演示种子）+ Migrations
    Dtos/           # 请求/响应契约
    Auth/           # JWT 签发、密码哈希、授权策略、当前用户
    Errors/         # 业务异常 + 统一异常中间件（ProblemDetails）
  tests/TrainSaas.Tests/
    Unit/           # 单元测试
    Integration/    # 集成测试（ApiFactory，真实 HTTP）
```

运行时目录（相对应用工作目录自动创建，已被 .gitignore 忽略）：`data/`（SQLite 数据库）、`uploads/`（图片上传根，见 `Storage:UploadRoot`）。注意 `dotnet run --project` 的工作目录是**项目目录**，因此默认生成 `backend/src/TrainSaas.Api/data/trainsaas.db` 与 `backend/src/TrainSaas.Api/uploads/`。

## 启动

```bash
dotnet run --project backend/src/TrainSaas.Api
```

默认以 Development 环境监听 `http://localhost:5080`。启动时自动完成：

1. **`data/` 目录自动创建**：SQLite 只建库文件不建父目录，程序在迁移前会按连接串里的 `Data Source` 自动创建 `data/` 目录（位于应用工作目录下，`dotnet run --project` 方式即项目目录内），因此全新克隆后直接 `dotnet run` 即可，无需任何手工初始化；
2. 应用 EF Core 迁移；
3. `SeedDemoData=true` 时写入演示数据（Development 默认开启；幂等，重复启动不会产生重复行）。

API 文档：Swagger UI `http://localhost:5080/swagger`（OpenAPI JSON 文档由内置 `MapOpenApi()` 产出，地址 `http://localhost:5080/openapi/v1.json`，Development 环境提供，也可导入 Postman / Apifox 调试）。

## 测试

```bash
cd backend && dotnet test
```

含单元测试与集成测试（集成测试经 `WebApplicationFactory` 走真实 HTTP，使用各自独立的临时数据库，不依赖开发库）。

## 演示账号（`SeedDemoData=true` 时写入，密码均为 `123456`）

| 端 | 账号 | 密码 | 说明 | 登录接口 |
| --- | --- | --- | --- | --- |
| 平台超管 | `admin` | 123456 | 管理机构（租户） | `POST /api/platform/auth/login` |
| 机构管理员 | `13900000001` | 123456 | demo 机构"演示机构" | `POST /api/org/auth/login` |
| 销售 | `13900000002` | 123456 | 王小销 | `POST /api/org/auth/login` |
| 教师 | `13900000003` | 123456 | 李老师 | `POST /api/org/auth/login` |
| 前台 | `13900000004` | 123456 | 赵前台 | `POST /api/org/auth/login` |
| 家长 | `13800001111` | 123456 | 张小明/张小红（兄妹）的家长 | `POST /api/parent/auth/login` |

演示数据还包括：产品「少儿舞蹈基础班」（固定班，¥1980/24 课时）与「体能体验课」（灵活班，¥299/10 课时）、学员李华（13800002222）、基础班周二/周四 18:00–19:30 模板（张小明已入班）、张小明一张已确认订单（含 24/24 课时包）、李华一张待处理订单。

## 配置键（`backend/src/TrainSaas.Api/appsettings.json`）

| 键 | 说明 | 默认值 |
| --- | --- | --- |
| `ConnectionStrings:Default` | SQLite 连接串；`Data Source` 所在目录启动时自动创建 | `Data Source=data/trainsaas.db` |
| `Jwt:Issuer` / `Jwt:Audience` | JWT 签发方 / 受众 | `TrainSaas` |
| `Jwt:Key` | JWT 签名密钥（**生产环境必须更换**） | dev-only 占位密钥 |
| `Jwt:ExpireHours` | 令牌有效期（小时） | `12` |
| `Storage:UploadRoot` | 图片上传根目录（logo/cover/voucher），运行时惰性读取 | `uploads` |
| `Cors:Origins` | 允许跨域的前端源（数组） | `["http://localhost:5173"]` |
| `SeedDemoData` | 启动时是否写入演示数据（幂等） | `false`（Development 覆盖为 `true`） |

## 生产首次部署

生产环境没有"预置平台账号"这回事（`SeedDemoData=false` 时启动只建库不种数据），首次引导按以下顺序完成：

1. **一次性引导**：以 `SeedDemoData=true` 启动一次（如 `dotnet run --project backend/src/TrainSaas.Api` 后随环境变量 `SeedDemoData=true`，或在 appsettings 中临时开启）。这会建好库表并写入平台超管 `admin`（密码 `123456`）与一个演示机构；
2. **立刻改掉平台超管密码**：`POST /api/platform/auth/login`（`admin` / `123456`）取得令牌后马上调 `POST /api/platform/auth/change-password`（`{oldPassword, newPassword}`，新密码 ≥6 位）——初始密码全网已知，这一步没有"稍后再说"；
3. **停用演示机构**：用平台令牌调 `POST /api/platform/institutions/{id}/disable` 把演示机构停用（其员工账号密码同样是初始 `123456`，仅作展示数据，不应留在可用状态；机构的学员/课表数据保留与否不影响租户隔离）；
4. **关闭种子开关**：把 `SeedDemoData` 设回 `false`（生产默认值即 false，Development 环境模板才会覆盖为 true）重启，之后启动只执行迁移，不再写任何数据。

后续新建机构走平台端 `POST /api/platform/institutions`（管理员初始密码同为 `123456`，机构管理员应同样第一时间改密）。登录接口已挂每 IP 每分钟 10 次的固定窗口限流（超限 429），但限流只是兜底，弱初始密码必须靠上述流程消除。

**反向代理 / ForwardedHeaders：V1 范围外。** 当前按直连 HTTP 部署（或由外部网关终结 TLS 后直转）。限流与匿名意向按 `RemoteIpAddress` 分区——若未来置于反代之后，需配置 `ForwardedHeaders` 把真实客户端 IP 透传，否则所有请求会共享代理 IP 的限流配额；此项连同 HTTPS/TLS 终结留待后续版本。
