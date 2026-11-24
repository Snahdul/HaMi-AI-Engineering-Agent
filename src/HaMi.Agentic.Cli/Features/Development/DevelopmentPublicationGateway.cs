using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HaMi.Agentic.Cli.Runtime;

namespace HaMi.Agentic.Cli.Features.Development;

// Separate host-only GitHub capability. No shell, personal gh credentials,
// local checkout mutations, ref updates, merge endpoint or generated commands.
internal sealed class DevelopmentPublicationGateway : IDisposable
{
    private readonly HttpClient client;
    private readonly string token;
    internal DevelopmentPublicationGateway(HttpMessageHandler? handler = null, string? testToken = null)
    {
        token = testToken ?? WindowsIssueCredentialStore.ReadPublication() ?? "";
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Dedicated GitHub publication credential missing.");
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path,
        object? payload, CancellationToken ct, bool allowMissing = false)
    {
        using var request = new HttpRequestMessage(method,
            "https://api.github.com/repos/" + GitHubIssueGateway.Repository + "/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("HaMi-AI-Engineering-Agent/0.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, ct);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Publication API rejected request: HTTP " + (int)response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, path, null, ct)
        ?? throw new InvalidOperationException("Publication API response missing.");

    private async Task<JsonDocument> PostAsync(string path, object payload, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, path, payload, ct)
        ?? throw new InvalidOperationException("Publication API response missing.");

    private static string Sha(JsonElement element)
    {
        string? sha = element.GetProperty("sha").GetString();
        if (!DevelopmentPublicationPolicy.IsSha(sha, 40))
            throw new InvalidOperationException("Invalid GitHub object SHA.");
        return sha!;
    }

    private async Task RequireBaseAsync(string approvedBase, CancellationToken ct)
    {
        using var reference = await GetAsync("git/ref/heads/main", ct);
        if (Sha(reference.RootElement.GetProperty("object")) != approvedBase)
            throw new InvalidOperationException("Remote main differs from approved base.");
    }

    internal async Task<int> PublishAsync(string repo, string resultsRoot,
        DevelopmentJobReport job, int issue, string approvedBase, bool authorized,
        CancellationToken ct)
    {
        byte[] bytes = await DevelopmentPublicationPolicy.ValidateCandidateAsync(
            repo, resultsRoot, job, issue, approvedBase, authorized);
        var attempt = DevelopmentPublicationPolicy.RequireSuccessfulAttempt(job, issue, approvedBase);
        var evidence = attempt.ProductionEvidence!;
        string branch = "hami-agent/job-" + job.JobId;
        string marker = "<!-- hami-agent-job:" + job.JobId + " -->";
        await RequireBaseAsync(approvedBase, ct);
        using var baseline = await GetAsync("git/commits/" + approvedBase, ct);
        if (Sha(baseline.RootElement) != approvedBase)
            throw new InvalidOperationException("Unexpected base commit.");
        string baseTree = Sha(baseline.RootElement.GetProperty("tree"));

        // Upload only the exact bytes accepted by the local gate. GitHub's
        // Git object API avoids local git filters, hooks and credential helpers.
        byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(
            System.Globalization.CultureInfo.InvariantCulture) + "\0");
        string expectedBlob = Convert.ToHexString(SHA1.HashData(header.Concat(bytes).ToArray())).ToLowerInvariant();
        using var blob = await PostAsync("git/blobs", new {
            content = Convert.ToBase64String(bytes), encoding = "base64"
        }, ct);
        if (Sha(blob.RootElement) != expectedBlob)
            throw new InvalidOperationException("Uploaded blob differs from verified bytes.");

        // Preserve the existing file mode. Reject truncated/ambiguous trees.
        using var tree = await GetAsync("git/trees/" + baseTree + "?recursive=1", ct);
        if (Sha(tree.RootElement) != baseTree || tree.RootElement.GetProperty("truncated").GetBoolean())
            throw new InvalidOperationException("Base tree could not be fully verified.");
        var original = tree.RootElement.GetProperty("tree").EnumerateArray()
            .Single(e => e.GetProperty("path").GetString() == evidence.SourcePath);
        if (original.GetProperty("type").GetString() != "blob"
            || original.GetProperty("mode").GetString() != "100644"
            || Sha(original) == expectedBlob)
            throw new InvalidOperationException("Unexpected baseline file mode or unchanged candidate.");

        using var nextTree = await PostAsync("git/trees", new {
            base_tree = baseTree,
            tree = new[] { new { path = evidence.SourcePath, mode = "100644", type = "blob", sha = expectedBlob } }
        }, ct);
        string candidateTree = Sha(nextTree.RootElement);
        using var treeCheck = await GetAsync("git/trees/" + candidateTree + "?recursive=1", ct);
        if (Sha(treeCheck.RootElement) != candidateTree || treeCheck.RootElement.GetProperty("truncated").GetBoolean())
            throw new InvalidOperationException("Candidate tree could not be fully verified.");
        VerifyTrees(tree.RootElement, treeCheck.RootElement, evidence.SourcePath, expectedBlob);

        // Stable commit identity for retry of the same job; use the trusted
        // base commit timestamp, not an uncontrolled wall clock.
        string date = baseline.RootElement.GetProperty("committer").GetProperty("date").GetString()
            ?? throw new InvalidOperationException("Missing base timestamp.");
        var identity = new { name = "HaMi Developer", email = "hami-developer@users.noreply.github.com", date };
        using var commit = await PostAsync("git/commits", new {
            message = "HaMi Developer candidate for #" + issue + "\n\n" + marker,
            tree = candidateTree, parents = new[] { approvedBase },
            author = identity, committer = identity
        }, ct);
        string candidateSha = Sha(commit.RootElement);
        using var verifiedCommit = await GetAsync("git/commits/" + candidateSha, ct);
        var commitRoot = verifiedCommit.RootElement;
        var parents = commitRoot.GetProperty("parents");
        if (Sha(commitRoot) != candidateSha || Sha(commitRoot.GetProperty("tree")) != candidateTree
            || parents.GetArrayLength() != 1 || Sha(parents[0]) != approvedBase)
            throw new InvalidOperationException("Candidate commit verification failed.");

        await RequireBaseAsync(approvedBase, ct);
        using var existing = await SendAsync(HttpMethod.Get, "git/ref/heads/" + branch, null, ct, true);
        if (existing is null)
        {
            using var created = await PostAsync("git/refs", new { @ref = "refs/heads/" + branch, sha = candidateSha }, ct);
            if (Sha(created.RootElement.GetProperty("object")) != candidateSha)
                throw new InvalidOperationException("Unexpected created branch.");
        }
        else if (Sha(existing.RootElement.GetProperty("object")) != candidateSha)
            throw new InvalidOperationException("Existing publication branch conflicts.");
        using var remote = await GetAsync("git/ref/heads/" + branch, ct);
        if (Sha(remote.RootElement.GetProperty("object")) != candidateSha)
            throw new InvalidOperationException("Remote publication branch changed.");

        string body = marker + "\nRefs #" + issue + "\n\n"
            + "Base: " + approvedBase + "\n\nCandidate: " + candidateSha
            + "\n\nSource SHA-256: " + evidence.SourceSha256
            + "\n\nPatch SHA-256: " + evidence.DiffSha256
            + "\n\nRestore=0; Build=0; Run=0; independent acceptance passed; MicroVM cleanup confirmed."
            + "\n\nHuman review and merge approval required.";
        await RequireBaseAsync(approvedBase, ct);
        using var prs = await GetAsync("pulls?state=all&per_page=100&head="
            + Uri.EscapeDataString("Snahdul:" + branch), ct);
        var array = prs.RootElement;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 1)
            throw new InvalidOperationException("Ambiguous publication pull requests.");
        if (array.GetArrayLength() == 1)
            return VerifyPull(array[0], branch, approvedBase, candidateSha, body);
        using var pr = await PostAsync("pulls", new {
            title = "HaMi Developer candidate for #" + issue,
            head = branch, @base = "main", body, draft = true, maintainer_can_modify = false
        }, ct);
        return VerifyPull(pr.RootElement, branch, approvedBase, candidateSha, body);
    }

    internal static void VerifyTrees(JsonElement baseline, JsonElement candidate, string path, string blob)
    {
        // Ignore directory object IDs, which necessarily change along the
        // edited file's ancestry. Compare every non-directory entry exactly.
        static Dictionary<string, (string? Mode, string? Type, string? Sha)> Entries(JsonElement root) =>
            root.GetProperty("tree").EnumerateArray()
                .Where(e => e.GetProperty("type").GetString() != "tree")
                .ToDictionary(e => e.GetProperty("path").GetString()!,
                    e => (e.GetProperty("mode").GetString(), e.GetProperty("type").GetString(),
                        e.GetProperty("sha").GetString()), StringComparer.Ordinal);
        var before = Entries(baseline);
        var after = Entries(candidate);
        if (!before.ContainsKey(path) || before.Count != after.Count)
            throw new InvalidOperationException("Candidate tree scope changed.");
        before[path] = ("100644", "blob", blob);
        if (before.Any(e => !after.TryGetValue(e.Key, out var value) || value != e.Value))
            throw new InvalidOperationException("Candidate tree contains unauthorized changes.");
    }

    private static int VerifyPull(JsonElement pr, string branch, string approvedBase,
        string sha, string body)
    {
        var head = pr.GetProperty("head");
        var target = pr.GetProperty("base");
        if (pr.GetProperty("state").GetString() != "open" || !pr.GetProperty("draft").GetBoolean()
            || head.GetProperty("ref").GetString() != branch || Sha(head) != sha
            || target.GetProperty("ref").GetString() != "main" || Sha(target) != approvedBase
            || head.GetProperty("repo").GetProperty("full_name").GetString() != GitHubIssueGateway.Repository
            || target.GetProperty("repo").GetProperty("full_name").GetString() != GitHubIssueGateway.Repository
            || pr.GetProperty("body").GetString() != body)
            throw new InvalidOperationException("Pull request identity or draft state changed.");
        int number = pr.GetProperty("number").GetInt32();
        return number > 0 ? number : throw new InvalidOperationException("Invalid pull request number.");
    }

    public void Dispose() => client.Dispose();
}
