using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HaMi.Agentic.Cli.Features.Coding;

namespace HaMi.Agentic.Cli.Features.Development;

// The real gate and publisher run against a disposable Git repository and
// a fake HTTP transport. No network, live credentials, model or guest.
internal static class DevelopmentPublicationGatewayTests
{
    internal static async Task SelfTestAsync(string repository)
    {
        string root = Path.Combine(Path.GetTempPath(), "hami-publish-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        string results = Path.Combine(root, "results");
        string candidate = Path.Combine(results, "candidate");
        string patch = Path.Combine(results, "candidate.patch");
        string sourcePath = DevelopmentProductionVerifier.SourcePath;
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(results);
        try
        {
            await Git(repo, "init", "-b", "main");
            await Git(repo, "config", "user.name", "Offline Test");
            await Git(repo, "config", "user.email", "test@example.invalid");
            await Git(repo, "config", "core.autocrlf", "false");
            await Git(repo, "remote", "add", "origin",
                "https://github.com/" + GitHubIssueGateway.Repository + ".git");
            foreach (string path in new[] { sourcePath, DevelopmentAgentContract.ProductionScopePath,
                DevelopmentPublicationPolicy.PolicyPath })
            {
                string target = Local(repo, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, (await File.ReadAllTextAsync(Local(repository, path)))
                    .Replace("\r\n", "\n", StringComparison.Ordinal));
            }
            await Git(repo, "add", ".");
            await Git(repo, "commit", "-m", "trusted fixture");
            string baseline = (await Git(repo, "rev-parse", "HEAD")).Trim();
            await Git(repo, "worktree", "add", "--detach", candidate, baseline);
            string source = Local(candidate, sourcePath);
            string original = await File.ReadAllTextAsync(source);
            string changed = CodingTaskDefinition.RetryBudget.ReplaceExpression(original,
                "attempt > 0 && maxCalls > 0 && attempt < maxCalls");
            if (changed == original)
                throw new InvalidOperationException("Publication fixture unexpectedly unchanged.");
            await File.WriteAllTextAsync(source, changed);
            string diff = await Git(candidate, "diff", "--no-ext-diff", "--no-textconv", "--", sourcePath);
            await File.WriteAllTextAsync(patch, diff);
            var evidence = new DevelopmentProductionVerifier.ProductionReport("passed",
                baseline, sourcePath, DevelopmentProductionVerifier.FileHash(source), patch,
                "synthetic-sandbox-report", 0, 0, 0, true, true, true,
                DevelopmentProductionVerifier.FileHash(patch));
            var attempt = new DevelopmentJobAttempt(1, "passed", 0, 0, 0, false, true,
                evidence.SandboxReport, true, candidate, patch, evidence);
            var job = new DevelopmentJobReport(new string('a', 32), "passed", 1, [attempt], null, 77);

            var transport = new FakeGitHub(baseline);
            using var publisher = new DevelopmentPublicationGateway(transport, "OFFLINE_DUMMY_TOKEN");
            Task<int> Publish(bool allowed = true) => publisher.PublishAsync(repo, results,
                job, 77, baseline, allowed, CancellationToken.None);
            await RejectAsync(() => Publish(false));
            // A clean status is insufficient if Git smudged baseline bytes.
            string baselineFile = Local(repo, sourcePath);
            await Git(repo, "config", "core.autocrlf", "true");
            await File.WriteAllTextAsync(baselineFile, original.Replace("\n", "\r\n", StringComparison.Ordinal));
            if ((await Git(repo, "status", "--porcelain")).Length != 0)
                throw new InvalidOperationException("Line ending regression fixture is not clean.");
            await RejectAsync(() => Publish());
            await File.WriteAllTextAsync(baselineFile, original);
            await Git(repo, "config", "core.autocrlf", "false");
            await File.AppendAllTextAsync(patch, "tampered");
            await RejectAsync(() => Publish());
            await File.WriteAllTextAsync(patch, diff);
            await File.AppendAllTextAsync(source, "\n// tampered");
            await RejectAsync(() => Publish());
            await File.WriteAllTextAsync(source, changed);
            using (var denied = new DevelopmentPublicationGateway(new FakeGitHub(baseline), ""))
                await RejectAsync(() => denied.PublishAsync(repo, results, job, 77,
                    baseline, true, CancellationToken.None));
            if (transport.Calls != 0)
                throw new InvalidOperationException("Rejected local candidate reached remote API.");

            transport.Denied = true;
            await RejectAsync(() => Publish());
            transport.Denied = false;
            transport.Stale = true;
            await RejectAsync(() => Publish());
            transport.Stale = false;
            transport.TamperedTree = true;
            await RejectAsync(() => Publish());
            transport.TamperedTree = false;
            if (transport.BranchCreates != 0 || transport.PullCreates != 0)
                throw new InvalidOperationException("Invalid remote state was published.");

            transport.InterruptPull = true;
            await RejectAsync(() => Publish());
            transport.InterruptPull = false;
            if (await Publish() != 99 || await Publish() != 99
                || transport.BranchCreates != 1 || transport.PullCreates != 1)
                throw new InvalidOperationException("Publication is not idempotent after interruption.");
            transport.Conflict = true;
            await RejectAsync(() => Publish());
            if (transport.BranchCreates != 1 || transport.PullCreates != 1
                || (await Git(repo, "status", "--porcelain")).Length != 0
                || (await Git(repo, "rev-parse", "HEAD")).Trim() != baseline)
                throw new InvalidOperationException("Conflict modified publication or baseline.");
        }
        finally
        {
            if (Directory.Exists(candidate))
                await Git(repo, "worktree", "remove", "--force", candidate);
            if (Directory.Exists(root))
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }

    private static Task<string> Git(string path, params string[] args) =>
        DevelopmentWorktreeGuard.GitAsync(path, args);

    private static string Local(string root, string path) =>
        Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    private static async Task RejectAsync(Func<Task<int>> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe publication was accepted.");
    }

    private sealed class FakeGitHub(string baseline) : HttpMessageHandler
    {
        private const string Root = "/repos/Snahdul/HaMi-AI-Engineering-Agent/";
        private static readonly string BaseTree = new('b', 40);
        private static readonly string NextTree = new('c', 40);
        private static readonly string Commit = new('d', 40);
        private static readonly string OldBlob = new('e', 40);
        private static readonly string OtherBlob = new('f', 40);
        private string? blob;
        private object? pull;
        private bool branch;
        internal bool Denied, Stale, TamperedTree, InterruptPull, Conflict;
        internal int Calls, BranchCreates, PullCreates;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.RequestUri?.Host != "api.github.com"
                || request.Headers.Authorization?.Parameter != "OFFLINE_DUMMY_TOKEN"
                || !request.RequestUri.AbsolutePath.StartsWith(Root, StringComparison.Ordinal))
                throw new Exception("Unexpected publication transport destination.");
            string path = request.RequestUri.AbsolutePath[Root.Length..];
            bool get = request.Method == HttpMethod.Get;
            bool post = request.Method == HttpMethod.Post;
            if (Denied) return Reply(new { }, HttpStatusCode.Forbidden);
            if (get && path == "git/ref/heads/main")
                return Reply(new { @object = new { sha = Stale ? OldBlob : baseline } });
            if (get && path == "git/commits/" + baseline)
                return Reply(new
                {
                    sha = baseline,
                    tree = new { sha = BaseTree },
                    committer = new { date = "2026-01-01T00:00:00Z" }
                });
            if (get && path == "git/trees/" + BaseTree)
                return Tree(BaseTree, OldBlob, OtherBlob);
            if (get && path == "git/trees/" + NextTree)
                return Tree(NextTree, blob!, TamperedTree ? OldBlob : OtherBlob);
            if (get && path == "git/commits/" + Commit)
                return Reply(new
                {
                    sha = Commit,
                    tree = new { sha = NextTree },
                    parents = new[] { new { sha = baseline } }
                });
            if (get && path.StartsWith("git/ref/heads/hami-agent/job-", StringComparison.Ordinal))
                return branch ? Reply(new { @object = new { sha = Conflict ? OldBlob : Commit } })
                    : Reply(new { }, HttpStatusCode.NotFound);
            if (get && path == "pulls")
                return Reply(pull is null ? Array.Empty<object>() : new[] { pull });
            if (!post || request.Content is null)
                throw new Exception("Unexpected publication HTTP operation.");
            using var doc = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            var data = doc.RootElement;
            switch (path)
            {
                case "git/blobs":
                    byte[] bytes = Convert.FromBase64String(data.GetProperty("content").GetString()!);
                    byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + "\0");
                    blob = Convert.ToHexString(SHA1.HashData(header.Concat(bytes).ToArray())).ToLowerInvariant();
                    return Reply(new { sha = blob });
                case "git/trees":
                    var edit = data.GetProperty("tree");
                    if (data.GetProperty("base_tree").GetString() != BaseTree || edit.GetArrayLength() != 1
                        || edit[0].GetProperty("path").GetString() != DevelopmentProductionVerifier.SourcePath
                        || edit[0].GetProperty("sha").GetString() != blob)
                        throw new Exception("Unexpected tree update.");
                    return Reply(new { sha = NextTree });
                case "git/commits":
                    if (data.GetProperty("tree").GetString() != NextTree
                        || data.GetProperty("parents").GetArrayLength() != 1
                        || data.GetProperty("parents")[0].GetString() != baseline)
                        throw new Exception("Unexpected commit creation.");
                    return Reply(new { sha = Commit });
                case "git/refs":
                    if (branch || data.GetProperty("sha").GetString() != Commit
                        || data.GetProperty("ref").GetString() != "refs/heads/hami-agent/job-" + new string('a', 32))
                        throw new Exception("Unexpected branch creation.");
                    branch = true;
                    BranchCreates++;
                    return Reply(new { @object = new { sha = Commit } });
                case "pulls":
                    if (pull is not null || !branch || !data.GetProperty("draft").GetBoolean()
                        || data.GetProperty("base").GetString() != "main")
                        throw new Exception("Unexpected pull request creation.");
                    var repository = new { full_name = GitHubIssueGateway.Repository };
                    pull = new
                    {
                        number = 99,
                        state = "open",
                        draft = true,
                        body = data.GetProperty("body").GetString(),
                        head = new { @ref = data.GetProperty("head").GetString(), sha = Commit, repo = repository },
                        @base = new { @ref = "main", sha = baseline, repo = repository }
                    };
                    PullCreates++;
                    return InterruptPull ? Reply(new { }, HttpStatusCode.ServiceUnavailable) : Reply(pull);
                default:
                    throw new Exception("Unexpected publication write endpoint.");
            }
        }

        private static HttpResponseMessage Tree(string sha, string source, string other) =>
            Reply(new
            {
                sha,
                truncated = false,
                tree = new[] {
                new { path = DevelopmentProductionVerifier.SourcePath, type = "blob", mode = "100644", sha = source },
                new { path = "unrelated.cs", type = "blob", mode = "100644", sha = other }
            }
            });

        private static HttpResponseMessage Reply(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}
