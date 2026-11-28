using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Sql.Hosting;
using HaMi.Agentic.Cli.Features.Development;
using LinqToDB;
using LinqToDB.Common;
using LinqToDB.Interceptors;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Shared guarded Akka/SQLite host for the existing probe and the isolated
// acceptance runner. No synthetic decisions, model, guest or issue effects.
internal sealed class OrderJournalHost : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private readonly IHost host;
    private readonly DevelopmentJobStateStore.Lease owner;

    private OrderJournalHost(IHost host, DevelopmentJobStateStore.Lease owner,
        string database, string connection, ProviderConnectionGuard guard)
    {
        this.host = host;
        this.owner = owner;
        Database = database;
        Connection = connection;
        Guard = guard;
    }

    internal string Database { get; }
    internal string Connection { get; }
    internal ProviderConnectionGuard Guard { get; }
    internal ActorSystem System => host.Services.GetRequiredService<ActorSystem>();

    // The caller must already own the exact claim and authenticated archive.
    // Test-only SQL is provided exclusively by the offline probe; acceptance
    // uses no such callback. It is not a caller-controlled SQL interface.
    internal static async Task<OrderJournalHost> OpenAsync(DevelopmentJobStateStore.Lease owner,
        bool first, Action<SqliteConnection>? offlineFixture = null)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        _ = owner.RequireArchivedOrder();
        string database = first ? owner.CreateOrderJournal() : owner.RequireOrderJournal();
        string connection = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        using (var binding = new SqliteConnection(connection))
        {
            binding.Open();
            CheckDurability(binding);
            CheckJournalIdentity(binding, claim, first);
            offlineFixture?.Invoke(binding);
            using var version = binding.CreateCommand();
            version.CommandText = "SELECT sqlite_version()";
            if (version.ExecuteScalar() is not string nativeVersion || nativeVersion != "3.54.1")
                throw new InvalidOperationException("Unexpected native SQLite version; reviewed 3.54.1 required.");
            Console.WriteLine("SQLite native version: " + nativeVersion);
        }

        Console.WriteLine("BRIDGE_PHASE: identity validated; initializing provider.");
        var guard = new ProviderConnectionGuard();
        var options = new DataOptions()
            .UseConnectionString(ProviderName.SQLiteMS, connection)
            .UseInterceptor(new ProviderOpenInterceptor(guard))
            .UseInterceptor(guard);
        IHost host = new HostBuilder()
            .ConfigureServices((_, services) => services.AddAkka("hami-persistence-probe", (builder, provider) =>
                builder.WithSqlPersistence(options)))
            .Build();
        try
        {
            await guard.AwaitAsync(host.StartAsync());
            return new(host, owner, database, connection, guard);
        }
        catch
        {
            // A failed bootstrap may have touched its journal. Preserve all
            // evidence and never retry or recreate on behalf of the caller.
            host.Dispose();
            throw;
        }
    }

    internal async Task StopAsync()
    {
        await host.StopAsync().WaitAsync(Deadline);
        owner.ValidateOrderJournalFiles();
    }

    public void Dispose() => host.Dispose();

    internal static void CheckDurability(DbConnection connection)
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

    internal static void CheckJournalIdentity(SqliteConnection connection, DevelopmentOrderClaim claim, bool first)
    {
        if (first)
        {
            using var initialize = connection.CreateCommand();
            initialize.CommandText = "CREATE TABLE hami_order_identity (persistence_id TEXT NOT NULL, archive_hash TEXT NOT NULL); "
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

    internal sealed class ProviderConnectionGuard : CommandInterceptor
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

}
