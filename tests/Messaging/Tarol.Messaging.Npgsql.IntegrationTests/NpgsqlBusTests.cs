using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Messaging.Contract;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Npgsql.IntegrationTests;

/// <summary>The bus's own rows: who may publish under a prefix (docs/migration/messaging.md §3.2), leases, order, dead letters, retention, hosting.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlBusTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Message Raw(string contract, string? data = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.CreateVersion7(),
        Source = Message.SystemOf(contract),
        Type = contract,
        Version = 1,
        Time = TimeProvider.System.GetUtcNow(),
        Correlation = Id<ICorrelation>.New(),
        Data = System.Text.Encoding.UTF8.GetBytes(data ?? "{}"),
    };

    private NpgsqlBus Bus(string schema, NpgsqlBusOptions? options = null)
        => new(postgres.DataSource, TimeProvider.System, (options ?? new NpgsqlBusOptions()) with { Schema = schema, PollInterval = TimeSpan.FromMilliseconds(20) });

    private static async Task<List<Incoming>> Collect(NpgsqlBus bus, Subscription subscription, int count, Func<Incoming, Task<Handled>>? answer = null)
    {
        var seen = new ConcurrentQueue<Incoming>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var consume = bus.Consume(subscription, async (incoming, ct) =>
        {
            seen.Enqueue(incoming);
            var handled = answer is null ? new Handled(Ack.Ack) : await answer(incoming);
            if (seen.Count >= count)
                await stop.CancelAsync();
            return handled;
        }, stop.Token);
        await consume;
        return [.. seen];
    }

    [Fact]
    public async Task Row_level_security_refuses_a_foreign_prefix_and_without_it_the_stamped_role_is_buried()
    {
        var secured = Postgres.NewSchema();
        var open = Postgres.NewSchema();
        var owner = Bus(secured);
        await owner.EnsureSchema(Ct);
        await Bus(open).EnsureSchema(Ct);
        await owner.Prepare([], [new Subscription("artflow.RecordPayment", ["billing.invoice-paid"])], Ct);
        await postgres.Execute("""
            SELECT pg_advisory_lock(hashtext('tarol.test.roles'));
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'artflow') THEN CREATE ROLE artflow LOGIN PASSWORD 'artflow'; END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'billing') THEN CREATE ROLE billing LOGIN PASSWORD 'billing'; END IF;
            END $$;
            SELECT pg_advisory_unlock(hashtext('tarol.test.roles'));
            """);
        await postgres.Execute(NpgsqlBusSchema.RowLevelSecurityScript(secured) + NpgsqlBusSchema.GrantScript("artflow", secured) + NpgsqlBusSchema.GrantScript("billing", secured)
            + NpgsqlBusSchema.GrantScript("artflow", open) + NpgsqlBusSchema.GrantScript("billing", open));
        var roles = new Dictionary<string, string> { ["artflow"] = "artflow", ["billing"] = "billing" };
        await using var billingDb = new NpgsqlSlimDataSourceBuilder(new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Username = "billing", Password = "billing" }.ConnectionString).Build();
        var billing = new NpgsqlBus(billingDb, TimeProvider.System, new NpgsqlBusOptions { Schema = secured, EnsureSchema = false, Roles = roles });

        var refused = await Should.ThrowAsync<PostgresException>(() => billing.Publish(Raw("artflow.auction-settled"), Ct));
        refused.SqlState.ShouldBe("42501");                                                          // row-level security
        await billing.Publish(Raw("billing.invoice-paid"), Ct);                                       // its own prefix
        var own = (await Collect(new NpgsqlBus(postgres.DataSource, TimeProvider.System, new NpgsqlBusOptions { Schema = secured, Roles = roles }),
            new Subscription("artflow.RecordPayment", ["billing.invoice-paid"]), 1)).Single();
        own.AuthenticatedAs.ShouldBe("billing");

        // without the policy the database still stamps the role: the consumer sees billing, not artflow, and buries it
        await using var harness = await new ContractHarness(new NpgsqlBus(postgres.DataSource, TimeProvider.System, new NpgsqlBusOptions { Schema = open, Roles = roles }),
            GeneratedConsumers.Set, "billing", new DeliveryPolicy { MaxAttempts = 2 }).Start();
        var forged = TransportContractMessages.Settled();
        await new NpgsqlBus(billingDb, TimeProvider.System, new NpgsqlBusOptions { Schema = open, EnsureSchema = false }).Publish(forged, Ct);
        await harness.Until(() => harness.DeadLetters.List(10, Ct).Result.Count == 2, "two dead letters");
        (await harness.DeadLetters.List(10, Ct)).ShouldAllBe(d => d.Reason == "Messaging.SourceMismatch: contract artflow.auction-settled, ce-source artflow, authenticated billing");
        harness.Seen.ShouldAllBe(s => s.Incoming.AuthenticatedAs == "billing");
        // a role the map does not know is never a source
        await billing.Publish(Raw("billing.invoice-paid"), Ct);
        (await Collect(new NpgsqlBus(postgres.DataSource, TimeProvider.System, new NpgsqlBusOptions { Schema = secured, Roles = new Dictionary<string, string>() }),
            new Subscription("artflow.RecordPayment", ["billing.invoice-paid"]), 1, static _ => Task.FromResult(new Handled(Ack.Retry))))
            .Single().AuthenticatedAs.ShouldBe("role:billing");
    }

    [Fact]
    public async Task A_delivery_that_outlives_its_lease_is_claimed_again_and_the_late_answer_changes_nothing()
    {
        var schema = Postgres.NewSchema();
        var bus = Bus(schema);
        var subscription = new Subscription("t.Slow", ["t.slow"]) { Concurrency = 2, Lease = TimeSpan.FromMilliseconds(300) };
        await bus.Prepare([], [subscription], Ct);
        await bus.Publish(Raw("t.slow"), Ct);

        var seen = await Collect(bus, subscription, 2, async incoming =>
        {
            if (incoming.Attempt == 1)
                await Task.Delay(1500);                                                           // the lease runs out meanwhile
            return new Handled(Ack.Ack);
        });
        await Task.Delay(1700, Ct);                                                             // the first answer arrives late

        seen.Select(i => i.Attempt).Order().ShouldBe([1, 2]);
        (await bus.Pending("t.Slow", Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_batch_that_outlived_half_its_lease_hands_its_tail_back_so_nothing_runs_twice()
    {
        var schema = Postgres.NewSchema();
        var bus = Bus(schema);
        var subscription = new Subscription("t.Batch", ["t.batch"]) { Concurrency = 2, Lease = TimeSpan.FromSeconds(4) };
        await bus.Prepare([], [subscription], Ct);
        for (var i = 0; i < 3; i++)
            await bus.Publish(Raw("t.batch"), Ct);                                              // one loop claims all three

        // each delivery takes more than half the lease: without handing the tail back, the second loop claims it again
        // once the lease ran out and runs it alongside
        var seen = await Collect(bus, subscription, 3, static async _ =>
        {
            await Task.Delay(2500);
            return new Handled(Ack.Ack);
        });

        seen.Select(i => i.Attempt).ShouldBe([1, 1, 1]);                                        // released claims are not counted
        seen.Select(i => i.Message!.Id).Distinct().Count().ShouldBe(3);
        (await bus.Pending("t.Batch", Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_retry_presents_its_claim_and_counts_the_attempt()
    {
        var schema = Postgres.NewSchema();
        var bus = Bus(schema);
        var subscription = new Subscription("t.Retry", ["t.retry"]);
        await bus.Prepare([], [subscription], Ct);
        await bus.Publish(Raw("t.retry"), Ct);

        var seen = await Collect(bus, subscription, 3, static i => Task.FromResult(i.Attempt < 3 ? new Handled(Ack.Retry, TimeSpan.FromMilliseconds(30)) : new Handled(Ack.Ack)));

        seen.Select(i => i.Attempt).ShouldBe([1, 2, 3]);
        (await bus.Pending("t.Retry", Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_claim_into_reused_free_space_keeps_the_publish_order()
    {
        // §5 item 6: a subscription's deliveries split across the heap (the second half lands in space freed by another
        // subscription); UPDATE … RETURNING follows the heap, the claim orders after it
        var schema = Postgres.NewSchema();
        var bus = Bus(schema, new NpgsqlBusOptions { Batch = 64 });
        var subscription = new Subscription("t.Ordered", ["t.n"]);
        await bus.Prepare([], [subscription, new Subscription("t.Other", ["t.o"])], Ct);
        await postgres.Execute($"""
            INSERT INTO {schema}.messages (id, type, version, source, headers, data, published_at)
              SELECT gen_random_uuid(), 't.o', 1, 't', jsonb_build_object(), '', now() FROM generate_series(1, 2000);
            INSERT INTO {schema}.deliveries (subscription, message_id, type, version, due_at)
              SELECT 't.Other', id, type, version, now() FROM {schema}.messages ORDER BY random();
            """);
        for (var n = 1; n <= 150; n++)
            await bus.Publish(Raw("t.n", "{\"n\":" + n + "}"), Ct);
        await postgres.Execute($"DELETE FROM {schema}.deliveries WHERE subscription = 't.Other' AND id < 900");
        await postgres.Execute($"VACUUM {schema}.deliveries");
        for (var n = 151; n <= 300; n++)
            await bus.Publish(Raw("t.n", "{\"n\":" + n + "}"), Ct);
        await postgres.Execute($"ANALYZE {schema}.deliveries; ANALYZE {schema}.messages");

        var seen = await Collect(bus, subscription, 300);

        seen.Select(i => int.Parse(System.Text.Encoding.UTF8.GetString(i.Raw.Span)[5..^1], System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(Enumerable.Range(1, 300));
    }

    [Fact]
    public async Task The_schema_is_created_once_by_many_at_once_and_an_incompatible_newer_version_is_refused()
    {
        var schema = Postgres.NewSchema();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Bus(schema).EnsureSchema(Ct)));

        (await postgres.Scalar($"SELECT count(*) FROM {schema}.schema_version")).ShouldBe(1L);
        (await postgres.Scalar($"SELECT compatible_from FROM {schema}.schema_version")).ShouldBe(1);
        await postgres.Execute($"UPDATE {schema}.schema_version SET version = 99");
        (await Bus(schema).EnsureSchema(SchemaMode.Upgrade, Ct)).ShouldBe(new SchemaState(99, 1, 0));        // newer, still compatible
        await postgres.Execute($"UPDATE {schema}.schema_version SET compatible_from = 99");
        (await Should.ThrowAsync<InvalidOperationException>(() => Bus(schema).EnsureSchema(Ct))).Message.ShouldContain("version 99, compatible from version 99");
        Should.Throw<ArgumentException>(() => Bus("Bad-Schema"));
    }

    [Fact]
    public async Task A_bus_schema_of_step_14_gains_compatible_from_and_VerifyOnly_checks_without_changing()
    {
        var schema = Postgres.NewSchema();
        var verify = await Should.ThrowAsync<InvalidOperationException>(() => Bus(schema).EnsureSchema(SchemaMode.VerifyOnly, Ct));
        verify.Message.ShouldContain("needs version 1");
        await postgres.Execute(NpgsqlBusSchema.Script(schema));                                              // what steps 11–14 created
        (await Bus(schema).EnsureSchema(SchemaMode.Upgrade, Ct)).ShouldBe(new SchemaState(1, 1, 0));
        (await Bus(schema).EnsureSchema(SchemaMode.VerifyOnly, Ct)).ShouldBe(new SchemaState(1, 1, 0));
        NpgsqlBusSchema.UpgradeScript(schema, 0).ShouldContain($"CREATE TABLE IF NOT EXISTS {schema}.deliveries");
        NpgsqlBusSchema.UpgradeScript(schema, 1).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dead_letters_of_step_14_are_upgraded_next_to_the_store_and_keep_their_rows()
    {
        var schema = Postgres.NewSchema();
        await postgres.Execute(NpgsqlBusSchema.DeadLettersScript(schema));                                    // unversioned, as steps 11–14 left it
        var dead = new NpgsqlMessageDeadLetters(postgres.DataSource, TimeProvider.System, schema);
        var message = Raw("billing.invoice-paid", "{\"x\":1}");
        await dead.Add("artflow.RecordPayment", new Incoming(message, 3, "billing", message.Data, message.Type), "Error NotFound", Ct);

        // the store and messages_dead share the schema and upgrade at once: one advisory lock per schema serializes them
        var store = new NpgsqlAggregateStore(postgres.DataSource, new EventCodec(new System.Text.Json.JsonSerializerOptions(ContractJson.Default.Options)), TimeProvider.System,
            new NpgsqlStoreOptions { Schema = schema });
        var results = await Task.WhenAll(
            dead.EnsureSchema(SchemaMode.Upgrade, Ct), new NpgsqlMessageDeadLetters(postgres.DataSource, TimeProvider.System, schema).EnsureSchema(SchemaMode.Upgrade, Ct),
            store.EnsureSchema(SchemaMode.Upgrade, Ct), store.EnsureSchema(SchemaMode.Upgrade, Ct));

        results[0].Version.ShouldBe(NpgsqlBusSchema.DeadLettersVersion);
        (results[0].StepsRun + results[1].StepsRun).ShouldBe(NpgsqlBusSchema.DeadLettersSteps(schema).Count);
        results[2].Version.ShouldBe(NpgsqlSchema.Version);
        (await postgres.Scalar($"SELECT version FROM {schema}.messages_dead_version")).ShouldBe(2);
        (await postgres.Scalar($"SELECT indisvalid FROM pg_index WHERE indexrelid = '{schema}.messages_dead_at'::regclass")).ShouldBe(true);
        (await postgres.Scalar($"SELECT count(*) FROM {schema}.operations_audit")).ShouldBe(0L);
        (await dead.List(10, Ct)).ShouldHaveSingleItem().Reason.ShouldBe("Error NotFound");
        (await dead.EnsureSchema(SchemaMode.VerifyOnly, Ct)).ShouldBe(new SchemaState(2, 1, 0));
        NpgsqlBusSchema.DeadLettersUpgradeScript(schema, 1).ShouldContain($"messages_dead_at ON {schema}.messages_dead (dead_at)");
    }

    [Fact]
    public async Task Purging_keeps_messages_a_subscription_still_has_to_receive()
    {
        var schema = Postgres.NewSchema();
        var bus = Bus(schema);
        var subscription = new Subscription("t.Purge", ["t.purge"]);
        await bus.Prepare([], [subscription], Ct);
        await bus.Publish(Raw("t.purge"), Ct);
        _ = await Collect(bus, subscription, 1);                                                      // acknowledged
        await bus.Publish(Raw("t.purge"), Ct);                                                       // still waiting

        (await bus.PurgeMessages(TimeProvider.System.GetUtcNow().AddMinutes(1), 100, Ct)).ShouldBe(1);
        (await postgres.Scalar($"SELECT count(*) FROM {schema}.messages")).ShouldBe(1L);
    }

    [Fact]
    public async Task The_bus_retention_is_a_maintenance_task_a_job_host_runs_hourly()
    {
        var schema = Postgres.NewSchema();
        var services = new ServiceCollection();
        services.AddTarol(ContractJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>().Add<NoteLog>())
            .AddMessaging("billing", m => m.UseNpgsqlBus(postgres.ConnectionString, new NpgsqlBusOptions { Schema = schema, Retention = TimeSpan.FromDays(2), PurgeBatch = 1 }));
        await using var provider = services.BuildServiceProvider();
        var bus = (NpgsqlBus)provider.GetRequiredService<IMessageTransport>();
        await bus.EnsureSchema(Ct);
        await bus.Publish(Raw("t.retention"), Ct);
        await bus.Publish(Raw("t.retention"), Ct);

        var task = provider.GetServices<MaintenanceTask>().ShouldHaveSingleItem();

        (task.Name, task.Every).ShouldBe((NpgsqlMessagingBuilderExtensions.PurgeMessagesJob, TimeSpan.FromHours(1)));
        (await task.Run(provider, TimeProvider.System.GetUtcNow().AddDays(1), Ct)).ShouldBe(0);            // within the retention
        (await task.Run(provider, TimeProvider.System.GetUtcNow().AddDays(3), Ct)).ShouldBe(2);            // in batches of one
        (await postgres.Scalar($"SELECT count(*) FROM {schema}.messages")).ShouldBe(0L);
        Should.Throw<ArgumentOutOfRangeException>(() => new ServiceCollection().AddTarol(ContractJson.Default, codec => codec.Add<Auction>())
            .AddMessaging("billing", m => m.UseNpgsqlBus(postgres.ConnectionString, new NpgsqlBusOptions { Retention = TimeSpan.Zero })));
    }

    [Fact]
    public async Task Dead_letters_keep_headers_and_bytes_and_are_taken_once()
    {
        var schema = Postgres.NewSchema();
        var dead = new NpgsqlMessageDeadLetters(postgres.DataSource, TimeProvider.System, schema);
        await dead.EnsureSchema(Ct);
        await dead.EnsureSchema(Ct);
        var message = Raw("billing.invoice-paid", "{\"x\":1}");

        await dead.Add("artflow.RecordPayment", new Incoming(message, 3, "billing", message.Data, message.Type), "Error NotFound", Ct);
        await dead.Add("artflow.RecordPayment", new Incoming(null, 1, null, "poison"u8.ToArray(), "billing.invoice-paid"), "Messaging.Malformed", Ct);

        var listed = await dead.List(10, Ct);
        listed.Count.ShouldBe(2);
        listed[0].MessageId.ShouldBe(message.Id);
        listed[0].Headers["ce-type"].ShouldBe("billing.invoice-paid");
        listed[0].Attempts.ShouldBe(3);
        System.Text.Encoding.UTF8.GetString(listed[0].Data.Span).ShouldBe("{\"x\":1}");
        listed[1].MessageId.ShouldBeNull();
        listed[1].Headers.ShouldBeEmpty();
        (await dead.Take(listed[0].Id, Ct))!.Reason.ShouldBe("Error NotFound");
        (await dead.Take(listed[0].Id, Ct)).ShouldBeNull();
        (await dead.List(10, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Dead_messages_page_filter_and_are_taken_or_discarded_with_an_audit_row()
    {
        var schema = Postgres.NewSchema();
        var dead = new NpgsqlMessageDeadLetters(postgres.DataSource, TimeProvider.System, schema);
        await dead.EnsureSchema(Ct);
        var tenant = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            var message = Raw("billing.invoice-paid", "{}") with { Tenant = i < 3 ? (Tarol.StronglyTypedId.Id<ITenant>)tenant : Tarol.Options.Option.None };
            await dead.Add(i % 2 == 0 ? "artflow.A" : "artflow.B", new Incoming(message, 3, "billing", message.Data, message.Type), "Error NotFound", Ct);
        }

        var first = await dead.List(new DeadLetterQuery { Limit = 2 }, Ct);
        var next = await dead.List(new DeadLetterQuery { Limit = 2, After = first[^1].Id }, Ct);
        var last = await dead.List(new DeadLetterQuery { Limit = 2, After = next[^1].Id }, Ct);
        (first.Count, next.Count, last.Count).ShouldBe((2, 2, 1));
        (await dead.List(new DeadLetterQuery { Tenant = (Tarol.StronglyTypedId.Id<ITenant>)tenant }, Ct)).Count.ShouldBe(3);
        (await dead.List(new DeadLetterQuery { Name = "artflow.A" }, Ct)).Count.ShouldBe(3);
        (await dead.Count(Ct)).ShouldBe(5);

        (await dead.Take(first[0].Id, "ops-alice", Ct))!.Id.ShouldBe(first[0].Id);
        (await dead.Discard(first[1].Id, "ops-bob", Ct)).ShouldBeTrue();
        (await dead.Discard(first[1].Id, "ops-bob", Ct)).ShouldBeFalse();
        (await dead.Discard(next[0].Id, Ct)).ShouldBeTrue();                                           // unaudited (the port)

        await using var audit = postgres.DataSource.CreateCommand($"SELECT actor || ':' || action || ':' || target FROM {schema}.operations_audit ORDER BY id");
        var rows = new List<string>();
        await using (var reader = await audit.ExecuteReaderAsync(Ct))
            while (await reader.ReadAsync(Ct))
                rows.Add(reader.GetString(0));
        rows.ShouldBe([$"ops-alice:redrive:messages:{first[0].Id}", $"ops-bob:discard:messages:{first[1].Id}"]);
        (await dead.Count(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task The_backlog_counts_the_deliveries_of_the_applications_subscriptions()
    {
        var schema = Postgres.NewSchema();
        var bus = Bus(schema);
        await bus.Prepare([], [new Subscription("artflow.One", ["t.one"]), new Subscription("artflow.Two", ["t.one", "t.two"]), new Subscription("artflowx.Other", ["t.one"])], Ct);
        await bus.Publish(Raw("t.one"), Ct);
        await bus.Publish(Raw("t.one"), Ct);
        await bus.Publish(Raw("t.two"), Ct);

        (await bus.Backlog("artflow", Ct)).ShouldBe([("artflow.One", 2L), ("artflow.Two", 3L)]);           // not artflowx.*
    }

    [Fact]
    public async Task A_hosted_system_publishes_through_its_outbox_and_consumes_on_the_bus()
    {
        var storeSchema = "s" + Guid.NewGuid().ToString("N")[..12];
        var busSchema = Postgres.NewSchema();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTarol(ContractJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>().Add<NoteLog>())
            .UseNpgsqlStore(postgres.ConnectionString, new NpgsqlStoreOptions { Schema = storeSchema })
            .AddReactionWorker(o => o.PollInterval = TimeSpan.FromMilliseconds(20))
            .AddMessaging("billing", m => m
                .Consume(GeneratedConsumers.Set)
                .Publish(GeneratedPublications.Set)
                .Trust("artflow", t => t.AnyTenant())
                .UseNpgsqlBus(postgres.ConnectionString, new NpgsqlBusOptions { Schema = busSchema, Notify = true })
                .UseNpgsqlDeadLetters(storeSchema));
        using var host = builder.Build();
        await host.StartAsync(Ct);
        var store = host.Services.GetRequiredService<IAggregateStore>();
        var auction = Auction.Open(Guid.CreateVersion7());
        var alice = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
        (await store.Append(auction, 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        Primitives.AggregateChanges.Committed(auction);
        _ = auction.Settle();
        (await store.Append(auction, 1, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();

        var clock = Stopwatch.StartNew();
        while ((long)(await postgres.Scalar($"SELECT count(*) FROM {storeSchema}.events"))! < 4 && clock.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(50, Ct);
        await host.StopAsync(Ct);

        (await postgres.Scalar($"SELECT count(*) FROM {storeSchema}.events WHERE actor_id = 'Consumers.IssueInvoice' AND on_behalf_of = 'alice'")).ShouldBe(1L);
        (await postgres.Scalar($"SELECT count(*) FROM {storeSchema}.events WHERE actor_id = 'Consumers.Sales'")).ShouldBe(1L);
        (await postgres.Scalar($"SELECT count(*) FROM {busSchema}.messages WHERE type = 'artflow.auction-settled'")).ShouldBe(1L);
        (await postgres.Scalar($"SELECT count(*) FROM {busSchema}.deliveries")).ShouldBe(0L);
        (await postgres.Scalar($"SELECT count(*) FROM {storeSchema}.outbox")).ShouldBe(0L);
        (await postgres.Scalar($"SELECT count(*) FROM {storeSchema}.messages_dead")).ShouldBe(0L);
    }
}

/// <summary>Messages the bus tests share with the contract.</summary>
internal static class TransportContractMessages
{
    public static Message Settled() => new()
    {
        Id = Guid.CreateVersion7(),
        Source = "artflow",
        Type = "artflow.auction-settled",
        Version = 1,
        Time = TimeProvider.System.GetUtcNow(),
        Tenant = TransportContract.TenantA,
        Correlation = Id<ICorrelation>.New(),
        Data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AuctionSettledV1(Guid.NewGuid(), 150m), ContractJson.Default.AuctionSettledV1),
    };
}
