using System.Text.RegularExpressions;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lis.Tools;

public sealed record ParsedSkill {
	public required string Name        { get; init; }
	public required string Description { get; init; }
	public          int    Version     { get; init; } = 1;
	public required string Content     { get; init; }
}

public sealed record SkillParseResult {
	public bool         IsSuccess { get; init; }
	public ParsedSkill? Skill     { get; init; }
	public string?      Error     { get; init; }

	public static SkillParseResult Success(ParsedSkill skill) => new() { IsSuccess = true, Skill = skill };
	public static SkillParseResult Failure(string error)      => new() { IsSuccess = false, Error = error };
}

public static partial class SkillParser {

	private const int MaxNameLength        = 50;
	private const int MaxDescriptionLength = 500;
	private const int MaxContentLength     = 100_000;

	private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
		.WithNamingConvention(UnderscoredNamingConvention.Instance)
		.Build();

	public static ParsedSkill Parse(string raw) {
		SkillParseResult result = TryParse(raw);
		if (!result.IsSuccess)
			throw new FormatException(result.Error);
		return result.Skill!;
	}

	public static SkillParseResult TryParse(string raw) {
		if (string.IsNullOrWhiteSpace(raw))
			return SkillParseResult.Failure("Skill file is empty.");

		string text = raw.TrimStart('﻿');

		if (!text.StartsWith("---"))
			return SkillParseResult.Failure("Missing front matter delimiters — expected '---' at the start of the file.");

		int closingIndex = text.IndexOf("\n---", 3, StringComparison.Ordinal);
		if (closingIndex < 0)
			return SkillParseResult.Failure("Missing closing front matter delimiter '---'.");

		string yamlBlock = text[3..closingIndex].Trim();
		string body      = text[(closingIndex + 4)..].Trim();

		Dictionary<string, object> frontMatter;
		try {
			frontMatter = YamlDeserializer.Deserialize<Dictionary<string, object>>(yamlBlock)
						  ?? [];
		}
		catch {
			return SkillParseResult.Failure("Invalid YAML in front matter.");
		}

		if (!frontMatter.TryGetValue("name", out object? nameObj) || nameObj is null)
			return SkillParseResult.Failure("Required field 'name' is missing from front matter.");

		string name = nameObj.ToString()!.Trim();

		if (!frontMatter.TryGetValue("description", out object? descObj) || descObj is null)
			return SkillParseResult.Failure("Required field 'description' is missing from front matter.");

		string description = descObj.ToString()!.Trim();

		if (name.Length < 2 || name.Length > MaxNameLength || !NamePattern().IsMatch(name))
			return SkillParseResult.Failure(
				$"Invalid name '{name}' — must be 2–{MaxNameLength} lowercase alphanumeric characters or hyphens, "
				+ "starting and ending with an alphanumeric character.");

		if (description.Length == 0 || description.Length > MaxDescriptionLength)
			return SkillParseResult.Failure(
				$"Description must be between 1 and {MaxDescriptionLength} characters.");

		if (body.Length == 0)
			return SkillParseResult.Failure("Skill content is required — the body after front matter is empty.");

		if (body.Length > MaxContentLength)
			return SkillParseResult.Failure(
				$"Skill content exceeds the maximum of {MaxContentLength:N0} characters ({body.Length:N0} provided).");

		int version = 1;
		if (frontMatter.TryGetValue("version", out object? verObj) && verObj is not null) {
			string verStr = verObj.ToString()!;
			if (!int.TryParse(verStr, out version) || version < 1)
				return SkillParseResult.Failure("Version must be a positive integer.");
		}

		return SkillParseResult.Success(new ParsedSkill {
			Name        = name,
			Description = description,
			Version     = version,
			Content     = body,
		});
	}

	[GeneratedRegex(@"^[a-z0-9][a-z0-9-]*[a-z0-9]$", RegexOptions.NonBacktracking)]
	private static partial Regex NamePattern();
}
