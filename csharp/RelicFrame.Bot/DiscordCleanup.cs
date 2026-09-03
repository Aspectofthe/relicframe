using Discord;

internal static class DiscordCleanup
{
    public static async Task BotMessagesAsync(IMessageChannel channel, ulong botUserId, params ulong[] keepIds)
    {
        var keep = keepIds.Where(id => id != 0).ToHashSet();
        // Managed boards should contain only their current bot message(s). Never delete user-authored content.
        var messages = await channel.GetMessagesAsync(500, CacheMode.AllowDownload).FlattenAsync();
        foreach (var message in messages.Where(message => message.Author.Id == botUserId && !keep.Contains(message.Id)))
        {
            try { await message.DeleteAsync(); }
            catch (Discord.Net.HttpException) { }
        }
    }
}
