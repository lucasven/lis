using Microsoft.Extensions.Options;

namespace Lis.Channels.Mattermost;

public sealed class MattermostBotRegistry(IOptions<MattermostOptions> options) {

	private readonly Dictionary<string, MattermostBotConfig> _byAgentName =
		options.Value.Bots.ToDictionary(b => b.AgentName, StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, MattermostBotConfig> _byUserId =
		options.Value.Bots.ToDictionary(b => b.UserId);

	private readonly Dictionary<long, MattermostBotConfig> _byAgentId = new();

	private readonly HashSet<string> _allBotUserIds =
		options.Value.Bots.Select(b => b.UserId).ToHashSet();

	public IReadOnlyList<MattermostBotConfig> All => options.Value.Bots;

	public MattermostBotConfig? GetByAgentName(string agentName) =>
		this._byAgentName.GetValueOrDefault(agentName);

	public MattermostBotConfig? GetByUserId(string userId) =>
		this._byUserId.GetValueOrDefault(userId);

	public MattermostBotConfig? GetByAgentId(long agentId) =>
		this._byAgentId.GetValueOrDefault(agentId);

	public bool IsBotUserId(string userId) => this._allBotUserIds.Contains(userId);

	public void MapAgentId(long agentId, string agentName) {
		if (this._byAgentName.TryGetValue(agentName, out MattermostBotConfig? bot))
			this._byAgentId[agentId] = bot;
	}
}
