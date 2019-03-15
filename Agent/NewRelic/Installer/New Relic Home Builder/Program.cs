﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using JetBrains.Annotations;
using MoreLinq;

namespace NewRelic.Installer
{
	public class Program
	{
		private const string HomeDirectoryNamePrefix = "New Relic Home ";
		private const string ProfilerSoFileName = "libNewRelicProfiler.so";

		// ReSharper disable MemberCanBePrivate.Global
		// ReSharper disable UnusedAutoPropertyAccessor.Global
		[CommandLine.Option("solution", Required = true, HelpText = "$(SolutionDir)")]
		[NotNull]
		public String SolutionPath { get; set; }

		[CommandLine.Option("configuration", Required = false, HelpText = "$(Configuration)")]
		[NotNull]
		public String Configuration { get; set; }

		private bool _isCoreClr = false;
		private bool _isLinux = false;

		[NotNull]
		public String Bitness { get; set; }


		// ReSharper restore UnusedAutoPropertyAccessor.Global
		// ReSharper restore MemberCanBePrivate.Global

		// output paths
		[NotNull]
		private String DestinationHomeDirectoryName {
			get
			{
				var name = HomeDirectoryNamePrefix + Bitness;
				if (_isCoreClr)
				{
					name += " CoreClr";
				}
				if (_isLinux)
				{
					name += "_Linux";
				}

				return name;
			}
		}
		[NotNull]
		private String DestinationHomeDirectoryPath { get { return Path.Combine(SolutionPath, DestinationHomeDirectoryName); } }
		[NotNull]
		private String DestinationAgentFilePath { get { return Path.Combine(DestinationHomeDirectoryPath, "NewRelic.Agent.Core.dll"); } }
		[NotNull]
		private string DestinationProfilerDllPath => Path.Combine(DestinationHomeDirectoryPath, "NewRelic.Profiler.dll");

		[NotNull]
		private string DestinationProfilerSoPath => Path.Combine(DestinationHomeDirectoryPath, ProfilerSoFileName);

		[NotNull]
		private String DestinationExtensionsDirectoryPath { get { return Path.Combine(DestinationHomeDirectoryPath, "Extensions"); } }
		[NotNull]
		private String DestinationRegistryFileName { get { return String.Format("New Relic Home {0}.reg", Bitness); } }
		[NotNull]
		private String DestinationRegistryFilePath { get { return Path.Combine(SolutionPath, DestinationRegistryFileName); } }
		[NotNull]
		private String DestinationNewRelicConfigXsdPath { get { return Path.Combine(DestinationHomeDirectoryPath, "newrelic.xsd"); } }
		[NotNull]
		private String BuildOutputPath { get { return Path.Combine(SolutionPath, "_build"); } }
		[NotNull]
		private String AnyCpuBuildPath { get { return Path.Combine(BuildOutputPath, AnyCpuBuildDirectoryName); } }

		// input paths
		[NotNull]
		private String AnyCpuBuildDirectoryName { get { return String.Format("AnyCPU-{0}", Configuration); } }
		[NotNull]
		private String NewRelicConfigPath { get { return Path.Combine(SolutionPath, "Configuration", "newrelic.config") ?? String.Empty; } }
		[NotNull]
		private String NewRelicConfigXsdPath { get { return Path.Combine(SolutionPath, "NewRelic", "Agent", "Core", "Config", "Configuration.xsd"); } }
		[NotNull]
		private String ExtensionsXsdPath { get { return Path.Combine(SolutionPath, "NewRelic", "Agent", "Core", "NewRelic.Agent.Core.Extension", "extension.xsd"); } }

		private string LicenseFilePath => Path.Combine(SolutionPath, "Miscellaneous", "License.txt");
		private string MiscInstrumentationFilePath => Path.Combine(SolutionPath, "Miscellaneous", "NewRelic.Providers.Wrapper.Misc.Instrumentation.xml");

		private string Core20ReadmeFileName = "netcore20-agent-readme.md";
		private string ReadmeFilePath => Path.Combine(SolutionPath, "Miscellaneous", Core20ReadmeFileName);

		private string AgentApiPath => Path.Combine(AnyCpuBuildPath, "NewRelic.Api.Agent", _isCoreClr ? "netstandard2.0" : "net45", "NewRelic.Api.Agent.dll");

		private string AgentVersion => FileVersionInfo.GetVersionInfo(DestinationAgentFilePath).FileVersion;

		[NotNull]
		private string ProfilerDllPath
		{
			get
			{
				var profilerPath = Path.Combine(SolutionPath, "NewRelic", "Profiler", "Profiler", "bin", $"{Bitness}", $"{Configuration}", "NewRelic.Profiler.dll");

				if (!File.Exists(profilerPath))
				{
					profilerPath = Path.Combine(SolutionPath, "ProfilerBuildsForDevMachines", "Windows", $"{Bitness}", "NewRelic.Profiler.dll");
				}

				return profilerPath;
			}
		}

		[NotNull]
		private string ProfilerSoPath
		{
			get
			{
				var profilerPath = Path.Combine(SolutionPath, "NewRelic", "Profiler", ProfilerSoFileName);

				if (!File.Exists(profilerPath))
				{
					profilerPath = Path.Combine(SolutionPath, "ProfilerBuildsForDevMachines", "Linux", ProfilerSoFileName);
				}

				return profilerPath;
			}
		}

		[NotNull]
		private String CoreBuildDirectoryPath { get { return Path.Combine(AnyCpuBuildPath, @"NewRelic.Agent.Core", _isCoreClr ? "netstandard2.0" : "net45"); } }

		private string ILRepackedNewRelicAgentCorePath { get { return Path.Combine(CoreBuildDirectoryPath + "-ILRepacked", "NewRelic.Agent.Core.dll"); } }
		private String NewRelicAgentExtensionsPath { get { return Path.Combine(CoreBuildDirectoryPath, "NewRelic.Agent.Extensions.dll"); } }
		[NotNull]
		private String ExtensionsDirectoryPath { get { return Path.Combine(SolutionPath, "NewRelic", "Agent", "Extensions"); } }

		void RealMain()
		{
			DoWork(bitness: "x86", isCoreClr: false);
			DoWork(bitness: "x64", isCoreClr: false);
			DoWork(bitness: "x86", isCoreClr: true);
			DoWork(bitness: "x64", isCoreClr: true);
			DoWork(bitness: "x64", isCoreClr: true, isLinux: true);
		}

		private void DoWork(string bitness, bool isCoreClr, bool isLinux = false)
		{
			Bitness = bitness;
			_isCoreClr = isCoreClr;
			_isLinux = isLinux;

			var frameworkMsg = _isCoreClr ? "CoreCLR" : ".NETFramework";
			frameworkMsg += _isLinux ? " Linux" : "";
			Console.WriteLine($"[HomeBuilder]: Building home for {frameworkMsg} {bitness}");

			Console.WriteLine("[HomeBuilder]: attempting to read and restore CustomInstrumentation.xml");

			var customInstrumentationFilePath = Path.Combine(DestinationExtensionsDirectoryPath, "CustomInstrumentation.xml");
			byte[] customInstrumentationBytes = ReadCustomInstrumentationBytes(customInstrumentationFilePath);

			ReCreateDirectoryWithEveryoneAccess(DestinationHomeDirectoryPath);

			Directory.CreateDirectory(DestinationExtensionsDirectoryPath);
			Directory.CreateDirectory(Path.Combine(DestinationExtensionsDirectoryPath, "netstandard2.0"));
			Directory.CreateDirectory(Path.Combine(DestinationExtensionsDirectoryPath, "net46"));

			CopyProfiler(isLinux);

			File.Copy(NewRelicConfigXsdPath, DestinationNewRelicConfigXsdPath, true);
			CopyToDirectory(ILRepackedNewRelicAgentCorePath, DestinationHomeDirectoryPath);
			CopyToDirectory(NewRelicConfigPath, DestinationHomeDirectoryPath);
			CopyToDirectory(ExtensionsXsdPath, DestinationExtensionsDirectoryPath);
			CopyToDirectory(NewRelicAgentExtensionsPath, DestinationHomeDirectoryPath);
			CopyAgentExtensions();
			CopyOtherDependencies();

			var shouldCreateRegistryFile = (isCoreClr == false);
			if (shouldCreateRegistryFile)
			{
				CreateRegistryFile();
			}

			if (customInstrumentationBytes != null)
			{
				File.WriteAllBytes(customInstrumentationFilePath, customInstrumentationBytes);
			}
		}

		private void CopyProfiler(bool isLinux = false)
		{
			if (isLinux)
			{
				var soExists = File.Exists(ProfilerSoPath);
				if (soExists)
				{
					Console.WriteLine($"[HomeBuilder]: Copying Linux profiler Shared Object (so) from: {ProfilerSoPath} to: {DestinationProfilerSoPath}");
					File.Copy(ProfilerSoPath, DestinationProfilerSoPath, true);
				}
				else
				{
					Console.WriteLine($"[HomeBuilder]: *** Did not find Linux profiler Shared Object (so) at path: {ProfilerSoPath} ***");
				}
			}
			else
			{
				Console.WriteLine($"[HomeBuilder]: Copying Windows profiler DLL from: {ProfilerDllPath} to: {DestinationProfilerDllPath}");

				File.Copy(ProfilerDllPath, DestinationProfilerDllPath, true);
			}
		}

		private byte[] ReadCustomInstrumentationBytes(string customInstrumentationFilePath)
		{
			byte[] customInstrumentationBytes = null;

			if (File.Exists(customInstrumentationFilePath))
			{
				customInstrumentationBytes = File.ReadAllBytes(customInstrumentationFilePath);
			}

			return customInstrumentationBytes;
		}

		private void CopyOtherDependencies()
		{
			CopyToDirectory(MiscInstrumentationFilePath, DestinationExtensionsDirectoryPath);

			if (_isCoreClr)
			{
				CopyToDirectory(LicenseFilePath, DestinationHomeDirectoryPath);
				CopyToDirectory(AgentApiPath, DestinationHomeDirectoryPath);
				CopyToDirectory(ReadmeFilePath, DestinationHomeDirectoryPath);
				File.Move(Path.Combine(DestinationHomeDirectoryPath, Core20ReadmeFileName), Path.Combine(DestinationHomeDirectoryPath, "README.md"));
				return;
			}

			// We copy JetBrains Annotations to the output extension folder because many of the extensions use it. Even though it does not need to be there for the extensions to work, sometimes our customers will use frameworks that do assembly scanning (such as EpiServer) that will panic when references are unresolved.
			var jetBrainsAnnotationsAssemblyPath = Path.Combine(CoreBuildDirectoryPath, "JetBrains.Annotations.dll");
			CopyToDirectory(jetBrainsAnnotationsAssemblyPath, DestinationExtensionsDirectoryPath);
		}

		private static void ReCreateDirectoryWithEveryoneAccess(String directoryPath)
		{
			try { Directory.Delete(directoryPath, true); }
			catch (DirectoryNotFoundException) { }

			Thread.Sleep(TimeSpan.FromMilliseconds(1));

			var directoryInfo = Directory.CreateDirectory(directoryPath);
			var directorySecurity = directoryInfo.GetAccessControl();
			var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
			directorySecurity.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
			directoryInfo.SetAccessControl(directorySecurity);
		}

		private static void CopyToDirectory([NotNull] String sourceFilePath, [NotNull] String destinationDirectoryPath)
		{
			if (sourceFilePath == null)
				throw new ArgumentNullException("sourceFilePath");
			if (destinationDirectoryPath == null)
				throw new ArgumentNullException("destinationDirectoryPath");

			CopyToDirectories(sourceFilePath, new[] { destinationDirectoryPath });
		}

		private static void CopyToDirectories([NotNull] String sourceFilePath, [NotNull] IEnumerable<String> destinationDirectoryPaths)
		{
			if (sourceFilePath == null)
				throw new ArgumentNullException("sourceFilePath");
			if (destinationDirectoryPaths == null)
				throw new ArgumentNullException("destinationDirectoryPaths");

			var fileName = Path.GetFileName(sourceFilePath);
			destinationDirectoryPaths
				.Where(destinationDirectoryPath => destinationDirectoryPath != null)
				.Select(destinationDirectoryPath => Path.Combine(destinationDirectoryPath, fileName))
				.Where(destinationFilePath => destinationFilePath != null)
				.ToList()
				.ForEach(destinationFilePath => File.Copy(sourceFilePath, destinationFilePath, true));
		}

		private void CopyAgentExtensions()
		{
			var directoriesWithoutFramework = Directory.EnumerateDirectories(ExtensionsDirectoryPath, Configuration, SearchOption.AllDirectories);

			List<string> allDirectoriesForConfiguration = new List<String>(directoriesWithoutFramework);
			foreach (var directory in directoriesWithoutFramework)
			{
				var frameworkSubDirectories = Directory.EnumerateDirectories(directory, "*net*");
				allDirectoriesForConfiguration.AddRange(frameworkSubDirectories);
			}

			var netstandardProjectsToIncludeInBothAgents = new[] {"AspNetCore"};

			var directories = allDirectoriesForConfiguration.ToList()
				.Where(directoryPath => directoryPath != null)
				.Where(directoryPath => directoryPath.Contains("netstandard") == _isCoreClr || netstandardProjectsToIncludeInBothAgents.Any(directoryPath.Contains))
				.Select(directoryPath => new DirectoryInfo(directoryPath))
				.Where(directoryInfo => directoryInfo.Parent != null)
				.Where(directoryInfo => directoryInfo.Parent.Name == "bin" || directoryInfo.Parent.Name == Configuration);

			var dlls = directories
				.SelectMany(directoryInfo => directoryInfo.EnumerateFiles("*.dll"))
				.Where(fileInfo => fileInfo != null)
				.DistinctBy(fileInfo => fileInfo.Name)
				.Where(fileInfo => fileInfo != null)
				.Select(fileInfo => fileInfo.FullName)
				.Where(filePath => filePath != null)
				.Where(filePath => FileVersionInfo.GetVersionInfo(filePath).FileVersion == AgentVersion);

			dlls.ForEach(filePath =>
			{
				var destination = DestinationExtensionsDirectoryPath;

				if (filePath.Contains("netstandard"))
				{
					destination = Path.Combine(destination, "netstandard2.0");
				}
				else if (filePath.Contains("net46"))
				{
					destination = Path.Combine(destination, "net46");
				}
				CopyNewRelicAssemblies(filePath, destination);
				TryCopyExtensionInstrumentationFile(filePath, DestinationExtensionsDirectoryPath);
			});
		}

		private static void CopyNewRelicAssemblies([NotNull] String assemblyFilePath, [NotNull] String destinationExtensionsDirectoryPath)
		{
			var directoryPath = Path.GetDirectoryName(assemblyFilePath);
			if (directoryPath == null)
				return;

			var directoryInfo = new DirectoryInfo(directoryPath);

			directoryInfo
				.EnumerateFiles("NewRelic.*.dll")
				.Where(fileInfo => fileInfo != null)
				.Where(fileInfo => fileInfo.Name != "NewRelic.Agent.Extensions.dll")
				.Where(fileInfo => !fileInfo.Name.EndsWith("Tests.dll"))
				.Select(fileInfo => fileInfo.FullName)
				.Where(filePath => filePath != null)
				.ForEach(filePath => CopyToDirectory(filePath, destinationExtensionsDirectoryPath));
		}

		private static void TryCopyExtensionInstrumentationFile([NotNull] String assemblyFilePath, [NotNull] String destinationExtensionsDirectoryPath)
		{
			var directory = Path.GetDirectoryName(assemblyFilePath);
			if (directory == null)
				return;

			var instrumentationFilePath = Path.Combine(directory, "Instrumentation.xml");
			if (!File.Exists(instrumentationFilePath))
				return;

			var assemblyName = Path.GetFileNameWithoutExtension(assemblyFilePath);
			if (assemblyName == null)
				return;

			var destinationFilePath = Path.Combine(destinationExtensionsDirectoryPath, assemblyName + ".Instrumentation.xml");

			File.Copy(instrumentationFilePath, destinationFilePath, true);
		}

		private void CreateRegistryFile()
		{
			// TODO: create .reg files for switching development between the two outside of the two folders
			var strings = new[]
			{
				@"COR_ENABLE_PROFILING=1",
				@"COR_PROFILER={71DA0A04-7777-4EC6-9643-7D28B46A8A41}",
				String.Format(@"COR_PROFILER_PATH={0}", DestinationProfilerDllPath),
				String.Format(@"NEWRELIC_HOME={0}\", DestinationHomeDirectoryPath)
			};

			var bytes = new List<Byte>();
			foreach (var @string in strings)
			{
				if (@string == null)
					continue;
				bytes.AddRange(Encoding.Unicode.GetBytes(@string));
				bytes.AddRange(new Byte[] { 0, 0 });
			}
			bytes.AddRange(new Byte[] { 0, 0 });

			var hexString = BitConverter.ToString(bytes.ToArray()).Replace('-', ',');
			const string fileContentsFormatter =
@"Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\W3SVC]
""Environment""=hex(7):{0}

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WAS]
""Environment""=hex(7):{0}
";
			var fileContents = String.Format(fileContentsFormatter, hexString);
			File.WriteAllText(DestinationRegistryFilePath, fileContents);
		}

		static void Main(string[] args)
		{
			var program = new Program();
			program.ParseCommandLineArguments(args);
			program.RealMain();
		}

		private void ParseCommandLineArguments(string[] commandLineArguments)
		{
			var defaultParser = CommandLine.Parser.Default;
			if (defaultParser == null)
				throw new NullReferenceException("defaultParser");

			defaultParser.ParseArgumentsStrict(commandLineArguments, this);
		}
	}
}
