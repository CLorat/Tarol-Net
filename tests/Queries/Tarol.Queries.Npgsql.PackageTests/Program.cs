using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Queries.Npgsql;
using Tarol.Queries.Npgsql.PackageTests;

var script = NpgsqlReadModelSchema.Script<NoteRow>();
var json = new JsonSerializerOptions(NotesJson.Default.Options);
var services = new ServiceCollection();
services.AddSingleton(_ => new NpgsqlSlimDataSourceBuilder("Host=localhost;Database=none").EnableArrays().Build());
new TarolBuilder(services, json, new EventCodec(json).Add<Note>()).UseNpgsqlReadModels();
await using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<IReadModels>().Store<NoteRow>();

if (!script.Contains("tarol.rm_notes_v1", StringComparison.Ordinal) || !script.Contains("written_at timestamptz NOT NULL", StringComparison.Ordinal)
    || store is not NpgsqlReadModelStore<NoteRow> { Table: "tarol.rm_notes_v1" })
{
    Console.Error.WriteLine("Tarol.Queries.Npgsql package consumer: unexpected script or store\n" + script);
    return 1;
}
Console.WriteLine($"Tarol.Queries.Npgsql package consumer: {((NpgsqlReadModelStore<NoteRow>)store).Table}, {script.Split('\n').Length} script lines");
return 0;
