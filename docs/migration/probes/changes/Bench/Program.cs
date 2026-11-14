using ArtFlow;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Tarol.Changes;
using Tarol.Changes.Generated;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

BenchmarkSwitcher.FromAssembly(typeof(PipelineBench).Assembly).Run(args);

/// <summary>A store that costs the same in every benchmark: load = replay of one event, append = nothing.</summary>
public sealed class FixedStore(StreamEvent[] history) : IAggregateStore
{
    public ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T> => ValueTask.FromResult<Option<T>>(T.Rehydrate(id, history));
    public ValueTask<Result> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T> => ValueTask.FromResult(Result.Success());
    public ValueTask<Option<ChangeOutcome>> Seen(IdempotencyKey key, CancellationToken cancellationToken) => ValueTask.FromResult<Option<ChangeOutcome>>(Option.None);
}

[MemoryDiagnoser]
public class PipelineBench
{
    private readonly TimeProvider _time = TimeProvider.System;
    private Id<Auction> _id;
    private FixedStore _store = null!;
    private ChangePipeline _pipeline = null!;
    private OperationContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        _id = Id<Auction>.New();
        var auction = Auction.Create(_id, "alice", new Title("Bench"), new Amount(10), _time.GetUtcNow().AddDays(2), _time.GetUtcNow()).GetValueOrThrow();
        _store = new FixedStore(auction.UncommittedChanges.ToArray());
        _pipeline = new ChangePipeline(_store, _time);
        _context = new OperationContext(Actor.User("alice"), Option.None, Id<ICorrelation>.New(), Option.None);
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<bool> HandWritten()
    {
        var input = new CloseAuction(_id);
        if (input.Validated() is { IsSuccess: false }) return false;
        if (!(await _store.Load(input.Auction, _context, default)).TryGetValue(out Auction? auction)) return false;
        var expected = auction.Version;
        if (!auction.Close(_time.GetUtcNow()).IsSuccess || !AggregateChanges.Prepare(auction).IsSuccess) return false;
        if (!(await _store.Append(auction, expected, _context, Option.None, default)).IsSuccess) return false;
        AggregateChanges.Committed(auction);
        return true;
    }

    [Benchmark]
    public async ValueTask<bool> Pipeline() => (await _pipeline.Run(new CloseAuction(_id), _context)).IsSuccess;

    [Benchmark]
    public async ValueTask<bool> GeneratedDispatch() => (await _pipeline.Dispatch(new CloseAuction(_id), _context)).IsSuccess;
}
