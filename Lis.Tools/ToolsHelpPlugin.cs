using System.ComponentModel;
using System.Reflection;

using Lis.Core.Util;

using Microsoft.SemanticKernel;

namespace Lis.Tools;

public sealed class ToolsHelpPlugin {

	private static readonly Dictionary<string, string> HelpContent = new(StringComparer.OrdinalIgnoreCase);

	static ToolsHelpPlugin() {
		Assembly asm    = typeof(ToolsHelpPlugin).Assembly;
		string   prefix = "Lis.Tools.Help.";

		foreach (string name in asm.GetManifestResourceNames()) {
			if (!name.StartsWith(prefix) || !name.EndsWith(".md")) continue;

			string group = name[prefix.Length..^3];
			using StreamReader reader = new(asm.GetManifestResourceStream(name)!);
			HelpContent[group] = reader.ReadToEnd();
		}
	}

	[KernelFunction("get")]
	[Description("Get detailed documentation for a tool group: workflows, parameter details, examples, and common errors. Call without arguments to list available groups.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public static string Get(
		[Description("Group name (e.g. 'skills', 'browser', 'memory'). Omit to list all groups.")] string? group = null) {

		if (group is null) {
			return "Available tool groups — call help-get(group=\"<name>\") for full documentation:\n"
			     + string.Join("\n", HelpContent.Keys.Order().Select(k => $"- {k}"));
		}

		if (HelpContent.TryGetValue(group, out string? content)) return content;

		return $"Unknown group '{group}'. Available groups:\n"
		     + string.Join("\n", HelpContent.Keys.Order().Select(k => $"- {k}"));
	}
}
