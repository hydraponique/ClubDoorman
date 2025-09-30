using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using ClubDoorman.Infrastructure;
using ClubDoorman.Services.Core.Configuration;
using ClubDoorman.Services.UserManagement;
using ClubDoorman.Services.Messaging;

namespace ClubDoorman.Services.UserManagement;

internal sealed class UserManager : IUserManager
{
    private readonly ILogger<UserManager> _logger;
    private readonly ApprovedUsersStorage _approvedUsersStorage;
    private readonly IAppConfig _appConfig;
    private readonly ConcurrentDictionary<long, byte> _banlist = [];
    private readonly SemaphoreSlim _semaphore = new(1);
    private readonly HttpClient _clubHttpClient = new();
    private readonly HttpClient _httpClient = new();
    
    // Тестовый блэклист из переменной окружения DOORMAN_TEST_BLACKLIST_IDS
    private static readonly HashSet<long> _testBlacklist = LoadTestBlacklist();

    public UserManager(ILogger<UserManager> logger, ApprovedUsersStorage approvedUsersStorage, IAppConfig appConfig)
    {
        _logger = logger;
        _approvedUsersStorage = approvedUsersStorage;
        _appConfig = appConfig;
        
        // Логируем состояние тестового блэклиста при создании UserManager
        Console.WriteLine($"[DEBUG] UserManager создан: тестовый блэклист содержит {_testBlacklist.Count} ID(s): [{string.Join(", ", _testBlacklist)}]");
        
        if (appConfig.ClubServiceToken == null)
            _logger.LogWarning("DOORMAN_CLUB_SERVICE_TOKEN variable is not set, additional club checks disabled");
        else
            _clubHttpClient.DefaultRequestHeaders.Add("X-Service-Token", appConfig.ClubServiceToken);
    }

    public async Task RefreshBanlist()
    {
        try
        {
            await _semaphore.WaitAsync();
            try
            {
                var httpClient = new HttpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var banlist = await httpClient.GetFromJsonAsync<long[]>("https://lols.bot/spam/banlist.json", cts.Token);
                
                if (banlist != null && banlist.Length > 0)
                {
                    var oldCount = _banlist.Count;
                    
                    // Очищаем текущий список
                    foreach (var key in _banlist.Keys.ToArray())
                        _banlist.TryRemove(key, out _);
                    
                    // Заполняем его новыми значениями: 1 = banned
					// 5228763720 - @JohnPhuket (Евгений Пхукет)
					// 554933235 - @cocorental (Анастасия Аренда авто и байков Пхукет)
                   // 6688417194 - @ageres_thd (Sergey Bestie_rent)
                    foreach (var id in banlist)
					{
                        if (id == 5228763720 || id == 554933235 || id == 6688417194)
						{
							_banlist.TryAdd(id, 0);
						}
						else
						{
							_banlist.TryAdd(id, 1);
						}
                    }
					
                    _logger.LogInformation("Обновлен банлист из lols.bot: было {OldCount}, стало {NewCount} записей", oldCount, _banlist.Count);
                }
                else
                {
                    _logger.LogWarning("Получен пустой банлист от lols.bot или ответ был null");
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось обновить банлист из lols.bot");
        }
    }

    /// <summary>
    /// Проверяет, одобрен ли пользователь
    /// </summary>
    /// <param name="userId">ID пользователя</param>
    /// <param name="groupId">ID группы (для проверки группового одобрения)</param>
    /// <returns>true, если пользователь одобрен</returns>
    public bool Approved(long userId, long? groupId = null)
    {
        return _approvedUsersStorage.IsApproved(userId, groupId);
    }

    /// <summary>
    /// Одобряет пользователя в зависимости от настроек
    /// </summary>
    /// <param name="userId">ID пользователя</param>
    /// <param name="groupId">ID группы (для группового одобрения)</param>
    public async ValueTask Approve(long userId, long? groupId = null)
    {
        if (Config.GlobalApprovalMode)
        {
            // Глобальный режим: одобряем глобально
            _approvedUsersStorage.ApproveUserGlobally(userId);
        }
        else
        {
            // Групповой режим: одобряем в конкретной группе
            if (groupId.HasValue)
            {
                _approvedUsersStorage.ApproveUserInGroup(userId, groupId.Value);
            }
            else
            {
                // Если группа не указана, одобряем глобально как fallback
                _approvedUsersStorage.ApproveUserGlobally(userId);
            }
        }
    }

    /// <summary>
    /// Удаляет одобрение пользователя
    /// </summary>
    /// <param name="userId">ID пользователя</param>
    /// <param name="groupId">ID группы (для удаления группового одобрения)</param>
    /// <param name="removeAll">Удалить все одобрения пользователя</param>
    /// <returns>true, если одобрение было удалено</returns>
    public bool RemoveApproval(long userId, long? groupId = null, bool removeAll = false)
    {
        try
        {
            if (removeAll)
            {
                return _approvedUsersStorage.RemoveAllApprovals(userId);
            }
            
            if (groupId.HasValue)
            {
                // Удаляем одобрение в конкретной группе
                return _approvedUsersStorage.RemoveGroupApproval(userId, groupId.Value);
            }
            else
            {
                // Удаляем глобальное одобрение
                return _approvedUsersStorage.RemoveGlobalApproval(userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось удалить одобрение пользователя {UserId}", userId);
            return false;
        }
    }

    /// <summary>
    /// Получает информацию об одобрении пользователя
    /// </summary>
    public (bool isGlobal, Dictionary<long, GroupApprovalInfo> groupApprovals) GetApprovalInfo(long userId)
    {
        var isGlobal = _approvedUsersStorage.IsGloballyApproved(userId);
        var groupApprovals = _approvedUsersStorage.GetUserGroupApprovals(userId);
        return (isGlobal, groupApprovals);
    }

    /// <summary>
    /// Получает статистику одобрений
    /// </summary>
    public (int globalCount, int groupCount, int totalGroupApprovals) GetApprovalStats()
    {
        return _approvedUsersStorage.GetApprovalStats();
    }

    /// <summary>
    /// Проверяет, находится ли пользователь в банлисте спамеров.
    /// Использует только кэшированный статический банлист из lols.bot без дополнительных HTTP запросов.
    /// </summary>
    /// <param name="userId">ID пользователя для проверки</param>
    /// <returns>true если пользователь в банлисте, false если нет</returns>
    public async ValueTask<bool> InBanlist(long userId)
    {
        Console.WriteLine($"[DEBUG] UserManager.InBanlist: проверяем пользователя {userId} (тестовых ID: {_testBlacklist.Count})");
        _logger.LogDebug("InBanlist: проверяем пользователя {UserId} (тестовых ID: {TestCount})", userId, _testBlacklist.Count);
        
        // 1. Сначала проверяем тестовый блэклист
        if (_testBlacklist.Contains(userId))
        {
            Console.WriteLine($"[DEBUG] 🎯 НАЙДЕН в тестовом блэклисте: {userId}");
            _logger.LogWarning("🎯 Пользователь {UserId} найден в ТЕСТОВОМ блэклисте", userId);
            return true;
        }
        
        // 2. Проверяем кэшированный результат из статического банлиста
        if (_banlist.TryGetValue(userId, out var cachedResult))
        {
            var isBanned = cachedResult == 1;
            _logger.LogDebug("✅ Пользователь {UserId} найден в кэше: {Status}", userId, isBanned ? "ЗАБЛОКИРОВАН" : "НЕ заблокирован");
            return isBanned; // 1 = banned, 0 = not banned
        }
        
        // 3. Если пользователя нет в статическом банлисте - считаем НЕ заблокированным и кэшируем
        _banlist.TryAdd(userId, 0); // 0 = not banned
        _logger.LogDebug("✅ Пользователь {UserId} НЕ в банлисте lols.bot, кэшируем как незаблокированного", userId);
        return false;
    }

    public async ValueTask<string?> GetClubUsername(long userId)
    {
        if (_appConfig.ClubServiceToken == null)
            return null;
        var url = $"{_appConfig.ClubUrl}user/by_telegram_id/{userId}.json";
        try
        {
                    using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(5));
            var get = await _clubHttpClient.GetAsync(url, cts.Token);
            
            if (!get.IsSuccessStatusCode)
            {
                _logger.LogWarning("GetClubUsername: HTTP {StatusCode} для пользователя {UserId}", get.StatusCode, userId);
                return null;
            }
            
            var content = await get.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(content) || !content.TrimStart().StartsWith("{"))
            {
                _logger.LogWarning("GetClubUsername: не JSON ответ для пользователя {UserId}: '{Content}'", userId, content?.Length > 100 ? content.Substring(0, 100) + "..." : content);
                return null;
            }
            
            var response = await get.Content.ReadFromJsonAsync<ClubByTgIdResponse>(cancellationToken: cts.Token);
            var fullName = response?.user?.full_name;
            if (!string.IsNullOrEmpty(fullName))
                await Approve(userId); // Одобряем глобально, так как это клубный пользователь
            return fullName;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "GetClubUsername для пользователя {UserId}", userId);
            return null;
        }
    }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
#pragma warning disable IDE1006 // Naming Styles

    private record LolsBotApiResponse(long user_id, int? offenses, bool banned, bool ok, string? when, float? spam_factor, bool? scammer);

    internal class ClubByTgIdResponse
    {
        public Error? error { get; set; }
        public User? user { get; set; }
    }

    internal class Error
    {
        public string code { get; set; }
        public Data data { get; set; }
        public object message { get; set; }
        public string title { get; set; }
    }

    internal class Data { }

    internal class User
    {
        public string avatar { get; set; }
        public string bio { get; set; }
        public string city { get; set; }
        public string company { get; set; }
        public string country { get; set; }
        public DateTime created_at { get; set; }
        public string? full_name { get; set; }
        public string id { get; set; }
        public bool is_active_member { get; set; }
        public DateTime membership_expires_at { get; set; }
        public DateTime membership_started_at { get; set; }
        public string moderation_status { get; set; }
        public string payment_status { get; set; }
        public string position { get; set; }
        public string slug { get; set; }
        public int upvotes { get; set; }
    }

    /// <summary>
    /// Загружает тестовый блэклист из переменной окружения DOORMAN_TEST_BLACKLIST_IDS
    /// Формат: "123456,789012,345678" (ID через запятую)
    /// </summary>
    private static HashSet<long> LoadTestBlacklist()
    {
        var testIds = Environment.GetEnvironmentVariable("DOORMAN_TEST_BLACKLIST_IDS");
        if (string.IsNullOrWhiteSpace(testIds))
        {
            Console.WriteLine("[DEBUG] DOORMAN_TEST_BLACKLIST_IDS не задана - тестовый блэклист пустой");
            return [];
        }

        var result = new HashSet<long>();
        var ids = testIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var idStr in ids)
        {
            if (long.TryParse(idStr.Trim(), out var id))
            {
                result.Add(id);
            }
            else
            {
                Console.WriteLine($"[WARNING] Некорректный ID в DOORMAN_TEST_BLACKLIST_IDS: '{idStr}'");
            }
        }
        
        Console.WriteLine($"[DEBUG] Загружен тестовый блэклист: {result.Count} ID(s) [{string.Join(", ", result)}]");
        return result;
    }

#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
#pragma warning restore IDE1006 // Naming Styles
} 