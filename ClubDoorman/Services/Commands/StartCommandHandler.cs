using ClubDoorman.Infrastructure;
using ClubDoorman.Services;
using ClubDoorman.Models.Notifications;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using ClubDoorman.Services.Core.Configuration;
using ClubDoorman.Services.Telegram;
using ClubDoorman.Services.Messaging;

namespace ClubDoorman.Services.Commands;

/// <summary>
/// Обработчик команды /start
/// </summary>
public class StartCommandHandler : ICommandHandler
{
    private readonly ITelegramBotClientWrapper _bot;
    private readonly ILogger<StartCommandHandler> _logger;
    private readonly IMessageService _messageService;
    private readonly IAppConfig _appConfig;

    public string CommandName => "start";

    public StartCommandHandler(ITelegramBotClientWrapper bot, ILogger<StartCommandHandler> logger, IMessageService messageService, IAppConfig appConfig)
    {
        _bot = bot;
        _logger = logger;
        _messageService = messageService;
        _appConfig = appConfig;
    }

    public async Task HandleAsync(Message message, CancellationToken cancellationToken = default)
    {
        if (message.Chat.Type != ChatType.Private)
            return;

        // Если whitelist активен - не отвечаем в личке
        if (!_appConfig.IsPrivateStartAllowed())
        {
            _logger.LogDebug("Команда /start в личке отключена - активен whitelist");
            return;
        }

        var about = GetStartMessage();
        await _messageService.SendUserNotificationAsync(
            message.From!, 
            message.Chat, 
            UserNotificationType.Welcome, 
            new SimpleNotificationData(message.From!, message.Chat, about), 
            cancellationToken
        );
    }

    private static string GetStartMessage()
    {
        return """
<b>👋 Привет! Я современный антиспам-бот для Telegram</b>

Защищаю <b>группы</b> и <b>каналы с обсуждениями</b> от спама, флуда и нежелательных участников.
""";
    }
} 