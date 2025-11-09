using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace EmailService.Infrastructure;

public enum EmailStatus { Queued, Sent, Failed }

[Table("Emails")]
public class Email
{
    [Key] public Guid Id { get; set; }
    [MaxLength(512)] public string To { get; set; } = default!;
    [MaxLength(256)] public string Subject { get; set; } = default!;
    public string Body { get; set; } = default!;
    [MaxLength(128)] public string IdempotencyKey { get; set; } = default!;
    public EmailStatus Status { get; set; } = EmailStatus.Queued;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
    public List<EmailAttempt> Attempts { get; set; } = new();
}

[Table("EmailAttempts")]
public class EmailAttempt
{
    [Key] public long Id { get; set; }
    public Guid EmailId { get; set; }
    public Email Email { get; set; } = default!;
    public int AttemptNumber { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public class EmailDbContext : DbContext
{
    public EmailDbContext(DbContextOptions<EmailDbContext> options) : base(options) { }
    public DbSet<Email> Emails => Set<Email>();
    public DbSet<EmailAttempt> Attempts => Set<EmailAttempt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Email>().ToTable("Emails");
        b.Entity<Email>().HasIndex(x => x.IdempotencyKey).IsUnique(false);
        b.Entity<EmailAttempt>().ToTable("EmailAttempts");
        b.Entity<EmailAttempt>().HasOne(a => a.Email).WithMany(e => e.Attempts).HasForeignKey(a => a.EmailId);
    }
}
