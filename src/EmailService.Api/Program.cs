using System.Reflection;
using EmailService.Contracts;
using EmailService.Infrastructure;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using StackExchange.Redis;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Google.Apis.Gmail.v1;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;

var builder = WebApplication.CreateBuilder(args);

var pg = builder.Configuration.GetConnectionString("Postgres")
         ?? "Host=postgres;Port=5432;Database=emails;Username=postgres;Password=postgres";

var redisConn = builder.Configuration["Redis:Connection"] ?? "redis:6379";
var rabbitHost = builder.Configuration["Rabbit:Host"] ?? "rabbitmq";
var rabbitUser = builder.Configuration["Rabbit:User"] ?? "guest";
var rabbitPass = builder.Configuration["Rabbit:Pass"] ?? "guest";
var rabbitUri = new Uri($"amqp://{rabbitUser}:{rabbitPass}@{rabbitHost}/");

// ---------- INFRA ----------
builder.Services.AddDbContext<EmailDbContext>(o => o.UseNpgsql(pg));

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Email Service API",
        Version = "v1",
        Description = "API para enfileirar, listar e consultar emails enviados pelo pipeline."
    });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "Please enter a valid token",
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        BearerFormat = "JWT",
        Scheme = "Bearer"
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

// Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "Cookies";
    options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
})
.AddCookie()
.AddGoogle(options =>
{
    options.ClientId = builder.Configuration["Authentication:Google:ClientId"];
    options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
    options.Scope.Add("https://www.googleapis.com/auth/gmail.readonly");
    options.CallbackPath = "/auth/google";
});

builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    });
});

builder.Services.AddHealthChecks()
    .AddNpgSql(pg)
    .AddRedis(redisConn)
    .AddRabbitMQ(_ =>
    {
        var factory = new ConnectionFactory { Uri = rabbitUri };
        return factory.CreateConnectionAsync().GetAwaiter().GetResult();
    });

// Redis connection multiplexer
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConn));

// ---------- MASS TRANSIT ----------
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(rabbitHost, "/", h =>
        {
            h.Username(rabbitUser);
            h.Password(rabbitPass);
        });
    });
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("EmailService.Api"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("EmailMetrics")
        .AddOtlpExporter(o => o.Endpoint =
            new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://otel-collector:4317")))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint =
            new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://otel-collector:4317")));

var app = builder.Build();

app.UseAuthentication();
app.UseCors();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
    var sql = """
    CREATE TABLE IF NOT EXISTS "Emails" (
        "Id" uuid PRIMARY KEY,
        "From" varchar(512) NOT NULL,
        "To" varchar(512) NOT NULL,
        "Subject" varchar(256) NOT NULL,
        "Body" text NOT NULL,
        "IdempotencyKey" varchar(128) NOT NULL,
        "Status" int NOT NULL,
        "CreatedAtUtc" timestamp without time zone NOT NULL,
        "SentAtUtc" timestamp without time zone NULL
    );
    CREATE INDEX IF NOT EXISTS idx_emails_idemp ON "Emails"("IdempotencyKey");
    CREATE TABLE IF NOT EXISTS "EmailAttempts" (
        "Id" bigserial PRIMARY KEY,
        "EmailId" uuid NOT NULL REFERENCES "Emails"("Id") ON DELETE CASCADE,
        "AttemptNumber" int NOT NULL,
        "Success" boolean NOT NULL,
        "Error" text NULL,
        "TimestampUtc" timestamp without time zone NOT NULL
    );
    CREATE TABLE IF NOT EXISTS "ReceivedEmails" (
        "Id" uuid PRIMARY KEY,
        "To" varchar(512) NOT NULL,
        "From" varchar(512) NOT NULL,
        "Subject" varchar(256) NOT NULL,
        "Body" text NOT NULL,
        "ReceivedAtUtc" timestamp without time zone NOT NULL,
        "MessageId" varchar(256) UNIQUE NOT NULL
    );
    CREATE INDEX IF NOT EXISTS idx_received_messageid ON "ReceivedEmails"("MessageId");
    """;
    db.Database.ExecuteSqlRaw(sql);
}

// ---------- ENDPOINTS ----------
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/health"));

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Email Service API v1");
    c.RoutePrefix = "swagger";
});

// Google Authentication
app.MapGet("/auth/google", () =>
    Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, new[] { GoogleDefaults.AuthenticationScheme }));
app.MapGet("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Redirect("/");
});

// POST /emails -> DB + POST RabbitMQ
app.MapPost("/emails", [Authorize] async (EmailDbContext db, IPublishEndpoint bus, IConnectionMultiplexer redis, EmailDto dto, HttpContext ctx) =>
{
    var userEmail = ctx.User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")?.Value;
    if (string.IsNullOrEmpty(userEmail))
        return Results.Unauthorized();

    // idempotência na API (opcional)
    if (!string.IsNullOrWhiteSpace(dto.IdempotencyKey))
    {
        var key = $"idem:api:{dto.IdempotencyKey}";
        var set = await redis.GetDatabase().StringSetAsync(key, "1", TimeSpan.FromHours(1), when: When.NotExists);
        if (!set) return Results.Conflict(new { message = "Duplicado por idempotencyKey" });
    }

    var email = new Email
    {
        Id = Guid.NewGuid(),
        From = userEmail,
        To = dto.To,
        Subject = dto.Subject,
        Body = dto.Body,
        IdempotencyKey = dto.IdempotencyKey ?? string.Empty,
        Status = EmailStatus.Queued,
        CreatedAtUtc = DateTime.UtcNow
    };
    db.Emails.Add(email);
    await db.SaveChangesAsync();

    await bus.Publish(new EmailRequestMessage(
        email.Id, email.From, email.To, email.Subject, email.Body, email.IdempotencyKey, email.CreatedAtUtc));

    return Results.Created($"/emails/{email.Id}", new { email.Id, status = email.Status.ToString() });
});

// status + history
app.MapGet("/emails/{id:guid}", async (EmailDbContext db, Guid id) =>
{
    var email = await db.Emails.Include(e => e.Attempts).FirstOrDefaultAsync(e => e.Id == id);
    return email is null ? Results.NotFound() : Results.Ok(new
    {
        email.Id,
        email.From,
        email.To,
        email.Subject,
        status = email.Status.ToString(),
        sentAtUtc = email.SentAtUtc,
        attempts = email.Attempts
            .OrderBy(a => a.AttemptNumber)
            .Select(a => new { a.AttemptNumber, a.Success, a.Error, a.TimestampUtc })
    });
});

// Lista de emails enviados pelo usuário autenticado
app.MapGet("/emails/sent", [Authorize] async (EmailDbContext db, HttpContext ctx) =>
{
    var userEmail = ctx.User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")?.Value;
    if (string.IsNullOrEmpty(userEmail))
        return Results.Unauthorized();

    var emails = await db.Emails
        .Where(e => e.From == userEmail)
        .OrderByDescending(e => e.CreatedAtUtc)
        .Select(e => new
        {
            e.Id,
            e.To,
            e.Subject,
            status = e.Status.ToString(),
            e.CreatedAtUtc,
            e.SentAtUtc
        })
        .ToListAsync();

    return Results.Ok(emails);
});

// Sincronizar emails recebidos da caixa de entrada do Gmail
app.MapPost("/emails/sync-received", [Authorize] async (EmailDbContext db, HttpContext ctx) =>
{
    var userEmail = ctx.User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")?.Value;
    if (string.IsNullOrEmpty(userEmail))
        return Results.Unauthorized();

    var accessToken = await ctx.GetTokenAsync("access_token");
    if (string.IsNullOrEmpty(accessToken))
        return Results.Unauthorized();

    var credential = GoogleCredential.FromAccessToken(accessToken);
    var service = new GmailService(new BaseClientService.Initializer
    {
        HttpClientInitializer = credential,
        ApplicationName = "Email Pipeline"
    });

    try
    {
        var request = service.Users.Messages.List("me");
        request.Q = "in:inbox";  // Apenas caixa de entrada
        request.MaxResults = 10;  // Limitar para teste
        var messages = await request.ExecuteAsync();

        foreach (var msg in messages.Messages ?? new List<Google.Apis.Gmail.v1.Data.Message>())
        {
            // Verificar se já existe
            var existing = await db.ReceivedEmails.FirstOrDefaultAsync(e => e.MessageId == msg.Id);
            if (existing != null) continue;

            // Buscar detalhes da mensagem
            var msgRequest = service.Users.Messages.Get("me", msg.Id);
            var message = await msgRequest.ExecuteAsync();

            var subject = message.Payload.Headers.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "Sem assunto";
            var from = message.Payload.Headers.FirstOrDefault(h => h.Name == "From")?.Value ?? "Desconhecido";
            var body = message.Payload.Body?.Data ?? message.Payload.Parts?.FirstOrDefault()?.Body?.Data ?? "";
            body = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/')));

            var receivedEmail = new ReceivedEmail
            {
                Id = Guid.NewGuid(),
                To = userEmail,
                From = from,
                Subject = subject,
                Body = body,
                ReceivedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)message.InternalDate).UtcDateTime,
                MessageId = msg.Id
            };

            db.ReceivedEmails.Add(receivedEmail);
        }

        await db.SaveChangesAsync();
        return Results.Ok(new { message = "Emails sincronizados com sucesso" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Erro ao sincronizar: {ex.Message}");
    }
});

// Lista de emails recebidos (caixa de entrada)
app.MapGet("/emails/received", [Authorize] async (EmailDbContext db, HttpContext ctx) =>
{
    var userEmail = ctx.User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")?.Value;
    if (string.IsNullOrEmpty(userEmail))
        return Results.Unauthorized();

    var emails = await db.ReceivedEmails
        .Where(e => e.To == userEmail)
        .OrderByDescending(e => e.ReceivedAtUtc)
        .Select(e => new { e.Id, e.From, e.Subject, e.Body, e.ReceivedAtUtc })
        .ToListAsync();

    return Results.Ok(emails);
});

app.Run();

record EmailDto(string To, string Subject, string Body, string? IdempotencyKey);
