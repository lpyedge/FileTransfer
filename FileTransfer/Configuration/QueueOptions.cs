[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
internal sealed class QueueOptions
{
    public int CopyCapacity { get; set; } = 10000;
    public int DeleteCapacity { get; set; } = 5000;

    public QueueOptions Clone() => new()
    {
        CopyCapacity = CopyCapacity,
        DeleteCapacity = DeleteCapacity
    };

    public void ApplyDefaults()
    {
        CopyCapacity = Math.Max(1, CopyCapacity);
        DeleteCapacity = Math.Max(1, DeleteCapacity);
    }

    public static QueueOptions FromConfiguration(IConfigurationSection section)
    {
        var options = new QueueOptions();
        if (!section.Exists())
        {
            return options;
        }

        ReadInt(section, nameof(CopyCapacity), value => options.CopyCapacity = value);
        ReadInt(section, nameof(DeleteCapacity), value => options.DeleteCapacity = value);
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
