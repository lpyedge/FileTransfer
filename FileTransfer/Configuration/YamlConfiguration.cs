using YamlDotNet.RepresentationModel;

internal static class YamlConfigurationExtensions
{
    public static IConfigurationBuilder AddYamlFile(
        this IConfigurationBuilder builder,
        string path,
        bool optional,
        bool reloadOnChange)
    {
        return builder.AddYamlFile((IFileProvider?)null, path, optional, reloadOnChange);
    }

    public static IConfigurationBuilder AddYamlFile(
        this IConfigurationBuilder builder,
        IFileProvider? provider,
        string path,
        bool optional,
        bool reloadOnChange)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return builder.Add(new YamlConfigurationSource
        {
            FileProvider = provider,
            Path = path,
            Optional = optional,
            ReloadOnChange = reloadOnChange
        });
    }
}

internal sealed class YamlConfigurationSource : FileConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new YamlConfigurationProvider(this);
    }
}

internal sealed class YamlConfigurationProvider : FileConfigurationProvider
{
    public YamlConfigurationProvider(YamlConfigurationSource source)
        : base(source)
    {
    }

    public override void Load(Stream stream)
    {
        Data = YamlConfigurationParser.Parse(stream);
    }
}

internal static class YamlConfigurationParser
{
    public static IDictionary<string, string?> Parse(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var yaml = new YamlStream();
        yaml.Load(reader);

        var data = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is null)
        {
            return data;
        }

        Visit(yaml.Documents[0].RootNode, parentPath: null, data);
        return data;
    }

    private static void Visit(YamlNode node, string? parentPath, IDictionary<string, string?> data)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                VisitMapping(mapping, parentPath, data);
                break;
            case YamlSequenceNode sequence:
                VisitSequence(sequence, parentPath, data);
                break;
            case YamlScalarNode scalar:
                if (!string.IsNullOrEmpty(parentPath))
                {
                    data[parentPath] = scalar.Value;
                }
                break;
        }
    }

    private static void VisitMapping(YamlMappingNode mapping, string? parentPath, IDictionary<string, string?> data)
    {
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode keyScalar || string.IsNullOrWhiteSpace(keyScalar.Value))
            {
                continue;
            }

            var path = string.IsNullOrEmpty(parentPath) ? keyScalar.Value.Trim() : parentPath + ConfigurationPath.KeyDelimiter + keyScalar.Value.Trim();
            Visit(valueNode, path, data);
        }
    }

    private static void VisitSequence(YamlSequenceNode sequence, string? parentPath, IDictionary<string, string?> data)
    {
        if (string.IsNullOrEmpty(parentPath))
        {
            return;
        }

        for (var i = 0; i < sequence.Children.Count; i++)
        {
            Visit(sequence.Children[i], parentPath + ConfigurationPath.KeyDelimiter + i.ToString(CultureInfo.InvariantCulture), data);
        }
    }
}
