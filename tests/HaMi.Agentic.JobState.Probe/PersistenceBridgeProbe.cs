using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using Akka.Actor;
using Akka.Hosting;
using Akka.Pattern;
using Akka.Persistence;
using Akka.Persistence.Sql.Hosting;
using HaMi.Agentic.Cli.Features.Development;
using LinqToDB;
using LinqToDB.Common;
using LinqToDB.Interceptors;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Disposable offline experiment, not an execution path or production journal.
internal static class PersistenceBridgeProbe
{
    internal const int Issue = 900001;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly byte[] AcceptedBytes = Encoding.UTF8.GetBytes("synthetic accepted S01");
    private static readonly string AcceptedHash = DevelopmentJobState.Hash(AcceptedBytes);

    internal static async Task RunAsync(string prefix, Func<string, string, Process> start,
        Func<string, string, Task> expectOwnership)
    {
        foreach (bool unknown in new[] { false, true })
        {
            string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            var store = DevelopmentJobStateRoot.OpenOrderProbeFixture(root, create: true);
            try
            {
                using (Process child = start(unknown ? "bridge-unknown" : "bridge-hold", root))
                {
                    Task<string> stderr = child.StandardError.ReadToEndAsync();
                    var startup = Stopwatch.StartNew();
                    try
                    {
                        await WaitForMarkerAsync(child, unknown ? "BRIDGE_RESERVED" : "BRIDGE_ACCEPTED").WaitAsync(Deadline);
                        Console.WriteLine($"Bridge fixture startup/acceptance: {startup.ElapsedMilliseconds} ms.");
                        if (child.HasExited)
                            throw new InvalidOperationException("Bridge owner stopped before the crash test.");
                        await expectOwnership(root, "BUSY");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Bridge startup failed after {startup.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
                        throw;
                    }
                    finally
                    {
                        if (!child.HasExited)
                            child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(Deadline);
                        string diagnostics = await stderr.WaitAsync(Deadline);
                        if (diagnostics.Length > 0)
                            Console.Error.WriteLine(diagnostics[^Math.Min(diagnostics.Length, 16 * 1024)..]);
                    }

                    if (child.ExitCode == 0)
                        throw new InvalidOperationException("Bridge owner did not terminate abnormally.");
                }

                string artifact = Path.Combine(root, $"issue-{Issue}.order.db.s01");
                using (var owner = store.AcquireOrder(Issue))
                {
                    _ = owner.RequireArchivedOrder();
                    string journal = owner.RequireOrderJournal();
                    Reject(() => owner.CreateOrderJournal());
                    Reject(() => store.Acquire(Issue).Dispose());
                    if (!unknown && !File.Exists(journal + "-journal"))
                        throw new InvalidOperationException("Crash did not retain the live rollback sidecar.");
                    if (!unknown)
                    {
                        byte[] original = owner.RequireOrderArtifact("S01", AcceptedHash);
                        File.WriteAllBytes(artifact, [.. original, 0x20]);
                        Reject(() => owner.RequireOrderArtifact("S01", AcceptedHash));
                        File.WriteAllBytes(artifact, original);
                        File.Move(artifact, artifact + ".saved");
                        try { Reject(() => owner.RequireOrderArtifact("S01", AcceptedHash)); }
                        finally { File.Move(artifact + ".saved", artifact); }
                        Reject(() => owner.StoreOrderArtifact("S01", original));
                    }
                    // Disposable fault injection only: no recovery path repairs
                    // or recreates missing/empty databases.
                    File.Move(journal, journal + ".saved");
                    try
                    {
                        Reject(() => owner.RequireOrderJournal());
                        Reject(() => owner.CreateOrderJournal());
                        using (File.Create(journal)) { }
                        Reject(() => owner.RequireOrderJournal());
                        File.Delete(journal);
                    }
                    finally
                    {
                        File.Move(journal + ".saved", journal);
                    }
                }
                await expectOwnership(root, "ACQUIRED:order");

                using Process resumed = start(unknown ? "bridge-blocked" : "bridge-resume", root);
                Task<string> output = resumed.StandardOutput.ReadToEndAsync();
                Task<string> errors = resumed.StandardError.ReadToEndAsync();
                try
                {
                    await resumed.WaitForExitAsync().WaitAsync(Deadline);
                    string result = await output;
                    if (resumed.ExitCode != 0 || !result.Split('\n').Any(line =>
                        line.Trim() == (unknown ? "BRIDGE_BLOCKED" : "BRIDGE_COMPLETED")))
                        throw new InvalidOperationException("Bridge reentry failed: " + result + await errors);

                    if (unknown ? File.Exists(artifact) || File.Exists(Path.Combine(root, $"issue-{Issue}.order.db.s02"))
                        : !File.ReadAllBytes(artifact).SequenceEqual(AcceptedBytes)
                        || !File.ReadAllBytes(Path.Combine(root, $"issue-{Issue}.order.db.s02")).SequenceEqual(AcceptedBytes))
                        throw new InvalidOperationException("Bridge external effect observation differs.");
                }
                finally
                {
                    if (!resumed.HasExited)
                    {
                        resumed.Kill(entireProcessTree: true);
                        await resumed.WaitForExitAsync().WaitAsync(Deadline);
                    }
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        Console.WriteLine("PASS: Akka SQL journal survives process kill; ownership, accepted artifact reuse and unknown-effect block.");
        Console.WriteLine("PASS: protected artifacts, journal identity, guarded SQL commands and live rollback sidecar recovery.");
    }

    private static async Task WaitForMarkerAsync(Process child, string expected)
    {
        while (await child.StandardOutput.ReadLineAsync() is string line)
        {
            // The fixture is synthetic and has no credentials or live model
            // output. Keep child diagnostics visible even if readiness times out.
            Console.WriteLine("[bridge child] " + line);
            if (line == expected)
                return;
        }

        throw new InvalidOperationException("Bridge child stopped before " + expected);
    }

    internal static DevelopmentOrderApproval FixtureApproval()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HaMi.Probe.Order")
            ?? throw new InvalidOperationException("Order fixture missing.");
        using var reader = new StreamReader(stream);
        string plan = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        byte[] body = Encoding.UTF8.GetBytes("\x60\x60\x60hami-order-json\n" + plan + "\x60\x60\x60\n");
        DevelopmentOrder order = DevelopmentOrder.ParseIssueBody(body);
        var document = order.Document;
        string repository = document.GetProperty("repository").GetString()!;
        string commit = document.GetProperty("baseCommit").GetString()!;
        string hash = DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("synthetic review"));
        var profile = new OrderReviewedProfile(repository, 42, "fixture-owner", commit, hash, hash, hash, hash, hash);
        DateTimeOffset time = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var comment = new OrderCommentObservation(1, 42, "fixture-owner",
            $"HaMi-Order-Approval: v1 1 {order.Sha256} {commit}", time, time);
        var observation = new OrderIssueObservation(repository, Issue, Issue,
            document.GetProperty("title").GetString()!, 42, "fixture-owner",
            true, true, body, time.AddMinutes(1), true, [comment]);
        return DevelopmentOrderApproval.Observe(observation, profile);
    }

    internal static async Task<int> RunChildAsync(string mode, string root, DevelopmentJobStateStore.Lease owner)
    {
        DevelopmentOrderClaim claim = owner.CurrentOrder
            ?? throw new InvalidOperationException("Order claim missing.");
        bool first = mode is "bridge-hold" or "bridge-unknown";
        string database = first ? owner.CreateOrderJournal() : owner.RequireOrderJournal();

        string connection = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        // The binding is immutable metadata, not a second workflow state.
        // Validate it before the Akka provider can initialize any tables.
        using (var binding = new SqliteConnection(connection))
        {
            binding.Open();
            CheckDurability(binding);
            CheckJournalIdentity(binding, claim, first);
            if (!first)
            {
                using var check = binding.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM hami_crash_probe";
                if (check.ExecuteScalar() is not long count || count != 0)
                    throw new InvalidOperationException("Uncommitted fixture transaction survived recovery.");
                Reject(() => CheckJournalIdentity(binding, claim with { JobId = new string('f', 32) }, false));
            }
            using var version = binding.CreateCommand();
            version.CommandText = "SELECT sqlite_version()";
            Console.WriteLine("SQLite native version: " + version.ExecuteScalar());
        }

        Console.WriteLine("BRIDGE_PHASE: identity validated; initializing provider.");
        var guard = new ProviderConnectionGuard();
        var options = new DataOptions()
            .UseConnectionString(ProviderName.SQLiteMS, connection)
            .UseInterceptor(new ProviderOpenInterceptor(guard))
            .UseInterceptor(guard);
        using IHost host = new HostBuilder()
            .ConfigureServices((_, services) => services.AddAkka("hami-persistence-probe", (builder, provider) =>
                builder.WithSqlPersistence(options)))
            .Build();
        await guard.AwaitAsync(host.StartAsync());
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            IActorRef actor = system.ActorOf(Props.Create(() => new BridgeActor(claim.PersistenceId, claim.ArchiveHash)), "bridge");
            async Task<BridgeState> ReadAsync()
            {
                Task<BridgeState> read = actor.Ask<BridgeState>(new ReadState(), Deadline);
                await guard.AwaitAsync(read);
                return await read;
            }
            async Task AdvanceAsync(string evt, int stage)
            {
                Task<BridgeState> pending = actor.Ask<BridgeState>(new Advance(evt), Deadline);
                await guard.AwaitAsync(pending);
                BridgeState state = await pending;
                if (state.Stage != stage)
                    throw new InvalidOperationException("Persisted transition was not acknowledged.");
            }

            Console.WriteLine("BRIDGE_PHASE: host started; awaiting replay.");
            var recovery = Stopwatch.StartNew();
            BridgeState current = await ReadAsync();
            guard.RequireObserved();
            Console.WriteLine($"BRIDGE_PHASE: replay state {current.Stage}; {recovery.ElapsedMilliseconds} ms.");
            string artifact = database + ".s01";
            if (first)
            {
                if (current.Stage != 0 || File.Exists(artifact))
                    throw new InvalidOperationException("New journal is not empty.");
                await AdvanceAsync("order-accepted:" + claim.ArchiveHash, 1);
                Console.WriteLine("BRIDGE_PHASE: order accepted.");
                await AdvanceAsync("s01-operation-reserved", 2);
                Console.WriteLine("BRIDGE_PHASE: operation reserved.");
                if (mode == "bridge-unknown")
                {
                    Console.WriteLine("BRIDGE_RESERVED");
                }
                else
                {
                    string hash = owner.StoreOrderArtifact("S01", AcceptedBytes);
                    await AdvanceAsync("s01-accepted:" + hash, 3);
                    Console.WriteLine("BRIDGE_PHASE: S01 persisted; opening rollback transaction.");
                    // Hold an actual native rollback transaction across process kill.
                    // This table is a test fixture; Akka event rows are untouched.
                    using var crashConnection = new SqliteConnection(connection);
                    crashConnection.Open();
                    CheckDurability(crashConnection);
                    using var transaction = crashConnection.BeginTransaction();
                    using var pending = crashConnection.CreateCommand();
                    pending.Transaction = transaction;
                    pending.CommandText = "INSERT INTO hami_crash_probe(value) VALUES (1)";
                    pending.ExecuteNonQuery();
                    if (!File.Exists(database + "-journal"))
                        throw new InvalidOperationException("Live rollback sidecar missing.");
                    owner.ValidateOrderJournalFiles();
                    Console.WriteLine("BRIDGE_ACCEPTED");
                    Console.Out.Flush();
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                    return 1;
                }

                owner.ValidateOrderJournalFiles();
                Console.Out.Flush();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 1;
            }

            if (mode == "bridge-blocked")
            {
                if (current.Stage != 2 || current.ArtifactHash is not null || File.Exists(artifact))
                    throw new InvalidOperationException("Unknown operation did not recover as reserved.");
                // Do not retry or release the reservation: outcome is unknown.
                Console.WriteLine("BRIDGE_BLOCKED");
                return 0;
            }

            if (mode != "bridge-resume" || current.Stage != 3 || current.ArtifactHash != AcceptedHash)
                throw new InvalidOperationException("Accepted step not recovered.");
            byte[] input = owner.RequireOrderArtifact("S01", AcceptedHash);

            // Reusing S01 is not a second write to its exclusive effect file.
            await AdvanceAsync("s02-operation-reserved", 4);
            string outputHash = owner.StoreOrderArtifact("S02", input);
            _ = owner.RequireOrderArtifact("S02", outputHash);
            await AdvanceAsync("s02-accepted", 5);
            guard.RequireObserved();
            Console.WriteLine("BRIDGE_COMPLETED");
            return 0;
        }
        finally
        {
            await host.StopAsync().WaitAsync(Deadline);
            owner.ValidateOrderJournalFiles();
        }
    }

    private static void CheckDurability(DbConnection connection)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        if (command.ExecuteScalar() is not string mode || !mode.Equals("delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite rollback journal mode required.");
        command.CommandText = "PRAGMA synchronous=FULL";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA synchronous";
        if (command.ExecuteScalar() is not long value || value < 2)
            throw new InvalidOperationException("SQLite FULL synchronization required.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid journal or claim was accepted.");
    }

    private static void CheckJournalIdentity(SqliteConnection connection, DevelopmentOrderClaim claim, bool first)
    {
        if (first)
        {
            using var initialize = connection.CreateCommand();
            initialize.CommandText = "CREATE TABLE hami_order_identity (persistence_id TEXT NOT NULL, archive_hash TEXT NOT NULL); "
                + "CREATE TABLE hami_crash_probe (value INTEGER NOT NULL); "
                + "INSERT INTO hami_order_identity VALUES ($id, $hash)";
            initialize.Parameters.AddWithValue("$id", claim.PersistenceId);
            initialize.Parameters.AddWithValue("$hash", claim.ArchiveHash);
            initialize.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT persistence_id, archive_hash FROM hami_order_identity";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != claim.PersistenceId
            || reader.GetString(1) != claim.ArchiveHash || reader.Read())
            throw new InvalidOperationException("Journal identity differs from claim.");
    }

    // LinqToDB 6 creates commands before opening their connection.
    // Configure at connection-open, then guard the actual execution boundary.
    // Both interceptors are registered in DataOptions for provider clones.
    private sealed class ProviderOpenInterceptor(ProviderConnectionGuard guard) : ConnectionInterceptor
    {
        public override void ConnectionOpened(ConnectionEventData eventData, DbConnection connection) =>
            guard.Configure(connection);

        public override Task ConnectionOpenedAsync(ConnectionEventData eventData, DbConnection connection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            guard.Configure(connection);
            return Task.CompletedTask;
        }
    }

    private sealed class ProviderConnectionGuard : CommandInterceptor
    {
        private readonly ConditionalWeakTable<DbConnection, object> configured = new();
        private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int commands;

        internal void Configure(DbConnection connection)
        {
            try
            {
                CheckDurability(connection);
                _ = configured.GetValue(connection, _ => new object());
            }
            catch (Exception ex)
            {
                Fail(ex);
                throw;
            }
        }

        private void Fail(Exception error)
        {
            if (failure.TrySetResult(error))
                Console.Error.WriteLine("BRIDGE_GUARD: " + error.Message);
        }

        private void RequireConfigured(DbCommand command)
        {
            if (command.Connection is not DbConnection connection
                || connection.State != ConnectionState.Open || !configured.TryGetValue(connection, out _))
            {
                var error = new InvalidOperationException("Provider attempted SQL execution without an open, configured connection.");
                Fail(error);
                throw error;
            }
            Interlocked.Increment(ref commands);
        }

        public override Option<int> ExecuteNonQuery(CommandEventData eventData, DbCommand command, Option<int> result)
        {
            RequireConfigured(command);
            return result;
        }

        public override Task<Option<int>> ExecuteNonQueryAsync(CommandEventData eventData, DbCommand command,
            Option<int> result, CancellationToken cancellationToken) =>
            Task.FromResult(ExecuteNonQuery(eventData, command, result));

        public override Option<object?> ExecuteScalar(CommandEventData eventData, DbCommand command, Option<object?> result)
        {
            RequireConfigured(command);
            return result;
        }

        public override Task<Option<object?>> ExecuteScalarAsync(CommandEventData eventData, DbCommand command,
            Option<object?> result, CancellationToken cancellationToken) =>
            Task.FromResult(ExecuteScalar(eventData, command, result));

        public override Option<DbDataReader> ExecuteReader(CommandEventData eventData, DbCommand command,
            CommandBehavior commandBehavior, Option<DbDataReader> result)
        {
            RequireConfigured(command);
            return result;
        }

        public override Task<Option<DbDataReader>> ExecuteReaderAsync(CommandEventData eventData, DbCommand command,
            CommandBehavior commandBehavior, Option<DbDataReader> result, CancellationToken cancellationToken) =>
            Task.FromResult(ExecuteReader(eventData, command, commandBehavior, result));

        internal async Task AwaitAsync(Task operation)
        {
            await Task.WhenAny(operation, failure.Task).WaitAsync(Deadline);
            if (failure.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("Provider durability guard rejected an operation.", await failure.Task);
            await operation;
        }

        internal void RequireObserved()
        {
            if (failure.Task.IsCompletedSuccessfully || Volatile.Read(ref commands) == 0)
                throw new InvalidOperationException("No valid guarded actual provider execution observed.");
        }
    }

    private sealed record ReadState;
    private sealed record Advance(string Event);
    private sealed record BridgeState(int Stage, string? ArtifactHash);

    private sealed class BridgeActor : ReceivePersistentActor
    {
        private BridgeState state = new(0, null);
        private bool recovered;
        private readonly string archiveHash;

        public override string PersistenceId { get; }

        public BridgeActor(string id, string archiveHash)
        {
            PersistenceId = id;
            this.archiveHash = archiveHash;
            // Recovery only folds acknowledged journal events; no effects.
            Recover<string>(evt => state = Next(state, evt));
            Recover<RecoveryCompleted>(_ => recovered = true);
            Command<ReadState>(_ => Sender.Tell(recovered ? state :
                throw new InvalidOperationException("Recovery incomplete.")));
            Command<Advance>(command =>
            {
                if (!recovered)
                    throw new InvalidOperationException("Recovery incomplete.");
                BridgeState next = Next(state, command.Event);
                IActorRef reply = Sender;
                Persist(command.Event, _ =>
                {
                    state = next;
                    reply.Tell(state);
                });
            });
        }

        private BridgeState Next(BridgeState previous, string evt) =>
            (previous.Stage, evt) switch
            {
                (0, _) when evt == "order-accepted:" + archiveHash => new(1, null),
                (1, "s01-operation-reserved") => new(2, null),
                (2, _) when evt == "s01-accepted:" + AcceptedHash => new(3, AcceptedHash),
                (3, "s02-operation-reserved") => previous with { Stage = 4 },
                (4, "s02-accepted") => previous with { Stage = 5 },
                _ => throw new InvalidOperationException("Bridge transition rejected.")
            };
    }
}
