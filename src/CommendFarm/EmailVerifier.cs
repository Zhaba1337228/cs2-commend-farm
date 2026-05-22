using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CommendFarm;

public class EmailVerifier
{
    private readonly ILogger<EmailVerifier> _logger;
    private const string IMAP_HOST = "imap.notletters.com";
    private const int IMAP_PORT = 993;

    public EmailVerifier(ILogger<EmailVerifier> logger)
    {
        _logger = logger;
    }

    public async Task<string?> GetSteamGuardCodeAsync(
        string email,
        string emailPassword,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[Email] Checking {Email} for Steam Guard code...", email);

        using var client = new ImapClient();

        try
        {
            await client.ConnectAsync(IMAP_HOST, IMAP_PORT, true, ct);
            await client.AuthenticateAsync(email, emailPassword, ct);

            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

            // Search for recent emails from Steam (last 5 minutes)
            var sinceDate = DateTime.UtcNow.AddMinutes(-10);

            // Try to find unread messages from Steam first
            var steamQuery = SearchQuery
                .FromContains("steampowered.com")
                .And(SearchQuery.DeliveredAfter(sinceDate));

            var uids = await inbox.SearchAsync(steamQuery, ct);

            if (uids.Count == 0)
            {
                // Also try "noreply@steampowered.com"
                steamQuery = SearchQuery
                    .FromContains("noreply@steampowered.com")
                    .And(SearchQuery.DeliveredAfter(sinceDate));

                uids = await inbox.SearchAsync(steamQuery, ct);
            }

            if (uids.Count == 0)
            {
                // Broader search: subject contains "Steam"
                var subjectQuery = SearchQuery
                    .SubjectContains("Steam")
                    .And(SearchQuery.DeliveredAfter(sinceDate));

                uids = await inbox.SearchAsync(subjectQuery, ct);
            }

            _logger.LogDebug("[Email] Found {Count} matching messages", uids.Count);

            // Check messages from newest to oldest
            foreach (var uid in uids.OrderByDescending(u => u.Id))
            {
                var message = await inbox.GetMessageAsync(uid, ct);
                var code = ExtractCodeFromMessage(message);

                if (code != null)
                {
                    _logger.LogInformation("[Email] Steam Guard code found: {Code}", code);
                    await client.DisconnectAsync(true, ct);
                    return code;
                }
            }

            _logger.LogWarning("[Email] No Steam Guard code found in {Email}", email);
            await client.DisconnectAsync(true, ct);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Email] IMAP error for {Email}: {Msg}", email, ex.Message);
            return null;
        }
    }

    private static string? ExtractCodeFromMessage(MimeMessage message)
    {
        var subject = message.Subject ?? "";
        var body = message.HtmlBody ?? message.TextBody ?? "";

        // Steam sends 5-character alphanumeric code
        // Look for patterns like: "XXXXX" or "Your code: XXXXX"

        // Try subject first
        var subjectMatch = System.Text.RegularExpressions.Regex.Match(
            subject, @"\b([A-Z0-9]{5})\b");
        if (subjectMatch.Success)
            return subjectMatch.Groups[1].Value;

        // Try body - look for 5-char uppercase code
        var bodyMatches = System.Text.RegularExpressions.Regex.Matches(
            body, @"\b([A-Z0-9]{5})\b");

        foreach (System.Text.RegularExpressions.Match match in bodyMatches)
        {
            var code = match.Groups[1].Value;
            // Skip common false positives
            if (code == "EMAIL" || code == "STEAM" || code == "GUARD" ||
                code == "HTTPS" || code == "CLICK" || code == "USING")
                continue;
            return code;
        }

        return null;
    }
}