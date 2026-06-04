[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
internal sealed class ReadySignalOptions
{
    public ReadySignalMode Mode { get; set; } = ReadySignalMode.StableSize;
    public int StableChecks { get; set; } = 3;
    public int IntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 300000;
    public bool RequireReadable { get; set; } = true;
    public string DoneFileSuffix { get; set; } = ".done";

    public ReadySignalOptions Clone() => new()
    {
        Mode = Mode,
        StableChecks = StableChecks,
        IntervalMs = IntervalMs,
        TimeoutMs = TimeoutMs,
        RequireReadable = RequireReadable,
        DoneFileSuffix = DoneFileSuffix
    };

    public void ApplyDefaults()
    {
        StableChecks = Math.Max(1, StableChecks);
        IntervalMs = Math.Max(100, IntervalMs);
        TimeoutMs = Math.Max(IntervalMs, TimeoutMs);
        DoneFileSuffix = string.IsNullOrWhiteSpace(DoneFileSuffix) ? ".done" : DoneFileSuffix.Trim();
    }

    public static ReadySignalOptions FromConfiguration(IConfigurationSection section)
    {
        var options = new ReadySignalOptions();
        if (!section.Exists())
        {
            return options;
        }

        if (Enum.TryParse<ReadySignalMode>(section[nameof(Mode)], true, out var mode))
        {
            options.Mode = mode;
        }

        ReadInt(section, nameof(StableChecks), value => options.StableChecks = value);
        ReadInt(section, nameof(IntervalMs), value => options.IntervalMs = value);
        ReadInt(section, nameof(TimeoutMs), value => options.TimeoutMs = value);
        if (bool.TryParse(section[nameof(RequireReadable)], out var requireReadable))
        {
            options.RequireReadable = requireReadable;
        }

        options.DoneFileSuffix = section[nameof(DoneFileSuffix)]?.Trim() ?? options.DoneFileSuffix;
        options.ApplyDefaults();
        return options;
    }

    private static void ReadInt(IConfiguration section, string name, Action<int> apply)
    {
        if (int.TryParse(section[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            apply(value);
        }
    }
}

internal enum ReadySignalMode
{
    StableSize,
    RenameOnly,
    DoneFile
}
