using System.Xml.Linq;
using Csmx.Compiler;
using Microsoft.CodeAnalysis;

namespace Csmx.LanguageServer;

internal static class CsmxRoslynProjectLoader
{
    public static CsmxRoslynProject Load(CsmxProjectContext context)
    {
        var projectDirectory = context.ProjectDirectory;
        var sourceBackedProjectReferences = EnumerateDirectProjectReferences(
                context.ProjectFilePath
            )
            .ToArray();
        var references = GetMetadataReferences(context, sourceBackedProjectReferences).ToArray();
        var sources = new List<CsmxRoslynSource>();
        var dependencies = context.CacheDependencies.ToList();

        sources.Add(
            new CsmxRoslynSource(
                Path.Combine(projectDirectory, "obj", "csmx.sdk.globalusings.g.cs"),
                """
                global using System;
                global using System.Collections.Generic;
                global using System.IO;
                global using System.Linq;
                global using System.Net.Http;
                global using System.Threading;
                global using System.Threading.Tasks;
                """
            )
        );

        AddProjectSources(sources, dependencies, projectDirectory, context.TransformOptions);

        foreach (var projectReference in sourceBackedProjectReferences)
        {
            AddProjectReferenceSources(sources, dependencies, projectReference, context);
        }

        return new CsmxRoslynProject(
            GetAssemblyName(context.ProjectFilePath),
            sources,
            references,
            dependencies
        );
    }

    private static void AddProjectSources(
        List<CsmxRoslynSource> sources,
        List<CsmxProjectDependency> dependencies,
        string projectDirectory,
        CsmxTransformOptions transformOptions
    )
    {
        foreach (var path in EnumerateProjectFiles(projectDirectory, "*.cs"))
        {
            if (!IsGeneratedOrIntermediate(path, projectDirectory))
            {
                AddSource(sources, dependencies, path, File.ReadAllText(path));
            }
        }

        foreach (var path in EnumerateProjectFiles(projectDirectory, "*.csmx"))
        {
            var fullPath = Path.GetFullPath(path);
            var relative = GetRelativePath(projectDirectory, fullPath).Replace('\\', '/');
            var options = transformOptions with { SourceIdentity = relative };
            var transform = CsmxTransformer.Transform(
                File.ReadAllText(fullPath),
                fullPath,
                options
            );
            AddSource(sources, dependencies, fullPath, transform.Code);
        }
    }

    private static void AddProjectReferenceSources(
        List<CsmxRoslynSource> sources,
        List<CsmxProjectDependency> dependencies,
        string projectFilePath,
        CsmxProjectContext parentContext
    )
    {
        var projectDirectory = Path.GetDirectoryName(projectFilePath);
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
        {
            return;
        }

        dependencies.Add(CsmxProjectDependency.Snapshot(projectFilePath));
        foreach (var path in EnumerateProjectFiles(projectDirectory, "*.cs"))
        {
            if (!IsGeneratedOrIntermediate(path, projectDirectory))
            {
                AddSource(sources, dependencies, path, File.ReadAllText(path));
            }
        }

        foreach (var path in EnumerateProjectFiles(projectDirectory, "*.csmx"))
        {
            var fullPath = Path.GetFullPath(path);
            if (IsGeneratedOrIntermediate(fullPath, projectDirectory))
            {
                continue;
            }

            var referenceContext = CsmxProjectContext.TryCreate(
                fullPath,
                CsmxProjectContextOptions.Create(
                    parentContext.Configuration,
                    parentContext.TargetFramework
                )
            );
            if (referenceContext is not null)
            {
                dependencies.AddRange(referenceContext.CacheDependencies);
            }

            var referenceProjectDirectory = referenceContext?.ProjectDirectory ?? projectDirectory;
            var relative = GetRelativePath(referenceProjectDirectory, fullPath).Replace('\\', '/');
            var options = (referenceContext?.TransformOptions ?? CsmxTransformOptions.Default) with
            {
                SourceIdentity = relative,
            };
            var transform = CsmxTransformer.Transform(
                File.ReadAllText(fullPath),
                fullPath,
                options
            );
            AddSource(sources, dependencies, fullPath, transform.Code);
        }
    }

    private static void AddSource(
        List<CsmxRoslynSource> sources,
        List<CsmxProjectDependency> dependencies,
        string path,
        string text
    )
    {
        var fullPath = Path.GetFullPath(path);
        sources.Add(new CsmxRoslynSource(fullPath, text));
        dependencies.Add(CsmxProjectDependency.Snapshot(fullPath));
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences(
        CsmxProjectContext context,
        IReadOnlyCollection<string> sourceBackedProjectReferences
    )
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(tpa))
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (File.Exists(path) && seen.Add(path))
                {
                    yield return MetadataReference.CreateFromFile(path);
                }
            }
        }

        foreach (
            var path in GetProjectReferenceOutputs(
                context.ProjectFilePath,
                context.Configuration,
                sourceBackedProjectReferences
            )
        )
        {
            if (File.Exists(path) && seen.Add(path))
            {
                yield return MetadataReference.CreateFromFile(path);
            }
        }
    }

    private static IEnumerable<string> GetProjectReferenceOutputs(
        string projectFilePath,
        string configuration,
        IReadOnlyCollection<string> sourceBackedProjectReferences
    )
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceBacked = new HashSet<string>(
            sourceBackedProjectReferences.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var projectReference in EnumerateProjectReferences(projectFilePath, visited))
        {
            if (sourceBacked.Contains(Path.GetFullPath(projectReference)))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(projectReference);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var targetFramework = ReadSingleProperty(projectReference, "TargetFramework");
            if (string.IsNullOrWhiteSpace(targetFramework))
            {
                targetFramework = ReadSingleProperty(projectReference, "TargetFrameworks")
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()
                    ?.Trim();
            }

            if (string.IsNullOrWhiteSpace(targetFramework))
            {
                continue;
            }

            var assemblyName = ReadSingleProperty(projectReference, "AssemblyName");
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                assemblyName = Path.GetFileNameWithoutExtension(projectReference);
            }

            yield return Path.Combine(
                directory,
                "bin",
                configuration,
                targetFramework,
                assemblyName + ".dll"
            );
        }
    }

    private static IEnumerable<string> EnumerateProjectReferences(
        string projectFilePath,
        HashSet<string> visited
    )
    {
        var fullProjectPath = Path.GetFullPath(projectFilePath);
        if (!visited.Add(fullProjectPath))
        {
            yield break;
        }

        var projectDirectory = Path.GetDirectoryName(fullProjectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            yield break;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(fullProjectPath);
        }
        catch
        {
            yield break;
        }

        foreach (
            var include in document
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
        )
        {
            var referenced = Path.GetFullPath(Path.Combine(projectDirectory, include!));
            yield return referenced;
            foreach (var transitive in EnumerateProjectReferences(referenced, visited))
            {
                yield return transitive;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectProjectReferences(string projectFilePath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            yield break;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath);
        }
        catch
        {
            yield break;
        }

        foreach (
            var include in document
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
        )
        {
            yield return Path.GetFullPath(Path.Combine(projectDirectory, include!));
        }
    }

    private static string ReadSingleProperty(string projectFilePath, string propertyName)
    {
        try
        {
            var document = XDocument.Load(projectFilePath);
            return document
                    .Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == propertyName)
                    ?.Value.Trim()
                ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> EnumerateProjectFiles(string directory, string pattern)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsGeneratedOrIntermediate(string path, string projectDirectory)
    {
        var relative = GetRelativePath(projectDirectory, path);
        return relative.StartsWith(
                "bin" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
            || relative.StartsWith(
                "obj" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
            || relative.StartsWith(
                "Generated" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static string GetAssemblyName(string projectFilePath)
    {
        var assemblyName = ReadSingleProperty(projectFilePath, "AssemblyName");
        return string.IsNullOrWhiteSpace(assemblyName)
            ? Path.GetFileNameWithoutExtension(projectFilePath)
            : assemblyName;
    }

    private static string GetRelativePath(string relativeTo, string path)
    {
        var root = Path.GetFullPath(relativeTo);
        if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            root += Path.DirectorySeparatorChar;
        }

        var relativeUri = new Uri(root).MakeRelativeUri(new Uri(Path.GetFullPath(path)));
        return Uri.UnescapeDataString(relativeUri.ToString())
            .Replace('/', Path.DirectorySeparatorChar);
    }
}

internal sealed record CsmxRoslynSource(string Path, string Text);

internal sealed record CsmxRoslynProject(
    string AssemblyName,
    IReadOnlyList<CsmxRoslynSource> Sources,
    IReadOnlyList<MetadataReference> References,
    IReadOnlyList<CsmxProjectDependency> Dependencies
);
