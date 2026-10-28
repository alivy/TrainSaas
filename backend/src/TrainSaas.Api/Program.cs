using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

// Claim-name fidelity (Global Constraints): the JWT handlers must never rewrite
// the fixed claim names (user_type, institution_id, staff_id, role, phone, name)
// into long .NET claim-type URIs on read. .NET 10's JwtBearer validates with
// JsonWebTokenHandler by default, and JwtSecurityTokenHandler is kept in sync.
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<IPasswordHasherService, PasswordHasherService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<InstitutionProvisioningService>();
builder.Services.AddScoped<StaffService>();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<StudentService>();
builder.Services.AddScoped<ClassTemplateService>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<SessionGenerationService>();
builder.Services.AddScoped<IDeductionService, DeductionService>();
builder.Services.AddScoped<AttendanceService>();
builder.Services.AddScoped<ConsumptionService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<IntentService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<ParentService>();
builder.Services.AddScoped<FileStorageService>();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Defense in depth for claim-name fidelity: keep inbound claims as-is.
        options.MapInboundClaims = false;

        // Read the Jwt section lazily: options are built at first use, after the
        // host has layered all configuration sources (e.g. WebApplicationFactory
        // overrides Jwt:Key in integration tests). Reading it eagerly at the
        // top level would freeze the pre-override value here.
        var jwtKey = builder.Configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key 未配置");

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            // Global Constraints: 60s instead of the 5-minute default.
            ClockSkew = TimeSpan.FromSeconds(60),
        };
    });

builder.Services.AddAuthorization(AuthPolicies.AddPolicies);

// CORS for the frontend origin(s) configured in Cors:Origins.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Fixed-window rate-limit policies (429 on excess, partitioned per remote IP):
// - "intent" (5/minute): the anonymous purchase-intent submission route
//   ([EnableRateLimiting("intent")] on PublicOrgsController).
// - "login" (I6, final review): the three anonymous login routes
//   (platform/org/parent, [EnableRateLimiting("login")]) — the network-wide
//   initial password 123456 must not be brute-forceable. PermitLimit is read
//   lazily inside the partition factory (Task 3 lesson: test-overridable
//   config must not be frozen at the top level); production default is 10
//   requests/minute/IP. Shared-fixture integration hosts raise it via
//   RateLimit:LoginPermitLimit (their login counts far exceed 10/minute);
//   the dedicated LoginRateLimitTests pin the real 429 behavior at 10.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("intent", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
            }));
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimit:LoginPermitLimit", 10),
                Window = TimeSpan.FromMinutes(1),
            }));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// R16a: Swagger UI (middleware only) over the built-in OpenAPI document, so
// http://host/swagger is browsable wherever /openapi/v1.json is mapped
// (Development). All environments per ruling — harmless on single-host deploys.
app.UseSwaggerUI(options =>
    options.SwaggerEndpoint("/openapi/v1.json", "TrainSaas API"));

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseCors();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/healthz", () => "ok");

// Apply pending migrations, then seed the minimal demo data when enabled
// (SeedDemoData=true, e.g. in the Development environment).
using (var scope = app.Services.CreateScope())
{
    // SQLite creates the database file but not its parent directory, so make
    // sure the directory of the configured data source (data/ for the default
    // "Data Source=data/trainsaas.db") exists before migrating.
    var dataSource = new SqliteConnectionStringBuilder(app.Configuration.GetConnectionString("Default")).DataSource;
    if (!string.IsNullOrWhiteSpace(dataSource) && dataSource != ":memory:")
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    if (app.Configuration.GetValue<bool>("SeedDemoData"))
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        SeedData.SeedMinimalAsync(db, hasher).GetAwaiter().GetResult();
        // Full demo seed (Task 16): business showcase rows for the demo
        // institution, idempotent, under the same gate.
        SeedData.SeedDemoDataAsync(db, hasher).GetAwaiter().GetResult();
    }
}

app.Run();

public partial class Program { }
