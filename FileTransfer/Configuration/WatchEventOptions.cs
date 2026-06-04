[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
internal sealed class WatchEventOptions
{
    public bool Created { get; set; } = true;
    public bool Changed { get; set; } = true;
    public bool Deleted { get; set; }

    public WatchEventOptions Clone() => new()
    {
        Created = Created,
        Changed = Changed,
        Deleted = Deleted
    };

    public static WatchEventOptions FromConfiguration(IConfigurationSection section)
    {
        var options = new WatchEventOptions();
        ReadBool(section, nameof(Created), value => options.Created = value);
        ReadBool(section, nameof(Changed), value => options.Changed = value);
        ReadBool(section, nameof(Deleted), value => options.Deleted = value);
        return options;
    }

    private static void ReadBool(IConfiguration section, string name, Action<bool> apply)
    {
        if (bool.TryParse(section[name], out var value))
        {
            apply(value);
        }
    }
}
