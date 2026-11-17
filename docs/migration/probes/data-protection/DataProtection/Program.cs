using Probe.DataProtection;

switch (args)
{
    case ["selftest", var kind]: return await SelfTest.Run(kind);
    case ["bench", .. var rest]: await Bench.Run(rest); return 0;
    default:
        Console.WriteLine("usage: selftest <protect|forget> | bench <crypto|codec|load|commit|export|forget|retention> [n]");
        return 2;
}
