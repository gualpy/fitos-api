using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Email;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Email;

public sealed class PostgresEmailSettingsStore(FitOsDbContext db) : IEmailSettingsStore
{
    public async Task<StoredEmailSettings?> GetAsync(CancellationToken cancellationToken)
    {
        var row = (await db.Database
            .SqlQuery<Row>($"SELECT host, port, security, username, from_address, password_enc, password_hint, updated_at FROM email_settings_get()")
            .ToListAsync(cancellationToken)).FirstOrDefault();

        return row is null
            ? null
            : new StoredEmailSettings(
                row.Host,
                row.Port,
                SmtpSecurityWire.TryParse(row.Security, out var security) ? security : SmtpSecurity.StartTls,
                row.Username,
                row.FromAddress,
                row.PasswordEnc,
                row.PasswordHint,
                row.UpdatedAt);
    }

    public async Task SaveAsync(string host, int port, SmtpSecurity security, string username, string fromAddress, byte[]? passwordEnc, string? passwordHint, Guid actorId, CancellationToken cancellationToken)
    {
        var wire = security.ToWire();
        await db.Database.ExecuteSqlAsync(
            $"SELECT email_settings_save({host}, {port}, {wire}, {username}, {fromAddress}, {passwordEnc}, {passwordHint}, {actorId})",
            cancellationToken);
    }

    private sealed class Row
    {
        [Column("host")]
        public string Host { get; set; } = string.Empty;

        [Column("port")]
        public int Port { get; set; }

        [Column("security")]
        public string Security { get; set; } = string.Empty;

        [Column("username")]
        public string Username { get; set; } = string.Empty;

        [Column("from_address")]
        public string FromAddress { get; set; } = string.Empty;

        [Column("password_enc")]
        public byte[]? PasswordEnc { get; set; }

        [Column("password_hint")]
        public string? PasswordHint { get; set; }

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
