﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Squirrel.SimpleSplat;

namespace Squirrel
{
    public enum DotnetRuntimeType
    {
        DotNet = 1,
        AspNetCore,
        WindowsDesktop,
        SDK,
    }

    public enum RuntimeCpu
    {
        Unknown = 0,
        X86 = 1,
        X64 = 2,
    }

    public enum RuntimeInstallResult
    {
        Success = 0,
        UserCancelled = 1602,
        AnotherInstallInProgress = 1618,
        RestartRequired = 3010,
        SystemDoesNotMeetRequirements = 5100,
    }

    public abstract class RuntimeInfo
    {
        public string Id { get; }
        public string DisplayName { get; }

        static IFullLogger Log = SquirrelLocator.Current.GetService<ILogManager>().GetLogger(typeof(RuntimeInfo));

        protected RuntimeInfo(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public abstract Task<string> GetDownloadUrl();

        public abstract Task<bool> CheckIsInstalled();

        public abstract Task<bool> CheckIsSupported();

        public virtual async Task DownloadToFile(string localPath, Action<DownloadProgressChangedEventArgs> progress = null)
        {
            var url = await GetDownloadUrl();
            Log.Info($"Downloading {Id} from {url} to {localPath}");
            using var wc = Utility.CreateWebClient();
            wc.DownloadProgressChanged += (s, e) => { progress?.Invoke(e); };
            await wc.DownloadFileTaskAsync(url, localPath);
        }

        public virtual async Task<RuntimeInstallResult> InvokeInstaller(string pathToInstaller, bool isQuiet)
        {
            var args = new string[] { "/passive", "/norestart", "/showrmui" };
            var quietArgs = new string[] { "/q", "/norestart" };
            Log.Info($"Running {Id} installer '{pathToInstaller} {string.Join(" ", args)}'");
            var p = await Utility.InvokeProcessAsync(pathToInstaller, isQuiet ? quietArgs : args, CancellationToken.None);

            // https://johnkoerner.com/install/windows-installer-error-codes/

            if (p.ExitCode == 1638) // a newer compatible version is already installed
                return RuntimeInstallResult.Success;

            if (p.ExitCode == 1641) // installer initiated a restart
                return RuntimeInstallResult.RestartRequired;

            return (RuntimeInstallResult) p.ExitCode;
        }

        public override string ToString() => $"[{Id}] {DisplayName}";

        public override int GetHashCode() => Id.GetHashCode();
    }

    public class FxRuntimeInfo : RuntimeInfo
    {
        public string DownloadUrl { get; }
        public int ReleaseVersion { get; }

        private const string ndpPath = "SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full";

        public FxRuntimeInfo(string id, string displayName, string downloadUrl, int releaseVersion) : base(id, displayName)
        {
            DownloadUrl = downloadUrl;
            ReleaseVersion = releaseVersion;
        }

        public override Task<string> GetDownloadUrl()
        {
            return Task.FromResult(DownloadUrl);
        }

        public override Task<bool> CheckIsSupported()
        {
            // TODO use IsWindowsVersionOrGreater function to verify it can be installed on this machine
            return Task.FromResult(true);
        }

        public override Task<bool> CheckIsInstalled()
        {
            using var view = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var key = view.OpenSubKey(ndpPath);
            if (key == null) return Task.FromResult(false);

            var dwRelease = key.GetValue("Release") as int?;
            if (dwRelease == null) return Task.FromResult(false);

            return Task.FromResult(dwRelease.Value >= ReleaseVersion);
        }
    }

    public class DotnetRuntimeInfo : RuntimeInfo
    {
        public string RequiredVersion { get; }
        public RuntimeCpu CpuArchitecture { get; }

        public DotnetRuntimeInfo(string id, string displayName, string version, RuntimeCpu architecture) : base(id, displayName)
        {
            RequiredVersion = version;
            CpuArchitecture = architecture;
        }

        private const string UncachedDotNetFeed = "https://dotnetcli.blob.core.windows.net/dotnet";
        private const string DotNetFeed = "https://dotnetcli.azureedge.net/dotnet";

        public override async Task<bool> CheckIsInstalled()
        {
            switch (CpuArchitecture) {

            case RuntimeCpu.X64: return await CheckIsInstalledX64();
            case RuntimeCpu.X86: return CheckIsInstalledX86();
            default: return false;

            }
        }

        public override Task<bool> CheckIsSupported()
        {
            if (CpuArchitecture == RuntimeCpu.X64 && !Environment.Is64BitOperatingSystem)
                return Task.FromResult(false);

            // TODO use IsWindowsVersionOrGreater function to verify it can be installed on this machine
            return Task.FromResult(true);
        }

        private bool CheckIsInstalledX86()
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return CheckIsInstalledInBaseDirectory(pf86);
        }

        private async Task<bool> CheckIsInstalledX64()
        {
            if (!Environment.Is64BitOperatingSystem)
                return false;

            // we are probably an x86 process, and I don't know of any great ways to
            // get the x64 ProgramFiles directory from an x86 process, so this code
            // is extremely unfortunate.

            if (Environment.Is64BitProcess) {
                // this only works in a 64 bit process, otherwise it points to ProgramFilesX86
                var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (CheckIsInstalledInBaseDirectory(pf64))
                    return true;
            }

            // https://docs.microsoft.com/en-us/windows/win32/winprog64/wow64-implementation-details
            var pf64compat = Environment.GetEnvironmentVariable("ProgramW6432");
            if (Directory.Exists(pf64compat))
                return CheckIsInstalledInBaseDirectory(pf64compat);

            // on a 64 bit operating system, the dotnet cli should be x64, and will only
            // return x64 results, so we can ask it as a last resort
            try {
                var token = new CancellationTokenSource(2000).Token;
                var output = await Utility.InvokeProcessAsync("dotnet", new[] { "--info" }, token);
                if (output.ExitCode != 0) return false;
                return output.StdOutput.Contains("Microsoft.WindowsDesktop.App " + RequiredVersion);
            } catch (Win32Exception wex) when (wex.HResult == -2147467259) {
                return false; // executable not found
            }
        }

        private bool CheckIsInstalledInBaseDirectory(string baseDirectory)
        {
            var directory = Path.Combine(baseDirectory, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(directory))
                return false;

            var myVer = new Version(RequiredVersion);

            var dirs = Directory.EnumerateDirectories(directory)
                .Select(d => Path.GetFileName(d))
                .Where(d => Version.TryParse(d, out var _))
                .Select(d => Version.Parse(d));

            return dirs.Any(v => v.Major == myVer.Major && v.Minor == myVer.Minor);
        }

        public override async Task<string> GetDownloadUrl()
        {
            var latest = await GetLatestDotNetVersion(DotnetRuntimeType.WindowsDesktop, RequiredVersion);
            var architecture = CpuArchitecture switch {
                RuntimeCpu.X86 => "x86",
                RuntimeCpu.X64 => "x64",
                _ => throw new ArgumentOutOfRangeException(nameof(CpuArchitecture)),
            };

            return GetDotNetDownloadUrl(DotnetRuntimeType.WindowsDesktop, latest, architecture);
        }

        /// <summary>
        /// Get latest available version of dotnet. Channel can be 'LTS', 'current', or a two part version 
        /// (eg. '6.0') to get the latest minor release.
        /// </summary>
        public static async Task<string> GetLatestDotNetVersion(DotnetRuntimeType runtimeType, string channel)
        {
            // https://github.com/dotnet/install-scripts/blob/main/src/dotnet-install.ps1#L427
            // these are case sensitive
            string runtime = runtimeType switch {
                DotnetRuntimeType.DotNet => "dotnet",
                DotnetRuntimeType.AspNetCore => "aspnetcore",
                DotnetRuntimeType.WindowsDesktop => "WindowsDesktop",
                DotnetRuntimeType.SDK => "Sdk",
                _ => throw new NotImplementedException(),
            };

            using var wc = Utility.CreateWebClient();
            return await wc.DownloadStringTaskAsync(new Uri($"{UncachedDotNetFeed}/{runtime}/{channel}/latest.version"));
        }

        /// <summary>
        /// Get download url for a specific version of dotnet. Version must be an absolute version, such as one
        /// returned by <see cref="GetLatestDotNetVersion(DotnetRuntimeType, string)"/>. cpuarch should be either
        /// 'x86', 'x64', or 'arm64'.
        /// </summary>
        public static string GetDotNetDownloadUrl(DotnetRuntimeType runtimeType, string version, string cpuarch)
        {
            // https://github.com/dotnet/install-scripts/blob/main/src/dotnet-install.ps1#L619
            return runtimeType switch {
                DotnetRuntimeType.DotNet => $"{DotNetFeed}/Runtime/{version}/dotnet-runtime-{version}-win-{cpuarch}.exe",
                DotnetRuntimeType.AspNetCore => $"{DotNetFeed}/aspnetcore/Runtime/{version}/aspnetcore-runtime-{version}-win-{cpuarch}.exe",
                DotnetRuntimeType.WindowsDesktop =>
                    new Version(version).Major >= 5
                        ? $"{DotNetFeed}/WindowsDesktop/{version}/windowsdesktop-runtime-{version}-win-{cpuarch}.exe"
                        : $"{DotNetFeed}/Runtime/{version}/windowsdesktop-runtime-{version}-win-{cpuarch}.exe",
                DotnetRuntimeType.SDK => $"{DotNetFeed}/Sdk/{version}/dotnet-sdk-{version}-win-{cpuarch}.exe",
                _ => throw new NotImplementedException(),
            };
        }
    }

    public class VCredistRuntimeInfo : RuntimeInfo
    {
        public Version MinVersion { get; }
        public RuntimeCpu CpuArchitecture { get; }

        public VCredistRuntimeInfo(string id, string displayName, Version minVersion, RuntimeCpu cpuArchitecture) : base(id, displayName)
        {
            MinVersion = minVersion;
            CpuArchitecture = cpuArchitecture;
        }

        public override Task<bool> CheckIsInstalled()
        {
            return Task.FromResult(GetInstalledVCVersions().Any(
                v => v.Cpu == CpuArchitecture &&
                v.Ver.Major == MinVersion.Major &&
                v.Ver >= MinVersion));
        }

        public override Task<bool> CheckIsSupported()
        {
            if (CpuArchitecture == RuntimeCpu.X64 && !Environment.Is64BitOperatingSystem)
                return Task.FromResult(false);

            // TODO use IsWindowsVersionOrGreater function to verify it can be installed on this machine
            return Task.FromResult(true);
        }

        const string UninstallRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        public static (Version Ver, RuntimeCpu Cpu)[] GetInstalledVCVersions()
        {
            List<(Version Ver, RuntimeCpu Cpu)> results = new List<(Version Ver, RuntimeCpu Cpu)>();

            void searchreg(RegistryKey view)
            {
                foreach (var kn in view.GetSubKeyNames()) {
                    var subKey = view.OpenSubKey(kn);
                    var name = subKey.GetValue("DisplayName") as string;
                    if (name != null && name.Contains("Microsoft Visual C++") && name.Contains("Redistributable")) {
                        var version = subKey.GetValue("DisplayVersion") as string;
                        if (Version.TryParse(version, out var v)) {
                            // these entries do not get added into the correct registry hive, so we need to determine
                            // the cpu architecture from the name. I hate this but what can I do?
                            if (name.Contains("x64") && Environment.Is64BitOperatingSystem) {
                                results.Add((v, RuntimeCpu.X64));
                            } else {
                                results.Add((v, RuntimeCpu.X86));
                            }
                        }
                    }
                }
            }

            using var view86 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
               .CreateSubKey(UninstallRegSubKey, RegistryKeyPermissionCheck.ReadSubTree);
            searchreg(view86);

            if (Environment.Is64BitOperatingSystem) {
                using var view64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .CreateSubKey(UninstallRegSubKey, RegistryKeyPermissionCheck.ReadSubTree);
                searchreg(view64);
            }

            return results.OrderBy(v => v.Ver).ToArray();
        }

        public override Task<string> GetDownloadUrl()
        {
            // https://docs.microsoft.com/en-US/cpp/windows/latest-supported-vc-redist?view=msvc-170#visual-studio-2015-2017-2019-and-2022
            // https://docs.microsoft.com/en-us/cpp/porting/binary-compat-2015-2017?view=msvc-170
            return Task.FromResult(CpuArchitecture switch {
                RuntimeCpu.X86 => "https://aka.ms/vs/17/release/vc_redist.x86.exe",
                RuntimeCpu.X64 => "https://aka.ms/vs/17/release/vc_redist.x64.exe",
                _ => throw new ArgumentOutOfRangeException(nameof(CpuArchitecture)),
            });
        }
    }

    public static class Runtimes
    {
        public static readonly FxRuntimeInfo NETFRAMEWORK45 = new("net45", ".NET Framework 4.5", "http://go.microsoft.com/fwlink/?LinkId=397707", 378389);
        public static readonly FxRuntimeInfo NETFRAMEWORK451 = new("net451", ".NET Framework 4.5.1", "http://go.microsoft.com/fwlink/?LinkId=397707", 378675);
        public static readonly FxRuntimeInfo NETFRAMEWORK452 = new("net452", ".NET Framework 4.5.2", "http://go.microsoft.com/fwlink/?LinkId=397707", 379893);
        public static readonly FxRuntimeInfo NETFRAMEWORK46 = new("net46", ".NET Framework 4.6", "http://go.microsoft.com/fwlink/?LinkId=780596", 393295);
        public static readonly FxRuntimeInfo NETFRAMEWORK461 = new("net461", ".NET Framework 4.6.1", "http://go.microsoft.com/fwlink/?LinkId=780596", 394254);
        public static readonly FxRuntimeInfo NETFRAMEWORK462 = new("net462", ".NET Framework 4.6.2", "http://go.microsoft.com/fwlink/?LinkId=780596", 394802);
        public static readonly FxRuntimeInfo NETFRAMEWORK47 = new("net47", ".NET Framework 4.7", "http://go.microsoft.com/fwlink/?LinkId=863262", 460798);
        public static readonly FxRuntimeInfo NETFRAMEWORK471 = new("net471", ".NET Framework 4.7.1", "http://go.microsoft.com/fwlink/?LinkId=863262", 461308);
        public static readonly FxRuntimeInfo NETFRAMEWORK472 = new("net472", ".NET Framework 4.7.2", "http://go.microsoft.com/fwlink/?LinkId=863262", 461808);
        public static readonly FxRuntimeInfo NETFRAMEWORK48 = new("net48", ".NET Framework 4.8", "http://go.microsoft.com/fwlink/?LinkId=2085155", 528040);

        public static readonly DotnetRuntimeInfo DOTNETCORE31_X86 = new("netcoreapp31-x86", ".NET Core 3.1 Desktop Runtime (x86)", "3.1", RuntimeCpu.X86);
        public static readonly DotnetRuntimeInfo DOTNETCORE31_X64 = new("netcoreapp31-x64", ".NET Core 3.1 Desktop Runtime (x64)", "3.1", RuntimeCpu.X64);
        public static readonly DotnetRuntimeInfo DOTNET5_X86 = new("net5-x86", ".NET 5.0 Desktop Runtime (x86)", "5.0", RuntimeCpu.X86);
        public static readonly DotnetRuntimeInfo DOTNET5_X64 = new("net5-x64", ".NET 5.0 Desktop Runtime (x64)", "5.0", RuntimeCpu.X64);
        public static readonly DotnetRuntimeInfo DOTNET6_X86 = new("net6-x86", ".NET 6.0 Desktop Runtime (x86)", "6.0", RuntimeCpu.X86);
        public static readonly DotnetRuntimeInfo DOTNET6_X64 = new("net6-x64", ".NET 6.0 Desktop Runtime (x64)", "6.0", RuntimeCpu.X64);

        public static readonly VCredistRuntimeInfo VCREDIST140_X86 = new("vcredist140-x86", "Visual C++ 2015 Redistributable (x86)", new(14, 00, 23506), RuntimeCpu.X86);
        public static readonly VCredistRuntimeInfo VCREDIST140_X64 = new("vcredist140-x64", "Visual C++ 2015 Redistributable (x64)", new(14, 00, 23506), RuntimeCpu.X64);
        public static readonly VCredistRuntimeInfo VCREDIST141_X86 = new("vcredist141-x86", "Visual C++ 2017 Redistributable (x86)", new(14, 15, 26706), RuntimeCpu.X86);
        public static readonly VCredistRuntimeInfo VCREDIST141_X64 = new("vcredist141-x64", "Visual C++ 2017 Redistributable (x64)", new(14, 15, 26706), RuntimeCpu.X64);
        public static readonly VCredistRuntimeInfo VCREDIST142_X86 = new("vcredist142-x86", "Visual C++ 2019 Redistributable (x86)", new(14, 20, 27508), RuntimeCpu.X86);
        public static readonly VCredistRuntimeInfo VCREDIST142_X64 = new("vcredist142-x64", "Visual C++ 2019 Redistributable (x64)", new(14, 20, 27508), RuntimeCpu.X64);
        public static readonly VCredistRuntimeInfo VCREDIST143_X86 = new("vcredist143-x86", "Visual C++ 2022 Redistributable (x86)", new(14, 30, 30704), RuntimeCpu.X86);
        public static readonly VCredistRuntimeInfo VCREDIST143_X64 = new("vcredist143-x64", "Visual C++ 2022 Redistributable (x64)", new(14, 30, 30704), RuntimeCpu.X64);

        public static readonly RuntimeInfo[] All;

        static Runtimes()
        {
            All = typeof(Runtimes)
                .GetFields()
                .Where(f => typeof(RuntimeInfo).IsAssignableFrom(f.FieldType))
                .Select(f => (RuntimeInfo) f.GetValue(null))
                .ToArray();
        }

        public static RuntimeInfo GetRuntimeByName(string name)
        {
            // default to x64 if not specified
            return All.FirstOrDefault(r => r.Id.Equals(name, StringComparison.InvariantCulture))
                ?? All.FirstOrDefault(r => r.Id.Equals(name + "-x64", StringComparison.InvariantCulture));
        }
    }
}