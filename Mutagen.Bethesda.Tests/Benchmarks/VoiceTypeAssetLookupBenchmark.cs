using BenchmarkDotNet.Attributes;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim.Records.Assets.VoiceType;
using BenchmarkDotNet.Engines;
using Mutagen.Bethesda.Plugins.Cache;

namespace Mutagen.Bethesda.Tests.Benchmarks;

[MemoryDiagnoser, InProcess]
public class VoiceTypeAssetLookupSetupBenchmark
{
    IAssetLinkCache assetCache = null!;

    [GlobalSetup]
    public void Setup()
    {
        var env = GameEnvironmentBuilder.Create(GameRelease.SkyrimSE)
            .Build();
        assetCache = env.LinkCache.CreateImmutableAssetLinkCache();
    }

    [Benchmark]
    public void Prepare()
    {
        var lookup = new VoiceTypeAssetLookup();
        lookup.Prep(assetCache);
    }
}

[MemoryDiagnoser, InProcess, WarmupCount(1), IterationCount(4)]
public class VoiceTypeAssetLookupRuntimeBenchmark
{
    VoiceTypeAssetLookup lookup = null!;
    ILoadOrderGetter<IModListingGetter<IModGetter>> loadOrder = null!;
    readonly Consumer consumer = new();

    [GlobalSetup]
    public void Setup()
    {
        var env = GameEnvironmentBuilder.Create(GameRelease.SkyrimSE)
            .Build();
        loadOrder = env.LoadOrder;
        lookup = new();
        lookup.Prep(env.LinkCache.CreateImmutableAssetLinkCache());
    }

    [Benchmark]
    public void LookupAllSpeakers()
    {
        var speakers = loadOrder.PriorityOrder.WinningOverrides<IDialogResponsesGetter>()
            .SelectMany(r => lookup.GetSpeakers(r));
        speakers.Consume(consumer);
    }

    [Benchmark]
    public void LookupAllPaths()
    {
        var paths = loadOrder.PriorityOrder.WinningOverrides<IDialogResponsesGetter>()
            .SelectMany(r => lookup.GetVoiceLineFilePaths(r));
        paths.Consume(consumer);
    }
}
