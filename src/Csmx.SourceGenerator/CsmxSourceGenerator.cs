using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Csmx.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Csmx.SourceGenerator;

[Generator]
public sealed class CsmxSourceGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor TransformError = new(
        id: "CSMX0001",
        title: "CSMX transform error",
        messageFormat: "{0}",
        category: "CSMX",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor TransformWarning = new(
        id: "CSMX1001",
        title: "CSMX transform warning",
        messageFormat: "{0}",
        category: "CSMX",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var options = context.AnalyzerConfigOptionsProvider.Select(
            static (provider, _) => CsmxGeneratorOptions.From(provider.GlobalOptions)
        );

        var sourceFiles = context.AdditionalTextsProvider.Where(static file =>
            file.Path.EndsWith(".csmx", StringComparison.OrdinalIgnoreCase)
        );

        var inputs = sourceFiles.Combine(options);
        context.RegisterSourceOutput(
            inputs,
            static (sourceContext, input) =>
            {
                var (file, generatorOptions) = input;
                Execute(sourceContext, file, generatorOptions);
            }
        );
    }

    private static void Execute(
        SourceProductionContext context,
        AdditionalText file,
        CsmxGeneratorOptions generatorOptions
    )
    {
        var sourceText = file.GetText(context.CancellationToken);
        if (sourceText is null)
        {
            return;
        }

        var source = sourceText.ToString();
        var sourcePath = Path.GetFullPath(file.Path);
        var transform = CsmxTransformer.Transform(
            source,
            sourcePath,
            generatorOptions.CreateTransformOptions(sourcePath)
        );

        foreach (var diagnostic in transform.Diagnostics)
        {
            context.ReportDiagnostic(ToRoslynDiagnostic(file.Path, sourceText, diagnostic));
        }

        if (
            transform.Diagnostics.Any(static diagnostic =>
                diagnostic.Severity == CsmxDiagnosticSeverity.Error
            )
        )
        {
            return;
        }

        context.AddSource(
            CreateHintName(file.Path),
            SourceText.From(transform.Code, Encoding.UTF8)
        );
    }

    private static Diagnostic ToRoslynDiagnostic(
        string path,
        SourceText sourceText,
        CsmxDiagnostic diagnostic
    )
    {
        var descriptor =
            diagnostic.Severity == CsmxDiagnosticSeverity.Warning
                ? TransformWarning
                : TransformError;
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
            Math.Max(0, Math.Min(diagnostic.Span.Start, sourceText.Length)),
            Math.Max(0, Math.Min(diagnostic.Span.End, sourceText.Length))
        );
        var lineSpan = sourceText.Lines.GetLinePositionSpan(span);
        var location = Location.Create(path, span, lineSpan);
        return Diagnostic.Create(descriptor, location, diagnostic.Message);
    }

    private static string CreateHintName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var builder = new StringBuilder(normalized.Length + 16);
        foreach (var ch in normalized)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        builder.Append('_');
        builder.Append(StableHash(normalized).ToString("x8"));
        builder.Append(".g.cs");
        return builder.ToString();
    }

    private static uint StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619u;
        }

        return hash;
    }

    private sealed record CsmxGeneratorOptions(
        string? ProjectDirectory,
        string? RootNamespace,
        string? CompileMode,
        string? ElementFactory,
        string? AttributeFactory,
        string? TextFactory,
        string? FormattedTextChild,
        string? AlignedFormattedTextChild,
        string? PropsFactory,
        string? ChildrenFactory,
        string? ChildSequenceFactory,
        string? KeyedSequenceElement,
        string? KeyedSequenceItemsAttribute,
        string? KeyedSequenceKeyAttribute,
        string? KeyedSequenceTemplate,
        string? ComponentNames,
        string? ComponentLowering,
        string? ComponentTemplate,
        string? FluentCreate,
        string? FluentAttribute,
        string? FluentTextChild,
        string? FluentExpressionChild,
        string? FluentFormattedExpressionChild,
        string? FluentElementChild,
        string? FluentComponentTemplate
    )
    {
        public static CsmxGeneratorOptions From(AnalyzerConfigOptions options) =>
            new(
                Get(options, "MSBuildProjectDirectory"),
                Get(options, "RootNamespace"),
                Get(options, "CsmxCompileMode"),
                Get(options, "CsmxElementFactory"),
                Get(options, "CsmxAttributeFactory"),
                Get(options, "CsmxTextFactory"),
                Get(options, "CsmxFormattedTextChild"),
                Get(options, "CsmxAlignedFormattedTextChild"),
                Get(options, "CsmxPropsFactory"),
                Get(options, "CsmxChildrenFactory"),
                Get(options, "CsmxChildSequenceFactory"),
                Get(options, "CsmxKeyedSequenceElement"),
                Get(options, "CsmxKeyedSequenceItemsAttribute"),
                Get(options, "CsmxKeyedSequenceKeyAttribute"),
                Get(options, "CsmxKeyedSequenceTemplate"),
                Get(options, "CsmxComponentNames"),
                Get(options, "CsmxComponentLowering"),
                Get(options, "CsmxComponentTemplate"),
                Get(options, "CsmxFluentCreate"),
                Get(options, "CsmxFluentAttribute"),
                Get(options, "CsmxFluentTextChild"),
                Get(options, "CsmxFluentExpressionChild"),
                Get(options, "CsmxFluentFormattedExpressionChild"),
                Get(options, "CsmxFluentElementChild"),
                Get(options, "CsmxFluentComponentTemplate")
            );

        public CsmxTransformOptions CreateTransformOptions(string sourcePath) =>
            new()
            {
                CompileMode = NormalizeCompileMode(
                    CompileMode,
                    CsmxTransformOptions.Default.CompileMode
                ),
                ElementFactory = NormalizeFactory(
                    ElementFactory,
                    CsmxTransformOptions.Default.ElementFactory
                ),
                AttributeFactory = NormalizeFactory(
                    AttributeFactory,
                    CsmxTransformOptions.Default.AttributeFactory
                ),
                TextFactory = NormalizeFactory(
                    TextFactory,
                    CsmxTransformOptions.Default.TextFactory
                ),
                FormattedTextChild = NormalizeTemplate(
                    FormattedTextChild,
                    CsmxTransformOptions.Default.FormattedTextChild
                ),
                AlignedFormattedTextChild = NormalizeTemplate(
                    AlignedFormattedTextChild,
                    CsmxTransformOptions.Default.AlignedFormattedTextChild
                ),
                PropsFactory = NormalizeFactory(
                    PropsFactory,
                    CsmxTransformOptions.Default.PropsFactory
                ),
                ChildrenFactory = NormalizeFactory(
                    ChildrenFactory,
                    CsmxTransformOptions.Default.ChildrenFactory
                ),
                ChildSequenceFactory = NormalizeFactory(
                    ChildSequenceFactory,
                    CsmxTransformOptions.Default.ChildSequenceFactory
                ),
                KeyedSequenceElement = NormalizeTemplate(
                    KeyedSequenceElement,
                    CsmxTransformOptions.Default.KeyedSequenceElement
                ),
                KeyedSequenceItemsAttribute = NormalizeTemplate(
                    KeyedSequenceItemsAttribute,
                    CsmxTransformOptions.Default.KeyedSequenceItemsAttribute
                ),
                KeyedSequenceKeyAttribute = NormalizeTemplate(
                    KeyedSequenceKeyAttribute,
                    CsmxTransformOptions.Default.KeyedSequenceKeyAttribute
                ),
                KeyedSequenceTemplate = NormalizeTemplate(
                    KeyedSequenceTemplate,
                    CsmxTransformOptions.Default.KeyedSequenceTemplate
                ),
                ComponentNames =
                    ComponentNames?.Trim() ?? CsmxTransformOptions.Default.ComponentNames,
                ComponentLowering = NormalizeComponentLowering(
                    ComponentLowering,
                    CsmxTransformOptions.Default.ComponentLowering
                ),
                ComponentTemplate = NormalizeTemplate(
                    ComponentTemplate,
                    CsmxTransformOptions.Default.ComponentTemplate
                ),
                SourceIdentity = CreateSourceIdentity(sourcePath, ProjectDirectory),
                FluentCreate = NormalizeTemplate(
                    FluentCreate,
                    CsmxTransformOptions.Default.FluentCreate
                ),
                FluentAttribute = NormalizeTemplate(
                    FluentAttribute,
                    CsmxTransformOptions.Default.FluentAttribute
                ),
                FluentTextChild = NormalizeTemplate(
                    FluentTextChild,
                    CsmxTransformOptions.Default.FluentTextChild
                ),
                FluentExpressionChild = NormalizeTemplate(
                    FluentExpressionChild,
                    CsmxTransformOptions.Default.FluentExpressionChild
                ),
                FluentFormattedExpressionChild = NormalizeTemplate(
                    FluentFormattedExpressionChild,
                    CsmxTransformOptions.Default.FluentFormattedExpressionChild
                ),
                FluentElementChild = NormalizeTemplate(
                    FluentElementChild,
                    CsmxTransformOptions.Default.FluentElementChild
                ),
                FluentComponentTemplate = NormalizeTemplate(
                    FluentComponentTemplate,
                    CsmxTransformOptions.Default.FluentComponentTemplate
                ),
            };

        private static string? Get(AnalyzerConfigOptions options, string name) =>
            options.TryGetValue("build_property." + name, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;

        private static CsmxCompileMode NormalizeCompileMode(
            string? value,
            CsmxCompileMode fallback
        ) =>
            string.Equals(value?.Trim(), "fluent", StringComparison.OrdinalIgnoreCase)
                ? CsmxCompileMode.Fluent
            : string.Equals(value?.Trim(), "factory", StringComparison.OrdinalIgnoreCase)
                ? CsmxCompileMode.Factory
            : fallback;

        private static CsmxComponentLowering NormalizeComponentLowering(
            string? value,
            CsmxComponentLowering fallback
        ) =>
            string.Equals(value?.Trim(), "factory", StringComparison.OrdinalIgnoreCase)
                ? CsmxComponentLowering.FactoryCall
            : string.Equals(value?.Trim(), "direct", StringComparison.OrdinalIgnoreCase)
                ? CsmxComponentLowering.DirectCall
            : fallback;

        private static string NormalizeFactory(string? value, string fallback)
        {
            var trimmed = value?.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed!;
        }

        private static string NormalizeTemplate(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();

        private static string CreateSourceIdentity(string sourcePath, string? projectDirectory)
        {
            if (!string.IsNullOrWhiteSpace(projectDirectory))
            {
                try
                {
                    var relative = GetRelativePath(
                        Path.GetFullPath(projectDirectory),
                        Path.GetFullPath(sourcePath)
                    );
                    if (!relative.StartsWith("..", StringComparison.Ordinal))
                    {
                        return relative.Replace('\\', '/');
                    }
                }
                catch
                {
                    // Fall through to absolute identity.
                }
            }

            return sourcePath.Replace('\\', '/');
        }

        private static string GetRelativePath(string relativeTo, string path)
        {
            if (
                !relativeTo.EndsWith(
                    Path.DirectorySeparatorChar.ToString(),
                    StringComparison.Ordinal
                )
            )
            {
                relativeTo += Path.DirectorySeparatorChar;
            }

            var relativeUri = new Uri(relativeTo).MakeRelativeUri(new Uri(path));
            return Uri.UnescapeDataString(relativeUri.ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
