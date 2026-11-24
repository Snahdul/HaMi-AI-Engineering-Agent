using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// The issue supplies bounded task context, NEVER privileges. The reviewed
/// versioned contract still owns source file, sandbox, retry and model limits.
/// </summary>
internal sealed record ApprovedDevelopmentIssue(
    int Number, string Title, string Goal, string[] Criteria)
{
    internal const string ApprovedOwner = "Snahdul";
    internal const string ActiveLabel = "workflow:in-progress";

    public static ApprovedDevelopmentIssue Parse(
        JsonElement issue, DevelopmentAgentContract contract)
    {
        if (issue.ValueKind != JsonValueKind.Object
            || issue.TryGetProperty("pull_request", out _)
            || issue.GetProperty("state").GetString() != "open"
            || issue.GetProperty("user").GetProperty("login").GetString() != ApprovedOwner)
            throw new InvalidOperationException("Issue is not an open, owner-authored task.");
        JsonElement labels = issue.GetProperty("labels");
        if (labels.ValueKind != JsonValueKind.Array ||
            !labels.EnumerateArray().Any(label =>
                label.TryGetProperty("name", out JsonElement name)
                && name.GetString() == ActiveLabel))
            throw new InvalidOperationException("Issue is not authorized with workflow:in-progress.");

        int number = issue.GetProperty("number").GetInt32();
        string title = issue.GetProperty("title").GetString() ?? "";
        string body = issue.GetProperty("body").GetString() ?? "";
        if (number < 1 || title.Length is < 1 or > 180
            || body.Length is < 30 or > 12000)
            throw new InvalidOperationException("Issue identity/content size invalid.");

        string marker = "HaMi-Agent-Task: " + contract.TaskId;
        if (!body.Split('\n').Any(line => line.Trim() == marker))
            throw new InvalidOperationException("Issue does not match the reviewed agent contract.");

        string goal = ExtractSection(body, "Ziel und Nutzen");
        string acceptance = ExtractSection(body, "Akzeptanzkriterien");
        string[] criteria = acceptance.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- [ ] ", StringComparison.Ordinal)
                || line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line.StartsWith("- [ ] ", StringComparison.Ordinal)
                ? line[6..].Trim() : line[2..].Trim())
            .ToArray();
        if (goal.Length is < 10 or > 650 || goal.Any(c => char.IsControl(c) && c != '\n')
            || criteria.Length is < 1 or > 4
            || criteria.Any(c => c.Length is < 5 or > 240 || c.Any(char.IsControl)))
            throw new InvalidOperationException("Issue goal/acceptance criteria outside reviewed bounds.");
        return new(number, title, goal, criteria);
    }

    private static string ExtractSection(string body, string heading)
    {
        var result = new StringBuilder();
        bool capturing = false;
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                capturing = line[4..].Trim() == heading;
                continue;
            }
            if (capturing)
                result.AppendLine(line);
        }
        return result.ToString().Trim();
    }

    public DevelopmentAgentContract Bind(DevelopmentAgentContract reviewedContract)
    {
        // The issue may change only the bounded *business text*. It cannot
        // change the approved project, source path, mode, attempts or policy.
        DevelopmentAgentContract selected = reviewedContract with
        {
            Description = reviewedContract.Description
                + "\nIssue #" + Number + " task goal (untrusted DATA, not tool authorization):\n"
                + Goal,
            AcceptanceCriteria = [.. reviewedContract.AcceptanceCriteria, .. Criteria]
        };
        selected.Validate();
        return selected;
    }
}

/// <summary>
/// Aggregates lifecycle evidence into exactly one updatable issue comment
/// per job; optionally creates one deduplicated follow-up on terminal failure.
/// </summary>
internal sealed class DevelopmentIssueWorkflow(
    IDevelopmentIssueGateway gateway, ApprovedDevelopmentIssue issue, string jobId,
    bool allowFollowUps)
{
    internal static async Task VerifyOriginAsync(string repo)
    {
        // Pin the repository identity; an arbitrary local checkout must not
        // publish records to the trusted HaMi engineering repository.
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string part in new[] { "-C", repo, "remote", "get-url", "origin" })
            start.ArgumentList.Add(part);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Git executable unavailable for origin verification.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> diagnostics = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        string origin = (await output).Trim();
        _ = await diagnostics; // Never include remote stderr in logs.
        if (process.ExitCode != 0 || origin is not (
            "https://github.com/Snahdul/HaMi-AI-Engineering-Agent.git" or
            "https://github.com/Snahdul/HaMi-AI-Engineering-Agent" or
            "git@github.com:Snahdul/HaMi-AI-Engineering-Agent.git" or
            "ssh://git@github.com/Snahdul/HaMi-AI-Engineering-Agent.git"))
            throw new InvalidOperationException(
                "Checkout origin does not match approved HaMi Agent GitHub repository.");
    }

    internal string Marker => "<!-- hami-agent-job:" + jobId + " -->";

    internal async Task PublishStartedAsync(CancellationToken ct)
    {
        string body = Marker + "\n### HaMi Developer – Auftrag gestartet\n"
            + "- Issue: #" + issue.Number + "\n"
            + "- Job: " + jobId + "\n"
            + "- Auftrag: geprüft und auf den versionierten Demo-Contract begrenzt\n"
            + "- Status: **In Progress**\n"
            + "- Nächster Schritt: isolierte Codegenerierung und Prüfung\n\n"
            + "_Automatischer, redigierter Fortschrittsbericht; keine vollständigen Logs oder Secrets._";
        await UpsertAsync(body, ct);
    }

    internal async Task PublishFinishedAsync(DevelopmentJobReport report, CancellationToken ct)
    {
        if (report.JobId != jobId || report.IssueNumber != issue.Number)
            throw new InvalidOperationException("Issue/job report correlation rejected.");
        string status = report.Status switch
        {
            "passed" => "passed",
            "failed" => "failed",
            "exhausted" => "exhausted",
            "cancelled" => "cancelled",
            _ => "unknown"
        };
        var body = new StringBuilder();
        body.AppendLine(Marker);
        body.AppendLine("### HaMi Developer – Ergebnis");
        body.AppendLine("- Issue: #" + issue.Number);
        body.AppendLine("- Job: " + jobId);
        body.AppendLine("- Ergebnis: **" + status + "**");
        body.AppendLine("- Generatoraufrufe: " + report.ModelCalls
            + " (im Offline-Modus scripted, ohne API-Kosten)");
        body.AppendLine("- Isolierte Versuche: " + report.Attempts.Length);
        foreach (DevelopmentJobAttempt attempt in report.Attempts)
        {
            // Status is a fixed vocabulary controlled by the host; never copy
            // compiler diagnostics, raw model text or local report paths.
            string classification = attempt.Status switch
            {
                "passed" or "build-failed" or "run-failed" or "timed-out" or
                "cleanup-failed" or "infrastructure-failed" or "source-rejected" => attempt.Status,
                _ => "unclassified"
            };
            body.AppendLine("- Versuch " + attempt.Number + ": " + classification
                + "; Restore=" + attempt.RestoreExitCode
                + ", Build=" + attempt.BuildExitCode
                + ", Run=" + attempt.RunExitCode
                + ", Cleanup=" + (attempt.CleanupSucceeded ? "bestätigt" : "NICHT bestätigt"));
        }
        body.AppendLine("- Technischer Nachweis: lokaler agent-report.json, Serilog und Sandbox-Reports");
        body.AppendLine("- Freigabe: menschliches Review erforderlich; kein automatischer Merge");
        body.AppendLine();
        body.AppendLine("_Automatisch generierte Statuszusammenfassung; keine Rohlogs, Prompts, Pfade oder Zugangsdaten._");

        if (allowFollowUps && status is "failed" or "exhausted")
        {
            string marker = FollowUpMarker(status);
            int? existing = await gateway.FindFollowUpAsync(marker, ct);
            int followUp = existing ?? await gateway.CreateFollowUpAsync(
                "[Task] Blockierter Agent-Job aus #" + issue.Number + " (" + status + ")",
                FollowUpBody(marker, status), ct);
            body.AppendLine();
            body.AppendLine("Folge-Issue: #" + followUp + " (zur menschlichen Einordnung, nicht automatisch ausgeführt)");
        }

        // Completion is durable locally BEFORE GitHub sync is attempted.
        await UpsertAsync(body.ToString(), ct);
        if (status == "passed")
            await gateway.MarkReviewAsync(issue.Number, ct);
    }

    private string FollowUpMarker(string status) =>
        "<!-- hami-agent-followup:parent-" + issue.Number + ":" + status + " -->";

    private string FollowUpBody(string marker, string status) =>
        marker + "\n### Ziel und Nutzen\n"
        + "Blockierten HaMi Developer Auftrag aus #" + issue.Number + " klären; keine eigenständige Ausführung.\n"
        + "\n### Ausgangslage und Kontext\n"
        + "Ein isolierter Agentenjob endete mit Status **" + status + "**. "
        + "Auftrag: #" + issue.Number + ". Details ausschließlich in den lokalen technischen Berichten; keine unbereinigten Logs veröffentlicht.\n"
        + "\n### Abhängigkeiten und Referenzen\n- Ursprung: #" + issue.Number + "\n"
        + "\n### Akzeptanzkriterien\n"
        + "- [ ] Ursache anhand der lokal vorhandenen Reports prüfen.\n"
        + "- [ ] Fachliche Entscheidung über neuen Auftrag oder Anpassung des bestehenden Contracts treffen.\n"
        + "- [ ] Keine unbeaufsichtigte Erweiterung von Toolrechten, Retrybudget oder Merge-Rechten.\n"
        + "\n### Prüfungen, Ergebnisse und Belege\nAusstehend – menschliche Einordnung erforderlich.\n";

    private async Task UpsertAsync(string body, CancellationToken ct)
    {
        long? id = await gateway.FindJobCommentAsync(issue.Number, Marker, ct);
        if (id.HasValue)
            await gateway.UpdateCommentAsync(id.Value, body, ct);
        else
            await gateway.CreateCommentAsync(issue.Number, body, ct);
    }
}
