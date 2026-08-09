using AutoFixture;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Records.Assets.VoiceType;
using Mutagen.Bethesda.Testing;
using Mutagen.Bethesda.Testing.AutoData;
using Shouldly;
using Xunit;

namespace Mutagen.Bethesda.UnitTests.Plugins.Records.Skyrim.Assets;

public class VoiceTypeAssetLookupTestFixture
{
    private readonly IFixture _fixture;
    public readonly SkyrimMod Mod;
    public readonly ILinkCache LinkCache;

    public readonly Quest Quest;
    public readonly DialogTopic Topic;
    public readonly VoiceType Voice1;
    public readonly Npc Npc1;
    public readonly VoiceType Voice2;
    public readonly Npc Npc2;

    public VoiceTypeAssetLookupTestFixture(
        IFixture fixture,
        SkyrimMod mod,
        DialogTopic topic,
        Quest quest,
        Npc npc1,
        VoiceType voice1,
        string edid1,
        Npc npc2,
        VoiceType voice2,
        string edid2)
    {
        _fixture = fixture;
        Mod = mod;
        LinkCache = mod.ToMutableLinkCache();

        voice1.EditorID = edid1;
        npc1.Voice.SetTo(voice1);
        npc1.EditorID = nameof(Npc1); // For error message clarity
        Voice1 = voice1;
        Npc1 = npc1;
        voice2.EditorID = edid2;
        npc2.Voice.SetTo(voice2);
        npc2.EditorID = nameof(Npc2);
        Voice2 = voice2;
        Npc2 = npc2;

        topic.Quest.SetTo(quest);
        Topic = topic;
        Quest = quest;
    }

    public void AssertSpeakersEqual(IEnumerable<Condition> conditions, IEnumerable<INpcGetter> expectedSpeakers)
    {
        var rec = _fixture.Create<DialogResponses>();
        Topic.Responses.Add(rec);
        rec.Conditions.AddRange(conditions);

        var lookup = new VoiceTypeAssetLookup();
        lookup.Prep(LinkCache.CreateImmutableAssetLinkCache());

        // Check against resolved links so that errors include editor IDs
        lookup.GetSpeakers(rec).Select(s => s.Resolve(LinkCache)).ShouldBe(expectedSpeakers, ignoreOrder: true);
    }
}

public class VoiceTypeAssetLookupTestSkyrim
{
    #region Condition Factories
    public ConditionFloat CreateCondition(ConditionData data, float value, Condition.Flag flags)
    {
        return new ConditionFloat
        {
            Data = data,
            ComparisonValue = value,
            Flags = flags
        };
    }
    public ConditionFloat CreateIdCondition(IReferenceableObjectGetter target, float value, Condition.Flag flags)
    {
        var data = new GetIsIDConditionData();
        data.Object.Link.SetTo(target);
        return CreateCondition(data, value, flags);
    }

    public ConditionFloat CreateAliasRefCondition(uint id, float value, Condition.Flag flags)
    {
        return CreateCondition(new GetIsAliasRefConditionData() { ReferenceAliasIndex = (int)id }, value, flags);
    }

    public ConditionFloat CreateSexCondition(MaleFemaleGender gender, float value, Condition.Flag flags)
    {
        return CreateCondition(new GetIsSexConditionData() { MaleFemaleGender = gender }, value, flags);
    }

    public ConditionFloat CreateVoiceCondition(IFormLinkGetter<IVoiceTypeOrListGetter> voice, float value, Condition.Flag flags)
    {
        var data = new GetIsVoiceTypeConditionData();
        data.VoiceTypeOrList.Link.SetTo(voice);
        return CreateCondition(data, value, flags);
    }

    public ConditionFloat CreateListCondition(FormList list, float value, Condition.Flag flags)
    {
        var data = new IsInListConditionData();
        data.FormList.Link.SetTo(list);
        return CreateCondition(data, value, flags);
    }

    #endregion

    [Theory, MutagenModAutoData]
    public void TestGetIsId(VoiceTypeAssetLookupTestFixture fixture)
    {
        fixture.AssertSpeakersEqual(
            [CreateIdCondition(fixture.Npc1, 1, 0)],
            [fixture.Npc1]);
    }

    [Theory, MutagenModAutoData]
    public void TestGetIsIdAnd(VoiceTypeAssetLookupTestFixture fixture)
    {
        fixture.AssertSpeakersEqual(
            // GetIsId 1 && GetIsId 2
            [CreateIdCondition(fixture.Npc1, 1, 0), CreateIdCondition(fixture.Npc2, 1, 0)],
            []);
    }

    [Theory, MutagenModAutoData]
    public void TestGetIsIdOr(VoiceTypeAssetLookupTestFixture fixture)
    {
        fixture.AssertSpeakersEqual(
            // GetIsId 1 || GetIsId 2
            [CreateIdCondition(fixture.Npc1, 1, Condition.Flag.OR), CreateIdCondition(fixture.Npc2, 1, 0)],
            [fixture.Npc1, fixture.Npc2]);
    }

    [Theory, MutagenModAutoData]
    public void TestGetIsAliasUniqueActor(VoiceTypeAssetLookupTestFixture fixture, uint aliasId)
    {
        fixture.Quest.Aliases.Add(new() { ID = aliasId, UniqueActor = fixture.Npc1.ToNullableLink() });
        fixture.AssertSpeakersEqual(
            [CreateAliasRefCondition(aliasId, 1, 0)],
            [fixture.Npc1]);
    }

    [Theory, MutagenModAutoData]
    public void TestGetIsSex(VoiceTypeAssetLookupTestFixture fixture)
    {
        fixture.Npc1.Configuration.Flags &= ~NpcConfiguration.Flag.Female;
        fixture.Npc2.Configuration.Flags |= NpcConfiguration.Flag.Female;

        // == 1
        fixture.AssertSpeakersEqual([CreateSexCondition(MaleFemaleGender.Male, 1, 0)], [fixture.Npc1]);
        fixture.AssertSpeakersEqual([CreateSexCondition(MaleFemaleGender.Female, 1, 0)], [fixture.Npc2]);
        // == 0
        fixture.AssertSpeakersEqual([CreateSexCondition(MaleFemaleGender.Male, 0, 0)], [fixture.Npc2]);
        fixture.AssertSpeakersEqual([CreateSexCondition(MaleFemaleGender.Female, 0, 0)], [fixture.Npc1]);
    }

    [Theory, MutagenModAutoData]
    public void TestGetIsVoice(VoiceTypeAssetLookupTestFixture fixture)
    {
        fixture.AssertSpeakersEqual([CreateVoiceCondition(fixture.Npc1.Voice, 1, 0)], [fixture.Npc1]);
    }

    [Theory, MutagenModAutoData]
    public void TestGetIsVoiceList(VoiceTypeAssetLookupTestFixture fixture, FormList list)
    {
        list.Items.AddRange(fixture.Npc1.Voice, fixture.Npc2.Voice);
        fixture.AssertSpeakersEqual([CreateVoiceCondition(list.ToLink(), 1, 0)], [fixture.Npc1, fixture.Npc2]);

        // (Voice1 || Voice2) && !Voice2
        fixture.AssertSpeakersEqual([CreateVoiceCondition(list.ToLink(), 1, 0), CreateVoiceCondition(fixture.Npc2.Voice, 0, 0)], [fixture.Npc1]);
    }

    [Theory, MutagenModAutoData]
    public void TestIsInList(VoiceTypeAssetLookupTestFixture fixture, FormList list)
    {
        list.Items.AddRange(fixture.Npc1.ToLink(), fixture.Npc2.Voice);
        fixture.AssertSpeakersEqual([CreateListCondition(list, 1, 0)], [fixture.Npc1, fixture.Npc2]);

        // (Npc1 || Npc2) && !Npc2
        fixture.AssertSpeakersEqual([CreateListCondition(list, 1, 0), CreateIdCondition(fixture.Npc2, 0, 0)], [fixture.Npc1]);
    }

    // TODO
    //[Theory, MutagenModAutoData]
    //public void TestFilePath(VoiceTypeAssetLookupTestFixture fixture)
    //{

    //}

    private readonly ILinkCache _linkCache;
    private readonly VoiceTypeAssetLookup _searcher = new();

    public VoiceTypeAssetLookupTestSkyrim()
    {
        var mod = SkyrimMod.CreateFromBinaryOverlay(TestDataPathing.VoiceTypeTesting, SkyrimRelease.SkyrimSE);
        _linkCache = mod.ToImmutableLinkCache();
        
        _searcher.Prep(_linkCache.CreateImmutableAssetLinkCache());
    }

    [Theory]
    [MutagenModAutoData]
    public void TestAliasAdditionalVoicesNPCList(
        VoiceTypeAssetLookupTestFixture fixture,
        FormList formList,
        uint aliasId)
    {
        formList.Items.AddRange(fixture.Npc1.Voice, fixture.Npc2.Voice);
        fixture.Quest.Aliases.Add(new() { ID = aliasId, VoiceTypes = formList.ToNullableLink() });
        fixture.AssertSpeakersEqual(
            [CreateAliasRefCondition(aliasId, 1, 0)],
            [fixture.Npc1, fixture.Npc2]);
    }

    [Fact]
    public void TestGetIsID()
    {
        Assert.True(_linkCache.TryResolve<IDialogResponsesGetter>(FormKey.Factory("15549C:VoiceTypeTestPlugin.esm"), out var responses), "Response not resolved");
        Assert.True(_linkCache.TryResolve<IDialogTopicGetter>(FormKey.Factory("155352:VoiceTypeTestPlugin.esm"), out var topic), "Topic not resolved");

        Assert.Equal(
            new VoiceContainer(new HashSet<string>
            {
                "CYRaaaPLACEHOLDERVoicetype"
            }),
            _searcher.GetVoicesWithQuest(topic!, responses!)
        );
    }

    [Fact]
    public void TestGetInFaction()
    {
        Assert.True(_linkCache.TryResolve<IDialogResponsesGetter>(FormKey.Factory("0CE3BE:VoiceTypeTestPlugin.esm"), out var responses), "Response not resolved");
        Assert.True(_linkCache.TryResolve<IDialogTopicGetter>(FormKey.Factory("0CE39F:VoiceTypeTestPlugin.esm"), out var topic), "Topic not resolved");

        Assert.Equal(
            new VoiceContainer(new HashSet<string>
            {
                "CYRMaleEvenToned",
                "CYRMaleStandard",
                "CYRMaleHonorable",
                "CYRFemaleRich",
                "CYRFemaleSultry",
                "CYRFemaleEnergetic",
                "CYRaaaPLACEHOLDERVoicetype"
            }),
            _searcher.GetVoicesWithQuest(topic!, responses!)
        );
    }

    [Fact]
    public void TestGetInFactionQuestConditions()
    {
        Assert.True(_linkCache.TryResolve<IDialogResponsesGetter>(FormKey.Factory("0724F4:VoiceTypeTestPlugin.esm"), out var responses), "Response not resolved");
        Assert.True(_linkCache.TryResolve<IDialogTopicGetter>(FormKey.Factory("0724D7:VoiceTypeTestPlugin.esm"), out var topic), "Topic not resolved");

        Assert.Equal(
            new VoiceContainer(new HashSet<string>
            {
                "CYRMaleArgonian",
                "CYRMaleArgonianAccented",
                "CYRFemaleDeepToned",
                "CYRFemaleKhajiit",
                "CYRFemaleSoftToned",
                "CYRFemaleNord",
                "CYRMaleBrute",
                "CYRMaleEvenToned",
                "CYRMaleElfHaughty",
                "CYRMaleGuttural",
                "CYRMaleLightToned",
                "CYRMaleDunmer",
                "CYRMaleRoughshod",
                "CYRMaleStandard",
                "CYRMaleNord",
                "CYRFemaleArgonian",
                "CYRMaleNordThick",
                "CYRFemaleRich",
                "CYRMaleKhajiitMercurial",
                "CYRFemaleEnergetic",
                "CYRMaleEnglishRich",
                "CYRMaleOrcAlexC"
            }),
            _searcher.GetVoicesWithQuest(topic!, responses!)
        );
    }

    [Fact]
    public void TestAliasForcedRef()
    {
        Assert.True(_linkCache.TryResolve<IDialogResponsesGetter>(FormKey.Factory("0AF3C5:VoiceTypeTestPlugin.esm"), out var responses), "Response not resolved");
        Assert.True(_linkCache.TryResolve<IDialogTopicGetter>(FormKey.Factory("0AF395:VoiceTypeTestPlugin.esm"), out var topic), "Topic not resolved");

        Assert.Equal(
            new VoiceContainer(new HashSet<string>
            {
                "CYRMaleSemiUniqueTES4MaleImperialVoiceMatch"
            }),
            _searcher.GetVoicesWithQuest(topic!, responses!)
        );
    }

    [Fact]
    public void TestAliasExternal()
    {
        Assert.True(_linkCache.TryResolve<IDialogResponsesGetter>(FormKey.Factory("07EFF0:VoiceTypeTestPlugin.esm"), out var responses), "Response not resolved");
        Assert.True(_linkCache.TryResolve<IDialogTopicGetter>(FormKey.Factory("07EFDC:VoiceTypeTestPlugin.esm"), out var topic), "Topic not resolved");

        Assert.Equal(
            new VoiceContainer(new HashSet<string>
            {
                "CYRFemaleEnergetic",
                "CYRMaleHonorable",
                "CYRMaleStandard"
            }),
            _searcher.GetVoicesWithQuest(topic!, responses!)
        );
    }

    [Fact]
    public void TestEmptyLeveledChar()
    {
        Assert.True(_linkCache.TryResolve<IDialogResponsesGetter>(FormKey.Factory("16EAE5:VoiceTypeTestPlugin.esm"), out var responses), "Response not resolved");
        Assert.True(_linkCache.TryResolve<IDialogTopicGetter>(FormKey.Factory("16EAE2:VoiceTypeTestPlugin.esm"), out var topic), "Topic not resolved");

        Assert.Equal(
            new VoiceContainer(new HashSet<string>()),
            _searcher.GetVoicesWithQuest(topic!, responses!)
        );
    }

    [Fact]
    public void TestGetIsVoiceTypeQuestConditions()
    {
        Assert.True(_linkCache.TryResolve<IDialogResponsesGetter>(FormKey.Factory("063C34:VoiceTypeTestPlugin.esm"), out var responses), "Response not resolved");
        Assert.True(_linkCache.TryResolve<IDialogTopicGetter>(FormKey.Factory("063B3F:VoiceTypeTestPlugin.esm"), out var topic), "Topic not resolved");

        Assert.Equal(
            new VoiceContainer(new HashSet<string>
            {
                "CYRMaleArgonian",
                "CYRMaleArgonianAccented",
                "CYRFemaleDeepToned",
                "CYRFemaleKhajiit",
                "CYRFemaleSoftToned",
                "CYRFemaleNord",
                "CYRMaleBrute",
                "CYRMaleEvenToned",
                "CYRMaleElfHaughty",
                "CYRMaleGuttural",
                "CYRMaleLightToned",
                "CYRMaleDunmer",
                "CYRMaleRoughshod",
                "CYRMaleStandard",
                "CYRMaleNord",
                "CYRFemaleArgonian",
                "CYRMaleHonorable",
                "CYRMaleNordThick",
                "CYRFemaleRich",
                "CYRMaleKhajiitMercurial",
                "CYRFemaleSultry",
                "CYRFemaleEnergetic",
                "CYRMaleEnglishRich",
                "CYRMaleOrcAlexC"
            }),
            _searcher.GetVoicesWithQuest(topic!, responses!)
        );
    }
}
