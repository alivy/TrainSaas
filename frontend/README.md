# TrainSaas 前端

Vue 3 + TypeScript + Element Plus 单页应用，一个应用承载四个区域：

| 区域 | 路径 | 说明 |
|---|---|---|
| 平台超管后台 | `/platform/**` | 机构开通/停用/重置密码、平台改密 |
| 机构后台 | `/admin/**` | 四角色（管理员/销售/老师/前台）工作台与业务页；老师点名页移动优先 |
| 机构公开介绍页 | `/org/:code` | 匿名访问，产品展示 + 报名意向（限流 5 次/分钟） |
| 家长端 H5 | `/parent/**` | 手机号+密码登录，孩子/课时包/记录/课表/改密 |

## 开发

```bash
# 1. 先启动后端（http://localhost:5080，见仓库根 README）
dotnet run --project ../backend/src/TrainSaas.Api

# 2. 启动前端
npm install
npm run dev        # http://localhost:5173
```

- 开发期所有 `/api` 请求经 Vite 代理转发到 `http://localhost:5080`（同源，无 CORS 问题）。
- **端口被占时 Vite 自动递增**（5174/5175…），以终端实际输出为准；代理与 CORS 不受端口影响。

## 测试与构建

```bash
npm run test       # vitest：http 拦截器/认证 store/格式化/API 参数契约/点名状态机等逻辑层
npm run build      # vue-tsc 类型检查 + vite 构建 → dist/
npm run preview    # 本地预览生产构建（注意：preview 不带 API 代理，仅供静态与路由检查）
```

## 生产部署

`npm run build` 产出 `dist/` 纯静态文件，任意静态服务器托管，并把 `/api` 反向代理到后端。nginx 示例：

```nginx
server {
    listen 80;
    server_name saas.example.com;

    root /var/www/trainsaas/dist;
    index index.html;

    # SPA 路由回退
    location / {
        try_files $uri $uri/ /index.html;
    }

    # API 反代到后端
    location /api/ {
        proxy_pass http://127.0.0.1:5080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;          # 限流按直连 IP 分区，反代场景需后端配合 ForwardedHeaders（V1 范围外）
        client_max_body_size 8m;                           # 凭据图片 ≤5MB + 余量
    }
}
```

## 契约要点（与后端约定，改任何一条都要两端同步）

- JSON 一律 camelCase；分页 `{total, items}`，参数 `page`(1 起)/`pageSize`(≤100)。
- **枚举一律名称字符串**（`"Fixed"`、`"Present"`…）：查询参数传数字串会被后端 400；点名 body 的 mark 大小写敏感。
- 日期查询参数 `yyyy-MM-dd`；时间提交 `"HH:mm"`；响应 TimeOnly 为 `"18:00:00"`、DateTime 为本地 ISO 串。
- 错误为 ProblemDetails `{status,title,detail}`，`detail` 即用户文案（http.ts 拦截器统一 toast）。
- 三个 token 分区存放：`ts_platform_token` / `ts_staff_token`(+`ts_staff_profile`) / `ts_parent_token`；四个 axios 实例（platformApi/orgApi/parentApi/publicApi）互不串 token；401 只清本区域并跳本区域登录页。
- 受保护图片（支付凭据）必须带 Authorization → 走 `utils/blobAuth.ts` fetch→objectURL（`AuthedImage` 组件）；公开图片（logo/封面）直接 `<img src="/api/files/images/{path}">`。
- API 单一契约源：`src/api/types.ts` + 各 `src/api/*.ts` 模块；页面禁止手拼 URL/参数。

## 目录结构

```
src/
├── api/          # types.ts（契约）+ http.ts（4 实例+拦截器）+ platform/org/public/parent.ts
├── stores/       # platformAuth / staffAuth / parentAuth（pinia）
├── utils/        # format.ts（金额/日期/枚举标签/Tag色）、blobAuth.ts、fileCheck.ts
├── layouts/      # AdminLayout（§8 角色菜单）/ PlatformLayout / MobileLayout / BlankLayout
├── components/   # PagedTable / EnumTag / ImagePreview / AuthedImage / VoucherUploader / ForbiddenPage
└── areas/        # platform / admin / org / parent 四区域页面
tests/            # vitest 逻辑层测试
```

## 演示账号（后端 SeedDemoData=true 时可用，密码均 123456）

- 平台超管：`admin`（用户名）
- 机构端：管理员 `13900000001` · 销售 `13900000002` · 老师 `13900000003` · 前台 `13900000004`
- 家长端：`13800001111`（张小明/张小红）· `13800002222`（李华）
