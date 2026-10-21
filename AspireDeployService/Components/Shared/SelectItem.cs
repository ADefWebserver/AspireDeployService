namespace AspireDeployService.Components.Shared;

/// <summary>One option of a <see cref="SearchSelect"/>.</summary>
/// <param name="Detail">A muted second part, for example a subscription id or a region name.</param>
/// <param name="Badge">A short warning shown on the right, for example "Disabled".</param>
/// <param name="Group">Options with the same group are listed under one heading, in the order given.</param>
public sealed record SelectItem(string Value, string Text, string? Detail = null, bool Disabled = false, string? Group = null,
    string? Badge = null);
