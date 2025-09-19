using System.Collections.Concurrent;
using ClubDoorman.Models;
using ClubDoorman.Infrastructure;
using ClubDoorman.Models.Requests;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types.Enums;
using ClubDoorman.Services.Core.Configuration;
using ClubDoorman.Services.Telegram;
using ClubDoorman.Services.Messaging;
using ClubDoorman.Services.Violation;
using ClubDoorman.Services.UserBan;

namespace ClubDoorman.Services.Captcha;

/// <summary>
/// Сервис для работы с капчей
/// 
/// ПРИМЕЧАНИЕ: CaptchaService НЕ мигрирован на UserBanService по следующим причинам:
/// - Автоматический разбан критически важен для капчи (через 20 минут)
/// - UserBanService не поддерживает автоматический разбан
/// - Логика капчи тесно связана с баном за неудачу
/// - Принцип единственной ответственности не нарушается
/// - CaptchaService остается самодостаточным
/// 
/// Прямые вызовы BanChatMember остаются здесь для управления временными банами
/// с автоматическим разбаном, что критично для функциональности капчи.
/// </summary>
public class CaptchaService : ICaptchaService
{
    private readonly ConcurrentDictionary<string, CaptchaInfo> _captchaNeededUsers = new();
    private readonly ITelegramBotClientWrapper _bot;
    private readonly ILogger<CaptchaService> _logger;
    private readonly IMessageService _messageService;
    private readonly IAppConfig _appConfig;
    private readonly IViolationTracker _violationTracker;
    private readonly IUserBanService _userBanService;

    // Черный список имен для отображения
    private readonly List<string> _namesBlacklist = ["p0rn", "porn", "порн", "п0рн", "pоrn", "пoрн", "bot"];

    /// <summary>
    /// Создает экземпляр сервиса капчи.
    /// </summary>
    /// <param name="bot">Клиент Telegram бота</param>
    /// <param name="logger">Логгер для записи событий</param>
    /// <param name="messageService">Сервис сообщений</param>
    /// <param name="appConfig">Конфигурация приложения</param>
    /// <param name="violationTracker">Трекер нарушений</param>
    public CaptchaService(ITelegramBotClientWrapper bot, ILogger<CaptchaService> logger, IMessageService messageService, IAppConfig appConfig, IViolationTracker violationTracker, IUserBanService userBanService)
    {
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _messageService = messageService ?? throw new ArgumentNullException(nameof(messageService));
        _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
        _violationTracker = violationTracker ?? throw new ArgumentNullException(nameof(violationTracker));
        _userBanService = userBanService ?? throw new ArgumentNullException(nameof(userBanService));
    }

    /// <summary>
    /// Создает капчу для нового пользователя в чате, либо возвращает null, если капча отключена для чата.
    /// </summary>
    /// <param name="chat">Чат, в котором создается капча</param>
    /// <param name="user">Пользователь, для которого создается капча</param>
    /// <param name="userJoinMessage">Сообщение о присоединении пользователя (опционально)</param>
    /// <returns>Информация о созданной капче или null, если капча отключена для чата</returns>
    /// <exception cref="ArgumentNullException">Если chat или user равны null</exception>
    /// <summary>
    /// Создает капчу используя Request объект
    /// </summary>
    /// <param name="request">Запрос на создание капчи</param>
    /// <returns>Информация о созданной капче или null, если капча отключена для чата</returns>
    public async Task<CaptchaInfo?> CreateCaptchaAsync(CreateCaptchaRequest request)
    {
        if (request.Chat == null) throw new ArgumentNullException(nameof(request.Chat));
        if (request.User == null) throw new ArgumentNullException(nameof(request.User));

        // Отключение капчи для определённых групп
        if (_appConfig.NoCaptchaGroups.Contains(request.Chat.Id))
        {
            _logger.LogInformation($"[NO_CAPTCHA] Капча отключена для чата {request.Chat.Id}");
            return null;
        }

        const int challengeLength = 8;
        var correctAnswerIndex = Random.Shared.Next(challengeLength);
        var challenge = new List<int>(challengeLength);
        
        while (challenge.Count < challengeLength)
        {
            var rand = Random.Shared.Next(Infrastructure.Captcha.CaptchaList.Count);
            if (!challenge.Contains(rand))
                challenge.Add(rand);
        }
        
        var correctAnswer = challenge[correctAnswerIndex];
        var keyboard = challenge
            .Select(x => new InlineKeyboardButton(Infrastructure.Captcha.CaptchaList[x].Emoji) 
            { 
                CallbackData = $"cap_{request.User.Id}_{x}" 
            })
            .ToList();

        ReplyParameters? replyParams = null;
        if (request.UserJoinMessage != null)
            replyParams = request.UserJoinMessage;

        var fullNameForDisplay = Utils.FullName(request.User);
        var fullNameLower = fullNameForDisplay.ToLowerInvariant();
        var username = request.User.Username?.ToLower();
        
        if (_namesBlacklist.Any(fullNameLower.Contains) || 
            username?.Contains("porn") == true || 
            username?.Contains("p0rn") == true)
        {
            fullNameForDisplay = "новый участник чата";
        }

        var welcomeMessage = $"Привет, <a href=\"tg://user?id={request.User.Id}\">{System.Net.WebUtility.HtmlEncode(fullNameForDisplay)}</a>\n\n" +
                            $"Антиспам проверка: на какой кнопке {Infrastructure.Captcha.CaptchaList[correctAnswer].Description}?";

        // Добавляем заглушку для рекламы если нужно
        var isNoAdGroup = IsNoAdGroup(request.Chat.Id);
        var vpnAdHtml = isNoAdGroup ? "" : "\n\n💵 ОБМЕН ВАЛЮТ: <b><a href=\"https://t.me/bereza_exchange\">@bereza_exchange</a></b>";
        //welcomeMessage += vpnAdHtml;

        Message captchaMessage;
        try
        {
            var captchaRequest = new SendCaptchaMessageRequest(
                request.Chat,
                welcomeMessage,
                replyParams,
                new InlineKeyboardMarkup(keyboard),
                default
            );
            captchaMessage = await _messageService.SendCaptchaMessageAsync(captchaRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при отправке капчи для пользователя {UserId} в чате {ChatId}", request.User.Id, request.Chat.Id);
            throw;
        }

        var cts = new CancellationTokenSource();
        var captchaInfo = new CaptchaInfo(request.Chat.Id, request.Chat.Title, DateTime.UtcNow, request.User, correctAnswer, cts, request.UserJoinMessage);
        
        var key = GenerateKey(request.Chat.Id, request.User.Id);
        _captchaNeededUsers.TryAdd(key, captchaInfo);

        // Автоматическое удаление капчи и бан через 1.2 минуты
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1.2), cts.Token);
                
                // Удаляем капчу из коллекции
                if (_captchaNeededUsers.TryRemove(key, out var expiredCaptcha))
                {
                    _logger.LogInformation("Пользователь {User} (id={UserId}) не прошёл капчу (таймаут) в группе '{ChatTitle}' (id={ChatId})", 
                        Utils.FullName(expiredCaptcha.User), expiredCaptcha.User.Id, expiredCaptcha.ChatTitle ?? "-", expiredCaptcha.ChatId);
                    
                    try
                    {
                        // Баним пользователя на 20 минут
                        await _bot.BanChatMemberAsync(expiredCaptcha.ChatId, expiredCaptcha.User.Id, 
                            untilDate: DateTime.UtcNow + TimeSpan.FromMinutes(20), revokeMessages: true);
                        
                        // Удаляем сообщения
                        await _bot.DeleteMessageAsync(request.Chat.Id, captchaMessage.MessageId);
                        if (request.UserJoinMessage != null)
                            await _bot.DeleteMessageAsync(request.Chat.Id, request.UserJoinMessage.MessageId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ошибка при бане пользователя {UserId} за просроченную капчу", expiredCaptcha.User.Id);
                    }
                    
                    // Разбан через 20 минут
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromMinutes(20));
                            await _bot.UnbanChatMemberAsync(expiredCaptcha.ChatId, expiredCaptcha.User.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Ошибка при разбане пользователя {UserId}", expiredCaptcha.User.Id);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Нормальная отмена
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при автоудалении капчи");
            }
        });

        return captchaInfo;
    }



    /// <summary>
    /// Проверяет ответ пользователя на капчу.
    /// </summary>
    /// <param name="key">Ключ капчи</param>
    /// <param name="answer">Ответ пользователя</param>
    /// <returns>true, если ответ правильный</returns>
    public async Task<bool> ValidateCaptchaAsync(string key, int answer)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        if (!_captchaNeededUsers.TryRemove(key, out var info))
        {
            _logger.LogWarning("Капча {Key} не найдена", key);
            return false;
        }

        try
        {
            await info.Cts.CancelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка при отмене токена капчи {Key}", key);
        }

        return info.CorrectAnswer == answer;
    }

    /// <summary>
    /// Получает информацию о капче по ключу.
    /// </summary>
    /// <param name="key">Ключ капчи</param>
    /// <returns>Информация о капче или null, если не найдена</returns>
    public CaptchaInfo? GetCaptchaInfo(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        return _captchaNeededUsers.TryGetValue(key, out var info) ? info : null;
    }

    /// <summary>
    /// Удаляет капчу по ключу.
    /// </summary>
    /// <param name="key">Ключ капчи</param>
    /// <returns>true, если капча была удалена</returns>
    public bool RemoveCaptcha(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        return _captchaNeededUsers.TryRemove(key, out _);
    }

    /// <summary>
    /// Генерирует ключ капчи для пользователя в чате.
    /// </summary>
    /// <param name="chatId">ID чата</param>
    /// <param name="userId">ID пользователя</param>
    /// <returns>Уникальный ключ капчи</returns>
    public string GenerateKey(long chatId, long userId)
    {
        return $"{chatId}_{userId}";
    }

    /// <summary>
    /// Банит пользователей с просроченными капчами.
    /// </summary>
    public async Task BanExpiredCaptchaUsersAsync()
    {
        if (_captchaNeededUsers.IsEmpty)
            return;

        var now = DateTime.UtcNow;
        var users = _captchaNeededUsers.ToArray();
        
        foreach (var (key, captchaInfo) in users)
        {
            var minutes = (now - captchaInfo.Timestamp).TotalMinutes;
            if (minutes > 1.3)
            {
                _logger.LogInformation("Пользователь {User} (id={UserId}) не прошёл капчу (таймаут) в группе '{ChatTitle}' (id={ChatId})", 
                    Utils.FullName(captchaInfo.User), captchaInfo.User.Id, captchaInfo.ChatTitle ?? "-", captchaInfo.ChatId);

                // Регистрируем нарушение за непройденную капчу
                var shouldBan = _violationTracker.RegisterViolation(captchaInfo.User.Id, captchaInfo.ChatId, ViolationType.CaptchaFailed);
                
                if (shouldBan)
                {
                    _logger.LogWarning("Пользователь {User} (id={UserId}) достиг лимита непройденных капч в группе '{ChatTitle}' (id={ChatId}) - бан навсегда", 
                        Utils.FullName(captchaInfo.User), captchaInfo.User.Id, captchaInfo.ChatTitle ?? "-", captchaInfo.ChatId);
                }

                _captchaNeededUsers.TryRemove(key, out _);
                
                try
                {
                    // Если достигнут лимит нарушений - бан навсегда, иначе временный бан на 20 минут
                    var banUntilDate = shouldBan ? null : (DateTime?)now.AddMinutes(20);
                    
                    await _bot.BanChatMemberAsync(captchaInfo.ChatId, captchaInfo.User.Id, 
                        untilDate: banUntilDate, revokeMessages: true);
                    
                    if (captchaInfo.UserJoinedMessage != null)
                        await _bot.DeleteMessageAsync(captchaInfo.ChatId, captchaInfo.UserJoinedMessage.MessageId);

                    // Отправляем уведомление через централизованную систему
                    if (shouldBan && captchaInfo.UserJoinedMessage != null)
                    {
                        // Используем существующее сообщение для передачи в UserBanService
                        await _userBanService.TrackViolationAndBanIfNeededAsync(captchaInfo.UserJoinedMessage, captchaInfo.User, "непройденная капча (таймаут)", CancellationToken.None);
                    }

                    // Разбан через 20 минут только если это не постоянный бан
                    if (!shouldBan)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromMinutes(20));
                                await _bot.UnbanChatMemberAsync(captchaInfo.ChatId, captchaInfo.User.Id);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Ошибка при разбане пользователя {UserId}", captchaInfo.User.Id);
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ошибка при бане пользователя за просроченную капчу");
                }
            }
        }
    }

    /// <summary>
    /// Проверяет, является ли группа группой без рекламы VPN.
    /// </summary>
    /// <param name="chatId">ID чата</param>
    /// <returns>true, если группа без рекламы VPN</returns>
    private static bool IsNoAdGroup(long chatId)
    {
        return Config.NoVpnAdGroups.Contains(chatId);
    }
} 