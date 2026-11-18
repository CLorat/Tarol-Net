using Probe.Search;

return args switch
{
    ["selftest", var engine] => await SelfTest.Run(engine),
    ["feeder", .. var rest] => await Feeder.Run(rest),
    ["bench", .. var rest] => await Bench.Run(rest),
    ["lag", .. var rest] => await Bench.Lag(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("usage: selftest <pg|meili|typesense|opensearch> | feeder [bench <streams>] | bench <engine> [sizes] | lag <engine>");
    return 2;
}
