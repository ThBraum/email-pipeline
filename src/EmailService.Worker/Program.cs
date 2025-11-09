using EmailService.Infrastructure;
using EmailService.Worker;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

// ---- CONFIG ----
var pg = builder.Configuration.GetConnectionString("Postgres")
		 ?? "Host=postgres;Port=5432;Database=emails;Username=postgres;Password=postgres";
var rabbitHost = builder.Configuration["Rabbit:Host"] ?? "rabbitmq";
var rabbitUser = builder.Configuration["Rabbit:User"] ?? "guest";
var rabbitPass = builder.Configuration["Rabbit:Pass"] ?? "guest";
var smtpHost = builder.Configuration["Smtp:Host"] ?? "mailhog"; // dev mailhog
var smtpPort = int.TryParse(builder.Configuration["Smtp:Port"], out var p) ? p : 1025;
var smtpUser = builder.Configuration["Smtp:User"]; // optional
var smtpPass = builder.Configuration["Smtp:Pass"]; // optional

// ---- INFRA / EF ----
builder.Services.AddDbContext<EmailDbContext>(o => o.UseNpgsql(pg));

// ---- EMAIL SENDER ----
builder.Services.AddScoped<IEmailSender>(_ => new SmtpEmailSender(smtpHost, smtpPort, smtpUser, smtpPass));

// ---- MASS TRANSIT ----
builder.Services.AddMassTransit(x =>
{
	x.AddConsumer<EmailRequestConsumer>();
	x.UsingRabbitMq((ctx, cfg) =>
	{
		cfg.Host(rabbitHost, "/", h =>
		{
			h.Username(rabbitUser);
			h.Password(rabbitPass);
		});
		// Configura endpoints convencionais (fila para o consumer)
		cfg.ConfigureEndpoints(ctx);
	});
});

// ---- OpenTelemetry (mínimo para métricas/traces do worker) ----
builder.Services.AddOpenTelemetry()
	.ConfigureResource(r => r.AddService("EmailService.Worker"))
	.WithMetrics(m => m
		.AddRuntimeInstrumentation()
		.AddMeter(EmailRequestConsumer.MeterName)
		.AddOtlpExporter(o => o.Endpoint =
			new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://otel-collector:4317")))
	.WithTracing(t => t
		.AddSource(EmailRequestConsumer.ActivitySourceName)
		.AddOtlpExporter(o => o.Endpoint =
			new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://otel-collector:4317")));

// Opcional: manter um HostedService simples para heartbeat de log
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
