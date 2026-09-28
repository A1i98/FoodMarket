using Telegram.Bot.Types.Enums;

namespace FoodMarket;

public enum GroupCommandResult { NotCommand, Unauthorized, Installed, Uninstalled }

public sealed class GroupAccess(MarketStore store, MarketOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public bool IsInstalled(long chatId) => store.Group(chatId)?.Active == true;

    public static bool AllowsInline(ChatType? chatType) => chatType is ChatType.Private or ChatType.Sender;

    public void Deactivate(long chatId)
    {
        var group = store.Group(chatId);
        if (group is null) return;
        group.Active = false;
        store.Save(group);
    }

    public GroupCommandResult Handle(long actor, long chatId, ChatType chatType, string? title, string text)
    {
        if (chatType is not (ChatType.Group or ChatType.Supergroup)) return GroupCommandResult.NotCommand;
        var command = PersianText.Normalize(text);
        var install = command is "نصب" or "/نصب" or "/install";
        var uninstall = command is "حذف نصب" or "/حذف_نصب" or "/uninstall";
        if (!install && !uninstall) return GroupCommandResult.NotCommand;
        if (options.AdminUserId <= 0 || actor != options.AdminUserId) return GroupCommandResult.Unauthorized;
        var group = store.Group(chatId) ?? new InstalledGroup { Id = chatId };
        group.Active = install;
        group.Title = string.IsNullOrWhiteSpace(title) ? $"گروه {chatId}" : title[..Math.Min(100, title.Length)];
        group.InstalledBy = actor;
        group.InstalledUtc = _clock.GetUtcNow().UtcDateTime;
        store.Save(group);
        return install ? GroupCommandResult.Installed : GroupCommandResult.Uninstalled;
    }
}
