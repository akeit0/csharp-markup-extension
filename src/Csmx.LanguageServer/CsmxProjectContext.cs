using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Csmx.Compiler;

namespace Csmx.LanguageServer;

internal sealed record CsmxProjectDependency(
    string Path,
    bool Exists,
    DateTime LastWriteUtc,
    long? LastWriteUtcMilliseconds
)
{
    public static CsmxProjectDependency Snapshot(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return new CsmxProjectDependency(fullPath, false, DateTime.MinValue, null);
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
        return new CsmxProjectDependency(
            fullPath,
            true,
            lastWriteUtc,
            new DateTimeOffset(lastWriteUtc).ToUnixTimeMilliseconds()
        );
    }

    public bool IsCurrent()
    {
        var exists = File.Exists(Path) || Directory.Exists(Path);
        if (exists != Exists)
        {
            return false;
        }

        if (!exists)
        {
            return true;
        }

        return File.GetLastWriteTimeUtc(Path) == LastWriteUtc;
    }
}

internal sealed record CsmxProjectContextOptions(string? Configuration, string? TargetFramework)
{
    public static CsmxProjectContextOptions Default { get; } = new(null, null);

    public static CsmxProjectContextOptions Create(
        string? configuration,
        string? targetFramework
    ) => new(Normalize(configuration), Normalize(targetFramework));

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

internal sealed record CsmxProjectContext(
    string SourceFilePath,
    string ProjectFilePath,
    string ProjectDirectory,
    string RelativeSourcePath,
    string GeneratedDirectory,
    string GeneratedFilePath,
    string Configuration,
    string TargetFramework,
    string EvaluationKind,
    IReadOnlyList<string> Messages,
    CsmxTransformOptions TransformOptions,
    IReadOnlyList<CsmxProjectDependency> CacheDependencies,
    bool? CompileIncludesGeneratedFile = null,
    int? CompileItemCount = null
)
{
    private const int MsBuildTimeoutMilliseconds = 6000;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CachedContext> ContextCache = new(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly string[] ProjectProperties =
    [
        "MSBuildProjectDirectory",
        "MSBuildAllProjects",
        "Configuration",
        "TargetFramework",
        "TargetFrameworks",
        "BaseIntermediateOutputPath",
        "IntermediateOutputPath",
        "MSBuildProjectExtensionsPath",
        "RootNamespace",
        "CsmxDefaultGeneratedRootDir",
        "CsmxGeneratedRootDir",
        "CsmxGeneratedDir",
        "CsmxCompileMode",
        "CsmxElementFactory",
        "CsmxAttributeFactory",
        "CsmxTextFactory",
        "CsmxFormattedTextChild",
        "CsmxAlignedFormattedTextChild",
        "CsmxPropsFactory",
        "CsmxChildrenFactory",
        "CsmxChildSequenceFactory",
        "CsmxKeyedSequenceElement",
        "CsmxKeyedSequenceItemsAttribute",
        "CsmxKeyedSequenceKeyAttribute",
        "CsmxKeyedSequenceTemplate",
        "CsmxComponentNames",
        "CsmxComponentLowering",
        "CsmxComponentTemplate",
        "CsmxFluentCreate",
        "CsmxFluentAttribute",
        "CsmxFluentTextChild",
        "CsmxFluentExpressionChild",
        "CsmxFluentFormattedExpressionChild",
        "CsmxFluentElementChild",
        "CsmxFluentComponentTemplate",
    ];

    public static CsmxProjectContext? TryCreate(
        string? sourcePath,
        CsmxProjectContextOptions? options = null
    ) => Create(sourcePath, includeCompileItems: false, useCache: true, NormalizeOptions(options));

    public static CsmxProjectContext? Inspect(
        string? sourcePath,
        CsmxProjectContextOptions? options = null
    ) => Create(sourcePath, includeCompileItems: true, useCache: false, NormalizeOptions(options));

    public static int ClearCache(string? sourcePath = null)
    {
        lock (CacheLock)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                var count = ContextCache.Count;
                ContextCache.Clear();
                return count;
            }

            var fullSourcePath = Path.GetFullPath(sourcePath);
            return ContextCache.Remove(fullSourcePath) ? 1 : 0;
        }
    }

    private static CsmxProjectContext? Create(
        string? sourcePath,
        bool includeCompileItems,
        bool useCache,
        CsmxProjectContextOptions options
    )
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var projectFilePath = FindNearestProject(fullSourcePath);
        if (projectFilePath is null)
        {
            return null;
        }

        if (useCache && TryGetCached(fullSourcePath, projectFilePath, options, out var cached))
        {
            return cached;
        }

        var projectDirectory = Path.GetDirectoryName(projectFilePath);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var messages = new List<string>();
        var evaluation = TryEvaluateProject(
            projectFilePath,
            includeCompileItems,
            options,
            messages
        );
        var properties = evaluation?.Properties;
        var evaluationKind = evaluation?.EvaluationKind ?? "msbuild";
        if (properties is null)
        {
            properties = ReadProjectProperties(projectFilePath);
            evaluationKind = "xml-fallback";
            messages.Add("Using XML fallback project evaluation.");
        }

        properties["MSBuildProjectDirectory"] = projectDirectory;
        properties["MSBuildThisFileDirectory"] = EnsureTrailingSeparator(projectDirectory);
        properties.TryAdd("Configuration", "Debug");
        properties.TryAdd("BaseIntermediateOutputPath", "obj\\");
        properties.TryAdd(
            "CsmxDefaultGeneratedRootDir",
            Path.Combine(projectDirectory, "Generated", "Csmx") + Path.DirectorySeparatorChar
        );
        properties.TryAdd(
            "CsmxGeneratedRootDir",
            Path.Combine(projectDirectory, "Generated", "Csmx") + Path.DirectorySeparatorChar
        );
        ApplyProjectContextOptions(properties, options);

        var targetFramework = GetTargetFramework(properties, options);
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            properties["TargetFramework"] = targetFramework;
        }

        var generatedDirectory = GetGeneratedDirectory(projectDirectory, properties);
        var relativeSourcePath = Path.GetRelativePath(projectDirectory, fullSourcePath);
        var generatedFilePath = GetGeneratedFilePath(generatedDirectory, relativeSourcePath);
        var fullGeneratedFilePath = Path.GetFullPath(generatedFilePath);
        var cacheDependencies = GetCacheDependencies(projectFilePath, projectDirectory, properties);
        var context = new CsmxProjectContext(
            fullSourcePath,
            projectFilePath,
            projectDirectory,
            relativeSourcePath,
            Path.GetFullPath(generatedDirectory),
            fullGeneratedFilePath,
            GetPropertyOrDefault(properties, "Configuration", "Debug"),
            targetFramework,
            evaluationKind,
            messages,
            CreateTransformOptions(properties, relativeSourcePath),
            cacheDependencies,
            includeCompileItems
                ? GetCompileIncludesGeneratedFile(evaluation, fullGeneratedFilePath)
                : null,
            includeCompileItems ? evaluation?.CompileItemCount : null
        );

        if (useCache)
        {
            SetCached(fullSourcePath, projectFilePath, options, context);
        }

        return context;
    }

    private static bool TryGetCached(
        string fullSourcePath,
        string projectFilePath,
        CsmxProjectContextOptions options,
        out CsmxProjectContext? context
    )
    {
        lock (CacheLock)
        {
            if (
                ContextCache.TryGetValue(fullSourcePath, out var cached)
                && string.Equals(
                    cached.ProjectFilePath,
                    projectFilePath,
                    StringComparison.OrdinalIgnoreCase
                )
                && cached.Options == options
                && cached.Dependencies.All(dependency => dependency.IsCurrent())
            )
            {
                context = cached.Context;
                return true;
            }
        }

        context = null;
        return false;
    }

    private static void SetCached(
        string fullSourcePath,
        string projectFilePath,
        CsmxProjectContextOptions options,
        CsmxProjectContext context
    )
    {
        lock (CacheLock)
        {
            ContextCache[fullSourcePath] = new CachedContext(
                projectFilePath,
                options,
                context.CacheDependencies,
                context
            );
        }
    }

    private static ProjectEvaluation? TryEvaluateProject(
        string projectFilePath,
        bool includeCompileItems,
        CsmxProjectContextOptions options,
        List<string> messages
    )
    {
        using var document = RunMsBuildQuery(
            projectFilePath,
            includeCompileItems,
            options,
            messages
        );
        if (document is null)
        {
            return null;
        }

        var properties = ReadMsBuildProperties(document.RootElement);
        IReadOnlyList<string> compileItems;
        if (
            ShouldReevaluateForInferredTargetFramework(properties, options, out var targetFramework)
        )
        {
            var targetOptions = CsmxProjectContextOptions.Create(
                options.Configuration,
                targetFramework
            );
            using var targetDocument = RunMsBuildQuery(
                projectFilePath,
                includeCompileItems,
                targetOptions,
                messages
            );
            if (targetDocument is not null)
            {
                compileItems = ReadMsBuildCompileItems(
                    targetDocument.RootElement,
                    includeCompileItems
                );
                properties = ReadMsBuildProperties(targetDocument.RootElement);
                options = targetOptions;
            }
            else
            {
                messages.Add(
                    $"Using outer-build MSBuild evaluation because inner-build evaluation for TargetFramework '{targetFramework}' failed."
                );
                compileItems = ReadMsBuildCompileItems(document.RootElement, includeCompileItems);
            }
        }
        else
        {
            compileItems = ReadMsBuildCompileItems(document.RootElement, includeCompileItems);
        }

        return new ProjectEvaluation(
            properties,
            compileItems,
            includeCompileItems ? compileItems.Count : null,
            "msbuild"
        );
    }

    private static Dictionary<string, string> ReadMsBuildProperties(JsonElement root)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (
            !root.TryGetProperty("Properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object
        )
        {
            return properties;
        }

        foreach (var property in propertiesElement.EnumerateObject())
        {
            properties[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return properties;
    }

    private static IReadOnlyList<string> ReadMsBuildCompileItems(
        JsonElement root,
        bool includeCompileItems
    )
    {
        if (
            !includeCompileItems
            || !root.TryGetProperty("Items", out var itemsElement)
            || !itemsElement.TryGetProperty("Compile", out var compileElement)
            || compileElement.ValueKind != JsonValueKind.Array
        )
        {
            return Array.Empty<string>();
        }

        return compileElement
            .EnumerateArray()
            .Select(item =>
                item.TryGetProperty("FullPath", out var fullPath)
                    ? fullPath.GetString() ?? string.Empty
                    : string.Empty
            )
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static bool ShouldReevaluateForInferredTargetFramework(
        IReadOnlyDictionary<string, string> properties,
        CsmxProjectContextOptions options,
        out string targetFramework
    )
    {
        targetFramework = string.Empty;
        if (!string.IsNullOrWhiteSpace(options.TargetFramework))
        {
            return false;
        }

        if (TryGetProperty(properties, "TargetFramework", out _))
        {
            return false;
        }

        targetFramework = GetTargetFramework(properties);
        return !string.IsNullOrWhiteSpace(targetFramework);
    }

    private static JsonDocument? RunMsBuildQuery(
        string projectFilePath,
        bool includeCompileItems,
        CsmxProjectContextOptions options,
        List<string> messages
    )
    {
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(projectFilePath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("msbuild");
            startInfo.ArgumentList.Add(projectFilePath);
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-p:DesignTimeBuild=true");
            startInfo.ArgumentList.Add("-p:SkipCompilerExecution=true");
            if (!string.IsNullOrWhiteSpace(options.Configuration))
            {
                startInfo.ArgumentList.Add("-p:Configuration=" + options.Configuration);
            }

            if (!string.IsNullOrWhiteSpace(options.TargetFramework))
            {
                startInfo.ArgumentList.Add("-p:TargetFramework=" + options.TargetFramework);
            }

            startInfo.ArgumentList.Add("-getProperty:" + string.Join(",", ProjectProperties));
            if (includeCompileItems)
            {
                startInfo.ArgumentList.Add("-getItem:Compile");
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                messages.Add("Could not start dotnet msbuild.");
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(MsBuildTimeoutMilliseconds))
            {
                TryKill(process);
                messages.Add($"dotnet msbuild timed out after {MsBuildTimeoutMilliseconds} ms.");
                return null;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                messages.Add($"dotnet msbuild exited with code {process.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    messages.Add(stderr.Trim());
                }

                return null;
            }

            var json = ExtractJsonObject(stdout);
            if (string.IsNullOrWhiteSpace(json))
            {
                messages.Add("dotnet msbuild did not return JSON output.");
                return null;
            }

            return JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            messages.Add($"dotnet msbuild evaluation failed: {ex.Message}");
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup after MSBuild timeout.
        }
    }

    private static string ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        return start >= 0 && end >= start ? output[start..(end + 1)] : string.Empty;
    }

    private static string? FindNearestProject(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string[] projects;
            try
            {
                projects = Directory.GetFiles(directory, "*.csproj");
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            if (projects.Length == 1)
            {
                return projects[0];
            }

            if (projects.Length > 1)
            {
                return projects
                    .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
                    .First();
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    private static Dictionary<string, string> ReadProjectProperties(string projectFilePath)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
            foreach (
                var property in document
                    .Descendants()
                    .Where(element => element.Name.LocalName == "PropertyGroup")
                    .Elements()
            )
            {
                if (!string.IsNullOrWhiteSpace(property.Name.LocalName))
                {
                    properties[property.Name.LocalName] = property.Value.Trim();
                }
            }
        }
        catch
        {
            // Project context is an editor aid. Fall back to compiler defaults when it cannot be read.
        }

        return properties;
    }

    private static CsmxTransformOptions CreateTransformOptions(
        IReadOnlyDictionary<string, string> properties,
        string relativeSourcePath
    )
    {
        var options = CsmxTransformOptions.Default with
        {
            SourceIdentity = relativeSourcePath.Replace('\\', '/'),
        };

        if (
            TryGetProperty(properties, "CsmxCompileMode", out var compileModeValue)
            && CsmxSourceOptions.TryParseCompileMode(compileModeValue, out var compileMode)
        )
        {
            options = options with { CompileMode = compileMode };
        }

        options = WithFactory(
            properties,
            options,
            "CsmxElementFactory",
            value => options with { ElementFactory = value }
        );
        options = WithFactory(
            properties,
            options,
            "CsmxAttributeFactory",
            value => options with { AttributeFactory = value }
        );
        options = WithFactory(
            properties,
            options,
            "CsmxTextFactory",
            value => options with { TextFactory = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxFormattedTextChild",
            value => options with { FormattedTextChild = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxAlignedFormattedTextChild",
            value => options with { AlignedFormattedTextChild = value }
        );
        options = WithFactory(
            properties,
            options,
            "CsmxPropsFactory",
            value => options with { PropsFactory = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxChildrenFactory",
            value => options with { ChildrenFactory = value }
        );
        options = WithFactory(
            properties,
            options,
            "CsmxChildSequenceFactory",
            value => options with { ChildSequenceFactory = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxKeyedSequenceElement",
            value => options with { KeyedSequenceElement = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxKeyedSequenceItemsAttribute",
            value => options with { KeyedSequenceItemsAttribute = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxKeyedSequenceKeyAttribute",
            value => options with { KeyedSequenceKeyAttribute = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxKeyedSequenceTemplate",
            value => options with { KeyedSequenceTemplate = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxComponentNames",
            value => options with { ComponentNames = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxComponentTemplate",
            value => options with { ComponentTemplate = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxFluentCreate",
            value => options with { FluentCreate = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxFluentAttribute",
            value => options with { FluentAttribute = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxFluentTextChild",
            value => options with { FluentTextChild = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxFluentExpressionChild",
            value => options with { FluentExpressionChild = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxFluentFormattedExpressionChild",
            value => options with { FluentFormattedExpressionChild = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxFluentElementChild",
            value => options with { FluentElementChild = value }
        );
        options = WithTemplate(
            properties,
            options,
            "CsmxFluentComponentTemplate",
            value => options with { FluentComponentTemplate = value }
        );

        if (TryGetProperty(properties, "CsmxComponentLowering", out var componentLowering))
        {
            options = options with
            {
                ComponentLowering = componentLowering.Trim().ToLowerInvariant() switch
                {
                    "factory" => CsmxComponentLowering.FactoryCall,
                    "direct" => CsmxComponentLowering.DirectCall,
                    _ => options.ComponentLowering,
                },
            };
        }

        return options;
    }

    private static CsmxTransformOptions WithFactory(
        IReadOnlyDictionary<string, string> properties,
        CsmxTransformOptions options,
        string propertyName,
        Func<string, CsmxTransformOptions> apply
    )
    {
        return
            TryGetProperty(properties, propertyName, out var value)
            && CsmxSourceOptions.IsValidFactoryExpression(value)
            ? apply(value)
            : options;
    }

    private static CsmxTransformOptions WithTemplate(
        IReadOnlyDictionary<string, string> properties,
        CsmxTransformOptions options,
        string propertyName,
        Func<string, CsmxTransformOptions> apply
    ) => TryGetProperty(properties, propertyName, out var value) ? apply(value) : options;

    private static bool TryGetProperty(
        IReadOnlyDictionary<string, string> properties,
        string propertyName,
        out string value
    )
    {
        if (properties.TryGetValue(propertyName, out value!) && !string.IsNullOrWhiteSpace(value))
        {
            value = ExpandProperties(value.Trim(), properties);
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    private static string GetGeneratedDirectory(
        string projectDirectory,
        IReadOnlyDictionary<string, string> properties
    )
    {
        if (TryGetProperty(properties, "CsmxGeneratedDir", out var configuredGeneratedDirectory))
        {
            return Path.GetFullPath(
                Path.IsPathRooted(configuredGeneratedDirectory)
                    ? configuredGeneratedDirectory
                    : Path.Combine(projectDirectory, configuredGeneratedDirectory)
            );
        }

        var configuration = GetPropertyOrDefault(properties, "Configuration", "Debug");
        var targetFramework = GetTargetFramework(properties);
        var root = TryGetProperty(properties, "CsmxGeneratedRootDir", out var configuredRoot)
            ? configuredRoot
            : Path.Combine(projectDirectory, "Generated", "Csmx");
        var generatedDirectory = string.IsNullOrWhiteSpace(targetFramework)
            ? Path.Combine(root, configuration)
            : Path.Combine(root, configuration, targetFramework);

        return Path.GetFullPath(
            Path.IsPathRooted(generatedDirectory)
                ? generatedDirectory
                : Path.Combine(projectDirectory, generatedDirectory)
        );
    }

    private static string GetGeneratedFilePath(string generatedDirectory, string relativeSourcePath)
    {
        var relativeDirectory = Path.GetDirectoryName(relativeSourcePath) ?? string.Empty;
        var generatedFileName = Path.GetFileName(relativeSourcePath) + ".g.cs";
        return Path.Combine(generatedDirectory, relativeDirectory, generatedFileName);
    }

    private static IReadOnlyList<CsmxProjectDependency> GetCacheDependencies(
        string projectFilePath,
        string projectDirectory,
        IReadOnlyDictionary<string, string> properties
    )
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(projectFilePath),
        };

        AddDirectoryBuildCandidates(projectDirectory, paths);
        AddMsBuildAllProjects(projectDirectory, properties, paths);
        AddProjectAssetsPath(projectDirectory, properties, paths);

        return paths.Select(CsmxProjectDependency.Snapshot).ToArray();
    }

    private static void AddDirectoryBuildCandidates(string projectDirectory, ISet<string> paths)
    {
        var directory = Path.GetFullPath(projectDirectory);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            paths.Add(Path.Combine(directory, "Directory.Build.props"));
            paths.Add(Path.Combine(directory, "Directory.Build.targets"));
            var parent = Directory.GetParent(directory)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || PathsEqual(parent, directory))
            {
                return;
            }

            directory = parent;
        }
    }

    private static void AddMsBuildAllProjects(
        string projectDirectory,
        IReadOnlyDictionary<string, string> properties,
        ISet<string> paths
    )
    {
        if (!properties.TryGetValue("MSBuildAllProjects", out var allProjects))
        {
            return;
        }

        foreach (
            var path in allProjects.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            AddPath(projectDirectory, path, paths);
        }
    }

    private static void AddProjectAssetsPath(
        string projectDirectory,
        IReadOnlyDictionary<string, string> properties,
        ISet<string> paths
    )
    {
        var extensionsPath = GetRawPropertyOrDefault(
            properties,
            "MSBuildProjectExtensionsPath",
            GetRawPropertyOrDefault(properties, "BaseIntermediateOutputPath", "obj\\")
        );

        AddPath(projectDirectory, Path.Combine(extensionsPath, "project.assets.json"), paths);
    }

    private static string GetRawPropertyOrDefault(
        IReadOnlyDictionary<string, string> properties,
        string propertyName,
        string fallback
    ) =>
        properties.TryGetValue(propertyName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;

    private static void AddPath(string baseDirectory, string path, ISet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            paths.Add(
                Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path))
            );
        }
        catch
        {
            // Dependency tracking is best effort. Invalid evaluated paths should not break editing.
        }
    }

    private static string GetTargetFramework(IReadOnlyDictionary<string, string> properties) =>
        GetTargetFramework(properties, CsmxProjectContextOptions.Default);

    private static string GetTargetFramework(
        IReadOnlyDictionary<string, string> properties,
        CsmxProjectContextOptions options
    )
    {
        if (!string.IsNullOrWhiteSpace(options.TargetFramework))
        {
            return options.TargetFramework;
        }

        if (TryGetProperty(properties, "TargetFramework", out var targetFramework))
        {
            return targetFramework;
        }

        if (!TryGetProperty(properties, "TargetFrameworks", out var targetFrameworks))
        {
            return string.Empty;
        }

        return targetFrameworks
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault()
            ?? string.Empty;
    }

    private static string GetPropertyOrDefault(
        IReadOnlyDictionary<string, string> properties,
        string propertyName,
        string fallback
    ) => TryGetProperty(properties, propertyName, out var value) ? value : fallback;

    private static string ExpandProperties(
        string value,
        IReadOnlyDictionary<string, string> properties
    )
    {
        var expanded = value;
        for (var pass = 0; pass < 8; pass++)
        {
            var next = expanded;
            foreach (var property in properties)
            {
                next = next.Replace(
                    "$(" + property.Key + ")",
                    property.Value,
                    StringComparison.OrdinalIgnoreCase
                );
            }

            if (string.Equals(next, expanded, StringComparison.Ordinal))
            {
                return next;
            }

            expanded = next;
        }

        return expanded;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase
        );

    private static string EnsureTrailingSeparator(string value) =>
        value.EndsWith(Path.DirectorySeparatorChar)
        || value.EndsWith(Path.AltDirectorySeparatorChar)
            ? value
            : value + Path.DirectorySeparatorChar;

    private static CsmxProjectContextOptions NormalizeOptions(CsmxProjectContextOptions? options) =>
        options is null
            ? CsmxProjectContextOptions.Default
            : CsmxProjectContextOptions.Create(options.Configuration, options.TargetFramework);

    private static void ApplyProjectContextOptions(
        IDictionary<string, string> properties,
        CsmxProjectContextOptions options
    )
    {
        if (!string.IsNullOrWhiteSpace(options.Configuration))
        {
            properties["Configuration"] = options.Configuration;
        }

        if (!string.IsNullOrWhiteSpace(options.TargetFramework))
        {
            properties["TargetFramework"] = options.TargetFramework;
        }
    }

    private static bool GetCompileIncludesGeneratedFile(
        ProjectEvaluation? evaluation,
        string generatedFilePath
    ) => evaluation?.CompileItems.Any(item => PathsEqual(item, generatedFilePath)) == true;

    private sealed record ProjectEvaluation(
        Dictionary<string, string> Properties,
        IReadOnlyList<string> CompileItems,
        int? CompileItemCount,
        string EvaluationKind
    );

    private sealed record CachedContext(
        string ProjectFilePath,
        CsmxProjectContextOptions Options,
        IReadOnlyList<CsmxProjectDependency> Dependencies,
        CsmxProjectContext Context
    );
}
