using System.Diagnostics;
using System.Data.Common;
using System.Reflection;
using System.Text;
using Akka.Actor;
using Akka.Hosting;
using Akka.Pattern;
using Akka.Persistence;
using Akka.Persistence.Sql.Hosting;
using HaMi.Agentic.Cli.Features.Development;
using LinqToDB;
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
                    try
                    {
                        await WaitForMarkerAsync(child, unknown ? "BRIDGE_RESERVED" : "BRIDGE_ACCEPTED").WaitAsync(Deadline);
                        if (child.HasExited)
                            throw new InvalidOperationException("Bridge owner stopped before the crash test.");
                        await expectOwnership(root, "BUSY");
                    }
                    finally
                    {
                        if (!child.HasExited)
                            child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(Deadline);
                    }

                    if (child.ExitCode == 0)
                        throw new InvalidOperationException("Bridge owner did not terminate abnormally.");
                }

                string artifact = Path.Combine(root, "s01.bin");
                using (var owner = store.AcquireOrder(Issue))
                {
                    _ = owner.RequireArchivedOrder();
                    string journal = owner.RequireOrderJournal();
                    Reject(() => owner.CreateOrderJournal());
                    Reject(() => store.Acquire(Issue).Dispose());
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

                    if (unknown ? File.Exists(artifact) || File.Exists(Path.Combine(root, "s02.bin"))
                        : !File.ReadAllBytes(artifact).SequenceEqual(AcceptedBytes)
                        || !File.ReadAllBytes(Path.Combine(root, "s02.bin")).SequenceEqual(AcceptedBytes))
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
    }

    private static async Task WaitForMarkerAsync(Process child, string expected)
    {
        while (await child.StandardOutput.ReadLineAsync() is string line)
        {
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
        int observedConnections = 0;
        var options = new DataOptions()
            .UseConnectionString(ProviderName.SQLiteMS, connection)
            .UseAfterConnectionOpened(opened =>
            {
                CheckDurability(opened);
                Interlocked.Increment(ref observedConnections);
            });
        using IHost host = new HostBuilder()
            .ConfigureServices((_, services) => services.AddAkka("hami-persistence-probe", (builder, provider) =>
                builder.WithSqlPersistence(options)))
            .Build();
        await host.StartAsync().WaitAsync(Deadline);
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            IActorRef actor = system.ActorOf(Props.Create(() => new BridgeActor(claim.PersistenceId, claim.ArchiveHash)), "bridge");
            async Task<BridgeState> ReadAsync() => await actor.Ask<BridgeState>(new ReadState(), Deadline);
            async Task AdvanceAsync(string evt, int stage)
            {
                BridgeState state = await actor.Ask<BridgeState>(new Advance(evt), Deadline);
                if (state.Stage != stage)
                    throw new InvalidOperationException("Persisted transition was not acknowledged.");
            }

            BridgeState current = await ReadAsync();
            if (Volatile.Read(ref observedConnections) == 0)
                throw new InvalidOperationException("No actual provider connection was observed.");
            string artifact = Path.Combine(root, "s01.bin");
            if (first)
            {
                if (current.Stage != 0 || File.Exists(artifact))
                    throw new InvalidOperationException("New journal is not empty.");
                await AdvanceAsync("order-accepted:" + claim.ArchiveHash, 1);
                await AdvanceAsync("s01-operation-reserved", 2);
                if (mode == "bridge-unknown")
                {
                    Console.WriteLine("BRIDGE_RESERVED");
                }
                else
                {
                    WriteOnce(artifact, AcceptedBytes);
                    await AdvanceAsync("s01-accepted:" + AcceptedHash, 3);
                    Console.WriteLine("BRIDGE_ACCEPTED");
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
            byte[] input = File.ReadAllBytes(artifact);
            if (DevelopmentJobState.Hash(input) != current.ArtifactHash)
                throw new InvalidOperationException("Accepted artifact missing or changed.");

            // Reusing S01 is not a second write to its exclusive effect file.
            await AdvanceAsync("s02-operation-reserved", 4);
            WriteOnce(Path.Combine(root, "s02.bin"), input);
            await AdvanceAsync("s02-accepted", 5);
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

    private static void WriteOnce(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
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
