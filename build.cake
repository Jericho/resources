// Install tools.
#tool dotnet:?package=GitVersion.Tool&version=5.12.0
#tool dotnet:?package=coveralls.net&version=4.0.1
#tool nuget:https://f.feedz.io/jericho/jericho/nuget/?package=GitReleaseManager&version=0.17.0-collaborators0003
#tool nuget:?package=ReportGenerator&version=5.2.3
#tool nuget:?package=xunit.runner.console&version=2.7.0
#tool nuget:?package=CodecovUploader&version=0.7.2

// Install addins.
#addin nuget:?package=Cake.Coveralls&version=1.1.0
#addin nuget:?package=Cake.Git&version=4.0.0
#addin nuget:?package=Cake.Codecov&version=1.0.1


///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////

var target = Argument<string>("target", "Default");
var configuration = Argument<string>("configuration", "Release");

if (IsRunningOnUnix()) target = "Run-Unit-Tests";


///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////

var libraryName = "%%PROJECT-NAME%%";
var gitHubRepo = "%%PROJECT-NAME%%";

var testCoverageFilters = new[]
{
	"+[%%PROJECT-NAME%%]*",
	"-[%%PROJECT-NAME%%]%%PROJECT-NAME%%.Properties.*",
	"-[%%PROJECT-NAME%%]%%PROJECT-NAME%%.Models.*",
	"-[%%PROJECT-NAME%%]*System.Text.Json.SourceGeneration*"
};
var testCoverageExcludeAttributes = new[]
{
	"Obsolete",
	"GeneratedCodeAttribute",
	"CompilerGeneratedAttribute",
	"ExcludeFromCodeCoverageAttribute"
};
var testCoverageExcludeFiles = new[]
 {
	"**/AssemblyInfo.cs"
};

var nuGetApiUrl = Argument<string>("NUGET_API_URL", EnvironmentVariable("NUGET_API_URL"));
var nuGetApiKey = Argument<string>("NUGET_API_KEY", EnvironmentVariable("NUGET_API_KEY"));

var gitHubToken = Argument<string>("GITHUB_TOKEN", EnvironmentVariable("GITHUB_TOKEN"));
var gitHubUserName = Argument<string>("GITHUB_USERNAME", EnvironmentVariable("GITHUB_USERNAME"));
var gitHubPassword = Argument<string>("GITHUB_PASSWORD", EnvironmentVariable("GITHUB_PASSWORD"));
var gitHubRepoOwner = Argument<string>("GITHUB_REPOOWNER", EnvironmentVariable("GITHUB_REPOOWNER") ?? gitHubUserName);

var coverallsToken = Argument<string>("COVERALLS_REPO_TOKEN", EnvironmentVariable("COVERALLS_REPO_TOKEN"));
var codecovToken = Argument<string>("CODECOV_TOKEN", EnvironmentVariable("CODECOV_TOKEN"));

var sourceFolder = "./Source/";
var outputDir = "./artifacts/";
var codeCoverageDir = $"{outputDir}CodeCoverage/";
var benchmarkDir = $"{outputDir}Benchmark/";
var coverageFile = $"{codeCoverageDir}coverage.{DefaultFramework}.xml";

var solutionFile = $"{sourceFolder}{libraryName}.sln";
var sourceProject = $"{sourceFolder}{libraryName}/{libraryName}.csproj";
var integrationTestsProject = $"{sourceFolder}{libraryName}.IntegrationTests/{libraryName}.IntegrationTests.csproj";
var unitTestsProject = $"{sourceFolder}{libraryName}.UnitTests/{libraryName}.UnitTests.csproj";
var benchmarkProject = $"{sourceFolder}{libraryName}.Benchmark/{libraryName}.Benchmark.csproj";

var buildBranch = Context.GetBuildBranch();
var repoName = Context.GetRepoName();

var versionInfo = (GitVersion)null; // Will be calculated in SETUP
var milestone = string.Empty; // Will be calculated in SETUP

var cakeVersion = typeof(ICakeContext).Assembly.GetName().Version.ToString();
var isLocalBuild = BuildSystem.IsLocalBuild;
var isMainBranch = StringComparer.OrdinalIgnoreCase.Equals("main", buildBranch);
var isMainRepo = StringComparer.OrdinalIgnoreCase.Equals($"{gitHubRepoOwner}/{gitHubRepo}", repoName);
var isPullRequest = BuildSystem.AppVeyor.Environment.PullRequest.IsPullRequest;
var isTagged = BuildSystem.AppVeyor.Environment.Repository.Tag.IsTag && !string.IsNullOrWhiteSpace(BuildSystem.AppVeyor.Environment.Repository.Tag.Name);
var isIntegrationTestsProjectPresent = FileExists(integrationTestsProject);
var isUnitTestsProjectPresent = FileExists(unitTestsProject);
var isBenchmarkProjectPresent = FileExists(benchmarkProject);

var publishingError = false;

// Generally speaking, we want to honor all the TFM configured in the source project and the unit test project.
// However, there are a few scenarios where a single framework is sufficient. Here are a few examples that come to mind:
// - when building source project on Ubuntu
// - when running unit tests on Ubuntu
// - when calculating code coverage
// FYI, this will cause an error if the source project and/or the unit test project are not configured to target this desired framework:
const string DefaultFramework = "net7.0";
var desiredFramework = (
		!IsRunningOnWindows() ||
		target.Equals("Coverage", StringComparison.OrdinalIgnoreCase) ||
		target.Equals("Run-Code-Coverage", StringComparison.OrdinalIgnoreCase) ||
		target.Equals("Generate-Code-Coverage-Report", StringComparison.OrdinalIgnoreCase) ||
		target.Equals("Upload-Coverage-Result", StringComparison.OrdinalIgnoreCase)
	) ? DefaultFramework : null;


///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(context =>
{
	if (!isLocalBuild && context.Log.Verbosity != Verbosity.Diagnostic)
	{
		Information("Increasing verbosity to diagnostic.");
		context.Log.Verbosity = Verbosity.Diagnostic;
	}

	Information("Calculating version info...");
	versionInfo = GitVersion(new GitVersionSettings() { OutputType = GitVersionOutput.Json });
	milestone = versionInfo.MajorMinorPatch;

	Information("Building version {0} of {1} ({2}, {3}) using version {4} of Cake",
		versionInfo.LegacySemVerPadded,
		libraryName,
		configuration,
		target,
		cakeVersion
	);

	Information("Variables:\r\n\tLocalBuild: {0}\r\n\tIsMainBranch: {1}\r\n\tIsMainRepo: {2}\r\n\tIsPullRequest: {3}\r\n\tIsTagged: {4}",
		isLocalBuild,
		isMainBranch,
		isMainRepo,
		isPullRequest,
		isTagged
	);

	Information("Nuget Info:\r\n\tApi Url: {0}\r\n\tApi Key: {1}",
		nuGetApiUrl,
		string.IsNullOrEmpty(nuGetApiKey) ? "[NULL]" : new string('*', nuGetApiKey.Length)
	);

	if (!string.IsNullOrEmpty(gitHubToken))
	{
		Information("GitHub Info:\r\n\tRepo: {0}\r\n\tUserName: {1}\r\n\tToken: {2}",
			$"{gitHubRepoOwner}/{gitHubRepo}",
			gitHubUserName,
			new string('*', gitHubToken.Length)
		);
	}
	else
	{
		Information("GitHub Info:\r\n\tRepo: {0}\r\n\tUserName: {1}\r\n\tPassword: {2}",
			$"{gitHubRepoOwner}/{gitHubRepo}",
			gitHubUserName,
			string.IsNullOrEmpty(gitHubPassword) ? "[NULL]" : new string('*', gitHubPassword.Length)
		);
	}

	// Integration tests are intended to be used for debugging purposes and not intended to be executed in CI environment.
	// Also, the runner for these tests contains windows-specific code (such as resizing window, moving window to center of screen, etc.)
	// which can cause problems when attempting to run unit tests on an Ubuntu image on AppVeyor.
	if (!isLocalBuild && isIntegrationTestsProjectPresent)
	{
		Information("");
		Information("Removing integration tests");
		DotNetTool(solutionFile, "sln", $"remove {integrationTestsProject.TrimStart(sourceFolder, StringComparison.OrdinalIgnoreCase)}");
	}

	// Similarly, benchmarking can causes problems similar to this one:
	// error NETSDK1005: Assets file '/home/appveyor/projects/stronggrid/Source/StrongGrid.Benchmark/obj/project.assets.json' doesn't have a target for 'net5.0'.
	// Ensure that restore has run and that you have included 'net5.0' in the TargetFrameworks for your project.
	if (!isLocalBuild && isBenchmarkProjectPresent)
	{
		Information("");
		Information("Removing benchmark project");
		DotNetTool(solutionFile, "sln", $"remove {benchmarkProject.TrimStart(sourceFolder, StringComparison.OrdinalIgnoreCase)}");
	}
});

Teardown(context =>
{
	if (!isLocalBuild)
	{
		Information("Restoring projects that may have been removed during build script setup");
		GitCheckout(".", new FilePath[] { solutionFile });
		Information("");
	}

	// Executed AFTER the last task.
	Information("Finished running tasks.");
});


///////////////////////////////////////////////////////////////////////////////
// TASK DEFINITIONS
///////////////////////////////////////////////////////////////////////////////

Task("AppVeyor-Build_Number")
	.WithCriteria(() => AppVeyor.IsRunningOnAppVeyor)
	.Does(() =>
{
	GitVersion(new GitVersionSettings()
	{
		UpdateAssemblyInfo = false,
		OutputType = GitVersionOutput.BuildServer
	});
});

Task("Clean")
	.IsDependentOn("AppVeyor-Build_Number")
	.Does(() =>
{
	// Clean solution directories.
	Information("Cleaning {0}", sourceFolder);
	CleanDirectories($"{sourceFolder}*/bin/{configuration}");
	CleanDirectories($"{sourceFolder}*/obj/{configuration}");

	// Clean previous artifacts
	Information("Cleaning {0}", outputDir);
	if (DirectoryExists(outputDir)) CleanDirectories(MakeAbsolute(Directory(outputDir)).FullPath);
	else CreateDirectory(outputDir);

	// Create folder for code coverage report
	CreateDirectory(codeCoverageDir);
});

Task("Restore-NuGet-Packages")
	.IsDependentOn("Clean")
	.Does(() =>
{
	DotNetRestore("./Source/", new DotNetRestoreSettings
	{
		Sources = new [] {
			"https://api.nuget.org/v3/index.json",
		}
	});
});

Task("Build")
	.IsDependentOn("Restore-NuGet-Packages")
	.Does(() =>
{
	DotNetBuild(solutionFile, new DotNetBuildSettings
	{
		Configuration = configuration,
		Framework =  desiredFramework,
		NoRestore = true,
		MSBuildSettings = new DotNetMSBuildSettings
		{
			Version = versionInfo.LegacySemVerPadded,
			AssemblyVersion = versionInfo.MajorMinorPatch,
			FileVersion = versionInfo.MajorMinorPatch,
			InformationalVersion = versionInfo.InformationalVersion,
			ContinuousIntegrationBuild = true
		}
	});
});

Task("Run-Unit-Tests")
	.WithCriteria(() => isUnitTestsProjectPresent)
	.IsDependentOn("Build")
	.Does(() =>
{
	DotNetTest(unitTestsProject, new DotNetTestSettings
	{
		NoBuild = true,
		NoRestore = true,
		Configuration = configuration,
		Framework = desiredFramework
	});
});

Task("Run-Code-Coverage")
	.WithCriteria(() => isUnitTestsProjectPresent)
	.IsDependentOn("Build")
	.Does(() =>
{
	var testSettings = new DotNetTestSettings
	{
		NoBuild = true,
		NoRestore = true,
		Configuration = configuration,
		Framework = DefaultFramework,

		// The following assumes that coverlet.msbuild has been added to the unit testing project
		ArgumentCustomization = args => args
			.Append("/p:CollectCoverage=true")
			.Append("/p:CoverletOutputFormat=opencover")
			.Append($"/p:CoverletOutput={MakeAbsolute(Directory(codeCoverageDir))}/coverage.xml")	// The name of the framework will be inserted between "coverage" and "xml". This is important to know when uploading the XML file to coveralls/codecov and when generating the HTML report
			.Append($"/p:ExcludeByAttribute={string.Join("%2c", testCoverageExcludeAttributes)}")
			.Append($"/p:ExcludeByFile={string.Join("%2c", testCoverageExcludeFiles)}")
			.Append($"/p:Exclude={string.Join("%2c", testCoverageFilters.Where(filter => filter.StartsWith("-")).Select(filter => filter.TrimStart("-", StringComparison.OrdinalIgnoreCase)))}")
			.Append($"/p:Include={string.Join("%2c", testCoverageFilters.Where(filter => filter.StartsWith("+")).Select(filter => filter.TrimStart("+", StringComparison.OrdinalIgnoreCase)))}")
			.Append("/p:SkipAutoProps=true")
    };

    DotNetTest(unitTestsProject, testSettings);
});

Task("Upload-Coverage-Result-Coveralls")
	.IsDependentOn("Run-Code-Coverage")
    .WithCriteria(() => FileExists(coverageFile))
	.WithCriteria(() => !isLocalBuild)
	.WithCriteria(() => !isPullRequest)
	.WithCriteria(() => isMainRepo)
	.Does(() =>
{
	if(string.IsNullOrEmpty(coverallsToken)) throw new InvalidOperationException("Could not resolve Coveralls token.");

	CoverallsNet(new FilePath(coverageFile), CoverallsNetReportType.OpenCover, new CoverallsNetSettings()
	{
		RepoToken = coverallsToken,
		UseRelativePaths = true
	});
}).OnError (exception =>
{
    Information(exception.Message);
    Information($"Failed to upload coverage result to Coveralls, but continuing with next Task...");
    publishingError = true;
});

Task("Upload-Coverage-Result-Codecov")
	.IsDependentOn("Run-Code-Coverage")
    .WithCriteria(() => FileExists(coverageFile))
	.WithCriteria(() => !isLocalBuild)
	.WithCriteria(() => !isPullRequest)
	.WithCriteria(() => isMainRepo)
	.Does(() =>
{
	if(string.IsNullOrEmpty(codecovToken)) throw new InvalidOperationException("Could not resolve CodeCov token.");

	Codecov(new CodecovSettings
    {
        Files = new[] { coverageFile },
        Token = codecovToken
    });
}).OnError (exception =>
{
    Information(exception.Message);
    Information($"Failed to upload coverage result to Codecov, but continuing with next Task...");
    publishingError = true;
});

Task("Generate-Code-Coverage-Report")
	.IsDependentOn("Run-Code-Coverage")
	.Does(() =>
{
	ReportGenerator(
		new FilePath(coverageFile),
		codeCoverageDir,
		new ReportGeneratorSettings() {
			ClassFilters = new[] { "*.UnitTests*" }
		}
	);
});

Task("Create-NuGet-Package")
	.IsDependentOn("Build")
	.Does(() =>
{
	var releaseNotesUrl = @$"https://github.com/{gitHubRepoOwner}/{gitHubRepo}/releases/tag/{milestone}";

	var settings = new DotNetPackSettings
	{
		Configuration = configuration,
		IncludeSource = false,
		IncludeSymbols = true,
		NoBuild = true,
		NoRestore = true,
		NoDependencies = true,
		OutputDirectory = outputDir,
		SymbolPackageFormat = "snupkg",
		MSBuildSettings = new DotNetMSBuildSettings
		{
			PackageReleaseNotes = releaseNotesUrl,
			PackageVersion = versionInfo.LegacySemVerPadded
		}
	};

	DotNetPack(sourceProject, settings);
});

Task("Upload-AppVeyor-Artifacts")
	.WithCriteria(() => AppVeyor.IsRunningOnAppVeyor)
	.Does(() =>
{
	var allFiles = GetFiles($"{outputDir}*.*") +
		GetFiles($"{benchmarkDir}results/*.*") +
		GetFiles($"{codeCoverageDir}*.*");

	foreach (var file in allFiles)
	{
		AppVeyor.UploadArtifact(file.FullPath);
	}
});

Task("Publish-NuGet")
	.IsDependentOn("Create-NuGet-Package")
	.WithCriteria(() => !isLocalBuild)
	.WithCriteria(() => !isPullRequest)
	.WithCriteria(() => isMainRepo)
	.WithCriteria(() => isMainBranch)
	.WithCriteria(() => isTagged)
	.Does(() =>
{
	if(string.IsNullOrEmpty(nuGetApiKey)) throw new InvalidOperationException("Could not resolve NuGet API key.");
	if(string.IsNullOrEmpty(nuGetApiUrl)) throw new InvalidOperationException("Could not resolve NuGet API url.");

	var settings = new DotNetNuGetPushSettings
	{
    	Source = nuGetApiUrl,
	    ApiKey = nuGetApiKey
	};

	foreach(var package in GetFiles(outputDir + "*.nupkg"))
	{
		DotNetNuGetPush(package, settings);
	}
});

Task("Create-Release-Notes")
	.Does(() =>
{
	if (string.IsNullOrEmpty(gitHubToken))
	{
		throw new InvalidOperationException("GitHub token was not provided.");
	}

	GitReleaseManagerCreate(gitHubToken, gitHubRepoOwner, gitHubRepo, new GitReleaseManagerCreateSettings
	{
		Name            = milestone,
		Milestone       = milestone,
		Prerelease      = false,
		TargetCommitish = "main",
		Verbose         = true
	});
});

Task("Publish-GitHub-Release")
	.WithCriteria(() => !isLocalBuild)
	.WithCriteria(() => !isPullRequest)
	.WithCriteria(() => isMainRepo)
	.WithCriteria(() => isMainBranch)
	.WithCriteria(() => isTagged)
	.Does(() =>
{
	if (string.IsNullOrEmpty(gitHubToken))
	{
		throw new InvalidOperationException("GitHub token was not provided.");
	}

	GitReleaseManagerClose(gitHubToken, gitHubRepoOwner, gitHubRepo, milestone, new GitReleaseManagerCloseMilestoneSettings
	{
		Verbose = true
	});
});

Task("Generate-Benchmark-Report")
	.IsDependentOn("Build")
	.WithCriteria(isBenchmarkProjectPresent)
	.Does(() =>
{
    var publishDirectory = $"{benchmarkDir}Publish/";
    var publishedAppLocation = MakeAbsolute(File($"{publishDirectory}{libraryName}.Benchmark.exe")).FullPath;
    var artifactsLocation = MakeAbsolute(File(benchmarkDir)).FullPath;

    DotNetPublish(benchmarkProject, new DotNetPublishSettings
    {
        Configuration = configuration,
		NoRestore = true,
        NoBuild = true,
        OutputDirectory = publishDirectory
    });

	using (DiagnosticVerbosity())
    {
        var processResult = StartProcess(
            publishedAppLocation,
            new ProcessSettings()
            {
                Arguments = $"-f * --artifacts={artifactsLocation}"
            });
        if (processResult != 0)
        {
            throw new Exception($"dotnet-benchmark.exe did not complete successfully. Result code: {processResult}");
        }
    }
});


///////////////////////////////////////////////////////////////////////////////
// TARGETS
///////////////////////////////////////////////////////////////////////////////

Task("Coverage")
	.IsDependentOn("Generate-Code-Coverage-Report")
	.Does(() =>
{
	StartProcess("cmd", $"/c start {codeCoverageDir}index.htm");
});

Task("Benchmark")
	.IsDependentOn("Generate-Benchmark-Report")
	.WithCriteria(isBenchmarkProjectPresent)
	.Does(() =>
{
    var htmlReports = GetFiles($"{benchmarkDir}results/*-report.html", new GlobberSettings { IsCaseSensitive = false });
	foreach (var htmlReport in htmlReports)
	{
		StartProcess("cmd", $"/c start {htmlReport}");
	}
});

Task("ReleaseNotes")
	.IsDependentOn("Create-Release-Notes");

Task("AppVeyor")
	.IsDependentOn("Run-Code-Coverage")
	.IsDependentOn("Upload-Coverage-Result-Coveralls")
	.IsDependentOn("Upload-Coverage-Result-Codecov")
	.IsDependentOn("Create-NuGet-Package")
	.IsDependentOn("Upload-AppVeyor-Artifacts")
	.IsDependentOn("Publish-NuGet")
	.IsDependentOn("Publish-GitHub-Release")
    .Finally(() =>
{
    if (publishingError)
    {
         Warning("At least one exception occurred when executing non-essential tasks. These exceptions were ignored in order to allow the build script to complete.");
    }
});

Task("Default")
	.IsDependentOn("Run-Unit-Tests")
	.IsDependentOn("Create-NuGet-Package");


///////////////////////////////////////////////////////////////////////////////
// EXECUTION
///////////////////////////////////////////////////////////////////////////////

RunTarget(target);



///////////////////////////////////////////////////////////////////////////////
// PRIVATE METHODS
///////////////////////////////////////////////////////////////////////////////
private static string TrimStart(this string source, string value, StringComparison comparisonType)
{
	if (source == null)
	{
		throw new ArgumentNullException(nameof(source));
	}

	int valueLength = value.Length;
	int startIndex = 0;
	while (source.IndexOf(value, startIndex, comparisonType) == startIndex)
	{
		startIndex += valueLength;
	}

	return source.Substring(startIndex);
}

private static List<string> ExecuteCommand(this ICakeContext context, FilePath exe, string args)
{
    context.StartProcess(exe, new ProcessSettings { Arguments = args, RedirectStandardOutput = true }, out var redirectedOutput);

    return redirectedOutput.ToList();
}

private static List<string> ExecGitCmd(this ICakeContext context, string cmd)
{
    var gitExe = context.Tools.Resolve(context.IsRunningOnWindows() ? "git.exe" : "git");
    return context.ExecuteCommand(gitExe, cmd);
}

private static string GetBuildBranch(this ICakeContext context)
{
    var buildSystem = context.BuildSystem();
    string repositoryBranch = null;

    if (buildSystem.IsRunningOnAppVeyor) repositoryBranch = buildSystem.AppVeyor.Environment.Repository.Branch;
    else if (buildSystem.IsRunningOnAzurePipelines) repositoryBranch = buildSystem.AzurePipelines.Environment.Repository.SourceBranchName;
    else if (buildSystem.IsRunningOnBamboo) repositoryBranch = buildSystem.Bamboo.Environment.Repository.Branch;
    else if (buildSystem.IsRunningOnBitbucketPipelines) repositoryBranch = buildSystem.BitbucketPipelines.Environment.Repository.Branch;
    else if (buildSystem.IsRunningOnBitrise) repositoryBranch = buildSystem.Bitrise.Environment.Repository.GitBranch;
    else if (buildSystem.IsRunningOnGitHubActions) repositoryBranch = buildSystem.GitHubActions.Environment.Workflow.Ref.Replace("refs/heads/", "");
    else if (buildSystem.IsRunningOnGitLabCI) repositoryBranch = buildSystem.GitLabCI.Environment.Build.RefName;
    else if (buildSystem.IsRunningOnTeamCity) repositoryBranch = buildSystem.TeamCity.Environment.Build.BranchName;
    else if (buildSystem.IsRunningOnTravisCI) repositoryBranch = buildSystem.TravisCI.Environment.Build.Branch;
	else repositoryBranch = ExecGitCmd(context, "rev-parse --abbrev-ref HEAD").Single();

    return repositoryBranch;
}

public static string GetRepoName(this ICakeContext context)
{
    var buildSystem = context.BuildSystem();

    if (buildSystem.IsRunningOnAppVeyor) return buildSystem.AppVeyor.Environment.Repository.Name;
    else if (buildSystem.IsRunningOnAzurePipelines) return buildSystem.AzurePipelines.Environment.Repository.RepoName;
    else if (buildSystem.IsRunningOnTravisCI) return buildSystem.TravisCI.Environment.Repository.Slug;
    else if (buildSystem.IsRunningOnGitHubActions) return buildSystem.GitHubActions.Environment.Workflow.Repository;

	var originUrl = ExecGitCmd(context, "config --get remote.origin.url").Single();
	var parts = originUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
	return $"{parts[parts.Length - 2]}/{parts[parts.Length - 1].Replace(".git", "")}";
}
