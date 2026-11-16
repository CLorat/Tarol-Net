using Probe.Queries;

return args switch
{
    ["selftest", var kind] => await SelfTest.Run(kind),
    ["bench", .. var rest] => await Bench.Run(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("usage: selftest <memory|postgres> | bench <seed|catchup|commit|query|state|lag|rebuild> [args]");
    return 2;
}
