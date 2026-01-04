using Newtonsoft.Json;
using RatScanner.TarkovDev.GraphQL;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using TTask = RatScanner.TarkovDev.GraphQL.Task;

namespace RatScanner;

public static class TarkovDevAPI {
	private class ResponseData<T> {
		[JsonProperty("data")]
		public ResponseDataInner<T>? Data { get; set; }
	}

	private class ResponseDataInner<T> {
		[JsonProperty("data")]
		public T? Data { get; set; }
	}

	const string ApiEndpoint = "https://api.tarkov.dev/graphql";

	private static readonly ConcurrentDictionary<string, (long expire, object response)> Cache = new();
	private static readonly ConcurrentDictionary<string, bool> PendingRequests = new();

	private static readonly HttpClient HttpClient = CreateHttpClient();

	private static HttpClient CreateHttpClient() {
		HttpClient client = new(new HttpClientHandler {
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
		});
		// Some upstreams reject requests without a user-agent.
		try {
			client.DefaultRequestHeaders.UserAgent.ParseAdd($"RatScanner/{RatConfig.Version}");
		} catch {
			client.DefaultRequestHeaders.UserAgent.ParseAdd("RatScanner");
		}
		return client;
	}

	private static readonly JsonSerializerSettings JsonSettings = new() {
		MissingMemberHandling = MissingMemberHandling.Ignore,
		NullValueHandling = NullValueHandling.Ignore,
		TypeNameHandling = TypeNameHandling.Auto,
		TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
	};

	private static async Task<Stream> Get(string query) {
		Dictionary<string, string> body = new() { { "query", query } };
		HttpResponseMessage responseTask = await HttpClient.PostAsJsonAsync(ApiEndpoint, body);

		if (responseTask.StatusCode != HttpStatusCode.OK) throw new Exception($"Tarkov.dev API request failed. {responseTask.ReasonPhrase}");
		return await responseTask.Content.ReadAsStreamAsync();
	}

	/// <summary>
	/// Tries to load data from offline cache
	/// </summary>
	/// <returns>True if cache was loaded successfully</returns>
	private static bool TryLoadFromOfflineCache<T>(string baseQueryKey, long ttl) where T : class {
		if (Cache.ContainsKey(baseQueryKey)) return true;

		if (RatConfig.ReadFromCache(baseQueryKey, out string cachedResponse)) {
			try {
				ResponseData<T[]>? neededResponse = JsonConvert.DeserializeObject<ResponseData<T[]>?>(cachedResponse, JsonSettings);
				if (neededResponse?.Data?.Data != null) {
					long time = DateTimeOffset.Now.ToUnixTimeSeconds();
					// Use expired TTL so background refresh will be triggered
					Cache[baseQueryKey] = (time - 1, neededResponse.Data.Data);
					Logger.LogInfo($"Loaded {neededResponse.Data.Data.Length} items from offline cache for: \"{baseQueryKey}\"");
					return true;
				}
			} catch (Exception e) {
				Logger.LogWarning($"Failed to load offline cache for: \"{baseQueryKey}\"", e);
			}
		}
		return false;
	}

	/// <summary>
	/// Fetches all data in a single request
	/// </summary>
	private static async Task QueueRequest<T>(string baseQueryKey, Func<string> queryBuilder, long ttl) where T : class {
		// Check if request is already pending
		if (!PendingRequests.TryAdd(baseQueryKey, true)) {
			Logger.LogInfo($"Request already pending for: \"{baseQueryKey}\", skipping");
			return;
		}

		try {
			Stopwatch sw = Stopwatch.StartNew();
			string query = queryBuilder();
			Logger.LogInfo($"Fetching data for: \"{baseQueryKey}\"");

			using Stream stream = await Get(query);
			using StreamReader streamReader = new(stream);

			// Read raw response for caching
			string rawResponse = await streamReader.ReadToEndAsync();

			// Parse the response
			ResponseData<T[]>? neededResponse = JsonConvert.DeserializeObject<ResponseData<T[]>>(rawResponse, JsonSettings);
			if (neededResponse?.Data?.Data == null) throw new Exception("Failed to deserialize response");

			T[] results = neededResponse.Data.Data;

			// Store results in cache
			long time = DateTimeOffset.Now.ToUnixTimeSeconds();
			Cache[baseQueryKey] = (time + ttl, results);

			// Cache raw response for offline use
			RatConfig.WriteToCache(baseQueryKey, rawResponse);

			Logger.LogInfo($"Completed fetch in {sw.ElapsedMilliseconds}ms: {results.Length} total items for \"{baseQueryKey}\"");
		} catch (Exception e) {
			Logger.LogWarning($"Failed request for: \"{baseQueryKey}\".", e);

			// If we have existing cached data, extend its TTL to prevent rapid retries
			if (Cache.TryGetValue(baseQueryKey, out var existingCache))
			{
				long time = DateTimeOffset.Now.ToUnixTimeSeconds();
				Cache[baseQueryKey] = (time + RatConfig.SuperShortTTL, existingCache.response);
				Logger.LogInfo($"Extended cache TTL for: \"{baseQueryKey}\" to prevent rapid retries");
				return;
			}

			// Try to load from offline cache
			if (RatConfig.ReadFromCache(baseQueryKey, out string cachedResponse)) {
				Logger.LogInfo($"Read from offline cache for: \"{baseQueryKey}\"");

				ResponseData<T[]>? neededResponse = JsonConvert.DeserializeObject<ResponseData<T[]>?>(cachedResponse, JsonSettings);
				if (neededResponse?.Data?.Data != null) {
					long time = DateTimeOffset.Now.ToUnixTimeSeconds();
					Cache[baseQueryKey] = (time + RatConfig.SuperShortTTL, neededResponse.Data.Data);
					return;
				}
			}

			if (!Cache.ContainsKey(baseQueryKey)) {
				throw new Exception("Failed to fetch query response and no cache available.");
			}
		} finally {
			// Always remove from pending requests when done
			PendingRequests.TryRemove(baseQueryKey, out _);
		}
	}

	private static T[] GetCached<T>(string baseQueryKey, Func<string> queryBuilder, long ttl, bool isRetry = false) where T : class {
		if (!Cache.TryGetValue(baseQueryKey, out (long expire, object response) value)) {
			if (isRetry) throw new Exception("Retrying to fetch query response failed.");

			Logger.LogInfo($"Query not found in cache: \"{baseQueryKey}\"");
			Task.Run(() => QueueRequest<T>(baseQueryKey, queryBuilder, ttl)).Wait();
			return GetCached<T>(baseQueryKey, queryBuilder, ttl, true);
		}

		// Queue request if cache is expired and no request is already pending
		long time = DateTimeOffset.Now.ToUnixTimeSeconds();
		if (time > value.expire && !PendingRequests.ContainsKey(baseQueryKey)) {
			Task.Run(() => QueueRequest<T>(baseQueryKey, queryBuilder, ttl));
		}

		return (T[])value.response;
	}

	/// <summary>
	/// Initializes cache from offline storage first, then queues background refresh.
	/// Returns true if all caches were loaded from offline storage.
	/// </summary>
	public static bool TryInitializeCacheFromOffline() {
		Logger.LogInfo("Attempting to load API cache from offline storage...");
		
		bool itemsLoaded = TryLoadFromOfflineCache<Item>(ItemsQueryKey(), RatConfig.MediumTTL);
		bool tasksLoaded = TryLoadFromOfflineCache<TTask>(TasksQueryKey(), RatConfig.LongTTL);
		bool hideoutLoaded = TryLoadFromOfflineCache<HideoutStation>(HideoutStationsQueryKey(), RatConfig.LongTTL);
		bool mapsLoaded = TryLoadFromOfflineCache<Map>(MapsQueryKey(), RatConfig.LongTTL);

		bool allLoaded = itemsLoaded && tasksLoaded && hideoutLoaded && mapsLoaded;
		
		if (allLoaded) {
			Logger.LogInfo("All API caches loaded from offline storage");
		} else {
			Logger.LogWarning($"Offline cache status - Items: {itemsLoaded}, Tasks: {tasksLoaded}, Hideout: {hideoutLoaded}, Maps: {mapsLoaded}");
		}

		return allLoaded;
	}

	/// <summary>
	/// Full cache initialization - waits for all requests to complete
	/// </summary>
	public static async Task InitializeCache() {
		await Task.WhenAll(
			Task.Run(() => QueueRequest<Item>(ItemsQueryKey(), ItemsQuery, RatConfig.MediumTTL)),
			Task.Run(() => QueueRequest<TTask>(TasksQueryKey(), TasksQuery, RatConfig.LongTTL)),
			Task.Run(() => QueueRequest<HideoutStation>(HideoutStationsQueryKey(), HideoutStationsQuery, RatConfig.LongTTL)),
			Task.Run(() => QueueRequest<Map>(MapsQueryKey(), MapsQuery, RatConfig.LongTTL))
		).ConfigureAwait(false);
	}

	public static Item[] GetItems(LanguageCode language, GameMode gameMode) => GetCached<Item>(ItemsQueryKey(language, gameMode), () => ItemsQuery(language, gameMode), RatConfig.MediumTTL);
	public static Item[] GetItems() => GetCached<Item>(ItemsQueryKey(), ItemsQuery, RatConfig.MediumTTL);

	public static TTask[] GetTasks(LanguageCode language, GameMode gameMode) => GetCached<TTask>(TasksQueryKey(language, gameMode), () => TasksQuery(language, gameMode), RatConfig.LongTTL);
	public static TTask[] GetTasks() => GetCached<TTask>(TasksQueryKey(), TasksQuery, RatConfig.LongTTL);

	public static HideoutStation[] GetHideoutStations(LanguageCode language, GameMode gameMode) => GetCached<HideoutStation>(HideoutStationsQueryKey(language, gameMode), () => HideoutStationsQuery(language, gameMode), RatConfig.LongTTL);
	public static HideoutStation[] GetHideoutStations() => GetCached<HideoutStation>(HideoutStationsQueryKey(), HideoutStationsQuery, RatConfig.LongTTL);

	public static Map[] GetMaps(LanguageCode language, GameMode gameMode) => GetCached<Map>(MapsQueryKey(language, gameMode), () => MapsQuery(language, gameMode), RatConfig.LongTTL);
	public static Map[] GetMaps() => GetCached<Map>(MapsQueryKey(), MapsQuery, RatConfig.LongTTL);

	#region Items Query

	private static string ItemsQueryKey() => ItemsQueryKey(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);
	private static string ItemsQueryKey(LanguageCode language, GameMode gameMode) => $"items_{language}_{gameMode}";

	private static string ItemsQuery() => ItemsQuery(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);
	private static string ItemsQuery(LanguageCode language, GameMode gameMode) {
		return new QueryQueryBuilder().WithItems(new ItemQueryBuilder().WithAllScalarFields()
		.WithProperties(new ItemPropertiesQueryBuilder().WithAllScalarFields()
			.WithItemPropertiesAmmoFragment(new ItemPropertiesAmmoQueryBuilder().WithAllScalarFields())
			.WithItemPropertiesFoodDrinkFragment(new ItemPropertiesFoodDrinkQueryBuilder().WithAllScalarFields()
				.WithStimEffects(new StimEffectQueryBuilder().WithAllScalarFields()))
			.WithItemPropertiesStimFragment(new ItemPropertiesStimQueryBuilder().WithAllScalarFields()
				.WithStimEffects(new StimEffectQueryBuilder().WithAllScalarFields()))
			.WithItemPropertiesMedicalItemFragment(new ItemPropertiesMedicalItemQueryBuilder().WithAllScalarFields())
			.WithItemPropertiesMedKitFragment(new ItemPropertiesMedKitQueryBuilder().WithAllScalarFields()))
		.WithSellFor(new ItemPriceQueryBuilder().WithAllScalarFields()
			.WithVendor(new VendorQueryBuilder().WithAllScalarFields()
				.WithTraderOfferFragment(new TraderOfferQueryBuilder().WithAllScalarFields()
					.WithTrader(new TraderQueryBuilder().WithAllScalarFields()))))
		.WithBuyFor(new ItemPriceQueryBuilder().WithAllScalarFields()
			.WithVendor(new VendorQueryBuilder().WithAllScalarFields()
				.WithTraderOfferFragment(new TraderOfferQueryBuilder().WithAllScalarFields()
					.WithTrader(new TraderQueryBuilder().WithAllScalarFields()))))
		.WithCategory(new ItemCategoryQueryBuilder().WithAllScalarFields())
		.WithCategories(new ItemCategoryQueryBuilder().WithAllScalarFields())
		.WithUsedInTasks(new TaskQueryBuilder().WithId())
		.WithReceivedFromTasks(new TaskQueryBuilder().WithId())
		.WithBartersFor(new BarterQueryBuilder().WithId())
		.WithBartersUsing(new BarterQueryBuilder().WithId())
		.WithCraftsFor(new CraftQueryBuilder().WithId())
		.WithCraftsUsing(new CraftQueryBuilder().WithId())
		.WithTypes()
		, alias: "data", lang: language, gameMode: gameMode).Build();
	}

	#endregion

	#region Tasks Query

	private static string TasksQueryKey() => TasksQueryKey(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);
	private static string TasksQueryKey(LanguageCode language, GameMode gameMode) => $"tasks_{language}_{gameMode}";

	private static string TasksQuery() => TasksQuery(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);
	private static string TasksQuery(LanguageCode language, GameMode gameMode) {
		return new QueryQueryBuilder().WithTasks(new TaskQueryBuilder().WithAllScalarFields()
			.WithKappaRequired()
			.WithMap(new MapQueryBuilder().WithAllScalarFields())
			.WithTrader(new TraderQueryBuilder().WithAllScalarFields())
			.WithObjectives(new TaskObjectiveQueryBuilder().WithAllScalarFields()
				.WithTaskObjectiveBasicFragment(new TaskObjectiveBasicQueryBuilder().WithAllScalarFields()
					.WithZones(new TaskZoneQueryBuilder().WithMap(new MapQueryBuilder().WithId()).WithPosition(new MapPositionQueryBuilder().WithAllScalarFields())))

				.WithTaskObjectiveBuildItemFragment(new TaskObjectiveBuildItemQueryBuilder().WithAllScalarFields()
					.WithItem(new ItemQueryBuilder().WithAllScalarFields()))

				.WithTaskObjectiveExperienceFragment(new TaskObjectiveExperienceQueryBuilder().WithAllScalarFields())

				.WithTaskObjectiveExtractFragment(new TaskObjectiveExtractQueryBuilder().WithAllScalarFields())

				.WithTaskObjectiveItemFragment(new TaskObjectiveItemQueryBuilder().WithAllScalarFields()
					.WithZones(new TaskZoneQueryBuilder().WithMap(new MapQueryBuilder().WithId()).WithPosition(new MapPositionQueryBuilder().WithAllScalarFields()))
					.WithItems(new ItemQueryBuilder().WithAllScalarFields()))

				.WithTaskObjectiveMarkFragment(new TaskObjectiveMarkQueryBuilder().WithAllScalarFields()
					.WithZones(new TaskZoneQueryBuilder().WithMap(new MapQueryBuilder().WithId()).WithPosition(new MapPositionQueryBuilder().WithAllScalarFields()))
					.WithMarkerItem(new ItemQueryBuilder().WithAllScalarFields()))

				.WithTaskObjectivePlayerLevelFragment(new TaskObjectivePlayerLevelQueryBuilder().WithAllScalarFields())

				.WithTaskObjectiveQuestItemFragment(new TaskObjectiveQuestItemQueryBuilder().WithAllScalarFields()
					.WithZones(new TaskZoneQueryBuilder().WithMap(new MapQueryBuilder().WithId()).WithPosition(new MapPositionQueryBuilder().WithAllScalarFields()))
					.WithQuestItem(new QuestItemQueryBuilder().WithAllScalarFields()))

				.WithTaskObjectiveShootFragment(new TaskObjectiveShootQueryBuilder().WithAllScalarFields())

				.WithTaskObjectiveSkillFragment(new TaskObjectiveSkillQueryBuilder().WithAllScalarFields())

				.WithTaskObjectiveTaskStatusFragment(new TaskObjectiveTaskStatusQueryBuilder().WithAllScalarFields())

				.WithTaskObjectiveTraderLevelFragment(new TaskObjectiveTraderLevelQueryBuilder().WithAllScalarFields()
					.WithTrader(new TraderQueryBuilder().WithAllScalarFields()))

				.WithTaskObjectiveTraderStandingFragment(new TaskObjectiveTraderStandingQueryBuilder().WithAllScalarFields()
					.WithTrader(new TraderQueryBuilder().WithAllScalarFields()))

				.WithTaskObjectiveUseItemFragment(new TaskObjectiveUseItemQueryBuilder().WithAllScalarFields()
					.WithZones(new TaskZoneQueryBuilder().WithMap(new MapQueryBuilder().WithId()).WithPosition(new MapPositionQueryBuilder().WithAllScalarFields()))
					.WithUseAny(new ItemQueryBuilder().WithAllScalarFields())))
			.WithTaskRequirements(new TaskStatusRequirementQueryBuilder().WithAllScalarFields()
				.WithTask(new TaskQueryBuilder().WithAllScalarFields()))
		, alias: "data", lang: language, gameMode: gameMode).Build();
	}

	#endregion

	#region HideoutStations Query

	private static string HideoutStationsQueryKey() => HideoutStationsQueryKey(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);
	private static string HideoutStationsQueryKey(LanguageCode language, GameMode gameMode) => $"hideout_{language}_{gameMode}";

	private static string HideoutStationsQuery() => HideoutStationsQuery(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);
	private static string HideoutStationsQuery(LanguageCode language, GameMode gameMode) {
		return new QueryQueryBuilder().WithHideoutStations(new HideoutStationQueryBuilder().WithAllScalarFields()
			.WithLevels(new HideoutStationLevelQueryBuilder().WithAllScalarFields()
				.WithItemRequirements(new RequirementItemQueryBuilder().WithAllScalarFields()
					.WithItem(new ItemQueryBuilder().WithAllScalarFields()))
				.WithStationLevelRequirements(new RequirementHideoutStationLevelQueryBuilder().WithAllScalarFields()
					.WithStation(new HideoutStationQueryBuilder().WithAllScalarFields()))
				.WithCrafts(new CraftQueryBuilder().WithAllScalarFields()
					.WithRequiredItems(new ContainedItemQueryBuilder().WithAllScalarFields()
						.WithItem(new ItemQueryBuilder().WithAllScalarFields()))
					.WithRewardItems(new ContainedItemQueryBuilder().WithAllScalarFields()
						.WithItem(new ItemQueryBuilder().WithAllScalarFields()))))
		, alias: "data", lang: language, gameMode: gameMode).Build();
	}

	#endregion

	#region Maps Query

	private static string MapsQueryKey() => MapsQueryKey(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);
	private static string MapsQueryKey(LanguageCode language, GameMode gameMode) => $"maps_{language}_{gameMode}";

	private static string MapsQuery() => MapsQuery(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);
	private static string MapsQuery(LanguageCode language, GameMode gameMode)
	{
		return new QueryQueryBuilder().WithMaps(new MapQueryBuilder().WithAllScalarFields()
			.WithExtracts(new MapExtractQueryBuilder().WithAllScalarFields()
				.WithPosition(new MapPositionQueryBuilder().WithAllScalarFields())
				.WithTransferItem(new ContainedItemQueryBuilder().WithAllScalarFields()
					.WithItem(new ItemQueryBuilder().WithId())))
			.WithTransits(new MapTransitQueryBuilder().WithAllScalarFields()
				.WithPosition(new MapPositionQueryBuilder().WithAllScalarFields()))
		, alias: "data", lang: language, gameMode: gameMode).Build();
	}

	#endregion
}
