using MailAggregator.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MailAggregator.Core.Data;

public class MailAggregatorDbContext : DbContext
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<MailFolder> Folders => Set<MailFolder>();
    public DbSet<EmailMessage> Messages => Set<EmailMessage>();
    public DbSet<EmailAttachment> Attachments => Set<EmailAttachment>();

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

        foreach (var entry in ChangeTracker.Entries<EmailMessage>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CachedAt = now;
            }
        }
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
            entity.HasIndex(e => e.EmailAddress);
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
        });
    }
}
