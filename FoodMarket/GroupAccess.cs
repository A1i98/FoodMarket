using Telegram.Bot.Types.Enums;

namespace FoodMarket;

public enum GroupCommandResult { NotCommand, Unauthorized, Installed, Uninstalled }
public enum InlineScope { None, PrivateListings }

public sealed class GroupAccess(MarketStore store, MarketOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public bool IsInstalled(long chatId) => store.Group(chatId)?.Active == true;

    public static InlineScope GetInlineScope(ChatType? chatType) => chatType switch
    {
        ChatType.Private or ChatType.Sender => InlineScope.PrivateListings,
        // Inline queries expose the chat type, but never the destination group's ID.
        // Group search is served by /food after checking the installed chat ID instead.
        _ => InlineScope.None
    };

    public string? AuthorizedSearchQuery(long chatId, string text, string botUsername)
    {
        if (!IsInstalled(chatId)) return null;
        return GroupSearchQuery(text, botUsername);
    }

    private static string? GroupSearchQuery(string text, string botUsername)
    {
        var parts = PersianText.Normalize(text).Split(' ', 2);
        if (parts[0] is "/food" or "/غذا" ||
            parts[0].Equals($"/food@{botUsername}", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals($"/غذا@{botUsername}", StringComparison.OrdinalIgnoreCase))
            return parts.Length == 2 ? parts[1] : "";
        return null;
    }

    public void Deactivate(long chatId)
    {
        var group = store.Group(chatId);
        if (group is null) return;
        group.Active = false;
        store.Save(group);
    }

    public GroupCommandResult Handle(long actor, long chatId, ChatType chatType, string? title, string text, string? botUsername = null)
    {
        if (chatType is not (ChatType.Group or ChatType.Supergroup)) return GroupCommandResult.NotCommand;
        var command = PersianText.Normalize(text);
        if (command.StartsWith('/') && command.Contains('@'))
        {
            var parts = command.Split('@', 2);
            if (botUsername is null || !parts[1].Equals(botUsername, StringComparison.OrdinalIgnoreCase))
                return GroupCommandResult.NotCommand;
            command = parts[0];
        }
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
        store.Save(new AdminAuditEvent { ActorId = actor, Action = install ? "group_install" : "group_uninstall", TargetId = chatId, Detail = group.Title });
        return install ? GroupCommandResult.Installed : GroupCommandResult.Uninstalled;
    }
}
