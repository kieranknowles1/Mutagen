namespace Mutagen.Bethesda.Skyrim.Records.Assets.VoiceType;

public static class VoiceContainerExtension
{
    public static VoiceContainer MergeInsert(this IEnumerable<VoiceContainer> voiceContainers, bool isDefaultIfEmpty)
    {
        // Don't use voiceContainers.Any as it would evaluate the enumerable twice
        var hasAny = false;
        // TODO: Should we have an edge case for N == 1? Bench against full dataset to get a realistic idea
        var output = new VoiceContainer();
        foreach (var voiceContainer in voiceContainers)
        {
            output.Insert(voiceContainer);
            hasAny = true;
        }
        if (!hasAny) return new(isDefaultIfEmpty);
        return output;
    }

    public static VoiceContainer MergeIntersect(this IEnumerable<VoiceContainer> voiceContainers)
    {
        var voiceContainerList = voiceContainers.ToList();

        switch (voiceContainerList) {
            case []: return new VoiceContainer();
            case [var voiceContainer]: return voiceContainer;
            default:
                var output = new VoiceContainer(true);
                foreach (var voiceContainer in voiceContainerList)
                {
                    output.IntersectWith(voiceContainer);
                }

                return output;
        }
    }
}
