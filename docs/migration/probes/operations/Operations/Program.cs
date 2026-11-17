using Probe.Operations;

switch (args)
{
    case ["selftest", var kind]: return await SelfTest.Run(kind);
    case ["bench", .. var rest]: await Bench.Run(rest); return 0;
    default:
        Console.WriteLine("usage: selftest <schema|dead|health|spans> | bench <metrics|gauges|upgrade|retention|drop|runners>");
        return 2;
}
