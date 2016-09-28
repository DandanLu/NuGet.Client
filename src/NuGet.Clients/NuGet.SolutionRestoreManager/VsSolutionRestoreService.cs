﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.PackageManagement.VisualStudio;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Versioning;
using ActivityLog = Microsoft.VisualStudio.Shell.ActivityLog;
using ThreadHelper = Microsoft.VisualStudio.Shell.ThreadHelper;

namespace NuGet.SolutionRestoreManager
{
    [PartCreationPolicy(CreationPolicy.Shared)]
    [Export(typeof(IVsSolutionRestoreService))]
    public class VsSolutionRestoreService : IVsSolutionRestoreService
    {
        private readonly EnvDTE.DTE _dte;
        private readonly IProjectSystemCache _projectSystemCache;

        [ImportingConstructor]
        public VsSolutionRestoreService(IProjectSystemCache projectSystemCache)
        {
            if (projectSystemCache == null)
            {
                throw new ArgumentNullException(nameof(projectSystemCache));
            }

            _projectSystemCache = projectSystemCache;

            _dte = ServiceLocator.GetInstance<EnvDTE.DTE>();
        }

        public Task<bool> CurrentRestoreOperation
        {
            get
            {
                return Task.FromResult(true);
            }
        }

        public async Task<bool> NominateProjectAsync(string projectUniqueName, IVsProjectRestoreInfo projectRestoreInfo, CancellationToken token)
        {
            if (string.IsNullOrEmpty(projectUniqueName))
            {
                throw new ArgumentException(ProjectManagement.Strings.Argument_Cannot_Be_Null_Or_Empty, nameof(projectUniqueName));
            }

            if (projectRestoreInfo == null)
            {
                throw new ArgumentNullException(nameof(projectRestoreInfo));
            }

            if (projectRestoreInfo.TargetFrameworks == null)
            {
                throw new InvalidOperationException("No target frameworks");
            }

            ActivityLog.LogInformation(
                ExceptionHelper.LogEntrySource,
                $"The nominate API is called for '{projectUniqueName}'.");

            var packageSpec = ToPackageSpec(projectRestoreInfo);
            packageSpec.FilePath = packageSpec.RestoreMetadata.ProjectPath = projectUniqueName;

            return await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var lookup = EnvDTESolutionUtility.GetPathToDTEProjectLookup(_dte.Solution);
                if (lookup.ContainsKey(projectUniqueName))
                {
                    var dteProject = lookup[projectUniqueName];

                    // Get information about the project from DTE.
                    var projectNames = ProjectNames.FromDTEProject(dteProject);
                    packageSpec.Name = packageSpec.RestoreMetadata.ProjectName = projectNames.ShortName;

                    var restoreMetadata = packageSpec.RestoreMetadata;
                    restoreMetadata.ProjectUniqueName = projectNames.UniqueName;
                    
                    var projectDirectory = EnvDTEProjectUtility.GetProjectDirectory(dteProject);
                    restoreMetadata.OutputPath = Path.GetFullPath(
                        Path.Combine(
                            projectDirectory,
                            projectRestoreInfo.BaseIntermediatePath));

                    DumpProjectRestoreInfo(packageSpec);

                    return _projectSystemCache.AddProjectRestoreInfo(projectNames, packageSpec);
                }

                return true;
            });
        }

        private static void DumpProjectRestoreInfo(PackageSpec packageSpec)
        {
            try
            {
                var outputPath = packageSpec.RestoreMetadata.OutputPath;
                if (!Directory.Exists(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                }

                // Create dg file
                var dgFile = new DependencyGraphSpec();
                dgFile.AddRestore(packageSpec.RestoreMetadata.ProjectName);
                dgFile.AddProject(packageSpec);

                var dgPath = Path.Combine(outputPath, $"{Guid.NewGuid().ToString()}.dg2");
                dgFile.Save(dgPath);
            }
            catch(Exception ex)
            {
                ExceptionHelper.WriteToActivityLog(ex);
            }
        }

        private static PackageSpec ToPackageSpec(IVsProjectRestoreInfo projectRestoreInfo)
        {
            var tfis = projectRestoreInfo.TargetFrameworks
                .Cast<IVsTargetFrameworkInfo>()
                .Select(ToTargetFrameworkInformation)
                .ToArray();

            var projectReferences = GetProjectReferences(projectRestoreInfo);

            var packageReferences = GetPackageReferences(projectRestoreInfo);

            var packageSpec = new PackageSpec(tfis)
            {
                RestoreMetadata = new ProjectRestoreMetadata
                {
                    OutputType = RestoreOutputType.NETCore,
                    OriginalTargetFrameworks = tfis
                        .Select(tfi => tfi.FrameworkName.GetShortFolderName())
                        .ToList()
                }
            };

            return packageSpec;
        }

        private static TargetFrameworkInformation ToTargetFrameworkInformation(
            IVsTargetFrameworkInfo targetFrameworkInfo)
        {
            var tfi = new TargetFrameworkInformation
            {
                FrameworkName = NuGetFramework.Parse(targetFrameworkInfo.TargetFrameworkMoniker)
            };

            if (targetFrameworkInfo.PackageReferences != null)
            {
                tfi.Dependencies.AddRange(
                    targetFrameworkInfo.PackageReferences
                        .Cast<IVsReferenceItem>()
                        .Select(ToPackageLibraryDependency));
            }

            if (targetFrameworkInfo.ProjectReferences != null)
            {
                tfi.Dependencies.AddRange(
                    targetFrameworkInfo.ProjectReferences
                        .Cast<IVsReferenceItem>()
                        .Select(ToProjectLibraryDependency));
            }

            return tfi;
        }

        private static PackageReference[] GetPackageReferences(
            IVsProjectRestoreInfo projectRestoreInfo)
        {
            var packageReferences = projectRestoreInfo
                .TargetFrameworks
                .Cast<IVsTargetFrameworkInfo>()
                .GroupBy(
                    keySelector: tfm =>
                        NuGetFramework.Parse(tfm.TargetFrameworkMoniker),
                    resultSelector: (key, tfms) =>
                        tfms.SelectMany(tfm =>
                            tfm.PackageReferences
                                .Cast<IVsReferenceItem>()
                                .Select(p => ToPackageReference(p, key))))
                .SelectMany(l => l)
                .ToList();

            var frameworkSorter = new NuGetFrameworkSorter();
            return packageReferences
                .GroupBy(
                    keySelector: p => p.PackageIdentity,
                    resultSelector: (id, ps) =>
                        ps.OrderBy(p => p.TargetFramework, frameworkSorter).First())
                .ToArray();
        }

        private static ProjectRestoreReference[] GetProjectReferences(
            IVsProjectRestoreInfo projectRestoreInfo)
        {
            var projectReferences = projectRestoreInfo
                .TargetFrameworks
                .Cast<IVsTargetFrameworkInfo>()
                .SelectMany(tfm =>
                    tfm.ProjectReferences
                        .Cast<IVsReferenceItem>()
                        .Select(ToProjectRestoreReference))
                .ToList();

            return projectReferences
                .GroupBy(
                    keySelector: p => p.ProjectPath,
                    resultSelector: (id, ps) => ps.First(),
                    comparer: StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static LibraryDependency ToPackageLibraryDependency(IVsReferenceItem item)
        {
            var dependency = new LibraryDependency
            {
                LibraryRange = new LibraryRange(
                    name: item.Name,
                    versionRange: GetVersionRange(item),
                    typeConstraint: LibraryDependencyTarget.Package)
            };

            return dependency;
        }

        private static LibraryDependency ToProjectLibraryDependency(IVsReferenceItem item)
        {
            var dependency = new LibraryDependency
            {
                LibraryRange = new LibraryRange(
                    name: item.Name,
                    versionRange: VersionRange.All,
                    typeConstraint: LibraryDependencyTarget.ExternalProject)
            };

            return dependency;
        }

        private static VersionRange GetVersionRange(IVsReferenceItem item)
        {
            var versionRange = TryGetProperty(item, "Version");

            if (!string.IsNullOrEmpty(versionRange))
            {
                return VersionRange.Parse(versionRange);
            }

            return VersionRange.All;
        }

        private static PackageReference ToPackageReference(
            IVsReferenceItem item, NuGetFramework framework)
        {
            var versionRange = GetVersionRange(item);
            var packageId = new PackageIdentity(item.Name, versionRange.MinVersion);
            var packageReference = new PackageReference(packageId, framework);

            return packageReference;
        }

        private static ProjectRestoreReference ToProjectRestoreReference(
            IVsReferenceItem item)
        {
            var projectPath = TryGetProperty(item, "ProjectFileFullPath");
            return new ProjectRestoreReference
            {
                ProjectUniqueName = item.Name,
                ProjectPath = projectPath
            };
        }

        private static string TryGetProperty(IVsReferenceItem item, string propertyName)
        {
            if (item.Properties == null)
            {
                // this happens in unit tests
                return null;
            }

            try
            {
                IVsReferenceProperty property = item.Properties.Item(propertyName);
                if (property != null)
                {
                    return property.Value;
                }
            }
            catch (ArgumentException)
            {
            }

            return null;
        }
    }
}
