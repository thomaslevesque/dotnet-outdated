﻿using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DotNetOutdated.Exceptions;
using DotNetOutdated.Services;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Versioning;

[assembly: InternalsVisibleTo("DotNetOutdated.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace DotNetOutdated
{
    [Command(
        Name = "dotnet outdated",
        FullName = "A .NET Core global tool to list outdated Nuget packages.")]
    [VersionOptionFromMember(MemberName = nameof(GetVersion))]
    class Program : CommandBase
    {
        private readonly IFileSystem _fileSystem;
        private readonly IReporter _reporter;
        private readonly INuGetPackageResolutionService _nugetService;
        private readonly IProjectAnalysisService _projectAnalysisService;
        private readonly IProjectDiscoveryService _projectDiscoveryService;

        [Option(CommandOptionType.NoValue, Description = "Specifies whether to include auto-referenced packages.",
            LongName = "include-auto-references")]
        public bool IncludeAutoReferences { get; set; } = false;

        [Argument(0, Description = "The path to a .sln or .csproj file, or to a directory containing a .NET Core solution/project. " +
                                   "If none is specified, the current directory will be used.")]
        public string Path { get; set; }

        [Option(CommandOptionType.SingleValue, Description = "Specifies whether to look for pre-release versions of packages. " +
                                                             "Possible values: Auto (default), Always or Never.",
            ShortName = "pr", LongName = "pre-release")]
        public PrereleaseReporting Prerelease { get; set; } = PrereleaseReporting.Auto;

        [Option(CommandOptionType.SingleValue, Description = "Specifies whether the package should be locked to the current Major or Minor version. " +
                                                             "Possible values: None (default), Major or Minor.",
            ShortName = "vl", LongName = "version-lock")]
        public VersionLock VersionLock { get; set; } = VersionLock.None;

        [Option(CommandOptionType.NoValue, Description = "Specifies whether it should detect transitive dependencies.",
            ShortName = "t", LongName = "transitive")]
        public bool Transitive { get; set; } = false;

        [Option(CommandOptionType.SingleValue, Description = "Defines how many levels deep transitive dependencies should be analyzed. " +
                                                             "Integer value (default = 1)",
            ShortName="td", LongName = "transitive-depth")]
        public int TransitiveDepth { get; set; } = 1;

        [Option(CommandOptionType.NoValue, Description = "Specifies whether only outdated packages should be shown.",
            ShortName="o", LongName = "show-only-outdated")]
        public bool ShowOnlyOutdated { get; set; }

        public static int Main(string[] args)
        {
            using (var services = new ServiceCollection()
                    .AddSingleton<IConsole, PhysicalConsole>()
                    .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
                    .AddSingleton<IFileSystem, FileSystem>()
                    .AddSingleton<IProjectDiscoveryService, ProjectDiscoveryService>()
                    .AddSingleton<IProjectAnalysisService, ProjectAnalysisService>()
                    .AddSingleton<IDotNetRunner, DotNetRunner>()
                    .AddSingleton<IDependencyGraphService, DependencyGraphService>()
                    .AddSingleton<IDotNetRestoreService, DotNetRestoreService>()
                    .AddSingleton<INuGetPackageInfoService, NuGetPackageInfoService>()
                    .AddSingleton<INuGetPackageResolutionService, NuGetPackageResolutionService>()
                    .BuildServiceProvider())
            {
                var app = new CommandLineApplication<Program>
                {
                    ThrowOnUnexpectedArgument = false
                };
                app.Conventions
                    .UseDefaultConventions()
                    .UseConstructorInjection(services);

                return app.Execute(args);
            }
        }

        public static string GetVersion() => typeof(Program)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            .InformationalVersion;

        public Program(IFileSystem fileSystem, IReporter reporter, INuGetPackageResolutionService nugetService, IProjectAnalysisService projectAnalysisService,
            IProjectDiscoveryService projectDiscoveryService)
        {
            _fileSystem = fileSystem;
            _reporter = reporter;
            _nugetService = nugetService;
            _projectAnalysisService = projectAnalysisService;
            _projectDiscoveryService = projectDiscoveryService;
        }

        public async Task<int> OnExecute(CommandLineApplication app, IConsole console)
        {
            try
            {
                // If no path is set, use the current directory
                if (string.IsNullOrEmpty(Path))
                    Path = _fileSystem.Directory.GetCurrentDirectory();

                // Get all the projects
                console.Write("Discovering projects...");
                string projectPath = _projectDiscoveryService.DiscoverProject(Path);
                ClearCurrentConsoleLine();

                // Analyze the projects
                console.Write("Analyzing project and restoring packages...");
                var projects = _projectAnalysisService.AnalyzeProject(projectPath, Transitive, TransitiveDepth);
                ClearCurrentConsoleLine();

                foreach (var project in projects)
                {
                    int indentLevel = 1;

                    WriteProjectName(console, project);

                    // Process each target framework with its related dependencies
                    foreach (var targetFramework in project.TargetFrameworks)
                    {
                        WriteTargetFramework(console, targetFramework, indentLevel);

                        var dependencies = targetFramework.Dependencies;

                        if (!IncludeAutoReferences)
                            dependencies = dependencies.Where(d => d.AutoReferenced == false).ToList();
                        
                        if (dependencies.Count > 0)
                        {
                            bool hasOutdatedDependency = false;
                            foreach (var dependency in dependencies)
                            {
                                hasOutdatedDependency |= await ReportDependency(console, dependency, dependency.VersionRange, project.Sources, indentLevel, targetFramework, project.FilePath);
                            }

                            if (ShowOnlyOutdated && !hasOutdatedDependency)
                            {
                                console.WriteIndent(indentLevel);
                                console.WriteLine("-- Everything up-to-date --");
                            }
                        }
                        else
                        {
                            console.WriteIndent(indentLevel);
                            console.WriteLine("-- No dependencies --");
                        }
                    }

                    console.WriteLine();
                }

                return 0;
            }
            catch (CommandValidationException e)
            {
                _reporter.Error(e.Message);

                return 1;
            }
        }

        private static void WriteProjectName(IConsole console, Project project)
        {
            console.Write($"» {project.Name}", ConsoleColor.Yellow);
            console.WriteLine();
        }

        private static void WriteTargetFramework(IConsole console, Project.TargetFramework targetFramework, int indentLevel)
        {
            console.WriteIndent(indentLevel);
            console.Write($"[{targetFramework.Name}]", ConsoleColor.Cyan);
            console.WriteLine();
        }

        private async Task<bool> ReportDependency(IConsole console, Project.Dependency dependency, VersionRange versionRange, List<Uri> sources, int indentLevel,
            Project.TargetFramework targetFramework, string projectFilePath)
        {
            console.WriteIndent(indentLevel);
            console.Write($"{dependency.Name}");

            if (dependency.AutoReferenced)
                console.Write(" [A]");

            bool isOutdated = false;
            bool hasError = false;

            var referencedVersion = dependency.ResolvedVersion;
            if (referencedVersion == null)
            {
                hasError = true;
                console.Write(" ");
                console.Write("Cannot resolve referenced version", ConsoleColor.White, ConsoleColor.DarkRed);
                console.WriteLine();
            }
            else
            {
                console.Write("...");

                var latestVersion = await _nugetService.ResolvePackageVersions(dependency.Name, referencedVersion, sources, versionRange, VersionLock, Prerelease, targetFramework.Name, projectFilePath);
                
                console.Write("\b\b\b ");

                if (latestVersion != null)
                {
                    console.Write(referencedVersion, latestVersion > referencedVersion ? ConsoleColor.Red : ConsoleColor.Green);
                }
                else
                {
                    hasError = true;
                    console.Write($"{referencedVersion} ", ConsoleColor.Yellow); 
                    console.Write("Cannot resolve latest version", ConsoleColor.White, ConsoleColor.DarkCyan);
                }

                if (latestVersion > referencedVersion)
                {
                    isOutdated = true;
                    console.Write(" (");
                    console.Write(latestVersion, ConsoleColor.Blue);
                    console.Write(")");
                }
                console.WriteLine();
            }

            bool hasOutdatedDependency = false;
            foreach (var childDependency in dependency.Dependencies)
            {
                hasOutdatedDependency |= await ReportDependency(console, childDependency, childDependency.VersionRange, sources, indentLevel + 1, targetFramework, projectFilePath);
            }

            bool result = isOutdated || hasOutdatedDependency || hasError;
            if (ShowOnlyOutdated && !result)
            {
                // Undo the previous WriteLine and delete previous line
                Console.SetCursorPosition(0, Console.CursorTop - 1);
                ClearCurrentConsoleLine();
            }

            return result;
        }
        
        public static void ClearCurrentConsoleLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.BufferWidth)); 
            Console.SetCursorPosition(0, currentLineCursor);
        }

    }
}
