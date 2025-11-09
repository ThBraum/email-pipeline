using System.Diagnostics;
using System.Diagnostics.Metrics;
using EmailService.Contracts;
using EmailService.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace EmailService.Worker;

public class EmailRequestConsumer : IConsumer<EmailRequestMessage>
{
    public const string MeterName = "EmailWorkerMetrics";
    public const string ActivitySourceName = "EmailWorker";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<int> EmailsProcessed = Meter.CreateCounter<int>("emails_processed");
    private static readonly Counter<int> EmailsFailed = Meter.CreateCounter<int>("emails_failed");

    private readonly ILogger<EmailRequestConsumer> _logger;
    private readonly EmailDbContext _db;
    private readonly IEmailSender _sender;

    public EmailRequestConsumer(ILogger<EmailRequestConsumer> logger, EmailDbContext db, IEmailSender sender)
    {
        _logger = logger;
        _db = db;
        _sender = sender;
    }

    public async Task Consume(ConsumeContext<EmailRequestMessage> context)
    {
        using var activity = ActivitySource.StartActivity("ProcessEmail", ActivityKind.Consumer);
        var msg = context.Message;
        activity?.SetTag("email.id", msg.Id);
        activity?.SetTag("email.to", msg.To);

        // Carrega email do banco (foi criado pela API)
        var email = await _db.Emails.FirstOrDefaultAsync(e => e.Id == msg.Id, context.CancellationToken);
        if (email is null)
        {
            _logger.LogWarning("Email {Id} não encontrado no banco.", msg.Id);
            EmailsFailed.Add(1);
            return;
        }

        var attemptNumber = await _db.Attempts.CountAsync(a => a.EmailId == email.Id, context.CancellationToken) + 1;

        var (success, error) = await _sender.SendAsync(msg.To, msg.Subject, msg.Body, context.CancellationToken);

        _db.Attempts.Add(new EmailAttempt
        {
            EmailId = email.Id,
            AttemptNumber = attemptNumber,
            Success = success,
            Error = error,
            TimestampUtc = DateTime.UtcNow
        });

        email.Status = success ? EmailStatus.Sent : EmailStatus.Failed;
        email.SentAtUtc = success ? DateTime.UtcNow : email.SentAtUtc;
        await _db.SaveChangesAsync(context.CancellationToken);

        if (success)
        {
            EmailsProcessed.Add(1);
            _logger.LogInformation("Email {Id} enviado para {To}.", email.Id, email.To);
        }
        else
        {
            EmailsFailed.Add(1);
            _logger.LogError("Falha ao enviar email {Id}: {Error}", email.Id, error);
        }
    }
}
