namespace Lis.Core.Channel;

public interface IChannelClientProvider {
	IChannelClient Get(string channel);
}
