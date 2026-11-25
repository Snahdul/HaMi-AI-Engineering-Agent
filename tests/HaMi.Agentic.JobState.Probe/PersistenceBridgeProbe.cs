using System.Diagnostics;
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
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly byte[] AcceptedBytes = Encoding.UTF8.GetBytes("synthetic accepted S01");
    private static readonly string AcceptedHash = DevelopmentJobState.Hash(AcceptedBytes);

    internal static async Task RunAsync(string prefix, Func<string, string, Process> start,
        Func<string, string, Task> expectOwnership)
    {
        foreach (bool unknown in new[] { false, true })
        {
            string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
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
                string journal = Path.Combine(root, "bridge.db");
                if (!File.Exists(journal))
                    throw new InvalidOperationException("SQLite journal missing after process interruption.");
                await expectOwnership(root, "ACQUIRED:accepted");

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

    internal static async Task<int> RunChildAsync(string mode, string root, string jobId)
    {
        string database = Path.Combine(root, "bridge.db");
        bool first = mode is "bridge-hold" or "bridge-unknown";
        if (first == File.Exists(database))
            throw new InvalidOperationException("Bridge journal presence rejected.");

        string connection = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = first ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        using IHost host = new HostBuilder()
            .ConfigureServices((_, services) => services.AddAkka("hami-persistence-probe", (builder, provider) =>
                builder.WithSqlPersistence(connectionString: connection, providerName: ProviderName.SQLiteMS)))
            .Build();
        await host.StartAsync().WaitAsync(Deadline);
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            IActorRef actor = system.ActorOf(Props.Create(() => new BridgeActor("bridge-v1-" + jobId)), "bridge");
            async Task<BridgeState> ReadAsync() => await actor.Ask<BridgeState>(new ReadState(), Deadline);
            async Task AdvanceAsync(string evt, int stage)
            {
                BridgeState state = await actor.Ask<BridgeState>(new Advance(evt), Deadline);
                if (state.Stage != stage)
                    throw new InvalidOperationException("Persisted transition was not acknowledged.");
            }

            BridgeState current = await ReadAsync();
            string artifact = Path.Combine(root, "s01.bin");
            if (first)
            {
                if (current.Stage != 0 || File.Exists(artifact))
                    throw new InvalidOperationException("New journal is not empty.");
                await AdvanceAsync("order-accepted", 1);
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
        }
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

        public override string PersistenceId { get; }

        public BridgeActor(string id)
        {
            PersistenceId = id;
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

        private static BridgeState Next(BridgeState previous, string evt) =>
            (previous.Stage, evt) switch
            {
                (0, "order-accepted") => new(1, null),
                (1, "s01-operation-reserved") => new(2, null),
                (2, _) when evt == "s01-accepted:" + AcceptedHash => new(3, AcceptedHash),
                (3, "s02-operation-reserved") => previous with { Stage = 4 },
                (4, "s02-accepted") => previous with { Stage = 5 },
                _ => throw new InvalidOperationException("Bridge transition rejected.")
            };
    }
}
