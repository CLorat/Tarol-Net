using System.Globalization;
using Probe.Jobs;

static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

switch (args)
{
    case ["selftest", var kind]: return await SelfTest.Run(kind);
    case ["worker", var variant, var schema, var name, var jobs]: return await Worker.Run(variant, schema, name, I(jobs));
    case ["chaos", var variant, var seconds, var workers, var jobs, var mode, .. var rest]:
        return await Chaos.Run(variant, I(seconds), I(workers), I(jobs), mode, rest is [var n] ? I(n) : 0);
    case ["bench", .. var rest]: await Bench.Run(rest); return 0;
    default:
        Console.WriteLine("usage: selftest <cron|test|postgres> | chaos <A|Anokey|B|D> <seconds> <workers> <jobs> <none|kill|freeze|both> [crashEvery] | bench <claim|tenants|backlog|lease|cron> …");
        return 2;
}
