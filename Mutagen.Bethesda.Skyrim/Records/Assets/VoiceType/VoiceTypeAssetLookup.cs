using Mutagen.Bethesda.Assets;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
namespace Mutagen.Bethesda.Skyrim.Records.Assets.VoiceType;

/// <summary>
/// Lookup for potential speakers and voice types of dialogue. Attempts to determine potential speakers at runtime to the extent that it can be statically determined.
/// Considers winning overrides only.
/// 
/// Intentionally differs from CK behaviour in that it ignores the `AllowDefaultDialog` flag of voice types, which has no effect at runtime but removes a response with
/// no explicit inclusion condition from voice export consideration in the CK.
/// 
/// Behaviour is undefined if a filter condition is compared against something other than a boolean, or using operators other than == or !=
/// </summary>
public class VoiceTypeAssetLookup : IAssetCacheComponent
{
    private ILinkCache _formLinkCache = null!;

    //Databases
    private HashSet<FormKey> _allVoiceTypes = null!;
    // TODO: This is probably unnecessary. Leave optimisation for its own PR.
    // Kept as enumerable as most unique NPCs have only one voice
    private readonly Dictionary<FormKey, IEnumerable<FormKey>> _speakerVoices = new();
    private readonly Dictionary<FormKey, HashSet<FormKey>> _factionNPCs = new();
    private readonly Dictionary<FormKey, HashSet<FormKey>> _classNPCs = new();
    private readonly Dictionary<FormKey, HashSet<FormKey>> _raceNPCs = new();
    private readonly Dictionary<FormKey, HashSet<FormKey>> _keywordNPCs = new();
    private readonly Dictionary<MaleFemaleGender, HashSet<FormKey>> _genderNPCs = new();
    private HashSet<FormKey> _childNPCs = null!;
    private readonly Dictionary<FormKey, int> _dialogueSceneAliasIndex = new();
    private readonly Dictionary<FormKey, HashSet<FormKey>> _sharedInfoUsages = new();

    //Caches
    private readonly object _questCacheLock = new();
    private readonly Dictionary<FormKey, VoiceContainer> _questCache = new();

    // Reverse lookup for GetIsVoiceType conditions
    // TODO: Would this be faster with links? Would mean distinct comparison also needs to use links
    private readonly Dictionary<FormKey, List<FormKey>> _voiceSpeakers = [];

    public void Prep(IAssetLinkCache linkCache)
    {
        _formLinkCache = linkCache.FormLinkCache;

        // TODO: Much of this could be lazy
        foreach (var quest in _formLinkCache.WinningOverrides<IQuestGetter>())
        {
            foreach (var alias in quest.Aliases)
            {
                var uniqueActor = alias.UniqueActor.FormKey;
                if (uniqueActor.IsNull) continue;

                foreach (var faction in alias.Factions)
                {
                    if (!faction.IsNull)
                    {
                        _factionNPCs
                            .GetOrAdd(faction.FormKey)
                            .Add(uniqueActor);
                    }
                }
                if (alias.Keywords != null)
                {
                    foreach (var keyword in alias.Keywords)
                    {
                        _keywordNPCs.GetOrAdd(keyword.FormKey).Add(uniqueActor);
                    }
                }
            }
        }

        foreach (var npc in _formLinkCache.WinningOverrides<INpcGetter>())
        {
            _speakerVoices.Add(npc.FormKey, GetVoiceTypes(npc));

            foreach (var factionKey in GetFactions(npc))
            {
                _factionNPCs
                    .GetOrAdd(factionKey.FormKey)
                    .Add(npc.FormKey);
            }

            foreach (var classKey in GetClasses(npc))
            {
                _classNPCs
                    .GetOrAdd(classKey.FormKey)
                    .Add(npc.FormKey);
            }

            foreach (var gender in GetGenders(npc))
            {
                _genderNPCs
                    .GetOrAdd(gender)
                    .Add(npc.FormKey);
            }

            foreach (var raceKey in GetRaces(npc))
            {
                _raceNPCs
                    .GetOrAdd(raceKey.FormKey)
                    .Add(npc.FormKey);
            }

            foreach (var keyword in GetKeywords(npc))
            {
                _keywordNPCs.GetOrAdd(keyword.FormKey).Add(npc.FormKey);
            }
        }

        // TODO: Use usage cache for this
        foreach (var response in _formLinkCache.WinningOverrides<IDialogResponsesGetter>())
        {
            if (!response.ResponseData.IsNull)
            {
                _sharedInfoUsages
                    .GetOrAdd(response.ResponseData.FormKey)
                    .Add(response.FormKey);
            }
        }

        foreach (var talkingActivator in _formLinkCache.WinningOverrides<ITalkingActivatorGetter>())
        {
            if (!talkingActivator.Voice.IsNull)
                _speakerVoices.Add(talkingActivator.FormKey, [talkingActivator.Voice.FormKey]);
        }

        // TODO: Use usage cache for this
        foreach (var scene in _formLinkCache.WinningOverrides<ISceneGetter>())
        {
            foreach (var action in scene.Actions)
            {
                if (action.Type == SceneAction.TypeEnum.Dialog && !action.Topic.IsNull && action.ActorID != null && !_dialogueSceneAliasIndex.ContainsKey(action.Topic.FormKey))
                {
                    _dialogueSceneAliasIndex.Add(action.Topic.FormKey, action.ActorID.Value);
                }
            }
        }

        // Build caches
        _childNPCs = _raceNPCs.Where(raceNpcs => _formLinkCache.TryResolve<IRaceGetter>(raceNpcs.Key, out var race) && race.Flags.HasFlag(Race.Flag.Child))
            .SelectMany(raceNpcs => raceNpcs.Value)
            .ToHashSet();

        _allVoiceTypes = _formLinkCache.WinningOverrides<IVoiceTypeGetter>()
            .Select(v => v.FormKey)
            .ToHashSet();

        foreach (var (speaker, voices) in _speakerVoices)
        {
            foreach (var voice in voices)
            {
                _voiceSpeakers.GetOrAdd(voice).Add(speaker);
            }
        }
    }

    /// <summary>
    /// Used for testing mainly
    /// </summary>
    /// <param name="topic"></param>
    /// <param name="response"></param>
    /// <returns></returns>
    public VoiceContainer? GetVoicesWithQuest(IDialogTopicGetter topic, IDialogResponsesGetter response)
    {
        var quest = topic.Quest.TryResolve(_formLinkCache);
        if (quest == null) return null;

        //When the quest doesn't allow export return no voices  
        if ((quest.Flags & Quest.Flag.ExcludeFromDialogExport) != 0) return new VoiceContainer();

        //When all responses use a sound override, return no voices
        if (response.Responses.All(r => !r.Sound.IsNull)) return new VoiceContainer();

        //If this is a shared info and it's not used, return no voices
        if (topic.Subtype == DialogTopic.SubtypeEnum.SharedInfo && !_sharedInfoUsages.ContainsKey(response.FormKey)) return new VoiceContainer();

        //Get quest voices
        var questVoices = GetQuestVoices(topic, quest);

        //If we have selected default voices, make sure the quest voices are being checked first - they might not be part of default voices
        var voices = GetVoices(topic, response, quest);
        voices.IntersectWith(questVoices);

        LimitVoicesToSharedInfoUsages(voices, topic, response);

        return voices;
    }

    public IEnumerable<DataRelativePath> GetVoiceLineFilePaths(IDialogTopicGetter topic)
    {
        var quest = topic.Quest.TryResolve(_formLinkCache);
        if (quest == null) yield break;

        //Get quest voices
        var questVoices = GetQuestVoices(topic, quest);

        var (questString, topicString) = GetQuestAndTopicStrings(topic, quest);
        foreach (var responses in topic.Responses)
        {
            foreach (var path in GetVoiceLineFilePaths(topic, responses, quest, questVoices, questString, topicString))
            {
                yield return path;
            }
        }
    }

    public IEnumerable<DataRelativePath> GetVoiceLineFilePaths(IDialogResponsesGetter responses)
    {
        var responsesContext = _formLinkCache.ResolveSimpleContext<IDialogResponsesGetter>(responses.FormKey);
        if (!responsesContext.TryGetParent<IDialogTopicGetter>(out var topic)) yield break;

        var quest = topic.Quest.TryResolve(_formLinkCache);
        if (quest == null) yield break;

        //Get quest voices
        var questVoices = GetQuestVoices(topic, quest);

        var (questString, topicString) = GetQuestAndTopicStrings(topic, quest);
        foreach (var path in GetVoiceLineFilePaths(topic, responses, quest, questVoices, questString, topicString))
        {
            yield return path;
        }
    }

    private VoiceContainer GetVoiceContainer(IDialogResponsesGetter responses)
    {
        if (!_formLinkCache.TryResolveSimpleContext(responses, out var context))
            return new();
        if (!context.TryGetParent<IDialogTopicGetter>(out var topic))
            return new();
        if (!topic.Quest.TryResolve(_formLinkCache, out var quest))
            return new();

        //Get quest voices
        var questVoices = GetQuestVoices(topic, quest);

        //If we have selected default voices, make sure the quest voices are being checked first - they might not be part of default voices
        var voiceContainer = GetVoices(topic, responses, quest);
        voiceContainer.IntersectWith(questVoices);

        if (voiceContainer.IsDefault)
        {
            voiceContainer = new(_allVoiceTypes);
        }
        return voiceContainer;
    }

    /// <summary>
    /// Get all voice types for a given dialog that can speak it based on the conditions of the dialog.
    /// </summary>
    /// <param name="responses">Dialog responses to get speakers for</param>
    /// <returns>List of voice types</returns>
    public IEnumerable<IFormLinkGetter<IVoiceTypeGetter>> GetVoiceTypes(IDialogResponsesGetter responses)
    {
        return GetVoiceContainer(responses).Voices.Keys
            .Select(v => v.ToLink<IVoiceTypeGetter>());
    }

    /// <summary>
    /// Get all NPCs for a given dialog that can speak it based on the conditions of the dialog.
    /// </summary>
    /// <param name="responses">Dialog responses to get speakers for</param>
    /// <returns>List of NPC speakers, including npcs or talking activators</returns>
    public IEnumerable<IFormLinkGetter<IHasVoiceTypeGetter>> GetSpeakers(IDialogResponsesGetter responses)
    {
        return GetVoiceContainer(responses).Voices.SelectMany(x =>
        {
            // A subset of speakers is used
            if (x.Value.Count > 0) return x.Value as IEnumerable<FormKey>;

            // The whole voice type is used
            return _voiceSpeakers.GetOrDefault(x.Key) ?? [];
        }).Distinct().Select(speaker => speaker.ToLink<IHasVoiceTypeGetter>());
    }

    private IEnumerable<DataRelativePath> GetVoiceLineFilePaths(
        IDialogTopicGetter topic,
        IDialogResponsesGetter responses,
        IQuestGetter quest,
        VoiceContainer questVoices,
        string questString,
        string topicString)
    {
        var voices = GetDialogVoiceContainer(topic, responses, quest, questVoices);

        var responseFormID = responses.FormKey.ID.ToString("X8");

        foreach (var response in responses.Responses)
        {
            // Skip responses with sound override
            if (!response.Sound.IsNull) continue;

            var responseNumber = response.ResponseNumber;
            foreach (var voiceType in voices.GetVoiceTypes(_allVoiceTypes))
            {
                if (!_formLinkCache.TryResolve<IVoiceTypeGetter>(voiceType, out var voice) || voice.EditorID == null) continue;
                yield return Path.Combine
                (
                    "Sound",
                    "Voice",
                    responses.FormKey.ModKey.FileName,
                    voice.EditorID,
                    $"{questString}_{topicString}_{responseFormID}_{responseNumber}.fuz"
                );
            }
        }
    }

    private VoiceContainer GetDialogVoiceContainer(
        IDialogTopicGetter topic,
        IDialogResponsesGetter responses,
        IQuestGetter quest,
        VoiceContainer questVoices)
    {
        //Don't process responses with response data
        if (!responses.ResponseData.IsNull)
        {
            return new VoiceContainer();
        }

        //If we have selected default voices, make sure the quest voices are being checked first - they might not be part of default voices
        var voices = GetVoices(topic, responses, quest);
        voices.IntersectWith(questVoices);

        LimitVoicesToSharedInfoUsages(voices, topic, responses);

        return voices;
    }

    private void LimitVoicesToSharedInfoUsages(VoiceContainer voices, IDialogTopicGetter topic, IDialogResponsesGetter responses) {
        if (topic.Subtype == DialogTopic.SubtypeEnum.SharedInfo && _sharedInfoUsages.TryGetValue(responses.FormKey, out var responseFormKeys))
        {
            var userConditions = responseFormKeys
                .Select(responseKey =>
                {
                    var responseContext = _formLinkCache.ResolveSimpleContext<IDialogResponsesGetter>(responseKey);
                    if (responseContext is not { Parent.Record: not null }) return null;

                    if (!responseContext.TryGetParent<IDialogTopicGetter>(out var currentTopic)) return null;
                    var currentQuest = currentTopic.Quest.TryResolve(_formLinkCache);
                    if (currentQuest == null) return null;

                    return GetVoices(responseContext.Record.Conditions, currentQuest);
                })
                .WhereNotNull()
                .MergeInsert(true);

            voices.IntersectWith(userConditions);
        }
    }

    private VoiceContainer GetQuestVoices(IDialogTopicGetter topic, IQuestGetter quest)
    {
        lock (_questCacheLock)
        {
            if (!_questCache.TryGetValue(quest.FormKey, out var questVoices))
            {
                questVoices = GetVoices(quest);
                _questCache.TryAdd(quest.FormKey, questVoices);
            }

            return questVoices;
        }
    }

    private static (string questString, string topicString) GetQuestAndTopicStrings(IDialogTopicGetter topic, IQuestGetter quest)
    {
        //Voice line variables
        var questID = quest.EditorID ?? "";
        var topicID = topic.EditorID ?? "";

        //Evaluate string length
        var questLength = questID.Length;
        var topicLength = topicID.Length;
        if (questLength + topicLength > 25)
        {
            if (questLength > 10)
            {
                questLength = 10;
                topicLength = 15;
            }
            else
            {
                topicLength = 25 - questLength;
            }
        }

        var questString = questID[..questLength];
        var topicString = topicID.Length > topicLength ? topicID[..topicLength] : topicID;
        return (questString, topicString);
    }

    private VoiceContainer GetVoices(IDialogTopicGetter topic, IDialogResponsesGetter response, IQuestGetter quest)
    {
        var voices = new VoiceContainer(true);

        //Use speaker only if we have one
        if (!response.Speaker.IsNull) return GetVoices(response.Speaker.FormKey);

        //Check scene
        if (topic.Category == DialogTopic.CategoryEnum.Scene && _dialogueSceneAliasIndex.TryGetValue(topic.FormKey, out var aliasIndex))
        {
            voices.IntersectWith(GetVoices(quest, aliasIndex));
        }

        //Search conditions
        if (response.Conditions.Any())
        {
            voices.IntersectWith(GetVoices(response.Conditions, quest));
        }

        return voices;
    }

    private VoiceContainer GetVoices(IEnumerable<IConditionGetter> conditions, IQuestGetter quest)
    {
        var voiceTypesOrBlock = new List<VoiceContainer>();
        var currentConditions = new List<IConditionGetter>();

        //Calculate OR blocks
        var conditionsList = conditions.ToList();
        for (var i = 0; i < conditionsList.Count; i++)
        {
            var condition = conditionsList[i];
            currentConditions.Add(condition);

            //At every new AND or at the end of the conditions, finish the current block
            if ((condition.Flags & Condition.Flag.OR) == 0 || i == conditionsList.Count - 1)
            {
                var voices = GetVoiceTypesOrBlock(currentConditions, quest);
                if (!voices.IsDefault) voiceTypesOrBlock.Add(voices);

                currentConditions.Clear();
            }
        }

        //Merge OR blocks
        return voiceTypesOrBlock.Any() ? voiceTypesOrBlock.MergeIntersect() : new VoiceContainer(true);
    }

    private VoiceContainer GetVoiceTypesOrBlock(IEnumerable<IConditionGetter> conditions, IQuestGetter quest)
    {
        return conditions
            .Select(condition =>
            {
                var conditionVoices = GetVoices(condition, quest);
                if (conditionVoices == null || conditionVoices.IsDefault) return null;

                return conditionVoices;
            })
            .WhereNotNull()
            .MergeInsert(true);
    }

    private VoiceContainer? GetConditionDataVoices(IConditionDataGetter data, IQuestGetter quest)
    {
        switch (data)
        {
            case IGetIsIDConditionDataGetter getIsId:
                if (getIsId.Object.UsesLink())
                {
                    var getIsIdFormKey = getIsId.Object.Link.FormKey;
                    if (_speakerVoices.TryGetValue(getIsIdFormKey, out var idVoices))
                    {
                        return new VoiceContainer(getIsIdFormKey, idVoices);
                    }
                }

                break;
            case IGetIsVoiceTypeConditionDataGetter isVoiceType:
                if (isVoiceType.VoiceTypeOrList.Link.TryResolve(_formLinkCache, out var voiceTypeRecord))
                {
                    switch (voiceTypeRecord)
                    {
                        case IVoiceTypeGetter voiceType:
                            return new VoiceContainer(voiceType.FormKey);
                        case IFormListGetter formList:
                            return new VoiceContainer(formList.Items
                                .Where(link => _formLinkCache.TryResolveIdentifier(link, out var _))
                                .Select(voice => voice.FormKey));
                    }
                }

                break;
            case IGetIsAliasRefConditionDataGetter aliasRef:
                return GetVoices(quest, aliasRef.ReferenceAliasIndex);
            case IGetInFactionConditionDataGetter getInFaction:
                if (getInFaction.Faction.UsesLink() && _factionNPCs.TryGetValue(getInFaction.Faction.Link.FormKey, out var factionNpcFormKeys))
                {
                    // TODO: Would passing lookup directly (probably via an interface) be faster?
                    return new VoiceContainer(factionNpcFormKeys, GetVoiceTypes);
                }

                break;
            case IGetFactionRankConditionDataGetter getFactionRank:
                // Assume the actor can be in any rank as long they are in the faction - they might shift ranks later on
                if (getFactionRank.Faction.UsesLink() && _factionNPCs.TryGetValue(getFactionRank.Faction.Link.FormKey, out var factionNpcFormKeys2))
                {
                    return new VoiceContainer(factionNpcFormKeys2, GetVoiceTypes);
                }

                break;
            case IGetIsClassConditionDataGetter getIsClass:
                if (getIsClass.Class.UsesLink() && _classNPCs.TryGetValue(getIsClass.Class.Link.FormKey, out var classNpcFormKeys))
                {
                    return new VoiceContainer(classNpcFormKeys, GetVoiceTypes);
                }

                break;
            case IHasKeywordConditionDataGetter hasKeyword:
                if (_keywordNPCs.TryGetValue(hasKeyword.Keyword.Link.FormKey, out var keywordNpcs))
                {
                    return new VoiceContainer(keywordNpcs, GetVoiceTypes);
                }
                break;
            case IGetIsRaceConditionDataGetter getIsRace:
                if (getIsRace.Race.UsesLink() && _raceNPCs.TryGetValue(getIsRace.Race.Link.FormKey, out var raceNpcFormKeys))
                {
                    return new VoiceContainer(raceNpcFormKeys, GetVoiceTypes);
                }

                break;
            case IGetIsSexConditionDataGetter sexConditionDataGetter:
                if (_genderNPCs.TryGetValue(sexConditionDataGetter.MaleFemaleGender, out var genderNpcFormKeys))
                {
                    // TODO: Maybe pre build container here
                    return new VoiceContainer(genderNpcFormKeys, GetVoiceTypes);
                }

                break;
            case IIsInListConditionDataGetter isInList:
                if (isInList.FormList.UsesLink())
                {
                    var formList = isInList.FormList.Link.TryResolve(_formLinkCache);
                    //Only look at speakers in the form list
                    // TODO: Don't use an intermediate voice container
                    if (formList != null) return formList.Items.Select(link => GetVoices(link.FormKey)).MergeInsert(false);
                }

                break;
            case IIsChildConditionDataGetter isChild:
                // TODO: Maybe pre build container here
                return new VoiceContainer(_childNPCs, GetVoiceTypes);
            default:
                // Condition not intended to filter
                return null;
        }
        // Condition not configured correctly or no one meets condition
        return new VoiceContainer();
    }

    // Returns speakers allowed by this condition, or null if its is invalid/doesn't filter by speaker
    private VoiceContainer? GetVoices(IConditionGetter condition, IQuestGetter quest)
    {
        var data = condition.Data;

        if (data.RunOnType != Condition.RunOnType.Subject) return null;
        var voices = GetConditionDataVoices(data, quest);

        // Not a filtering condition
        if (voices == null || voices.IsDefault)
            return null;

        if (IsConditionInverted(condition))
        {
            //Can't invert alias according to CK calculation
            // Aliases may change at runtime
            if (data.Function == Condition.Function.GetIsAliasRef)
            {
                return null;
            }

            voices = Invert(voices);
        }

        return voices;
    }

    private bool IsConditionInverted(IConditionGetter condition)
    {
        var compareValue = condition switch
        {
            IConditionFloatGetter conditionFloat => conditionFloat.ComparisonValue,
            IConditionGlobalGetter conditionGlobal => conditionGlobal.ComparisonValue.TryResolve(_formLinkCache) switch
            {
                IGlobalFloat globalFloat => globalFloat.Data,
                IGlobalInt globalInt => globalInt.Data,
                IGlobalShortGetter globalShort => globalShort.Data,
                _ => 0,
            },
            _ => 0
        } ?? 0;

        switch (condition.CompareOperator)
        {
            case CompareOperator.EqualTo:
                return compareValue == 0;
            case CompareOperator.NotEqualTo:
                return compareValue == 1;
            case CompareOperator.GreaterThan:
            case CompareOperator.GreaterThanOrEqualTo:
            case CompareOperator.LessThan:
            case CompareOperator.LessThanOrEqualTo:
            default:
                return false;
        }
    }

    private VoiceContainer GetVoices(IQuestGetter quest, int aliasIndex)
    {
        var alias = quest.Aliases.FirstOrDefault(a => a.ID == aliasIndex);
        return alias == null ? new VoiceContainer(true) : GetVoices(alias, quest);
    }

    private VoiceContainer GetVoices(IQuestAliasGetter alias, IQuestGetter quest)
    {
        //External Alias
        if (alias.External != null)
        {
            var externalQuest = alias.External.Quest.TryResolve(_formLinkCache);
            var aliasIndex = alias.External.AliasID;
            if (externalQuest != null && aliasIndex != null)
            {
                return GetVoices(externalQuest, aliasIndex.Value);
            }
        }

        //Additional voice types
        VoiceContainer? additionalVoices = null;
        if (!alias.VoiceTypes.IsNull)
        {
            var additionalVoiceTypes = alias.VoiceTypes.TryResolve(_formLinkCache);
            if (additionalVoiceTypes != null)
            {
                additionalVoices = additionalVoiceTypes switch
                {
                    INpcGetter npc => GetVoices(npc),
                    IFormListGetter formList => GetVoices(formList),
                    _ => new VoiceContainer(true)
                };
            }
        }

        //Forced Ref
        if (!alias.ForcedReference.IsNull)
        {
            var placedNPC = alias.ForcedReference.TryResolve<IPlacedNpcGetter>(_formLinkCache);
            if (placedNPC != null)
            {
                var voices = GetVoices(placedNPC.Base.FormKey);
                if (additionalVoices != null) voices.Insert(additionalVoices);
                return voices;
            }

            var placedObject = alias.ForcedReference.TryResolve<IPlacedObjectGetter>(_formLinkCache);
            if (placedObject != null)
            {
                var voices = GetVoices(placedObject.Base.FormKey);
                if (additionalVoices != null) voices.Insert(additionalVoices);
                return voices;
            }
        }

        //Created object
        if (alias.CreateReferenceToObject != null)
        {
            var voices = GetVoices(alias.CreateReferenceToObject.Object.FormKey);
            if (additionalVoices != null) voices.Insert(additionalVoices);
            return voices;
        }

        //Conditions
        if (alias.Conditions.Any())
        {
            var voices = GetVoices(alias.Conditions, quest);
            if (additionalVoices != null) voices.Insert(additionalVoices);
            return voices;
        }

        //Additional voices overwrites location alias and unique npc
        if (additionalVoices != null)
        {
            return additionalVoices;
        }

        //Location alias. Currently only SpecificLocation is supported
        if (alias.Location is { AliasID: {} })
        {
            var locationAlias = quest.Aliases.FirstOrDefault(a => a.ID == alias.Location.AliasID.Value);
            if (locationAlias != null)
            {
                if (!locationAlias.SpecificLocation.IsNull)
                {
                    var location = locationAlias.SpecificLocation.TryResolve(_formLinkCache);
                    if (location is not null)
                    {
                        foreach (var locRef in location.LocationRefTypesReferences().Where(r => r.LocationRefType.FormKey == alias.Location.RefType.FormKey))
                        {
                            var linkedRef = locRef.Ref.TryResolve(_formLinkCache);
                            if (linkedRef == null) continue;

                            return linkedRef switch
                            {
                                IPlacedNpcGetter placedNpc => GetVoices(placedNpc.Base.FormKey),
                                IPlacedObjectGetter placedObject => GetVoices(placedObject.Base.FormKey),
                                _ => new VoiceContainer(true)
                            };
                        }
                    }
                }
            }
        }

        //Unique NPC
        if (!alias.UniqueActor.IsNull) return new VoiceContainer(alias.UniqueActor.FormKey, GetVoiceTypes(alias.UniqueActor.FormKey));

        //Find matching from event => default voices
        if (alias.FindMatchingRefFromEvent != null || alias.FindMatchingRefNearAlias != null)
        {
            return new VoiceContainer(true);
        }

        //Nothing is valid => no voices for this alias
        return new VoiceContainer();
    }

    private VoiceContainer GetVoices(FormKey speaker) => new(speaker, GetVoiceTypes(speaker));
    private VoiceContainer GetVoices(INpcGetter npc) => new(npc.FormKey, GetVoiceTypes(npc.FormKey));

    private VoiceContainer GetVoices(IFormListGetter formList)
    {
        var voices = new List<VoiceContainer>();

        foreach (var item in formList.Items)
        {
            if (_formLinkCache.TryResolveIdentifier<IVoiceTypeGetter>(item.FormKey, out var _))
            {
                //FormList entry is VoiceType
                voices.Add(new VoiceContainer(item.FormKey));
            }
            else if (_speakerVoices.ContainsKey(item.FormKey))
            {
                //FormList entry is Npc
                voices.Add(GetVoices(item.FormKey));
            }
        }

        return voices.MergeInsert(false);
    }

    private VoiceContainer GetVoices(IQuestGetter quest) => GetVoices(quest.DialogConditions, quest);

    private VoiceContainer Invert(VoiceContainer voiceContainer)
    {
        VoiceContainer baseVoices = new(_voiceSpeakers);
        baseVoices.Remove(voiceContainer);
        return baseVoices;
    }

    private IEnumerable<FormKey> GetVoiceTypes(FormKey speaker)
    {
        return _speakerVoices.GetOrDefault(speaker) ?? [];
    }

    private IEnumerable<T> GetInheritedData<T>(INpcSpawnGetter spawn, NpcConfiguration.TemplateFlag inheritFlag, Func<INpcGetter, IEnumerable<T>> getter)
    {
        switch (spawn)
        {
            case INpcGetter npc:
                if (npc.Configuration.TemplateFlags.HasFlag(inheritFlag) && npc.Template.TryResolve(_formLinkCache, out var template))
                    return GetInheritedData(template, inheritFlag, getter);
                else
                    return getter(npc);
            case ILeveledNpcGetter leveledNpc:
                if (leveledNpc.Entries == null) return [];

                return leveledNpc.Entries
                    .Select(e => e.Data?.Reference?.TryResolve(_formLinkCache))
                    .WhereNotNull()
                    .SelectMany(e => GetInheritedData(e, inheritFlag, getter));
            default: return [];
        }
    }

    private FormKey GetDefaultVoice(IFormLinkGetter<IRaceGetter> raceLink, bool female)
    {
        if (!raceLink.TryResolve(_formLinkCache, out var race))
            return FormKey.Null;
        var link = female ? race.Voices.Female : race.Voices.Male;
        return link.FormKey;
    }

    private HashSet<FormKey> GetVoiceTypes(INpcGetter npc)
    {
        return GetInheritedData<FormKey>(npc, NpcConfiguration.TemplateFlag.Traits, entry => {
            if (!entry.Voice.IsNull)
                return [entry.Voice.FormKey];
            else
            {
                var defaultVoice = GetDefaultVoice(npc.Race, npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female));
                return defaultVoice.IsNull ? [] : [defaultVoice];
            }
            // TODO: Could this avoid hash set for single-voice NPCs?
        }).ToHashSet();
    }

    private HashSet<IFormLinkGetter<IFactionGetter>> GetFactions(INpcSpawnGetter npc)
    {
        return GetInheritedData(npc, NpcConfiguration.TemplateFlag.Factions, entry => entry.Factions.Select(f => f.Faction)).ToHashSet();
    }

    private HashSet<IFormLinkGetter<IClassGetter>> GetClasses(INpcSpawnGetter npc)
    {
        return GetInheritedData<IFormLinkGetter<IClassGetter>>(npc, NpcConfiguration.TemplateFlag.Stats, entry => [entry.Class])
            .Where(c => !c.IsNull)
            .ToHashSet();
    }

    private HashSet<MaleFemaleGender> GetGenders(INpcSpawnGetter npc)
    {
        return GetInheritedData<MaleFemaleGender>(npc, NpcConfiguration.TemplateFlag.Traits, entry => [entry.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? MaleFemaleGender.Female : MaleFemaleGender.Male])
            // TODO: HashSet is overkill
            .ToHashSet();

    }

    private HashSet<IFormLinkGetter<IRaceGetter>> GetRaces(INpcSpawnGetter npc)
    {
        return GetInheritedData<IFormLinkGetter<IRaceGetter>>(npc, NpcConfiguration.TemplateFlag.Traits, entry => [entry.Race])
            .Where(r => !r.IsNull)
            .ToHashSet();
    }

    private HashSet<IFormLinkGetter<IKeywordGetter>> GetKeywords(INpcSpawnGetter npc)
    {
        var direct = GetInheritedData(npc, NpcConfiguration.TemplateFlag.Keywords, entry => entry.Keywords ?? []);
        var race = GetInheritedData(npc, NpcConfiguration.TemplateFlag.Traits, entry => entry.Race.TryResolve(_formLinkCache)?.Keywords ?? []);
        return direct.And(race).ToHashSet();
    }
}