using Microsoft.Extensions.DependencyInjection;

namespace Lis.Core.Channel;

public sealed class ChannelClientProvider(IServiceProvider serviceProvider) : IChannelClientProvider {
	public IChannelClient Get(string channel) {
		return serviceProvider.GetRequiredKeyedService<IChannelClient>(channel);
	}
}
