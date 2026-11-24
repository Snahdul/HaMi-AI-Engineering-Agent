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
        const string oneBased = "attempt >= 1 && maxCalls >= 1 && attempt < maxCalls";
        const string positive = "attempt > 0 && maxCalls > 0 && attempt < maxCalls";
        await SelfTestVariantAsync(repository, oneBased, positive, recoveryMatrix: true);
        await SelfTestVariantAsync(repository, positive, oneBased, recoveryMatrix: false);
    }

    private static async Task SelfTestVariantAsync(string repository,
        string baselineExpression, string candidateExpression, bool recoveryMatrix)
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
                DevelopmentPublicationPolicy.PolicyPath, DevelopmentAgentContract.ProductionPath })
            {
                string target = Local(repo, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string content = (await File.ReadAllTextAsync(Local(repository, path)))
                    .Replace("\r\n", "\n", StringComparison.Ordinal);
                // The disposable fixture owns its starting expression, regardless
                // of the spelling in the production checkout being tested.
                if (path == sourcePath)
                    content = CodingTaskDefinition.RetryBudget.ReplaceExpression(content, baselineExpression);
                await File.WriteAllTextAsync(target, content);
            }
            await Git(repo, "add", ".");
            await Git(repo, "commit", "-m", "trusted fixture");
            string baseline = (await Git(repo, "rev-parse", "HEAD")).Trim();
            await Git(repo, "worktree", "add", "--detach", candidate, baseline);
            string source = Local(candidate, sourcePath);
            string original = await File.ReadAllTextAsync(source);
            string changed = CodingTaskDefinition.RetryBudget.ReplaceExpression(original,
                candidateExpression);
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
            // A fresh filtered checkout has a clean index but different bytes.
            // Do not change autocrlf on an already populated worktree.
            string filteredRepo = Path.Combine(root, "filtered-repo");
            await Git(repo, "clone", "--local", "--no-hardlinks", "-c",
                "core.autocrlf=true", repo, filteredRepo);
            await Git(filteredRepo, "remote", "set-url", "origin",
                "https://github.com/" + GitHubIssueGateway.Repository + ".git");
            if ((await Git(filteredRepo, "status", "--porcelain")).Length != 0
                || (await Git(filteredRepo, "hash-object", "--no-filters", "--", sourcePath)).Trim()
                    == (await Git(filteredRepo, "rev-parse", baseline + ":" + sourcePath)).Trim())
                throw new InvalidOperationException("Line ending regression fixture lacks a clean transformed checkout.");
            await RejectAsync(() => publisher.PublishAsync(filteredRepo, results,
                job, 77, baseline, true, CancellationToken.None),
                "Publication requires byte-identical baseline checkout; review Git checkout filters.");
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
            int postsBeforeRecovery = transport.Posts;
            if (await Publish() != 99 || await Publish() != 99
                || transport.Posts != postsBeforeRecovery
                || transport.BranchCreates != 1 || transport.PullCreates != 1)
                throw new InvalidOperationException("Publication is not idempotent after interruption.");
            transport.Conflict = true;
            await RejectAsync(() => Publish());
            if (transport.Posts != postsBeforeRecovery || transport.BranchCreates != 1 || transport.PullCreates != 1
                || (await Git(repo, "status", "--porcelain")).Length != 0
                || (await Git(repo, "rev-parse", "HEAD")).Trim() != baseline)
                throw new InvalidOperationException("Conflict modified publication or baseline.");
            if (recoveryMatrix)
            {
                transport.Conflict = false;
                foreach (string conflict in new[] { "closed", "ready", "body", "multiple", "missing-ref" })
                {
                    transport.PullConflict = conflict;
                    await RejectAsync(() => Publish());
                    if (transport.Posts != postsBeforeRecovery)
                        throw new InvalidOperationException("Conflicting existing PR caused remote writes.");
                }
                transport.PullConflict = null;
                transport.TamperedTree = true;
                await RejectAsync(() => Publish());
                transport.TamperedTree = false;
                transport.ConflictingCommit = true;
                await RejectAsync(() => Publish());
                if (transport.Posts != postsBeforeRecovery)
                    throw new InvalidOperationException("Conflicting existing tree or commit caused remote writes.");
                transport.ConflictingCommit = false;
                await VerifyRecoveryAsync(repo, results, root, baseline, changed);
            }
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

    // Reuse the disposable real Git baseline. Only the external issue,
    // sandbox inspection and GitHub HTTP boundaries are controlled fixtures.
    // No real credential, model, build, guest or remote write is involved.
    private static async Task VerifyRecoveryAsync(string repo, string results, string root,
        string baseline, string changed)
    {
        string jobId = new('a', 32);
        string runId = new('f', 32);
        const int issueNumber = 77;
        string sourcePath = DevelopmentProductionVerifier.SourcePath;
        bool revoked = false, changedTask = false, unknownCleanup = false;
        int reads = 0, inspections = 0, publisherStarts = 0;

        Task<JsonDocument> ReadIssue(int number)
        {
            if (number != issueNumber)
                throw new Exception("Recovery used a different issue.");
            reads++;
            return Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                number,
                state = revoked ? "closed" : "open",
                user = new { login = "Snahdul" },
                labels = new[] { new { name = "workflow:in-progress" } },
                title = "Approved recovery fixture",
                body = "HaMi-Agent-Task: production-retry-budget\n\n### Ziel und Nutzen\n"
                    + (changedTask ? "Changed approved business goal." : "Preserve the approved retry behavior.")
                    + "\n\n### Akzeptanzkriterien\n- Preserve the retry semantics."
            })));
        }

        Task Inspect(string id)
        {
            if (id != runId)
                throw new Exception("Recovery inspected the wrong sandbox.");
            inspections++;
            if (unknownCleanup)
                throw new InvalidOperationException("Fixture sandbox absence not confirmed.");
            return Task.CompletedTask;
        }

        var contract = DevelopmentAgentContract.Load(repo, DevelopmentAgentContract.ProductionPath);
        using var approvedJson = await ReadIssue(issueNumber);
        var issue = ApprovedDevelopmentIssue.Parse(approvedJson.RootElement, contract);
        var identity = await DevelopmentJobIdentityFactory.CreateAsync(repo, results, jobId, issueNumber,
            issue, issue.Bind(contract), baseline, false);
        string jobRoot = DevelopmentJobEvidence.JobRoot(identity);
        string candidate = Path.Combine(jobRoot, "worktrees", "01");
        string production = Path.Combine(jobRoot, "evaluations", "01", "production-check-" + new string('e', 32));
        string sandbox = Path.Combine(production, "sandbox", "reports", "hami-code-" + runId);
        string patch = Path.Combine(production, "candidate.patch");
        string sandboxReport = Path.Combine(sandbox, "report.json");
        string originalPath = Path.Combine(jobRoot, DevelopmentJobEvidence.OriginalReportName);
        string projection = Path.Combine(jobRoot, "agent-report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
        Directory.CreateDirectory(sandbox);
        await Git(repo, "worktree", "add", "--detach", candidate, baseline);
        try
        {
            string source = Local(candidate, sourcePath);
            await File.WriteAllTextAsync(source, changed);
            await File.WriteAllTextAsync(patch, await Git(candidate, "diff", "--no-ext-diff", "--no-textconv", "--", sourcePath));
            string snapshot = Local(Path.Combine(production, "snapshot"), sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
            File.Copy(source, snapshot);
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var evidence = new DevelopmentProductionVerifier.ProductionReport("passed",
                baseline, sourcePath, DevelopmentProductionVerifier.FileHash(source), patch, sandboxReport,
                0, 0, 0, true, true, true, DevelopmentProductionVerifier.FileHash(patch));
            await File.WriteAllTextAsync(Path.Combine(production, "production-report.json"),
                JsonSerializer.Serialize(evidence, jsonOptions));
            await File.WriteAllTextAsync(sandboxReport, JsonSerializer.Serialize(
                new DevelopmentSandboxRunner.SandboxRunReport(1, runId, "samples/RetryBudget/RetryBudget.csproj",
                    "run", 0, 0, 0, false, 1, 1, 1, 1, 1, 1, 6, true, null), jsonOptions));
            await File.WriteAllTextAsync(Path.Combine(sandbox, "execution.log"),
                DevelopmentOutputAcceptance.ReviewedProductionStdout);
            var attempt = new DevelopmentJobAttempt(1, "passed", 0, 0, 0, false, true,
                sandboxReport, true, candidate, patch, evidence);
            var job = new DevelopmentJobReport(jobId, "passed", 1, [attempt], null, issueNumber);
            string manifestHash = await DevelopmentJobEvidence.SealAsync(repo, identity, job);
            byte[] original = File.ReadAllBytes(originalPath);
            byte[] originalManifest = File.ReadAllBytes(Path.Combine(jobRoot, DevelopmentJobEvidence.ManifestName));
            string[] fullWrites = ["git/blobs", "git/trees", "git/commits", "git/refs", "pulls"];

            int scenario = 0;
            foreach (string? interrupted in new string?[]
            {
                null, "git/blobs", "git/trees", "git/commits", "git/refs", "pulls", "rate-limit"
            })
            {
                string stateRoot = Path.Combine(root, "recovery-state-" + (++scenario));
                Directory.CreateDirectory(stateRoot);
                var store = new DevelopmentJobStateStore(stateRoot);
                var remote = new FakeGitHub(baseline);
                // Simulate process loss by closing the initial lease. The
                // durable checkpoint and fake remote effects survive.
                using (var initial = store.Acquire(issueNumber))
                {
                    initial.Claim(DevelopmentJobState.Create(identity));
                    initial.Commit(initial.RequireCurrent().Start());
                    initial.Commit(initial.RequireCurrent().Verify(1, 1, manifestHash));
                    if (interrupted is not null)
                    {
                        initial.Commit(initial.RequireCurrent().ReservePublication());
                        remote.InterruptEndpoint = interrupted;
                        remote.RateLimited = interrupted == "rate-limit";
                        using var publisher = remote.OpenPublisher();
                        await RejectAsync(() => publisher.PublishAsync(repo, results, job, issueNumber,
                            baseline, true, CancellationToken.None), remote.RateLimited
                                ? "Publication API rejected request: HTTP 429"
                                : "Publication API rejected request: HTTP 503");
                    }
                }

                remote.InterruptEndpoint = null;
                remote.RateLimited = false;
                int previousPosts = remote.Posts;
                int expectedReservations = interrupted is null ? 1 : 2;
                using (var owner = store.AcquireJob(jobId))
                {
                    Task<int> Resume(string approvedBase = "") =>
                        DevelopmentRecoveryCommand.ResumeOwnedAsync(repo, owner,
                            approvedBase == "" ? baseline : approvedBase, ReadIssue, Inspect, () =>
                            {
                                publisherStarts++;
                                var pending = owner.RequireCurrent();
                                if (pending.Stage != DevelopmentJobState.PublicationPending
                                    || pending.PublicationAttempts != expectedReservations
                                    || pending.Identity.JobId != jobId)
                                    throw new Exception("Publication started before durable reservation.");
                                return remote.OpenPublisher();
                            });

                    var before = owner.RequireCurrent();
                    if (before.Identity != identity || before.ModelCalls != 1 || before.Attempts != 1
                        || before.PublicationAttempts != expectedReservations - 1)
                        throw new InvalidOperationException("Original recovery identity or budget was lost.");

                    // The same production reentry use case must reject these
                    // cases before the publication boundary is constructed.
                    if (interrupted is null)
                    {
                        int beforeStarts = publisherStarts;
                        await RejectAsync(() => Resume(new string('c', 40)));
                        changedTask = true;
                        await RejectAsync(() => Resume());
                        changedTask = false;
                        revoked = true;
                        await RejectAsync(() => Resume());
                        revoked = false;
                        unknownCleanup = true;
                        await RejectAsync(() => Resume());
                        unknownCleanup = false;
                        if (publisherStarts != beforeStarts || remote.Calls != 0
                            || owner.RequireCurrent() != before)
                            throw new InvalidOperationException("Rejected recovery reached publication or consumed budget.");
                    }

                    int previousReads = reads, previousInspections = inspections, previousStarts = publisherStarts;
                    if (await Resume() != 99)
                        throw new InvalidOperationException("Recovery returned an unexpected PR.");
                    string[] expectedWrites = interrupted switch
                    {
                        "git/refs" => ["pulls"],
                        "pulls" => [],
                        _ => fullWrites
                    };
                    var final = owner.RequireCurrent();
                    if (reads != previousReads + 1 || inspections != previousInspections + 1
                        || publisherStarts != previousStarts + 1
                        || !remote.PostPaths.Skip(previousPosts).SequenceEqual(expectedWrites)
                        || remote.BranchCreates != 1 || remote.PullCreates != 1
                        || final.Stage != DevelopmentJobState.Published || final.PullRequestNumber != 99
                        || final.Identity != identity || final.ModelCalls != 1 || final.Attempts != 1
                        || final.PublicationAttempts != expectedReservations
                        || !File.ReadAllBytes(originalPath).SequenceEqual(original)
                        || !File.ReadAllBytes(Path.Combine(jobRoot, DevelopmentJobEvidence.ManifestName)).SequenceEqual(originalManifest))
                        throw new InvalidOperationException("Recovery did not preserve job identity, evidence or exact remote effect count.");
                }

                // Simulate the report update being lost after a confirmed
                // remote publication. The next invocation is local only.
                File.Delete(projection);
                using (var published = store.AcquireJob(jobId))
                {
                    int repair = await DevelopmentRecoveryCommand.ResumeOwnedAsync(repo, published, baseline,
                        _ => throw new Exception("Published repair read an issue."),
                        _ => throw new Exception("Published repair inspected a sandbox."),
                        () => throw new Exception("Published repair constructed a publisher."));
                    if (repair != 99 || published.RequireCurrent().PublicationAttempts != expectedReservations
                        || JsonSerializer.Deserialize<DevelopmentJobReport>(File.ReadAllBytes(projection), jsonOptions)?.DraftPullRequestNumber != 99)
                        throw new InvalidOperationException("Published recovery did not repair the local projection.");
                }
                Console.WriteLine("Recovery regression passed: " + (interrupted ?? "before publication"));
            }

            // Exhausted publication budget and unknown early execution must
            // not resolve any remote dependency or create a new job.
            string terminalRoot = Path.Combine(root, "recovery-terminal-state");
            Directory.CreateDirectory(terminalRoot);
            var terminalStore = new DevelopmentJobStateStore(terminalRoot);
            using (var owner = terminalStore.Acquire(issueNumber))
            {
                owner.Claim(DevelopmentJobState.Create(identity));
                owner.Commit(owner.RequireCurrent().Start());
                await RejectAsync(() => DevelopmentRecoveryCommand.ResumeOwnedAsync(repo, owner, baseline,
                    _ => throw new Exception("Unknown execution read an issue."),
                    _ => throw new Exception("Unknown execution inspected a sandbox."),
                    () => throw new Exception("Unknown execution constructed a publisher.")));
                if (owner.RequireCurrent().Stage != DevelopmentJobState.RecoveryBlocked)
                    throw new InvalidOperationException("Unknown execution was not durably blocked.");
            }

            string exhaustedRoot = Path.Combine(root, "recovery-budget-state");
            Directory.CreateDirectory(exhaustedRoot);
            using (var owner = new DevelopmentJobStateStore(exhaustedRoot).Acquire(issueNumber))
            {
                owner.Claim(DevelopmentJobState.Create(identity));
                owner.Commit(owner.RequireCurrent().Start());
                owner.Commit(owner.RequireCurrent().Verify(1, 1, manifestHash));
                for (int i = 0; i < identity.MaxPublicationAttempts; i++)
                    owner.Commit(owner.RequireCurrent().ReservePublication());
                var exhausted = owner.RequireCurrent();
                await RejectAsync(() => DevelopmentRecoveryCommand.ResumeOwnedAsync(repo, owner, baseline,
                    _ => throw new Exception("Exhausted recovery read an issue."),
                    _ => throw new Exception("Exhausted recovery inspected a sandbox."),
                    () => throw new Exception("Exhausted recovery constructed a publisher.")));
                if (owner.RequireCurrent() != exhausted)
                    throw new InvalidOperationException("Exhausted recovery budget changed.");
            }
        }
        finally
        {
            await Git(repo, "worktree", "remove", "--force", candidate);
        }
    }

    private static Task<string> Git(string path, params string[] args) =>
        DevelopmentWorktreeGuard.GitAsync(path, args);

    private static string Local(string root, string path) =>
        Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    private static async Task RejectAsync(Func<Task<int>> action, string? expectedMessage = null)
    {
        try { await action(); }
        catch (InvalidOperationException ex) when (expectedMessage is null || ex.Message == expectedMessage) { return; }
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
        private string? commitMessage;
        private JsonElement commitAuthor, commitCommitter;
        private bool branch;
        internal bool Denied, Stale, TamperedTree, InterruptPull, Conflict, RateLimited, ConflictingCommit;
        internal string? InterruptEndpoint, PullConflict;
        internal int Calls, Posts, BranchCreates, PullCreates;
        internal List<string> PostPaths { get; } = [];
        private readonly Dictionary<string, string> objectPayloads = new(StringComparer.Ordinal);

        // New client/handler per invocation, same simulated remote resources.
        internal DevelopmentPublicationGateway OpenPublisher() =>
            new(new Connection(this), "OFFLINE_DUMMY_TOKEN");

        private sealed class Connection(FakeGitHub remote) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                remote.SendAsync(request, ct);
        }

        private HttpResponseMessage AfterEffect(string endpoint, object value) =>
            InterruptEndpoint == endpoint || (endpoint == "pulls" && InterruptPull)
                ? Reply(new { }, HttpStatusCode.ServiceUnavailable) : Reply(value);

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
            if (post)
            {
                Posts++;
                PostPaths.Add(path);
            }
            if (Denied) return Reply(new { }, HttpStatusCode.Forbidden);
            if (RateLimited) return Reply(new { }, HttpStatusCode.TooManyRequests);
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
                    parents = new[] { new { sha = baseline } },
                    message = ConflictingCommit ? "Different job marker" : commitMessage,
                    author = commitAuthor,
                    committer = commitCommitter
                });
            if (get && path == "git/commits/" + OldBlob)
                return Reply(new { }, HttpStatusCode.NotFound);
            if (get && path.StartsWith("git/ref/heads/hami-agent/job-", StringComparison.Ordinal))
                return branch && PullConflict != "missing-ref" ? Reply(new { @object = new { sha = Conflict ? OldBlob : Commit } })
                    : Reply(new { }, HttpStatusCode.NotFound);
            if (get && path == "pulls")
            {
                if (pull is null) return Reply(Array.Empty<object>());
                if (PullConflict == "multiple") return Reply(new[] { pull, pull });
                if (PullConflict is "closed" or "ready" or "body")
                {
                    var modified = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                        JsonSerializer.Serialize(pull))!;
                    if (PullConflict == "closed") modified["state"] = "closed";
                    if (PullConflict == "ready") modified["draft"] = false;
                    if (PullConflict == "body") modified["body"] = "Different evidence";
                    return Reply(new[] { modified });
                }
                return Reply(new[] { pull });
            }
            if (!post || request.Content is null)
                throw new Exception("Unexpected publication HTTP operation.");
            using var doc = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            var data = doc.RootElement;
            if (path is "git/blobs" or "git/trees" or "git/commits")
            {
                string payload = data.GetRawText();
                if (objectPayloads.TryGetValue(path, out string? previous) && previous != payload)
                    throw new Exception("Repeated content-addressed object request changed identity.");
                objectPayloads[path] = payload;
            }
            switch (path)
            {
                case "git/blobs":
                    byte[] bytes = Convert.FromBase64String(data.GetProperty("content").GetString()!);
                    byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + "\0");
                    blob = Convert.ToHexString(SHA1.HashData(header.Concat(bytes).ToArray())).ToLowerInvariant();
                    return AfterEffect(path, new { sha = blob });
                case "git/trees":
                    var edit = data.GetProperty("tree");
                    if (data.GetProperty("base_tree").GetString() != BaseTree || edit.GetArrayLength() != 1
                        || edit[0].GetProperty("path").GetString() != DevelopmentProductionVerifier.SourcePath
                        || edit[0].GetProperty("sha").GetString() != blob)
                        throw new Exception("Unexpected tree update.");
                    return AfterEffect(path, new { sha = NextTree });
                case "git/commits":
                    if (data.GetProperty("tree").GetString() != NextTree
                        || data.GetProperty("parents").GetArrayLength() != 1
                        || data.GetProperty("parents")[0].GetString() != baseline)
                        throw new Exception("Unexpected commit creation.");
                    commitMessage = data.GetProperty("message").GetString();
                    commitAuthor = data.GetProperty("author").Clone();
                    commitCommitter = data.GetProperty("committer").Clone();
                    return AfterEffect(path, new { sha = Commit });
                case "git/refs":
                    if (branch || data.GetProperty("sha").GetString() != Commit
                        || data.GetProperty("ref").GetString() != "refs/heads/hami-agent/job-" + new string('a', 32))
                        throw new Exception("Unexpected branch creation.");
                    branch = true;
                    BranchCreates++;
                    return AfterEffect(path, new { @object = new { sha = Commit } });
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
                    return AfterEffect(path, pull);
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
