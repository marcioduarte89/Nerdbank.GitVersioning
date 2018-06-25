namespace Nerdbank.GitVersioning.Tool
{
    using System;
    using System.Collections.Generic;
    using System.CommandLine;
    using System.IO;
    using System.Linq;
    using Newtonsoft.Json;
    using MSBuild = Microsoft.Build.Evaluation;

    internal class Program
    {
        private const string DefaultVersionSpec = "1.0-beta";

        private const string DefaultVersionInfoFormat = "text";

        private const string DefaultRef = "HEAD";

        private enum ExitCodes
        {
            OK,
            NoGitRepo,
            InvalidVersionSpec,
            BadCloudVariable,
            DuplicateCloudVariable,
            NoCloudBuildEnvDetected,
            UnsupportedFormat,
            NoMatchingVersion,
        }


        private static ExitCodes exitCode;

        public static int Main(string[] args)
        {
            ArgumentSyntax.Parse(args, syntax =>
            {
                var commandText = string.Empty;
                var projectPath = string.Empty;
                var versionJsonRoot = string.Empty;
                var version = string.Empty;
                IReadOnlyList<string> cloudVariables = Array.Empty<string>();
                var format = string.Empty;
                bool quiet = false;

                var install = syntax.DefineCommand("install", ref commandText, "Prepares a project to have version stamps applied using Nerdbank.GitVersioning.");
                syntax.DefineOption("p|path", ref versionJsonRoot, "The path to the directory that should contain the version.json file. The default is the root of the git repo.");
                syntax.DefineOption("v|version", ref version, $"The initial version to set. The default is {DefaultVersionSpec}.");

                var getVersion = syntax.DefineCommand("get-version", ref commandText, "Gets the version information for a project.");
                syntax.DefineOption("p|project", ref projectPath, "The path to the project or project directory. The default is the current directory.");
                syntax.DefineOption("f|format", ref format, $"The format to write the version information. Allowed values are: text, json. The default is {DefaultVersionInfoFormat}.");

                var setVersion = syntax.DefineCommand("set-version", ref commandText, "Updates the version stamp that is applied to a project.");
                syntax.DefineOption("p|project", ref projectPath, "The path to the project or project directory. The default is the root directory of the repo that spans the current directory, or an existing version.json file, if applicable.");
                syntax.DefineParameter("version", ref version, "The version to set.");

                var tag = syntax.DefineCommand("tag", ref commandText, "Creates a git tag to mark a version.");
                syntax.DefineOption("p|project", ref projectPath, "The path to the project or project directory. The default is the root directory of the repo that spans the current directory, or an existing version.json file, if applicable.");
                syntax.DefineParameter("versionOrRef", ref version, $"The a.b.c[.d] version or git ref to be tagged. If not specified, {DefaultRef} is used.");

                var getCommits = syntax.DefineCommand("get-commits", ref commandText, "Gets the commit(s) that match a given version.");
                syntax.DefineOption("p|project", ref projectPath, "The path to the project or project directory. The default is the root directory of the repo that spans the current directory, or an existing version.json file, if applicable.");
                syntax.DefineOption("q|quiet", ref quiet, "Use minimal output.");
                syntax.DefineParameter("version", ref version, "The a.b.c[.d] version to find.");

                var cloud = syntax.DefineCommand("cloud", ref commandText, "Communicates with the ambient cloud build to set the build number and/or other cloud build variables.");
                syntax.DefineOption("p|project", ref projectPath, "The path to the project or project directory used to calculate the version. The default is the current directory. Ignored if the -v option is specified.");
                syntax.DefineOption("v|version", ref version, "The string to use for the cloud build number. If not specified, the computed version will be used.");
                syntax.DefineOptionList("d|define", ref cloudVariables, "Additional cloud build variables to define. Each should be in the NAME=VALUE syntax.");

                if (install.IsActive)
                {
                    exitCode = OnInstallCommand(versionJsonRoot, version);
                }
                else if (getVersion.IsActive)
                {
                    exitCode = OnGetVersionCommand(projectPath, format);
                }
                else if (setVersion.IsActive)
                {
                    exitCode = OnSetVersionCommand(projectPath, version);
                }
                else if (tag.IsActive)
                {
                    exitCode = OnTagCommand(projectPath, version);
                }
                else if (getCommits.IsActive)
                {
                    exitCode = OnGetCommitsCommand(projectPath, version, quiet);
                }
                else if (cloud.IsActive)
                {
                    exitCode = OnCloudCommand(projectPath, version, cloudVariables);
                }
            });

            return (int)exitCode;
        }

        private static ExitCodes OnInstallCommand(string versionJsonRoot, string version)
        {
            if (!SemanticVersion.TryParse(string.IsNullOrEmpty(version) ? DefaultVersionSpec : version, out var semver))
            {
                Console.Error.WriteLine($"\"{version}\" is not a semver-compliant version spec.");
                return ExitCodes.InvalidVersionSpec;
            }

            var options = new VersionOptions
            {
                Version = semver,
                PublicReleaseRefSpec = new string[]
                {
                    @"^refs/heads/master$",
                    @"^refs/heads/v\d+(?:\.\d+)?$",
                },
                CloudBuild = new VersionOptions.CloudBuildOptions
                {
                    BuildNumber = new VersionOptions.CloudBuildNumberOptions
                    {
                        Enabled = true,
                    },
                },
            };
            string searchPath = GetSpecifiedOrCurrentDirectoryPath(versionJsonRoot);
            if (!Directory.Exists(searchPath))
            {
                Console.Error.WriteLine("\"{0}\" is not an existing directory.", searchPath);
                return ExitCodes.NoGitRepo;
            }

            var repository = GitExtensions.OpenGitRepo(searchPath);
            if (repository == null)
            {
                Console.Error.WriteLine("No git repo found at or above: \"{0}\"", searchPath);
                return ExitCodes.NoGitRepo;
            }

            if (string.IsNullOrEmpty(versionJsonRoot))
            {
                versionJsonRoot = repository.Info.WorkingDirectory;
            }

            var existingOptions = VersionFile.GetVersion(versionJsonRoot);
            if (existingOptions != null)
            {
                if (!string.IsNullOrEmpty(version))
                {
                    var setVersionExitCode = OnSetVersionCommand(versionJsonRoot, version);
                    if (setVersionExitCode != ExitCodes.OK)
                    {
                        return setVersionExitCode;
                    }
                }
            }
            else
            {
                string versionJsonPath = VersionFile.SetVersion(versionJsonRoot, options);
                LibGit2Sharp.Commands.Stage(repository, versionJsonPath);
            }

            // Create/update the Directory.Build.props file in the directory of the version.json file to add the NB.GV package.
            string directoryBuildPropsPath = Path.Combine(versionJsonRoot, "Directory.Build.props");
            MSBuild.Project propsFile;
            if (File.Exists(directoryBuildPropsPath))
            {
                propsFile = new MSBuild.Project(directoryBuildPropsPath);
            }
            else
            {
                propsFile = new MSBuild.Project();
            }

            const string PackageReferenceItemType = "PackageReference";
            const string PackageId = "Nerdbank.GitVersioning";
            if (!propsFile.GetItemsByEvaluatedInclude(PackageId).Any(i => i.ItemType == "PackageReference"))
            {
                propsFile.AddItem(
                    PackageReferenceItemType,
                    PackageId,
                    new Dictionary<string, string>
                    {
                        { "Version", "2.1.23" }, // TODO: use the latest version... somehow...
                        { "PrivateAssets", "all" },
                    });

                propsFile.Save(directoryBuildPropsPath);
            }

            LibGit2Sharp.Commands.Stage(repository, directoryBuildPropsPath);

            return ExitCodes.OK;
        }

        private static string GetSpecifiedOrCurrentDirectoryPath(string versionJsonRoot)
        {
            return Path.GetFullPath(string.IsNullOrEmpty(versionJsonRoot) ? "." : versionJsonRoot);
        }

        private static ExitCodes OnGetVersionCommand(string projectPath, string format)
        {
            if (string.IsNullOrEmpty(format))
            {
                format = DefaultVersionInfoFormat;
            }

            string searchPath = GetSpecifiedOrCurrentDirectoryPath(projectPath);
            var oracle = VersionOracle.Create(searchPath);
            switch (format.ToLowerInvariant())
            {
                case "text":
                    Console.WriteLine("Version:                      {0}", oracle.Version);
                    Console.WriteLine("AssemblyVersion:              {0}", oracle.AssemblyVersion);
                    Console.WriteLine("AssemblyInformationalVersion: {0}", oracle.AssemblyInformationalVersion);
                    Console.WriteLine("NuGet package Version:        {0}", oracle.NuGetPackageVersion);
                    Console.WriteLine("NPM package Version:          {0}", oracle.NpmPackageVersion);
                    break;
                case "json":
                    Console.WriteLine(JsonConvert.SerializeObject(oracle, Formatting.Indented));
                    break;
                default:
                    Console.Error.WriteLine("Unsupported format: {0}", format);
                    return ExitCodes.UnsupportedFormat;
            }

            return ExitCodes.OK;
        }

        private static ExitCodes OnSetVersionCommand(string projectPath, string version)
        {
            if (!SemanticVersion.TryParse(string.IsNullOrEmpty(version) ? DefaultVersionSpec : version, out var semver))
            {
                Console.Error.WriteLine($"\"{version}\" is not a semver-compliant version spec.");
                return ExitCodes.InvalidVersionSpec;
            }

            var defaultOptions = new VersionOptions
            {
                Version = semver,
            };

            string searchPath = GetSpecifiedOrCurrentDirectoryPath(projectPath);
            var repository = GitExtensions.OpenGitRepo(searchPath);
            var existingOptions = VersionFile.GetVersion(searchPath, out string actualDirectory);
            string versionJsonPath;
            if (existingOptions != null)
            {
                existingOptions.Version = semver;
                versionJsonPath = VersionFile.SetVersion(actualDirectory, existingOptions);
            }
            else if (string.IsNullOrEmpty(projectPath))
            {
                if (repository == null)
                {
                    Console.Error.WriteLine("No version file and no git repo found at or above: \"{0}\"", searchPath);
                    return ExitCodes.NoGitRepo;
                }

                versionJsonPath = VersionFile.SetVersion(repository.Info.WorkingDirectory, defaultOptions);
            }
            else
            {
                versionJsonPath = VersionFile.SetVersion(projectPath, defaultOptions);
            }

            if (repository != null)
            {
                LibGit2Sharp.Commands.Stage(repository, versionJsonPath);
            }

            return ExitCodes.OK;
        }

        private static ExitCodes OnTagCommand(string projectPath, string versionOrRef)
        {
            if (string.IsNullOrEmpty(versionOrRef))
            {
                versionOrRef = DefaultRef;
            }

            string searchPath = GetSpecifiedOrCurrentDirectoryPath(projectPath);

            var repository = GitExtensions.OpenGitRepo(searchPath);
            if (repository == null)
            {
                Console.Error.WriteLine("No git repo found at or above: \"{0}\"", searchPath);
                return ExitCodes.NoGitRepo;
            }

            LibGit2Sharp.GitObject refObject = null;
            try
            {
                repository.RevParse(versionOrRef, out var reference, out refObject);
            }
            catch (LibGit2Sharp.NotFoundException) { }

            var commit = refObject as LibGit2Sharp.Commit;
            if (commit == null)
            {
                if (!Version.TryParse(versionOrRef, out Version parsedVersion))
                {
                    Console.Error.WriteLine($"\"{versionOrRef}\" is not a simple a.b.c[.d] version spec or git reference.");
                    return ExitCodes.InvalidVersionSpec;
                }

                string repoRelativeProjectDir = GetRepoRelativePath(searchPath, repository);
                var candidateCommits = GitExtensions.GetCommitsFromVersion(repository, parsedVersion, repoRelativeProjectDir).ToList();
                if (candidateCommits.Count == 0)
                {
                    Console.Error.WriteLine("No commit with that version found.");
                    return ExitCodes.NoMatchingVersion;
                }
                else if (candidateCommits.Count > 1)
                {
                    PrintCommits(false, searchPath, repository, candidateCommits, includeOptions: true);
                    int selection;
                    do
                    {
                        Console.Write("Enter selection: ");
                    }
                    while (!int.TryParse(Console.ReadLine(), out selection) || selection > candidateCommits.Count || selection < 1);
                    commit = candidateCommits[selection - 1];
                }
                else
                {
                    commit = candidateCommits.Single();
                }
            }

            var oracle = new VersionOracle(searchPath, repository, commit, CloudBuild.Active);
            oracle.PublicRelease = true; // assume a public release so we don't get a redundant -gCOMMITID in the tag name
            string tagName = $"v{oracle.SemVer2}";
            repository.Tags.Add(tagName, commit);
            Console.WriteLine("{0} tag created at {1}.", tagName, commit.Sha);
            Console.WriteLine("Remember to push to a remote: git push origin {0}", tagName);

            return ExitCodes.OK;
        }

        private static ExitCodes OnGetCommitsCommand(string projectPath, string version, bool quiet)
        {
            if (!Version.TryParse(version, out Version parsedVersion))
            {
                Console.Error.WriteLine($"\"{version}\" is not a simple a.b.c[.d] version spec.");
                return ExitCodes.InvalidVersionSpec;
            }

            string searchPath = GetSpecifiedOrCurrentDirectoryPath(projectPath);

            var repository = GitExtensions.OpenGitRepo(searchPath);
            if (repository == null)
            {
                Console.Error.WriteLine("No git repo found at or above: \"{0}\"", searchPath);
                return ExitCodes.NoGitRepo;
            }

            string repoRelativeProjectDir = GetRepoRelativePath(searchPath, repository);
            var candidateCommits = GitExtensions.GetCommitsFromVersion(repository, parsedVersion, repoRelativeProjectDir).ToList();
            PrintCommits(quiet, searchPath, repository, candidateCommits);

            return ExitCodes.OK;
        }

        private static ExitCodes OnCloudCommand(string projectPath, string version, IReadOnlyList<string> cloudVariables)
        {
            var variables = new Dictionary<string, string>();
            foreach (string def in cloudVariables)
            {
                string[] split = def.Split(new char[] { '=' }, 2);
                if (split.Length < 2)
                {
                    Console.Error.WriteLine($"\"{def}\" is not in the NAME=VALUE syntax required for cloud variables.");
                    return ExitCodes.BadCloudVariable;
                }

                if (variables.ContainsKey(split[0]))
                {
                    Console.Error.WriteLine($"Cloud build variable \"{split[0]}\" specified more than once.");
                    return ExitCodes.DuplicateCloudVariable;
                }

                variables.Add(split[0], split[1]);
            }

            ICloudBuild activeCloudBuild = CloudBuild.Active;
            if (activeCloudBuild != null)
            {
                activeCloudBuild.SetCloudBuildNumber(version, Console.Out, Console.Error);
                foreach (var pair in variables)
                {
                    activeCloudBuild.SetCloudBuildVariable(pair.Key, pair.Value, Console.Out, Console.Error);
                }

                return ExitCodes.OK;
            }
            else
            {
                Console.Error.WriteLine("No cloud build detected.");
                return ExitCodes.NoCloudBuildEnvDetected;
            }
        }

        private static string GetRepoRelativePath(string searchPath, LibGit2Sharp.Repository repository)
        {
            return searchPath.Substring(repository.Info.WorkingDirectory.Length);
        }

        private static void PrintCommits(bool quiet, string projectDirectory, LibGit2Sharp.Repository repository, List<LibGit2Sharp.Commit> candidateCommits, bool includeOptions = false)
        {
            int index = 1;
            foreach (var commit in candidateCommits)
            {
                if (includeOptions)
                {
                    Console.Write($"{index++,3}. ");
                }

                if (quiet)
                {
                    Console.WriteLine(commit.Sha);
                }
                else
                {
                    var oracle = new VersionOracle(projectDirectory, repository, commit, null);
                    Console.WriteLine($"{commit.Sha} {oracle.Version} {commit.MessageShort}");
                }
            }
        }
    }
}
