namespace BlockCircuit;

/// <summary>Stable, case-sensitive content identity used on disk and at API boundaries.</summary>
public readonly record struct ContentId
{
    public string Value { get; }
    public ContentId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int colon = value.IndexOf(':');
        if (colon <= 0 || colon != value.LastIndexOf(':') || colon == value.Length - 1 ||
            value[..colon].Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-' or '.')) ||
            value[(colon + 1)..].Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-' or '.' or '/')))
            throw new ArgumentException("Expected a lowercase namespace:path identifier.", nameof(value));
        Value = value;
    }
    public override string ToString() => Value ?? throw new InvalidOperationException("Uninitialized content identifier.");
}
