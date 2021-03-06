﻿using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using NuGet.ProjectModel;

namespace DotNetOutdated.Services
{
    internal class ProjectAnalysisService : IProjectAnalysisService
    {
        private readonly IDependencyGraphService _dependencyGraphService;
        private readonly IDotNetRestoreService _dotNetRestoreService;
        private readonly IFileSystem _fileSystem;

        public ProjectAnalysisService(IDependencyGraphService dependencyGraphService, IDotNetRestoreService dotNetRestoreService, IFileSystem fileSystem)
        {
            _dependencyGraphService = dependencyGraphService;
            _dotNetRestoreService = dotNetRestoreService;
            _fileSystem = fileSystem;
        }
        
        public List<Project> AnalyzeProject(string projectPath, bool includeTransitiveDependencies, int transitiveDepth)
        {
            var dependencyGraph = _dependencyGraphService.GenerateDependencyGraph(projectPath);
            if (dependencyGraph == null)
                return null;

            var projects = new List<Project>();
            foreach (var packageSpec in dependencyGraph.Projects.Where(p => p.RestoreMetadata.ProjectStyle == ProjectStyle.PackageReference))
            {
                // Restore the packages
                _dotNetRestoreService.Restore(packageSpec.FilePath);
                
                // Load the lock file
                string lockFilePath = _fileSystem.Path.Combine(packageSpec.RestoreMetadata.OutputPath, "project.assets.json");
                var lockFile = LockFileUtilities.GetLockFile(lockFilePath, NuGet.Common.NullLogger.Instance);
                
                // Create a project
                var project = new Project
                {
                    Name = packageSpec.Name,
                    Sources = packageSpec.RestoreMetadata.Sources.Select(s => s.SourceUri).ToList(),
                    FilePath = packageSpec.FilePath
                };
                projects.Add(project);

                // Get the target frameworks with their dependencies 
                foreach (var targetFrameworkInformation in packageSpec.TargetFrameworks)
                {
                    var targetFramework = new Project.TargetFramework
                    {
                        Name = targetFrameworkInformation.FrameworkName,
                    };
                    project.TargetFrameworks.Add(targetFramework);

                    var target = lockFile.Targets.FirstOrDefault(t => t.TargetFramework.Equals(targetFrameworkInformation.FrameworkName));

                    if (target != null)
                    {
                        foreach (var projectDependency in targetFrameworkInformation.Dependencies)
                        {
                           var projectLibrary = target.Libraries.FirstOrDefault(library => string.Equals(library.Name, projectDependency.Name, StringComparison.OrdinalIgnoreCase));

                            var dependency = new Project.Dependency
                            {
                                Name = projectDependency.Name,
                                VersionRange = projectDependency.LibraryRange.VersionRange,
                                ResolvedVersion = projectLibrary?.Version,
                                AutoReferenced = projectDependency.AutoReferenced
                            };
                            targetFramework.Dependencies.Add(dependency);

                            // Process transitive dependencies for the library
                            if (includeTransitiveDependencies)
                                AddDependencies(dependency, projectLibrary, target, 1, transitiveDepth);
                        }
                    }
                }
            }

            return projects;
        }

        private void AddDependencies(Project.Dependency parentDependency, LockFileTargetLibrary parentLibrary, LockFileTarget target, int level, int transitiveDepth)
        {
            if (parentLibrary?.Dependencies != null)
            {
                foreach (var packageDependency in parentLibrary.Dependencies)
                {
                    var childLibrary = target.Libraries.FirstOrDefault(library => library.Name == packageDependency.Id);

                    var childDependency = new Project.Dependency
                    {
                        Name = packageDependency.Id,
                        VersionRange = packageDependency.VersionRange,
                        ResolvedVersion = childLibrary?.Version
                    };
                    parentDependency.Dependencies.Add(childDependency);

                    // Process the dependency for this project depency
                    if (level < transitiveDepth)
                        AddDependencies(childDependency, childLibrary, target, level + 1, transitiveDepth);
                }
            }
        }
    }
}