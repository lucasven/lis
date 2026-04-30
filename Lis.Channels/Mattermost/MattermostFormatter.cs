using Lis.Core.Channel;

namespace Lis.Channels.Mattermost;

public sealed class MattermostFormatter : IMessageFormatter {
	public string Format(string content) {
		// Mattermost supports standard Markdown and has a 16383 char limit
		return content.Length > 16383 ? content[..16380] + "..." : content;
	}
}
