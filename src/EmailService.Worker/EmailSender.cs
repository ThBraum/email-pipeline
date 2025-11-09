using System.Net.Mail;
using System.Net;
using System.Threading.Tasks;

namespace EmailService.Worker;

public interface IEmailSender
{
    Task<(bool success, string? error)> SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

public class SmtpEmailSender : IEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _pass;

    public SmtpEmailSender(string host, int port, string? user, string? pass)
    {
        _host = host;
        _port = port;
        _user = user;
        _pass = pass;
    }

    public async Task<(bool success, string? error)> SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        try
        {
            using var client = new SmtpClient(_host, _port);
            client.EnableSsl = false; // Mailhog não usa SSL; produção pode ligar.
            if (!string.IsNullOrEmpty(_user) && !string.IsNullOrEmpty(_pass))
            {
                client.Credentials = new NetworkCredential(_user, _pass);
            }
            var mail = new MailMessage("no-reply@example.com", to, subject, body);
            await client.SendMailAsync(mail, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
