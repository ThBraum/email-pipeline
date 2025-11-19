using System.Net.Mail;
using System.Net;
using System.Threading.Tasks;

namespace EmailService.Worker;

public interface IEmailSender
{
    Task<(bool success, string? error)> SendAsync(string from, string to, string subject, string body, CancellationToken ct = default);
}

public class SmtpEmailSender : IEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _pass;
    private readonly bool _enableSsl;
    private readonly string _from;

    public SmtpEmailSender(string host, int port, string? user, string? pass, bool enableSsl, string? from)
    {
        _host = host;
        _port = port;
        _user = user;
        _pass = pass;
        _enableSsl = enableSsl;
        _from = string.IsNullOrWhiteSpace(from) ? (user ?? "no-reply@example.com") : from;
    }

    public async Task<(bool success, string? error)> SendAsync(string from, string to, string subject, string body, CancellationToken ct = default)
    {
        try
        {
            using var client = new SmtpClient(_host, _port)
            {
                EnableSsl = _enableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = (!string.IsNullOrEmpty(_user) && !string.IsNullOrEmpty(_pass))
                    ? new NetworkCredential(_user, _pass)
                    : null
            };

            using var mail = new MailMessage(from, to, subject, body);
            await client.SendMailAsync(mail, ct);
            return (true, null);
        }
        catch (SmtpException ex)
        {
            return (false, $"SMTP error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
