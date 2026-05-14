using MailAggregator.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MailAggregator.Core.Data;

public class MailAggregatorDbContext : DbContext
{
    /// <summary>
    /// Converts DateTimeOffset to UTC ticks (long) for SQLite compatibility.
    /// SQLite doesn't support DateTimeOffset in ORDER BY clauses natively.
    /// </summary>
    private class DateTimeOffsetToLongConverter : ValueConverter<DateTimeOffset, long>
    {
        public DateTimeOffsetToLongConverter() : base(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero))
        { }
    }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<MailFolder> Folders => Set<MailFolder>();
    public DbSet<EmailMessage> Messages => Set<EmailMessage>();
    public DbSet<EmailAttachment> Attachments => Set<EmailAttachment>();
    public DbSet<TwoFactorAccount> TwoFactorAccounts => Set<TwoFactorAccount>();

    public MailAggregatorDbContext(DbContextOptions<MailAggregatorDbContext> options)
        : base(options)
    {
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTimestamps()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Account>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<TwoFactorAccount>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<EmailMessage>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CachedAt = now;
            }
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite doesn't support DateTimeOffset in ORDER BY or WHERE clauses.
        // Store all DateTimeOffset values as UTC ticks (long) for correct sorting.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToLongConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Account
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmailAddress).IsRequired().HasMaxLength(256);
            entity.Property(e => e.DisplayName).HasMaxLength(256);
            entity.Property(e => e.ImapHost).IsRequired().HasMaxLength(256);
            entity.Property(e => e.SmtpHost).IsRequired().HasMaxLength(256);
            entity.Property(e => e.ProxyHost).HasMaxLength(256);
            entity.HasIndex(e => e.EmailAddress).IsUnique();
        });

        // MailFolder
        modelBuilder.Entity<MailFolder>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.FullName).IsRequired().HasMaxLength(512);
            entity.HasOne(e => e.Account)
                .WithMany(a => a.Folders)
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.AccountId, e.FullName }).IsUnique();
        });

        // EmailMessage
        modelBuilder.Entity<EmailMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FromAddress).IsRequired().HasMaxLength(256);
            entity.Property(e => e.FromName).HasMaxLength(256);
            entity.Property(e => e.Subject).HasMaxLength(1024);
            entity.Property(e => e.MessageId).HasMaxLength(512);
            entity.Property(e => e.PreviewText).HasMaxLength(512);
            entity.HasOne(e => e.Account)
                .WithMany(a => a.Messages)
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Folder)
                .WithMany(f => f.Messages)
                .HasForeignKey(e => e.FolderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.FolderId, e.Uid }).IsUnique();
            entity.HasIndex(e => e.AccountId);
            entity.HasIndex(e => e.DateSent);
            entity.HasIndex(e => new { e.FolderId, e.DateSent });
        });

        // TwoFactorAccount
        modelBuilder.Entity<TwoFactorAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Issuer).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(256);
            entity.Property(e => e.EncryptedSecret).IsRequired();
        });

        // EmailAttachment
        modelBuilder.Entity<EmailAttachment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(512);
            entity.Property(e => e.ContentType).HasMaxLength(256);
            entity.Property(e => e.ContentId).HasMaxLength(256);
            entity.HasOne(e => e.EmailMessage)
                .WithMany(m => m.Attachments)
                .HasForeignKey(e => e.EmailMessageId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.EmailMessageId);
        });
    }
}
