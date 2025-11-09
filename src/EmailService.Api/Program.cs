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

var builder = WebApplication.CreateBuilder(args);

var pg = builder.Configuration.GetConnectionString("Postgres")
         ?? "Host=postgres;Port=5432;Database=emails;Username=postgres;Password=postgres";

var redisConn = builder.Configuration["Redis:Connection"] ?? "redis:6379";
var rabbitHost = builder.Configuration["Rabbit:Host"] ?? "rabbitmq";
var rabbitUser = builder.Configuration["Rabbit:User"] ?? "guest";
var rabbitPass = builder.Configuration["Rabbit:Pass"] ?? "guest";
var rabbitUri  = new Uri($"amqp://{rabbitUser}:{rabbitPass}@{rabbitHost}/");

// ---------- INFRA ----------
builder.Services.AddDbContext<EmailDbContext>(o => o.UseNpgsql(pg));

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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
    var sql = """
    CREATE TABLE IF NOT EXISTS "Emails" (
        "Id" uuid PRIMARY KEY,
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
    """;
    db.Database.ExecuteSqlRaw(sql);
}

// ---------- ENDPOINTS ----------
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/health"));

// POST /emails -> DB + POST RabbitMQ
app.MapPost("/emails", async (EmailDbContext db, IPublishEndpoint bus, IConnectionMultiplexer redis, EmailDto dto) =>
{
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
        email.Id, email.To, email.Subject, email.Body, email.IdempotencyKey, email.CreatedAtUtc));

    return Results.Created($"/emails/{email.Id}", new { email.Id, status = email.Status.ToString() });
});

// status + history
app.MapGet("/emails/{id:guid}", async (EmailDbContext db, Guid id) =>
{
    var email = await db.Emails.Include(e => e.Attempts).FirstOrDefaultAsync(e => e.Id == id);
    return email is null ? Results.NotFound() : Results.Ok(new
    {
        email.Id,
        email.To,
        email.Subject,
        status = email.Status.ToString(),
        sentAtUtc = email.SentAtUtc,
        attempts = email.Attempts
            .OrderBy(a => a.AttemptNumber)
            .Select(a => new { a.AttemptNumber, a.Success, a.Error, a.TimestampUtc })
    });
});

app.Run();

record EmailDto(string To, string Subject, string Body, string? IdempotencyKey);
