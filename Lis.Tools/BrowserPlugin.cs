using System.ComponentModel;

using Lis.Core.Util;
using Lis.Tools.Pinchtab;

using Microsoft.SemanticKernel;

namespace Lis.Tools;

/// <summary>
/// Browser automation via the PinchTab sidecar. Each agent gets its own browser
/// instance (profile "agent-{agentId}"), lazily started on first navigate and
/// closed automatically when the conversation turn ends.
/// </summary>
public sealed class BrowserPlugin(PinchtabInstanceManager instances, IPinchtabClient client) {

	[Trace("BrowserPlugin > NavigateAsync")]
	[KernelFunction("navigate")]
	[Description("Open a URL in the agent's browser. Starts the browser automatically on first use. Use headed=true to show a visible browser window (only works when a display is available).")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> NavigateAsync(
		[Description("URL to navigate to")] string url,
		[Description("Show a visible browser window (default: false, headless)")] bool headed = false,
		CancellationToken ct = default) {
		await ToolContext.NotifyAsync($"🌐 Opening: {url}", ct);

		if (headed && !DisplayProbe.IsAvailable())
			return "Headed mode is not available: no display detected on this system (e.g. running in Docker). Retry with headed=false.";

		try {
			long agentId = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");
			AgentBrowserSession session = await instances.EnsureSessionAsync(agentId, headed, ct);

			if (session.TabId is null) {
				PinchtabTab tab = await client.OpenTabAsync(session.InstanceId, url, ct);
				session.TabId = tab.TabId;
				return $"Navigated to {url} (title: {tab.Title}). Use browser-snapshot to read the page.";
			}

			await client.NavigateAsync(session.TabId, url, ct);
			return $"Navigated to {url}. Use browser-snapshot to read the page.";
		} catch (Exception ex) {
			return $"Navigation error: {ex.Message}";
		}
	}

	[Trace("BrowserPlugin > SnapshotAsync")]
	[KernelFunction("snapshot")]
	[Description("Read the current page as an accessibility tree with element refs (e0, e1, ...). Refs are used by browser-click, browser-type, and browser-press. Filter 'interactive' returns only actionable elements; 'full' returns all text content.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> SnapshotAsync(
		[Description("Filter: 'interactive' (default, actionable elements only) or 'full' (all content)")] string filter = "interactive",
		CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			return await client.SnapshotAsync(tabId, filter == "full" ? "none" : "interactive", maxTokens: 4000, ct);
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Snapshot error: {ex.Message}";
		}
	}

	[Trace("BrowserPlugin > ClickAsync")]
	[KernelFunction("click")]
	[Description("Click an element by its ref from browser-snapshot (e.g. 'e5').")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> ClickAsync(
		[Description("Element ref from browser-snapshot (e.g. 'e5')")] string @ref,
		CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			// waitNav: clicks often navigate; without it pinchtab's guard rejects the action with 409 navigation_changed
			await client.ActionAsync(tabId, new { kind = "click", @ref, waitNav = true }, ct);
			return $"Clicked {@ref}. Page may have changed — use browser-snapshot to see the result.";
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Click error: {ex.Message}";
		}
	}

	[Trace("BrowserPlugin > TypeAsync")]
	[KernelFunction("type")]
	[Description("Fill a form field by its ref from browser-snapshot. Replaces existing content.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> TypeAsync(
		[Description("Element ref from browser-snapshot (e.g. 'e3')")] string @ref,
		[Description("Text to fill into the field")] string text,
		CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			await client.ActionAsync(tabId, new { kind = "fill", @ref, value = text }, ct);
			return $"Typed into {@ref}.";
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Type error: {ex.Message}";
		}
	}

	[Trace("BrowserPlugin > PressAsync")]
	[KernelFunction("press")]
	[Description("Press a keyboard key (e.g. 'Enter', 'Tab', 'Escape'), optionally on a specific element.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> PressAsync(
		[Description("Key to press (e.g. 'Enter', 'Tab', 'Escape', 'ArrowDown')")] string key,
		[Description("Element ref to focus first (optional)")] string? @ref = null,
		CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			// waitNav: Enter on a form navigates; without it pinchtab's guard rejects the action with 409 navigation_changed
			object payload = @ref is null ? new { kind = "press", key, waitNav = true } : new { kind = "press", key, @ref, waitNav = true };
			await client.ActionAsync(tabId, payload, ct);
			return $"Pressed {key}.";
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Press error: {ex.Message}";
		}
	}

	[Trace("BrowserPlugin > ScrollAsync")]
	[KernelFunction("scroll")]
	[Description("Scroll the page up or down.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> ScrollAsync(
		[Description("Direction: 'down' or 'up'")] string direction = "down",
		CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			await client.ActionAsync(tabId, new { kind = "scroll", direction }, ct);
			return $"Scrolled {direction}.";
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Scroll error: {ex.Message}";
		}
	}

	[Trace("BrowserPlugin > ScreenshotAsync")]
	[KernelFunction("screenshot")]
	[Description("Take a visual screenshot of the current page. Use browser-snapshot for text content instead — screenshots cost more tokens.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> ScreenshotAsync(CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			byte[] bytes = await client.ScreenshotAsync(tabId, ct);
			return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Screenshot error: {ex.Message}";
		}
	}

	[Trace("BrowserPlugin > EvalAsync")]
	[KernelFunction("eval")]
	[Description("Execute JavaScript in the page context and return the result. Useful for extracting data or interacting with page APIs.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> EvalAsync(
		[Description("JavaScript expression to execute")] string code,
		CancellationToken ct = default) {
		await ToolContext.NotifyAsync("🔧 Evaluating JS", ct);

		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			return await client.EvaluateAsync(tabId, code, ct);
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Evaluate error: {ex.Message}";
		}
	}

	[Trace("BrowserPlugin > TabsAsync")]
	[KernelFunction("tabs")]
	[Description("List all open browser tabs with their titles and URLs.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> TabsAsync(CancellationToken ct = default) {
		try {
			(AgentBrowserSession session, _) = await this.GetTabOrThrowAsync();
			return await client.ListTabsAsync(session.InstanceId, ct);
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Tabs error: {ex.Message}";
		}
	}

	[Trace("BrowserPlugin > HandoffAsync")]
	[KernelFunction("handoff")]
	[Description("Hand the browser to the user for manual interaction (login, captcha, 2FA). Requires a headed session (browser-navigate with headed=true). The window stays open after your reply; the user's next message returns control to you.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> HandoffAsync(CancellationToken ct = default) {
		await ToolContext.NotifyAsync("🤝 Handing browser to you", ct);

		try {
			long agentId = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");
			return instances.MarkHandoff(agentId);
		} catch (Exception ex) {
			return $"Handoff error: {ex.Message}";
		}
	}

	private Task<(AgentBrowserSession Session, string TabId)> GetTabOrThrowAsync() {
		long agentId = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		AgentBrowserSession? session = instances.GetSession(agentId);
		if (session?.TabId is null)
			throw new InvalidOperationException("No page open. Use browser-navigate first.");

		return Task.FromResult((session, session.TabId));
	}
}
