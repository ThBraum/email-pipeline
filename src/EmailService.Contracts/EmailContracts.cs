namespace EmailService.Contracts;

public record EmailRequestMessage(
    Guid Id,
    string From,
    string To,
    string Subject,
    string Body,
    string IdempotencyKey,
    DateTime CreatedAtUtc);

public record EmailResultMessage(
    Guid Id,
    bool Success,
    string? Error,
    int Attempt,
    DateTime TimestampUtc);
